"""Offline audio/score fingerprint alignment and tempo-retimed MIDI export."""
import argparse
import hashlib
import html
import json
import subprocess
from pathlib import Path

import mido
import numpy as np
import pandas as pd
import soundfile as sf
from scipy.signal import resample_poly
from synctoolbox.feature.pitch import audio_to_pitch_features
from synctoolbox.feature.pitch_onset import audio_to_pitch_onset_features
from synctoolbox.feature.utils import estimate_tuning
from synctoolbox.feature.csv_tools import df_to_pitch_features, df_to_pitch_onset_features
from synctoolbox.feature.chroma import pitch_to_chroma, quantize_chroma, quantized_chroma_to_CENS
from synctoolbox.feature.dlnco import pitch_onset_features_to_DLNCO
from synctoolbox.dtw.mrmsdtw import sync_via_mrmsdtw, sync_via_mrmsdtw_with_anchors
from synctoolbox.dtw.utils import make_path_strictly_monotonic, compute_optimal_chroma_shift


def digest(path):
    with open(path, 'rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read_score(path):
    midi = mido.MidiFile(path)
    if midi.type == 2 or midi.ticks_per_beat <= 0:
        raise ValueError('Only format 0/1 PPQ MIDI is supported.')
    events = []
    for track, messages in enumerate(midi.tracks):
        tick = 0
        for order, message in enumerate(messages):
            tick += message.time
            events.append((tick, track, order, message))
    events.sort(key=lambda e: e[:3])
    time, previous, tempo = 0., 0, 500000
    tempo_ticks, tempo_times = [0.], [0.]
    pending, notes = {}, []
    for tick, track, _, msg in events:
        time += mido.tick2second(tick-previous, midi.ticks_per_beat, tempo)
        previous = tick
        if msg.type == 'set_tempo':
            tempo = msg.tempo
            if tick == tempo_ticks[-1]:
                tempo_times[-1] = time
            else:
                tempo_ticks.append(float(tick)); tempo_times.append(time)
        if msg.type in ('note_on', 'note_off'):
            key = (track, msg.channel, msg.note)
            if msg.type == 'note_on' and msg.velocity:
                pending.setdefault(key, []).append((time, msg.velocity))
            elif pending.get(key):
                start, velocity = pending[key].pop(0)
                if msg.channel != 9:
                    notes.append(dict(start=start, duration=max(.001, time-start), pitch=msg.note,
                                      velocity=velocity, instrument=str(track)))
    if pending and any(pending.values()):
        raise ValueError('Unmatched MIDI note-on events; repair the score before alignment.')
    if not notes:
        raise ValueError('No pitched notes to align.')
    if previous > tempo_ticks[-1]:
        tempo_ticks.append(float(previous)); tempo_times.append(time)
    return midi, events, pd.DataFrame(notes), np.array(tempo_ticks), np.array(tempo_times)


def retime(midi, events, tempo_ticks, tempo_times, score_times, audio_times, output):
    """Replace the tempo map, preserving musical ticks and all other event payloads."""
    factor = max(1, int(np.ceil(1920/midi.ticks_per_beat)))
    ppq = midi.ticks_per_beat * factor
    ticks = np.rint(np.interp(score_times, tempo_times, tempo_ticks)*factor).astype(int)
    unique = np.r_[True, np.diff(ticks) > 0]
    ticks, targets = ticks[unique], audio_times[unique]
    if ticks[0] != 0 or targets[0] != 0:
        ticks = np.r_[0, ticks]; targets = np.r_[0., targets]
    result = mido.MidiFile(type=1, ticks_per_beat=ppq)
    conductor = mido.MidiTrack(); result.tracks.append(conductor)
    conductor.append(mido.MetaMessage('track_name', name='Recording fingerprint tempo map', time=0))
    conductor.append(mido.MetaMessage('text', text='Resonance.Prepared:v1; timing baked into MIDI', time=0))
    actual_time, last_tick = 0., 0
    for i in range(len(ticks)-1):
        dt = int(ticks[i+1]-ticks[i])
        tempo = int(round((targets[i+1]-actual_time)*ppq*1e6/dt))
        if not 1 <= tempo <= 0xffffff:
            raise ValueError('Alignment requires an invalid MIDI tempo; add corrective anchors.')
        conductor.append(mido.MetaMessage('set_tempo', tempo=tempo, time=int(ticks[i]-last_tick)))
        last_tick = int(ticks[i]); actual_time += dt*tempo/(ppq*1e6)
    conductor.append(mido.MetaMessage('end_of_track', time=int(ticks[-1]-last_tick)))
    for track in range(len(midi.tracks)):
        target = mido.MidiTrack(); result.tracks.append(target); previous = 0
        for tick, index, _, msg in events:
            if index != track or msg.type in ('set_tempo', 'end_of_track'):
                continue
            absolute = tick*factor
            target.append(msg.copy(time=absolute-previous)); previous = absolute
        target.append(mido.MetaMessage('end_of_track', time=max(0, int(ticks[-1])-previous)))
    result.save(output)
    # Reparse independently to catch ordering, export and accumulated tempo-rounding errors.
    _, exported_events, exported_notes, _, _ = read_score(output)
    source_payload = [msg.copy(time=0).bytes() for _, _, _, msg in events if msg.type not in ('set_tempo', 'end_of_track')]
    output_payload = [msg.copy(time=0).bytes() for _, track, _, msg in exported_events if track > 0 and msg.type not in ('set_tempo', 'end_of_track')]
    assert source_payload == output_payload, 'Export changed MIDI event payloads or ordering'
    return exported_notes


def normalize(features):
    return features/np.maximum(1e-9, np.linalg.norm(features, axis=0, keepdims=True))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--audio', required=True, type=Path)
    parser.add_argument('--midi', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--anchors', type=Path, help='Optional CSV: score_seconds,audio_seconds (matching musical cues)')
    parser.add_argument('--ffmpeg', default='ffmpeg')
    parser.add_argument('--feature-rate', type=int, default=100, choices=[50, 100])
    args = parser.parse_args()
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=True)
    audio_path, midi_path = args.audio.resolve(), args.midi.resolve()
    recording, aligned = output/'recording.wav', output/'aligned.mid'
    if audio_path == recording or midi_path == aligned:
        raise ValueError('Use original inputs, not this output directory, as preprocessing sources.')
    sidecar = output/'aligned.mid.prepared.json'
    # An interrupted/failed run must never advertise stale results as ready for Unity.
    sidecar.unlink(missing_ok=True)
    audio_hash, midi_hash = digest(audio_path), digest(midi_path)
    print('1/5 Decode recording to a canonical sample clock', flush=True)
    subprocess.run([args.ffmpeg, '-nostdin', '-v', 'error', '-y', '-i', str(audio_path), '-vn', '-c:a', 'pcm_s16le', str(recording)], check=True)
    data, sr = sf.read(recording, always_2d=True); duration = len(data)/sr
    import math
    divisor = math.gcd(sr, 22050)
    mono = resample_poly(data.mean(axis=1), 22050//divisor, sr//divisor)
    midi, events, notes, tempo_ticks, tempo_times = read_score(midi_path)
    rate = args.feature_rate; cache = output/'fingerprint-cache.npz'
    cache_key = f'{audio_hash}:{midi_hash}:{rate}:3'
    print('2/5 Extract tuning-aware pitch and attack fingerprints', flush=True)
    saved = np.load(cache) if cache.exists() else None
    if saved is not None and str(saved['key']) == cache_key:
        ca, oa, cm, om, tuning = [saved[k] for k in ['ca', 'oa', 'cm', 'om', 'tuning']]
    else:
        tuning = estimate_tuning(mono, 22050)
        ap = audio_to_pitch_features(mono, Fs=22050, tuning_offset=tuning, feature_rate=rate)
        # The filterbank uses integer sample hops (220 rather than 220.5 at
        # 100 Hz). Restore the exact sample clock before comparing with MIDI.
        actual_times = np.arange(ap.shape[1])*int(22050/rate)/22050
        exact_times = np.arange(int(np.ceil(duration*rate))+1)/rate
        ap = np.array([np.interp(exact_times, actual_times, row) for row in ap])
        ca = quantize_chroma(pitch_to_chroma(ap))
        peaks = audio_to_pitch_onset_features(mono, Fs=22050, tuning_offset=tuning)
        oa = pitch_onset_features_to_DLNCO(peaks, feature_rate=rate, feature_sequence_length=ca.shape[1])
        mp = df_to_pitch_features(notes, feature_rate=rate)
        cm = quantize_chroma(pitch_to_chroma(mp))
        om = pitch_onset_features_to_DLNCO(df_to_pitch_onset_features(notes), feature_rate=rate, feature_sequence_length=cm.shape[1])
        np.savez_compressed(cache, key=cache_key, ca=ca, oa=oa, cm=cm, om=om, tuning=tuning)
    coarse_a = quantized_chroma_to_CENS(ca, 2*rate+1, rate, rate)[0]
    coarse_m = quantized_chroma_to_CENS(cm, 2*rate+1, rate, rate)[0]
    shift = int(compute_optimal_chroma_shift(coarse_a, coarse_m))
    print(f'3/5 Multiscale alignment at {1000/rate:g} ms; pitch-class shift diagnostic: {shift}', flush=True)
    kwargs = dict(f_chroma1=ca, f_chroma2=cm, f_onset1=oa, f_onset2=om, input_feature_rate=rate,
                  step_weights=np.array([1.5, 1.5, 2.0]), threshold_rec=1000000, verbose=False)
    if args.anchors:
        cues = np.atleast_2d(np.loadtxt(args.anchors, delimiter=',', skiprows=1))
        if np.any(np.diff(cues, axis=0) <= 0):
            raise ValueError('Cue times must increase in both columns.')
        kwargs['anchor_pairs'] = [(float(a), float(m)) for m, a in cues]
        path = sync_via_mrmsdtw_with_anchors(**kwargs)
    else:
        path = sync_via_mrmsdtw(**kwargs)
    strict = make_path_strictly_monotonic(path)/rate
    score_times, audio_times = strict[1], strict[0]
    # Preserve final MIDI rests and map the full recording, not an estimated MP3 duration.
    keep = (score_times > 0) & (score_times < tempo_times[-1]) & (audio_times > 0) & (audio_times < duration)
    score_times = np.r_[0., score_times[keep], tempo_times[-1]]
    audio_times = np.r_[0., audio_times[keep], duration]
    print('4/5 Bake recording timing into MIDI tempo events; verify all note/controller payloads', flush=True)
    exported = retime(midi, events, tempo_ticks, tempo_times, score_times, audio_times, aligned)
    expected = np.interp(notes.start, score_times, audio_times)
    errors = np.abs(exported.start.to_numpy()-expected)
    if errors.max() > .002:
        raise ValueError(f'MIDI export drift exceeds 2 ms: {errors.max()}')
    np.savetxt(output/'timing-map.csv', np.c_[score_times, audio_times], delimiter=',', header='score_seconds,audio_seconds', comments='', fmt='%.6f')
    # Compare matched pitch fingerprints and inspect local slopes; these are diagnostics,
    # not independent ground-truth timing measurements or a promise of exact transcription.
    aindex = path[0].astype(int); mindex = path[1].astype(int)
    matches = np.sum(normalize(ca)[:, aindex]*normalize(cm)[:, mindex], axis=0)
    linear_idx = np.clip(np.rint(np.arange(cm.shape[1])*duration/tempo_times[-1]).astype(int), 0, ca.shape[1]-1)
    before = np.sum(normalize(ca)[:, linear_idx]*normalize(cm), axis=0)
    windows = []
    for start in np.arange(0, duration, 5):
        mask = (aindex/rate >= start) & (aindex/rate < start+5)
        similarity = float(matches[mask].mean()) if mask.any() else 0
        if similarity < .7:
            windows.append(dict(start=float(start), end=float(min(duration,start+5)), pitchSimilarity=similarity))
    metrics = dict(sourceDuration=float(tempo_times[-1]), audioDuration=duration, noteCount=len(notes),
                   featureResolutionMs=1000/rate, tuningCents=float(tuning), suggestedPitchClassShift=shift,
                   linearPitchSimilarity=float(before.mean()), alignedPitchSimilarity=float(matches.mean()),
                   maxMidiExportErrorMs=float(errors.max()*1000), weakWindows=windows, verifiedPerfect=False)
    (output/'analysis.json').write_text(json.dumps(metrics, indent=2), encoding='utf-8')
    print('5/5 Export fingerprint plots, listening preview and Unity manifest', flush=True)
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    fig, axes = plt.subplots(3, 1, figsize=(14, 10), constrained_layout=True)
    axes[0].imshow(ca, origin='lower', aspect='auto', extent=(0,duration,0,12)); axes[0].set_title('Recording pitch fingerprint (tuning corrected)')
    warped = np.array([np.interp(np.arange(ca.shape[1])/rate, audio_times, score_times)]).ravel()
    mapped = np.clip(np.rint(warped*rate).astype(int),0,cm.shape[1]-1)
    axes[1].imshow(cm[:,mapped], origin='lower', aspect='auto', extent=(0,duration,0,12)); axes[1].set_title('Retimed MIDI pitch fingerprint')
    axes[2].plot(score_times, audio_times-score_times); axes[2].set(xlabel='Original MIDI seconds', ylabel='Timing correction (seconds)', title='Timing baked into the exported MIDI')
    fig.savefig(output/'fingerprints.png', dpi=130); plt.close(fig)
    preview = np.zeros(len(mono), dtype=np.float32)
    for row in exported.itertuples():
        start = int(row.start*22050); length = min(int(min(row.duration,.22)*22050),len(preview)-start)
        if length <= 0: continue
        t = np.arange(length)/22050
        preview[start:start+length] += .05*(row.velocity/127)*np.sin(2*np.pi*440*2**((row.pitch-69)/12)*t)*np.exp(-t*22)
    sf.write(output/'alignment-audition.wav', np.c_[mono, np.tanh(preview)], 22050, subtype='PCM_16')
    weak_rows = ''.join(f'<li>{w["start"]:.0f}–{w["end"]:.0f}s: {w["pitchSimilarity"]:.0%}</li>' for w in windows)
    (output/'report.html').write_text(f'''<!doctype html><meta charset="utf-8"><title>Song fingerprint alignment</title>
<style>body{{max-width:1100px;margin:40px auto;background:#101522;color:#e7ebf4;font:16px system-ui}}img{{width:100%}}pre{{white-space:pre-wrap}}a{{color:#8bd}}</style>
<h1>{html.escape(audio_path.stem)}</h1><p>Offline pitch + attack fingerprint alignment, {1000/rate:g} ms feature grid. Timing is baked into aligned.mid; Unity must use an identity time map.</p>
<p>Pitch similarity: linear {before.mean():.1%} → aligned {matches.mean():.1%}. MIDI export timing error ≤ {errors.max()*1000:.3f} ms. This export error measures encoding fidelity, not musical alignment accuracy.</p>
<p><b>Not certified perfect.</b> Missing/extra notes, performance differences and repeated harmony remain ambiguous. Suggested pitch-class shift: {shift}; pitches were preserved. Inspect the weak windows and use matching cues with --anchors to rerun offline.</p>
<img src="fingerprints.png"><h2>Listening check</h2><p>Left: recording. Right: retimed note attacks. This preview is for checking alignment only; Unity plays the recording alone.</p><audio controls src="alignment-audition.wav"></audio>
<h2>Weak fingerprint windows</h2><ul>{weak_rows or '<li>No windows below the diagnostic threshold.</li>'}</ul>
<p><a href="timing-map.csv">Timing map</a> · <a href="analysis.json">Metrics</a></p>''', encoding='utf-8')
    manifest = dict(version=1, audioPath='recording.wav', midiPath='aligned.mid', reportPath='report.html',
                    sourceAudioPath=str(audio_path), sourceAudioSha256=audio_hash, sourceMidiSha256=midi_hash,
                    audioSha256=digest(recording), midiSha256=digest(aligned), featureResolutionMs=1000/rate,
                    status='fingerprint-aligned; review weak windows', audioDuration=duration)
    (output/'aligned.mid.prepared.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(json.dumps(metrics, indent=2), flush=True)


if __name__ == '__main__':
    main()
