"""Word timings for the tutorial narration: each step's WAV is force-aligned (torchaudio's MMS
aligner, the one lyric sync uses; weights cached under SongLibraryData/models) against the words
it speaks, and manifest.json gets, per clip, the start and end of every word of the step's
displayed text, so the caption can light the word being spoken.

  Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/tutorial_timing.py

Run after tutorial_narration.py. The spoken wording (`speech`) may spell a word differently
from the caption (Möbius / Moybius); the words are matched by position, and words the aligner
cannot hear (numbers, symbols) are placed between their neighbours."""
import json
import os
import re
from pathlib import Path
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
FOLDER = ROOT / 'Assets' / 'StreamingAssets' / 'Tutorial'
MODELS = ROOT / 'SongLibraryData' / 'models' / 'torch'


def clean(word):
    return re.sub(r"[^a-z']", '', word.lower().replace('’', "'"))


def align(model, tokenizer, aligner, bundle, wav, words):
    import torch, torchaudio
    waveform, rate = torchaudio.load(str(wav))
    waveform = torchaudio.functional.resample(waveform.mean(0, keepdim=True), rate, bundle.sample_rate)
    with torch.inference_mode():
        emission, _ = model(waveform)
    tokens = [clean(w) for w in words]
    heard = [i for i, t in enumerate(tokens) if t]
    spans = aligner(emission[0], tokenizer([tokens[i] for i in heard]))
    ratio = 320 / bundle.sample_rate
    starts, ends = [None] * len(words), [None] * len(words)
    for i, span in zip(heard, spans):
        starts[i], ends[i] = span[0].start * ratio, span[-1].end * ratio
    # Unheard words (digits, symbols) sit between their neighbours.
    total = waveform.size(1) / bundle.sample_rate
    for i in range(len(words)):
        if starts[i] is None:
            prev = next((ends[j] for j in range(i - 1, -1, -1) if ends[j] is not None), 0.0)
            nxt = next((starts[j] for j in range(i + 1, len(words)) if starts[j] is not None), total)
            starts[i], ends[i] = prev, max(prev, nxt)
    return [round(s, 3) for s in starts], [round(e, 3) for e in ends]


def main():
    import torch, torchaudio
    os.environ['TORCH_HOME'] = str(MODELS)
    bundle = torchaudio.pipelines.MMS_FA
    model = bundle.get_model(with_star=False)
    tokenizer, aligner = bundle.get_tokenizer(), bundle.get_aligner()
    script = json.loads((FOLDER / 'script.json').read_text(encoding='utf-8'))
    manifest = json.loads((FOLDER / 'manifest.json').read_text(encoding='utf-8'))
    clips = {c['id']: c for c in manifest['clips']}
    for step in script['steps']:
        clip = clips.get(step['id'])
        if not clip:
            continue
        shown = step['text'].split()
        spoken = (step.get('speech') or step['text']).split()
        if len(spoken) != len(shown):
            raise ValueError(f"{step['id']}: speech and text must have the same number of words")
        starts, ends = align(model, tokenizer, aligner, bundle, FOLDER / clip['file'], spoken)
        clip['starts'], clip['ends'] = starts, ends
        print(f"{step['id']}: {len(shown)} words, last ends {ends[-1]:.1f} s of {clip['seconds']:.1f} s")
    (FOLDER / 'manifest.json').write_text(json.dumps(manifest, indent=1), encoding='utf-8')


if __name__ == '__main__':
    main()
