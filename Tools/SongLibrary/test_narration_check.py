"""The narration check: what counts as a change, what Whisper may change on its own, the cache and the report.
Whisper itself is never loaded here (narration_check.whisper is replaced).

    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/test_narration_check.py
"""
import io
import json
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import patch
import numpy as np
import soundfile as sf
import narration_check as nc
from common import sha

# Lines of the Chopin étude tour, and what Whisper heard of its first render.
NAMES = {'adolph', 'gutmann', 'gorlatch', 'alexei', 'chopin', 'etude', 'etudes', 'tristesse', 'pachelbels', 'canon'}
HEARD = [
    ('Chopin told his pupil Adolph Gutmann that he had never in his life written another such beautiful melody. This is that melody: the Étude in E major, Opus 10, Number 3.',
     'Chopin told his pupil, Adolf Gutmann, that he had never in his life written another such beautiful melody. This is that melody. The Etude in A Major, Op. 10, No. 3.'),
    ('Then two swells. The first rises forte in A major, and answers itself softly in A minor.',
     'Then two swells. Then two swells. The first rises forte in A major and answers itself softly in A minor.'),
    ('This performance is by Alexei Gorlatch, at the 2009 Leeds International Piano Competition, where he won second prize.',
     'This performance is by Alexei Gorlach at the 2009 Leeds International Piano Competition, where he won second prize.'),
    ('An étude is a study, written to perfect one skill.', 'Anichud is a study written to perfect one skill.'),
    ("The bridge begins, a little more animated, and the étude's other skill appears: sixths.",
     'The bridge begins, a little more animated, and the etudes other skill appears, 6ths.'),
    ('This time Chopin skips them, straight to the climb.', 'This time, Sopa skips them straight to the climb.'),
    ('Its nickname is Tristesse: sadness.', 'Its nickname is Tristes, Sadness.'),
    ("Beneath the sigh, the chords walk the path of Pachelbel's Canon. Bittersweet.",
     "Beneath the sigh, the cords walk the path of Pachelbel's canon. Bitter sweet."),
    ('Twice it sighs; the third time the shadow lifts.', 'Twice its size. The third time, the shadow lifts.'),
    ('In gold, bars 6 to 13 of the first time through: a second phrase.', 'In gold, bars 6 to 13 of the first time through. A second phrase.'),
]


def check_line(script, heard):
    return nc.compare(script.split(), heard, NAMES)


