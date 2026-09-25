"""Sync a lyric sheet to a recording, for songs whose MIDI carries no lyric timing.

  python lyric_sync.py <bundle-folder> [--audio recording.wav] [--vocals stems/vocals.wav]
                       [--lyrics lyrics.txt] [--aligner auto|onsets|mms] [--download-aligner]

Writes lyrics.timing.json beside the lyric sheet; PatternPrep reads it (after MIDI lyric
events, before the vocal's notes) and converts its seconds to the bundle's beats.

  • Beats and downbeats: beat_this (Foscarin, Schlüter & Widmer, ISMIR 2024) on the mix.
  • Words: the sheet's syllables aligned in order to the vocal's syllable onsets by dynamic
    programming (no model download). Line starts prefer onsets after a breath, onsets near
    the beat grid are preferred, and a spurious or missing onset costs by its strength.
    With --aligner mms, torchaudio's MMS forced aligner (wav2vec2, CTC) aligns the words
    instead; its weights (~1.2 GB) are downloaded only with --download-aligner.
  • Vibrato: pYIN pitch on the vocal; a voiced stretch whose pitch oscillates at 4–8.5 Hz by
    at least a tenth of a semitone around its moving average is a vibrato span.

The vocal stem (stems/vocals.wav, or --vocals) is used when present; alignment on a full
mix is weaker. Timing is evidence to review, not a certified transcription.
"""
from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path

import numpy as np
import soundfile as sf

ROOT = Path(__file__).resolve().parents[2]
MODELS = ROOT / 'SongLibraryData' / 'models' / 'torch'
RATE = 22050


# ---------- the lyric sheet (the same format PatternPrep reads) ----------
def vowel_groups(word: str) -> int:
    """Syllables by vowel groups, with PatternPrep's silent endings (a final e, -es, -ed)."""
    w = re.sub(r"[^a-z]", '', word.lower())
    groups = len(re.findall(r'[aeiouy]+', w))
    if groups > 1:
        if w.endswith('e') and not re.search(r'[^aeiou]le$', w) and not w.endswith(('ee', 'ye')):
            groups -= 1
        elif w.endswith('es') and not re.search(r'(s|x|z|ch|sh|ce|ge|se|ze)es$', w) and not re.search(r'[^aeiou]les$', w):
            groups -= 1
        elif w.endswith('ed') and not re.search(r'[td]ed$', w):
            groups -= 1
    return max(1, groups)


def read_sheet(path: Path):
    """Stanzas of lines of words; each word lists its syllables (hyphens, else a count)."""
    stanzas, current = [], None
    for raw in path.read_text(encoding='utf-8').splitlines():
        line = raw.strip()
        if not line or line.startswith('#'):
            if not line and current and current['lines']:
                current = None
            continue
        header = re.match(r'^\[(.+)\]$', line)
        if header:
            parts = [p.strip() for p in header.group(1).split('|')]
            mode = parts[1].lower() if len(parts) > 1 else ''
            spoken = 'spoken' in mode or 'rap' in mode or (not mode and re.search(r'\b(rap|spoken)\b', parts[0], re.I) is not None)
            current = {'name': parts[0], 'spoken': spoken and 'sung' not in mode, 'lines': []}
            stanzas.append(current)
            continue
        if current is None:
            current = {'name': f'Stanza {len(stanzas) + 1}', 'spoken': False, 'lines': []}
            stanzas.append(current)
        words = []
        for token in line.split():
            clean = re.sub(r"[^A-Za-z'\-_]", '', token).strip("-'")
            if not clean.strip('_-'):
                continue
            parts = [p.strip('_') for p in clean.split('-') if p.strip('_')] if '-' in clean else None
            text = ''.join(parts) if parts else clean.strip('_')
            words.append({'text': text, 'syllables': len(parts) if parts else vowel_groups(text), 'parts': parts or []})
        if words:
            current['lines'].append(words)
    return [s for s in stanzas if s['lines']]


def load(path: Path) -> np.ndarray:
    import librosa
    y, rate = sf.read(path, always_2d=True)
    y = y.mean(axis=1)
    return librosa.resample(y, orig_sr=rate, target_sr=RATE) if rate != RATE else y


# ---------- beats ----------
def beats_and_downbeats(audio: Path):
    """beat_this on the recording; ([], []) when it is not installed."""
    try:
        from beat_this.inference import File2Beats
    except ImportError:
        return [], [], 'beat_this not installed'
    import torch
    os.environ.setdefault('TORCH_HOME', str(MODELS))
    device = 'cuda' if torch.cuda.is_available() else 'cpu'
    beats, downbeats = File2Beats(checkpoint_path='final0', device=device, dbn=False)(str(audio))
    return [float(b) for b in beats], [float(d) for d in downbeats], 'beat_this final0'


