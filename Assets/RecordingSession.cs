using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// The runtime half of video recording (the editor's Tools → Resonance → Record menu drives the
// Unity Recorder around it): recording mode on, the stacked layout for a vertical video or the
// side-by-side one for a horizontal video, the tutorial or a song's tour made ready, then played
// when the recorder starts (Go: the tutorial from the top, a tour from its first cue), and its steps
// (tutorial) or cues (tour) logged with their times in the video. The report JSON beside the video is
// what Tools/Video/cut_shorts.py cuts clips from, on step boundaries, and what Tools/Video/edit_package.py
// builds the editor's package from.
public sealed class RecordingSession : MonoBehaviour
{
    [Serializable] public sealed class Marker {public string id="",title="";public double start,end;}
    // bundle: the song's folder (tours).
    // songStart: the song seconds at the take's first played frame, where the tour starts (its first cue); -1 when no
    // song played (a tutorial, or a tour whose song never started).
    // audioStart: the video seconds at which song time 0 sounds, so song time t is heard at audioStart+t (tours; the
    // median of video time minus song time over the first seconds of play). Negative when the take starts mid-song;
    // -1 when songStart is -1 (older reports have no songStart, and there -1 alone means unknown).
    // synthStem: the app's synth sound captured during a tutorial take (RecordingStemTap), beside the video, starting
    // at the video's first frame.
    // What the take played, so the editor's package can tell a bundle rebuilt since: storySha256, the SHA-256 of the
    // story.json bytes the tour played; narrationSha256, narration.json's sha256 of the voice track loaded for it (""
    // when the narration was off or not ready as the take started); narrationPlayed, whether that voice spoke.
    // duckLagFrames: how many frames after SongNarration sets a frame's music gain (the ducking) the music plays at it:
    // 0, as the music's volumes are set in LateUpdate (reports without the field were made with 1, and a stem's
    // crossfade then also began a frame after its cue; now both start in the frame they are decided).
    [Serializable] public sealed class Report {public string kind="",song="",bundle="",orientation="",video="",synthStem="",storySha256="",narrationSha256="";public int width,height,fps,duckLagFrames=0;public double duration,songStart=-1,audioStart=-1;public bool narrationPlayed;public List<Marker> markers=new();public string error="";}
    public enum State {Idle,Preparing,Ready,Playing,Done}
    public State Current {get;private set;}=State.Idle;
    public string Error {get;private set;}="";
    public string ReportPath {get;private set;}="";
    public static string Folder=>Path.GetFullPath(Path.Combine(Application.dataPath,"../Recordings/Videos"));
    Report report;float startTime;bool go;bool verticalBefore;RecordingStemTap tap;

    // Make the tutorial (song null) or a song's tour ready to record.
    public void Prepare(string song,bool vertical){if(Current is State.Idle or State.Done)StartCoroutine(Run(song,vertical));}
    // The recorder is running: play; video time starts now.
    public void Go(string videoPath,int fps){if(Current!=State.Ready)return;report.video=Path.GetFileName(videoPath);report.fps=fps;ReportPath=Path.ChangeExtension(videoPath,".json");startTime=Time.time;tap?.Begin();go=true;}
    double Now=>Time.time-startTime;

