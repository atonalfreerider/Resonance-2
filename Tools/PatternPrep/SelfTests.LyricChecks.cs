using NAudio.Midi;

public static partial class SelfTests
{
    // The lyric fixture through the offline pipeline: instrument patterns per lane, lyric sync
    // from events and from notes alone, meter, rhyme and matchups.
    static void LyricChecks()
    {
        Check(LyricEnglish.Syllables("day").Length == 1 && LyricEnglish.Syllables("little").SequenceEqual(new[] { "lit", "tle" }) && LyricEnglish.Syllables("twinkle").SequenceEqual(new[] { "twin", "kle" }) && LyricEnglish.Syllables("shines").Length == 1 && LyricEnglish.Syllables("wonder").SequenceEqual(new[] { "won", "der" }), "syllables: day, lit-tle, twin-kle, shines, won-der");
        string Key(string w) { var s = LyricEnglish.Syllables(w); return LyricEnglish.RhymeKey(w, s, LyricEnglish.Stress(w, s.Length)).perfect; }
        foreach (var (a, b) in new[] { ("star", "are"), ("high", "sky"), ("gone", "upon"), ("light", "night"), ("day", "May"), ("shines", "declines"), ("see", "thee") })
            Check(Key(a) == Key(b), $"{a} rhymes with {b}: {Key(a)} / {Key(b)}");
        Check(Key("star") != Key("sky") && Key("day") != Key("dark"), "star/sky and day/dark do not rhyme");
        Check(LyricEnglish.Scan("/x/x/x/").meter == "trochaic tetrameter (catalectic)" && LyricEnglish.Scan("x/x/x/x/x/").meter == "iambic pentameter" && LyricEnglish.Scan("/xx/xx/xx/xx").meter.StartsWith("dactylic tetrameter"), "meter scan: trochaic tetrameter, iambic pentameter, dactylic tetrameter");

        string folder = Path.Combine(Path.GetTempPath(), "PatternPrepLyrics"); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "lyrics-song.mid");
        var saved = Console.Out; Console.SetOut(TextWriter.Null);
        try { WriteLyricFixture(path); } finally { Console.SetOut(saved); }
        PreparedPatternSong Prepare(MidiFile midi)
        {
            var cycles = MidiCycleAnalysis.Analyze(midi, true);
            var names = Enumerable.Range(0, midi.Tracks).Select(t => midi.Events[t].OfType<TextEvent>().FirstOrDefault(e => e.MetaEventType == MetaEventType.SequenceTrackName)?.Text ?? "").ToArray();
            var settings = new SongSettings { Key = 3, LeadVocalTrack = 3, SectionBoundaries = LyricBoundaries };
            var analysis = FormAnalysis.Build(cycles, settings, names);
            var song = PreparedPatternSong.Capture(cycles, analysis.Form);
            song.TrackNames = names; song.LeadVocalTrack = 3; song.Key = 3;
            for (int i = 0; i < song.Sections.Length; i++) { song.Sections[i].Label = analysis.Sections[i].Label; song.Sections[i].Role = analysis.Sections[i].Role; }
            FormAnalysis.Apply(analysis, song);
            song.Frames = new[] { new PreparedPatternSong.Frame { Time = 0, Key = 3 } };
            InstrumentPatterns.Build(song, cycles, analysis.Grid, 3, false);
            Lyrics.Build(song, cycles, midi, Path.Combine(folder, "lyrics.txt"), null);
            return song;
        }
        var data = Prepare(new MidiFile(path, false));
        var keys = data.Parts.Single(p => p.Name == "Keys"); var vocal = data.Parts.First();
        Check(vocal.Vocal && vocal.Name == "Lead Vocal" && vocal.Grammar == "· A · A · A′ ·", "the vocal lane is first and sings one pattern three times, the third with a chord variation: " + vocal.Grammar);
        Check(keys.Grammar == "A B C×2 B C×2 C′ B′ D×2", "keys: verse pattern B returns varied (Am–D), the rap loop C repeats and its third pass varies: " + keys.Grammar);
        var third = keys.Plays.First(p => p.Pattern >= 0 && keys.Patterns[p.Pattern].Letter == "B" && p.Variation > 0);
        Check(third.Changed.Length == 2 && Math.Abs(third.Changed[0] - 28) < 1e-6 && Math.Abs(third.Changed[1] - 32) < 1e-6, "the changed span is the verse's eighth bar: " + string.Join(",", third.Changed));
        var rap = keys.Plays.Where(p => p.Pattern >= 0 && keys.Patterns[p.Pattern].Letter == "C").ToList();
        Check(rap.Count == 5 && rap[0].Run == 2 && rap[1].Repeat == 2 && rap[4].Run == 3 && rap[4].Repeat == 3, "rap loop runs: ×2 then ×3 with the variation as its third repeat");
        Check(keys.FundamentalBars == 19 && keys.Bars == 60, $"keys: 60 bars → {keys.FundamentalBars} bars of fundamentals");

