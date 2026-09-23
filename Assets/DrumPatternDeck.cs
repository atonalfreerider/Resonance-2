using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// The drum wheel is a music-box disc: a steel plate with a dimple wherever the groove strikes,
// turning once per notated measure under a comb at twelve o'clock. Radial lines mark the beats
// (brightest on the downbeat) and fainter, shorter lines the upbeats between them. Each drum
// voice has its own engraved track (kick innermost, cymbals near the rim). Drums carry no
// tonality, so everything is the light muted teal of the form ink; colour stays reserved for
// chord function. Each disc is a groove family and the stack slides the playing groove up; the
// hub names the section it mostly plays in. A groove's first bar is its fundamental: its strikes
// left out of this bar remain as faint empty dimples, and strikes it does not have are ringed
// and spark when hit.
public sealed class DrumPatternDeck : MonoBehaviour
{
    MidiPlayer midi;Main main;PreparedPatternSong source;Transform deck;Material material,discMaterial;Mesh discMesh,detailMesh,dimpleMesh;
    public Transform WheelTransform=>deck;
    public int CurrentCounts {get;private set;}
    public int CurrentFamily {get;private set;}=-1;
    public int CurrentVariant {get;private set;}
    public double CurrentBarLength {get;private set;}
    // Strikes in the current bar that its groove's fundamental does not have.
    public int CurrentVariationHits {get;private set;}
    // Short name of the section family the playing groove belongs to ("V", "C").
    public string CurrentGrooveSection=>grooveName.TryGetValue(CurrentFamily,out var name)?name:"";
    public bool StackMoving=>views.Any(v=>Mathf.Abs(v.Root.localPosition.y-v.TargetY)>.002f);
    readonly List<DiscView> views=new();readonly List<LineRenderer> waves=new();readonly List<Ripple> ripples=new();
    MidiCycleAnalysis.Hit[] hits=Array.Empty<MidiCycleAnalysis.Hit>();
    readonly Dictionary<int,HashSet<int>> fundamental=new();readonly Dictionary<int,string> grooveName=new();
    readonly Vector3[] points=new Vector3[193];double previous;int nextHit,lastBar=-1;bool wasPlaying;DiscView active;
    // Created on first use: Unity does not allow property blocks in a MonoBehaviour's constructor.
    MaterialPropertyBlock block;
    // Muted teal steel: the same ink as the pattern wheel's form marks.
    // A dark plate: where it passes behind the translucent torus it recedes instead of glowing through.
    static readonly Color Plate=new(.022f,.03f,.03f),Brush=new(.03f,.045f,.045f),Track=new(.12f,.18f,.18f),Rim=new(.32f,.48f,.47f),
        Beat=new(.36f,.54f,.53f),Upbeat=new(.2f,.3f,.3f),Needle=new(.5f,.74f,.72f),Count=new(.6f,.78f,.76f),Wave=new(.5f,.74f,.72f);
    const float Hub=.16f,Edge=1.15f;
    // Low enough that the torus hides only the plate's far edge, leaving the struck dimples in view.
    public const float DeckDepth=4.3f;
    // Drawn after the torus and its depth occluder, so the torus hides the plate behind it.
    const int PlateQueue=3100,GlowQueue=3101;
    sealed class DiscView
    {
        public Transform Root;public int Family=-1,Rank;public float TargetY;public PreparedPatternSong.DrumBar Bar;
        public Renderer Detail;public TextBox Label;public string Name="";
        public readonly List<Transform> Dimples=new(),Ghosts=new();public readonly List<LineRenderer> Beats=new(),Rings=new();public readonly List<TextBox> Counts=new();
        public readonly List<(int slot,bool variation)> Struck=new();
    }
    sealed class Ripple {public double Start;public int Frequency;public float Velocity,Decay,Radius,Width;public Vector3 Origin;}
    public int VisibleRippleCount=>ripples.Count;
    public static int FrequencyBand(int pitch)=>pitch is 35 or 36?4:pitch is 38 or 39 or 40?18:pitch is 42 or 44 or 46 or 54 or 69 or 70?48:pitch is 49 or 51 or 52 or 53 or 55 or 57 or 59?32:10;
    void OnEnable(){
        if(!Application.isPlaying)return;
        source=null;active=null;lastBar=-1;views.Clear();waves.Clear();ripples.Clear();
        midi=GetComponent<MidiPlayer>();main=GetComponent<Main>();
        deck=new GameObject("Percussion CD changer · one measure per revolution").transform;deck.SetParent(transform,false);deck.localPosition=new Vector3(0,-DeckDepth,0);deck.localScale=Vector3.one*1.25f;
        material=new Material(Resources.Load<Shader>("HarmonicGlow"));material.SetColor("_BaseColor",Color.white*2);material.renderQueue=GlowQueue;
        discMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit"));discMaterial.SetColor("_BaseColor",Plate);discMaterial.renderQueue=PlateQueue;
        var vertices=new List<Vector3>();var triangles=new List<int>();
        for(int i=0;i<=96;i++){float a=i*Mathf.PI*2/96;vertices.Add(new Vector3(Mathf.Sin(a)*Hub,0,Mathf.Cos(a)*Hub));vertices.Add(new Vector3(Mathf.Sin(a)*Edge,0,Mathf.Cos(a)*Edge));if(i<96){int n=i*2;triangles.AddRange(new[]{n,n+1,n+3,n,n+3,n+2,n+3,n+1,n,n+2,n+3,n});}}
        discMesh=new Mesh{name="Music-box steel plate"};discMesh.SetVertices(vertices);discMesh.SetTriangles(triangles,0);discMesh.RecalculateNormals();
        detailMesh=new Mesh{name="Music-box plate engraving"};dimpleMesh=Dimple();
        for(int layer=0;layer<4;layer++){
            var root=new GameObject("Sliding rhythm disc").transform;root.SetParent(deck,false);root.localPosition=new Vector3(0,-layer*.28f,0);
            root.gameObject.AddComponent<MeshFilter>().sharedMesh=discMesh;root.gameObject.AddComponent<MeshRenderer>().sharedMaterial=discMaterial;
            var v=new DiscView{Root=root,Rank=layer,TargetY=-layer*.28f};views.Add(v);
            var detail=new GameObject("Plate engraving");detail.transform.SetParent(root,false);
            detail.AddComponent<MeshFilter>().sharedMesh=detailMesh;v.Detail=detail.AddComponent<MeshRenderer>();v.Detail.sharedMaterial=material;
            v.Label=TextBox.Create("",TMPro.TextAlignmentOptions.Center);Behind(v.Label);v.Label.transform.SetParent(root,false);v.Label.transform.localPosition=new Vector3(0,.03f,0);v.Label.Size=2.2f;v.Label.Color=Count;v.Label.gameObject.SetActive(false);
        }
        for(int i=0;i<96;i++){var line=Line("Percussion energy ripple",.012f,deck);line.enabled=false;waves.Add(line);}
        // The comb at twelve o'clock, where the dimples are struck.
        var needle=Line("Drum twelve o’clock triangle",.01f,deck);needle.positionCount=4;needle.SetPositions(new[]{new Vector3(-.065f,.025f,1.27f),new Vector3(0,.025f,1.16f),new Vector3(.065f,.025f,1.27f),new Vector3(-.065f,.025f,1.27f)});needle.startColor=needle.endColor=Needle;
    }
    LineRenderer Line(string name,float width,Transform parent){var go=new GameObject(name);go.transform.SetParent(parent,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=material;l.useWorldSpace=false;l.widthMultiplier=width;l.positionCount=points.Length;return l;}
    static Vector3 At(float radius,double phase,float y=.02f){float a=(float)phase*Mathf.PI*2;return new Vector3(Mathf.Sin(a)*radius,y,Mathf.Cos(a)*radius);}
    // Strike markers rest in the form teal and flash white when hit.
    void Tint(Renderer renderer,float brightness,float flash=0){block??=new MaterialPropertyBlock();renderer.GetPropertyBlock(block);var ink=Color.Lerp(FormHatch.Ink(1),Color.white,flash);ink.a=1;block.SetColor("_BaseColor",ink*brightness);renderer.SetPropertyBlock(block);}
    static void Behind(TextBox label){label.TextField.fontMaterial.renderQueue=GlowQueue;}
    static void Upright(TextBox label){var camera=Camera.main;if(camera!=null)label.transform.rotation=camera.transform.rotation;}

    // Soft strokes in the plate plane (the glow shader fades across uv.y).
    sealed class Strokes
    {
        public readonly List<Vector3> Vertices=new();public readonly List<Vector2> Uv=new();public readonly List<Color> Colors=new();public readonly List<int> Triangles=new();
        public void Add(Vector3 a,Vector3 b,float width,Color color)
        {
            var d=b-a;if(d.sqrMagnitude<1e-12)return;var n=new Vector3(-d.z,0,d.x).normalized*width;int k=Vertices.Count;
            Vertices.Add(a-n);Vertices.Add(a+n);Vertices.Add(b+n);Vertices.Add(b-n);
            Uv.Add(new Vector2(0,0));Uv.Add(new Vector2(0,1));Uv.Add(new Vector2(1,1));Uv.Add(new Vector2(1,0));
            for(int i=0;i<4;i++)Colors.Add(color);Triangles.AddRange(new[]{k,k+1,k+2,k,k+2,k+3});
        }
        public void Circle(float radius,float width,Color color,float y=.012f,int pieces=120){for(int i=0;i<pieces;i++)Add(At(radius,i/(double)pieces,y),At(radius,(i+1)/(double)pieces,y),width,color);}
        public void Into(Mesh mesh){mesh.Clear();mesh.SetVertices(Vertices);mesh.SetUVs(0,Uv);mesh.SetColors(Colors);mesh.SetTriangles(Triangles,0);mesh.RecalculateBounds();}
    }
    // A dimple (radius 1.9 units, so a 0.025 scale is 0.048 on the plate): a raised lip around a
    // shallow, faintly lit centre.
    static Mesh Dimple()
    {
        var s=new Strokes();
        for(int i=0;i<24;i++){s.Add(At(1.5f,i/24.0,0),At(1.5f,(i+1)/24.0,0),.4f,Color.white);s.Add(At(.55f,i/24.0,0),At(.55f,(i+1)/24.0,0),.45f,Color.white*.4f);}
        var mesh=new Mesh{name="Music-box dimple"};s.Into(mesh);return mesh;
    }
    // Brushed steel, one engraved track per drum voice the song uses, a riveted rim with drive
    // holes, and the spindle hub.
    void Engrave()
    {
        var s=new Strokes();
        for(float r=Hub+.03f;r<Edge-.04f;r+=.045f)s.Circle(r,.0025f,Brush,.01f,96);
        foreach(float r in hits.Select(h=>(float)Math.Round(h.StrikeRadius,3)).Where(r=>r>Hub&&r<Edge).Distinct())s.Circle(r,.004f,Track);
        s.Circle(Edge-.005f,.007f,Rim);s.Circle(Edge-.06f,.003f,Rim*.6f);
        for(int i=0;i<72;i++){double a=(i+.5)/72;s.Add(At(Edge-.035f,a-.0025,.013f),At(Edge-.035f,a+.0025,.013f),.008f,Rim*.7f);}
        s.Circle(Hub,.006f,Rim);s.Circle(Hub*.55f,.004f,Rim*.7f);
        s.Into(detailMesh);
    }

    void Load(){
        source=midi.Prepared;source?.EnsurePatterns();
        hits=source?.Notes.Where(h=>h.Channel==10).OrderBy(h=>h.Beat).ToArray()??Array.Empty<MidiCycleAnalysis.Hit>();nextHit=0;previous=-1;lastBar=-1;active=null;ripples.Clear();
        fundamental.Clear();grooveName.Clear();Engrave();
        if(source?.DrumBars!=null)
            foreach(var group in source.DrumBars.Where(b=>b.Family>=0).GroupBy(b=>b.Family))
            {
                // The groove's first bar is its fundamental; later bars add variation slots.
                fundamental[group.Key]=group.First().Slots.ToHashSet();
                // A groove belongs to the section family it plays in most.
                var home=group.Select(b=>source.Sections?.LastOrDefault(s=>s.Start<=b.Start+1e-6)).Where(s=>s!=null).GroupBy(s=>s.Family).OrderByDescending(g=>g.Count()).ThenBy(g=>g.First().Start).FirstOrDefault()?.Key;
                int index=home==null?-1:Array.FindIndex(source.Patterns,p=>p.Family==home.Value);
                grooveName[group.Key]=index<0?"":source.Patterns[index].Short;
            }
        foreach(var view in views){view.Family=-1;view.Bar=null;view.Name="";view.Label.gameObject.SetActive(false);
            foreach(var dimple in view.Dimples)dimple.gameObject.SetActive(false);foreach(var ghost in view.Ghosts)ghost.gameObject.SetActive(false);
            foreach(var line in view.Beats)line.enabled=false;foreach(var ring in view.Rings)ring.enabled=false;foreach(var count in view.Counts)count.gameObject.SetActive(false);}
        if(source?.DrumBars!=null){int index=0;foreach(var bar in source.DrumBars.Where(b=>b.Family>=0).GroupBy(b=>b.Family).Select(g=>g.First()).Take(views.Count)){var view=views[index++];view.Family=bar.Family;view.Bar=bar;Prepare(view);}}
    }
    void Select(PreparedPatternSong.DrumBar bar){
        CurrentCounts=bar.Numerator;CurrentBarLength=bar.End-bar.Start;CurrentVariant=bar.Variant;CurrentFamily=bar.Family;
        if(bar.Family<0){CurrentVariationHits=0;if(active!=null){active.Bar=bar;Prepare(active);}return;}
        var next=views.FirstOrDefault(v=>v.Family==bar.Family);
        if(next==null){next=views.OrderByDescending(v=>v.Rank).First();next.Family=bar.Family;foreach(var dimple in next.Dimples)dimple.gameObject.SetActive(false);}
        if(next!=active){foreach(var v in views.Where(v=>v!=next).OrderBy(v=>v.Rank).Select((v,i)=>(v,i))){v.v.Rank=v.i+1;v.v.TargetY=-v.v.Rank*.28f;}next.Rank=0;next.TargetY=0;active=next;}
        next.Bar=bar;Prepare(next);
        CurrentVariationHits=next.Struck.Count(s=>s.variation);
        foreach(var waiting in views.Where(v=>v!=active)){
            foreach(var dimple in waiting.Dimples){dimple.localScale=Vector3.one*.02f;Tint(dimple.GetComponent<Renderer>(),.16f);}
            foreach(var ring in waiting.Rings)ring.enabled=false;foreach(var ghost in waiting.Ghosts)ghost.gameObject.SetActive(false);
        }
    }
    Transform NewDimple(Transform parent,string name,float scale,float brightness)
    {
        var go=new GameObject(name);go.transform.SetParent(parent,false);go.transform.localScale=Vector3.one*scale;
        go.AddComponent<MeshFilter>().sharedMesh=dimpleMesh;var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;Tint(renderer,brightness);
        return go.transform;
    }
    void Prepare(DiscView view){
        var bar=view.Bar;int slots=bar.Family<0?0:source.DrumFamilies[bar.Family].Slots.Length;double length=bar.End-bar.Start;
        view.Name=bar.Family>=0&&grooveName.TryGetValue(bar.Family,out var name)?name:"";view.Label.Text=view.Name;
        while(view.Dimples.Count<slots){var dimple=NewDimple(view.Root,"Drum variation slot",.025f,.16f);dimple.gameObject.SetActive(false);view.Dimples.Add(dimple);}
        var wasActive=view.Dimples.Select(p=>p.gameObject.activeSelf).ToArray();
        foreach(var dimple in view.Dimples)dimple.gameObject.SetActive(false);
        view.Struck.Clear();var core=bar.Family>=0&&fundamental.TryGetValue(bar.Family,out var set)?set:null;
        for(int i=0;i<bar.Hits.Length;i++){var hit=bar.Hits[i];int slot=bar.Slots[i];var dimple=view.Dimples[slot];if(!wasActive[slot])dimple.localPosition=At(hit.StrikeRadius,hit.Beat/length,.022f);dimple.localScale=Vector3.one*.025f*Mathf.Sqrt(Mathf.Clamp01(hit.Velocity));Tint(dimple.GetComponent<Renderer>(),.16f);dimple.gameObject.SetActive(true);view.Struck.Add((slot,core!=null&&!core.Contains(slot)));}
        // Variation strikes are ringed; fundamental strikes this bar leaves out stay as empty dimples.
        int variations=view.Struck.Count(s=>s.variation);
        while(view.Rings.Count<variations){var ring=Line("Groove variation ring",.005f,view.Root);ring.positionCount=25;ring.enabled=false;view.Rings.Add(ring);}
        for(int i=0,r=0;i<bar.Hits.Length;i++){if(!view.Struck[i].variation)continue;var ring=view.Rings[r++];var hit=bar.Hits[i];var c=At(hit.StrikeRadius,hit.Beat/length,.024f);
            for(int k=0;k<25;k++){float a=k*Mathf.PI*2/24;ring.SetPosition(k,c+new Vector3(Mathf.Sin(a),0,Mathf.Cos(a))*.05f);}ring.startColor=ring.endColor=Count*.6f;ring.enabled=true;}
        for(int r=variations;r<view.Rings.Count;r++)view.Rings[r].enabled=false;
        var missing=core==null?new List<int>():core.Where(slot=>!bar.Slots.Contains(slot)).ToList();
        while(view.Ghosts.Count<missing.Count)view.Ghosts.Add(NewDimple(view.Root,"Groove fundamental ghost",.018f,.07f));
        for(int g=0;g<view.Ghosts.Count;g++){bool on=g<missing.Count;view.Ghosts[g].gameObject.SetActive(on);if(!on)continue;var slot=source.DrumFamilies[bar.Family].Slots[missing[g]];view.Ghosts[g].localPosition=At(slot.StrikeRadius,slot.Beat/length,.022f);}
        // Radial beat lines: every count from hub to rim, the downbeat brightest; the upbeats
        // between them shorter and fainter.
        int counts=Math.Max(1,bar.Numerator),lines=counts*2;
        while(view.Beats.Count<lines)view.Beats.Add(Line("Beat line",.006f,view.Root));
        for(int i=0;i<view.Beats.Count;i++)
        {
            var line=view.Beats[i];line.enabled=i<lines;if(!line.enabled)continue;
            bool down=i%2==0;int beat=i/2;double phase=(beat+(down?0:.5))/counts;
            line.positionCount=2;line.SetPositions(new[]{At(down?Hub:.46f,phase,.016f),At(down?Edge-.01f:Edge-.07f,phase,.016f)});
            line.widthMultiplier=down?(beat==0?.008f:.006f):.0035f;line.startColor=line.endColor=down?(beat==0?Needle*.8f:Beat):Upbeat;
        }
        while(view.Counts.Count<counts){var text=TextBox.Create((view.Counts.Count+1).ToString(),TMPro.TextAlignmentOptions.Center);Behind(text);text.transform.SetParent(view.Root,false);text.Size=1.2f;text.Color=Count;view.Counts.Add(text);}
        for(int i=0;i<view.Counts.Count;i++){view.Counts[i].gameObject.SetActive(i<counts);if(i<counts)view.Counts[i].transform.localPosition=At(1.29f,i/(double)counts,.025f);}
    }
    void Update(){
        if(deck==null)return;
        float unfold=main!=null?main.UncoilAmount:0;
        deck.localPosition=Vector3.Lerp(new Vector3(0,-DeckDepth,0),new Vector3(0,0,.08f),unfold);
        deck.localRotation=Quaternion.Slerp(Quaternion.identity,Quaternion.Euler(-90,0,0),unfold);
        deck.localScale=Vector3.one*Mathf.Lerp(1.25f,.43f,unfold);
        if(midi==null)midi=GetComponent<MidiPlayer>();
        if(midi==null||midi.Cycles==null||midi.Prepared==null){deck.gameObject.SetActive(false);source=null;return;}
        if(discMaterial.renderQueue!=PlateQueue)discMaterial.renderQueue=PlateQueue;
        if(source!=midi.Prepared)Load();bool ready=source?.DrumBars?.Length>0&&hits.Length>0;deck.gameObject.SetActive(ready);if(!ready)return;
        double beat=midi.Cycles.BeatAt(midi.ScorePosition),now=midi.ScorePosition;
        int barIndex=Array.FindLastIndex(source.DrumBars,b=>b.Start<=beat);barIndex=Math.Max(0,barIndex);var bar=source.DrumBars[barIndex];
        if(barIndex!=lastBar){Select(bar);lastBar=barIndex;}
        foreach(var view in views){view.Root.localPosition=Vector3.Lerp(view.Root.localPosition,new Vector3(0,view.TargetY,0),1-Mathf.Exp(-Time.unscaledDeltaTime*9));
            // Only the playing disc is lit and labelled: the stacked discs below would read
            // through it as a second, rotated clock.
            bool on=view==active;Tint(view.Detail,on?1.4f:.45f);
            for(int i=0;i<view.Beats.Count;i++){bool show=on&&view.Bar!=null&&i<Math.Max(1,view.Bar.Numerator)*2;if(view.Beats[i].enabled!=show)view.Beats[i].enabled=show;}
            for(int i=0;i<view.Counts.Count;i++){var label=view.Counts[i];bool show=on&&view.Bar!=null&&i<view.Bar.Numerator;if(label.gameObject.activeSelf!=show)label.gameObject.SetActive(show);if(show)Upright(label);}
            bool named=on&&view.Name.Length>0;if(view.Label.gameObject.activeSelf!=named)view.Label.gameObject.SetActive(named);if(named)Upright(view.Label);}
        if(active!=null&&active.Bar==bar){active.Root.localRotation=Quaternion.Euler(0,-(float)((beat-bar.Start)/(bar.End-bar.Start))*360,0);
            for(int i=0,r=0;i<bar.Hits.Length;i++){var hit=bar.Hits[i];var dimple=active.Dimples[bar.Slots[i]];var target=At(hit.StrikeRadius,hit.Beat/(bar.End-bar.Start),.022f);dimple.localPosition=Vector3.Lerp(dimple.localPosition,target,1-Mathf.Exp(-Time.unscaledDeltaTime*14));double elapsed=now-midi.Cycles.SecondsAt(bar.Start+hit.Beat);float energy=midi.IsPlaying&&elapsed>=0?(float)Math.Exp(-elapsed*12):0;dimple.localScale=Vector3.one*(.025f+.04f*energy)*Mathf.Sqrt(hit.Velocity);Tint(dimple.GetComponent<Renderer>(),.6f+energy*1.6f,energy);
                // A variation strike sparks its ring, as a changed chord sparks the moon's contact.
                if(i<active.Struck.Count&&active.Struck[i].variation&&r<active.Rings.Count){var ring=active.Rings[r++];ring.startColor=ring.endColor=Count*(.6f+energy*2.4f);ring.widthMultiplier=.005f*(1+energy*1.5f);}}}
        bool jump=now<previous||Math.Abs(now-previous)>.3||(!wasPlaying&&midi.IsPlaying);
        if(jump){ripples.Clear();nextHit=Array.FindIndex(hits,h=>h.Beat>=beat-.00001);if(nextHit<0)nextHit=hits.Length;}
        if(midi.IsPlaying)while(nextHit<hits.Length&&hits[nextHit].Beat<=beat){var hit=hits[nextHit++];ripples.Add(new Ripple{Start=midi.Cycles.SecondsAt(hit.Beat),Frequency=hit.RippleFrequency,Decay=hit.RippleFrequency==4?.24f:hit.DecaySeconds,Velocity=hit.Velocity,Radius=hit.RippleRadius,Width=hit.RippleWidth,Origin=new Vector3(0,.015f,hit.StrikeRadius)});}
        if(!midi.IsPlaying)ripples.Clear();previous=now;wasPlaying=midi.IsPlaying;
        ripples.RemoveAll(r=>now-r.Start>r.Decay*1.3f);
        if(ripples.Count>waves.Count/3)ripples.RemoveRange(0,ripples.Count-waves.Count/3);
        for(int n=0;n<waves.Count;n++)
        {
            var line=waves[n];int hitIndex=n/3,follow=n%3;line.enabled=hitIndex<ripples.Count;if(!line.enabled)continue;
            var wave=ripples[hitIndex];bool kick=wave.Frequency==4;
            if(kick&&follow>0){line.enabled=false;continue;}
            float lag=follow*wave.Decay*.12f,age=(float)(now-wave.Start)-lag;
            if(age<=0){line.enabled=false;continue;}
            float progress=Mathf.Clamp01(age/Mathf.Max(.05f,wave.Decay));
            float envelope=Mathf.Exp(-progress*3)*Mathf.Pow(.38f,follow)*Mathf.Clamp01(age/.015f);
            float radius=wave.Radius*(1-Mathf.Exp(-progress*(kick?4.5f:2.4f)));
            for(int i=0;i<points.Length;i++){
                float a=i*Mathf.PI*2/(points.Length-1);
                float corrugation=Main.ReducedMotion?0:Mathf.Sin(a*wave.Frequency-age*wave.Frequency*2)*Mathf.Min(kick?.008f:.002f,radius*.015f)*envelope;
                float r=radius+corrugation;points[i]=wave.Origin+new Vector3(Mathf.Sin(a)*r,0,Mathf.Cos(a)*r);
            }
            line.SetPositions(points);line.widthMultiplier=wave.Width*(kick?3.2f:.65f)*(1-progress*.6f)*(follow==0?1:.6f);
            line.startColor=line.endColor=Wave*(envelope*wave.Velocity*(kick?2.2f:.65f));
        }
    }
    void OnDisable(){if(material!=null)Destroy(material);if(discMaterial!=null)Destroy(discMaterial);foreach(var mesh in new[]{discMesh,detailMesh,dimpleMesh})if(mesh!=null)Destroy(mesh);if(deck!=null){deck.gameObject.SetActive(false);Destroy(deck.gameObject);}deck=null;source=null;active=null;views.Clear();waves.Clear();ripples.Clear();}
}
