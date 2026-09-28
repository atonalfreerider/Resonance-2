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
                Check(Vector2.Distance(deck.MetaCenter,deck.FeaturedCenter)<.001f&&deck.FeaturedRadius>40,$"Playing section is the centre planet: {section.DisplayName} (meta {deck.MetaCenter}, featured {deck.FeaturedCenter}, r {deck.FeaturedRadius:0})");
                Check(deck.RackPixels>lastPixels&&deck.RackTurns<lastTurns,"Rack and ring advance together: "+section.DisplayName);
                lastPixels=deck.RackPixels;lastTurns=deck.RackTurns;
            }
            // Key changes are detected offline: the lifted last chorus moves the key from C to D.
            const int C=3,D=5,G=10;var lift=data.Sections[7];
            Check(data.KeyChanges.Length==1&&data.KeyChanges[0].From==C&&data.KeyChanges[0].Key==D&&Math.Abs(data.KeyChanges[0].Beat-lift.Start)<1e-6,"Key change C → D at the lifted chorus: "+string.Join(", ",data.KeyChanges.Select(k=>k.Beat+" "+k.Evidence)));
            var pointer=main.GetComponent<KeyShiftIndicator>();
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(lift.Start-2)));await Task.Delay(900);
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(lift.Start+.1)));await Task.Delay(120);
            Check(main.currentKey==D&&main.KeyChanging&&pointer.Visible&&pointer.Direction==1&&pointer.Target==D,$"As the torus turns into D a rail stands at the tonic label, its chevron up (sharpward) to D (alpha {pointer.Alpha:0.00})");
            await Task.Delay(1400);
            Check(main.currentKey==D&&!main.KeyChanging&&!pointer.Visible,"Once the torus is locked into D the rail is gone");
            // A V/V in the bridge is named, but the torus does not move for it and no rail stands.
            var vv=data.Tensions.First(t=>t.Kind=="V/V");
            Check(vv.Target==G&&!vv.Completes,"The bridge's D major is V/V pointing at G, without a key change");
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(vv.End-.2)));await Task.Delay(1300);
            Check(main.currentKey==C&&midi.CurrentTension==vv&&main.TensionAmount==0&&!pointer.Visible,"During V/V the torus stays still in C and no rail stands: the caption alone names the tension");
            // The drum wheel names the section each groove belongs to.
            var drums=main.GetComponent<DrumPatternDeck>();var chorus=data.Sections[2];
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(chorus.Start+1)));await Task.Delay(150);
            Check(drums.CurrentFamily>=0&&drums.CurrentGrooveSection=="C","Chorus groove disc is the chorus's");
            var verseGroove=data.Sections[1];midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(verseGroove.Start+1)));await Task.Delay(150);
            Check(drums.CurrentGrooveSection=="V","Verse groove disc is the verse's");
            var beats=drums.WheelTransform.GetComponentsInChildren<LineRenderer>().Where(l=>l.name=="Beat line"&&l.enabled).ToArray();
            Check(beats.Length==drums.CurrentCounts*2,"The playing disc has a radial line on every beat and upbeat");
            Check(FormHatch.ForRole("Verse",0)!=FormHatch.ForRole("Chorus",1)&&FormHatch.ForRole("Chorus",1)!=FormHatch.ForRole("Bridge",3),"Verse, chorus and bridge are told apart by texture, not colour");
            // The wheels and the 3D scene share the screen without overlapping.
            main.GetComponent<VisualizationViews>().SetView(VisualizationViews.View.Overview);await Task.Delay(1500);
            var overlay=deck.Overlay.worldBound;var screen=root.worldBound;var view=Camera.main.rect;
            // Portrait stacks the scene above the wheels (the camera starts above them); landscape
            // puts it beside them (the camera starts right of them).
            bool stacked=view.yMin>.05f;
            // (Beside them the scene may start over the wheels' empty right margin, 48 units at most.)
            Check(stacked?overlay.yMin/screen.height>=1-view.yMin-.01f:overlay.xMax-48<=view.xMin*screen.width+.5f,"Pattern wheels and the torus/drum viewport do not overlap (beyond the wheels' margin)");
            var verse=data.Sections[3];midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(verse.Start+30)));deck.Tick();await Task.Delay(120);
            Directory.CreateDirectory("Temp/ResonanceChecks");ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/pattern-wheels.png");await Task.Delay(200);
            results.Add("ALL PATTERN WHEEL CHECKS PASSED");
        }
        catch(Exception e){results.Add("FAIL: "+e.Message);UnityEngine.Debug.LogException(e);}
        finally{Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/pattern-wheels.txt",results);UnityEngine.Debug.Log(string.Join("\n",results));}
    }
}
