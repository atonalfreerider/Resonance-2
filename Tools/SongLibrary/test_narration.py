import unittest,tempfile,hashlib,io,json,subprocess,sys,types
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import MagicMock,patch
import numpy as np
import soundfile as sf
import narration,story_timing
from narration import fit_clip,speech_window,duck_gain,duck_envelope,generate,speech_request,DUCK_RANGE
from story import validate_cues
from common import MODEL_PYTHON,sha
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
 def test_take_zero_keeps_every_cached_voice(self):
  # The identities every line had before takes existed: a take of 0 must find the voice already rendered.
  cue=dict(text='Two.',speech='The second line.');old=lambda provider,body,**voice:hashlib.sha256(json.dumps(dict(provider=provider,body=body,**voice),sort_keys=True).encode()).hexdigest()
  body,identity=speech_request(cue,'elevenlabs','eleven_v4','v','en-US',dict(delivery='calm'))
  self.assertEqual(identity,old('elevenlabs',dict(text='[calm] The second line.',model_id='eleven_v4',language_code='en',voice_settings=dict(stability=.5,similarity_boost=.75)),voice='v'))
  self.assertEqual(speech_request(dict(cue,take=0),'elevenlabs','eleven_v4','v','en-US',dict(delivery='calm')),(body,identity))
  again,other=speech_request(dict(cue,take=2),'elevenlabs','eleven_v4','v','en-US',dict(delivery='calm'))
  self.assertEqual(again,dict(body,seed=2));self.assertNotEqual(other,identity)
  for provider,model in (('cartesia','sonic-3.6'),('openai','gpt-4o-mini-tts')):
   body,identity=speech_request(cue,provider,model,'v','en-GB',{})
   self.assertEqual(identity,old(provider,body))
   again,other=speech_request(dict(cue,take=1),provider,model,'v','en-GB',{})
   self.assertEqual(again,body);self.assertNotEqual(other,identity)   # no seed to send: the take is the identity alone
 def test_takes_and_duck_floors_are_validated(self):
  cue=dict(start=0,end=10,text='Listen.',view='Torus',uncoil=False,stem='')
  for good in (dict(take=0),dict(take=3),dict(take=4294967295),dict(duckFloor=.35),dict(duckFloor=.8),dict(duckFloor=1e-3)):
   validate_cues([dict(cue,**good)],10,[])
  for bad in (dict(take=-1),dict(take=1.0),dict(take=True),dict(take='1'),dict(take=2**32),dict(duckFloor=0),dict(duckFloor=.81),dict(duckFloor=-.2),
              dict(duckFloor=float('nan')),dict(duckFloor=True),dict(duckFloor='.5'),dict(duckFloor=None)):
   with self.subTest(bad=bad),self.assertRaises(ValueError):validate_cues([dict(cue,**bad)],10,[])

