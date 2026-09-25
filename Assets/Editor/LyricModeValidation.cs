using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// Lyric mode and instrument changers on a generated song of public-domain words and tunes
// (Taylor's "The Star", Longfellow's Hiawatha, Shakespeare's Sonnet 18): PatternPrep writes
// and prepares it with its lyric sheet, then Play Mode walks the changers, the vocal wheel,
// the drum rack and the rhyme board through their moments.
public static class LyricModeValidation
{
    const string Folder="Temp/LyricMode",Score=Folder+"/lyrics-song.mid",Checks="Temp/ResonanceChecks";
    static void Prep(params string[] args)
    {
        var info=new ProcessStartInfo("dotnet","run --project Tools/PatternPrep -- "+string.Join(" ",args.Select(a=>"\""+a+"\""))){WorkingDirectory=Directory.GetCurrentDirectory(),UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        using var process=Process.Start(info);string output=process.StandardOutput.ReadToEnd()+process.StandardError.ReadToEnd();
        if(!process.WaitForExit(180000)||process.ExitCode!=0)throw new Exception("PatternPrep failed: "+output);
    }
    static async Task Shot(string name){ScreenCapture.CaptureScreenshot($"{Checks}/{name}.png");await Task.Delay(250);}
    [MenuItem("Tools/Resonance/Check lyric mode and instrument changers (generated song)")]
    public static async void Run()
    {
        if(!EditorApplication.isPlaying){UnityEngine.Debug.LogWarning("Enter Play Mode first.");return;}
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();var views=main.GetComponent<VisualizationViews>();
        var root=main.GetComponent<UIDocument>().rootVisualElement;var deck=root.Q<PatternWheelDeck>();var rack=main.GetComponent<DrumLyricRack>();
        var results=new List<string>();
        void Check(bool condition,string message){if(!condition)throw new Exception(message);results.Add("PASS: "+message);}
        async Task At(double beat,int wait=150,bool play=false){midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(beat)));if(play)midi.Play();else midi.Pause();deck.Tick();await Task.Delay(wait);deck.Tick();}
        try
        {
            Directory.CreateDirectory(Folder);Directory.CreateDirectory(Checks);
            await Task.Run(()=>{Prep("--write-fixture","lyrics",Score);Prep(Score);});
            Check(midi.Load(Path.GetFullPath(Score)),"Generated lyric song loads: "+midi.Status);
            var data=midi.Prepared;var lyrics=data.Lyrics;
            Check(data.Version==PreparedPatternSong.CurrentVersion&&data.Parts.Length==3&&lyrics!=null&&lyrics.Syllables.Length==251,$"Bundle v{data.Version}: {data.Parts.Length} instrument parts, {lyrics?.Syllables.Length} synced syllables ({lyrics?.Sync})");

            // Instrument changers in the overview: each lane shows its pattern and repeat.
            views.SetView(VisualizationViews.View.Overview);await Task.Delay(1200);
            var verse3=data.Sections[5];
            await At(verse3.Start+29,300);
            var keys=deck.Changers.Showing.First(s=>s.name=="Keys");var voice=deck.Changers.Showing.First(s=>s.name=="Lead Vocal");
            Check(deck.Changers.LaneCount==3&&keys.token=="B′"&&voice.token=="A′","Verse 3's Am–D bar: keys and vocal changers show their variation discs (B′, A′)");
            var rap2=data.Sections[4];
            await At(rap2.Start+33,300);
            keys=deck.Changers.Showing.First(s=>s.name=="Keys");
            Check(keys.token=="C′"&&keys.repeat==3&&keys.run==3,"Rap 2's last pass: the keys' rap loop on its third repeat of three, varied (C′ 3/3)");
            voice=deck.Changers.Showing.First(s=>s.name=="Lead Vocal");
            Check(!voice.top&&voice.token=="·","The vocal rests during the rap: no disc on top of its stack");
            await At(data.Sections[1].Start+6,300,true);await Shot("instrument-changers");

            // Lyric mode: the panel turns to the vocal wheel and rhyme board, the drum wheel grows its rack.
            views.SetView(VisualizationViews.View.Lyrics);await Task.Delay(1800);
            Check(deck.LyricLayout&&rack.Shown,"Lyric mode shows the vocal wheel, the rhyme board and the drum rack");
            var overlay=deck.Overlay.worldBound;var screen=root.worldBound;var view=Camera.main.rect;
            Check(view.yMin>.3f&&overlay.yMin/screen.height>=1-view.yMin-.02f,"The drum rack's strip sits above the lyric panel without overlap");

            // Sung: the held "star" is lit on the vocal wheel, with vibrato.
            var star=lyrics.Syllables.First(s=>s.Line==0&&s.Text=="star");
            await At(star.VibratoStart+.3,500,true);
            Check(deck.Lyrics.Sung==star&&star.Vibrato>.2f,$"The vocal wheel lights the sung syllable \"{deck.Lyrics.Sung?.Text}\" (vibrato {star.Vibrato:0.00} st at {star.VibratoRate:0.0} Hz)");
            Check(deck.Lyrics.LettersShown>=12,$"{deck.Lyrics.LettersShown} letters ride the melody curve");
            Check(deck.Lyrics.BoardLine==0&&deck.Lyrics.BoardRows==6,"The rhyme board shows Verse 1, its first line current");
            await Shot("lyric-sung");

            // Spoken trochees: a stressed downbeat drops into its well, the upbeat after it is bumped over the saw.
            var stood=lyrics.Syllables.First(s=>s.Text=="Stood");var the=lyrics.Syllables.First(s=>s.Spoken&&s.Start>stood.Start);
            await At(stood.Start-.25,50,true);
            var waiting=rack.Placed.FirstOrDefault(p=>p.syllable==stood);
            Check(waiting.syllable!=null&&waiting.state=="waiting"&&waiting.lift>DrumLyricRack.Crest,"Before its beat, \"Stood\" waits on the crest");
            await Task.Delay(Mathf.RoundToInt((float)(midi.Cycles.SecondsAt(the.Start)-midi.Cycles.SecondsAt(stood.Start-.25))*1000)+60);
            var dropped=rack.Placed.First(p=>p.syllable==stood);var bumped=rack.Placed.First(p=>p.syllable==the);
            Check(dropped.state=="in well"&&dropped.lift<.14f,$"On the downbeat \"Stood\" dropped into its well (lift {dropped.lift:0.00})");
            Check(bumped.state=="bumped"&&bumped.lift>dropped.lift+.05f,$"The upbeat \"the\" is bumped up over the saw (lift {bumped.lift:0.00})");
            Check(rack.Caption.Contains("Stood")&&rack.Caption.Contains("wig"),"The spoken line is written above the rack");
            var rap1=lyrics.Stanzas.First(s=>s.Name=="Rap 1");
            Check(deck.Lyrics.BoardRows==8&&lyrics.Lines[deck.Lyrics.BoardLine].Stanza==Array.IndexOf(lyrics.Stanzas,rap1),"The rhyme board follows into Rap 1 (8 trochaic lines)");
            await Task.Delay(700);await Shot("lyric-rap");

            // Pentameter: the held line-end "day" floats in an arc over the teeth, at the comb, then dives.
            var day=lyrics.Syllables.First(s=>s.Text=="day");
            await At(day.Start+1.2,200,true);
            var floating=rack.Placed.First(p=>p.syllable==day);
            Check(floating.state=="floating"&&floating.lift>DrumLyricRack.Crest+.2f&&Mathf.Abs(floating.x)<.6f,$"\"day\", held over {day.Teeth} teeth, floats above the saw at the comb (x {floating.x:0.00}, lift {floating.lift:0.00})");
            await Shot("lyric-sonnet");
            await At(Math.Ceiling(day.End)+.6,250);
            var dived=rack.Placed.FirstOrDefault(p=>p.syllable==day);
            Check(dived.syllable!=null&&dived.state=="in well"&&dived.lift<.14f,"After its hold, \"day\" has dived into the well where it ends");
            views.SetView(VisualizationViews.View.Overview);await Task.Delay(1500);
            Check(!rack.Shown&&!deck.LyricLayout,"Leaving lyric mode hides the lyrics");
            results.Add("ALL LYRIC MODE CHECKS PASSED");
        }
        catch(Exception e){results.Add("FAIL: "+e.Message);UnityEngine.Debug.LogException(e);}
        finally{Directory.CreateDirectory(Checks);File.WriteAllLines(Checks+"/lyric-mode.txt",results);UnityEngine.Debug.Log(string.Join("\n",results));}
    }
}
