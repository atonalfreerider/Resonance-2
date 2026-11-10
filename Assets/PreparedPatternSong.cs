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
    // Version 6 adds the motif layer: the piece reduced to atoms (cell shapes with chirality) and the
    // transformations that place them (tonal or real transposition, mirror, augmentation, the parallel mode),
    // nested powers (a unit stated n times, each a step from the last), phrases and periods, and sections
    // related by returns with edits (A′ = A − bars 6–13 + coda). Every note is checked to decode from it.
    // Readers accept 5 and 6. A version-5 bundle has an empty layer (Schema 0): the wheels stay classic.
    public const int CurrentVersion=6,MinimumReadableVersion=5;
    // Version stays the first field written (System.Text.Json writes fields in declaration order): the song list and
    // deploy_quest.py read it from a bundle's first 64 KB, and the motif layer after it can run to megabytes.
    public int Version=CurrentVersion,TrackCount,LeadVocalTrack=-1;
    public MotifLayer Motifs=new MotifLayer();
    public bool HasMotifs()=>Motifs!=null&&Motifs.Schema>=1&&Motifs.Nodes!=null&&Motifs.Nodes.Length>1;

    public static class MotifKinds { public const int Piece=0,Section=1,Phrase=2,Period=3,SubPhrase=4,Power=5,Unit=6,Coda=7,Intro=8,Arrival=9; }
    public static class MotifRelations { public const int Return=0,Power=1,Run=2,Doubling=3,Alternation=4,Liquidation=5,CrossReference=6; }
    public static class MotifSparks { public const int Inserted=0,Deleted=1,Substituted=2,Altered=3,Shifted=4,Lengthened=5,Octave=6,Ornament=7; }
    public static class MotifEdits { public const int Match=0,Delete=1,Insert=2,Substitute=3; }

    [Serializable] public sealed class MotifLayer
    {
        public int Schema;                                    // 0: none (version 5, or the analysis was disabled: Status); 1: this layout
        public string Status="",Formula="",Caption="";       // Formula: the piece line, then section and phrase lines, joined by '\n'
        public string[] Levels=Array.Empty<string>();        // names of node levels: Piece, Section, Phrase, Unit
        public int PrincipalStream=-1;                       // the stream the glyph follows by default (the melody)
        public MotifStream[] Streams=Array.Empty<MotifStream>();
        public MotifAtom[] Atoms=Array.Empty<MotifAtom>();
        public MotifParadigm[] Paradigms=Array.Empty<MotifParadigm>();
        public MotifNode[] Nodes=Array.Empty<MotifNode>();          // preorder; Nodes[0] = the piece; children contiguous
        public MotifCell[] Cells=Array.Empty<MotifCell>();          // grouped by stream (MotifStream.FirstCell, CellCount), by Start
        public MotifSpark[] Sparks=Array.Empty<MotifSpark>();       // grouped by cell (MotifCell.FirstSpark, SparkCount)
        public MotifRelation[] Relations=Array.Empty<MotifRelation>();
        public MotifEdit[] Edits=Array.Empty<MotifEdit>();          // return scripts (MotifRelation.FirstEdit, EditCount)
        public MotifStats Stats=new MotifStats();
    }
    // One monophonic line or texture. Kind: voice (a score voice), line (a lane's outer line), rest, texture,
    // both (both hands), drums. Staff and Voice come from a MuseScore import (-1 otherwise).
    [Serializable] public sealed class MotifStream
    {
        public int Id,Track,Channel,Staff=-1,Voice=-1,FirstCell,CellCount;
        public string Name="",Kind="";
        public bool Bass;
    }
    // A family of paradigms with one diminution signature, signs dropped (N neighbour, T turn, P passing or
    // sigh, A appoggiatura, Osc, Arp, Shape, Hold). The census is its chirality: members whose figure turns
    // upward (Positive) or downward (Negative), Neutral for achiral ones.
    [Serializable] public sealed class MotifAtom
    {
        public int Id,Paradigms,Members,Positive,Negative,Neutral;
        public string Kind="",Name="",Signature="";
    }
    // An exact orbit: its prototype (the first statement, Notes relative to its first note) and every cell
    // that is the prototype transposed, mirrored or augmented, plus variants with residual edits. Clock is
    // the glyph's pitch clock: 7 (scale steps) or 12 (semitones). Symmetry: the transpositions that fix a
    // texture shape ({0,6} a tritone, {0,3,6,9} a diminished seventh).
    [Serializable] public sealed class MotifParadigm
    {
        public int Id,Atom=-1,Stream,Members,Mirrored,Variants,Clock=7,Sign,PrototypeCell=-1;
        public string Letter="",Name="",Signature="";
        public double Beats;
        public bool Achiral;
        public MotifNote[] Notes=Array.Empty<MotifNote>();
        public int[] Symmetry=Array.Empty<int>();
    }
    // Onset and Length in beats from the cell start; Step and Semitones from the first note; Alter: the
    // chromatic alteration against the local scale; Chord: 0 the line's note, 1.. chord notes below it.
    // Role: the diminution token ("" structural, "N+", "N-", "P-", "T+", "A-", "Rep").
    [Serializable] public sealed class MotifNote
    {
        public double Onset,Length;
        public int Step,Semitones,Alter,Chord;
        public bool Tied;
        public string Role="";
    }
    // One cell of a stream: which paradigm it states and how (from the prototype). Degree is the scale degree
    // of its first note in the local key (0 = tonic; for Clock 12 the semitone above the tonic): the glyph's
    // angle. Real: the transposition is named chromatically. ModeChange: the parallel mode (P). Relation: the
    // power this cell is an iteration of. Text: the musician's name of the relation to its previous statement
    // ("tonal sequence up a second", "in the parallel minor", "an octave lower with C♮: ♭6 borrowed from E minor").
    [Serializable] public sealed class MotifCell
    {
        public int Stream,Paradigm,Node=-1,Steps,Semitones,Degree,FirstSpark=-1,SparkCount,Relation=-1;
        public double Start,End;
        public bool Inverted,Exact=true,Real,ModeChange;
        public float Scale=1;
        public string Text="";
    }
    // A residual edit: where the music departs from the formula (the white sparks). Note: the prototype note
    // it concerns (-1 for an inserted note). Tag names it ("C♮ = ♭6 from E minor", "held 13/16", "P+").
    [Serializable] public sealed class MotifSpark
    {
        public double Beat;
        public int Cell=-1,Kind,Pitch=-1,Note=-1;
        public string Tag="";
    }
    // The flattened tree. Level: 0 piece, 1 section, 2 phrase or period, 3 sub-phrase, power or unit (powers
    // nest; Depth is the tree depth). Prototype: the node this one restates (A for A′, a1 for a1′, the first
    // iteration for later ones) under Steps/Semitones/Inverted. Projected: inherited from the prototype through
    // the return's script. Omits: prototype children the copy leaves out ("a2, a1′"). Powers: Count statements,
    // each StepSteps/StepSemitones from the last; Iteration is a child's index in its parent power. Order: the
    // generator's order (0 translation, 1 loop, 2 alternation, n a cycle). Text: the node's formula line.
    [Serializable] public sealed class MotifNode
    {
        public int Id,Parent=-1,FirstChild=-1,ChildCount,Depth,Level,Kind;
        public double Start,End;
        public string Label="",Name="",Text="",Omits="";
        public int Prototype=-1,Relation=-1,Paradigm=-1,Steps,Semitones;
        public bool Inverted,Real,Projected;
        public int Count=1,Iteration,StepSteps,StepSemitones,Order;
        public float Bits,Evidence;
    }
    // A relation between spans or streams: Return (Node restates Source with the Edits script), Power (Count
    // statements stepped by Steps/Semitones), Run (a texture chain stepped by Semitones per event; Period > 0
    // when it cycles back to its pitch classes; Symmetry: the transpositions that fix its hand shape), Doubling
    // (StreamB doubles StreamA; Pattern lists the octave registers, "-24,-12"; Contrary marks mirror motion),
    // Alternation (Pattern "RRLL"), Liquidation (Pattern "8,4,2": the shrinking period in quarters of a tactus),
    // CrossReference (bar 16 = bar 6 a sixth higher). Text is the musician's sentence for captions and tours.
    [Serializable] public sealed class MotifRelation
    {
        public int Id,Kind,Level,Node=-1,Source=-1,StreamA=-1,StreamB=-1,Steps,Semitones,Count=1,Period,Order,FirstEdit=-1,EditCount;
        public double Start,End,SourceStart=-1,SourceEnd=-1;
        public bool Inverted,Real,ModeChange,CrossScale,Contrary;
        public float Bits,SavedBits;
        public string Text="",Pattern="";
        public int[] Symmetry=Array.Empty<int>();
    }
    [Serializable] public sealed class MotifEdit
    {
        public int Op;                                       // MotifEdits: match, delete (source not restated), insert (new target), substitute
        public double SourceStart=-1,SourceEnd=-1,TargetStart=-1,TargetEnd=-1;
        public float Bits;
    }
    // Description lengths in bits. Literal: every note coded alone; Entropy: order-0 entropy coder; Cells:
    // paradigms without powers or returns; Model: the full reduction (articulation excluded, reported apart).
    [Serializable] public sealed class MotifStats
    {
        public int Notes,Streams,Cells,Paradigms,Atoms,Nodes,Relations,Powers,Returns,MirroredCells,DerivedNotes;
        public float BitsLiteral,BitsEntropy,BitsCells,BitsModel,BitsResidual,BitsArticulation,Derived;
    }

    public MelodyStrand[] PianoMelody=Array.Empty<MelodyStrand>(),OrchestraMelody=Array.Empty<MelodyStrand>();
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
    public MelodyStrand[] Melody=Array.Empty<MelodyStrand>();
    public string MelodySource="";
    public bool PianoConcerto;
    public int[] PianoTracks=Array.Empty<int>(),OrchestraTracks=Array.Empty<int>();
    [Serializable] public sealed class MelodyStrand { public int Track,Channel,Rank;public MelodyNote[] Notes; }
    [Serializable] public sealed class MelodyNote { public int Pitch,SourceTrack,SourceChannel;public bool Accent;public double Start,End;public float Velocity; }
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
