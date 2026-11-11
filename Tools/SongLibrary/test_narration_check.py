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
# The étude's narration as it is published now (take 0 of a rewritten script, every line read as written), and what
# Whisper heard of it: all 21 lines pass.
ETUDE_NOW = [
    ('Chopin told his pupil Adolph Gutmann that he had never in his life written another such beautiful melody. This is it: Opus 10, Number 3, in E major.',
     'Chopin told his pupil Adolph Gutemann that he had never in his life written another such beautiful melody. This is it. Opus 10, number three in E major.'),
    ('This performance is by Alexei Gorlatch, at the 2009 Leeds International Piano Competition, where he won second prize.',
     'This performance is by Alexei Gorlach at the 2009 Leeds International Piano Competition, where he won second prize.'),
    ('An étude is a study, written to perfect one skill. Listen to the right hand alone.',
     'Anichud is a study written to perfect one skill. Listen to the right hand alone.'),
    ('It plays two parts at once: the weaker outer fingers sing the melody, while the thumb and index finger keep a soft pulse beneath it.',
     'It plays two parts at once. The weaker outer fingers sing the melody, while the thumb and index finger keep a soft pulse beneath it.'),
    ('Now the line climbs, each bar reaching higher. At the top, a single high voice begins to fall.',
     'Now the line climbs, each bar reaching higher. At the top, a single high voice begins to fall.'),
    ('Each phrase falls a fifth: G sharp to C sharp.', 'Each phrase falls a fifth, G sharp to C sharp.'),
    ('The next begins a fourth lower, on D sharp.', 'The next begins a fourth lower on D sharp.'),
    ('Then a third lower, on B.', 'Then a third lower on B.'),
    ('Then a smaller third, on G sharp, and home to E.', 'Then a smaller third on G sharp and home to E.'),
    ("The bridge begins, a little more animated, and the étude's other skill appears: sixths.",
     'The bridge begins, a little more animated, and the etudes other skill appears, sixths.'),
    ('The right hand rocks between two shapes, typically thumb with ring finger, then index with little finger.',
     'The right hand rocks between two shapes, typically thumb with ring finger, then index with little finger.'),
    ('Two swells follow: the first rises forte in A major, and answers itself softly in A minor.',
     'Two swells follow. The first rises forte in A major and answers itself softly in A minor.'),
    ('The second swell climbs a step higher, then darkens into a chain of diminished chords and tritones: notes from opposite sides of the circle of fifths.',
     'The second swell climbs a step higher, then darkens into a chain of diminished chords and tritones, notes from opposite sides of the circle of fifths.'),
    ('Out of the spin, the hands lock into repeating, alternating sixths, the same rocking the étude trains.',
     'Out of the spin, the hands lock into repeating, alternating 6ths, the same rocking the etude trains.'),
    ('It steadies the storm, and the music settles onto the dominant, waiting.', 'It steadies the storm and the music settles onto the dominant, waiting.'),
    ('Its nickname is Tristesse: sadness.', 'Its nickname is Tristes. Sadness.'),
    ('The melody returns, but Chopin shortens the way home.', 'The melody returns, but Chopin shortens the way home.'),
    ('In gold, bars 6 to 13 of the first time through: a second phrase, then the opening again. Watch the return reach the cut.',
     'In gold, bars 6 to 13 of the first time through. A second phrase, then the opening again. Watch the return reach the cut.'),
    ('This time he skips them, straight to the climb.', 'This time he skips them straight to the climb.'),
    ("Beneath the sigh, the chords walk the path of Pachelbel's Canon: each bright major chord answered by a darker minor one. Bittersweet.",
     "Beneath the sigh, the cords walk the path of Pachelbel's canon, each bright major chord answered by a darker minor one. Bitter sweet."),
    ('In the outro the sigh returns an octave lower, darkened by a C natural borrowed from E minor. Twice it sighs; the third time the shadow lifts, and the melody comes home.',
     'In the outro, the sigh returns an octave lower, darkened by a C natural borrowed from E minor. Twice its size. The third time, the shadow lifts and the melody comes home.'),
]
# Lines of ten other narrated tours (ComeFirst, Drank, Ni**as in Paris, SaySo, Sexual, Sorry, Sugar, Touch x Be My Baby,
# Umbrella), which the check's first rules were not written for, as Whisper heard them, and what a listener wants: the
# first six were critical alarms over Whisper's formatting alone (chord symbols, "a major", "90s", "PS1", "0-3").
OTHERS = [
    ("Then the beat drops in. Ryan Riback's remix brings what NYLON called future-nineties house.",
     "Then the beat drops in. Ryan Ribac's remix brings what Nylon called future 90s house.", 'ok'),
    ('The post-chorus drop: kick and snare arrive and the drum wheel fills. Underneath, B seven leans toward E minor, a borrowed dominant the torus marks as a tension.',
     'The post-chorus drop, kick and snare arrive and the drum wheel fills. Underneath, B7 leans toward E minor, a borrowed dominant the Taurus marks his attention.', 'flagged'),
    ('The torus turns back to B-flat. Listen for the last resolution: B-flat seven, the dominant, settling onto E-flat.',
     'The tourist turns back to Bb. Listen for the last resolution. Bb7, the dominant settling onto Eb.', 'ok'),
    ('The outro changes the whole progression. The torus turns to A-flat, and the chords rock between B-flat minor and A-flat, the second and home, with A major sliding in half a step above: the Neapolitan.',
     'The outro changes the whole progression. The torus turns to A-flat and the chords rock between B-flat minor and A-flat, the second in home, with a major sliding in half, a step above the Neapolitan.', 'ok'),
    ('Cashmere Cat first played Be My Baby in public at a DJ set at MoMA P.S. 1 in New York, weeks before the album came out.',
     'Kashmir Cat first played Be My Baby in public at a DJ set at MoMA PS1 in New York, weeks before the album came out.', 'ok'),
    ("Umbrella starts with a stock drum loop from Apple's GarageBand, Vintage Funk Kit 03. Tricky Stewart's team built a number one on it.",
     "Umbrella starts with a stock drum loop from Apple's GarageBand. Vintage Funk Kit, 0-3. Tricky Stewart's team built a number one on it.", 'ok'),
    ('Under Kanye\'s verse the harmony darkens: the analysis hears the home turn from C major to C minor, and the torus follows.',
     "Under Kanye's verse, the harmony darkens. The analysis hears the home turn from C major to C minor, and the chorus follows.", 'ok'),
    # A word heard as a number is still critical: "this verse" may be "this first" said.
    ('On the pattern wheels, this verse plays its loop eight times where the others play four.',
     'On the pattern wheels, this first plays its loop eight times where the others play four.', 'critical'),
    ('In this interlude the drums nearly vanish: barely a strike a bar on the drum wheel.',
     'In this interlude the drums nearly vanish, barely a striker bar on the drum wheel.', 'flagged'),
    ('The chorus is G and D, a fifth apart, rocking back and forth.', 'The chorus is G and D, a fifth apart, rocking back and forth.', 'ok'),
    ('Underneath runs a restrained dembow, the reggaeton pulse. On the drum wheel, the groove keeps changing its details: sixty-four drum families in one song.',
     'Underneath runs a restrained Dembo, the reggaeton pulse. On the drum wheel, the groove keeps changing its details, 64 drum families in one song.', 'flagged'),
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
                           dict(text='The bridge, poco più animato'),
                           dict(text='At the Leeds International Piano Competition, on a Steinway: the piano sings.')])
        names = nc.story_names(story)
        self.assertTrue({'chopin', 'adolph', 'gutmann', 'etude', 'piu', 'leeds', 'steinway'} <= names)
        # Capitalised in a title but written in lower case elsewhere: a word, not a name.
        self.assertFalse({'this', 'the', 'e', 'major', 'piano'} & names)

    def test_the_etude_as_published_passes(self):
        for script, heard in ETUDE_NOW:
            with self.subTest(script=script):
                self.assertEqual(check_line(script, heard)['status'], 'ok')

    def test_other_tours_alarm_only_on_the_voice(self):
        for script, heard, status in OTHERS:
            with self.subTest(script=script):
                self.assertEqual(nc.compare(script.split(), heard, {'kanyes', 'cashmere', 'moma', 'nylon', 'neapolitan'})['status'], status)

    def test_chord_symbols_case_decades_and_digits_as_whisper_writes_them(self):
        keys = lambda text: [t.key for t in nc.tokens(text.split())]
        self.assertEqual(keys('B7 Bb7, Am7 F#m Cmaj7 Gdim Dsus4.'), ['note:b', '#7', 'note:bb', '#7', 'note:a', 'minor', '#7', 'note:f#', 'minor',
                                                                     'note:c', 'major', '#7', 'note:g', 'diminished', 'note:d', 'suspended', '#4'])
        self.assertEqual(keys('A7 resolves. Am I wrong?'), ['note:a', '#7', 'resolves', 'am', 'i', 'wrong'])
        self.assertEqual(keys('the nineteen nineties, the nineties, the 90s, the 90\'s, the eighteen hundreds'),
                         ['the', '#1990s', 'the', '#90s', 'the', '#90s', 'the', '#90s', 'the', '#1800s'])
        self.assertEqual(keys('nineteen oh five, oh three, 03, PS1, P.S. 1'), ['#1905', '#03', '#03', 'ps', '#1', 'ps', '#1'])
        self.assertEqual(keys('the St. Louis Dr. Dre Jr'), ['the', 'saint', 'louis', 'doctor', 'dre', 'junior'])
        for script, heard in [
                ('Underneath, B seven leans toward E minor', 'Underneath, B7 leans toward E minor'),
                ('B-flat seven, the dominant', 'Bb7, the dominant'),
                ('A minor seven, then F sharp minor', 'Am7, then F#m'),
                ('C major seven', 'Cmaj7'),
                ('a D sus four chord', 'a Dsus4 chord'),
                ('with A major sliding in', 'with a major sliding in'),           # case, which Whisper guesses
                ('in A major, and answers itself softly in A minor.', 'in a major, and answers itself softly in a minor.'),
                ('a minor third above', 'A minor third above'),
                ('a major chord', 'the major chord'),
                ('on A sharp', 'on a sharp'),
                ('future-nineties house', 'future 90s house'),
                ('the 1830s', 'the eighteen thirties'),
                ('in nineteen oh five', 'in 1905'),
                ("the '90s", 'the nineties'),
                ('at MoMA P.S. 1 in New York', 'at MoMA PS1 in New York'),
                ('Vintage Funk Kit 03.', 'Vintage Funk Kit, 0-3.'),
                ('Vintage Funk Kit 03.', 'Vintage Funk Kit 3.'),
                ('Vintage Funk Kit 03.', 'Vintage Funk Kit oh three.'),
                ('Oh, one more time', 'Oh one more time'),
                ('bars 6 to 13', 'bars 6-13'),
                ('the first rises forte', 'the first rises forty'),
                ('Watch the torus twist', 'Watch the chorus twist'),
                ('The torus turns back', 'The tourist turns back'),
                ('St. Louis', 'Saint Louis')]:
            with self.subTest(heard=heard):
                self.assertEqual(check_line(script, heard)['issues'], [])
        # Still critical: the note, the number or the chord changed.
        for script, heard, why in [
                ('in E major.', 'in a major.', 'note name'),
                ('and home to E.', 'and home to a.', 'note name'),
                ('B seven', 'B9', 'number'),
                ('B-flat seven', 'B7', 'note name'),
                ('E minor', 'Em7', 'number'),
                ('bars 6 to 13', 'bars 6-30', 'number'),
                ('the nineties', 'the 80s', 'number'),
                ('Kit 03', 'Kit 0-4', 'number')]:
            with self.subTest(heard=heard):
                line = check_line(script, heard)
                self.assertEqual((line['status'], line['issues'][0]['why']), ('critical', why))

    def test_a_sentence_said_twice_is_critical_however_long(self):
        sentence = "Beneath the sigh, the chords walk the path of the old canon each time, slowly."
        line = check_line(sentence, sentence + ' ' + sentence)
        self.assertEqual(line['status'], 'critical')
        self.assertEqual([(i['kind'], i['why'], i['heard']) for i in line['issues']],
                         [('repeated', 'repeated phrase', 'Beneath the sigh the chords walk the path of the old canon each time slowly')])
        twice = 'It plays two parts at once: the weaker outer fingers sing the melody, while the thumb keeps a soft pulse.'
        self.assertEqual(check_line(twice + ' ' + twice, twice + ' ' + twice)['issues'], [])     # as the script has it

    def test_a_name_heard_as_another_name_is_flagged(self):
        names = NAMES | {'leeds', 'international', 'piano', 'competition'}
        for script, heard, why in [
                ('This time Chopin skips them.', 'This time Sopa skips them.', 'name'),
                ('This time Chopin skips them.', 'This time Shopa skips them.', 'name'),
                ('This time Chopin skips them.', 'This time Showpass skips them.', 'name'),
                ('An étude is a study', 'Anichud is a study', 'name')]:
            with self.subTest(heard=heard):
                line = nc.compare(script.split(), heard, names)
                self.assertEqual((line['status'], [t['why'] for t in line['tolerated']]), ('ok', [why]))
        for script, heard in [
                ('This time Chopin skips them.', 'This time Liszt skips them.'),
                ('This time Chopin skips them.', 'This time Schumann skips them.'),
                ('his pupil Adolph Gutmann', 'his pupil Franz Liszt'),
                ('the Leeds International Piano Competition', 'the Chopin International Piano Competition'),
                ('the Leeds International Piano Competition', 'the Leeds International Violin Competition'),
                ('the piano remains restless', 'the guitar remains restless')]:
            with self.subTest(heard=heard):
                line = nc.compare(script.split(), heard, names)
                self.assertEqual((line['status'], line['issues'][0]['why']), ('flagged', 'changed words'))
        self.assertTrue(nc.sounds_alike('gorlatch', 'gorlach') and nc.sounds_alike('elina', 'alina'))
        self.assertFalse(nc.sounds_alike('chopin', 'liszt') or nc.sounds_alike('piano', 'violin') or nc.sounds_alike('', 'a'))


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

    def test_a_fault_in_the_check_exits_2_not_1(self):
        # 1 is a critical difference in the voice; a narration.json the check cannot read is no verdict on it.
        manifest = json.loads((self.bundle/'narration.json').read_text(encoding='utf-8'))
        del manifest['storySha256']
        (self.bundle/'narration.json').write_text(json.dumps(manifest), encoding='utf-8')
        with patch.object(nc, 'whisper') as heard, redirect_stderr(io.StringIO()) as said:
            self.assertEqual(nc.main([str(self.bundle), '--out', str(self.out), '--cache', str(self.cache)]), 2)
        heard.assert_not_called()
        self.assertIn('KeyError', said.getvalue())

    def test_a_repeat_is_timed_from_before_its_first_copy(self):
        # The aligner put "Then two swells." on one copy (here the second, 2.0-3.0 s into the line): from a phrase's
        # length before it both copies are heard; never before the line starts.
        for starts, ends, at in (([2.0, 2.3, 2.6], [2.3, 2.6, 3.0], 4.2+1.0), ([.3, .6, .8], [.6, .8, 1.0], 4.2)):
            manifest = json.loads((self.bundle/'narration.json').read_text(encoding='utf-8'))
            manifest['segments'][1].update(spokenStarts=starts, spokenEnds=ends)
            (self.bundle/'narration.json').write_text(json.dumps(manifest), encoding='utf-8')
            with patch.object(nc, 'whisper', side_effect=self.fake_whisper):
                issue = nc.check(self.bundle, self.out, self.cache)['lines'][1]['issues'][0]
            self.assertEqual((issue['why'], issue['at']), ('repeated phrase', round(at, 2)))


if __name__ == '__main__':
    unittest.main(verbosity=2)
