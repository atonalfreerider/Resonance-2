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
// and prepares it with its lyric sheet, then Play Mode walks the changers (and a click on one),
// the vocal wheel, the reader on the drum wheel and the lyric graph through their moments.
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
            // A click on a changer makes its lane the highlighted instrument.
            var feature=main.GetComponent<FeaturedInstrument>();var before=(feature.Track,feature.Channel);
            await At(data.Sections[1].Start+6,300,true);
            int keysCell=deck.Changers.Cells.FindIndex(c=>c.name=="Keys");var pick=root.Q<VisualElement>("changer-select-"+keysCell);
            Check(deck.Changers.Cells.Count==3&&keysCell>=0&&pick!=null&&pick.resolvedStyle.display==DisplayStyle.Flex&&pick.worldBound.width>20&&pick.worldBound.height>40,$"Each changer has a click target over it (Keys: {pick?.worldBound.size})");
            using(var down=PointerDownEvent.GetPooled(new Event{type=EventType.MouseDown,button=0,mousePosition=pick.worldBound.center})){down.target=pick;pick.SendEvent(down);}
            await Task.Delay(300);
            var keysPart=data.Parts.First(x=>x.Name=="Keys");
            Check(feature.Matches(keysPart.Track,keysPart.Channel)&&deck.Changers.FeaturedTrack==keysPart.Track,"Clicking the Keys changer makes the keys the highlighted instrument, ringed in white");
            await Shot("instrument-changers");
            feature.Select(before.Item1,before.Item2);

            // Lyric mode: the reader and the drum wheel take most of the screen; the lyric graph and
            // a small vocal wheel sit in the panel below.
            views.SetView(VisualizationViews.View.Lyrics);await Task.Delay(1800);
            Check(deck.LyricLayout&&rack.Shown,"Lyric mode shows the reader on the drum wheel, the lyric graph and the vocal wheel");
            var overlay=deck.Overlay.worldBound;var screen=root.worldBound;var view=Camera.main.rect;
            Check(view.yMin>.28f&&view.yMin<.46f&&overlay.yMin/screen.height>=1-view.yMin-.02f,$"The reader's strip takes {1-view.yMin:P0} of the height, above the lyric panel without overlap");
            Check(deck.Graph.Columns==lyrics.Stanzas.Length&&deck.Graph.LinkCount>=20,$"The lyric graph lays out {deck.Graph.Columns} stanzas and {deck.Graph.LinkCount} rhyme links ({lyrics.Links.Length} in the sheet, repeats marked as refrains)");

            // Sung: the held "star" is lit on the vocal wheel and in the graph, with its rhyme links glowing.
            var star=lyrics.Syllables.First(s=>s.Line==0&&s.Text=="star");
            await At(star.VibratoStart+.3,500,true);
            Check(deck.Lyrics.Sung==star&&star.Vibrato>.2f,$"The vocal wheel lights the sung syllable \"{deck.Lyrics.Sung?.Text}\" (vibrato {star.Vibrato:0.00} st at {star.VibratoRate:0.0} Hz)");
            Check(deck.Graph.Stanza=="VERSE 1"&&deck.Graph.LitWord.Contains("star",StringComparison.OrdinalIgnoreCase)&&deck.Graph.LitLinks>0,$"The lyric graph lights \"{deck.Graph.LitWord}\" in Verse 1 with {deck.Graph.LitLinks} links of its line glowing");
            await Shot("lyric-sung");
            await At(star.Start+1,150);
            Check(rack.ReaderSyllable==star&&rack.Holding,"The reader holds \"star\" on the centerline, its hold bar running out");
            // The bloom: white on landing, resolved into the chord's colour 0.8 s later.
            double starAt=midi.Cycles.SecondsAt(star.Start);
            midi.Seek(midi.AudioTime(starAt+.02));midi.Pause();await Task.Delay(150);
            var flash=rack.ReaderColor;var chord=rack.ChordColor;
            midi.Seek(midi.AudioTime(starAt+.8));midi.Pause();await Task.Delay(150);
            var settled=rack.ReaderColor;chord=rack.ChordColor;
            Vector3 Hue(Color c){var v=new Vector3(c.r,c.g,c.b);return v/Mathf.Max(1e-4f,Mathf.Max(v.x,Mathf.Max(v.y,v.z)));}
            Check(flash.maxColorComponent>chord.maxColorComponent*2&&Vector3.Distance(Hue(settled),Hue(chord))<.08f&&settled.maxColorComponent<chord.maxColorComponent*1.25f,$"The syllable lands in a white bloom ({flash.maxColorComponent:0.0}x) that resolves to the chord's colour ({settled.maxColorComponent:0.00} vs {chord.maxColorComponent:0.00})");

            // Spoken trochees: the stressed downbeat "Stood" comes down the lane above and is shoved
            // down onto the centerline exactly on its beat; the upbeat "the" comes up from below.
            var stood=lyrics.Syllables.First(s=>s.Text=="Stood");var the=lyrics.Syllables.First(s=>s.Spoken&&s.Start>stood.Start);
            var drumBeats=data.Notes.Where(n=>n.Channel==10).Select(n=>n.Beat).ToArray();
            await At(stood.Start-1,60,true);
            Check(rack.LaneOf(stood)==1&&rack.LaneOf(the)==-1,"Before its beat, \"Stood\" rides the downbeat lane above the centerline and \"the\" the upbeat lane below");
            Check(rack.VisibleTeeth.Count>4&&rack.VisibleTeeth.All(t=>drumBeats.Any(b=>Math.Abs(b-t)<1e-3))&&rack.VisibleTeeth.Any(t=>Math.Abs(t-stood.Start)<1e-3),$"Every cliff of the saw is a drum hit, one under \"Stood\" ({rack.VisibleTeeth.Count} on the rack)");
            double onsetMs=(midi.Cycles.SecondsAt(stood.Start)-midi.ScorePosition)*1000;
            await Task.Delay(Mathf.Max(0,Mathf.RoundToInt((float)onsetMs))+25);
            Check(rack.ReaderSyllable==stood&&rack.ReaderFrom==1&&Mathf.Abs(rack.ReaderOffset)<.005f&&Mathf.Abs(rack.ReaderOrpX)<.01f,$"Just after its beat \"Stood\" has been shoved down onto the centerline, no overshoot (offset {rack.ReaderOffset:0.000}), its recognition letter on the reticle (x {rack.ReaderOrpX:0.000})");
            await Task.Delay(Mathf.RoundToInt((float)(midi.Cycles.SecondsAt(the.Start)-midi.ScorePosition)*1000)+40);
            Check(rack.ReaderSyllable==the&&rack.ReaderFrom==-1&&Mathf.Abs(rack.ReaderOffset)<.005f,$"The upbeat \"the\" is shoved up onto the centerline from below (offset {rack.ReaderOffset:0.000})");
            Check(rack.Caption.Contains("Stood")&&rack.Caption.Contains("wig"),"The spoken line is written above the reader");
            Check(deck.Graph.Stanza=="RAP 1"&&deck.Graph.LitWord.Length>0,$"The lyric graph follows into Rap 1, lighting \"{deck.Graph.LitWord}\"");
            await Task.Delay(700);await Shot("lyric-rap");

            // Pentameter: the held line-end "day" stays on the centerline with its hold bar.
            var day=lyrics.Syllables.First(s=>s.Text=="day");
            await At(day.Start+1.2,200,true);
            Check(rack.ReaderSyllable==day&&rack.Holding&&Mathf.Abs(rack.ReaderOffset)<.03f,$"\"day\", held over {day.Teeth} teeth, stays on the centerline with its hold bar");
            await Shot("lyric-sonnet");
            views.SetView(VisualizationViews.View.Overview);await Task.Delay(1500);
            Check(!rack.Shown&&!deck.LyricLayout,"Leaving lyric mode hides the lyrics");
            results.Add("ALL LYRIC MODE CHECKS PASSED");
        }
        catch(Exception e){results.Add("FAIL: "+e.Message);UnityEngine.Debug.LogException(e);}
        finally{Directory.CreateDirectory(Checks);File.WriteAllLines(Checks+"/lyric-mode.txt",results);UnityEngine.Debug.Log(string.Join("\n",results));}
    }
}
