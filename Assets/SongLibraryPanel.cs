using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

// The song selector: the songs of the library (PreparedSongs/Library) that are fully prepared
// (a recording manifest, an aligned score and a current pattern bundle), one card each. It
// opens as the intro, centred, until a song is chosen; then it docks at the right edge behind
// its own tuck button, and can be pulled out again to change song.
public sealed class SongLibraryPanel : MonoBehaviour
{
    public static string LibraryRoot=>Application.platform==RuntimePlatform.Android?Path.Combine(Application.persistentDataPath,"Library"):Path.GetFullPath(Path.Combine(Application.dataPath,"../PreparedSongs/Library"));
    public sealed class Song {public string Folder,Title,Details,Score;public bool Lyrics,Stems,Tour;public Color[] Stripes;public VisualElement StripeRow;}
    // A song's chord progression as colour stripes, sampled evenly over its length, cached beside the bundle.
    [Serializable] sealed class ChordStripes {public int version=1;public int key;public Color[] stripes;}
    const int StripeCount=48;
    public List<Song> Songs {get;private set;}=new();
    public bool Intro {get;private set;}=true;
    public bool Open {get;private set;}=true;
    // The width the docked panel takes at the right edge now (for the views' layout).
    public float DockedWidth=>Intro?0:panel!=null?panel.layout.width*shown:0;
    VisualElement root,panel,list,progress,progressFill;Button tuck,tutorial;Label status;bool hidden;
    // While the tutorial runs the list is out of the way; it comes back when the tour ends.
    public void Hide(){hidden=true;panel.style.display=DisplayStyle.None;tuck.style.display=DisplayStyle.None;}
    public void Show(){hidden=false;panel.style.display=DisplayStyle.Flex;if(!Intro){SetOpen(true);tuck.style.display=DisplayStyle.Flex;}}
    MidiPlayer midi;SongAudio audio;float shown=1,placedShown=-1,placedWidth=-1,placedHeight=-1;bool placedIntro,placedBusy;string loadedScore="";
    const float Width=440;

    public void Bind(VisualElement ui)
    {
        root=ui;midi=GetComponent<MidiPlayer>();audio=GetComponent<SongAudio>();
        panel=new VisualElement{name="song-library"};panel.style.position=Position.Absolute;panel.style.width=Width;
        panel.style.backgroundColor=new Color(.05f,.075f,.12f,.96f);panel.style.paddingLeft=panel.style.paddingRight=22;panel.style.paddingTop=20;panel.style.paddingBottom=18;
        panel.style.borderTopLeftRadius=panel.style.borderBottomLeftRadius=panel.style.borderTopRightRadius=panel.style.borderBottomRightRadius=14;
        panel.style.borderLeftWidth=panel.style.borderRightWidth=panel.style.borderTopWidth=panel.style.borderBottomWidth=1;
        panel.style.borderLeftColor=panel.style.borderRightColor=panel.style.borderTopColor=panel.style.borderBottomColor=new Color(.2f,.26f,.34f);
        var eyebrow=new Label("RESONANCE");eyebrow.style.color=new Color(.52f,.66f,.8f);eyebrow.style.fontSize=11;eyebrow.style.letterSpacing=3;panel.Add(eyebrow);
        var title=new Label("Choose a song");title.style.fontSize=24;title.style.unityFontStyleAndWeight=FontStyle.Bold;title.style.marginBottom=4;title.style.color=Color.white;panel.Add(title);
        var hint=new Label("Fully prepared songs from the library: an aligned recording and its pattern bundle.");hint.style.whiteSpace=WhiteSpace.Normal;hint.style.color=new Color(.64f,.7f,.78f);hint.style.fontSize=12;hint.style.marginBottom=10;panel.Add(hint);
        tutorial=new Button(()=>{ExplorerInputFocus.ClaimUI();GetComponent<TutorialDirector>()?.Play();}){text="▶  Tutorial · how the torus works",name="tutorial-button"};
        tutorial.style.marginBottom=10;tutorial.style.marginRight=0;tutorial.style.backgroundColor=new Color(.16f,.3f,.42f);tutorial.style.borderLeftColor=tutorial.style.borderRightColor=tutorial.style.borderTopColor=tutorial.style.borderBottomColor=new Color(.35f,.55f,.72f);
        tutorial.style.borderTopLeftRadius=tutorial.style.borderTopRightRadius=tutorial.style.borderBottomLeftRadius=tutorial.style.borderBottomRightRadius=7;tutorial.style.paddingTop=9;tutorial.style.paddingBottom=9;tutorial.style.unityTextAlign=TextAnchor.MiddleLeft;panel.Add(tutorial);
        var scroll=new ScrollView(ScrollViewMode.Vertical);scroll.style.maxHeight=new Length(70,LengthUnit.Percent);scroll.style.flexGrow=1;panel.Add(scroll);
        list=scroll.contentContainer;
        status=new Label("");status.style.whiteSpace=WhiteSpace.Normal;status.style.fontSize=11;status.style.color=new Color(.64f,.7f,.78f);status.style.marginTop=10;panel.Add(status);
        root.Add(panel);
        progress=new VisualElement{pickingMode=PickingMode.Ignore};progress.style.height=3;progress.style.marginTop=6;progress.style.backgroundColor=new Color(.12f,.18f,.26f);progress.style.display=DisplayStyle.None;panel.Add(progress);
        progressFill=new VisualElement{pickingMode=PickingMode.Ignore};progressFill.style.height=3;progressFill.style.backgroundColor=new Color(.5f,.78f,.95f);progress.Add(progressFill);
        tuck=new Button(()=>SetOpen(!Open)){text="›",name="tuck-song-library",tooltip="Tuck away the song list"};root.Add(tuck);
        tuck.style.position=Position.Absolute;tuck.style.width=25;tuck.style.height=64;tuck.style.fontSize=27;
        tuck.style.marginLeft=tuck.style.marginRight=tuck.style.marginTop=tuck.style.marginBottom=0;tuck.style.paddingLeft=tuck.style.paddingRight=0;
        tuck.style.backgroundImage=StyleKeyword.None;tuck.style.backgroundColor=new Color(.07f,.13f,.2f,.92f);tuck.style.color=new Color(.62f,.84f,1);
        tuck.style.borderTopLeftRadius=tuck.style.borderBottomLeftRadius=10;tuck.style.borderTopRightRadius=tuck.style.borderBottomRightRadius=0;
        tuck.style.borderTopWidth=tuck.style.borderBottomWidth=tuck.style.borderLeftWidth=tuck.style.borderRightWidth=0;
        tuck.style.display=DisplayStyle.None;
        Rescan();
    }
    public void SetOpen(bool open){Open=open;tuck.text=open?"›":"‹";tuck.tooltip=open?"Tuck away the song list":"Choose a song";if(!open)ExplorerInputFocus.ClaimViewport();}

