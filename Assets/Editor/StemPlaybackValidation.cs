using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

public static class StemPlaybackValidation
{
    public static async void Run(string score)
    {
        var checks=new List<string>();
        void Check(bool value,string message){if(!value)throw new Exception(message);checks.Add("PASS: "+message);}
        try{
            var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();
            var audio=main.GetComponent<SongAudio>();var stems=main.GetComponent<StemPlayback>();
            audio.LoadPair("",Path.GetFullPath(score));
            for(int n=0;n<1200&&(!audio.Ready||audio.Busy);n++)await Task.Delay(100);
            Check(audio.Ready&&!audio.Busy,"Stem bundle loads");
            Check(stems.CachedCount==7,"All stem audio and scores preloaded");
            Check(stems.Stems.Length==7,"Seven separated / derived solo views available");
            var master=midi.HarmonicPrepared;var clip=audio.Source.clip;midi.Seek(30);
            var outline=main.GetComponent<DominantChordOutline>();await Task.Delay(120);int root=outline.RegionRoot;
            foreach(string id in new[]{"vocals","drums","other-high","bass",""}){
                stems.Select(id);for(int n=0;n<300&&stems.IsLoading;n++)await Task.Delay(100);await Task.Delay(120);
                Check(stems.SelectedId==id,"Solo selection completed: "+(id==""?"full mix":id));
                Check(ReferenceEquals(master,midi.HarmonicPrepared)&&outline.RegionRoot==root,"Full-song harmony persists: "+id);
                Check(audio.Source.clip.samples==clip.samples&&audio.Source.clip.frequency==clip.frequency,"Audio sample clocks match: "+id);
                if(id!="")Check(stems.AudibleSource.clip.name==stems.Stems.First(s=>s.id==id).name&&midi.Prepared.MidiSha256==stems.Stems.First(s=>s.id==id).midiSha256,"Audio and visual source both match selected stem: "+id);
                Check(Math.Abs(midi.Position-30)<.001,"Paused seek is preserved: "+id);
                if(id=="drums")Check(midi.Prepared.Notes.All(n=>n.Channel==10),"Rhythm solo contains only drum notes");
                if(id=="other-high")Check(midi.Prepared.Notes.All(n=>n.Pitch>=60&&n.Channel!=10),"High register visual contains only upper accompaniment notes");
                if(id=="")Check(ReferenceEquals(midi.Prepared,master)&&audio.Source.clip==clip,"Full mix restores original score and recording");
            }
            midi.Play();await Task.Delay(250);Check(audio.Source.isPlaying,"Recording plays on DSP clock");
            double before=midi.Position;var timer=System.Diagnostics.Stopwatch.StartNew();stems.Select("vocals");timer.Stop();Check(!stems.IsLoading&&midi.Position>=before,"Hot switch keeps clock running without loading");checks.Add("Switch time: "+timer.Elapsed.TotalMilliseconds+" ms");for(int n=0;n<300&&stems.IsLoading;n++)await Task.Delay(100);await Task.Delay(250);
            Check(midi.IsPlaying&&audio.Source.isPlaying&&stems.SelectedId=="vocals","Playing solo switch resumes synchronized audio");
            Check(main.GetComponent<FeaturedInstrument>().VoiceCount==2,"Vocal solo retains both harmony mice");
            var frame=midi.Prepared.Frames.LastOrDefault(f=>f.Time<=midi.VisualScorePosition);
            var expected=new HashSet<int>((frame?.Voices??Array.Empty<PreparedPatternSong.Voice>()).Where(v=>v.Channel!=10&&v.Pitch>=21&&v.Pitch<117).Select(v=>v.Pitch-21));
            Check(expected.SetEquals(main.ActiveNotes.Select(n=>n.Item1)),"Visible active notes exactly match the isolated stem frame");
            var ui=main.GetComponent<UIDocument>().rootVisualElement.Q<DropdownField>("stem-selector");
            Check(ui!=null&&ui.choices.Count==8,"Solo selector populated in player UI");
            var rootElement=main.GetComponent<UIDocument>().rootVisualElement;var tabs=rootElement.Q<SideMenuTabs>();
            foreach(string tab in new[]{"loading","solo","structure","advanced"}){
                tabs.Select(tab);await Task.Delay(80);
                Check(rootElement.Query<ScrollView>().ToList().Count(p=>p.name.StartsWith("page-")&&p.resolvedStyle.display==DisplayStyle.Flex)==1,"Only selected tab is displayed: "+tab);
                Check(midi.IsPlaying&&stems.SelectedId=="vocals","Tab switching preserves audio / visual solo: "+tab);
            }
            tabs.Select("solo");
            var views=main.GetComponent<VisualizationViews>();views.SetView(VisualizationViews.View.Torus);main.SetUncoiled(true);await Task.Delay(2600);
            Check(main.UncoilAmount>.999f,"Uncoil animation completes");
            Check(main.GetComponentsInChildren<MeshRenderer>().Where(r=>r.name.StartsWith("Aurora volume")).All(r=>!r.enabled),"All aurora volumes hidden while uncoiled");
            Check(main.GetComponentsInChildren<Chord>().Where(c=>!c.Releasing).All(c=>main.UncoiledChordAllowed(c.Note1.Index,c.Note2.Index)),"Uncoiled active chords connect only octaves and adjacent fifths");
            Check(main.UncoiledPoint(0,1).y>0&&Mathf.Abs(main.UncoiledPoint(0,1).x)<.001f,"Tonic is centered at top of open arcs");
            Check(main.UncoiledPoint(-.5f,1).y<0&&main.UncoiledPoint(.5f,1).y<0,"Open arc gap is opposite tonic at bottom");
            Check(Camera.main.GetComponent<CameraControl>().enabled,"Camera controls available in uncoiled torus view");
            Directory.CreateDirectory("Temp/ResonanceChecks");ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/uncoiled.png");await Task.Delay(200);
            main.SetUncoiled(false);await Task.Delay(2600);Check(main.UncoilAmount<.001f,"Recoil animation completes");
            midi.Pause();Directory.CreateDirectory("Temp/ResonanceChecks");ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/stem-solo.png");
            checks.Add("ALL STEM PLAYBACK CHECKS PASSED");
        }catch(Exception error){checks.Add("FAIL: "+error);Debug.LogException(error);}
        finally{Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/stem-playback.txt",checks);}
    }
}
