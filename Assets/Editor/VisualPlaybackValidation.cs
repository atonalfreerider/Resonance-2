using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class VisualPlaybackValidation
{
    [MenuItem("Tools/Resonance/Check rack motion and recording library")]
    public static async void Run()
    {
        if(!EditorApplication.isPlaying)return;
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();var audio=midi.Recording;
        var result=new List<string>();var errors=new List<string>();float volume=AudioListener.volume;
        void Log(string message,string stack,LogType type){if(type==LogType.Exception||type==LogType.Error)errors.Add(message);}
        void Check(bool ok,string message){if(!ok)throw new Exception(message);result.Add("PASS: "+message);}
        Application.logMessageReceived+=Log;
        try{
            AudioListener.volume=0;midi.Pause();
            var root=main.GetComponent<UIDocument>().rootVisualElement;var deck=root.Q<PatternWheelDeck>();
            main.GetComponent<CameraControl>();Camera.main.GetComponent<CameraControl>().ResetView();
            var screen=Camera.main.WorldToViewportPoint(main.transform.position);
            Check(screen.x>.53f&&screen.y>.55f,"Torus framed in the upper right");
            Check(Camera.main.transform.forward.y<-.6f,"Opening camera is more overhead");
            double inside=midi.Prepared.Sections[1].Start;
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(inside+2)));await Task.Delay(100);float before=deck.RackPixels;double turns=deck.RackTurns;
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(inside+3)));await Task.Delay(100);Check(deck.RackPixels>before,"Rack scrolls within a section, not just at boundaries");
            Check(deck.RackTurns<turns,"Meta gear rolls continuously with upward rack travel");
            var second=midi.Prepared.Sections[2];midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(second.Start+1)));await Task.Delay(100);
            Check(deck.ActiveFamilyNode==second.Node,"Crossing a rack section trigger indexes the matching wheel");
            Check(main.GetComponentsInChildren<LineRenderer>().All(l=>l.name!="Drum playhead"),"Bright radial drum playhead removed");
            var triangle=main.GetComponentsInChildren<LineRenderer>().First(l=>l.name.Contains("triangle"));
            Check(triangle.positionCount==4,"Drum clock has a small triangular strike indicator");
            var kick=midi.Prepared.Notes.First(n=>n.Channel==10&&(n.Pitch==35||n.Pitch==36));
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(kick.Beat))-.06);midi.Play();await Task.Delay(160);
            var waves=main.GetComponentsInChildren<LineRenderer>().Where(l=>l.name=="Percussion energy ripple"&&l.enabled).ToArray();
            Check(waves.Any(l=>l.widthMultiplier>.1f),"Bass drum has a fat wide wave crest");
            Check(waves.Length>=1,"Drum strikes produce visible crests");midi.Pause();
            foreach(string name in new[]{"ComeFirst","Drank","JustAnotherInterlude","SaySo","Sexual","TouchxBeMyBaby"}){
                string path=Path.GetFullPath("PreparedSongs/Recordings/"+name+"/aligned.mid");audio.LoadPair("",path);
                for(int i=0;i<200&&(audio.Busy||!audio.Ready);i++)await Task.Delay(50);
                Check(audio.Ready&&Path.GetFullPath(midi.midiPath)==path,"WAV linked: "+name);
                midi.Seek(2);midi.Play();await Task.Delay(230);
                Check(audio.Source.isPlaying&&audio.Source.timeSamples>0,"WAV sample-clock playback: "+name);
                Check(main.Synth.GetComponent<AudioSource>().mute,"MIDI synth muted while recording plays: "+name);midi.Pause();
            }
            Check(errors.Count==0,"No Unity rendering exceptions during playback and button updates");
            result.Add("ALL VISUAL / RECORDING CHECKS PASSED");
        }catch(Exception e){result.Add("FAIL: "+e);}
        finally{Application.logMessageReceived-=Log;AudioListener.volume=volume;midi.Pause();audio.LoadPair("",Path.GetFullPath("PreparedSongs/TicketToRide-Restored/aligned.mid"));Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/visual-playback.txt",result);}
    }
}
