"""Editor packages, checked against takes made without the exporter.

    Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/test_edit_package.py

A fake bundle (a recording and its right-hand stem, written from sums of sinusoids at random frequencies, so their
correlation has one clear peak; a story with stem solos; narration with measured ducks and its spoken words aligned)
and fake takes whose sound this file renders itself, the way the app's engine makes it: once a video frame,
SongDirector picks the cue and its solo, SongNarration eases the music gain and StemPlayback moves the volumes, then the
mixer renders whole 1024-sample blocks with them. The music file starts off the 8 kHz grid, the mono voice plays
centre-panned at -3 dB and 39 ms ahead of it (as an MP3 bundle plays: NAudio keeps the encoder delay), and in the first
take the volumes are set in Update, before the director and the narration have decided the frame (a frame late, as
takes did before report.duckLagFrames existed). Nothing here comes from edit_package's own model: the package has to
find what the take did on its own. That take then meets bundles changed after it was recorded, one way at a time, and
every gate has to say so; the tutorial and a Quest performance get takes of their own.
"""
import csv
import json
import hashlib
import math
import re
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


def music(t, hush=None):
    """(left-hand part, right-hand part) of the recording at song times t, stereo: the hands sit a little apart.
    `hush`: a rest, in song seconds."""
    inside = ((t >= 0) & (t < SONG)).astype(float)
    if hush:
        inside[(t >= hush[0]) & (t < hush[1])] = 0
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


CUES = [dict(start=0., end=4., view='Torus', stem='', text='The étude opens.',
             speech='Chopin wrote this Étude in E major. [pause] It is Opus 10, Number 3, from 1832.'),
        dict(start=4., end=7., view='Torus', stem='right-hand', soloGain=.1, releaseSoloAfterNarration=True, text='Right hand alone',
             speech='[calm] Now the right hand alone.'),
        dict(start=7., end=SONG, view='Torus', stem='right-hand', soloUntil=8.5, narrate=False, text='Right hand, then all')]
SEGMENTS = [dict(cue=0, start=.5, end=3.2, duck=.3), dict(cue=1, start=4.3, end=5.6, duck=.5)]
# Each line's spoken words as story_timing.py aligns them (seconds from the line's start, the [pause] in place): the
# narrator holds the pause, so "It" comes 0.3 s after its characters would put it, and the line's last two words fall
# after song time 3.0, where the mid-song take starts.
SPOKEN_STARTS = [[0., .2, .35, .5, .8, .9, 1., 1.3, 1.8, 1.9, 2., 2.15, 2.25, 2.4, 2.52, 2.6], [0., .2, .35, .6, .85]]


RENDERED, ENCODED, RECORDINGS = {}, {}, {}
SHARED = tempfile.TemporaryDirectory()          # removed when the interpreter exits, however the classes are run


def render(seconds, start=START, song_start=0., lag=1, narrated=True, voice=VOICE, lead=LEAD, hush=None):
    key = (seconds, start, song_start, lag, narrated, id(voice), lead, hush)
    if key not in RENDERED:
        RENDERED[key] = engine(seconds, start, song_start, lag, narrated, voice, lead, hush)
    return RENDERED[key]


def engine(seconds, start, song_start, lag, narrated, voice, lead, hush):
    """A take's sound as the app's engine makes it, and what went into it (the truth the package is checked
    against): the music file's sample 0 at video time `start`, the song clock `lead` earlier. Frame k's Update sees the
    DSP clock at the samples mixed so far; the mixer then renders whole blocks up to the frame's end with the volumes
    then set."""
    clock = start-lead
    frames, n = int(seconds*FPS), int(seconds*FPS)*RATE//FPS
    t = np.arange(n)/RATE
    left, right = music(t-start, hush)
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


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def edit_json(path, change):
    data = json.loads(path.read_text(encoding='utf-8'))
    change(data)
    path.write_text(json.dumps(data, ensure_ascii=False), encoding='utf-8')


