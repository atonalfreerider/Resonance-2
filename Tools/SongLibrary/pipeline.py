"""Transactional MP3/WAV → recording + MIDI + compressed visualization bundle."""
import html
import shutil
import uuid
import zipfile
from pathlib import Path
from common import *
from catalog import Catalog, bitmidi_search, download_candidate, midi_info


def ingest(audio, midi=None, title=None, online=True, force_neural=False, meter=4, notify=lambda x:None, stems=True, piano_hands=False):
    if midi and force_neural:raise ValueError('Choose either a companion MIDI or forced neural transcription')
    if piano_hands and not midi:raise ValueError('Piano-hand mode requires the authored two-track ground-truth MIDI')
    audio=Path(audio).resolve()
    if not audio.is_file() or audio.suffix.lower() not in ('.mp3','.wav','.flac','.ogg','.m4a'):
        raise ValueError('Choose an existing MP3, WAV, FLAC, OGG or M4A recording')
    if meter not in (3,4): raise ValueError('Meter must be 3/4 or 4/4')
    if not title:
        try:
            metadata=json.loads(run(['ffprobe','-v','error','-show_entries','format_tags=artist,title','-of','json',audio]))
            tags={k.lower():v for k,v in metadata.get('format',{}).get('tags',{}).items()}
            title=' - '.join(tags[k] for k in ('artist','title') if tags.get(k))
        except Exception:pass
    title=title or audio.stem; audio_hash=sha(audio)
    catalog=Catalog(); notify('Indexing local MIDI and prepared songs')
    catalog.index([ROOT/'PreparedSongs',DATA/'midi'])
    existing_bundles=catalog.exact(audio_hash)
    if not midi and not force_neural:
        for existing in existing_bundles:
            try: validate_bundle(existing)
            except (ValueError,KeyError,OSError): continue
            stem_ids={stem.get('id') for stem in read_json(existing/'aligned.mid.prepared.json').get('stems',[])}
            requested_ready={'right-hand','left-hand'}<=stem_ids if piano_hands else bool(stem_ids)
            if stems and not requested_ready:
                if piano_hands:
                    break
                from stems import enrich
                return enrich(existing,notify)
            notify('Reusing the verified bundle for this exact recording')
            return dict(path=str(existing), reused=True, title=title)
    job=DATA/'jobs'/uuid.uuid4().hex;job.mkdir(parents=True)
    stage=job/'bundle';stage.mkdir();log=job/'process.log'
    provenance=dict(version=PIPELINE_VERSION,title=title,sourceAudioSha256=audio_hash,
                    reviewed=False,warnings=[],candidates=[],method='',sourceAudioName=audio.name)
    atomic_json(job/'request.json',dict(audio=str(audio),midi=str(midi) if midi else None,title=title,pianoHands=piano_hands))
    notify('Decoding recording and estimating key / beat grid')
    from features import decode,evidence,match_midi,regrid
    decode(audio,stage/'recording.wav');info,chroma=evidence(stage/'recording.wav')
    selected=Path(midi).resolve() if midi else None
    if selected: midi_info(selected);provenance['method']='provided-midi'
    if not selected and not force_neural:
        notify('Searching the MIDI catalog and comparing audio fingerprints')
        choices=catalog.search(title)
        if online:
            try:
                for candidate in bitmidi_search(title):
                    try: candidate['path']=str(download_candidate(candidate,DATA/'midi'));choices.append(candidate)
                    except Exception as error: provenance['warnings'].append('MIDI download: '+str(error))
            except Exception as error: provenance['warnings'].append('Online lookup unavailable: '+str(error))
        seen=set()
        for candidate in choices:
            try:
                digest=sha(candidate['path'])
                if digest in seen: continue
                seen.add(digest);candidate['fingerprint']=match_midi(candidate['path'],chroma,info['duration'])
                provenance['candidates'].append(candidate)
            except Exception as error: provenance['warnings'].append('Candidate rejected: '+str(error))
        accepted=[c for c in provenance['candidates'] if c['fingerprint'].get('accepted')]
        accepted.sort(key=lambda c:c['fingerprint']['score'],reverse=True)
        if accepted:
            selected=Path(accepted[0]['path']);provenance['method']='fingerprint-verified-lookup'
            provenance['selected']=accepted[0]
    settings=dict(Key=info['key'],Minor=info['minor'],LeadVocalTrack=-1,
                  KeySource='Estimated from recording chroma; review required',
                  SectionSource='Repeated-pattern families; automatic eight-bar boundaries, review required')
    for existing in existing_bundles:
        known=read_json(existing/'song.json',{})
        if known.get('Key',-1)>=0 and 'reviewed' in known.get('KeySource','').lower():
            settings.update({k:known[k] for k in ('Key','Minor','KeySource') if k in known})
            provenance['keyInheritedFrom']=str(existing)
            break
    if selected and (selected.parent/'song.json').exists():
        settings.update(read_json(selected.parent/'song.json'))
    atomic_json(stage/'song.json',settings)
    if selected:
        notify('Aligning companion MIDI to recording fingerprints; compiling patterns')
        run([PREP_PYTHON,ROOT/'Tools/SongPrep/prepare_song.py','--audio',audio,'--midi',selected,'--output',stage],log)
        provenance['sourceMidiSha256']=sha(selected)
        provenance['alignment']=read_json(stage/'analysis.json')
    else:
        provenance['method']='local-yourmt3';provenance['warnings'].extend([
            'Neural notes, instrument assignments and drums are estimates.',
            'Beat/downbeat grid and meter are estimated; review the first downbeat and section boundaries.',
            'Lead vocal is not automatically identified; select its MIDI track after listening.'])
        notify('Transcribing instruments and drums locally with YourMT3')
        from transcribe import HASH
        provenance['model']=dict(name='YourMT3 YPTF MoE noPS',sha256=HASH,
                                 implementation='openmirlab/mt3-infer@3675ad860ea7c0adcaec8498b798ff605d0a0f42')
        raw=stage/'transcription.mid'
        run([MODEL_PYTHON,ROOT/'Tools/SongLibrary/transcribe.py','--audio',stage/'recording.wav','--output',raw],log)
        midi_info(raw)
        settings['PatternTimingToleranceBeats']=.25
        import mido
        voice_tracks=[i for i,t in enumerate(mido.MidiFile(raw).tracks) if any(m.type=='track_name' and ('singing voice' in m.name.lower() or 'lead vocal' in m.name.lower()) for m in t)]
        if len(voice_tracks)==1:
            # regrid inserts a conductor track; keep the predicted vocal visually prominent.
            settings['LeadVocalTrack']=voice_tracks[0]+1
            provenance['warnings'].append('Highlighted voice track is the model prediction; verify that it is the lead vocal.')
        atomic_json(stage/'song.json',settings)
        notify('Preserving note timestamps; compiling section families, variants and drum bars')
        regrid(raw,stage/'aligned.mid',info['beats'],meter)
        compile_patterns(stage/'aligned.mid',log)
        atomic_json(stage/'analysis.json',dict(method=provenance['method'],verifiedPerfect=False,**info))
        atomic_json(stage/'aligned.mid.prepared.json',dict(version=1,audioPath='recording.wav',midiPath='aligned.mid',
            reportPath='report.html',sourceAudioPath=audio.name,sourceAudioSha256=audio_hash,
            sourceMidiSha256=sha(raw),audioSha256=sha(stage/'recording.wav'),midiSha256=sha(stage/'aligned.mid'),
            featureResolutionMs=10,status='Local neural transcription; review required',audioDuration=info['duration']))
        (stage/'report.html').write_text('<!doctype html><meta charset="utf-8"><title>Song analysis</title><h1>'+html.escape(title)+
            '</h1><p>Local multitrack transcription. Notes, key, beat grid and form need review.</p><audio controls src="recording.wav"></audio><p><a href="analysis.json">Audio evidence</a></p>',encoding='utf-8')
    provenance['audioEvidence']=dict(key=info['key'],minor=info['minor'],keyMargin=info['keyMargin'],meter=meter)
    provenance['warnings'].append('Automatic analysis is not a guarantee of exact transcription or alignment.')
    atomic_json(stage/'library.json',provenance)
    manifest=read_json(stage/'aligned.mid.prepared.json');manifest['sourceAudioPath']=audio.name
    atomic_json(stage/'aligned.mid.prepared.json',manifest)
    if stems:
        if piano_hands:
            from piano_hands import prepare_piano_hands
            prepare_piano_hands(stage,notify)
        else:
            from stems import prepare_stems
            prepare_stems(stage,notify)
        provenance['stems']=read_json(stage/'stems/separation.json')
        provenance['warnings'].append('Piano hand audio uses complementary score-guided masks; overlapping partials and pedal resonance can leak between solos.' if piano_hands else 'Stem isolation and per-stem notes are estimates; high/low accompaniment is a C4 register filter.')
        atomic_json(stage/'library.json',provenance)
    patterns=validate_bundle(stage)
    destination=ROOT/'PreparedSongs/Library'/(slug(title)+'-'+audio_hash[:8]+'-'+job.name[:6])
    destination.parent.mkdir(parents=True,exist_ok=True)
    shutil.move(str(stage),str(destination));catalog.index([destination])
    notify('Ready for review: '+str(destination))
    return dict(path=str(destination),title=title,reused=False,notes=len(patterns['Notes']),method=provenance['method'])


