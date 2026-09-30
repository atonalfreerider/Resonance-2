using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Torus play (headset): trace a fingertip over the torus and the note nearest it sounds, louder
// the closer the finger is, like a theremin but in discrete notes: it only ever jumps from one
// note to the next, never bends, and a new note has to be clearly nearer before it takes over.
// Each hand plays its own note. On the torus's outer rim, where there are no notes, small handles
// appear under the hand: pinch there and drag around the ring (circumferentially) to turn the key
// by a fifth per step (clockwise seen from above: up a fifth), or drag up or down around the tube
// (toroidally) to turn it by a major third (up: a third up). Each step twists the torus into the
// new key; keep dragging for more steps.
public sealed class TorusTheremin : MonoBehaviour
{
    VrSession session;Main main;HandInput hands;
    const float S=VrSession.Scale;
    const float Reach=.045f,Switch=.8f,Step=.05f;   // metres; a new note must be this fraction as far to take over
    sealed class Voice {public int Note=-1;public float Amplitude;}
    readonly Voice left=new(),right=new();
    readonly List<System.Tuple<int,float>> sounding=new();int lastA=-2,lastB=-2;float lastAmpA,lastAmpB;
    // Handles.
    Transform handle;LineRenderer ring,around,updown;TextBox hint;Material glow;
    HandInput.Hand grabbing,lastOnRim;Vector3 grabFrom,grabTangent,lastRadial;float rimRadius,lastOnRimTime=-1;
    const float GrabGrace=.3f;   // seconds: pinching pulls the fingertip in a little, off the rim
    public string Hint=>hint!=null&&hint.gameObject.activeSelf?hint.TextField.text:"";
    public int LeftNote=>left.Note;public int RightNote=>right.Note;
    public bool HandleShown=>handle!=null&&handle.gameObject.activeSelf;

