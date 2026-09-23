// Conservative offline inference. Reviewed keys and authored MIDI signatures take priority.
public static class KeyContext
{
    public sealed record Change(double Beat,int Key,bool Minor);
    public static List<Change> Infer(IEnumerable<MidiCycleAnalysis.Hit> source,double end,int initial,bool minor)
    {
        var notes=source.Where(n=>n.Channel!=10).ToArray();
        var result=new List<Change>{new(0,Math.Max(0,initial),minor)};
        int current=result[0].Key+(minor?12:0),pending=-1;double since=0;
        for(double beat=0;beat<end;beat+=4){
            var weights=new double[12];
            foreach(var n in notes){double overlap=Math.Max(0,Math.Min(n.Beat+n.Length,beat+8)-Math.Max(n.Beat,beat));weights[HarmonyModel.Mod(n.Pitch-21)]+=overlap*n.Velocity;}
            double total=weights.Sum();if(total<2){pending=-1;continue;}
            double Score(int state){int root=state%12;bool m=state>=12;int[] scale=m?new[]{0,2,3,5,7,8,10}:new[]{0,2,4,5,7,9,11};double value=0;
                for(int pc=0;pc<12;pc++){int rel=HarmonyModel.Mod(pc-root);double w=scale.Contains(rel)?1:-2.5;if(rel==0)w+=.55;if(rel==(m?3:4))w+=.15;if(rel==7)w+=.2;value+=weights[pc]*w;}return value/total;}
            var ranked=Enumerable.Range(0,24).OrderByDescending(Score).ToArray();int best=ranked[0];
            // A single borrowed/passing chord never supplies enough sustained evidence.
            bool tonic=weights[best%12]/total>.16;
            if(best==current||Score(best)-Score(current)<.22||Score(best)-Score(ranked[1])<.045||!tonic){pending=-1;continue;}
            if(pending!=best){pending=best;since=beat;continue;}
            if(beat-since<8)continue;
            current=best;result.Add(new(since,best%12,best>=12));pending=-1;
        }
        return result;
    }
    // Keys for every frame, the detected key changes, and the tensions inside each key.
    // Reviewed keys stay locked (tensions are still found). Several authored signatures are kept.
    // One opening signature, or none, is only a starting point: modulations are detected from
    // the chord timeline (KeyAnalysis).
    public static void Apply(PreparedPatternSong song,SongSettings settings,MidiCycleAnalysis cycles)
    {
        bool reviewed=settings.KeySource.Contains("reviewed",StringComparison.OrdinalIgnoreCase)||settings.KeySource.Contains("known",StringComparison.OrdinalIgnoreCase);
        var signed=song.Frames.Where(f=>f.Key>=0).ToList();
        // Many exporters always write "major": keep the configured tonic when it shares the collection.
        if(settings.Key>=0)foreach(var f in signed)
            if(HarmonyModel.Mod(f.Key+(f.Minor?3:0))==HarmonyModel.Mod(settings.Key+(settings.Minor?3:0))){f.Key=settings.Key;f.Minor=settings.Minor;}
        var authored=new List<(double time,int key,bool minor)>();
        foreach(var f in signed)if(authored.Count==0||authored[^1].key!=f.Key||authored[^1].minor!=f.Minor)authored.Add((f.Time,f.Key,f.Minor));
        var chords=song.Chords??Array.Empty<SongFormAnalysis.ChordStep>();
        List<KeyAnalysis.Region> regions;
        if(reviewed||!settings.InferKeyChanges||authored.Count>1||cycles==null)
        {
            if((reviewed||!settings.InferKeyChanges)&&settings.Key>=0)foreach(var f in song.Frames){f.Key=settings.Key;f.Minor=settings.Minor;}
            if(cycles==null)return;
            regions=new();
            foreach(var f in song.Frames.Where(f=>f.Key>=0))
            {
                double beat=cycles.BeatAt(f.Time);
                if(regions.Count==0||regions[^1].Key!=f.Key||regions[^1].Minor!=f.Minor)regions.Add(new KeyAnalysis.Region{Start=regions.Count==0?0:beat,End=song.EndBeat,Key=f.Key,Minor=f.Minor});
                if(regions.Count>1)regions[^2].End=regions[^1].Start;
            }
            if(regions.Count==0)regions.Add(new KeyAnalysis.Region{Start=0,End=song.EndBeat,Key=Math.Max(0,settings.Key),Minor=settings.Minor});
        }
        else
        {
            int initial=settings.Key>=0?settings.Key:authored.Count==1?authored[0].key:-1;bool minor=settings.Key>=0?settings.Minor:authored.Count==1&&authored[0].minor;
            double beatsPerBar=song.Measures?.Length>0?song.Measures.GroupBy(m=>Math.Round(m.End-m.Start,3)).OrderByDescending(g=>g.Count()).First().Key:4;
            regions=KeyAnalysis.Regions(chords,initial,minor,beatsPerBar);
            regions[^1].End=Math.Max(regions[^1].End,song.EndBeat);
            foreach(var f in song.Frames){double beat=cycles.BeatAt(f.Time);var region=regions.LastOrDefault(r=>r.Start<=beat+1e-6)??regions[0];f.Key=region.Key;f.Minor=region.Minor;}
            song.KeySource=regions.Count>1?(authored.Count==1?"MIDI signature; key changes detected offline (review)":"Offline chord-timeline keys; key changes detected (review)")
                :(authored.Count==1||settings.Key>=0?song.KeySource??"":"Offline chord-timeline key (review)");
        }
        (song.KeyChanges,song.Tensions)=KeyAnalysis.Describe(chords,regions,song.Sections);
    }
    public static void SelfTest()
    {
        var notes=new List<MidiCycleAnalysis.Hit>();
        void Chord(double beat,int[] pitches,double length=4){foreach(int pc in pitches)notes.Add(new(){Beat=beat,Length=length,Pitch=57+pc,Channel=1,Velocity=.8f});}
        for(int b=0;b<64;b+=4)Chord(b,b<32?new[]{0,4,7}:new[]{2,6,9});
        var changes=Infer(notes,64,0,false);
        if(!changes.Any(c=>c.Key==2&&!c.Minor&&c.Beat>=24))throw new Exception("Sustained modulation was not found");
        notes.Clear();for(int b=0;b<64;b+=4)Chord(b,new[]{0,4,7});Chord(20,new[]{2,6,9},.5);
        if(Infer(notes,64,0,false).Count!=1)throw new Exception("Passing chord caused a modulation");
        var song=new PreparedPatternSong{Frames=new[]{new PreparedPatternSong.Frame{Key=0},new PreparedPatternSong.Frame{Key=7}}};
        Apply(song,new SongSettings{Key=3,KeySource="estimated"},null);
        if(song.Frames[1].Key!=7)throw new Exception("MIDI key change overwritten");
        Apply(song,new SongSettings{Key=0,KeySource="User reviewed key"},null);
        if(song.Frames.Any(f=>f.Key!=0))throw new Exception("Reviewed key not preserved");
        song.Sections=new[]{new PreparedPatternSong.Section{Start=0,End=8}};
        song.Chords=new[]{new SongFormAnalysis.ChordStep{Start=0,End=3,Root=0,Quality=""},new SongFormAnalysis.ChordStep{Start=3,End=3.5,Root=2,Quality=""},new SongFormAnalysis.ChordStep{Start=3.5,End=8,Root=0,Quality=""}};
        RegionPhases.Build(song);
        if(song.RegionPhases.Length!=1||song.RegionPhases[0].End!=8||song.Chords.Length!=3)throw new Exception("Passing region smoothing lost harmonic context or events");
        Console.WriteLine("PASS: sustained modulation, passing-chord rejection, authored signatures, reviewed key and passing region continuity");
    }
}
