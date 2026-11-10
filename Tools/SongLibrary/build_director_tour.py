"""Publish a hand-directed tour whose cues are written in printed bars.

For bundles ingested from a MuseScore score (library.py ingest --score). The director file lives in
Tools/SongLibrary/directors/ and is committed; the bundle (and so story.draft.json, story.json and the narration)
is not. A cue says where it starts and ends as bar specs ("from": "17", "to": "21", "46:2" for a beat,
"9#2" for the second time a bar is played, "end"), or in seconds ("start"/"end"); soloUntil, narrationStart and
narrationEnd may be bar specs too. score_bars.BarClock turns them into recording seconds through the aligned tempo
map, so the cues follow the recording wherever the performer took time. A cue without an end runs to the next
cue's start.

    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/build_director_tour.py Tools/SongLibrary/directors/chopin-op10-3.json
    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/narration.py PreparedSongs/Library/<bundle>

The director names its bundle with a glob ("bundle"); the newest score-ingested match is used unless --bundle
is given. A tour script with every cue's time and words is written to Reports/SongProcessing/<bundle>/.
"""
import argparse
import json
from pathlib import Path

from common import ROOT, atomic_json
from publish_story import publish
from score_bars import BarClock

TIMED = ('soloUntil', 'narrationStart', 'narrationEnd')


def find_bundle(pattern):
    library = ROOT/'PreparedSongs/Library'
    found = [p for p in library.glob(pattern) if (p/'score-import.json').exists() and (p/'aligned.mid.patterns.json').exists()]
    if not found:
        raise ValueError(f'No score-ingested bundle matches {pattern!r} in {library}')
    return max(found, key=lambda p: (p/'aligned.mid.patterns.json').stat().st_mtime)


def timed_cues(director, clock):
    """The director's cues with every bar spec turned into recording seconds."""
    cues = []
    for i, source in enumerate(director['cues']):
        cue = dict(source)
        if 'from' in cue:
            cue['start'] = clock.seconds(cue.pop('from'))
        if 'to' in cue:
            cue['end'] = clock.seconds(cue.pop('to'))
        for key in TIMED:
            if isinstance(cue.get(key), str):
                cue[key] = clock.seconds(cue[key])
        # Two passages side by side (Unity draws a wheel for each): what the second leaves out of the first is
        # highlighted on the first, and the place it is missing from is marked on the second.
        compare = cue.pop('compare', None)
        if compare:
            span = lambda pair: [round(clock.seconds(pair[0]), 3), round(clock.seconds(pair[1]), 3)]
            cue['compareFirst'], cue['compareSecond'] = span(compare['first']), span(compare['second'])
            if compare.get('highlight'):
                cue['compareHighlight'] = span(compare['highlight'])
            if compare.get('added'):
                cue['compareAdded'] = span(compare['added'])
            if compare.get('missingAt'):
                cue['compareMissingAt'] = round(clock.seconds(compare['missingAt']), 3)
            if compare.get('labels'):
                cue['compareLabels'] = [str(x) for x in compare['labels']]
        if 'start' not in cue:
            raise ValueError(f'Cue {i} needs "from" or "start"')
        cues.append(cue)
    for cue, following in zip(cues, cues[1:]):
        cue.setdefault('end', following['start'])
    cues[-1].setdefault('end', clock.duration)
    for cue in cues:
        for key in ('start', 'end', *TIMED):
            if isinstance(cue.get(key), float):
                cue[key] = round(cue[key], 3)
    return cues


def script(draft, bundle):
    stamp = lambda s: f'{int(s) // 60}:{s % 60:04.1f}'
    narrator = draft.get('narrator', {})
    lines = [draft['title'], f"{narrator.get('name', '')}: {narrator.get('delivery', '')}", f'Bundle: {bundle.name}', '']
    spoken = 0
    for cue in draft['cues']:
        speech = cue.get('speech') or cue['text']
        talk = cue.get('narrate', True)
        spoken += len(speech.split()) if talk else 0
        extra = ''.join(f' | {k} {cue[k]}' for k in ('stem', 'annotationTarget') if cue.get(k) and cue.get(k) != 'none')
        lines += [f"{stamp(cue['start'])}–{stamp(cue['end'])} | {cue.get('view', 'Overview')}{' | uncoiled' if cue.get('uncoil') else ''}{extra}",
                  speech if talk else '[Music alone] ' + cue['text'], '']
    lines += [f'{spoken} spoken words', '', 'Sources'] + [s['title'] + '\n' + s['url'] for s in draft.get('sources', [])]
    return '\n'.join(lines)


def build(director_path, bundle=None):
    director_path = Path(director_path).resolve()
    director = json.loads(director_path.read_text(encoding='utf-8'))
    bundle = Path(bundle).resolve() if bundle else find_bundle(director['bundle'])
    draft = {k: v for k, v in director.items() if k not in ('cues', 'bundle', 'assets')}
    # Picture files named relative to the director's assets folder (e.g. SongLibraryData/scores/<song>), which
    # holds the downloaded originals; publish copies them into the bundle at card size.
    assets = (ROOT/director['assets']) if director.get('assets') else director_path.parent
    for picture in draft.get('pictures', {}).values():
        if not Path(picture['file']).is_absolute():
            picture['file'] = str((assets/picture['file']).resolve())
    draft['cues'] = timed_cues(director, BarClock(bundle))
    atomic_json(bundle/'story.draft.json', draft)
    result = publish(bundle)
    report = ROOT/'Reports/SongProcessing'/bundle.name
    report.mkdir(parents=True, exist_ok=True)
    (report/'tour-script.txt').write_text(script(draft, bundle), encoding='utf-8')
    library = bundle/'library.json'
    if library.exists():
        record = json.loads(library.read_text(encoding='utf-8'))
        record['directorScript'] = True
        atomic_json(library, record)
    return dict(result, bundle=str(bundle), script=str(report/'tour-script.txt'))


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    p.add_argument('director')
    p.add_argument('--bundle')
    a = p.parse_args()
    print(json.dumps(build(a.director, a.bundle), indent=2, ensure_ascii=False))
