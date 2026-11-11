using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

// The structure wheel's model (Docs/MOTIF-REDUCTION.md §14.4): a version-6 bundle's motif layer indexed for drawing, shared by
// the desktop deck (PatternWheelDeck) and the Quest wheel. It is built once per bundle instance, glyphs included, so that
// Resolve, Ghost, the glyph lookups and anchors allocate nothing while a song plays.
//  Path     — the section, phrase and unit (nodes) and the featured stream's cell sounding at a beat, with how far each has gone.
//  Ghost    — inside a level-1 return, where the music is in what it restates (A′ at bar 62 is A at bar 1); it jumps the omission.
//  Glyph    — a paradigm's prototype on the pitch clock: angle is the scale step (or semitone), radius is time spiralling out.
//  Anchors  — the Director's annotationAnchor ("omission:A′", "phrase:a3", …) as a target the renderer turns into a point.
public sealed class MotifWheelModel
{
    static readonly ConditionalWeakTable<PreparedPatternSong,MotifWheelModel> cache=new();
    static readonly ConditionalWeakTable<PreparedPatternSong,MotifWheelModel>.CreateValueCallback create=song=>new MotifWheelModel(song);
    public static MotifWheelModel For(PreparedPatternSong song)=>song==null?null:cache.GetValue(song,create);

    public struct Path { public int Section,Phrase,Unit,Cell,Paradigm,Stream; public double SectionU,PhraseU,UnitU,CellU; public bool ChangedSinceLast;
        public static Path None=>new Path{Section=-1,Phrase=-1,Unit=-1,Cell=-1,Paradigm=-1,Stream=-2}; }
    public enum AnchorKind { None,Arc,Omission,Staircase,Glyph,Ghost,Cell,Span }
    // Ring: 1 sections, 2 phrases or periods, 3 units and cells. Node: the arc's node (the prototype's for an omission); Start/End in beats.
    public struct AnchorTarget { public AnchorKind Kind; public int Ring,Node,Cell,Relation; public double Start,End; }
    // What ring 3 draws for a phrase: a leaf unit, a bracket over a power or sub-phrase, or a cell of the stream where no unit is.
    // Offset: the staircase's cumulative step (scale steps, or semitones for a real power) of the power iterations it lies in.
    public struct RingItem { public int Node,Cell,Depth,Offset; public bool Bracket; public double Start,End; }
    // A level-1 return's omission: the prototype's span the copy leaves out (Source), where the copy jumps it (TargetAt), and
    // the span of the copy during which the omission is the thing to watch: from the copy's start through the first restated
    // span after the jump, so it is lit before, during and after the skip.
    public sealed class Omission { public int Relation,Copy,Prototype; public double SourceStart,SourceEnd,TargetAt,LitFrom,LitUntil; public string Tooltip=""; }
    // A paradigm's prototype drawn once: unit radius, y down, the first note at twelve o'clock, clockwise as the pitch rises.
    public sealed class GlyphGeometry
    {
        public int Clock=7;public bool Tied;
        public Vector2[] Path=Array.Empty<Vector2>();public float[] Time=Array.Empty<float>();    // the line, Time in 0..1 of the cell
        public Vector2[] Beads=Array.Empty<Vector2>();public bool[] Hollow=Array.Empty<bool>();    // every note; ornaments hollow
        public int[] Links=Array.Empty<int>();                                                     // bead pairs: a chord note to its line note
        public float[] Angles=Array.Empty<float>();                                                // per note, degrees clockwise from twelve
        // Where the playhead is on the line at a time 0..1 of the cell.
        public Vector2 PointAt(float time)
        {
            int n=Path.Length;if(n==0)return Vector2.zero;if(time<=Time[0])return Path[0];if(time>=Time[n-1])return Path[n-1];
            int lo=0,hi=n-1;while(hi-lo>1){int mid=(lo+hi)>>1;if(Time[mid]<=time)lo=mid;else hi=mid;}
            float span=Time[hi]-Time[lo];return span<=1e-6f?Path[hi]:Vector2.Lerp(Path[lo],Path[hi],(time-Time[lo])/span);
        }
    }

