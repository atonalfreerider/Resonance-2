using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

public static class VisualizationViewValidation
{
    public static async void Run()
    {
        var log=new System.Collections.Generic.List<string>();
        void Check(bool value,string message){if(!value)throw new Exception(message);log.Add("PASS: "+message);}
        VisualizationViews view=null;
        try{
            var main=UnityEngine.Object.FindAnyObjectByType<Main>();view=main.GetComponent<VisualizationViews>();var midi=main.GetComponent<MidiPlayer>();
            midi.Recording.LoadPair("",Path.GetFullPath("PreparedSongs/TicketToRide-Restored/aligned.mid"));
            for(int i=0;i<100&&!midi.Recording.Ready;i++)await Task.Delay(100);
            Check(midi.Recording.Ready,"Recording ready");midi.Seek(18);midi.Play();
            var root=main.GetComponent<UIDocument>().rootVisualElement;
            Check(root.Q<Button>("tuck-side-menu")!=null&&root.Q<DropdownField>("view-selector")!=null,"Persistent menu and overview controls exist");
            view.SetPanelHidden(true);view.SetView(VisualizationViews.View.Torus);await Task.Delay(1400);
            Check(Camera.main.rect.x<.001f,"Hidden menu returns its space to the camera");
            var orbit=Camera.main.GetComponent<CameraControl>();
            Check(orbit.enabled&&orbit.Centered,"Torus focus hands control to the centered orbit camera");
            var before=Camera.main.transform.position;var state=orbit.OrbitState;state.z+=.2f;orbit.RestoreOrbit(state);ExplorerInputFocus.ClaimViewport();await Task.Delay(180);
            Check(Vector3.Distance(before,Camera.main.transform.position)>.1f,"Torus orbit movement survives view-controller updates");
            Check(view.TorusOpacity>.99f&&view.DrumOpacity<.001f,"Torus focus fades drum deck");
            ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/focus-torus.png");await Task.Delay(150);
            view.SetView(VisualizationViews.View.Drums);await Task.Delay(1400);
            Check(Vector3.Dot(Camera.main.transform.forward,Vector3.down)>.999f,"Drum focus is overhead");
            Check(view.TorusOpacity<.001f&&view.DrumOpacity>.99f,"Drum focus excludes torus");
            var drumRoot=main.GetComponent<DrumPatternDeck>().WheelTransform;
            Check(main.GetComponentsInChildren<Renderer>().Where(r=>!r.transform.IsChildOf(drumRoot)).All(r=>r.forceRenderingOff),"Torus renderers actually hidden in drum focus");
            ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/focus-drums.png");await Task.Delay(150);
            view.SetView(VisualizationViews.View.Timeline);await Task.Delay(1400);
            Check(view.TorusOpacity<.001f&&view.DrumOpacity<.001f,"Timeline focus fades both world visuals");
            Check(root.Q("pattern-wheel-overlay").resolvedStyle.width>600,"Timeline expands into available space");
            ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/focus-timeline.png");await Task.Delay(150);
            view.SetView(VisualizationViews.View.Overview);view.SetPanelHidden(false);await Task.Delay(1400);
            Check(view.TorusOpacity>.99f&&view.DrumOpacity>.99f&&Camera.main.rect.x>.1f,"Overview restores both visuals and menu space");
            Check(Camera.main.GetComponent<CameraControl>().enabled,"Overview restores orbit controls");
            midi.Pause();log.Add("ALL VIEW CHECKS PASSED");
        }catch(Exception e){log.Add("FAIL: "+e);Debug.LogException(e);}
        finally{if(view!=null){view.SetView(VisualizationViews.View.Overview);view.SetPanelHidden(false);}Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/views.txt",log);}
    }
}
