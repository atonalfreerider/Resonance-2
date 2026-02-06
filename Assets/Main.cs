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

    public const int Tones = 12;
    public const int Octaves = 8;
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

    public int currentKey = 0; // A
    float torusRotation = 0f; // Current rotation of the torus

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
        // Maps semitone interval to circle-of-fifths index: I(0)->11(Blue), V(7)->10(Blue/Green), IV(5)->0(Red)
        for (int i = 0; i < Tones; i++)
        {
            int fifthsIndex = (11 - (7 * i) % Tones + Tones) % Tones;
            fifthToColor.Add(i, fifthsIndex);
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
        
        // Initialize note colors with empty input to set default colors
        PlayKeys(new List<Tuple<int, float>>());
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
            chromaticList.Add(UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, t, Mathf.PI + torusRotation, ratio));
        }

        for (int i = 0; i < Tones; i++)
        {
            LineRenderer fifthsSegment = fifthsLineRenderer[i];
            List<Vector3> subSection = new();
            for (float t = (float)i / Tones; t < (float)(i + 1) / Tones; t += resolution)
            {
                subSection.Add(UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, t, Mathf.PI + torusRotation));
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
                    UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, (float)i / Tones, Mathf.PI + torusRotation);

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

    public void ChangeKey(int newKey)
    {
        if (newKey == currentKey) return;
        
        currentKey = newKey;
        
        // Calculate rotation needed to put new key at 12 o'clock
        // Each step is 2π/12 = π/6 radians
        torusRotation = -currentKey * (2f * Mathf.PI / Tones);
        
        // Rebuild the torus with new rotation
        SetUmbilic();
    }

    Color CalculateNoteColor(int noteIndex, List<Tuple<int, float>> activeNotes)
    {
        int baseNote = noteIndex % Tones;
        int relativeToKey = (baseNote - currentKey + Tones) % Tones;
        
        // Base color from position in circle of fifths relative to current key
        Color baseColor = descendingFifthColors[fifthToColor[relativeToKey]];
        
        if (activeNotes.Count <= 1)
        {
            // Single note or no harmonies - use base color
            return baseColor;
        }
        
        // Calculate harmonic relationships with other active notes
        Color blendedColor = baseColor * 0.5f; // Start with half the base color
        float totalInfluence = 0.5f;
        
        foreach (var (otherNoteIndex, amplitude) in activeNotes)
        {
            if (otherNoteIndex == noteIndex) continue;
            
            int otherBase = otherNoteIndex % Tones;
            int interval = (otherBase - baseNote + Tones) % Tones;
            
            float influence = 0f;
            Color harmonicColor = Color.clear;
            
            // Determine harmonic relationship and color influence
            switch (interval)
            {
                case 0: // Octave - same color
                    harmonicColor = baseColor;
                    influence = amplitude * 0.3f;
                    break;
                case 5: // Perfect fourth
                case 7: // Perfect fifth
                    int fifthRelativeToKey = (otherBase - currentKey + Tones) % Tones;
                    harmonicColor = descendingFifthColors[fifthToColor[fifthRelativeToKey]];
                    influence = amplitude * 0.4f;
                    break;
                case 3: // Minor third
                case 4: // Major third
                case 8: // Minor sixth
                case 9: // Major sixth
                    int thirdRelativeToKey = (otherBase - currentKey + Tones) % Tones;
                    harmonicColor = descendingFifthColors[fifthToColor[thirdRelativeToKey]];
                    influence = amplitude * 0.3f;
                    break;
                default:
                    // Dissonant intervals - less influence
                    int dissonantRelativeToKey = (otherBase - currentKey + Tones) % Tones;
                    harmonicColor = descendingFifthColors[fifthToColor[dissonantRelativeToKey]];
                    influence = amplitude * 0.1f;
                    break;
            }
            
            if (influence > 0)
            {
                blendedColor += harmonicColor * influence;
                totalInfluence += influence;
            }
        }
        
        // Normalize the color
        if (totalInfluence > 0)
        {
            blendedColor /= totalInfluence;
        }
        
        return blendedColor;
    }

    /// <summary>
    /// Takes input from keyboard or midi file
    /// </summary>
    public void PlayKeys(List<Tuple<int, float>> keysAndAmplitudes)
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
            int scaleOther = otherKey % Tones;
            int scaleKey = key % Tones;

            float otherSum = votes[scaleOther];
            float thisSum = votes[scaleKey];

            Color calcColor = Color.white;
            if (thisSum == 0 && otherSum == 0)
            {
                // default to inner note color
                calcColor = pointingOut
                    ? descendingFifthColors[fifthToColor[(scaleKey - currentKey + Tones) % Tones]]
                    : descendingFifthColors[fifthToColor[(scaleOther - currentKey + Tones) % Tones]];
            }
            else
            {
                float colorT = GetRatio(thisSum, otherSum);

                int startIdx = scaleKey;
                int endIdx = scaleOther;

                calcColor = Color.Lerp(
                    descendingFifthColors[fifthToColor[(startIdx - currentKey + Tones) % Tones]],
                    descendingFifthColors[fifthToColor[(endIdx - currentKey + Tones) % Tones]],
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

            // Get torus positions (0-11)
            int torusIdx1 = scaleToFifths[key] % Tones;
            int torusIdx2 = scaleToFifths[otherKey] % Tones;

            float t1 = (float)torusIdx1 / Tones;
            float t2 = (float)torusIdx2 / Tones;

            // Handle wrap-around for shortest path
            float delta = t2 - t1;
            if (delta > 0.5f) t1 += 1.0f;
            else if (delta < -0.5f) t2 += 1.0f;

            int startOctave = key / Tones;
            int endOctave = otherKey / Tones;
            float startFactor = (float)(startOctave + 1) / Octaves;
            float endFactor = (float)(endOctave + 1) / Octaves;

            List<Vector3> fifthLine = new();
            const int steps = 40;
            for (int i = 0; i <= steps; i++)
            {
                float progress = (float)i / steps;
                float t = Mathf.Lerp(t1, t2, progress);
                float currentFactor = Mathf.Lerp(startFactor, endFactor, progress);
                
                float wrappedT = t % 1.0f;
                if (wrappedT < 0) wrappedT += 1.0f;

                Vector3 umbilicPos = UmbilicTorus.PointAlongUmbilical(
                    Sides,
                    EdgeLength,
                    Rad,
                    wrappedT,
                    Mathf.PI + torusRotation);

                Vector3 centroid = CentroidAt(wrappedT);
                fifthLine.Add(Vector3.Lerp(centroid, umbilicPos, currentFactor));
            }

            float combinedAmplitude = note1.CurrentAmp + note2.CurrentAmp;

            // set color of fifth (it is always the same color, but intensity changes)
            float intensity = combinedAmplitude * 1.5f;
            Color color = Color.Lerp(
                descendingFifthColors[fifthToColor[(key % Tones - currentKey + Tones) % Tones]],
                Color.white,
                .3f) * Mathf.Pow(2, intensity); // whiten and intensify on HDR

            chordRend.material.color = color;

            newChord.Fifth(fifthLine);
        }
        
        // Color individual notes based on harmonic relationships
        for (int i = 0; i < notes.Count; i++)
        {
            Note note = notes[i];
            Color noteColor = CalculateNoteColor(i, keysAndAmplitudes);
            
            // Apply intensity based on amplitude
            if (note.CurrentAmp > 0)
            {
                // Brighten and intensify active notes
                float intensity = note.CurrentAmp * 1.5f;
                noteColor = Color.Lerp(noteColor, Color.white, 0.2f) * Mathf.Pow(2, intensity);
            }
            else
            {
                // Dim inactive notes
                noteColor = Color.Lerp(noteColor, Color.black, 0.8f);
            }
            
            // Set the note color
            note.SetColor(noteColor);
        }
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

    static Vector3 CentroidAt(float t)
    {
        float i = t * Tones;
        float alpha = 2 * Mathf.PI * (i + 1) / Sections;
        return new Vector3(
            Rad * Mathf.Sin(alpha),
            0,
            Rad * Mathf.Cos(alpha));
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