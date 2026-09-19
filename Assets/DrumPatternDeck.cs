using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Percussion has its own XZ-plane player below the torus. It never enters tonal voices.
public sealed class DrumPatternDeck : MonoBehaviour
{
    MidiPlayer midi;Main main;PreparedPatternSong source;
    Transform deck;Material material,discMaterial;Mesh discMesh;readonly List<LineRenderer> rings=new();readonly List<Transform> pins=new();
    readonly List<LineRenderer> waves=new();readonly List<Ripple> ripples=new();
    PreparedPatternSong.Disk[] disks=Array.Empty<PreparedPatternSong.Disk>();
    MidiCycleAnalysis.Hit[] hits=Array.Empty<MidiCycleAnalysis.Hit>();
    readonly Vector3[] points=new Vector3[193];double previous;int nextHit;bool wasPlaying;
    sealed class Ripple {public double Start;public int Frequency;public float Velocity,Decay,Radius,Width;public Vector3 Origin;}
    public int VisibleRippleCount=>ripples.Count;
    public static int FrequencyBand(int pitch)=>pitch is 35 or 36?4:pitch is 38 or 39 or 40?18:pitch is 42 or 44 or 46 or 54 or 69 or 70?48:pitch is 49 or 51 or 52 or 53 or 55 or 57 or 59?32:10;
    void Start()
    {
        midi=GetComponent<MidiPlayer>();main=GetComponent<Main>();
        deck=new GameObject("Percussion CD changer · twelve o'clock playhead").transform;deck.SetParent(transform,false);deck.localPosition=new Vector3(0,-1.3f,0);
        material=new Material(Resources.Load<Shader>("HarmonicGlow"));material.SetColor("_BaseColor",Color.white*2);
        discMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit"));discMaterial.SetColor("_BaseColor",new Color(.014f,.022f,.032f));
        var vertices=new List<Vector3>();var triangles=new List<int>();
        for(int i=0;i<=96;i++){float a=i*Mathf.PI*2/96;vertices.Add(new Vector3(Mathf.Sin(a)*.18f,0,Mathf.Cos(a)*.18f));vertices.Add(new Vector3(Mathf.Sin(a)*1.15f,0,Mathf.Cos(a)*1.15f));if(i<96){int n=i*2;triangles.AddRange(new[]{n,n+1,n+3,n,n+3,n+2,n+3,n+1,n,n+2,n+3,n});}}
        discMesh=new Mesh{name="Percussion disc annulus"};discMesh.SetVertices(vertices);discMesh.SetTriangles(triangles,0);discMesh.RecalculateNormals();
        for(int layer=0;layer<4;layer++){var disc=new GameObject("Stacked rhythm disc");disc.transform.SetParent(deck,false);disc.transform.localPosition=new Vector3(0,-layer*.065f-.006f,0);disc.AddComponent<MeshFilter>().sharedMesh=discMesh;disc.AddComponent<MeshRenderer>().sharedMaterial=discMaterial;}
        for(int i=0;i<10;i++)rings.Add(Line("Disc groove",.009f));
        for(int i=0;i<32;i++){var line=Line("Percussion energy ripple",.012f);line.enabled=false;waves.Add(line);}
        var needle=Line("Drum playhead",.025f);needle.positionCount=2;needle.SetPositions(new[]{new Vector3(0,.025f,.2f),new Vector3(0,.025f,1.23f)});needle.startColor=needle.endColor=Color.white;
    }
    LineRenderer Line(string name,float width)
    {var go=new GameObject(name);go.transform.SetParent(deck,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=material;l.useWorldSpace=false;l.widthMultiplier=width;l.positionCount=points.Length;l.startColor=l.endColor=new Color(.17f,.28f,.4f);return l;}
    void Circle(LineRenderer line,float radius,float y,Color hue)
    {for(int i=0;i<points.Length;i++){float a=i*Mathf.PI*2/(points.Length-1);points[i]=new Vector3(Mathf.Sin(a)*radius,y,Mathf.Cos(a)*radius);}line.SetPositions(points);line.startColor=line.endColor=hue;}
    void Load()
    {
        source=midi.Prepared;disks=source?.Disks.Where(d=>d.Channel==10).ToArray()??Array.Empty<PreparedPatternSong.Disk>();
        hits=source?.Notes.Where(h=>h.Channel==10).OrderBy(h=>h.Beat).ToArray()??Array.Empty<MidiCycleAnalysis.Hit>();nextHit=0;previous=-1;ripples.Clear();
    }
    void Update()
    {
        if(deck==null)return;if(source!=midi.Prepared)Load();deck.gameObject.SetActive(disks.Length>0);if(disks.Length==0)return;
        double beat=midi.Cycles.BeatAt(midi.ScorePosition),now=midi.ScorePosition;
        bool jump=now<previous||Math.Abs(now-previous)>.3||(!wasPlaying&&midi.IsPlaying);
        if(jump){ripples.Clear();nextHit=Array.FindIndex(hits,h=>h.Beat>=beat-.00001);if(nextHit<0)nextHit=hits.Length;}
        if(midi.IsPlaying)while(nextHit<hits.Length&&hits[nextHit].Beat<=beat){var hit=hits[nextHit++];ripples.Add(new Ripple{Start=midi.Cycles.SecondsAt(hit.Beat),Frequency=hit.RippleFrequency,Decay=hit.DecaySeconds,Velocity=hit.Velocity,Radius=hit.RippleRadius,Width=hit.RippleWidth,Origin=new Vector3(0,.015f,hit.StrikeRadius)});}
        if(!midi.IsPlaying)ripples.Clear();previous=now;wasPlaying=midi.IsPlaying;
        PreparedPatternSong.Disk current=null;PreparedPatternSong.Visit visit=null;
        foreach(var d in disks)foreach(var v in d.Visits)if(v.Beat<=beat&&beat<v.Beat+d.Beats){current=d;visit=v;}
        var disc=current??disks[0];var content=visit?.Hits??disc.Hits;double phase=current==null?0:(beat-visit.Beat)/disc.Beats;
        for(int i=0;i<4;i++)Circle(rings[i],1.15f,-i*.065f,new Color(.17f,.26f,.36f)*(i==0?1.8f:.6f));
        for(int i=4;i<10;i++)Circle(rings[i],.19f+(i-4)*.17f,0,new Color(.12f,.2f,.28f));
        while(pins.Count<content.Length){var pin=GameObject.CreatePrimitive(PrimitiveType.Sphere);Destroy(pin.GetComponent<Collider>());pin.name="Drum dimple";pin.transform.SetParent(deck,false);pin.GetComponent<Renderer>().sharedMaterial=material;pins.Add(pin.transform);}
        var block=new MaterialPropertyBlock();
        for(int i=0;i<pins.Count;i++)
        {
            bool visible=i<content.Length;pins[i].gameObject.SetActive(visible);if(!visible)continue;
            var hit=content[i];float radius=hit.StrikeRadius;
            float angle=(float)(hit.Beat/disc.Beats-phase)*Mathf.PI*2;
            double elapsed=current==null?-1:now-midi.Cycles.SecondsAt(visit.Beat+hit.Beat);
            float energy=midi.IsPlaying&&elapsed>=0?(float)Math.Exp(-elapsed*12):0;
            pins[i].localPosition=new Vector3(Mathf.Sin(angle)*radius,.025f,Mathf.Cos(angle)*radius);
            pins[i].localScale=Vector3.one*(.025f+.05f*energy)*Mathf.Sqrt(hit.Velocity);
            block.SetColor("_BaseColor",Color.white*(.55f+energy*18));pins[i].GetComponent<Renderer>().SetPropertyBlock(block);
        }
        ripples.RemoveAll(r=>now-r.Start>r.Decay);
        if(ripples.Count>waves.Count)ripples.RemoveRange(0,ripples.Count-waves.Count);
        for(int n=0;n<waves.Count;n++)
        {
            var line=waves[n];line.enabled=n<ripples.Count;if(!line.enabled)continue;
            var wave=ripples[n];float age=(float)(now-wave.Start);int frequency=wave.Frequency;
            float progress=Mathf.Clamp01(age/Mathf.Max(.05f,wave.Decay));float decay=Mathf.Exp(-progress*4);float radius=wave.Radius*(1-Mathf.Exp(-progress*3));
            for(int i=0;i<points.Length;i++){float a=i*Mathf.PI*2/(points.Length-1);float corrugation=Main.ReducedMotion?0:Mathf.Sin(a*frequency-age*frequency*8)*Mathf.Min(.035f,radius*.07f)*decay;float r=radius+corrugation;points[i]=wave.Origin+new Vector3(Mathf.Sin(a)*r,0,Mathf.Cos(a)*r);}
            line.SetPositions(points);line.widthMultiplier=wave.Width*decay;
            line.startColor=line.endColor=Color.white*(decay*wave.Velocity*(frequency==4?8:5));
        }
    }
    void OnDestroy(){if(material!=null)Destroy(material);if(discMaterial!=null)Destroy(discMaterial);if(discMesh!=null)Destroy(discMesh);if(deck!=null)Destroy(deck.gameObject);}
}
