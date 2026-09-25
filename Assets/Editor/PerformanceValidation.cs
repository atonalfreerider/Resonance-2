using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

// Frame time in the states that matter: the overview, lyric mode sung and rapped, and a key
// change with tonal tension (the torus pose animating). Reads the main-thread time and every
// Resonance profiler marker per frame, and writes Temp/ResonanceChecks/performance.txt.
public static class PerformanceValidation
{
    const string Checks="Temp/ResonanceChecks",Lyric="PreparedSongs/Library/lyric-fixture-public-domain/aligned.mid",KeyChange="Temp/PatternWheel/verse-chorus.mid";
    sealed class Scenario{public string Name,Midi;public VisualizationViews.View View;public double Beat,Seconds;}
    [MenuItem("Tools/Resonance/Measure frame time (overview, lyric mode, key change)")]
    public static async void Run()
    {
        if(!EditorApplication.isPlaying){UnityEngine.Debug.LogWarning("Enter Play Mode first.");return;}
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();var views=main.GetComponent<VisualizationViews>();var audio=main.GetComponent<SongAudio>();
        var lines=new List<string>{$"Frame time · {DateTime.Now:yyyy-MM-dd HH:mm} · {Screen.width}×{Screen.height} · editor Play Mode (includes editor overhead)",""};
        var scenarios=new List<Scenario>
        {
            new(){Name="Overview · lyric song verse",Midi=Lyric,View=VisualizationViews.View.Overview,Beat=10,Seconds=6},
            new(){Name="Lyrics · sung verse",Midi=Lyric,View=VisualizationViews.View.Lyrics,Beat=90,Seconds=6},
            new(){Name="Lyrics · rap",Midi=Lyric,View=VisualizationViews.View.Lyrics,Beat=58,Seconds=6},
            new(){Name="Lyrics · verse 3 V/V tension",Midi=Lyric,View=VisualizationViews.View.Lyrics,Beat=210,Seconds=6},
            new(){Name="Overview · key change C→D",Midi=KeyChange,View=VisualizationViews.View.Overview,Beat=-1,Seconds=6},
            new(){Name="Overview · bridge V/V tension",Midi=KeyChange,View=VisualizationViews.View.Overview,Beat=-2,Seconds=6},
        };
        // A real song from the library, when one has a lyric sheet: its heavier score and stems.
        var real=Directory.Exists("PreparedSongs/Library")?Directory.GetDirectories("PreparedSongs/Library").Where(d=>File.Exists(Path.Combine(d,"lyrics.txt"))&&File.Exists(Path.Combine(d,"aligned.mid.prepared.json"))&&!d.Contains("lyric-fixture")).OrderBy(d=>d).FirstOrDefault():null;
        if(real!=null)
        {
            string score=Path.Combine(real,"aligned.mid").Replace('\\','/');string title=Path.GetFileName(real);
            scenarios.Add(new(){Name=$"Overview · {title}",Midi=score,View=VisualizationViews.View.Overview,Beat=-3,Seconds=6});
            scenarios.Add(new(){Name=$"Lyrics · {title}",Midi=score,View=VisualizationViews.View.Lyrics,Beat=-3,Seconds=6});
        }
        var names=new[]{"Main Thread","PlayerLoop"}.Concat(Perf.Names).ToArray();
        string loaded=null;
        try
        {
            foreach(var s in scenarios)
            {
                if(!File.Exists(s.Midi)){lines.Add($"{s.Name}: skipped, {s.Midi} missing (run the lyric fixture / pattern wheel checks first)");continue;}
                if(loaded!=s.Midi)
                {
                    string prepared=s.Midi+".prepared.json";
                    if(File.Exists(prepared)){audio.LoadPair("",Path.GetFullPath(s.Midi));for(int i=0;i<100&&(audio.Busy||!audio.Ready);i++)await Task.Delay(100);}
                    else midi.Load(Path.GetFullPath(s.Midi));
                    loaded=s.Midi;
                }
                double beat=s.Beat;
                if(beat==-1){var k=midi.Prepared.KeyChanges.FirstOrDefault();beat=k!=null?k.Beat-3:0;}
                if(beat==-2){var t=midi.Prepared.Tensions.FirstOrDefault(x=>x.Kind=="V/V");beat=t!=null?t.Start-3:0;}
                if(beat==-3){var first=midi.Prepared.Lyrics?.Syllables?.FirstOrDefault();beat=first!=null?first.Start+16:midi.Prepared.EndBeat*.3;}
                views.SetView(s.View);await Task.Delay(1500);
                midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(beat)));midi.Play();await Task.Delay(300);
                var recorders=names.Select(n=>n=="Main Thread"||n=="PlayerLoop"?ProfilerRecorder.StartNew(ProfilerCategory.Internal,n,4000):ProfilerRecorder.StartNew(ProfilerCategory.Scripts,n,4000)).ToArray();
                var deltas=new List<float>();var until=Time.realtimeSinceStartup+s.Seconds;
                while(Time.realtimeSinceStartup<until){await Task.Yield();deltas.Add(Time.unscaledDeltaTime*1000);}
                midi.Pause();
                lines.Add($"{s.Name}  ({deltas.Count} frames, {deltas.Count/s.Seconds:0} fps)");
                lines.Add($"  frame Δ ms  mean {deltas.Average():0.0}  p95 {P(deltas,.95):0.0}  max {deltas.Max():0.0}");
                var rows=new List<(string name,double mean,double p95)>();var series=new Dictionary<string,List<double>>();
                for(int r=0;r<recorders.Length;r++)
                {
                    var rec=recorders[r];if(!rec.Valid||rec.Count==0){rec.Dispose();continue;}
                    var samples=new List<ProfilerRecorderSample>(rec.Capacity);rec.CopyTo(samples);
                    var ms=samples.Select(x=>x.Value/1e6).ToList();rec.Dispose();series[names[r]]=ms;
                    rows.Add((names[r],ms.Average(),P(ms.Select(v=>(float)v).ToList(),.95)));
                }
                // The worst frame and what it spent its time on.
                if(series.TryGetValue("Main Thread",out var main_))
                {
                    int worst=main_.IndexOf(main_.Max());
                    var parts=series.Where(k=>k.Key.StartsWith("Resonance")&&k.Value.Count==main_.Count&&k.Value[worst]>=.5).OrderByDescending(k=>k.Value[worst]).Select(k=>$"{k.Key.Replace("Resonance.","")} {k.Value[worst]:0.0}");
                    lines.Add($"  worst frame {main_[worst]:0.0} ms: {string.Join(", ",parts)}");
                }
                foreach(var (name,mean,p95) in rows.Where(x=>x.name is "Main Thread" or "PlayerLoop"))lines.Add($"  {name,-38} mean {mean,6:0.00} ms  p95 {p95,6:0.00}");
                foreach(var (name,mean,p95) in rows.Where(x=>x.name.StartsWith("Resonance")).OrderByDescending(x=>x.mean).Where(x=>x.mean>=.02))lines.Add($"  {name,-38} mean {mean,6:0.00} ms  p95 {p95,6:0.00}");
                lines.Add("");
            }
        }
        catch(Exception e){lines.Add("FAIL: "+e.Message);UnityEngine.Debug.LogException(e);}
        finally{Directory.CreateDirectory(Checks);File.WriteAllLines(Checks+"/performance.txt",lines);UnityEngine.Debug.Log(string.Join("\n",lines));}
    }
    static float P(List<float> values,double q){if(values.Count==0)return 0;var s=values.OrderBy(v=>v).ToList();return s[Math.Min(s.Count-1,(int)(q*s.Count))];}
}
