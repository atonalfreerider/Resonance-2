using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

// Driving the headset app from the computer over USB, for testing without hands: the app reads
// vr-commands.txt in its data folder (pushed with adb), runs each line and deletes the file.
//   passthrough on|off · mode song|torus · load <part of a song folder> · play · pause ·
//   seek <seconds> · recenter · dump · probe · sweep · post on|off · msaa 1|2|4 · eyescale <0.5–1>
// "probe" checks what passthrough and the lyric line depend on: the eye camera's picture
// rendered once into a texture (how much of it is transparent, with and without post-processing),
// every composition layer with its order and blending, and whether the lyric texture has content.
// "sweep" measures what each part of the picture costs: with a song playing it records the
// app's own GPU and CPU frame times with everything on, then with each suspect switched off in
// turn (post-processing, MSAA, eye resolution, the lyric strip's camera, and every group of
// renderers sharing a shader), and logs the savings largest first (logcat tag Unity, "VR perf")
// and writes them to vr-perf.txt beside the commands.
public sealed class VrCommands : MonoBehaviour
{
    VrSession session;float poll;bool sweeping;
    string Folder=>Application.persistentDataPath;
    readonly FrameTiming[] timing=new FrameTiming[1];

    void Start(){session=GetComponent<VrSession>();}
    void Update()
    {
        FrameTimingManager.CaptureFrameTimings();
        if((poll-=Time.unscaledDeltaTime)>0)return;poll=.5f;
        string path=Path.Combine(Folder,"vr-commands.txt");
        if(!File.Exists(path))return;
        string[] lines;
        try{lines=File.ReadAllLines(path);File.Delete(path);}
        catch(Exception e){Debug.LogWarning("VR command file unreadable: "+e.Message);try{File.Delete(path);}catch{}return;}
        foreach(var raw in lines){var line=raw.Trim();if(line.Length>0)Run(line);}
    }
    void Run(string line)
    {
        Debug.Log("VR command: "+line);
        var parts=line.Split(new[]{' '},2);string verb=parts[0].ToLowerInvariant(),arg=parts.Length>1?parts[1].Trim():"";
        try
        {
            switch(verb)
            {
                case "passthrough":session.SetPassthrough(arg!="off");break;
                case "mode":session.SetMode(arg.StartsWith("torus")?VrSession.Mode.TorusPlay:VrSession.Mode.Song);break;
                case "load":Load(arg);break;
                case "play":session.Midi.Play();break;
                case "pause":session.Midi.Pause();break;
                case "seek":session.Midi.Seek(double.Parse(arg,System.Globalization.CultureInfo.InvariantCulture));break;
                case "recenter":session.Recenter();break;
                case "dump":Debug.Log("VR dump: "+session.Diagnostics());break;
                case "post":session.Head.GetUniversalAdditionalCameraData().renderPostProcessing=arg!="off";break;
                case "msaa":if(Pipeline!=null)Pipeline.msaaSampleCount=int.Parse(arg);break;
                case "eyescale":XRSettings.eyeTextureResolutionScale=float.Parse(arg,System.Globalization.CultureInfo.InvariantCulture);break;
                case "sweep":if(!sweeping)StartCoroutine(Sweep());break;
                case "probe":Probe();break;
                default:Debug.LogWarning("VR command unknown: "+line);break;
            }
        }
        catch(Exception e){Debug.LogWarning($"VR command failed: {line}: {e.Message}");}
    }
    static UniversalRenderPipelineAsset Pipeline=>GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
    bool Load(string part)
    {
        var song=SongLibraryPanel.Scan().FirstOrDefault(s=>string.IsNullOrEmpty(part)||s.Folder.IndexOf(part,StringComparison.OrdinalIgnoreCase)>=0);
        if(song==null){Debug.LogWarning("VR command: no song matching "+part);return false;}
        session.SetMode(VrSession.Mode.Song);session.Audio.LoadPair("",song.Score);GetComponent<VrMenu>()?.ShowSongs(false);return true;
    }

