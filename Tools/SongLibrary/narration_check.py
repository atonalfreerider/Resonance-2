"""Hear every narrated line again and compare it with the script: a phrase said twice, a word missing, added or changed,
and above all a changed note name, major or minor, or number (the voice said "A major" where the script says "E major").

    Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/narration_check.py PreparedSongs/Library/<song>
    Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/narration.py PreparedSongs/Library/<song> --check

Each line's clip in the narration WAV is transcribed offline by Whisper (openai/whisper-small.en from the local Hugging
Face cache; nothing is downloaded or sent anywhere), and the transcription is cached by the clip's samples, so a check
after a render only hears the lines that changed. What Whisper changes on its own is tolerated: punctuation, case
("A major" as "a major"), hyphens and compounds, Op./Opus, No./Number, digits (decades, leading zeros, "0-3" for
"03", "2 million", "2.5 million"), chord symbols (B7, Bb7, Am7, and "G seventh" as G7, "the flat sixth" as b6th),
letters run into digits (PS1), the article run into a note ("AC natural", "Abm7b5" for "a B minor seven flat five"),
a note's letter as a word ("See now glows" for "C now"), dates ("3 April" for "the third of April") and currency
signs, small words it drops or swaps (an/in), homophones (sighs/size, chords/cords), this project's words it hears as
everyday ones (torus as chorus) and the spelling of names and foreign words when it sounds alike (Gorlatch/Gorlach,
étude/etude, Chopin/Sopa, but not Chopin/Liszt). A phrase of two or more words said twice, whole sentences too, and any
change to a note name, major/minor or a number, is critical. Other differences are flagged for a listen, among them a
number written as a word that sounds like it ("Fifths" as "Fists": the voice said no other number, and a take cannot
change Whisper's spelling) and an interval without its plural ("Sevenths" as "7th"). A line whose duck floor leaves
its voice under 6 dB above the music is listed too. The report (narration-check.txt, and narration-check.json for
tools) goes to Reports/SongProcessing/<bundle>/; the exit status is 1 when a line has a critical issue, 2 when the
narration cannot be checked (or the check itself fails).
"""
import argparse
import collections
import difflib
import hashlib
import math
import os
import re
import sys
import unicodedata
from decimal import Decimal
from pathlib import Path
from common import DATA, ROOT, atomic_json, read_json, sha
from story_timing import SPOKEN_ABBREVIATIONS, chord_words, number_words, spoken_words

MODEL = 'openai/whisper-small.en'
# Part of every transcription's cache key: bump it when the model or its decoding changes, to hear every line again.
DECODER = 1
RATE = 16000

STRIP = '.,;:!?"\'“”‘’()[]{}«»…*'
DASHES = re.compile(r'[\s\-‐‑‒–—―/]+')
ACCIDENTALS = {'sharp': '#', 'flat': 'b', 'natural': 'n'}
SIGNS = {'#': '#', '♯': '#', 'b': 'b', '♭': 'b', '♮': 'n'}                 # written on the letter: G♯, Bb
MODES = ('major', 'minor')
# What can follow a note name (and so makes an "A" a note, not the article).
MARKS = {*ACCIDENTALS, *MODES, 'prime'}
# A note name heard as a word that sounds like its letter ("a see natural"), accepted only before one of MARKS.
LETTER_SOUNDS = {'see': 'c', 'sea': 'c', 'bee': 'b', 'be': 'b', 'dee': 'd', 'gee': 'g', 'eff': 'f'}
UNITS = {w: i for i, w in enumerate('zero one two three four five six seven eight nine ten eleven twelve thirteen '
                                    'fourteen fifteen sixteen seventeen eighteen nineteen'.split())}
TENS = {w: 10*i for i, w in enumerate('twenty thirty forty fifty sixty seventy eighty ninety'.split(), 2)}
SCALES = {'hundred': 100, 'thousand': 1000, 'million': 10**6, 'billion': 10**9}
ORDINALS = dict({'first': 1, 'second': 2, 'third': 3, 'fourth': 4, 'fifth': 5, 'sixth': 6, 'seventh': 7, 'eighth': 8,
                 'ninth': 9, 'tenth': 10, 'eleventh': 11, 'twelfth': 12, 'hundredth': 100, 'thousandth': 1000},
                **{w+'th': v for w, v in UNITS.items() if 13 <= v <= 19}, **{w[:-1]+'ieth': v for w, v in TENS.items()})
