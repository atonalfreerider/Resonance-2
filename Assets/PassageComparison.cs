using System;
using UnityEngine;
using UnityEngine.UIElements;

// Two passages of the song side by side, for a Director cue that makes a point by comparison (compareFirst /
// compareSecond, in song seconds): each passage is a wheel that turns once over its own length, starting at
// twelve o'clock. What the second leaves out of the first (compareHighlight) is lit on the first wheel, and the
// place in the second where it is missing (compareMissingAt) is cut open on the second. The parts the two share
// carry the same ink on both wheels and are joined across, so "A′ = A without bars 6–13" is seen, not told.
// While the second passage plays, a ghost playhead on the first wheel shows where the music is in the first
// (compareFollow); when the second skips, the ghost leaps across the lit span and the cut flashes.
// In the Torus view the card takes its own part of the screen (VisualizationViews.ShareScreen) and the torus
// moves aside. The wheels are drawn once per cue (and when the card is resized); only the playhead layer
// repaints while the card is shown, and nothing draws while it is hidden.
[DefaultExecutionOrder(1115)]
public sealed class PassageComparison : MonoBehaviour
{
    SongDirector director;MidiPlayer midi;VisualizationViews views;VisualElement root,card;Wheels wheels;Playhead head;Label title,note;
    float alpha;int cueShown=-2;Rect placed=new(0,0,-1,-1);
    // The ghost's leap: from and to (angles on the first wheel), and how long ago it began (negative: none).
    float leapFrom,leapTo,leapAge=-1;double ghostLast=double.NaN;
    const float FadeSeconds=.45f,LeapSeconds=1.1f,FlashSeconds=1.8f;
    static readonly Color Ink=new(.42f,.82f,.78f),Shared=new(.36f,.62f,.86f),Lit=new(1f,.78f,.36f),Added=new(.78f,.52f,.95f),Faint=new(.55f,.7f,.8f,.35f),Words=new(.9f,.95f,1f);

    public SongDirector.Cue Shown=>alpha>.5f?Active:null;
    SongDirector.Cue Active{get{var c=director!=null?director.CurrentCue:null;return c!=null&&c.compareFirst!=null&&c.compareFirst.Length==2&&c.compareSecond!=null&&c.compareSecond.Length==2?c:null;}}

    public void Bind(VisualElement ui)
    {
        root=ui;director=GetComponent<SongDirector>();midi=GetComponent<MidiPlayer>();views=GetComponent<VisualizationViews>();
        card=new VisualElement{name="passage-comparison",pickingMode=PickingMode.Ignore};
        card.style.position=Position.Absolute;card.style.backgroundColor=new Color(.025f,.05f,.085f,.97f);
        card.style.borderTopLeftRadius=card.style.borderTopRightRadius=card.style.borderBottomLeftRadius=card.style.borderBottomRightRadius=14;
        card.style.borderLeftWidth=card.style.borderRightWidth=card.style.borderTopWidth=card.style.borderBottomWidth=1;
        card.style.borderLeftColor=card.style.borderRightColor=card.style.borderBottomColor=new Color(.5f,.72f,.92f,.2f);card.style.borderTopColor=new Color(.55f,.78f,.96f,.4f);
        title=new Label{pickingMode=PickingMode.Ignore};title.style.position=Position.Absolute;title.style.left=0;title.style.right=0;title.style.top=10;
        title.style.unityTextAlign=TextAnchor.UpperCenter;title.style.fontSize=17;title.style.color=Words;title.style.unityFontStyleAndWeight=FontStyle.Bold;
        note=new Label{pickingMode=PickingMode.Ignore};note.style.position=Position.Absolute;note.style.left=0;note.style.right=0;note.style.bottom=10;
        note.style.unityTextAlign=TextAnchor.LowerCenter;note.style.fontSize=13;note.style.color=new Color(.75f,.84f,.92f);
        wheels=new Wheels{pickingMode=PickingMode.Ignore};wheels.StretchToParentSize();
        head=new Playhead{pickingMode=PickingMode.Ignore,owner=this};head.StretchToParentSize();head.usageHints=UsageHints.DynamicTransform;
        card.Add(wheels);card.Add(head);card.Add(title);card.Add(note);
        card.style.opacity=0;card.style.display=DisplayStyle.None;root.Add(card);
    }

