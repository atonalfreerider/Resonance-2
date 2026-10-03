using System;
using System.Linq;
using System.Collections.Generic;

// A virtual visual channel: source note payloads and sounding score are untouched.
public static class MelodySelection
{
    public sealed class Accent {public int Track,Pitch;public double Beat;}
    public static PreparedPatternSong.MelodyStrand[] Group(PreparedPatternSong song,int[] tracks,Accent[] accents=null)
    {
        var part=new PreparedPatternSong{Notes=song.Notes.Where(n=>tracks.Contains(n.Track)).ToArray(),TrackNames=song.TrackNames,EndBeat=song.EndBeat,Tempos=song.Tempos,LeadVocalTrack=song.LeadVocalTrack};
        Build(part,accents);return part.Melody;
    }
    public static void Build(PreparedPatternSong song,Accent[] accents=null)
    {
        accents??=Array.Empty<Accent>();
        var marked=accents.Select(a=>(a.Track,a.Pitch,(long)Math.Round(a.Beat*480))).ToHashSet();
        var candidates=song.Notes.Where(n=>n.Channel!=10&&n.Pitch>=48&&n.Length>.04)
            .Where(n=>song.TrackNames==null||n.Track>=song.TrackNames.Length||!new[]{"timpani","drum","double bass","tuba"}.Any(x=>song.TrackNames[n.Track].Contains(x,StringComparison.OrdinalIgnoreCase)))
            .GroupBy(n=>(n.Track,n.Channel)).ToArray();
        if(candidates.Length==0){song.Melody=Array.Empty<PreparedPatternSong.MelodyStrand>();return;}
        bool Acc(MidiCycleAnalysis.Hit n)=>marked.Contains((n.Track,n.Pitch,(long)Math.Round(n.Beat*480)));
        string Name(int track)=>song.TrackNames!=null&&track<song.TrackNames.Length?song.TrackNames[track]:"";
        var selected=new List<MidiCycleAnalysis.Hit>();(int Track,int Channel) previous=(-1,-1);
        for(double start=0;start<song.EndBeat;start+=2){
            var scored=candidates.Select(lane=>{
                var notes=lane.Where(n=>n.Beat<start+2&&n.Beat+n.Length>start).ToArray();
                if(notes.Length==0)return (lane,score:double.NegativeInfinity);
                var attacks=notes.GroupBy(n=>Math.Round(n.Beat,5)).ToArray();
                double poly=attacks.Average(g=>g.Count()),density=attacks.Length/2.0;
                bool lead=lane.Key.Track==song.LeadVocalTrack||new[]{"vocal","singing","melody","lead","solo"}.Any(x=>Name(lane.Key.Track).Contains(x,StringComparison.OrdinalIgnoreCase));
                double score=(lead?12:0)+notes.Average(n=>n.Velocity)*3+notes.Count(Acc)*3
                    +Math.Min(2,notes.Average(n=>Math.Min(2,n.Length)))-Math.Max(0,poly-2)*2-Math.Max(0,density-3)
                    +(lane.Key==previous?1.5:0)+(notes.Average(n=>n.Pitch)-60)/24.0;
                return (lane,score);
            }).OrderByDescending(x=>x.score).First();
            if(double.IsNegativeInfinity(scored.score))continue;
            previous=scored.lane.Key;
            foreach(var group in scored.lane.Where(n=>n.Beat>=start&&n.Beat<start+2).GroupBy(n=>Math.Round(n.Beat,5))){
                var top=group.OrderByDescending(Acc).ThenByDescending(n=>n.Pitch).ToArray();
                selected.Add(top[0]);
                // Retain close harmony only on sparse authored lead lanes or explicit accents.
                if(top.Length==2&&top[0].Pitch-top[1].Pitch<=12&&(Acc(top[1])||Name(previous.Track).Contains("vocal",StringComparison.OrdinalIgnoreCase)||Name(previous.Track).Contains("singing",StringComparison.OrdinalIgnoreCase)))selected.Add(top[1]);
            }
        }
        var strands=new[]{new List<PreparedPatternSong.MelodyNote>(),new List<PreparedPatternSong.MelodyNote>()};
        double Seconds(double beat){var t=song.Tempos.LastOrDefault(t=>t.Beat<=beat)??song.Tempos[0];return t.Seconds+(beat-t.Beat)*t.Microseconds/1000000.0;}
        foreach(var group in selected.GroupBy(n=>Math.Round(n.Beat,5)).OrderBy(g=>g.Key)){
            var notes=group.OrderByDescending(n=>n.Pitch).Take(2).ToArray();double time=Seconds(notes[0].Beat);
            // Prevent pedal/legato overlaps from accumulating highlight voices.
            foreach(var strand in strands)if(strand.Count>0)strand[^1].End=Math.Min(strand[^1].End,time);
            for(int i=0;i<notes.Length;i++){var n=notes[i];strands[i].Add(new(){Pitch=n.Pitch,Start=Seconds(n.Beat),End=Seconds(n.Beat+n.Length),Velocity=n.Velocity,SourceTrack=n.Track,SourceChannel=n.Channel,Accent=Acc(n)});}
        }
        song.Melody=strands.Select((notes,i)=>new PreparedPatternSong.MelodyStrand{Track=-2,Channel=0,Rank=i,Notes=notes.ToArray()}).Where(s=>s.Notes.Length>0).ToArray();
        song.MelodySource=accents.Length>0?"Authored MuseScore accents, sparse lead lanes and phrase continuity":"Sparse vocal/lead lanes, note salience and phrase continuity (estimated)";
        if(song.PianoConcerto){song.PianoMelody=Group(song,song.PianoTracks,accents);song.OrchestraMelody=Group(song,song.OrchestraTracks,accents);}
    }

