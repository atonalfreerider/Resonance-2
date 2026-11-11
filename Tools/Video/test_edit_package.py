"""Editor packages, checked against takes made without the exporter.

    Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/test_edit_package.py

A fake bundle (a recording and its right-hand stem, written from sums of sinusoids at random frequencies, so their
correlation has one clear peak; a story with stem solos; narration with measured ducks) and fake takes whose sound
this file renders itself, the way the app's engine makes it: once a video frame, SongDirector picks the cue and its
solo, SongNarration eases the music gain and StemPlayback moves the volumes, then the mixer renders whole 1024-sample
blocks with them. The music file starts off the 8 kHz grid, the mono voice plays centre-panned at -3 dB and 39 ms
ahead of it (as an MP3 bundle plays: NAudio keeps the encoder delay), and in the first take the volumes are set in
Update, before the director and the narration have decided the frame (a frame late, as takes did before
report.duckLagFrames existed). Nothing here comes from edit_package's own model: the package has to find what the
take did on its own.
"""
import csv
import json
import hashlib
import math
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.parse import unquote

import numpy as np
import soundfile as sf

import edit_package as ep

SR, SONG, FPS, RATE, BLOCK = 44100, 10.0, 30, 48000, 1024
START = .70031                    # the music file's first sample in the video: not on the exporter's 8 kHz grid
LEAD = 1729/44100                 # the voice (the song clock) sits this far ahead of the music file
CLOCK = START-LEAD
MASTER, PAN = .8, math.sqrt(.5)
RNG = np.random.default_rng(11)


def partials(count, lo, hi, scale):
    f = np.exp(RNG.uniform(math.log(lo), math.log(hi), count))
    return f, scale*RNG.uniform(.4, 1, count)/np.sqrt(f/lo), RNG.uniform(0, 2*np.pi, count)


LEFT, RIGHT, VOICE, OTHER = partials(24, 60, 1800, .06), partials(24, 300, 5000, .05), partials(20, 150, 3500, .05), partials(20, 160, 3400, .05)


def tone(t, spec, am):
    f, a, phase = spec
    out = np.zeros(len(t))
    for fi, ai, pi in zip(f, a, phase):
        out += ai*np.sin(2*np.pi*fi*t+pi)
    return out*(.6+.4*np.sin(2*np.pi*am*t)**2)


def music(t):
    """(left-hand part, right-hand part) of the recording at song times t, stereo: the hands sit a little apart."""
    inside = ((t >= 0) & (t < SONG)).astype(float)
    left, right = tone(t, LEFT, .7)*inside, tone(t, RIGHT, .45)*inside
    return np.c_[left, .8*left], np.c_[.8*right, right]


def speech(t, segments, spec=VOICE):
    """The narrator at song times t: syllable-shaped bursts over each line, silent between them."""
    out = np.zeros(len(t))
    for s in segments:
        w = (t >= s['start']) & (t < s['end'])
        x = t[w]
        edge = np.clip(np.minimum(x-s['start'], s['end']-x)/.01, 0, 1)
        out[w] = tone(x, spec, 0)*(.5+.5*np.sin(2*np.pi*4.1*x+1.3)**2)*(.6+.4*np.sin(2*np.pi*1.7*x))*edge
    return out


CUES = [dict(start=0., end=4., view='Torus', stem='', text='The étude opens.', speech='Chopin wrote this Étude in E major, [pause] Opus 10, Number 3, in 1832.'),
        dict(start=4., end=7., view='Torus', stem='right-hand', soloGain=.1, releaseSoloAfterNarration=True, text='Right hand alone',
             speech='[calm] Now the right hand alone.'),
        dict(start=7., end=SONG, view='Torus', stem='right-hand', soloUntil=8.5, narrate=False, text='Right hand, then all')]
SEGMENTS = [dict(cue=0, start=.5, end=3.2, duck=.3), dict(cue=1, start=4.3, end=5.6, duck=.5)]
SPOKEN_STARTS = [0., .3, .45, .6, .95, 1.1, 1.2, 1.3, 1.4, 1.6, 1.85, 2.05, 2.3, 2.5]      # one per spoken word of line 1


RENDERED = {}


