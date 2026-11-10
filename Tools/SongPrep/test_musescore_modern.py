"""Tests for musescore_modern (MuseScore 3.x / 4.x scores read by Resonance).

    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongPrep/test_musescore_modern.py

Every score here is generated in the test (no copyrighted material).  The last class is an optional check against
MuseScore 4 itself, skipped when MuseScore is not installed: it exports MIDI from copies of the scores in the
user's MuseScore 4 folder (read-only; never written) and from the generated scores, and compares the notes
(``RESONANCE_MUSESCORE_CORPUS=all`` checks every score in that folder, otherwise five named ones).
"""
import bisect
import json
import os
import shutil
import subprocess
import tempfile
import unittest
import zipfile
from collections import defaultdict
from fractions import Fraction
from pathlib import Path

import mido

import musescore_import as msi

TPC = [14, 21, 16, 11, 18, 13, 20, 15, 10, 17, 12, 19]     # a spelling for every pitch class (C, C#, D, Eb...)


# ------------------------------------------------------------------------------------------- score builder

def loc(fractions=None, measures=None, grace=None, notes=None, voices=None):
    parts = []
    if voices is not None:
        parts.append(f'<voices>{voices}</voices>')
    if measures is not None:
        parts.append(f'<measures>{measures}</measures>')
    if fractions is not None:
        parts.append(f'<fractions>{fractions}</fractions>')
    if grace is not None:
        parts.append(f'<grace>{grace}</grace>')
    if notes is not None:
        parts.append(f'<notes>{notes}</notes>')
    return f'<location>{"".join(parts)}</location>'


def move(fractions):
    """A cursor move inside a voice."""
    return loc(fractions)


def tie_to(fractions=None, measures=None, grace=None):
    return f'<Spanner type="Tie"><Tie/><next>{loc(fractions, measures, grace)}</next></Spanner>'


def tie_from(fractions=None, measures=None, grace=None):
    return f'<Spanner type="Tie"><prev>{loc(fractions, measures, grace)}</prev></Spanner>'


def note(pitch, inside=''):
    return f'<Note>{inside}<pitch>{pitch}</pitch><tpc>{TPC[pitch % 12]}</tpc></Note>'


def chord(duration, *pitches, dots=0, grace=None, artic=(), ties=None, inside='', note_inside=None):
    """A chord; ``ties``: {pitch: spanner xml} added inside that note; ``note_inside``: {pitch: xml}."""
    out = ['<Chord>']
    if grace:
        out.append(f'<{grace}/>')
    if dots:
        out.append(f'<dots>{dots}</dots>')
    out.append(f'<durationType>{duration}</durationType>')
    for a in artic:
        out.append(f'<Articulation><subtype>{a}</subtype></Articulation>')
    out.append(inside)
    for p in pitches:
        extra = (ties or {}).get(p, '') + (note_inside or {}).get(p, '')
        out.append(note(p, extra))
    out.append('</Chord>')
    return ''.join(out)


def rest(duration='measure', length=None):
    d = f'<duration>{length}</duration>' if length else ''
    return f'<Rest><durationType>{duration}</durationType>{d}</Rest>'


def tuplet(normal, actual, base='eighth'):
    return (f'<Tuplet><normalNotes>{normal}</normalNotes><actualNotes>{actual}</actualNotes>'
            f'<baseNote>{base}</baseNote></Tuplet>')


END_TUPLET = '<endTuplet/>'


def spanner(kind, props='', fractions=None, measures=None):
    return f'<Spanner type="{kind}"><{kind}>{props}</{kind}><next>{loc(fractions, measures)}</next></Spanner>'


def spanner_end(kind, fractions=None, measures=None):
    return f'<Spanner type="{kind}"><prev>{loc(fractions, measures)}</prev></Spanner>'


def timesig(n, d):
    return f'<TimeSig><sigN>{n}</sigN><sigD>{d}</sigD></TimeSig>'


def tempo(qps, text=None):
    return f'<Tempo><tempo>{qps}</tempo><followText>1</followText><text>{text or f"= {round(qps * 60)}"}</text></Tempo>'


def dynamic(sub):
    return f'<Dynamic><subtype>{sub}</subtype></Dynamic>'


def measure(*voices, len_=None, pre=''):
    attr = f' len="{len_}"' if len_ else ''
    return f'<Measure{attr}>{pre}' + ''.join(f'<voice>{v}</voice>' for v in voices) + '</Measure>'


