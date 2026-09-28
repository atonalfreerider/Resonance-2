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
            // In the overview the lyric strip sits between the pattern wheels and the torus.
            var stripO=views.LyricStrip;var wheelsO=deck.Overlay.worldBound;
            Check(rack.Shown&&stripO.height>60&&stripO.x>=wheelsO.xMax-48&&stripO.xMax<views.SceneRect.xMax-120,$"In the overview the lyric strip ({stripO.width:0}×{stripO.height:0}) sits between the pattern wheels and the torus");

            // Lyric mode: the reader and the drum wheel take most of the screen; the lyric graph and
            // a small vocal wheel sit in the panel below.
            views.SetView(VisualizationViews.View.Lyrics);await Task.Delay(1800);
            Check(deck.LyricLayout&&rack.Shown,"Lyric mode shows the lyric strip, the lyric graph and the vocal wheel");
            var overlay=deck.Overlay.worldBound;var screen=root.worldBound;var strip=views.LyricStrip;
            Check(strip.height>=100&&strip.yMax<=overlay.yMin+2&&strip.yMin>=screen.height*.2f&&rack.Viewport.height>.08f,$"The lyric strip takes {strip.height/screen.height:P0} of the height, between the drum wheel above and the lyric panel below");
            Check(deck.Graph.Columns==lyrics.Stanzas.Length&&deck.Graph.LinkCount>=20,$"The lyric graph lays out {deck.Graph.Columns} stanzas and {deck.Graph.LinkCount} rhyme links ({lyrics.Links.Length} in the sheet, repeats marked as refrains)");

            // Sung: the held "star" is lit on the vocal wheel and in the graph, with its rhyme links glowing.
            var star=lyrics.Syllables.First(s=>s.Line==0&&s.Text=="star");
            await At(star.VibratoStart+.3,500,true);
            Check(deck.Lyrics.Sung==star&&star.Vibrato>.2f,$"The vocal wheel lights the sung syllable \"{deck.Lyrics.Sung?.Text}\" (vibrato {star.Vibrato:0.00} st at {star.VibratoRate:0.0} Hz)");
            Check(deck.Graph.Stanza=="VERSE 1"&&deck.Graph.LitWord.Contains("star",StringComparison.OrdinalIgnoreCase)&&deck.Graph.LitLinks>0,$"The lyric graph lights \"{deck.Graph.LitWord}\" in Verse 1 with {deck.Graph.LitLinks} links of its line glowing");
            await Shot("lyric-sung");
            await At(star.Start+1,150);
            Check(rack.ReaderSyllable==star&&rack.Holding,"The reader holds \"star\" on the syllable line, its hold bar running out");
            // Paused at exact moments: the song position is the reader's clock.
            async Task HoldAt(double seconds){midi.Seek(midi.AudioTime(seconds));midi.Pause();await Task.Delay(150);}
            double SecondsOf(double beat)=>midi.Cycles.SecondsAt(beat);
            // The syllable is in the chord's colour; the strike across it is a white bloom that fades fast.
            await HoldAt(SecondsOf(star.Start)+.02);
            float strikeOn=rack.Strike;var landing=rack.ReaderColor;
            await HoldAt(SecondsOf(star.Start)+.3);
            float strikeOff=rack.Strike;var sung=rack.ReaderColor;var chord=rack.ChordColor;
            Vector3 Hue(Color c){var v=new Vector3(c.r,c.g,c.b);return v/Mathf.Max(1e-4f,Mathf.Max(v.x,Mathf.Max(v.y,v.z)));}
            Check(Vector3.Distance(Hue(sung),Hue(chord))<.05f&&strikeOn>.3f&&strikeOff<.03f&&landing.maxColorComponent>sung.maxColorComponent*1.2f,$"\"star\" lands with a small bloom ({landing.maxColorComponent:0.00} → {sung.maxColorComponent:0.00}) into the chord's colour; its strike flares white ({strikeOn:0.00}) and is gone 0.3 s later ({strikeOff:0.00})");

            // Spoken trochees: the stressed downbeat "Stood" is struck down, the upbeat "the" up.
            var stood=lyrics.Syllables.First(s=>s.Text=="Stood");var the=lyrics.Syllables.First(s=>s.Spoken&&s.Start>stood.Start);
            await HoldAt(SecondsOf(stood.Start)-.08);
            var drop=rack.ReaderShift;
            Check(rack.ReaderSyllable==stood&&rack.ReaderFrom==1&&drop.x<-.1f&&drop.y>.1f,$"80 ms before its beat \"Stood\" is sliding down its slash toward the well ({drop.x:0.00}, {drop.y:0.00} from its place)");
            await HoldAt(SecondsOf(the.Start)-.08);
            var rise=rack.ReaderShift;
            Check(rack.ReaderSyllable==the&&rack.ReaderFrom==-1&&rise.x<-.1f&&rise.y<-.1f,$"80 ms before it the off-beat \"the\" is sliding up its slash toward the crest ({rise.x:0.00}, {rise.y:0.00})");
            await HoldAt(SecondsOf(stood.Start)+.01);
            Check(rack.ReaderSyllable==stood&&rack.ReaderShift==Vector2.zero&&rack.ReaderY<-.05f&&rack.ReaderSquash.y<.92f&&rack.ReaderSquash.x>1.05f&&rack.Strike>.3f&&Mathf.Abs(rack.ReaderOrpX)<.01f,$"\"Stood\" lands centred on the stem (x {rack.ReaderOrpX:0.000}), low in the well (y {rack.ReaderY:0.00}), squashed by the impact ({rack.ReaderSquash.x:0.00}×{rack.ReaderSquash.y:0.00}) with a down strike ({rack.Strike:0.00})");
            await HoldAt(SecondsOf(the.Start)+.01);
            Check(rack.ReaderSyllable==the&&rack.ReaderY>.05f&&rack.ReaderSquash.y>1.05f&&rack.Strike>.3f&&Mathf.Abs(rack.ReaderOrpX)<.01f,$"The off-beat \"the\" lands centred, high on the crest (y {rack.ReaderY:0.00}), stretched ({rack.ReaderSquash.y:0.00}) with an up strike ({rack.Strike:0.00})");
            await HoldAt(SecondsOf(the.Start)+.14);
            Check(rack.ReaderSyllable==the&&Mathf.Abs(rack.ReaderSquash.x-1)<.01f&&Mathf.Abs(rack.ReaderSquash.y-1)<.01f,"140 ms later the impact has settled: no bounce");
            Check(rack.TrailCount>=1&&rack.TrailNearestRight<rack.ReaderLeft&&rack.TrailNearestY<-.05f&&rack.TrailNearestAlpha<1.01f&&rack.TrailNearestAlpha>.3f,$"\"Stood\" has joined the trail building to the left, still in its well (y {rack.TrailNearestY:0.00}; {rack.TrailCount} syllables, nearest ending at {rack.TrailNearestRight:0.00}, left of {rack.ReaderLeft:0.00})");
            // A drawn-out syllable is set wide; a short one is not.
            var dayHeld=lyrics.Syllables.First(s=>s.Text=="day");
            await HoldAt(SecondsOf(dayHeld.Start)+.05);
            Check(rack.ReaderSyllable==dayHeld&&rack.ReaderSpacing>=12,$"\"day\", held over {dayHeld.Teeth} teeth, is set wide (letter spacing {rack.ReaderSpacing:0})");
            await HoldAt(SecondsOf(the.Start)+.05);
            Check(rack.ReaderSpacing==0,"\"the\", an eighth, is set tight");
            // Vibrato: the letters shimmer while the held star is sung with vibrato.
            await HoldAt(SecondsOf(star.VibratoStart)+.3);
            Check(rack.ReaderSyllable==star&&rack.Shimmer>.5f,$"Under vibrato the letters of \"star\" shimmer at {star.VibratoRate:0.0} Hz (depth {rack.Shimmer:0.00})");
            // The groove is a slash at every beat and upbeat, scrolling across the strip.
            Check(rack.SlashCount>=6,$"The groove is a slash at every beat and upbeat across the strip ({rack.SlashCount} in view)");
            // Played in real time: each is centred on its onset.
            await At(stood.Start-1,60,true);
            double onsetMs=(SecondsOf(stood.Start)-midi.ScorePosition)*1000;
            await Task.Delay(Mathf.Max(0,Mathf.RoundToInt((float)onsetMs))+25);
            Check(rack.ReaderSyllable==stood&&Mathf.Abs(rack.ReaderOrpX)<.01f,$"Played, just after its beat \"Stood\" is centred on the reticle (x {rack.ReaderOrpX:0.000})");
            await Task.Delay(Mathf.RoundToInt((float)(SecondsOf(the.Start)-midi.ScorePosition)*1000)+40);
            Check(rack.ReaderSyllable==the&&Mathf.Abs(rack.ReaderOrpX)<.01f,"Played, the upbeat \"the\" is centred on its onset");
            Check(deck.Graph.Stanza=="RAP 1"&&deck.Graph.LitWord.Length>0,$"The lyric graph follows into Rap 1, lighting \"{deck.Graph.LitWord}\"");
            await Task.Delay(700);await Shot("lyric-rap");

            // Pentameter: the held line-end "day" stays on the line with its hold bar.
            var day=lyrics.Syllables.First(s=>s.Text=="day");
            await At(day.Start+1.2,200,true);
            Check(rack.ReaderSyllable==day&&rack.Holding&&Mathf.Abs(rack.ReaderOrpX)<.03f,$"\"day\", held over {day.Teeth} teeth, stays on the line with its hold bar");
            await Shot("lyric-sonnet");
            views.SetView(VisualizationViews.View.Torus);await Task.Delay(1500);
            Check(!rack.Shown&&!deck.LyricLayout,"The torus view alone hides the lyrics");
            views.SetView(VisualizationViews.View.Overview);await Task.Delay(600);
            results.Add("ALL LYRIC MODE CHECKS PASSED");
        }
        catch(Exception e){results.Add("FAIL: "+e.Message);UnityEngine.Debug.LogException(e);}
        finally{Directory.CreateDirectory(Checks);File.WriteAllLines(Checks+"/lyric-mode.txt",results);UnityEngine.Debug.Log(string.Join("\n",results));}
    }
}
