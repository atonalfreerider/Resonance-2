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
 def render(self,run=None,job=False):
  """generate() with the speech API and the aligner mocked; with `job`, as the Song Workshop runs it (/api/narrate), and
  the job's record comes back in place of the result."""
  (self.bundle/'story.json').write_text(json.dumps(self.story),encoding='utf-8')
  sent=[];self.notes=[]
  def speak(request,timeout):
   sent.append(json.loads(request.data));t=np.arange(36000)/24000
   reply=MagicMock();reply.__enter__.return_value.read.return_value=(.3*np.sin(2*np.pi*600*t)*np.sin(np.pi*t/1.5)*32767).astype('<i2').tobytes();return reply
  def align(command,timeout):
   # What story_timing.py adds to narration.json, without torchaudio.
   self.command=command;path=Path(command[2])/'narration.json';manifest=json.loads(path.read_text(encoding='utf-8'))
   for s in manifest['segments']:s.update(spokenWords=['w'],spokenStarts=[.1],spokenEnds=[.5])
   path.write_text(json.dumps(manifest),encoding='utf-8')
  with patch('narration.validate_bundle'),patch('narration.DATA',self.data),patch('narration.urllib.request.urlopen',side_effect=speak),patch('narration.run',side_effect=run or align):
   if job:
    import server
    with patch.object(server,'DATA',self.data):
     key=server.enqueue(lambda notify:generate(self.bundle,self.key,provider='elevenlabs',notify=notify))
     server.POOL.submit(lambda:None).result()                           # one worker: the job has finished
    result=server.JOBS[key]
   else:result=generate(self.bundle,self.key,provider='elevenlabs',notify=self.notes.append)
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
 def test_a_floor_says_what_it_leaves_of_the_voice(self):
  # Voice and music measured alike: every line ducks to 0.3004 for its 10 dB. The story's floor of 0.3 lifts nothing;
  # the last line's 0.5 lifts its music until the voice stands 5.6 dB clear, which is said, with the floor keeping 6 dB.
  plain_result,_,plain=self.render();self.assertFalse(any('speechOverMusicDb' in s for s in plain['segments']));self.assertEqual(plain_result['warnings'],[])
  self.story['duckFloor']=.3;self.story['cues'][2]['duckFloor']=.5
  with patch('narration.band_rms',return_value=.1):result,_,floored=self.render()
  self.assertEqual([(s['duckMeasured'],s['duck'],s['speechOverMusicDb']) for s in floored['segments']],[(.3004,.3004,10.),(.3004,.3004,10.),(.3004,.5,5.6)])
  warnings=[n for n in self.notes if n.startswith('Warning')]
  self.assertEqual(len(warnings),1);self.assertIn("cue 2's duckFloor 0.5 leaves its voice 5.6 dB above the music",warnings[0]);self.assertIn('a floor of 0.47 keeps 6 dB',warnings[0])
  self.assertEqual(result['warnings'],warnings)                         # the result keeps what was said
  # Music far louder than the voice: the line's own duck is already the deepest, and no floor keeps 6 dB.
  del self.story['duckFloor']
  with patch('narration.band_rms',side_effect=[.01,1.]*3):_,_,loud=self.render()
  self.assertEqual([(s['duckMeasured'],s['duck'],s['speechOverMusicDb']) for s in loud['segments'] if 'duckFloor' in s],[(DUCK_RANGE[0],.5,-34.4)])
  self.assertIn('even its measured duck leaves under 6 dB',[n for n in self.notes if n.startswith('Warning')][0])
 def test_a_workshop_job_keeps_the_floor_warning(self):
  # The page shows a job's latest message, which moves on from the warning to "Complete": the job's result keeps it
  # (index.html lists result.warnings under the job), and so does the job's file.
  self.story['cues'][2]['duckFloor']=.5
  with patch('narration.band_rms',return_value=.1):job,_,_=self.render(job=True)
  self.assertEqual((job['state'],job['message']),('complete','Complete — ready for review'))
  self.assertEqual(len(job['result']['warnings']),1);self.assertIn("cue 2's duckFloor 0.5 leaves its voice 5.6 dB above the music",job['result']['warnings'][0])
  saved=json.loads((self.data/'ui-jobs'/(job['id']+'.json')).read_text(encoding='utf-8'))
  self.assertEqual(saved['result']['warnings'],job['result']['warnings'])
  # A render whose words then fail to align ends the job with that error: the warning goes with it.
  def fail(command,timeout):raise subprocess.CalledProcessError(1,command,stderr='OSError: no aligner weights')
  with patch('narration.band_rms',return_value=.1):job,_,_=self.render(fail,job=True)
  self.assertEqual(job['state'],'failed');self.assertRegex(job['error'],"no aligner weights.*cue 2's duckFloor 0.5")
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
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup)
 def bundle(self,cues,segments):
  d=Path(self.tmp.name);sf.write(d/'n.wav',np.zeros(24000*6,dtype='float32'),24000,subtype='PCM_16')
  (d/'story.json').write_text(json.dumps(dict(cues=cues)),encoding='utf-8')
  (d/'narration.json').write_text(json.dumps(dict(storySha256=sha(d/'story.json'),sha256=sha(d/'n.wav'),audioPath='n.wav',segments=segments)),encoding='utf-8')
  return d
 def prepare(self,d,align=None,cuda=False,lines=None):
  """story_timing.prepare without torch: `align` stands for the MMS aligner, or `lines` for a whole pass on a device."""
  torch=types.SimpleNamespace(cuda=types.SimpleNamespace(is_available=lambda:cuda))
  with patch.dict(sys.modules,{'torch':torch,'torchaudio':types.SimpleNamespace(pipelines=types.SimpleNamespace(MMS_FA=MagicMock()))}),redirect_stdout(io.StringIO()) as said:
   with patch('story_timing.align',side_effect=align) if align else patch('story_timing.align_lines',side_effect=lines):
    result=story_timing.prepare(d)
  return result,said.getvalue(),json.loads((d/'narration.json').read_text(encoding='utf-8'))
 def test_spoken_words_and_what_the_aligner_is_given(self):
  self.assertEqual(story_timing.spoken_words(dict(text='Op. 10',speech='[calm] Opus 10, the étude.')),['Opus','10,','the','étude.'])
  self.assertEqual(story_timing.spoken_words(dict(text='Shown')),['Shown'])
  self.assertEqual(story_timing.spoken_words(dict(text='Shown',speech=' ')),['Shown'])     # as SongRecordingCaptions: blank speech shows the caption
  self.assertEqual(story_timing.caption_words(dict(text='x',speech='[calm] Opus 10')),['[calm]','Opus','10'])
  say=lambda text:story_timing.say(text.split())
  self.assertEqual(say('the Étude [softly, slowly] in E'),[['the'],['etude'],[],[],['in'],['ee']])
  # The aligner hears letters: a note's letter is given as its name, and symbols and digits as words.
  self.assertEqual(say('falls a fifth: G♯ to C sharp, then A′.'),[['falls'],['a'],['fifth'],['gee','sharp'],['to'],['see'],['sharp'],['then'],['ay','prime']])
  self.assertEqual(say('B7 Bb7 Am7 F#m Cmaj7'),[['bee','seven'],['bee','flat','seven'],['ay','minor','seven'],['eff','sharp','minor'],['see','major','seven']])
  self.assertEqual(say('A study. A major chord, in A.'),[['a'],['study'],['ay'],['major'],['chord'],['in'],['ay']])
  self.assertEqual(say('6ths in 2009, 1832, 1905, the 1990s, Kit 03, the 21st, bars 6–13'),
                   [['sixths'],['in'],['two','thousand','nine'],['eighteen','thirty','two'],['nineteen','oh','five'],['the'],['nineteen','nineties'],
                    ['kit'],['oh','three'],['the'],['twenty','first'],['bars'],['six','to','thirteen']])
  self.assertEqual(say('P.S. 1 PS1 DJ $4 #3 50% Dr. —'),[['pee','ess'],['one'],['pee','ess','one'],['dee','jay'],['four','dollars'],['number','three'],['fifty','percent'],['doctor'],[]])
 def test_word_times_join_what_is_said_for_each_word(self):
  # "G♯ to C." is given as gee, sharp, to, see: each word takes its first start and its last end, and an end reaches
  # the next start across a gap too short for a pause (the aligner ends "to" a frame after it starts).
  given=[]
  def align(model,tokenizer,aligner,bundle,wav,words):given.append(words);return [.1,.3,.6,.9],[.25,.5,.62,1.]
  with patch('story_timing.align',side_effect=align):starts,ends=story_timing.word_times(None,None,None,None,'w.wav',['G♯','to','C.'])
  self.assertEqual(given,[['gee','sharp','to','see']]);self.assertEqual((starts,ends),([.1,.6,.9],[.6,.9,1.]))
  # After punctuation 0.2 s is a pause, and the word ends where it was heard; inside a phrase 0.3 s is not.
  with patch('story_timing.align',side_effect=lambda *a:([0.,.5,1.],[.3,.7,1.2])):_,ends=story_timing.word_times(None,None,None,None,'w.wav',['third,','then','home.'])
  self.assertEqual(ends,[.3,1.,1.2])
  # A [direction] nobody says is given empty, so the aligner places it between its neighbours.
  with patch('story_timing.align',side_effect=lambda *a:([0.,.4,.4],[.4,.4,.8])) as aligned:story_timing.word_times(None,None,None,None,'w.wav',['Now','[pause]','go'])
  self.assertEqual(aligned.call_args.args[5],['now','','go'])
 def test_every_spoken_line_is_aligned(self):
  # Lines whose speech differs from their caption are aligned too (the captions show the spoken words); a line of
  # digits is aligned as they are said; a line with nothing to hear keeps the captions' estimate, and times left from
  # an earlier narration go.
  cues=[dict(text='Op. 10',speech='Opus ten, the étude.'),dict(text='Same words here'),dict(text='1832',speech='[calm] 1832'),dict(text='[sighs]')]
  d=self.bundle(cues,[dict(cue=0,start=0,end=2,wordStarts=[9.]),dict(cue=1,start=2,end=4),dict(cue=2,start=4,end=5,spokenStarts=[1.]),dict(cue=3,start=5,end=6,wordStarts=[1.])])
  heard=[]
  def align(model,tokenizer,aligner,bundle,wav,words):
   heard.append((words,sf.info(wav).duration));return [round(.3*i,3) for i in range(len(words))],[round(.3*i+.2,3) for i in range(len(words))]
  aligned,_,manifest=self.prepare(d,align)
  self.assertEqual(heard,[(['opus','ten','the','etude'],2.),(['same','words','here'],2.),(['eighteen','thirty','two'],1.)])
  self.assertEqual(aligned,3)
  first,second,third,fourth=manifest['segments']
  # Each word ends where the next starts (gaps of 0.1 s are no pause); the last ends where the aligner heard it end.
  self.assertEqual(first['spokenWords'],['Opus','ten,','the','étude.']);self.assertEqual(first['spokenStarts'],[0,.3,.6,.9]);self.assertEqual(first['spokenEnds'],[.3,.6,.9,1.1])
  self.assertEqual((first['wordStarts'],first['wordEnds']),(first['spokenStarts'],first['spokenEnds']))
  self.assertEqual(second['spokenWords'],['Same','words','here'])
  # "[calm] 1832": the voice says one word, SongRecordingCaptions shows two; the direction waits for the first spoken word.
  self.assertEqual((third['spokenWords'],third['spokenStarts'],third['spokenEnds']),(['1832'],[0],[.8]))
  self.assertEqual((third['wordStarts'],third['wordEnds']),([0,0],[0,.8]))
  self.assertFalse(set(story_timing.SPOKEN_FIELDS)&set(fourth))
  self.assertEqual(manifest['wordTimingMethod'],'MMS forced alignment of rendered narration')
  (d/'story.json').write_text(json.dumps(dict(cues=cues[:1])),encoding='utf-8')
  with self.assertRaisesRegex(ValueError,'current story'):self.prepare(d,align)
 def test_a_failed_gpu_pass_is_aligned_again_on_the_cpu(self):
  d=self.bundle([dict(text='Two words')],[dict(cue=0,start=0,end=2)])
  devices=[]
  def lines(bundle,device,story,manifest,signal,rate):
   devices.append(device)
   if device=='cuda':raise RuntimeError('CUDA error: out of memory')
   return [dict(spokenWords=['Two','words'],spokenStarts=[0,.5],spokenEnds=[.5,.9],wordStarts=[0,.5],wordEnds=[.5,.9])]
  aligned,said,manifest=self.prepare(d,cuda=True,lines=lines)
  self.assertEqual((devices,aligned,manifest['segments'][0]['spokenStarts']),(['cuda','cpu'],1,[0,.5]))
  self.assertIn('aligning on the CPU',said)
  devices.clear()
  def broken(bundle,device,*rest):devices.append(device);raise RuntimeError('no aligner weights')
  with self.assertRaisesRegex(RuntimeError,'no aligner weights'):self.prepare(d,cuda=False,lines=broken)
  self.assertEqual(devices,['cpu'])
 def test_a_narration_replaced_while_aligning_is_left_as_it_is(self):
  # A second render publishing while the first one's words are aligned: the first one's words must not overwrite it.
  d=self.bundle([dict(text='Two words')],[dict(cue=0,start=0,end=2)])
  newer=dict(json.loads((d/'narration.json').read_text(encoding='utf-8')),sha256='newer',audioPath='narration/newer/narration.wav')
  def lines(*args):
   (d/'narration.json').write_text(json.dumps(newer),encoding='utf-8')
   return [dict(spokenWords=['Two','words'],spokenStarts=[0,.5],spokenEnds=[.5,.9],wordStarts=[0,.5],wordEnds=[.5,.9])]
  aligned,said,manifest=self.prepare(d,lines=lines)
  self.assertIsNone(aligned);self.assertEqual(manifest,newer);self.assertIn('left as it is',said)
  # The same narration, rewritten meanwhile (another alignment, a field added): the words join what is there now.
  current=json.loads((d/'narration.json').read_text(encoding='utf-8'))
  current.update(sha256=sha(d/'n.wav'),audioPath='n.wav',previewPath='p.mp3')
  (d/'narration.json').write_text(json.dumps(current),encoding='utf-8')
  def again(*args):
   manifest=json.loads((d/'narration.json').read_text(encoding='utf-8'));manifest['segments'][0]['duck']=.5
   (d/'narration.json').write_text(json.dumps(manifest),encoding='utf-8')
   return [dict(spokenWords=['Two','words'],spokenStarts=[0,.5],spokenEnds=[.5,.9],wordStarts=[0,.5],wordEnds=[.5,.9])]
  aligned,_,manifest=self.prepare(d,lines=again)
  self.assertEqual((aligned,manifest['previewPath'],manifest['segments'][0]['duck'],manifest['segments'][0]['spokenStarts']),(1,'p.mp3',.5,[0,.5]))
if __name__=='__main__':unittest.main()
