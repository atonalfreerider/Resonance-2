"""Editor packages: a synthetic tour take is rebuilt from its parts.

    Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/test_edit_package.py

A fake bundle (recording, a right-hand stem, a story with a soloed cue, narration with measured ducks) and a fake
take: an MP4 (ffmpeg, AAC) whose sound is music-as-played x ducking + 0.95 x narration, scaled by a master volume
and started 0.70 s into the video, as Director mode records it. The package must find that start, rebuild the
sound, and write captions, cues and a timeline an editor can import.
"""
import csv
import json
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np
import soundfile as sf

import edit_package as ep

SR, SONG, START, MASTER, FPS, VIDEO = 44100, 10.0, .70, .8, 30, 12.0


def tone(seconds, rate, freqs, seed):
    t = np.arange(int(seconds*rate))/rate
    rng = np.random.default_rng(seed)
    x = sum(np.sin(2*np.pi*f*t+rng.uniform(0, 6)) for f in freqs)*.12
    x *= .6+.4*np.sin(2*np.pi*.7*t)**2            # some movement, so the correlation has a clear peak
    return (x+rng.normal(0, .01, len(t))).astype('float32')


class EditPackage(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = Path(cls.tmp.name)
        bundle = root/'Song'; (bundle/'stems').mkdir(parents=True); (bundle/'narration/x').mkdir(parents=True)
        left, right = tone(SONG, SR, [110, 165, 220], 1), tone(SONG, SR, [440, 554, 659], 2)
        sf.write(bundle/'recording.wav', np.c_[left+right, left+right], SR)
        sf.write(bundle/'stems/right-hand.wav', np.c_[right, right], SR)
        cues = [dict(start=0., end=4., text='First line of the tour.', speech='The first line is spoken here, slowly and clearly.', view='Overview', stem=''),
                dict(start=4., end=7., text='Right hand', speech='Now the right hand alone.', view='Overview', stem='right-hand', soloUntil=6.),
                dict(start=7., end=SONG, text='Music alone', view='Torus', stem='', narrate=False)]
        segments = [dict(cue=0, start=.5, end=3.2, duck=.3), dict(cue=1, start=4.3, end=5.6, duck=.5)]
        voice = np.zeros(int(SONG*24000), dtype='float32')
        for s in segments:
            a, b = int(s['start']*24000), int(s['end']*24000)
            voice[a:b] = tone((b-a)/24000, 24000, [180, 360, 900], 3)[:b-a]*2
        sf.write(bundle/'narration/x/narration.wav', voice, 24000)
        (bundle/'story.json').write_text(json.dumps(dict(title='A Test Tour', cues=cues)), encoding='utf-8')
        (bundle/'narration.json').write_text(json.dumps(dict(audioPath='narration/x/narration.wav', segments=segments)), encoding='utf-8')
        (bundle/'aligned.mid.prepared.json').write_text(json.dumps(dict(audioPath='recording.wav', stems=[dict(id='right-hand', audioPath='stems/right-hand.wav')])), encoding='utf-8')
        # the take's sound, exactly as Director mode mixes it, then encoded like the Recorder's MP4
        manifest = json.loads((bundle/'aligned.mid.prepared.json').read_text())
        played, soloed = ep.music_as_played(bundle, manifest, cues, segments, SONG)
        frames = int(VIDEO*FPS); length = int(VIDEO*ep.RATE)
        gain = ep.envelope(segments, cues, frames, FPS, START)
        per = np.interp(np.arange(length)/ep.RATE, np.arange(frames)/FPS, gain)[:, None]
        mix = MASTER*(ep.place(played, START, length)*per + ep.VOICE_GAIN*ep.place(ep.load(bundle/'narration/x/narration.wav'), START, length))
        sf.write(root/'mix.wav', mix, ep.RATE, subtype='FLOAT')
        cls.video = root/'a-test-tour-vertical-20261110-120000.mp4'
        subprocess.run(['ffmpeg', '-v', 'error', '-y', '-f', 'lavfi', '-i', f'color=c=black:s=320x568:r={FPS}:d={VIDEO}', '-i', str(root/'mix.wav'),
                        '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '256k', '-shortest', str(cls.video)], check=True)
        report = dict(kind='tour', song='A Test Tour', bundle=str(bundle), video=cls.video.name, fps=FPS, audioStart=.64,
                      markers=[dict(id='cue-0', title='', start=.72, end=4.7)])
        cls.report = cls.video.with_suffix('.json'); cls.report.write_text(json.dumps(report), encoding='utf-8')
        cls.out, cls.check = ep.build(cls.report)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_lines_up_with_the_video_and_rebuilds_its_sound(self):
        self.assertAlmostEqual(self.check['audioStart'], START, delta=.002)
        self.assertAlmostEqual(self.check['musicLevel'], MASTER, delta=.05)
        self.assertAlmostEqual(self.check['narrationRelativeToMusic'], ep.VOICE_GAIN, delta=.03)
        self.assertGreater(self.check['reproductionSnrDb'], 20)
        for line in self.check['duckingMeasured']:                   # the cue sheet's levels are what the mix has
            self.assertAlmostEqual(line['measured'], line['cueSheet'], delta=.03)

    def test_every_track_spans_the_video_at_48k(self):
        for name in ('narration.wav', 'music.wav', 'music-as-played.wav'):
            info = sf.info(self.out/name)
            self.assertEqual(info.samplerate, 48000)
            self.assertAlmostEqual(info.duration, VIDEO, delta=1/FPS)
        voice, _ = sf.read(self.out/'narration.wav')
        first = np.flatnonzero(np.abs(voice[:, 0]) > .01)[0]/48000
        self.assertAlmostEqual(first, START+.5, delta=.01)       # the first line, in video time

    def test_ducking_cues_and_envelope(self):
        with open(self.out/'ducking.csv', encoding='utf-8') as f:
            rows = list(csv.DictReader(f))
        self.assertEqual([(r['cue'], r['music_gain']) for r in rows], [('0', '0.3'), ('1', '0.5')])
        self.assertAlmostEqual(float(rows[0]['duck_starts']), START+.5-.12, delta=.002)
        self.assertAlmostEqual(float(rows[1]['release_starts']), START+5.6+.15, delta=.002)
        with open(self.out/'ducking-envelope.csv', encoding='utf-8') as f:
            env = list(csv.DictReader(f))
        self.assertEqual(len(env), int(VIDEO*FPS))
        lowest = min(float(r['music_gain']) for r in env)
        self.assertAlmostEqual(lowest, .3, delta=.01)

    def test_captions(self):
        text = (self.out/'captions.srt').read_text(encoding='utf-8')
        blocks = [b for b in text.strip().split('\n\n') if b]
        self.assertGreaterEqual(len(blocks), 2)
        first = blocks[0].split('\n')
        self.assertEqual(first[0], '1')
        self.assertRegex(first[1], r'^00:00:01,\d{3} --> 00:00:0\d,\d{3}$')    # 0.70 + 0.5 s
        self.assertTrue(all(len(line) <= 42 for b in blocks for line in b.split('\n')[2:]))
        self.assertIn('right hand alone', text)

    def test_timeline_imports(self):
        root = ET.parse(self.out/'timeline.xml').getroot()
        self.assertEqual(root.tag, 'xmeml')
        tracks = root.findall('./sequence/media/audio/track')
        self.assertEqual(len(tracks), 4)                                       # MP4 mix, music, narration, original
        self.assertEqual(tracks[0].findtext('enabled'), 'FALSE')
        keys = tracks[1].findall('.//effect/parameter/keyframe')
        self.assertGreater(len(keys), 4)
        self.assertLess(min(float(k.findtext('value')) for k in keys), .35)
        self.assertAlmostEqual(float(tracks[2].findtext('.//effect/parameter/value')), .95, delta=.03)
        url = root.findtext('./sequence/media/video/track/clipitem/file/pathurl')
        self.assertTrue(url.startswith('file://localhost/') and url.endswith('.mp4'))


if __name__ == '__main__':
    unittest.main(verbosity=2)
