using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

// Lyric mode, in the pattern-wheel panel (the spoken lines ride the drum wheel: DrumLyricRack).
//  Vocal wheel — the vocal changer's top disc turned to face the viewer. Its melody is a curve
//                through the notes (radius is pitch; the disc turns once per loop under the comb
//                at twelve o'clock, so the line being sung arrives at the top). Each held note is
//                an arc at its pitch and the voice bends to the next note. Sung syllables sit on
//                the curve at their notes, always upright. The one being sung is lit and glows
//                with its emphasis; under vibrato its letters shimmer at the vibrato's rate.
//  Word graph  — the song's stanzas as tabs, the one being heard lit, and that stanza's lines as
//                small graphs of their words: each word a node on the line's beat grid, raised by
//                its pitch (sung) or its stress (spoken), the word being heard lit, the rhyme
//                letter at each line's end.
// Everything per song is prepared in Load; a frame walks arrays and allocates nothing.
public sealed class LyricWheel
{
    readonly Main main;readonly MidiPlayer midi;readonly InstrumentChangers changers;
    PreparedPatternSong source;PreparedPatternSong.LyricSheet lyrics;
    MidiCycleAnalysis.Hit[] vocalNotes=Array.Empty<MidiCycleAnalysis.Hit>();int[] noteOwner=Array.Empty<int>();
    PreparedPatternSong.Syllable[] sung=Array.Empty<PreparedPatternSong.Syllable>();double[] sungStart=Array.Empty<double>();
    int low=55,high=76;
    public float Tilt=.42f;
    // For validation: what is lit now.
    public PreparedPatternSong.Syllable Sung {get;private set;}
    public int SyllablesShown {get;private set;}
    public int BoardLine {get;private set;}=-1;
    public int BoardRows {get;private set;}
    public LyricWheel(Main main,MidiPlayer midi,InstrumentChangers changers){this.main=main;this.midi=midi;this.changers=changers;}

    // The word graph, laid out once per song in beats and graph units.
    sealed class Word {public string Text="";public double Start,End;public float Height;public int First,Last;public bool Stressed;}
    sealed class Row {public int Line;public Word[] Words=Array.Empty<Word>();public double Anchor,Span;public string Letter="";public bool Rhymes;}
    sealed class Verse {public int Stanza;public Row[] Rows=Array.Empty<Row>();public double Width,Lead;public string Name="",Scheme="",Meter="";public bool Spoken;}
    Verse[] verses=Array.Empty<Verse>();int[] verseOfLine=Array.Empty<int>();

