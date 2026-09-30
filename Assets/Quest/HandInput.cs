using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

// Hands in the headset (XR Hands on OpenXR), in world space: each hand's index and thumb tips,
// palm pose, pinch, and whether the palm faces the head. Joint poses arrive in the rig's
// tracking space (metres) and are carried into the world through the rig's transform, which
// is scaled, so everything here is in scene units. Pinches have hysteresis so a held pinch does
// not flicker. Without a headset (the editor's simulated session) the right hand follows the
// mouse on a plane in front of the head and the left mouse button pinches.
public sealed class HandInput : MonoBehaviour
{
    public sealed class Hand
    {
        public bool Tracked, Pinching, PinchStarted, PinchEnded, PalmToward;
        public Vector3 Index, Thumb, Palm, PalmNormal, PreviousIndex;
        public Quaternion PalmRotation=Quaternion.identity;
    }
    public readonly Hand Left=new(),Right=new();
    public IEnumerable<Hand> Both{get{yield return Left;yield return Right;}}
    public Transform Rig,Head;
    public float Scale=1;          // scene units per metre (the rig's scale)
    public bool Simulated;
    // The simulated hand's reach in front of the head, metres, and whether the left palm faces up.
    public float SimulatedDepth=.5f;public bool SimulatedPalm;public bool SimulatedPinch;public Vector2? SimulatedScreen;
    XRHandSubsystem subsystem;readonly List<XRHandSubsystem> found=new();
    const float PinchOn=.018f,PinchOff=.032f;
    readonly List<GameObject> tips=new();Material tipMaterial;

    void Start()
    {
        tipMaterial=new Material(Resources.Load<Shader>("VrPanel"));tipMaterial.SetColor("_Color",new Color(.75f,.95f,1f,.9f));
        tipMaterial.SetFloat("_SrcBlend",(float)UnityEngine.Rendering.BlendMode.SrcAlpha);tipMaterial.SetFloat("_DstBlend",(float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        for(int i=0;i<4;i++)
        {
            var tip=GameObject.CreatePrimitive(PrimitiveType.Sphere);tip.name="Fingertip";Destroy(tip.GetComponent<Collider>());
            tip.GetComponent<Renderer>().sharedMaterial=tipMaterial;tip.SetActive(false);tips.Add(tip);
        }
    }
    void Update()
    {
        if(Simulated)SimulateRight();else ReadHands();
        // Fingertip markers (index and thumb of each hand): the only sign of the hands in blackout.
        int t=0;
        foreach(var h in Both)
        {
            foreach(var p in new[]{h.Index,h.Thumb})
            {
                var tip=tips[t++];tip.SetActive(h.Tracked);
                if(h.Tracked){tip.transform.position=p;tip.transform.localScale=Vector3.one*.011f*Scale*(h.Pinching?1.4f:1);}
            }
        }
    }
    void ReadHands()
    {
        if(subsystem==null||!subsystem.running)
        {
            SubsystemManager.GetSubsystems(found);subsystem=null;
            foreach(var s in found)if(s.running){subsystem=s;break;}
        }
        Read(subsystem?.leftHand,Left);Read(subsystem?.rightHand,Right);
    }
    void Read(XRHand? source,Hand hand)
    {
        hand.PreviousIndex=hand.Index;hand.PinchStarted=hand.PinchEnded=false;
        if(source==null||!source.Value.isTracked||Rig==null){if(hand.Pinching)hand.PinchEnded=true;hand.Tracked=hand.Pinching=false;return;}
        var h=source.Value;
        if(!h.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var index)||!h.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumb)||!h.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm))
        {hand.Tracked=false;return;}
        bool wasTracked=hand.Tracked;hand.Tracked=true;
        hand.Index=Rig.TransformPoint(index.position);hand.Thumb=Rig.TransformPoint(thumb.position);hand.Palm=Rig.TransformPoint(palm.position);
        hand.PalmRotation=Rig.rotation*palm.rotation;
        // The palm faces along the joint's down axis (its up axis points out of the back of the hand).
        hand.PalmNormal=hand.PalmRotation*Vector3.down;
        if(!wasTracked)hand.PreviousIndex=hand.Index;
        Pinch(hand,Vector3.Distance(index.position,thumb.position));
        Facing(hand);
    }
    void Pinch(Hand hand,float metres)
    {
        bool was=hand.Pinching;
        hand.Pinching=was?metres<PinchOff:metres<PinchOn;
        hand.PinchStarted=hand.Pinching&&!was;hand.PinchEnded=!hand.Pinching&&was;
    }
    void Facing(Hand hand)
    {
        if(Head==null){hand.PalmToward=false;return;}
        var toHead=(Head.position-hand.Palm).normalized;float d=Vector3.Dot(hand.PalmNormal,toHead);
        hand.PalmToward=hand.PalmToward?d>.45f:d>.65f;
    }
    // The editor's stand-in: the right index tip on the mouse ray at a fixed reach; the left palm
    // held up (menu showing) at the lower left of the view while SimulatedPalm is set.
    void SimulateRight()
    {
        var camera=Head!=null?Head.GetComponent<Camera>():null;if(camera==null)return;
        var r=Right;r.PreviousIndex=r.Index;r.PinchStarted=r.PinchEnded=false;
        Vector2 screen=SimulatedScreen??(Vector2)(UnityEngine.InputSystem.Mouse.current?.position.ReadValue()??Vector2.zero);
        var ray=camera.ScreenPointToRay(screen);
        bool press=SimulatedPinch||(UnityEngine.InputSystem.Mouse.current?.leftButton.isPressed??false);
        r.Tracked=true;r.Index=ray.GetPoint(SimulatedDepth*Scale*(press?1.06f:1));r.Thumb=r.Index+Head.right*.03f*Scale*(press?.2f:1);
        r.Palm=r.Index-ray.direction*.08f*Scale;r.PalmNormal=ray.direction;
        bool was=r.Pinching;r.Pinching=press;r.PinchStarted=press&&!was;r.PinchEnded=!press&&was;
        var l=Left;l.PinchStarted=l.PinchEnded=false;l.Tracked=SimulatedPalm;
        if(SimulatedPalm)
        {
            l.Palm=Head.position+Head.forward*.38f*Scale-Head.up*.16f*Scale-Head.right*.14f*Scale;
            l.PalmNormal=(Head.position-l.Palm).normalized;l.PalmToward=true;l.Index=l.Palm+Head.up*.08f*Scale;l.Thumb=l.Palm+Head.right*.05f*Scale;
            l.PalmRotation=Quaternion.LookRotation(Head.up,-l.PalmNormal);
        }
        else l.PalmToward=false;
    }
    void OnDestroy(){if(tipMaterial!=null)Destroy(tipMaterial);foreach(var t in tips)if(t!=null)Destroy(t);}
}
