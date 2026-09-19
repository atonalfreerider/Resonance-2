import copy
import unittest
import json
import tempfile
from pathlib import Path
from unittest.mock import patch
import urllib.error
from story import validate_cues, settle_transitions, seconds_at

class StoryChecks(unittest.TestCase):
    def setUp(self):
        self.cue=dict(start=0,end=20,text='Listen to the two voices.',view='Torus',uncoil=True,stem='vocals')
    def test_valid_solo(self):
        self.assertEqual(validate_cues([self.cue],30,['vocals']),[self.cue])
    def test_rejects_bad_controls_and_clock(self):
        for patch in [dict(start=-1),dict(end=31),dict(start=float('nan')),dict(stem='../secret'),dict(view='RunCode'),dict(view='Drums',uncoil=True),dict(text='')]:
            with self.subTest(patch=patch), self.assertRaises(ValueError):
                validate_cues([dict(self.cue,**patch)],30,['vocals'])
        with self.assertRaises(ValueError):validate_cues([self.cue,self.cue],30,['vocals'])
    def test_short_uncoil_is_suppressed_but_continuous_passage_survives(self):
        self.assertFalse(settle_transitions([dict(self.cue,end=8)])[0]['uncoil'])
        cues=[dict(self.cue,end=8),dict(self.cue,start=8,end=16)]
        self.assertTrue(all(c['uncoil'] for c in settle_transitions(cues)))
    def test_tempo_changes_and_duplicate_origin(self):
        data={'Tempos':[dict(Beat=0,Seconds=0,Microseconds=500000),dict(Beat=0,Seconds=0,Microseconds=1000000),dict(Beat=4,Seconds=4,Microseconds=250000)]}
        self.assertEqual(seconds_at(data,2),2)
        self.assertEqual(seconds_at(data,8),5)
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

if __name__=='__main__':unittest.main()
