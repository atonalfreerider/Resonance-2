using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

// Lyric mode, in the pattern-wheel panel (the spoken lines ride the drum wheel: DrumLyricRack).
//  Vocal wheel — the vocal changer's top disc turned to face the viewer. Its melody is a curve
//                through the notes (radius is pitch; the disc turns once per loop under the comb
//                at twelve o'clock, so the line being sung arrives at the top). Each held note is
//                an arc at its pitch and the voice bends to the next note along a bezier. Sung
//                syllables ride the curve from their notes, letter by letter, turned with it.
//                The syllable being sung is lit and blooms with its emphasis; under vibrato its
//                letters and its stretch of curve shiver at the vibrato's rate and depth.
//  Rhyme board — the stanza being heard, one row per line on its beat grid: a bar over each
//                stressed syllable and a cup over each unstressed one, a dot under each
//                syllable that lands on a beat; end-rhyme letters joined by a bracket on the
//                right, front rhymes (repeated openings, head rhyme, alliteration) on the left;
//                the meter under each line. Where another stanza sings the same pattern, its
//                matching line is ghosted beneath on the same beats, with how well the two meet.
public sealed class LyricWheel
{
    readonly Main main;readonly MidiPlayer midi;readonly InstrumentChangers changers;readonly VisualElement host;
    PreparedPatternSong source;MidiCycleAnalysis.Hit[] vocalNotes=Array.Empty<MidiCycleAnalysis.Hit>();int low=55,high=76;
    public float Tilt=.42f;
    Rect wheelArea;bool wheelShown;
    readonly List<Label> letters=new();readonly Dictionary<(char,int),float> widths=new();
    // For validation: what is lit now.
    public PreparedPatternSong.Syllable Sung {get;private set;}
    public int LettersShown {get;private set;}
    public int BoardLine {get;private set;}=-1;
    public int BoardRows {get;private set;}
    public LyricWheel(Main main,MidiPlayer midi,InstrumentChangers changers,VisualElement host){this.main=main;this.midi=midi;this.changers=changers;this.host=host;}
    public void Load(PreparedPatternSong data)
    {
        source=data;Sung=null;BoardLine=-1;
        var lyrics=data?.Lyrics;
        vocalNotes=lyrics==null||lyrics.Track<0?Array.Empty<MidiCycleAnalysis.Hit>():(data.Notes??Array.Empty<MidiCycleAnalysis.Hit>()).Where(n=>n.Track==lyrics.Track&&n.Channel==lyrics.Channel).OrderBy(n=>n.Beat).ToArray();
        if(vocalNotes.Length>0){low=vocalNotes.Min(n=>n.Pitch)-2;high=Math.Max(low+9,vocalNotes.Max(n=>n.Pitch)+2);}
    }
    public bool HasLyrics=>source?.Lyrics?.Syllables!=null&&source.Lyrics.Syllables.Length>0;
    static Color Alpha(Color c,float a)=>new(c.r,c.g,c.b,c.a*a);
    static void Text(MeshGenerationContext ctx,string text,Vector2 center,float size,Color color)=>ctx.DrawText(text,center-new Vector2(text.Length*size*.29f,size*.62f),size,color);
    static float Glyph(float size)=>size*.56f;