# ---------- word alignment from syllable onsets ----------
HOP = 256


def pitch(vocal: np.ndarray):
    """pYIN pitch in semitones (A4 = 69; NaN where unvoiced), one frame per HOP."""
    import librosa
    f0, voiced, _ = librosa.pyin(vocal, fmin=80, fmax=1000, sr=RATE, frame_length=1024, hop_length=HOP)
    return np.where(voiced & np.isfinite(f0), 69 + 12 * np.log2(np.where(np.isfinite(f0), f0, 440) / 440), np.nan)


def onsets(vocal: np.ndarray, semis: np.ndarray | None = None, wobble: list | None = None):
    """Syllable onsets (seconds) with their strength: spectral-flux attacks, the points where
    a sung pitch steps to a new note or the voice starts, and dips in the voice's energy
    (a repeated note). Pitch steps and dips inside a vibrato span are the vibrato, not new
    syllables. Returns them with an energy envelope for finding breaths."""
    import librosa
    hop = HOP
    flux = librosa.onset.onset_strength(y=vocal, sr=RATE, hop_length=hop, lag=1)
    rms = librosa.feature.rms(y=vocal, frame_length=1024, hop_length=hop)[0]
    n = min(len(flux), len(rms))
    flux, rms = flux[:n], rms[:n]
    scale = np.percentile(flux, 95) + 1e-9
    shaking = np.zeros(n, dtype=bool)
    for span in wobble or []:
        shaking[int(span['start'] * RATE / hop) + 3:int(span['end'] * RATE / hop)] = True
    candidates = {int(f): flux[f] / scale for f in librosa.onset.onset_detect(onset_envelope=flux, sr=RATE, hop_length=hop, backtrack=False, delta=.03, wait=2)
                  if not shaking[f] or flux[f] / scale > 1}
    if semis is not None:
        s = semis[:n]
        for f in range(6, n - 6):
            if np.isnan(s[f]) or shaking[f]:
                continue
            before, after = s[f - 6:f], s[f:f + 6]
            if np.all(np.isnan(before[-3:])) and np.count_nonzero(~np.isnan(after)) >= 4:
                candidates[f] = max(candidates.get(f, 0), .8)          # the voice starts
            elif np.count_nonzero(~np.isnan(before)) >= 4 and np.count_nonzero(~np.isnan(after)) >= 4 and abs(np.nanmedian(after) - np.nanmedian(before)) >= .8:
                candidates[f] = max(candidates.get(f, 0), .7)          # a new note
    smooth = np.convolve(rms, np.ones(3) / 3, mode='same')
    floor = np.percentile(smooth, 30) * 1.5 + 1e-6
    for f in range(3, n - 3):
        if shaking[f]:
            continue
        if smooth[f] <= smooth[f - 1] and smooth[f] < smooth[f + 1] and smooth[f + 3] > floor:
            peak = min(smooth[max(0, f - 12):f].max(), smooth[f:f + 12].max())
            if peak > floor and smooth[f] < peak * .55:
                candidates[f + 1] = max(candidates.get(f + 1, 0), .6)  # a dip, then the next syllable
    # Candidates within 120 ms are one onset (at the earliest, with the strongest evidence).
    frames, power = [], []
    for f in sorted(candidates):
        if frames and f - frames[-1] <= 10:
            power[-1] = max(power[-1], candidates[f])
            continue
        frames.append(f)
        power.append(candidates[f])
    times = np.array(frames) * hop / RATE
    # Sung onsets are followed by a steady voiced pitch; speech wanders or is unvoiced.
    tonal = np.zeros(len(frames))
    if semis is not None:
        for k, f in enumerate(frames):
            after = semis[f + 2:f + 15]
            voiced = after[~np.isnan(after)]
            tonal[k] = 1.0 if len(voiced) >= .7 * len(after) and len(voiced) > 3 and np.std(voiced) < .6 else 0.0
    return times, np.clip(np.array(power), 0, 2), rms, hop, tonal


def silence_before(t: float, rms: np.ndarray, hop: int, floor: float) -> float:
    """How long the vocal was quiet just before t."""
    i = int(t * RATE / hop) - 1
    n = 0
    while i - n >= 0 and rms[i - n] < floor:
        n += 1
    return n * hop / RATE


