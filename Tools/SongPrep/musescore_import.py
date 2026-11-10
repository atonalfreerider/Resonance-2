"""MuseScore scores (.mscz / .mscx) into timed notes, a MIDI file and a timing report.

    python Tools/SongPrep/musescore_import.py score.mscz --midi score.mid --json score-import.json

Every format version is accepted:

* **MuseScore 1.x and 2.x** (mscx 1.14 and older, 2.0x) are read here.  These files keep the music staff by
  staff: each ``<Staff id>`` holds that staff's measures; inside a measure, chords and rests follow one another
  from a cursor that an element's ``<tick>`` moves (an absolute tick) and an element's ``<track>`` assigns to a
  voice (track = staff * 4 + voice).  Tuplets are declared once (``<Tuplet id>``) and joined by id; grace chords
  carry ``<acciaccatura/>`` (or ``<appoggiatura/>``, ``<grace4>``...) and take no time; ties start on a note
  (``<Tie>``) and join the next note of that pitch in that voice; the time-signature list and the tempo list are
  global (``<siglist>`` with actual and nominal lengths, ``<tempolist>`` in quarter notes per second); pedal
  lines and hairpins are global spanners with ``<tick>``/``<tick2>``.
* **MuseScore 3.x and 4.x** are read by ``musescore_modern.py`` (voices inside measures, relative ``<location>``
  moves, tuplets opened and closed in place, ties and other spanners as ``<Spanner>`` pairs, repeats and jumps
  unrolled as MuseScore plays them).

Resonance reads every version itself: nothing outside this project is needed (MuseScore is not run).

**Corrupted measures.**  Every measure of every staff is checked against its time signature, voice by voice,
as MuseScore checks a score when it opens it: voice 1 must fill the measure exactly, other voices may stop
early but never run past the bar line, and no element may start outside its measure.  A measure that fails is
repaired in the import (never in the source file) and the repair is written to the report with the measure's
printed number, staff, voice, what was found and what was done:

* content that overruns because a tuplet's declared span disagrees with its notes is fitted by the tuplet's
  ratio (the notes keep their relative lengths);
* a voice whose notes overrun the bar line with no tuplet to blame is scaled to the measure (an unmarked tuplet,
  the usual cause), unless only its last note crosses the bar line, which is then shortened;
* a voice 1 that stops early is padded with silence;
* an element whose absolute ``<tick>`` lies outside its measure is moved to where the voice's cursor stood.

MuseScore 4 also rejects some old files whose measures are complete, because its 1.x reader places an element
differently from MuseScore 1.x (it ignores a Tuplet's own ``<tick>``, which MuseScore 1.3 wrote to bring the
reading position back after a bar's annotations); :func:`museScore4_compatibility` names those measures, so the
report says what MuseScore would complain about and how this import reads it, and ``--musescore4-copy`` writes
a copy MuseScore 4 opens (each misread chord gets its own ``<tick>``; nothing else changes).

Ottava lines (8va, 8vb, 15ma...) move the sounding pitch: the file keeps the written pitch, as MuseScore does.

Measure numbers are the printed ones: an incomplete first measure (a pickup) is measure 0, irregular measures
are not counted, exactly as MuseScore numbers them.  Times in the JSON are in seconds of the score's own tempo
map; aligning to a recording happens later (``prepare_song.py``), which keeps every tick.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import sys
import zipfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field, asdict
from fractions import Fraction
from pathlib import Path
from typing import Dict, List, Optional, Tuple

# Lengths of note values in quarter notes.
VALUES = {'longa': Fraction(16), 'breve': Fraction(8), 'whole': Fraction(4), 'half': Fraction(2),
          'quarter': Fraction(1), 'eighth': Fraction(1, 2), '16th': Fraction(1, 4), '32nd': Fraction(1, 8),
          '64th': Fraction(1, 16), '128th': Fraction(1, 32), '256th': Fraction(1, 64)}
GRACE_TAGS = ('acciaccatura', 'appoggiatura', 'grace4', 'grace8after', 'grace16', 'grace16after', 'grace32',
              'grace32after')
# MuseScore's default dynamic velocities.
DYNAMICS = {'pppppp': 1, 'ppppp': 5, 'pppp': 10, 'ppp': 16, 'pp': 33, 'p': 49, 'mp': 64, 'mf': 80, 'f': 96,
            'ff': 112, 'fff': 126, 'ffff': 127, 'fffff': 127, 'ffffff': 127, 'fp': 96, 'pf': 49, 'sf': 112,
            'sfz': 112, 'sff': 126, 'sffz': 126, 'sfp': 112, 'sfpp': 112, 'rfz': 112, 'rf': 112, 'fz': 112}
ACCENTS = {'sf', 'sfz', 'sff', 'sffz', 'sfp', 'sfpp', 'rfz', 'rf', 'fz', 'fp'}   # one-note dynamics
GATE = {'staccato': Fraction(1, 2), 'staccatissimo': Fraction(1, 3), 'ustaccatissimo': Fraction(1, 3),
        'dstaccatissimo': Fraction(1, 3), 'portato': Fraction(3, 4)}
LETTERS = 'FCGDAEB'
STEP_SEMITONES = {'C': 0, 'D': 2, 'E': 4, 'F': 5, 'G': 7, 'A': 9, 'B': 11}
# Ottava lines: MuseScore 1.x numbers the subtypes, 2.x and later name them.
OTTAVA_SHIFTS = {'0': 12, '1': 24, '2': -12, '3': -24, '8va': 12, '8vb': -12, '15ma': 24, '15mb': -24,
                 '22ma': 36, '22mb': -36, '8va alta': 12, '8va bassa': -12, '15ma alta': 24, '15ma bassa': -24}
INLINE_SPANNERS = ('Ottava', 'Pedal')       # 2.x spanners opened in a measure (<X id>) and closed by <endSpanner id>


def ottava_shift(el: ET.Element) -> int:
    """Semitones an ottava line moves its notes: from its subtype, else from its printed text (8va, 8vb...)."""
    sub = (el.findtext('subtype') or '').strip()
    if sub in OTTAVA_SHIFTS:
        return OTTAVA_SHIFTS[sub]
    text = ' '.join(t for t in el.itertext()).lower()
    for mark, shift in (('22mb', -36), ('22ma', 36), ('15mb', -24), ('15ma', 24), ('8vb', -12), ('bassa', -12), ('8va', 12)):
        if mark in text:
            return shift
    return 12


class ScoreImportError(Exception):
    pass


@dataclass
class Measure:
    index: int                 # position in the score (0-based)
    number: int                # printed number (a pickup is 0)
    tick: Fraction             # start, in ticks of the score's division
    ticks: Fraction            # actual length
    time_sig: Tuple[int, int]  # nominal (as printed)
    actual: Tuple[int, int]    # actual length as a fraction of a whole note
    irregular: bool = False


@dataclass
class Note:
    staff: int
    voice: int
    track: int
    pitch: int
    tpc: Optional[int]
    tick: Fraction
    ticks: Fraction            # written length (ties merged)
    play_ticks: Fraction       # sounding length (articulation gate)
    velocity: int
    measure: int               # measure index
    grace: bool = False
    articulations: List[str] = field(default_factory=list)
    tie_start: bool = False


@dataclass
class Direction:
    tick: Fraction
    staff: int
    kind: str                  # tempo | dynamic | text | expression
    text: str
    measure: int = -1


@dataclass
class Repair:
    measure: int               # printed number
    index: int                 # measure index
    staff: int
    voice: int
    kind: str
    found: str
    expected: str
    action: str


@dataclass
class ScoreImport:
    source: str
    sha256: str
    format_version: str
    program_version: str
    reader: str
    title: str
    composer: str
    division: int
    parts: List[dict]
    staves: List[dict]
    measures: List[Measure]
    notes: List[Note]
    tempos: List[Tuple[Fraction, float]]          # (tick, quarter notes per second)
    sigs: List[Tuple[Fraction, int, int]]         # (tick, numerator, denominator), actual lengths
    pedals: List[Tuple[int, Fraction, Fraction]]  # (staff, tick, tick2)
    directions: List[Direction]
    repairs: List[Repair] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)
    musescore4: List[dict] = field(default_factory=list)


# ---------------------------------------------------------------------------------------------------- reading

def read_bytes(path: str) -> Tuple[bytes, str]:
    """The main .mscx text of a .mscz (or the .mscx itself) and its name inside the archive."""
    p = Path(path)
    if p.suffix.lower() == '.mscx':
        return p.read_bytes(), p.name
    with zipfile.ZipFile(p) as z:
        names = z.namelist()
        root = None
        if 'META-INF/container.xml' in names:
            container = ET.fromstring(z.read('META-INF/container.xml'))
            for rf in container.iter('rootfile'):
                full = rf.get('full-path')
                if full and full in names and full.lower().endswith('.mscx'):
                    root = full
                    break
        if root is None:
            candidates = [n for n in names if n.lower().endswith('.mscx') and '/' not in n.strip('/')]
            candidates = candidates or [n for n in names if n.lower().endswith('.mscx')]
            if not candidates:
                raise ScoreImportError(f'{path}: no .mscx inside the archive')
            root = candidates[0]
        return z.read(root), root


def file_version(data: bytes) -> str:
    head = data[:4000].decode('utf-8', 'replace')
    m = re.search(r'<museScore\s+version="([^"]+)"', head)
    if not m:
        raise ScoreImportError('not a MuseScore file (no <museScore version>)')
    return m.group(1)


def version_number(version: str) -> float:
    try:
        major, _, minor = version.partition('.')
        return int(major) + int((minor or '0')[:2].ljust(2, '0')) / 100
    except ValueError:
        return 0.0


def _text(el: Optional[ET.Element]) -> str:
    """Plain text of a text element: 1.x keeps rich text as embedded HTML (<html-data>), 2.x+ as <text>."""
    if el is None:
        return ''
    html = el.find('html-data')
    if html is not None:
        parts = []
        for node in html.iter():
            if node.tag in ('style', 'head'):
                continue
            if node.text:
                parts.append(node.text)
            if node.tail and node is not html:
                parts.append(node.tail)
        raw = ' '.join(parts)
    else:
        t = el.find('text')
        raw = ''.join(t.itertext()) if t is not None else ''
    raw = re.sub(r'p, li \{[^}]*\}', '', raw)
    raw = raw.replace('', '♪').replace('', '♩')
    return re.sub(r'\s+', ' ', raw).strip()


def _int(el: ET.Element, tag: str, default=None):
    v = el.findtext(tag)
    if v is None or not v.strip():
        return default
    try:
        return int(v.strip())
    except ValueError:
        return int(float(v.strip()))


def spelled(pitch: int, tpc: Optional[int]) -> str:
    """'C#4' from a MIDI pitch and MuseScore's tonal pitch class (line of fifths, 14 = C)."""
    if tpc is None or not (-1 <= tpc <= 33):
        names = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']
        return f'{names[pitch % 12]}{pitch // 12 - 1}'
    letter = LETTERS[(tpc + 1) % 7]
    alter = (tpc + 1) // 7 - 2
    octave = (pitch - alter - STEP_SEMITONES[letter]) // 12 - 1
    return letter + ('#' * alter if alter > 0 else 'b' * -alter) + str(octave)


