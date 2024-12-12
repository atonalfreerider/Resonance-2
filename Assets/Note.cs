using UnityEngine;

public class Note : MonoBehaviour
{
    public float Hertz;
    public float CurrentAmp = 0;
    public float homeScale;

    public static Note Create(string name, float hertz, float scaleFactor)
    {
        GameObject container = new(name);
        Note newNote = container.AddComponent<Note>();
        newNote.Hertz = hertz;
        GameObject pointGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        pointGo.transform.SetParent(container.transform, false);
        pointGo.transform.localScale = Vector3.one * scaleFactor;
        newNote.homeScale = scaleFactor;
        
        return newNote;
    }
}