def make_bundle(root, voice=VOICE, replaced=None, hush=None):
    """A bundle as the library writes one: narration.json signed for its story.json (narration.py's storySha256), each
    line's spoken words aligned. `replaced`: a line rendered again since the take, in another voice; `hush`: a rest in
    the recording."""
    bundle = root/'Song'
    (bundle/'stems').mkdir(parents=True)
    (bundle/'narration/x').mkdir(parents=True)
    if hush not in RECORDINGS:
        RECORDINGS[hush] = music(np.arange(int(SONG*SR))/SR, hush)
    left, right = RECORDINGS[hush]
    sf.write(bundle/'recording.wav', left+right, SR, subtype='FLOAT')
    sf.write(bundle/'stems/right-hand.wav', right, SR, subtype='FLOAT')
    t = np.arange(int(SONG*24000))/24000
    sf.write(bundle/'narration/x/narration.wav', sum(speech(t, [s], OTHER if i == replaced else voice) for i, s in enumerate(SEGMENTS)),
             24000, subtype='FLOAT')
    (bundle/'story.json').write_text(json.dumps(dict(title='A Test Tour', duration=SONG, cues=CUES), ensure_ascii=False), encoding='utf-8')
    segments = []
    for s, starts in zip(SEGMENTS, SPOKEN_STARTS):
        words, ends = re.sub(r'^\s*\[[^\]]*\]\s*', '', CUES[s['cue']]['speech']).split(), starts[1:]+[s['end']-s['start']]
        segments.append(dict(s, spokenWords=words, spokenStarts=starts, spokenEnds=ends, wordStarts=starts, wordEnds=ends))
    (bundle/'narration.json').write_text(json.dumps(dict(storySha256=sha(bundle/'story.json'), audioPath='narration/x/narration.wav',
                                                         sha256=sha(bundle/'narration/x/narration.wav'), segments=segments), ensure_ascii=False), encoding='utf-8')
    (bundle/'aligned.mid.prepared.json').write_text(json.dumps(dict(audioPath='recording.wav', stems=[dict(id='right-hand', audioPath='stems/right-hand.wav')])), encoding='utf-8')
    return bundle


def resign(bundle):
    """narration.py run again on an edited story whose speech did not change: the same voice, the new story's hash."""
    edit_json(bundle/'narration.json', lambda n: n.update(storySha256=sha(bundle/'story.json')))


