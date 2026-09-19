import csv, json, tempfile, unittest
from pathlib import Path
import mido
from restore_legacy import restore, pitch

class LegacyTests(unittest.TestCase):
    def test_authored_substitution_rests_repetition_and_drums(self):
        rows=[['Info','Label','Value'],['','BPM','120'],['','Key','C'],['Sequence','Track','0','1','2'],['','01-Piano','0[0]','-1[-1]','0[1]'],['','02-drums','0[0]'],
              ['01-Piano'],['','0'],['','','DivDownbeat','4'],['','','Sequences','0|1','1'],['','','Patterns','BeatStart','BeatEnd','Note','0','1'],['','','','1','2','c5','-','d5'],['','','','3','4','e5','-','x'],
              ['02-drums'],['','0'],['','','DivDownbeat','4'],['','','Sequences','0'],['','','Patterns','BeatStart','BeatEnd','Note','0'],['','','','1','1.25','c3','-']]
        with tempfile.TemporaryDirectory() as directory:
            folder=Path(directory);source=folder/'fixture.csv'
            with source.open('w',newline='') as f:csv.writer(f).writerows(rows)
            midi,authored,report=restore(source,folder/'out');data=json.loads(authored.read_text())
            self.assertEqual(report['notes'],5)
            piano=[d for d in data['Disks'] if d['Channel']==1];self.assertEqual(len(piano),2)
            self.assertEqual(piano[1]['Hits'][0]['Pitch'],62)
            self.assertEqual([v['Beat'] for v in piano[1]['Visits']],[4,12])
            drum=next(d for d in data['Disks'] if d['Channel']==10);self.assertEqual(drum['Hits'][0]['Pitch'],36)
            self.assertEqual(sum(m.type=='note_on' and m.velocity>0 for t in mido.MidiFile(midi).tracks for m in t),5)

    def test_event_csv_preserves_timing_velocity_channel_and_bend(self):
        rows=[['FRAME']+['']*16]
        def event(beat,kind,note='c5',velocity='',v1='',v2='',detail=''):
            rows.append(['0','0',str(beat*.5),'0','120','0',str(beat),'Test','',kind,detail,'2',note,str(velocity),str(v1),str(v2),''])
        event(0,'Set Tempo(BPM)',detail='500000');event(2,'Note On',velocity=93);event(3,'Pitch Wheel',v1=0,v2=80);event(4,'Note Off',velocity=17)
        with tempfile.TemporaryDirectory() as directory:
            folder=Path(directory);source=folder/'events.csv'
            with source.open('w',newline='') as f:csv.writer(f).writerows(rows)
            path,_,_=restore(source,folder/'out');time=0;notes=[];bend=None
            for m in mido.MidiFile(path):
                time+=m.time
                if m.type in ('note_on','note_off'):notes.append((m.type,time,m.note,m.velocity,m.channel))
                if m.type=='pitchwheel':bend=m.pitch
            self.assertEqual(notes,[('note_on',1,60,93,2),('note_off',2,60,17,2)])
            self.assertEqual(bend,2048)

if __name__=='__main__':unittest.main()
