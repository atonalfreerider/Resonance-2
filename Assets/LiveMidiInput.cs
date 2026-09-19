using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NAudio.Midi;
using UnityEngine;

public class LiveMidiInput : MonoBehaviour
{
    readonly ConcurrentQueue<MidiEvent> pending = new();
    MidiIn device;
    MidiVoiceState voices = new();
    Main main;
    public bool Connected => device != null;
    public string Status { get; private set; } = "Disconnected";
    public static List<string> Devices()
    {
        var result = new List<string> { "Disconnected" };
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        try { for(int i=0;i<MidiIn.NumberOfDevices;i++)result.Add(MidiIn.DeviceInfo(i).ProductName); }
        catch { result.Add("Device enumeration unavailable"); }
#endif
        return result;
    }
    void Awake(){main=GetComponent<Main>();}
    public void Connect(int index)
    {
        Disconnect(); if(index<0)return;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        try
        {
            GetComponent<MidiPlayer>()?.Stop(); GetComponent<HarmonyExplorer>()?.StopLesson();
            device=new MidiIn(index); device.MessageReceived+=Receive; device.Start();
            Status="Connected: "+MidiIn.DeviceInfo(index).ProductName;
        }
        catch(Exception e){Disconnect();Status="MIDI input: "+e.Message;}
#else
        Status="Live MIDI input currently supports Windows.";
#endif
    }
    void Receive(object sender,MidiInMessageEventArgs e){pending.Enqueue(e.MidiEvent);}
    public void Disconnect()
    {
        if(device!=null){device.MessageReceived-=Receive;device.Stop();device.Dispose();device=null;}
        while(pending.TryDequeue(out _)){} voices=new MidiVoiceState();
        if(main!=null)main.Silence();Status="Disconnected";
    }
    void Update()
    {
        bool changed=false;
        while(pending.TryDequeue(out var evt))
        {
            if(evt==null || evt.Channel==10)continue;
            if(evt is NoteEvent note && (evt.CommandCode==MidiCommandCode.NoteOn || evt.CommandCode==MidiCommandCode.NoteOff))
            {
                int index=note.NoteNumber-21;if(index<0 || index>=96)continue;
                if(evt is NoteOnEvent on && on.Velocity>0)voices.NoteOn(evt.Channel,index,on.Velocity/127f);else voices.NoteOff(evt.Channel,index);
                changed=true;
            }
            else if(evt is ControlChangeEvent cc){voices.Control(evt.Channel,(int)cc.Controller,cc.ControllerValue);changed=true;}
        }
        if(changed)main.PlayKeys(voices.Snapshot());
    }
    void OnApplicationFocus(bool focus){if(!focus)Disconnect();}
    void OnDisable(){Disconnect();}
}
