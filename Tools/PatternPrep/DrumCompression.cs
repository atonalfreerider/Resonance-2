using System.Text.Json;

public static class DrumCompression
{
    public static void Build(PreparedPatternSong song){
        var families=new List<PreparedPatternSong.DrumFamily>();var bars=new List<PreparedPatternSong.DrumBar>();
        var variants=new List<Dictionary<string,int>>();var options=new JsonSerializerOptions{IncludeFields=true};
        foreach(var bar in song.Measures){
            var hits=song.Notes.Where(n=>n.Channel==10&&n.Beat>=bar.Start&&n.Beat<bar.End).OrderBy(n=>n.Beat).ThenBy(n=>n.Pitch).Select(n=>{var h=JsonSerializer.Deserialize<MidiCycleAnalysis.Hit>(JsonSerializer.Serialize(n,options),options);h.Beat-=bar.Start;return h;}).ToArray();
            int family=-1;double best=.72;int[] chosen=null;
            foreach(var f in families.Where(f=>f.Numerator==bar.Numerator&&f.Denominator==bar.Denominator)){
                var used=new HashSet<int>();var map=Enumerable.Repeat(-1,hits.Length).ToArray();int matched=0;
                for(int i=0;i<hits.Length;i++){
                    int nearest=Enumerable.Range(0,f.Slots.Length).Where(j=>!used.Contains(j)&&f.Slots[j].RippleFrequency==hits[i].RippleFrequency&&Math.Abs(f.Slots[j].Beat-hits[i].Beat)<=.26).OrderBy(j=>Math.Abs(f.Slots[j].Beat-hits[i].Beat)).DefaultIfEmpty(-1).First();
                    if(nearest>=0){used.Add(nearest);map[i]=nearest;matched++;}
                }
                double score=2.0*matched/Math.Max(1,hits.Length+f.Slots.Length);
                if(score>best){best=score;family=f.Id;chosen=map;}
            }
            if(hits.Length==0){bars.Add(new(){Family=-1,Numerator=bar.Numerator,Denominator=bar.Denominator,Start=bar.Start,End=bar.Start+bar.Numerator*4.0/bar.Denominator,Hits=hits,Slots=Array.Empty<int>()});continue;}
            if(family<0){family=families.Count;families.Add(new(){Id=family,Numerator=bar.Numerator,Denominator=bar.Denominator,Slots=hits.ToArray()});variants.Add(new());chosen=Enumerable.Range(0,hits.Length).ToArray();}
            else{var slots=families[family].Slots.ToList();for(int i=0;i<hits.Length;i++)if(chosen[i]<0){chosen[i]=slots.Count;slots.Add(hits[i]);}families[family].Slots=slots.ToArray();}
            string key=JsonSerializer.Serialize(hits,options);if(!variants[family].TryGetValue(key,out int variant)){variant=variants[family].Count;variants[family].Add(key,variant);}
            bars.Add(new(){Family=family,Variant=variant,Numerator=bar.Numerator,Denominator=bar.Denominator,Start=bar.Start,End=bar.Start+bar.Numerator*4.0/bar.Denominator,Hits=hits,Slots=chosen});
        }
        if(bars.Sum(b=>b.Hits.Length)!=song.Notes.Count(n=>n.Channel==10))throw new InvalidDataException("Drum bar extraction lost events");
        song.DrumFamilies=families.ToArray();song.DrumBars=bars.ToArray();
    }
}
