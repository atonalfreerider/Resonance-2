using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

// The headset's menus, made for fingertips. Turn the left palm toward you and a column of
// buttons stands above it: Songs, Play/Pause (a loading percentage while a song loads), Play the
// torus or Song view, Passthrough or Blackout, See inside or Solid torus. Poke one with the right
// index finger. Recentring follows the headset's own (hold the Meta button). Songs opens the song
// list in front of you: the fully prepared songs on the headset, one card each with its chord
// progression as a strip of colours, ten to a page. It opens by itself when no song is loaded.
public sealed class VrMenu : MonoBehaviour
{
    VrSession session;HandInput hands;
    Transform palm,list;VrButton songs,play,mode,room,inside,prev,next,close;TextBox listTitle,listStatus;
    readonly List<VrButton> cards=new();List<SongLibraryPanel.Song> library=new();int page;float palmLinger;
    // Built in metres; the roots take the rig's scale (which changes with the mode) each frame.
    static float S=>VrSession.Scale;
    static readonly Vector2 PalmButton=new(.13f,.032f),Card=new(.22f,.062f),Small=new(.08f,.032f);
    const int PerPage=10;

    void Start()
    {
        session=GetComponent<VrSession>();hands=session.Hands;
        palm=new GameObject("Palm menu").transform;palm.SetParent(transform,false);
        float y=0;VrButton Row(string text,System.Action action){var b=VrButton.Create(palm,text,PalmButton,1,action);b.transform.localPosition=new Vector3(0,y,0);y-=PalmButton.y+.008f;return b;}
        songs=Row("Songs",()=>ShowSongs(!list.gameObject.activeSelf));
        play=Row("Play",TogglePlay);
        mode=Row("Play the torus",()=>session.SetMode(session.Current==VrSession.Mode.Song?VrSession.Mode.TorusPlay:VrSession.Mode.Song));
        room=Row("Passthrough",()=>session.SetPassthrough(!session.Passthrough));
        inside=Row("See inside",()=>session.SetSeeThrough(!session.SeeThrough));
        palm.gameObject.SetActive(false);
        BuildList();ShowSongs(!session.Midi.Loaded);
    }
    void BuildList()
    {
        list=new GameObject("Song list").transform;list.SetParent(transform,false);
        listTitle=TextBox.Create("Choose a song",TextAlignmentOptions.Center);listTitle.transform.SetParent(list,false);listTitle.Size=.03f*10*.55f;
        listTitle.transform.localPosition=new Vector3(0,.2f,0);listTitle.Color=Color.white;listTitle.TextField.fontStyle=FontStyles.Bold;listTitle.TextField.fontMaterial.renderQueue=3200;
        listStatus=TextBox.Create("",TextAlignmentOptions.Center);listStatus.transform.SetParent(list,false);listStatus.Size=.02f*10*.55f;
        listStatus.transform.localPosition=new Vector3(0,.172f,0);listStatus.Color=new Color(.7f,.8f,.9f);listStatus.TextField.fontMaterial.renderQueue=3200;
        for(int i=0;i<PerPage;i++)
        {
            int slot=i;var card=VrButton.Create(list,"",Card,1,()=>Choose(slot),.016f*10*.55f);
            card.transform.localPosition=new Vector3((i%2==0?-1:1)*(Card.x*.5f+.006f),.125f-(i/2)*(Card.y+.01f),0);cards.Add(card);
        }
        float bottom=.125f-5*(Card.y+.01f);
        prev=VrButton.Create(list,"‹ Prev",Small,1,()=>Page(-1));prev.transform.localPosition=new Vector3(-.16f,bottom,0);
        close=VrButton.Create(list,"Close",Small,1,()=>ShowSongs(false));close.transform.localPosition=new Vector3(0,bottom,0);
        next=VrButton.Create(list,"Next ›",Small,1,()=>Page(1));next.transform.localPosition=new Vector3(.16f,bottom,0);
        list.gameObject.SetActive(false);
    }
    public void ShowSongs(bool show)
    {
        if(list==null)return;
        if(show){library=SongLibraryPanel.Scan();page=0;Fill();Recentered();}
        list.gameObject.SetActive(show);
    }
    // The list stands at arm's length in front of the viewer, a little below the eyes, square to them.
    public void Recentered()
    {
        if(list==null||session.Head==null)return;
        list.localScale=Vector3.one*S;
        var head=session.Head.transform;var forward=head.forward;forward.y=0;if(forward.sqrMagnitude<1e-4f)forward=session.ViewDirection;forward.Normalize();
        list.position=head.position+forward*.5f*S-Vector3.up*.06f*S;list.rotation=Quaternion.LookRotation(list.position-head.position,Vector3.up);
    }
    void Page(int step){int pages=Mathf.Max(1,(library.Count+PerPage-1)/PerPage);page=(page+step+pages)%pages;Fill();}
    void Fill()
    {
        int pages=Mathf.Max(1,(library.Count+PerPage-1)/PerPage);
        listStatus.Text=library.Count==0?$"No songs on the headset yet: run Tools/SongLibrary/deploy_quest.py ({SongLibraryPanel.LibraryRoot})":$"{library.Count} songs · page {page+1} of {pages}";
        for(int i=0;i<PerPage;i++)
        {
            int index=page*PerPage+i;var card=cards[i];bool on=index<library.Count;card.gameObject.SetActive(on);if(!on)continue;
            var song=library[index];card.SetText(song.Title);card.SetStripes(Stripes(song),1);
        }
        prev.gameObject.SetActive(pages>1);next.gameObject.SetActive(pages>1);
    }
    static Color[] Stripes(SongLibraryPanel.Song song)
    {
        string sidecar=song.Score+".stripes.json";if(!File.Exists(sidecar))return null;
        try{return JsonUtility.FromJson<StripeFile>(File.ReadAllText(sidecar)).stripes;}catch{return null;}
    }
    [System.Serializable] sealed class StripeFile{public Color[] stripes;}
    void Choose(int slot)
    {
        int index=page*PerPage+slot;if(index>=library.Count||session.Audio==null||session.Audio.Busy)return;
        session.SetMode(VrSession.Mode.Song);session.Audio.LoadPair("",library[index].Score);ShowSongs(false);
    }
    void TogglePlay()
    {
        var midi=session.Midi;if(!midi.Loaded||session.Audio.Busy)return;
        if(session.Current!=VrSession.Mode.Song)session.SetMode(VrSession.Mode.Song);
        if(midi.IsPlaying)midi.Pause();else midi.Play();
    }