    public void Load(PreparedPatternSong data)
    {
        source=data;lyrics=data?.Lyrics;Sung=null;BoardLine=-1;verses=Array.Empty<Verse>();
        if(lyrics==null||lyrics.Syllables==null||lyrics.Syllables.Length==0){vocalNotes=Array.Empty<MidiCycleAnalysis.Hit>();sung=Array.Empty<PreparedPatternSong.Syllable>();sungStart=Array.Empty<double>();return;}
        vocalNotes=lyrics.Track<0?Array.Empty<MidiCycleAnalysis.Hit>():(data.Notes??Array.Empty<MidiCycleAnalysis.Hit>()).Where(n=>n.Track==lyrics.Track&&n.Channel==lyrics.Channel).OrderBy(n=>n.Beat).ToArray();
        if(vocalNotes.Length>0){low=vocalNotes.Min(n=>n.Pitch)-2;high=Math.Max(low+9,vocalNotes.Max(n=>n.Pitch)+2);}
        sung=lyrics.Syllables.Where(s=>!s.Spoken).OrderBy(s=>s.Start).ToArray();sungStart=sung.Select(s=>s.Start).ToArray();
        // Which sung syllable each vocal note carries (for its vibrato).
        noteOwner=new int[vocalNotes.Length];
        for(int i=0,k=-1;i<vocalNotes.Length;i++)
        {
            while(k+1<sung.Length&&sung[k+1].Start<=vocalNotes[i].Beat+1e-6)k++;
            noteOwner[i]=k>=0&&vocalNotes[i].Beat<sung[k].End?k:-1;
        }
        // Words per line, lines per stanza.
        verseOfLine=new int[lyrics.Lines.Length];
        var list=new List<Verse>();
        for(int s=0;s<lyrics.Stanzas.Length;s++)
        {
            var st=lyrics.Stanzas[s];var rows=new List<Row>();
            for(int li=st.FirstLine;li<st.FirstLine+st.Lines;li++)
            {
                verseOfLine[li]=list.Count;var line=lyrics.Lines[li];if(line.Count==0)continue;
                var words=new List<Word>();
                for(int i=line.First;i<line.First+line.Count;i++)
                {
                    var x=lyrics.Syllables[i];
                    if(x.WordStart||words.Count==0)words.Add(new Word{Text=x.Word,Start=x.Start,First=i});
                    var w=words[^1];w.End=x.End;w.Last=i;w.Stressed|=x.Stress>0;
                }
                var first=lyrics.Syllables.Skip(line.First).Take(line.Count).FirstOrDefault(x=>x.Metric>=1)??lyrics.Syllables[line.First];
                var row=new Row{Line=li,Words=words.ToArray(),Anchor=first.Start-first.Position,Letter=line.Letter,Rhymes=line.RhymeGroup>=0};
                row.Span=line.End-row.Anchor;
                foreach(var w in row.Words)
                {
                    var head=lyrics.Syllables[w.First];
                    w.Height=st.Spoken?(w.Stressed?(head.Metric>=1?1:.75f):head.Metric>=1?.45f:.2f):Mathf.InverseLerp(low,high,head.Pitch>=0?head.Pitch:low);
                }
                rows.Add(row);
            }
            if(rows.Count==0)continue;
            list.Add(new Verse{Stanza=s,Rows=rows.ToArray(),Width=rows.Max(r=>r.Span),Lead=rows.Max(r=>Math.Max(0,r.Anchor-lyrics.Lines[r.Line].Start)),Name=st.Name,Scheme=st.Scheme,Meter=st.Meter,Spoken=st.Spoken});
        }
        verses=list.ToArray();
    }
    public bool HasLyrics=>lyrics?.Syllables!=null&&lyrics.Syllables.Length>0;
    static Color Alpha(Color c,float a)=>new(c.r,c.g,c.b,c.a*a);
    static void Text(MeshGenerationContext ctx,string text,Vector2 center,float size,Color color)=>ctx.DrawText(text,center-new Vector2(text.Length*size*.29f,size*.62f),size,color);
    static float Glyph(float size)=>size*.56f;

