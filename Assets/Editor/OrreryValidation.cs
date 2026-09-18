using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

public static class OrreryValidation
{
    [MenuItem("Tools/Resonance/Check persistence and orrery")]
    public static async void Run()
    {
        if(!EditorApplication.isPlaying)return;
        var originalKeyboard=Keyboard.current;
        var testKeyboard=InputSystem.AddDevice<Keyboard>();testKeyboard.MakeCurrent();
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();
        var results=new List<string>();float listener=AudioListener.volume,volume=main.Synth.Volume;
        double position=midi.Position;bool playing=midi.IsPlaying;string map=midi.SectionBoundaries;int sectionBars=midi.SectionBars;
        var originalNotes=main.ActiveNotes.ToList();float halfLife=main.ResonanceHalfLife;
        float noteSeconds=main.NoteReleaseSeconds,chordSeconds=main.VisualReleaseSeconds;
        var root=main.GetComponent<UIDocument>().rootVisualElement;
        var panel=root.Q<CyclicOrrery>();var scroll=root.Q<ScrollView>("controls");Vector2 offset=scroll.scrollOffset;
        void Check(bool pass,string name){if(!pass)throw new Exception(name);results.Add("PASS: "+name);}
        async Task Click(string text)
        {
            var button=panel.Query<Button>().ToList().First(b=>b.text==text);
            scroll.ScrollTo(button);ExplorerInputFocus.ClaimUI();button.Focus();await Task.Delay(100);
            InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState(Key.Enter));await Task.Delay(120);
            InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState());await Task.Delay(100);
        }
        try
        {
            AudioListener.volume=0;midi.Pause();main.Synth.Volume=.6f;main.ResonanceHalfLife=1;
            main.TonalField.ClearMemory();
            main.SetNotes(new List<Tuple<int,float>>{Tuple.Create(39,.7f)},false);await Task.Delay(220);
            float before=main.TonalField.ResidualEnergy[3];Check(before>0,"Held note deposits field energy");
            main.Silence();await Task.Delay(220);
            Check(main.TonalField.ResidualEnergy[3]>0 && main.TonalField.ResidualEnergy[3]<before,"Release leaves a decaying visual trace");
            main.SetNotes(new List<Tuple<int,float>>{Tuple.Create(46,.7f)},false);await Task.Delay(160);
            Check(main.TonalField.ResidualEnergy[3]>0 && main.TonalField.ResidualEnergy[10]>0,"Old and new tonal energies coexist");
            main.Silence();main.TonalField.ClearMemory();
            Check(main.TonalField.ResidualEnergy.All(e=>e==0),"Clear memory removes residual excitation");
            main.NoteReleaseSeconds=.6f;main.VisualReleaseSeconds=1.2f;
            var triad=new List<Tuple<int,float>>{Tuple.Create(39,.8f),Tuple.Create(43,.7f),Tuple.Create(46,.7f)};
            main.SetNotes(triad,false);await Task.Delay(100);
            var note=main.GetComponentsInChildren<Note>(true).First(n=>n.Index==39);
            var chord=main.GetComponentsInChildren<Chord>().First(c=>!c.Releasing);
            var line=chord.GetComponent<LineRenderer>();float scale=note.VisualScale,width=line.startWidth,light=line.sharedMaterial.GetColor("_BaseColor").r;
            Vector3 a=line.GetPosition(0),b=line.GetPosition(line.positionCount-1);
            main.Silence();await Task.Delay(200);
            Check(note.VisualAmplitude>0 && note.VisualScale<scale,"Released note shrinks gradually");
            Check(chord.gameObject.activeSelf && chord.Releasing && line.startWidth<width && line.sharedMaterial.GetColor("_BaseColor").r<light,"Chord thickness and light dissipate after note off");
            Check(Vector3.Distance(a,line.GetPosition(0))<.0001f && Vector3.Distance(b,line.GetPosition(line.positionCount-1))<.0001f,"Dissipating chord retains its full span");
            await Task.Delay(480);
            Check(note.VisualAmplitude==0 && chord.VisualAmplitude>0,"Notes disappear faster than chord energy");
            int pool=main.GetComponentsInChildren<Chord>(true).Length;main.SetNotes(triad,false);await Task.Delay(50);
            Check(main.GetComponentsInChildren<Chord>(true).Length==pool && !chord.Releasing,"Retrigger reuses the fading interval");
            main.Silence();await Task.Delay(1300);
            Check(!chord.gameObject.activeSelf,"Expired interval returns to the pool");
            Check(midi.SongForm!=null && midi.SongForm.Sections.Count>0,"Song has section and progression hierarchy");
            midi.RebuildSongForm(4,"1 Verse\n5 Chorus\n9 Verse");await Task.Delay(100);
            Check(midi.SongForm.Families.Count==2 && midi.SongForm.Sections.Count==3,"Editable form groups recurring verse sections");
            var sectionChoice=panel.Query<DropdownField>().ToList().First(d=>d.label=="Inspect section");
            sectionChoice.index=1;await Task.Delay(100);await Click("Go to section");
            Check(Math.Abs(midi.Position-midi.AudioTime(midi.Cycles.SecondsAt(midi.SongForm.Sections[1].Start)))<.001,"Section navigation seeks the mapped song transport");
            Check(!ExplorerInputFocus.ViewportOwnsKeyboard,"Orrery interactions keep keyboard focus in UI");
            midi.Seek(midi.Duration*.5);panel.Tick();
            Check(Math.Abs(panel.SongOrbitPhase-.5)<.000001,"Half the file produces half a solar orbit");
            Check(Math.Abs(CyclicOrrery.Phase(4,0,16)-.25)<.000001 && Math.Abs(CyclicOrrery.Phase(8,0,16)-.5)<.000001,"Chord boundaries do not restart a progression orbit");
            Check(CyclicOrrery.ChordColor(new SongFormAnalysis.ChordStep{Root=3},3).b>.9f && CyclicOrrery.ChordColor(new SongFormAnalysis.ChordStep{Root=8},3).r>.9f && CyclicOrrery.ChordColor(new SongFormAnalysis.ChordStep{Root=10},3).g>.9f,"Orrery I IV V share torus blue red green");
            midi.Play();await Task.Delay(400);
            Check(main.TonalField.ResidualEnergy.Sum()>0,"MIDI transport deposits integrated harmonic energy");
            midi.Pause();
            results.Add("ALL PERSISTENCE / ORRERY CHECKS PASSED");
        }
        catch(Exception e){results.Add("FAIL: "+e);Debug.LogError(e);}
        finally
        {
            InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState());
            InputSystem.RemoveDevice(testKeyboard);originalKeyboard?.MakeCurrent();
            midi.Pause();midi.RebuildSongForm(sectionBars,map);midi.Seek(position);main.Silence();
            main.TonalField.ClearMemory();main.ResonanceHalfLife=halfLife;main.Synth.Volume=volume;AudioListener.volume=listener;
            main.NoteReleaseSeconds=noteSeconds;main.VisualReleaseSeconds=chordSeconds;main.ClearVisualMemory();
            if(playing)midi.Play();else main.PlayKeys(originalNotes);
            scroll.scrollOffset=offset;ExplorerInputFocus.ClaimViewport();
            Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/orrery.txt",results);Debug.Log(string.Join("\n",results));
        }
    }
}
