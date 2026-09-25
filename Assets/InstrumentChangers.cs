using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

// Instrument changers: every pitched lane is a CD changer, the same design as the drum wheel.
// Each of the lane's patterns (its chord loops, prepared offline) is a disc in the stack; the
// pattern playing rises to the top and turns once per loop under a comb at twelve o'clock.
//  Top disc  — its chord band (colour is tonality, as everywhere), changed chords outlined in
//              white with a spark as they pass the comb, and the lane's notes as dimples:
//              radius is pitch, played dimples lit, the sounding one flashing.
//  Hub       — the pattern's letter with its variation primes (A, A′) and the repeat count
//              (2 of 4): cyclic repetition.
//  Stack     — the other patterns waiting below, lettered on their edge; when the lane moves
//              to another pattern its disc slides up and the old one sinks.
//  Grammar   — the lane written out below (C A B×2 A B′…), the current token lit: compression.
// The vocal changer's melody is drawn as a curve through its notes; in lyric mode it faces
// the viewer and the sung syllables ride it (LyricWheel).
public sealed class InstrumentChangers
{
    readonly Main main;readonly MidiPlayer midi;
    PreparedPatternSong source;
    // Per lane: its notes, the grammar tokens and which token each play belongs to, and each
    // pattern disc's shown place in the stack (0 on top), eased toward its target.
    sealed class Lane
    {
        public PreparedPatternSong.InstrumentPart Part;public MidiCycleAnalysis.Hit[] Notes=Array.Empty<MidiCycleAnalysis.Hit>();
        public List<string> Tokens=new();public int[] TokenOf=Array.Empty<int>();public float[] Shown=Array.Empty<float>();public int Low=48,High=84;
        // Per note, the chord sounding at its onset (its dimple's colour); the stack order,
        // recomputed only when the lane moves to another play.
        public SongFormAnalysis.ChordStep[] NoteChord=Array.Empty<SongFormAnalysis.ChordStep>();public int[] Order=Array.Empty<int>();public int OrderedPlay=-2;
    }
    readonly List<Lane> lanes=new();
    public int LaneCount=>lanes.Count;
    // For validation: the pattern and repeat each lane shows now.
    public readonly List<(string name,string token,int repeat,int run,bool top)> Showing=new();
    public InstrumentChangers(Main main,MidiPlayer midi){this.main=main;this.midi=midi;}
    public PreparedPatternSong.InstrumentPart VocalPart=>lanes.FirstOrDefault(l=>l.Part.Vocal)?.Part;

    public static string Token(PreparedPatternSong.InstrumentPart part,PreparedPatternSong.LanePlay play)
    {
        if(play.Pattern<0||play.Pattern>=part.Patterns.Length)return "·";
        string token=part.Patterns[play.Pattern].Letter+(play.Variation switch{0=>"",1=>"′",2=>"″",3=>"‴",_=>"^"+play.Variation});
        if(play.Transpose!=0){int t=HarmonyModel.Mod(play.Transpose);token+=t<=6?"+"+t:"−"+(12-t);}
        return token;
    }
    public void Load(PreparedPatternSong data)
    {
        source=data;lanes.Clear();
        if(data?.Parts==null)return;
        foreach(var part in data.Parts)
        {
            if(part.Patterns==null||part.Patterns.Length==0)continue;
            var lane=new Lane{Part=part,Notes=(data.Notes??Array.Empty<MidiCycleAnalysis.Hit>()).Where(n=>n.Track==part.Track&&n.Channel==part.Channel).OrderBy(n=>n.Beat).ToArray(),Shown=Enumerable.Range(0,part.Patterns.Length).Select(i=>(float)i+1).ToArray()};
            if(lane.Notes.Length>0){lane.Low=lane.Notes.Min(n=>n.Pitch);lane.High=Math.Max(lane.Low+7,lane.Notes.Max(n=>n.Pitch));}
            var chords=data.Chords??Array.Empty<SongFormAnalysis.ChordStep>();lane.NoteChord=new SongFormAnalysis.ChordStep[lane.Notes.Length];
            for(int i=0,c=0;i<lane.Notes.Length;i++){while(c+1<chords.Length&&chords[c+1].Start<=lane.Notes[i].Beat+1e-6)c++;lane.NoteChord[i]=c<chords.Length&&chords[c].Start<=lane.Notes[i].Beat+1e-6&&lane.Notes[i].Beat<chords[c].End?chords[c]:null;}
            // The grammar, folded as PatternPrep writes it, with each play's token index.
            var raw=part.Plays.Select(p=>Token(part,p)).ToList();lane.TokenOf=new int[raw.Count];
            for(int i=0;i<raw.Count;)
            {
                int j=i;while(j<raw.Count&&raw[j]==raw[i])j++;
                for(int k=i;k<j;k++)lane.TokenOf[k]=lane.Tokens.Count;
                lane.Tokens.Add(raw[i]=="·"||j-i==1?raw[i]:$"{raw[i]}×{j-i}");i=j;
            }
            lanes.Add(lane);
        }
    }
    public static int PlayAt(PreparedPatternSong.InstrumentPart part,double beat)
    {
        var plays=part.Plays;int lo=0,hi=plays.Length-1,found=-1;
        while(lo<=hi){int mid=(lo+hi)/2;if(plays[mid].Start<=beat+1e-6){found=mid;lo=mid+1;}else hi=mid-1;}
        return found>=0&&beat<plays[found].End+1e-6?found:-1;
    }
    public static double LoopPhase(PreparedPatternSong.InstrumentPart part,PreparedPatternSong.LanePlay play,double beat)
    {
        double loop=Math.Max(.25,part.Patterns[Math.Max(0,play.Pattern)].LoopBeats);
        return ((play.Offset+beat-play.Start)/loop%1+1)%1;
    }

