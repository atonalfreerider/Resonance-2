using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

// Two visual instruments share the prepared score and recording clock; they create no audio sources.
[DefaultExecutionOrder(1100)]
public sealed class ConcertoTori : MonoBehaviour
{
    Main owner;MidiPlayer midi;VisualizationViews views;Camera master;
    Main[] shapes=new Main[2];Rect[] bounds=new Rect[2];Label[] labels=new Label[2];VisualElement[] badges=new VisualElement[2];
    PreparedPatternSong source;int frameIndex;double last=-1;bool lastAudible;
    readonly List<Renderer> ownerRenderers=new();
    bool previousOrthographic;float previousSize;CameraClearFlags previousClear;Color previousBackground;
    Camera backgroundCamera;bool backgroundEnabled;
    int lastSeek=-1;double focusSince=-1;readonly float[] depths=new float[2];bool lastDirected;
    public string ManualFocus {get;private set;}="balanced";
    public void SetFocus(string focus){if(!new[]{"balanced","piano","orchestra","piano-only","orchestra-only"}.Contains(focus))throw new ArgumentException("Invalid concerto focus");ManualFocus=focus;focusSince=-1;}
    public bool SplitActive {get;private set;}
    public Rect PianoViewport=>bounds[0];
    public Rect OrchestraViewport=>bounds[1];
    public Main Piano=>shapes[0];public Main Orchestra=>shapes[1];
    public string Focus {get;private set;}="balanced";
    public static Rect[] Split(Rect area,bool vertical)=>vertical
        ?new[]{new Rect(area.x,area.y+area.height*.5f,area.width,area.height*.5f),new Rect(area.x,area.y,area.width,area.height*.5f)}
        :new[]{new Rect(area.x,area.y,area.width*.5f,area.height),new Rect(area.x+area.width*.5f,area.y,area.width*.5f,area.height)};
    void Awake(){owner=GetComponent<Main>();midi=GetComponent<MidiPlayer>();views=GetComponent<VisualizationViews>();master=Camera.main;}
    void Create()
    {
        backgroundCamera=Resources.FindObjectsOfTypeAll<Camera>().FirstOrDefault(c=>c.name=="Screen clear"&&c.gameObject.scene==gameObject.scene);
        var root=GetComponent<UIDocument>().rootVisualElement;
        for(int i=0;i<2;i++){
            var go=new GameObject(i==0?"Piano torus":"Orchestra torus");go.SetActive(false);go.transform.position=new Vector3(100+i*100,0,0);
            var shape=go.AddComponent<Main>();shape.VisualOnly=true;shape.VisualCamera=master;shape.BloomMat=owner.BloomMat;shape.UseSharedSynth(owner.Synth);
            go.AddComponent<TonalDominance>();go.SetActive(true);
            var feature=go.GetComponent<FeaturedInstrument>();feature.PlaybackSource=midi;feature.GroupTracks=i==0?midi.HarmonicPrepared.PianoTracks:midi.HarmonicPrepared.OrchestraTracks;
            shapes[i]=shape;
            var label=new Label(i==0?"PIANO":"ORCHESTRA"){name=i==0?"piano-torus-label":"orchestra-torus-label",pickingMode=PickingMode.Ignore};
            label.style.position=Position.Absolute;label.style.color=new Color(.85f,.92f,1);label.style.fontSize=14;label.style.letterSpacing=3;root.Add(label);labels[i]=label;
            var badge=new PartBadge(i==0){name=i==0?"piano-part-badge":"orchestra-part-badge",pickingMode=PickingMode.Ignore};badge.style.position=Position.Absolute;badge.style.width=92;badge.style.height=36;root.Add(badge);badges[i]=badge;
        }
    }
    void LateUpdate()
    {
        bool active=midi.Loaded&&midi.HarmonicPrepared?.PianoConcerto==true&&views.Current==VisualizationViews.View.Torus&&!VrSession.Active;
        if(active&&shapes[0]==null)Create();
        bool wasSplit=SplitActive;SplitActive=active;
        if(shapes[0]==null)return;
        var drums=owner.GetComponent<DrumPatternDeck>()?.WheelTransform;
        if(active||wasSplit){
            // Notes, melody strands and effects can be replaced without changing the
            // hierarchy count. Always include their current renderers when hiding the
            // combined torus, otherwise a replacement becomes a third, ghost torus.
            owner.GetComponentsInChildren(true,ownerRenderers);
            foreach(var renderer in ownerRenderers)if(renderer!=null&&(drums==null||!renderer.transform.IsChildOf(drums)))renderer.forceRenderingOff=active||views.TorusOpacity<.005f;
        }
        if(!active){
            if(wasSplit){master.orthographic=previousOrthographic;master.orthographicSize=previousSize;master.clearFlags=previousClear;master.backgroundColor=previousBackground;master.ResetProjectionMatrix();if(backgroundCamera!=null)backgroundCamera.enabled=backgroundEnabled;}
            DisposeVisuals();last=-1;return;
        }
        if(!wasSplit){previousOrthographic=master.orthographic;previousSize=master.orthographicSize;previousClear=master.clearFlags;previousBackground=master.backgroundColor;backgroundEnabled=backgroundCamera!=null&&backgroundCamera.enabled;}
        if(backgroundCamera!=null)backgroundCamera.enabled=false;
        bool vertical=views.Vertical||Screen.height>Screen.width;
        master.rect=new Rect(0,0,1,1);master.orthographic=true;master.orthographicSize=vertical?4.9f:Mathf.Max(2.45f,4.9f/(Screen.width/(float)Screen.height));
        master.clearFlags=CameraClearFlags.SolidColor;master.backgroundColor=Color.black;master.ResetProjectionMatrix();
        if(source!=midi.Prepared){source=midi.Prepared;last=-1;foreach(var s in shapes)s.ClearVisualMemory();}
        double now=midi.VisualScorePosition;bool seek=last<0||now<last||midi.SeekRevision!=lastSeek;
        int lo=0,hi=source.Frames.Length;while(lo<hi){int mid=(lo+hi)/2;if(source.Frames[mid].Time<=now)lo=mid+1;else hi=mid;}
        var frame=lo>0?source.Frames[lo-1]:null;
        var director=GetComponent<SongDirector>();
        var activeCue=director?.Directing==true?director.Current?.cues.FirstOrDefault(c=>now>=c.start&&now<c.end):null;
        string requested=director?.Directing==true?activeCue?.concertoFocus??"balanced":ManualFocus;
        // Sounding notes never reframe the duet. Only an authored cue or a direct
        // user choice can feature a section, with at least twelve score seconds
        // between changes during continuous playback. Seeks adopt their cue.
        bool directing=director?.Directing==true;
        if(seek||focusSince<0||!directing||directing!=lastDirected){Focus=requested;focusSince=now;}
        else if(Focus!=requested&&now-focusSince>=12){Focus=requested;focusSince=now;}
        lastDirected=directing;
        var viewport=views.PanelHidden?new Rect(0,0,1,1):new Rect(views.SceneRect.x/Screen.width,1-views.SceneRect.yMax/Screen.height,views.SceneRect.width/Screen.width,views.SceneRect.height/Screen.height);
        var target=DirectedLayout(viewport,vertical,Focus);
        bool custom=activeCue?.melodyTarget=="three"||activeCue?.directedMelody==true;
        float easing=1-Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime,.1f)*.6f);
        for(int i=0;i<2;i++)bounds[i]=seek||director?.Directing!=true?target[i]:Lerp(bounds[i],target[i],easing);
        for(int i=0;i<2;i++){
            var shape=shapes[i];var tracks=i==0?midi.HarmonicPrepared.PianoTracks:midi.HarmonicPrepared.OrchestraTracks;
            bool hidden=(i==1&&Focus=="piano-only")||(i==0&&Focus=="orchestra-only");
            shape.gameObject.SetActive(!hidden);
            labels[i].style.display=hidden?DisplayStyle.None:DisplayStyle.Flex;badges[i].style.display=labels[i].style.display;
            shape.GetComponent<FeaturedInstrument>().SetDirectedMelody(custom?(i==0?director.Current.pianoMelody:director.Current.orchestraMelody):null);
            shape.UseSharedSynth(owner.Synth);shape.MinorMode=owner.MinorMode;shape.UseFlats=owner.UseFlats;
            shape.ShowSurfaces=owner.ShowSurfaces;shape.ShowDiagonals=owner.ShowDiagonals;shape.ShowStructure=owner.ShowStructure;shape.ShowRegisters=owner.ShowRegisters;
            shape.SoundingOnly=owner.SoundingOnly;shape.DiatonicStrip=owner.DiatonicStrip;shape.ShowHarmonics=owner.ShowHarmonics;shape.FieldDensity=owner.FieldDensity;
            shape.ResonanceHalfLife=owner.ResonanceHalfLife;shape.ResonanceGain=owner.ResonanceGain;shape.VisualReleaseSeconds=owner.VisualReleaseSeconds;shape.NoteReleaseSeconds=owner.NoteReleaseSeconds;
            bool foreground=(i==0&&Focus=="piano")||(i==1&&Focus=="orchestra");
            float targetDepth=foreground?-.8f:Focus=="piano"||Focus=="orchestra"?1.8f:0;
            depths[i]=seek||director?.Directing!=true?targetDepth:Mathf.Lerp(depths[i],targetDepth,easing);
            float depth=Mathf.Max(5,Vector3.Distance(master.transform.position,owner.transform.position))+depths[i];
            // A small shared-clock drift keeps the duet alive without changing either torus's orientation.
            float drift=director?.Directing==true&&!owner.Uncoiled?(float)Math.Sin(now*.065+i*Math.PI)*.012f:0;
            var position=master.ViewportToWorldPoint(new Vector3(bounds[i].center.x+drift,bounds[i].center.y,depth));
            if(shape.transform.position!=position||shape.transform.rotation!=owner.transform.rotation)shape.transform.SetPositionAndRotation(position,owner.transform.rotation);
            var a=master.ViewportToWorldPoint(new Vector3(bounds[i].xMin,bounds[i].yMin,depth));var b=master.ViewportToWorldPoint(new Vector3(bounds[i].xMax,bounds[i].yMax,depth));
            float size=Mathf.Min(Mathf.Abs(Vector3.Dot(b-a,master.transform.right)),Mathf.Abs(Vector3.Dot(b-a,master.transform.up)));
            var scale=Vector3.one*size*.42f/2.25f;if(shape.transform.localScale!=scale)shape.transform.localScale=scale;
            shape.MatchVisualPose(owner);
            shape.UpdateText();
            if(seek)shape.ClearVisualMemory();
            if(seek||lo!=frameIndex||lastAudible!=midi.IsAudible||!midi.IsAudible){
                var notes=midi.IsAudible&&frame!=null?frame.Voices.Where(v=>tracks.Contains(v.Track)&&v.Pitch>=21&&v.Pitch<117).Select(v=>Tuple.Create(v.Pitch-21,v.Velocity)).ToList():new List<Tuple<int,float>>();
                shape.SetNotes(notes,false);
            }
            if(midi.IsAudible&&!seek)for(int f=frameIndex;f<lo;f++)foreach(var attack in source.Frames[f].Attacks)if(tracks.Contains(attack.Track))shape.StrikeNote(attack.Pitch-21,attack.Velocity);
            labels[i].style.left=bounds[i].xMin*Screen.width+20;labels[i].style.top=(1-bounds[i].yMax)*Screen.height+54;
            badges[i].style.left=bounds[i].xMin*Screen.width+20;badges[i].style.top=(1-bounds[i].yMax)*Screen.height+78;
        }
        frameIndex=lo;lastSeek=midi.SeekRevision;last=now;lastAudible=midi.IsAudible;
    }
    static Rect Lerp(Rect a,Rect b,float t)=>new Rect(Mathf.Lerp(a.x,b.x,t),Mathf.Lerp(a.y,b.y,t),Mathf.Lerp(a.width,b.width,t),Mathf.Lerp(a.height,b.height,t));
    public static Rect[] DirectedLayout(Rect area,bool vertical,string focus)
    {
        var result=Split(area,vertical);
        Rect Cell(float x,float y,float w,float h)=>new Rect(area.x+x*area.width,area.y+y*area.height,w*area.width,h*area.height);
        if(focus=="piano-only"){result[0]=area;return result;}
        if(focus=="orchestra-only"){result[1]=area;return result;}
        if(focus!="piano"&&focus!="orchestra")return result;
        int front=focus=="piano"?0:1;
        result[front]=vertical?Cell(.03f,.02f,.94f,.68f):Cell(.01f,.02f,.74f,.94f);
        result[1-front]=vertical?Cell(.25f,.66f,.5f,.33f):Cell(.65f,.51f,.34f,.46f);
        return result;
    }
    // Quiet vector identities stay crisp in portrait recordings without opaque backplates.
    sealed class PartBadge : VisualElement
    {
        readonly bool piano;
        public PartBadge(bool keyboard){piano=keyboard;generateVisualContent+=Draw;}
        void Draw(MeshGenerationContext ctx)
        {
            var p=ctx.painter2D;p.lineWidth=.8f;p.lineCap=LineCap.Round;p.lineJoin=LineJoin.Round;
            Color ink=new Color(.62f,.72f,.79f,.52f),paper=new Color(.46f,.57f,.65f,.12f);
            p.strokeColor=ink;p.fillColor=paper;
            void Line(float x,float y,float a,float b){p.BeginPath();p.MoveTo(new Vector2(x,y));p.LineTo(new Vector2(a,b));p.Stroke();}
            void Box(float x,float y,float w,float h,bool fill){p.BeginPath();p.MoveTo(new Vector2(x,y));p.LineTo(new Vector2(x+w,y));p.LineTo(new Vector2(x+w,y+h));p.LineTo(new Vector2(x,y+h));p.ClosePath();if(fill)p.Fill();p.Stroke();}
            if(piano){
                Box(1,2,87,25,true);Line(2,5,87,5);
                for(int i=1;i<14;i++)Line(1+i*87f/14,5,1+i*87f/14,26);
                p.fillColor=new Color(.53f,.64f,.72f,.55f);p.strokeColor=Color.clear;
                for(int i=0;i<13;i++)if(i%7!=2&&i%7!=6)Box(1+(i+1)*87f/14-1.8f,5,3.6f,13,true);
            }else{
                p.BeginPath();p.MoveTo(new Vector2(4,3));p.QuadraticCurveTo(new Vector2(23,0),new Vector2(44,5));p.QuadraticCurveTo(new Vector2(65,0),new Vector2(84,3));p.LineTo(new Vector2(84,22));p.QuadraticCurveTo(new Vector2(64,19),new Vector2(44,24));p.QuadraticCurveTo(new Vector2(24,19),new Vector2(4,22));p.ClosePath();p.Fill();p.Stroke();Line(44,5,44,24);
                p.strokeColor=new Color(.62f,.72f,.79f,.34f);for(int i=0;i<5;i++){Line(10,7+i*2.3f,37,9+i*2.3f);Line(51,9+i*2.3f,78,7+i*2.3f);}
                p.strokeColor=ink;p.fillColor=ink;
                foreach(var n in new[]{new Vector2(18,13),new Vector2(29,17),new Vector2(59,15),new Vector2(70,10)}){p.BeginPath();p.Arc(n,1.5f,0,360);p.Fill();Line(n.x+1,n.y,n.x+1,n.y-6);}
                Line(44,25,44,31);Line(31,34,44,31);Line(44,31,57,34);
            }
        }
    }
    void DisposeVisuals(){for(int i=0;i<2;i++){labels[i]?.RemoveFromHierarchy();labels[i]=null;badges[i]?.RemoveFromHierarchy();badges[i]=null;if(shapes[i]!=null){shapes[i].gameObject.SetActive(false);Destroy(shapes[i].gameObject);}shapes[i]=null;}source=null;}
    void OnDestroy(){if(SplitActive&&master!=null){master.orthographic=previousOrthographic;master.orthographicSize=previousSize;master.clearFlags=previousClear;master.backgroundColor=previousBackground;master.ResetProjectionMatrix();if(backgroundCamera!=null)backgroundCamera.enabled=backgroundEnabled;}DisposeVisuals();}
}
