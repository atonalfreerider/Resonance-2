import numpy as np
from align_probe import *
def notes_in(d,h,t0,t1,out0,out1):
    res=[]
    for n in d['notes'][h]:
        s=d['sec_of'](n['s']);e=d['sec_of'](n['e'])
        if t0<=s<t1:res.append((out0+(s-t0)*(out1-out0)/(t1-t0),out0+(min(e,t1)-t0)*(out1-out0)/(t1-t0),n['p'],n['v']))
    return res
W=1.5/30.0
def consonance(mel,acc):
    """share of melody onset-time (weighted) whose pitch class is in the accompaniment's sounding classes (+/- dissonance 1 semitone shown separately)"""
    good=tot=0;clash=0
    for s,e,p,v in mel:
        sounding={q%12 for (s2,e2,q,v2) in acc if s-W<=s2<=s+0.2*W}
        if not sounding:continue
        tot+=1
        if p%12 in sounding:good+=1
        # minor second/ major seventh against any sounding pc
        if any((p-q)%12 in(1,11) for q in sounding):clash+=1
    return good/max(tot,1),clash/max(tot,1)
def test(la,lb,ca,cb):
    lt0,lt1=span(L,la,lb);ct0,ct1=span(C,ca,cb)
    lrh=notes_in(L,0,lt0,lt1,0,1);llh=notes_in(L,1,lt0,lt1,0,1)
    crh=notes_in(C,0,ct0,ct1,0,1);clh=notes_in(C,1,ct0,ct1,0,1)
    r=lambda m,a:tuple(round(x,2) for x in consonance(m,a))
    return dict(own_L=r(lrh,llh),own_C=r(crh,clh),L_rh_over_C_lh=r(lrh,clh),C_rh_over_L_lh=r(crh,llh))
if __name__=='__main__':
    for a in [(3,8,2,5),(3,13,2,10),(18,27,26,33),(18,24,26,30),(29,41,17,25),(44,55,46,57),(47,61,70,77),(56,61,75,77),(53,61,72,77),(47,61,62,77)]:
        print(a,test(*a))
