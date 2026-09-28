"""Cut the tutorial recording into Shorts and the full tour, on step boundaries.

  Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/cut_shorts.py Recordings/Videos/tutorial-vertical-<time>.json

The JSON is the report RecordingSession writes beside the video (each tutorial step's start and end
in the video). Each clip is re-encoded (H.264 / AAC) with accurate cuts; a Short can open on a
one-second hook taken from later in its own span (the band snapping shut, the key-change twist),
then play from its start. The full tour ends before the outro, which speaks to app users and
would push it past the three-minute Shorts limit. A posting plan (titles, descriptions, X
captions, schedule) is written beside the clips."""
import json
import subprocess
import sys
from pathlib import Path

# Steps (by id) in each clip, and where its hook comes from: (step, fraction of that step, seconds).
CLIPS = [
    dict(slug='short1-four-triangles', steps=['twelve', 'triangles', 'moebius'], hook=('moebius', .80, 1.2),
         title='The 4 Triangles Hiding in Every Octave',
         description='Take every fourth note of the 12-tone circle and you get a triangle. There are exactly four of them, and spinning one around the ring builds a Möbius band with three sides, called the umbilic torus. Part of my Human Super Intelligence series. #musictheory #math #geometry #music #shorts',
         x='There are exactly 4 triangles hiding inside the 12 notes of music. Spin one around the ring and it builds a Möbius band with three sides. 🎶 #MusicTheory'),
    dict(slug='short2-key-change-twist', steps=['colours', 'position', 'keychange'], hook=('keychange', .40, 1.2),
         title='What a Key Change Looks Like in 3D',
         description='Blue is home, red is the subdominant, and green is the dominant, in any key. Chords that lead into each other sit side by side, and when a song changes key the whole shape turns. #musictheory #music #songwriting #math #shorts',
         x='This is what a key change looks like. Nothing gets redrawn. The whole shape just turns until the new key is on top. 🔄 #MusicTheory'),
    dict(slug='short3-every-note-has-a-job', steps=['key'], hook=None,
         title='Every Note Is Named by Its Relationship to the Key',
         description='The key sits on top, with its fourth and fifth beside it. Every other note is named by its role, like the minor third or the Neapolitan, and each note\'s octaves stack beneath it. #musictheory #music #songwriter #shorts',
         x="Musicians don't really think in note names. They think in relationships to home. Here's what that looks like, with every octave stacked underneath. #MusicTheory"),
    dict(slug='short4-history', steps=['fifths', 'credit'], hook=None,
         title='The 1976 Math Shape That Maps All of Harmony',
         description='The circle of fifths is a single ring. The umbilic torus keeps that ring and adds major thirds as a second direction. Christopher Zeeman named the shape the umbilic bracelet in 1976, and in 2024 Dimitrios Cholidis published the Cholidean harmony structure, which puts music on it. #musictheory #math #history #circleoffifths #shorts',
         x='The circle of fifths is missing a whole dimension. A 1976 shape from singularity theory adds it back, and Dimitrios Cholidis turned it into a map of harmony. #MusicTheory'),
    dict(slug='full-tour', steps=['welcome', 'twelve', 'triangles', 'moebius', 'key', 'colours', 'position', 'keychange', 'fifths', 'credit'], hook=None,
         title='All of Harmony on One Shape: The Umbilic Torus (Full Tour)',
         description='A narrated tour of the umbilic torus: the twelve tones, the four triangles, the three-sided band, the key and its colours, key changes as a twist, and the circle of fifths compared. Built on the Cholidean harmony structure by Dimitrios Cholidis. #musictheory #math #music',
         x='All of harmony on one shape: the full tour of the umbilic torus.'),
]
SCHEDULE = [('Week 1, Monday', 'short1-four-triangles'), ('Week 1, Wednesday', 'short2-key-change-twist'), ('Week 1, Friday', 'short4-history'),
            ('Week 2, Monday', 'short3-every-note-has-a-job'), ('Week 2, Wednesday', 'full-tour')]


