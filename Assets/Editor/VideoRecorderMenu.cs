using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;

// Tools → Resonance → Record: videos of the tutorial or of the loaded song's tour, vertical
// (1080x1920, for Shorts and Reels) or horizontal (1920x1080), with the Unity Recorder. The
// Recorder renders every frame at a constant 30 fps with the audio rendered in step
// (AudioRenderer), so the picture never drops frames or drifts from the sound. The app is put in
// recording mode (no controls, captions raised in a vertical frame), made ready, and played from
// the top as the Recorder starts; when the tour ends the Recorder stops. For the tutorial, the
// Shorts and the full tour are then cut from the video (Tools/Video/cut_shorts.py).
// Files: Recordings/Videos/<name>-<orientation>-<time>.mp4, with a JSON of step times beside it.
static class VideoRecorderMenu
{
    const string Request="Resonance.RecordRequest";
    const int Fps=30;
    [MenuItem("Tools/Resonance/Record/Tour of the loaded song · vertical")] static void TourVertical()=>Begin(LoadedSong(),true);
    [MenuItem("Tools/Resonance/Record/Tour of the loaded song · horizontal")] static void TourHorizontal()=>Begin(LoadedSong(),false);

    // For scripts and the editor bridge: song "" records the tutorial.
    public static void Begin(string song,bool vertical)
    {
        if(song==null){UnityEngine.Debug.LogWarning("Recording: load a song with a tour first.");return;}
        SessionState.SetString(Request,(vertical?"v":"h")+"|"+song);
        if(!EditorApplication.isPlaying)EditorApplication.isPlaying=true;else Wait();
    }
    static string LoadedSong()
    {
        if(!EditorApplication.isPlaying)return SessionState.GetString("Resonance.LastTourSong","")is var s&&s.Length>0?s:null;
        var midi=UnityEngine.Object.FindAnyObjectByType<MidiPlayer>();var library=UnityEngine.Object.FindAnyObjectByType<SongLibraryPanel>();
        if(midi==null||library==null)return null;
        var song=library.Songs.Find(x=>string.Equals(Path.GetFullPath(x.Score),Path.GetFullPath(midi.midiPath??""),StringComparison.OrdinalIgnoreCase));
        if(song!=null)SessionState.SetString("Resonance.LastTourSong",song.Title);
        return song?.Title;
    }
    [InitializeOnLoadMethod] static void Hook()
    {
        EditorApplication.playModeStateChanged+=state=>{if(state==PlayModeStateChange.EnteredPlayMode&&SessionState.GetString(Request,"").Length>0)Wait();};
    }

    static RecorderController controller;static RecordingSession session;static string video;static double deadline;
    static void Wait(){deadline=EditorApplication.timeSinceStartup+60;EditorApplication.update-=WhenAppReady;EditorApplication.update+=WhenAppReady;}
    // Once the app has bound its components, prepare the session in the chosen orientation.
    static void WhenAppReady()
    {
        session=UnityEngine.Object.FindAnyObjectByType<RecordingSession>();var library=UnityEngine.Object.FindAnyObjectByType<SongLibraryPanel>();
        if(session==null||library==null||library.Songs.Count==0){if(EditorApplication.timeSinceStartup>deadline){EditorApplication.update-=WhenAppReady;UnityEngine.Debug.LogError("Recording: the app did not start.");}return;}
        EditorApplication.update-=WhenAppReady;
        string[] request=SessionState.GetString(Request,"").Split('|');SessionState.EraseString(Request);
        bool vertical=request[0]=="v";string song=request.Length>1&&request[1].Length>0?request[1]:null;
        SetUpRecorder(vertical,song);
        session.Prepare(song,vertical);
        EditorApplication.update+=WhenSessionReady;
    }
    static void SetUpRecorder(bool vertical,string song)
    {
        var settings=ScriptableObject.CreateInstance<RecorderControllerSettings>();
        var movie=ScriptableObject.CreateInstance<MovieRecorderSettings>();
        movie.name="Resonance video";movie.Enabled=true;
        movie.EncoderSettings=new CoreEncoderSettings{Codec=CoreEncoderSettings.OutputCodec.MP4,EncodingQuality=CoreEncoderSettings.VideoEncodingQuality.High};
        movie.ImageInputSettings=new GameViewInputSettings{OutputWidth=vertical?1080:1920,OutputHeight=vertical?1920:1080};
        movie.AudioInputSettings.PreserveAudio=true;
        string name=song==null?"tutorial":Slug(song);
        video=Path.Combine(RecordingSession.Folder,$"{name}-{(vertical?"vertical":"horizontal")}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(RecordingSession.Folder);
        movie.OutputFile=video;
        settings.AddRecorderSettings(movie);settings.SetRecordModeToManual();
        settings.FrameRatePlayback=FrameRatePlayback.Constant;settings.FrameRate=Fps;settings.CapFrameRate=true;
        controller=new RecorderController(settings);
    }
    static void WhenSessionReady()
    {
        if(session==null){EditorApplication.update-=WhenSessionReady;return;}
        if(session.Current==RecordingSession.State.Done){EditorApplication.update-=WhenSessionReady;UnityEngine.Debug.LogError("Recording: "+session.Error);return;}
        if(session.Current!=RecordingSession.State.Ready)return;
        EditorApplication.update-=WhenSessionReady;
        controller.PrepareRecording();controller.StartRecording();
        session.Go(video+".mp4",Fps);
        EditorApplication.update+=WhenSessionDone;
    }
    static void WhenSessionDone()
    {
        if(session!=null&&session.Current!=RecordingSession.State.Done)return;
        EditorApplication.update-=WhenSessionDone;
        controller.StopRecording();
        string report=session!=null?session.ReportPath:"";
        UnityEngine.Debug.Log($"Recording saved: {video}.mp4");
        // The tutorial: cut the Shorts and the full tour once the file is closed.
        if(File.Exists(report)&&File.ReadAllText(report).Contains("\"kind\": \"tutorial\""))
            EditorApplication.delayCall+=()=>CutShorts(report);
    }
    static void CutShorts(string report)
    {
        string root=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
        string python=Path.Combine(root,"Tools/SongLibrary/.venv/Scripts/python.exe"),script=Path.Combine(root,"Tools/Video/cut_shorts.py");
        if(!File.Exists(python)||!File.Exists(script))return;
        var process=Process.Start(new ProcessStartInfo(python,$"\"{script}\" \"{report}\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true});
        process.EnableRaisingEvents=true;
        process.Exited+=(_,__)=>{string output=process.StandardOutput.ReadToEnd()+process.StandardError.ReadToEnd();EditorApplication.delayCall+=()=>UnityEngine.Debug.Log("Shorts: "+output);};
    }
    static string Slug(string s){var chars=s.ToLowerInvariant().ToCharArray();for(int i=0;i<chars.Length;i++)if(!char.IsLetterOrDigit(chars[i]))chars[i]='-';return new string(chars).Trim('-');}
}