def align_onsets(stanzas, vocal: np.ndarray, beats: list[float], semis: np.ndarray | None = None, wobble: list | None = None):
    """Each syllable on an onset, in order, by dynamic programming over (syllable, onset).
    A spoken syllable inside a word may go without one. Costs: an onset left unused (by its
    strength); syllables closer than 0.1 s; a long gap inside a line; a line that starts
    without a breath; an onset off the eighth-note grid (when beats are known)."""
    times, power, rms, hop, tonal = onsets(vocal, semis, wobble)
    floor = np.percentile(rms, 30) * 1.5 + 1e-6
    grid = np.array(sorted(beats + [(a + b) / 2 for a, b in zip(beats, beats[1:])])) if len(beats) > 1 else np.zeros(0)
    syllables = []
    for s, stanza in enumerate(stanzas):
        for l, words in enumerate(stanza['lines']):
            for w, word in enumerate(words):
                for k in range(word['syllables']):
                    syllables.append({'lineStart': w == 0 and k == 0, 'optional': stanza['spoken'] and k > 0, 'sung': not stanza['spoken']})
    n, m = len(syllables), len(times)
    if n == 0 or m == 0:
        return [], 'no onsets found'
    quiet = np.array([silence_before(t, rms, hop, floor) for t in times])
    near = np.array([np.min(np.abs(grid - t)) for t in times]) if len(grid) else np.zeros(m)
    eighth = float(np.median(np.diff(beats))) / 2 if len(beats) > 2 else 0.0
    skip = .3 + .5 * power
    prefix = np.concatenate([[0], np.cumsum(skip)])          # prefix[j] = cost of skipping onsets 0..j-1
    def fit(i, j):
        syl = syllables[i]
        # Rapped words land on the beat grid far more strictly than sung notes drift around it.
        c = (.35 if syl['sung'] else .9) * min(1, near[j] / .08) if len(grid) else 0
        if semis is not None:
            c += (1.0 if tonal[j] < .5 else 0) if syl['sung'] else (.5 if tonal[j] >= .5 else 0)
        if syl['lineStart']:
            c += -.5 if quiet[j] > .15 else .6
        elif quiet[j] > .45:
            c += 1.2
        return c
    W, INF = 24, 1e18
    cost = np.full((n, m), INF)
    back = np.full((n, m, 2), -1, dtype=np.int32)             # (previous matched syllable, its onset)
    fits = np.array([[fit(i, j) for j in range(m)] for i in range(n)])
    for j in range(m):
        # Onsets before the first syllable (an intro, or instruments bleeding into the stem) are cheap to leave.
        cost[0, j] = .25 * prefix[j] + fits[0, j]
    for i in range(1, n):
        # The previous matched syllable: i-1, or further back across optional syllables left without onsets.
        sources, missed = [], 0.0
        for q in range(i - 1, max(-1, i - 5), -1):
            sources.append((q, missed))
            if not syllables[q]['optional']:
                break
            missed += .35
        for j in range(1, m):
            lo = max(0, j - W)
            best, arg = INF, (-1, -1)
            gap = times[j] - times[lo:j]
            pace = np.where(gap < .1, 2.5, 0.0) + (0 if syllables[i]['lineStart'] else np.where(gap > 1.6, 1.0, 0.0))
            between = prefix[j] - prefix[lo + 1:j + 1]          # onsets strictly between j' and j left unused
            for q, miss in sources:
                # A sung line usually ends on a held note: a line start after a long gap.
                shape = np.where(gap >= .9, -.4, .5) if syllables[i]['lineStart'] and syllables[q]['sung'] else 0
                # Rapped syllables run about one per eighth: a gap should fit the syllables it spans.
                if not syllables[i]['sung'] and not syllables[q]['sung'] and eighth > 0 and not syllables[i]['lineStart']:
                    shape = shape + .6 * np.minimum(2, np.abs(gap - (i - q) * eighth) / eighth)
                total = cost[q, lo:j] + between + pace + miss + shape
                k = int(np.argmin(total))
                if total[k] < best:
                    best, arg = total[k], (q, lo + k)
            cost[i, j] = best + fits[i, j]
            back[i, j] = arg
    # The last syllable, then every onset after it unused.
    final = cost[n - 1] + .25 * (prefix[m] - prefix[1:m + 1])
    j = int(np.argmin(final))
    matched = [None] * n
    i = n - 1
    while i >= 0 and j >= 0:
        matched[i] = float(times[j])
        q, jj = back[i, j]
        i, j = (q, jj) if q >= 0 else (-1, -1)
    # Words: the first syllable's onset, or between its timed neighbours.
    starts = [None] * n
    known = [i for i in range(n) if matched[i] is not None]
    for i in range(n):
        if matched[i] is not None:
            starts[i] = matched[i]
            continue
        before = max((k for k in known if k < i), default=None)
        after = min((k for k in known if k > i), default=None)
        if before is not None and after is not None:
            starts[i] = matched[before] + (matched[after] - matched[before]) * (i - before) / (after - before)
        elif before is not None:
            starts[i] = matched[before] + .15 * (i - before)
        elif after is not None:
            starts[i] = max(0.0, matched[after] - .15 * (after - i))
    words, index = [], 0
    for stanza in stanzas:
        for words_in_line in stanza['lines']:
            for word in words_in_line:
                first = index
                index += word['syllables']
                if starts[first] is None:
                    continue
                confident = matched[first] is not None
                own = [round(starts[k], 4) for k in range(first, index) if starts[k] is not None]
                words.append({'text': word['text'], 'start': round(starts[first], 4), 'end': None, 'confidence': 1.0 if confident else .4,
                              'syllables': own if len(own) == word['syllables'] else []})
    for k, word in enumerate(words):
        nxt = words[k + 1]['start'] if k + 1 < len(words) else word['start'] + 1
        # The word lasts until the next word, or until the voice falls quiet before it.
        stop = word['start'] + .05
        f = int(stop * RATE / hop)
        while f < len(rms) and f * hop / RATE < nxt and rms[f] >= floor:
            f += 1
        word['end'] = round(min(nxt, max(stop, f * hop / RATE)), 4)
    matched_count = sum(1 for x in matched if x is not None)
    return words, f'syllable onsets ({matched_count} of {n} syllables on onsets)'


