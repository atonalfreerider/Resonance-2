using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;
using Debug=UnityEngine.Debug;

// Videos of a torus-play performance recorded in the headset (VrPerformance).
//  Pull performances from the headset — copies the takes over USB to Recordings/Performances.
//  Render newest performance — pulls, then plays the newest take in the simulated headset and
//  films two videos at once with the Unity Recorder, both with the sound: the player's own view
//  (the recorded head) and an overhead view of the torus with the glowing hands playing it.
//  1920×1080 at a constant 30 fps, H.264, in Recordings/Videos, the sound brought to a steady
//  loudness with ffmpeg when it is on the PATH.
static class PerformanceRecorderMenu
{
    const int Fps=30,Width=1920,Height=1080;
    const string Package="com.primitive.resonance",Request="Resonance.PerformanceRequest",WasSimulating="Resonance.PerformanceWasSimulating";
    static string Root=>Path.GetFullPath(Path.Combine(Application.dataPath,".."));
    static string Takes=>Path.Combine(Root,"Recordings","Performances");
    static string Videos=>Path.Combine(Root,"Recordings","Videos");

    [MenuItem("Tools/Resonance/Quest/Pull performances from headset")]
    static void PullMenu(){int n=Pull();Debug.Log(n<0?"Performances: no headset on adb.":$"Performances: {n} take(s) in {Takes}");}
    [MenuItem("Tools/Resonance/Quest/Render newest performance · POV + overhead")]
    static void Render()
    {
        Pull();
        var newest=Directory.Exists(Takes)?new DirectoryInfo(Takes).GetFiles("*"+VrPerformance.Extension).OrderByDescending(f=>f.LastWriteTimeUtc).FirstOrDefault():null;
        if(newest==null){Debug.LogError($"Performances: no takes in {Takes}. Record one in torus play (palm menu, Record) first.");return;}
        SessionState.SetString(Request,newest.FullName);
        SessionState.SetBool(WasSimulating,EditorPrefs.GetBool(VrSession.SimulateKey,false));
        EditorPrefs.SetBool(VrSession.SimulateKey,true);
        if(!EditorApplication.isPlaying)EditorApplication.isPlaying=true;else Wait();
    }
    static void Wait(){deadline=EditorApplication.timeSinceStartup+90;EditorApplication.update-=WhenReady;EditorApplication.update+=WhenReady;}
    // Entering play mode reloads the editor's scripts: the request survives in SessionState.
    [InitializeOnLoadMethod] static void Hook()
    {
        EditorApplication.playModeStateChanged+=state=>{if(state==PlayModeStateChange.EnteredPlayMode&&SessionState.GetString(Request,"").Length>0)Wait();};
    }

