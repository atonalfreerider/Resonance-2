using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

// Lyric mode's reader, on the drum wheel. The syllable line runs across the strip just above the
// disc and is joined to it at twelve o'clock (the comb, where the dimples are struck) by a stem
// that flashes with every drum hit. Every syllable, sung or spoken, is shoved onto the line
// exactly on its onset: a beat drops in diagonally from the upper left, an off-beat slides up
// from the lower right, and each pushes the one before out along its own path. No bounce, no
// overshoot. The syllable is bold and set so its fastest-to-read letter (the optimal recognition
// point, about a third of the way in) sits on the stem. It lands in a white bloom that dies away
// fast into the colour of the chord of the moment. A held syllable trails a bar that runs out
// with it, trembling under vibrato. The line being heard is written above. Text meshes are
// rebuilt only when their syllable changes.
[DefaultExecutionOrder(80)]
public sealed class DrumLyricRack : MonoBehaviour
{
    Main main;MidiPlayer midi;DrumPatternDeck deck;DominantChordOutline region;PreparedPatternSong source;
    Transform root;LineRenderer centerline,hold,stem,tick;Material glow;
    TextBox caption;
    sealed class Slot {public TextBox Box;public Material Face;public PreparedPatternSong.Syllable Syllable;public float Orp;}
    readonly Slot[] reader=new Slot[2];int shown=-1;
    PreparedPatternSong.Syllable[] syllables=Array.Empty<PreparedPatternSong.Syllable>();double[] onsets=Array.Empty<double>();
    // The drum hits (score seconds, weight 0..1 by drum and velocity) that flash the stem.
    double[] hitTime=Array.Empty<double>();float[] hitWeight=Array.Empty<float>();
    float visible;
    public bool Shown=>visible>.5f;
    public float Visibility=>visible;
    // Deck-local geometry (the disc's rim is 1.15), up the screen from the rim: the syllable
    // line, the line being heard. A beat starts its shove at Drop from its place, an off-beat at Rise.
    public const float Edge=1.15f,Center=1.6f,CaptionZ=2.72f;
    static readonly Vector2 Drop=new(-.75f,.55f),Rise=new(.75f,-.55f);
    const float ReaderSize=7.2f;
    // The lyric camera frames Tall deck units around Middle: the whole disc, the line and the caption.
    public const float Tall=4.5f,Middle=.82f;
    public float Span {get;private set;}=3;
    public double PerBeat {get;private set;}
    // For validation: the syllable on the line, the side it came from (+1 a beat, -1 off it), how far
    // it still is from its place (x, z), where its recognition letter sits, its colour, the stem's flash.
    public PreparedPatternSong.Syllable ReaderSyllable=>shown>=0?syllables[shown]:null;
    public int ReaderFrom {get;private set;}
    public Vector2 ReaderShift {get;private set;}
    public float ReaderOffset=>ReaderShift.magnitude;
    public float ReaderOrpX {get;private set;}
    public Color ReaderColor {get;private set;}
    public Color ChordColor {get;private set;}
    public float Pulse {get;private set;}
    public string Caption=>caption!=null?caption.TextField.text:"";
    public bool Holding=>hold!=null&&hold.positionCount==2;
    static readonly Color Ink=new(.5f,.74f,.72f);
    Gradient lineGradient;float lineFade=-1,lineSpan=-1;string captionKey="";
    readonly Vector3[] pair=new Vector3[2];

