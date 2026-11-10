"""MuseScore 3.x and 4.x scores (mscx 3.00-3.02, 4.00-4.70) read by Resonance into a ``ScoreImport``.

``musescore_import.read`` calls :class:`ModernReader` for files of version 3.0 and later; ``musescore_import.py``
reads 1.x/2.x itself and owns the data model, the corruption checks, the MIDI writer and the timing JSON.
Nothing outside this project is needed (MuseScore is not run).

**The format.**  A ``<Score>`` lists its ``<Part>`` elements first (each with its ``<Staff>`` definitions and an
``<Instrument>``), then one ``<Staff id="n">`` per staff holding that staff's ``<Measure>`` elements and frames
(``<VBox>`` with the title texts, ``<HBox>``...).  The first staff's measures make the timeline: a measure is as
long as its time signature unless its ``len="n/d"`` attribute says otherwise (a pickup, a split measure);
``<irregular>`` excludes it from the measure count and ``<noOffset>`` adds to the count.  Inside a measure each
``<voice>`` (at most four; track = staff * 4 + voice) is read with a cursor that starts at the bar line: a chord
or rest sits at the cursor and moves it by its length (duration type and dots, times the ratio of every
enclosing tuplet); ``<location><fractions>`` moves the cursor (a voice that starts later, an annotation between
notes); grace chords take no time and belong to the next chord of their voice.  Tuplets are opened by
``<Tuplet>`` and closed by ``<endTuplet/>``, nested ones inside (the 2.x form, a ``<Tuplet id>`` that chords
name, is accepted too).  Lines are ``<Spanner type="...">`` pairs: the start holds the line's properties and a
``<next><location>`` (relative measures and fractions; for ties also staves, voices, grace and note index) to its
end, which holds a ``<prev><location>`` back; the start alone tells where the line ends.  Repeat barlines
(``<startRepeat/>``, ``<endRepeat>n``), ``<Marker>`` (segno, coda, fine, to coda: in a 4.x ``<markerType>``, in
3.x the ``<label>``) and ``<Jump>`` (D.C., D.S., al Fine, al Coda) belong to the first staff's measures, voltas
are spanners on it.  Pitches are concert pitches, so transposing instruments need nothing; a note under an
ottava line keeps its written pitch and the line's shift is added here, as MuseScore's playback adds it.
Staves linked to another staff (``<linkedTo>``: a tablature or second view of the same music) are skipped.

**How it is played.**  Resonance plays the score the way MuseScore 4 plays it; its MIDI export was the
reference these rules were checked against (``test_musescore_modern.py`` compares a score with it when
MuseScore is installed):

* the timeline is the *played* one: repeats, voltas (1st/2nd endings), D.C./D.S. al Fine/al Coda jumps are
  unrolled after MuseScore's ``RepeatList::unwind``; every played measure is one ``Measure`` with its score
  position (``source``), its pass (``play``: 0 the first time that measure is played, 1 the second...) and its
  printed number; each keeps its own time signature, so a repeat back into another meter stays right;
* measure repeats play the measure(s) they repeat (the last measures actually played, as MuseScore does);
* a tie joins its notes into one note when the tied-to note is played right after the tied-from one (also from
  a grace note into its chord, across voices and across staves); a tie into a measure the music does not go on
  to (across a repeat jump) leaves both notes struck, where MuseScore would sound only the first; a note that
  does not play (``<play>0``) silences the notes tied to it;
* grace notes: acciaccaturas take their time from before the beat (half the chord before them at most),
  appoggiaturas and other grace notes before a chord take theirs from the start of the chord (whose notes then
  start later), grace notes after a chord take the end of it, with MuseScore's proportions;
* articulations shorten notes (staccato 50%, staccatissimo 30%, portato 67%... or the instrument's own values),
  notes outside slurs are played at the instrument's default length (often 95%); accents, marcato and sforzato
  raise the velocity; unisons inside one chord sound once;
* arpeggios roll their chord, tremolos (one-chord and two-chord) are played as repeated or alternating notes,
  notes and grace notes with user-edited play events (piano roll) keep them;
* velocities follow the dynamics (per instrument, staff or voice, as the dynamic is assigned), with hairpins as
  ramps and sf/sfz/fp as an accent that decays;
* the tempo map has the tempo markings (also "a tempo" / "tempo primo"), fermatas (from the chord or rest under
  the fermata to the next one the reader sees, its time stretch slows the tempo; with 'hide empty staves' a
  rest on a hidden staff does not end it), gradual tempo changes (rit., accel. with their easing) and a pickup
  measure takes the first measure's tempo; at a repeat jump the tempo is the score's tempo at the place the
  music jumps to;
* pauses (a breath or caesura with a pause time, the pause after a section break) are time, not ticks: the
  stretch from the last note onset before the pause is slowed until it lasts the pause longer (notes sounding
  into the pause are held through it, as MuseScore holds them), so measures keep their ticks and onsets their
  place.

Not realized as extra notes (the written note is kept and named in ``articulations``): trills and ornaments
(mordent, turn...), glissandos, guitar bends, chord-symbol playback and swing.  MuseScore plays those as extra
notes, pitch bends or shifted onsets.  Ottava subtypes are read by their meaning, so '8va bassa' lowers (MuseScore
4 knows only its own names, '8vb'...).

**Corrupted measures** are checked and repaired voice by voice exactly as for the old formats
(``musescore_import.check_measure``): voice 1 must fill its measure, other voices may stop early, nothing may
cross the bar line; repairs are reported with the printed measure number and never written into the file.

Measure numbers are the printed ones (MuseScore's count: measures excluded from the count share the number of
the measure after them, ``<noOffset>`` adds, a section break may restart at 1); a first measure that is
excluded from the count or shorter than its time signature is a pickup, measure 0.
"""
from __future__ import annotations

import bisect
import math
import re
import struct
import zipfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from fractions import Fraction
from typing import Dict, List, Optional, Tuple

import musescore_import as msi

# --------------------------------------------------------------------------------------------------- tables

# Note values in whole notes (MuseScore's Fraction unit).
DURATIONS = {'long': Fraction(4), 'longa': Fraction(4), 'breve': Fraction(2), 'whole': Fraction(1),
             'half': Fraction(1, 2), 'quarter': Fraction(1, 4), 'eighth': Fraction(1, 8), '16th': Fraction(1, 16),
             '32nd': Fraction(1, 32), '64th': Fraction(1, 64), '128th': Fraction(1, 128),
             '256th': Fraction(1, 256), '512th': Fraction(1, 512), '1024th': Fraction(1, 1024)}
HOOKS = {'eighth': 1, '16th': 2, '32nd': 3, '64th': 4, '128th': 5, '256th': 6, '512th': 7, '1024th': 8}
GRACE_BEFORE = ('acciaccatura', 'appoggiatura', 'grace4', 'grace16', 'grace32')
GRACE_AFTER = ('grace8after', 'grace16after', 'grace32after')
MS_WHOLE = 1920                  # MuseScore's ticks per whole note (its playback arithmetic is in these)
NOTE_LENGTH = 1000               # play events are in thousandths of their chord's length

ANNOTATIONS = {'Tempo', 'Dynamic', 'Expression', 'StaffText', 'SystemText', 'RehearsalMark', 'PlayTechAnnotation',
               'InstrumentChange', 'Harmony', 'Text', 'Sticking', 'FiguredBass', 'FretDiagram', 'Symbol', 'Image',
               'Capo', 'StringTunings', 'HarpPedalDiagram', 'TremoloBar', 'StaffState'}

# Dynamic subtype -> (velocity, change after the accent) as MuseScore 4 plays them.
DYNAMICS = {'pppppp': (1, 0), 'ppppp': (5, 0), 'pppp': (10, 0), 'ppp': (16, 0), 'pp': (33, 0), 'p': (49, 0),
            'mp': (64, 0), 'mf': (80, 0), 'f': (96, 0), 'ff': (112, 0), 'fff': (126, 0), 'ffff': (127, 0),
            'fffff': (127, 0), 'ffffff': (127, 0), 'fp': (96, -47), 'pf': (49, 47), 'sf': (112, -18),
            'sfz': (112, -18), 'sff': (126, -18), 'sffz': (126, -18), 'sfff': (127, -18), 'sfffz': (127, -18),
            'sfp': (112, -47), 'sfpp': (112, -79), 'rfz': (112, -18), 'rf': (112, -18), 'fz': (112, -18),
            'm': (96, -16), 'r': (112, -18), 's': (112, -18), 'z': (80, 0), 'n': (49, -48)}
HAIRPIN_STEP = 16                # a hairpin with no dynamic at its end moves the velocity this much

# Articulation symbols -> the playback name MuseScore looks up in the instrument's articulation table.
_PLAYBACK = {
    'staccatissimo': ('articStaccatissimoAbove', 'articStaccatissimoBelow', 'articStaccatissimoStrokeAbove',
                      'articStaccatissimoStrokeBelow', 'articStaccatissimoWedgeAbove', 'articStaccatissimoWedgeBelow'),
    'staccato': ('articStaccatoAbove', 'articStaccatoBelow', 'tremoloDivisiDots2', 'tremoloDivisiDots3',
                 'tremoloDivisiDots4', 'tremoloDivisiDots6'),
    'sforzatoStaccato': ('articAccentStaccatoAbove', 'articAccentStaccatoBelow'),
    'marcatoStaccato': ('articMarcatoStaccatoAbove', 'articMarcatoStaccatoBelow'),
    'portato': ('articTenutoStaccatoAbove', 'articTenutoStaccatoBelow'),
    'sforzatoTenuto': ('articTenutoAccentAbove', 'articTenutoAccentBelow'),
    'marcatoTenuto': ('articMarcatoTenutoAbove', 'articMarcatoTenutoBelow'),
    'tenuto': ('articTenutoAbove', 'articTenutoBelow'),
    'marcato': ('articMarcatoAbove', 'articMarcatoBelow'),
    'accent': ('articAccentAbove', 'articAccentBelow'),
    'sforzato': ('dynamicSforzato', 'dynamicSforzando'),
    'open': ('brassMuteOpen',), 'closed': ('brassMuteClosed',), 'harmonic': ('stringsHarmonic',),
    'mordent': ('ornamentMordent',),
}
PLAYBACK_NAME = {sym: name for name, syms in _PLAYBACK.items() for sym in syms}
# Built-in (velocity factor, gate %) used when the instrument does not define the articulation itself.
BUILTIN_ARTICULATIONS = {'staccatissimo': (1.0, 30), 'staccato': (1.0, 50), 'portato': (1.0, 67),
                         'tenuto': (1.0, 100), 'accent': (1.2, 100), 'marcato': (1.44, 100),
                         'sforzato': (1.69, 100)}
# Articulation names of 2.x/early 3.x files (read by MuseScore 4 as these symbols).
LEGACY_ARTICULATIONS = {
    'fermata': 'fermataAbove', 'shortfermata': 'fermataShortAbove', 'longfermata': 'fermataLongAbove',
    'verylongfermata': 'fermataVeryLongAbove', 'sforzato': 'articAccentAbove', 'staccato': 'articStaccatoAbove',
    'staccatissimo': 'articStaccatissimoAbove', 'tenuto': 'articTenutoAbove', 'portato': 'articTenutoStaccatoAbove',
    'marcato': 'articMarcatoAbove', 'ouvert': 'brassMuteOpen', 'plusstop': 'brassMuteClosed',
    'upbow': 'stringsUpBow', 'downbow': 'stringsDownBow', 'reverseturn': 'ornamentTurnInverted',
    'turn': 'ornamentTurn', 'trill': 'ornamentTrill', 'prall': 'ornamentShortTrill', 'mordent': 'ornamentMordent',
    'prallprall': 'ornamentTremblement', 'prallmordent': 'ornamentPrallMordent', 'upprall': 'ornamentUpPrall',
    'upmordent': 'ornamentUpMordent', 'downmordent': 'ornamentDownMordent', 'pralldown': 'ornamentPrallDown',
    'prallup': 'ornamentPrallUp', 'lineprall': 'ornamentLinePrall', 'schleifer': 'ornamentPrecompSlide',
    'downprall': 'ornamentPrecompMordentUpperPrefix'}
# Combined articulations that files before 4.10 split into their parts.
SPLIT_ARTICULATIONS = {'articAccentStaccato': ('articStaccato', 'articAccent'),
                       'articTenutoAccent': ('articTenuto', 'articAccent'),
                       'articTenutoStaccato': ('articTenuto', 'articStaccato'),
                       'articMarcatoStaccato': ('articStaccato', 'articMarcato'),
                       'articMarcatoTenuto': ('articTenuto', 'articMarcato')}
# Ornaments MuseScore plays as runs of notes (kept here as the written note, named in its articulations).
ORNAMENTS = {'ornamentTurn', 'ornamentTurnInverted', 'ornamentTurnSlash', 'ornamentTrill', 'brassMuteClosed',
             'ornamentMordent', 'ornamentShortTrill', 'ornamentTremblement', 'ornamentPrallMordent',
             'ornamentLinePrall', 'ornamentUpPrall', 'ornamentUpMordent', 'ornamentPrecompMordentUpperPrefix',
             'ornamentDownMordent', 'ornamentPrallUp', 'ornamentPrallDown', 'ornamentPrecompSlide'}

FERMATA_STRETCH = {'fermataVeryShort': 1.25, 'fermataShort': 1.5, 'fermataShortHenze': 1.5, 'fermataLong': 3.0,
                   'fermataLongHenze': 3.0, 'fermataVeryLong': 4.0}
GRADUAL_FACTORS = {'accelerando': 1.33, 'allargando': 0.75, 'calando': 0.5, 'lentando': 0.75, 'morendo': 0.5,
                   'precipitando': 1.15, 'rallentando': 0.75, 'ritardando': 0.75, 'smorzando': 0.5,
                   'sostenuto': 0.95, 'stringendo': 1.5}
