// Display regions describe harmonic context, including gaps between sounding notes.
public static class RegionPhases
{
    public static void Build(PreparedPatternSong song)
    {
        var output=new List<SongFormAnalysis.ChordStep>();
        bool Valid(SongFormAnalysis.ChordStep c)=>!c.Rest&&!c.Quality.Contains("tone")&&!c.Quality.Contains("dyad");
        foreach(var section in song.Sections){
            var evidence=song.Chords.Where(c=>c.End>section.Start&&c.Start<section.End&&Valid(c)).ToArray();
            // Keep note/chord events intact, but do not flash the contextual region for a short A-B-A passing chord.
            evidence=evidence.Where((c,i)=>!(i>0&&i+1<evidence.Length&&c.End-c.Start<.75&&evidence[i-1].Root==evidence[i+1].Root&&evidence[i-1].Quality==evidence[i+1].Quality)).ToArray();
            var current=evidence.FirstOrDefault()??new SongFormAnalysis.ChordStep{Root=song.Key>=0?song.Key:0,Quality=song.Minor?"m":""};
            double start=section.Start;
            string Quality(SongFormAnalysis.ChordStep c)=>c.Quality=="dim"?"dim":c.Quality.StartsWith("m")&&!c.Quality.StartsWith("maj")?"m":"";
            void Emit(double end){if(end>start)output.Add(new(){Start=start,End=end,Root=current.Root,Quality=Quality(current),Energy=1});start=end;}
            foreach(var chord in evidence){
                if(chord.Root==current.Root&&Quality(chord)==Quality(current))continue;
                Emit(Math.Max(section.Start,chord.Start));current=chord;
            }
            Emit(section.End);
        }
        song.RegionPhases=output.ToArray();
        foreach(var section in song.Sections){
            var phases=output.Where(p=>p.Start>=section.Start&&p.End<=section.End).ToArray();
            if(phases.Length==0||Math.Abs(phases[0].Start-section.Start)>1e-6||Math.Abs(phases[^1].End-section.End)>1e-6||phases.Zip(phases.Skip(1),(a,b)=>Math.Abs(a.End-b.Start)).Any(g=>g>1e-6))throw new InvalidDataException("Region phases do not cover the section continuously");
        }
    }
}
