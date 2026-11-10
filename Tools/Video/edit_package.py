"""An editor's package for a Resonance video take: the parts of its mix, so an external editor can re-edit it.

    Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/edit_package.py Recordings/Videos/<take>.json
    Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/edit_package.py Recordings/Videos/<performance>.mp4

The Recorder's MP4 is a finished mix. Beside it this writes <take>-edit/ with everything that went into its sound:

  narration.wav          the voice alone (a tour's narration track, or the tutorial's step clips)
  music.wav              the original music, never ducked: the song's recording for a tour, the app's own synth sound
                         for the tutorial (captured live during the take) or a performance
  music-as-played.wav    tours whose cues solo a stem: the music as Director mode played it (solos and their
                         crossfades), still never ducked
  ducking.csv            one row per spoken line: when the music starts to dip, the level it dips to, when it starts
                         to come back and when it is back
  ducking-envelope.csv   the music gain at every video frame, exactly as the take applied it
  captions.srt           the spoken words in caption-sized lines
  timeline.xml           an FCP7 XML sequence (Premiere Pro, DaVinci Resolve, Final Cut Pro 7): the video, the music
                         with the ducking as Audio Levels keyframes, the narration at its mix level, and the MP4's own
                         mix on a muted track for reference
  check.json             how the parts were lined up with the video (cross-correlation against the MP4's own audio),
                         the music and narration levels the take used, how closely the parts reproduce its audio,
                         and per spoken line the music level measured under the voice against the cue sheet
  README.txt

Every WAV is 48 kHz, starts at the video's first frame and lasts as long as the video, so the files line up when
they are dropped at 00:00 on any timeline. The mix in the MP4 is music x ducking + narration x a level measured from
the take: the app asks for 0.95 of the music's volume, and Unity plays the mono voice into both stereo channels at
-3 dB each (constant-power panning), so a tour's narration sits near 0.67 of the music.
"""
import argparse
import csv
import json
import math
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from fractions import Fraction
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy.signal import resample_poly

ROOT = Path(__file__).resolve().parents[2]
LIBRARY = ROOT/'PreparedSongs/Library'
TUTORIAL = ROOT/'Assets/StreamingAssets/Tutorial'
RATE = 48000
VOICE_GAIN = .95                 # SongNarration: voice.volume = master * 0.95
LEAD, TAIL = .12, .15            # SongNarration: the music dips from 0.12 s before a line until 0.15 s after it
ATTACK, RELEASE, RELEASE_SOLO = 16., 4., 50.   # per-second rates of its exponential approach
CROSSFADE = .45                  # StemPlayback's editorial stem crossfade
CAPTION_CHARS, CAPTION_LINES, CAPTION_SECONDS = 42, 2, 6.


# ------------------------------------------------------------------------------------------------- audio helpers

def ffmpeg_audio(video, rate=RATE):
    """The video's own audio as float32 (samples, 2), or None when it has none."""
    with tempfile.TemporaryDirectory(prefix='resonance-edit-') as folder:
        wav = Path(folder)/'mix.wav'
        result = subprocess.run(['ffmpeg', '-v', 'error', '-y', '-i', str(video), '-vn', '-ac', '2', '-ar', str(rate),
                                 '-c:a', 'pcm_f32le', str(wav)], capture_output=True)
        if result.returncode != 0 or not wav.exists():
            return None
        signal, _ = sf.read(wav, dtype='float32', always_2d=True)
    return signal


def video_info(video):
    out = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries',
                          'stream=width,height,r_frame_rate:format=duration', '-of', 'json', str(video)],
                         capture_output=True, text=True, check=True)
    data = json.loads(out.stdout)
    stream = data['streams'][0]
    return dict(width=int(stream['width']), height=int(stream['height']), fps=float(Fraction(stream['r_frame_rate'])),
                duration=float(data['format']['duration']))


