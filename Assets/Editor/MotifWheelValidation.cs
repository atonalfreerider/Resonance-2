using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// The structure wheel (Docs/MOTIF-REDUCTION.md §14) on PatternPrep's generated motif-ternary fixture: the étude's principal line and
// bass for A and A′ (public domain) around a synthetic B. PatternPrep writes and prepares it, then Play Mode checks §15.4's U1–U8
// and U9's allocation part; U9's other parts are Check pattern wheels, Check lyric mode and Measure frame time. Results go to
// Temp/ResonanceChecks/motif-wheel.txt, with a screenshot.
public static class MotifWheelValidation
{
    public const string Folder="Temp/MotifWheel",Score=Folder+"/ternary.mid";const string Checks="Temp/ResonanceChecks";
    static void Prep(params string[] args)
    {
        var info=new ProcessStartInfo("dotnet","run --project Tools/PatternPrep -- "+string.Join(" ",args.Select(a=>"\""+a+"\""))){WorkingDirectory=Directory.GetCurrentDirectory(),UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        using var process=Process.Start(info);string output=process.StandardOutput.ReadToEnd()+process.StandardError.ReadToEnd();
        if(!process.WaitForExit(240000)||process.ExitCode!=0)throw new Exception("PatternPrep failed: "+output);
    }
    // The library's folders whose bundle is version 5: what an unregenerated library holds.
    static List<string> VersionFive()
    {
        var found=new List<string>();if(!Directory.Exists("PreparedSongs/Library"))return found;
        foreach(var folder in Directory.GetDirectories("PreparedSongs/Library").OrderBy(f=>f,StringComparer.Ordinal))
        {
            string score=Path.Combine(folder,"aligned.mid"),bundle=score+".patterns.json";
            if(!File.Exists(score)||!File.Exists(score+".prepared.json")||!File.Exists(bundle))continue;
            using var reader=new StreamReader(bundle);var head=new char[65536];int read=reader.Read(head,0,head.Length);
            var m=Regex.Match(new string(head,0,read),"\"Version\"\\s*:\\s*(\\d+)");if(m.Success&&m.Groups[1].Value=="5")found.Add(folder);
        }
        return found;
    }
    static double Phase(Vector2 local,Vector2 center){var d=local-center;double a=Math.Atan2(d.x,-d.y)/(2*Math.PI);return a-Math.Floor(a);}
    // A click on the structure wheel where an overlay-local point is, through the picker's own pointer handler.
    static void Click(PatternWheelDeck deck,Vector2 local)
    {
        var world=deck.Overlay.LocalToWorld(local);var picker=deck.StructurePicker;
        using(var down=PointerDownEvent.GetPooled(new Event{type=EventType.MouseDown,button=0,clickCount=1,mousePosition=world})){down.target=picker;picker.SendEvent(down);}
        using(var up=PointerUpEvent.GetPooled(new Event{type=EventType.MouseUp,button=0,mousePosition=world})){up.target=picker;picker.SendEvent(up);}
    }
    [MenuItem("Tools/Resonance/Check motif wheel (generated ternary fixture)")]
    public static async void Run()
    {
        if(!EditorApplication.isPlaying){UnityEngine.Debug.LogWarning("Enter Play Mode first.");return;}
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();var views=main.GetComponent<VisualizationViews>();
        var root=main.GetComponent<UIDocument>().rootVisualElement;var deck=root.Q<PatternWheelDeck>();var director=main.GetComponent<SongDirector>();var arrows=main.GetComponent<StoryAnnotations>();
        var feature=main.GetComponent<FeaturedInstrument>();
        var results=new List<string>();var errors=new List<string>();
        void Check(bool condition,string message){if(!condition)throw new Exception(message);results.Add("PASS: "+message);}
        void Log(string message,string stack,LogType type){if(type==LogType.Exception||type==LogType.Error)errors.Add(message);}
        double Now()=>midi.Cycles.BeatAt(midi.ScorePosition);
        async Task At(double beat,int wait=150){midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(beat)));midi.Pause();deck.Tick();await Task.Delay(wait);deck.Tick();await Task.Yield();}
        Application.logMessageReceived+=Log;bool enabled=deck.StructureEnabled;(int track,int channel) featured=(-1,-1);
        try
        {
            Directory.CreateDirectory(Folder);Directory.CreateDirectory(Checks);deck.StructureEnabled=true;
            views.SetView(VisualizationViews.View.Overview);

            // U1: an unregenerated library is listed as before: version-5 bundles pass through MinimumReadableVersion.
            var listed=SongLibraryPanel.Scan();var five=VersionFive();
            Check(five.All(f=>listed.Any(s=>string.Equals(Path.GetFullPath(s.Folder),Path.GetFullPath(f),StringComparison.OrdinalIgnoreCase))),$"U1 · the song list shows {listed.Count} songs, every one of the library's {five.Count} version-5 bundles among them");

            // U8: a version-5 bundle draws the classic wheel with the toggle on, without exceptions (the smallest one, MIDI only).
            var classic=five.OrderBy(f=>new FileInfo(Path.Combine(f,"aligned.mid.patterns.json")).Length).FirstOrDefault();
            if(classic!=null)
            {
                Check(midi.Load(Path.GetFullPath(Path.Combine(classic,"aligned.mid"))),"A version-5 bundle loads: "+midi.Status);
                await Task.Delay(1500);var v5=midi.Prepared;int before=errors.Count;
                foreach(var section in v5.Sections.Take(4))await At(section.Start+1,200);
                Check(v5.Version==5&&!v5.HasMotifs()&&deck.StructureEnabled&&!deck.StructureActive&&deck.FeaturedRadius>0&&errors.Count==before,$"U8 · {Path.GetFileName(classic)} (version {v5.Version}) draws the classic wheel with the structure toggle on, without exceptions");
            }
            else results.Add("SKIP: U8 needs a version-5 bundle in PreparedSongs/Library");

            // U2: the fixture is version 6 with a motif layer.
            await Task.Run(()=>{Prep("--write-fixture","motif-ternary",Score);Prep(Score);});
            Check(midi.Load(Path.GetFullPath(Score)),"The motif-ternary fixture loads: "+midi.Status);
            var song=midi.Prepared;midi.Pause();var model=MotifWheelModel.For(song);var nodes=model.Nodes;
            Check(song.Version==6&&song.HasMotifs()&&model.Valid,$"U2 · the fixture is version {song.Version} with a motif layer: {song.Motifs.Caption}");
            await Task.Delay(1500);
            var principal=song.Motifs.Streams[song.Motifs.PrincipalStream];featured=(feature.Track,feature.Channel);feature.Select(principal.Track,principal.Channel);
            await At(nodes[model.Sections[0]].Start+1,300);
            Check(deck.StructureActive&&deck.StructureModel==model,"The structure wheel replaces orbits, planet and moon for a bundle with motifs");
            Check(Vector2.Distance(deck.MetaCenter,deck.FeaturedCenter)<.001f&&deck.FeaturedRadius>0,$"FeaturedCenter is the wheel's centre and FeaturedRadius ring 3's inner edge ({deck.FeaturedRadius:0} px)");

            // U3: at 12 beats the lit arcs of rings 1–3 are the model's path; while playing, the rings only advance.
            var path=MotifWheelModel.Path.None;
            foreach(int s in model.Sections)
                for(int k=0;k<4;k++)
                {
                    await At(nodes[s].Start+(nodes[s].End-nodes[s].Start)*(k+.5)/4+.13,120);double beat=Now();
                    model.Resolve(beat,feature.Track,feature.Channel,ref path);
                    string Name(int n)=>n>=0?nodes[n].Label:"—";
                    bool unit=path.Unit>=0?deck.LitUnit==path.Unit:deck.LitUnit<0&&deck.LitCell==path.Cell;
                    Check(deck.LitSection==path.Section&&deck.LitPhrase==path.Phrase&&unit&&deck.StructurePath.Cell==path.Cell,
                        $"U3 · beat {beat:0.##}: rings 1–3 light {Name(deck.LitSection)} · {Name(deck.LitPhrase)} · {(deck.LitUnit>=0?Name(deck.LitUnit):"cell "+deck.LitCell)}, the model's path");
                }
            await At(nodes[model.Sections[0]].Start+.6,200);midi.Play();
            float last2=float.NaN,last3=float.NaN,lastTurns=float.NaN;int lastSection=-1,lastPhrase=-1,frames=0,backwards=0,turns=0;
            for(float until=Time.realtimeSinceStartup+3;Time.realtimeSinceStartup<until;)
            {
                await Task.Yield();frames++;var now=deck.StructurePath;
                float a2=deck.StructureRing2.resolvedStyle.rotate.angle.ToDegrees(),a3=deck.StructureRing3.resolvedStyle.rotate.angle.ToDegrees(),t=(float)deck.RackTurns;
                if(now.Section==lastSection&&a2<last2-1e-3f)backwards++;if(now.Phrase==lastPhrase&&a3<last3-1e-3f)backwards++;if(t>lastTurns+1e-7f)backwards++;
                if(now.Phrase==lastPhrase&&a3>last3+1e-3f)turns++;
                last2=a2;last3=a3;lastTurns=t;lastSection=now.Section;lastPhrase=now.Phrase;
            }
            midi.Pause();
            Check(frames>20&&backwards==0&&turns>frames/3,$"U3 · while playing the rings only advance ({frames} frames, ring 3 turned in {turns}, none backwards)");

            // U4: rotation is transposition, a mirror is an inversion, an alteration is a spark.
            var cells=model.Cells;int member=-1;
            for(int c=principal.FirstCell;c<principal.FirstCell+principal.CellCount&&member<0;c++)
            {
                var x=cells[c];var q=model.Paradigms[x.Paradigm];
                if(q.Clock==7&&x.Steps==1&&!x.Real&&!x.Inverted&&q.PrototypeCell>=0&&q.PrototypeCell<c&&cells[q.PrototypeCell].Steps==0&&!cells[q.PrototypeCell].Inverted)member=c;
            }
            Check(member>=0,"The fixture's principal line has a member a scale step above its prototype");
            int prototype=model.Paradigms[cells[member].Paradigm].PrototypeCell;
            await At(cells[prototype].Start+.05,200);float a0=deck.StructureGlyph.resolvedStyle.rotate.angle.ToDegrees();
            await At(cells[member].Start+.05,200);float a1=deck.StructureGlyph.resolvedStyle.rotate.angle.ToDegrees();
            float turned=Mathf.Repeat(a1-a0,360);
            Check(deck.StructurePath.Cell==member&&Mathf.Abs(turned-360f/7)<=.5f,$"U4 · {model.Paradigms[cells[member].Paradigm].Letter} at beat {cells[member].Start} (Steps +1) is drawn turned {turned:0.00}° from its prototype at {cells[prototype].Start} (360/7 = {360f/7:0.00}°)");
            int inverted=-1;
            for(int c=0;c<cells.Length&&inverted<0;c++){var x=cells[c];var lane=song.Motifs.Streams[x.Stream];if(x.Inverted&&model.StreamFor(lane.Track,lane.Channel)==x.Stream)inverted=c;}
            Check(inverted>=0,"The fixture has an inverted member on a lane's own stream");
            var invertedLane=song.Motifs.Streams[cells[inverted].Stream];feature.Select(invertedLane.Track,invertedLane.Channel);
            await At(cells[inverted].Start+.05,200);float mirror=deck.StructureGlyph.resolvedStyle.scale.value.x;
            Check(deck.StructurePath.Cell==inverted&&mirror==-1,$"U4 · the inverted {model.Paradigms[cells[inverted].Paradigm].Letter} at beat {cells[inverted].Start} ({invertedLane.Name}) is drawn with scale.x = {mirror}");
            feature.Select(principal.Track,principal.Channel);
            int coda=Array.FindIndex(nodes,n=>n.Kind==PreparedPatternSong.MotifKinds.Coda),altered=-1;
            for(int c=principal.FirstCell;c<principal.FirstCell+principal.CellCount&&altered<0&&coda>=0;c++)
            {
                var x=cells[c];if(x.Start<nodes[coda].Start||x.Start>=nodes[coda].End||x.FirstSpark<0)continue;
                for(int i=x.FirstSpark;i<x.FirstSpark+x.SparkCount;i++)if(song.Motifs.Sparks[i].Kind==PreparedPatternSong.MotifSparks.Altered)altered=c;
            }
            Check(altered>=0,"The coda has a cell with an altered note");
            await At(cells[altered].Start+.05,200);
            Check(deck.StructurePath.Cell==altered&&deck.AlteredSparksDrawn>=1,$"U4 · the coda cell at beat {cells[altered].Start} draws {deck.AlteredSparksDrawn} altered spark ({song.Motifs.Sparks[cells[altered].FirstSpark].Tag})");

            // U5: inside A′ the ghost maps into A and jumps the omitted span once; on the wheel it leaps once, and the omission
            // is lit from before the jump until after it.
            Check(model.Omissions.Length>=1,"The fixture's reprise leaves bars out");
            var omission=model.Omissions[0];var copy=nodes[omission.Copy];var source=nodes[omission.Prototype];
            int jumps=0;bool insideSource=true,acrossOmission=false;double previous=double.NaN;
            for(double b=copy.Start;b<copy.End;b+=1/32.0)
            {
                if(!model.Ghost(b,out double from,out _)){previous=double.NaN;continue;}
                insideSource&=from>=source.Start-1e-6&&from<source.End;
                if(!double.IsNaN(previous)&&from-previous>.5){jumps++;acrossOmission=previous<=omission.SourceStart+.1&&from>=omission.SourceEnd-.1;}
                previous=from;
            }
            Check(insideSource&&jumps==1&&acrossOmission,$"U5 · during {copy.Label} the ghost maps into {source.Label} and jumps [{omission.SourceStart}, {omission.SourceEnd}) exactly once");
            int leaps=deck.GhostLeaps;await At(omission.TargetAt-1.5,200);midi.Play();
            bool litBefore=false,litAfter=false;
            for(float until=Time.realtimeSinceStartup+6;Time.realtimeSinceStartup<until&&Now()<omission.TargetAt+1.5;)
            {
                await Task.Yield();double b=Now();bool lit=deck.OmissionLit;
                if(b<omission.TargetAt-.25)litBefore|=lit;if(b>omission.TargetAt+.25)litAfter|=lit;
            }
            midi.Pause();
            Check(deck.GhostLeaps-leaps==1&&litBefore&&litAfter,$"U5 · playing through {copy.Label}'s jump at beat {omission.TargetAt}, the ghost leaps once and the slit is lit before and after the skip ({omission.Tooltip})");

            // U6: the omission anchor is a point on ring 1 inside the slit, and a cue with it points the story arrow there.
            string anchor="omission:"+copy.Label;Vector2 point=default;double inside=double.NaN;
            foreach(double offset in new[]{1.0,4,8,12,16,20,24,28})
            {
                if(copy.Start+offset>=copy.End)break;await At(copy.Start+offset,150);
                if(!deck.TryGetAnchor(anchor,out point))continue;var world=deck.Overlay.LocalToWorld(point);float w=root.resolvedStyle.width,h=root.resolvedStyle.height;
                float left=views.PanelHidden?20:(root.Q("controls")?.resolvedStyle.width??0)+20;
                if(world.x>=left&&world.x<=w-10&&world.y>=65&&world.y<=h-110){inside=copy.Start+offset;break;}
            }
            Check(!double.IsNaN(inside),$"U6 · {anchor} resolves to a point the story arrow may reach ({point})");
            double slitFrom=deck.Ring1Phase(omission.SourceEnd),slitTo=deck.Ring1Phase(omission.SourceStart),at=Phase(point,deck.MetaCenter);
            bool within=slitFrom<=slitTo?at>=slitFrom&&at<=slitTo:at>=slitFrom||at<=slitTo;float radius=(point-deck.MetaCenter).magnitude;
            Check(within&&Mathf.Abs(radius-deck.Ring1Radius)<5,$"U6 · at beat {inside:0.##} {anchor} is on ring 1 (r {radius:0.0}, ring {deck.Ring1Radius:0.0}) inside the slit (phase {at:0.000} in [{slitFrom:0.000}, {slitTo:0.000}])");
            var cue=new SongDirector.Cue{start=0,end=midi.Duration,text="A′ leaves bars out",view="Overview",stem="",annotationTarget="patterns",annotationLabel="the bars A′ leaves out",annotationAnchor=anchor};
            var story=new SongDirector.Story{version=1,title="motif fixture",duration=midi.Duration,cues=new[]{cue}};
            var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            typeof(SongDirector).GetField("story",flags).SetValue(director,story);typeof(SongDirector).GetField("current",flags).SetValue(director,0);
            typeof(SongDirector).GetProperty("Directing").GetSetMethod(true).Invoke(director,new object[]{true});
            try
            {
                await Task.Delay(250);deck.TryGetAnchor(anchor,out point);var expected=deck.Overlay.LocalToWorld(point);var target=arrows.FirstTarget;
                Check(arrows.ArrowCount==1&&Vector2.Distance(target,expected)<1.5f,$"U6 · a cue anchored at {anchor} points the story arrow at the slit ({target} against {expected})");
            }
            finally{director.SetDirecting(false);typeof(SongDirector).GetField("story",flags).SetValue(director,null);typeof(SongDirector).GetField("current",flags).SetValue(director,-2);}

            // U7: a click on a ring arc seeks to that node's start within a frame (ring 2: the next phrase; ring 1: the next section).
            int first=model.Sections[0];var phrase=nodes[nodes[first].FirstChild+1];
            await At(nodes[first].Start+.6,200);
            Check(deck.TryGetAnchor($"node:{nodes[first].Label}/{phrase.Label}",out var arc),"Ring 2 has an arc for "+phrase.Label);
            Click(deck,arc);await Task.Yield();
            Check(Math.Abs(Now()-phrase.Start)<.02,$"U7 · a click on ring 2's {phrase.Label} seeks to its start, beat {phrase.Start} (now {Now():0.###})");
            var next=nodes[model.Sections[1]];
            Check(deck.TryGetAnchor("section:"+next.Label,out arc),"Ring 1 has an arc for "+next.Label);
            Click(deck,arc);await Task.Yield();
            Check(Math.Abs(Now()-next.Start)<.02,$"U7 · a click on ring 1's {next.Label} seeks to its start, beat {next.Start} (now {Now():0.###})");

            // U9 (allocation): no GC allocation inside the structure wheel's marker while it plays, after load.
            await At(copy.Start-3,300);midi.Play();await Task.Delay(500);
            var (bytes,profiled,dirty)=await PerformanceValidation.MarkerAllocations("Resonance.PatternWheels.Structure",4);midi.Pause();
            Check(profiled>20&&bytes==0,$"U9 · {profiled} frames across {copy.Label}'s start allocate {bytes} bytes inside Resonance.PatternWheels.Structure ({dirty} frames with any)");

            await At(omission.TargetAt+.5,400);ScreenCapture.CaptureScreenshot($"{Checks}/motif-wheel.png");await Task.Delay(250);
            Check(errors.Count==0,"No errors or exceptions were logged: "+string.Join(" | ",errors.Take(3)));
            results.Add("ALL MOTIF WHEEL CHECKS PASSED");
        }
        catch(Exception e){results.Add("FAIL: "+e.Message);UnityEngine.Debug.LogException(e);}
        finally
        {
            Application.logMessageReceived-=Log;deck.StructureEnabled=enabled;if(featured.track!=-1||featured.channel!=-1)feature.Select(featured.track,featured.channel);
            Directory.CreateDirectory(Checks);File.WriteAllLines($"{Checks}/motif-wheel.txt",results);UnityEngine.Debug.Log(string.Join("\n",results));
        }
    }
}
