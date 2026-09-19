import json, unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
class RegionCoverage(unittest.TestCase):
 def test_all_prepared_sections_are_continuous(self):
  songs=list((ROOT/'PreparedSongs').rglob('*.patterns.json'))
  self.assertTrue(songs)
  for path in songs:
   data=json.loads(path.read_text(encoding='utf-8'))
   for section in data['Sections']:
    phases=[p for p in data['RegionPhases'] if section['Start']<=p['Start'] and p['End']<=section['End']]
    with self.subTest(song=str(path),section=section['Start']):
     self.assertTrue(phases)
     self.assertEqual(phases[0]['Start'],section['Start'])
     self.assertEqual(phases[-1]['End'],section['End'])
     for phase in phases:
      self.assertGreater(phase['End'],phase['Start'])
      self.assertGreaterEqual(phase['Root'],0)
      self.assertIn(phase['Quality'],['','m','dim'])
     for a,b in zip(phases,phases[1:]):self.assertEqual(a['End'],b['Start'])
if __name__=='__main__':unittest.main()
