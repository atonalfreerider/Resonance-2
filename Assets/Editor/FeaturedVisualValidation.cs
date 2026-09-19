using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class FeaturedVisualValidation
{
    public static async void Run()
    {
        var result=new System.Collections.Generic.List<string>();
        void Check(bool value,string message){if(!value)throw new Exception(message);result.Add("PASS: "+message);}
        try{
            var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();var featured=main.GetComponent<FeaturedInstrument>();
            midi.Recording.LoadPair("",Path.GetFullPath("PreparedSongs/TicketToRide-Restored/aligned.mid"));
            for(int i=0;i<100&&!midi.Recording.Ready;i++)await Task.Delay(100);
            Check(midi.Recording.Ready,"Recording loaded");
            var root=main.GetComponent<UIDocument>().rootVisualElement;var deck=root.Q<PatternWheelDeck>();
            Check(root.Q<DropdownField>("featured-instrument")!=null,"Side menu contains shared instrument/channel selector");
            var data=new PreparedPatternSong{LeadVocalTrack=-1,TrackNames=new[]{"Bass","Flute"},Notes=new[]{new MidiCycleAnalysis.Hit{Track=0,Channel=1,Pitch=40},new MidiCycleAnalysis.Hit{Track=1,Channel=2,Pitch=84}}};
            Check(FeaturedInstrument.DefaultLane(data)==(1,2),"Highest-register fallback");data.LeadVocalTrack=0;
            Check(FeaturedInstrument.DefaultLane(data)==(0,1),"Declared lead vocal takes precedence over register");
            midi.Seek(18);midi.Play();await Task.Delay(1500);
            Check(deck.FeaturedRadius>85&&Vector2.Distance(deck.FeaturedCenter,deck.MetaCenter)<.01f,"Composite's playing child remains full-size and centered");
            Check(deck.LeadVocalTrack==featured.Track,"Torus and pattern wheels share featured selection");
            var selected=main.GetComponentsInChildren<Note>().Where(n=>n.Featured&&n.CurrentAmp>0).ToArray();
            Check(selected.Length>0,"Featured sounding notes are marked white on torus");
            Check(main.GetComponent<ChordAurora>().VolumeCount==3,"Aurora uses bounded filled volumes with release ghosts");
            Check(featured.VoiceCount==2,"Lead Vocals has two independent prepared harmony strands");
            Check(featured.VisibleHeads>0,"Melody mice are visible during playback");
            Check(featured.TrailLength>0&&featured.TrailLength<=main.HistoryCircumference+.001f,"Voice trails obey circumference budget");
            Check(midi.Prepared.TrackNames[featured.Track]=="Lead Vocals","Ticket to Ride vocal alias is applied offline");
            Check(FeaturedInstrument.TrailFalloff(0)==1&&Mathf.Abs(FeaturedInstrument.TrailFalloff(1))<.0001f&&FeaturedInstrument.TrailFalloff(.5f)<.25f,"Logarithmic trail falloff has bright head and fading long tail");
            Check(FeaturedInstrument.Departure(1,2,2)<FeaturedInstrument.Departure(1,2,.2f),"Long jumps depart earlier");
            midi.Seek(19);Check(featured.HistoryCount==0,"Seeking resets history instead of connecting unrelated passages");
            midi.Play();await Task.Delay(900);Directory.CreateDirectory("Temp/ResonanceChecks");ScreenCapture.CaptureScreenshot("Temp/ResonanceChecks/featured-volume.png");
            await Task.Delay(150);midi.Pause();await Task.Delay(450);
            Check(main.GetComponent<ChordAurora>().Energy<.001f,"Released volume quickly dissipates");
            result.Add("ALL FEATURED VISUAL CHECKS PASSED");
        }catch(Exception e){result.Add("FAIL: "+e);Debug.LogException(e);}
        finally{Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/featured-visuals.txt",result);}
    }
}
