import tempfile
import unittest
from pathlib import Path
import mido
import numpy as np
from prepare_song import read_score, retime, encodable_time_map


class RetimingTests(unittest.TestCase):
    def test_tempo_changes_preserve_payloads_and_durations(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory)/'source.mid'; output = Path(directory)/'aligned.mid'
            midi = mido.MidiFile(type=1, ticks_per_beat=480)
            midi.tracks.append(mido.MidiTrack([
                mido.MetaMessage('set_tempo', tempo=500000),
                mido.MetaMessage('time_signature', numerator=4, denominator=4),
                mido.MetaMessage('marker', text='Verse', time=0),
                mido.MetaMessage('set_tempo', tempo=750000, time=960),
                mido.MetaMessage('end_of_track', time=960)]))
            midi.tracks.append(mido.MidiTrack([
                mido.Message('program_change', program=12, channel=1),
                mido.Message('control_change', control=64, value=127, channel=1, time=240),
                mido.Message('note_on', note=60, velocity=92, channel=1, time=240),
                mido.Message('note_off', note=60, velocity=25, channel=1, time=480),
                mido.Message('pitchwheel', pitch=1024, channel=1, time=0),
                mido.Message('note_on', note=64, velocity=88, channel=1, time=240),
                mido.Message('note_off', note=64, channel=1, time=480),
                mido.Message('control_change', control=64, value=0, channel=1, time=240)]))
            midi.save(source)
            midi, events, notes, ticks, times = read_score(source)
            x = np.array([0,.5,1,1.5,2.5]); y = np.array([0,.2,.9,1.7,2.7])
            result = retime(midi, events, ticks, times, x, y, output)
            np.testing.assert_allclose(result.start, np.interp(notes.start,x,y), atol=.001)
            np.testing.assert_allclose(result.start+result.duration, np.interp(notes.start+notes.duration,x,y), atol=.001)
            self.assertEqual(mido.MidiFile(source).tracks[1][2].note, 60)
            self.assertAlmostEqual(mido.MidiFile(output).length,2.7,places=5)

    def test_extreme_map_is_monotonic_encodable_and_reports_adjustment(self):
        t=np.array([0.,480.,960.]);seconds=np.array([0.,.5,1.])
        x=np.array([0.,.2,.201,.202,.3,1.]);y=np.array([0.,.2,2.2,2.21,2.3,3.])
        a,b,removed,error=encodable_time_map(t,seconds,x,y,480)
        self.assertGreater(removed,0);self.assertGreater(error,0)
        self.assertEqual(a[0],0);self.assertEqual(b[-1],3)
        self.assertTrue(np.all(np.diff(a)>0));self.assertTrue(np.all(np.diff(b)>0))
        ticks=np.interp(a,seconds,t)
        self.assertTrue(np.all(np.diff(b)*480*1e6/np.diff(ticks)<0xffffff))

    def test_one_shot_percussion_can_end_at_eof(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'drum.mid';m=mido.MidiFile()
            m.tracks.append(mido.MidiTrack([mido.Message('note_on',note=60,velocity=70),mido.Message('note_on',note=42,velocity=70,channel=9),mido.Message('note_off',note=60,time=480)]));m.save(path)
            _,_,notes,_,_=read_score(path);self.assertEqual(len(notes),1)

    def test_reject_unpaired_notes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'bad.mid'
            midi=mido.MidiFile();midi.tracks.append(mido.MidiTrack([mido.Message('note_on',note=60,velocity=70)]));midi.save(path)
            with self.assertRaises(ValueError):read_score(path)


if __name__ == '__main__':unittest.main()
