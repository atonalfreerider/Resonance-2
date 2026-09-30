using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;
using TrackedPoseDriver = UnityEngine.InputSystem.XR.TrackedPoseDriver;

// The headset session (Meta Quest, hands only). When an XR display is running (or the editor's
// simulated session is switched on), the scene is seen through a tracked rig whose scale sets how
// big the world is around the viewer.
//  Song mode   — the torus (about 1.65 m across) stands in front of the viewer at waist height,
//                the drum wheel under it, the lyric line above it, and the pattern wheels behind
//                it as a solid 3D object facing the viewer (PatternWheel3D).
//  Torus play  — the torus is about 2.2 m across and the viewer stands at its centre, the tube
//                around them at chest height, notes within reach all round (TorusTheremin).
// The desktop's flat panels are switched off entirely: they cost most of a frame and have nowhere
// to go. Hands drive everything: a menu on the left palm and a song list (VrMenu). The room shows
// through (passthrough) or is blacked out.
[DefaultExecutionOrder(1100)]
public sealed class VrSession : MonoBehaviour
{
    public static VrSession Instance {get;private set;}
    public static bool Active=>Instance!=null;
    // Scene units per metre: the rig is this much larger than the room. The torus is 4.4 units
    // across, so song mode shows it 25% smaller than torus play.
    public const float SongScale=2/.75f,PlayScale=2;
    public static float Scale {get;private set;}=SongScale;
    public enum Mode{Song,TorusPlay}
    public Mode Current {get;private set;}=Mode.Song;
    public bool Passthrough {get;private set;}
    public bool Simulated {get;private set;}
    public Camera Head {get;private set;}
    public Transform Rig {get;private set;}
    public HandInput Hands {get;private set;}
    public Main Main {get;private set;}
    public MidiPlayer Midi {get;private set;}
    public SongAudio Audio {get;private set;}
    public PatternWheel3D Wheels {get;private set;}
    public DrumLyricRack Lyrics=>lyrics;
    public GameObject LyricCard=>lyricQuad;
    public Vector3 TorusCenter=>Main!=null?Main.transform.position:Vector3.zero;
    // The torus's outer reach from its centre, in scene units (its rim).
    public float TorusRim {get;private set;}=2.2f;
    // The direction the viewer looks at the torus from (horizontal), the same side as the desktop's overview.
    public Vector3 ViewDirection {get;private set;}=new Vector3(-1,0,-1).normalized;
    // Where the floor is in the scene (the tracking space's floor), when known.
    public float FloorY=>Rig!=null?Rig.position.y:0;
    Camera desktop;VisualizationViews views;DrumLyricRack lyrics;SongLibraryPanel library;DrumPatternDeck drums;
    VrMenu menu;TorusTheremin theremin;ARCameraManager cameraManager;
    RenderTexture sink;GameObject lyricQuad;Camera[] cameras=new Camera[32];Material lyricMaterial;float sweep;
    bool floorOrigin,awaitingPose=true;
    readonly List<XRInputSubsystem> inputs=new();
    // Layout in metres. Song mode: the torus centre below the eyes (waist height) and the gap
    // between the viewer and its near rim; the wheels behind and above it; the lyric line above
    // it. Torus play: the torus centre below the eyes (chest height), the viewer at its centre.
    const float SongBelow=.92f,NearGap=.35f,WheelsBehind=.9f,WheelsClear=.3f,WheelRadius=.9f,LyricAbove=.42f,LyricWidth=1.3f,DrumBelow=.7f,
        PlayBelow=.42f;
    static readonly Vector2Int LyricPixels=new(1400,360);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        bool xr=XRGeneralSettings.Instance!=null&&XRGeneralSettings.Instance.Manager!=null&&XRGeneralSettings.Instance.Manager.activeLoader!=null;
        bool simulate=false;
#if UNITY_EDITOR
        simulate=UnityEditor.EditorPrefs.GetBool(SimulateKey,false);
#endif
        if(!xr&&!simulate)return;
        var go=new GameObject("VR session");var session=go.AddComponent<VrSession>();session.Simulated=!xr;
    }
    public const string SimulateKey="Resonance.SimulateQuest";

    IEnumerator Start()
    {
        Instance=this;Scale=SongScale;
        // The desktop UI and scene set themselves up in their own Start: wait for them.
        while((Main=FindAnyObjectByType<Main>())==null||Main.GetComponent<UIDocument>()==null||Main.GetComponent<VisualizationViews>()==null)yield return null;
        yield return null;yield return null;
        Midi=Main.GetComponent<MidiPlayer>();Audio=Main.GetComponent<SongAudio>();views=Main.GetComponent<VisualizationViews>();
        lyrics=Main.GetComponent<DrumLyricRack>();library=Main.GetComponent<SongLibraryPanel>();drums=Main.GetComponent<DrumPatternDeck>();
        DrumPatternDeck.Lite=true;   // the drum wheel is the lowest priority in the headset's frame
        // On the headset: the lighter pipeline (no HDR, 4x MSAA, alpha kept for passthrough).
        if(!Simulated){var pipeline=Resources.Load<RenderPipelineAsset>("QuestPipeline");if(pipeline!=null)QualitySettings.renderPipeline=pipeline;}
        BuildRig();SilenceFlatUi();BuildLyrics();
        Hands=gameObject.AddComponent<HandInput>();Hands.Rig=Rig;Hands.Head=Head.transform;Hands.Simulated=Simulated;
        Wheels=gameObject.AddComponent<PatternWheel3D>();Wheels.Init(Main,Hands);
        menu=gameObject.AddComponent<VrMenu>();theremin=gameObject.AddComponent<TorusTheremin>();
        gameObject.AddComponent<VrCommands>();
        views.SetView(VisualizationViews.View.Overview);
        ApplyScale();Recenter();SetPassthrough(false);
        Debug.Log("VR session: "+Diagnostics());
    }
    void BuildRig()
    {
        desktop=Camera.main;
        Rig=new GameObject("XR rig").transform;
        var go=new GameObject("Head");go.transform.SetParent(Rig,false);Head=go.AddComponent<Camera>();
        Head.clearFlags=CameraClearFlags.SolidColor;Head.backgroundColor=Color.black;
        if(Simulated)Head.fieldOfView=90;   // about the headset's own
        Head.cullingMask=(desktop!=null?desktop.cullingMask:~0)&~(1<<30)&~(1<<31);Head.allowHDR=Simulated;Head.allowMSAA=!Simulated;
        var data=Head.GetUniversalAdditionalCameraData();
        if(desktop!=null){var from=desktop.GetUniversalAdditionalCameraData();data.renderPostProcessing=from.renderPostProcessing;data.volumeLayerMask=from.volumeLayerMask;data.antialiasing=AntialiasingMode.None;}
        data.renderShadows=false;
        // The desktop camera steps aside: the head is now the main camera (labels billboard to it).
        if(desktop!=null){desktop.tag="Untagged";desktop.enabled=false;var orbit=desktop.GetComponent<CameraControl>();if(orbit!=null)orbit.enabled=false;}
        go.tag="MainCamera";
        if(!Simulated)
        {
            var driver=go.AddComponent<TrackedPoseDriver>();
            var position=new InputAction("Head position",binding:"<XRHMD>/centerEyePosition",expectedControlType:"Vector3");
            var rotation=new InputAction("Head rotation",binding:"<XRHMD>/centerEyeRotation",expectedControlType:"Quaternion");
            position.Enable();rotation.Enable();driver.positionInput=new InputActionProperty(position);driver.rotationInput=new InputActionProperty(rotation);
            // Passthrough (Meta OpenXR through AR Foundation): a composition layer under the eye
            // buffer shows wherever the eye buffer is transparent.
            var session=new GameObject("AR session").AddComponent<ARSession>();session.transform.SetParent(Rig,false);
            cameraManager=go.AddComponent<ARCameraManager>();cameraManager.enabled=false;
            // The floor as the tracking origin, so the floor is known and heights are real.
            SubsystemManager.GetSubsystems(inputs);
            foreach(var input in inputs)
            {
                floorOrigin|=input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                input.trackingOriginUpdated+=_=>awaitingPose=true;   // the user recentred in the system menu
            }
        }
    }
    // The desktop's flat panels (pattern wheel panel, instrument changers, menus, the orrery glow)
    // would cost most of a frame and could draw over the eye buffer: every panel renders into a
    // tiny texture nobody shows, with its content hidden. Panels made later are caught by the sweep.
    void SilenceFlatUi()
    {
        sink=new RenderTexture(4,4,0,RenderTextureFormat.ARGB32){name="Flat UI sink (headset)"};sink.Create();
        library?.Hide();
        SweepPanels();
    }
    // Each document gets its own copy of its panel settings, so the project's assets (and the
    // desktop app in the editor) are never changed.
    readonly HashSet<PanelSettings> copies=new();
    void SweepPanels()
    {
        foreach(var doc in Resources.FindObjectsOfTypeAll<UIDocument>())
        {
            if(doc==null||doc.panelSettings==null)continue;
#if UNITY_EDITOR
            if(UnityEditor.EditorUtility.IsPersistent(doc))continue;
#endif
            if(!copies.Contains(doc.panelSettings)){var copy=Instantiate(doc.panelSettings);copy.name=doc.panelSettings.name+" (headset)";copy.targetTexture=sink;copies.Add(copy);doc.panelSettings=copy;}
            var root=doc.rootVisualElement;if(root!=null&&root.style.display!=DisplayStyle.None)root.style.display=DisplayStyle.None;
        }
    }
    void BuildLyrics()
    {
        var quad=GameObject.CreatePrimitive(PrimitiveType.Quad);quad.name="Lyric line";Destroy(quad.GetComponent<Collider>());
        quad.transform.SetParent(transform,false);lyricMaterial=VrButton.Panel(Color.white);lyricMaterial.renderQueue=3170;quad.GetComponent<Renderer>().sharedMaterial=lyricMaterial;lyricQuad=quad;
        if(lyrics!=null){lyrics.Headless=true;lyrics.PixelSize=LyricPixels;}
    }

    // The rig's scale for the mode: everything sized in metres (menus, fingertips, handles)
    // follows it through Scale.
    void ApplyScale()
    {
        Scale=Current==Mode.TorusPlay?PlayScale:SongScale;
        Rig.localScale=Vector3.one*Scale;Head.nearClipPlane=.04f*Scale;Head.farClipPlane=80*Scale;
    }
    // Put the torus where it belongs relative to the viewer's head now. Song mode: in front at
    // waist height, seen from the overview's side, with the wheels, the drum wheel and the lyric
    // line around it. Torus play: the viewer at its centre, the tube at chest height.
    public void Recenter()
    {
        var centre=TorusCenter;var up=Vector3.up;
        float rim=0;for(int i=0;i<Main.NoteCount;i++){var d=Main.NotePosition(i)-centre;d.y=0;rim=Mathf.Max(rim,d.magnitude);}
        if(rim>0)TorusRim=rim;
        bool play=Current==Mode.TorusPlay;
        var eyes=play?centre+up*PlayBelow*Scale:centre-ViewDirection*(TorusRim+NearGap*Scale)+up*SongBelow*Scale;
        if(Simulated){Head.transform.position=eyes;Head.transform.rotation=Quaternion.LookRotation(play?ViewDirection-up*.6f:centre-eyes,up);}
        else
        {
            // Keep the viewer's own facing in torus play; in song mode turn the world so they face the torus.
            var local=Head.transform.localPosition;var forward=Head.transform.localRotation*Vector3.forward;forward.y=0;
            if(forward.sqrMagnitude<1e-4f)forward=Vector3.forward;
            float yaw=Vector3.SignedAngle(forward.normalized,ViewDirection,up);
            Rig.rotation=Quaternion.AngleAxis(yaw,up);Rig.position=eyes-Rig.rotation*(local*Scale);
        }
        // The wheels behind the torus, their lowest edge just above its far rim so nothing hides
        // them, and shifted left so the ring, its rack and the stacks beside it sit centred.
        var right=Vector3.Cross(up,ViewDirection).normalized;
        Wheels.Place(centre+ViewDirection*(TorusRim+WheelsBehind*Scale)+up*(WheelsClear+WheelRadius)*Scale-right*.6f*WheelRadius*Scale,Quaternion.LookRotation(ViewDirection,up),WheelRadius*Scale);
        PlaceDrums();
        lyricQuad.transform.position=centre+up*LyricAbove*Scale;
        lyricQuad.transform.localScale=new Vector3(LyricWidth,LyricWidth*LyricPixels.y/LyricPixels.x,1)*Scale;
        menu?.Recentered();
        Debug.Log($"VR recenter: {Current} scale {Scale:0.00} torus {TorusRim*2/Scale:0.00} m across, centre {(centre.y-FloorY)/Scale:0.00} m above the floor (floor origin {floorOrigin})");
    }
    // The drum wheel under the torus, above the floor.
    void PlaceDrums()
    {
        if(drums==null||drums.WheelTransform==null)return;
        float above=(TorusCenter.y-FloorY)/Scale,below=DrumBelow;
        if(!Simulated&&floorOrigin)below=Mathf.Clamp(above-.18f,.3f,DrumBelow);
        // The deck places itself each frame from DeckDepth (in the torus's own units).
        DrumPatternDeck.DeckDepth=below*Scale/Mathf.Max(1e-4f,Main.transform.lossyScale.y);
    }
    public void SetPassthrough(bool on)
    {
        Passthrough=on;
        // Transparent where nothing is drawn lets the room through; opaque black blacks it out.
        Head.clearFlags=CameraClearFlags.SolidColor;Head.backgroundColor=on?new Color(0,0,0,0):Color.black;
        if(cameraManager!=null)cameraManager.enabled=on;
        if(on)StartCoroutine(LogPassthrough());
    }
    IEnumerator LogPassthrough(){yield return new WaitForSecondsRealtime(1);Debug.Log("VR passthrough on: "+Diagnostics());}
    public void SetMode(Mode mode)
    {
        if(mode==Current)return;Current=mode;
        if(mode==Mode.TorusPlay){Midi.Pause();views.SetView(VisualizationViews.View.Torus);}
        else{Main.Silence();views.SetView(VisualizationViews.View.Overview);}
        ApplyScale();Recenter();
    }
    // What decides whether the room can show through and whether the lyric line shows: the
    // pipeline, the eye camera, the AR session and passthrough layer, and anything else drawing
    // to the screen.
    public string Diagnostics()
    {
        var s=new StringBuilder();
        var pipeline=GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        s.Append($"pipeline {(pipeline!=null?pipeline.name:"none")} hdr {(pipeline!=null&&pipeline.supportsHDR)} alphaOut {(pipeline!=null&&pipeline.allowPostProcessAlphaOutput)} msaa {(pipeline!=null?pipeline.msaaSampleCount:0)}");
        s.Append($"; head clear {Head.clearFlags} {Head.backgroundColor} hdr {Head.allowHDR} post {Head.GetUniversalAdditionalCameraData().renderPostProcessing}");
        s.Append($"; ar camera {(cameraManager!=null&&cameraManager.enabled)} subsystem {(cameraManager!=null&&cameraManager.subsystem!=null&&cameraManager.subsystem.running)} session {ARSession.state}");
        s.Append($"; composition layer provider started {UnityEngine.XR.OpenXR.CompositionLayers.OpenXRLayerProvider.isStarted}");
        s.Append($"; lyrics shown {(lyrics!=null&&lyrics.Shown)} output {(lyrics?.Output!=null?lyrics.Output.width+"x"+lyrics.Output.height:"none")} presence {(lyrics!=null?lyrics.Presence:0):0.00} card {(lyricQuad!=null&&lyricQuad.activeSelf)}");
        s.Append($"; floor origin {floorOrigin} head {Head.transform.localPosition}");
        foreach(var c in Camera.allCameras)if(c!=Head)s.Append($"; camera {c.name} → {(c.targetTexture!=null?c.targetTexture.name:"screen")}");
        foreach(var d in Resources.FindObjectsOfTypeAll<UIDocument>())if(d!=null&&d.panelSettings!=null&&d.panelSettings.targetTexture==null&&d.isActiveAndEnabled)s.Append($"; screen panel {d.name}");
        return s.ToString();
    }

    void LateUpdate()
    {
        if(Head==null)return;
        // The first valid head pose (and any system recentre) puts the torus where it belongs:
        // before tracking starts the head reads as the floor.
        if(awaitingPose&&(Simulated||Head.transform.localPosition.sqrMagnitude>.01f)){awaitingPose=false;Recenter();}
        // Only the eye camera and the lyric strip's own camera render (the views would otherwise
        // keep driving the desktop's), and panels made since the last sweep are silenced.
        if(Camera.allCamerasCount>cameras.Length)cameras=new Camera[Camera.allCamerasCount*2];
        int count=Camera.GetAllCameras(cameras);
        for(int i=0;i<count;i++){var c=cameras[i];if(c!=Head&&c.enabled&&!(c.targetTexture!=null&&c.targetTexture.name.StartsWith("Lyric strip")))c.enabled=false;}
        if((sweep-=Time.unscaledDeltaTime)<=0){sweep=1;SweepPanels();}
        bool song=Current==Mode.Song;
        Wheels.Visible=song&&Midi.Loaded;
        // Torus play is the torus alone: the drum wheel would stand at the viewer's feet.
        DrumPatternDeck.Hidden=!song;
        // The lyric line above the torus, from the strip's own transparent texture.
        if(lyrics!=null)
        {
            lyrics.Wanted=song;lyrics.Viewport=new Rect(0,0,.5f,.25f);
            lyricMaterial.mainTexture=lyrics.Output;bool show=song&&lyrics.Shown&&lyrics.Output!=null;if(lyricQuad.activeSelf!=show)lyricQuad.SetActive(show);
            if(show){var toHead=Head.transform.position-lyricQuad.transform.position;toHead.y=0;if(toHead.sqrMagnitude>1e-4f)lyricQuad.transform.rotation=Quaternion.LookRotation(-toHead,Vector3.up);}
        }
    }
    void OnDestroy()
    {
        if(Instance==this)Instance=null;
        DrumPatternDeck.Hidden=DrumPatternDeck.Lite=false;DrumPatternDeck.DeckDepth=4.3f;
        foreach(var copy in copies)if(copy!=null)Destroy(copy);
        if(sink!=null){sink.Release();Destroy(sink);}
        if(lyricMaterial!=null)Destroy(lyricMaterial);
    }
}
