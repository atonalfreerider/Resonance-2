using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

// The views, and the screen's layout. In the overview the pattern wheels, the lyric strip and
// the torus sit side by side in a landscape window (wheels, then lyrics, then the torus), and
// stacked in a portrait one (torus above, lyrics between, wheels below, the drum wheel deep in
// the scene behind). Every camera clears to the one background, so no part ends at a hard
// edge; the strip's own camera draws the lyrics in its part. The side
// menu at the left and the song list at the right each tuck away behind their own button; the
// side menu starts tucked.
[DefaultExecutionOrder(1000)]
public sealed class VisualizationViews : MonoBehaviour
{
    public enum View { Overview,Torus,Timeline,Drums,Lyrics }
    public View Current {get;private set;}
    public bool PanelHidden {get;private set;}
    public float TorusOpacity {get;private set;}=1;
    // A cap on the torus's opacity (the tutorial lowers it to see through the band).
    public float TorusOpacityCap=1;
    // Force the stacked (portrait) layout in any window.
    public bool Vertical {get;private set;}
    Toggle vertical;
    public float DrumOpacity {get;private set;}=1;
    // Where the lyric strip sits (pixels, from the top left), for validation.
    public Rect LyricStrip {get;private set;}
    public Rect SceneRect {get;private set;}
    // Screen space kept clear of the 3D scene above and below (panel pixels): the tutorial's
    // diagram and caption in a portrait window, so the torus is framed between them.
    public float SceneTopInset,SceneBottomInset;
    // How much closer the Torus view's camera sits (the tutorial doubles the torus in a portrait window).
    public float TorusZoom=1;
    VisualElement root,panel,overlay,toolbar;Button tuck;
    DropdownField viewChoice;
    Toggle uncoil;
    readonly Dictionary<Renderer,float> appliedOpacity=new();
    MaterialPropertyBlock block;
    readonly List<Renderer> renderers=new();
    readonly Dictionary<Material,Shader> replacedShaders=new();
    Camera camera,clear;CameraControl orbit;DrumPatternDeck drums;DrumLyricRack lyrics;SongLibraryPanel library;
    Vector3 overviewPosition;Quaternion overviewRotation;Vector3 overviewAngles;
    float panelOpen,timelineOpacity=1,timelineFocus,overviewSplit=1;
    float shownTorus=-1,shownDrums=-1,nextWalk;int shownCount=-1;bool wasUncoiling;
    bool cameraMoving;VisualElement vowelWheel;PatternWheelDeck wheelsDeck;Rect placedWheel=new(0,0,-1,-1);
    void Awake(){block=new MaterialPropertyBlock();}
    public void Bind(VisualElement ui,VisualElement controls,PatternWheelDeck wheels)
    {
        root=ui;panel=controls;overlay=wheels.Overlay;wheelsDeck=wheels;vowelWheel=wheels.Graph.WheelElement;root.Add(vowelWheel);camera=Camera.main;orbit=camera.GetComponent<CameraControl>();drums=GetComponent<DrumPatternDeck>();lyrics=GetComponent<DrumLyricRack>();library=GetComponent<SongLibraryPanel>();
        // The scene camera renders only its part of the screen and clears no colour; this one
        // clears the whole screen first, through the same post-processing, so the scene and the
        // wheels share one background with no edge between them.
        var clearing=new GameObject("Screen clear"){hideFlags=HideFlags.DontSave};clear=clearing.AddComponent<Camera>();
        clear.cullingMask=0;clear.clearFlags=CameraClearFlags.SolidColor;clear.backgroundColor=camera.backgroundColor;clear.depth=camera.depth-10;clear.rect=new Rect(0,0,1,1);
        var clearData=clear.GetUniversalAdditionalCameraData();clearData.renderPostProcessing=camera.GetUniversalAdditionalCameraData().renderPostProcessing;clearData.renderShadows=false;
        camera.clearFlags=CameraClearFlags.Depth;
        toolbar=new VisualElement{name="visualization-views"};toolbar.style.position=Position.Absolute;toolbar.style.top=12;toolbar.style.right=16;
        toolbar.style.flexDirection=FlexDirection.Row;toolbar.style.alignItems=Align.Center;toolbar.style.backgroundColor=new Color(.055f,.09f,.14f,.92f);
        toolbar.style.borderTopLeftRadius=18;toolbar.style.borderTopRightRadius=18;toolbar.style.borderBottomLeftRadius=18;toolbar.style.borderBottomRightRadius=18;
        toolbar.style.paddingLeft=16;toolbar.style.paddingRight=8;root.Add(toolbar);
        var label=new Label("VIEW");label.style.color=new Color(.43f,.63f,.78f);label.style.fontSize=10;label.style.letterSpacing=2;toolbar.Add(label);
        viewChoice=new DropdownField(new List<string>{"Overview","Torus","Pattern timeline","Drum wheel","Lyrics"},0){name="view-selector",tooltip="Choose the featured visualization"};
        viewChoice.style.width=168;viewChoice.style.minHeight=32;viewChoice.style.height=32;viewChoice.style.marginLeft=8;viewChoice.style.marginTop=3;viewChoice.style.marginBottom=3;
        var input=viewChoice.Q(className:"unity-base-field__input");if(input!=null){input.style.backgroundColor=Color.clear;input.style.borderTopWidth=input.style.borderBottomWidth=input.style.borderLeftWidth=input.style.borderRightWidth=0;input.style.color=new Color(.9f,.95f,1);}
        viewChoice.RegisterValueChangedCallback(_=>SetView((View)viewChoice.index));toolbar.Add(viewChoice);
        uncoil=new Toggle("Uncoil"){name="uncoil-torus",tooltip="Open the torus into concentric octave arcs"};
        uncoil.style.marginLeft=12;uncoil.style.marginRight=12;uncoil.style.color=new Color(.9f,.95f,1);
        uncoil.RegisterValueChangedCallback(e=>GetComponent<Main>().SetUncoiled(e.newValue));toolbar.Add(uncoil);
        vertical=new Toggle("Vertical"){name="vertical-layout",tooltip="Stack the torus, the lyrics and the pattern wheels top to bottom"};
        vertical.style.marginLeft=4;vertical.style.marginRight=12;vertical.style.color=new Color(.9f,.95f,1);
        vertical.RegisterValueChangedCallback(e=>{Vertical=e.newValue;ExplorerInputFocus.ClaimViewport();});toolbar.Add(vertical);
        tuck=new Button(()=>SetPanelHidden(!PanelHidden)){text="‹",name="tuck-side-menu",tooltip="Tuck away side menu"};root.Add(tuck);
        tuck.style.position=Position.Absolute;tuck.style.width=25;tuck.style.height=64;tuck.style.fontSize=27;
        tuck.style.marginLeft=tuck.style.marginRight=tuck.style.marginTop=tuck.style.marginBottom=0;
        tuck.style.paddingLeft=tuck.style.paddingRight=0;tuck.style.backgroundImage=StyleKeyword.None;
        tuck.style.backgroundColor=new Color(.07f,.13f,.2f,.92f);tuck.style.color=new Color(.62f,.84f,1);
        tuck.style.borderTopRightRadius=10;tuck.style.borderBottomRightRadius=10;
        tuck.style.borderTopLeftRadius=0;tuck.style.borderBottomLeftRadius=0;
        tuck.style.borderTopWidth=tuck.style.borderBottomWidth=tuck.style.borderLeftWidth=tuck.style.borderRightWidth=0;
        panel.style.paddingTop=18;
        SetPanelHidden(true);
        SetView(View.Overview);
    }
    public void SetPanelHidden(bool hidden){PanelHidden=hidden;tuck.text=hidden?"›":"‹";tuck.tooltip=hidden?"Show side menu":"Tuck away side menu";if(hidden)ExplorerInputFocus.ClaimViewport();}
    public void ReframeTorus(){if(Current==View.Torus){cameraMoving=true;if(orbit!=null)orbit.enabled=false;}}
    public void SetView(View view)
    {
        if(camera==null)return;
        if(Current==View.Overview&&view!=View.Overview){overviewPosition=camera.transform.position;overviewRotation=camera.transform.rotation;overviewAngles=orbit!=null?orbit.OrbitState:Vector3.zero;}
        if(Current!=view){cameraMoving=true;if(view==View.Overview){orbit?.OverviewFraming();orbit?.RestoreOrbit(overviewAngles);}}
        if(view!=View.Torus&&GetComponent<Main>().Uncoiled){GetComponent<Main>().SetUncoiled(false);uncoil?.SetValueWithoutNotify(false);}
        if(uncoil!=null)uncoil.style.display=view==View.Torus?DisplayStyle.Flex:DisplayStyle.None;
        Current=view;if(orbit!=null)orbit.enabled=(view==View.Overview||view==View.Torus)&&!cameraMoving;
        viewChoice?.SetValueWithoutNotify(viewChoice.choices[(int)view]);
        ExplorerInputFocus.ClaimViewport();
    }
    void LateUpdate()
    {
        using var perf=Perf.Views.Auto();
        if(root==null||camera==null)return;
        uncoil?.SetValueWithoutNotify(GetComponent<Main>().Uncoiled);
        float blend=Main.ReducedMotion?1:1-Mathf.Exp(-Time.unscaledDeltaTime*8);
        panelOpen=Mathf.Lerp(panelOpen,PanelHidden?0:1,blend);
        float width=root.resolvedStyle.width,height=root.resolvedStyle.height;
        if(!float.IsFinite(width)||width<1||height<1)return;
        float panelWidth=panel.layout.width;
        panel.style.translate=new Translate(-panelWidth*(1-panelOpen),0);panel.style.opacity=panelOpen;
        panel.style.visibility=panelOpen<.005f?Visibility.Hidden:Visibility.Visible;
        float left=panelWidth*panelOpen,right=library!=null?library.DockedWidth:0;
        tuck.style.left=left;tuck.style.top=Mathf.Max(90,(height-64)*.5f);
        float available=Mathf.Max(200,width-left-right);
        timelineFocus=Mathf.Lerp(timelineFocus,Current==View.Timeline?1:0,blend);
        timelineOpacity=Mathf.Lerp(timelineOpacity,Current==View.Overview||Current==View.Timeline||Current==View.Lyrics?1:0,blend);
        overviewSplit=Mathf.Lerp(overviewSplit,Current==View.Overview||Current==View.Lyrics?1:0,blend);
        bool lyricView=Current==View.Lyrics,portrait=Vertical||available<height*.95f||lyricView;
        Rect dock,strip,scene;float sideFrame;
        if(!portrait)
        {
            // Landscape: wheels, then a column with the lyric strip at its middle, then the torus.
            float dockWidth=Mathf.Clamp(available*.37f,340,600);dock=new Rect(left+16,52,dockWidth,height-64);
            // The strip keeps hard left, overlapping the wheels' empty right margin, and is
            // composited transparent: the torus is aimed to its right and nothing is hidden.
            float column=Mathf.Clamp(available*.17f,150,360),stripHeight=Mathf.Clamp(column*.34f,56,150);
            strip=new Rect(dock.xMax-40,(height-stripHeight)*.5f,column,stripHeight);
            scene=new Rect(dock.xMax-40,0,left+available-(dock.xMax-40),height);
            // Aim the camera so the torus centres in the part right of the lyric column, but never
            // so far that it leaves the frame: the look-at moves right by the column's share of
            // the view's width at the torus, capped by the room the torus needs.
            float rad=orbit!=null?orbit.OrbitState.x:5.6f,halfWidth=rad*Mathf.Tan(camera.fieldOfView*.5f*Mathf.Deg2Rad)*(scene.width/Mathf.Max(1,scene.height));
            float shift=Mathf.Clamp(Mathf.Min(column/Mathf.Max(1,scene.width)*halfWidth,halfWidth-3.3f),0,2.2f);
            sideFrame=shift/.95f;
        }
        else
        {
            // Portrait: the torus above, the lyric strip between, the wheels below (in lyric mode
            // the strip and the panel are larger).
            float sceneHeight=height*(lyricView?.3f:.44f),stripHeight=Mathf.Clamp(height*(lyricView?.26f:.15f),70,400);
            strip=new Rect(left+12,sceneHeight+4,available-24,stripHeight);
            float dockTop=strip.yMax+8;dock=new Rect(left+12,dockTop,available-24,height-dockTop-10);
            scene=new Rect(left,0,available,strip.yMin-2);
            sideFrame=0;
        }
        LyricStrip=strip;
        // The vowel wheel of the rhyme graph: in the lyric panel's left column under the vocal
        // wheel, beside the rhyming lines, as large as that column allows (written only when it moves).
        var wheelArea=wheelsDeck.Graph.WheelArea;var ov=overlay.worldBound;
        if(Current==View.Lyrics&&wheelArea.width>0&&ov.width>1&&timelineOpacity>.5f)
        {
            var column=new Rect(ov.x+wheelArea.x*ov.width,ov.y+wheelArea.y*ov.height,wheelArea.width*ov.width,wheelArea.height*ov.height);
            float wheelSize=Mathf.Min(column.width,column.height);var at=new Rect(column.x+(column.width-wheelSize)*.5f,column.y+(column.height-wheelSize)*.5f,wheelSize,wheelSize);
            if(at!=placedWheel){placedWheel=at;vowelWheel.style.left=at.x;vowelWheel.style.top=at.y;vowelWheel.style.width=at.width;vowelWheel.style.height=at.height;vowelWheel.style.visibility=Visibility.Visible;vowelWheel.MarkDirtyRepaint();}
        }
        else if(placedWheel.width>=0){placedWheel=new Rect(0,0,-1,-1);vowelWheel.style.visibility=Visibility.Hidden;}
        float focusWidth=Mathf.Min(available-24,(height-70)*1.12f);
        var focus=new Rect(left+(available-focusWidth)*.5f,52,focusWidth,height-70);
        overlay.style.maxWidth=StyleKeyword.None;
        overlay.style.width=Mathf.Lerp(dock.width,focus.width,timelineFocus);overlay.style.height=Mathf.Lerp(dock.height,focus.height,timelineFocus);
        overlay.style.left=Mathf.Lerp(dock.x,focus.x,timelineFocus)-(1-timelineOpacity)*available;
        overlay.style.top=Mathf.Lerp(dock.y,focus.y,timelineFocus);overlay.style.opacity=timelineOpacity;
        // The scene camera: its part of the screen in the overview, all of it otherwise.
        float split=overviewSplit*(1-timelineFocus);
        var full=new Rect(left,SceneTopInset,available,Mathf.Max(80,height-SceneTopInset-SceneBottomInset));
        var shown=new Rect(Mathf.Lerp(full.x,scene.x,split),Mathf.Lerp(full.y,scene.y,split),Mathf.Lerp(full.width,scene.width,split),Mathf.Lerp(full.height,scene.height,split));
        SceneRect=shown;
        camera.rect=new Rect(shown.x/width,1-shown.yMax/height,shown.width/width,shown.height/height);
        // The orbit's side framing recomputes the camera's place; while a view change lerps the
        // camera the two would fight and jitter, so it waits until the orbit is back in charge.
        if(orbit!=null&&orbit.enabled&&!cameraMoving)orbit.SideFrame=sideFrame*split;
        // Nothing to wheel before a song is chosen: the intro stands alone.
        overlay.style.visibility=timelineOpacity<.005f||!GetComponent<MidiPlayer>().Loaded?Visibility.Hidden:Visibility.Visible;
        // The lyric strip: between the wheels and the torus in the overview, larger in lyric mode.
        if(lyrics!=null)
        {
            bool wanted=(Current==View.Overview||lyricView)&&timelineFocus<.5f;
            lyrics.Wanted=wanted;lyrics.Viewport=new Rect(strip.x/width,1-strip.yMax/height,strip.width/width,strip.height/height);
        }
        // Lyric mode focuses on the lyrics: the torus steps back and the drum wheel lies deep behind.
        TorusOpacity=Mathf.Lerp(TorusOpacity,(Current==View.Overview||Current==View.Torus?1:0)*Mathf.Clamp01(TorusOpacityCap),blend);
        var shape=GetComponent<Main>();
        bool showDrums=Current==View.Overview||Current==View.Drums||Current==View.Lyrics||(Current==View.Torus&&shape.Uncoiled&&!shape.UncoilMoving&&shape.UncoilAmount>.9999f);
        DrumOpacity=showDrums?Mathf.Lerp(DrumOpacity,1,blend):0;
        if(Current==View.Drums||Current==View.Timeline||Current==View.Lyrics||cameraMoving){
            Vector3 position=overviewPosition;Quaternion rotation=overviewRotation;
            if(Current==View.Torus){float distance=4.3f/Mathf.Min(1,camera.aspect)/Mathf.Max(.25f,TorusZoom);Vector3 target=transform.position;position=target+new Vector3(.51f,.75f,.51f).normalized*distance;rotation=Quaternion.LookRotation(target-position);float unfold=GetComponent<Main>().UncoilAmount;position=Vector3.Slerp(position-target,-transform.forward*(6f/Mathf.Min(1,camera.aspect)),unfold)+target;position=target+(position-target)*(1+.55f*GetComponent<Main>().TransitionWiden);rotation=Quaternion.LookRotation(target-position,transform.up);}
            if(Current==View.Drums||Current==View.Lyrics){Vector3 target=drums?.WheelTransform!=null?drums.WheelTransform.position:transform.position+Vector3.down*DrumPatternDeck.DeckDepth;position=target+Vector3.up*((Current==View.Lyrics?4.6f:3.6f)/Mathf.Min(1,camera.aspect));rotation=Quaternion.LookRotation(Vector3.down,Vector3.forward);}
            if(Current==View.Timeline){position=overviewPosition+Vector3.right*5;rotation=overviewRotation;}
            camera.transform.position=Vector3.Lerp(camera.transform.position,position,blend);camera.transform.rotation=Quaternion.Slerp(camera.transform.rotation,rotation,blend);
            orbit?.MovementUpdater?.Invoke();
            if((Current==View.Overview||Current==View.Torus)&&Vector3.Distance(camera.transform.position,position)<.005f&&Quaternion.Angle(camera.transform.rotation,rotation)<.1f){cameraMoving=GetComponent<Main>().UncoilMoving;if(orbit!=null&&!cameraMoving){if(Current==View.Torus)orbit.AdoptView(transform.position,true);orbit.enabled=true;}}
        }
        TorusOpacity=Snap(TorusOpacity);DrumOpacity=Snap(DrumOpacity);
        // Opacity only needs reapplying when it changes, when renderers come and go, or while
        // the uncoil hides labels; otherwise the walk over every renderer is skipped (a walk on
        // a timer would be a hitch every time it fired).
        bool uncoiling=GetComponent<Main>().UncoilMoving;int count=transform.hierarchyCount;
        if(TorusOpacity==shownTorus&&DrumOpacity==shownDrums&&count==shownCount&&!uncoiling&&!wasUncoiling)return;
        shownTorus=TorusOpacity;shownDrums=DrumOpacity;shownCount=count;wasUncoiling=uncoiling;
        renderers.Clear();GetComponentsInChildren(true,renderers);
        if(appliedOpacity.Count>2048)appliedOpacity.Clear();
        var drumRoot=drums?.WheelTransform;
        foreach(var renderer in renderers){
            if(renderer==null)continue;
            float opacity=drumRoot!=null&&renderer.transform.IsChildOf(drumRoot)?DrumOpacity:TorusOpacity;
            bool hideLabel=GetComponent<Main>().UncoilMoving&&renderer.GetComponent<TMPro.TMP_Text>()!=null;
            if(hideLabel){renderer.forceRenderingOff=true;appliedOpacity.Remove(renderer);continue;}
            if(appliedOpacity.TryGetValue(renderer,out var previous)&&previous==opacity)continue;
            appliedOpacity[renderer]=opacity;
            renderer.forceRenderingOff=opacity<.005f;
            var mat=renderer.sharedMaterial;if(mat==null)continue;
            if(mat.shader!=null&&mat.shader.name=="Universal Render Pipeline/Unlit"){
                var fadeShader=Resources.Load<Shader>("ViewUnlit");if(fadeShader!=null){replacedShaders[mat]=mat.shader;mat.shader=fadeShader;}
            }
            renderer.GetPropertyBlock(block);block.SetFloat("_ViewOpacity",opacity);
            if(mat.HasProperty("_FaceColor")){var face=mat.GetColor("_FaceColor");face.a*=opacity;block.SetColor("_FaceColor",face);}
            renderer.SetPropertyBlock(block);
        }
    }
    static float Snap(float value)=>value<.0001f?0:value>.9999f?1:value;
    void OnDestroy(){if(clear!=null)Destroy(clear.gameObject);}
    void OnDisable(){foreach(var renderer in renderers)if(renderer!=null){renderer.forceRenderingOff=false;renderer.GetPropertyBlock(block);block.SetFloat("_ViewOpacity",1);renderer.SetPropertyBlock(block);}foreach(var entry in replacedShaders)if(entry.Key!=null)entry.Key.shader=entry.Value;replacedShaders.Clear();if(orbit!=null)orbit.enabled=true;}
}
