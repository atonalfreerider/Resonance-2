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
    Main[] shapes=new Main[2];Rect[] bounds=new Rect[2];Label[] labels=new Label[2];
    PreparedPatternSong source;int frameIndex;double last=-1;bool lastAudible;
    readonly List<Renderer> ownerRenderers=new();int ownerCount=-1;
    bool previousOrthographic;float previousSize;CameraClearFlags previousClear;Color previousBackground;
    Camera backgroundCamera;bool backgroundEnabled;
    public bool SplitActive {get;private set;}
    public Rect PianoViewport=>bounds[0];
    public Rect OrchestraViewport=>bounds[1];
    public Main Piano=>shapes[0];public Main Orchestra=>shapes[1];
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
            if(ownerCount!=owner.transform.hierarchyCount){owner.GetComponentsInChildren(true,ownerRenderers);ownerCount=owner.transform.hierarchyCount;}
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
        double now=midi.VisualScorePosition;bool seek=last<0||now<last||Math.Abs(now-last)>.5;
        int lo=0,hi=source.Frames.Length;while(lo<hi){int mid=(lo+hi)/2;if(source.Frames[mid].Time<=now)lo=mid+1;else hi=mid;}
        var frame=lo>0?source.Frames[lo-1]:null;
        var viewport=views.PanelHidden?new Rect(0,0,1,1):new Rect(views.SceneRect.x/Screen.width,1-views.SceneRect.yMax/Screen.height,views.SceneRect.width/Screen.width,views.SceneRect.height/Screen.height);bounds=Split(viewport,views.Vertical||Screen.height>Screen.width);
        for(int i=0;i<2;i++){
            var shape=shapes[i];var tracks=i==0?midi.HarmonicPrepared.PianoTracks:midi.HarmonicPrepared.OrchestraTracks;
            shape.UseSharedSynth(owner.Synth);shape.MinorMode=owner.MinorMode;shape.UseFlats=owner.UseFlats;
            shape.ShowSurfaces=owner.ShowSurfaces;shape.ShowDiagonals=owner.ShowDiagonals;shape.ShowStructure=owner.ShowStructure;shape.ShowRegisters=owner.ShowRegisters;
            shape.SoundingOnly=owner.SoundingOnly;shape.DiatonicStrip=owner.DiatonicStrip;shape.ShowHarmonics=owner.ShowHarmonics;shape.FieldDensity=owner.FieldDensity;
            shape.ResonanceHalfLife=owner.ResonanceHalfLife;shape.ResonanceGain=owner.ResonanceGain;shape.VisualReleaseSeconds=owner.VisualReleaseSeconds;shape.NoteReleaseSeconds=owner.NoteReleaseSeconds;
            float depth=Mathf.Max(5,Vector3.Distance(master.transform.position,owner.transform.position));
            var position=master.ViewportToWorldPoint(new Vector3(bounds[i].center.x,bounds[i].center.y,depth));
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
        }
        frameIndex=lo;last=now;lastAudible=midi.IsAudible;
    }
    void DisposeVisuals(){for(int i=0;i<2;i++){labels[i]?.RemoveFromHierarchy();labels[i]=null;if(shapes[i]!=null){shapes[i].gameObject.SetActive(false);Destroy(shapes[i].gameObject);}shapes[i]=null;}source=null;}
    void OnDestroy(){if(SplitActive&&master!=null){master.orthographic=previousOrthographic;master.orthographicSize=previousSize;master.clearFlags=previousClear;master.backgroundColor=previousBackground;master.ResetProjectionMatrix();if(backgroundCamera!=null)backgroundCamera.enabled=backgroundEnabled;}DisposeVisuals();}
}
