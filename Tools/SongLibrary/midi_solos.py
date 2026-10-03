"""Compile authored, aligned MIDI groups for synthesis in the engine, without audio stems."""
import argparse
import copy
from common import *
from stems import copy_master_stem


def prepare(directory, groups):
    bundle=Path(directory).resolve()
    master=validate_bundle(bundle)
    folder=bundle/'stems';folder.mkdir(exist_ok=True)
    settings=read_json(bundle/'song.json',{})
    settings.update(SectionBoundaries='\n'.join(f"{s['FirstBar']+1} {s['Name']}" for s in master['Sections']))
    atomic_json(folder/'song.json',settings)
    if (bundle/'score-accents.json').exists():
        (folder/'score-accents.json').write_bytes((bundle/'score-accents.json').read_bytes())
    entries=[]
    for group in groups:
        score=folder/(group['id']+'.mid')
        copy_master_stem(bundle/'aligned.mid',score,{'other':group['tracks']},'other')
        compile_patterns(score,bundle/'midi-solos.log')
        path=Path(str(score)+'.patterns.json');patterns=read_json(path)
        for field in MASTER_FIELDS:
            if field in master:patterns[field]=copy.deepcopy(master[field])
        for section in patterns['Sections']:
            reference=next((s for s in master['Sections'] if s['Start']==section['Start']),None)
            if reference:
                for field in MASTER_SECTION_FIELDS:
                    if field in reference:section[field]=copy.deepcopy(reference[field])
        atomic_json(path,patterns)
        entries.append(dict(id=group['id'],name=group['name'],method='midi-synthesis',
            midiPath=score.relative_to(bundle).as_posix(),midiSha256=sha(score),
            patternsPath=path.relative_to(bundle).as_posix(),patternsSha256=sha(path),notes=len(patterns['Notes'])))
        print('Compiled MIDI solo: '+group['name'],flush=True)
    manifest=read_json(bundle/'aligned.mid.prepared.json');manifest['stems']=entries
    atomic_json(bundle/'aligned.mid.prepared.json',manifest)
    provenance=read_json(bundle/'library.json',{});provenance['soloMethod']='authored aligned MIDI synthesized in engine'
    atomic_json(bundle/'library.json',provenance)
    validate_bundle(bundle)
    return entries


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle');parser.add_argument('groups',help='JSON list: id, name, zero-based tracks')
    args=parser.parse_args();prepare(args.bundle,read_json(args.groups))