    // ---------- the disc in perspective: phase 0 at twelve o'clock (the far edge), clockwise ----------
    public struct Disc
    {
        public Vector2 Center;public float Radius,Tilt;
        public Vector2 At(double phase,float radius){float a=(float)(phase*Math.PI*2);return Center+new Vector2(Mathf.Sin(a)*radius,-Mathf.Cos(a)*radius*Tilt);}
    }
    static void Ellipse(Painter2D p,Disc d,float radius,int steps=72){p.BeginPath();for(int i=0;i<=steps;i++){var q=d.At(i/(double)steps,radius);if(i==0)p.MoveTo(q);else p.LineTo(q);}p.ClosePath();}
    public static void Sector(Painter2D p,Disc d,float inner,float outer,double from,double to,Color color)
    {
        if(to-from<1e-5)return;int steps=Mathf.Max(3,Mathf.CeilToInt((float)(to-from)*96));
        p.fillColor=color;p.BeginPath();
        for(int i=0;i<=steps;i++){var q=d.At(from+(to-from)*i/steps,outer);if(i==0)p.MoveTo(q);else p.LineTo(q);}
        for(int i=steps;i>=0;i--)p.LineTo(d.At(from+(to-from)*i/steps,inner));
        p.ClosePath();p.Fill();
    }
    public static void Arc(Painter2D p,Disc d,float radius,double from,double to,Color color,float width)
    {
        if(to-from<1e-5)return;int steps=Mathf.Max(3,Mathf.CeilToInt((float)(to-from)*96));
        p.strokeColor=color;p.lineWidth=width;p.BeginPath();
        for(int i=0;i<=steps;i++){var q=d.At(from+(to-from)*i/steps,radius);if(i==0)p.MoveTo(q);else p.LineTo(q);}
        p.Stroke();
    }
    // A steel plate seen from above and in front: its front rim as a band of thickness, then the face.
    public static void Plate(Painter2D p,Disc d,float thickness,Color face,Color rim,Color edge)
    {
        if(thickness>.1f&&d.Tilt<.98f)
        {
            p.fillColor=rim;p.BeginPath();
            for(int i=0;i<=36;i++){var q=d.At(.25+i/72.0,d.Radius);if(i==0)p.MoveTo(q);else p.LineTo(q);}
            for(int i=36;i>=0;i--)p.LineTo(d.At(.25+i/72.0,d.Radius)+new Vector2(0,thickness));
            p.ClosePath();p.Fill();
        }
        p.fillColor=face;Ellipse(p,d,d.Radius);p.Fill();
        p.strokeColor=edge;p.lineWidth=1;Ellipse(p,d,d.Radius);p.Stroke();
    }
    public static void Comb(Painter2D p,Disc d,Color color)
    {
        var tip=d.At(0,d.Radius+1);p.fillColor=color;p.BeginPath();p.MoveTo(tip);p.LineTo(tip+new Vector2(-4.5f,-7));p.LineTo(tip+new Vector2(4.5f,-7));p.ClosePath();p.Fill();
    }
    static void Text(MeshGenerationContext ctx,string text,Vector2 center,float size,Color color)=>ctx.DrawText(text,center-new Vector2(text.Length*size*.29f,size*.62f),size,color);
    static Color Alpha(Color c,float a)=>new(c.r,c.g,c.b,c.a*a);

