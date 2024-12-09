using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Midi;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Umbilic Torus Circle of Fifths credit:
/// https://jimishol.github.io/post/tonality/
/// </summary>
public class Main : MonoBehaviour
{
    private static readonly int Color1 = Shader.PropertyToID("_Color");
    public string midiPath;

    const float noteScale = .01f;

    const int tones = 12;
    const int octaves = 7;
    const int s = 3;
    const float edgeLength = 0.67f;
    const float rad = 1f;
    const int sections = tones / s;

    readonly List<GameObject> pointGameObjects = new();
    readonly List<TextBox> noteTextLabels = new();

    LineRenderer fifthsLineRenderer;
    LineRenderer chromaticLineRenderer;

    readonly List<LineRenderer> chordLineRenderers = new();
    Material chordMat;

    readonly string[] noteLabels = { "A", "A#", "B", "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#" };

    int currentKey = 0; // A

    readonly Dictionary<int, int> scaleToFifths = new();

    CameraControl cameraControl;

    void Awake()
    {
        for (int j = 0; j < octaves; j++)
        {
            // set the notes in 5th intervals evenly from 0 to 1 on the umbilical
            int next = tones * j;
            for (int i = 0; i < tones; i++)
            {
                GameObject pointGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                pointGo.transform.SetParent(transform, false);
                pointGo.transform.localScale =
                    Vector3.one * Mathf.Lerp(.03f, .005f, (float)(j * tones + i) / (tones * octaves));
                pointGameObjects.Add(pointGo);
                pointGo.name = $"{noteLabels[i]} {j}";
                
                scaleToFifths.Add(j*tones + i, next);
                
                next += 5; // fifths
                if (next >= tones * (j + 1))
                {
                    next -= tones;
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
        const int ratio = s * 2 - 1;
        for (float t = 0; t < 1; t += .001f)
        {
            umbilicalList.Add(UmbilicTorus.PointAlongUmbilical(s, edgeLength, rad, t, Mathf.PI)); // natural 3 roll
            chromaticList.Add(UmbilicTorus.PointAlongUmbilical(s, edgeLength, rad, t, Mathf.PI,  ratio)); // 5 roll
        }

        fifthsLineRenderer.positionCount = umbilicalList.Count;
        fifthsLineRenderer.SetPositions(umbilicalList.ToArray());

        chromaticLineRenderer.positionCount = chromaticList.Count;
        chromaticLineRenderer.SetPositions(chromaticList.ToArray());
        //chromaticLineRenderer.gameObject.SetActive(false);

        for (int j = 0; j < octaves; j++)
        {
            // set the notes in 5th intervals evenly from 0 to 1 on the umbilical
            for (int i = 0; i < tones; i++)
            {
                int next = scaleToFifths[j * tones + i];
                
                Vector3 umbilicPosition = UmbilicTorus.PointAlongUmbilical(s, edgeLength, rad, (float)i / tones, Mathf.PI);

                // interpolate octave points to centroid of torus
                Vector3 centroid = Centroid(i);

                pointGameObjects[next].transform.localPosition =
                    Vector3.Lerp(umbilicPosition, centroid, (float)j / octaves);

                if (j == 0)
                {
                    noteTextLabels[next].transform.localPosition =  Vector3.LerpUnclamped(umbilicPosition, centroid, -.2f);
                }
            }
        }
    }

    /// <summary>
    /// Takes input from keyboard or midi file
    /// </summary>
    /// <param name="keys"></param>
    void PlayKeys(List<int> keys)
    {
        foreach (GameObject pointGameObject in pointGameObjects)
        {
            pointGameObject.GetComponent<Renderer>().material.SetColor(Color1, Color.white);
        }

        foreach (LineRenderer chordLineRenderer in chordLineRenderers)
        {
            Destroy(chordLineRenderer.gameObject);
        }
        
        chordLineRenderers.Clear();
        
        foreach (int key in keys)
        {
            pointGameObjects[key].GetComponent<Renderer>().material.SetColor(Color1, Color.red);

            foreach (int otherKey in keys)
            {
                if (Math.Abs(key - otherKey) == 7)
                {
                    // fifth
                    GameObject chordGo = new("chord");
                    LineRenderer chordRend = NewLineRenderer(chordGo, chordMat, .02f, false);
                    chordLineRenderers.Add(chordRend);

                    int min = Math.Min(scaleToFifths[key], scaleToFifths[otherKey]);
                    
                    float startT = (float)min / tones;
                    float endT = (float)((min + 1)%tones) / tones;
                    List<Vector3> fifthLine = new();
                    for (float t = startT; t < endT; t += .001f)
                    {
                        fifthLine.Add(UmbilicTorus.PointAlongUmbilical(s, edgeLength, rad, t, Mathf.PI));
                    }

                    chordRend.positionCount =fifthLine.Count;
                    chordRend.SetPositions(fifthLine.ToArray());
                }
                
                if (Math.Abs(key - otherKey) == 4)
                {
                    // third
                    GameObject chordGo = new("chord");
                    LineRenderer chordRend = NewLineRenderer(chordGo, chordMat, .015f, false);
                    chordLineRenderers.Add(chordRend);

                    chordRend.positionCount = 2;
                    chordRend.SetPosition(0, pointGameObjects[key].transform.position);
                    chordRend.SetPosition(1, pointGameObjects[otherKey].transform.localPosition);
                }
            }
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

                    noteGo.transform.localScale = new Vector3(.2f, .001f, duration * noteScale);
                    noteGo.transform.Translate(Vector3.right * noteKey * .5f);
                    noteGo.transform.Translate(Vector3.forward * (onAbsoluteTime + duration * .5f) * noteScale);
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
        int fractionOfTorus = (i + 1) % sections;
        float alpha = 2 * Mathf.PI * fractionOfTorus / sections;
        return new Vector3(
            (rad + edgeLength * .5f) * Mathf.Sin(alpha),
            0, 
            (rad + edgeLength * .5f) * Mathf.Cos(alpha));
    }
}