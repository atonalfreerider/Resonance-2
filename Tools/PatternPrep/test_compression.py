import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import mido

ROOT=Path(__file__).resolve().parents[2]
class CompressionTests(unittest.TestCase):
    def test_transposition_quality_change_hierarchy_and_reviewed_key(self):
        with tempfile.TemporaryDirectory() as directory:
            folder=Path(directory);path=folder/'fixture.mid'
            midi=mido.MidiFile(ticks_per_beat=480)
            midi.tracks.append(mido.MidiTrack([mido.MetaMessage('set_tempo',tempo=500000),mido.MetaMessage('key_signature',key='C')]))
            for channel in [0,2]:
                events=[]
                for beat in range(16):
                    root,third=[(60,64),(65,69),(67,70),(60,64)][beat//4]
                    for note in [root,third]:
                        events.extend([(beat*480,mido.Message('note_on',note=note,velocity=83,channel=channel)),(beat*480+240,mido.Message('note_off',note=note,channel=channel))])
                track=mido.MidiTrack();last=0
                for tick,message in sorted(events,key=lambda e:e[0]):track.append(message.copy(time=tick-last));last=tick
                track.append(mido.MetaMessage('end_of_track',time=16*480-last));midi.tracks.append(track)
            midi.save(path)
            (folder/'song.json').write_text(json.dumps(dict(Key=0,Minor=False,KeySource='Reviewed A major',SectionBoundaries='1 Verse\n3 Chorus',SectionParents=['Song/Movement I/Exposition','Song/Movement II'])))
            subprocess.run(['dotnet',str(ROOT/'Tools/PatternPrep/bin/Debug/net10.0/PatternPrep.dll'),str(path)],check=True,stdout=subprocess.DEVNULL)
            data=json.loads(Path(str(path)+'.patterns.json').read_text())
            self.assertEqual(data['PatternNoteCount'],64)
            self.assertEqual(data['TemplateNoteCount'],4)
            self.assertEqual(len(data['Templates']),2)
            self.assertTrue(all(frame['Key']==0 for frame in data['Frames']))
            self.assertTrue(any(v['Transpose']==5 for t in data['Templates'] for v in t['Variants']))
            self.assertTrue(any(v['PitchDelta']==[0,-1] for t in data['Templates'] for v in t['Variants']))
            self.assertEqual([n['Name'] for n in data['Form'] if n['Parent']==0],['Movement I','Movement II'])
            self.assertTrue(any(n['Path']=='Song/Movement I/Exposition/Verse' for n in data['Form']))
            self.assertEqual([len(s['Lanes']) for s in data['Sections']],[2,2])

if __name__=='__main__':unittest.main()