    // The top disc's face: chord band, variation outline, note dimples. Returns the contact
    // at the comb (for the spark) and whether a changed chord is passing it.
    readonly List<(Vector2 at,double onset,MidiCycleAnalysis.Hit note,SongFormAnalysis.ChordStep chord)> points=new(256);
    readonly List<(int key,Color color,Vector2 at,float size)> dimples=new(256);
    public (Vector2 comb,bool varied) Face(MeshGenerationContext ctx,Painter2D p,OrreryBloom bloom,Disc d,PreparedPatternSong.InstrumentPart part,MidiCycleAnalysis.Hit[] notes,SongFormAnalysis.ChordStep[] noteChord,int low,int high,PreparedPatternSong.LanePlay play,double beat,float alpha,bool melody)
    {
        var pattern=part.Patterns[play.Pattern];double loop=Math.Max(.25,pattern.LoopBeats);
        double phase=LoopPhase(part,play,beat),spin=-phase;
        float band=Mathf.Clamp(d.Radius*.14f,3,12),outer=d.Radius-2,inner=outer-band;
        foreach(var chord in pattern.Loop)
        {
            var shifted=new SongFormAnalysis.ChordStep{Root=chord.Rest?-1:HarmonyModel.Mod(chord.Root+play.Transpose),Quality=chord.Quality};
            double a=chord.Start/loop,b=chord.End/loop;bool here=phase>=a&&phase<b;
            Sector(p,d,inner,outer,spin+a,spin+b,Alpha(CyclicOrrery.ChordColor(shifted,main.currentKey),(here?1:.72f)*alpha));
        }
        bool varied=false;
        for(int i=0;i+1<play.Changed.Length;i+=2)
        {
            double a=play.Changed[i]/loop,b=play.Changed[i+1]/loop;
            Arc(p,d,inner-1.5f,spin+a,spin+b,FormHatch.Label(.9f*alpha),2);
            if(phase>=a&&phase<b)varied=true;
        }
        // Note dimples between the hub and the band: radius is pitch. Only this play's notes.
        float hub=d.Radius*.22f,span=inner-3-hub;
        int from=0,hi=notes.Length;while(from<hi){int mid=(from+hi)/2;if(notes[mid].Beat<play.Start-1e-6)from=mid+1;else hi=mid;}
        points.Clear();
        for(int i=from;i<notes.Length&&notes[i].Beat<play.End-1e-6;i++)
        {
            var n=notes[i];double at=(play.Offset+n.Beat-play.Start)/loop;
            float r=hub+span*Mathf.InverseLerp(low,high,n.Pitch);
            points.Add((d.At(spin+at,r),n.Beat,n,noteChord.Length>i?noteChord[i]:null));
        }
        if(melody&&points.Count>1)
        {
            // The melody as a curve through its notes (each note's own span then a bend to the next).
            p.strokeColor=FormHatch.Label(.35f*alpha);p.lineWidth=1.2f;p.BeginPath();p.MoveTo(points[0].at);
            for(int i=1;i<points.Count;i++){var a=points[i-1].at;var b=points[i].at;var m=(a+b)/2;p.BezierCurveTo(new Vector2(m.x,a.y),new Vector2(m.x,b.y),b);}
            p.Stroke();
        }
        // Dimples of one colour share one path: a handful of fills instead of one per note.
        dimples.Clear();
        foreach(var (at,onset,n,chord) in points)
        {
            double elapsed=beat-onset;bool played=elapsed>=0;bool sounding=played&&beat<onset+n.Length;
            float size=Mathf.Clamp(d.Radius*.035f,1.2f,3.2f)*(sounding?1.5f:1);
            var hue=CyclicOrrery.ChordColor(chord,main.currentKey);
            var color=played?Alpha(Color.Lerp(hue,Color.white,sounding?.6f:.15f),alpha):Alpha(FormHatch.Ink(.4f),alpha);
            Color32 key=color;dimples.Add((key.r|key.g<<8|key.b<<16|key.a<<24,color,at,size));
            float energy=midi.IsPlaying&&played?Mathf.Exp(-(float)elapsed*5):0;
            if(energy>.04f)bloom.Disk(at,size*1.2f,hue,energy*.8f*alpha);
        }
        dimples.Sort((a,b)=>a.key.CompareTo(b.key));
        for(int i=0;i<dimples.Count;)
        {
            int j=i;p.fillColor=dimples[i].color;p.BeginPath();
            while(j<dimples.Count&&dimples[j].key==dimples[i].key){var (_,_,at,size)=dimples[j];p.MoveTo(at+new Vector2(size,0));p.Arc(at,size,Angle.Degrees(0),Angle.Degrees(360));j++;}
            p.Fill();i=j;
        }
        return (d.At(0,(inner+outer)/2),varied);
    }

