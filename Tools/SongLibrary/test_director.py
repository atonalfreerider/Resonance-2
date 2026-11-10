"""Bar-based director tours: printed bars to recording seconds, cue timing, and the TypeSafe check's plumbing.

    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/test_director.py
"""
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import build_director_tour
import story
import tour_check
from score_bars import BarClock


def bundle(folder):
    """A 2/4 score with a pickup (bar 0, one eighth) and bars 1-3, after a 4-beat silent lead-in; the recording
    plays the lead-in in 2 s, then every quarter note in 1 s except bar 2, which is taken twice as slowly."""
    folder = Path(folder)
    measures = [dict(index=0, number=0, tick=0, ticks=240, timeSignature=[2, 4]),
                dict(index=1, number=1, tick=240, ticks=960, timeSignature=[2, 4]),
                dict(index=2, number=2, tick=1200, ticks=960, timeSignature=[2, 4]),
                dict(index=3, number=3, tick=2160, ticks=960, timeSignature=[2, 4])]
    (folder/'score-import.json').write_text(json.dumps(dict(division=480, measures=measures)), encoding='utf-8')
    # bundle beats: lead-in 0-4, pickup 4-4.5, bar 1 4.5-6.5, bar 2 6.5-8.5 (slow), bar 3 8.5-10.5
    tempos = [dict(Beat=0, Seconds=0, Microseconds=500000), dict(Beat=4, Seconds=2, Microseconds=1000000),
              dict(Beat=6.5, Seconds=4.5, Microseconds=2000000), dict(Beat=8.5, Seconds=8.5, Microseconds=1000000)]
    (folder/'aligned.mid.patterns.json').write_text(json.dumps(dict(Tempos=tempos, Duration=11.0)), encoding='utf-8')
    (folder/'song.json').write_text(json.dumps(dict(ScoreLeadInBeats=4)), encoding='utf-8')
    return folder


class BarTimes(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.clock = BarClock(bundle(self.tmp.name))

    def tearDown(self):
        self.tmp.cleanup()

    def test_bars_and_beats(self):
        self.assertAlmostEqual(self.clock.seconds('0'), 2.0)
        self.assertAlmostEqual(self.clock.seconds(1), 2.5)
        self.assertAlmostEqual(self.clock.seconds('1:2'), 3.5)
        self.assertAlmostEqual(self.clock.seconds('2'), 4.5)
        self.assertAlmostEqual(self.clock.seconds('2:2'), 6.5)          # the slow bar
        self.assertAlmostEqual(self.clock.seconds('3'), 8.5)
        self.assertAlmostEqual(self.clock.seconds('end'), 10.5)
        with self.assertRaises(ValueError):
            self.clock.seconds('2:3')                                    # 2/4 has two beats
        with self.assertRaises(ValueError):
            self.clock.seconds('2#2')                                    # bar 2 is played once
        with self.assertRaises(ValueError):
            self.clock.seconds('bar 2')

    def test_cues_in_bars_become_seconds(self):
        director = dict(cues=[dict(start=0, to='1', text='a'), dict({'from': '1', 'to': '3'}, text='b', stem='right-hand', soloUntil='2:2'),
                              dict({'from': '3'}, text='c')])
        cues = build_director_tour.timed_cues(director, self.clock)
        self.assertEqual([(c['start'], c['end']) for c in cues], [(0, 2.5), (2.5, 8.5), (8.5, 11.0)])
        self.assertEqual(cues[1]['soloUntil'], 6.5)
        self.assertNotIn('from', cues[1])

    def test_follow_runs_become_knots(self):
        # The second passage (bars 2-3) plays bar 1 again at bar 2, then skips to bar 3 of the first at bar 3.
        compare = dict(first=['1', '3:2'], second=['2', 'end'], follow=[['2', '1', 1], ['3', '2:1', 1]])
        with self.assertRaises(ValueError):
            build_director_tour.timed_cues(dict(cues=[dict(start=0, text='a', compare=compare)]), self.clock)  # 2:1 is not a bar line
        compare['follow'] = [['2', '1', 1], ['3', '2', 1]]
        cue = build_director_tour.timed_cues(dict(cues=[dict(start=0, text='a', compare=compare)]), self.clock)[0]
        self.assertEqual(cue['compareFollow'], [4.5, 2.5, 8.5, 4.5, 8.5, 4.5, 10.5, 8.5])
        self.assertEqual(cue['compareFollowRuns'], [2, 2])
        self.assertEqual(build_director_tour.shift_bar('9#2', 2), '11#2')
        story_cue = dict(cue, view='Torus', stem='', uncoil=False)
        story.validate_cues([story_cue], 11.0, [])
        for broken in (dict(compareFollowRuns=[3, 1]), dict(compareFollow=[4.5, 2.5, 4.4, 4.5, 8.5, 4.5, 10.5, 8.5]),
                       dict(compareFollow=[4.5, 2.5, 8.5, 9.9, 8.5, 4.5, 10.5, 8.5])):
            with self.assertRaises(ValueError):
                story.validate_cues([dict(story_cue, **broken)], 11.0, [])


class Checks(unittest.TestCase):
    def test_claims_and_beats_decisions(self):
        director = dict(title='T', cues=[dict(start=0, end=5, text='x', speech='Chopin wrote it in 1832. Listen closely.', sourceIds=['w']),
                                         dict({'from': '1', 'to': '3'}, text='y', narrate=False)])
        evidence = dict(sources=dict(w=['Composed in 1832.']), beats=[dict(beat='the slow bar', cue='1-3')],
                        windows=[dict(id='slow', bars='2-2', cue='1-3', description='a slow bar'),
                                 dict(id='fast', bars='3-3', cue='3-4', description='a fast bar')])
        answers = iter([
            dict(model='jev-test', answers=dict(factual=dict(type='noul', noul=0.97),
                 relation=dict(type='choice', choice='supports', confidence=0.95, probabilities=dict(supports=0.97, contradicts=0.01, says_nothing=0.02)))),
            dict(model='jev-test', answers=dict(factual=dict(type='noul', noul=0.05),
                 relation=dict(type='choice', choice='says_nothing', confidence=0.9, probabilities=dict(supports=0.02, contradicts=0.01, says_nothing=0.97)))),
            dict(model='jev-test', answers=dict(passage=dict(type='choice', choice='slow', confidence=0.9, probabilities=dict(slow=0.95, fast=0.05)))),
        ])
        jev = tour_check.Jev('jev-test')
        with patch.object(tour_check.Jev, 'ask', side_effect=lambda state, questions: next(answers)):
            claims = tour_check.check_claims(director, evidence, jev)
            beats = tour_check.check_beats(director, evidence, jev)
        self.assertEqual([(c['verdict'], c['review']) for c in claims], [('verified', False), ('interpretation', False)])
        self.assertEqual([(b['chosen'], b['agrees']) for b in beats], [('slow', True)])

    def test_sentences(self):
        self.assertEqual(tour_check.sentences('One. Two! “Three?” Four'), ['One.', 'Two!', '“Three?”', 'Four'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
