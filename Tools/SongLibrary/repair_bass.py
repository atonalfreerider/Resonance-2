"""Reviewed MIDI-guided recovery of bass misclassified as accompaniment.

Redistributes existing audio, never synthesizes notes or alters the full mix.
This remains an estimate: instruments sharing a partial cannot be disentangled
perfectly by a spectral mask. Original stems are backed up outside the bundle.
"""
import argparse
import shutil
import uuid
from pathlib import Path
import numpy as np
import soundfile as sf
from scipy.signal import stft, istft, resample_poly, butter, sosfiltfilt
from common import DATA, atomic_json, read_json, sha, validate_bundle
from story import seconds_at


def guided_recovery(audio, sr, notes, max_hz=700):
    rate=sr/4
    low=resample_poly(audio,1,4,axis=0).astype('float32')
    frequencies,times,spectrum=stft(low.mean(axis=1),fs=rate,nperseg=2048,noverlap=1920)
    pitches=sorted({n[2] for n in notes})
    gates={pitch:np.zeros(len(times),dtype='float32') for pitch in pitches}
    for start,end,pitch in notes:
        gate=np.minimum(np.clip((times-start+.035)/.035,0,1),np.clip((end+.09-times)/.09,0,1))
        gates[pitch]=np.maximum(gates[pitch],gate)
    # Estimate global tuning from the dominant recorded partials, not a neural
    # pitch rewrite. This recording may be pitch-shifted away from concert A.
    power=abs(spectrum)**2
    profiles={pitch:(power*gates[pitch]).sum(axis=1) for pitch in pitches}
    candidates=np.arange(-90,91,5);scores=[]
    for cents in candidates:
        score=0.
        for pitch in pitches:
            envelope=gates[pitch]
            if envelope.sum()==0:continue
            profile=profiles[pitch]
            f0=440*2**((pitch-69+cents/100)/12)
            for harmonic in range(1,7):
                center=f0*harmonic
                if center>max_hz:break
                band=np.exp(-.5*((frequencies-center)/max(3,center*.009))**2)
                score+=float(profile@band)/harmonic
        scores.append(score)
    cents=int(candidates[int(np.argmax(scores))])
    mask=np.zeros(spectrum.shape,dtype='float32')
    for pitch in pitches:
        f0=440*2**((pitch-69+cents/100)/12);profile=np.zeros(len(frequencies),dtype='float32')
        for harmonic in range(1,13):
            center=f0*harmonic
            if center>max_hz:break
            width=max(6,center*.022)
            profile=np.maximum(profile,np.exp(-.5*((frequencies-center)/width)**2))
        profile*=np.clip((max_hz-frequencies)/100,0,1)
        mask=np.maximum(mask,profile[:,None]*gates[pitch][None,:])
    recovered=[]
    for channel in range(audio.shape[1]):
        _,_,z=stft(low[:,channel],fs=rate,nperseg=2048,noverlap=1920)
        _,signal=istft(z*mask,fs=rate,nperseg=2048,noverlap=1920)
        up=resample_poly(signal,4,1)[:len(audio)]
        recovered.append(np.pad(up,(0,max(0,len(audio)-len(up)))))
    return np.stack(recovered,axis=1).astype('float32'),cents


def repair(bundle):
    bundle=Path(bundle).resolve();patterns=validate_bundle(bundle)
    settings=read_json(bundle/'song.json',{});tracks=settings.get('StemTracks',{}).get('bass',[])
    if not tracks:raise ValueError('Bass recovery requires reviewed bass track mapping')
    manifest=read_json(bundle/'aligned.mid.prepared.json');entries={s['id']:s for s in manifest.get('stems',[])}
    if not {'bass','other','other-low','other-high'} <= entries.keys():raise ValueError('Prepare separated stems first')
    previous=read_json(bundle/'stems/bass-recovery.json',{})
    if previous.get('midiSha256')==manifest['midiSha256'] and all(sha(bundle/entries[k]['audioPath'])==v for k,v in previous.get('outputHashes',{}).items()) and previous.get('outputHashes'):
        return dict(reused=True,**previous)
    if previous:raise ValueError('Restore original stems or separate again before revising an existing bass recovery')
    notes=[(seconds_at(patterns,n['Beat']),seconds_at(patterns,n['Beat']+n['Length']),n['Pitch']) for n in patterns['Notes'] if n['Track'] in tracks and n['Channel']!=10]
    if not notes:raise ValueError('Reviewed bass score has no notes')
    other,sr=sf.read(bundle/entries['other']['audioPath'],dtype='float32',always_2d=True)
    bass,bsr=sf.read(bundle/entries['bass']['audioPath'],dtype='float32',always_2d=True)
    if bsr!=sr or other.shape!=bass.shape:raise ValueError('Stem clocks differ')
    recovered,cents=guided_recovery(other,sr,notes)
    new_bass=bass+recovered;new_other=other-recovered
    low=sosfiltfilt(butter(4,261.625565,fs=sr,output='sos'),new_other,axis=0).astype('float32')
    outputs={'bass':new_bass,'other':new_other,'other-low':low,'other-high':new_other-low}
    if not all(np.isfinite(v).all() for v in outputs.values()):raise ValueError('Nonfinite recovered audio')
    residual=float(abs((bass+other)-(new_bass+new_other)).max())
    if residual>1e-5:raise ValueError('Recovery altered the instrument sum')
    job=DATA/'jobs'/('bass-repair-'+uuid.uuid4().hex);backup=job/'original';stage=job/'repaired'
    backup.mkdir(parents=True);stage.mkdir()
    shutil.copy2(bundle/'aligned.mid.prepared.json',backup/'aligned.mid.prepared.json')
    for key,value in outputs.items():
        shutil.copy2(bundle/entries[key]['audioPath'],backup/(key+'.wav'))
        sf.write(stage/(key+'.wav'),value,sr,subtype='FLOAT')
    report=dict(method='reviewed-midi-guided redistribution from accompaniment',midiSha256=manifest['midiSha256'],
        tuningCents=cents,maxHz=700,samples=len(bass),sampleRate=sr,bassTrackIds=tracks,
        originalBassRmsDb=float(20*np.log10(np.sqrt(np.mean(bass*bass))+1e-12)),
        recoveredBassRmsDb=float(20*np.log10(np.sqrt(np.mean(new_bass*new_bass))+1e-12)),
        instrumentSumMaxError=residual,backup=str(backup.relative_to(DATA)),
        limitation='Estimated isolation; harmonics shared with accompaniment may remain in bass.',
        outputHashes={k:sha(stage/(k+'.wav')) for k in outputs})
    try:
        for key in outputs:
            shutil.copy2(stage/(key+'.wav'),bundle/entries[key]['audioPath'])
            entries[key]['audioSha256']=report['outputHashes'][key]
            entries[key]['audioMethod']=report['method']
        atomic_json(bundle/'aligned.mid.prepared.json',manifest)
        validate_bundle(bundle)
        atomic_json(bundle/'stems/bass-recovery.json',report)
    except Exception:
        for key in outputs:shutil.copy2(backup/(key+'.wav'),bundle/entries[key]['audioPath'])
        shutil.copy2(backup/'aligned.mid.prepared.json',bundle/'aligned.mid.prepared.json')
        raise
    return report


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('bundle');args=parser.parse_args()
    import json
    print(json.dumps(repair(args.bundle),indent=2))
