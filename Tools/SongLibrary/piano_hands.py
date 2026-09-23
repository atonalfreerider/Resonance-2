"""Ground-truth-MIDI-guided left/right-hand piano stem preparation."""
import copy
import math
from collections import defaultdict, deque
from pathlib import Path

import mido
import numpy as np
import soundfile as sf
from scipy.ndimage import gaussian_filter

from common import MASTER_FIELDS, MASTER_SECTION_FIELDS, atomic_json, compile_patterns, read_json, sha, validate_bundle
from stems import copy_master_stem


HAND_NAMES = {'right-hand': 'Right hand', 'left-hand': 'Left hand'}


def timed_notes(path):
    """Read note times without merging authored MIDI tracks."""
    midi = mido.MidiFile(path)
    events = []
    for track_index, track in enumerate(midi.tracks):
        tick = 0
        for order, message in enumerate(track):
            tick += message.time
            events.append((tick, track_index, order, message))
    events.sort(key=lambda event: event[:3])
    absolute_seconds = 0.0
    previous_tick = 0
    tempo = 500000
    pending = defaultdict(deque)
    notes = []
    for tick, track_index, _, message in events:
        absolute_seconds += mido.tick2second(tick-previous_tick, midi.ticks_per_beat, tempo)
        previous_tick = tick
        if message.type == 'set_tempo':
            tempo = message.tempo
        if message.type not in ('note_on', 'note_off') or message.channel == 9:
            continue
        key = (track_index, message.channel, message.note)
        if message.type == 'note_on' and message.velocity:
            pending[key].append((absolute_seconds, message.velocity))
        elif pending[key]:
            start, velocity = pending[key].popleft()
            notes.append(dict(track=track_index, pitch=message.note, velocity=velocity,
                              start=start, end=max(start+.001, absolute_seconds)))
    if any(pending.values()):
        raise ValueError('Piano score has unmatched note-on events; repair it before hand separation.')
    return midi, notes


def detect_hand_tracks(path):
    """Require two authored pitched tracks and identify hands by pitch register."""
    midi, notes = timed_notes(path)
    by_track = defaultdict(list)
    for note in notes:
        by_track[note['track']].append(note['pitch'])
    if len(by_track) != 2:
        summary = ', '.join(f'{track}:{len(pitches)}' for track, pitches in sorted(by_track.items()))
        raise ValueError('Piano-hand mode requires exactly two authored pitched MIDI tracks; found '+summary)
    ranked = sorted(by_track, key=lambda track: (float(np.median(by_track[track])), float(np.mean(by_track[track]))))
    left, right = ranked
    if np.median(by_track[right])-np.median(by_track[left]) < 7:
        raise ValueError('The two MIDI tracks do not have clearly separated left/right-hand registers.')
    names = [next((message.name for message in track if message.type == 'track_name'), '')
             for track in midi.tracks]
    return dict(right=right, left=left, notes=notes, trackNames=names,
                rightMedian=float(np.median(by_track[right])), leftMedian=float(np.median(by_track[left])),
                rightNotes=len(by_track[right]), leftNotes=len(by_track[left]))


def label_hand_tracks(path, hands):
    """Label only the derived aligned MIDI; the supplied ground truth stays untouched."""
    path = Path(path)
    midi = mido.MidiFile(path)
    labels = {hands['right']: 'Right hand', hands['left']: 'Left hand'}
    for track_index, label in labels.items():
        track = midi.tracks[track_index]
        existing = next((message for message in track if message.type == 'track_name'), None)
        if existing is not None:
            existing.name = label
        else:
            track.insert(0, mido.MetaMessage('track_name', name=label, time=0))
    temporary = path.with_suffix(path.suffix+'.tmp')
    midi.save(temporary)
    temporary.replace(path)


def _note_template(pitch, frequencies):
    fundamental = 440.0*2**((pitch-69)/12)
    template = np.zeros(len(frequencies), dtype=np.float32)
    bin_hz = frequencies[1]-frequencies[0]
    harmonic = 1
    while fundamental*harmonic < frequencies[-1]:
        center = fundamental*harmonic*math.sqrt(1+0.00012*harmonic*harmonic)
        width = max(bin_hz*1.35, center*.0035)
        lo = max(0, int((center-3*width)/bin_hz))
        hi = min(len(frequencies), int((center+3*width)/bin_hz)+1)
        if hi > lo:
            template[lo:hi] += np.exp(-.5*((frequencies[lo:hi]-center)/width)**2).astype(np.float32)/(harmonic**1.15)
        harmonic += 1
    maximum = float(template.max())
    return template/maximum if maximum else template


