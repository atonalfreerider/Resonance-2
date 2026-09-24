// Instrument patterns: every pitched lane is read as the chords it plays.
//
// 1. Chords. The lane's own pitch classes on the notated beat grid, with the song's harmony
//    as context (ChordTimeline.Lane), decoded like the song timeline.
// 2. Windows. The song's loop passes (Section.Passes) cut the lane into windows, so its
//    patterns turn with the song's loops. A window whose chords repeat inside it is cut
//    shorter: a lane vamping on one chord for a four-bar pass is a one-bar loop played four
//    times.
// 3. Patterns. By definition a window that repeats the same chords is the same pattern (a
//    transposed copy too, when it moves between chords); a window that keeps at least half of
//    them but changes one or more is a variation of it. Each fundamental is the per-step
//    consensus of its plays, so a varied first play does not become the reference.
// 4. Runs. Consecutive plays of one pattern are a run (repeat n of m), and the lane is
//    written as a grammar: C A B×2 A B×2 B′ A′ C, with · where it rests.
public static class InstrumentPatterns
{
    static char Triad(int state) => ChordTimeline.Quality(state) switch { "m" or "m7" => 'm', "dim" => 'd', _ => 'M' };
    // Seventh and extension colours do not change the chords a lane plays: A and A7 agree.
    static bool Same(int a, int b) => a < 0 || b < 0 ? a == b : ChordTimeline.Root(a) == ChordTimeline.Root(b) && Triad(a) == Triad(b);

    sealed class Window
    {
        public int From, To, Loop, Offset;   // grid steps [From, To); the loop it belongs to, in steps, and where in it the window starts
        public double Start, End, OffsetBeats;
        public bool Partial;
        public int Pattern = -1, Variation, T;
        public double Score;
        public List<int> Changed = new();   // loop positions (steps) whose chords differ from the fundamental
        public int Length => To - From;
    }
    sealed class Fundamental
    {
        public int[] States = Array.Empty<int>();
        public double[] Starts = Array.Empty<double>(), Ends = Array.Empty<double>(); // loop-relative beats of each step
        public int Reference, Bars;
    }

    static double Agreement(int[] states, Window w, int[] f, int t, List<int> changed = null)
    {
        int same = 0;
        for (int k = 0; k < w.Length; k++)
        {
            if (Same(ChordTimeline.Transpose(f[w.Offset + k], t), states[w.From + k])) same++;
            else changed?.Add(w.Offset + k);
        }
        return w.Length == 0 ? 0 : same / (double)w.Length;
    }

    // The best transposition of fundamental f for window w. Moving a pattern by a key is only
    // the same pattern when it moves between chords: a vamp on Am is not a vamp on C.
    static (double score, int t) Best(int[] states, Window w, int[] f)
    {
        if (f.Length != w.Loop || w.Offset + w.Length > f.Length) return (-1, 0);
        double at0 = Agreement(states, w, f, 0);
        int roots = Enumerable.Range(w.From, w.Length).Select(i => ChordTimeline.Root(states[i])).Where(r => r >= 0).Distinct().Count();
        if (at0 >= 1 || roots < 2) return (at0, 0);
        var moved = Enumerable.Range(1, 11).Select(t => (score: Agreement(states, w, f, t), t)).OrderByDescending(x => x.score).ThenBy(x => Math.Min(x.t, 12 - x.t)).First();
        return moved.score >= .75 && moved.score >= at0 + .25 ? moved : (at0, 0);
    }