    void LateUpdate()
    {
        if(card==null||director==null)return;
        var cue=director.Directing?Active:null;
        float target=cue!=null?1:0,step=RecordingMode.Step;
        if(target>0&&director.CueIndex!=cueShown){bool same=cueShown>=0&&SameComparison(cue,director.Current?.cues,cueShown);cueShown=director.CueIndex;Prepare(cue,same);}
        // In the Torus view the card asks for its own part of the screen every frame it wants to show; it keeps
        // the place it has while it fades out.
        Rect slot=views!=null&&views.SharesScreen?views.ShareScreen(target>0,SharedSize()):new Rect(0,0,-1,-1);
        if(alpha==target&&target==0){if(card.style.display!=DisplayStyle.None)card.style.display=DisplayStyle.None;placed=new Rect(0,0,-1,-1);return;}
        if(target>0||placed.width<0)Place(slot);
        alpha=Mathf.MoveTowards(alpha,target,Main.ReducedMotion?1:step/FadeSeconds);
        card.style.display=DisplayStyle.Flex;card.style.opacity=alpha;
        if(leapAge>=0){leapAge+=step;if(leapAge>FlashSeconds)leapAge=-1;}
        Follow(cue??head.cue);
        if(alpha>0)head.MarkDirtyRepaint();
        if(alpha==0){cueShown=-2;ghostLast=double.NaN;leapAge=-1;}
    }

    // Two cues in a row with the same passages keep the ghost's place (a cue that only changes the words).
    static bool SameComparison(SongDirector.Cue cue,SongDirector.Cue[] cues,int index)
    {
        if(cues==null||index<0||index>=cues.Length)return false;var other=cues[index];
        return other.compareFirst!=null&&other.compareSecond!=null&&other.compareFirst.Length==2&&other.compareSecond.Length==2&&
            other.compareFirst[0]==cue.compareFirst[0]&&other.compareFirst[1]==cue.compareFirst[1]&&other.compareSecond[0]==cue.compareSecond[0]&&other.compareSecond[1]==cue.compareSecond[1];
    }

    // Where the second passage's time is in the first, through the cue's follow knots (the last run holding
    // the time wins, so a run that starts where another ends takes over there); NaN outside every run.
    public static double FollowTime(SongDirector.Cue c,double now)
    {
        var k=c?.compareFollow;var runs=c?.compareFollowRuns;if(k==null||runs==null)return double.NaN;
        int at=0;double found=double.NaN;
        foreach(int n in runs)
        {
            if(n<2||at+2*n>k.Length)break;
            if(now>=k[at]&&now<=k[at+2*(n-1)])
                for(int i=0;i<n-1;i++){double a=k[at+2*i],b=k[at+2*i+2];if(now>=a&&now<=b){double f=b>a?(now-a)/(b-a):0;found=k[at+2*i+1]+f*(k[at+2*i+3]-k[at+2*i+1]);break;}}
            at+=2*n;
        }
        return found;
    }
    // A forward jump of the ghost while the song plays is a skip: the ghost leaps across it and the cut flashes.
    void Follow(SongDirector.Cue cue)
    {
        if(cue==null||midi==null){ghostLast=double.NaN;return;}
        double ghost=FollowTime(cue,midi.Position);
        if(!double.IsNaN(ghost)&&!double.IsNaN(ghostLast)&&ghost-ghostLast>1&&midi.IsPlaying)
        {
            double a0=cue.compareFirst[0],a1=cue.compareFirst[1];
            leapFrom=Turn(ghostLast,a0,a1);leapTo=Turn(ghost,a0,a1);leapAge=0;
        }
        ghostLast=ghost;
    }