# Numbers that sound like a word ("where he won", "bars 6 to 13", "rises forte"): one for the other is not a change.
NUMBER_SOUNDS = {'#1': {'won'}, '#2': {'to', 'too'}, '#4': {'for', 'fore'}, '#4th': {'forth'}, '#8': {'ate'}, '#40': {'forte'}}
# Short words that go with a name ("an étude", "his pupil Adolph") when Whisper respells the name and runs them in.
NAME_COMPANIONS = {'a', 'an', 'the', 'of', 'de', 'la', 'le', 'du', 'von', 'van', 'his', 'her', 'its', 'and'}
# Unstressed words Whisper drops, adds and swaps on its own ("circling an invisible shape" as "in invisible shape").
SMALL_WORDS = {'a', 'an', 'the', 'of', 'in', 'on', 'at', 'to', 'as', 'and', 'or', 'for', 'by', 'with', 'from'}
CURRENCY = {'$': 'dollars', '£': 'pounds', '€': 'euros'}            # Whisper writes "four dollars, or five" as "$4 or $5"
MONTHS = {'january', 'february', 'march', 'april', 'may', 'june', 'july', 'august', 'september', 'october', 'november', 'december'}
# A chord's quality as a script spells it out and as a chord symbol shortens it (Dsus4 is "D sus four").
QUALITIES = {'sus': 'suspended', 'dim': 'diminished', 'aug': 'augmented'}
# Words of a chord's name: after one (or a note, or major/minor) an ordinal is the chord's number ("C diminished
# seventh" is Cdim7, "the flat sixth" b6).
CHORD_WORDS = {*ACCIDENTALS, *QUALITIES.values(), 'dominant', 'add'}
# Words of this project that Whisper hears as everyday ones, whatever the voice said: one for the other is no change.
LEXICON = {'torus': {'taurus', 'chorus', 'tourist'}, 'toruses': {'tauruses', 'choruses', 'tourists'}}
# A name heard as something that sounds this much like it (name_sound) is a respelling, not another word.
NAME_LIKENESS = .5
# A line whose duck floor leaves its voice less than this far above the music is listed (narration.FLOOR_WARNING_DB).
LEVEL_WARNING_DB = 6
Token = collections.namedtuple('Token', 'key kind text word')     # kind: word, note, mode or number


def fold(text):
    """Without accents (Étude, Etude), with plain quotes."""
    text = text.replace('’', "'").replace('‘', "'").replace('ʼ', "'")
    return ''.join(c for c in unicodedata.normalize('NFKD', text) if not unicodedata.combining(c))


def word_key(text):
    return re.sub(r"[^a-z0-9]", '', fold(text).lower())


def spoken_key(key):
    """A word's key as it is said: St. and Saint, Jr and Junior, sus and suspended are one."""
    return QUALITIES.get(key) or SPOKEN_ABBREVIATIONS.get(key, key)


def pieces(words):
    """(word index, text, starts a sentence, ends at a pause) for every piece of the words: hyphens and dashes split a
    word, edge punctuation goes, [tags] are not spoken, A′ is "A prime", a chord symbol is its root as written and its
    words (Bb7 is "Bb 7", Am7 "A minor 7"), letters run into digits are two pieces (PS1 is "PS 1", as "P.S. 1" is said,
    and the scale degree b6 is "flat 6"), and so is the article run into the note it names ("AC natural" is "a C
    natural").

    A dash between digits splits them and is not "to": Whisper writes "Kit 03" (said "oh three") as "0-3", and where it
    stands for a "to" ("1829–1832"), that small word is tolerated anyway."""
    out, start, tag = [], True, False
    for w, word in enumerate(words):
        if word.startswith('['):
            tag = True
        if tag:
            tag = not word.endswith(']')
            continue
        said = fold(word.replace('″', ' double prime ').replace('′', ' prime ').replace('&', ' and ').replace('%', ' percent'))
        said = re.sub(r'(?<![A-Za-z0-9])#(?=\d)', 'number ', said)             # "#3" is "number 3"; G#3 a note, C7#9 a chord
        said = re.sub(r'([$£€])(\d+(?:[,.]\d+)*)', lambda m: m.group(2) + ' ' + CURRENCY[m.group(1)], said)
        parts = []                                                               # (text, may start a sentence)
        for part in (p.strip(STRIP) for p in DASHES.split(said)):
            if not part:
                continue
            chord = chord_words(part)
            run = re.fullmatch(r'([A-Za-z]+)(\d+(?:st|nd|rd|th|s)?)', part)
            if chord:                     # a chord's letter is a note even where a sentence starts ("A7 resolves")
                # Its root as written: "Abm7" is A flat minor 7, or "a Bm7" run together (tolerated()).
                parts += [(c, False) for c in ([part[:2]] + chord[2:] if part[1:2] in SIGNS else chord)]
            elif run and run.group(1) == 'b':                                   # "the b6th": a flat scale degree
                parts += [('flat', True), (run.group(2), True)]
            else:
                parts += [(p, True) for p in (run.groups() if run else [part])]
        pause = bool(re.search(r'[,;:.!?][\'"”)\]]*$', said))
        out += [(w, p, start and n == 0 and may, pause and n == len(parts)-1) for n, (p, may) in enumerate(parts)]
        if parts:
            start = bool(re.search(r'[.!?][\'"”)\]]*$', said))
    # "a C natural" written "AC natural": the article run into the note it names, where what follows makes it a note.
    for n in range(len(out)-2, -1, -1):
        w, p, initial, pause = out[n]
        if re.fullmatch(r'A[B-G]', p) and word_key(out[n+1][1]) in MARKS:
            out[n:n+1] = [(w, 'a', initial, False), (w, p[1], False, pause)]
    return out


