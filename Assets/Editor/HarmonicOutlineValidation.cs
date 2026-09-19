using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
public static class HarmonicOutlineValidation
{
    public static async void Run(){
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();if(!EditorApplication.isPlaying||!midi.Loaded)return;
        var results=new List<string>();double position=midi.Position;bool playing=midi.IsPlaying;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);results.Add("PASS: "+message);}
        try{
            midi.Pause();var deck=main.GetComponent<UIDocument>().rootVisualElement.Q<PatternWheelDeck>();
            midi.Seek(10);await Task.Delay(100);float scroll=deck.RackPixels;double turns=deck.RackTurns;
            midi.Seek(11);await Task.Delay(100);
            Check(deck.RackPixels>scroll&&deck.RackTurns<turns,"Gear rotates continuously with rack displacement");
            Check(Vector2.Distance(deck.MetaCenter,deck.FeaturedCenter)<.001f,"Featured pattern remains centered on the gear");
            Check(main.GetComponent<DrumPatternDeck>().WheelTransform.localScale==Vector3.one*1.25f,"Drum wheel enlarged exactly 25 percent");
            var dominance=main.GetComponent<TonalDominance>();var path=new List<Vector3>();
            foreach(var test in new[]{(name:"major",root:0,third:4),(name:"minor",root:0,third:3),(name:"secondary dominant",root:2,third:4),(name:"Neapolitan",root:1,third:4)}){
                main.SetNotes(new List<Tuple<int,float>>{Tuple.Create(36+test.root,.9f),Tuple.Create(36+test.root+test.third,.7f),Tuple.Create(36+test.root+7,.6f)},false);await Task.Delay(230);
                Check(dominance.ChordRoot==test.root&&dominance.HasChord,"Dominant triad selected: "+test.name);
                var lines=main.GetComponentsInChildren<LineRenderer>().Where(l=>l.name=="Dominant chord surface outline"&&l.enabled).ToArray();Check(lines.Length==3,"Three subtle solid outline edges: "+test.name);
                int[] pcs={test.root,test.root+test.third,test.root+7};
                for(int i=0;i<3;i++){main.ChordOutlinePath(pcs[i],pcs[(i+1)%3],path);Check(Vector3.Distance(lines[i].GetPosition(0),path[0])<.0001f&&Vector3.Distance(lines[i].GetPosition(40),path[40])<.0001f,"Outer-register surface endpoints: "+test.name);}
            }
            results.Add("ALL GEAR / CHORD OUTLINE CHECKS PASSED");
        }catch(Exception e){results.Add("FAIL: "+e);}
        finally{main.Silence();midi.Seek(position);if(playing)midi.Play();File.WriteAllLines("Temp/ResonanceChecks/harmonic-outline.txt",results);}
    }
}
