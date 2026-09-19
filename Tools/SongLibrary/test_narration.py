import unittest,tempfile
import numpy as np
from narration import fit_clip,speech_window
from story import validate_cues
class NarrationChecks(unittest.TestCase):
 def test_listening_scenes_and_explicit_windows(self):
  cue=dict(start=10,end=20)
  self.assertIsNone(speech_window(dict(cue,narrate=False)))
  self.assertEqual(speech_window(dict(cue,narrationStart=13,narrationEnd=18)),(13,18))
  for values in [dict(narrationStart=9),dict(narrationEnd=21),dict(narrationStart=float('nan')),dict(narrationEnd=10.1)]:
   with self.assertRaises(ValueError):speech_window(dict(cue,**values))
 def test_fit_preserves_pitch_and_bounds(self):
  rate=24000;x=.2*np.sin(2*np.pi*440*np.arange(rate*2)/rate)
  with tempfile.TemporaryDirectory() as d:y,ratio=fit_clip(x,rate,1.7,d)
  self.assertLessEqual(len(y),int(1.7*rate));self.assertLessEqual(abs(y).max(),.851)
  peak=np.argmax(abs(np.fft.rfft(y)))*rate/len(y)
  self.assertAlmostEqual(peak,440,delta=3);self.assertGreater(ratio,1)
 def test_rejects_silent_or_rushed_speech(self):
  with tempfile.TemporaryDirectory() as d:
   with self.assertRaises(ValueError):fit_clip(np.zeros(24000),24000,1,d)
   with self.assertRaises(ValueError):fit_clip(np.ones(24000),24000,.2,d)
 def test_annotation_targets_are_constrained(self):
  cue=dict(start=0,end=10,text='Listen.',view='Torus',uncoil=False,stem='',annotationTarget='melody',annotationLabel='Two voices')
  validate_cues([cue],10,[])
  with self.assertRaises(ValueError):validate_cues([dict(cue,annotationTarget='arbitrary')],10,[])
if __name__=='__main__':unittest.main()
