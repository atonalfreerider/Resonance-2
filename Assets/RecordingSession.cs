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
    [Serializable] public sealed class Report {public string kind="",song="",orientation="",video="";public int width,height,fps;public double duration;public List<Marker> markers=new();public string error="";}
    public enum State {Idle,Preparing,Ready,Playing,Done}
    public State Current {get;private set;}=State.Idle;
    public string Error {get;private set;}="";
    public string ReportPath {get;private set;}="";
    public static string Folder=>Path.GetFullPath(Path.Combine(Application.dataPath,"../Recordings/Videos"));
    Report report;float startTime;bool go;bool verticalBefore;

    // Make the tutorial (song null) or a song's tour ready to record.
    public void Prepare(string song,bool vertical){if(Current is State.Idle or State.Done)StartCoroutine(Run(song,vertical));}
    // The recorder is running: play from the top; video time starts now.
    public void Go(string videoPath,int fps){if(Current!=State.Ready)return;report.video=Path.GetFileName(videoPath);report.fps=fps;ReportPath=Path.ChangeExtension(videoPath,".json");startTime=Time.time;go=true;}
    double Now=>Time.time-startTime;

    IEnumerator Run(string songTitle,bool vertical)
    {
        Current=State.Preparing;Error="";go=false;
        var views=GetComponent<VisualizationViews>();var library=GetComponent<SongLibraryPanel>();
        var tutorial=GetComponent<TutorialDirector>();var director=GetComponent<SongDirector>();var midi=GetComponent<MidiPlayer>();
        verticalBefore=views.Vertical;views.SetVertical(vertical);RecordingMode.Set(true);views.SetPanelHidden(true);library.SetOpen(false);
        report=new Report{kind=songTitle==null?"tutorial":"tour",song=songTitle??"",orientation=vertical?"vertical":"horizontal",width=Screen.width&~1,height=Screen.height&~1};
        if(songTitle!=null)
        {
            var song=library.Songs.FirstOrDefault(s=>string.Equals(s.Title,songTitle,StringComparison.OrdinalIgnoreCase))??library.Songs.FirstOrDefault(s=>s.Title.StartsWith(songTitle,StringComparison.OrdinalIgnoreCase));
            if(song==null||!song.Tour){Fail("No tour for "+songTitle,views);yield break;}
            director.Tour(song);
            float waited=0;while(!director.Directing&&waited<150){waited+=Time.unscaledDeltaTime;yield return null;}
            if(!director.Directing){Fail("The tour did not start",views);yield break;}
            midi.Pause();midi.Seek(0);
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
            midi.Play();int cue=-2;
            while(director.Directing){if(director.CueIndex>=0&&director.CueIndex!=cue){cue=director.CueIndex;Mark("cue-"+cue,director.CurrentCue?.text??"");}yield return null;}
        }
        if(marker!=null)marker.end=Now;
        yield return new WaitForSeconds(.4f);
        report.duration=Now;
        Directory.CreateDirectory(Folder);File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true));
        RecordingMode.Set(false);views.SetVertical(verticalBefore);Current=State.Done;
    }
    void Fail(string message,VisualizationViews views){Error=message;RecordingMode.Set(false);views.SetVertical(verticalBefore);Current=State.Done;}
}