    void OnEnable()
    {
        if(!Application.isPlaying)return;
        main=GetComponent<Main>();midi=GetComponent<MidiPlayer>();deck=GetComponent<DrumPatternDeck>();region=GetComponent<DominantChordOutline>();
        // Not a child of the scene root: the views' opacity pass would override the glow.
        root=new GameObject("Drum lyric reader · syllable line on the comb").transform;
        glow=new Material(Resources.Load<Shader>("HarmonicGlow"));glow.SetColor("_BaseColor",Color.white*2);glow.renderQueue=3102;
        centerline=Line("Syllable line",.006f,Ink);hold=Line("Held syllable",.03f,Ink);
        stem=Line("Stem from the comb",.016f,Ink);tick=Line("Reader reticle",.014f,Color.white);
        caption=Text("",2.8f,TextAlignmentOptions.Center);caption.TextField.richText=true;
        for(int i=0;i<2;i++)reader[i]=NewSlot(ReaderSize);
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
    Slot NewSlot(float size)
    {
        var box=Text("",size,TextAlignmentOptions.Left);
        box.TextField.fontStyle=FontStyles.Bold|FontStyles.UpperCase;
        box.gameObject.SetActive(false);return new Slot{Box=box,Face=box.TextField.fontMaterial};
    }
    // A drum's weight: how bright a flash its hit sends up the stem.
    public static float Weight(int pitch)=>pitch switch{35 or 36=>1f,38 or 40=>.9f,37 or 39=>.75f,41 or 43 or 45 or 47 or 48 or 50=>.7f,49 or 55 or 57=>.65f,51 or 53 or 59=>.45f,42 or 44 or 46=>.35f,_=>.4f};
    // The optimal recognition point: the letter the eye reads a word from fastest.
    public static int Recognition(int letters)=>letters<=1?0:letters<=5?1:letters<=9?2:letters<=13?3:4;
    void Load()
    {
        source=midi.Prepared;
        syllables=source?.Lyrics?.Syllables?.OrderBy(s=>s.Start).ToArray()??Array.Empty<PreparedPatternSong.Syllable>();
        onsets=syllables.Select(s=>midi.Cycles.SecondsAt(s.Start)).ToArray();
        var groups=(source?.Notes??Array.Empty<MidiCycleAnalysis.Hit>()).Where(n=>n.Channel==10).GroupBy(n=>Math.Round(n.Beat*96)).OrderBy(g=>g.Key)
            .Select(g=>(beat:g.Min(n=>n.Beat),weight:g.Max(n=>Weight(n.Pitch)*(.55f+.45f*Mathf.Clamp01(n.Velocity))))).ToArray();
        hitTime=groups.Select(g=>midi.Cycles.SecondsAt(g.beat)).ToArray();hitWeight=groups.Select(g=>g.weight).ToArray();
        foreach(var slot in reader){slot.Syllable=null;slot.Box.gameObject.SetActive(false);}
        shown=-1;captionKey="";
    }
    public bool HasLyrics=>syllables.Length>0;

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
        // Rim speed (one revolution per bar) sets how long a held syllable's bar is.
        double perBeat=2*Math.PI*Edge/Math.Max(1,barLength);PerBeat=perBeat;
        float fade=Mathf.SmoothStep(0,1,visible);
        var view=Camera.main;float aspect=view!=null?view.aspect:1.6f;
        Span=Mathf.Clamp(Tall*.5f*aspect-.35f,2.2f,9);
        ChordColor=region!=null&&region.HasRegion?region.RegionColor:Ink;
        DrawLine(fade);
        DrawStem(now,fade);
        DrawReader(now,perBeat,fade);
    }
    PreparedPatternSong.DrumBar CurrentBar(double beat)
    {
        var bars=source.DrumBars;if(bars==null||bars.Length==0)return null;
        int lo=0,hi=bars.Length-1,found=0;
        while(lo<=hi){int mid=(lo+hi)/2;if(bars[mid].Start<=beat+1e-6){found=mid;lo=mid+1;}else hi=mid-1;}
        return bars[found];
    }
    // The syllable line, fading out at both ends; rewritten only when the fade or the width changes.
    void DrawLine(float fade)
    {
        if(lineFade==fade&&lineSpan==Span)return;
        lineFade=fade;lineSpan=Span;
        pair[0]=new Vector3(-Span,.03f,Center);pair[1]=new Vector3(Span,.03f,Center);
        centerline.positionCount=2;centerline.SetPositions(pair);
        lineGradient??=new Gradient();
        lineGradient.SetKeys(new[]{new GradientColorKey(Ink*.8f,0),new GradientColorKey(Ink*.8f,1)},new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(.6f*fade,.25f),new GradientAlphaKey(.6f*fade,.75f),new GradientAlphaKey(0,1)});
        centerline.colorGradient=lineGradient;
    }
    // The stem joins the comb to the line and flashes with each drum hit, brightest for the kick.
    void DrawStem(double now,float fade)
    {
        int lo=0,hi=hitTime.Length;while(lo<hi){int mid=(lo+hi)/2;if(hitTime[mid]<=now)lo=mid+1;else hi=mid;}
        Pulse=lo>0?hitWeight[lo-1]*Mathf.Exp(-(float)(now-hitTime[lo-1])/.07f):0;
        pair[0]=new Vector3(0,.035f,Edge+.005f);pair[1]=new Vector3(0,.035f,Center);  // up to the line, behind the letters
        stem.positionCount=2;stem.SetPositions(pair);stem.widthMultiplier=.016f+.02f*Pulse;
        var c=Color.Lerp(Ink,Color.white,Pulse)*(1+3*Pulse);c.a=fade;stem.startColor=stem.endColor=c;
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
        // Set the recognition letter on the stem: its centre in the text's own x.
        var info=slot.Box.TextField.textInfo;int count=info.characterCount,at=Mathf.Clamp(Recognition(count),0,Math.Max(0,count-1));
        slot.Orp=count==0?0:(info.characterInfo[at].vertex_BL.position.x+info.characterInfo[at].vertex_TR.position.x)*.5f;
    }
    void DrawReader(double now,double perBeat,float fade)
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
        if(k<0){foreach(var r in reader)if(r.Box.gameObject.activeSelf)r.Box.gameObject.SetActive(false);hold.positionCount=0;tick.positionCount=0;ReaderShift=Vector2.zero;return;}
        var s=syllables[k];double gap=k>0?onsets[k]-onsets[k-1]:1;float lead=Lead(gap);
        float e=(float)(now-onsets[k]);
        // The shove: eased hard onto the line along its diagonal, landing exactly on the onset, never past it.
        float p=Mathf.Clamp01((e+lead)/lead),ease=1-(1-p)*(1-p)*(1-p);
        bool beatSyllable=s.Metric>=1;ReaderFrom=beatSyllable?1:-1;
        var path=beatSyllable?Drop:Rise;
        var shift=path*(1-ease);
        // Vibrato: a tremor while it is held.
        if(!s.Spoken&&s.Vibrato>0&&midi.Cycles.BeatAt(now)>=s.VibratoStart&&e>0)shift.y+=.018f*Mathf.Clamp(s.Vibrato/.35f,.5f,1.5f)*Mathf.Sin((float)now*Mathf.PI*2*(s.VibratoRate>0?s.VibratoRate:5.5f));
        ReaderShift=shift;
        // The bloom: white with emphasis on landing, dying away fast into the chord of the moment.
        float flash=e>=0?Mathf.Exp(-e/.09f):0,bold=1+(1f+.9f*s.Emphasis)*flash;  // bright enough to flare, not to blow the letters out
        var color=Color.Lerp(ChordColor,Color.white,Mathf.Clamp01(flash*1.2f))*bold;color.a=fade;
        incoming.Face.SetColor(ShaderUtilities.ID_FaceColor,color);ReaderColor=color;
        incoming.Box.transform.localScale=Vector3.one*(1+.08f*flash);
        Place(incoming,shift);
        ReaderOrpX=incoming.Box.transform.localPosition.x+incoming.Orp*incoming.Box.transform.localScale.x;
        // The one before is pushed on along the same path and fades.
        if(k>0&&outgoing.Syllable==syllables[k-1]&&p<1)  // after a seek the other slot may hold anything
        {
            outgoing.Box.transform.localScale=Vector3.one;
            Place(outgoing,-path*ease);
            var c=Color.Lerp(ChordColor,Ink,.3f);c.a=fade*(1-ease);outgoing.Face.SetColor(ShaderUtilities.ID_FaceColor,c);
        }
        else if(outgoing.Box.gameObject.activeSelf)outgoing.Box.gameObject.SetActive(false);
        // The reticle over the recognition letter, the stem under it.
        var mark=Color.white*(1+2*flash);mark.a=fade;
        pair[0]=new Vector3(0,.04f,Center+.33f);pair[1]=new Vector3(0,.04f,Center+.43f);
        tick.positionCount=2;tick.SetPositions(pair);tick.startColor=tick.endColor=mark;
        // A held syllable trails a bar that runs out with it.
        double left=s.End-midi.Cycles.BeatAt(now);
        if(e>=0&&s.End-s.Start>=1&&left>0)
        {
            var bounds=incoming.Box.TextField.textBounds;float x0=incoming.Box.transform.localPosition.x+bounds.max.x*incoming.Box.transform.localScale.x+.08f;
            pair[0]=new Vector3(x0,.035f,Center+shift.y);pair[1]=new Vector3(x0+(float)(left*perBeat),.035f,Center+shift.y);
            hold.positionCount=2;hold.SetPositions(pair);
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
    // Left-aligned text, moved so its recognition letter sits on x = 0 (the stem), shifted along its path.
    void Place(Slot slot,Vector2 shift)
    {
        if(!slot.Box.gameObject.activeSelf)slot.Box.gameObject.SetActive(true);
        slot.Box.transform.localPosition=new Vector3(shift.x-slot.Orp*slot.Box.transform.localScale.x,.05f,Center+shift.y-.02f);
    }
    void OnDisable()
    {
        if(root!=null)Destroy(root.gameObject);root=null;
        if(glow!=null)Destroy(glow);
    }
}