def document(staves, version='4.50', parts=None, title='Test score', composer='Nobody', style=''):
    """A .mscx text: ``staves`` is one list of measure strings per staff; ``parts`` a list of dicts (name, staves,
    program, drumset, linked: staff ids linked to an earlier one); default: one piano part holding every staff."""
    three = version.startswith('3')
    parts = parts or [dict(name='Piano', staves=len(staves), program=0)]
    part_xml = []
    sid = 0
    for pi, part in enumerate(parts):
        st = []
        for k in range(part['staves']):
            sid += 1
            group = 'percussion' if part.get('drumset') else ('tablature' if part.get('tab') else 'pitched')
            linked = f'<linkedTo>{part["linked"][k]}</linkedTo>' if part.get('linked') and part['linked'][k] else ''
            ident = f' id="{sid}"' if three or version < '4.60' else ''
            st.append(f'<Staff{ident}><StaffType group="{group}"><name>std</name></StaffType>{linked}</Staff>')
        drum = '<useDrumset>1</useDrumset>' if part.get('drumset') else ''
        part_xml.append(f'<Part id="{pi + 1}">{"".join(st)}<trackName>{part["name"]}</trackName>'
                        f'<Instrument id="{part["name"].lower()}"><longName>{part["name"]}</longName>'
                        f'<trackName>{part["name"]}</trackName>{drum}{part.get("instrument", "")}'
                        f'<Channel><program value="{part.get("program", 0)}"/></Channel></Instrument></Part>')
    text_style = 'Title' if three else 'title'
    composer_style = 'Composer' if three else 'composer'
    box = (f'<VBox><height>10</height><Text><style>{text_style}</style><text>{title}</text></Text>'
           f'<Text><style>{composer_style}</style><text>{composer}</text></Text></VBox>')
    content = ''.join(f'<Staff id="{k + 1}">{box if k == 0 else ""}{"".join(ms)}</Staff>' for k, ms in enumerate(staves))
    program = '3.6.2' if three else '4.5.2'
    inline_style = f'<Style>{style}</Style>' if style else ''
    return (f'<?xml version="1.0" encoding="UTF-8"?>\n<museScore version="{version}"><programVersion>{program}'
            f'</programVersion><Score><Division>480</Division>{inline_style}<metaTag name="workTitle">{title}</metaTag>'
            f'{"".join(part_xml)}{content}</Score></museScore>').encode('utf-8')


def write_mscz(folder, xml: bytes, name='test.mscz') -> Path:
    path = Path(folder) / name
    with zipfile.ZipFile(path, 'w') as z:
        z.writestr('META-INF/container.xml', f'<container><rootfiles><rootfile full-path="{Path(name).stem}.mscx"/>'
                                             f'</rootfiles></container>')
        z.writestr(f'{Path(name).stem}.mscx', xml)
    return path


def notes_of(score, staff=None):
    return [(int(n.tick), n.pitch) for n in score.notes if staff is None or n.staff == staff]


