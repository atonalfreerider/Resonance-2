"""An editor's package for a Resonance video take: the parts of its mix, so an external editor can re-edit it.

    Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/edit_package.py Recordings/Videos/<take>.json
    Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/edit_package.py Recordings/Videos/<performance>.mp4

The Recorder's MP4 is a finished mix. Beside it this writes <take>-edit/ with everything that went into its sound:

  narration.wav          the voice alone (a tour's narration track, or the tutorial's step clips), 24-bit
  music.wav              the original music, never ducked, 32-bit float: what the take played of the song's
                         recording (tours), the app's own synth sound captured live during the take (tutorial), or the
                         take's own sound (a Quest performance, after the performance recorder's loudness pass)
  music-as-played.wav    tours whose cues solo a stem: the music as Director mode played it (the solos at their
                         soloGain, and the crossfades), still never ducked
  ducking.csv            one row per spoken line: when the take's music starts to dip, reaches the line's level,
                         starts back up and is back
  ducking-envelope.csv   the music gain at every video frame, as the take applied it
  captions.srt           the spoken words, in captions of at most two 42-character lines (UTF-8 with BOM, CRLF)
  timeline.xml           an FCP7 XML sequence (Premiere Pro, DaVinci Resolve, Final Cut Pro 7): the video, every stereo
                         file as a linked left/right pair, the music with the ducking as Audio Levels keyframes and
                         the narration at the take's own balance, and the MP4's own mix, muted, for reference
  check.json             how the parts were lined up with the video, the levels the take used, how closely the parts
                         rebuild its sound, and whether they match the take at all (matchesTake, problems)
  README.txt

Every file starts at the video's first frame and lasts as long as the video (its picture, not its container), so the
files line up when dropped at 00:00 on any timeline. Nothing is written until the package is complete: it is built in
a temporary folder beside the take and moved into place at the end.

What the package models, from the runtime's own rules: the music and the voice are found in the MP4's sound on their
own, each to a fraction of a sample (the voice can sit tens of ms off the music: the app decodes an MP3 recording with
NAudio, which keeps ~1729 samples of encoder delay that soundfile trims, while the voice is scheduled on the song
clock). SongNarration eases the music gain once a frame at 16/s down, 4/s up and 50/s once a releasing cue's lines are
over; StemPlayback crossfades the recording and a soloed stem linearly over 0.45 s (soloGain clamped to 0.25-4, and
dropped at the release); SongAudio and StemPlayback apply both report.duckLagFrames frames later (1 for takes recorded
before that field existed), from the 1024-sample DSP block where that frame's Update stood. Unity plays a mono voice
centre-panned at -3 dB, so a tour's narration sits near 0.95 x 0.71 = 0.67 of the music. A take that starts mid-song
(report.songStart) played nothing of the song before it: everything on the song clock sits at report.audioStart + t,
and its captions start at the first word it played. The captions say the narration's own words (narration.json's
spokenWords, the speech the voice was rendered from) at their aligned times when it has them.

When the parts do not rebuild the take (reproduction SNR under 15 dB, an implausible narration level, a voice not
where narration.wav has it, a line's duck more than 0.5 dB off the cue sheet, cue markers off the story, a narration
rendered for an earlier story.json) check.json and README.txt say so and the exit code is 2; such a package never
replaces an earlier one that matched its take (it goes to <take>-edit-failed). A take whose story.json or narration
changed since it was recorded (report.storySha256 / narrationSha256) gets no package at all, also exit 2.
"""
import argparse
import csv
import hashlib
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from fractions import Fraction
from pathlib import Path
from urllib.parse import quote

import numpy as np
import soundfile as sf
from scipy.signal import correlate, resample_poly

ROOT = Path(__file__).resolve().parents[2]
LIBRARY = ROOT/'PreparedSongs/Library'
TUTORIAL = ROOT/'Assets/StreamingAssets/Tutorial'
RATE = 48000
VOICE_GAIN = .95                 # SongNarration: voice.volume = master * 0.95
PAN = math.sqrt(.5)              # Unity plays a mono clip centre-panned: -3 dB in each channel
LEAD, TAIL = .12, .15            # SongNarration: the music dips from 0.12 s before a line until 0.15 s after it
ATTACK, RELEASE, RELEASE_SOLO = 16., 4., 50.   # per-second rates of its exponential approach
DEFAULT_DUCK = .42               # SongNarration.DefaultDuck, for manifests that predate measured ducking
CROSSFADE = .45                  # StemPlayback.CrossfadeSeconds
SOLO_GAIN = (.25, 4.)            # StemPlayback clamps a cue's soloGain to this
DSP_BLOCK = 1024                 # ProjectSettings/AudioManager m_DSPBufferSize
CAPTION_CHARS, CAPTION_LINES, CAPTION_SECONDS = 42, 2, 6.
MIN_SNR_DB = 15.                 # tour takes rebuild to 23-26 dB; a voice 39 ms off or a changed bundle to 2-9 dB
NARRATION_RANGE = (.3, 1.5)      # the narration's level against the music (tours) or absolute (the tutorial): ~0.67
DUCK_DB = .5                     # the music level measured under a line against the modelled one (takes: within 0.08 dB)
MUSIC_FLOOR_DB = -30.            # the music's share of the sound under a line below which its level cannot be measured
MARKER_TOLERANCE = .1            # seconds between a report's cue marker and the frame the story puts that cue on
VOICE_FOUND = .5                 # normalised correlation of a narrated line with what the music leaves of the mix
CHUNK = 10*RATE                  # long takes are fitted and written ten seconds at a time


class Refused(Exception):
    """The take cannot get a package: its parts would not be the ones it played."""


# ------------------------------------------------------------------------------------------------- audio helpers

def mono(signal):
    return signal.mean(axis=1, dtype='float32') if signal.ndim > 1 else signal


