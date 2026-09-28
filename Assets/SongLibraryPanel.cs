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
    public static string LibraryRoot=>Path.GetFullPath(Path.Combine(Application.dataPath,"../PreparedSongs/Library"));
    public sealed class Song {public string Folder,Title,Details,Score;public bool Lyrics,Stems;}
    public List<Song> Songs {get;private set;}=new();
    public bool Intro {get;private set;}=true;
    public bool Open {get;private set;}=true;
    // The width the docked panel takes at the right edge now (for the views' layout).
    public float DockedWidth=>Intro?0:panel!=null?panel.layout.width*shown:0;
    VisualElement root,panel,list;Button tuck;Label status;
    MidiPlayer midi;SongAudio audio;float shown=1;string loadedScore="";
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
        var hint=new Label("Fully prepared songs from the library: an aligned recording and its pattern bundle.");hint.style.whiteSpace=WhiteSpace.Normal;hint.style.color=new Color(.64f,.7f,.78f);hint.style.fontSize=12;hint.style.marginBottom=12;panel.Add(hint);
        var scroll=new ScrollView(ScrollViewMode.Vertical);scroll.style.maxHeight=new Length(70,LengthUnit.Percent);scroll.style.flexGrow=1;panel.Add(scroll);
        list=scroll.contentContainer;
        status=new Label("");status.style.whiteSpace=WhiteSpace.Normal;status.style.fontSize=11;status.style.color=new Color(.64f,.7f,.78f);status.style.marginTop=10;panel.Add(status);
        root.Add(panel);
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
            songs.Add(new Song{Folder=folder,Score=score,Title=TitleOf(name),Lyrics=File.Exists(Path.Combine(folder,"lyrics.txt")),Stems=Directory.Exists(Path.Combine(folder,"stems"))});
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
            var details=new Label((s.Lyrics?"lyrics · ":"")+(s.Stems?"stems · ":"")+"recording");details.style.fontSize=11;details.style.color=new Color(.6f,.72f,.8f);card.Add(details);
            list.Add(card);
        }
        status.text=Songs.Count==0?$"No fully prepared songs in {LibraryRoot}. Prepare one with the Song Workshop.":$"{Songs.Count} song{(Songs.Count==1?"":"s")} · {LibraryRoot}";
    }
    void Choose(Song song)
    {
        if(audio==null||audio.Busy)return;
        ExplorerInputFocus.ClaimUI();loadedScore=song.Score;audio.LoadPair("",song.Score);
        Intro=false;SetOpen(false);tuck.style.display=DisplayStyle.Flex;
    }
    void LateUpdate()
    {
        if(panel==null||root==null)return;
        float width=root.resolvedStyle.width,height=root.resolvedStyle.height;
        if(!float.IsFinite(width)||width<1)return;
        // Loaded another way (a validation, a file field): leave the intro without a choice.
        if(Intro&&midi!=null&&midi.Loaded){Intro=false;SetOpen(false);tuck.style.display=DisplayStyle.Flex;}
        float blend=Main.ReducedMotion?1:1-Mathf.Exp(-Time.unscaledDeltaTime*8);
        shown=Mathf.Lerp(shown,Intro||Open?1:0,blend);
        if(Intro)
        {
            panel.style.left=(width-Width)*.5f;panel.style.top=Mathf.Max(24,height*.12f);panel.style.maxHeight=height*.76f;panel.style.translate=new Translate(0,0);panel.style.opacity=1;
            panel.style.visibility=Visibility.Visible;return;
        }
        panel.style.left=width-Width;panel.style.top=12;panel.style.maxHeight=height-24;
        panel.style.translate=new Translate(Width*(1-shown),0);panel.style.opacity=shown;
        panel.style.visibility=shown<.005f?Visibility.Hidden:Visibility.Visible;
        tuck.style.left=width-Width*shown-25;tuck.style.top=Mathf.Max(90,(height-64)*.5f);
        foreach(var card in list.Children())card.SetEnabled(!(audio?.Busy??false));
    }
}
