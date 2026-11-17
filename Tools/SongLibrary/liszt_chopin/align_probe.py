import sys,numpy as np
from scoreio import load
D='../../../SongLibraryData/scores/liszt-chopin/'
L=load(D+'liszt-consolation3.mid');C=load(D+'chopin-nocturne-op27-2.mid')
def span(d,a,b):return d['bars'][a-1]['sec'],d['bars'][b-1]['end_sec']
def chroma(d,t0,t1,hands=(0,1),slices=24):
    out=np.zeros((slices,12))
    for h in hands:
        for n in d['notes'][h]:
            s=d['sec_of'](n['s']);e=d['sec_of'](n['e'])
            for k in range(slices):
                a=t0+(t1-t0)*k/slices;b=t0+(t1-t0)*(k+1)/slices
                o=min(b,e)-max(a,s)
                if o>0:out[k][n['p']%12]+=o*(0.5+n['v']/127)
    return out
def sim(la,lb,ca,cb,lead=0.0):
    lt0,lt1=span(L,la,lb);ct0,ct1=span(C,ca,cb)
    slices=int(round((lt1-lt0)/1.5))
    a=chroma(L,lt0,lt1,slices=slices);b=chroma(C,ct0,ct1,slices=slices)
    cs=[float(x@y/(np.linalg.norm(x)*np.linalg.norm(y)+1e-9)) for x,y in zip(a,b)]
    return np.mean(cs),(ct1-ct0)/(lt1-lt0),(lt1-lt0)
if __name__=='__main__':
    for args in [(1,13,1,10),(3,8,2,5),(3,13,2,10),(18,27,26,33),(18,27,26,35),(18,24,26,30),(29,41,17,25),(28,43,33,38),(28,43,17,25),(31,40,34,38),(44,61,46,58),(47,61,62,77),(47,61,70,77),(56,61,75,77),(53,61,72,77),(44,55,46,57),(44,47,46,49),(18,23,2,6)]:
        s,r,d=sim(*args);print(args,'sim %.2f ratio %.2f dur %.1f'%(s,r,d))
