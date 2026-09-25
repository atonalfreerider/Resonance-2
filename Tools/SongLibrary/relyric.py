"""Re-sync a prepared bundle's lyrics after editing lyrics.txt or improving the sync.

  Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/relyric.py <bundle> [--resync-audio]

Recompiles the bundle's patterns (PatternPrep reads lyrics.txt, lyrics.timing.json and the
vocal's notes), copies the master's harmony, form and lyrics into each stem's patterns as the
stem compiler does, refreshes the stem hashes in the manifest and validates the bundle. The
files it replaces are copied to SongLibraryData/backups first. --resync-audio re-runs
lyric_sync.py on the recording before compiling.
"""
import argparse
import copy
import json
import shutil
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from common import DATA, MASTER_FIELDS, MASTER_SECTION_FIELDS, atomic_json, compile_patterns, read_json, sha, validate_bundle  # noqa: E402

MASTER_FIELDS_WITH_LYRICS = tuple(dict.fromkeys(MASTER_FIELDS + ('Lyrics',)))


def relyric(bundle: Path, resync_audio: bool = False) -> dict:
    bundle = bundle.resolve()
    master_path = bundle / 'aligned.mid.patterns.json'
    manifest_path = bundle / 'aligned.mid.prepared.json'
    manifest = read_json(manifest_path)
    if not manifest or not (bundle / 'lyrics.txt').exists():
        raise FileNotFoundError('Need a prepared bundle with lyrics.txt')
    backup = DATA / 'backups' / f'{bundle.name}-{time.strftime("%Y%m%d-%H%M%S")}'
    backup.mkdir(parents=True, exist_ok=True)
    for path in [master_path, manifest_path, bundle / 'lyrics.timing.json'] + [bundle / s['patternsPath'] for s in manifest.get('stems', [])]:
        if path.exists():
            target = backup / path.relative_to(bundle)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
    if resync_audio:
        from lyric_sync import sync
        sync(bundle)
    compile_patterns(bundle / 'aligned.mid')
    master = read_json(master_path)
    for stem in manifest.get('stems', []):
        path = bundle / stem['patternsPath']
        patterns = read_json(path)
        for field in MASTER_FIELDS_WITH_LYRICS:
            if field in master:
                patterns[field] = copy.deepcopy(master[field])
        for section in patterns.get('Sections', []):
            reference = next((s for s in master['Sections'] if s['Start'] == section['Start']), None)
            if reference:
                for field in MASTER_SECTION_FIELDS:
                    if field in reference:
                        section[field] = copy.deepcopy(reference[field])
        atomic_json(path, patterns)
        stem['patternsSha256'] = sha(path)
    atomic_json(manifest_path, manifest)
    validate_bundle(bundle)
    story = read_json(bundle / 'story.json')
    lyrics = master.get('Lyrics') or {}
    report = {'bundle': str(bundle), 'backup': str(backup), 'sync': lyrics.get('Sync', ''), 'syllables': len(lyrics.get('Syllables', [])),
              'stanzas': len(lyrics.get('Stanzas', [])), 'stems': len(manifest.get('stems', [])),
              'storyNeedsRebuild': bool(story) and story.get('patternsSha256') != sha(master_path)}
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('bundle', type=Path)
    parser.add_argument('--resync-audio', action='store_true')
    args = parser.parse_args()
    print(json.dumps(relyric(args.bundle, args.resync_audio), indent=1))
