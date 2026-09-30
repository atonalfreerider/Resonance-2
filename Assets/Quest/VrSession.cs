using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;
using TrackedPoseDriver = UnityEngine.InputSystem.XR.TrackedPoseDriver;

// The headset session (Meta Quest, hands only). When an XR display is running (or the editor's
// simulated session is switched on), the scene is seen through a tracked rig scaled so the torus
// is about 75 cm across: the torus floats in front of the viewer a little below the eyes, the drum
// wheel under it, the lyric line above it, and the pattern wheels as a large backdrop behind,
// facing the viewer (the flat UI is rendered into a texture for it; the lyric graph and the side
// menus are not shown). Hands drive everything: a menu on the left palm, a song list, and a
// torus-play mode (TorusTheremin). The room shows through (passthrough) or is blacked out.
[DefaultExecutionOrder(1100)]
public sealed class VrSession : MonoBehaviour
{
    public static VrSession Instance {get;private set;}
    public static bool Active=>Instance!=null;
    // Scene units per metre: the rig is this much larger than the room.
    public const float Scale=6;
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
    public Vector3 TorusCenter=>Main!=null?Main.transform.position:Vector3.zero;
    // The direction the viewer looks at the torus from (horizontal), the same side as the desktop's overview.
    public Vector3 ViewDirection {get;private set;}=new Vector3(-1,0,-1).normalized;
    Camera desktop,clearCamera;VisualizationViews views;DrumLyricRack lyrics;SongLibraryPanel library;PatternWheelDeck deck;
    VrMenu menu;TorusTheremin theremin;ARCameraManager cameraManager;
    RenderTexture wheels;GameObject backdrop,backdropShade,lyricQuad;Material backdropMaterial,shadeMaterial,lyricMaterial;VisualElement uiRoot;
    // Layout, metres from the viewer's eyes (forward along the view direction, up).
    const float TorusForward=.85f,TorusBelow=.45f,BackdropBehind=1.7f,BackdropAbove=.55f,BackdropWidth=2.6f,LyricAbove=.34f,LyricWidth=1f;
    static readonly Vector2Int WheelPixels=new(1600,1000),LyricPixels=new(1400,360);

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
        lyrics=Main.GetComponent<DrumLyricRack>();library=Main.GetComponent<SongLibraryPanel>();
        DrumPatternDeck.DeckDepth=3.2f;
        // On the headset: the lighter pipeline (no HDR, 4x MSAA, alpha kept for passthrough).
        if(!Simulated){var pipeline=Resources.Load<UnityEngine.Rendering.RenderPipelineAsset>("QuestPipeline");if(pipeline!=null)QualitySettings.renderPipeline=pipeline;}
        BuildRig();BuildBackdrop();BuildLyrics();
        Hands=gameObject.AddComponent<HandInput>();Hands.Rig=Rig;Hands.Head=Head.transform;Hands.Scale=Scale;Hands.Simulated=Simulated;
        menu=gameObject.AddComponent<VrMenu>();theremin=gameObject.AddComponent<TorusTheremin>();
        views.SetView(VisualizationViews.View.Overview);
        Recenter();SetPassthrough(false);
    }
    void BuildRig()
    {
        desktop=Camera.main;
        Rig=new GameObject("XR rig · 1 m = "+Scale+" units").transform;Rig.localScale=Vector3.one*Scale;
        var go=new GameObject("Head");go.transform.SetParent(Rig,false);Head=go.AddComponent<Camera>();
        Head.clearFlags=CameraClearFlags.SolidColor;Head.backgroundColor=Color.black;Head.nearClipPlane=.04f*Scale;if(Simulated)Head.fieldOfView=90;   // about the headset's own
        Head.farClipPlane=80*Scale;
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
    // The pattern wheels, rendered by the flat UI into a texture, on a large card behind the torus.
    void BuildBackdrop()
    {
        wheels=new RenderTexture(WheelPixels.x,WheelPixels.y,24,RenderTextureFormat.ARGB32){name="Pattern wheels (headset backdrop)"};wheels.Create();
        foreach(var doc in Resources.FindObjectsOfTypeAll<UIDocument>())
        {
            if(doc==null||doc.panelSettings==null||doc.gameObject.name.StartsWith("Lyric strip"))continue;
            doc.panelSettings.targetTexture=wheels;
        }
        var main=Main.GetComponent<UIDocument>().panelSettings;main.clearColor=true;main.colorClearValue=Color.clear;
        uiRoot=Main.GetComponent<UIDocument>().rootVisualElement;
        foreach(var name in new[]{"controls","visualization-views","tuck-side-menu","tuck-song-library","song-library","rack-play-pause"}){var e=uiRoot.Q(name);if(e!=null)e.style.display=DisplayStyle.None;}
        library?.Hide();
        deck=uiRoot.Q<PatternWheelDeck>();
        backdropShade=Card("Pattern wheels · shade",shadeMaterial=VrButton.Panel(new Color(0,0,0,.55f)));
        backdrop=Card("Pattern wheels · backdrop",backdropMaterial=VrButton.Panel(Color.white,true));backdropMaterial.mainTexture=wheels;backdropMaterial.renderQueue=3160;
    }
    void BuildLyrics()
    {
        lyricQuad=Card("Lyric line",lyricMaterial=VrButton.Panel(Color.white));lyricMaterial.renderQueue=3170;
        if(lyrics!=null){lyrics.Headless=true;lyrics.PixelSize=LyricPixels;}
    }
    GameObject Card(string name,Material material)
    {
        var quad=GameObject.CreatePrimitive(PrimitiveType.Quad);quad.name=name;Destroy(quad.GetComponent<Collider>());
        quad.transform.SetParent(transform,false);quad.GetComponent<Renderer>().sharedMaterial=material;return quad;
    }

    // Put the torus where it belongs relative to the viewer's head now: in front and a little
    // below, seen from the overview's side, with the backdrop and the lyric line placed around it.
    public void Recenter()
    {
        var centre=TorusCenter;var up=Vector3.up;
        var eyes=centre-ViewDirection*TorusForward*Scale+up*TorusBelow*Scale;
        if(Simulated){Head.transform.position=eyes;Head.transform.rotation=Quaternion.LookRotation(centre-eyes,up);}
        else
        {
            var local=Head.transform.localPosition;var forward=Head.transform.localRotation*Vector3.forward;forward.y=0;
            if(forward.sqrMagnitude<1e-4f)forward=Vector3.forward;
            float yaw=Vector3.SignedAngle(forward.normalized,ViewDirection,up);
            Rig.rotation=Quaternion.AngleAxis(yaw,up);Rig.position=eyes-Rig.rotation*(local*Scale);
        }
        var back=centre+ViewDirection*BackdropBehind*Scale+up*BackdropAbove*Scale;
        float h=BackdropWidth*WheelPixels.y/WheelPixels.x;
        Place(backdrop,back,new Vector2(BackdropWidth,h));Place(backdropShade,back+ViewDirection*.01f*Scale,new Vector2(BackdropWidth*1.03f,h*1.05f));
        Place(lyricQuad,centre+up*LyricAbove*Scale,new Vector2(LyricWidth,LyricWidth*LyricPixels.y/LyricPixels.x));
        menu?.Recentered();
    }
    void Place(GameObject card,Vector3 at,Vector2 metres)
    {
        card.transform.position=at;card.transform.rotation=Quaternion.LookRotation(ViewDirection,Vector3.up);card.transform.localScale=new Vector3(metres.x,metres.y,1)*Scale;
    }
    public void SetPassthrough(bool on)
    {
        Passthrough=on;
        // Transparent where nothing is drawn lets the room through; opaque black blacks it out.
        Head.backgroundColor=on?new Color(0,0,0,0):Color.black;
        if(cameraManager!=null)cameraManager.enabled=on;
    }
    public void SetMode(Mode mode)
    {
        if(mode==Current)return;Current=mode;
        if(mode==Mode.TorusPlay){Midi.Pause();views.SetView(VisualizationViews.View.Torus);}
        else{Main.Silence();views.SetView(VisualizationViews.View.Overview);}
    }

    void LateUpdate()
    {
        if(Head==null)return;
        // The desktop's own cameras stay off (the views would otherwise keep driving them).
        if(desktop!=null&&desktop.enabled)desktop.enabled=false;
        if(clearCamera==null){var go=GameObject.Find("Screen clear");if(go!=null)clearCamera=go.GetComponent<Camera>();}
        if(clearCamera!=null&&clearCamera.enabled)clearCamera.enabled=false;
        bool song=Current==Mode.Song;
        // The wheels fill their texture (the views laid them out for a desktop window).
        if(deck!=null&&song)
        {
            var overlay=deck.Overlay;float w=uiRoot.resolvedStyle.width,h=uiRoot.resolvedStyle.height;
            if(float.IsFinite(w)&&w>1){overlay.style.left=12;overlay.style.top=12;overlay.style.width=w-24;overlay.style.height=h-24;}
        }
        backdrop.SetActive(song&&Midi.Loaded);backdropShade.SetActive(song&&Midi.Loaded);
        // The lyric line above the torus, from the strip's own transparent texture.
        if(lyrics!=null)
        {
            lyrics.Wanted=song;lyrics.Viewport=new Rect(0,0,.5f,.25f);
            lyricMaterial.mainTexture=lyrics.Output;lyricQuad.SetActive(song&&lyrics.Shown&&lyrics.Output!=null);
            if(lyricQuad.activeSelf){var toHead=Head.transform.position-lyricQuad.transform.position;toHead.y=0;if(toHead.sqrMagnitude>1e-4f)lyricQuad.transform.rotation=Quaternion.LookRotation(-toHead,Vector3.up);}
        }
        Main.BillboardLabels();
    }
    void OnDestroy()
    {
        if(Instance==this)Instance=null;
        if(wheels!=null){wheels.Release();Destroy(wheels);}
        foreach(var m in new[]{backdropMaterial,shadeMaterial,lyricMaterial})if(m!=null)Destroy(m);
    }
}
