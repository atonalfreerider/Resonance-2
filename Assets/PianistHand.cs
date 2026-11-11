using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UIElements;

// A pianist's hand for a Director cue that talks about fingering (the cue's hand): a photograph of a pianist's hand seen
// from above on the keys (Resources/PianistHand: a right hand, its thumb at the left; mirrored for a left hand), a small
// number beside each fingertip, 1 (thumb) to 5 (little finger), and arrows from each group's label, in a dark pill on the
// dark ground above the keys, to its fingertips, which glow in the group's colour. With alternate the groups take turns at
// the hand's own notes (the onsets on the score track named for that hand, or the beats without one), so a rocking figure
// such as sixths on 1+4 then 2+5 is seen as it is heard; the song clock decides, so seeks and recordings agree. In the
// Torus view the card takes its own part of the screen (VisualizationViews.ShareScreen) and the torus moves aside. The
// photograph and the overlay are laid out and drawn once per figure (and when the card is resized; a card that only moves
// is moved), each group's glow and marks in layers of their own: the turn and an onset's flare change only those layers'
// opacity, so nothing is drawn again while the hand plays.
[DefaultExecutionOrder(1117)]
public sealed class PianistHand : MonoBehaviour
{
    // A cue's hand: "right" or "left"; one to three groups of fingers (1 thumb to 5 little finger, none in two groups),
    // each with a label of up to 40 characters (the finger numbers when empty); alternate: the groups take turns.
    [Serializable] public sealed class Group { public int[] fingers;public string label; }
    [Serializable] public sealed class Figure { public string side;public Group[] groups;public bool alternate; }

    SongDirector director;MidiPlayer midi;VisualizationViews views;TutorialDirector tutorial;
    VisualElement root,card,picture,captions;Layer shade,badges;readonly Layer[] glows=new Layer[3],marks=new Layer[3],marksTop=new Layer[3];
    Label heading,ruler;readonly Label[] labels=new Label[3],numbers=new Label[6];
    Photo photo;Layout layout;Gradient vignette;
    Figure shown;int shownCue=-2,revision=-1,turn=-2;double[] onsets=Array.Empty<double>();int[] turns=Array.Empty<int>();
    float alpha,placedAlpha=-1,flare,placedPixels=-1,captionFoot=float.PositiveInfinity;Rect placed=new(0,0,-1,-1),placedRoom=new(0,0,-1,-1);
    // Each group's light (1 its turn, 0 waiting), eased; and the opacities and colours last written, so a frame writes only
    // what changed.
    readonly float[] light=new float[3],glowSet=new float[3],markSet=new float[3],topSet=new float[3],labelSet=new float[3];readonly Color[] numberSet=new Color[6];
    const float FadeSeconds=.45f,FlareSeconds=.32f,TurnSeconds=.08f;
    // Two onsets closer than this (two sixths a few hundredths apart) would flash a turn too short to see: the turn before
    // carries on through it, and the count goes on from the next, so whose turn it is stays right.
    const double ShortestTurn=.12;
    // Gold, sea green and violet, as PassageComparison's.
    static readonly Color[] Inks={new(1f,.78f,.36f),new(.42f,.86f,.8f),new(.8f,.56f,.98f)};
    static readonly Color NumberLit=new(1,1,1,.97f),NumberWaiting=new(.86f,.9f,.95f,.6f),NumberFree=new(.8f,.86f,.92f,.5f);
    static readonly ProfilerMarker Cost=new("Resonance.PianistHand"),Painting=new("Resonance.PianistHand.Paint");

    public Figure Shown=>alpha>.5f?shown:null;
    // The group whose turn it is (-1: every group is lit), and how many turns the figure has.
    public int Turn=>turn;
    public int Turns=>onsets.Length;
    // Where the photograph and its overlay are in the card (null until a figure is placed), for checks.
    public Layout Placed=>shown!=null?layout:null;

    // JsonUtility gives a cue without a hand an empty figure.
    public static bool Present(Figure f)=>f!=null&&(!string.IsNullOrEmpty(f.side)||(f.groups?.Length??0)>0||f.alternate);
    // As story.py's validate_hand: right or left; one to three groups of fingers 1-5, no finger in two groups; labels of up
    // to 40 characters; alternate with two groups or more; and one graphic per cue (no picture or comparison beside it).
    public static bool Valid(SongDirector.Cue cue)
    {
        var f=cue.hand;if(!Present(f))return true;
        if((f.side!="right"&&f.side!="left")||f.groups==null||f.groups.Length<1||f.groups.Length>3||(f.alternate&&f.groups.Length<2))return false;
        if(!string.IsNullOrEmpty(cue.image)||(cue.compareFirst?.Length??0)>0)return false;
        int seen=0;
        foreach(var g in f.groups)
        {
            if(g?.fingers==null||g.fingers.Length<1||(g.label?.Length??0)>40)return false;
            foreach(int n in g.fingers){if(n<1||n>5||(seen&(1<<n))!=0)return false;seen|=1<<n;}
        }
        return true;
    }
    static bool Same(Figure a,Figure b)
    {
        if(ReferenceEquals(a,b))return Present(a);
        if(!Present(a)||!Present(b)||a.side!=b.side||a.alternate!=b.alternate||a.groups.Length!=b.groups.Length)return false;
        for(int i=0;i<a.groups.Length;i++)
        {
            var x=a.groups[i];var y=b.groups[i];if((x.label??"")!=(y.label??"")||x.fingers.Length!=y.fingers.Length)return false;
            for(int j=0;j<x.fingers.Length;j++)if(x.fingers[j]!=y.fingers[j])return false;
        }
        return true;
    }
    static int GroupOf(Figure f,int finger)
    {
        for(int i=0;i<f.groups.Length;i++)foreach(int k in f.groups[i].fingers)if(k==finger)return i;
        return -1;
    }
    bool Lit(int group)=>turn<0||turn==group;

