using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

// The pattern wheels as a solid object for the headset: the same rack, pinion, orbits, planet
// and moon as the desktop panel (PatternWheelDeck), built from meshes standing upright behind
// the torus and facing the viewer, with real depth between the layers.
//  Pinion  — a thick ring, the song wound once around: one slab per section visit, its relief
//            and stripe spacing telling the family (the desktop's hatch), brightest where the
//            song is. It turns so now sits at nine o'clock, where the rack meets it.
//  Rack    — the song unrolled down the left side, scrolling up as it plays. Pinch it and drag
//            up or down to seek.
//  Orbits  — each recurring group's carrier floats at its own depth in front of the ring, its
//            families riding it as small planets (their fundamental's chords around the rim).
//  Planet  — the section playing, at the centre and nearest the viewer: this visit's chords
//            around its rim, the family's loop rolling inside it as a moon.
//  Stacks  — every pitched lane's patterns as a stack of discs below the wheel; the playing
//            pattern rides on top and turns once per loop.
// Meshes are rebuilt only when the song, the section or a lane's play changes; each frame only
// transforms move (and the rack's few slabs), so it costs far less than the flat panel it replaces.
public sealed class PatternWheel3D : MonoBehaviour
{
    Main main;MidiPlayer midi;PreparedPatternSong source;HandInput hands;
    Material solid,glow;
    Transform root,ring,rackT,planetT,moonT,moonSpin,stacksT;
    Mesh ringMesh,rackMesh,planetMesh,moonMesh,glowMesh;
    readonly Builder b=new();
    double[] barU=Array.Empty<double>(),startU=Array.Empty<double>(),endU=Array.Empty<double>(),keyU=Array.Empty<double>();double cachedDuration=-1;
    int[] familyIndex=Array.Empty<int>(),visitNumber=Array.Empty<int>(),familyKey=Array.Empty<int>(),orbitOf=Array.Empty<int>(),memberOf=Array.Empty<int>();
    sealed class OrbitView{public int Group=-1;public int[] Members=Array.Empty<int>();public double Shown=double.NaN;public Transform Carrier;public readonly List<Transform> Planets=new();public float Radius,Size;}
    readonly List<OrbitView> orbits=new();
    readonly List<TextBox> ringLabels=new(),rackLabels=new(),keyLabels=new();readonly List<double> keyPhase=new();
    TextBox planetName,planetVariation,moonCount,caption;string captionText="",planetText="";
    int builtSection=-1,builtMoonPattern=-1,builtMoonTranspose=int.MinValue;float planetRebuild;
    Transform nowMark,contact;
    // Seeking by pinching the rack.
    HandInput.Hand dragging;float dragFromY;double dragFromTime;
    // Layout, in units of the ring's radius.
    const float RingIn=.86f,RingOut=.97f,Planet=.34f,RackX=-1.13f,RackHalf=1.1f;
    static readonly Color Metal=new(.32f,.46f,.46f),Shadow=new(.03f,.045f,.05f),InkBright=new(.62f,.84f,.82f),Plate=new(.05f,.07f,.08f);

