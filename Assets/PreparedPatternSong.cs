using System;
using System.Collections.Generic;
using System.Linq;

// Portable offline analysis contract. No inference or pattern discovery in playback.
[Serializable] public sealed class PreparedPatternSong
{
    public int Version=1,TrackCount,LeadVocalTrack=-1;
    public string[] TrackNames=Array.Empty<string>();
    public string MidiSha256,Title,Provenance;
    public double Duration,EndBeat;
    public Tempo[] Tempos;
    public MidiCycleAnalysis.Bar[] Measures;
    public MidiCycleAnalysis.Hit[] Notes;
    public Disk[] Disks;
    public Section[] Sections;
    public SongFormAnalysis.ChordStep[] Chords;
    public SongFormAnalysis.ChordStep[] RegionPhases=Array.Empty<SongFormAnalysis.ChordStep>();
    public Frame[] Frames;
    public DrumBar[] DrumBars=Array.Empty<DrumBar>();
    public DrumFamily[] DrumFamilies=Array.Empty<DrumFamily>();
    [Serializable] public sealed class DrumFamily { public int Id,Numerator,Denominator;public MidiCycleAnalysis.Hit[] Slots; }
    [Serializable] public sealed class DrumBar { public int Family,Variant,Numerator,Denominator;public double Start,End;public MidiCycleAnalysis.Hit[] Hits;public int[] Slots; }
    public int Key=-1,TemplateNoteCount,PatternNoteCount;
    public bool Minor;
    public string KeySource;
    public RhythmTemplate[] Templates;
    public FormNode[] Form;
    [Serializable] public sealed class RhythmTemplate
    {
        public int Id,Track,Channel;
        public double Beats;
        public MidiCycleAnalysis.Hit[] Slots;
        public PitchVariant[] Variants;
    }
    [Serializable] public sealed class PitchVariant
    {
        public int Transpose;
        public int[] PitchDelta;
        public float[] Velocities;
        public double[] BeatOffsets,LengthOffsets;
    }
    [Serializable] public sealed class PatternPlay { public int Template,Variant;public double Start,End; }
    [Serializable] public sealed class InstrumentLane
    {
        public int Track,Channel;public string Name;
        public PatternPlay[] Plays;
    }
    [Serializable] public sealed class Route { public int Section,Child;public double Turns; }
    [Serializable] public sealed class FormNode
    {
        public int Id,Parent=-1,Family=-1;public string Name,Path;
        public int[] Children;
        public Route[] Route;
    }
    [Serializable] public sealed class Tempo { public double Beat,Seconds,Microseconds; }
    [Serializable] public sealed class Disk
    {
        public int Id,Track,Channel,Bars;
        public string Name;
        public double Beats;
        public MidiCycleAnalysis.Hit[] Hits;
        public Visit[] Visits;
    }
    [Serializable] public sealed class Visit
    {
        public int Bar;
        public double Beat,Seconds;
        public MidiCycleAnalysis.Hit[] Hits;
    }
    [Serializable] public sealed class Section
    {
        public int FirstBar,BarCount,Family;
        public string Name;
        public double Start,End,ProgressionBeats;
        public SongFormAnalysis.ChordStep[] Chords;
        public string ParentPath="Song";
        public int Node;
        public InstrumentLane[] Lanes;
    }
    [Serializable] public sealed class Voice { public int Pitch,Channel,Track;public float Velocity; }
    [Serializable] public sealed class Frame
    {
        public double Time;
        public int Key=-1;
        public bool Minor;
        public Voice[] Voices,Attacks;
    }
    public static PreparedPatternSong Capture(MidiCycleAnalysis cycles,SongFormAnalysis form)
    {
        return new PreparedPatternSong {
            EndBeat=cycles.EndBeat,Tempos=cycles.ExportTempos(),Measures=cycles.Measures.ToArray(),Notes=cycles.Notes.ToArray(),
            Disks=cycles.Patterns.Select(p=>new Disk {Id=p.Id,Track=p.Track,Channel=p.Channel,Bars=p.Bars,Name=p.Name,Beats=p.Beats,Hits=p.Hits.ToArray(),
                Visits=p.Occurrences.Select(o=>new Visit{Bar=o.Bar,Beat=o.Beat,Seconds=o.Seconds,Hits=o.Hits.ToArray()}).ToArray()}).ToArray(),
            Sections=form.Sections.Select(s=>new Section{FirstBar=s.FirstBar,BarCount=s.BarCount,Family=s.Family.Id,Name=s.Family.Name,Start=s.Start,End=s.End,ProgressionBeats=s.ProgressionBeats,Chords=s.Chords.ToArray()}).ToArray(),
            Chords=form.Timeline.ToArray(),Provenance=form.BoundarySource
        };
    }
    public SongFormAnalysis RestoreForm()
    {
        var form=new SongFormAnalysis{EndBeat=EndBeat,BoundarySource=Provenance};
        foreach(var group in Sections.GroupBy(s=>s.Family).OrderBy(g=>g.Key))form.Families.Add(new SongFormAnalysis.Family{Id=group.Key,Name=group.First().Name});
        foreach(var s in Sections){var f=form.Families.First(f=>f.Id==s.Family);var item=new SongFormAnalysis.Section{FirstBar=s.FirstBar,BarCount=s.BarCount,Start=s.Start,End=s.End,ProgressionBeats=s.ProgressionBeats,Family=f};item.Chords.AddRange(s.Chords);f.Occurrences.Add(item);form.Sections.Add(item);}
        form.Timeline.AddRange(Chords);return form;
    }
}
