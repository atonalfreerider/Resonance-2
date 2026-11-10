"""Tests for musescore_import (MuseScore 1.x/2.x reader, repairs, MIDI and timing output).

    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongPrep/test_musescore_import.py

Scores are generated here (no copyrighted score is needed).
"""
import json
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile
from fractions import Fraction
from pathlib import Path

import mido

import musescore_import as msi


def chord(duration, *pitches, tick=None, track=None, dots=0, tuplet=None, grace=False, tie=False, velocity=None, artic=None):
    parts = ['<Chord>']
    if tick is not None:
        parts.append(f'<tick>{tick}</tick>')
    if track is not None:
        parts.append(f'<track>{track}</track>')
    if grace:
        parts.append('<acciaccatura/>')
    if dots:
        parts.append(f'<dots>{dots}</dots>')
    parts.append(f'<durationType>{duration}</durationType>')
    if tuplet is not None:
        parts.append(f'<Tuplet>{tuplet}</Tuplet>')
    if artic:
        parts.append(f'<Articulation><subtype>{artic}</subtype></Articulation>')
    for p in pitches:
        note = f'<Note><pitch>{p}</pitch><tpc>14</tpc>'
        if tie:
            note += '<Tie id="1"/>'
        if velocity is not None:
            note += f'<veloType>user</veloType><velocity>{velocity}</velocity>'
        parts.append(note + '</Note>')
    parts.append('</Chord>')
    return ''.join(parts)


def rest(duration, tick=None, track=None):
    t = f'<tick>{tick}</tick>' if tick is not None else ''
    tr = f'<track>{track}</track>' if track is not None else ''
    return f'<Rest>{t}{tr}<durationType>{duration}</durationType></Rest>'


def tuplet(tid, normal, actual, base='eighth'):
    return f'<Tuplet id="{tid}"><normalNotes>{normal}</normalNotes><actualNotes>{actual}</actualNotes><baseNote>{base}</baseNote></Tuplet>'


def score_114(staff_measures, siglist=((0, 2, 4, 2, 4),), tempos=((0, 1.0),), extra='', pickup_first=False):
    """A MuseScore 1.14 score: one Part with len(staff_measures) staves (2 = a piano's grand staff)."""
    sig = ''.join(f'<sig tick="{t}"><nom>{n}</nom><denom>{d}</denom><nom2>{n2}</nom2><denom2>{d2}</denom2></sig>' for t, n, d, n2, d2 in siglist)
    tempo = ''.join(f'<tempo tick="{t}">{q}</tempo>' for t, q in tempos)
    staves = ''.join('<Staff/>' for _ in staff_measures)
    music = ''
    for i, measures in enumerate(staff_measures):
        body = ''
        for k, m in enumerate(measures):
            irregular = '<irregular/>' if (pickup_first and k == 0) else ''
            body += f'<Measure number="{k + 1}">{irregular}{m}</Measure>'
        music += f'<Staff id="{i + 1}">{body}</Staff>'
    xml = (f'<?xml version="1.0" encoding="UTF-8"?><museScore version="1.14"><programVersion>1.3</programVersion>'
           f'<Division>480</Division><siglist>{sig}</siglist><tempolist fix="2">{tempo}</tempolist>'
           f'<Part>{staves}<Instrument><trackName>Piano</trackName><Channel><program value="0"/></Channel></Instrument></Part>'
           f'{music}{extra}</museScore>')
    return xml.encode('utf-8')


def write_mscz(folder, xml: bytes, name='test.mscz'):
    path = Path(folder) / name
    with zipfile.ZipFile(path, 'w') as z:
        z.writestr('META-INF/container.xml', '<container><rootfiles><rootfile full-path="temp.mscx"/></rootfiles></container>')
        z.writestr('temp.mscx', xml)
    return path