    public readonly PreparedPatternSong Song;public readonly PreparedPatternSong.MotifLayer Layer;
    public bool Valid {get;}
    public readonly PreparedPatternSong.MotifNode[] Nodes;public readonly PreparedPatternSong.MotifCell[] Cells;
    public readonly PreparedPatternSong.MotifParadigm[] Paradigms;public readonly PreparedPatternSong.MotifRelation[] Relations;
    public readonly int[] Sections=Array.Empty<int>();     // level-1 node ids in time order
    public readonly int[] Identity=Array.Empty<int>();     // per node: the node it restates in the end (itself for a prototype)
    public readonly int[] IdentityIndex=Array.Empty<int>();// per node: its identity's order of first appearance among nodes of its level
    public readonly bool[] Recurs=Array.Empty<bool>();     // per node: its identity is stated more than once at its level
    public readonly int[] PeriodOrder=Array.Empty<int>();  // per node: a period's generator order (-1: no sign) from its section's line
    public readonly int[] ReturnOf=Array.Empty<int>();     // per node: the level-1 Return relation that restates it as a copy, else -1
    public readonly Omission[] Omissions=Array.Empty<Omission>();
    public readonly string[] ReturnLabel=Array.Empty<string>();   // per relation: "A′ ← A" for a level-1 return
    readonly int[] returns1=Array.Empty<int>(),runs=Array.Empty<int>();
    readonly GlyphGeometry[] glyphs=Array.Empty<GlyphGeometry>();
    readonly List<(int track,int channel,int stream)> lanes=new();

