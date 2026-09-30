using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

// The lyric strip: one lyric line, drawn on its own stage by its own camera and composited,
// transparent, over whatever part of the screen the views give it (between the pattern wheels
// and the torus), so it hides nothing behind it. The line is a groove of slashes scrolling
// left at the bar's speed, one for every drum hit: a "\" where the hit falls on a beat (tallest
// on the downbeat) and a "/" where it falls off the beat. No hit, no slash; no words, no groove. The syllable being heard is always centred, its
// fastest-to-read letter (the optimal recognition point) on the reticle, bold and in the colour
// of the chord of the moment, and it slides in along its slash and locks into it exactly on the
// onset: down along the "\" into the well on a beat, up along the "/" onto the crest off it,
// sideways between beats. It lands hard (a beat squashed, an off-beat stretched, harder with
// emphasis) and the slash under it flares white and blooms out. A drawn-out syllable is set
// wide and comes in more slowly, trailing a bar that runs out with it; under vibrato it
// wiggles gently up and down at the vibrato's rate. The syllables before build to the left as
// a fading trail keeping their places in the groove, so the meter reads back as a pattern.
// Text meshes are rebuilt only when a slot's syllable changes.
[DefaultExecutionOrder(80)]
public sealed class DrumLyricRack : MonoBehaviour
{
    Main main;MidiPlayer midi;DominantChordOutline region;PreparedPatternSong source;
    Transform root;Camera stage;LineRenderer centerline,hold,tick,strike,tooth;Material glow,composite;
    RenderTexture hdr,texture;GameObject display;PanelSettings panelSettings;VisualElement displayRoot;Image image;Rect placed;
    readonly List<LineRenderer> slashes=new();
    sealed class Slot {public TextBox Box;public Material Face;public PreparedPatternSong.Syllable Syllable;public float Orp,Left,Right;}
    readonly Slot[] reader=new Slot[2];int shown=-1;
    // Trail slots, one per syllable index modulo the pool, so a syllable keeps its slot (and mesh) while it trails.
    const int TrailLength=10;readonly Slot[] trail=new Slot[TrailLength];
    PreparedPatternSong.Syllable[] syllables=Array.Empty<PreparedPatternSong.Syllable>();double[] onsets=Array.Empty<double>();
    // The drum hits (score seconds and beats, weight 0..1 by drum and velocity): the strike's strength and the incoming tooth.
    double[] hitTime=Array.Empty<double>(),hitBeat=Array.Empty<double>();float[] hitWeight=Array.Empty<float>();
    float visible;
    // The views say where on the screen the strip is (normalized) and whether it is wanted.
    public Rect Viewport=new(0,0,0,0);public bool Wanted;
    // In a headset the strip is shown on a card in the scene instead of its own screen panel:
    // Headless hides the panel, PixelSize fixes the texture's size, Output is the texture.
    public bool Headless;public Vector2Int PixelSize;public Texture Output=>texture;
    // In the headset the stage itself stands in the scene (the eye camera draws it directly) and
    // its own camera stays off: StageRoot is placed by the headset, StageLayer drawn by its eyes.
    public Transform StageRoot=>root;public Camera StageCamera=>stage;public const int StageLayerIndex=StageLayer;
    public bool Shown=>visible>.5f;
    public float Visibility=>visible;
    // Stage geometry: the line at Center, the groove's depth, the strip Tall units high.
    public const float Center=0,Well=.11f,Crest=.1f,Tall=1.9f;
    const float ReaderSize=7.2f,TrailSize=4.3f,Slash=.55f,StageScale=1.25f;const int StageLayer=30;
    static readonly Vector3 StagePosition=new(700,-300,700);
    public float Span {get;private set;}=3;
    public double PerBeat {get;private set;}
    // For validation: the syllable on the line, its entry (+1 down into a well, -1 up onto a
    // crest, 0 sideways), how far it still is from its place, its strike, its recognition letter's
    // x, its colour, its letter spacing, squash and wiggle, the trail, the slashes and the tooth.
    public PreparedPatternSong.Syllable ReaderSyllable=>shown>=0?syllables[shown]:null;
    public int ReaderFrom {get;private set;}
    public Vector2 ReaderShift {get;private set;}
    public float ReaderY {get;private set;}
    public float Strike {get;private set;}
    public float ReaderOrpX {get;private set;}
    public Color ReaderColor {get;private set;}
    public Color ChordColor {get;private set;}
    public float ReaderSpacing {get;private set;}
    public Vector2 ReaderSquash {get;private set;}=Vector2.one;
    public float Shimmer {get;private set;}
    public float Wiggle {get;private set;}
    public int TrailCount {get;private set;}
    public float TrailNearestRight {get;private set;}
    public float TrailNearestAlpha {get;private set;}
    public float TrailNearestY {get;private set;}
    public float ReaderLeft {get;private set;}
    public int SlashCount {get;private set;}
    public double NextToothBeat {get;private set;}=double.NaN;
    public float NextToothX {get;private set;}
    public bool Holding=>hold!=null&&hold.positionCount==2;
    static readonly Color Ink=new(.5f,.74f,.72f);
    Gradient lineGradient;float lineFade=-1,lineSpan=-1;
    readonly Vector3[] pair=new Vector3[2],saw=new Vector3[3];

