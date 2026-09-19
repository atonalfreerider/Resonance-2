import tempfile
import unittest
from pathlib import Path
import mido
import pretty_midi
from stems import map_to_master,filter_score

class StemTests(unittest.TestCase):
    def test_master_meter_and_tempo_keep_physical_note_times(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);master=mido.MidiFile(ticks_per_beat=480)
            master.tracks.append(mido.MidiTrack([
                mido.MetaMessage('set_tempo',tempo=400000),
                mido.MetaMessage('time_signature',numerator=4,denominator=4),
                mido.MetaMessage('set_tempo',tempo=620000,time=733),
                mido.MetaMessage('time_signature',numerator=3,denominator=4,time=227),
                mido.MetaMessage('end_of_track',time=1920)]))
            master.save(root/'master.mid')
            source=pretty_midi.PrettyMIDI();instrument=pretty_midi.Instrument(0)
            instrument.notes=[pretty_midi.Note(100,60,.421,1.82),pretty_midi.Note(80,67,2.14,2.5)]
            source.instruments=[instrument];source.write(str(root/'source.mid'))
            map_to_master(root/'source.mid',root/'output.mid',root/'master.mid')
            actual=pretty_midi.PrettyMIDI(str(root/'output.mid'))
            expected=pretty_midi.PrettyMIDI(str(root/'source.mid'))
            for a,b in zip(expected.instruments[0].notes,actual.instruments[0].notes):
                self.assertLess(abs(a.start-b.start),.0002);self.assertLess(abs(a.end-b.end),.0002)
            self.assertEqual([x.numerator for x in actual.time_signature_changes],[4,3])

    def test_register_views_partition_notes_and_rhythm_discards_pitched_leakage(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);score=pretty_midi.PrettyMIDI();i=pretty_midi.Instrument(0);d=pretty_midi.Instrument(0,is_drum=True)
            i.notes=[pretty_midi.Note(90,n,0,.5) for n in (48,59,60,72)];d.notes=[pretty_midi.Note(100,36,0,.1)]
            score.instruments=[i,d];score.write(str(root/'raw.mid'))
            for kind,expected in [('other-low',[48,59]),('other-high',[60,72]),('drums',[36])]:
                filter_score(root/'raw.mid',root/'filtered.mid',kind)
                actual=pretty_midi.PrettyMIDI(str(root/'filtered.mid'))
                self.assertEqual([n.pitch for i in actual.instruments for n in i.notes],expected)
                self.assertEqual(actual.instruments[0].is_drum,kind=='drums')

if __name__=='__main__':unittest.main()
