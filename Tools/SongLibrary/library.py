"""Command-line entry point; use the SongPrep environment for analysis orchestration."""
import argparse
import json
from pathlib import Path
from common import *
from catalog import Catalog

def main():
    parser=argparse.ArgumentParser(description='Resonance offline song library')
    sub=parser.add_subparsers(dest='command',required=True)
    p=sub.add_parser('ingest');p.add_argument('audio');p.add_argument('--midi');p.add_argument('--title');p.add_argument('--offline',action='store_true');p.add_argument('--neural',action='store_true');p.add_argument('--meter',type=int,choices=[3,4],default=4)
    p=sub.add_parser('index');p.add_argument('directories',nargs='+')
    p=sub.add_parser('stems');p.add_argument('bundle')
    p=sub.add_parser('repair-bass');p.add_argument('bundle')
    p=sub.add_parser('narrate');p.add_argument('bundle');p.add_argument('--key-file',required=True);p.add_argument('--voice',default='onyx')
    p=sub.add_parser('story');p.add_argument('bundle');p.add_argument('--key-file',required=True);p.add_argument('--model',default='gpt-4.1')
    p=sub.add_parser('search');p.add_argument('title');p.add_argument('--online',action='store_true')
    p=sub.add_parser('export');p.add_argument('bundle');p.add_argument('zip')
    p=sub.add_parser('review');p.add_argument('bundle');p.add_argument('settings',help='JSON with Key, Minor, LeadVocalTrack, SectionBoundaries, SectionParents')
    p=sub.add_parser('serve');p.add_argument('--port',type=int,default=8765);p.add_argument('--no-browser',action='store_true');p.add_argument('--story-key-file')
    sub.add_parser('doctor');sub.add_parser('list')
    a=parser.parse_args()
    if a.command=='ingest':
        from pipeline import ingest
        result=ingest(a.audio,a.midi,a.title,not a.offline,a.neural,a.meter,lambda s:print(s,flush=True))
    elif a.command=='repair-bass':
        from repair_bass import repair
        result=repair(a.bundle)
    elif a.command=='narrate':
        from narration import generate
        result=generate(a.bundle,a.key_file,a.voice)
    elif a.command=='story':
        from story import generate
        result=generate(a.bundle,a.key_file,a.model)
    elif a.command=='index':result={'indexed':Catalog().index(a.directories)}
    elif a.command=='stems':
        from stems import enrich
        result=enrich(a.bundle,lambda s:print(s,flush=True))
    elif a.command=='search':
        result=Catalog().search(a.title)
        if a.online:
            from catalog import bitmidi_search
            result+=bitmidi_search(a.title)
    elif a.command=='export':
        from pipeline import export
        result=export(a.bundle,a.zip)
    elif a.command=='review':
        from pipeline import review
        result=review(a.bundle,read_json(a.settings))
    elif a.command=='serve':
        from server import serve
        serve(a.port,not a.no_browser,a.story_key_file);return
    elif a.command=='list':
        result=[str(p.parent) for p in (ROOT/'PreparedSongs').rglob('aligned.mid.prepared.json')]
    else:
        import shutil
        from transcribe import CHECKPOINT,HASH
        result=dict(preprocessingPython=PREP_PYTHON.is_file(),modelPython=MODEL_PYTHON.is_file(),
                    ffmpeg=shutil.which('ffmpeg'),dotnet=shutil.which('dotnet'),
                    modelVerified=CHECKPOINT.is_file() and sha(CHECKPOINT)==HASH)
        if MODEL_PYTHON.exists():
            try:
                result['device']=run([MODEL_PYTHON,'-c','import torch; print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else "CPU")']).strip()
                result['separator']=run([MODEL_PYTHON,'-c','import importlib.metadata; print("Demucs " + importlib.metadata.version("demucs"))']).strip()
                result['separatorWeightsCached']=bool(list((DATA/'models/torch/hub/checkpoints').glob('955717e8-*.th')))
            except Exception as error:result['deviceError']=str(error)
    print(json.dumps(result,indent=2))

if __name__=='__main__':main()