GRADUAL_TEXT = {'accelerando': 'accel.', 'allargando': 'allarg.', 'calando': 'calando', 'lentando': 'lentando',
                'morendo': 'morendo', 'precipitando': 'precipitando', 'rallentando': 'rall.',
                'ritardando': 'rit.', 'smorzando': 'smorz.', 'sostenuto': 'sost.', 'stringendo': 'string.'}
DEFAULT_TEMPO = 2.0              # quarter notes per second (120 bpm) until a tempo marking says otherwise
MIN_TEMPO, MAX_TEMPO = 5.0 / 60, 999.0 / 60

# Markers placed at the end of their measure ('fine', 'coda' = "To Coda" in MuseScore's names; 'codab' is the
# coda sign itself).
RIGHT_MARKERS = {'fine', 'coda', 'tocoda', 'tocodasym', 'dacoda', 'da_coda', 'dadoublecoda', 'da_double_coda',
                 'dadblcoda', 'da_dbl_coda'}
MARKER_LABELS = {'segno': 'segno', 'varsegno': 'varsegno', 'codab': 'codab', 'varcoda': 'varcoda',
                 'codetta': 'codetta', 'fine': 'fine', 'coda': 'coda', 'tocoda': 'coda', 'tocodasym': 'coda'}

SYMBOLS = {'metNoteQuarterUp': '♩', 'metNote8thUp': '♪', 'metNoteHalfUp': '𝅗𝅥', 'metNoteWhole': '𝅝',
           'metNote16thUp': '𝅘𝅥𝅯', 'metAugmentationDot': '.', 'space': ' ', 'segno': '𝄋', 'coda': '𝄌',
           'codaSquare': '𝄌', 'dynamicPiano': 'p', 'dynamicForte': 'f', 'dynamicMezzo': 'm', 'dynamicSforzando': 's',
           'dynamicZ': 'z', 'dynamicRinforzando': 'r', 'dynamicNiente': 'n'}
# The same metronome symbols written as SMuFL characters (MuseScore 4 puts some marks in the text itself).
SMUFL_CHARACTERS = str.maketrans({'': '♩', '': '♪', '': '𝅗𝅥', '': '𝅝', '': '𝅘𝅥𝅯',
                                  '': '.', '': '𝅜', '': '𝅜'})
PLACEHOLDER_TEXTS = {'composer / arranger', 'composer', 'untitled score', 'title', 'subtitle'}


# --------------------------------------------------------------------------------------------------- helpers

def _fraction(text: Optional[str], default: Fraction = Fraction(0)) -> Fraction:
    if text is None or not text.strip():
        return default
    text = text.strip()
    try:
        if '/' in text:
            n, d = text.split('/', 1)
            return Fraction(int(n), int(d)) if int(d) else default
        return Fraction(int(text))
    except ValueError:
        return default


def _int(el: Optional[ET.Element], tag: str, default=0):
    if el is None:
        return default
    v = el.findtext(tag)
    if v is None or not v.strip():
        return default
    v = v.strip().rstrip('%').strip()
    try:
        return int(v)
    except ValueError:
        try:
            return int(float(v))
        except ValueError:
            return default


def _float(el: Optional[ET.Element], tag: str, default: Optional[float] = None) -> Optional[float]:
    if el is None:
        return default
    v = el.findtext(tag)
    try:
        return float(v) if v is not None and v.strip() else default
    except ValueError:
        return default


def _flag(el: Optional[ET.Element], tag: str, default: bool) -> bool:
    """A boolean property: '0'/'false' are false, anything else true, absent is the default."""
    if el is None:
        return default
    v = el.findtext(tag)
    if v is None:
        return default
    return v.strip().lower() not in ('0', 'false')


def _plays(el: Optional[ET.Element]) -> bool:
    return _flag(el, 'play', True)


