using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Midi;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Main))]
public class MidiPlayer : MonoBehaviour
{
    public string midiPath;
    public float playbackSpeed = 1f;
    public bool Loop, FollowKey = true, SkipPercussion = true;
    public int ChannelFilter; // 0 = all, otherwise MIDI channel 1-16
    public int TrackFilter = -1;
    public string Status { get; private set; } = "Load a MIDI file";
    public int TrackCount { get; private set; }
    public bool IsPlaying { get; private set; }
    public double ScoreDuration { get; private set; }
    public SongAudio Recording => GetComponent<SongAudio>();
    public double Duration => Recording != null && Recording.Ready ? Recording.Source.clip.length : ScoreDuration;
    public double ScorePosition => Recording != null && Recording.Ready ? Recording.Alignment.ToMidi(Position) : Position;
    public double AudioTime(double scoreTime) => Recording != null && Recording.Ready ? Recording.Alignment.ToAudio(scoreTime) : scoreTime;
    public MidiCycleAnalysis Cycles { get; private set; }
    public SongFormAnalysis SongForm { get; private set; }
    public int SectionBars=8;
    public string SectionBoundaries="";
    public bool MatchPatternPitch;
    double memoryPosition;
    int memoryIndex;
    public double Position => IsPlaying ? (Recording != null && Recording.Ready ? (AudioSettings.dspTime < originDsp ? originPosition : (double)Recording.Source.timeSamples / Recording.Source.clip.frequency) : Math.Min(Duration, originPosition + Math.Max(0,AudioSettings.dspTime-originDsp)*playbackSpeed)) : originPosition;
    sealed class Frame
    {
        public double Time;
        public List<Tuple<int,float>> Notes;
        public int? Key;
        public bool Minor;
    }
    readonly List<Frame> frames = new();
    Main main;
    double originPosition, originDsp;
    int visualIndex, audioIndex;
    int fallbackKey; bool fallbackMinor;
    public bool Loaded => frames.Count > 0;
    void Awake() { main = GetComponent<Main>(); }
    void Start() { if (!string.IsNullOrWhiteSpace(midiPath)) Load(midiPath); }
    public bool Load(string path)
    {
        Stop(); Recording?.Unload(); frames.Clear(); ScoreDuration = 0; Cycles=null; SongForm=null; SectionBoundaries=""; main.ClearVisualMemory();
        try
        {
            var file = new MidiFile(path.Trim().Trim('"'), false);
            if (file.FileFormat == 2 || file.DeltaTicksPerQuarterNote <= 0) throw new ArgumentException("Use a format 0/1 MIDI with PPQ timing.");
            Cycles=MidiCycleAnalysis.Analyze(file,MatchPatternPitch);
            SongForm=SongFormAnalysis.Build(Cycles,SectionBars);
            TrackCount = file.Tracks;
            var events = new List<(long tick, int order, int track, MidiEvent evt)>();
            int order = 0;
            for (int track=0;track<file.Tracks;track++) foreach (var evt in file.Events[track]) events.Add((evt.AbsoluteTime, order++, track, evt));
            double time=0, tempo=500000; long tick=0;
            int? key = null; bool minor = false;
            var voices = new MidiVoiceState();
            foreach (var entry in events.OrderBy(e=>e.tick).ThenBy(e=>e.order))
            {
                time += (entry.tick-tick)*tempo/(file.DeltaTicksPerQuarterNote*1000000.0); tick=entry.tick;
                var evt=entry.evt;
                if (evt is TempoEvent te) { tempo=te.MicrosecondsPerQuarterNote; continue; }
                bool changed=false;
                if (evt is KeySignatureEvent ks) { minor=ks.MajorMinor!=0; key=HarmonyModel.Mod((minor?0:3)+7*ks.SharpsFlats); changed=true; }
                else if ((TrackFilter<0 || entry.track==TrackFilter) && (ChannelFilter==0 || evt.Channel==ChannelFilter) && !(SkipPercussion && evt.Channel==10))
                {
                    if (evt is NoteEvent ne && (evt.CommandCode==MidiCommandCode.NoteOn || evt.CommandCode==MidiCommandCode.NoteOff))
                    {
                        int note=ne.NoteNumber-21;
                        if (note>=0 && note<96)
                        {
                            if (evt is NoteOnEvent on && on.Velocity>0) voices.NoteOn(evt.Channel,note,on.Velocity/127f);
                            else voices.NoteOff(evt.Channel,note);
                            changed=true;
                        }
                    }
                    else if (evt is ControlChangeEvent cc) { voices.Control(evt.Channel,(int)cc.Controller,cc.ControllerValue); changed=true; }
                }
                if (changed) frames.Add(new Frame { Time=time, Notes=voices.Snapshot(), Key=key, Minor=minor });
            }
            ScoreDuration=time+.08;
            frames.Add(new Frame {Time=Duration,Notes=new List<Tuple<int,float>>(),Key=key,Minor=minor});
            midiPath=path; originPosition=0;
            fallbackKey=main.currentKey; fallbackMinor=main.MinorMode;
            Status=$"{System.IO.Path.GetFileName(path)} · {TrackCount} tracks";
            return true;
        }
        catch (Exception e) { frames.Clear(); Cycles=null; SongForm=null; Status="MIDI import: "+e.Message; return false; }
    }
    public void RebuildSongForm(int bars,string boundaries)
    {
        if(Cycles==null)return;
        var form=SongFormAnalysis.Build(Cycles,bars,boundaries);
        SongForm=form;SectionBars=bars;SectionBoundaries=boundaries;
    }
    public void ReanalyzePatterns(bool matchPitch)
    {
        MatchPatternPitch=matchPitch;
        if(!Loaded)return;
        try{Cycles=MidiCycleAnalysis.Analyze(new MidiFile(midiPath.Trim().Trim('"'),false),matchPitch);}
        catch(Exception e){Status="Pattern analysis: "+e.Message;}
    }
    public void Play()
    {
        if (!Loaded || (Recording!=null && Recording.Busy)) return;
        GetComponent<LiveMidiInput>()?.Disconnect();
        GetComponent<HarmonyExplorer>()?.StopLesson();
        if (Position>=Duration) originPosition=0;
        if (originPosition==0 && main.KeySource.StartsWith("Manual")) { fallbackKey=main.currentKey; fallbackMinor=main.MinorMode; }
        IsPlaying=true; Rebase(originPosition);
    }
    void Rebase(double position)
    {
        originPosition=Math.Clamp(position,0,Duration); originDsp=AudioSettings.dspTime+.05;
        main.Synth.ResetVoices(); visualIndex=0;
        bool recorded=Recording!=null && Recording.Ready;
        double scorePosition=recorded?Recording.Alignment.ToMidi(originPosition):originPosition;
        if(recorded){Recording.Source.Stop();Recording.Source.timeSamples=Math.Clamp((int)(originPosition*Recording.Source.clip.frequency),0,Recording.Source.clip.samples-1);Recording.Source.pitch=playbackSpeed;if(IsPlaying)Recording.Source.PlayScheduled(originDsp);}
        while (visualIndex<frames.Count && frames[visualIndex].Time<=scorePosition) visualIndex++;
        audioIndex=visualIndex;
        memoryPosition=scorePosition; memoryIndex=visualIndex;
        var notes=visualIndex>0?frames[visualIndex-1].Notes:new List<Tuple<int,float>>();
        if(!recorded)main.Synth.Schedule(originDsp,notes); main.SetNotes(notes,false);
        ApplyKey(visualIndex>0?frames[visualIndex-1]:null);
    }
    void ApplyKey(Frame frame)
    {
        if (!FollowKey) return;
        int key=frame?.Key??fallbackKey; bool minor=frame?.Key!=null?frame.Minor:fallbackMinor;
        main.KeySource=frame?.Key!=null?"MIDI signature":"Manual (no MIDI signature)";
        if (main.currentKey!=key || main.MinorMode!=minor) { main.MinorMode=minor; main.ChangeKey(key); }
    }
    public void Pause()
    {
        double position=Position; IsPlaying=false; originPosition=position; Recording?.Source.Pause(); main.Silence();
    }
    public void Stop() { Recording?.Source.Stop(); IsPlaying=false; originPosition=0; visualIndex=audioIndex=0; if (main!=null) main.Silence(); }
    public void Seek(double seconds)
    {
        if (!Loaded || main.Synth==null) return;
        bool playing=IsPlaying; main.Silence();main.ClearVisualMemory(); Rebase(seconds); if (!playing) main.Synth.ResetVoices();
    }
    public void SetSpeed(float speed)
    {
        double position=Position; playbackSpeed=Recording!=null&&Recording.Ready?1:Mathf.Clamp(speed,.25f,2f);
        if (IsPlaying) Rebase(position);
    }
    void Update()
    {
        if (Keyboard.current!=null && Keyboard.current.spaceKey.wasPressedThisFrame && ExplorerInputFocus.ViewportOwnsKeyboard)
        { if (IsPlaying) Pause(); else Play(); }
        if (!IsPlaying) return;
        double position=ScorePosition;
        // Integrate every MIDI snapshot, including notes shorter than a rendered frame.
        while(memoryIndex<frames.Count && frames[memoryIndex].Time<=position)
        {
            if(memoryIndex>0)main.TonalField?.Integrate(frames[memoryIndex-1].Notes,Math.Max(0,AudioTime(frames[memoryIndex].Time)-AudioTime(memoryPosition))/playbackSpeed);
            memoryPosition=frames[memoryIndex].Time;memoryIndex++;
        }
        if(memoryIndex>0)main.TonalField?.Integrate(frames[memoryIndex-1].Notes,Math.Max(0,AudioTime(position)-AudioTime(memoryPosition))/playbackSpeed);
        memoryPosition=position;
        while ((Recording==null || !Recording.Ready) && audioIndex<frames.Count && frames[audioIndex].Time <= position + .5*playbackSpeed)
        {
            var frame=frames[audioIndex++];
            main.Synth.Schedule(originDsp+(frame.Time-originPosition)/playbackSpeed,frame.Notes);
        }
        int previous=visualIndex;
        while (visualIndex<frames.Count && frames[visualIndex].Time<=position) visualIndex++;
        if (visualIndex!=previous && visualIndex>0) { var frame=frames[visualIndex-1]; main.SetNotes(frame.Notes,false); ApplyKey(frame); }
        if (Position>=Duration-.025 || (Recording!=null && Recording.Ready && AudioSettings.dspTime>originDsp+.1 && !Recording.Source.isPlaying)) { if (Loop) { originPosition=0; Rebase(0); } else Stop(); }
    }
    void OnApplicationFocus(bool focus) { if (!focus && IsPlaying) Pause(); }
    void OnDisable() { if (main!=null) Stop(); }
}
