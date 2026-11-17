"""Render each hand of each score alone with MuseScore 4's own piano (notes of the other staff get <play>0</play>)."""
import re,shutil,subprocess,sys,zipfile
from pathlib import Path
MS=r'C:\Program Files\MuseScore 4\bin\MuseScore4.exe'
HERE=Path(__file__).resolve().parents[3]/'SongLibraryData/scores/liszt-chopin'
SCORES={'liszt':'liszt-consolation3.mscz','chopin':'chopin-nocturne-op27-2.mscz'}
def mute_staff(xml,staff):
    # the Staff blocks that hold Measures (after the Part definitions) are the ones to mute
    out=[];pos=0
    for m in re.finditer(r'<Staff id="%d">(?:(?!</Staff>).)*?</Staff>'%staff,xml,re.S):
        block=m.group(0)
        if '<Measure' not in block:continue
        out.append((m.start(),m.end(),block.replace('<Note>','<Note><play>0</play>')))
    res=xml;
    for s,e,b in reversed(out):res=res[:s]+b+res[e:]
    return res
def build(piece,hand):
    src=HERE/SCORES[piece];work=HERE/'hands';work.mkdir(exist_ok=True)
    dst=work/f'{piece}-{hand}.mscz'
    with zipfile.ZipFile(src) as z:
        names=z.namelist();mscx=[n for n in names if n.endswith('.mscx')][0]
        xml=z.read(mscx).decode('utf8')
        xml=mute_staff(xml,2 if hand=='rh' else 1)
        with zipfile.ZipFile(dst,'w',zipfile.ZIP_DEFLATED) as o:
            for n in names:o.writestr(n,xml.encode('utf8') if n==mscx else z.read(n))
    subprocess.run([MS,'-o',str(dst.with_suffix('.mp3')),str(dst)],check=True)
if __name__=='__main__':
    for p in SCORES:
        for h in('rh','lh'):build(p,h)
