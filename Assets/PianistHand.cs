using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

// A pianist's hand for a Director cue that talks about fingering (the cue's hand): the hand seen from above, palm down, as
// it lies on the keys (a right hand has its thumb at the left), the finger numbers 1 (thumb) to 5 (little finger) on the
// fingers, and arrows from each group's label to its fingertips, which glow in the group's colour. With alternate the
// groups take turns at the hand's own notes (the onsets on the score track named for that hand, or the beats without
// one), so a rocking figure such as sixths on 1+4 then 2+5 is seen as it is heard; the song clock decides, so seeks and
// recordings agree. In the Torus view the card takes its own part of the screen (VisualizationViews.ShareScreen) and the
// torus moves aside. The hand is drawn once per figure (and when the card is resized); only the highlight layer repaints,
// when the turn passes and while an onset's flare fades.
[DefaultExecutionOrder(1117)]
public sealed class PianistHand : MonoBehaviour
{
    // A cue's hand: "right" or "left"; one to three groups of fingers (1 thumb to 5 little finger, none in two groups),
    // each with a label of up to 40 characters (the finger numbers when empty); alternate: the groups take turns.
    [Serializable] public sealed class Group { public int[] fingers;public string label; }
    [Serializable] public sealed class Figure { public string side;public Group[] groups;public bool alternate; }

    SongDirector director;MidiPlayer midi;VisualizationViews views;TutorialDirector tutorial;
    VisualElement root,card;Drawing drawing;Highlight highlight;Label heading;readonly Label[] labels=new Label[3];
    Figure shown;int shownCue=-2,revision=-1,turn=-2;int[] slots=Array.Empty<int>();double[] onsets=Array.Empty<double>();
    float alpha,placedAlpha=-1,flare;Rect placed=new(0,0,-1,-1),placedRoom=new(0,0,-1,-1);
    const float FadeSeconds=.45f,FlareSeconds=.32f;
    // Gold, sea green and violet, as PassageComparison's.
    static readonly Color[] Inks={new(1f,.78f,.36f),new(.42f,.86f,.8f),new(.8f,.56f,.98f)};

    public Figure Shown=>alpha>.5f?shown:null;
    // The group whose turn it is (-1: every group is lit), and how many turns the figure has.
    public int Turn=>turn;
    public int Turns=>onsets.Length;

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

    public void Bind(VisualElement ui)
    {
        root=ui;director=GetComponent<SongDirector>();midi=GetComponent<MidiPlayer>();views=GetComponent<VisualizationViews>();
        card=new VisualElement{name="pianist-hand",pickingMode=PickingMode.Ignore,usageHints=UsageHints.DynamicTransform};
        card.style.position=Position.Absolute;card.style.backgroundColor=new Color(.025f,.05f,.085f,.97f);
        card.style.borderTopLeftRadius=card.style.borderTopRightRadius=card.style.borderBottomLeftRadius=card.style.borderBottomRightRadius=14;
        card.style.borderLeftWidth=card.style.borderRightWidth=card.style.borderTopWidth=card.style.borderBottomWidth=1;
        card.style.borderLeftColor=card.style.borderRightColor=card.style.borderBottomColor=new Color(.5f,.72f,.92f,.2f);card.style.borderTopColor=new Color(.55f,.78f,.96f,.4f);
        drawing=new Drawing{pickingMode=PickingMode.Ignore};drawing.StretchToParentSize();
        highlight=new Highlight{pickingMode=PickingMode.Ignore,owner=this};highlight.StretchToParentSize();
        heading=new Label{pickingMode=PickingMode.Ignore,enableRichText=false};heading.style.position=Position.Absolute;heading.style.left=16;heading.style.top=12;
        heading.style.fontSize=11;heading.style.letterSpacing=2.5f;heading.style.color=new Color(.5f,.68f,.84f);
        card.Add(drawing);card.Add(highlight);card.Add(heading);
        for(int i=0;i<labels.Length;i++)
        {
            var l=new Label{pickingMode=PickingMode.Ignore,enableRichText=false};l.style.position=Position.Absolute;l.style.whiteSpace=WhiteSpace.Normal;
            l.style.unityFontStyleAndWeight=FontStyle.Bold;l.style.marginLeft=l.style.marginRight=l.style.marginTop=l.style.marginBottom=0;
            l.style.paddingLeft=l.style.paddingRight=l.style.paddingTop=l.style.paddingBottom=0;l.style.display=DisplayStyle.None;card.Add(l);labels[i]=l;
        }
        card.style.opacity=0;card.style.display=DisplayStyle.None;root.Add(card);
    }

