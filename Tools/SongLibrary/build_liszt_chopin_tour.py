"""Publish the Liszt/Chopin two-piano tour: cues are written in seconds on the comparison's own clock.

    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/build_liszt_chopin_tour.py
    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/narration.py PreparedSongs/Library/<bundle>
"""
import argparse
import json
import shutil
from pathlib import Path

from common import ROOT, atomic_json
from publish_story import publish


def build(bundle=None):
    source = Path(__file__).parent / 'directors/liszt-chopin.json'
    director = json.loads(source.read_text(encoding='utf-8'))
    library = ROOT / 'PreparedSongs/Library'
    bundle = Path(bundle).resolve() if bundle else max(library.glob(director['bundle']), key=lambda p: p.stat().st_mtime)
    assets = ROOT / director['assets']
    draft = {k: v for k, v in director.items() if k not in ('bundle', 'assets', 'portraits', 'arrows')}
    # Each pianist's portrait, shown beside their torus (ConcertoTori): portraits/<name>.jpg in the bundle.
    portraits = bundle / 'portraits'
    portraits.mkdir(exist_ok=True)
    for name, file in director.get('portraits', {}).items():
        shutil.copyfile((assets / file).resolve(), portraits / f'{name.lower()}.jpg')
    for picture in draft['pictures'].values():
        picture['file'] = str((assets / picture['file']).resolve())
    for cue in draft['cues']:
        cue.setdefault('stem', '')
        cue.setdefault('uncoil', False)
        cue.setdefault('melodyTarget', 'both')
    atomic_json(bundle / 'story.draft.json', draft)
    result = publish(bundle)
    stamp = lambda s: f'{int(s) // 60}:{s % 60:04.1f}'
    lines = [draft['title'], 'JohnV4: grounded delivery, downward sentence endings.', f'Bundle: {bundle.name}', '']
    for cue in draft['cues']:
        lines += [f"{stamp(cue['start'])}-{stamp(cue['end'])} | {cue.get('concertoFocus')}", cue.get('speech') or '[Music alone] ' + cue['text'], '']
    lines += ['Sources'] + [s['title'] + '\n' + s['url'] for s in draft['sources']]
    report = ROOT / 'Reports/SongProcessing' / bundle.name
    report.mkdir(parents=True, exist_ok=True)
    (report / 'tour-script.txt').write_text('\n'.join(lines), encoding='utf-8')
    record = bundle / 'library.json'
    if record.exists():
        data = json.loads(record.read_text(encoding='utf-8'))
        data['directorScript'] = True
        atomic_json(record, data)
    return dict(result, bundle=str(bundle))


def write_arrows(bundle=None):
    """arrows.json beside the story: each arrow of the director file, timed to the spoken word it points out (the caption
    words' times from narration.json, so this runs after narration.py), with a few seconds on screen."""
    import re
    source = Path(__file__).parent / 'directors/liszt-chopin.json'
    director = json.loads(source.read_text(encoding='utf-8'))
    library = ROOT / 'PreparedSongs/Library'
    bundle = Path(bundle).resolve() if bundle else max(library.glob(director['bundle']), key=lambda p: p.stat().st_mtime)
    segments = {s['cue']: s for s in json.loads((bundle / 'narration.json').read_text(encoding='utf-8'))['segments']}
    norm = lambda w: re.sub(r"[^\w']", '', w).lower()
    out = []
    for a in director['arrows']:
        cue = director['cues'][a['cue']]
        words = [norm(w) for w in (cue.get('speech') or cue['text']).split()]
        hits = [i for i, w in enumerate(words) if w == a['word']]
        if len(hits) <= a['nth']:
            raise ValueError(f"Arrow '{a['label']}': word '{a['word']}' #{a['nth']} not in cue {a['cue']}")
        seg = segments[a['cue']]
        at = seg['start'] + seg['wordStarts'][hits[a['nth']]] - .1
        out.append(dict(at=round(at, 2), until=round(at + a['dur'], 2), torus=a['torus'], target=a['target'], label=a['label'],
                        pitchClass=a.get('pitchClass', 0), midiPitch=a.get('midiPitch', 0)))
    out.sort(key=lambda a: a['at'])
    atomic_json(bundle / 'arrows.json', dict(arrows=out))
    return out


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('--bundle')
    p.add_argument('--arrows', action='store_true', help='write arrows.json from the narration timing (after narration.py)')
    a = p.parse_args()
    print(json.dumps(write_arrows(a.bundle) if a.arrows else build(a.bundle), indent=2, ensure_ascii=False))
