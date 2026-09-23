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
            for (int i = 0; i < melody.Length; i++) Note(Beat + i * beats / (double)melody.Length, beats / (double)melody.Length, root + 24 + melody[i], .8f * loudness, 3, 3);
            if (drums) for (int b = 0; b < beats; b++) { Note(Beat + b, .1, b % 2 == 0 ? 36 : 38, .8f, 10, 4); Note(Beat + b + .5, .1, 42, .5f, 10, 4); }
            Beat += beats;
        }
        public MidiCycleAnalysis Cycles() => MidiCycleAnalysis.Restore(new PreparedPatternSong
        {
            EndBeat = Beat, Tempos = new[] { new PreparedPatternSong.Tempo { Beat = 0, Seconds = 0, Microseconds = 500000 } },
            Measures = Bars.ToArray(), Notes = Notes.ToArray(), Disks = Array.Empty<PreparedPatternSong.Disk>()
        });
    }

    const int C = 48, D = 50, E = 52, F = 53, G = 55, A = 57, Bb = 58;

    public static void Run(bool verbose = false)
    {
        FormAnalysis.Verbose = verbose;
        Hierarchy();
        Pop();
        Ternary();
        Console.WriteLine("PASS: hierarchy carriers, pop verse–chorus roles with transposed final chorus, classical ternary with key areas");
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
        };
        foreach (var line in reviewed)
        {
            // path|boundaries|optional|classical
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
        foreach (double seg in new[] { 1.4, 1.8, 2.2, 2.8 })
        foreach (double reward in new[] { .6, 1.2, 1.8 })
        foreach (double threshold in new[] { .35, .5, .65 })
        foreach (double inner in new[] { 1.5, 3.0, 5.0 })
        foreach (double off in new[] { .6, 1.1, 1.8 })
        foreach (double near in new[] { .3, .6, 1.0 })
        foreach (double shortPrior in new[] { .3, .6, 1.2 })
        {
            var w = new FormAnalysis.Tuning { SegmentCost = seg, NoveltyReward = reward, InnerThreshold = threshold, InnerWeight = inner, OffGridPrior = off, NearGridPrior = near, ShortPrior = shortPrior };
            var scores = cases.Select(c => F1(FormAnalysis.Boundaries(c.model, w), c.truth, c.optional)).ToArray();
            results.Add((scores.Average() - .5 * scores.Select(x => Math.Max(0, .8 - x)).Sum(), $"seg {seg} reward {reward} threshold {threshold} inner {inner} off {off} near {near} short {shortPrior}", string.Join(" ", scores.Select(x => x.ToString("0.00")))));
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