    // ---------- the vocal wheel's geometry, shared by the drawing and the letters ----------
    struct Point {public Vector2 At,Normal;public double Beat;public bool Break;}
    sealed class Layout
    {
        public InstrumentChangers.Disc Disc;public PreparedPatternSong.InstrumentPart Part;public PreparedPatternSong.LanePlay Play;
        public double Loop,Spin,From,To;public float Inner,Outer;public readonly List<Point> Curve=new();public List<PreparedPatternSong.Syllable> Syllables=new();
    }
    float Radius(Layout l,double pitch){float hub=l.Disc.Radius*.3f,span=l.Inner-l.Disc.Radius*.12f-hub;return hub+span*Mathf.InverseLerp(low,high,(float)pitch);}
    Layout Build(Rect area,double beat)
    {
        var l=new Layout();float radius=Mathf.Min(area.width,area.height)*.44f;
        l.Disc=new InstrumentChangers.Disc{Center=area.center+new Vector2(0,area.height*.03f),Radius=radius,Tilt=Tilt};
        l.Outer=radius-3;l.Inner=l.Outer-Mathf.Clamp(radius*.06f,5,14);
        l.Part=changers.VocalPart;int pi=l.Part==null?-1:InstrumentChangers.PlayAt(l.Part,beat);l.Play=pi>=0?l.Part.Plays[pi]:null;
        if(l.Play==null||l.Play.Pattern<0)return l;
        var play=l.Play;l.Loop=Math.Max(.25,l.Part.Patterns[play.Pattern].LoopBeats);
        l.Spin=-InstrumentChangers.LoopPhase(l.Part,play,beat);
        // Only the stretch around the comb: the line just sung and the next to come, never
        // far enough round for the letters to turn over.
        l.From=Math.Max(play.Start,beat-Math.Min(6,l.Loop*.22));l.To=Math.Min(play.End,beat+Math.Min(14,l.Loop*.3));
        var lyrics=source.Lyrics;
        l.Syllables=lyrics.Syllables.Where(s=>!s.Spoken&&s.Start>=l.From-1e-6&&s.Start<l.To).ToList();
        double Phase(double b)=>(play.Offset+b-play.Start)/l.Loop;
        int first=Array.FindIndex(vocalNotes,n=>n.Beat+n.Length>l.From);if(first<0)return l;
        for(int i=first;i<vocalNotes.Length&&vocalNotes[i].Beat<l.To;i++)
        {
            var n=vocalNotes[i];var next=i+1<vocalNotes.Length?vocalNotes[i+1]:null;
            var owner=lyrics.Syllables.LastOrDefault(s=>!s.Spoken&&s.Start<=n.Beat+1e-6&&n.Beat<s.End);
            double end=n.Beat+n.Length;bool legato=next!=null&&next.Beat-end<.3;
            double glide=legato?Math.Min(.45,n.Length*.4):0;
            if(l.Curve.Count>0&&n.Beat-l.Curve[^1].Beat>.3)l.Curve.Add(new Point{Break=true,Beat=n.Beat});
            for(double b=Math.Max(n.Beat,l.From);b<=Math.Min(l.To,end-glide)+1e-6;b+=1/24.0)
            {
                double pitch=n.Pitch;
                if(owner!=null&&owner.Vibrato>0&&b>=owner.VibratoStart)pitch+=owner.Vibrato*Math.Sin((b-owner.VibratoStart)*10);
                l.Curve.Add(new Point{At=l.Disc.At(l.Spin+Phase(b),Radius(l,pitch)),Beat=b});
            }
            // The bend to the next note: a cubic ease from this pitch into the next.
            if(legato)for(int k=1;k<=8;k++)
            {
                double b=end-glide+(next.Beat-(end-glide))*k/8;if(b<l.From||b>l.To)continue;
                float t=k/8f,s=t*t*(3-2*t);
                l.Curve.Add(new Point{At=l.Disc.At(l.Spin+Phase(b),Radius(l,Mathf.Lerp(n.Pitch,next.Pitch,s))),Beat=b});
            }
        }
        // Normals point away from the centre, so letters sit above the line.
        for(int i=0;i<l.Curve.Count;i++){var q=l.Curve[i];if(q.Break)continue;q.Normal=(q.At-l.Disc.Center).normalized;l.Curve[i]=q;}
        return l;
    }

