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
    [MenuItem("Tools/Resonance/Check persistence and pattern decks")]
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
        var panel=root.Q<PatternWheelDeck>();var scroll=root.Q<ScrollView>("controls");Vector2 offset=scroll.scrollOffset;
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
            Check(midi.Prepared!=null && midi.Prepared.Disks.Length>0,"Song uses precomputed pattern discs");
            var target=midi.SongForm.Sections[1];
            await Click($"{target.Family.Name} · bar {target.FirstBar+1}");
            Check(Math.Abs(midi.Position-midi.AudioTime(midi.Cycles.SecondsAt(target.Start)))<.001,"Section navigation seeks the recording clock");
            Check(!ExplorerInputFocus.ViewportOwnsKeyboard,"Pattern deck controls keep keyboard focus in UI");
            midi.Seek(midi.Duration*.5);panel.Tick();
            Check(Math.Abs(midi.Position/midi.Duration-.5)<.000001,"Half the recording produces half a meta-disc revolution");
            Check(Math.Abs(CyclicOrrery.Phase(4,0,16)-.25)<.000001 && Math.Abs(CyclicOrrery.Phase(8,0,16)-.5)<.000001,"Chord boundaries do not restart a progression cycle");
            bool reduced=Main.ReducedMotion;Main.ReducedMotion=true;
            foreach(int interval in new[]{0,1,3,4,5,7,8,9,11})
            {
                main.ClearVisualMemory();int end=interval==0?51:39+interval;
                main.SetNotes(new List<Tuple<int,float>>{Tuple.Create(39,.6f),Tuple.Create(end,.6f)},false);await Task.Delay(40);
                var connection=main.GetComponentsInChildren<Chord>().First(c=>!c.Releasing&&c.Note1.Index==39&&c.Note2.Index==end).GetComponent<LineRenderer>();
                int mid=connection.positionCount/2;var straight=Vector3.Lerp(connection.GetPosition(0),connection.GetPosition(connection.positionCount-1),mid/(float)(connection.positionCount-1));
                float deviation=Vector3.Distance(straight,connection.GetPosition(mid));
                Check(interval is 0 or 4 or 8?deviation<.0001f:deviation>.01f,"Interval "+interval+" follows its torus/cross-section route");
            }
            Main.ReducedMotion=reduced;main.Silence();main.ClearVisualMemory();
            main.SetNotes(new List<Tuple<int,float>>{Tuple.Create(39,1f)},false);await Task.Delay(25);
            float attackScale=note.VisualScale;var renderer=note.GetComponentInChildren<Renderer>();var attackColor=renderer.sharedMaterial.GetColor("_BaseColor");
            await Task.Delay(220);float settledScale=note.VisualScale;var settledColor=renderer.sharedMaterial.GetColor("_BaseColor");
            Check(attackScale>settledScale*1.15f,"Note attack starts larger before settling");
            Check(Mathf.Min(attackColor.r,attackColor.g,attackColor.b)/attackColor.maxColorComponent>Mathf.Min(settledColor.r,settledColor.g,settledColor.b)/settledColor.maxColorComponent,"Note attack starts whiter before settling");
            main.Silence();
            var drum=midi.Prepared.Notes.First(n=>n.Channel==10&&midi.Cycles.SecondsAt(n.Beat)>1);
            Check(drum.RippleFrequency>0&&drum.HighHz>drum.LowHz,"Drum frequency bands are precomputed");
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(drum.Beat))-.08);midi.Play();await Task.Delay(230);
            var deck=main.GetComponent<DrumPatternDeck>();
            Check(deck.VisibleRippleCount>0,"Crossing the drum playhead emits a ripple");
            var rootDeck=main.transform.Find("Percussion CD changer · twelve o'clock playhead");
            Check(rootDeck.localPosition.y<-.8f,"Drum deck stays below the torus in world space");
            var rippleLines=rootDeck.GetComponentsInChildren<LineRenderer>().Where(l=>l.name=="Percussion energy ripple"&&l.enabled).ToArray();
            Check(rippleLines.All(l=>Enumerable.Range(0,l.positionCount).All(i=>Mathf.Abs(l.GetPosition(i).y-.015f)<.00001f)),"Percussion energy expands within the disc plane");
            midi.Pause();midi.Seek(25);
            midi.Play();await Task.Delay(400);
            Check(main.TonalField.ResidualEnergy.Sum()>0,"MIDI transport deposits integrated harmonic energy");
            midi.Pause();
            results.Add("ALL PERSISTENCE / PATTERN DECK CHECKS PASSED");
        }
        catch(Exception e){results.Add("FAIL: "+e);Debug.LogError(e);}
        finally
        {
            InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState());
            InputSystem.RemoveDevice(testKeyboard);originalKeyboard?.MakeCurrent();
            midi.Pause();midi.Seek(position);main.Silence();
            main.TonalField.ClearMemory();main.ResonanceHalfLife=halfLife;main.Synth.Volume=volume;AudioListener.volume=listener;
            main.NoteReleaseSeconds=noteSeconds;main.VisualReleaseSeconds=chordSeconds;main.ClearVisualMemory();
            if(playing)midi.Play();else main.PlayKeys(originalNotes);
            scroll.scrollOffset=offset;ExplorerInputFocus.ClaimViewport();
            Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/orrery.txt",results);Debug.Log(string.Join("\n",results));
        }
    }
}
