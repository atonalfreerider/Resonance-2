using UnityEngine;

public class Note : MonoBehaviour
{
    public float Hertz, CurrentAmp, homeScale;
    public int Index;
    Material material;
    Transform sphere;
    readonly VisualRelease release=new();
    Color hue=Color.white;
    TonalDominance dominance;
    bool showIdle=true;
    float releaseSeconds=2.4f;
    float attack,previousAmp;
    public void Strike(float velocity){attack=Mathf.Max(attack,Mathf.Clamp01(velocity));}
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
    public void ClearTail(){release.Clear();attack=0;previousAmp=0;}
    void LateUpdate()
    {
        if(sphere==null)return;
        release.Set(CurrentAmp);release.Advance(Time.unscaledDeltaTime,releaseSeconds);
        float glow=release.Level;
        if(dominance==null)dominance=GetComponentInParent<TonalDominance>();
        sphere.localScale=Vector3.one*homeScale*((showIdle?1:0)+2*Mathf.Sqrt(glow)+2.8f*attack);
        Color activeHue=dominance!=null?dominance.Blend(hue,dominance.Energy):hue;
        material.SetColor("_BaseColor",hue*(showIdle?.22f:0)+Color.Lerp(activeHue,Color.white,attack*.9f)*(10*glow+12*attack));
        attack*=Mathf.Exp(-Time.unscaledDeltaTime*13);
        sphere.gameObject.SetActive(showIdle || glow>0 || attack>.005f);
    }
    void OnDestroy() { if (material != null) Destroy(material); }
}