    // ---- The photograph: Resources/PianistHand/right-hand.jpg, and right-hand.json beside it with where each fingertip is
    // (the centre of its nail, as fractions of the picture's width and height from its top-left corner), which hand it
    // shows, and keyboardTop, above which the picture is dark enough for the labels. ----
    public const string PhotoPath="PianistHand/right-hand";
    public sealed class Photo { public Texture2D texture;public bool left;public float keyboardTop;public readonly Vector2[] tips=new Vector2[6]; }
    [Serializable] sealed class PhotoNotes { public string side;public float keyboardTop=-1; }
    // JsonUtility reads no dictionaries, so the tips ({"1": [x, y], ...}) are read with a pattern.
    static readonly Regex TipsObject=new(@"""tips""\s*:\s*\{(?<body>[^{}]*)\}"),TipEntry=new(@"""(?<k>[1-5])""\s*:\s*\[\s*(?<x>[-+0-9.eE]+)\s*,\s*(?<y>[-+0-9.eE]+)\s*\]");
    // The picture and its anchors, checked: right or left; all five fingertips inside the picture, in order across the hand
    // (from the thumb at the left for a right hand); the keys' top edge above every fingertip.
    public static Photo ReadPhoto(Texture2D texture,string json)
    {
        if(texture==null||string.IsNullOrWhiteSpace(json))throw new InvalidDataException($"Resources/{PhotoPath} (.jpg and .json) is missing");
        var notes=JsonUtility.FromJson<PhotoNotes>(json);var tips=TipsObject.Match(json);
        if(notes==null||(notes.side!="right"&&notes.side!="left")||!tips.Success)throw new InvalidDataException("the hand's anchors need side (right or left) and tips");
        var photo=new Photo{texture=texture,left=notes.side=="left",keyboardTop=notes.keyboardTop};int found=0;
        foreach(Match m in TipEntry.Matches(tips.Groups["body"].Value))
        {
            int k=m.Groups["k"].Value[0]-'0';
            if(!float.TryParse(m.Groups["x"].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out float x)||!float.TryParse(m.Groups["y"].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out float y)||
                !(x>0&&x<1&&y>0&&y<1)||(found&(1<<k))!=0)throw new InvalidDataException($"fingertip {k} is not a point inside the picture");
            photo.tips[k]=new Vector2(x,y);found|=1<<k;
        }
        if(found!=0b111110)throw new InvalidDataException("the hand's anchors need all five fingertips, 1 to 5");
        float across=photo.left?-1:1;
        for(int k=1;k<=5;k++)
        {
            if(k<5&&(photo.tips[k+1].x-photo.tips[k].x)*across<=0)throw new InvalidDataException($"fingertips 1 to 5 must run across a {notes.side} hand from its thumb");
            if(!(photo.keyboardTop>0&&photo.keyboardTop<photo.tips[k].y))throw new InvalidDataException("keyboardTop must lie above every fingertip");
        }
        return photo;
    }

    public void Bind(VisualElement ui)
    {
        root=ui;director=GetComponent<SongDirector>();midi=GetComponent<MidiPlayer>();views=GetComponent<VisualizationViews>();
        // Loaded with the app, so the first hand shows without a hitch; without it no hand shows.
        try{photo=ReadPhoto(Resources.Load<Texture2D>(PhotoPath),Resources.Load<TextAsset>(PhotoPath)?.text);}
        catch(Exception e){Debug.LogError("Pianist's hand unavailable: "+e.Message);}
        card=new VisualElement{name="pianist-hand",pickingMode=PickingMode.Ignore,usageHints=UsageHints.DynamicTransform};
        // The photograph fills the card and is cut to its rounded corners.
        card.style.position=Position.Absolute;card.style.overflow=Overflow.Hidden;card.style.backgroundColor=new Color(.006f,.009f,.014f,.97f);
        card.style.borderTopLeftRadius=card.style.borderTopRightRadius=card.style.borderBottomLeftRadius=card.style.borderBottomRightRadius=14;
        card.style.borderLeftWidth=card.style.borderRightWidth=card.style.borderTopWidth=card.style.borderBottomWidth=1;
        card.style.borderLeftColor=card.style.borderRightColor=card.style.borderBottomColor=new Color(.5f,.72f,.92f,.2f);card.style.borderTopColor=new Color(.55f,.78f,.96f,.4f);
        picture=new VisualElement{pickingMode=PickingMode.Ignore};picture.style.position=Position.Absolute;
        picture.style.backgroundSize=new BackgroundSize(Length.Percent(100),Length.Percent(100));
        if(photo!=null)picture.style.backgroundImage=new StyleBackground(photo.texture);
        card.Add(picture);card.Add(shade=new Layer(this,Part.Shade));
        // Each group's glow and marks in layers of their own, whose opacity alone shows whose turn it is (DynamicColor: an
        // opacity change is not drawn again); the badges' discs between them, under the rings the marks draw round them. The
        // marks twice: every group's, dimmed, and above them all the lit group's at full strength, so a lit arrow is never
        // crossed by a waiting one.
        for(int i=0;i<glows.Length;i++)card.Add(glows[i]=new Layer(this,Part.Glow,i));
        card.Add(badges=new Layer(this,Part.Badges));
        for(int i=0;i<marks.Length;i++)card.Add(marks[i]=new Layer(this,Part.Marks,i));
        for(int i=0;i<marksTop.Length;i++)card.Add(marksTop[i]=new Layer(this,Part.Marks,i));
        heading=new Label{pickingMode=PickingMode.Ignore,enableRichText=false};heading.style.position=Position.Absolute;
        heading.style.marginLeft=heading.style.marginRight=heading.style.marginTop=heading.style.marginBottom=0;heading.style.paddingLeft=heading.style.paddingRight=heading.style.paddingTop=heading.style.paddingBottom=0;
        heading.style.color=new Color(.5f,.68f,.84f);heading.style.unityFontStyleAndWeight=FontStyle.Bold;
        card.Add(heading);
        // A finger's number in its badge.
        for(int k=1;k<=5;k++)
        {
            var n=new Label(k.ToString()){pickingMode=PickingMode.Ignore,enableRichText=false,usageHints=UsageHints.DynamicColor};n.style.position=Position.Absolute;
            n.style.marginLeft=n.style.marginRight=n.style.marginTop=n.style.marginBottom=0;n.style.paddingLeft=n.style.paddingRight=n.style.paddingTop=n.style.paddingBottom=0;
            n.style.unityFontStyleAndWeight=FontStyle.Bold;n.style.unityTextAlign=TextAnchor.MiddleCenter;
            card.Add(n);numbers[k]=n;
        }
        // The groups' labels, each in a dark pill edged in its colour.
        for(int i=0;i<labels.Length;i++)
        {
            var l=new Label{pickingMode=PickingMode.Ignore,enableRichText=false,usageHints=UsageHints.DynamicColor};l.style.position=Position.Absolute;l.style.whiteSpace=WhiteSpace.Normal;
            l.style.unityFontStyleAndWeight=FontStyle.Bold;l.style.unityTextAlign=TextAnchor.MiddleCenter;
            l.style.marginLeft=l.style.marginRight=l.style.marginTop=l.style.marginBottom=0;l.style.backgroundColor=new Color(.012f,.02f,.034f,.74f);
            l.style.borderLeftWidth=l.style.borderRightWidth=l.style.borderTopWidth=l.style.borderBottomWidth=1;l.style.display=DisplayStyle.None;card.Add(l);labels[i]=l;
        }
        // Labels are measured at their designed size on a ruler that never changes it (the labels' own size follows the
        // overlay's scale).
        ruler=new Label{pickingMode=PickingMode.Ignore,enableRichText=false};ruler.style.position=Position.Absolute;ruler.style.visibility=Visibility.Hidden;
        ruler.style.unityFontStyleAndWeight=FontStyle.Bold;ruler.style.fontSize=LabelSize;card.Add(ruler);
        card.style.opacity=0;card.style.display=DisplayStyle.None;root.Add(card);
    }

    void LateUpdate()
    {
        using var cost=Cost.Auto();
        if(card==null||director==null||root.panel==null)return;
        tutorial??=GetComponent<TutorialDirector>();
        if(revision!=director.Revision){revision=director.Revision;shownCue=-2;}
        var cue=director.CurrentCue;
        // Only while the story directs and no tutorial runs (and only with the photograph).
        var wanted=cue!=null&&photo!=null&&Present(cue.hand)&&!(tutorial!=null&&tutorial.Playing)?cue.hand:null;
        float step=Main.ReducedMotion?1:RecordingMode.Step/FadeSeconds;
        // The same figure carries on into the next cue; a different one waits for the card to fade out.
        if(shown!=null&&Same(wanted,shown)){if(director.CueIndex!=shownCue){shownCue=director.CueIndex;FindOnsets();}alpha=Mathf.MoveTowards(alpha,1,step);}
        else if(shown!=null){alpha=Mathf.MoveTowards(alpha,0,step);if(alpha<=0)shown=null;}
        if(shown==null&&wanted!=null){shown=wanted;shownCue=director.CueIndex;Prepare();}
        // In the Torus view the card asks for its own part of the screen every frame it wants to show; it keeps the place
        // it has while it fades out (a concerto's tori keep their compact layout).
        bool concerto=midi?.HarmonicPrepared?.PianoConcerto==true;
        Rect slot=views!=null&&views.SharesScreen&&!concerto?views.ShareScreen(wanted!=null,SharedSize()):new Rect(0,0,-1,-1);
        if(shown==null){if(placedAlpha!=0){card.style.display=DisplayStyle.None;card.style.opacity=0;placedAlpha=0;}placed=placedRoom=new Rect(0,0,-1,-1);return;}
        if(wanted!=null||placed.width<0)Place(slot);
        if(alpha!=placedAlpha)
        {
            placedAlpha=alpha;float eased=alpha*alpha*(3-2*alpha);
            card.style.display=DisplayStyle.Flex;card.style.opacity=eased;
            card.style.scale=new Scale(Vector3.one*(Main.ReducedMotion?1:.94f+.06f*eased));card.style.translate=new Translate(0,Main.ReducedMotion?0:(1-eased)*10);
        }
        Follow();
    }

    void Prepare()
    {
        FindOnsets();turn=-2;flare=0;layout=null;captionFoot=float.PositiveInfinity;
        heading.text=shown.side=="left"?"LEFT HAND":"RIGHT HAND";
        for(int i=0;i<labels.Length;i++)
        {
            var l=labels[i];bool used=i<shown.groups.Length;l.style.display=used?DisplayStyle.Flex:DisplayStyle.None;if(!used)continue;
            var ink=Inks[i];l.text=LabelText(shown.groups[i]);l.style.color=ink;
            l.style.borderLeftColor=l.style.borderRightColor=l.style.borderTopColor=l.style.borderBottomColor=new Color(ink.r,ink.g,ink.b,.42f);
        }
        placed=placedRoom=new Rect(0,0,-1,-1);
    }

    // Whose turn it is: the turn of the last onset the song has passed (TurnsFor); and how bright that onset still is. Each
    // group eases to its light; all of it changes only opacities and colours.
    void Follow()
    {
        int now=-1;float lift=0;
        if(shown.alternate&&midi!=null)
        {
            double t=midi.Position;int lo=0,hi=onsets.Length;
            while(lo<hi){int mid=(lo+hi)/2;if(onsets[mid]<=t)lo=mid+1;else hi=mid;}
            int at=lo-1;while(at>0&&at+1<onsets.Length&&onsets[at+1]-onsets[at]<ShortestTurn)at--;
            now=at<0?(turns.Length>0?turns[0]:0):at<turns.Length?turns[at]:at%shown.groups.Length;
            if(at>=0&&!Main.ReducedMotion)lift=Mathf.Clamp01(1-(float)(t-onsets[at])/FlareSeconds);
        }
        bool first=turn==-2;turn=now;flare=lift;
        // Over TurnSeconds, so the turn reads as a change of light rather than a flicker; at once when the figure first shows.
        float step=first||Main.ReducedMotion?1:RecordingMode.Step/TurnSeconds;
        for(int i=0;i<shown.groups.Length&&i<light.Length;i++)light[i]=Mathf.MoveTowards(first?0:light[i],Lit(i)?1:0,step);
        Restyle(first);
    }
    // The lit group's glow, marks and label at full strength (the glow brighter just after an onset); a group waiting for its
    // turn dimmed but readable, its label nearly as clear (the words should not flicker with the notes) and without its glow;
    // the numbers of the lit fingers brightest.
    void Restyle(bool force)
    {
        for(int i=0;i<glows.Length;i++)
        {
            bool used=i<shown.groups.Length;float lit=used?light[i]:0;
            Opacity(glows[i],ref glowSet[i],lit*(.56f+.44f*(i==turn?flare:0)),force);
            Opacity(marks[i],ref markSet[i],used?Waiting:0,force);
            Opacity(marksTop[i],ref topSet[i],lit,force);
            if(used)Opacity(labels[i],ref labelSet[i],WaitingLabel+(1-WaitingLabel)*lit,force);
        }
        for(int k=1;k<=5;k++)
        {
            int g=GroupOf(shown,k);var c=g<0?NumberFree:Color.Lerp(NumberWaiting,NumberLit,light[g]);
            if(force||c!=numberSet[k]){numberSet[k]=c;numbers[k].style.color=c;}
        }
    }
    static void Opacity(VisualElement e,ref float set,float value,bool force){if(!force&&value==set)return;set=value;e.style.opacity=value;}
    // A waiting group's marks (under the lit group's, which draw over them at full strength), and its label.
    const float Waiting=.36f,WaitingLabel=.75f;

    // The song times at which the turn passes, over this cue and the cues beside it that show the same figure: the hand's
    // own onsets, on the score track named for it ("Right hand"), a chord's notes a few milliseconds apart counted once;
    // without such a track, the beats.
    void FindOnsets()
    {
        onsets=Array.Empty<double>();turns=Array.Empty<int>();var cues=director.Current?.cues;int index=director.CueIndex;
        if(!shown.alternate||cues==null||index<0||index>=cues.Length||midi==null)return;
        int first=index,last=index;
        while(first>0&&Same(cues[first-1].hand,shown)&&Math.Abs(cues[first-1].end-cues[first].start)<.002)first--;
        while(last+1<cues.Length&&Same(cues[last+1].hand,shown)&&Math.Abs(cues[last+1].start-cues[last].end)<.002)last++;
        double from=cues[first].start-.02,to=cues[last].end;var list=new List<double>();var chords=new List<List<int>>();
        var score=midi.HarmonicPrepared;int track=HandTrack(score?.TrackNames,shown.side);
        if(track>=0&&score.Frames!=null)
            foreach(var frame in score.Frames)
            {
                if(!Struck(frame.Attacks,track))continue;
                double t=midi.AudioTime(frame.Time);
                if(t<from||t>to)continue;
                if(list.Count==0||t-list[list.Count-1]>=.035){list.Add(t);chords.Add(new List<int>());}
                var chord=chords[chords.Count-1];
                foreach(var a in frame.Attacks)if(a.Track==track&&a.Pitch>=21&&a.Pitch<117&&!chord.Contains(a.Pitch))chord.Add(a.Pitch);
            }
        var beats=midi.HarmonicCycles;
        if(list.Count==0&&beats!=null)
        {
            double Score(double t)=>midi.Recording!=null&&midi.Recording.Ready?midi.Recording.Alignment.ToMidi(t):t;
            for(double b=Math.Ceiling(beats.BeatAt(Score(from))-1e-6),end=beats.BeatAt(Score(to));b<=end&&list.Count<20000;b++)list.Add(midi.AudioTime(beats.SecondsAt(b)));
        }
        onsets=list.ToArray();turns=TurnsFor(shown,chords,onsets.Length);
    }
    // Whose turn each onset is. Two groups that rock between two sixths (1+4 and 2+5) take turns by register, as a hand that
    // stays in place plays them: the higher sixth with the group of fingers nearer the little finger of a right hand (the
    // thumb of a left one), the lower with the other; a single note, a third or a fourth between them leaves the turn where
    // it was, and the onsets before the first sixth wait in its group. With three groups, or no sixths, the groups take
    // turns note by note from the first.
    static int[] TurnsFor(Figure f,List<List<int>> chords,int count)
    {
        var result=new int[count];for(int i=0;i<count;i++)result[i]=i%f.groups.Length;
        if(f.groups.Length!=2||chords==null||chords.Count!=count)return result;
        static float Mean(int[] fingers){float s=0;foreach(int k in fingers)s+=k;return s/Mathf.Max(1,fingers.Length);}
        int upper=(Mean(f.groups[1].fingers)>Mean(f.groups[0].fingers))==(f.side!="left")?1:0,lower=1-upper;
        var low=new int[count];var sixth=new bool[count];bool any=false;
        for(int i=0;i<count;i++)
        {
            var c=chords[i];if(c.Count!=2)continue;
            int a=Math.Min(c[0],c[1]),b=Math.Max(c[0],c[1]);
            if(b-a==8||b-a==9){sixth[i]=true;low[i]=a;any=true;}
        }
        if(!any)return result;
        int previous=-1,turn=-1;
        for(int i=0;i<count;i++)
        {
            if(sixth[i])
            {
                if(previous>=0)turn=low[i]>low[previous]?upper:low[i]<low[previous]?lower:turn;
                else{int next=-1;for(int j=i+1;j<count&&next<0;j++)if(sixth[j])next=j;turn=next>=0&&low[next]<low[i]?upper:lower;}
                previous=i;
            }
            result[i]=turn;
        }
        int opening=-1;for(int i=0;i<count&&opening<0;i++)if(sixth[i])opening=result[i];
        for(int i=0;i<count&&result[i]<0;i++)result[i]=opening;
        return result;
    }
    static bool Struck(PreparedPatternSong.Voice[] attacks,int track)
    {
        if(attacks!=null)foreach(var a in attacks)if(a.Track==track&&a.Pitch>=21&&a.Pitch<117)return true;
        return false;
    }
    static readonly Regex RightTrack=new(@"\bright\b|\brh\b",RegexOptions.IgnoreCase),LeftTrack=new(@"\bleft\b|\blh\b",RegexOptions.IgnoreCase);
    static int HandTrack(string[] names,string side)
    {
        var word=side=="left"?LeftTrack:RightTrack;
        if(names!=null)for(int i=0;i<names.Length;i++)if(!string.IsNullOrEmpty(names[i])&&word.IsMatch(names[i]))return i;
        return -1;
    }

    // The card's size when it shares the screen: wide and short across the top above the torus in a portrait layout, tall
    // at the left beside it in a landscape one (down to a margin above where a horizontal recording's captions begin).
    Vector2 SharedSize()
    {
        float w=root.resolvedStyle.width,h=root.resolvedStyle.height;if(!float.IsFinite(w)||w<10)return Vector2.zero;
        if(views.Portrait)return new Vector2(w-32,h*.3f);
        float tall=h*CaptionFoot-16;return new Vector2(Mathf.Min(w*.38f,tall*.88f),tall);
    }
    // Where a horizontal recording's captions begin (two lines of them, SongRecordingCaptions), as a fraction of the height,
    // less a margin as wide as the card's own: the card ends there.
    const float CaptionFoot=.74f,CaptionGap=24;
    // Screen pixels to a panel unit, from the panel itself (Screen.height trails a resized Game view by a frame).
    float Pixels(){float p=root.panel?.scaledPixelsPerPoint??0;return p>0&&float.IsFinite(p)?p:Screen.height/Mathf.Max(1,root.resolvedStyle.height);}

    // In its slot when sharing the screen; otherwise near the top of the screen, over the scene. The card is the
    // photograph framed for that room, and the overlay follows it. The room is kept to whole screen pixels: a slot still
    // gliding by fractions of a pixel moves nothing, and a room that only moves moves the card without laying it out or
    // drawing it again.
    void Place(Rect slot)
    {
        float w=root.resolvedStyle.width,h=root.resolvedStyle.height;if(!float.IsFinite(w)||w<10)return;
        bool portrait=views!=null?views.Portrait:h>w;Rect room;float px=Pixels();
        if(slot.width>10&&slot.height>10)
        {
            room=slot;
            if(!portrait)
            {
                // Clear of the captions: two lines of them allowed for, and a taller caption, once seen, for the rest of the figure.
                var cap=captions??=root.Q("song-recording-caption");
                if(cap!=null&&cap.resolvedStyle.display==DisplayStyle.Flex){var b=cap.worldBound;if(b.height>1&&b.xMin<slot.xMax&&b.xMax>slot.xMin)captionFoot=Mathf.Min(captionFoot,b.yMin-CaptionGap);}
                room.height=Mathf.Max(120,Mathf.Min(slot.height,Mathf.Min(h*CaptionFoot,captionFoot)-slot.y));
            }
        }
        else if(h>w){float width=Mathf.Min(w*.9f,820);room=new Rect((w-width)/2,h*.06f,width,Mathf.Min(width*.6f,h*.34f));}
        else{float width=Mathf.Min(w*.34f,640);room=new Rect((w-width)/2,h*.08f,width,Mathf.Min(width*1.15f,h*.66f));}
        room=new Rect(Mathf.Round(room.x*px)/px,Mathf.Round(room.y*px)/px,Mathf.Round(room.width*px)/px,Mathf.Round(room.height*px)/px);
        bool resized=layout==null||placedRoom.width<0||!Mathf.Approximately(room.width,placedRoom.width)||!Mathf.Approximately(room.height,placedRoom.height)||Mathf.Abs(px-placedPixels)>.01f*placedPixels;
        if(!resized){if(room.position!=placedRoom.position){placedRoom=room;Move(room,portrait);}return;}
        placedRoom=room;placedPixels=px;
        // The photograph fills the card inside its 1-pixel border; the overlay is laid out in the same frame. Screen pixels
        // to a panel unit bound how far the photograph may be enlarged.
        var l=Lay(photo,shown,new Vector2(room.width-2,room.height-2),px,Measure);layout=l;float u=l.unit;
        Move(room,portrait);
        picture.style.left=-l.crop.x*l.whole.x;picture.style.top=-l.crop.y*l.whole.y;picture.style.width=l.whole.x;picture.style.height=l.whole.y;
        picture.style.scale=new Scale(new Vector3(l.mirror?-1:1,1,1));
        heading.style.left=HeadingLeft*u;heading.style.top=HeadingTop*u;heading.style.fontSize=HeadingSize*u;heading.style.letterSpacing=2.5f*u;
        for(int i=0;i<shown.groups.Length&&i<labels.Length;i++)
        {
            var label=labels[i];var pill=l.pills[i];label.style.left=pill.x;label.style.top=pill.y;label.style.width=pill.width;label.style.height=pill.height;label.style.whiteSpace=l.lines[i]>1?WhiteSpace.Normal:WhiteSpace.NoWrap;
            label.style.fontSize=LabelSize*u;label.style.paddingLeft=label.style.paddingRight=PillX*u;label.style.paddingTop=label.style.paddingBottom=PillY*u;
            float r=Mathf.Min(pill.height*.5f,15*u);label.style.borderTopLeftRadius=label.style.borderTopRightRadius=label.style.borderBottomLeftRadius=label.style.borderBottomRightRadius=r;
        }
        for(int k=1;k<=5;k++)
        {
            var n=numbers[k];float r=BadgeRadius*u;n.style.left=l.badges[k].x-r;n.style.top=l.badges[k].y-r;n.style.width=n.style.height=2*r;n.style.fontSize=BadgeSize*u;
        }
        vignette=Vignette(l);
        shade.MarkDirtyRepaint();badges.MarkDirtyRepaint();foreach(var layer in glows)layer.MarkDirtyRepaint();foreach(var layer in marks)layer.MarkDirtyRepaint();foreach(var layer in marksTop)layer.MarkDirtyRepaint();
    }
    // The card in its room: beside the torus in a landscape layout centred on the room; above it in a portrait one, at its top.
    void Move(Rect room,bool portrait)
    {
        var l=layout;if(l==null)return;float cw=l.size.x+2,ch=l.size.y+2;
        var at=new Rect(room.x+(room.width-cw)/2,portrait?room.y:room.y+Mathf.Max(0,(room.height-ch)/2),cw,ch);placed=at;
        card.style.left=at.x;card.style.top=at.y;card.style.width=at.width;card.style.height=at.height;
    }

    // A label's text width at the designed size, as the panel lays it out.
    float Measure(string text)=>ruler.MeasureTextSize(text,0,VisualElement.MeasureMode.Undefined,0,VisualElement.MeasureMode.Undefined).x;

    // ---- The layout: the photograph framed, and the overlay placed on it, in the photo's panel space (0,0 at the card's
    // inner top-left corner). ----
    public sealed class Layout
    {
        // The part of the picture shown (fractions of the picture as shown, which is mirrored for the other hand), its size
        // on screen, and the whole picture's size at that scale.
        public Rect crop;public Vector2 size,whole;public bool mirror;
        // Fingertips (1-5), the radius of the ring round a nail, the badges beside the fingertips, and the keys' top edge.
        public readonly Vector2[] tips=new Vector2[6],badges=new Vector2[6];public float ring,keys;
        // The overlay's scale: 1 at the size it was drawn for, less where the fingertips are closer together than that.
        public float unit=1;
        public readonly Rect[] pills=new Rect[3];public readonly int[] lines=new int[3];
        public readonly List<Arrow> arrows=new();
        // A point of the picture (its own fractions) in the photo's panel space.
        public Vector2 At(Vector2 uv)=>new(((mirror?1-uv.x:uv.x)-crop.x)*whole.x,(uv.y-crop.y)*whole.y);
        // The fingertip nearest a point.
        public int Nearest(Vector2 at){int best=1;for(int k=2;k<=5;k++)if((at-tips[k]).sqrMagnitude<(at-tips[best]).sqrMagnitude)best=k;return best;}
    }
    public struct Arrow { public int group,finger;public Vector2 from,c1,c2,to,dir; }
    // The overlay's sizes at its designed scale (unit 1). Slack: room beyond the measured text, which the layout rounds to
    // whole pixels (a label as wide as its text can break inside a word).
    const float LabelSize=16,LineHeight=1.3f,PillX=11,PillY=4,Slack=4,HeadingLeft=14,HeadingTop=11,HeadingSize=11,HeadingFoot=27,KeysGap=12,Margin=12,Gap=14;
    const float BadgeRadius=9.5f,BadgeSize=11.5f,ArrowGap=5;
    // The most screen pixels to a pixel of the picture: more and it looks soft.
    const float MostEnlarged=1.1f;
    // The ring the overlay was drawn for (a 1080-wide portrait frame). Where the fingertips come closer than that (a dense
    // screen, where the photograph is kept sharp and so smaller) the overlay shrinks with them, so a number stays by its own
    // finger; never so far that its smallest text falls under LeastText screen pixels.
    const float DesignRing=18.6f,LeastText=14;

    // measure: a label's text width at LabelSize in panel units (the estimate when null).
    public static Layout Lay(Photo photo,Figure f,Vector2 room,float pixels,Func<string,float> measure=null)
    {
        bool mirror=(f.side=="left")!=photo.left;
        var text=new float[f.groups.Length];
        for(int i=0;i<text.Length;i++){string words=LabelText(f.groups[i]);float w=measure?.Invoke(words)??0;text[i]=float.IsFinite(w)&&w>0?w:TextWidth(words)*LabelSize;}
        // The dark ground above the keys must hold the heading and the labels: one line of labels to begin with, more when
        // the photograph comes out too narrow for them side by side; at the overlay's scale, which follows the framing.
        // The band settles from above (enough ground, at most a unit to spare): a band cut to the labels' height can let
        // the photograph grow, and the overlay with it, past what the band then holds.
        float band=HeadingFoot+8+KeysGap+PillHeight(1,1);Layout l=null;
        for(int pass=0;pass<8;pass++)
        {
            l=Frame(photo,mirror,room,pixels,band);
            float want=(HeadingFoot+8+KeysGap)*l.unit+Labels(l,f,text);if(want<=band+.01f&&band-want<=1)break;band=want>band?want:want+.5f;
        }
        float middle=l.tips[3].x;
        for(int i=0;i<f.groups.Length;i++)foreach(int k in f.groups[i].fingers){var a=Route(l.pills[i],l.tips[k],l.ring+ArrowGap*l.unit,middle);a.group=i;a.finger=k;l.arrows.Add(a);}
        Badges(l);
        return l;
    }
    // The photograph framed for a room (panel units): the hand (every fingertip with room round it, and its knuckles) and a
    // band of the dark ground above the keys (panel units) always in view; as large as the room allows, but enlarged no more
    // than MostEnlarged screen pixels to a picture pixel (pixels: screen pixels to a panel unit); what the room holds beyond
    // that, centred on the hand, up to the picture's edges. The ring round a nail (a little over a quarter of the way to the
    // next fingertip) and the overlay's scale follow.
    static Layout Frame(Photo photo,bool mirror,Vector2 room,float pixels,float band)
    {
        var l=new Layout{mirror=mirror};
        float iw=photo.texture.width,ih=photo.texture.height,kt=photo.keyboardTop,x0=1,x1=0,y1=0;
        for(int k=1;k<=5;k++){float u=mirror?1-photo.tips[k].x:photo.tips[k].x;x0=Mathf.Min(x0,u);x1=Mathf.Max(x1,u);y1=Mathf.Max(y1,photo.tips[k].y);}
        float span=x1-x0;x0=Mathf.Max(0,x0-.2f*span);x1=Mathf.Min(1,x1+.2f*span);y1=Mathf.Min(1,y1+.3f*span);
        // Panel units to a picture pixel: the largest the hand across, the band and the hand down, and sharpness allow.
        float scale=Mathf.Min(room.x/((x1-x0)*iw),Mathf.Min(MostEnlarged/Mathf.Max(.01f,pixels),(room.y-band)/Mathf.Max(1,(y1-kt)*ih)));
        // Less dark ground above the keys than the band: the picture from its top edge.
        if(kt*ih*scale<band)scale=Mathf.Min(scale,room.y/(y1*ih));
        scale=Mathf.Max(scale,.01f);
        float y0=Mathf.Max(0,kt-band/(ih*scale));
        l.whole=new Vector2(iw*scale,ih*scale);l.size=new Vector2(Mathf.Min(room.x,l.whole.x),Mathf.Min(room.y,l.whole.y));
        float cw=l.size.x/l.whole.x,ch=l.size.y/l.whole.y;
        l.crop=new Rect(Mathf.Clamp((x0+x1-cw)/2,0,1-cw),Mathf.Clamp((y0+y1-ch)/2,0,1-ch),cw,ch);
        for(int k=1;k<=5;k++)l.tips[k]=l.At(photo.tips[k]);
        l.keys=l.At(new Vector2(0,kt)).y;
        float spacing=0;for(int k=2;k<5;k++)spacing+=Vector2.Distance(l.tips[k],l.tips[k+1]);l.ring=.28f*spacing/3;
        l.unit=Mathf.Clamp(l.ring/DesignRing,Mathf.Min(1,LeastText/(HeadingSize*Mathf.Max(.01f,pixels))),1);
        return l;
    }

    // Labels: the group's words (or its finger numbers), on one line when they fit side by side, the longest wrapped when
    // they do not.
    static string LabelText(Group g)=>string.IsNullOrWhiteSpace(g.label)?string.Join(" + ",g.fingers):g.label;
    static float PillHeight(int lines,float u)=>(lines*LabelSize*LineHeight+2*PillY)*u+2;
    // A bold label's width in font sizes, estimated from the panel's sans face (when the label cannot be measured).
    static float TextWidth(string text)
    {
        float w=0;
        foreach(char c in text)w+=c is 'i' or 'l' or 'j' or '\'' or '.' or ',' or '!' or '|' or ':' or ';'?.3f:c is 't' or 'f' or 'r' or 'I'?.37f:c is 'm' or 'w'?.9f:c is 'M' or 'W'?.95f:
            char.IsUpper(c)?.72f:c==' '?.3f:.58f;
        return w;
    }
    // The labels in a row on the dark ground above the keys, each as near as it can be to above the middle of its
    // fingertips, apart from the others and inside the photograph, bottoms aligned a little above the keys (text: each
    // label's text width at the designed size). Returns the tallest label's height.
    static float Labels(Layout l,Figure f,float[] designed)
    {
        int n=f.groups.Length;float u=l.unit,tallest=0,chrome=(2*PillX+Slack)*u+2,margin=Margin*u,gap=Gap*u;
        var text=new float[n];for(int i=0;i<n;i++)text[i]=designed[i]*u;
        var width=new float[n];var centre=new float[n];var height=new float[n];
        // The row's width shared out: labels that fit their share keep their width, the rest share what is left and wrap.
        var wraps=new bool[n];float left=l.size.x-2*margin-(n-1)*gap;int sharing=n;
        for(bool settled=false;!settled&&sharing>0;)
        {
            settled=true;float share=left/sharing;
            for(int i=0;i<n;i++)if(!wraps[i]&&width[i]==0&&text[i]+chrome<=share){width[i]=text[i]+chrome;left-=width[i];sharing--;settled=false;}
            if(settled)for(int i=0;i<n;i++)if(width[i]==0){wraps[i]=true;width[i]=Mathf.Max(LabelSize*3*u+chrome,share);}
        }
        for(int i=0;i<n;i++)
        {
            // A label that wraps is cut into lines of about equal length, each with room to break between words.
            int lines=1;
            if(wraps[i]){lines=Mathf.Clamp(Mathf.CeilToInt(text[i]*1.1f/(width[i]-chrome+Slack*u)),2,3);width[i]=Mathf.Min(width[i],text[i]*1.2f/lines+chrome);}
            l.lines[i]=lines;height[i]=PillHeight(lines,u);tallest=Mathf.Max(tallest,height[i]);
            float x=0;foreach(int k in f.groups[i].fingers)x+=l.tips[k].x;centre[i]=x/f.groups[i].fingers.Length;
        }
        var order=new int[n];for(int i=0;i<n;i++)order[i]=i;Array.Sort(order,(a,b)=>centre[a].CompareTo(centre[b]));
        for(int round=0;round<40;round++)
        {
            for(int j=0;j+1<n;j++){int a=order[j],b=order[j+1];float over=centre[a]+width[a]/2+gap-(centre[b]-width[b]/2);if(over>0){centre[a]-=over/2;centre[b]+=over/2;}}
            for(int i=0;i<n;i++)centre[i]=Mathf.Clamp(centre[i],margin+width[i]/2,Mathf.Max(margin+width[i]/2,l.size.x-margin-width[i]/2));
        }
        float foot=Mathf.Min(l.keys-KeysGap*u,l.size.y-margin);
        for(int i=0;i<n;i++)l.pills[i]=new Rect(centre[i]-width[i]/2,foot-height[i],width[i],height[i]);
        return tallest;
    }
    // An arrow from a label to a fingertip: out of the label's foot (above the fingertip, as far as the label reaches), down
    // in a gentle curve that bows away from the middle of the hand, and in to the nail from above, over the key beyond the
    // fingertip, ending clear (just outside the ring round the nail).
    static Arrow Route(Rect pill,Vector2 tip,float clear,float middle)
    {
        float inset=Mathf.Min(pill.height,pill.width)*.5f;
        var from=new Vector2(Mathf.Clamp(tip.x,pill.xMin+inset,pill.xMax-inset),pill.yMax-1);
        var dir=((tip-from).normalized+new Vector2(0,1.6f)).normalized;   // panel y runs down
        var to=tip-dir*clear;float reach=Vector2.Distance(from,to),bow=(tip.x<middle?-1:1)*reach*.14f;
        return new Arrow{from=from,c1=from+new Vector2(bow,reach*.42f),c2=to-dir*(reach*.42f),to=to,dir=dir};
    }
    static Vector2 Bezier(in Arrow a,float t){float u=1-t;return u*u*u*a.from+3*u*u*t*a.c1+3*u*t*t*a.c2+t*t*t*a.to;}
    // A number beside each fingertip, just outside its ring: above the nail where no arrow comes in, otherwise on the side
    // away from the middle finger (the middle finger's toward the index), or the nearest place round the nail that touches
    // no nail, badge, arrow, label or edge. Always nearer its own nail than any other, or it would read as that finger's
    // number: a place that is not is never taken (closer in to the nail first; only a hand too cramped for any falls back).
    static readonly (float angle,float cost)[] Around={(-90,0),(0,.8f),(-45,1.2f),(180,1.6f),(-135,2f),(45,3f),(135,3.4f)};   // degrees from the outward side, y down
    static void Badges(Layout l)
    {
        float u=l.unit,r=BadgeRadius*u;var path=new List<Vector2>();
        foreach(var a in l.arrows)for(int i=0;i<=24;i++)path.Add(Bezier(a,i/24f));
        for(int k=1;k<=5;k++)
        {
            float away=Mathf.Sign((k==3?l.tips[2].x:l.tips[k].x)-l.tips[3].x);if(away==0)away=-1;
            float best=float.MaxValue,fallback=float.MaxValue;var loose=l.tips[k];bool taken=false;
            for(int closer=0;closer<3&&!taken;closer++)
            {
                float reach=l.ring+r+(4-1.5f*closer)*u;
                foreach(var (angle,preference) in Around)
                {
                    float a=angle*Mathf.Deg2Rad;var at=l.tips[k]+new Vector2(Mathf.Cos(a)*away,Mathf.Sin(a))*reach;float cost=preference;
                    if(at.x<r+4*u||at.x>l.size.x-r-4*u||at.y<r+4*u||at.y>l.size.y-r-4*u)cost+=100;
                    for(int j=1;j<=5;j++)
                    {
                        float d=Vector2.Distance(at,l.tips[j]);cost+=20*Mathf.Max(0,l.ring+r+3*u-d);
                        // Beside another finger's nail it would be easy to misread.
                        if(j!=k&&d<reach+.6f*l.ring)cost+=4;
                    }
                    for(int j=1;j<k;j++)cost+=20*Mathf.Max(0,2*r+4*u-Vector2.Distance(at,l.badges[j]));
                    foreach(var p in path)cost+=8*Mathf.Max(0,r+5*u-Vector2.Distance(at,p));
                    foreach(var pill in l.pills)if(pill.width>0&&at.x>pill.xMin-r-3*u&&at.x<pill.xMax+r+3*u&&at.y>pill.yMin-r-3*u&&at.y<pill.yMax+r+3*u)cost+=100;
                    if(l.Nearest(at)!=k){if(cost<fallback&&!taken){fallback=cost;loose=at;}continue;}
                    if(cost<best){best=cost;l.badges[k]=at;taken=true;}
                }
            }
            if(!taken)l.badges[k]=loose;
        }
    }

    // Fades are Gradients with alpha keys (a FillGradient made from two Colors ignores their alpha), made on the main thread
    // when first drawn (or placed) and kept.
    static Gradient edge,beside;static readonly Dictionary<Color,Gradient> halos=new();
    static readonly Color Dark=new(.006f,.009f,.014f);
    // A halo round a ring: nothing over the nail, brightest at the ring, fading out beyond it (radius Reach rings).
    const float Reach=1.8f;
    static Gradient Halo(Color ink)
    {
        if(halos.TryGetValue(ink,out var g))return g;
        const float a=.85f,ring=1/Reach;g=new Gradient();
        g.SetKeys(new[]{new GradientColorKey(ink,0),new GradientColorKey(ink,1)},
            new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(0,ring*.8f),new GradientAlphaKey(a,ring),new GradientAlphaKey(a*.38f,ring+(1-ring)*.35f),new GradientAlphaKey(0,1)});
        halos[ink]=g;return g;
    }
    // The light falls on the hand: clear over the fingertips with their rings and numbers, darkening smoothly towards the
    // card's far corners, so the keys away from the hand sink into the card instead of reading as a bright strip. Radial
    // about the fingertips' middle, out to the farthest corner (VignetteRadius).
    const float VignetteDepth=.72f;
    static Gradient Vignette(Layout l)
    {
        var at=VignetteCentre(l);float clear=0;for(int k=1;k<=5;k++)clear=Mathf.Max(clear,Vector2.Distance(at,l.tips[k]));
        clear+=l.ring+2*BadgeRadius*l.unit;
        float t0=Mathf.Clamp(clear/Mathf.Max(1,VignetteRadius(l,at)),.2f,.85f),span=1-t0;
        var g=new Gradient();
        g.SetKeys(new[]{new GradientColorKey(Dark,0),new GradientColorKey(Dark,1)},
            new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(0,t0),new GradientAlphaKey(VignetteDepth*.1f,t0+span*.25f),new GradientAlphaKey(VignetteDepth*.38f,t0+span*.5f),new GradientAlphaKey(VignetteDepth*.72f,t0+span*.75f),new GradientAlphaKey(VignetteDepth,1)});
        return g;
    }
    static Vector2 VignetteCentre(Layout l){var c=Vector2.zero;for(int k=1;k<=5;k++)c+=l.tips[k];return c/5;}
    static float VignetteRadius(Layout l,Vector2 at){float r=0;foreach(var corner in new[]{Vector2.zero,new Vector2(l.size.x,0),new Vector2(0,l.size.y),l.size})r=Mathf.Max(r,Vector2.Distance(at,corner));return r;}
    static void Circle(Painter2D p,Vector2 c,float r){p.BeginPath();p.Arc(c,r,Angle.Degrees(0),Angle.Degrees(360));p.ClosePath();}

    // The overlay's layers, each drawn once per placement: the light on the hand and the soft edge round the photograph; a
    // group's glow (round its rings, at an onset's brightness, dimmed by the layer's opacity); the badges' discs; a group's
    // marks (its arrows, the rings round its nails and round its fingers' badges).
    enum Part { Shade,Glow,Badges,Marks }
    sealed class Layer : VisualElement
    {
        readonly PianistHand owner;readonly Part part;readonly int group;
        static readonly Color Under=new(.01f,.015f,.025f),Neutral=new(.62f,.72f,.82f);
        public Layer(PianistHand owner,Part part,int group=-1)
        {
            this.owner=owner;this.part=part;this.group=group;pickingMode=PickingMode.Ignore;this.StretchToParentSize();
            if(part is Part.Glow or Part.Marks)usageHints=UsageHints.DynamicColor;
            generateVisualContent+=Draw;
        }
        void Draw(MeshGenerationContext ctx)
        {
            var l=owner.layout;var f=owner.shown;var r=contentRect;if(l==null||f==null||r.width<20||r.height<20)return;
            if(group>=f.groups.Length)return;
            using var painting=Painting.Auto();
            var p=ctx.painter2D;p.lineCap=LineCap.Round;p.lineJoin=LineJoin.Round;float u=l.unit,badge=BadgeRadius*u;
            switch(part)
            {
                case Part.Shade:Shade(p,r,l,owner.vignette);break;
                case Part.Glow:
                    foreach(int k in f.groups[group].fingers)
                    {
                        var c=l.tips[k];float radius=l.ring*Reach;
                        p.fillGradient=FillGradient.MakeRadialGradient(Halo(Inks[group]),c,radius,c,AddressMode.Clamp);Circle(p,c,radius);p.Fill();
                    }
                    p.fillGradient=default;break;
                case Part.Badges:
                    for(int k=1;k<=5;k++)
                    {
                        p.fillColor=new Color(Under.r,Under.g,Under.b,.8f);Circle(p,l.badges[k],badge);p.Fill();
                        if(GroupOf(f,k)<0){p.strokeColor=new Color(Neutral.r,Neutral.g,Neutral.b,.3f);p.lineWidth=1.3f*u;Circle(p,l.badges[k],badge);p.Stroke();}
                    }
                    break;
                case Part.Marks:
                    var ink=Inks[group];
                    foreach(var a in l.arrows)if(a.group==group)DrawArrow(p,a,ink,u);
                    foreach(int k in f.groups[group].fingers)
                    {
                        p.strokeColor=new Color(Under.r,Under.g,Under.b,.5f);p.lineWidth=4.6f*u;Circle(p,l.tips[k],l.ring);p.Stroke();
                        p.strokeColor=new Color(ink.r,ink.g,ink.b,.96f);p.lineWidth=2.3f*u;Circle(p,l.tips[k],l.ring);p.Stroke();
                        p.strokeColor=new Color(ink.r,ink.g,ink.b,.9f);p.lineWidth=1.3f*u;Circle(p,l.badges[k],badge);p.Stroke();
                    }
                    break;
            }
        }
        // The light on the hand: the corners darkened round the fingertips, and the keys beside the hand falling into shadow
        // from just beyond the outer fingertips' numbers to the card's edges; then darker at the card's rim, so the cut-off
        // keys and the arm fade into the card. (The card is blended in linear colour, where a dark veil reads weaker than its
        // alpha: hence the depths.)
        static void Shade(Painter2D p,Rect r,Layout l,Gradient light)
        {
            void Box(Rect area){p.BeginPath();p.MoveTo(area.min);p.LineTo(new Vector2(area.xMax,area.yMin));p.LineTo(area.max);p.LineTo(new Vector2(area.xMin,area.yMax));p.ClosePath();p.Fill();}
            if(light!=null)
            {
                var at=VignetteCentre(l);
                p.fillGradient=FillGradient.MakeRadialGradient(light,at,VignetteRadius(l,at),at,AddressMode.Clamp);Box(new Rect(0,0,r.width,r.height));
            }
            if(edge==null)
            {
                edge=new Gradient();
                edge.SetKeys(new[]{new GradientColorKey(Dark,0),new GradientColorKey(Dark,1)},new[]{new GradientAlphaKey(.82f,0),new GradientAlphaKey(.3f,.45f),new GradientAlphaKey(0,1)});
                beside=new Gradient();
                beside.SetKeys(new[]{new GradientColorKey(Dark,0),new GradientColorKey(Dark,1)},new[]{new GradientAlphaKey(.8f,0),new GradientAlphaKey(.56f,.3f),new GradientAlphaKey(.24f,.65f),new GradientAlphaKey(0,1)});
            }
            void Band(Gradient g,Rect area,Vector2 from,Vector2 to){p.fillGradient=FillGradient.MakeLinearGradient(g,from,to,AddressMode.Clamp);Box(area);}
            float x0=float.MaxValue,x1=float.MinValue;for(int k=1;k<=5;k++){x0=Mathf.Min(x0,l.tips[k].x);x1=Mathf.Max(x1,l.tips[k].x);}
            float clear=l.ring+(2*BadgeRadius+6)*l.unit,left=x0-clear,right=x1+clear;
            if(left>4)Band(beside,new Rect(0,0,left,r.height),new Vector2(0,0),new Vector2(left,0));
            if(right<r.width-4)Band(beside,new Rect(right,0,r.width-right,r.height),new Vector2(r.width,0),new Vector2(right,0));
            float side=Mathf.Min(r.width*.09f,70),foot=Mathf.Min(r.height*.16f,90);
            Band(edge,new Rect(0,0,side,r.height),new Vector2(0,0),new Vector2(side,0));
            Band(edge,new Rect(r.width-side,0,side,r.height),new Vector2(r.width,0),new Vector2(r.width-side,0));
            Band(edge,new Rect(0,r.height-foot,r.width,foot),new Vector2(0,r.height),new Vector2(0,r.height-foot));
            p.fillGradient=default;
        }
        // A dark edge under the line and the head keeps the arrow clear over the white keys.
        static void DrawArrow(Painter2D p,in Arrow a,Color ink,float u)
        {
            float head=10*u;const float wide=.58f;
            Vector2 side=new(-a.dir.y,a.dir.x),stop=a.to-a.dir*head*.6f;
            for(int layer=0;layer<2;layer++)
            {
                p.strokeColor=layer==0?new Color(Under.r,Under.g,Under.b,.6f):ink;p.lineWidth=(layer==0?5.4f:2.3f)*u;
                p.BeginPath();p.MoveTo(a.from);p.BezierCurveTo(a.c1,a.c2,stop);p.Stroke();
            }
            p.BeginPath();p.MoveTo(a.to);p.LineTo(a.to-a.dir*head+side*head*wide);p.LineTo(a.to-a.dir*head-side*head*wide);p.ClosePath();
            p.strokeColor=new Color(Under.r,Under.g,Under.b,.6f);p.lineWidth=2.6f*u;p.Stroke();p.fillColor=ink;p.Fill();
        }
    }
}
