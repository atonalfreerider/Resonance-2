"""Loopback-only manager. One analysis job at a time; no analysis in Unity."""
import json
import re
import secrets
import threading
import uuid
import webbrowser
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse,parse_qs,unquote
from common import *

TOKEN=secrets.token_urlsafe(32)
POOL=ThreadPoolExecutor(max_workers=1)
JOBS={};LOCK=threading.Lock()

def bundles():
    result=[]
    for p in sorted((ROOT/'PreparedSongs').rglob('aligned.mid.prepared.json')):
        relative=str(p.parent.relative_to(ROOT/'PreparedSongs')).replace('\\','/')
        provenance=read_json(p.parent/'library.json',{})
        result.append(dict(id=relative,title=provenance.get('title',p.parent.name),
                           reviewed=provenance.get('reviewed',False),method=provenance.get('method','existing prepared song'),
                           settings=read_json(p.parent/'song.json',{})))
    return result

def bundle_path(identifier):
    root=(ROOT/'PreparedSongs').resolve();path=(root/identifier).resolve()
    if not path.is_relative_to(root) or not (path/'aligned.mid.prepared.json').is_file(): raise ValueError('Unknown bundle')
    return path

def enqueue(fn):
    key=uuid.uuid4().hex
    def status(message):
        with LOCK:JOBS[key]['message']=message;atomic_json(DATA/'ui-jobs'/f'{key}.json',JOBS[key])
    with LOCK:JOBS[key]=dict(id=key,state='queued',message='Waiting for analysis worker')
    def work():
        try:
            with LOCK:JOBS[key]['state']='running'
            result=fn(status)
            with LOCK:JOBS[key].update(state='complete',result=result)
            status('Complete — ready for review')
        except Exception as error:
            import traceback
            with LOCK:JOBS[key].update(state='failed',error=str(error))
            (DATA/'ui-jobs').mkdir(parents=True,exist_ok=True)
            (DATA/'ui-jobs'/f'{key}.log').write_text(traceback.format_exc(),encoding='utf-8')
            status('Failed: '+str(error))
    POOL.submit(work);return key

