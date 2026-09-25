using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

// Spoken lyrics on the drum wheel, in lyric mode. A sawtooth rack meshes with the drum disc at
// twelve o'clock, where the dimples are struck, and slides with the disc's rim, so a syllable
// reaches the comb exactly when it is spoken. The saw is cut by the drums themselves: every
// hit (or group of hits struck together) is a cliff whose height is the hit's weight (kick
// tallest, then snare, toms, cymbals, hats) and velocity, with a ramp rising to it over at
// most the beat before. The disc's rim is a ratchet cut by the same hits in its bar.
//  • A syllable on a beat waits on the crest and drops with gravity, landing in its well
//    exactly on its beat, and stays low: an impact flash and a short squash, no bounce.
//  • A syllable off the beat rides the saw and is bumped up over it as it is spoken.
//  • A syllable held across three or more eighths lands the same way, then floats in an arc
//    above the teeth while it lasts (staying at the comb as the rack runs under it) and dives
//    into the well where it ends.
// The line being spoken is written above the rack with its meter. Text meshes are rebuilt only
// when a slot's syllable changes; pops and squashes are scales, not font sizes.
[DefaultExecutionOrder(80)]
public sealed class DrumLyricRack : MonoBehaviour
{
    Main main;MidiPlayer midi;DrumPatternDeck deck;PreparedPatternSong source;
    Transform root;LineRenderer saw,teeth,ratchet,arc;Material glow,steel;Mesh body;
    TextBox caption,meter;
    sealed class Slot {public TextBox Box;public Material Face;public PreparedPatternSong.Syllable Syllable;}
    readonly List<Slot> pool=new();
    PreparedPatternSong.Syllable[] spoken=Array.Empty<PreparedPatternSong.Syllable>();
    // The drum teeth: onset beats and weights (0..1) of every hit group in the song.
    double[] toothBeat=Array.Empty<double>();float[] toothDepth=Array.Empty<float>();
    float visible;
    public bool Shown=>visible>.5f;
    public float Visibility=>visible;
    // Deck-local geometry (the disc's rim is 1.15): the rack's wells sit just beyond the rim.
    public const float Edge=1.15f,Well=1.27f,Crest=.2f,TextSize=2.1f,TextLift=.08f;
    // The lyric camera frames Tall deck units around Middle (from below the disc to the line
    // written above the rack); the rack runs as wide as the view.
    public const float Tall=4.3f,Middle=.72f;
    public float Span {get;private set;}=3;
    public double PerBeat {get;private set;}
    // For validation: each visible syllable's rack slot, where it is shown (x from the comb,
    // lift above the wells) and what it is doing; and the cliffs now on the rack.
    public readonly List<(PreparedPatternSong.Syllable syllable,float slot,float x,float lift,string state)> Placed=new();
    public readonly List<double> VisibleTeeth=new();
    public string Caption=>caption!=null?caption.TextField.text:"";
    static readonly Color Ink=new(.5f,.74f,.72f),Steel=new(.2f,.3f,.3f);
    readonly List<Vector3> top=new(256),bottom=new(256),vertices=new(512);readonly List<int> triangles=new(768);
    Vector3[] ratchetPoints=Array.Empty<Vector3>();readonly Vector3[] arcPoints=new Vector3[24];
    Gradient edgeGradient;float edgeFade=-1;string captionKey="";
    PreparedPatternSong.DrumBar ratchetBar;(double phase,float depth)[] ratchetHits=Array.Empty<(double,float)>();