class Modern(unittest.TestCase):
    version = '4.50'

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def read(self, staves, **kw):
        xml = document(staves, version=self.version, **kw)
        self.path = write_mscz(self.dir, xml)
        return msi.read(str(self.path))

    # --------------------------------------------------------------------------------------- voices
    def test_two_voices_and_location_moves(self):
        m1 = measure(timesig(4, 4) + tempo(2.0) + chord('half', 72) + move('-1/4') + '<StaffText><text>dolce</text></StaffText>'
                     + move('1/4') + chord('half', 74),
                     move('1/4') + chord('quarter', 64) + chord('quarter', 65))
        s = self.read([[m1, measure(chord('whole', 60))]])
        self.assertEqual(s.reader, 'resonance.modern')
        self.assertEqual(sorted((int(n.tick), n.pitch, n.voice) for n in s.notes),
                         [(0, 72, 0), (480, 64, 1), (960, 65, 1), (960, 74, 0), (1920, 60, 0)])
        text = [d for d in s.directions if d.kind == 'text']
        self.assertEqual([(int(d.tick), d.text) for d in text], [(480, 'dolce')])
        self.assertEqual(s.repairs, [])
        self.assertEqual((s.title, s.composer), ('Test score', 'Nobody'))

    def test_nested_tuplets(self):
        m1 = measure(timesig(4, 4) + tuplet(2, 3, 'eighth') + chord('eighth', 60) + tuplet(2, 3, '16th')
                     + chord('16th', 62) + chord('16th', 64) + chord('16th', 65) + END_TUPLET + chord('eighth', 67)
                     + END_TUPLET + chord('quarter', 69) + chord('half', 71))
        s = self.read([[m1]])
        ticks = [n.tick for n in s.notes]
        self.assertEqual(ticks, [0, 160, Fraction(640, 3), Fraction(800, 3), 320, 480, 960])
        self.assertEqual(s.notes[1].ticks, Fraction(160, 3))       # a 16th in a triplet inside a triplet
        self.assertEqual(s.repairs, [])

    # --------------------------------------------------------------------------------------- ties
    def test_ties_across_bar_lines_make_one_note(self):
        staves = [[measure(timesig(4, 4) + chord('half', 60) + chord('half', 60, ties={60: tie_to('-1/2', 1)})),
                   measure(chord('whole', 60, ties={60: tie_from('1/2', -1) + tie_to(None, 1)})),
                   measure(chord('half', 60, ties={60: tie_from(None, -1)}) + chord('half', 62))]]
        s = self.read(staves)
        self.assertEqual([(int(n.tick), n.pitch, int(n.ticks)) for n in s.notes],
                         [(0, 60, 960), (960, 60, 960 + 1920 + 960), (4800, 62, 960)])
        self.assertTrue(s.notes[1].tie_start)
        self.assertEqual(s.warnings, [])

    # --------------------------------------------------------------------------------------- grace notes
    def test_grace_notes(self):
        # beat 2: an acciaccatura takes its time from before the beat (and from the chord before it);
        # beat 3: an appoggiatura takes the start of its chord; bar 2: a grace note after a chord takes its end
        m1 = measure(timesig(4, 4) + chord('quarter', 71) + chord('eighth', 74, grace='acciaccatura')
                     + chord('quarter', 72) + chord('eighth', 76, grace='appoggiatura') + chord('half', 77))
        m2 = measure(chord('eighth', 62, grace='grace8after') + chord('half', 60) + rest('half'))
        s = self.read([[m1, m2]])
        got = [(int(n.tick), n.pitch, int(n.play_ticks), n.grace) for n in s.notes]
        self.assertEqual(got, [(0, 71, 240, False),           # shortened: the acciaccatura sounds in its end
                               (240, 74, 240, True),
                               (480, 72, 480, False),
                               (960, 76, 240, True),            # 1/4 of the half note (an eighth of a half)
                               (1200, 77, 720, False),          # starts after the appoggiatura
                               (1920, 60, 480, False),          # the grace note after it takes its second half
                               (2400, 62, 480, True)])

    # --------------------------------------------------------------------------------------- repeats
    def test_repeats_with_first_and_second_endings(self):
        volta1 = spanner('Volta', '<endHookType>1</endHookType><beginText>1.</beginText><endings>1</endings>', measures=1)
        volta2 = spanner('Volta', '<endHookType>0</endHookType><beginText>2.</beginText><endings>2</endings>', measures=1)
        end = spanner_end('Volta', measures=-1)
        staves = [[measure(timesig(4, 4) + chord('whole', 60)),
                   measure(chord('whole', 62), pre='<startRepeat/>'),
                   measure(volta1 + chord('whole', 64), pre='<endRepeat>2</endRepeat>'),
                   measure(end + volta2 + chord('whole', 65)),
                   measure(end + chord('whole', 67))]]
        s = self.read(staves)
        self.assertEqual([(m.number, m.source, m.play) for m in s.measures],
                         [(1, 0, 0), (2, 1, 0), (3, 2, 0), (2, 1, 1), (4, 3, 0), (5, 4, 0)])
        self.assertEqual(notes_of(s), [(0, 60), (1920, 62), (3840, 64), (5760, 62), (7680, 65), (9600, 67)])
        self.assertEqual([m.measure for m in s.notes], [0, 1, 2, 3, 4, 5])
        self.assertIn('1.', [d.text for d in s.directions if d.kind == 'volta'])

    def test_repeat_played_three_times(self):
        staves = [[measure(timesig(4, 4) + chord('whole', 60), pre='<startRepeat/>'),
                   measure(chord('whole', 62), pre='<endRepeat>3</endRepeat>'),
                   measure(chord('whole', 64))]]
        s = self.read(staves)
        self.assertEqual([(m.number, m.play) for m in s.measures], [(1, 0), (2, 0), (1, 1), (2, 1), (1, 2), (2, 2), (3, 0)])

    def marker(self, label, kind, text):
        mt = '' if self.version.startswith('3') else f'<markerType>{kind}</markerType>'
        return f'<Marker><text>{text}</text><label>{label}</label>{mt}</Marker>'

    def test_da_capo_al_fine(self):
        jump = '<Jump><text>D.C. al Fine</text><jumpTo>start</jumpTo><playUntil>fine</playUntil><continueAt></continueAt></Jump>'
        staves = [[measure(timesig(4, 4) + chord('whole', 60)),
                   measure(chord('whole', 62), pre=self.marker('fine', 'fine', 'Fine')),
                   measure(chord('whole', 64), pre=jump)]]
        s = self.read(staves)
        self.assertEqual([(m.number, m.play) for m in s.measures], [(1, 0), (2, 0), (3, 0), (1, 1), (2, 1)])
        self.assertEqual(notes_of(s), [(0, 60), (1920, 62), (3840, 64), (5760, 60), (7680, 62)])
        self.assertIn('D.C. al Fine', [d.text for d in s.directions if d.kind == 'repeat'])

    def test_dal_segno_al_coda(self):
        jump = '<Jump><text>D.S. al Coda</text><jumpTo>segno</jumpTo><playUntil>coda</playUntil><continueAt>codab</continueAt></Jump>'
        staves = [[measure(timesig(4, 4) + chord('whole', 60)),
                   measure(chord('whole', 62), pre=self.marker('segno', 'segno', 'Segno')),
                   measure(chord('whole', 64), pre=self.marker('coda', 'coda', 'To Coda')),
                   measure(chord('whole', 65), pre=jump),
                   measure(chord('whole', 67), pre=self.marker('codab', 'codab', 'Coda'))]]
        s = self.read(staves)
        self.assertEqual([m.number for m in s.measures], [1, 2, 3, 4, 2, 3, 5])

    def test_measure_repeat(self):
        if self.version.startswith('3'):
            repeat = '<RepeatMeasure><durationType>measure</durationType><duration>4/4</duration></RepeatMeasure>'
            staves = [[measure(timesig(4, 4) + chord('half', 60) + chord('half', 62)),
                       measure(repeat, pre='<measureRepeatCount>1</measureRepeatCount>'),
                       measure(chord('whole', 64))]]
            expect = [(0, 60), (960, 62), (1920, 60), (2880, 62), (3840, 64)]
        else:
            repeat = ('<MeasureRepeat><subtype>2</subtype><durationType>measure</durationType>'
                      '<duration>4/4</duration></MeasureRepeat>')
            staves = [[measure(timesig(4, 4) + chord('half', 60) + chord('half', 62)),
                       measure(chord('whole', 64)),
                       measure('', pre='<measureRepeatCount>1</measureRepeatCount>'),
                       measure(repeat, pre='<measureRepeatCount>2</measureRepeatCount>'),
                       measure(chord('whole', 65))]]
            expect = [(0, 60), (960, 62), (1920, 64), (3840, 60), (4800, 62), (5760, 64), (7680, 65)]
        s = self.read(staves)
        self.assertEqual(notes_of(s), expect)
        self.assertEqual(s.repairs, [])

    # --------------------------------------------------------------------------------------- measures
    def test_irregular_pickup_and_measure_numbers(self):
        staves = [[measure(timesig(3, 4) + chord('quarter', 67), len_='1/4', pre='<irregular>1</irregular>'),
                   measure(tempo(1.5) + chord('half', dots=1, *[72])),
                   measure(chord('half', 71), len_='2/4', pre='<irregular>1</irregular>'),
                   measure(chord('quarter', 69), len_='1/4'),
                   measure(chord('half', dots=1, *[67]))]]
        s = self.read(staves)
        self.assertEqual([m.number for m in s.measures], [0, 1, 2, 2, 3])     # MuseScore's count
        self.assertEqual([int(m.ticks) for m in s.measures], [480, 1440, 960, 480, 1440])
        self.assertEqual([(int(t), n, d) for t, n, d in s.sigs],
                         [(0, 1, 4), (480, 3, 4), (1920, 2, 4), (2880, 1, 4), (3360, 3, 4)])
        self.assertEqual(s.tempos[0], (Fraction(0), 1.5))     # the pickup takes the first measure's tempo
        report = msi.timing(s)
        self.assertEqual(report['measures'][0]['timeSignature'], [3, 4])
        self.assertEqual(report['measures'][0]['actual'], [1, 4])
        self.assertAlmostEqual(report['notes'][1]['start'], 480 / 480 / 1.5, places=5)

    # --------------------------------------------------------------------------------------- corruption
    def test_corrupted_measures_are_repaired_not_rewritten(self):
        staves = [[measure(timesig(4, 4) + ''.join(chord('quarter', p) for p in (60, 62, 64, 65, 67))),
                   measure(chord('quarter', 60) + chord('half', 62) + chord('half', 64)),
                   measure(chord('half', 60)),
                   measure(chord('whole', 60), move('1/2') + chord('whole', 48))]]
        s = self.read(staves)
        before = self.path.read_bytes()
        kinds = [(r.measure, r.staff, r.voice, r.kind) for r in s.repairs]
        self.assertIn((1, 1, 1, 'voice longer than the measure'), kinds)
        self.assertIn((2, 1, 1, 'last note crosses the bar line'), kinds)
        self.assertIn((3, 1, 1, 'incomplete voice 1'), kinds)
        self.assertIn((4, 1, 2, 'last note crosses the bar line'), kinds)
        bar1 = [(int(n.tick), int(n.ticks)) for n in s.notes if n.measure == 0]
        self.assertEqual(bar1, [(0, 384), (384, 384), (768, 384), (1152, 384), (1536, 384)])
        self.assertEqual(next(n for n in s.notes if n.pitch == 48).ticks, 960)
        self.assertEqual(self.path.read_bytes(), before)
        msi.write_midi(s, str(self.dir / 'out.mid'))

    # --------------------------------------------------------------------------------------- playback
    def test_tempo_dynamics_pedal_and_articulations(self):
        fermata = '<Fermata><subtype>fermataAbove</subtype></Fermata>'
        pedal = spanner('Pedal', '<beginHookType>1</beginHookType>', measures=1)
        staves = [[measure(timesig(4, 4) + tempo(1.5) + dynamic('p') + pedal + chord('quarter', 60, artic=['articStaccatoAbove'])
                           + chord('quarter', 62, artic=['articAccentAbove']) + chord('half', 64)),
                   measure(spanner_end('Pedal', measures=-1) + dynamic('f') + fermata + chord('half', 65) + chord('half', 67))]]
        s = self.read(staves)
        vel = {n.pitch: n.velocity for n in s.notes}
        self.assertEqual(vel[60], 49)                     # p
        self.assertEqual(vel[62], int(49 * 1.2))          # accent
        self.assertEqual(vel[65], 96)                     # f
        self.assertEqual(next(n for n in s.notes if n.pitch == 60).play_ticks, 240)     # staccato
        self.assertIn('accent', next(n for n in s.notes if n.pitch == 62).articulations)
        self.assertEqual(s.pedals, [(0, Fraction(0), Fraction(1920))])
        # the half note under the fermata is played at half speed; the tempo comes back just before the next chord
        self.assertEqual([(int(t), round(q, 4)) for t, q in s.tempos], [(0, 1.5), (1920, 0.75), (2879, 1.5)])
        self.assertEqual([d.text for d in s.directions if d.kind == 'dynamic'], ['p', 'f'])
        self.assertIn('fermata', [d.kind for d in s.directions])

    def test_ottava_lines_shift_the_sounding_pitch(self):
        ottava = spanner('Ottava', '<subtype>8va</subtype>', measures=1)
        bassa = spanner('Ottava', '<subtype>8vb</subtype>', measures=1)
        down2 = spanner('Ottava', '<subtype>15mb</subtype>', fractions='1/2')
        staves = [[measure(timesig(4, 4) + ottava + chord('whole', 72)),
                   measure(spanner_end('Ottava', measures=-1) + bassa + chord('whole', 60)),
                   measure(spanner_end('Ottava', measures=-1) + down2 + chord('half', 48) + spanner_end('Ottava', '-1/2')
                           + chord('half', 48))]]
        s = self.read(staves)
        self.assertEqual(notes_of(s), [(0, 84), (1920, 48), (3840, 24), (4800, 48)])
        # names MuseScore shows (and older files use) for the same lines
        import musescore_modern
        shifts = {name: musescore_modern._ottava_shift(name) for name in
                  ('8va', '8vb', '15ma', '15mb', '22ma', '22mb', '8va alta', '8va bassa', '15ma bassa', '0', '1', '3')}
        self.assertEqual(shifts, {'8va': 12, '8vb': -12, '15ma': 24, '15mb': -24, '22ma': 36, '22mb': -36,
                                  '8va alta': 12, '8va bassa': -12, '15ma bassa': -24, '0': 12, '1': -12, '3': -24})

    def test_tremolos_are_played_as_repeated_notes(self):
        single = '<TremoloSingleChord><subtype>r16</subtype></TremoloSingleChord>'
        double = '<TremoloTwoChord><subtype>c8</subtype></TremoloTwoChord>'
        if self.version.startswith('3'):
            single = '<Tremolo><subtype>r16</subtype></Tremolo>'
            double = '<Tremolo><subtype>c8</subtype></Tremolo>'
        staves = [[measure(timesig(4, 4) + chord('half', 60, inside=single) + chord('half', 62)),
                   measure(chord('half', 60, inside=double) + chord('half', 64))]]
        s = self.read(staves)
        bar1 = notes_of(s)[:9]
        self.assertEqual(bar1, [(k * 120, 60) for k in range(8)] + [(960, 62)])
        bar2 = notes_of(s)[9:]
        self.assertEqual(bar2, [(1920 + k * 240, 60 if k % 2 == 0 else 64) for k in range(8)])

    def test_pause_is_time_not_ticks(self):
        breath = '<Breath><symbol>caesura</symbol><pause>1.5</pause></Breath>'
        staves = [[measure(timesig(4, 4) + tempo(2.0) + chord('whole', 60) + breath), measure(chord('whole', 62))]]
        s = self.read(staves)
        self.assertEqual([int(m.tick) for m in s.measures], [0, 1920])       # no tick was added
        report = msi.timing(s)
        self.assertAlmostEqual(report['measures'][1]['start'], 2.0 + 1.5)
        self.assertAlmostEqual(report['notes'][1]['start'], 3.5)
        self.assertEqual(report['notes'][0]['start'], 0.0)
        self.assertIn('1.5 s', [d['text'] for d in report['directions'] if d['kind'] == 'pause'])

    # --------------------------------------------------------------------------------------- parts, output
    def test_parts_linked_staves_and_midi(self):
        parts = [dict(name='Piano', staves=2, program=0),
                 dict(name='Guitar', staves=2, program=25, linked=[None, 3]),
                 dict(name='Drum Kit', staves=1, program=0, drumset=True)]
        staves = [[measure(timesig(3, 4) + tempo(2.0) + chord('half', dots=1, *[72]))],
                  [measure(chord('half', dots=1, *[48]))],
                  [measure(chord('half', dots=1, *[64]))],
                  [measure(chord('half', dots=1, *[64]))],          # the tablature linked to the guitar staff
                  [measure(chord('quarter', 36) + chord('quarter', 38) + chord('quarter', 42))]]
        s = self.read(staves, parts=parts)
        self.assertEqual([st['name'] for st in s.staves], ['Right hand', 'Left hand', 'Guitar', 'Drum Kit'])
        self.assertEqual([p['percussion'] for p in s.parts], [False, False, True])
        self.assertEqual(len(s.notes), 6)
        out = self.dir / 'out.mid'
        msi.write_midi(s, str(out))
        mid = mido.MidiFile(str(out))
        names = [[m.name for m in t if m.type == 'track_name'][0] for t in mid.tracks]
        self.assertEqual(names[1:], ['Right hand', 'Left hand', 'Guitar', 'Drum Kit'])
        drum_channels = {m.channel for m in mid.tracks[4] if m.type == 'note_on'}
        self.assertEqual(drum_channels, {9})
        sig = [(m.numerator, m.denominator) for m in mid.tracks[0] if m.type == 'time_signature']
        self.assertEqual(sig, [(3, 4)])
        self.assertEqual(sum(1 for t in mid.tracks for m in t if m.type == 'note_on' and m.velocity), 6)

    def test_timing_json_lists_the_played_measures(self):
        staves = [[measure(timesig(2, 4) + tempo(1.0) + chord('half', 60), pre='<startRepeat/>'),
                   measure(chord('half', 62), pre='<endRepeat>2</endRepeat>')]]
        s = self.read(staves)
        report = msi.timing(s)
        rows = [(m['index'], m['number'], m['source'], m['pass'], m['tick'], m['start'], m['end'])
                for m in report['measures']]
        self.assertEqual(rows, [(0, 1, 0, 0, 0.0, 0.0, 2.0), (1, 2, 1, 0, 960.0, 2.0, 4.0),
                                (2, 1, 0, 1, 1920.0, 4.0, 6.0), (3, 2, 1, 1, 2880.0, 6.0, 8.0)])
        self.assertEqual([(n['measure'], n['pass']) for n in report['notes']], [(1, 0), (2, 0), (1, 1), (2, 1)])
        json.dumps(report)


