"""Cut a recorded Liszt/Chopin take into its chapters (each 60 s or less), with short fades.

    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/liszt_chopin/cut_chapters.py Recordings/Videos/<take>.json
"""
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


def main(report):
    report = Path(report).resolve()
    take = json.loads(report.read_text(encoding='utf-8'))
    timeline = json.loads((ROOT / 'SongLibraryData/scores/liszt-chopin/build/timeline.json').read_text(encoding='utf-8'))
    video = report.with_suffix('.mp4')
    out = report.parent / (report.stem + '-chapters')
    out.mkdir(exist_ok=True)
    offset = take.get('audioStart', 0) - take.get('songStart', 0)
    for i, ch in enumerate(timeline['chapters'], 1):
        a, b = ch['start'] + offset, ch['end'] + offset + 0.3
        length = b - a
        assert length <= 60, f'chapter {i} is {length:.1f} s'
        slug = re.sub(r'[^a-z0-9]+', '-', ch['title'].lower()).strip('-')
        target = out / f'{i}-{slug}.mp4'
        subprocess.run(['ffmpeg', '-y', '-v', 'error', '-ss', f'{a:.3f}', '-i', str(video), '-t', f'{length:.3f}',
                        '-vf', f'fade=t=in:d=0.25,fade=t=out:st={length - .3:.3f}:d=0.3',
                        '-af', f'afade=t=in:d=0.25,afade=t=out:st={length - .3:.3f}:d=0.3',
                        '-c:v', 'libx264', '-crf', '18', '-preset', 'medium', '-c:a', 'aac', '-b:a', '192k', str(target)], check=True)
        print(f'{target.name}: {length:.1f} s')


if __name__ == '__main__':
    main(sys.argv[1])
