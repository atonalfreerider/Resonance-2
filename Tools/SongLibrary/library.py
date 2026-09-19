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
    p=sub.add_parser('search');p.add_argument('title')
    p=sub.add_parser('export');p.add_argument('bundle');p.add_argument('zip')
    p=sub.add_parser('review');p.add_argument('bundle');p.add_argument('settings',help='JSON with Key, Minor, LeadVocalTrack, SectionBoundaries, SectionParents')
    p=sub.add_parser('serve');p.add_argument('--port',type=int,default=8765);p.add_argument('--no-browser',action='store_true')
    sub.add_parser('doctor');sub.add_parser('list')
    a=parser.parse_args()
    if a.command=='ingest':
        from pipeline import ingest
        result=ingest(a.audio,a.midi,a.title,not a.offline,a.neural,a.meter,lambda s:print(s,flush=True))
    elif a.command=='index':result={'indexed':Catalog().index(a.directories)}
    elif a.command=='search':result=Catalog().search(a.title)
    elif a.command=='export':
        from pipeline import export
        result=export(a.bundle,a.zip)
    elif a.command=='review':
        from pipeline import review
        result=review(a.bundle,read_json(a.settings))
    elif a.command=='serve':
        from server import serve
        serve(a.port,not a.no_browser);return
    elif a.command=='list':
        result=[str(p.parent) for p in (ROOT/'PreparedSongs').rglob('aligned.mid.prepared.json')]
    else:
        import shutil
        from transcribe import CHECKPOINT,HASH
        result=dict(preprocessingPython=PREP_PYTHON.is_file(),modelPython=MODEL_PYTHON.is_file(),
                    ffmpeg=shutil.which('ffmpeg'),dotnet=shutil.which('dotnet'),
                    modelVerified=CHECKPOINT.is_file() and sha(CHECKPOINT)==HASH)
        if MODEL_PYTHON.exists():
            try:result['device']=run([MODEL_PYTHON,'-c','import torch; print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else "CPU")']).strip()
            except Exception as error:result['deviceError']=str(error)
    print(json.dumps(result,indent=2))

if __name__=='__main__':main()
