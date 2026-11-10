"""Re-apply the master's harmony, form and tonal context to every stem of prepared bundles.

    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/inherit_stems.py                 # every library bundle
    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/inherit_stems.py PreparedSongs/Library/<song> ...

Stems are compiled on their own and then given the master's fields (common.inherit_master). Bundles prepared
before the tonal context was inherited keep a stem's own key estimate, which a solo then shows: the right hand of
Chopin's E-major etude alone reads as G-sharp minor, so soloing it turned the torus to a key the piece is never in.
This rewrites each stem's patterns with the master's key changes, tonicizations and frame keys (and the other
inherited fields), and updates the stem's hash in the bundle manifest. Nothing else changes.
"""
import argparse
import json
from pathlib import Path

from common import ROOT, atomic_json, inherit_master, read_json, sha


def frame_keys(patterns):
    return [(f.get('Key', -1), f.get('Minor', False)) for f in patterns.get('Frames', [])]


def key_runs(patterns):
    runs, last = 0, None
    for k in frame_keys(patterns):
        if k != last:
            runs += 1; last = k
    return runs


def fix(bundle):
    bundle = Path(bundle)
    manifest_path = bundle/'aligned.mid.prepared.json'
    manifest = read_json(manifest_path) or {}
    stems = [s for s in manifest.get('stems', []) if s.get('patternsPath')]
    if not stems:
        return []
    master = read_json(bundle/'aligned.mid.patterns.json')
    report = []
    for stem in stems:
        path = bundle/stem['patternsPath']
        if not path.exists():
            continue
        patterns = read_json(path)
        before = dict(runs=key_runs(patterns), changes=len(patterns.get('KeyChanges') or []))
        inherit_master(patterns, master)
        atomic_json(path, patterns)
        stem['patternsSha256'] = sha(path)
        report.append(dict(stem=stem['id'], keyRunsBefore=before['runs'], keyChangesBefore=before['changes'],
                           keyRunsAfter=key_runs(patterns), keyChangesAfter=len(patterns.get('KeyChanges') or [])))
    atomic_json(manifest_path, manifest)
    return report


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    p.add_argument('bundles', nargs='*')
    a = p.parse_args()
    bundles = [Path(b) for b in a.bundles] or sorted(d for d in (ROOT/'PreparedSongs/Library').iterdir() if (d/'aligned.mid.prepared.json').exists())
    for bundle in bundles:
        for row in fix(bundle):
            print(f"{bundle.name[:60]:60} {row['stem']:14} key runs {row['keyRunsBefore']:>3} -> {row['keyRunsAfter']:<3} key changes {row['keyChangesBefore']} -> {row['keyChangesAfter']}")
