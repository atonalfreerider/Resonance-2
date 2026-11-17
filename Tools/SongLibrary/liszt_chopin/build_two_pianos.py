"""Build the two-piano comparison of Chopin's Nocturne Op. 27 No. 2 and Liszt's Consolation No. 3.

Liszt's clock is the base. Chopin's passages are laid onto it (one linear time map per scene: a lead-in and a
stretch), each hand of each piece is a separate MuseScore 4 piano render (render_hands.py), and a scene says which of
the four parts sound. The same map writes the MIDI the visuals follow, so a torus shows exactly the hands you hear.

    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/liszt_chopin/build_two_pianos.py
"""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

import mido
import numpy as np
import soundfile as sf

sys.path.insert(0, str(Path(__file__).parent))
from scoreio import load  # noqa: E402

ROOT = Path(__file__).resolve().parents[3]
DATA = ROOT / 'SongLibraryData/scores/liszt-chopin'
OUT = DATA / 'build'
SR = 44100
PARTS = ('L_rh', 'L_lh', 'C_rh', 'C_lh')       # MIDI tracks 1..4: Liszt hands then Chopin hands
GAP = 0.8                                      # silence between chapters (seconds)
TAIL = 0.9                                     # a cut part rings out this long


def scenes_and_chapters():
    """Scenes in Liszt bars (l) and Chopin bars (c). lead: silence before Chopin starts; mask: parts that sound."""
    ALL, MEL_L, MEL_C = 'L_rh L_lh C_rh C_lh', 'L_rh C_lh', 'C_rh L_lh'
    return json.loads((Path(__file__).parent / 'scenes.json').read_text(encoding='utf-8'))


def decode(path):
    with tempfile.TemporaryDirectory() as t:
        wav = Path(t) / 'a.wav'
        subprocess.run(['ffmpeg', '-y', '-v', 'error', '-i', str(path), '-ar', str(SR), '-ac', '2', str(wav)], check=True)
        data, _ = sf.read(wav, dtype='float32')
    return data


def stretch(audio, ratio):
    """ratio = output length / input length; MuseScore's own piano, time-stretched with Rubber Band (no pitch change)."""
    if abs(ratio - 1) < .004:
        return audio
    with tempfile.TemporaryDirectory() as t:
        a, b = Path(t) / 'in.wav', Path(t) / 'out.wav'
        sf.write(a, audio, SR, subtype='FLOAT')
        subprocess.run(['ffmpeg', '-y', '-v', 'error', '-i', str(a), '-af', f'rubberband=tempo={1 / ratio:.6f}:transients=smooth:pitchq=quality', str(b)], check=True)
        out, _ = sf.read(b, dtype='float32')
    return out


