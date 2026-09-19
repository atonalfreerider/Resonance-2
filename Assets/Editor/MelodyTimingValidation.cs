using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

public static class MelodyTimingValidation
{
    public static async void Run()
    {
        var log=new System.Collections.Generic.List<string>();
        void Check(bool condition,string message){if(!condition)throw new Exception(message);log.Add("PASS: "+message);}
        try{
            var main=UnityEngine.Object.FindAnyObjectByType<Main>();var midi=main.GetComponent<MidiPlayer>();var feature=main.GetComponent<FeaturedInstrument>();
            midi.Recording.LoadPair("",Path.GetFullPath("PreparedSongs/TicketToRide-Restored/aligned.mid"));
            for(int i=0;i<100&&!midi.Recording.Ready;i++)await Task.Delay(100);
            Check(midi.Recording.Ready,"Recording loaded");midi.Pause();feature.EnsureLoaded(midi.Prepared);feature.Select(3,1);
            var data=midi.Prepared;int total=0;
            foreach(var strand in data.MelodyStrands)foreach(var n in strand.Notes){
                if(!data.Frames.Any(f=>Math.Abs(f.Time-n.Start)<1e-8&&f.Attacks.Any(a=>a.Track==strand.Track&&a.Channel==strand.Channel&&a.Pitch==n.Pitch)))throw new Exception("Prepared onset mismatch");total++;
            }
            Check(total>3000,"Every prepared mouse onset matches an exact MIDI attack: "+total);
            var flags=BindingFlags.NonPublic|BindingFlags.Instance;var at=typeof(FeaturedInstrument).GetMethod("At",flags);var point=typeof(FeaturedInstrument).GetMethod("Point",flags);
            var voices=(System.Collections.IEnumerable)typeof(FeaturedInstrument).GetField("voices",flags).GetValue(feature);
            int arrivals=0;
            foreach(var voice in voices){var notes=((PreparedPatternSong.MelodyStrand)voice.GetType().GetField("Data").GetValue(voice)).Notes;
                foreach(var note in notes){
                    var sample=at.Invoke(feature,new object[]{voice,note.Start,0f});
                    var position=(Vector3)point.Invoke(feature,new[]{sample});var path=new System.Collections.Generic.List<Vector3>();main.NoteHistoryPath(note.Pitch-21,note.Pitch-21,path);
                    if(Vector3.Distance(position,path[0])>.00001f)throw new Exception("Mouse misses onset endpoint");arrivals++;
                }
            }
            Check(arrivals==515,"Both vocal mice occupy their exact note positions at all 515 onsets");
            Check(FeaturedInstrument.Departure(1,1.1,5)>1.02,"Fast notes retain an arrival dwell");
            Check(MidiPlayer.ScheduledPosition(12,100,99,1,200)==12,"Scheduled start does not advance early");
            Check(Math.Abs(MidiPlayer.ScheduledPosition(12,100,100.125,1,200)-12.125)<1e-10,"Recording clock advances exactly from scheduled DSP start");
            Check(Math.Abs(MidiPlayer.ScheduledPosition(12,100,100.125,2,200)-12.25)<1e-10,"MIDI speed uses the same clock math");
            midi.Seek(18);midi.Play();await Task.Delay(500);
            Check(Math.Abs(midi.VisualScorePosition-midi.ScorePosition)<.025,"Shared presentation timestamp stays within one rendered frame");
            midi.Pause();log.Add("ALL TIMING CHECKS PASSED");
        }catch(Exception e){log.Add("FAIL: "+e);Debug.LogException(e);}
        finally{Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/melody-timing.txt",log);}
    }
}
