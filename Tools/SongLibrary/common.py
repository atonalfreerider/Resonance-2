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
PREP_PYTHON = ROOT / 'Tools/SongPrep/.venv/Scripts/python.exe'
MODEL_PYTHON = ROOT / 'Tools/SongLibrary/.venv/Scripts/python.exe'
PIPELINE_VERSION = 1


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


def compile_patterns(midi, log=None):
    run(['dotnet', 'run', '--project', ROOT/'Tools/PatternPrep/PatternPrep.csproj', '--', midi], log)


def validate_bundle(directory):
    directory = Path(directory)
    manifest = read_json(directory/'aligned.mid.prepared.json')
    if not manifest:
        raise ValueError('Missing completed recording manifest')
    for key, hash_key in [('audioPath', 'audioSha256'), ('midiPath', 'midiSha256')]:
        path = (directory/manifest[key]).resolve()
        if not path.is_relative_to(directory.resolve()) or not path.is_file() or sha(path) != manifest[hash_key]:
            raise ValueError('Bundle file/hash mismatch: ' + key)
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