class LegacyReader:
    """MuseScore 1.x / 2.x."""

    def __init__(self, data: bytes, version: str):
        self.version = version
        self.root = ET.fromstring(data)
        self.score = self.root.find('Score')
        if self.score is None:
            self.score = self.root
        self.division = _int(self.score, 'Division', 480) or 480
        self.repairs: List[Repair] = []
        self.warnings: List[str] = []
        self.ms4_issues: List[dict] = []
        self._ottava: List[Tuple[int, Fraction, Fraction, int]] = []
        self._inline_open: Dict[str, tuple] = {}
        self._inline_ottavas: List[Tuple[int, Fraction, Fraction, int]] = []
        self._inline_pedals: List[Tuple[int, Fraction, Fraction]] = []

    # --------------------------------------------------------------- structure
    def parts_and_staves(self):
        parts, staves = [], []
        for pi, part in enumerate(self.score.findall('Part')):
            instrument = part.find('Instrument')
            name = (instrument.findtext('trackName') if instrument is not None else None) or part.findtext('trackName') or f'Part {pi + 1}'
            program = 0
            if instrument is not None:
                prog = instrument.find('.//program')
                if prog is not None and prog.get('value') is not None:
                    program = int(prog.get('value'))
            count = max(1, len(part.findall('Staff')))
            first = len(staves)
            for k in range(count):
                if count == 2 and program in range(0, 8):
                    label = 'Right hand' if k == 0 else 'Left hand'
                else:
                    label = name if count == 1 else f'{name} {k + 1}'
                staves.append(dict(index=len(staves), part=pi, name=label))
            parts.append(dict(index=pi, name=name, program=program, staves=list(range(first, first + count))))
        music = [s for s in self.score.findall('Staff') if s.find('Measure') is not None]
        if not staves:
            staves = [dict(index=i, part=0, name=f'Staff {i + 1}') for i in range(len(music))]
            parts = [dict(index=0, name='Score', program=0, staves=list(range(len(music))))]
        if len(music) != len(staves):
            self.warnings.append(f'{len(music)} staves with music, {len(staves)} declared: using the music staves in order')
            staves = staves[:len(music)] + [dict(index=i, part=parts[-1]['index'], name=f'Staff {i + 1}') for i in range(len(staves), len(music))]
        return parts, staves, music

    def signatures(self, music) -> List[Tuple[Fraction, int, int, int, int]]:
        """(tick, actual n, actual d, nominal n, nominal d) from <siglist> (1.x) or the first staff's TimeSigs (2.x)."""
        out = []
        siglist = self.score.find('siglist')
        if siglist is not None:
            for s in siglist.findall('sig'):
                n, d = _int(s, 'nom', 4), _int(s, 'denom', 4)
                out.append((Fraction(int(s.get('tick', '0'))), n, d, _int(s, 'nom2', n), _int(s, 'denom2', d)))
        if not out:
            tick = Fraction(0)
            for m in music[0].findall('Measure'):
                ts = m.find('TimeSig')
                if ts is not None:
                    n = _int(ts, 'sigN', _int(ts, 'nom1', 4)); d = _int(ts, 'sigD', _int(ts, 'den', 4))
                    out.append((tick, n, d, n, d))
                # 2.x: an irregular measure carries its actual length
                length = m.get('len')
                if length:
                    a, b = (int(x) for x in length.split('/'))
                    nom = out[-1] if out else (tick, 4, 4, 4, 4)
                    out.append((tick, a, b, nom[3], nom[4]))
                    out.append((tick + Fraction(4 * a, b) * self.division, nom[3], nom[4], nom[3], nom[4]))
                cur = out[-1] if out else (tick, 4, 4, 4, 4)
                tick += Fraction(4 * cur[1], cur[2]) * self.division
        if not out:
            out = [(Fraction(0), 4, 4, 4, 4)]
        out.sort(key=lambda x: x[0])
        return out

    def tempos(self, music) -> List[Tuple[Fraction, float]]:
        out: Dict[Fraction, float] = {}
        tl = self.score.find('tempolist')
        if tl is not None:
            for t in tl.findall('tempo'):
                try:
                    out[Fraction(int(t.get('tick', '0')))] = float(t.text)
                except (TypeError, ValueError):
                    continue
        return sorted(out.items())

    # --------------------------------------------------------------- measures
    def measures(self, music, sigs) -> List[Measure]:
        first = music[0].findall('Measure')
        result = []
        tick = Fraction(0)
        counted = 0
        for i, m in enumerate(first):
            active = sigs[0]
            for s in sigs:
                if s[0] <= tick:
                    active = s
            _, n, d, nn, nd = active
            length = m.get('len')
            if length:
                a, b = (int(x) for x in length.split('/'))
                n, d = a, b
            ticks = Fraction(4 * n, d) * self.division
            irregular = m.find('irregular') is not None or (n * nd != nn * d) or bool(length)
            if not irregular:
                counted += 1
            number = counted if not irregular else (0 if i == 0 else counted)
            result.append(Measure(index=i, number=number, tick=tick, ticks=ticks, time_sig=(nn, nd), actual=(n, d),
                                  irregular=irregular))
            tick += ticks
        return result

    # --------------------------------------------------------------- notes
    def duration(self, el: ET.Element, tuplets, measure: Measure) -> Fraction:
        kind = el.findtext('durationType') or 'quarter'
        if kind == 'measure':
            return measure.ticks
        value = VALUES.get(kind)
        if value is None:
            raise ScoreImportError(f'unknown duration type {kind!r}')
        d = value * self.division
        dots = _int(el, 'dots', 0) or 0
        add = d
        for _ in range(dots):
            add /= 2
            d += add
        tid = el.findtext('Tuplet')
        if tid is not None and tid.strip() in tuplets:
            normal, actual = tuplets[tid.strip()]['ratio']
            d = d * normal / actual
        return d

    def read(self, source: str, sha: str) -> ScoreImport:
        parts, staves, music = self.parts_and_staves()
        sigs = self.signatures(music)
        measures = self.measures(music, sigs)
        tempos = self.tempos(music)
        notes: List[Note] = []
        directions: List[Direction] = []
        dynamics: Dict[int, List[Tuple[Fraction, int, bool]]] = {}
        self._ottava = self._ottavas(len(music))

        for staff_index, staff in enumerate(music):
            elements = staff.findall('Measure')
            if len(elements) != len(measures):
                self.warnings.append(f'staff {staff_index + 1} has {len(elements)} measures, the first staff {len(measures)}')
            open_ties: Dict[Tuple[int, int], Note] = {}
            dyn = dynamics.setdefault(staff_index, [])
            for mi, m in enumerate(elements[:len(measures)]):
                measure = measures[mi]
                placed = self._read_measure(m, measure, staff_index, directions, dyn)
                self._check_measure(placed, measure, staff_index)
                notes.extend(self._notes_of(placed, measure, staff_index, open_ties))
        # velocities from dynamics where the note has none of its own
        for n in notes:
            if n.velocity <= 0:
                n.velocity = self._dynamic_at(dynamics.get(n.staff, []), n.tick)
        # 2.x ottavas opened and closed inside measures (1.x ones were applied while reading)
        for s, t, t2, shift in self._inline_ottavas:
            for n in notes:
                if n.staff == s and t <= n.tick < t2:
                    n.pitch += shift
        pedals = sorted(set(self._pedals(len(music)) + self._inline_pedals), key=lambda x: (x[1], x[0]))
        title, composer = self._metadata()
        notes.sort(key=lambda n: (n.tick, n.staff, n.pitch))
        for d in directions:
            d.measure = measure_at(measures, d.tick).index
        sig_list = [(t, n, d) for (t, n, d, _, _) in sigs]
        return ScoreImport(source=source, sha256=sha, format_version=self.version,
                           program_version=self.root.findtext('programVersion') or '', reader='resonance.legacy',
                           title=title, composer=composer, division=self.division, parts=parts, staves=staves,
                           measures=measures, notes=notes, tempos=tempos, sigs=sig_list, pedals=pedals,
                           directions=directions, repairs=self.repairs, warnings=self.warnings)

    def _read_measure(self, m: ET.Element, measure: Measure, staff: int, directions, dyn) -> List[dict]:
        """Chords and rests of one staff's measure, placed: dicts with tick, ticks, track, element, grace.

        The reading position follows MuseScore 1.x: ANY element's own ``<tick>`` moves it (MuseScore 1.3's writer
        tracked one position for everything it wrote, so a chord with no tick of its own follows whatever element
        last wrote one - a Tempo, a Dynamic or a Tuplet).  MuseScore 4's 1.x reader ignores a Tuplet's tick when
        it moves the position; where that matters the measure is recorded in ``self.ms4_issues``."""
        tuplets: Dict[str, dict] = {}
        cursor = measure.tick
        ms4 = cursor                                # the position MuseScore 4 would read at
        default_track = staff * 4
        placed: List[dict] = []
        pending_grace: List[dict] = []
        for position, el in enumerate(m):
            tag = el.tag
            own = el.findtext('tick') if tag not in ('Chord', 'Rest', 'tick') else None
            if own is not None:
                try:
                    cursor = Fraction(int(own))
                    if tag != 'Tuplet':
                        ms4 = cursor
                except ValueError:
                    pass
            if tag == 'Tuplet':
                tid = el.get('id') or el.findtext('id') or str(len(tuplets))
                normal = _int(el, 'normalNotes', 2); actual = _int(el, 'actualNotes', 3)
                base = el.findtext('baseNote')
                tuplets[tid] = dict(ratio=(normal, actual), base=base, members=[], tick=_int(el, 'tick'),
                                    ms4=ms4, cursor=cursor)
                continue
            if tag == 'tick':                       # 2.x: a bare cursor move
                try:
                    cursor = ms4 = Fraction(int(el.text))
                except (TypeError, ValueError):
                    pass
                continue
            if tag in INLINE_SPANNERS and el.get('id') is not None:         # 2.x: a spanner opened in place
                self._inline_open[el.get('id')] = (tag, staff, cursor, el)
                continue
            if tag == 'endSpanner' and el.get('id') in self._inline_open:
                kind, s, start, opened = self._inline_open.pop(el.get('id'))
                if cursor > start:
                    if kind == 'Ottava':
                        self._inline_ottavas.append((s, start, cursor, ottava_shift(opened)))
                    else:
                        self._inline_pedals.append((s, start, cursor))
                continue
            if tag in ('Tempo',):
                directions.append(Direction(tick=cursor, staff=staff, kind='tempo', text=_text(el)))
                continue
            if tag == 'Dynamic':
                sub = (el.findtext('subtype') or '').strip()
                text = _text(el)
                if sub in DYNAMICS:
                    dyn.append((cursor, DYNAMICS[sub], sub in ACCENTS))
                    directions.append(Direction(tick=cursor, staff=staff, kind='dynamic', text=sub))
                elif text:
                    directions.append(Direction(tick=cursor, staff=staff, kind='expression', text=text))
                continue
            if tag in ('StaffText', 'Text', 'SystemText', 'RehearsalMark'):
                sub = el.findtext('subtype') or ''
                text = _text(el)
                if text and sub != 'MeasureNumber':
                    directions.append(Direction(tick=cursor, staff=staff, kind='text', text=text))
                continue
            if tag not in ('Chord', 'Rest'):
                continue
            t = el.findtext('tick')
            explicit = False
            if t is not None:
                try:
                    cursor = ms4 = Fraction(int(t))
                    explicit = True
                except ValueError:
                    pass
            tuplet_id = (el.findtext('Tuplet') or '').strip() or None
            chord_number = len(placed) + len(pending_grace)          # this element's place among the measure's chords/rests
            if not explicit and ms4 != cursor:
                # Only a tuplet's own tick separates the two readings; the first chord that follows it lands
                # elsewhere in MuseScore 4 (later: the voice overruns and the file is refused; earlier: it plays early).
                late = ms4 > cursor
                self.ms4_issues.append(dict(
                    measure=measure.number, index=measure.index, museScore4Measure=measure.index + 1,
                    staff=staff + 1, chord=chord_number, tuplet=tuplet_id, tick=int(cursor), museScore4Tick=int(ms4),
                    problem=(f'MuseScore 4 ignores the <tick> of the tuplet before this chord and starts it at tick {ms4} '
                             f'instead of {cursor}' + (', so the voice runs past the bar line and MuseScore 4 refuses the '
                             'file as corrupted ("Incomplete measure", numbering the pickup as measure 1)' if late else
                             ', so it plays that passage early')),
                    resonance='read as MuseScore 1.x wrote it (every <tick> moves the reading position): the measure is complete',
                    fix=f'give that chord its own <tick>{cursor}</tick> (no change under MuseScore 1.x rules)'))
                ms4 = cursor
            track = _int(el, 'track', default_track)
            if track is None or track // 4 != staff:
                # a track from another staff (cross-staff notes keep their own staff's track): keep the voice
                track = staff * 4 + ((track or 0) % 4)
            grace = any(el.find(g) is not None for g in GRACE_TAGS) or (el.findtext('noteType') or '').startswith(('acciaccatura', 'appoggiatura', 'grace'))
            ticks = self.duration(el, tuplets, measure)
            item = dict(element=el, tick=cursor, ticks=ticks, track=track, grace=grace, rest=tag == 'Rest',
                        tuplet=(el.findtext('Tuplet') or '').strip() or None, written=ticks, explicit=explicit)
            if item['tuplet'] in tuplets:
                tuplets[item['tuplet']]['members'].append(item)
            if grace:
                pending_grace.append(item)
                continue
            for g in pending_grace:                 # graces sound just before their main chord
                g['tick'] = cursor
                g['before'] = True
            placed.extend(pending_grace)
            pending_grace = []
            placed.append(item)
            cursor = cursor + ticks
            ms4 = ms4 + ticks
        for g in pending_grace:                     # a trailing grace (after-grace)
            g['tick'] = cursor
            placed.append(g)
        self._tuplets = tuplets
        return placed

    # --------------------------------------------------------------- validation and repair
    def _check_measure(self, placed: List[dict], measure: Measure, staff: int):
        start, end = measure.tick, measure.tick + measure.ticks
        voices: Dict[int, List[dict]] = {}
        for item in placed:
            if item['grace']:
                continue
            voices.setdefault(item['track'] % 4, []).append(item)
        for voice, items in sorted(voices.items()):
            items.sort(key=lambda it: it['tick'])
            # elements whose absolute tick lies outside the measure
            for k, it in enumerate(items):
                if it['explicit'] and (it['tick'] < start or it['tick'] >= end):
                    prev_end = items[k - 1]['tick'] + items[k - 1]['ticks'] if k else start
                    self._repair(measure, staff, voice, 'element outside its measure',
                                 found=f'starts at tick {it["tick"]}', expected=f'{start}..{end}',
                                 action=f'moved to tick {prev_end}')
                    it['tick'] = prev_end
            content_end = max(it['tick'] + it['ticks'] for it in items)
            filled = content_end - start
            if filled > measure.ticks:
                over = [it for it in items if it['tick'] + it['ticks'] > end]
                tuplet_ids = {it['tuplet'] for it in items if it['tuplet']}
                if tuplet_ids:
                    # fit the tuplet(s) so the voice ends at the bar line
                    excess = filled - measure.ticks
                    span = sum(it['ticks'] for it in items if it['tuplet'] in tuplet_ids)
                    if span > excess:
                        factor = (span - excess) / span
                        self._scale_from(items, lambda it: it['tuplet'] in tuplet_ids, factor)
                        self._repair(measure, staff, voice, 'tuplet does not fit the measure',
                                     found=frac_text(filled / (4 * self.division)), expected=sig_text(measure),
                                     action=f'tuplet ratio corrected by {factor}')
                        continue
                if len(over) == 1 and over[0] is items[-1] and over[0]['tick'] < end:
                    before = over[0]['ticks']
                    over[0]['ticks'] = end - over[0]['tick']
                    self._repair(measure, staff, voice, 'last note crosses the bar line',
                                 found=frac_text(filled / (4 * self.division)), expected=sig_text(measure),
                                 action=f'last note shortened from {before} to {over[0]["ticks"]} ticks')
                    continue
                factor = measure.ticks / filled
                self._scale_from(items, lambda it: True, factor, origin=start)
                self._repair(measure, staff, voice, 'voice longer than the measure',
                             found=frac_text(filled / (4 * self.division)), expected=sig_text(measure),
                             action=f'voice scaled by {factor} (an unmarked tuplet)')
            elif filled < measure.ticks and voice == 0:
                self._repair(measure, staff, voice, 'incomplete voice 1',
                             found=frac_text(filled / (4 * self.division)), expected=sig_text(measure),
                             action='padded with silence to the bar line')

    @staticmethod
    def _scale_from(items, select, factor, origin=None):
        """Scale the lengths of the selected items by factor and re-flow the voice: each item follows the one
        before it after the same gap as before (gaps only come from explicit <tick> moves), starting at origin
        (or where the voice started)."""
        gaps = [Fraction(0)] + [max(Fraction(0), b['tick'] - (a['tick'] + a['ticks'])) for a, b in zip(items, items[1:])]
        cursor = origin if origin is not None else items[0]['tick']
        for it, gap in zip(items, gaps):
            if select(it):
                it['ticks'] = it['ticks'] * factor
            it['tick'] = cursor + gap
            cursor = it['tick'] + it['ticks']

    def _repair(self, measure: Measure, staff: int, voice: int, kind: str, found: str, expected: str, action: str):
        self.repairs.append(Repair(measure=measure.number, index=measure.index, staff=staff + 1, voice=voice + 1,
                                   kind=kind, found=found, expected=expected, action=action))

    # --------------------------------------------------------------- notes, ties, articulations
    def _notes_of(self, placed: List[dict], measure: Measure, staff: int, open_ties) -> List[Note]:
        out: List[Note] = []
        graces_before: Dict[Fraction, int] = {}
        for item in placed:
            if item['rest']:
                continue
            el = item['element']
            arts = [(a.findtext('subtype') or '').strip() for a in el.findall('Articulation')]
            arts = [a for a in arts if a]
            gate = Fraction(1)
            for a in arts:
                if a in GATE:
                    gate = min(gate, GATE[a])
            voice = item['track'] % 4
            # an ottava line moves the sounding pitch; the file keeps the written one (graces go with their chord)
            shift = sum(sh for s, t, t2, sh in self._ottava if s == staff and t <= item['tick'] < t2)
            for note in el.findall('Note'):
                written = _int(note, 'pitch')
                if written is None:
                    continue
                pitch = written + shift
                tpc = _int(note, 'tpc')
                velocity = 0
                if (note.findtext('veloType') or '').strip() == 'user':
                    velocity = max(1, min(127, _int(note, 'velocity', 0) or 0))
                if item['grace']:
                    k = graces_before.get(item['tick'], 0)
                    graces_before[item['tick']] = k + 1
                    length = Fraction(self.division, 8)            # a 32nd-note grace, taken before the beat
                    out.append(Note(staff=staff, voice=voice, track=item['track'], pitch=pitch, tpc=tpc,
                                    tick=item['tick'] - length * (k + 1), ticks=length, play_ticks=length,
                                    velocity=velocity, measure=measure.index, grace=True, articulations=arts))
                    continue
                key = (item['track'], written)
                tied = open_ties.pop(key, None)
                if tied is not None and tied.tick + tied.ticks == item['tick']:
                    tied.ticks += item['ticks']
                    tied.play_ticks = tied.ticks * (gate if gate < 1 else 1)
                    current = tied
                else:
                    if tied is not None:
                        self.warnings.append(f'measure {measure.number}: tie from {spelled(pitch, tpc)} found no continuation')
                    current = Note(staff=staff, voice=voice, track=item['track'], pitch=pitch, tpc=tpc,
                                   tick=item['tick'], ticks=item['ticks'], play_ticks=item['ticks'] * gate,
                                   velocity=velocity, measure=measure.index, articulations=arts)
                    out.append(current)
                if note.find('Tie') is not None:
                    current.tie_start = True
                    open_ties[key] = current
        return out

    def _dynamic_at(self, dyn, tick) -> int:
        level, accent_tick = 80, None
        for t, v, accent in sorted(dyn, key=lambda x: x[0]):
            if t > tick:
                break
            if accent:
                accent_tick = t
                if t == tick:
                    return v
                continue
            level = v
        return level

    def _pedals(self, staves: int) -> List[Tuple[int, Fraction, Fraction]]:
        out = []
        for p in self.score.findall('Pedal') + self.root.findall('Pedal'):
            t, t2, tr = _int(p, 'tick'), _int(p, 'tick2'), _int(p, 'track', 0)
            if t is None or t2 is None or t2 <= t:
                continue
            out.append((min(staves - 1, (tr or 0) // 4), Fraction(t), Fraction(t2)))
        return sorted(set(out), key=lambda x: (x[1], x[0]))

    def _ottavas(self, staves: int) -> List[Tuple[int, Fraction, Fraction, int]]:
        """1.x ottava lines: global spanners (staff from <track>, default the first) over [tick, tick2)."""
        out = []
        for o in self.score.findall('Ottava') + (self.root.findall('Ottava') if self.root is not self.score else []):
            t, t2, tr = _int(o, 'tick'), _int(o, 'tick2'), _int(o, 'track', 0)
            if t is None or t2 is None or t2 <= t:
                continue
            out.append((min(staves - 1, (tr or 0) // 4), Fraction(t), Fraction(t2), ottava_shift(o)))
        return sorted(set(out), key=lambda x: (x[1], x[0]))

    def _metadata(self) -> Tuple[str, str]:
        title = composer = ''
        for text in self.score.iter('Text'):
            sub = (text.findtext('subtype') or '').lower()
            value = _text(text)
            if sub == 'title' and not title:
                title = value
            elif sub == 'composer' and not composer:
                composer = re.sub(r'\(\s*\d{4}\s*-\s*\d{4}\s*\)', '', value).strip()
        for tag in ('workTitle', 'movementTitle'):
            for mt in self.score.iter('metaTag'):
                if mt.get('name') == tag and (mt.text or '').strip() and not title:
                    title = mt.text.strip()
        for mt in self.score.iter('metaTag'):
            if mt.get('name') == 'composer' and (mt.text or '').strip() and not composer:
                composer = mt.text.strip()
        return title, composer


def frac_text(f: Fraction) -> str:
    return f'{f.numerator}/{f.denominator}'


def sig_text(m: Measure) -> str:
    return f'{m.actual[0]}/{m.actual[1]}'


def measure_at(measures: List[Measure], tick: Fraction) -> Measure:
    lo, hi = 0, len(measures) - 1
    while lo < hi:
        mid = (lo + hi + 1) // 2
        if measures[mid].tick <= tick:
            lo = mid
        else:
            hi = mid - 1
    return measures[lo]


# ---------------------------------------------------------------------------------------------- MuseScore 4 view

def museScore4_compatibility(data: bytes, measures: List[Measure], division: int) -> List[dict]:
    """Measures MuseScore 4 misreads when it opens this 1.x/2.x file, with how this import reads them.

    MuseScore 1.3 wrote one reading position for everything in a measure: when annotations (a 'rall.', a
    dynamic) are written at the start of a bar with their own later ticks, the tuplet that follows carries the
    tick that brings the position back, and its chords carry none.  MuseScore 4's 1.x reader (read114) moves
    the position for a <tick> inside a Chord, Rest, Dynamic or Tempo but not inside a Tuplet, so the tuplet's
    first chord lands on the last annotation's tick, the voice runs past the bar line and the file is refused
    as corrupted ('Incomplete measure ... Found 15/20, Expected 2/4').  :func:`musescore4_copy` writes a copy
    MuseScore 4 opens: the tuplet's first chord gets its own <tick>, which changes nothing under 1.x rules."""
    reader = LegacyReader(data, file_version(data))
    reader.read('', '')
    return reader.ms4_issues


def musescore4_copy(path: str, out: str, issues: List[dict]) -> int:
    """Copy a 1.x/2.x score with each misread tuplet's first chord given its own <tick> (see
    :func:`museScore4_compatibility`).  Only those lines are added; every other byte is kept.  Returns the
    number of chords changed."""
    data, inner = read_bytes(path)
    text = data.decode('utf-8')
    changed = 0
    for issue in sorted(issues, key=lambda i: (i['staff'], i['index']), reverse=True):
        staves = [m.start() for m in re.finditer(rf'<Staff id="{issue["staff"]}">', text)]
        if not staves:
            continue
        start = staves[-1]
        end = text.find('</Staff>', start)
        measures = [m.start() for m in re.finditer(r'<Measure[\s>]', text[start:end])]
        if issue['index'] >= len(measures):
            continue
        m0 = start + measures[issue['index']]
        m1 = text.find('</Measure>', m0)
        chords = [m.start() for m in re.finditer(r'<(?:Chord|Rest)>', text[m0:m1])]
        if issue['chord'] >= len(chords):
            continue
        tag_end = m0 + chords[issue['chord']] + len('<Chord>' if text.startswith('<Chord>', m0 + chords[issue['chord']]) else '<Rest>')
        layout = re.match(r'(\r?\n)([ \t]*)', text[tag_end:])       # keep the file's own line breaks and indent
        insertion = (layout.group(1) + layout.group(2) if layout else '') + f'<tick>{issue["tick"]}</tick>'
        text = text[:tag_end] + insertion + text[tag_end:]
        changed += 1
    fixed = text.encode('utf-8')
    if Path(path).suffix.lower() == '.mscx':
        Path(out).write_bytes(fixed)
    else:
        with zipfile.ZipFile(path) as src, zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as dst:
            for info in src.infolist():
                dst.writestr(info, fixed if info.filename == inner else src.read(info.filename))
    return changed


# --------------------------------------------------------------------------------------------- modern formats

def read_modern(path: str, data: bytes, version: str, sha: str) -> 'ScoreImport':
    """MuseScore 3.x / 4.x: Resonance's own reader (musescore_modern.py)."""
    try:
        from musescore_modern import ModernReader            # type: ignore
    except ImportError:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        from musescore_modern import ModernReader            # type: ignore
    return ModernReader(data, version).read(str(Path(path).resolve()), sha)


# ----------------------------------------------------------------------------------------------------- entry

def read(path: str) -> ScoreImport:
    data, _ = read_bytes(path)
    sha = hashlib.sha256(Path(path).read_bytes()).hexdigest()
    version = file_version(data)
    number = version_number(version)
    if number >= 3.0:
        return read_modern(path, data, version, sha)
    reader = LegacyReader(data, version)
    result = reader.read(str(Path(path).resolve()), sha)
    result.musescore4 = museScore4_compatibility(data, result.measures, result.division)
    if not result.tempos:
        result.tempos = [(Fraction(0), 2.0)]
    return result


class TempoMap:
    """Ticks to seconds through the score's tempo list (quarter notes per second)."""

    def __init__(self, tempos: List[Tuple[Fraction, float]], division: int):
        self.division = division
        points = sorted(tempos) if tempos else [(Fraction(0), 2.0)]
        if points[0][0] > 0:
            points.insert(0, (Fraction(0), points[0][1]))
        self.points = points
        self.seconds = [0.0]
        for (t0, q0), (t1, _) in zip(points, points[1:]):
            self.seconds.append(self.seconds[-1] + float(t1 - t0) / division / q0)

    def __call__(self, tick) -> float:
        tick = Fraction(tick)
        k = 0
        for i, (t, _) in enumerate(self.points):
            if t <= tick:
                k = i
        t0, qps = self.points[k]
        return self.seconds[k] + float(tick - t0) / self.division / qps


# --------------------------------------------------------------------------------------------------- writers

def write_midi(score: ScoreImport, path: str, ppq: Optional[int] = None) -> None:
    """Type-1 MIDI: a conductor track (tempo, time signatures) and one track per staff ('Right hand' / 'Left hand'
    for a piano's two staves), with sustain pedal."""
    import mido
    ppq = ppq or score.division
    scale = Fraction(ppq, score.division)

    def at(tick) -> int:
        return int(round(Fraction(tick) * scale))

    mid = mido.MidiFile(type=1, ticks_per_beat=ppq)
    conductor = mido.MidiTrack()
    mid.tracks.append(conductor)
    events = [(0, 0, mido.MetaMessage('track_name', name=latin1(score.title or 'Score')))]
    for t, qps in score.tempos:
        events.append((at(t), 1, mido.MetaMessage('set_tempo', tempo=int(round(1e6 / qps)))))
    for t, n, d in score.sigs:
        events.append((at(t), 2, mido.MetaMessage('time_signature', numerator=n, denominator=d)))
    # No markers or text events: PatternPrep reads markers as section boundaries and text events as karaoke
    # lyrics. Printed measure numbers and expression texts are in the timing JSON.
    _flush(conductor, events)
    for staff in score.staves:
        track = mido.MidiTrack()
        mid.tracks.append(track)
        channel = staff['part'] % 15
        if channel >= 9:
            channel += 1
        program = next((p['program'] for p in score.parts if p['index'] == staff['part']), 0)
        events = [(0, 0, mido.MetaMessage('track_name', name=latin1(staff['name']))),
                  (0, 1, mido.Message('program_change', channel=channel, program=program))]
        for s, t, t2 in score.pedals:
            if s == staff['index']:
                events.append((at(t), 2, mido.Message('control_change', channel=channel, control=64, value=127)))
                events.append((at(t2), 1, mido.Message('control_change', channel=channel, control=64, value=0)))
        for n in score.notes:
            if n.staff != staff['index']:
                continue
            on, off = at(n.tick), at(n.tick + max(n.play_ticks, Fraction(1)))
            off = max(off, on + 1)
            events.append((on, 3, mido.Message('note_on', channel=channel, note=n.pitch, velocity=max(1, min(127, n.velocity)))))
            events.append((off, 0, mido.Message('note_off', channel=channel, note=n.pitch, velocity=0)))
        _flush(track, events)
    mid.save(path)


def latin1(text: str) -> str:
    """MIDI text events are Latin-1: other characters become their closest plain form."""
    text = text.replace('♪', 'eighth').replace('♩', 'quarter').replace('’', "'").replace('–', '-').replace('—', '-')
    return text.encode('latin-1', 'replace').decode('latin-1')


def _flush(track, events):
    import mido
    events.sort(key=lambda e: (e[0], e[1]))
    last = 0
    for tick, _, msg in events:
        track.append(msg.copy(time=max(0, tick - last)))
        last = max(last, tick)
    track.append(mido.MetaMessage('end_of_track', time=0))


def timing(score: ScoreImport) -> dict:
    """The import as JSON: measures, tempo map, every note with its score tick, printed measure, beat and
    seconds of the score's own tempo map, directions, repairs, warnings."""
    tm = TempoMap(score.tempos, score.division)
    by_index = {m.index: m for m in score.measures}

    def beat(tick, m: Measure) -> float:
        unit = Fraction(4, m.time_sig[1]) * score.division
        return float(1 + (Fraction(tick) - m.tick) / unit)

    notes = []
    for i, n in enumerate(score.notes):
        m = by_index[n.measure]
        notes.append(dict(id=i, staff=n.staff, voice=n.voice, track=n.track, pitch=n.pitch,
                          name=spelled(n.pitch, n.tpc), tpc=n.tpc, tick=float(n.tick), ticks=float(n.ticks),
                          measure=m.number, measureIndex=m.index, beat=round(beat(n.tick, m), 6),
                          start=round(tm(n.tick), 6), end=round(tm(n.tick + n.ticks), 6), velocity=n.velocity,
                          grace=n.grace, articulations=n.articulations))
    measures = [dict(index=m.index, number=m.number, tick=float(m.tick), ticks=float(m.ticks),
                     timeSignature=list(m.time_sig), actual=list(m.actual), irregular=m.irregular,
                     start=round(tm(m.tick), 6), end=round(tm(m.tick + m.ticks), 6)) for m in score.measures]
    return dict(format='resonance.score-import/1', source=score.source, sha256=score.sha256,
                museScoreVersion=score.format_version, programVersion=score.program_version, reader=score.reader,
                title=score.title, composer=score.composer, division=score.division, parts=score.parts,
                staves=score.staves, measures=measures,
                tempos=[dict(tick=float(t), qpm=round(q * 60, 4), start=round(tm(t), 6)) for t, q in score.tempos],
                timeSignatures=[dict(tick=float(t), numerator=n, denominator=d) for t, n, d in score.sigs],
                pedals=[dict(staff=s, tick=float(t), tick2=float(t2)) for s, t, t2 in score.pedals],
                directions=[dict(tick=float(d.tick), measure=by_index[d.measure].number if d.measure in by_index else None,
                                 staff=d.staff, kind=d.kind, text=d.text) for d in score.directions],
                notes=notes, repairs=[asdict(r) for r in score.repairs], warnings=score.warnings,
                museScore4=score.musescore4,
                summary=dict(notes=len(notes), measures=len(measures), staves=len(score.staves),
                             duration=round(tm(score.measures[-1].tick + score.measures[-1].ticks), 3) if score.measures else 0,
                             repairs=len(score.repairs)))


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    p.add_argument('score', help='.mscz or .mscx')
    p.add_argument('--midi', help='write a type-1 MIDI here')
    p.add_argument('--json', help='write the timing report here')
    p.add_argument('--ppq', type=int, help='MIDI resolution (default: the score\'s division)')
    p.add_argument('--musescore4-copy', help='for a 1.x/2.x score MuseScore 4 refuses: write a copy it can open')
    a = p.parse_args(argv)
    score = read(a.score)
    if a.midi:
        write_midi(score, a.midi, a.ppq)
    if a.musescore4_copy:
        changed = musescore4_copy(a.score, a.musescore4_copy, score.musescore4) if score.musescore4 else 0
        print(f'MuseScore 4 copy: {changed} chord(s) given their own <tick>' if changed else
              'MuseScore 4 copy: nothing to change (no misread measures); no copy written', file=sys.stderr)
    report = timing(score)
    if a.json:
        Path(a.json).write_text(json.dumps(report, indent=1, ensure_ascii=False), encoding='utf-8')
    out = dict(report['summary'], title=score.title, composer=score.composer, version=score.format_version,
               reader=score.reader, repairs=report['repairs'], museScore4=report['museScore4'],
               warnings=score.warnings[:20])
    print(json.dumps(out, indent=1, ensure_ascii=False))


if __name__ == '__main__':
    main()
