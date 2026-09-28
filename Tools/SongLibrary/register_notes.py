"""Recover a quiet, fast part the stem transcription missed, from a register stem's audio, and
rebuild that stem's pattern wheels. Written for Rollout's synth arpeggiator, which sits in the
high-register accompaniment (above middle C) from the first bar but was transcribed as one note.

  Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/register_notes.py PreparedSongs/Library/<song> [--stem other-high] [--name "Synth arpeggiator"]

Each onset in the stem (librosa onset detection) becomes a note whose pitch is the strongest
constant-Q bin in the stem's register until the next onset; quiet onsets (under a fraction of the
stem's loud ones) are dropped. The notes are added to the stem's existing notes as their own
track, the stem is mapped to the master clock and compiled, the full-song harmony is copied in as
compile_stems does, and only that stem's manifest entry changes. Idempotent: the original notes
are kept beside the stem as <stem>-notes.orig.mid and reused on a rerun."""
import argparse
import copy
import shutil
from pathlib import Path
import numpy as np
from common import MASTER_FIELDS, MASTER_SECTION_FIELDS, atomic_json, compile_patterns, read_json, sha, validate_bundle
from stems import map_to_master


def detect(wav, low_midi, high_midi, floor):
    import librosa
    y, sr = librosa.load(str(wav), sr=None, mono=True)
    hop = 256
    bins = high_midi - low_midi
    cqt = np.abs(librosa.cqt(y, sr=sr, hop_length=hop, fmin=librosa.midi_to_hz(low_midi), n_bins=bins, bins_per_octave=12))
    onsets = librosa.onset.onset_detect(y=y, sr=sr, hop_length=hop, backtrack=True, units='frames', delta=.015, wait=1)
    if len(onsets) == 0:
        return []
    frames = cqt.shape[1]
    strength = [cqt[:, a:min(frames, b if b > a else a + 1)].mean(axis=1) for a, b in zip(onsets, list(onsets[1:]) + [frames])]
    loud = np.percentile([s.max() for s in strength], 90)
    notes = []
    for i, (a, s) in enumerate(zip(onsets, strength)):
        if s.max() < floor * loud:
            continue
        pitch = low_midi + int(np.argmax(s))
        start = a * hop / sr
        end = (onsets[i + 1] * hop / sr) if i + 1 < len(onsets) else start + .15
        end = min(end, start + .4)
        velocity = int(np.clip(40 + 87 * s.max() / loud, 30, 127))
        notes.append((start, max(start + .03, end - .005), pitch, velocity))
    return notes


def rebuild(bundle, stem='other-high', name='Synth arpeggiator', low=60, high=100, floor=.07):
    import pretty_midi
    bundle = Path(bundle).resolve(); validate_bundle(bundle)
    folder = bundle / 'stems'; notes_mid = folder / f'{stem}-notes.mid'; original = folder / f'{stem}-notes.orig.mid'
    if not original.exists():
        shutil.copy(notes_mid, original)
    found = detect(folder / f'{stem}.wav', low, high, floor)
    score = pretty_midi.PrettyMIDI(str(original))
    track = pretty_midi.Instrument(program=81, name=name)
    track.notes = [pretty_midi.Note(velocity=v, pitch=p, start=s, end=e) for s, e, p, v in found]
    score.instruments.append(track); score.write(str(notes_mid))
    midi = folder / f'{stem}.mid'
    map_to_master(notes_mid, midi, bundle / 'aligned.mid')
    compile_patterns(midi, bundle.parent / 'stems.log')
    path = Path(str(midi) + '.patterns.json'); patterns = read_json(path); master = read_json(bundle / 'aligned.mid.patterns.json')
    for field in MASTER_FIELDS:
        if field in master: patterns[field] = copy.deepcopy(master[field])
    for section in patterns['Sections']:
        reference = next((s for s in master['Sections'] if s['Start'] == section['Start']), None)
        if reference:
            for field in MASTER_SECTION_FIELDS:
                if field in reference: section[field] = copy.deepcopy(reference[field])
    atomic_json(path, patterns)
    manifest = read_json(bundle / 'aligned.mid.prepared.json')
    for entry in manifest['stems']:
        if entry['id'] == stem:
            entry.update(midiSha256=sha(midi), patternsSha256=sha(path), notes=len(patterns['Notes']),
                         method=entry.get('method', '').split('+')[0] + '+recovered-register-notes')
    atomic_json(bundle / 'aligned.mid.prepared.json', manifest)
    validate_bundle(bundle)
    return dict(stem=stem, recovered=len(found), first=[round(n[0], 2) for n in found[:8]], pitches=sorted({n[2] for n in found})[:12], notes=len(patterns['Notes']))


if __name__ == '__main__':
    import json
    p = argparse.ArgumentParser(); p.add_argument('bundle'); p.add_argument('--stem', default='other-high'); p.add_argument('--name', default='Synth arpeggiator')
    p.add_argument('--floor', type=float, default=.07); a = p.parse_args()
    print(json.dumps(rebuild(a.bundle, a.stem, a.name, floor=a.floor), indent=2))