def figure(value):
    """A number as compared, from an int or a Decimal: 2.50 is 2.5, 2.5 million 2500000."""
    return format(Decimal(value).normalize(), 'f')


def number_value(text):
    """A number written in digits: (value, suffix), suffix 'th' for an ordinal (6th), 'ths' for intervals (6ths,
    3rds, and 5th's as Whisper also writes "fifths"), 's' for a decade (1830s, 90's); None for anything else. A leading
    zero stays ("03" is said "oh three"): numbers are compared without it where they differ (tolerated)."""
    m = re.fullmatch(r"(\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)(?:(st|nd|rd|th)('?s)?|'?(s))?", text.lower())
    if not m:
        return None
    value = m.group(1).replace(',', '')
    value = figure(value) if '.' in value else value
    return value, ('th' + ('s' if m.group(3) else '') if m.group(2) else m.group(4) or '')


def spelled(words):
    """Numbers said in words, from the start of `words`: [(value, suffix, words used)], one per number. "two
    thousand and nine" is 2009, "eighteen thirty-two" (a year) 1832, "nineteen oh five" 1905, "oh three" 03,
    "twenty-first" 21st, "two three" two numbers, "sixths" the ordinal 6 in the plural, "the nineties" and "the
    eighteen hundreds" the decades 90s and 1800s, "two point five million" 2500000. Empty when the first word is not a
    number."""
    numbers, i = [], 0
    total = current = None
    last = suffix = None
    begin = zeros = 0

    def close():
        nonlocal total, current, last, suffix, zeros
        if current is not None or total is not None:
            numbers.append([(total or 0) + (current or 0), suffix or '', i - begin, last, zeros])
        total = current = last = suffix = None
        zeros = 0
    while i < len(words):
        w = words[i]
        plural = w.endswith('s') and w[:-1] in ORDINALS
        ordinal = ORDINALS.get(w[:-1] if plural else w)
        decade = TENS.get(w[:-3] + 'y') if w.endswith('ies') else None          # "the nineties"
        value = UNITS.get(w, TENS.get(w, ordinal if decade is None else decade))
        if w == 'and' and last == 'scale' and i+1 < len(words) and (words[i+1] in UNITS or words[i+1] in TENS):
            i += 1
            continue
        if w == 'oh' and i+1 < len(words) and 1 <= UNITS.get(words[i+1], 0) <= 9:   # a zero said "oh": 1905, 03
            close()
            begin, zeros = i, 1
            i += 1
            continue
        digit = lambda j: j < len(words) and (UNITS.get(words[j], 10) <= 9 or words[j] == 'oh')
        if w == 'point' and (current is not None or total is not None) and suffix is None and not zeros \
                and last != 'decimal' and digit(i+1):                     # "two point five": a digit at a time after it
            places = ''
            i += 1
            while digit(i):
                places += str(UNITS.get(words[i], 0))
                i += 1
            total, current, last = None, (total or 0) + (current or 0) + Decimal('0.' + places), 'decimal'
            continue
        if w in SCALES and suffix is None and (current is not None or total is None):
            if current is None:                                   # "a hundred", "a thousand"
                begin, current = i, 1
            if SCALES[w] == 100:
                current *= 100
            else:
                total, current = (total or 0) + current*SCALES[w], None
            last = 'scale'
            i += 1
            continue
        if w[:-1] in SCALES and w.endswith('s') and current is not None and suffix is None:   # "the eighteen hundreds"
            if SCALES[w[:-1]] == 100:
                current *= 100
            else:
                total, current = (total or 0) + current*SCALES[w[:-1]], None
            last, suffix = 'scale', 's'
            i += 1
            continue
        if value is None:
            break
        kind = 'ten' if w in TENS or decade or (ordinal and ordinal >= 20 and ordinal % 10 == 0) else 'unit'
        joins = (current is not None or total is not None) and suffix is None and not zeros and (
            (last == 'ten' and kind == 'unit' and value < 10) or (last == 'scale' and (current or 0) % 100 == 0 and value < 100))
        if not joins and not (zeros and current is None):
            close()
            begin = i
            current = 0
        current = (current or 0) + value
        last = kind if value < 100 else 'scale'
        if ordinal and decade is None:
            suffix = 'ths' if plural else 'th'
        if decade:
            suffix = 's'
        i += 1
        if zeros:                                                 # "oh three" is one digit
            close()
    close()
    # A year said in two halves: "eighteen thirty-two", "twenty twenty", "nineteen nineties", "nineteen oh five".
    merged = []
    for n in numbers:
        second = (10 <= n[0] <= 99 and not n[4] or 1 <= n[0] <= 9 and n[4] == 1) and n[3] != 'decimal'
        if merged and not merged[-1][1] and not merged[-1][4] and 10 <= merged[-1][0] <= 99 and second and merged[-1][3] not in ('scale', 'decimal'):
            merged[-1] = [merged[-1][0]*100 + n[0], n[1], merged[-1][2] + n[2], n[3], 0]
        else:
            merged.append(n)
    return [('0'*z + figure(v), s, used) for v, s, used, _, z in merged]


