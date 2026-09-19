using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

// Unity consumes completed preprocessing artifacts; it never estimates a timing warp.
public sealed class SongAudio : MonoBehaviour
{
    [Serializable] public sealed class Prepared
    {
        public int version;
        public string audioPath,midiPath,reportPath,sourceAudioPath,sourceAudioSha256,audioSha256,midiSha256,status;
        public double audioDuration,featureResolutionMs;
    }
    [Serializable] sealed class Sections { public int bars=8; public string boundaries=""; }
    public AudioSource Source {get;private set;}
    public SongAlignment Alignment {get;private set;}
    public bool Ready=>Source!=null&&Source.clip!=null;
    public bool Busy {get;private set;}
    public string Status="Load a recording and its offline-preprocessed MIDI.";
    public string AudioPath="";
    public string ReportPath {get;private set;}
    public string RecordingName {get;private set;}
    MidiPlayer midi;Main main;int generation;
    TextField audioField,midiField;Label status;
    string Library=>Path.Combine(Application.persistentDataPath,"Songs");
    string SectionPath=>Path.Combine(Library,HashText(midi.midiPath)+".sections.json");
    static string HashText(string text){using var hash=System.Security.Cryptography.SHA256.Create();return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-","").ToLowerInvariant();}
    static string HashFile(string path){using var hash=System.Security.Cryptography.SHA256.Create();using var stream=File.OpenRead(path);return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
    public static Prepared ValidatePrepared(string selectedAudio,string score,out string canonicalAudio,out string report)
    {
        string sidecar=score+".prepared.json";
        if(!File.Exists(sidecar))throw new ArgumentException("Run Tools/SongPrep/prepare_song.py first, then select its aligned.mid. Alignment happens before Unity.");
        var manifest=JsonUtility.FromJson<Prepared>(File.ReadAllText(sidecar));
        if(manifest==null||manifest.version!=1)throw new ArgumentException("Unsupported prepared-song manifest.");
        string directory=Path.GetDirectoryName(Path.GetFullPath(sidecar));
        canonicalAudio=Path.GetFullPath(Path.Combine(directory,manifest.audioPath));
        report=Path.GetFullPath(Path.Combine(directory,manifest.reportPath));
        if(!File.Exists(canonicalAudio)||HashFile(canonicalAudio)!=manifest.audioSha256||HashFile(score)!=manifest.midiSha256)
            throw new ArgumentException("Prepared files changed or are missing. Rerun offline preprocessing.");
        if(!string.IsNullOrWhiteSpace(selectedAudio))
        {
            string selectedHash=HashFile(selectedAudio);
            if(selectedHash!=manifest.sourceAudioSha256&&selectedHash!=manifest.audioSha256)
                throw new ArgumentException("This audio does not match the preprocessed MIDI fingerprint.");
        }
        return manifest;
    }
    void Awake()
    {
        midi=GetComponent<MidiPlayer>();main=GetComponent<Main>();
        var go=new GameObject("Recording audio (master clock)");go.transform.SetParent(transform,false);
        Source=go.AddComponent<AudioSource>();Source.playOnAwake=false;Source.spatialBlend=0;
    }
    public void Unload()
    {
        generation++;Busy=false;Source.Stop();if(Source.clip!=null)Destroy(Source.clip);
        Source.clip=null;Alignment=null;ReportPath=null;RecordingName=null;AudioPath="";
        if(main.Synth!=null)main.Synth.GetComponent<AudioSource>().mute=false;
    }
    IEnumerator Start()
    {
        yield return null;
        string audio=PlayerPrefs.GetString("Resonance.LastAudio"),score=PlayerPrefs.GetString("Resonance.LastMidi");
        if(!Busy&&!Ready&&File.Exists(score))LoadPair(audio,score);
    }
    public void LoadPair(string audio,string score){if(!Busy)StartCoroutine(Load(audio,score));}
    IEnumerator Load(string audio,string score)
    {
        audio=audio.Trim().Trim('"');score=score.Trim().Trim('"');
        string canonical,report;Prepared manifest;
        try{manifest=ValidatePrepared(audio,score,out canonical,out report);}
        catch(Exception e){Status=e.Message;yield break;}
        if(!midi.Load(score)){Status=midi.Status;yield break;}
        Busy=true;int version=generation;Status="Loading fingerprint-aligned song…";AudioPath=canonical;RecordingName=Path.GetFileName(manifest.sourceAudioPath);ReportPath=report;midi.playbackSpeed=1;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        string decodePath=canonical;
        var decode=Task.Run(()=>
        {
            using var reader=new NAudio.Wave.AudioFileReader(decodePath);
            var buffer=new float[16384];var pcm=new System.Collections.Generic.List<float>();int read;
            while((read=reader.Read(buffer,0,buffer.Length))>0)for(int i=0;i<read;i++)pcm.Add(buffer[i]);
            return (data:pcm.ToArray(),channels:reader.WaveFormat.Channels,rate:reader.WaveFormat.SampleRate);
        });
        while(!decode.IsCompleted){if(version!=generation)yield break;yield return null;}
        if(version!=generation)yield break;
        if(decode.IsFaulted){Busy=false;Status="Audio decode failed: "+decode.Exception.GetBaseException().Message;yield break;}
        var decoded=decode.Result;
        Source.clip=AudioClip.Create(Path.GetFileName(canonical),decoded.data.Length/decoded.channels,decoded.channels,decoded.rate,false);
        Source.clip.SetData(decoded.data,0);
#else
        using(var request=UnityWebRequestMultimedia.GetAudioClip(new Uri(canonical).AbsoluteUri,AudioType.WAV))
        {
            yield return request.SendWebRequest();if(version!=generation)yield break;
            if(request.result!=UnityWebRequest.Result.Success){Busy=false;Status="Audio import: "+request.error;yield break;}
            Source.clip=DownloadHandlerAudioClip.GetContent(request);
        }
#endif
        Alignment=SongAlignment.Estimate(Source.clip.length,Source.clip.length);
        Alignment.method="Timing baked into preprocessed MIDI (identity playback)";
        try
        {
            Status=$"Prepared song · {manifest.featureResolutionMs:0} ms fingerprint grid\n{manifest.status}\nRecording is the sole audio source; no runtime timing warp.";
        }
        catch(Exception e){Status="Song loaded; section map: "+e.Message;}
        Busy=false;midi.Seek(0);
        audioField?.SetValueWithoutNotify(AudioPath);midiField?.SetValueWithoutNotify(score);
        PlayerPrefs.SetString("Resonance.LastAudio",AudioPath);PlayerPrefs.SetString("Resonance.LastMidi",score);PlayerPrefs.Save();
    }
    public void Save(bool unused)
    {
        if(!Ready)return;
        try{Directory.CreateDirectory(Library);File.WriteAllText(SectionPath,JsonUtility.ToJson(new Sections{bars=midi.SectionBars,boundaries=midi.SectionBoundaries},true));Status="Section map saved. MIDI timing remains baked offline.";}
        catch(Exception e){Status="Save failed: "+e.Message;}
    }
    public VisualElement BuildUI()
    {
        var box=new Foldout{text="SONG · preprocessed recording + MIDI",value=true};
        string libraryPath=Path.GetFullPath(Path.Combine(Application.dataPath,"../PreparedSongs"));
        var songs=Directory.Exists(libraryPath)?new System.Collections.Generic.List<string>(Directory.GetFiles(libraryPath,"*.patterns.json",SearchOption.AllDirectories)):new System.Collections.Generic.List<string>();
        // Prefer a completed recording bundle over its duplicate restored-score entry.
        songs=songs.Where(s=>!s.Contains(Path.DirectorySeparatorChar+"Recordings"+Path.DirectorySeparatorChar)||File.Exists(s.Substring(0,s.Length-".patterns.json".Length)+".prepared.json"))
            .GroupBy(s=>Path.GetFileName(Path.GetDirectoryName(s)),StringComparer.OrdinalIgnoreCase)
            .Select(g=>g.OrderByDescending(s=>File.Exists(s.Substring(0,s.Length-".patterns.json".Length)+".prepared.json")).First()).ToList();
        songs.Sort(StringComparer.OrdinalIgnoreCase);
        var choices=new System.Collections.Generic.List<string>();foreach(var song in songs){string score=song.Substring(0,song.Length-".patterns.json".Length);choices.Add(Path.GetFileName(Path.GetDirectoryName(score))+(File.Exists(score+".prepared.json")?" · recording":" · restored score"));}
        if(songs.Count>0){string last=PlayerPrefs.GetString("Resonance.LastMidi").Replace('/',Path.DirectorySeparatorChar)+".patterns.json";int selected=Math.Max(0,songs.FindIndex(s=>string.Equals(s,last,StringComparison.OrdinalIgnoreCase)));var library=new DropdownField("Prepared library",choices,selected);box.Add(library);
            box.Add(new Button(()=>{string score=songs[library.index];score=score.Substring(0,score.Length-".patterns.json".Length);if(File.Exists(score+".prepared.json"))LoadPair("",score);else {midi.Load(score);midiField.SetValueWithoutNotify(score);Status="Restored score preview. A recording requires offline fingerprint preparation.";}}){text="Load selected pattern bundle"});}
        var files=new Foldout{text="Load companion files",value=false};box.Add(files);
        var hint=new Label("Prepare the song outside Unity, then load aligned.mid. Its manifest selects the exact decoded recording.");hint.style.whiteSpace=WhiteSpace.Normal;files.Add(hint);
        audioField=new TextField("Original MP3 / prepared WAV"){value=PlayerPrefs.GetString("Resonance.LastAudio","")};
        midiField=new TextField("Preprocessed MIDI"){value=PlayerPrefs.GetString("Resonance.LastMidi",midi.midiPath)};
        files.Add(audioField);files.Add(midiField);
        var row=new VisualElement();row.AddToClassList("row");files.Add(row);
        row.Add(new Button(()=>Browse(audioField,"mp3,wav")){text="Choose audio…"});row.Add(new Button(()=>Browse(midiField,"mid,midi")){text="Choose MIDI…"});
        files.Add(new Button(()=>LoadPair(audioField.value,midiField.value)){text="Load preprocessed song"});
        box.Add(new Button(()=>{if(!string.IsNullOrEmpty(ReportPath)&&File.Exists(ReportPath))Application.OpenURL(new Uri(ReportPath).AbsoluteUri);}){text="Open offline fingerprint report"});
        var transport=new VisualElement();transport.AddToClassList("row");box.Add(transport);
        transport.Add(new Button(()=>{if(midi.IsPlaying)midi.Pause();else midi.Play();}){text="Play / pause"});transport.Add(new Button(midi.Stop){text="Stop"});
        var position=new Slider("Recording position",0,1);position.RegisterValueChangedCallback(e=>{if(midi.Loaded)midi.Seek(e.newValue*midi.Duration);});box.Add(position);
        var clock=new Label();box.Add(clock);var recordingLabel=new Label(){name="linked-recording"};recordingLabel.style.whiteSpace=WhiteSpace.Normal;box.Add(recordingLabel);
        box.schedule.Execute(()=>{position.SetValueWithoutNotify(midi.Duration>0?(float)(midi.Position/midi.Duration):0);clock.text=$"{midi.Position:0.00}s / {midi.Duration:0.00}s";recordingLabel.text=Ready?$"{(midi.IsPlaying?"Playing recording":"Linked recording")}: {RecordingName}\nWAV audio · MIDI drives visualization":Busy?"Loading recording…":"MIDI preview · no recording linked";}).Every(100);
        status=new Label(Status);status.style.whiteSpace=WhiteSpace.Normal;box.Add(status);
        return box;
    }
    static void Browse(TextField field,string extensions)
    {
#if UNITY_EDITOR
        string p=UnityEditor.EditorUtility.OpenFilePanel("Choose companion file","",extensions);if(!string.IsNullOrEmpty(p))field.value=p;
#else
        ExplorerInputFocus.ClaimUI();field.Focus();
#endif
    }
    void Update(){if(status!=null)status.text=Status;if(main.Synth!=null){Source.volume=main.Synth.Volume;Source.pitch=1;main.Synth.GetComponent<AudioSource>().mute=Ready&&midi.IsPlaying;}}
    void OnDestroy(){generation++;if(Source!=null&&Source.clip!=null)Destroy(Source.clip);}
}
