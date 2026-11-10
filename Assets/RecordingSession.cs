using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// The runtime half of video recording (the editor's Tools → Resonance → Record menu drives the
// Unity Recorder around it): recording mode on, the stacked layout for a vertical video or the
// side-by-side one for a horizontal video, the tutorial or a song's tour made ready, then played
// from the top when the recorder starts (Go), and its steps (tutorial) or cues (tour) logged with
// their times in the video. The report JSON beside the video is what Tools/Video/cut_shorts.py
// cuts clips from, on step boundaries.
public sealed class RecordingSession : MonoBehaviour
{
    [Serializable] public sealed class Marker {public string id="",title="";public double start,end;}
    // bundle: the song's folder (tours). audioStart: the video time at which the song's audio is at 0 (tours; the
    // median over the first seconds of play, -1 when unknown). synthStem: the app's synth sound captured during a
    // tutorial take (RecordingStemTap), beside the video, starting at the video's first frame.
    [Serializable] public sealed class Report {public string kind="",song="",bundle="",orientation="",video="",synthStem="";public int width,height,fps;public double duration,audioStart=-1;public List<Marker> markers=new();public string error="";}
    public enum State {Idle,Preparing,Ready,Playing,Done}
    public State Current {get;private set;}=State.Idle;
    public string Error {get;private set;}="";
    public string ReportPath {get;private set;}="";
    public static string Folder=>Path.GetFullPath(Path.Combine(Application.dataPath,"../Recordings/Videos"));
    Report report;float startTime;bool go;bool verticalBefore;RecordingStemTap tap;

    // Make the tutorial (song null) or a song's tour ready to record.
    public void Prepare(string song,bool vertical){if(Current is State.Idle or State.Done)StartCoroutine(Run(song,vertical));}
    // The recorder is running: play from the top; video time starts now.
    public void Go(string videoPath,int fps){if(Current!=State.Ready)return;report.video=Path.GetFileName(videoPath);report.fps=fps;ReportPath=Path.ChangeExtension(videoPath,".json");startTime=Time.time;tap?.Begin();go=true;}
    double Now=>Time.time-startTime;

    IEnumerator Run(string songTitle,bool vertical)
    {
        Current=State.Preparing;Error="";go=false;
        var views=GetComponent<VisualizationViews>();var library=GetComponent<SongLibraryPanel>();
        var tutorial=GetComponent<TutorialDirector>();var director=GetComponent<SongDirector>();var midi=GetComponent<MidiPlayer>();
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
            director.Tour(song);
            float waited=0;while(!director.Directing&&waited<150){waited+=Time.unscaledDeltaTime;yield return null;}
            if(!director.Directing){Fail("The tour did not start",views);yield break;}
            midi.Pause();midi.Seek(0);
            // MidiPlayer.Play does nothing while the recording's audio or a stem is still loading: be sure both are
            // idle before the Recorder starts, or the take films a song that never plays.
            var audio=GetComponent<SongAudio>();var stems=GetComponent<StemPlayback>();waited=0;
            while(waited<120&&(!midi.Loaded||(audio!=null&&audio.Busy)||(stems!=null&&stems.IsLoading))){waited+=Time.unscaledDeltaTime;yield return null;}
            if(!midi.Loaded||(audio!=null&&audio.Busy)||(stems!=null&&stems.IsLoading)){Fail("The song's audio or stems did not finish loading",views);yield break;}
        }
        yield return new WaitForSecondsRealtime(.5f);
        Current=State.Ready;
        while(!go)yield return null;
        Current=State.Playing;
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
            int cue=-2;var offsets=new List<double>();double furthest=0;
            // The take ends with the tour, and in any case once the song has played to its end and stopped.
            while(director.Directing&&!(furthest>=midi.Duration-1&&!midi.IsPlaying))
            {
                if(midi.IsPlaying)furthest=Math.Max(furthest,midi.Position);
                if(director.CueIndex>=0&&director.CueIndex!=cue){cue=director.CueIndex;Mark("cue-"+cue,director.CurrentCue?.text??"");}
                // Where the song's audio clock sits in the video: video time minus song time, while it plays.
                if(midi.IsPlaying&&midi.Position>.25&&offsets.Count<150)offsets.Add(Now-midi.Position);
                yield return null;
            }
            if(offsets.Count>0){offsets.Sort();report.audioStart=offsets[offsets.Count/2];}
        }
        if(marker!=null)marker.end=Now;
        yield return new WaitForSeconds(.4f);
        report.duration=Now;
        if(tap!=null&&tap.Capturing)
        {
            string stem=Path.ChangeExtension(ReportPath,".synth.wav");
            if(tap.End(stem))report.synthStem=Path.GetFileName(stem);
        }
        Directory.CreateDirectory(Folder);File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true));
        RecordingMode.Set(false);views.SetVertical(verticalBefore);Current=State.Done;
    }
    void Fail(string message,VisualizationViews views){Error=message;RecordingMode.Set(false);views.SetVertical(verticalBefore);Current=State.Done;}
}
