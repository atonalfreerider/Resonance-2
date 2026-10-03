"""Use PianoVisionFormatter's MuseScore playback renderer to attach timed articulations."""
import argparse
import hashlib
import json
import sys
from pathlib import Path


def extract(score_path, midi_path, formatter):
    sys.path.insert(0,str(Path(formatter).resolve()))
    from pianovision.mscx import read_score
    from pianovision.render import Renderer
    import mido
    score=read_score(str(score_path));renderer=Renderer(score);renderer.render()
    midi=mido.MidiFile(midi_path)
    # The companion MIDI excludes simplified and orchestral-piano staves.
    retained=[s for s in score.staves if not any(x in s.part.track_name.lower() for x in ('simplified','orchestral'))]
    if len(retained)!=len(midi.tracks):raise ValueError('Score staves do not match the filtered MIDI')
    staff_tracks={s.idx:i for i,s in enumerate(retained)}
    onsets={}
    for track,messages in enumerate(midi.tracks):
        tick=0
        for msg in messages:
            tick+=msg.time
            if msg.type=='note_on' and msg.velocity:onsets.setdefault((track,msg.note),[]).append(tick)
    marks=[];seen=set();unmatched=0
    for channel in range(len(renderer.events)):
        for tick,_,event in renderer.events.items(channel):
            if event.type!='on' or event.velo<=0 or event.note is None or event.orig_staff not in staff_tracks:continue
            chord=event.note.chord
            accented=any(any(x in a.subtype.lower() for x in ('accent','marcato','sforz')) for a in chord.articulations)
            if not accented:continue
            track=staff_tracks[event.orig_staff];possible=onsets.get((track,event.pitch),[])
            if not possible:unmatched+=1;continue
            closest=min(possible,key=lambda t:abs(t-tick))
            if abs(closest-tick)>12:unmatched+=1;continue
            key=(track,event.pitch,closest)
            if key in seen:continue
            seen.add(key);marks.append(dict(Track=track+1,Pitch=event.pitch,Beat=closest/midi.ticks_per_beat))
    return marks,dict(renderer='PianoVisionFormatter.pianovision.render.Renderer',formatter=str(Path(formatter).resolve()),
        sourceScoreSha256=hashlib.sha256(Path(score_path).read_bytes()).hexdigest(),matchedAccentNotes=len(marks),unmatchedAccentNotes=unmatched,
        matching='Original rendered staff, pitch and playback tick, within 12 source ticks; aligned track adds conductor')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--score',required=True);p.add_argument('--midi',required=True);p.add_argument('--formatter',required=True);p.add_argument('--output',required=True)
    a=p.parse_args();marks,report=extract(a.score,a.midi,a.formatter);out=Path(a.output)
    out.write_text(json.dumps(marks,indent=2),encoding='utf-8');out.with_name('score-accents.provenance.json').write_text(json.dumps(report,indent=2),encoding='utf-8');print(json.dumps(report,indent=2))
