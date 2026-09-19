import unittest,tempfile,subprocess,json
from pathlib import Path
import mido
ROOT=Path(__file__).resolve().parents[2]
class DrumBars(unittest.TestCase):
 def test_meter_and_reused_variations(self):
  with tempfile.TemporaryDirectory() as directory:
   path=Path(directory)/'meter.mid';m=mido.MidiFile(ticks_per_beat=480)
   m.tracks.append(mido.MidiTrack([mido.MetaMessage('time_signature',numerator=4,denominator=4),mido.MetaMessage('time_signature',numerator=3,denominator=4,time=8*480),mido.MetaMessage('end_of_track',time=6*480)]))
   events=[]
   for start,length in [(0,4),(4,4),(8,3),(11,3)]:
    for count in range(length):
     for pitch in [42]+([36] if count%2==0 else [38]):
      events.extend([(int((start+count)*480),mido.Message('note_on',note=pitch,velocity=90,channel=9)),(int((start+count)*480+60),mido.Message('note_off',note=pitch,channel=9))])
   # A small extra hat belongs to the same family but is a different variation.
   events.extend([(7*480+240,mido.Message('note_on',note=42,velocity=60,channel=9)),(7*480+300,mido.Message('note_off',note=42,channel=9))])
   tr=mido.MidiTrack();last=0
   for tick,event in sorted(events,key=lambda e:e[0]):tr.append(event.copy(time=tick-last));last=tick
   m.tracks.append(tr);m.save(path)
   subprocess.run(['dotnet','run','--project',str(ROOT/'Tools/PatternPrep/PatternPrep.csproj'),'--',str(path)],check=True,capture_output=True)
   data=json.loads(Path(str(path)+'.patterns.json').read_text());bars=data['DrumBars']
   self.assertEqual([b['Numerator'] for b in bars],[4,4,3,3])
   self.assertEqual([b['End']-b['Start'] for b in bars],[4,4,3,3])
   self.assertEqual(bars[0]['Family'],bars[1]['Family']);self.assertNotEqual(bars[0]['Variant'],bars[1]['Variant'])
   self.assertEqual(sum(len(b['Hits']) for b in bars),len(data['Notes']))
   for b in bars:self.assertEqual(len(set(b['Slots'])),len(b['Hits']))
if __name__=='__main__':unittest.main()