def _hand_guide(notes, frames, sample_rate, n_fft, hop):
    frequencies = np.fft.rfftfreq(n_fft, 1/sample_rate)
    guide = np.zeros((len(frequencies), frames), dtype=np.float32)
    templates = {}
    broadband = (1/(1+(frequencies/4200)**2)).astype(np.float32)
    for note in notes:
        start = max(0, int(math.floor(note['start']*sample_rate/hop))-1)
        release_start = min(frames, int(math.ceil(note['end']*sample_rate/hop))+1)
        end = min(frames, release_start+int(math.ceil(1.35*sample_rate/hop)))
        if end <= start:
            continue
        velocity = max(.08, note['velocity']/127)
        envelope = np.ones(end-start, dtype=np.float32)*velocity
        attack_frames = min(len(envelope), max(1, int(round(.025*sample_rate/hop))))
        envelope[:attack_frames] *= np.linspace(.45, 1, attack_frames, dtype=np.float32)
        if release_start > start and release_start < end:
            tail = np.arange(end-release_start, dtype=np.float32)*hop/sample_rate
            envelope[release_start-start:] *= np.exp(-tail/.48)
        template = templates.setdefault(note['pitch'], _note_template(note['pitch'], frequencies))
        guide[:, start:end] += template[:, None]*envelope[None, :]
        transient_end = min(end, start+max(1, int(round(.07*sample_rate/hop))))
        decay = np.linspace(1, .08, transient_end-start, dtype=np.float32)
        guide[:, start:transient_end] += broadband[:, None]*(velocity*.055)*decay[None, :]
    return guide


def separate_recording(recording, output, hands, n_fft=4096, hop=1024):
    """Create complementary sample-aligned hand stems from score-informed masks."""
    import librosa
    recording, output = Path(recording), Path(output)
    output.mkdir(parents=True, exist_ok=True)
    audio, sample_rate = sf.read(recording, always_2d=True, dtype='float32')
    mono = audio.mean(axis=1)
    reference = librosa.stft(mono, n_fft=n_fft, hop_length=hop, win_length=n_fft, center=True)
    frames = reference.shape[1]
    right_notes = [note for note in hands['notes'] if note['track'] == hands['right']]
    left_notes = [note for note in hands['notes'] if note['track'] == hands['left']]
    right = _hand_guide(right_notes, frames, sample_rate, n_fft, hop)
    left = _hand_guide(left_notes, frames, sample_rate, n_fft, hop)
    total = right+left
    right_activity = right.sum(axis=0)
    left_activity = left.sum(axis=0)
    activity_total = right_activity+left_activity
    temporal_share = np.divide(right_activity, activity_total,
                               out=np.full(frames, .5, dtype=np.float32), where=activity_total>1e-8)
    floor = np.maximum(total.max(axis=0)*.012, 1e-7)
    mask = (right+floor[None, :]*temporal_share[None, :])/(total+floor[None, :])
    mask = np.clip(gaussian_filter(mask, sigma=(.7, 1.15), mode='nearest'), 0, 1).astype(np.float32)
    right_audio = np.empty_like(audio)
    for channel in range(audio.shape[1]):
        spectrum = librosa.stft(audio[:, channel], n_fft=n_fft, hop_length=hop, win_length=n_fft, center=True)
        right_audio[:, channel] = librosa.istft(
            spectrum*mask, hop_length=hop, win_length=n_fft, center=True, length=len(audio)).astype(np.float32)
    left_audio = audio-right_audio
    sf.write(output/'right-hand.wav', right_audio, sample_rate, subtype='FLOAT')
    sf.write(output/'left-hand.wav', left_audio, sample_rate, subtype='FLOAT')
    reread_right, _ = sf.read(output/'right-hand.wav', always_2d=True, dtype='float32')
    reread_left, _ = sf.read(output/'left-hand.wav', always_2d=True, dtype='float32')
    reconstruction = float(np.max(np.abs((reread_right+reread_left)-audio)))
    report = dict(method='ground-truth-midi-guided-complementary-stft-mask',
                  sampleRate=sample_rate, samples=len(audio), channels=audio.shape[1],
                  fftSize=n_fft, hopSize=hop, rightTrack=hands['right'], leftTrack=hands['left'],
                  rightMedianPitch=hands['rightMedian'], leftMedianPitch=hands['leftMedian'],
                  rightNotes=len(right_notes), leftNotes=len(left_notes),
                  instrumentSumMaxError=reconstruction,
                  qualityWarnings=['Hands share one piano and room response; overlapping partials and pedal resonance can leak between solos.'])
    atomic_json(output/'separation.json', report)
    return report


