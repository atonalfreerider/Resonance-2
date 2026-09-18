using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

public sealed class CyclicOrrery : VisualElement
{
    readonly Main main;
    readonly MidiPlayer midi;
    readonly Mechanism overview,detail;
    readonly Label status,description,message;
    readonly VisualElement sequence,chords;
    readonly DropdownField family;
    readonly TextField label,boundaries;
    readonly Toggle followToggle;
    SongFormAnalysis source;
    int selectedFamily,occurrence,lastKey=-1;
    bool lastFlats;
    public double SongOrbitPhase=>overview.SongPhase;
    public SongFormAnalysis.Section SelectedSection=>source==null || source.Families.Count==0?null:source.Families[selectedFamily].Occurrences[occurrence];
    public CyclicOrrery(Main owner,MidiPlayer player)
    {
        main=owner;midi=player;name="cyclic-orrery";AddToClassList("orrery");
        status=Text(this,"Load MIDI to reveal song → sections → progressions.");
        var transport=Row(this);
        AddButton(transport,"Play / pause",()=>{if(midi.IsPlaying)midi.Pause();else midi.Play();});
        AddButton(transport,"Stop",midi.Stop);
        overview=new Mechanism(main,true){name="song-mechanism"};Add(overview);
        Text(this,@"SUN: KEY · PLANET: SONG
MOON: SECTION · SMALL MOON: PROGRESSION").AddToClassList("orbit-legend");
        Text(this,"SONG FORM").AddToClassList("eyebrow");
        sequence=Row(this);sequence.name="song-sequence";
        family=new DropdownField("Inspect section",new List<string>{"No MIDI"},0);Add(family);
        family.RegisterValueChangedCallback(_=>SelectFamily(family.index));
        followToggle=new Toggle("Follow the current section"){value=true};Add(followToggle);
        description=Text(this,"");
        detail=new Mechanism(main,false){name="progression-mechanism"};detail.Seek=SeekSection;Add(detail);
        chords=Row(this);chords.name="progression-chords";
        var visits=Row(this);
        AddButton(visits,"◀ Occurrence",()=>Visit(-1));AddButton(visits,"Occurrence ▶",()=>Visit(1));
        AddButton(visits,"Go to section",()=>Visit(0));
        var edit=new Foldout{text="Section names and boundaries",value=false};Add(edit);
        label=new TextField("Section family name");edit.Add(label);
        AddButton(edit,"Rename this family",()=>
        {
            if(SelectedSection==null || string.IsNullOrWhiteSpace(label.value))return;
            SelectedSection.Family.Name=label.value.Trim();RebuildControls();
        });
        var bars=new DropdownField("Estimated section length",new List<string>{"4 bars","8 bars","16 bars","32 bars"},1);edit.Add(bars);
        bars.RegisterValueChangedCallback(_=>Rebuild(4<<bars.index,""));
        boundaries=new TextField("Boundaries: bar number, then name"){multiline=true,name="section-boundaries"};
        boundaries.style.height=150;edit.Add(boundaries);
        AddButton(edit,"Copy current boundaries",()=>
        {if(source!=null)boundaries.value=string.Join("\n",source.Sections.Select(s=>$"{s.FirstBar+1} {s.Family.Name}"));});
        AddButton(edit,"Apply section boundaries",()=>Rebuild(4<<bars.index,boundaries.value));
        AddButton(edit,"Use MIDI markers / estimates",()=>Rebuild(4<<bars.index,""));
        message=Text(edit,@"Example:
1 Intro
5 Verse
13 Chorus
21 Verse
29 Chorus
37 Bridge
Repeat names identify the same section family.");
        Text(this,@"The song planet makes ONE revolution over the file. Its section moon revolves once per section; the smaller moon follows the complete chord progression, repeating within that section. Chord changes recolor that moon without restarting its orbit.
The outer band follows actual section order. Color bands on each body are built from its chords: I blue, IV red, V green, relative to the key.
MIDI markers supply names when present. Otherwise boundaries and section letters are estimates. Edit the form to identify verse, chorus and bridge. Chords are estimates from duration-weighted pitched notes.").AddToClassList("muted");
    }
    void Rebuild(int bars,string map)
    {
        try{midi.RebuildSongForm(bars,map);message.text="Section map applied.";Tick();}
        catch(Exception e){message.text=e.Message;}
    }
    public void Tick()
    {
        if(source!=midi.SongForm){source=midi.SongForm;selectedFamily=occurrence=0;RebuildControls();}
        double beat=midi.Cycles?.BeatAt(midi.Position)??0;
        var active=source?.At(beat);
        if(followToggle.value && active!=null && active!=SelectedSection)
        {selectedFamily=active.Family.Id;occurrence=active.Family.Occurrences.IndexOf(active);RefreshSelection();}
        if(lastKey!=main.currentKey || lastFlats!=main.UseFlats)
        {lastKey=main.currentKey;lastFlats=main.UseFlats;RefreshSelection();sequence.Query<SectionPatternStrip>().ForEach(s=>s.MarkDirtyRepaint());}
        overview.Form=detail.Form=source;overview.Beat=detail.Beat=beat;
        // The solar mechanism ALWAYS follows the song. Inspection only changes the enlargement.
        overview.Section=active;detail.Section=SelectedSection;
        overview.SongPhase=midi.Duration>0?midi.Position/midi.Duration:0;
        overview.Timing=midi.Cycles;overview.SongDuration=midi.Duration;
        overview.MarkDirtyRepaint();detail.MarkDirtyRepaint();
        status.text=source==null?"Load a MIDI file in MIDI FILE below.":$@"{source.Sections.Count} sections · {source.Families.Count} families
{source.BoundarySource}
Now: {active?.Family.Name} · {CurrentChord(active,beat)?.Name(main.UseFlats)??"Rest"}";
    }
    void RebuildControls()
    {
        family.choices=source==null || source.Families.Count==0?new List<string>{"No sections"}:source.Families.Select(f=>$"{f.Id+1}. {f.Name} · ×{f.Occurrences.Count}").ToList();
        sequence.Clear();
        if(source!=null)foreach(var s in source.Sections)
        {
            var card=new VisualElement();card.AddToClassList("section-card");sequence.Add(card);
            var button=new Button(()=>SeekSection(s.Start)){text=s.Family.Name,tooltip=$"Bar {s.FirstBar+1} · {s.BarCount} bars"};
            button.AddToClassList("section-chip");card.Add(button);card.Add(new SectionPatternStrip(s,main));
        }
        RefreshSelection();
    }
    public void SelectFamily(int index)
    {
        if(source==null || index<0 || index>=source.Families.Count)return;
        selectedFamily=index;occurrence=0;followToggle.SetValueWithoutNotify(false);RefreshSelection();
    }
    void RefreshSelection()
    {
        var s=SelectedSection;chords.Clear();if(s==null){description.text="";return;}
        family.SetValueWithoutNotify(family.choices[selectedFamily]);label.SetValueWithoutNotify(s.Family.Name);
        description.text=$@"{s.Family.Name} · occurrence {occurrence+1}/{s.Family.Occurrences.Count}
Bars {s.FirstBar+1}–{s.FirstBar+s.BarCount}
Progression: {s.ProgressionBeats:0.##} beats × {s.Repetitions:0.##}";
        foreach(var c in s.Chords)
        {
            var button=new Button(()=>SeekSection(c.Start)){text=c.Name(main.UseFlats),tooltip=$"{c.End-c.Start:0.##} quarter beats"};
            button.AddToClassList("chord-chip");button.style.borderBottomColor=ChordColor(c,main.currentKey);
            button.style.borderBottomWidth=3;chords.Add(button);
        }
    }
    void Visit(int delta)
    {
        if(SelectedSection==null)return;
        var f=source.Families[selectedFamily];occurrence=(occurrence+delta+f.Occurrences.Count)%f.Occurrences.Count;
        SeekSection(f.Occurrences[occurrence].Start);
    }
    void SeekSection(double beat)
    {
        var s=source?.At(beat);if(s==null)return;
        selectedFamily=s.Family.Id;occurrence=s.Family.Occurrences.IndexOf(s);RefreshSelection();
        midi.Seek(midi.Cycles.SecondsAt(beat));
    }
    public static double Phase(double beat,double start,double duration)=>duration<=0?0:((beat-start)%duration+duration)%duration/duration;
    public static SongFormAnalysis.ChordStep CurrentChord(SongFormAnalysis.Section s,double beat)
    {
        if(s==null)return null;double local=s.Start+Phase(beat,s.Start,s.ProgressionBeats)*s.ProgressionBeats;
        return s.Chords.LastOrDefault(c=>c.Start<=local)??s.Chords.FirstOrDefault();
    }
    public static Color ChordColor(SongFormAnalysis.ChordStep chord,int key)
    {
        if(chord==null || chord.Rest)return new Color(.15f,.18f,.22f);
        return TonalColorField.Pitch(chord.Root,key)*(chord.Quality.StartsWith("m",StringComparison.Ordinal) && !chord.Quality.StartsWith("maj",StringComparison.Ordinal)?.65f:1);
    }
    static Label Text(VisualElement parent,string text){var l=new Label(text);l.AddToClassList("details");parent.Add(l);return l;}
    static VisualElement Row(VisualElement parent){var row=new VisualElement();row.AddToClassList("row");parent.Add(row);return row;}
    static void AddButton(VisualElement parent,string text,Action action)=>parent.Add(new Button(action){text=text});

    sealed class SectionPatternStrip : VisualElement
    {
        public SectionPatternStrip(SongFormAnalysis.Section section,Main main)
        {
            style.height=6;style.marginBottom=5;pickingMode=PickingMode.Ignore;
            generateVisualContent+=ctx=>
            {
                if(contentRect.width<=0)return;
                var painter=ctx.painter2D;painter.lineWidth=5;
                foreach(var chord in section.Chords)
                {
                    float start=(float)((chord.Start-section.Start)/section.ProgressionBeats)*contentRect.width;
                    float end=(float)((chord.End-section.Start)/section.ProgressionBeats)*contentRect.width;
                    painter.strokeColor=ChordColor(chord,main.currentKey);painter.BeginPath();
                    painter.MoveTo(new Vector2(start,3));painter.LineTo(new Vector2(end,3));painter.Stroke();
                }
            };
        }
    }

    sealed class Mechanism : VisualElement
    {
        readonly Main main;
        readonly bool overview;
        readonly List<(Vector2 center,double beat)> moons=new();
        public SongFormAnalysis Form;
        public SongFormAnalysis.Section Section;
        public double Beat,SongPhase;
        public MidiCycleAnalysis Timing;
        public double SongDuration;
        public Action<double> Seek;
        static readonly Color Brass=new(.48f,.39f,.25f),Track=new(.20f,.27f,.34f);
        public Mechanism(Main owner,bool all)
        {
            main=owner;overview=all;style.height=all?280:235;style.flexShrink=0;
            generateVisualContent+=Draw;
            RegisterCallback<PointerDownEvent>(e=>
            {
                Vector2 point=new(e.localPosition.x,e.localPosition.y);
                foreach(var m in moons)if(Vector2.Distance(point,m.center)<14){Seek?.Invoke(m.beat);return;}
            });
        }
        static Vector2 Point(Vector2 c,float r,double phase)=>c+new Vector2(Mathf.Sin((float)phase*Mathf.PI*2),-Mathf.Cos((float)phase*Mathf.PI*2))*r;
        static void Circle(Painter2D p,Vector2 c,float r,Color color,bool fill)
        {p.BeginPath();p.Arc(c,r,Angle.Degrees(0),Angle.Degrees(360));if(fill){p.fillColor=color;p.Fill();}else{p.strokeColor=color;p.Stroke();}}
        static void Arc(Painter2D p,Vector2 c,float r,double start,double end,Color color,float width)
        {if(end<=start)return;p.lineWidth=width;p.strokeColor=color;p.BeginPath();p.Arc(c,r,Angle.Degrees((float)(start*360-90)),Angle.Degrees((float)(end*360-90)));p.Stroke();}
        static void Arm(Painter2D p,Vector2 a,Vector2 b,Color color)
        {p.lineWidth=1.5f;p.strokeColor=color;p.BeginPath();p.MoveTo(a);p.LineTo(b);p.Stroke();}
        void Pattern(Painter2D p,Vector2 center,float radius,SongFormAnalysis.Section section)
        {
            Circle(p,center,radius,new Color(.025f,.03f,.04f),true);
            foreach(var chord in section.Chords)Arc(p,center,radius-2,(chord.Start-section.Start)/section.ProgressionBeats,(chord.End-section.Start)/section.ProgressionBeats,ChordColor(chord,main.currentKey),4);
            p.lineWidth=1;Circle(p,center,radius,Brass,false);
        }
        void Draw(MeshGenerationContext ctx)
        {
            moons.Clear();if(contentRect.width<=0)return;
            var p=ctx.painter2D;Vector2 center=contentRect.center;float r=Mathf.Min(contentRect.width,contentRect.height)*.44f;
            Circle(p,center,r+9,new Color(.025f,.035f,.052f),true);
            if(Form==null || Section==null)return;
            var s=Section;var current=CurrentChord(s,Beat);
            if(overview)
            {
                foreach(var section in Form.Sections)
                {
                    for(double cycle=section.Start;cycle<section.End-.00001;cycle+=section.ProgressionBeats)
                        foreach(var chord in section.Chords)
                        {
                            double start=cycle+chord.Start-section.Start,end=Math.Min(section.End,cycle+chord.End-section.Start);
                            Arc(p,center,r,Timing.SecondsAt(start)/SongDuration,Timing.SecondsAt(end)/SongDuration,ChordColor(chord,main.currentKey),5);
                        }
                    Arm(p,Point(center,r-5,Timing.SecondsAt(section.Start)/SongDuration),Point(center,r+5,Timing.SecondsAt(section.Start)/SongDuration),Brass);
                }
                Circle(p,Point(center,r,SongPhase),3,Color.white,true);
                float songRadius=r*.55f,sectionRadius=r*.24f,progressionRadius=r*.12f;
                p.lineWidth=1;Circle(p,center,songRadius,Track,false);
                // One revolution per file. Smaller mechanisms are carried by their parent.
                Vector2 song=Point(center,songRadius,Main.ReducedMotion?0:SongPhase);
                double sectionPhase=Phase(Beat,s.Start,s.End-s.Start);
                Vector2 sectionMoon=Point(song,sectionRadius,Main.ReducedMotion?0:sectionPhase);
                double progressionPhase=Phase(Beat,s.Start,s.ProgressionBeats);
                Vector2 progression=Point(sectionMoon,progressionRadius,Main.ReducedMotion?0:progressionPhase);
                Arm(p,center,song,Brass);p.lineWidth=1;Circle(p,song,sectionRadius,Track,false);
                Arm(p,song,sectionMoon,Brass);p.lineWidth=1;Circle(p,sectionMoon,progressionRadius,Track,false);
                Arm(p,sectionMoon,progression,Brass);
                Pattern(p,song,12,s);Pattern(p,sectionMoon,7,s);
                Circle(p,progression,4,ChordColor(current,main.currentKey),true);
                p.lineWidth=1;Circle(p,progression,5,Color.white,false);
                Circle(p,center,12,Brass,true);Circle(p,center,8,TonalColorField.Tonic,true);
                ctx.DrawText(main.PitchName(main.currentKey),center+new Vector2(-4,-5),10,Color.white,null);
            }
            else
            {
                float orbit=r*.77f;p.lineWidth=1;Circle(p,center,orbit,Brass,false);
                Pattern(p,center,23,s);
                // Chord sectors are fixed on a progression wheel. ONE hand traverses all chords.
                int labelIndex=0,labelStride=Mathf.Max(1,Mathf.CeilToInt(s.Chords.Count/10f));
                foreach(var chord in s.Chords)
                {
                    double start=(chord.Start-s.Start)/s.ProgressionBeats;
                    Arc(p,center,orbit,start,(chord.End-s.Start)/s.ProgressionBeats,ChordColor(chord,main.currentKey),4);
                    Vector2 marker=Point(center,orbit,start);
                    Circle(p,marker,3,ChordColor(chord,main.currentKey),true);
                    if(labelIndex++%labelStride==0 || chord==current)
                        ctx.DrawText(chord.Name(main.UseFlats),Point(center,orbit+13,(start+(chord.End-s.Start)/s.ProgressionBeats)*.5)+new Vector2(-9,-4),10,Color.white,null);
                    moons.Add((marker,chord.Start));
                }
                Vector2 moon=Point(center,orbit,Main.ReducedMotion?0:Phase(Beat,s.Start,s.ProgressionBeats));
                Arm(p,center,moon,Brass);Circle(p,moon,7,ChordColor(current,main.currentKey),true);
                p.lineWidth=1;Circle(p,moon,9,Color.white,false);
                ctx.DrawText($"×{s.Repetitions:0.##}",center+new Vector2(-10,-5),11,Color.white,null);
            }
        }
    }
}
