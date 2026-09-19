using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Midi;

// Measure-relative pattern compression, inspired by AlgoRhythmAnalyzer.
// Exact 1/24-quarter quantization; timing/duration matches can optionally include pitch.
public sealed class MidiCycleAnalysis
{
    [Serializable] public sealed class Hit
    {
        public double Beat, Length;
        public int Pitch, Track, Channel;
        public float Velocity;
        public int RippleFrequency;
        public float LowHz,HighHz,DecaySeconds;
        public float RippleRadius,RippleWidth,StrikeRadius;
    }
    [Serializable] public sealed class Bar { public double Start, End; public int Numerator, Denominator; }
    public sealed class Occurrence { public int Bar; public double Beat, Seconds; public List<Hit> Hits; }
    public sealed class Pattern
    {
        public int Id, Track, Channel, Bars;
        public string Name;
        public double Beats;
        public List<Hit> Hits;
        public readonly List<Occurrence> Occurrences=new();
    }
    public readonly List<Pattern> Patterns=new();
    public readonly List<Bar> Measures=new();
    public readonly List<Hit> Notes=new();
    public readonly List<(double Beat,string Label)> Markers=new();
    readonly List<(double beat,double seconds,double tempo)> tempos=new();
    public int NoteCount;
    public double EndBeat;
    public PreparedPatternSong.Tempo[] ExportTempos()=>tempos.Select(t=>new PreparedPatternSong.Tempo{Beat=t.beat,Seconds=t.seconds,Microseconds=t.tempo}).ToArray();
    public static MidiCycleAnalysis Restore(PreparedPatternSong data)
    {
        var r=new MidiCycleAnalysis{EndBeat=data.EndBeat,NoteCount=data.Notes.Length};
        r.tempos.AddRange(data.Tempos.Select(t=>(t.Beat,t.Seconds,t.Microseconds)));r.Measures.AddRange(data.Measures);r.Notes.AddRange(data.Notes);
        foreach(var d in data.Disks){var p=new Pattern{Id=d.Id,Track=d.Track,Channel=d.Channel,Bars=d.Bars,Name=d.Name,Beats=d.Beats,Hits=d.Hits.ToList()};
            p.Occurrences.AddRange(d.Visits.Select(v=>new Occurrence{Bar=v.Bar,Beat=v.Beat,Seconds=v.Seconds,Hits=v.Hits.ToList()}));r.Patterns.Add(p);}
        return r;
    }
    public double BeatAt(double seconds)
    {
        int lo=0,hi=tempos.Count-1;
        while(lo<hi){int mid=(lo+hi+1)/2;if(tempos[mid].seconds<=seconds)lo=mid;else hi=mid-1;}
        var t=tempos[lo];return t.beat+(seconds-t.seconds)*1000000/t.tempo;
    }
    public double SecondsAt(double beat)
    {
        int lo=0,hi=tempos.Count-1;
        while(lo<hi){int mid=(lo+hi+1)/2;if(tempos[mid].beat<=beat)lo=mid;else hi=mid-1;}
        var t=tempos[lo];return t.seconds+(beat-t.beat)*t.tempo/1000000;
    }
    public static MidiCycleAnalysis Analyze(MidiFile file,bool matchPitch)
    {
        if(file.FileFormat==2 || file.DeltaTicksPerQuarterNote<=0)throw new ArgumentException("Pattern analysis needs format 0/1 PPQ MIDI.");
        var result=new MidiCycleAnalysis();
        double ppq=file.DeltaTicksPerQuarterNote;
        var events=new List<(int track,MidiEvent e)>();
        for(int t=0;t<file.Tracks;t++)foreach(var e in file.Events[t])events.Add((t,e));
        var ordered=events.OrderBy(x=>x.e.AbsoluteTime).ToList();
        result.EndBeat=ordered.Count==0?0:ordered.Last().e.AbsoluteTime/ppq;
        result.tempos.Add((0,0,500000));
        foreach(var entry in ordered)
        {
            if(entry.e is TempoEvent tempo && tempo.MicrosecondsPerQuarterNote>0)
            {
                double beat=entry.e.AbsoluteTime/ppq;
                result.tempos.Add((beat,result.SecondsAt(beat),tempo.MicrosecondsPerQuarterNote));
            }
        }
        var meters=ordered.Where(x=>x.e is TimeSignatureEvent).GroupBy(x=>x.e.AbsoluteTime)
            .Select(g=>(beat:g.Key/ppq,meter:(TimeSignatureEvent)g.Last().e)).ToList();
        int meterIndex=0,numerator=4,denominator=4; double start=0;
        while(start<result.EndBeat-.000001)
        {
            while(meterIndex<meters.Count && meters[meterIndex].beat<=start+.000001)
            {var m=meters[meterIndex++].meter;numerator=Math.Max(1,m.Numerator);denominator=1<<Math.Min(8,m.Denominator);}
            double end=Math.Min(result.EndBeat,start+numerator*4.0/denominator);
            if(meterIndex<meters.Count)end=Math.Min(end,meters[meterIndex].beat);
            result.Measures.Add(new Bar{Start=start,End=end,Numerator=numerator,Denominator=denominator});start=end;
        }
        var hits=new List<Hit>();
        foreach(var entry in ordered)
        {
            if(entry.e is NoteOnEvent on && on.Velocity>0)
            {
                double beat=on.AbsoluteTime/ppq;
                hits.Add(new Hit{Beat=beat,Length=Math.Max(1/ppq,((on.OffEvent?.AbsoluteTime??on.AbsoluteTime+1)-on.AbsoluteTime)/ppq),
                    Pitch=on.NoteNumber,Velocity=on.Velocity/127f,Track=entry.track,Channel=on.Channel});
            }
        }
        result.NoteCount=hits.Count;
        result.Notes.AddRange(hits);
        foreach(var entry in ordered)
            if(entry.e is TextEvent marker && marker.MetaEventType==MetaEventType.Marker && !string.IsNullOrWhiteSpace(marker.Text))
                result.Markers.Add((entry.e.AbsoluteTime/ppq,marker.Text.Trim()));
        foreach(var lane in hits.GroupBy(h=>(h.Track,h.Channel)))
        {
            string trackName=file.Events[lane.Key.Track].OfType<TextEvent>().FirstOrDefault(t=>t.MetaEventType==MetaEventType.SequenceTrackName)?.Text;
            string name=$"T{lane.Key.Track+1} · {(lane.Key.Channel==10?"drums":"ch "+lane.Key.Channel)}";
            if(!string.IsNullOrWhiteSpace(trackName))name+=" · "+trackName;
            var byBar=new List<Hit>[result.Measures.Count];
            for(int b=0;b<byBar.Length;b++)byBar[b]=new List<Hit>();
            int bar=0;
            foreach(var h in lane)
            {
                while(bar<byBar.Length && h.Beat>=result.Measures[bar].End-.0000001)bar++;
                if(bar<byBar.Length)byBar[bar].Add(h);
            }
            var signatures=new string[byBar.Length];
            for(int b=0;b<byBar.Length;b++)signatures[b]=Signature(byBar[b],result.Measures[b].Start,matchPitch);
            foreach(int length in new[]{1,2,4,8})
            {
                var groups=new Dictionary<string,Pattern>();
                // Non-overlapping phrase windows, anchored at the start of the track.
                for(int b=0;b+length<=byBar.Length;b+=length)
                {
                    var first=result.Measures[b];double duration=first.End-first.Start;
                    bool uniform=true;
                    for(int j=0;j<length;j++)
                    {var m=result.Measures[b+j];if(m.Numerator!=first.Numerator || m.Denominator!=first.Denominator || Math.Abs(m.End-m.Start-duration)>.00001)uniform=false;}
                    if(!uniform)continue;
                    if(length>1)
                    {
                        bool primitive=true;
                        foreach(int divisor in new[]{1,2,4})if(divisor<length && Enumerable.Range(0,length).All(j=>signatures[b+j]==signatures[b+j%divisor]))primitive=false;
                        if(!primitive)continue;
                    }
                    var block=Enumerable.Range(b,length).SelectMany(i=>byBar[i]).ToList();
                    if(block.Count==0)continue;
                    string signature=$"{first.Numerator}/{first.Denominator}:{Q(duration)}:"+string.Join("|",signatures.Skip(b).Take(length));
                    if(!groups.TryGetValue(signature,out var pattern))
                    {
                        pattern=new Pattern{Track=lane.Key.Track,Channel=lane.Key.Channel,Name=name,Bars=length,Beats=duration*length,
                            Hits=block.Select(h=>new Hit{Beat=h.Beat-first.Start,Length=h.Length,Pitch=h.Pitch,Velocity=h.Velocity,Track=h.Track,Channel=h.Channel}).ToList()};
                        groups.Add(signature,pattern);
                    }
                    pattern.Occurrences.Add(new Occurrence{Bar=b,Beat=first.Start,Seconds=result.SecondsAt(first.Start),
                        Hits=block.Select(h=>new Hit{Beat=h.Beat-first.Start,Length=h.Length,Pitch=h.Pitch,Velocity=h.Velocity,Track=h.Track,Channel=h.Channel}).ToList()});
                    // Merge velocity evidence from repetitions without changing the rhythm signature.
                    for(int j=0;j<Math.Min(block.Count,pattern.Hits.Count);j++)pattern.Hits[j].Velocity=Math.Max(pattern.Hits[j].Velocity,block[j].Velocity);
                }
                result.Patterns.AddRange(groups.Values.Where(p=>length==1 || p.Occurrences.Count>1));
            }
        }
        result.Patterns.Sort((a,b)=>{int c=b.Occurrences.Count.CompareTo(a.Occurrences.Count);if(c!=0)return c;c=a.Track.CompareTo(b.Track);return c!=0?c:a.Bars.CompareTo(b.Bars);});
        for(int i=0;i<result.Patterns.Count;i++)result.Patterns[i].Id=i+1;
        return result;
    }
    static long Q(double beat)=>(long)Math.Round(beat*24,MidpointRounding.AwayFromZero);
    static string Signature(IEnumerable<Hit> hits,double start,bool pitch) => string.Join(";",hits.Select(h=>$"{Q(h.Beat-start)},{Q(h.Length)},{(pitch?h.Pitch:0)}").OrderBy(s=>s,StringComparer.Ordinal));
}
