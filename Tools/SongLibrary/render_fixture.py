"""Render the generated lyric fixture (PatternPrep --write-fixture lyrics) to audio.

The fixture's words and tunes are public domain and its arrangement is generated, so the
rendered audio is free to test with. Sung syllables are a formant voice following the
Lead Vocal's notes and pitch bends (its vibrato); spoken syllables are words from the
Windows speech synthesizer (offline), each placed at its first syllable's lyric event and
fitted to the time before the next word. Keys, bass and drums are simple synthesis.

Writes mix.wav and vocals.wav (the voice alone, as a separated stem would be) beside the MIDI.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import tempfile
from pathlib import Path

import mido
import numpy as np
import soundfile as sf

RATE = 22050


def tempo_map(mid: mido.MidiFile):
    """Beat → seconds from the MIDI's tempo events."""
    changes = [(0, 500000)]
    for track in mid.tracks:
        tick = 0
        for msg in track:
            tick += msg.time
            if msg.type == 'set_tempo':
                changes.append((tick, msg.tempo))
    changes.sort()
    ppq = mid.ticks_per_beat

    def seconds(tick: float) -> float:
        total, last_tick, last_tempo = 0.0, 0, changes[0][1]
        for t, tempo in changes[1:]:
            if t >= tick:
                break
            total += (t - last_tick) / ppq * last_tempo / 1e6
            last_tick, last_tempo = t, tempo
        return total + (tick - last_tick) / ppq * last_tempo / 1e6
    return seconds


def events(mid: mido.MidiFile):
    """Notes, bends and lyric events per track name, in seconds."""
    seconds = tempo_map(mid)
    out = {}
    for track in mid.tracks:
        name = next((m.name for m in track if m.type == 'track_name'), '')
        tick, held, notes, bends, lyrics = 0, {}, [], [], []
        for msg in track:
            tick += msg.time
            if msg.type == 'note_on' and msg.velocity > 0:
                held[(msg.channel, msg.note)] = (tick, msg.velocity)
            elif msg.type in ('note_off', 'note_on') and (msg.channel, msg.note) in held:
                start, velocity = held.pop((msg.channel, msg.note))
                notes.append((seconds(start), seconds(tick), msg.note, velocity / 127, msg.channel))
            elif msg.type == 'pitchwheel':
                bends.append((seconds(tick), msg.pitch / 8192 * 2))
            elif msg.type == 'lyrics':
                lyrics.append((seconds(tick), msg.text))
        out[name] = {'notes': notes, 'bends': bends, 'lyrics': lyrics}
    return out


def envelope(n: int, attack: float, release: float) -> np.ndarray:
    env = np.ones(n)
    a, r = max(1, int(attack * RATE)), max(1, int(release * RATE))
    env[:a] = np.linspace(0, 1, min(a, n))[:len(env[:a])]
    env[-r:] *= np.linspace(1, 0, min(r, n))
    return env


def voice(notes, bends, length: int) -> np.ndarray:
    """A sung 'ah': harmonics weighted by vowel formants, following pitch and bends."""
    out = np.zeros(length)
    bend_t = np.array([b[0] for b in bends]) if bends else np.zeros(1)
    bend_v = np.array([b[1] for b in bends]) if bends else np.zeros(1)
    formants = [(730, 90), (1090, 110), (2440, 170)]
    for start, end, pitch, velocity, _ in notes:
        a, b = int(start * RATE), min(length, int(end * RATE))
        if b <= a:
            continue
        t = np.arange(a, b) / RATE
        idx = np.searchsorted(bend_t, t, side='right') - 1
        semis = pitch + np.where(idx >= 0, bend_v[np.clip(idx, 0, None)], 0)
        f0 = 440 * 2 ** ((semis - 69) / 12)
        phase = 2 * np.pi * np.cumsum(f0) / RATE
        tone = np.zeros(b - a)
        for k in range(1, 30):
            fk = f0 * k
            gain = sum(np.exp(-0.5 * ((fk - f) / w) ** 2) * g for (f, w), g in zip(formants, (1, .7, .35))) + .02
            tone += np.where(fk < RATE / 2, gain / k ** .6 * np.sin(k * phase), 0)
        out[a:b] += tone * envelope(b - a, .03, .06) * velocity * .18
    return out


def speak(words: list[str], folder: Path) -> dict[str, np.ndarray]:
    """Each distinct word through the offline Windows speech synthesizer."""
    unique = sorted(set(words))
    script = ["Add-Type -AssemblyName System.Speech", "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer", "$s.Rate = 2"]
    for i, word in enumerate(unique):
        path = folder / f"w{i}.wav"
        script += [f"$s.SetOutputToWaveFile('{path}')", f"$s.Speak('{word.replace(chr(39), chr(39) * 2)}')"]
    script.append("$s.SetOutputToNull()")
    ps = folder / 'speak.ps1'
    ps.write_text('\n'.join(script), encoding='utf-8')
    subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(ps)], check=True, capture_output=True)
    audio = {}
    for i, word in enumerate(unique):
        data, rate = sf.read(folder / f"w{i}.wav")
        if data.ndim > 1:
            data = data.mean(axis=1)
        if rate != RATE:
            import librosa
            data = librosa.resample(data, orig_sr=rate, target_sr=RATE)
        # Trim the synthesizer's silence.
        loud = np.flatnonzero(np.abs(data) > .02 * np.abs(data).max())
        audio[word] = data[loud[0]:loud[-1] + 1] if len(loud) else data
    return audio


