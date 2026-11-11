"""Align recording captions to a prepared voice track without regenerating speech.

narration.py runs this after every render; run it by hand to align an existing narration again:
story_timing.py <prepared-bundle>. Uses the same local MMS word aligner as the tutorial captions.

Every spoken line's words (its `speech`, or its caption when it has none) are aligned in the narration WAV, and its
segment in narration.json gets them as spokenWords, with spokenStarts and spokenEnds in seconds from the line's start.
The aligner hears letters, so it is given what the voice says: a note's letter by its name ("E" as "ee", "G♯" as "gee
sharp"), symbols and digits in words ("6ths" as "sixths", "2009" as "two thousand nine"), and each word takes the times
of what is said for it. A word's end reaches the next word's start across a gap too short to be a pause (JOIN_GAP, or
PHRASE_GAP inside a phrase). wordStarts and wordEnds carry the same times with one entry per word as
SongRecordingCaptions splits the speech: the words of a leading [direction], which nobody says, take the first spoken
word's start. The editor's package reads spokenStarts.

The GPU aligns when there is one, and the CPU again when that fails. The words are written only into the narration they
were heard in: if a newer render replaced narration.json meanwhile, it is left as it is.
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
# The aligner ends a word where it last heard one of its letters, often before the voice leaves it (a vowel's span is
# a frame or two): across a gap shorter than this, which is no pause, a word ends where the next one starts.
JOIN_GAP = .15
# Inside a phrase (no punctuation after the word) gaps up to this are the aligner's too: in the étude every pause after
# punctuation measured 0.26 s or more, and the articles before "fourth", "third" and "single" kept 20 ms spans at 0.16 s.
PHRASE_GAP = .35
PAUSE = re.compile(r'[,;:.!?…—–]["\'”’)\]]*$')
# A chord symbol, as a script or Whisper writes one ("B7", "Bb7", "Am7", "F#m", "Cmaj7", "Gdim", "Dsus4", "C7b9").
CHORD = re.compile(r'([A-G])([#♯b♭]?)(maj|min|dim|aug|sus|add|m|M|°|ø|\+)?(\d{1,2})?((?:(?:[#♯b♭]|sus|add)\d{1,2})*)')
SIGN_WORDS = {'#': 'sharp', '♯': 'sharp', 'b': 'flat', '♭': 'flat', '♮': 'natural'}
QUALITY_WORDS = {'maj': 'major', 'M': 'major', 'min': 'minor', 'm': 'minor', 'dim': 'diminished', '°': 'diminished',
                 'ø': 'half diminished', 'aug': 'augmented', '+': 'augmented', 'sus': 'suspended', 'add': 'add'}
LETTER_NAMES = dict(a='ay', b='bee', c='see', d='dee', e='ee', f='eff', g='gee', h='aitch', i='eye', j='jay', k='kay',
                    l='el', m='em', n='en', o='oh', p='pee', q='cue', r='ar', s='ess', t='tee', u='you', v='vee',
                    w='double you', x='ex', y='why', z='zee')
# After a note's letter these make it a note ("A major"); without one, a sentence's first "A" is the article.
MARKS = {'major', 'minor', 'sharp', 'flat', 'natural', 'prime'}
SPOKEN_ABBREVIATIONS = dict(dr='doctor', mr='mister', mrs='missus', st='saint', jr='junior', sr='senior', vs='versus')
ONES = ('zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen '
        'seventeen eighteen nineteen').split()
TENS = 'twenty thirty forty fifty sixty seventy eighty ninety'.split()
ORDINAL_WORDS = dict(one='first', two='second', three='third', five='fifth', eight='eighth', nine='ninth', twelve='twelfth')
SEPARATORS = re.compile(r'[\s\-‐‑‒–—―/]+')
EDGES = '.,;:!?"\'“”‘’()[]{}«»…*'


def caption_words(cue):
    """The words SongRecordingCaptions shows: the cue's speech split on whitespace, or its caption when the speech is
    blank."""
    speech = cue.get('speech')
    return (speech if speech and speech.strip() else cue['text']).split()


def spoken_words(cue):
    """What the narrator says, word by word as the captions show it: the caption words without a leading [direction],
    which Eleven v4 takes as performance control, not words."""
    speech = cue.get('speech')
    return re.sub(r'^\s*\[[^\]]*\]\s*', '', speech if speech and speech.strip() else cue['text']).split()


def chord_words(text):
    """A chord symbol's words as they are said: "Bb7" is ['B', 'flat', '7'], "Am7" ['A', 'minor', '7']; None for
    anything else. "Am", "AM" and "FM" are words, so a symbol without a number needs a quality that is no word."""
    m = CHORD.fullmatch(text)
    if not m:
        return None
    root, sign, quality, number, more = m.groups()
    if not number and not more and (not quality or quality == 'M' or quality == 'm' and root == 'A' and not sign):
        return None
    words = [root] + [SIGN_WORDS[s] for s in sign] + QUALITY_WORDS.get(quality, '').split() + ([number] if number else [])
    for mark, value in re.findall(r'([#♯b♭]|sus|add)(\d{1,2})', more):
        words += [SIGN_WORDS.get(mark) or QUALITY_WORDS[mark], value]
    return words


def cardinal(n):
    if n < 20:
        return [ONES[n]]
    if n < 100:
        return [TENS[n//10-2]] + ([ONES[n % 10]] if n % 10 else [])
    for size, name in ((10**9, 'billion'), (10**6, 'million'), (1000, 'thousand'), (100, 'hundred')):
        if n >= size:
            return cardinal(n//size) + [name] + (cardinal(n % size) if n % size else [])


def year(n):
    """A four-digit number as a year is said: 1832 eighteen thirty two, 1900 nineteen hundred, 1905 nineteen oh five,
    2009 two thousand nine, 2016 twenty sixteen."""
    high, low = divmod(n, 100)
    if not (11 <= high <= 19 or high == 20 and low >= 10):
        return cardinal(n)
    return cardinal(high) + (['hundred'] if not low else ['oh'] + cardinal(low) if low < 10 else cardinal(low))


def number_words(text):
    """Digits as they are said, or None: 13, 2009, 6ths (sixths), 21st, 1990s (nineteen nineties), 90's, 1.5, 03 (oh
    three), 1,000."""
    m = re.fullmatch(r"(\d{1,3}(?:,\d{3})+|\d+)(?:\.(\d+))?(?:(st|nd|rd|th)(s?)|'?(s))?", text, re.IGNORECASE)
    if not m:
        return None
    digits, decimal, ordinal, plural_ordinal, decade = m.groups()
    whole = digits.replace(',', '')
    n = int(whole)
    zeros = len(whole) - len(whole.lstrip('0')) if n else len(whole) - 1
    words = ['oh']*zeros + (year(n) if len(whole) == 4 and not zeros and ',' not in digits and not decimal else cardinal(n))
    if decimal:
        words += ['point'] + [ONES[int(d)] for d in decimal]
    if ordinal:
        last = words[-1]
        words[-1] = ORDINAL_WORDS.get(last) or (last[:-1] + 'ieth' if last.endswith('y') else last + 'th')
    if plural_ordinal or decade:
        last = words[-1]
        words[-1] = last[:-1] + 'ies' if last.endswith('y') else last + ('es' if last.endswith(('s', 'x')) else 's')
    return words


def fold(text):
    return ''.join(c for c in unicodedata.normalize('NFKD', text) if not unicodedata.combining(c))


def said_part(part, initial, following):
    """What is said for one piece of a word (a word split at its hyphens and dashes)."""
    note = re.fullmatch(r'([A-G])([#♯b♭♮]?)(′|″)?', part)
    if note and not (part == 'A' and initial and following not in MARKS):
        letter, sign, prime = note.groups()
        return ([LETTER_NAMES[letter.lower()]] + ([SIGN_WORDS[sign]] if sign else [])
                + (['prime'] if prime == '′' else ['double', 'prime'] if prime else []))
    part = part.replace('′', ' prime').replace('″', ' double prime')
    if ' ' in part:
        return [w for p in part.split() for w in said_part(p, False, following)]
    chord = chord_words(part)
    if chord:
        return [w for p in chord for w in (number_words(p) or said_part(p, False, ''))]
    if re.fullmatch(r'(?:[A-Z]\.)+[A-Z]?|[A-Z]{2,3}|[B-HJ-Z]', part):                 # P.S., DJ, MTV, Jay Z
        return [w for c in part if c != '.' for w in LETTER_NAMES[c.lower()].split()]
    run = re.fullmatch(r'([A-Za-z]+)(\d.*)', part)                                     # PS1, MTV2
    if run:
        return said_part(run.group(1), False, '') + said_part(run.group(2), False, '')
    money = re.fullmatch(r'([$£€])(\d[\d,.]*)', part)
    if money:
        return (number_words(money.group(2)) or []) + [dict(zip('$£€', ('dollars', 'pounds', 'euros')))[money.group(1)]]
    if re.fullmatch(r'#\d+', part):
        return ['number'] + number_words(part[1:])
    if part.endswith('%') and number_words(part[:-1]):
        return number_words(part[:-1]) + ['percent']
    spoken = number_words(part)
    if spoken:
        return spoken
    key = clean(fold(part))
    return [SPOKEN_ABBREVIATIONS.get(key, key)] if key else []


def say(words):
    """What the voice says for each word, in words the aligner can hear: a list per word, empty for what nobody says (a
    [direction], a dash)."""
    out, tag, initial = [], False, True
    for k, word in enumerate(words):
        if word.startswith('['):
            tag = True
        if tag:
            tag = not word.endswith(']')
            out.append([])
            continue
        # A dash between digits is said "to" ("bars 6–13", "1829–1832").
        word = re.sub(r'(?<=\d)[‒–—-](?=\d)', ' to ', word.replace('&', ' and '))
        parts = [p for p in (p.strip(EDGES) for p in SEPARATORS.split(word)) if p]
        after = parts[1:] + [SEPARATORS.split(words[k+1])[0] if k+1 < len(words) else '']
        out.append([w for n, p in enumerate(parts) for w in said_part(p, initial and n == 0, clean(fold(after[n])))])
        initial = bool(re.search(r'[.!?][\'"”)\]]*$', word))
    return out


def word_times(model, tokenizer, aligner, bundle, wav, words):
    """Each word's start and end in the clip, from the alignment of what is said for it; a word nobody says (and so
    one the aligner cannot hear) sits between its neighbours."""
    spoken = say(words)
    flat, owner = [], []
    for i, said in enumerate(spoken):
        flat += said or ['']
        owner += [i]*max(1, len(said))
    starts, ends = align(model, tokenizer, aligner, bundle, wav, flat)
    first = {o: s for o, s in reversed(list(zip(owner, starts)))}
    last = dict(zip(owner, ends))
    starts, ends = [first[i] for i in range(len(words))], [last[i] for i in range(len(words))]
    for i in range(len(words)-1):
        if starts[i+1]-ends[i] < (JOIN_GAP if PAUSE.search(words[i]) else PHRASE_GAP):
            ends[i] = max(ends[i], starts[i+1])
    return starts, ends


def align_lines(bundle, device, story, manifest, signal, rate):
    """Every spoken line's word fields, aligned on `device`: one dict per segment, empty for a line with nothing to
    hear (only tags: the captions estimate it)."""
    network = bundle.get_model(with_star=False).to(device)
    tokenizer, aligner = bundle.get_tokenizer(), bundle.get_aligner()

    def model(waveform):
        # This runs after every render, and on a GPU the emission takes seconds where the CPU takes over a minute;
        # the aligner gets it back on the CPU.
        emission, lengths = network(waveform.to(device))
        return emission.cpu(), lengths

    lines = []
    with tempfile.TemporaryDirectory(prefix='resonance-caption-') as temporary:
        wav = Path(temporary)/'scene.wav'
        for segment in manifest['segments']:
            cue = story['cues'][segment['cue']]
            words = spoken_words(cue)
            if not any(said for said in say(words)):
                lines.append({})
                continue
            sf.write(wav, signal[round(segment['start']*rate):round(segment['end']*rate)], rate)
            starts, ends = word_times(model, tokenizer, aligner, bundle, wav, words)
            # The captions split the speech as it is written, a leading [direction] too: its words wait for the first
            # spoken word.
            lead = max(0, len(caption_words(cue))-len(words))
            lines.append(dict(spokenWords=words, spokenStarts=starts, spokenEnds=ends,
                              wordStarts=[starts[0]]*lead + starts, wordEnds=[starts[0]]*lead + ends))
            print(f"Cue {segment['cue']}: {len(starts)} words aligned", flush=True)
    return lines


def prepare(folder):
    """Align every spoken line of a bundle's narration and add the word fields to narration.json; returns how many
    lines were aligned, or None when narration.json was replaced meanwhile (a newer render, which aligns its own)."""
    folder = Path(folder)
    story = read_json(folder/'story.json')
    manifest = read_json(folder/'narration.json')
    if manifest['storySha256'] != sha(folder/'story.json') or manifest['sha256'] != sha(folder/manifest['audioPath']):
        raise ValueError('Narration must match the current story before aligning words')
    import torch, torchaudio
    os.environ['TORCH_HOME'] = str(MODELS)
    bundle = torchaudio.pipelines.MMS_FA
    signal, rate = sf.read(folder/manifest['audioPath'], dtype='float32')
    # The GPU is the Unity editor's too: when it fails (out of memory, a lost device), the CPU aligns instead, slower.
    devices = ['cuda', 'cpu'] if torch.cuda.is_available() else ['cpu']
    for device in devices:
        try:
            lines = align_lines(bundle, device, story, manifest, signal, rate)
            break
        except Exception as error:
            if device == devices[-1]:
                raise
            print(f'Aligning on the GPU failed ({type(error).__name__}: {error}); aligning on the CPU', flush=True)
    # Aligning takes seconds to minutes, and a render finishing meanwhile publishes its own narration.json: the words
    # go only into the narration they were heard in.
    current = read_json(folder/'narration.json')
    if not current or any(current.get(k) != manifest[k] for k in ('storySha256', 'sha256', 'audioPath')) or \
            [s['cue'] for s in current['segments']] != [s['cue'] for s in manifest['segments']]:
        print('narration.json was replaced while its words were aligned (a newer render aligns its own): left as it is',
              flush=True)
        return None
    for segment, fields in zip(current['segments'], lines):
        for key in SPOKEN_FIELDS:
            segment.pop(key, None)
        segment.update(fields)
    current['wordTimingMethod'] = 'MMS forced alignment of rendered narration'
    atomic_json(folder/'narration.json', current)
    return sum(bool(fields) for fields in lines)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle', type=Path)
    prepare(parser.parse_args().bundle)
