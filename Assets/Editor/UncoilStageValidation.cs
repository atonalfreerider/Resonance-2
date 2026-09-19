using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public static class UncoilStageValidation
{
    public static async void Run()
    {
        var lines=new System.Collections.Generic.List<string>();
        void Check(bool ok,string name){if(!ok)throw new Exception(name);lines.Add("PASS: "+name);}
        try{
            var m=UnityEngine.Object.FindAnyObjectByType<Main>();
            m.GetComponent<VisualizationViews>().SetView(VisualizationViews.View.Torus);
            m.SetUncoiled(true);await Task.Delay(3200);
            Check(m.GetComponent<VisualizationViews>().DrumOpacity<.001f,"Drums hidden during unwind");
            Check(m.UncoilMoving&&m.OctaveSpread==0,"Primary curve unwinds before octave spread");
            Check(m.GetComponentsInChildren<LineRenderer>().Count(r=>r.name.StartsWith("Uncoiled octave")&&r.enabled)==1,"Only primary curve visible during unwind");
            ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/primary-unwind.png");
            await Task.Delay(3500);
            Check(m.GetComponent<VisualizationViews>().DrumOpacity>.95f,"Drums appear only after uncoil completes");
            Check(m.OctaveSpread>.999f&&!m.UncoilMoving,"Octaves finish spreading at end");
            Check(m.GetComponentsInChildren<LineRenderer>().Count(r=>r.name.StartsWith("Uncoiled octave")&&r.enabled)==8,"All eight final octave rings visible");
            m.SetUncoiled(false);await Task.Delay(2200);
            Check(m.GetComponent<VisualizationViews>().DrumOpacity<.001f,"Drums hidden during recoil");
            Check(m.OctaveSpread<.001f&&m.UncoilMoving,"Octaves collapse before primary curve recoils");
            await Task.Delay(4500);Check(m.UncoilAmount==0,"Recoil completes");Check(m.GetComponent<VisualizationViews>().DrumOpacity<.001f,"Drums remain hidden in 3D torus view");
        }catch(Exception e){lines.Add("FAIL: "+e);}
        Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/uncoil-stages.txt",lines);
    }
}
