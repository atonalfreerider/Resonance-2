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
    { hue=color;showIdle=idle;releaseSeconds=seconds;release.Set(CurrentAmp); }
    public void ClearTail(){release.Clear();}
    void LateUpdate()
    {
        if(sphere==null)return;
        release.Set(CurrentAmp);release.Advance(Time.unscaledDeltaTime,releaseSeconds);
        float glow=release.Level;
        if(dominance==null)dominance=GetComponentInParent<TonalDominance>();
        sphere.localScale=Vector3.one*homeScale*((showIdle?1:0)+2*Mathf.Sqrt(glow));
        Color activeHue=dominance!=null?dominance.Blend(hue,dominance.Energy):hue;
        material.SetColor("_BaseColor",hue*(showIdle?.22f:0)+activeHue*10*glow);
        sphere.gameObject.SetActive(showIdle || glow>0);
    }
    void OnDestroy() { if (material != null) Destroy(material); }
}
