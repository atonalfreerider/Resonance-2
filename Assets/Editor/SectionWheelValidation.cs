using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class SectionWheelValidation
{
    public static void ShowLibrary()
    {
        var root=UnityEngine.Object.FindAnyObjectByType<Main>().GetComponent<UIDocument>().rootVisualElement;
        var field=root.Query<DropdownField>().ToList().First(f=>f.label=="Prepared library");
        var scroll=root.Q<ScrollView>("controls");scroll.ScrollTo(field);ExplorerInputFocus.ClaimUI();field.Focus();
        var method=field.GetType().GetMethod("ShowMenu",BindingFlags.Instance|BindingFlags.NonPublic);
        if(method==null)throw new Exception("Cannot open the library popup for visual validation.");method.Invoke(field,null);
    }
    [MenuItem("Tools/Resonance/Check section wheels and library")]
    public static async void Run()
    {
        if(!EditorApplication.isPlaying)return;
        var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();
        if(!midi.Recording.Ready)return;
        var root=main.GetComponent<UIDocument>().rootVisualElement;var deck=root.Q<PatternWheelDeck>();
        var results=new List<string>();double position=midi.Position;bool playing=midi.IsPlaying;
        void Check(bool condition,string message){if(!condition)throw new Exception(message);results.Add("PASS: "+message);}
        try
        {
            midi.Pause();var data=midi.Prepared;
            Check(data.Key==0&&!data.Minor&&data.Frames.All(f=>f.Key==0),"Ticket to Ride keeps the reviewed A-major context throughout playback");
            Check(data.Version>=3&&data.Patterns.Length==data.Sections.Select(s=>s.Family).Distinct().Count(),"Every section family is compressed to one fundamental loop");
            Check(data.Sections.All(s=>s.Passes.Length>0&&Math.Abs(s.Passes[0].Start-s.Start)<1e-6&&Math.Abs(s.Passes[^1].End-s.End)<1e-6),"Every visit is described as passes of its family loop, end to end");
            Check(data.Groups.Any(g=>g.Name.Contains("Verse")&&g.Name.Contains("Chorus")&&g.Visits>=2),"Verse and chorus return together as a group");
            Check(data.Sections.Where(s=>s.Name=="Verse").Select(s=>s.Node).Distinct().Count()==1,"Verse visits reuse one family wheel");
            Check(data.Sections.All(s=>s.Lanes.Select(l=>l.Channel).Distinct().Count()>1),"Instrument channels are grouped inside their sections");
            Check(data.TemplateNoteCount<data.PatternNoteCount/2,"Rhythm slots are reused with saved pitch variations");
            double last=-1;
            foreach(var section in data.Sections)
            {
                midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(section.Start+1)));deck.Tick();await Task.Delay(65);
                Check(deck.ActiveFamilyNode==section.Node&&deck.ActiveGroup==section.Group,"Section powers its family planet: "+section.Name+" / bar "+(section.FirstBar+1));
                Check(Vector2.Distance(deck.MetaCenter,deck.FeaturedCenter)<.001f,"Active pattern is centered on the meta wheel");
                Check(deck.RackPixels>=last,"Rack motion stays continuous in song order");last=deck.RackPixels;
            }
            Check(main.currentKey==0&&!main.MinorMode,"Seeking does not restore the legacy C-major header");
            var kick=data.Notes.First(n=>n.Channel==10&&(n.Pitch==35||n.Pitch==36));var hat=data.Notes.First(n=>n.Channel==10&&(n.Pitch==42||n.Pitch==54));
            var snare=data.Notes.First(n=>n.Channel==10&&(n.Pitch==38||n.Pitch==40));
            Check(kick.RippleRadius>snare.RippleRadius&&snare.RippleRadius>hat.RippleRadius,"Kick, snare and hat waves have distinct size ordering");
            Check(hat.DecaySeconds<.2f&&hat.DecaySeconds<snare.DecaySeconds,"Hi-hat energy dissipates quickly");
            midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(kick.Beat))-.06);midi.Play();await Task.Delay(210);midi.Pause();
            // Capture geometry before the next frame clears paused waves.
            var waves=main.GetComponentsInChildren<LineRenderer>().Where(l=>l.name=="Percussion energy ripple"&&l.enabled).ToArray();
            Check(waves.Length>0,"Drum impacts create visible waves");
            foreach(var wave in waves){var origin=(wave.GetPosition(0)+wave.GetPosition((wave.positionCount-1)/2))*.5f;
                Check(Mathf.Abs(origin.x)<.0001f&&origin.z>.3f&&Mathf.Abs(origin.y-.015f)<.0001f,"Wave is centered on the twelve-o'clock collision point in the disc plane");}
            // Exercise the same pointer event route used by mouse and touch input.
            var rack=root.Q<VisualElement>("time-rack-seek");midi.Pause();midi.Seek(50);var pos=rack.worldBound.center;
            using(var e=PointerDownEvent.GetPooled(new Event{type=EventType.MouseDown,button=0,mousePosition=pos})){e.target=rack;rack.SendEvent(e);}
            using(var e=PointerMoveEvent.GetPooled(new Event{type=EventType.MouseDrag,button=0,mousePosition=pos+new Vector2(0,-60)})){e.target=rack;rack.SendEvent(e);}
            using(var e=PointerUpEvent.GetPooled(new Event{type=EventType.MouseUp,button=0,mousePosition=pos+new Vector2(0,-60)})){e.target=rack;rack.SendEvent(e);}
            Check(midi.Position>55&&!midi.IsPlaying,"Upward rack drag seeks forward and preserves pause");
            Check(!ExplorerInputFocus.ViewportOwnsKeyboard,"Rack gesture takes UI input ownership");
            var transport=root.Q<Button>("rack-play-pause");
            using(var e=NavigationSubmitEvent.GetPooled()){e.target=transport;transport.SendEvent(e);}await Task.Delay(130);
            Check(midi.IsPlaying,"Rack Play button starts recording");
            using(var e=NavigationSubmitEvent.GetPooled()){e.target=transport;transport.SendEvent(e);}await Task.Delay(40);
            Check(!midi.IsPlaying,"Rack Pause button stops recording");
            var points=new List<Vector3>();
            for(int a=0;a<12;a++)for(int b=0;b<12;b++){
                main.ShortSurfaceRoute(a/12f,b/12f,.5f,.75f,points);float sweep=0;
                for(int j=1;j<points.Count;j++){var x=main.transform.InverseTransformPoint(points[j-1]);var y=main.transform.InverseTransformPoint(points[j]);sweep+=Mathf.Abs(Mathf.DeltaAngle(Mathf.Atan2(x.z,x.x)*Mathf.Rad2Deg,Mathf.Atan2(y.z,y.x)*Mathf.Rad2Deg));}
                if(sweep>180.02f)throw new Exception("Chord wraps the long way: "+a+" / "+b);
                var get=typeof(Main).GetMethod("GetPointAt",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                var start=main.transform.TransformPoint((Vector3)get.Invoke(main,new object[]{a/12f,.5f}));var end=main.transform.TransformPoint((Vector3)get.Invoke(main,new object[]{b/12f,.75f}));
                if(Vector3.Distance(start,points[0])>.0001f||Vector3.Distance(end,points[40])>.0001f)throw new Exception("Chord endpoint mismatch");
            }
            results.Add("PASS: all 144 chord routes preserve endpoints and take at most half an orbit");
            ShowLibrary();await Task.Delay(150);
            var labels=root.panel.visualTree.Query<Label>().ToList().Where(l=>l.ClassListContains("unity-base-dropdown__label")).ToArray();
            Check(labels.Length>=12,"Prepared-song popup contains the restored library");
            Check(root.panel.visualTree.Q<VisualElement>(className:"unity-base-dropdown__container-inner").resolvedStyle.backgroundColor.maxColorComponent<.3f,"Song picker has a dark background");
            Check(labels.All(l=>l.resolvedStyle.color.maxColorComponent>.6f),"Popup labels are readable light text");
            Check(labels.Zip(labels.Skip(1),(a,b)=>Mathf.Abs(a.worldBound.center.y-b.worldBound.center.y)>15).All(v=>v),"Popup entries have separate, non-overlapping rows");
            results.Add("ALL SECTION WHEEL / POPUP CHECKS PASSED");
        }
        catch(Exception e){results.Add("FAIL: "+e);Debug.LogException(e);}
        finally{midi.Pause();midi.Seek(position);if(playing)midi.Play();Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/section-wheels.txt",results);}
    }
}