def render(seconds, start=START, song_start=0., lag=1, narrated=True, voice=VOICE, lead=LEAD):
    key = (seconds, start, song_start, lag, narrated, id(voice), lead)
    if key not in RENDERED:
        RENDERED[key] = engine(seconds, start, song_start, lag, narrated, voice, lead)
    return RENDERED[key]


def engine(seconds, start, song_start, lag, narrated, voice, lead):
    """A take's sound as the app's engine makes it, and what went into it (the truth the package is checked
    against): the music file's sample 0 at video time `start`, the song clock `lead` earlier. Frame k's Update sees the
    DSP clock at the samples mixed so far; the mixer then renders whole blocks up to the frame's end with the volumes
    then set."""
    clock = start-lead
    frames, n = int(seconds*FPS), int(seconds*FPS)*RATE//FPS
    t = np.arange(n)/RATE
    left, right = music(t-start)
    playing_from = int(math.ceil((clock+song_start)*RATE))          # PlayScheduled: the song starts on this sample
    left[:playing_from] = right[:playing_from] = 0
    talk = speech(t-clock, SEGMENTS if narrated else [], voice)
    talk[:playing_from] = 0
    state = dict(selected='', solo=1., g=1.)
    mix, played, env = np.zeros((n, 2)), np.zeros((n, 2)), np.zeros(frames)
    events = {}
    mixed = 0

    def cue_at(now):
        return next((i for i, c in enumerate(CUES) if c['start'] <= now < c['end']), -1)

    def finished(i, now):
        return not narrated or not any(s['cue'] == i and now < s['end'] for s in SEGMENTS)

    def stem_playback():                       # StemPlayback: the crossfade, and every volume with the duck
        step = (1/FPS)/.45
        move = lambda x, to: to if abs(to-x) <= step else x+math.copysign(step, to-x)
        state['master'] = move(state['master'], 0. if state['selected'] else 1.)
        state['stem'] = move(state['stem'], 1. if state['selected'] == 'right-hand' else 0.)
        solo = min(4., max(.25, state['solo'])) if state['selected'] == 'right-hand' else 1.
        state['volumes'] = (state['master'], state['stem']*solo, state['g'])

    def director(now):
        i = cue_at(now)
        if i < 0:
            return
        c = CUES[i]
        release = (c.get('soloUntil', 0) > 0 and now >= c['soloUntil']) or (c.get('releaseSoloAfterNarration') and finished(i, now))
        state['selected'] = '' if release else c['stem']
        state['solo'] = c['soloGain'] if not release and c['stem'] and c.get('soloGain', 0) > 0 else 1.

    def narration(k, now, playing):
        i = cue_at(now)
        released = i >= 0 and CUES[i].get('releaseSoloAfterNarration') is True and finished(i, now)
        duck = 1.
        if narrated and playing and not released:
            for j, s in enumerate(SEGMENTS):
                if s['start']-.12 <= now < s['end']+.15:
                    duck = min(duck, s['duck'])
                    events.setdefault(('dip', j), k)
        if released:
            events.setdefault(('released', i), k)
        rate = 16 if duck < 1 else 50 if released else 4
        state['g'] += (duck-state['g'])*(1-math.exp(-rate/FPS))

    director(song_start)                                              # paused at the seek point: its solo already in
    state['master'], state['stem'] = (0., 1.) if state['selected'] else (1., 0.)
    stem_playback()
    for k in range(frames):
        position = mixed/RATE-clock                                   # midi.Position in this frame's Update
        playing = position >= song_start
        now = position if playing else song_start
        if lag:
            stem_playback()                                           # Update: before the director and the narration
        director(now)
        narration(k, now, playing)
        if not lag:
            stem_playback()                                           # LateUpdate: after them
        rec, stem, g = state['volumes']
        env[k] = g
        events[('block', k)] = mixed
        while mixed+BLOCK <= (k+1)*RATE//FPS:
            b = slice(mixed, mixed+BLOCK)
            played[b] = rec*(left[b]+right[b])+stem*right[b]
            mix[b] = MASTER*g*played[b]+(.95*MASTER*PAN*talk[b])[:, None]
            mixed += BLOCK
    b = slice(mixed, n)
    played[b] = rec*(left[b]+right[b])+stem*right[b]
    mix[b] = MASTER*g*played[b]+(.95*MASTER*PAN*talk[b])[:, None]
    return mix, dict(played=played, recording=left+right, voice=talk, env=env, events=events, frames=frames, clock=clock)


