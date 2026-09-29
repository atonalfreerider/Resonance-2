using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

// The tutorial: a narrated tour of the umbilic torus, from the twelve tones to the four
// triangles that spin into a three-sided band, the key at the top, the colours, position,
// key changes and the circle of fifths. Its script and narration live in
// StreamingAssets/Tutorial (script.json, one WAV per step, a manifest with durations); the
// torus, the camera and a diagram overlay follow each step. Started from the song list.
public sealed class TutorialDirector : MonoBehaviour
{
    [Serializable] public sealed class Step {public string id="",title="",action="",diagram="",text="";}
    [Serializable] sealed class Script {public int version;public Step[] steps=Array.Empty<Step>();}
    // starts/ends: each displayed word's time in the clip (Tools/SongLibrary/tutorial_timing.py).
    [Serializable] sealed class Clip {public string id="",file="";public double seconds;public float[] starts=Array.Empty<float>(),ends=Array.Empty<float>();}
    [Serializable] sealed class Manifest {public int version;public Clip[] clips=Array.Empty<Clip>();}
    public static string Folder=>Path.Combine(Application.streamingAssetsPath,"Tutorial");
    public bool Playing {get;private set;}
    public int StepIndex {get;private set;}=-1;
    public string StepId=>StepIndex>=0&&StepIndex<steps.Length?steps[StepIndex].id:"";
    public string StepTitle=>StepIndex>=0&&StepIndex<steps.Length?steps[StepIndex].title:"";
    public string Diagram {get;private set;}="";
    public float StepTime {get;private set;}
    // The sweep of the moebius step: how far round the ring the moving triangle is (0..1) and
    // how many of the four triangles it has snapped into place.
    public float Sweep {get;private set;}
    public int Snapped {get;private set;}
    LineRenderer sweepTriangle,travelArrow,spinArrow,spinArrowBack;readonly LineRenderer[] snapped=new LineRenderer[4],trails=new LineRenderer[3];
    const int TrailPoints=120;readonly Vector3[] trail=new Vector3[TrailPoints];Material sweepGlow;bool sweeping;float sweepStart,sweepLength;
    static readonly Color[] TriadColors={new(.36f,.62f,1),new(1,.42f,.42f),new(.42f,.9f,.5f),new(.85f,.6f,1)};
    Main main;MidiPlayer midi;VisualizationViews views;SongLibraryPanel library;CameraControl orbit;AudioSource voice;
    VisualElement root,caption,sentence,buttonRow;Label title,progress;Button skip,back,next;Diagrams diagrams;int jump=-1;
    Step[] steps=Array.Empty<Step>();readonly Dictionary<string,AudioClip> clips=new();readonly Dictionary<string,double> seconds=new();
    readonly Dictionary<string,float[]> wordStarts=new();
    // The caption shows the sentence being spoken, three times the old size, and lights the word
    // being said: a bloom that swells on its onset and settles.
    const float SentenceSize=42,TitleSize=32,PortraitZoom=1.6f;
    string[] words=Array.Empty<string>();int[] sentenceOf=Array.Empty<int>();float[] starts=Array.Empty<float>();
    readonly List<Label> wordLabels=new();readonly List<VisualElement> wordGlows=new();static Texture2D glowTexture;
    // A soft round glow (gaussian alpha), made once.
    static Texture2D Glow()
    {
        if(glowTexture!=null)return glowTexture;
        const int n=64;glowTexture=new Texture2D(n,n,TextureFormat.RGBA32,false){wrapMode=TextureWrapMode.Clamp,name="Tutorial word glow"};
        var px=new Color32[n*n];
        for(int y=0;y<n;y++)for(int x=0;x<n;x++){float dx=(x+.5f)/n*2-1,dy=(y+.5f)/n*2-1,a=Mathf.Exp(-(dx*dx+dy*dy)*3.2f)*Mathf.Clamp01(1-Mathf.Sqrt(dx*dx+dy*dy));px[y*n+x]=new Color32(255,255,255,(byte)(a*255));}
        glowTexture.SetPixels32(px);glowTexture.Apply();return glowTexture;
    }int shownSentence=-1,firstWord,litWord=-1;float litAt;bool blooming;
    static readonly Color Upcoming=new(.6f,.67f,.76f),Spoken=new(.9f,.93f,.97f),Lit=new(1,.9f,.55f);
    float placedWidth=-1,placedHeight=-1;bool placedPortrait,placedRecording;
    Coroutine run;int keyBefore;bool minorBefore;VisualizationViews.View viewBefore;