def spoken_words(lyric_events, sheet: Path):
    """Group spoken syllable events into the sheet's words: (start, word)."""
    words = []
    for line in sheet.read_text(encoding='utf-8').splitlines():
        line = line.strip()
        if not line or line.startswith(('#', '[')):
            continue
        for token in line.split():
            clean = re.sub(r"[^A-Za-z'\-]", '', token).strip('-')
            if clean:
                words.append(clean.split('-'))
    # Walk the sheet's words against the events, syllable by syllable.
    out, i = [], 0
    texts = [re.sub(r'[^a-z]', '', t.lower()) for _, t in lyric_events]
    for parts in words:
        if i >= len(lyric_events):
            break
        first = re.sub(r'[^a-z]', '', parts[0].lower())
        if texts[i] != first:
            continue
        out.append((lyric_events[i][0], ''.join(parts)))
        i += len(parts)
    return out


def render(midi_path: Path) -> tuple[Path, Path]:
    mid = mido.MidiFile(midi_path)
    tracks = events(mid)
    length = int((max(n[1] for t in tracks.values() for n in t['notes']) + 2) * RATE)
    mix = np.zeros(length)
    rng = np.random.default_rng(7)
    for name, data in tracks.items():
        for start, end, pitch, velocity, channel in data['notes']:
            a, b = int(start * RATE), min(length, int(end * RATE))
            if channel == 9 or name == 'Drums':
                n = int(.25 * RATE)
                b = min(length, a + n)
                t = np.arange(b - a) / RATE
                if pitch in (35, 36):
                    hit = np.sin(2 * np.pi * (50 + 90 * np.exp(-t * 30)) * t) * np.exp(-t * 14)
                elif pitch in (38, 40):
                    hit = rng.standard_normal(b - a) * np.exp(-t * 22) * .6 + np.sin(2 * np.pi * 190 * t) * np.exp(-t * 30) * .4
                else:
                    noise = rng.standard_normal(b - a)
                    hit = np.diff(noise, prepend=0) * np.exp(-t * 60) * .3
                mix[a:b] += hit * velocity * .5
            elif name == 'Bass':
                t = np.arange(b - a) / RATE
                f = 440 * 2 ** ((pitch - 69) / 12)
                mix[a:b] += (np.sin(2 * np.pi * f * t) + .3 * np.sin(4 * np.pi * f * t)) * envelope(b - a, .01, .05) * velocity * .25
            elif name == 'Keys':
                t = np.arange(b - a) / RATE
                f = 440 * 2 ** ((pitch - 69) / 12)
                tone = sum(np.sin(2 * np.pi * f * k * t) / k ** 1.5 for k in range(1, 6))
                mix[a:b] += tone * np.exp(-t * 1.8) * envelope(b - a, .005, .08) * velocity * .07
    vocal_track = tracks.get('Lead Vocal', {'notes': [], 'bends': []})
    vocals = voice(vocal_track['notes'], vocal_track['bends'], length)
    rap = tracks.get('Rap Vocal', {'lyrics': []})['lyrics']
    if rap:
        placed = spoken_words(rap, midi_path.parent / 'lyrics.txt')
        with tempfile.TemporaryDirectory() as folder:
            spoken = speak([w for _, w in placed], Path(folder))
        import librosa
        for k, (start, word) in enumerate(placed):
            room = (placed[k + 1][0] if k + 1 < len(placed) else start + .8) - start
            clip = spoken[word]
            if len(clip) / RATE > room * .95:
                clip = librosa.effects.time_stretch(clip, rate=len(clip) / RATE / (room * .95))
            a = int(start * RATE)
            b = min(length, a + len(clip))
            vocals[a:b] += clip[:b - a] / (np.abs(clip).max() + 1e-9) * .35
    mix += vocals
    peak = np.abs(mix).max() + 1e-9
    mix_path, vocal_path = midi_path.parent / 'mix.wav', midi_path.parent / 'vocals.wav'
    sf.write(mix_path, mix / peak * .9, RATE)
    sf.write(vocal_path, vocals / peak * .9, RATE)
    (midi_path.parent / 'render.json').write_text(json.dumps({'midi': midi_path.name, 'rate': RATE, 'seconds': length / RATE, 'spokenWords': len(rap)}), encoding='utf-8')
    return mix_path, vocal_path


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('midi', type=Path)
    args = parser.parse_args()
    mix, vocals = render(args.midi.resolve())
    print(f'{mix}\n{vocals}')
