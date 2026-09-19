import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
import mido
import pretty_midi
import numpy as np
from common import validate_bundle,ROOT,atomic_json
from features import regrid,match_midi
from catalog import Catalog,bitmidi_search

class PipelineTests(unittest.TestCase):
    def test_beat_grid_preserves_notes_drums_programs_and_seconds(self):
        with tempfile.TemporaryDirectory() as folder:
            source=Path(folder)/'source.mid';dest=Path(folder)/'grid.mid'
            score=mido.MidiFile(ticks_per_beat=480)
            clock=mido.MidiTrack([mido.MetaMessage('set_tempo',tempo=500000),mido.MetaMessage('set_tempo',tempo=600000,time=960)])
            notes=mido.MidiTrack([mido.Message('program_change',program=24),mido.Message('note_on',note=60,velocity=95,time=240),mido.Message('note_off',note=60,time=480),mido.Message('note_on',channel=9,note=36,velocity=90,time=360),mido.Message('note_off',channel=9,note=36,time=120)])
            score.tracks.extend([clock,notes]);score.save(source)
            regrid(source,dest,[0,.4,.91,1.47,2.03],3)
            a=pretty_midi.PrettyMIDI(str(source));b=pretty_midi.PrettyMIDI(str(dest))
            for x,y in zip(a.instruments,b.instruments):
                self.assertEqual((x.program,x.is_drum),(y.program,y.is_drum))
                for n,m in zip(x.notes,y.notes):
                    self.assertEqual((n.pitch,n.velocity),(m.pitch,m.velocity))
                    self.assertLess(abs(n.start-m.start),.0001);self.assertLess(abs(n.end-m.end),.0001)
            self.assertEqual(b.time_signature_changes[0].numerator,3)

    def test_prepared_bundle_hash_and_regions(self):
        song=validate_bundle(ROOT/'PreparedSongs/TicketToRide-Restored')
        self.assertGreater(len(song['Notes']),5000)
        self.assertGreater(len(song['RegionPhases']),0)

    def test_catalog_exact_recording_and_title(self):
        with tempfile.TemporaryDirectory() as folder:
            c=Catalog(folder);c.index([ROOT/'PreparedSongs/TicketToRide-Restored'])
            self.assertTrue(c.search('Ticket To Ride'))
            self.assertEqual(len(c.exact('3cfba5aa32c256a2cf59979242da7e2feda0cf226a2c70bb0239db413dfc8cae')),1)

    def test_provider_uses_published_download_link(self):
        with patch('catalog.requests.get') as get:
            get.return_value.json.return_value={'result':{'results':[{'name':'Test song','slug':'test-song','downloadUrl':'/uploads/123.mid','url':'/test-song'}]}}
            self.assertEqual(bitmidi_search('test')[0]['url'],'https://bitmidi.com/uploads/123.mid')

    def test_unsafe_bundle_path_rejected(self):
        from server import bundle_path
        with self.assertRaises(ValueError):bundle_path('../../Windows')

    def test_fingerprint_rejects_static_unrelated_harmony(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'wrong.mid';p=pretty_midi.PrettyMIDI();i=pretty_midi.Instrument(0)
            i.notes=[pretty_midi.Note(100,n,0,10) for n in [60,64,67]];p.instruments=[i];p.write(str(path))
            chroma=np.zeros((12,108));chroma[[1,5,8],:]=1
            self.assertFalse(match_midi(path,chroma,10)['accepted'])

    def test_http_upload_and_session_guards(self):
        import requests,threading
        import server
        with tempfile.TemporaryDirectory() as folder, patch.object(server,'DATA',Path(folder)):
            http=server.ThreadingHTTPServer(('127.0.0.1',0),server.Handler)
            thread=threading.Thread(target=http.serve_forever,daemon=True);thread.start()
            base=f'http://127.0.0.1:{http.server_port}'
            try:
                self.assertEqual(requests.get(base+'/api/state').status_code,403)
                headers={'X-Song-Token':server.TOKEN,'X-Filename':'test.mid'}
                payload=b'MThd'+bytes(20)
                response=requests.post(base+'/api/upload',headers=headers,data=payload)
                self.assertEqual(response.status_code,200)
                self.assertEqual((Path(folder)/'uploads'/response.json()['id']/'test.mid').read_bytes(),payload)
                headers['Origin']='https://unrelated.example'
                self.assertEqual(requests.post(base+'/api/upload',headers=headers,data=payload).status_code,403)
                self.assertEqual(requests.get(base+'/api/bundle?id=../../Windows',headers={'X-Song-Token':server.TOKEN}).status_code,400)
            finally:http.shutdown();http.server_close();thread.join()

if __name__=='__main__':unittest.main()
