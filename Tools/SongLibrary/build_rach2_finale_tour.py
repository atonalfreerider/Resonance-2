"""Publish John's third-movement tour using the aligned Rubinstein score."""
import argparse
import copy
import json
from pathlib import Path
from common import ROOT, atomic_json
from publish_story import publish


def build(bundle):
    bundle=Path(bundle).resolve()
    source=Path(__file__).parent/'directors/rach2-finale.json'
    draft=json.loads(source.read_text(encoding='utf-8'))
    data=json.loads((bundle/'aligned.mid.patterns.json').read_text(encoding='utf-8'))
    draft['pianoMelody']=data['PianoMelody'];draft['orchestraMelody']=data['OrchestraMelody']
    pictures=json.loads((ROOT/'PreparedSongs/Library/Rach2-Moderato-Rubinstein/story.draft.json').read_text(encoding='utf-8'))['pictures']
    draft['pictures']={k:dict(pictures[k],file=str(ROOT/'PreparedSongs/Library/Rach2-Moderato-Rubinstein'/pictures[k]['file'])) for k in ('composer','recordshop')}
    for cue in draft['cues']:
        cue.setdefault('view','Torus');cue.setdefault('stem','');cue.setdefault('uncoil',False)
        cue.setdefault('melodyTarget','both');cue['directedMelody']=True
    atomic_json(bundle/'story.draft.json',draft)
    result=publish(bundle)
    report=ROOT/'Reports/SongProcessing/Rach2-Allegro-Scherzando-Rubinstein';report.mkdir(parents=True,exist_ok=True)
    stamp=lambda s:f'{int(s)//60}:{s%60:04.1f}'
    lines=[draft['title'],'JohnV4: grounded delivery, downward sentence endings.','Target: half narration, half clean Rubinstein recording.','Emotional, folk and ecclesiastical comparisons are listening interpretations. Extra notes are deliberately composed, not accidental keystrokes.','Score timing: prepared alignment; lower-confidence windows remain flagged in the analysis.','']
    for cue in draft['cues']:
        lines.extend([f"{stamp(cue['start'])}–{stamp(cue['end'])} | {cue['concertoFocus']}"+(' | circle of fifths' if cue['uncoil'] else ''),cue.get('speech') or '[Music alone] '+cue['text'],''])
    lines.extend(['Sources']+[s['title']+'\n'+s['url'] for s in draft['sources']])
    (report/'tour-script.txt').write_text('\n'.join(lines),encoding='utf-8')
    library=bundle/'library.json'
    if library.exists():
        record=json.loads(library.read_text(encoding='utf-8'));record['directorScript']=True;atomic_json(library,record)
    return result


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--bundle',default=str(ROOT/'PreparedSongs/Library/Rach2-Allegro-Scherzando-Rubinstein'))
    print(json.dumps(build(p.parse_args().bundle),indent=2))
