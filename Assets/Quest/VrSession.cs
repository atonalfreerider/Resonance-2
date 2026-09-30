using System.Collections;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;
using TrackedPoseDriver = UnityEngine.InputSystem.XR.TrackedPoseDriver;

// The headset session (Meta Quest, hands only). When an XR display is running (or the editor's
// simulated session is switched on), the scene is seen through a tracked rig scaled so the torus
// is about 2.2 m across: it stands in front of the viewer at chest height, the drum wheel under
// it, the lyric line above it, and the pattern wheels behind it as a solid 3D object facing the
// viewer (PatternWheel3D). The desktop's flat panels are switched off entirely: they cost most of
// a frame and have nowhere to go. Hands drive everything: a menu on the left palm, a song list,
// and a torus-play mode (TorusTheremin). The room shows through (passthrough) or is blacked out.
[DefaultExecutionOrder(1100)]
public sealed class VrSession : MonoBehaviour
{
    public static VrSession Instance {get;private set;}
    public static bool Active=>Instance!=null;
    // Scene units per metre: the rig is this much larger than the room.
    public const float Scale=2;
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
    public Vector3 TorusCenter=>Main!=null?Main.transform.position:Vector3.zero;
    // The torus's outer reach from its centre, in scene units (its rim).
    public float TorusRim {get;private set;}=2.2f;
    // The direction the viewer looks at the torus from (horizontal), the same side as the desktop's overview.
    public Vector3 ViewDirection {get;private set;}=new Vector3(-1,0,-1).normalized;
    Camera desktop;VisualizationViews views;DrumLyricRack lyrics;SongLibraryPanel library;DrumPatternDeck drums;
    VrMenu menu;TorusTheremin theremin;ARCameraManager cameraManager;
    RenderTexture sink;GameObject lyricQuad;Camera[] cameras=new Camera[32];Material lyricMaterial;float sweep;
    // Layout in metres: the torus centre below the eyes (chest height) and the gap between the
    // viewer and its near rim; the wheels behind it; the lyric line above it.
    const float ChestBelow=.42f,NearGap=.35f,WheelsBehind=.9f,WheelsClear=.35f,WheelRadius=1f,LyricAbove=.52f,LyricWidth=1.5f,DrumBelow=.95f;
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
        Instance=this;
        // The desktop UI and scene set themselves up in their own Start: wait for them.
        while((Main=FindAnyObjectByType<Main>())==null||Main.GetComponent<UIDocument>()==null||Main.GetComponent<VisualizationViews>()==null)yield return null;
        yield return null;yield return null;
        Midi=Main.GetComponent<MidiPlayer>();Audio=Main.GetComponent<SongAudio>();views=Main.GetComponent<VisualizationViews>();
        lyrics=Main.GetComponent<DrumLyricRack>();library=Main.GetComponent<SongLibraryPanel>();drums=Main.GetComponent<DrumPatternDeck>();
        // On the headset: the lighter pipeline (no HDR, 4x MSAA, alpha kept for passthrough).
        if(!Simulated){var pipeline=Resources.Load<RenderPipelineAsset>("QuestPipeline");if(pipeline!=null)QualitySettings.renderPipeline=pipeline;}
        BuildRig();SilenceFlatUi();BuildLyrics();
        Hands=gameObject.AddComponent<HandInput>();Hands.Rig=Rig;Hands.Head=Head.transform;Hands.Scale=Scale;Hands.Simulated=Simulated;
        Wheels=gameObject.AddComponent<PatternWheel3D>();Wheels.Init(Main,Hands);
        menu=gameObject.AddComponent<VrMenu>();theremin=gameObject.AddComponent<TorusTheremin>();
        views.SetView(VisualizationViews.View.Overview);
        Recenter();SetPassthrough(false);
        Debug.Log("VR session: "+Diagnostics());
    }
    void BuildRig()
    {
        desktop=Camera.main;
        Rig=new GameObject("XR rig · 1 m = "+Scale+" units").transform;Rig.localScale=Vector3.one*Scale;
        var go=new GameObject("Head");go.transform.SetParent(Rig,false);Head=go.AddComponent<Camera>();
        Head.clearFlags=CameraClearFlags.SolidColor;Head.backgroundColor=Color.black;Head.nearClipPlane=.04f*Scale;Head.farClipPlane=80*Scale;
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
            // Passthrough (Meta OpenXR through AR Foundation): the camera feed shows wherever the eye buffer is transparent.
            var session=new GameObject("AR session").AddComponent<ARSession>();session.transform.SetParent(Rig,false);
            cameraManager=go.AddComponent<ARCameraManager>();cameraManager.enabled=false;
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
    readonly System.Collections.Generic.HashSet<PanelSettings> copies=new();
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

    // Put the torus where it belongs relative to the viewer's head now: in front at chest height,
    // seen from the overview's side, with the wheels, the drum wheel and the lyric line around it.
    public void Recenter()
    {
        var centre=TorusCenter;var up=Vector3.up;
        float rim=0;for(int i=0;i<Main.NoteCount;i++){var d=Main.NotePosition(i)-centre;d.y=0;rim=Mathf.Max(rim,d.magnitude);}
        if(rim>0)TorusRim=rim;
        var eyes=centre-ViewDirection*(TorusRim+NearGap*Scale)+up*ChestBelow*Scale;
        if(Simulated){Head.transform.position=eyes;Head.transform.rotation=Quaternion.LookRotation(centre-eyes,up);}
        else
        {
            var local=Head.transform.localPosition;var forward=Head.transform.localRotation*Vector3.forward;forward.y=0;
            if(forward.sqrMagnitude<1e-4f)forward=Vector3.forward;
            float yaw=Vector3.SignedAngle(forward.normalized,ViewDirection,up);
            Rig.rotation=Quaternion.AngleAxis(yaw,up);Rig.position=eyes-Rig.rotation*(local*Scale);
        }
        // The wheels behind the torus, their lowest edge just above its far rim so nothing hides
        // them, and shifted left so the ring, its rack and the stacks beside it sit centred.
        var right=Vector3.Cross(up,ViewDirection).normalized;
        Wheels.Place(centre+ViewDirection*(TorusRim+WheelsBehind*Scale)+up*(WheelsClear+WheelRadius)*Scale-right*.6f*WheelRadius*Scale,Quaternion.LookRotation(ViewDirection,up),WheelRadius*Scale);
        if(drums!=null&&drums.WheelTransform!=null)drums.WheelTransform.localPosition=new Vector3(0,-DrumBelow*Scale/Mathf.Max(1e-4f,Main.transform.lossyScale.y),0);
        lyricQuad.transform.position=centre+up*LyricAbove*Scale;
        lyricQuad.transform.localScale=new Vector3(LyricWidth,LyricWidth*LyricPixels.y/LyricPixels.x,1)*Scale;
        menu?.Recentered();
    }
    public void SetPassthrough(bool on)
    {
        Passthrough=on;
        // Transparent where nothing is drawn lets the room through; opaque black blacks it out.
        Head.clearFlags=CameraClearFlags.SolidColor;Head.backgroundColor=on?new Color(0,0,0,0):Color.black;
        if(cameraManager!=null)cameraManager.enabled=on;
        if(on)Debug.Log("VR passthrough on: "+Diagnostics());
    }
    public void SetMode(Mode mode)
    {
        if(mode==Current)return;Current=mode;
        if(mode==Mode.TorusPlay){Midi.Pause();views.SetView(VisualizationViews.View.Torus);}
        else{Main.Silence();views.SetView(VisualizationViews.View.Overview);}
    }
    // What decides whether the room can show through: the pipeline, the eye camera's clear and
    // formats, and anything else that draws to the screen.
    string Diagnostics()
    {
        var s=new StringBuilder();
        var pipeline=GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        s.Append($"pipeline {(pipeline!=null?pipeline.name:"none")} hdr {(pipeline!=null&&pipeline.supportsHDR)} alphaOut {(pipeline!=null&&pipeline.allowPostProcessAlphaOutput)} msaa {(pipeline!=null?pipeline.msaaSampleCount:0)}");
        s.Append($"; head clear {Head.clearFlags} {Head.backgroundColor} hdr {Head.allowHDR} post {Head.GetUniversalAdditionalCameraData().renderPostProcessing}");
        s.Append($"; ar camera {(cameraManager!=null&&cameraManager.enabled)} subsystem {(cameraManager!=null&&cameraManager.subsystem!=null&&cameraManager.subsystem.running)} session {ARSession.state}");
        foreach(var c in Camera.allCameras)if(c!=Head&&c.targetTexture==null)s.Append($"; screen camera {c.name}");
        foreach(var d in Resources.FindObjectsOfTypeAll<UIDocument>())if(d!=null&&d.panelSettings!=null&&d.panelSettings.targetTexture==null&&d.isActiveAndEnabled)s.Append($"; screen panel {d.name}");
        return s.ToString();
    }

    void LateUpdate()
    {
        if(Head==null)return;
        // Only the eye camera and the lyric strip's own camera render (the views would otherwise keep driving the
        // desktop's), and panels made since the last sweep are silenced.
        if(Camera.allCamerasCount>cameras.Length)cameras=new Camera[Camera.allCamerasCount*2];
        int count=Camera.GetAllCameras(cameras);
        for(int i=0;i<count;i++){var c=cameras[i];if(c!=Head&&c.enabled&&!(c.targetTexture!=null&&c.targetTexture.name.StartsWith("Lyric strip")))c.enabled=false;}
        if((sweep-=Time.unscaledDeltaTime)<=0){sweep=1;SweepPanels();}
        bool song=Current==Mode.Song;
        Wheels.Visible=song&&Midi.Loaded;
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
        foreach(var copy in copies)if(copy!=null)Destroy(copy);
        if(sink!=null){sink.Release();Destroy(sink);}
        if(lyricMaterial!=null)Destroy(lyricMaterial);
    }
}
