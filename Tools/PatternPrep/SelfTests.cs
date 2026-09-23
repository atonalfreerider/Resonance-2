// Synthetic fixtures for the offline form pipeline (run: PatternPrep --self-test).
public static class SelfTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }

    sealed class Score
    {
        public readonly List<MidiCycleAnalysis.Hit> Notes = new();
        public readonly List<MidiCycleAnalysis.Bar> Bars = new();
        public double Beat;
        public void Bar(int beats = 4) { Bars.Add(new MidiCycleAnalysis.Bar { Start = Beat, End = Beat + beats, Numerator = beats, Denominator = 4 }); }
        public void Note(double beat, double length, int pitch, float velocity, int channel = 1, int track = 1) =>
            Notes.Add(new MidiCycleAnalysis.Hit { Beat = beat, Length = length, Pitch = pitch, Velocity = velocity, Channel = channel, Track = track });
        // One bar of chord (root in MIDI numbers), bass, a melody figure and optional drums.
        public void Chord(int root, bool minor, int[] melody, float loudness, bool drums, int beats = 4)
        {
            Bar(beats);
            int third = minor ? 3 : 4;
            foreach (int x in new[] { 0, third, 7 }) Note(Beat, beats, root + 12 + x, .5f * loudness, 1, 1);
            Note(Beat, beats, root - 12, .7f * loudness, 2, 2);
            // The tune follows the chord's quality: a major third becomes minor over a minor chord.
            for (int i = 0; i < melody.Length; i++) Note(Beat + i * beats / (double)melody.Length, beats / (double)melody.Length, root + 24 + (minor && melody[i] % 12 == 4 ? melody[i] - 1 : melody[i]), .8f * loudness, 3, 3);
            if (drums) for (int b = 0; b < beats; b++) { Note(Beat + b, .1, b % 2 == 0 ? 36 : 38, .8f, 10, 4); Note(Beat + b + .5, .1, 42, .5f, 10, 4); }
            Beat += beats;
        }
        // One bar of a drum groove (style 1 verse, 2 chorus, 3 bridge) starting at a bar's
        // first beat; a fill replaces the last two beats with snare and toms.
        public void Groove(double start, int beats, int style, bool fill)
        {
            for (int b = 0; b < beats; b++)
            {
                double t = start + b; bool tail = fill && b >= beats - 2;
                if (tail) { foreach (double x in new[] { 0, .25, .5, .75 }) Note(t + x, .1, x < .5 ? 38 : b == beats - 1 ? 45 : 47, .75f, 10, 4); continue; }
                switch (style)
                {
                    case 1: Note(t, .1, b % 2 == 0 ? 36 : 38, .8f, 10, 4); Note(t + .5, .1, 42, .45f, 10, 4); Note(t, .1, 42, .5f, 10, 4); break;
                    case 2: Note(t, .1, b % 2 == 0 ? 36 : 38, .9f, 10, 4); if (b % 2 == 1) Note(t + .5, .1, 36, .7f, 10, 4); Note(t + .5, .1, 46, .55f, 10, 4); if (b == 0) Note(t, .1, 49, .7f, 10, 4); break;
                    case 3: Note(t, .1, 51, .55f, 10, 4); if (b == 0) Note(t, .1, 36, .7f, 10, 4); if (b == 2) Note(t, .1, 38, .6f, 10, 4); break;
                }
            }
        }
        public MidiCycleAnalysis Cycles() => MidiCycleAnalysis.Restore(new PreparedPatternSong
        {
            EndBeat = Beat, Tempos = new[] { new PreparedPatternSong.Tempo { Beat = 0, Seconds = 0, Microseconds = 500000 } },
            Measures = Bars.ToArray(), Notes = Notes.ToArray(), Disks = Array.Empty<PreparedPatternSong.Disk>()
        });
    }

    const int C = 48, D = 50, E = 52, F = 53, G = 55, A = 57, Bb = 58;

    // PatternPrep --write-fixture name out.mid: an original synthetic song as a playable MIDI
    // (keys, bass, lead vocal line, drums), for previewing the pattern wheels.
    public static void WriteFixture(string name, string path)
    {
        var (score, bpm) = name switch
        {
            "pop" => (PopScore(), 112), "variation" => (VariationScore().score, 116), "alternating" => (AlternatingScore(), 108), "strain" => (StrainScore(), 96), "ternary" => (TernaryScore(), 88),
            _ => throw new ArgumentException("fixtures: pop, variation, alternating, strain, ternary")
        };
        const int ppq = 480;
        var events = new NAudio.Midi.MidiEventCollection(1, ppq);
        long Tick(double beat) => (long)Math.Round(beat * ppq);
        events.AddTrack(); events.AddEvent(new NAudio.Midi.TempoEvent(60000000 / bpm, 0), 0);
        events.AddEvent(new NAudio.Midi.KeySignatureEvent(0, 0, 0), 0); // every fixture is in C major
        events.AddEvent(new NAudio.Midi.TextEvent(name + " (generated pattern-wheel fixture)", NAudio.Midi.MetaEventType.SequenceTrackName, 0), 0);
        int last = -1;
        foreach (var bar in score.Bars)
            if (bar.Numerator != last) { events.AddEvent(new NAudio.Midi.TimeSignatureEvent(Tick(bar.Start), bar.Numerator, 2, 24, 8), 0); last = bar.Numerator; }
        bool piano = name is "strain" or "ternary";
        string[] names = piano ? new[] { "", "Piano chords", "Piano left hand", "Piano melody", "" } : new[] { "", "Keys", "Bass", "Lead Vocal", "Drums" };
        for (int track = 1; track <= 4; track++)
        {
            if (!score.Notes.Any(x => x.Track == track)) continue;
            events.AddTrack(); events.AddEvent(new NAudio.Midi.TextEvent(names[track], NAudio.Midi.MetaEventType.SequenceTrackName, 0), track);
            foreach (var n in score.Notes.Where(x => x.Track == track).OrderBy(x => x.Beat))
            {
                int length = Math.Max(1, (int)Tick(n.Length));
                var on = new NAudio.Midi.NoteOnEvent(Tick(n.Beat), n.Channel, n.Pitch, Math.Clamp((int)Math.Round(n.Velocity * 127), 1, 127), length);
                events.AddEvent(on, track); events.AddEvent(on.OffEvent, track);
            }
        }
        long end = Tick(score.Beat);
        for (int t = 0; t < events.Tracks; t++) events.AddEvent(new NAudio.Midi.MetaEvent(NAudio.Midi.MetaEventType.EndTrack, 0, end), t);
        events.PrepareForExport();
        NAudio.Midi.MidiFile.Export(path, events);
        Console.WriteLine($"{path}: {score.Bars.Count} bars, {score.Notes.Count} notes");
    }

    // PatternPrep --fixture name: verbose analysis of one synthetic score.
    public static void Fixture(string name)
    {
        FormAnalysis.Verbose = true;
        var (score, settings, style) = name switch
        {
            "pop" => (PopScore(), new SongSettings { Key = 3 }, "pop"),
            "ternary" => (TernaryScore(), new SongSettings(), "classical"),
            "variation" => (VariationScore().score, new SongSettings { Key = 3 }, "pop"),
            "alternating" => (AlternatingScore(), new SongSettings { Key = 3 }, "pop"),
            "strain" => (StrainScore(), new SongSettings { Key = 3 }, "classical"),
            _ => throw new ArgumentException("fixtures: pop, ternary, variation, alternating, strain")
        };
        settings.Style = style;
        Dump(FormAnalysis.Build(score.Cycles(), settings, new[] { "", "Keys", "Bass", "Lead", "Drums" }));
    }

    public static void Run(bool verbose = false)
    {
        FormAnalysis.Verbose = verbose;
        Hierarchy();
        Pop();
        Ternary();
        Variations();
        Structure();
        Alternating();
        Console.WriteLine("PASS: hierarchy carriers, pop verse–chorus roles with transposed final chorus, classical ternary with key areas");
        Console.WriteLine("PASS: pattern compression — family fundamentals, new ending, extra pass, lead-in, transposed return, verse–chorus groups and form grammar");
        Console.WriteLine("PASS: repetition-first structure — pop and varied verse–chorus forms and adjacent classical strains found without reviewed boundaries");
        Console.WriteLine("PASS: alternating pair — A B A B C A B C keeps A and B as one recurring group that C interrupts");
    }

    static Score PopScore()
    {
        var s = new Score();
        int[] verseTune = { 0, 2, 4, 2 }, chorusTune = { 7, 7, 9, 12, 9, 7, 4, 7 }, bridgeTune = { 5, 4, 2, 0 };
        void Verse() { for (int r = 0; r < 2; r++) { s.Chord(C, false, verseTune, .6f, true); s.Chord(G, false, verseTune, .6f, true); s.Chord(A, true, verseTune, .6f, true); s.Chord(F, false, verseTune, .6f, true); } }
        void Chorus(int up) { for (int r = 0; r < 2; r++) { s.Chord(F + up, false, chorusTune, 1f, true); s.Chord(C + up, false, chorusTune, 1f, true); s.Chord(G + up, false, chorusTune, 1f, true); s.Chord(A + up, true, chorusTune, 1f, true); } }
        foreach (int r in new[] { A, F, A, F }) s.Chord(r, r == A, new[] { 0 }, .35f, false);
        Verse(); Chorus(0); Verse(); Chorus(0);
        foreach (int r in new[] { D, E, F, G, D, E, F, G }) s.Chord(r, r is D or E, bridgeTune, .7f, true);
        Chorus(2);
        foreach (int r in new[] { C, C, C, C }) s.Chord(r, false, new[] { 0 }, .3f, false);
        return s;
    }
    static void Pop()
    {
        var s = PopScore();
        var result = FormAnalysis.Build(s.Cycles(), new SongSettings { Key = 3 }, new[] { "", "Keys", "Bass", "Lead Vocal", "Drums" });
        if (FormAnalysis.Verbose) Dump(result);
        string roles = string.Join(",", result.Sections.Select(x => x.Label));
        Check(result.Style == "pop", "drums and vocal tracks select pop naming");
        Check(roles == "Intro,Verse 1,Chorus 1,Verse 2,Chorus 2,Bridge,Chorus 3,Outro", "pop roles: " + roles);
        Check(result.Sections[6].Transpose == 2 && result.Form.Sections[6].Family == result.Form.Sections[2].Family, "final chorus up a whole step stays in the chorus family");
        Check(result.Form.Sections[1].ProgressionBeats == 16 && result.Sections[1].Loops == 2, "verse loop is the 4-bar progression, played twice");
        Check(result.FormName.StartsWith("Verse–chorus with bridge"), "form name: " + result.FormName);
        var song = PreparedPatternSong.Capture(s.Cycles(), result.Form);
        song.Frames = new[] { new PreparedPatternSong.Frame { Time = 0, Key = 3 } };
        GrammarWords.Build(song);
        Check(GrammarWords.Word(song.Sections[1]) == "0M > 3PM > 2m > 0M", "verse word I–V–vi–IV: " + GrammarWords.Word(song.Sections[1]));
        Check(string.Join(" ", song.Sections[2].Chords.Select(c => c.Roman)) == "IV I V vi", "chorus numerals");
    }

    // Without reviewed boundaries: repetition decides the sections.
    static void Structure()
    {
        var pop = FormAnalysis.Build(PopScore().Cycles(), new SongSettings { Key = 3 }, new[] { "", "Keys", "Bass", "Lead Vocal", "Drums" });
        Check(pop.Grammar == "In (V C)×2 Br C′ Out", "pop grammar: " + pop.Grammar);
        var varied = FormAnalysis.Build(VariationScore().score.Cycles(), new SongSettings { Key = 3 }, new[] { "", "Keys", "Bass", "Lead Vocal", "Drums" });
        Check(varied.Grammar == "In (V C) (V′ C) Br (V C′) Out", "variation grammar without boundaries: " + varied.Grammar);
        Check(varied.Sections[0].Role == "Intro" && varied.Sections[^1].Role == "Outro" && varied.Form.Sections[0].Family == varied.Form.Sections[^1].Family, "the same quiet bars open and close the song: a bookend family");
        var strain = FormAnalysis.Build(StrainScore().Cycles(), new SongSettings { Key = 3 }, new[] { "", "Piano" });
        var starts = strain.Form.Sections.Select(x => x.FirstBar + 1).ToArray();
        Check(starts.SequenceEqual(new[] { 1, 17, 33, 49, 65, 81, 97 }), "strains A A B B A C C are found back to back: " + string.Join(",", starts));
        Check(strain.Grammar == "(A×2 B×2 A) (C×2)", "strain grammar: " + strain.Grammar);
        var a = strain.Patterns.Single(p => p.Family == strain.Form.Sections[0].Family.Id);
        Check(a.LoopBars == 8 && a.Visits == 3 && strain.Sections[0].Passes[1].ChangedBars.Count > 0 && strain.Sections[1].Variation == "repeats A", "an A strain is an eight-bar phrase answered with a variation, and its repeat is exact");
    }

    // Two sections alternating as a pair, interrupted by a third: A B A B C A B C.
    static Score AlternatingScore()
    {
        var s = new Score();
        void Part((int root, bool minor)[] chords, int[] tune, float loudness, int style)
        {
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < chords.Length; i++) { s.Groove(s.Beat, 4, style, pass == 1 && i == chords.Length - 1); s.Chord(chords[i].root, chords[i].minor, tune, loudness, false); }
        }
        var a = new[] { (C, false), (A, true), (F, false), (G, false) };
        var b = new[] { (F, false), (G, false), (E, true), (A, true) };
        var c = new[] { (D, true), (Bb, false), (F, false), (G, false) };
        int[] tuneA = { 0, 4, 7, 4 }, tuneB = { 7, 9, 12, 9, 7, 4, 7, 9 }, tuneC = { 12, 7, 5, 4 };
        void PlayA() => Part(a, tuneA, .6f, 1); void PlayB() => Part(b, tuneB, 1f, 2); void PlayC() => Part(c, tuneC, .75f, 3);
        PlayA(); PlayB(); PlayA(); PlayB(); PlayC(); PlayA(); PlayB(); PlayC();
        return s;
    }
    static void Alternating()
    {
        var result = FormAnalysis.Build(AlternatingScore().Cycles(), new SongSettings { Key = 3 }, new[] { "", "Keys", "Bass", "Lead Vocal", "Drums" });
        var families = result.Form.Sections.Select(x => x.Family.Id).ToArray();
        Check(families.Length == 8 && families[0] == families[2] && families[2] == families[5] && families[1] == families[3] && families[3] == families[6] && families[4] == families[7] && families.Distinct().Count() == 3, "A B A B C A B C: three families " + string.Join(",", families));
        Check(result.Groups.Count == 1 && result.Groups[0].Visits == 3 && result.Groups[0].Families.Length == 2 && result.Sections[4].Group < 0 && result.Sections[7].Group < 0, "A and B return as a pair three times; C interrupts outside the pair");
        Check(System.Text.RegularExpressions.Regex.IsMatch(result.Grammar, @"^\((\S+) (\S+)\)×2 (\S+) \(\1 \2\) \3$"), "grammar folds the pair and marks the interruption: " + result.Grammar);
    }

    // Reviewed boundaries isolate the compression layer from segmentation.
    static (Score score, string boundaries) VariationScore()
    {
        var s = new Score();
        int[] verseTune = { 0, 4, 7, 4 }, chorusTune = { 7, 9, 12, 9, 7, 4, 7, 9 }, bridgeTune = { 4, 2, 0, 2 };
        int style = 1;
        void Loop((int root, bool minor)[] chords, int[] tune, float loudness, int up = 0, bool fill = false)
        {
            for (int i = 0; i < chords.Length; i++) { s.Groove(s.Beat, 4, style, fill && i == chords.Length - 1); s.Chord(chords[i].root + up, chords[i].minor, tune, loudness, false); }
        }
        var verse = new[] { (C, false), (A, true), (F, false), (G, false) };
        var ending = new[] { (C, false), (A, true), (F, false), (E, false) };
        var chorus = new[] { (A, true), (F, false), (C, false), (G, false) };
        foreach (int r in new[] { C, C }) s.Chord(r, false, new[] { 0 }, .35f, false);   // bars 1-2 intro
        style = 1; Loop(verse, verseTune, .6f); Loop(verse, verseTune, .6f);                      // 3 verse
        style = 2; Loop(chorus, chorusTune, 1f); Loop(chorus, chorusTune, 1f, 0, true);           // 11 chorus, fill
        style = 1; Loop(verse, verseTune, .6f); Loop(ending, verseTune, .6f);                     // 19 verse, new ending
        style = 2; Loop(chorus, chorusTune, 1f); Loop(chorus, chorusTune, 1f); Loop(chorus, chorusTune, 1f, 0, true); // 27 chorus, extra pass
        style = 3; Loop(new[] { (F, false), (G, false), (E, true), (A, true), (D, false), (G, false), (Bb, false), (G, false) }, bridgeTune, .7f); // 39 bridge (D is V/V)
        style = 1; Loop(new[] { (G, false) }, new[] { 0, 2 }, .5f);                                // 47 lead-in bar
        Loop(verse, verseTune, .6f); Loop(verse, verseTune, .6f);                                 // 48 verse
        style = 2; Loop(chorus, chorusTune, 1f, 2); Loop(chorus, chorusTune, 1f, 2, true);        // 56 chorus up a step
        foreach (int r in new[] { C, C }) s.Chord(r, false, new[] { 0 }, .3f, false);   // 64 outro
        return (s, "1 Intro\n3 Verse\n11 Chorus\n19 Verse\n27 Chorus\n39 Bridge\n47 Verse\n56 Chorus\n64 Outro");
    }
    static void Variations()
    {
        var (s, boundaries) = VariationScore();
        var result = FormAnalysis.Build(s.Cycles(), new SongSettings { Key = 3, SectionBoundaries = boundaries }, new[] { "", "Keys", "Bass", "Lead Vocal", "Drums" });
        if (FormAnalysis.Verbose) Dump(result);
        var verse = result.Patterns.Single(p => p.Role == "Verse"); var chorus = result.Patterns.Single(p => p.Role == "Chorus");
        Check(verse.LoopBars == 4 && string.Join(" ", verse.Loop.Select(c => c.Name(true))) == "C Am F G", "verse fundamental is C Am F G: " + string.Join(" ", verse.Loop.Select(c => c.Name(true))));
        Check(chorus.LoopBars == 4 && chorus.Visits == 3, "chorus fundamental is one four-bar loop for three visits");
        Check(result.Sections[1].Variation == "fundamental" && result.Sections[3].Variation == "new ending", "second verse keeps the loop but changes its ending: " + result.Sections[3].Variation);
        var ending = result.Sections[3].Passes[1];
        Check(ending.ChangedBars.SequenceEqual(new[] { 3 }) && ending.Changed.Count == 2 && Math.Abs(ending.Changed[0] - 12) < 1e-6 && Math.Abs(ending.Changed[1] - 16) < 1e-6, "the changed span is the loop's last bar");
        Check(result.Sections[4].Passes.Count(p => !p.Partial) == 3 && result.Sections[4].Variation.StartsWith("3 passes"), "second chorus plays an extra pass: " + result.Sections[4].Variation);
        Check(result.Sections[6].Passes[0].Partial && result.Sections[6].Passes[0].Bars == 1 && result.Sections[6].Variation.Contains("lead-in"), "third verse begins with a one-bar lead-in: " + result.Sections[6].Variation);
        Check(result.Sections[7].Variation.StartsWith("transposed +2") && result.Sections[7].Passes.All(p => p.Transpose == 2), "last chorus is the fundamental up a whole step: " + result.Sections[7].Variation);
        Check(result.Groups.Count == 1 && result.Groups[0].Visits == 3 && result.Sections.Count(x => x.Group == 0) == 6, "verse + chorus recurs as one group three times");
        Check(result.Grammar == "In (V C) (V′ C) Br (V C′) Out", "form grammar: " + result.Grammar);
        Check(result.FundamentalBars < result.SongBars / 3, $"{result.SongBars} bars reduce to {result.FundamentalBars} bars of fundamentals");
        // The playback contract carries the same compression.
        var song = PreparedPatternSong.Capture(s.Cycles(), result.Form);
        FormAnalysis.Apply(result, song);
        Check(song.Version == PreparedPatternSong.CurrentVersion && song.Patterns.Length == result.Patterns.Count && song.Sections[3].Passes[1].Changed.Length == 2 && song.FormGrammar == result.Grammar, "the prepared song carries fundamentals and passes");
        Check(!song.EnsurePatterns(), "a v3 song needs no runtime projection");
        var legacy = PreparedPatternSong.Capture(s.Cycles(), result.Form);
        Check(legacy.EnsurePatterns() && legacy.Sections.All(x => x.Passes.Length > 0) && legacy.Patterns.Length == 5, "older bundles get a projected fundamental per family");
    }

    // Ragtime-style strains in 2/4: A A B B A C C, sixteen bars each, every strain made of
    // two different eight-bar phrases so no strain is a loop of itself.
    static Score StrainScore()
    {
        var s = new Score();
        void Strain((int root, bool minor)[] chords, int[] tune, int[] answer, float loudness)
        {
            for (int i = 0; i < chords.Length; i++) s.Chord(chords[i].root, chords[i].minor, i < 8 ? tune : answer, loudness, false, 2);
        }
        var a = new[] { (C, false), (C, false), (G, false), (G, false), (G, false), (G, false), (C, false), (C, false), (C, false), (C, false), (F, false), (F, false), (G, false), (G, false), (C, false), (C, false) };
        var b = new[] { (A, true), (A, true), (E, false), (E, false), (A, true), (A, true), (E, false), (E, false), (F, false), (F, false), (C, false), (C, false), (G, false), (G, false), (C, false), (C, false) };
        var c = new[] { (F, false), (F, false), (C, false), (C, false), (F, false), (F, false), (Bb, false), (Bb, false), (F, false), (D, true), (G, true), (C, false), (F, false), (C, false), (F, false), (F, false) };
        int[] a1 = { 0, 4, 7, 12 }, a2 = { 12, 7, 4, 0 }, b1 = { 3, 7, 3, 0 }, b2 = { 7, 12, 7, 4 }, c1 = { 0, 2, 4, 5 }, c2 = { 9, 7, 5, 4 };
        Strain(a, a1, a2, .7f); Strain(a, a1, a2, .7f); Strain(b, b1, b2, .75f); Strain(b, b1, b2, .75f);
        Strain(a, a1, a2, .7f); Strain(c, c1, c2, .6f); Strain(c, c1, c2, .6f);
        return s;
    }

    static Score TernaryScore()
    {
        var s = new Score();
        int[] tuneA = { 0, 3, 7, 3, 0, -2 }, tuneB = { 4, 7, 12, 7, 4, 0 };
        void PartA() { foreach (var (r, m) in new[] { (A, true), (D, true), (E, false), (A, true), (A, true), (D, true), (E, false), (A, true) }) s.Chord(r, m, tuneA, .6f, false, 3); }
        PartA();
        foreach (var (r, m) in new[] { (C, false), (F, false), (G, false), (C, false), (C, false), (F, false), (G, false), (C, false), (F, false), (Bb, false), (C, false), (F, false) }) s.Chord(r, m, tuneB, .8f, false, 3);
        PartA();
        foreach (var (r, m) in new[] { (A, true), (A, true) }) s.Chord(r, m, new[] { 0 }, .3f, false, 3);
        return s;
    }
    static void Ternary()
    {
        var s = TernaryScore();
        var result = FormAnalysis.Build(s.Cycles(), new SongSettings(), new[] { "", "Piano RH", "Piano LH", "Piano" });
        if (FormAnalysis.Verbose) Dump(result);
        Check(result.Style == "classical", "piano score selects classical naming");
        Check(result.FormName.StartsWith("Ternary (ABA)"), "ternary form: " + result.FormName + " / " + string.Join(" ", result.Sections.Select(x => x.Label + "@" + x.Parent)));
        Check(result.Sections[0].KeyMinor && result.Sections[0].KeyRoot == 0, "A section is in A minor");
    }

    // Grid search of segmentation weights against reviewed boundaries (1-based start bars,
    // excluding bar 1). Boundaries within one bar count; "optional" ones are never penalized.
    public static void Sweep(string[] reviewed)
    {
        var cases = new List<(string name, FormAnalysis.Prepared model, int[] truth, int[] optional)>
        {
            ("pop fixture", FormAnalysis.Prepare(PopScore().Cycles(), 3, false, false), new[] { 5, 13, 21, 29, 37, 45, 53 }, Array.Empty<int>()),
            ("ternary fixture", FormAnalysis.Prepare(TernaryScore().Cycles(), 0, true, true), new[] { 9, 21, 29 }, new[] { 17 }),
            ("variation fixture", FormAnalysis.Prepare(VariationScore().score.Cycles(), 3, false, false), new[] { 3, 11, 19, 27, 39, 47, 56, 64 }, new[] { 48 }),
            ("strain fixture", FormAnalysis.Prepare(StrainScore().Cycles(), 3, false, true), new[] { 17, 33, 49, 65, 81, 97 }, Array.Empty<int>()),
        };
        foreach (var raw in reviewed)
        {
            // path|boundaries|optional|classical, or path|reference-form.json
            string line = raw;
            var split = raw.Split('|');
            if (split.Length == 2 && split[1].EndsWith(".json"))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(split[1]));
                var root = doc.RootElement;
                var starts = root.GetProperty("sections").EnumerateArray().Select(x => x[0].GetInt32()).Where(b => b > 1);
                // A count-in bar makes the intro start a boundary too.
                var optional = root.GetProperty("optional").EnumerateArray().Select(x => x.GetInt32());
                line = $"{split[0]}|{string.Join(",", starts)}|{string.Join(",", optional)}|{root.GetProperty("style").GetString()}";
            }
            var parts = line.Split('|');
            var midi = new NAudio.Midi.MidiFile(parts[0], false);
            cases.Add((Path.GetFileName(Path.GetDirectoryName(parts[0])), FormAnalysis.Prepare(MidiCycleAnalysis.Analyze(midi, true), -1, false, parts.Length > 3 && parts[3] == "classical"),
                parts[1].Split(',').Select(int.Parse).ToArray(), parts.Length > 2 && parts[2].Length > 0 ? parts[2].Split(',').Select(int.Parse).ToArray() : Array.Empty<int>()));
        }
        double F1(List<int> found, int[] truth, int[] optional)
        {
            var predicted = found.Where(b => b > 1 && !optional.Any(o => Math.Abs(o - b) <= 1)).ToList();
            int hits = truth.Count(t => predicted.Any(p => Math.Abs(p - t) <= 1));
            int correct = predicted.Count(p => truth.Any(t => Math.Abs(p - t) <= 1));
            double precision = predicted.Count == 0 ? 0 : correct / (double)predicted.Count, recall = truth.Length == 0 ? 1 : hits / (double)truth.Length;
            return precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        }
        var results = new List<(double score, string weights, string detail)>();
        var baseline = FormAnalysis.Weights;
        foreach (double seg in new[] { 1.8, 2.2, 2.8 })
        foreach (double reward in new[] { .6, 1.2 })
        foreach (double threshold in new[] { .35, .5 })
        foreach (double inner in new[] { 3.0, 5.0 })
        foreach (double edge in new[] { 0, .5, 1.0, 1.5, 2.0 })
        foreach (double edgeInner in new[] { 0, .5, 1.0, 2.0 })
        {
            var w = new FormAnalysis.Tuning { SegmentCost = seg, NoveltyReward = reward, InnerThreshold = threshold, InnerWeight = inner, OffGridPrior = baseline.OffGridPrior,
                NearGridPrior = baseline.NearGridPrior, ShortPrior = baseline.ShortPrior, EdgeReward = edge, EdgeInner = edgeInner };
            var scores = cases.Select(c => F1(FormAnalysis.Boundaries(c.model, w), c.truth, c.optional)).ToArray();
            results.Add((scores.Average() - .5 * scores.Select(x => Math.Max(0, .8 - x)).Sum(), $"seg {seg} reward {reward} threshold {threshold} inner {inner} edge {edge} edgeInner {edgeInner}", string.Join(" ", scores.Select(x => x.ToString("0.00")))));
        }
        foreach (var r in results.OrderByDescending(r => r.score).Take(12)) Console.WriteLine($"{r.score:0.000}  {r.detail}  {r.weights}");
        var current = cases.Select(c => F1(FormAnalysis.Boundaries(c.model, FormAnalysis.Weights), c.truth, c.optional));
        Console.WriteLine("current: " + string.Join(" ", current.Select(x => x.ToString("0.00"))));
        foreach (var c in cases) Console.WriteLine($"  {c.name}: {string.Join(",", FormAnalysis.Boundaries(c.model, FormAnalysis.Weights))}");
    }

    public static void Dump(FormAnalysis.Result result)
    {
        var f = result.Form;
        for (int i = 0; i < f.Sections.Count; i++)
        {
            var x = f.Sections[i]; var info = result.Sections[i];
            Console.WriteLine($"  {info.Label,-12} bar {x.FirstBar + 1,3} ×{x.BarCount,-3} fam {x.Family.Id} t{info.Transpose} sim {info.Similarity:0.00} loop {x.ProgressionBeats}b ×{info.Loops} key {HarmonyModel.Name(info.KeyRoot)}{(info.KeyMinor ? "m" : "")} {info.Parent} | " +
                string.Join(" ", x.Chords.Select(c => c.Name(true))));
        }
        Console.WriteLine("  form: " + result.FormName);
        Console.WriteLine("  grammar: " + result.Grammar + $" · {result.SongBars} bars → {result.Patterns.Count} fundamentals of {result.FundamentalBars} bars");
        foreach (var p in result.Patterns)
            Console.WriteLine($"  pattern {p.Short,-4} fam {p.Family} ref {p.Reference} loop {p.LoopBars} bars ×{p.Passes} of {p.SectionBars} · visits {p.Visits} | {string.Join(" ", p.Loop.Select(c => c.Name(true)))}");
        for (int i = 0; i < result.Sections.Count; i++)
        {
            var info = result.Sections[i];
            Console.WriteLine($"    {info.Label,-12} g{info.Group}/{info.GroupVisit} {info.Variation} | " + string.Join(" ", info.Passes.Select(p => $"[{p.Bars}{(p.Partial ? "p" : "")}{(p.Offset > 0 ? "@" + p.Offset : "")}{(p.Transpose != 0 ? "t" + p.Transpose : "")}{(p.ChangedBars.Count > 0 ? "~" + string.Join(",", p.ChangedBars) : "")}]")));
        }
    }

    static void Hierarchy()
    {
        var names = new[] { "Intro", "Verse", "Chorus", "Verse", "Chorus", "Bridge", "Verse", "Chorus", "Verse", "Chorus", "Outro" };
        var families = names.Distinct().ToArray();
        var song = new PreparedPatternSong { Sections = names.Select((name, i) => new PreparedPatternSong.Section { Name = name, Family = Array.IndexOf(families, name), Start = i * 32, End = (i + 1) * 32, ParentPath = "Song" }).ToArray() };
        SectionCompression.GroupRepeatedForms(song); SectionCompression.BuildHierarchy(song);
        Check(song.Form[0].Children.Length == 4, "root holds intro, verse+chorus, bridge, outro");
        var carrier = song.Form.Single(n => n.Name == "Verse + Chorus");
        Check(carrier.Children.Length == 2 && carrier.Route.Length == 8, "carrier with two child gears and all eight visits");
        for (int i = 0; i < song.Sections.Length; i++) Check(song.Form[0].Route[i].Section == i, "root route keeps every visit");
        foreach (int count in new[] { 3, 4, 5, 8 })
        {
            var repeated = new PreparedPatternSong { Sections = Enumerable.Range(0, count * 2).Select(i => new PreparedPatternSong.Section { Name = "Part " + i % count, Family = i % count, Start = i * 8, End = (i + 1) * 8, ParentPath = "Song" }).ToArray() };
            SectionCompression.GroupRepeatedForms(repeated); SectionCompression.BuildHierarchy(repeated);
            Check(repeated.Form[0].Children.Length == 1 && repeated.Form[repeated.Form[0].Children[0]].Children.Length == count, "composite size " + count);
        }
    }
}