# ---------- word alignment with torchaudio's MMS forced aligner ----------
def align_mms(stanzas, vocal_path: Path, download: bool):
    """Phonetic forced alignment (wav2vec2 MMS, CTC) of the sheet's words to the vocal.
    Emissions are computed in 30 s chunks with overlap, so a whole song fits in memory, then
    the transcript is aligned against the joined emissions in one pass. A wildcard token
    between lines absorbs sung sounds the sheet does not write (ad-libs, oohs). Each word's
    character spans give its syllables' times."""
    import torch
    import torchaudio
    os.environ['TORCH_HOME'] = str(MODELS)
    bundle = torchaudio.pipelines.MMS_FA
    weights = MODELS / 'hub' / 'checkpoints' / Path(bundle._path).name
    if not weights.exists() and not download:
        raise RuntimeError(f'MMS aligner weights not cached ({weights}); pass --download-aligner to fetch them (~1.2 GB) or use --aligner onsets')
    device = 'cuda' if torch.cuda.is_available() else 'cpu'
    model = bundle.get_model(with_star=True).to(device)
    tokenizer, aligner = bundle.get_tokenizer(), bundle.get_aligner()
    waveform, rate = torchaudio.load(str(vocal_path))
    waveform = torchaudio.functional.resample(waveform.mean(0, keepdim=True), rate, bundle.sample_rate)
    total, hop = waveform.size(1), 320                       # 20 ms emission frames
    chunk, pad = 30 * bundle.sample_rate, int(1.5 * bundle.sample_rate)
    pieces = []
    with torch.inference_mode():
        for start in range(0, total, chunk):
            a, b = max(0, start - pad), min(total, start + chunk + pad)
            emission, _ = model(waveform[:, a:b].to(device))
            k0 = round((start - a) / hop); k1 = min(emission.size(1), k0 + round(min(chunk, total - start) / hop))
            pieces.append(emission[0, k0:k1].cpu())
        emission = torch.cat(pieces)
        # The transcript: every sheet word, a wildcard between lines.
        entries = []
        for s_, stanza in enumerate(stanzas):
            for line in stanza['lines']:
                entries.append(None)
                for word in line:
                    entries.append(word)
        tokens, index = [], []
        for e in entries:
            if e is None:
                tokens.append('*'); index.append(None); continue
            clean = re.sub(r"[^a-z']", '', e['text'].lower())
            if clean:
                tokens.append(clean); index.append(e)
        spans = aligner(emission, tokenizer(tokens))
    ratio = total / emission.size(0) / bundle.sample_rate
    words = []
    for word, span in zip(index, spans):
        if word is None:
            continue
        start, end = span[0].start * ratio, span[-1].end * ratio
        # Syllables start at their first letter's span: the sheet's parts, else an even share of letters.
        letters = [t for t in span]
        count = word['syllables']
        if word['parts']:
            cuts, at = [], 0
            for part in word['parts']:
                cuts.append(min(len(letters) - 1, at)); at += len(re.sub(r"[^a-z']", '', part.lower()))
        else:
            cuts = [min(len(letters) - 1, round(len(letters) * k / count)) for k in range(count)]
        words.append({'text': word['text'], 'start': round(start, 4), 'end': round(end, 4),
                      'confidence': round(float(np.mean([t.score for t in span])), 3),
                      'syllables': [round(letters[c].start * ratio, 4) for c in cuts] if len(cuts) == count else []})
    return words, 'MMS forced alignment (torchaudio, wav2vec2)'


