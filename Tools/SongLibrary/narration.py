"""Offline, cached speech, with explicit music-only listening windows."""
import concurrent.futures
import hashlib
import io
import json
import math
import subprocess
import urllib.error
import urllib.request
from pathlib import Path
import numpy as np
import soundfile as sf
from common import DATA, atomic_json, read_json, sha, validate_bundle
from story import validate_cues

RATE=24000
DELIVERY='Deep masculine baritone, resonant lower register, warm and grounded music-documentary narration. Maintain the same low vocal placement throughout. No impersonation of any real person. Conversational and quietly engaged, never theatrical. Natural neutral English. Keep a flowing, fairly brisk pace with short pauses. Read only the supplied words. No singing, music, sound effects, introductions or added words.'


def fit_clip(signal, rate, available, folder):
    if signal.ndim>1:signal=signal.mean(axis=1)
    active=np.flatnonzero(abs(signal)>.001)
    if len(active)==0:raise ValueError('Speech response was silent')
    signal=signal[max(0,active[0]-int(.04*rate)):min(len(signal),active[-1]+int(.07*rate))]
    ratio=max(1,len(signal)/rate/available)
    if ratio>1.35:raise ValueError('Narration is too long for its scene; shorten that caption before generating again')
    folder=Path(folder);folder.mkdir(parents=True,exist_ok=True)
    sf.write(folder/'input.wav',signal,rate)
    subprocess.run(['ffmpeg','-v','error','-y','-i',str(folder/'input.wav'),'-af',f'atempo={ratio:.8f}',
                    '-ac','1','-ar',str(RATE),str(folder/'fit.wav')],check=True,capture_output=True)
    signal,rate=sf.read(folder/'fit.wav',dtype='float32')
    if len(signal)>round(available*rate)+rate*.05:
        raise ValueError('Fitted narration exceeds its scene')
    signal=signal[:round(available*rate)]
    peak=float(abs(signal).max());rms=float(np.sqrt(np.mean(signal*signal)))
    signal*=min(.85/max(peak,1e-6),.12/max(rms,1e-6))
    edge=min(int(.008*rate),len(signal)//2)
    signal[:edge]*=np.linspace(0,1,edge);signal[-edge:]*=np.linspace(1,0,edge)
    return signal,ratio


def speech_window(cue):
    if cue.get('narrate',True) is False:return None
    start=cue.get('narrationStart',cue['start']+.18)
    end=cue.get('narrationEnd',cue['end']-.22)
    if not all(isinstance(t,(int,float)) and math.isfinite(t) for t in (start,end)) or start<cue['start'] or end>cue['end'] or end-start<=.2:
        raise ValueError('Narration must fit inside its scene')
    return start,end


def generate(bundle,key_file,voice=None,model=None,notify=print,provider='openai'):
    if provider not in ('openai','cartesia'):raise ValueError('Unknown speech provider')
    voice=voice or ('f114a467-c40a-4db8-964d-aaba89cd08fa' if provider=='cartesia' else 'onyx')
    model=model or ('sonic-3.6' if provider=='cartesia' else 'gpt-4o-mini-tts')
    bundle=Path(bundle).resolve();validate_bundle(bundle)
    story=read_json(bundle/'story.json');manifest=read_json(bundle/'aligned.mid.prepared.json')
    if not story or story['midiSha256']!=manifest['midiSha256'] or story['audioSha256']!=manifest['audioSha256'] or story['patternsSha256']!=sha(bundle/'aligned.mid.patterns.json'):
        raise ValueError('Generate a current story first')
    cues=validate_cues(story['cues'],story['duration'],[s['id'] for s in manifest.get('stems',[])],[s['id'] for s in story.get('sources',[])])
    key=Path(key_file).read_text(encoding='utf-8-sig').strip()
    if not key or '\n' in key or '\r' in key:raise ValueError('Key file must contain one API key')
    cache=DATA/'speech-cache';cache.mkdir(parents=True,exist_ok=True)
    def render(item):
        i,cue=item;window=speech_window(cue)
        if window is None:return i,None,1
        if provider=='cartesia':
            body=dict(model_id=model,voice=voice,transcript=cue['text'],language='en-GB',output_format=dict(container='wav',encoding='pcm_s16le',sample_rate=RATE),generation_config=dict(speed=1,volume=1))
        else:body=dict(model=model,voice=voice,input=cue['text'],instructions=DELIVERY,response_format='wav',speed=1.0)
        identity=hashlib.sha256(json.dumps(dict(provider=provider,body=body),sort_keys=True).encode()).hexdigest();raw=cache/(identity+'.wav')
        if not raw.exists():
            headers={'Authorization':'Bearer '+key,'Content-Type':'application/json'}
            if provider=='cartesia':headers['Cartesia-Version']='2026-08-14'
            request=urllib.request.Request('https://api.cartesia.ai/tts/bytes' if provider=='cartesia' else 'https://api.openai.com/v1/audio/speech',data=json.dumps(body).encode(),headers=headers,method='POST')
            try:
                with urllib.request.urlopen(request,timeout=180) as response:content=response.read()
            except urllib.error.HTTPError as error:
                raise RuntimeError(f'Speech request failed (HTTP {error.code}); credentials and response body not logged') from None
            except urllib.error.URLError:
                raise RuntimeError('Speech connection failed; credentials not logged') from None
            signal,rate=sf.read(io.BytesIO(content),dtype='float32')
            sf.write(raw,signal,rate)
        signal,rate=sf.read(raw,dtype='float32')
        available=window[1]-window[0]
        if available<=.2:raise ValueError('Scene too short for narration')
        signal,ratio=fit_clip(signal,rate,available,cache/identity)
        notify(f'Narration scene {i+1}/{len(cues)} ready')
        return i,signal,ratio
    with concurrent.futures.ThreadPoolExecutor(max_workers=1 if provider=='cartesia' else 3) as pool:
        clips=list(pool.map(render,enumerate(cues)))
    key=None
    timeline=np.zeros(math.ceil(story['duration']*RATE),dtype='float32');segments=[]
    for i,signal,ratio in clips:
        if signal is None:continue
        start=round(speech_window(cues[i])[0]*RATE);end=start+len(signal)
        if end>len(timeline):raise ValueError('Narration exceeds recording')
        timeline[start:end]+=signal
        segments.append(dict(cue=i,start=start/RATE,end=end/RATE,tempoRatio=ratio))
    spoken=sum(s['end']-s['start'] for s in segments)
    fraction=spoken/story['duration']
    target=story.get('narrationTargetFraction',2/3)
    if not 0<target<=1:raise ValueError('Invalid narration target fraction')
    if fraction>target+.025:raise ValueError('Too much narration: shorten the script or reserve more music-only scenes')
    # Publish only after every scene succeeded. Name by content to keep a previous
    # manifest and track usable while a new version is being rendered.
    identity=sha(bundle/'story.json');content_id=hashlib.sha256(timeline.tobytes()).hexdigest();directory=bundle/'narration'/content_id[:12];directory.mkdir(parents=True,exist_ok=True)
    wav=directory/'narration.wav';mp3=directory/'narration.mp3'
    sf.write(wav,timeline,RATE,subtype='PCM_16')
    subprocess.run(['ffmpeg','-v','error','-y','-i',str(wav),'-codec:a','libmp3lame','-b:a','160k',str(mp3)],check=True,capture_output=True)
    metadata=dict(version=1,storySha256=identity,audioSha256=manifest['audioSha256'],duration=story['duration'],
        audioPath=wav.relative_to(bundle).as_posix(),sha256=sha(wav),mp3Path=mp3.relative_to(bundle).as_posix(),
        sampleRate=RATE,samples=len(timeline),model=model,voice=voice,provider=provider,disclosure='AI-generated narration',segments=segments,
        narratedSeconds=spoken,musicOnlySeconds=story['duration']-spoken,narrationFraction=fraction)
    atomic_json(bundle/'narration.json',metadata)
    return dict(wav=str(wav),mp3=str(mp3),scenes=len(segments),duration=len(timeline)/RATE,narratedSeconds=spoken,musicOnlySeconds=story['duration']-spoken)


if __name__=='__main__':
    import argparse
    p=argparse.ArgumentParser();p.add_argument('bundle');p.add_argument('--key-file',required=True);p.add_argument('--voice');p.add_argument('--provider',choices=['openai','cartesia'],default='openai');a=p.parse_args()
    print(json.dumps(generate(a.bundle,a.key_file,a.voice,provider=a.provider),indent=2))