class Modern47(Modern):
    """The same scores in MuseScore 4.7's format."""
    version = '4.70'

    def test_line_break_in_title(self):
        s = self.read([[measure(timesig(4, 4) + chord('whole', 60))]], title='Two<br/>Lines')
        self.assertEqual(s.title, 'Two Lines')


class Modern3(Modern):
    """MuseScore 3.x (format 3.02): the same scores in its form (RepeatMeasure, markers by label, <Tremolo>)."""
    version = '3.02'

    def test_old_tuplet_ids_and_ties(self):
        m1 = measure(timesig(2, 4) + '<Tuplet id="1"><normalNotes>2</normalNotes><actualNotes>3</actualNotes>'
                     '<baseNote>eighth</baseNote></Tuplet>'
                     + chord('eighth', 60, inside='<Tuplet>1</Tuplet>') + chord('eighth', 62, inside='<Tuplet>1</Tuplet>')
                     + chord('eighth', 64, inside='<Tuplet>1</Tuplet>') + chord('quarter', 65, note_inside={65: '<Tie id="1"/>'}))
        m2 = measure(chord('quarter', 65) + chord('quarter', 67))
        s = self.read([[m1, m2]])
        self.assertEqual([(int(n.tick), n.pitch, int(n.ticks)) for n in s.notes],
                         [(0, 60, 160), (160, 62, 160), (320, 64, 160), (480, 65, 960), (1440, 67, 480)])


