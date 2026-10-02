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
# Music under a spoken line is ducked until the voice stands this far above it.
SPEECH_OVER_MUSIC_DB=10
STEM_CROSSFADE_SECONDS=.45
DUCK_RANGE=(.14,.8)
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


def band_rms(signal,rate,start,end):
    """Loudness of the speech band (300 Hz – 4 kHz) where the music masks a voice: the 90th
    percentile of 400 ms frames, so a loud moment inside the line counts."""
    from scipy.signal import butter,sosfiltfilt
    a,b=max(0,int(start*rate)),min(len(signal),int(end*rate))
    if b-a<rate*.1:return 0.
    segment=sosfiltfilt(butter(4,[300,4000],btype='band',fs=rate,output='sos'),signal[a:b])
    frame=int(.4*rate);frames=[segment[i:i+frame] for i in range(0,max(1,len(segment)-frame+1),frame//2)]
    return float(np.percentile([np.sqrt(np.mean(f*f)) for f in frames if len(f)],90))


def duck_gain(speech,music):
    """The music gain that puts the voice SPEECH_OVER_MUSIC_DB above the music (Unity plays the
    voice at 0.95 of the music's master volume)."""
    if music<=1e-6:return DUCK_RANGE[1]
    return float(np.clip(.95*speech/(music*10**(SPEECH_OVER_MUSIC_DB/20)),*DUCK_RANGE))


def duck_envelope(segments,duration,rate=100):
    """The music gain SongNarration applies, sampled at `rate` Hz: each line's duck from 0.12 s
    before it to 0.15 s after, approached at 16/s and released at 4/s."""
    target=np.ones(math.ceil(duration*rate));gain=np.ones_like(target)
    for s in segments:target[max(0,int((s['start']-.12)*rate)):int((s['end']+.15)*rate)]=np.minimum(target[max(0,int((s['start']-.12)*rate)):int((s['end']+.15)*rate)],s['duck'])
    g=1.
    for i,t in enumerate(target):g+=(t-g)*(1-math.exp(-(16 if t<1 else 4)/rate));gain[i]=g
    return gain


def preview_mix(bundle,manifest,cues,segments,timeline,path):
    """What Director mode plays: the recording (or the cue's soloed stem) under the duck envelope,
    plus the voice at 0.95 — for checking the balance by ear outside Unity."""
    music,rate=mono(bundle/manifest.get('audioPath','recording.wav'))
    stems={s['id']:s for s in manifest.get('stems',[])}
    for i,c in enumerate(cues):
        if c['stem'] and c['stem'] in stems:
            solo,_=mono(bundle/stems[c['stem']]['audioPath'])
            a=int(c['start']*rate);end=c['end']
            if c.get('soloUntil',0)>0:end=min(end,c['soloUntil'])
            if c.get('releaseSoloAfterNarration'):end=min(end,max([s['end'] for s in segments if s['cue']==i],default=end))
            b=min(len(music),int(end*rate));isolated=solo[a:b]*c.get('soloGain',1)
            # Match Unity's editorial stem transition instead of making a hard cut in the preview.
            count=b-a;fade=min(count//2,max(1,int(STEM_CROSSFADE_SECONDS*rate)))
            blend=np.ones(count,dtype='float32')
            previous=cues[i-1] if i>0 else None;following=cues[i+1] if i+1<len(cues) else None
            continues_from_previous=previous is not None and previous['stem']==c['stem'] and not previous.get('releaseSoloAfterNarration') and not previous.get('soloUntil',0) and abs(previous['end']-c['start'])<1e-6
            continues_to_following=following is not None and following['stem']==c['stem'] and not c.get('releaseSoloAfterNarration') and not c.get('soloUntil',0) and abs(c['end']-following['start'])<1e-6
            if not continues_from_previous:blend[:fade]=np.linspace(0,1,fade,endpoint=True,dtype='float32')
            music[a:b]=music[a:b]*(1-blend)+isolated*blend
            # Unity begins its return to the full mix at the editorial boundary. Carry the
            # outgoing stem forward through the fade instead of finishing the fade early.
            if not continues_to_following and b<len(music):
                tail=min(fade,len(music)-b);out=np.linspace(1,0,tail,endpoint=True,dtype='float32')
                music[b:b+tail]=music[b:b+tail]*(1-out)+solo[b:b+tail]*c.get('soloGain',1)*out
    t=np.arange(len(music))/rate;env=duck_envelope(segments,len(music)/rate)
    music=music*np.interp(t,np.arange(len(env))/100,env)
    voice=np.interp(t,np.arange(len(timeline))/RATE,timeline)*.95
    mix=music+voice;mix/=max(1,float(abs(mix).max())/.98)
    wav=Path(path).with_suffix('.wav');sf.write(wav,mix.astype('float32'),rate)
    subprocess.run(['ffmpeg','-v','error','-y','-i',str(wav),'-codec:a','libmp3lame','-b:a','128k',str(path)],check=True,capture_output=True)
    wav.unlink()


def mono(path):
    signal,rate=sf.read(path,dtype='float32')
    return (signal.mean(axis=1) if signal.ndim>1 else signal),rate


PROVIDERS=('openai','cartesia','elevenlabs')
MODELS=dict(openai='gpt-4o-mini-tts',cartesia='sonic-3.6',elevenlabs='eleven_v4')


def generate(bundle,key_file,voice=None,model=None,notify=print,provider='openai',override_voice=None):
    bundle=Path(bundle).resolve();validate_bundle(bundle)
    story=read_json(bundle/'story.json');manifest=read_json(bundle/'aligned.mid.prepared.json')
    if not story or story['midiSha256']!=manifest['midiSha256'] or story['audioSha256']!=manifest['audioSha256'] or story['patternsSha256']!=sha(bundle/'aligned.mid.patterns.json'):
        raise ValueError('Generate a current story first')
    cues=validate_cues(story['cues'],story['duration'],[s['id'] for s in manifest.get('stems',[])],[s['id'] for s in story.get('sources',[])],bundle)
    # The story names its narrator; a voice passed on the command line overrides it.
    narrator=story.get('narrator') or {}
    # A narrator from another provider brings its own key (settings: <provider>KeyFile).
    if narrator.get('provider') and narrator['provider']!=provider:
        provider=narrator['provider'];key_file=(read_json(DATA/'settings.json') or {}).get(provider+'KeyFile')
        if not key_file:raise ValueError(f'Configure {provider}KeyFile in SongLibraryData/settings.json')
    if provider not in PROVIDERS:raise ValueError('Unknown speech provider')
    model=model or MODELS[provider]
    voice=override_voice or narrator.get('voice') or voice or ('f114a467-c40a-4db8-964d-aaba89cd08fa' if provider=='cartesia' else 'onyx')
    language=narrator.get('language','en-GB')
    key=Path(key_file).read_text(encoding='utf-8-sig').strip()
    if not key or '\n' in key or '\r' in key:raise ValueError('Key file must contain one API key')
    cache=DATA/'speech-cache';cache.mkdir(parents=True,exist_ok=True)
    def render(item):
        i,cue=item;window=speech_window(cue)
        if window is None:return i,None,1
        if provider=='elevenlabs':
            settings=dict(stability=.5,similarity_boost=.75)
            if model not in ('eleven_v4','eleven_v4_turbo'):
                settings.update(style=.15,use_speaker_boost=True)
            body=dict(text=cue.get('speech') or cue['text'],model_id=model,language_code=language[:2],voice_settings=settings)
        elif provider=='cartesia':
            body=dict(model_id=model,voice=voice,transcript=cue.get('speech') or cue['text'],language=language,output_format=dict(container='wav',encoding='pcm_s16le',sample_rate=RATE),generation_config=dict(speed=1,volume=1))
        else:body=dict(model=model,voice=voice,input=cue.get('speech') or cue['text'],instructions=DELIVERY,response_format='wav',speed=1.0)
        identity=hashlib.sha256(json.dumps(dict(provider=provider,body=body,**({'voice':voice} if provider=='elevenlabs' else {})),sort_keys=True).encode()).hexdigest();raw=cache/(identity+'.wav')
        if not raw.exists():
            if provider=='elevenlabs':
                headers={'xi-api-key':key,'Content-Type':'application/json'}
                url=f'https://api.elevenlabs.io/v1/text-to-speech/{voice}?output_format=pcm_{RATE}'
            else:
                headers={'Authorization':'Bearer '+key,'Content-Type':'application/json'}
                if provider=='cartesia':headers['Cartesia-Version']='2026-08-14'
                url='https://api.cartesia.ai/tts/bytes' if provider=='cartesia' else 'https://api.openai.com/v1/audio/speech'
            request=urllib.request.Request(url,data=json.dumps(body).encode(),headers=headers,method='POST')
            try:
                with urllib.request.urlopen(request,timeout=180) as response:content=response.read()
            except urllib.error.HTTPError as error:
                raise RuntimeError(f'Speech request failed (HTTP {error.code}); credentials and response body not logged') from None
            except urllib.error.URLError:
                raise RuntimeError('Speech connection failed; credentials not logged') from None
            if provider=='elevenlabs':signal,rate=np.frombuffer(content,dtype='<i2').astype('float32')/32768,RATE
            else:signal,rate=sf.read(io.BytesIO(content),dtype='float32')
            sf.write(raw,signal,rate)
        signal,rate=sf.read(raw,dtype='float32')
        available=window[1]-window[0]
        if available<=.2:raise ValueError('Scene too short for narration')
        signal,ratio=fit_clip(signal,rate,available,cache/identity)
        notify(f'Narration scene {i+1}/{len(cues)} ready')
        return i,signal,ratio
    with concurrent.futures.ThreadPoolExecutor(max_workers=1 if provider=='cartesia' else 2 if provider=='elevenlabs' else 3) as pool:
        clips=list(pool.map(render,enumerate(cues)))
    key=None
    timeline=np.zeros(math.ceil(story['duration']*RATE),dtype='float32');segments=[]
    # Measure what actually plays under each line: the recording, or the soloed stem.
    music={'':mono(bundle/manifest.get('audioPath','recording.wav'))}
    for stem in manifest.get('stems',[]):
        if any(c['stem']==stem['id'] for c in cues):music[stem['id']]=mono(bundle/stem['audioPath']) if 'audioPath' in stem else mono(bundle/'stems'/(stem['id']+'.wav'))
    for i,signal,ratio in clips:
        if signal is None:continue
        start=round(speech_window(cues[i])[0]*RATE);end=start+len(signal)
        if end>len(timeline):raise ValueError('Narration exceeds recording')
        timeline[start:end]+=signal
        solo='' if cues[i].get('releaseSoloAfterNarration') or cues[i].get('soloUntil',0)>0 else cues[i]['stem'];under=music.get(solo,music[''])
        speech=band_rms(signal,RATE,0,len(signal)/RATE);level=band_rms(under[0],under[1],start/RATE,end/RATE)*(cues[i].get('soloGain',1) if solo else 1)
        segments.append(dict(cue=i,start=start/RATE,end=end/RATE,tempoRatio=ratio,duck=round(duck_gain(speech,level),4)))
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
        sampleRate=RATE,samples=len(timeline),model=model,voice=voice,voiceName=narrator.get('name',''),language=language,provider=provider,disclosure='AI-generated narration',segments=segments,speechOverMusicDb=SPEECH_OVER_MUSIC_DB,
        narratedSeconds=spoken,musicOnlySeconds=story['duration']-spoken,narrationFraction=fraction)
    preview_mix(bundle,manifest,cues,segments,timeline,directory/'preview.mp3')
    metadata['previewPath']=(directory/'preview.mp3').relative_to(bundle).as_posix()
    atomic_json(bundle/'narration.json',metadata)
    return dict(wav=str(wav),mp3=str(mp3),scenes=len(segments),duration=len(timeline)/RATE,narratedSeconds=spoken,musicOnlySeconds=story['duration']-spoken)


if __name__=='__main__':
    import argparse
    p=argparse.ArgumentParser();p.add_argument('bundle');p.add_argument('--key-file');p.add_argument('--voice',help="overrides the story's narrator");p.add_argument('--provider',choices=list(PROVIDERS))
    a=p.parse_args();settings=read_json(DATA/'settings.json') or {}
    provider=a.provider or settings.get('narrationProvider','openai')
    key_file=a.key_file or settings.get('cartesiaKeyFile' if provider=='cartesia' else 'storyKeyFile')
    if not key_file:raise SystemExit('No key file: pass --key-file or configure SongLibraryData/settings.json')
    print(json.dumps(generate(a.bundle,key_file,settings.get('narrationVoice'),provider=provider,override_voice=a.voice),indent=2))
