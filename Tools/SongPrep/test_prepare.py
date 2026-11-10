import tempfile
import unittest
from pathlib import Path
import mido
import numpy as np
from prepare_song import read_score, retime, encodable_time_map, apply_landmarks


class RetimingTests(unittest.TestCase):
    def test_original_tempo_boundary_between_alignment_knots(self):
        with tempfile.TemporaryDirectory() as directory:
            source=Path(directory)/'source.mid';output=Path(directory)/'aligned.mid'
            midi=mido.MidiFile(ticks_per_beat=480)
            midi.tracks.append(mido.MidiTrack([
                mido.MetaMessage('set_tempo',tempo=500000),
                mido.MetaMessage('set_tempo',tempo=1000000,time=480),
                mido.MetaMessage('end_of_track',time=480)]))
            midi.tracks.append(mido.MidiTrack([
                mido.Message('note_on',note=60,velocity=80,time=240),
                mido.Message('note_off',note=60,time=480)]))
            midi.save(source)
            midi,events,notes,ticks,times=read_score(source)
            result=retime(midi,events,ticks,times,np.array([0.,1.5]),np.array([0.,3.]),output)
            np.testing.assert_allclose(result.start,notes.start*2,atol=.001)
            np.testing.assert_allclose(result.start+result.duration,(notes.start+notes.duration)*2,atol=.001)

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

    def test_pause_is_absorbed_before_the_arrival(self):
        # A 1.5 s breath before the downbeat at score 0.5 s: the DTW map steps almost vertically there. Too steep
        # for one MIDI tempo, so the pause must go into the held note before it, and the arrival keep its time.
        t=np.array([0.,480.,960.]);seconds=np.array([0.,.5,1.])
        x=np.round(np.arange(0,1.0001,.01),4);y=x+np.where(x>=.5,1.5,0.)
        y[x==.5]=2.0
        a,b,removed,error=encodable_time_map(t,seconds,x,y,480)
        at=lambda s: float(np.interp(s,a,b))
        self.assertAlmostEqual(at(.5),2.0)                  # the arrival after the pause
        self.assertAlmostEqual(at(.6),2.1)
        self.assertAlmostEqual(at(.4),.4)                   # notes well before the pause are untouched
        self.assertGreater(removed,0)
        ticks=np.interp(a,seconds,t)
        self.assertTrue(np.all(np.diff(b)*480*1e6/np.diff(ticks)<0xffffff))

    def test_recording_leading_silence_delays_score_without_moving_later_targets(self):
        with tempfile.TemporaryDirectory() as directory:
            source=Path(directory)/'in.mid';output=Path(directory)/'out.mid'
            m=mido.MidiFile();m.tracks.append(mido.MidiTrack([mido.Message('note_on',note=60,velocity=70),mido.Message('note_off',note=60,time=960)]));m.save(source)
            midi,events,notes,ticks,times=read_score(source)
            result=retime(midi,events,ticks,times,np.array([0.,1.]),np.array([1.32,2.5]),output)
            self.assertAlmostEqual(result.start.iloc[0],1.32,places=5)
            self.assertAlmostEqual(result.start.iloc[0]+result.duration.iloc[0],2.5,places=5)

    def test_original_slow_tempo_inside_valid_average_alignment_is_encodable(self):
        with tempfile.TemporaryDirectory() as directory:
            source=Path(directory)/'in.mid';output=Path(directory)/'out.mid'
            m=mido.MidiFile(ticks_per_beat=480)
            m.tracks.append(mido.MidiTrack([
                mido.MetaMessage('set_tempo',tempo=500000),
                mido.MetaMessage('set_tempo',tempo=16000000,time=480),
                mido.MetaMessage('set_tempo',tempo=500000,time=24),
                mido.MetaMessage('end_of_track',time=480)]))
            m.tracks.append(mido.MidiTrack([mido.Message('note_on',note=60,velocity=80,time=480),mido.Message('note_off',note=60,time=24)]));m.save(source)
            midi,events,notes,ticks,times=read_score(source)
            x,y,merged,error=encodable_time_map(ticks,times,np.array([0.,times[-1]]),np.array([0.,times[-1]*2]),480)
            self.assertGreater(merged,0);self.assertGreater(error,0)
            retime(midi,events,ticks,times,x,y,output)
            self.assertAlmostEqual(mido.MidiFile(output).length,times[-1]*2,places=5)
            self.assertTrue(all(v.tempo<=0xffffff for t in mido.MidiFile(output).tracks for v in t if v.type=='set_tempo'))

    def test_one_shot_percussion_can_end_at_eof(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'drum.mid';m=mido.MidiFile()
            m.tracks.append(mido.MidiTrack([mido.Message('note_on',note=60,velocity=70),mido.Message('note_on',note=42,velocity=70,channel=9),mido.Message('note_off',note=60,time=480)]));m.save(path)
            _,_,notes,_,_=read_score(path);self.assertEqual(len(notes),1)

    def test_landmarks_move_the_map_locally_and_keep_its_ends(self):
        score = np.arange(0., 41.)
        audio = score*2+3                                   # 3 s of leading silence, then two seconds per score-second
        knots, moved, report = apply_landmarks(score, audio, [(10., 24.0), (20., 43.1), (30., 63.5), (31.5, 66.6)])
        at = lambda s: float(np.interp(s, knots, moved))
        self.assertAlmostEqual(at(10), 24.0)                # a pause the alignment missed: +1 s at bar 10
        self.assertAlmostEqual(at(7.5), 18.0)               # 2.5 score-seconds away: untouched
        self.assertAlmostEqual(at(11), 25.5)                # half the correction, halfway to where it fades
        self.assertAlmostEqual(at(30), 63.5)
        self.assertAlmostEqual(at(31.5), 66.6)              # near landmarks: the correction runs between them
        self.assertAlmostEqual(at(30.75), 64.5+0.55)
        self.assertEqual((moved[0], moved[-1]), (audio[0], audio[-1]))
        self.assertTrue(np.all(np.diff(moved) > 0))
        self.assertAlmostEqual(at(20), 43.0)                # missed by 0.1 s only: the alignment is trusted
        self.assertEqual((report['count'], report['applied']), (4, 3))
        self.assertAlmostEqual(report['maxCorrectionSeconds'], 1.0)

    def test_reject_unpaired_notes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'bad.mid'
            midi=mido.MidiFile();midi.tracks.append(mido.MidiTrack([mido.Message('note_on',note=60,velocity=70)]));midi.save(path)
            with self.assertRaises(ValueError):read_score(path)


if __name__ == '__main__':unittest.main()
