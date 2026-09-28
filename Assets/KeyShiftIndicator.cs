using TMPro;
using UnityEngine;

// The scale a key change is measured on: while the torus turns into a new key, a height scale
// stands through the old key's label along the y axis, a thin rule with the chromatic scale
// on it, one tick and one note name per semitone, the old key at the torus's edge. It slides
// as the torus turns, by the semitones between the keys (the shorter way), so the new key
// arrives at the edge as the turn completes. It is faint, most present at the edge and fading
// quickly above and below, and it stands where the label was as the change began, never
// turning with the torus. It fades once the torus is locked in. Tonicizations no longer move
// the torus, so they raise no scale.
[DefaultExecutionOrder(90)]
public sealed class KeyShiftIndicator : MonoBehaviour
{
    Main main;LineRenderer rail,ticks,mark;Material glow;readonly TextBox[] names=new TextBox[12];
    float shown;int target=-1,from=-1,direction,steps;Vector3 anchor;bool wasChanging;
    const float Pitch=.13f,Tick=.05f,Fade=.5f;const int Points=41;
    static readonly Color Metal=new(.5f,.72f,.74f);
    readonly Vector3[] points=new Vector3[Points];readonly Vector3[] tickPoints=new Vector3[36];readonly Gradient gradient=new();readonly GradientAlphaKey[] alphas=new GradientAlphaKey[8];
    // For validation: whether it stands, and where it points.
    public bool Visible=>shown>.05f;
    public int Direction=>direction;
    public int Target=>target;
    public float Alpha=>shown;

    void Awake()
    {
        main=GetComponent<Main>();
        glow=new Material(Resources.Load<Shader>("HarmonicGlow"));glow.SetColor("_BaseColor",Color.white);glow.renderQueue=3105;
        rail=Line("Key shift · scale",.011f);ticks=Line("Key shift · ticks",.008f);ticks.numCapVertices=0;ticks.numCornerVertices=0;mark=Line("Key shift · mark",.014f);mark.numCapVertices=0;
        for(int i=0;i<12;i++)
        {
            var label=TextBox.Create("",TextAlignmentOptions.Left);label.transform.SetParent(transform,false);label.Size=.24f;label.TextField.fontStyle=FontStyles.Bold;
            label.TextField.fontMaterial.renderQueue=3106;label.gameObject.SetActive(false);names[i]=label;
        }
    }
    LineRenderer Line(string name,float width)
    {
        var go=new GameObject(name);go.transform.SetParent(transform,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=glow;l.useWorldSpace=true;l.widthMultiplier=width;l.numCapVertices=2;l.positionCount=0;return l;
    }
    // Semitones from one key to another, the shorter way (up is positive, a tritone goes up).
    public static int Semitones(int from,int to){int d=HarmonyModel.Mod(to-from);return d<=6?d:d-12;}
    public static int DirectionTo(int from,int to){int d=Semitones(from,to);return d==0?0:d>0?1:-1;}

    void LateUpdate()
    {
        if(main==null)return;
        bool changing=main.KeyChanging&&main.currentKey!=main.KeyFrom;
        if(changing&&!wasChanging){anchor=main.LabelPosition(main.KeyFrom);from=main.KeyFrom;target=main.currentKey;steps=Semitones(from,target);direction=steps==0?0:steps>0?1:-1;}
        wasChanging=changing;
        float want=changing?1:0;
        shown=Main.ReducedMotion?want:Mathf.MoveTowards(shown,want,Time.unscaledDeltaTime/(changing?.2f:Fade));
        if(shown<=0||target<0){if(rail.positionCount>0){rail.positionCount=ticks.positionCount=mark.positionCount=0;foreach(var n in names)n.gameObject.SetActive(false);}return;}
        float scale=Mathf.Max(.5f,main.transform.lossyScale.x);
        // The scale slides with the turn, by the semitones between the keys, so the new key
        // arrives at the edge (the anchor) as the turn completes.
        float slide=-steps*Pitch*scale*Mathf.SmoothStep(0,1,main.KeyBlend);
        var camera=Camera.main;var side=camera!=null?camera.transform.right:Vector3.right;
        var toCentre=main.transform.position-anchor;toCentre-=Vector3.up*toCentre.y;
        var tickSide=Vector3.Dot(toCentre,side)>=0?side:-side;
        float bottom=-6.5f*Pitch*scale,top=5.5f*Pitch*scale;
        for(int i=0;i<Points;i++)points[i]=anchor+Vector3.up*(slide+Mathf.Lerp(bottom,top,i/(float)(Points-1)));
        rail.positionCount=Points;rail.SetPositions(points);
        // Most present at the edge, gone within about half a unit either way, whatever has slid there.
        for(int i=0;i<8;i++){float t=i/7f;float d=Mathf.Abs(slide+Mathf.Lerp(bottom,top,t));alphas[i]=new GradientAlphaKey(.5f*shown*Mathf.Exp(-d/(.45f*scale)),t);}
        gradient.SetKeys(new[]{new GradientColorKey(Metal,0),new GradientColorKey(Metal,1)},alphas);rail.colorGradient=gradient;ticks.colorGradient=gradient;
        // The chromatic scale: a tick and a name per semitone, the old key at zero, the keys' ticks longer.
        for(int i=0;i<12;i++)
        {
            int pc=HarmonyModel.Mod(from+i-6);int offset=i-6;float y=slide+offset*Pitch*scale;
            bool isTarget=pc==target,isFrom=pc==from;float length=(isTarget||isFrom?2f:1)*Tick*scale;
            var at=anchor+Vector3.up*y;
            tickPoints[i*3]=at;tickPoints[i*3+1]=at+tickSide*length;tickPoints[i*3+2]=at;
            var label=names[i];if(!label.gameObject.activeSelf)label.gameObject.SetActive(true);
            label.Text=main.PitchName(pc);label.transform.position=at+tickSide*(length+.04f*scale);label.Billboard();
            float a=shown*Mathf.Exp(-Mathf.Abs(y)/(.45f*scale))*(isTarget?1:.7f);
            float bright=isTarget?1.4f:1;
            label.TextField.fontMaterial.SetColor(ShaderUtilities.ID_FaceColor,new Color(Metal.r*bright,Metal.g*bright,Metal.b*bright,a));
        }
        ticks.positionCount=36;ticks.SetPositions(tickPoints);
        // The edge mark: where the torus meets the scale, which the new key is sliding toward.
        mark.positionCount=2;mark.SetPosition(0,anchor-tickSide*.05f*scale);mark.SetPosition(1,anchor+tickSide*.16f*scale);
        var c=Metal*1.2f;c.a=.7f*shown;mark.startColor=mark.endColor=c;
    }
    void OnDestroy(){if(glow!=null)Destroy(glow);}
}
