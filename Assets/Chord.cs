using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class Chord : MonoBehaviour
{
    public Note Note1;
    public Note Note2;
    LineRenderer lineRenderer;

    // Parameters for wave animation
    [Header("Wave Settings")] public int segmentCount = 220; // Number of segments for straight chords
    public float waveFrequency = 60f; // Frequency of the wave oscillation
    public float amplitudeScale = 0.02f; // Scales the overall wave amplitude
    public AnimationCurve thicknessCurve; // Controls thickness from start to end

    List<Vector3> basePositions; // The base positions of the chord line (either generated or from fifthLine)
    bool isFifthLine = false;

    // Initialization
    public void Init(Note note1, Note note2, LineRenderer lr)
    {
        Note1 = note1;
        Note2 = note2;
        lineRenderer = lr;

        // Ensure the line renderer is set up
        lineRenderer.useWorldSpace = true;
        lineRenderer.alignment = LineAlignment.View;
        thicknessCurve = new AnimationCurve(
            new Keyframe(0, Note1.CurrentAmp * 0.01f), // start thickness
            new Keyframe(1, Note2.CurrentAmp * 0.01f) // end thickness
        );
        lineRenderer.widthCurve = thicknessCurve;
        // You can also set a gradient or color if needed:
        // lineRenderer.colorGradient = ...

        // By default, if no fifth line is set, we assume a straight line
        isFifthLine = false;
        GenerateBaseLinePositions();
    }

    // Called for a curved line (fifth)
    public void Fifth(List<Vector3> fifthLine)
    {
        if (lineRenderer == null)
            lineRenderer = GetComponent<LineRenderer>();

        lineRenderer.positionCount = fifthLine.Count;
        basePositions = new List<Vector3>(fifthLine);
        isFifthLine = true;

        // Set initial positions (no wave offset yet)
        lineRenderer.SetPositions(basePositions.ToArray());
    }

    void Update()
    {
        AnimateWave();
    }

    /// <summary>
    /// Generates a simple straight line between Note1 and Note2 when no fifth line is provided.
    /// </summary>
    void GenerateBaseLinePositions()
    {
        Vector3 startPos = Note1.transform.position;
        Vector3 endPos = Note2.transform.position;

        basePositions = new List<Vector3>(segmentCount + 1);
        for (int i = 0; i <= segmentCount; i++)
        {
            float t = i / (float)segmentCount;
            Vector3 pos = Vector3.Lerp(startPos, endPos, t);
            basePositions.Add(pos);
        }

        lineRenderer.positionCount = basePositions.Count;
        lineRenderer.SetPositions(basePositions.ToArray());
    }

    /// <summary>
    /// Animates the wave along the line.
    /// </summary>
    void AnimateWave()
    {
        if (basePositions == null || basePositions.Count < 2)
            return;

        // Compute combined amplitude factor
        float combinedAmp = (Note1.CurrentAmp + Note2.CurrentAmp) * 0.5f;
        float maxWaveAmplitude = combinedAmp * amplitudeScale * (isFifthLine ? 3: 1);

        // Update thickness based on note amplitudes
        // We'll create a widthCurve dynamically if one isn't assigned.
        if (thicknessCurve == null || thicknessCurve.length == 0)
        {
            thicknessCurve = new AnimationCurve(
                new Keyframe(0, Note1.CurrentAmp * 0.01f), // start thickness
                new Keyframe(1, Note2.CurrentAmp * 0.01f) // end thickness
            );
            lineRenderer.widthCurve = thicknessCurve;
        }
        else
        {
            // If a curve is assigned, we could also modify it dynamically, but that might be less common.
            // Alternatively, set startWidth/endWidth directly:
            lineRenderer.startWidth = Note1.CurrentAmp * 0.01f;
            lineRenderer.endWidth = Note2.CurrentAmp * 0.01f;
        }

        // We'll apply a sine wave offset to each intermediate point.
        // First and last points remain unchanged (standing nodes).
        Vector3[] updatedPositions = new Vector3[basePositions.Count];
        updatedPositions[0] = basePositions[0];
        updatedPositions[^1] = basePositions[updatedPositions.Length - 1];

        // Calculate a direction/perpendicular vector for the line
        // If we have a curved line, direction is not uniform, so we handle that per segment.
        for (int i = 1; i < basePositions.Count - 1; i++)
        {
            float t = i / (float)(basePositions.Count - 1);

            // Find local direction for curved or straight line:
            Vector3 forwardDir;
            if (!isFifthLine)
            {
                // For a straight line, direction is simply end-start:
                forwardDir = (basePositions[^1] - basePositions[0]).normalized;
            }
            else
            {
                // For a curved line, approximate direction using neighboring points
                Vector3 prev = basePositions[Mathf.Max(i - 1, 0)];
                Vector3 next = basePositions[Mathf.Min(i + 1, basePositions.Count - 1)];
                forwardDir = (next - prev).normalized;
            }

            // Find a perpendicular vector to forwardDir. 
            // We'll try using Vector3.Cross with an arbitrary up vector.
            Vector3 perpendicular = Vector3.Cross(forwardDir, Vector3.up);
            if (perpendicular.sqrMagnitude < 0.0001f)
            {
                // If forward is too close to up, pick another axis:
                perpendicular = Vector3.Cross(forwardDir, Vector3.right);
            }

            perpendicular.Normalize();

            // Amplitude factor that makes wave zero at ends and max in the middle
            // Using a sine that goes from 0 to pi: sin(pi*t), t in [0..1].
            // At t=0 or t=1 => sin(0)=0, sin(pi)=0, at t=0.5 => sin(pi/2)=1.
            float envelope = Mathf.Sin(Mathf.PI * t);

            // Wave oscillation over time
            float wave = Mathf.Sin(Time.time * waveFrequency + i * 0.5f);

            // Final offset
            Vector3 offset = perpendicular * (maxWaveAmplitude * envelope * wave);

            // Apply offset to the base position
            updatedPositions[i] = basePositions[i] + offset;
        }

        // Update line positions
        lineRenderer.SetPositions(updatedPositions);
    }
}