"""Printed bar numbers to recording seconds, for bundles ingested from a MuseScore score.

A director writes cue boundaries the way a musician talks: "bar 17", "bar 46, beat 2", "the second time bar 9
is played". The bundle keeps the score import (score-import.json: every measure with its printed number, tick
and, for repeats, its pass) and the aligned tempo map (aligned.mid.patterns.json: the score's ticks against the
recording's seconds, after a silent lead-in of song.json's ScoreLeadInBeats). Together they place any bar in the
recording, which stays the source of truth for time.

    BarClock(bundle).seconds('17')      # downbeat of printed bar 17
    BarClock(bundle).seconds('46:2')    # bar 46, beat 2 (beats counted in the bar's time-signature unit)
    BarClock(bundle).seconds('9#2')     # the second time bar 9 is played (repeats unrolled)
    BarClock(bundle).seconds('end')     # the end of the last bar
"""
import bisect
import json
import re
from pathlib import Path

SPEC = re.compile(r'^\s*(?P<bar>-?\d+)(?:#(?P<pass>\d+))?(?::(?P<beat>\d+(?:\.\d+)?))?\s*$')


class BarClock:
    def __init__(self, bundle):
        bundle = Path(bundle)
        imported = bundle/'score-import.json'
        if not imported.exists():
            raise ValueError('This bundle was not ingested from a score (no score-import.json)')
        score = json.loads(imported.read_text(encoding='utf-8'))
        patterns = json.loads((bundle/'aligned.mid.patterns.json').read_text(encoding='utf-8'))
        song = json.loads((bundle/'song.json').read_text(encoding='utf-8')) if (bundle/'song.json').exists() else {}
        self.lead = float(song.get('ScoreLeadInBeats', 0) or 0)
        self.division = score['division']
        self.measures = score['measures']
        self.duration = float(patterns['Duration'])
        self.tempos = patterns['Tempos']
        self.beats = [t['Beat'] for t in self.tempos]

    def at_beat(self, beat):
        """Recording seconds of a bundle beat (quarter notes from the start of the aligned MIDI)."""
        t = self.tempos[max(0, bisect.bisect_right(self.beats, beat) - 1)]
        return t['Seconds'] + (beat - t['Beat']) * t['Microseconds'] / 1e6

    def measure(self, bar, occurrence=1):
        found = [m for m in self.measures if m['number'] == bar]
        if len(found) < occurrence:
            raise ValueError(f'Bar {bar} is played {len(found)} time(s); occurrence {occurrence} does not exist')
        return found[occurrence - 1]

    def seconds(self, spec):
        """Recording seconds of a bar spec: 'N', 'N:beat', 'N#pass', 'N#pass:beat' or 'end'."""
        if isinstance(spec, (int, float)) and not isinstance(spec, bool):
            spec = str(int(spec))
        if str(spec).strip() == 'end':
            last = self.measures[-1]
            return min(self.duration, self.at_beat(self.lead + (last['tick'] + last['ticks']) / self.division))
        m = SPEC.match(str(spec))
        if not m:
            raise ValueError(f'Bar spec {spec!r}: use N, N:beat, N#pass or N#pass:beat')
        measure = self.measure(int(m['bar']), int(m['pass'] or 1))
        beat = float(m['beat'] or 1)
        unit = 4 / measure['timeSignature'][1] * self.division        # ticks per counted beat
        tick = measure['tick'] + (beat - 1) * unit
        if not measure['tick'] <= tick < measure['tick'] + measure['ticks']:
            raise ValueError(f'Bar spec {spec!r}: beat {beat:g} is outside the bar')
        return self.at_beat(self.lead + tick / self.division)


if __name__ == '__main__':
    import argparse
    p = argparse.ArgumentParser(description='Print recording seconds for printed bars')
    p.add_argument('bundle')
    p.add_argument('bars', nargs='*', help='bar specs (default: every downbeat)')
    a = p.parse_args()
    clock = BarClock(a.bundle)
    specs = a.bars or [str(m['number']) for m in clock.measures]
    for s in specs:
        print(f'{s:>8}  {clock.seconds(s):9.3f}')
