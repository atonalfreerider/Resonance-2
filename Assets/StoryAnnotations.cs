using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

[DefaultExecutionOrder(1160)]
public sealed class StoryAnnotations : MonoBehaviour
{
    SongDirector director;Main main;FeaturedInstrument featured;PatternWheelDeck wheels;
    VisualElement root,controls;ArrowCanvas canvas;Label label;bool enabledArrows=true;Vector2 labelPosition;
    readonly List<Vector3> worldPoints=new();
    readonly Label[] voiceLabels=new Label[3];
    public int ArrowCount=>canvas?.Targets.Count??0;
    public string TargetName=>director?.CurrentCue?.annotationTarget??"none";
    public void Bind(VisualElement ui,VisualElement panel,PatternWheelDeck patterns,VisualElement settings)
    {
        director=GetComponent<SongDirector>();main=GetComponent<Main>();featured=GetComponent<FeaturedInstrument>();
        root=ui;controls=panel;wheels=patterns;
        var toggle=new Toggle("Story guide arrows"){name="story-guide-arrows",value=true};toggle.RegisterValueChangedCallback(e=>enabledArrows=e.newValue);settings.Add(toggle);
        canvas=new ArrowCanvas{name="story-guide-overlay",pickingMode=PickingMode.Ignore};
        canvas.style.position=Position.Absolute;canvas.style.left=canvas.style.right=canvas.style.top=canvas.style.bottom=0;root.Add(canvas);
        label=new Label{name="story-guide-label",pickingMode=PickingMode.Ignore,enableRichText=false};canvas.Add(label);
        label.style.position=Position.Absolute;label.style.width=210;label.style.fontSize=14;label.style.whiteSpace=WhiteSpace.Normal;label.style.color=new Color(.88f,.96f,1);
        for(int i=0;i<voiceLabels.Length;i++){var tip=new Label{pickingMode=PickingMode.Ignore,enableRichText=false};tip.style.position=Position.Absolute;tip.style.fontSize=12;tip.style.color=new Color(.88f,.96f,1);canvas.Add(tip);voiceLabels[i]=tip;}
        canvas.style.display=DisplayStyle.None;
    }
    void LateUpdate()
    {
        if(canvas==null||root.panel==null)return;
        canvas.Targets.Clear();var cue=director.CurrentCue;
        foreach(var tip in voiceLabels)tip.style.display=DisplayStyle.None;
        if(!enabledArrows||cue==null||main.UncoilMoving){canvas.style.display=DisplayStyle.None;return;}
        float width=root.resolvedStyle.width,height=root.resolvedStyle.height;
        if(!float.IsFinite(width)||width<1||height<1)return;
        float left=GetComponent<VisualizationViews>().PanelHidden?20:controls.resolvedStyle.width+20;
        if(cue.annotationTarget=="patterns"){
            if(wheels.Overlay.resolvedStyle.opacity>.3f)canvas.Targets.Add(canvas.WorldToLocal(wheels.Overlay.LocalToWorld(wheels.FeaturedCenter+Vector2.up*wheels.FeaturedRadius*.65f)));
        }else{
            worldPoints.Clear();
            if(cue.annotationTarget=="melody"){
                var pair=GetComponent<ConcertoTori>();
                if(pair!=null&&pair.SplitActive){
                    var scratch=new List<Vector3>();
                    string target=cue.melodyTarget??"both";
                    if(target!="orchestra"&&pair.Piano.gameObject.activeInHierarchy){pair.Piano.GetComponent<FeaturedInstrument>().GetFocusPoints(scratch);worldPoints.AddRange(scratch);}
                    if(target!="piano"&&pair.Orchestra.gameObject.activeInHierarchy){pair.Orchestra.GetComponent<FeaturedInstrument>().GetFocusPoints(scratch);worldPoints.AddRange(scratch);}
                }else featured.GetFocusPoints(worldPoints);
            }
            if(cue.annotationTarget=="drums"&&GetComponent<VisualizationViews>().DrumOpacity>.3f){var drum=GetComponent<DrumPatternDeck>().WheelTransform;if(drum!=null)worldPoints.Add(drum.TransformPoint(new Vector3(0,.04f,1.16f)));}
            if(cue.annotationTarget=="note"&&cue.annotationMidiPitch>=21&&cue.annotationMidiPitch<117){
                var pair=GetComponent<ConcertoTori>();var shape=pair!=null&&pair.SplitActive?(cue.melodyTarget=="orchestra"?pair.Orchestra:pair.Piano):main;
                if(shape.gameObject.activeInHierarchy&&shape.NoteCount>cue.annotationMidiPitch-21)worldPoints.Add(shape.NotePosition(cue.annotationMidiPitch-21));
            }
            if(cue.annotationTarget=="chord"&&cue.annotationPitchClass>=0){
                var pair=GetComponent<ConcertoTori>();
                if(pair!=null&&pair.SplitActive){foreach(var shape in new[]{pair.Piano,pair.Orchestra})if(shape.gameObject.activeInHierarchy)worldPoints.Add(shape.LabelPosition(cue.annotationPitchClass));}
                else worldPoints.Add(main.LabelPosition(cue.annotationPitchClass));
            }
            foreach(var point in worldPoints){var screen=Camera.main.WorldToScreenPoint(point);if(screen.z<=0)continue;
                var panel=RuntimePanelUtils.ScreenToPanel(root.panel,new Vector2(screen.x,Screen.height-screen.y));canvas.Targets.Add(canvas.WorldToLocal(panel));}
        }
        canvas.Targets.RemoveAll(p=>!float.IsFinite(p.x)||!float.IsFinite(p.y)||p.x<left||p.x>width-10||p.y<65||p.y>height-110);
        if(canvas.Targets.Count==0){canvas.style.display=DisplayStyle.None;return;}
        if(cue.melodyTarget=="three")for(int i=0;i<Mathf.Min(3,canvas.Targets.Count);i++){
            voiceLabels[i].text=i==0?"1 · Piano upper":i==1?"2 · Piano inner":"3 · Orchestra";
            voiceLabels[i].style.left=Mathf.Clamp(canvas.Targets[i].x+14,left,width-120);voiceLabels[i].style.top=canvas.Targets[i].y-24;voiceLabels[i].style.display=DisplayStyle.Flex;
        }
        // A stable reading position; only the arrow tips follow the performance.
        // Reflow only when the viewport or side panel changes, never with notes.
        labelPosition=new Vector2(Mathf.Min(left+16,Mathf.Max(16,width-225)),90);
        label.text=cue.annotationLabel??"";label.style.left=labelPosition.x;label.style.top=labelPosition.y;
        canvas.Start=labelPosition+new Vector2(100,42);canvas.style.display=DisplayStyle.Flex;canvas.MarkDirtyRepaint();
    }
    sealed class ArrowCanvas : VisualElement
    {
        public readonly List<Vector2> Targets=new();public Vector2 Start;
        public ArrowCanvas(){generateVisualContent+=Draw;}
        void Draw(MeshGenerationContext context)
        {
            var p=context.painter2D;p.lineCap=LineCap.Round;p.lineJoin=LineJoin.Round;
            foreach(var target in Targets){
                Vector2 approach=target+(Start-target).normalized*13;
                Vector2 control=Vector2.Lerp(Start,approach,.55f)+Vector2.up*20;
                for(int layer=0;layer<2;layer++){
                    p.lineWidth=layer==0?4:1.5f;p.strokeColor=layer==0?new Color(.1f,.25f,.4f,.55f):new Color(.84f,.95f,1,.9f);
                    p.BeginPath();p.MoveTo(Start);p.QuadraticCurveTo(control,approach);p.Stroke();
                    Vector2 direction=(approach-control).normalized,side=new Vector2(-direction.y,direction.x);
                    p.BeginPath();p.MoveTo(approach-direction*11+side*5);p.LineTo(approach);p.LineTo(approach-direction*11-side*5);p.Stroke();
                }
            }
        }
    }
}