    // ---------- the vocal wheel ----------
    readonly List<Vector2> curve=new(256);readonly List<double> curveBeat=new(256);readonly List<bool> curveBreak=new(256);
    public void DrawVocal(MeshGenerationContext ctx,Painter2D p,OrreryBloom bloom,Rect area,double beat)
    {
        using var perf=Perf.Vocal.Auto();
        var part=changers.VocalPart;
        float radius=Mathf.Min(area.width,area.height)*.44f;
        var d=new InstrumentChangers.Disc{Center=area.center+new Vector2(0,area.height*.03f),Radius=radius,Tilt=Tilt};
        InstrumentChangers.Plate(p,d,Mathf.Lerp(8,0,(Tilt-.42f)/.58f),FormHatch.Shadow,new Color(.1f,.15f,.15f,.95f),FormHatch.Metal);
        InstrumentChangers.Comb(p,d,FormHatch.Label(.9f));
        Sung=null;SyllablesShown=0;
        int pi=part==null?-1:InstrumentChangers.PlayAt(part,beat);var play=pi>=0?part.Plays[pi]:null;
        string title=part==null?"No vocal lane":part.Name;
        if(play==null||play.Pattern<0)
        {
            float fit=Mathf.Clamp(radius/140,.6f,1.2f);
            bool spoken=HasLyrics&&NextSpoken(beat);
            Text(ctx,title,d.Center+new Vector2(0,-14*fit),18*fit,FormHatch.Label(.8f));
            Text(ctx,spoken?"spoken: see the drum rack":"the vocal rests",d.Center+new Vector2(0,12*fit),13*fit,FormHatch.Label(.55f));
            return;
        }
        var pattern=part.Patterns[play.Pattern];double loop=Math.Max(.25,pattern.LoopBeats);
        double phaseNow=InstrumentChangers.LoopPhase(part,play,beat),spin=-phaseNow;
        float outer=radius-3,inner=outer-Mathf.Clamp(radius*.06f,5,14);
        foreach(var chord in pattern.Loop)
        {
            var shifted=new SongFormAnalysis.ChordStep{Root=chord.Rest?-1:HarmonyModel.Mod(chord.Root+play.Transpose),Quality=chord.Quality};
            double a=chord.Start/loop,b=chord.End/loop;bool here=phaseNow>=a&&phaseNow<b;
            InstrumentChangers.Sector(p,d,inner,outer,spin+a,spin+b,Alpha(CyclicOrrery.ChordColor(shifted,main.currentKey),here?1:.6f));
            if(radius>120&&(b-a)*radius*6>30)Text(ctx,string.IsNullOrEmpty(chord.Roman)?shifted.Name():chord.Roman,d.At(spin+(a+b)/2,inner-12),12,FormHatch.Label(here?.95f:.45f));
        }
        for(int i=0;i+1<play.Changed.Length;i+=2)InstrumentChangers.Arc(p,d,inner-2,spin+play.Changed[i]/loop,spin+play.Changed[i+1]/loop,FormHatch.Label(.9f),2);
        float hub=radius*.3f,span=inner-radius*.12f-hub;
        float R(double pitch)=>hub+span*Mathf.InverseLerp(low,high,(float)pitch);
        // The melody around the comb: the line just sung and the next to come, never far enough
        // round to crowd the rim.
        double from=Math.Max(play.Start,beat-Math.Min(6,loop*.22)),to=Math.Min(play.End,beat+Math.Min(14,loop*.3));
        double Phase(double b)=>(play.Offset+b-play.Start)/loop;
        curve.Clear();curveBeat.Clear();curveBreak.Clear();
        int firstNote=LowerBound(vocalNotes,from-8);
        for(int i=firstNote;i<vocalNotes.Length&&vocalNotes[i].Beat<to;i++)
        {
            var n=vocalNotes[i];double end=n.Beat+n.Length;if(end<from)continue;
            var next=i+1<vocalNotes.Length?vocalNotes[i+1]:null;var owner=noteOwner[i]>=0?sung[noteOwner[i]]:null;
            bool legato=next!=null&&next.Beat-end<.3;double glide=legato?Math.Min(.45,n.Length*.4):0;
            if(curve.Count>0&&n.Beat-curveBeat[^1]>.3){curve.Add(Vector2.zero);curveBeat.Add(n.Beat);curveBreak.Add(true);}
            double step=Math.Max(1/12.0,n.Length/12);
            for(double b=Math.Max(n.Beat,from);b<=Math.Min(to,end-glide)+1e-6;b+=step)
            {
                double pitch=n.Pitch;
                if(owner!=null&&owner.Vibrato>0&&b>=owner.VibratoStart)pitch+=owner.Vibrato*Math.Sin((b-owner.VibratoStart)*10);
                curve.Add(d.At(spin+Phase(b),R(pitch)));curveBeat.Add(b);curveBreak.Add(false);
            }
            if(legato)for(int k=1;k<=6;k++)
            {
                double b=end-glide+(next.Beat-(end-glide))*k/6;if(b<from||b>to)continue;
                float t=k/6f,s=t*t*(3-2*t);curve.Add(d.At(spin+Phase(b),R(Mathf.Lerp(n.Pitch,next.Pitch,s))));curveBeat.Add(b);curveBreak.Add(false);
            }
        }
        // Sung stretch bright, the rest fainter.
        p.lineJoin=LineJoin.Round;p.lineCap=LineCap.Round;
        for(int pass=0;pass<2;pass++)
        {
            bool done=pass==1;p.strokeColor=done?FormHatch.Label(.8f):FormHatch.Ink(.4f);p.lineWidth=done?2.4f:1.6f;p.BeginPath();bool open=false;
            for(int i=0;i<curve.Count;i++)
            {
                if(curveBreak[i]||(curveBeat[i]<=beat)!=done){open=false;continue;}
                if(!open){p.MoveTo(curve[i]);open=true;}else p.LineTo(curve[i]);
            }
            p.Stroke();
        }
        // Syllables at their notes on the curve, upright, lifted off the line.
        int firstSung=LowerBound(sungStart,from),lastSung=LowerBound(sungStart,to);
        int c=0;float time=Time.unscaledTime;
        for(int k=firstSung;k<lastSung;k++)
        {
            var s=sung[k];
            while(c+1<curveBeat.Count&&curveBeat[c+1]<=s.Start+1e-6)c++;
            if(c>=curve.Count||curveBreak[c])continue;
            bool now=s.Start<=beat&&beat<s.End;if(now)Sung=s;
            float fade=Mathf.Clamp01(1.25f-(float)Math.Abs(beat-s.Start)/11f);if(fade<.05f)continue;
            float size=Mathf.Clamp(radius*(now?.075f:.055f),12,30);
            var at=curve[c];var outward=(at-d.Center).normalized;var middle=at+outward*(size*.9f+4);
            var color=now?Color.white:s.End<=beat?FormHatch.Label(.5f*fade):FormHatch.Label(.9f*fade);
            if(now&&s.Vibrato>0&&beat>=s.VibratoStart)
            {
                // Vibrato: each letter bobs at the vibrato's rate, the letters staying upright.
                float amplitude=Mathf.Clamp(s.Vibrato/.35f*3.5f,1.5f,6),rate=s.VibratoRate>0?s.VibratoRate:5.5f,glyph=Glyph(size);
                float left=middle.x-s.Text.Length*glyph*.5f;
                for(int i=0;i<s.Text.Length;i++)ctx.DrawText(s.Text[i].ToString(),new Vector2(left+i*glyph,middle.y-size*.62f+amplitude*Mathf.Sin(time*Mathf.PI*2*rate-i*1.1f)),size,color);
            }
            else Text(ctx,s.Text,middle,size,color);
            SyllablesShown++;
            if(now)
            {
                float age=(float)(midi.Cycles.SecondsAt(beat)-midi.Cycles.SecondsAt(s.Start));
                float glow=(.06f+.16f*s.Emphasis*Mathf.Exp(-age*2))*(midi.IsPlaying?1:.6f);
                p.fillColor=Color.white;p.BeginPath();p.Arc(at,3.5f,Angle.Degrees(0),Angle.Degrees(360));p.Fill();
                bloom.Disk(at,2.5f+2.5f*s.Emphasis,Color.white,glow);
                bloom.Line(middle+new Vector2(-s.Text.Length*Glyph(size)*.5f,size*.7f),middle+new Vector2(s.Text.Length*Glyph(size)*.5f,size*.7f),2,Color.white,glow);
            }
        }
        // Hub: the vocal pattern and its repeat count; the title above.
        string token=InstrumentChangers.Token(part,play);
        Text(ctx,token,d.Center+new Vector2(0,-10),Mathf.Clamp(radius*.1f,14,26),Color.white);
        if(play.Run>1)Text(ctx,$"{play.Repeat}/{play.Run}",d.Center+new Vector2(0,14),12,FormHatch.Label(.7f));
        ctx.DrawText($"{title}  ·  {pattern.LoopBars}-bar pattern {token}",new Vector2(area.x+4,area.y+2),14,FormHatch.Label(.85f));
    }
    bool NextSpoken(double beat)
    {
        foreach(var s in lyrics.Syllables)if(s.End>beat)return s.Spoken;
        return false;
    }
    static int LowerBound(double[] starts,double beat){int lo=0,hi=starts.Length;while(lo<hi){int mid=(lo+hi)/2;if(starts[mid]<beat)lo=mid+1;else hi=mid;}return lo;}
    static int LowerBound(MidiCycleAnalysis.Hit[] notes,double beat){int lo=0,hi=notes.Length;while(lo<hi){int mid=(lo+hi)/2;if(notes[mid].Beat<beat)lo=mid+1;else hi=mid;}return lo;}

