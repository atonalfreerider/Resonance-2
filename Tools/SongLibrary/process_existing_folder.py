"""Resume-safe local batch: enrich existing aligned songs without retranscribing their MIDI."""
import argparse
import shutil
import traceback
from common import *
from catalog import Catalog
from stems import compile_stems


def process(folder):
    sources=sorted(p for p in Path(folder).iterdir() if p.suffix.lower() in ('.wav','.mp3','.flac'))
    job=DATA/'jobs'/'current-song-folder';job.mkdir(parents=True,exist_ok=True)
    report={'sourceFolder':str(Path(folder).resolve()),'songs':[]}
    def save():atomic_json(job/'batch-report.json',report)
    for audio in sources:
        item={'song':audio.stem,'source':str(audio.resolve()),'status':'preparing'};report['songs'].append(item);save()
        try:
            source=ROOT/'PreparedSongs'/'Recordings'/audio.stem
            data=validate_bundle(source);manifest=read_json(source/'aligned.mid.prepared.json')
            if sha(audio)!=manifest['sourceAudioSha256']:raise ValueError('Recording changed since alignment; realignment required')
            destination=ROOT/'PreparedSongs'/'Library'/(audio.stem+'-prepared-stems')
            if destination.exists():
                ready=validate_bundle(destination)
                if len(read_json(destination/'aligned.mid.prepared.json').get('stems',[]))!=7:raise ValueError('Incomplete existing destination')
                if sha(destination/'aligned.mid')!=sha(source/'aligned.mid'):raise ValueError('Existing destination belongs to a different companion MIDI revision')
                item.update(status='complete',bundle=str(destination),reused=True,notes=len(ready['Notes']));save();continue
            work=job/audio.stem
            if not work.exists():shutil.copytree(source,work)
            tracks=data['TrackNames'];mapping={'vocals':[],'bass':[],'drums':[],'other':[]}
            active=sorted(set(n['Track'] for n in data['Notes']))
            for i in active:
                name=tracks[i].lower()
                kind='drums' if 'drum' in name else 'bass' if 'bass' in name else 'vocals' if 'vocal' in name or 'voice' in name else 'other'
                mapping[kind].append(i)
            lead=next((i for i in mapping['vocals'] if tracks[i].lower().split('-',1)[-1] in ('vocal','voice')),mapping['vocals'][0] if mapping['vocals'] else -1)
            settings=read_json(work/'song.json',{})
            settings.update(StemTracks=mapping,LeadVocalTrack=lead,SectionSource='Authored companion MIDI; section roles remain provisional')
            atomic_json(work/'song.json',settings)
            original=sha(source/'aligned.mid');compile_patterns(work/'aligned.mid',job/(audio.stem+'.log'))
            output=work/'stems';separation=read_json(output/'separation.json',{})
            if separation.get('sourceSha256')!=sha(work/'recording.wav'):
                item['status']='separating';save();print(audio.stem+': separating audio with local Demucs',flush=True)
                run([MODEL_PYTHON,ROOT/'Tools/SongLibrary/separate.py','--audio',work/'recording.wav','--output',output],job/(audio.stem+'.log'))
            item['status']='compiling';save()
            compile_stems(work,lambda s:print(audio.stem+': '+s,flush=True))
            if sha(work/'aligned.mid')!=original:raise ValueError('Companion MIDI unexpectedly changed')
            compiled=validate_bundle(work)
            alignment=read_json(work/'analysis.json',{})
            provenance={'sourceFolder':str(Path(folder).resolve()),'midiMethod':'preserved-existing-authored-companion','originalMidiSha256':original,
                'stems':read_json(output/'separation.json'),'warnings':['Audio separation is estimated. Section names and fingerprint weak windows require review. MIDI pitches and timing were preserved.'],
                'alignmentSimilarity':alignment.get('alignedPitchSimilarity'),'weakWindows':len(alignment.get('weakWindows',[]))}
            atomic_json(work/'library.json',provenance)
            destination.parent.mkdir(parents=True,exist_ok=True);shutil.move(str(work),str(destination));Catalog().index([destination])
            item.update(status='complete',bundle=str(destination),notes=len(compiled['Notes']),stems=7,midiSha256=original,
                        weakWindows=provenance['weakWindows'],alignmentSimilarity=provenance['alignmentSimilarity']);save()
            print(audio.stem+': COMPLETE',flush=True)
        except Exception as error:
            item.update(status='failed',error=str(error));save();traceback.print_exc()
    save();print(json.dumps(report,indent=2),flush=True)
    if any(s['status']!='complete' for s in report['songs']):raise SystemExit(1)

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('folder');process(parser.parse_args().folder)