    public void DrawVocal(MeshGenerationContext ctx,Painter2D p,OrreryBloom bloom,Rect area,double beat)
    {
        wheelArea=area;wheelShown=true;
        var l=Build(area,beat);var d=l.Disc;var lyrics=source?.Lyrics;
        InstrumentChangers.Plate(p,d,Mathf.Lerp(8,0,(Tilt-.42f)/.58f),FormHatch.Shadow,new Color(.1f,.15f,.15f,.95f),FormHatch.Metal);
        InstrumentChangers.Comb(p,d,FormHatch.Label(.9f));
        string title=l.Part==null?"No vocal lane":l.Part.Name;
        if(l.Play==null||l.Play.Pattern<0)
        {
            bool spoken=lyrics!=null&&NextSpoken(lyrics,beat);
            float fit=Mathf.Clamp(d.Radius/140,.6f,1.2f);
            Text(ctx,title,d.Center+new Vector2(0,-14*fit),18*fit,FormHatch.Label(.8f));
            Text(ctx,spoken?"spoken: see the drum rack":"the vocal rests",d.Center+new Vector2(0,12*fit),13*fit,FormHatch.Label(.55f));
            return;
        }
        var play=l.Play;var pattern=l.Part.Patterns[play.Pattern];double phaseNow=-l.Spin;
        foreach(var chord in pattern.Loop)
        {
            var shifted=new SongFormAnalysis.ChordStep{Root=chord.Rest?-1:HarmonyModel.Mod(chord.Root+play.Transpose),Quality=chord.Quality};
            double a=chord.Start/l.Loop,b=chord.End/l.Loop;bool here=phaseNow>=a&&phaseNow<b;
            InstrumentChangers.Sector(p,d,l.Inner,l.Outer,l.Spin+a,l.Spin+b,Alpha(CyclicOrrery.ChordColor(shifted,main.currentKey),here?1:.6f));
            if(d.Radius>120&&(b-a)*d.Radius*6>30)Text(ctx,string.IsNullOrEmpty(chord.Roman)?shifted.Name():chord.Roman,d.At(l.Spin+(a+b)/2,l.Inner-12),12,FormHatch.Label(here?.95f:.45f));
        }
        for(int i=0;i+1<play.Changed.Length;i+=2)InstrumentChangers.Arc(p,d,l.Inner-2,l.Spin+play.Changed[i]/l.Loop,l.Spin+play.Changed[i+1]/l.Loop,FormHatch.Label(.9f),2);
        // Faint pitch rings at each C, for a sense of height.
        for(int c=(low/12+1)*12;c<high;c+=12){InstrumentChangers.Arc(p,d,Radius(l,c),0,1,FormHatch.Ink(.1f),1);Text(ctx,"C"+(c/12-1),d.At(.5,Radius(l,c))+new Vector2(12,0),10,FormHatch.Label(.3f));}
        // The melody: sung stretch bright, the rest to come fainter, fading toward the ends.
        void Stroke(bool sung,float width)
        {
            p.lineWidth=width;p.lineJoin=LineJoin.Round;p.lineCap=LineCap.Round;
            for(int i=1;i<l.Curve.Count;i++)
            {
                var a=l.Curve[i-1];var b=l.Curve[i];if(a.Break||b.Break||(b.Beat<=beat)!=sung)continue;
                float fade=Mathf.Clamp01(1.3f-(float)Math.Abs(b.Beat-beat)/10f);
                p.strokeColor=sung?FormHatch.Label(.8f*fade):FormHatch.Ink(.45f*fade);
                p.BeginPath();p.MoveTo(a.At);p.LineTo(b.At);p.Stroke();
            }
        }
        Stroke(false,1.6f);Stroke(true,2.4f);
        // Where the voice is now: a bead on the curve at the comb, glowing with the syllable's emphasis.
        Sung=lyrics.Syllables.FirstOrDefault(s=>!s.Spoken&&s.Start<=beat&&beat<s.End);
        var bead=l.Curve.LastOrDefault(q=>!q.Break&&q.Beat<=beat);
        if(Sung!=null&&bead.Beat>0)
        {
            float emphasis=Sung.Emphasis,age=(float)(midi.Cycles.SecondsAt(beat)-midi.Cycles.SecondsAt(Sung.Start));
            p.fillColor=Color.white;p.BeginPath();p.Arc(bead.At,3.5f,Angle.Degrees(0),Angle.Degrees(360));p.Fill();
            bloom.Disk(bead.At,2.5f+2.5f*emphasis,Color.white,(.06f+.16f*emphasis*Mathf.Exp(-age*2))*(midi.IsPlaying?1:.6f));
        }
        // The syllable being sung: a glowing stroke along its stretch of curve, brighter with emphasis.
        if(Sung!=null)
        {
            var stretch=l.Curve.Where(q=>!q.Break&&q.Beat>=Sung.Start&&q.Beat<=Math.Min(beat,Sung.End)).ToList();
            float power=(.05f+.13f*Sung.Emphasis)*(midi.IsPlaying?1:.6f);
            for(int i=1;i<stretch.Count;i++)bloom.Line(stretch[i-1].At,stretch[i].At,2.5f,Color.white,power);
        }
        // Hub: the vocal pattern and its repeat count; the title above.
        string token=InstrumentChangers.Token(l.Part,play);
        Text(ctx,token,d.Center+new Vector2(0,-10),Mathf.Clamp(d.Radius*.1f,14,26),Color.white);
        if(play.Run>1)Text(ctx,$"{play.Repeat}/{play.Run}",d.Center+new Vector2(0,14),12,FormHatch.Label(.7f));
        ctx.DrawText($"{title}  ·  {pattern.LoopBars}-bar pattern {token}",new Vector2(area.x+4,area.y+2),14,FormHatch.Label(.85f));
        var line=Sung!=null?lyrics.Lines[Sung.Line]:null;
        if(line!=null)ctx.DrawText($"{lyrics.Stanzas[line.Stanza].Name}  ·  {line.Meter}",new Vector2(area.x+4,area.y+22),12,FormHatch.Label(.55f));
    }
    public void HideVocal()=>wheelShown=false;
    static bool NextSpoken(PreparedPatternSong.LyricSheet lyrics,double beat)=>lyrics.Syllables.Where(s=>s.End>beat).OrderBy(s=>s.Start).FirstOrDefault()?.Spoken??false;

