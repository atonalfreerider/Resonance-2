// Each section's progression loop as an umbilic-grammar word plus Roman numerals.
// The word starts on the key's own surface (I = M on the tonic surface, i = m on the
// surface a major third below), so the same progression gives the same word in every
// key: I–V–vi–IV is always 0M > 3PM > 2m > 0M.
public static class GrammarWords
{
    public static (int key, bool minor) KeyAt(PreparedPatternSong song, double beat, Func<double, double> seconds)
    {
        double time = seconds(beat);
        var frame = song.Frames.LastOrDefault(f => f.Time <= time + 1e-6 && f.Key >= 0) ?? song.Frames.FirstOrDefault(f => f.Key >= 0);
        if (frame != null) return (frame.Key, frame.Minor);
        return song.Key >= 0 ? (song.Key, song.Minor) : (-1, false);
    }

    public static void Build(PreparedPatternSong song)
    {
        var cycles = MidiCycleAnalysis.Restore(song);
        foreach (var section in song.Sections)
        {
            var (key, minor) = KeyAt(song, section.Start, cycles.SecondsAt);
            if (key < 0) { key = section.KeyRoot >= 0 ? section.KeyRoot : 0; minor = section.KeyMinor; }
            int from = HarmonyModel.KeyHome(key, minor).surface;
            foreach (var chord in section.Chords)
            {
                if (chord.Rest) { chord.Token = ""; chord.Roman = "–"; continue; }
                var home = HarmonyModel.Home(chord.Root, chord.Quality);
                chord.Token = HarmonyModel.Token(from, home.surface, home.obj);
                chord.Roman = HarmonyModel.Roman(chord.Root, chord.Quality, key, minor);
                from = home.surface;
            }
        }
    }

    public static string Word(PreparedPatternSong.Section section) => string.Join(" > ", section.Chords.Where(c => !c.Rest).Select(c => c.Token));

    // How far the hierarchy compresses the song, in the units the wheel displays.
    public static string Summary(PreparedPatternSong song)
    {
        int families = song.Sections.Select(s => s.Family).Distinct().Count();
        int changes = song.Chords.Count(c => !c.Rest);
        int loopChords = song.Sections.GroupBy(s => s.Family).Sum(g => g.First().Chords.Count(c => !c.Rest));
        int words = song.Sections.GroupBy(s => s.Family).Select(g => Word(g.First())).Where(w => w.Length > 0).Distinct().Count();
        return $"{song.Sections.Length} sections → {families} families · {changes} chord changes → {loopChords} loop chords in {words} grammar words · {song.PatternNoteCount} notes → {song.TemplateNoteCount} rhythm slots";
    }
}
