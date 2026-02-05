using UnityEngine;

public class Note : MonoBehaviour
{
    public float Hertz;
    public float CurrentAmp = 0;
    public float homeScale;
    private Material noteMaterial;
    private Renderer noteRenderer;
    private float phase;
    private float samplingFrequency;

    void Awake()
    {
        samplingFrequency = AudioSettings.outputSampleRate;
    }

    void Start()
    {
        // AudioSource managed in Update to conserve voices
    }

    public static Note Create(string name, float hertz, float scaleFactor)
    {
        GameObject container = new(name);
        Note newNote = container.AddComponent<Note>();
        newNote.Hertz = hertz;

        // Add AudioSource for procedural sound synthesis
        AudioSource audioSource = container.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
        audioSource.Stop(); 

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
        AudioSource audioSource = GetComponent<AudioSource>();

        // Scale note and manage audio state based on amplitude
        if (CurrentAmp > 0)
        {
            if (!audioSource.isPlaying) audioSource.Play();
            
            float scale = homeScale * (1f + CurrentAmp * 2f);
            transform.GetChild(0).localScale = Vector3.one * scale;
        }
        else
        {
            if (audioSource.isPlaying) audioSource.Stop();
            
            transform.GetChild(0).localScale = Vector3.one * homeScale;
        }
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        if (samplingFrequency <= 0 || CurrentAmp <= 0) return;

        float increment = Hertz * 2f * Mathf.PI / samplingFrequency;
        for (int i = 0; i < data.Length; i += channels)
        {
            // Synthesize a simple sine wave; volume scaled by CurrentAmp
            float value = Mathf.Sin(phase) * CurrentAmp * 0.05f;
            for (int j = 0; j < channels; j++)
            {
                data[i + j] = value;
            }
            phase += increment;
            if (phase > 2f * Mathf.PI) phase -= 2f * Mathf.PI;
        }
    }
}