    void OnEnable()
    {
        if(!Application.isPlaying)return;
        main=GetComponent<Main>();midi=GetComponent<MidiPlayer>();deck=GetComponent<DrumPatternDeck>();
        // Not a child of the scene root: the views' opacity pass would override the glow.
        root=new GameObject("Drum lyric rack · sawtooth cut by the drum hits").transform;
        glow=new Material(Resources.Load<Shader>("HarmonicGlow"));glow.SetColor("_BaseColor",Color.white*2);glow.renderQueue=3102;
        steel=new Material(Shader.Find("Universal Render Pipeline/Unlit"));steel.SetColor("_BaseColor",new Color(.028f,.04f,.04f));steel.renderQueue=3100;
        body=new Mesh{name="Rack body"};body.MarkDynamic();var plate=new GameObject("Rack body");plate.transform.SetParent(root,false);plate.AddComponent<MeshFilter>().sharedMesh=body;plate.AddComponent<MeshRenderer>().sharedMaterial=steel;
        saw=Line("Rack saw",.022f,Ink);teeth=Line("Rack teeth",.01f,Steel);ratchet=Line("Drum ratchet",.009f,Steel);arc=Line("Held syllable arc",.006f,Ink);
        caption=Text("",2.6f);caption.TextField.richText=true;meter=Text("",1.5f);
        root.gameObject.SetActive(false);
    }
    LineRenderer Line(string name,float width,Color color)
    {
        var go=new GameObject(name);go.transform.SetParent(root,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=glow;l.useWorldSpace=false;l.widthMultiplier=width;
        l.numCapVertices=2;l.startColor=l.endColor=color;l.positionCount=0;return l;
    }
    TextBox Text(string text,float size)
    {
        var box=TextBox.Create(text,TextAlignmentOptions.Center);box.transform.SetParent(root,false);box.Size=size;
        // Lying flat on the deck, readable from above with twelve o'clock up.
        box.transform.localRotation=Quaternion.Euler(90,0,0);box.TextField.fontMaterial.renderQueue=3103;
        box.TextField.textWrappingMode=TextWrappingModes.NoWrap;return box;
    }
    // A drum's weight in the saw: how tall a cliff its hit cuts.
    public static float Weight(int pitch)=>pitch switch{35 or 36=>1f,38 or 40=>.9f,37 or 39=>.75f,41 or 43 or 45 or 47 or 48 or 50=>.7f,49 or 55 or 57=>.65f,51 or 53 or 59=>.45f,42 or 44 or 46=>.35f,_=>.4f};
    void Load()
    {
        source=midi.Prepared;
        spoken=source?.Lyrics?.Syllables?.Where(s=>s.Spoken).OrderBy(s=>s.Start).ToArray()??Array.Empty<PreparedPatternSong.Syllable>();
        var groups=(source?.Notes??Array.Empty<MidiCycleAnalysis.Hit>()).Where(n=>n.Channel==10).GroupBy(n=>Math.Round(n.Beat*96)).OrderBy(g=>g.Key)
            .Select(g=>(beat:g.Min(n=>n.Beat),depth:g.Max(n=>Weight(n.Pitch)*(.55f+.45f*Mathf.Clamp01(n.Velocity))))).ToArray();
        toothBeat=groups.Select(g=>g.beat).ToArray();toothDepth=groups.Select(g=>g.depth).ToArray();
        foreach(var slot in pool){slot.Syllable=null;slot.Box.gameObject.SetActive(false);}
        captionKey="";
    }
    public bool HasSpoken=>spoken.Length>0;
    int ToothAfter(double beat){int lo=0,hi=toothBeat.Length;while(lo<hi){int mid=(lo+hi)/2;if(toothBeat[mid]<=beat+1e-6)lo=mid+1;else hi=mid;}return lo;}
    // The saw's height above the wells at a beat: rising over at most the beat before the next
    // hit to that hit's crest, then dropping at the hit.
    public float Profile(double beat)
    {
        int k=ToothAfter(beat);if(k>=toothBeat.Length)return 0;
        double next=toothBeat[k],prev=k>0?toothBeat[k-1]:next-1,from=Math.Max(prev,next-1);
        return (float)(Crest*toothDepth[k]*Math.Clamp((beat-from)/Math.Max(1e-6,next-from),0,1));
    }
    float DepthAt(double beat){int k=ToothAfter(beat-.02);return k<toothBeat.Length&&Math.Abs(toothBeat[k]-beat)<.06?toothDepth[k]:.6f;}

    void LateUpdate()
    {
        using var perf=Perf.Rack.Auto();
        if(root==null)return;
        if(midi==null||deck==null){midi=GetComponent<MidiPlayer>();deck=GetComponent<DrumPatternDeck>();return;}
        var views=GetComponent<VisualizationViews>();
        bool want=views!=null&&views.Current==VisualizationViews.View.Lyrics&&deck.WheelTransform!=null&&deck.WheelTransform.gameObject.activeInHierarchy&&midi.Prepared!=null;
        visible=Main.ReducedMotion?(want?1:0):Mathf.MoveTowards(visible,want?1:0,Time.unscaledDeltaTime*3);
        if(visible<=0){if(root.gameObject.activeSelf)root.gameObject.SetActive(false);Placed.Clear();return;}
        if(!root.gameObject.activeSelf)root.gameObject.SetActive(true);
        if(source!=midi.Prepared)Load();
        var wheel=deck.WheelTransform;root.SetPositionAndRotation(wheel.position,wheel.rotation);root.localScale=wheel.lossyScale;
        double beat=midi.Cycles.BeatAt(midi.ScorePosition),now=midi.ScorePosition;
        var bar=CurrentBar(beat);
        double barLength=bar!=null?bar.End-bar.Start:4;
        // Rim speed: one revolution per bar.
        double perBeat=2*Math.PI*Edge/Math.Max(1,barLength);PerBeat=perBeat;
        float fade=Mathf.SmoothStep(0,1,visible);
        var view=Camera.main;float aspect=view!=null?view.aspect:1.6f;
        Span=Mathf.Clamp(Tall*.5f*aspect-.35f,2.2f,9);
        DrawRack(beat,perBeat,fade);
        DrawRatchet(beat,bar,fade);
        DrawSyllables(beat,now,perBeat,fade);
    }
    PreparedPatternSong.DrumBar CurrentBar(double beat)
    {
        var bars=source.DrumBars;if(bars==null||bars.Length==0)return null;
        int lo=0,hi=bars.Length-1,found=0;
        while(lo<=hi){int mid=(lo+hi)/2;if(bars[mid].Start<=beat+1e-6){found=mid;lo=mid+1;}else hi=mid-1;}
        return bars[found];
    }

    void DrawRack(double beat,double perBeat,float fade)
    {
        top.Clear();bottom.Clear();VisibleTeeth.Clear();
        double from=beat-Span/perBeat,to=beat+Span/perBeat;
        float X(double b)=>(float)((b-beat)*perBeat);
        top.Add(new Vector3(-Span,.03f,Well+Profile(from)));
        for(int k=ToothAfter(from);k<toothBeat.Length&&toothBeat[k]<=to;k++)
        {
            double hit=toothBeat[k],prev=k>0?toothBeat[k-1]:hit-1,rise=Math.Max(prev,hit-1);
            if(rise>from&&X(rise)>top[^1].x)top.Add(new Vector3(X(rise),.03f,Well));
            float x=X(hit);VisibleTeeth.Add(hit);
            top.Add(new Vector3(x-.001f,.03f,Well+Crest*toothDepth[k]));top.Add(new Vector3(x,.03f,Well));
            float w=(float)perBeat*.08f;
            bottom.Add(new Vector3(x-w,.03f,Edge+.075f));bottom.Add(new Vector3(x,.03f,Edge+.02f));bottom.Add(new Vector3(x+w,.03f,Edge+.075f));
        }
        top.Add(new Vector3(Span,.03f,Well+Profile(to)));
        saw.positionCount=top.Count;for(int i=0;i<top.Count;i++)saw.SetPosition(i,top[i]);
        teeth.positionCount=bottom.Count;for(int i=0;i<bottom.Count;i++)teeth.SetPosition(i,bottom[i]);
        // Body: from the meshing edge up to the saw.
        vertices.Clear();triangles.Clear();
        foreach(var q in top){vertices.Add(new Vector3(q.x,.02f,Edge+.07f));vertices.Add(new Vector3(q.x,.02f,q.z));}
        for(int i=0;i+1<top.Count;i++){int k=i*2;triangles.Add(k);triangles.Add(k+1);triangles.Add(k+3);triangles.Add(k);triangles.Add(k+3);triangles.Add(k+2);}
        body.Clear();body.SetVertices(vertices);body.SetTriangles(triangles,0);body.RecalculateBounds();
        if(edgeFade!=fade)
        {
            edgeFade=fade;steel.SetColor("_BaseColor",new Color(.028f,.04f,.04f,fade));
            edgeGradient??=new Gradient();
            edgeGradient.SetKeys(new[]{new GradientColorKey(Ink*1.3f,0),new GradientColorKey(Ink*1.3f,1)},new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(fade,.25f),new GradientAlphaKey(fade,.75f),new GradientAlphaKey(0,1)});
            saw.colorGradient=edgeGradient;teeth.colorGradient=edgeGradient;
        }
    }
    // The disc's ratchet: cut by this bar's drum hits, turning with the disc.
    void DrawRatchet(double beat,PreparedPatternSong.DrumBar bar,float fade)
    {
        if(bar==null||bar.Hits==null||bar.Hits.Length==0){ratchet.positionCount=0;return;}
        double length=Math.Max(1e-6,bar.End-bar.Start),turn=(beat-bar.Start)/length;
        // Hit groups of this bar, in phase order (grouped once per bar).
        if(bar!=ratchetBar){ratchetBar=bar;ratchetHits=bar.Hits.GroupBy(h=>Math.Round(h.Beat*96)).OrderBy(g=>g.Key).Select(g=>(phase:g.Min(h=>h.Beat)/length,depth:g.Max(h=>Weight(h.Pitch)*(.55f+.45f*Mathf.Clamp01(h.Velocity))))).ToArray();}
        var hits=ratchetHits;
        int count=hits.Length*9+1;if(ratchetPoints.Length!=count)ratchetPoints=new Vector3[count];
        int n=0;
        for(int k=0;k<hits.Length;k++)
        {
            double prev=hits[(k+hits.Length-1)%hits.Length].phase,next=hits[k].phase;if(prev>=next)prev-=1;
            double rise=Math.Max(prev,next-1/Math.Max(1.0,length));
            for(int s=0;s<=7;s++)
            {
                double f=s/7.0,phase=rise+(next-rise)*f-turn;float r=Edge+.012f+.05f*hits[k].depth*(float)f;
                float a=(float)(phase*Math.PI*2);ratchetPoints[n++]=new Vector3(Mathf.Sin(a)*r,.025f,Mathf.Cos(a)*r);
            }
            float b=(float)((next-turn)*Math.PI*2);ratchetPoints[n++]=new Vector3(Mathf.Sin(b)*(Edge+.012f),.025f,Mathf.Cos(b)*(Edge+.012f));
        }
        ratchetPoints[n++]=ratchetPoints[0];
        ratchet.positionCount=n;ratchet.SetPositions(ratchetPoints);ratchet.startColor=ratchet.endColor=Steel*(1.4f*fade);
    }