def make_bundle(root, voice=VOICE):
    bundle = root/'Song'
    (bundle/'stems').mkdir(parents=True)
    (bundle/'narration/x').mkdir(parents=True)
    left, right = music(np.arange(int(SONG*SR))/SR)
    sf.write(bundle/'recording.wav', left+right, SR, subtype='FLOAT')
    sf.write(bundle/'stems/right-hand.wav', right, SR, subtype='FLOAT')
    sf.write(bundle/'narration/x/narration.wav', speech(np.arange(int(SONG*24000))/24000, SEGMENTS, voice), 24000, subtype='FLOAT')
    words, ends = CUES[0]['speech'].split(), SPOKEN_STARTS[1:]+[2.7]      # as story_timing.py writes a line's alignment
    segments = [dict(SEGMENTS[0], spokenWords=words, spokenStarts=SPOKEN_STARTS, spokenEnds=ends, wordStarts=SPOKEN_STARTS, wordEnds=ends), SEGMENTS[1]]
    (bundle/'story.json').write_text(json.dumps(dict(title='A Test Tour', duration=SONG, cues=CUES), ensure_ascii=False), encoding='utf-8')
    audio = hashlib.sha256((bundle/'narration/x/narration.wav').read_bytes()).hexdigest()
    (bundle/'narration.json').write_text(json.dumps(dict(audioPath='narration/x/narration.wav', sha256=audio, segments=segments)), encoding='utf-8')
    (bundle/'aligned.mid.prepared.json').write_text(json.dumps(dict(audioPath='recording.wav', stems=[dict(id='right-hand', audioPath='stems/right-hand.wav')])), encoding='utf-8')
    return bundle