def load(path, rate=RATE, channels=2):
    signal, source = sf.read(path, dtype='float32', always_2d=True)
    if source != rate:
        g = math.gcd(int(source), rate)
        signal = resample_poly(signal, rate//g, int(source)//g, axis=0).astype('float32')
    if signal.shape[1] != channels:
        signal = np.repeat(signal.mean(axis=1, keepdims=True), channels, axis=1) if channels > 1 else signal.mean(axis=1, keepdims=True)
    return signal


def place(signal, at_seconds, length, rate=RATE):
    """`signal` on a timeline of `length` samples, starting at `at_seconds` (cropped at either end)."""
    out = np.zeros((length, signal.shape[1]), dtype='float32')
    start = int(round(at_seconds*rate))
    a, b = max(0, start), min(length, start+len(signal))
    if b > a:
        out[a:b] = signal[a-start:b-start]
    return out


def mono(signal):
    return signal.mean(axis=1) if signal.ndim > 1 else signal


def best_lag(reference, candidate, estimate, search, rate=RATE, work=8000):
    """Seconds to delay `candidate` (placed at time 0) so it lines up best with `reference`, searched within
    `search` seconds of `estimate`; and the normalised correlation there. Both are compared at 8 kHz."""
    g = math.gcd(rate, work)
    ref = resample_poly(mono(reference), work//g, rate//g)
    can = resample_poly(mono(candidate), work//g, rate//g)
    if not len(can) or not np.any(can):
        return estimate, 0.
    n = 1 << int(math.ceil(math.log2(len(ref)+len(can))))
    corr = np.fft.irfft(np.fft.rfft(ref, n)*np.conj(np.fft.rfft(can, n)), n)
    lags = np.arange(n); lags[lags > n//2] -= n          # corr[k] = sum ref[t+k] * can[t]
    lo, hi = int((estimate-search)*work), int((estimate+search)*work)
    window = (lags >= lo) & (lags <= hi)
    if not window.any():
        return estimate, 0.
    k = int(np.argmax(np.where(window, corr, -np.inf)))
    norm = float(np.sqrt(np.sum(ref*ref)*np.sum(can*can))) or 1.
    return lags[k]/work, float(corr[k]/norm)


# --------------------------------------------------------------------------------------------- ducking (tours)

def envelope(segments, cues, frames, fps, audio_start):
    """Music gain at every video frame, as SongNarration applies it in Update: the target is the lowest duck of
    the lines whose window [start - 0.12, end + 0.15) holds the song time, approached exponentially (16/s down,
    4/s back up, 50/s back up once a releaseSoloAfterNarration cue's line is over)."""
    gain = np.ones(frames)
    g, dt = 1., 1/fps
    ends = {}
    for s in segments:
        ends[s['cue']] = max(ends.get(s['cue'], 0.), s['end'])
    for k in range(frames):
        song = k/fps - audio_start
        target, released = 1., False
        if song >= 0:
            cue = next((i for i, c in enumerate(cues) if c['start'] <= song < c['end']), -1)
            released = cue >= 0 and cues[cue].get('releaseSoloAfterNarration') is True and song >= ends.get(cue, 1e9)
            if not released:
                for s in segments:
                    if s['start']-LEAD <= song < s['end']+TAIL:
                        target = min(target, s.get('duck') or .42)
        rate = ATTACK if target < 1 else (RELEASE_SOLO if released else RELEASE)
        g += (target-g)*(1-math.exp(-dt*rate))
        gain[k] = g
    return gain


def duck_rows(segments, cues, audio_start):
    rows = []
    for i, s in enumerate(segments):
        duck = s.get('duck') or .42
        a0, r0 = audio_start+s['start']-LEAD, audio_start+s['end']+TAIL
        rows.append(dict(line=i+1, cue=s['cue'], voice_start=round(audio_start+s['start'], 3), voice_end=round(audio_start+s['end'], 3),
                         duck_starts=round(a0, 3), duck_reached=round(a0+3/ATTACK, 3), release_starts=round(r0, 3),
                         release_done=round(r0+3/RELEASE, 3), music_gain=round(duck, 4), music_gain_db=round(20*math.log10(duck), 2),
                         text=(cues[s['cue']].get('speech') or cues[s['cue']]['text']) if s['cue'] < len(cues) else ''))
    return rows


def music_as_played(bundle, manifest, cues, segments, length_seconds):
    """The recording with the tour's stem solos (and their crossfades), unducked, in song time, 48 kHz stereo:
    narration.preview_mix's music path."""
    music = load(bundle/manifest.get('audioPath', 'recording.wav'))
    stems = {s['id']: s for s in manifest.get('stems', [])}
    soloed = False
    cache = {}
    for i, c in enumerate(cues):
        if not c.get('stem') or c['stem'] not in stems:
            continue
        soloed = True
        solo = cache.setdefault(c['stem'], load(bundle/stems[c['stem']]['audioPath']))
        a = int(c['start']*RATE)
        end = c['end']
        if c.get('soloUntil', 0) > 0:
            end = min(end, c['soloUntil'])
        if c.get('releaseSoloAfterNarration'):
            end = min(end, max([s['end'] for s in segments if s['cue'] == i], default=end))
        b = min(len(music), int(end*RATE))
        if b <= a:
            continue
        isolated = solo[a:b]*c.get('soloGain', 1)
        count = b-a
        fade = min(count//2, max(1, int(CROSSFADE*RATE)))
        blend = np.ones((count, 1), dtype='float32')
        previous = cues[i-1] if i > 0 else None
        following = cues[i+1] if i+1 < len(cues) else None
        from_previous = previous is not None and previous.get('stem') == c['stem'] and not previous.get('releaseSoloAfterNarration') and not previous.get('soloUntil', 0) and abs(previous['end']-c['start']) < 1e-6
        to_following = following is not None and following.get('stem') == c['stem'] and not c.get('releaseSoloAfterNarration') and not c.get('soloUntil', 0) and abs(c['end']-following['start']) < 1e-6
        if not from_previous:
            blend[:fade, 0] = np.linspace(0, 1, fade, dtype='float32')
        music[a:b] = music[a:b]*(1-blend)+isolated*blend
        if not to_following and b < len(music):
            tail = min(fade, len(music)-b)
            out = np.linspace(1, 0, tail, dtype='float32')[:, None]
            music[b:b+tail] = music[b:b+tail]*(1-out)+solo[b:b+tail]*c.get('soloGain', 1)*out
    return music, soloed


# ------------------------------------------------------------------------------------------------------ captions

def stamp(seconds):
    ms = int(round(max(0., seconds)*1000))
    return f'{ms//3600000:02d}:{ms//60000 % 60:02d}:{ms//1000 % 60:02d},{ms % 1000:03d}'


def caption_blocks(words, starts, end):
    """Words grouped into captions of at most two 42-character lines and 6 s, breaking after punctuation where
    it can; each caption runs until the next starts (or the line ends)."""
    blocks, current = [], []
    def flush():
        if current:
            blocks.append(list(current)); current.clear()
    for i, w in enumerate(words):
        candidate = ' '.join(x for x, _ in current+[(w, 0)])
        too_long = len(candidate) > CAPTION_CHARS*CAPTION_LINES or (current and starts[i]-current[0][1] > CAPTION_SECONDS)
        if too_long:
            flush()
        current.append((w, starts[i]))
        if re.search(r'[.!?;:]["”’)]*$', w) and len(' '.join(x for x, _ in current)) > CAPTION_CHARS*.6:
            flush()
    flush()
    out = []
    for j, block in enumerate(blocks):
        text = ' '.join(w for w, _ in block)
        lines = wrap(text)
        start = block[0][1]
        finish = blocks[j+1][0][1]-.04 if j+1 < len(blocks) else end
        out.append((start, max(start+.5, finish), '\n'.join(lines)))
    return out


def wrap(text):
    if len(text) <= CAPTION_CHARS:
        return [text]
    words = text.split()
    best, best_cost = [text], 1e9
    for k in range(1, len(words)):
        a, b = ' '.join(words[:k]), ' '.join(words[k:])
        cost = max(len(a), len(b)) + (0 if a.endswith((',', '.', ';', ':', '!', '?')) else 3)
        if len(a) <= CAPTION_CHARS and len(b) <= CAPTION_CHARS and cost < best_cost:
            best, best_cost = [a, b], cost
    return best


def word_times(text, start, end, word_starts=None):
    """Each word's start: the forced-alignment times when there is one per word (relative to the line's start, as
    story_timing.py and tutorial_timing.py write them), else spread over the line by characters."""
    words = text.split()
    if word_starts and len(word_starts) == len(words):
        return words, [start+t for t in word_starts]
    lengths = np.array([len(w)+1 for w in words], dtype=float)
    offsets = np.concatenate([[0.], np.cumsum(lengths)[:-1]])/max(1., lengths.sum())
    return words, [start+o*(end-start) for o in offsets]


def write_srt(path, captions):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        for i, (a, b, text) in enumerate(captions, 1):
            f.write(f'{i}\n{stamp(a)} --> {stamp(b)}\n{text}\n\n')


# --------------------------------------------------------------------------------------------- FCP7 XML timeline

def pathurl(path):
    p = Path(path).resolve().as_posix()
    return 'file://localhost/' + re.sub(r'[ #%?]', lambda m: '%{:02X}'.format(ord(m.group(0))), p.lstrip('/'))


def rate_xml(parent, fps):
    r = ET.SubElement(parent, 'rate')
    ET.SubElement(r, 'timebase').text = str(int(round(fps)))
    ET.SubElement(r, 'ntsc').text = 'FALSE'


def write_timeline(path, name, video, info, tracks, frames):
    """tracks: [(label, file, level or None, keyframes [(frame, gain)] or [], enabled)]"""
    fps = info['fps']
    root = ET.Element('xmeml', version='4')
    seq = ET.SubElement(root, 'sequence', id='sequence-1')
    ET.SubElement(seq, 'name').text = name
    ET.SubElement(seq, 'duration').text = str(frames)
    rate_xml(seq, fps)
    media = ET.SubElement(seq, 'media')
    vid = ET.SubElement(media, 'video')
    fmt = ET.SubElement(ET.SubElement(vid, 'format'), 'samplecharacteristics')
    rate_xml(fmt, fps)
    ET.SubElement(fmt, 'width').text = str(info['width']); ET.SubElement(fmt, 'height').text = str(info['height'])
    ET.SubElement(fmt, 'pixelaspectratio').text = 'square'
    vtrack = ET.SubElement(vid, 'track')
    files = {}

    def file_element(parent, file, kind):
        key = str(file)
        if key in files:
            ET.SubElement(parent, 'file', id=files[key]); return
        fid = f'file-{len(files)+1}'; files[key] = fid
        f = ET.SubElement(parent, 'file', id=fid)
        ET.SubElement(f, 'name').text = Path(file).name
        ET.SubElement(f, 'pathurl').text = pathurl(file)
        rate_xml(f, fps)
        ET.SubElement(f, 'duration').text = str(frames)
        m = ET.SubElement(f, 'media')
        if kind == 'video':
            sc = ET.SubElement(ET.SubElement(m, 'video'), 'samplecharacteristics')
            rate_xml(sc, fps)
            ET.SubElement(sc, 'width').text = str(info['width']); ET.SubElement(sc, 'height').text = str(info['height'])
        a = ET.SubElement(m, 'audio')
        sc = ET.SubElement(a, 'samplecharacteristics')
        ET.SubElement(sc, 'depth').text = '16'; ET.SubElement(sc, 'samplerate').text = str(RATE)
        ET.SubElement(a, 'channelcount').text = '2'

    def clip(track, cid, label, file, kind, level=None, keys=()):
        c = ET.SubElement(track, 'clipitem', id=cid)
        ET.SubElement(c, 'name').text = label
        ET.SubElement(c, 'enabled').text = 'TRUE'
        ET.SubElement(c, 'duration').text = str(frames)
        rate_xml(c, fps)
        for tag, value in (('start', 0), ('end', frames), ('in', 0), ('out', frames)):
            ET.SubElement(c, tag).text = str(value)
        file_element(c, file, kind)
        if kind == 'audio':
            st = ET.SubElement(c, 'sourcetrack')
            ET.SubElement(st, 'mediatype').text = 'audio'; ET.SubElement(st, 'trackindex').text = '1'
        if level is not None or keys:
            effect = ET.SubElement(ET.SubElement(c, 'filter'), 'effect')
            for tag, value in (('name', 'Audio Levels'), ('effectid', 'audiolevels'), ('effectcategory', 'audiolevels'),
                               ('effecttype', 'audiolevels'), ('mediatype', 'audio')):
                ET.SubElement(effect, tag).text = value
            p = ET.SubElement(effect, 'parameter', authoringApp='PremierePro')
            ET.SubElement(p, 'parameterid').text = 'level'; ET.SubElement(p, 'name').text = 'Level'
            ET.SubElement(p, 'valuemin').text = '0'; ET.SubElement(p, 'valuemax').text = '3.98109'
            ET.SubElement(p, 'value').text = f'{(level if level is not None else 1):.5f}'
            for frame, gain in keys:
                kf = ET.SubElement(p, 'keyframe')
                ET.SubElement(kf, 'when').text = str(int(frame)); ET.SubElement(kf, 'value').text = f'{gain:.5f}'
        return c

    clip(vtrack, 'clipitem-video', Path(video).name, video, 'video')
    aud = ET.SubElement(media, 'audio')
    ET.SubElement(aud, 'numOutputChannels').text = '2'
    afmt = ET.SubElement(ET.SubElement(aud, 'format'), 'samplecharacteristics')
    ET.SubElement(afmt, 'depth').text = '16'; ET.SubElement(afmt, 'samplerate').text = str(RATE)
    for n, (label, file, level, keys, enabled) in enumerate(tracks, 1):
        track = ET.SubElement(aud, 'track')
        clip(track, f'clipitem-audio-{n}', label, file, 'audio', level, keys)
        ET.SubElement(track, 'enabled').text = 'TRUE' if enabled else 'FALSE'
    ET.indent(root)
    with open(path, 'wb') as f:
        f.write(b'<?xml version="1.0" encoding="UTF-8"?>\n<!DOCTYPE xmeml>\n')
        f.write(ET.tostring(root, encoding='utf-8'))


def keyframes(gain, tolerance=.004):
    """Few keyframes that follow the per-frame gain within `tolerance` (linear interpolation between them)."""
    keys = [0]
    i = 0
    n = len(gain)
    while i < n-1:
        j = i+1
        while j+1 < n:
            a, b = gain[i], gain[j+1]
            span = np.arange(i, j+2)
            line = a+(b-a)*(span-i)/(j+1-i)
            if np.max(np.abs(line-gain[i:j+2])) > tolerance:
                break
            j += 1
        keys.append(j)
        i = j
    return [(k, float(gain[k])) for k in keys]


# -------------------------------------------------------------------------------------------------------- kinds

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


def tour(report, take, out, mix, info, length, frames):
    bundle = find_bundle(report)
    story = json.loads((bundle/'story.json').read_text(encoding='utf-8'))
    narration = json.loads((bundle/'narration.json').read_text(encoding='utf-8'))
    manifest = json.loads((bundle/'aligned.mid.prepared.json').read_text(encoding='utf-8'))
    cues, segments = story['cues'], narration['segments']
    voice = load(bundle/narration['audioPath'])
    original = load(bundle/manifest.get('audioPath', 'recording.wav'))
    played, soloed = music_as_played(bundle, manifest, cues, segments, length/RATE)
    estimate = report.get('audioStart', -1)
    if estimate is None or estimate < 0:
        estimate = report['markers'][0]['start'] if report.get('markers') else 0.
    check = dict(kind='tour', bundle=bundle.name, estimate=round(estimate, 4))
    audio_start = estimate
    if mix is not None:
        lag, score = best_lag(mix, played, estimate, .75)
        check.update(alignedBy='cross-correlation of the music with the MP4 audio', correlation=round(score, 4), audioStart=round(lag, 4))
        audio_start = lag
    gain = envelope(segments, cues, frames, info['fps'], audio_start)
    music_track = place(original, audio_start, length)
    played_track = place(played, audio_start, length)
    voice_track = place(voice, audio_start, length)
    t = np.arange(length)/RATE
    per_sample = np.interp(t, np.arange(frames)/info['fps'], gain).astype('float32')[:, None]
    relative = VOICE_GAIN
    if mix is not None:
        # The take's own balance: mix = music_level x music x ducking + narration_level x narration, fitted by least
        # squares over the whole take (the nominal 0.95 is what the app asks for; the take is what it got).
        ducked = played_track*per_sample
        mm, mv, vv = float(np.sum(ducked*ducked)), float(np.sum(ducked*voice_track)), float(np.sum(voice_track*voice_track))
        mx, vx = float(np.sum(ducked*mix)), float(np.sum(voice_track*mix))
        det = mm*vv-mv*mv
        music_level, voice_level = ((mx*vv-vx*mv)/det, (vx*mm-mx*mv)/det) if abs(det) > 1e-12 else (mx/max(mm, 1e-12), 0.)
        residual = mix-music_level*ducked-voice_level*voice_track
        relative = voice_level/music_level if music_level > 1e-6 else VOICE_GAIN
        check.update(musicLevel=round(music_level, 4), narrationLevel=round(voice_level, 4), narrationRelativeToMusic=round(relative, 4),
                     narrationRelativeDb=round(20*math.log10(max(relative, 1e-6)), 2),
                     reproductionSnrDb=round(10*math.log10(float(np.sum(mix**2))/max(1e-12, float(np.sum(residual**2)))), 2))
        # Each spoken line: the music level actually heard under the voice, against the cue sheet's level.
        lines = []
        for i, s in enumerate(segments):
            a, b = int((audio_start+s['start']-LEAD+3/ATTACK+.1)*RATE), int((audio_start+s['end']+TAIL-.1)*RATE)
            if b-a < RATE//2:
                continue
            m, v, x = played_track[a:b].ravel(), voice_track[a:b].ravel(), mix[a:b].ravel()
            sol, *_ = np.linalg.lstsq(np.c_[m, v], x, rcond=None)
            lines.append(dict(line=i+1, cueSheet=round(s.get('duck') or .42, 4), measured=round(float(sol[0])/music_level, 4) if music_level > 1e-6 else None))
        check['duckingMeasured'] = lines
    sf.write(out/'narration.wav', voice_track, RATE, subtype='PCM_24')
    sf.write(out/'music.wav', music_track, RATE, subtype='PCM_24')
    files = dict(narration='narration.wav', music='music.wav')
    if soloed:
        sf.write(out/'music-as-played.wav', played_track, RATE, subtype='PCM_24')
        files['musicAsPlayed'] = 'music-as-played.wav'
    rows = duck_rows(segments, cues, audio_start)
    with open(out/'ducking.csv', 'w', encoding='utf-8', newline='') as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()) if rows else ['line'])
        w.writeheader(); w.writerows(rows)
    with open(out/'ducking-envelope.csv', 'w', encoding='utf-8', newline='') as f:
        w = csv.writer(f); w.writerow(['frame', 'seconds', 'music_gain', 'music_gain_db'])
        for k, g in enumerate(gain):
            w.writerow([k, f'{k/info["fps"]:.4f}', f'{g:.5f}', f'{20*math.log10(max(g, 1e-6)):.2f}'])
    captions = []
    for s in segments:
        cue = cues[s['cue']]
        text = re.sub(r'^\s*\[[^\]]*\]\s*', '', cue.get('speech') or cue['text'])
        words, starts = word_times(text, s['start'], s['end'], s.get('wordStarts'))
        captions += [(a+audio_start, b+audio_start, x) for a, b, x in caption_blocks(words, starts, s['end'])]
    write_srt(out/'captions.srt', captions)
    music_file = out/(files.get('musicAsPlayed') or files['music'])
    tracks = [('MP4 mix (reference)', take, None, [], False),
              ('Music (ducked)' if not soloed else 'Music as played (ducked)', music_file, None, keyframes(gain), True),
              ('Narration', out/'narration.wav', min(relative, 3.98), [], True)]
    if soloed:
        tracks.append(('Original recording (muted)', out/'music.wav', None, keyframes(gain), False))
    write_timeline(out/'timeline.xml', Path(take).stem, take, info, tracks, frames)
    check.update(files=dict(files, ducking='ducking.csv', envelope='ducking-envelope.csv', captions='captions.srt', timeline='timeline.xml'),
                 lines=len(rows), captions=len(captions), lowestGain=round(float(gain.min()), 4))
    return check


def tutorial(report, take, out, mix, info, length, frames):
    manifest = json.loads((TUTORIAL/'manifest.json').read_text(encoding='utf-8'))
    script = json.loads((TUTORIAL/'script.json').read_text(encoding='utf-8'))
    texts = {s['id']: s.get('text', '') for s in script.get('steps', [])}
    clips = {c['id']: c for c in manifest['clips']}
    voice_track = np.zeros((length, 2), dtype='float32')
    captions, placed = [], []
    for m in report.get('markers', []):
        c = clips.get(m['id'])
        if not c or not (TUTORIAL/c['file']).exists():
            continue
        clip = load(TUTORIAL/c['file'])
        at = m.get('voiceStart', m['start'])
        if mix is not None:
            a, b = int(max(0, at-1)*RATE), int(min(length, at+len(clip)/RATE+1)*RATE)
            lag, score = best_lag(mix[a:b], clip, at-a/RATE, .4)
            if score > .2:
                at = a/RATE+lag
        voice_track += place(clip, at, length)
        placed.append(dict(step=m['id'], voiceStart=round(at, 4)))
        words, starts = word_times(texts.get(m['id'], ''), 0., len(clip)/RATE, c.get('starts'))
        if words:
            captions += [(a+at, b+at, x) for a, b, x in caption_blocks(words, starts, len(clip)/RATE)]
    sf.write(out/'narration.wav', voice_track, RATE, subtype='PCM_24')
    files = dict(narration='narration.wav')
    synth = Path(take).with_suffix('.synth.wav')
    check = dict(kind='tutorial', steps=placed)
    if synth.exists():
        music = place(load(synth), report.get('synthStart', 0.), length)
        if mix is not None:
            lag, score = best_lag(mix, music, 0., .3)
            music = place(load(synth), report.get('synthStart', 0.)+lag, length)
            check.update(synthCorrelation=round(score, 4), synthShift=round(lag, 4))
        sf.write(out/'music.wav', music, RATE, subtype='PCM_24'); files['music'] = 'music.wav'
    else:
        check['music'] = 'no synth capture in this take (recorded before the stem tap existed)'
    with open(out/'ducking.csv', 'w', encoding='utf-8', newline='') as f:
        csv.writer(f).writerow(['line', 'cue', 'voice_start', 'voice_end', 'duck_starts', 'duck_reached', 'release_starts', 'release_done', 'music_gain', 'music_gain_db', 'text'])
    write_srt(out/'captions.srt', captions)
    tracks = [('MP4 mix (reference)', take, None, [], False), ('Narration', out/'narration.wav', 1., [], True)]
    if 'music' in files:
        tracks.insert(1, ('App sound', out/'music.wav', 1., [], True))
    write_timeline(out/'timeline.xml', Path(take).stem, take, info, tracks, frames)
    check.update(files=dict(files, ducking='ducking.csv (the tutorial does not duck)', captions='captions.srt', timeline='timeline.xml'), captions=len(captions))
    return check


def performance(take, out, mix, info, length, frames):
    if mix is None:
        raise ValueError(f'{take} has no audio')
    sf.write(out/'music.wav', place(mix, 0., length), RATE, subtype='PCM_24')
    with open(out/'ducking.csv', 'w', encoding='utf-8', newline='') as f:
        csv.writer(f).writerow(['line', 'cue', 'voice_start', 'voice_end', 'duck_starts', 'duck_reached', 'release_starts', 'release_done', 'music_gain', 'music_gain_db', 'text'])
    write_srt(out/'captions.srt', [])
    write_timeline(out/'timeline.xml', Path(take).stem, take, info, [('Performance sound', out/'music.wav', 1., [], True)], frames)
    return dict(kind='performance', files=dict(music='music.wav (the take\'s own sound: no narration, nothing ducked)',
                                               captions='captions.srt (empty: nothing is spoken)', timeline='timeline.xml'))


README = """Editor package for {video}
==========================================

Everything here starts at the video's first frame and runs for its whole length ({seconds:.2f} s, {frames} frames at {fps:g} fps),
so the files line up when dropped at 00:00:00:00. Audio is 48 kHz, 24-bit.

  {video}              the finished take (picture and its mixed sound)
  narration.wav        the voice alone
  music.wav            the original music, never ducked
  music-as-played.wav  (tours that solo a stem) the music with Director mode's solos, never ducked
  ducking.csv          one row per spoken line: the music starts to dip at duck_starts, reaches music_gain by
                       duck_reached, starts back at release_starts and is back to full by release_done
  ducking-envelope.csv the exact music gain at every video frame
  captions.srt         the spoken words, in caption-sized lines
  timeline.xml         FCP7 XML: File > Import in Premiere Pro or DaVinci Resolve. Video on V1; the MP4's own mix
                       on A1 (muted, for reference); the music on A2 with the ducking as Audio Levels keyframes;
                       the narration on A3 at the level it had in the take, relative to the music
  check.json           how the parts were lined up with the video, the levels the take used and how well the
                       parts rebuild its sound (and, per spoken line, the music level measured under the voice)

{balance}
"""


def build(source):
    source = Path(source).resolve()
    if source.suffix.lower() == '.json':
        report = json.loads(source.read_text(encoding='utf-8'))
        take = source.with_name(report.get('video') or source.with_suffix('.mp4').name)
    else:
        report, take = dict(kind='performance'), source
    if not take.exists():
        raise ValueError(f'No video {take}')
    info = video_info(take)
    frames = int(round(info['duration']*info['fps']))
    length = int(round(frames/info['fps']*RATE))
    mix = ffmpeg_audio(take)
    if mix is not None:
        mix = place(mix, 0., length)
    out = take.with_name(take.stem+'-edit')
    out.mkdir(exist_ok=True)
    kind = report.get('kind', 'performance')
    if kind == 'tour':
        check = tour(report, take, out, mix, info, length, frames)
    elif kind == 'tutorial':
        check = tutorial(report, take, out, mix, info, length, frames)
    else:
        check = performance(take, out, mix, info, length, frames)
    check.update(video=take.name, seconds=round(length/RATE, 3), frames=frames, fps=info['fps'])
    (out/'check.json').write_text(json.dumps(check, indent=1, ensure_ascii=False), encoding='utf-8')
    if 'narrationRelativeToMusic' in check:
        balance = (f"In this take the narration sat at {check['narrationRelativeToMusic']:.3f} of the music's level "
                   f"({check['narrationRelativeDb']:+.1f} dB); the parts rebuild its sound to {check['reproductionSnrDb']:.1f} dB SNR. "
                   "The take's mix: music x ducking + narration x that level.")
    else:
        balance = "The app mixes music x ducking + narration x 0.95."
    (out/'README.txt').write_text(README.format(video=take.name, seconds=length/RATE, frames=frames, fps=info['fps'], balance=balance), encoding='utf-8')
    return out, check


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    p.add_argument('take', help='the take report (<take>.json beside the video) or, for a performance, the .mp4')
    a = p.parse_args()
    folder, result = build(a.take)
    print(json.dumps(dict(result, folder=str(folder)), indent=1, ensure_ascii=False))
