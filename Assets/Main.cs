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
    static readonly int Color1 = Shader.PropertyToID("_Color");
    public string midiPath;

    const float NoteScale = .01f;

    const int Tones = 12;
    const int Octaves = 8;
    const int Sides = 3;
    const float EdgeLength = 0.67f;
    const float Rad = 1f;
    const int Sections = Tones / Sides;

    readonly List<GameObject> pointGameObjects = new();
    readonly List<TextBox> noteTextLabels = new();

    LineRenderer fifthsLineRenderer;
    LineRenderer chromaticLineRenderer;

    readonly Dictionary<ulong, LineRenderer> chordLineRenderers = new();
    Material chordMat;

    readonly string[] noteLabels = { "A", "A#", "B", "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#" };
    readonly Dictionary<int, float> hertzTable = new();

    int currentKey = 0; // A

    readonly Dictionary<int, int> scaleToFifths = new();

    CameraControl cameraControl;

    void Awake()
    {
        for (int j = 0; j < Octaves; j++)
        {
            // set the notes in 5th intervals evenly from 0 to 1 on the umbilical
            int next = Tones * j;
            for (int i = 0; i < Tones; i++)
            {
                int noteIndex = j * Tones + i;
                GameObject pointGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                pointGo.transform.SetParent(transform, false);
                pointGo.transform.localScale =
                    Vector3.one * Mathf.Lerp(.03f, .005f, (float)(noteIndex) / (Tones * Octaves));
                pointGameObjects.Add(pointGo);
                pointGo.name = $"{noteLabels[i]} {j}";

                scaleToFifths.Add(noteIndex, next);
                hertzTable.Add(noteIndex, ToHertz(noteIndex));

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

        Material fifthsMat = new(Shader.Find("Unlit/Color"))
        {
            color = new Color(0.5f, 0.5f, 0.5f)
        };
        GameObject fifthsGo = new("fifths");
        fifthsGo.transform.SetParent(transform, false);
        fifthsLineRenderer = NewLineRenderer(fifthsGo, fifthsMat, .01f, true);

        Material chromaticMat = new(Shader.Find("Unlit/Color"))
        {
            color = new Color(0.2f, 0.2f, 0.2f)
        };
        GameObject chromGo = new("chromatic");
        chromGo.transform.SetParent(transform, false);
        chromaticLineRenderer = NewLineRenderer(chromGo, chromaticMat, .007f, true);

        chordMat = new Material(Shader.Find("Unlit/Color"));

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
        foreach (GameObject pointGo in pointGameObjects)
        {
            pointGo.transform.localPosition = new Vector3(0, stack, 0);
            stack += .03f;
        }
    }

    void SetUmbilic()
    {
        List<Vector3> umbilicalList = new();
        List<Vector3> chromaticList = new();
        const int ratio = Sides * 2 - 1;
        for (float t = 0; t < 1; t += .001f)
        {
            umbilicalList.Add(UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, t, Mathf.PI)); // natural 3 roll
            chromaticList.Add(UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, t, Mathf.PI, ratio)); // 5 roll
        }

        fifthsLineRenderer.positionCount = umbilicalList.Count;
        fifthsLineRenderer.SetPositions(umbilicalList.ToArray());

        chromaticLineRenderer.positionCount = chromaticList.Count;
        chromaticLineRenderer.SetPositions(chromaticList.ToArray());
        //chromaticLineRenderer.gameObject.SetActive(false);

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

                pointGameObjects[next].transform.localPosition =
                    Vector3.Lerp(umbilicPosition, centroid, (float)j / Octaves);

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
    void PlayKeys(List<int> keys)
    {
        foreach (GameObject pointGameObject in pointGameObjects)
        {
            pointGameObject.GetComponent<Renderer>().material.SetColor(Color1, Color.white);
        }

        List<ulong> playingOctaves = new();
        List<ulong> playingThirds = new();
        List<ulong> playingFifths = new();
        foreach (int key in keys)
        {
            pointGameObjects[key].GetComponent<Renderer>().material.SetColor(Color1, Color.red);

            int baseKey = key % Tones;
            foreach (int otherKey in keys)
            {
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

        List<ulong> playing = playingOctaves.Concat(playingThirds).Concat(playingFifths).ToList();
        List<ulong> offList = chordLineRenderers.Keys.Where(oldKey => !playing.Contains(oldKey)).ToList();

        foreach (ulong toRemove in offList)
        {
            Destroy(chordLineRenderers[toRemove].gameObject);
            chordLineRenderers.Remove(toRemove);
        }

        foreach (ulong playKey in playingThirds)
        {
            if (chordLineRenderers.ContainsKey(playKey)) continue;
            uint[] pair = Szudzik.uintSzudzik2tupleReverse(playKey);
            int key = (int)pair[0];
            int otherKey = (int)pair[1];

            // draw thirds as simple lines
            GameObject chordGo = new("chord");
            LineRenderer chordRend = NewLineRenderer(chordGo, chordMat, .015f, false);
            chordLineRenderers.Add(playKey, chordRend);

            chordRend.positionCount = 2;
            chordRend.SetPosition(0, pointGameObjects[key].transform.position);
            chordRend.SetPosition(1, pointGameObjects[otherKey].transform.localPosition);
        }

        foreach (ulong playKey in playingFifths)
        {
            if (chordLineRenderers.ContainsKey(playKey)) continue;
            uint[] pair = Szudzik.uintSzudzik2tupleReverse(playKey);
            int key = (int)pair[0];
            int otherKey = (int)pair[1];

            // draw fifths along umbilical
            GameObject chordGo = new("chord");
            LineRenderer chordRend = NewLineRenderer(chordGo, chordMat, .02f, false);

            chordLineRenderers.Add(playKey, chordRend);

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
            int startIndex = key % Tones - key;
            int endIndex = otherKey % Tones - otherKey;

            float startLength = EdgeLength * (1 - (float)startIndex / Octaves);
            float endLength = EdgeLength * (1 - (float)endIndex / Octaves);

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

            chordRend.positionCount = fifthLine.Count;
            chordRend.SetPositions(fifthLine.ToArray());
        }
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

        List<int> adjusted = new();
        foreach (int key in currentKeys)
        {
            int newKey = key + currentKey;
            if (newKey > 12)
            {
                newKey -= 12;
            }

            adjusted.Add(newKey);
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
            (Rad + EdgeLength * .5f) * Mathf.Sin(alpha),
            0,
            (Rad + EdgeLength * .5f) * Mathf.Cos(alpha));
    }

    static float ToHertz(int n)
    {
        return 27.5f * Mathf.Pow(2, (float)n / 12); // from A0 at 27.5 Hz
    }
}