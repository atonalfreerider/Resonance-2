using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public static class SongPlaybackValidation
{
    [MenuItem("Tools/Resonance/Check paired recording playback")]
    public static async void Run()
    {
        var midi=UnityEngine.Object.FindAnyObjectByType<MidiPlayer>();
        if(!EditorApplication.isPlaying||midi==null||midi.Recording==null||!midi.Recording.Ready)return;
        var audio=midi.Recording;var main=midi.GetComponent<Main>();
        var results=new List<string>();double previous=midi.Position;float volume=AudioListener.volume;
        void Check(bool pass,string name){if(!pass)throw new Exception(name);results.Add("PASS: "+name);}
        try
        {
            AudioListener.volume=0;midi.Pause();
            Check(audio.Source.gameObject!=main.Synth.gameObject,"Recording source is isolated from synth audio filter");
            Check(Math.Abs(midi.Duration-audio.Source.clip.samples/(double)audio.Source.clip.frequency)<.001,"Song duration equals decoded sample count");
            midi.Seek(midi.Duration*.5);
            Check(Math.Abs(midi.Position/midi.Duration-.5)<.00001,"Full-song orbit uses recording duration");
            Check(Math.Abs(audio.Alignment.ToAudio(midi.ScorePosition)-midi.Position)<.00001,"Orrery score position maps back to recording clock");
            Check(!audio.Source.isPlaying,"Paused seek does not start recording");
            midi.Play();await Task.Delay(450);
            Check(audio.Source.isPlaying&&midi.IsPlaying,"Play starts recording and score together");
            Check(main.Synth.GetComponent<AudioSource>().mute,"MIDI synthesis muted during recording playback");
            Check(Math.Abs(midi.Position-audio.Source.timeSamples/(double)audio.Source.clip.frequency)<.002,"MIDI transport reads recording sample clock");
            midi.Pause();double paused=midi.Position;await Task.Delay(160);
            Check(Math.Abs(midi.Position-paused)<.0001&&!audio.Source.isPlaying,"Pause freezes recording and score clock");
            midi.SetSpeed(.5f);Check(midi.playbackSpeed==1,"Recording retains original pitch and speed");
            midi.Seek(midi.Duration-.15);midi.Play();await Task.Delay(400);
            Check(!midi.IsPlaying&&!audio.Source.isPlaying,"End of recording stops both transports");
            midi.Loop=true;midi.Seek(midi.Duration-.12);midi.Play();await Task.Delay(400);
            Check(midi.IsPlaying&&midi.Position<1,"Loop wraps recording and score together");midi.Loop=false;
            results.Add("PASS: paired recording checks complete");
        }
        catch(Exception e){results.Add("FAIL: "+e.Message);Debug.LogException(e);}
        finally{midi.Stop();midi.Seek(previous);AudioListener.volume=volume;Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllLines("Temp/ResonanceChecks/paired-audio.txt",results);}
    }
}
