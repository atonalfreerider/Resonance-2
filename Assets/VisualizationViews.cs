using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

[DefaultExecutionOrder(1000)]
public sealed class VisualizationViews : MonoBehaviour
{
    public enum View { Overview,Torus,Timeline,Drums,Lyrics }
    public View Current {get;private set;}
    public bool PanelHidden {get;private set;}
    public float TorusOpacity {get;private set;}=1;
    public float DrumOpacity {get;private set;}=1;
    VisualElement root,panel,overlay,toolbar;Button tuck;
    DropdownField viewChoice;
    Toggle uncoil;
    readonly Dictionary<Renderer,float> appliedOpacity=new();
    MaterialPropertyBlock block;
    readonly List<Renderer> renderers=new();
    readonly Dictionary<Material,Shader> replacedShaders=new();
    Camera camera,clear;CameraControl orbit;DrumPatternDeck drums;
    Vector3 overviewPosition;Quaternion overviewRotation;Vector3 overviewAngles;
    float panelOpen=1,timelineOpacity=1,timelineFocus,overviewSplit=1,lyricDock;
    float shownTorus=-1,shownDrums=-1,nextWalk;int shownCount=-1;bool wasUncoiling;
    bool cameraMoving;
    void Awake(){block=new MaterialPropertyBlock();}
    public void Bind(VisualElement ui,VisualElement controls,PatternWheelDeck wheels)
    {
        root=ui;panel=controls;overlay=wheels.Overlay;camera=Camera.main;orbit=camera.GetComponent<CameraControl>();drums=GetComponent<DrumPatternDeck>();
        // The scene camera renders only its part of a split screen; this one clears the whole
        // screen first, so the wheels never draw over stale frames.
        var clearing=new GameObject("Screen clear"){hideFlags=HideFlags.DontSave};clear=clearing.AddComponent<Camera>();
        clear.cullingMask=0;clear.clearFlags=CameraClearFlags.SolidColor;clear.backgroundColor=camera.backgroundColor;clear.depth=camera.depth-10;clear.rect=new Rect(0,0,1,1);
        var clearData=clear.GetUniversalAdditionalCameraData();clearData.renderPostProcessing=false;clearData.renderShadows=false;
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
        tuck=new Button(()=>SetPanelHidden(!PanelHidden)){text="‹",name="tuck-side-menu",tooltip="Tuck away side menu"};root.Add(tuck);
        tuck.style.position=Position.Absolute;tuck.style.width=25;tuck.style.height=64;tuck.style.fontSize=27;
        tuck.style.marginLeft=tuck.style.marginRight=tuck.style.marginTop=tuck.style.marginBottom=0;
        tuck.style.paddingLeft=tuck.style.paddingRight=0;tuck.style.backgroundImage=StyleKeyword.None;
        tuck.style.backgroundColor=new Color(.07f,.13f,.2f,.92f);tuck.style.color=new Color(.62f,.84f,1);
        tuck.style.borderTopRightRadius=10;tuck.style.borderBottomRightRadius=10;
        tuck.style.borderTopLeftRadius=0;tuck.style.borderBottomLeftRadius=0;
        tuck.style.borderTopWidth=tuck.style.borderBottomWidth=tuck.style.borderLeftWidth=tuck.style.borderRightWidth=0;
        panel.style.paddingTop=18;
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
        float left=panelWidth*panelOpen;
        tuck.style.left=left;tuck.style.top=Mathf.Max(90,(height-64)*.5f);
        float available=width-left;
        timelineFocus=Mathf.Lerp(timelineFocus,Current==View.Timeline?1:0,blend);
        timelineOpacity=Mathf.Lerp(timelineOpacity,Current==View.Overview||Current==View.Timeline||Current==View.Lyrics?1:0,blend);
        // The overview splits the screen so the pattern wheels and the 3D scene (torus and drum
        // wheel) never overlap: side by side in a landscape window, stacked in a portrait one
        // (scene above, wheels below). The camera renders only its own part.
        // Lyric mode always stacks: the drum wheel and its lyric rack in a wide strip above, the
        // vocal wheel and rhyme board below.
        overviewSplit=Mathf.Lerp(overviewSplit,Current==View.Overview||Current==View.Lyrics?1:0,blend);
        lyricDock=Mathf.Lerp(lyricDock,Current==View.Lyrics?1:0,blend);
        bool portrait=available<height*.9f||lyricDock>.5f;
        // In portrait the wheels take only the height their width can use (ring, rack and
        // caption); the rest goes to the scene.
        float dockHeight=lyricDock>.5f?Mathf.Clamp(height*.6f,420,height-220):portrait?Mathf.Clamp(available+150,380,height*.58f):height-64;
        var dock=portrait?new Rect(left+12,height-dockHeight-10,available-24,dockHeight)
            :new Rect(left+16,52,Mathf.Clamp(available*.46f,340,640),dockHeight);
        float focusWidth=Mathf.Min(available-24,(height-70)*1.12f);
        var focus=new Rect(left+(available-focusWidth)*.5f,52,focusWidth,height-70);
        overlay.style.maxWidth=StyleKeyword.None;
        overlay.style.width=Mathf.Lerp(dock.width,focus.width,timelineFocus);overlay.style.height=Mathf.Lerp(dock.height,focus.height,timelineFocus);
        overlay.style.left=Mathf.Lerp(dock.x,focus.x,timelineFocus)-(1-timelineOpacity)*available;
        overlay.style.top=Mathf.Lerp(dock.y,focus.y,timelineFocus);overlay.style.opacity=timelineOpacity;
        float split=overviewSplit*(1-timelineFocus);
        if(portrait){float below=(height-dock.y+4)/height*split;camera.rect=new Rect(left/width,below,1-left/width,1-below);}
        else{float beside=(dock.xMax+8-left)*split;camera.rect=new Rect((left+beside)/width,0,1-(left+beside)/width,1);}
        if(orbit!=null)orbit.SideFrame=1-split;
        overlay.style.visibility=timelineOpacity<.005f?Visibility.Hidden:Visibility.Visible;
        // Lyric mode focuses on the lyrics: the torus steps back and the drum wheel carries the rack.
        TorusOpacity=Mathf.Lerp(TorusOpacity,Current==View.Overview||Current==View.Torus?1:0,blend);
        var shape=GetComponent<Main>();
        bool showDrums=Current==View.Overview||Current==View.Drums||Current==View.Lyrics||(Current==View.Torus&&shape.Uncoiled&&!shape.UncoilMoving&&shape.UncoilAmount>.9999f);
        DrumOpacity=showDrums?Mathf.Lerp(DrumOpacity,1,blend):0;
        if(Current==View.Drums||Current==View.Timeline||Current==View.Lyrics||cameraMoving){
            Vector3 position=overviewPosition;Quaternion rotation=overviewRotation;
            if(Current==View.Torus){float distance=4.3f/Mathf.Min(1,camera.aspect);Vector3 target=transform.position;position=target+new Vector3(.51f,.75f,.51f).normalized*distance;rotation=Quaternion.LookRotation(target-position);float unfold=GetComponent<Main>().UncoilAmount;position=Vector3.Slerp(position-target,-transform.forward*(6f/Mathf.Min(1,camera.aspect)),unfold)+target;position=target+(position-target)*(1+.55f*GetComponent<Main>().TransitionWiden);rotation=Quaternion.LookRotation(target-position,transform.up);}
            if(Current==View.Drums){Vector3 target=drums?.WheelTransform!=null?drums.WheelTransform.position:transform.position+Vector3.down*DrumPatternDeck.DeckDepth;position=target+Vector3.up*(3.6f/Mathf.Min(1,camera.aspect));rotation=Quaternion.LookRotation(Vector3.down,Vector3.forward);}
            if(Current==View.Timeline){position=overviewPosition+Vector3.right*5;rotation=overviewRotation;}
            if(Current==View.Lyrics)
            {
                // Straight down on the drum wheel, twelve o'clock up, framing the rack beyond the rim.
                var wheel=drums?.WheelTransform;Vector3 target=wheel!=null?wheel.position:transform.position+Vector3.down*DrumPatternDeck.DeckDepth;
                float scale=wheel!=null?wheel.lossyScale.x:1.25f;target+=Vector3.forward*scale*DrumLyricRack.Middle;
                // The disc, the rack and the line above it set the height; the rack runs as wide as the strip.
                float tan=Mathf.Tan(camera.fieldOfView*.5f*Mathf.Deg2Rad);
                float distance=DrumLyricRack.Tall*.5f*scale/tan;
                position=target+Vector3.up*distance;rotation=Quaternion.LookRotation(Vector3.down,Vector3.forward);
            }
            camera.transform.position=Vector3.Lerp(camera.transform.position,position,blend);camera.transform.rotation=Quaternion.Slerp(camera.transform.rotation,rotation,blend);
            orbit?.MovementUpdater?.Invoke();
            if((Current==View.Overview||Current==View.Torus)&&Vector3.Distance(camera.transform.position,position)<.005f&&Quaternion.Angle(camera.transform.rotation,rotation)<.1f){cameraMoving=GetComponent<Main>().UncoilMoving;if(orbit!=null&&!cameraMoving){if(Current==View.Torus)orbit.AdoptView(transform.position,true);orbit.enabled=true;}}
        }
        TorusOpacity=Snap(TorusOpacity);DrumOpacity=Snap(DrumOpacity);
        // Opacity only needs reapplying when it changes, when renderers come and go, or while
        // the uncoil hides labels; otherwise the walk over every renderer is skipped.
        bool uncoiling=GetComponent<Main>().UncoilMoving;int count=transform.hierarchyCount;
        if(TorusOpacity==shownTorus&&DrumOpacity==shownDrums&&count==shownCount&&!uncoiling&&!wasUncoiling&&Time.unscaledTime<nextWalk)return;
        shownTorus=TorusOpacity;shownDrums=DrumOpacity;shownCount=count;wasUncoiling=uncoiling;nextWalk=Time.unscaledTime+1;
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
