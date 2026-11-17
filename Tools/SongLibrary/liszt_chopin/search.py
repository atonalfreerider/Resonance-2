import sys
from swap_probe import *
res=[]
for la in range(1,58):
  for ll in (4,5,6):
    lb=la+ll-1
    if lb>61:continue
    lt0,lt1=span(L,la,lb);ld=lt1-lt0
    for ca in range(1,74):
      for cl in range(3,9):
        cb=ca+cl-1
        if cb>77:continue
        ct0,ct1=span(C,ca,cb);r=(ct1-ct0)/ld
        if not .82<=r<=1.22:continue
        t=test(la,lb,ca,cb)
        a=t['L_rh_over_C_lh'];b=t['C_rh_over_L_lh']
        score=min(a[0]-a[1],b[0]-b[1])
        res.append((score,(la,lb,ca,cb),round(r,2),a,b))
res.sort(reverse=True)
for x in res[:25]:print(x)
