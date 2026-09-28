"""Narrate the tutorial (Assets/StreamingAssets/Tutorial/script.json) with Cartesia, in a male
British narration voice, one WAV per step beside the script, plus a manifest with durations.

  Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/tutorial_narration.py [--voice ID]

The key comes from cartesiaKeyFile in SongLibraryData/settings.json. Responses are cached by
their request, so re-running only speaks changed steps."""
import argparse
import hashlib
import io
import json
import re
import urllib.error
import urllib.request
from pathlib import Path
import numpy as np
import soundfile as sf

ROOT = Path(__file__).resolve().parents[2]
FOLDER = ROOT / 'Assets' / 'StreamingAssets' / 'Tutorial'
CACHE = ROOT / 'SongLibraryData' / 'speech-cache' / 'tutorial'
RATE = 24000
VERSION = '2026-08-14'


def key_text():
    settings = json.loads((ROOT / 'SongLibraryData' / 'settings.json').read_text(encoding='utf-8'))
    key = Path(settings['cartesiaKeyFile']).read_text(encoding='utf-8-sig').strip()
    if not key or '\n' in key:
        raise ValueError('Key file must contain one API key')
    return key


def request(path, key, body=None):
    headers = {'Authorization': 'Bearer ' + key, 'Cartesia-Version': VERSION}
    data = None
    if body is not None:
        headers['Content-Type'] = 'application/json'; data = json.dumps(body).encode()
    req = urllib.request.Request('https://api.cartesia.ai' + path, data=data, headers=headers, method='POST' if body else 'GET')
    try:
        with urllib.request.urlopen(req, timeout=180) as response:
            return response.read()
    except urllib.error.HTTPError as error:
        raise RuntimeError(f'Cartesia request failed (HTTP {error.code}) for {path}; credentials and body not logged') from None
    except urllib.error.URLError:
        raise RuntimeError('Cartesia connection failed; credentials not logged') from None


def pick_voice(key):
    """A male British narration voice: the library voice whose description says so."""
    voices = json.loads(request('/voices?limit=100&is_starred=false', key))
    items = voices.get('data', voices if isinstance(voices, list) else [])
    def score(v):
        text = ' '.join(str(v.get(k, '')) for k in ('name', 'description', 'gender', 'language')).lower()
        s = 0
        if 'british' in text or 'en-gb' in text or ' uk ' in text or 'english (uk' in text: s += 4
        if 'male' in text and 'female' not in text: s += 3
        if v.get('gender', '').lower() == 'masculine' or v.get('gender', '').lower() == 'male': s += 3
        if 'narrat' in text or 'documentary' in text or 'storytell' in text: s += 2
        if 'calm' in text or 'warm' in text: s += 1
        if v.get('language', 'en').startswith('en'): s += 1
        return s
    ranked = sorted(items, key=score, reverse=True)
    if not ranked:
        raise RuntimeError('No voices returned')
    best = ranked[0]
    return best['id'], best.get('name', ''), best.get('description', '')


def speak(key, voice, text):
    body = dict(model_id='sonic-3.6', voice=voice, transcript=text, language='en-GB',
                output_format=dict(container='wav', encoding='pcm_s16le', sample_rate=RATE),
                generation_config=dict(speed=1, volume=1))
    identity = hashlib.sha256(json.dumps(body, sort_keys=True).encode()).hexdigest()
    CACHE.mkdir(parents=True, exist_ok=True); raw = CACHE / (identity + '.wav')
    if not raw.exists():
        content = request('/tts/bytes', key, body)
        signal, rate = sf.read(io.BytesIO(content), dtype='float32')
        sf.write(raw, signal, rate)
    signal, rate = sf.read(raw, dtype='float32')
    if signal.ndim > 1: signal = signal.mean(axis=1)
    active = np.flatnonzero(abs(signal) > .001)
    if len(active) == 0: raise ValueError('Speech was silent')
    signal = signal[max(0, active[0] - int(.05 * rate)):min(len(signal), active[-1] + int(.25 * rate))]
    peak = float(abs(signal).max()); rms = float(np.sqrt(np.mean(signal * signal)))
    signal *= min(.85 / max(peak, 1e-6), .14 / max(rms, 1e-6))
    return signal, rate


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--voice'); args = parser.parse_args()
    key = key_text()
    script = json.loads((FOLDER / 'script.json').read_text(encoding='utf-8'))
    voice, name, description = (args.voice, '', '') if args.voice else pick_voice(key)
    print(f'voice: {name or voice} {("· " + description[:90]) if description else ""}')
    manifest = dict(version=1, voice=voice, voiceName=name, sampleRate=RATE, clips=[])
    for i, step in enumerate(script['steps']):
        signal, rate = speak(key, voice, step.get('speech') or step['text'])
        out = FOLDER / f'{i:02d}-{step["id"]}.wav'
        sf.write(out, signal, rate, subtype='PCM_16')
        manifest['clips'].append(dict(id=step['id'], file=out.name, seconds=round(len(signal) / rate, 3)))
        print(f'{out.name}: {len(signal) / rate:.1f} s')
    (FOLDER / 'manifest.json').write_text(json.dumps(manifest, indent=1), encoding='utf-8')
    print('total', round(sum(c['seconds'] for c in manifest['clips']), 1), 's')


if __name__ == '__main__':
    main()