    void Start()
    {
        session=GetComponent<VrSession>();main=session.Main;hands=session.Hands;
        glow=new Material(Resources.Load<Shader>("HarmonicGlowOverlay"));glow.SetColor("_BaseColor",Color.white*1.6f);
        handle=new GameObject("Key handles").transform;handle.SetParent(transform,false);
        ring=Line("Rim grip",.012f);around=Line("Around the ring · fifths",.016f);updown=Line("Around the tube · thirds",.016f);
        hint=TextBox.Create("",TextAlignmentOptions.Center);hint.transform.SetParent(handle,false);hint.Size=.8f;hint.Color=new Color(.9f,.95f,1f);hint.TextField.fontMaterial.renderQueue=3200;
        handle.gameObject.SetActive(false);
    }
    LineRenderer Line(string name,float width)
    {
        var go=new GameObject(name);go.transform.SetParent(handle,false);var l=go.AddComponent<LineRenderer>();l.sharedMaterial=glow;l.useWorldSpace=true;l.widthMultiplier=width*S;l.numCapVertices=3;return l;
    }
    void Update()
    {
        if(session==null||main==null)return;
        if(session.Current!=VrSession.Mode.TorusPlay){if(left.Note>=0||right.Note>=0){left.Note=right.Note=-1;Sound();}handle.gameObject.SetActive(false);grabbing=null;return;}
        rimRadius=RimRadius();
        Play(hands.Left,left);Play(hands.Right,right);Sound();
        Handles();
    }
    // The outermost horizontal reach of the notes: the rim of the torus.
    float RimRadius()
    {
        float r=0;var c=session.TorusCenter;
        for(int i=0;i<main.NoteCount;i++){var d=main.NotePosition(i)-c;d.y=0;r=Mathf.Max(r,d.magnitude);}
        return r;
    }
    void Play(HandInput.Hand hand,Voice voice)
    {
        if(!hand.Tracked||hand==grabbing){voice.Note=-1;voice.Amplitude=0;return;}
        int best=-1;float bestD=float.MaxValue,currentD=float.MaxValue;
        for(int i=0;i<main.NoteCount;i++)
        {
            float d=Vector3.Distance(hand.Index,main.NotePosition(i));
            if(d<bestD){bestD=d;best=i;}
            if(i==voice.Note)currentD=d;
        }
        float reach=Reach*S;
        // Discrete: hold the note playing until another is clearly nearer, or the finger leaves.
        int note=voice.Note>=0&&currentD<reach&&!(bestD<currentD*Switch)?voice.Note:bestD<reach?best:-1;
        float d0=note==voice.Note?currentD:bestD;
        float target=note<0?0:Mathf.Pow(Mathf.Clamp01(1-d0/reach),1.3f);
        voice.Amplitude=note==voice.Note?Mathf.Lerp(voice.Amplitude,target,1-Mathf.Exp(-Time.unscaledDeltaTime*20)):target;
        voice.Note=note;
    }
    void Sound()
    {
        // Only when something audible changed: every call restarts the synth's voices.
        bool changed=left.Note!=lastA||right.Note!=lastB||Mathf.Abs(left.Amplitude-lastAmpA)>.03f||Mathf.Abs(right.Amplitude-lastAmpB)>.03f;
        if(!changed)return;
        lastA=left.Note;lastB=right.Note;lastAmpA=left.Amplitude;lastAmpB=right.Amplitude;
        sounding.Clear();
        if(left.Note>=0&&left.Amplitude>.01f)sounding.Add(System.Tuple.Create(left.Note,left.Amplitude));
        if(right.Note>=0&&right.Amplitude>.01f)sounding.Add(System.Tuple.Create(right.Note,right.Amplitude));
        main.SetNotes(sounding,true);
    }
    // The rim zone: just outside the notes' outermost reach, about the torus's height.
    bool OnRim(HandInput.Hand hand,out Vector3 radial)
    {
        var d=hand.Index-session.TorusCenter;float y=d.y;d.y=0;float r=d.magnitude;radial=r>1e-4f?d/r:Vector3.forward;
        if(!hand.Tracked||rimRadius<=0)return false;
        bool zone=r>rimRadius*1.0f&&r<rimRadius+.1f*S&&Mathf.Abs(y)<.09f*S;
        if(!zone)return false;
        for(int i=0;i<main.NoteCount;i++)if(Vector3.Distance(hand.Index,main.NotePosition(i))<Reach*S*.7f)return false;
        return true;
    }
    void Handles()
    {
        HandInput.Hand near=null;Vector3 radial=Vector3.forward;
        if(grabbing!=null){if(!grabbing.Tracked||!grabbing.Pinching){grabbing=null;}else{near=grabbing;var d=grabbing.Index-session.TorusCenter;d.y=0;radial=d.normalized;}}
        if(near==null)foreach(var h in hands.Both)if(OnRim(h,out var r)){near=h;radial=r;lastOnRim=h;lastRadial=r;lastOnRimTime=Time.unscaledTime;break;}
        // A pinch that begins just after the finger left the rim still takes the handle.
        if(near==null&&grabbing==null&&lastOnRim!=null&&lastOnRim.Tracked&&lastOnRim.PinchStarted&&Time.unscaledTime-lastOnRimTime<GrabGrace){near=lastOnRim;radial=lastRadial;}
        if(near==null){if(handle.gameObject.activeSelf&&Time.unscaledTime-lastOnRimTime>GrabGrace)handle.gameObject.SetActive(false);return;}
        if(!handle.gameObject.activeSelf)handle.gameObject.SetActive(true);
        var centre=session.TorusCenter;var at=centre+radial*(rimRadius+.03f*S);
        // Clockwise seen from above is the tangent that turns right when looking down.
        var tangent=Vector3.Cross(radial,Vector3.up).normalized;
        if(grabbing==null&&near.PinchStarted){grabbing=near;grabFrom=near.Index;grabTangent=tangent;}
        Draw(at,radial,tangent);
        if(grabbing!=null)
        {
            var delta=grabbing.Index-grabFrom;float along=Vector3.Dot(delta,grabTangent)/S,vertical=delta.y/S;
            if(Mathf.Abs(along)>=Step&&Mathf.Abs(along)>=Mathf.Abs(vertical)){Turn(along>0?7:-7);grabFrom=grabbing.Index;}
            else if(Mathf.Abs(vertical)>=Step){Turn(vertical>0?4:-4);grabFrom=grabbing.Index;}
            hint.Text=Preview(along,vertical);
        }
        else hint.Text="pinch · drag ↻ fifths · ↕ thirds";
        hint.transform.position=at+Vector3.up*.07f*S;hint.Billboard();
    }
    string Preview(float along,float vertical)
    {
        int key=main.currentKey;string N(int k)=>main.PitchName(HarmonyModel.Mod(k));
        // Until the drag picks a direction, both destinations.
        if(Mathf.Max(Mathf.Abs(along),Mathf.Abs(vertical))<Step*.25f)return $"↻ {N(key+7)}  ·  ↑ {N(key+4)}";
        if(Mathf.Abs(along)>=Mathf.Abs(vertical))return along>=0?$"↻ {N(key+7)}  (up a fifth)":$"↺ {N(key-7)}  (down a fifth)";
        return vertical>=0?$"↑ {N(key+4)}  (up a third)":$"↓ {N(key-4)}  (down a third)";
    }
    void Turn(int semitones){main.ChangeKey(main.currentKey+semitones,.7f);}
    readonly Vector3[] points=new Vector3[24];
    void Draw(Vector3 at,Vector3 radial,Vector3 tangent)
    {
        float s=S;var up=Vector3.up;
        // A small grip ring on the rim, an arc along the ring (fifths) and one up and over the tube (thirds).
        ring.positionCount=17;for(int i=0;i<17;i++){float a=i/16f*Mathf.PI*2;ring.SetPosition(i,at+(tangent*Mathf.Cos(a)+up*Mathf.Sin(a))*.012f*s);}
        around.positionCount=13;for(int i=0;i<13;i++){float u=(i/12f-.5f)*.14f*s;around.SetPosition(i,at+tangent*u-radial*(u*u/(rimRadius+.03f*s))*.5f);}
        updown.positionCount=13;for(int i=0;i<13;i++){float a=(i/12f-.5f)*1.6f;updown.SetPosition(i,at-radial*.05f*s+(radial*Mathf.Cos(a)+up*Mathf.Sin(a))*.05f*s);}
        float live=grabbing!=null?1.8f:1;var c=new Color(.7f,.9f,1f)*live;c.a=.9f;
        ring.startColor=ring.endColor=around.startColor=around.endColor=updown.startColor=updown.endColor=c;
    }
    void OnDestroy(){if(glow!=null)Destroy(glow);}
}
