"""Publish the hand-directed Rubinstein tour against the installed, aligned score.

Assets are downloaded separately after checking their Commons description pages.
The source script is editable at directors/rach2-moderato.json.
"""
import argparse
import copy
import json
from pathlib import Path
from common import ROOT, atomic_json
from publish_story import publish


def strand(data, track, rank, start, end):
    source=next(s for s in data['MelodyStrands'] if s['Track']==track and s['Rank']==rank)
    notes=[dict(n,SourceTrack=track,SourceChannel=source['Channel']) for n in source['Notes'] if start-.5<=n['Start']<end]
    notes.sort(key=lambda n:n['Start'])
    for a,b in zip(notes,notes[1:]):a['End']=min(a['End'],b['Start'])
    return dict(Track=-2,Channel=0,Rank=rank,Notes=notes)


def build(bundle, images):
    bundle=Path(bundle).resolve();images=Path(images).resolve()
    draft=json.loads((Path(__file__).parent/'directors/rach2-moderato.json').read_text(encoding='utf-8'))
    data=json.loads((bundle/'aligned.mid.patterns.json').read_text(encoding='utf-8'))
    # Explicit score voices for the brief three-line demonstration. These are
    # visual selections only; no notes or recording audio are altered.
    draft['pianoMelody']=[strand(data,1,4,425,444),strand(data,1,1,425,444)]
    draft['pianoMelody'][0]['Rank']=0;draft['pianoMelody'][1]['Rank']=1
    orchestra=strand(data,14,3,425,444)
    fallback=[strand(data,14,r,425,444) for r in (1,2)]
    for extra in [n for s in fallback for n in s['Notes']]:
        if not any(n['Start']<=extra['Start']<n['End'] for n in orchestra['Notes']):orchestra['Notes'].append(copy.deepcopy(extra))
    orchestra['Notes'].sort(key=lambda n:n['Start'])
    for a,b in zip(orchestra['Notes'],orchestra['Notes'][1:]):a['End']=min(a['End'],b['Start'])
    orchestra['Rank']=0;draft['orchestraMelody']=[orchestra]
    pictures={
        'bells':('Bells_of_the_Belfry_of_St._Sophia_Cathedral_in_Veliky_Novgorod.jpg','Novgorod: bells at Saint Sophia Cathedral','Vityavo / Wikimedia Commons · CC0 1.0','CC0-1.0'),
        'composer':('Rachmaninoff_1900.jpg','Rachmaninoff at the piano, early 1900s','Unknown photographer / Wikimedia Commons · Public domain (US)','Public domain (US)'),
        'recordshop':('Victor_Record_Shop_window_LCCN2014710038.jpg','Victor record shop, 1919 · historical context','Bain News Service / Library of Congress · Public domain','Public domain')
    }
    for key,(page,caption,credit,license) in pictures.items():
        draft['pictures'][key]=dict(file=str(images/(key+'.jpg')),caption=caption,credit=credit,license=license,commonsPage='https://commons.wikimedia.org/wiki/File:'+page,placement='right')
    for cue in draft['cues']:
        cue.setdefault('view','Torus');cue.setdefault('stem','');cue.setdefault('uncoil',False)
        cue.setdefault('melodyTarget','both')
    atomic_json(bundle/'story.draft.json',draft)
    result=publish(bundle)
    report=ROOT/'Reports/SongProcessing/Rach2-Moderato-Rubinstein';report.mkdir(parents=True,exist_ok=True)
    def stamp(seconds):return f'{int(seconds)//60}:{seconds%60:04.1f}'
    lines=[draft['title'],'JohnV4 · calm delivery, downward sentence endings','Full mix: Rubinstein MP3. Visual selections use the aligned score.','Three musical languages are an interpretive listening framework.','']
    for cue in draft['cues']:
        lines.append(f"{stamp(cue['start'])}–{stamp(cue['end'])} | {cue['concertoFocus']}"+(' | circle of fifths' if cue['uncoil'] else ''))
        lines.append(cue.get('speech') or '[Music alone] '+cue['text']);lines.append('')
    lines.extend(['Sources']+[s['title']+'\n'+s['url'] for s in draft['sources']])
    lines.extend(['','Image credits']+[p['credit']+'\n'+p['commonsPage'] for p in draft['pictures'].values()])
    (report/'tour-script.txt').write_text('\n'.join(lines),encoding='utf-8')
    return result


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--bundle',default=str(ROOT/'PreparedSongs/Library/Rach2-Moderato-Rubinstein'));p.add_argument('--images',default=str(ROOT/'SongLibraryData/jobs/rach2-tour/images'))
    a=p.parse_args();print(json.dumps(build(a.bundle,a.images),indent=2))
