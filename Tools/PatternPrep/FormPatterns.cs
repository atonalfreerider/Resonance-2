// Pattern compression: reduce the song to its fundamentals and name the variations.
//
// 1. Every section family gets ONE fundamental: the shortest chord loop that the
//    family's visits repeat (a four-bar verse loop played twice is one loop, ×2).
//    The loop is a consensus of all agreeing passes, so a varied first pass does not
//    become the reference.
// 2. Every visit is described as passes of that loop: where each pass starts, its
//    transposition, whether it is partial (a pickup or a tag), and the loop-relative
//    beats whose harmony differs from the fundamental.
// 3. Families that recur together (Verse + Chorus, or a classical part) become groups,
//    and the whole song is written as a compressed grammar: In (V C)×2 Br C′ Out.
public static partial class FormAnalysis
{
    public sealed class PatternInfo
    {
        public int Family, Reference, LoopBars, SectionBars, Visits, Passes;
        public double LoopBeats;
        public string Name = "", Role = "", Letter = "", Short = "";
        public List<SongFormAnalysis.ChordStep> Loop = new();
    }
    public sealed class PassInfo
    {
        public double Start, End, Offset;
        public int Transpose, FirstBar, Bars;
        public bool Partial;
        public List<double> Changed = new();
        public List<int> ChangedBars = new(), ChangedAt = new(); // loop positions (0-based) and song bars with a harmony change
    }
    public sealed class GroupInfo
    {
        public int Id, Visits;
        public string Name = "", Short = "";
        public int[] Families = Array.Empty<int>();
    }

    // Seventh and extension colours do not change a loop's harmony: A and A7 agree.
    static char Triad(int state) => ChordTimeline.Quality(state) switch { "m" or "m7" => 'm', "dim" => 'd', _ => 'M' };
    static bool SameHarmony(int a, int b) => a < 0 || b < 0 ? a == b : ChordTimeline.Root(a) == ChordTimeline.Root(b) && Triad(a) == Triad(b);

    // Share of beat steps in bar y that agree with bar x transposed by t.
    static double Agreement(Bars bars, int x, int y, int t)
    {
        var a = bars.States[x]; var b = bars.States[y];
        if (a.Length == 0 || a.Length != b.Length) return 0;
        int same = 0;
        for (int k = 0; k < a.Length; k++) if (SameHarmony(ChordTimeline.Transpose(a[k], t), b[k])) same++;
        return same / (double)a.Length;
    }

    sealed class Slot { public int Bar, T; }
    sealed class Block { public int Visit, FirstBar, Bars, Offset, T; public bool Partial; public double Score; }
    sealed class Fit { public int Period; public Slot[] Fundamental = Array.Empty<Slot>(); public List<Block> Blocks = new(); public bool Accepted; }

    static double BlockScore(Bars bars, Slot[] f, Block b)
    {
        double sum = 0;
        for (int k = 0; k < b.Bars; k++) { var s = f[(b.Offset + k) % f.Length]; sum += Agreement(bars, s.Bar, b.FirstBar + k, HarmonyModel.Mod(b.T - s.T)); }
        return b.Bars == 0 ? 0 : sum / b.Bars;
    }

    // Lay one visit out as passes of a period-p loop. A visit may begin part-way through
    // the loop (a pickup or an intro bar folded into the verse); the best alignment wins,
    // and the start of the section is preferred unless another offset is clearly better.
    static List<Block> Layout(Bars bars, Slot[] f, int visit, (int start, int length) seg, int t)
    {
        int p = f.Length; List<Block> best = null; double bestScore = double.NegativeInfinity;
        for (int lead = 0; lead < Math.Min(p, seg.length); lead++)
        {
            if (lead > 0 && seg.length - lead < p) break;
            var blocks = new List<Block>();
            if (lead > 0) blocks.Add(new Block { Visit = visit, FirstBar = seg.start, Bars = lead, Offset = p - lead, T = t, Partial = true });
            for (int b = seg.start + lead; b < seg.start + seg.length; b += p)
                blocks.Add(new Block { Visit = visit, FirstBar = b, Bars = Math.Min(p, seg.start + seg.length - b), T = t, Partial = seg.start + seg.length - b < p });
            foreach (var block in blocks) block.Score = BlockScore(bars, f, block);
            var full = blocks.Where(b => !b.Partial).ToList();
            double score = (full.Count > 0 ? full.Average(b => b.Score) : blocks.Average(b => b.Score)) - (lead > 0 ? .08 : 0);
            if (score > bestScore + 1e-9) { bestScore = score; best = blocks; }
        }
        return best ?? new List<Block>();
    }