    // adb pull of the headset's Performances folder; the number of takes here, or -1 without a headset.
    static int Pull()
    {
        string adb=FindAdb();if(adb==null)return -1;
        Directory.CreateDirectory(Takes);
        string remote=$"/sdcard/Android/data/{Package}/files/Performances/.";
        var p=Process.Start(new ProcessStartInfo(adb,$"pull \"{remote}\" \"{Takes}\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true});
        string output=p.StandardOutput.ReadToEnd()+p.StandardError.ReadToEnd();p.WaitForExit();
        if(p.ExitCode!=0&&!output.Contains("pulled")){Debug.LogWarning("Performances: adb pull: "+output.Trim());return output.Contains("no devices")?-1:Directory.GetFiles(Takes,"*"+VrPerformance.Extension).Length;}
        return Directory.GetFiles(Takes,"*"+VrPerformance.Extension).Length;
    }
    static string FindAdb()
    {
        string editor=Path.GetDirectoryName(EditorApplication.applicationPath);
        string bundled=Path.Combine(editor,"Data","PlaybackEngines","AndroidPlayer","SDK","platform-tools","adb.exe");
        return File.Exists(bundled)?bundled:null;
    }

    static double deadline;static RecorderController controller;static RenderTexture pov,overhead;static VrPerformance player;static string baseName;
    static void WhenReady()
    {
        if(!EditorApplication.isPlaying){if(EditorApplication.timeSinceStartup>deadline)Fail("play mode did not start");return;}
        var session=VrSession.Instance;
        if(session==null||session.Performance==null||session.Theremin==null||session.Menu==null||session.Hands==null){if(EditorApplication.timeSinceStartup>deadline)Fail("the simulated headset did not start");return;}
        EditorApplication.update-=WhenReady;
        string path=SessionState.GetString(Request,"");SessionState.EraseString(Request);
        VrPerformance.Take take;
        try{take=VrPerformance.Read(path);}catch(Exception e){Fail(e.Message);return;}
        player=session.Performance;player.Play(take);
        pov=new RenderTexture(Width,Height,24,RenderTextureFormat.ARGB32){name="Performance · player's view"};pov.Create();
        overhead=new RenderTexture(Width,Height,24,RenderTextureFormat.ARGB32){name="Performance · overhead"};overhead.Create();
        session.Head.targetTexture=pov;session.Head.fieldOfView=70;
        player.Overhead.targetTexture=overhead;player.Overhead.enabled=true;
        baseName=Path.Combine(Videos,Path.GetFileNameWithoutExtension(path));Directory.CreateDirectory(Videos);
        var settings=ScriptableObject.CreateInstance<RecorderControllerSettings>();
        settings.AddRecorderSettings(Movie("Player's view",pov,baseName+"-pov"));
        settings.AddRecorderSettings(Movie("Overhead",overhead,baseName+"-overhead"));
        settings.SetRecordModeToManual();settings.FrameRatePlayback=FrameRatePlayback.Constant;settings.FrameRate=Fps;settings.CapFrameRate=true;
        controller=new RecorderController(settings);controller.PrepareRecording();controller.StartRecording();
        Debug.Log($"Performances: rendering {take.Length:0.0} s from {Path.GetFileName(path)}");
        EditorApplication.update+=WhenDone;
    }
    static MovieRecorderSettings Movie(string name,RenderTexture source,string file)
    {
        var movie=ScriptableObject.CreateInstance<MovieRecorderSettings>();
        movie.name=name;movie.Enabled=true;
        movie.EncoderSettings=new CoreEncoderSettings{Codec=CoreEncoderSettings.OutputCodec.MP4,EncodingQuality=CoreEncoderSettings.VideoEncodingQuality.High};
        movie.ImageInputSettings=new RenderTextureInputSettings{RenderTexture=source};
        movie.AudioInputSettings.PreserveAudio=true;movie.OutputFile=file;
        return movie;
    }
    static void WhenDone()
    {
        if(player!=null&&!player.Finished&&EditorApplication.isPlaying)return;
        EditorApplication.update-=WhenDone;
        controller?.StopRecording();controller=null;
        if(VrSession.Instance!=null&&VrSession.Instance.Head!=null)VrSession.Instance.Head.targetTexture=null;
        if(player!=null&&player.Overhead!=null){player.Overhead.targetTexture=null;player.Overhead.enabled=false;}
        player?.EndPlayback();
        string name=baseName;
        EditorApplication.delayCall+=Finish;
        // The synth plays quietly: each video's sound is brought to a steady loudness (the picture
        // is copied untouched), once the Recorder has closed the files.
        EditorApplication.delayCall+=()=>{foreach(var view in new[]{"-pov","-overhead"})Normalize(name+view+".mp4");Debug.Log($"Performances: saved {name}-pov.mp4 and {name}-overhead.mp4");};
    }
    static void Normalize(string video)
    {
        if(!File.Exists(video))return;
        string loud=Path.ChangeExtension(video,".loud.mp4");
        try
        {
            var p=Process.Start(new ProcessStartInfo("ffmpeg",$"-v error -y -i \"{video}\" -c:v copy -af loudnorm=I=-16:TP=-1.5:LRA=11 -c:a aac -b:a 192k \"{loud}\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true});
            string error=p.StandardError.ReadToEnd();p.WaitForExit();
            if(p.ExitCode==0&&File.Exists(loud)){File.Delete(video);File.Move(loud,video);}
            else Debug.LogWarning("Performances: loudness not adjusted: "+error.Trim());
        }
        catch(Exception e){Debug.LogWarning("Performances: loudness not adjusted (ffmpeg on the PATH?): "+e.Message);}
    }
    static void Fail(string why){EditorApplication.update-=WhenReady;Debug.LogError("Performances: "+why);Finish();}
    static void Finish()
    {
        EditorPrefs.SetBool(VrSession.SimulateKey,SessionState.GetBool(WasSimulating,false));
        if(EditorApplication.isPlaying)EditorApplication.isPlaying=false;
        foreach(var rt in new[]{pov,overhead})if(rt!=null){rt.Release();UnityEngine.Object.DestroyImmediate(rt);}
        pov=overhead=null;player=null;
    }
}