def _compile_hand_scores(bundle, hands):
    bundle = Path(bundle)
    master = read_json(bundle/'aligned.mid.patterns.json')
    folder = bundle/'stems'
    entries = []
    mapping = {'right-hand': [hands['right']], 'left-hand': [hands['left']]}
    for identifier, label in HAND_NAMES.items():
        score = folder/(identifier+'.mid')
        copy_master_stem(bundle/'aligned.mid', score, mapping, identifier)
        compile_patterns(score, bundle.parent/'piano-hands.log')
        patterns_path = Path(str(score)+'.patterns.json')
        patterns = read_json(patterns_path)
        for field in MASTER_FIELDS:
            if field in master:
                patterns[field] = copy.deepcopy(master[field])
        for section in patterns['Sections']:
            reference = next((item for item in master['Sections'] if item['Start'] == section['Start']), None)
            if reference:
                for field in MASTER_SECTION_FIELDS:
                    if field in reference:
                        section[field] = copy.deepcopy(reference[field])
        atomic_json(patterns_path, patterns)
        audio_path = folder/(identifier+'.wav')
        info = sf.info(audio_path)
        entries.append(dict(
            id=identifier, name=label,
            audioPath=str(audio_path.relative_to(bundle)).replace('\\','/'), audioSha256=sha(audio_path),
            midiPath=str(score.relative_to(bundle)).replace('\\','/'), midiSha256=sha(score),
            patternsPath=str(patterns_path.relative_to(bundle)).replace('\\','/'), patternsSha256=sha(patterns_path),
            samples=info.frames, sampleRate=info.samplerate, notes=len(patterns['Notes']),
            method='ground-truth-midi-guided-piano-hand'))
    return entries


def recompile_piano_hand_scores(bundle):
    """Rebuild hand visual scores after key/form review without separating audio again."""
    bundle = Path(bundle)
    mapping = read_json(bundle/'song.json', {}).get('PianoHandTracks', {})
    if set(mapping) != {'right-hand', 'left-hand'} or any(len(mapping[name]) != 1 for name in mapping):
        raise ValueError('Missing exact piano-hand track mapping.')
    hands = {'right': mapping['right-hand'][0], 'left': mapping['left-hand'][0]}
    manifest = read_json(bundle/'aligned.mid.prepared.json')
    manifest['stems'] = _compile_hand_scores(bundle, hands)
    manifest['defaultInstrumentStem'] = 'right-hand'
    atomic_json(bundle/'aligned.mid.prepared.json', manifest)
    validate_bundle(bundle)
    return manifest['stems']


def prepare_piano_hands(bundle, notify=lambda message: None):
    bundle = Path(bundle)
    validate_bundle(bundle)
    hands = detect_hand_tracks(bundle/'aligned.mid')
    notify(f"Detected right hand on track {hands['right']+1} and left hand on track {hands['left']+1}")
    label_hand_tracks(bundle/'aligned.mid', hands)
    settings = read_json(bundle/'song.json', {})
    settings.update(LeadVocalTrack=hands['right'], PianoHandTracks={
        'right-hand': [hands['right']], 'left-hand': [hands['left']]})
    atomic_json(bundle/'song.json', settings)
    notify('Recompiling the full score with Right hand as the default highlighted instrument')
    compile_patterns(bundle/'aligned.mid', bundle.parent/'piano-hands.log')
    manifest = read_json(bundle/'aligned.mid.prepared.json')
    manifest['midiSha256'] = sha(bundle/'aligned.mid')
    atomic_json(bundle/'aligned.mid.prepared.json', manifest)
    notify('Separating sample-aligned left/right-hand audio from the ground-truth score')
    report = separate_recording(bundle/'recording.wav', bundle/'stems', hands)
    notify('Compiling exact authored MIDI and pattern views for both hands')
    manifest = read_json(bundle/'aligned.mid.prepared.json')
    manifest['stems'] = _compile_hand_scores(bundle, hands)
    manifest['defaultInstrumentStem'] = 'right-hand'
    atomic_json(bundle/'aligned.mid.prepared.json', manifest)
    validate_bundle(bundle)
    return report
