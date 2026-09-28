using TMPro;
using UnityEngine;

// The scale a key change is measured on: while the torus turns into a new key, a height
// scale stands through its tonic label along the y axis, a thin rule with ticks (a longer one
// every fifth), in the pattern wheels' metal, faint, most present where it meets the torus's
// edge and fading quickly above and below, with the new key named on the side the change goes
// (up is sharpward on the circle of fifths, down flatward). The rack stands where the label was as the change began and stays put,
// sliding up or down with the turn as the torus rolls along it; it never turns with the torus.
// It fades once the torus is locked into the new key. Tonicizations no longer move the torus,
// so they raise no rack.
[DefaultExecutionOrder(90)]
public sealed class KeyShiftIndicator : MonoBehaviour
{
    Main main;LineRenderer rail,teeth,chevron;TextBox label;Material glow;
    float shown;int target=-1,direction;Vector3 anchor;bool wasChanging;
    const float Travel=.5f;
    const float Reach=1.6f,Fade=.5f,Pitch=.1f,Tick=.045f;const int Points=41;
    static readonly Color Metal=new(.5f,.72f,.74f);
    readonly Vector3[] points=new Vector3[Points];Vector3[] rack=System.Array.Empty<Vector3>();Gradient gradient,toothGradient;float gradientShown=-1;
    // For validation: whether it stands, and where it points.
    public bool Visible=>shown>.05f;
    public int Direction=>direction;
    public int Target=>target;
    public float Alpha=>shown;

    void Awake()
    {
        main=GetComponent<Main>();
        glow=new Material(Resources.Load<Shader>("HarmonicGlow"));glow.SetColor("_BaseColor",Color.white);glow.renderQueue=3105;
        rail=Line("Key shift · scale",.011f);teeth=Line("Key shift · ticks",.008f);teeth.numCapVertices=0;teeth.numCornerVertices=0;chevron=Line("Key shift · mark",.014f);chevron.numCapVertices=0;
        label=TextBox.Create("",TextAlignmentOptions.Center);label.transform.SetParent(transform,false);label.Size=.36f;label.TextField.fontStyle=FontStyles.Bold;
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
        bool changing=main.KeyChanging&&main.currentKey!=main.KeyFrom;
        if(changing&&!wasChanging){anchor=main.LabelPosition(main.KeyFrom);target=main.currentKey;direction=DirectionTo(main.KeyFrom,main.currentKey);}
        wasChanging=changing;
        float want=changing?1:0;
        shown=Main.ReducedMotion?want:Mathf.MoveTowards(shown,want,Time.unscaledDeltaTime/(changing?.2f:Fade));
        if(shown<=0||target<0){if(rail.positionCount>0){rail.positionCount=teeth.positionCount=chevron.positionCount=0;label.gameObject.SetActive(false);}return;}
        // The rack stands where the label of the key being left was as the change began, and
        // slides with the turn: the torus rolls along it.
        float scale=Mathf.Max(.5f,main.transform.lossyScale.x);
        var stand=anchor+Vector3.up*(direction*Travel*scale*Mathf.SmoothStep(0,1,main.KeyBlend));
        var camera=Camera.main;var side=camera!=null?camera.transform.right:Vector3.right;
        // The teeth face the torus: toward its centre, as seen from the camera.
        var toCentre=main.transform.position-stand;toCentre-=Vector3.up*toCentre.y;
        var toothSide=(Vector3.Dot(toCentre,side)>=0?side:-side);
        for(int i=0;i<Points;i++)points[i]=stand+Vector3.up*((i/(float)(Points-1)*2-1)*Reach*scale);
        rail.positionCount=Points;rail.SetPositions(points);
        // The ticks: one per pitch along the rule, every fifth longer, one thin zigzag that
        // returns to the rule between ticks.
        int count=Mathf.RoundToInt(2*Reach/Pitch)+1;if(rack.Length!=count*3)rack=new Vector3[count*3];
        for(int i=0;i<count;i++)
        {
            float y0=(-Reach+i*Pitch)*scale;float length=(i%5==0?2.2f:1)*Tick*scale;
            rack[i*3]=stand+Vector3.up*y0;rack[i*3+1]=stand+Vector3.up*y0+toothSide*length;rack[i*3+2]=stand+Vector3.up*y0;
        }
        teeth.positionCount=rack.Length;teeth.SetPositions(rack);
        if(gradientShown!=shown)
        {
            gradientShown=shown;gradient??=new Gradient();toothGradient??=new Gradient();
            // Most present at the torus's edge, gone within about half a unit either way.
            var alphas=new GradientAlphaKey[8];var toothAlphas=new GradientAlphaKey[8];
            for(int i=0;i<8;i++){float t=i/7f;float d=Mathf.Abs(t*2-1)*Reach;float a=shown*Mathf.Exp(-d/.45f);alphas[i]=new GradientAlphaKey(.5f*a,t);toothAlphas[i]=new GradientAlphaKey(.45f*a,t);}
            gradient.SetKeys(new[]{new GradientColorKey(Metal,0),new GradientColorKey(Metal,1)},alphas);rail.colorGradient=gradient;
            toothGradient.SetKeys(new[]{new GradientColorKey(Metal,0),new GradientColorKey(Metal,1)},toothAlphas);teeth.colorGradient=toothGradient;
        }
        // A mark on the side the change goes, with the key it goes to.
        var at=stand+Vector3.up*.55f*scale*direction;float w=.06f*scale;
        chevron.positionCount=2;chevron.SetPosition(0,at-toothSide*w);chevron.SetPosition(1,at+toothSide*w*2);
        var c=Metal;c.a=.6f*shown;chevron.startColor=chevron.endColor=c;
        if(!label.gameObject.activeSelf)label.gameObject.SetActive(true);
        label.Text=main.PitchName(target);
        label.transform.position=at+Vector3.up*.2f*scale*direction;label.Billboard();
        label.TextField.fontMaterial.SetColor(ShaderUtilities.ID_FaceColor,new Color(Metal.r,Metal.g,Metal.b,.6f*shown));
    }
    void OnDestroy(){if(glow!=null)Destroy(glow);}
}
