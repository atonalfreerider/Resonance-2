// Offline song-form analysis: bars → sections → families → named roles → larger parts.
//
// 1. Every bar gets a harmonic fingerprint (smoothed chords, chroma, bass, rhythm).
// 2. Bars are compared under all 12 transpositions, so a chorus sung a step higher
//    still counts as the same chorus.
// 3. Dynamic programming chooses section boundaries that explain as much of the song
//    as possible by repetition (a minimum-description-length choice), with a mild
//    preference for 4/8/16-bar phrases and for boundaries where the texture changes.
// 4. Repeating sections become one family. Families are named with pop roles
//    (Intro, Verse, Pre-Chorus, Chorus, Bridge, Outro) or classical letters (A, B, A′)
//    and the family sequence is matched against common forms (verse–chorus, AABA,
//    binary, ternary, rondo, theme and variations).
// 5. Inside each section the shortest repeating chord loop becomes the progression wheel.
public static class FormAnalysis
{
    public sealed class SectionInfo
    {
        public string Label = "", Role = "", Letter = "", Parent = "Song";
        public int Visit = 1, Transpose, PhraseBars = 4, Loops = 1, KeyRoot = -1, Part = -1;
        public bool KeyMinor;
        public double CycleBeats, Similarity = 1;
    }
    public sealed class Result
    {
        public SongFormAnalysis Form = new();
        public List<SectionInfo> Sections = new();
        public string Style = "", FormName = "", Summary = "";
        public ChordTimeline.Grid Grid = new();
    }

    // ---------- Bar fingerprints and similarity ----------
    internal sealed class Bars
    {
        public int Count;
        public double[] Length = Array.Empty<double>();
        public double[][] Chroma = Array.Empty<double[]>(), BassChroma = Array.Empty<double[]>(), Rhythm = Array.Empty<double[]>();
        public int[][] States = Array.Empty<int[]>(), Melody = Array.Empty<int[]>();
        public double[][] MelodyPcs = Array.Empty<double[]>();
        public double[] Energy = Array.Empty<double>(), Density = Array.Empty<double>(), Register = Array.Empty<double>();
        public float[][,] Sim = Array.Empty<float[,]>(); // [t][i,j]: bar i transposed by t against bar j
        public float[][,] Harm = Array.Empty<float[,]>(); // harmony only: chords, chroma, bass (variations keep it)
        public int Unit = 4;
    }