# ---------- vibrato ----------
def vibrato(semis: np.ndarray):
    dt = HOP / RATE
    spans = []
    cents = semis * 100
    i = 0
    while i < len(cents):
        if np.isnan(cents[i]):
            i += 1
            continue
        j = i
        while j < len(cents) and not np.isnan(cents[j]):
            j += 1
        run = cents[i:j]
        if len(run) * dt >= .35:
            width = max(3, int(.2 / dt))
            trend = np.convolve(np.pad(run, width, mode='edge'), np.ones(2 * width + 1) / (2 * width + 1), mode='same')[width:-width]
            ripple = run - trend
            window = max(8, int(.4 / dt))
            flags = np.zeros(len(run), dtype=bool)
            rates, depths = np.zeros(len(run)), np.zeros(len(run))
            for a in range(0, len(run) - window + 1, max(1, window // 4)):
                seg = ripple[a:a + window]
                crossings = np.count_nonzero(np.diff(np.sign(seg - seg.mean())) != 0)
                rate = crossings / 2 / (window * dt)
                depth = (np.percentile(seg, 95) - np.percentile(seg, 5)) / 2 / 100
                if 4 <= rate <= 8.5 and depth >= .1:
                    flags[a:a + window] = True
                    rates[a:a + window] = rate
                    depths[a:a + window] = depth
            k = 0
            while k < len(flags):
                if not flags[k]:
                    k += 1
                    continue
                e = k
                while e < len(flags) and flags[e]:
                    e += 1
                if (e - k) * dt >= .3:
                    spans.append({'start': round((i + k) * dt, 3), 'end': round((i + e) * dt, 3), 'rate': round(float(rates[k:e].mean()), 2), 'depth': round(float(depths[k:e].mean()), 3)})
                k = e
        i = j
    return spans


def sync(folder: Path, audio: Path | None = None, vocals: Path | None = None, lyrics: Path | None = None, aligner: str = 'auto', download: bool = False) -> Path:
    lyrics = lyrics or folder / 'lyrics.txt'
    audio = audio or next((p for p in (folder / 'recording.wav', folder / 'mix.wav') if p.exists()), None)
    vocals = vocals or next((p for p in (folder / 'stems' / 'vocals.wav', folder / 'vocals.wav') if p.exists()), audio)
    if audio is None or not lyrics.exists():
        raise FileNotFoundError('Need a recording (recording.wav) and a lyric sheet (lyrics.txt)')
    stanzas = read_sheet(lyrics)
    beats, downbeats, beat_method = beats_and_downbeats(audio)
    voice = load(vocals)
    semis = pitch(voice)
    words, method, unavailable = None, '', ''
    if aligner in ('mms', 'auto'):
        try:
            words, method = align_mms(stanzas, vocals, download)
        except Exception as error:  # noqa: BLE001 - fall back to onsets, and say why
            if aligner == 'mms':
                raise
            unavailable = str(error)
    if words is None:
        found, how = align_onsets(stanzas, voice, beats, semis, vibrato(semis))
        words, method = found, how
    result = {'method': method, 'words': words, 'vibrato': vibrato(semis), 'beats': beats, 'downbeats': downbeats,
              'beatMethod': beat_method, 'audio': audio.name, 'vocals': vocals.name, 'forcedAlignerUnavailable': unavailable}
    out = lyrics.parent / 'lyrics.timing.json'
    out.write_text(json.dumps(result, indent=1), encoding='utf-8')
    return out


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('folder', type=Path)
    parser.add_argument('--audio', type=Path)
    parser.add_argument('--vocals', type=Path)
    parser.add_argument('--lyrics', type=Path)
    parser.add_argument('--aligner', choices=['auto', 'onsets', 'mms'], default='auto')
    parser.add_argument('--download-aligner', action='store_true')
    args = parser.parse_args()
    path = sync(args.folder.resolve(), args.audio, args.vocals, args.lyrics, args.aligner, args.download_aligner)
    data = json.loads(path.read_text(encoding='utf-8'))
    print(f"{path}: {len(data['words'])} words · {data['method']} · {len(data['vibrato'])} vibrato spans · {len(data['beats'])} beats, {len(data['downbeats'])} downbeats ({data['beatMethod']})")