    MotifWheelModel(PreparedPatternSong song)
    {
        Song=song;Layer=song.Motifs;Valid=song.HasMotifs();
        Nodes=Layer?.Nodes??Array.Empty<PreparedPatternSong.MotifNode>();Cells=Layer?.Cells??Array.Empty<PreparedPatternSong.MotifCell>();
        Paradigms=Layer?.Paradigms??Array.Empty<PreparedPatternSong.MotifParadigm>();Relations=Layer?.Relations??Array.Empty<PreparedPatternSong.MotifRelation>();
        if(!Valid)return;
        int n=Nodes.Length;var root=Nodes[0];
        Sections=new int[root.ChildCount];for(int k=0;k<Sections.Length;k++)Sections[k]=root.FirstChild+k;
        // Identity: a copy (A′), a primed phrase (a1′), a projected child and a later power iteration restate a prototype.
        Identity=new int[n];IdentityIndex=new int[n];Recurs=new bool[n];PeriodOrder=new int[n];ReturnOf=new int[n];
        var count=new int[n];var order=new int[n];var levelCount=new int[8];
        for(int i=0;i<n;i++){int j=i;for(int guard=0;guard<n&&Nodes[j].Prototype>=0&&Nodes[j].Prototype!=j;guard++)j=Nodes[j].Prototype;Identity[i]=j;count[j]++;}
        for(int i=0;i<n;i++)order[i]=-1;
        foreach(int i in Preorder()){int id=Identity[i],level=Math.Min(7,Nodes[i].Level);if(order[id]<0)order[id]=levelCount[level]++;IdentityIndex[i]=order[id];Recurs[i]=count[id]>1;}
        for(int i=0;i<n;i++){PeriodOrder[i]=-1;ReturnOf[i]=-1;}
        // A section of periods carries its generators' signs after its line: "B1 · B2 · … · B5    → → ⬡ ⇄ ·".
        foreach(int s in Sections)
        {
            var section=Nodes[s];if(section.ChildCount==0||section.Text==null)continue;bool periods=true;
            for(int k=0;k<section.ChildCount;k++)periods&=Nodes[section.FirstChild+k].Kind==PreparedPatternSong.MotifKinds.Period;
            int cut=section.Text.LastIndexOf("    ",StringComparison.Ordinal);if(!periods||cut<0)continue;
            var signs=section.Text.Substring(cut+4).Split(' ',StringSplitOptions.RemoveEmptyEntries);if(signs.Length!=section.ChildCount)continue;
            for(int k=0;k<signs.Length;k++)PeriodOrder[section.FirstChild+k]=SignOrder(signs[k]);
        }
        // Level-1 returns: the ghost's scripts, the omissions and the bracket labels.
        ReturnLabel=new string[Relations.Length];var list=new List<int>();var omissions=new List<Omission>();var edits=Layer.Edits??Array.Empty<PreparedPatternSong.MotifEdit>();
        for(int r=0;r<Relations.Length;r++)
        {
            var rel=Relations[r];ReturnLabel[r]="";
            if(rel.Kind!=PreparedPatternSong.MotifRelations.Return||rel.Level!=1)continue;
            list.Add(r);if(rel.Node>=0&&rel.Node<n)ReturnOf[rel.Node]=r;
            ReturnLabel[r]=SpanLabel(rel.Start,rel.End)+" ← "+SpanLabel(rel.SourceStart,rel.SourceEnd);
            double at=rel.Start;
            for(int e=rel.FirstEdit;e>=0&&e<rel.FirstEdit+rel.EditCount&&e<edits.Length;e++)
            {
                var edit=edits[e];
                if(edit.Op==PreparedPatternSong.MotifEdits.Delete&&edit.SourceEnd>edit.SourceStart)
                {
                    // Lit until the end of the first span the copy restates after the jump (at least the copy's end).
                    double until=rel.End;
                    for(int f=e+1;f<rel.FirstEdit+rel.EditCount&&f<edits.Length;f++)if(edits[f].Op!=PreparedPatternSong.MotifEdits.Delete&&edits[f].TargetEnd>at){until=edits[f].TargetEnd;break;}
                    int prototype=rel.Source>=0&&rel.Source<n?rel.Source:SectionAt(edit.SourceStart);
                    omissions.Add(new Omission{Relation=r,Copy=rel.Node,Prototype=prototype,SourceStart=edit.SourceStart,SourceEnd=edit.SourceEnd,TargetAt=at,LitFrom=rel.Start,LitUntil=until,
                        Tooltip=OmissionText(rel,rel.Node>=0&&rel.Node<n?Nodes[rel.Node]:null)});
                }
                else if(edit.TargetEnd>at)at=edit.TargetEnd;
            }
        }
        returns1=list.ToArray();Omissions=omissions.ToArray();
        // Runs that cycle back to their pitch classes with a symmetric hand shape: the glyph's gear while they play.
        var cycling=new List<int>();for(int r=0;r<Relations.Length;r++)if(Relations[r].Kind==PreparedPatternSong.MotifRelations.Run&&Relations[r].Period>0&&(Relations[r].Symmetry?.Length??0)>=2)cycling.Add(r);
        cycling.Sort((a,b)=>Relations[a].Start!=Relations[b].Start?Relations[a].Start.CompareTo(Relations[b].Start):a.CompareTo(b));runs=cycling.ToArray();
        glyphs=new GlyphGeometry[Paradigms.Length];for(int p=0;p<Paradigms.Length;p++)glyphs[p]=Build(Paradigms[p]);
    }
    IEnumerable<int> Preorder()
    {
        var stack=new Stack<int>();stack.Push(0);
        while(stack.Count>0){int i=stack.Pop();yield return i;var x=Nodes[i];for(int k=x.ChildCount-1;k>=0;k--)if(x.FirstChild+k<Nodes.Length)stack.Push(x.FirstChild+k);}
    }
    static int SignOrder(string sign)=>sign switch{"→"=>0,"·"=>1,"⇄"=>2,"△"=>3,"□"=>4,"⬠"=>5,"⬡"=>6,_=>sign.EndsWith("-cycle",StringComparison.Ordinal)&&int.TryParse(sign.Substring(0,sign.Length-6),out int k)?k:-1};
    // "A′", or "(V2 C2)" for a union of sections.
    string SpanLabel(double start,double end)
    {
        var labels=new List<string>();
        foreach(int s in Sections){var x=Nodes[s];if(x.Start>=start-1e-6&&x.End<=end+1e-6||x.Start<=start+1e-6&&end<=x.End+1e-6)labels.Add(x.Label);}
        return labels.Count==1?labels[0]:"("+string.Join(" ",labels)+")";
    }
    // "bars 6–13 (a2, a1′) are left out in A′", the bars as the return's sentence names them.
    static string OmissionText(PreparedPatternSong.MotifRelation rel,PreparedPatternSong.MotifNode copy)
    {
        string bars="";const string key="leaving out ";int at=rel.Text?.IndexOf(key,StringComparison.Ordinal)??-1;
        if(at>=0){int end=rel.Text.IndexOfAny(new[]{' ',',',';'},rel.Text.IndexOf(' ',at+key.Length)+1);bars=end<0?rel.Text.Substring(at+key.Length):rel.Text.Substring(at+key.Length,end-at-key.Length);}
        string omits=copy!=null&&!string.IsNullOrEmpty(copy.Omits)?$" ({copy.Omits})":"";
        return $"{(bars.Length>0?bars:"these bars")}{omits} {(bars.StartsWith("bars",StringComparison.Ordinal)?"are":"is")} left out in {copy?.Label??"the return"}";
    }

