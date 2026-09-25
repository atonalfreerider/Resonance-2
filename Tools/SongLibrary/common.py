"""Shared paths, atomic state and bounded subprocess execution for offline jobs."""
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / 'SongLibraryData'


def venv_python(directory):
    """Return the platform-appropriate interpreter in a project-local venv."""
    directory = Path(directory)
    candidates = (directory/'bin/python', directory/'Scripts/python.exe')
    return next((path for path in candidates if path.is_file()), candidates[0])


PREP_PYTHON = venv_python(ROOT/'Tools/SongPrep/.venv')
MODEL_PYTHON = venv_python(ROOT/'Tools/SongLibrary/.venv')
PIPELINE_VERSION = 2


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read_json(path, default=None):
    return json.loads(Path(path).read_text(encoding='utf-8')) if Path(path).exists() else default


def atomic_json(path, value):
    path = Path(path); path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.tmp')
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding='utf-8')
    os.replace(temporary, path)


def slug(text):
    return re.sub(r'[^a-zA-Z0-9_-]+', '-', text).strip('-')[:70] or 'song'


def run(command, log=None, timeout=7200, env=None):
    command = [str(x) for x in command]
    if log:
        with Path(log).open('a', encoding='utf-8') as stream:
            stream.write('\n$ ' + subprocess.list2cmdline(command) + '\n'); stream.flush()
            subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT, check=True,
                           timeout=timeout, cwd=ROOT, env=env)
    else:
        return subprocess.run(command, capture_output=True, text=True, encoding='utf-8',
                              errors='replace', check=True, timeout=timeout, cwd=ROOT, env=env).stdout


# Isolated stems show the master's harmony and form: the same chords, fundamentals,
# section groups and per-visit passes, so a stem's pattern wheels match the full mix.
MASTER_FIELDS = ('Chords', 'RegionPhases', 'Key', 'Minor', 'KeySource', 'Duration', 'EndBeat',
                 'Style', 'FormName', 'FormGrammar', 'SongBars', 'FundamentalBars', 'Patterns', 'Groups', 'Lyrics')
MASTER_SECTION_FIELDS = ('Chords', 'ProgressionBeats', 'Family', 'Label', 'Role', 'Letter', 'Short',
                         'Passes', 'Variation', 'Group', 'GroupVisit', 'CycleBeats', 'Loops')


def compile_patterns(midi, log=None):
    sync_lyrics(Path(midi), log)
    run(['dotnet', 'run', '--project', ROOT/'Tools/PatternPrep/PatternPrep.csproj', '--', midi], log)


# A lyric sheet beside the MIDI (lyrics.txt) is synced to the recording first when the MIDI
# carries no lyric events of its own; PatternPrep then reads lyrics.timing.json.
def sync_lyrics(midi, log=None):
    folder = Path(midi).parent
    sheet, timing, audio = folder/'lyrics.txt', folder/'lyrics.timing.json', folder/'recording.wav'
    if not sheet.exists() or not audio.exists():
        return
    if timing.exists() and timing.stat().st_mtime >= max(sheet.stat().st_mtime, audio.stat().st_mtime):
        return
    import mido
    if any(message.type == 'lyrics' for track in mido.MidiFile(midi).tracks for message in track):
        return
    from lyric_sync import sync
    out = sync(folder)
    if log:
        with open(log, 'a', encoding='utf-8') as stream:
            stream.write(f'\nlyrics synced to the recording: {out}\n')


def validate_bundle(directory):
    directory = Path(directory)
    manifest = read_json(directory/'aligned.mid.prepared.json')
    if not manifest:
        raise ValueError('Missing completed recording manifest')
    for key, hash_key in [('audioPath', 'audioSha256'), ('midiPath', 'midiSha256')]:
        path = (directory/manifest[key]).resolve()
        if not path.is_relative_to(directory.resolve()) or not path.is_file() or sha(path) != manifest[hash_key]:
            raise ValueError('Bundle file/hash mismatch: ' + key)
    for stem in manifest.get('stems',[]):
        for key in ('audio','midi','patterns'):
            path=(directory/stem[key+'Path']).resolve()
            if not path.is_relative_to(directory.resolve()) or not path.is_file() or sha(path)!=stem[key+'Sha256']:
                raise ValueError('Stem file/hash mismatch: '+stem['id']+' / '+key)
        import soundfile as sf
        info=sf.info(directory/stem['audioPath']);master_info=sf.info(directory/manifest['audioPath'])
        if (info.frames,info.samplerate)!=(master_info.frames,master_info.samplerate):
            raise ValueError('Stem sample clock differs from full recording: '+stem['id'])
        if read_json(directory/stem['patternsPath'])['MidiSha256']!=stem['midiSha256']:
            raise ValueError('Stale stem patterns: '+stem['id'])
    patterns = read_json(directory/'aligned.mid.patterns.json')
    if patterns['MidiSha256'] != manifest['midiSha256'] or not patterns['Notes']:
        raise ValueError('Stale or empty pattern analysis')
    for section in patterns['Sections']:
        phases = [p for p in patterns['RegionPhases'] if section['Start'] <= p['Start'] and p['End'] <= section['End']]
        if not phases or phases[0]['Start'] != section['Start'] or phases[-1]['End'] != section['End']:
            raise ValueError('Region timeline does not cover the section')
        if any(a['End'] != b['Start'] for a, b in zip(phases, phases[1:])):
            raise ValueError('Region timeline contains a dropout')
    return patterns