# ------------------------------------------------------------------------------------ MuseScore 4 oracle

MUSESCORE = Path(os.environ.get('RESONANCE_MUSESCORE', r'C:/Program Files/MuseScore 4/bin/MuseScore4.exe'))
CORPUS = Path(os.environ.get('RESONANCE_MUSESCORE_SCORES', Path.home() / 'Documents/MuseScore4/Scores'))
NAMED = ['You-piano-solo', 'Vocalise_No.14_from_Fourteen_Songs_Op.34__Sergei_Rachmaninoff_VocalisePiano_Solo.',
         'The_Beatles_-_Ticket_to_Ride-no-perc', 'Gershwin_Rhapsody_in_Blue_Piano_solo', 'Piano_Concerto_2_Second']


def musescore_midi(score: Path, out: Path) -> Path:
    """MuseScore 4's own MIDI export of a score (a development-time reference only)."""
    for attempt in range(3):                    # its command line sometimes crashes on exit: try again
        result = subprocess.run([str(MUSESCORE), '-o', str(out), str(score)], capture_output=True, timeout=900)
        if result.returncode == 0 and out.exists():
            break
    if result.returncode != 0 or not out.exists():
        raise RuntimeError(f'MuseScore could not export {score.name} (exit {result.returncode}; its log is the newest '
                           f'file in %LOCALAPPDATA%/MuseScore/MuseScore4/logs)')
    return out


