using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// The drum wheel speaks the pattern wheel's language. Drums carry no tonality, so it is all
// neutral ink; colour stays reserved for chord function. Each disc is a groove family (one
// notated measure per revolution) and its rim wears the hatch of the section family it mostly
// plays in, so the chorus groove slides in with the chorus's texture. A groove's first bar is
// its fundamental: fundamental strikes left out of this bar stay as faint ghosts, and strikes
// the fundamental does not have are ringed and spark when hit, as the moon sparks on a
// changed chord.
public sealed class DrumPatternDeck : MonoBehaviour
{
    MidiPlayer midi;Main main;PreparedPatternSong source;Transform deck;Material material,discMaterial;Mesh discMesh;
    public Transform WheelTransform=>deck;
    public int CurrentCounts {get;private set;}
    public int CurrentFamily {get;private set;}=-1;
    public int CurrentVariant {get;private set;}
    public double CurrentBarLength {get;private set;}
    // Strikes in the current bar that its groove's fundamental does not have.
    public int CurrentVariationHits {get;private set;}
    // The playing groove's form: the hatch and short name of the section family it belongs to.
    public FormHatch.Pattern CurrentHatch=>grooveForm.TryGetValue(CurrentFamily,out var form)?form.hatch:FormHatch.Pattern.Ticks;
    public string CurrentGrooveSection=>grooveForm.TryGetValue(CurrentFamily,out var form)?form.name:"";
    public bool StackMoving=>views.Any(v=>Mathf.Abs(v.Root.localPosition.y-v.TargetY)>.002f);
    readonly List<DiscView> views=new();readonly List<LineRenderer> waves=new();readonly List<Ripple> ripples=new();
    MidiCycleAnalysis.Hit[] hits=Array.Empty<MidiCycleAnalysis.Hit>();
    readonly Dictionary<int,HashSet<int>> fundamental=new();readonly Dictionary<int,(FormHatch.Pattern hatch,string name)> grooveForm=new();
    readonly Vector3[] points=new Vector3[193];double previous;int nextHit,lastBar=-1;bool wasPlaying;DiscView active;
    // Created on first use: Unity does not allow property blocks in a MonoBehaviour's constructor.
    MaterialPropertyBlock block;
    // The same light, muted teal as the pattern wheel's form ink.
    static readonly Color Rim=new(.26f,.4f,.39f),Groove=new(.06f,.1f,.1f),Lane=new(.3f,.46f,.45f),Needle=new(.44f,.66f,.64f),Count=new(.6f,.78f,.76f),Wave=new(.5f,.74f,.72f);
    const float HatchInner=.98f,HatchOuter=1.12f;
    sealed class DiscView
    {
        public Transform Root;public int Family=-1,Rank;public float TargetY;public PreparedPatternSong.DrumBar Bar;
        public Mesh Hatch;public int HatchFamily=-2;public float HatchInk=-1;public TextBox Label;public string Name="";
        public readonly List<Transform> Pins=new(),Ghosts=new();public readonly List<LineRenderer> Marks=new(),Rings=new();public readonly List<TextBox> Counts=new();
        public readonly List<(int slot,bool variation)> Struck=new();
    }
    sealed class Ripple {public double Start;public int Frequency;public float Velocity,Decay,Radius,Width;public Vector3 Origin;}
    public int VisibleRippleCount=>ripples.Count;
    public static int FrequencyBand(int pitch)=>pitch is 35 or 36?4:pitch is 38 or 39 or 40?18:pitch is 42 or 44 or 46 or 54 or 69 or 70?48:pitch is 49 or 51 or 52 or 53 or 55 or 57 or 59?32:10;
    void OnEnable(){
        if(!Application.isPlaying)return;
        source=null;active=null;lastBar=-1;views.Clear();waves.Clear();ripples.Clear();
        midi=GetComponent<MidiPlayer>();main=GetComponent<Main>();
        deck=new GameObject("Percussion CD changer · one measure per revolution").transform;deck.SetParent(transform,false);deck.localPosition=new Vector3(0,-2.8f,0);deck.localScale=Vector3.one*1.25f;
        material=new Material(Resources.Load<Shader>("HarmonicGlow"));material.SetColor("_BaseColor",Color.white*2);
        discMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit"));discMaterial.SetColor("_BaseColor",new Color(.016f,.017f,.019f));
        var vertices=new List<Vector3>();var triangles=new List<int>();
        for(int i=0;i<=96;i++){float a=i*Mathf.PI*2/96;vertices.Add(new Vector3(Mathf.Sin(a)*.18f,0,Mathf.Cos(a)*.18f));vertices.Add(new Vector3(Mathf.Sin(a)*1.15f,0,Mathf.Cos(a)*1.15f));if(i<96){int n=i*2;triangles.AddRange(new[]{n,n+1,n+3,n,n+3,n+2,n+3,n+1,n,n+2,n+3,n});}}
        discMesh=new Mesh{name="Percussion disc annulus"};discMesh.SetVertices(vertices);discMesh.SetTriangles(triangles,0);discMesh.RecalculateNormals();
        for(int layer=0;layer<4;layer++){
            var root=new GameObject("Sliding rhythm disc").transform;root.SetParent(deck,false);root.localPosition=new Vector3(0,-layer*.28f,0);
            root.gameObject.AddComponent<MeshFilter>().sharedMesh=discMesh;root.gameObject.AddComponent<MeshRenderer>().sharedMaterial=discMaterial;
            var v=new DiscView{Root=root,Rank=layer,TargetY=-layer*.28f};views.Add(v);
            Circle(Line("Disc rim",.009f,root),1.15f,.003f,Rim);
            for(int i=0;i<5;i++)Circle(Line("Disc groove",.005f,root),.19f+i*.17f,.005f,Groove);
            Circle(Line("Kick drum lane",.012f,root),.42f,.012f,Lane);
            var hatch=new GameObject("Groove form hatch");hatch.transform.SetParent(root,false);
            v.Hatch=new Mesh{name="Groove form hatch"};v.Hatch.MarkDynamic();hatch.AddComponent<MeshFilter>().sharedMesh=v.Hatch;hatch.AddComponent<MeshRenderer>().sharedMaterial=material;
            v.Label=TextBox.Create("",TMPro.TextAlignmentOptions.Center);v.Label.transform.SetParent(root,false);v.Label.transform.localPosition=new Vector3(0,.03f,0);v.Label.Size=2.2f;v.Label.Color=Count;v.Label.gameObject.SetActive(false);
        }
        for(int i=0;i<96;i++){var line=Line("Percussion energy ripple",.012f,deck);line.enabled=false;waves.Add(line);}
        var needle=Line("Drum twelve o’clock triangle",.01f,deck);needle.positionCount=4;needle.SetPositions(new[]{new Vector3(-.065f,.025f,1.27f),new Vector3(0,.025f,1.16f),new Vector3(.065f,.025f,1.27f),new Vector3(-.065f,.025f,1.27f)});needle.startColor=needle.endColor=Needle;
    }
    LineRenderer Line(string name,float width,Transform parent){var go=new GameObject(name);go.transform.SetParent(parent,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=material;l.useWorldSpace=false;l.widthMultiplier=width;l.positionCount=points.Length;return l;}
    void Circle(LineRenderer line,float radius,float y,Color hue){for(int i=0;i<points.Length;i++){float a=i*Mathf.PI*2/(points.Length-1);points[i]=new Vector3(Mathf.Sin(a)*radius,y,Mathf.Cos(a)*radius);}line.SetPositions(points);line.startColor=line.endColor=hue;}
    static Vector3 At(float radius,double phase,float y=.02f){float a=(float)phase*Mathf.PI*2;return new Vector3(Mathf.Sin(a)*radius,y,Mathf.Cos(a)*radius);}
    // Strike markers rest in the form teal and flash white when hit.
    void Tint(Renderer renderer,float brightness,float flash=0){block??=new MaterialPropertyBlock();renderer.GetPropertyBlock(block);var ink=Color.Lerp(FormHatch.Ink(1),Color.white,flash);ink.a=1;block.SetColor("_BaseColor",ink*brightness);renderer.SetPropertyBlock(block);}

    // Parallel to the screen and upright: a look-at billboard flips under the Drum view's
    // straight-down camera.
    static void Upright(TextBox label){var camera=Camera.main;if(camera!=null)label.transform.rotation=camera.transform.rotation;}

    void Load(){
        source=midi.Prepared;source?.EnsurePatterns();
        hits=source?.Notes.Where(h=>h.Channel==10).OrderBy(h=>h.Beat).ToArray()??Array.Empty<MidiCycleAnalysis.Hit>();nextHit=0;previous=-1;lastBar=-1;active=null;ripples.Clear();
        fundamental.Clear();grooveForm.Clear();
        if(source?.DrumBars!=null)
            foreach(var group in source.DrumBars.Where(b=>b.Family>=0).GroupBy(b=>b.Family))
            {
                // The groove's first bar is its fundamental; later bars add variation slots.
                fundamental[group.Key]=group.First().Slots.ToHashSet();
                // A groove belongs to the section family it plays in most.
                var home=group.Select(b=>source.Sections?.LastOrDefault(s=>s.Start<=b.Start+1e-6)).Where(s=>s!=null).GroupBy(s=>s.Family).OrderByDescending(g=>g.Count()).ThenBy(g=>g.First().Start).FirstOrDefault()?.Key;
                int index=home==null?-1:Array.FindIndex(source.Patterns,p=>p.Family==home.Value);
                grooveForm[group.Key]=index<0?(FormHatch.Pattern.Ticks,""):(FormHatch.ForRole(source.Patterns[index].Role,index),source.Patterns[index].Short);
            }
        foreach(var view in views){view.Family=-1;view.Bar=null;view.HatchFamily=-2;view.Hatch.Clear();view.Label.gameObject.SetActive(false);
            foreach(var pin in view.Pins)pin.gameObject.SetActive(false);foreach(var ghost in view.Ghosts)ghost.gameObject.SetActive(false);
            foreach(var mark in view.Marks)mark.enabled=false;foreach(var ring in view.Rings)ring.enabled=false;foreach(var count in view.Counts)count.gameObject.SetActive(false);}
        if(source?.DrumBars!=null){int index=0;foreach(var bar in source.DrumBars.Where(b=>b.Family>=0).GroupBy(b=>b.Family).Select(g=>g.First()).Take(views.Count)){var view=views[index++];view.Family=bar.Family;view.Bar=bar;Prepare(view);}}
    }
    void Select(PreparedPatternSong.DrumBar bar){
        CurrentCounts=bar.Numerator;CurrentBarLength=bar.End-bar.Start;CurrentVariant=bar.Variant;CurrentFamily=bar.Family;
        if(bar.Family<0){CurrentVariationHits=0;if(active!=null){active.Bar=bar;Prepare(active);}return;}
        var next=views.FirstOrDefault(v=>v.Family==bar.Family);
        if(next==null){next=views.OrderByDescending(v=>v.Rank).First();next.Family=bar.Family;foreach(var pin in next.Pins)pin.gameObject.SetActive(false);}
        if(next!=active){foreach(var v in views.Where(v=>v!=next).OrderBy(v=>v.Rank).Select((v,i)=>(v,i))){v.v.Rank=v.i+1;v.v.TargetY=-v.v.Rank*.28f;}next.Rank=0;next.TargetY=0;active=next;}
        next.Bar=bar;Prepare(next);
        CurrentVariationHits=next.Struck.Count(s=>s.variation);
        foreach(var waiting in views.Where(v=>v!=active)){
            foreach(var pin in waiting.Pins){pin.localScale=Vector3.one*.02f;Tint(pin.GetComponent<Renderer>(),.16f);}
            foreach(var ring in waiting.Rings)ring.enabled=false;foreach(var ghost in waiting.Ghosts)ghost.gameObject.SetActive(false);
        }
    }
    // The groove's form texture on the disc rim: one mesh of soft strokes, the same hatch
    // generator the pattern wheel draws with.
    void BuildHatch(DiscView view){
        if(view.HatchFamily==view.Family)return;view.HatchFamily=view.Family;view.HatchInk=-1;view.Hatch.Clear();
        if(!grooveForm.TryGetValue(view.Family,out var form)){view.Name="";view.Label.gameObject.SetActive(false);return;}
        var lines=new List<(float s0,float v0,float s1,float v1)>();var dots=new List<(float s,float v)>();
        float mid=(HatchInner+HatchOuter)/2,circumference=2*Mathf.PI*mid,width=.005f;
        FormHatch.Generate(form.hatch,0,circumference,HatchInner,HatchOuter,.09f,lines,dots);
        foreach(var (s,v) in dots)lines.Add((s-.008f,v,s+.008f,v));
        Vector3 P(float s,float v)=>At(v,s/circumference,.018f);
        var vertices=new List<Vector3>();var uv=new List<Vector2>();var triangles=new List<int>();
        void Stroke(Vector3 a,Vector3 b){var d=b-a;if(d.sqrMagnitude<1e-10)return;var n=new Vector3(-d.z,0,d.x).normalized*width;int k=vertices.Count;
            vertices.Add(a-n);vertices.Add(a+n);vertices.Add(b+n);vertices.Add(b-n);uv.Add(new Vector2(0,0));uv.Add(new Vector2(0,1));uv.Add(new Vector2(1,1));uv.Add(new Vector2(1,0));
            triangles.AddRange(new[]{k,k+1,k+2,k,k+2,k+3});}
        foreach(var (s0,v0,s1,v1) in lines)Stroke(P(s0,v0),P(s1,v1));
        // Solid edges: a groove that recurs; the dashed look belongs to material heard once.
        for(int i=0;i<96;i++){double a=i/96.0,b=(i+1)/96.0;Stroke(At(HatchInner,a,.018f),At(HatchInner,b,.018f));Stroke(At(HatchOuter,a,.018f),At(HatchOuter,b,.018f));}
        view.Hatch.SetVertices(vertices);view.Hatch.SetUVs(0,uv);view.Hatch.SetTriangles(triangles,0);view.Hatch.RecalculateBounds();
        view.Name=form.name;view.Label.Text=form.name;view.Label.gameObject.SetActive(form.name.Length>0);
    }
    void InkHatch(DiscView view,float ink){
        if(Mathf.Abs(view.HatchInk-ink)<.005f||view.Hatch.vertexCount==0)return;view.HatchInk=ink;
        var ink3=FormHatch.Ink(1)*ink;ink3.a=1;var colors=new Color[view.Hatch.vertexCount];for(int i=0;i<colors.Length;i++)colors[i]=ink3;view.Hatch.colors=colors;
        view.Label.Color=new Color(Count.r,Count.g,Count.b,Mathf.Clamp01(ink*1.3f));
    }
    void Prepare(DiscView view){
        var bar=view.Bar;int slots=bar.Family<0?0:source.DrumFamilies[bar.Family].Slots.Length;
        BuildHatch(view);
        while(view.Pins.Count<slots){var pin=GameObject.CreatePrimitive(PrimitiveType.Sphere);pin.SetActive(false);Destroy(pin.GetComponent<Collider>());pin.name="Drum variation slot";pin.transform.SetParent(view.Root,false);pin.transform.localScale=Vector3.one*.025f;pin.GetComponent<Renderer>().sharedMaterial=material;Tint(pin.GetComponent<Renderer>(),.16f);view.Pins.Add(pin.transform);pin.SetActive(false);}
        var wasActive=view.Pins.Select(p=>p.gameObject.activeSelf).ToArray();
        foreach(var pin in view.Pins)pin.gameObject.SetActive(false);
        view.Struck.Clear();var core=bar.Family>=0&&fundamental.TryGetValue(bar.Family,out var set)?set:null;
        for(int i=0;i<bar.Hits.Length;i++){var hit=bar.Hits[i];int slot=bar.Slots[i];var pin=view.Pins[slot];if(!wasActive[slot])pin.localPosition=At(hit.StrikeRadius,hit.Beat/(bar.End-bar.Start),.025f);pin.localScale=Vector3.one*.025f*Mathf.Sqrt(Mathf.Clamp01(hit.Velocity));Tint(pin.GetComponent<Renderer>(),.16f);pin.gameObject.SetActive(true);view.Struck.Add((slot,core!=null&&!core.Contains(slot)));}
        // Variation strikes are ringed; fundamental strikes this bar leaves out are ghosts.
        int variations=view.Struck.Count(s=>s.variation);
        while(view.Rings.Count<variations){var ring=Line("Groove variation ring",.006f,view.Root);ring.positionCount=25;ring.enabled=false;view.Rings.Add(ring);}
        for(int i=0,r=0;i<bar.Hits.Length;i++){if(!view.Struck[i].variation)continue;var ring=view.Rings[r++];var hit=bar.Hits[i];var c=At(hit.StrikeRadius,hit.Beat/(bar.End-bar.Start),.026f);
            for(int k=0;k<25;k++){float a=k*Mathf.PI*2/24;ring.SetPosition(k,c+new Vector3(Mathf.Sin(a),0,Mathf.Cos(a))*.05f);}ring.startColor=ring.endColor=Count*.6f;ring.enabled=true;}
        for(int r=variations;r<view.Rings.Count;r++)view.Rings[r].enabled=false;
        var missing=core==null?new List<int>():core.Where(slot=>!bar.Slots.Contains(slot)).ToList();
        while(view.Ghosts.Count<missing.Count){var ghost=GameObject.CreatePrimitive(PrimitiveType.Sphere);Destroy(ghost.GetComponent<Collider>());ghost.name="Groove fundamental ghost";ghost.transform.SetParent(view.Root,false);ghost.transform.localScale=Vector3.one*.013f;ghost.GetComponent<Renderer>().sharedMaterial=material;Tint(ghost.GetComponent<Renderer>(),.09f);view.Ghosts.Add(ghost.transform);}
        for(int g=0;g<view.Ghosts.Count;g++){bool on=g<missing.Count;view.Ghosts[g].gameObject.SetActive(on);if(!on)continue;var slot=source.DrumFamilies[bar.Family].Slots[missing[g]];view.Ghosts[g].localPosition=At(slot.StrikeRadius,slot.Beat/(bar.End-bar.Start),.025f);}
        // Counts sit outside the hatch: a full spoke on the downbeat, short ticks on the others.
        var kickPhases=bar.Hits.Where(h=>h.Pitch is 35 or 36).Select(h=>h.Beat/(bar.End-bar.Start)).ToArray();int marks=bar.Numerator+kickPhases.Length;
        while(view.Marks.Count<marks)view.Marks.Add(Line("Count / kick marker",.008f,view.Root));
        for(int i=0;i<view.Marks.Count;i++){var line=view.Marks[i];line.enabled=i<marks;if(!line.enabled)continue;bool count=i<bar.Numerator;double phase=count?i/(double)bar.Numerator:kickPhases[i-bar.Numerator];line.positionCount=2;line.SetPositions(new[]{At(count?(i==0?.2f:HatchOuter):.34f,phase),At(count?1.2f:.5f,phase)});line.startColor=line.endColor=count&&i==0?Needle*.8f:Rim;}
        while(view.Counts.Count<bar.Numerator){var text=TextBox.Create((view.Counts.Count+1).ToString(),TMPro.TextAlignmentOptions.Center);text.transform.SetParent(view.Root,false);text.Size=1.2f;text.Color=Count;view.Counts.Add(text);}
        for(int i=0;i<view.Counts.Count;i++){view.Counts[i].gameObject.SetActive(i<bar.Numerator);if(i<bar.Numerator)view.Counts[i].transform.localPosition=At(1.29f,i/(double)bar.Numerator,.025f);}
    }
    void Update(){
        if(deck==null)return;
        float unfold=main!=null?main.UncoilAmount:0;
        deck.localPosition=Vector3.Lerp(new Vector3(0,-2.8f,0),new Vector3(0,0,.08f),unfold);
        deck.localRotation=Quaternion.Slerp(Quaternion.identity,Quaternion.Euler(-90,0,0),unfold);
        deck.localScale=Vector3.one*Mathf.Lerp(1.25f,.43f,unfold);
        if(midi==null)midi=GetComponent<MidiPlayer>();
        if(midi==null||midi.Cycles==null||midi.Prepared==null){deck.gameObject.SetActive(false);source=null;return;}
        if(source!=midi.Prepared)Load();bool ready=source?.DrumBars?.Length>0&&hits.Length>0;deck.gameObject.SetActive(ready);if(!ready)return;
        double beat=midi.Cycles.BeatAt(midi.ScorePosition),now=midi.ScorePosition;
        int barIndex=Array.FindLastIndex(source.DrumBars,b=>b.Start<=beat);barIndex=Math.Max(0,barIndex);var bar=source.DrumBars[barIndex];
        if(barIndex!=lastBar){Select(bar);lastBar=barIndex;}
        foreach(var view in views){view.Root.localPosition=Vector3.Lerp(view.Root.localPosition,new Vector3(0,view.TargetY,0),1-Mathf.Exp(-Time.unscaledDeltaTime*9));
            // Only the playing disc is labelled: counts and names of the stacked discs below would
            // read through it as a second, rotated clock.
            bool on=view==active;InkHatch(view,on?.42f:.12f);
            for(int i=0;i<view.Counts.Count;i++){var label=view.Counts[i];bool show=on&&view.Bar!=null&&i<view.Bar.Numerator;if(label.gameObject.activeSelf!=show)label.gameObject.SetActive(show);if(show)Upright(label);}
            bool named=on&&view.Name.Length>0;if(view.Label.gameObject.activeSelf!=named)view.Label.gameObject.SetActive(named);if(named)Upright(view.Label);}
        if(active!=null&&active.Bar==bar){active.Root.localRotation=Quaternion.Euler(0,-(float)((beat-bar.Start)/(bar.End-bar.Start))*360,0);
            for(int i=0,r=0;i<bar.Hits.Length;i++){var hit=bar.Hits[i];var pin=active.Pins[bar.Slots[i]];var target=At(hit.StrikeRadius,hit.Beat/(bar.End-bar.Start),.025f);pin.localPosition=Vector3.Lerp(pin.localPosition,target,1-Mathf.Exp(-Time.unscaledDeltaTime*14));double elapsed=now-midi.Cycles.SecondsAt(bar.Start+hit.Beat);float energy=midi.IsPlaying&&elapsed>=0?(float)Math.Exp(-elapsed*12):0;pin.localScale=Vector3.one*(.025f+.05f*energy)*Mathf.Sqrt(hit.Velocity);Tint(pin.GetComponent<Renderer>(),.3f+energy*1.8f,energy);
                // A variation strike sparks its ring, as a changed chord sparks the moon's contact.
                if(i<active.Struck.Count&&active.Struck[i].variation&&r<active.Rings.Count){var ring=active.Rings[r++];ring.startColor=ring.endColor=Count*(.6f+energy*2.4f);ring.widthMultiplier=.006f*(1+energy*1.5f);}}}
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
    void OnDisable(){if(material!=null)Destroy(material);if(discMaterial!=null)Destroy(discMaterial);if(discMesh!=null)Destroy(discMesh);foreach(var view in views)if(view.Hatch!=null)Destroy(view.Hatch);if(deck!=null){deck.gameObject.SetActive(false);Destroy(deck.gameObject);}deck=null;source=null;active=null;views.Clear();waves.Clear();ripples.Clear();}
}
