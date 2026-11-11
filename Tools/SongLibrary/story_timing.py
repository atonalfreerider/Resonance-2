"""Align recording captions to a prepared voice track without regenerating speech.

narration.py runs this after every render; run it by hand to align an existing narration again:
story_timing.py <prepared-bundle>. Uses the same local MMS word aligner as the tutorial captions.

Every spoken line's words (its `speech`, or its caption when it has none) are aligned in the narration WAV, and its
segment in narration.json gets them as spokenWords, with spokenStarts and spokenEnds in seconds from the line's start.
wordStarts and wordEnds carry the same times: SongRecordingCaptions reads those and matches them against the same
spoken words, and the editor's package reads spokenStarts.
"""
import argparse
import os
import re
import tempfile
import unicodedata
from pathlib import Path
import soundfile as sf
from common import atomic_json, read_json, sha
from tutorial_timing import MODELS, align, clean

SPOKEN_FIELDS = ('spokenWords', 'spokenStarts', 'spokenEnds', 'wordStarts', 'wordEnds')


def spoken_words(cue):
    """What the narrator says, word by word as the captions show it: the cue's speech (or its caption) without a leading
    [direction], which Eleven v4 takes as performance control, not words."""
    return re.sub(r'^\s*\[[^\]]*\]\s*', '', cue.get('speech') or cue['text']).split()


def audible(words):
    """The words in the aligner's alphabet: accents folded (étude as etude, not "tude"), and [tags] left empty so
    they are placed between their neighbours, as digits are."""
    out, tag = [], False
    for word in words:
        if word.startswith('['):
            tag = True
        out.append('' if tag else unicodedata.normalize('NFKD', word).encode('ascii', 'ignore').decode())
        if word.endswith(']'):
            tag = False
    return out


def prepare(folder):
    folder = Path(folder)
    story = read_json(folder/'story.json')
    manifest = read_json(folder/'narration.json')
    if manifest['storySha256'] != sha(folder/'story.json') or manifest['sha256'] != sha(folder/manifest['audioPath']):
        raise ValueError('Narration must match the current story before aligning words')
    import torch, torchaudio
    os.environ['TORCH_HOME'] = str(MODELS)
    bundle = torchaudio.pipelines.MMS_FA
    device = 'cuda' if torch.cuda.is_available() else 'cpu'
    network = bundle.get_model(with_star=False).to(device)
    tokenizer, aligner = bundle.get_tokenizer(), bundle.get_aligner()

    def model(waveform):
        # This runs after every render, and on a GPU the emission takes seconds where the CPU takes over a minute;
        # the aligner gets it back on the CPU.
        emission, lengths = network(waveform.to(device))
        return emission.cpu(), lengths

    signal, rate = sf.read(folder/manifest['audioPath'], dtype='float32')
    with tempfile.TemporaryDirectory(prefix='resonance-caption-') as temporary:
        wav = Path(temporary)/'scene.wav'
        for segment in manifest['segments']:
            for key in SPOKEN_FIELDS:
                segment.pop(key, None)
            words = spoken_words(story['cues'][segment['cue']])
            heard = audible(words)
            if not any(clean(w) for w in heard):
                continue                                   # nothing to hear (only digits or tags): the captions estimate
            sf.write(wav, signal[round(segment['start']*rate):round(segment['end']*rate)], rate)
            starts, ends = align(model, tokenizer, aligner, bundle, wav, heard)
            segment.update(spokenWords=words, spokenStarts=starts, spokenEnds=ends, wordStarts=starts, wordEnds=ends)
            print(f"Cue {segment['cue']}: {len(starts)} words aligned", flush=True)
    manifest['wordTimingMethod'] = 'MMS forced alignment of rendered narration'
    atomic_json(folder/'narration.json', manifest)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle', type=Path)
    prepare(parser.parse_args().bundle)
