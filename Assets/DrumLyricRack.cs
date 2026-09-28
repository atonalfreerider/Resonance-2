using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

// Lyric mode's reader, on the drum wheel: one lyric line across the top of the strip, joined to
// the disc's comb at twelve o'clock (where the dimples are struck) by a stem that flashes with
// every drum hit. The line is a groove: a faint wave scrolling left at the rim's speed with a
// well at every beat and a crest between. The syllable being heard is always centred, its
// fastest-to-read letter (the optimal recognition point) on the stem, bold and in the colour of
// the chord of the moment, and it fits the groove where it lands:
//  • on a beat it hammers straight down into the well and sits low, squashed by the impact;
//  • off the beat it is kicked up from below onto the crest and sits high, stretched;
//  • between beats it skids in sideways along the line;
//  • a drawn-out syllable is set wide (letter spacing grows with its length), comes in more
//    slowly, and trails a bar that runs out with it;
//  • under vibrato its letters shimmer at the vibrato's rate;
//  • emphasis sets how far and hard it comes in.
// Every entry lands exactly on the onset, never past it. As it lands a slanted strike slices
// across it, down (\) into the well and up (/) over the crest, a white bloom that fades fast,
// brighter for the drum hit landing with it. The syllables before build to the left as a fading
// trail that keeps their groove positions, so the meter reads back as a pattern. The next drum
// hit comes in from the right as a translucent steep tooth. Text meshes are rebuilt only when a
// slot's syllable changes (and per frame for the one syllable under vibrato).
[DefaultExecutionOrder(80)]
public sealed class DrumLyricRack : MonoBehaviour
{
    Main main;MidiPlayer midi;DrumPatternDeck deck;DominantChordOutline region;PreparedPatternSong source;
    Transform root;LineRenderer centerline,groove,hold,stem,tick,strike,tooth;Material glow;
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
    // Deck-local geometry (the disc's rim is 1.15): the lyric line above it, the groove's depth.
    public const float Edge=1.15f,Center=1.6f,Well=.11f,Crest=.1f;
    const float ReaderSize=7.2f,TrailSize=4.3f,Slash=.55f;
    // The lyric camera frames Tall deck units around Middle: the whole disc and the line with its strikes.
    public const float Tall=4.1f,Middle=.62f;
    public float Span {get;private set;}=3;
    public double PerBeat {get;private set;}
    // For validation: the syllable on the line, its entry (+1 down into a well, -1 up onto a
    // crest, 0 sideways), how far it still is from its place, its strike, its recognition letter's
    // x, its colour, its letter spacing and squash, the trail, the stem's flash and the tooth.
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
    public int TrailCount {get;private set;}
    public float TrailNearestRight {get;private set;}
    public float TrailNearestAlpha {get;private set;}
    public float TrailNearestY {get;private set;}
    public float ReaderLeft {get;private set;}
    public float Pulse {get;private set;}
    public double NextToothBeat {get;private set;}=double.NaN;
    public float NextToothX {get;private set;}
    public bool Holding=>hold!=null&&hold.positionCount==2;
    static readonly Color Ink=new(.5f,.74f,.72f);
    Gradient lineGradient;float lineFade=-1,lineSpan=-1;
    readonly Vector3[] pair=new Vector3[2],saw=new Vector3[3];Vector3[] wave=Array.Empty<Vector3>();

