using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Midi;
using UnityEngine;

[RequireComponent(typeof(Main))]
public class MidiPlayer : MonoBehaviour
{
    public string midiPath;
    public float playbackSpeed = 1f;

    private Main main;
    private double playbackStartTime;
    private bool isPlaying;
    private readonly Dictionary<int, List<Tuple<int, float>>> timeSlicedNotes = new();
    private int totalTimeSlices;
    private const float TIME_SLICE = 0.01f;

    public bool IsPlaying => isPlaying;

    void Awake()
    {
        main = GetComponent<Main>();
    }

    void Start()
    {
        LoadMidi();
    }

    void Update()
    {
        if (UnityEngine.InputSystem.Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            playbackStartTime = Time.timeAsDouble;
            isPlaying = true;
        }

        if (isPlaying)
        {
            double currentTime = (Time.timeAsDouble - playbackStartTime) * playbackSpeed;
            int currentSlice = (int)(currentTime / TIME_SLICE);

            if (currentSlice >= totalTimeSlices)
            {
                isPlaying = false;
                main.PlayKeys(new List<Tuple<int, float>>());
                return;
            }

            if (timeSlicedNotes.TryGetValue(currentSlice, out List<Tuple<int, float>> notesToPlay))
            {
                main.PlayKeys(notesToPlay);
            }
            else
            {
                main.PlayKeys(new List<Tuple<int, float>>());
            }
        }
    }

    void LoadMidi()
    {
        if (string.IsNullOrEmpty(midiPath)) return;
        MidiFile midiFile = new MidiFile(midiPath, false);
        timeSlicedNotes.Clear();

        int ticksPerQuarter = midiFile.DeltaTicksPerQuarterNote;
        double currentTempo = 500000.0;
        List<(long tick, MidiEvent evt)> allEvents = new();
        
        foreach (IList<MidiEvent> track in midiFile.Events)
        {
            long absoluteTick = 0;
            foreach (MidiEvent evt in track)
            {
                absoluteTick += evt.DeltaTime;
                allEvents.Add((absoluteTick, evt));
            }
        }

        allEvents.Sort((a, b) => a.tick.CompareTo(b.tick));

        List<(double time, int note, float velocity)> noteEvents = new();
        double currentTime = 0;
        long lastTick = 0;

        foreach ((long tick, MidiEvent evt) in allEvents)
        {
            long deltaTicks = tick - lastTick;
            currentTime += (deltaTicks * currentTempo) / (ticksPerQuarter * 1_000_000.0);
            lastTick = tick;

            if (evt is TempoEvent tempoEvent) currentTempo = tempoEvent.MicrosecondsPerQuarterNote;
            else if (evt is NoteEvent noteEvent)
            {
                int noteIndex = noteEvent.NoteNumber - 21; // MIDI 21 is A0
                if (noteIndex >= 0 && noteIndex < Main.Tones * Main.Octaves)
                {
                    float velocity = noteEvent.CommandCode == MidiCommandCode.NoteOn ? ((NoteOnEvent)noteEvent).Velocity / 127f : 0;
                    noteEvents.Add((currentTime, noteIndex, velocity));
                }
            }
        }

        if (!noteEvents.Any()) return;

        double startTime = noteEvents[0].time;
        totalTimeSlices = Mathf.Max(1, Mathf.CeilToInt((float)((noteEvents[^1].time - startTime) / TIME_SLICE)));
        Dictionary<int, float> activeNotes = new();
        int eventIdx = 0;

        for (int slice = 0; slice < totalTimeSlices; slice++)
        {
            double nextSliceTime = startTime + (slice + 1) * TIME_SLICE;
            while (eventIdx < noteEvents.Count && noteEvents[eventIdx].time < nextSliceTime)
            {
                var e = noteEvents[eventIdx++];
                if (e.velocity > 0) activeNotes[e.note] = e.velocity;
                else activeNotes.Remove(e.note);
            }
            timeSlicedNotes[slice] = activeNotes.Select(kvp => new Tuple<int, float>(kvp.Key, kvp.Value)).ToList();
        }
    }
}
