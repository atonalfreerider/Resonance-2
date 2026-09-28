using TMPro;
using UnityEngine;

// A vertical pointer at the torus's tonic label whenever the torus flexes: up (sharpward, toward
// the dominant side of the circle of fifths) or down (flatward) to the key the flex points at,
// named at its tip. It appears as a tension's lean begins or a key change starts, and goes
// away once the torus is locked into the new key or has settled back into the old one.
[DefaultExecutionOrder(90)]
public sealed class KeyShiftIndicator : MonoBehaviour
{
    Main main;LineRenderer shaft,head;TextBox label;Material glow;
    float shown,settle=1;int target=-1,direction;
    const float Length=.6f,Fade=.45f;
    // For validation: whether it is up, and where it points.
    public bool Visible=>shown>.05f;
    public int Direction=>direction;
    public int Target=>target;
    public float Alpha=>shown;

    void Awake()
    {
        main=GetComponent<Main>();
        glow=new Material(Resources.Load<Shader>("HarmonicGlow"));glow.SetColor("_BaseColor",Color.white*2);glow.renderQueue=3105;
        shaft=Line("Key shift · shaft",.028f);head=Line("Key shift · head",.028f);head.numCapVertices=0;
        label=TextBox.Create("",TextAlignmentOptions.Center);label.transform.SetParent(transform,false);label.Size=.55f;label.TextField.fontStyle=FontStyles.Bold;
        label.TextField.fontMaterial.renderQueue=3106;label.gameObject.SetActive(false);
    }
    LineRenderer Line(string name,float width)
    {
        var go=new GameObject(name);go.transform.SetParent(transform,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=glow;l.useWorldSpace=true;l.widthMultiplier=width;l.numCapVertices=2;l.positionCount=0;return l;
    }
    // Up when the key is sharpward within a tritone on the circle of fifths, else down.
    public static int DirectionTo(int from,int to){int fifths=HarmonyModel.Mod((to-from)*7);return fifths==0?0:fifths<=6?1:-1;}

    void LateUpdate()
    {
        if(main==null)return;
        // What the torus is flexing toward now: a key change under way, else a tension's lean.
        bool changing=main.KeyChanging;
        int to=changing?main.currentKey:main.TensionKey,from=changing?main.KeyFrom:main.currentKey;
        bool flexing=(changing||main.TensionAmount>.015f)&&to!=from;
        if(flexing){target=to;direction=DirectionTo(from,to);settle=0;}
        else if(shown>0)settle+=Time.unscaledDeltaTime;
        float want=flexing?1:0;
        shown=Main.ReducedMotion?want:Mathf.MoveTowards(shown,want,Time.unscaledDeltaTime/(flexing?.25f:Fade));
        if(shown<=0||target<0){if(shaft.positionCount>0){shaft.positionCount=head.positionCount=0;label.gameObject.SetActive(false);}return;}
        var anchor=main.LabelPosition(from);var camera=Camera.main;
        var up=camera!=null?camera.transform.up:Vector3.up;
        float scale=Mathf.Max(.5f,main.transform.lossyScale.x);
        var start=anchor+up*.25f*scale*direction;var tip=start+up*Length*scale*direction;
        shaft.positionCount=2;shaft.SetPosition(0,start);shaft.SetPosition(1,tip);
        // The arrowhead: two strokes back from the tip.
        var side=camera!=null?camera.transform.right:Vector3.right;float w=.12f*scale;
        head.positionCount=3;head.SetPosition(0,tip-up*w*direction-side*w);head.SetPosition(1,tip);head.SetPosition(2,tip-up*w*direction+side*w);
        var c=Color.white*(.9f+.3f*shown);c.a=shown;shaft.startColor=shaft.endColor=head.startColor=head.endColor=c;
        if(!label.gameObject.activeSelf)label.gameObject.SetActive(true);
        label.Text=(direction>0?"▲ ":"▼ ")+main.PitchName(target);
        label.transform.position=tip+up*.22f*scale*direction;label.Billboard();
        label.TextField.fontMaterial.SetColor(ShaderUtilities.ID_FaceColor,new Color(1,1,1,shown));
    }
    void OnDestroy(){if(glow!=null)Destroy(glow);}
}