def _harmony_channels(data: bytes) -> set:
    """MIDI channels MuseScore gives chord-symbol playback (simplified MasterScore::rebuildMidiMapping)."""
    import xml.etree.ElementTree as ET
    score = ET.fromstring(data).find('Score')
    contents = [s for s in score if s.tag == 'Staff']
    order, staff = [], 0
    for part in score.findall('Part'):
        n = max(1, len(part.findall('Staff')))
        has = any(contents[k].find('.//Harmony') is not None for k in range(staff, min(staff + n, len(contents))))
        staff += n
        ins = part.find('Instrument')
        drum = ins is not None and (ins.findtext('useDrumset') or '0').strip() not in ('0', '')
        entries = [dict(drum=drum, port=int(ch.findtext('midiPort') or -1), chan=int(ch.findtext('midiChannel') or -1),
                        harmony=ch.get('name') == 'harmony') for ch in (ins.findall('Channel') if ins is not None else [])]
        if has and not any(e['harmony'] for e in entries):
            entries.append(dict(drum=drum, port=-1, chan=-1, harmony=True))
        order.extend(entries)
    used = {e['port'] * 16 + e['chan'] for e in order if e['port'] != -1 and e['chan'] != -1}
    search, out = 0, set()
    for e in order:
        if e['port'] != -1 and e['chan'] != -1:
            slot = e['port'] * 16 + e['chan']
        elif e['chan'] != -1:
            slot = e['chan']
            while slot in used:
                slot += 16
            used.add(slot)
        elif e['drum']:
            slot = 9
            while slot in used:
                slot += 16
            used.add(slot)
        else:
            while search % 16 == 9 or search in used:
                search += 1
            used.add(search)
            slot = search
        if e['harmony']:
            out.add(slot % 16)
    return out


