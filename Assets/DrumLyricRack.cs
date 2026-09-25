using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

// Lyric mode's reader, on the drum wheel. A centerline runs through the comb at twelve o'clock,
// where the dimples are struck, and every syllable, sung or spoken, is shoved into it exactly on
// its onset: from above on a beat, from below off it, pushing the one before out the same way.
// No bounce, no overshoot. The syllable is bold and set so its fastest-to-read letter (the
// optimal recognition point, about a third of the way in) sits under the reticle on the
// centerline. It lands in a white bloom that resolves into the colour of the chord of the
// moment. A held syllable trails a bar that runs out with it, trembling under vibrato.
// Upcoming syllables ride two lanes toward the comb at the rim's speed, downbeats above the
// centerline and upbeats below, so each is seen coming from the side it will be shoved from.
// Under them a sawtooth rack meshes with the disc, cut by the drum hits (cliff height by drum
// and velocity), and the disc's rim is a ratchet cut by the same hits in its bar. The line
// being heard is written above. Text meshes are rebuilt only when their syllable changes.
[DefaultExecutionOrder(80)]
public sealed class DrumLyricRack : MonoBehaviour
{
    Main main;MidiPlayer midi;DrumPatternDeck deck;DominantChordOutline region;PreparedPatternSong source;
    Transform root;LineRenderer saw,teeth,ratchet,centerline,hold;readonly LineRenderer[] reticle=new LineRenderer[2];Material glow,steel;Mesh body;
    TextBox caption;
    sealed class Slot {public TextBox Box;public Material Face;public PreparedPatternSong.Syllable Syllable;public float Orp;}
    readonly Slot[] reader=new Slot[2];int shown=-1;
    readonly List<Slot> lanes=new();
    PreparedPatternSong.Syllable[] syllables=Array.Empty<PreparedPatternSong.Syllable>();double[] onsets=Array.Empty<double>();
    // The drum teeth: onset beats and weights (0..1) of every hit group in the song.
    double[] toothBeat=Array.Empty<double>();float[] toothDepth=Array.Empty<float>();
    float visible;
    public bool Shown=>visible>.5f;
    public float Visibility=>visible;
    // Deck-local geometry (the disc's rim is 1.15), up the screen from the rim: the rack, the
    // upbeat lane, the centerline, the downbeat lane, the line being heard.
    public const float Edge=1.15f,Well=1.27f,Crest=.2f,UpLane=1.74f,Center=2.26f,DownLane=2.82f,CaptionZ=3.32f,Shove=.62f;
    const float ReaderSize=7.2f,LaneSize=2.4f;
    // The lyric camera frames Tall deck units around Middle; the rack and lanes run as wide as the view.
    public const float Tall=5f,Middle=1.22f;
    public float Span {get;private set;}=3;
    public double PerBeat {get;private set;}
    // For validation: the syllable on the centerline, the side it came from (+1 above, -1 below),
    // its distance from the line, where its recognition letter sits, its colour, and the cliffs.
    public PreparedPatternSong.Syllable ReaderSyllable=>shown>=0?syllables[shown]:null;
    public int ReaderFrom {get;private set;}
    public float ReaderOffset {get;private set;}
    public float ReaderOrpX {get;private set;}
    public Color ReaderColor {get;private set;}
    public Color ChordColor {get;private set;}
    public readonly List<double> VisibleTeeth=new();
    public string Caption=>caption!=null?caption.TextField.text:"";
    public bool Holding=>hold!=null&&hold.positionCount==2;
    // Which lane an upcoming syllable rides: +1 above (a beat), -1 below (off the beat), 0 not shown.
    public int LaneOf(PreparedPatternSong.Syllable s){foreach(var l in lanes)if(l.Syllable==s&&l.Box.gameObject.activeSelf)return l.Box.transform.localPosition.z>Center?1:-1;return 0;}
    static readonly Color Ink=new(.5f,.74f,.72f),Steel=new(.2f,.3f,.3f);
    readonly List<Vector3> top=new(256),bottom=new(256),vertices=new(512);readonly List<int> triangles=new(768);
    Vector3[] ratchetPoints=Array.Empty<Vector3>();
    PreparedPatternSong.DrumBar ratchetBar;(double phase,float depth)[] ratchetHits=Array.Empty<(double,float)>();
    Gradient edgeGradient;float edgeFade=-1,edgeSpan=-1;string captionKey="";