    void LateUpdate()
    {
        if(card==null||director==null||root.panel==null)return;
        tutorial??=GetComponent<TutorialDirector>();
        if(revision!=director.Revision){revision=director.Revision;shownCue=-2;}
        var cue=director.CurrentCue;
        // Only while the story directs and no tutorial runs.
        var wanted=cue!=null&&Present(cue.hand)&&!(tutorial!=null&&tutorial.Playing)?cue.hand:null;
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
        FindOnsets();turn=-2;flare=0;slots=Slots(shown);
        heading.text=shown.side=="left"?"LEFT HAND":"RIGHT HAND";
        for(int i=0;i<labels.Length;i++)
        {
            var l=labels[i];bool used=i<shown.groups.Length;l.style.display=used?DisplayStyle.Flex:DisplayStyle.None;if(!used)continue;
            l.text=LabelText(shown.groups[i]);l.style.color=Inks[i];l.style.opacity=1;
            l.style.unityTextAlign=slots[i]==0?TextAnchor.MiddleRight:slots[i]==1?TextAnchor.MiddleLeft:TextAnchor.LowerCenter;
        }
        drawing.figure=shown;drawing.MarkDirtyRepaint();highlight.MarkDirtyRepaint();placed=placedRoom=new Rect(0,0,-1,-1);
    }

    // Whose turn it is: the onsets the song has passed, counted from the figure's first (the first group's); and how bright
    // that onset still is. Repaints only when either changes.
    void Follow()
    {
        int now=-1;float lift=0;
        if(shown.alternate&&midi!=null)
        {
            double t=midi.Position;int lo=0,hi=onsets.Length;
            while(lo<hi){int mid=(lo+hi)/2;if(onsets[mid]<=t)lo=mid+1;else hi=mid;}
            now=lo==0?0:(lo-1)%shown.groups.Length;
            if(lo>0&&!Main.ReducedMotion)lift=Mathf.Clamp01(1-(float)(t-onsets[lo-1])/FlareSeconds);
        }
        if(now!=turn){turn=now;for(int i=0;i<shown.groups.Length&&i<labels.Length;i++)labels[i].style.opacity=turn<0||turn==i?1:.4f;highlight.MarkDirtyRepaint();}
        if(lift!=flare){flare=lift;highlight.MarkDirtyRepaint();}
    }