    IEnumerator Run(string songTitle,bool vertical)
    {
        Current=State.Preparing;Error="";go=false;
        var views=GetComponent<VisualizationViews>();var library=GetComponent<SongLibraryPanel>();
        var tutorial=GetComponent<TutorialDirector>();var director=GetComponent<SongDirector>();var midi=GetComponent<MidiPlayer>();var narration=GetComponent<SongNarration>();
        verticalBefore=views.Vertical;views.SetVertical(vertical);RecordingMode.Set(true);views.SetPanelHidden(true);library.SetOpen(false);
        report=new Report{kind=songTitle==null?"tutorial":"tour",song=songTitle??"",orientation=vertical?"vertical":"horizontal",width=Screen.width&~1,height=Screen.height&~1};
        // The tutorial's own sound (synth notes) exists in no file: capture it for the editor's package.
        var synth=GetComponent<Main>()?.Synth;
        tap=songTitle==null&&synth!=null?(synth.GetComponent<RecordingStemTap>()??synth.gameObject.AddComponent<RecordingStemTap>()):null;
        if(songTitle!=null)
        {
            var song=library.Songs.FirstOrDefault(s=>string.Equals(s.Title,songTitle,StringComparison.OrdinalIgnoreCase))??library.Songs.FirstOrDefault(s=>s.Title.StartsWith(songTitle,StringComparison.OrdinalIgnoreCase));
            if(song==null||!song.Tour){Fail("No tour for "+songTitle,views);yield break;}
            report.bundle=Path.GetFullPath(song.Folder);
            // A tour rebuilt since its song loaded (new cues, or a new voice) is read again: the take plays what is on disk.
            if(Rebuilt(director,narration,report.bundle))director.Reload();
            director.Tour(song);
            float waited=0;while(!director.Directing&&waited<150){waited+=Time.unscaledDeltaTime;yield return null;}
            if(!director.Directing){Fail("The tour did not start",views);yield break;}
            midi.Pause();
            // MidiPlayer.Play does nothing while the recording's audio or a stem is still loading: be sure both are
            // idle before the Recorder starts, or the take films a song that never plays.
            var audio=GetComponent<SongAudio>();var stems=GetComponent<StemPlayback>();waited=0;
            while(waited<120&&(!midi.Loaded||(audio!=null&&audio.Busy)||(stems!=null&&stems.IsLoading))){waited+=Time.unscaledDeltaTime;yield return null;}
            if(!midi.Loaded||(audio!=null&&audio.Busy)||(stems!=null&&stems.IsLoading)){Fail("The song's audio or stems did not finish loading",views);yield break;}
            // The take starts where the tour does, at its first cue, so no silent head plays before the music; after
            // the loads, which rewind a song to 0 as they finish.
            midi.Seek(director.TourStart);
        }
        yield return new WaitForSecondsRealtime(.5f);
        Current=State.Ready;
        while(!go)yield return null;
        Current=State.Playing;
        if(songTitle!=null){report.storySha256=director.StorySha256;report.narrationSha256=narration!=null&&narration.Ready&&narration.Enabled?narration.Sha256:"";}
        Marker marker=null;void Mark(string id,string title){if(marker!=null)marker.end=Now;marker=new Marker{id=id,title=title,start=Now};report.markers.Add(marker);}
        if(songTitle==null)
        {
            tutorial.Play();int step=-2;
            while(tutorial.Playing||step==-2){if(tutorial.StepIndex>=0&&tutorial.StepIndex!=step){step=tutorial.StepIndex;Mark(tutorial.StepId,tutorial.StepTitle);}if(step==-2&&Now>20)break;yield return null;}
        }
        else
        {
            // Play, and keep asking until the song really plays (a late stem load can refuse the first request).
            midi.Play();float asked=0;
            while(!midi.IsPlaying&&director.Directing&&asked<30){asked+=Time.unscaledDeltaTime;yield return null;midi.Play();}
            if(!midi.IsPlaying){report.error="The song did not start playing";Debug.LogError("Recording: "+report.error);director.SetDirecting(false);}
            else report.songStart=midi.ClockPosition;
            int cue=-2;var offsets=new List<double>();double furthest=0,first=double.NaN;
            // The take ends with the tour, and in any case once the song has played to its end and stopped.
            while(director.Directing&&!(furthest>=midi.Duration-1&&!midi.IsPlaying))
            {
                if(midi.IsPlaying)furthest=Math.Max(furthest,midi.Position);
                if(director.CueIndex>=0&&director.CueIndex!=cue){cue=director.CueIndex;Mark("cue-"+cue,director.CurrentCue?.text??"");}
                report.narrationPlayed|=narration!=null&&narration.Speaking;
                // Where the song's audio clock sits in the video: video time minus song time, while it plays (from a
                // quarter second in; the first audible frame stands in should the song stop sooner).
                if(midi.IsAudible&&double.IsNaN(first))first=Now-midi.Position;
                if(midi.IsPlaying&&midi.Position>report.songStart+.25&&offsets.Count<150)offsets.Add(Now-midi.Position);
                yield return null;
            }
            if(offsets.Count>0){offsets.Sort();report.audioStart=offsets[offsets.Count/2];}
            else if(!double.IsNaN(first))report.audioStart=first;
        }
        if(marker!=null)marker.end=Now;
        yield return new WaitForSeconds(.4f);
        report.duration=Now;
        // Whatever fails while the take's files are written, the take still ends (Done is what stops the Recorder),
        // and the failure goes into the report and the console.
        try
        {
            if(tap!=null&&tap.Capturing)
            {
                string stem=Path.ChangeExtension(ReportPath,".synth.wav");
                try{if(tap.End(stem))report.synthStem=Path.GetFileName(stem);}
                catch(Exception e){Problem("the synth capture was not saved: "+e.Message);}
            }
            Directory.CreateDirectory(Folder);File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true));
        }
        catch(Exception e){Problem("the report was not saved: "+e.Message);}
        finally{RecordingMode.Set(false);views.SetVertical(verticalBefore);Current=State.Done;}
    }
    void Problem(string message){report.error=report.error.Length>0?report.error+"; "+message:message;Error=report.error;Debug.LogError("Recording: "+message);}
    void Fail(string message,VisualizationViews views){Error=message;RecordingMode.Set(false);views.SetVertical(verticalBefore);Current=State.Done;}
    // Whether the loaded song's tour was rebuilt on disk since it was read: its story.json, or the voice its
    // narration.json names. (Another song's tour is read fresh as that song loads.)
    static bool Rebuilt(SongDirector director,SongNarration narration,string bundle)
    {
        if(!director.Available||!string.Equals(director.Folder,bundle,StringComparison.OrdinalIgnoreCase))return false;
        string story=Path.Combine(bundle,"story.json"),voice=Path.Combine(bundle,"narration.json");
        if(!File.Exists(story)||Sha256(story)!=director.StorySha256)return true;
        if(narration==null||narration.Pending||!File.Exists(voice))return false;
        try{return !narration.Ready||JsonUtility.FromJson<SongNarration.Manifest>(File.ReadAllText(voice))?.sha256!=narration.Sha256;}
        catch(Exception){return true;}
    }
    static string Sha256(string path){using var h=System.Security.Cryptography.SHA256.Create();using var s=File.OpenRead(path);return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
}
