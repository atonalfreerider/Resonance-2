using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

public static class AuroraEnergyValidation
{
    public static async void Run()
    {
        var log=new List<string>();Main main=null;
        void Check(bool value,string message){if(!value)throw new Exception(message);log.Add("PASS: "+message);}
        try{
            main=UnityEngine.Object.FindAnyObjectByType<Main>();main.GetComponent<MidiPlayer>().Pause();var aurora=main.GetComponent<ChordAurora>();
            main.GetComponent<VisualizationViews>().SetView(VisualizationViews.View.Torus);
            main.PlayKeys(new List<Tuple<int,float>>{Tuple.Create(36,.25f)});await Task.Delay(300);
            Check(aurora.SingleNote&&aurora.EmitterNote==36&&aurora.Energy>.05f,"Single note emits at its actual register without requiring a chord region");
            float quiet=aurora.Energy;
            main.PlayKeys(new List<Tuple<int,float>>{Tuple.Create(36,.9f)});await Task.Delay(180);
            Check(aurora.Energy>quiet*2,"Source velocity raises emitter energy");
            ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/aurora-solo.png");await Task.Delay(100);
            main.PlayKeys(new List<Tuple<int,float>>{Tuple.Create(36,.7f),Tuple.Create(40,.7f),Tuple.Create(43,.7f)});await Task.Delay(250);
            Check(!aurora.SingleNote&&aurora.EmitterRoot==0,"Major chord follows only its curved triangular section");
            Check(aurora.Crosswind<.001f,"Chord members do not create crosswind");
            main.PlayKeys(new List<Tuple<int,float>>{Tuple.Create(36,.7f),Tuple.Create(40,.7f),Tuple.Create(43,.7f),Tuple.Create(42,.3f)});await Task.Delay(60);
            Check(!aurora.SingleNote&&aurora.EmitterRoot==0&&aurora.Crosswind>.03f,"Remote note bends the dominant emitter without becoming another source");
            main.StrikeNote(36,1);await Task.Delay(35);Check(aurora.Shock>.25f,"Repeated attacks launch shock impulses");
            ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/aurora-crosswind.png");await Task.Delay(100);
            main.Silence();await Task.Delay(400);Check(aurora.Energy<.001f,"Emission rapidly dissipates after release");
            log.Add("ALL ENERGY / SOURCE CHECKS PASSED");
        }catch(Exception e){log.Add("FAIL: "+e);Debug.LogException(e);}
        finally{if(main!=null)main.Silence();Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/aurora-energy.txt",log);}
    }
}
