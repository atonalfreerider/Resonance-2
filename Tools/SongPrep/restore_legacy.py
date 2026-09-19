"""Recover legacy event CSVs and authored sequence/pattern tables without modifying sources."""
import argparse, csv, hashlib, json, re, subprocess
from pathlib import Path
import mido

def pitch(name):
    m=re.fullmatch(r'([a-gA-G])([#b]?)(-?\d+)',name.strip())
    if not m: raise ValueError('Unknown pitch '+repr(name))
    # Legacy export uses C0 = MIDI 0 (its c3/d3/f#3 drums are GM 36/38/42).
    return int(m[3])*12+dict(c=0,d=2,e=4,f=5,g=7,a=9,b=11)[m[1].lower()]+{'':0,'#':1,'b':-1}[m[2]]

def write_midi(path,tracks,tempo=500000,key=None):
    out=mido.MidiFile(ticks_per_beat=1920)
    for name,events in tracks.items():
        track=mido.MidiTrack();out.tracks.append(track)
        track.append(mido.MetaMessage('track_name',name=name,time=0))
        if len(out.tracks)==1:
            track.append(mido.MetaMessage('set_tempo',tempo=tempo,time=0))
            track.append(mido.MetaMessage('time_signature',numerator=4,denominator=4,time=0))
            if key: track.append(mido.MetaMessage('key_signature',key=key,time=0))
        last=0
        for beat,msg in sorted(events,key=lambda e:(e[0],0 if e[1].type=='note_off' else 1)):
            tick=max(0,round(beat*1920));track.append(msg.copy(time=max(0,tick-last)));last=tick
        track.append(mido.MetaMessage('end_of_track',time=0))
    out.save(path)
    return out

def raw(rows):
    tracks={};warnings=[];controllers={'Bank Select':0,'Modulation Wheel':1,'Modulation wheel':1,'Channel Volume (formerly Main Volume)':7,'Pan':10,'Expression Controller':11,'Damper pedal on/off (Sustain)':64,'Effects 1 Depth':91,'Effects 3 Depth':93,
        'Sound Controller 5 (Brightness)':74,'Registered Parameter Number MSB':101,'Registered Parameter Number LSB':100,'Data Entry':6,'Data entry':6,'Reset All Controllers':121,'Sound Controller 2 (Timbre)':71,'Sound Controller 3 (Release Time)':72,'Sound Controller 4 (Attack Time)':73,'Effects 2 Depth':92,'Effects 4 Depth':94,'Effects 5 Depth':95,'Non-Registered Parameter Number LSB':98,'Non-Registered Parameter Number MSB':99,'All notes off':123,'All Sound Off':120,'Portamento on/off':65,'Portamento time':5}
    for r in rows[1:]:
        if len(r)<16: continue
        try:
            beat=float(r[6]);name=r[7].strip() or 'Untitled';ev=r[9].strip();channel=int(r[11]) if r[11].strip() else 0
            events=tracks.setdefault(name,[]);msg=None
            if ev in ('Note On','Note Off'):msg=mido.Message('note_on' if ev=='Note On' else 'note_off',channel=channel,note=pitch(r[12]),velocity=int(float(r[13] or 0)))
            elif ev=='Set Tempo(BPM)':msg=mido.MetaMessage('set_tempo',tempo=int(r[10]))
            elif ev=='Program Change':msg=mido.Message('program_change',channel=channel,program=int(r[14]))
            elif ev=='Pitch Wheel':msg=mido.Message('pitchwheel',channel=channel,pitch=int(r[14])+128*int(r[15])-8192)
            elif ev=='Controller' and r[10] in controllers:msg=mido.Message('control_change',channel=channel,control=controllers[r[10]],value=int(r[14]))
            elif ev=='Key Signature':
                values=r[10].split();sf=int(values[0]);minor=int(values[1]);keys=['Cb','Gb','Db','Ab','Eb','Bb','F','C','G','D','A','E','B','F#','C#'];mins=['Abm','Ebm','Bbm','Fm','Cm','Gm','Dm','Am','Em','Bm','F#m','C#m','G#m','D#m','A#m'];msg=mido.MetaMessage('key_signature',key=(mins if minor else keys)[sf+7])
            elif ev=='Time Signature':
                nums=re.findall(r'\d+',r[10]);msg=mido.MetaMessage('time_signature',numerator=int(nums[0]),denominator=2**int(nums[1]))
            elif ev=='Track Instument name':msg=mido.MetaMessage('instrument_name',name=r[10])
            elif ev in ('Marker','Text','Lyric','Copyright'):msg=mido.MetaMessage({'Marker':'marker','Text':'text','Lyric':'lyrics','Copyright':'copyright'}[ev],text=r[10])
            elif ev not in ('Sequence or Track name','End of Track') and ev: warnings.append('Not recoverable unambiguously: '+ev)
            if msg:events.append((beat,msg))
        except (ValueError,IndexError) as e:warnings.append(f'Row {rows.index(r)+1}: {e}')
    warnings.append('The legacy Bank Select label omits MSB/LSB identity; recovered as controller 0. Unsupported vendor/system metadata is listed above.')
    return tracks,None,500000,None,sorted(set(warnings))

