using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

public static class KeyAnimationValidation
{
    public static async void Run()
    {
        var log=new List<string>();
        void Check(bool ok,string description){if(!ok)throw new Exception(description);log.Add("PASS: "+description);}
        try{
            var m=UnityEngine.Object.FindAnyObjectByType<Main>();m.GetComponent<MidiPlayer>().Pause();
            m.GetComponent<VisualizationViews>().SetView(VisualizationViews.View.Torus);
            foreach(bool flat in new[]{false,true}){
                m.SetUncoiled(flat);await Task.Delay(6700);m.ChangeKey(0,0);
                foreach(int key in new[]{7,5,6,0}){
                    var before=m.NoteEmissionPoint(36);float radius=m.transform.InverseTransformPoint(before).magnitude;
                    m.ChangeKey(key,1.2f);
                    Check(Vector3.Distance(before,m.NoteEmissionPoint(36))<.025f,"No initial key-change jump: "+flat+" / "+key);
                    for(int i=0;i<8;i++){await Task.Delay(110);var point=m.transform.InverseTransformPoint(m.NoteEmissionPoint(36));
                        Check(float.IsFinite(point.x)&&float.IsFinite(point.y)&&float.IsFinite(point.z),"Finite animation geometry");
                        if(flat)Check(Mathf.Abs(point.z)<.001f&&Mathf.Abs(point.magnitude-radius)<.005f,"Flat key change preserves octave circle");
                    }
                    await Task.Delay(500);Check(!m.KeyChanging&&m.currentKey==key,"Key transition finishes: "+flat+" / "+key);
                    if(!flat)Check(Vector3.Distance(m.NoteEmissionPoint(36),m.CoiledNoteEmissionPoint(36))<.001f,"Coiled geometry preserved");
                }
            }
            ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/key-flat-field.png");
            log.Add("ALL KEY ANIMATION CHECKS PASSED");
        }catch(Exception e){log.Add("FAIL: "+e);}
        Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/key-animation.txt",log);
    }
}