def make_take(folder, name, mix, seconds):
    """An MP4 as the Recorder writes one: H.264 at 30 fps and the sound as AAC."""
    wav = folder/(name+'.wav')
    sf.write(wav, mix, RATE, subtype='FLOAT')
    video = folder/(name+'.mp4')
    subprocess.run(['ffmpeg', '-v', 'error', '-y', '-f', 'lavfi', '-i', f'color=c=black:s=320x568:r={FPS}:d={seconds}', '-i', str(wav),
                    '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '320k', '-shortest', str(video)], check=True)
    wav.unlink()
    return video


def report(video, bundle, truth, **extra):
    markers, cue = [], -1
    for k in range(truth['frames']):                                  # the frame each cue became current
        now = truth['events'][('block', k)]/RATE-truth['clock']
        i = next((i for i, c in enumerate(CUES) if c['start'] <= max(now, extra.get('songStart', 0)) < c['end']), -1)
        if i >= 0 and i != cue:
            cue = i
            markers.append(dict(id=f'cue-{i}', title=CUES[i]['text'], start=k/FPS, end=0.))
    data = dict(kind='tour', song='A Test Tour', bundle=str(bundle), video=video.name, fps=FPS, audioStart=START+.0107, markers=markers)
    data.update(extra)
    path = video.with_suffix('.json')
    path.write_text(json.dumps(data, ensure_ascii=False), encoding='utf-8')
    return path


def snr(reference, estimate):
    return 10*math.log10(float(np.sum(reference**2))/max(1e-20, float(np.sum((reference-estimate)**2))))


def run(report_path):
    return subprocess.run([sys.executable, str(Path(ep.__file__)), str(report_path)], capture_output=True, text=True)


class Take(unittest.TestCase):
    """A take from the top, recorded before report.duckLagFrames: everything a frame late."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.tmp.name)/'Take #1 50% & Étude [x]'           # characters a pathurl has to escape
        cls.root.mkdir()
        cls.bundle = make_bundle(cls.root)
        mix, cls.truth = render(12.)
        cls.video = make_take(cls.root, 'a-test-tour-vertical-20261110-120000', mix, 12.)
        cls.report = report(cls.video, cls.bundle, cls.truth)
        cls.out, cls.check = ep.build(cls.report)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_finds_the_music_and_the_voice_each_to_a_fraction_of_a_sample(self):
        self.assertAlmostEqual(self.check['audioStart'], START, delta=.25/RATE)
        self.assertAlmostEqual(self.check['voiceStart'], CLOCK, delta=.25/RATE)
        self.assertAlmostEqual(self.check['voiceShiftMs'], -LEAD*1000, delta=.01)

    def test_rebuilds_the_take(self):
        self.assertTrue(self.check['matchesTake'], self.check['problems'])
        self.assertGreater(self.check['reproductionSnrDb'], 30)
        self.assertAlmostEqual(self.check['musicLevel'], MASTER, delta=.01)
        self.assertAlmostEqual(self.check['narrationRelativeToMusic'], .95*PAN, delta=.01)
        for line in self.check['duckingMeasured']:
            self.assertAlmostEqual(line['measured'], line['model'], delta=.02)

    def test_the_parts_are_what_the_take_played(self):
        played, _ = sf.read(self.out/'music-as-played.wav', dtype='float64')
        self.assertGreater(snr(self.truth['played'], played), 40)       # soloGain clamped to 0.25, 1x as the solo fades
        original, _ = sf.read(self.out/'music.wav', dtype='float64')
        self.assertGreater(snr(self.truth['recording'], original), 40)
        voice, _ = sf.read(self.out/'narration.wav', dtype='float64')
        self.assertGreater(snr(self.truth['voice'], voice[:, 0]), 40)
        with open(self.out/'ducking-envelope.csv', encoding='utf-8-sig') as f:
            env = [float(r['music_gain']) for r in csv.DictReader(f)]
        self.assertEqual(len(env), self.truth['frames'])
        self.assertLess(np.max(np.abs(np.array(env)-self.truth['env'])), 1e-4)

    def test_every_file_spans_the_picture_at_48k_and_the_music_cannot_clip(self):
        for name in ('narration.wav', 'music.wav', 'music-as-played.wav'):
            info = sf.info(self.out/name)
            self.assertEqual((info.samplerate, info.frames), (RATE, 12*RATE))
        self.assertEqual(sf.info(self.out/'music.wav').subtype, 'FLOAT')

    def test_ducking_cues_follow_the_take(self):
        with open(self.out/'ducking.csv', encoding='utf-8-sig') as f:
            rows = list(csv.DictReader(f))
        self.assertEqual(list(rows[0])[:8], ['line', 'cue', 'voice_start_s', 'voice_end_s', 'duck_starts_s', 'duck_reached_s', 'release_starts_s', 'release_done_s'])
        self.assertEqual([(r['cue'], r['music_gain']) for r in rows], [('0', '0.3'), ('1', '0.5')])
        self.assertAlmostEqual(float(rows[0]['voice_start_s']), CLOCK+.5, delta=.001)
        block = self.truth['events']
        dip = block[('block', block[('dip', 0)]+1)]/RATE                # decided in one frame, heard from the next
        self.assertAlmostEqual(float(rows[0]['duck_starts_s']), dip, delta=.001)
        released = block[('block', block[('released', 1)]+1)]/RATE      # cue 1 releases its solo at its line's end
        self.assertAlmostEqual(float(rows[1]['release_starts_s']), released, delta=.001)
        self.assertLess(float(rows[1]['release_done_s'])-float(rows[1]['release_starts_s']), .1)     # at 50/s, not 4/s

    def test_captions(self):
        raw = (self.out/'captions.srt').read_bytes()
        self.assertTrue(raw.startswith(b'\xef\xbb\xbf'))
        self.assertNotIn(b'\n', raw.replace(b'\r\n', b''))
        blocks = raw[3:].decode('utf-8').strip().split('\r\n\r\n')
        first = blocks[0].split('\r\n')
        self.assertEqual(first[0], '1')
        self.assertEqual(first[1][:12], ep.stamp(CLOCK+.5))              # the line's first aligned word
        self.assertIn('Étude', raw.decode('utf-8-sig'))
        self.assertTrue(all(len(b.split('\r\n')) <= 4 and all(len(l) <= 42 for l in b.split('\r\n')[2:]) for b in blocks))
        said = raw.decode('utf-8-sig').replace('\r\n', ' ')
        self.assertIn('Now the right hand alone.', said)
        self.assertIn('major, Opus 10', said)
        self.assertNotIn('[', said)                                      # no [direction], leading or inside a line

    def test_timeline_imports_as_stereo_pairs(self):
        root = ET.parse(self.out/'timeline.xml').getroot()
        seq = root.find('sequence')
        self.assertEqual(seq.findtext('timecode/string'), '00:00:00:00')
        tracks = seq.findall('media/audio/track')
        names = [t.findtext('clipitem/name') for t in tracks]
        self.assertEqual(names, ['MP4 mix (reference)']*2+['Music as played (ducked)']*2+['Narration']*2+['Original recording (muted)']*2)
        self.assertEqual([t.findtext('clipitem/sourcetrack/trackindex') for t in tracks], ['1', '2']*4)
        self.assertEqual([t.findtext('outputchannelindex') for t in tracks], ['1', '2']*4)
        for t in tracks[:2]+tracks[6:]:                                  # the references are muted on the clip itself
            self.assertEqual((t.findtext('enabled'), t.findtext('clipitem/enabled')), ('FALSE', 'FALSE'))
        items = {c.get('id'): c for c in root.iter('clipitem')}
        for t in tracks:
            item = t.find('clipitem')
            linked = {l.findtext('linkclipref') for l in item.findall('link')}
            self.assertTrue(all(ref in items for ref in linked))
            self.assertEqual(len([r for r in linked if r != 'clipitem-video']), 2)
        self.assertIn('clipitem-video', {l.findtext('linkclipref') for l in tracks[0].find('clipitem').findall('link')})
        keys = [float(k.findtext('value')) for k in tracks[2].findall('.//keyframe')]
        self.assertAlmostEqual(min(keys), .3*MASTER, delta=.01)           # the take's own music level, ducked
        self.assertAlmostEqual(float(tracks[4].findtext('.//effect/parameter/value')), .95*PAN*MASTER, delta=.01)
        self.assertEqual(sf.info(self.out/'music.wav').subtype, 'FLOAT')
        depths = {f.findtext('name'): f.findtext('media/audio/samplecharacteristics/depth') for f in root.iter('file') if f.findtext('name')}
        self.assertEqual((depths['music.wav'], depths['narration.wav'], depths[self.video.name]), ('32', '24', '16'))
        for f in root.iter('pathurl'):
            url = f.text
            self.assertTrue(url.isascii() and url.startswith('file://localhost/'), url)
            self.assertTrue(Path(unquote(url[len('file://localhost/'):])).exists(), url)
        self.assertEqual([m.findtext('name') for m in seq.findall('marker')], [c['text'] for c in CUES])

    def test_readme_names_what_is_there(self):
        text = (self.out/'README.txt').read_text(encoding='utf-8-sig')
        self.assertNotIn('WARNING', text)
        for name in ('narration.wav', 'music.wav', 'music-as-played.wav', 'ducking.csv', 'captions.srt', 'timeline.xml'):
            self.assertIn(name, text)
        self.assertIn('A5-A6', text)

    def test_a_changed_story_gets_no_package(self):
        video = self.root/'changed-story-vertical-20261110-120100.mp4'
        shutil.copy(self.video, video)
        path = report(video, self.bundle, self.truth, storySha256='0'*64)
        result = run(path)
        self.assertEqual(result.returncode, 2, result.stderr[-800:])
        self.assertIn('story.json changed', json.loads(result.stdout)['refused'])
        self.assertEqual(list(self.root.glob('changed-story*-edit*')), [])


class Gate(unittest.TestCase):
    """The narration was regenerated after the take: the voice in the MP4 is in no file. The package must say so,
    exit 2, keep the narration audible in its timeline, and leave an earlier package that matched in place."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        bundle = make_bundle(root, voice=OTHER)
        mix, truth = render(12.)
        video = make_take(root, 'regenerated-vertical-20261110-120200', mix, 12.)
        cls.report = report(video, bundle, truth)
        cls.earlier = video.with_name(video.stem+'-edit')
        cls.earlier.mkdir()
        (cls.earlier/'check.json').write_text(json.dumps(dict(kind='tour', reproductionSnrDb=26.6, matchesTake=True)), encoding='utf-8')
        cls.result = run(cls.report)
        cls.failed = video.with_name(video.stem+'-edit-failed')

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_exits_2_and_says_why(self):
        self.assertEqual(self.result.returncode, 2, self.result.stdout[-800:]+self.result.stderr[-800:])
        check = json.loads((self.failed/'check.json').read_text(encoding='utf-8'))
        self.assertFalse(check['matchesTake'])
        self.assertTrue(any('voice' in p for p in check['problems']), check['problems'])
        self.assertTrue((self.failed/'README.txt').read_text(encoding='utf-8-sig').split('\n', 3)[3].startswith('WARNING'))

    def test_keeps_the_earlier_package_that_matched(self):
        self.assertEqual(sorted(p.name for p in self.earlier.iterdir()), ['check.json'])

    def test_timeline_keeps_the_nominal_balance(self):
        root = ET.parse(self.failed/'timeline.xml').getroot()
        level = [float(t.findtext('.//effect/parameter/value')) for t in root.findall('./sequence/media/audio/track') if t.findtext('clipitem/name') == 'Narration']
        self.assertAlmostEqual(level[0], .95*PAN, delta=.001)


class MidSong(unittest.TestCase):
    """A take that starts at its first cue, mid-song (report.songStart), recorded with the volumes set in LateUpdate
    (duckLagFrames 0), naming the story and narration it played."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        cls.bundle = make_bundle(root)
        cls.song_start = 3.
        mix, cls.truth = render(8., start=START-cls.song_start, song_start=cls.song_start, lag=0)
        video = make_take(root, 'mid-song-vertical-20261110-120300', mix, 8.)
        narration = json.loads((cls.bundle/'narration.json').read_text(encoding='utf-8'))
        cls.report = report(video, cls.bundle, cls.truth, songStart=cls.song_start, audioStart=START-cls.song_start+.0107, duckLagFrames=0,
                            storySha256=hashlib.sha256((cls.bundle/'story.json').read_bytes()).hexdigest(),
                            narrationSha256=narration['sha256'], narrationPlayed=True)
        cls.result = run(cls.report)
        cls.out = video.with_name(video.stem+'-edit')
        cls.check = json.loads((cls.out/'check.json').read_text(encoding='utf-8'))

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_lines_up_and_rebuilds(self):
        self.assertEqual(self.result.returncode, 0, self.result.stdout[-800:]+self.result.stderr[-800:])
        self.assertAlmostEqual(self.check['audioStart'], START-self.song_start, delta=.25/RATE)     # negative: before the video
        self.assertAlmostEqual(self.check['voiceStart'], CLOCK-self.song_start, delta=.25/RATE)
        self.assertGreater(self.check['reproductionSnrDb'], 30)
        self.assertEqual(self.check['narration'], 'the report names the narration the take played')

    def test_nothing_before_the_take_started(self):
        first = int(round((CLOCK)*RATE))                                 # song time 3.0, on the song clock
        voice, _ = sf.read(self.out/'narration.wav', dtype='float64')
        music, _ = sf.read(self.out/'music.wav', dtype='float64')
        self.assertEqual(float(np.max(np.abs(voice[:first-40]))), 0.)
        self.assertEqual(float(np.max(np.abs(music[:first-40]))), 0.)
        self.assertGreater(snr(self.truth['voice'], voice[:, 0]), 40)
        self.assertGreater(snr(self.truth['recording'], music), 40)
        with open(self.out/'ducking-envelope.csv', encoding='utf-8-sig') as f:
            env = [float(r['music_gain']) for r in csv.DictReader(f)]
        self.assertLess(np.max(np.abs(np.array(env)-self.truth['env'])), 1e-4)

    def test_captions_and_cues_start_with_the_take(self):
        text = (self.out/'captions.srt').read_text(encoding='utf-8-sig')
        self.assertTrue(text.startswith('1\n00:00:00,'))                  # line 1 is mid-way when the take starts
        with open(self.out/'ducking.csv', encoding='utf-8-sig') as f:
            rows = list(csv.DictReader(f))
        self.assertGreaterEqual(float(rows[0]['voice_start_s']), CLOCK-.001)


class NoNarration(unittest.TestCase):
    """The narration was off as the take started (report.narrationSha256 ""): no voice, no ducking, and the cue that
    releases its solo after the narration releases it at once. With no voice to find the song clock by, the package
    keeps the music's: a WAV bundle's (an MP3 bundle, whose clocks differ, cannot solo its stems in the app)."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        bundle = make_bundle(root)
        mix, cls.truth = render(8., narrated=False, lag=0, lead=0.)
        video = make_take(root, 'no-narration-vertical-20261110-120400', mix, 8.)
        path = report(video, bundle, cls.truth, songStart=0., audioStart=START+.0107, duckLagFrames=0, narrationSha256='', narrationPlayed=False,
                      storySha256=hashlib.sha256((bundle/'story.json').read_bytes()).hexdigest())
        cls.out, cls.check = ep.build(path)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_only_the_music(self):
        self.assertTrue(self.check['matchesTake'], self.check['problems'])
        self.assertGreater(self.check['reproductionSnrDb'], 30)
        self.assertEqual(sorted(p.name for p in self.out.iterdir()), ['README.txt', 'check.json', 'music-as-played.wav', 'music.wav', 'timeline.xml'])
        played, _ = sf.read(self.out/'music-as-played.wav', dtype='float64')
        self.assertGreater(snr(self.truth['played'], played), 40)


class NoSound(unittest.TestCase):
    """A take whose song never started (a silent MP4) or whose MP4 has no audio at all: a package placed from the
    report's times, saying it could not be checked, and exit 2; no crash."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        bundle = make_bundle(root)
        _, truth = render(12.)
        cls.results = {}
        for name, source, codec in (('silent', ['-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo'], ['-c:a', 'aac']), ('mute', [], ['-an'])):
            video = root/f'{name}-vertical-20261110-120500.mp4'
            subprocess.run(['ffmpeg', '-v', 'error', '-y', '-f', 'lavfi', '-i', f'color=c=black:s=320x568:r={FPS}:d=6', *source,
                            '-c:v', 'libx264', '-pix_fmt', 'yuv420p', *codec, '-t', '6', str(video)], check=True)
            cls.results[name] = (run(report(video, bundle, truth)), video.with_name(video.stem+'-edit'))

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_says_it_could_not_check(self):
        for name, (result, out) in self.results.items():
            self.assertEqual(result.returncode, 2, result.stderr[-800:])
            check = json.loads((out/'check.json').read_text(encoding='utf-8'))
            self.assertFalse(check['matchesTake'])
            self.assertIn('silent' if name == 'silent' else 'no sound', check['problems'][0])
            self.assertTrue((out/'narration.wav').exists() and (out/'timeline.xml').exists())
            items = [t.findtext('clipitem/name') for t in ET.parse(out/'timeline.xml').getroot().findall('./sequence/media/audio/track')]
            self.assertEqual('MP4 mix (reference)' in items, name == 'silent')


class Captions(unittest.TestCase):
    def test_never_more_than_two_lines_of_42(self):
        words = ('Once, while Gutmann was studying it, Chopin lifted his clasped hands and exclaimed: '
                 '"O, my fatherland!" A Supercalifragilisticexpialidociouslyunbelievable word, too.').split()
        caps = ep.caption_blocks(words, [i*.3 for i in range(len(words))], len(words)*.3)
        for _, _, text in caps:
            self.assertLessEqual(len(text.split('\n')), 2)
            self.assertTrue(all(len(line) <= 42 for line in text.split('\n')), text)
        self.assertEqual(' '.join(' '.join(t.split('\n')) for _, _, t in caps).replace(' ', ''), ''.join(words))

    def test_aligned_spoken_words_are_used(self):
        words = 'Now the right hand alone.'.split()
        for key in ep.SPOKEN_TIMES:
            starts, found = ep.aligned_starts({key: [0., .2, .4, .6, .8]}, words)
            self.assertEqual((starts, found), ([0., .2, .4, .6, .8], key))
        self.assertEqual(ep.aligned_starts(dict(wordStarts=[0., .5]), words), (None, None))
        self.assertEqual(ep.unspoken('Then [slowly, calm] two swells. [pause]'.split()), [False, True, True, False, False, True])

    def test_no_file_when_nothing_is_spoken(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertFalse(ep.write_srt(Path(folder)/'captions.srt', []))
            self.assertFalse((Path(folder)/'captions.srt').exists())


if __name__ == '__main__':
    unittest.main(verbosity=2)
