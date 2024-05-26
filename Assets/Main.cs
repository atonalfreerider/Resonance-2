using System.Collections.Generic;
using System.Linq;
using NAudio.Midi;
using TMPro;
using UnityEngine;

/// <summary>
/// Umbilic Torus Circle of Fifths credit:
/// https://jimishol.github.io/post/tonality/
/// </summary>
public class Main : MonoBehaviour
{
    public string midiPath;

    const float noteScale = .01f;

    const int tones = 12;
    const int octaves = 7;

    readonly List<GameObject> pointGameObjects = new();
    readonly List<TextBox> noteTextLabels = new();

    LineRenderer fifthsLineRenderer;
    LineRenderer chromaticLineRenderer;

    readonly string[] noteLabels = { "A", "A#", "B", "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#" };

    int currentKey = 0; // A

    CameraControl cameraControl;

    void Awake()
    {
        for (int j = 0; j < octaves; j++)
        {
            for (int i = 0; i < tones; i++)
            {
                GameObject pointGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                pointGo.transform.SetParent(transform, false);
                pointGo.transform.localScale =
                    Vector3.one * Mathf.Lerp(.03f, .005f, (float)(j * tones + i) / (tones * octaves));
                pointGameObjects.Add(pointGo);
                pointGo.name = $"{noteLabels[i]} {j}";

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
        fifthsLineRenderer = fifthsGo.AddComponent<LineRenderer>();
        fifthsLineRenderer.material = fifthsMat;
        fifthsLineRenderer.startWidth = .01f;
        fifthsLineRenderer.endWidth = .01f;
        fifthsLineRenderer.loop = true;
        fifthsLineRenderer.useWorldSpace = false;

        Material chromaticMat = new(Shader.Find("Unlit/Color"))
        {
            color = new Color(0.2f, 0.2f, 0.2f)
        };
        GameObject chromGo = new GameObject("chromatic");
        chromGo.transform.SetParent(transform, false);
        chromaticLineRenderer = chromGo.AddComponent<LineRenderer>();
        chromaticLineRenderer.material = chromaticMat;
        chromaticLineRenderer.startWidth = .01f;
        chromaticLineRenderer.endWidth = .01f;
        chromaticLineRenderer.loop = true;
        chromaticLineRenderer.useWorldSpace = false;

        SetUmbilic();
    }

    void Start()
    {
        UpdateText();
        Camera.main.GetComponent<CameraControl>().MovementUpdater += UpdateText;
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
        const int s = 3;
        const float edgeLength = 0.67f;
        const float rad = 1f;
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

        const int sections = tones / s;
        for (int j = 0; j < octaves; j++)
        {
            // set the notes in 5th intervals evenly from 0 to 1 on the umbilical
            int next = tones * j;

            for (int i = 0; i < tones; i++)
            {
                Vector3 umbilicPosition = UmbilicTorus.PointAlongUmbilical(s, edgeLength, rad, (float)i / tones, Mathf.PI);

                // interpolate octave points to centroid of torus
                int fractionOfTorus = (i + 1) % sections;
                float alpha = 2 * Mathf.PI * fractionOfTorus / sections;
                Vector3 centroid = new((rad + edgeLength * .5f) * Mathf.Sin(alpha), 0,
                    (rad + edgeLength * .5f) * Mathf.Cos(alpha));

                pointGameObjects[next].transform.localPosition =
                    Vector3.Lerp(umbilicPosition, centroid, (float)j / octaves);

                if (j == 0)
                {
                    noteTextLabels[next].transform.localPosition =  Vector3.LerpUnclamped(umbilicPosition, centroid, -.2f);
                }

                next += 5; // fifths
                if (next >= tones * (j + 1))
                {
                    next -= tones;
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
}