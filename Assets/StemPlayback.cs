using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

// All separation and transcription are offline. Solo swaps a sample-aligned recording and score.
public sealed class StemPlayback : MonoBehaviour
{
    [Serializable] public sealed class Stem
    {
        public string id,name,audioPath,audioSha256,midiPath,midiSha256,patternsPath,patternsSha256,method;
        public int samples,sampleRate,notes;
    }
    public Stem[] Stems {get;private set;}=Array.Empty<Stem>();
    public bool IsLoading {get;private set;}
    public string SelectedId {get;private set;}="";
    public string Status {get;private set;}="No separated stems in this bundle.";
    AudioClip masterClip,soloClip;string directory;int generation;
    MidiPlayer midi;SongAudio recording;DropdownField selector;Label label;
    void Awake(){midi=GetComponent<MidiPlayer>();recording=GetComponent<SongAudio>();}
    public void Configure(Stem[] stems,string folder)
    {
        Stems=stems??Array.Empty<Stem>();directory=folder;masterClip=recording.Source.clip;SelectedId="";
        Status=Stems.Length>0?"Full mix · chord colors follow the complete song.":"No stems yet · use Add stems in Song Workshop.";
        RefreshUI();
    }
    public void Clear()
    {
        generation++;IsLoading=false;
        if(recording!=null&&recording.Source!=null&&masterClip!=null)recording.Source.clip=masterClip;
        if(soloClip!=null)Destroy(soloClip);soloClip=null;masterClip=null;Stems=Array.Empty<Stem>();SelectedId="";
        RefreshUI();
    }
    static string Hash(string path){using var h=System.Security.Cryptography.SHA256.Create();using var s=File.OpenRead(path);return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
    string Verified(string path,string hash)
    {
        string full=Path.GetFullPath(Path.Combine(directory,path));
        if(!full.StartsWith(Path.GetFullPath(directory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Hash(full)!=hash)
            throw new InvalidDataException("Stem file is missing, outside its bundle, or changed. Rebuild the stem bundle.");
        return full;
    }
    public void Select(string id)
    {
        if(IsLoading||!recording.Ready||recording.Busy)return;
        if(id!=""&&!Stems.Any(s=>s.id==id))return;
        StartCoroutine(Load(id));
    }
    IEnumerator Load(string id)
    {
        bool resume=midi.IsPlaying;double position=midi.Position;midi.Pause();IsLoading=true;int version=++generation;
        PreparedPatternSong data=null;AudioClip clip=masterClip;string error=null;
        if(id!=""){
            var stem=Stems.First(s=>s.id==id);Status="Loading "+stem.name+"…";
            var task=Task.Run(()=>{
                string audio=Verified(stem.audioPath,stem.audioSha256);Verified(stem.midiPath,stem.midiSha256);
                string analysis=File.ReadAllText(Verified(stem.patternsPath,stem.patternsSha256));
                using var reader=new NAudio.Wave.AudioFileReader(audio);
                var buffer=new float[16384];var pcm=new System.Collections.Generic.List<float>();int read;
                while((read=reader.Read(buffer,0,buffer.Length))>0)for(int i=0;i<read;i++)pcm.Add(buffer[i]);
                return (pcm:pcm.ToArray(),channels:reader.WaveFormat.Channels,rate:reader.WaveFormat.SampleRate,analysis);
            });
            while(!task.IsCompleted){if(version!=generation)yield break;yield return null;}
            if(version!=generation)yield break;
            if(task.IsFaulted)error=task.Exception.GetBaseException().Message;
            else try{
                var result=task.Result;
                if(result.rate!=masterClip.frequency||result.pcm.Length/result.channels!=masterClip.samples)
                    throw new InvalidDataException("Stem and recording sample clocks differ.");
                data=JsonUtility.FromJson<PreparedPatternSong>(result.analysis);
                if(data.Version!=1||data.MidiSha256!=stem.midiSha256||data.Frames==null||data.Form==null)
                    throw new InvalidDataException("Invalid stem analysis.");
                clip=AudioClip.Create(stem.name,masterClip.samples,result.channels,result.rate,false);clip.SetData(result.pcm,0);
            }catch(Exception e){error=e.Message;}
        }
        if(error==null){
            recording.Source.Stop();recording.Source.clip=clip;
            if(soloClip!=null)Destroy(soloClip);soloClip=id==""?null:clip;
            SelectedId=id;midi.SetVisualPrepared(data);
            Status=id==""?"Full mix · original recording":"Solo: "+Stems.First(s=>s.id==id).name+" · full-song chord colors";
        }else Status="Stem load failed: "+error;
        IsLoading=false;midi.Seek(position);if(resume)midi.Play();RefreshUI();
    }
    public VisualElement BuildUI()
    {
        var box=new VisualElement{name="stem-controls"};box.style.marginTop=8;box.style.marginBottom=8;
        var heading=new Label("SOLO AUDIO + VISUALS");heading.AddToClassList("section");box.Add(heading);
        var hint=new Label("Solo the audio, notes and pattern wheels together. Full-song chord colors stay visible.");hint.style.whiteSpace=WhiteSpace.Normal;box.Add(hint);
        selector=new DropdownField("Instrument stem",new System.Collections.Generic.List<string>{"Full mix"},0){name="stem-selector"};
        selector.RegisterValueChangedCallback(_=>{int index=selector.index;if(index>=0)Select(index==0?"":Stems[index-1].id);});box.Add(selector);
        label=new Label();label.style.whiteSpace=WhiteSpace.Normal;box.Add(label);
        var restore=new Button(()=>Select("")){text="Restore full mix",name="restore-full-mix"};box.Add(restore);
        box.schedule.Execute(()=>{selector.SetEnabled(!IsLoading&&Stems.Length>0);restore.SetEnabled(!IsLoading&&SelectedId!="");label.text=Status;}).Every(100);
        RefreshUI();return box;
    }
    void RefreshUI(){if(selector==null)return;selector.choices=new[]{"Full mix"}.Concat(Stems.Select(s=>s.name)).ToList();selector.SetValueWithoutNotify(selector.choices[Math.Max(0,Array.FindIndex(Stems,s=>s.id==SelectedId)+1)]);}
    void OnDestroy(){if(soloClip!=null)Destroy(soloClip);if(masterClip!=null)Destroy(masterClip);generation++;}
}