    void Update()
    {
        if(session==null||hands==null||palm==null)return;
        palm.localScale=list.localScale=Vector3.one*S;
        // The palm menu follows the left palm while it faces the head (and a moment after, so a
        // finger can reach it as the hand turns).
        var left=hands.Left;
        if(left.Tracked&&left.PalmToward)palmLinger=.5f;else palmLinger-=Time.unscaledDeltaTime;
        bool showPalm=palmLinger>0;
        if(showPalm!=palm.gameObject.activeSelf)palm.gameObject.SetActive(showPalm);
        if(showPalm&&left.Tracked)
        {
            var head=session.Head.transform.position;var up=Vector3.up;
            var at=left.Palm+up*.13f*S+(head-left.Palm).normalized*.02f*S;
            palm.position=Vector3.Lerp(palm.position,at,1-Mathf.Exp(-Time.unscaledDeltaTime*18));
            var face=palm.position-head;if(face.sqrMagnitude>1e-4f)palm.rotation=Quaternion.LookRotation(face,up);   // square to the eyes, however the hand is held
        }
        // Labels that follow the state.
        var audio=session.Audio;bool loading=audio!=null&&audio.Busy;
        play.SetText(loading?$"Loading {Mathf.RoundToInt(audio.Progress*100)}%":session.Midi.IsPlaying?"Pause":"Play");play.Interactable=session.Midi.Loaded&&!loading;
        mode.SetText(session.Current==VrSession.Mode.Song?"Play the torus":"Song view");
        room.SetText(session.Passthrough?"Blackout":"Passthrough");
        inside.SetText(session.SeeThrough?"Solid torus":"See inside");
        if(list.gameObject.activeSelf){foreach(var c in cards)c.Interactable=!loading;if(loading)listStatus.Text=$"Loading {Mathf.RoundToInt(audio.Progress*100)}%";}
        // Pokes: the palm menu only from the right hand (the left carries it); the list from either.
        if(palm.gameObject.activeSelf)foreach(var b in new[]{songs,play,mode,room,inside})b.Poke(new[]{hands.Right},1);
        if(list.gameObject.activeSelf)foreach(var b in cards.Append(prev).Append(next).Append(close))if(b.gameObject.activeSelf)b.Poke(hands.Both,1);
    }
}
