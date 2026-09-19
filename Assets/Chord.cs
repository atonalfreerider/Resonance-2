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
    TonalDominance dominance;
    Color startHue,endHue;
    readonly VisualRelease release=new();
    float amp1,amp2,peak,releaseSeconds=2.4f,attackAge=10,releaseAge;
    public float AttackFlash=>Mathf.Exp(-attackAge*16);
    public void Strike()=>attackAge=0;
    public bool Releasing => !release.Held;
    public bool TailComplete => Releasing && release.Level<=0;
    public float VisualAmplitude => release.Level;
    public void Drive(float a,float b,Color start,Color end,float seconds)
    {
        if(!release.Held)Strike();
        releaseAge=0;
        amp1=a;amp2=b;peak=(a+b)*.5f;releaseSeconds=seconds;release.Set(peak);
        startHue=start;endHue=end;line.startColor=start;line.endColor=end;
    }
    public void Release(){if(release.Held){releaseAge=0;startHue=line.startColor;endHue=line.endColor;}release.Set(0);}
    public static Color ReleaseHue(Color color,float seconds)=>Color.Lerp(Color.gray*color.grayscale*.4f,color,Mathf.Exp(-Mathf.Max(0,seconds)*8));
    public static float ReleaseLight(float seconds)=>Mathf.Exp(-Mathf.Max(0,seconds)*4);
    public void Recolor(Color start,Color end){startHue=start;endHue=end;line.startColor=start;line.endColor=end;}
    public void ClearTail()=>release.Clear();
    public void Init(Note a, Note b, LineRenderer renderer)
    {
        Note1 = a; Note2 = b; line = renderer;
        dominance=GetComponentInParent<TonalDominance>();
        line.useWorldSpace = true;
        line.alignment = LineAlignment.View;
        line.widthCurve = AnimationCurve.Linear(0, 1, 1, 1);
        curved = false;
        release.Clear();attackAge=10;releaseAge=0;
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
        if(Releasing)releaseAge+=Time.unscaledDeltaTime;
        line.startColor=Releasing?ReleaseHue(startHue,releaseAge):dominance!=null?dominance.Blend(startHue,dominance.Energy):startHue;
        line.endColor=Releasing?ReleaseHue(endHue,releaseAge):dominance!=null?dominance.Blend(endHue,dominance.Energy):endHue;
        float flash=Releasing?0:AttackFlash;attackAge+=Time.unscaledDeltaTime;
        line.startColor=Color.Lerp(line.startColor,Color.white,flash);line.endColor=Color.Lerp(line.endColor,Color.white,flash);
        line.startWidth = (.004f + amp1 * .01f)*Mathf.Sqrt(fade)*(1+flash*.6f);
        line.endWidth = (.004f + amp2 * .01f)*Mathf.Sqrt(fade)*(1+flash*.6f);
        line.sharedMaterial.SetColor("_BaseColor",Color.white*((2+5*(amp1+amp2))*(1+flash*1.8f)*fade*(Releasing?ReleaseLight(releaseAge):1)));
        float amp = Main.ReducedMotion ? 0 : release.Level * amplitudeScale*3.2f*(Releasing?Mathf.Sqrt(fade):1);
        for (int i = 0; i < basis.Length; i++)
        {
            Vector3 direction = basis[Mathf.Min(i + 1, basis.Length - 1)] - basis[Mathf.Max(0, i - 1)];
            Vector3 perpendicular = Vector3.Cross(direction, Vector3.up).normalized;
            if(perpendicular.sqrMagnitude<.01f)perpendicular=Vector3.Cross(direction,Vector3.right).normalized;
            Vector3 second=Vector3.Cross(direction.normalized,perpendicular).normalized;
            float envelope = Mathf.Sin(Mathf.PI * i / (basis.Length - 1));
            float t=Time.time*waveFrequency,phase=i*.5f;
            float wave=Mathf.Sin(t+phase)+.38f*Mathf.Sin(t*1.63f-phase*1.8f);
            animated[i] = basis[i] + amp*envelope*(perpendicular*wave+second*(.45f*Mathf.Sin(t*1.21f-phase*.8f)));
        }
        line.SetPositions(animated);
    }
    void OnDestroy() { if (line != null && line.sharedMaterial != null) Destroy(line.sharedMaterial); }
}
