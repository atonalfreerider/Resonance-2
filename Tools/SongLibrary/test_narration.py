import unittest,tempfile
from pathlib import Path
import numpy as np
from narration import fit_clip,speech_window,duck_gain,duck_envelope,DUCK_RANGE
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
 def test_story_pictures_stay_in_the_bundle_and_carry_credit(self):
  cue=dict(start=0,end=10,text='Listen.',view='Lyrics',uncoil=False,stem='',image='story/pictures/a.jpg',imageCaption='A',imageCredit='Photo: X, CC BY 2.0')
  with tempfile.TemporaryDirectory() as d:
   (Path(d)/'story/pictures').mkdir(parents=True);(Path(d)/'story/pictures/a.jpg').write_bytes(b'x')
   validate_cues([cue],10,[],(),d)
   for bad in [dict(image='../a.jpg'),dict(image='C:/a.jpg'),dict(image='story/pictures/a.gif'),dict(image='story/pictures/b.jpg'),dict(imageCredit=''),dict(imagePlacement='top')]:
    with self.assertRaises(ValueError):validate_cues([dict(cue,**bad)],10,[],(),d)
 def test_duck_puts_speech_clear_of_the_music(self):
  self.assertAlmostEqual(duck_gain(.1,.1),.95/10**(10/20),places=4)
  self.assertEqual(duck_gain(.1,0),DUCK_RANGE[1]);self.assertEqual(duck_gain(.01,1),DUCK_RANGE[0])
  env=duck_envelope([dict(start=1,end=2,duck=.2)],4)
  self.assertLess(env[150],.25);self.assertAlmostEqual(env[0],1);self.assertGreater(env[-1],.9)
if __name__=='__main__':unittest.main()
