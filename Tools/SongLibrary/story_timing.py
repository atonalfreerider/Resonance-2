"""Align recording captions to a prepared voice track without regenerating speech.

Run after narration generation: story_timing.py <prepared-bundle>.
Uses the same local MMS word aligner as the tutorial captions.
"""
import argparse
import os
import re
import tempfile
from pathlib import Path
import soundfile as sf
from common import atomic_json, read_json, sha
from tutorial_timing import MODELS, align


def prepare(folder):
    import torchaudio
    folder = Path(folder)
    story = read_json(folder/'story.json')
    manifest = read_json(folder/'narration.json')
    if manifest['storySha256'] != sha(folder/'story.json') or manifest['sha256'] != sha(folder/manifest['audioPath']):
        raise ValueError('Narration must match the current story before aligning words')
    os.environ['TORCH_HOME'] = str(MODELS)
    bundle = torchaudio.pipelines.MMS_FA
    model = bundle.get_model(with_star=False)
    tokenizer, aligner = bundle.get_tokenizer(), bundle.get_aligner()
    signal, rate = sf.read(folder/manifest['audioPath'], dtype='float32')
    with tempfile.TemporaryDirectory(prefix='resonance-caption-') as temporary:
        wav = Path(temporary)/'scene.wav'
        for segment in manifest['segments']:
            cue = story['cues'][segment['cue']]
            shown = cue['text'].split()
            # Eleven v4 uses a leading bracketed direction as performance control; it is not
            # spoken. Exclude it before comparing and aligning the words shown on screen.
            spoken_text = re.sub(r'^\s*\[[^\]]*\]\s*', '', cue.get('speech') or cue['text'])
            spoken = spoken_text.split()
            if len(shown) != len(spoken):
                # Keep the player's clock-based fallback for rewritten speech.
                segment.pop('wordStarts', None)
                continue
            sf.write(wav, signal[round(segment['start']*rate):round(segment['end']*rate)], rate)
            starts, ends = align(model, tokenizer, aligner, bundle, wav, spoken)
            segment['wordStarts'] = starts
            segment['wordEnds'] = ends
            print(f"Cue {segment['cue']}: {len(starts)} words aligned", flush=True)
    manifest['wordTimingMethod'] = 'MMS forced alignment of rendered narration'
    atomic_json(folder/'narration.json', manifest)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle', type=Path)
    prepare(parser.parse_args().bundle)
