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
// The structure wheel (a version-6 bundle's motif layer, Docs/MOTIF-REDUCTION.md §14) replaces orbits, planet and moon: ring 1
// holds the sections (A′ in A's hatch, the bars a reprise leaves out as a slit, a ghost cursor that jumps it), ring 2 the
// phrases or periods of the section playing, ring 3 the units of the phrase playing (powers as staircases), and the centre the
// playing cell's paradigm on a pitch clock, where rotation is transposition and a mirror is an inversion. Rings 2, 3 and the
// glyph are retained elements that only turn while the music plays; they are redrawn when the section, phrase or cell changes.
public sealed class PatternWheelDeck : VisualElement
{
    readonly Main main;readonly MidiPlayer midi;readonly Label status,hint;
    readonly WheelCanvas canvas;readonly VisualElement patterns,chapters;
    PreparedPatternSong source;bool chapterStructure;
    const string StructurePref="Resonance.StructureWheel";
    const string ClassicHint="PATTERN WHEELS · rack, pinion, planets and moons\nThe rack is the song unrolled; the ring is the song wound once around, each visit coloured by family. The centre planet is the section playing, one turn per visit. Its moon is the family's fundamental loop: it turns once per loop, so a moon half the planet's size is a loop played twice. A spark where they touch marks a variation. Parked planets hold every other family's progression.";
    const string StructureHint="STRUCTURE WHEEL · the piece reduced to motifs\nThe outer ring holds the sections: a reprise shares its source's hatch, and the bars it leaves out are a slit that a ghost cursor jumps. The middle ring is the section playing, cut into phrases or periods; the inner ring is the phrase playing, its powers drawn as staircases. At the centre the playing motif sits on a pitch clock: rotation is transposition, a mirror image is an inversion, and white sparks mark where the music departs from it. Click an arc to hear it.";
    public VisualElement Overlay=>canvas;
    // The structure wheel (motif layer): whether it is drawn, its model and path, what it lit, and where an anchor points.
    public bool StructureActive=>canvas.StructureActive;
    public bool StructureEnabled{get=>canvas.StructureEnabled;set=>canvas.StructureEnabled=value;}
    public MotifWheelModel StructureModel=>canvas.Model;
    public MotifWheelModel.Path StructurePath=>canvas.StructurePath;
    public int LitSection=>canvas.LitSection;public int LitPhrase=>canvas.LitPhrase;public int LitUnit=>canvas.LitUnit;public int LitCell=>canvas.LitCell;
    public int SparksDrawn=>canvas.SparksDrawn;public int AlteredSparksDrawn=>canvas.AlteredSparksDrawn;public int GhostLeaps=>canvas.GhostLeaps;public bool OmissionLit=>canvas.OmissionLit;
    public VisualElement StructureRing2=>canvas.Ring2;public VisualElement StructureRing3=>canvas.Ring3;public VisualElement StructureGlyph=>canvas.GlyphElement;public VisualElement StructurePicker=>canvas.Picker;
    public float Ring1Radius=>canvas.Ring1Middle;public float Ring2Radius=>canvas.Ring2Middle;
    // Ring 1's screen phase of a beat (0 twelve o'clock, clockwise; nine o'clock is now), and a point on a ring at a phase.
    public double Ring1Phase(double beat)=>canvas.Ring1Phase(beat);
    public Vector2 RingPoint(float radius,double phase)=>canvas.RingPoint(radius,phase);
    // A Director annotationAnchor (§14.8) as a point in the overlay's local space; false when it does not resolve now.
    public bool TryGetAnchor(string anchor,out Vector2 local)=>canvas.TryGetAnchor(anchor,out local);
    public double RackTurns=>canvas.Turns;
    public float RackPixels=>canvas.RackScroll;
    // Form node of the family now playing (its planet) and the group it belongs to, if any.
    public int ActiveFamilyNode=>canvas.ActiveNode;
    public int ActiveGroup=>canvas.ActiveGroup;
    public Vector2 MetaCenter=>canvas.metaCenter;
    public Vector2 FeaturedCenter=>canvas.featuredCenter;
    public int LeadVocalTrack=>canvas.LeadTrack;
    public float FeaturedRadius=>canvas.FeaturedRadius;
    public InstrumentChangers Changers=>canvas.Changers;
    public LyricWheel Lyrics=>canvas.Lyrics;
    public LyricGraph Graph=>canvas.Graph;
    public bool LyricLayout=>canvas.LyricLayout;
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
        hint=new Label(ClassicHint);hint.style.whiteSpace=WhiteSpace.Normal;Add(hint);
        patterns=new VisualElement{name="pattern-families"};Add(patterns);
        chapters=new VisualElement();Add(chapters);
        canvas=new WheelCanvas(main,midi){name="pattern-wheel-overlay",pickingMode=PickingMode.Ignore};
        var toggle=new Toggle("Show rack and pattern wheels"){value=true};toggle.RegisterValueChangedCallback(e=>canvas.style.display=e.newValue?DisplayStyle.Flex:DisplayStyle.None);Add(toggle);
        // On by default; it only changes songs prepared with a motif layer, and the choice is the viewer's own.
        var structure=new Toggle("Structure wheel (motifs)"){name="structure-wheel",value=PlayerPrefs.GetInt(StructurePref,1)!=0,tooltip="Draw the piece's motif reduction (sections, phrases, units, the playing motif on a pitch clock) instead of the planets, for songs prepared with motifs"};
        structure.RegisterValueChangedCallback(e=>{PlayerPrefs.SetInt(StructurePref,e.newValue?1:0);canvas.StructureEnabled=e.newValue;});Add(structure);canvas.StructureEnabled=structure.value;
        if(owner.GetComponent<DrumPatternDeck>()==null)owner.gameObject.AddComponent<DrumPatternDeck>();
        if(owner.GetComponent<DrumLyricRack>()==null)owner.gameObject.AddComponent<DrumLyricRack>();
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
        patterns.Clear();chapters.Clear();chapterStructure=canvas.StructureAvailable;hint.text=chapterStructure?StructureHint:ClassicHint;if(source==null)return;
        for(int i=0;i<source.Patterns.Length;i++)
        {
            var p=source.Patterns[i];int family=p.Family;
            string loop=p.LoopBars>0?$"{p.LoopBars}-bar loop ×{Math.Max(1,p.Passes)}":$"{p.LoopBeats:0.#}-beat loop";
            var button=new Button(()=>SeekVisit(family)){text=$"{p.Short}  {p.Name} · {loop} · {p.Visits} visit{(p.Visits==1?"":"s")}\n{Numerals(p)}",tooltip="Seek to this family's next visit"};
            button.style.unityTextAlign=TextAnchor.MiddleLeft;button.style.whiteSpace=WhiteSpace.Normal;
            patterns.Add(Hatched(button,FamilyHatch(p,i)));
        }
        // The structure wheel's chapters: each section, then its phrases or periods, hatched as on the rings.
        if(chapterStructure)
        {
            var model=canvas.Model;
            foreach(int s in model.Sections)
            {
                var node=model.Nodes[s];
                chapters.Add(Chapter(node,$"{node.Label} · bar {canvas.BarOf(node.Start)+1}",0));
                for(int k=0;k<node.ChildCount;k++){var child=model.Nodes[node.FirstChild+k];chapters.Add(Chapter(child,$"{child.Label} · bar {canvas.BarOf(child.Start)+1}{(child.Projected?" (as before)":"")}",18));}
            }
            return;
        }
        foreach(var section in source.Sections)
        {
            var s=section;int index=Array.FindIndex(source.Patterns,p=>p.Family==s.Family);
            var button=new Button(()=>midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(s.Start)))){text=$"{s.DisplayName} · bar {s.FirstBar+1}"};
            button.style.unityTextAlign=TextAnchor.MiddleLeft;
            chapters.Add(Hatched(button,index>=0?FamilyHatch(source.Patterns[index],index):FormHatch.ForRole(s.Role,0)));
        }
    }
    Button Chapter(PreparedPatternSong.MotifNode node,string text,float indent)
    {
        double start=node.Start;var button=new Button(()=>midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(start)))){text=text,tooltip=string.IsNullOrEmpty(node.Text)?node.Name:node.Name+" · "+node.Text};
        button.style.unityTextAlign=TextAnchor.MiddleLeft;button.style.marginLeft=indent;
        return Hatched(button,canvas.HatchOf(node.Id));
    }
    // A family's hatch swatch at the left of its button: the legend is the texture, not a colour.
    static Button Hatched(Button button,FormHatch.Pattern hatch)
    {
        var swatch=new FormHatch.Swatch(hatch);swatch.style.position=Position.Absolute;swatch.style.left=7;swatch.style.top=new Length(50,LengthUnit.Percent);swatch.style.marginTop=-7;
        button.style.paddingLeft=36;button.Add(swatch);return button;
    }
    public void Tick()
    {
        using var perf=Perf.WheelsTick.Auto();
        if(source!=midi.Prepared)
        {
            source=midi.Prepared;source?.EnsurePatterns();if(source!=null&&midi.Cycles!=null)source.EnsureKeyChanges(midi.Cycles.BeatAt);canvas.Load(source);Rebuild();
        }
        var feature=main.GetComponent<FeaturedInstrument>();feature.EnsureLoaded(source);canvas.LeadTrack=feature.Track;canvas.LeadChannel=feature.Channel;
        if(source?.Sections==null||source.Sections.Length==0){status.text="Prepare the pattern wheels offline.";canvas.TickControls();canvas.TickStructure();canvas.MarkDirtyRepaint();return;}
        double beat=midi.Cycles?.BeatAt(midi.ScorePosition)??0;
        var active=source.Sections[Math.Max(0,Array.FindLastIndex(source.Sections,s=>s.Start<=beat))];
        var pattern=source.PatternOf(active);
        string form=string.IsNullOrEmpty(source.FormName)?"":source.FormName.Split(new[]{" · "},StringSplitOptions.None)[0]+"\n";
        string compression=source.SongBars>0?$"{source.SongBars} bars → {source.Patterns.Length} fundamentals of {source.FundamentalBars} bars\n":$"{source.Sections.Length} section visits · {source.Patterns.Length} families\n";
        status.text=$"{form}{source.FormGrammar}\n{compression}Now: {active.DisplayName}{(string.IsNullOrEmpty(active.Variation)?"":" · "+active.Variation)}{(pattern==null?"":" · "+Numerals(pattern))}\n{midi.Position:0.0}s / {midi.Duration:0.0}s\n{source.Provenance}";
        canvas.TickControls();canvas.TickStructure();if(canvas.StructureAvailable!=chapterStructure)Rebuild();canvas.MarkDirtyRepaint();
    }

    sealed class WheelCanvas : VisualElement
    {
        readonly Main main;readonly MidiPlayer midi;OrreryBloom bloom;
        PreparedPatternSong source;
        readonly VisualElement rackInput,progressTrack,progressFill;readonly Button transport;int dragPointer=-1;float dragY;double dragTime;
        public int ActiveNode=-1,ActiveGroup=-1,LeadTrack=-1,LeadChannel=-1;public double Turns;public float RackScroll;
        public Vector2 metaCenter,featuredCenter;public float FeaturedRadius;
        public readonly InstrumentChangers Changers;public readonly LyricWheel Lyrics;public readonly LyricGraph Graph=new();public bool LyricLayout;float lyricFocus;
        float pixelsPerSong=1000;
        // Cached per bundle: where every bar and section sits on the song ring (0..1).
        double[] barU=Array.Empty<double>(),sectionStartU=Array.Empty<double>(),sectionEndU=Array.Empty<double>(),keyU=Array.Empty<double>();double cachedDuration=-1;
        int[] familyIndex=Array.Empty<int>(),visitNumber=Array.Empty<int>(),familyKey=Array.Empty<int>();
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
            Changers=new InstrumentChangers(main,midi);Lyrics=new LyricWheel(main,midi,Changers);Add(Graph.Element);
            transport=new Button(()=>{ExplorerInputFocus.ClaimUI();if(midi.IsPlaying)midi.Pause();else midi.Play();}){name="rack-play-pause",text="Play",pickingMode=PickingMode.Position};
            transport.style.position=Position.Absolute;transport.style.left=0;transport.style.top=0;transport.style.width=76;Add(transport);
            // While the song loads the button is locked, with the load's progress along its foot.
            progressTrack=new VisualElement{name="load-progress",pickingMode=PickingMode.Ignore};progressTrack.style.position=Position.Absolute;progressTrack.style.left=4;progressTrack.style.top=34;progressTrack.style.width=68;progressTrack.style.height=4;
            progressTrack.style.backgroundColor=new Color(.12f,.18f,.26f);progressTrack.style.display=DisplayStyle.None;Add(progressTrack);
            progressFill=new VisualElement{pickingMode=PickingMode.Ignore};progressFill.style.position=Position.Absolute;progressFill.style.left=0;progressFill.style.top=0;progressFill.style.height=4;progressFill.style.backgroundColor=new Color(.5f,.78f,.95f);progressTrack.Add(progressFill);
            rackInput=new VisualElement{name="time-rack-seek",pickingMode=PickingMode.Position,tooltip="Drag upward to seek forward; drag downward to rewind."};
            rackInput.style.position=Position.Absolute;rackInput.style.left=0;rackInput.style.top=45;rackInput.style.bottom=44;rackInput.style.width=88;Add(rackInput);
            rackInput.RegisterCallback<PointerDownEvent>(e=>{if(e.button!=0||!midi.Loaded)return;ExplorerInputFocus.ClaimUI();dragPointer=e.pointerId;dragY=e.position.y;dragTime=midi.Position;rackInput.CapturePointer(e.pointerId);e.StopPropagation();});
            rackInput.RegisterCallback<PointerMoveEvent>(e=>{if(e.pointerId!=dragPointer)return;midi.Seek(SeekTimeForDrag(e.position.y-dragY));MarkDirtyRepaint();e.StopPropagation();});
            rackInput.RegisterCallback<PointerUpEvent>(e=>{if(e.pointerId!=dragPointer)return;rackInput.ReleasePointer(e.pointerId);dragPointer=-1;e.StopPropagation();});
            rackInput.RegisterCallback<PointerCaptureOutEvent>(_=>dragPointer=-1);
            rackInput.RegisterCallback<WheelEvent>(e=>{ExplorerInputFocus.ClaimUI();midi.Seek(Math.Clamp(midi.Position+e.delta.y*1.5,0,midi.Duration));e.StopPropagation();});
            structure=BuildStructure();Insert(1,structure);
            RegisterCallback<AttachToPanelEvent>(_=>{bloom=new OrreryBloom(main);bloom.Place(this);});
            RegisterCallback<DetachFromPanelEvent>(_=>{bloom?.Dispose();bloom=null;});
            schedule.Execute(()=>bloom?.Place(this)).Every(50);
        }
        // Click targets over the instrument changers, placed where they were last drawn: a click
        // makes that lane the highlighted instrument.
        readonly List<VisualElement> picks=new();
        public void TickControls()
        {
            var audio=main.GetComponent<SongAudio>();var stems=main.GetComponent<StemPlayback>();
            bool loading=(audio!=null&&audio.Busy)||(stems!=null&&stems.IsLoading);
            transport.text=loading?"Loading":midi.IsPlaying?"Pause":"Play";transport.SetEnabled(midi.Loaded&&!loading);
            progressTrack.style.display=loading?DisplayStyle.Flex:DisplayStyle.None;
            if(loading)progressFill.style.width=68*Mathf.Clamp01(audio!=null?audio.Progress:0);
            Changers.FeaturedTrack=LeadTrack;Changers.FeaturedChannel=LeadChannel;if(midi.Cycles!=null)Graph.Tick(LyricLayout&&Lyrics.HasLyrics,midi.Cycles.BeatAt(midi.ScorePosition));
            var cells=Changers.Cells;
            while(picks.Count<cells.Count)
            {
                int index=picks.Count;var pick=new VisualElement{name="changer-select-"+index,pickingMode=PickingMode.Position};pick.style.position=Position.Absolute;
                pick.RegisterCallback<PointerDownEvent>(e=>
                {
                    if(e.button!=0||index>=Changers.Cells.Count)return;
                    ExplorerInputFocus.ClaimUI();var c=Changers.Cells[index];SelectLane(c.track,c.channel);e.StopPropagation();
                });
                pick.RegisterCallback<PointerEnterEvent>(_=>Changers.Hover=index);
                pick.RegisterCallback<PointerLeaveEvent>(_=>{if(Changers.Hover==index)Changers.Hover=-1;});
                Add(pick);picks.Add(pick);
            }
            for(int i=0;i<picks.Count;i++)
            {
                var pick=picks[i];bool on=i<cells.Count;
                pick.style.display=on?DisplayStyle.Flex:DisplayStyle.None;if(!on)continue;
                var r=cells[i].rect;pick.style.left=r.x;pick.style.top=r.y;pick.style.width=r.width;pick.style.height=r.height;
                bool featured=cells[i].track==LeadTrack&&cells[i].channel==LeadChannel;
                pick.tooltip=featured?$"{cells[i].name} is the highlighted instrument":$"Click to highlight {cells[i].name} in white";
            }
            if(Changers.Hover>=cells.Count)Changers.Hover=-1;
        }
        public void SelectLane(int track,int channel)
        {
            main.GetComponent<FeaturedInstrument>().Select(track,channel);
            LeadTrack=track;LeadChannel=channel;Changers.FeaturedTrack=track;Changers.FeaturedChannel=channel;MarkDirtyRepaint();
        }
        public void Load(PreparedPatternSong data)
        {
            source=data;cachedDuration=-1;leadKey=int.MinValue;
            orbits.Clear();Changers.Load(data);Lyrics.Load(data);Graph.Load(data);
            model=MotifWheelModel.For(data);
            if(source?.Sections==null){familyIndex=visitNumber=orbitOf=memberOf=Array.Empty<int>();hatch=Array.Empty<FormHatch.Pattern>();PrepareStructure();return;}
            familyIndex=source.Sections.Select(s=>Array.FindIndex(source.Patterns,p=>p.Family==s.Family)).ToArray();
            visitNumber=source.Sections.Select((s,i)=>source.Sections.Take(i+1).Count(x=>x.Family==s.Family)).ToArray();
            hatch=source.Patterns.Select((p,i)=>FamilyHatch(p,i)).ToArray();
            familyKey=source.Patterns.Select(p=>source.Sections.FirstOrDefault(s=>s.Family==p.Family)?.KeyRoot??-1).ToArray();
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
            PrepareStructure();
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
            using var perf=Perf.Wheels.Auto();
            if(contentRect.width<1||bloom==null)return;
            var p=ctx.painter2D;float w=contentRect.width,h=contentRect.height;
            bloom.Begin(w,h,resolvedStyle.display!=DisplayStyle.None);
            Changers.Cells.Clear();
            if(source?.Sections==null||source.Sections.Length==0||source.Patterns.Length==0||midi.Cycles==null){ctx.DrawText("PREPARE PATTERN WHEELS OFFLINE",new Vector2(20,45),12,Color.gray);bloom.End();return;}
            Cache();CacheLead();
            // Lyric mode: the lyric graph and a small vocal wheel take the panel (the reader rides
            // the drum wheel in the scene above).
            bool lyricView=main.GetComponent<VisualizationViews>()?.Current==VisualizationViews.View.Lyrics;
            lyricFocus=Main.ReducedMotion?(lyricView?1:0):Mathf.MoveTowards(lyricFocus,lyricView?1:0,Time.unscaledDeltaTime*2.5f);
            LyricLayout=lyricView;rackInput.style.display=lyricView?DisplayStyle.None:DisplayStyle.Flex;
            if(lyricView){DrawLyricMode(ctx,p,w,h);bloom.End();return;}
            // Geometry: the ring meshes with the rack at nine o'clock. The caption goes below the
            // ring in a tall panel and beside it in a wide one, whichever leaves the larger ring.
            // The instrument changers take a bay along the bottom.
            var geometry=Layout(w,h);float rackWidth=RackWidth,bay=geometry.Bay,ring=geometry.Ring;bool beside=geometry.Beside;
            var center=geometry.Center;metaCenter=center;
            pixelsPerSong=2*Mathf.PI*ring;
            double duration=Math.Max(1e-6,midi.Duration),now=Math.Clamp(midi.Position/duration,0,1);
            Turns=-now;RackScroll=(float)(now*pixelsPerSong);
            double beat=midi.Cycles.BeatAt(midi.ScorePosition);
            int si=Math.Max(0,Array.FindLastIndex(source.Sections,s=>s.Start<=beat));var section=source.Sections[si];
            int fi=Math.Max(0,familyIndex[si]);var pattern=source.Patterns[fi];
            ActiveNode=section.Node;ActiveGroup=section.Group;
            // The structure wheel (a bundle with motifs, toggle on) draws its own rack and ring 1; the classic wheel the rest.
            if(StructureActive)DrawStructure(ctx,p,center,ring,now,h,beat);
            else
            {
                DrawRack(ctx,p,center,ring,now,si,h);
                DrawRing(ctx,p,center,ring,now,si);
                float planet=ring*.34f;double progress=Math.Clamp((beat-section.Start)/Math.Max(1e-6,section.End-section.Start),0,1);
                DrawOrbits(ctx,p,center,ring,planet,si,fi,progress);
                DrawPlanet(ctx,p,center,planet,section,si,pattern,fi,beat);
                DrawCaption(ctx,p,center,ring,section,si,pattern,fi,beat,beside);
            }
            if(bay>0)
            {
                float bayTop=beside?center.y+ring+16:center.y+ring+128;
                Changers.Draw(ctx,p,bloom,new Rect(rackWidth+8,bayTop,w-rackWidth-12,h-bayTop-6),beat);
            }
            bloom.End();
        }
        void DrawLyricMode(MeshGenerationContext ctx,Painter2D p,float w,float h)
        {
            double beat=midi.Cycles.BeatAt(midi.ScorePosition);
            var area=new Rect(4,40,w-8,h-44);
            if(!Lyrics.HasLyrics)
            {
                ctx.DrawText("LYRIC MODE",new Vector2(90,12),12,Label(.8f));
                ctx.DrawText("No lyric sheet in this bundle. Put a lyrics.txt beside the MIDI\n([Verse 1], [Rap | spoken] stanzas, hy-phen-at-ed syllables)\nand regenerate it with Tools/PatternPrep.",new Vector2(area.x+6,area.y+10),11,Label(.7f));
                Changers.Draw(ctx,p,bloom,new Rect(area.x,area.y+90,area.width,Mathf.Min(260,area.height-90)),beat);
                return;
            }
            Lyrics.Tilt=Mathf.Lerp(.42f,1,Mathf.SmoothStep(0,1,lyricFocus));
            // The graph takes the height, so its words can be large; the vocal wheel keeps a small
            // square at the left (smaller still in a narrow panel, where the graph needs the width).
            bool wide=area.width>area.height*1.6f;Rect wheel,graph;
            float side=wide?Mathf.Min(area.height*.5f,area.width*.2f):Mathf.Min(area.width*.24f,area.height*.34f);
            // The graph spans the panel; it keeps the vocal wheel's square at its top left and puts
            // its vowel wheel beneath it.
            wheel=new Rect(area.x,area.y,side,side);graph=new Rect(area.x,area.y,area.width,area.height-4);
            // The left column holds the vocal wheel above and the vowel colour wheel below it, beside
            // the rhyming lines (the views place the vowel wheel from these fractions of the panel).
            float column=Mathf.Max(side,area.width*.34f);Graph.LeftColumn=column;
            Graph.WheelArea=new Rect((area.x)/w,(area.y+side+6)/h,column/w,Mathf.Max(0,area.height-side-10)/h);
            ctx.DrawText("LYRIC MODE · the words' rhymes and refrains; the reader above",new Vector2(90,12),11,Label(.7f));
            Lyrics.DrawVocal(ctx,p,bloom,wheel,beat);
            Graph.Draw(bloom,graph);
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
            if(!RecordingMode.Active)ctx.DrawText("DRAG TO SEEK",new Vector2(0,h-30),10,Label(.6f));
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
            // Its chords in the colours of its own key: a loop keeps its look when the song modulates.
            int key=familyKey[k]>=0?familyKey[k]:main.currentKey;
            foreach(var chord in pattern.Loop)Band(p,c,r-3.5f-band/2,chord.Start/loop,chord.End/loop,CyclicOrrery.ChordColor(chord,key),band);
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

        // ---------- the structure wheel (Docs/MOTIF-REDUCTION.md §14): a bundle's motif layer ----------
        // The rack and ring 1 (sections) stay in this immediate Draw. Rings 2 and 3 and the glyph are retained children: they turn
        // by style.rotate and are redrawn only when the section, phrase or paradigm changes. A live layer draws what moves inside
        // them (the lit arcs, the glyph's playhead and sparks, a cycling run's gear). After a bundle loads nothing here allocates.
        const float RackWidth=92,Step3=3,LeapSeconds=1.1f,FlashSeconds=1.8f;
        struct Geometry{public Vector2 Center;public float Ring,Bay;public bool Beside;}
        Geometry Layout(float w,float h)
        {
            float top=44,rackWidth=RackWidth;
            float bay=Changers.LaneCount>0?Mathf.Clamp(h*.24f,130,230):0;
            float below=Mathf.Min((w-rackWidth-10)/2,(h-top-124-bay)/2),aside=Mathf.Min((w-rackWidth-240)/2,(h-top-14-bay)/2);
            bool beside=aside>below+6;float ring=Mathf.Max(60,beside?aside:below);
            return new Geometry{Center=new Vector2(rackWidth+4+ring,top+ring+2),Ring=ring,Bay=bay,Beside=beside};
        }
        // Radii as fractions of ring 1's: ring 2 (phrases), ring 3 (units; its inner edge is FeaturedRadius, as the planet's
        // was), the brackets over powers and sub-phrases, the glyph inside.
        static float Ring2Inner(float R)=>R*.6f;static float Ring2Outer(float R)=>R*.74f;
        static float Ring3Inner(float R)=>R*.34f;static float UnitInner(float R)=>R*.34f+7;static float UnitOuter(float R)=>R*.5f;
        static float BracketRadius(float R,int depth)=>Mathf.Min(R*.53f+depth*4,R*.585f);static float Ring3Half(float R)=>R*.6f+3;
        static float GlyphRadius(float R)=>Mathf.Max(8,R*.34f-5);
        public float Ring1Middle=>structRing-8.5f;public float Ring2Middle=>(Ring2Inner(structRing)+Ring2Outer(structRing))/2;

        public bool StructureEnabled=true;
        public bool StructureActive{get;private set;}
        public bool StructureAvailable=>StructureEnabled&&model!=null&&model.Valid&&source?.Sections!=null&&source.Sections.Length>0;
        MotifWheelModel model;public MotifWheelModel Model=>model;
        public MotifWheelModel.Path StructurePath=MotifWheelModel.Path.None;
        public int LitSection=-1,LitPhrase=-1,LitUnit=-1,LitCell=-1,SparksDrawn,AlteredSparksDrawn,GhostLeaps;public bool OmissionLit;
        readonly VisualElement structure;Layer ring2,ring3,face,glyph,live;VisualElement picker,captionBox;VisualizationViews views;
        readonly UnityEngine.UIElements.Label[] captionLines=new UnityEngine.UIElements.Label[5],ring2Labels=new UnityEngine.UIElements.Label[12],ring3Labels=new UnityEngine.UIElements.Label[12];
        UnityEngine.UIElements.Label octave,hover;int ring2Shown,ring3Shown;float ring2Counter=float.NaN,ring3Counter=float.NaN;
        public VisualElement Ring2=>ring2;public VisualElement Ring3=>ring3;public VisualElement GlyphElement=>glyph;public VisualElement Picker=>picker;
        readonly List<MotifWheelModel.RingItem> ring3Items=new(256);readonly List<int> ring3Glyph=new(256);
        int ring2Section=-1,ring3Phrase=-1,ring3Stream=-2,ring3Key=int.MinValue,glyphParadigm=-1,glyphClock=-1,glyphMirror;
        float ring2Angle=float.NaN,ring3Angle=float.NaN,glyphAngle=float.NaN;
        Vector2 structCenter;float structRing=-1,structWidth=-1;bool structBeside;
        // Per bundle: hatches, caption strings, which way an alteration spark leans, the key names.
        FormHatch.Pattern[] nodeHatch=Array.Empty<FormHatch.Pattern>();string[] nodeLine=Array.Empty<string>(),paradigmLine=Array.Empty<string>(),keyRack=Array.Empty<string>(),keyRing=Array.Empty<string>();
        int[] sparkSign=Array.Empty<int>();string formulaLine="",compressionLine="",formTitle="";bool captionDirty=true;
        // Ring 1's song fractions (audio time, as on the classic ring), per bundle and duration.
        double[] nodeStartU=Array.Empty<double>(),nodeEndU=Array.Empty<double>(),omissionFromU=Array.Empty<double>(),omissionToU=Array.Empty<double>(),omissionAtU=Array.Empty<double>(),returnU=Array.Empty<double>();
        int[] returns1=Array.Empty<int>();MotifWheelModel structModel;double structDuration=-1;
        // What moves this frame: worked out in TickStructure, drawn by Draw (ring 1, bloom) and the live layer.
        bool ghostOn,beadOn,litPhraseOn,litUnitOn;Vector2 ghostAt,bead;double ghostSource=double.NaN,ghostBeat,leapFrom,leapTo,litPhraseFrom,litPhraseTo,litUnitFrom,litUnitTo;
        int ghostRevision=-1;float leapAge=-1,litUnitInner,litUnitOuter;
        readonly Vector2[] sparkAt=new Vector2[8],sparkTail=new Vector2[8],gear=new Vector2[12];readonly int[] sparkKind=new int[8];readonly bool[] sparkLit=new bool[8];int sparkCount,gearCount;

        sealed class Layer:VisualElement
        {
            public Layer(string name,Action<MeshGenerationContext> draw,bool turning)
            {
                this.name=name;pickingMode=PickingMode.Ignore;style.position=Position.Absolute;
                if(turning)usageHints=UsageHints.DynamicTransform;generateVisualContent+=draw;
            }
        }
        VisualElement BuildStructure()
        {
            var root=new VisualElement{name="structure-wheel",pickingMode=PickingMode.Ignore};root.style.position=Position.Absolute;
            root.style.left=root.style.top=root.style.right=root.style.bottom=0;root.style.display=DisplayStyle.None;
            ring3=new Layer("structure-ring-3",DrawRing3,true);ring2=new Layer("structure-ring-2",DrawRing2,true);face=new Layer("structure-clock",DrawFace,false);
            glyph=new Layer("structure-glyph",DrawGlyph,true);live=new Layer("structure-live",DrawLive,false);live.style.left=live.style.top=live.style.right=live.style.bottom=0;
            root.Add(ring3);root.Add(ring2);root.Add(face);root.Add(glyph);root.Add(live);
            for(int i=0;i<ring2Labels.Length;i++){ring2Labels[i]=RingLabel();ring2.Add(ring2Labels[i]);ring3Labels[i]=RingLabel();ring3.Add(ring3Labels[i]);}
            captionBox=new VisualElement{name="structure-caption",pickingMode=PickingMode.Ignore};captionBox.style.position=Position.Absolute;captionBox.style.overflow=Overflow.Hidden;
            float[] sizes={12,11,11,10,10},alphas={.95f,.85f,.9f,.75f,.6f};
            for(int i=0;i<captionLines.Length;i++)
            {
                var line=new UnityEngine.UIElements.Label{pickingMode=PickingMode.Ignore,enableRichText=false};line.style.fontSize=sizes[i];line.style.color=Label(alphas[i]);
                line.style.whiteSpace=WhiteSpace.Normal;line.style.marginLeft=line.style.marginRight=line.style.marginTop=0;line.style.marginBottom=i==0?5:2;
                line.style.paddingLeft=line.style.paddingRight=line.style.paddingTop=line.style.paddingBottom=0;captionBox.Add(line);captionLines[i]=line;
            }
            root.Add(captionBox);
            octave=new UnityEngine.UIElements.Label{name="structure-octave",pickingMode=PickingMode.Ignore};octave.style.position=Position.Absolute;octave.style.fontSize=10;octave.style.color=Label(.85f);root.Add(octave);
            picker=new VisualElement{name="structure-picker",pickingMode=PickingMode.Position};picker.style.position=Position.Absolute;root.Add(picker);
            picker.RegisterCallback<PointerDownEvent>(OnPick);picker.RegisterCallback<PointerMoveEvent>(OnHover);picker.RegisterCallback<PointerLeaveEvent>(_=>hover.style.display=DisplayStyle.None);
            hover=new UnityEngine.UIElements.Label{name="structure-hover",pickingMode=PickingMode.Ignore,enableRichText=false};hover.style.position=Position.Absolute;hover.style.maxWidth=280;
            hover.style.whiteSpace=WhiteSpace.Normal;hover.style.fontSize=11;hover.style.color=Label(.95f);hover.style.backgroundColor=new Color(.02f,.04f,.06f,.94f);
            hover.style.paddingLeft=hover.style.paddingRight=6;hover.style.paddingTop=hover.style.paddingBottom=3;
            hover.style.borderTopLeftRadius=hover.style.borderTopRightRadius=hover.style.borderBottomLeftRadius=hover.style.borderBottomRightRadius=5;hover.style.display=DisplayStyle.None;root.Add(hover);
            return root;
        }
        // A ring's label: it rides its arc and is turned back by the ring's angle, so it stays upright.
        static UnityEngine.UIElements.Label RingLabel()
        {
            var label=new UnityEngine.UIElements.Label{pickingMode=PickingMode.Ignore,enableRichText=false,usageHints=UsageHints.DynamicTransform};
            label.style.position=Position.Absolute;label.style.fontSize=10;label.style.color=Label(.95f);label.style.backgroundColor=new Color(.01f,.016f,.017f,.82f);
            label.style.paddingLeft=label.style.paddingRight=3;label.style.paddingTop=label.style.paddingBottom=0;label.style.marginLeft=label.style.marginTop=label.style.marginRight=label.style.marginBottom=0;
            label.style.borderTopLeftRadius=label.style.borderTopRightRadius=label.style.borderBottomLeftRadius=label.style.borderBottomRightRadius=3;
            label.style.translate=new Translate(Length.Percent(-50),Length.Percent(-50));label.style.display=DisplayStyle.None;return label;
        }
        public FormHatch.Pattern HatchOf(int node)=>node>=0&&node<nodeHatch.Length?nodeHatch[node]:FormHatch.Pattern.Diagonal;
        public int BarOf(double beat)
        {
            var bars=source?.Measures;if(bars==null||bars.Length==0)return 0;int lo=0,hi=bars.Length-1;
            while(lo<hi){int mid=(lo+hi+1)>>1;if(bars[mid].Start<=beat+1e-6)lo=mid;else hi=mid-1;}
            return lo;
        }
        void PrepareStructure()
        {
            structModel=null;structDuration=-1;structRing=-1;ring2Section=ring3Phrase=glyphParadigm=glyphClock=-1;ring3Stream=-2;ring3Key=int.MinValue;glyphMirror=0;
            ring2Angle=ring3Angle=glyphAngle=float.NaN;StructurePath=MotifWheelModel.Path.None;captionDirty=true;ghostSource=double.NaN;leapAge=-1;
            if(model==null||!model.Valid||source?.Sections==null){nodeHatch=Array.Empty<FormHatch.Pattern>();nodeLine=paradigmLine=keyRack=keyRing=Array.Empty<string>();sparkSign=Array.Empty<int>();return;}
            var nodes=model.Nodes;int n=nodes.Length;nodeHatch=new FormHatch.Pattern[n];nodeLine=new string[n];
            // Pop and reviewed forms keep data.Sections as level 1: their rings keep the classic family hatches.
            bool same=model.Sections.Length==source.Sections.Length;
            for(int k=0;same&&k<model.Sections.Length;k++)same=Math.Abs(nodes[model.Sections[k]].Start-source.Sections[k].Start)<1e-6;
            for(int i=0;i<n;i++)
            {
                var x=nodes[i];string role=x.Kind==PreparedPatternSong.MotifKinds.Coda?"coda":x.Kind==PreparedPatternSong.MotifKinds.Intro?"intro":"";
                int k=same&&x.Level==1?Array.IndexOf(model.Sections,i):-1;
                nodeHatch[i]=k>=0&&k<familyIndex.Length&&familyIndex[k]>=0?hatch[familyIndex[k]]:FormHatch.ForRole(role,model.IdentityIndex[i]);
                nodeLine[i]=string.IsNullOrEmpty(x.Text)?x.Label:x.Label+" = "+Spaced(x.Text);
            }
            var atoms=model.Layer.Atoms??Array.Empty<PreparedPatternSong.MotifAtom>();paradigmLine=new string[model.Paradigms.Length];
            for(int i=0;i<paradigmLine.Length;i++)
            {
                var q=model.Paradigms[i];var atom=q.Atom>=0&&q.Atom<atoms.Length?atoms[q.Atom]:null;string sign=q.Sign>0?"⁺":q.Sign<0?"⁻":"";
                paradigmLine[i]=atom!=null?$"{atom.Kind}{sign} {q.Name} {q.Letter}":$"{q.Name} {q.Letter}";
            }
            // An alteration spark sits a quarter tone off its scale degree: below for a lowered note (♭6), above for a raised one.
            var sparks=model.Layer.Sparks??Array.Empty<PreparedPatternSong.MotifSpark>();sparkSign=new int[sparks.Length];
            for(int i=0;i<sparks.Length;i++){string tag=sparks[i].Tag??"";sparkSign[i]=tag.IndexOf('♭')>=0||tag.IndexOf("lowered",StringComparison.Ordinal)>=0?-1:1;}
            string formula=model.Layer.Formula??"";int cut=formula.IndexOf('\n');formulaLine=Spaced(cut<0?formula:formula.Substring(0,cut));
            string caption=model.Layer.Caption??"";int notes=caption.IndexOf(" notes → ",StringComparison.Ordinal),from=notes<0?-1:caption.LastIndexOf(" · ",notes,StringComparison.Ordinal);
            compressionLine=from<0?caption:caption.Substring(from+3);
            formTitle=string.IsNullOrEmpty(source.FormName)?"":source.FormName.Split(new[]{" · "},StringSplitOptions.None)[0];
            var keys=source.KeyChanges??Array.Empty<PreparedPatternSong.KeyChange>();keyRack=new string[keys.Length];keyRing=new string[keys.Length];
            for(int k=0;k<keys.Length;k++){keyRack[k]=$"→ {KeyName(keys[k].Key,keys[k].Minor)}";keyRing[k]=KeyName(keys[k].Key,keys[k].Minor,true);}
        }
        // The formula's gaps between its parts ("A · B · A′    A′ = A − …") survive a label's whitespace collapsing.
        static string Spaced(string text)=>System.Text.RegularExpressions.Regex.Replace(text??""," {2,}"," \u00A0\u00A0 ");
        double SongU(double beat)=>midi.AudioTime(midi.Cycles.SecondsAt(beat))/Math.Max(1e-6,midi.Duration);
        double SongNow=>Math.Clamp(midi.Position/Math.Max(1e-6,midi.Duration),0,1);
        public double Ring1Phase(double beat)=>Wrap01(.75-(SongU(beat)-SongNow));
        public Vector2 RingPoint(float radius,double phase)=>At(structCenter,radius,phase);
        static double Wrap01(double x)=>x-Math.Floor(x);
        void CacheStructure()
        {
            if(structModel==model&&Math.Abs(structDuration-midi.Duration)<1e-6)return;
            structModel=model;structDuration=midi.Duration;var nodes=model.Nodes;nodeStartU=new double[nodes.Length];nodeEndU=new double[nodes.Length];
            foreach(int s in model.Sections)
            {
                nodeStartU[s]=SongU(nodes[s].Start);nodeEndU[s]=SongU(nodes[s].End);
                for(int k=0;k<nodes[s].ChildCount;k++){int c=nodes[s].FirstChild+k;nodeStartU[c]=SongU(nodes[c].Start);nodeEndU[c]=SongU(nodes[c].End);}
            }
            var om=model.Omissions;omissionFromU=new double[om.Length];omissionToU=new double[om.Length];omissionAtU=new double[om.Length];
            for(int i=0;i<om.Length;i++){omissionFromU[i]=SongU(om[i].SourceStart);omissionToU[i]=SongU(om[i].SourceEnd);omissionAtU[i]=SongU(om[i].TargetAt);}
            var list=new List<int>();for(int r=0;r<model.Relations.Length;r++)if(model.ReturnLabel[r].Length>0)list.Add(r);returns1=list.ToArray();returnU=new double[4*returns1.Length];
            for(int i=0;i<returns1.Length;i++){var r=model.Relations[returns1[i]];returnU[4*i]=SongU(r.Start);returnU[4*i+1]=SongU(r.End);returnU[4*i+2]=SongU(r.SourceStart);returnU[4*i+3]=SongU(r.SourceEnd);}
        }
        void PlaceStructure(Geometry geometry,float w)
        {
            structCenter=geometry.Center;structRing=geometry.Ring;structBeside=geometry.Beside;structWidth=w;float R=structRing;
            void Square(VisualElement e,float half){e.style.left=structCenter.x-half;e.style.top=structCenter.y-half;e.style.width=e.style.height=2*half;}
            Square(ring2,Ring2Outer(R)+4);Square(ring3,Ring3Half(R));Square(face,GlyphRadius(R)+3);Square(glyph,GlyphRadius(R)+3);Square(picker,R+6);
            // The caption keeps the classic one's place: beside the ring in a wide panel, below it in a tall one, never past the panel.
            float x=structBeside?structCenter.x+R+18:structCenter.x-R+6,y=structBeside?structCenter.y-R+8:structCenter.y+R+12;
            captionBox.style.left=x;captionBox.style.top=y;captionBox.style.width=Mathf.Max(60,w-x-8);captionBox.style.maxHeight=structBeside?Mathf.Max(60,2*R-16):116;
            octave.style.left=structCenter.x+GlyphRadius(R)*.6f;octave.style.top=structCenter.y-GlyphRadius(R)-4;
            ring2Section=ring3Phrase=glyphParadigm=glyphClock=-1;ring2Angle=ring3Angle=glyphAngle=float.NaN;captionDirty=true;
        }

        // Every frame: the path, what to redraw (only what changed), the rings' turn, the caption when the path moved on.
        public void TickStructure()
        {
            using var perf=Perf.WheelsStructure.Auto();
            views??=main.GetComponent<VisualizationViews>();
            bool lyricView=views!=null&&views.Current==VisualizationViews.View.Lyrics;float w=contentRect.width,h=contentRect.height;
            bool on=StructureAvailable&&!lyricView&&midi.Cycles!=null&&source.Patterns.Length>0&&w>=1&&h>=1&&bloom!=null;
            if(on!=StructureActive)
            {
                StructureActive=on;structure.style.display=on?DisplayStyle.Flex:DisplayStyle.None;structRing=-1;
                if(!on){LitSection=LitPhrase=LitUnit=LitCell=-1;SparksDrawn=AlteredSparksDrawn=0;ghostOn=beadOn=false;hover.style.display=DisplayStyle.None;}
            }
            // Hidden (another view has the screen, or no song yet): nothing to turn until it shows again.
            if(!on||resolvedStyle.visibility==Visibility.Hidden)return;
            var geometry=Layout(w,h);if(geometry.Ring!=structRing||geometry.Center!=structCenter||geometry.Beside!=structBeside||w!=structWidth)PlaceStructure(geometry,w);
            CacheStructure();
            double beat=midi.Cycles.BeatAt(midi.ScorePosition),now=SongNow;float R=structRing;
            model.Resolve(beat,LeadTrack,LeadChannel,ref StructurePath);var path=StructurePath;
            if(path.Section!=ring2Section){ring2Section=path.Section;LabelRing2();ring2.MarkDirtyRepaint();}
            if(path.Phrase!=ring3Phrase||path.Stream!=ring3Stream||main.currentKey!=ring3Key){ring3Phrase=path.Phrase;ring3Stream=path.Stream;ring3Key=main.currentKey;LayoutRing3();ring3.MarkDirtyRepaint();}
            if(path.Paradigm!=glyphParadigm)
            {
                glyphParadigm=path.Paradigm;glyph.MarkDirtyRepaint();
                int clock=path.Cell>=0?model.ClockOf(path.Cell):7;if(clock!=glyphClock){glyphClock=clock;face.MarkDirtyRepaint();}
            }
            // The rings turn so that now sits at the gate (nine o'clock): one turn per section visit, one per phrase.
            Turn(ring2,(float)((.75+path.SectionU)*360),ref ring2Angle,ring2Labels,ring2Shown,ref ring2Counter);
            Turn(ring3,(float)((.75+path.PhraseU)*360),ref ring3Angle,ring3Labels,ring3Shown,ref ring3Counter);
            // The glyph: rotation is transposition (its first note on its scale degree), a mirror is an inversion.
            float angle=path.Cell>=0?model.GlyphAngle(path.Cell):0;int mirror=model.GlyphMirrored(path.Cell)?-1:1;
            if(angle!=glyphAngle){glyphAngle=angle;glyph.style.rotate=new Rotate(Angle.Degrees(angle));}
            if(mirror!=glyphMirror){glyphMirror=mirror;glyph.style.scale=new Scale(new Vector2(mirror,1));}
            if(path.ChangedSinceLast||captionDirty)Caption(path);
            Lit(path,beat,now,R);Dynamics(path,beat,now,R);
            live.MarkDirtyRepaint();
        }
        static void Turn(VisualElement ring,float angle,ref float shown,UnityEngine.UIElements.Label[] labels,int count,ref float counter)
        {
            if(!(Math.Abs(angle-shown)<.01f)){shown=angle;ring.style.rotate=new Rotate(Angle.Degrees(angle));}
            // Its labels are turned back to upright, to within a degree (not every frame on a slow ring); new labels at once.
            if(Math.Abs(angle-counter)<1f)return;counter=angle;var back=new StyleRotate(new Rotate(Angle.Degrees(-angle)));
            for(int i=0;i<count;i++)labels[i].style.rotate=back;
        }
        void LabelRing2()
        {
            ring2Shown=0;ring2Counter=float.NaN;int s=ring2Section;float R=structRing;
            // A small wheel (a short panel) has no room for its rings' labels: the caption names what plays.
            if(s>=0&&R>=90)
            {
                var sec=model.Nodes[s];double len=Math.Max(1e-6,sec.End-sec.Start);float mid=(Ring2Inner(R)+Ring2Outer(R))/2,half=Ring2Outer(R)+4;
                for(int k=0;k<sec.ChildCount&&ring2Shown<ring2Labels.Length;k++)
                {
                    int n=sec.FirstChild+k;var x=model.Nodes[n];double u0=(x.Start-sec.Start)/len,u1=(x.End-sec.Start)/len;
                    if((u1-u0)*2*Math.PI*mid<24)continue;
                    // A period's label sits a quarter of the way in: its generator's sign takes the middle.
                    double at=model.PeriodOrder[n]>=0?u0+(u1-u0)*.25:(u0+u1)/2;var point=At(new Vector2(half,half),mid,-at);var label=ring2Labels[ring2Shown++];
                    label.text=x.Label;label.style.left=point.x;label.style.top=point.y;label.style.display=DisplayStyle.Flex;
                }
            }
            for(int i=ring2Shown;i<ring2Labels.Length;i++)ring2Labels[i].style.display=DisplayStyle.None;
        }
        void LayoutRing3()
        {
            ring3Items.Clear();ring3Glyph.Clear();ring3Shown=0;ring3Counter=float.NaN;int ph=ring3Phrase;float R=structRing;
            if(ph>=0&&R>0)
            {
                model.PhraseItems(ph,ring3Stream,ring3Items);var x=model.Nodes[ph];double len=Math.Max(1e-6,x.End-x.Start);float half=Ring3Half(R);
                for(int i=0;i<ring3Items.Count;i++)
                {
                    var item=ring3Items[i];
                    // A unit shows the motif its stream states at its start; a cell its own.
                    ring3Glyph.Add(item.Cell>=0?item.Cell:item.Bracket?-1:model.CellAt(ring3Stream,item.Start+1e-6));
                    if(!item.Bracket||ring3Shown>=ring3Labels.Length||R<120)continue;
                    double u0=(item.Start-x.Start)/len,u1=(item.End-x.Start)/len;float r=BracketRadius(R,item.Depth);
                    if((u1-u0)*2*Math.PI*r<24)continue;
                    var point=At(new Vector2(half,half),r,-(u0+u1)/2);var label=ring3Labels[ring3Shown++];
                    label.text=model.Nodes[item.Node].Label;label.style.left=point.x;label.style.top=point.y;label.style.display=DisplayStyle.Flex;
                }
            }
            for(int i=ring3Shown;i<ring3Labels.Length;i++)ring3Labels[i].style.display=DisplayStyle.None;
        }
        // The caption's four lines: the piece's formula, the phrase's (or section's) line, the playing cell (two labels: its atom
        // and paradigm, then how it relates to its last statement), the compression. Strings are the bundle's or made at load.
        void Caption(MotifWheelModel.Path path)
        {
            captionDirty=false;int node=path.Phrase>=0?path.Phrase:path.Section,cell=path.Cell;var x=cell>=0?model.Cells[cell]:null;
            captionLines[0].text=formulaLine;captionLines[1].text=node>=0?nodeLine[node]:"";
            captionLines[2].text=x!=null&&x.Paradigm>=0&&x.Paradigm<paradigmLine.Length?paradigmLine[x.Paradigm]:"";
            captionLines[3].text=x==null?"":string.IsNullOrEmpty(x.Text)?"its first statement":x.Text;captionLines[4].text=compressionLine;
            int octaves=x==null?0:model.ClockOf(cell)==12?x.Semitones/12:x.Steps/7;octave.text=octaves<0?"8vb":octaves>0?"8va":"";
        }
        float NodeInk(int n)=>n==LitSection?.8f:LitSection>=0&&model.Identity[n]==model.Identity[LitSection]?.5f:.22f;
        // The lit arcs: on each ring the arc under the gate (ring 1 by song time, as it turns; rings 2 and 3 by their own turn).
        void Lit(MotifWheelModel.Path path,double beat,double now,float R)
        {
            LitSection=-1;foreach(int s in model.Sections)if(nodeStartU[s]<=now&&now<nodeEndU[s]){LitSection=s;break;}
            LitPhrase=-1;litPhraseOn=false;
            if(ring2Section>=0)
            {
                var sec=model.Nodes[ring2Section];double len=Math.Max(1e-6,sec.End-sec.Start),u=path.SectionU;
                for(int k=0;k<sec.ChildCount;k++)
                {
                    var x=model.Nodes[sec.FirstChild+k];double u0=(x.Start-sec.Start)/len,u1=(x.End-sec.Start)/len;
                    if(u0<=u&&u<u1){LitPhrase=sec.FirstChild+k;litPhraseOn=true;litPhraseFrom=.75-(u1-u);litPhraseTo=.75-(u0-u);break;}
                }
            }
            LitUnit=LitCell=-1;litUnitOn=false;
            if(ring3Phrase>=0)
            {
                // The deepest arc there: a unit or a cell, else the innermost bracket.
                var x=model.Nodes[ring3Phrase];double len=Math.Max(1e-6,x.End-x.Start),u=path.PhraseU;int best=-1,score=-1;
                for(int i=0;i<ring3Items.Count;i++){var item=ring3Items[i];if(beat<item.Start||beat>=item.End)continue;int s=(item.Bracket?0:100)+item.Depth;if(s>score){score=s;best=i;}}
                if(best>=0)
                {
                    var item=ring3Items[best];LitUnit=item.Node;LitCell=item.Cell;double u0=(item.Start-x.Start)/len,u1=(item.End-x.Start)/len;
                    litUnitOn=true;litUnitFrom=.75-(u1-u);litUnitTo=.75-(u0-u);
                    if(item.Bracket)litUnitInner=litUnitOuter=BracketRadius(R,item.Depth);
                    else{float o=Mathf.Clamp(item.Offset*Step3,-R*.05f,R*.05f);litUnitInner=UnitInner(R)+o;litUnitOuter=UnitOuter(R)+o;}
                }
            }
        }
        void Dynamics(MotifWheelModel.Path path,double beat,double now,float R)
        {
            float rg=GlyphRadius(R);var c=structCenter;
            // The playhead travels the glyph's spiral; the cell's sparks sit at the notes they change.
            var g=model.Glyph(path.Paradigm);beadOn=g!=null&&path.Cell>=0;sparkCount=0;AlteredSparksDrawn=0;
            if(beadOn)
            {
                var cell=model.Cells[path.Cell];bool mirror=cell.Inverted;float angle=glyphAngle;bead=c+Turn(g.PointAt((float)path.CellU),angle,mirror)*rg;
                var sparks=model.Layer.Sparks;int first=cell.FirstSpark,count=Math.Min(sparkAt.Length,cell.SparkCount);
                for(int i=0;i<count&&first>=0&&first+i<sparks.Length;i++)
                {
                    var spark=sparks[first+i];int note=spark.Note;Vector2 at;
                    // At its note, in the clock face's frame (clockwise is up), an alteration a quarter tone off its degree.
                    if(note>=0&&note<g.Beads.Length)at=At(c,g.Beads[note].magnitude*rg,((mirror?-g.Angles[note]:g.Angles[note])+angle+(spark.Kind==PreparedPatternSong.MotifSparks.Altered?15f*sparkSign[first+i]:0))/360.0);
                    else{double length=cell.End-cell.Start;at=c+Turn(g.PointAt(length>0?(float)((spark.Beat-cell.Start)/length):0),angle,mirror)*rg;}
                    sparkAt[sparkCount]=at;sparkKind[sparkCount]=spark.Kind;sparkLit[sparkCount]=beat>=spark.Beat-1e-6;
                    sparkTail[sparkCount]=spark.Kind==PreparedPatternSong.MotifSparks.Lengthened?at+(at-c).normalized*6:at;
                    if(spark.Kind==PreparedPatternSong.MotifSparks.Altered)AlteredSparksDrawn++;
                    sparkCount++;
                }
            }
            SparksDrawn=sparkCount;
            // A cycling run turns its hand shape by its step at each event: the tritone's diameter closes after six.
            gearCount=0;int run=model.RunAt(beat);
            if(run>=0)
            {
                var r=model.Relations[run];var symmetry=r.Symmetry;int events=Math.Max(1,r.Count);double unit=(r.End-r.Start)/events;
                int k=Math.Clamp((int)Math.Floor((beat-r.Start)/Math.Max(1e-6,unit)),0,events-1);float turn=(r.Real||r.Steps==0?30f*r.Semitones:360f/7*r.Steps)*k;
                for(int i=0;i<symmetry.Length&&gearCount<gear.Length;i++)gear[gearCount++]=At(c,rg*.55f,(turn+30f*symmetry[i])/360.0);
                if(gearCount<2)gearCount=0;
            }
            // The ghost cursor: inside a reprise, where the music is in what it restates. A jump across an omission (not a seek)
            // is a leap, drawn across the slit's bridge.
            ghostOn=model.Ghost(beat,out double from,out _,out _);
            if(ghostOn)
            {
                ghostAt=At(c,Ring1Middle,.75-(SongU(from)-now));
                if(!double.IsNaN(ghostSource)&&midi.SeekRevision==ghostRevision&&from-ghostSource>beat-ghostBeat+1){leapFrom=ghostSource;leapTo=from;leapAge=0;GhostLeaps++;}
                ghostSource=from;ghostBeat=beat;ghostRevision=midi.SeekRevision;
            }
            else ghostSource=double.NaN;
            if(leapAge>=0){leapAge+=RecordingMode.Step;if(leapAge>FlashSeconds)leapAge=-1;}
        }

        // ---------- drawing ----------
        void DrawStructure(MeshGenerationContext ctx,Painter2D p,Vector2 center,float ring,double now,float h,double beat)
        {
            featuredCenter=center;FeaturedRadius=Ring3Inner(ring);CacheStructure();
            DrawRackStructure(ctx,p,center,ring,now,h,beat);
            DrawRingStructure(ctx,p,center,ring,now,beat);
            ctx.DrawText(formTitle,new Vector2(84,12),12,Label(.75f));
            using var perf=Perf.WheelsStructure.Auto();
            // The path: a thin radial line from the gate joins the lit arcs to the glyph.
            Line(p,At(center,ring-14,Gate),At(center,GlyphRadius(ring)+1,Gate),Ink(.3f),1);
            if(litPhraseOn)bloom.Arc(center,(Ring2Inner(ring)+Ring2Outer(ring))/2,litPhraseFrom,litPhraseTo,Ink(1),.1f);
            if(litUnitOn)bloom.Arc(center,(litUnitInner+litUnitOuter)/2,litUnitFrom,litUnitTo,Ink(1),.12f);
            if(beadOn)bloom.Disk(bead,2.5f,Color.white,midi.IsPlaying?.6f:.25f);
            for(int i=0;i<sparkCount;i++)if(sparkLit[i])bloom.Disk(sparkAt[i],2.5f,Color.white,midi.IsPlaying?.8f:.3f);
        }
        // The rack: level-1 strips (a reprise in its source's hatch), phrase ticks, return brackets on the left edge.
        void DrawRackStructure(MeshGenerationContext ctx,Painter2D p,Vector2 center,float ring,double now,float h,double beat)
        {
            float rackX=center.x-ring-7,top=40,bottom=h-44;
            float Y(double u)=>center.y+(float)((u-now)*pixelsPerSong);
            Line(p,new Vector2(rackX,top),new Vector2(rackX,bottom),Metal,2);
            double spacing=barU.Length>1?(barU[^1]-barU[0])/(barU.Length-1)*pixelsPerSong:10;int step=Math.Max(1,(int)Math.Ceiling(4/Math.Max(.1,spacing)));
            p.strokeColor=Metal;p.lineWidth=1;p.BeginPath();
            for(int b=0;b<barU.Length;b+=step){float y=Y(barU[b]);if(y<top||y>bottom)continue;p.MoveTo(new Vector2(rackX,y));p.LineTo(new Vector2(rackX+5,y));}
            p.Stroke();
            float x0=rackX-13,x1=rackX-3;var nodes=model.Nodes;
            foreach(int s in model.Sections)
            {
                float y0=Mathf.Max(top,Y(nodeStartU[s]))+1,y1=Mathf.Min(bottom,Y(nodeEndU[s]))-1;if(y1<=y0)continue;float ink=NodeInk(s);
                p.fillColor=Shadow;p.BeginPath();p.MoveTo(new Vector2(x0,y0));p.LineTo(new Vector2(x1,y0));p.LineTo(new Vector2(x1,y1));p.LineTo(new Vector2(x0,y1));p.ClosePath();p.Fill();
                FormHatch.Draw(p,nodeHatch[s],y0,y1,x0,x1,6,(along,across)=>new Vector2(across,along),Ink(ink),.8f);
                if(model.Recurs[s]){Line(p,new Vector2(x0,y0),new Vector2(x0,y1),Ink(ink*.75f),.8f);Line(p,new Vector2(x1,y0),new Vector2(x1,y1),Ink(ink*.75f),.8f);}
                else for(float y=y0;y<y1;y+=8){Line(p,new Vector2(x0,y),new Vector2(x0,Mathf.Min(y1,y+4)),Ink(ink*.75f),.8f);Line(p,new Vector2(x1,y),new Vector2(x1,Mathf.Min(y1,y+4)),Ink(ink*.75f),.8f);}
                // Where each phrase or period begins.
                var x=nodes[s];p.strokeColor=Ink(Mathf.Min(1,ink+.25f));p.lineWidth=1;p.BeginPath();
                for(int k=1;k<x.ChildCount;k++){float y=Y(nodeStartU[x.FirstChild+k]);if(y<y0||y>y1)continue;p.MoveTo(new Vector2(x0-3,y));p.LineTo(new Vector2(x1,y));}
                p.Stroke();
                float ly=Y(nodeStartU[s]);if(ly<top||ly>bottom-10)continue;
                ctx.DrawText(x.Label,new Vector2(9,ly+1),s==LitSection?12:10,Label(s==LitSection?1:ink+.15f));
            }
            // Return brackets down the left edge: the span each reprise restates its source in.
            for(int i=0;i<returns1.Length;i++)
            {
                float y0=Mathf.Max(top,Y(returnU[4*i])+2),y1=Mathf.Min(bottom,Y(returnU[4*i+1])-2);if(y1<=y0)continue;
                var r=model.Relations[returns1[i]];var color=Ink(beat>=r.Start&&beat<r.End?.9f:.3f);
                Line(p,new Vector2(3,y0),new Vector2(3,y1),color,1.5f);Line(p,new Vector2(3,y0),new Vector2(7,y0),color);Line(p,new Vector2(3,y1),new Vector2(7,y1),color);
            }
            // Key changes: a diamond on the rack and the new key, as on the classic rack.
            var keys=source.KeyChanges??Array.Empty<PreparedPatternSong.KeyChange>();
            for(int k=0;k<keys.Length&&k<keyU.Length&&k<keyRack.Length;k++)
            {
                float y=Y(keyU[k]);if(y<top||y>bottom)continue;bool passed=keyU[k]<=now+1e-6;
                var d=new Vector2(rackX,y);p.fillColor=Label(passed?.95f:.6f);p.BeginPath();p.MoveTo(d+new Vector2(0,-5));p.LineTo(d+new Vector2(4,0));p.LineTo(d+new Vector2(0,5));p.LineTo(d+new Vector2(-4,0));p.ClosePath();p.Fill();
                Line(p,new Vector2(x0-2,y),new Vector2(rackX+6,y),Label(passed?.7f:.4f),1);
                ctx.DrawText(keyRack[k],new Vector2(9,y+14),10,Label(passed?.95f:.65f));
            }
            float flash=LitSection>=0?(float)Math.Exp(-Math.Max(0,beat-nodes[LitSection].Start)*4):0;
            Line(p,new Vector2(rackX,center.y),new Vector2(center.x-ring,center.y),Ink(.45f+.55f*flash),1.5f);
            Disc(p,new Vector2(rackX,center.y),3.5f,Label(1));bloom.Disk(new Vector2(rackX,center.y),3,Ink(1),flash);
            if(!RecordingMode.Active)ctx.DrawText("DRAG TO SEEK",new Vector2(0,h-30),10,Label(.6f));
        }
        // Ring 1: the sections wound once around, the omission's slit, the return brackets, the ghost cursor.
        void DrawRingStructure(MeshGenerationContext ctx,Painter2D p,Vector2 center,float ring,double now,double beat)
        {
            double Phase(double u)=>.75-(u-now);
            double spacing=barU.Length>1?(barU[^1]-barU[0])/(barU.Length-1)*pixelsPerSong:10;int step=Math.Max(1,(int)Math.Ceiling(4.5/Math.Max(.1,spacing)));
            p.strokeColor=Metal;p.lineWidth=1;p.BeginPath();
            for(int b=0;b<barU.Length;b+=step){double phase=Phase(barU[b]);p.MoveTo(At(center,ring,phase));p.LineTo(At(center,ring+4,phase));}
            p.Stroke();Circle(p,center,ring,Metal,1.2f);
            float inner=ring-13,outer=ring-4,mid=(inner+outer)/2;
            foreach(int s in model.Sections)
            {
                double a=Phase(nodeEndU[s]),b=Phase(nodeStartU[s]),gap=1.2/(2*Math.PI*outer);float ink=NodeInk(s);
                HatchBand(p,center,inner,outer,a+gap,b-gap,nodeHatch[s],ink,model.Recurs[s]);
                if(s==LitSection)bloom.Arc(center,mid,a+gap,b-gap,Ink(1),.1f);
                // A reprise is named with what it restates: "A′ ← A".
                int r=model.ReturnOf[s];string label=r>=0&&(b-a)*2*Math.PI*inner>=model.ReturnLabel[r].Length*6.5f?model.ReturnLabel[r]:model.Nodes[s].Label;
                if((b-a)*2*Math.PI*inner>=24)Text(ctx,label,At(center,inner-10,(a+b)/2),10,Label(s==LitSection?1:ink+.1f));
            }
            // The omission: the bars a reprise leaves out are a slit in its source's arc, bridged by a chord; the reprise's arc
            // is notched where it jumps them. It is lit from the reprise's start until the music it rejoins has been heard, so it
            // is there before, during and after the skip; the leap across it flashes.
            var om=model.Omissions;float flash=leapAge>=0?Mathf.Clamp01(1-leapAge/FlashSeconds):0;OmissionLit=false;
            for(int i=0;i<om.Length;i++)
            {
                var o=om[i];double a=Phase(omissionToU[i]),b=Phase(omissionFromU[i]);bool on=beat>=o.LitFrom&&beat<o.LitUntil;OmissionLit|=on;
                Band(p,center,mid,a,b,Shadow,outer-inner);
                HatchSector(p,HatchOf(o.Prototype),center,inner+.5f,outer-.5f,a,b,Ink(on?.42f:.16f),Mathf.Clamp((outer-inner)*.7f,4.5f,8),.6f);
                DashedBand(p,center,inner,a,b,Ink(on?.85f:.4f),.8f);DashedBand(p,center,outer,a,b,Ink(on?.85f:.4f),.8f);
                Line(p,At(center,mid,a),At(center,mid,b),Label(on?.95f:.45f),on?1.4f:.8f);
                if(on){bloom.Arc(center,mid,a,b,Ink(1),.12f+.6f*flash);if(flash>0&&(b-a)*2*Math.PI*ring>=40)Text(ctx,"skipped",At(center,ring+17,(a+b)/2),10,Label(.45f+.55f*flash));}
                double t=Phase(omissionAtU[i]),d=3.5/(2*Math.PI*outer);
                p.fillColor=Label(on?.95f:.55f);p.BeginPath();p.MoveTo(At(center,outer+2,t-d));p.LineTo(At(center,outer-5,t));p.LineTo(At(center,outer+2,t+d));p.ClosePath();p.Fill();
            }
            // Return brackets inside the rim, in place of group brackets: over the reprise, and fainter over what it restates.
            for(int i=0;i<returns1.Length;i++)
            {
                var r=model.Relations[returns1[i]];bool playing=beat>=r.Start&&beat<r.End;double gap=2.5/(2*Math.PI*(ring-20));
                Bracket(p,center,ring-20,Phase(returnU[4*i+1])+gap,Phase(returnU[4*i])-gap,Ink(playing?.9f:.45f));
                Bracket(p,center,ring-20,Phase(returnU[4*i+3])+gap,Phase(returnU[4*i+2])-gap,Ink(playing?.55f:.2f));
            }
            // Key changes cross the rim, named outside it.
            var keys=source.KeyChanges??Array.Empty<PreparedPatternSong.KeyChange>();
            for(int k=0;k<keys.Length&&k<keyU.Length&&k<keyRing.Length;k++)
            {
                double phase=Phase(keyU[k]);bool passed=keyU[k]<=now+1e-6;
                Line(p,At(center,inner-2,phase),At(center,ring+5,phase),Label(passed?.85f:.5f),1.2f);
                Text(ctx,keyRing[k],At(center,ring+15,phase),9,Label(passed?.9f:.55f));
            }
            var mark=At(center,mid,Gate);Disc(p,mark,3,Label(1));bloom.Disk(mark,2.5f,Ink(1),midi.IsPlaying?.5f:.2f);
            if(ghostOn){Circle(p,ghostAt,4.5f,Label(.95f),1.4f);Disc(p,ghostAt,1.6f,Label(.95f));bloom.Disk(ghostAt,3.5f,Color.white,midi.IsPlaying?.55f:.25f);}
            if(leapAge>=0&&leapAge<LeapSeconds)
            {
                float t=Mathf.SmoothStep(0,1,leapAge/LeapSeconds);Vector2 from=At(center,mid,Phase(SongU(leapFrom))),to=At(center,mid,Phase(SongU(leapTo))),at=Vector2.Lerp(from,to,t);
                Line(p,from,at,Label(.75f*(1-.5f*t)),1.8f);Disc(p,at,3,Label(1));bloom.Disk(at,3,Color.white,1-.5f*t);
            }
        }
        // Ring 2 (retained): the section's phrases or periods, a full turn for the section. Time runs counterclockwise here,
        // so that turning clockwise brings it to the gate; the section starts at twelve o'clock.
        void DrawRing2(MeshGenerationContext ctx)
        {
            using var perf=Perf.WheelsStructure.Auto();
            int s=ring2Section;float R=structRing;if(model==null||s<0||R<0||s>=model.Nodes.Length)return;
            var p=ctx.painter2D;float half=Ring2Outer(R)+4,inner=Ring2Inner(R),outer=Ring2Outer(R),mid=(inner+outer)/2;var c=new Vector2(half,half);
            var sec=model.Nodes[s];double len=Math.Max(1e-6,sec.End-sec.Start);
            DashedBand(p,c,inner,0,1,Ink(.12f),.6f);DashedBand(p,c,outer,0,1,Ink(.12f),.6f);
            for(int k=0;k<sec.ChildCount;k++)
            {
                int n=sec.FirstChild+k;var x=model.Nodes[n];double u0=(x.Start-sec.Start)/len,u1=(x.End-sec.Start)/len,from=-u1,to=-u0;
                if((u1-u0)*2*Math.PI*mid<3){Line(p,At(c,inner,to),At(c,outer,to),Ink(.5f),1);continue;}
                double gap=1.2/(2*Math.PI*outer);
                HatchBand(p,c,inner,outer,from+gap,to-gap,HatchOf(n),x.Projected?.45f:.6f,model.Recurs[n]);
                // A projected phrase (inherited through the return) carries a thin rule along its inner edge.
                if(x.Projected)Band(p,c,inner+2,from+gap,to-gap,Label(.35f),.8f);
                int order=model.PeriodOrder[n];if(order>=0&&(u1-u0)*2*Math.PI*mid>=16)Sign(p,c,mid,(from+to)/2,order,Mathf.Clamp((outer-inner)*.38f,3,7));
            }
            // Where a reprise jumps the bars it leaves out, a notch (as on its arc on ring 1).
            foreach(var o in model.Omissions)
            {
                if(o.TargetAt<sec.Start||o.TargetAt>sec.End)continue;double t=-(o.TargetAt-sec.Start)/len,d=3.5/(2*Math.PI*outer);
                p.fillColor=Label(.75f);p.BeginPath();p.MoveTo(At(c,outer+3,t-d));p.LineTo(At(c,outer-5,t));p.LineTo(At(c,outer+3,t+d));p.ClosePath();p.Fill();
            }
        }
        // A period's generator at its arc's middle: → translation (toward later), ⇄ involution, · identity, an n-gon for a cycle.
        static void Sign(Painter2D p,Vector2 c,float r,double phase,int order,float size)
        {
            var at=At(c,r,phase);float a=(float)(phase*2*Math.PI);var along=new Vector2(-Mathf.Cos(a),-Mathf.Sin(a));var across=new Vector2(-along.y,along.x);var ink=Label(.9f);
            p.strokeColor=ink;p.fillColor=ink;p.lineWidth=1.3f;
            if(order==1){Disc(p,at,1.8f,ink);return;}
            if(order==0)
            {
                p.BeginPath();p.MoveTo(at-along*size);p.LineTo(at+along*size);p.Stroke();
                p.BeginPath();p.MoveTo(at+along*size);p.LineTo(at+along*(size*.35f)+across*(size*.45f));p.LineTo(at+along*(size*.35f)-across*(size*.45f));p.ClosePath();p.Fill();return;
            }
            if(order==2)
            {
                var up=across*(size*.32f);p.BeginPath();p.MoveTo(at-along*size+up);p.LineTo(at+along*size+up);p.MoveTo(at+along*size-up);p.LineTo(at-along*size-up);p.Stroke();
                p.BeginPath();p.MoveTo(at+along*size+up);p.LineTo(at+along*(size*.45f)+up*2.4f);p.LineTo(at+along*(size*.45f)+up*.2f);p.ClosePath();p.Fill();
                p.BeginPath();p.MoveTo(at-along*size-up);p.LineTo(at-along*(size*.45f)-up*2.4f);p.LineTo(at-along*(size*.45f)-up*.2f);p.ClosePath();p.Fill();return;
            }
            p.BeginPath();for(int i=0;i<=order;i++){float b=a+2*Mathf.PI*i/order;var q=at+new Vector2(Mathf.Sin(b),-Mathf.Cos(b))*size;if(i==0)p.MoveTo(q);else p.LineTo(q);}p.Stroke();
        }
        // Ring 3 (retained): the phrase's units, a full turn for the phrase. Powers are staircases (each iteration stepped out by
        // its cumulative step, in when it falls), powers and sub-phrases are bracketed, cells fill what no unit covers; the chords
        // run along the inner edge. Each arc carries its motif small, oriented as the centre glyph shows it at the gate.
        void DrawRing3(MeshGenerationContext ctx)
        {
            using var perf=Perf.WheelsStructure.Auto();
            int ph=ring3Phrase;float R=structRing;if(model==null||ph<0||R<0||ph>=model.Nodes.Length)return;
            var p=ctx.painter2D;float half=Ring3Half(R);var c=new Vector2(half,half);var x=model.Nodes[ph];double len=Math.Max(1e-6,x.End-x.Start);
            var chords=source.Chords??Array.Empty<SongFormAnalysis.ChordStep>();float band=Ring3Inner(R)+2.5f;
            for(int i=0;i<chords.Length;i++)
            {
                var chord=chords[i];if(chord.End<=x.Start)continue;if(chord.Start>=x.End)break;
                double u0=(Math.Max(chord.Start,x.Start)-x.Start)/len,u1=(Math.Min(chord.End,x.End)-x.Start)/len;Band(p,c,band,-u1,-u0,CyclicOrrery.ChordColor(chord,ring3Key),4);
            }
            float inner=UnitInner(R),outer=UnitOuter(R),room=R*.05f;
            for(int i=0;i<ring3Items.Count;i++)
            {
                var item=ring3Items[i];double u0=(item.Start-x.Start)/len,u1=(item.End-x.Start)/len,from=-u1,to=-u0;
                if(item.Bracket){float r=BracketRadius(R,item.Depth);if((u1-u0)*2*Math.PI*r<3)continue;double gap=2/(2*Math.PI*r);Bracket(p,c,r,from+gap,to-gap,Ink(.6f),-3);continue;}
                float o=Mathf.Clamp(item.Offset*Step3,-room,room),a=inner+o,b=outer+o,px=(float)((u1-u0)*2*Math.PI*(a+b)/2);
                // Arcs under 3 px collapse to a tick: the level of detail the ring can show.
                if(px<3){Line(p,At(c,a,to),At(c,b,to),Ink(.45f),.8f);continue;}
                double g=1/(2*Math.PI*b);bool unit=item.Node>=0;int paradigm=unit?-1:model.Cells[item.Cell].Paradigm;
                HatchBand(p,c,a,b,from+g,to-g,unit?HatchOf(item.Node):FormHatch.ForRole("",paradigm),unit?.6f:.38f,unit?model.Recurs[item.Node]:paradigm>=0&&model.Paradigms[paradigm].Members>1);
                int cell=ring3Glyph[i];float size=Mathf.Min((b-a)*.42f,px*.3f);
                if(cell>=0&&size>=5)MiniGlyph(p,At(c,(a+b)/2,(from+to)/2),size,cell,(from+to)/2);
            }
        }
        void MiniGlyph(Painter2D p,Vector2 at,float size,int cell,double phase)
        {
            var g=model.Glyph(model.Cells[cell].Paradigm);if(g==null||g.Path.Length==0)return;
            float angle=model.GlyphAngle(cell)-(float)((.75-phase)*360);bool mirror=model.GlyphMirrored(cell);
            Circle(p,at,size,Ink(.25f),.6f);
            if(g.Path.Length>1){p.strokeColor=Label(.85f);p.lineWidth=1;p.lineJoin=LineJoin.Round;p.BeginPath();for(int i=0;i<g.Path.Length;i++){var q=at+Turn(g.Path[i],angle,mirror)*size;if(i==0)p.MoveTo(q);else p.LineTo(q);}p.Stroke();}
            Disc(p,at+Turn(g.Path[0],angle,mirror)*size,1.3f,Label(.95f));
        }
        static Vector2 Turn(Vector2 v,float degrees,bool mirror)
        {
            if(mirror)v.x=-v.x;float a=degrees*Mathf.Deg2Rad,cos=Mathf.Cos(a),sin=Mathf.Sin(a);
            return new Vector2(v.x*cos-v.y*sin,v.x*sin+v.y*cos);
        }
        // The clock face (it does not turn): Clock ticks, the tonic's at twelve o'clock brighter.
        void DrawFace(MeshGenerationContext ctx)
        {
            using var perf=Perf.WheelsStructure.Auto();
            if(structRing<0||glyphClock<0)return;var p=ctx.painter2D;float rg=GlyphRadius(structRing),half=rg+3;var c=new Vector2(half,half);
            Disc(p,c,rg,new Color(.01f,.016f,.017f,.8f));Circle(p,c,rg,Metal,1);Circle(p,c,rg*.25f,Ink(.15f),.6f);
            for(int k=0;k<glyphClock;k++){double phase=k/(double)glyphClock;bool tonic=k==0;Line(p,At(c,rg-(tonic?7:4),phase),At(c,rg,phase),tonic?Label(.9f):Ink(.45f),tonic?1.6f:1);}
        }
        // The glyph (retained, turned and mirrored by style): the paradigm's prototype, time spiralling out from a quarter of the
        // radius, a step along the clock, a leap as a chord; structural notes filled, ornaments hollow, a tied note's tail inside.
        void DrawGlyph(MeshGenerationContext ctx)
        {
            using var perf=Perf.WheelsStructure.Auto();
            var g=model?.Glyph(glyphParadigm);if(g==null||structRing<0)return;var p=ctx.painter2D;float rg=GlyphRadius(structRing),half=rg+3;var c=new Vector2(half,half);
            if(g.Tied&&g.Beads.Length>0)Line(p,c+g.Beads[0].normalized*rg*.1f,c+g.Beads[0]*rg,Label(.7f),1.4f);
            if(g.Links.Length>1){p.strokeColor=Ink(.7f);p.lineWidth=1;p.BeginPath();for(int i=0;i+1<g.Links.Length;i+=2){p.MoveTo(c+g.Beads[g.Links[i]]*rg);p.LineTo(c+g.Beads[g.Links[i+1]]*rg);}p.Stroke();}
            if(g.Path.Length>1){p.strokeColor=Label(.9f);p.lineWidth=1.6f;p.lineJoin=LineJoin.Round;p.BeginPath();for(int i=0;i<g.Path.Length;i++){var q=c+g.Path[i]*rg;if(i==0)p.MoveTo(q);else p.LineTo(q);}p.Stroke();}
            for(int i=0;i<g.Beads.Length;i++){var q=c+g.Beads[i]*rg;if(g.Hollow[i])Circle(p,q,2.1f,Label(.85f),1);else Disc(p,q,2.4f,Label(.95f));}
        }
        // The live layer (every frame): the lit arcs of rings 2 and 3 edged in white, a cycling run's gear, the playhead, the sparks.
        void DrawLive(MeshGenerationContext ctx)
        {
            using var perf=Perf.WheelsStructure.Auto();
            if(!StructureActive||structRing<0)return;var p=ctx.painter2D;var c=structCenter;float R=structRing;
            if(litPhraseOn){Band(p,c,Ring2Outer(R)+1,litPhraseFrom,litPhraseTo,Label(.85f),1.2f);Band(p,c,Ring2Inner(R)-1,litPhraseFrom,litPhraseTo,Label(.85f),1.2f);}
            if(litUnitOn)
            {
                if(litUnitInner==litUnitOuter)Band(p,c,litUnitInner,litUnitFrom,litUnitTo,Label(.9f),2);
                else{Band(p,c,litUnitOuter+1,litUnitFrom,litUnitTo,Label(.85f),1.2f);Band(p,c,litUnitInner-1,litUnitFrom,litUnitTo,Label(.85f),1.2f);}
            }
            if(gearCount>=2)
            {
                p.strokeColor=Ink(.8f);p.lineWidth=1.3f;p.BeginPath();p.MoveTo(gear[0]);for(int i=1;i<gearCount;i++)p.LineTo(gear[i]);if(gearCount>2)p.ClosePath();p.Stroke();
                for(int i=0;i<gearCount;i++)Disc(p,gear[i],1.8f,Ink(.9f));
            }
            if(beadOn){Disc(p,bead,3,Color.white);Circle(p,bead,4.5f,Label(.5f),1);}
            for(int i=0;i<sparkCount;i++)
            {
                var at=sparkAt[i];var color=new Color(1,1,1,sparkLit[i]?1:.55f);
                if(sparkTail[i]!=at)Line(p,at,sparkTail[i],color,1.6f);
                if(sparkKind[i]==PreparedPatternSong.MotifSparks.Deleted)Circle(p,at,3,color,1.2f);else Spark(p,at,sparkLit[i]?3.4f:2.6f,color);
            }
        }
        static void Spark(Painter2D p,Vector2 c,float r,Color color)
        {
            p.strokeColor=color;p.lineWidth=1.2f;p.BeginPath();p.MoveTo(c+new Vector2(-r,0));p.LineTo(c+new Vector2(r,0));p.MoveTo(c+new Vector2(0,-r));p.LineTo(c+new Vector2(0,r));p.Stroke();
            Disc(p,c,r*.45f,color);
        }
        static void Bracket(Painter2D p,Vector2 c,float r,double from,double to,Color color,float tick=4)
        {
            if(to-from<1e-5)return;Band(p,c,r,from,to,color,1.2f);Line(p,At(c,r,from),At(c,r+tick,from),color);Line(p,At(c,r,to),At(c,r+tick,to),color);
        }
        // A hatched ring sector outlined solid (it recurs) or dashed (stated once), as FormBand, without its allocations.
        static void HatchBand(Painter2D p,Vector2 c,float inner,float outer,double from,double to,FormHatch.Pattern pattern,float ink,bool recurs)
        {
            if(to-from<1e-5)return;
            Band(p,c,(inner+outer)/2,from,to,Shadow,outer-inner);
            HatchSector(p,pattern,c,inner+.5f,outer-.5f,from,to,Ink(ink),Mathf.Clamp((outer-inner)*.7f,4.5f,8),.8f);
            if(recurs){Band(p,c,inner,from,to,Ink(ink*.75f),.8f);Band(p,c,outer,from,to,Ink(ink*.75f),.8f);}
            else{DashedBand(p,c,inner,from,to,Ink(ink*.75f),.8f);DashedBand(p,c,outer,from,to,Ink(ink*.75f),.8f);}
        }
        static readonly List<(float,float,float,float)> hatchLines=new(256);static readonly List<(float,float)> hatchDots=new(256);
        static void HatchSector(Painter2D p,FormHatch.Pattern pattern,Vector2 c,float inner,float outer,double from,double to,Color ink,float spacing,float width)
        {
            if(to-from<1e-5)return;float mid=(inner+outer)/2,circumference=2*Mathf.PI*mid;
            hatchLines.Clear();hatchDots.Clear();FormHatch.Generate(pattern,(float)from*circumference,(float)to*circumference,inner,outer,spacing,hatchLines,hatchDots);
            p.strokeColor=ink;p.lineWidth=width;p.lineCap=LineCap.Butt;p.BeginPath();
            foreach(var (s0,v0,s1,v1) in hatchLines){p.MoveTo(c+new Vector2(Mathf.Sin(s0/mid),-Mathf.Cos(s0/mid))*v0);p.LineTo(c+new Vector2(Mathf.Sin(s1/mid),-Mathf.Cos(s1/mid))*v1);}
            p.Stroke();if(hatchDots.Count==0)return;p.fillColor=ink;
            foreach(var (s,v) in hatchDots){p.BeginPath();p.Arc(c+new Vector2(Mathf.Sin(s/mid),-Mathf.Cos(s/mid))*v,width*1.1f,Angle.Degrees(0),Angle.Degrees(360));p.Fill();}
        }

        // ---------- interaction ----------
        // Annulus picking: the radius picks the ring, the angle the arc. A click seeks to the arc's start; on the slit, to the bars
        // left out; on the ghost, to where it is in the source ("hear the original"); on the glyph, to the motif's next statement.
        enum Hit{None,Arc,Omission,Ghost,Glyph,Cell}
        Hit Pick(Vector2 local,out int index,out double seek)
        {
            index=-1;seek=0;if(!StructureActive||structRing<0||model==null)return Hit.None;
            var d=local-structCenter;float r=d.magnitude,R=structRing;double phase=Wrap01(Math.Atan2(d.x,-d.y)/(2*Math.PI));
            double beat=midi.Cycles.BeatAt(midi.ScorePosition),now=SongNow;
            if(ghostOn&&(local-ghostAt).magnitude<=8){seek=ghostSource;return Hit.Ghost;}
            if(r<=GlyphRadius(R)+2)
            {
                int cell=StructurePath.Cell;if(cell<0)return Hit.None;int paradigm=model.Cells[cell].Paradigm;var stream=model.Layer.Streams[model.Cells[cell].Stream];int first=-1;
                for(int k=stream.FirstCell;k<stream.FirstCell+stream.CellCount;k++){if(model.Cells[k].Paradigm!=paradigm)continue;if(first<0)first=k;if(model.Cells[k].Start>beat+1e-6){index=k;break;}}
                if(index<0)index=first;if(index<0)return Hit.None;seek=model.Cells[index].Start;return Hit.Glyph;
            }
            if(r>=R-22&&r<=R+5)
            {
                double u=Wrap01(now+.75-phase);var om=model.Omissions;
                for(int i=0;i<om.Length;i++)if(r>=R-14&&omissionFromU[i]<=u&&u<omissionToU[i]){index=i;seek=om[i].SourceStart;return Hit.Omission;}
                foreach(int s in model.Sections)if(nodeStartU[s]<=u&&u<nodeEndU[s]){index=s;seek=model.Nodes[s].Start;return Hit.Arc;}
                return Hit.None;
            }
            if(r>=Ring2Inner(R)-4&&r<=Ring2Outer(R)+4&&ring2Section>=0)
            {
                var sec=model.Nodes[ring2Section];int child=model.ChildAt(ring2Section,sec.Start+Wrap01(StructurePath.SectionU+.75-phase)*(sec.End-sec.Start));
                if(child<0)return Hit.None;index=child;seek=model.Nodes[child].Start;return Hit.Arc;
            }
            if(r>=Ring3Inner(R)&&r<=Ring3Half(R)&&ring3Phrase>=0)
            {
                var x=model.Nodes[ring3Phrase];double at=x.Start+Wrap01(StructurePath.PhraseU+.75-phase)*(x.End-x.Start);bool brackets=r>=BracketRadius(R,0)-2;int best=-1;
                for(int i=0;i<ring3Items.Count;i++){var item=ring3Items[i];if(at<item.Start||at>=item.End||item.Bracket!=brackets)continue;if(best<0||item.Depth>ring3Items[best].Depth)best=i;}
                if(best<0)return Hit.None;var hit=ring3Items[best];
                if(hit.Node>=0){index=hit.Node;seek=model.Nodes[hit.Node].Start;return Hit.Arc;}
                index=hit.Cell;seek=model.Cells[hit.Cell].Start;return Hit.Cell;
            }
            return Hit.None;
        }
        void OnPick(PointerDownEvent e)
        {
            if(e.button!=0)return;var hit=Pick(this.WorldToLocal(e.position),out _,out double seek);if(hit==Hit.None)return;
            ExplorerInputFocus.ClaimUI();midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(seek)));MarkDirtyRepaint();e.StopPropagation();
        }
        void OnHover(PointerMoveEvent e)
        {
            var local=this.WorldToLocal(e.position);var hit=Pick(local,out int index,out _);
            if(hit==Hit.None){hover.style.display=DisplayStyle.None;return;}
            var node=hit==Hit.Arc?model.Nodes[index]:null;var cell=hit==Hit.Cell?model.Cells[index]:null;
            hover.text=hit switch
            {
                Hit.Arc=>string.IsNullOrEmpty(node.Text)?node.Name:node.Name+" · "+node.Text,
                Hit.Omission=>model.Omissions[index].Tooltip,
                Hit.Ghost=>"Where the music is in what this reprise restates: click to hear the original",
                Hit.Glyph=>"The playing motif on its pitch clock: click for its next statement",
                _=>(cell.Paradigm>=0&&cell.Paradigm<paradigmLine.Length?paradigmLine[cell.Paradigm]:"")+(string.IsNullOrEmpty(cell.Text)?"":" · "+cell.Text)
            };
            hover.style.left=Mathf.Clamp(local.x+14,0,Mathf.Max(0,contentRect.width-290));hover.style.top=local.y+14;hover.style.display=DisplayStyle.Flex;
        }
        // A Director anchor (§14.8) as a point here: on the ring that shows it now (ring 3 inside the phrase playing, ring 2 inside
        // the section playing), else on ring 1; the omission on its slit, the glyph at its centre, the ghost where it is.
        public bool TryGetAnchor(string anchor,out Vector2 local)
        {
            local=default;if(!StructureActive||model==null||structRing<0||midi.Cycles==null)return false;
            double beat=midi.Cycles.BeatAt(midi.ScorePosition);if(!model.TryGetAnchor(anchor,beat,StructurePath.Stream,out var t))return false;
            var c=structCenter;float R=structRing;
            switch(t.Kind)
            {
                case MotifWheelModel.AnchorKind.Glyph:local=c;return true;
                case MotifWheelModel.AnchorKind.Ghost:if(!ghostOn)return false;local=ghostAt;return true;
                case MotifWheelModel.AnchorKind.Omission:local=At(c,Ring1Middle,Ring1Phase((t.Start+t.End)/2));return true;
            }
            double mid=(t.Start+t.End)/2;
            if(t.Ring>=3&&ring3Phrase>=0&&Inside(ring3Phrase,t.Start,t.End)){var x=model.Nodes[ring3Phrase];local=At(c,(UnitInner(R)+UnitOuter(R))/2,.75-((mid-x.Start)/Math.Max(1e-6,x.End-x.Start)-StructurePath.PhraseU));return true;}
            if(t.Ring>=2&&ring2Section>=0&&Inside(ring2Section,t.Start,t.End)){var x=model.Nodes[ring2Section];local=At(c,Ring2Middle,.75-((mid-x.Start)/Math.Max(1e-6,x.End-x.Start)-StructurePath.SectionU));return true;}
            local=At(c,Ring1Middle,Ring1Phase(mid));return true;
        }
        bool Inside(int node,double start,double end)=>start>=model.Nodes[node].Start-1e-6&&end<=model.Nodes[node].End+1e-6;
    }
}
