using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
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
    UmbilicField tonalField;

    LineRenderer chromaticLineRenderer;

    readonly Dictionary<ulong, Chord> chordLineRenderers = new();

    readonly string[] noteLabels = { "A", "A#", "B", "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#" };

    public int currentKey = 0; // A (Default)
    float currentVisualRotation = 0f; 
    float currentVisualTwist = Mathf.PI;
    int visualKeyForRendering = 0;
    private Coroutine keyChangeCoroutine;
    private List<Tuple<int, float>> lastActiveKeys = new();

    private static readonly (int x, int y)[] pathMap = {
        (0,0),   // 0: Unison
        (-1,2),  // 1: -7 + 8 = 1
        (2,0),   // 2: 14 = 2
        (1,-1),  // 3: 7 - 4 = 3 (A to C: Turn to E, Twist to C)
        (0,1),   // 4: 4 (Major Third)
        (-1,0),  // 5: -7 = 5 (Fourth)
        (2,-2),  // 6: 14 - 8 = 6 (Tritone)
        (1,0),   // 7: 7 (Fifth)
        (0,-1),  // 8: -4 = 8 (Minor Sixth)
        (-1,1),  // 9: -7 + 4 = 9 (Major Sixth)
        (-2,0),  // 10: -14 = 10
        (1,-2)   // 11: 7 - 8 = -1 = 11
    };

    readonly Dictionary<int, int> scaleToFifths = new();

    CameraControl cameraControl;
    public static bool ReducedMotion;
    public bool MinorMode, UseFlats, ShowSurfaces = true, ShowDiagonals = true, ShowStructure = true, ShowRegisters = true, SoundingOnly, DiatonicStrip;
    public int SelectedSurface = 3;
    public bool ShowHarmonics = true, SurfaceGuide;
    public float FieldDensity = .7f;
    public float ResonanceHalfLife = 1.8f, ResonanceGain = 1f;
    public float VisualReleaseSeconds = 2.4f, NoteReleaseSeconds = .7f;
    public UmbilicField TonalField => tonalField;
    public float VisualRotation => currentVisualRotation;
    public float VisualTwist => currentVisualTwist;
    public Vector3 UmbilicPoint(float t) => UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, Mathf.Repeat(t,1), currentVisualTwist);
    public MusicSynth Synth { get; private set; }
    public IReadOnlyList<Tuple<int, float>> ActiveNotes => lastActiveKeys;
    public event Action StateChanged;
    readonly Stack<Chord> chordPool = new();
    readonly List<Vector3> curveBuffer = new(41);
    public string KeySource = "Manual";
    public int CollectionRoot => HarmonyModel.Mod(currentKey + (MinorMode ? 3 : 0));
    public string PitchName(int pc) => HarmonyModel.Name(pc, UseFlats);
    public Vector3 SurfaceCurve(int a, int b, float u)
    {
        float t1=scaleToFifths[HarmonyModel.Mod(a)]/(float)Tones+currentVisualRotation;
        float t2=scaleToFifths[HarmonyModel.Mod(b)]/(float)Tones+currentVisualRotation;
        if(t2-t1>.5f)t1++;else if(t2-t1<-.5f)t2++;
        return transform.TransformPoint(GetPointAt(Mathf.Lerp(t1,t2,u),1));
    }

    void Awake()
    {
        visualKeyForRendering = currentKey;
        currentKey = HarmonyModel.Mod(currentKey); currentVisualRotation = pathMap[currentKey].x / (float)Tones; currentVisualTwist = Mathf.PI + pathMap[currentKey].y * 2f * Mathf.PI / 3f;

        for (int j = 0; j < Octaves; j++)
        {
            // set the notes in 5th intervals evenly from 0 to 1 on the umbilical
            int next = Tones * j;
            for (int i = 0; i < Tones; i++)
            {
                int noteIndex = j * Tones + i;
                float scaleFactor = Mathf.Lerp(.03f, .005f, (float)noteIndex / (Tones * Octaves));
                Note newNote = Note.Create($"{noteLabels[i]} {j}", ToHertz(noteIndex), scaleFactor);
                newNote.Index = noteIndex; newNote.transform.SetParent(transform, false);
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
            Material fifthsMat = new(Resources.Load<Shader>("HarmonicGlow"));
            GameObject fifthsGo = new("fifths");
            fifthsGo.transform.SetParent(transform, false);
            fifthsLineRenderer.Add(NewLineRenderer(fifthsGo, fifthsMat, .01f, false));

        }

        Material chromaticMat = new(Shader.Find("Universal Render Pipeline/Unlit"))
        {
            color = new Color(0.2f, 0.2f, 0.2f)
        };
        GameObject chromGo = new("chromatic");
        chromGo.transform.SetParent(transform, false);
        chromaticLineRenderer = NewLineRenderer(chromGo, chromaticMat, .007f, true);

        SetUmbilic();
        var fieldGo = new GameObject("Umbilic tonal field"); fieldGo.transform.SetParent(transform,false);
        tonalField = fieldGo.AddComponent<UmbilicField>(); tonalField.Initialize(this);
    }

    void Start()
    {
        UpdateText();
        UpdateLabelStyles();
        cameraControl = Camera.main.GetComponent<CameraControl>(); if (cameraControl != null) cameraControl.MovementUpdater += UpdateText;
        Camera.main.transform.LookAt(Vector3.zero);
        
        Synth = gameObject.AddComponent<MusicSynth>(); gameObject.AddComponent<HarmonyExplorer>(); gameObject.AddComponent<SurfaceOverlay>();
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
            chromaticList.Add(UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, t, currentVisualTwist, ratio));
        }

        for (int i = 0; i < Tones; i++)
        {
            LineRenderer fifthsSegment = fifthsLineRenderer[i];
            List<Vector3> subSection = new();
            for (float t = (float)i / Tones; t < (float)(i + 1) / Tones; t += resolution)
            {
                subSection.Add(UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, t+currentVisualRotation, currentVisualTwist));
            }

            fifthsSegment.positionCount = subSection.Count;
            fifthsSegment.SetPositions(subSection.ToArray());

            fifthsSegment.startColor=TonalColorField.Pitch(HarmonyModel.Mod(i*5),currentKey)*.45f;
            fifthsSegment.endColor=TonalColorField.Pitch(HarmonyModel.Mod((i+1)*5),currentKey)*.45f;
        }

        chromaticLineRenderer.positionCount = chromaticList.Count;
        chromaticLineRenderer.SetPositions(chromaticList.ToArray());
        chromaticLineRenderer.gameObject.SetActive(false);

        UpdateTorusPoints(currentVisualRotation, visualKeyForRendering);
        UpdateLabelStyles();
    }

    void UpdateTorusPoints(float phaseShift, int visualKey)
    {


        for (int j = 0; j < Octaves; j++)
        {
            for (int i = 0; i < Tones; i++)
            {
                // Correctly map the chromatic note 'i' to its circle-of-fifths slot
                int chromaticIndex = j * Tones + i;
                int slot = scaleToFifths[chromaticIndex] % Tones;

                float t = ((float)slot / Tones + phaseShift) % 1.0f;
                if (t < 0) t += 1.0f;

                Vector3 umbilicPosition = UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, t, currentVisualTwist);
                Vector3 centroid = CentroidAt(t);

                // Update the position of the chromatic note object
                notes[chromaticIndex].transform.localPosition = Vector3.Lerp(centroid, umbilicPosition, (float)(j + 1) / Octaves);

                if (j == 0)
                {
                    noteTextLabels[i].transform.localPosition = Vector3.LerpUnclamped(umbilicPosition, centroid, -.2f);
                }
            }
        }

    }

    Vector3 GetPointAt(float t, float factor)
    {
        float wt = t % 1.0f; if (wt < 0) wt += 1.0f;
        Vector3 umbilicPos = UmbilicTorus.PointAlongUmbilical(Sides, EdgeLength, Rad, wt, currentVisualTwist);
        return Vector3.Lerp(CentroidAt(wt), umbilicPos, factor);
    }

    void AddQuad(List<int> tris, int a, int b, int c, int d)
    {
        tris.AddRange(new[] { a, b, c, a, c, d });
    }

    public void ChangeKey(int newKey, float duration = .7f)
    {
        if (keyChangeCoroutine != null) StopCoroutine(keyChangeCoroutine);
        currentKey = HarmonyModel.Mod(newKey);
        visualKeyForRendering = currentKey;
        if (ReducedMotion || duration <= 0)
        {
            currentVisualRotation = pathMap[currentKey].x / (float)Tones;
            currentVisualTwist = Mathf.PI + pathMap[currentKey].y * 2f * Mathf.PI / 3f;
            RefreshView(); keyChangeCoroutine = null;
        }
        else keyChangeCoroutine = StartCoroutine(KeyChangeRoutine(duration));
        StateChanged?.Invoke();
    }
    System.Collections.IEnumerator KeyChangeRoutine(float duration)
    {
        float rotation = currentVisualRotation, twist = currentVisualTwist;
        float targetRotation = pathMap[currentKey].x / (float)Tones;
        float targetTwist = Mathf.PI + pathMap[currentKey].y * 2f * Mathf.PI / 3f;
        float elapsed = 0;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.SmoothStep(0, 1, Mathf.Clamp01(elapsed / duration));
            currentVisualRotation = Mathf.Lerp(rotation, targetRotation, t);
            currentVisualTwist = Mathf.Lerp(twist, targetTwist, t);
            RefreshView(); yield return null;
        }
        keyChangeCoroutine = null;
    }
    public void RefreshView()
    {
        SetUmbilic(); UpdateText();
        foreach (var line in fifthsLineRenderer) line.enabled = ShowStructure;
        RenderKeys();
    }
    public void PlayKeys(List<Tuple<int, float>> values) => SetNotes(values, true);
    public void SetNotes(List<Tuple<int, float>> values, bool audio)
    {
        lastActiveKeys = values.Where(n => n.Item1 >= 0 && n.Item1 < notes.Count && n.Item2 > 0 && !float.IsNaN(n.Item2))
            .GroupBy(n => n.Item1).Select(g => Tuple.Create(g.Key, Mathf.Clamp01(g.Max(n => n.Item2)))).ToList();
        RenderKeys();
        if (audio && Synth != null) { Synth.ResetVoices(); Synth.Schedule(AudioSettings.dspTime, lastActiveKeys); }
        StateChanged?.Invoke();
    }
    public void StrikeNote(int index,float velocity){if(index>=0&&index<notes.Count)notes[index].Strike(velocity);}
    public void Silence() => PlayKeys(new List<Tuple<int, float>>());
    void RenderKeys()
    {
        foreach (var note in notes) note.CurrentAmp = 0;
        foreach (var n in lastActiveKeys) notes[n.Item1].CurrentAmp = n.Item2;
        var wanted = new HashSet<ulong>();
        for (int a = 0; a < lastActiveKeys.Count; a++) for (int b = a + 1; b < lastActiveKeys.Count; b++)
        {
            int ia = lastActiveKeys[a].Item1, ib = lastActiveKeys[b].Item1;
            int interval = HarmonyModel.Mod(ib - ia);
            bool fifth = interval is 5 or 7;
            bool border = interval is 4 or 8;
            bool diagonal = interval is 3 or 9 or 1 or 11;
            if (!(fifth || border || interval==0 || (diagonal && ShowDiagonals))) continue;
            ulong id = Szudzik.uintSzudzik2tupleCombine((uint)Mathf.Min(ia,ib), (uint)Mathf.Max(ia,ib));
            wanted.Add(id);
            if (!chordLineRenderers.TryGetValue(id, out var chord))
            {
                if (chordPool.Count > 0) { chord = chordPool.Pop(); chord.gameObject.SetActive(true); }
                else
                {
                    var go = new GameObject("Sounding interval"); go.transform.SetParent(transform, false);
                    var lr = NewLineRenderer(go, new Material(Resources.Load<Shader>("HarmonicGlow")), .012f, false);
                    chord = go.AddComponent<Chord>();
                }
                chord.Init(notes[ia], notes[ib], chord.GetComponent<LineRenderer>());
                chordLineRenderers.Add(id, chord);
            }
            chord.Drive(notes[ia].CurrentAmp,notes[ib].CurrentAmp,TonalColorField.Pitch(ia,currentKey),TonalColorField.Pitch(ib,currentKey),VisualReleaseSeconds);
        }
        foreach(var entry in chordLineRenderers)
        {
            var chord=entry.Value;
            if(!wanted.Contains(entry.Key))chord.Release();
            int ia=chord.Note1.Index,ib=chord.Note2.Index;
            chord.Recolor(TonalColorField.Pitch(ia,currentKey),TonalColorField.Pitch(ib,currentKey));
            // Only octave/radial lines and the major-third triangle edges share
            // a cross-section. Every other interval follows the umbilic curve.
            if (HarmonyModel.Mod(ib-ia) is not (0 or 4 or 8))
            {
                float t1 = scaleToFifths[ia] % Tones / (float)Tones + currentVisualRotation;
                float t2 = scaleToFifths[ib] % Tones / (float)Tones + currentVisualRotation;
                curveBuffer.Clear();
                ShortSurfaceRoute(t1,t2,(ia/12+1)/(float)Octaves,(ib/12+1)/(float)Octaves,curveBuffer);
                chord.Fifth(curveBuffer);
            }
        }
        for (int i=0;i<notes.Count;i++)
        {
            var note = notes[i];
            Color color = TonalColorField.Pitch(i,currentKey);
            note.Configure(color,(ShowRegisters || i/12==3) && !SoundingOnly,NoteReleaseSeconds);
        }
    }
    // The umbilic parameter winds THREE times around the hole. Wrapping it at
    // 0.5 selected long physical arcs. Unwrap the major angle at 1/6 instead,
    // then travel across the appropriate triangle edge to retain both endpoints.
    public void ShortSurfaceRoute(float from,float to,float fromRegister,float toRegister,List<Vector3> result)
    {
        int bestShift=0;float bestLength=float.PositiveInfinity;
        for(int shift=-3;shift<=3;shift++)
        {
            float end=to+shift/3f;
            if(Mathf.Abs(end-from)>1f/6f+.00001f)continue;
            float length=0;Vector3 previous=SurfaceRoutePoint(from,end,shift,fromRegister,toRegister,0);
            for(int j=1;j<=40;j++){var point=SurfaceRoutePoint(from,end,shift,fromRegister,toRegister,j/40f);length+=Vector3.Distance(previous,point);previous=point;}
            if(length<bestLength){bestLength=length;bestShift=shift;}
        }
        result.Clear();
        for(int j=0;j<=40;j++)result.Add(transform.TransformPoint(SurfaceRoutePoint(from,to+bestShift/3f,bestShift,fromRegister,toRegister,j/40f)));
    }
    Vector3 SurfaceRoutePoint(float from,float to,int shift,float ra,float rb,float u)
    {
        int corner=(((-shift)%3)+3)%3;if(corner==2)corner=-1;
        float edge=corner*u;int side=Mathf.FloorToInt(edge);float t=Mathf.Lerp(from,to,u),register=Mathf.Lerp(ra,rb,u);
        return Vector3.Lerp(GetPointAt(t+side/3f,register),GetPointAt(t+(side+1)/3f,register),edge-side);
    }
    void Update()
    {
        foreach(var id in chordLineRenderers.Keys.Where(id=>chordLineRenderers[id].TailComplete).ToArray())
        {
            var chord=chordLineRenderers[id];chord.gameObject.SetActive(false);chordPool.Push(chord);chordLineRenderers.Remove(id);
        }
    }
    public void ClearVisualMemory()
    {
        tonalField?.ClearMemory();foreach(var note in notes)note.ClearTail();
        foreach(var chord in chordLineRenderers.Values)if(chord.Releasing)chord.ClearTail();
    }
    void OnApplicationFocus(bool focus) { if (!focus && Synth != null) Silence(); }
    void OnDestroy()
    {
        if (cameraControl != null) cameraControl.MovementUpdater -= UpdateText;

        foreach (var lr in fifthsLineRenderer) if (lr != null) Destroy(lr.sharedMaterial);

        if (chromaticLineRenderer != null) Destroy(chromaticLineRenderer.sharedMaterial);
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

    void UpdateLabelStyles()
    {
        for (int i = 0; i < noteTextLabels.Count; i++)
        {
            int rel = (i - visualKeyForRendering + Tones) % Tones;
            noteTextLabels[i].Text = PitchName(i) + (rel == 0 ? "  I" : rel == 5 ? "  IV" : rel == 7 ? "  V" : "");
            const float baseSize = 0.5f;

            if (rel == 0) // Key
            {
                noteTextLabels[i].Color = Color.blue;
                noteTextLabels[i].Size = baseSize * 4.0f;
            }
            else if (rel == 7) // Upper Fifth
            {
                noteTextLabels[i].Color = Color.green;
                noteTextLabels[i].Size = baseSize * 1.75f;
            }
            else if (rel == 5) // Lower Fifth
            {
                noteTextLabels[i].Color = Color.red;
                noteTextLabels[i].Size = baseSize * 1.75f;
            }
            else
            {
                noteTextLabels[i].Color = Color.white;
                noteTextLabels[i].Size = baseSize;
            }
        }
    }

    static LineRenderer NewLineRenderer(GameObject parent, Material fifthsMat, float LW, bool loop)
    {
        LineRenderer fifthsLineRenderer = parent.AddComponent<LineRenderer>();
        fifthsLineRenderer.sharedMaterial = fifthsMat;
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