def tokens(words):
    """The words as compared: note names, major/minor and numbers as tokens of their own, every other word by its
    letters (no case, accents, apostrophes or punctuation); Op. and No. before a number are Opus and Number."""
    found = pieces(words)
    low = [word_key(t) for _, t, _, _ in found]
    out, k = [], 0
    while k < len(found):
        w, text, initial, _ = found[k]
        following = low[k+1] if k+1 < len(found) else ''
        letter, sign = None, None
        if re.fullmatch(r'[B-Gb-g]', text) or (text == 'A' and (following in MARKS or not initial)):
            letter = text.lower()
        elif re.fullmatch(r'[A-G][#♯b♭♮]', text):
            letter, sign = text[0].lower(), SIGNS[text[1]]
        elif low[k] in LETTER_SOUNDS and following in MARKS:
            letter = LETTER_SOUNDS[low[k]]
        if letter:
            accidental = sign or ACCIDENTALS.get(following, '')
            used = 2 if accidental and not sign else 1                       # the accidental said as the next word
            shown = ' '.join(p[1] for p in found[k:k+used])
            out.append(Token('note:' + letter + accidental, 'note', shown, w))
            k += used
            continue
        if low[k] in MODES:
            out.append(Token(low[k], 'mode', text, w))
            k += 1
            continue
        digits = number_value(text)
        if digits:
            scale = SCALES.get(low[k+1]) if k+1 < len(found) and not digits[1] else None
            if scale:                                               # "2 million" is two million, "2.5 million" too
                out.append(Token('#' + figure(Decimal(digits[0])*scale), 'number', text + ' ' + found[k+1][1], w))
                k += 2
                continue
            out.append(Token('#' + digits[0] + digits[1], 'number', text, w))
            k += 1
            continue
        # A number said in words runs to the next pause: "thirteen, fourteen" is two numbers, not the year 1314.
        stop = next((j+1 for j in range(k, len(found)) if found[j][3]), len(found))
        said = spelled(low[k:stop])
        if said:
            for value, suffix, used in said:
                out.append(Token('#' + value + suffix, 'number', ' '.join(p[1] for p in found[k:k+used]), found[k][0]))
                k += used
            continue
        key = spoken_key(low[k])
        if key in ('op', 'opus', 'no', 'nr', 'number') and k+1 < len(found) and (number_value(found[k+1][1]) or spelled(low[k+1:k+2])):
            key = 'opus' if key.startswith('op') else 'number'
        if key:
            out.append(Token(key, 'word', text, w))
        k += 1
    # A date as Whisper writes it: "the third of April", "April the third", "3rd April" and "3 April" are one day, its
    # number a cardinal.
    drop = set()
    for i, t in enumerate(out):
        if t.kind == 'number' and re.fullmatch(r'#\d{1,2}th', t.key):
            of_month = i+2 < len(out) and out[i+1].key == 'of' and out[i+2].key in MONTHS
            month_after = of_month or i+1 < len(out) and out[i+1].key in MONTHS
            month_before = i > 0 and out[i-1].key in MONTHS or i > 1 and out[i-1].key == 'the' and out[i-2].key in MONTHS
            if not (month_after or month_before):
                continue
            out[i] = t._replace(key=t.key[:-2])
            if of_month:
                drop.add(i+1)
            if i > 0 and out[i-1].key == 'the':
                drop.add(i-1)
    return [t for i, t in enumerate(out) if i not in drop]


def sound(letters):
    """A rough sound of English letters: spellings of one sound made one, and the vowels after the first letter
    dropped, so homophones (sighs/size, it sighs/its size, chords/cords, hymn/him, colour/color) meet."""
    s = re.sub(r'[^a-z]', '', letters)
    for a, b in (('tch', 'ch'), ('sch', 'sk'), ('ch', 'k'), ('ph', 'f'), ('gh', ''), ('ck', 'k'), ('qu', 'kw'),
                 ('wh', 'w'), ('kn', 'n'), ('wr', 'r'), ('mn', 'm'), ('x', 'ks'), ('c', 'k'), ('q', 'k'), ('z', 's'), ('y', 'i')):
        s = s.replace(a, b)
    s = s[:1] + re.sub(r'[aeiou]', '', s[1:])
    return re.sub(r'(.)\1+', r'\1', s)


