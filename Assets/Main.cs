using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Midi;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using Util;

/// <summary>
/// Umbilic Torus Circle of Fifths credit:
/// https://jimishol.github.io/post/tonality/
/// </summary>
public class Main : MonoBehaviour
{
    public Material BloomMat;

    public string midiPath;

    const float NoteScale = .01f;

    const int Tones = 12;
    const int Octaves = 8;
    const int Sides = 3;
    const float EdgeLength = 1.2f;
    const float Rad = 1.5f;
    const int Sections = Tones / Sides;

    readonly List<Note> notes = new();
    readonly List<TextBox> noteTextLabels = new();

    readonly List<LineRenderer> fifthsLineRenderer = new();
    LineRenderer chromaticLineRenderer;

    readonly Dictionary<ulong, Chord> chordLineRenderers = new();

    readonly string[] noteLabels = { "A", "A#", "B", "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#" };

    int currentKey = 0; // A

    static Color darkGrey => new(0.2f, 0.2f, 0.2f);

    readonly Color[] descendingFifthColors =
    {
        // order from key - 1, descending through fifths
        // Major
        Color.red,
        // Transient
        Color.yellow,
        darkGrey,
        darkGrey,
        darkGrey,
        Color.yellow,
        Color.Lerp(Color.yellow, Color.green, 0.5f),
        // Minor
        Color.Lerp(Color.green, Color.blue, 0.5f),
        Color.Lerp(Color.blue, Color.red, 0.5f),
        Color.Lerp(Color.red, Color.yellow, 0.5f),
        // Major
        Color.green,
        Color.blue
    };

    readonly Dictionary<int, int> fifthToColor = new();
    readonly Dictionary<int, int> scaleToFifths = new();

    CameraControl cameraControl;

    void Awake()
    {
        // create map for fifths to color lookup
        int count = 7;
        for (int i = Tones - 1; i >= 0; i--)
        {
            fifthToColor.Add(count, i);
            count = Roll(count, -5);
        }

        for (int j = 0; j < Octaves; j++)
        {
            // set the notes in 5th intervals evenly from 0 to 1 on the umbilical
            int next = Tones * j;
            for (int i = 0; i < Tones; i++)
            {
                int noteIndex = j * Tones + i;
                float scaleFactor = Mathf.Lerp(.03f, .005f, (float)noteIndex / (Tones * Octaves));
                Note newNote = Note.Create($"{noteLabels[i]} {j}", ToHertz(noteIndex), scaleFactor);
                newNote.transform.SetParent(transform, false);
                notes.Add(newNote);
                scaleToFifths.Add(noteIndex, next);

                next += 5; // fifths
                if (next >= Tones * (j + 1))
                {
                    next -= Tones;
                }

                if (j != 0) continue;

                TextBox labelText = TextBox.Create(noteLabels[i], TextAlignmentOptions.Center);
                labelText.transform.SetParent(transform, false);
                labelText.Size = .5f;
                noteTextLabels.Add(labelText);
            }
        }

        for (int i = 0; i < Tones; i++)
        {
            Material fifthsMat = new(Shader.Find("Particles/Standard Unlit"));
            GameObject fifthsGo = new("fifths");
            fifthsGo.transform.SetParent(transform, false);
            fifthsLineRenderer.Add(NewLineRenderer(fifthsGo, fifthsMat, .01f, false));
        }

        Material chromaticMat = new(Shader.Find("Unlit/Color"))
        {
            color = new Color(0.2f, 0.2f, 0.2f)
        };
        GameObject chromGo = new("chromatic");
        chromGo.transform.SetParent(transform, false);
        chromaticLineRenderer = NewLineRenderer(chromGo, chromaticMat, .007f, true);

        SetUmbilic();
    }

    void Start()
    {
        UpdateText();
        Camera.main.GetComponent<CameraControl>().MovementUpdater += UpdateText;
        Camera.main.transform.LookAt(Vector3.zero);
    }

    void SetChromatic()
    {
        float stack = 0;
        foreach (Note pointGo in notes)
        {
            pointGo.transform.localPosition = new Vector3(0, stack, 0);
            stack += .03f;
        }
    }

    void SetUmbilic()
    {
        List<Vector3> chromaticList = new();
        const float resolution = 0.001f;
        const int ratio = Sides * 2 - 1;
        for (float t = 0; t < 1; t += resolution)
        {
            chromaticList.Add(UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, t, Mathf.PI, ratio)); // 5 roll
        }

