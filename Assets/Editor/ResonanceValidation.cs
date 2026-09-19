using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public static class ResonanceValidation
{
    [MenuItem("Tools/Resonance/Run runtime regression checks")]
    public static async void Run()
    {
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();
        if(!EditorApplication.isPlaying || main==null){Debug.LogError("Run checks in Play Mode.");return;}
        int oldKey=main.currentKey; float volume=main.Synth.Volume;
        var oldNotes=main.ActiveNotes.ToList(); bool minor=main.MinorMode;
        var midi=main.GetComponent<MidiPlayer>(); bool wasPlaying=midi.IsPlaying; double position=midi.Position;
        var results=new List<string>();
        void Check(bool pass,string name){if(!pass)throw new Exception(name);results.Add("PASS: "+name);}
        try
        {
            midi.Pause();main.Synth.Volume=0;main.MinorMode=false;
            var meshes=main.GetComponentsInChildren<UmbilicField>(true).Select(f=>f.SurfaceMesh).ToArray();
            Check(meshes.Length==1 && meshes[0]!=null,"Single continuous umbilic surface");
            main.PlayKeys(new List<Tuple<int,float>>{Tuple.Create(39,.7f),Tuple.Create(43,.7f),Tuple.Create(46,.7f)});
            for(int key=0;key<12;key++)
            {
                main.ChangeKey(key,0);
                var positions=main.GetComponentsInChildren<Note>(true).OrderBy(n=>n.Index).Select(n=>n.transform.position).ToArray();
                main.ChangeKey((key+5)%12,0);main.ChangeKey(key,.05f);await Task.Delay(180);
                var notes=main.GetComponentsInChildren<Note>(true).OrderBy(n=>n.Index).ToArray();
                Check(notes.Select((n,i)=>Vector3.Distance(n.transform.position,positions[i])).Max()<.0001f,"Canonical key pose "+key);
            }
            main.ChangeKey(0,0);main.ChangeKey(3,2);await Task.Delay(120);main.ChangeKey(8,.06f);await Task.Delay(180);
            var interrupted=main.GetComponentsInChildren<Note>(true).OrderBy(n=>n.Index).Select(n=>n.transform.position).ToArray();
            main.ChangeKey(8,0);var final=main.GetComponentsInChildren<Note>(true).OrderBy(n=>n.Index).ToArray();
            Check(final.Select((n,i)=>Vector3.Distance(n.transform.position,interrupted[i])).Max()<.0001f,"Interrupted transition reaches canonical pose");
            await Task.Delay(80);
            foreach(var chord in main.GetComponentsInChildren<Chord>())
            {
                var line=chord.GetComponent<LineRenderer>();
                Check(Vector3.Distance(line.GetPosition(0),chord.Note1.transform.position)<.0001f && Vector3.Distance(line.GetPosition(line.positionCount-1),chord.Note2.transform.position)<.0001f,"Held line follows endpoints");
            }
            for(int repeat=0;repeat<50;repeat++)main.ChangeKey(repeat%12,0);
            Check(meshes.All(m=>m!=null && main.GetComponentsInChildren<MeshFilter>(true).Any(f=>f.sharedMesh==m)),"Existing surface meshes are reused");
            int peak=main.GetComponentsInChildren<Chord>(true).Length;
            for(int i=0;i<50;i++){main.Silence();main.PlayKeys(oldNotes.Count>0?oldNotes:new List<Tuple<int,float>>{Tuple.Create(39,.7f),Tuple.Create(43,.7f),Tuple.Create(46,.7f)});}
            Check(main.GetComponentsInChildren<Chord>(true).Length<=Math.Max(peak,3),"Interval renderer pool is reused");
            var doc=main.GetComponent<UnityEngine.UIElements.UIDocument>();
            var panel=UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.ScrollView>(doc.rootVisualElement,"controls");
            Check(panel.contentContainer.childCount>40 && panel.contentContainer.layout.height>panel.contentViewport.layout.height,"Explorer content scrolls");
            foreach(var frame in HarmonyModel.Evaluate(1,"0m > PM > M"))main.PlayKeys(frame.Pitches.Select(pc=>Tuple.Create(pc+36,.7f)).ToList());
            Check(HarmonyModel.Chord(main.ActiveNotes.Select(n=>n.Item1))=="C","Guided ii-V-I reaches C major");
            if(midi.Loaded)
            {
                midi.Play();await Task.Delay(250);double p=midi.Position;midi.SetSpeed(1.5f);
                Check(Math.Abs(midi.Position-p)<.1,"Speed change preserves playhead");
                midi.Seek(midi.Duration*.5);Check(Math.Abs(midi.Position-midi.Duration*.5)<.1,"Seek reaches requested time");
                midi.Pause();Check(main.ActiveNotes.Count==0,"Pause releases voices");midi.SetSpeed(1);
            }
            results.Add("ALL RUNTIME CHECKS PASSED");
        }
        catch(Exception e){results.Add("FAIL: "+e);Debug.LogError(e);}
        finally
        {
            midi.Pause();main.MinorMode=minor;main.ChangeKey(oldKey,0);main.PlayKeys(oldNotes);main.Synth.Volume=volume;
            if(wasPlaying){midi.Seek(position);midi.Play();}
            Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/runtime.txt",results);
            Debug.Log(string.Join("\n",results));
        }
    }
}
