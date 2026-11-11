import copy
import unittest
import json
import tempfile
from pathlib import Path
from unittest.mock import patch
import urllib.error
from story import validate_cues, settle_transitions, seconds_at, load_context

class StoryChecks(unittest.TestCase):
    def setUp(self):
        self.cue=dict(start=0,end=20,text='Listen to the two voices.',view='Torus',uncoil=True,stem='vocals')
    def test_valid_solo(self):
        self.assertEqual(validate_cues([self.cue],30,['vocals']),[self.cue])
    def test_rejects_bad_controls_and_clock(self):
        for patch in [dict(start=-1),dict(end=31),dict(start=float('nan')),dict(stem='../secret'),dict(view='RunCode'),dict(view='Drums',uncoil=True),dict(text=''),dict(concertoFocus='unknown'),dict(melodyTarget='unknown')]:
            with self.subTest(patch=patch), self.assertRaises(ValueError):
                validate_cues([dict(self.cue,**patch)],30,['vocals'])
        with self.assertRaises(ValueError):validate_cues([self.cue,self.cue],30,['vocals'])
    def test_chord_guide_requires_valid_pitch_class(self):
        cue=dict(self.cue,annotationTarget='chord',annotationPitchClass=10)
        self.assertEqual(validate_cues([cue],30,['vocals'])[0]['annotationPitchClass'],10)
        for pitch in (-1,12,None,'10'):
            with self.subTest(pitch=pitch),self.assertRaises(ValueError):
                validate_cues([dict(cue,annotationPitchClass=pitch)],30,['vocals'])
    def test_note_guide_requires_midi_pitch(self):
        cue=dict(self.cue,annotationTarget='note',annotationMidiPitch=29)
        self.assertEqual(validate_cues([cue],30,['vocals'])[0]['annotationMidiPitch'],29)
        for pitch in (20,117,None):
            with self.assertRaises(ValueError):validate_cues([dict(cue,annotationMidiPitch=pitch)],30,['vocals'])
    def test_short_uncoil_is_suppressed_but_continuous_passage_survives(self):
        self.assertFalse(settle_transitions([dict(self.cue,end=8)])[0]['uncoil'])
        cues=[dict(self.cue,end=8),dict(self.cue,start=8,end=16)]
        self.assertTrue(all(c['uncoil'] for c in settle_transitions(cues)))
    def test_tempo_changes_and_duplicate_origin(self):
        data={'Tempos':[dict(Beat=0,Seconds=0,Microseconds=500000),dict(Beat=0,Seconds=0,Microseconds=1000000),dict(Beat=4,Seconds=4,Microseconds=250000)]}
        self.assertEqual(seconds_at(data,2),2)
        self.assertEqual(seconds_at(data,8),5)
    def test_history_requires_known_sources(self):
        with tempfile.TemporaryDirectory() as folder:
            p=Path(folder)/'story.context.json'
            p.write_text(json.dumps(dict(version=1,sources=[],facts=[dict(text='A historical assertion',sourceIds=['missing'])])))
            with self.assertRaises(ValueError):load_context(folder)
        with self.assertRaises(ValueError):validate_cues([dict(self.cue,sourceIds=['invented'])],30,['vocals'],['verified'])
        validate_cues([dict(self.cue,sourceIds=['verified'])],30,['vocals'],['verified'])
    def test_request_keeps_key_out_of_payload_and_preserves_story_on_failure(self):
        from story import generate
        with tempfile.TemporaryDirectory() as folder:
            bundle=Path(folder);key=bundle/'private-key.txt';key.write_text('test-only-secret')
            (bundle/'story.json').write_text('previous story')
            manifest=dict(audioDuration=30,stems=[])
            with patch('story.validate_bundle',return_value={}),patch('story.read_json',return_value=manifest),patch('story.summarize',return_value={'title':'Synthetic'}),patch('story.urllib.request.urlopen') as send:
                send.side_effect=urllib.error.HTTPError('https://api.openai.com/v1/responses',401,'denied',{},None)
                with self.assertRaisesRegex(RuntimeError,'HTTP 401') as error:generate(bundle,key)
                request=send.call_args.args[0]
                body=json.loads(request.data)
                self.assertFalse(body['store'])
                self.assertNotIn('test-only-secret',request.data.decode())
                self.assertNotIn('test-only-secret',str(error.exception))
                self.assertEqual((bundle/'story.json').read_text(),'previous story')
    def test_published_story_keeps_takes_and_duck_floors(self):
        # A director's story-level duckFloor and a cue's take and duckFloor reach story.json, where narration.py reads them.
        from publish_story import publish
        with tempfile.TemporaryDirectory() as folder:
            bundle=Path(folder)
            (bundle/'aligned.mid.prepared.json').write_text(json.dumps(dict(audioDuration=30,midiSha256='m',audioSha256='a',stems=[])))
            (bundle/'aligned.mid.patterns.json').write_text('{}')
            draft=dict(title='T',narrator=dict(name='N'),duckFloor=.35,cues=[dict(start=0,end=20,text='Listen.',take=2,duckFloor=.5)])
            (bundle/'story.draft.json').write_text(json.dumps(draft))
            with patch('publish_story.validate_bundle'):publish(bundle)
            story=json.loads((bundle/'story.json').read_text(encoding='utf-8'))
            self.assertEqual((story['duckFloor'],story['cues'][0]['take'],story['cues'][0]['duckFloor']),(.35,2,.5))
            for bad in (dict(draft,duckFloor=0),dict(draft,duckFloor=1),dict(draft,cues=[dict(draft['cues'][0],take=-1)])):
                (bundle/'story.draft.json').write_text(json.dumps(bad))
                with self.subTest(bad=bad),patch('publish_story.validate_bundle'),self.assertRaises(ValueError):publish(bundle)

if __name__=='__main__':unittest.main()
