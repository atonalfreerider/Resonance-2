using System.Collections.Generic;
using System.Linq;
using NAudio.Midi;
using UnityEngine;

public class Main : MonoBehaviour
{
    public string midiPath;

    const float noteScale = .01f;

    public void Awake()
    {
        // Load the MIDI file
        MidiFile midi = new(midiPath, false);

        for (int trackIndex = 0; trackIndex < midi.Events.Count(); trackIndex++)
        {
            IList<MidiEvent> trackEvents = midi.Events[trackIndex];

            int totalNoteCount = 0;

            foreach (MidiEvent trackEvent in trackEvents)
            {
                if (totalNoteCount > 50) continue;

                if (trackEvent is NoteOnEvent noteOnEvent &&
                    noteOnEvent.Velocity > 0) // Only consider NoteOn with velocity > 0 (actual note starts)
                {
                    int noteKey = noteOnEvent.NoteNumber;
                    long onAbsoluteTime = noteOnEvent.AbsoluteTime;
                    long offAbsoluteTime = noteOnEvent.OffEvent.AbsoluteTime;
                    long duration = offAbsoluteTime - onAbsoluteTime;

                    GameObject noteGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    noteGo.transform.SetParent(transform, false);

                    noteGo.transform.localScale = new Vector3(.2f, .001f, duration * noteScale);
                    noteGo.transform.Translate(Vector3.right * noteKey * .5f);
                    noteGo.transform.Translate(Vector3.forward * (onAbsoluteTime + duration * .5f) * noteScale);
                    totalNoteCount++;
                }
            }
        }
    }
}