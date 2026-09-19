"""Audio evidence and a beat grid which changes notation, never note timing."""
import json
from pathlib import Path
import numpy as np
import librosa
import mido
import pretty_midi
from common import run


def decode(source, output):
    run(['ffmpeg', '-v', 'error', '-y', '-i', source, '-vn', '-ac', '2', '-ar', '44100', output])


def evidence(audio):
    y, sr = librosa.load(audio, sr=22050, mono=True)
    if len(y) < sr*2 or not np.isfinite(y).all() or np.max(np.abs(y)) < 1e-5:
        raise ValueError('Recording is silent, invalid, or shorter than two seconds')
    harmonic = librosa.effects.harmonic(y)
    chroma = librosa.feature.chroma_stft(y=harmonic, sr=sr, hop_length=2048)
    major = np.array([6.35,2.23,3.48,2.33,4.38,4.09,2.52,5.19,2.39,3.66,2.29,2.88])
    minor = np.array([6.33,2.68,3.52,5.38,2.60,3.53,2.54,4.75,3.98,2.69,3.34,3.17])
    mean = chroma.mean(axis=1)
    ranked = sorted([(float(np.corrcoef(mean,np.roll(p,k))[0,1]),k,m)
                     for m,p in enumerate([major,minor]) for k in range(12)], reverse=True)
    _, frames = librosa.beat.beat_track(y=y, sr=sr, hop_length=512)
    beats = librosa.frames_to_time(frames, sr=sr, hop_length=512)
    if len(beats) < 4:
        raise ValueError('Too few confident beats; provide a companion MIDI with a reviewed tempo map')
    gap = float(np.median(np.diff(beats)))
    while beats[0] > gap*.5: beats = np.r_[max(0,beats[0]-gap),beats]
    if beats[0] > 0: beats = np.r_[0,beats]
    duration = len(y)/sr
    while beats[-1] < duration: beats = np.r_[beats,beats[-1]+gap]
    return dict(duration=duration, key=(ranked[0][1]-9)%12, minor=bool(ranked[0][2]),
                keyMargin=ranked[0][0]-ranked[1][0], beats=beats.tolist()), chroma


def match_midi(path, audio_chroma, duration):
    score = pretty_midi.PrettyMIDI(str(path))
    ratio = score.get_end_time()/duration
    if not .55 < ratio < 1.65: return dict(score=0, durationRatio=ratio)
    c = score.get_chroma(fs=22050/2048)
    # Bound DTW memory and compare the same relative time resolution.
    n = min(900, audio_chroma.shape[1], c.shape[1])
    a = audio_chroma[:,np.linspace(0,audio_chroma.shape[1]-1,n).astype(int)]
    b = c[:,np.linspace(0,c.shape[1]-1,n).astype(int)]
    a /= np.maximum(np.linalg.norm(a,axis=0),1e-8)
    b /= np.maximum(np.linalg.norm(b,axis=0),1e-8)
    _, wp = librosa.sequence.dtw(C=1-a.T@b, global_constraints=True, band_rad=.15)
    similarity = float(np.mean(np.sum(a[:,wp[:,0]]*b[:,wp[:,1]],axis=0)))
    baseline = float(np.mean(a.T@b))
    return dict(score=similarity, contrast=similarity-baseline, durationRatio=ratio,
                accepted=similarity>=.66 and similarity-baseline>=.10)


def regrid(source, destination, beats, meter=4):
    """Preserve absolute seconds while assigning audio-estimated beats to PPQ ticks."""
    score = mido.MidiFile(source); ppq=15360
    tempos=[(0,500000)]; absolute=0
    for msg in mido.merge_tracks(score.tracks):
        absolute += msg.time
        if msg.type=='set_tempo': tempos.append((absolute,msg.tempo))
    tempos.sort(key=lambda x:x[0])
    def seconds(tick):
        t=0; last=0; tempo=500000
        for pos,new in tempos:
            if pos>tick: break
            t += mido.tick2second(pos-last,score.ticks_per_beat,tempo); last=pos; tempo=new
        return t+mido.tick2second(tick-last,score.ticks_per_beat,tempo)
    beats=np.array(beats,dtype=float)
    def ticks(t): return int(round(np.interp(t,beats,np.arange(len(beats)))*ppq))
    out=mido.MidiFile(ticks_per_beat=ppq); clock=mido.MidiTrack(); out.tracks.append(clock)
    clock.append(mido.MetaMessage('time_signature',numerator=meter,denominator=4))
    last=0
    for i,delta in enumerate(np.diff(beats)):
        if not .06 <= delta <= 16: raise ValueError('Invalid estimated beat spacing')
        pos=i*ppq; clock.append(mido.MetaMessage('set_tempo',tempo=round(delta*1e6),time=pos-last));last=pos
    clock.append(mido.MetaMessage('end_of_track',time=ppq))
    for track in score.tracks:
        dest=mido.MidiTrack(); out.tracks.append(dest); source_tick=last=0
        for msg in track:
            source_tick+=msg.time
            if msg.type in ('set_tempo','time_signature','end_of_track'): continue
            pos=ticks(seconds(source_tick));dest.append(msg.copy(time=max(0,pos-last)));last=pos
        dest.append(mido.MetaMessage('end_of_track'))
    out.save(destination)