    // A song is fully prepared when its folder holds the aligned score, the recording manifest
    // beside it, and a pattern bundle of the current version.
    public static List<Song> Scan()
    {
        var songs=new List<Song>();
        if(!Directory.Exists(LibraryRoot))return songs;
        foreach(var folder in Directory.GetDirectories(LibraryRoot).OrderBy(f=>Path.GetFileName(f),StringComparer.OrdinalIgnoreCase))
        {
            string score=Path.Combine(folder,"aligned.mid"),bundle=score+".patterns.json";
            if(!File.Exists(score)||!File.Exists(score+".prepared.json")||!File.Exists(bundle))continue;
            if(!CurrentBundle(bundle))continue;
            string name=Path.GetFileName(folder);
            songs.Add(new Song{Folder=folder,Score=score,Title=StoryTitle(folder)??TitleOf(name),Lyrics=File.Exists(Path.Combine(folder,"lyrics.txt")),Stems=Directory.Exists(Path.Combine(folder,"stems")),
                Tour=File.Exists(Path.Combine(folder,"story.json"))&&File.Exists(Path.Combine(folder,"narration.json"))});
        }
        return songs;
    }
    static bool CurrentBundle(string bundle)
    {
        try
        {
            using var reader=new StreamReader(bundle);var head=new char[65536];int read=reader.Read(head,0,head.Length);
            var m=Regex.Match(new string(head,0,read),"\"Version\"\\s*:\\s*(\\d+)");
            return m.Success&&int.Parse(m.Groups[1].Value)>=PreparedPatternSong.CurrentVersion;
        }
        catch{return false;}
    }
    // A narrated song carries its proper title in story.json ("Ni**as in Paris", "Say So").
    static string StoryTitle(string folder)
    {
        try
        {
            string path=Path.Combine(folder,"story.json");if(!File.Exists(path))return null;
            using var reader=new StreamReader(path);var head=new char[4096];int read=reader.Read(head,0,head.Length);
            var m=Regex.Match(new string(head,0,read),@"""title""\s*:\s*""((?:[^""\\]|\\.)*)""");
            return m.Success?Regex.Unescape(m.Groups[1].Value):null;
        }
        catch{return null;}
    }
    // "Fireflies---Owl-City-f259424e-61b3f5" → "Fireflies — Owl City"; "SaySo-prepared-stems" → "SaySo".
    public static string TitleOf(string folder)
    {
        string s=Regex.Replace(folder,@"-[0-9a-f]{8}-[0-9a-f]{6}$","",RegexOptions.IgnoreCase);
        s=Regex.Replace(s,@"-prepared-stems$|-stems-[0-9a-f]{6}$|-local-neural-analysis$","",RegexOptions.IgnoreCase);
        s=s.Replace("---"," — ").Replace("-"," ");
        return Regex.Replace(s,@"\s+"," ").Trim();
    }
    public void Rescan()
    {
        Songs=Scan();list.Clear();
        foreach(var song in Songs)
        {
            var s=song;var card=new Button(()=>Choose(s)){name="song-card"};
            card.style.flexDirection=FlexDirection.Column;card.style.alignItems=Align.FlexStart;card.style.marginTop=3;card.style.marginBottom=3;card.style.marginRight=0;card.style.paddingTop=9;card.style.paddingBottom=9;card.style.paddingLeft=12;
            card.style.backgroundColor=new Color(.13f,.19f,.27f);card.style.borderTopLeftRadius=card.style.borderTopRightRadius=card.style.borderBottomLeftRadius=card.style.borderBottomRightRadius=7;
            card.style.borderLeftWidth=card.style.borderRightWidth=card.style.borderTopWidth=card.style.borderBottomWidth=1;card.style.borderLeftColor=card.style.borderRightColor=card.style.borderTopColor=card.style.borderBottomColor=new Color(.26f,.33f,.43f);
            card.RegisterCallback<PointerEnterEvent>(_=>card.style.backgroundColor=new Color(.21f,.29f,.4f));card.RegisterCallback<PointerLeaveEvent>(_=>card.style.backgroundColor=new Color(.13f,.19f,.27f));
            var name=new Label(s.Title);name.style.fontSize=15;name.style.color=Color.white;name.style.whiteSpace=WhiteSpace.Normal;card.Add(name);
            var details=new Label((s.Tour?"guided tour · ":"")+(s.Lyrics?"lyrics · ":"")+(s.Stems?"stems · ":"")+"recording");details.style.fontSize=11;details.style.color=new Color(.6f,.72f,.8f);card.Add(details);
            // The chord progression as a row of colour stripes along the card's foot.
            s.StripeRow=new VisualElement{pickingMode=PickingMode.Ignore};s.StripeRow.style.flexDirection=FlexDirection.Row;s.StripeRow.style.height=7;s.StripeRow.style.marginTop=7;s.StripeRow.style.width=new Length(100,LengthUnit.Percent);
            card.Add(s.StripeRow);if(s.Stripes!=null)ShowStripes(s);
            // A narrated song carries a tour button beside its card.
            var row=new VisualElement{name="song-row"};row.style.flexDirection=FlexDirection.Row;row.style.alignItems=Align.Stretch;
            card.style.flexGrow=1;card.style.flexShrink=1;row.Add(card);
            if(s.Tour)
            {
                var tour=new Button(()=>{ExplorerInputFocus.ClaimUI();GetComponent<SongDirector>()?.Tour(s);}){text="▶\nTour",name="song-tour",tooltip="Guided tour: the song's narrated story, its history and what each view shows"};
                tour.style.width=54;tour.style.marginTop=3;tour.style.marginBottom=3;tour.style.marginLeft=5;tour.style.marginRight=0;tour.style.fontSize=11;tour.style.whiteSpace=WhiteSpace.Normal;
                tour.style.backgroundColor=new Color(.16f,.3f,.42f);tour.style.color=new Color(.86f,.95f,1);
                tour.style.borderLeftColor=tour.style.borderRightColor=tour.style.borderTopColor=tour.style.borderBottomColor=new Color(.35f,.55f,.72f);
                tour.style.borderTopLeftRadius=tour.style.borderTopRightRadius=tour.style.borderBottomLeftRadius=tour.style.borderBottomRightRadius=7;
                tour.RegisterCallback<PointerEnterEvent>(_=>tour.style.backgroundColor=new Color(.22f,.4f,.55f));tour.RegisterCallback<PointerLeaveEvent>(_=>tour.style.backgroundColor=new Color(.16f,.3f,.42f));
                row.Add(tour);
            }
            list.Add(row);
        }
        StopAllCoroutines();StartCoroutine(FillStripes());
        status.text=Songs.Count==0?$"No fully prepared songs in {LibraryRoot}. Prepare one with the Song Workshop.":$"{Songs.Count} song{(Songs.Count==1?"":"s")} · {LibraryRoot}";
    }
    static void ShowStripes(Song s)
    {
        s.StripeRow.Clear();
        foreach(var c in s.Stripes){var stripe=new VisualElement{pickingMode=PickingMode.Ignore};stripe.style.flexGrow=1;stripe.style.height=7;stripe.style.backgroundColor=new Color(c.r,c.g,c.b,.85f);s.StripeRow.Add(stripe);}
    }
    // Stripes come from a small sidecar beside the bundle; the first time, from the bundle
    // itself, one song a frame so the intro stays quick.
    System.Collections.IEnumerator FillStripes()
    {
        foreach(var song in Songs.ToList())
        {
            if(song.Stripes!=null)continue;
            string sidecar=song.Score+".stripes.json";
            try
            {
                ChordStripes cached=File.Exists(sidecar)?JsonUtility.FromJson<ChordStripes>(File.ReadAllText(sidecar)):null;
                if(cached==null||cached.stripes==null||cached.stripes.Length!=StripeCount)
                {
                    var data=JsonUtility.FromJson<PreparedPatternSong>(File.ReadAllText(song.Score+".patterns.json"));
                    cached=new ChordStripes{key=data.Frames!=null&&data.Frames.Length>0&&data.Frames[0].Key>=0?data.Frames[0].Key:Math.Max(0,data.Key),stripes=Sample(data)};
                    File.WriteAllText(sidecar,JsonUtility.ToJson(cached));
                }
                song.Stripes=cached.stripes;
            }
            catch(Exception e){Debug.LogWarning($"No chord stripes for {song.Title}: {e.Message}");song.Stripes=new Color[StripeCount];}
            if(song.StripeRow!=null)ShowStripes(song);
            yield return null;
        }
    }
    static Color[] Sample(PreparedPatternSong data)
    {
        var stripes=new Color[StripeCount];var chords=data.Chords??Array.Empty<SongFormAnalysis.ChordStep>();
        int key=data.Frames!=null&&data.Frames.Length>0&&data.Frames[0].Key>=0?data.Frames[0].Key:Math.Max(0,data.Key);
        double end=Math.Max(1e-6,data.EndBeat);
        for(int i=0;i<StripeCount;i++)
        {
            double beat=(i+.5)/StripeCount*end;SongFormAnalysis.ChordStep at=null;
            foreach(var c in chords)if(c.Start<=beat&&beat<c.End){at=c;break;}
            stripes[i]=at==null||at.Root<0?new Color(.12f,.16f,.2f):CyclicOrrery.ChordColor(at,key);
        }
        return stripes;
    }
    public void Choose(Song song)
    {
        if(audio==null||audio.Busy)return;
        ExplorerInputFocus.ClaimUI();loadedScore=song.Score;audio.LoadPair("",song.Score);
        Intro=false;SetOpen(false);tuck.style.display=DisplayStyle.Flex;
    }
    void LateUpdate()
    {
        if(panel==null||root==null||hidden)return;
        float width=root.resolvedStyle.width,height=root.resolvedStyle.height;
        if(!float.IsFinite(width)||width<1)return;
        // Loaded another way (a validation, a file field): leave the intro without a choice.
        if(Intro&&midi!=null&&midi.Loaded){Intro=false;SetOpen(false);tuck.style.display=DisplayStyle.Flex;}
        float blend=Main.ReducedMotion?1:1-Mathf.Exp(-Time.unscaledDeltaTime*8);
        shown=Mathf.Lerp(shown,Intro||Open?1:0,blend);if(Mathf.Abs(shown-(Intro||Open?1:0))<.002f)shown=Intro||Open?1:0;
        // Styles are written only when something moved: a write relayouts the whole list.
        bool busy=audio?.Busy??false;
        if(shown==placedShown&&width==placedWidth&&height==placedHeight&&Intro==placedIntro&&busy==placedBusy&&!busy)return;
        placedShown=shown;placedWidth=width;placedHeight=height;placedIntro=Intro;placedBusy=busy;
        if(Intro)
        {
            panel.style.left=(width-Width)*.5f;panel.style.top=Mathf.Max(24,height*.12f);panel.style.maxHeight=height*.76f;panel.style.translate=new Translate(0,0);panel.style.opacity=1;
            panel.style.visibility=Visibility.Visible;return;
        }
        panel.style.left=width-Width;panel.style.top=12;panel.style.maxHeight=height-24;
        panel.style.translate=new Translate(Width*(1-shown),0);panel.style.opacity=shown;
        panel.style.visibility=shown<.005f?Visibility.Hidden:Visibility.Visible;
        tuck.style.left=width-Width*shown-25;tuck.style.top=Mathf.Max(90,(height-64)*.5f);
        foreach(var card in list.Children())card.SetEnabled(!busy);
        progress.style.display=busy?DisplayStyle.Flex:DisplayStyle.None;
        if(busy)progressFill.style.width=new Length(100*Mathf.Clamp01(audio.Progress),LengthUnit.Percent);
    }
}
