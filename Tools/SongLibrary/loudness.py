"""Make a soft recording louder: a fixed gain, with a look-ahead peak limiter only where the louder recording would
clip, applied identically to the recording and its stems (so a soloed stem matches the mix).

    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/loudness.py PreparedSongs/Library/<song> --db 6

A prepared bundle keeps the recording's level as it came; a quiet performance (Chopin's Op. 10 No. 3 from the
2009 Leeds competition sits near -23.5 LUFS, ten below the library's other piano recordings) then plays softly
under the narration. The gain is recorded in the bundle's song.json and provenance (RecordingGainDb), and the
bundle manifest's hashes are updated; republish the tour and re-render its narration afterwards so the ducking
is measured against the louder music. pipeline.ingest applies a RecordingGainDb from the source's song.json the
same way, so a re-ingest keeps it.
"""
import argparse
import json
import math
from pathlib import Path

import numpy as np
import soundfile as sf

from common import atomic_json, read_json, sha

CEILING_DB = -1.0          # peaks stay below this (dBFS)
ATTACK, RELEASE = .005, .120   # seconds: look-ahead of the limiter and its recovery time


def gain_curve(signal, rate, db, ceiling_db=CEILING_DB):
    """Per-sample gain for `signal` (samples x channels): 10^(db/20), reduced by a look-ahead limiter wherever the
    louder signal would pass the ceiling, recovering exponentially over RELEASE seconds."""
    target = 10**(db/20)
    ceiling = 10**(ceiling_db/20)
    block = max(1, int(rate/1000))                                   # ~1 ms blocks
    n = int(math.ceil(len(signal)/block))
    peaks = np.zeros(n)
    flat = np.abs(signal).max(axis=1) if signal.ndim > 1 else np.abs(signal)
    padded = np.zeros(n*block); padded[:len(flat)] = flat
    peaks = padded.reshape(n, block).max(axis=1)
    need = np.minimum(1., ceiling/np.maximum(peaks*target, 1e-12))   # reduction each block needs
    ahead = max(1, int(ATTACK*1000))
    # look-ahead: a block starts reducing as soon as a peak within the next ATTACK seconds needs it
    from scipy.ndimage import minimum_filter1d
    need = minimum_filter1d(need, size=2*ahead+1, origin=-ahead, mode='nearest')
    recover = 1-math.exp(-1/(RELEASE*1000))
    reduction = np.empty(n)
    r = 1.
    for i in range(n):
        r = need[i] if need[i] < r else r+(min(1., need[i])-r)*recover
        reduction[i] = r
    per_block = target*reduction
    centres = (np.arange(n)+.5)*block
    return np.interp(np.arange(len(signal)), centres, per_block).astype('float32'), float(1-reduction.min())


def apply(bundle, db):
    bundle = Path(bundle)
    manifest_path = bundle/'aligned.mid.prepared.json'
    manifest = read_json(manifest_path)
    recording = bundle/manifest.get('audioPath', 'recording.wav')
    mix, rate = sf.read(recording, dtype='float32', always_2d=True)
    info = sf.info(recording)
    curve, limited = gain_curve(mix, rate, db)
    sf.write(recording, mix*curve[:, None], rate, subtype=info.subtype)
    manifest['audioSha256'] = sha(recording)
    for stem in manifest.get('stems', []):
        if not stem.get('audioPath'):
            continue
        path = bundle/stem['audioPath']
        signal, srate = sf.read(path, dtype='float32', always_2d=True)
        sinfo = sf.info(path)
        g = curve if srate == rate and len(signal) == len(curve) else np.interp(np.arange(len(signal))*rate/srate, np.arange(len(curve)), curve).astype('float32')
        sf.write(path, signal*g[:len(signal), None], srate, subtype=sinfo.subtype)
        stem['audioSha256'] = sha(path)
    atomic_json(manifest_path, manifest)
    song = read_json(bundle/'song.json') or {}
    song['RecordingGainDb'] = round(song.get('RecordingGainDb', 0)+db, 2)
    atomic_json(bundle/'song.json', song)
    library = bundle/'library.json'
    if library.exists():
        record = read_json(library)
        record.setdefault('provenance', {})['recordingGainDb'] = song['RecordingGainDb']
        atomic_json(library, record)
    return dict(gainDb=db, peakReductionDb=round(-20*math.log10(max(1e-6, 1-limited)), 2), recording=recording.name)


def apply_to_wav(path, db):
    """The pipeline's form: a freshly decoded recording, before alignment and stems."""
    signal, rate = sf.read(path, dtype='float32', always_2d=True)
    info = sf.info(path)
    curve, _ = gain_curve(signal, rate, db)
    sf.write(path, signal*curve[:, None], rate, subtype=info.subtype)


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    p.add_argument('bundle')
    p.add_argument('--db', type=float, required=True)
    a = p.parse_args()
    print(json.dumps(apply(a.bundle, a.db), indent=1))
