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
            for(int n=0;n<300&&(!audio.Ready||audio.Busy);n++)await Task.Delay(100);
            Check(audio.Ready&&!audio.Busy,"Stem bundle loads");
            Check(stems.Stems.Length==7,"Seven separated / derived solo views available");
            var master=midi.HarmonicPrepared;var clip=audio.Source.clip;midi.Seek(30);
            var outline=main.GetComponent<DominantChordOutline>();await Task.Delay(120);int root=outline.RegionRoot;
            foreach(string id in new[]{"vocals","drums","other-high","bass",""}){
                stems.Select(id);for(int n=0;n<300&&stems.IsLoading;n++)await Task.Delay(100);await Task.Delay(120);
                Check(stems.SelectedId==id,"Solo selection completed: "+(id==""?"full mix":id));
                Check(ReferenceEquals(master,midi.HarmonicPrepared)&&outline.RegionRoot==root,"Full-song harmony persists: "+id);
                Check(audio.Source.clip.samples==clip.samples&&audio.Source.clip.frequency==clip.frequency,"Audio sample clocks match: "+id);
                if(id!="")Check(audio.Source.clip.name==stems.Stems.First(s=>s.id==id).name&&midi.Prepared.MidiSha256==stems.Stems.First(s=>s.id==id).midiSha256,"Audio and visual source both match selected stem: "+id);
                Check(Math.Abs(midi.Position-30)<.001,"Paused seek is preserved: "+id);
                if(id=="drums")Check(midi.Prepared.Notes.All(n=>n.Channel==10),"Rhythm solo contains only drum notes");
                if(id=="other-high")Check(midi.Prepared.Notes.All(n=>n.Pitch>=60&&n.Channel!=10),"High register visual contains only upper accompaniment notes");
                if(id=="")Check(ReferenceEquals(midi.Prepared,master)&&audio.Source.clip==clip,"Full mix restores original score and recording");
            }
            midi.Play();await Task.Delay(250);Check(audio.Source.isPlaying,"Recording plays on DSP clock");
            stems.Select("vocals");for(int n=0;n<300&&stems.IsLoading;n++)await Task.Delay(100);await Task.Delay(250);
            Check(midi.IsPlaying&&audio.Source.isPlaying&&stems.SelectedId=="vocals","Playing solo switch resumes synchronized audio");
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
            midi.Pause();Directory.CreateDirectory("Temp/ResonanceChecks");ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/stem-solo.png");
            checks.Add("ALL STEM PLAYBACK CHECKS PASSED");
        }catch(Exception error){checks.Add("FAIL: "+error);Debug.LogException(error);}
        finally{Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/stem-playback.txt",checks);}
    }
}