    static Fit Try(Bars bars, int p, int[] visits, int reference, List<(int start, int length)> segments, int[] tv, bool forced)
    {
        var fit = new Fit { Period = p };
        var seed = Enumerable.Range(0, p).Select(q => new Slot { Bar = segments[reference].start + q, T = tv[reference] }).ToArray();
        for (int round = 0; round < 2; round++)
        {
            var f = round == 0 ? seed : fit.Fundamental;
            fit.Blocks = visits.SelectMany(v => Layout(bars, f, v, segments[v], tv[v])).ToList();
            // Consensus: at each loop position keep the bar that best agrees with the others.
            var agreeing = fit.Blocks.Where(b => !b.Partial && b.Score >= .75).ToList();
            fit.Fundamental = Enumerable.Range(0, p).Select(q =>
            {
                var candidates = agreeing.Select(b => new Slot { Bar = b.FirstBar + q, T = b.T }).ToList();
                if (candidates.Count < 3) return f[q];
                return candidates.OrderByDescending(c => candidates.Sum(o => Agreement(bars, c.Bar, o.Bar, HarmonyModel.Mod(o.T - c.T)))).ThenBy(c => c.Bar).First();
            }).ToArray();
        }
        foreach (var b in fit.Blocks) b.Score = BlockScore(bars, fit.Fundamental, b);
        var full = fit.Blocks.Where(b => !b.Partial).ToList();
        var own = full.Where(b => b.Visit == reference).ToList();
        fit.Accepted = forced || own.Count >= 2 && own.All(b => b.Score >= .75) && full.Count(b => b.Score >= .75) >= .8 * full.Count;
        return fit;
    }

    static IEnumerable<int> Periods(int length) => new[] { 1, 2, 3, 4, 6, 8, 12, 16 }.Where(p => p * 2 <= length);