def _ms(ticks: Fraction, division: int) -> int:
    """MuseScore's integer ticks (480 per quarter) of a length in this score's ticks, rounded like
    Fraction::ticks() (half away from zero)."""
    f = Fraction(ticks) * 480 / division
    sign = -1 if f < 0 else 1
    return sign * ((sign * f.numerator * 2 + f.denominator) // (2 * f.denominator))


def _cdiv(a: int, b: int) -> int:
    """C++ integer division (truncates toward zero)."""
    if b == 0:
        return 0
    q = abs(a) // abs(b)
    return q if (a >= 0) == (b >= 0) else -q


def _f32(x: float) -> float:
    return struct.unpack('f', struct.pack('f', x))[0]


def _plain_text(el: Optional[ET.Element]) -> str:
    """The text of a MuseScore text element (``<text>`` with rich-text tags, ``<sym>`` symbols, ``<br/>``); a
    line's ``<beginText>`` holds its rich text directly."""
    if el is None:
        return ''
    t = el if el.tag in ('text', 'beginText', 'continueText', 'endText') else el.find('text')
    if t is None:
        return ''
    parts: List[str] = []

    def walk(node: ET.Element):
        if node.tag == 'sym':
            parts.append(SYMBOLS.get((node.text or '').strip(), ''))
        elif node.tag == 'br':
            parts.append('\n')
        elif node.text:
            parts.append(node.text)
        for c in node:
            walk(c)
            if c.tail:
                parts.append(c.tail)

    walk(t)
    text = ''.join(parts).replace('\r', '').translate(SMUFL_CHARACTERS)
    return re.sub(r'[ \t]+', ' ', text).strip()


def _location(el: Optional[ET.Element]) -> dict:
    """A relative ``<location>``: staves, voices, measures, fractions (whole notes), grace and note index."""
    out = dict(staves=0, voices=0, measures=0, fractions=Fraction(0), grace=None, notes=0)
    if el is None:
        return out
    for c in el:
        if c.tag == 'fractions':
            out['fractions'] = _fraction(c.text)
        elif c.tag in ('staves', 'voices', 'measures', 'notes', 'grace'):
            try:
                out[c.tag] = int((c.text or '0').strip())
            except ValueError:
                pass
    return out


def _friendly(sym: str) -> str:
    """'articAccentAbove' -> 'accent', 'ornamentTrill' -> 'trill', 'fermataAbove' -> 'fermata'."""
    s = re.sub(r'(Above|Below)$', '', sym)
    for prefix in ('artic', 'ornament'):
        if s.startswith(prefix) and len(s) > len(prefix):
            return s[len(prefix)].lower() + s[len(prefix) + 1:]
    return s


def _ottava_shift(subtype: str) -> int:
    """Semitones an ottava line adds to playback: 8va +12, 8vb -12, 15ma +24, 15mb -24, 22ma/22mb +-36, also
    '8va alta' / '8va bassa' and the numbers of old files (0 = 8va, 1 = 8vb, 2 = 15ma, ...)."""
    s = subtype.strip().lower()
    numeric = {'0': 12, '1': -12, '2': 24, '3': -24, '4': 36, '5': -36}
    if s in numeric:
        return numeric[s]
    m = re.match(r'(8|15|22)\s*(.*)$', s)
    if not m:
        return 0
    size = {'8': 12, '15': 24, '22': 36}[m.group(1)]
    rest = m.group(2)
    down = 'b' in rest.replace('alta', '') or 'bassa' in rest
    return -size if down else size


def _tremolo_lines(subtype: str) -> int:
    s = subtype.strip().lower()
    for key, lines in (('64', 4), ('32', 3), ('16', 2), ('8', 1)):
        if s.endswith(key):
            return lines
    return {'0': 1, '1': 2, '2': 3, '3': 4, '4': 1, '5': 2, '6': 3, '7': 4}.get(s, 0)


def _easing(x: float, method: str) -> float:
    """MuseScore's easing curves for gradual tempo changes."""
    if method == 'ease-in':
        r = 1 - math.sqrt(max(0.0, 1 - x * x))
    elif method == 'ease-out':
        r = math.sqrt(max(0.0, 1 - (x - 1) ** 2))
    elif method == 'ease-in-out':
        r = (1 - math.sqrt(max(0.0, 1 - (2 * x) ** 2))) / 2 if x < 0.5 else (math.sqrt(max(0.0, 1 - (-2 * x + 2) ** 2)) + 1) / 2
    elif method == 'exponential':
        r = x if abs(x - 1.0) < 1e-9 else 1.0 - math.pow(2, -10 * x)
    else:
        r = x
    return _f32(r)


# ------------------------------------------------------------------------------------------- score model

@dataclass
class _Tuplet:
    ident: str
    normal: int
    actual: int
    parent: Optional['_Tuplet'] = None

    def top(self) -> '_Tuplet':
        t = self
        while t.parent is not None:
            t = t.parent
        return t


@dataclass
class _Instrument:
    name: str = ''
    long_name: str = ''
    instrument_id: str = ''
    program: int = 0
    drumset: bool = False
    transpose: int = 0
    articulations: Dict[str, Tuple[int, int]] = field(default_factory=dict)   # name -> (velocity %, gate %)

    def gate(self, gate: int, name: str) -> int:
        """The gate after an articulation: the instrument's own value, else MuseScore's built-in one."""
        if name in self.articulations:
            return min(gate, self.articulations[name][1])
        if name in BUILTIN_ARTICULATIONS:
            return min(gate, BUILTIN_ARTICULATIONS[name][1])
        return gate

    def velocity(self, name: str) -> float:
        if name in self.articulations:
            return self.articulations[name][0] / 100
        if name in BUILTIN_ARTICULATIONS:
            return BUILTIN_ARTICULATIONS[name][0]
        return 1.0

    def default_gate(self) -> int:
        """Gate of notes outside slurs (the instrument's nameless articulation, often 95%)."""
        return self.articulations.get('', (100, 100))[1]


@dataclass
class _StaffDef:
    index: int                 # staff index in the score (0-based), as MuseScore numbers them
    part: int                  # part index in the score
    linked: bool = False
    group: str = 'pitched'
    hide: str = 'auto'         # when empty: 'auto' (with its instrument), 'staff' (on its own), 'never'


@dataclass
class _Measure:
    index: int
    start: Fraction                     # whole notes from the start of the score
    length: Fraction                    # actual length in whole notes
    timesig: Tuple[int, int]            # nominal (as printed)
    actual: Tuple[int, int]             # actual length as written (len="3/8")
    len_attr: bool = False
    excluded: bool = False              # <irregular>: not counted
    no_offset: int = 0
    repeat_start: bool = False
    repeat_end: bool = False
    repeat_count: int = 2
    marks: List[dict] = field(default_factory=list)          # markers and jumps, in file order
    breaks: set = field(default_factory=set)                 # layout break subtypes
    section_pause: Optional[float] = None
    measure_one: bool = True
    number: int = 0

    @property
    def end(self) -> Fraction:
        return self.start + self.length

    @property
    def section_break(self) -> bool:
        return 'section' in self.breaks


@dataclass
class _Frame:
    """A frame (VBox, HBox...) between measures: only its layout breaks matter here."""
    breaks: set = field(default_factory=set)
    section_pause: Optional[float] = None
    measure_one: bool = True

    @property
    def section_break(self) -> bool:
        return 'section' in self.breaks


class ModernReader:
    """MuseScore 3.x / 4.x (see the module docstring)."""

    def __init__(self, data: bytes, version: str):
        self.version = version
        self.number = msi.version_number(version)
        self.root = ET.fromstring(data)
        self.score = self.root.find('Score')
        if self.score is None:
            raise msi.ScoreImportError('no <Score> in the file')
        self.division = _int(self.score, 'Division', 480) or 480
        self.repairs: List[msi.Repair] = []
        self.warnings: List[str] = []
        self.style: Dict[str, str] = {}
        self.instruments: List[_Instrument] = []
        self.part_names: List[str] = []
        self.staff_defs: List[_StaffDef] = []
        self.measures: List[_Measure] = []
        self.bases: List[object] = []               # measures and frames of the first staff, in order
        self.placed: Dict[Tuple[int, int], List[dict]] = {}     # (staff, measure) -> chords, rests (not graces)
        self.repeat_counts: Dict[Tuple[int, int], int] = {}     # (staff, measure) -> measure repeat group position
        self.annotations: List[dict] = []
        self.spanners: List[dict] = []
        self.frame_texts: List[Tuple[str, str]] = []
        self._next_sig: Optional[Tuple[int, int]] = None
        self._tuplets = 0
        self._warned = set()

    # ================================================================================================ entry
    def read(self, source: str, sha: str) -> msi.ScoreImport:
        self.style = self._read_style(source)
        self._read_parts()
        self._read_content()
        if not self.measures:
            raise msi.ScoreImportError('the score has no measures')
        self._number_measures()
        self._check()
        self._index_content()
        self._resolve_spanners()
        self._tempo_map()
        self._velocity_maps()
        self._render()
        played = self._unwind()
        return self._emit(played, source, sha)

    def _warn(self, text: str):
        if text not in self._warned:
            self._warned.add(text)
            self.warnings.append(text)

    def _ticks(self, whole: Fraction) -> Fraction:
        """Whole notes -> this score's ticks (division per quarter note), exactly."""
        return Fraction(whole) * 4 * self.division

    # ============================================================================================ structure
    def _read_style(self, source: str) -> Dict[str, str]:
        style: Dict[str, str] = {}
        inline = self.score.find('Style')
        if inline is not None:
            for c in inline:
                style[c.tag] = (c.text or '').strip()
        try:
            if source and zipfile.is_zipfile(source):
                with zipfile.ZipFile(source) as z:
                    names = [n for n in z.namelist() if n.lower().endswith('.mss') and '/' not in n.strip('/')]
                    if names:
                        root = ET.fromstring(z.read(names[0]))
                        st = root.find('Style')
                        for c in (st if st is not None else []):
                            style.setdefault(c.tag, (c.text or '').strip())
        except (OSError, zipfile.BadZipFile, ET.ParseError):
            pass
        return style

    def _read_parts(self):
        for pi, part in enumerate(self.score.findall('Part')):
            ins_el = part.find('Instrument')
            ins = self._read_instrument(ins_el)
            name = (part.findtext('trackName') or '').strip() or ins.name or ins.long_name or f'Part {pi + 1}'
            staves = part.findall('Staff')
            for st in staves or [None]:
                group = 'pitched'
                stype = st.find('StaffType') if st is not None else None
                if stype is not None:
                    group = stype.get('group', 'pitched')
                linked = st is not None and st.find('linkedTo') is not None
                hide = (st.findtext('hideWhenEmpty') or '').strip().lower() if st is not None else ''
                hide = {'on': 'staff', '1': 'staff', 'off': 'never', '2': 'never'}.get(hide, 'auto')
                if (part.findtext('hideWhenEmpty') or '').strip().lower() == 'off':
                    hide = 'never'
                elif _flag(part, 'hideStavesWhenIndividuallyEmpty', False) and hide == 'auto':
                    hide = 'staff'
                self.staff_defs.append(_StaffDef(index=len(self.staff_defs), part=pi, linked=linked, group=group,
                                                 hide=hide))
            self.instruments.append(ins)
            self.part_names.append(name)
        if not self.staff_defs:
            self._warn('no parts declared: every staff is read as a part of its own')

    def _read_instrument(self, el: Optional[ET.Element]) -> _Instrument:
        ins = _Instrument()
        if el is None:
            return ins
        ins.name = (el.findtext('trackName') or '').strip()
        long_name = el.find('longName')
        ins.long_name = ''.join(long_name.itertext()).strip() if long_name is not None else ''
        ins.instrument_id = (el.findtext('instrumentId') or el.get('id') or '').strip()
        ins.drumset = _int(el, 'useDrumset', 0) != 0
        ins.transpose = _int(el, 'transposeChromatic', 0)
        for a in el.findall('Articulation'):
            ins.articulations[a.get('name', '')] = (_int(a, 'velocity', 100), _int(a, 'gateTime', 100))
        channel = el.find('Channel')
        if channel is not None:
            prog = channel.find('program')
            if prog is not None:
                value = prog.get('value') or (prog.text or '').strip()
                try:
                    ins.program = max(0, min(127, int(value)))
                except (TypeError, ValueError):
                    ins.program = 0
        return ins

    # ============================================================================================ content
    def _read_content(self):
        contents = [el for el in self.score if el.tag == 'Staff']
        by_index: Dict[int, ET.Element] = {}
        for k, el in enumerate(contents):
            sid = (el.get('id') or '').strip()
            index = int(sid) - 1 if sid.isdigit() else k
            by_index.setdefault(index, el)
        if not by_index:
            raise msi.ScoreImportError('the score has no staff content')
        while len(self.staff_defs) < max(by_index) + 1:      # staves without a part declaration
            self.staff_defs.append(_StaffDef(index=len(self.staff_defs), part=len(self.instruments)))
            self.instruments.append(_Instrument())
            self.part_names.append(f'Staff {len(self.staff_defs)}')
        first = min(by_index)
        self._read_staff(first, by_index[first], structure=True)
        for index in sorted(by_index):
            if index == first:
                continue
            if self.staff_defs[index].linked:
                continue
            self._read_staff(index, by_index[index], structure=False)

    def _read_staff(self, staff: int, el: ET.Element, structure: bool):
        mi = 0
        start = Fraction(0)
        for child in el:
            if child.tag == 'Measure':
                if structure:
                    m = self._new_measure(child, mi, start)
                else:
                    if mi >= len(self.measures):
                        self._warn(f'staff {staff + 1} has more measures than the first staff: the extra ones are ignored')
                        break
                    m = self.measures[mi]
                self._read_measure(staff, m, child, structure)
                if structure:
                    if m.length <= 0:
                        self._warn(f'measure {mi + 1} has no length: read as its time signature')
                        m.length = Fraction(*m.timesig)
                        m.actual = m.timesig
                    start = m.end
                    self.bases.append(m)
                mi += 1
            elif structure and child.tag in ('VBox', 'HBox', 'TBox', 'FBox'):
                frame = _Frame()
                for c in child:
                    if c.tag == 'LayoutBreak':
                        self._layout_break(frame, c)
                    elif c.tag == 'Text':
                        style = (c.findtext('style') or c.findtext('subtype') or '').strip().lower()
                        self.frame_texts.append((style, _plain_text(c)))
                self.bases.append(frame)
        if not structure and mi < len(self.measures):
            self._warn(f'staff {staff + 1} has {mi} measures, the first staff {len(self.measures)}')

    def _new_measure(self, el: ET.Element, mi: int, start: Fraction) -> _Measure:
        prev = self.measures[-1] if self.measures else None
        if self._next_sig is not None:
            sig = self._next_sig
            self._next_sig = None
        elif prev is not None:
            sig = prev.timesig
        else:
            sig = (4, 4)
        m = _Measure(index=mi, start=start, length=Fraction(*sig), timesig=sig, actual=sig)
        length = (el.get('len') or '').strip()
        if length:
            try:
                n, d = (int(x) for x in length.split('/'))
                if n > 0 and d > 0:
                    m.length, m.actual, m.len_attr = Fraction(n, d), (n, d), True
            except ValueError:
                self._warn(f'measure {mi + 1}: unreadable length {length!r}')
        self.measures.append(m)
        return m

    def _layout_break(self, owner, el: ET.Element):
        sub = (el.findtext('subtype') or '').strip()
        owner.breaks.add(sub)
        if sub == 'section':
            owner.section_pause = _float(el, 'pause')
            owner.measure_one = _flag(el, 'startWithMeasureOne', True)

    def _read_measure(self, staff: int, m: _Measure, el: ET.Element, structure: bool):
        voice = 0
        has_voices = el.find('voice') is not None
        for child in el:
            tag = child.tag
            if tag == 'voice':
                if voice < 4:
                    self._read_voice(staff, voice, m, child, structure)
                else:
                    self._warn(f'staff {staff + 1}: a measure with more than four voices (the extra ones are ignored)')
                voice += 1
            elif tag == 'measureRepeatCount':
                self.repeat_counts[(staff, m.index)] = _int(el, 'measureRepeatCount', 0)
            elif not structure:
                continue
            elif tag == 'irregular':
                m.excluded = (child.text or '1').strip().lower() not in ('0', 'false')
            elif tag == 'noOffset':
                m.no_offset = _int(el, 'noOffset', 0)
            elif tag == 'startRepeat':
                m.repeat_start = True
            elif tag == 'endRepeat':
                m.repeat_end = True
                m.repeat_count = max(1, _int(el, 'endRepeat', 2) or 2)
            elif tag in ('Marker', 'Jump'):
                m.marks.append(self._mark(child))
            elif tag == 'LayoutBreak':
                self._layout_break(m, child)
        if not has_voices and any(c.tag in ('Chord', 'Rest') for c in el):
            self._read_voice(staff, 0, m, el, structure)        # a measure written without <voice>

    def _mark(self, el: ET.Element) -> dict:
        if el.tag == 'Jump':
            return dict(kind='jump', jump_to=(el.findtext('jumpTo') or '').strip(),
                        play_until=(el.findtext('playUntil') or '').strip(),
                        continue_at=(el.findtext('continueAt') or '').strip(),
                        play_repeats=_flag(el, 'playRepeats', False), text=_plain_text(el))
        label = (el.findtext('label') or '').strip()
        mtype = (el.findtext('markerType') or el.findtext('subtype') or label).strip()
        return dict(kind='marker', label=label or MARKER_LABELS.get(mtype.lower(), ''), type=mtype,
                    right=mtype.lower() in RIGHT_MARKERS, text=_plain_text(el) or mtype)

    def _read_voice(self, staff: int, voice: int, m: _Measure, el: ET.Element, structure: bool):
        track = staff * 4 + voice
        cursor = Fraction(0)
        moved = False
        tuplet: Optional[_Tuplet] = None
        by_id: Dict[str, _Tuplet] = {}
        graces: List[dict] = []
        fermata: Optional[ET.Element] = None
        placed = self.placed.setdefault((staff, m.index), [])
        for child in el:
            tag = child.tag
            if tag == 'location':
                cursor += _fraction(child.findtext('fractions'))
                moved = True
            elif tag == 'tick':                       # 3.0x: an absolute tick in the file's division
                try:
                    cursor = Fraction(int((child.text or '0').strip()), 4 * self.division) - m.start
                    moved = True
                except ValueError:
                    pass
            elif tag in ('Chord', 'Rest', 'MeasureRepeat', 'RepeatMeasure'):
                item = self._read_chordrest(child, staff, track, m, cursor, tuplet, by_id, moved)
                moved = False
                if item['grace']:
                    graces.append(item)
                    continue
                item['graces'] = graces
                graces = []
                if fermata is not None:
                    self._annotate('Fermata', fermata, staff, track, m, cursor)
                    fermata = None
                placed.append(item)
                cursor += item['length']
            elif tag == 'Tuplet':
                self._tuplets += 1
                t = _Tuplet(f't{self._tuplets}', max(1, _int(child, 'normalNotes', 1)),
                            max(1, _int(child, 'actualNotes', 1)), parent=tuplet)
                if child.get('id') is not None:       # the 2.x form: chords name the tuplet by id
                    t.parent = None
                    by_id[child.get('id')] = t
                else:
                    tuplet = t
            elif tag == 'endTuplet':
                if tuplet is not None:
                    tuplet = tuplet.parent
            elif tag == 'Spanner':
                self._spanner(child, staff, track, m.index, cursor, anchor='voice')
            elif tag == 'TimeSig':
                if structure:
                    self._time_sig(child, m, cursor)
            elif tag == 'Fermata':
                fermata = child
            elif tag == 'BarLine':
                if fermata is not None:
                    self._annotate('Fermata', fermata, staff, track, m, cursor, barline=True)
                    fermata = None
            elif tag == 'Breath':
                self._annotate('Breath', child, staff, track, m, cursor)
            elif tag in ('Marker', 'Jump'):
                if structure:
                    m.marks.append(self._mark(child))
            elif tag in ANNOTATIONS:
                self._annotate(tag, child, staff, track, m, cursor)
        if graces:
            self._warn(f'staff {staff + 1}, measure {m.index + 1}: grace notes with no chord after them are ignored')
        if fermata is not None:
            self._annotate('Fermata', fermata, staff, track, m, cursor, barline=cursor == m.length)

    def _time_sig(self, el: ET.Element, m: _Measure, cursor: Fraction):
        n, d = _int(el, 'sigN', 0) or _int(el, 'nom1', 4) or 4, _int(el, 'sigD', 0) or _int(el, 'den', 4) or 4
        sn, sd = _int(el, 'stretchN', 1) or 1, _int(el, 'stretchD', 1) or 1
        if (sn, sd) != (1, 1):
            n, d = n * sd, d * sn
        if cursor == 0:
            m.timesig = (n, d)
            if not m.len_attr:
                m.length, m.actual = Fraction(n, d), (n, d)
        elif cursor > 0:
            self._next_sig = (n, d)

    def _annotate(self, tag: str, el: ET.Element, staff: int, track: int, m: _Measure, rtick: Fraction, **extra):
        self.annotations.append(dict(tag=tag, el=el, staff=staff, track=track, mi=m.index, rtick=rtick,
                                     tick=self._ticks(m.start + rtick), order=len(self.annotations), **extra))

    def _spanner(self, el: ET.Element, staff: int, track: int, mi: int, rtick: Fraction, anchor: str):
        nxt = el.find('next')
        if nxt is None:
            return                                  # the end of a line: its start says where it ends
        stype = el.get('type', '')
        body = el.find(stype)
        self.spanners.append(dict(type=stype, body=body if body is not None else ET.Element(stype), staff=staff,
                                  track=track, mi=mi, rtick=rtick, rel=_location(nxt.find('location')),
                                  anchor=anchor, seq=len(self.spanners)))

    def _read_chordrest(self, el: ET.Element, staff: int, track: int, m: _Measure, cursor: Fraction,
                        tuplet: Optional[_Tuplet], by_id: Dict[str, _Tuplet], moved: bool) -> dict:
        tag = el.tag
        kind = 'chord' if tag == 'Chord' else ('repeat' if tag in ('MeasureRepeat', 'RepeatMeasure') else 'rest')
        dtype = (el.findtext('durationType') or ('quarter' if kind == 'chord' else 'measure')).strip()
        dots = _int(el, 'dots', 0) or 0
        explicit = el.findtext('duration')
        if dtype == 'measure' or kind == 'repeat':
            written = _fraction(explicit, m.length) if explicit else m.length
        else:
            base = DURATIONS.get(dtype)
            if base is None:
                self._warn(f'unknown duration type {dtype!r}: read as a quarter note')
                base = Fraction(1, 4)
            written = base * (2 - Fraction(1, 2 ** dots)) if dots else base
            if explicit:
                written = _fraction(explicit, written)
        grace = None
        if kind == 'chord':
            grace = next((g for g in GRACE_BEFORE + GRACE_AFTER if el.find(g) is not None), None)
        t = tuplet
        ref = (el.findtext('Tuplet') or '').strip()
        if ref and ref in by_id:
            t = by_id[ref]
        if grace is not None:
            t = None                                # grace chords stand outside tuplets
        length = written
        f = t
        while f is not None:
            length = length * f.normal / f.actual
            f = f.parent
        item = dict(kind=kind, element=el, staff=staff, track=track, measure=m.index, rtick=cursor,
                    written=written, length=length, dtype=dtype, dots=dots, grace=grace is not None,
                    grace_type=grace, tuplet=t.top().ident if t is not None else None, explicit=moved,
                    rest=kind != 'chord', tick=self._ticks(m.start + cursor), ticks=self._ticks(length),
                    graces=[])
        if kind == 'repeat':
            item['repeat_measures'] = max(1, _int(el, 'subtype', 1)) if tag == 'MeasureRepeat' else 1
            self.repeat_counts.setdefault((staff, m.index), 1)
        if kind == 'chord':
            item.update(self._read_chord(el, staff, track, m, cursor))
        return item

    def _read_chord(self, el: ET.Element, staff: int, track: int, m: _Measure, cursor: Fraction) -> dict:
        notes, arts = [], []
        arpeggio = tremolo = None
        for c in el:
            tag = c.tag
            if tag == 'Note':
                note = self._read_note(c)
                if note is not None:
                    notes.append(note)
            elif tag in ('Articulation', 'Ornament'):
                sym = (c.findtext('subtype') or '').strip()
                if self.number < 4.1 and not sym.startswith(('artic', 'ornament', 'brass', 'strings', 'fermata',
                                                             'guitar', 'wiggle', 'plucked', 'lute', 'tremolo',
                                                             'dynamic', 'handbells')):
                    sym = LEGACY_ARTICULATIONS.get(sym, sym)
                arts.append(dict(sym=sym, play=_plays(c), ornament=tag == 'Ornament' or sym in ORNAMENTS))
            elif tag == 'Arpeggio':
                arpeggio = dict(subtype=(c.findtext('subtype') or '0').strip(), play=_plays(c),
                                stretch=_float(c, 'timeStretch', None) or _float(c, 'stretch', 1.0) or 1.0)
            elif tag in ('Tremolo', 'TremoloSingleChord', 'TremoloTwoChord'):
                sub = (c.findtext('subtype') or '').strip()
                two = tag == 'TremoloTwoChord' or (tag == 'Tremolo' and sub.lower().startswith('c'))
                tremolo = dict(subtype=sub, two=two, lines=_tremolo_lines(sub), buzz='buzz' in sub.lower())
            elif tag == 'Spanner':
                self._spanner(c, staff, track, m.index, cursor, anchor='chord')
        notes.sort(key=lambda n: n['pitch'])
        if self.number < 4.1:                       # combined articulations of older files are played as parts
            split = []
            for a in arts:
                base = re.sub(r'(Above|Below)$', '', a['sym'])
                if base in SPLIT_ARTICULATIONS:
                    suffix = a['sym'][len(base):]
                    split.extend(dict(a, sym=part + suffix) for part in SPLIT_ARTICULATIONS[base])
                else:
                    split.append(a)
            arts = split
        return dict(notes=notes, articulations=arts, arpeggio=arpeggio, tremolo=tremolo,
                    user_events=any(n['events'] is not None for n in notes))

    def _read_note(self, el: ET.Element) -> Optional[dict]:
        pitch = _int(el, 'pitch', None)
        if pitch is None:
            return None
        note = dict(pitch=max(0, min(127, pitch)), tpc=_int(el, 'tpc', None), play=_flag(el, 'play', True),
                    velocity=0, velo_type=(el.findtext('veloType') or '').strip().lower(), events=None,
                    tie=None, old_tie=False, lines=[])
        velocity = _int(el, 'velocity', 0)
        if velocity and (self.number >= 4.0 or note['velo_type'] == 'user'):
            note['velocity'] = velocity
        for c in el:
            if c.tag == 'Spanner':
                stype = c.get('type', '')
                nxt = c.find('next')
                if stype == 'Tie':
                    if nxt is not None:
                        note['tie'] = _location(nxt.find('location'))
                elif nxt is not None and stype:
                    note['lines'].append(stype)
            elif c.tag == 'Tie':
                note['old_tie'] = True
            elif c.tag == 'Events':
                note['events'] = [(_int(ev, 'pitch', 0), _int(ev, 'ontime', 0), _int(ev, 'len', NOTE_LENGTH))
                                  for ev in c.findall('Event')]
        return note

    # ======================================================================================= measure numbers
    def _number_measures(self):
        """MuseScore's count: ``no += noOffset``; the measure shows ``no + 1``; a measure excluded from the count
        does not advance it; a section break may restart it.  The first measure of the score (or of a section
        that restarts the count) is a pickup, number 0, when it is excluded or shorter than its time signature."""
        no = 0
        first = True
        for base in self.bases:
            if isinstance(base, _Measure):
                m = base
                no += m.no_offset
                if first and (m.excluded or m.length < Fraction(*m.timesig)):
                    m.number = 0
                else:
                    m.number = no + 1
                    if not m.excluded:
                        no += 1
                first = False
            if base.section_break and base.measure_one:
                no = 0
                first = True
        self.score_measures = [
            msi.Measure(index=m.index, number=m.number, tick=self._ticks(m.start), ticks=self._ticks(m.length),
                        time_sig=m.timesig, actual=m.actual,
                        irregular=m.excluded or Fraction(*m.actual) != Fraction(*m.timesig))
            for m in self.measures]
        self.total = self.measures[-1].end

    # ================================================================================= corruption and repair
    def _check(self):
        self.out_staff: Dict[int, int] = {}
        for sd in self.staff_defs:
            if not sd.linked:
                self.out_staff[sd.index] = len(self.out_staff)
        for (staff, mi), items in sorted(self.placed.items()):
            if items and staff in self.out_staff:
                msi.check_measure(items, self.score_measures[mi], self.out_staff[staff], self.division, self._repair)

    def _repair(self, measure, staff: int, voice: int, kind: str, found: str, expected: str, action: str):
        self.repairs.append(msi.Repair(measure=measure.number, index=measure.index, staff=staff + 1, voice=voice + 1,
                                       kind=kind, found=found, expected=expected, action=action))

    # =================================================================================== content indexes
    def _start_of(self, mi: int) -> Fraction:
        if mi < 0:
            return Fraction(0)
        if mi >= len(self.measures):
            return self.total
        return self.measures[mi].start

    def _index_content(self):
        """Positions as read (whole notes) for ties, the chords of every track in order, the segment ticks
        (every chord or rest start, all staves) MuseScore's playback rules look at."""
        self.notes_by_key: Dict[tuple, List[Tuple[dict, int]]] = {}     # -> [(note, index in its chord)]
        self.track_items: Dict[int, List[dict]] = {}
        segs: Dict[int, set] = {}
        for (staff, mi), items in self.placed.items():
            if staff not in self.out_staff:
                continue
            m = self.measures[mi]
            for it in items:
                it['pos'] = m.start + it['rtick']
                segs.setdefault(mi, set()).add(it['tick'])
                self.track_items.setdefault(it['track'], []).append(it)
                if it['kind'] != 'chord':
                    continue
                for gi, g in enumerate(it['graces']):
                    g['pos'] = it['pos']
                    g['parent'] = it
                    g['grace_index'] = gi
                    for k, n in enumerate(g['notes']):
                        self.notes_by_key.setdefault((g['track'], it['pos'], gi, n['pitch']), []).append((n, k))
                for k, n in enumerate(it['notes']):
                    self.notes_by_key.setdefault((it['track'], it['pos'], None, n['pitch']), []).append((n, k))
        for items in self.track_items.values():
            items.sort(key=lambda it: it['tick'])
        ticks = {self._ticks(m.start) for m in self.measures}
        for s in segs.values():
            ticks |= s
        ticks |= {a['tick'] for a in self.annotations if a['tag'] == 'Breath'}
        self.segment_ticks = sorted(ticks)
        self.measure_segments = {mi: sorted(s) for mi, s in segs.items()}

    # ============================================================================================ spanners
    def _end_tick(self, sp: dict) -> Fraction:
        rel = sp['rel']
        return self._ticks(self._start_of(sp['mi'] + rel['measures']) + sp['rtick'] + rel['fractions'])

    def _resolve_spanners(self):
        self.slurs: Dict[int, Tuple[List[Fraction], List[Fraction]]] = {}
        self.pedals: List[Tuple[int, Fraction, Fraction]] = []
        self.ottavas: Dict[int, List[Tuple[Fraction, int]]] = {}
        self.hairpins: List[dict] = []
        self.trills: Dict[int, List[Tuple[Fraction, Fraction]]] = {}
        self.gradual: List[dict] = []
        voltas: List[dict] = []
        slurs: Dict[int, List[Tuple[Fraction, Fraction]]] = {}
        marks: Dict[int, List[Tuple[Fraction, int]]] = {}
        for sp in sorted(self.spanners, key=lambda s: (self._ticks(self._start_of(s['mi']) + s['rtick']), s['seq'])):
            tick = self._ticks(self._start_of(sp['mi']) + sp['rtick'])
            tick2 = self._end_tick(sp)
            sp.update(tick=tick, tick2=tick2)
            body, stype, staff = sp['body'], sp['type'], sp['staff']
            if stype == 'Volta':
                voltas.append(sp)
                continue
            if staff not in self.out_staff:
                continue
            if stype == 'Slur':
                slurs.setdefault(staff, []).append((min(tick, tick2), max(tick, tick2)))
            elif stype == 'Pedal':
                if tick2 > tick:
                    if self.number < 4.4 and (body.findtext('endHookType') or '').strip() == '2':
                        tick2 = self._chordrest_ending_before(tick2, staff) or tick2
                    if tick2 > tick:
                        self.pedals.append((staff, tick, tick2))
            elif stype == 'Ottava':
                if _plays(body) and tick2 > tick:
                    shift = _ottava_shift(body.findtext('subtype') or '8va')
                    marks.setdefault(staff, []).extend([(tick, shift), (tick2, 0)])
            elif stype == 'HairPin':
                self.hairpins.append(sp)
            elif stype == 'Trill':
                if _plays(body):
                    self.trills.setdefault(sp['track'], []).append((tick, tick2))
            elif stype == 'GradualTempoChange':
                self.gradual.append(sp)
        for staff, spans in slurs.items():
            spans.sort()
            starts, ends, running = [], [], None
            for a, b in spans:
                running = b if running is None or b > running else running
                starts.append(a)
                ends.append(running)
            self.slurs[staff] = (starts, ends)
        for staff, rows in marks.items():
            table: Dict[Fraction, int] = {}
            for t, shift in rows:                   # a later line overwrites, as in MuseScore's pitch offsets
                table[t] = shift
            self.ottavas[staff] = sorted(table.items())
        # voltas are system lines: a copy on another staff is the same volta
        seen = set()
        self.voltas = []
        for v in voltas:
            key = (v['tick'], v['tick2'], (v['body'].findtext('endings') or '').strip())
            if key not in seen:
                seen.add(key)
                self.voltas.append(v)
        self._resolve_ties()

    def _chordrest_ending_before(self, tick: Fraction, staff: int) -> Optional[Fraction]:
        """Start of the chord or rest of the staff whose end is the latest at or before ``tick`` (MuseScore moves
        the end of an angled pedal line back to it in files before 4.4)."""
        best, best_end = None, Fraction(-1)
        eps = Fraction(self.division, 480)
        for (st, mi), items in self.placed.items():
            if st != staff:
                continue
            for it in items:
                if it['tick'] > tick - eps:
                    continue
                end = it['tick'] + it['ticks']
                if best_end <= end <= tick:
                    best, best_end = it['tick'], end
        return best

    def in_slur(self, staff: int, tick: Fraction) -> bool:
        idx = self.slurs.get(staff)
        if not idx:
            return False
        starts, ends = idx
        i = bisect.bisect_right(starts, tick) - 1
        return i >= 0 and ends[i] > tick

    def ottava(self, staff: int, tick: Fraction) -> int:
        rows = self.ottavas.get(staff)
        if not rows:
            return 0
        i = bisect.bisect_right([t for t, _ in rows], tick) - 1
        return rows[i][1] if i >= 0 else 0

    def trill_on(self, track: int, tick: Fraction, ticks: Fraction) -> bool:
        eps = Fraction(self.division, 480)
        return any(a <= tick + ticks - eps and b >= tick + eps for a, b in self.trills.get(track, []))

    def _resolve_ties(self):
        """Each tie start names its end note: the start's location (relative staff, voice, measure, fraction,
        grace index) gives the end position; the end is the note of the same pitch there."""
        lost = 0
        for (staff, mi), items in self.placed.items():
            if staff not in self.out_staff:
                continue
            m = self.measures[mi]
            for it in items:
                if it['kind'] != 'chord':
                    continue
                for chord in it['graces'] + [it]:
                    for index, n in enumerate(chord['notes']):
                        n.setdefault('tied_from', None)
                        n['tie_to'] = None
                        if n['tie'] is not None:
                            rel = n['tie']
                            voice = chord['track'] % 4 + rel['voices']
                            track = (chord['track'] // 4 + rel['staves']) * 4 + voice
                            pos = self._start_of(mi + rel['measures']) + (chord['pos'] - m.start) + rel['fractions']
                            found = self.notes_by_key.get((track, pos, rel['grace'], n['pitch']), [])
                            expected = (0 if len(chord['notes']) == 1 else index) + rel['notes']
                        elif n['old_tie'] and not chord['grace']:
                            found = self.notes_by_key.get((chord['track'], chord['pos'] + chord['length'], None,
                                                           n['pitch']), [])
                            expected = None
                        else:
                            continue
                        # unisons in one chord: the note index of the location tells them apart
                        found = [c for c in found if c[0] is not n]
                        target = next((c[0] for c in found if c[1] == expected), None) or \
                            next((c[0] for c in found if c[0].get('tied_from') is None), None) or \
                            (found[0][0] if found else None)
                        if target is None:
                            lost += 1
                            continue
                        n['tie_to'] = target
                        target['tied_from'] = n
        if lost:
            self._warn(f'{lost} tie(s) found no note to end on (their notes are kept apart)')

    # ============================================================================================ tempo map
    def _tempo_map(self):
        """The score's tempo map in score ticks, by MuseScore's rules (tempo markings, a tempo / tempo primo,
        fermatas, gradual tempo changes, a pickup taking the next measure's tempo), and its pauses."""
        tempo: Dict[Fraction, float] = {Fraction(0): DEFAULT_TEMPO}
        keys: List[Fraction] = [Fraction(0)]

        def at(t: Fraction) -> float:
            i = bisect.bisect_right(keys, t) - 1
            return tempo[keys[i]] if i >= 0 else DEFAULT_TEMPO

        def put(t: Fraction, v: float):
            if t not in tempo:
                bisect.insort(keys, t)
            tempo[t] = v

        one = Fraction(self.division, 480)          # one MuseScore tick
        active = self._active_segments()
        primo: Optional[float] = None
        by_tick: Dict[Fraction, List[dict]] = {}
        for a in self.annotations:
            if a['tag'] in ('Tempo', 'Fermata') and a['staff'] in self.out_staff:
                by_tick.setdefault(a['tick'], []).append(a)
        for t in sorted(by_tick):
            stretch = 0.0
            for a in sorted(by_tick[t], key=lambda a: a['order']):
                el = a['el']
                if a['tag'] == 'Fermata':
                    if not a.get('barline') and _plays(el):
                        stretch = max(stretch, self._fermata_stretch(el))
                    continue
                if not _plays(el):
                    continue
                value = min(max(_float(el, 'tempo', DEFAULT_TEMPO) or DEFAULT_TEMPO, MIN_TEMPO), MAX_TEMPO)
                ttype = (el.findtext('type') or 'normal').strip()
                follow = _flag(el, 'followText', False)
                if ttype not in ('aTempo', 'tempoPrimo') and primo is None:
                    primo = value
                if ttype == 'aTempo' and follow:
                    put(t, at(t))
                elif ttype == 'tempoPrimo' and follow:
                    put(t, primo if primo is not None else DEFAULT_TEMPO)
                else:
                    put(t, value)
            if stretch not in (0.0, 1.0):
                before = at(t)
                put(t, before / stretch)
                i = bisect.bisect_right(active, t)
                if i < len(active):
                    end = active[i] - one
                    if end > t and end not in tempo:
                        put(end, before)
        for sp in sorted(self.gradual, key=lambda s: (s['tick'], s['seq'])):
            body = sp['body']
            if not _plays(body) or sp['tick2'] <= sp['tick']:
                continue
            cur = at(sp['tick'])
            ctype = (body.findtext('tempoChangeType') or 'undefined').strip()
            factor = _float(body, 'tempoChangeFactor', None)
            factor = _f32(factor) if factor is not None else GRADUAL_FACTORS.get(ctype, 1.0)
            total = _ms(sp['tick2'] - sp['tick'], self.division)
            steps = max(8, int(total / 480))
            method = (body.findtext('tempoEasingMethod') or 'normal').strip()
            step = _f32(_f32(float(total)) / _f32(float(steps)))
            done = set()
            for i in range(steps + 1):
                off = int(_f32(i * step))
                if off in done:
                    continue
                done.add(off)
                x = _f32(_f32(float(i)) / _f32(float(steps)))
                t = sp['tick'] + Fraction(off * self.division, 480)
                if t not in tempo:
                    put(t, cur + _easing(x, method) * (cur * factor - cur))
        # a pickup with no tempo marking of its own takes the next measure's
        for k, base in enumerate(self.bases):
            if not isinstance(base, _Measure) or base.index + 1 >= len(self.measures):
                continue
            prev = self.bases[k - 1] if k else None
            starts = base.excluded or prev is None or not isinstance(prev, _Measure) or \
                bool(prev.breaks & {'line', 'page', 'section'})
            if not (starts and base.length < Fraction(*base.timesig)):
                continue
            own = [a for a in self.annotations if a['tag'] == 'Tempo' and a['mi'] == base.index and _plays(a['el'])]
            nxt = sorted((a for a in self.annotations if a['tag'] == 'Tempo' and a['mi'] == base.index + 1
                          and _plays(a['el'])), key=lambda a: (a['tick'], a['order']))
            if not own and nxt:
                value = _float(nxt[0]['el'], 'tempo', DEFAULT_TEMPO) or DEFAULT_TEMPO
                put(self._ticks(base.start), min(max(value, MIN_TEMPO), MAX_TEMPO))
        self.score_tempo = [(t, tempo[t]) for t in keys]
        self._tempo_at = at
        # pauses: breath marks and caesuras with a pause time, the pause after a section break
        pauses: Dict[Fraction, float] = {}
        for a in self.annotations:
            if a['tag'] == 'Breath' and a['staff'] in self.out_staff:
                p = _float(a['el'], 'pause', 0.0) or 0.0
                if p > 0:
                    pauses[a['tick']] = max(pauses.get(a['tick'], 0.0), p)
        try:
            default_pause = float(self.style.get('sectionPause', '3') or 3)
        except ValueError:
            default_pause = 3.0
        end = Fraction(0)
        for base in self.bases:
            if isinstance(base, _Measure):
                end = self._ticks(base.end)
            if base.section_break and end > 0:
                p = base.section_pause if base.section_pause is not None else default_pause
                if p > 0:
                    pauses[end] = max(pauses.get(end, 0.0), p)
        self.score_pauses = sorted(pauses.items())

    def _active_segments(self) -> List[Fraction]:
        """The segment ticks MuseScore's fermata looks at for its end: every chord or rest start, but with 'hide
        empty staves' only those of staves shown in their measure (MuseScore lays each system out first; here
        each measure stands for its system), plus breath marks and measure starts."""
        if self.style.get('hideEmptyStaves', '0').strip() != '1':
            return self.segment_ticks
        keep_first = self.style.get('dontHideStavesInFirstSystem', '1').strip() == '1'
        system = {'Tempo', 'RehearsalMark', 'SystemText', 'Fermata', 'Breath'}
        marked = set()
        for a in self.annotations:
            if a['tag'] not in system and _flag(a['el'], 'visible', True):
                marked.add((a['staff'], a['mi']))
        starts = [self._ticks(m.start) for m in self.measures]
        crossed = set()                             # (staff, measure) a line runs through
        for sp in self.spanners:
            if 'tick' not in sp or sp['type'] == 'Volta' or sp['tick2'] < sp['tick']:
                continue
            first = max(0, bisect.bisect_right(starts, sp['tick']) - 1)
            last = max(0, bisect.bisect_right(starts, sp['tick2']) - 1)
            if last > first and starts[last] == sp['tick2'] and sp['type'] != 'Slur':
                last -= 1                           # a line ending at a bar line does not reach into the measure
            crossed.update((sp['staff'], mi) for mi in range(first, last + 1))
        parts: Dict[int, List[int]] = {}
        for sd in self.staff_defs:
            if sd.index in self.out_staff:
                parts.setdefault(sd.part, []).append(sd.index)
        ticks = {self._ticks(m.start) for m in self.measures}
        ticks |= {a['tick'] for a in self.annotations if a['tag'] == 'Breath'}
        for mi, m in enumerate(self.measures):
            staves = list(self.out_staff)
            if not (mi == 0 and keep_first):
                empty = {st: (st, mi) not in marked and
                         not any(it['kind'] == 'chord' for it in self.placed.get((st, mi), []))
                         for st in staves}
                shown = []
                for st in staves:
                    mode = self.staff_defs[st].hide
                    part_empty = all(empty[o] for o in parts.get(self.staff_defs[st].part, [st]))
                    if mode == 'never' or not empty[st] or (st, mi) in crossed or (mode != 'staff' and not part_empty):
                        shown.append(st)
                staves = shown or staves[:1]
            for st in staves:
                ticks.update(it['tick'] for it in self.placed.get((st, mi), []))
        return sorted(ticks)

    @staticmethod
    def _fermata_stretch(el: ET.Element) -> float:
        stretch = _float(el, 'timeStretch', None)
        if stretch is not None:
            return stretch
        sub = re.sub(r'(Above|Below)$', '', (el.findtext('subtype') or 'fermataAbove').strip())
        return FERMATA_STRETCH.get(sub, 2.0)

    # ========================================================================================== velocities
    def _velocity_maps(self):
        """Per track: dynamics (a level from their tick on; an accent dynamic decays to its second level) and
        hairpins (a ramp to the dynamic at their end, or a step of 16 when none is there), as MuseScore 4 plays
        them.  A dynamic applies to the whole instrument unless it is assigned to its staff or voice."""
        part_staves: Dict[int, List[int]] = {}
        for sd in self.staff_defs:
            if not sd.linked:
                part_staves.setdefault(sd.part, []).append(sd.index)
        events: Dict[int, List[tuple]] = {}

        def targets(el: ET.Element, staff: int, track: int) -> List[int]:
            va = (el.findtext('voiceAssignment') or '').strip()
            if not va and self.number < 4.4:
                if track % 4:
                    va = 'currentVoiceOnly'
                elif (el.findtext('dynType') or '').strip() == '0':
                    va = 'allInStaff'
            if va == 'currentVoiceOnly':
                return [track]
            if va == 'allInStaff':
                return [staff * 4 + v for v in range(4)]
            part = self.staff_defs[staff].part
            return [s * 4 + v for s in part_staves.get(part, [staff]) for v in range(4)]

        for a in self.annotations:
            if a['tag'] != 'Dynamic' or a['staff'] not in self.out_staff:
                continue
            el = a['el']
            sub = (el.findtext('subtype') or '').strip()
            base, change = DYNAMICS.get(sub, (-1, 0))
            v = _int(el, 'velocity', 0)
            if v > 0:
                base = v
            if el.find('veloChange') is not None:
                change = _int(el, 'veloChange', change)
            if base < 1:
                continue
            base = max(1, min(127, base))
            length = Fraction(0)
            if change:
                speed = {'slow': 1.3, 'fast': 0.5}.get((el.findtext('veloChangeSpeed') or 'normal').strip().lower(), 0.8)
                ratio = self._tempo_at(a['tick']) / DEFAULT_TEMPO
                length = Fraction(int(ratio * speed * 480) * self.division, 480)
            for tr in targets(el, a['staff'], a['track']):
                events.setdefault(tr, []).append((a['tick'], 0, base, change, length))
        for sp in self.hairpins:
            body = sp['body']
            if not _plays(body) or sp['tick2'] <= sp['tick']:
                continue
            change = abs(_int(body, 'veloChange', 0))
            sub = (body.findtext('subtype') or '0').strip()
            down = sub in ('1', '3') or 'decresc' in sub.lower() or 'dim' in sub.lower()
            for tr in targets(body, sp['staff'], sp['track']):
                events.setdefault(tr, []).append((sp['tick'], 1, change, down, sp['tick2'] - sp['tick']))
        self.levels: Dict[int, Tuple[List[tuple], List[Fraction]]] = {}
        for tr, evs in events.items():
            evs.sort(key=lambda e: (e[0], e[1]))
            dyn_ticks = sorted({e[0] for e in evs if e[1] == 0})
            dyn_values = {e[0]: e[2] for e in evs if e[1] == 0}
            rows: List[tuple] = []          # (start, end, value at start, value at end); a point has end == start
            level = 80
            for e in evs:
                tick = e[0]
                if e[1] == 0:
                    _, _, base, change, length = e
                    rows.append((tick, tick, base, base))
                    level = base
                    if change and length > 0:
                        level = max(1, min(127, base + change))
                        rows.append((tick, tick + length, base, level))
                    continue
                _, _, change, down, length = e
                end = tick + length
                k = bisect.bisect_right(dyn_ticks, tick)
                if k < len(dyn_ticks) and dyn_ticks[k] < end:     # a dynamic inside the hairpin ends it there
                    end = dyn_ticks[k]
                start_value = level
                if change:
                    end_value = start_value - change if down else start_value + change
                elif end in dyn_values:
                    end_value = dyn_values[end]
                else:
                    end_value = start_value + (-HAIRPIN_STEP if down else HAIRPIN_STEP)
                end_value = max(1, min(127, end_value))
                if (down and end_value > start_value) or (not down and end_value < start_value):
                    end_value = start_value
                rows.append((tick, end, start_value, end_value))
                level = end_value
            rows.sort(key=lambda r: (r[0], r[1]))
            self.levels[tr] = (rows, [r[0] for r in rows])

    def velocity_at(self, track: int, tick: Fraction) -> int:
        """The level at a tick: the last dynamic or hairpin that started at or before it decides."""
        rows, starts = self.levels.get(track, ((), ()))
        i = bisect.bisect_right(starts, tick) - 1
        if i < 0:
            return 80
        start, end, v0, v1 = rows[i]
        value = v0 + (v1 - v0) * float((tick - start) / (end - start)) if start < end and tick < end else v1
        return max(1, min(127, int(value)))

    # ============================================================================================ playback
    def _render(self):
        """Every chord's notes as MuseScore plays them, in score ticks: ``self.snotes[(staff, measure)]`` holds one
        entry per sounding event (a tremolo or user-edited note has several)."""
        self.snotes: Dict[Tuple[int, int], List[dict]] = {}
        self.item_at: Dict[int, Dict[Fraction, dict]] = {tr: {it['tick']: it for it in items}
                                                         for tr, items in self.track_items.items()}
        self.track_ticks = {tr: [it['tick'] for it in items] for tr, items in self.track_items.items()}
        self._owner: Dict[int, dict] = {}           # id(note) -> its chord (graces included)
        for items in self.track_items.values():
            for x in items:
                if x['kind'] == 'chord':
                    for chord in x['graces'] + [x]:
                        for n in chord['notes']:
                            self._owner[id(n)] = chord
        # two-chord tremolos: the chord after the first one (same track and measure, rests skipped) is its partner
        for (staff, mi), items in self.placed.items():
            if staff not in self.out_staff:
                continue
            by_track: Dict[int, List[dict]] = {}
            for it in sorted(items, key=lambda it: it['tick']):
                by_track.setdefault(it['track'], []).append(it)
            for seq in by_track.values():
                for k, it in enumerate(seq):
                    trem = it.get('tremolo')
                    if it['kind'] != 'chord' or not trem or not trem['two'] or it.get('tremolo_second'):
                        continue
                    partner = next((x for x in seq[k + 1:] if x['kind'] == 'chord'), None)
                    if partner is None or seq[k + 1]['kind'] != 'chord':
                        it['tremolo'] = None
                        continue
                    it['tremolo_partner'] = partner
                    partner['tremolo_second'] = True
        for (staff, mi), items in sorted(self.placed.items()):
            if staff not in self.out_staff:
                continue
            out: List[dict] = []
            for it in sorted(items, key=lambda it: (it['tick'], it['track'])):
                if it['kind'] == 'chord':
                    out.extend(self._play_chord(it, mi))
            out.sort(key=lambda s: (s['tick'], s['track'], s['pitch']))
            self.snotes[(staff, mi)] = out

    def _neighbours(self, it: dict, mi: int) -> Tuple[Optional[dict], Optional[dict]]:
        """The chord MuseScore treats as the previous one (this track's chord at the previous segment, any staff's
        chord or rest making a segment) and the next chord of the track."""
        track = it['track']
        at = self.item_at.get(track, {})
        segs = self.measure_segments.get(mi, [])
        i = bisect.bisect_left(segs, it['tick'])
        if i > 0:
            prev_tick = segs[i - 1]
        else:
            before = self.measure_segments.get(mi - 1, []) if mi > 0 else []
            prev_tick = before[-1] if before else None
        prev = at.get(prev_tick) if prev_tick is not None else None
        if prev is not None and prev['kind'] != 'chord':
            prev = None
        seq = self.track_items.get(track, [])
        j = bisect.bisect_right(self.track_ticks.get(track, []), it['tick'])
        nxt = seq[j] if j < len(seq) else None
        if nxt is not None and nxt['kind'] != 'chord':
            nxt = None
            if mi + 1 < len(self.measures):
                first = at.get(self._ticks(self.measures[mi + 1].start))
                nxt = first if first is not None and first['kind'] == 'chord' else None
        return prev, nxt

    @staticmethod
    def _grace_plays(g: dict) -> bool:
        return bool(g['notes']) and g['notes'][0]['play']

    def _chain_ms(self, note: dict, chord: dict) -> int:
        """MuseScore ticks from this note's chord to the end of the last note tied to it."""
        start = chord['tick']
        end_note, end_chord, seen = note, chord, set()
        while end_note.get('tie_to') is not None and id(end_note) not in seen:
            seen.add(id(end_note))
            nxt = end_note['tie_to']
            owner = self._owner.get(id(nxt))
            if owner is None:
                break
            end_note, end_chord = nxt, owner
        return max(1, _ms(end_chord['tick'] + end_chord['ticks'] - start, self.division))

    def _play_chord(self, it: dict, mi: int) -> List[dict]:
        staff, track = it['staff'], it['track']
        ins = self.instruments[self.staff_defs[staff].part]
        tick = it['tick']
        one = Fraction(self.division, 480)
        cms = max(1, _ms(it['ticks'], self.division))
        arts = [a for a in it['articulations'] if a['play']]
        trill = self.staff_defs[staff].group == 'pitched' and self.trill_on(track, tick, it['ticks'])
        merged = trill or any(a['ornament'] for a in arts)
        names = [_friendly(a['sym']) for a in it['articulations'] if a['sym']]
        if trill:
            names.append('trill')
        if it.get('arpeggio'):
            names.append('arpeggio')
        if it.get('tremolo'):
            names.append('tremolo')
        lines = {line for n in it['notes'] for line in n['lines']}
        names += [name for line, name in (('Glissando', 'glissando'), ('GuitarBend', 'bend')) if line in lines]
        prev, nxt = self._neighbours(it, mi)
        # velocity factor of the chord's articulations
        mult = 1.0
        for a in arts:
            mult *= ins.velocity(PLAYBACK_NAME.get(a['sym'], '---'))
        # gate: notes outside slurs at the instrument's default length, then the articulations
        gate = 100
        if not self.in_slur(staff, tick):
            gate = min(gate, ins.default_gate())
        before = [g for g in it['graces'] if g['grace_type'] in GRACE_BEFORE and self._grace_plays(g)]
        after = [g for g in reversed(it['graces']) if g['grace_type'] in GRACE_AFTER and self._grace_plays(g)]
        ontime = trailtime = grace_len = 0
        if (before or after) and not merged:
            if before and before[0]['grace_type'] != 'acciaccatura':
                ontime = min(500, int(_ms(before[0]['ticks'], self.division) / cms * 1000))
                grace_len = _cdiv(ontime, len(before))
                trailtime += ontime
            if after:
                trailtime = {1: 667, 2: 571}.get(it['dots'], 500)
        if nxt is not None:                         # the next chord's acciaccaturas take the end of this one
            accs = [g for g in nxt['graces'] if g['grace_type'] == 'acciaccatura']
            current = _ms(self._ticks(it['written']), self.division)
            if accs and current > 0:
                reduced = min(_ms(accs[0]['ticks'], self.division), _cdiv(current, 2))
                trailtime = int(trailtime + reduced / current * NOTE_LENGTH)
        events: Dict[int, List[list]] = {}          # note index -> [pitch offset, ontime, len, velocity factor]
        arpeggio = it.get('arpeggio')
        tremolo = it.get('tremolo')
        notes = it['notes']
        if it['user_events']:
            for k, n in enumerate(notes):
                events[k] = [[p, on, ln, 1.0] for p, on, ln in (n['events'] or [(0, 0, NOTE_LENGTH)])]
            gate = 100
        elif arpeggio and arpeggio['play'] and notes:
            top = notes[-1]
            play_ticks = self._chain_ms(top, it)
            step = 64
            while step and step * len(notes) > play_ticks:
                step = _cdiv(2 * step, 3)
            up = arpeggio['subtype'] not in ('2', '5', 'down', 'downStraight')
            order = list(range(len(notes))) if up else list(range(len(notes) - 1, -1, -1))
            ratio = self._tempo_at(tick) / DEFAULT_TEMPO
            for j, k in enumerate(order):
                ot = int(_cdiv(step * j * 1000, play_ticks) * ratio * arpeggio['stretch'])
                ot = min(max(ot + ontime, ot), NOTE_LENGTH)
                events[k] = [[0, ot, NOTE_LENGTH - ot, 1.0]]
            gate = 100
        else:
            if it.get('tremolo_second'):
                return self._grace_notes(it, before, after, prev, cms, ontime, trailtime, grace_len, mult, merged)
            if tremolo and not tremolo['buzz'] and tremolo['lines']:
                hooks = HOOKS.get(it['dtype'], 0)
                t = _cdiv(480, 1 << (tremolo['lines'] + hooks)) or 1
                written_ms = _ms(self._ticks(it['written']), self.division)
                if tremolo['two'] and it.get('tremolo_partner'):
                    other = it['tremolo_partner']['notes']
                    tticks = written_ms * 2
                    count = _cdiv(_cdiv(tticks, t), 2)
                    ln = _cdiv(2000 * t, tticks) if tticks else 0
                    for k in range(max(len(notes), len(other))):
                        own = k < len(notes)
                        slot = events.setdefault(k if own else 0, [])
                        for i in range(count):
                            if own:
                                slot.append([0, ln * i * 2, ln, 1.0])
                            if k < len(other):
                                base = notes[k]['pitch'] if own else notes[0]['pitch']
                                slot.append([other[k]['pitch'] - base, ln * i * 2 + ln, ln, 1.0])
                elif not tremolo['two']:
                    part = (NOTE_LENGTH - ontime - trailtime) / NOTE_LENGTH
                    size = int(_cdiv(written_ms, t) * part)
                    trem_time = int(1000 * part)
                    step = _cdiv(trem_time, size) if size else 0
                    for k in range(len(notes)):
                        events[k] = [[0, step * i, step, 1.0] for i in range(size)]
            if not trill:
                for a in arts:
                    if not a['ornament']:
                        gate = ins.gate(gate, PLAYBACK_NAME.get(a['sym'], '---'))
            for k in range(len(notes)):
                if k not in events:
                    events[k] = [[0, ontime, NOTE_LENGTH - trailtime, 1.0]]
            for evs in events.values():
                for e in evs:
                    e[2] = _cdiv(e[2] * gate, 100)
        out = self._grace_notes(it, before, after, prev, cms, ontime, trailtime, grace_len, mult, merged)
        # unisons in one chord: only the note with the longest tie chain sounds
        longest: Dict[int, int] = {}
        chains = [self._chain_ms(n, it) for n in notes]
        for n, c in zip(notes, chains):
            longest[n['pitch']] = max(longest.get(n['pitch'], 0), c)
        shift = self.ottava(staff, tick)
        for k, n in enumerate(notes):
            if chains[k] != longest[n['pitch']]:
                continue
            if not n['play']:                       # silent, and so are the notes tied to it
                out.append(dict(staff=staff, track=track, pitch=n['pitch'], tpc=n['tpc'], tick=tick, end=tick,
                                play=Fraction(0), velocity=0, grace=False, arts=[], note=n, silent=True,
                                tie_to=n.get('tie_to'), cont=n.get('tied_from') is not None))
                continue
            evs = events.get(k, [])
            single = len(evs) == 1
            for j, (dp, on, ln, vm) in enumerate(evs):
                start = tick + Fraction(_cdiv(cms * on, NOTE_LENGTH)) * one
                play = Fraction(max(0, _cdiv(cms * ln, NOTE_LENGTH))) * one
                if play <= 0 and ln <= 0:
                    continue
                written_end = tick + it['ticks'] if single else start + play
                velocity = self._velocity(n, track, start, mult * vm)
                out.append(dict(staff=staff, track=track, pitch=max(0, min(127, n['pitch'] + shift + dp)),
                                tpc=n['tpc'] if dp == 0 else None, tick=start, end=max(written_end, start),
                                play=play, velocity=velocity, grace=False, arts=list(names), note=n,
                                tie_to=n.get('tie_to') if j == len(evs) - 1 else None,
                                cont=single and n.get('tied_from') is not None))
        return out

    def _grace_notes(self, it: dict, before: List[dict], after: List[dict], prev: Optional[dict], cms: int,
                     ontime: int, trailtime: int, grace_len: int, mult: float, merged: bool) -> List[dict]:
        """Grace notes as MuseScore times them: acciaccaturas before the beat (half the previous chord at most),
        appoggiaturas from the beat, grace notes after a chord at its end."""
        out: List[dict] = []
        staff, track, tick = it['staff'], it['track'], it['tick']
        one = Fraction(self.division, 480)
        shift = self.ottava(staff, tick)
        accs = [g for g in it['graces'] if g['grace_type'] == 'acciaccatura']
        total = step = 0
        if accs:
            total = _ms(self._ticks(accs[0]['written']), self.division)
            if prev is not None:
                total = min(_cdiv(_ms(self._ticks(prev['written']), self.division), 2), total)
            step = _cdiv(total, len(accs))
        timed: List[Tuple[dict, Fraction, Fraction]] = []
        if merged:                                  # MuseScore folds them into the ornament: keep them short
            for k, g in enumerate(before):
                length = max(1, _ms(self._ticks(g['written']), self.division) // 2)
                timed.append((g, tick - Fraction(length * (len(before) - k)) * one, Fraction(length) * one))
        else:
            position = 0
            for k, g in enumerate(b for b in it['graces'] if b['grace_type'] in GRACE_BEFORE):
                if g not in before:
                    position += 1
                    continue
                if g['grace_type'] == 'acciaccatura':
                    on = tick - Fraction(total - step * position) * one
                    timed.append((g, on, Fraction(max(0, step)) * one))
                else:
                    on = tick + Fraction(_cdiv(cms * (grace_len * position), NOTE_LENGTH)) * one
                    timed.append((g, on, Fraction(_cdiv(cms * grace_len, NOTE_LENGTH)) * one))
                position += 1
        if after and not merged:
            each = _cdiv(trailtime, len(after))
            for k, g in enumerate(after):
                on = tick + Fraction(_cdiv(cms * (NOTE_LENGTH - trailtime + k * each), NOTE_LENGTH)) * one
                timed.append((g, on, Fraction(_cdiv(cms * each, NOTE_LENGTH)) * one))
        elif after:
            for k, g in enumerate(after):
                length = max(1, _ms(self._ticks(g['written']), self.division) // 2)
                timed.append((g, tick + it['ticks'] - Fraction(length * (len(after) - k)) * one, Fraction(length) * one))
        for g, on, length in timed:
            names = [_friendly(a['sym']) for a in g['articulations'] if a['sym']] + [g['grace_type']]
            user = any(n['events'] is not None for n in g['notes'])
            for n in g['notes']:
                if not n['play']:
                    continue
                if user:                            # play events edited by hand, in thousandths of the main chord
                    base = on if g['grace_type'] == 'acciaccatura' and not merged else tick
                    for dp, start, ln in n['events'] or [(0, 0, NOTE_LENGTH)]:
                        t = base + Fraction(_cdiv(cms * start, NOTE_LENGTH)) * one
                        play = Fraction(max(0, _cdiv(cms * ln, NOTE_LENGTH))) * one
                        if play > 0:
                            out.append(dict(staff=staff, track=track, pitch=max(0, min(127, n['pitch'] + shift + dp)),
                                            tpc=n['tpc'] if dp == 0 else None, tick=t, end=t + play, play=play,
                                            velocity=self._velocity(n, track, tick, mult), grace=True, arts=names,
                                            note=n, tie_to=n.get('tie_to'), cont=False))
                    continue
                if length <= 0:
                    continue
                out.append(dict(staff=staff, track=track, pitch=max(0, min(127, n['pitch'] + shift)), tpc=n['tpc'],
                                tick=on, end=on + length, play=length,
                                velocity=self._velocity(n, track, tick, mult), grace=True, arts=names, note=n,
                                tie_to=n.get('tie_to'), cont=False))
        return out

    def _velocity(self, note: dict, track: int, tick: Fraction, factor: float) -> int:
        velocity = int(self.velocity_at(track, tick) * factor)
        if note['velocity']:
            if note['velo_type'] == 'offset':
                velocity = velocity + _cdiv(velocity * note['velocity'], 100)
            else:
                velocity = note['velocity']
        return max(1, min(127, velocity))

    # ============================================================================================= repeats
    def _unwind(self) -> List[Tuple[int, int]]:
        """The measures in the order they are played, each with its pass (0 the first time it is played)."""
        order = _Unwinder(self).run()
        if not order:
            order = list(range(len(self.measures)))
        played, seen = [], {}
        for mi in order:
            played.append((mi, seen.get(mi, 0)))
            seen[mi] = seen.get(mi, 0) + 1
        return played

    def _repeat_size(self, staff: int, mi: int) -> int:
        """How many measures a measure-repeat group repeats (its MeasureRepeat element may be in any of them)."""
        count = self.repeat_counts.get((staff, mi), 0)
        first = mi - max(0, count - 1)
        for j in range(first, first + 4):
            for it in self.placed.get((staff, j), []):
                if it['kind'] == 'repeat':
                    return it['repeat_measures']
        return 1

    # =============================================================================================== output
    def _emit(self, played: List[Tuple[int, int]], source: str, sha: str) -> msi.ScoreImport:
        division = self.division
        measures: List[msi.Measure] = []
        tick = Fraction(0)
        for p, (mi, k) in enumerate(played):
            sm = self.score_measures[mi]
            measures.append(msi.Measure(index=p, number=sm.number, tick=tick, ticks=sm.ticks, time_sig=sm.time_sig,
                                        actual=sm.actual, irregular=sm.irregular, source=mi, play=k))
            tick += sm.ticks
        offsets = [measures[p].tick - self.score_measures[mi].tick for p, (mi, _) in enumerate(played)]
        # runs: stretches played as written (each measure followed by the next one in the score)
        run_end_score, run_end_played = [Fraction(0)] * len(played), [Fraction(0)] * len(played)
        start = 0
        for p in range(len(played) + 1):
            if p == len(played) or (p > start and played[p][0] != played[p - 1][0] + 1):
                last = p - 1
                end_score = self.score_measures[played[last][0]].tick + self.score_measures[played[last][0]].ticks
                end_played = measures[last].tick + measures[last].ticks
                for q in range(start, p):
                    run_end_score[q], run_end_played[q] = end_score, end_played
                start = p
        run_start = [p == 0 or played[p][0] != played[p - 1][0] + 1 for p in range(len(played))]

        # ---- notes
        notes: List[msi.Note] = []
        open_ties: Dict[int, Tuple[msi.Note, Fraction]] = {}
        plain: Dict[int, int] = {}
        for p, (mi, _) in enumerate(played):
            batch = []                              # every staff's notes of this measure, in time order
            for staff, out in self.out_staff.items():
                src, off = mi, offsets[p]
                if self.repeat_counts.get((staff, mi)):
                    last = plain.get(staff)
                    if last is None:
                        continue
                    count, size = self.repeat_counts[(staff, mi)], self._repeat_size(staff, mi)
                    while count < size and last > 0:
                        last -= 1
                        count += 1
                    src, off = last, measures[p].tick - self.score_measures[last].tick
                else:
                    plain[staff] = mi
                batch.extend((sn['tick'] + off, out, off, sn) for sn in self.snotes.get((staff, src), []))
            batch.sort(key=lambda x: (x[0], x[1], x[3]['track'], x[3]['pitch']))
            for _, out, off, sn in batch:           # (a tie may cross staves and voices: heads come first)
                key = id(sn['note'])
                waiting = open_ties.get(key)
                tied = sn['cont'] and waiting is not None and waiting[1] == off
                if sn.get('silent') or (tied and waiting[0] is None):
                    open_ties.pop(key, None)            # a silent note, or tied to one: nothing sounds
                    if sn['tie_to'] is not None:
                        open_ties[id(sn['tie_to'])] = (None, off)
                    continue
                if tied:
                    head = open_ties.pop(key)[0]
                    head.ticks = sn['end'] + off - head.tick
                    head.play_ticks += sn['play']
                    current = head
                else:
                    current = msi.Note(staff=out, voice=sn['track'] % 4, track=out * 4 + sn['track'] % 4,
                                       pitch=sn['pitch'], tpc=sn['tpc'], tick=sn['tick'] + off,
                                       ticks=sn['end'] - sn['tick'], play_ticks=sn['play'],
                                       velocity=sn['velocity'], measure=p, grace=sn['grace'],
                                       articulations=list(sn['arts']))
                    notes.append(current)
                if sn['tie_to'] is not None:
                    current.tie_start = True
                    open_ties[id(sn['tie_to'])] = (current, off)
        notes.sort(key=lambda n: (n.tick, n.staff, n.pitch))

        # ---- tempo, time signatures, pauses
        tempos: List[Tuple[Fraction, float]] = []
        score_ticks = [t for t, _ in self.score_tempo]
        for p, (mi, _) in enumerate(played):
            sm = self.score_measures[mi]
            if run_start[p]:
                tempos.append((measures[p].tick, self._tempo_at(sm.tick)))
            i = bisect.bisect_left(score_ticks, sm.tick)
            while i < len(score_ticks) and score_ticks[i] < sm.tick + sm.ticks:
                tempos.append((score_ticks[i] + offsets[p], self.score_tempo[i][1]))
                i += 1
        sigs: List[Tuple[Fraction, int, int]] = []
        for p, m in enumerate(measures):
            if not sigs or (sigs[-1][1], sigs[-1][2]) != m.actual:
                sigs.append((m.tick, m.actual[0], m.actual[1]))
        pauses: List[Tuple[Fraction, float]] = []
        starts = [sm.tick for sm in self.score_measures]
        for t, seconds in self.score_pauses:
            i = bisect.bisect_left(starts, t) - 1   # the measure the pause ends: start < t <= end
            if i < 0:
                continue
            for p, (mi, _) in enumerate(played):
                if mi == i:
                    pauses.append((t + offsets[p], seconds))
        onsets = sorted({n.tick for n in notes} | {m.tick for m in measures})
        tempos = _fold_pauses(_tidy_tempos(tempos), sorted(pauses), onsets, division)

        # ---- pedals, directions
        pedals: List[Tuple[int, Fraction, Fraction]] = []
        by_measure: Dict[int, List[int]] = {}
        for p, (mi, _) in enumerate(played):
            by_measure.setdefault(mi, []).append(p)

        def measure_of(t: Fraction) -> int:
            return max(0, bisect.bisect_right(starts, t) - 1)

        def span(p: int, a: Fraction, b: Fraction) -> Tuple[Fraction, Fraction]:
            end = b + offsets[p] if b <= run_end_score[p] else run_end_played[p]
            return a + offsets[p], max(a + offsets[p], end)

        for staff, a, b in self.pedals:
            for p in by_measure.get(measure_of(a), []):
                t0, t1 = span(p, a, b)
                if t1 > t0:
                    pedals.append((self.out_staff[staff], t0, t1))
        pedals.sort(key=lambda x: (x[1], x[0]))
        directions = self._directions(played, offsets, by_measure, measure_of, pauses)

        parts, staves = self._parts_and_staves()
        title, composer = self._metadata()
        return msi.ScoreImport(source=source, sha256=sha, format_version=self.version,
                               program_version=(self.root.findtext('programVersion') or '').strip(),
                               reader='resonance.modern', title=title, composer=composer, division=division,
                               parts=parts, staves=staves, measures=measures, notes=notes, tempos=tempos,
                               sigs=sigs, pedals=pedals, directions=directions, repairs=self.repairs,
                               warnings=self.warnings)

    def _directions(self, played, offsets, by_measure, measure_of, pauses) -> List[msi.Direction]:
        out: List[msi.Direction] = []
        texts = {'Tempo': 'tempo', 'StaffText': 'text', 'SystemText': 'text', 'Text': 'text',
                 'PlayTechAnnotation': 'text', 'RehearsalMark': 'rehearsal', 'Expression': 'expression'}
        for a in self.annotations:
            if a['staff'] not in self.out_staff:
                continue
            staff = self.out_staff[a['staff']]
            tag, el = a['tag'], a['el']
            kind = text = None
            if tag == 'Dynamic':
                sub = (el.findtext('subtype') or '').strip()
                if sub in DYNAMICS:
                    kind, text = 'dynamic', sub
                else:
                    kind, text = 'expression', _plain_text(el) or sub
            elif tag in texts:
                kind, text = texts[tag], _plain_text(el)
            elif tag == 'Fermata':
                kind, text = 'fermata', _friendly((el.findtext('subtype') or 'fermataAbove').strip())
            if not kind or not text:
                continue
            for p in by_measure.get(a['mi'], []):
                out.append(msi.Direction(tick=a['tick'] + offsets[p], staff=staff, kind=kind, text=text, measure=p))
        for sp in self.hairpins + self.gradual:
            body = sp['body']
            if sp['type'] == 'HairPin':
                sub = (body.findtext('subtype') or '0').strip()
                down = sub in ('1', '3')
                text = _plain_text(body.find('beginText')) if body.find('beginText') is not None else ''
                kind, text = 'hairpin', text or ('diminuendo' if down else 'crescendo')
            else:
                ctype = (body.findtext('tempoChangeType') or '').strip()
                begin = body.find('beginText')
                kind, text = 'tempo', (_plain_text(begin) if begin is not None else '') or GRADUAL_TEXT.get(ctype, ctype)
            if sp['staff'] not in self.out_staff or not text:
                continue
            for p in by_measure.get(measure_of(sp['tick']), []):
                out.append(msi.Direction(tick=sp['tick'] + offsets[p], staff=self.out_staff[sp['staff']], kind=kind,
                                         text=text, measure=p))
        for mi, m in enumerate(self.measures):
            for mark in m.marks:
                text = mark['text'] or mark.get('label', '')
                if not text:
                    continue
                t = self.score_measures[mi].tick + (self.score_measures[mi].ticks
                                                    if mark['kind'] == 'jump' or mark.get('right') else 0)
                for p in by_measure.get(mi, []):
                    out.append(msi.Direction(tick=t + offsets[p], staff=0, kind='repeat', text=text, measure=p))
        for v in self.voltas:
            text = _plain_text(v['body'].find('beginText')) if v['body'].find('beginText') is not None else ''
            text = text or (v['body'].findtext('endings') or '').strip()
            for p in by_measure.get(measure_of(v['tick']), []):
                out.append(msi.Direction(tick=v['tick'] + offsets[p], staff=0, kind='volta', text=text, measure=p))
        played_ticks = [self.score_measures[mi].tick + offsets[p] for p, (mi, _) in enumerate(played)]
        for t, seconds in pauses:                   # a pause belongs to the measure it ends
            p = max(0, min(len(played) - 1, bisect.bisect_left(played_ticks, t) - 1))
            out.append(msi.Direction(tick=t, staff=0, kind='pause', text=f'{seconds:g} s', measure=p))
        out.sort(key=lambda d: (d.tick, d.staff))
        return out

    def _parts_and_staves(self) -> Tuple[List[dict], List[dict]]:
        parts: List[dict] = []
        staves: List[dict] = []
        for pi, ins in enumerate(self.instruments):
            ids = [sd.index for sd in self.staff_defs if sd.part == pi and not sd.linked]
            if not ids:
                continue
            name = self.part_names[pi] if pi < len(self.part_names) else f'Part {pi + 1}'
            percussion = ins.drumset or any(self.staff_defs[s].group == 'percussion' for s in ids)
            index = len(parts)
            parts.append(dict(index=index, name=name, program=ins.program, staves=[self.out_staff[s] for s in ids],
                              percussion=percussion, instrument=ins.instrument_id, transpose=ins.transpose))
            for k, s in enumerate(ids):
                staves.append(dict(index=self.out_staff[s], part=index,
                                   name=_staff_label(name, ins.program, len(ids), k, percussion),
                                   percussion=percussion, scoreStaff=s + 1,
                                   tablature=self.staff_defs[s].group == 'tablature'))
        staves.sort(key=lambda s: s['index'])
        return parts, staves

    def _metadata(self) -> Tuple[str, str]:
        title = composer = ''
        for style, text in self.frame_texts:
            text = re.sub(r'\s*\n\s*', ' ', text).strip()
            if style == 'title' and not title:
                title = text
            elif style == 'composer' and not composer:
                composer = text
        meta = {mt.get('name'): (mt.text or '').strip() for mt in self.score.findall('metaTag')}
        if not title:
            title = meta.get('workTitle') or meta.get('movementTitle') or ''
        if not composer:
            composer = meta.get('composer', '')
        if composer.lower() in PLACEHOLDER_TEXTS:
            composer = ''
        composer = re.sub(r'\(\s*\d{4}\s*-\s*\d{4}\s*\)', '', composer).strip()
        return title, composer


PIANO_NAMES = {'', 'piano', 'grand piano', 'acoustic grand piano', 'pianoforte', 'solo piano', 'piano solo',
               'upright piano', 'keyboard', 'harpsichord', 'electric piano'}


def _staff_label(name: str, program: int, count: int, k: int, percussion: bool) -> str:
    """'Right hand' / 'Left hand' for a keyboard's two staves (with the part's name when it is not plainly a
    piano, e.g. 'Piano-Orchestral (right hand)'), else the part's name, numbered when it has several staves."""
    if count == 2 and 0 <= program < 8 and not percussion:
        hand = 'Right hand' if k == 0 else 'Left hand'
        return hand if name.strip().lower() in PIANO_NAMES else f'{name} ({hand.lower()})'
    return name if count == 1 else f'{name} {k + 1}'


def _tidy_tempos(tempos: List[Tuple[Fraction, float]]) -> List[Tuple[Fraction, float]]:
    """Sorted, one value per tick (the last one given), no repeats of the same value."""
    last: Dict[Fraction, float] = {}
    for t, q in tempos:
        last[t] = q
    out: List[Tuple[Fraction, float]] = []
    for t in sorted(last):
        if out and abs(out[-1][1] - last[t]) < 1e-12:
            continue
        out.append((t, last[t]))
    return out or [(Fraction(0), DEFAULT_TEMPO)]


def _fold_pauses(tempos: List[Tuple[Fraction, float]], pauses: List[Tuple[Fraction, float]],
                 onsets: List[Fraction], division: int) -> List[Tuple[Fraction, float]]:
    """Pauses become time, not ticks: the stretch from the last note onset before a pause up to the pause is
    played slowly enough to last its own time plus the pause, so the notes sounding into the pause are held
    through it (as MuseScore holds them) and no onset moves.  When that stretch is shorter than a MIDI tempo can
    hold (16.7 s per quarter note at most), it starts a little earlier."""
    for at, seconds in pauses:
        if at <= 0 or seconds <= 0:
            continue
        shortest = Fraction(math.ceil(division * (seconds + 0.25) / 15), 1)
        i = bisect.bisect_left(onsets, at) - 1
        start = onsets[i] if i >= 0 else Fraction(0)
        start = max(Fraction(0), min(start, at - shortest))
        if start >= at:
            continue
        tm = msi.TempoMap(tempos, division)
        duration = tm(at) - tm(start)
        after = _tempo_value(tempos, at)
        qps = float(at - start) / division / (duration + seconds)
        kept = [(t, q) for t, q in tempos if t < start or t > at]
        kept.append((start, qps))
        if not any(t == at for t, _ in tempos):
            kept.append((at, after))
        else:
            kept.extend((t, q) for t, q in tempos if t == at)
        tempos = _tidy_tempos(kept)
    return tempos


def _tempo_value(tempos: List[Tuple[Fraction, float]], tick: Fraction) -> float:
    value = tempos[0][1] if tempos else DEFAULT_TEMPO
    for t, q in tempos:
        if t > tick:
            break
        value = q
    return value


# ================================================================================================ repeats

SECTION_BREAK, VOLTA_START, VOLTA_END, REPEAT_START, REPEAT_END, JUMP, MARKER = range(7)


class _Volta:
    def __init__(self, start: int, end: int, endings: List[int], open_end: bool):
        self.start, self.end, self.endings, self.open_end = start, end, list(endings), open_end

    def clone(self) -> '_Volta':
        return _Volta(self.start, self.end, self.endings, self.open_end)

    def has(self, n: int) -> bool:
        return n in self.endings

    def first(self) -> int:
        return self.endings[0] if self.endings else 0

    def last(self) -> int:
        return self.endings[-1] if self.endings else 0


class _Element:
    __slots__ = ('type', 'element', 'measure', 'count')

    def __init__(self, kind: int, element, measure: int):
        self.type, self.element, self.measure = kind, element, measure
        self.count = 1 if kind == REPEAT_START else 0


class _Segment:
    def __init__(self, count: int):
        self.count = count
        self.measures: List[int] = []

    def add(self, m: Optional[int]):
        if m is not None and (not self.measures or self.measures[-1] != m):
            self.measures.append(m)

    def add_through(self, m: int, last: int):
        """Add every measure after the current last one up to ``m`` (MuseScore's addMeasures)."""
        if self.measures:
            nxt = self.measures[-1] + 1
            if nxt <= last and nxt < m:
                while nxt != m:
                    self.measures.append(nxt)
                    nxt += 1
            while self.measures and self.measures[-1] >= m:
                self.measures.pop()
        self.add(m)


class _Unwinder:
    """The order measures are played in, after MuseScore's RepeatList (collect + unwind): repeats with their
    counts, voltas (endings merged where they overlap), jumps to markers with play-until / continue-at, and
    sections (a section break starts a new repeat structure)."""

    def __init__(self, reader: ModernReader):
        self.r = reader
        self.n = len(reader.measures)
        self.sections: List[List[_Element]] = []

    def _next(self, m: int) -> Optional[int]:
        return m + 1 if m + 1 < self.n else None

    def _voltas(self) -> List[_Volta]:
        starts = [m.start for m in self.r.measures]
        q = 4 * self.r.division

        def start_of(tick):
            return max(0, bisect.bisect_right(starts, tick / q) - 1)

        def end_of(tick2):             # the measure m with m.tick < tick2 <= m.end
            w = tick2 / q
            for m in self.r.measures:
                if m.start < w <= m.end:
                    return m.index
            return None

        pre: List[_Volta] = []
        for sp in sorted(self.r.voltas, key=lambda s: (s['tick'], s['seq'])):
            body = sp['body']
            if not _plays(body):
                continue
            sm, em = start_of(sp['tick']), end_of(sp['tick2'])
            if em is None or em < sm:
                continue
            endings = []
            for part in (body.findtext('endings') or '').replace(',', ' ').split():
                try:
                    endings.append(int(part))
                except ValueError:
                    pass
            volta = _Volta(sm, em, endings, (body.findtext('endHookType') or '0').strip() == '0')
            if not pre:
                pre.append(volta)
                continue
            merge = []
            while pre and volta.start <= pre[-1].end:
                merge.append(pre.pop())
            while merge:
                rest = merge[-1].clone()
                if volta.start != rest.start:
                    merge[-1].end = volta.start - 1
                    rest.start = volta.start
                    pre.append(merge[-1])
                merge.pop()
                if volta.end < rest.end:
                    volta, rest = rest, volta
                rest.endings = [e for e in rest.endings if volta.has(e)]
                pre.append(rest)
                if volta.end != rest.end:
                    volta.start = rest.end + 1
                else:
                    pre.extend(merge)
                    merge = []
                    volta = None
                    break
            if volta is not None:
                pre.append(volta)
        return pre

    def collect(self):
        measures = self.r.measures
        bases = self.r.bases
        pre = self._voltas()
        first = 0
        section: List[_Element] = []
        start_from = _Element(REPEAT_START, first, first)
        section.append(start_from)
        section_end = None
        volta: Optional[_Volta] = None
        i = next((k for k, b in enumerate(bases) if isinstance(b, _Measure)), len(bases))
        while i < len(bases):
            base = bases[i]
            if isinstance(base, _Measure):
                m = base.index
                section_end = m
                if pre and pre[0].start == m:
                    if volta is not None:
                        section.append(_Element(VOLTA_END, volta, m - 1))
                    volta = pre.pop(0)
                    section.append(_Element(VOLTA_START, volta, m))
                if base.repeat_start:
                    if volta is not None and volta.start != m:
                        section.append(_Element(VOLTA_END, volta, m - 1))
                        volta = None
                    start_from = _Element(REPEAT_START, m, m)
                    section.append(start_from)
                for mark in base.marks:
                    if mark['kind'] == 'jump':
                        section.append(_Element(JUMP, mark, m))
                        if volta is not None and (volta.end < m or (volta.end == m and volta.open_end)) \
                                and not base.repeat_end:
                            section.append(_Element(VOLTA_END, volta, m))
                            volta = None
                    else:
                        element = _Element(MARKER, mark, m)
                        pos = len(section) - 1
                        while pos > 0 and section[pos].measure == m:
                            before = section[pos].type == MARKER and not mark['right'] and section[pos].element['right']
                            if before or section[pos].type == JUMP:
                                pos -= 1
                            else:
                                break
                        section.insert(pos + 1, element)
                if base.repeat_end:
                    section.append(_Element(REPEAT_END, m, m))
                    if start_from is not None:
                        start_from.count += base.repeat_count - 1
                    if volta is not None:
                        section.append(_Element(VOLTA_END, volta, m))
                        volta = None
                if volta is not None and volta.end == m and not volta.open_end:
                    section.append(_Element(VOLTA_END, volta, m))
                    volta = None
            nxt = next((k for k in range(i + 1, len(bases)) if isinstance(bases[k], _Measure)), None)
            if base.section_break or nxt is None:
                if section_end is not None:
                    if volta is not None:
                        section.append(_Element(VOLTA_END, volta, section_end))
                        volta = None
                    section.append(_Element(SECTION_BREAK, base, section_end))
                    section_end = None
                    self.sections.append(section)
                if nxt is not None:
                    m = bases[nxt].index
                    start_from = _Element(REPEAT_START, m, m)
                    section = [start_from]
                    i = nxt
                    continue
                break
            i += 1

    def find(self, label: str, si: int, ei: int) -> Tuple[Optional[int], Optional[int]]:
        secs = self.sections
        if not label:
            return None, None
        if label == 'start':
            return si, 0
        if label == 'end':
            return si, len(secs[si]) - 1
        sec = secs[si]

        def hit(e: _Element) -> bool:
            return e.type == MARKER and e.element.get('label') == label

        for j in range(ei - 1, -1, -1):
            if hit(sec[j]):
                return si, j
        for j in range(ei + 1, len(sec)):
            if hit(sec[j]):
                return si, j
        for s in range(si - 1, -1, -1):
            for j in range(len(secs[s]) - 1, -1, -1):
                if hit(secs[s][j]):
                    return s, j
        for s in range(si + 1, len(secs)):
            for j, e in enumerate(secs[s]):
                if hit(e):
                    return s, j
        return None, None

    def jump(self, si: int, target: int, with_repeats: bool):
        sec = self.sections[si]
        volta = None
        start = sec[0]
        for j in range(target):
            e = sec[j]
            if e.type == VOLTA_START:
                volta = e.element
            elif e.type == VOLTA_END:
                volta = None
            elif e.type == REPEAT_START:
                start = e
        if with_repeats:
            count = (volta.first() or 1) if volta is not None else 1
        else:
            count = (volta.last() or start.count) if volta is not None else start.count
        return count, volta, start

    def run(self) -> List[int]:
        if not self.n:
            return []
        self.collect()
        repeat_count = {m.index: m.repeat_count for m in self.r.measures}
        result: List[Optional[_Segment]] = []
        taken = set()
        active_jump = None
        secs = self.sections
        si = 0
        steps = 0
        limit = 200 * self.n + 1000
        while si < len(secs):
            sec = secs[si]
            count = 1
            start_ref = sec[0]
            volta = None
            until = (None, None)
            cont = (None, None)
            final = False
            rs: Optional[_Segment] = _Segment(count)
            rs.add(sec[0].measure)
            ei = 1
            while ei < len(sec):
                steps += 1
                if steps > limit:
                    self.r._warn('the repeat structure did not settle: playing the rest of it as written')
                    return [m for seg in result if seg is not None for m in seg.measures] or list(range(self.n))
                e = sec[ei]
                if rs is not None:
                    rs.add_through(e.measure, self.n - 1)
                if e.type == SECTION_BREAK:
                    if rs is not None and rs.measures:
                        result.append(rs)
                elif e.type == VOLTA_START:
                    volta = e.element
                    if not volta.has(count):
                        if rs is not None:
                            if rs.measures:
                                rs.measures.pop()
                            if rs.measures:
                                result.append(rs)
                        ei += 1
                        while ei < len(sec) and sec[ei].type != VOLTA_END:
                            ei += 1
                        volta = None
                        if ei >= len(sec):
                            rs = None
                            break
                        nxt = self._next(sec[ei].measure)
                        if nxt is None:
                            rs = None
                        else:
                            rs = _Segment(count)
                            rs.add(nxt)
                elif e.type == VOLTA_END:
                    volta = None
                elif e.type == REPEAT_START:
                    if rs is None:
                        rs = _Segment(count)
                        rs.add(e.measure)
                    else:
                        desired = e.count if final else 1
                        if rs.count != desired:
                            if rs.measures:
                                rs.measures.pop()
                            if rs.measures:
                                result.append(rs)
                            count = desired
                            rs = _Segment(count)
                            rs.add(e.measure)
                        start_ref = e
                elif e.type == REPEAT_END:
                    e.count += 1
                    if count < start_ref.count and e.count < repeat_count[e.measure]:
                        if rs is not None and rs.measures:
                            result.append(rs)
                        rs = None
                        while ei > 0:
                            ei -= 1
                            if sec[ei].type == VOLTA_START:
                                volta = None
                            elif sec[ei].type == VOLTA_END:
                                volta = sec[ei].element
                            if sec[ei] is start_ref:
                                break
                        count += 1
                        continue
                elif e.type == JUMP:
                    mark = e.element
                    if active_jump is mark:
                        final = False
                    if count >= start_ref.count or (volta is not None and count == volta.last()):
                        occasion = (id(mark), count)
                        if occasion not in taken:
                            taken.add(occasion)
                            target = self.find(mark['jump_to'], si, ei)
                            until = self.find(mark['play_until'], si, ei)
                            cont = self.find(mark['continue_at'], si, ei)
                            if target[0] is not None:
                                result.append(rs)
                                rs = None
                                active_jump = mark
                                count, volta, start_ref = self.jump(target[0], target[1], mark['play_repeats'])
                                si, ei = target
                                sec = secs[si]
                                final = not mark['play_repeats']
                                if until[0] is not None:
                                    for k in range(ei + 1, len(sec)):
                                        x = sec[k]
                                        if x.type == REPEAT_END and x.count != 0:
                                            x.count = repeat_count[x.measure] if final else 0
                                if volta is not None and count < start_ref.count:
                                    k = ei
                                    while k > 0:
                                        k -= 1
                                        if sec[k] is start_ref:
                                            break
                                    k += 1
                                    ref = None
                                    processed = 1
                                    while k < len(sec) and sec[k].type != REPEAT_START and processed < start_ref.count:
                                        x = sec[k]
                                        if x.type == VOLTA_START:
                                            ref = x.element
                                            if ref.last() < count:
                                                ref = None
                                        elif x.type == REPEAT_END:
                                            if ref is not None:
                                                remaining = sum(1 for n in ref.endings if n >= count)
                                                x.count = repeat_count[x.measure] - remaining - 1
                                            processed += repeat_count[x.measure] - 1
                                        k += 1
                                rs = _Segment(count)
                                rs.add(sec[ei].measure)
                elif e.type == MARKER:
                    if (si, ei) == until and (count == start_ref.count or
                                              (volta is not None and count == volta.last())):
                        result.append(rs)
                        rs = None
                        until = (None, None)
                        final = False
                        if cont[0] is not None:
                            count, volta, start_ref = self.jump(cont[0], cont[1], True)
                            si, ei = cont
                            sec = secs[si]
                            if start_ref.measure != sec[ei].measure:
                                start_ref = sec[ei]
                            rs = _Segment(count)
                            rs.add(sec[ei].measure)
                            cont = (None, None)
                        else:
                            ei = len(sec)
                            continue
                ei += 1
            si += 1
        return [m for seg in result if seg is not None for m in seg.measures]