    public static void Build(PreparedPatternSong song, MidiCycleAnalysis cycles, ChordTimeline.Grid grid, int key, bool minor)
    {
        var parts = new List<PreparedPatternSong.InstrumentPart>();
        if (grid.Count == 0 || song.Sections == null) { song.Parts = parts.ToArray(); return; }
        var pitched = song.Notes.Where(n => n.Channel != 10).ToArray();
        var lanes = pitched.GroupBy(n => (n.Track, n.Channel)).ToArray();
        foreach (var lane in lanes)
        {
            var lg = ChordTimeline.Lane(grid, cycles, lane, key, minor);
            var windows = Windows(song, lg);
            var fundamentals = new List<Fundamental>();
            // Two rounds: assign, take the consensus, then assign again against it.
            for (int round = 0; round < 2; round++)
            {
                for (int i = 0; i < windows.Count; i++) Assign(lg, windows[i], i, fundamentals);
                if (round == 1) break;
                foreach (var (f, id) in fundamentals.Select((f, i) => (f, i)))
                {
                    var plays = windows.Where(w => w.Pattern == id && w.Offset == 0 && w.Length == w.Loop).ToList();
                    if (plays.Count < 3) continue;
                    f.States = Enumerable.Range(0, f.States.Length).Select(k =>
                    {
                        var votes = plays.Select(w => ChordTimeline.Transpose(lg.State[w.From + k], -w.T)).ToList();
                        return votes.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key == f.States[k] ? 0 : 1).First().Key;
                    }).ToArray();
                }
                foreach (var w in windows) { w.Pattern = -1; w.Changed.Clear(); }
            }
            // Distinct sets of changed chords are distinct variations (′, ″, …).
            foreach (var group in windows.Where(w => w.Pattern >= 0).GroupBy(w => w.Pattern))
            {
                var signatures = new List<string>();
                foreach (var w in group.OrderBy(w => w.From))
                {
                    if (w.Changed.Count == 0) { w.Variation = 0; continue; }
                    string signature = string.Join(";", w.Changed.Select(k => $"{k}:{ChordTimeline.Transpose(lg.State[w.From + k - w.Offset], -w.T)}"));
                    int v = signatures.IndexOf(signature); if (v < 0) { v = signatures.Count; signatures.Add(signature); }
                    w.Variation = v + 1;
                }
            }
            // Patterns are lettered in order of first appearance, and runs counted.
            var order = windows.Where(w => w.Pattern >= 0).Select(w => w.Pattern).Distinct().ToList();
            string trackName = lane.Key.Track < song.TrackNames.Length && !string.IsNullOrWhiteSpace(song.TrackNames[lane.Key.Track]) ? song.TrackNames[lane.Key.Track] : $"Track {lane.Key.Track + 1}";
            var part = new PreparedPatternSong.InstrumentPart
            {
                Track = lane.Key.Track, Channel = lane.Key.Channel, Name = trackName,
                Vocal = lane.Key.Track == song.LeadVocalTrack || trackName.Contains("vocal", StringComparison.OrdinalIgnoreCase) || trackName.Contains("voice", StringComparison.OrdinalIgnoreCase),
            };
            part.Role = Role(part, lane.ToArray());
            part.Patterns = order.Select((id, index) =>
            {
                var f = fundamentals[id]; var plays = windows.Where(w => w.Pattern == id).ToList();
                var reference = windows[f.Reference];
                var (k, m) = GrammarWords.KeyAt(song, reference.Start, cycles.SecondsAt);
                var loop = new List<SongFormAnalysis.ChordStep>();
                for (int s = 0; s < f.States.Length; s++)
                {
                    int state = f.States[s];
                    if (loop.Count > 0 && loop[^1].Root == ChordTimeline.Root(state) && loop[^1].Quality == ChordTimeline.Quality(state)) { loop[^1].End = f.Ends[s]; continue; }
                    loop.Add(new SongFormAnalysis.ChordStep { Start = f.Starts[s], End = f.Ends[s], Root = ChordTimeline.Root(state), Quality = ChordTimeline.Quality(state) });
                }
                foreach (var chord in loop) chord.Roman = chord.Rest ? "–" : HarmonyModel.Roman(chord.Root, chord.Quality, k >= 0 ? k : 0, m);
                return new PreparedPatternSong.LanePattern
                {
                    Id = index, Letter = Letter(index), LoopBeats = f.Ends.Length > 0 ? f.Ends[^1] : 0, LoopBars = f.Bars,
                    Plays = plays.Count, Variations = plays.Where(w => w.Variation > 0).Select(w => w.Variation).Distinct().Count(), Loop = loop.ToArray()
                };
            }).ToArray();
            var output = windows.Select(w => new PreparedPatternSong.LanePlay
            {
                Start = w.Start, End = w.End, Offset = w.OffsetBeats, Pattern = w.Pattern < 0 ? -1 : order.IndexOf(w.Pattern), Variation = w.Variation, Transpose = w.T, Partial = w.Partial,
                Changed = Spans(w, fundamentals, lg)
            }).ToList();
            // Runs: consecutive plays of the same pattern (its variations included).
            for (int i = 0; i < output.Count;)
            {
                int j = i; while (j + 1 < output.Count && output[j + 1].Pattern == output[i].Pattern) j++;
                for (int x = i; x <= j; x++) { output[x].Run = j - i + 1; output[x].Repeat = x - i + 1; }
                i = j + 1;
            }
            part.Plays = output.ToArray();
            part.Grammar = Grammar(part);
            part.Bars = windows.Where(w => w.Pattern >= 0).Sum(w => lg.Bar[w.To - 1] - lg.Bar[w.From] + 1);
            part.FundamentalBars = part.Patterns.Sum(p => p.LoopBars);
            parts.Add(part);
        }
        // The vocal first, then the other lanes in score order.
        song.Parts = parts.OrderByDescending(p => p.Vocal).ThenBy(p => p.Track).ThenBy(p => p.Channel).ToArray();
    }

    // The song's loop passes as windows, cut shorter where the lane's chords repeat inside a pass.
    static List<Window> Windows(PreparedPatternSong song, ChordTimeline.Grid g)
    {
        var windows = new List<Window>();
        int Step(double beat) { int i = Array.BinarySearch(g.Start, beat - 1e-6); if (i < 0) i = ~i; return Math.Clamp(i, 0, g.Count); }
        foreach (var section in song.Sections)
        {
            var passes = section.Passes.Length > 0 ? section.Passes : new[] { new PreparedPatternSong.Pass { Start = section.Start, End = section.End } };
            // A pass's loop length in steps: that of the family's full passes (a partial pass is part of one).
            var full = passes.FirstOrDefault(p => !p.Partial);
            int loop = full == null ? -1 : Step(full.End) - Step(full.Start);
            foreach (var pass in passes)
            {
                int from = Step(pass.Start), to = Step(pass.End); if (to <= from) continue;
                int length = to - from;
                if (!pass.Partial || loop < length)
                {
                    int period = Period(g, from, to);
                    for (int a = from; a < to; a += period)
                        windows.Add(new Window { From = a, To = Math.Min(to, a + period), Loop = Math.Min(to, a + period) - a, Start = g.Start[a], End = Math.Min(to, a + period) == to ? Math.Max(pass.End, g.End[to - 1]) : g.Start[a + period] });
                    continue;
                }
                // A lead-in ends where the loop ends; a tag starts where it starts.
                bool leadIn = pass.Offset > 1e-6;
                windows.Add(new Window { From = from, To = to, Loop = loop, Offset = leadIn ? loop - length : 0, Partial = true, Start = pass.Start, End = pass.End, OffsetBeats = pass.Offset });
            }
        }
        return windows;
    }

    // The shortest whole number of bars the lane's chords repeat at, exactly, across the window.
    static int Period(ChordTimeline.Grid g, int from, int to)
    {
        var bars = Enumerable.Range(from, to - from).GroupBy(i => g.Bar[i]).Select(b => b.ToArray()).ToList();
        if (Enumerable.Range(from, to - from).All(i => g.State[i] < 0)) return to - from;
        foreach (int p in new[] { 1, 2, 3, 4, 6, 8 })
        {
            if (p >= bars.Count || bars.Count % p != 0) continue;
            int steps = bars.Take(p).Sum(b => b.Length);
            if ((to - from) % steps != 0) continue;
            bool repeats = true;
            for (int i = from + steps; i < to && repeats; i++) repeats = Same(g.State[i], g.State[i - steps]);
            if (repeats) return steps;
        }
        return to - from;
    }

    static void Assign(ChordTimeline.Grid g, Window w, int index, List<Fundamental> fundamentals)
    {
        w.Changed.Clear();
        if (Enumerable.Range(w.From, w.Length).All(i => g.State[i] < 0)) { w.Pattern = -1; return; }
        double best = -1; int chosen = -1, t = 0;
        for (int id = 0; id < fundamentals.Count; id++)
        {
            var (score, shift) = Best(g.State, w, fundamentals[id].States);
            if (score > best + 1e-9) { best = score; chosen = id; t = shift; }
        }
        if (chosen >= 0 && best >= .5)
        {
            w.Pattern = chosen; w.T = t; w.Score = best;
            Agreement(g.State, w, fundamentals[chosen].States, t, w.Changed);
            return;
        }
        // A new pattern. A partial window stands as a loop of its own.
        if (w.Partial) { w.Loop = w.Length; w.Offset = 0; }
        var starts = Enumerable.Range(w.From, w.Length).Select(i => g.Start[i] - w.Start).ToArray();
        var ends = Enumerable.Range(w.From, w.Length).Select(i => g.End[i] - w.Start).ToArray();
        fundamentals.Add(new Fundamental { States = Enumerable.Range(w.From, w.Length).Select(i => g.State[i]).ToArray(), Starts = starts, Ends = ends, Reference = index, Bars = g.Bar[w.To - 1] - g.Bar[w.From] + 1 });
        w.Pattern = fundamentals.Count - 1; w.T = 0; w.Score = 1;
    }

    static double[] Spans(Window w, List<Fundamental> fundamentals, ChordTimeline.Grid g)
    {
        if (w.Pattern < 0 || w.Changed.Count == 0) return Array.Empty<double>();
        var f = fundamentals[w.Pattern]; var spans = new List<double>();
        foreach (int k in w.Changed.OrderBy(k => k))
        {
            double a = f.Starts[k], b = f.Ends[k];
            if (spans.Count > 0 && Math.Abs(spans[^1] - a) < 1e-6) spans[^1] = b; else { spans.Add(a); spans.Add(b); }
        }
        return spans.ToArray();
    }

    static string Letter(int index) => index < 26 ? ((char)('A' + index)).ToString() : "P" + (index + 1);
    static readonly string[] Primes = { "", "′", "″", "‴" };
    public static string Token(PreparedPatternSong.InstrumentPart part, PreparedPatternSong.LanePlay play)
    {
        if (play.Pattern < 0) return "·";
        string token = part.Patterns[play.Pattern].Letter + (play.Variation < Primes.Length ? Primes[play.Variation] : "^" + play.Variation);
        if (play.Transpose != 0) { int t = HarmonyModel.Mod(play.Transpose); token += t <= 6 ? "+" + t : "−" + (12 - t); }
        return token;
    }
    // Consecutive equal tokens fold into ×n; consecutive rests are one rest.
    static string Grammar(PreparedPatternSong.InstrumentPart part)
    {
        var tokens = part.Plays.Select(p => Token(part, p)).ToList();
        var output = new List<string>();
        for (int i = 0; i < tokens.Count;)
        {
            int j = i; while (j < tokens.Count && tokens[j] == tokens[i]) j++;
            output.Add(tokens[i] == "·" || j - i == 1 ? tokens[i] : $"{tokens[i]}×{j - i}"); i = j;
        }
        return string.Join(" ", output);
    }

    static string Role(PreparedPatternSong.InstrumentPart part, MidiCycleAnalysis.Hit[] notes)
    {
        string name = part.Name.ToLowerInvariant();
        if (part.Vocal) return "vocal";
        foreach (var (word, role) in new[] { ("bass", "bass"), ("piano", "keys"), ("keys", "keys"), ("organ", "keys"), ("guitar", "guitar"), ("string", "strings"), ("pad", "pad"), ("lead", "lead"), ("melody", "lead") })
            if (name.Contains(word)) return role;
        double pitch = notes.Average(n => n.Pitch);
        int together = notes.GroupBy(n => Math.Round(n.Beat * 24)).Count(g => g.Count() >= 3);
        return pitch < 48 ? "bass" : together > notes.Length / 8 ? "chords" : pitch >= 64 ? "lead" : "line";
    }
}