    static void BuildPatterns(Result result, Bars bars, ChordTimeline.Grid grid, MidiCycleAnalysis cycles,
        List<(int start, int length)> segments, int[] family, int[] transpose)
    {
        var steps = Enumerable.Range(0, bars.Count).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < grid.Count; i++) steps[grid.Bar[i]].Add(i);
        var sections = result.Sections; var form = result.Form;
        foreach (int f in family.Distinct())
        {
            var visits = Enumerable.Range(0, segments.Count).Where(s => family[s] == f).ToArray();
            // The reference visit has the family's most common length (the earliest such).
            int refLength = visits.GroupBy(s => segments[s].length).OrderByDescending(g => g.Count()).ThenBy(g => g.First()).First().Key;
            int reference = visits.First(s => segments[s].length == refLength);
            Fit fit = null;
            foreach (int p in Periods(refLength)) { var trial = Try(bars, p, visits, reference, segments, transpose, false); if (trial.Accepted) { fit = trial; break; } }
            fit ??= Try(bars, refLength, visits, reference, segments, transpose, true);
            int period = fit.Period;
            // Loop-relative beat position of each bar and step in the fundamental.
            var barOffset = new double[period + 1];
            for (int q = 0; q < period; q++) barOffset[q + 1] = barOffset[q] + bars.Length[fit.Fundamental[q].Bar];
            var pattern = new PatternInfo
            {
                Family = f, Reference = reference, LoopBars = period, LoopBeats = barOffset[period], SectionBars = refLength, Visits = visits.Length,
                Name = form.Sections[reference].Family.Name, Role = sections[reference].Role, Letter = sections[reference].Letter.TrimEnd('′', '″', '‴'),
                Passes = fit.Blocks.Count(b => b.Visit == reference && !b.Partial)
            };
            for (int q = 0; q < period; q++)
            {
                var slot = fit.Fundamental[q]; var bar = cycles.Measures[slot.Bar];
                foreach (int i in steps[slot.Bar])
                {
                    int state = ChordTimeline.Transpose(grid.State[i], -slot.T);
                    double a = barOffset[q] + grid.Start[i] - bar.Start, b = barOffset[q] + grid.End[i] - bar.Start;
                    var last = pattern.Loop.LastOrDefault();
                    if (last != null && last.Root == ChordTimeline.Root(state) && last.Quality == ChordTimeline.Quality(state) && Math.Abs(last.End - a) < 1e-6) { last.End = b; continue; }
                    pattern.Loop.Add(new SongFormAnalysis.ChordStep { Start = a, End = b, Root = ChordTimeline.Root(state), Quality = ChordTimeline.Quality(state), Energy = (float)grid.Weight[i] });
                }
            }
            result.Patterns.Add(pattern);
            foreach (int v in visits)
            {
                var info = sections[v]; var section = form.Sections[v];
                info.Passes.Clear();
                foreach (var block in fit.Blocks.Where(b => b.Visit == v))
                {
                    // A pass that does not fit at the visit's transposition may have modulated.
                    int t = block.T;
                    if (period >= 2 && block.Score < .75)
                    {
                        var shifted = Enumerable.Range(0, 12).Select(x => (t: x, score: BlockScore(bars, fit.Fundamental, new Block { FirstBar = block.FirstBar, Bars = block.Bars, Offset = block.Offset, T = x }))).OrderByDescending(x => x.score).First();
                        if (shifted.score >= .9) { t = shifted.t; block.T = t; block.Score = shifted.score; }
                    }
                    var pass = new PassInfo
                    {
                        Start = Math.Max(section.Start, cycles.Measures[block.FirstBar].Start), End = cycles.Measures[block.FirstBar + block.Bars - 1].End,
                        Offset = barOffset[block.Offset], Transpose = t, Partial = block.Partial, FirstBar = block.FirstBar, Bars = block.Bars
                    };
                    if (block.FirstBar + block.Bars == segments[v].start + segments[v].length) pass.End = Math.Max(pass.End, section.End);
                    for (int k = 0; k < block.Bars; k++)
                    {
                        int q = (block.Offset + k) % period; var slot = fit.Fundamental[q];
                        var a = bars.States[slot.Bar]; var b = bars.States[block.FirstBar + k]; var bar = cycles.Measures[block.FirstBar + k];
                        var differ = Enumerable.Range(0, b.Length).Where(j => j >= a.Length || !SameHarmony(ChordTimeline.Transpose(a[j], HarmonyModel.Mod(t - slot.T)), b[j])).ToList();
                        // One differing beat is a passing chord or estimation spill-over; a
                        // variation changes at least half the bar.
                        if (differ.Count == 0 || differ.Count * 2 < b.Length) continue;
                        foreach (int j in differ)
                        {
                            int step = steps[block.FirstBar + k][j];
                            double from = barOffset[q] + grid.Start[step] - bar.Start, to = barOffset[q] + grid.End[step] - bar.Start;
                            if (pass.Changed.Count > 0 && Math.Abs(pass.Changed[^1] - from) < 1e-6) pass.Changed[^1] = to; else { pass.Changed.Add(from); pass.Changed.Add(to); }
                        }
                        pass.ChangedBars.Add(q); pass.ChangedAt.Add(block.FirstBar + k);
                    }
                    info.Passes.Add(pass);
                }
                int full = info.Passes.Count(x => !x.Partial);
                info.Loops = Math.Max(1, full);
                info.CycleBeats = pattern.LoopBeats;
                section.ProgressionBeats = Math.Min(pattern.LoopBeats, section.End - section.Start);
                section.Chords.Clear();
                foreach (var c in form.Timeline.Where(c => c.End > section.Start && c.Start < section.Start + section.ProgressionBeats))
                    section.Chords.Add(new SongFormAnalysis.ChordStep { Start = Math.Max(section.Start, c.Start), End = Math.Min(section.Start + section.ProgressionBeats, c.End), Root = c.Root, Quality = c.Quality, Energy = c.Energy });
            }
            foreach (int v in visits) sections[v].Variation = Describe(sections, v, reference, pattern, segments[v].start);
        }
        result.SongBars = bars.Count;
        result.FundamentalBars = result.Patterns.Sum(p => p.LoopBars);
    }

    static string Signed(int t) { t = HarmonyModel.Mod(t); return t <= 6 ? "+" + t : "−" + (12 - t); }

    // How one visit differs from the family fundamental, in a few words.
    static string Describe(List<SectionInfo> sections, int visit, int reference, PatternInfo pattern, int firstBar)
    {
        var info = sections[visit]; var mine = info.Passes; var theirs = sections[reference].Passes;
        var parts = new List<string>();
        int t = mine.Where(p => !p.Partial).Select(p => p.Transpose).DefaultIfEmpty(mine.FirstOrDefault()?.Transpose ?? 0).First();
        int baseT = theirs.FirstOrDefault(p => !p.Partial)?.Transpose ?? 0;
        if (HarmonyModel.Mod(t - baseT) != 0) parts.Add($"transposed {Signed(t - baseT)}");
        var modulated = mine.Where(p => !p.Partial && p.Transpose != t).ToList();
        if (modulated.Count > 0) parts.Add($"modulates {Signed(modulated[0].Transpose - t)} in pass {mine.Where(p => !p.Partial).ToList().IndexOf(modulated[0]) + 1}");
        if (mine.Count > 0 && mine[0].Partial && mine.Count > 1) parts.Add($"{mine[0].Bars}-bar lead-in");
        int full = mine.Count(p => !p.Partial), refFull = theirs.Count(p => !p.Partial);
        if (visit != reference && pattern.LoopBars < pattern.SectionBars)
        {
            if (full > refFull) parts.Add($"{full} passes (usually {refFull})");
            else if (full < refFull) parts.Add($"{full} of {refFull} passes");
        }
        var tail = mine.Count > 1 && mine[^1].Partial ? mine[^1] : null;
        if (tail != null) parts.Add(tail.ChangedBars.Count == 0 ? $"{tail.Bars}-bar tag" : $"extended {tail.Bars} bar{(tail.Bars > 1 ? "s" : "")}");
        if (mine.Count == 1 && mine[0].Partial) parts.Add($"{mine[0].Bars} of {pattern.LoopBars} bars");
        // Harmony changes are compared with the reference visit, not the bare loop: a
        // strain whose answer phrase always varies is not a varied strain.
        var mineFull = mine.Where(p => !p.Partial).ToList(); var theirFull = theirs.Where(p => !p.Partial).ToList();
        var changed = new SortedSet<int>(); var lastDiffers = new List<int>(); bool earlier = false;
        for (int k = 0; k < mineFull.Count; k++)
        {
            var own = mineFull[k].ChangedBars; var other = k < theirFull.Count ? theirFull[k].ChangedBars : theirFull.LastOrDefault()?.ChangedBars ?? new List<int>();
            var diff = own.Except(other).Concat(other.Except(own)).Distinct().ToList();
            foreach (int q in diff) changed.Add(mineFull[k].FirstBar - firstBar + 1 + q);
            if (k == mineFull.Count - 1) lastDiffers = diff; else if (diff.Count > 0) earlier = true;
        }
        if (changed.Count > 0)
        {
            bool ending = mineFull.Count > 1 && !earlier && lastDiffers.All(q => q >= pattern.LoopBars / 2);
            parts.Add(ending ? "new ending" : changed.Count <= 3 ? $"varied bar{(changed.Count > 1 ? "s" : "")} {string.Join(", ", changed)}" : $"varied in {changed.Count} bars");
        }
        if (parts.Count > 0) return string.Join(" · ", parts);
        return visit == reference ? "fundamental" : $"repeats {sections[reference].Label}";
    }

    // A visit whose harmony departs from its fundamental is marked with a prime in the grammar.
    static bool Varied(SectionInfo info) =>
        info.Variation.Contains("transposed") || info.Variation.Contains("modulates") || info.Variation.Contains("varied") || info.Variation.Contains("new ending");

    public static string Short(string role, string letter, string style)
    {
        if (style == "classical" && role == "Theme") return letter.TrimEnd('′', '″', '‴');
        string r = role.Trim().ToLowerInvariant();
        foreach (var (key, value) in new[] { ("intro", "In"), ("pre-chorus", "PC"), ("prechorus", "PC"), ("post-chorus", "PoC"), ("verse", "V"), ("chorus", "C"),
            ("bridge", "Br"), ("interlude", "It"), ("outro", "Out"), ("refrain", "R"), ("coda", "Co"), ("link", "Ln"), ("solo", "So"), ("break", "Bk"),
            ("count", "Ct"), ("tag", "Tg"), ("instrumental", "Ins"), ("hook", "H"), ("theme", "T") })
            if (r.StartsWith(key, StringComparison.Ordinal)) return value;
        if (r.StartsWith("part ", StringComparison.Ordinal) && r.Length > 5) return r[5..].ToLowerInvariant();
        var words = role.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1) return string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])));
        return role.Length <= 4 ? role : char.ToUpperInvariant(role[0]) + role.Substring(1, 1);
    }

    // Recurring runs of different families, longest-and-most-repeated first. Each visit
    // belongs to at most one run. Shared with SectionCompression for reviewed forms.
    public static List<(int start, int length, int group)> Combos(IReadOnlyList<int> families)
    {
        int n = families.Count; var occupied = new bool[n]; var found = new List<(int, int, int)>(); int id = 0;
        while (true)
        {
            int bestScore = 0, bestLength = 0; List<int> bestStarts = null;
            for (int length = 2; length <= n / 2; length++)
                for (int start = 0; start + length <= n; start++)
                {
                    if (Enumerable.Range(start, length).Any(i => occupied[i])) continue;
                    var sequence = Enumerable.Range(start, length).Select(i => families[i]).ToArray();
                    if (sequence.Distinct().Count() < 2) continue;
                    var matches = new List<int>();
                    for (int i = 0; i + length <= n; i++)
                        if (Enumerable.Range(0, length).All(j => !occupied[i + j] && families[i + j] == sequence[j])) { matches.Add(i); i += length - 1; }
                    int score = (matches.Count - 1) * (length - 1);
                    if (matches.Count >= 2 && score > bestScore) { bestScore = score; bestLength = length; bestStarts = matches; }
                }
            if (bestStarts == null) break;
            foreach (int start in bestStarts) { found.Add((start, bestLength, id)); for (int j = 0; j < bestLength; j++) occupied[start + j] = true; }
            id++;
        }
        return found.OrderBy(x => x.Item1).ToList();
    }

    static void BuildGroups(Result result, int[] family)
    {
        var sections = result.Sections; int n = sections.Count;
        var shorts = sections.Select(s => Short(s.Role, s.Letter, result.Style)).ToArray();
        var occurrences = new List<(int start, int length, int group)>();
        if (sections.Any(s => s.Parent != "Song"))
        {
            // Classical parts (or reviewed parents): every run under one parent is a visit.
            var names = new List<string>();
            for (int i = 0; i < n;)
            {
                int j = i; while (j < n && sections[j].Parent == sections[i].Parent) j++;
                if (sections[i].Parent != "Song")
                {
                    string name = sections[i].Parent.Split('/').Last().TrimEnd('′', '″', '‴');
                    int g = names.IndexOf(name); if (g < 0) { g = names.Count; names.Add(name); }
                    occurrences.Add((i, j - i, g));
                }
                i = j;
            }
            foreach (var (name, g) in names.Select((x, g) => (x, g)))
            {
                var first = occurrences.First(o => o.group == g);
                result.Groups.Add(new GroupInfo { Id = g, Name = name, Short = name.StartsWith("Part ") ? name[5..] : name, Visits = occurrences.Count(o => o.group == g),
                    Families = Enumerable.Range(first.start, first.length).Select(i => family[i]).Distinct().ToArray() });
            }
        }
        else
        {
            occurrences = Combos(family);
            foreach (int g in occurrences.Select(o => o.group).Distinct())
            {
                var first = occurrences.First(o => o.group == g);
                var members = Enumerable.Range(first.start, first.length).ToArray();
                result.Groups.Add(new GroupInfo { Id = g, Name = string.Join(" + ", members.Select(i => sections[i].Role)), Short = string.Join(" ", members.Select(i => shorts[i])),
                    Visits = occurrences.Count(o => o.group == g), Families = members.Select(i => family[i]).ToArray() });
            }
            foreach (var (start, length, g) in occurrences)
                for (int i = start; i < start + length; i++) sections[i].Parent = "Song/" + result.Groups[g].Name.Replace('/', '-');
        }
        var visits = new Dictionary<int, int>();
        foreach (var (start, length, g) in occurrences)
        {
            visits[g] = visits.GetValueOrDefault(g) + 1;
            for (int i = start; i < start + length; i++) { sections[i].Group = g; sections[i].GroupVisit = visits[g]; }
        }
        // Grammar: groups in parentheses (or part letters), primes on varied returns,
        // and consecutive identical tokens folded into ×n.
        string Token(int k) => shorts[k] + (Varied(sections[k]) ? "′" : "");
        var tokens = new List<string>();
        for (int i = 0; i < n;)
        {
            var occurrence = occurrences.FirstOrDefault(o => o.start == i && o.length > 0);
            if (occurrence.length > 0)
            {
                tokens.Add("(" + string.Join(" ", Fold(Enumerable.Range(i, occurrence.length).Select(Token).ToList())) + ")");
                i += occurrence.length;
            }
            else { tokens.Add(Token(i)); i++; }
        }
        result.Grammar = string.Join(" ", Fold(tokens));
        foreach (var p in result.Patterns) p.Short = shorts[p.Reference];
    }

    // Consecutive identical tokens fold into ×n.
    static List<string> Fold(List<string> tokens)
    {
        var folded = new List<string>();
        for (int i = 0; i < tokens.Count;)
        {
            int j = i; while (j < tokens.Count && tokens[j] == tokens[i]) j++;
            folded.Add(j - i > 1 ? $"{tokens[i]}×{j - i}" : tokens[i]); i = j;
        }
        return folded;
    }

    // Copy the compression into the playback contract (sections are index-aligned).
    public static void Apply(Result result, PreparedPatternSong song)
    {
        song.FormGrammar = result.Grammar; song.SongBars = result.SongBars; song.FundamentalBars = result.FundamentalBars;
        song.Patterns = result.Patterns.Select(p => new PreparedPatternSong.Pattern
        {
            Family = p.Family, Reference = p.Reference, LoopBars = p.LoopBars, SectionBars = p.SectionBars, Visits = p.Visits, Passes = p.Passes,
            LoopBeats = p.LoopBeats, Name = p.Name, Role = p.Role, Letter = p.Letter, Short = p.Short, Loop = p.Loop.ToArray()
        }).ToArray();
        song.Groups = result.Groups.Select(g => new PreparedPatternSong.SectionGroup { Id = g.Id, Visits = g.Visits, Name = g.Name, Short = g.Short, Families = g.Families }).ToArray();
        for (int i = 0; i < song.Sections.Length && i < result.Sections.Count; i++)
        {
            var info = result.Sections[i]; var section = song.Sections[i];
            section.Passes = info.Passes.Select(p => new PreparedPatternSong.Pass { Start = p.Start, End = p.End, Offset = p.Offset, Transpose = p.Transpose, Partial = p.Partial, Changed = p.Changed.ToArray() }).ToArray();
            section.Variation = info.Variation; section.Group = info.Group; section.GroupVisit = info.GroupVisit; section.Short = Short(info.Role, info.Letter, result.Style);
        }
    }
}
