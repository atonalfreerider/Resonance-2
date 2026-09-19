using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
public static class ChordPhaseValidation
{
    public static async void Run(){
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();if(!EditorApplication.isPlaying||!midi.Loaded)return;
        var outline=main.GetComponent<DominantChordOutline>();var saved=midi.Prepared.RegionPhases;double position=midi.Position;bool playing=midi.IsPlaying;GameObject go=null;
        var results=new List<string>();void Check(bool ok,string message){if(!ok)throw new Exception(message);results.Add("PASS: "+message);}
        void Render()=>typeof(DominantChordOutline).GetMethod("LateUpdate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(outline,null);
        try{
            midi.Pause();midi.Prepared.RegionPhases=new[]{new SongFormAnalysis.ChordStep{Start=0,End=4,Root=0,Quality="",Energy=0},new SongFormAnalysis.ChordStep{Start=4,End=8,Root=5,Quality="m",Energy=0},new SongFormAnalysis.ChordStep{Start=8,End=12,Root=-1}};
            foreach(double beat in new[]{0.0,1.0,3.99}){
                midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(beat)));main.Silence();Render();
                Check(outline.RegionVisible&&outline.RegionRoot==0,"Silent chord phase persists at beat "+beat);
                var fill=main.GetComponentsInChildren<MeshRenderer>().First(r=>r.name=="Active chord surface tint");Check(Mathf.Abs(fill.sharedMaterial.GetColor("_BaseColor").a-.19f)<.0001f,"Tint opacity is fixed, independent of energy");
            }
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(4)));main.Silence();Render();Check(outline.RegionVisible&&outline.RegionRoot==5,"Next phase replaces the region immediately");
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(8)));Render();Check(!outline.RegionVisible,"Rest removes the region immediately");
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(2)));Render();Check(outline.RegionVisible&&outline.RegionRoot==0,"Paused backward seek restores the correct phase immediately");
            go=new GameObject("Release shading check");go.transform.SetParent(main.transform,false);var line=go.AddComponent<LineRenderer>();line.sharedMaterial=new Material(Resources.Load<Shader>("HarmonicGlow"));var chord=go.AddComponent<Chord>();var notes=main.GetComponentsInChildren<Note>();chord.Init(notes[0],notes[7],line);chord.Drive(.8f,.8f,Color.red,Color.red,2.4f);
            await Task.Delay(350);chord.Release();float atRelease=line.sharedMaterial.GetColor("_BaseColor").r;await Task.Delay(450);
            float after=line.sharedMaterial.GetColor("_BaseColor").r;Check(after<atRelease*.2f,"Released chord glow drops below 20 percent within 450ms");Check(chord.VisualAmplitude>0,"Vibration dissipates along its full path while darkening");
            Color tail=Chord.ReleaseHue(Color.red,.45f);Check(Mathf.Abs(tail.r-tail.g)<.04f,"Released chord becomes subdued gray within 450ms");
            results.Add("ALL CHORD PHASE / RELEASE CHECKS PASSED");
        }catch(Exception e){results.Add("FAIL: "+e);}
        finally{if(go!=null)UnityEngine.Object.Destroy(go);midi.Prepared.RegionPhases=saved;main.Silence();midi.Seek(position);if(playing)midi.Play();File.WriteAllLines("Temp/ResonanceChecks/chord-phase.txt",results);}
    }
}