def compare_with_musescore(score_path: Path, midi_path: Path, tolerance: int = 2) -> dict:
    """Match our notes with MuseScore's note-ons by (staff track, pitch, onset within ``tolerance`` ticks), after
    moving our ticks onto MuseScore's axis (its MIDI inserts a measure of ticks at every pause).  Durations are
    compared for matched notes whose pitch MuseScore did not have to share on its MIDI channel."""
    data, _ = msi.read_bytes(str(score_path))
    score = msi.read(str(score_path))
    mid = mido.MidiFile(str(midi_path))
    scale = Fraction(mid.ticks_per_beat, score.division)
    harmony = _harmony_channels(data)
    sig_ticks = [t for t, _, _ in score.sigs]
    inserted = []
    for d in score.directions:
        if d.kind == 'pause':
            _, n, den = score.sigs[max(0, bisect.bisect_right(sig_ticks, d.tick) - 1)]
            inserted.append((d.tick, int(4 * n * 480 / den)))

    def axis(t):
        return int(round(Fraction(t) * scale)) + sum(k for p, k in inserted if p <= t)

    theirs, spans = [], defaultdict(list)
    for ti, track in enumerate(mid.tracks):
        tick, pending = 0, defaultdict(list)
        rows = []
        for msg in track:
            tick += msg.time
            if msg.type == 'note_on' and msg.velocity:
                pending[(msg.channel, msg.note)].append(len(rows))
                rows.append([tick, msg.note, msg.channel, None])
            elif msg.type == 'note_off' or (msg.type == 'note_on' and not msg.velocity):
                q = pending.get((msg.channel, msg.note))
                if q:
                    k = q.pop(0)
                    rows[k][3] = tick - rows[k][0]
        theirs.append(rows)
        for k, (t, p, ch, dur) in enumerate(rows):
            spans[(ch, p)].append((t, t + (dur or 0), ti, k))
    result = dict(ours=0, theirs=0, matched=0, durations=0, shared=0, chord_symbols=0, missing=[], extra=[])
    by_track = defaultdict(list)
    for n in score.notes:
        by_track[score.staves[n.staff].get('scoreStaff', n.staff + 1) - 1].append(n)
    for ti, rows in enumerate(theirs):
        mine = by_track.get(ti, [])
        index = defaultdict(list)
        for k, (t, p, ch, dur) in enumerate(rows):
            if ch in harmony:
                result['chord_symbols'] += 1
                continue
            index[p].append((t, k))
            result['theirs'] += 1
        used = set()
        result['ours'] += len(mine)
        for n in sorted(mine, key=lambda n: (n.tick, n.pitch)):
            t = axis(n.tick)
            cands = [(abs(ct - t), k) for ct, k in index.get(n.pitch, []) if abs(ct - t) <= tolerance and k not in used]
            if not cands:
                result['extra'].append((ti, int(t), n.pitch))
                continue
            _, k = min(cands)
            used.add(k)
            result['matched'] += 1
            ct, p, ch, dur = rows[k]
            end = ct + (dur or 0)
            shared = any((a == ct or a == end or (a < end and ct < b)) and (tj, kj) != (ti, k) for a, b, tj, kj in spans[(ch, p)])
            if shared:
                result['shared'] += 1
            elif dur is not None and abs(dur + 1 - int(round(n.play_ticks * scale))) <= max(3, 0.03 * n.play_ticks * scale):
                result['durations'] += 1
        for p, cands in index.items():
            result['missing'].extend((ti, t, p) for t, k in cands if k not in used)
    # MuseScore plays trills, mordents, turns, glissandos and bends as runs of notes and folds grace notes into a
    # trill; Resonance keeps the written note and names it.  A difference under such a note is explained.
    realized = {'trill', 'mordent', 'turn', 'shortTrill', 'tremblement', 'prallMordent', 'upPrall', 'upMordent',
                'downMordent', 'prallUp', 'prallDown', 'linePrall', 'precompSlide', 'precompMordentUpperPrefix',
                'turnInverted', 'turnSlash', 'mordentInverted', 'glissando', 'bend'}
    spans = defaultdict(list)
    for ti, mine in by_track.items():
        for n in mine:
            if set(n.articulations) & realized:
                a = axis(n.tick)
                spans[ti].append((a - mid.ticks_per_beat // 2, a + int(round(n.ticks * scale)) + tolerance))

    def under(ti, t):
        return any(a <= t <= b for a, b in spans.get(ti, ()))

    result['unexplained_missing'] = [m for m in result['missing'] if not under(m[0], m[1])]
    result['unexplained_extra'] = [e for e in result['extra'] if not under(e[0], e[1])]
    compared = result['matched'] - result['shared']
    result['precision'] = result['matched'] / result['ours'] if result['ours'] else 1.0
    result['recall'] = result['matched'] / result['theirs'] if result['theirs'] else 1.0
    result['duration_agreement'] = result['durations'] / compared if compared else 1.0
    return result


@unittest.skipUnless(MUSESCORE.exists(), 'MuseScore 4 is not installed (development-time reference only)')
class AgainstMuseScore(unittest.TestCase):
    """Optional: Resonance's reading against MuseScore 4's own MIDI export."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def check(self, path: Path) -> dict:
        midi = musescore_midi(path, self.dir / (path.stem + '.mid'))
        r = compare_with_musescore(path, midi)
        explained = len(r['missing']) - len(r['unexplained_missing']) + len(r['extra']) - len(r['unexplained_extra'])
        print(f"\n  {path.stem[:56]:56s} notes {r['ours']:6d}/{r['theirs']:6d} precision {r['precision']:.4f} "
              f"recall {r['recall']:.4f} durations {r['duration_agreement']:.4f}"
              + (f" ({explained} under ornaments/glissandos)" if explained else '')
              + (f" (chord symbols {r['chord_symbols']} not compared)" if r['chord_symbols'] else ''))
        return r

    def test_generated_scores(self):
        """The generated scores of the tests above (both formats) play exactly as MuseScore plays them."""
        names = ['test_two_voices_and_location_moves', 'test_nested_tuplets', 'test_ties_across_bar_lines_make_one_note',
                 'test_grace_notes', 'test_repeats_with_first_and_second_endings', 'test_repeat_played_three_times',
                 'test_da_capo_al_fine', 'test_dal_segno_al_coda', 'test_measure_repeat',
                 'test_irregular_pickup_and_measure_numbers', 'test_tempo_dynamics_pedal_and_articulations',
                 'test_ottava_lines_shift_the_sounding_pitch', 'test_tremolos_are_played_as_repeated_notes',
                 'test_pause_is_time_not_ticks', 'test_timing_json_lists_the_played_measures']
        # (not test_old_tuplet_ids_and_ties: MuseScore 4 ignores a 2.x-style <Tie> left in a 3.x file; Resonance
        # keeps the tie)
        cases = [kind(name) for kind in (Modern, Modern3) for name in names]
        for case in cases:
            case.setUp()
            try:
                getattr(case, case._testMethodName)()
                path = self.dir / f'{case.version}-{case._testMethodName}.mscz'
                shutil.copy(case.path, path)
            finally:
                case.tearDown()
            r = self.check(path)
            self.assertEqual((r['precision'], r['recall']), (1.0, 1.0), (path.stem, r['missing'][:5], r['extra'][:5]))

    @unittest.skipUnless(CORPUS.exists(), 'no MuseScore 4 scores folder')
    def test_scores_in_the_musescore_folder(self):
        names = [p.stem for p in sorted(CORPUS.glob('*.mscz'))] if os.environ.get('RESONANCE_MUSESCORE_CORPUS') == 'all' \
            else [n for n in NAMED if (CORPUS / f'{n}.mscz').exists()]
        if not names:
            self.skipTest('none of the named scores is there')
        for name in names:
            copy = self.dir / f'{name}.mscz'
            shutil.copy(CORPUS / f'{name}.mscz', copy)          # the folder is only read
            r = self.check(copy)
            # trills, mordents and glissandos are played by MuseScore as runs of notes, Resonance keeps the
            # written note: those differences are explained; every other note must be found both ways
            self.assertGreaterEqual(r['precision'], 0.99, name)
            self.assertLessEqual(len(r['unexplained_missing']), 0.01 * r['theirs'], (name, r['unexplained_missing'][:8]))
            self.assertLessEqual(len(r['unexplained_extra']), 0.01 * r['ours'], (name, r['unexplained_extra'][:8]))


if __name__ == '__main__':
    unittest.main(verbosity=2)
