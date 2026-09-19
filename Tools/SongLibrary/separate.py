"""Local Demucs worker. Sample-aligned floating-point stems; no per-stem normalization."""
import argparse
import os
from pathlib import Path
from common import DATA, atomic_json, sha

MODEL = 'htdemucs'

def load_model(device):
    os.environ['TORCH_HOME'] = str(DATA/'models'/'torch')
    # Demucs' official, hash-checked checkpoint serializes its model class.
    os.environ['TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD'] = '1'
    import torch
    from demucs.pretrained import get_model
    model = get_model(MODEL)
    return model.to(device).eval()

def separate(audio, output, device='auto'):
    import numpy as np
    import soundfile as sf
    import torch
    from scipy.signal import butter, sosfiltfilt
    from demucs.apply import apply_model
    from demucs.audio import convert_audio
    torch.set_num_threads(min(8, os.cpu_count() or 1))
    device = ('cuda' if torch.cuda.is_available() else 'cpu') if device == 'auto' else device
    model = load_model(device)
    data, sr = sf.read(audio, dtype='float32', always_2d=True)
    wave = convert_audio(torch.from_numpy(data.T.copy()), sr, model.samplerate, model.audio_channels)
    reference = wave.mean(0); mean, std = reference.mean(), reference.std().clamp_min(1e-6)
    with torch.inference_mode():
        result = apply_model(model, ((wave-mean)/std)[None], device=device,
                             shifts=1, split=True, overlap=.25, progress=True)[0].cpu()*std+mean
    stems = {}
    for name, signal in zip(model.sources, result):
        value = convert_audio(signal, model.samplerate, sr, data.shape[1]).T.numpy()
        stems[name] = np.pad(value, ((0,max(0,len(data)-len(value))),(0,0)))[:len(data)]
    # A complementary register view, explicitly distinct from neural instrument separation.
    low = sosfiltfilt(butter(4, 261.625565, fs=sr, output='sos'), stems['other'], axis=0).astype('float32')
    stems['other-low'] = low
    stems['other-high'] = stems['other']-low
    stems['instruments'] = stems['drums']+stems['bass']+stems['other']
    output = Path(output); output.mkdir(parents=True, exist_ok=True)
    for name, signal in stems.items():
        if not np.isfinite(signal).all(): raise ValueError('Non-finite separated samples: '+name)
        sf.write(output/(name+'.wav'), signal, sr, subtype='FLOAT')
    weights = {p.name:sha(p) for p in (DATA/'models'/'torch'/'hub'/'checkpoints').glob('*.th')}
    atomic_json(output/'separation.json', dict(model=MODEL,implementation='demucs==4.0.1',
        modelHashes=weights,sourceSha256=sha(audio),sampleRate=sr,samples=len(data),
        device=device,registerCrossoverHz=261.625565,
        registerMethod='Complementary zero-phase lowpass / residual on accompaniment; not instrument classification'))

if __name__ == '__main__':
    p=argparse.ArgumentParser();p.add_argument('--audio');p.add_argument('--output');p.add_argument('--install',action='store_true');p.add_argument('--device',default='auto');a=p.parse_args()
    if a.install: load_model('cpu');print('Demucs weights installed')
    else: separate(a.audio,a.output,a.device)