def resample(signal, source, target=RATE, downmix=False, block=1 << 20):
    """`signal` from `source` Hz to `target`, float32, a block at a time (optionally averaged to mono on the way): the
    same samples as one resample_poly over the whole (the blocks overlap by the filter's reach, on its phase grid),
    without its whole-length temporaries."""
    if int(source) == target and not downmix:
        return signal
    g = math.gcd(int(source), target)
    up, down = target//g, int(source)//g
    pad = down*math.ceil((10*max(up, down)/up+2)/down)
    step, n = down*max(1, block//down), len(signal)
    out = np.empty((-(-n*up//down),)+(() if downmix or signal.ndim == 1 else (signal.shape[1],)), dtype='float32')
    for i0 in range(0, n, step):
        i1, a = min(n, i0+step), max(0, i0-pad)
        x = mono(signal[a:min(n, i1+pad)]) if downmix else signal[a:min(n, i1+pad)]
        y = resample_poly(x, up, down, axis=0) if (up, down) != (1, 1) else x
        m0, m1 = i0*up//down, min(len(out), -(-i1*up//down))
        out[m0:m1] = y[(i0-a)*up//down:][:m1-m0]
    return out


def load(path, channels=2):
    """A file as float32 at 48 kHz: a mono file repeated in both channels, or (channels=1) a stereo one averaged."""
    signal, source = sf.read(path, dtype='float32', always_2d=True)
    if signal.shape[1] != channels:
        signal = signal.mean(axis=1, keepdims=True, dtype='float32')
    signal = resample(signal, source)
    return np.repeat(signal, channels, axis=1) if signal.shape[1] != channels else signal


def video_info(video):
    """The picture's size, rate and length in frames (the video stream's own, not the container's, which an audio
    stream can outlast), and the audio stream's rate and channels."""
    out = subprocess.run(['ffprobe', '-v', 'error', '-show_entries',
                          'stream=codec_type,width,height,r_frame_rate,nb_frames,duration,sample_rate,channels',
                          '-of', 'json', str(video)], capture_output=True, text=True, check=True)
    streams = json.loads(out.stdout).get('streams', [])
    v = next((s for s in streams if s.get('codec_type') == 'video'), None)
    if v is None:
        raise ValueError(f'{video} has no video stream')
    a = next((s for s in streams if s.get('codec_type') == 'audio'), None)
    rate = Fraction(v['r_frame_rate'])
    frames = int(v.get('nb_frames') or 0) or int(round(float(v['duration'])*rate))
    return dict(width=int(v['width']), height=int(v['height']), fps=float(rate), ntsc=rate.denominator == 1001,
                frames=frames, audio=a is not None, audio_rate=int(a.get('sample_rate') or RATE) if a else RATE,
                audio_channels=int(a.get('channels') or 2) if a else 2)


def take_audio(video, length):
    """The MP4's own sound as float32 (length, 2) at 48 kHz, or None when it has no audio stream or it fails to
    decode; and whether it is silent."""
    with tempfile.TemporaryDirectory(prefix='resonance-edit-') as folder:
        wav = Path(folder)/'mix.wav'
        result = subprocess.run(['ffmpeg', '-v', 'error', '-y', '-i', str(video), '-vn', '-ac', '2', '-ar', str(RATE),
                                 '-c:a', 'pcm_f32le', str(wav)], capture_output=True)
        if result.returncode != 0 or not wav.exists():
            return None, False
        mix = np.zeros((length, 2), dtype='float32')
        with sf.SoundFile(wav) as f:
            f.read(out=mix[:min(length, f.frames)], dtype='float32')
    return mix, peak(mix) <= 1e-5


def peak(signal):
    return max([float(np.max(np.abs(signal[a:a+CHUNK]))) for a in range(0, len(signal), CHUNK)], default=0.)


class Placed:
    """A source on the video's timeline: its sample i sounds at video sample at*RATE + i, to a fraction of a sample
    (a 65-tap Kaiser-windowed sinc: a voice one 48 kHz sample off costs the rebuild ~9 dB), and nothing before
    source sample `first` sounds (a take that starts mid-song never played the song's head)."""
    def __init__(self, signal, at, first=0):
        self.signal, self.first = signal, max(0, int(first))
        position = at*RATE
        self.start = int(math.floor(position))
        frac = position-self.start
        if frac > 1-1e-4:
            self.start, frac = self.start+1, 0.
        self.kernel = None
        if frac > 1e-4:
            n = np.arange(-32, 33)
            h = np.sinc(n-frac)*np.kaiser(len(n), 8.)
            self.kernel = (h/h.sum()).astype('float32')[:, None]

    @property
    def channels(self):
        return self.signal.shape[1]

    def chunk(self, a, b):
        """Video samples [a, b) of this source, (b-a, channels)."""
        pad = 32 if self.kernel is not None else 0
        lo, hi = a-self.start-pad, b-self.start+pad
        out = np.zeros((hi-lo, self.channels), dtype='float32')
        s0, s1 = max(lo, self.first), min(hi, len(self.signal))
        if s1 > s0:
            out[s0-lo:s1-lo] = self.signal[s0:s1]
            if self.kernel is not None:                   # direct, not FFT: what was silent stays exactly 0
                out = np.stack([np.convolve(out[:, c], self.kernel[:, 0], mode='valid') for c in range(out.shape[1])], axis=1)
        return out[:b-a]


def write_wav(path, length, channels, subtype, chunk):
    with sf.SoundFile(path, 'w', RATE, channels, subtype) as f:
        for a in range(0, length, CHUNK):
            f.write(chunk(a, min(length, a+CHUNK)))


def decimate(signal):
    return resample(signal, RATE, 8000, downmix=True)


def xcorr(ref, can, lo, hi, block=1 << 15):
    """corr[k-lo] = sum_t ref[t+k]*can[t] for every lag lo..hi, a block of `can` at a time (a whole-take FFT of a
    36-minute take would need gigabytes)."""
    out = np.zeros(hi-lo+1)
    for t0 in range(0, len(can), block):
        c = can[t0:t0+block]
        if not np.any(c):
            continue
        a, b = t0+lo, t0+len(c)+hi
        seg = np.zeros(b-a, dtype='float32')
        s0, s1 = max(a, 0), min(b, len(ref))
        if s1 > s0:
            seg[s0-a:s1-a] = ref[s0:s1]
            out += correlate(seg, c, mode='valid', method='fft')
    return out


def best_lag(reference, candidate, estimate, search, work=8000):
    """Seconds to delay `candidate` (its sample 0 at the video's time 0) so it lines up best with `reference`,
    searched within `search` seconds of `estimate`, and the normalised correlation there over the span the two share
    (a take of a minute of a song is not scored against the rest of it): a coarse search at 8 kHz (refine() takes it
    below a sample)."""
    ref, can = decimate(reference), decimate(candidate)
    if not len(can) or not np.any(can):
        return estimate, 0.
    lo, hi = int(math.floor((estimate-search)*work)), int(math.ceil((estimate+search)*work))
    corr = xcorr(ref, can, lo, hi)
    k = int(np.argmax(corr))
    lag = lo+k
    t0, t1 = max(0, -lag), min(len(can), len(ref)-lag)
    norm = math.sqrt(float(np.dot(ref[t0+lag:t1+lag], ref[t0+lag:t1+lag]))*float(np.dot(can[t0:t1], can[t0:t1]))) if t1 > t0 else 0.
    return lag/work, float(corr[k]/norm) if norm > 0 else 0.


def vertex(c, i):
    """The fractional offset of a correlation peak at c[i]: the vertex of the parabola through it and its neighbours."""
    if 0 < i < len(c)-1:
        den = c[i-1]-2*c[i]+c[i+1]
        if den < 0:
            return float(np.clip(.5*(c[i-1]-c[i+1])/den, -.5, .5))
    return 0.


def refine(mix, candidate, at, first=0, span=6):
    """`at` (the video time of candidate sample 0, found on the 8 kHz grid, up to 3 samples off at 48 kHz) refined to
    a fraction of a sample: the mix's correlation with the candidate (from sample `first`) at the whole lags around
    it at 48 kHz, ten seconds at a time."""
    k0 = int(round(at*RATE))+first                      # the video sample of candidate sample `first`
    c = np.zeros(2*span+1)
    i0, i1 = max(first, span-k0+first), min(len(candidate), len(mix)-span-k0+first)
    for c0 in range(i0, i1, CHUNK):
        c1 = min(i1, c0+CHUNK)
        x, y = mono(mix[k0-first+c0-span:k0-first+c1+span]), mono(candidate[c0:c1])
        c += [float(np.dot(x[d:d+len(y)], y)) for d in range(2*span+1)]
    i = int(np.argmax(c))
    return (k0-span+i+vertex(c, i)-first)/RATE


# ------------------------------------------------------------------------------------------- the take's runtime

def dsp_clock(frames, fps):
    """The video sample where Unity's DSP clock (midi.Position's) stood in each frame's Update: the start of the next
    1024-sample block to mix. The Recorder's AudioRenderer mixes whole blocks from the take's first sample, so a volume
    set in that Update is heard from there (take 140444: every gain step lands on floor(1600 k / 1024) * 1024)."""
    return (np.round(np.arange(frames+1)*RATE/fps).astype(np.int64)//DSP_BLOCK)*DSP_BLOCK


def clamp(value, lo, hi):
    return min(hi, max(lo, value))


def directed(cues, segments, solo_stems, frames, fps, clock, song_start, song_end, lag, narrated):
    """What Director mode applied at every video frame, from the runtime's rules. SongDirector (order 80) picks the cue
    at midi.Position and solos its stem until soloUntil or, with releaseSoloAfterNarration, until its lines are over
    (at once when no narration plays); SongNarration (order 90) eases MusicGain toward the lowest duck of the lines
    whose window [start-0.12, end+0.15) holds the position; StemPlayback moves the recording's and each stem's gain
    linearly over 0.45 s, the soloed stem times its soloGain; SongAudio and StemPlayback apply it all `lag` frames
    later (0 from LateUpdate; 1 in takes recorded while they set it in Update, before the director and the narration
    had decided the frame). Frame k's values are heard from dsp_clock[k]: (duck gain, recording gain, {stem: gain})."""
    bounds = dsp_clock(frames, fps)
    position = bounds[:frames]/RATE-clock
    playing = (position >= song_start-1e-9) & (position < song_end-.001)
    now = np.where(position < song_start, song_start, position)          # paused at the seek point until it plays
    index = np.full(frames, -1)
    for i in range(len(cues)-1, -1, -1):                                  # Array.FindIndex: the first cue that holds it
        index[(now >= cues[i]['start']) & (now < cues[i]['end'])] = i
    last = last_ends(segments)
    after = position >= song_end-.001                                     # the tour has ended: no solo, no ducking
    selected, solo_gain, released = ['']*frames, np.ones(frames), np.zeros(frames, dtype=bool)
    for k in range(frames):
        i = int(index[k])
        if i < 0 or after[k]:
            continue
        c = cues[i]
        finished = not narrated or now[k] >= last.get(i, -math.inf)       # SongNarration.CueFinished
        released[k] = c.get('releaseSoloAfterNarration') is True and finished
        release = (float(c.get('soloUntil') or 0) > 0 and now[k] >= float(c['soloUntil'])) or released[k]
        stem = '' if release else (c.get('stem') or '')
        selected[k] = stem if stem in solo_stems else ''
        if selected[k] and float(c.get('soloGain') or 0) > 0:
            solo_gain[k] = clamp(float(c['soloGain']), *SOLO_GAIN)
    target = np.ones(frames)
    if narrated:
        for s in segments:
            w = playing & (now >= s['start']-LEAD) & (now < s['end']+TAIL)
            target[w] = np.minimum(target[w], s.get('duck') or DEFAULT_DUCK)
    target[released | ~playing] = 1.
    dt = 1/fps
    alpha = {ATTACK: 1-math.exp(-dt*ATTACK), RELEASE: 1-math.exp(-dt*RELEASE), RELEASE_SOLO: 1-math.exp(-dt*RELEASE_SOLO)}
    music_gain, g = np.empty(frames), 1.
    for k in range(frames):
        g += (target[k]-g)*alpha[ATTACK if target[k] < 1 else RELEASE_SOLO if released[k] else RELEASE]
        music_gain[k] = g
    # StemPlayback starts from where the paused tour left it: the solo of the cue at the seek point fully in.
    start = int(np.argmax(position >= song_start)) if np.any(position >= song_start) else 0
    initial = selected[start]
    master, gains, step = 0. if initial else 1., {s: float(s == initial) for s in solo_stems}, dt/CROSSFADE
    env, rec, stems = np.empty(frames), np.empty(frames), {s: np.empty(frames) for s in solo_stems}
    for j in range(frames):
        k = j-lag
        sel, sg, mg = (selected[k], solo_gain[k], music_gain[k]) if k >= 0 else (initial, solo_gain[start], 1.)
        master += clamp((0. if sel else 1.)-master, -step, step)
        for s in gains:
            gains[s] += clamp(float(s == sel)-gains[s], -step, step)
            stems[s][j] = gains[s]*(sg if s == sel else 1.)
        rec[j], env[j] = master, mg
    return dict(env=env, rec=rec, stems=stems, bounds=bounds, position=position, playing=playing, lag=lag)


def per_sample(values, bounds, a, b):
    """Per-frame values as the take heard them on video samples [a, b): frame k's from bounds[k] to bounds[k+1] (the
    last frame's to the end)."""
    n = len(values)
    k0 = min(n-1, max(0, int(np.searchsorted(bounds, a, side='right'))-1))
    k1 = min(n-1, max(k0, int(np.searchsorted(bounds, b-1, side='right'))-1))
    starts = np.maximum(bounds[k0:k1+1], a)
    return np.repeat(values[k0:k1+1].astype('float32'), np.r_[starts[1:], b]-starts)[:, None]


def last_ends(segments):
    """Each narrated cue's last line end: SongNarration.CueFinished is true from there (and at once for a cue with
    no line)."""
    last = {}
    for s in segments:
        last[s['cue']] = max(last.get(s['cue'], -math.inf), s['end'])
    return last


def heard(sim, song_time):
    """The first video frame whose Update saw the song clock reach `song_time`, and the frame that applied it (None
    when the take ended first)."""
    k = int(np.searchsorted(sim['position'], song_time-1e-9))
    return k, k+sim['lag'] if k+sim['lag'] < len(sim['env']) else None


# --------------------------------------------------------------------------------------------- ducking (tours)

def played_line(s, clock, first_play, end):
    """Whether the take played any of a spoken line (video seconds [first_play, end))."""
    return clock+s['end'] > first_play and clock+s['start'] < end


def duck_rows(segments, cues, sim, clock, song_start, first_play, end, texts):
    """One row per spoken line the take played, read off the modelled take: when its music starts to dip, reaches
    95% of the way to the line's level, starts back up (at the line's end once a releaseSoloAfterNarration cue's
    lines are over, else 0.15 s after it) and is 95% of the way back; blank where the take never gets there (the next
    line dips first, or the take ends)."""
    bounds, env, last = sim['bounds'], sim['env'], last_ends(segments)
    order = sorted(range(len(segments)), key=lambda i: segments[i]['start'])
    lines = [i for i in order if played_line(segments[i], clock, first_play, end)]
    seconds = lambda j: '' if j is None else round(bounds[j]/RATE, 3)
    rows = []
    for n, i in enumerate(lines):
        s = segments[i]
        duck = s.get('duck') or DEFAULT_DUCK
        cue = cues[s['cue']] if 0 <= s['cue'] < len(cues) else {}
        fast = cue.get('releaseSoloAfterNarration') is True and s['end'] >= last[s['cue']]
        d0 = heard(sim, max(s['start']-LEAD, song_start))[1]
        r0 = heard(sim, s['end']+(0. if fast else TAIL))[1]
        upto = heard(sim, segments[lines[n+1]]['start']-LEAD)[1] if n+1 < len(lines) else None
        reached = None if d0 is None else next((j for j in range(d0, len(env) if r0 is None else r0+1) if env[j] <= duck+.05*(1-duck)), None)
        back = None if r0 is None else next((j for j in range(r0, len(env) if upto is None else upto) if env[j] >= 1-.05*(1-duck)), None)
        rows.append({'line': i+1, 'cue': s['cue'], 'voice_start_s': round(max(clock+s['start'], first_play), 3),
                     'voice_end_s': round(min(clock+s['end'], end), 3), 'duck_starts_s': seconds(d0),
                     'duck_reached_s': seconds(reached), 'release_starts_s': seconds(r0), 'release_done_s': seconds(back),
                     'music_gain': round(duck, 4), 'music_gain_db': round(20*math.log10(duck), 2), 'text': texts[i]})
    return rows


# ------------------------------------------------------------------------------------------------------ captions

def spoken_text(cue):
    """What the narrator says: the cue's speech (or its text) without a leading [direction], which Eleven v4 takes as
    performance control and does not speak."""
    return re.sub(r'^\s*\[[^\]]*\]\s*', '', cue.get('speech') or cue.get('text') or '')


def stamp(seconds):
    ms = int(round(max(0., seconds)*1000))
    return f'{ms//3600000:02d}:{ms//60000 % 60:02d}:{ms//1000 % 60:02d},{ms % 1000:03d}'


def wrap(text):
    """One line, or the two-line split with the shorter longer line (a break after punctuation preferred); the text
    unsplit when no split gives two lines of 42 characters (caption_blocks never lets that through)."""
    if len(text) <= CAPTION_CHARS:
        return [text]
    words = text.split()
    best, best_cost = [text], 1e9
    for k in range(1, len(words)):
        a, b = ' '.join(words[:k]), ' '.join(words[k:])
        cost = max(len(a), len(b))+(0 if a.endswith((',', '.', ';', ':', '!', '?')) else 3)
        if len(a) <= CAPTION_CHARS and len(b) <= CAPTION_CHARS and cost < best_cost:
            best, best_cost = [a, b], cost
    return best


def fits(text):
    lines = wrap(text)
    return len(lines) <= CAPTION_LINES and max(map(len, lines)) <= CAPTION_CHARS


def caption_blocks(words, starts, end):
    """Words grouped into captions of at most two 42-character lines and 6 s, breaking after punctuation where it
    can; each caption runs until the next starts (or the line ends)."""
    pieces = []
    for w, t in zip(words, starts):          # a word longer than a caption line is cut, so no line ever exceeds it
        pieces += [(w[i:i+CAPTION_CHARS], t) for i in range(0, len(w), CAPTION_CHARS)] or [(w, t)]
    blocks, current = [], []
    for w, t in pieces:
        if current and (not fits(' '.join(x for x, _ in current+[(w, t)])) or t-current[0][1] > CAPTION_SECONDS):
            blocks.append(current); current = []
        current.append((w, t))
        if re.search(r'[.!?;:]["”’)]*$', w) and len(' '.join(x for x, _ in current)) > CAPTION_CHARS*.6:
            blocks.append(current); current = []
    if current:
        blocks.append(current)
    out = []
    for j, block in enumerate(blocks):
        start = block[0][1]
        finish = blocks[j+1][0][1]-.04 if j+1 < len(blocks) else end
        out.append((start, max(start+.5, finish), '\n'.join(wrap(' '.join(w for w, _ in block)))))
    return out


def word_times(text, start, end, word_starts=None):
    """Each word's start: aligned times when there is one per word (relative to the line's start, as story_timing.py
    and tutorial_timing.py write them), else spread over the line by characters."""
    words = text.split()
    if word_starts is not None and len(word_starts) == len(words):
        return words, [start+float(t) for t in word_starts]
    lengths = np.array([len(w)+1 for w in words], dtype=float)
    offsets = np.concatenate([[0.], np.cumsum(lengths)[:-1]])/max(1., lengths.sum())
    return words, [start+o*(end-start) for o in offsets]


SPOKEN_TIMES = ('spokenStarts', 'wordStarts')


def aligned_starts(segment, words):
    """The narration's own word starts for a line (seconds from its start) when narration.json has one per spoken
    word: story_timing.py's forced alignment of the rendered voice, spokenStarts (wordStarts in older bundles, which
    aligned a line only when its caption and its speech had as many words)."""
    for key in SPOKEN_TIMES:
        value = segment.get(key)
        if isinstance(value, list) and len(value) == len(words) and all(isinstance(t, (int, float)) for t in value):
            return value, key
    return None, None


def unspoken(words):
    """Which words are a [direction] inside a line (story_timing.py aligns them in place, but nobody says them)."""
    out, tag = [], False
    for w in words:
        tag = tag or w.startswith('[')
        out.append(tag)
        tag = tag and not w.endswith(']')
    return out


def said(words):
    return ' '.join(w for w, tag in zip(words, unspoken(words)) if not tag)


def line_words(segment, cue):
    """A spoken line's words, and their aligned starts when narration.json has them: the narration's own spokenWords
    (the speech it was rendered from, still what the voice says after the story is edited), else the cue's speech as
    the story has it now."""
    words = segment.get('spokenWords')
    if isinstance(words, list) and words and all(isinstance(w, str) for w in words):
        words = ' '.join(words).split()
    else:
        words = spoken_text(cue).split()
    return words, aligned_starts(segment, words)[0]


def tour_captions(segments, spoken, clock, song_start, first_play, end):
    """The captions of the lines the take played, on the video's clock, from the first word it heard (a take that
    starts mid-line never played the words before), and how they are timed."""
    captions, lines, aligned = [], 0, 0
    for s, (words, starts) in zip(segments, spoken):
        if not played_line(s, clock, first_play, end):
            continue
        lines += 1
        aligned += starts is not None
        words, times = word_times(' '.join(words), s['start'], s['end'], starts)
        heard_words = [(w, t) for w, t, tag in zip(words, times, unspoken(words)) if not tag and t >= song_start-1e-6]
        if heard_words:
            captions += [(a+clock, b+clock, x) for a, b, x in caption_blocks(*map(list, zip(*heard_words)), s['end'])]
    timing = None if not lines else ("every line on the narration's aligned word times" if aligned == lines else
                                     f'{lines-aligned} of {lines} lines spread over the line by characters (no aligned word times)')
    return clip_captions(captions, first_play, end), timing


def clip_captions(captions, first, end):
    """Only what the take played: captions inside [first, end), the first and last cut to fit."""
    out = []
    for a, b, text in captions:
        a, b = max(a, first), min(b, end)
        if b-a >= .2:
            out.append((a, b, text))
    return out


def write_srt(path, captions):
    """SRT as Premiere reads accented text: UTF-8 with a byte-order mark, CRLF line ends. No file when nothing is
    spoken (a caption importer stops on an empty one)."""
    if not captions:
        return False
    with open(path, 'w', encoding='utf-8-sig', newline='\r\n') as f:
        for i, (a, b, text) in enumerate(captions, 1):
            f.write(f'{i}\n{stamp(a)} --> {stamp(b)}\n{text}\n\n')
    return True


def write_csv(path, header, rows):
    with open(path, 'w', encoding='utf-8-sig', newline='') as f:     # BOM: Excel reads the É and curly quotes
        w = csv.writer(f)
        w.writerow(header)
        w.writerows(rows)


# --------------------------------------------------------------------------------------------- FCP7 XML timeline

def pathurl(path):
    """file://localhost/C:/... with everything outside RFC 3986's unreserved set, '/' and ':' %-escaped as UTF-8."""
    return 'file://localhost/'+quote(Path(path).resolve().as_posix().lstrip('/'), safe='/:')


def rate_xml(parent, fps, ntsc=False):
    r = ET.SubElement(parent, 'rate')
    ET.SubElement(r, 'timebase').text = str(int(round(fps*1001/1000 if ntsc else fps)))
    ET.SubElement(r, 'ntsc').text = 'TRUE' if ntsc else 'FALSE'


def write_timeline(path, name, video, info, tracks, frames, markers=(), final=lambda file: file):
    """tracks: [(label, file, level or None, keyframes [(frame, gain)] or [], enabled)]; final(file) is where a file
    will be once the package is in place (its pathurl, which is absolute). A stereo file goes in as
    FCP7 and Premiere write one: a clipitem per channel (source channel 1 on one track to output 1, channel 2 on the
    next to output 2), linked as a stereo pair, which Premiere joins back into one stereo clip; a single item reading
    source channel 1 would import the left channel alone. The MP4's own sound is linked to its picture. Returns the
    audio layout written, for the README."""
    fps, ntsc = info['fps'], info.get('ntsc', False)
    root = ET.Element('xmeml', version='4')
    seq = ET.SubElement(root, 'sequence', id='sequence-1', explodedTracks='true')
    ET.SubElement(seq, 'name').text = name
    ET.SubElement(seq, 'duration').text = str(frames)
    rate_xml(seq, fps, ntsc)
    tc = ET.SubElement(seq, 'timecode')
    rate_xml(tc, fps, ntsc)
    ET.SubElement(tc, 'string').text = '00:00:00:00'
    ET.SubElement(tc, 'frame').text = '0'
    ET.SubElement(tc, 'displayformat').text = 'NDF'
    media = ET.SubElement(seq, 'media')
    vid = ET.SubElement(media, 'video')
    fmt = ET.SubElement(ET.SubElement(vid, 'format'), 'samplecharacteristics')
    rate_xml(fmt, fps, ntsc)
    for tag, value in (('width', info['width']), ('height', info['height']), ('anamorphic', 'FALSE'),
                       ('pixelaspectratio', 'square'), ('fielddominance', 'none'), ('colordepth', 24)):
        ET.SubElement(fmt, tag).text = str(value)
    vtrack = ET.SubElement(vid, 'track')
    files = {}

    def audio_of(file):
        if str(file) == str(video):
            return 16, info['audio_rate'], info['audio_channels']
        i = sf.info(str(file))
        return {'PCM_16': 16, 'PCM_24': 24, 'PCM_32': 32, 'FLOAT': 32, 'DOUBLE': 64}.get(i.subtype, 24), i.samplerate, i.channels

    def file_element(parent, file, kind):
        key = str(file)
        if key in files:
            ET.SubElement(parent, 'file', id=files[key])
            return
        fid = files[key] = f'file-{len(files)+1}'
        f = ET.SubElement(parent, 'file', id=fid)
        ET.SubElement(f, 'name').text = Path(file).name
        ET.SubElement(f, 'pathurl').text = pathurl(final(file))
        rate_xml(f, fps, ntsc)
        ET.SubElement(f, 'duration').text = str(frames)
        m = ET.SubElement(f, 'media')
        if kind == 'video':
            sc = ET.SubElement(ET.SubElement(m, 'video'), 'samplecharacteristics')
            rate_xml(sc, fps, ntsc)
            ET.SubElement(sc, 'width').text = str(info['width'])
            ET.SubElement(sc, 'height').text = str(info['height'])
        if kind == 'audio' or info.get('audio'):
            depth, rate, channels = audio_of(file)
            a = ET.SubElement(m, 'audio')
            sc = ET.SubElement(a, 'samplecharacteristics')
            ET.SubElement(sc, 'depth').text = str(depth)
            ET.SubElement(sc, 'samplerate').text = str(rate)
            ET.SubElement(a, 'channelcount').text = str(channels)

    def clip(track, cid, label, file, kind, level=None, keys=(), channel=1, enabled=True, stereo=False):
        c = ET.SubElement(track, 'clipitem', id=cid, **({'premiereChannelType': 'stereo'} if stereo else {}))
        ET.SubElement(c, 'name').text = label
        ET.SubElement(c, 'enabled').text = 'TRUE' if enabled else 'FALSE'
        ET.SubElement(c, 'duration').text = str(frames)
        rate_xml(c, fps, ntsc)
        for tag, value in (('start', 0), ('end', frames), ('in', 0), ('out', frames)):
            ET.SubElement(c, tag).text = str(value)
        file_element(c, file, kind)
        if kind == 'audio':
            st = ET.SubElement(c, 'sourcetrack')
            ET.SubElement(st, 'mediatype').text = 'audio'
            ET.SubElement(st, 'trackindex').text = str(channel)
        if level is not None or keys:
            effect = ET.SubElement(ET.SubElement(c, 'filter'), 'effect')
            for tag, value in (('name', 'Audio Levels'), ('effectid', 'audiolevels'), ('effectcategory', 'audiolevels'),
                               ('effecttype', 'audiolevels'), ('mediatype', 'audio')):
                ET.SubElement(effect, tag).text = value
            p = ET.SubElement(effect, 'parameter', authoringApp='PremierePro')
            ET.SubElement(p, 'parameterid').text = 'level'
            ET.SubElement(p, 'name').text = 'Level'
            ET.SubElement(p, 'valuemin').text = '0'
            ET.SubElement(p, 'valuemax').text = '3.98109'
            ET.SubElement(p, 'value').text = f'{clamp(level if level is not None else 1., 0., 3.98109):.5f}'
            for frame, gain in keys:
                kf = ET.SubElement(p, 'keyframe')
                ET.SubElement(kf, 'when').text = str(int(frame))
                ET.SubElement(kf, 'value').text = f'{clamp(gain, 0., 3.98109):.5f}'
        return c

    def link(c, ref, mediatype, trackindex, group=None):
        l = ET.SubElement(c, 'link')
        ET.SubElement(l, 'linkclipref').text = ref
        ET.SubElement(l, 'mediatype').text = mediatype
        ET.SubElement(l, 'trackindex').text = str(trackindex)
        ET.SubElement(l, 'clipindex').text = '1'
        if group is not None:
            ET.SubElement(l, 'groupindex').text = str(group)

    video_clip = clip(vtrack, 'clipitem-video', Path(video).name, video, 'video')
    ET.SubElement(vtrack, 'enabled').text = 'TRUE'
    ET.SubElement(vtrack, 'locked').text = 'FALSE'
    aud = ET.SubElement(media, 'audio')
    ET.SubElement(aud, 'numOutputChannels').text = '2'
    afmt = ET.SubElement(ET.SubElement(aud, 'format'), 'samplecharacteristics')
    ET.SubElement(afmt, 'depth').text = '24'
    ET.SubElement(afmt, 'samplerate').text = str(RATE)
    outputs = ET.SubElement(aud, 'outputs')
    for ch in (1, 2):
        g = ET.SubElement(outputs, 'group')
        ET.SubElement(g, 'index').text = str(ch)
        ET.SubElement(g, 'numchannels').text = '1'
        ET.SubElement(g, 'downmix').text = '0'
        ET.SubElement(ET.SubElement(g, 'channel'), 'index').text = str(ch)
    layout, number = [], 0
    for n, (label, file, level, keys, enabled) in enumerate(tracks, 1):
        sides = (1, 2) if audio_of(file)[2] >= 2 else (1,)
        pair = []
        for side in sides:
            number += 1
            attrs = dict(currentExplodedTrackIndex=str(side-1), totalExplodedTrackCount='2', premiereTrackType='Stereo') if len(sides) == 2 else {}
            track = ET.SubElement(aud, 'track', **attrs)
            item = clip(track, f'clipitem-audio-{n}-{side}', label, file, 'audio', level, keys, side, enabled, len(sides) == 2)
            pair.append((item, number))
            ET.SubElement(track, 'enabled').text = 'TRUE' if enabled else 'FALSE'
            ET.SubElement(track, 'locked').text = 'FALSE'
            ET.SubElement(track, 'outputchannelindex').text = str(side)
        with_video = str(file) == str(video)
        for item, _ in pair:
            if with_video:
                link(item, 'clipitem-video', 'video', 1)
            for other, index in pair:
                link(item, other.get('id'), 'audio', index, 1)
        if with_video:
            link(video_clip, 'clipitem-video', 'video', 1)
            for other, index in pair:
                link(video_clip, other.get('id'), 'audio', index, 1)
        tracks_named = f'A{pair[0][1]}-A{pair[-1][1]}' if len(pair) == 2 else f'A{pair[0][1]}'
        what = [f'{label} ({Path(file).name})'+('' if enabled else ', muted')]
        if keys:
            what.append('the ducking as Audio Levels keyframes'+(f', at the take\'s music level {20*math.log10(max(level or 1, 1e-6)):+.1f} dB' if level not in (None, 1.) else ''))
        elif level is not None:
            what.append(f'at {level:.3f} ({20*math.log10(max(level, 1e-6)):+.1f} dB)')
        layout.append(f'{tracks_named:<8} '+', '.join(what))
    for frame, title, comment in markers:
        mk = ET.SubElement(seq, 'marker')
        ET.SubElement(mk, 'name').text = title
        ET.SubElement(mk, 'comment').text = comment
        ET.SubElement(mk, 'in').text = str(int(frame))
        ET.SubElement(mk, 'out').text = '-1'
    ET.indent(root)
    with open(path, 'wb') as f:
        f.write(b'<?xml version="1.0" encoding="UTF-8"?>\n<!DOCTYPE xmeml>\n')
        f.write(ET.tostring(root, encoding='utf-8'))
    return layout


def keyframes(gain, tolerance=.004):
    """Few keyframes that follow the per-frame gain within `tolerance` (linear interpolation between them)."""
    keys = [0]
    i, n = 0, len(gain)
    while i < n-1:
        j = i+1
        while j+1 < n:
            a, b = gain[i], gain[j+1]
            span = np.arange(i, j+2)
            if np.max(np.abs(a+(b-a)*(span-i)/(j+1-i)-gain[i:j+2])) > tolerance:
                break
            j += 1
        keys.append(j)
        i = j
    return [(k, float(gain[k])) for k in keys]


# -------------------------------------------------------------------------------------------------------- kinds

def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def find_bundle(report):
    """The take's bundle: the folder RecordingSession names, else the narrated bundle whose story or library title
    is the song's (the song list shows a toured song by its story's title), newest first."""
    if report.get('bundle') and Path(report['bundle']).is_dir():
        return Path(report['bundle'])
    want = (report.get('song') or '').strip().lower()
    found = []
    for folder in LIBRARY.iterdir():
        titles = []
        for meta in ('story.json', 'library.json'):
            try:
                titles.append(str(json.loads((folder/meta).read_text(encoding='utf-8')).get('title', '')).strip().lower())
            except (OSError, ValueError):
                pass
        if want and (want in titles or want == folder.name.lower()):
            found.append(folder)
    narrated = [f for f in found if (f/'story.json').exists() and (f/'narration.json').exists()]
    if not (narrated or found):
        raise ValueError(f'No library bundle titled {report.get("song")!r}')
    return max(narrated or found, key=lambda f: (f/'story.json').stat().st_mtime if (f/'story.json').exists() else 0)


def identity(report, bundle):
    """Refuse a take whose bundle is no longer what it played (the report's hashes, written since takes recorded
    them); else whether its narration played, and how that is known."""
    want = (report.get('storySha256') or '').lower()
    if want:
        if not (bundle/'story.json').exists():
            raise Refused(f'{bundle.name} has no story.json any more: the take played a story that is gone')
        if sha256(bundle/'story.json') != want:
            raise Refused(f"{bundle.name}/story.json changed since this take was recorded: the package would carry another "
                          "story's narration, captions and ducking. Record the tour again.")
    narration_path = bundle/'narration.json'
    if 'narrationSha256' in report:
        want = (report.get('narrationSha256') or '').lower()
        if not want or report.get('narrationPlayed') is False:
            return False, 'the take played no narration'
        if not narration_path.exists():
            raise Refused(f'{bundle.name} has no narration.json any more: the take played a narration that is gone')
        narration = json.loads(narration_path.read_text(encoding='utf-8'))
        audio = bundle/narration.get('audioPath', '')
        known = {str(narration.get('sha256') or '').lower(), sha256(narration_path)}
        if audio.is_file():
            known.add(sha256(audio))
        if want not in known:
            raise Refused(f'{bundle.name}: the narration was regenerated since this take was recorded: the voice in the take '
                          'is in no file of the bundle. Record the tour again.')
        return True, 'the report names the narration the take played'
    if report.get('narrationPlayed') is False:
        return False, 'the take played no narration'
    if not narration_path.exists():
        return False, 'the bundle has no narration'
    return True, 'assumed (the report predates narrationSha256): the measured checks decide'


def audio_start_estimate(report, song_start):
    """report.audioStart (video seconds of song time 0, negative when the take starts mid-song; unknown when songStart
    is -1, or when it is -1 in an older report that has no songStart), else the first cue marker (where the song
    started playing) less songStart."""
    value = report.get('audioStart')
    if value is not None and math.isfinite(float(value)) and (float(report['songStart']) >= 0 if report.get('songStart') is not None else float(value) >= 0):
        return float(value)
    return (float(report['markers'][0]['start']) if report.get('markers') else 0.)-song_start


def marker_problems(report, cues, sim, fps, first_play):
    """Cue markers the story does not put where the take had them: a story edited since the take (older reports have
    no storySha256), checked by the frame each cue should have started on and by its title."""
    off = []
    for m in report.get('markers', [])[1:]:
        match = re.fullmatch(r'cue-(\d+)', str(m.get('id', '')))
        if not match:
            continue
        i = int(match.group(1))
        if i >= len(cues):
            off.append(f"{m['id']} (the story has {len(cues)} cues)")
            continue
        k = int(np.searchsorted(sim['position'], cues[i]['start']-1e-9))
        expected = max(k/fps, first_play)
        title = ' '.join(str(m.get('title') or '').split())
        if abs(float(m['start'])-expected) > MARKER_TOLERANCE:
            off.append(f"{m['id']} at {float(m['start']):.2f} s (the story puts it at {expected:.2f} s)")
        elif title and title != ' '.join(str(cues[i].get('text') or '').split()):
            off.append(f"{m['id']} titled {title[:40]!r} (the story's cue reads {str(cues[i].get('text') or '')[:40]!r})")
    return off


def tour(report, take, out, sound, info, length, frames):
    bundle = find_bundle(report)
    narrated, how = identity(report, bundle)
    story = json.loads((bundle/'story.json').read_text(encoding='utf-8'))
    narration = json.loads((bundle/'narration.json').read_text(encoding='utf-8')) if (bundle/'narration.json').exists() else dict(segments=[])
    manifest = json.loads((bundle/'aligned.mid.prepared.json').read_text(encoding='utf-8'))
    cues = story['cues']
    segments = [s for s in narration.get('segments', []) if 0 <= s.get('cue', -1) < len(cues)] if narrated else []
    fps, song_start = info['fps'], max(0., float(report.get('songStart') or 0.))     # -1: the song never played
    lag = int(report['duckLagFrames']) if report.get('duckLagFrames') is not None else 1
    original = load(bundle/manifest.get('audioPath', 'recording.wav'))
    song_end = min(len(original)/RATE, float(story.get('duration') or math.inf))
    first = int(round(song_start*RATE))
    estimate = audio_start_estimate(report, song_start)
    check = dict(kind='tour', bundle=bundle.name, narration=how, songStart=song_start, estimate=round(estimate, 4), duckLagFrames=lag)
    notes, problems = [], []
    spoken = [line_words(s, cues[s['cue']]) for s in segments]
    texts = [said(words) for words, _ in spoken]
    # SongNarration plays only a narration rendered for the story as it is, so a narrated take played the story this
    # narration was rendered for: a story.json edited since (the speech, say, before the voice is rendered again) is
    # not the take's. Reports with storySha256 are refused for it in identity(); this catches the older ones.
    if narrated and narration.get('storySha256') and str(narration['storySha256']).lower() != sha256(bundle/'story.json'):
        own = all(isinstance(s.get('spokenWords'), list) for s in segments)
        problems.append("narration.json was rendered for an earlier story.json (the app would refuse it as stale now): the story "
                        "was edited after this take, so its cue times, solos and markers may not be what the take played"
                        +("; the captions keep the narration's own words" if own else "; the captions and ducking.csv read the edited story"))
    stems = {s['id']: s for s in manifest.get('stems', [])}
    solo_stems, sources = set(), {}
    for i, c in enumerate(cues):
        stem = c.get('stem') or ''
        if not stem:
            continue
        if stem not in stems:
            notes.append(f'cue {i} solos {stem!r}, which the bundle does not have: Director mode could not solo it')
        elif not stems[stem].get('audioPath'):
            solo_stems.add(stem)
            notes.append(f'cue {i} solos {stem!r}, a MIDI-synthesis stem: the app synthesised it live and its sound is in no file')
        else:
            solo_stems.add(stem)
            if stem not in sources:
                sources[stem] = load(bundle/stems[stem]['audioPath'])
    voice = load(bundle/narration['audioPath'], channels=1) if narrated else None
    mix, silent = sound()                              # decoded once the sources are in, not beside their load
    mix = None if silent else mix
    audio_start = estimate
    if mix is not None:
        coarse, score = best_lag(mix, original[first:], estimate+song_start, .75)
        audio_start = refine(mix, original, coarse-song_start, first)
        check.update(alignedBy='cross-correlation of the music with the MP4 audio, refined at 48 kHz', correlation=round(score, 4),
                     audioStart=round(audio_start, 6))
    recording = Placed(original, audio_start, first)
    placed_stems = {s: Placed(x, audio_start, first) for s, x in sources.items()}

    def run(clock):
        return directed(cues, segments, solo_stems, frames, fps, clock, song_start, song_end, lag, narrated)

    def played(sim, a, b, ducked=True):
        if placed_stems or np.any(sim['rec'] != 1):
            x = recording.chunk(a, b)*per_sample(sim['rec'], sim['bounds'], a, b)
            for s, p in placed_stems.items():
                x += p.chunk(a, b)*per_sample(sim['stems'][s], sim['bounds'], a, b)
        else:
            x = recording.chunk(a, b)
        return x*per_sample(sim['env'], sim['bounds'], a, b) if ducked else x

    clock = audio_start
    sim = run(clock)
    if mix is not None and narrated and segments:
        # The voice is scheduled on the song clock, which need not be where the music file's sample 0 landed: find
        # each line in what the music leaves of the mix, and take the lines' common offset.
        mm = mx = 0.
        for a in range(0, length, CHUNK):
            m = played(sim, a, min(length, a+CHUNK))
            mm += float(np.sum(m*m)); mx += float(np.sum(m*mix[a:a+len(m)]))
        level = mx/mm if mm > 0 else 0.
        found, tried = [], []
        for i, s in enumerate(segments):
            a, b = max(0, int(round((audio_start+s['start']-.25)*RATE))), min(length, int(round((audio_start+s['end']+.25)*RATE)))
            i0 = int(round(s['start']*RATE))
            x = voice[i0:int(round(s['end']*RATE)), 0]
            if s['end']-s['start'] < .5 or s['start'] < song_start or b-a < len(x)+RATE//10:
                continue
            y = mono(mix[a:b]-level*played(sim, a, b))
            if not np.any(x):
                continue
            c = correlate(y, x, mode='valid', method='fft')
            k = int(np.argmax(c))
            energy = float(np.dot(y[k:k+len(x)], y[k:k+len(x)]))*float(np.dot(x, x))
            score = float(c[k])/math.sqrt(energy) if energy > 0 else 0.
            tried.append((i, score))
            if score > VOICE_FOUND:                       # voice sample i0 sounds at video sample a+k
                found.append((a+k+vertex(c, k)-i0)/RATE-audio_start)
        lost = [i+1 for i, score in tried if score <= VOICE_FOUND]
        if found and len(found)*2 >= len(tried):
            shift = float(np.median(found))
            clock = audio_start+shift
            check.update(voiceStart=round(clock, 6), voiceShiftMs=round(shift*1000, 3),
                         voiceSpreadMs=round(max(abs(f-shift) for f in found)*1000, 3), voiceLinesFound=f'{len(found)} of {len(tried)}')
            if lost:
                problems.append(f"spoken lines {', '.join(map(str, lost))}: narration.wav's voice there is not the take's "
                                '(narration regenerated since the take?)')
        elif tried:
            check['voiceLinesFound'] = f'{len(found)} of {len(tried)}'
            problems.append("narration.wav's voice is not in the take: its narration, captions and ducking cues are "
                            "not where the take has them (a narration regenerated or off since the take?)")
        sim = run(clock)
    first_play = max(0., clock+song_start)              # on the song clock: where the voice and the cues begin
    # The music starts playing where the song clock does: a recording that sits off the clock (an MP3's decoder delay)
    # starts that much before or after songStart.
    recording.first = max(0, int(round((clock+song_start-audio_start)*RATE)))
    for p in placed_stems.values():
        p.first = recording.first
    voice_placed = Placed(voice, clock, first) if narrated else None
    end = length/RATE
    music_level, voice_level = 1., (VOICE_GAIN*PAN if narrated else 0.)
    if mix is not None:
        # The take's own balance: mix = music_level x music-as-played x ducking + voice_level x narration, fitted by
        # least squares a chunk at a time (the nominal 0.95 is what the app asks for; the take is what it got).
        mm = mv = vv = mx = vx = xx = 0.
        for a in range(0, length, CHUNK):
            b = min(length, a+CHUNK)
            m, x = played(sim, a, b), mix[a:b]
            mm += float(np.sum(m*m)); mx += float(np.sum(m*x)); xx += float(np.sum(x*x))
            if voice_placed is not None:                  # the mono voice, the same in both channels
                v = voice_placed.chunk(a, b)
                mv += float(np.sum(m*v)); vv += 2*float(np.sum(v*v)); vx += float(np.sum(v*x))
        if voice_placed is not None and mm*vv-mv*mv > 1e-12:
            det = mm*vv-mv*mv
            music_level, voice_level = (mx*vv-vx*mv)/det, (vx*mm-mx*mv)/det
        else:
            music_level, voice_level = (mx/mm if mm > 0 else 0.), 0.
        rr = xx-2*(music_level*mx+voice_level*vx)+music_level**2*mm+2*music_level*voice_level*mv+voice_level**2*vv
        snr = 10*math.log10(max(xx, 1e-12)/max(rr, 1e-12*max(xx, 1e-12)))
        check.update(musicLevel=round(music_level, 4), reproductionSnrDb=round(snr, 2))
        if snr < MIN_SNR_DB:
            problems.append(f'the parts rebuild the MP4 sound to only {snr:.1f} dB (a matching take is above {MIN_SNR_DB:g} dB)')
        if voice_placed is not None:
            relative = voice_level/music_level if music_level > 1e-6 else 0.
            check.update(narrationLevel=round(voice_level, 4), narrationRelativeToMusic=round(relative, 4),
                         narrationRelativeDb=round(20*math.log10(max(relative, 1e-6)), 2))
            if not NARRATION_RANGE[0] <= relative <= NARRATION_RANGE[1]:
                problems.append(f'the narration fits the take at {relative:.3f} of the music (a take has it near {VOICE_GAIN*PAN:.2f})')
        # Each spoken line: the music heard under the voice against the modelled ducking (the cue sheet's duck, or less
        # where a neighbouring line's window still holds the music down), fitted as one factor on the music as the
        # model ducks it, so a gain that moves within the window is weighed as the take heard it; in dB, as a duck of
        # 0.14 played where the cue sheet now says 0.18 is 2.2 dB, where 0.04 at 0.8 would be 0.4. Over a rest the music
        # has no level to hear or measure, and the fit there is noise.
        last, lines = last_ends(segments), []
        for i, s in enumerate(segments):
            fast = cues[s['cue']].get('releaseSoloAfterNarration') is True and s['end'] >= last[s['cue']]
            a = max(int((clock+s['start']-LEAD+3/ATTACK+.1)*RATE), int(first_play*RATE))
            b = min(int((clock+s['end']+(0. if fast else TAIL-.1))*RATE), length)
            if b-a < RATE//2 or music_level <= 1e-6:
                continue
            m, v, y = played(sim, a, b), voice_placed.chunk(a, b), mix[a:b].ravel()
            sol, *_ = np.linalg.lstsq(np.c_[m.ravel(), np.repeat(v, 2, axis=1).ravel()], y, rcond=None)
            model, ratio = float(np.mean(per_sample(sim['env'], sim['bounds'], a, b))), float(sol[0])/music_level
            share = 10*math.log10(max(music_level**2*float(np.sum(m*m)), 1e-30)/max(float(np.dot(y, y)), 1e-30))
            line = dict(line=i+1, cueSheet=round(s.get('duck') or DEFAULT_DUCK, 4), model=round(model, 4), musicShareDb=round(share, 1))
            if share >= MUSIC_FLOOR_DB:
                line.update(measured=round(model*ratio, 4), offDb=round(20*math.log10(max(ratio, 1e-6)), 2))
            lines.append(line)
        check['duckingMeasured'] = lines
        off = [l for l in lines if 'offDb' in l and abs(l['offDb']) > DUCK_DB]
        if off:
            worst = max((l['offDb'] for l in off), key=abs)
            problems.append(f"spoken lines {', '.join(str(l['line']) for l in off)}: the music under them is not at the cue sheet's "
                            f"level (up to {worst:+.1f} dB from it: ducks changed since the take?)")
        markers = marker_problems(report, cues, sim, fps, first_play)
        if markers:
            problems.append('cue markers off the story (edited since the take?): '+'; '.join(markers[:4])+(' ...' if len(markers) > 4 else ''))
    # The files, written a chunk at a time.
    files = {}
    if narrated:
        write_wav(out/'narration.wav', length, 2, 'PCM_24', lambda a, b: np.repeat(voice_placed.chunk(a, b), 2, axis=1))
        files['narration'] = 'narration.wav'
    write_wav(out/'music.wav', length, 2, 'FLOAT', recording.chunk)
    files['music'] = 'music.wav'
    soloed = bool(placed_stems) or bool(np.any(sim['rec'] != 1))
    if soloed:
        write_wav(out/'music-as-played.wav', length, 2, 'FLOAT', lambda a, b: played(sim, a, b, ducked=False))
        files['musicAsPlayed'] = 'music-as-played.wav'
    rows = duck_rows(segments, cues, sim, clock, song_start, first_play, end, texts) if narrated else []
    if rows:
        write_csv(out/'ducking.csv', list(rows[0].keys()), [list(r.values()) for r in rows])
        files['ducking'] = 'ducking.csv'
        write_csv(out/'ducking-envelope.csv', ['frame', 'time_s', 'music_gain', 'music_gain_db'],
                  [[k, f'{k/fps:.4f}', f'{g:.5f}', f'{20*math.log10(max(g, 1e-6)):.2f}'] for k, g in enumerate(sim['env'])])
        files['envelope'] = 'ducking-envelope.csv'
    captions, timing = tour_captions(segments, spoken, clock, song_start, first_play, end)
    if write_srt(out/'captions.srt', captions):
        files['captions'] = 'captions.srt'
    if timing:
        check['captionTiming'] = timing
    trusted = mix is not None and not problems
    music_keys = keyframes(sim['env']*(music_level if trusted else 1.))
    music_file = out/(files.get('musicAsPlayed') or files['music'])
    tracks = []
    if info.get('audio'):
        tracks.append(('MP4 mix (reference)', take, None, [], False))
    tracks.append(('Music as played (ducked)' if soloed else 'Music (ducked)', music_file, music_level if trusted else None, music_keys, True))
    if narrated:
        tracks.append(('Narration', out/'narration.wav', voice_level if trusted else VOICE_GAIN*PAN, [], True))
    if soloed:
        tracks.append(('Original recording (muted)', out/'music.wav', music_level if trusted else None, music_keys, False))
    lines_of = {}
    for s, text in zip(segments, texts):
        lines_of.setdefault(s['cue'], []).append(text)
    markers = []
    for m in report.get('markers', []):
        match = re.fullmatch(r'cue-(\d+)', str(m.get('id', '')))
        i = int(match.group(1)) if match else -1
        known = 0 <= i < len(cues) and not problems
        title = str(cues[i].get('text') or '') if known else str(m.get('title') or m.get('id'))
        comment = ' '.join(lines_of.get(i) or [said(spoken_text(cues[i]).split())]) if known else ''
        markers.append((round(float(m['start'])*fps), title or str(m.get('id')), comment))
    files['timeline'] = 'timeline.xml'
    check.update(files=files, lines=len(rows), captions=len(captions), lowestGain=round(float(sim['env'].min()), 4),
                 timelineLevels='the take\'s own' if trusted else 'nominal (the fit is not trusted)', notes=notes, problems=problems)
    return check, dict(tracks=tracks, markers=markers)


def tutorial(report, take, out, sound, info, length, frames):
    mix, silent = sound()
    mix = None if silent else mix
    manifest = json.loads((TUTORIAL/'manifest.json').read_text(encoding='utf-8'))
    script = json.loads((TUTORIAL/'script.json').read_text(encoding='utf-8'))
    texts = {s['id']: s.get('text', '') for s in script.get('steps', [])}
    clips = {c['id']: c for c in manifest['clips']}
    voices, captions, placed, problems, notes = [], [], [], [], []
    for m in report.get('markers', []):
        c = clips.get(m['id'])
        if not c or not (TUTORIAL/c['file']).exists():
            continue
        clip = load(TUTORIAL/c['file'], channels=1)
        at, score = float(m.get('voiceStart', m['start'])), None
        if mix is not None:
            a, b = int(max(0., at-1)*RATE), int(min(length/RATE, at+len(clip)/RATE+1)*RATE)
            lag, score = best_lag(mix[a:b], clip, at-a/RATE, .4)
            if score > .2:
                at = refine(mix, clip, a/RATE+lag)
        voices.append(Placed(clip, at))
        placed.append(dict(step=m['id'], voiceStart=round(at, 6), correlation=None if score is None else round(score, 4)))
        words, starts = word_times(texts.get(m['id'], ''), 0., len(clip)/RATE, c.get('starts'))
        if words:
            captions += [(a+at, b+at, x) for a, b, x in caption_blocks(words, starts, len(clip)/RATE)]

    def voice(a, b):
        v = np.zeros((b-a, 1), dtype='float32')
        for p in voices:
            if p.start < b and p.start+len(p.signal)+32 > a:
                v += p.chunk(a, b)
        return v
    check = dict(kind='tutorial', steps=placed)
    lost = [p['step'] for p in placed if p['correlation'] is not None and p['correlation'] <= .2]
    if lost:
        problems.append(f"step clips not found in the MP4's sound (left at their marker times): {', '.join(lost)}")
    write_wav(out/'narration.wav', length, 2, 'PCM_24', lambda a, b: np.repeat(voice(a, b), 2, axis=1))
    files = dict(narration='narration.wav')
    synth = take.with_name(report['synthStem']) if report.get('synthStem') else Path(take).with_suffix('.synth.wav')
    app = None
    if synth.exists():
        captured = load(synth)
        at = float(report.get('synthStart', 0.))
        if mix is not None:
            lag, score = best_lag(mix, captured, at, .3)
            at = refine(mix, captured, lag)
            check.update(synthCorrelation=round(score, 4), synthShift=round(at-float(report.get('synthStart', 0.)), 6))
        app = Placed(captured, at)
        write_wav(out/'music.wav', length, 2, 'FLOAT', app.chunk)
        files['music'] = 'music.wav'
    else:
        notes.append('no app sound: this take was recorded before the synth capture existed, so the parts rebuild only its narration')
    levels = dict(narration=1., app=1.)
    if mix is not None:
        parts = [('narration', lambda a, b: np.repeat(voice(a, b), 2, axis=1))]+([('app', app.chunk)] if app else [])
        gram, rhs, xx = np.zeros((len(parts), len(parts))), np.zeros(len(parts)), 0.
        for a in range(0, length, CHUNK):
            b = min(length, a+CHUNK)
            xs, x = [f(a, b) for _, f in parts], mix[a:b]
            for i, p in enumerate(xs):
                rhs[i] += float(np.sum(p*x))
                for j, q in enumerate(xs):
                    gram[i, j] += float(np.sum(p*q))
            xx += float(np.sum(x*x))
        solution = np.linalg.lstsq(gram, rhs, rcond=None)[0]
        rr = xx-2*float(solution@rhs)+float(solution@gram@solution)
        snr = 10*math.log10(max(xx, 1e-12)/max(rr, 1e-12*max(xx, 1e-12)))
        levels.update({name: float(v) for (name, _), v in zip(parts, solution)})
        check.update(narrationLevel=round(levels['narration'], 4), narrationLevelDb=round(20*math.log10(max(levels['narration'], 1e-6)), 2),
                     reproductionSnrDb=round(snr, 2))
        if app:
            check['appSoundLevel'] = round(levels['app'], 4)
            if snr < MIN_SNR_DB:
                problems.append(f'the parts rebuild the MP4 sound to only {snr:.1f} dB (a matching take is above {MIN_SNR_DB:g} dB)')
        if not NARRATION_RANGE[0] <= levels['narration'] <= NARRATION_RANGE[1]:
            problems.append(f"the narration fits the take at {levels['narration']:.3f} (Unity plays the mono clips near {PAN:.2f})")
    captions = clip_captions(captions, 0., length/RATE)
    if write_srt(out/'captions.srt', captions):
        files['captions'] = 'captions.srt'
    trusted = mix is not None and not problems
    tracks = [('MP4 mix (reference)', take, None, [], False)] if info.get('audio') else []
    if app:
        tracks.append(('App sound', out/'music.wav', levels['app'] if trusted else 1., [], True))
    tracks.append(('Narration', out/'narration.wav', levels['narration'] if trusted else PAN, [], True))
    markers = [(round(float(m['start'])*info['fps']), str(m.get('title') or m.get('id')), '') for m in report.get('markers', [])]
    files['timeline'] = 'timeline.xml'
    check.update(files=files, captions=len(captions), notes=notes, problems=problems,
                 timelineLevels='the take\'s own' if trusted else 'nominal (the fit is not trusted)')
    return check, dict(tracks=tracks, markers=markers)


def performance(report, take, out, sound, info, length, frames):
    mix, silent = sound()
    if mix is None or silent:
        raise Refused(f'{take.name} has no sound: a performance package is its sound')
    write_wav(out/'music.wav', length, 2, 'FLOAT', lambda a, b: mix[a:b])
    top = peak(mix)
    rms = math.sqrt(sum(float(np.sum(mix[a:a+CHUNK]**2)) for a in range(0, length, CHUNK))/max(1, 2*length))
    check = dict(kind='performance', peakDbfs=round(20*math.log10(max(top, 1e-9)), 2), rmsDbfs=round(20*math.log10(max(rms, 1e-9)), 2),
                 mp4AudioRate=info['audio_rate'], problems=[], files=dict(music='music.wav', timeline='timeline.xml'),
                 notes=['nothing is spoken and nothing ducked: no narration, captions or ducking files'])
    return check, dict(tracks=[('Performance sound', out/'music.wav', 1., [], True)], markers=[])


# ------------------------------------------------------------------------------------------------------- README

FILES = {
    'narration.wav': 'the voice alone (24-bit)',
    'music.wav': {'tour': 'the original recording, never ducked: what the take played of it (32-bit float)',
                  'tutorial': "the app's own synth sound, captured live during the take (32-bit float)",
                  'performance': "the take's own sound, as the MP4 has it (after the performance recorder's loudness pass; 32-bit float)"},
    'music-as-played.wav': 'the music as Director mode played it, never ducked: the stem solos at their soloGain, and the\n'
                           'crossfades (32-bit float)',
    'ducking.csv': 'one row per spoken line, read off the take (below)',
    'ducking-envelope.csv': 'the music gain at every video frame, linear and in dB, as the take applied it',
    'captions.srt': 'the spoken words, at most two 42-character lines a caption (UTF-8 with BOM, CRLF)',
    'timeline.xml': 'FCP7 XML: File > Import in Premiere Pro or DaVinci Resolve (Final Cut Pro 7 opens it too;\n'
                    'Final Cut Pro X/11 needs it converted to FCPXML first, e.g. with SendToX)',
    'check.json': 'how the parts were lined up with the video, the levels the take used, how well they rebuild its\n'
                  'sound, and whether they match the take at all',
    'README.txt': 'this file',
}


def readme(check, video, seconds, frames, fps, names):
    kind = check['kind']
    out = [f'Editor package for {video}', '='*(len(video)+21), '']
    if check['problems']:
        out += ['WARNING: these parts do not match the take. Check before use:'] + [f'  - {p}' for p in check['problems']] + ['']
    out += [f"Everything here starts at the video's first frame and runs for its whole length ({seconds:.2f} s, {frames} frames at {fps:g} fps),",
            'so the files line up when dropped at 00:00:00:00. Audio is 48 kHz.', '']
    width = max(len(n) for n in names)
    out += [f'The take, {video}, is the picture and its mixed sound. Beside it:']
    for name in names:
        text = FILES.get(name, '')
        text = (text[kind] if isinstance(text, dict) else text).split('\n')
        out += [f'  {name:<{width}}  {text[0]}']+[f"  {'':<{width}}  {more}" for more in text[1:]]
    out += ['', 'timeline.xml, as imported: V1 the video;'] + [f'  {line}' for line in check.get('layout', [])]
    out += ['Each stereo file comes in as a linked pair of tracks (left, right). Paths are absolute: move the folder and the editor',
            'asks you to relink.', '']
    if 'ducking.csv' in names:
        out += ['ducking.csv: times are seconds from the video\'s first frame; music_gain is linear amplitude (1 = unducked) and also',
                'in dB; cue is the story\'s 0-based cue index, line counts the narration\'s lines from 1. The music starts to dip',
                '0.12 s before a line and comes back 0.15 s after it (at the line\'s end once a cue that releases its solo after',
                'the narration is over), a frame or two later in the take than that, as the app applied it; duck_reached and',
                'release_done are where the music has made 95% of the move. A time is blank where the take never gets there',
                '(the next line dips first, or the take ends).', '']
    if kind == 'tour' and 'reproductionSnrDb' in check:
        if 'narrationLevel' in check:
            out.append(f"In this take the music played at {check['musicLevel']:.3f} ({20*math.log10(max(check['musicLevel'], 1e-6)):+.1f} dB) "
                       f"and the narration at {check['narrationLevel']:.3f}, {check['narrationRelativeToMusic']:.3f} of the music "
                       f"({check['narrationRelativeDb']:+.1f} dB);")
        else:
            out.append(f"In this take the music played at {check['musicLevel']:.3f} ({20*math.log10(max(check['musicLevel'], 1e-6)):+.1f} dB), with no narration;")
        out.append(f"the parts rebuild its sound to {check['reproductionSnrDb']:.1f} dB SNR: the take's mix is music x ducking x that level"
                   + (' + narration x its level.' if 'narrationLevel' in check else '.'))
    elif kind == 'tutorial' and 'narrationLevel' in check:
        out.append(f"In this take the narration played at {check['narrationLevel']:.3f} ({check['narrationLevelDb']:+.1f} dB: Unity plays the mono clips at -3 dB)"
                   + (f" and the app's sound at {check['appSoundLevel']:.3f}" if 'appSoundLevel' in check else '')
                   + f"; the parts rebuild its sound to {check['reproductionSnrDb']:.1f} dB SNR. The tutorial does not duck.")
    elif kind == 'performance':
        out.append(f"The take's sound peaks at {check['peakDbfs']:+.1f} dBFS ({check['rmsDbfs']:+.1f} dBFS RMS); the MP4 carries it at {check['mp4AudioRate']} Hz.")
    if check.get('timelineLevels', '').startswith('nominal'):
        out.append("The timeline uses the app's nominal balance, not the take's: the fit above is not trusted.")
    elif kind != 'performance':
        out.append("The timeline plays the take's own balance, so it sounds as loud as the MP4.")
    for note in check.get('notes', []):
        out.append(f'Note: {note}.')
    return '\n'.join(out)+'\n'


# -------------------------------------------------------------------------------------------------------- build

def matched(folder):
    """An existing package that matched its take: a failed rebuild must not replace it."""
    try:
        old = json.loads((folder/'check.json').read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return False
    return old.get('matchesTake', True) and not old.get('warning') and (old.get('reproductionSnrDb') is None or old['reproductionSnrDb'] >= MIN_SNR_DB)


def install(work, out, ok, finish):
    """Move the finished package into place (never a half one): over an earlier package unless this one fails the
    checks and the earlier one passed them, in which case it goes beside it as <take>-edit-failed. finish(target)
    first writes what names the folder's final place (the timeline's absolute paths)."""
    target = out if ok or not out.exists() or not matched(out) else out.with_name(out.name+'-failed')
    finish(target)
    old = None
    if target.exists():
        old = target.with_name(f'.{target.name}.old-{os.getpid()}')
        try:
            os.rename(target, old)
        except OSError:                       # an editor still has a file open in it: the new package goes beside it
            old, target = None, target.with_name(target.name+'-'+work.name.rsplit('.', 1)[-1])
            finish(target)
    try:
        os.rename(work, target)
    except OSError:
        if old is not None:
            os.rename(old, target)
        raise
    if old is not None:
        shutil.rmtree(old, ignore_errors=True)
    return target


def build(source):
    source = Path(source).resolve()
    if source.suffix.lower() == '.json':
        report = json.loads(source.read_text(encoding='utf-8'))
        take = source.with_name(report.get('video') or source.with_suffix('.mp4').name)
    else:
        report, take = dict(kind='performance'), source
    if not take.exists():
        raise ValueError(f'No video {take}')
    kind = report.get('kind', 'performance')
    if kind == 'tour':
        identity(report, find_bundle(report))          # a refused take costs nothing and touches nothing
    info = video_info(take)
    frames = info['frames']
    length = int(round(frames*RATE/info['fps']))
    decoded = []

    def sound():
        if not decoded:
            decoded.append(take_audio(take, length) if info['audio'] else (None, False))
        return decoded[0]
    out = take.with_name(take.stem+'-edit')
    work = Path(tempfile.mkdtemp(prefix=f'.{out.name}.', dir=take.parent))
    try:
        handler = dict(tour=tour, tutorial=tutorial).get(kind, performance)
        check, timeline = handler(report, take, work, sound, info, length, frames)
        silent = sound()[1]
        decoded.clear()
        generic = []
        if not info['audio'] and kind != 'performance':
            generic.append("the MP4 has no sound: the parts are placed from the report's times and nothing could be checked")
        elif silent:
            generic.append("the MP4 is silent (did the song start?): the parts are placed from the report's times and nothing could be checked")
        if report.get('error'):
            generic.append(f"the take reported an error: {report['error']}")
        check['problems'] = generic+check['problems']
        check.update(video=take.name, seconds=round(length/RATE, 3), frames=frames, fps=info['fps'], matchesTake=not check['problems'])
        names = sorted(p.name for p in work.iterdir())+['timeline.xml', 'check.json', 'README.txt']
        names = sorted(set(names), key=names.index)

        def finish(target):
            check['layout'] = write_timeline(work/'timeline.xml', take.stem, take, info, timeline['tracks'], frames, timeline['markers'],
                                             lambda file: target/Path(file).name if Path(file).parent == work else file)
            (work/'check.json').write_text(json.dumps(check, indent=1, ensure_ascii=False), encoding='utf-8')
            (work/'README.txt').write_text(readme(check, take.name, length/RATE, frames, info['fps'], names), encoding='utf-8-sig')
        folder = install(work, out, check['matchesTake'], finish)
    except BaseException:
        shutil.rmtree(work, ignore_errors=True)
        raise
    return folder, check


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    p.add_argument('take', help='the take report (<take>.json beside the video) or, for a performance, the .mp4')
    a = p.parse_args()
    try:
        folder, result = build(a.take)
    except Refused as refusal:
        print(json.dumps(dict(refused=str(refusal), take=str(a.take)), indent=1))
        sys.exit(2)
    print(json.dumps(dict(result, folder=str(folder)), indent=1))
    sys.exit(0 if result['matchesTake'] else 2)
