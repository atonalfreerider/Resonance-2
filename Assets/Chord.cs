using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class Chord : MonoBehaviour
{
    public Note Note1, Note2;
    public int segmentCount = 48;
    public float waveFrequency = 12f, amplitudeScale = .012f;
    LineRenderer line;
    Vector3[] basis, animated;
    bool curved;
    public void Init(Note a, Note b, LineRenderer renderer)
    {
        Note1 = a; Note2 = b; line = renderer;
        line.useWorldSpace = true;
        line.alignment = LineAlignment.View;
        line.widthCurve = AnimationCurve.Linear(0, 1, 1, 1);
        curved = false;
        Resize(segmentCount + 1);
    }
    void Resize(int count)
    {
        if (basis == null || basis.Length != count) { basis = new Vector3[count]; animated = new Vector3[count]; }
        line.positionCount = count;
    }
    public void Fifth(List<Vector3> points)
    {
        Resize(points.Count); points.CopyTo(basis); curved = true;
    }
    void LateUpdate()
    {
        if (Note1 == null || Note2 == null || basis == null) return;
        if (!curved) for (int i = 0; i < basis.Length; i++)
            basis[i] = Vector3.Lerp(Note1.transform.position, Note2.transform.position, i / (float)(basis.Length - 1));
        line.startWidth = .004f + Note1.CurrentAmp * .01f;
        line.endWidth = .004f + Note2.CurrentAmp * .01f;
        float amp = Main.ReducedMotion ? 0 : (Note1.CurrentAmp + Note2.CurrentAmp) * .5f * amplitudeScale;
        for (int i = 0; i < basis.Length; i++)
        {
            Vector3 direction = basis[Mathf.Min(i + 1, basis.Length - 1)] - basis[Mathf.Max(0, i - 1)];
            Vector3 perpendicular = Vector3.Cross(direction, Vector3.up).normalized;
            float envelope = Mathf.Sin(Mathf.PI * i / (basis.Length - 1));
            animated[i] = basis[i] + perpendicular * (amp * envelope * Mathf.Sin(Time.time * waveFrequency + i * .5f));
        }
        line.SetPositions(animated);
    }
    void OnDestroy() { if (line != null && line.sharedMaterial != null) Destroy(line.sharedMaterial); }
}