    // ---------- letters: labels placed along the curve (outside the draw pass) ----------
    float Width(char c,int size)
    {
        if(widths.TryGetValue((c,size),out float w))return w;
        var probe=letters.Count>0?letters[0]:null;
        w=probe!=null?probe.MeasureTextSize(c.ToString(),0,VisualElement.MeasureMode.Undefined,0,VisualElement.MeasureMode.Undefined).x:size*.56f;
        if(w<=0||float.IsNaN(w))w=size*(c==' '?.3f:.56f);
        widths[(c,size)]=w;return w;
    }
    Label Letter(int index)
    {
        while(letters.Count<=index)
        {
            var label=new Label{pickingMode=PickingMode.Ignore};label.style.position=Position.Absolute;label.style.unityTextAlign=TextAnchor.MiddleCenter;
            label.style.marginLeft=label.style.marginRight=label.style.marginTop=label.style.marginBottom=0;label.style.paddingLeft=label.style.paddingRight=label.style.paddingTop=label.style.paddingBottom=0;
            host.Add(label);letters.Add(label);
        }
        return letters[index];
    }
    public void PlaceLetters(double beat)
    {
        int used=0;
        if(wheelShown&&HasLyrics&&wheelArea.width>10)
        {
            var l=Build(wheelArea,beat);float time=Time.unscaledTime;
            if(letters.Count==0)Letter(0);
            foreach(var s in l.Syllables)
            {
                int i=l.Curve.FindIndex(q=>!q.Break&&q.Beat>=s.Start-1e-6);if(i<0)continue;
                bool now=s.Start<=beat&&beat<s.End;bool past=s.End<=beat;
                float fade=Mathf.Clamp01(1.25f-(float)Math.Abs(beat-s.Start)/11f);if(fade<.05f)continue;
                double age=midi.Cycles.SecondsAt(beat)-midi.Cycles.SecondsAt(s.Start);
                float pop=now?1+.25f*Mathf.Exp(-(float)age*6)*(.5f+s.Emphasis):1;
                int size=Mathf.RoundToInt(Mathf.Clamp(l.Disc.Radius*(now?.075f:.058f),12,30)*pop);
                float shiver=now&&s.Vibrato>0&&beat>=s.VibratoStart?Mathf.Clamp(s.Vibrato/.35f*4,1.5f,7):0;
                float rate=s.VibratoRate>0?s.VibratoRate:5.5f;
                var color=now?Color.white:past?FormHatch.Label(.5f*fade):FormHatch.Label(.9f*fade);
                // Walk the curve from the syllable's note; each letter centred on its stretch.
                float walked=0,target=Width(s.Text[0],size)*.5f;int k=0;var previous=l.Curve[i].At;
                for(int j=i;j<l.Curve.Count&&k<s.Text.Length;j++)
                {
                    var q=l.Curve[j];if(q.Break)break;
                    float step=Vector2.Distance(previous,q.At);
                    while(k<s.Text.Length&&walked+step>=target)
                    {
                        float t=step>0?(target-walked)/step:0;var at=Vector2.Lerp(previous,q.At,t);
                        var tangent=(q.At-previous).sqrMagnitude>1e-6f?(q.At-previous).normalized:new Vector2(-q.Normal.y,q.Normal.x);
                        var normal=new Vector2(tangent.y,-tangent.x);if(Vector2.Dot(normal,q.Normal)<0)normal=-normal;
                        at+=normal*(size*.6f+4);
                        if(shiver>0)at+=normal*shiver*Mathf.Sin(time*Mathf.PI*2*rate-k*1.1f);
                        var label=Letter(used++);char c=s.Text[k];float w=Width(c,size);
                        if(label.text!=c.ToString())label.text=c.ToString();
                        label.style.fontSize=size;label.style.color=color;label.style.width=w+2;label.style.height=size*1.3f;
                        label.style.left=at.x-(w+2)*.5f;label.style.top=at.y-size*.65f;
                        label.style.rotate=new Rotate(Angle.Radians(Mathf.Atan2(tangent.y,tangent.x)));
                        label.style.unityFontStyleAndWeight=now||s.Stress>0?FontStyle.Bold:FontStyle.Normal;
                        label.style.display=DisplayStyle.Flex;
                        k++;if(k<s.Text.Length)target+=(w+Width(s.Text[k],size))*.5f;
                    }
                    walked+=step;previous=q.At;
                }
            }
        }
        LettersShown=used;
        for(int i=used;i<letters.Count;i++)if(letters[i].style.display!=DisplayStyle.None)letters[i].style.display=DisplayStyle.None;
    }

