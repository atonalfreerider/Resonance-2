using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

// The room behind a torus-play take: while a take records, the headset's colour passthrough
// camera (Meta's Passthrough Camera API, with the headset-camera permission) is saved frame by
// frame beside the take, so the player's-view video can show the room as the player saw it.
//
// Frames are converted from the camera's YUV on the camera's own path (ConvertAsync), copied into
// reused buffers and compressed to JPEG on a worker thread, so a take costs the frame little.
// Beside take.rperf: take.frames/000001.jpg …, index.txt (frame number and time into the take)
// and camera.json (the camera's size and intrinsics in the saved frames' pixels).
public sealed class VrRoomCapture : MonoBehaviour
{
    public const string Permission="horizonos.permission.HEADSET_CAMERA";
    const int Width=960,MaxFps=30,Quality=80;
    VrSession session;string folder;bool capturing;
    XRCpuImage.AsyncConversion conversion;bool converting;float conversionTime;double lastTimestamp=-1;float lastCapture=-1;
    int written,pending,frameNumber;readonly List<string> index=new();readonly object gate=new();
    readonly Stack<byte[]> pool=new();Vector2Int size;bool cameraSaved;
    public int Frames=>written;
    public bool Available {get;private set;}

    void Start()
    {
        session=GetComponent<VrSession>();
#if UNITY_ANDROID && !UNITY_EDITOR
        if(!UnityEngine.Android.Permission.HasUserAuthorizedPermission(Permission))
        {
            var callbacks=new PermissionCallbacks();
            callbacks.PermissionGranted+=_=>{Debug.Log("VR room capture: camera permission granted");session.RestartPassthrough();};
            callbacks.PermissionDenied+=_=>Debug.LogWarning("VR room capture: camera permission denied; takes will have no room");
            UnityEngine.Android.Permission.RequestUserPermission(Permission,callbacks);
        }
#endif
    }

    public void Begin(string takePath)
    {
        folder=Path.ChangeExtension(takePath,null)+".frames";
        try{Directory.CreateDirectory(folder);}catch(Exception e){Debug.LogWarning("VR room capture: "+e.Message);return;}
        lock(gate){index.Clear();written=0;}frameNumber=0;cameraSaved=false;lastTimestamp=-1;lastCapture=-1;capturing=true;
        Available=session.CameraManager!=null&&session.CameraManager.enabled&&session.CameraManager.subsystem!=null&&session.CameraManager.subsystem.running;
        if(!Available)Debug.LogWarning("VR room capture: the passthrough camera is not running; this take will have no room");
    }
    public void Stop()
    {
        capturing=false;
        // The index is written once the last frames are on disk.
        StartCoroutine(Finish(folder));
    }
    System.Collections.IEnumerator Finish(string into)
    {
        float waited=0;while((pending>0||converting)&&waited<5){waited+=Time.unscaledDeltaTime;yield return null;}
        string text;lock(gate){index.Sort(StringComparer.Ordinal);text=string.Join("\n",index);}
        try{File.WriteAllText(Path.Combine(into,"index.txt"),text);}catch(Exception e){Debug.LogWarning("VR room capture: "+e.Message);}
        Debug.Log($"VR room capture: {written} frames in {into}");
    }

    void Update()
    {
        if(converting)Collect();
        if(!capturing||converting)return;
        var manager=session.CameraManager;var performance=session.Performance;
        if(manager==null||!manager.enabled||performance==null||!performance.Recording)return;
        float now=performance.Elapsed;if(lastCapture>=0&&now-lastCapture<1f/MaxFps)return;
        if(!manager.TryAcquireLatestCpuImage(out var image))return;
        using(image)
        {
            if(image.timestamp==lastTimestamp)return;
            lastTimestamp=image.timestamp;lastCapture=now;
            int height=Mathf.RoundToInt(Width*image.height/(float)image.width);size=new Vector2Int(Width,height);
            if(!cameraSaved)SaveCamera(manager,image.width,image.height);
            // Rows bottom first, as a texture holds them, so the JPEG comes out upright.
            var parameters=new XRCpuImage.ConversionParams(image,TextureFormat.RGB24,XRCpuImage.Transformation.MirrorY){outputDimensions=size};
            conversion=image.ConvertAsync(parameters);converting=true;conversionTime=now;
        }
    }
    void Collect()
    {
        var status=conversion.status;
        if(status==XRCpuImage.AsyncConversionStatus.Pending||status==XRCpuImage.AsyncConversionStatus.Processing)return;
        converting=false;
        if(status!=XRCpuImage.AsyncConversionStatus.Ready){conversion.Dispose();return;}
        var data=conversion.GetData<byte>();int length=data.Length;
        byte[] buffer;lock(pool)buffer=pool.Count>0&&pool.Peek().Length==length?pool.Pop():new byte[length];
        NativeArray<byte>.Copy(data,buffer,length);conversion.Dispose();
        int number=++frameNumber;float time=conversionTime;var dims=size;string into=folder;
        Interlocked.Increment(ref pending);
        Task.Run(()=>
        {
            try
            {
                var jpeg=ImageConversion.EncodeArrayToJPG(buffer,GraphicsFormat.R8G8B8_SRGB,(uint)dims.x,(uint)dims.y,0,Quality);
                File.WriteAllBytes(Path.Combine(into,$"{number:000000}.jpg"),jpeg);
                lock(gate){index.Add($"{number:000000} {time.ToString("0.0000",System.Globalization.CultureInfo.InvariantCulture)}");written++;}
            }
            catch(Exception e){Debug.LogWarning("VR room capture: frame "+number+": "+e.Message);}
            finally{lock(pool)if(pool.Count<6)pool.Push(buffer);Interlocked.Decrement(ref pending);}
        });
    }
    // The camera's intrinsics, scaled to the saved frames' pixels.
    void SaveCamera(ARCameraManager manager,int imageWidth,int imageHeight)
    {
        cameraSaved=true;
        if(!manager.TryGetIntrinsics(out var k)){Debug.LogWarning("VR room capture: no camera intrinsics");return;}
        float scale=Width/(float)Math.Max(1,k.resolution.x);
        var camera=new CameraFile{width=size.x,height=size.y,fx=k.focalLength.x*scale,fy=k.focalLength.y*scale,cx=k.principalPoint.x*scale,cy=k.principalPoint.y*scale};
        try{File.WriteAllText(Path.Combine(folder,"camera.json"),JsonUtility.ToJson(camera,true));}catch(Exception e){Debug.LogWarning("VR room capture: "+e.Message);}
        Debug.Log($"VR room capture: camera {k.resolution.x}x{k.resolution.y} (image {imageWidth}x{imageHeight}), saved at {size.x}x{size.y}, fx {camera.fx:0.0} fy {camera.fy:0.0}");
    }
    [Serializable] public sealed class CameraFile{public int width,height;public float fx,fy,cx,cy;}
    void OnDestroy(){if(converting)conversion.Dispose();}
}