def authored(rows):
    info={};sequence={};defs={};section='';lane=None;group=None;columns=[]
    for r in rows:
        r=r+['']*(75-len(r))
        if r[0]:
            section=r[0].strip();lane=section if re.match(r'\d+-',section) else None
            if lane:defs.setdefault(lane,{})
        if section=='Info' and r[1]:info[r[1]]=r[2]
        elif section=='Sequence' and r[1] and r[1]!='Track':sequence[r[1]]=[s.strip() for s in r[2:] if s.strip()]
        elif lane:
            if r[1].strip().isdigit():group=int(r[1]);defs[lane][group]={'beats':8,'sequences':[],'rows':[]}
            elif r[2]=='DivDownbeat':defs[lane][group]['beats']=float(r[3])
            elif r[2]=='Sequences':defs[lane][group]['sequences']=[[int(v) for v in s.split('|')] for s in r[3:] if s]
            elif r[2]=='Patterns':columns=[int(s) for s in r[6:] if s.strip()]
            elif r[3] and r[4] and r[5]:defs[lane][group]['rows'].append((float(r[3])-1,float(r[4])-1,r[5],dict(zip(columns,r[6:]))))
    bpm=float(info.get('BPM',120));tempo=mido.bpm2tempo(bpm);tracks={};disks=[];warnings=[];drum=lambda s:'drum' in s.lower()
    key=info.get('Key','').strip();key=key.upper() if key.isupper() else (key[:1].upper()+key[1:]+'m' if key else None)
    for ti,(lane,groups) in enumerate(defs.items(),1):
        channel=9 if drum(lane) else (ti-1 if ti<=9 else ti);events=[];tracks[lane]=events;cursor=float(info.get('Delay',0))*bpm/60
        default=next(iter(groups.values()))['beats'];disk_map={}
        for token in sequence.get(lane,[]):
            match=re.fullmatch(r'(-?\d+)\[(-?\d+)\]',token)
            if not match:raise ValueError('Unknown sequence '+token)
            gid,variant=map(int,match.groups())
            if gid<0:cursor+=default;continue
            definition=groups[gid];seq=definition['sequences'][variant];length=definition['beats']
            for variation in seq:
                hits=[]
                for a,b,n,choices in definition['rows']:
                    choice=choices.get(variation,'x').strip()
                    if choice in ('x',''):continue
                    p=pitch(n if choice=='-' else choice);start=cursor+a;end=cursor+b
                    if start<0:warnings.append('Pickup before zero clipped at file start')
                    events.extend([(max(0,start),mido.Message('note_on',channel=channel,note=p,velocity=100)),(max(.001,end),mido.Message('note_off',channel=channel,note=p,velocity=0))])
                    hits.append(dict(Beat=a,Length=b-a,Pitch=p,Track=ti-1,Channel=channel+1,Velocity=100/127))
                did=(gid,variation)
                if did not in disk_map:
                    d=dict(Id=len(disks),Track=ti-1,Channel=channel+1,Bars=max(1,round(length/4)),Name=f'{lane} · group {gid} / variation {variation}',Beats=length,Hits=hits,Visits=[]);disks.append(d);disk_map[did]=d
                disk_map[did]['Visits'].append(dict(Bar=max(0,int(cursor/4)),Beat=cursor,Seconds=cursor*60/bpm,Hits=hits))
                cursor+=length
    warnings.append('Authored CSV has no velocities/programs: velocity 100 and default programs used. - is base pitch; x is rest; explicit pitch is substitution. C0=MIDI0. Empty sequence slots use one DivDownbeat cycle. Section names remain offline estimates.')
    return tracks,{'Disks':disks},tempo,key,warnings

def restore(source,output):
    with source.open(encoding='utf-8-sig',newline='') as f:rows=list(csv.reader(f))
    tracks,patterns,tempo,key,warnings=raw(rows) if rows[0][0]=='FRAME' else authored(rows)
    output.mkdir(parents=True,exist_ok=True);path=output/'restored.mid';mid=write_midi(path,tracks,tempo,key)
    authored_path=output/'authored-patterns.json'
    if patterns:authored_path.write_text(json.dumps(patterns),encoding='utf-8')
    report=dict(source=str(source),sourceSha256=hashlib.sha256(source.read_bytes()).hexdigest(),format='event export' if rows[0][0]=='FRAME' else 'authored patterns',notes=sum(m.type=='note_on' and m.velocity>0 for t in mid.tracks for m in t),warnings=warnings)
    (output/'restoration.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    return path,authored_path if patterns else None,report

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--source',required=True,type=Path);p.add_argument('--output',required=True,type=Path);a=p.parse_args()
    sources=sorted(a.source.glob('*.csv')) if a.source.is_dir() else [a.source]
    for source in sources:
        path,patterns,report=restore(source,a.output/source.stem)
        command=['dotnet','run','--project',str(Path(__file__).resolve().parents[1]/'PatternPrep/PatternPrep.csproj'),'--',str(path)]
        if patterns:command.append(str(patterns))
        subprocess.run(command,check=True);print(f'{source.stem}: {report["notes"]} notes recovered',flush=True)