    // ---------- the path: allocation-free binary searches ----------
    // The child of a node sounding at a beat, else -1. Children are contiguous and in time order.
    public int ChildAt(int parent,double beat)
    {
        if(parent<0||parent>=Nodes.Length)return -1;var x=Nodes[parent];int lo=x.FirstChild,hi=x.FirstChild+x.ChildCount-1;
        if(x.ChildCount<=0||lo<0)return -1;int found=-1;
        while(lo<=hi){int mid=(lo+hi)>>1;if(Nodes[mid].Start<=beat){found=mid;lo=mid+1;}else hi=mid-1;}
        return found>=0&&beat<Nodes[found].End?found:-1;
    }
    public int SectionAt(double beat)=>Valid?ChildAt(0,beat):-1;
    // The stream's cell sounding at a beat, else -1.
    public int CellAt(int stream,double beat)
    {
        var streams=Layer?.Streams;if(!Valid||streams==null||stream<0||stream>=streams.Length)return -1;
        int lo=streams[stream].FirstCell,hi=lo+streams[stream].CellCount-1,found=-1;
        while(lo<=hi){int mid=(lo+hi)>>1;if(Cells[mid].Start<=beat){found=mid;lo=mid+1;}else hi=mid-1;}
        return found>=0&&beat<Cells[found].End?found:-1;
    }
    static double Fraction(double start,double end,double beat)=>end>start?Math.Clamp((beat-start)/(end-start),0,1):0;
    public void Resolve(double beat,int featuredTrack,int featuredChannel,ref Path path)
    {
        int section=-1,phrase=-1,unit=-1,cell=-1,paradigm=-1,stream=StreamFor(featuredTrack,featuredChannel);double su=0,pu=0,uu=0,cu=0;
        if(Valid)
        {
            section=ChildAt(0,beat);
            if(section>=0){su=Fraction(Nodes[section].Start,Nodes[section].End,beat);phrase=ChildAt(section,beat);}
            if(phrase>=0){pu=Fraction(Nodes[phrase].Start,Nodes[phrase].End,beat);for(int x=ChildAt(phrase,beat),guard=0;x>=0&&guard<32;x=ChildAt(x,beat),guard++)unit=x;}
            if(unit>=0)uu=Fraction(Nodes[unit].Start,Nodes[unit].End,beat);
            cell=CellAt(stream,beat);
            if(cell>=0){paradigm=Cells[cell].Paradigm;cu=Fraction(Cells[cell].Start,Cells[cell].End,beat);}
        }
        path.ChangedSinceLast=section!=path.Section||phrase!=path.Phrase||unit!=path.Unit||cell!=path.Cell||paradigm!=path.Paradigm||stream!=path.Stream;
        path.Section=section;path.Phrase=phrase;path.Unit=unit;path.Cell=cell;path.Paradigm=paradigm;path.Stream=stream;
        path.SectionU=su;path.PhraseU=pu;path.UnitU=uu;path.CellU=cu;
    }
    // The lane's own stream for the glyph: the principal stream when it is that lane's, else its outer line, else its voice with
    // the most cells; the melody (track -2) and lanes without cells follow the principal stream.
    public int StreamFor(int track,int channel)
    {
        if(!Valid)return -1;
        for(int i=0;i<lanes.Count;i++)if(lanes[i].track==track&&lanes[i].channel==channel)return lanes[i].stream;
        var streams=Layer.Streams;int principal=Layer.PrincipalStream,best=-1,bestScore=-1;
        if(track!=-2)
            for(int s=0;s<streams.Length;s++)
            {
                var x=streams[s];if(x.Track!=track||x.Channel!=channel||x.CellCount==0)continue;
                int score=s==principal?3:x.Kind=="line"?2:x.Kind=="voice"?1:0;score=score*100000+Math.Min(99999,x.CellCount);
                if(score>bestScore){bestScore=score;best=s;}
            }
        if(best<0)best=principal>=0&&principal<streams.Length?principal:-1;
        lanes.Add((track,channel,best));return best;
    }

