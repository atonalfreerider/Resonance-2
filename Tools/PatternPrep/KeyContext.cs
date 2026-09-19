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
    public static void Apply(PreparedPatternSong song,SongSettings settings,MidiCycleAnalysis cycles)
    {
        bool reviewed=settings.KeySource.Contains("reviewed",StringComparison.OrdinalIgnoreCase);
        if(reviewed||!settings.InferKeyChanges){if(settings.Key>=0)foreach(var f in song.Frames){f.Key=settings.Key;f.Minor=settings.Minor;}return;}
        if(song.Frames.Any(f=>f.Key>=0))return; // Preserve supplied MIDI key changes.
        var changes=Infer(song.Notes,song.EndBeat,settings.Key,settings.Minor);
        foreach(var f in song.Frames){double beat=cycles.BeatAt(f.Time);var change=changes.Last(c=>c.Beat<=beat);f.Key=change.Key;f.Minor=change.Minor;}
        song.KeySource="Offline sustained tonal context; inferred changes require review";
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