    // ---------- the rhyme board ----------
    public void DrawRhymes(MeshGenerationContext ctx,Painter2D p,OrreryBloom bloom,Rect area,double beat)
    {
        var lyrics=source?.Lyrics;BoardRows=0;BoardLine=-1;
        if(lyrics==null||lyrics.Lines.Length==0)return;
        // The line being heard, or the next one to come.
        int current=Array.FindIndex(lyrics.Lines,l=>l.Count>0&&l.Start<=beat&&beat<l.End+.5);
        if(current<0)current=Array.FindIndex(lyrics.Lines,l=>l.Count>0&&l.Start>beat);
        if(current<0)current=lyrics.Lines.Length-1;
        BoardLine=current;
        var stanza=lyrics.Stanzas[lyrics.Lines[current].Stanza];
        var rows=Enumerable.Range(stanza.FirstLine,stanza.Lines).ToList();
        var ghost=rows.ToDictionary(i=>i,i=>lyrics.Matches.FirstOrDefault(m=>m.B==i)??lyrics.Matches.FirstOrDefault(m=>m.A==i));
        bool ghosts=ghost.Values.Any(m=>m!=null);
        // Scale: a line row, its ghost and its meter must fit.
        float block=(area.height-40)/Math.Max(1,rows.Count),unit=Mathf.Clamp((block-12)/(ghosts?88:58),.6f,1.35f);
        float size=18*unit,small=13.5f*unit,tiny=11.5f*unit;
        ctx.DrawText($"{stanza.Name.ToUpperInvariant()}",new Vector2(area.x+4,area.y),16*unit,FormHatch.Label(.95f));
        ctx.DrawText($"rhyme {stanza.Scheme}   ·   {stanza.Meter}{(stanza.Spoken?"   ·   spoken on the drum rack":"")}",new Vector2(area.x+4+(stanza.Name.Length+3)*16*unit*.6f,area.y+3),12*unit,FormHatch.Label(.6f));
        float top=area.y+34*unit;
        float left=area.x+86*unit,right=area.xMax-70*unit;
        // A shared grid: each row starts at its first on-beat syllable's bar; the widest row sets the scale.
        double Anchor(PreparedPatternSong.LyricLine l)
        {
            var own=lyrics.Syllables.Skip(l.First).Take(l.Count);var on=own.FirstOrDefault(s=>s.Metric>=1)??own.First();
            return on.Start-on.Position;
        }
        var all=rows.Concat(ghosts?rows.Where(i=>ghost[i]!=null).Select(i=>ghost[i].A==i?ghost[i].B:ghost[i].A):Enumerable.Empty<int>()).Where(i=>lyrics.Lines[i].Count>0).ToList();
        double span=all.Max(i=>{var l=lyrics.Lines[i];return l.End-Anchor(l);});
        double lead=all.Max(i=>{var l=lyrics.Lines[i];return Math.Max(0,Anchor(l)-l.Start);});
        float perBeat=(float)((right-left)/Math.Max(1,span+lead));
        float X(PreparedPatternSong.LyricLine l,double b)=>left+(float)((b-Anchor(l)+lead)*perBeat);
        var rhymeRows=new Dictionary<int,List<float>>();var frontRows=new Dictionary<int,List<float>>();
        for(int r=0;r<rows.Count;r++)
        {
            int li=rows[r];var line=lyrics.Lines[li];float y=top+r*block;bool here=li==current;
            if(line.Count==0)continue;BoardRows++;
            float lineHeight=size*2.3f;
            if(here){p.fillColor=FormHatch.Ink(.08f);p.BeginPath();p.MoveTo(new Vector2(area.x,y-4));p.LineTo(new Vector2(area.xMax,y-4));p.LineTo(new Vector2(area.xMax,y+block-8));p.LineTo(new Vector2(area.x,y+block-8));p.ClosePath();p.Fill();}
            Row(ctx,p,bloom,lyrics,line,y,X,perBeat,here,beat,1,size);
            float mid=y+size*.9f;
            if(line.RhymeGroup>=0){if(!rhymeRows.ContainsKey(line.RhymeGroup))rhymeRows[line.RhymeGroup]=new List<float>();rhymeRows[line.RhymeGroup].Add(mid);}
            if(line.FrontGroup>=0){if(!frontRows.ContainsKey(line.FrontGroup))frontRows[line.FrontGroup]=new List<float>();frontRows[line.FrontGroup].Add(mid);}
            // End rhyme letter, circled when it rhymes, with the sound it rhymes on.
            var letterAt=new Vector2(right+30*unit,mid);
            p.strokeColor=FormHatch.Ink(line.RhymeGroup>=0?.85f:.3f);p.lineWidth=1.2f;p.BeginPath();p.Arc(letterAt,10*unit,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();
            Text(ctx,line.Letter,letterAt,13*unit,FormHatch.Label(line.RhymeGroup>=0?1:.5f));
            if(line.EndRhyme.Length>0&&line.RhymeGroup>=0)ctx.DrawText("-"+line.EndRhyme,new Vector2(right+44*unit,mid-6*unit),tiny,FormHatch.Label(.5f));
            float below=y+lineHeight;
            // A ghost of the matching line of the other stanza, on the same grid.
            var m=ghost[li];
            if(m!=null)
            {
                var other=lyrics.Lines[m.A==li?m.B:m.A];
                if(other.Count>0)Row(ctx,p,bloom,lyrics,other,below,X,perBeat,false,beat,.45f,small);
                string score=$"{lyrics.Stanzas[other.Stanza].Name} · {Mathf.RoundToInt(m.Score*100)}% · {m.Note}";
                ctx.DrawText(score,new Vector2(right-score.Length*tiny*.55f,below+small*2.2f),tiny,FormHatch.Label(m.Score>.95f?.4f:.8f));
                below+=small*2.2f;
            }
            ctx.DrawText(line.Meter,new Vector2(left,below+2),tiny,FormHatch.Label(here?.65f:.4f));
        }
        // Brackets: end rhymes on the right, front rhymes on the left.
        foreach(var ys in rhymeRows.Values.Where(v=>v.Count>1))Bracket(p,right+14*unit,ys,-6,FormHatch.Ink(.75f));
        foreach(var (group,ys) in frontRows.Select(k=>(k.Key,k.Value)).Where(k=>k.Value.Count>1))
        {
            Bracket(p,left-14*unit,ys,6,FormHatch.Ink(.75f));
            var line=lyrics.Lines.First(l=>l.FrontGroup==group);
            string label=line.FrontRhyme.Length>10?line.FrontRhyme.Substring(0,9)+"…":line.FrontRhyme;
            ctx.DrawText(label,new Vector2(area.x+2,(ys.First()+ys.Last())/2-12*unit),12*unit,FormHatch.Label(.85f));
            ctx.DrawText(line.FrontKind,new Vector2(area.x+2,(ys.First()+ys.Last())/2+2*unit),tiny,FormHatch.Label(.45f));
        }
    }
    static void Bracket(Painter2D p,float x,List<float> ys,float hook,Color color)
    {
        p.strokeColor=color;p.lineWidth=1.3f;p.BeginPath();p.MoveTo(new Vector2(x,ys.Min()));p.LineTo(new Vector2(x,ys.Max()));p.Stroke();
        foreach(float y in ys){p.BeginPath();p.MoveTo(new Vector2(x,y));p.LineTo(new Vector2(x+hook,y));p.Stroke();}
    }
    // One line on the beat grid: beat ticks, stress marks, syllables, beat dots.
    void Row(MeshGenerationContext ctx,Painter2D p,OrreryBloom bloom,PreparedPatternSong.LyricSheet lyrics,PreparedPatternSong.LyricLine line,float y,Func<PreparedPatternSong.LyricLine,double,float> X,float perBeat,bool here,double beat,float alpha,float size)
    {
        var own=lyrics.Syllables.Skip(line.First).Take(line.Count).ToList();
        double first=Math.Floor(own[0].Start+1e-6),last=own[^1].End;
        var measures=source.Measures??Array.Empty<MidiCycleAnalysis.Bar>();
        float baseline=y+size*1.9f;
        for(double b=first;b<=last+1e-6;b+=1)
        {
            bool down=measures.Any(m=>Math.Abs(m.Start-b)<1e-6);
            float x=X(line,b);p.strokeColor=FormHatch.Ink((down?.5f:.22f)*alpha);p.lineWidth=down?1.4f:1;p.BeginPath();p.MoveTo(new Vector2(x,baseline-(down?6:3)));p.LineTo(new Vector2(x,baseline+3));p.Stroke();
        }
        p.strokeColor=FormHatch.Ink(.18f*alpha);p.lineWidth=1;p.BeginPath();p.MoveTo(new Vector2(X(line,first),baseline));p.LineTo(new Vector2(X(line,last),baseline));p.Stroke();
        foreach(var s in own)
        {
            float x=X(line,s.Start);bool now=here&&s.Start<=beat&&beat<s.End;bool past=here&&s.End<=beat;
            float text=s.Text.Length*Glyph(size);
            // Stress mark above: a bar for stressed, a cup for unstressed.
            var mark=new Vector2(x+text*.5f,y);
            p.strokeColor=FormHatch.Label((s.Stress>0?.95f:.5f)*alpha);p.lineWidth=s.Stress>0?2.2f:1.2f;p.BeginPath();
            if(s.Stress>0){p.MoveTo(mark+new Vector2(-size*.35f,0));p.LineTo(mark+new Vector2(size*.35f,0));}
            else p.Arc(mark+new Vector2(0,-size*.25f),size*.25f,Angle.Degrees(25),Angle.Degrees(155));
            p.Stroke();
            // Held syllables stretch a line over their teeth.
            if(s.End-s.Start>=1.5){p.strokeColor=FormHatch.Ink(.45f*alpha);p.lineWidth=1.2f;p.BeginPath();p.MoveTo(new Vector2(x+text+2,y+size*.95f));p.LineTo(new Vector2(X(line,s.End)-3,y+size*.95f));p.Stroke();}
            var color=now?Color.white:past?FormHatch.Label(.55f*alpha):FormHatch.Label(.9f*alpha);
            ctx.DrawText(s.Text,new Vector2(x,y+size*.35f),now?size*1.12f:size,color);
            if(now)bloom.Line(new Vector2(x,y+size*1.55f),new Vector2(x+text,y+size*1.55f),2,Color.white,(.06f+.14f*s.Emphasis)*(midi.IsPlaying?1:.6f));
            // A dot on the grid under a syllable that lands on a beat (the delivered accent).
            if(s.Metric>=1){p.fillColor=FormHatch.Ink((s.Metric==2?.95f:.7f)*alpha);p.BeginPath();p.Arc(new Vector2(x+2,baseline),s.Metric==2?2.8f:2f,Angle.Degrees(0),Angle.Degrees(360));p.Fill();}
        }
    }
}
