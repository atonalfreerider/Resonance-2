using UnityEngine;

public class Note : MonoBehaviour
{
    public float Hertz;
    public float CurrentAmp = 0;
    public float homeScale;
    private Material noteMaterial;
    private Renderer noteRenderer;

    public static Note Create(string name, float hertz, float scaleFactor)
    {
        GameObject container = new(name);
        Note newNote = container.AddComponent<Note>();
        newNote.Hertz = hertz;
        GameObject pointGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        pointGo.transform.SetParent(container.transform, false);
        pointGo.transform.localScale = Vector3.one * scaleFactor;
        newNote.homeScale = scaleFactor;
        
        // Store renderer reference and create unique material
        newNote.noteRenderer = pointGo.GetComponent<Renderer>();
        // Try different shaders in order of preference
        Shader targetShader = Shader.Find("Unlit/Color");
                
        newNote.noteMaterial = new Material(targetShader);
        newNote.noteMaterial.color = Color.white;
        newNote.noteRenderer.material = newNote.noteMaterial;
        
        return newNote;
    }

    public void SetColor(Color color)
    {
        if (noteMaterial != null)
        {
            noteMaterial.color = color;
        }
    }

    void Update()
    {
        // Scale note based on amplitude
        if (CurrentAmp > 0)
        {
            float scale = homeScale * (1f + CurrentAmp * 2f);
            transform.GetChild(0).localScale = Vector3.one * scale;
        }
        else
        {
            transform.GetChild(0).localScale = Vector3.one * homeScale;
        }
    }
}