def review(directory, settings):
    directory=Path(directory).resolve();validate_bundle(directory)
    allowed={'Key','Minor','LeadVocalTrack','SectionBoundaries','SectionParents','Meter'}
    if set(settings)-allowed: raise ValueError('Unsupported review settings')
    if not -1<=int(settings.get('Key',-1))<=11: raise ValueError('Key out of range')
    # Build a separate revision; failed edits cannot invalidate the playable original.
    revision=directory.with_name(directory.name+'-review-'+uuid.uuid4().hex[:6])
    work=DATA/'jobs'/uuid.uuid4().hex/'bundle';work.parent.mkdir(parents=True)
    shutil.copytree(directory,work)
    values=read_json(work/'song.json',{});values.update(settings)
    if 'Meter' in settings:
        if settings['Meter'] not in (3,4):raise ValueError('Meter must be 3 or 4')
        if not (work/'transcription.mid').exists():raise ValueError('Meter override is for neural transcriptions; edit a companion MIDI meter before import')
        from features import regrid
        regrid(work/'transcription.mid',work/'aligned.mid',read_json(work/'analysis.json')['beats'],settings['Meter'])
        manifest=read_json(work/'aligned.mid.prepared.json');manifest['midiSha256']=sha(work/'aligned.mid')
        atomic_json(work/'aligned.mid.prepared.json',manifest)
    if 'Key' in settings or 'Minor' in settings:values['KeySource']='User reviewed key'
    if 'SectionBoundaries' in settings or 'SectionParents' in settings:values['SectionSource']='User reviewed section boundaries'
    atomic_json(work/'song.json',values);compile_patterns(work/'aligned.mid',work.parent/'process.log')
    if read_json(work/'aligned.mid.prepared.json').get('stems'):
        if values.get('PianoHandTracks'):
            from piano_hands import recompile_piano_hand_scores
            recompile_piano_hand_scores(work)
        else:
            from stems import compile_stems
            compile_stems(work)
    validate_bundle(work)
    provenance=read_json(work/'library.json',{});provenance.update(reviewed=True,parentBundle=directory.name)
    atomic_json(work/'library.json',provenance);shutil.move(str(work),str(revision))
    Catalog().index([revision]);return dict(path=str(revision))


def export(directory, output):
    directory=Path(directory).resolve();validate_bundle(directory);output=Path(output)
    output.parent.mkdir(parents=True,exist_ok=True)
    temporary=output.with_name(output.name+'.tmp')
    with zipfile.ZipFile(temporary,'w',zipfile.ZIP_DEFLATED) as archive:
        for path in directory.rglob('*'):
            if path.is_file() and path.suffix not in ('.meta','.tmp'):
                archive.write(path,Path('PreparedSongs')/directory.name/path.relative_to(directory))
    temporary.replace(output)
    return dict(path=str(output),sha256=sha(output))
