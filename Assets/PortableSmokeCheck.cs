using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
public sealed class PortableSmokeCheck : MonoBehaviour
{
    string resultPath;readonly System.Collections.Generic.List<string> errors=new();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Launch(){
        var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"--portable-smoke-test");if(index<0||index+1>=args.Length)return;
        var test=new GameObject("Portable startup verification").AddComponent<PortableSmokeCheck>();test.resultPath=args[index+1];
    }
    void OnEnable(){Application.logMessageReceived+=Log;}
    void OnDisable(){Application.logMessageReceived-=Log;}
    void Log(string message,string stack,LogType type){if((type==LogType.Error||type==LogType.Exception)&&errors.Count<12&&!errors.Contains(message))errors.Add(message);}
    IEnumerator Start(){
        AudioListener.volume=0;float deadline=Time.realtimeSinceStartup+90;SongAudio audio=null;MidiPlayer midi=null;
        while(Time.realtimeSinceStartup<deadline){audio=UnityEngine.Object.FindAnyObjectByType<SongAudio>();midi=UnityEngine.Object.FindAnyObjectByType<MidiPlayer>();if(audio!=null&&audio.Ready&&midi!=null&&midi.Loaded)break;if(errors.Count>0){Finish(false,string.Join(" | ",errors));yield break;}yield return null;}
        if(audio==null||!audio.Ready||midi==null||!midi.Loaded){Finish(false,"Bundled song failed to load: "+audio?.Status);yield break;}
        bool local=Path.GetFullPath(midi.midiPath)==SongAudio.BundledScore&&Path.GetFullPath(audio.AudioPath).StartsWith(Path.GetDirectoryName(SongAudio.BundledScore));
        midi.Play();double start=midi.Position;yield return new WaitForSecondsRealtime(1);
        bool advancing=midi.Position>start+.1&&audio.Source.timeSamples>0;midi.Pause();bool regions=true;var outline=midi.GetComponent<DominantChordOutline>();
        foreach(var section in midi.Prepared.Sections){foreach(double fraction in new[]{0.0,.25,.5,.75,.999}){midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(section.Start+(section.End-section.Start)*fraction)));yield return null;yield return null;regions&=outline.RegionVisible;}}
        Finish(local&&advancing&&regions&&errors.Count==0,"Portable paths="+local+"; recording clock advances="+advancing+"; section regions persist="+regions+"; key="+midi.Prepared.Key+"; notes="+midi.Prepared.Notes.Length+"; errors="+string.Join(" | ",errors));
    }
    void Finish(bool ok,string detail){File.WriteAllText(resultPath,(ok?"PASS":"FAIL")+"\n"+detail);Application.Quit(ok?0:1);}
}
