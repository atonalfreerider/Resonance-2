using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

[DefaultExecutionOrder(90)]
public sealed class SongNarration : MonoBehaviour
{
    [Serializable] public sealed class Segment { public int cue;public double start,end; }
    [Serializable] public sealed class Manifest { public int version,sampleRate,samples;public string storySha256,audioSha256,audioPath,sha256;public double duration;public Segment[] segments; }
    sealed class Decoded { public Manifest Meta;public float[] Data;public int Channels,Rate; }
    MidiPlayer midi;SongDirector director;AudioSource voice;Toggle toggle;Label status;
    Task<Decoded> pending;Manifest manifest;int revision=-1;bool running;
    public bool Enabled {get;private set;}=true;
    public bool Ready=>manifest!=null&&voice!=null&&voice.clip!=null;
    public float MusicGain {get;private set;}=1;
    public AudioSource Source=>voice;
    public bool CueFinished(int index,double position)=>!Enabled||!Ready||!manifest.segments.Any(s=>s.cue==index&&position<s.end);
    public void Bind(VisualElement page)
    {
        midi=GetComponent<MidiPlayer>();director=GetComponent<SongDirector>();
        var go=new GameObject("AI narration · recording clock");go.transform.SetParent(transform,false);
        voice=go.AddComponent<AudioSource>();voice.playOnAwake=false;voice.spatialBlend=0;voice.priority=0;
        toggle=new Toggle("AI voice narration"){name="ai-voice-narration",value=true};
        toggle.RegisterValueChangedCallback(e=>{Enabled=e.newValue;if(!Enabled)Stop();});page.Add(toggle);
        status=new Label("Generate narration in Song Workshop.");status.style.whiteSpace=WhiteSpace.Normal;page.Add(status);
    }
    static string Hash(string path){using var h=System.Security.Cryptography.SHA256.Create();using var s=File.OpenRead(path);return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
    static Decoded Read(string score)
    {
        string folder=Path.GetDirectoryName(Path.GetFullPath(score)),path=Path.Combine(folder,"narration.json");
        if(!File.Exists(path))return null;
        var meta=JsonUtility.FromJson<Manifest>(File.ReadAllText(path));
        var song=JsonUtility.FromJson<SongAudio.Prepared>(File.ReadAllText(score+".prepared.json"));
        if(meta==null||!double.IsFinite(meta.duration)||meta.version!=1||meta.storySha256!=Hash(Path.Combine(folder,"story.json"))||meta.audioSha256!=song.audioSha256||Math.Abs(meta.duration-song.audioDuration)>.01)
            throw new InvalidDataException("Narration is stale. Regenerate it after editing the story.");
        string audio=Path.GetFullPath(Path.Combine(folder,meta.audioPath));
        if(!audio.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Hash(audio)!=meta.sha256)
            throw new InvalidDataException("Narration file is invalid.");
        using var reader=new NAudio.Wave.AudioFileReader(audio);
        int channels=reader.WaveFormat.Channels,rate=reader.WaveFormat.SampleRate;
        if(rate!=meta.sampleRate||reader.Length/sizeof(float)/channels!=meta.samples||Math.Abs(meta.samples/(double)rate-meta.duration)>.01)
            throw new InvalidDataException("Narration clock differs from the recording.");
        if(meta.segments==null||meta.segments.Any(s=>s==null||!double.IsFinite(s.start)||!double.IsFinite(s.end)||s.start<0||s.end<=s.start||s.end>meta.duration))
            throw new InvalidDataException("Invalid narration segment clock.");
        var pcm=new float[meta.samples*channels];int offset=0,count;
        while(offset<pcm.Length&&(count=reader.Read(pcm,offset,pcm.Length-offset))>0)offset+=count;
        if(offset!=pcm.Length)throw new InvalidDataException("Incomplete narration audio.");
        return new Decoded{Meta=meta,Data=pcm,Channels=channels,Rate=rate};
    }
    public void Schedule(double position,double dsp,float speed,bool playing)
    {
        Stop();if(!Ready||!Enabled||!director.Directing||!playing)return;
        if(dsp<AudioSettings.dspTime+.015){double next=AudioSettings.dspTime+.025;position+=(next-dsp)*speed;dsp=next;}
        voice.timeSamples=Math.Clamp((int)Math.Round(position*voice.clip.frequency),0,voice.clip.samples-1);
        voice.pitch=speed;voice.PlayScheduled(dsp);running=true;
    }
    public void Stop(){if(voice!=null)voice.Stop();running=false;}
    void Update()
    {
        if(midi==null)return;
        if(revision!=director.Revision){
            revision=director.Revision;Stop();manifest=null;if(voice.clip!=null)Destroy(voice.clip);voice.clip=null;
            string score=midi.midiPath;pending=director.Available?Task.Run(()=>Read(score)):null;
            status.text="Checking AI narration…";
        }
        if(pending!=null&&pending.IsCompleted){
            if(pending.IsFaulted)status.text="Narration unavailable or stale. Regenerate it in Song Workshop.";
            else if(pending.Result==null)status.text="No voice track yet. Generate narration in Song Workshop.";
            else{var value=pending.Result;manifest=value.Meta;voice.clip=AudioClip.Create("AI narration",manifest.samples,value.Channels,value.Rate,false);voice.clip.SetData(value.Data,0);status.text="AI-generated voice · synchronized to the song.";}
            pending=null;
        }
        toggle.SetEnabled(Ready);
        bool shouldPlay=Ready&&Enabled&&director.Directing&&midi.IsPlaying&&!midi.Recording.Busy;
        if(shouldPlay&&!running)Schedule(midi.ClockPosition,midi.ClockDspStart,midi.playbackSpeed,true);
        else if(!shouldPlay&&running)Stop();
        double now=midi.Position;
        bool released=director.CurrentCue?.releaseSoloAfterNarration==true&&CueFinished(director.CueIndex,now);
        bool speaking=shouldPlay&&!released&&manifest.segments.Any(s=>now>=s.start-.08&&now<s.end+.1);
        MusicGain=Mathf.Lerp(MusicGain,speaking?.42f:1,1-Mathf.Exp(-Time.unscaledDeltaTime*(speaking?14:released?50:5)));
        voice.volume=(GetComponent<Main>().Synth?.Volume??0)*.95f;
    }
    void OnDisable(){Stop();MusicGain=1;}
    void OnDestroy(){if(voice!=null&&voice.clip!=null)Destroy(voice.clip);}
}