class Rendering(unittest.TestCase):
 """generate() on a synthetic bundle, end to end: the speech API and the word aligner are mocked."""
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();root=Path(self.tmp.name);self.bundle=root/'bundle';self.bundle.mkdir();self.data=root/'data'
  # Loud music: every line measures the deepest duck, so a floor shows.
  sf.write(self.bundle/'recording.wav',np.random.default_rng(1).standard_normal(12*24000).astype('float32'),24000,subtype='FLOAT')
  (self.bundle/'aligned.mid.patterns.json').write_text('{}')
  (self.bundle/'aligned.mid.prepared.json').write_text(json.dumps(dict(audioPath='recording.wav',midiSha256='m',audioSha256='a',audioDuration=12.0,stems=[])))
  self.key=root/'key.txt';self.key.write_text('test-only-key')
  line=lambda start,**more:dict(start=start,end=start+4,text=f'Line at {start}.',view='Torus',stem='',uncoil=False,**more)
  self.story=dict(version=1,title='T',midiSha256='m',audioSha256='a',patternsSha256=sha(self.bundle/'aligned.mid.patterns.json'),duration=12.0,
                  narrationTargetFraction=1,narrator=dict(name='Test',provider='elevenlabs',voice='voice-id',language='en-US'),
                  cues=[line(0,speech='The first line.'),line(4),line(8,speech='The third line.')])
 def tearDown(self):self.tmp.cleanup()
 def render(self,run=None):
  (self.bundle/'story.json').write_text(json.dumps(self.story),encoding='utf-8')
  sent=[]
  def speak(request,timeout):
   sent.append(json.loads(request.data));t=np.arange(36000)/24000
   reply=MagicMock();reply.__enter__.return_value.read.return_value=(.3*np.sin(2*np.pi*600*t)*np.sin(np.pi*t/1.5)*32767).astype('<i2').tobytes();return reply
  def align(command,timeout):
   # What story_timing.py adds to narration.json, without torchaudio.
   self.command=command;path=Path(command[2])/'narration.json';manifest=json.loads(path.read_text(encoding='utf-8'))
   for s in manifest['segments']:s.update(spokenWords=['w'],spokenStarts=[.1],spokenEnds=[.5])
   path.write_text(json.dumps(manifest),encoding='utf-8')
  with patch('narration.validate_bundle'),patch('narration.DATA',self.data),patch('narration.urllib.request.urlopen',side_effect=speak),patch('narration.run',side_effect=run or align):
   result=generate(self.bundle,self.key,provider='elevenlabs',notify=lambda *_:None)
  return result,sent,json.loads((self.bundle/'narration.json').read_text(encoding='utf-8'))
 def test_a_new_take_renders_only_its_line(self):
  result,sent,manifest=self.render()
  self.assertEqual(sorted(r['text'] for r in sent),['Line at 4.','The first line.','The third line.']);self.assertFalse(any('seed' in r for r in sent))
  self.story['cues'][1]['take']=2
  _,sent,_=self.render()
  self.assertEqual([(r['text'],r['seed']) for r in sent],[('Line at 4.',2)])
  self.story['cues'][1]['take']=0                                       # the first take is still there
  _,sent,_=self.render();self.assertEqual(sent,[])
  # The words were aligned in the model environment and reached narration.json.
  self.assertEqual(self.command[:2],[MODEL_PYTHON,Path(narration.__file__).with_name('story_timing.py')]);self.assertEqual(Path(self.command[2]),self.bundle.resolve())
  self.assertEqual(result['alignedLines'],3);self.assertTrue(all(s['spokenStarts']==[.1] for s in manifest['segments']))
 def test_duck_floor_keeps_the_music_up(self):
  _,_,plain=self.render()
  self.assertTrue(all(s['duck']==DUCK_RANGE[0] and 'duckFloor' not in s and 'duckMeasured' not in s for s in plain['segments']))
  self.story['duckFloor']=.3;self.story['cues'][2]['duckFloor']=.5      # a cue's own floor wins over the story's
  _,sent,floored=self.render();self.assertEqual(sent,[])                # ducks do not render speech again
  self.assertEqual([(s['duck'],s['duckFloor']) for s in floored['segments']],[(.3,.3),(.3,.3),(.5,.5)])
  self.assertEqual([s['duckMeasured'] for s in floored['segments']],[s['duck'] for s in plain['segments']])
  env=duck_envelope(floored['segments'],12);self.assertGreater(env[int(9.5*100)],.49)  # what the preview and Unity apply
  self.story['duckFloor']=.9
  with self.assertRaisesRegex(ValueError,'duckFloor'):self.render()
 def test_a_failed_alignment_says_so(self):
  def fail(command,timeout):raise subprocess.CalledProcessError(1,command,stderr='Traceback (most recent call last):\nOSError: no aligner weights')
  with self.assertRaisesRegex(RuntimeError,'published.*no aligner weights'):self.render(fail)
  manifest=json.loads((self.bundle/'narration.json').read_text(encoding='utf-8'))
  self.assertTrue(manifest['segments'] and not any('spokenStarts' in s for s in manifest['segments']))
 def test_check_runs_in_the_model_environment(self):
  with patch('narration.subprocess.run',return_value=subprocess.CompletedProcess([],1)) as run:
   self.assertEqual(narration.check(self.bundle),1)
  self.assertEqual(run.call_args.args[0],[str(MODEL_PYTHON),str(Path(narration.__file__).with_name('narration_check.py')),str(self.bundle)])