    static double[] Normalize(double[] v) { double n = Math.Sqrt(v.Sum(x => x * x)); return n <= 0 ? v : v.Select(x => x / n).ToArray(); }
    static double Cos(double[] a, double[] b, int shift = 0)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0; for (int i = 0; i < a.Length; i++) dot += a[i] * b[HarmonyModel.Mod(i + shift, a.Length)];
        return dot;
    }

    static Bars Fingerprint(MidiCycleAnalysis cycles, ChordTimeline.Grid grid)
    {
        int count = cycles.Measures.Count;
        var bars = new Bars { Count = count, Length = cycles.Measures.Select(m => m.End - m.Start).ToArray() };
        bars.Chroma = new double[count][]; bars.BassChroma = new double[count][]; bars.Rhythm = new double[count][]; bars.States = new int[count][];
        bars.Melody = new int[count][]; bars.MelodyPcs = new double[count][];
        bars.Energy = new double[count]; bars.Density = new double[count]; bars.Register = new double[count];
        var steps = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < grid.Count; i++) steps[grid.Bar[i]].Add(i);
        var pitched = cycles.Notes.Where(n => n.Channel != 10).OrderBy(n => n.Beat).ToArray();
        var drums = cycles.Notes.Where(n => n.Channel == 10).ToArray();
        for (int b = 0; b < count; b++)
        {
            var bar = cycles.Measures[b];
            var chroma = new double[12]; var bass = new double[12];
            foreach (int i in steps[b]) { for (int pc = 0; pc < 12; pc++) chroma[pc] += grid.Chroma[i][pc]; if (grid.Bass[i] != int.MaxValue) bass[HarmonyModel.Mod(grid.Bass[i] - 21)] += 1; }
            bars.Chroma[b] = Normalize(chroma); bars.BassChroma[b] = Normalize(bass);
            bars.States[b] = steps[b].Select(i => grid.State[i]).ToArray();
            int bins = Math.Max(1, (int)Math.Round(bars.Length[b] * 4));
            var rhythm = new double[bins * 2];
            var inBar = pitched.Where(n => n.Beat >= bar.Start - 1e-6 && n.Beat < bar.End - 1e-6).ToArray();
            foreach (var n in inBar) rhythm[Math.Min(bins - 1, (int)((n.Beat - bar.Start) * 4))] += n.Velocity;
            foreach (var n in drums.Where(n => n.Beat >= bar.Start - 1e-6 && n.Beat < bar.End - 1e-6)) rhythm[bins + Math.Min(bins - 1, (int)((n.Beat - bar.Start) * 4))] += n.Velocity;
            bars.Rhythm[b] = Normalize(rhythm);
            bars.Energy[b] = inBar.Sum(n => n.Velocity * Math.Min(1, n.Length)) / Math.Max(1, bars.Length[b]);
            bars.Density[b] = inBar.Length / Math.Max(1, bars.Length[b]);
            bars.Register[b] = inBar.Length == 0 ? 0 : inBar.GroupBy(n => n.Beat).Average(g => g.Max(n => n.Pitch));
            // Top voice sampled eight times per bar: the tune is what tells a verse from a
            // chorus when both use the same four chords.
            var melody = new int[8]; var pcs = new double[12];
            var sounding = pitched.Where(n => n.Beat < bar.End && n.Beat + n.Length > bar.Start).ToArray();
            for (int k = 0; k < 8; k++)
            {
                double t = bar.Start + (k + .5) * bars.Length[b] / 8;
                var top = sounding.Where(n => n.Beat <= t && t < n.Beat + n.Length).Select(n => n.Pitch).DefaultIfEmpty(-1).Max();
                melody[k] = top; if (top >= 0) pcs[HarmonyModel.Mod(top - 21)] += 1;
            }
            bars.Melody[b] = melody; bars.MelodyPcs[b] = Normalize(pcs);
        }
        bars.Sim = new float[12][,]; bars.Harm = new float[12][,];
        for (int t = 0; t < 12; t++)
        {
            var sim = new float[count, count]; var harm = new float[count, count];
            for (int i = 0; i < count; i++) for (int j = 0; j < count; j++) { var (full, harmonic) = BarSimilarity(bars, i, j, t); sim[i, j] = (float)full; harm[i, j] = (float)harmonic; }
            bars.Sim[t] = sim; bars.Harm[t] = harm;
        }
        // Phrase unit: the power of two closest to 16–24 beats (4 bars of 4/4, 8 of 3/4 or 2/4, 4 of 6/4).
        double beatsPerBar = bars.Length.GroupBy(x => Math.Round(x, 3)).OrderByDescending(g => g.Count()).First().Key;
        bars.Unit = Math.Clamp(1 << (int)Math.Floor(Math.Log2(Math.Max(1, 24 / Math.Max(.5, beatsPerBar)))), 2, 8);
        return bars;
    }

    static double Ratio(double a, double b) => Math.Max(a, b) <= 1e-9 ? 1 : 1 - Math.Abs(a - b) / Math.Max(a, b);
    static (double full, double harmonic) BarSimilarity(Bars bars, int i, int j, int t)
    {
        if (Math.Abs(bars.Length[i] - bars.Length[j]) > 1e-6) return (0, 0);
        var a = bars.States[i]; var b = bars.States[j];
        if (a.Length == 0 || a.Length != b.Length) return (0, 0);
        bool silentA = bars.Chroma[i].All(x => x == 0), silentB = bars.Chroma[j].All(x => x == 0);
        if (silentA || silentB) { double quiet = silentA && silentB ? .9 * Cos(bars.Rhythm[i], bars.Rhythm[j]) + .1 : 0; return (quiet, quiet); }
        int agree = 0; for (int k = 0; k < a.Length; k++) if (ChordTimeline.Transpose(a[k], t) == b[k]) agree++;
        double chords = agree / (double)a.Length;
        // Chroma cosine is high even for unrelated chords in one key; rescale 0.5..1 → 0..1.
        double chroma = Math.Max(0, (Cos(bars.Chroma[j], bars.Chroma[i], -t) - .5) / .5);
        double bass = Math.Max(0, (Cos(bars.BassChroma[j], bars.BassChroma[i], -t) - .3) / .7);
        double rhythm = Cos(bars.Rhythm[i], bars.Rhythm[j]);
        int same = 0, heard = 0;
        for (int k = 0; k < 8; k++)
        {
            int x = bars.Melody[i][k], y = bars.Melody[j][k];
            if (x < 0 && y < 0) continue;
            heard++; if (x >= 0 && y >= 0 && HarmonyModel.Mod(x + t - y) == 0) same++;
        }
        double melody = heard == 0 ? 1 : same / (double)heard;
        double texture = (Ratio(bars.Energy[i], bars.Energy[j]) + Ratio(bars.Density[i], bars.Density[j]) + Math.Max(0, 1 - Math.Abs(bars.Register[i] + t - bars.Register[j]) / 12)) / 3;
        return (.30 * chords + .12 * chroma + .08 * bass + .15 * rhythm + .25 * melody + .10 * texture, .50 * chords + .30 * chroma + .20 * bass);
    }

    // Mean similarity of bars [i, i+L) against [j, j+L) under transposition t.
    static double Segment(Bars bars, int i, int j, int length, int t, bool harmonic = false)
    {
        double sum = 0; var sim = harmonic ? bars.Harm[t] : bars.Sim[t];
        for (int k = 0; k < length; k++) sum += sim[i + k, j + k];
        return sum / length;
    }
    const double TransposePenalty = .04;
    static (double score, int t) BestMatch(Bars bars, int i, int j, int length, bool harmonic = false)
    {
        double best = double.NegativeInfinity; int bestT = 0;
        for (int t = 0; t < 12; t++)
        {
            double s = Segment(bars, i, j, length, t, harmonic) - (t == 0 ? 0 : TransposePenalty);
            if (s > best) { best = s; bestT = t; }
        }
        return (best, bestT);
    }

    // Novelty at each bar boundary: how different the window before is from the window after.
    // Windows span a whole phrase, so chord changes inside a loop average out and what is
    // left is a change of tune, harmony collection, rhythm or energy: a section boundary.
    static double[] Novelty(Bars bars, int unit)
    {
        var novelty = new double[bars.Count + 1];
        double[] Sum(Func<int, double[]> feature, int from, int to)
        {
            double[] total = null;
            for (int b = Math.Max(0, from); b < Math.Min(bars.Count, to); b++) { var f = feature(b); total ??= new double[f.Length]; if (f.Length == total.Length) for (int k = 0; k < f.Length; k++) total[k] += f[k]; }
            return Normalize(total ?? new double[1]);
        }
        double Mean(double[] values, int from, int to) { double sum = 0; int n = 0; for (int b = Math.Max(0, from); b < Math.Min(bars.Count, to); b++) { sum += values[b]; n++; } return n == 0 ? 0 : sum / n; }
        foreach (int half in new[] { Math.Max(1, unit / 2), unit })
        for (int b = 1; b < bars.Count; b++)
        {
            int lo = b - half, hi = b + half;
            double chroma = 1 - Cos(Sum(x => bars.Chroma[x], lo, b), Sum(x => bars.Chroma[x], b, hi));
            double tune = 1 - Cos(Sum(x => bars.MelodyPcs[x], lo, b), Sum(x => bars.MelodyPcs[x], b, hi));
            double rhythm = 1 - Cos(Sum(x => bars.Rhythm[x], lo, b), Sum(x => bars.Rhythm[x], b, hi));
            double energy = 1 - Ratio(Mean(bars.Energy, lo, b), Mean(bars.Energy, b, hi));
            double density = 1 - Ratio(Mean(bars.Density, lo, b), Mean(bars.Density, b, hi));
            double register = Math.Min(1, Math.Abs(Mean(bars.Register, lo, b) - Mean(bars.Register, b, hi)) / 7);
            novelty[b] += 1.2 * chroma + 1.2 * tune + .8 * rhythm + .8 * energy + .5 * density + .5 * register;
        }
        double max = novelty.Max();
        if (max > 0) for (int b = 0; b < novelty.Length; b++) novelty[b] /= max;
        // Keep only local peaks: a boundary sits where change is greatest, not on its flanks.
        var peaks = new double[novelty.Length];
        for (int b = 1; b < bars.Count; b++)
        {
            bool peak = true;
            for (int k = Math.Max(1, b - 1); k <= Math.Min(bars.Count - 1, b + 1); k++)
                if (novelty[k] > novelty[b] || novelty[k] == novelty[b] && k < b) { peak = false; break; }
            peaks[b] = peak ? novelty[b] : 0;
        }
        // Scale so a typical section change is 1; one dramatic change (a quiet outro)
        // must not flatten every other boundary.
        var nonzero = peaks.Where(p => p > 0).OrderBy(p => p).ToArray();
        double typical = nonzero.Length >= 3 ? nonzero[(int)(nonzero.Length * .75)] : nonzero.DefaultIfEmpty(1).Max();
        for (int b = 0; b < peaks.Length; b++) peaks[b] = Math.Min(1.5, peaks[b] / typical);
        return peaks;
    }

    public static bool Verbose;
    // Segmentation weights (chosen by PatternPrep --sweep against reviewed boundaries).
    public sealed class Tuning
    {
        public double SegmentCost = 2.2, NoveltyReward = .6, InnerThreshold = .35, InnerWeight = 5.0, OffGridPrior = 1.8, NearGridPrior = .3, ShortPrior = .6;
    }
    public static Tuning Weights = new();
    // ---------- Segmentation ----------
    // Everything the segmentation needs that does not depend on the weights.
    internal sealed class SegmentModel
    {
        public Bars Bars; public int N, Unit, MinLength, MaxLength;
        public double[] Novelty; public double[,] Repeat; public int[,] Period;
    }
    static SegmentModel Model(Bars bars, bool classical)
    {
        int n = bars.Count, unit = bars.Unit;
        var m = new SegmentModel { Bars = bars, N = n, Unit = unit, MinLength = Math.Min(n, Math.Max(2, unit / 2)), MaxLength = Math.Min(n, unit * 4) };
        m.Novelty = Novelty(bars, unit);
        // Repetition evidence for every candidate segment: its best match anywhere else.
        // Classical variations keep their harmony while the melody is ornamented.
        m.Repeat = new double[n, m.MaxLength + 1]; m.Period = new int[n, m.MaxLength + 1];
        for (int i = 0; i < n; i++)
            for (int length = 1; length <= m.MaxLength && i + length <= n; length++)
            {
                double match = 0;
                for (int j = 0; j + length <= n; j++)
                {
                    // An immediately adjacent copy is a loop inside one section, not a repeat of it.
                    if (Math.Abs(j - i) <= length) continue;
                    match = Math.Max(match, BestMatch(bars, i, j, length).score);
                    if (classical) match = Math.Max(match, BestMatch(bars, i, j, length, true).score - .08);
                }
                m.Repeat[i, length] = match;
                // Smallest period after which the segment repeats itself (a loop played again).
                m.Period[i, length] = length;
                foreach (int p in new[] { 1, 2, 3, 4, 6, 8 })
                {
                    if (p * 2 > length) break;
                    double sum = 0; for (int k = i; k < i + length - p; k++) sum += bars.Sim[0][k, k + p];
                    if (sum / (length - p) >= .85) { m.Period[i, length] = p; break; }
                }
            }
        return m;
    }
    static double Cost(SegmentModel m, Tuning w, int i, int length)
    {
        int unit = m.Unit;
        double prior = length == unit || length == unit * 2 ? 0
            : length * 2 == unit ? w.ShortPrior
            : length == unit * 4 || length == unit * 3 || length * 2 == unit * 3 ? w.NearGridPrior
            : w.OffGridPrior + .15 * Math.Min(Math.Abs(length - unit), Math.Abs(length - unit * 2));
        double explained = Math.Clamp((m.Repeat[i, length] - .55) / .30, 0, 1);
        // Content that is not a repeat of elsewhere costs its bars, but a loop inside the
        // segment only needs describing once.
        double content = Math.Min(length * (1 - explained), m.Period[i, length]);
        double cost = content + w.SegmentCost + prior;
        if (i > 0) cost -= w.NoveltyReward * Math.Min(1, m.Novelty[i]);
        // Every clear change inside the segment is a boundary it failed to mark.
        for (int k = i + 1; k < i + length; k++) if (m.Novelty[k] > w.InnerThreshold) cost += w.InnerWeight * (Math.Min(1, m.Novelty[k]) - w.InnerThreshold);
        return cost;
    }
    static List<(int start, int length)> Solve(SegmentModel m, Tuning w)
    {
        int n = m.N;
        if (n <= 2) return new() { (0, n) };
        var best = Enumerable.Repeat(double.PositiveInfinity, n + 1).ToArray(); var from = new int[n + 1];
        best[0] = 0;
        for (int end = 1; end <= n; end++)
            for (int length = 1; length <= Math.Min(m.MaxLength, end); length++)
            {
                int start = end - length;
                bool edge = start == 0 || end == n;
                if (length < m.MinLength && !edge) continue;
                double value = best[start] + Cost(m, w, start, length);
                if (value < best[end]) { best[end] = value; from[end] = start; }
            }
        var segments = new List<(int, int)>();
        for (int end = n; end > 0; end = from[end]) segments.Add((from[end], end - from[end]));
        segments.Reverse();
        return segments;
    }
    static List<(int start, int length)> Segment(Bars bars, bool classical)
    {
        var m = Model(bars, classical);
        var segments = Solve(m, Weights);
        if (Verbose)
        {
            Console.WriteLine($"bars={m.N} unit={m.Unit} min={m.MinLength} max={m.MaxLength}");
            for (int b = 0; b < m.N; b++)
                Console.WriteLine($"  bar {b + 1,3} nov {m.Novelty[b]:0.00} E {bars.Energy[b]:0.00} D {bars.Density[b]:0.0} R {bars.Register[b]:0} | {string.Join(" ", bars.States[b].Select(x => x < 0 ? "-" : HarmonyModel.Name(ChordTimeline.Root(x), true) + ChordTimeline.Quality(x)))} | {string.Join(",", bars.Melody[b].Select(x => x < 0 ? "." : HarmonyModel.Name(x - 21, true)))}");
            foreach (var (a, l) in segments) Console.WriteLine($"  seg {a + 1}+{l} repeat={m.Repeat[a, l]:0.00} cost={Cost(m, Weights, a, l):0.00}");
        }
        return segments;
    }

    // Boundary search for parameter sweeps: one precomputed model, many weightings.
    public sealed class Prepared { internal SegmentModel Model; }
    public static Prepared Prepare(MidiCycleAnalysis cycles, int key, bool minor, bool classical) =>
        new() { Model = Model(Fingerprint(cycles, ChordTimeline.Build(cycles, key, minor)), classical) };
    public static List<int> Boundaries(Prepared prepared, Tuning w) => Solve(prepared.Model, w).Select(x => x.start + 1).ToList();

    // Best alignment of two segments of possibly different lengths: slide the shorter one
    // inside the longer (a verse with a two-bar riff in front still matches the verse).
    static (double score, int t, double harmonic) Align(Bars bars, (int start, int length) a, (int start, int length) b)
    {
        var (shortSeg, longSeg) = a.length <= b.length ? (a, b) : (b, a);
        if (shortSeg.length < .6 * longSeg.length) return (0, 0, 0);
        double best = double.NegativeInfinity, bestHarm = 0; int bestT = 0;
        for (int offset = 0; offset + shortSeg.length <= longSeg.length; offset++)
        {
            var m = BestMatch(bars, shortSeg.start, longSeg.start + offset, shortSeg.length);
            var h = BestMatch(bars, shortSeg.start, longSeg.start + offset, shortSeg.length, true);
            double coverage = shortSeg.length / (double)longSeg.length;
            double score = m.score - .15 * (1 - coverage);
            if (score > best) { best = score; bestT = shortSeg.start == a.start ? m.t : HarmonyModel.Mod(-m.t); bestHarm = h.score - .15 * (1 - coverage); }
        }
        return (best, bestT, bestHarm);
    }

    // Link segments that repeat (possibly transposed, lengthened or ornamented) into families.
    static (int[] family, int[] transpose, double[] similarity) Families(Bars bars, List<(int start, int length)> segments, bool classical)
    {
        int n = segments.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var match = new (double score, int t, double harmonic)[n, n];
        for (int a = 0; a < n; a++) for (int b = 0; b < n; b++) match[a, b] = a == b ? (1, 0, 1) : Align(bars, segments[a], segments[b]);
        for (int a = 0; a < n; a++) for (int b = a + 1; b < n; b++)
        {
            var m = match[a, b];
            if (m.score >= .78 || classical && m.harmonic >= .86 && m.score >= .55) parent[Find(b)] = Find(a);
        }
        var roots = new Dictionary<int, int>(); var family = new int[n]; var transpose = new int[n]; var similarity = new double[n];
        for (int s = 0; s < n; s++)
        {
            int root = Find(s);
            if (!roots.TryGetValue(root, out int id)) { id = roots.Count; roots[root] = id; }
            family[s] = id;
        }
        for (int s = 0; s < n; s++)
        {
            int first = Array.IndexOf(family, family[s]);
            transpose[s] = s == first ? 0 : HarmonyModel.Mod(match[first, s].t);
            similarity[s] = s == first ? 1 : Math.Clamp(match[first, s].score, 0, 1);
        }
        return (family, transpose, similarity);
    }

    // ---------- Style, roles and form ----------
    static readonly string[] PopWords = { "vocal", "voice", "vox", "guitar", "gtr", "bass", "drum", "synth", "lead", "organ", "beat" };
    public static string Style(MidiCycleAnalysis cycles, string[] trackNames, string setting)
    {
        setting = (setting ?? "").Trim().ToLowerInvariant();
        if (setting is "pop" or "classical") return setting;
        if (cycles.Notes.Count(n => n.Channel == 10) > cycles.Measures.Count) return "pop";
        if (trackNames.Any(t => PopWords.Any(w => (t ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)))) return "pop";
        return "classical";
    }

    static string[] PopRoles(List<(int start, int length)> segments, int[] family, Bars bars)
    {
        int n = segments.Count; var roles = new string[n];
        var count = family.GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count());
        double Energy(int s) { var (a, l) = segments[s]; return Enumerable.Range(a, l).Average(b => bars.Energy[b] + .02 * bars.Register[b]); }
        var familyEnergy = family.Distinct().ToDictionary(f => f, f => Enumerable.Range(0, n).Where(s => family[s] == f).Average(Energy));
        var repeated = count.Where(p => p.Value >= 2).Select(p => p.Key).ToList();
        int chorus = -1, verse = -1;
        if (repeated.Count >= 2)
        {
            double Score(int f)
            {
                var visits = Enumerable.Range(0, n).Where(s => family[s] == f).ToArray();
                double late = visits.Count(s => s >= n * 2 / 3.0) > 0 ? .5 : 0;
                double follows = visits.Count(s => s > 0 && family[s - 1] != f && count[family[s - 1]] >= 2);
                double mean = familyEnergy.Values.Average(), spread = Math.Max(1e-6, familyEnergy.Values.Max() - familyEnergy.Values.Min());
                // Choruses carry the energy, come back most often and are heard early; a bridge
                // that first appears late in the song is never the chorus.
                return (familyEnergy[f] - mean) / spread * 1.5 + .6 * visits.Length + late + .4 * follows - (visits[0] == 0 ? .8 : 0) - (visits[0] > n * .4 ? 1.5 : 0);
            }
            chorus = repeated.OrderByDescending(Score).First();
            // The verse is the repeated family that most often leads into the chorus.
            verse = repeated.Where(f => f != chorus).OrderByDescending(f => Enumerable.Range(0, n - 1).Count(s => family[s] == f && (family[s + 1] == chorus || s + 2 < n && family[s + 2] == chorus)))
                .ThenBy(f => Array.IndexOf(family, f)).First();
            if (Array.IndexOf(family, chorus) < Array.IndexOf(family, verse) && familyEnergy[chorus] < familyEnergy[verse]) (chorus, verse) = (verse, chorus);
        }
        else if (repeated.Count == 1)
        {
            // Strophic songs repeat one verse; a single refrain between unique parts is a chorus.
            int f = repeated[0]; double share = Enumerable.Range(0, n).Where(s => family[s] == f).Sum(s => segments[s].length) / (double)bars.Count;
            if (share >= .55) verse = f; else chorus = f;
        }
        int preChorus = -1, postChorus = -1;
        foreach (int f in repeated.Where(f => f != chorus && f != verse))
        {
            int before = Enumerable.Range(1, Math.Max(0, n - 2)).Count(s => family[s] == f && family[s + 1] == chorus && family[s - 1] == verse);
            int after = Enumerable.Range(1, Math.Max(0, n - 1)).Count(s => family[s] == f && family[s - 1] == chorus);
            if (before >= 1 && preChorus < 0) preChorus = f;
            else if (after >= 2 && postChorus < 0) postChorus = f;
        }
        var extra = new Dictionary<int, string>();
        int bridgeCount = 0;
        for (int s = 0; s < n; s++)
        {
            int f = family[s];
            if (f == chorus) roles[s] = "Chorus";
            else if (f == verse) roles[s] = "Verse";
            else if (f == preChorus) roles[s] = "Pre-Chorus";
            else if (f == postChorus) roles[s] = "Post-Chorus";
            else if (count[f] == 1 && s == 0) roles[s] = "Intro";
            else if (count[f] == 1 && s == n - 1) roles[s] = "Outro";
            else if (count[f] == 1 && s > n / 3 && bridgeCount == 0 && (chorus < 0 || Array.IndexOf(family, chorus) < s)) { roles[s] = "Bridge"; bridgeCount++; }
            else if (count[f] == 1) roles[s] = "Interlude";
            else
            {
                if (!extra.TryGetValue(f, out var name)) { name = extra.Count == 0 ? "Refrain" : "Part " + (char)('C' + extra.Count); extra[f] = name; }
                roles[s] = name;
            }
        }
        // Several unique sections at the very end form one outro.
        for (int s = n - 1; s > 0 && count[family[s]] == 1 && roles[s] is "Interlude" or "Outro" && roles[s - 1] is "Interlude"; s--) roles[s - 1] = "Outro";
        return roles;
    }

    static string Letters(int n) => n < 26 ? ((char)('A' + n)).ToString() : "A" + Letters(n - 26);
    static string Primes(int n) => n == 0 ? "" : n == 1 ? "′" : n == 2 ? "″" : n == 3 ? "‴" : "⁽" + n + "⁾";

    static string FormFromLetters(IReadOnlyList<string> parts, string style, IReadOnlyList<string> roles)
    {
        var core = parts.Where((p, i) => roles[i] is not ("Intro" or "Outro" or "Coda")).Select(p => p.TrimEnd('′', '″', '‴')).ToList();
        string sequence = string.Join(" ", parts);
        if (style == "pop")
        {
            bool verse = roles.Contains("Verse"), chorus = roles.Contains("Chorus"), bridge = roles.Contains("Bridge");
            if (verse && chorus) return bridge ? "Verse–chorus with bridge" : "Verse–chorus";
            if (core.Count == 4 && core[0] == core[1] && core[1] == core[3] && core[2] != core[0]) return "AABA (32-bar song form)";
            if (verse) return "Strophic (repeated verse)";
            if (chorus) return "Refrain form";
            return "Through-composed";
        }
        // Map the letters onto X, Y, Z in order of appearance and compare with common forms.
        var names = new Dictionary<string, char>();
        string shape = new(core.Select(l => names.TryGetValue(l, out var c) ? c : names[l] = (char)('X' + names.Count)).ToArray());
        string collapsed = new(shape.Where((c, i) => i == 0 || shape[i - 1] != c).ToArray());
        string form = shape switch
        {
            "X" => "Single theme",
            "XY" => "Binary",
            "XXYY" => "Binary (AABB)",
            "XYXY" => "Repeated binary (ABAB)",
            "XXY" => "Bar form (AAB)",
            "XYX" => "Ternary (ABA)",
            "XXYX" => "AABA song form",
            "XYXZX" or "XYXYX" or "XYXZXYX" => "Rondo",
            _ when shape.Length >= 3 && shape.All(c => c == 'X') => "Theme and variations",
            _ when collapsed == "XYX" => "Ternary (ABA)",
            _ when collapsed.Length >= 5 && collapsed.Where((c, i) => i % 2 == 0).All(c => c == 'X') => "Rondo",
            _ when collapsed.Length >= 3 && collapsed[0] == collapsed[^1] => "Arch / rounded form",
            _ => "Through-composed"
        };
        return form + " · " + sequence;
    }

    // Tonic/mode that best explains a stretch of music (Krumhansl–Kessler profiles, bass-weighted).
    static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
    static (int key, bool minor) KeyArea(Bars bars, int start, int length)
    {
        var chroma = new double[12];
        for (int b = start; b < start + length; b++) for (int pc = 0; pc < 12; pc++) chroma[pc] += bars.Chroma[b][pc] + .6 * bars.BassChroma[b][pc];
        double best = double.NegativeInfinity; (int, bool) result = (0, false);
        for (int k = 0; k < 12; k++) foreach (bool minor in new[] { false, true })
        {
            var profile = minor ? MinorProfile : MajorProfile; double mean = profile.Average(), c = chroma.Average();
            double num = 0, da = 0, db = 0;
            for (int pc = 0; pc < 12; pc++) { double x = chroma[HarmonyModel.Mod(pc + k)] - c, y = profile[pc] - mean; num += x * y; da += x * x; db += y * y; }
            double r = num / Math.Sqrt(Math.Max(1e-12, da * db));
            if (r > best) { best = r; result = (k, minor); }
        }
        return result;
    }

    // ---------- Progression loops ----------
    static int LoopBars(Bars bars, int start, int length)
    {
        foreach (int period in new[] { 1, 2, 3, 4, 6, 8 })
        {
            if (period * 2 > length) break;
            bool uniform = Enumerable.Range(start, length).All(b => Math.Abs(bars.Length[b] - bars.Length[start + (b - start) % period]) < 1e-6);
            if (!uniform) continue;
            int agree = 0, total = 0; double chroma = 0;
            for (int b = start + period; b < start + length; b++)
            {
                int a = start + (b - start) % period;
                var x = bars.States[b]; var y = bars.States[a];
                for (int k = 0; k < Math.Min(x.Length, y.Length); k++) { total++; if (x[k] == y[k]) agree++; }
                chroma += Cos(bars.Chroma[a], bars.Chroma[b]);
            }
            chroma /= Math.Max(1, length - period);
            if (total > 0 && agree / (double)total >= .8 && chroma >= .85) return period;
        }
        return length;
    }

    // ---------- Entry point ----------
    public static Result Build(MidiCycleAnalysis cycles, SongSettings settings, string[] trackNames)
    {
        var result = new Result();
        var form = result.Form; form.EndBeat = cycles.EndBeat;
        if (cycles.Measures.Count == 0) return result;
        bool known = settings.Key >= 0;
        var grid = ChordTimeline.Build(cycles, settings.Key, settings.Minor); result.Grid = grid;
        form.Timeline.AddRange(ChordTimeline.Steps(grid));
        var bars = Fingerprint(cycles, grid);
        result.Style = Style(cycles, trackNames, settings.Style);

        // Boundaries: reviewed text, then MIDI markers, then repetition-driven segmentation.
        var segments = new List<(int start, int length)>(); var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.SectionBoundaries) || cycles.Markers.Any(m => m.Beat < cycles.EndBeat))
        {
            var starts = new List<(int bar, string label)>();
            if (!string.IsNullOrWhiteSpace(settings.SectionBoundaries))
            {
                foreach (string raw in settings.SectionBoundaries.Split('\n'))
                {
                    string line = raw.Trim(); if (line.Length == 0) continue;
                    int split = line.IndexOfAny(new[] { ' ', '\t' });
                    if (split < 1 || !int.TryParse(line[..split], out int bar) || bar < 1 || bar > cycles.Measures.Count || string.IsNullOrWhiteSpace(line[split..]))
                        throw new ArgumentException("Use one boundary per line: 1 Intro, 5 Verse, 13 Chorus. Bars are 1-based.");
                    if (starts.Count > 0 && bar - 1 <= starts[^1].bar) throw new ArgumentException("Section boundaries must increase, without duplicate bars.");
                    starts.Add((bar - 1, line[split..].Trim()));
                }
                if (starts.Count == 0 || starts[0].bar != 0) throw new ArgumentException("The first section must start at bar 1.");
                form.BoundarySource = "Your section boundaries";
            }
            else
            {
                foreach (var g in cycles.Markers.Where(m => m.Beat < cycles.EndBeat).GroupBy(m => cycles.Measures.FindIndex(b => b.End > m.Beat + 1e-6)).OrderBy(g => g.Key))
                    if (g.Key >= 0 && (starts.Count == 0 || g.Key > starts[^1].bar)) starts.Add((g.Key, g.Last().Label));
                if (starts[0].bar > 0) starts.Insert(0, (0, "Intro"));
                form.BoundarySource = "MIDI section markers";
            }
            for (int i = 0; i < starts.Count; i++)
            {
                int end = i + 1 < starts.Count ? starts[i + 1].bar : cycles.Measures.Count;
                segments.Add((starts[i].bar, end - starts[i].bar)); labels.Add(starts[i].label);
            }
        }
        else
        {
            segments = Segment(bars, result.Style == "classical");
            form.BoundarySource = "Repetition-based sections (review in Song Workshop)";
        }

        int[] family; int[] transpose; double[] similarity;
        if (labels.Count > 0)
        {
            var names = labels.Select(l => l.Trim()).ToList();
            var ids = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            family = names.Select(l => ids.FindIndex(x => string.Equals(x, l, StringComparison.OrdinalIgnoreCase))).ToArray();
            transpose = new int[segments.Count]; similarity = Enumerable.Repeat(1.0, segments.Count).ToArray();
            for (int s = 0; s < segments.Count; s++)
            {
                int first = Array.IndexOf(family, family[s]);
                if (first == s || segments[first].length != segments[s].length) continue;
                var m = BestMatch(bars, segments[first].start, segments[s].start, segments[s].length); transpose[s] = m.t; similarity[s] = Math.Clamp(m.score, 0, 1);
            }
        }
        else (family, transpose, similarity) = Families(bars, segments, result.Style == "classical");

        // Names: reviewed labels, pop roles or classical letters.
        int count = segments.Count;
        var roles = new string[count]; var letters = new string[count];
        var letterOf = new Dictionary<int, string>();
        foreach (int f in family) if (!letterOf.ContainsKey(f)) letterOf[f] = Letters(letterOf.Count);
        if (labels.Count > 0) for (int s = 0; s < count; s++) roles[s] = labels[s];
        else if (result.Style == "pop") roles = PopRoles(segments, family, bars);
        else
        {
            var familyCount = family.GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count());
            double median = segments.Select(s => (double)s.length).OrderBy(x => x).ElementAt(count / 2);
            for (int s = 0; s < count; s++)
                roles[s] = familyCount[family[s]] == 1 && s == 0 && segments[s].length < median && count > 2 ? "Intro"
                    : familyCount[family[s]] == 1 && s == count - 1 && segments[s].length <= median && count > 2 ? "Coda" : "Theme";
            // Letters skip introductions and codas so the form reads A B A.
            letterOf.Clear();
            for (int s = 0; s < count; s++) if (roles[s] == "Theme" && !letterOf.ContainsKey(family[s])) letterOf[family[s]] = Letters(letterOf.Count);
        }
        var variantIndex = new Dictionary<int, List<(int transpose, bool exact)>>();
        var visits = new Dictionary<int, int>();
        for (int s = 0; s < count; s++)
        {
            var (start, length) = segments[s];
            int f = family[s];
            visits[f] = visits.GetValueOrDefault(f) + 1;
            var info = new SectionInfo { Visit = visits[f], Transpose = transpose[s], Similarity = similarity[s], Role = roles[s] };
            string letter = letterOf.GetValueOrDefault(f, "");
            if (result.Style == "classical" && labels.Count == 0 && roles[s] == "Theme")
            {
                // A′ marks a varied or transposed return; identical returns keep the plain letter.
                if (!variantIndex.TryGetValue(f, out var seen)) variantIndex[f] = seen = new();
                bool exact = transpose[s] == 0 && similarity[s] >= .9;
                int prime = 0;
                if (seen.Count > 0 && !exact) { int k = seen.FindIndex(v => v.transpose == transpose[s] && !v.exact); prime = k >= 0 ? k + 1 : seen.Count(v => !v.exact) + 1; }
                seen.Add((transpose[s], exact || seen.Count == 0));
                info.Letter = letter + Primes(prime); info.Label = info.Letter;
            }
            else
            {
                info.Letter = letter;
                int total = family.Count(x => x == f);
                info.Label = total > 1 && roles[s] is not ("Intro" or "Outro" or "Coda") ? $"{roles[s]} {visits[f]}" : roles[s];
            }
            int loop = LoopBars(bars, start, length);
            double loopBeats = Enumerable.Range(start, loop).Sum(b => bars.Length[b]);
            double beatsPerBar = bars.Length[start];
            info.PhraseBars = beatsPerBar >= 6 ? 2 : 4;
            info.Loops = Math.Max(1, length / loop);
            // The inner dial turns once per loop, or once per phrase for long through-composed loops.
            info.CycleBeats = loopBeats <= 32 + 1e-6 ? loopBeats : Enumerable.Range(start, Math.Min(length, info.PhraseBars)).Sum(b => bars.Length[b]);
            var key = KeyArea(bars, start, length); info.KeyRoot = key.key; info.KeyMinor = key.minor;
            result.Sections.Add(info);

            double sectionStart = cycles.Measures[start].Start, sectionEnd = cycles.Measures[start + length - 1].End;
            if (s == count - 1) sectionEnd = Math.Max(sectionEnd, cycles.EndBeat);
            var section = new SongFormAnalysis.Section { Start = sectionStart, End = sectionEnd, FirstBar = start, BarCount = length, ProgressionBeats = loopBeats };
            foreach (var c in form.Timeline.Where(c => c.End > sectionStart && c.Start < sectionStart + loopBeats))
                section.Chords.Add(new SongFormAnalysis.ChordStep { Start = Math.Max(sectionStart, c.Start), End = Math.Min(sectionStart + loopBeats, c.End), Root = c.Root, Quality = c.Quality, Energy = c.Energy });
            var fam = form.Families.FirstOrDefault(x => x.Id == f);
            if (fam == null)
            {
                string name = labels.Count > 0 ? labels[s] : result.Style == "classical" && roles[s] == "Theme" ? "Theme " + letter : roles[s];
                fam = new SongFormAnalysis.Family { Id = f, Name = name }; form.Families.Add(fam);
            }
            section.Family = fam; fam.Occurrences.Add(section); form.Sections.Add(section);
        }
        form.Families.Sort((a, b) => a.Id.CompareTo(b.Id));

        // Larger parts: key areas and returns of the opening material (classical), or
        // repeated section groups such as Verse + Chorus (pop, via SectionCompression).
        if (result.Style == "classical" && labels.Count == 0 && string.IsNullOrWhiteSpace(settings.SectionBoundaries))
            AssignClassicalParts(result, family, bars, segments);
        var partLabels = result.Style == "classical" ? ClassicalPartLetters(result) : result.Sections.Select(s => s.Letter).ToList();
        result.FormName = FormFromLetters(partLabels, result.Style, result.Style == "classical" ? ClassicalPartRoles(result) : result.Sections.Select(s => s.Role).ToList());
        if (result.Style == "pop") result.FormName += " · " + string.Join(" ", result.Sections.Select(s => Abbreviation(s.Role)));
        return result;
    }

    public static string Abbreviation(string role) => role switch
    {
        "Intro" => "In", "Verse" => "V", "Pre-Chorus" => "PC", "Chorus" => "C", "Post-Chorus" => "PoC",
        "Bridge" => "Br", "Interlude" => "It", "Outro" => "Out", "Refrain" => "R", "Coda" => "Co",
        _ => role.Length <= 3 ? role : role[..1]
    };

    // Larger classical parts. Sections are grouped into key areas (tonic, ignoring mode),
    // and brief excursions are absorbed by their surroundings, so B♭ minor → D♭ major →
    // B♭ minor reads as three parts even when the middle wanders through A♭ or D.
    // A later part in the same key area whose harmony was heard before is a return (A′).
    // A piece that never leaves its key is divided at returns of its opening material.
    static void AssignClassicalParts(Result result, int[] family, Bars bars, List<(int start, int length)> segments)
    {
        var sections = result.Sections; int n = sections.Count;
        if (n < 3) return;
        int first = Enumerable.Range(0, n).FirstOrDefault(s => sections[s].Role == "Theme");
        var parts = new List<List<int>>();
        var themes = Enumerable.Range(0, n).Where(s => sections[s].Role == "Theme").ToList();
        (int, bool) Area(int s) => (sections[s].KeyRoot, sections[s].KeyMinor);
        if (themes.Count == 0) return;
        var runs = new List<List<int>>();
        foreach (int s in themes)
        {
            if (runs.Count == 0 || Area(runs[^1][^1]) != Area(s)) runs.Add(new());
            runs[^1].Add(s);
        }
        int Bars(List<int> run) => run.Sum(s => segments[s].length);
        double minPart = Math.Max(bars.Unit, bars.Count / 8.0);
        while (runs.Count > 1)
        {
            int shortest = Enumerable.Range(0, runs.Count).OrderBy(r => Bars(runs[r])).First();
            if (Bars(runs[shortest]) >= minPart) break;
            int into;
            if (shortest == 0) into = 1;
            else if (shortest == runs.Count - 1) into = shortest - 1;
            // An excursion between two runs in the same key closes up; otherwise it continues the previous part.
            else into = shortest - 1;
            bool sandwich = shortest > 0 && shortest < runs.Count - 1 && Area(runs[shortest - 1][0]) == Area(runs[shortest + 1][0]);
            if (sandwich) { runs[shortest - 1].AddRange(runs[shortest]); runs[shortest - 1].AddRange(runs[shortest + 1]); runs.RemoveRange(shortest, 2); continue; }
            if (into < shortest) runs[into].AddRange(runs[shortest]); else runs[into].InsertRange(0, runs[shortest]);
            runs.RemoveAt(shortest);
            // Neighbours that now share a key merge too.
            for (int r = runs.Count - 1; r > 0; r--)
                if (Area(runs[r][0]) == Area(runs[r - 1][0])) { runs[r - 1].AddRange(runs[r]); runs.RemoveAt(r); }
        }
        if (runs.Count > 1)
        {
            foreach (var run in runs) run.Sort();
            if (first > 0) parts.Add(Enumerable.Range(0, first).ToList());
            parts.AddRange(runs);
            for (int s = themes[^1] + 1; s < n; s++) parts.Add(new() { s });
            // Letters: a part in a key area heard before, whose harmony mostly appeared there, returns.
            double Coverage(List<int> later, List<int> earlier)
            {
                var earlierBars = earlier.SelectMany(s => Enumerable.Range(segments[s].start, segments[s].length)).ToArray();
                var laterBars = later.SelectMany(s => Enumerable.Range(segments[s].start, segments[s].length)).ToArray();
                return laterBars.Average(b => earlierBars.Max(c => bars.Harm[0][b, c]));
            }
            var partLetters = new List<string>(); var partOrigin = new List<int>(); var partPrimes = new Dictionary<int, int>(); int partCount = 0;
            for (int p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                if (sections[part[0]].Role != "Theme") { partLetters.Add(sections[part[0]].Role); partOrigin.Add(-1); continue; }
                int match = -1; double coverage = 0;
                for (int q = 0; q < p; q++)
                {
                    if (partOrigin[q] != q) continue;
                    // Same material: most of this part's families were heard there, or (in the same
                    // key area) most of its harmony was.
                    var mine = part.Select(x => family[x]).ToHashSet(); var theirs = parts[q].Select(x => family[x]).ToHashSet();
                    double shared = mine.Count(theirs.Contains) / (double)Math.Min(mine.Count, theirs.Count);
                    double c = Math.Max(shared >= .5 ? .7 + .3 * shared : 0, Area(parts[q][0]) == Area(part[0]) ? Coverage(part, parts[q]) : 0);
                    if (Verbose) Console.WriteLine($"  part {p} vs {q}: shared {shared:0.00} coverage {c:0.00}");
                    if (c >= .6 && c > coverage) { coverage = c; match = q; }
                }
                if (match < 0) { partLetters.Add(Letters(partCount++)); partOrigin.Add(p); continue; }
                bool exact = coverage >= .95 && Bars(part) == Bars(parts[match]);
                if (!exact) partPrimes[match] = partPrimes.GetValueOrDefault(match) + 1;
                partLetters.Add(partLetters[match] + (exact ? "" : Primes(partPrimes[match])));
                partOrigin.Add(match);
            }
            for (int p = 0; p < parts.Count; p++)
                foreach (int s in parts[p]) { sections[s].Part = p; sections[s].Parent = partLetters[p] is "Intro" or "Coda" ? "Song" : "Song/Part " + partLetters[p]; }
            return;
        }
        (int, bool) Key(int s) => (sections[s].KeyRoot, sections[s].KeyMinor);
        bool InKey(int s, (int, bool) key) => Key(s) == key || s + 1 < n && s > 0 && Key(s - 1) == key && Key(s + 1) == key;
        var home = Key(first);
        int openingEnd = first + 1;
        while (openingEnd < n && sections[openingEnd].Role == "Theme" && InKey(openingEnd, home) && family[openingEnd] != family[first]) openingEnd++;
        while (openingEnd < n && sections[openingEnd].Role == "Theme" && family[openingEnd] == family[first]) openingEnd++;
        var openingFamilies = Enumerable.Range(first, openingEnd - first).Select(s => family[s]).ToHashSet();
        if (first > 0) parts.Add(Enumerable.Range(0, first).ToList());
        parts.Add(Enumerable.Range(first, openingEnd - first).ToList());
        // A return is any later re-entry of opening material (the reprise may begin mid-theme).
        var returns = Enumerable.Range(openingEnd + 1, Math.Max(0, n - openingEnd - 1)).Where(s => openingFamilies.Contains(family[s]) && !openingFamilies.Contains(family[s - 1])).ToList();
        if (returns.Count == 0) return;
        {
            int s = openingEnd;
            while (s < n)
            {
                if (sections[s].Role == "Coda") { parts.Add(new() { s }); s++; continue; }
                var part = new List<int>();
                if (returns.Contains(s))
                    while (s < n && sections[s].Role == "Theme" && (openingFamilies.Contains(family[s]) || InKey(s, home)) && (part.Count == 0 || !returns.Contains(s))) part.Add(s++);
                else
                    while (s < n && !returns.Contains(s) && sections[s].Role == "Theme") part.Add(s++);
                if (part.Count == 0) part.Add(s++);
                parts.Add(part);
            }
            int lastReturn = parts.FindLastIndex(p => returns.Contains(p[0]));
            while (lastReturn >= 0 && lastReturn + 1 < parts.Count && sections[parts[lastReturn + 1][0]].Role == "Theme"
                   && parts[lastReturn + 1].All(x => !returns.Contains(x)) && parts.Skip(lastReturn + 1).Where(p => sections[p[0]].Role == "Theme").Sum(p => p.Count) <= Math.Max(1, n / 8))
            { parts[lastReturn].AddRange(parts[lastReturn + 1]); parts.RemoveAt(lastReturn + 1); }
        }
        if (parts.Count < 2) return;
        var letters = new List<string>(); var sets = new List<HashSet<int>>(); var origin = new List<int>();
        var primes = new Dictionary<int, int>(); int distinct = 0;
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            var set = part.Where(s => sections[s].Role == "Theme").Select(s => family[s]).ToHashSet();
            sets.Add(set);
            if (set.Count == 0) { letters.Add(sections[part[0]].Role); origin.Add(-1); continue; }
            int match = Enumerable.Range(0, p).FirstOrDefault(k => origin[k] == k && sets[k].Intersect(set).Count() >= Math.Max(1, Math.Min(sets[k].Count, set.Count) * .5), -1);
            if (match < 0) { letters.Add(Letters(distinct++)); origin.Add(p); continue; }
            bool exact = set.SetEquals(sets[match]) && part.All(s => sections[s].Transpose == 0 && sections[s].Similarity >= .9);
            if (!exact) primes[match] = primes.GetValueOrDefault(match) + 1;
            letters.Add(letters[match] + (exact ? "" : Primes(primes[match])));
            origin.Add(match);
        }
        for (int p = 0; p < parts.Count; p++)
            foreach (int s in parts[p]) { sections[s].Part = p; sections[s].Parent = letters[p] is "Intro" or "Coda" ? "Song" : "Song/Part " + letters[p]; }
    }
    static List<string> ClassicalPartLetters(Result result)
    {
        var parts = new List<string>(); int last = int.MinValue;
        foreach (var s in result.Sections)
        {
            if (s.Part >= 0 && s.Part == last) continue;
            last = s.Part;
            parts.Add(s.Part >= 0 && s.Parent.StartsWith("Song/Part ") ? s.Parent["Song/Part ".Length..] : s.Role is "Intro" or "Coda" ? s.Role : s.Letter);
        }
        return parts;
    }
    static List<string> ClassicalPartRoles(Result result) => ClassicalPartLetters(result).Select(l => l is "Intro" or "Coda" ? l : "Theme").ToList();
}
