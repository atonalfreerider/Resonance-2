using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// Pattern wheels on a generated verse–chorus song (an original fixture, no recording):
// PatternPrep writes and prepares it, then every section is visited in Play Mode.
public static class PatternWheelValidation
{
    const string Folder="Temp/PatternWheel",Score=Folder+"/verse-chorus.mid";
    static void Prep(params string[] args)
    {
        var info=new ProcessStartInfo("dotnet","run --project Tools/PatternPrep -- "+string.Join(" ",args.Select(a=>"\""+a+"\""))){WorkingDirectory=Directory.GetCurrentDirectory(),UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        using var process=Process.Start(info);string output=process.StandardOutput.ReadToEnd()+process.StandardError.ReadToEnd();
        if(!process.WaitForExit(180000)||process.ExitCode!=0)throw new Exception("PatternPrep failed: "+output);
    }
    [MenuItem("Tools/Resonance/Check pattern wheels (generated song)")]
    public static async void Run()
    {
        if(!EditorApplication.isPlaying)return;
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();
        var root=main.GetComponent<UIDocument>().rootVisualElement;var deck=root.Q<PatternWheelDeck>();
        var results=new List<string>();
        void Check(bool condition,string message){if(!condition)throw new Exception(message);results.Add("PASS: "+message);}
        try
        {
            Directory.CreateDirectory(Folder);
            await Task.Run(()=>{Prep("--write-fixture","variation",Score);Prep(Score);});
            Check(midi.Load(Path.GetFullPath(Score)),"Generated song loads: "+midi.Status);
            var data=midi.Prepared;midi.Pause();
            Check(data.Version==PreparedPatternSong.CurrentVersion&&data.FormGrammar=="In (V C) (V′ C) Br (V C′) Out","Form grammar: "+data.FormGrammar);
            Check(data.Patterns.Length==4&&data.FundamentalBars<data.SongBars/3,$"{data.SongBars} bars compress to {data.Patterns.Length} fundamentals of {data.FundamentalBars} bars");
            Check(data.Groups.Length==1&&data.Groups[0].Visits==3,"Verse + chorus returns three times");
            Check(data.Sections[3].Variation=="new ending"&&data.Sections[7].Variation.StartsWith("transposed +2"),"Variations are named against the fundamental");
            double lastPixels=-1,lastTurns=double.PositiveInfinity;
            foreach(var section in data.Sections)
            {
                midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(section.Start+1)));deck.Tick();await Task.Delay(80);
                Check(deck.ActiveFamilyNode==section.Node&&deck.ActiveGroup==section.Group,"Planet and group follow the song: "+section.DisplayName);
                Check(Vector2.Distance(deck.MetaCenter,deck.FeaturedCenter)<.001f&&deck.FeaturedRadius>40,"Playing section is the centre planet: "+section.DisplayName);
                Check(deck.RackPixels>lastPixels&&deck.RackTurns<lastTurns,"Rack and ring advance together: "+section.DisplayName);
                lastPixels=deck.RackPixels;lastTurns=deck.RackTurns;
            }
            // The drum wheel speaks the same form language: the chorus groove wears the chorus hatch.
            var drums=main.GetComponent<DrumPatternDeck>();var chorus=data.Sections[2];
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(chorus.Start+1)));await Task.Delay(150);
            Check(drums.CurrentFamily>=0&&drums.CurrentGrooveSection=="C"&&drums.CurrentHatch==FormHatch.ForRole(data.PatternOf(chorus).Role,Array.IndexOf(data.Patterns,data.PatternOf(chorus))),"Chorus groove disc wears the chorus hatch");
            var verseGroove=data.Sections[1];midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(verseGroove.Start+1)));await Task.Delay(150);
            Check(drums.CurrentGrooveSection=="V"&&drums.CurrentHatch==FormHatch.Pattern.Diagonal,"Verse groove disc wears the verse hatch");
            Check(FormHatch.ForRole("Verse",0)!=FormHatch.ForRole("Chorus",1)&&FormHatch.ForRole("Chorus",1)!=FormHatch.ForRole("Bridge",3),"Verse, chorus and bridge are told apart by texture, not colour");
            var verse=data.Sections[3];midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(verse.Start+30)));deck.Tick();await Task.Delay(120);
            Directory.CreateDirectory("Temp/ResonanceChecks");ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/pattern-wheels.png");await Task.Delay(200);
            results.Add("ALL PATTERN WHEEL CHECKS PASSED");
        }
        catch(Exception e){results.Add("FAIL: "+e.Message);UnityEngine.Debug.LogException(e);}
        finally{Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/pattern-wheels.txt",results);UnityEngine.Debug.Log(string.Join("\n",results));}
    }
}