    // The card's size when it shares the screen: across the top above the torus in a portrait layout, at the left
    // beside it in a landscape one.
    Vector2 SharedSize()
    {
        float w=root.resolvedStyle.width,h=root.resolvedStyle.height;if(!float.IsFinite(w)||w<10)return Vector2.zero;
        if(views.Portrait){float width=Mathf.Min(w-32,1000);return new Vector2(width,Mathf.Clamp(width*.5f,260,h*.36f));}
        float side=Mathf.Min(w*.46f,900);return new Vector2(side,Mathf.Min(side*.62f,h*.8f));
    }

    // In its slot when sharing the screen, otherwise the upper half of the screen, over the scene.
    void Place(Rect slot)
    {
        float w=root.resolvedStyle.width,h=root.resolvedStyle.height;if(float.IsNaN(w)||w<10)return;
        Rect at;
        if(slot.width>10&&slot.height>10)
        {
            var size=SharedSize();float width=Mathf.Min(slot.width,size.x),height=Mathf.Min(slot.height,size.y);
            at=new Rect(slot.x+(slot.width-width)/2,views.Portrait?slot.y:slot.y+(slot.height-height)/2,width,height);
        }
        else{float width=Mathf.Min(w*.94f,980f),height=Mathf.Min(width*.62f,h*.46f);at=new Rect((w-width)/2,h*(h>w?.06f:.08f),width,height);}
        if(at==placed)return;
        placed=at;card.style.left=at.x;card.style.top=at.y;card.style.width=at.width;card.style.height=at.height;
    }

    void Prepare(SongDirector.Cue cue,bool keepGhost)
    {
        string[] labels=cue.compareLabels??Array.Empty<string>();
        string L(int i,string fallback)=>i<labels.Length&&!string.IsNullOrWhiteSpace(labels[i])?labels[i]:fallback;
        title.text=L(4,"");note.text=L(5,"");
        wheels.Set(cue,L(0,"First"),L(1,"Second"),L(2,""),L(3,""),L(6,""));
        head.cue=cue;
        if(!keepGhost){ghostLast=double.NaN;leapAge=-1;}
    }

    // Angle of time t on a wheel spanning [s0,s1): twelve o'clock, clockwise.
    static float Turn(double t,double s0,double s1)=>(float)(-90+360*(t-s0)/Math.Max(1e-6,s1-s0));
    static Vector2 Polar(Vector2 c,float r,float degrees){float a=degrees*Mathf.Deg2Rad;return c+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*r;}
    static float Ease(float x){x=Mathf.Clamp01(x);return x*x*(3-2*x);}

    // Each wheel's centre and radius inside the card: side by side in a wide card, the first above the second in
    // a tall one.
    static (Vector2 c,float r) Wheel(Rect box,int index)
    {
        if(box.width>=box.height*1.15f){float r=Mathf.Min(box.width*.2f,box.height*.31f);return (new Vector2(box.width*(index==0?.27f:.73f),box.height*.53f),r);}
        float rs=Mathf.Min(box.width*.3f,box.height*.17f);return (new Vector2(box.width*.5f,box.height*(index==0?.3f:.71f)),rs);
    }
    static float Band(float r)=>Mathf.Max(8,r*.16f);
    static void Arc(Painter2D p,Vector2 c,float r,float from,float to,Color color,float width)
    {
        if(to-from<.2f)return;p.strokeColor=color;p.lineWidth=width;p.lineCap=LineCap.Butt;p.BeginPath();p.Arc(c,r,Angle.Degrees(from),Angle.Degrees(to));p.Stroke();
    }
    static void Line(Painter2D p,Vector2 a,Vector2 b,Color color,float width){p.strokeColor=color;p.lineWidth=width;p.BeginPath();p.MoveTo(a);p.LineTo(b);p.Stroke();}
    static void Write(MeshGenerationContext ctx,string text,Vector2 centre,float size,Color color)
    {
        if(string.IsNullOrEmpty(text))return;ctx.DrawText(text,centre-new Vector2(text.Length*size*.27f,size*.62f),size,color);
    }