        var lyrics = data.Lyrics;
        Check(lyrics != null && lyrics.Sync == "MIDI lyric events" && lyrics.Track == 3 && lyrics.Syllables.Length == 251 && lyrics.Lines.Length == 32, $"lyric events sync all 251 syllables to the vocal: {lyrics?.Sync} track {lyrics?.Track}");
        var stanza = lyrics.Stanzas.ToDictionary(s => s.Name);
        Check(stanza["Verse 1"].Scheme == "AABBAA" && stanza["Verse 1"].Meter == "trochaic tetrameter" && !stanza["Verse 1"].Spoken, "Twinkle: AABBAA in trochaic tetrameter");
        Check(stanza["Rap 1"].Spoken && stanza["Rap 1"].Meter == "trochaic tetrameter" && stanza["Rap 2"].Meter == "iambic pentameter" && stanza["Rap 2"].Scheme == "ABABCC", "Hiawatha is trochaic tetrameter; the sonnet lines iambic pentameter, ABAB CC");
        var hiawatha = lyrics.Lines.Skip(stanza["Rap 1"].FirstLine).Take(8).ToArray();
        Check(hiawatha[0].FrontRhyme == "By the" && hiawatha[0].FrontGroup == hiawatha[1].FrontGroup && hiawatha[5].FrontRhyme == "Rose the" && hiawatha[3].FrontKind == "alliteration" && hiawatha[3].FrontGroup == hiawatha[4].FrontGroup, "front rhyme: By the / Rose the repeat, Daughter / Dark alliterate");
        Check(stanza["Rap 1"].Section == 2 && stanza["Rap 2"].Section == 4 && stanza["Verse 3"].Section == 5, "stanzas sit in their sections, a pickup notwithstanding");
        var go = lyrics.Lines[stanza["Verse 3"].FirstLine + 2];
        Check(go.Meter == "iambic tetrameter" && go.Count == 8, "He could not see which way to go: eight syllables, iambic");
        var match = lyrics.Matches.Single(m => m.B == stanza["Verse 3"].FirstLine + 2);
        Check(match.A == stanza["Verse 2"].FirstLine + 2 && match.Note.Contains("+1 syllable") && match.Note.Contains("iambic") && match.Score < .9, "matchup flags the extra syllable and the change of foot: " + match.Note);
        Check(lyrics.Matches.Any(m => m.A == 0 && m.B == stanza["Verse 2"].FirstLine && m.Note.Contains("same beats")), "verse 1 and verse 2 lines share their beats");
        var star = lyrics.Syllables.First(x => x.Line == 0 && x.Text == "star");
        Check(star.Vibrato > .2 && star.Vibrato < .5 && star.VibratoRate > 4.5 && star.VibratoRate < 6.5 && star.VibratoStart > star.Start, $"vibrato on the held star: {star.Vibrato:0.00} st at {star.VibratoRate:0.0} Hz");
        Check(lyrics.Syllables.First(x => x.Line == 0 && x.Text == "Twin").Vibrato == 0, "no vibrato on a short note");
        var day = lyrics.Syllables.First(x => x.Text == "day"); var shall = lyrics.Syllables.First(x => x.Text == "Shall");
        Check(day.Spoken && day.Teeth >= 6 && day.Metric == 2 && shall.Metric == 0, $"the sonnet's line-end stress is held over {day.Teeth} teeth from a downbeat; its pickup is an upbeat");
        var by = lyrics.Syllables.Where(x => x.Line == stanza["Rap 1"].FirstLine).ToArray();
        Check(new string(by.Select(x => x.Metric >= 1 ? '/' : 'x').ToArray()) == "/x/x/x/x" && by[2].Emphasis > by[3].Emphasis, "trochees: stresses on the beats, unstressed on the upbeats, emphasis follows");

        // Without lyric events: sung syllables fall on the vocal's notes in order, spoken ones
        // are placed on the beat grid.
        var bare = new MidiFile(path, false);
        for (int t = 0; t < bare.Tracks; t++) foreach (var e in bare.Events[t].OfType<TextEvent>().Where(e => e.MetaEventType == MetaEventType.Lyric).ToList()) bare.Events[t].Remove(e);
        var fromNotes = Prepare(bare).Lyrics;
        Check(fromNotes.Sync == "vocal notes + estimated spoken placement" && fromNotes.Track == 3, "without events: " + fromNotes.Sync);
        var sung = lyrics.Syllables.Where(x => !x.Spoken).ToArray(); var guessed = fromNotes.Syllables.Where(x => !x.Spoken).ToArray();
        Check(sung.Length == guessed.Length && sung.Zip(guessed).All(p => Math.Abs(p.First.Start - p.Second.Start) < 1e-6 && p.First.Pitch == p.Second.Pitch), "every sung syllable lands on the same note as with events");
        Console.WriteLine("PASS: lyrics — syllables, rhyme keys and meter; events and note-only sync; trochaic and iambic stanzas, end and front rhyme, matchups, vibrato and held syllables");
        Console.WriteLine("PASS: instrument patterns — per-lane chords, runs, variations (a changed bar, a varied pass) and lane grammars");
    }
}