    // The song times at which the turn passes, over this cue and the cues beside it that show the same figure: the hand's
    // own onsets, on the score track named for it ("Right hand"), a chord's notes a few milliseconds apart counted once;
    // without such a track, the beats.
    void FindOnsets()
    {
        onsets=Array.Empty<double>();var cues=director.Current?.cues;int index=director.CueIndex;
        if(!shown.alternate||cues==null||index<0||index>=cues.Length||midi==null)return;
        int first=index,last=index;
        while(first>0&&Same(cues[first-1].hand,shown)&&Math.Abs(cues[first-1].end-cues[first].start)<.002)first--;
        while(last+1<cues.Length&&Same(cues[last+1].hand,shown)&&Math.Abs(cues[last+1].start-cues[last].end)<.002)last++;
        double from=cues[first].start-.02,to=cues[last].end;var list=new List<double>();
        var score=midi.HarmonicPrepared;int track=HandTrack(score?.TrackNames,shown.side);
        if(track>=0&&score.Frames!=null)
            foreach(var frame in score.Frames)
            {
                if(!Struck(frame.Attacks,track))continue;
                double t=midi.AudioTime(frame.Time);
                if(t>=from&&t<=to&&(list.Count==0||t-list[list.Count-1]>=.035))list.Add(t);
            }
        var beats=midi.HarmonicCycles;
        if(list.Count==0&&beats!=null)
        {
            double Score(double t)=>midi.Recording!=null&&midi.Recording.Ready?midi.Recording.Alignment.ToMidi(t):t;
            for(double b=Math.Ceiling(beats.BeatAt(Score(from))-1e-6),end=beats.BeatAt(Score(to));b<=end&&list.Count<20000;b++)list.Add(midi.AudioTime(beats.SecondsAt(b)));
        }
        onsets=list.ToArray();
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

    // The card's size when it shares the screen: across the top above the torus in a portrait layout, at the left beside it
    // in a landscape one.
    Vector2 SharedSize()
    {
        float w=root.resolvedStyle.width,h=root.resolvedStyle.height;if(!float.IsFinite(w)||w<10)return Vector2.zero;
        if(views.Portrait){float width=Mathf.Min(w-32,820);return new Vector2(width,Mathf.Clamp(width*.62f,280,h*.32f));}
        float side=Mathf.Min(w*.4f,760);return new Vector2(side,Mathf.Min(side*.95f,h*.78f));
    }

    // In its slot when sharing the screen; otherwise near the top of the screen, over the scene. The card fits the hand and
    // its labels (no empty margins), and the labels follow the hand.
    void Place(Rect slot)
    {
        float w=root.resolvedStyle.width,h=root.resolvedStyle.height;if(!float.IsFinite(w)||w<10)return;
        Rect room;bool portrait=views!=null?views.Portrait:h>w;
        if(slot.width>10&&slot.height>10){var size=SharedSize();room=new Rect(slot.x,slot.y,slot.width,Mathf.Min(slot.height,size.y));}
        else if(h>w){float width=Mathf.Min(w*.9f,820);room=new Rect((w-width)/2,h*.06f,width,Mathf.Min(width*.62f,h*.34f));}
        else{float width=Mathf.Min(w*.34f,640);room=new Rect((w-width)/2,h*.08f,width,Mathf.Min(width*.95f,h*.66f));}
        if(room==placedRoom)return;
        placedRoom=room;
        // The layers fill the card inside its 1-pixel border; the labels are laid out in the same frame.
        var fit=Layout(new Vector2(room.width-2,room.height-2),shown,slots);
        float cw=Mathf.Min(room.width,Mathf.Max(260,fit.w+2*Pad+2)),ch=Mathf.Min(room.height,fit.h+Heading+10);
        // Beside the torus in a landscape layout the card is centred on the slot, but its foot stays clear of the captions
        // a horizontal recording shows along the bottom.
        float top=portrait?room.y:room.y+Mathf.Max(0,Mathf.Min((slot.height-ch)/2,h*.76f-room.y-ch));
        var at=new Rect(room.x+(room.width-cw)/2,top,cw,ch);placed=at;
        card.style.left=at.x;card.style.top=at.y;card.style.width=at.width;card.style.height=at.height;
        fit=Layout(new Vector2(at.width-2,at.height-2),shown,slots);
        for(int i=0;i<shown.groups.Length&&i<labels.Length;i++)
        {
            var l=labels[i];Vector2 a=Anchor(fit,slots[i]);float lw=LabelWidth(shown.groups[i],fit.labelRoom),lh=LabelSize*LabelLines;
            l.style.fontSize=LabelSize;l.style.width=lw;l.style.height=lh;
            l.style.left=slots[i]==0?a.x-8-lw:slots[i]==1?a.x+8:a.x-lw/2;l.style.top=slots[i]==2?a.y-4-lh:a.y-lh/2;
        }
    }

    // ---- The hand, in hand units: the palm's width at the knuckles is 1, y runs down and the fingers point up; a right hand
    // (a left hand is its mirror image). Proportions of a hand curved on the keys, seen from above: the middle finger the
    // longest, the ring finger a little longer than the index, the little finger short and set lower, the thumb lower still
    // and angled out, its tip about level with the other fingers' middle joints. ----
    static readonly Vector2[] Knuckle={default,default,new(-.352f,.04f),new(-.105f,0),new(.142f,.028f),new(.368f,.105f)};
    static readonly float[] Lean={0,0,-3,0,3.5f,8},Reach={0,0,.8f,.9f,.84f,.66f},Breadth={0,0,.236f,.246f,.228f,.196f};
    // Where the joints fall along a finger's visible length: a long first phalanx, the middle and end ones foreshortened.
    const float Pip=.56f,Dip=.8f,Cap=1.12f;
    static readonly Vector2[] Thumb={new(-.3f,.74f),new(-.44f,.56f),new(-.575f,.37f),new(-.655f,.215f),new(-.705f,.06f),new(-.73f,-.075f)};
    static readonly float[] ThumbHalf={.18f,.165f,.142f,.134f,.127f,.112f};
    // The back of the hand, from the forearm (which fades out) up to the webs between the fingers and round the thumb's root.
    static readonly Vector2[] Palm={new(.36f,1.36f),new(.375f,1.16f),new(.39f,1f),new(.445f,.78f),new(.5f,.42f),new(.475f,.06f),new(.265f,-.13f),new(.025f,-.21f),
        new(-.225f,-.2f),new(-.46f,-.05f),new(-.5f,.12f),new(-.565f,.25f),new(-.6f,.46f),new(-.525f,.78f),new(-.4f,1f),new(-.385f,1.16f),new(-.37f,1.36f)};
    const float Wrist=.92f;
    static readonly Vector2[] Webs={new(.265f,-.13f),new(.025f,-.21f),new(-.225f,-.2f)};
    static Vector2 Dir(int k){if(k==1)return (Thumb[5]-Thumb[4]).normalized;float a=Lean[k]*Mathf.Deg2Rad;return new Vector2(Mathf.Sin(a),-Mathf.Cos(a));}
    static Vector2 Tip(int k)=>k==1?Thumb[5]+Dir(1)*ThumbHalf[5]*Cap:Knuckle[k]+Dir(k)*Reach[k];
    static float TipHalf(int k)=>k==1?ThumbHalf[5]:Breadth[k]*.5f*.82f;
    // A finger's stations from inside the palm to the shoulder of its tip, with their half widths.
    static void Stations(int k,List<Vector2> chain,List<float> half)
    {
        chain.Clear();half.Clear();if(k==1){chain.AddRange(Thumb);half.AddRange(ThumbHalf);return;}
        var d=Dir(k);float w=Breadth[k]*.5f,L=Reach[k],hs=w*.82f;
        void At(float s,float h){chain.Add(Knuckle[k]+d*s);half.Add(h);}
        At(-.3f,w);At(0,w);At(.25f*L,w*.95f);At(Pip*L,w*.955f);At(.68f*L,w*.9f);At(Dip*L,w*.88f);At(L-hs*Cap,hs);
    }

    // Label anchors (a right hand): beside the index fingertip (the label ends there), above the little finger (it starts
    // there), and above the middle finger for a third group. The extent of the hand, and room for the card's heading.
    static readonly Vector2 ThumbSide=new(-.66f,-.96f),LittleSide=new(.46f,-1f),Above=new(-.12f,-1.2f);
    const float HandLeft=-.88f,HandRight=.58f,HandTop=-.94f,HandBottom=1.22f,Heading=28,Pad=14;
    // Where the hand sits in a card: hand units to pixels (s pixels to the unit, mirrored for a left hand).
    struct Fit
    {
        public float s,mirror,labelRoom,w,h;public Vector2 o;
        public Vector2 At(Vector2 u)=>o+new Vector2(u.x*mirror,u.y)*s;
        public Vector2 Toward(Vector2 u)=>new(u.x*mirror,u.y);
    }
    // The largest hand that fits the card with its labels beside it (and above, for a third group), centred.
    static Fit Layout(Vector2 size,Figure f,int[] slots)
    {
        var fit=new Fit{mirror=f.side=="left"?-1:1,labelRoom=Mathf.Clamp(size.x*.3f,120,260)};
        bool left=false,right=false,above=false;float lw=0,rw=0;
        for(int i=0;i<slots.Length&&i<f.groups.Length;i++){int k=slots[i];left|=k==0;right|=k==1;above|=k==2;if(k==0)lw=LabelWidth(f.groups[i],fit.labelRoom);if(k==1)rw=LabelWidth(f.groups[i],fit.labelRoom);}
        Vector2 la=fit.mirror>0?ThumbSide:new Vector2(-LittleSide.x,LittleSide.y),ra=fit.mirror>0?LittleSide:new Vector2(-ThumbSide.x,ThumbSide.y);
        float hl=fit.mirror>0?HandLeft:-HandRight,hr=fit.mirror>0?HandRight:-HandLeft,width=size.x-2*Pad,height=size.y-Heading-8;
        (float w,float h,float l,float t) Extent(float s)
        {
            const float lh=LabelSize*LabelLines;
            float l=Mathf.Max(-hl*s,left?-la.x*s+lw+8:0),r=Mathf.Max(hr*s,right?ra.x*s+rw+8:0);
            float t=Mathf.Max(-HandTop*s,Mathf.Max(left?-la.y*s+lh/2:0,Mathf.Max(right?-ra.y*s+lh/2:0,above?-Above.y*s+lh+4:0)));
            return (l+r,t+HandBottom*s,l,t);
        }
        float lo=0,hi=300;
        for(int i=0;i<24;i++){float mid=(lo+hi)/2;var e=Extent(mid);if(e.w<=width&&e.h<=height)lo=mid;else hi=mid;}
        var x=Extent(lo);fit.s=lo;fit.w=x.w;fit.h=x.h;
        fit.o=new Vector2(Pad+(width-x.w)/2+x.l,Heading+Mathf.Max(0,(height-x.h)/2)+x.t);
        return fit;
    }
    // Labels: the group's words (or its finger numbers), on one line when they fit, two at most (40 characters).
    const float LabelSize=17,LabelLines=2.6f;
    static string LabelText(Group g)=>string.IsNullOrWhiteSpace(g.label)?string.Join(" + ",g.fingers):g.label;
    static float LabelWidth(Group g,float room)=>Mathf.Min(room,TextWidth(LabelText(g))*LabelSize+10);
    // A bold label's width in font sizes, estimated from the panel's sans face.
    static float TextWidth(string text)
    {
        float w=0;
        foreach(char c in text)w+=c is 'i' or 'l' or 'j' or '\'' or '.' or ',' or '!' or '|' or ':' or ';'?.3f:c is 't' or 'f' or 'r' or 'I'?.37f:c is 'm' or 'w'?.9f:c is 'M' or 'W'?.95f:
            char.IsUpper(c)?.72f:c==' '?.3f:.58f;
        return w;
    }
    static Vector2 Anchor(Fit fit,int slot)=>fit.At(slot==2?Above:(slot==0)==(fit.mirror>0)?ThumbSide:LittleSide);
    // Which side each group's label takes: the group whose fingertips lie furthest left (as drawn) at the left, the one
    // furthest right at the right, a third above the hand.
    static int[] Slots(Figure f)
    {
        int n=f.groups.Length;var mean=new float[n];float mirror=f.side=="left"?-1:1;
        for(int i=0;i<n;i++){float x=0;foreach(int k in f.groups[i].fingers)x+=Tip(k).x*mirror;mean[i]=x/f.groups[i].fingers.Length;}
        var result=new int[n];
        if(n==1){result[0]=mean[0]<Tip(3).x*mirror?0:1;return result;}
        var order=new int[n];for(int i=0;i<n;i++)order[i]=i;Array.Sort(order,(a,b)=>mean[a].CompareTo(mean[b]));
        result[order[0]]=0;result[order[n-1]]=1;if(n==3)result[order[1]]=2;
        return result;
    }

    // A smooth closed outline through the points (Catmull-Rom, as cubic Béziers).
    static void Loop(Painter2D p,Vector2[] u,Fit fit)
    {
        int n=u.Length;p.MoveTo(fit.At(u[0]));
        for(int i=0;i<n;i++){Vector2 a=u[(i+n-1)%n],b=u[i],c=u[(i+1)%n],d=u[(i+2)%n];p.BezierCurveTo(fit.At(b+(c-a)/6),fit.At(c-(d-b)/6),fit.At(c));}
        p.ClosePath();
    }
    // A finger's outline: smooth sides through its stations and a rounded tip; every finger is wound the same way, so a fill
    // of the palm and the fingers together is their union.
    static void Limb(Painter2D p,List<Vector2> chain,List<float> half,Fit fit)
    {
        int n=chain.Count;
        Vector2 D(int i)=>(chain[Mathf.Min(i+1,n-1)]-chain[Mathf.Max(i-1,0)]).normalized;
        Vector2 N(int i){var d=D(i);return new Vector2(-d.y,d.x);}
        Vector2 R(int i){i=Mathf.Clamp(i,0,n-1);return chain[i]+N(i)*half[i];}
        Vector2 L(int i){i=Mathf.Clamp(i,0,n-1);return chain[i]-N(i)*half[i];}
        p.MoveTo(fit.At(R(0)));
        for(int i=0;i<n-1;i++)p.BezierCurveTo(fit.At(R(i)+(R(i+1)-R(i-1))/6),fit.At(R(i+1)-(R(i+2)-R(i))/6),fit.At(R(i+1)));
        Vector2 dl=D(n-1),nl=N(n-1),apex=chain[n-1]+dl*half[n-1]*Cap;float h=half[n-1];const float k=.5523f;
        p.BezierCurveTo(fit.At(R(n-1)+dl*k*h*Cap),fit.At(apex+nl*k*h),fit.At(apex));
        p.BezierCurveTo(fit.At(apex-nl*k*h),fit.At(L(n-1)+dl*k*h*Cap),fit.At(L(n-1)));
        for(int i=n-1;i>0;i--)p.BezierCurveTo(fit.At(L(i)+(L(i-1)-L(i+1))/6),fit.At(L(i-1)-(L(i-2)-L(i))/6),fit.At(L(i-1)));
        p.ClosePath();
    }
    static void Circle(Painter2D p,Vector2 c,float r){p.BeginPath();p.Arc(c,r,Angle.Degrees(0),Angle.Degrees(360));p.ClosePath();}
    // A soft round light or shade: strength at the centre, nothing at the rim. A halo is strongest at half its radius (round
    // a fingertip) and lighter over the finger itself.
    static void Glow(Painter2D p,Vector2 c,float r,Color ink,float strength,bool halo=false)
    {
        p.fillGradient=FillGradient.MakeRadialGradient(Fade(ink,strength,halo),c,r,c,AddressMode.Clamp);Circle(p,c,r);p.Fill();p.fillGradient=default;
    }
    // Radial fades are Gradients with alpha keys (a FillGradient made from two Colors ignores their alpha), made on the main
    // thread and kept, one per colour and strength (in twentieths).
    static readonly Dictionary<(Color,int,bool),Gradient> fades=new();
    static Gradient Fade(Color ink,float strength,bool halo)
    {
        int level=Mathf.Clamp(Mathf.RoundToInt(strength*20),0,20);var key=(ink,level,halo);if(fades.TryGetValue(key,out var g))return g;
        float a=level/20f;g=new Gradient();
        g.SetKeys(new[]{new GradientColorKey(ink,0),new GradientColorKey(ink,1)},halo?new[]{new GradientAlphaKey(a*.45f,0),new GradientAlphaKey(a,.5f),new GradientAlphaKey(a*.35f,.75f),new GradientAlphaKey(0,1)}
            :new[]{new GradientAlphaKey(a,0),new GradientAlphaKey(a*.45f,.5f),new GradientAlphaKey(0,1)});
        fades[key]=g;return g;
    }
    static Gradient Ramp(Color[] colors,float[] at,float fadeFrom)
    {
        var keys=new GradientColorKey[colors.Length];for(int i=0;i<colors.Length;i++)keys[i]=new GradientColorKey(colors[i],at[i]);
        var g=new Gradient();g.SetKeys(keys,new[]{new GradientAlphaKey(1,0),new GradientAlphaKey(1,fadeFrom),new GradientAlphaKey(.42f,fadeFrom+(1-fadeFrom)*.4f),new GradientAlphaKey(0,1)});return g;
    }

    // The hand itself, drawn once per figure: skin, outline, the light on the back of the hand and the knuckles, the joints'
    // creases, the nails and the finger numbers.
    sealed class Drawing : VisualElement
    {
        public Figure figure;
        // Made on the main thread when first drawn (a Gradient is a native object).
        static Gradient skin,outline;
        static readonly Color NailInk=new(.98f,.92f,.9f),Crease=new(.45f,.32f,.3f),Shade=new(.45f,.3f,.27f),Number=new(.2f,.25f,.33f,.95f);
        readonly List<Vector2> chain=new();readonly List<float> half=new();
        public Drawing(){generateVisualContent+=Draw;}
        void Draw(MeshGenerationContext ctx)
        {
            if(figure==null)return;var fit=Layout(contentRect.size,figure,Slots(figure));float s=fit.s;if(s<20)return;
            var p=ctx.painter2D;p.lineJoin=LineJoin.Round;p.lineCap=LineCap.Round;
            // The forearm fades out from the wrist to the bottom of the hand's extent.
            float fade=(Wrist-HandTop)/(HandBottom-HandTop);
            skin??=Ramp(new[]{new Color(.96f,.89f,.83f),new Color(.93f,.85f,.78f),new Color(.86f,.77f,.71f)},new[]{0,.45f,fade},fade);
            outline??=Ramp(new[]{new Color(.42f,.31f,.29f),new Color(.4f,.3f,.28f)},new[]{0,fade},fade);
            Vector2 top=fit.At(new Vector2(0,HandTop)),bottom=fit.At(new Vector2(0,HandBottom));
            // The whole hand as one path; the outline is stroked under the fill, so it shows round the hand and between the
            // fingers, not where the shapes overlap.
            p.BeginPath();Loop(p,Palm,fit);
            for(int k=1;k<=5;k++){Stations(k,chain,half);Limb(p,chain,half,fit);}
            p.lineWidth=Mathf.Max(2,s*.014f);p.strokeFillGradient=FillGradient.MakeLinearGradient(outline,top,bottom,AddressMode.Clamp);p.Stroke();p.strokeFillGradient=default;
            // Filled twice: one fill leaves faint seams where a shape's edge lies inside another (each shape's edge is smoothed).
            p.fillGradient=FillGradient.MakeLinearGradient(skin,top,bottom,AddressMode.Clamp);p.Fill();p.Fill();p.fillGradient=default;
            // Light and shade on the back of the hand, clipped to the palm (a clip of the overlapping shapes would be
            // even-odd): a sheen in the middle, shade along the little finger's side, by the thumb and in the webs, and a
            // highlight on each knuckle. Then a soft highlight down each finger, narrower than the finger.
            p.BeginPath();Loop(p,Palm,fit);p.PushClip();
            Glow(p,fit.At(new Vector2(-.05f,.36f)),.62f*s,Color.white,.22f);
            Glow(p,fit.At(new Vector2(.66f,.6f)),.5f*s,Shade,.22f);
            Glow(p,fit.At(new Vector2(-.56f,.3f)),.24f*s,Shade,.16f);
            foreach(var web in Webs)Glow(p,fit.At(web+new Vector2(0,.03f)),.085f*s,Shade,.32f);
            for(int k=2;k<=5;k++)Glow(p,fit.At(Knuckle[k]+Dir(k)*.015f),.1f*s,Color.white,.4f);
            p.PopClip();
            p.strokeColor=new Color(1,1,1,.13f);
            for(int k=1;k<=5;k++)
            {
                p.lineWidth=(k==1?ThumbHalf[3]:Breadth[k]*.5f)*.7f*s;p.BeginPath();
                if(k==1){p.MoveTo(fit.At(Thumb[3]));p.LineTo(fit.At(Thumb[4]));p.LineTo(fit.At(Thumb[5]));}
                else{var d=Dir(k);p.MoveTo(fit.At(Knuckle[k]+d*.06f));p.LineTo(fit.At(Knuckle[k]+d*(Reach[k]-.17f)));}
                p.Stroke();
            }
            // The joints' creases across each finger, the thumb's at its joint.
            p.lineWidth=Mathf.Max(1,s*.0055f);
            for(int k=1;k<=5;k++)
            {
                Vector2 d=Dir(k),n=new(-d.y,d.x);
                if(k==1){d=(Thumb[4]-Thumb[3]).normalized;n=new Vector2(-d.y,d.x);Fold(p,fit,Thumb[4],d,n,ThumbHalf[4]*.62f,.4f);Fold(p,fit,Thumb[4]-d*.02f,d,n,ThumbHalf[4]*.5f,.28f);continue;}
                float w=Breadth[k]*.5f,L=Reach[k];
                Fold(p,fit,Knuckle[k]+d*(Pip*L-.012f),d,n,w*.6f,.42f);Fold(p,fit,Knuckle[k]+d*(Pip*L+.012f),d,n,w*.55f,.34f);
                Fold(p,fit,Knuckle[k]+d*Dip*L,d,n,w*.5f,.32f);
            }
            for(int k=1;k<=5;k++)Nail(p,fit,k);
            float size=Mathf.Clamp(.19f*s,13,34);
            for(int k=1;k<=5;k++)
            {
                Vector2 at=k==1?(Thumb[3]+Thumb[4])*.5f:Knuckle[k]+Dir(k)*(.3f*Reach[k]);string text=k.ToString();
                ctx.DrawText(text,fit.At(at)-new Vector2(text.Length*size*.29f,size*.62f),size,Number);
            }
        }
        static void Fold(Painter2D p,Fit fit,Vector2 c,Vector2 d,Vector2 n,float reach,float alpha)
        {
            p.strokeColor=new Color(Crease.r,Crease.g,Crease.b,alpha);p.BeginPath();
            p.MoveTo(fit.At(c-n*reach));p.QuadraticCurveTo(fit.At(c+d*.022f),fit.At(c+n*reach));p.Stroke();
        }
        // A nail near the tip, seen from above on a curved finger (so short); the thumb lies on its side on the keys, so its
        // nail is narrower and turned outward.
        static void Nail(Painter2D p,Fit fit,int k)
        {
            Vector2 d=Dir(k),n=new(-d.y,d.x);float h=TipHalf(k),length=k==5?.115f:k==1?.12f:.13f,a=h*(k==1?.5f:.66f),b=h*(k==1?.42f:.56f);
            Vector2 shift=k==1?-n*h*.18f:Vector2.zero,tip=Tip(k)-d*.028f+shift,root=Tip(k)-d*(length+.028f)+shift;
            p.BeginPath();p.MoveTo(fit.At(root-n*b));
            p.BezierCurveTo(fit.At(root-n*b+d*length*.55f),fit.At(tip-n*a-d*length*.2f),fit.At(tip-n*a*.92f));
            p.BezierCurveTo(fit.At(tip-n*a*.6f+d*.012f),fit.At(tip+n*a*.6f+d*.012f),fit.At(tip+n*a*.92f));
            p.BezierCurveTo(fit.At(tip+n*a-d*length*.2f),fit.At(root+n*b+d*length*.55f),fit.At(root+n*b));
            p.BezierCurveTo(fit.At(root+n*b*.5f-d*.03f),fit.At(root-n*b*.5f-d*.03f),fit.At(root-n*b));
            p.ClosePath();p.fillColor=NailInk;p.Fill();
            p.strokeColor=new Color(.55f,.4f,.38f,.5f);p.lineWidth=Mathf.Max(1,fit.s*.005f);p.Stroke();
        }
    }

    // What is lit: a glow on each fingertip of the groups whose turn it is (brighter just after an onset), a ring on every
    // named fingertip, and an arrow from each group's label to each of its fingertips, arriving along the finger.
    sealed class Highlight : VisualElement
    {
        public PianistHand owner;
        public Highlight(){generateVisualContent+=Draw;}
        void Draw(MeshGenerationContext ctx)
        {
            var f=owner?.shown;if(f==null||owner.slots.Length!=f.groups.Length)return;
            var fit=Layout(contentRect.size,f,owner.slots);float s=fit.s;if(s<20)return;
            var p=ctx.painter2D;p.lineCap=LineCap.Round;p.lineJoin=LineJoin.Round;
            for(int i=0;i<f.groups.Length;i++)
            {
                bool lit=owner.turn<0||owner.turn==i;var ink=Inks[i];float lift=lit&&owner.turn>=0?owner.flare:0;Vector2 from=Anchor(fit,owner.slots[i]);
                foreach(int k in f.groups[i].fingers)
                {
                    Vector2 tip=fit.At(Tip(k)),d=fit.Toward(Dir(k)),pad=tip-d*(.075f*s);
                    if(lit)Glow(p,pad,(.26f+.08f*lift)*s,ink,.7f+.3f*lift,true);
                    p.strokeColor=new Color(ink.r,ink.g,ink.b,lit?.95f:.35f);p.lineWidth=lit?2.4f:1.6f;Circle(p,pad,.115f*s);p.Stroke();
                    Arrow(p,from,tip+d*(.07f*s),d,s,new Color(ink.r,ink.g,ink.b,lit?1:.3f),owner.slots[i]);
                }
            }
        }
        static void Arrow(Painter2D p,Vector2 from,Vector2 to,Vector2 d,float s,Color ink,int slot)
        {
            // Out of the label toward the hand, as far as the way across needs (a fingertip just below its label gets a plain
            // curve), then in along the finger.
            Vector2 leave=slot==0?Vector2.right:slot==1?Vector2.left:new Vector2(0,1);   // panel y runs down
            float reach=Mathf.Clamp((slot==2?Mathf.Abs(to.y-from.y):Mathf.Abs(to.x-from.x))*.55f,.05f*s,.32f*s);
            // A long arrow from the right rises a little and one from the left dips, so two that cross the hand (1+4 against
            // 2+5) cross once instead of running together.
            Vector2 c1=from+leave*reach+new Vector2(0,(slot==1?-.4f:slot==0?.25f:0)*reach),c2=to+d*(.3f*s),dir=(to-c2).normalized,side=new(-dir.y,dir.x);
            float head=Mathf.Clamp(.055f*s,8,14);
            var under=new Color(.02f,.04f,.07f,.7f*ink.a);
            for(int layer=0;layer<2;layer++)
            {
                p.strokeColor=layer==0?under:ink;p.lineWidth=layer==0?5.5f:2.2f;
                p.BeginPath();p.MoveTo(from);p.BezierCurveTo(c1,c2,to-dir*head*.6f);p.Stroke();
            }
            p.BeginPath();p.MoveTo(to);p.LineTo(to-dir*head+side*head*.55f);p.LineTo(to-dir*head-side*head*.55f);p.ClosePath();
            p.strokeColor=under;p.lineWidth=3;p.Stroke();p.fillColor=ink;p.Fill();
        }
    }
}
