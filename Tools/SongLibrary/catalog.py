"""Local MIDI catalogue plus a bounded adapter to BitMidi's public search endpoint."""
import difflib
import json
import re
import sqlite3
from contextlib import contextmanager
from pathlib import Path
from urllib.parse import urljoin, urlparse

import mido
import requests

from common import DATA, ROOT, read_json, sha


def words(value):
    value = re.sub(r'([a-z])([A-Z])', r'\1 \2', value)
    value = re.sub(r'\([^)]*\)|\[[^]]*\]', ' ', value.lower())
    return ' '.join(re.findall(r'[a-z0-9]+', value))


def name_score(query, name):
    q, n = words(query), words(name)
    a, b = set(q.split()), set(n.split())
    coverage = len(a & b)/max(1, len(a))
    return .7*coverage + .3*difflib.SequenceMatcher(None, q, n).ratio()


def midi_info(path):
    midi = mido.MidiFile(path)
    if midi.type == 2 or midi.ticks_per_beat <= 0:
        raise ValueError('Unsupported MIDI clock')
    notes = [m for t in midi.tracks for m in t if m.type == 'note_on' and m.velocity > 0]
    if not notes or not any(m.channel != 9 for m in notes):
        raise ValueError('MIDI has no pitched notes')
    return dict(notes=len(notes), drums=sum(m.channel == 9 for m in notes), tracks=len(midi.tracks),
                duration=midi.length)


class Catalog:
    def __init__(self, data=DATA):
        self.data = Path(data); self.data.mkdir(parents=True, exist_ok=True)
        self.db = self.data/'library.sqlite'
        with self.connect() as db:
            db.execute('CREATE TABLE IF NOT EXISTS midi (path TEXT PRIMARY KEY, title TEXT, hash TEXT, info TEXT)')
            db.execute('CREATE TABLE IF NOT EXISTS bundles (path TEXT PRIMARY KEY, audio_hash TEXT, title TEXT)')

    @contextmanager
    def connect(self):
        db=sqlite3.connect(self.db, timeout=30)
        try:
            with db:yield db
        finally:db.close()

    def index(self, roots):
        count = 0
        with self.connect() as db:
            for root in roots:
                for path in sorted(set(Path(root).resolve().rglob('*.mid')) | set(Path(root).resolve().rglob('*.midi'))):
                    if '.staging' in path.parts:
                        continue
                    try:
                        info = midi_info(path)
                        title = path.parent.name if path.stem in ('aligned', 'restored', 'score') else path.stem
                        manifest = read_json(str(path)+'.prepared.json', {})
                        if manifest.get('sourceAudioPath'):
                            title = Path(manifest['sourceAudioPath']).stem
                        db.execute('INSERT OR REPLACE INTO midi VALUES (?,?,?,?)', (str(path), title, sha(path), json.dumps(info)))
                        if manifest.get('sourceAudioSha256'):
                            db.execute('INSERT OR REPLACE INTO bundles VALUES (?,?,?)',
                                       (str(path.parent), manifest['sourceAudioSha256'], title))
                        count += 1
                    except (ValueError, OSError, EOFError, KeyError):
                        continue
        return count

    def exact(self, audio_hash):
        with self.connect() as db:
            paths=[Path(row[0]) for row in db.execute('SELECT path FROM bundles WHERE audio_hash=?', (audio_hash,)) if Path(row[0]).exists()]
        def rank(path):
            provenance=read_json(path/'library.json',{})
            settings=read_json(path/'song.json',{})
            return (not provenance.get('reviewed',False),provenance.get('method')=='local-yourmt3',
                    'reviewed' not in settings.get('KeySource','').lower(),str(path))
        return sorted(paths,key=rank)

    def search(self, query, limit=8):
        with self.connect() as db:
            rows = db.execute('SELECT path,title,hash,info FROM midi').fetchall()
        choices = [dict(path=p, name=n, sha256=h, info=json.loads(i), provider='local', name_score=name_score(query, n))
                   for p, n, h, i in rows if Path(p).is_file()]
        choices.sort(key=lambda c: c['name_score'], reverse=True)
        return [c for c in choices if c['name_score'] >= .3][:limit]


def bitmidi_search(query, limit=5):
    response = requests.get('https://bitmidi.com/api/midi/search',
                            params={'q': query, 'page': 0, 'pageSize': limit}, timeout=(8, 25),
                            headers={'User-Agent': 'ResonanceSongLibrary/1.0'})
    response.raise_for_status()
    rows = response.json()['result']['results']
    result = []
    for row in rows[:limit]:
        # Published virtual attribute from BitMidi's Midi model.
        url = row.get('downloadUrl')
        if not url:
            continue
        url = urljoin('https://bitmidi.com', url)
        if urlparse(url).scheme != 'https' or urlparse(url).hostname not in ('bitmidi.com', 'www.bitmidi.com'):
            continue
        result.append(dict(name=row.get('name', row.get('slug', 'MIDI')), url=url,
                           source_url='https://bitmidi.com/'+row['slug'], provider='bitmidi',
                           name_score=name_score(query, row.get('name', ''))))
    return result


def download_candidate(candidate, cache):
    cache = Path(cache); cache.mkdir(parents=True, exist_ok=True)
    response = requests.get(candidate['url'], stream=True, timeout=(8, 30))
    response.raise_for_status()
    if urlparse(response.url).hostname not in ('bitmidi.com', 'www.bitmidi.com'):
        raise ValueError('Unexpected MIDI download redirect')
    content = bytearray()
    for block in response.iter_content(65536):
        content.extend(block)
        if len(content) > 10*1024*1024:
            raise ValueError('MIDI download exceeds 10 MB')
    if not content.startswith(b'MThd'):
        raise ValueError('Provider returned a page instead of MIDI')
    import hashlib
    path = cache/(hashlib.sha256(content).hexdigest()+'.mid')
    path.write_bytes(content); midi_info(path)
    return path