    Slot Pooled(int index)
    {
        while(pool.Count<=index){var box=Text("",TextSize);pool.Add(new Slot{Box=box,Face=box.TextField.fontMaterial});}
        return pool[index];
    }
    static Vector2 Bezier(Vector2 a,Vector2 b,Vector2 c,Vector2 d,float t){float u=1-t;return u*u*u*a+3*u*u*t*b+3*u*t*t*c+t*t*t*d;}

    void DrawSyllables(double beat,double now,double perBeat,float fade)
    {
        Placed.Clear();int used=0;arc.positionCount=0;
        double window=Span/perBeat+.5;
        PreparedPatternSong.Syllable speaking=null;
        for(int i=0;i<spoken.Length;i++)
        {
            var s=spoken[i];if(s.Start>beat+window)break;
            bool held=s.End-s.Start>=1.5;
            double end=held?Math.Ceiling(s.End-1e-6):s.Start;
            if(end<beat-window)continue;
            if(s.Start<=beat&&beat<Math.Max(s.End,s.Start+.25))speaking=s;
            double onset=midi.Cycles.SecondsAt(s.Start);float e=(float)(now-onset);
            float x=(float)((s.Start-beat)*perBeat);
            bool down=s.Metric>=1;
            float low=Well+TextLift,rest=down?low:Well+Profile(s.Start)+TextLift;
            // The drop: from the crest just before the slot, with gravity, landing on the beat.
            float drop=down?Mathf.Clamp(i>0?(float)(onset-midi.Cycles.SecondsAt(spoken[i-1].Start))*.7f:.14f,.06f,.14f):0;
            var perch=new Vector2(-.12f,Well+Crest*DepthAt(s.Start)+TextLift);
            Vector2 at;string state;float squash=0;
            if(e<-drop){at=down?perch:new Vector2(0,rest);state="waiting";}
            else if(e<0){float t=(e+drop)/drop;at=new Vector2(Mathf.Lerp(perch.x,.04f,t),Mathf.Lerp(perch.y,low,t*t));state="dropping";}
            else if(held&&beat<s.End)
            {
                // Landed on the beat, then floats along an arc over the teeth to the well at its end.
                float length=(float)((end-s.Start)*perBeat);double u=(beat-s.Start)/Math.Max(1e-6,s.End-s.Start);
                var a=new Vector2(.04f,down?low:rest);var b=new Vector2(length*.2f,Well+Crest+.55f);var c=new Vector2(length*.8f,Well+Crest+.55f);
                float rise=Mathf.Clamp01(e/.12f);
                at=Vector2.Lerp(a,Bezier(a,b,c,new Vector2(length*.92f,Well+Crest+.28f),(float)u),rise);state=rise<1?"in well":"floating";
                for(int k=0;k<24;k++){var q=Bezier(a,b,c,new Vector2(length,low),k/23f);arcPoints[k]=new Vector3(x+q.x,.035f,q.y);}
                arc.positionCount=24;arc.SetPositions(arcPoints);arc.startColor=arc.endColor=Ink*(.6f*fade);
                squash=down?Mathf.Exp(-e/.08f):0;
            }
            else if(held)
            {
                // The dive into the well where it ends.
                float length=(float)((end-s.Start)*perBeat);float t=Mathf.Clamp01((float)((beat-s.End)/Math.Max(.05,end-s.End+.01))*3);
                at=Vector2.Lerp(new Vector2(length*.92f,Well+Crest+.28f),new Vector2(length,low),t*t);state=t>=1?"in well":"diving";
            }
            else if(down){at=new Vector2(.04f,low);state="in well";squash=Mathf.Exp(-e/.08f);}
            else
            {
                // Bumped up over the saw and back onto it.
                float t=Mathf.Clamp01(e/.28f);at=new Vector2(.12f*t,rest+Mathf.Sin(t*Mathf.PI)*(Crest+.14f));state=t<1?"bumped":"on ramp";
            }
            var slot=Pooled(used++);var box=slot.Box;
            if(!box.gameObject.activeSelf)box.gameObject.SetActive(true);
            if(slot.Syllable!=s){slot.Syllable=s;box.Text=s.Text;}
            box.transform.localPosition=new Vector3(x+at.x,.04f,at.y+.07f);
            // Landing: a flash scaled by emphasis and a short squash; no bounce.
            float flash=e>=0?Mathf.Exp(-e/.35f):0;float dist=Mathf.Clamp01(1-Mathf.Abs(x+at.x)/Span);
            box.transform.localScale=new Vector3(1+.3f*squash+.15f*flash*s.Emphasis,1-.25f*squash,1);
            float intensity=(s.Stress>0?1.05f:.7f)+flash*(1+3.5f*s.Emphasis)+(state=="floating"?1.2f*s.Emphasis:0);
            var color=Color.Lerp(Ink,Color.white,Mathf.Clamp01(flash+(state=="floating"?.6f:0)))*intensity;color.a=fade*dist*(e>1.2f&&state!="floating"?.55f:1);
            slot.Face.SetColor(ShaderUtilities.ID_FaceColor,color);
            Placed.Add((s,x,x+at.x,at.y-Well,state));
        }
        for(int i=used;i<pool.Count;i++)if(pool[i].Box.gameObject.activeSelf){pool[i].Box.gameObject.SetActive(false);pool[i].Syllable=null;}
        // The line being spoken, written out above the rack with its meter; rebuilt only when it changes.
        var lyrics=source.Lyrics;PreparedPatternSong.LyricLine line=null;
        if(speaking!=null)line=lyrics.Lines[speaking.Line];
        else foreach(var s in spoken)if(s.Start>beat&&s.Start<beat+4){line=lyrics.Lines[s.Line];break;}
        string key=line==null?"":line.Start+":"+(speaking?.Start??-1);
        if(key!=captionKey)
        {
            captionKey=key;
            if(line==null){caption.Text="";meter.Text="";}
            else
            {
                var text=new System.Text.StringBuilder();
                for(int i=line.First;i<line.First+line.Count;i++)
                {
                    var s=lyrics.Syllables[i];string piece=s.Text+(s.WordEnd?" ":"");
                    text.Append(s==speaking?$"<color=#FFFFFF>{piece}</color>":s.Start<beat?$"<color=#6FA8A4>{piece}</color>":$"<color=#9CCFCB>{piece}</color>");
                }
                caption.Text=text.ToString().TrimEnd();caption.transform.localPosition=new Vector3(0,.04f,Well+Crest+1.05f);
                meter.Text=$"{line.Meter}   {line.Stresses}";meter.transform.localPosition=new Vector3(0,.04f,Well+Crest+.72f);
            }
        }
        caption.TextField.fontMaterial.SetColor(ShaderUtilities.ID_FaceColor,Color.white*fade);
        meter.TextField.fontMaterial.SetColor(ShaderUtilities.ID_FaceColor,new Color(.5f,.74f,.72f,.8f*fade));
    }
    void OnDisable()
    {
        if(root!=null)Destroy(root.gameObject);root=null;pool.Clear();
        if(glow!=null)Destroy(glow);if(steel!=null)Destroy(steel);if(body!=null)Destroy(body);
    }
}
