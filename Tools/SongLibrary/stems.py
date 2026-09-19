"""Offline stem bundle compilation. Full-song harmony stays authoritative during solo."""
import copy
import shutil
import uuid
from common import *

NAMES={'vocals':'Vocals','bass':'Bass','drums':'Drums / rhythm','other':'Accompaniment',
       'other-high':'High-register accompaniment','other-low':'Low-register accompaniment','instruments':'All instruments'}

def filter_score(source, output, kind):
    import pretty_midi
    score=pretty_midi.PrettyMIDI(str(source))
    for instrument in score.instruments:
        if kind=='drums':
            if not instrument.is_drum:instrument.notes=[]
        else:
            if instrument.is_drum: instrument.notes=[];continue
            if kind=='other-high':instrument.notes=[n for n in instrument.notes if n.pitch>=60]
            if kind=='other-low':instrument.notes=[n for n in instrument.notes if n.pitch<60]
        if kind=='vocals':instrument.name='Lead Vocals'
    score.instruments=[i for i in score.instruments if i.notes]
    score.write(str(output))

def map_to_master(source, destination, master_midi):
    """Use the exact master tempo/meter map; never warp stem note onsets."""
    import bisect
    import mido
    master=mido.MidiFile(master_midi)
    ppq=min(32760,master.ticks_per_beat*16)
    tempo_events=[];clock_events=[];absolute=0;seconds=0.;last=0;tempo=500000
    for msg in mido.merge_tracks(master.tracks):
        absolute+=msg.time
        if msg.type=='set_tempo':
            seconds+=mido.tick2second(absolute-last,master.ticks_per_beat,tempo);last=absolute;tempo=msg.tempo
            tempo_events.append((seconds,absolute/master.ticks_per_beat,tempo))
        if msg.type in ('set_tempo','time_signature','key_signature'):
            clock_events.append((round(absolute/master.ticks_per_beat*ppq),msg))
    tempo_events.insert(0,(0.,0.,500000));times=[t[0] for t in tempo_events]
    def ticks(sec):
        t,beat,us=tempo_events[bisect.bisect_right(times,sec)-1]
        return round((beat+(sec-t)*1e6/us)*ppq)
    result=mido.MidiFile(ticks_per_beat=ppq);clock=mido.MidiTrack();result.tracks.append(clock);last=0
    for tick,msg in clock_events:clock.append(msg.copy(time=tick-last));last=tick
    clock.append(mido.MetaMessage('end_of_track',time=max(0,round(absolute/master.ticks_per_beat*ppq)-last)))
    # pretty_midi preserves physical timestamps and instrument/drum classification.
    import pretty_midi
    parsed=pretty_midi.PrettyMIDI(str(source))
    for index,instrument in enumerate(parsed.instruments):
        channel=9 if instrument.is_drum else [i for i in range(16) if i!=9][index%15]
        track=mido.MidiTrack([mido.MetaMessage('track_name',name=instrument.name),mido.Message('program_change',channel=channel,program=instrument.program)])
        events=[]
        for note in instrument.notes:
            events.append((ticks(note.start),1,mido.Message('note_on',channel=channel,note=note.pitch,velocity=note.velocity)))
            events.append((max(ticks(note.start)+1,ticks(note.end)),0,mido.Message('note_off',channel=channel,note=note.pitch)))
        last=0
        for tick,_,msg in sorted(events,key=lambda e:(e[0],e[1])):track.append(msg.copy(time=tick-last));last=tick
        result.tracks.append(track)
    result.save(destination)

def prepare_stems(bundle, notify=lambda x:None):
    bundle=Path(bundle); validate_bundle(bundle)
    folder=bundle/'stems';folder.mkdir(exist_ok=True);log=bundle.parent/'stems.log'
    notify('Separating vocals, bass, drums and accompaniment locally with Demucs')
    run([MODEL_PYTHON,ROOT/'Tools/SongLibrary/separate.py','--audio',bundle/'recording.wav','--output',folder],log)
    for warning in read_json(folder/'separation.json',{}).get('qualityWarnings',[]):notify(warning)
    if read_json(bundle/'song.json',{}).get('StemTracks'):
        notify('Using the existing reviewed MIDI for stem notes; no neural retranscription')
        return compile_stems(bundle,notify)
    jobs=[dict(audio=str((folder/(name+'.wav')).resolve()),output=str((folder/(name+'-raw.mid')).resolve()))
          for name in ('vocals','bass','drums','other')]
    request=bundle.parent/'stem-transcription-jobs.json';atomic_json(request,jobs)
    notify('Transcribing four isolated stems locally (YourMT3); preserving their recording timestamps')
    run([MODEL_PYTHON,ROOT/'Tools/SongLibrary/transcribe.py','--batch',request],log)
    return compile_stems(bundle,notify)