class LegacyReading(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def read(self, xml):
        return msi.read(str(write_mscz(self.dir, xml)))

    def test_pickup_numbering_ties_voices_and_seconds(self):
        right = [chord('eighth', 59),
                 chord('quarter', 64, tie=True) + chord('16th', 64) + chord('16th', 66) + chord('eighth', 68)
                 + chord('quarter', 55, tick=240, track=1) + chord('quarter', 57, track=1),
                 chord('half', 67)]
        left = [rest('eighth'), chord('half', 40), chord('half', 47)]
        s = self.read(score_114([right, left], siglist=((0, 1, 8, 2, 4), (240, 2, 4, 2, 4)), tempos=((0, 1.0),), pickup_first=True))
        self.assertEqual([m.number for m in s.measures], [0, 1, 2])
        self.assertEqual([m.ticks for m in s.measures], [240, 960, 960])
        self.assertEqual(s.repairs, [])
        tied = [n for n in s.notes if n.pitch == 64]
        self.assertEqual(len(tied), 1)
        self.assertEqual(tied[0].ticks, 480 + 120)            # quarter tied into a 16th
        inner = [n for n in s.notes if n.voice == 1]
        self.assertEqual([(n.pitch, n.tick) for n in inner], [(55, 240), (57, 720)])
        report = msi.timing(s)
        g = next(n for n in report['notes'] if n['pitch'] == 67)
        self.assertEqual(g['measure'], 2)
        self.assertAlmostEqual(g['start'], (240 + 960) / 480, places=6)   # 1 quarter per second
        self.assertEqual(g['beat'], 1.0)

    def test_tuplets_grace_notes_and_articulations(self):
        right = [tuplet(0, 2, 3) + chord('eighth', 60, tuplet=0) + chord('eighth', 62, tuplet=0) + chord('eighth', 64, tuplet=0)
                 + chord('32nd', 71, grace=True) + chord('quarter', 72, artic='staccato')]
        s = self.read(score_114([right]))
        main = [n for n in s.notes if not n.grace]
        self.assertEqual([n.tick for n in main], [0, 160, 320, 480])
        grace = [n for n in s.notes if n.grace]
        self.assertEqual(len(grace), 1)
        self.assertLess(grace[0].tick, 480)
        stacc = next(n for n in main if n.pitch == 72)
        self.assertEqual(stacc.play_ticks, 240)
        self.assertEqual(s.repairs, [])

    def test_voice_longer_than_measure_is_scaled_and_reported(self):
        # three quarters written in a 2/4 bar: an unmarked triplet
        right = [chord('quarter', 60) + chord('quarter', 62) + chord('quarter', 64), chord('half', 65)]
        s = self.read(score_114([right]))
        self.assertEqual(len(s.repairs), 1)
        r = s.repairs[0]
        self.assertEqual((r.measure, r.staff, r.voice), (1, 1, 1))
        self.assertEqual(r.found, '3/4')
        bar1 = sorted((n.tick, n.ticks) for n in s.notes if n.measure == 0)
        self.assertEqual(bar1, [(0, 320), (320, 320), (640, 320)])
        self.assertEqual(next(n for n in s.notes if n.pitch == 65).tick, 960)

    def test_tuplet_declared_wrongly_is_fitted(self):
        # a 3:2 tuplet whose notes add up to four eighths: the voice overruns, the tuplet is fitted
        right = [tuplet(0, 2, 3) + chord('quarter', 60, tuplet=0) + chord('quarter', 62, tuplet=0) + chord('quarter', 64, tuplet=0)
                 + chord('quarter', 65, tuplet=0) + chord('eighth', 67)]
        s = self.read(score_114([right]))
        self.assertTrue(any(r.kind == 'tuplet does not fit the measure' for r in s.repairs))
        end = max(n.tick + n.ticks for n in s.notes)
        self.assertEqual(end, 960)

    def test_last_note_over_bar_line_and_incomplete_voice(self):
        right = [chord('quarter', 60) + chord('half', 62), chord('quarter', 64)]
        s = self.read(score_114([right]))
        kinds = [(r.measure, r.kind) for r in s.repairs]
        self.assertIn((1, 'last note crosses the bar line'), kinds)
        self.assertIn((2, 'incomplete voice 1'), kinds)
        self.assertEqual(next(n for n in s.notes if n.pitch == 62).ticks, 480)

    def test_element_outside_measure_moved(self):
        right = [chord('quarter', 60) + chord('quarter', 62, tick=5000), chord('half', 64)]
        s = self.read(score_114([right]))
        self.assertTrue(any(r.kind == 'element outside its measure' for r in s.repairs))
        self.assertEqual(next(n for n in s.notes if n.pitch == 62).tick, 480)

    def test_midi_tracks_tempo_pedal_and_velocity(self):
        right = [chord('half', 72, velocity=90), chord('half', 74)]
        left = [chord('half', 48), chord('half', 50)]
        pedal = '<Pedal><track>4</track><tick>0</tick><tick2>960</tick2></Pedal>'
        dyn = '<Dynamic><subtype>f</subtype></Dynamic>'
        left[1] = dyn + left[1]
        s = self.read(score_114([right, left], tempos=((0, 1.0), (960, 2.0)), extra=pedal))
        out = self.dir / 'out.mid'
        msi.write_midi(s, str(out))
        mid = mido.MidiFile(str(out))
        names = [[m.name for m in t if m.type == 'track_name'][0] for t in mid.tracks]
        self.assertEqual(names[1:], ['Right hand', 'Left hand'])
        tempos = [m.tempo for m in mid.tracks[0] if m.type == 'set_tempo']
        self.assertEqual(tempos, [1_000_000, 500_000])
        cc = [m.value for m in mid.tracks[2] if m.type == 'control_change' and m.control == 64]
        self.assertEqual(cc, [127, 0])
        vel = {n.pitch: n.velocity for n in s.notes}
        self.assertEqual(vel[72], 90)       # the note's own velocity
        self.assertEqual(vel[48], 80)       # no dynamic yet: mf
        self.assertEqual(vel[50], 96)       # f
        report = msi.timing(s)
        self.assertAlmostEqual(report['summary']['duration'], 2.0 + 1.0)   # bar 1 at 1 q/s, bar 2 at 2 q/s
        json.dumps(report)

    def test_every_tick_moves_the_reading_position_and_musescore4_misreads_are_named(self):
        # MuseScore 1.3 writes a bar's annotations first, each at its own tick; the tuplet that follows carries the
        # tick that brings the position back to the start of the bar, and its chords carry none.
        bar = ('<Tempo><tick>120</tick><tempo>0.8</tempo><text>rall.</text></Tempo>'
               '<Dynamic><tick>480</tick><subtype>pp</subtype></Dynamic>'
               + tuplet(0, 2, 3).replace('<Tuplet id="0">', '<Tuplet id="0"><tick>0</tick>')
               + chord('eighth', 60, tuplet=0) + chord('eighth', 62, tuplet=0) + chord('eighth', 64, tuplet=0)
               + chord('quarter', 65))
        path = write_mscz(self.dir, score_114([[bar, chord('half', 67)]]))
        s = msi.read(str(path))
        self.assertEqual(s.repairs, [])                                   # complete under MuseScore 1.x rules
        self.assertEqual([(n.pitch, n.tick) for n in s.notes if n.measure == 0], [(60, 0), (62, 160), (64, 320), (65, 480)])
        self.assertEqual(sorted((d.kind, d.tick) for d in s.directions), [('dynamic', 480), ('tempo', 120)])
        self.assertEqual(len(s.musescore4), 1)
        issue = s.musescore4[0]
        self.assertEqual((issue['measure'], issue['museScore4Measure'], issue['staff'], issue['chord'], issue['tick'],
                          issue['museScore4Tick']), (1, 1, 1, 0, 0, 480))
        self.assertIn('refuses', issue['problem'])
        out = self.dir / 'for-ms4.mscz'
        self.assertEqual(msi.musescore4_copy(str(path), str(out), s.musescore4), 1)
        fixed, _ = msi.read_bytes(str(out))
        original, _ = msi.read_bytes(str(path))
        self.assertEqual(fixed.decode(), original.decode().replace('<Chord>', '<Chord><tick>0</tick>', 1))
        again = msi.read(str(out))
        self.assertEqual(again.musescore4, [])
        self.assertEqual([(n.pitch, n.tick) for n in again.notes], [(n.pitch, n.tick) for n in s.notes])

    def test_ottava_lines_move_the_sounding_pitch(self):
        right = [chord('quarter', 72) + chord('quarter', 74), chord('quarter', 76) + chord('quarter', 77, tie=True), chord('half', 77)]
        left = [chord('half', 48), chord('half', 50), chord('half', 52)]
        lines = ('<Ottava><tick>480</tick><tick2>1440</tick2><beginText><text>8va</text></beginText></Ottava>'
                 '<Ottava><track>4</track><subtype>2</subtype><tick>960</tick><tick2>1920</tick2></Ottava>')
        s = self.read(score_114([right, left], extra=lines))
        rh = [(n.tick, n.pitch) for n in s.notes if n.staff == 0]
        lh = [(n.tick, n.pitch) for n in s.notes if n.staff == 1]
        self.assertEqual(rh, [(0, 72), (480, 86), (960, 88), (1440, 77)])   # tie from outside the line keeps its pitch
        self.assertEqual(lh, [(0, 48), (960, 38), (1920, 52)])               # subtype 2 = 8vb, on the second staff
        self.assertEqual(msi.ottava_shift(ET.fromstring('<Ottava><subtype>15ma</subtype></Ottava>')), 24)
        self.assertEqual(msi.ottava_shift(ET.fromstring('<Ottava><beginText><text>8va bassa</text></beginText></Ottava>')), -12)

    def test_score_midi_has_no_markers_or_text(self):
        # PatternPrep reads MIDI markers as section boundaries and text events as karaoke lyrics.
        right = [chord('half', 72), chord('half', 74)]
        s = self.read(score_114([right], extra='<StaffText><track>0</track><tick>0</tick><text>dolce</text></StaffText>'))
        out = self.dir / 'plain.mid'
        msi.write_midi(s, str(out))
        kinds = {m.type for t in mido.MidiFile(str(out)).tracks for m in t}
        self.assertNotIn('marker', kinds)
        self.assertNotIn('text', kinds)
        self.assertNotIn('lyrics', kinds)

    def test_accents_match_the_aligned_track_numbers(self):
        import mscz_accents
        right = [chord('quarter', 72, artic='sforzato') + chord('quarter', 74), chord('half', 76)]
        left = [chord('half', 48), '<Dynamic><subtype>sf</subtype></Dynamic>' + chord('half', 50)]
        path = write_mscz(self.dir, score_114([right, left]))
        s = msi.read(str(path))
        mid = self.dir / 'score.mid'
        msi.write_midi(s, str(mid), ppq=960)
        marks, report = mscz_accents.extract(path, mid)
        # MIDI track 1 = right hand, 2 = left hand; alignment inserts a conductor track, so PatternPrep sees +1.
        self.assertEqual(sorted((m['Track'], m['Pitch'], m['Beat']) for m in marks), [(2, 72, 0.0), (3, 50, 2.0)])
        self.assertEqual(report['matchedAccentNotes'], 2)
        self.assertEqual(report['unmatchedAccentNotes'], 0)

    def test_spelling(self):
        self.assertEqual(msi.spelled(61, 21), 'C#4')
        self.assertEqual(msi.spelled(61, 9), 'Db4')
        self.assertEqual(msi.spelled(60, 26), 'B#3')
        self.assertEqual(msi.spelled(64, 18), 'E4')


if __name__ == '__main__':
    unittest.main(verbosity=2)
