using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

[DefaultExecutionOrder(1000)]
public sealed class VisualizationViews : MonoBehaviour
{
    public enum View { Overview,Torus,Timeline,Drums }
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
    Camera camera;CameraControl orbit;DrumPatternDeck drums;
    Vector3 overviewPosition;Quaternion overviewRotation;Vector3 overviewAngles;
    float panelOpen=1,timelineOpacity=1,timelineFocus;
    bool cameraMoving;
    void Awake(){block=new MaterialPropertyBlock();}
    public void Bind(VisualElement ui,VisualElement controls,PatternWheelDeck wheels)
    {
        root=ui;panel=controls;overlay=wheels.Overlay;camera=Camera.main;orbit=camera.GetComponent<CameraControl>();drums=GetComponent<DrumPatternDeck>();
        toolbar=new VisualElement{name="visualization-views"};toolbar.style.position=Position.Absolute;toolbar.style.top=12;toolbar.style.right=16;
        toolbar.style.flexDirection=FlexDirection.Row;toolbar.style.alignItems=Align.Center;toolbar.style.backgroundColor=new Color(.055f,.09f,.14f,.92f);
        toolbar.style.borderTopLeftRadius=18;toolbar.style.borderTopRightRadius=18;toolbar.style.borderBottomLeftRadius=18;toolbar.style.borderBottomRightRadius=18;
        toolbar.style.paddingLeft=16;toolbar.style.paddingRight=8;root.Add(toolbar);
        var label=new Label("VIEW");label.style.color=new Color(.43f,.63f,.78f);label.style.fontSize=10;label.style.letterSpacing=2;toolbar.Add(label);
        viewChoice=new DropdownField(new List<string>{"Overview","Torus","Pattern timeline","Drum wheel"},0){name="view-selector",tooltip="Choose the featured visualization"};
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
        camera.rect=new Rect(left/width,0,1-left/width,1);
        float available=width-left;
        timelineFocus=Mathf.Lerp(timelineFocus,Current==View.Timeline?1:0,blend);
        timelineOpacity=Mathf.Lerp(timelineOpacity,Current==View.Overview||Current==View.Timeline?1:0,blend);
        float wheelWidth=Mathf.Lerp(Mathf.Min(510,available*.49f),Mathf.Min(available-24,(height-70)*1.12f),timelineFocus);
        overlay.style.maxWidth=StyleKeyword.None;overlay.style.width=wheelWidth;
        overlay.style.height=Mathf.Lerp(Mathf.Min(620,height-60),height-70,timelineFocus);
        overlay.style.left=left+30+timelineFocus*(available-wheelWidth)*.5f-(1-timelineOpacity)*available;
        overlay.style.top=52;overlay.style.opacity=timelineOpacity;
        overlay.style.visibility=timelineOpacity<.005f?Visibility.Hidden:Visibility.Visible;
        TorusOpacity=Mathf.Lerp(TorusOpacity,Current==View.Overview||Current==View.Torus?1:0,blend);
        var shape=GetComponent<Main>();
        bool showDrums=Current==View.Drums||(Current==View.Torus&&shape.Uncoiled&&!shape.UncoilMoving&&shape.UncoilAmount>.9999f);
        DrumOpacity=showDrums?Mathf.Lerp(DrumOpacity,1,blend):0;
        if(Current==View.Drums||Current==View.Timeline||cameraMoving){
            Vector3 position=overviewPosition;Quaternion rotation=overviewRotation;
            if(Current==View.Torus){float distance=4.3f/Mathf.Min(1,camera.aspect);Vector3 target=transform.position;position=target+new Vector3(.51f,.75f,.51f).normalized*distance;rotation=Quaternion.LookRotation(target-position);float unfold=GetComponent<Main>().UncoilAmount;position=Vector3.Slerp(position-target,-transform.forward*(6f/Mathf.Min(1,camera.aspect)),unfold)+target;position=target+(position-target)*(1+.55f*GetComponent<Main>().TransitionWiden);rotation=Quaternion.LookRotation(target-position,transform.up);}
            if(Current==View.Drums){Vector3 target=drums?.WheelTransform!=null?drums.WheelTransform.position:transform.position+Vector3.down*2.8f;position=target+Vector3.up*(3.6f/Mathf.Min(1,camera.aspect));rotation=Quaternion.LookRotation(Vector3.down,Vector3.forward);}
            if(Current==View.Timeline){position=overviewPosition+Vector3.right*5;rotation=overviewRotation;}
            camera.transform.position=Vector3.Lerp(camera.transform.position,position,blend);camera.transform.rotation=Quaternion.Slerp(camera.transform.rotation,rotation,blend);
            orbit?.MovementUpdater?.Invoke();
            if((Current==View.Overview||Current==View.Torus)&&Vector3.Distance(camera.transform.position,position)<.005f&&Quaternion.Angle(camera.transform.rotation,rotation)<.1f){cameraMoving=GetComponent<Main>().UncoilMoving;if(orbit!=null&&!cameraMoving){if(Current==View.Torus)orbit.AdoptView(transform.position,true);orbit.enabled=true;}}
        }
        renderers.Clear();GetComponentsInChildren(true,renderers);
        TorusOpacity=Snap(TorusOpacity);DrumOpacity=Snap(DrumOpacity);
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
    void OnDisable(){foreach(var renderer in renderers)if(renderer!=null){renderer.forceRenderingOff=false;renderer.GetPropertyBlock(block);block.SetFloat("_ViewOpacity",1);renderer.SetPropertyBlock(block);}foreach(var entry in replacedShaders)if(entry.Key!=null)entry.Key.shader=entry.Value;replacedShaders.Clear();if(orbit!=null)orbit.enabled=true;}
}
