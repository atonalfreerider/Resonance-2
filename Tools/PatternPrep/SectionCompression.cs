using System.Text.Json;

public sealed class SongSettings
{
    public int Key=-1,LeadVocalTrack=-1;
    public bool Minor;
    public string KeySource="",SectionBoundaries="",SectionSource="";
    public string[] SectionParents=Array.Empty<string>();
}

// Lossless musical reconstruction: quantization chooses template candidates;
// exact timing, duration, velocity and pitch residuals are retained per variant.
public static class SectionCompression
{
    static long Q(double value)=>(long)Math.Round(value*24);
    static string Signature(IEnumerable<MidiCycleAnalysis.Hit> hits,double start)=>string.Join(";",hits.OrderBy(h=>h.Beat).ThenBy(h=>h.Pitch).Select(h=>$"{Q(h.Beat-start)},{Q(h.Length)}"));
    public static void Build(PreparedPatternSong song,SongSettings settings)
    {
        var templates=new List<PreparedPatternSong.RhythmTemplate>();
        var keys=new Dictionary<string,int>();var variants=new List<List<PreparedPatternSong.PitchVariant>>();
        var variantKeys=new List<Dictionary<string,int>>();
        var pitched=song.Notes.OrderBy(n=>n.Beat).ThenBy(n=>n.Pitch).ToArray();
        song.PatternNoteCount=pitched.Length;
        foreach(var section in song.Sections)
        {
            int sectionIndex=Array.IndexOf(song.Sections,section);
            section.ParentPath=sectionIndex<settings.SectionParents.Length?settings.SectionParents[sectionIndex]:"Song";
            var lanes=new List<PreparedPatternSong.InstrumentLane>();
            foreach(var lane in pitched.Where(n=>n.Beat>=section.Start&&n.Beat<section.End).GroupBy(n=>(n.Track,n.Channel)).OrderBy(g=>g.Key.Track).ThenBy(g=>g.Key.Channel))
            {
                var plays=new List<PreparedPatternSong.PatternPlay>();
                foreach(var measure in song.Measures.Where(b=>b.Start<section.End&&b.End>section.Start))
                {
                    double start=Math.Max(section.Start,measure.Start),end=Math.Min(section.End,measure.End),duration=end-start;
                    var notes=lane.Where(n=>n.Beat>=start&&n.Beat<end).OrderBy(n=>n.Beat).ThenBy(n=>n.Pitch).ToArray();
                    if(notes.Length==0)continue;
                    double period=duration;
                    // Choose the shortest repeating attack/duration cell, even when pitches change.
                    foreach(int divisions in new[]{8,4,2})
                    {
                        double candidate=duration/divisions;if(candidate<.5)continue;
                        var first=notes.Where(n=>n.Beat<start+candidate).ToArray();if(first.Length==0)continue;
                        string signature=Signature(first,start);bool repeated=true;
                        for(int j=1;j<divisions;j++)if(Signature(notes.Where(n=>n.Beat>=start+j*candidate&&n.Beat<start+(j+1)*candidate),start+j*candidate)!=signature){repeated=false;break;}
                        if(repeated){period=candidate;break;}
                    }
                    for(double a=start;a<end-.0000001;a+=period)
                    {
                        double z=Math.Min(end,a+period);var hits=notes.Where(n=>n.Beat>=a&&n.Beat<z).ToArray();if(hits.Length==0)continue;
                        string key=$"{lane.Key.Track}:{lane.Key.Channel}:{Q(z-a)}:"+Signature(hits,a);
                        if(!keys.TryGetValue(key,out int id))
                        {
                            id=templates.Count;keys.Add(key,id);variants.Add(new());variantKeys.Add(new());
                            templates.Add(new(){Id=id,Track=lane.Key.Track,Channel=lane.Key.Channel,Beats=z-a,Slots=hits.Select(h=>new MidiCycleAnalysis.Hit{Beat=h.Beat-a,Length=h.Length,Pitch=h.Pitch,Velocity=h.Velocity,Track=h.Track,Channel=h.Channel}).ToArray()});
                        }
                        var slots=templates[id].Slots;int transpose=hits[0].Pitch-slots[0].Pitch;
                        var delta=hits.Select((h,i)=>h.Pitch-slots[i].Pitch-transpose).ToArray();
                        var variation=new PreparedPatternSong.PitchVariant{Transpose=transpose,PitchDelta=delta.Any(d=>d!=0)?delta:Array.Empty<int>(),Velocities=hits.Select(h=>h.Velocity).ToArray(),
                            BeatOffsets=hits.Select((h,i)=>h.Beat-a-slots[i].Beat).ToArray(),LengthOffsets=hits.Select((h,i)=>h.Length-slots[i].Length).ToArray()};
                        string vk=JsonSerializer.Serialize(variation,new JsonSerializerOptions{IncludeFields=true});
                        if(!variantKeys[id].TryGetValue(vk,out int vi)){vi=variants[id].Count;variants[id].Add(variation);variantKeys[id].Add(vk,vi);}
                        plays.Add(new(){Template=id,Variant=vi,Start=a,End=z});
                    }
                }
                lanes.Add(new(){Track=lane.Key.Track,Channel=lane.Key.Channel,Name=lane.Key.Track<song.TrackNames.Length&&!string.IsNullOrWhiteSpace(song.TrackNames[lane.Key.Track])?song.TrackNames[lane.Key.Track]:$"Track {lane.Key.Track+1} / Ch {lane.Key.Channel}",Plays=plays.ToArray()});
            }
            section.Lanes=lanes.ToArray();
        }
        for(int i=0;i<templates.Count;i++)templates[i].Variants=variants[i].ToArray();
        song.Templates=templates.ToArray();song.TemplateNoteCount=templates.Sum(t=>t.Slots.Length);
        BuildHierarchy(song);
        Verify(song,pitched);
    }
    static void BuildHierarchy(PreparedPatternSong song)
    {
        var nodes=new List<PreparedPatternSong.FormNode>{new(){Id=0,Name="Song",Path="Song"}};
        int Group(string path)
        {
            if(string.IsNullOrWhiteSpace(path)||path=="Song")return 0;
            var parts=path.Split('/',StringSplitOptions.RemoveEmptyEntries);int parent=0;
            foreach(var part in parts.Where(p=>p!="Song"))
            {var found=nodes.FirstOrDefault(n=>n.Parent==parent&&n.Family<0&&n.Name==part);if(found==null){found=new(){Id=nodes.Count,Parent=parent,Name=part,Path=nodes[parent].Path+"/"+part};nodes.Add(found);}parent=found.Id;}
            return parent;
        }
        foreach(var section in song.Sections)
        {
            int parent=Group(section.ParentPath);
            var family=nodes.FirstOrDefault(n=>n.Parent==parent&&n.Family==section.Family);
            if(family==null){family=new(){Id=nodes.Count,Parent=parent,Family=section.Family,Name=section.Name,Path=nodes[parent].Path+"/"+section.Name};nodes.Add(family);}
            section.Node=family.Id;
        }
        foreach(var node in nodes)
        {
            node.Children=nodes.Where(n=>n.Parent==node.Id).Select(n=>n.Id).ToArray();var route=new List<PreparedPatternSong.Route>();double turns=0;int previous=0;
            if(node.Children.Length>0)for(int i=0;i<song.Sections.Length;i++)
            {
                int child=song.Sections[i].Node;while(child>0&&nodes[child].Parent!=node.Id)child=nodes[child].Parent;
                int rank=Array.IndexOf(node.Children,child);if(rank<0)continue;
                if(route.Count==0)turns=rank/(double)node.Children.Length;
                else turns+=(rank-previous+node.Children.Length)%node.Children.Length/(double)node.Children.Length;
                route.Add(new(){Section=i,Child=child,Turns=turns});previous=rank;
            }
            node.Route=route.ToArray();
        }
        song.Form=nodes.ToArray();
    }
    static void Verify(PreparedPatternSong song,MidiCycleAnalysis.Hit[] originals)
    {
        var recovered=new List<(int track,int channel,double beat,double length,int pitch,float velocity)>();
        foreach(var s in song.Sections)foreach(var lane in s.Lanes)foreach(var play in lane.Plays)
        {
            var t=song.Templates[play.Template];var v=t.Variants[play.Variant];
            for(int i=0;i<t.Slots.Length;i++){var h=t.Slots[i];recovered.Add((lane.Track,lane.Channel,play.Start+h.Beat+v.BeatOffsets[i],h.Length+v.LengthOffsets[i],h.Pitch+v.Transpose+(v.PitchDelta.Length==0?0:v.PitchDelta[i]),v.Velocities[i]));}
        }
        var expected=originals.Select(h=>(track:h.Track,channel:h.Channel,beat:h.Beat,length:h.Length,pitch:h.Pitch,velocity:h.Velocity)).OrderBy(h=>h.track).ThenBy(h=>h.channel).ThenBy(h=>h.beat).ThenBy(h=>h.pitch).ThenBy(h=>h.length).ToArray();
        var actual=recovered.OrderBy(h=>h.track).ThenBy(h=>h.channel).ThenBy(h=>h.beat).ThenBy(h=>h.pitch).ThenBy(h=>h.length).ToArray();
        if(expected.Length!=actual.Length)throw new InvalidDataException("Pattern compression lost note events.");
        for(int i=0;i<actual.Length;i++){var a=actual[i];var e=expected[i];if(a.track!=e.track||a.channel!=e.channel||a.pitch!=e.pitch||Math.Abs(a.beat-e.beat)>1e-8||Math.Abs(a.length-e.length)>1e-8||Math.Abs(a.velocity-e.velocity)>1e-6)throw new InvalidDataException("Pattern reconstruction changed a note.");}
    }
}