    public void Bind(VisualElement ui)
    {
        root=ui;main=GetComponent<Main>();midi=GetComponent<MidiPlayer>();views=GetComponent<VisualizationViews>();library=GetComponent<SongLibraryPanel>();
        orbit=Camera.main!=null?Camera.main.GetComponent<CameraControl>():null;
        var go=new GameObject("Tutorial narration");go.transform.SetParent(transform,false);voice=go.AddComponent<AudioSource>();voice.playOnAwake=false;voice.spatialBlend=0;
        sweepGlow=new Material(Resources.Load<Shader>("HarmonicGlowOverlay"));sweepGlow.SetColor("_BaseColor",Color.white*2);sweepGlow.renderQueue=3107;
        sweepTriangle=Stroke("Tutorial · sweeping triangle",.035f);travelArrow=Stroke("Tutorial · direction of travel",.03f);spinArrow=Stroke("Tutorial · direction of rotation",.025f);
        for(int i=0;i<4;i++)snapped[i]=Stroke("Tutorial · triangle "+i,.022f);
        for(int i=0;i<3;i++)trails[i]=Stroke("Tutorial · traced umbilic "+i,.034f);
        spinArrowBack=Stroke("Tutorial · direction of rotation (opposite side)",.025f);
        diagrams=new Diagrams(this,main){name="tutorial-diagram",pickingMode=PickingMode.Ignore};diagrams.style.position=Position.Absolute;diagrams.style.display=DisplayStyle.None;root.Add(diagrams);
        caption=new VisualElement{name="tutorial-caption",pickingMode=PickingMode.Ignore};caption.style.position=Position.Absolute;caption.style.display=DisplayStyle.None;
        caption.style.backgroundColor=new Color(.04f,.06f,.1f,.5f);caption.style.paddingLeft=caption.style.paddingRight=22;caption.style.paddingTop=14;caption.style.paddingBottom=14;
        caption.style.borderTopLeftRadius=caption.style.borderTopRightRadius=caption.style.borderBottomLeftRadius=caption.style.borderBottomRightRadius=12;
        title=new Label("");title.style.fontSize=TitleSize;title.style.unityFontStyleAndWeight=FontStyle.Bold;title.style.color=new Color(.62f,.8f,1);title.style.marginBottom=6;caption.Add(title);
        sentence=new VisualElement{pickingMode=PickingMode.Ignore};sentence.style.flexDirection=FlexDirection.Row;sentence.style.flexWrap=Wrap.Wrap;caption.Add(sentence);
        progress=new Label(""){name="tutorial-progress"};progress.style.fontSize=18;progress.style.color=new Color(.52f,.66f,.8f);progress.style.marginTop=10;caption.Add(progress);
        root.Add(caption);caption.RegisterCallback<GeometryChangedEvent>(FitCaption);
        skip=new Button(Stop){text="Skip tutorial",name="tutorial-skip"};
        // Step through: back to the step before, on to the next.
        var row=buttonRow=new VisualElement{pickingMode=PickingMode.Ignore};row.style.flexDirection=FlexDirection.Row;row.style.marginTop=8;row.style.alignItems=Align.Center;caption.Add(row);
        back=new Button(()=>Jump(StepIndex-1)){text="‹ Back",name="tutorial-back"};next=new Button(()=>Jump(StepIndex+1)){text="Next ›",name="tutorial-next"};
        foreach(var b in new[]{back,next,skip}){b.style.fontSize=22;b.style.paddingLeft=b.style.paddingRight=16;b.style.paddingTop=b.style.paddingBottom=6;}
        row.Add(back);row.Add(next);var gap=new VisualElement{pickingMode=PickingMode.Ignore};gap.style.flexGrow=1;row.Add(gap);row.Add(skip);
        try
        {
            var script=JsonUtility.FromJson<Script>(File.ReadAllText(Path.Combine(Folder,"script.json")));steps=script?.steps??Array.Empty<Step>();
            string manifestPath=Path.Combine(Folder,"manifest.json");
            if(File.Exists(manifestPath))foreach(var c in JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath)).clips){seconds[c.id]=c.seconds;if(c.starts!=null&&c.starts.Length>0)wordStarts[c.id]=c.starts;}
        }
        catch(Exception e){Debug.LogWarning("Tutorial script: "+e.Message);}
    }
    LineRenderer Stroke(string name,float width)
    {
        var go=new GameObject(name);go.transform.SetParent(transform,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=sweepGlow;l.useWorldSpace=true;l.widthMultiplier=width;l.numCapVertices=3;l.numCornerVertices=2;l.positionCount=0;return l;
    }
    public bool Available=>steps.Length>0;
    public void Play(){if(run!=null)StopCoroutine(run);jump=-1;run=StartCoroutine(Run());}
    // Skip to a step (the running step's wait ends at once; the narration is cut off).
    public void Jump(int index){if(!Playing)return;ExplorerInputFocus.ClaimUI();jump=Mathf.Clamp(index,0,Math.Max(0,steps.Length-1));}
    public void Stop(){if(run!=null){StopCoroutine(run);run=null;}Finish();}

    IEnumerator Run()
    {
        // The views and the camera are bound after this component: found when the tour starts.
        views??=GetComponent<VisualizationViews>();library??=GetComponent<SongLibraryPanel>();orbit??=Camera.main!=null?Camera.main.GetComponent<CameraControl>():null;
        // A story playing when the tutorial starts stops, pictures and all.
        GetComponent<SongDirector>()?.SetDirecting(false);
        Playing=true;keyBefore=main.currentKey;minorBefore=main.MinorMode;viewBefore=views.Current;
        midi.Pause();library.Hide();views.TorusZoom=Portrait?PortraitZoom:1;placedWidth=-1;Place();views.SetView(VisualizationViews.View.Torus);views.ReframeTorus();views.SetPanelHidden(true);
        caption.style.display=DisplayStyle.Flex;diagrams.style.display=DisplayStyle.Flex;
        yield return Load();
        for(int i=0;i<steps.Length;i++)
        {
            var step=steps[i];StepIndex=i;StepTime=0;Diagram=step.diagram;jump=-1;
            back.SetEnabled(i>0);next.SetEnabled(i<steps.Length-1);
            // The band is seen through while a triangle sweeps round inside it.
            views.TorusOpacityCap=step.action=="spin"?.22f:1;
            if(step.action!="spin"){sweeping=false;Snapped=0;foreach(var l in snapped)l.positionCount=0;}
            title.text=step.title;progress.text=$"{i+1} / {steps.Length}";
            double length=clips.TryGetValue(step.id,out var clip)?clip.length:seconds.TryGetValue(step.id,out var s)?s:Mathf.Max(5,step.text.Length/16f);
            PrepareWords(step,(float)length);
            if(clip!=null){voice.clip=clip;voice.Play();}
            var action=StartCoroutine(Act(step.action,(float)length));
            for(float t=0;t<length+.6f&&jump<0;t+=Time.unscaledDeltaTime){StepTime=t;if(orbit!=null&&orbit.enabled&&step.action!="keychange")orbit.Turn(-.09f*Time.unscaledDeltaTime);yield return null;}
            if(action!=null)StopCoroutine(action);
            if(jump>=0){voice.Stop();main.Silence();i=jump-1;}
        }
        run=null;Finish();
    }
    IEnumerator Load()
    {
        foreach(var step in steps)
        {
            if(clips.ContainsKey(step.id))continue;
            string file=Directory.Exists(Folder)?Directory.GetFiles(Folder,"*-"+step.id+".wav").FirstOrDefault():null;
            if(file==null)continue;
            using var request=UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri,AudioType.WAV);
            yield return request.SendWebRequest();
            if(request.result==UnityWebRequest.Result.Success)clips[step.id]=DownloadHandlerAudioClip.GetContent(request);
        }
    }
    // What the torus does under each step.
    float elapsed=>StepTime;
    IEnumerator Act(string action,float length)
    {
        switch(action)
        {
            case "labels":
                // Lit as the narration names them: the key, its fourth, its fifth, then the minor
                // third, the Neapolitan and the second, then the key in every octave.
                foreach(var (from,to,degrees,octaves) in new[]{(0f,.36f,new[]{0},false),(.36f,.46f,new[]{5},false),(.46f,.56f,new[]{7},false),(.6f,.67f,new[]{3},false),(.67f,.74f,new[]{1},false),(.74f,.82f,new[]{2},false),(.86f,1f,new[]{0},true)})
                {
                    yield return Wait(Mathf.Max(0,from*length-elapsed));Light(degrees,octaves);yield return Wait(Mathf.Max(0,(to-from)*length));main.Silence();
                }
                break;
            case "chords":
                // I, IV, V, I: the three colours the rest are blended from.
                foreach(var chord in new[]{new[]{0,4,7},new[]{5,9,0},new[]{7,11,2},new[]{0,4,7}}){Light(chord);yield return Wait(length/4.4f);}
                main.Silence();break;
            case "spin":
                // One triangle carried once round the ring, tracing the edge: a third of the
                // parameter is one revolution, and every twelfth it lands on one of the four.
                sweeping=true;sweepStart=main.EdgeParameter(main.currentKey);sweepLength=Mathf.Max(6,length*.82f);
                for(int i=0;i<4;i++)snapped[i].positionCount=0;Snapped=0;
                for(float t=0;t<length;)
                {
                    // One or two random tones flash in their colours, now here, now there.
                    var sparks=new List<Tuple<int,float>>();int count=UnityEngine.Random.value<.35f?2:1;
                    for(int n=0;n<count;n++)sparks.Add(Tuple.Create(UnityEngine.Random.Range(0,12)+Main.Tones*UnityEngine.Random.Range(4,6),UnityEngine.Random.Range(.55f,.95f)));
                    main.SetNotes(sparks,false);foreach(var spark in sparks)main.StrikeNote(spark.Item1,1);
                    float hold=UnityEngine.Random.Range(.28f,.7f);yield return Wait(hold);t+=hold;
                }
                main.Silence();sweeping=false;break;
            case "keychange":
                yield return Wait(length*.3f);
                main.ChangeKey(main.currentKey+7,2.4f);yield return Wait(length*.45f);
                main.ChangeKey(main.currentKey-7,2.4f);break;
            case "finish":
                yield return Wait(length*.9f);break;
        }
    }
    static IEnumerator Wait(float s){for(float t=0;t<s;t+=Time.unscaledDeltaTime)yield return null;}
    void Light(int[] degrees,bool allOctaves=false)
    {
        var list=new List<Tuple<int,float>>();
        foreach(int d in degrees){int pc=HarmonyModel.Mod(main.currentKey+d);if(allOctaves)for(int j=0;j<Main.Octaves;j++)list.Add(Tuple.Create(pc+Main.Tones*j,.7f));else list.Add(Tuple.Create(pc+Main.Tones*4,.8f));}
        main.SetNotes(list,true);foreach(var note in list)main.StrikeNote(note.Item1,1);
    }
    void Finish()
    {
        if(!Playing)return;Playing=false;StepIndex=-1;Diagram="";sweeping=false;Sweep=0;Snapped=0;
        sweepTriangle.positionCount=travelArrow.positionCount=spinArrow.positionCount=0;foreach(var l in snapped)l.positionCount=0;
        voice.Stop();main.Silence();
        if(main.currentKey!=keyBefore){main.MinorMode=minorBefore;main.ChangeKey(keyBefore,.7f);}
        caption.style.display=DisplayStyle.None;diagrams.style.display=DisplayStyle.None;views.TorusOpacityCap=1;
        views.SceneTopInset=views.SceneBottomInset=0;placedWidth=-1;
        if(views.TorusZoom!=1){views.TorusZoom=1;views.ReframeTorus();}
        foreach(var l in trails)l.positionCount=0;spinArrowBack.positionCount=0;
        views.SetView(midi.Loaded?viewBefore:VisualizationViews.View.Overview);library.Show();
    }
    // The step's words, each with its time: aligned timings when the manifest has them, otherwise
    // an even share of the clip by letters. Sentences end at . ! or ?.
    void PrepareWords(Step step,float length)
    {
        words=step.text.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
        sentenceOf=new int[words.Length];int n=0;
        for(int k=0;k<words.Length;k++){sentenceOf[k]=n;if(words[k].EndsWith(".")||words[k].EndsWith("!")||words[k].EndsWith("?"))n++;}
        if(wordStarts.TryGetValue(step.id,out var aligned)&&aligned.Length==words.Length)starts=aligned;
        else{starts=new float[words.Length];float letters=words.Sum(w=>w.Length+1f),at=0;for(int k=0;k<words.Length;k++){starts[k]=at/letters*length*.95f;at+=words[k].Length+1;}}
        shownSentence=-1;litWord=-1;
    }
    void ShowSentence(int index)
    {
        shownSentence=index;sentence.Clear();wordLabels.Clear();wordGlows.Clear();
        firstWord=Array.IndexOf(sentenceOf,index);if(firstWord<0)return;
        sentenceSize=FitSentenceSize(index);
        for(int k=firstWord;k<words.Length&&sentenceOf[k]==index;k++)
        {
            var l=new Label(words[k]){pickingMode=PickingMode.Ignore};l.style.fontSize=sentenceSize;l.style.color=Upcoming;
            l.style.marginRight=sentenceSize*.36f;l.style.marginTop=l.style.marginBottom=0;l.style.paddingLeft=l.style.paddingRight=0;
            l.style.transformOrigin=new TransformOrigin(Length.Percent(50),Length.Percent(60));
            var box=new VisualElement{pickingMode=PickingMode.Ignore};
            var glow=new VisualElement{pickingMode=PickingMode.Ignore};glow.style.position=Position.Absolute;
            glow.style.left=glow.style.right=Length.Percent(-45);glow.style.top=glow.style.bottom=Length.Percent(-70);
            glow.style.backgroundImage=new StyleBackground(Glow());glow.style.unityBackgroundImageTintColor=new Color(1,.72f,.25f);glow.style.opacity=0;
            box.Add(glow);box.Add(l);sentence.Add(box);wordLabels.Add(l);wordGlows.Add(glow);
        }
    }
    // Recording a vertical video, the caption hangs from CaptionTop with nothing below it, so a long
    // sentence at the full size (the credit's, the circle of fifths') ran off the bottom of the
    // frame. There each sentence takes the largest size, down to MinSentenceSize, whose words,
    // wrapped by a generous estimate, fit above the bottom margin; should the laid-out caption still
    // reach past the frame's edge, FitCaption steps the size down. Landscape and interactive
    // portrait keep SentenceSize.
    const float MinSentenceSize=24,CaptionBottomMargin=24,CaptionLineHeight=1.5f;
    float sentenceSize=SentenceSize;
    bool RecordingVertical=>RecordingMode.Active&&Portrait;
    // The lines `items` take wrapped into `width` at `size`: `perLetter` em a letter, then `gap` em
    // and `extra` px after each (the real captions wrap a little tighter than this).
    static int WrappedLines(IEnumerable<string> items,float size,float perLetter,float gap,float extra,float width)
    {
        int lines=0;float x=0;
        foreach(var item in items){float w=item.Length*perLetter*size+gap*size+extra;if(lines==0||(x>0&&x+w>width)){lines++;x=0;}x+=w;}
        return Math.Max(lines,1);
    }
    float FitSentenceSize(int index)
    {
        if(!RecordingVertical)return SentenceSize;
        float width=root.resolvedStyle.width,height=root.resolvedStyle.height;if(!float.IsFinite(width)||width<1||!float.IsFinite(height)||height<1)return SentenceSize;
        // Place() gives the caption width-32 and it pads 22 a side and 14 above and below; the
        // title's line, its 6 below and the theme's spacing come before the sentence.
        float inner=width-32-44;
        float titleHeight=WrappedLines((title.text??"").Split((char[])null,StringSplitOptions.RemoveEmptyEntries),TitleSize,.62f,.28f,0,inner)*TitleSize*CaptionLineHeight+6+12;
        float room=height*(1-RecordingMode.CaptionTop)-28-titleHeight-CaptionBottomMargin;
        var sentenceWords=new List<string>();for(int k=firstWord;k<words.Length&&sentenceOf[k]==index;k++)sentenceWords.Add(words[k]);
        float size=SentenceSize;
        while(size>MinSentenceSize&&WrappedLines(sentenceWords,size,.58f,.36f,3,inner)*size*CaptionLineHeight>room)size--;
        return size;
    }
    // The safety net: once laid out, a caption still reaching into the bottom margin shrinks its
    // words a step at a time (each step lays it out again) until it clears or hits MinSentenceSize.
    void FitCaption(GeometryChangedEvent e)
    {
        if(!Playing||!RecordingVertical||wordLabels.Count==0||sentenceSize<=MinSentenceSize)return;
        if(caption.worldBound.yMax<=root.worldBound.yMax-CaptionBottomMargin*.5f)return;
        sentenceSize=Mathf.Max(MinSentenceSize,sentenceSize-2);
        foreach(var l in wordLabels){l.style.fontSize=sentenceSize;l.style.marginRight=sentenceSize*.36f;}
    }
    // Only the words whose state changed are restyled; the bloom touches the lit word alone.
    void LightWords()
    {
        if(words.Length==0)return;
        float t=voice.isPlaying?voice.time:StepTime;
        int k=0;while(k+1<words.Length&&starts[k+1]<=t)k++;
        if(sentenceOf[k]!=shownSentence){ShowSentence(sentenceOf[k]);litWord=-1;}
        if(k!=litWord)
        {
            for(int i=0;i<wordLabels.Count;i++)
            {
                var l=wordLabels[i];int w=firstWord+i;l.style.color=w<k?Spoken:w==k?Lit:Upcoming;
                if(w==litWord){l.style.scale=new Scale(Vector3.one);l.style.textShadow=new TextShadow{offset=Vector2.zero,blurRadius=0,color=Color.clear};wordGlows[i].style.opacity=0;}
            }
            litWord=k;litAt=Time.unscaledTime;blooming=true;
        }
        int index=litWord-firstWord;if(!blooming||index<0||index>=wordLabels.Count)return;
        float e=Mathf.Clamp01((Time.unscaledTime-litAt)/.35f),swell=(1-e)*(1-e);
        var lit=wordLabels[index];
        lit.style.scale=new Scale(Vector3.one*(Main.ReducedMotion?1.06f:1.06f+.16f*swell));
        lit.style.textShadow=new TextShadow{offset=Vector2.zero,blurRadius=12+16*swell,color=new Color(1,.8f,.35f,.7f+.3f*swell)};
        // The bloom behind the word: a bright flash on the onset settling to a steady glow.
        var bloom=wordGlows[index];bloom.style.opacity=.55f+.45f*swell;bloom.style.scale=new Scale(Vector3.one*(1+.35f*swell));
        if(e>=1)blooming=false;
    }
    bool Portrait=>root!=null&&(views.Vertical||root.resolvedStyle.height>root.resolvedStyle.width*1.05f);
    void LateUpdate()
    {
        if(!Playing||root==null)return;
        DrawSweep();LightWords();
        diagrams.MarkDirtyRepaint();
        Place();
        // Between a step with a diagram and one without, the torus glides to its new frame.
        if(placedPortrait){float target=TopInsetTarget;if(Mathf.Abs(views.SceneTopInset-target)>.5f)views.SceneTopInset=Main.ReducedMotion?target:Mathf.Lerp(views.SceneTopInset,target,1-Mathf.Exp(-Time.unscaledDeltaTime*4));}
    }
    float diagramInset;
    float ToolbarInset=>RecordingMode.Active?12:50;
    float TopInsetTarget=>StepIndex>=0&&StepIndex<steps.Length&&!string.IsNullOrEmpty(steps[StepIndex].diagram)&&steps[StepIndex].diagram!="none"?diagramInset:ToolbarInset;
    // The layout, applied as the tour starts (so the torus is framed from its first frame) and
    // again whenever the window or its orientation change.
    void Place()
    {
        float width=root.resolvedStyle.width,height=root.resolvedStyle.height;if(!float.IsFinite(width)||width<1)return;
        bool portrait=Portrait;
        // Styles are written only when the window or its orientation change: the torus's frame is
        // fixed by the window alone, so a longer or shorter caption never moves or resizes it.
        if(width==placedWidth&&height==placedHeight&&portrait==placedPortrait&&RecordingMode.Active==placedRecording)return;
        placedRecording=RecordingMode.Active;
        if(portrait!=placedPortrait||placedWidth<0){float zoom=portrait?PortraitZoom:1;if(views.TorusZoom!=zoom){views.TorusZoom=zoom;views.ReframeTorus();}}
        placedWidth=width;placedHeight=height;placedPortrait=portrait;
        // Recording a vertical video: the caption starts two thirds of the way down, clear of the
        // space Shorts and Reels cover with their own titles and buttons.
        bool recordingVertical=portrait&&RecordingMode.Active;
        // Recording: no buttons, and no empty row where they were.
        buttonRow.style.display=RecordingMode.Active?DisplayStyle.None:DisplayStyle.Flex;progress.style.display=RecordingMode.Active?DisplayStyle.None:DisplayStyle.Flex;
        if(recordingVertical){caption.style.bottom=StyleKeyword.Auto;caption.style.top=height*RecordingMode.CaptionTop;}
        else{caption.style.top=StyleKeyword.Auto;caption.style.bottom=portrait?16:22;}
        if(portrait)
        {
            // Portrait: the diagram above the torus, the caption below it, the torus framed between.
            float d=Mathf.Min(width*.7f,height*.28f);
            diagrams.style.left=(width-d)*.5f;diagrams.style.top=RecordingMode.Active?8:58;diagrams.style.width=d;diagrams.style.height=d;
            caption.style.left=16;caption.style.width=width-32;
            // The torus fills the top two thirds (below the diagram on a step that has one); the
            // caption keeps the last third.
            diagramInset=(RecordingMode.Active?8:58)+d+6;views.SceneBottomInset=recordingVertical?height*(1-RecordingMode.CaptionTop)+8:height/3f;views.SceneTopInset=TopInsetTarget;
        }
        else
        {
            float d=Mathf.Min(380,height*.42f);diagrams.style.left=28;diagrams.style.top=Mathf.Max(20,height*.12f);diagrams.style.width=d;diagrams.style.height=d;
            float cw=Mathf.Min(1250,width*.8f);
            caption.style.left=(width-cw)*.5f;caption.style.width=cw;
            diagramInset=0;views.SceneTopInset=0;views.SceneBottomInset=height*.24f;
        }
    }

    // The moving triangle on the torus itself: its three corners are the edge at t, t+1/3 and
    // t+2/3 (a major third apart), so carrying it round the ring turns it a third of the way.
    // As it passes each of the four triangles' places it snaps that triangle in, lit in its
    // colour. An arrow ahead of the leading corner shows the direction of travel, an arc about
    // the centroid the direction of rotation.
    readonly Vector3[] tri=new Vector3[4],arrow=new Vector3[5],arc=new Vector3[14];
    // A 100 degree arc round the axis starting at `start` degrees, drawn the way the band turns, and
    // its arrowhead at the leading end; the start advances with time so the arrow circulates.
    void DrawSpin(LineRenderer line,Vector3 centre,Vector3 axis,Vector3 radial,float start,float sense,Color color)
    {
        for(int i=0;i<12;i++)arc[i]=centre+Quaternion.AngleAxis(start+sense*i*(100f/11f),axis)*radial;
        var end=arc[11];var dir=(arc[11]-arc[10]).normalized;var side=Vector3.Cross(dir,axis).normalized*.06f;
        arc[12]=end-dir*.1f+side;arc[13]=end;
        line.positionCount=14;line.SetPositions(arc);line.startColor=new Color(color.r,color.g,color.b,.35f);line.endColor=color;
    }
    void DrawSweep()
    {
        if(!sweeping){if(sweepTriangle.positionCount>0){sweepTriangle.positionCount=travelArrow.positionCount=spinArrow.positionCount=spinArrowBack.positionCount=0;foreach(var l in trails)l.positionCount=0;}return;}
        float u=Mathf.Clamp01(StepTime/sweepLength);Sweep=u;float t=sweepStart+u/3f;
        for(int j=0;j<3;j++)tri[j]=main.EdgePoint(t+j/3f);tri[3]=tri[0];
        sweepTriangle.positionCount=4;sweepTriangle.SetPositions(tri);
        var white=Color.white*1.6f;white.a=.95f;sweepTriangle.startColor=sweepTriangle.endColor=white;
        // Snap each of the four in as the sweep reaches it (every twelfth of the edge).
        int reach=Mathf.Min(4,Mathf.FloorToInt(u*4+1e-4f)+1);
        for(int k=0;k<reach;k++)
        {
            if(snapped[k].positionCount>0)continue;
            float tk=sweepStart+k/12f;var c=TriadColors[k];c.a=.85f;
            for(int j=0;j<3;j++)tri[j]=main.EdgePoint(tk+j/3f);tri[3]=tri[0];
            snapped[k].positionCount=4;snapped[k].SetPositions(tri);snapped[k].startColor=snapped[k].endColor=c;Snapped=k+1;
        }
        for(int j=0;j<3;j++)tri[j]=main.EdgePoint(t+j/3f);
        // Direction of travel: an arrow along the edge ahead of the leading corner.
        var ahead=main.EdgePoint(t+.02f);var tangent=(ahead-tri[0]).normalized;var lead=tri[0]+tangent*.12f;var tip=lead+tangent*.32f;
        var normal=Vector3.Cross(tri[1]-tri[0],tri[2]-tri[0]).normalized;var sideways=Vector3.Cross(tangent,normal).normalized*.07f;
        arrow[0]=lead;arrow[1]=tip;arrow[2]=tip-tangent*.1f+sideways;arrow[3]=tip;arrow[4]=tip-tangent*.1f-sideways;
        travelArrow.positionCount=5;travelArrow.SetPositions(arrow);var gold=new Color(1,.85f,.4f,.95f);travelArrow.startColor=travelArrow.endColor=gold;
        // The umbilic, traced: each corner draws the stretch of edge it has swept, so by the end the
        // three trails have drawn the whole edge once. Their colours shimmer and flicker.
        int reached=Mathf.Clamp(Mathf.CeilToInt(u*(TrailPoints-1))+1,2,TrailPoints);
        for(int c=0;c<3;c++)
        {
            for(int k=0;k<reached;k++)trail[k]=main.EdgePoint(sweepStart+c/3f+(k/(TrailPoints-1f))*u/3f);
            trails[c].positionCount=reached;trails[c].SetPositions(trail);
            float flicker=Mathf.PerlinNoise(Time.unscaledTime*7f,c*3.1f);
            var hue=Color.Lerp(TriadColors[(c+Mathf.FloorToInt(Time.unscaledTime*1.5f))%4],Color.white,.25f+.35f*Mathf.Sin(Time.unscaledTime*9+c*2.1f));
            hue*=1.3f+1.2f*flicker;hue.a=.75f+.25f*flicker;trails[c].startColor=new Color(hue.r,hue.g,hue.b,.25f);trails[c].endColor=hue;
        }
        // Direction of rotation: two arcs round the band's cross-section at the triangle, a little
        // outside its corners, each with a head, circling the way the corners actually turn.
        var centroid=(tri[0]+tri[1]+tri[2])/3;var axis=Vector3.Cross(tri[1]-tri[0],tri[2]-tri[0]).normalized;
        float du=.004f;Vector3 next0=main.EdgePoint(t+du/3f),next1=main.EdgePoint(t+du/3f+1/3f),next2=main.EdgePoint(t+du/3f+2/3f);
        var nextCentroid=(next0+next1+next2)/3;
        float sense=Mathf.Sign(Vector3.Dot(axis,Vector3.Cross(tri[0]-centroid,next0-nextCentroid)));if(sense==0)sense=1;
        var radial=(tri[0]-centroid)*1.28f;float phase=Main.ReducedMotion?0:Time.unscaledTime*140f*sense;
        DrawSpin(spinArrow,centroid,axis,radial,phase,sense,gold);DrawSpin(spinArrowBack,centroid,axis,radial,phase+180,sense,gold);
    }
    // The diagrams beside the torus: the chromatic circle, the four triangles, the triangles
    // spinning a third of a turn per revolution, the colour legend, the circle of fifths.
    sealed class Diagrams : VisualElement
    {
        readonly TutorialDirector owner;readonly Main main;
        static readonly Color Ink=new(.5f,.74f,.72f);
        static readonly Color[] Triad={new(.36f,.62f,1),new(1,.42f,.42f),new(.42f,.9f,.5f),new(.85f,.6f,1)};
        public Diagrams(TutorialDirector owner,Main main){this.owner=owner;this.main=main;generateVisualContent+=Draw;}
        void Draw(MeshGenerationContext ctx)
        {
            string kind=owner.Diagram;if(string.IsNullOrEmpty(kind)||kind=="none")return;
            var p=ctx.painter2D;float w=contentRect.width,h=contentRect.height;var c=new Vector2(w*.5f,h*.5f);float r=Mathf.Min(w,h)*.38f;float t=owner.StepTime;
            // Text scales with the diagram (12 px at 300 px across).
            float ks=Mathf.Max(1,Mathf.Min(w,h)/300f);
            Vector2 At(int i,float radius,float spin=0){float a=(i/12f+spin)*Mathf.PI*2-Mathf.PI*.5f;return c+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*radius;}
            void Ring(){p.strokeColor=new Color(Ink.r,Ink.g,Ink.b,.45f);p.lineWidth=1.2f;p.BeginPath();p.Arc(c,r,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();}
            void Dot(Vector2 at,float size,Color color,float flare=0)
            {
                // Bloom: layered halos, wider and brighter while the point flares in.
                for(int g=3;g>=1;g--){var halo=color;halo.a*=(.07f+.1f*flare)*(4-g);p.fillColor=halo;p.BeginPath();p.Arc(at,size*(1+g*(1.1f+1.6f*flare)),Angle.Degrees(0),Angle.Degrees(360));p.Fill();}
                p.fillColor=Color.Lerp(color,Color.white,.35f*flare);p.BeginPath();p.Arc(at,size*(1+.4f*flare),Angle.Degrees(0),Angle.Degrees(360));p.Fill();
            }
            void GlowStroke(System.Action path,Color color,float width,float flare=0)
            {
                for(int g=3;g>=1;g--){var halo=color;halo.a*=(.06f+.1f*flare)*(4-g);p.strokeColor=halo;p.lineWidth=width*(1+g*(1.3f+2f*flare));p.BeginPath();path();p.Stroke();}
                p.strokeColor=Color.Lerp(color,Color.white,.3f*flare);p.lineWidth=width;p.BeginPath();path();p.Stroke();
            }
            void Name(int pc,Vector2 at,Color color){string s=main.PitchName(pc);ctx.DrawText(s,at-new Vector2(s.Length*3.6f*ks,7*ks),12*ks,color);}
            switch(kind)
            {
                case "chromatic":
                {
                    Ring();int shown=Mathf.Clamp(Mathf.FloorToInt(t*4),0,12);
                    for(int i=0;i<12;i++){var at=At(i,r);bool on=i<shown;float flare=on?Mathf.Exp(-(t-i/4f)*5f):0;Dot(at,(on?5:2.5f)*ks,on?Color.white:new Color(1,1,1,.3f),flare);if(on)Name(HarmonyModel.Mod(main.currentKey+i),At(i,r+22*ks),new Color(1,1,1,.85f));}
                    ctx.DrawText("12 tones · a semitone apart",new Vector2(8,h-20*ks),12*ks,new Color(Ink.r,Ink.g,Ink.b,.9f));break;
                }
                case "triangles":
                case "spin":
                {
                    Ring();
                    // Four augmented triads: every fourth tone. In "spin" each turns on, a third of a turn per second.
                    for(int k=0;k<4;k++)
                    {
                        float appear=kind=="triangles"?Mathf.Clamp01(t*.9f-k*.8f):k<owner.Snapped?1:0;if(appear<=0)continue;
                        float spin=0;var color=Triad[k];color.a=appear;
                        // The triangle flares as it is drawn in, then keeps a soft glow.
                        float born=kind=="triangles"?(k*.8f+1)/.9f:0,flare=kind=="triangles"?Mathf.Clamp01(Mathf.Exp(-(t-born)*3f))*(appear>=1?1:appear):0;
                        int kk=k;GlowStroke(()=>{for(int v=0;v<=3;v++){var at=At(kk+4*(v%3),r,spin);if(v==0)p.MoveTo(at);else p.LineTo(at);}},color,2.2f*ks,flare);
                        for(int v=0;v<3;v++)Dot(At(k+4*v,r,spin),4*ks,color,flare);
                    }
                    if(kind=="triangles")for(int i=0;i<12;i++)Name(HarmonyModel.Mod(main.currentKey+i),At(i,r+22*ks),new Color(1,1,1,.75f));
                    ctx.DrawText(kind=="triangles"?"4 equilateral triangles · every fourth tone":"a third of a turn each time round: one edge, three sides",new Vector2(8,h-20*ks),12*ks,new Color(Ink.r,Ink.g,Ink.b,.9f));
                    if(kind=="spin")
                    {
                        // The moving triangle, turning a third of the way round over the sweep, and an
                        // arrow round the rim for the direction of travel.
                        float spin=owner.Sweep/3f;p.strokeColor=Color.white;p.lineWidth=2.6f;p.BeginPath();
                        for(int v=0;v<=3;v++){var at=At(4*(v%3),r,spin);if(v==0)p.MoveTo(at);else p.LineTo(at);}
                        p.Stroke();
                        p.strokeColor=new Color(1,.85f,.4f,.9f);p.lineWidth=2;p.BeginPath();
                        float a0=(owner.Sweep+.02f)*Mathf.PI*2-Mathf.PI*.5f,a1=a0+.5f;
                        p.Arc(c,r+12,Angle.Radians(a0),Angle.Radians(a1));p.Stroke();
                        var tipAt=c+new Vector2(Mathf.Cos(a1),Mathf.Sin(a1))*(r+12);var tan=new Vector2(-Mathf.Sin(a1),Mathf.Cos(a1));var nrm=new Vector2(Mathf.Cos(a1),Mathf.Sin(a1));
                        p.BeginPath();p.MoveTo(tipAt-tan*7+nrm*5);p.LineTo(tipAt);p.LineTo(tipAt-tan*7-nrm*5);p.Stroke();
                    }
                    break;
                }
                case "legend":
                {
                    string[] names={"Key (I)","Subdominant (IV)","Dominant (V)"};Color[] colors={Color.blue,Color.red,Color.green};int[] rel={0,5,7};
                    for(int i=0;i<3;i++)
                    {
                        float y=h*.25f+i*h*.2f;p.fillColor=colors[i];p.BeginPath();p.MoveTo(new Vector2(20,y));p.LineTo(new Vector2(60,y));p.LineTo(new Vector2(60,y+26));p.LineTo(new Vector2(20,y+26));p.ClosePath();p.Fill();
                        ctx.DrawText($"{names[i]} · {main.PitchName(HarmonyModel.Mod(main.currentKey+rel[i]))}",new Vector2(72,y+5),14*ks,Color.white);
                    }
                    ctx.DrawText("every other chord blends these by where it stands",new Vector2(8,h-20*ks),12*ks,new Color(Ink.r,Ink.g,Ink.b,.9f));break;
                }
                case "fifths":
                {
                    Ring();
                    for(int i=0;i<12;i++){int pc=HarmonyModel.Mod(main.currentKey+i*7);var at=At(i,r);bool triad=i is 0 or 1 or 11;Dot(at,triad?5.5f:3.5f,i==0?Color.blue:i==1?Color.green:i==11?Color.red:new Color(1,1,1,.7f));Name(pc,At(i,r+22*ks),new Color(1,1,1,.85f));}
                    ctx.DrawText("circle of fifths: one ring · the torus adds the thirds across the band",new Vector2(8,h-20*ks),11*ks,new Color(Ink.r,Ink.g,Ink.b,.9f));break;
                }
            }
        }
    }
}