    void OnEnable()
    {
        if(!Application.isPlaying)return;
        main=GetComponent<Main>();midi=GetComponent<MidiPlayer>();region=GetComponent<DominantChordOutline>();
        // The stage: far from the scene, on its own layer, seen only by its own camera.
        root=new GameObject("Lyric strip · stage").transform;root.position=StagePosition;root.localScale=Vector3.one*StageScale;
        var cam=new GameObject("Lyric strip camera");cam.transform.SetParent(root,false);stage=cam.AddComponent<Camera>();
        // A base camera always clears its colour in URP: it clears to the screen's own background,
        // so the strip has no edge of its own.
        // Rendered to a texture through the torus's post-processing, then composited transparent
        // (glow on nothing) into a panel of its own over the screen.
        stage.stereoTargetEye=StereoTargetEyeMask.None;   // a headset would otherwise render it in stereo to the eyes, leaving its texture empty
        stage.clearFlags=CameraClearFlags.SolidColor;stage.backgroundColor=Color.clear;stage.cullingMask=1<<StageLayer;stage.fieldOfView=30;stage.nearClipPlane=.1f;stage.farClipPlane=40;
        stage.allowHDR=true;stage.allowMSAA=false;stage.depth=-19;stage.enabled=false;
        var data=stage.GetUniversalAdditionalCameraData();data.renderPostProcessing=true;data.renderShadows=false;
        if(Camera.main!=null){data.volumeLayerMask=Camera.main.GetUniversalAdditionalCameraData().volumeLayerMask;Camera.main.cullingMask&=~(1<<StageLayer);}
        composite=new Material(Resources.Load<Shader>("OrreryTransparent"));
        display=new GameObject("Lyric strip display"){hideFlags=HideFlags.HideAndDontSave};
        panelSettings=Instantiate(main.GetComponent<UIDocument>().panelSettings);panelSettings.sortingOrder+=1;
        var document=display.AddComponent<UIDocument>();document.panelSettings=panelSettings;
        displayRoot=document.rootVisualElement;displayRoot.pickingMode=PickingMode.Ignore;
        image=new Image{pickingMode=PickingMode.Ignore,scaleMode=ScaleMode.StretchToFill};image.style.position=Position.Absolute;displayRoot.Add(image);
        displayRoot.style.display=DisplayStyle.None;
        RenderPipelineManager.endCameraRendering+=Composite;
        glow=new Material(Resources.Load<Shader>("HarmonicGlow"));glow.SetColor("_BaseColor",Color.white*2);glow.renderQueue=3102;
        centerline=Line("Lyric line",.006f,Ink);hold=Line("Held syllable",.03f,Ink);tick=Line("Reader reticle",.014f,Color.white);
        strike=Line("Strike",.04f,Color.white);tooth=Line("Next drum hit",.02f,Ink);tooth.numCapVertices=0;tooth.numCornerVertices=0;
        for(int i=0;i<2;i++)reader[i]=NewSlot(ReaderSize);
        for(int i=0;i<TrailLength;i++)trail[i]=NewSlot(TrailSize);
        root.gameObject.SetActive(false);
    }
    static void Layer(GameObject go){go.layer=StageLayer;foreach(Transform t in go.transform)Layer(t.gameObject);}
    LineRenderer Line(string name,float width,Color color)
    {
        var go=new GameObject(name){layer=StageLayer};go.transform.SetParent(root,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=glow;l.useWorldSpace=false;l.widthMultiplier=width;
        l.numCapVertices=2;l.startColor=l.endColor=color;l.positionCount=0;return l;
    }
    Slot NewSlot(float size)
    {
        var box=TextBox.Create("",TextAlignmentOptions.Left);box.transform.SetParent(root,false);box.Size=size;Layer(box.gameObject);
        // Lying flat on the stage, readable from above with the line running left to right.
        box.transform.localRotation=Quaternion.Euler(90,0,0);box.TextField.fontMaterial.renderQueue=3103;
        box.TextField.textWrappingMode=TextWrappingModes.NoWrap;box.TextField.fontStyle=FontStyles.Bold|FontStyles.UpperCase;
        box.gameObject.SetActive(false);return new Slot{Box=box,Face=box.TextField.fontMaterial};
    }
    // A drum's weight: how bright a strike its hit makes, how tall its tooth.
    public static float Weight(int pitch)=>pitch switch{35 or 36=>1f,38 or 40=>.9f,37 or 39=>.75f,41 or 43 or 45 or 47 or 48 or 50=>.7f,49 or 55 or 57=>.65f,51 or 53 or 59=>.45f,42 or 44 or 46=>.35f,_=>.4f};
    // The optimal recognition point: the letter the eye reads a word from fastest.
    public static int Recognition(int letters)=>letters<=1?0:letters<=5?1:letters<=9?2:letters<=13?3:4;
    // Where a syllable sits in the groove: low in the well on a beat (lowest on the downbeat),
    // high on the crest off it, just above the line between beats.
    public static float GrooveY(PreparedPatternSong.Syllable s)=>s.Metric>=2?-Well*1.4f:s.Metric==1?-Well:s.Metric==0?Crest:Crest*.3f;
    // How wide a drawn-out syllable is set: letter spacing grows with the eighths it is held across.
    public static float Spacing(PreparedPatternSong.Syllable s)=>s.Teeth<=2?0:Mathf.Min(28,6f*(s.Teeth-2));
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
        if(midi==null){midi=GetComponent<MidiPlayer>();return;}
        bool want=Wanted&&Viewport.width>.01f&&Viewport.height>.01f&&midi.Prepared!=null&&HasLyricsLoaded();
        visible=Main.ReducedMotion?(want?1:0):Mathf.MoveTowards(visible,want?1:0,Time.unscaledDeltaTime*3);
        if(visible<=0){if(root.gameObject.activeSelf){root.gameObject.SetActive(false);stage.enabled=false;displayRoot.style.display=DisplayStyle.None;}return;}
        if(!root.gameObject.activeSelf){root.gameObject.SetActive(true);stage.enabled=true;displayRoot.style.display=Headless?DisplayStyle.None:DisplayStyle.Flex;}
        if(source!=midi.Prepared)Load();
        // The strip's texture matches its part of the screen; the image sits there in its own panel.
        float panelWidth=displayRoot.resolvedStyle.width,panelHeight=displayRoot.resolvedStyle.height;
        // Headless (the headset shows the texture on its own card), the hidden panel has no size.
        if(!Headless&&(!float.IsFinite(panelWidth)||panelWidth<1))return;
        int w=PixelSize.x>0?PixelSize.x:Mathf.Max(8,Mathf.RoundToInt(Viewport.width*Screen.width)),h=PixelSize.y>0?PixelSize.y:Mathf.Max(8,Mathf.RoundToInt(Viewport.height*Screen.height));
        if(Headless&&displayRoot.style.display!=DisplayStyle.None)displayRoot.style.display=DisplayStyle.None;
        if(hdr==null||Mathf.Abs(hdr.width-w)>w*.1f||Mathf.Abs(hdr.height-h)>h*.1f)Resize(w,h);
        var place=new Rect(Viewport.x*panelWidth,(1-Viewport.y-Viewport.height)*panelHeight,Viewport.width*panelWidth,Viewport.height*panelHeight);
        if(!Headless&&place!=placed){placed=place;image.style.left=place.x;image.style.top=place.y;image.style.width=place.width;image.style.height=place.height;}
        // The camera frames Tall stage units of the strip, looking straight down at the line.
        float aspect=w/(float)h;
        float tan=Mathf.Tan(stage.fieldOfView*.5f*Mathf.Deg2Rad),distance=Tall*.5f/tan;
        stage.transform.localPosition=new Vector3(0,distance,Center);stage.transform.localRotation=Quaternion.LookRotation(Vector3.down,Vector3.forward);
        Span=Mathf.Clamp(Tall*.5f*aspect,1.2f,9);
        double beat=midi.Cycles.BeatAt(midi.ScorePosition),now=midi.ScorePosition;
        var bar=CurrentBar(beat);
        double barLength=bar!=null?bar.End-bar.Start:4,barStart=bar?.Start??0;
        // The groove scrolls at the rim's speed of the drum wheel: one bar per turn.
        double perBeat=2*Math.PI*1.15/Math.Max(1,barLength);PerBeat=perBeat;
        float fade=Mathf.SmoothStep(0,1,visible);
        ChordColor=region!=null&&region.HasRegion?region.RegionColor:Ink;
        // The groove is there only around the words: it fades in a moment before a line
        // begins and out a moment after it ends.
        Presence=Near(now);
        DrawLine(fade*Presence);
        DrawSlashes(beat,perBeat,barStart,barLength,fade*Presence);
        DrawReader(now,perBeat,fade);
    }
    void Resize(int w,int h)
    {
        if(hdr!=null){stage.targetTexture=null;hdr.Release();Destroy(hdr);texture.Release();Destroy(texture);}
        hdr=new RenderTexture(w,h,24,RenderTextureFormat.ARGBHalf){name="Lyric strip HDR"};hdr.Create();stage.targetTexture=hdr;
        texture=new RenderTexture(w,h,0,RenderTextureFormat.ARGBHalf){name="Lyric strip transparent"};texture.Create();image.image=texture;
    }
    void Composite(ScriptableRenderContext context,Camera rendered)
    {
        if(rendered!=stage||hdr==null)return;
        var command=CommandBufferPool.Get("Transparent lyric strip");command.Blit(hdr,texture,composite);
        context.ExecuteCommandBuffer(command);CommandBufferPool.Release(command);
    }
    // How present the words are now: full within 1.2 s of a syllable, gone 2.4 s from any.
    public float Presence {get;private set;}
    float Near(double now)
    {
        if(onsets.Length==0)return 0;
        int lo=0,hi=onsets.Length;while(lo<hi){int mid=(lo+hi)/2;if(onsets[mid]<=now)lo=mid+1;else hi=mid;}
        double ahead=lo<onsets.Length?onsets[lo]-now:double.PositiveInfinity;
        double behind=lo>0?now-Math.Max(onsets[lo-1],midi.Cycles.SecondsAt(syllables[lo-1].End)):double.PositiveInfinity;
        double gap=Math.Min(ahead,behind);
        return Mathf.Clamp01((float)(1-(gap-1.2)/1.2));
    }
    bool HasLyricsLoaded()=>source==midi.Prepared?syllables.Length>0:(midi.Prepared?.Lyrics?.Syllables?.Length??0)>0;
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
        lineGradient.SetKeys(new[]{new GradientColorKey(Ink*.8f,0),new GradientColorKey(Ink*.8f,1)},new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(.25f*fade,.25f),new GradientAlphaKey(.25f*fade,.75f),new GradientAlphaKey(0,1)});
        centerline.colorGradient=lineGradient;
    }
    // The slash of a beat ("\", from the crest down into the well) or an upbeat ("/"), centred at x.
    static void SlashEnds(float x,bool down,float size,out Vector3 a,out Vector3 b)
    {
        float w=size*.29f,h=size*.5f;
        a=new Vector3(x-w,.028f,Center+(down?h:-h));b=new Vector3(x+w,.028f,Center+(down?-h:h));
    }
    // The groove: a slash for every drum hit, scrolling left, "\" on a beat (the downbeat's
    // tallest) and "/" off it, taller for a heavier hit; brightest as it reaches the reticle,
    // where the syllable locks into it.
    void DrawSlashes(double beat,double perBeat,double barStart,double barLength,float fade)
    {
        double reach=Span/perBeat;int used=0;
        int lo=0,hi=hitBeat.Length;while(lo<hi){int mid=(lo+hi)/2;if(hitBeat[mid]<beat-reach)lo=mid+1;else hi=mid;}
        for(int k=lo;k<hitBeat.Length&&hitBeat[k]<=beat+reach;k++)
        {
            double b=hitBeat[k];float x=(float)((b-beat)*perBeat);if(Mathf.Abs(x)>Span)continue;
            double frac=b-Math.Floor(b+1e-6);bool down=frac<.1||frac>.9;bool first=down&&Math.Abs(Math.IEEERemainder(b-barStart,barLength))<.1;
            float weight=.7f+.5f*hitWeight[k];
            while(slashes.Count<=used){var l=Line("Groove slash",.014f,Ink);l.numCapVertices=1;slashes.Add(l);}
            var slash=slashes[used++];
            SlashEnds(x,down,(first?1.15f:down?.95f:.7f)*weight,out var a,out var c);
            pair[0]=a;pair[1]=c;slash.positionCount=2;slash.SetPositions(pair);
            float near=Mathf.Clamp01(1-Mathf.Abs(x)/.5f);
            var color=Color.Lerp(Ink,Color.white,.35f*near)*(1+.6f*near);color.a=fade*(first?.5f:down?.4f:.28f)*Mathf.Clamp01((Span-Mathf.Abs(x))/.6f)*(1+near);
            slash.startColor=slash.endColor=color;slash.widthMultiplier=(first?.018f:down?.014f:.011f)*(1+.6f*near);
        }
        SlashCount=used;
        for(int i=used;i<slashes.Count;i++)slashes[i].positionCount=0;
    }
    // The next drum hit, coming in from the right at the rim's speed: a steep translucent tooth
    // whose cliff reaches the reticle as it is struck, taller for a heavier hit.
    void DrawTooth(double beat,int next,double perBeat,float fade)
    {
        if(next>=hitBeat.Length){tooth.positionCount=0;NextToothBeat=double.NaN;return;}
        float x=(float)((hitBeat[next]-beat)*perBeat);NextToothBeat=hitBeat[next];NextToothX=x;
        if(x>Span){tooth.positionCount=0;return;}
        float h=.3f+.35f*hitWeight[next],w=.22f;
        saw[0]=new Vector3(x,.03f,Center-h*.5f);saw[1]=new Vector3(x,.03f,Center+h*.5f);saw[2]=new Vector3(x+w,.03f,Center-h*.5f);
        tooth.positionCount=3;tooth.SetPositions(saw);
        var c=Color.Lerp(Ink,Color.white,.4f);c.a=fade*.3f*Mathf.Clamp01((Span-x)/.8f);tooth.startColor=tooth.endColor=c;
    }

    // The syllable whose entry is under way or done: the last with its entry begun. A drawn-out
    // syllable comes in more slowly.
    static float Lead(double gap,PreparedPatternSong.Syllable s)=>(float)Math.Clamp(gap*.6,.04,.1)*(s.Teeth>=3?1.6f:1);
    int Current(double now)
    {
        int lo=0,hi=onsets.Length;while(lo<hi){int mid=(lo+hi)/2;if(onsets[mid]<=now+.16)lo=mid+1;else hi=mid;}
        int k=lo-1;
        while(k>=0){double gap=k>0?onsets[k]-onsets[k-1]:1;if(onsets[k]-Lead(gap,syllables[k])<=now)break;k--;}
        return k;
    }
    void Put(Slot slot,PreparedPatternSong.Syllable s)
    {
        if(slot.Syllable==s)return;
        slot.Syllable=s;slot.Box.TextField.characterSpacing=Spacing(s);slot.Box.Text=s.Text;slot.Box.TextField.ForceMeshUpdate(true,true);  // also while the slot is hidden
        // The recognition letter's centre and the text's extent, in the text's own x.
        var info=slot.Box.TextField.textInfo;int count=info.characterCount,at=Mathf.Clamp(Recognition(count),0,Math.Max(0,count-1));
        slot.Orp=count==0?0:(info.characterInfo[at].vertex_BL.position.x+info.characterInfo[at].vertex_TR.position.x)*.5f;
        slot.Left=count==0?0:info.characterInfo[0].vertex_BL.position.x;slot.Right=count==0?0:info.characterInfo[count-1].vertex_TR.position.x;
    }
    // Space between syllable j and the next: none inside a word, a space between words, more between lines.
    float Gap(int j)=>j+1<syllables.Length&&syllables[j].Line!=syllables[j+1].Line?.55f:syllables[j].WordEnd?.2f:.015f;
    // Where a syllable's entry starts, from its place: along its slash. A beat slides down the
    // "\" from the upper left (farther with emphasis), an off-beat up the "/" from the lower
    // left, a syllable between beats in from the right.
    static Vector2 EntryFrom(PreparedPatternSong.Syllable s)
    {
        float reach=.75f+.4f*s.Emphasis;
        return s.Metric>=1?new Vector2(-.5f,.86f)*reach:s.Metric==0?new Vector2(-.5f,-.86f)*reach*.8f:new Vector2(.7f,0);
    }
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
        var s=syllables[k];double gap=k>0?onsets[k]-onsets[k-1]:1;float lead=Lead(gap,s);
        float e=(float)(now-onsets[k]);
        // The entry: eased hard along the slash, locked exactly on the onset, never past it.
        float p=Mathf.Clamp01((e+lead)/lead),ease=1-(1-p)*(1-p)*(1-p);
        ReaderFrom=s.Metric>=1?1:s.Metric==0?-1:0;
        float y=GrooveY(s);ReaderY=y;
        var shift=EntryFrom(s)*(1-ease);ReaderShift=shift;
        // The impact: a beat lands squashed, an off-beat stretched, harder with emphasis; gone in 80 ms.
        float impact=e>=0?Mathf.Exp(-e/.045f):0,hard=.6f+.6f*s.Emphasis;
        var squash=ReaderFrom>0?new Vector2(1+.14f*hard*impact,1-.2f*hard*impact):ReaderFrom<0?new Vector2(1-.08f*hard*impact,1+.16f*hard*impact):new Vector2(1+.1f*hard*impact,1-.06f*hard*impact);
        ReaderSquash=squash;incoming.Box.transform.localScale=new Vector3(squash.x,squash.y,1);
        // Vibrato: a gentle wiggle up and down at its rate.
        Shimmer=0;Wiggle=0;
        if(!s.Spoken&&s.Vibrato>0&&midi.Cycles.BeatAt(now)>=s.VibratoStart&&e>0){Shimmer=Mathf.Clamp(s.Vibrato/.35f,.5f,1.5f);Wiggle=.016f*Shimmer*Mathf.Sin((float)now*Mathf.PI*2*(s.VibratoRate>0?s.VibratoRate:5.5f));}
        Place(incoming,-incoming.Orp*squash.x+shift.x,Center+y+shift.y+Wiggle);
        // In the chord's colour, with a small bloom as it lands.
        float hit=e>=0?Mathf.Exp(-e/.08f):0;
        var color=Color.Lerp(ChordColor*1.15f,Color.white,.45f*hit)*(1+(.6f+.5f*s.Emphasis)*hit);color.a=fade*Mathf.Max(.35f,ease);incoming.Face.SetColor(ShaderUtilities.ID_FaceColor,color);ReaderColor=color;
        ReaderOrpX=incoming.Box.transform.localPosition.x+incoming.Orp*squash.x-shift.x;
        ReaderSpacing=incoming.Box.TextField.characterSpacing;
        float left=-incoming.Orp*squash.x+incoming.Left*squash.x;ReaderLeft=left;
        // The syllable before, still large, gives way to its trail copy as the new one comes in.
        if(k>0&&outgoing.Syllable==syllables[k-1]&&p<1)
        {
            outgoing.Box.transform.localScale=Vector3.one;
            Place(outgoing,-outgoing.Orp-ease*(outgoing.Right-outgoing.Left)*.5f,Center+GrooveY(syllables[k-1]));
            var c=ChordColor;c.a=fade*(1-ease);outgoing.Face.SetColor(ShaderUtilities.ID_FaceColor,c);
        }
        else if(outgoing.Box.gameObject.activeSelf)outgoing.Box.gameObject.SetActive(false);
        DrawTrail(k,left,ease,fade);
        DrawStrike(k,e,fade);
        // The reticle over the recognition letter.
        var mark=Color.white*1.5f;mark.a=fade;
        pair[0]=new Vector3(0,.04f,Center+.4f);pair[1]=new Vector3(0,.04f,Center+.5f);
        tick.positionCount=2;tick.SetPositions(pair);tick.startColor=tick.endColor=mark;
        // A held syllable trails a bar that runs out with it.
        double remaining=s.End-midi.Cycles.BeatAt(now);
        if(e>=0&&s.End-s.Start>=1&&remaining>0)
        {
            float x0=-incoming.Orp*squash.x+incoming.Right*squash.x+.08f;
            pair[0]=new Vector3(x0,.035f,Center+y);pair[1]=new Vector3(x0+(float)(remaining*perBeat),.035f,Center+y);
            hold.positionCount=2;hold.SetPositions(pair);
            var hc=Color.Lerp(ChordColor,Color.white,.3f)*1.4f;hc.a=fade;hold.startColor=hold.endColor=hc;
        }
        else hold.positionCount=0;
    }
    // The syllables before the current one, right to left from its left edge, fading with
    // distance, each keeping its place in the groove. While the new one comes in they slide left
    // from where they sat a syllable ago.
    void DrawTrail(int k,float left,float ease,float fade)
    {
        float cursor=left;int count=0;TrailNearestRight=float.NaN;TrailNearestAlpha=0;
        // How far the trail moves this entry: the new syllable's width left of the reticle, plus its gap.
        float slide=(1-ease)*(Mathf.Max(0,-left)+(k>0?Gap(k-1):0));
        used.Clear();
        for(int i=1;i<=TrailLength&&k-i>=0;i++)
        {
            int j=k-i;var slot=trail[j%TrailLength];Put(slot,syllables[j]);
            cursor-=Gap(j);
            float right=cursor+slide;float x=right-slot.Right;
            if(x+slot.Left<-Span)break;
            float y=GrooveY(syllables[j]);
            Place(slot,x,Center+y);
            // Fading with distance: dimmer as well as more transparent, since the face glows.
            float alpha=fade*.8f*Mathf.Pow(.6f,i-1)*Mathf.Clamp01((x+slot.Left+Span)/.8f);
            var c=Color.Lerp(ChordColor,Ink,.35f)*(.25f+.6f*alpha);c.a=alpha;slot.Face.SetColor(ShaderUtilities.ID_FaceColor,c);
            if(i==1){TrailNearestRight=right;TrailNearestAlpha=alpha;TrailNearestY=y;}
            used.Add(j%TrailLength);
            cursor-=slot.Right-slot.Left;count++;
        }
        TrailCount=count;
        for(int t=0;t<TrailLength;t++)if(!used.Contains(t)&&trail[t].Box.gameObject.activeSelf)trail[t].Box.gameObject.SetActive(false);
    }
    readonly List<int> used=new(TrailLength);
    // The strike: the slash under the syllable flaring as it locks in, down (\) on a beat, up
    // (/) off it, shallow between beats. It slices in within 25 ms, flares and widens, then
    // blooms out in about a quarter of a second while its tail chases its head off the end; the
    // drum hit landing with it makes it brighter and wider.
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
        float rise=ReaderFrom>0?-Slash:ReaderFrom<0?Slash:Slash*.25f;
        var from=new Vector3(-Slash*.6f,.045f,Center-rise);var to=new Vector3(Slash*.6f,.045f,Center+rise);
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
        RenderPipelineManager.endCameraRendering-=Composite;
        if(root!=null)Destroy(root.gameObject);root=null;slashes.Clear();
        if(hdr!=null){hdr.Release();Destroy(hdr);hdr=null;}if(texture!=null){texture.Release();Destroy(texture);texture=null;}
        if(display!=null)Destroy(display);if(panelSettings!=null)Destroy(panelSettings);
        if(glow!=null)Destroy(glow);if(composite!=null)Destroy(composite);
    }
}