def main():
    spec = scenes_and_chapters()
    OUT.mkdir(exist_ok=True)
    pieces = {'L': load(str(DATA / 'liszt-consolation3.mid')), 'C': load(str(DATA / 'chopin-nocturne-op27-2.mid'))}
    stems = {p: decode(DATA / 'hands' / f"{'liszt' if p[0] == 'L' else 'chopin'}-{p[2:]}.mp3") for p in PARTS}
    for p in PARTS:
        print(p, round(len(stems[p]) / SR, 1), 's')

    def bars(piece, a, b):
        d = pieces[piece]
        return d['bars'][a - 1]['sec'], d['bars'][b - 1]['end_sec']

    timeline = []                    # scenes with absolute dst times
    cursor = 0.0
    chapters = []
    for ci, chapter in enumerate(spec['chapters']):
        if ci:
            cursor += GAP
        chapter_start = cursor
        for sc in chapter['scenes']:
            mask = sc['mask'].split()
            l = sc.get('l')
            c = sc.get('c')
            lead = sc.get('lead', 0.0)
            if l:
                l0, l1 = bars('L', *l)
                dur = sc.get('dur', l1 - l0)
            else:
                c0, c1 = bars('C', *c)
                dur = (c1 - c0) * sc.get('ratio', 1.0)
            scene = dict(chapter=ci, name=sc.get('name', ''), mask=mask, dst0=cursor, dst1=cursor + dur, lead=lead,
                         L=dict(src0=l0, src1=l1, ratio=dur / (l1 - l0)) if l else None)
            if c:
                c0, c1 = bars('C', *c)
                scene['C'] = dict(src0=c0, src1=c1, ratio=(dur - lead) / (c1 - c0))
            timeline.append(scene)
            cursor += dur
        chapters.append(dict(title=chapter['title'], start=round(chapter_start, 3), end=round(cursor, 3)))
    total = cursor + 1.0
    mix = np.zeros((int(total * SR) + SR, 2), dtype='float32')
    midi_notes = {p: [] for p in PARTS}
    report = []
    last = {}
    for si, sc in enumerate(timeline):
        for p in PARTS:
            if p not in sc['mask']:
                continue
            side = p[0]
            m = sc.get(side)
            if not m:
                continue
            piece = pieces[side]
            hand = 0 if p.endswith('rh') else 1
            if side == 'L':
                s0, s1, d0, d1, ratio = m['src0'], m['src1'], sc['dst0'], sc['dst1'], m['ratio']
            else:
                s0, s1, ratio = m['src0'], m['src1'], m['ratio']
                d0, d1 = sc['dst0'] + sc['lead'], sc['dst1']
            # audio: the scene's source span plus a ringing tail, stretched by ratio, faded out over the tail
            src = stems[p]
            a, b = int(s0 * SR), int(min((s1 + TAIL / ratio), len(src) / SR) * SR)
            seg = stretch(src[a:b], ratio)
            n_main = int((d1 - d0) * SR)
            fade_in = min(int(.006 * SR), len(seg))
            seg[:fade_in] *= np.linspace(0, 1, fade_in)[:, None]
            nxt = next((t for t in timeline[si + 1:si + 2] if p in t['mask'] and t.get(side)), None)
            joins = nxt is not None and abs(nxt[side]['src0'] - s1) < 1e-3 and abs(nxt['dst0'] + (nxt['lead'] if side == 'C' else 0) - d1) < 1e-3
            prev = last.get(p)
            if prev is not None and abs(prev[0] - s0) < 1e-3 and abs(prev[1] - d0) < 1e-3:
                seg[:fade_in] /= np.linspace(0, 1, fade_in)[:, None].clip(1e-6)   # continuing: no fade-in
            last[p] = (s1, d1)
            tail = len(seg) - n_main
            if joins:
                seg = seg[:n_main]
            elif tail > 0:
                seg[n_main:] *= np.linspace(1, 0, tail)[:, None] ** 2
            start = int(d0 * SR)
            end = min(start + len(seg), len(mix))
            mix[start:end] += seg[:end - start]
            # MIDI: onsets inside the span, mapped by the same line; sounding length clipped to the scene's end
            for n in piece['notes'][hand]:
                s = piece['sec_of'](n['s'])
                if not (s0 - 1e-6 <= s < s1 - 1e-6):
                    continue
                e = piece['sec_of'](n['e'])
                ds = d0 + (s - s0) * ratio
                de = min(d0 + (e - s0) * ratio, d1 + .3)
                if de - ds >= .05:
                    midi_notes[p].append((ds, de, n['p'], n['v']))
        report.append({k: sc[k] for k in ('chapter', 'name', 'dst0', 'dst1', 'lead', 'mask')} |
                      ({'chopinStretch': round(sc['C']['ratio'], 3)} if 'C' in sc else {}))
    peak = float(np.abs(mix).max())
    mix *= .89 / peak
    wav = OUT / 'recording.wav'
    sf.write(wav, mix[:int(total * SR)], SR, subtype='PCM_16')
    subprocess.run(['ffmpeg', '-y', '-v', 'error', '-i', str(wav), '-b:a', '192k', str(OUT / 'recording.mp3')], check=True)
    # MIDI at 120 bpm / 480 tpb: one second is 960 ticks
    mid = mido.MidiFile(type=1, ticks_per_beat=480)
    conductor = mido.MidiTrack()
    conductor.append(mido.MetaMessage('set_tempo', tempo=500000, time=0))
    conductor.append(mido.MetaMessage('time_signature', numerator=4, denominator=4, time=0))
    conductor.append(mido.MetaMessage('track_name', name='Two pianos', time=0))
    mid.tracks.append(conductor)
    names = {'L_rh': 'Liszt RH', 'L_lh': 'Liszt LH', 'C_rh': 'Chopin RH', 'C_lh': 'Chopin LH'}
    for p in PARTS:
        tr = mido.MidiTrack()
        tr.append(mido.MetaMessage('track_name', name=names[p], time=0))
        events = []
        for ds, de, pitch, vel in midi_notes[p]:
            events.append((round(ds * 960), 1, pitch, vel))
            events.append((round(de * 960), 0, pitch, 0))
        events.sort(key=lambda e: (e[0], e[1]))
        last = 0
        for tick, on, pitch, vel in events:
            tr.append(mido.Message('note_on' if on else 'note_off', note=pitch, velocity=vel if on else 0, time=tick - last))
            last = tick
        mid.tracks.append(tr)
    mid.save(OUT / 'two-pianos.mid')
    (OUT / 'song.json').write_text(json.dumps(dict(Key=5, Minor=False, LeadVocalTrack=-1, KeySource='Authored scores: D-flat major',
                                                     PianoConcerto=True, PianoTracks=[1, 2], OrchestraTracks=[3, 4],
                                                     SectionSource='Scenes of the comparison'), indent=1), encoding='utf-8')
    (OUT / 'timeline.json').write_text(json.dumps(dict(duration=total, chapters=chapters, scenes=report), indent=1), encoding='utf-8')
    for r in report:
        print(f"{r['dst0']:7.1f}-{r['dst1']:7.1f}  ch{r['chapter']}  {r['name']:<28} {' '.join(r['mask']):<20} stretch {r.get('chopinStretch', '-')}")
    print('total', round(total, 1), 's; chapters', [(c['title'], c['start'], c['end']) for c in chapters])


if __name__ == '__main__':
    main()
