using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public static class AttackLayoutValidation
{
    [MenuItem("Tools/Resonance/Check upper layout and transient attacks")]
    public static async void Run()
    {
        if(!EditorApplication.isPlaying)return;
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();
        double position=midi.Position;bool playing=midi.IsPlaying;float volume=AudioListener.volume;GameObject test=null;var results=new List<string>();
        void Check(bool ok,string message){if(!ok)throw new Exception(message);results.Add("PASS: "+message);}
        try{
            AudioListener.volume=0;midi.Pause();Camera.main.GetComponent<CameraControl>().ResetView();await Task.Delay(80);
            var view=Camera.main.WorldToViewportPoint(main.transform.position);
            Check(view.y>.5f&&view.x>.3f&&view.x<.7f,"Larger default torus is framed above the drum wheel in its own viewport");
            var drum=main.GetComponent<DrumPatternDeck>().WheelTransform;
            Check(drum.localPosition.y<=-2.8f,"Drum wheel sits farther beneath the torus in the shared 3D view");
            Check((Camera.main.cullingMask&(1<<drum.gameObject.layer))!=0,"Main camera renders the drum wheel");
            test=new GameObject("Chord attack validation");test.transform.SetParent(main.transform,false);
            var line=test.AddComponent<LineRenderer>();line.sharedMaterial=new Material(Resources.Load<Shader>("HarmonicGlow"));
            var chord=test.AddComponent<Chord>();var notes=main.GetComponentsInChildren<Note>();
            chord.Init(notes.First(n=>n.Index==39),notes.First(n=>n.Index==46),line);chord.Drive(.7f,.7f,Color.blue,Color.red,1);
            Check(chord.AttackFlash==1,"Chord starts with a white attack");await Task.Delay(330);
            Check(chord.AttackFlash<.02f,"White chord flash decays rapidly into the harmonic color");
            chord.Strike();Check(chord.AttackFlash==1,"Repeated note attacks re-energize held chords");
            var kicks=midi.Prepared.Notes.Where(n=>n.Channel==10&&(n.Pitch==35||n.Pitch==36)).ToArray();
            var kick=kicks.First(k=>k.Beat>2&&kicks.Count(n=>Math.Abs(midi.Cycles.SecondsAt(n.Beat)-midi.Cycles.SecondsAt(k.Beat))<.35)==1);
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(kick.Beat))-.06);midi.Play();await Task.Delay(160);
            var rings=main.GetComponentsInChildren<LineRenderer>().Where(l=>l.name=="Percussion energy ripple"&&l.enabled).ToArray();
            int kickRings=rings.Count(l=>{var origin=(l.GetPosition(0)+l.GetPosition(96))*.5f;return Mathf.Abs(origin.z-kick.StrikeRadius)<.01f;});
            Check(kickRings==1,"Kick emits exactly one crest with no follow waves");
            results.Add("ALL LAYOUT / ATTACK CHECKS PASSED");
        }catch(Exception e){results.Add("FAIL: "+e);}
        finally{if(test!=null)UnityEngine.Object.Destroy(test);midi.Pause();midi.Seek(position);if(playing)midi.Play();AudioListener.volume=volume;File.WriteAllLines("Temp/ResonanceChecks/attack-layout.txt",results);}
    }
}
