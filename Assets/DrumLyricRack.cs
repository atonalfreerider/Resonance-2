using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

// Lyric mode's reader, on the drum wheel: one lyric line across the top of the strip, joined to
// the disc's comb at twelve o'clock (where the dimples are struck) by a stem that flashes with
// every drum hit. The syllable being heard is always centred, set so its fastest-to-read letter
// (the optimal recognition point) sits on the stem, bold and in the colour of the chord of the
// moment. The syllables before it build to the left as a fading trail; each new one slides the
// trail over in the last 30 to 80 ms before its onset and lands exactly on it. As it lands a
// slanted strike cuts across it, down (\) on a beat and up (/) off one, a white bloom that
// fades fast, brighter and wider for the drum hit that lands with it. The next drum hit comes
// in from the right as a translucent steep sawtooth tooth, its cliff reaching the stem as it is
// struck. A held syllable trails a bar that runs out with it, trembling under vibrato. Text
// meshes are rebuilt only when a slot's syllable changes.
[DefaultExecutionOrder(80)]
public sealed class DrumLyricRack : MonoBehaviour
{
    Main main;MidiPlayer midi;DrumPatternDeck deck;DominantChordOutline region;PreparedPatternSong source;
    Transform root;LineRenderer centerline,hold,stem,tick,strike,tooth;Material glow;
    sealed class Slot {public TextBox Box;public Material Face;public PreparedPatternSong.Syllable Syllable;public float Orp,Left,Right;}
    readonly Slot[] reader=new Slot[2];int shown=-1;
    // Trail slots, one per syllable index modulo the pool, so a syllable keeps its slot (and mesh) while it trails.
    const int TrailLength=10;readonly Slot[] trail=new Slot[TrailLength];
    PreparedPatternSong.Syllable[] syllables=Array.Empty<PreparedPatternSong.Syllable>();double[] onsets=Array.Empty<double>();
    // The drum hits (score seconds and beats, weight 0..1 by drum and velocity): the stem's flash,
    // the strike's strength and the incoming tooth.
    double[] hitTime=Array.Empty<double>(),hitBeat=Array.Empty<double>();float[] hitWeight=Array.Empty<float>();
    float visible;
    public bool Shown=>visible>.5f;
    public float Visibility=>visible;
    // Deck-local geometry (the disc's rim is 1.15): the lyric line above it.
    public const float Edge=1.15f,Center=1.6f;
    const float ReaderSize=7.2f,TrailSize=4.3f,Slash=.55f;
    // Where a syllable starts its slide from its place: a beat from the upper left, an off-beat from the lower right.
    static readonly Vector2 Drop=new(-.55f,.4f),Rise=new(.55f,-.4f);
    // The lyric camera frames Tall deck units around Middle: the whole disc and the line with its strikes.
    public const float Tall=4.1f,Middle=.62f;
    public float Span {get;private set;}=3;
    public double PerBeat {get;private set;}
    // For validation: the syllable on the line, its strike's direction (+1 down on a beat, -1 up
    // off it) and brightness, where its recognition letter sits, its colour, the trail, the stem's
    // flash and the incoming tooth.
    public PreparedPatternSong.Syllable ReaderSyllable=>shown>=0?syllables[shown]:null;
    public int ReaderFrom {get;private set;}
    public Vector2 ReaderShift {get;private set;}
    public float Strike {get;private set;}
    public float ReaderOrpX {get;private set;}
    public Color ReaderColor {get;private set;}
    public Color ChordColor {get;private set;}
    public int TrailCount {get;private set;}
    public float TrailNearestRight {get;private set;}
    public float TrailNearestAlpha {get;private set;}
    public float ReaderLeft {get;private set;}
    public float Pulse {get;private set;}
    public double NextToothBeat {get;private set;}=double.NaN;
    public float NextToothX {get;private set;}
    public bool Holding=>hold!=null&&hold.positionCount==2;
    static readonly Color Ink=new(.5f,.74f,.72f);
    Gradient lineGradient;float lineFade=-1,lineSpan=-1;
    readonly Vector3[] pair=new Vector3[2],saw=new Vector3[3];

