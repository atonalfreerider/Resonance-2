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
    private int[] timeSlicedKeys;
    private int lastTriggeredKey = -1;
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
            lastTriggeredKey = -1; // Reset to ensure the first key is triggered
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

            // Check for key change
            if (timeSlicedKeys != null && currentSlice < timeSlicedKeys.Length)
            {
                int currentKey = timeSlicedKeys[currentSlice];
                if (currentKey != lastTriggeredKey)
                {
                    main.ChangeKey(currentKey);
                    lastTriggeredKey = currentKey;
                }
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
        List<(double time, int keyIndex)> keyEvents = new();
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
            else if (evt is KeySignatureEvent keySig)
            {
                int rootNote = (keySig.MajorMinor == 0 ? 3 : 0); // C Major is 3, A Minor is 0
                int keyIndex = (rootNote + 7 * keySig.SharpsFlats + 120) % 12;
                keyEvents.Add((currentTime, keyIndex));
            }
        }

        if (!noteEvents.Any() && !keyEvents.Any()) return;

        double startTime = noteEvents.Count > 0 ? noteEvents[0].time : (keyEvents.Count > 0 ? keyEvents[0].time : 0);
        double endTime = noteEvents.Count > 0 ? noteEvents[^1].time : (keyEvents.Count > 0 ? keyEvents[^1].time : 0);
        totalTimeSlices = Mathf.Max(1, Mathf.CeilToInt((float)((endTime - startTime) / TIME_SLICE)));
        
        timeSlicedKeys = new int[totalTimeSlices];
        Dictionary<int, float> activeNotes = new();
        int eventIdx = 0;
        int keyEventIdx = 0;
        int currentKey = 0; // Default to A (0)

        for (int slice = 0; slice < totalTimeSlices; slice++)
        {
            double nextSliceTime = startTime + (slice + 1) * TIME_SLICE;
            
            // Update current key for this slice
            while (keyEventIdx < keyEvents.Count && keyEvents[keyEventIdx].time < nextSliceTime)
            {
                currentKey = keyEvents[keyEventIdx++].keyIndex;
            }
            timeSlicedKeys[slice] = currentKey;

            // Update notes for this slice
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
