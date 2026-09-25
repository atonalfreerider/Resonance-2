using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

// Spoken lyrics on the drum wheel, in lyric mode. The drum disc becomes a ratchet — one tooth
// per beat — and a sawtooth rack meshes with it at twelve o'clock, where the dimples are struck.
// Each tooth of the rack is one beat: a ramp rising through the beat (its upbeat halfway up)
// to a crest, then a sheer drop into the well of the next downbeat. The rack slides with the
// disc's rim, so a syllable reaches the comb exactly when it is spoken:
//  • a syllable on the downbeat waits on the crest and drops into its well, with a bounce;
//  • a syllable on the upbeat rides the ramp and is bumped up over the saw;
//  • a syllable held across several teeth floats in a bezier arc above them while it lasts
//    (staying at the comb as the rack runs under it), then dives into the well where it ends.
// Stress and emphasis bloom as each syllable lands; the line being spoken is written above
// the rack with its meter, and trochees and iambs read as wells and ramps under the words.
[DefaultExecutionOrder(80)]
public sealed class DrumLyricRack : MonoBehaviour
{
    Main main;MidiPlayer midi;DrumPatternDeck deck;PreparedPatternSong source;
    Transform root;LineRenderer saw,teeth,ratchet,arc;Material glow,steel;Mesh body;readonly List<LineRenderer> sparks=new();
    TextBox caption,meter;readonly List<TextBox> pool=new();
    PreparedPatternSong.Syllable[] spoken=Array.Empty<PreparedPatternSong.Syllable>();
    float visible;
    public bool Shown=>visible>.5f;
    public float Visibility=>visible;
    // Deck-local geometry (the disc's rim is 1.15): the rack's wells sit just beyond the rim.
    public const float Edge=1.15f,Well=1.27f,Crest=.17f,TextSize=2.1f;
    // The lyric camera frames Tall deck units around Middle (from below the disc to the line
    // written above the rack); the rack runs as wide as the view.
    public const float Tall=4.3f,Middle=.72f;
    public float Span {get;private set;}=3;
    // For validation: each visible syllable's rack slot, where it is shown (x from the comb,
    // lift above the wells) and what it is doing.
    public readonly List<(PreparedPatternSong.Syllable syllable,float slot,float x,float lift,string state)> Placed=new();
    public string Caption=>caption!=null?caption.TextField.text:"";
    public double PerBeat {get;private set;}
    static readonly Color Ink=new(.5f,.74f,.72f),Steel=new(.2f,.3f,.3f),Dim=new(.3f,.44f,.43f);