    sealed class Wheels : VisualElement
    {
        SongDirector.Cue cue;string first,second,lit,missing,added;
        public Wheels(){generateVisualContent+=Draw;}
        public void Set(SongDirector.Cue c,string a,string b,string highlight,string gap,string extra){cue=c;first=a;second=b;lit=highlight;missing=gap;added=extra;MarkDirtyRepaint();}
        void Draw(MeshGenerationContext ctx)
        {
            if(cue==null)return;var box=contentRect;if(box.width<50)return;var p=ctx.painter2D;
            double a0=cue.compareFirst[0],a1=cue.compareFirst[1],b0=cue.compareSecond[0],b1=cue.compareSecond[1];
            bool hasCut=cue.compareHighlight!=null&&cue.compareHighlight.Length==2;
            double h0=hasCut?cue.compareHighlight[0]:a1,h1=hasCut?cue.compareHighlight[1]:a1,m=cue.compareMissingAt>=0?cue.compareMissingAt:b1;
            // what the second adds that the first does not have (an outro): its own ink at the end of the second wheel
            bool hasAdded=cue.compareAdded!=null&&cue.compareAdded.Length==2;double e0=hasAdded?Math.Max(m,cue.compareAdded[0]):b1;
            var (ca,ra)=Wheel(box,0);var (cb,rb)=Wheel(box,1);float band=Band(ra);
            // The shared parts: before the cut (ink) and after it (a second ink), on both wheels.
            Arc(p,ca,ra,Turn(a0,a0,a1),Turn(h0,a0,a1),Ink,band);Arc(p,ca,ra,Turn(h1,a0,a1),Turn(a1,a0,a1),Shared,band);
            Arc(p,cb,rb,Turn(b0,b0,b1),Turn(m,b0,b1),Ink,band);Arc(p,cb,rb,Turn(m,b0,b1)+2.5f,Turn(e0,b0,b1),Shared,band);
            if(hasAdded){Arc(p,cb,rb,Turn(e0,b0,b1)+1f,Turn(b1,b0,b1),Added,band);Write(ctx,added,Polar(cb,rb-band*2.6f,(Turn(e0,b0,b1)+Turn(b1,b0,b1))/2),13,Added);}
            // What the second leaves out: lit on the first wheel, a cut on the second.
            if(hasCut)
            {
                Arc(p,ca,ra,Turn(h0,a0,a1),Turn(h1,a0,a1),Lit,band*1.25f);
                float g=Turn(m,b0,b1);
                Line(p,Polar(cb,rb-band*1.4f,g),Polar(cb,rb+band*1.4f,g),Lit,3);
                Line(p,Polar(cb,rb-band*1.4f,g+2.5f),Polar(cb,rb+band*1.4f,g+2.5f),Lit,3);
                float mid=(Turn(h0,a0,a1)+Turn(h1,a0,a1))/2;Write(ctx,lit,Polar(ca,ra-band*2.6f,mid),13,Lit);
                Write(ctx,missing,Polar(cb,rb+band*3.4f,g+1.2f),13,Lit);   // clear of the playhead's tip as it passes
                // the lit span's place, carried over to the cut as a dashed thread
                var from=Polar(ca,ra+band*.9f,mid);var to=Polar(cb,rb+band*.9f,g+1.2f);
                for(int i=0;i<24;i+=2){Line(p,Vector2.Lerp(from,to,i/24f),Vector2.Lerp(from,to,(i+1)/24f),new Color(Lit.r,Lit.g,Lit.b,.55f),1.4f);}
            }
            // hub rings and names
            p.strokeColor=Faint;p.lineWidth=1;p.BeginPath();p.Arc(ca,ra*.55f,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();
            p.BeginPath();p.Arc(cb,rb*.55f,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();
            Write(ctx,first,ca,24,Words);Write(ctx,second,cb,24,Words);
            Write(ctx,"start",Polar(ca,ra+band*1.6f,-90),11,Faint);Write(ctx,"start",Polar(cb,rb+band*1.6f,-90),11,Faint);
        }
    }

    sealed class Playhead : VisualElement
    {
        public PassageComparison owner;public SongDirector.Cue cue;
        public Playhead(){generateVisualContent+=Draw;}
        void Draw(MeshGenerationContext ctx)
        {
            if(cue==null||owner==null||owner.midi==null)return;var box=contentRect;if(box.width<50)return;
            double now=owner.midi.Position;var p=ctx.painter2D;
            double a0=cue.compareFirst[0],a1=cue.compareFirst[1],b0=cue.compareSecond[0],b1=cue.compareSecond[1];
            void Hand(int index,float a,Color color,float width,bool hollow)
            {
                var (c,r)=Wheel(box,index);float band=Band(r);
                p.strokeColor=color;p.lineWidth=width;p.BeginPath();p.MoveTo(Polar(c,r-band*1.5f,a));p.LineTo(Polar(c,r+band*1.5f,a));p.Stroke();
                var tip=Polar(c,r+band*1.5f,a);
                if(hollow){p.strokeColor=color;p.lineWidth=2;p.BeginPath();p.Arc(tip,5,Angle.Degrees(0),Angle.Degrees(360));p.Stroke();}
                else{p.fillColor=color;p.BeginPath();p.Arc(tip,4,Angle.Degrees(0),Angle.Degrees(360));p.Fill();}
            }
            // The leap: a bright sweep across the lit span of the first wheel, the word "skipped" beside it, and a
            // flash at the cut on the second.
            float age=owner.leapAge;
            if(age>=0)
            {
                var (ca,ra)=Wheel(box,0);var (cb,rb)=Wheel(box,1);float band=Band(ra),fade=1-Mathf.Clamp01((age-LeapSeconds)/(FlashSeconds-LeapSeconds));
                float reach=Mathf.Lerp(owner.leapFrom,owner.leapTo,Ease(age/LeapSeconds));
                Arc(p,ca,ra,owner.leapFrom,reach,new Color(1,.95f,.75f,.85f*fade),band*.55f);
                Write(ctx,"skipped",Polar(ca,ra-band*4.2f,(owner.leapFrom+owner.leapTo)/2),15,new Color(Lit.r,Lit.g,Lit.b,fade));
                if(cue.compareMissingAt>=0)
                {
                    float g=Turn(cue.compareMissingAt,b0,b1)+1.2f,glow=1-Mathf.Clamp01(age/FlashSeconds);
                    p.strokeColor=new Color(Lit.r,Lit.g,Lit.b,.9f*glow);p.lineWidth=3;p.BeginPath();p.Arc(Polar(cb,rb,g),band*(1.4f+1.6f*Ease(age/FlashSeconds)),Angle.Degrees(0),Angle.Degrees(360));p.Stroke();
                }
            }
            // The ghost: where the music is in the first passage while the second plays.
            double ghost=FollowTime(cue,now);
            if(!double.IsNaN(ghost))
            {
                float a=age>=0&&age<LeapSeconds?Mathf.Lerp(owner.leapFrom,owner.leapTo,Ease(age/LeapSeconds)):Turn(ghost,a0,a1);
                Hand(0,a,new Color(1,1,1,.8f),2,true);
            }
            if(now>=a0&&now<a1)Hand(0,Turn(now,a0,a1),Color.white,2.5f,false);
            if(now>=b0&&now<b1)Hand(1,Turn(now,b0,b1),Color.white,2.5f,false);
        }
    }
}
