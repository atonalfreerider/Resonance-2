using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
public static class VisualLifecycleValidation
{
    public static async void Run(){
        if(!EditorApplication.isPlaying)return;
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();
        var drum=main.GetComponent<DrumPatternDeck>();var overlay=main.GetComponent<SurfaceOverlay>();var outline=main.GetComponent<DominantChordOutline>();
        var results=new List<string>();var errors=new List<string>();double position=midi.Position;bool playing=midi.IsPlaying;bool midiEnabled=midi.enabled;
        void Log(string message,string stack,LogType type){if(type==LogType.Exception||type==LogType.Error)errors.Add(message);}
        void Check(bool ok,string name){if(!ok)throw new Exception(name);results.Add("PASS: "+name);}
        void Invoke(object target,string method)=>target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,null);
        Application.logMessageReceived+=Log;
        try{
            midi.Pause();
            for(int k=0;k<3;k++){
                drum.enabled=false;overlay.enabled=false;outline.enabled=false;await Task.Delay(60);
                drum.enabled=true;overlay.enabled=true;outline.enabled=true;
                Invoke(drum,"Load");
                var pins=drum.WheelTransform.GetComponentsInChildren<Transform>(true).Where(t=>t.name=="Drum variation slot").ToArray();
                Check(pins.Length>0&&pins.All(t=>t.localScale.x<=.0251f),"All waiting drum markers are small before the first update, restart "+k);
                var block=new MaterialPropertyBlock();foreach(var pin in pins){pin.GetComponent<Renderer>().GetPropertyBlock(block);Check(block.GetColor("_BaseColor").maxColorComponent<=.161f,"Initial drum brightness is dim");}
                await Task.Delay(100);
                Check(main.GetComponentsInChildren<LineRenderer>(true).Count(l=>l.name=="Dominant chord surface outline")==3,"Exactly one outline after re-enable");
            }
            var cyclesField=typeof(MidiPlayer).GetField("<Cycles>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic);var cycles=midi.Cycles;
            try{cyclesField.SetValue(midi,null);Invoke(drum,"Update");Check(!drum.WheelTransform.gameObject.activeSelf,"Missing timing data hides drums safely");}finally{cyclesField.SetValue(midi,cycles);}
            Invoke(drum,"Update");Check(drum.WheelTransform.gameObject.activeSelf,"Drums recover when timing data returns");
            var mainField=typeof(SurfaceOverlay).GetField("main",BindingFlags.Instance|BindingFlags.NonPublic);mainField.SetValue(overlay,null);Invoke(overlay,"LateUpdate");Check(mainField.GetValue(overlay)!=null,"Surface overlay reacquires its owner");
            var path=new List<Vector3>();float maxError=0;
            for(int root=0;root<12;root++)foreach(int third in new[]{3,4}){
                int[] pcs={root,root+third,root+7};var uv=pcs.Select(pc=>main.ChordRegionCoordinate(root,pc)).ToArray();
                for(int e=0;e<3;e++){main.ChordOutlinePath(pcs[e],pcs[(e+1)%3],path);for(int j=0;j<path.Count;j++)maxError=Mathf.Max(maxError,Vector3.Distance(path[j],main.ChordRegionPoint(Vector2.Lerp(uv[e],uv[(e+1)%3],j/40f))));}
            }
            Check(maxError<.001f,"Tint boundary follows all major/minor shortest-path outlines: "+maxError);
            var section=new PreparedPatternSong.Section{Chords=new[]{new SongFormAnalysis.ChordStep{Start=0,End=4,Root=0,Quality=""},new SongFormAnalysis.ChordStep{Start=4,End=8,Root=5,Quality=""}}};
            Check(PatternWheelDeck.NoteColor(section,1,0,false)==TonalColorField.Chord(0,0,false),"Instrument notes use tonic chord color");
            Check(PatternWheelDeck.NoteColor(section,5,0,false)==TonalColorField.Chord(5,0,false),"All instruments follow the prepared chord change");
            Check(PatternWheelDeck.NoteColor(section,5,0,true)==Color.white,"Lead vocal notes and trails are white");
            midi.enabled=false;
            main.SetNotes(new List<Tuple<int,float>>{Tuple.Create(36,.9f),Tuple.Create(40,.7f),Tuple.Create(43,.6f)},false);await Task.Delay(250);
            var fill=main.GetComponentsInChildren<MeshFilter>().First(f=>f.name=="Active chord surface tint");Check(fill.sharedMesh.vertexCount==325,"Chord fill is tessellated on the torus surface");
            Check(errors.Count==0,"No runtime exceptions during initialization, re-enable and missing-data checks: "+string.Join("; ",errors));
            results.Add("ALL VISUAL LIFECYCLE CHECKS PASSED");
        }catch(Exception e){results.Add("FAIL: "+e);}
        finally{Application.logMessageReceived-=Log;main.Silence();midi.enabled=midiEnabled;midi.Seek(position);if(playing)midi.Play();Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/visual-lifecycle.txt",results);}
    }
}