    void OnEnable()
    {
        if(!Application.isPlaying)return;
        main=GetComponent<Main>();midi=GetComponent<MidiPlayer>();deck=GetComponent<DrumPatternDeck>();
        // Not a child of the scene root: the views' opacity pass would override the glow.
        root=new GameObject("Drum lyric rack · sawtooth, one tooth per beat").transform;
        glow=new Material(Resources.Load<Shader>("HarmonicGlow"));glow.SetColor("_BaseColor",Color.white*2);glow.renderQueue=3102;
        // The rack's steel body under the saw, drawn before its edges.
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
    void Load()
    {
        source=midi.Prepared;
        spoken=source?.Lyrics?.Syllables?.Where(s=>s.Spoken).OrderBy(s=>s.Start).ToArray()??Array.Empty<PreparedPatternSong.Syllable>();
    }
    public bool HasSpoken=>spoken.Length>0;

    // The saw's height above the well line at a beat: a ramp through the beat, a drop at the next.
    public static float Profile(double beat){double f=beat-Math.Floor(beat);return (float)(Crest*Math.Min(1,f/.92));}

    void LateUpdate()
    {
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
        var bar=source.DrumBars?.LastOrDefault(b=>b.Start<=beat+1e-6);
        int counts=Math.Max(1,bar?.Numerator??4);double barLength=bar!=null?bar.End-bar.Start:4;
        // Rim speed: one revolution per bar, so a beat is a tooth of 2πR / counts.
        double perBeat=2*Math.PI*Edge/Math.Max(1,barLength);PerBeat=perBeat;
        float fade=Mathf.SmoothStep(0,1,visible);
        var view=Camera.main;float aspect=view!=null?view.aspect:1.6f;
        Span=Mathf.Clamp(Tall*.5f*aspect-.35f,2.2f,9);
        DrawRack(beat,perBeat,fade);
        DrawRatchet(beat,bar,counts,fade);
        DrawSyllables(beat,now,perBeat,fade);
    }

    void DrawRack(double beat,double perBeat,float fade)
    {
        // Upper edge: the saw the syllables ride. Lower edge: the teeth meshing with the ratchet.
        var top=new List<Vector3>();var bottom=new List<Vector3>();
        double from=beat-Span/perBeat,to=beat+Span/perBeat;
        float X(double b)=>(float)((b-beat)*perBeat);
        top.Add(new Vector3(-Span,.03f,Well+Profile(from)));
        for(double k=Math.Ceiling(from);k<=to;k++)
        {
            float x=X(k);
            top.Add(new Vector3(x-.001f,.03f,Well+Crest*.99f));top.Add(new Vector3(x,.03f,Well));
            bottom.Add(new Vector3(x-(float)perBeat*.15f,.03f,Edge+.075f));bottom.Add(new Vector3(x,.03f,Edge+.02f));bottom.Add(new Vector3(x+(float)perBeat*.15f,.03f,Edge+.075f));
        }
        top.Add(new Vector3(Span,.03f,Well+Profile(to)));
        saw.positionCount=top.Count;saw.SetPositions(top.ToArray());
        // Body: from the meshing edge up to the saw.
        var vertices=new List<Vector3>();var triangles=new List<int>();
        foreach(var q in top){vertices.Add(new Vector3(q.x,.02f,Edge+.07f));vertices.Add(new Vector3(q.x,.02f,q.z));}
        for(int i=0;i+1<top.Count;i++){int k=i*2;triangles.AddRange(new[]{k,k+1,k+3,k,k+3,k+2});}
        body.Clear();body.SetVertices(vertices);body.SetTriangles(triangles,0);body.RecalculateBounds();steel.SetColor("_BaseColor",new Color(.028f,.04f,.04f,fade));
        teeth.positionCount=bottom.Count;teeth.SetPositions(bottom.ToArray());
        var edge=new Gradient();edge.SetKeys(new[]{new GradientColorKey(Ink*1.3f,0),new GradientColorKey(Ink*1.3f,1)},new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(fade,.25f),new GradientAlphaKey(fade,.75f),new GradientAlphaKey(0,1)});
        saw.colorGradient=edge;teeth.colorGradient=edge;
        // Upbeat marks halfway up each ramp.
        while(sparks.Count<16)sparks.Add(Line("Rack upbeat mark",.004f,Dim));
        int m=0;
        for(double k=Math.Ceiling(from-.5)+.5;k<=to&&m<sparks.Count;k++)
        {
            float x=X(k);if(Mathf.Abs(x)>Span)continue;var mark=sparks[m++];mark.positionCount=2;
            float z=Well+Profile(k);mark.SetPositions(new[]{new Vector3(x,.03f,z-.035f),new Vector3(x,.03f,z+.02f)});
            float a=fade*(1-Mathf.Abs(x)/Span);mark.startColor=mark.endColor=Dim*a;
        }
        for(;m<sparks.Count;m++)sparks[m].positionCount=0;
    }
    // The drum disc's ratchet: one tooth per beat, turning with the disc (a quarter-turn per beat in 4/4).
    void DrawRatchet(double beat,PreparedPatternSong.DrumBar bar,int counts,float fade)
    {
        double turn=bar==null?0:(beat-bar.Start)/Math.Max(1e-6,bar.End-bar.Start);
        var points=new List<Vector3>();
        for(int k=0;k<counts;k++)
            for(int s=0;s<=12;s++)
            {
                double f=s/12.0,phase=(k+f)/counts-turn;float r=Edge+.012f+.05f*(float)Math.Min(1,f/.92);
                float a=(float)(phase*Math.PI*2);points.Add(new Vector3(Mathf.Sin(a)*r,.025f,Mathf.Cos(a)*r));
                if(s==12){float r0=Edge+.012f;points.Add(new Vector3(Mathf.Sin(a)*r0,.025f,Mathf.Cos(a)*r0));}
            }
        points.Add(points[0]);
        ratchet.positionCount=points.Count;ratchet.SetPositions(points.ToArray());ratchet.startColor=ratchet.endColor=Steel*(1.4f*fade);
    }

    TextBox Pooled(int index)
    {
        while(pool.Count<=index){var box=Text("",TextSize);pool.Add(box);}
        return pool[index];
    }
    static Vector2 Bezier(Vector2 a,Vector2 b,Vector2 c,Vector2 d,float t){float u=1-t;return u*u*u*a+3*u*u*t*b+3*u*t*t*c+t*t*t*d;}

