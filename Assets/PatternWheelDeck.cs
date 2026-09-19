using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

// Section families are planets; instruments and pitch variants live inside them.
public sealed class PatternWheelDeck : VisualElement
{
    readonly Main main;readonly MidiPlayer midi;readonly Label status;
    readonly WheelCanvas canvas;readonly VisualElement chapters;readonly DropdownField level,vocal;int[] vocalTracks=Array.Empty<int>();
    PreparedPatternSong source;int[] levels=Array.Empty<int>();
    public int SelectedLevel=>canvas.Level;
    public double RackTurns=>canvas.Turns;
    public float RackPixels=>canvas.RackScroll;
    public int ActiveFamilyNode=>canvas.ActiveNode;
    public Vector2 MetaCenter=>canvas.metaCenter;
    public Vector2 FeaturedCenter=>canvas.featuredCenter;
    public int LeadVocalTrack=>canvas.LeadTrack;
    public PatternWheelDeck(Main owner,MidiPlayer player)
    {
        main=owner;midi=player;name="pattern-wheel-deck";
        status=new Label("Load a precomputed song bundle.");status.style.whiteSpace=WhiteSpace.Normal;Add(status);
        var hint=new Label("SECTION WHEELS · all instruments together\nThe rack advances the song form. The active section plays at the center; other wheels dim. Pitch variants slide in radial slots. Percussion strikes below the torus.");hint.style.whiteSpace=WhiteSpace.Normal;Add(hint);
        level=new DropdownField("Form level",new List<string>{"Song"},0);Add(level);
        level.RegisterValueChangedCallback(_=>{if(level.index>=0&&level.index<levels.Length)canvas.Level=levels[level.index];});
        vocal=new DropdownField("Lead vocal · brightest lane",new List<string>{"Choose a track"},0);Add(vocal);
        vocal.RegisterValueChangedCallback(_=>{if(source==null||vocal.index<0||vocal.index>=vocalTracks.Length)return;canvas.LeadTrack=vocalTracks[vocal.index];PlayerPrefs.SetInt("Resonance.Vocal."+source.MidiSha256,canvas.LeadTrack);});
        chapters=new VisualElement();Add(chapters);
        canvas=new WheelCanvas(main,midi){name="pattern-wheel-overlay",pickingMode=PickingMode.Ignore};
        var toggle=new Toggle("Show rack and section wheels"){value=true};toggle.RegisterValueChangedCallback(e=>canvas.style.display=e.newValue?DisplayStyle.Flex:DisplayStyle.None);Add(toggle);
        if(owner.GetComponent<DrumPatternDeck>()==null)owner.gameObject.AddComponent<DrumPatternDeck>();
    }
    public void AttachOverlay(VisualElement root)
    {
        canvas.style.position=Position.Absolute;canvas.style.left=336;canvas.style.top=8;
        root.RegisterCallback<GeometryChangedEvent>(_=>{var controls=root.Q<ScrollView>("controls");canvas.style.left=(controls?.layout.width??328)+8;});
        canvas.style.width=510;canvas.style.height=620;canvas.style.maxWidth=new Length(49,LengthUnit.Percent);root.Add(canvas);
    }
    public void Tick()
    {
        if(source!=midi.Prepared)
        {
            source=midi.Prepared;canvas.Load(source);chapters.Clear();
            vocalTracks=new[]{-1}.Concat(source?.Sections.SelectMany(s=>s.Lanes).Where(l=>l.Channel!=10).Select(l=>l.Track).Distinct().OrderBy(t=>t)??Enumerable.Empty<int>()).ToArray();
            string TrackName(int track)=>source?.TrackNames!=null&&track>=0&&track<source.TrackNames.Length&&!string.IsNullOrWhiteSpace(source.TrackNames[track])?source.TrackNames[track]:$"Track {track+1}";
            vocal.choices=vocalTracks.Select(t=>t<0?"No vocal / instrumental":TrackName(t)).ToList();
            int suggested=source?.LeadVocalTrack??-1;
            if(suggested<0)suggested=vocalTracks.Where(t=>t>=0&&TrackName(t).IndexOf("lead",StringComparison.OrdinalIgnoreCase)>=0&&TrackName(t).IndexOf("dbl",StringComparison.OrdinalIgnoreCase)<0).DefaultIfEmpty(-1).First();
            canvas.LeadTrack=source==null?-1:PlayerPrefs.GetInt("Resonance.Vocal."+source.MidiSha256,suggested);
            vocal.SetValueWithoutNotify(vocal.choices[Math.Max(0,Array.IndexOf(vocalTracks,canvas.LeadTrack))]);
            levels=source?.Form?.Where(n=>n.Children.Length>0).Select(n=>n.Id).ToArray()??Array.Empty<int>();
            level.choices=levels.Length==0?new List<string>{"Song"}:levels.Select(id=>source.Form[id].Path).ToList();level.SetValueWithoutNotify(level.choices[0]);canvas.Level=0;
            if(source!=null)foreach(var section in source.Sections){var s=section;var button=new Button(()=>midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(s.Start)))){text=$"{s.Name} · bar {s.FirstBar+1}"};chapters.Add(button);}
        }
        var active=midi.SongForm?.At(midi.Cycles?.BeatAt(midi.ScorePosition)??0);
        status.text=source?.Templates==null?"Prepare the section-wheel analysis offline.":$"{source.Sections.Length} section visits · {source.Form.Count(n=>n.Family>=0)} family wheels\n{source.TemplateNoteCount} rhythm slots represent {source.PatternNoteCount} note events\n{active?.Family.Name} · {midi.Position:0.0}s / {midi.Duration:0.0}s\n{source.Provenance}";
        canvas.TickControls();canvas.MarkDirtyRepaint();
    }
    sealed class WheelCanvas : VisualElement
    {
        readonly Main main;readonly MidiPlayer midi;OrreryBloom bloom;
        PreparedPatternSong source;readonly Dictionary<string,float> pitchPositions=new();
        readonly VisualElement rackInput;readonly Button transport;int dragPointer=-1;float dragY;double dragTime;
        public double SeekTimeForDrag(float pixels)=>Math.Clamp(dragTime-pixels*Math.Max(1,Math.Min(75,midi.Duration))/Math.Max(100,contentRect.height-100),0,midi.Duration);
        public int Level,ActiveNode=-1,LeadTrack=-1;public double Turns;public float RackScroll;
        public WheelCanvas(Main main,MidiPlayer midi)
        {
            this.main=main;this.midi=midi;generateVisualContent+=Draw;
            transport=new Button(()=>{ExplorerInputFocus.ClaimUI();if(midi.IsPlaying)midi.Pause();else midi.Play();}){name="rack-play-pause",text="Play",pickingMode=PickingMode.Position};
            transport.style.position=Position.Absolute;transport.style.left=0;transport.style.top=0;transport.style.width=76;Add(transport);
            rackInput=new VisualElement{name="time-rack-seek",pickingMode=PickingMode.Position,tooltip="Drag upward to seek forward; drag downward to rewind."};
            rackInput.style.position=Position.Absolute;rackInput.style.left=0;rackInput.style.top=45;rackInput.style.bottom=44;rackInput.style.width=80;Add(rackInput);
            rackInput.RegisterCallback<PointerDownEvent>(e=>{if(e.button!=0||!midi.Loaded)return;ExplorerInputFocus.ClaimUI();dragPointer=e.pointerId;dragY=e.position.y;dragTime=midi.Position;rackInput.CapturePointer(e.pointerId);e.StopPropagation();});
            rackInput.RegisterCallback<PointerMoveEvent>(e=>{if(e.pointerId!=dragPointer)return;midi.Seek(SeekTimeForDrag(e.position.y-dragY));MarkDirtyRepaint();e.StopPropagation();});
            rackInput.RegisterCallback<PointerUpEvent>(e=>{if(e.pointerId!=dragPointer)return;rackInput.ReleasePointer(e.pointerId);dragPointer=-1;e.StopPropagation();});
            rackInput.RegisterCallback<PointerCaptureOutEvent>(_=>dragPointer=-1);
            rackInput.RegisterCallback<WheelEvent>(e=>{ExplorerInputFocus.ClaimUI();midi.Seek(Math.Clamp(midi.Position+e.delta.y*1.5,0,midi.Duration));e.StopPropagation();});
            RegisterCallback<AttachToPanelEvent>(_=>{bloom=new OrreryBloom(main);bloom.Place(this);});
            RegisterCallback<DetachFromPanelEvent>(_=>{bloom?.Dispose();bloom=null;});
            schedule.Execute(()=>bloom?.Place(this)).Every(50);
        }
        public void TickControls(){transport.text=midi.IsPlaying?"Pause":"Play";transport.SetEnabled(midi.Loaded);}
        public void Load(PreparedPatternSong data){source=data;pitchPositions.Clear();Level=0;}
        static Vector2 At(Vector2 c,float radius,double phase){float a=(float)phase*Mathf.PI*2;return c+new Vector2(Mathf.Sin(a)*radius,-Mathf.Cos(a)*radius);}
        static void Line(Painter2D p,Vector2 a,Vector2 b,Color color,float width=1){p.strokeColor=color;p.lineWidth=width;p.BeginPath();p.MoveTo(a);p.LineTo(b);p.Stroke();}
        static void Ring(Painter2D p,Vector2 c,float r,Color color,float width=1,double start=0,double end=1)
        {p.strokeColor=color;p.lineWidth=width;p.BeginPath();int steps=Mathf.Max(2,(int)((end-start)*100));for(int i=0;i<=steps;i++){var pt=At(c,r,start+(end-start)*i/steps);if(i==0)p.MoveTo(pt);else p.LineTo(pt);}p.Stroke();}
        static void Dot(Painter2D p,Vector2 c,float r,Color color){p.fillColor=color;p.BeginPath();p.Arc(c,r,0,360);p.Fill();}
        static void Gear(Painter2D p,Vector2 c,float radius,double turns,Color hue,int teeth)
        {
            p.strokeColor=hue;p.lineWidth=1;p.BeginPath();
            for(int i=0;i<=teeth*4;i++){float r=radius+(i%4 is 1 or 2?3:0);var pt=At(c,r,i/(double)(teeth*4)-turns);if(i==0)p.MoveTo(pt);else p.LineTo(pt);}p.ClosePath();p.Stroke();
        }
        Color Hue(int pitch)=>TonalColorField.Pitch(pitch-21,main.currentKey);
        static Color Dim(Color color,float strength)=>new Color(color.r*strength,color.g*strength,color.b*strength,color.a);
        readonly List<(Vector2 anchor,Vector2 direction,string text,bool bright)> labels=new();
        public Vector2 metaCenter,featuredCenter;
        void RadialLabel(Vector2 anchor,string text,bool bright=false){var direction=(anchor-metaCenter).normalized;if(direction.sqrMagnitude<.1f)direction=Vector2.up;labels.Add((anchor,direction,text,bright));}
        void DrawLabels(MeshGenerationContext ctx,Painter2D p,float w,float h){
            foreach(bool right in new[]{false,true}){
                float last=48;
                foreach(var label in labels.Where(l=>(l.direction.x>=0)==right).OrderBy(l=>l.anchor.y+l.direction.y*48)){
                    float y=Mathf.Clamp(Mathf.Max(last,label.anchor.y+label.direction.y*48),48,h-84);last=y+36;
                    float x=right?w-128:85;var end=new Vector2(right?x-4:x+118,y+6);
                    Line(p,label.anchor,label.anchor+label.direction*13,new Color(.38f,.45f,.52f,.65f));Line(p,label.anchor+label.direction*13,end,new Color(.38f,.45f,.52f,.65f));
                    float height=label.text.Contains("\n")?30:17;
                    p.fillColor=new Color(.018f,.025f,.034f,.95f);p.BeginPath();p.MoveTo(new Vector2(x-2,y-1));p.LineTo(new Vector2(x+125,y-1));p.LineTo(new Vector2(x+125,y+height));p.LineTo(new Vector2(x-2,y+height));p.ClosePath();p.Fill();
                    ctx.DrawText(label.text,new Vector2(x,y),11,label.bright?Color.white:new Color(.83f,.86f,.9f));
                }
            }
        }
        void Draw(MeshGenerationContext ctx)
        {
            if(contentRect.width<1||bloom==null)return;
            labels.Clear();var p=ctx.painter2D;float w=contentRect.width,h=contentRect.height;
            bloom.Begin(w,h,resolvedStyle.display!=DisplayStyle.None);
            if(source?.Form==null||source.Form.Length==0){ctx.DrawText("PREPARE SECTION WHEELS OFFLINE",new Vector2(20,45),12,Color.gray);bloom.End();return;}
            var parent=source.Form[Mathf.Clamp(Level,0,source.Form.Length-1)];if(parent.Children.Length==0){bloom.End();return;}
            double beat=midi.Cycles.BeatAt(midi.ScorePosition);int si=Array.FindLastIndex(source.Sections,s=>s.Start<=beat);
            si=Math.Max(0,si);var section=source.Sections[si];
            int ri=Array.FindLastIndex(parent.Route,r=>r.Section<=si);ri=Math.Max(0,ri);var route=parent.Route[ri];ActiveNode=route.Child;
            var dockSection=source.Sections[route.Section];
            double previous=ri>0?parent.Route[ri-1].Turns:route.Turns;
            double ease=Math.Clamp((beat-dockSection.Start)/.65,0,1);ease=ease*ease*(3-2*ease);Turns=previous+(route.Turns-previous)*ease;
            float gearRadius=w*.32f;var center=new Vector2(gearRadius+83,h*.45f);metaCenter=center;float rackX=center.x-gearRadius-4;
            var metal=new Color(.31f,.44f,.55f,.75f);
            Line(p,new Vector2(rackX-7,26),new Vector2(rackX-7,h-46),metal,2);
            float pitch=2*Mathf.PI*gearRadius/70,scroll=(float)(midi.Position/Math.Max(1,Math.Min(75,midi.Duration))*(h-100));RackScroll=scroll;Turns=-scroll/(2*Math.PI*gearRadius);
            for(float y=26-scroll%pitch;y<h-46;y+=pitch){Line(p,new Vector2(rackX-7,y),new Vector2(rackX+2,y),metal);Line(p,new Vector2(rackX+2,y),new Vector2(rackX+2,y+pitch*.5f),metal);}
            ctx.DrawText("DRAG TO SEEK",new Vector2(0,h-28),10,new Color(.65f,.79f,.88f));
            for(int j=0;j<source.Sections.Length;j++)
            {var s=source.Sections[j];float y=center.y+(float)((midi.AudioTime(midi.Cycles.SecondsAt(s.Start))-midi.Position)/Math.Max(1,Math.Min(75,midi.Duration))*(h-100));if(y<48||y>h-48)continue;ctx.DrawText(s.Name,new Vector2(3,y-7),j==si?13:11,j==si?Color.white:new Color(.34f,.43f,.52f));Dot(p,new Vector2(rackX-2,y),j==si?4:3,j==si?Color.white:new Color(.42f,.66f,.83f));Line(p,new Vector2(rackX-12,y),new Vector2(rackX+4,y),new Color(.38f,.6f,.78f));}
            float trigger=(float)Math.Exp(-Math.Max(0,beat-section.Start)*4);
            Line(p,new Vector2(rackX+5,center.y),new Vector2(rackX+23,center.y),Color.Lerp(new Color(.35f,.55f,.72f),Color.white,trigger),2);
            Dot(p,new Vector2(rackX+5,center.y),4,Color.white);bloom.Disk(new Vector2(rackX+5,center.y),3,Color.white,trigger);
            Gear(p,center,gearRadius,Turns,metal,70);Ring(p,center,gearRadius*.96f,new Color(.18f,.29f,.37f,.6f));Dot(p,center,6,new Color(.48f,.64f,.75f));
            float orbit=gearRadius*.79f;int count=parent.Children.Length;
            // Inactive wheels are behind the docked one, and retain their own channel contents.
            foreach(int child in parent.Children.Where(id=>id!=ActiveNode).Concat(new[]{ActiveNode}))
            {
                int rank=Array.IndexOf(parent.Children,child);double angle=rank/(double)count-Turns;
                var c=center+new Vector2(Mathf.Cos((float)angle*Mathf.PI*2),Mathf.Sin((float)angle*Mathf.PI*2))*orbit;
                bool active=child==ActiveNode;float radius=active?w*.19f:w*.055f;if(active){c=center;featuredCenter=c;}
                Line(p,center,c,new Color(.23f,.34f,.44f,active?.8f:.25f));
                var node=source.Form[child];var sample=active?dockSection:source.Sections.FirstOrDefault(s=>s.Node==child);
                DrawSection(ctx,p,node,sample,c,radius,active,beat);
            }
            ctx.DrawText($"{parent.Path} · {midi.Position/midi.Duration*100:0}%",new Vector2(rackX+18,h-34),12,new Color(.62f,.76f,.86f));
            ctx.DrawText("Pitch slides radially · rhythm stays in its slot",new Vector2(rackX+18,h-16),10,new Color(.48f,.6f,.7f));
            DrawLabels(ctx,p,w,h);bloom.End();
        }
        void DrawSection(MeshGenerationContext ctx,Painter2D p,PreparedPatternSong.FormNode node,PreparedPatternSong.Section section,Vector2 center,float radius,bool active,double beat)
        {
            float dim=active?1:.32f;
            Dot(p,center,radius,new Color(.008f,.015f,.024f,active?.92f:.55f));
            double local=section==null?0:beat-section.Start;
            double spin=active&&section!=null?local/Math.Max(.01,section.ProgressionBeats):0;
            Gear(p,center,radius,spin,Dim(new Color(.32f,.53f,.69f),dim),48);
            Ring(p,center,radius*.94f,Dim(new Color(.28f,.44f,.58f),dim));
            if(section!=null)
            {
                foreach(var chord in section.Chords){double a=(chord.Start-section.Start)/section.ProgressionBeats-spin,b=(chord.End-section.Start)/section.ProgressionBeats-spin;Ring(p,center,radius*.88f,Dim(CyclicOrrery.ChordColor(chord,main.currentKey),dim),active?4:2,a,b);}
                int li=0;int laneCount=section.Lanes.Count(l=>l.Channel!=10);
                foreach(var lane in section.Lanes.Where(l=>l.Channel!=10).OrderBy(l=>l.Track==LeadTrack?1:0))
                {
                    var play=active?lane.Plays.LastOrDefault(v=>v.Start<=beat&&beat<v.End):lane.Plays.FirstOrDefault();if(play==null){if(active)RadialLabel(At(center,radius*.98f,(li+.5)/Math.Max(1,laneCount)),lane.Name+"\nRest",lane.Track==LeadTrack);li++;continue;}
                    bool lead=lane.Track==LeadTrack;float laneLight=lead?1.65f:.48f;
                    var template=source.Templates[play.Template];var variation=template.Variants[play.Variant];double position=active?beat-play.Start:0;
                    for(int i=0;i<template.Slots.Length;i++)
                    {
                        var slot=template.Slots[i];int pitch=slot.Pitch+variation.Transpose+(variation.PitchDelta.Length==0?0:variation.PitchDelta[i]);
                        string key=$"{node.Id}/{lane.Track}/{lane.Channel}/{template.Id}/{i}";
                        if(!pitchPositions.TryGetValue(key,out float shown))shown=pitch;
                        shown=active?Mathf.Lerp(shown,pitch,1-Mathf.Exp(-Time.unscaledDeltaTime*12)):pitch;pitchPositions[key]=shown;
                        double phase=(slot.Beat+variation.BeatOffsets[i]-position)/template.Beats;
                        float inner=radius*.23f,outer=radius*.8f;float r=Mathf.Lerp(inner,outer,Mathf.InverseLerp(36,96,shown));
                        // One persistent radial slot per rhythm onset; harmony only changes its radius.
                        Line(p,At(center,inner,phase),At(center,outer,phase),new Color(.17f,.28f,.37f,active?.34f:.07f),.6f);
                        var pt=At(center,r,phase);var hue=Hue(pitch);double elapsed=position-slot.Beat-variation.BeatOffsets[i];
                        float energy=active&&midi.IsPlaying&&elapsed>=0?(float)Math.Exp(-elapsed*7):0;
                        double duration=Math.Max(0,slot.Length+variation.LengthOffsets[i]);
                        double span=duration/template.Beats,visibleSpan=Math.Min(1,span);
                        // Rotation decreases phase; the duration ribbon follows behind the onset.
                        int pieces=Math.Max(2,(int)Math.Ceiling(visibleSpan*64));
                        for(int tail=0;tail<pieces;tail++){
                            double a=phase+visibleSpan*tail/pieces,b=phase+visibleSpan*(tail+1)/pieces;
                            float strength=Mathf.Lerp(.65f,.12f,tail/(float)pieces)*dim*laneLight;
                            Line(p,At(center,r,a),At(center,r,b),Dim(hue,strength),active?2.4f:1.1f);
                        }
                        if(active&&span>1.001)ctx.DrawText($"{span:0.#}×",pt+new Vector2(4,3),8,Dim(hue,.75f));
                        Dot(p,pt,(active?2.3f:1.2f)+energy*2,Color.Lerp(Dim(hue,dim*laneLight),lead?Color.white:Dim(hue,.8f),energy));
                        if(active)bloom.Disk(pt,lead?2.2f:1.2f,hue,energy*variation.Velocities[i]*(lead?1.5f:.12f));
                    }
                    if(active){var badge=At(center,radius*.98f,(li+.5)/Math.Max(1,laneCount));Ring(p,badge,5,new Color(.6f,.64f,.69f));Line(p,badge,At(badge,4,(play.Variant%12+1)/12.0),Color.white);RadialLabel(badge,$"{lane.Name}\nP{play.Template+1} · V{play.Variant+1}"+(lead?" · VOCAL":""),lead);}li++;
                }
            }
            var visit=section==null?0:source.Sections.Where(s=>s.Node==section.Node&&s.Start<=section.Start).Count()-1;
            int visits=section==null?1:source.Sections.Count(s=>s.Node==section.Node);
            var dial=center+new Vector2(radius*.51f,-radius*.64f);float dr=active?23:12;
            Dot(p,dial,dr+3,new Color(.012f,.023f,.035f));Ring(p,dial,dr,Dim(new Color(.55f,.72f,.84f),active?1:.7f));
            int digits=Math.Min(6,Math.Max(3,visits));
            for(int d=0;d<digits;d++){var pt=At(dial,dr*.74f,(d+1)/(double)(digits+2));ctx.DrawText((d+1).ToString(),pt-new Vector2(3,5),active?10:8,Dim(Color.white,active?.85f:.45f));}
            var hand=At(dial,dr*.53f,(visit+1)/(double)(digits+2));Line(p,dial,hand,active?Color.white:new Color(.45f,.57f,.66f),active?2:1);Dot(p,dial,2,Color.white);
            if(active)ctx.DrawText($"TAKE {visit+1}",dial+new Vector2(-20,dr+4),9,new Color(.62f,.8f,.94f));
            Dot(p,center,radius*.15f,new Color(.012f,.023f,.035f));
            if(active)ctx.DrawText(node.Name,center+new Vector2(-radius*.38f,-6),15,Color.white);else RadialLabel(center+(center-metaCenter).normalized*radius,node.Name);
            if(active){var needle=At(center,radius*.88f,0);Dot(p,needle,3,Color.white);if(midi.IsPlaying)bloom.Disk(needle,2,Color.white,.7f);}
            if(node.Children.Length>0)ctx.DrawText($"{node.Children.Length} subforms",center+new Vector2(-25,15),10,Dim(Color.white,dim));
        }
    }
}