    // The changers in a row inside the area (the vocal first).
    public void Draw(MeshGenerationContext ctx,Painter2D p,OrreryBloom bloom,Rect area,double beat,bool skipVocal=false)
    {
        using var perf=Perf.Changers.Auto();
        Showing.Clear();
        var shown=lanes.Where(l=>!(skipVocal&&l.Part.Vocal)).Take(6).ToList();
        if(shown.Count==0||area.width<40||area.height<60)return;
        float cell=area.width/shown.Count,tilt=.42f;
        // The stack (face, four waiting discs, rim) and three rows of labels must fit the bay.
        float radius=Mathf.Max(12,Mathf.Min(cell*.4f,(area.height-58)/(2*tilt+.62f)));
        float blend=Main.ReducedMotion?1:1-Mathf.Exp(-Time.unscaledDeltaTime*7);
        for(int k=0;k<shown.Count;k++)
        {
            var lane=shown[k];var part=lane.Part;
            int pi=PlayAt(part,beat);var play=pi>=0?part.Plays[pi]:null;
            int top=play!=null&&play.Pattern>=0?play.Pattern:-1;
            // Target stack: the playing pattern on top, the rest in order of their last play.
            if(lane.OrderedPlay!=pi)
            {
                lane.OrderedPlay=pi;var order=Enumerable.Range(0,part.Patterns.Length).OrderBy(i=>i==top?0:1).ThenByDescending(i=>Array.FindLastIndex(part.Plays,x=>x.Pattern==i&&x.Start<=beat)).ToList();
                lane.Order=new int[part.Patterns.Length];for(int i=0;i<part.Patterns.Length;i++)lane.Order[i]=order.IndexOf(i)+(top<0?1:0);
            }
            for(int i=0;i<part.Patterns.Length;i++)lane.Shown[i]=Mathf.Lerp(lane.Shown[i],lane.Order[i],blend);
            float gap=Mathf.Clamp(radius*.12f,3,8),thickness=Mathf.Clamp(radius*.06f,2,4);
            var center=new Vector2(area.x+cell*(k+.5f),area.y+radius*tilt+12);
            // Bottom of the stack first; only the first few discs are drawn.
            foreach(int i in Enumerable.Range(0,part.Patterns.Length).Where(i=>lane.Shown[i]<4.5f).OrderByDescending(i=>lane.Shown[i]))
            {
                float rank=lane.Shown[i];var d=new Disc{Center=center+new Vector2(0,rank*gap),Radius=radius,Tilt=tilt};
                bool onTop=rank<.5f;
                Plate(p,d,thickness,FormHatch.Shadow,new Color(.1f,.15f,.15f,.95f),Alpha(FormHatch.Metal,onTop?1:.7f));
                if(!onTop)
                {
                    // Waiting discs show their letter on the right edge.
                    var tab=d.At(.3,radius)+new Vector2(6,0);float fade=Mathf.Clamp01(1.2f-rank*.25f);
                    ctx.DrawText(part.Patterns[i].Letter,tab-new Vector2(0,5),9,FormHatch.Label(.55f*fade));
                    continue;
                }
                float alpha=Mathf.Clamp01(1-rank*2);
                if(play!=null&&play.Pattern==i)
                {
                    var (comb,varied)=Face(ctx,p,bloom,d,part,lane.Notes,lane.NoteChord,lane.Low,lane.High,play,beat,alpha,part.Vocal);
                    Dot(p,comb,varied?3.2f:2,varied?Color.white:FormHatch.Label(.8f));
                    if(varied){bloom.Disk(comb,3,Color.white,midi.IsPlaying?.9f:.3f);}
                    string letter=Token(part,play);
                    Text(ctx,letter,center+new Vector2(0,-2),Mathf.Clamp(radius*.3f,9,16),Color.white);
                    if(play.Run>1)Text(ctx,$"{play.Repeat}/{play.Run}",center+new Vector2(0,Mathf.Clamp(radius*.3f,9,16)*.9f),9,FormHatch.Label(.75f));
                    Showing.Add((part.Name,letter,play.Repeat,play.Run,true));
                }
            }
            if(top<0)Showing.Add((part.Name,"·",0,0,false));
            Comb(p,new Disc{Center=center,Radius=radius,Tilt=tilt},FormHatch.Label(.85f));
            // Name, compression and grammar under the stack.
            float y=center.y+radius*tilt+3*gap+thickness+8;
            string name=part.Name.Length>16?part.Name.Substring(0,15)+"…":part.Name;
            Text(ctx,name,new Vector2(center.x,y),10,FormHatch.Label(.9f));
            Text(ctx,$"{part.Bars} bars → {part.FundamentalBars}",new Vector2(center.x,y+12),8,FormHatch.Label(.5f));
            Grammar(ctx,p,lane,pi,new Vector2(center.x,y+25),cell-6);
        }
    }
    static void Dot(Painter2D p,Vector2 c,float r,Color color){p.fillColor=color;p.BeginPath();p.Arc(c,r,Angle.Degrees(0),Angle.Degrees(360));p.Fill();}
    // The lane's grammar around its current token, which is lit.
    static void Grammar(MeshGenerationContext ctx,Painter2D p,Lane lane,int play,Vector2 center,float width)
    {
        if(lane.Tokens.Count==0)return;
        int current=play>=0&&play<lane.TokenOf.Length?lane.TokenOf[play]:-1;
        const float size=9,glyph=size*.56f;
        int first=Math.Max(0,current),last=first;float used=lane.Tokens[first].Length*glyph;
        // Grow the window around the current token while it fits.
        while(true)
        {
            bool grew=false;
            if(last+1<lane.Tokens.Count&&used+(lane.Tokens[last+1].Length+1)*glyph<=width){last++;used+=(lane.Tokens[last].Length+1)*glyph;grew=true;}
            if(first>0&&used+(lane.Tokens[first-1].Length+1)*glyph<=width){first--;used+=(lane.Tokens[first].Length+1)*glyph;grew=true;}
            if(!grew)break;
        }
        float x=center.x-used/2;
        if(first>0)ctx.DrawText("…",new Vector2(x-glyph*1.2f,center.y),size,FormHatch.Label(.4f));
        for(int t=first;t<=last;t++)
        {
            bool now=t==current;string token=lane.Tokens[t];
            ctx.DrawText(token,new Vector2(x,center.y),size,now?Color.white:FormHatch.Label(t<current?.45f:.65f));
            if(now){p.strokeColor=FormHatch.Ink(.9f);p.lineWidth=1.2f;p.BeginPath();p.MoveTo(new Vector2(x,center.y+size+2));p.LineTo(new Vector2(x+token.Length*glyph,center.y+size+2));p.Stroke();}
            x+=(token.Length+1)*glyph;
        }
        if(last<lane.Tokens.Count-1)ctx.DrawText("…",new Vector2(x,center.y),size,FormHatch.Label(.4f));
    }
}