    // ---------- the ghost cursor ----------
    public bool Ghost(double beat,out double sourceBeat,out bool jumped)=>Ghost(beat,out sourceBeat,out jumped,out _);
    // Inside a level-1 return's restated spans: where the music is in its source. jumped: an omission lies behind the ghost.
    public bool Ghost(double beat,out double sourceBeat,out bool jumped,out int relation)
    {
        sourceBeat=-1;jumped=false;relation=-1;if(!Valid)return false;var edits=Layer.Edits;
        for(int i=0;i<returns1.Length;i++)
        {
            var rel=Relations[returns1[i]];if(beat<rel.Start||beat>=rel.End)continue;
            bool deleted=false;int last=Math.Min(edits.Length,rel.FirstEdit+rel.EditCount);
            for(int e=Math.Max(0,rel.FirstEdit);e<last;e++)
            {
                var edit=edits[e];
                if(edit.Op==PreparedPatternSong.MotifEdits.Delete){deleted=true;continue;}
                if(edit.Op==PreparedPatternSong.MotifEdits.Insert||edit.TargetStart<0||beat<edit.TargetStart||beat>=edit.TargetEnd)continue;
                double length=edit.TargetEnd-edit.TargetStart;
                sourceBeat=edit.SourceStart+(length>0?(beat-edit.TargetStart)*(edit.SourceEnd-edit.SourceStart)/length:0);
                jumped=deleted;relation=returns1[i];return true;
            }
            return false;
        }
        return false;
    }

    // The cycling run sounding at a beat, else -1.
    public int RunAt(double beat)
    {
        int lo=0,hi=runs.Length-1,found=-1;
        while(lo<=hi){int mid=(lo+hi)>>1;if(Relations[runs[mid]].Start<=beat){found=mid;lo=mid+1;}else hi=mid-1;}
        for(int i=found;i>=0&&i>found-8;i--)if(beat<Relations[runs[i]].End)return runs[i];
        return -1;
    }