    public void Init(Main owner,HandInput handInput)
    {
        main=owner;midi=owner.GetComponent<MidiPlayer>();hands=handInput;
        var shader=Resources.Load<Shader>("VrWheel");
        solid=new Material(shader){name="Pattern wheel · solid"};
        glow=new Material(shader){name="Pattern wheel · glow"};glow.SetFloat("_SrcBlend",(float)UnityEngine.Rendering.BlendMode.One);glow.SetFloat("_DstBlend",(float)UnityEngine.Rendering.BlendMode.One);glow.SetFloat("_ZWrite",0);glow.renderQueue=3050;
        root=new GameObject("Pattern wheels · 3D").transform;root.SetParent(transform,false);
        ring=Part("Pinion · the song wound once around",root,out ringMesh);
        rackT=Part("Rack · the song unrolled",root,out rackMesh);
        planetT=Part("Planet · the section playing",root,out planetMesh);
        moonT=new GameObject("Moon").transform;moonT.SetParent(planetT,false);moonSpin=Part("Moon · the family's loop",moonT,out moonMesh);
        stacksT=new GameObject("Instrument stacks").transform;stacksT.SetParent(root,false);
        var g=new GameObject("Sparks");g.transform.SetParent(root,false);glowMesh=new Mesh{indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};glowMesh.MarkDynamic();g.AddComponent<MeshFilter>().sharedMesh=glowMesh;g.AddComponent<MeshRenderer>().sharedMaterial=glow;
        nowMark=Sphere("Now",root,.035f);contact=Sphere("Contact",planetT,.03f);
        planetName=Label(planetT,"",.07f,TextAlignmentOptions.Center,Color.white);planetVariation=Label(planetT,"",.04f,TextAlignmentOptions.Center,new Color(.7f,.8f,.85f));
        moonCount=Label(moonT,"",.05f,TextAlignmentOptions.Center,Color.white);
        caption=Label(root,"",.065f,TextAlignmentOptions.TopLeft,new Color(.78f,.88f,.9f));caption.SetFixedWithWrap(1.6f);caption.transform.localPosition=new Vector3(1.16f,1.0f,-.02f);
        root.gameObject.SetActive(false);
    }
    Transform Part(string name,Transform parent,out Mesh mesh)
    {
        var go=new GameObject(name);go.transform.SetParent(parent,false);
        mesh=new Mesh{name=name,indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterial=solid;return go.transform;
    }
    Transform Sphere(string name,Transform parent,float radius)
    {
        var go=GameObject.CreatePrimitive(PrimitiveType.Sphere);go.name=name;Destroy(go.GetComponent<Collider>());go.transform.SetParent(parent,false);
        go.transform.localScale=Vector3.one*radius*2;var m=new Material(glow);m.SetColor("_Color",Color.white);go.GetComponent<Renderer>().sharedMaterial=m;return go.transform;
    }
    // Text height in the wheel's units.
    static TextBox Label(Transform parent,string text,float height,TextAlignmentOptions align,Color color)
    {
        var t=TextBox.Create(text,align);t.transform.SetParent(parent,false);t.Size=height*12f;   // TextMeshPro font size per unit of line height, measuredt.Color=color;t.TextField.fontMaterial.renderQueue=3200;
        t.TextField.textWrappingMode=TextWrappingModes.NoWrap;return t;
    }
    public void Place(Vector3 centre,Quaternion rotation,float unitsPerRadius)
    {
        if(root==null)return;root.position=centre;root.rotation=rotation;root.localScale=Vector3.one*unitsPerRadius;
    }
    public bool Visible{get=>root!=null&&root.gameObject.activeSelf;set{if(root!=null&&root.gameObject.activeSelf!=value)root.gameObject.SetActive(value);}}
    public Transform Root=>root;

    void LateUpdate()
    {
        if(root==null||!root.gameObject.activeSelf||midi==null)return;
        if(source!=midi.Prepared){source=midi.Prepared;source?.EnsurePatterns();Load();}
        if(source?.Sections==null||source.Sections.Length==0||source.Patterns.Length==0||midi.Cycles==null)return;
        Cache();
        double duration=Math.Max(1e-6,midi.Duration),now=Math.Clamp(midi.Position/duration,0,1);
        double beat=midi.Cycles.BeatAt(midi.ScorePosition);
        int si=Math.Max(0,Array.FindLastIndex(source.Sections,s=>s.Start<=beat));var section=source.Sections[si];
        int fi=Math.Max(0,familyIndex[si]);var pattern=source.Patterns[fi];
        double progress=Math.Clamp((beat-section.Start)/Math.Max(1e-6,section.End-section.Start),0,1);
        if(si!=builtSection){builtSection=si;BuildRing(si);BuildPlanet(section,fi,progress,beat);}
        else if((planetRebuild-=Time.unscaledDeltaTime)<=0){planetRebuild=.25f;BuildPlanet(section,fi,progress,beat);}
        TurnRing(now);
        BuildRack(now,si);
        TurnOrbits(si,fi,progress);
        Moon(section,pattern,fi,beat,progress);
        Stacks(beat);
        Caption(section,si,pattern,fi,beat);
        Seek();
    }

    // ---------- the song ----------
    void Load()
    {
        builtSection=-1;builtMoonPattern=-1;cachedDuration=-1;
        foreach(var o in orbits)if(o.Carrier!=null)Destroy(o.Carrier.gameObject);orbits.Clear();
        foreach(var l in ringLabels.Concat(rackLabels).Concat(keyLabels))if(l!=null)Destroy(l.gameObject);ringLabels.Clear();rackLabels.Clear();keyLabels.Clear();keyPhase.Clear();
        foreach(Transform c in stacksT)Destroy(c.gameObject);lanes.Clear();
        if(source?.Sections==null){familyIndex=visitNumber=orbitOf=memberOf=familyKey=Array.Empty<int>();return;}
        familyIndex=source.Sections.Select(s=>Array.FindIndex(source.Patterns,p=>p.Family==s.Family)).ToArray();
        visitNumber=source.Sections.Select((s,i)=>source.Sections.Take(i+1).Count(x=>x.Family==s.Family)).ToArray();
        familyKey=source.Patterns.Select(p=>source.Sections.FirstOrDefault(s=>s.Family==p.Family)?.KeyRoot??-1).ToArray();
        // Orbits as on the desktop: the two most recurring groups each get a carrier, the rest ride the outer one.
        orbitOf=Enumerable.Repeat(-1,source.Patterns.Length).ToArray();memberOf=new int[source.Patterns.Length];
        void Place(int[] members,int group){for(int m=0;m<members.Length;m++){orbitOf[members[m]]=orbits.Count;memberOf[members[m]]=m;}orbits.Add(new OrbitView{Group=group,Members=members});}
        foreach(var g in (source.Groups??Array.Empty<PreparedPatternSong.SectionGroup>()).OrderByDescending(g=>g.Visits).ThenByDescending(g=>g.Families.Length).Take(2))
        {
            var members=g.Families.Select(f=>Array.FindIndex(source.Patterns,q=>q.Family==f)).Where(k=>k>=0&&orbitOf[k]<0).Distinct().ToArray();
            if(members.Length>=2)Place(members,g.Id);
        }
        var free=Enumerable.Range(0,source.Patterns.Length).Where(k=>orbitOf[k]<0).OrderBy(k=>Array.FindIndex(source.Sections,x=>x.Family==source.Patterns[k].Family)).ToArray();
        if(free.Length>0)Place(free,-1);
        BuildOrbits();
        for(int j=0;j<source.Sections.Length;j++){ringLabels.Add(Label(ring,RimLabel(j),.06f,TextAlignmentOptions.Center,InkBright));rackLabels.Add(Label(rackT,source.Sections[j].DisplayName,.065f,TextAlignmentOptions.Right,InkBright));}
        foreach(var k in source.KeyChanges??Array.Empty<PreparedPatternSong.KeyChange>())keyLabels.Add(Label(ring,main.PitchName(k.Key)+(k.Minor?"m":""),.065f,TextAlignmentOptions.Center,Color.white));
        LoadLanes();
    }
    void Cache()
    {
        if(Math.Abs(cachedDuration-midi.Duration)<1e-6&&barU.Length==(source.Measures?.Length??0))return;
        cachedDuration=midi.Duration;double duration=Math.Max(1e-6,midi.Duration);
        double U(double beat)=>midi.AudioTime(midi.Cycles.SecondsAt(beat))/duration;
        barU=(source.Measures??Array.Empty<MidiCycleAnalysis.Bar>()).Select(x=>U(x.Start)).ToArray();
        startU=source.Sections.Select(s=>U(s.Start)).ToArray();endU=source.Sections.Select(s=>U(s.End)).ToArray();
        keyU=(source.KeyChanges??Array.Empty<PreparedPatternSong.KeyChange>()).Select(k=>U(k.Beat)).ToArray();
        builtSection=-1;
    }
    string RimLabel(int j)
    {
        var pattern=source.Patterns[Math.Max(0,familyIndex[j])];var section=source.Sections[j];
        string own=string.IsNullOrEmpty(section.Short)?pattern.Short:section.Short;
        int same=source.Sections.Count(s=>s.Family==section.Family&&(string.IsNullOrEmpty(s.Short)?pattern.Short:s.Short)==own);
        return same>1?own+visitNumber[j]:own;
    }
    float Level(int j,int si)=>j==si?.85f:familyIndex[j]==familyIndex[si]?.5f:.22f;
    static Color Ink(float level)=>Color.Lerp(Shadow,InkBright,level);
    // A family's relief and stripe spacing: the solid version of its hatch.
    static float Relief(int family)=>.035f+.02f*(family%3);
    static double Stripe(int family)=>.004+.0025*((family/3)%3);

    // The pinion: built in ring phase (song position u at phase −u), turned each frame.
    void BuildRing(int si)
    {
        b.Clear();
        b.Slab(Vector2.zero,RingOut,1.0f,0,1,-.02f,.05f,Metal,Metal*.6f);
        b.Slab(Vector2.zero,RingIn-.015f,RingOut,0,1,.0f,.03f,Plate,Plate);
        for(int i=0;i<barU.Length;i+=Math.Max(1,barU.Length/160))b.Tooth(Vector2.zero,1.0f,1.04f,-barU[i],.004,-.02f,.04f,Metal);
        for(int j=0;j<source.Sections.Length;j++)
        {
            double a=-endU[j],c=-startU[j];double gap=.0025;if(c-a<=gap*2)continue;
            int f=Math.Max(0,familyIndex[j]);float level=Level(j,si);bool returns=source.Patterns[f].Visits>1;
            float relief=Relief(f)*(j==si?1.6f:1);
            b.Striped(Vector2.zero,RingIn,RingOut,a+gap,c-gap,-relief,relief,Ink(level),Ink(level*.55f),Stripe(f),returns);
        }
        // Group brackets inside the band: the verse + chorus pair each time it returns.
        foreach(var (first,last,g) in GroupRuns())
        {
            double a=-endU[last],c=-startU[first];bool current=first<=si&&si<=last,family=g==source.Sections[si].Group;
            b.Sector(Vector2.zero,.82f,.832f,a+.004,c-.004,-.03f,Ink(current?.9f:family?.5f:.18f));
        }
        for(int k=0;k<keyU.Length;k++)b.Tooth(Vector2.zero,RingIn-.03f,1.06f,-keyU[k],.003,-.08f,.1f,Color.white*.85f);
        b.Apply(ringMesh);
        for(int j=0;j<ringLabels.Count;j++)
        {
            double mid=-(startU[j]+endU[j])/2;var t=ringLabels[j];bool fits=(endU[j]-startU[j])*2*Math.PI*RingIn>=.12;
            t.gameObject.SetActive(fits);if(!fits)continue;
            t.transform.localPosition=Builder.At(Vector2.zero,.765f,mid,-.05f);t.Color=j==si?Color.white:Ink(Level(j,si)+.25f);
        }
        for(int k=0;k<keyLabels.Count&&k<keyU.Length;k++)keyLabels[k].transform.localPosition=Builder.At(Vector2.zero,1.13f,-keyU[k],-.06f);
    }
    void TurnRing(double now)
    {
        // Phase .75 (nine o'clock) is now; the ring turns clockwise as the song plays.
        var turn=Quaternion.AngleAxis(-(float)((.75+now)*360),Vector3.forward);ring.localRotation=turn;
        var upright=Quaternion.Inverse(turn);
        foreach(var t in ringLabels)if(t.gameObject.activeSelf)t.transform.localRotation=upright;
        foreach(var t in keyLabels)t.transform.localRotation=upright;
        nowMark.localPosition=Builder.At(Vector2.zero,(RingIn+RingOut)/2,.75,-.09f);
        float pulse=midi.IsPlaying?1.4f:.6f;nowMark.GetComponent<Renderer>().sharedMaterial.SetColor("_Color",new Color(.8f,1,1)*pulse);
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

    // The rack: the song unrolled at the pinion's scale, future below, meeting the ring at nine o'clock.
    void BuildRack(double now,int si)
    {
        float circumference=2*Mathf.PI*RingOut;float Y(double u)=>-(float)((u-now)*circumference);
        b.Clear();
        b.Box(new Vector3(RackX,-RackHalf,-.02f),new Vector3(RackX+.02f,RackHalf,.03f),Metal);
        int step=Math.Max(1,barU.Length/200);
        for(int i=0;i<barU.Length;i+=step){float y=Y(barU[i]);if(y<-RackHalf||y>RackHalf)continue;b.Box(new Vector3(RackX+.02f,y-.004f,-.01f),new Vector3(RackX+.05f,y+.004f,.02f),Metal);}
        for(int j=0;j<source.Sections.Length;j++)
        {
            float y0=Mathf.Clamp(Y(endU[j]),-RackHalf,RackHalf),y1=Mathf.Clamp(Y(startU[j]),-RackHalf,RackHalf);
            var label=rackLabels[j];bool show=y1-y0>.01f;
            if(show)
            {
                int f=Math.Max(0,familyIndex[j]);float level=Level(j,si),relief=Relief(f)*(j==si?1.6f:1);
                b.Box(new Vector3(RackX-.12f,y0+.006f,-relief),new Vector3(RackX-.02f,y1-.006f,.02f),Ink(level));
            }
            float top=Y(startU[j]);bool named=show&&top<=RackHalf&&top>=-RackHalf+.06f;
            if(label.gameObject.activeSelf!=named)label.gameObject.SetActive(named);
            if(named){label.transform.localPosition=new Vector3(RackX-.15f,top-.035f,-.02f);label.Color=j==si?Color.white:Ink(Level(j,si)+.3f);}
        }
        // The mesh point: the rack's now meets the ring's.
        b.Box(new Vector3(RackX+.02f,-.006f,-.03f),new Vector3(-RingOut,.006f,-.01f),Ink(.7f));
        b.Apply(rackMesh);
    }

    // ---------- the planetary train ----------
    void BuildOrbits()
    {
        int count=orbits.Count;if(count==0)return;
        float inner=Planet+.08f,outer=RingIn-.14f,lane=(outer-inner)/count;
        for(int o=0;o<count;o++)
        {
            var orbit=orbits[o];int n=orbit.Members.Length;orbit.Radius=inner+lane*(o+.5f);
            orbit.Size=Mathf.Clamp(Mathf.Min(lane/2-.02f,Mathf.PI*orbit.Radius/n-.03f),.035f,.12f);
            orbit.Carrier=Part("Orbit "+(o+1),root,out var carrierMesh);
            // Each orbit floats at its own depth: the train reads as layers in front of the ring.
            orbit.Carrier.localPosition=new Vector3(0,0,-.06f-.05f*o);
            b.Clear();b.Torus(orbit.Radius,.006f,Ink(.35f));
            for(int i=0;i<24;i++)b.Tooth(Vector2.zero,orbit.Radius-.02f,orbit.Radius-.006f,i/24.0,.003,-.005f,.01f,Ink(.3f));
            b.Apply(carrierMesh);
            for(int m=0;m<n;m++)
            {
                int k=orbit.Members[m];var planet=Part(source.Patterns[k].Short,orbit.Carrier,out var mesh);
                BuildFundamental(mesh,k,orbit.Size);
                var name=Label(planet,source.Patterns[k].Short,Mathf.Clamp(orbit.Size*.6f,.03f,.06f),TextAlignmentOptions.Center,Color.white);name.transform.localPosition=new Vector3(0,0,-.03f);
                orbit.Planets.Add(planet);
            }
        }
    }
    void BuildFundamental(Mesh mesh,int k,float r)
    {
        var pattern=source.Patterns[k];b.Clear();
        b.Cylinder(Vector2.zero,r,-.02f,.03f,Plate,Metal*.5f);
        double loop=Math.Max(.25,pattern.LoopBeats);int key=familyKey[k]>=0?familyKey[k]:main.currentKey;
        foreach(var chord in pattern.Loop)b.Sector(Vector2.zero,r*.62f,r*.92f,chord.Start/loop,chord.End/loop,-.025f,CyclicOrrery.ChordColor(chord,key));
        int visits=Math.Min(10,pattern.Visits);
        for(int v=0;v<visits;v++){var at=Builder.At(Vector2.zero,r*.45f,.5+(v-(visits-1)/2.0)*.08,-.026f);b.Disc(new Vector2(at.x,at.y),r*.05f,-.026f,InkBright);}
        b.Apply(mesh);
    }
    int OrbitOfSection(int j)=>familyIndex[j]<0?-1:orbitOf[familyIndex[j]];
    double Carrier(int o,int si,double progress)
    {
        const double Gate=.75;int n=orbits[o].Members.Length,j=si;double q=progress;
        while(j>=0&&OrbitOfSection(j)!=o)j--;
        if(j<si)q=1;
        if(j<0){j=0;while(j<source.Sections.Length&&OrbitOfSection(j)!=o)j++;if(j==source.Sections.Length)return Gate;q=0;}
        return Gate+(memberOf[familyIndex[j]]+q-.5)/n;
    }
    static double Wrap(double x)=>x-Math.Round(x);
    void TurnOrbits(int si,int fi,double progress)
    {
        float blend=Main.ReducedMotion?1:1-Mathf.Exp(-Time.unscaledDeltaTime*6);
        for(int o=0;o<orbits.Count;o++)
        {
            var orbit=orbits[o];double target=Carrier(o,si,progress);
            orbit.Shown=double.IsNaN(orbit.Shown)?target:orbit.Shown+Wrap(target-orbit.Shown)*blend;
            var turn=Quaternion.AngleAxis(-(float)(orbit.Shown*360),Vector3.forward);orbit.Carrier.localRotation=turn;var upright=Quaternion.Inverse(turn);
            for(int m=0;m<orbit.Planets.Count;m++)
            {
                var planet=orbit.Planets[m];bool on=orbit.Members[m]==fi;
                // Member m sits m/n behind the carrier's phase; the one playing lifts toward the viewer.
                var at=Builder.At(Vector2.zero,orbit.Radius,-m/(double)orbit.Members.Length,0);
                planet.localPosition=Vector3.Lerp(planet.localPosition,new Vector3(at.x,at.y,on?-.08f:0),blend);planet.localRotation=upright;
                planet.localScale=Vector3.one*(on?1.15f:1);
            }
        }
    }

    // The section playing: this visit's chords around the rim, brighter once played; pass ticks.
    void BuildPlanet(PreparedPatternSong.Section section,int fi,double progress,double beat)
    {
        b.Clear();double length=Math.Max(1e-6,section.End-section.Start);
        b.Cylinder(Vector2.zero,Planet,-.14f,.1f,Plate,Metal*.6f);
        int bars=Math.Max(4,section.BarCount);for(int i=0;i<bars;i++)b.Tooth(Vector2.zero,Planet,Planet+.025f,i/(double)bars,.004,-.14f,.06f,Metal);
        b.Striped(Vector2.zero,Planet-.02f,Planet,0,1,-.15f,.01f,Ink(.8f),Ink(.45f),Stripe(fi),source.Patterns[fi].Visits>1);
        float chordIn=Planet-.085f,chordOut=Planet-.03f;
        foreach(var chord in source.Chords??Array.Empty<SongFormAnalysis.ChordStep>())
        {
            if(chord.End<=section.Start||chord.Start>=section.End)continue;
            double a=(Math.Max(chord.Start,section.Start)-section.Start)/length,c=(Math.Min(chord.End,section.End)-section.Start)/length;
            var hue=CyclicOrrery.ChordColor(chord,main.currentKey);
            b.Sector(Vector2.zero,chordIn,chordOut,a,c,-.15f,a<progress?hue:hue*.5f);
        }
        var passes=section.Passes??Array.Empty<PreparedPatternSong.Pass>();
        for(int k=1;k<passes.Length;k++)b.Tooth(Vector2.zero,chordIn-.01f,Planet,(passes[k].Start-section.Start)/length,.004,-.16f,.02f,Ink(.9f));
        b.Apply(planetMesh);
        planetT.localPosition=new Vector3(0,0,-.02f);
    }
    void Moon(PreparedPatternSong.Section section,PreparedPatternSong.Pattern pattern,int fi,double beat,double progress)
    {
        double length=Math.Max(1e-6,section.End-section.Start),loop=Math.Max(.25,pattern.LoopBeats);
        var passes=section.Passes??Array.Empty<PreparedPatternSong.Pass>();int pi=Math.Max(0,Array.FindLastIndex(passes,x=>x.Start<=beat+1e-6));
        var pass=passes.Length>0?passes[pi]:null;
        bool moon=passes.Length>1||length>loop*1.05;float inner=Planet-.1f;
        float m=Mathf.Clamp(inner*(float)(loop/length),.07f,inner*.5f);
        double phase=pass==null?((beat-section.Start)/loop%1+1)%1:(((pass.Offset+beat-pass.Start)/loop)%1+1)%1;
        int transpose=pass?.Transpose??0;
        moonT.gameObject.SetActive(moon);
        if(moon)
        {
            if(fi!=builtMoonPattern||transpose!=builtMoonTranspose||Mathf.Abs(moonSpin.localScale.x-m)>1e-4f)
            {
                builtMoonPattern=fi;builtMoonTranspose=transpose;b.Clear();
                b.Cylinder(Vector2.zero,1,-.02f,.04f,Shadow,Metal*.6f);
                int teeth=Math.Max(6,pattern.LoopBars*4);for(int i=0;i<teeth;i++)b.Tooth(Vector2.zero,1,1.08f,i/(double)teeth,.006,-.02f,.04f,Metal);
                foreach(var chord in pattern.Loop)
                {
                    var shifted=new SongFormAnalysis.ChordStep{Root=chord.Rest?-1:HarmonyModel.Mod(chord.Root+transpose),Quality=chord.Quality};
                    b.Sector(Vector2.zero,.55f,.88f,chord.Start/loop,chord.End/loop,-.03f,CyclicOrrery.ChordColor(shifted,main.currentKey));
                }
                b.Apply(moonMesh);moonSpin.localScale=new Vector3(m,m,1);
            }
            var at=Builder.At(Vector2.zero,inner-m,progress,0);moonT.localPosition=new Vector3(at.x,at.y,-.2f);
            moonSpin.localRotation=Quaternion.AngleAxis(-(float)((progress-phase)*360),Vector3.forward);
            int full=passes.Count(x=>!x.Partial),index=passes.Take(pi+1).Count(x=>!x.Partial);
            string count=pass!=null&&pass.Partial?(pi==0?"in":"tag"):$"{index}/{full}";if(moonCount.TextField.text!=count)moonCount.Text=count;
            moonCount.transform.localPosition=new Vector3(0,0,-.05f);
        }
        // Contact: the fundamental meets this visit; a white spark where the visit departs from it.
        bool varied=false;
        if(pass!=null){var changed=pass.Changed??Array.Empty<double>();for(int i=0;i+1<changed.Length;i+=2){double from=pass.Start+changed[i]-pass.Offset,to=pass.Start+changed[i+1]-pass.Offset;if(beat>=from&&beat<to)varied=true;}}
        var sounding=source.Chords?.LastOrDefault(x=>x.Start<=beat+1e-6&&beat<x.End);
        var hue=varied?Color.white:CyclicOrrery.ChordColor(sounding,main.currentKey);
        var c=Builder.At(Vector2.zero,inner,progress,-.2f);contact.localPosition=c;contact.localScale=Vector3.one*(varied?.07f:.045f);
        contact.GetComponent<Renderer>().sharedMaterial.SetColor("_Color",hue*(midi.IsPlaying?1.5f:.6f));
        string title=section.DisplayName;if(title!=planetText){planetText=title;planetName.Text=title;planetVariation.Text=string.IsNullOrEmpty(section.Variation)?"":section.Variation.Length>28?section.Variation.Substring(0,27)+"…":section.Variation;}
        var labelAt=moon?-(Vector3)(Vector2)Builder.At(Vector2.zero,inner*.5f,progress,0):new Vector3(0,Planet*.25f,0);
        planetName.transform.localPosition=new Vector3(labelAt.x,labelAt.y,-.17f);planetVariation.transform.localPosition=new Vector3(labelAt.x,labelAt.y-.07f,-.17f);
    }

    void Caption(PreparedPatternSong.Section section,int si,PreparedPatternSong.Pattern pattern,int fi,double beat)
    {
        var passes=section.Passes??Array.Empty<PreparedPatternSong.Pass>();int pi=Math.Max(0,Array.FindLastIndex(passes,q=>q.Start<=beat+1e-6));
        int full=passes.Count(q=>!q.Partial),index=passes.Take(pi+1).Count(q=>!q.Partial);
        string passText=passes.Length==0?"":passes[pi].Partial?(pi==0?"lead-in":"tag"):$"pass {index} of {full}";
        string key=main.PitchName(main.currentKey)+(main.MinorMode?" minor":" major");
        var tension=midi.CurrentTension;if(tension!=null)key+=$" · {tension.Kind} → {main.PitchName(tension.Target)}{(tension.TargetMinor?" minor":" major")}";
        string group=section.Group>=0&&section.Group<source.Groups.Length?$"{source.Groups[section.Group].Short} returns: {section.GroupVisit} of {source.Groups[section.Group].Visits}\n":"";
        string text=$"<b>{section.DisplayName}</b>\n{pattern.Short}  {PatternWheelDeck.Numerals(pattern)}\n{pattern.LoopBars} bar loop · {passText}\nvisit {visitNumber[si]} of {pattern.Visits}\n{group}{key}\n{(source.SongBars>0?source.FormGrammar:"")}";
        if(text!=captionText){captionText=text;caption.Text=text;}
    }

    // Pinch the rack and drag: up to go forward, down to go back, at the rack's own scale.
    void Seek()
    {
        if(hands==null||!midi.Loaded)return;
        if(dragging!=null)
        {
            if(!dragging.Tracked||!dragging.Pinching){dragging=null;return;}
            float y=root.InverseTransformPoint(dragging.Index).y,circumference=2*Mathf.PI*RingOut;
            midi.Seek(Math.Clamp(dragFromTime+(y-dragFromY)/circumference*midi.Duration,0,midi.Duration));return;
        }
        foreach(var h in hands.Both)
        {
            if(!h.Tracked||!h.PinchStarted)continue;
            var p=root.InverseTransformPoint(h.Index);
            if(p.x>RackX-.3f&&p.x<RackX+.1f&&Mathf.Abs(p.y)<RackHalf&&Mathf.Abs(p.z)<.15f){dragging=h;dragFromY=p.y;dragFromTime=midi.Position;return;}
        }
    }

    // ---------- the instrument stacks ----------
    sealed class LaneView
    {
        public PreparedPatternSong.InstrumentPart Part;public Transform Root;public readonly List<Transform> Discs=new();public readonly List<TextBox> Letters=new();
        public float[] Shown=Array.Empty<float>();public int Play=-2;public Transform Top;public Mesh TopMesh;public TextBox Name,Token;public Transform Featured;
        public MidiCycleAnalysis.Hit[] Notes=Array.Empty<MidiCycleAnalysis.Hit>();public int Low=48,High=84;
    }
    readonly List<LaneView> lanes=new();
    void LoadLanes()
    {
        if(source.Parts==null)return;
        var parts=source.Parts.Where(p=>p.Patterns!=null&&p.Patterns.Length>0).ToList();if(parts.Count==0)return;
        // A grid to the right of the ring, under the caption: the wheel stays compact enough to
        // stand clear above the torus.
        int columns=parts.Count>8?3:parts.Count>3?2:1,rows=(parts.Count+columns-1)/columns;
        float cell=Mathf.Min(.62f,1.3f/Math.Max(1,rows)),radius=Mathf.Min(.19f,cell*.34f);
        for(int i=0;i<parts.Count;i++)
        {
            var part=parts[i];var lane=new LaneView{Part=part,Shown=Enumerable.Range(0,part.Patterns.Length).Select(x=>(float)x).ToArray()};
            lane.Notes=(source.Notes??Array.Empty<MidiCycleAnalysis.Hit>()).Where(n=>n.Track==part.Track&&n.Channel==part.Channel).OrderBy(n=>n.Beat).ToArray();
            if(lane.Notes.Length>0){lane.Low=lane.Notes.Min(n=>n.Pitch);lane.High=Math.Max(lane.Low+7,lane.Notes.Max(n=>n.Pitch));}
            lane.Root=new GameObject("Stack · "+part.Name).transform;lane.Root.SetParent(stacksT,false);
            lane.Root.localPosition=new Vector3(1.42f+(i%columns)*.62f,.3f-(i/columns)*cell,-.05f);lane.Root.localScale=Vector3.one*radius;
            for(int k=0;k<part.Patterns.Length&&k<5;k++)
            {
                var disc=Part("Pattern "+part.Patterns[k].Letter,lane.Root,out var mesh);b.Clear();b.Cylinder(Vector2.zero,1,-.06f,.12f,Plate,Metal*.5f);b.Apply(mesh);
                disc.localRotation=Quaternion.Euler(62,0,0);
                var letter=Label(disc,part.Patterns[k].Letter,.3f,TextAlignmentOptions.Center,InkBright);letter.transform.localPosition=new Vector3(0,-.75f,-.07f);
                lane.Discs.Add(disc);lane.Letters.Add(letter);
            }
            lane.Top=Part("Playing pattern",lane.Root,out lane.TopMesh);lane.Top.localRotation=Quaternion.Euler(62,0,0);
            lane.Token=Label(lane.Root,"",.32f,TextAlignmentOptions.Center,Color.white);
            lane.Name=Label(lane.Root,part.Name,.28f,TextAlignmentOptions.Center,new Color(.7f,.8f,.85f));lane.Name.transform.localPosition=new Vector3(0,-1.25f,0);
            var ringGo=Part("Highlighted",lane.Root,out var ringMeshF);b.Clear();b.Sector(Vector2.zero,1.06f,1.14f,0,1,-.07f,Color.white);b.Apply(ringMeshF);ringGo.localRotation=Quaternion.Euler(62,0,0);lane.Featured=ringGo;
            lanes.Add(lane);
        }
    }
    void Stacks(double beat)
    {
        var feature=main.GetComponent<FeaturedInstrument>();float blend=Main.ReducedMotion?1:1-Mathf.Exp(-Time.unscaledDeltaTime*5);
        foreach(var lane in lanes)
        {
            int play=InstrumentChangers.PlayAt(lane.Part,beat);var current=play>=0?lane.Part.Plays[play]:null;int top=current?.Pattern??-1;
            // The playing pattern rides on top; the others wait below in order.
            int rank=1;
            for(int k=0;k<lane.Discs.Count;k++)
            {
                float target=k==top?0:rank++;lane.Shown[k]=Mathf.Lerp(lane.Shown[k],target,blend);
                bool shown=lane.Shown[k]<3.5f&&k!=top;var disc=lane.Discs[k];if(disc.gameObject.activeSelf!=shown)disc.gameObject.SetActive(shown);
                disc.localPosition=new Vector3(0,-lane.Shown[k]*.22f,lane.Shown[k]*.05f);
            }
            if(play!=lane.Play){lane.Play=play;BuildTop(lane,current);}
            lane.Top.gameObject.SetActive(current!=null&&top>=0);
            if(current!=null&&top>=0)
            {
                double phase=InstrumentChangers.LoopPhase(lane.Part,current,beat);
                lane.Top.localRotation=Quaternion.Euler(62,0,0)*Quaternion.AngleAxis((float)(phase*360),Vector3.forward);
                lane.Top.localPosition=new Vector3(0,.06f,0);
                string token=InstrumentChangers.Token(lane.Part,current)+(current.Run>1?$"  {current.Repeat}/{current.Run}":"");
                if(lane.Token.TextField.text!=token)lane.Token.Text=token;
                lane.Token.transform.localPosition=new Vector3(0,.55f,-.3f);
            }
            else if(lane.Token.TextField.text!="")lane.Token.Text="";
            bool featured=feature!=null&&feature.Track==lane.Part.Track&&feature.Channel==lane.Part.Channel;
            if(lane.Featured.gameObject.activeSelf!=featured)lane.Featured.gameObject.SetActive(featured);
            lane.Featured.localPosition=lane.Top.localPosition;
        }
    }
    void BuildTop(LaneView lane,PreparedPatternSong.LanePlay play)
    {
        b.Clear();
        if(play!=null&&play.Pattern>=0&&play.Pattern<lane.Part.Patterns.Length)
        {
            var pattern=lane.Part.Patterns[play.Pattern];double loop=Math.Max(.25,pattern.LoopBeats);
            b.Cylinder(Vector2.zero,1,-.06f,.12f,Plate*1.4f,Metal*.7f);
            foreach(var chord in pattern.Loop)
            {
                var shifted=new SongFormAnalysis.ChordStep{Root=chord.Rest?-1:HarmonyModel.Mod(chord.Root+play.Transpose),Quality=chord.Quality};
                b.Sector(Vector2.zero,.8f,.97f,chord.Start/loop,chord.End/loop,-.07f,CyclicOrrery.ChordColor(shifted,main.currentKey));
            }
            // The lane's notes in one loop of this play as dimples: radius is pitch.
            double end=Math.Min(play.End,play.Start+loop);
            foreach(var n in lane.Notes)
            {
                if(n.Beat<play.Start-1e-6)continue;if(n.Beat>=end)break;
                double at=((play.Offset+n.Beat-play.Start)/loop%1+1)%1;float r=Mathf.Lerp(.25f,.72f,Mathf.InverseLerp(lane.Low,lane.High,n.Pitch));
                var p=Builder.At(Vector2.zero,r,-at,-.075f);b.Disc(new Vector2(p.x,p.y),.035f,-.075f,InkBright);
            }
        }
        b.Apply(lane.TopMesh);
    }

    void OnDestroy(){foreach(var m in new[]{solid,glow})if(m!=null)Destroy(m);}

    // ---------- mesh building: phase 0 at twelve o'clock, clockwise as the viewer sees it (viewer at −z) ----------
    sealed class Builder
    {
        readonly List<Vector3> v=new();readonly List<Color> c=new();readonly List<int> t=new();
        public void Clear(){v.Clear();c.Clear();t.Clear();}
        public static Vector3 At(Vector2 centre,float r,double phase,float z){float a=(float)(phase*2*Math.PI);return new Vector3(centre.x+Mathf.Sin(a)*r,centre.y+Mathf.Cos(a)*r,z);}
        public void Quad(Vector3 a,Vector3 b,Vector3 d,Vector3 e,Color color){int n=v.Count;v.Add(a);v.Add(b);v.Add(d);v.Add(e);c.Add(color);c.Add(color);c.Add(color);c.Add(color);t.Add(n);t.Add(n+1);t.Add(n+2);t.Add(n);t.Add(n+2);t.Add(n+3);}
        static int Segments(double span,float r)=>Mathf.Clamp((int)Math.Ceiling(span*Mathf.Max(24,r*96)),1,128);
        public void Sector(Vector2 centre,float inner,float outer,double from,double to,float z,Color color)
        {
            if(to-from<1e-6)return;int s=Segments(to-from,outer);
            for(int i=0;i<s;i++){double p0=from+(to-from)*i/s,p1=from+(to-from)*(i+1)/s;Quad(At(centre,inner,p0,z),At(centre,outer,p0,z),At(centre,outer,p1,z),At(centre,inner,p1,z),color);}
        }
        // A solid ring sector: front face at z, extending back by depth, with its outer and inner walls.
        public void Slab(Vector2 centre,float inner,float outer,double from,double to,float z,float depth,Color face,Color side)
        {
            if(to-from<1e-6)return;Sector(centre,inner,outer,from,to,z,face);int s=Segments(to-from,outer);
            for(int i=0;i<s;i++)
            {
                double p0=from+(to-from)*i/s,p1=from+(to-from)*(i+1)/s;
                Quad(At(centre,outer,p0,z),At(centre,outer,p0,z+depth),At(centre,outer,p1,z+depth),At(centre,outer,p1,z),side);
                if(inner>0)Quad(At(centre,inner,p0,z),At(centre,inner,p1,z),At(centre,inner,p1,z+depth),At(centre,inner,p0,z+depth),side*.7f);
            }
        }
        // A family's hatch in relief: alternating stripes across the sector, dashed edges when heard once.
        public void Striped(Vector2 centre,float inner,float outer,double from,double to,float z,float depth,Color light,Color dark,double period,bool solidEdge)
        {
            if(to-from<1e-6)return;
            Slab(centre,inner,outer,from,to,z*.6f,depth-z*.4f,dark,dark*.7f);
            for(double a=from;a<to;a+=period*2)Sector(centre,inner+.004f,outer-.004f,a,Math.Min(to,a+period),z,light);
            if(solidEdge){Sector(centre,outer-.006f,outer,from,to,z-.002f,light);Sector(centre,inner,inner+.006f,from,to,z-.002f,light);}
            else for(double a=from;a<to;a+=period*3){Sector(centre,outer-.006f,outer,a,Math.Min(to,a+period*1.5),z-.002f,light);}
        }
        public void Disc(Vector2 centre,float r,float z,Color color)=>Sector(centre,0,r,0,1,z,color);
        public void Cylinder(Vector2 centre,float r,float z,float depth,Color face,Color side)=>Slab(centre,0,r,0,1,z,depth,face,side);
        public void Tooth(Vector2 centre,float inner,float outer,double phase,double halfWidth,float z,float depth,Color color)=>Slab(centre,inner,outer,phase-halfWidth,phase+halfWidth,z,depth,color,color*.7f);
        public void Box(Vector3 min,Vector3 max,Color color)
        {
            Quad(new(min.x,min.y,min.z),new(min.x,max.y,min.z),new(max.x,max.y,min.z),new(max.x,min.y,min.z),color);
            var side=color*.7f;side.a=color.a;
            Quad(new(min.x,min.y,min.z),new(min.x,min.y,max.z),new(min.x,max.y,max.z),new(min.x,max.y,min.z),side);
            Quad(new(max.x,min.y,min.z),new(max.x,max.y,min.z),new(max.x,max.y,max.z),new(max.x,min.y,max.z),side);
            Quad(new(min.x,max.y,min.z),new(min.x,max.y,max.z),new(max.x,max.y,max.z),new(max.x,max.y,min.z),side);
        }
        // A thin tube around the centre (an orbit's carrier).
        public void Torus(float radius,float tube,Color color)
        {
            const int around=96,sides=6;int n0=v.Count;
            for(int i=0;i<=around;i++)for(int j=0;j<sides;j++)
            {
                float a=i*2*Mathf.PI/around,q=j*2*Mathf.PI/sides;var dir=new Vector3(Mathf.Sin(a),Mathf.Cos(a),0);
                v.Add(dir*(radius+Mathf.Cos(q)*tube)+Vector3.forward*Mathf.Sin(q)*tube);c.Add(color*(.7f+.3f*Mathf.Cos(q)));
            }
            for(int i=0;i<around;i++)for(int j=0;j<sides;j++)
            {
                int a=n0+i*sides+j,bb=n0+i*sides+(j+1)%sides,d=n0+(i+1)*sides+j,e=n0+(i+1)*sides+(j+1)%sides;
                t.Add(a);t.Add(d);t.Add(bb);t.Add(bb);t.Add(d);t.Add(e);
            }
        }
        public void Apply(Mesh mesh)
        {
            for(int i=0;i<c.Count;i++){var k=c[i];k.a=1;c[i]=k;}
            mesh.Clear();mesh.SetVertices(v);mesh.SetColors(c);mesh.SetTriangles(t,0,true);
        }
    }
}