    // ---------- the word graph ----------
    public void DrawGraph(MeshGenerationContext ctx,Painter2D p,OrreryBloom bloom,Rect area,double beat)
    {
        using var perf=Perf.Board.Auto();
        BoardRows=0;BoardLine=-1;
        if(verses.Length==0)return;
        // The line being heard, or the next to come.
        int current=-1;
        for(int i=0;i<lyrics.Lines.Length;i++){var l=lyrics.Lines[i];if(l.Count==0)continue;if(l.Start<=beat&&beat<l.End+.5||l.Start>beat){current=i;break;}}
        if(current<0)current=lyrics.Lines.Length-1;
        BoardLine=current;
        int vi=Mathf.Clamp(verseOfLine[current],0,verses.Length-1);var verse=verses[vi];
        // Stanza tabs: the song, section by section, the one being heard lit.
        float tabHeight=26,tabWidth=Mathf.Min(150,area.width/verses.Length);
        for(int k=0;k<verses.Length;k++)
        {
            var v=verses[k];bool on=k==vi;var tab=new Rect(area.x+k*tabWidth+2,area.y,tabWidth-4,tabHeight);
            p.fillColor=on?FormHatch.Ink(.22f):FormHatch.Ink(.06f);p.BeginPath();p.MoveTo(new Vector2(tab.xMin,tab.yMin));p.LineTo(new Vector2(tab.xMax,tab.yMin));p.LineTo(new Vector2(tab.xMax,tab.yMax));p.LineTo(new Vector2(tab.xMin,tab.yMax));p.ClosePath();p.Fill();
            if(on){p.strokeColor=FormHatch.Label(.9f);p.lineWidth=2;p.BeginPath();p.MoveTo(new Vector2(tab.xMin,tab.yMax));p.LineTo(new Vector2(tab.xMax,tab.yMax));p.Stroke();}
            ctx.DrawText(v.Name,new Vector2(tab.xMin+8,tab.yMin+5),13,on?Color.white:FormHatch.Label(k<vi?.45f:.65f));
        }
        ctx.DrawText($"{verse.Scheme}   ·   {verse.Meter}{(verse.Spoken?"   ·   spoken on the drum rack":"")}",new Vector2(area.x+4,area.y+tabHeight+8),12,FormHatch.Label(.6f));
        // One graph per line: words on the line's beat grid, raised by pitch or stress.
        float top=area.y+tabHeight+32,left=area.x+8,right=area.xMax-40;
        float row=Mathf.Min(78,(area.yMax-top)/Math.Max(1,verse.Rows.Length)),lift=Mathf.Min(26,row*.4f);
        float size=Mathf.Clamp(row*.26f,11,18);
        float perBeat=(float)((right-left)/Math.Max(1,verse.Width+verse.Lead));
        foreach(var r in verse.Rows)
        {
            float y0=top+BoardRows*row;BoardRows++;
            bool here=r.Line==current;float baseline=y0+row*.62f;
            float X(double b)=>left+(float)((b-r.Anchor+verse.Lead)*perBeat);
            float Y(Word w)=>baseline-w.Height*lift;
            if(here){p.fillColor=FormHatch.Ink(.07f);p.BeginPath();p.MoveTo(new Vector2(area.x,y0));p.LineTo(new Vector2(area.xMax,y0));p.LineTo(new Vector2(area.xMax,y0+row-4));p.LineTo(new Vector2(area.x,y0+row-4));p.ClosePath();p.Fill();}
            // The line's path through its words.
            p.lineWidth=1.4f;p.strokeColor=FormHatch.Ink(here?.55f:.25f);p.BeginPath();
            for(int k=0;k<r.Words.Length;k++){var at=new Vector2(X(r.Words[k].Start),Y(r.Words[k]));if(k==0)p.MoveTo(at);else p.LineTo(at);}
            p.Stroke();
            foreach(var w in r.Words)
            {
                var at=new Vector2(X(w.Start),Y(w));bool now=here&&w.Start<=beat&&beat<Math.Max(w.End,w.Start+.25);bool past=w.End<=beat;
                p.fillColor=now?Color.white:w.Stressed?FormHatch.Label(past?.5f:.9f):FormHatch.Ink(past?.35f:.6f);
                p.BeginPath();p.Arc(at,now?4.5f:w.Stressed?3f:2f,Angle.Degrees(0),Angle.Degrees(360));p.Fill();
                var color=now?Color.white:past?FormHatch.Label(here?.5f:.4f):FormHatch.Label(here?.95f:.7f);
                ctx.DrawText(w.Text,new Vector2(at.x-2,at.y-size*1.45f),now?size*1.12f:size,color);
                if(now)bloom.Disk(at,5,Color.white,.25f*(midi.IsPlaying?1:.6f));
            }
            // The rhyme letter at the line's end.
            var end=new Vector2(right+22,baseline-lift*.5f);
            p.strokeColor=FormHatch.Ink(r.Rhymes?.8f:.25f);p.lineWidth=1.2f;p.BeginPath();p.Arc(end,9,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();
            Text(ctx,r.Letter,end,12,FormHatch.Label(r.Rhymes?1:.45f));
        }
    }
}