        for (int i = 0; i < Tones; i++)
        {
            LineRenderer fifthsSegment = fifthsLineRenderer[i];
            List<Vector3> subSection = new();
            for (float t = (float)i / Tones; t < (float)(i + 1) / Tones; t += resolution)
            {
                subSection.Add(UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, t, Mathf.PI)); // natural 3 roll
            }

            fifthsSegment.positionCount = subSection.Count;
            fifthsSegment.SetPositions(subSection.ToArray());

            float lerp = .2f;
            if (i is 7 or 8 or 11)
            {
                lerp = .3f;
            }

            Color color = Color.Lerp(Color.black, descendingFifthColors[i], lerp);

            Color endColor = color;
            if (i == 1)
            {
                endColor = darkGrey;
            }
            else if (i == 5)
            {
                color = darkGrey;
                endColor = Color.Lerp(Color.black, Color.yellow, lerp);
            }

            const float alpha = 1.0f;
            Gradient gradient = new();
            gradient.SetKeys(
                new[] { new GradientColorKey(color, 0.0f), new GradientColorKey(endColor, 1.0f) },
                new[] { new GradientAlphaKey(alpha, 0.0f), new GradientAlphaKey(alpha, 1.0f) }
            );
            fifthsSegment.colorGradient = gradient;
        }

        chromaticLineRenderer.positionCount = chromaticList.Count;
        chromaticLineRenderer.SetPositions(chromaticList.ToArray());
        chromaticLineRenderer.gameObject.SetActive(false);

        for (int j = 0; j < Octaves; j++)
        {
            // set the notes in 5th intervals evenly from 0 to 1 on the umbilical
            for (int i = 0; i < Tones; i++)
            {
                int next = scaleToFifths[j * Tones + i];

                Vector3 umbilicPosition =
                    UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, (float)i / Tones, Mathf.PI);

                // interpolate octave points to centroid of torus
                Vector3 centroid = Centroid(i);

                notes[next].transform.localPosition =
                    Vector3.Lerp(centroid, umbilicPosition, (float)(j + 1) / Octaves);

                if (j == 0)
                {
                    noteTextLabels[next].transform.localPosition =
                        Vector3.LerpUnclamped(umbilicPosition, centroid, -.2f);
                }
            }
        }
    }

    /// <summary>
    /// Takes input from keyboard or midi file
    /// </summary>
    void PlayKeys(List<Tuple<int, float>> keysAndAmplitudes)
    {
        float[] votes = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        
        // from the input, sort all notes into octaves, fifths, and thirds
        List<ulong> playingOctaves = new();
        List<ulong> playingThirds = new();
        List<ulong> playingFifths = new();
        foreach ((int key, float amp) in keysAndAmplitudes)
        {
            // update the amplitude to the current value in all cases
            notes[key].CurrentAmp = amp;

            int baseKey = key % Tones;
            votes[baseKey] += amp;
            foreach (Tuple<int, float> otherKeyAndAmp in keysAndAmplitudes)
            {
                int otherKey = otherKeyAndAmp.Item1;
                ulong id = Szudzik.uintSzudzik2tupleCombine((uint)key, (uint)otherKey);
                int compareKey = otherKey % Tones;

                if (baseKey == compareKey)
                {
                    // octave
                    playingOctaves.Add(id);
                }
                else if ((baseKey < compareKey && compareKey - baseKey == 7) || baseKey - compareKey == 5)
                {
                    // fifth
                    playingFifths.Add(id);
                }
                else if ((baseKey < compareKey && compareKey - baseKey == 4) || baseKey - compareKey == 8)
                {
                    // third
                    playingThirds.Add(id);
                }
            }
        }

        // set all other notes to zero amplitude
        List<int> currentKeys = keysAndAmplitudes.Select(x => x.Item1).ToList();
        int count = 0;
        foreach (Note note in notes)
        {
            if (!currentKeys.Contains(count))
            {
                note.CurrentAmp = 0;
            }

            count++;
        }

        // determine chords to switch off from last update
        List<ulong> playing = playingOctaves.Concat(playingThirds).Concat(playingFifths).ToList();
        List<ulong> offList = chordLineRenderers.Keys.Where(oldKey => !playing.Contains(oldKey)).ToList();

        foreach (ulong toRemove in offList)
        {
            Destroy(chordLineRenderers[toRemove].gameObject);
            chordLineRenderers.Remove(toRemove);
        }

        // create new chords
        foreach (ulong playKey in playingThirds)
        {
            uint[] pair = Szudzik.uintSzudzik2tupleReverse(playKey);
            int key = (int)pair[0];
            int otherKey = (int)pair[1];
            Note note1 = notes[key];
            Note note2 = notes[otherKey];

            float combinedAmplitude = note1.CurrentAmp + note2.CurrentAmp;
            float intensity = combinedAmplitude * 1.5f;

            bool pointingOut = Vector3.Magnitude(notes[key].transform.localPosition) <
                               Vector3.Magnitude(notes[otherKey].transform.localPosition);

            // sum all fifths below key and below otherKey and retrieve vote
            int scaleOther = Roll(otherKey, pointingOut ? 5 : -5);
            int scaleKey = Roll(key, pointingOut ? -5 : 5);

            scaleOther %= Tones;
            scaleKey %= Tones;

            float otherSum = votes[scaleOther];
            float thisSum = votes[scaleKey];

            Color calcColor = Color.white;
            if (thisSum == 0 && otherSum == 0)
            {
                // default to inner (major) color
                calcColor = pointingOut
                    ? descendingFifthColors[fifthToColor[scaleKey]]
                    : descendingFifthColors[fifthToColor[otherKey]];
            }
            else
            {
                float colorT = GetRatio(thisSum, otherSum);

                calcColor = Color.Lerp(
                    descendingFifthColors[fifthToColor[pointingOut ? scaleKey : Roll(scaleKey, -5)]],
                    descendingFifthColors[fifthToColor[pointingOut ? Roll(scaleOther, -5) : scaleOther]],
                    colorT);
            }

            Color color = Color.Lerp(
                calcColor,
                Color.white,
                .3f) * Mathf.Pow(2, intensity); // whiten and intensify on HDR

            if (chordLineRenderers.TryGetValue(playKey, out Chord lineRenderer))
            {
                // update chord color
                lineRenderer.GetComponent<Renderer>().material.color = color;
                continue;
            }

            // draw thirds as simple lines
            GameObject chordGo = new("chord");
            LineRenderer chordRend = NewLineRenderer(chordGo, BloomMat, .015f, false);
            Chord newChord = chordGo.AddComponent<Chord>();
            newChord.Init(note1, note2, chordRend);
            chordLineRenderers.Add(playKey, newChord);

            // set chord color
            chordRend.material.color = color;
        }

        foreach (ulong playKey in playingFifths)
        {
            if (chordLineRenderers.ContainsKey(playKey)) continue;
            uint[] pair = Szudzik.uintSzudzik2tupleReverse(playKey);
            int key = (int)pair[0];
            int otherKey = (int)pair[1];

            // draw fifths along umbilical
            GameObject chordGo = new("chord");
            LineRenderer chordRend = NewLineRenderer(chordGo, BloomMat, .02f, false);

            Note note1 = notes[key];
            Note note2 = notes[otherKey];
            Chord newChord = chordGo.AddComponent<Chord>();
            newChord.Init(note1, note2, chordRend);
            chordLineRenderers.Add(playKey, newChord);

            int min = Math.Min(scaleToFifths[key], scaleToFifths[otherKey]);
            float startT = (float)min / Tones;
            float endT = (float)((min + 1) % Tones) / Tones;
            if (min == 0 && scaleToFifths[otherKey] == Tones - 1)
            {
                // 0 -> 0 and 7 -> 11
                // 11 needs to wrap back to zero
                startT = (float)scaleToFifths[otherKey] / Tones;
                endT = 1;
            }

            // move fifth line across face as it approaches other note
            int startOctave = key / Tones;
            int endOctave = otherKey / Tones;

            float startLength = EdgeLength * ((float)(startOctave + 1) / Octaves);
            float endLength = EdgeLength * ((float)(endOctave + 1) / Octaves);

            List<Vector3> fifthLine = new();
            for (float t = startT; t < endT; t += .001f)
            {
                fifthLine.Add(UmbilicTorus.PointAlongUmbilical(
                    Sides,
                    Mathf.Lerp(startLength, endLength, (t - startT) / (endT - startT)),
                    Rad,
                    t,
                    Mathf.PI));
            }

            float combinedAmplitude = note1.CurrentAmp + note2.CurrentAmp;

            // set color of fifth (it is always the same color, but intensity changes)
            float intensity = combinedAmplitude * 1.5f;
            Color color = Color.Lerp(
                descendingFifthColors[scaleToFifths[otherKey]],
                Color.white,
                .3f) * Mathf.Pow(2, intensity); // whiten and intensify on HDR

            chordRend.material.color = color;

            newChord.Fifth(fifthLine);
        }
        
        // color notes
        
    }

    static float GetRatio(float a, float b)
    {
        // Handle special cases
        if (b == 0f && a != 0f)
            return 0f;
        if (a == 0f && b != 0f)
            return 1f;

        // General case: both a and b are positive
        return b / (a + b);
    }

    static int Roll(int val, int interval)
    {
        val += interval;
        return val switch
        {
            < 0 => val + Tones,
            >= Tones => val - Tones,
            _ => val
        };
    }

    void UpdateText()
    {
        foreach (TextBox textLabel in noteTextLabels)
        {
            textLabel.Billboard();
        }
    }

    public void LoadMidi()
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

                    noteGo.transform.localScale = new Vector3(.2f, .001f, duration * NoteScale);
                    noteGo.transform.Translate(Vector3.right * noteKey * .5f);
                    noteGo.transform.Translate(Vector3.forward * (onAbsoluteTime + duration * .5f) * NoteScale);
                    totalNoteCount++;
                }
            }
        }
    }

    void Update()
    {
        List<int> currentKeys = new();
        if (Keyboard.current.digit1Key.isPressed)
        {
            currentKeys.Add(0);
        }

        if (Keyboard.current.digit2Key.isPressed)
        {
            currentKeys.Add(1);
        }

        if (Keyboard.current.digit3Key.isPressed)
        {
            currentKeys.Add(2);
        }

        if (Keyboard.current.digit4Key.isPressed)
        {
            currentKeys.Add(3);
        }

        if (Keyboard.current.digit5Key.isPressed)
        {
            currentKeys.Add(4);
        }

        if (Keyboard.current.digit6Key.isPressed)
        {
            currentKeys.Add(5);
        }

        if (Keyboard.current.digit7Key.isPressed)
        {
            currentKeys.Add(6);
        }

        if (Keyboard.current.digit8Key.isPressed)
        {
            currentKeys.Add(7);
        }

        if (Keyboard.current.digit9Key.isPressed)
        {
            currentKeys.Add(8);
        }

        if (Keyboard.current.digit0Key.isPressed)
        {
            currentKeys.Add(9);
        }

        if (Keyboard.current.minusKey.isPressed)
        {
            currentKeys.Add(10);
        }

        if (Keyboard.current.equalsKey.isPressed)
        {
            currentKeys.Add(11);
        }

        List<Tuple<int, float>> adjusted = new();
        foreach (int key in currentKeys)
        {
            int newKey = key + currentKey;
            if (newKey > 12)
            {
                newKey -= 12;
            }

            //newKey += 36;

            adjusted.Add(new Tuple<int, float>(newKey, 1f)); // default 1 amplitude
        }

        if (adjusted.Any() || Keyboard.current.anyKey.wasReleasedThisFrame)
        {
            PlayKeys(adjusted);
        }
    }

    static LineRenderer NewLineRenderer(GameObject parent, Material fifthsMat, float LW, bool loop)
    {
        LineRenderer fifthsLineRenderer = parent.AddComponent<LineRenderer>();
        fifthsLineRenderer.material = fifthsMat;
        fifthsLineRenderer.startWidth = LW;
        fifthsLineRenderer.endWidth = LW;
        fifthsLineRenderer.loop = loop;
        fifthsLineRenderer.useWorldSpace = false;

        return fifthsLineRenderer;
    }

    static Vector3 Centroid(int i)
    {
        int fractionOfTorus = (i + 1) % Sections;
        float alpha = 2 * Mathf.PI * fractionOfTorus / Sections;
        return new Vector3(
            Rad * Mathf.Sin(alpha),
            0,
            Rad * Mathf.Cos(alpha));
    }

    static float ToHertz(int n)
    {
        return 27.5f * Mathf.Pow(2, (float)n / 12); // from A0 at 27.5 Hz
    }
}