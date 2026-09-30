using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// Recording a torus-play performance, and playing it back to render videos.
//
// On the headset (torus play, Record on the palm menu) every frame is written: the head's pose,
// both hands' 26 joints and pinch, the notes sounding with their loudness, the key, and the
// key-change handles as drawn. Positions are metres from the torus's centre on the scene's own
// axes (the torus stays put; the rig moves around it), so a take replays in any session. Takes
// go to Performances/ in the app's data folder (Tools/SongLibrary/deploy_quest.py --pull-takes
// or the editor's Tools/Resonance/Quest menu copies them to Recordings/Performances).
//
// In the editor (Tools/Resonance/Quest/Render performance) a take plays back in the simulated
// headset: the notes, keys and handles come from the take, the head camera follows the recorded
// head, and glowing hands are built from the joints. The Unity Recorder films the head's view
// and an overhead camera at once (PerformanceRecorderMenu).
[DefaultExecutionOrder(1200)]
public sealed class VrPerformance : MonoBehaviour
{
    public const string Extension=".rperf";
    const int Version=1,MaxSeconds=20*60;
    VrSession session;
    public bool Recording {get;private set;}
    public float Elapsed {get;private set;}
    public string LastSaved {get;private set;}
    MemoryStream buffer;BinaryWriter writer;GameObject dot;string takePath;bool passthroughBefore;
    public static string Folder=>Path.Combine(Application.persistentDataPath,"Performances");

    void Start(){session=GetComponent<VrSession>();}

    // ---------- recording ----------
    public void Toggle(){if(Recording)Stop();else Begin();}
    void Begin()
    {
        if(session.Current!=VrSession.Mode.TorusPlay)return;
        buffer=new MemoryStream(1<<22);writer=new BinaryWriter(buffer,Encoding.UTF8);
        writer.Write(Encoding.ASCII.GetBytes("RPRF"));writer.Write(Version);writer.Write(VrSession.Scale);
        writer.Write(session.Main.currentKey);writer.Write(session.SeeThrough);writer.Write(DateTime.Now.Ticks);
        Recording=true;Elapsed=0;
        // A take is made in passthrough, and the room seen through it is captured beside it.
        takePath=Path.Combine(Folder,$"performance-{DateTime.Now:yyyyMMdd-HHmmss}{Extension}");
        try{Directory.CreateDirectory(Folder);}catch(Exception e){Debug.LogWarning("VR performance: "+e.Message);}
        passthroughBefore=session.Passthrough;if(!session.Passthrough)session.SetPassthrough(true);
        session.RoomCapture?.Begin(takePath);
        // A small red dot at the top of the view while a take runs (not part of the take).
        if(dot==null)
        {
            dot=GameObject.CreatePrimitive(PrimitiveType.Sphere);dot.name="Recording";Destroy(dot.GetComponent<Collider>());
            var m=VrButton.Panel(new Color(1,.15f,.1f,.9f));m.renderQueue=3300;m.SetFloat("_ZTest",(float)UnityEngine.Rendering.CompareFunction.Always);dot.GetComponent<Renderer>().sharedMaterial=m;
        }
        dot.transform.SetParent(session.Head.transform,false);dot.SetActive(true);
        Debug.Log("VR performance: recording");
    }
    void Stop()
    {
        Recording=false;if(dot!=null)dot.SetActive(false);
        if(writer==null)return;
        writer.Flush();
        session.RoomCapture?.Stop();
        if(!passthroughBefore&&session.Passthrough)session.SetPassthrough(false);
        try
        {
            Directory.CreateDirectory(Folder);
            LastSaved=takePath;
            File.WriteAllBytes(LastSaved,buffer.ToArray());
            Debug.Log($"VR performance: saved {LastSaved} ({Elapsed:0.0} s, {buffer.Length/1024} KB)");
        }
        catch(Exception e){Debug.LogWarning("VR performance: could not save: "+e.Message);}
        writer.Dispose();writer=null;buffer=null;
    }
    Vector3 Local(Vector3 world)=>(world-session.TorusCenter)/VrSession.Scale;
    void Write(Vector3 v){writer.Write(v.x);writer.Write(v.y);writer.Write(v.z);}
    void Write(Quaternion q){writer.Write(q.x);writer.Write(q.y);writer.Write(q.z);writer.Write(q.w);}
    void WriteHand(HandInput.Hand hand)
    {
        bool tracked=hand.Tracked&&hand.JointsValid;
        writer.Write((byte)((tracked?1:0)|(hand.Pinching?2:0)));
        if(tracked)for(int j=0;j<HandInput.Hand.JointCount;j++)Write(Local(hand.Joints[j]));
    }
    void RecordFrame()
    {
        if(session.Current!=VrSession.Mode.TorusPlay||Elapsed>MaxSeconds){Stop();return;}
        Elapsed+=Time.unscaledDeltaTime;
        writer.Write(Elapsed);
        var head=session.Head.transform;Write(Local(head.position));Write(head.rotation);
        WriteHand(session.Hands.Left);WriteHand(session.Hands.Right);
        var sounding=session.Theremin.Sounding;writer.Write((byte)sounding.Count);
        foreach(var n in sounding){writer.Write((short)n.Item1);writer.Write(n.Item2);}
        writer.Write((short)session.Main.currentKey);
        var h=session.Theremin.Handle;writer.Write((byte)((h.Shown?1:0)|(h.Held?2:0)));
        if(h.Shown){Write(Local(h.At));Write(h.Radial);Write(h.Tangent);Write(Local(h.Tether));writer.Write(h.Hint??"");}
        if(dot!=null){dot.transform.localPosition=new Vector3(-.12f,.1f,.45f)*1;dot.transform.localScale=Vector3.one*.008f;}
    }

