using UnityEngine;

public class Note : MonoBehaviour
{
    public float Hertz, CurrentAmp, homeScale;
    public int Index;
    public bool Featured;
    bool whiteTail;
    Material material;
    Transform sphere;
    readonly VisualRelease release=new();
    Color hue=Color.white;
    TonalDominance dominance;
    bool showIdle=true;
    float releaseSeconds=2.4f;
    float attack,attackWhite,previousAmp;
    public void Strike(float velocity){attack=Mathf.Max(attack,Mathf.Clamp01(velocity));attackWhite=1;}
    public float VisualAmplitude => release.Level;
    public float VisualScale => sphere==null?0:sphere.localScale.x;
    public static Note Create(string name, float hertz, float scaleFactor)
    {
        var go = new GameObject(name);
        var note = go.AddComponent<Note>();
        note.Hertz = hertz; note.homeScale = scaleFactor;
        var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        visual.transform.SetParent(go.transform, false);
        note.sphere = visual.transform;
        note.sphere.localScale = Vector3.one * scaleFactor;
        note.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        visual.GetComponent<Renderer>().sharedMaterial = note.material;
        return note;
    }
    public void Configure(Color color,bool idle,float seconds)
    { hue=color;showIdle=idle;releaseSeconds=seconds;release.Set(CurrentAmp);
      if(CurrentAmp>previousAmp+.08f)Strike(CurrentAmp);previousAmp=CurrentAmp; }
    public void ClearTail(){release.Clear();attack=attackWhite=0;previousAmp=0;}
    void LateUpdate()
    {
        if(sphere==null)return;
        release.Set(CurrentAmp);release.Advance(Time.unscaledDeltaTime,releaseSeconds);
        float glow=release.Level;
        if(dominance==null)dominance=GetComponentInParent<TonalDominance>();
        sphere.localScale=Vector3.one*homeScale*((showIdle?1:0)+2*Mathf.Sqrt(glow)+2.8f*attack)*(Featured?1.35f+.65f*attack:1);
        if(CurrentAmp>0)whiteTail=Featured;else if(glow<=0)whiteTail=false;
        Color activeHue=whiteTail?Color.white:dominance!=null?dominance.Blend(hue,dominance.Energy):hue;
        // Half of the previous attack emission; sustained chord colors are untouched.
        material.SetColor("_BaseColor",hue*(showIdle?.22f:0)+Color.Lerp(activeHue,Color.white,attackWhite)*(10*glow*(1-.5f*attackWhite)+6*attack)*(Featured?1.6f+attack:1));
        attack*=Mathf.Exp(-Time.unscaledDeltaTime*13);
        attackWhite*=Mathf.Exp(-Time.unscaledDeltaTime*13);
        sphere.gameObject.SetActive(showIdle || glow>0 || attack>.005f);
    }
    void OnDestroy() { if (material != null) Destroy(material); }
}
