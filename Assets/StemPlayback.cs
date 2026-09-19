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
    sealed class CachedStem { public AudioSource Source;public PreparedPatternSong Score;public float Gain; }
    readonly System.Collections.Generic.Dictionary<string,CachedStem> cache=new();
    public bool ReadyToSolo=>!IsLoading&&Stems.Length>0&&cache.Count==Stems.Length;
    public float MasterGain {get;private set;}=1;
    public AudioSource AudibleSource=>cache.TryGetValue(SelectedId,out var value)?value.Source:recording.Source;
    public int CachedCount=>cache.Count;
    string directory;int generation;
    MidiPlayer midi;SongAudio recording;DropdownField selector;Label label;
    void Awake(){midi=GetComponent<MidiPlayer>();recording=GetComponent<SongAudio>();}
    public IEnumerator Preload(Stem[] stems,string folder)
    {
        Stems=stems??Array.Empty<Stem>();directory=folder;SelectedId="";MasterGain=1;IsLoading=true;int version=++generation;
        foreach(var stem in Stems){
            Status="Preparing instant solo: "+stem.name+"…";RefreshUI();
            var task=Task.Run(()=>{
                string audio=Verified(stem.audioPath,stem.audioSha256);Verified(stem.midiPath,stem.midiSha256);
                string analysis=File.ReadAllText(Verified(stem.patternsPath,stem.patternsSha256));
                using var reader=new NAudio.Wave.AudioFileReader(audio);
                int channels=reader.WaveFormat.Channels,rate=reader.WaveFormat.SampleRate;
                var pcm=new float[(int)(reader.Length/sizeof(float))];int offset=0,read;
                while(offset<pcm.Length&&(read=reader.Read(pcm,offset,pcm.Length-offset))>0)offset+=read;
                if(offset!=pcm.Length)throw new InvalidDataException("Incomplete stem decode.");
                return (pcm,channels,rate,analysis);
            });
            while(!task.IsCompleted){if(version!=generation)yield break;yield return null;}
            if(version!=generation)yield break;
            string error=task.IsFaulted?task.Exception.GetBaseException().Message:null;
            if(error==null)try{
                var result=task.Result;var master=recording.Source.clip;
                if(result.rate!=master.frequency||result.pcm.Length/result.channels!=master.samples)throw new InvalidDataException("Stem sample clock differs from recording.");
                var data=JsonUtility.FromJson<PreparedPatternSong>(result.analysis);
                if(data.Version!=1||data.MidiSha256!=stem.midiSha256||data.Frames==null||data.Form==null)throw new InvalidDataException("Invalid stem analysis.");
                var clip=AudioClip.Create(stem.name,master.samples,result.channels,result.rate,false);clip.SetData(result.pcm,0);
                var go=new GameObject("Preloaded stem · "+stem.name);go.transform.SetParent(transform,false);
                var source=go.AddComponent<AudioSource>();source.clip=clip;source.playOnAwake=false;source.spatialBlend=0;source.priority=0;source.volume=0;
                cache.Add(stem.id,new CachedStem{Source=source,Score=data});midi.WarmVisualPrepared(data);
            }catch(Exception e){error=e.Message;}
            if(error!=null){Status="Stem preparation failed: "+error;IsLoading=false;RefreshUI();yield break;}
            yield return null;
        }
        IsLoading=false;Status=Stems.Length>0?"Ready · instant audio + visual solo. Full-song chord colors stay visible.":"No stems yet · use Add stems in Song Workshop.";RefreshUI();
    }
    public void Clear()
    {
        generation++;IsLoading=false;foreach(var item in cache.Values){if(item.Source!=null){item.Source.Stop();Destroy(item.Source.clip);Destroy(item.Source.gameObject);}}
        cache.Clear();Stems=Array.Empty<Stem>();SelectedId="";MasterGain=1;Status="Load a prepared song with stems.";RefreshUI();
    }
    static string Hash(string path){using var h=System.Security.Cryptography.SHA256.Create();using var s=File.OpenRead(path);return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
    string Verified(string path,string hash)
    {
        string full=Path.GetFullPath(Path.Combine(directory,path));
        if(!full.StartsWith(Path.GetFullPath(directory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Hash(full)!=hash)throw new InvalidDataException("Stem file is missing, outside its bundle, or changed.");
        return full;
    }
    public void Select(string id)
    {
        if(!ReadyToSolo||recording.Busy||id==SelectedId)return;
        if(id!=""&&!cache.ContainsKey(id))return;
        SelectedId=id;midi.SetVisualPrepared(id==""?null:cache[id].Score);
        Status=id==""?"Full mix · original recording and MIDI":"Solo: "+Stems.First(s=>s.id==id).name+" · full-song chord colors";
        RefreshUI();
    }
    public void Schedule(double position,double dspTime,float speed,bool playing)
    {
        foreach(var item in cache.Values){var source=item.Source;source.Stop();source.timeSamples=Math.Clamp((int)(position*source.clip.frequency),0,source.clip.samples-1);source.pitch=speed;if(playing)source.PlayScheduled(dspTime);}
    }
    public void Pause(){foreach(var item in cache.Values)item.Source.Pause();}
    public void Stop(){foreach(var item in cache.Values)item.Source.Stop();}
    void Update()
    {
        float step=Time.unscaledDeltaTime/.025f,volume=GetComponent<Main>().Synth?.Volume??0;
        MasterGain=Mathf.MoveTowards(MasterGain,SelectedId==""?1:0,step);
        recording.Source.volume=MasterGain*volume;
        foreach(var entry in cache){entry.Value.Gain=Mathf.MoveTowards(entry.Value.Gain,entry.Key==SelectedId?1:0,step);entry.Value.Source.volume=entry.Value.Gain*volume;}
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
        box.schedule.Execute(()=>{selector.SetEnabled(ReadyToSolo);restore.SetEnabled(!IsLoading&&SelectedId!="");label.text=Status;}).Every(100);
        RefreshUI();return box;
    }
    void RefreshUI(){if(selector==null)return;selector.choices=new[]{"Full mix"}.Concat(Stems.Select(s=>s.name)).ToList();selector.SetValueWithoutNotify(selector.choices[Math.Max(0,Array.FindIndex(Stems,s=>s.id==SelectedId)+1)]);}
    void OnDestroy(){Clear();}
}