    // ---------- glyphs ----------
    public GlyphGeometry Glyph(int paradigm)=>paradigm>=0&&paradigm<glyphs.Length?glyphs[paradigm]:null;
    public int ClockOf(int cell)=>cell>=0&&cell<Cells.Length&&Cells[cell].Paradigm>=0&&Cells[cell].Paradigm<Paradigms.Length&&Paradigms[Cells[cell].Paradigm].Clock==12?12:7;
    // Rotation is transposition: the first note sits on its scale degree (or its semitone above the tonic, Clock 12).
    public float GlyphAngle(int cell)
    {
        if(cell<0||cell>=Cells.Length)return 0;int clock=ClockOf(cell);
        return 360f*(((Cells[cell].Degree%clock)+clock)%clock)/clock;
    }
    public bool GlyphMirrored(int cell)=>cell>=0&&cell<Cells.Length&&Cells[cell].Inverted;
    static Vector2 Polar(float degrees,float radius){float a=degrees*Mathf.Deg2Rad;return new Vector2(Mathf.Sin(a),-Mathf.Cos(a))*radius;}
    static GlyphGeometry Build(PreparedPatternSong.MotifParadigm p)
    {
        int clock=p.Clock==12?12:7;var notes=p.Notes??Array.Empty<PreparedPatternSong.MotifNote>();int n=notes.Length;
        var g=new GlyphGeometry{Clock=clock,Tied=n>0&&notes[0].Tied};double beats=Math.Max(1e-6,p.Beats);
        float Time(double onset)=>(float)Math.Clamp(onset/beats,0,1);
        float Radius(double onset)=>.25f+.75f*Time(onset);
        int Position(int k)=>clock==12?notes[k].Semitones:notes[k].Step;
        g.Angles=new float[n];g.Beads=new Vector2[n];g.Hollow=new bool[n];
        var links=new List<int>();int lead=-1,leads=0,steps=0;
        for(int k=0;k<n;k++)
        {
            g.Angles[k]=360f*(((Position(k)%clock)+clock)%clock)/clock;g.Beads[k]=Polar(g.Angles[k],Radius(notes[k].Onset));g.Hollow[k]=!string.IsNullOrEmpty(notes[k].Role);
            if(notes[k].Chord!=0)continue;
            if(lead>=0){int d=Position(k)-Position(lead);if(d!=0&&Math.Abs(d)<=(clock==12?2:1))steps++;}
            lead=k;leads++;
        }
        // A chord's notes are joined into its shape: a dyad by a line (a tritone's is a diameter), more by their polygon in order
        // round the clock (a diminished seventh is a square).
        for(int k=0;k<n;)
        {
            int end=k+1;while(end<n&&notes[end].Chord!=0)end++;
            if(end-k==2){links.Add(k);links.Add(k+1);}
            else if(end-k>2)
            {
                var corners=new List<int>();for(int i=k;i<end;i++)corners.Add(i);corners.Sort((a,b)=>g.Angles[a]!=g.Angles[b]?g.Angles[a].CompareTo(g.Angles[b]):a.CompareTo(b));
                for(int i=0;i<corners.Count;i++){links.Add(corners[i]);links.Add(corners[(i+1)%corners.Count]);}
            }
            k=end;
        }
        g.Links=links.ToArray();
        // The line: a held note runs out along its radius; a step moves along the clock, a leap jumps as a chord. At most 64 points.
        int arcPoints=steps==0?0:Math.Clamp((64-(2*leads+1))/steps,0,4);
        var path=new List<Vector2>(64);var time=new List<float>(64);lead=-1;
        for(int k=0;k<n;k++)
        {
            if(notes[k].Chord!=0)continue;float t=Time(notes[k].Onset),r=Radius(notes[k].Onset);
            if(lead>=0)
            {
                path.Add(Polar(g.Angles[lead],r));time.Add(t);
                int d=Position(k)-Position(lead);
                if(d!=0&&Math.Abs(d)<=(clock==12?2:1))for(int i=1;i<=arcPoints;i++){path.Add(Polar(g.Angles[lead]+360f*d/clock*i/(arcPoints+1),r));time.Add(t);}
            }
            path.Add(g.Beads[k]);time.Add(t);lead=k;
        }
        if(lead>=0){float end=Time(notes[lead].Onset+notes[lead].Length);if(end>time[^1]){path.Add(Polar(g.Angles[lead],.25f+.75f*end));time.Add(end);}}
        g.Path=path.ToArray();g.Time=time.ToArray();return g;
    }

    // ---------- ring 3 ----------
    // What ring 3 draws for a phrase, into a caller's pooled list: units (leaves), brackets over powers and sub-phrases with the
    // staircase offsets of power iterations, and the stream's cells wherever no unit is (all of them when the phrase has none).
    public void PhraseItems(int phrase,int stream,List<RingItem> items)
    {
        items.Clear();if(phrase<0||phrase>=Nodes.Length)return;var p=Nodes[phrase];
        for(int k=0;k<p.ChildCount;k++)Walk(p.FirstChild+k,0,0,items);
        double at=p.Start;
        for(int k=0;k<=p.ChildCount;k++)
        {
            double gapEnd=k<p.ChildCount?Nodes[p.FirstChild+k].Start:p.End;
            if(gapEnd-at>1e-6)AddCells(stream,at,gapEnd,items);
            if(k<p.ChildCount)at=Math.Max(at,Nodes[p.FirstChild+k].End);
        }
    }
    void Walk(int node,int depth,int offset,List<RingItem> items)
    {
        var x=Nodes[node];
        items.Add(new RingItem{Node=node,Cell=-1,Depth=depth,Offset=offset,Bracket=x.ChildCount>0,Start=x.Start,End=x.End});
        int step=x.Kind==PreparedPatternSong.MotifKinds.Power?(x.StepSteps!=0?x.StepSteps:x.StepSemitones):0;
        for(int k=0;k<x.ChildCount&&depth<16;k++){var c=Nodes[x.FirstChild+k];Walk(x.FirstChild+k,depth+1,offset+step*c.Iteration,items);}
    }
    void AddCells(int stream,double from,double to,List<RingItem> items)
    {
        var streams=Layer?.Streams;if(streams==null||stream<0||stream>=streams.Length)return;
        int first=streams[stream].FirstCell,last=first+streams[stream].CellCount;
        for(int c=first;c<last;c++){var x=Cells[c];if(x.End<=from+1e-6)continue;if(x.Start>=to-1e-6)break;items.Add(new RingItem{Node=-1,Cell=c,Start=Math.Max(from,x.Start),End=Math.Min(to,x.End)});}
    }

