using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

// Pattern wheels: a rack and pinion driving a planetary train, one gear per level of repetition.
// Colour means tonality only (chord bands: I blue, IV red, V green); form is told in neutral
// ink by hatch pattern (FormHatch): every section family has one texture, so a return reads
// as the same texture coming back, and material heard only once has a dashed outline.
//  Rack    — the song unrolled in time, one tooth per bar. Drag it to seek.
//  Pinion  — the same song wound once around a ring: one revolution per song. Its rim holds
//            every section visit in its family's hatch, and a bracket spans each group
//            (verse + chorus) that comes back. Rack and rim mesh at nine o'clock, where the
//            song is now: the rack is the rim unrolled.
//  Orbits  — each recurring group (verse + chorus, A + B) turns on its own orbit: a carrier
//            ring with its families fixed on it, one revolution per group cycle, so the playing
//            family passes the gate at nine o'clock (in line with the rack and the ring's now).
//            Families outside the groups ride an outer orbit. When one of them interrupts
//            (A B A B C A B C), the group orbit holds its phase, dashed, and resumes from it.
//  Planet  — the section now playing, magnified at the centre. One revolution per visit: its
//            chord band is this visit's harmony, ticked at each pass, changed bars outlined.
//  Moon    — the family's fundamental loop rolling inside the planet. It turns once per loop,
//            so its size is the repetition count (half the planet: the loop is played twice).
//            Where it touches the chord band the fundamental meets the visit; a spark marks a
//            variation.
//  Every family's fundamental rides its orbit as a small planet (chord band, hatched edge, one
//  dot per visit), so verse, chorus and bridge progressions compare at a glance.
public sealed class PatternWheelDeck : VisualElement
{
    readonly Main main;readonly MidiPlayer midi;readonly Label status;
    readonly WheelCanvas canvas;readonly VisualElement patterns,chapters;
    PreparedPatternSong source;
    public VisualElement Overlay=>canvas;
    public double RackTurns=>canvas.Turns;
    public float RackPixels=>canvas.RackScroll;
    // Form node of the family now playing (its planet) and the group it belongs to, if any.
    public int ActiveFamilyNode=>canvas.ActiveNode;
    public int ActiveGroup=>canvas.ActiveGroup;
    public Vector2 MetaCenter=>canvas.metaCenter;
    public Vector2 FeaturedCenter=>canvas.featuredCenter;
    public int LeadVocalTrack=>canvas.LeadTrack;
    public float FeaturedRadius=>canvas.FeaturedRadius;
    public static Color NoteColor(PreparedPatternSong.Section section,double onset,int key,bool lead)
    {
        if(lead)return Color.white;
        var chord=section.Chords.LastOrDefault(c=>c.Start<=onset&&onset<c.End);
        return CyclicOrrery.ChordColor(chord,key);
    }
    public static FormHatch.Pattern FamilyHatch(PreparedPatternSong.Pattern p,int index)=>FormHatch.ForRole(p.Role,index);
    public static string Numerals(PreparedPatternSong.Pattern p)
    {
        var steps=new List<string>();
        foreach(var c in p.Loop)if(!c.Rest&&(steps.Count==0||steps[^1]!=c.Roman))steps.Add(string.IsNullOrEmpty(c.Roman)?c.Name():c.Roman);
        return string.Join(" – ",steps);
    }