class Compare(unittest.TestCase):
    def test_the_etude_render(self):
        first, swells, *rest = [check_line(s, h) for s, h in HEARD]
        self.assertEqual(first['status'], 'critical')
        self.assertEqual([(i['kind'], i['why'], i['script'], i['heard']) for i in first['issues']], [('changed', 'note name', 'E', 'A')])
        self.assertEqual(swells['status'], 'critical')
        self.assertEqual([(i['kind'], i['why'], i['heard']) for i in swells['issues']], [('repeated', 'repeated phrase', 'Then two swells')])
        for (script, heard), line in zip(HEARD[2:], rest):
            with self.subTest(script=script):
                self.assertEqual((line['status'], line['issues']), ('ok', []))
        why = {(t['script'], t['heard']): t['why'] for line in rest for t in line['tolerated']}
        self.assertEqual(why[('Gorlatch', 'Gorlach')], 'sounds the same')
        self.assertEqual(why[('An etude', 'Anichud')], 'name')
        self.assertEqual(why[('Chopin', 'Sopa')], 'name')
        self.assertEqual(why[('chords', 'cords')], 'sounds the same')
        self.assertEqual(why[('Bittersweet', 'Bitter sweet')], 'spacing')
        self.assertEqual(why[('it sighs', 'its size')], 'sounds the same')

    def test_critical_changes(self):
        for script, heard, why in [
                ('in E major.', 'in E minor.', 'major/minor'),
                ('falls to G sharp.', 'falls to G.', 'note name'),
                ('on D sharp.', 'on D flat.', 'note name'),
                ('bars 6 to 13.', 'bars 6 to 30.', 'number'),
                ('a fourth lower.', 'a fifth lower.', 'number'),
                ('in E major.', 'in a major.', 'note name'),             # the article, not the note A
                ('Opus 10, Number 3.', 'Opus 10, Number 4.', 'number'),
                ('two swells', 'three swells', 'number')]:
            with self.subTest(heard=heard):
                line = check_line(script, heard)
                self.assertEqual(line['status'], 'critical')
                self.assertEqual(line['issues'][0]['why'], why)

    def test_what_whisper_changes_on_its_own(self):
        for script, heard in [
                ('the Étude in E major, Opus 10, Number 3.', 'The Etude in E-Major, Op. 10, No. 3.'),
                ('falls a fifth: G sharp to C sharp.', 'falls a fifth, G♯ to C-sharp.'),
                ('darkened by a C natural', 'darkened by a see natural'),
                ('at the 2009 Leeds competition', 'at the two thousand and nine Leeds competition'),
                ('in eighteen thirty-two', 'in 1832'),
                ('the twenty-first time', 'the 21st time'),
                ('alternating sixths and thirds', 'alternating 6ths and 3rds'),
                ('he won second prize', 'he won 2nd prize'),
                ('bars 6 to 13', 'bars six to thirteen'),
                ('the A′ return', 'the A prime return'),
                ('a well-known colour', 'a well known color'),
                ('bars thirteen, fourteen', 'bars 13, 14'),
                ('a hundred times', 'a 100 times'),
                ('Opus 10, Number 3', 'Opus 10 #3'),
                ('fifty percent', '50%'),
                ('Sources differ on her fee: four dollars, or five.', 'Sources differ on her fee. $4 or $5?'),
                ('on the third of April 1968, with', 'on 3rd April 1968 with'),
                ('gives 1829–1832 as a broad range', 'gives 1829 to 1832 as a broad range'),
                ("Young's love of Blink one eighty-two.", "Young's love of Blink-182."),
                ('a camp-meeting hymn', 'a camp meeting him'),
                ('—it circles, tests', 'Its circles, tests'),
                ('circling an invisible shape', 'circling in invisible shape'),
                ('discovered Ko-li-dee-ann harmony', 'discovered Colidean harmony'),
                ('A second phrase', 'a second phrase')]:
            with self.subTest(heard=heard):
                self.assertEqual(check_line(script, heard)['issues'], [])

    def test_words_missing_or_added_are_flagged_not_critical(self):
        line = check_line('the weaker outer fingers sing the melody', 'the outer fingers sing the melody softly')
        self.assertEqual(line['status'], 'flagged')
        self.assertEqual([(i['kind'], i['critical']) for i in line['issues']], [('missing', False), ('extra', False)])
        line = check_line('the thumb and index finger', 'the thumb the thumb and index finger')
        self.assertEqual(line['issues'][0]['why'], 'repeated phrase')
        self.assertTrue(line['issues'][0]['critical'])
        self.assertEqual(check_line('it sighs and sighs', 'it sighs and sighs')['issues'], [])
        # The script's own repetition is no issue; a word said once more than written is a flag.
        self.assertEqual(check_line('very, very slowly', 'very very slowly')['issues'], [])
        line = check_line('very slowly', 'very very slowly')
        self.assertEqual((line['status'], line['issues'][0]['why']), ('flagged', 'repeated word'))
        self.assertEqual(check_line('Chopin', '')['issues'][0]['kind'], 'missing')

    def test_numbers_and_notes(self):
        keys = lambda text: [t.key for t in nc.tokens(text.split())]
        self.assertEqual(keys('two thousand and nine'), ['#2009'])
        self.assertEqual(keys('eighteen thirty-two'), ['#1832'])
        self.assertEqual(keys('two three'), ['#2', '#3'])
        self.assertEqual(keys('one hundred twenty-three'), ['#123'])
        self.assertEqual(keys('sixths, 6ths, a fifth'), ['#6ths', '#6ths', 'a', '#5th'])
        self.assertEqual(keys('Op. 10, No. 3, no doubt'), ['opus', '#10', 'number', '#3', 'no', 'doubt'])
        self.assertEqual(keys('G♯ to C-sharp, B♭, E flat, F natural'), ['note:g#', 'to', 'note:c#', 'note:bb', 'note:eb', 'note:fn'])
        self.assertEqual(keys('A sharp contrast. A major chord, in A.'), ['note:a#', 'contrast', 'note:a', 'major', 'chord', 'in', 'note:a'])
        self.assertEqual(keys('A study. a study'), ['a', 'study', 'a', 'study'])
        self.assertEqual(keys('[softly, slowly] said once'), ['said', 'once'])

    def test_names_from_the_story(self):
        story = dict(cues=[dict(text='x', speech='Chopin told his pupil Adolph Gutmann. This time Chopin skips the Étude in E major.'),
                           dict(text='The bridge, poco più animato')])
        names = nc.story_names(story)
        self.assertTrue({'chopin', 'adolph', 'gutmann', 'etude', 'piu'} <= names)
        self.assertFalse({'this', 'the', 'e', 'major'} & names)