def name_sound(letters):
    """A name as it sounds, loosely: spellings of one sound made one (ch, sh and s, as Chopin is said; ph and f),
    voiced and unvoiced pairs made one (b/p, d/t, g/k, v/f, z/s), m and n made one, h, w and doubled letters dropped.
    Its vowels stay: a respelling keeps them (Chopin as Shopa), another name mostly does not (Schumann)."""
    s = re.sub(r'[^a-z]', '', letters)
    for a, b in (('sch', 's'), ('tch', 's'), ('ch', 's'), ('sh', 's'), ('ph', 'f'), ('gh', ''), ('ck', 'k'), ('qu', 'kw'),
                 ('kn', 'n'), ('wr', 'r')):
        s = s.replace(a, b)
    s = re.sub(r'c(?=[eiy])', 's', s)
    s = ''.join(dict(c='k', q='k', x='ks', z='s', b='p', d='t', g='k', v='f', m='n', h='', w='', y='i').get(c, c) for c in s)
    return re.sub(r'(.)\1+', r'\1', s)


def sounds_alike(a, b):
    """Whether Whisper's spelling `b` could be the name `a` said: the same first sound (any vowel for a vowel) and
    much of the rest (Chopin heard as Sopa or Showpass, Gorlatch as Gorlach; not Chopin as Liszt or Schumann, Piano as
    Violin)."""
    x, y = name_sound(a), name_sound(b)
    first = lambda s: 'a' if s[:1] in 'aeiou' else s[:1]
    return bool(x and y) and first(x) == first(y) and difflib.SequenceMatcher(None, x, y, autojunk=False).ratio() >= NAME_LIKENESS


def as_note(run):
    """The note the article would be with what follows it ("a" A, "a sharp" A sharp), or None."""
    keys = [t.key for t in run]
    if keys[:1] != ['a'] or len(keys) > 2 or any(t.kind != 'word' for t in run):
        return None
    return 'note:a' + ACCIDENTALS[keys[1]] if len(keys) == 2 and keys[1] in ACCIDENTALS else 'note:a' if len(keys) == 1 else None


def critical_change(a, b):
    """Which of a note name, major/minor and a number differs between two runs of tokens, or ''. An interval without its
    plural is the same number: Whisper writes "Sevenths pile up", said with its s, as "7th pile up" (flagged)."""
    key = lambda t: re.sub(r'ths$', 'th', t.key) if t.kind == 'number' else t.key
    kinds = []
    for kind, label in (('note', 'note name'), ('mode', 'major/minor'), ('number', 'number')):
        if collections.Counter(key(t) for t in a if t.kind == kind) != collections.Counter(key(t) for t in b if t.kind == kind):
            kinds.append(label)
    return ', '.join(kinds)


def number_keys(run):
    return collections.Counter(t.key for t in run if t.kind == 'number')


def tolerated(a, b, names, before=None):
    """Why a difference Whisper makes on its own is no change at all, or None. `before` is the script's token before
    the difference."""
    small = lambda run: len(run) == 1 and run[0].kind == 'word' and run[0].key in SMALL_WORDS
    if (small(a) or not a) and (small(b) or not b):
        return 'small word'
    if not a or not b:
        money = all(t.key in CURRENCY.values() for t in a or b)
        return 'currency' if money and before is not None and before.kind == 'number' else None
    # "A major" written "a major", or the article heard as the note A: one sound, and Whisper guesses the case.
    if len(a) == 1 and a[0].kind == 'note' and as_note(b) == a[0].key or len(b) == 1 and b[0].kind == 'note' and as_note(a) == b[0].key:
        return 'case'
    # "a B minor seven" written "Abm7b5" (or "Ab"): the article run into the note B, which reads as A flat.
    if [t.key for t in a] == ['a', 'note:b'] and len(b) == 1 and b[0].kind == 'note' and b[0].text.startswith('Ab'):
        return 'article run in'
    # "C now glows blue" written "See now glows blue": a note's letter as the word that sounds like it.
    if len(a) == len(b) == 1 and {a[0].kind, b[0].kind} == {'note', 'word'}:
        note, word = (a[0], b[0]) if a[0].kind == 'note' else (b[0], a[0])
        if LETTER_SOUNDS.get(word.key) == note.key[len('note:'):]:
            return 'sounds the same'
    if len(a) == len(b) == 1 and {a[0].kind, b[0].kind} == {'number', 'word'}:
        number, word = (a[0], b[0]) if a[0].kind == 'number' else (b[0], a[0])
        return 'sounds the same' if word.key in NUMBER_SOUNDS.get(number.key, ()) else None
    # "G seventh" written "G7", "the flat sixth" "b6": after a note or a word of a chord's name, an ordinal is the
    # chord's number. Elsewhere "the twelfth bar" said "the twelve bar" is a changed number.
    ordinal = lambda t: re.sub(r'th$', '', t.key)
    if (len(a) == len(b) == 1 and a[0].kind == b[0].kind == 'number' and ordinal(a[0]) == ordinal(b[0])
            and before is not None and (before.kind in ('note', 'mode') or before.key in CHORD_WORDS)):
        return 'chord symbol'
    # "Blink one eighty-two" is 182: the same digits, grouped as they are said; "Kit 03" is "0-3", "3" and "oh, three".
    digits = lambda run: ''.join('0' if t.key == 'oh' else t.key[1:] for t in run).lstrip('0')
    if all(re.fullmatch(r'#\d+', t.key) or t.key == 'oh' for t in a+b) and digits(a) == digits(b):
        return 'number grouping'
    if critical_change(a, b) or number_keys(a) != number_keys(b):     # an interval heard without its plural is flagged
        return None
    joined_a, joined_b = ''.join(t.key for t in a), ''.join(t.key for t in b)
    if joined_a == joined_b:
        return 'spacing'
    if len(a) == len(b) == 1 and (joined_a+'s' == joined_b or joined_b+'s' == joined_a):
        return 'sounds the same'                                              # it/its, toward/towards
    if len({t.word for t in a}) <= 3 and len({t.word for t in b}) <= 3 and sound(joined_a) == sound(joined_b):
        return 'sounds the same'
    if len(a) == len(b) == 1 and (b[0].key in LEXICON.get(a[0].key, ()) or a[0].key in LEXICON.get(b[0].key, ())):
        return 'project word'
    if (len(b) <= len(a)+1 and all(t.kind == 'word' for t in a+b) and any(t.key in names for t in a)
            and all(t.key in names or t.key in NAME_COMPANIONS for t in a) and sounds_alike(joined_a, joined_b)):
        return 'name'
    return None