    void DrawSyllables(double beat,double now,double perBeat,float fade)
    {
        Placed.Clear();int used=0;arc.positionCount=0;
        double window=Span/perBeat+.5;
        PreparedPatternSong.Syllable speaking=null;
        foreach(var s in spoken)
        {
            if(s.Start>beat+window)break;
            bool held=s.End-s.Start>=1.5;
            // A held syllable stays in view until it has dived into its well.
            double end=held?Math.Ceiling(s.End-1e-6):s.Start;
            if(end<beat-window)continue;
            if(s.Start<=beat&&beat<Math.Max(s.End,s.Start+.25))speaking=s;
            double onset=midi.Cycles.SecondsAt(s.Start);float e=(float)(now-onset);
            float x=(float)((s.Start-beat)*perBeat);
            bool down=s.Metric>=1;
            float rest=down?Well+.08f:Well+Profile(s.Start)+.08f;
            Vector2 at;string state;
            if(held&&beat>=s.Start)
            {
                // Float along an arc from the start slot to the well at the end, then dive in.
                float length=(float)((end-s.Start)*perBeat);double u=(beat-s.Start)/Math.Max(1e-6,s.End-s.Start);
                var a=new Vector2(0,rest);var b=new Vector2(length*.2f,Well+Crest+.55f);var c=new Vector2(length*.8f,Well+Crest+.55f);var d=new Vector2(length,Well+.08f);
                if(u<1){at=Bezier(a,b,c,new Vector2(length*.92f,Well+Crest+.28f),(float)u);state="floating";}
                else{float dive=Mathf.Clamp01((float)((beat-s.End)/Math.Max(.05,end-s.End+.01)));at=Vector2.Lerp(Bezier(a,b,c,new Vector2(length*.92f,Well+Crest+.28f),1),d,Mathf.SmoothStep(0,1,Mathf.Clamp01(dive*3)));state=dive>=.34f?"in well":"diving";}
                // The arc it floats along, drawn over the teeth.
                var path=new Vector3[24];for(int k=0;k<24;k++){var q=Bezier(a,b,c,d,k/23f);path[k]=new Vector3(x+q.x,.035f,q.y);}
                arc.positionCount=24;arc.SetPositions(path);arc.startColor=arc.endColor=Ink*(.6f*fade);
            }
            else if(e<0)
            {
                // Waiting: a downbeat syllable on the crest before its drop, an upbeat one on the ramp.
                at=down?new Vector2(-.14f,Well+Crest+.08f):new Vector2(0,rest);state="waiting";
            }
            else if(down)
            {
                // Drop into the well with a bounce.
                float t=Mathf.Clamp01(e/.24f);
                at=Bezier(new Vector2(-.14f,Well+Crest+.08f),new Vector2(-.02f,Well+Crest+.2f),new Vector2(.05f,Well+.2f),new Vector2(.06f,rest),t);
                if(e>.24f&&e<.42f)at.y+=.045f*Mathf.Sin((e-.24f)/.18f*Mathf.PI);
                state=e<.24f?"dropping":"in well";if(e>=.24f)at.x=.06f;
            }
            else
            {
                // Bumped up over the saw and back onto the ramp.
                float t=Mathf.Clamp01(e/.3f);float hop=Mathf.Sin(t*Mathf.PI);
                at=new Vector2(.12f*t,rest+hop*(Crest+.14f));state=t<1?"bumped":"on ramp";
            }
            var box=Pooled(used++);box.gameObject.SetActive(true);
            box.transform.localPosition=new Vector3(x+at.x,.04f,at.y+.07f);
            box.Text=s.Text;
            // Emphasis blooms on landing; stressed syllables are brighter at rest too.
            float flash=e>=0?Mathf.Exp(-e/.45f):0;float dist=Mathf.Clamp01(1-Mathf.Abs(x+at.x)/Span);
            float intensity=(s.Stress>0?1.05f:.7f)+flash*(1+3.5f*s.Emphasis)+(state=="floating"?1.2f*s.Emphasis:0);
            var color=Color.Lerp(Ink,Color.white,Mathf.Clamp01(flash+(state=="floating"?.6f:0)))*intensity;color.a=fade*dist*(e>1.2f&&state!="floating"?.55f:1);
            box.TextField.fontMaterial.SetColor(ShaderUtilities.ID_FaceColor,color);
            box.Size=TextSize*(1+.35f*flash*s.Emphasis+(state=="floating"?.2f:0));
            Placed.Add((s,x,x+at.x,at.y-Well,state));
        }
        for(int i=used;i<pool.Count;i++)if(pool[i].gameObject.activeSelf)pool[i].gameObject.SetActive(false);
        // The line being spoken, written out above the rack with its meter.
        var lyrics=source.Lyrics;var line=speaking!=null?lyrics.Lines[speaking.Line]:lyrics.Lines.FirstOrDefault(l=>l.Spoken&&l.Count>0&&l.Start>beat&&l.Start<beat+4);
        if(line==null){caption.Text="";meter.Text="";return;}
        var own=lyrics.Syllables.Skip(line.First).Take(line.Count).ToList();
        var text=new System.Text.StringBuilder();
        for(int i=0;i<own.Count;i++)
        {
            var s=own[i];bool lit=s==speaking;string piece=s.Text+(s.WordEnd?" ":"");
            text.Append(lit?$"<color=#FFFFFF>{piece}</color>":s.Start<beat?$"<color=#6FA8A4>{piece}</color>":$"<color=#9CCFCB>{piece}</color>");
        }
        caption.Text=text.ToString().TrimEnd();caption.transform.localPosition=new Vector3(0,.04f,Well+Crest+1.05f);
        caption.TextField.fontMaterial.SetColor(ShaderUtilities.ID_FaceColor,Color.white*fade);
        meter.Text=$"{line.Meter}   {line.Stresses}";meter.transform.localPosition=new Vector3(0,.04f,Well+Crest+.72f);
        meter.TextField.fontMaterial.SetColor(ShaderUtilities.ID_FaceColor,new Color(.5f,.74f,.72f,.8f*fade));
    }
    void OnDisable()
    {
        if(root!=null)Destroy(root.gameObject);root=null;pool.Clear();sparks.Clear();
        if(glow!=null)Destroy(glow);if(steel!=null)Destroy(steel);if(body!=null)Destroy(body);
    }
}
