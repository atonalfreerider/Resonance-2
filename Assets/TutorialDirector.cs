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
    [Serializable] sealed class Clip {public string id="",file="";public double seconds;}
    [Serializable] sealed class Manifest {public int version;public Clip[] clips=Array.Empty<Clip>();}
    public static string Folder=>Path.Combine(Application.streamingAssetsPath,"Tutorial");
    public bool Playing {get;private set;}
    public int StepIndex {get;private set;}=-1;
    public string Diagram {get;private set;}="";
    public float StepTime {get;private set;}
    // The sweep of the moebius step: how far round the ring the moving triangle is (0..1) and
    // how many of the four triangles it has snapped into place.
    public float Sweep {get;private set;}
    public int Snapped {get;private set;}
    LineRenderer sweepTriangle,travelArrow,spinArrow;readonly LineRenderer[] snapped=new LineRenderer[4];Material sweepGlow;bool sweeping;float sweepStart,sweepLength;
    static readonly Color[] TriadColors={new(.36f,.62f,1),new(1,.42f,.42f),new(.42f,.9f,.5f),new(.85f,.6f,1)};
    Main main;MidiPlayer midi;VisualizationViews views;SongLibraryPanel library;CameraControl orbit;AudioSource voice;
    VisualElement root,caption;Label title,text,progress;Button skip,back,next;Diagrams diagrams;int jump=-1;
    Step[] steps=Array.Empty<Step>();readonly Dictionary<string,AudioClip> clips=new();readonly Dictionary<string,double> seconds=new();
    Coroutine run;int keyBefore;bool minorBefore;VisualizationViews.View viewBefore;

    public void Bind(VisualElement ui)
    {
        root=ui;main=GetComponent<Main>();midi=GetComponent<MidiPlayer>();views=GetComponent<VisualizationViews>();library=GetComponent<SongLibraryPanel>();
        orbit=Camera.main!=null?Camera.main.GetComponent<CameraControl>():null;
        var go=new GameObject("Tutorial narration");go.transform.SetParent(transform,false);voice=go.AddComponent<AudioSource>();voice.playOnAwake=false;voice.spatialBlend=0;
        sweepGlow=new Material(Resources.Load<Shader>("HarmonicGlow"));sweepGlow.SetColor("_BaseColor",Color.white*2);sweepGlow.renderQueue=3107;
        sweepTriangle=Stroke("Tutorial · sweeping triangle",.035f);travelArrow=Stroke("Tutorial · direction of travel",.03f);spinArrow=Stroke("Tutorial · direction of rotation",.025f);
        for(int i=0;i<4;i++)snapped[i]=Stroke("Tutorial · triangle "+i,.022f);
        diagrams=new Diagrams(this,main){name="tutorial-diagram",pickingMode=PickingMode.Ignore};diagrams.style.position=Position.Absolute;diagrams.style.display=DisplayStyle.None;root.Add(diagrams);
        caption=new VisualElement{name="tutorial-caption",pickingMode=PickingMode.Ignore};caption.style.position=Position.Absolute;caption.style.display=DisplayStyle.None;
        caption.style.backgroundColor=new Color(.05f,.075f,.12f,.88f);caption.style.paddingLeft=caption.style.paddingRight=22;caption.style.paddingTop=14;caption.style.paddingBottom=14;
        caption.style.borderTopLeftRadius=caption.style.borderTopRightRadius=caption.style.borderBottomLeftRadius=caption.style.borderBottomRightRadius=12;
        title=new Label("");title.style.fontSize=17;title.style.unityFontStyleAndWeight=FontStyle.Bold;title.style.color=Color.white;title.style.marginBottom=4;caption.Add(title);
        text=new Label("");text.style.fontSize=14;text.style.whiteSpace=WhiteSpace.Normal;text.style.color=new Color(.86f,.9f,.95f);caption.Add(text);
        progress=new Label("");progress.style.fontSize=11;progress.style.color=new Color(.52f,.66f,.8f);progress.style.marginTop=8;caption.Add(progress);
        root.Add(caption);
        skip=new Button(Stop){text="Skip tutorial",name="tutorial-skip"};skip.style.position=Position.Absolute;skip.style.display=DisplayStyle.None;root.Add(skip);
        // Step through: back to the step before, on to the next.
        var row=new VisualElement{pickingMode=PickingMode.Ignore};row.style.flexDirection=FlexDirection.Row;row.style.marginTop=8;caption.Add(row);
        back=new Button(()=>Jump(StepIndex-1)){text="‹ Back",name="tutorial-back"};next=new Button(()=>Jump(StepIndex+1)){text="Next ›",name="tutorial-next"};
        row.Add(back);row.Add(next);
        try
        {
            var script=JsonUtility.FromJson<Script>(File.ReadAllText(Path.Combine(Folder,"script.json")));steps=script?.steps??Array.Empty<Step>();
            string manifestPath=Path.Combine(Folder,"manifest.json");
            if(File.Exists(manifestPath))foreach(var c in JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath)).clips)seconds[c.id]=c.seconds;
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
        Playing=true;keyBefore=main.currentKey;minorBefore=main.MinorMode;viewBefore=views.Current;
        midi.Pause();library.Hide();views.SetView(VisualizationViews.View.Torus);views.SetPanelHidden(true);
        caption.style.display=DisplayStyle.Flex;skip.style.display=DisplayStyle.Flex;diagrams.style.display=DisplayStyle.Flex;
        yield return Load();
        for(int i=0;i<steps.Length;i++)
        {
            var step=steps[i];StepIndex=i;StepTime=0;Diagram=step.diagram;jump=-1;
            back.SetEnabled(i>0);next.SetEnabled(i<steps.Length-1);
            // The band is seen through while a triangle sweeps round inside it.
            views.TorusOpacityCap=step.action=="spin"?.38f:1;
            if(step.action!="spin"){sweeping=false;Snapped=0;foreach(var l in snapped)l.positionCount=0;}
            title.text=step.title;text.text=step.text;progress.text=$"{i+1} / {steps.Length}";
            double length=clips.TryGetValue(step.id,out var clip)?clip.length:seconds.TryGetValue(step.id,out var s)?s:Mathf.Max(5,step.text.Length/16f);
            if(clip!=null){voice.clip=clip;voice.Play();}
            var action=StartCoroutine(Act(step.action,(float)length));
            for(float t=0;t<length+.6f&&jump<0;t+=Time.unscaledDeltaTime){StepTime=t;if(orbit!=null&&step.action!="keychange")orbit.Turn(-.09f*Time.unscaledDeltaTime);yield return null;}
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
    IEnumerator Act(string action,float length)
    {
        switch(action)
        {
            case "labels":
                // Light the key, its fourth and its fifth in turn.
                foreach(int rel in new[]{0,7,5,0}){Light(new[]{rel});yield return Wait(length/4.5f);}
                main.Silence();break;
            case "chords":
                // I, IV, V, I: the three colours the rest are blended from.
                foreach(var chord in new[]{new[]{0,4,7},new[]{5,9,0},new[]{7,11,2},new[]{0,4,7}}){Light(chord);yield return Wait(length/4.4f);}
                main.Silence();break;
            case "spin":
                // One triangle carried once round the ring, tracing the edge: a third of the
                // parameter is one revolution, and every twelfth it lands on one of the four.
                sweeping=true;sweepStart=main.EdgeParameter(main.currentKey);sweepLength=Mathf.Max(6,length*.82f);
                for(int i=0;i<4;i++)snapped[i].positionCount=0;Snapped=0;
                yield return Wait(length);
                sweeping=false;break;
            case "keychange":
                yield return Wait(length*.3f);
                main.ChangeKey(main.currentKey+7,2.4f);yield return Wait(length*.45f);
                main.ChangeKey(main.currentKey-7,2.4f);break;
            case "finish":
                yield return Wait(length*.9f);break;
        }
    }
    static IEnumerator Wait(float s){for(float t=0;t<s;t+=Time.unscaledDeltaTime)yield return null;}
    void Light(int[] degrees)
    {
        var list=new List<Tuple<int,float>>();
        foreach(int d in degrees){int pc=HarmonyModel.Mod(main.currentKey+d);list.Add(Tuple.Create(pc+Main.Tones*1,.8f));}
        main.SetNotes(list,true);
    }
    void Finish()
    {
        if(!Playing)return;Playing=false;StepIndex=-1;Diagram="";sweeping=false;Sweep=0;Snapped=0;
        sweepTriangle.positionCount=travelArrow.positionCount=spinArrow.positionCount=0;foreach(var l in snapped)l.positionCount=0;
        voice.Stop();main.Silence();
        if(main.currentKey!=keyBefore){main.MinorMode=minorBefore;main.ChangeKey(keyBefore,.7f);}
        caption.style.display=DisplayStyle.None;skip.style.display=DisplayStyle.None;diagrams.style.display=DisplayStyle.None;views.TorusOpacityCap=1;
        views.SetView(midi.Loaded?viewBefore:VisualizationViews.View.Overview);library.Show();
    }
    void LateUpdate()
    {
        if(!Playing||root==null)return;
        DrawSweep();
        float width=root.resolvedStyle.width,height=root.resolvedStyle.height;if(!float.IsFinite(width))return;
        float cw=Mathf.Min(560,width*.5f);
        caption.style.left=(width-cw)*.5f;caption.style.width=cw;caption.style.top=height-caption.layout.height-28;
        skip.style.left=width-150;skip.style.top=60;
        float d=Mathf.Min(380,height*.42f);diagrams.style.left=28;diagrams.style.top=(height-d)*.5f-40;diagrams.style.width=d;diagrams.style.height=d;
        diagrams.MarkDirtyRepaint();
    }

    // The moving triangle on the torus itself: its three corners are the edge at t, t+1/3 and
    // t+2/3 (a major third apart), so carrying it round the ring turns it a third of the way.
    // As it passes each of the four triangles' places it snaps that triangle in, lit in its
    // colour. An arrow ahead of the leading corner shows the direction of travel, an arc about
    // the centroid the direction of rotation.
    readonly Vector3[] tri=new Vector3[4],arrow=new Vector3[5],arc=new Vector3[14];
    void DrawSweep()
    {
        if(!sweeping){if(sweepTriangle.positionCount>0){sweepTriangle.positionCount=travelArrow.positionCount=spinArrow.positionCount=0;}return;}
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
        // Direction of rotation: an arc about the centroid, in the triangle's plane, with a head.
        var centroid=(tri[0]+tri[1]+tri[2])/3;var radial=(tri[0]-centroid)*.45f;
        for(int i=0;i<12;i++){float a=i*11f;arc[i]=centroid+Quaternion.AngleAxis(a,normal)*radial;}
        var end=arc[11];var dir=(arc[11]-arc[10]).normalized;var off=Vector3.Cross(dir,normal).normalized*.05f;
        arc[12]=end-dir*.08f+off;arc[13]=end;
        spinArrow.positionCount=14;spinArrow.SetPositions(arc);spinArrow.startColor=spinArrow.endColor=gold;
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
            Vector2 At(int i,float radius,float spin=0){float a=(i/12f+spin)*Mathf.PI*2-Mathf.PI*.5f;return c+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*radius;}
            void Ring(){p.strokeColor=new Color(Ink.r,Ink.g,Ink.b,.45f);p.lineWidth=1.2f;p.BeginPath();p.Arc(c,r,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();}
            void Dot(Vector2 at,float size,Color color){p.fillColor=color;p.BeginPath();p.Arc(at,size,Angle.Degrees(0),Angle.Degrees(360));p.Fill();}
            void Name(int pc,Vector2 at,Color color){string s=main.PitchName(pc);ctx.DrawText(s,at-new Vector2(s.Length*3.6f,7),12,color);}
            switch(kind)
            {
                case "chromatic":
                {
                    Ring();int shown=Mathf.Clamp(Mathf.FloorToInt(t*4),0,12);
                    for(int i=0;i<12;i++){var at=At(i,r);bool on=i<shown;Dot(at,on?5:2.5f,on?Color.white:new Color(1,1,1,.3f));if(on)Name(HarmonyModel.Mod(main.currentKey+i),At(i,r+22),new Color(1,1,1,.85f));}
                    ctx.DrawText("12 tones · a semitone apart",new Vector2(8,h-20),12,new Color(Ink.r,Ink.g,Ink.b,.9f));break;
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
                        p.strokeColor=color;p.lineWidth=2.2f;p.BeginPath();
                        for(int v=0;v<=3;v++){var at=At(k+4*(v%3),r,spin);if(v==0)p.MoveTo(at);else p.LineTo(at);}
                        p.Stroke();
                        for(int v=0;v<3;v++)Dot(At(k+4*v,r,spin),4,color);
                    }
                    if(kind=="triangles")for(int i=0;i<12;i++)Name(HarmonyModel.Mod(main.currentKey+i),At(i,r+22),new Color(1,1,1,.75f));
                    ctx.DrawText(kind=="triangles"?"4 equilateral triangles · every fourth tone":"a third of a turn each time round: one edge, three sides",new Vector2(8,h-20),12,new Color(Ink.r,Ink.g,Ink.b,.9f));
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
                        ctx.DrawText($"{names[i]} · {main.PitchName(HarmonyModel.Mod(main.currentKey+rel[i]))}",new Vector2(72,y+5),14,Color.white);
                    }
                    ctx.DrawText("every other chord blends these by where it stands",new Vector2(8,h-20),12,new Color(Ink.r,Ink.g,Ink.b,.9f));break;
                }
                case "fifths":
                {
                    Ring();
                    for(int i=0;i<12;i++){int pc=HarmonyModel.Mod(main.currentKey+i*7);var at=At(i,r);bool triad=i is 0 or 1 or 11;Dot(at,triad?5.5f:3.5f,i==0?Color.blue:i==1?Color.green:i==11?Color.red:new Color(1,1,1,.7f));Name(pc,At(i,r+22),new Color(1,1,1,.85f));}
                    ctx.DrawText("circle of fifths: one ring · the torus adds the thirds across the band",new Vector2(8,h-20),11,new Color(Ink.r,Ink.g,Ink.b,.9f));break;
                }
            }
        }
    }
}