def number_as_a_word(a, b):
    """Whether Whisper wrote words, and no number, that sound like the script's number: "Fifths" as Fists, Fifts or
    Fifthes, "four chord" as forecourt. A voice that changed the number says another one, which Whisper writes as a
    number or as a word that sounds like one ("fifth" heard as "forth" stays a changed number), and a number heard as
    a small word ("then two swells" as "then the swells") is a number gone."""
    if not b or len(a) > 3 or len(b) > 3 or any(t.kind != 'word' for t in b) or any(t.kind in ('note', 'mode') for t in a):
        return False
    if any(t.key in sounds for t in b for sounds in NUMBER_SOUNDS.values()) or all(t.key in SMALL_WORDS for t in b):
        return False
    said = ''.join(w for t in a for piece in t.text.split() for w in (number_words(piece) or [word_key(piece)]))
    return sounds_alike(said, ''.join(t.key for t in b))


def repeats(keys):
    """Every run said again straight after itself, longest first at each place, up to a whole sentence or line:
    [(start of the second copy, length)]."""
    found, k = [], 0
    while k < len(keys):
        n = next((n for n in range((len(keys)-k)//2, 0, -1) if keys[k:k+n] == keys[k+n:k+2*n]), 0)
        if n:
            found.append((k+n, n))
        k += n or 1
    return found


def compare(script_words, heard_text, names=()):
    """What a line's voice changed from its script: issues (each critical or not) and the differences tolerated."""
    script, heard = tokens(script_words), tokens(heard_text.split())
    issues, forgiven = [], []
    # A phrase the voice says twice where the script says it once. Its second copy is set aside before comparing.
    own = collections.Counter(tuple(t.key for t in script[a:a+n]) for a, n in repeats([t.key for t in script]))
    drop = set()
    for a, n in repeats([t.key for t in heard]):
        phrase = tuple(t.key for t in heard[a:a+n])
        if own[phrase]:
            own[phrase] -= 1
            continue
        copy = heard[a:a+n]
        issues.append(dict(kind='repeated', critical=n >= 2 or bool(critical_change(copy, [])),
                           why='repeated phrase' if n >= 2 else 'repeated word', script='', heard=' '.join(t.text for t in copy),
                           first=a-n))
        drop.update(range(a, a+n))
    kept = [k for k in range(len(heard)) if k not in drop]
    voice = [heard[k] for k in kept]
    matcher = difflib.SequenceMatcher(None, [t.key for t in script], [t.key for t in voice], autojunk=False)
    # Where each heard token sits in the script, to place a repeat in time. The aligner put the phrase's script words on
    # one copy or the other, with a pause between them or not: both copies come after the script word before the
    # phrase, so a repeat is heard from that word's start (or the line's).
    place = {kept[j+d]: i+d for i, j, n in matcher.get_matching_blocks() for d in range(n)}
    for issue in issues:
        first = place.get(issue.pop('first'))
        before = script[first].word - 1 if first is not None else -1
        issue.update(word=before if before >= 0 else None, end=False)
    for tag, i1, i2, j1, j2 in matcher.get_opcodes():
        if tag == 'equal':
            continue
        a, b = script[i1:i2], voice[j1:j2]
        shown = dict(script=' '.join(t.text for t in a), heard=' '.join(t.text for t in b))
        why = tolerated(a, b, names, script[i1-1] if i1 else None)
        if why:
            forgiven.append(dict(shown, why=why))
            continue
        change = critical_change(a, b)
        kind = dict(replace='changed', delete='missing', insert='extra')[tag]
        at = dict(word=a[0].word, end=False) if a else dict(word=script[i1-1].word if i1 else None, end=bool(i1))
        if change == 'number' and number_as_a_word(a, b):
            # Whisper wrote a word that sounds like the number ("Fifths" as Fists): the voice said no other number, and
            # a new take cannot change how Whisper spells it. Worth a listen.
            issues.append(dict(kind=kind, critical=False, why='number heard as a word', **shown, **at))
            continue
        plural = not change and number_keys(a) != number_keys(b)
        issues.append(dict(kind=kind, critical=bool(change), why=change or ('plural' if plural else kind+' words'), **shown, **at))
    status = 'critical' if any(i['critical'] for i in issues) else 'flagged' if issues else 'ok'
    return dict(status=status, issues=issues, tolerated=forgiven)


def story_names(story):
    """Words Whisper cannot be expected to spell: names (capitalised inside a sentence somewhere in the story's speech
    or captions, and never written in lower case: "the piano" makes the Piano of "Leeds International Piano
    Competition" a word) and foreign words (accented in the script: étude, più)."""
    capital, lower, foreign = set(), set(), set()
    for cue in story['cues']:
        for text in (cue.get('speech'), cue.get('text')):
            words = (text or '').split()
            for _, piece, initial, _ in pieces(words):
                key = spoken_key(word_key(piece))
                if len(key) > 1 and not key.isdigit():
                    if piece[:1].islower():
                        lower.add(key)
                    elif piece[:1].isupper() and not initial:
                        capital.add(key)
            foreign.update(word_key(w) for w in words if any(unicodedata.combining(c) for c in unicodedata.normalize('NFKD', w)))
    return (capital - lower | foreign) - set(MODES)


def whisper(clips, device='auto'):
    """Whisper's text for every clip (16 kHz float32), offline."""
    os.environ['HF_HUB_OFFLINE'] = os.environ['TRANSFORMERS_OFFLINE'] = '1'      # read before transformers loads
    import warnings
    warnings.filterwarnings('ignore')
    import torch
    import transformers
    transformers.logging.set_verbosity_error()
    if device == 'auto':
        device = 'cuda:0' if torch.cuda.is_available() else 'cpu'
    asr = transformers.pipeline('automatic-speech-recognition', model=MODEL, device=device, chunk_length_s=30,
                                torch_dtype=torch.float16 if device.startswith('cuda') else torch.float32)
    return [r['text'].strip() for r in asr([dict(raw=c, sampling_rate=RATE) for c in clips], batch_size=8)]


def transcribe(clips, rate, cache, device='auto'):
    """Whisper's text for every clip (int16 samples at `rate`), cached by the clip's samples: when every line is in the
    cache the model is never loaded."""
    import numpy as np
    from scipy.signal import resample_poly
    cache = Path(cache)
    keys = [hashlib.sha256(f'{MODEL}|{DECODER}|{rate}|'.encode() + np.ascontiguousarray(c).tobytes()).hexdigest() for c in clips]
    texts = [(read_json(cache/(k+'.json')) or {}).get('text') for k in keys]
    todo = [i for i, t in enumerate(texts) if t is None]
    if todo:
        g = math.gcd(RATE, rate)
        heard = whisper([resample_poly(clips[i].astype('float32')/32768, RATE//g, rate//g).astype('float32') for i in todo], device)
        for i, text in zip(todo, heard):
            texts[i] = text
            atomic_json(cache/(keys[i]+'.json'), dict(model=MODEL, decoder=DECODER, text=text))
    return texts, len(todo)


def clock(seconds):
    return f'{int(seconds//60)}:{seconds % 60:04.1f}'


def check(bundle, out=None, cache=None, device='auto'):
    """Check a bundle's narration against its story; writes narration-check.txt and .json, returns the report."""
    import soundfile as sf
    bundle = Path(bundle).resolve()
    story, narration = read_json(bundle/'story.json'), read_json(bundle/'narration.json')
    if not story or not narration:
        raise ValueError('The bundle has no story or no narration to check')
    if narration['storySha256'] != sha(bundle/'story.json') or narration['sha256'] != sha(bundle/narration['audioPath']):
        raise ValueError('The narration does not match the current story: render it again before checking it')
    signal, rate = sf.read(bundle/narration['audioPath'], dtype='int16')
    if signal.ndim > 1:
        signal = signal[:, 0]
    segments = narration['segments']
    texts, heard = transcribe([signal[round(s['start']*rate):round(s['end']*rate)] for s in segments], rate,
                              cache or DATA/'narration-check-cache', device)
    names = story_names(story)
    lines = []
    for segment, text in zip(segments, texts):
        cue = story['cues'][segment['cue']]
        words = spoken_words(cue)
        result = compare(words, text, names)
        starts, ends = segment.get('spokenStarts'), segment.get('spokenEnds')
        timed = isinstance(starts, list) and isinstance(ends, list) and len(starts) == len(ends) == len(words)
        for issue in result['issues']:
            word, end = issue.pop('word'), issue.pop('end')
            at = (ends if end else starts)[word] if timed and word is not None else 0
            issue['at'] = round(segment['start'] + at, 2) if timed else None
        # The voice may be right and still lost: a duck floor that keeps the music up leaves it this far above it.
        level = segment.get('speechOverMusicDb')
        low = {'speechOverMusicDb': level} if isinstance(level, (int, float)) and level < LEVEL_WARNING_DB else {}
        lines.append(dict(cue=segment['cue'], take=cue.get('take', 0), start=round(segment['start'], 3), end=round(segment['end'], 3),
                          script=' '.join(words), heard=text, **result, **low))
    report = dict(version=1, bundle=bundle.name, narration=narration['audioPath'], narrationSha256=narration['sha256'],
                  storySha256=narration['storySha256'], model=MODEL, heardNow=heard,
                  critical=[l['cue'] for l in lines if l['status'] == 'critical'],
                  flagged=[l['cue'] for l in lines if l['status'] == 'flagged'],
                  buried=[l['cue'] for l in lines if 'speechOverMusicDb' in l], lines=lines)
    out = Path(out) if out else ROOT/'Reports/SongProcessing'/bundle.name
    out.mkdir(parents=True, exist_ok=True)
    atomic_json(out/'narration-check.json', report)
    (out/'narration-check.txt').write_text(readable(report), encoding='utf-8')
    report['files'] = [str(out/'narration-check.txt'), str(out/'narration-check.json')]
    return report


def readable(report):
    lines = report['lines']
    ok = len(lines) - len(report['critical']) - len(report['flagged'])
    which = lambda cues: ' (cue ' + ', '.join(map(str, cues)) + ')' if cues else ''
    text = [f"Narration check: {report['bundle']}",
            f"{report['narration']}, heard by {report['model']} ({report['heardNow']} of {len(lines)} lines heard now, the rest cached)",
            f"{len(lines)} spoken lines: {len(report['critical'])} critical{which(report['critical'])}, "
            f"{len(report['flagged'])} flagged{which(report['flagged'])}, {ok} as written"]
    buried = report.get('buried')
    if buried:
        text.append(f"Under {LEVEL_WARNING_DB} dB above the music, where a duckFloor keeps it up: {len(buried)} line"
                    f"{'s' if len(buried) > 1 else ''}{which(buried)}. Words get lost there: a lower floor, or the line "
                    "somewhere quieter.")
    text.append('')
    for line in lines:
        head = f"{line['status'].upper():9}cue {line['cue']}, {clock(line['start'])}-{clock(line['end'])}" + (f", take {line['take']}" if line['take'] else '')
        if 'speechOverMusicDb' in line:
            head += f", voice {line['speechOverMusicDb']:.1f} dB above the music"
        forgiven = '; '.join(f"{t['script']} / {t['heard']} ({t['why']})" for t in line['tolerated'])
        if line['status'] == 'ok':
            text.append(head + (f'   tolerated: {forgiven}' if forgiven else ''))
            continue
        text += [head, f"  script: {line['script']}", f"  heard:  {line['heard']}"]
        for issue in line['issues']:
            at = f" at {clock(issue['at'])}" if issue['at'] is not None else ''
            if issue['kind'] == 'repeated':
                said = f'"{issue["heard"]}" said twice'
            elif issue['kind'] == 'changed':
                said = f'"{issue["script"]}" heard as "{issue["heard"]}"'
            elif issue['kind'] == 'missing':
                said = f'"{issue["script"]}" not heard'
            else:
                said = f'"{issue["heard"]}" added'
            text.append(f"  {'!' if issue['critical'] else '-'} {issue['why']}: {said}{at}")
        if forgiven:
            text.append(f'  tolerated: {forgiven}')
        text.append(f"  Listen to it. A new take renders only this line again: \"take\": {line['take']+1} on cue {line['cue']}"
                    + ('; if every take says it, respell its speech.' if line['status'] == 'critical' else '.'))
    return '\n'.join(text) + '\n'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('bundle')
    parser.add_argument('--out', help='folder for narration-check.txt and .json (default Reports/SongProcessing/<bundle>)')
    parser.add_argument('--cache', help='transcription cache (default SongLibraryData/narration-check-cache)')
    parser.add_argument('--device', default='auto', help='auto (a GPU when there is one), cpu or cuda')
    a = parser.parse_args(argv)
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')    # a console in cp1252 cannot show É or ′
    try:
        report = check(a.bundle, a.out, a.cache, a.device)
    except Exception as error:
        # 2, not 1: nothing was found wrong with the voice, it could not be heard (a stale narration, a missing model, a
        # narration.json without the fields it needs, a fault here); `narration.py --check` passes 1 on as critical.
        print(f'Narration check could not run: {type(error).__name__}: {error}', file=sys.stderr)
        return 2
    print(readable(report) + 'Report: ' + report['files'][0])
    return 1 if report['critical'] else 0


if __name__ == '__main__':
    sys.exit(main())