    void OnEnable()
    {
        if(!Application.isPlaying)return;
        main=GetComponent<Main>();midi=GetComponent<MidiPlayer>();deck=GetComponent<DrumPatternDeck>();region=GetComponent<DominantChordOutline>();
        // Not a child of the scene root: the views' opacity pass would override the glow.
        root=new GameObject("Drum lyric reader · lyric line on the comb").transform;
        glow=new Material(Resources.Load<Shader>("HarmonicGlow"));glow.SetColor("_BaseColor",Color.white*2);glow.renderQueue=3102;
        centerline=Line("Lyric line",.006f,Ink);groove=Line("Beat groove",.012f,Ink);hold=Line("Held syllable",.03f,Ink);
        stem=Line("Stem from the comb",.016f,Ink);tick=Line("Reader reticle",.014f,Color.white);
        strike=Line("Strike",.04f,Color.white);tooth=Line("Next drum hit",.02f,Ink);tooth.numCapVertices=0;tooth.numCornerVertices=0;
        for(int i=0;i<2;i++)reader[i]=NewSlot(ReaderSize);
        for(int i=0;i<TrailLength;i++)trail[i]=NewSlot(TrailSize);
        root.gameObject.SetActive(false);
    }
    LineRenderer Line(string name,float width,Color color)
    {
        var go=new GameObject(name);go.transform.SetParent(root,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=glow;l.useWorldSpace=false;l.widthMultiplier=width;
        l.numCapVertices=2;l.startColor=l.endColor=color;l.positionCount=0;return l;
    }
    Slot NewSlot(float size)
    {
        var box=TextBox.Create("",TextAlignmentOptions.Left);box.transform.SetParent(root,false);box.Size=size;
        // Lying flat on the deck, readable from above with twelve o'clock up.
        box.transform.localRotation=Quaternion.Euler(90,0,0);box.TextField.fontMaterial.renderQueue=3103;
        box.TextField.textWrappingMode=TextWrappingModes.NoWrap;box.TextField.fontStyle=FontStyles.Bold|FontStyles.UpperCase;
        box.gameObject.SetActive(false);return new Slot{Box=box,Face=box.TextField.fontMaterial};
    }
    // A drum's weight: how bright a flash and strike its hit makes.
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
        // Rim speed (one revolution per bar): how fast the groove and the next hit come in.
        double perBeat=2*Math.PI*Edge/Math.Max(1,barLength);PerBeat=perBeat;
        float fade=Mathf.SmoothStep(0,1,visible);
        var view=Camera.main;float aspect=view!=null?view.aspect:1.6f;
        Span=Mathf.Clamp(Tall*.5f*aspect-.35f,2.2f,9);
        ChordColor=region!=null&&region.HasRegion?region.RegionColor:Ink;
        DrawLine(fade);
        DrawGroove(beat,perBeat,fade);
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
        lineGradient.SetKeys(new[]{new GradientColorKey(Ink*.8f,0),new GradientColorKey(Ink*.8f,1)},new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(.3f*fade,.25f),new GradientAlphaKey(.3f*fade,.75f),new GradientAlphaKey(0,1)});
        centerline.colorGradient=lineGradient;
        var faint=new Gradient();
        faint.SetKeys(new[]{new GradientColorKey(Ink,0),new GradientColorKey(Ink,1)},new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(.22f*fade,.2f),new GradientAlphaKey(.22f*fade,.8f),new GradientAlphaKey(0,1)});
        groove.colorGradient=faint;
    }
    // The groove: a wave scrolling left at the rim's speed, a well at every beat, a crest at every
    // upbeat, so each syllable is seen to land where its beat passes the stem.
    void DrawGroove(double beat,double perBeat,float fade)
    {
        int count=Mathf.Clamp(Mathf.RoundToInt(Span*2/(float)perBeat*16)+1,17,257);
        if(wave.Length!=count)wave=new Vector3[count];
        for(int i=0;i<count;i++)
        {
            float x=-Span+i*(2*Span/(count-1));double b=beat+x/perBeat;
            float phase=(float)(b-Math.Floor(b));
            // A well (-Well) at the beat, a crest (+Crest) at the half: a cosine skewed so the drop into the well is steep.
            float t=phase<.5f?phase/.5f:(1-phase)/.5f;
            float y=Mathf.Lerp(-Well,Crest,Mathf.SmoothStep(0,1,t));
            wave[i]=new Vector3(x,.025f,Center+y);
        }
        groove.positionCount=count;groove.SetPositions(wave);
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
    // Where a syllable's entry starts, from its place: a beat from straight above (farther with
    // emphasis), an off-beat from straight below, a syllable between beats from the right.
    static Vector2 EntryFrom(PreparedPatternSong.Syllable s)=>s.Metric>=1?new Vector2(0,.5f+.4f*s.Emphasis):s.Metric==0?new Vector2(0,-(.45f+.35f*s.Emphasis)):new Vector2(.7f,0);
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
        // The entry: eased hard into the groove, done exactly on the onset, never past it.
        float p=Mathf.Clamp01((e+lead)/lead),ease=1-(1-p)*(1-p)*(1-p);
        ReaderFrom=s.Metric>=1?1:s.Metric==0?-1:0;
        float y=GrooveY(s);ReaderY=y;
        var shift=EntryFrom(s)*(1-ease);ReaderShift=shift;
        // The impact: a beat lands squashed, an off-beat stretched, harder with emphasis; gone in 80 ms.
        float impact=e>=0?Mathf.Exp(-e/.045f):0,hard=.6f+.6f*s.Emphasis;
        var squash=ReaderFrom>0?new Vector2(1+.14f*hard*impact,1-.2f*hard*impact):ReaderFrom<0?new Vector2(1-.08f*hard*impact,1+.16f*hard*impact):new Vector2(1+.1f*hard*impact,1-.06f*hard*impact);
        ReaderSquash=squash;incoming.Box.transform.localScale=new Vector3(squash.x,squash.y,1);
        // Vibrato: the letters shimmer at its rate; otherwise the mesh stays as set.
        Shimmer=0;
        if(!s.Spoken&&s.Vibrato>0&&midi.Cycles.BeatAt(now)>=s.VibratoStart&&e>0){Shimmer=Mathf.Clamp(s.Vibrato/.35f,.5f,1.5f);ShimmerLetters(incoming,(float)now,s.VibratoRate>0?s.VibratoRate:5.5f,Shimmer);}
        else if(shimmered==incoming){incoming.Box.TextField.ForceMeshUpdate(true,true);shimmered=null;}
        Place(incoming,-incoming.Orp*squash.x+shift.x,Center+y+shift.y);
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
        DrawStrike(k,e,y,fade);
        // The reticle over the recognition letter.
        var mark=Color.white*1.5f;mark.a=fade;
        pair[0]=new Vector3(0,.04f,Center+.36f);pair[1]=new Vector3(0,.04f,Center+.46f);
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
    // Vibrato: each letter of the syllable bobs at the vibrato's rate, a wave running along the
    // word, by moving the mesh's vertices from their set positions (the mesh is regenerated first).
    Slot shimmered;
    void ShimmerLetters(Slot slot,float time,float rate,float depth)
    {
        var text=slot.Box.TextField;text.ForceMeshUpdate(true,true);shimmered=slot;
        var info=text.textInfo;float amplitude=.09f*depth*text.fontSize;
        for(int i=0;i<info.characterCount;i++)
        {
            var ch=info.characterInfo[i];if(!ch.isVisible)continue;
            var verts=info.meshInfo[ch.materialReferenceIndex].vertices;
            var offset=new Vector3(0,amplitude*Mathf.Sin(time*Mathf.PI*2*rate-i*1.1f),0);
            for(int v=0;v<4;v++)verts[ch.vertexIndex+v]+=offset;
        }
        text.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);
    }
    // The syllables before the current one, right to left from its left edge, fading with
    // distance, each keeping its place in the groove. While the new one comes in they slide left
    // from where they sat a syllable ago.
    void DrawTrail(int k,float left,float ease,float fade)
    {
        float cursor=left;int count=0;TrailNearestRight=float.NaN;TrailNearestAlpha=0;
        // How far the trail moves this entry: the new syllable's width left of the stem, plus its gap.
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
    // The strike: a slash across the syllable as it lands, down (\) into the well, up (/) onto
    // the crest, shallow between beats. It slices in within 25 ms, flares and widens, then blooms
    // out in about a quarter of a second while its tail chases its head off the end; the drum hit
    // landing with it makes it brighter and wider.
    void DrawStrike(int k,float e,float y,float fade)
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
        var from=new Vector3(-Slash*1.3f,.045f,Center+y-rise);var to=new Vector3(Slash*1.3f,.045f,Center+y+rise);
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
