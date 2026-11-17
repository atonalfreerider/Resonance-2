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
    draft = {k: v for k, v in director.items() if k not in ('bundle', 'assets', 'portraits')}
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


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('--bundle')
    print(json.dumps(build(p.parse_args().bundle), indent=2, ensure_ascii=False))