    // ---------- the take, read back ----------
    public sealed class Frame
    {
        public float Time;public Vector3 Head;public Quaternion HeadRotation;
        public byte LeftFlags,RightFlags;public Vector3[] Left,Right;
        public List<Tuple<int,float>> Notes=new();public int Key;
        public TorusTheremin.HandleState Handle;
    }
    public sealed class Take
    {
        public float Scale;public int StartKey;public bool SeeThrough;public DateTime Taken;public readonly List<Frame> Frames=new();public float Length=>Frames.Count>0?Frames[^1].Time:0;
        // The room (VrRoomCapture), when the take has it: frame files by time, and the camera.
        public string RoomFolder;public readonly List<(float time,string file)> Room=new();public VrRoomCapture.CameraFile Camera;
        public bool HasRoom=>Room.Count>0&&Camera!=null&&Camera.fy>0;
        public float RoomVerticalFov=>HasRoom?2*Mathf.Atan(Camera.height*.5f/Camera.fy)*Mathf.Rad2Deg:0;
        public float RoomAspect=>HasRoom?Camera.width/(float)Camera.height:16f/9;
    }
    public static Take Read(string path)
    {
        using var reader=new BinaryReader(File.OpenRead(path),Encoding.UTF8);
        if(Encoding.ASCII.GetString(reader.ReadBytes(4))!="RPRF")throw new InvalidDataException("not a performance: "+path);
        int version=reader.ReadInt32();if(version!=Version)throw new InvalidDataException("performance version "+version);
        var take=new Take{Scale=reader.ReadSingle(),StartKey=reader.ReadInt32(),SeeThrough=reader.ReadBoolean(),Taken=new DateTime(reader.ReadInt64())};
        Vector3 V()=>new(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
        Vector3[] Hand(byte flags){if((flags&1)==0)return null;var joints=new Vector3[HandInput.Hand.JointCount];for(int j=0;j<joints.Length;j++)joints[j]=V();return joints;}
        while(reader.BaseStream.Position<reader.BaseStream.Length)
        {
            var f=new Frame{Time=reader.ReadSingle(),Head=V(),HeadRotation=new Quaternion(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle())};
            f.LeftFlags=reader.ReadByte();f.Left=Hand(f.LeftFlags);f.RightFlags=reader.ReadByte();f.Right=Hand(f.RightFlags);
            int notes=reader.ReadByte();for(int i=0;i<notes;i++)f.Notes.Add(Tuple.Create((int)reader.ReadInt16(),reader.ReadSingle()));
            f.Key=reader.ReadInt16();
            byte hf=reader.ReadByte();f.Handle.Shown=(hf&1)!=0;f.Handle.Held=(hf&2)!=0;
            if(f.Handle.Shown){f.Handle.At=V();f.Handle.Radial=V();f.Handle.Tangent=V();f.Handle.Tether=V();f.Handle.Hint=reader.ReadString();}
            take.Frames.Add(f);
        }
        string room=Path.ChangeExtension(path,null)+".frames",list=Path.Combine(room,"index.txt"),camera=Path.Combine(room,"camera.json");
        if(File.Exists(list)&&File.Exists(camera))
        {
            take.RoomFolder=room;take.Camera=JsonUtility.FromJson<VrRoomCapture.CameraFile>(File.ReadAllText(camera));
            foreach(var line in File.ReadAllLines(list))
            {
                var parts=line.Split(' ');if(parts.Length<2)continue;
                if(float.TryParse(parts[1],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out float t))take.Room.Add((t,Path.Combine(room,parts[0]+".jpg")));
            }
            take.Room.Sort((a,b)=>a.time.CompareTo(b.time));
        }
        return take;
    }

    // ---------- playback ----------
    public Take Playing {get;private set;}
    public float PlayTime {get;private set;}
    public bool Finished=>Playing!=null&&PlayTime>Playing.Length+1;
    public Camera Overhead {get;private set;}
    int cursor;string appliedNotes="";readonly List<Tuple<int,float>> notes=new();
    // The room behind the player's view: each captured frame on a plate far out along the
    // camera's view at the moment it was taken, sized by the camera's intrinsics, drawn first.
    // The colour camera sits a little left of the eyes' centre; its image arrives a few
    // hundredths of a second after the moment it shows.
    public const int RoomLayer=29;
    static readonly Vector3 CameraOffset=new(-.032f,-.01f,.05f);const float CameraLatency=.03f,PlateDistance=40;
    GameObject plate;Material plateMaterial;Texture2D roomTexture;int roomShown=-1;
    HandModel leftHand,rightHand;
    public void Play(Take take)
    {
        Playing=take;PlayTime=0;cursor=0;appliedNotes="";
        session.SetMode(VrSession.Mode.TorusPlay);session.SetSeeThrough(take.SeeThrough);
        session.Main.ChangeKey(take.StartKey,0);
        session.Theremin.Replaying=true;session.Hands.Replaying=true;session.Menu.Suppressed=true;
        leftHand??=new HandModel("Left hand",transform);rightHand??=new HandModel("Right hand",transform);
        if(Overhead==null)
        {
            var go=new GameObject("Overhead camera");go.transform.SetParent(transform,false);Overhead=go.AddComponent<Camera>();
            Overhead.CopyFrom(session.Head);Overhead.fieldOfView=50;Overhead.enabled=false;
            var data=UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(Overhead);
            var from=UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(session.Head);data.renderPostProcessing=from.renderPostProcessing;data.volumeLayerMask=from.volumeLayerMask;
        }
        // Above the torus, a little to the viewer's side, looking down at the ring and the hands.
        var centre=session.TorusCenter;float s=VrSession.Scale;
        Overhead.transform.position=centre+Vector3.up*2.35f*s-session.ViewDirection*.7f*s;
        Overhead.transform.rotation=Quaternion.LookRotation(centre-Overhead.transform.position,session.ViewDirection);
        if(take.HasRoom)
        {
            if(plate==null)
            {
                plate=GameObject.CreatePrimitive(PrimitiveType.Quad);plate.name="Room (captured)";Destroy(plate.GetComponent<Collider>());plate.layer=RoomLayer;plate.transform.SetParent(transform,false);
                plateMaterial=VrButton.Panel(Color.white);plateMaterial.SetFloat("_SrcBlend",(float)UnityEngine.Rendering.BlendMode.One);plateMaterial.SetFloat("_DstBlend",(float)UnityEngine.Rendering.BlendMode.Zero);
                plateMaterial.SetFloat("_ZTest",(float)UnityEngine.Rendering.CompareFunction.Always);plateMaterial.renderQueue=1000;plate.GetComponent<Renderer>().sharedMaterial=plateMaterial;
                roomTexture=new Texture2D(2,2,TextureFormat.RGB24,false);plateMaterial.mainTexture=roomTexture;
            }
            plate.SetActive(true);roomShown=-1;
            session.Head.cullingMask|=1<<RoomLayer;Overhead.cullingMask&=~(1<<RoomLayer);
        }
        else if(plate!=null)plate.SetActive(false);
        Debug.Log($"VR performance: playing {take.Length:0.0} s, {take.Frames.Count} frames{(take.HasRoom?$", room {take.Room.Count} frames":", no room")}");
    }
    public void EndPlayback()
    {
        Playing=null;
        if(session==null)return;
        session.Theremin.Replaying=false;session.Hands.Replaying=false;session.Menu.Suppressed=false;
        leftHand?.Show(false);rightHand?.Show(false);session.Main.Silence();if(plate!=null)plate.SetActive(false);
    }

    void LateUpdate()
    {
        if(session==null||session.Head==null)return;
        if(Recording&&writer!=null)RecordFrame();
        if(Playing==null)return;
        PlayTime+=Time.deltaTime;
        var frames=Playing.Frames;if(frames.Count==0)return;
        while(cursor+1<frames.Count&&frames[cursor+1].Time<=PlayTime)cursor++;
        var a=frames[cursor];var b=cursor+1<frames.Count?frames[cursor+1]:a;
        float t=b.Time>a.Time?Mathf.Clamp01((PlayTime-a.Time)/(b.Time-a.Time)):0;
        var centre=session.TorusCenter;float s=VrSession.Scale;
        Vector3 World(Vector3 local)=>centre+local*s;
        session.Head.transform.SetPositionAndRotation(World(Vector3.Lerp(a.Head,b.Head,t)),Quaternion.Slerp(a.HeadRotation,b.HeadRotation,t));
        leftHand.Pose(a.Left,b.Left,t,(a.LeftFlags&2)!=0,World);rightHand.Pose(a.Right,b.Right,t,(a.RightFlags&2)!=0,World);
        // Notes and keys exactly as they changed during the take.
        var key=new StringBuilder();foreach(var n in a.Notes)key.Append(n.Item1).Append(':').Append(n.Item2.ToString("0.00")).Append(' ');
        if(key.ToString()!=appliedNotes){appliedNotes=key.ToString();notes.Clear();notes.AddRange(a.Notes);session.Main.SetNotes(notes,true);}
        if(a.Key!=session.Main.currentKey)session.Main.ChangeKey(a.Key,.7f);
        var h=a.Handle;if(h.Shown){h.At=World(h.At);h.Tether=World(h.Tether);}
        session.Theremin.ShowReplay(h);
        if(Playing.HasRoom)ShowRoom(World);
    }
    void ShowRoom(Func<Vector3,Vector3> world)
    {
        var room=Playing.Room;int i=roomShown<0?0:roomShown;
        while(i+1<room.Count&&room[i+1].time<=PlayTime)i++;
        if(i!=roomShown){roomShown=i;try{roomTexture.LoadImage(File.ReadAllBytes(room[i].file),false);}catch(Exception e){Debug.LogWarning("VR performance: room frame: "+e.Message);}}
        // Where the camera was when this frame was taken.
        HeadAt(room[i].time-CameraLatency,out var head,out var rotation);
        float s=VrSession.Scale,d=PlateDistance;var k=Playing.Camera;
        var camera=world(head)+rotation*CameraOffset*s;
        float w=d*k.width/k.fx,hgt=d*k.height/k.fy,ox=d*(k.cx-k.width*.5f)/k.fx,oy=d*(k.height*.5f-k.cy)/k.fy;
        plate.transform.SetPositionAndRotation(camera+rotation*new Vector3(-ox,-oy,d),rotation);
        plate.transform.localScale=new Vector3(w,hgt,1);
    }
    void HeadAt(float time,out Vector3 position,out Quaternion rotation)
    {
        var frames=Playing.Frames;int lo=0,hi=frames.Count-1;
        while(lo<hi){int mid=(lo+hi+1)/2;if(frames[mid].Time<=time)lo=mid;else hi=mid-1;}
        var a=frames[lo];var b=lo+1<frames.Count?frames[lo+1]:a;float t=b.Time>a.Time?Mathf.Clamp01((time-a.Time)/(b.Time-a.Time)):0;
        position=Vector3.Lerp(a.Head,b.Head,t);rotation=Quaternion.Slerp(a.HeadRotation,b.HeadRotation,t);
    }
    void OnDestroy(){if(Recording)Stop();if(plateMaterial!=null)Destroy(plateMaterial);if(roomTexture!=null)Destroy(roomTexture);}

    // Glowing hands from the joints: a bead at every joint and a stroke along every bone; the
    // index and thumb tips brighter, brighter still while pinching.
    sealed class HandModel
    {
        static readonly int[][] Chains={new[]{0,2,3,4,5},new[]{0,6,7,8,9,10},new[]{0,11,12,13,14,15},new[]{0,16,17,18,19,20},new[]{0,21,22,23,24,25}};
        readonly Transform root;readonly Transform[] beads=new Transform[HandInput.Hand.JointCount];readonly LineRenderer[] bones=new LineRenderer[Chains.Length];
        readonly Vector3[] joints=new Vector3[HandInput.Hand.JointCount];readonly Material bead,tip,line;
        public HandModel(string name,Transform parent)
        {
            root=new GameObject(name).transform;root.SetParent(parent,false);
            bead=VrButton.Panel(new Color(.7f,.92f,1f,.85f));tip=VrButton.Panel(new Color(1,1,1,.95f));
            line=new Material(Resources.Load<Shader>("HarmonicGlow"));line.SetColor("_BaseColor",new Color(.55f,.85f,1f)*1.4f);
            for(int j=0;j<beads.Length;j++)
            {
                var go=GameObject.CreatePrimitive(PrimitiveType.Sphere);go.name="Joint "+j;UnityEngine.Object.Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(root,false);go.GetComponent<Renderer>().sharedMaterial=j==5||j==10?tip:bead;beads[j]=go.transform;
            }
            for(int c=0;c<Chains.Length;c++)
            {
                var go=new GameObject("Finger "+c);go.transform.SetParent(root,false);var l=go.AddComponent<LineRenderer>();
                l.sharedMaterial=line;l.useWorldSpace=true;l.positionCount=Chains[c].Length;l.numCapVertices=4;l.numCornerVertices=2;bones[c]=l;
            }
            Show(false);
        }
        public void Show(bool on){if(root.gameObject.activeSelf!=on)root.gameObject.SetActive(on);}
        public void Pose(Vector3[] a,Vector3[] b,float t,bool pinching,Func<Vector3,Vector3> world)
        {
            if(a==null){Show(false);return;}
            Show(true);float s=VrSession.Scale;
            for(int j=0;j<joints.Length;j++)joints[j]=world(b!=null?Vector3.Lerp(a[j],b[j],t):a[j]);
            for(int j=0;j<beads.Length;j++)
            {
                beads[j].position=joints[j];
                float size=j==5||j==10?(pinching?.017f:.012f):j<=1?.014f:.009f;beads[j].localScale=Vector3.one*size*s;
            }
            for(int c=0;c<Chains.Length;c++){var l=bones[c];l.widthMultiplier=.007f*s;for(int k=0;k<Chains[c].Length;k++)l.SetPosition(k,joints[Chains[c][k]]);}
        }
    }
}
