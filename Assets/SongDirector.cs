using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

// A prepared script drives the same clock and controls as manual playback. No API at runtime.
[DefaultExecutionOrder(80)]
public sealed class SongDirector : MonoBehaviour
{
    [Serializable] public sealed class Cue { public double start,end;public string text,view,stem,annotationTarget,annotationLabel;public bool uncoil,releaseSoloAfterNarration; }
    [Serializable] public sealed class Story {
        public int version;public string title,midiSha256,audioSha256,patternsSha256,model;public double duration;public Cue[] cues;
    }
    MidiPlayer midi;SongAudio audio;StemPlayback stems;VisualizationViews views;Main main;
    Toggle toggle;Label status,caption;VisualElement footer,controls;
    Story story;Task<Story> pending;string source;int current=-2;
    VisualizationViews.View previousView;string previousStem;bool previousUncoil;
    public bool Directing {get;private set;}
    public int CueIndex=>current;
    public bool Available=>story!=null;
    public int Revision {get;private set;}
    public Cue CurrentCue=>Directing&&story!=null&&current>=0&&current<story.cues.Length?story.cues[current]:null;
    public string Caption=>caption?.text??"";
    public string Status=>status?.text??"";
    public void Bind(VisualElement root,VisualElement panel,VisualElement page,PatternWheelDeck wheels)
    {
        midi=GetComponent<MidiPlayer>();audio=GetComponent<SongAudio>();stems=GetComponent<StemPlayback>();
        views=GetComponent<VisualizationViews>();main=GetComponent<Main>();controls=panel;
        var box=new VisualElement{name="song-director-controls"};page.Insert(0,box);
        toggle=new Toggle("Director mode"){name="director-mode",tooltip="Follow the prepared listening story: camera views, audio and visual solos."};
        toggle.RegisterValueChangedCallback(e=>SetDirecting(e.newValue));box.Add(toggle);
        status=new Label("Load a song with a prepared story.");status.style.whiteSpace=WhiteSpace.Normal;box.Add(status);
        box.Add(new Button(Reload){text="Reload prepared story",name="reload-song-story"});
        gameObject.AddComponent<SongNarration>().Bind(box);
        gameObject.AddComponent<StoryAnnotations>().Bind(root,panel,wheels,box);
        footer=new VisualElement{name="song-story-footer",pickingMode=PickingMode.Ignore};root.Add(footer);
        caption=new Label{name="song-story-caption",pickingMode=PickingMode.Ignore,enableRichText=false};footer.Add(caption);
        footer.style.display=DisplayStyle.None;toggle.SetEnabled(false);
    }
    static string Hash(string path){using var h=System.Security.Cryptography.SHA256.Create();using var s=File.OpenRead(path);return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
    static Story Read(string score)
    {
        string directory=Path.GetDirectoryName(Path.GetFullPath(score));string path=Path.Combine(directory,"story.json");
        if(!File.Exists(path))return null;
        var value=JsonUtility.FromJson<Story>(File.ReadAllText(path));
        var manifest=JsonUtility.FromJson<SongAudio.Prepared>(File.ReadAllText(score+".prepared.json"));
        if(value==null||manifest==null||value.version!=1||value.midiSha256!=manifest.midiSha256||value.audioSha256!=manifest.audioSha256||
            value.patternsSha256!=Hash(score+".patterns.json")||!double.IsFinite(value.duration)||Math.Abs(value.duration-manifest.audioDuration)>.01)
            throw new InvalidDataException("Story is stale. Regenerate it in Song Prep.");
        if(value.cues==null||value.cues.Length<1||value.cues.Length>100)throw new InvalidDataException("Invalid story cues.");
        double end=0;
        foreach(var cue in value.cues){
            if(cue==null||!double.IsFinite(cue.start)||!double.IsFinite(cue.end)||cue.start<end||cue.end<=cue.start||cue.end>value.duration+.001||
                string.IsNullOrWhiteSpace(cue.text)||cue.text.Length>450||!Enum.GetNames(typeof(VisualizationViews.View)).Contains(cue.view)||
                (cue.uncoil&&cue.view!="Torus")||cue.stem==null||(cue.stem!=""&&!(manifest.stems??Array.Empty<StemPlayback.Stem>()).Any(s=>s.id==cue.stem)))
                throw new InvalidDataException("Invalid story cue; no controls applied.");
            end=cue.end;
        }
        return value;
    }
    public void SetDirecting(bool enabled)
    {
        enabled=enabled&&story!=null&&audio.Ready&&!audio.Busy&&!stems.IsLoading;
        if(enabled==Directing){toggle?.SetValueWithoutNotify(enabled);return;}
        if(enabled){previousView=views.Current;previousUncoil=main.Uncoiled;previousStem=stems.SelectedId;current=-2;}
        else{stems.Select(previousStem??"");views.SetView(previousView);main.SetUncoiled(previousUncoil);footer.style.display=DisplayStyle.None;caption.text="";}
        Directing=enabled;toggle.SetValueWithoutNotify(enabled);
    }
    public void Reload(){if(Directing)SetDirecting(false);source=null;}
    void Update()
    {
        if(midi==null)return;
        if(source!=midi.midiPath){
            if(Directing)SetDirecting(false);
            source=midi.midiPath;story=null;Revision++;toggle.SetEnabled(false);current=-2;
            string score=source;
            pending=string.IsNullOrWhiteSpace(score)?null:Task.Run(()=>Read(score));
            status.text="Checking prepared story…";
        }
        if(pending!=null&&pending.IsCompleted){
            if(pending.IsFaulted)status.text="Story unavailable or stale. Regenerate it in Song Prep.";
            else{story=pending.Result;status.text=story==null?"No story yet. Generate one in Song Prep.":"Prepared listening story · "+story.cues.Length+" scenes. Views and audio + visual solos follow the song clock.";}
            pending=null;Revision++;
        }
        toggle.SetEnabled(story!=null&&audio.Ready&&!audio.Busy&&!stems.IsLoading);
        if(!Directing||audio.Busy||stems.IsLoading)return;
        footer.style.left=views.PanelHidden?24:controls.resolvedStyle.width+36;
        double now=midi.Position;int index=Array.FindIndex(story.cues,c=>now>=c.start&&now<c.end);
        // AudioClip.length is a float; its endpoint can be fractionally earlier
        // than the sample-accurate duration stored by preprocessing.
        if(now>=Math.Min(story.duration,midi.Duration)-.001){SetDirecting(false);return;}
        if(index>=0){
            var active=story.cues[index];var narration=GetComponent<SongNarration>();
            bool release=active.releaseSoloAfterNarration&&(narration==null||narration.CueFinished(index,now));
            stems.Select(release?"":active.stem);
        }
        if(index==current)return;
        current=index;
        if(index<0){caption.text="";footer.style.display=DisplayStyle.None;stems.Select("");return;}
        var cue=story.cues[index];views.SetView(Enum.Parse<VisualizationViews.View>(cue.view));main.SetUncoiled(cue.uncoil);
        caption.text=cue.text;footer.style.display=DisplayStyle.Flex;
    }
    void OnDisable(){if(Directing&&views!=null)SetDirecting(false);}
}