    void OnEnable()
    {
        if(!Application.isPlaying)return;
        main=GetComponent<Main>();midi=GetComponent<MidiPlayer>();deck=GetComponent<DrumPatternDeck>();region=GetComponent<DominantChordOutline>();
        // Not a child of the scene root: the views' opacity pass would override the glow.
        root=new GameObject("Drum lyric reader · lyric line on the comb").transform;
        glow=new Material(Resources.Load<Shader>("HarmonicGlow"));glow.SetColor("_BaseColor",Color.white*2);glow.renderQueue=3102;
        centerline=Line("Lyric line",.006f,Ink);hold=Line("Held syllable",.03f,Ink);
        stem=Line("Stem from the comb",.016f,Ink);tick=Line("Reader reticle",.014f,Color.white);
        strike=Line("Strike",.04f,Color.white);tooth=Line("Next drum hit",.02f,Ink);tooth.numCapVertices=0;tooth.numCornerVertices=0;
        for(int i=0;i<2;i++)reader[i]=NewSlot(ReaderSize,true);
        for(int i=0;i<TrailLength;i++)trail[i]=NewSlot(TrailSize,true);
        root.gameObject.SetActive(false);
    }
    LineRenderer Line(string name,float width,Color color)
    {
        var go=new GameObject(name);go.transform.SetParent(root,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=glow;l.useWorldSpace=false;l.widthMultiplier=width;
        l.numCapVertices=2;l.startColor=l.endColor=color;l.positionCount=0;return l;
    }
    Slot NewSlot(float size,bool bold)
    {
        var box=TextBox.Create("",TextAlignmentOptions.Left);box.transform.SetParent(root,false);box.Size=size;
        // Lying flat on the deck, readable from above with twelve o'clock up.
        box.transform.localRotation=Quaternion.Euler(90,0,0);box.TextField.fontMaterial.renderQueue=3103;
        box.TextField.textWrappingMode=TextWrappingModes.NoWrap;
        box.TextField.fontStyle=bold?FontStyles.Bold|FontStyles.UpperCase:FontStyles.UpperCase;
        box.gameObject.SetActive(false);return new Slot{Box=box,Face=box.TextField.fontMaterial};
    }
    // A drum's weight: how bright a flash and strike its hit makes.
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
        hitBeat=groups.Select(g=>g.beat).ToArray();hitTime=hitBeat.Select(b=>midi.Cycles.SecondsAt(b)).ToArray();hitWeight=groups.Select(g=>g.weight).ToArray();
        foreach(var slot in reader.Concat(trail)){slot.Syllable=null;slot.Box.gameObject.SetActive(false);}
        shown=-1;
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
        // Rim speed (one revolution per bar): how fast the next hit comes in and how long a hold bar is.
        double perBeat=2*Math.PI*Edge/Math.Max(1,barLength);PerBeat=perBeat;
        float fade=Mathf.SmoothStep(0,1,visible);
        var view=Camera.main;float aspect=view!=null?view.aspect:1.6f;
        Span=Mathf.Clamp(Tall*.5f*aspect-.35f,2.2f,9);
        ChordColor=region!=null&&region.HasRegion?region.RegionColor:Ink;
        DrawLine(fade);
        int hit=HitAfter(now);
        DrawStem(now,hit,fade);
        DrawTooth(beat,hit,perBeat,fade);
        DrawReader(now,perBeat,fade);
    }
    PreparedPatternSong.DrumBar CurrentBar(double beat)
    {
        var bars=source.DrumBars;if(bars==null||bars.Length==0)return null;
        int lo=0,hi=bars.Length-1,found=0;
        while(lo<=hi){int mid=(lo+hi)/2;if(bars[mid].Start<=beat+1e-6){found=mid;lo=mid+1;}else hi=mid-1;}
        return bars[found];
    }
    // The first hit after now.
    int HitAfter(double now){int lo=0,hi=hitTime.Length;while(lo<hi){int mid=(lo+hi)/2;if(hitTime[mid]<=now)lo=mid+1;else hi=mid;}return lo;}
    // The lyric line, fading out at both ends; rewritten only when the fade or the width changes.
    void DrawLine(float fade)
    {
        if(lineFade==fade&&lineSpan==Span)return;
        lineFade=fade;lineSpan=Span;
        pair[0]=new Vector3(-Span,.03f,Center);pair[1]=new Vector3(Span,.03f,Center);
        centerline.positionCount=2;centerline.SetPositions(pair);
        lineGradient??=new Gradient();
        lineGradient.SetKeys(new[]{new GradientColorKey(Ink*.8f,0),new GradientColorKey(Ink*.8f,1)},new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(.45f*fade,.25f),new GradientAlphaKey(.45f*fade,.75f),new GradientAlphaKey(0,1)});
        centerline.colorGradient=lineGradient;
    }
    // The stem joins the comb to the line and flashes with each drum hit, brightest for the kick.
    void DrawStem(double now,int next,float fade)
    {
        Pulse=next>0?hitWeight[next-1]*Mathf.Exp(-(float)(now-hitTime[next-1])/.07f):0;
        pair[0]=new Vector3(0,.035f,Edge+.005f);pair[1]=new Vector3(0,.035f,Center);  // up to the line, behind the letters
        stem.positionCount=2;stem.SetPositions(pair);stem.widthMultiplier=.016f+.02f*Pulse;
        var c=Color.Lerp(Ink,Color.white,Pulse)*(1+3*Pulse);c.a=fade;stem.startColor=stem.endColor=c;
    }
    // The next drum hit, coming in from the right at the rim's speed: a steep translucent tooth
    // whose cliff reaches the stem as it is struck, taller for a heavier hit.
    void DrawTooth(double beat,int next,double perBeat,float fade)
    {
        if(next>=hitBeat.Length){tooth.positionCount=0;NextToothBeat=double.NaN;return;}
        float x=(float)((hitBeat[next]-beat)*perBeat);NextToothBeat=hitBeat[next];NextToothX=x;
        if(x>Span){tooth.positionCount=0;return;}
        float h=.3f+.35f*hitWeight[next],w=.22f;
        saw[0]=new Vector3(x,.03f,Center-h*.5f);saw[1]=new Vector3(x,.03f,Center+h*.5f);saw[2]=new Vector3(x+w,.03f,Center-h*.5f);
        tooth.positionCount=3;tooth.SetPositions(saw);
        var c=Color.Lerp(Ink,Color.white,.4f);c.a=fade*.35f*Mathf.Clamp01((Span-x)/.8f);tooth.startColor=tooth.endColor=c;
    }

    // The syllable whose slide is under way or done: the last with its slide begun.
    static float Lead(double gap)=>(float)Math.Clamp(gap*.6,.04,.1);
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
        // The recognition letter's centre and the text's extent, in the text's own x.
        var info=slot.Box.TextField.textInfo;int count=info.characterCount,at=Mathf.Clamp(Recognition(count),0,Math.Max(0,count-1));
        slot.Orp=count==0?0:(info.characterInfo[at].vertex_BL.position.x+info.characterInfo[at].vertex_TR.position.x)*.5f;
        slot.Left=count==0?0:info.characterInfo[0].vertex_BL.position.x;slot.Right=count==0?0:info.characterInfo[count-1].vertex_TR.position.x;
    }
    // Space between syllable j and the next: none inside a word, a space between words, more between lines.
    float Gap(int j)=>j+1<syllables.Length&&syllables[j].Line!=syllables[j+1].Line?.55f:syllables[j].WordEnd?.2f:.015f;
    void DrawReader(double now,double perBeat,float fade)
    {
        int k=Current(now);
        var incoming=reader[0];var outgoing=reader[1];
        if(k!=shown&&k>=0)
        {
            if(reader[0].Syllable==syllables[k]){incoming=reader[0];outgoing=reader[1];}
            else{(reader[0],reader[1])=(reader[1],reader[0]);incoming=reader[0];outgoing=reader[1];Put(incoming,syllables[k]);}
            shown=k;
        }
        if(k<0)
        {
            foreach(var r in reader.Concat(trail))if(r.Box.gameObject.activeSelf)r.Box.gameObject.SetActive(false);
            hold.positionCount=0;tick.positionCount=0;strike.positionCount=0;TrailCount=0;Strike=0;return;
        }
        var s=syllables[k];double gap=k>0?onsets[k]-onsets[k-1]:1;float lead=Lead(gap);
        float e=(float)(now-onsets[k]);
        // The slide: eased hard, done exactly on the onset, never past it.
        float p=Mathf.Clamp01((e+lead)/lead),ease=1-(1-p)*(1-p)*(1-p);
        ReaderFrom=s.Metric>=1?1:-1;
        // The syllable: centred on its recognition letter, in the chord's colour, fading in with the slide.
        float tremor=!s.Spoken&&s.Vibrato>0&&midi.Cycles.BeatAt(now)>=s.VibratoStart&&e>0?.018f*Mathf.Clamp(s.Vibrato/.35f,.5f,1.5f)*Mathf.Sin((float)now*Mathf.PI*2*(s.VibratoRate>0?s.VibratoRate:5.5f)):0;
        // The slide: in along its diagonal, eased hard, landing exactly on the onset, never past it.
        var shift=(ReaderFrom>0?Drop:Rise)*(1-ease);ReaderShift=shift;
        Place(incoming,-incoming.Orp+shift.x,Center+tremor+shift.y);
        // In the chord's colour, with a small bloom as it lands.
        float hit=e>=0?Mathf.Exp(-e/.08f):0;
        var color=Color.Lerp(ChordColor*1.15f,Color.white,.45f*hit)*(1+(.6f+.5f*s.Emphasis)*hit);color.a=fade*Mathf.Max(.35f,ease);incoming.Face.SetColor(ShaderUtilities.ID_FaceColor,color);ReaderColor=color;
        ReaderOrpX=incoming.Box.transform.localPosition.x+incoming.Orp-shift.x;
        float left=-incoming.Orp+incoming.Left;ReaderLeft=left;
        // The syllable before, still large, gives way to its trail copy as the new one slides in.
        if(k>0&&outgoing.Syllable==syllables[k-1]&&p<1)
        {
            Place(outgoing,-outgoing.Orp-ease*(outgoing.Right-outgoing.Left)*.5f,Center);
            var c=ChordColor;c.a=fade*(1-ease);outgoing.Face.SetColor(ShaderUtilities.ID_FaceColor,c);
        }
        else if(outgoing.Box.gameObject.activeSelf)outgoing.Box.gameObject.SetActive(false);
        DrawTrail(k,left,ease,fade);
        DrawStrike(k,e,fade);
        // The reticle over the recognition letter.
        var mark=Color.white*1.5f;mark.a=fade;
        pair[0]=new Vector3(0,.04f,Center+.33f);pair[1]=new Vector3(0,.04f,Center+.43f);
        tick.positionCount=2;tick.SetPositions(pair);tick.startColor=tick.endColor=mark;
        // A held syllable trails a bar that runs out with it.
        double remaining=s.End-midi.Cycles.BeatAt(now);
        if(e>=0&&s.End-s.Start>=1&&remaining>0)
        {
            float x0=-incoming.Orp+incoming.Right+.08f;
            pair[0]=new Vector3(x0,.035f,Center+tremor);pair[1]=new Vector3(x0+(float)(remaining*perBeat),.035f,Center+tremor);
            hold.positionCount=2;hold.SetPositions(pair);
            var hc=Color.Lerp(ChordColor,Color.white,.3f)*1.4f;hc.a=fade;hold.startColor=hold.endColor=hc;
        }
        else hold.positionCount=0;
    }
    // The syllables before the current one, right to left from its left edge, fading with distance.
    // While the new one slides in they slide left from where they sat a syllable ago.
    void DrawTrail(int k,float left,float ease,float fade)
    {
        float cursor=left;int count=0;TrailNearestRight=float.NaN;TrailNearestAlpha=0;
        // How far the trail moves this slide: the new syllable's width left of the stem, plus its gap.
        float slide=(1-ease)*(Mathf.Max(0,-left)+(k>0?Gap(k-1):0));
        used.Clear();
        for(int i=1;i<=TrailLength&&k-i>=0;i++)
        {
            int j=k-i;var slot=trail[j%TrailLength];Put(slot,syllables[j]);
            cursor-=Gap(j);
            float right=cursor+slide;float x=right-slot.Right;
            if(x+slot.Left<-Span)break;
            Place(slot,x,Center);
            // Fading with distance: dimmer as well as more transparent, since the face glows.
            float alpha=fade*.8f*Mathf.Pow(.6f,i-1)*Mathf.Clamp01((x+slot.Left+Span)/.8f);
            var c=Color.Lerp(ChordColor,Ink,.35f)*(.25f+.6f*alpha);c.a=alpha;slot.Face.SetColor(ShaderUtilities.ID_FaceColor,c);
            if(i==1){TrailNearestRight=right;TrailNearestAlpha=alpha;}
            used.Add(j%TrailLength);
            cursor-=slot.Right-slot.Left;count++;
        }
        TrailCount=count;
        for(int t=0;t<TrailLength;t++)if(!used.Contains(t)&&trail[t].Box.gameObject.activeSelf)trail[t].Box.gameObject.SetActive(false);
    }
    readonly List<int> used=new(TrailLength);
    // The strike: a slash across the syllable as it lands, down (\) on a beat and up (/) off it.
    // It slices in within 25 ms, flares and widens, then blooms out in about a quarter of a second
    // while its tail chases its head off the end; the drum hit landing with it makes it brighter
    // and wider.
    void DrawStrike(int k,float e,float fade)
    {
        if(e<0){strike.positionCount=0;Strike=0;return;}
        int h=HitAfter(onsets[k]-.06);
        float weight=h<hitTime.Length&&Math.Abs(hitTime[h]-onsets[k])<=.06?hitWeight[h]:.45f;
        const float slice=.025f;
        float cut=Mathf.Clamp01(e/slice),head=1-(1-cut)*(1-cut)*(1-cut);
        float glow=e<=slice?1:Mathf.Exp(-(e-slice)/.07f);
        Strike=glow*(.5f+.5f*weight);
        if(Strike<.01f){strike.positionCount=0;return;}
        float tail=e<=slice?0:Mathf.SmoothStep(0,1,(e-slice)/.22f);
        bool down=ReaderFrom>0;
        var from=new Vector3(-Slash*1.3f,.045f,Center+(down?Slash:-Slash));var to=new Vector3(Slash*1.3f,.045f,Center+(down?-Slash:Slash));
        pair[0]=Vector3.Lerp(from,to,tail*head);pair[1]=Vector3.Lerp(from,to,head);
        strike.positionCount=2;strike.SetPositions(pair);
        strike.widthMultiplier=(.03f+.04f*weight)*(.5f+.9f*glow);
        var c=Color.white*(1+6*Strike);c.a=fade*Mathf.Clamp01(Strike*1.6f);strike.startColor=strike.endColor=c;
    }
    void Place(Slot slot,float x,float z)
    {
        if(!slot.Box.gameObject.activeSelf)slot.Box.gameObject.SetActive(true);
        slot.Box.transform.localPosition=new Vector3(x,.05f,z-.02f);
    }
    void OnDisable()
    {
        if(root!=null)Destroy(root.gameObject);root=null;
        if(glow!=null)Destroy(glow);
    }
}