def make_take(folder, name, mix, seconds, key=None):
    """An MP4 as the Recorder writes one: H.264 at 30 fps and the sound as AAC (a sound with a key encoded once)."""
    video = folder/(name+'.mp4')
    if key in ENCODED and ENCODED[key].exists():
        shutil.copy(ENCODED[key], video)
        return video
    wav = folder/(name+'.wav')
    sf.write(wav, mix, RATE, subtype='FLOAT')
    subprocess.run(['ffmpeg', '-v', 'error', '-y', '-f', 'lavfi', '-i', f'color=c=black:s=320x568:r={FPS}:d={seconds}', '-i', str(wav),
                    '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '320k', '-shortest', str(video)], check=True)
    wav.unlink()
    if key is not None:
        ENCODED[key] = Path(SHARED.name)/f'{key}.mp4'
        shutil.copy(video, ENCODED[key])
    return video


def report(video, bundle, truth, seconds=None, **extra):
    markers, cue = [], -1
    for k in range(truth['frames'] if seconds is None else int(round(seconds*FPS))):     # the frame each cue became current
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


def srt(path):
    """captions.srt as (start stamp, text) per caption."""
    blocks = [b.split('\n') for b in path.read_text(encoding='utf-8-sig').strip().split('\n\n')]
    return [(b[1][:12], ' '.join(b[2:])) for b in blocks]


class Take(unittest.TestCase):
    """A take from the top, recorded before report.duckLagFrames: everything a frame late."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.tmp.name)/'Take #1 50% & Étude [x]'           # characters a pathurl has to escape
        cls.root.mkdir()
        cls.bundle = make_bundle(cls.root)
        mix, cls.truth = render(12.)
        cls.video = make_take(cls.root, 'a-test-tour-vertical-20261110-120000', mix, 12., key='take')
        cls.report = report(cls.video, cls.bundle, cls.truth)
        cls.out, cls.check = ep.build(cls.report)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_finds_the_music_and_the_voice_each_to_a_fraction_of_a_sample(self):
        self.assertAlmostEqual(self.check['audioStart'], START, delta=.25/RATE)
        self.assertAlmostEqual(self.check['voiceStart'], CLOCK, delta=.25/RATE)
        self.assertAlmostEqual(self.check['voiceShiftMs'], -LEAD*1000, delta=.01)
        self.assertGreater(self.check['correlation'], .5)

    def test_rebuilds_the_take(self):
        self.assertTrue(self.check['matchesTake'], self.check['problems'])
        self.assertGreater(self.check['reproductionSnrDb'], 30)
        self.assertAlmostEqual(self.check['musicLevel'], MASTER, delta=.01)
        self.assertAlmostEqual(self.check['narrationRelativeToMusic'], .95*PAN, delta=.01)
        for line in self.check['duckingMeasured']:
            self.assertAlmostEqual(line['measured'], line['model'], delta=.01)
            self.assertLess(abs(line['offDb']), .15)

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
        self.assertEqual(rows[0]['text'], 'Chopin wrote this Étude in E major. It is Opus 10, Number 3, from 1832.')

    def test_captions(self):
        raw = (self.out/'captions.srt').read_bytes()
        self.assertTrue(raw.startswith(b'\xef\xbb\xbf'))
        self.assertNotIn(b'\n', raw.replace(b'\r\n', b''))
        blocks = [b.split('\r\n') for b in raw[3:].decode('utf-8').strip().split('\r\n\r\n')]
        self.assertEqual([b[0] for b in blocks], ['1', '2', '3'])
        self.assertTrue(all(len(b) <= 4 and all(len(l) <= 42 for l in b[2:]) for b in blocks))
        # Line 1 splits at its sentence, and the second caption starts where the narrator says "It", after the pause:
        # on the aligned word, not where the line's characters would put it.
        self.assertEqual(srt(self.out/'captions.srt'), [(ep.stamp(CLOCK+.5), 'Chopin wrote this Étude in E major.'),
                                                        (ep.stamp(CLOCK+.5+1.8), 'It is Opus 10, Number 3, from 1832.'),
                                                        (ep.stamp(CLOCK+4.3), 'Now the right hand alone.')])
        self.assertEqual(self.check['captionTiming'], "every line on the narration's aligned word times")

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
        self.assertEqual(seq.findall('marker')[0].findtext('comment'), 'Chopin wrote this Étude in E major. It is Opus 10, Number 3, from 1832.')

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


class Changed(unittest.TestCase):
    """The first take against its bundle changed after it was recorded, one way at a time. The report predates the
    hashes, as every take recorded before 10 November's afternoon does, so only the package's own checks can tell:
    each change must fail the package (exit 2) and say why."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.tmp.name)
        cls.mix, cls.truth = render(12.)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def variant(self, name, change=None, **bundle):
        folder = self.root/name
        folder.mkdir()
        b = make_bundle(folder, **bundle)
        if change:
            change(b)
        video = make_take(folder, f'{name}-vertical-20261110-121000', self.mix, 12., key='take')
        return report(video, b, self.truth), b

    def problem(self, check, start):
        self.assertFalse(check['matchesTake'])
        found = [p for p in check['problems'] if p.startswith(start)]
        self.assertTrue(found, check['problems'])
        return found[0]

    def test_a_story_edited_since_its_voice_was_rendered(self):
        """The speech respelled after the take and the voice not rendered again: SongNarration would refuse such a
        narration, so the take played the story it was rendered for. The captions keep the words the voice says."""
        def respell(b):
            edit_json(b/'story.json', lambda s: s['cues'][0].update(speech=s['cues'][0]['speech'].replace('Étude', 'study')))
        path, _ = self.variant('respelled', respell)
        result = run(path)
        self.assertEqual(result.returncode, 2, result.stdout[-800:]+result.stderr[-800:])
        out = path.with_name(path.stem+'-edit')
        check = json.loads((out/'check.json').read_text(encoding='utf-8'))
        self.assertIn("the captions keep the narration's own words", self.problem(check, 'narration.json was rendered for an earlier story.json'))
        said = ' '.join(text for _, text in srt(out/'captions.srt'))
        self.assertIn('this Étude in E major', said)
        self.assertNotIn('study', said)
        with open(out/'ducking.csv', encoding='utf-8-sig') as f:
            self.assertIn('Étude', next(csv.DictReader(f))['text'])

    def test_a_narration_the_take_did_not_play_gets_no_package(self):
        path, b = self.variant('renamed')
        edit_json(path, lambda r: r.update(songStart=0., duckLagFrames=1, storySha256=sha(b/'story.json'), narrationSha256='ab'*32, narrationPlayed=True))
        with self.assertRaisesRegex(ep.Refused, 'narration was regenerated'):
            ep.build(path)
        self.assertEqual(list(path.parent.glob('*-edit*')), [])

    def test_a_cue_retimed_and_another_retitled(self):
        def edit(b):
            def story(s):
                s['cues'][1]['end'] = s['cues'][2]['start'] = 7.4
                s['cues'][1]['text'] = 'The right hand, on its own'
            edit_json(b/'story.json', story)
            resign(b)
        path, _ = self.variant('retimed', edit)
        problem = self.problem(ep.build(path)[1], 'cue markers off the story')
        self.assertIn('cue-2 at ', problem)
        self.assertIn("cue-1 titled 'Right hand alone'", problem)

    def test_a_duck_lifted_by_less_than_a_decibel(self):
        """Line 1 played at 0.3; the cue sheet now says 0.33 (0.8 dB, a floor lifting a duck a little), which 0.05 of
        linear gain let through."""
        path, _ = self.variant('lifted', lambda b: edit_json(b/'narration.json', lambda n: n['segments'][0].update(duck=.33, duckMeasured=.3, duckFloor=.33)))
        check = ep.build(path)[1]
        self.problem(check, 'spoken lines 1: the music under them')
        line = check['duckingMeasured'][0]
        self.assertAlmostEqual(line['measured'], .3, delta=.005)
        self.assertAlmostEqual(line['offDb'], 20*math.log10(.3/.33), delta=.15)

    def test_one_line_rendered_again(self):
        path, _ = self.variant('rerendered', replaced=1)
        check = ep.build(path)[1]
        self.problem(check, "spoken lines 2: narration.wav's voice there is not the take's")
        self.assertEqual(check['voiceLinesFound'], '1 of 2')
        self.assertAlmostEqual(check['voiceShiftMs'], -LEAD*1000, delta=.02)

    def test_a_narration_rendered_louder(self):
        def louder(b):
            voice, rate = sf.read(b/'narration/x/narration.wav', dtype='float32')
            sf.write(b/'narration/x/narration.wav', 8*voice, rate, subtype='FLOAT')
            edit_json(b/'narration.json', lambda n: n.update(sha256=sha(b/'narration/x/narration.wav')))
        path, _ = self.variant('louder', louder)
        check = ep.build(path)[1]
        self.problem(check, 'the narration fits the take at')
        self.assertAlmostEqual(check['narrationRelativeToMusic'], .95*PAN/8, delta=.01)


class Gate(unittest.TestCase):
    """The narration was regenerated after the take: the voice in the MP4 is in no file. The package must say so,
    exit 2, keep the narration audible in its timeline, and leave an earlier package that matched in place."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        bundle = make_bundle(root, voice=OTHER)
        mix, truth = render(12.)
        video = make_take(root, 'regenerated-vertical-20261110-120200', mix, 12., key='take')
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
    """A take that starts at its first cue, mid-song (report.songStart) and mid-line, recorded with the volumes set in
    LateUpdate (duckLagFrames 0), naming the story and narration it played."""
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
                            storySha256=sha(cls.bundle/'story.json'), narrationSha256=narration['sha256'], narrationPlayed=True)
        cls.out, cls.check = ep.build(cls.report)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_lines_up_and_rebuilds(self):
        self.assertTrue(self.check['matchesTake'], self.check['problems'])
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
        # Line 1 is mid-way when the take starts: its caption starts at the first word the take played.
        captions = srt(self.out/'captions.srt')
        self.assertEqual(captions[0], (ep.stamp(CLOCK-self.song_start+.5+2.52), 'from 1832.'))
        self.assertEqual(captions[1], (ep.stamp(CLOCK-self.song_start+4.3), 'Now the right hand alone.'))
        self.assertEqual(self.check['captionTiming'], "every line on the narration's aligned word times")
        with open(self.out/'ducking.csv', encoding='utf-8-sig') as f:
            rows = list(csv.DictReader(f))
        self.assertGreaterEqual(float(rows[0]['voice_start_s']), CLOCK-.001)


class Cut(unittest.TestCase):
    """The first take stopped during its second line, 5.5 s in: that line's music never comes back up in it, and the
    take holds half of the song."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        bundle = make_bundle(root)
        mix, truth = render(12.)
        cls.seconds = 5.5
        video = make_take(root, 'cut-vertical-20261110-120600', mix[:int(cls.seconds*RATE)], cls.seconds)
        cls.out, cls.check = ep.build(report(video, bundle, truth, seconds=cls.seconds))

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_a_line_the_take_ends_in(self):
        self.assertTrue(self.check['matchesTake'], self.check['problems'])
        with open(self.out/'ducking.csv', encoding='utf-8-sig') as f:
            rows = list(csv.DictReader(f))
        self.assertEqual(len(rows), 2)
        self.assertEqual(float(rows[1]['voice_end_s']), self.seconds)
        self.assertNotIn('', (rows[1]['duck_starts_s'], rows[1]['duck_reached_s']))
        self.assertEqual((rows[1]['release_starts_s'], rows[1]['release_done_s']), ('', ''))
        self.assertEqual(self.check['captionTiming'], "every line on the narration's aligned word times")

    def test_the_music_is_scored_on_what_the_take_holds(self):
        self.assertGreater(self.check['correlation'], .5)


class Hush(unittest.TestCase):
    """Line 1 spoken over a rest in the music (the recording silent under it, as before a pickup): there is no duck
    to hear or measure there, so the line is left unmeasured rather than failing the take."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        hush = (.25, 3.45)
        bundle = make_bundle(root, hush=hush)
        mix, _ = render(12., hush=hush)
        video = make_take(root, 'hush-vertical-20261110-120700', mix, 12.)
        cls.out, cls.check = ep.build(report(video, bundle, render(12., hush=hush)[1]))

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_a_line_over_a_rest_is_not_measured(self):
        self.assertTrue(self.check['matchesTake'], self.check['problems'])
        first, second = self.check['duckingMeasured']
        self.assertNotIn('measured', first)
        self.assertLess(first['musicShareDb'], ep.MUSIC_FLOOR_DB)
        self.assertAlmostEqual(second['measured'], .5, delta=.01)


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
                      storySha256=sha(bundle/'story.json'))
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
    report's times, saying it could not be checked (so exit 2); no crash."""
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
            cls.results[name] = ep.build(report(video, bundle, truth))

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_says_it_could_not_check(self):
        for name, (out, check) in self.results.items():
            self.assertFalse(check['matchesTake'])
            self.assertEqual(json.loads((out/'check.json').read_text(encoding='utf-8'))['problems'], check['problems'])
            self.assertIn('silent' if name == 'silent' else 'no sound', check['problems'][0])
            self.assertTrue((out/'narration.wav').exists() and (out/'timeline.xml').exists())
            items = [t.findtext('clipitem/name') for t in ET.parse(out/'timeline.xml').getroot().findall('./sequence/media/audio/track')]
            self.assertEqual('MP4 mix (reference)' in items, name == 'silent')


class Tutorial(unittest.TestCase):
    """A tutorial take with the app's synth captured live (RecordingStemTap): two step clips (24 kHz files, as the
    tutorial ships them) played centre-panned at -3 dB over the synth, off the sample grid, and the capture starting a
    little after the report says."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        folder = root/'Tutorial'
        folder.mkdir()
        seconds, cls.level, capture = 10., .975*PAN, .02131
        cls.starts, length = dict(keys=1.01337, turn=5.52711), 3.2
        line = [dict(start=0., end=length)]
        t = np.arange(int(seconds*RATE))/RATE
        cls.synth = np.c_[tone(t, LEFT, .3), tone(t, RIGHT, .2)]*(t >= capture)[:, None]
        mix = cls.synth.copy()
        clips, steps = [], []
        for step, at in cls.starts.items():
            sf.write(folder/f'{step}.wav', speech(np.arange(int(length*24000))/24000, line), 24000, subtype='PCM_16')
            mix += cls.level*speech(t-at, line)[:, None]
            clips.append(dict(id=step, file=f'{step}.wav'))
            steps.append(dict(id=step, text=f'The {step} step says a few words here, and then a few more of them.'))
        (folder/'manifest.json').write_text(json.dumps(dict(clips=clips)), encoding='utf-8')
        (folder/'script.json').write_text(json.dumps(dict(steps=steps)), encoding='utf-8')
        video = make_take(root, 'tutorial-vertical-20261110-120900', mix, seconds)
        tc = capture+np.arange(int((seconds-capture)*RATE))/RATE
        sf.write(video.with_suffix('.synth.wav'), np.c_[tone(tc, LEFT, .3), tone(tc, RIGHT, .2)], RATE, subtype='FLOAT')
        path = video.with_suffix('.json')
        path.write_text(json.dumps(dict(kind='tutorial', video=video.name, fps=FPS, synthStem=video.stem+'.synth.wav', synthStart=.02,
                                        markers=[dict(id=s, title=s.title(), start=at-.1, end=at+length) for s, at in cls.starts.items()])), encoding='utf-8')
        cls.saved, ep.TUTORIAL = ep.TUTORIAL, folder
        try:
            cls.out, cls.check = ep.build(path)
        finally:
            ep.TUTORIAL = cls.saved

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_the_voice_and_the_app_sound_at_their_levels(self):
        self.assertTrue(self.check['matchesTake'], self.check['problems'])
        self.assertGreater(self.check['reproductionSnrDb'], 30)
        self.assertAlmostEqual(self.check['narrationLevel'], self.level, delta=.01)
        self.assertAlmostEqual(self.check['appSoundLevel'], 1., delta=.01)
        self.assertAlmostEqual(self.check['synthShift'], .00131, delta=.25/RATE)
        for step in self.check['steps']:                                  # a 16-bit 24 kHz clip: within half a sample
            self.assertAlmostEqual(step['voiceStart'], self.starts[step['step']], delta=.5/RATE)
        music, _ = sf.read(self.out/'music.wav', dtype='float64')
        self.assertGreater(snr(self.synth, music), 40)
        self.assertEqual([text[:12] for _, text in srt(self.out/'captions.srt')][:1], ['The keys ste'])
        self.assertIn("the app's sound at", (self.out/'README.txt').read_text(encoding='utf-8-sig'))


class Performance(unittest.TestCase):
    """A Quest performance: its sound is its package. The MP4's sound runs on past its picture: every file lasts as
    long as the picture."""
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        t = np.arange(int(3.5*RATE))/RATE
        cls.sound = .5*np.c_[tone(t, LEFT, .7), tone(t, RIGHT, .45)]
        sf.write(root/'sound.wav', cls.sound, RATE, subtype='FLOAT')
        video = root/'performance-20261110-120800-pov.mp4'
        subprocess.run(['ffmpeg', '-v', 'error', '-y', '-f', 'lavfi', '-i', f'color=c=black:s=320x568:r={FPS}:d=3', '-i', str(root/'sound.wav'),
                        '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '320k', str(video)], check=True)
        cls.out, cls.check = ep.build(video)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_its_sound_as_long_as_its_picture(self):
        self.assertTrue(self.check['matchesTake'], self.check['problems'])
        self.assertEqual((self.check['kind'], self.check['frames']), ('performance', 3*FPS))
        music, rate = sf.read(self.out/'music.wav', dtype='float64')
        self.assertEqual((rate, len(music)), (RATE, 3*RATE))
        self.assertGreater(snr(self.sound[:3*RATE], music), 20)
        self.assertEqual(sorted(p.name for p in self.out.iterdir()), ['README.txt', 'check.json', 'music.wav', 'timeline.xml'])


class Lining(unittest.TestCase):
    def test_the_correlation_is_scored_where_the_take_and_the_song_overlap(self):
        t = np.arange(10*RATE)/RATE
        song = np.c_[tone(t, LEFT, .7), tone(t, RIGHT, .45)].astype('float32')
        lag, score = ep.best_lag(song[4*RATE:6*RATE], song, -4., .05)        # two seconds of a ten-second song
        self.assertAlmostEqual(lag, -4., delta=1/8000)
        self.assertGreater(score, .99)


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

    def test_the_narrations_own_words(self):
        segment = dict(spokenWords='Then two swells.'.split(), spokenStarts=[0., .3, .5])
        self.assertEqual(ep.line_words(segment, dict(speech='The first swell.')), ('Then two swells.'.split(), [0., .3, .5]))
        self.assertEqual(ep.line_words(dict(), dict(speech='[calm] The first swell.')), ('The first swell.'.split(), None))

    def test_a_mid_song_take_captions_what_it_played(self):
        """Words spoken before the take started were never heard, and a line it never reached does not count."""
        segments = [dict(cue=0, start=.5, end=3.2), dict(cue=1, start=4.3, end=5.6), dict(cue=2, start=8., end=9.)]
        spoken = [(re.sub(r'^\s*\[[^\]]*\]\s*', '', CUES[0]['speech']).split(), SPOKEN_STARTS[0]),
                  ('Now the right hand alone.'.split(), None), ('Never played.'.split(), None)]
        captions, timing = ep.tour_captions(segments, spoken, 0., 3., 3., 6.)
        self.assertEqual([text for _, _, text in captions], ['from 1832.', 'Now the right hand alone.'])
        self.assertAlmostEqual(captions[0][0], 3.02)
        self.assertEqual(timing, '1 of 2 lines spread over the line by characters (no aligned word times)')

    def test_no_file_when_nothing_is_spoken(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertFalse(ep.write_srt(Path(folder)/'captions.srt', []))
            self.assertFalse((Path(folder)/'captions.srt').exists())


if __name__ == '__main__':
    unittest.main(verbosity=2)
