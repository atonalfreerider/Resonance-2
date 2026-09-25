"""Audio lyric sync on the generated public-domain fixture, against its known timing.

PatternPrep writes the fixture (MIDI with karaoke lyric events, lyrics.txt, song.json);
render_fixture renders it (formant voice with vibrato for the sung verses, offline speech
synthesis for the rapped ones); lyric_sync aligns the sheet to the rendered vocal. The
lyric events are the truth. Then the events are stripped from the MIDI and PatternPrep must
sync the bundle from lyrics.timing.json instead.

Slow (about two minutes: beat_this, pYIN and the fixture render). Run:
  Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/test_lyric_sync.py
"""
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import mido
import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
from lyric_sync import read_sheet, sync  # noqa: E402
from render_fixture import events, render  # noqa: E402


def prep(*args):
    subprocess.run(['dotnet', 'run', '--project', str(ROOT / 'Tools' / 'PatternPrep'), '--', *map(str, args)], check=True, capture_output=True, cwd=ROOT)


@unittest.skipUnless(importlib.util.find_spec('beat_this') and sys.platform == 'win32',
                     'needs the Song Workshop environment (Tools/SongLibrary/.venv, with beat_this) on Windows for offline speech')
class LyricSyncTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder = Path(tempfile.mkdtemp(prefix='lyric-sync-'))
        cls.midi = cls.folder / 'lyrics-song.mid'
        prep('--write-fixture', 'lyrics', cls.midi)
        render(cls.midi)
        cls.timing = json.loads(sync(cls.folder, aligner='onsets').read_text(encoding='utf-8'))
        tracks = events(mido.MidiFile(cls.midi))
        cls.lyric_events = sorted(tracks['Lead Vocal']['lyrics'] + tracks['Rap Vocal']['lyrics'])
        cls.bends = tracks['Lead Vocal']
        # Truth: each sheet word starts at its first syllable's lyric event.
        cls.truth, index = [], 0
        for stanza in read_sheet(cls.folder / 'lyrics.txt'):
            for line in stanza['lines']:
                for word in line:
                    cls.truth.append((word['text'], cls.lyric_events[index][0], stanza['spoken']))
                    index += word['syllables']

    def errors(self, spoken):
        words, j, out = self.timing['words'], 0, []
        for text, time, is_spoken in self.truth:
            while j < len(words) and words[j]['text'] != text:
                j += 1
            if j < len(words):
                if is_spoken == spoken:
                    out.append(abs(words[j]['start'] - time))
                j += 1
        return np.array(out)

    def test_beats_and_downbeats(self):
        beats, downbeats = np.array(self.timing['beats']), np.array(self.timing['downbeats'])
        grid = np.arange(0, beats[-1] + 1, .6)  # 100 bpm
        self.assertGreater(len(beats), 200)
        self.assertLess(np.median([np.min(np.abs(grid - b)) for b in beats]), .03)
        self.assertAlmostEqual(float(np.median(np.diff(downbeats))), 2.4, delta=.05)

    def test_sung_words(self):
        err = self.errors(False)
        self.assertEqual(len(err), 100)
        self.assertGreaterEqual(np.mean(err < .1), .95, f'sung words within 100 ms: {np.mean(err < .1):.2f}')

    def test_spoken_words(self):
        err = self.errors(True)
        self.assertEqual(len(err), 98)
        self.assertGreaterEqual(np.mean(err < .15), .8, f'spoken words within 150 ms: {np.mean(err < .15):.2f}')
        self.assertLess(np.median(err), .06)

    def test_vibrato_on_held_notes(self):
        held = [n for n in self.bends['notes'] if n[1] - n[0] >= 1.1]
        found = [n for n in held if any(v['start'] < n[1] and v['end'] > n[0] + .4 * (n[1] - n[0]) for v in self.timing['vibrato'])]
        self.assertGreaterEqual(len(found) / len(held), .8, f'{len(found)} of {len(held)} held notes')
        rates = [v['rate'] for v in self.timing['vibrato']]
        self.assertTrue(all(4.5 <= r <= 6.5 for r in rates), rates)
        short = [n for n in self.bends['notes'] if n[1] - n[0] < .7]
        wrong = [n for n in short if any(v['start'] < n[1] - .05 and v['end'] > n[0] + .05 for v in self.timing['vibrato'])]
        self.assertLessEqual(len(wrong), len(short) * .1)

    def test_patternprep_syncs_from_audio(self):
        # Without lyric events PatternPrep must take the audio's word times.
        bare = self.folder / 'bare'
        bare.mkdir(exist_ok=True)
        mid = mido.MidiFile(self.midi)
        for track in mid.tracks:
            kept, carry = [], 0
            for msg in track:
                if msg.type == 'lyrics':
                    carry += msg.time
                    continue
                kept.append(msg.copy(time=msg.time + carry))
                carry = 0
            track[:] = kept
        mid.save(bare / 'lyrics-song.mid')
        for name in ('lyrics.txt', 'song.json', 'lyrics.timing.json'):
            (bare / name).write_bytes((self.folder / name).read_bytes())
        prep(bare / 'lyrics-song.mid')
        bundle = json.loads((bare / 'lyrics-song.mid.patterns.json').read_text(encoding='utf-8'))
        lyrics = bundle['Lyrics']
        self.assertIn('audio alignment', lyrics['Sync'])
        # Truth in beats: 100 bpm, one beat is 0.6 s.
        truth = [t / .6 for t, _ in self.lyric_events]
        starts = [s['Start'] for s in lyrics['Syllables']]
        spoken = [s['Spoken'] for s in lyrics['Syllables']]
        self.assertEqual(len(starts), len(truth))
        err = np.abs(np.array(starts) - np.array(truth))
        sung_err, spoken_err = err[~np.array(spoken)], err[np.array(spoken)]
        self.assertGreaterEqual(np.mean(sung_err < .05), .95, f'sung syllables on their notes: {np.mean(sung_err < .05):.2f}')
        self.assertGreaterEqual(np.mean(spoken_err < .35), .75, f'spoken syllables within a third of a beat: {np.mean(spoken_err < .35):.2f}')


if __name__ == '__main__':
    unittest.main()
