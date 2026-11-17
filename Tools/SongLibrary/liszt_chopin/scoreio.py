"""Read the two MuseScore-exported MIDIs: notes per hand, bar boundaries in ticks and seconds."""
import mido,bisect
NAMES=['C','Db','D','Eb','E','F','Gb','G','Ab','A','Bb','B']
def load(path):
    m=mido.MidiFile(path);tpb=m.ticks_per_beat
    tempos=[(0,500000)];ts=[]
    notes=[[] for _ in m.tracks]
    for ti,t in enumerate(m.tracks):
        a=0;on={}
        for msg in t:
            a+=msg.time
            if msg.type=='set_tempo' and ti==0 or (msg.type=='set_tempo' and not any(x[0]==a for x in tempos)):tempos.append((a,msg.tempo))
            if msg.type=='time_signature' and ti==0:ts.append((a,msg.numerator,msg.denominator))
            if msg.type=='note_on' and msg.velocity>0:on.setdefault(msg.note,[]).append((a,msg.velocity))
            elif msg.type in('note_off','note_on') and msg.note in on and on[msg.note]:
                s,v=on[msg.note].pop(0);notes[ti].append(dict(s=s,e=a,p=msg.note,v=v))
    tempos=sorted(set(tempos));
    # seconds map
    marks=[];sec=0;prev=tempos[0]
    for i,(tk,tp) in enumerate(tempos):
        if i>0:sec+=(tk-tempos[i-1][0])*tempos[i-1][1]/1e6/tpb
        marks.append((tk,sec,tp))
    def sec_of(tick):
        i=bisect.bisect_right([x[0] for x in marks],tick)-1
        tk,s,tp=marks[i];return s+(tick-tk)*tp/1e6/tpb
    bars=[];tick=0;end=max(n['e'] for t in notes for n in t)
    cur=ts[0];tsi=0
    while tick<end:
        while tsi+1<len(ts) and ts[tsi+1][0]<=tick:tsi+=1
        _,nu,de=ts[tsi];length=int(tpb*4*nu/de);bars.append(dict(tick=tick,len=length,sec=sec_of(tick)));tick+=length
    for b in bars:b['end_sec']=sec_of(b['tick']+b['len'])
    return dict(tpb=tpb,notes=notes,bars=bars,sec_of=sec_of,tempos=marks)
def pname(p):return NAMES[p%12]+str(p//12-1)