    public static void SelfTest()
    {
        var notes=new List<MidiCycleAnalysis.Hit>();
        for(int i=0;i<16;i++){
            notes.Add(new(){Track=1,Channel=2,Pitch=64+i%5,Beat=i,Length=1.2,Velocity=.6f});
            for(int j=0;j<5;j++)notes.Add(new(){Track=2,Channel=3,Pitch=72+j,Beat=i,Length=2,Velocity=.95f});
        }
        var song=new PreparedPatternSong{EndBeat=16,Notes=notes.ToArray(),TrackNames=new[]{"","Lead vocal","Piano"},LeadVocalTrack=1,Tempos=new[]{new PreparedPatternSong.Tempo{Beat=0,Seconds=0,Microseconds=500000}}};
        Build(song);
        if(song.Melody.SelectMany(s=>s.Notes).Any(n=>n.SourceTrack!=1))throw new Exception("Sparse declared vocal did not beat dense accompaniment");
        if(song.Notes.Length!=96)throw new Exception("Melody changed source score");
        var all=song.Melody.SelectMany(s=>s.Notes).ToArray();
        foreach(var time in all.SelectMany(n=>new[]{n.Start,n.End}))if(all.Count(n=>n.Start<=time&&n.End>time)>2)throw new Exception("Melody exceeded two simultaneous notes");
        song.LeadVocalTrack=-1;song.TrackNames=new[]{"","Violin","Piano"};Build(song,notes.Where(n=>n.Track==1).Select(n=>new Accent{Track=n.Track,Pitch=n.Pitch,Beat=n.Beat}).ToArray());
        if(song.Melody.SelectMany(s=>s.Notes).Any(n=>!n.Accent))throw new Exception("Authored accented melody lost precedence");
        song.PianoConcerto=true;song.PianoTracks=new[]{2};song.OrchestraTracks=new[]{1};Build(song);
        if(song.PianoMelody.Length==0||song.OrchestraMelody.Length==0||song.PianoMelody.SelectMany(s=>s.Notes).Any(n=>n.SourceTrack!=2)||song.OrchestraMelody.SelectMany(s=>s.Notes).Any(n=>n.SourceTrack!=1))throw new Exception("Concerto melodies must be independently selected for both groups");
        Console.WriteLine("PASS: Melody selection prefers sparse vocals and authored accents, preserves source notes and caps overlap at two");
    }
}
