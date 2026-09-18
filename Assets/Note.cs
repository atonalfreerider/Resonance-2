using UnityEngine;

public class Note : MonoBehaviour
{
    public float Hertz, CurrentAmp, homeScale;
    public int Index;
    Material material;
    Transform sphere;
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
    public void SetColor(Color color) { material.SetColor("_BaseColor", color); }
    void Update() { sphere.localScale = Vector3.one * homeScale * (1 + CurrentAmp * 2); }
    void OnDestroy() { if (material != null) Destroy(material); }
}
