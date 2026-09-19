using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public sealed class DrumPatternDeck : MonoBehaviour
{
    MidiPlayer midi;Main main;PreparedPatternSong source;Transform deck;Material material,discMaterial;Mesh discMesh;
    public Transform WheelTransform=>deck;
    public int CurrentCounts {get;private set;}
    public int CurrentFamily {get;private set;}=-1;
    public int CurrentVariant {get;private set;}
    public double CurrentBarLength {get;private set;}
    public bool StackMoving=>views.Any(v=>Mathf.Abs(v.Root.localPosition.y-v.TargetY)>.002f);
    readonly List<DiscView> views=new();readonly List<LineRenderer> waves=new();readonly List<Ripple> ripples=new();
    MidiCycleAnalysis.Hit[] hits=Array.Empty<MidiCycleAnalysis.Hit>();
    readonly Vector3[] points=new Vector3[193];double previous;int nextHit,lastBar=-1;bool wasPlaying;DiscView active;
    sealed class DiscView {public Transform Root;public int Family=-1,Rank;public float TargetY;public PreparedPatternSong.DrumBar Bar;public readonly List<Transform> Pins=new();public readonly List<LineRenderer> Marks=new();public readonly List<TextBox> Counts=new();}
    sealed class Ripple {public double Start;public int Frequency;public float Velocity,Decay,Radius,Width;public Vector3 Origin;}
    public int VisibleRippleCount=>ripples.Count;
    public static int FrequencyBand(int pitch)=>pitch is 35 or 36?4:pitch is 38 or 39 or 40?18:pitch is 42 or 44 or 46 or 54 or 69 or 70?48:pitch is 49 or 51 or 52 or 53 or 55 or 57 or 59?32:10;
    void OnEnable(){
        if(!Application.isPlaying)return;
        source=null;active=null;lastBar=-1;views.Clear();waves.Clear();ripples.Clear();
        midi=GetComponent<MidiPlayer>();main=GetComponent<Main>();
        deck=new GameObject("Percussion CD changer · one measure per revolution").transform;deck.SetParent(transform,false);deck.localPosition=new Vector3(0,-2.8f,0);deck.localScale=Vector3.one*1.25f;
        material=new Material(Resources.Load<Shader>("HarmonicGlow"));material.SetColor("_BaseColor",Color.white*2);
        discMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit"));discMaterial.SetColor("_BaseColor",new Color(.014f,.022f,.032f));
        var vertices=new List<Vector3>();var triangles=new List<int>();
        for(int i=0;i<=96;i++){float a=i*Mathf.PI*2/96;vertices.Add(new Vector3(Mathf.Sin(a)*.18f,0,Mathf.Cos(a)*.18f));vertices.Add(new Vector3(Mathf.Sin(a)*1.15f,0,Mathf.Cos(a)*1.15f));if(i<96){int n=i*2;triangles.AddRange(new[]{n,n+1,n+3,n,n+3,n+2,n+3,n+1,n,n+2,n+3,n});}}
        discMesh=new Mesh{name="Percussion disc annulus"};discMesh.SetVertices(vertices);discMesh.SetTriangles(triangles,0);discMesh.RecalculateNormals();
        for(int layer=0;layer<4;layer++){
            var root=new GameObject("Sliding rhythm disc").transform;root.SetParent(deck,false);root.localPosition=new Vector3(0,-layer*.28f,0);
            root.gameObject.AddComponent<MeshFilter>().sharedMesh=discMesh;root.gameObject.AddComponent<MeshRenderer>().sharedMaterial=discMaterial;
            var v=new DiscView{Root=root,Rank=layer,TargetY=-layer*.28f};views.Add(v);
            Circle(Line("Disc rim",.014f,root),1.15f,.003f,new Color(.25f,.37f,.48f));
            for(int i=0;i<6;i++)Circle(Line("Disc groove",.007f,root),.19f+i*.17f,.005f,new Color(.1f,.17f,.24f));
            Circle(Line("Kick drum lane",.02f,root),.42f,.012f,new Color(.48f,.58f,.68f));
        }
        for(int i=0;i<96;i++){var line=Line("Percussion energy ripple",.012f,deck);line.enabled=false;waves.Add(line);}
        var needle=Line("Drum twelve o’clock triangle",.014f,deck);needle.positionCount=4;needle.SetPositions(new[]{new Vector3(-.065f,.025f,1.27f),new Vector3(0,.025f,1.16f),new Vector3(.065f,.025f,1.27f),new Vector3(-.065f,.025f,1.27f)});needle.startColor=needle.endColor=new Color(.6f,.7f,.78f);
    }
    LineRenderer Line(string name,float width,Transform parent){var go=new GameObject(name);go.transform.SetParent(parent,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=material;l.useWorldSpace=false;l.widthMultiplier=width;l.positionCount=points.Length;return l;}
    void Circle(LineRenderer line,float radius,float y,Color hue){for(int i=0;i<points.Length;i++){float a=i*Mathf.PI*2/(points.Length-1);points[i]=new Vector3(Mathf.Sin(a)*radius,y,Mathf.Cos(a)*radius);}line.SetPositions(points);line.startColor=line.endColor=hue;}
    static Vector3 At(float radius,double phase,float y=.02f){float a=(float)phase*Mathf.PI*2;return new Vector3(Mathf.Sin(a)*radius,y,Mathf.Cos(a)*radius);}
    void Load(){
        source=midi.Prepared;hits=source?.Notes.Where(h=>h.Channel==10).OrderBy(h=>h.Beat).ToArray()??Array.Empty<MidiCycleAnalysis.Hit>();nextHit=0;previous=-1;lastBar=-1;active=null;ripples.Clear();
        foreach(var view in views){view.Family=-1;view.Bar=null;foreach(var pin in view.Pins)pin.gameObject.SetActive(false);foreach(var mark in view.Marks)mark.enabled=false;foreach(var count in view.Counts)count.gameObject.SetActive(false);}
        if(source?.DrumBars!=null){int index=0;foreach(var bar in source.DrumBars.Where(b=>b.Family>=0).GroupBy(b=>b.Family).Select(g=>g.First()).Take(views.Count)){var view=views[index++];view.Family=bar.Family;view.Bar=bar;Prepare(view);}}
    }
    void Select(PreparedPatternSong.DrumBar bar){
        CurrentCounts=bar.Numerator;CurrentBarLength=bar.End-bar.Start;CurrentVariant=bar.Variant;CurrentFamily=bar.Family;
        if(bar.Family<0){if(active!=null){active.Bar=bar;Prepare(active);}return;}
        var next=views.FirstOrDefault(v=>v.Family==bar.Family);
        if(next==null){next=views.OrderByDescending(v=>v.Rank).First();next.Family=bar.Family;foreach(var pin in next.Pins)pin.gameObject.SetActive(false);}
        if(next!=active){foreach(var v in views.Where(v=>v!=next).OrderBy(v=>v.Rank).Select((v,i)=>(v,i))){v.v.Rank=v.i+1;v.v.TargetY=-v.v.Rank*.28f;}next.Rank=0;next.TargetY=0;active=next;}
        next.Bar=bar;Prepare(next);
        foreach(var waiting in views.Where(v=>v!=active))foreach(var pin in waiting.Pins){pin.localScale=Vector3.one*.02f;var dim=new MaterialPropertyBlock();dim.SetColor("_BaseColor",Color.white*.16f);pin.GetComponent<Renderer>().SetPropertyBlock(dim);}
    }
    void Prepare(DiscView view){
        var bar=view.Bar;int slots=bar.Family<0?0:source.DrumFamilies[bar.Family].Slots.Length;
        while(view.Pins.Count<slots){var pin=GameObject.CreatePrimitive(PrimitiveType.Sphere);pin.SetActive(false);Destroy(pin.GetComponent<Collider>());pin.name="Drum variation slot";pin.transform.SetParent(view.Root,false);pin.transform.localScale=Vector3.one*.025f;pin.GetComponent<Renderer>().sharedMaterial=material;var initial=new MaterialPropertyBlock();initial.SetColor("_BaseColor",Color.white*.16f);pin.GetComponent<Renderer>().SetPropertyBlock(initial);view.Pins.Add(pin.transform);pin.SetActive(false);}
        var wasActive=view.Pins.Select(p=>p.gameObject.activeSelf).ToArray();
        foreach(var pin in view.Pins)pin.gameObject.SetActive(false);
        for(int i=0;i<bar.Hits.Length;i++){var hit=bar.Hits[i];var pin=view.Pins[bar.Slots[i]];if(!wasActive[bar.Slots[i]])pin.localPosition=At(hit.StrikeRadius,hit.Beat/(bar.End-bar.Start),.025f);pin.localScale=Vector3.one*.025f*Mathf.Sqrt(Mathf.Clamp01(hit.Velocity));var initial=new MaterialPropertyBlock();initial.SetColor("_BaseColor",Color.white*.16f);pin.GetComponent<Renderer>().SetPropertyBlock(initial);pin.gameObject.SetActive(true);}
        var kickPhases=bar.Hits.Where(h=>h.Pitch is 35 or 36).Select(h=>h.Beat/(bar.End-bar.Start)).ToArray();int marks=bar.Numerator+kickPhases.Length;
        while(view.Marks.Count<marks)view.Marks.Add(Line("Count / kick marker",.013f,view.Root));
        for(int i=0;i<view.Marks.Count;i++){var line=view.Marks[i];line.enabled=i<marks;if(!line.enabled)continue;bool count=i<bar.Numerator;double phase=count?i/(double)bar.Numerator:kickPhases[i-bar.Numerator];line.positionCount=2;line.SetPositions(new[]{At(count?(i==0?.2f:1.02f):.34f,phase),At(count?1.15f:.5f,phase)});line.startColor=line.endColor=count&&i==0?new Color(.55f,.64f,.72f):new Color(.3f,.4f,.5f);}
        while(view.Counts.Count<bar.Numerator){var text=TextBox.Create((view.Counts.Count+1).ToString(),TMPro.TextAlignmentOptions.Center);text.transform.SetParent(view.Root,false);text.Size=1.2f;text.Color=new Color(.7f,.77f,.83f);view.Counts.Add(text);}
        for(int i=0;i<view.Counts.Count;i++){view.Counts[i].gameObject.SetActive(i<bar.Numerator);if(i<bar.Numerator)view.Counts[i].transform.localPosition=At(1.24f,i/(double)bar.Numerator,.025f);}
    }
    void Update(){
        if(deck==null)return;if(midi==null)midi=GetComponent<MidiPlayer>();
        if(midi==null||midi.Cycles==null||midi.Prepared==null){deck.gameObject.SetActive(false);source=null;return;}
        if(source!=midi.Prepared)Load();bool ready=source?.DrumBars?.Length>0&&hits.Length>0;deck.gameObject.SetActive(ready);if(!ready)return;
        double beat=midi.Cycles.BeatAt(midi.ScorePosition),now=midi.ScorePosition;
        int barIndex=Array.FindLastIndex(source.DrumBars,b=>b.Start<=beat);barIndex=Math.Max(0,barIndex);var bar=source.DrumBars[barIndex];
        if(barIndex!=lastBar){Select(bar);lastBar=barIndex;}
        foreach(var view in views){view.Root.localPosition=Vector3.Lerp(view.Root.localPosition,new Vector3(0,view.TargetY,0),1-Mathf.Exp(-Time.unscaledDeltaTime*9));foreach(var label in view.Counts)if(label.gameObject.activeSelf)label.Billboard();}
        if(active!=null){active.Root.localRotation=Quaternion.Euler(0,-(float)((beat-bar.Start)/(bar.End-bar.Start))*360,0);var block=new MaterialPropertyBlock();
            for(int i=0;i<bar.Hits.Length;i++){var hit=bar.Hits[i];var pin=active.Pins[bar.Slots[i]];var target=At(hit.StrikeRadius,hit.Beat/(bar.End-bar.Start),.025f);pin.localPosition=Vector3.Lerp(pin.localPosition,target,1-Mathf.Exp(-Time.unscaledDeltaTime*14));double elapsed=now-midi.Cycles.SecondsAt(bar.Start+hit.Beat);float energy=midi.IsPlaying&&elapsed>=0?(float)Math.Exp(-elapsed*12):0;pin.localScale=Vector3.one*(.025f+.05f*energy)*Mathf.Sqrt(hit.Velocity);block.SetColor("_BaseColor",Color.white*(.3f+energy*1.8f));pin.GetComponent<Renderer>().SetPropertyBlock(block);}}
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
            line.startColor=line.endColor=new Color(.82f,.91f,1f)*(envelope*wave.Velocity*(kick?2.2f:.65f));
        }
    }
    void OnDisable(){if(material!=null)Destroy(material);if(discMaterial!=null)Destroy(discMaterial);if(discMesh!=null)Destroy(discMesh);if(deck!=null){deck.gameObject.SetActive(false);Destroy(deck.gameObject);}deck=null;source=null;active=null;views.Clear();waves.Clear();ripples.Clear();}
}