    void OnEnable()
    {
        if(!Application.isPlaying)return;
        main=GetComponent<Main>();midi=GetComponent<MidiPlayer>();deck=GetComponent<DrumPatternDeck>();region=GetComponent<DominantChordOutline>();
        // Not a child of the scene root: the views' opacity pass would override the glow.
        root=new GameObject("Drum lyric reader · centerline at the comb").transform;
        glow=new Material(Resources.Load<Shader>("HarmonicGlow"));glow.SetColor("_BaseColor",Color.white*2);glow.renderQueue=3102;
        steel=new Material(Shader.Find("Universal Render Pipeline/Unlit"));steel.SetColor("_BaseColor",new Color(.028f,.04f,.04f));steel.renderQueue=3100;
        body=new Mesh{name="Rack body"};body.MarkDynamic();var plate=new GameObject("Rack body");plate.transform.SetParent(root,false);plate.AddComponent<MeshFilter>().sharedMesh=body;plate.AddComponent<MeshRenderer>().sharedMaterial=steel;
        saw=Line("Rack saw",.022f,Ink);teeth=Line("Rack teeth",.01f,Steel);ratchet=Line("Drum ratchet",.009f,Steel);
        centerline=Line("Reader centerline",.006f,Ink);hold=Line("Held syllable",.03f,Ink);
        for(int i=0;i<2;i++)reticle[i]=Line("Reader reticle",.014f,Color.white);
        caption=Text("",2.8f,TextAlignmentOptions.Center);caption.TextField.richText=true;
        for(int i=0;i<2;i++)reader[i]=NewSlot(ReaderSize,true);
        root.gameObject.SetActive(false);
    }
    LineRenderer Line(string name,float width,Color color)
    {
        var go=new GameObject(name);go.transform.SetParent(root,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=glow;l.useWorldSpace=false;l.widthMultiplier=width;
        l.numCapVertices=2;l.startColor=l.endColor=color;l.positionCount=0;return l;
    }
    TextBox Text(string text,float size,TextAlignmentOptions align)
    {
        var box=TextBox.Create(text,align);box.transform.SetParent(root,false);box.Size=size;
        // Lying flat on the deck, readable from above with twelve o'clock up.
        box.transform.localRotation=Quaternion.Euler(90,0,0);box.TextField.fontMaterial.renderQueue=3103;
        box.TextField.textWrappingMode=TextWrappingModes.NoWrap;return box;
    }
    Slot NewSlot(float size,bool bold)
    {
        var box=Text("",size,TextAlignmentOptions.Left);
        box.TextField.fontStyle=bold?FontStyles.Bold|FontStyles.UpperCase:FontStyles.UpperCase;
        box.gameObject.SetActive(false);return new Slot{Box=box,Face=box.TextField.fontMaterial};
    }
    // A drum's weight in the saw: how tall a cliff its hit cuts.
    public static float Weight(int pitch)=>pitch switch{35 or 36=>1f,38 or 40=>.9f,37 or 39=>.75f,41 or 43 or 45 or 47 or 48 or 50=>.7f,49 or 55 or 57=>.65f,51 or 53 or 59=>.45f,42 or 44 or 46=>.35f,_=>.4f};
    // The optimal recognition point: the letter the eye reads a word from fastest.
    public static int Recognition(int letters)=>letters<=1?0:letters<=5?1:letters<=9?2:letters<=13?3:4;
    void Load()
    {
        source=midi.Prepared;
        syllables=source?.Lyrics?.Syllables?.OrderBy(s=>s.Start).ToArray()??Array.Empty<PreparedPatternSong.Syllable>();
        onsets=syllables.Select(s=>midi.Cycles.SecondsAt(s.Start)).ToArray();
        var groups=(source?.Notes??Array.Empty<MidiCycleAnalysis.Hit>()).Where(n=>n.Channel==10).GroupBy(n=>Math.Round(n.Beat*96)).OrderBy(g=>g.Key)
            .Select(g=>(beat:g.Min(n=>n.Beat),depth:g.Max(n=>Weight(n.Pitch)*(.55f+.45f*Mathf.Clamp01(n.Velocity))))).ToArray();
        toothBeat=groups.Select(g=>g.beat).ToArray();toothDepth=groups.Select(g=>g.depth).ToArray();
        foreach(var slot in lanes.Concat(reader)){slot.Syllable=null;slot.Box.gameObject.SetActive(false);}
        shown=-1;captionKey="";
    }
    public bool HasLyrics=>syllables.Length>0;
    int ToothAfter(double beat){int lo=0,hi=toothBeat.Length;while(lo<hi){int mid=(lo+hi)/2;if(toothBeat[mid]<=beat+1e-6)lo=mid+1;else hi=mid;}return lo;}
    // The saw's height above the wells at a beat: rising over at most the beat before the next
    // hit to that hit's crest, then dropping at the hit.
    public float Profile(double beat)
    {
        int k=ToothAfter(beat);if(k>=toothBeat.Length)return 0;
        double next=toothBeat[k],prev=k>0?toothBeat[k-1]:next-1,from=Math.Max(prev,next-1);
        return (float)(Crest*toothDepth[k]*Math.Clamp((beat-from)/Math.Max(1e-6,next-from),0,1));
    }

    void LateUpdate()
    {
        using var perf=Perf.Rack.Auto();
        if(root==null)return;
        if(midi==null||deck==null){midi=GetComponent<MidiPlayer>();deck=GetComponent<DrumPatternDeck>();return;}
        var views=GetComponent<VisualizationViews>();
        bool want=views!=null&&views.Current==VisualizationViews.View.Lyrics&&deck.WheelTransform!=null&&deck.WheelTransform.gameObject.activeInHierarchy&&midi.Prepared!=null;
        visible=Main.ReducedMotion?(want?1:0):Mathf.MoveTowards(visible,want?1:0,Time.unscaledDeltaTime*3);
        if(visible<=0){if(root.gameObject.activeSelf)root.gameObject.SetActive(false);return;}
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
        ChordColor=region!=null&&region.HasRegion?region.RegionColor:Ink;
        DrawRack(beat,perBeat,fade);
        DrawRatchet(beat,bar,fade);
        DrawReader(beat,now,perBeat,fade);
        DrawLanes(beat,perBeat,fade);
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
        vertices.Clear();triangles.Clear();
        foreach(var q in top){vertices.Add(new Vector3(q.x,.02f,Edge+.07f));vertices.Add(new Vector3(q.x,.02f,q.z));}
        for(int i=0;i+1<top.Count;i++){int k=i*2;triangles.Add(k);triangles.Add(k+1);triangles.Add(k+3);triangles.Add(k);triangles.Add(k+3);triangles.Add(k+2);}
        body.Clear();body.SetVertices(vertices);body.SetTriangles(triangles,0);body.RecalculateBounds();
        if(edgeFade!=fade||edgeSpan!=Span)
        {
            edgeFade=fade;edgeSpan=Span;steel.SetColor("_BaseColor",new Color(.028f,.04f,.04f,fade));
            edgeGradient??=new Gradient();
            edgeGradient.SetKeys(new[]{new GradientColorKey(Ink*1.3f,0),new GradientColorKey(Ink*1.3f,1)},new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(fade,.2f),new GradientAlphaKey(fade,.8f),new GradientAlphaKey(0,1)});
            saw.colorGradient=edgeGradient;teeth.colorGradient=edgeGradient;
            centerline.positionCount=2;centerline.SetPositions(new[]{new Vector3(-Span,.03f,Center),new Vector3(Span,.03f,Center)});
            var faint=new Gradient();faint.SetKeys(new[]{new GradientColorKey(Ink*.6f,0),new GradientColorKey(Ink*.6f,1)},new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(.5f*fade,.3f),new GradientAlphaKey(.5f*fade,.7f),new GradientAlphaKey(0,1)});
            centerline.colorGradient=faint;
        }
    }
    // The disc's ratchet: cut by this bar's drum hits, turning with the disc.
    void DrawRatchet(double beat,PreparedPatternSong.DrumBar bar,float fade)
    {
        if(bar==null||bar.Hits==null||bar.Hits.Length==0){ratchet.positionCount=0;return;}
        double length=Math.Max(1e-6,bar.End-bar.Start),turn=(beat-bar.Start)/length;
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

    // The syllable whose shove is under way or done: the last with its shove begun.
    static float Lead(double gap)=>(float)Math.Clamp(gap*.6,.03,.08);
    int Current(double now)
    {
        int lo=0,hi=onsets.Length;while(lo<hi){int mid=(lo+hi)/2;if(onsets[mid]<=now+.08)lo=mid+1;else hi=mid;}
        int k=lo-1;
        while(k>=0){double gap=k>0?onsets[k]-onsets[k-1]:1;if(onsets[k]-Lead(gap)<=now)break;k--;}
        return k;
    }
    void Put(Slot slot,PreparedPatternSong.Syllable s)
    {
        if(slot.Syllable==s)return;
        slot.Syllable=s;slot.Box.Text=s.Text;slot.Box.TextField.ForceMeshUpdate(true,true);  // also while the slot is hidden
        // Set the recognition letter on the centerline: its centre in the text's own x.
        var info=slot.Box.TextField.textInfo;int count=info.characterCount,at=Mathf.Clamp(Recognition(count),0,Math.Max(0,count-1));
        slot.Orp=count==0?0:(info.characterInfo[at].vertex_BL.position.x+info.characterInfo[at].vertex_TR.position.x)*.5f;
    }
    void DrawReader(double beat,double now,double perBeat,float fade)
    {
        int k=Current(now);
        var incoming=reader[0];var outgoing=reader[1];
        if(k!=shown&&k>=0)
        {
            // The new syllable takes the free slot; the one on the line becomes the outgoing one.
            if(reader[0].Syllable==syllables[k]){incoming=reader[0];outgoing=reader[1];}
            else{(reader[0],reader[1])=(reader[1],reader[0]);incoming=reader[0];outgoing=reader[1];Put(incoming,syllables[k]);}
            shown=k;
        }
        if(k<0){foreach(var r in reader)if(r.Box.gameObject.activeSelf)r.Box.gameObject.SetActive(false);hold.positionCount=0;foreach(var t in reticle)t.positionCount=0;ReaderOffset=0;return;}
        var s=syllables[k];double gap=k>0?onsets[k]-onsets[k-1]:1;float lead=Lead(gap);
        float e=(float)(now-onsets[k]);
        // The shove: eased hard into the line, landing exactly on the onset, never past it.
        float p=Mathf.Clamp01((e+lead)/lead),ease=1-(1-p)*(1-p)*(1-p);
        int from=s.Metric>=1?1:-1;ReaderFrom=from;
        float offset=from*Shove*(1-ease);
        // Vibrato: a tremor while it is held.
        if(!s.Spoken&&s.Vibrato>0&&midi.Cycles.BeatAt(now)>=s.VibratoStart&&e>0)offset+=.018f*Mathf.Clamp(s.Vibrato/.35f,.5f,1.5f)*Mathf.Sin((float)now*Mathf.PI*2*(s.VibratoRate>0?s.VibratoRate:5.5f));
        ReaderOffset=offset;
        // The bloom: white with emphasis on landing, resolving into the chord of the moment.
        float flash=e>=0?Mathf.Exp(-e/.22f):0,bold=1+(2.4f+3f*s.Emphasis)*flash;
        var color=Color.Lerp(ChordColor,Color.white,Mathf.Clamp01(flash*1.2f))*bold;color.a=fade;
        incoming.Face.SetColor(ShaderUtilities.ID_FaceColor,color);ReaderColor=color;
        incoming.Box.transform.localScale=Vector3.one*(1+.08f*flash);
        Place(incoming,Center+offset,1,fade);
        ReaderOrpX=incoming.Box.transform.localPosition.x+incoming.Orp*incoming.Box.transform.localScale.x;
        // The one before is pushed out the same way and fades.
        if(outgoing.Syllable!=null&&p<1)
        {
            Place(outgoing,Center-from*Shove*ease,1-ease,fade);
            var c=Color.Lerp(ChordColor,Ink,.3f);c.a=fade*(1-ease);outgoing.Face.SetColor(ShaderUtilities.ID_FaceColor,c);
        }
        else if(outgoing.Box.gameObject.activeSelf)outgoing.Box.gameObject.SetActive(false);
        // Reticle over and under the recognition letter.
        var tick=Color.white*(1+2*flash);tick.a=fade;
        reticle[0].positionCount=reticle[1].positionCount=2;
        reticle[0].SetPositions(new[]{new Vector3(0,.04f,Center+.36f),new Vector3(0,.04f,Center+.46f)});
        reticle[1].SetPositions(new[]{new Vector3(0,.04f,Center-.36f),new Vector3(0,.04f,Center-.46f)});
        reticle[0].startColor=reticle[0].endColor=reticle[1].startColor=reticle[1].endColor=tick;
        // A held syllable trails a bar that runs out with it.
        double left=s.End-midi.Cycles.BeatAt(now);
        if(e>=0&&s.End-s.Start>=1&&left>0)
        {
            var bounds=incoming.Box.TextField.textBounds;float x0=incoming.Box.transform.localPosition.x+bounds.max.x*incoming.Box.transform.localScale.x+.08f;
            hold.positionCount=2;hold.SetPositions(new[]{new Vector3(x0,.035f,Center+offset),new Vector3(x0+(float)(left*perBeat),.035f,Center+offset)});
            var hc=Color.Lerp(ChordColor,Color.white,.3f)*1.4f;hc.a=fade;hold.startColor=hold.endColor=hc;
        }
        else hold.positionCount=0;
        // The line being heard, above; rebuilt only when the syllable changes.
        var lyrics=source.Lyrics;var line=lyrics.Lines[s.Line];
        string key=s.Line+":"+k;
        if(key!=captionKey)
        {
            captionKey=key;var text=new System.Text.StringBuilder();
            for(int i=line.First;i<line.First+line.Count;i++)
            {
                var x=lyrics.Syllables[i];string piece=x.Text+(x.WordEnd?" ":"");
                text.Append(x==s?$"<b><color=#FFFFFF>{piece}</color></b>":x.Start<s.Start?$"<color=#6FA8A4>{piece}</color>":$"<color=#9CCFCB>{piece}</color>");
            }
            caption.Text=text.ToString().TrimEnd();caption.transform.localPosition=new Vector3(0,.04f,CaptionZ);
        }
        caption.TextField.fontMaterial.SetColor(ShaderUtilities.ID_FaceColor,Color.white*fade);
    }
    void Place(Slot slot,float z,float alpha,float fade)
    {
        if(!slot.Box.gameObject.activeSelf)slot.Box.gameObject.SetActive(true);
        // Left-aligned text, moved so its recognition letter sits on x = 0 (the comb).
        slot.Box.transform.localPosition=new Vector3(-slot.Orp*slot.Box.transform.localScale.x,.05f,z-.02f);
    }
    // Upcoming syllables ride their lanes toward the comb: beats above, off-beats below.
    void DrawLanes(double beat,double perBeat,float fade)
    {
        int used=0;double reach=Span/perBeat;
        int first=shown+1;
        for(int k=Math.Max(0,first);k<syllables.Length;k++)
        {
            var s=syllables[k];if(s.Start>beat+reach)break;
            float x=(float)((s.Start-beat)*perBeat);if(x<.9f)continue;
            while(lanes.Count<=used)lanes.Add(NewSlot(LaneSize,false));
            var slot=lanes[used++];
            if(slot.Syllable!=s){slot.Syllable=s;slot.Box.Text=s.Text;}
            if(!slot.Box.gameObject.activeSelf)slot.Box.gameObject.SetActive(true);
            bool down=s.Metric>=1;
            slot.Box.transform.localPosition=new Vector3(x-.1f,.04f,(down?DownLane:UpLane)-.02f);
            float a=fade*Mathf.Clamp01((x-.9f)/.6f)*Mathf.Clamp01((Span-x)/.8f);
            var c=(s.Stress>0?Ink*1.15f:Ink*.8f);c.a=a;slot.Face.SetColor(ShaderUtilities.ID_FaceColor,c);
        }
        for(int i=used;i<lanes.Count;i++)if(lanes[i].Box.gameObject.activeSelf){lanes[i].Box.gameObject.SetActive(false);lanes[i].Syllable=null;}
    }
    void OnDisable()
    {
        if(root!=null)Destroy(root.gameObject);root=null;lanes.Clear();
        if(glow!=null)Destroy(glow);if(steel!=null)Destroy(steel);if(body!=null)Destroy(body);
    }
}
