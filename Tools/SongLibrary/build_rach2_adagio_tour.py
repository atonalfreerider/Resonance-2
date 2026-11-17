"""Publish John's second-movement tour using the aligned Rubinstein score."""
import argparse
import copy
import json
from pathlib import Path
from common import ROOT, atomic_json
from publish_story import publish


def melody(data):
    # The opening melody passes from solo flute to solo clarinet. Preserve that
    # handoff explicitly rather than letting the accompaniment win a pitch vote.
    notes=[]
    for track,start,end in ((3,46.303,62.26),(5,62.26,102.794)):
        strand=next(s for s in data['MelodyStrands'] if s['Track']==track and s['Rank']==0)
        notes.extend(dict(n,SourceTrack=track,SourceChannel=strand['Channel']) for n in strand['Notes'] if start-.05<=n['Start']<end)
    for strand in data['OrchestraMelody']:
        notes.extend(copy.deepcopy(n) for n in strand['Notes'] if n['Start']>=102.794 or n['Start']<46.253)
    notes.sort(key=lambda n:n['Start'])
    for a,b in zip(notes,notes[1:]):a['End']=min(a['End'],b['Start'])
    return [dict(Track=-2,Channel=0,Rank=0,Notes=[n for n in notes if n['End']>n['Start']])]


def build(bundle):
    bundle=Path(bundle).resolve()
    source=Path(__file__).parent/'directors/rach2-adagio.json'
    draft=json.loads(source.read_text(encoding='utf-8'))
    data=json.loads((bundle/'aligned.mid.patterns.json').read_text(encoding='utf-8'))
    draft['pianoMelody']=data['PianoMelody'];draft['orchestraMelody']=melody(data)
    pictures=json.loads((ROOT/'PreparedSongs/Library/Rach2-Moderato-Rubinstein/story.draft.json').read_text(encoding='utf-8'))['pictures']
    draft['pictures']={k:dict(pictures[k],file=str(ROOT/'PreparedSongs/Library/Rach2-Moderato-Rubinstein'/pictures[k]['file'])) for k in ('composer','recordshop')}
    for cue in draft['cues']:
        cue.setdefault('view','Torus');cue.setdefault('stem','');cue.setdefault('uncoil',False)
        cue.setdefault('melodyTarget','both');cue['directedMelody']=True
    atomic_json(bundle/'story.draft.json',draft)
    result=publish(bundle)
    report=ROOT/'Reports/SongProcessing/Rach2-Adagio-Sostenuto-Rubinstein';report.mkdir(parents=True,exist_ok=True)
    stamp=lambda s:f'{int(s)//60}:{s%60:04.1f}'
    lines=[draft['title'],'JohnV4: grounded delivery, downward sentence endings.','Target: half narration, half clean Rubinstein recording.','Emotional and Moonlight comparisons are listening interpretations. Extra notes are deliberately composed, not accidental keystrokes.','Score timing: prepared alignment; lower-confidence windows remain flagged in the analysis.','']
    for cue in draft['cues']:
        lines.extend([f"{stamp(cue['start'])}–{stamp(cue['end'])} | {cue['concertoFocus']}"+(' | circle of fifths' if cue['uncoil'] else ''),cue.get('speech') or '[Music alone] '+cue['text'],''])
    lines.extend(['Sources']+[s['title']+'\n'+s['url'] for s in draft['sources']])
    (report/'tour-script.txt').write_text('\n'.join(lines),encoding='utf-8')
    library=bundle/'library.json'
    if library.exists():
        record=json.loads(library.read_text(encoding='utf-8'));record['directorScript']=True;atomic_json(library,record)
    return result


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--bundle',default=str(ROOT/'PreparedSongs/Library/Rach2-Adagio-Sostenuto-Rubinstein'))
    print(json.dumps(build(p.parse_args().bundle),indent=2))