def compile_stems(bundle,notify=lambda x:None):
    import pretty_midi
    import soundfile as sf
    bundle=Path(bundle);master=read_json(bundle/'aligned.mid.patterns.json')
    folder=bundle/'stems';log=bundle.parent/'stems.log'
    settings=read_json(bundle/'song.json',{})
    mapping=settings.get('StemTracks')
    if not mapping:
        for name in ('vocals','bass','drums','other'):
            filter_score(folder/(name+'-raw.mid'),folder/(name+'-notes.mid'),name)
        for name in ('other-high','other-low'):
            filter_score(folder/'other-notes.mid',folder/(name+'-notes.mid'),name)
        instruments=pretty_midi.PrettyMIDI()
        for name in ('bass','drums','other'):
            instruments.instruments.extend(pretty_midi.PrettyMIDI(str(folder/(name+'-notes.mid'))).instruments)
        instruments.write(str(folder/'instruments-notes.mid'))
    settings.update(Key=master['Key'],Minor=master['Minor'],KeySource=master['KeySource'],LeadVocalTrack=-1,
        SectionBoundaries='\n'.join(f"{s['FirstBar']+1} {s['Name']}" for s in master['Sections']))
    atomic_json(folder/'song.json',settings)
    entries=[]
    for name,label in NAMES.items():
        notify('Compiling isolated pattern wheels: '+label)
        score=folder/(name+'.mid')
        if mapping:
            copy_master_stem(bundle/'aligned.mid',score,mapping,name)
        else:map_to_master(folder/(name+'-notes.mid'),score,bundle/'aligned.mid')
        compile_patterns(score,log)
        path=Path(str(score)+'.patterns.json');patterns=read_json(path)
        # Preserve the exact full-song harmony and extent, including silence in a stem.
        for field in ('Chords','RegionPhases','Key','Minor','KeySource','Duration','EndBeat'):
            patterns[field]=copy.deepcopy(master[field])
        for section in patterns['Sections']:
            reference=next((s for s in master['Sections'] if s['Start']==section['Start']),None)
            if reference:
                section['Chords']=copy.deepcopy(reference['Chords'])
                section['ProgressionBeats']=reference['ProgressionBeats']
        atomic_json(path,patterns)
        audio=folder/(name+'.wav');info=sf.info(audio)
        entries.append(dict(id=name,name=label,audioPath=str(audio.relative_to(bundle)).replace('\\','/'),
            audioSha256=sha(audio),midiPath=str(score.relative_to(bundle)).replace('\\','/'),midiSha256=sha(score),
            patternsPath=str(path.relative_to(bundle)).replace('\\','/'),patternsSha256=sha(path),
            samples=info.frames,sampleRate=info.samplerate,notes=len(patterns['Notes']),
            method='existing-reviewed-midi' if mapping else 'derived-register-view' if name.startswith('other-') else 'local-demucs-yourmt3'))
    manifest=read_json(bundle/'aligned.mid.prepared.json');manifest['stems']=entries
    atomic_json(bundle/'aligned.mid.prepared.json',manifest)
    validate_bundle(bundle)
    return entries

def copy_master_stem(source,destination,mapping,name):
    """Filter existing MIDI events while preserving track IDs and absolute tick times."""
    import mido
    source=mido.MidiFile(source);out=mido.MidiFile(type=source.type,ticks_per_beat=source.ticks_per_beat)
    tracks=mapping.get('other' if name.startswith('other-') else name,[])
    if name=='instruments':tracks=sum((mapping.get(k,[]) for k in ('bass','drums','other')),[])
    for index,track in enumerate(source.tracks):
        dest=mido.MidiTrack();pending=0
        for msg in track:
            pending+=msg.time
            if msg.type in ('note_on','note_off','polytouch'):
                if index not in tracks:continue
                if name=='other-high' and msg.note<60:continue
                if name=='other-low' and msg.note>=60:continue
            dest.append(msg.copy(time=pending));pending=0
        if pending:dest.append(mido.MetaMessage('end_of_track',time=pending))
        out.tracks.append(dest)
    out.save(destination)

def enrich(directory, notify=lambda x:None):
    directory=Path(directory).resolve();validate_bundle(directory)
    work=DATA/'jobs'/uuid.uuid4().hex/'bundle';work.parent.mkdir(parents=True)
    shutil.copytree(directory,work)
    entries=prepare_stems(work,notify)
    provenance=read_json(work/'library.json',{});provenance['stems']=read_json(work/'stems/separation.json')
    reviewed=bool(read_json(work/'song.json',{}).get('StemTracks'))
    provenance.setdefault('warnings',[]).append('Audio separation is estimated; solo notes preserve the reviewed MIDI. Register views use a C4 crossover.' if reviewed else 'Separated stems and per-stem neural notes are estimates. Register views use a C4 crossover.')
    atomic_json(work/'library.json',provenance)
    destination=directory.with_name(directory.name+'-stems-'+work.parent.name[:6])
    shutil.move(str(work),str(destination))
    from catalog import Catalog
    Catalog().index([destination])
    return dict(path=str(destination),stems=len(entries))
