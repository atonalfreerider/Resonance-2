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
    readonly VisualRelease release=new();
    float amp1,amp2,peak,releaseSeconds=2.4f;
    public bool Releasing => !release.Held;
    public bool TailComplete => Releasing && release.Level<=0;
    public float VisualAmplitude => release.Level;
    public void Drive(float a,float b,Color start,Color end,float seconds)
    {
        amp1=a;amp2=b;peak=(a+b)*.5f;releaseSeconds=seconds;release.Set(peak);
        line.startColor=start;line.endColor=end;
    }
    public void Release()=>release.Set(0);
    public void Recolor(Color start,Color end){line.startColor=start;line.endColor=end;}
    public void ClearTail()=>release.Clear();
    public void Init(Note a, Note b, LineRenderer renderer)
    {
        Note1 = a; Note2 = b; line = renderer;
        line.useWorldSpace = true;
        line.alignment = LineAlignment.View;
        line.widthCurve = AnimationCurve.Linear(0, 1, 1, 1);
        curved = false;
        release.Clear();
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
        release.Advance(Time.unscaledDeltaTime,releaseSeconds);
        if (!curved) for (int i = 0; i < basis.Length; i++)
            basis[i] = Vector3.Lerp(Note1.transform.position, Note2.transform.position, i / (float)(basis.Length - 1));
        float fade=peak>0?release.Level/peak:0;
        line.startWidth = (.004f + amp1 * .01f)*Mathf.Sqrt(fade);
        line.endWidth = (.004f + amp2 * .01f)*Mathf.Sqrt(fade);
        line.sharedMaterial.SetColor("_BaseColor",Color.white*((2+5*(amp1+amp2))*fade));
        float amp = Main.ReducedMotion ? 0 : release.Level * amplitudeScale*(Releasing?fade:1);
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