def bundle(folder, speech, take=0):
    """A bundle with a story of three spoken lines, its narration WAV (a tone in each line's place) and narration.json."""
    folder = Path(folder)
    folder.mkdir()
    cues = [dict(start=0, end=4, text='a', speech=speech[0]), dict(start=4, end=8, text='b', speech=speech[1], take=take),
            dict(start=8, end=12, text='c', narrate=False), dict(start=12, end=16, text='d', speech=speech[2])]
    (folder/'story.json').write_text(json.dumps(dict(cues=cues)), encoding='utf-8')
    rate, signal = 24000, np.zeros(16*24000, dtype='float32')
    segments = []
    for i, (start, end) in ((0, (.2, 3.5)), (1, (4.2, 7.1)), (3, (12.2, 15))):
        signal[int(start*rate):int(end*rate)] = .1*np.sin(np.arange(int(end*rate)-int(start*rate))*(i+1)/9)
        segments.append(dict(cue=i, start=start, end=end, duck=.4))
    (folder/'narration').mkdir(exist_ok=True)
    sf.write(folder/'narration/narration.wav', signal, rate, subtype='PCM_16')
    segments[0].update(spokenStarts=[.1, .5, .9, 1.3, 1.7, 2.1, 2.5], spokenEnds=[.4, .8, 1.2, 1.6, 2.0, 2.4, 3.0])
    (folder/'narration.json').write_text(json.dumps(dict(storySha256=sha(folder/'story.json'), sha256=sha(folder/'narration/narration.wav'),
                                                         audioPath='narration/narration.wav', segments=segments)), encoding='utf-8')
    return folder


class Check(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.speech = ['This is the Étude in E major.', 'Then two swells.', 'The melody returns.']
        self.bundle = bundle(root/'bundle', self.speech)
        self.out, self.cache = root/'report', root/'cache'
        self.heard = {0.2: 'This is the Etude in A Major.', 4.2: 'Then two swells. Then two swells.', 12.2: 'The melody returns.'}

    def tearDown(self):
        self.tmp.cleanup()

    def fake_whisper(self, clips, device='auto'):
        # Each line's tone says which line it is: its length in 16 kHz samples.
        by_length = {round((end-start)*16000): self.heard[start] for start, end in ((.2, 3.5), (4.2, 7.1), (12.2, 15))}
        return [by_length[min(by_length, key=lambda n: abs(n-len(c)))] for c in clips]

    def test_report_cache_and_exit_status(self):
        with patch.object(nc, 'whisper', side_effect=self.fake_whisper) as heard, redirect_stdout(io.StringIO()) as shown:
            self.assertEqual(nc.main([str(self.bundle), '--out', str(self.out), '--cache', str(self.cache)]), 1)
            self.assertEqual(len(heard.call_args.args[0]), 3)
            report = json.loads((self.out/'narration-check.json').read_text(encoding='utf-8'))
            self.assertEqual((report['critical'], report['flagged'], report['heardNow']), ([0, 1], [], 3))
            first = report['lines'][0]
            self.assertEqual((first['issues'][0]['why'], first['issues'][0]['at']), ('note name', round(.2+2.1, 2)))  # "E" is word 5
            self.assertIsNone(report['lines'][1]['issues'][0]['at'])                    # no aligned words for that line
            text = (self.out/'narration-check.txt').read_text(encoding='utf-8')
            self.assertIn(text, shown.getvalue())
            self.assertIn('"E" heard as "A" at 0:02.3', text)
            self.assertIn('"Then two swells" said twice', text)
            self.assertIn('"take": 1 on cue 1', text)
            # Every clip is now cached: the model is not loaded again.
            heard.reset_mock()
            self.assertEqual(nc.check(self.bundle, self.out, self.cache)['heardNow'], 0)
            heard.assert_not_called()

    def test_a_new_take_is_heard_again_and_a_clean_render_passes(self):
        with patch.object(nc, 'whisper', side_effect=self.fake_whisper):
            nc.check(self.bundle, self.out, self.cache)
        # A new take of line 1 changes only its clip: it alone is heard again.
        self.heard.update({0.2: 'This is the Étude in E major.', 4.2: 'Then two swells.'})
        signal, rate = sf.read(self.bundle/'narration/narration.wav', dtype='float32')
        signal[int(4.2*rate):int(7.1*rate)] *= .5
        sf.write(self.bundle/'narration/narration.wav', signal, rate, subtype='PCM_16')
        manifest = json.loads((self.bundle/'narration.json').read_text(encoding='utf-8'))
        manifest['sha256'] = sha(self.bundle/'narration/narration.wav')
        (self.bundle/'narration.json').write_text(json.dumps(manifest), encoding='utf-8')
        with patch.object(nc, 'whisper', side_effect=self.fake_whisper) as heard:
            report = nc.check(self.bundle, self.out, self.cache)
        self.assertEqual(len(heard.call_args.args[0]), 1)
        self.assertEqual((report['heardNow'], report['critical'], report['lines'][1]['status']), (1, [0], 'ok'))

    def test_a_stale_narration_is_not_checked(self):
        (self.bundle/'story.json').write_text('{"cues": []}', encoding='utf-8')
        with patch.object(nc, 'whisper') as heard, redirect_stderr(io.StringIO()) as said:
            self.assertEqual(nc.main([str(self.bundle), '--out', str(self.out), '--cache', str(self.cache)]), 2)
        heard.assert_not_called()
        self.assertIn('render it again', said.getvalue())


if __name__ == '__main__':
    unittest.main(verbosity=2)
