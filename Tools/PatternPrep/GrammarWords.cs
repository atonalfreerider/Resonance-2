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
        (int, bool) Key(PreparedPatternSong.Section section)
        {
            var (key, minor) = KeyAt(song, section.Start, cycles.SecondsAt);
            return key >= 0 ? (key, minor) : (section.KeyRoot >= 0 ? section.KeyRoot : 0, section.KeyMinor);
        }
        foreach (var section in song.Sections) Spell(section.Chords, Key(section));
        // Fundamentals are spelled in the key of the family's first visit.
        foreach (var pattern in song.Patterns ?? Array.Empty<PreparedPatternSong.Pattern>())
        {
            var first = song.Sections.FirstOrDefault(s => s.Family == pattern.Family);
            if (first == null) continue;
            Spell(pattern.Loop, Key(first));
            pattern.Word = string.Join(" > ", pattern.Loop.Where(c => !c.Rest).Select(c => c.Token));
        }
    }

    static void Spell(IEnumerable<SongFormAnalysis.ChordStep> chords, (int key, bool minor) context)
    {
        var (key, minor) = context;
        int from = HarmonyModel.KeyHome(key, minor).surface;
        foreach (var chord in chords)
        {
            if (chord.Rest) { chord.Token = ""; chord.Roman = "–"; continue; }
            var home = HarmonyModel.Home(chord.Root, chord.Quality);
            chord.Token = HarmonyModel.Token(from, home.surface, home.obj);
            chord.Roman = HarmonyModel.Roman(chord.Root, chord.Quality, key, minor);
            from = home.surface;
        }
    }

    public static string Word(PreparedPatternSong.Section section) => string.Join(" > ", section.Chords.Where(c => !c.Rest).Select(c => c.Token));

    // How far the hierarchy compresses the song, in the units the wheel displays.
    public static string Summary(PreparedPatternSong song)
    {
        int families = song.Sections.Select(s => s.Family).Distinct().Count();
        int changes = song.Chords.Count(c => !c.Rest);
        bool compressed = song.Patterns?.Length > 0;
        int loopChords = compressed ? song.Patterns.Sum(p => p.Loop.Count(c => !c.Rest)) : song.Sections.GroupBy(s => s.Family).Sum(g => g.First().Chords.Count(c => !c.Rest));
        int words = (compressed ? song.Patterns.Select(p => p.Word) : song.Sections.GroupBy(s => s.Family).Select(g => Word(g.First()))).Where(w => w.Length > 0).Distinct().Count();
        string patterns = song.Patterns?.Length > 0 ? $" · {song.SongBars} bars → {song.Patterns.Length} fundamentals of {song.FundamentalBars} bars" : "";
        return $"{song.Sections.Length} sections → {families} families{patterns} · {changes} chord changes → {loopChords} loop chords in {words} grammar words · {song.PatternNoteCount} notes → {song.TemplateNoteCount} rhythm slots";
    }
}
