"""Add the public-domain lyric fixture to the prepared library, as a recording bundle.

  Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/add_lyric_fixture.py

PatternPrep writes the song (Twinkle, Hiawatha and Sonnet 18 on an original arrangement)
with its lyric sheet; render_fixture renders it (formant voice with vibrato, offline speech
for the rapped lines), so the recording is the MIDI's own timing. Select it in Unity's
Prepared library as "lyric-fixture-public-domain · recording" and choose View → Lyrics.
"""
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from common import ROOT, sha  # noqa: E402
from render_fixture import render  # noqa: E402

BUNDLE = ROOT / 'PreparedSongs' / 'Library' / 'lyric-fixture-public-domain'


def main():
    BUNDLE.mkdir(parents=True, exist_ok=True)
    midi = BUNDLE / 'aligned.mid'
    prep = ['dotnet', 'run', '--project', str(ROOT / 'Tools' / 'PatternPrep'), '--']
    subprocess.run(prep + ['--write-fixture', 'lyrics', str(midi)], check=True, cwd=ROOT)
    mix, vocals = render(midi)
    recording = BUNDLE / 'recording.wav'
    mix.replace(recording)
    vocals.replace(BUNDLE / 'vocals.wav')
    (BUNDLE / 'library.json').write_text(json.dumps({
        'title': 'Twinkle · Hiawatha · Sonnet 18 (public-domain lyric fixture)',
        'midiMethod': 'generated-fixture', 'warnings': ['Generated test song: words and tunes are public domain; the recording is rendered from the MIDI.']}, indent=1), encoding='utf-8')
    import soundfile as sf
    info = sf.info(recording)
    (BUNDLE / 'aligned.mid.prepared.json').write_text(json.dumps({
        'version': 1, 'audioPath': 'recording.wav', 'midiPath': 'aligned.mid', 'reportPath': '',
        'sourceAudioPath': 'recording.wav', 'sourceAudioSha256': sha(recording), 'sourceMidiSha256': sha(midi),
        'audioSha256': sha(recording), 'midiSha256': sha(midi), 'featureResolutionMs': 0,
        'status': 'generated fixture: the recording is rendered from the MIDI (exact timing)', 'audioDuration': info.frames / info.samplerate}, indent=1), encoding='utf-8')
    subprocess.run(prep + [str(midi)], check=True, cwd=ROOT)
    print(BUNDLE)


if __name__ == '__main__':
    main()
