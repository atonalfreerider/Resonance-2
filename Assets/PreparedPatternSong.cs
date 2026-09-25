using System;
using System.Collections.Generic;
using System.Linq;

// Portable offline analysis contract. No inference or pattern discovery in playback.
[Serializable] public sealed class PreparedPatternSong
{
    // Version 2 adds named form roles, progression loops and grammar words.
    // Version 3 adds pattern compression: one fundamental loop per section family,
    // every visit described as passes of that loop with their variations, and the
    // repeated section groups (verse + chorus) that return through the song.
    // Version 4 adds detected key changes and tonal tensions (tonicizations).
    // Version 5 adds instrument patterns (each pitched lane's own chord loops, their
    // repetitions and variations) and a lyric sheet synced to the music, with meter and rhyme.
    public const int CurrentVersion=5;
    public int Version=CurrentVersion,TrackCount,LeadVocalTrack=-1;
    public string Style="",FormName="",Summary="",FormGrammar="";
    public int SongBars,FundamentalBars;
    public Pattern[] Patterns=Array.Empty<Pattern>();
    public SectionGroup[] Groups=Array.Empty<SectionGroup>();
    // The fundamental of a section family: its shortest repeating chord loop, taken from
    // the passes that agree most (so a varied first pass does not become the reference).
    // Loop chords start at 0 and are spelled in the key of the family's first visit.
    [Serializable] public sealed class Pattern
    {
        public int Family,Reference,LoopBars,SectionBars,Visits,Passes;
        public double LoopBeats;
        public string Name="",Role="",Letter="",Short="",Word="";
        public SongFormAnalysis.ChordStep[] Loop=Array.Empty<SongFormAnalysis.ChordStep>();
    }
    // One turn of the family loop inside a visit. Offset is where in the loop the pass
    // begins (a pickup begins late). Changed holds loop-relative [start,end) beat pairs
    // whose harmony differs from the fundamental after transposition.
    [Serializable] public sealed class Pass
    {
        public double Start,End,Offset;
        public int Transpose;
        public bool Partial;
        public double[] Changed=Array.Empty<double>();
    }
    // A modulation: the key from this beat on, the key it left, and why it counts.
    [Serializable] public sealed class KeyChange
    {
        public double Beat;
        public int Key,From;
        public bool Minor,FromMinor;
        public string Evidence="";
    }
    // A tonicization inside a key (V/V, vii°/V, the Neapolitan, a borrowed chord): the torus
    // leans toward Target over [Start, End) and relaxes after, unless Completes, when the key
    // change to Target follows.
    [Serializable] public sealed class Tension
    {
        public double Start,End;
        public int Target;
        public bool TargetMinor,Completes;
        public string Kind="";
        public float Amount;
    }
    public KeyChange[] KeyChanges=Array.Empty<KeyChange>();
    public Tension[] Tensions=Array.Empty<Tension>();
    // A run of different families that recurs as a unit (Verse + Chorus, or a classical part).
    [Serializable] public sealed class SectionGroup
    {
        public int Id,Visits;
        public string Name="",Short="";
        public int[] Families=Array.Empty<int>();
    }
    // Instrument patterns (v5). Each pitched lane is read as the chords it plays; windows of
    // the song's loop grid (cut shorter where the lane's own chords repeat sooner) that play
    // the same chords are one pattern, and windows that change one or more chords are its
    // variations. A run of the same pattern is one play repeated (×n).
    public InstrumentPart[] Parts=Array.Empty<InstrumentPart>();
    [Serializable] public sealed class InstrumentPart
    {
        public int Track,Channel,Bars,FundamentalBars;
        public string Name="",Role="",Grammar="";
        public bool Vocal;
        public LanePattern[] Patterns=Array.Empty<LanePattern>();
        public LanePlay[] Plays=Array.Empty<LanePlay>();
    }
    // A lane's fundamental: loop-relative chords (the consensus of its plays), in the key of
    // its first play. Variations counts the distinct chord changes heard against it.
    [Serializable] public sealed class LanePattern
    {
        public int Id,LoopBars,Plays,Variations;
        public double LoopBeats;
        public string Letter="";
        public SongFormAnalysis.ChordStep[] Loop=Array.Empty<SongFormAnalysis.ChordStep>();
    }
    // One window of a lane: which pattern it plays (-1: the lane rests), which variation (0 is
    // the fundamental itself), its transposition, and its place in a run of repeats
    // (Repeat of Run). Changed holds loop-relative [start,end) beat pairs of changed chords.
    [Serializable] public sealed class LanePlay
    {
        public double Start,End,Offset;
        public int Pattern=-1,Variation,Transpose,Run=1,Repeat=1;
        public bool Partial;
        public double[] Changed=Array.Empty<double>();
    }
    public InstrumentPart PartOf(int track,int channel)=>Array.Find(Parts??Array.Empty<InstrumentPart>(),p=>p.Track==track&&p.Channel==channel);
    // Lyrics (v5): a lyric sheet synced to the music. Sung syllables follow the vocal lane's
    // notes; spoken (rapped) syllables sit on the drum grid. Every syllable carries its lexical
    // stress, where it falls in the bar, and how strongly it is delivered.
    public LyricSheet Lyrics;
    [Serializable] public sealed class LyricSheet
    {
        public string Source="",Sync="";
        public int Track=-1,Channel=-1;
        public Syllable[] Syllables=Array.Empty<Syllable>();
        public LyricLine[] Lines=Array.Empty<LyricLine>();
        public Stanza[] Stanzas=Array.Empty<Stanza>();
        public MeterMatch[] Matches=Array.Empty<MeterMatch>();
        public RhymeLink[] Links=Array.Empty<RhymeLink>();
    }
    // Index is the syllable's place in its word. Position is its beat within the bar; Metric is
    // 2 on the bar's downbeat, 1 on a beat, 0 on an upbeat (the "and"), -1 between. Teeth is how
    // many eighth-note teeth of the drum rack it is held across. Vibrato depth is in semitones
    // and rate in hertz, from VibratoStart.
    [Serializable] public sealed class Syllable
    {
        public string Text="",Word="";
        public int Line,WordIndex,Index,Pitch=-1,Stress,Metric,Teeth=1;
        public double Start,End,VibratoStart;
        public float Emphasis,Position,Vibrato,VibratoRate;
        public bool Spoken,WordStart,WordEnd;
    }
    // Stresses is the line's lexical pattern (/ stressed, x unstressed); Beats the delivered
    // pattern (/ on a beat, x off it). Letter is the end-rhyme scheme letter within the stanza,
    // and FrontGroup links lines that begin alike (alliteration, a head rhyme or repeated words).
    [Serializable] public sealed class LyricLine
    {
        public int Stanza,First,Count,RhymeGroup=-1,FrontGroup=-1;
        public double Start,End;
        public string Text="",Stresses="",Beats="",Meter="",EndRhyme="",FrontRhyme="",Letter="",FrontKind="";
        public bool Spoken;
    }
    [Serializable] public sealed class Stanza
    {
        public int FirstLine,Lines,Section=-1;
        public string Name="",Scheme="",Meter="";
        public bool Spoken;
    }
    // The lyric's semantic structure as links between words (A, B: the first syllable of each):
    // "end" rhymes between line ends, "internal" rhymes inside or across neighbouring lines,
    // "front" rhymes between line openings, and "repeat" for a line heard again (a refrain).
    [Serializable] public sealed class RhymeLink
    {
        public int A,B;
        public string Kind="",Key="";
    }
    // Line A against line B (the same line of two stanzas sung or spoken to the same pattern):
    // how well their stresses line up on the beat, and what differs.
    [Serializable] public sealed class MeterMatch
    {
        public int A,B;
        public float Score;
        public string Note="";
    }
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
    public MelodyStrand[] MelodyStrands=Array.Empty<MelodyStrand>();
    [Serializable] public sealed class MelodyStrand { public int Track,Channel,Rank;public MelodyNote[] Notes; }
    [Serializable] public sealed class MelodyNote { public int Pitch;public double Start,End;public float Velocity; }
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
        // Form analysis (v2): display label ("Verse 2", "A′"), pop/classical role, family letter,
        // visit number, transposition from the family's first visit, loop count and the
        // inner-dial cycle (one progression loop, or one phrase of a long loop).
        public string Label="",Role="",Letter="";
        public int Visit=1,Transpose,PhraseBars=4,Loops=1,KeyRoot=-1;
        public bool KeyMinor;
        public double CycleBeats,Similarity=1;
        // Pattern compression (v3): passes of the family loop, how this visit differs from
        // the fundamental, and its place in a recurring group.
        public Pass[] Passes=Array.Empty<Pass>();
        public string Variation="",Short="";
        public int Group=-1,GroupVisit;
        public string DisplayName=>string.IsNullOrEmpty(Label)?Name:Label;
        public double Cycle=>CycleBeats>0?CycleBeats:ProgressionBeats;
    }
    public Pattern PatternOf(Section section)=>Array.Find(Patterns,p=>p.Family==section.Family);
    // Older bundles: key changes are read off the key frames (no tensions without regeneration).
    public void EnsureKeyChanges(Func<double,double> beatAt)
    {
        KeyChanges??=Array.Empty<KeyChange>();Tensions??=Array.Empty<Tension>();
        if(KeyChanges.Length>0||Frames==null)return;
        var changes=new List<KeyChange>();int key=-1;bool minor=false;
        foreach(var f in Frames)
        {
            if(f.Key<0)continue;
            if(key>=0&&(f.Key!=key||f.Minor!=minor))changes.Add(new KeyChange{Beat=beatAt(f.Time),Key=f.Key,Minor=f.Minor,From=key,FromMinor=minor,Evidence="key frames"});
            key=f.Key;minor=f.Minor;
        }
        KeyChanges=changes.ToArray();
    }
    // Older bundles have no compression data: project each family's first visit as its own
    // fundamental so the wheel still shows the form. Regenerating replaces this.
    public bool EnsurePatterns()
    {
        Patterns??=Array.Empty<Pattern>();Groups??=Array.Empty<SectionGroup>();FormGrammar??="";
        if(Sections==null||Sections.Length==0)return false;
        foreach(var s in Sections){s.Passes??=Array.Empty<Pass>();s.Variation??="";s.Short??="";}
        if(Patterns.Length>0&&Sections.All(s=>s.Passes.Length>0&&PatternOf(s)!=null))return false;
        Patterns=Sections.GroupBy(s=>s.Family).Select(g=>{var first=g.First();double loop=Math.Max(.25,first.ProgressionBeats);
            return new Pattern{Family=g.Key,Reference=Array.IndexOf(Sections,first),Name=first.Name,Role=string.IsNullOrEmpty(first.Role)?first.Name:first.Role,Letter=first.Letter,
                Short=first.Name.Length<=3?first.Name:first.Name.Substring(0,1),LoopBeats=loop,Visits=g.Count(),
                LoopBars=Math.Max(1,Measures?.Count(m=>m.Start>=first.Start-1e-6&&m.Start<first.Start+loop-1e-6)??1),Passes=Math.Max(1,(int)Math.Floor((first.End-first.Start)/loop+1e-6)),
                Loop=(first.Chords??Array.Empty<SongFormAnalysis.ChordStep>()).Select(c=>new SongFormAnalysis.ChordStep{Start=c.Start-first.Start,End=c.End-first.Start,Root=c.Root,Quality=c.Quality,Token=c.Token,Roman=c.Roman}).ToArray()};}).ToArray();
        foreach(var s in Sections)
        {
            double loop=PatternOf(s).LoopBeats;var passes=new List<Pass>();
            for(double a=s.Start;a<s.End-1e-6;a+=loop)passes.Add(new Pass{Start=a,End=Math.Min(s.End,a+loop),Partial=s.End-a<loop-1e-6});
            s.Passes=passes.ToArray();
        }
        return true;
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
