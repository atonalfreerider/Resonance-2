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

public static class FieldInteractionValidation
{
    [MenuItem("Tools/Resonance/Check tonal field and input focus")]
    public static async void Run()
    {
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();
        if(!EditorApplication.isPlaying || main==null)return;
        var results=new List<string>();
        int originalKey=main.currentKey;float volume=main.Synth.Volume;
        var notes=main.ActiveNotes.ToList();
        var midi=main.GetComponent<MidiPlayer>();
        var keyboard=Keyboard.current;
        var root=main.GetComponent<UIDocument>().rootVisualElement;
        var text=root.Query<TextField>().ToList();var textValues=text.Select(t=>t.value).ToArray();
        void Check(bool pass,string name){if(!pass)throw new Exception(name);results.Add("PASS: "+name);}
        async Task Press(params Key[] keys)
        {
            InputSystem.QueueStateEvent(keyboard,new KeyboardState(keys));await Task.Delay(100);
            InputSystem.QueueStateEvent(keyboard,new KeyboardState());await Task.Delay(100);
        }
        try
        {
            midi.Stop();main.GetComponent<HarmonyExplorer>().StopLesson();main.Synth.Volume=0;
            // Field geometry follows the key pose in LateUpdate; wait for that frame.
            await Task.Delay(120);
            var field=main.GetComponentInChildren<UmbilicField>();var mesh=field.SurfaceMesh;var vertices=mesh.vertices;
            Check(vertices.Length==6025,"Continuous surface has expected topology");
            for(int j=0;j<=24;j++)Check(Vector3.Distance(vertices[j],vertices[240*25+j])<.00001f,"Longitudinal seam "+j);
            for(int i=0;i<240;i+=20)Check(Vector3.Distance(vertices[i*25+24],vertices[((i+80)%240)*25])<.00001f,"Shared umbilic edge "+i);
            for(int key=0;key<12;key++)
            {
                Color tonic=TonalColorField.Pitch(key,key),fourth=TonalColorField.Pitch(key+5,key),fifth=TonalColorField.Pitch(key+7,key);
                Check(tonic.b>.9f && fourth.r>.9f && fifth.g>.9f,"Key-relative color anchors "+key);
            }
            var chord=new List<Tuple<int,float>>{Tuple.Create(39,.7f),Tuple.Create(43,.7f),Tuple.Create(46,.7f)};
            main.PlayKeys(chord);await Task.Delay(100);
            Check(field.Energy.Sum()>0,"Notes excite the light field");
            Check(vertices.SequenceEqual(mesh.vertices),"Sound does not deform the umbilic geometry");
            var camera=Camera.main.transform;Vector3 position=camera.position;
            text.Last().Focus();await Task.Delay(60);
            Check(!ExplorerInputFocus.ViewportOwnsKeyboard,"Text field owns shortcuts");
            await Press(Key.A,Key.Digit1,Key.RightArrow,Key.Space,Key.Escape);
            Check(main.currentKey==originalKey,"Typing does not change tonic");
            Check(Vector3.Distance(position,camera.position)<.00001f,"UI arrows do not move camera");
            Check(!midi.IsPlaying,"UI space does not start MIDI");
            Check(main.ActiveNotes.Count==3,"UI escape / number input does not silence or play notes");
            foreach(var control in new VisualElement[]{root.Query<IntegerField>().First(),root.Query<Slider>().First(),root.Query<DropdownField>().First(),root.Query<Button>().First()})
            {
                control.Focus();await Task.Delay(40);Check(!ExplorerInputFocus.ViewportOwnsKeyboard,control.GetType().Name+" owns keyboard");
            }
            ExplorerInputFocus.ClaimViewport();await Task.Delay(40);
            Check(ExplorerInputFocus.ViewportOwnsKeyboard,"Viewport reclaims shortcuts");
            await Press(Key.RightArrow);
            Check(Vector3.Distance(position,camera.position)>.0001f,"Viewport arrows move camera");
            ExplorerInputFocus.ClaimViewport();await Press(Key.A);
            Check(main.currentKey==0,"Viewport tonic shortcut works");
            ExplorerInputFocus.ClaimViewport();InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Digit1));await Task.Delay(60);
            Check(main.ActiveNotes.Count==1,"Viewport note keyboard works");
            text.Last().Focus();await Task.Delay(60);
            Check(main.ActiveNotes.Count==0,"Giving focus to UI releases keyboard notes");
            InputSystem.QueueStateEvent(keyboard,new KeyboardState());await Task.Delay(40);
            main.PlayKeys(chord);await Press(Key.LeftCtrl,Key.Escape);
            Check(main.ActiveNotes.Count==0,"Ctrl-Escape is a global panic shortcut");
            results.Add("ALL FIELD / INPUT CHECKS PASSED");
        }
        catch(Exception e){results.Add("FAIL: "+e);Debug.LogError(e);}
        finally
        {
            if(keyboard!=null)InputSystem.QueueStateEvent(keyboard,new KeyboardState());
            for(int i=0;i<text.Count;i++)text[i].SetValueWithoutNotify(textValues[i]);
            main.ChangeKey(originalKey,0);main.PlayKeys(notes);main.Synth.Volume=volume;
            Camera.main.GetComponent<CameraControl>().ResetView();ExplorerInputFocus.ClaimViewport();
            Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/field-input.txt",results);Debug.Log(string.Join("\n",results));
        }
    }
}
