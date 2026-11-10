"""Timed accents from a MuseScore score, matched to the companion MIDI's notes (score-accents.json).

Resonance reads the score itself (musescore_import.py); nothing outside the project is needed. Every chord
marked with an accent, a marcato or a sforzato (and every note under an sf / sfz / fz dynamic) becomes a mark
on the MIDI note it belongs to: the same staff (the MIDI's pitched tracks in staff order, after dropping
'simplified' and 'orchestral' piano staves), the same pitch, and the nearest onset within 12 ticks of the score's
position (converted to the MIDI's resolution). PatternPrep's MelodySelection reads the marks.
"""
import argparse
import hashlib
import json
from fractions import Fraction
from pathlib import Path

import mido

import musescore_import

ACCENT_WORDS = ('accent', 'marcato', 'sforz')
ACCENT_DYNAMICS = {'sf', 'sfz', 'sff', 'sffz', 'fz', 'rfz', 'rf', 'sfp', 'sfpp'}


def _accented_ticks(score):
    """(staff, tick) of the chords under a one-note accent dynamic."""
    out = set()
    for d in score.directions:
        if d.kind == 'dynamic' and d.text in ACCENT_DYNAMICS:
            out.add((d.staff, d.tick))
    return out


def extract(score_path, midi_path):
    score = musescore_import.read(str(score_path))
    midi = mido.MidiFile(midi_path)
    retained = [s for s in score.staves if not any(x in s['name'].lower() for x in ('simplified', 'orchestral'))]
    pitched = [i for i, t in enumerate(midi.tracks) if any(m.type == 'note_on' and m.velocity for m in t)]
    if len(retained) != len(pitched):
        raise ValueError(f'{len(retained)} score staves but {len(pitched)} MIDI tracks with notes')
    staff_tracks = {s['index']: track for s, track in zip(retained, pitched)}
    scale = Fraction(midi.ticks_per_beat, score.division)
    onsets = {}
    for track, messages in enumerate(midi.tracks):
        tick = 0
        for msg in messages:
            tick += msg.time
            if msg.type == 'note_on' and msg.velocity:
                onsets.setdefault((track, msg.note), []).append(tick)
    under_dynamic = _accented_ticks(score)
    marks, seen, unmatched = [], set(), 0
    for note in score.notes:
        if note.grace or note.staff not in staff_tracks:
            continue
        accented = any(any(w in a.lower() for w in ACCENT_WORDS) for a in note.articulations) or (note.staff, note.tick) in under_dynamic
        if not accented:
            continue
        track = staff_tracks[note.staff]
        tick = note.tick * scale
        possible = onsets.get((track, note.pitch), [])
        if not possible:
            unmatched += 1
            continue
        closest = min(possible, key=lambda t: abs(t - tick))
        if abs(closest - tick) > 12:
            unmatched += 1
            continue
        key = (track, note.pitch, closest)
        if key in seen:
            continue
        seen.add(key)
        # Track numbers are as PatternPrep sees them: alignment writes a new conductor track first and copies
        # every companion track after it, in order.
        marks.append(dict(Track=track + 1, Pitch=note.pitch, Beat=closest / midi.ticks_per_beat))
    report = dict(reader=score.reader, museScoreVersion=score.format_version,
                  sourceScoreSha256=hashlib.sha256(Path(score_path).read_bytes()).hexdigest(),
                  matchedAccentNotes=len(marks), unmatchedAccentNotes=unmatched,
                  matching='Score staff, pitch and score tick (in MIDI ticks), within 12 ticks')
    return marks, report


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    p.add_argument('--score', required=True)
    p.add_argument('--midi', required=True)
    p.add_argument('--output', required=True)
    a = p.parse_args()
    marks, report = extract(a.score, a.midi)
    out = Path(a.output)
    out.write_text(json.dumps(marks, indent=2), encoding='utf-8')
    out.with_name('score-accents.provenance.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))