    // ---------- Director anchors (§14.8) ----------
    string parsedAnchor;AnchorKind parsedKind;string parsedName;int parsedNode=-1,parsedRelation=-1;
    readonly List<int> parsedAtoms=new();
    public bool TryGetAnchor(string anchor,double beat,out AnchorTarget target)=>TryGetAnchor(anchor,beat,Valid?Layer.PrincipalStream:-1,out target);
    // stream: the glyph's stream, for "atom:". Parsing is cached by the anchor string's reference, so a cue's anchor costs nothing per frame.
    public bool TryGetAnchor(string anchor,double beat,int stream,out AnchorTarget target)
    {
        target=default;if(!Valid||string.IsNullOrEmpty(anchor))return false;
        if(!ReferenceEquals(anchor,parsedAnchor))Parse(anchor);
        int section=SectionAt(beat);
        switch(parsedKind)
        {
            case AnchorKind.Glyph:target.Kind=AnchorKind.Glyph;return true;
            case AnchorKind.Ghost:target.Kind=AnchorKind.Ghost;return Ghost(beat,out _,out _);
            case AnchorKind.Arc:
            {
                int node=parsedNode;
                // "phrase:a3": the current section's child with that label, falling back to the first with it.
                if(parsedName!=null&&section>=0){var s=Nodes[section];for(int k=0;k<s.ChildCount;k++)if(Nodes[s.FirstChild+k].Label==parsedName){node=s.FirstChild+k;break;}}
                if(node<0)return false;
                target.Kind=AnchorKind.Arc;target.Node=node;target.Ring=Math.Min(3,Math.Max(1,Nodes[node].Level));target.Start=Nodes[node].Start;target.End=Nodes[node].End;return true;
            }
            case AnchorKind.Omission:
            {
                for(int i=0;i<Omissions.Length;i++)
                    if(Omissions[i].Copy==parsedNode){var o=Omissions[i];target.Kind=AnchorKind.Omission;target.Ring=1;target.Node=o.Prototype;target.Relation=o.Relation;target.Start=o.SourceStart;target.End=o.SourceEnd;return true;}
                return false;
            }
            case AnchorKind.Staircase:
                if(parsedNode<0)return false;
                target.Kind=AnchorKind.Staircase;target.Ring=3;target.Node=parsedNode;target.Start=Nodes[parsedNode].Start;target.End=Nodes[parsedNode].End;return true;
            case AnchorKind.Span:
            {
                if(parsedRelation<0)return false;var r=Relations[parsedRelation];
                target.Kind=AnchorKind.Span;target.Relation=parsedRelation;target.Ring=Math.Min(3,Math.Max(1,r.Level));target.Node=r.Node;target.Start=r.Start;target.End=r.End;return true;
            }
            case AnchorKind.Cell:
            {
                // "atom:N": the glyph while a cell of that atom plays, otherwise its next cell in the phrase (on ring 3).
                if(parsedAtoms.Count==0)return false;int playing=CellAt(stream,beat);
                if(playing>=0&&AtomOf(Cells[playing].Paradigm)){target.Kind=AnchorKind.Glyph;target.Cell=playing;return true;}
                int phrase=section>=0?ChildAt(section,beat):-1;var streams=Layer.Streams;if(phrase<0||stream<0||stream>=streams.Length)return false;
                int first=streams[stream].FirstCell,last=first+streams[stream].CellCount;
                for(int c=first;c<last;c++)
                {
                    var x=Cells[c];if(x.Start<beat)continue;if(x.Start>=Nodes[phrase].End)break;
                    if(AtomOf(x.Paradigm)){target.Kind=AnchorKind.Cell;target.Ring=3;target.Cell=c;target.Node=phrase;target.Start=x.Start;target.End=x.End;return true;}
                }
                return false;
            }
        }
        return false;
    }
    bool AtomOf(int paradigm){if(paradigm<0||paradigm>=Paradigms.Length)return false;int atom=Paradigms[paradigm].Atom;for(int i=0;i<parsedAtoms.Count;i++)if(parsedAtoms[i]==atom)return true;return false;}
    void Parse(string anchor)
    {
        parsedAnchor=anchor;parsedKind=AnchorKind.None;parsedName=null;parsedNode=parsedRelation=-1;parsedAtoms.Clear();
        if(anchor=="glyph"){parsedKind=AnchorKind.Glyph;return;}
        if(anchor=="ghost"){parsedKind=AnchorKind.Ghost;return;}
        int colon=anchor.IndexOf(':');if(colon<0)return;string kind=anchor.Substring(0,colon),name=anchor.Substring(colon+1);
        switch(kind)
        {
            case "section":parsedKind=AnchorKind.Arc;foreach(int s in Sections)if(Nodes[s].Label==name){parsedNode=s;break;}break;
            case "phrase":parsedKind=AnchorKind.Arc;parsedName=name;parsedNode=FindLabel(name,2);break;
            case "node":parsedKind=AnchorKind.Arc;parsedNode=FindPath(name);break;
            case "omission":parsedKind=AnchorKind.Omission;parsedNode=FindPath(name);break;
            case "run":
            {
                // The power under that node: the node itself when it is one, else its first power in preorder.
                parsedKind=AnchorKind.Staircase;int node=FindPath(name);if(node<0)node=FindLabel(name,-1);
                if(node>=0){var stack=new Stack<int>();stack.Push(node);while(stack.Count>0){int i=stack.Pop();if(Nodes[i].Kind==PreparedPatternSong.MotifKinds.Power){parsedNode=i;break;}for(int k=Nodes[i].ChildCount-1;k>=0;k--)stack.Push(Nodes[i].FirstChild+k);}}
                break;
            }
            case "atom":
            {
                parsedKind=AnchorKind.Cell;var atoms=Layer.Atoms;
                for(int a=0;a<atoms.Length;a++)if(atoms[a].Kind==name||atoms[a].Name==name||atoms[a].Signature==name)parsedAtoms.Add(a);
                break;
            }
            case "relation":parsedKind=AnchorKind.Span;if(int.TryParse(name,out int r)&&r>=0&&r<Relations.Length)parsedRelation=r;break;
        }
    }
    // The first node with that label (at a level, or any level for -1), in preorder.
    int FindLabel(string label,int level){foreach(int i in Preorder())if((level<0||Nodes[i].Level==level)&&Nodes[i].Label==label)return i;return -1;}
    // "A′/a1": a level-1 label, then a label among each next node's descendants (nearest first).
    int FindPath(string path)
    {
        var parts=path.Split('/');int node=-1;
        foreach(int s in Sections)if(Nodes[s].Label==parts[0]){node=s;break;}
        if(node<0)return parts.Length==1?FindLabel(parts[0],-1):-1;
        for(int p=1;p<parts.Length&&node>=0;p++)
        {
            int found=-1;var queue=new Queue<int>();for(int k=0;k<Nodes[node].ChildCount;k++)queue.Enqueue(Nodes[node].FirstChild+k);
            while(queue.Count>0&&found<0){int i=queue.Dequeue();if(Nodes[i].Label==parts[p]){found=i;break;}for(int k=0;k<Nodes[i].ChildCount;k++)queue.Enqueue(Nodes[i].FirstChild+k);}
            node=found;
        }
        return node;
    }
}