def cut(source, output, start, end, hook=None):
    """Re-encode [start, end], optionally preceded by the hook span [a, b]."""
    parts = ([hook] if hook else []) + [(start, end)]
    filters, labels = [], []
    for i, (a, b) in enumerate(parts):
        filters.append(f'[0:v]trim=start={a:.3f}:end={b:.3f},setpts=PTS-STARTPTS[v{i}];[0:a]atrim=start={a:.3f}:end={b:.3f},asetpts=PTS-STARTPTS[a{i}]')
        labels.append(f'[v{i}][a{i}]')
    graph = ';'.join(filters) + ';' + ''.join(labels) + f'concat=n={len(parts)}:v=1:a=1[v][a]'
    subprocess.run(['ffmpeg', '-y', '-loglevel', 'error', '-i', str(source), '-filter_complex', graph, '-map', '[v]', '-map', '[a]',
                    '-c:v', 'libx264', '-preset', 'medium', '-crf', '18', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '192k',
                    '-movflags', '+faststart', str(output)], check=True)
    return sum(b - a for a, b in parts)


def main(report_path):
    report_path = Path(report_path)
    report = json.loads(report_path.read_text(encoding='utf-8'))
    source = report_path.with_name(report['video'])
    steps = {m['id']: m for m in report['markers']}
    folder = report_path.with_suffix('')
    folder.mkdir(exist_ok=True)
    made = []
    for clip in CLIPS:
        if not all(s in steps for s in clip['steps']):
            print(f"skip {clip['slug']}: missing steps"); continue
        start, end = steps[clip['steps'][0]]['start'], steps[clip['steps'][-1]]['end']
        hook = None
        if clip['hook']:
            step, fraction, length = clip['hook']; m = steps[step]
            at = m['start'] + fraction * (m['end'] - m['start']); hook = (max(m['start'], at - length / 2), min(m['end'], at + length / 2))
        output = folder / f"{clip['slug']}-{report['orientation']}.mp4"
        seconds = cut(source, output, start, end, hook)
        made.append(dict(clip, file=output.name, seconds=seconds, span=(start, end), hookSpan=hook))
        warn = '  (over the 3-minute Shorts limit)' if seconds > 180 else ''
        print(f"{output.name}: {seconds:.1f} s{warn}")
    # The posting plan.
    lines = ['# Posting plan', '', f"Cut from `{report['video']}` ({report['orientation']}, {report['width']}x{report['height']}).", '',
             'Post each clip natively on YouTube Shorts and on X. On the day the full tour goes up, post an X thread: Short 1 as the opening post, '
             'Shorts 2, 3 and 4 as replies in story order, and the full YouTube tour linked in the last reply (X shows posts with outside links to fewer people). '
             'A free X account cannot upload the full three-minute video. If Dimitrios Cholidis is on X, tag him in the history post.', '']
    for c in made:
        mm, ss = divmod(round(c['seconds']), 60)
        lines += [f"## {c['title']}", '', f"- File: `{c['file']}` ({mm}:{ss:02d})",
                  f"- Span in the recording: {c['span'][0]:.1f}–{c['span'][1]:.1f} s" + (f"; opens on {c['hookSpan'][0]:.1f}–{c['hookSpan'][1]:.1f} s" if c['hookSpan'] else ''),
                  f"- YouTube description: {c['description']}", f"- X caption: {c['x']}", '']
    lines += ['## Schedule', ''] + [f"- {day}: {next((c['title'] for c in made if c['slug'] == slug), slug)}" for day, slug in SCHEDULE] + ['']
    (folder / 'posting-plan.md').write_text('\n'.join(lines), encoding='utf-8')
    print('plan:', folder / 'posting-plan.md')


if __name__ == '__main__':
    main(sys.argv[1])
