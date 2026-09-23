using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class AuroraValidation
{
    public static async void Run()
    {
        var results=new System.Collections.Generic.List<string>();
        void Check(bool condition,string description){if(!condition)throw new Exception(description);results.Add("PASS: "+description);}
        try{
            var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();
            var aurora=main.GetComponent<ChordAurora>();var region=main.GetComponent<DominantChordOutline>();
            Check(aurora!=null,"Aurora initialized");
            Check(ChordAurora.Release(1,0,.17f)<.05f,"Release dissipates 95% within 170ms");
            Check(ChordAurora.ModeShape(.5f,false)>.99f&&ChordAurora.ModeShape(.5f,true)<.001f,"Major has central crest; minor has a central node");
            for(int root=0;root<12;root++)foreach(int third in new[]{3,4}){
                var a=main.ChordRegionCoordinate(root,root);var b=main.ChordRegionCoordinate(root,root+third);var c=main.ChordRegionCoordinate(root,root+7);
                var uv=a*.3f+b*.35f+c*.35f;var n=main.ChordRegionNormal(uv);const float e=.0001f;
                var u=(main.ChordRegionPoint(uv+Vector2.right*e)-main.ChordRegionPoint(uv-Vector2.right*e)).normalized;
                var v=(main.ChordRegionPoint(uv+Vector2.up*e)-main.ChordRegionPoint(uv-Vector2.up*e)).normalized;
                Check(n.sqrMagnitude>.99f&&Mathf.Abs(Vector3.Dot(n,u))<.002f&&Mathf.Abs(Vector3.Dot(n,v))<.002f,"Surface-normal emission: "+root+" / "+third);
            }
            midi.Recording.LoadPair("",Path.GetFullPath("PreparedSongs/TicketToRide-Restored/aligned.mid"));
            for(int i=0;i<100&&!midi.Recording.Ready;i++)await Task.Delay(100);
            Check(midi.Recording.Ready,"Recording loaded");
            midi.Seek(18);midi.Play();await Task.Delay(1200);
            Check(aurora.Energy>.001f,"Recording and note energy drives the aurora");
            Check(aurora.EmitterRoot==region.RegionRoot,"Emitter is restricted to the current chord region");
            var deck=main.GetComponent<UIDocument>().rootVisualElement.Q<PatternWheelDeck>();
            var groups=midi.Prepared.Groups;
            Check(groups.Any(g=>g.Name.Contains("Verse")&&g.Name.Contains("Chorus")),"A recurring group holds the verse and chorus");
            Check(deck.ActiveGroup>=0&&groups[deck.ActiveGroup].Name.Contains("Verse"),"Pattern wheels mark the verse + chorus group during a verse");
            Directory.CreateDirectory("Temp/ResonanceChecks");ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/chord-aurora.png");
            await Task.Delay(300);midi.Pause();await Task.Delay(400);
            Check(aurora.Energy<.001f&&region.RegionVisible,"Wave decays after note-off while region persists");
            main.PlayKeys(new System.Collections.Generic.List<Tuple<int,float>>{Tuple.Create(24,.7f),Tuple.Create(27,.7f),Tuple.Create(31,.7f)});
            await Task.Delay(220);
            Check(aurora.MinorShape&&aurora.Energy>.1f,"Manually played minor chord uses split-lobe emission even with a song loaded");
            int bakes=aurora.MeshBakeCount;await Task.Delay(150);
            Check(aurora.MeshBakeCount==bakes,"GPU wave animates without rebuilding the baked mesh");
            ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/chord-aurora-minor.png");await Task.Delay(100);main.Silence();await Task.Delay(350);
            Check(aurora.Energy<.001f,"Manual note-off also dissipates quickly");
            results.Add("ALL AURORA / COMPOSITE GEAR CHECKS PASSED");
        }catch(Exception error){results.Add("FAIL: "+error);Debug.LogException(error);}
        finally{Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/aurora.txt",results);}
    }
}