class Handler(BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def respond(self,value,status=200,kind='application/json'):
        content=json.dumps(value).encode() if kind=='application/json' else value
        self.send_response(status);self.send_header('Content-Type',kind);self.send_header('Content-Length',str(len(content)))
        self.send_header('Cache-Control','no-store');self.send_header('X-Content-Type-Options','nosniff');self.end_headers();self.wfile.write(content)
    def authorized(self):
        if self.headers.get('Host')!=f'127.0.0.1:{self.server.server_port}':return False
        origin=self.headers.get('Origin')
        return (not origin or origin==f'http://127.0.0.1:{self.server.server_port}') and secrets.compare_digest(self.headers.get('X-Song-Token',''),TOKEN)
    def do_GET(self):
        try:
            path=urlparse(self.path).path
            if self.headers.get('Host')!=f'127.0.0.1:{self.server.server_port}':return self.respond({'error':'Invalid host'},403)
            if path=='/':
                page=(Path(__file__).parent/'index.html').read_text(encoding='utf-8').replace('__TOKEN__',TOKEN)
                return self.respond(page.encode(),kind='text/html; charset=utf-8')
            if not self.authorized():return self.respond({'error':'Invalid session'},403)
            if path=='/api/state':
                with LOCK:jobs=list(JOBS.values())
                return self.respond(dict(bundles=bundles(),jobs=jobs,storyEnabled=bool(getattr(self.server,'story_key_file',None)),narrationEnabled=bool(getattr(self.server,'narration_key_file',None))))
            if path=='/api/bundle':
                p=bundle_path(parse_qs(urlparse(self.path).query)['id'][0]);patterns=read_json(p/'aligned.mid.patterns.json')
                def seconds(beat):
                    tempo=next(t for t in reversed(patterns['Tempos']) if t['Beat']<=beat)
                    return tempo['Seconds']+(beat-tempo['Beat'])*tempo['Microseconds']/1e6
                return self.respond(dict(settings=read_json(p/'song.json',{}),provenance=read_json(p/'library.json',{}),
                    tracks=patterns['TrackNames'],sections=[{k:s[k] for k in ('Name','FirstBar','BarCount','Family')} for s in patterns['Sections']],notes=len(patterns['Notes']),
                    templates=patterns['TemplateNoteCount'],key=patterns['Key'],bars=[dict(bar=i+1,seconds=seconds(b['Start'])) for i,b in enumerate(patterns['Measures'])]))
            if path=='/api/audio':
                p=bundle_path(parse_qs(urlparse(self.path).query)['id'][0])
                validate_bundle(p)
                return self.respond((p/'recording.wav').read_bytes(),kind='audio/wav')
            return self.respond({'error':'Not found'},404)
        except Exception as error:self.respond({'error':str(error)},400)
    def do_POST(self):
        try:
            if not self.authorized():return self.respond({'error':'Invalid session'},403)
            path=urlparse(self.path).path;size=int(self.headers.get('Content-Length','0'))
            if size<=0 or size>512*1024*1024:return self.respond({'error':'Upload limit is 512 MB'},413)
            if path=='/api/upload':
                name=Path(unquote(self.headers.get('X-Filename','file'))).name
                if Path(name).suffix.lower() not in ('.mp3','.wav','.flac','.ogg','.m4a','.mid','.midi'):raise ValueError('Unsupported file type')
                identifier=uuid.uuid4().hex;folder=DATA/'uploads'/identifier;folder.mkdir(parents=True)
                with (folder/name).open('wb') as f:
                    remaining=size
                    while remaining:
                        block=self.rfile.read(min(1024*1024,remaining))
                        if not block:raise ValueError('Incomplete upload')
                        f.write(block);remaining-=len(block)
                return self.respond(dict(id=identifier,name=name))
            if size>65536:raise ValueError('Request too large')
            body=json.loads(self.rfile.read(size))
            if path=='/api/ingest':
                def upload(key):
                    value=body.get(key)
                    if not value:return None
                    if not re.fullmatch('[a-f0-9]{32}',value):raise ValueError('Invalid upload')
                    return next((DATA/'uploads'/value).iterdir())
                audio=upload('audio');midi=upload('midi')
                from pipeline import ingest
                return self.respond(dict(job=enqueue(lambda notify:ingest(audio,midi,body.get('title'),bool(body.get('online',True)),bool(body.get('neural',False)),int(body.get('meter',4)),notify))))
            if path=='/api/review':
                from pipeline import review
                p=bundle_path(body['id']);settings=body['settings']
                return self.respond(dict(job=enqueue(lambda notify:review(p,settings))))
            if path=='/api/stems':
                from stems import enrich
                p=bundle_path(body['id'])
                return self.respond(dict(job=enqueue(lambda notify:enrich(p,notify))))
            if path=='/api/export':
                from pipeline import export
                p=bundle_path(body['id'])
                return self.respond(dict(job=enqueue(lambda notify:export(p,ROOT/'Builds/SongBundles'/(p.name+'.zip')))))
            if path=='/api/narrate':
                from narration import generate
                p=bundle_path(body['id']);key_file=getattr(self.server,'narration_key_file',None)
                if not key_file:raise ValueError('Configure the local key file for the selected narration provider')
                return self.respond(dict(job=enqueue(lambda notify:generate(p,key_file,voice=self.server.narration_voice,provider=self.server.narration_provider,notify=notify))))
            if path=='/api/story':
                from story import generate
                p=bundle_path(body['id']);key_file=getattr(self.server,'story_key_file',None)
                if not key_file:raise ValueError('Start Song Workshop with --story-key-file to enable story generation')
                return self.respond(dict(job=enqueue(lambda notify:generate(p,key_file))))
            return self.respond({'error':'Not found'},404)
        except Exception as error:self.respond({'error':str(error)},400)

def serve(port=8765,browser=True,story_key_file=None):
    DATA.mkdir(parents=True,exist_ok=True)
    story_key_file=story_key_file or read_json(DATA/'settings.json',{}).get('storyKeyFile')
    for p in (DATA/'ui-jobs').glob('*.json'):
        job=read_json(p)
        if job['state'] in ('queued','running'):job.update(state='interrupted',message='Worker stopped; re-import to retry. Original inputs are preserved.')
        JOBS[job['id']]=job
    try:server=ThreadingHTTPServer(('127.0.0.1',port),Handler)
    except OSError:
        # Double-clicking the launcher again should open the existing manager.
        from urllib.request import urlopen
        url=f'http://127.0.0.1:{port}'
        if browser:
            with urlopen(url,timeout=2) as response:
                if b'Song workshop' in response.read(65536):webbrowser.open(url);return
        raise
    server.story_key_file=story_key_file
    settings=read_json(DATA/'settings.json',{})
    server.narration_provider=settings.get('narrationProvider','openai')
    server.narration_voice=settings.get('narrationVoice')
    server.narration_key_file=settings.get('cartesiaKeyFile') if server.narration_provider=='cartesia' else story_key_file
    url=f'http://127.0.0.1:{server.server_port}'
    print('Song manager: '+url,flush=True)
    if browser:webbrowser.open(url)
    try:server.serve_forever()
    finally:server.server_close();POOL.shutdown(wait=False)
