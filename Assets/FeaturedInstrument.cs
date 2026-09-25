using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

[DefaultExecutionOrder(60)]
public sealed class FeaturedInstrument : MonoBehaviour
{
    Main main;MidiPlayer midi;PreparedPatternSong source;DropdownField selector;
    (int track,int channel)[] choices=Array.Empty<(int,int)>();
    readonly HashSet<int> pitches=new();
    readonly List<VoiceTrail> voices=new();
    // Routes over the torus between two notes, for the pose they were computed in. While the
    // pose moves (a key change, a tension's lean) the trail's last second is always exact and
    // older stretches catch up a few routes per frame, so no frame recomputes them all.
    sealed class RouteCache {public Vector3[] Points;public float Length;public int Version=-1;}
    readonly Dictionary<(int,int),RouteCache> routes=new();readonly List<Vector3> routeScratch=new(64);
    int poseVersion,refreshBudget;
    float routeRotation=float.NaN,routeTwist,routeUncoil;Matrix4x4 routeTransform;

    Material material,headMaterial;double lastPosition=-1;int frameIndex;
    sealed class Sample { public int From,To;public float Progress,Stamp,Impact; }
    sealed class VoiceTrail { public PreparedPatternSong.MelodyStrand Data;public LineRenderer Line;public Transform Head;public readonly List<Sample> Samples=new(); }
    public int VoiceCount=>voices.Count;
    public void GetFocusPoints(List<Vector3> points){points.Clear();foreach(var v in voices)if(v.Head!=null&&v.Head.gameObject.activeSelf){points.Add(v.Head.position);if(points.Count==2)break;}}
    public int VisibleHeads=>voices.Count(v=>v.Head.gameObject.activeSelf);
    public int Track {get;private set;}=-1;
    public int Channel {get;private set;}=-1;
    public int HistoryCount=>voices.Sum(v=>v.Samples.Count);
    public float TrailLength {get;private set;}
    public bool Matches(int track,int channel)=>track==Track&&channel==Channel;
    public static (int track,int channel) DefaultLane(PreparedPatternSong data)
    {
        var lanes=data.Notes.Where(n=>n.Channel!=10).GroupBy(n=>(n.Track,n.Channel)).ToArray();
        if(lanes.Length==0)return (-1,-1);
        var lead=lanes.Where(g=>g.Key.Track==data.LeadVocalTrack).OrderByDescending(g=>g.Count()).FirstOrDefault();
        if(lead==null)lead=lanes.Where(g=>data.TrackNames!=null&&g.Key.Track<data.TrackNames.Length&&
            (data.TrackNames[g.Key.Track].IndexOf("lead vocal",StringComparison.OrdinalIgnoreCase)>=0||data.TrackNames[g.Key.Track].IndexOf("singing voice",StringComparison.OrdinalIgnoreCase)>=0)).OrderByDescending(g=>g.Count()).FirstOrDefault();
        return (lead??lanes.OrderByDescending(g=>g.Average(n=>n.Pitch)).First()).Key;
    }
    void Awake(){main=GetComponent<Main>();}
    public VisualElement BuildUI()
    {
        var box=new VisualElement{name="featured-instrument-controls"};
        box.style.marginTop=8;box.style.marginBottom=12;box.style.paddingLeft=8;box.style.paddingRight=8;box.style.paddingBottom=8;
        box.style.borderLeftWidth=2;box.style.borderLeftColor=new Color(.85f,.91f,1);
        box.Add(new Label("MELODY HIGHLIGHT · WHITE"));
        selector=new DropdownField("Highlight instrument / channel",new List<string>{"Load a song"},0){name="featured-instrument"};
        selector.RegisterValueChangedCallback(_=>{if(selector.index>=0&&selector.index<choices.Length)Select(choices[selector.index].track,choices[selector.index].channel);});box.Add(selector);
        var hint=new Label("White notes and comet history on the torus; white notes on every pattern wheel.");hint.style.whiteSpace=WhiteSpace.Normal;box.Add(hint);return box;
    }
    public void EnsureLoaded(PreparedPatternSong data)
    {
        if(source==data)return;source=data;ClearHistory();pitches.Clear();main.FeatureNotes(pitches);
        if(data==null){Track=Channel=-1;return;}
        choices=data.Notes.Where(n=>n.Channel!=10).Select(n=>(n.Track,n.Channel)).Distinct().OrderBy(p=>p.Track).ThenBy(p=>p.Channel).ToArray();
        var chosen=DefaultLane(data);Track=chosen.track;Channel=chosen.channel;
        string pref="Resonance.Featured."+data.MidiSha256;
        int savedTrack=PlayerPrefs.GetInt(pref+".track",Track),savedChannel=PlayerPrefs.GetInt(pref+".channel",Channel);
        if(choices.Contains((savedTrack,savedChannel))){Track=savedTrack;Channel=savedChannel;}
        if(selector!=null){selector.choices=choices.Select(p=>(data.TrackNames!=null&&p.track<data.TrackNames.Length&&!string.IsNullOrEmpty(data.TrackNames[p.track])?data.TrackNames[p.track]:"Track "+(p.track+1))+" · Ch "+p.channel).ToList();
            if(selector.choices.Count==0)selector.choices.Add("No pitched instruments");selector.SetValueWithoutNotify(selector.choices[Math.Max(0,Array.IndexOf(choices,(Track,Channel)))]);}
        lastPosition=-1;BuildVoices();
    }
    public void Select(int track,int channel)
    {
        if(!choices.Contains((track,channel)))return;Track=track;Channel=channel;ClearHistory();lastPosition=-1;BuildVoices();
        if(source!=null){string pref="Resonance.Featured."+source.MidiSha256;PlayerPrefs.SetInt(pref+".track",track);PlayerPrefs.SetInt(pref+".channel",channel);}
    }
    void BuildVoices()
    {
        foreach(var voice in voices){Destroy(voice.Line.gameObject);Destroy(voice.Head.gameObject);}voices.Clear();
        if(material==null){material=new Material(Resources.Load<Shader>("HarmonicGlow"));material.SetColor("_BaseColor",Color.white*1.4f);
            headMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit"));headMaterial.SetColor("_BaseColor",Color.white*10);}
        foreach(var data in source?.MelodyStrands??Array.Empty<PreparedPatternSong.MelodyStrand>()){
            if(!Matches(data.Track,data.Channel)||data.Notes.Length==0)continue;
            var go=new GameObject("Melody strand "+(data.Rank+1));go.transform.SetParent(transform,false);
            var line=go.AddComponent<LineRenderer>();line.sharedMaterial=material;line.useWorldSpace=true;line.numCapVertices=3;line.widthMultiplier=.014f;
            line.widthCurve=AnimationCurve.Linear(0,1,1,.02f);
            var head=GameObject.CreatePrimitive(PrimitiveType.Sphere);head.name="Melody mouse "+(data.Rank+1);head.transform.SetParent(transform,false);
            Destroy(head.GetComponent<Collider>());head.GetComponent<Renderer>().sharedMaterial=headMaterial;head.SetActive(false);
            voices.Add(new VoiceTrail{Data=data,Line=line,Head=head.transform});
        }
    }
    public void ClearHistory(){foreach(var v in voices){v.Samples.Clear();v.Line.positionCount=0;v.Head.gameObject.SetActive(false);}TrailLength=0;}
    public void ResetPosition(){ClearHistory();lastPosition=-1;}
    void Update()
    {
        using var perf=Perf.FeaturedFrames.Auto();
        if(midi==null)midi=GetComponent<MidiPlayer>();if(midi==null)return;EnsureLoaded(midi.Prepared);
        if(source?.Frames==null)return;double now=midi.VisualScorePosition;
        bool jump=lastPosition<0||now<lastPosition-.001||now-lastPosition>Math.Max(.5,Time.unscaledDeltaTime*midi.playbackSpeed*3);
        if(jump){ClearHistory();frameIndex=0;while(frameIndex<source.Frames.Length&&source.Frames[frameIndex].Time<=now)frameIndex++;}
        else while(frameIndex<source.Frames.Length&&source.Frames[frameIndex].Time<=now)frameIndex++;
        pitches.Clear();if(midi.IsAudible&&frameIndex>0)foreach(var v in source.Frames[frameIndex-1].Voices)if(Matches(v.Track,v.Channel)&&Accepted(v))pitches.Add(v.Pitch-21);
        main.FeatureNotes(pitches);
    }
    bool Accepted(PreparedPatternSong.Voice v)=>v.Pitch>=21&&v.Pitch<117&&(midi.TrackFilter<0||midi.TrackFilter==v.Track)&&(midi.ChannelFilter==0||midi.ChannelFilter==v.Channel);
    RouteCache Route(int from,int to,bool fresh=true)
    {
        if(!routes.TryGetValue((from,to),out var r)){r=new RouteCache();routes[(from,to)]=r;}
        if(r.Version!=poseVersion&&(fresh||r.Points==null||refreshBudget-->0))
        {
            routeScratch.Clear();main.NoteHistoryPath(Mathf.Clamp(from-21,0,95),Mathf.Clamp(to-21,0,95),routeScratch);
            r.Points=routeScratch.ToArray();r.Length=Length(r.Points);r.Version=poseVersion;
        }
        return r;
    }
    static float Length(Vector3[] points){float d=0;for(int i=1;i<points.Length;i++)d+=Vector3.Distance(points[i-1],points[i]);return d;}
    Vector3 Point(Sample sample,bool fresh=true)
    {
        var route=Route(sample.From,sample.To,fresh);var points=route.Points;float remaining=route.Length*sample.Progress;
        for(int i=1;i<points.Length;i++){float d=Vector3.Distance(points[i-1],points[i]);if(remaining<=d)return Vector3.Lerp(points[i-1],points[i],d>0?remaining/d:0);remaining-=d;}return points[points.Length-1];
    }
    public static float TrailFalloff(float distance)=>1-Mathf.Log(1+31*Mathf.Clamp01(distance))/Mathf.Log(32);
    public static double Departure(double previous,double next,float distance)=>Math.Max(previous+Math.Min(.075,Math.Max(0,next-previous)*.3),next-Math.Clamp(distance/7f,.09f,.42f));
    Sample At(VoiceTrail voice,double time,out float light)
    {
        var notes=voice.Data.Notes;int lo=0,hi=notes.Length;
        while(lo<hi){int mid=(lo+hi)/2;if(notes[mid].Start<=time)lo=mid+1;else hi=mid;}
        int index=lo-1;light=0;if(index<0)return null;
        var n=notes[index];var sample=new Sample{Impact=Mathf.Exp(-(float)Math.Max(0,time-n.Start)*22),From=n.Pitch,To=n.Pitch,Progress=1,Stamp=Time.unscaledTime};
        light=Mathf.Exp(-(float)Math.Max(0,time-n.End)*8);
        if(index+1<notes.Length){var next=notes[index+1];double depart=Departure(n.Start,next.Start,Route(n.Pitch,next.Pitch).Length);
            if(time>=depart&&next.Start>depart){sample.To=next.Pitch;sample.Progress=(float)((time-depart)/(next.Start-depart));light=1;}}
        return sample;
    }
    void LateUpdate()
    {
        using var perf=Perf.Featured.Auto();
        if(midi==null||source==null)return;
        if(routeRotation!=main.VisualRotation||routeTwist!=main.VisualTwist||routeUncoil!=main.UncoilAmount||routeTransform!=transform.localToWorldMatrix)
        {poseVersion++;routeRotation=main.VisualRotation;routeTwist=main.VisualTwist;routeUncoil=main.UncoilAmount;routeTransform=transform.localToWorldMatrix;}
        refreshBudget=6;
        double now=midi.VisualScorePosition;
        bool accepted=(midi.TrackFilter<0||midi.TrackFilter==Track)&&(midi.ChannelFilter==0||midi.ChannelFilter==Channel);
        TrailLength=0;
        foreach(var voice in voices){
            if(!accepted){voice.Line.positionCount=0;voice.Head.gameObject.SetActive(false);continue;}
            if(midi.IsAudible){
                // Sample score time, including travel between frames, never trigger notes.
                double start=lastPosition<0?now:Math.Max(lastPosition,now-.5);
                int steps=Math.Max(1,(int)Math.Ceiling((now-start)/.012));
                for(int step=1;step<=steps;step++){
                    double t=start+(now-start)*step/steps;var sample=At(voice,t,out _);if(sample==null)continue;
                    sample.Stamp=Time.unscaledTime-(float)((now-t)/Math.Max(.01,midi.playbackSpeed));
                    if(voice.Samples.Count==0||Vector3.Distance(Point(sample),Point(voice.Samples[voice.Samples.Count-1]))>.012f)voice.Samples.Add(sample);
                }
            }
            var head=At(voice,now,out float light);voice.Head.gameObject.SetActive(midi.IsAudible&&head!=null&&light>.02f);
            if(head!=null){voice.Head.position=Point(head);voice.Head.localScale=Vector3.one*(.075f+.045f*head.Impact)*Mathf.Sqrt(light);}
            // Old samples expire individually; a stationary head cannot renew the tail.
            voice.Samples.RemoveAll(s=>Time.unscaledTime-s.Stamp>9);
            float idle=voice.Samples.Count==0?0:Time.unscaledTime-voice.Samples[voice.Samples.Count-1].Stamp;
            float budget=main.HistoryCircumference*Mathf.Clamp01(1-Mathf.Max(0,idle-1)/3);
            var points=new List<Vector3>();var stamps=new List<float>();float length=0;
            for(int i=voice.Samples.Count-1;i>=0;i--){var point=Point(voice.Samples[i],Time.unscaledTime-voice.Samples[i].Stamp<1.2f);float d=points.Count>0?Vector3.Distance(points[points.Count-1],point):0;
                if(length+d>budget)break;length+=d;points.Add(point);stamps.Add(voice.Samples[i].Stamp);}
            TrailLength=Mathf.Max(TrailLength,length);
            int retained=points.Count;
            if(points.Count>1){var uniform=new List<Vector3>{points[0]};var times=new List<float>{stamps[0]};float walked=0,next=.03f;
                for(int i=1;i<points.Count;i++){float distance=Vector3.Distance(points[i-1],points[i]);
                    while(distance>.000001f&&next<=walked+distance){float t=(next-walked)/distance;uniform.Add(Vector3.Lerp(points[i-1],points[i],t));times.Add(Mathf.Lerp(stamps[i-1],stamps[i],t));next+=.03f;}walked+=distance;}
                uniform.Add(points[points.Count-1]);times.Add(stamps[stamps.Count-1]);points=uniform;stamps=times;}
            voice.Line.positionCount=points.Count;voice.Line.SetPositions(points.ToArray());
            if(points.Count<2)continue;
            var colors=new GradientColorKey[8];
            float[] stops={0,.015f,.04f,.1f,.22f,.42f,.7f,1};
            for(int k=0;k<8;k++){float fraction=stops[k];int index=Mathf.RoundToInt(fraction*(stamps.Count-1));
                float age=Time.unscaledTime-stamps[index];float intensity=TrailFalloff(fraction)*Mathf.SmoothStep(0,1,Mathf.Clamp01((9-age)/3));
                colors[k]=new GradientColorKey(Color.white*intensity,fraction);}
            var gradient=new Gradient();gradient.SetKeys(colors,new[]{new GradientAlphaKey(1,0),new GradientAlphaKey(0,1)});voice.Line.colorGradient=gradient;
            if(voice.Samples.Count>retained+1)voice.Samples.RemoveRange(0,voice.Samples.Count-retained-1);
        }
        lastPosition=now;
    }
    void OnDisable(){pitches.Clear();if(main!=null)main.FeatureNotes(pitches);ClearHistory();}
    void OnDestroy(){foreach(var v in voices){if(v.Line!=null)Destroy(v.Line.gameObject);if(v.Head!=null)Destroy(v.Head.gameObject);}if(material!=null)Destroy(material);if(headMaterial!=null)Destroy(headMaterial);}
}