class WordTiming(unittest.TestCase):
 def test_spoken_words_and_their_spelling_for_the_aligner(self):
  self.assertEqual(story_timing.spoken_words(dict(text='Op. 10',speech='[calm] Opus 10, the étude.')),['Opus','10,','the','étude.'])
  self.assertEqual(story_timing.spoken_words(dict(text='Shown')),['Shown'])
  self.assertEqual(story_timing.audible(['the','Étude','[softly,','slowly]','in','E']),['the','Etude','','','in','E'])
 def test_every_spoken_line_is_aligned(self):
  # Lines whose speech differs from their caption are aligned too (the captions show the spoken words); a line with
  # nothing to hear keeps the captions' estimate, and times left from an earlier narration go.
  with tempfile.TemporaryDirectory() as d:
   d=Path(d);sf.write(d/'n.wav',np.zeros(24000*6,dtype='float32'),24000,subtype='PCM_16')
   cues=[dict(text='Op. 10',speech='Opus ten, the étude.'),dict(text='Same words here'),dict(text='1832',speech='[calm] 1832')]
   (d/'story.json').write_text(json.dumps(dict(cues=cues)),encoding='utf-8')
   segments=[dict(cue=0,start=0,end=2,wordStarts=[9.]),dict(cue=1,start=2,end=4),dict(cue=2,start=4,end=6,spokenStarts=[1.],wordStarts=[1.])]
   (d/'narration.json').write_text(json.dumps(dict(storySha256=sha(d/'story.json'),sha256=sha(d/'n.wav'),audioPath='n.wav',segments=segments)),encoding='utf-8')
   heard=[]
   def align(model,tokenizer,aligner,bundle,wav,words):
    heard.append((words,sf.info(wav).duration));return [round(.3*i,3) for i in range(len(words))],[round(.3*i+.2,3) for i in range(len(words))]
   torch=types.SimpleNamespace(cuda=types.SimpleNamespace(is_available=lambda:False))
   with patch.dict(sys.modules,{'torch':torch,'torchaudio':types.SimpleNamespace(pipelines=types.SimpleNamespace(MMS_FA=MagicMock()))}),patch('story_timing.align',side_effect=align),redirect_stdout(io.StringIO()):
    story_timing.prepare(d)
   manifest=json.loads((d/'narration.json').read_text(encoding='utf-8'))
   (d/'story.json').write_text(json.dumps(dict(cues=cues[:1])),encoding='utf-8')
   with self.assertRaisesRegex(ValueError,'current story'):story_timing.prepare(d)
  self.assertEqual(heard,[(['Opus','ten,','the','etude.'],2.),(['Same','words','here'],2.)])
  first,second,third=manifest['segments']
  self.assertEqual(first['spokenWords'],['Opus','ten,','the','étude.']);self.assertEqual(first['spokenStarts'],[0,.3,.6,.9]);self.assertEqual(first['spokenEnds'],[.2,.5,.8,1.1])
  self.assertEqual((first['wordStarts'],first['wordEnds']),(first['spokenStarts'],first['spokenEnds']))
  self.assertEqual(second['spokenWords'],['Same','words','here'])
  self.assertFalse(set(story_timing.SPOKEN_FIELDS)&set(third))
  self.assertEqual(manifest['wordTimingMethod'],'MMS forced alignment of rendered narration')
if __name__=='__main__':unittest.main()