    // ---------- the probe ----------
    void Probe()
    {
        var head=session.Head;var text=new StringBuilder();
        foreach(bool post in new[]{true,false})
        {
            var go=new GameObject("Alpha probe camera");var cam=go.AddComponent<Camera>();
            cam.CopyFrom(head);cam.stereoTargetEye=StereoTargetEyeMask.None;cam.fieldOfView=90;cam.aspect=1;cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=Color.clear;   // as in passthrough
            go.transform.SetPositionAndRotation(head.transform.position,head.transform.rotation);
            var data=cam.GetUniversalAdditionalCameraData();var from=head.GetUniversalAdditionalCameraData();
            data.renderPostProcessing=post;data.volumeLayerMask=from.volumeLayerMask;data.antialiasing=from.antialiasing;
            var rt=new RenderTexture(256,256,24,RenderTextureFormat.ARGB32){antiAliasing=1};rt.Create();cam.targetTexture=rt;
            cam.Render();
            var stats=Stats(rt);text.Append($"eye picture (post {(post?"on":"off")}): {stats}; ");
            cam.targetTexture=null;rt.Release();Destroy(rt);Destroy(go);
        }
        var output=session.Lyrics!=null?session.Lyrics.Output as RenderTexture:null;
        text.Append(output!=null?$"lyric texture {output.width}x{output.height} {output.format}: {Stats(output)}; ":"lyric texture none; ");
        var card=session.LyricCard;
        if(card!=null){var v=head.WorldToViewportPoint(card.transform.position);text.Append($"lyric card active {card.activeInHierarchy} at viewport ({v.x:0.00},{v.y:0.00}) {v.z/VrSession.Scale:0.00} m, scale {card.transform.lossyScale/VrSession.Scale}; ");}
        foreach(var layer in FindObjectsByType<Unity.XR.CompositionLayers.CompositionLayer>(FindObjectsInactive.Include))
            text.Append($"layer {layer.name} order {layer.Order} {layer.LayerData?.GetType().Name} blend {layer.LayerData?.BlendType} enabled {layer.isActiveAndEnabled}; ");
        Debug.Log("VR probe: "+text);
    }
    // How much of a texture is transparent (alpha under 0.05), its mean alpha and brightest pixel.
    static string Stats(RenderTexture rt)
    {
        var previous=RenderTexture.active;RenderTexture.active=rt;
        var read=new Texture2D(rt.width,rt.height,TextureFormat.RGBAFloat,false);read.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);read.Apply();
        RenderTexture.active=previous;
        var px=read.GetPixels();int clear=0;double alpha=0;float bright=0;
        foreach(var c in px){if(c.a<.05f)clear++;alpha+=c.a;bright=Mathf.Max(bright,Mathf.Max(c.r,Mathf.Max(c.g,c.b)));}
        Destroy(read);
        return $"{100f*clear/px.Length:0}% transparent, mean alpha {alpha/px.Length:0.00}, brightest {bright:0.00}";
    }

    // ---------- the sweep ----------
    struct Sample{public double Gpu,Main,Render;public int Frames;}
    IEnumerator Measure(Action<Sample> done,int settle=45,int frames=120)
    {
        for(int i=0;i<settle;i++)yield return null;
        double gpu=0,main=0,render=0;int n=0,g=0;
        for(int i=0;i<frames;i++)
        {
            yield return null;
            if(FrameTimingManager.GetLatestTimings(1,timing)<1)continue;
            var t=timing[0];n++;main+=t.cpuMainThreadFrameTime-t.cpuMainThreadPresentWaitTime;render+=t.cpuRenderThreadFrameTime;
            if(t.gpuFrameTime>0){gpu+=t.gpuFrameTime;g++;}
        }
        done(new Sample{Gpu=g>0?gpu/g:-1,Main=n>0?main/n:-1,Render=n>0?render/n:-1,Frames=n});
    }
    IEnumerator Sweep()
    {
        sweeping=true;
        // A song playing in song mode, the heaviest ordinary state.
        if(session.Current!=VrSession.Mode.Song)session.SetMode(VrSession.Mode.Song);
        if(!session.Midi.Loaded&&!Load(""))  {sweeping=false;yield break;}
        while(session.Audio.Busy||!session.Midi.Loaded)yield return null;
        if(!session.Midi.IsPlaying)session.Midi.Play();
        for(int i=0;i<90;i++)yield return null;
        var results=new List<(string name,Sample s)>();Sample baseline=default;
        Debug.Log("VR perf: begin baseline");
        yield return Measure(s=>baseline=s);
        results.Add(("everything on",baseline));
        var data=session.Head.GetUniversalAdditionalCameraData();
        IEnumerator Variant(string name,Action off,Action on)
        {
            Debug.Log("VR perf: begin "+name);
            off();Sample s=default;yield return Measure(x=>s=x);on();results.Add((name,s));
            for(int i=0;i<20;i++)yield return null;
        }
        yield return Variant("no post-processing",()=>data.renderPostProcessing=false,()=>data.renderPostProcessing=true);
        int msaa=Pipeline!=null?Pipeline.msaaSampleCount:1;
        if(msaa>1)yield return Variant("no MSAA",()=>Pipeline.msaaSampleCount=1,()=>Pipeline.msaaSampleCount=msaa);
        float eye=XRSettings.eyeTextureResolutionScale;
        yield return Variant("eye resolution 0.8",()=>XRSettings.eyeTextureResolutionScale=.8f,()=>XRSettings.eyeTextureResolutionScale=eye);
        var stage=Camera.allCameras.FirstOrDefault(c=>c.targetTexture!=null&&c.targetTexture.name.StartsWith("Lyric strip"));
        if(stage!=null)yield return Variant("no lyric strip camera",()=>session.Lyrics.enabled=false,()=>session.Lyrics.enabled=true);
        // Every group of visible renderers sharing a shader, switched off together.
        var groups=FindObjectsByType<Renderer>(FindObjectsInactive.Exclude)
            .Where(r=>r.enabled&&r.isVisible&&r.sharedMaterial!=null&&r.gameObject.layer!=30&&r.gameObject.layer!=31)
            .GroupBy(r=>r.sharedMaterial.shader.name).OrderByDescending(g=>g.Count()).ToList();
        foreach(var group in groups)
        {
            var renderers=group.ToList();
            yield return Variant($"no {group.Key} ({renderers.Count} renderers)",()=>{foreach(var r in renderers)if(r!=null)r.enabled=false;},()=>{foreach(var r in renderers)if(r!=null)r.enabled=true;});
        }
        // Largest GPU saving first.
        var text=new StringBuilder();
        text.AppendLine($"VR perf · {DateTime.Now:yyyy-MM-dd HH:mm} · {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} per eye · msaa {msaa} · {Application.targetFrameRate} target");
        text.AppendLine($"baseline: gpu {baseline.Gpu:0.00} ms · main thread {baseline.Main:0.00} ms · render thread {baseline.Render:0.00} ms · {baseline.Frames} frames");
        foreach(var (name,s) in results.Skip(1).OrderByDescending(r=>baseline.Gpu-r.s.Gpu))
            text.AppendLine($"{baseline.Gpu-s.Gpu,6:+0.00;-0.00} ms gpu · {baseline.Main-s.Main,6:+0.00;-0.00} ms main · {baseline.Render-s.Render,6:+0.00;-0.00} ms render · {name}");
        foreach(var line in text.ToString().Split('\n'))if(line.Trim().Length>0)Debug.Log("VR perf: "+line.TrimEnd());
        try{File.WriteAllText(Path.Combine(Folder,"vr-perf.txt"),text.ToString());}catch(Exception e){Debug.LogWarning("VR perf file: "+e.Message);}
        sweeping=false;
    }
}