    public PatternWheelDeck(Main owner,MidiPlayer player)
    {
        main=owner;midi=player;name="pattern-wheel-deck";
        status=new Label("Load a precomputed song bundle.");status.style.whiteSpace=WhiteSpace.Normal;Add(status);
        var hint=new Label("PATTERN WHEELS · rack, pinion, planets and moons\nThe rack is the song unrolled; the ring is the song wound once around, each visit coloured by family. The centre planet is the section playing, one turn per visit. Its moon is the family's fundamental loop: it turns once per loop, so a moon half the planet's size is a loop played twice. A spark where they touch marks a variation. Parked planets hold every other family's progression.");
        hint.style.whiteSpace=WhiteSpace.Normal;Add(hint);
        patterns=new VisualElement{name="pattern-families"};Add(patterns);
        chapters=new VisualElement();Add(chapters);
        canvas=new WheelCanvas(main,midi){name="pattern-wheel-overlay",pickingMode=PickingMode.Ignore};
        var toggle=new Toggle("Show rack and pattern wheels"){value=true};toggle.RegisterValueChangedCallback(e=>canvas.style.display=e.newValue?DisplayStyle.Flex:DisplayStyle.None);Add(toggle);
        if(owner.GetComponent<DrumPatternDeck>()==null)owner.gameObject.AddComponent<DrumPatternDeck>();
    }
    public void AttachOverlay(VisualElement root)
    {
        canvas.style.position=Position.Absolute;canvas.style.left=336;canvas.style.top=8;
        canvas.style.width=510;canvas.style.height=640;canvas.style.maxWidth=new Length(49,LengthUnit.Percent);root.Add(canvas);
    }
    void SeekVisit(int family)
    {
        if(source==null)return;double beat=midi.Cycles?.BeatAt(midi.ScorePosition)??0;
        var visits=source.Sections.Where(s=>s.Family==family).ToList();if(visits.Count==0)return;
        var next=visits.FirstOrDefault(s=>s.Start>beat+.01)??visits[0];
        midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(next.Start)));
    }
    void Rebuild()
    {
        patterns.Clear();chapters.Clear();if(source==null)return;
        for(int i=0;i<source.Patterns.Length;i++)
        {
            var p=source.Patterns[i];int family=p.Family;
            string loop=p.LoopBars>0?$"{p.LoopBars}-bar loop ×{Math.Max(1,p.Passes)}":$"{p.LoopBeats:0.#}-beat loop";
            var button=new Button(()=>SeekVisit(family)){text=$"{p.Short}  {p.Name} · {loop} · {p.Visits} visit{(p.Visits==1?"":"s")}\n{Numerals(p)}",tooltip="Seek to this family's next visit"};
            button.style.unityTextAlign=TextAnchor.MiddleLeft;button.style.whiteSpace=WhiteSpace.Normal;
            patterns.Add(Hatched(button,FamilyHatch(p,i)));
        }
        foreach(var section in source.Sections)
        {
            var s=section;int index=Array.FindIndex(source.Patterns,p=>p.Family==s.Family);
            var button=new Button(()=>midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(s.Start)))){text=$"{s.DisplayName} · bar {s.FirstBar+1}"};
            button.style.unityTextAlign=TextAnchor.MiddleLeft;
            chapters.Add(Hatched(button,index>=0?FamilyHatch(source.Patterns[index],index):FormHatch.ForRole(s.Role,0)));
        }
    }
    // A family's hatch swatch at the left of its button: the legend is the texture, not a colour.
    static Button Hatched(Button button,FormHatch.Pattern hatch)
    {
        var swatch=new FormHatch.Swatch(hatch);swatch.style.position=Position.Absolute;swatch.style.left=7;swatch.style.top=new Length(50,LengthUnit.Percent);swatch.style.marginTop=-7;
        button.style.paddingLeft=36;button.Add(swatch);return button;
    }
    public void Tick()
    {
        if(source!=midi.Prepared)
        {
            source=midi.Prepared;source?.EnsurePatterns();if(source!=null&&midi.Cycles!=null)source.EnsureKeyChanges(midi.Cycles.BeatAt);canvas.Load(source);Rebuild();
        }
        var feature=main.GetComponent<FeaturedInstrument>();feature.EnsureLoaded(source);canvas.LeadTrack=feature.Track;canvas.LeadChannel=feature.Channel;
        if(source?.Sections==null||source.Sections.Length==0){status.text="Prepare the pattern wheels offline.";canvas.TickControls();canvas.MarkDirtyRepaint();return;}
        double beat=midi.Cycles?.BeatAt(midi.ScorePosition)??0;
        var active=source.Sections[Math.Max(0,Array.FindLastIndex(source.Sections,s=>s.Start<=beat))];
        var pattern=source.PatternOf(active);
        string form=string.IsNullOrEmpty(source.FormName)?"":source.FormName.Split(new[]{" · "},StringSplitOptions.None)[0]+"\n";
        string compression=source.SongBars>0?$"{source.SongBars} bars → {source.Patterns.Length} fundamentals of {source.FundamentalBars} bars\n":$"{source.Sections.Length} section visits · {source.Patterns.Length} families\n";
        status.text=$"{form}{source.FormGrammar}\n{compression}Now: {active.DisplayName}{(string.IsNullOrEmpty(active.Variation)?"":" · "+active.Variation)}{(pattern==null?"":" · "+Numerals(pattern))}\n{midi.Position:0.0}s / {midi.Duration:0.0}s\n{source.Provenance}";
        canvas.TickControls();canvas.MarkDirtyRepaint();
    }

    sealed class WheelCanvas : VisualElement
    {
        readonly Main main;readonly MidiPlayer midi;OrreryBloom bloom;
        PreparedPatternSong source;
        readonly VisualElement rackInput;readonly Button transport;int dragPointer=-1;float dragY;double dragTime;
        public int ActiveNode=-1,ActiveGroup=-1,LeadTrack=-1,LeadChannel=-1;public double Turns;public float RackScroll;
        public Vector2 metaCenter,featuredCenter;public float FeaturedRadius;
        float pixelsPerSong=1000;
        // Cached per bundle: where every bar and section sits on the song ring (0..1).
        double[] barU=Array.Empty<double>(),sectionStartU=Array.Empty<double>(),sectionEndU=Array.Empty<double>(),keyU=Array.Empty<double>();double cachedDuration=-1;
        int[] familyIndex=Array.Empty<int>(),visitNumber=Array.Empty<int>();
        FormHatch.Pattern[] hatch=Array.Empty<FormHatch.Pattern>();
        // Orbits: each recurring group's families on one carrier; the rest on an outer orbit.
        sealed class Orbit{public int Group=-1;public int[] Members=Array.Empty<int>();public double Shown=double.NaN;}
        readonly List<Orbit> orbits=new();int[] orbitOf=Array.Empty<int>(),memberOf=Array.Empty<int>();
        const double Gate=.75;
        List<(double beat,double length,int pitch)>[] lead=Array.Empty<List<(double,double,int)>>();int leadKey=int.MinValue;int leadLow=48,leadHigh=84;
        public double SeekTimeForDrag(float pixels)=>Math.Clamp(dragTime-pixels*Math.Max(1,midi.Duration)/Math.Max(100,pixelsPerSong),0,midi.Duration);
        public WheelCanvas(Main main,MidiPlayer midi)
        {
            this.main=main;this.midi=midi;generateVisualContent+=Draw;
            transport=new Button(()=>{ExplorerInputFocus.ClaimUI();if(midi.IsPlaying)midi.Pause();else midi.Play();}){name="rack-play-pause",text="Play",pickingMode=PickingMode.Position};
            transport.style.position=Position.Absolute;transport.style.left=0;transport.style.top=0;transport.style.width=76;Add(transport);
            rackInput=new VisualElement{name="time-rack-seek",pickingMode=PickingMode.Position,tooltip="Drag upward to seek forward; drag downward to rewind."};
            rackInput.style.position=Position.Absolute;rackInput.style.left=0;rackInput.style.top=45;rackInput.style.bottom=44;rackInput.style.width=88;Add(rackInput);
            rackInput.RegisterCallback<PointerDownEvent>(e=>{if(e.button!=0||!midi.Loaded)return;ExplorerInputFocus.ClaimUI();dragPointer=e.pointerId;dragY=e.position.y;dragTime=midi.Position;rackInput.CapturePointer(e.pointerId);e.StopPropagation();});
            rackInput.RegisterCallback<PointerMoveEvent>(e=>{if(e.pointerId!=dragPointer)return;midi.Seek(SeekTimeForDrag(e.position.y-dragY));MarkDirtyRepaint();e.StopPropagation();});
            rackInput.RegisterCallback<PointerUpEvent>(e=>{if(e.pointerId!=dragPointer)return;rackInput.ReleasePointer(e.pointerId);dragPointer=-1;e.StopPropagation();});
            rackInput.RegisterCallback<PointerCaptureOutEvent>(_=>dragPointer=-1);
            rackInput.RegisterCallback<WheelEvent>(e=>{ExplorerInputFocus.ClaimUI();midi.Seek(Math.Clamp(midi.Position+e.delta.y*1.5,0,midi.Duration));e.StopPropagation();});
            RegisterCallback<AttachToPanelEvent>(_=>{bloom=new OrreryBloom(main);bloom.Place(this);});
            RegisterCallback<DetachFromPanelEvent>(_=>{bloom?.Dispose();bloom=null;});
            schedule.Execute(()=>bloom?.Place(this)).Every(50);
        }
        public void TickControls(){transport.text=midi.IsPlaying?"Pause":"Play";transport.SetEnabled(midi.Loaded);}
        public void Load(PreparedPatternSong data)
        {
            source=data;cachedDuration=-1;leadKey=int.MinValue;
            orbits.Clear();
            if(source?.Sections==null){familyIndex=visitNumber=orbitOf=memberOf=Array.Empty<int>();hatch=Array.Empty<FormHatch.Pattern>();return;}
            familyIndex=source.Sections.Select(s=>Array.FindIndex(source.Patterns,p=>p.Family==s.Family)).ToArray();
            visitNumber=source.Sections.Select((s,i)=>source.Sections.Take(i+1).Count(x=>x.Family==s.Family)).ToArray();
            hatch=source.Patterns.Select((p,i)=>FamilyHatch(p,i)).ToArray();
            // The two most recurring groups get their own orbits; every other family (intro,
            // bridge, an interrupting C, outro) rides the outer orbit in order of appearance.
            orbitOf=Enumerable.Repeat(-1,source.Patterns.Length).ToArray();memberOf=new int[source.Patterns.Length];
            void Place(int[] members,int group){for(int m=0;m<members.Length;m++){orbitOf[members[m]]=orbits.Count;memberOf[members[m]]=m;}orbits.Add(new Orbit{Group=group,Members=members});}
            foreach(var g in source.Groups.OrderByDescending(g=>g.Visits).ThenByDescending(g=>g.Families.Length).Take(2))
            {
                var members=g.Families.Select(f=>Array.FindIndex(source.Patterns,q=>q.Family==f)).Where(k=>k>=0&&orbitOf[k]<0).Distinct().ToArray();
                if(members.Length>=2)Place(members,g.Id);
            }
            var free=Enumerable.Range(0,source.Patterns.Length).Where(k=>orbitOf[k]<0).OrderBy(k=>Array.FindIndex(source.Sections,x=>x.Family==source.Patterns[k].Family)).ToArray();
            if(free.Length>0)Place(free,-1);
        }
        void Cache()
        {
            if(Math.Abs(cachedDuration-midi.Duration)<1e-6&&barU.Length==(source.Measures?.Length??0))return;
            cachedDuration=midi.Duration;double duration=Math.Max(1e-6,midi.Duration);
            double U(double beat)=>midi.AudioTime(midi.Cycles.SecondsAt(beat))/duration;
            barU=(source.Measures??Array.Empty<MidiCycleAnalysis.Bar>()).Select(b=>U(b.Start)).ToArray();
            sectionStartU=source.Sections.Select(s=>U(s.Start)).ToArray();sectionEndU=source.Sections.Select(s=>U(s.End)).ToArray();
            keyU=(source.KeyChanges??Array.Empty<PreparedPatternSong.KeyChange>()).Select(k=>U(k.Beat)).ToArray();
        }
        void CacheLead()
        {
            int key=LeadTrack*64+LeadChannel;if(key==leadKey&&lead.Length==source.Sections.Length)return;leadKey=key;
            var notes=(source.Notes??Array.Empty<MidiCycleAnalysis.Hit>()).Where(n=>n.Channel!=10&&n.Track==LeadTrack&&n.Channel==LeadChannel).OrderBy(n=>n.Beat).ToArray();
            lead=source.Sections.Select(s=>notes.Where(n=>n.Beat>=s.Start&&n.Beat<s.End).Select(n=>(n.Beat,n.Length,n.Pitch)).ToList()).ToArray();
            leadLow=notes.Length==0?48:notes.Min(n=>n.Pitch);leadHigh=Math.Max(leadLow+7,notes.Length==0?84:notes.Max(n=>n.Pitch));
        }

        // ---------- drawing primitives: phase 0 is twelve o'clock, increasing clockwise ----------
        static Vector2 At(Vector2 c,float r,double phase){float a=(float)(phase*Math.PI*2);return c+new Vector2(Mathf.Sin(a),-Mathf.Cos(a))*r;}
        static Vector2 Direction(double phase)=>At(Vector2.zero,1,phase);
        static void Line(Painter2D p,Vector2 a,Vector2 b,Color color,float width=1){p.strokeColor=color;p.lineWidth=width;p.BeginPath();p.MoveTo(a);p.LineTo(b);p.Stroke();}
        static void Circle(Painter2D p,Vector2 c,float r,Color color,float width=1){p.strokeColor=color;p.lineWidth=width;p.BeginPath();p.Arc(c,r,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();}
        static void Disc(Painter2D p,Vector2 c,float r,Color color){p.fillColor=color;p.BeginPath();p.Arc(c,r,Angle.Degrees(0),Angle.Degrees(360));p.Fill();}
        static void Band(Painter2D p,Vector2 c,float r,double from,double to,Color color,float width)
        {
            if(to-from<1e-5)return;to=Math.Min(to,from+.9999);
            p.strokeColor=color;p.lineWidth=width;p.lineCap=LineCap.Butt;p.BeginPath();p.Arc(c,r,Angle.Degrees((float)(from*360-90)),Angle.Degrees((float)(to*360-90)));p.Stroke();
        }
        static void DashedBand(Painter2D p,Vector2 c,float r,double from,double to,Color color,float width=1.2f)
        {
            double dash=5/(2*Math.PI*Math.Max(1,r));
            for(double a=from;a<to;a+=dash*2)Band(p,c,r,a,Math.Min(to,a+dash),color,width);
        }
        static void Teeth(Painter2D p,Vector2 c,float r,IEnumerable<double> phases,Color color,float depth=3)
        {
            p.strokeColor=color;p.lineWidth=1;p.BeginPath();
            foreach(double phase in phases){p.MoveTo(At(c,r,phase));p.LineTo(At(c,r+depth,phase));}
            p.Stroke();
        }
        static void Text(MeshGenerationContext ctx,string text,Vector2 center,float size,Color color)=>ctx.DrawText(text,center-new Vector2(text.Length*size*.29f,size*.62f),size,color);
        static Color Alpha(Color color,float alpha)=>new(color.r,color.g,color.b,color.a*alpha);
        string KeyName(int key,bool minor,bool brief=false)=>main.PitchName(key)+(brief?(minor?"m":""):(minor?" minor":" major"));
        static Color Ink(float alpha)=>FormHatch.Ink(alpha);
        static Color Label(float alpha)=>FormHatch.Label(alpha);
        static readonly Color Metal=FormHatch.Metal,Shadow=FormHatch.Shadow;
        // A hatched ring sector outlined solid (a family that returns) or dashed (heard once).
        void FormBand(Painter2D p,Vector2 c,float inner,float outer,double from,double to,int family,float ink,bool fill=true)
        {
            if(to-from<1e-5)return;
            if(fill)Band(p,c,(inner+outer)/2,from,to,Shadow,outer-inner);
            FormHatch.Sector(p,hatch[family],c,inner+.5f,outer-.5f,from,to,Ink(ink),Mathf.Clamp((outer-inner)*.7f,4.5f,8),.8f);
            bool returns=source.Patterns[family].Visits>1;
            foreach(float r in new[]{inner,outer})
                if(returns)Band(p,c,r,from,to,Ink(ink*.75f),.8f);else DashedBand(p,c,r,from,to,Ink(ink*.75f),.8f);
        }

        void Draw(MeshGenerationContext ctx)
        {
            if(contentRect.width<1||bloom==null)return;
            var p=ctx.painter2D;float w=contentRect.width,h=contentRect.height;
            bloom.Begin(w,h,resolvedStyle.display!=DisplayStyle.None);
            if(source?.Sections==null||source.Sections.Length==0||source.Patterns.Length==0||midi.Cycles==null){ctx.DrawText("PREPARE PATTERN WHEELS OFFLINE",new Vector2(20,45),12,Color.gray);bloom.End();return;}
            Cache();CacheLead();
            // Geometry: the ring meshes with the rack at nine o'clock. The caption goes below the
            // ring in a tall panel and beside it in a wide one, whichever leaves the larger ring.
            float top=44,rackWidth=92;
            float below=Mathf.Min((w-rackWidth-10)/2,(h-top-124)/2),aside=Mathf.Min((w-rackWidth-240)/2,(h-top-14)/2);
            bool beside=aside>below+6;float ring=Mathf.Max(60,beside?aside:below);
            var center=new Vector2(rackWidth+4+ring,top+ring+2);metaCenter=center;
            pixelsPerSong=2*Mathf.PI*ring;
            double duration=Math.Max(1e-6,midi.Duration),now=Math.Clamp(midi.Position/duration,0,1);
            Turns=-now;RackScroll=(float)(now*pixelsPerSong);
            double beat=midi.Cycles.BeatAt(midi.ScorePosition);
            int si=Math.Max(0,Array.FindLastIndex(source.Sections,s=>s.Start<=beat));var section=source.Sections[si];
            int fi=Math.Max(0,familyIndex[si]);var pattern=source.Patterns[fi];
            ActiveNode=section.Node;ActiveGroup=section.Group;
            DrawRack(ctx,p,center,ring,now,si,h);
            DrawRing(ctx,p,center,ring,now,si);
            float planet=ring*.34f;double progress=Math.Clamp((beat-section.Start)/Math.Max(1e-6,section.End-section.Start),0,1);
            DrawOrbits(ctx,p,center,ring,planet,si,fi,progress);
            DrawPlanet(ctx,p,center,planet,section,si,pattern,fi,beat);
            DrawCaption(ctx,p,center,ring,section,si,pattern,fi,beat,beside);
            bloom.End();
        }
        float Level(int j,int si)=>j==si?.8f:familyIndex[j]==familyIndex[si]?.5f:.22f;

        // The song unrolled: bars as teeth, section visits as hatched strips.
        void DrawRack(MeshGenerationContext ctx,Painter2D p,Vector2 center,float ring,double now,int si,float h)
        {
            float rackX=center.x-ring-7,top=40,bottom=h-44;
            float Y(double u)=>center.y+(float)((u-now)*pixelsPerSong);
            Line(p,new Vector2(rackX,top),new Vector2(rackX,bottom),Metal,2);
            double spacing=barU.Length>1?(barU[^1]-barU[0])/(barU.Length-1)*pixelsPerSong:10;int step=Math.Max(1,(int)Math.Ceiling(4/Math.Max(.1,spacing)));
            p.strokeColor=Metal;p.lineWidth=1;p.BeginPath();
            for(int b=0;b<barU.Length;b+=step){float y=Y(barU[b]);if(y<top||y>bottom)continue;p.MoveTo(new Vector2(rackX,y));p.LineTo(new Vector2(rackX+5,y));}
            p.Stroke();
            float x0=rackX-13,x1=rackX-3;
            for(int j=0;j<source.Sections.Length;j++)
            {
                float y0=Mathf.Max(top,Y(sectionStartU[j]))+1,y1=Mathf.Min(bottom,Y(sectionEndU[j]))-1;if(y1<=y0)continue;
                int f=Math.Max(0,familyIndex[j]);float ink=Level(j,si);
                p.fillColor=Shadow;p.BeginPath();p.MoveTo(new Vector2(x0,y0));p.LineTo(new Vector2(x1,y0));p.LineTo(new Vector2(x1,y1));p.LineTo(new Vector2(x0,y1));p.ClosePath();p.Fill();
                FormHatch.Draw(p,hatch[f],y0,y1,x0,x1,6,(s,v)=>new Vector2(v,s),Ink(ink),.8f);
                if(source.Patterns[f].Visits>1){Line(p,new Vector2(x0,y0),new Vector2(x0,y1),Ink(ink*.75f),.8f);Line(p,new Vector2(x1,y0),new Vector2(x1,y1),Ink(ink*.75f),.8f);}
                else for(float y=y0;y<y1;y+=8){Line(p,new Vector2(x0,y),new Vector2(x0,Mathf.Min(y1,y+4)),Ink(ink*.75f),.8f);Line(p,new Vector2(x1,y),new Vector2(x1,Mathf.Min(y1,y+4)),Ink(ink*.75f),.8f);}
                float ly=Y(sectionStartU[j]);if(ly<top||ly>bottom-10)continue;
                ctx.DrawText(source.Sections[j].DisplayName,new Vector2(9,ly+1),j==si?12:10,Label(j==si?1:ink+.15f));
            }
            // Group brackets down the left edge: the verse + chorus pair each time it returns.
            foreach(var (a,b,g) in GroupRuns())
            {
                float y0=Mathf.Max(top,Y(sectionStartU[a])+2),y1=Mathf.Min(bottom,Y(sectionEndU[b])-2);if(y1<=y0)continue;
                var color=Ink(a<=si&&si<=b?.9f:source.Sections[si].Group==g?.5f:.2f);
                Line(p,new Vector2(3,y0),new Vector2(3,y1),color,1.5f);Line(p,new Vector2(3,y0),new Vector2(7,y0),color);Line(p,new Vector2(3,y1),new Vector2(7,y1),color);
            }
            // Key changes: a diamond on the rack and the new key under the section name.
            var keys=source.KeyChanges??Array.Empty<PreparedPatternSong.KeyChange>();
            for(int k=0;k<keys.Length&&k<keyU.Length;k++)
            {
                float y=Y(keyU[k]);if(y<top||y>bottom)continue;bool passed=keyU[k]<=now+1e-6;
                var d=new Vector2(rackX,y);p.fillColor=Label(passed?.95f:.6f);p.BeginPath();p.MoveTo(d+new Vector2(0,-5));p.LineTo(d+new Vector2(4,0));p.LineTo(d+new Vector2(0,5));p.LineTo(d+new Vector2(-4,0));p.ClosePath();p.Fill();
                Line(p,new Vector2(x0-2,y),new Vector2(rackX+6,y),Label(passed?.7f:.4f),1);
                ctx.DrawText($"→ {KeyName(keys[k].Key,keys[k].Minor)}",new Vector2(9,y+14),10,Label(passed?.95f:.65f));
            }
            // The mesh point: rack tooth meets ring tooth at nine o'clock.
            float flash=(float)Math.Exp(-Math.Max(0,midi.Cycles.BeatAt(midi.ScorePosition)-source.Sections[si].Start)*4);
            Line(p,new Vector2(rackX,center.y),new Vector2(center.x-ring,center.y),Ink(.45f+.55f*flash),1.5f);
            Disc(p,new Vector2(rackX,center.y),3.5f,Label(1));bloom.Disk(new Vector2(rackX,center.y),3,Ink(1),flash);
            ctx.DrawText("DRAG TO SEEK",new Vector2(0,h-30),10,Label(.6f));
        }
        // "V2": the family's short name and which visit this is. A bookend (the intro's
        // material closing the song) keeps its own name.
        string RimLabel(int j)
        {
            var pattern=source.Patterns[Math.Max(0,familyIndex[j])];var section=source.Sections[j];
            string own=string.IsNullOrEmpty(section.Short)?pattern.Short:section.Short;
            int same=source.Sections.Count(s=>s.Family==section.Family&&(string.IsNullOrEmpty(s.Short)?pattern.Short:s.Short)==own);
            return same>1?own+visitNumber[j]:own;
        }
        IEnumerable<(int first,int last,int group)> GroupRuns()
        {
            var sections=source.Sections;
            for(int i=0;i<sections.Length;)
            {
                if(sections[i].Group<0){i++;continue;}
                int j=i;while(j+1<sections.Length&&sections[j+1].Group==sections[i].Group&&sections[j+1].GroupVisit==sections[i].GroupVisit)j++;
                yield return (i,j,sections[i].Group);i=j+1;
            }
        }

        // The song wound once around the pinion. Now is at nine o'clock; the ring turns clockwise.
        void DrawRing(MeshGenerationContext ctx,Painter2D p,Vector2 center,float ring,double now,int si)
        {
            double Phase(double u)=>.75-(u-now);
            double spacing=barU.Length>1?(barU[^1]-barU[0])/(barU.Length-1)*pixelsPerSong:10;int step=Math.Max(1,(int)Math.Ceiling(4.5/Math.Max(.1,spacing)));
            Teeth(p,center,ring,barU.Where((_,i)=>i%step==0).Select(Phase),Metal,4);
            Circle(p,center,ring,Metal,1.2f);
            float inner=ring-13,outer=ring-4;
            for(int j=0;j<source.Sections.Length;j++)
            {
                double a=Phase(sectionEndU[j]),b=Phase(sectionStartU[j]);double gap=1.2/(2*Math.PI*outer);
                int f=Math.Max(0,familyIndex[j]);float ink=Level(j,si);
                FormBand(p,center,inner,outer,a+gap,b-gap,f,ink);
                if(j==si)bloom.Arc(center,(inner+outer)/2,a+gap,b-gap,Ink(1),.1f);
                if((b-a)*2*Math.PI*inner>=24)Text(ctx,RimLabel(j),At(center,inner-10,(a+b)/2),10,Label(j==si?1:ink+.1f));
            }
            foreach(var (first,last,g) in GroupRuns())
            {
                double a=Phase(sectionEndU[last]),b=Phase(sectionStartU[first]);double gap=2.5/(2*Math.PI*(ring-20));
                bool current=first<=si&&si<=last,family=g==source.Sections[si].Group;var color=Ink(current?.9f:family?.5f:.18f);
                Band(p,center,ring-20,a+gap,b-gap,color,1.2f);
                Line(p,At(center,ring-20,a+gap),At(center,ring-16,a+gap),color);Line(p,At(center,ring-20,b-gap),At(center,ring-16,b-gap),color);
            }
            // Key changes cross the ring's rim, named outside it.
            var keys=source.KeyChanges??Array.Empty<PreparedPatternSong.KeyChange>();
            for(int k=0;k<keys.Length&&k<keyU.Length;k++)
            {
                double phase=Phase(keyU[k]);bool passed=keyU[k]<=now+1e-6;
                Line(p,At(center,inner-2,phase),At(center,ring+5,phase),Label(passed?.85f:.5f),1.2f);
                Text(ctx,KeyName(keys[k].Key,keys[k].Minor,true),At(center,ring+15,phase),9,Label(passed?.9f:.55f));
            }
            var mark=At(center,(inner+outer)/2,.75);Disc(p,mark,3,Label(1));bloom.Disk(mark,2.5f,Ink(1),midi.IsPlaying?.5f:.2f);
        }

        int OrbitOfSection(int j)=>familyIndex[j]<0?-1:orbitOf[familyIndex[j]];
        // Carrier phase of an orbit, from the song position alone (so seeking lands exactly):
        // member k sits at phase c − k/n and passes the gate at the middle of its visit. While
        // another orbit's family plays, this one holds where its last visit ended.
        double Carrier(int o,int si,double progress)
        {
            int n=orbits[o].Members.Length,j=si;double q=progress;
            while(j>=0&&OrbitOfSection(j)!=o)j--;
            if(j<si)q=1;
            if(j<0){j=0;while(j<source.Sections.Length&&OrbitOfSection(j)!=o)j++;if(j==source.Sections.Length)return Gate;q=0;}
            return Gate+(memberOf[familyIndex[j]]+q-.5)/n;
        }
        static double Wrap(double x)=>x-Math.Round(x);

        // The planetary train: one carrier ring per orbit, its families fixed on it.
        void DrawOrbits(MeshGenerationContext ctx,Painter2D p,Vector2 center,float ring,float planet,int si,int fi,double progress)
        {
            int count=orbits.Count;if(count==0)return;
            float inner=planet+16,outer=ring-28,lane=(outer-inner)/count;
            int playing=orbitOf[fi];float blend=Main.ReducedMotion?1:1-Mathf.Exp(-Time.unscaledDeltaTime*6);
            for(int o=0;o<count;o++)
            {
                var orbit=orbits[o];int n=orbit.Members.Length;float r=inner+lane*(o+.5f);bool on=o==playing;
                double target=Carrier(o,si,progress);
                orbit.Shown=double.IsNaN(orbit.Shown)?target:orbit.Shown+Wrap(target-orbit.Shown)*blend;
                // The carrier: solid while it turns, dashed while an interruption holds it.
                if(on)Circle(p,center,r,Ink(.4f),1);else DashedBand(p,center,r,0,1,Ink(.18f),.8f);
                Teeth(p,center,r-1.5f,Enumerable.Range(0,24).Select(i=>orbit.Shown+i/24.0),Ink(on?.3f:.1f),3);
                float size=Mathf.Clamp(Mathf.Min(lane/2-4,Mathf.PI*r/n-6),7,Mathf.Max(22,ring*.12f));
                for(int m=0;m<n;m++)
                {
                    int k=orbit.Members[m];var at=At(center,r,orbit.Shown-m/(double)n);
                    DrawFundamental(ctx,p,at,size,k,k==fi);
                }
                // The gate: where a family plays, in line with the rack and the ring's now.
                var notch=At(center,r+size+4,Gate);p.fillColor=Ink(on?.7f:.3f);p.BeginPath();p.MoveTo(notch);p.LineTo(notch+new Vector2(-5,-3.5f));p.LineTo(notch+new Vector2(-5,3.5f));p.ClosePath();p.Fill();
                if(on)Line(p,At(center,planet+2,Gate),At(center,r-size-2,Gate),Ink(.45f),1);
            }
        }
        void DrawFundamental(MeshGenerationContext ctx,Painter2D p,Vector2 c,float r,int k,bool playing)
        {
            var pattern=source.Patterns[k];
            Disc(p,c,r,Shadow);
            Teeth(p,c,r,Enumerable.Range(0,Math.Max(4,pattern.LoopBars*2)).Select(i=>i/(double)Math.Max(4,pattern.LoopBars*2)),Alpha(Metal,.8f),2);
            FormBand(p,c,r-2.5f,r,0,1,k,playing?.9f:.55f,false);
            double loop=Math.Max(.25,pattern.LoopBeats);float band=r>=14?4.5f:3f;
            foreach(var chord in pattern.Loop)Band(p,c,r-3.5f-band/2,chord.Start/loop,chord.End/loop,CyclicOrrery.ChordColor(chord,main.currentKey),band);
            Text(ctx,pattern.Short,c-new Vector2(0,r*.1f),Mathf.Clamp(r*.5f,8,12),Label(playing?1:.8f));
            // One dot per visit: how often this family returns.
            int visits=Math.Min(10,pattern.Visits);
            if(r>=12)for(int v=0;v<visits;v++)Disc(p,At(c,r*.45f,.5+(v-(visits-1)/2.0)*.08),1.2f,Ink(.8f));
            if(playing)Circle(p,c,r+3.5f,Label(.85f),1.1f);
        }

        // The section playing: its hatched form rim, this visit's chords, the fundamental rolling inside.
        void DrawPlanet(MeshGenerationContext ctx,Painter2D p,Vector2 center,float planet,PreparedPatternSong.Section section,int si,PreparedPatternSong.Pattern pattern,int fi,double beat)
        {
            var c=center;float R=planet;
            featuredCenter=center;FeaturedRadius=planet;
            double length=Math.Max(1e-6,section.End-section.Start);
            double progress=Math.Clamp((beat-section.Start)/length,0,1);
            Disc(p,c,R,Shadow);
            int bars=Math.Max(4,section.BarCount);
            Teeth(p,c,R,Enumerable.Range(0,bars).Select(i=>i/(double)bars),Metal,4);
            FormBand(p,c,R-3.5f,R,0,1,fi,.75f,false);
            // This visit's chords, fixed on the band: brighter once played.
            float chordIn=R-15,chordOut=R-5,chordMid=(chordIn+chordOut)/2;
            foreach(var chord in source.Chords)
            {
                if(chord.End<=section.Start||chord.Start>=section.End)continue;
                double a=(Math.Max(chord.Start,section.Start)-section.Start)/length,b=(Math.Min(chord.End,section.End)-section.Start)/length;
                var hue=CyclicOrrery.ChordColor(chord,main.currentKey);
                Band(p,c,chordMid,a,b,a<progress?hue:Alpha(hue,.55f),chordOut-chordIn);
            }
            var passes=section.Passes??Array.Empty<PreparedPatternSong.Pass>();
            int pi=Math.Max(0,Array.FindLastIndex(passes,x=>x.Start<=beat+1e-6));
            // Pass boundaries tick the band; partial passes (lead-in, tag) are dotted inside it;
            // bars whose harmony departs from the fundamental are outlined.
            bool varied=false;
            for(int k=0;k<passes.Length;k++)
            {
                var pass=passes[k];double a=(pass.Start-section.Start)/length,b=(pass.End-section.Start)/length;
                if(k>0)Line(p,At(c,chordIn-2,a),At(c,R,a),Ink(.85f),1.5f);
                if(pass.Partial)for(double x=a;x<b;x+=.012)Disc(p,At(c,chordIn-3,x),.9f,Ink(.45f));
                var changed=pass.Changed??Array.Empty<double>();
                for(int i=0;i+1<changed.Length;i+=2)
                {
                    double from=pass.Start+changed[i]-pass.Offset,to=pass.Start+changed[i+1]-pass.Offset;
                    Band(p,c,chordIn-1.5f,(from-section.Start)/length,(to-section.Start)/length,Label(.9f),2);
                    if(k==pi&&beat>=from&&beat<to)varied=true;
                }
            }
            double turn=progress;float inner=chordIn-3;var contact=At(c,inner,turn);
            var sounding=source.Chords.LastOrDefault(x=>x.Start<=beat+1e-6&&beat<x.End);var hueNow=CyclicOrrery.ChordColor(sounding,main.currentKey);
            // The moon: one turn per loop, rolling on the chord band's inner edge.
            double loop=Math.Max(.25,pattern.LoopBeats);
            bool moon=passes.Length>1||length>loop*1.05;
            float m=Mathf.Clamp(inner*(float)(loop/length),14,inner*.5f);
            var pass0=passes.Length>0?passes[pi]:null;
            double phase=pass0==null?((beat-section.Start)/loop%1+1)%1:(((pass0.Offset+beat-pass0.Start)/loop)%1+1)%1;
            Vector2 labelAt=c;
            if(moon)
            {
                var mc=c+Direction(turn)*(inner-m);
                Line(p,c,mc,Ink(.45f),1.5f);Disc(p,c,3,Ink(.8f));
                Disc(p,mc,m,Shadow);
                int teeth=Math.Max(6,pattern.LoopBars*4);double spin=turn-phase;
                Teeth(p,mc,m,Enumerable.Range(0,teeth).Select(i=>spin+i/(double)teeth),Metal,2.5f);
                FormBand(p,mc,m-2,m,0,1,fi,.7f,false);
                int transpose=pass0?.Transpose??0;
                foreach(var chord in pattern.Loop)
                {
                    var shifted=new SongFormAnalysis.ChordStep{Root=chord.Rest?-1:HarmonyModel.Mod(chord.Root+transpose),Quality=chord.Quality};
                    double a=chord.Start/loop,b=chord.End/loop;bool here=phase>=a&&phase<b;
                    var hue=CyclicOrrery.ChordColor(shifted,main.currentKey);
                    Band(p,mc,m-5.5f,spin+a,spin+b,here?hue:Alpha(hue,.8f),5);
                    if(m>=26&&(b-a)*2*Math.PI*m>=18)Text(ctx,string.IsNullOrEmpty(chord.Roman)?chord.Name():chord.Roman,At(mc,m-15,spin+(a+b)/2),8,Label(here?1:.6f));
                }
                if(m>=14)
                {
                    int full=passes.Count(x=>!x.Partial),index=passes.Take(pi+1).Count(x=>!x.Partial);
                    string count=pass0!=null&&pass0.Partial?(pi==0?"in":"tag"):$"{index}/{full}";
                    Text(ctx,count,mc,m>=26?13:9,Color.white);
                    if(m>=30)Text(ctx,"loop",mc+new Vector2(0,13),8,Label(.6f));
                }
                labelAt=c-Direction(turn)*inner*.5f;
            }
            else
            {
                Line(p,c,contact,Ink(.45f),1.5f);Disc(p,c,3,Ink(.8f));
                labelAt=c+new Vector2(0,R*.2f);
            }
            // Contact: the fundamental meets this visit. The glow is the sounding chord's
            // colour; a white spark where the visit departs from the fundamental.
            Disc(p,contact,varied?4.5f:3,varied?Color.white:hueNow);
            bloom.Disk(contact,varied?4:2.5f,varied?Color.white:hueNow,midi.IsPlaying?(varied?.9f:.45f):.2f);
            if(varied)Circle(p,contact,7,Ink(.8f),1.2f);
            DrawOnsets(p,c,inner-2,section,si,progress,length,beat);
            Text(ctx,section.DisplayName,labelAt,13,Color.white);
            if(!string.IsNullOrEmpty(section.Variation))Text(ctx,section.Variation.Length>28?section.Variation.Substring(0,27)+"…":section.Variation,labelAt+new Vector2(0,15),9,Label(.7f));
        }

        // The lead line's onsets as short ticks inside the chord band: a repeated rhythm reads
        // as the same comb of ticks once per pass. Played ticks are bright; each strike flashes.
        void DrawOnsets(Painter2D p,Vector2 c,float r,PreparedPatternSong.Section section,int si,double progress,double length,double beat)
        {
            if(si>=lead.Length||lead[si].Count==0)return;
            p.lineWidth=1.2f;
            foreach(var (onset,duration,pitch) in lead[si])
            {
                double at=(onset-section.Start)/length;float height=2+5*Mathf.InverseLerp(leadLow,leadHigh,pitch);
                double elapsed=beat-onset;bool played=elapsed>=0;
                p.strokeColor=played?Ink(.85f):Ink(.3f);
                p.BeginPath();p.MoveTo(At(c,r,at));p.LineTo(At(c,r-height,at));p.Stroke();
                float energy=midi.IsPlaying&&played?(float)Math.Exp(-elapsed*6):0;
                if(energy>.03f)bloom.Disk(At(c,r-height,at),1.6f,Color.white,energy*.6f);
            }
        }

        // Below the ring: a few joined lines. Beside it (a wide panel): one fact per line.
        void DrawCaption(MeshGenerationContext ctx,Painter2D p,Vector2 center,float ring,PreparedPatternSong.Section section,int si,PreparedPatternSong.Pattern pattern,int fi,double beat,bool beside)
        {
            var passes=section.Passes??Array.Empty<PreparedPatternSong.Pass>();int pi=Math.Max(0,Array.FindLastIndex(passes,q=>q.Start<=beat+1e-6));
            int full=passes.Count(q=>!q.Partial),index=passes.Take(pi+1).Count(q=>!q.Partial);
            string passText=passes.Length==0?"":passes[pi].Partial?(pi==0?"lead-in":"tag"):$"pass {index} of {full}";
            string loop=$"{pattern.LoopBars} bar loop   ·   {passText}";
            string visits=$"visit {visitNumber[si]} of {pattern.Visits}";
            string group=section.Group>=0&&section.Group<source.Groups.Length?$"{source.Groups[section.Group].Short} returns: {section.GroupVisit} of {source.Groups[section.Group].Visits}":"";
            // The key now, and the pull of any tonicization toward the key it points at.
            var tension=midi.CurrentTension;string keyText=KeyName(main.currentKey,main.MinorMode);
            if(tension!=null)keyText+=$"   ·   {tension.Kind} → {KeyName(tension.Target,tension.TargetMinor)}{(tension.Completes?" (key change)":"")}";
            string compression=source.SongBars>0?$"{source.SongBars} bars → {source.Patterns.Length} fundamentals · {source.FundamentalBars} bars":"Older bundle: regenerate with PatternPrep for fundamentals and variations";
            float x=beside?center.x+ring+18:center.x-ring+6,y=beside?center.y-ring+8:center.y+ring+12;
            void Row(string text,float size,Color color,float gap){if(!string.IsNullOrEmpty(text))ctx.DrawText(text,new Vector2(x,y),size,color);y+=gap;}
            Row(section.DisplayName,14,Color.white,beside?18:0);
            if(!beside){ctx.DrawText(section.Variation,new Vector2(x+section.DisplayName.Length*8.4f+14,y),14,Label(.8f));y+=20;}
            else Row(section.Variation,11,Label(.8f),22);
            // The family's hatch is its legend: a swatch before the fundamental's numerals.
            FormHatch.Draw(p,hatch[fi],x,x+22,y+1,y+13,4.5f,(sv,v)=>new Vector2(sv,v),Ink(.75f),.9f);
            ctx.DrawText($"{pattern.Short}  {Numerals(pattern)}",new Vector2(x+28,y),11,Label(.9f));
            if(beside){y+=18;Row(loop,10,Label(.75f),16);Row(visits,10,Label(.6f),16);Row(group,10,Label(.6f),24);}
            else{ctx.DrawText(loop,new Vector2(x+34+(pattern.Short.Length+Numerals(pattern).Length)*6.2f,y),11,Label(.75f));y+=18;Row(visits+(group.Length>0?"   ·   "+group:""),10,Label(.6f),18);}
            Row(keyText,10,Label(.8f),16);
            Row(source.SongBars>0?source.FormGrammar:"",11,Label(.85f),16);
            Row(compression,10,Label(.6f),16);
            string form=string.IsNullOrEmpty(source.FormName)?"":source.FormName.Split(new[]{" · "},StringSplitOptions.None)[0];
            ctx.DrawText(form,new Vector2(84,12),12,Label(.75f));
        }
    }
}
