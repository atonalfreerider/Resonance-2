using System.Text.RegularExpressions;

// Stage E of the motif reduction (Docs/MOTIF-REDUCTION.md §7): returns and sections.
// - E1–E2: the soft, transposition-aware similarity S of every pair of columns (Iteration.Columns' sim_k), kept as S, its
//   argmax K and the transpositions within 0.05 of S; θ, the piece's own background, and gap costs tied to 1 − θ.
// - E3: discovery by Gotoh Smith–Waterman, one transposition per path, Waterman–Eggert masking, only at lags of the node's
//   own scale; path trimming and the structural test. Above BarColumnsAbove columns it runs on bars, refined in a band.
// - E4: the best family's prototype hull and its occurrences by anchored semi-global alignment, accepted as copies.
// - E5: the canonical edit script: anchored affine DP, gap sliding with its explicit tie-break, the script and the omitted
//   phrases. It needs the prototype's phrase edges: §8 F1's cut sources (arrivals and internal-return edges), built here.
// - E6: sections (families, complements, head, tail and coda) with the arrival, cell and direction snaps; reviewed and pop
//   forms keep data.Sections and relate unions of consecutive sections.
// - E7: internal returns inside each classical section, giving phrase-level relations and phrase edges.
// Everything is in score ticks and grid columns; nothing here writes the bundle (the hierarchy and export do).
public sealed class Returns
{
    const double Slack = .05, Transposed = .04, Same = .999, Eps = 1e-9, NegInf = double.NegativeInfinity;
    // Section kinds: a family's hull (A), its return (A′), a span between them (B, C, …), a long unique head, or a reviewed
    // or pop section kept verbatim.
    public const int PrototypeKind = 0, CopyKind = 1, ComplementKind = 2, IntroKind = 3, ReviewedKind = 4;
    static readonly int[] Order = { 0, -1, 1, -2, 2, -3, 3, -4, 4, -5, 5, -6, 6 };   // transpositions, the nearer first on a tie
    static readonly Regex Direction = new("a tempo|tempo i|più|meno|animato|mosso|bravura|legatissimo|coda|rit|rall|lento|allegro|andante",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ---------------------------------------------------------------------------------------------------------------
    // Types.

    // §7 E1 over units (tactus columns, or bars above BarColumnsAbove): for each pair a < b, S = max over k of mean sim_k −
    // 0.04·[k ≠ 0] (bars compare their last columns pairwise), its argmax K and the transpositions scoring within 0.05 of S,
    // the only ones a path may keep there (§7 E3). Seven bytes a pair, never the 13 sims (§11); S_k is computed on demand.
    public sealed class Matrix
    {
        public int Count;
        public int[] Start = Array.Empty<int>(), Length = Array.Empty<int>();   // per unit: its first column and its column count
        public double[] Beats = Array.Empty<double>();                          // per unit: its length in beats (gaps cost per beat)
        float[] s = Array.Empty<float>(); sbyte[] k = Array.Empty<sbyte>(); ushort[] allowed = Array.Empty<ushort>();
        long Index(int a, int b) => (long)a * (2L * Count - a - 1) / 2 + (b - a - 1);
        public double S(int a, int b) => s[Index(a, b)];
        public int K(int a, int b) => k[Index(a, b)];
        public bool Allows(int a, int b, int t) => (allowed[Index(a, b)] >> (t + 6) & 1) != 0;

        // Rows in parallel, each writing its own slots (§12.3). sample: the S of every compared column pair at least four
        // beats apart, in row order, for θ.
        public static Matrix Build(Iteration.Columns cols, MotifGrid grid, int[] start, int[] length, out float[] sample)
        {
            int n = start.Length;
            var m = new Matrix { Count = n, Start = start, Length = length, Beats = new double[n] };
            for (int a = 0; a < n; a++) m.Beats[a] = (grid.ColumnEnd[start[a] + length[a] - 1] - grid.ColumnStart[start[a]]) / (double)grid.Ppq;
            long pairs = (long)n * (n - 1) / 2;
            m.s = new float[pairs]; m.k = new sbyte[pairs]; m.allowed = new ushort[pairs];
            var rows = new float[n][]; long four = 4L * grid.Ppq;
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, a =>
            {
                Span<double> sims = stackalloc double[13], sum = stackalloc double[13];
                var row = new List<float>();
                for (int b = a + 1; b < n; b++)
                {
                    int w = Math.Min(length[a], length[b]); sum.Clear();
                    for (int t = 0; t < w; t++)
                    {
                        int u = start[a] + length[a] - w + t, v = start[b] + length[b] - w + t;
                        cols.Similarities(u, v, -6, 6, sims);
                        for (int x = 0; x < 13; x++) sum[x] += sims[x];
                        if (grid.ColumnStart[v] - grid.ColumnStart[u] >= four) row.Add((float)Best(sims, out _));
                    }
                    for (int x = 0; x < 13; x++) sum[x] /= w;
                    double best = Best(sum, out int at); long i = m.Index(a, b);
                    m.s[i] = (float)best; m.k[i] = (sbyte)at;
                    ushort mask = 0;
                    for (int t = -6; t <= 6; t++) if (sum[t + 6] - (t != 0 ? Transposed : 0) >= best - Slack - Eps) mask |= (ushort)(1 << (t + 6));
                    m.allowed[i] = mask;
                }
                rows[a] = row.ToArray();
            });
            int total = 0; foreach (var r in rows) total += r.Length;
            sample = new float[total]; total = 0;
            foreach (var r in rows) { r.CopyTo(sample, total); total += r.Length; }
            return m;
        }
    }
    // §7 E1's S over the 13 sim_k: the best of sim_k − 0.04·[k ≠ 0], the smaller |k| on a tie (as Iteration.Columns.Best).
    static double Best(ReadOnlySpan<double> sims, out int at)
    {
        double best = double.NegativeInfinity; at = 0;
        foreach (int t in Order) { double x = sims[t + 6] - (t != 0 ? Transposed : 0); if (x > best + Eps) { best = x; at = t; } }
        return Math.Max(0, best);
    }

    // A discovered local alignment (§7 E3): matched columns Rows[i] ~ Cols[i] (source before target), in order, at the
    // transposition K (semitones, target − source). Score: Σ (S − θ) less the gap costs, after trimming.
    public sealed class Path
    {
        public int K;
        public double Score;
        public bool Structural;
        public int[] Rows = Array.Empty<int>(), Cols = Array.Empty<int>();
        public int Length => Rows.Length;
    }

    // One edit of a return's script (§7 E5): Op is MotifEdits.Match, Delete, Insert or Substitute; spans in ticks, −1 when the
    // edit has no side there (a deletion has no target, an insertion no source).
    public struct Edit
    {
        public int Op;
        public long SourceStart, SourceEnd, TargetStart, TargetEnd;
    }

    // A return (§7 E4–E5, E7): the target span restates the source span under the transposition K with the canonical Script.
    // Level 1 relates sections (Section and SourceSection, or for reviewed forms the unions Section…LastSection and
    // SourceSection…LastSourceSection), level 2 spans inside one section (§7 E7). Rows ~ Cols: the matched columns after gap
    // sliding. TailStart: where the trailing insert begins (−1: none). Omitted: the prototype's phrases lying wholly inside
    // deletions (their labels, and the relation's sentence, are §8 F6's). CopyShare and PrototypeShare: how much of each side
    // the matched columns explain.
    public sealed class Return
    {
        public int Id, Level, K, Section = -1, LastSection = -1, SourceSection = -1, LastSourceSection = -1;
        public long Start, End, SourceStart, SourceEnd, TailStart = -1;
        public double Score, CopyShare, PrototypeShare;
        public bool Reviewed;
        public Edit[] Script = Array.Empty<Edit>();
        public int[] Rows = Array.Empty<int>(), Cols = Array.Empty<int>();
        public (long Start, long End)[] Omitted = Array.Empty<(long, long)>();
    }

    // A level-1 section (§7 E6), [Start, End) in ticks. Family: the prototype section of its family (itself for a hull, −1
    // for complements and intros); Prototype: the section it restates (−1 unless a copy); Return: that relation. Hull: the
    // prototype's aligned columns (−1 otherwise). CodaStart: where its Coda child begins (−1: none). Letter "A", Label "A′".
    public sealed class Section
    {
        public int Id, Kind, Family = -1, Prototype = -1, Return = -1, HullFirst = -1, HullLast = -1;
        public long Start, End, CodaStart = -1;
        public string Letter = "", Label = "";
    }

    // A phrase cut (§8 F1's sources): its tick, strength (full arrival 1.0, snapped return edge 0.9, half arrival 0.8, unsnapped
    // return edge 0.6, grid 0.3) and whether an arrival backs it (§7 E5's edge weight 1, else 0.5).
    public struct Cut
    {
        public long Tick;
        public double Strength;
        public bool Arrival;
    }

    // An alignment op: a source column matched to a target column, a source column deleted or a target column inserted.
    struct Op
    {
        public byte Kind;
        public int Row, Col;
        public Op(byte kind, int row, int col) { Kind = kind; Row = row; Col = col; }
    }
    const byte Match = 0, Delete = 1, Insert = 2;

    // An occurrence of a hull (§7 E4) and, after E5, its canonical alignment.
    sealed class Occurrence
    {
        public int K, First, Last;       // target columns, inclusive
        public double Score;
        public List<Op> Ops = new();
    }
    // A family (§7 E4): its hull (columns, inclusive) and the occurrences accepted as copies.
    sealed class Family
    {
        public int HullFirst, HullLast;
        public readonly List<Occurrence> Copies = new();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Results.

    public double Theta = double.NaN, GapOpen, GapExtend;
    public bool Reviewed, OnBars;                         // reviewed or pop form; discovery ran on bar columns
    public Iteration.Columns Cols;
    public Matrix Tactus;                                 // null on bars: S_k is then computed on demand
    public Path[] Paths = Array.Empty<Path>();            // the root's discovery, in the order found
    public Section[] Sections = Array.Empty<Section>();
    public Return[] All = Array.Empty<Return>();          // level 1, then level 2
    public Cut[][] Cuts = Array.Empty<Cut[]>();           // per section, by tick
    public long[] Directions = Array.Empty<long>();       // ticks of tempo and expression directions that mark sections (+0.3 evidence in §8's DPs)
    public long[] Boundaries => Sections.Skip(1).Select(s => s.Start).ToArray();
    public long[] ArrivalCuts => arrivalCuts;             // §8 F1's arrival cuts in time order (§8 F2 weighs them too) …
    public bool[] ArrivalFull => arrivalFull;             // … and whether each closes on the tonic
    public static Action<string> Trace;

    MotifInput input; MotifGrid grid; MotifSettings settings; CellLexicon lex;
    int first;                                            // the span's first column: the tactus matrix's unit 0
    long[] arrivalCuts = Array.Empty<long>(); bool[] arrivalFull = Array.Empty<bool>();
    readonly List<Return> returns = new();

    // data: its Sections and Style decide a reviewed or pop form (§7 E6); everything else comes from the input.
    public static Returns Build(MotifInput input, CellLexicon lex, PreparedPatternSong data)
    {
        var ret = new Returns { input = input, grid = input.Grid, settings = input.Settings, lex = lex };
        var g = ret.grid;
        ret.first = g.FirstColumn;
        ret.Cols = Iteration.Columns.Build(input);
        int columns = g.Columns - g.FirstColumn;
        ret.Reviewed = data != null && data.Sections != null && data.Sections.Length > 0 && (input.Settings.ReviewedSections || data.Style == "pop");
        if (columns < 2) { ret.Sections = new[] { new Section { Kind = ComplementKind, Start = g.SpanStart, End = g.SpanEnd, Letter = "A", Label = "A" } }; ret.Cuts = new[] { Array.Empty<Cut>() }; return ret; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        ret.OnBars = columns > input.Settings.BarColumnsAbove;
        float[] sample;
        if (!ret.OnBars)
        {
            var start = new int[columns]; var length = new int[columns];
            for (int a = 0; a < columns; a++) { start[a] = g.FirstColumn + a; length[a] = 1; }
            ret.Tactus = Matrix.Build(ret.Cols, g, start, length, out sample);
        }
        else
        {
            // θ from the column pairs the bar matrix compares (whole bars apart): a sample of every pair's S.
            var bars = ret.BarUnits(g.FirstColumn, g.Columns);
            Matrix.Build(ret.Cols, g, bars.start, bars.length, out sample);
        }
        Array.Sort(sample);
        double q = sample.Length == 0 ? 0 : sample[(int)Math.Floor(input.Settings.ThetaQuantile * (sample.Length - 1))];
        ret.Theta = Math.Clamp(q, input.Settings.ThetaMin, input.Settings.ThetaMax);
        ret.GapOpen = input.Settings.GapOpen * (1 - ret.Theta); ret.GapExtend = input.Settings.GapExtend * (1 - ret.Theta);
        Trace?.Invoke($"returns: similarity of {columns} columns and θ in {clock.ElapsedMilliseconds} ms");
        ret.Arrivals();
        ret.Directions = ret.MarkedDirections();
        if (ret.Reviewed) ret.ReviewedForm(data);
        else ret.ClassicalForm();
        // Level 1 first, each level in time order; sections keep pointing at their relation.
        ret.All = ret.returns.OrderBy(r => r.Level).ThenBy(r => r.Start).ThenBy(r => r.Id).ToArray();
        var remap = new int[ret.returns.Count];
        for (int i = 0; i < ret.All.Length; i++) remap[ret.All[i].Id] = i;
        foreach (var s in ret.Sections) if (s.Return >= 0) s.Return = remap[s.Return];
        foreach (var r in ret.All) r.Id = remap[r.Id];
        return ret;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers on columns, bars and arrivals.

    long Tick(int column) => grid.ColumnStart[column];
    long TickEnd(int column) => grid.ColumnEnd[column];
    double Beats(int column) => (grid.ColumnEnd[column] - grid.ColumnStart[column]) / (double)grid.Ppq;
    double Beats(int from, int to) { double b = 0; for (int c = from; c <= to; c++) b += Beats(c); return b; }
    int ColumnOf(long tick) => Math.Max(grid.FirstColumn, grid.ColumnAt(tick));
    long BarLength(long tick) { int b = Math.Max(0, grid.BarAt(tick)); return grid.BarEnd[b] - grid.BarStart[b]; }
    // The modal number of columns a bar holds in [c0, c1): "1 bar" of the scale rule.
    int BarColumns(int c0, int c1)
    {
        var counts = new SortedDictionary<int, int>();
        for (int c = c0; c < c1;) { int b = grid.ColumnBar[c], n = 0; while (c < c1 && grid.ColumnBar[c] == b) { c++; n++; } counts[n] = counts.GetValueOrDefault(n) + 1; }
        int best = 1, most = 0; foreach (var (n, k) in counts) if (k > most) { best = n; most = k; }
        return Math.Max(1, best);
    }
    (int[] start, int[] length) BarUnits(int c0, int c1)
    {
        var start = new List<int>(); var length = new List<int>();
        for (int c = c0; c < c1;) { int b = grid.ColumnBar[c], f = c; while (c < c1 && grid.ColumnBar[c] == b) c++; start.Add(f); length.Add(c - f); }
        return (start.ToArray(), length.ToArray());
    }
    // The first downbeat of [c0, …): the first bar start at or after c0 that is not an anacrusis (a bar shorter than the next).
    int FirstDownbeat(int c0, int c1)
    {
        for (int c = c0; c < c1; c++)
        {
            int b = grid.ColumnBar[c];
            if (grid.ColumnStart[c] != grid.BarStart[b]) continue;
            if (b + 1 < grid.Bars && grid.BarEnd[b] - grid.BarStart[b] < grid.BarEnd[b + 1] - grid.BarStart[b + 1]) continue;
            return c;
        }
        return c0;
    }

    // The match score of two columns (u before v) at a return's transposition k, before θ: §7 E2's S where k lies within §7
    // E3's 0.05 of it (S's own argmax or a near tie), as a discovered path scores; elsewhere S_k, which then lies below S − 0.05,
    // so where that is below θ it is the bound S − 0.05 (no match there either way) and is computed on demand only where it could
    // still reach θ. The anchored DPs score with it, so a return keeps one transposition as a discovered path does.
    double Sk(int u, int v, int k)
    {
        if (Tactus != null && u >= first && v > u)
        {
            int a = u - first, b = v - first; double s = Tactus.S(a, b);
            if (Tactus.Allows(a, b, k)) return s;
            if (s < Theta + Slack) return s - Slack;
        }
        return Cols.Similarity(u, v, k) - (k != 0 ? Transposed : 0);
    }
    // The transpositions worth aligning the source columns [h0, h1] at against the target [t0, t1] (both inclusive, source
    // first): those some pair of their columns allows (within 0.05 of S) with S ≥ θ, in Order; every one without the matrix.
    List<int> Candidates(int h0, int h1, int t0, int t1)
    {
        if (Tactus == null) return Order.ToList();
        var live = new bool[13];
        for (int u = h0; u <= h1; u++)
            for (int v = Math.Max(t0, u + 1); v <= t1; v++)
            {
                int a = u - first, b = v - first; if (Tactus.S(a, b) < Theta - Eps) continue;
                for (int t = -6; t <= 6; t++) if (Tactus.Allows(a, b, t)) live[t + 6] = true;
            }
        return Order.Where(t => live[t + 6]).ToList();
    }
    // Whether the target [t0, t1] could restate the source [h0, h1] as §7 E4 accepts a copy. Matches pair columns one to one,
    // so neither side can have more beats explained than the other side's columns hold at most; and a column is explained only
    // where some column of the other side matches it by θ at the alignment's transposition, which S bounds for every k. Either
    // bound rules the pair out before any alignment.
    bool CouldCopy(int h0, int h1, int t0, int t1)
    {
        double Most(int from, int to, int count) { var b = Enumerable.Range(from, to - from + 1).Select(c => Beats(c)).OrderByDescending(x => x).ToArray(); double sum = 0; for (int i = 0; i < Math.Min(count, b.Length); i++) sum += b[i]; return sum; }
        if (Most(t0, t1, h1 - h0 + 1) < settings.CopyShare * Beats(t0, t1) - Eps || Most(h0, h1, t1 - t0 + 1) < settings.PrototypeShare * Beats(h0, h1) - Eps) return false;
        if (Tactus == null) return true;
        double target = 0, source = 0; var rowHit = new bool[h1 - h0 + 1];
        for (int v = t0; v <= t1; v++)
        {
            bool hit = false;
            for (int u = h0; u <= h1 && u < v; u++) if (Tactus.S(u - first, v - first) >= Theta - Eps) { hit = true; rowHit[u - h0] = true; }
            if (hit) target += Beats(v);
        }
        for (int u = h0; u <= h1; u++) if (rowHit[u - h0]) source += Beats(u);
        return target >= settings.CopyShare * Beats(t0, t1) - Eps && source >= settings.PrototypeShare * Beats(h0, h1) - Eps;
    }
    // Whether column v restates column u exactly under k: the same notes at the same hands and slots, moved by k semitones.
    bool Exact(int u, int v, int k) { var p = Cols.Relate(u, v); return p.Real && p.K == k; }

    // The arrivals of the principal line (§4 B2) as cuts: the next tactus after the arrival note, where its phrase closes (§8
    // F1): the end of its column, or of the column its held note ends in (F25's whole-note cadences close their bars). A note
    // released less than half a slot into a column (a legato release a few ticks late) is not held there.
    void Arrivals()
    {
        int p = input.Streams.Principal;
        if (p < 0 || p >= lex.Lines.Length || lex.Lines[p] == null) return;
        var cuts = new List<long>(); var full = new List<bool>();
        foreach (var a in Diminution.Arrivals(input, lex.Lines[p]))
        {
            int c = grid.ColumnAt(a.Tick); if (c < 0) continue;
            int held = Math.Max(c, grid.ColumnAt(a.Tick + input.Events[a.Note].Duration - Math.Max(1, grid.SlotAt(a.Tick) / 2)));
            long cut = grid.ColumnEnd[held];
            if (cut <= grid.SpanStart || cut >= grid.SpanEnd || cuts.Count > 0 && cuts[^1] >= cut) continue;
            cuts.Add(cut); full.Add(a.Full);
        }
        arrivalCuts = cuts.ToArray(); arrivalFull = full.ToArray();
    }
    // Tempo and expression directions of a score import that mark sections (§7 E6's direction snap).
    long[] MarkedDirections()
    {
        if (input.Import == null) return Array.Empty<long>();
        var ticks = new SortedSet<long>();
        foreach (var d in input.Import.Directions)
            if (d.Kind is "tempo" or "expression" && Direction.IsMatch(d.Text) && d.Tick >= grid.SpanStart && d.Tick < grid.SpanEnd) ticks.Add(d.Tick);
        return ticks.ToArray();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §7 E3: discovery.

    // Up to DiscoveryPaths local alignments of the units [n0, n1) against themselves at lags ≥ minLag, each at one
    // transposition: a layer per k, where a diagonal step is allowed only where k scores within 0.05 of S and the cell is not
    // masked, and scores (S − θ)·its columns. Waterman–Eggert: each path found masks its cells with a ±1 band for every
    // later one. A layer is recomputed only when the band touches the best path it holds: until then that path is still its
    // best, with the same score, since masking only removes alignments.
    List<Path> Discover(Matrix m, int n0, int n1, int minLag)
    {
        int n = n1 - n0; var found = new List<Path>();
        if (n <= minLag) return found;
        double go = GapOpen, ge = GapExtend, theta = Theta;
        var masked = new bool[(long)n * n]; var trace = new byte[(long)n * n];
        var hPrev = new double[n]; var hCur = new double[n]; var f = new double[n];
        var live = new bool[13];
        for (int i = 0; i + minLag < n; i++)
            for (int j = i + minLag; j < n; j++)
            {
                int a = n0 + i, b = n0 + j; if (m.S(a, b) <= theta) continue;
                for (int t = -6; t <= 6; t++) if (m.Allows(a, b, t)) live[t + 6] = true;
            }
        (double score, int bi, int bj) Run(int t)
        {
            Array.Clear(hPrev); Array.Fill(f, NegInf);
            double best = 0; int bi = -1, bj = -1;
            for (int i = 0; i + minLag < n; i++)
            {
                Array.Clear(hCur); double e = NegInf; int a = n0 + i; double wi = m.Beats[a];
                for (int j = i + minLag; j < n; j++)
                {
                    int b = n0 + j; double wj = m.Beats[b];
                    double eOpen = hCur[j - 1] - go - ge * wj, eExt = e - ge * wj;
                    bool eNew = eOpen >= eExt; e = eNew ? eOpen : eExt;
                    double fOpen = hPrev[j] - go - ge * wi, fExt = f[j] - ge * wi;
                    bool fNew = fOpen >= fExt; f[j] = fNew ? fOpen : fExt;
                    long cell = (long)i * n + j;
                    double d = NegInf;
                    if (!masked[cell] && m.Allows(a, b, t)) d = (i > 0 ? hPrev[j - 1] : 0) + (m.S(a, b) - theta) * Math.Min(m.Length[a], m.Length[b]);
                    double h = 0; byte src = 0;
                    if (d > h) { h = d; src = 1; }
                    if (e > h) { h = e; src = 2; }
                    if (f[j] > h) { h = f[j]; src = 3; }
                    hCur[j] = h;
                    trace[cell] = (byte)(src | (eNew ? 4 : 0) | (fNew ? 8 : 0));
                    if (h > best + Eps) { best = h; bi = i; bj = j; }
                }
                (hPrev, hCur) = (hCur, hPrev);
            }
            return (best, bi, bj);
        }
        Path Back(int t, int bi, int bj, double score)
        {
            var rows = new List<int>(); var cols = new List<int>();
            int i = bi, j = bj, state = 0;
            while (i >= 0 && j - i >= minLag)
            {
                byte x = trace[(long)i * n + j];
                if (state == 0)
                {
                    int src = x & 3; if (src == 0) break;
                    if (src == 1) { rows.Add(n0 + i); cols.Add(n0 + j); i--; j--; continue; }
                    state = src;
                }
                if (state == 2) { bool open = (x & 4) != 0; j--; if (open) state = 0; }
                else { bool open = (x & 8) != 0; i--; if (open) state = 0; }
            }
            rows.Reverse(); cols.Reverse();
            return new Path { K = t, Score = score, Rows = rows.ToArray(), Cols = cols.ToArray() };
        }
        Path Compute(int t) { var (score, bi, bj) = Run(t); return bi < 0 ? null : Back(t, bi, bj, score); }
        var cache = new Path[13];
        foreach (int t in Order) if (live[t + 6]) cache[t + 6] = Compute(t);
        double stop = settings.DiscoveryStop * (1 - theta);
        while (found.Count < settings.DiscoveryPaths)
        {
            Path best = null;
            foreach (int t in Order) { var p = cache[t + 6]; if (p != null && p.Length > 0 && (best == null || p.Score > best.Score + Eps)) best = p; }
            if (best == null || best.Score < stop - Eps) break;
            found.Add(best);
            for (int x = 0; x < best.Length; x++)
                for (int di = -1; di <= 1; di++)
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        int i = best.Rows[x] - n0 + di, j = best.Cols[x] - n0 + dj;
                        if (i >= 0 && j >= 0 && i < n && j < n) masked[(long)i * n + j] = true;
                    }
            foreach (int t in Order)
            {
                var p = cache[t + 6]; if (p == null) continue;
                bool touched = false;
                for (int x = 0; x < p.Length && !touched; x++) touched = masked[(long)(p.Rows[x] - n0) * n + (p.Cols[x] - n0)];
                if (touched) cache[t + 6] = Compute(t);
            }
        }
        return found;
    }

    // Above BarColumnsAbove columns a node is discovered on bars, and each bar path refined in tactus columns inside a band of
    // ±2 bars around its matched bars (§7 E3), at its transposition, with S and the allowed transpositions computed there.
    Path Refine(Path bars, Matrix m, int minLagColumns)
    {
        var band = new HashSet<long>(); int lo = int.MaxValue, hi = int.MinValue, left = int.MaxValue, right = int.MinValue;
        for (int x = 0; x < bars.Length; x++)
        {
            int a = bars.Rows[x], b = bars.Cols[x];
            int r0 = m.Start[Math.Max(0, a - 2)], r1 = m.Start[Math.Min(m.Count - 1, a + 2)] + m.Length[Math.Min(m.Count - 1, a + 2)] - 1;
            int c0 = m.Start[Math.Max(0, b - 2)], c1 = m.Start[Math.Min(m.Count - 1, b + 2)] + m.Length[Math.Min(m.Count - 1, b + 2)] - 1;
            for (int u = r0; u <= r1; u++) for (int v = c0; v <= c1; v++) if (v - u >= minLagColumns) band.Add((long)u << 32 | (uint)v);
            lo = Math.Min(lo, r0); hi = Math.Max(hi, r1); left = Math.Min(left, c0); right = Math.Max(right, c1);
        }
        if (band.Count == 0) return null;
        int rows = hi - lo + 1, cols = right - left + 1; double go = GapOpen, ge = GapExtend;
        var h = new double[rows + 1, cols + 1]; var e = new double[rows + 1, cols + 1]; var f = new double[rows + 1, cols + 1]; var trace = new byte[rows + 1, cols + 1];
        for (int i = 0; i <= rows; i++) for (int j = 0; j <= cols; j++) { e[i, j] = f[i, j] = NegInf; }
        Span<double> sims = stackalloc double[13];
        double best = 0; int bi = -1, bj = -1;
        for (int i = 1; i <= rows; i++)
            for (int j = 1; j <= cols; j++)
            {
                int u = lo + i - 1, v = left + j - 1;
                double eOpen = h[i, j - 1] - go - ge * Beats(v), eExt = e[i, j - 1] - ge * Beats(v); bool eNew = eOpen >= eExt; e[i, j] = eNew ? eOpen : eExt;
                double fOpen = h[i - 1, j] - go - ge * Beats(u), fExt = f[i - 1, j] - ge * Beats(u); bool fNew = fOpen >= fExt; f[i, j] = fNew ? fOpen : fExt;
                double d = NegInf;
                if (band.Contains((long)u << 32 | (uint)v))
                {
                    Cols.Similarities(u, v, -6, 6, sims); double s = Best(sims, out _), sk = sims[bars.K + 6] - (bars.K != 0 ? Transposed : 0);
                    if (sk >= s - Slack - Eps) d = h[i - 1, j - 1] + s - Theta;
                }
                double x = 0; byte src = 0;
                if (d > x) { x = d; src = 1; }
                if (e[i, j] > x) { x = e[i, j]; src = 2; }
                if (f[i, j] > x) { x = f[i, j]; src = 3; }
                h[i, j] = x; trace[i, j] = (byte)(src | (eNew ? 4 : 0) | (fNew ? 8 : 0));
                if (x > best + Eps) { best = x; bi = i; bj = j; }
            }
        if (bi < 0) return null;
        var rs = new List<int>(); var cs = new List<int>(); int state = 0;
        for (int i = bi, j = bj; i > 0 && j > 0;)
        {
            byte t = trace[i, j];
            if (state == 0)
            {
                int src = t & 3; if (src == 0) break;
                if (src == 1) { rs.Add(lo + i - 1); cs.Add(left + j - 1); i--; j--; continue; }
                state = src;
            }
            if (state == 2) { bool open = (t & 4) != 0; j--; if (open) state = 0; }
            else { bool open = (t & 8) != 0; i--; if (open) state = 0; }
        }
        rs.Reverse(); cs.Reverse();
        return new Path { K = bars.K, Score = best, Rows = rs.ToArray(), Cols = cs.ToArray() };
    }

    // §7 E3's trimming: a path's ends are dropped while the target column there matches none of the path's source columns,
    // at its transposition, by θ + PathTrim (the étude's [8.2–20.1]~[61.2–73.1] so starts at 62.1). Null when nothing is left.
    Path Trim(Path p)
    {
        int a = 0, z = p.Length - 1;
        double To(int c) { double best = 0; for (int r = p.Rows[a]; r <= p.Rows[z]; r++) if (r < c) best = Math.Max(best, Sk(r, c, p.K)); return best; }
        double limit = Theta + settings.PathTrim - Eps;
        while (a <= z && To(p.Cols[a]) < limit) a++;
        while (z >= a && To(p.Cols[z]) < limit) z--;
        if (a > z) return null;
        var q = new Path { K = p.K, Rows = p.Rows[a..(z + 1)], Cols = p.Cols[a..(z + 1)] };
        q.Score = Score(q.Rows, q.Cols, q.K);
        return q;
    }
    // Σ (S_k − θ) over the matched columns less a gap's go + ge per beat for each run of skipped rows and of skipped columns.
    double Score(int[] rows, int[] cols, int k)
    {
        double s = 0;
        for (int x = 0; x < rows.Length; x++)
        {
            s += Sk(rows[x], cols[x], k) - Theta;
            if (x == 0) continue;
            if (rows[x] - rows[x - 1] > 1) s -= GapOpen + GapExtend * Beats(rows[x - 1] + 1, rows[x] - 1);
            if (cols[x] - cols[x - 1] > 1) s -= GapOpen + GapExtend * Beats(cols[x - 1] + 1, cols[x] - 1);
        }
        return s;
    }
    // §7 E3: structural at a node of `columns` columns when it scores ≥ 6(1 − θ) and both sides span ≥ max(2 bars, L/16).
    bool Structural(Path p, int columns, int barColumns) =>
        p.Score >= settings.StructuralScore * (1 - Theta) - Eps
        && Math.Min(p.Rows[^1] - p.Rows[0], p.Cols[^1] - p.Cols[0]) + 1 >= Math.Max(settings.StructuralBars * barColumns, columns / (double)settings.StructuralDivisor) - Eps;

    // The discovery of a node of columns [c0, c1): on tactus columns, or on bars refined in tactus columns. Paths are trimmed
    // and marked structural.
    List<Path> DiscoverNode(int c0, int c1, out int minLag, out int barColumns)
    {
        int columns = c1 - c0;
        barColumns = BarColumns(c0, c1);
        minLag = Math.Max(barColumns, (int)Math.Round(columns / (double)settings.MinLagDivisor, MidpointRounding.AwayFromZero));
        var found = new List<Path>();
        if (columns <= minLag) return found;
        List<Path> raw;
        if (Tactus != null) raw = Discover(Tactus, c0 - first, c1 - first, minLag).Select(p => new Path { K = p.K, Score = p.Score, Rows = p.Rows.Select(a => a + first).ToArray(), Cols = p.Cols.Select(b => b + first).ToArray() }).ToList();
        else
        {
            var (start, length) = BarUnits(c0, c1);
            var bars = Matrix.Build(Cols, grid, start, length, out _);
            int lagBars = Math.Max(1, (int)Math.Round(minLag / (double)barColumns, MidpointRounding.AwayFromZero));
            raw = new List<Path>();
            foreach (var p in Discover(bars, 0, bars.Count, lagBars)) { var r = Refine(p, bars, minLag); if (r != null && r.Length > 0) raw.Add(r); }
        }
        foreach (var p in raw)
        {
            var t = Trim(p); if (t == null) continue;
            t.Structural = Structural(t, columns, barColumns);
            found.Add(t);
            Trace?.Invoke($"  path [{Show(t.Rows[0])}–{Show(t.Rows[^1])}]~[{Show(t.Cols[0])}–{Show(t.Cols[^1])}] k {t.K} score {t.Score:0.00} (raw {p.Score:0.00}){(t.Structural ? " structural" : "")}");
        }
        return found;
    }
    // "b.k" (printed bar, tactus beat) for traces.
    string Show(int column) { int b = grid.ColumnBar[column]; return FormattableString.Invariant($"{grid.Printed[b]}.{(grid.ColumnStart[column] - grid.BarStart[b]) / grid.Unit[b] + 1}"); }

    // ---------------------------------------------------------------------------------------------------------------
    // §7 E4–E5: alignment of a hull against a target.

    // An affine alignment of the source columns [r0, r1] (every one matched or deleted) against the target columns from c0 on
    // (to c1, inclusive), scored Σ (S_k − θ) over matches less go + ge per beat for each run of deleted or inserted columns.
    // freeStart: it may begin at any target column (§7 E4's occurrences); otherwise at c0 (§7 E5). globalEnd: it must end at
    // c1 (reviewed sections); otherwise its end is local and the rest of the target is left over (the tail). allowed: the target
    // columns a match or insertion may use (null: all). Null when no alignment exists.
    (double score, List<Op> ops) Align(int r0, int r1, int c0, int c1, int k, bool freeStart, bool globalEnd = false, bool[] allowed = null, double[,] scores = null)
    {
        int R = r1 - r0 + 1, C = c1 - c0 + 1;
        if (R <= 0 || C <= 0) return (NegInf, null);
        double go = GapOpen, ge = GapExtend;
        var mx = new double[R + 1, C + 1]; var dx = new double[R + 1, C + 1]; var ix = new double[R + 1, C + 1];
        var tm = new byte[R + 1, C + 1]; var td = new byte[R + 1, C + 1]; var ti = new byte[R + 1, C + 1];
        bool Free(int c) => allowed == null || allowed[c0 + c];
        double Start(int c) => freeStart || c == 0 ? 0 : NegInf;
        for (int r = 0; r <= R; r++)
            for (int c = 0; c <= C; c++)
            {
                mx[r, c] = dx[r, c] = ix[r, c] = NegInf;
                if (r > 0 && c > 0 && Free(c - 1))
                {
                    double s = (scores != null ? scores[r - 1, c - 1] : Sk(r0 + r - 1, c0 + c - 1, k)) - Theta;
                    double best = mx[r - 1, c - 1]; byte from = 0;
                    if (dx[r - 1, c - 1] > best) { best = dx[r - 1, c - 1]; from = 1; }
                    if (ix[r - 1, c - 1] > best) { best = ix[r - 1, c - 1]; from = 2; }
                    if (r == 1 && Start(c - 1) > best) { best = Start(c - 1); from = 3; }
                    if (best > NegInf) { mx[r, c] = best + s; tm[r, c] = from; }
                }
                if (r > 0)
                {
                    double open = go + ge * Beats(r0 + r - 1), ext = ge * Beats(r0 + r - 1);
                    double best = mx[r - 1, c] - open; byte from = 0;
                    if (ix[r - 1, c] - open > best) { best = ix[r - 1, c] - open; from = 1; }
                    if (r == 1 && Start(c) - open > best) { best = Start(c) - open; from = 2; }
                    if (dx[r - 1, c] - ext > best) { best = dx[r - 1, c] - ext; from = 3; }
                    dx[r, c] = best; td[r, c] = from;
                }
                if (c > 0 && Free(c - 1) && (r > 0 || !freeStart))
                {
                    double open = go + ge * Beats(c0 + c - 1), ext = ge * Beats(c0 + c - 1);
                    double best = mx[r, c - 1] - open; byte from = 0;
                    if (dx[r, c - 1] - open > best) { best = dx[r, c - 1] - open; from = 1; }
                    if (ix[r, c - 1] - ext > best) { best = ix[r, c - 1] - ext; from = 2; }
                    if (r == 0 && c == 1 && Start(0) - open > best) { best = Start(0) - open; from = 3; }
                    ix[r, c] = best; ti[r, c] = from;
                }
            }
        double end = NegInf; int ec = -1; byte state = 0;
        for (int c = globalEnd ? C : 0; c <= C; c++)
        {
            if (mx[R, c] > end + Eps) { end = mx[R, c]; ec = c; state = Match; }
            if (dx[R, c] > end + Eps) { end = dx[R, c]; ec = c; state = Delete; }
        }
        if (ec < 0 || end == NegInf) return (NegInf, null);
        var ops = new List<Op>();
        for (int r = R, c = ec; r > 0 || c > 0;)
        {
            if (state == Match)
            {
                ops.Add(new Op(Match, r0 + r - 1, c0 + c - 1)); byte from = tm[r, c]; r--; c--;
                if (from == 3) break;
                state = from == 0 ? Match : from == 1 ? Delete : Insert;
            }
            else if (state == Delete)
            {
                ops.Add(new Op(Delete, r0 + r - 1, -1)); byte from = td[r, c]; r--;
                if (from == 2) break;
                state = from == 0 ? Match : from == 1 ? Insert : Delete;
            }
            else
            {
                ops.Add(new Op(Insert, -1, c0 + c - 1)); byte from = ti[r, c]; c--;
                if (from == 3) break;
                state = from == 0 ? Match : from == 1 ? Delete : Insert;
            }
        }
        ops.Reverse();
        return (end, ops);
    }

    // The matched columns of an alignment and the shares they explain (S_k ≥ θ), in beats, of the target's aligned span and of
    // the source.
    (int matched, double copyShare, double prototypeShare) Explained(List<Op> ops, int k, int r0, int r1)
    {
        int matched = 0, t0 = -1, t1 = -1; double target = 0, source = 0;
        foreach (var o in ops)
        {
            if (o.Col >= 0) { if (t0 < 0) t0 = o.Col; t1 = o.Col; }
            if (o.Kind != Match) continue;
            matched++;
            if (Sk(o.Row, o.Col, k) >= Theta - Eps) { target += Beats(o.Col); source += Beats(o.Row); }
        }
        return (matched, t0 < 0 ? 0 : target / Math.Max(Eps, Beats(t0, t1)), source / Math.Max(Eps, Beats(r0, r1)));
    }

    // §7 E4: the occurrences of the hull [h0, h1] among the allowed target columns, the best first: anchored semi-global
    // alignment at each of the family's transpositions; one needs ≥ 50% of the hull's columns matched. An inserted span that
    // recurs elsewhere (a bridge heard twice) cuts it, keeping the side that holds more matches. Each occurrence found closes
    // its columns and those within min_lag before it. Copies: occurrences explaining ≥ CopyShare of themselves and ≥
    // PrototypeShare of the hull.
    List<Occurrence> Occurrences(int h0, int h1, IReadOnlyList<int> ks, bool[] allowed, int c0, int c1, int minLag, List<Path> paths)
    {
        var copies = new List<Occurrence>();
        allowed = (bool[])allowed.Clone();
        int rows = h1 - h0 + 1;
        for (int round = 0; round < 64; round++)
        {
            Occurrence best = null;
            foreach (int k in ks)
            {
                var (score, ops) = Align(h0, h1, c0, c1 - 1, k, freeStart: true, allowed: allowed);
                if (ops == null || score <= Eps) continue;
                if (best == null || score > best.Score + Eps) best = new Occurrence { K = k, Score = score, Ops = ops };
            }
            if (best == null) break;
            CutRecurring(best, paths);
            if (!best.Ops.Any(o => o.Col >= 0)) break;
            var (matched, copyShare, prototypeShare) = Explained(best.Ops, best.K, h0, h1);
            if (matched < .5 * rows - Eps) break;
            best.First = best.Ops.First(o => o.Col >= 0).Col; best.Last = best.Ops.Last(o => o.Col >= 0).Col;
            for (int c = Math.Max(c0, best.First - minLag + 1); c <= best.Last; c++) allowed[c] = false;
            Trace?.Invoke($"  occurrence [{Show(best.First)}–{Show(best.Last)}] k {best.K} score {best.Score:0.00} matched {matched}/{rows} explains {copyShare:0.00} of itself, {prototypeShare:0.00} of the hull");
            if (copyShare >= settings.CopyShare - Eps && prototypeShare >= settings.PrototypeShare - Eps) copies.Add(best);
        }
        return copies.OrderBy(o => o.First).ToList();
    }
    // An inserted run of at least a bar inside an occurrence that recurs elsewhere: some discovered path has one side lying at
    // least half inside it and the other outside it. The occurrence is cut there and keeps the side with more matches.
    void CutRecurring(Occurrence occ, List<Path> paths)
    {
        var ops = occ.Ops;
        for (int x = 0; x < ops.Count; x++)
        {
            if (ops[x].Kind != Insert || x == 0 || ops[x - 1].Kind == Insert) continue;
            int y = x; while (y < ops.Count && ops[y].Kind == Insert) y++;
            if (y >= ops.Count || !ops.Skip(y).Any(o => o.Kind == Match) || !ops.Take(x).Any(o => o.Kind == Match)) { x = y; continue; }
            int a = ops[x].Col, b = ops[y - 1].Col;
            if (TickEnd(b) - Tick(a) < BarLength(Tick(a))) { x = y; continue; }
            bool Recurs(int[] side, int[] other)
            {
                int inside = 0; foreach (int c in side) if (c >= a && c <= b) inside++;
                if (inside < .5 * (b - a + 1)) return false;
                foreach (int c in other) if (c >= a && c <= b) return false;
                return true;
            }
            if (!paths.Any(p => Recurs(p.Rows, p.Cols) || Recurs(p.Cols, p.Rows))) { x = y; continue; }
            int before = ops.Take(x).Count(o => o.Kind == Match), after = ops.Skip(y).Count(o => o.Kind == Match);
            occ.Ops = before >= after ? ops.Take(x).ToList() : ops.Skip(y).ToList();
            while (occ.Ops.Count > 0 && occ.Ops[^1].Kind == Insert) occ.Ops.RemoveAt(occ.Ops.Count - 1);
            while (occ.Ops.Count > 0 && occ.Ops[0].Kind == Insert) occ.Ops.RemoveAt(0);
            CutRecurring(occ, paths);
            return;
        }
    }

    // §7 E5.1–2: the canonical alignment of a copy: the anchored affine DP from its trimmed start (its end local: what follows is
    // the tail), then gap sliding. Each deletion, left to right in two passes, may move to any placement [a, a + g) of the hull
    // columns that keeps the copy's columns; among placements scoring ≥ best − δ (δ = 1 − θ) the choice is lexicographic: gap
    // ends on the prototype's phrase edges (an arrival's edge 1, an internal return's 0.5), descending; whether the copy's first
    // column restates its source exactly, descending; the first matched source column, ascending (the reprise restates its source
    // from the start); the larger a (mdl-grammar's right-normalisation).
    List<Op> Canonical(int h0, int h1, int t0, int t1, int k, double[] startWeight, double[] endWeight)
    {
        var (_, ops) = Align(h0, h1, t0, t1, k, freeStart: false);
        if (ops == null) return null;
        double delta = settings.SlideDelta * (1 - Theta);
        for (int pass = 0; pass < 2; pass++)
            for (int x = 0; x < ops.Count; x++)
            {
                if (ops[x].Kind != Delete || x > 0 && ops[x - 1].Kind == Delete) continue;
                int y = x; while (y < ops.Count && ops[y].Kind == Delete) y++;
                int lo = x; while (lo > 0 && ops[lo - 1].Kind == Match) lo--;
                int hi = y; while (hi < ops.Count && ops[hi].Kind == Match) hi++;
                if (lo == x && hi == y) continue;
                int g = y - x, r0 = ops[lo].Row, r1 = ops[hi - 1].Row + 1, firstCol = -1;
                for (int z = lo; z < hi; z++) if (ops[z].Kind == Match) { firstCol = ops[z].Col; break; }
                int count = r1 - r0 - g;
                int globalFirst = ops.FindIndex(o => o.Kind == Match);
                bool holdsFirst = globalFirst >= lo && globalFirst < hi;
                int fixedFirstRow = holdsFirst ? -1 : ops[globalFirst].Row, fixedFirstCol = holdsFirst ? -1 : ops[globalFirst].Col;
                (double score, double edges, bool exact, int firstRow, int a) best = (NegInf, 0, false, 0, 0);
                var placements = new List<(double score, double edges, bool exact, int firstRow, int a)>();
                for (int a = r0; a <= r1 - g; a++)
                {
                    double score = -(GapOpen + GapExtend * Beats(a, a + g - 1));
                    for (int t = 0; t < count; t++) { int row = t < a - r0 ? r0 + t : a + g + t - (a - r0); score += Sk(row, firstCol + t, k) - Theta; }
                    int fr = holdsFirst ? (a > r0 ? r0 : a + g) : fixedFirstRow, fc = holdsFirst ? firstCol : fixedFirstCol;
                    var p = (score, startWeight[a - h0] + endWeight[a + g - 1 - h0], fc == t0 && Exact(fr, fc, k), fr, a);
                    placements.Add(p);
                    if (score > best.score) best = p;
                }
                // A placement whose exchanged hull columns restate the best one's (mean S ≥ θ: the hull repeats itself there, §0.4's
                // equal-content region a1 ≅ a1′) ties whatever its score: noise moves the score there, not the music.
                int Row(int a, int t) => t < a - r0 ? r0 + t : a + g + t - (a - r0);
                bool Equivalent(int a)
                {
                    double sum = 0; int n = 0;
                    for (int t = 0; t < count; t++)
                    {
                        int u = Row(a, t), v = Row(best.a, t); if (u == v) continue;
                        sum += Sk(Math.Min(u, v), Math.Max(u, v), 0); n++;
                    }
                    return n > 0 && sum / n >= Theta - Eps;
                }
                var ties = placements.Where(p => p.score >= best.score - delta - Eps || Equivalent(p.a)).ToList();
                var chosen = ties.OrderByDescending(p => p.edges).ThenByDescending(p => p.exact).ThenBy(p => p.firstRow).ThenByDescending(p => p.a).First();
                if (chosen.a == ops[x].Row) { x = y - 1; continue; }
                var region = new List<Op>();
                for (int t = 0; t < chosen.a - r0; t++) region.Add(new Op(Match, r0 + t, firstCol + t));
                for (int t = 0; t < g; t++) region.Add(new Op(Delete, chosen.a + t, -1));
                for (int t = chosen.a - r0; t < count; t++) region.Add(new Op(Match, chosen.a + g + t - (chosen.a - r0), firstCol + t));
                ops.RemoveRange(lo, hi - lo); ops.InsertRange(lo, region);
                Trace?.Invoke($"  slide: {g}-column deletion placed at {Show(chosen.a)} of {ties.Count} near ties (score {chosen.score:0.00}, best {best.score:0.00}, edges {chosen.edges}, exact first {chosen.exact})");
                x = lo + (chosen.a - r0) + g - 1;
            }
        return ops;
    }

    // §7 E5.3: matched runs become match edits, split where a column's sim_k falls below 0.999 (substitutions, the sparks);
    // deleted runs delete edits; target columns left unmatched inside the copy insert edits, and those after its last match up
    // to t1 the tail insert (the coda candidate).
    Edit[] Script(List<Op> ops, int k, int t1, out long tail)
    {
        var edits = new List<Edit>(); tail = -1;
        for (int x = 0; x < ops.Count;)
        {
            var o = ops[x]; int y = x + 1;
            if (o.Kind == Match)
            {
                bool Sub(Op p) => Cols.Similarity(p.Row, p.Col, k) < Same;
                bool sub = Sub(o);
                while (y < ops.Count && ops[y].Kind == Match && Sub(ops[y]) == sub) y++;
                edits.Add(new Edit { Op = sub ? PreparedPatternSong.MotifEdits.Substitute : PreparedPatternSong.MotifEdits.Match, SourceStart = Tick(o.Row), SourceEnd = TickEnd(ops[y - 1].Row), TargetStart = Tick(o.Col), TargetEnd = TickEnd(ops[y - 1].Col) });
            }
            else
            {
                while (y < ops.Count && ops[y].Kind == o.Kind) y++;
                edits.Add(o.Kind == Delete
                    ? new Edit { Op = PreparedPatternSong.MotifEdits.Delete, SourceStart = Tick(o.Row), SourceEnd = TickEnd(ops[y - 1].Row), TargetStart = -1, TargetEnd = -1 }
                    : new Edit { Op = PreparedPatternSong.MotifEdits.Insert, SourceStart = -1, SourceEnd = -1, TargetStart = Tick(o.Col), TargetEnd = TickEnd(ops[y - 1].Col) });
            }
            x = y;
        }
        int last = -1; foreach (var o in ops) if (o.Col >= 0) last = o.Col;
        if (last >= 0 && last < t1)
        {
            tail = Tick(last + 1);
            edits.Add(new Edit { Op = PreparedPatternSong.MotifEdits.Insert, SourceStart = -1, SourceEnd = -1, TargetStart = tail, TargetEnd = TickEnd(t1) });
        }
        return edits.ToArray();
    }

    // The return of an alignment: its script, matched columns, span and shares. Omitted: the phrases [cuts) of the prototype
    // whose hull columns are all deleted (§7 E5.4).
    Return MakeReturn(int level, List<Op> ops, int k, int h0, int h1, int t1, long[] phrases)
    {
        var r = new Return { Id = returns.Count, Level = level, K = k };
        r.Script = Script(ops, k, t1, out r.TailStart);
        r.Rows = ops.Where(o => o.Kind == Match).Select(o => o.Row).ToArray(); r.Cols = ops.Where(o => o.Kind == Match).Select(o => o.Col).ToArray();
        int sFirst = ops.First(o => o.Row >= 0).Row, sLast = ops.Last(o => o.Row >= 0).Row, tFirst = ops.First(o => o.Col >= 0).Col;
        r.SourceStart = Tick(sFirst); r.SourceEnd = TickEnd(sLast); r.Start = Tick(tFirst); r.End = TickEnd(t1);
        var (_, copyShare, prototypeShare) = Explained(ops, k, h0, h1);
        r.CopyShare = copyShare; r.PrototypeShare = prototypeShare;
        r.Score = 0; foreach (var o in ops) if (o.Kind == Match) r.Score += Sk(o.Row, o.Col, k) - Theta;
        var deleted = new HashSet<int>(ops.Where(o => o.Kind == Delete).Select(o => o.Row));
        var omitted = new List<(long, long)>();
        for (int p = 0; p + 1 < (phrases?.Length ?? 0); p++)
        {
            int inHull = 0, gone = 0;
            for (int c = h0; c <= h1; c++) if (Tick(c) >= phrases[p] && Tick(c) < phrases[p + 1]) { inHull++; if (deleted.Contains(c)) gone++; }
            if (inHull > 0 && gone == inHull) omitted.Add((phrases[p], phrases[p + 1]));
        }
        r.Omitted = omitted.ToArray();
        returns.Add(r);
        return r;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §7 E4 at a node: families in order of explained beats.

    // Families of the node [c0, c1): the structural paths (their pairs outside columns already taken) grouped by overlapping
    // targets; the family explaining the most beats first. Its hull is the union of its paths' sources whose gaps are ≤ max(2
    // bars, L/8), the block holding the most matched columns, extended to the node's first downbeat when it begins within
    // min_lag of it; its copies are the accepted occurrences. Taken columns (hulls and copies) leave the later families.
    List<Family> Families(int c0, int c1, List<Path> paths, int minLag, int barColumns, bool[] taken)
    {
        var families = new List<Family>(); int columns = c1 - c0;
        var spent = new bool[paths.Count];
        for (int round = 0; round < 64; round++)
        {
            var live = new List<Path>(); var origin = new List<int>(); var any = new List<Path>(); var anyOrigin = new List<int>();
            for (int i = 0; i < paths.Count; i++)
            {
                var p = paths[i]; if (spent[i]) continue;
                var keep = Enumerable.Range(0, p.Length).Where(x => !taken[p.Rows[x]] && !taken[p.Cols[x]]).ToArray();
                if (keep.Length == 0) continue;
                var q = keep.Length == p.Length ? p : new Path { K = p.K, Rows = keep.Select(x => p.Rows[x]).ToArray(), Cols = keep.Select(x => p.Cols[x]).ToArray() };
                if (q != p) { q.Score = Score(q.Rows, q.Cols, q.K); q.Structural = Structural(q, columns, barColumns); }
                any.Add(q); anyOrigin.Add(i);
                if (q.Structural) { live.Add(q); origin.Add(i); }
            }
            if (live.Count == 0) break;
            // Group by overlapping targets (in path order, so groups are deterministic).
            var group = new int[live.Count]; for (int i = 0; i < group.Length; i++) group[i] = i;
            int Root(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }
            for (int i = 0; i < live.Count; i++)
                for (int j = i + 1; j < live.Count; j++)
                    if (live[i].Cols[0] <= live[j].Cols[^1] && live[j].Cols[0] <= live[i].Cols[^1]) group[Root(j)] = Root(i);
            int bestRoot = -1; double bestBeats = 0; int bestStart = int.MaxValue;
            for (int i = 0; i < live.Count; i++)
            {
                if (Root(i) != i) continue;
                var cols = new SortedSet<int>(); int start = int.MaxValue;
                for (int j = 0; j < live.Count; j++) if (Root(j) == i) { foreach (int c in live[j].Cols) cols.Add(c); start = Math.Min(start, live[j].Cols[0]); }
                double beats = 0; foreach (int c in cols) beats += Beats(c);
                if (beats > bestBeats + Eps || Math.Abs(beats - bestBeats) <= Eps && start < bestStart) { bestRoot = i; bestBeats = beats; bestStart = start; }
            }
            var members = live.Where((p, i) => Root(i) == bestRoot).ToList();
            for (int i = 0; i < live.Count; i++) if (Root(i) == bestRoot) spent[origin[i]] = true;
            // The hull: the sources of every path mapping into the family's target region (within a bar of it), structural or
            // not, as the design evidence (§0.4) built it: under transcription noise the étude's [1.1–6.2]~[62.1–67.2] falls
            // below 6(1 − θ) while it still places the reprise's head. They merge across gaps ≤ max(2 bars, L/8), never across
            // taken columns.
            int targetFirst = members.Min(p => p.Cols[0]), targetLast = members.Max(p => p.Cols[^1]);
            var sources = new List<Path>(members);
            for (int i = 0; i < any.Count; i++)
            {
                var p = any[i]; if (p.Structural) continue;
                if (p.Cols[0] < targetFirst - barColumns || p.Cols[^1] > targetLast + barColumns || p.Cols[^1] < targetFirst || p.Cols[0] > targetLast) continue;
                // A source inside the target region restates the target itself: with a1′ equal to a1 (F13) the target's own
                // [9.2–10.2]~[12.2–13.2] otherwise grew the hull over the copy, and the internal return a1′ ≅ a1 was lost.
                if (p.Rows[^1] >= targetFirst && p.Rows[0] <= targetLast) continue;
                sources.Add(p); spent[anyOrigin[i]] = true;
            }
            double allow = Math.Max(settings.StructuralBars * barColumns, columns / (double)settings.MinLagDivisor);
            var blocks = new List<(int first, int last, int matched)>();
            foreach (var p in sources.OrderBy(p => p.Rows[0]).ThenBy(p => p.Rows[^1]))
            {
                int a = p.Rows[0], b = p.Rows[^1];
                if (blocks.Count > 0)
                {
                    var last = blocks[^1]; int gap = a - last.last - 1;
                    bool clear = true; for (int c = last.last + 1; c < a && clear; c++) clear = !taken[c];
                    if (gap <= allow + Eps && clear) { blocks[^1] = (last.first, Math.Max(last.last, b), last.matched + p.Length); continue; }
                }
                blocks.Add((a, b, p.Length));
            }
            var hull = blocks[0]; foreach (var bl in blocks) if (bl.matched > hull.matched) hull = bl;
            int downbeat = FirstDownbeat(c0, c1), h0 = hull.first, h1 = hull.last;
            if (h0 > downbeat && h0 - downbeat <= minLag)
            {
                bool clear = true; for (int c = downbeat; c < h0 && clear; c++) clear = !taken[c];
                if (clear) h0 = downbeat;
            }
            // Occurrences after the hull, at least min_lag from its start, on columns not yet taken. When the head the hull took
            // on leaves no occurrence with half its columns matched (the Rach2 Moderato's 30 bars before its returning theme), the
            // hull drops it again: the head joins the section anyway (§7 E6), and the family stays.
            var ks = Order.Where(t => members.Any(p => p.K == t)).ToList();
            List<Occurrence> Find(int from)
            {
                var allowed = new bool[grid.Columns];
                for (int c = Math.Max(h1 + 1, from + minLag); c < c1; c++) allowed[c] = !taken[c];
                Trace?.Invoke($"  family: hull [{Show(from)}–{Show(h1)}], {members.Count} structural paths and {sources.Count - members.Count} more into their target, {bestBeats:0.#} beats explained, k {string.Join(",", ks)}");
                return Occurrences(from, h1, ks, allowed, Math.Max(h1 + 1, from + minLag), c1, minLag, paths);
            }
            var copies = Find(h0);
            if (copies.Count == 0 && h0 < hull.first) { h0 = hull.first; copies = Find(h0); }
            if (copies.Count == 0) continue;
            // A later family lying wholly inside one span the earlier families leave (the Nocturne's refrain inside B) is that
            // span's own structure: each repetition enters the tree once, so it waits for the span's internal returns.
            if (families.Count > 0)
            {
                int lo = Math.Min(h0, copies[0].First), hi = Math.Max(h1, copies[^1].Last);
                bool inside = true; for (int c = lo; c <= hi && inside; c++) inside = !taken[c];
                if (inside) { Trace?.Invoke("  (inside one span between the families above: left to its internal returns)"); continue; }
            }
            var family = new Family { HullFirst = h0, HullLast = h1 };
            family.Copies.AddRange(copies);
            families.Add(family);
            for (int c = h0; c <= h1; c++) taken[c] = true;
            foreach (var o in copies) for (int c = o.First; c <= o.Last; c++) taken[c] = true;
        }
        return families;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §7 E6–E7: classical forms.

    void ClassicalForm()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int c0 = grid.FirstColumn, c1 = grid.Columns, columns = c1 - c0;
        Trace?.Invoke($"returns: θ {Theta:0.###}, go {GapOpen:0.###}, ge {GapExtend:0.###} per beat, {columns} columns{(OnBars ? " (discovery on bars)" : "")}");
        var paths = DiscoverNode(c0, c1, out int minLag, out int barColumns);
        Paths = paths.ToArray();
        Trace?.Invoke($"  ({clock.ElapsedMilliseconds} ms: discovery)");
        var taken = new bool[grid.Columns];
        var families = Families(c0, c1, paths, minLag, barColumns, taken);
        Trace?.Invoke($"  ({clock.ElapsedMilliseconds} ms: families)");
        // Provisional sections in time order: hulls, copies, and the spans between them.
        var spans = new List<(int first, int last, int kind, int family, int copy)>();
        for (int f = 0; f < families.Count; f++)
        {
            spans.Add((families[f].HullFirst, families[f].HullLast, PrototypeKind, f, -1));
            for (int o = 0; o < families[f].Copies.Count; o++) spans.Add((families[f].Copies[o].First, families[f].Copies[o].Last, CopyKind, f, o));
        }
        spans.Sort((a, b) => a.first.CompareTo(b.first));
        var sections = new List<Section>(); var hullOf = new Dictionary<Section, Family>(); var copyOf = new Dictionary<Section, Occurrence>();   // lookups only
        int at = c0;
        foreach (var sp in spans)
        {
            if (sp.first > at) sections.Add(new Section { Kind = ComplementKind, Start = Tick(at), End = Tick(sp.first) });
            var s = new Section { Kind = sp.kind, Start = Tick(sp.first), End = TickEnd(sp.last) };
            if (sp.kind == PrototypeKind) { s.HullFirst = families[sp.family].HullFirst; s.HullLast = families[sp.family].HullLast; hullOf[s] = families[sp.family]; }
            else copyOf[s] = families[sp.family].Copies[sp.copy];
            sections.Add(s); at = sp.last + 1;
        }
        if (at < c1)
        {
            // What follows the last section: the last copy's tail (decided after E5), else a complement.
            if (sections.Count > 0 && sections[^1].Kind == CopyKind) sections[^1].End = grid.SpanEnd;
            else sections.Add(new Section { Kind = ComplementKind, Start = Tick(at), End = grid.SpanEnd });
        }
        else if (sections.Count > 0) sections[^1].End = grid.SpanEnd;
        if (sections.Count == 0) sections.Add(new Section { Kind = ComplementKind, Start = grid.SpanStart, End = grid.SpanEnd });
        // The head, before the first hull: up to a bar, or min_lag, joins that section as its anacrusis (the étude's pickup B3);
        // a longer one with notes is an Intro. It is no complement: nothing precedes it.
        if (sections.Count > 1 && sections[0].Kind == ComplementKind && sections[1].Kind == PrototypeKind)
        {
            var head = sections[0]; var next = sections[1];
            bool sounding = input.Events.Any(e => !e.Drum && e.Tick >= head.Start && e.Tick < head.End);
            if (!sounding || ColumnOf(next.Start) - c0 <= minLag || head.End - head.Start <= BarLength(head.Start)) { sections.RemoveAt(0); next.Start = grid.SpanStart; }
            else { head.Kind = IntroKind; head.Label = "Intro"; }
        }
        sections[0].Start = grid.SpanStart;
        Snap(sections, copyOf);
        // §7 E7 inside every section that is not a copy, then the cuts of every section.
        Sections = sections.ToArray();
        Cuts = new Cut[Sections.Length][];
        for (int i = 0; i < Sections.Length; i++)
        {
            var s = Sections[i]; s.Id = i;
            var inner = Sections[i].Kind == CopyKind ? new List<Return>() : Internal(s);
            Cuts[i] = PhraseCuts(s.Start, s.End, inner);
        }
        Trace?.Invoke($"  ({clock.ElapsedMilliseconds} ms: internal returns)");
        // §7 E5 for every copy, against its family's hull and the phrase edges of the hull's section.
        foreach (var s in Sections)
        {
            if (s.Kind != CopyKind) continue;
            var occ = copyOf[s]; var family = families.First(f => f.Copies.Contains(occ));
            var proto = Sections.First(x => x.Kind == PrototypeKind && hullOf[x] == family);
            int t0 = ColumnOf(s.Start), t1 = ColumnOf(s.End - 1);   // the last copy runs to the end: what follows its match is its tail
            var (starts, ends) = Edges(proto, family.HullFirst, family.HullLast);
            var ops = Canonical(family.HullFirst, family.HullLast, t0, t1, occ.K, starts, ends) ?? occ.Ops;
            var r = MakeReturn(1, ops, occ.K, family.HullFirst, family.HullLast, t1, PhraseStarts(proto));
            r.Section = s.Id; r.SourceSection = proto.Id;
            s.Prototype = proto.Id; s.Family = proto.Id; s.Return = r.Id;
            Trace?.Invoke($"  return: {Describe(r)}");
        }
        foreach (var s in Sections) if (s.Kind == PrototypeKind) s.Family = s.Id;
        Tail();
        Letters();
        Trace?.Invoke($"  ({clock.ElapsedMilliseconds} ms: canonical scripts)");
        ComplementReturns();
        Letters();
        Trace?.Invoke($"  ({clock.ElapsedMilliseconds} ms: complements)");
    }

    // §7 E6's snaps of the section boundaries (not the span's ends). Where a hull or a copy ends and a complement begins, the
    // boundary moves to the nearest arrival cut within a bar: the section closes on its cadence, while repetition alone ends
    // where the copies diverge (the étude's 20.1, a bar before A closes on 21.1). The cell snap asks whether the source aligned
    // to the cell's part before the boundary holds a member of its paradigm, so it applies where that part lies in an
    // alignment: a copy's end no arrival places (and the coda's start, in Tail). A hull's end and a copy's start, which the
    // alignment places, stay: with no aligned source the condition would hold for any cell, and a transcription's tiling
    // would pull them a beat early. Last, the direction snap (score imports only).
    void Snap(List<Section> sections, Dictionary<Section, Occurrence> copyOf)
    {
        for (int i = 1; i < sections.Count; i++)
        {
            var left = sections[i - 1]; var right = sections[i];
            long b = right.Start;
            if (left.Kind is PrototypeKind or CopyKind && right.Kind == ComplementKind)
            {
                long cut = ArrivalNear(b, left.Start, right.End);
                if (cut >= 0) b = cut;
                else if (left.Kind == CopyKind && copyOf.TryGetValue(left, out var occ))
                {
                    var (s0, s1) = AlignedBefore(occ.Ops.Where(o => o.Kind == Match).Select(o => o.Row).ToArray(), occ.Ops.Where(o => o.Kind == Match).Select(o => o.Col).ToArray(), b);
                    b = CellSnap(b, s0, s1, left.Start, right.End);
                }
            }
            b = DirectionSnap(b, left.Start, right.End);
            left.End = right.Start = b;
        }
    }
    // The source column matched to the target column that ends at b (the tactus before it): (start, end) ticks, −1 when none.
    (long start, long end) AlignedBefore(int[] rows, int[] cols, long b)
    {
        for (int x = cols.Length - 1; x >= 0; x--) if (TickEnd(cols[x]) <= b && TickEnd(cols[x]) > b - grid.TactusAt(b - 1)) return (Tick(rows[x]), TickEnd(rows[x]));
        return (-1, -1);
    }
    // §7 E6's cell snap: a boundary strictly inside a principal-stream cell [c0, c1), at most a tactus after c0, moves to c0,
    // unless a member of the cell's paradigm lies in the source span [sourceStart, sourceEnd) aligned to [c0, b) (none: no snap).
    // The coda so starts with the sigh at 73.1, whose G♯4 is bar 20's.
    long CellSnap(long b, long sourceStart, long sourceEnd, long low, long high)
    {
        int p = input.Streams.Principal; if (p < 0 || p >= lex.StreamCount.Length || sourceStart < 0) return b;
        foreach (var c in lex.Of(p))
        {
            if (c.Start >= b) break;
            if (c.End <= b || b - c.Start > grid.TactusAt(c.Start) || c.Start <= low || c.Start >= high) continue;
            if (c.Paradigm >= 0 && lex.MembersOf(lex.Paradigms[c.Paradigm]).Any(m => m.Start < sourceEnd && m.End > sourceStart)) return b;
            return c.Start;
        }
        return b;
    }
    // §7 E6's direction snap: a boundary within a tactus of a marked tempo or expression direction moves to it, unless that
    // would cut a principal-stream cell.
    long DirectionSnap(long b, long low, long high)
    {
        long best = -1, tactus = grid.TactusAt(b);
        foreach (long d in Directions) if (Math.Abs(d - b) <= tactus && d > low && d < high && (best < 0 || Math.Abs(d - b) < Math.Abs(best - b) || Math.Abs(d - b) == Math.Abs(best - b) && d > best)) best = d;
        if (best < 0 || best == b) return b;
        int p = input.Streams.Principal;
        if (p >= 0 && p < lex.StreamCount.Length) foreach (var c in lex.Of(p)) if (c.Start < best && c.End > best) return b;
        return best;
    }

    // §7 E6's tail: the last copy's trailing insert is a Coda child of the copy when it is ≤ CodaShare of the piece and starts
    // no recurring family, else a section of its own. Its start takes the cell snap (against the source aligned to the cell's
    // part before it) and the direction snap; the matches the snap passes over leave the script (the coda branches from them).
    void Tail()
    {
        var last = Sections.LastOrDefault(s => s.Kind == CopyKind && s.End == grid.SpanEnd);
        if (last == null || last.Return < 0) return;
        var r = returns[last.Return];
        if (r.TailStart < 0) return;
        long tail = r.TailStart;
        long piece = grid.SpanEnd - grid.SpanStart;
        bool recurs = Paths.Any(p => p.Structural && Tick(p.Cols[0]) >= tail && Tick(p.Rows[^1]) < tail);
        if (grid.SpanEnd - tail <= settings.CodaShare * piece + Eps && !recurs)
        {
            var (sourceStart, sourceEnd) = AlignedBefore(r.Rows, r.Cols, tail);
            long snapped = CellSnap(tail, sourceStart, sourceEnd, last.Start, last.End);
            snapped = DirectionSnap(snapped, last.Start, last.End);
            if (snapped < tail) Retail(r, snapped);
            last.CodaStart = r.TailStart;
        }
        else
        {
            var own = new Section { Kind = ComplementKind, Start = tail, End = last.End };
            last.End = tail;
            var list = Sections.ToList(); list.Insert(last.Id + 1, own); Sections = list.ToArray();
            var cuts = Cuts.ToList(); cuts.Insert(last.Id + 1, PhraseCuts(own.Start, own.End, Internal(own))); Cuts = cuts.ToArray();
            for (int i = 0; i < Sections.Length; i++) Sections[i].Id = i;
            foreach (var x in returns) { if (x.Section > last.Id) x.Section++; if (x.SourceSection > last.Id) x.SourceSection++; }
            foreach (var s in Sections) { if (s.Prototype > last.Id) s.Prototype++; if (s.Family > last.Id) s.Family++; }
        }
    }
    // Moves a return's tail insert earlier to `tail`: the matches it passes over leave the script, and the source span ends
    // with the last match kept.
    void Retail(Return r, long tail)
    {
        var keep = Enumerable.Range(0, r.Cols.Length).Where(x => Tick(r.Cols[x]) < tail).ToArray();
        r.Rows = keep.Select(x => r.Rows[x]).ToArray(); r.Cols = keep.Select(x => r.Cols[x]).ToArray();
        var edits = new List<Edit>();
        foreach (var e in r.Script)
        {
            if (e.TargetStart >= tail) continue;
            var x = e;
            if (x.TargetEnd > tail)
            {
                // A match cut at the tail keeps the source columns matched before it.
                x.TargetEnd = tail;
                if (x.SourceStart >= 0) for (int y = r.Cols.Length - 1; y >= 0; y--) if (Tick(r.Cols[y]) >= e.TargetStart) { x.SourceEnd = TickEnd(r.Rows[y]); break; }
            }
            edits.Add(x);
        }
        while (edits.Count > 0 && edits[^1].Op == PreparedPatternSong.MotifEdits.Delete) edits.RemoveAt(edits.Count - 1);
        edits.Add(new Edit { Op = PreparedPatternSong.MotifEdits.Insert, SourceStart = -1, SourceEnd = -1, TargetStart = tail, TargetEnd = r.End });
        r.Script = edits.ToArray(); r.TailStart = tail;
        r.SourceEnd = r.Rows.Length > 0 ? TickEnd(r.Rows[^1]) : r.SourceStart;
    }

    // §7 E6's letters: sections by first appearance (A, B, …), each copy its prototype's letter primed by its order (A′, A″).
    void Letters()
    {
        int next = 0; var copies = new int[Sections.Length];
        foreach (var s in Sections)
        {
            if (s.Kind == IntroKind) { s.Letter = ""; s.Label = "Intro"; continue; }
            if (s.Kind == CopyKind && s.Prototype >= 0)
            {
                var p = Sections[s.Prototype]; s.Letter = p.Letter;
                s.Label = p.Letter + new string('′', ++copies[p.Id]);
                continue;
            }
            s.Letter = s.Label = next < 26 ? ((char)('A' + next)).ToString() : "S" + (next + 1);
            next++;
        }
    }

    // §7 E6: a complement that restates an earlier complement or hull (an occurrence §7 E4 would accept as a copy) takes that
    // section's letter as its copy.
    void ComplementReturns()
    {
        foreach (var s in Sections)
        {
            if (s.Kind != ComplementKind) continue;
            int t0 = ColumnOf(s.Start), t1 = ColumnOf(s.End - 1);
            foreach (var p in Sections)
            {
                if (p.Id >= s.Id || p.Kind is not (ComplementKind or PrototypeKind)) continue;
                int h0 = p.HullFirst >= 0 ? p.HullFirst : ColumnOf(p.Start), h1 = p.HullLast >= 0 ? p.HullLast : ColumnOf(p.End - 1);
                if (h1 < h0 || t1 < t0 || !CouldCopy(h0, h1, t0, t1)) continue;
                Return found = null;
                foreach (int k in Candidates(h0, h1, t0, t1))
                {
                    var (score, ops) = Align(h0, h1, t0, t1, k, freeStart: false);
                    if (ops == null || score <= Eps) continue;
                    // The complement must restate the section as a whole: its share counts all its columns, not the aligned part.
                    var (matched, _, prototypeShare) = Explained(ops, k, h0, h1);
                    double copyShare = 0; foreach (var o in ops) if (o.Kind == Match && Sk(o.Row, o.Col, k) >= Theta - Eps) copyShare += Beats(o.Col);
                    copyShare /= Math.Max(Eps, Beats(t0, t1));
                    if (matched < .5 * (h1 - h0 + 1) - Eps || copyShare < settings.CopyShare - Eps || prototypeShare < settings.PrototypeShare - Eps) continue;
                    found = MakeReturn(1, ops, k, h0, h1, t1, PhraseStarts(p));
                    break;
                }
                if (found == null) continue;
                found.Section = s.Id; found.SourceSection = p.Id;
                if (p.Kind == ComplementKind) { p.Kind = PrototypeKind; p.HullFirst = h0; p.HullLast = h1; }
                p.Family = p.Id; s.Kind = CopyKind; s.Prototype = p.Id; s.Family = p.Id; s.Return = found.Id;
                break;
            }
        }
    }

    // §7 E7: inside a section of at least eight bars, E3–E5 run again with the section's own L. Each accepted copy is a level-2
    // return of its hull, scripted with the section's arrival edges.
    List<Return> Internal(Section s)
    {
        var found = new List<Return>();
        int c0 = ColumnOf(s.Start), c1 = grid.ColumnAt(s.End - 1) + 1;
        long bar = BarLength(s.Start);
        if (c1 - c0 < 2 || s.End - s.Start < 8 * bar) return found;
        Trace?.Invoke($" section {Show(c0)}–{Show(c1 - 1)}: internal returns");
        var paths = DiscoverNode(c0, c1, out int minLag, out int barColumns);
        var taken = new bool[grid.Columns];
        var cuts = PhraseCuts(s.Start, s.End, found);
        foreach (var f in Families(c0, c1, paths, minLag, barColumns, taken))
        {
            var (starts, ends) = Edges(s.Start, s.End, cuts, f.HullFirst, f.HullLast);
            foreach (var o in f.Copies)
            {
                var ops = Clip(Canonical(f.HullFirst, f.HullLast, o.First, o.Last, o.K, starts, ends) ?? o.Ops);
                var r = MakeReturn(2, ops, o.K, f.HullFirst, f.HullLast, ops.Last(x => x.Col >= 0).Col, null);
                r.Section = r.SourceSection = s.Id;
                found.Add(r);
                Trace?.Invoke($"  internal return: {Describe(r)}");
            }
        }
        return found;
    }

    // An internal return's extent can overshoot by a shared bar (the étude's 6.2 = 14.2, where bar 14 is bar 6 with a new head):
    // each end of its source and its copy within a bar of an arrival's cut moves there (§8 F1's snapped return edge), and the ops
    // outside the snapped spans leave it, so a1′ ≅ a1 is the phrase with its one substitution at 9.1.
    List<Op> Clip(List<Op> ops)
    {
        long Snapped(long tick) { long cut = ArrivalNear(tick, long.MinValue, long.MaxValue); return cut >= 0 ? cut : tick; }
        long s0 = Snapped(Tick(ops.First(o => o.Row >= 0).Row)), s1 = Snapped(TickEnd(ops.Last(o => o.Row >= 0).Row));
        long t0 = Snapped(Tick(ops.First(o => o.Col >= 0).Col)), t1 = Snapped(TickEnd(ops.Last(o => o.Col >= 0).Col));
        var kept = ops.Where(o => (o.Row < 0 || Tick(o.Row) >= s0 && TickEnd(o.Row) <= s1) && (o.Col < 0 || Tick(o.Col) >= t0 && TickEnd(o.Col) <= t1)).ToList();
        while (kept.Count > 0 && kept[0].Kind != Match) kept.RemoveAt(0);
        while (kept.Count > 0 && kept[^1].Kind != Match) kept.RemoveAt(kept.Count - 1);
        return kept.Count == 0 ? ops : kept;
    }
    // The arrival cut nearest to tick within a bar of it and strictly inside (low, high); the later on a tie; −1 when none.
    long ArrivalNear(long tick, long low, long high)
    {
        long bar = BarLength(tick), best = -1;
        foreach (long cut in arrivalCuts)
            if (Math.Abs(cut - tick) <= bar && cut > low && cut < high && (best < 0 || Math.Abs(cut - tick) < Math.Abs(best - tick) || Math.Abs(cut - tick) == Math.Abs(best - tick) && cut > best)) best = cut;
        return best;
    }

    // §8 F1's cut sources for a section [start, end): arrivals (at the next tactus after the arrival note: full 1.0, half 0.8),
    // internal-return edges (both ends of each source and copy span: 0.9 when an arrival's cut lies within a bar, where it moves,
    // else 0.6) and, for pop, the section's phrase grid (0.3). Phrases last at least PhraseMinBars: of two cuts closer than that
    // the stronger stays, the later on a tie, and the section's own ends always win.
    Cut[] PhraseCuts(long start, long end, List<Return> inner, IEnumerable<long> phraseGrid = null)
    {
        var cand = new List<Cut>();
        if (phraseGrid != null) foreach (long t in phraseGrid) if (t > start && t < end) cand.Add(new Cut { Tick = t, Strength = .3 });
        for (int i = 0; i < arrivalCuts.Length; i++) if (arrivalCuts[i] > start && arrivalCuts[i] < end) cand.Add(new Cut { Tick = arrivalCuts[i], Strength = arrivalFull[i] ? 1 : .8, Arrival = true });
        foreach (var r in inner)
            foreach (long edge in new[] { r.SourceStart, r.SourceEnd, r.Start, r.TailStart >= 0 ? r.TailStart : r.End })
            {
                long snapped = ArrivalNear(edge, long.MinValue, long.MaxValue);
                long at = snapped >= 0 ? snapped : edge;
                if (at > start && at < end) cand.Add(new Cut { Tick = at, Strength = snapped >= 0 ? .9 : .6, Arrival = snapped >= 0 });
            }
        cand.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : b.Strength.CompareTo(a.Strength));
        var merged = new List<Cut>();
        foreach (var c in cand)
        {
            if (merged.Count > 0 && merged[^1].Tick == c.Tick) { var m = merged[^1]; m.Strength = Math.Max(m.Strength, c.Strength); m.Arrival |= c.Arrival; merged[^1] = m; continue; }
            merged.Add(c);
        }
        var kept = new List<Cut>();
        double Min(long tick) => settings.PhraseMinBars * BarLength(tick);
        foreach (var c in merged)
        {
            if (c.Tick - start < Min(c.Tick) - Eps || end - c.Tick < Min(c.Tick) - Eps) continue;
            if (kept.Count > 0 && c.Tick - kept[^1].Tick < Min(kept[^1].Tick) - Eps)
            {
                if (c.Strength >= kept[^1].Strength - Eps) kept[^1] = c;
                continue;
            }
            kept.Add(c);
        }
        return kept.ToArray();
    }
    long[] PhraseStarts(Section s) { var list = new List<long> { s.Start }; foreach (var c in Cuts[s.Id]) list.Add(c.Tick); list.Add(s.End); return list.ToArray(); }

    // §7 E5's edge weights per hull column: a phrase starting or ending there (the section's own ends and arrival-backed cuts 1,
    // cuts only an internal return gives 0.5). The phrase holding the hull's first column starts there, and the one holding its
    // last ends there, as the hull stands for the section.
    (double[] starts, double[] ends) Edges(Section s, int h0, int h1) => Edges(s.Start, s.End, Cuts[s.Id], h0, h1);
    (double[] starts, double[] ends) Edges(long start, long end, Cut[] cuts, int h0, int h1)
    {
        var starts = new double[h1 - h0 + 1]; var ends = new double[h1 - h0 + 1];
        var edges = new List<(long tick, double weight)> { (start, 1) };
        foreach (var c in cuts) edges.Add((c.Tick, c.Arrival ? 1 : .5));
        edges.Add((end, 1));
        for (int e = 0; e + 1 < edges.Count; e++)
        {
            // Phrase [edges[e], edges[e + 1]): its first and last hull columns.
            int a = -1, z = -1;
            for (int c = h0; c <= h1; c++) if (Tick(c) >= edges[e].tick && Tick(c) < edges[e + 1].tick) { if (a < 0) a = c; z = c; }
            if (a < 0) continue;
            starts[a - h0] = Math.Max(starts[a - h0], edges[e].weight);
            ends[z - h0] = Math.Max(ends[z - h0], edges[e + 1].weight);
        }
        return (starts, ends);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §7 E6: reviewed and pop forms.

    // data.Sections are level 1 verbatim. Returns relate only unions of consecutive sections: each pair of sections is aligned
    // globally at each transposition, a pair is a copy as §7 E4 accepts one, and runs of consecutive copied pairs at one
    // transposition become one relation (a recurring verse + chorus spans both). No section is the target of two relations: the
    // longer run first, then the later target (a group's reprise is read whole: Ticket to Ride's bridge, verse and chorus at
    // bars 63–87 restate 38–62), then the earlier source. Classical reviewed forms get internal returns too.
    void ReviewedForm(PreparedPatternSong data)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var list = new List<Section>();
        foreach (var x in data.Sections)
        {
            long s = MotifInput.Ticks(x.Start, input.Ppq), e = MotifInput.Ticks(x.End, input.Ppq);
            if (e <= s) continue;
            list.Add(new Section { Id = list.Count, Kind = ReviewedKind, Start = s, End = e, Letter = x.Letter ?? "", Label = x.DisplayName ?? "" });
        }
        Sections = list.ToArray();
        int n = Sections.Length;
        Trace?.Invoke($"returns (reviewed): θ {Theta:0.###}, {n} sections");
        var span = new (int first, int last)[n];
        for (int i = 0; i < n; i++) span[i] = (ColumnOf(Sections[i].Start), grid.ColumnAt(Sections[i].End - 1));
        var pairK = new int[n, n]; var copied = new bool[n, n];
        for (int p = 0; p < n; p++)
            for (int q = p + 1; q < n; q++)
            {
                var (h0, h1) = span[p]; var (t0, t1) = span[q];
                if (h1 < h0 || t1 < t0 || !CouldCopy(h0, h1, t0, t1)) continue;
                double best = NegInf; int bestK = 0; List<Op> bestOps = null;
                foreach (int t in Candidates(h0, h1, t0, t1))
                {
                    var (score, ops) = Align(h0, h1, t0, t1, t, freeStart: false, globalEnd: true);
                    if (ops != null && score > best + Eps) { best = score; bestK = t; bestOps = ops; }
                }
                if (bestOps == null) continue;
                var (matched, copyShare, prototypeShare) = Explained(bestOps, bestK, h0, h1);
                pairK[p, q] = bestK;
                copied[p, q] = matched >= .5 * (h1 - h0 + 1) - Eps && copyShare >= settings.CopyShare - Eps && prototypeShare >= settings.PrototypeShare - Eps;
                if (copied[p, q]) Trace?.Invoke($"  section {p} ~ {q} at k {bestK}");
            }
        var runs = new List<(int p, int q, int m, int k)>();
        for (int p = 0; p < n; p++)
            for (int q = p + 1; q < n; q++)
            {
                if (!copied[p, q]) continue;
                for (int m = 1; q + m - 1 < n && p + m - 1 < q && copied[p + m - 1, q + m - 1] && pairK[p + m - 1, q + m - 1] == pairK[p, q]; m++) runs.Add((p, q, m, pairK[p, q]));
            }
        Trace?.Invoke($"  ({clock.ElapsedMilliseconds} ms: section pairs)");
        runs.Sort((a, b) => a.m != b.m ? b.m.CompareTo(a.m) : a.q != b.q ? b.q.CompareTo(a.q) : a.p.CompareTo(b.p));
        var target = new bool[n];
        foreach (var (p, q, m, k) in runs)
        {
            bool free = true; for (int i = q; i < q + m && free; i++) free = !target[i];
            if (!free) continue;
            int h0 = span[p].first, h1 = span[p + m - 1].last, t0 = span[q].first, t1 = span[q + m - 1].last;
            var (_, ops) = Align(h0, h1, t0, t1, k, freeStart: false, globalEnd: true);
            if (ops == null) continue;
            var r = MakeReturn(1, ops, k, h0, h1, t1, null);
            r.Reviewed = true; r.Section = q; r.LastSection = q + m - 1; r.SourceSection = p; r.LastSourceSection = p + m - 1;
            for (int i = 0; i < m; i++) { target[q + i] = true; Sections[q + i].Prototype = p + i; Sections[q + i].Return = r.Id; }
            Trace?.Invoke($"  return: {Describe(r)}");
        }
        Cuts = new Cut[n][];
        bool classical = data.Style != "pop";
        for (int i = 0; i < n; i++)
        {
            // Pop sections also cut on their phrase grid (Section.PhraseBars).
            var phraseGrid = new List<long>(); int bars = classical || i >= data.Sections.Length ? 0 : data.Sections[i].PhraseBars;
            for (int b = Math.Max(0, grid.BarAt(Sections[i].Start)) + bars; bars > 0 && b < grid.Bars && grid.BarStart[b] < Sections[i].End; b += bars) phraseGrid.Add(grid.BarStart[b]);
            Cuts[i] = PhraseCuts(Sections[i].Start, Sections[i].End, classical ? Internal(Sections[i]) : new List<Return>(), phraseGrid);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Reports.

    // "[1.1–5.2]~[62.1–66.2] del[6.1–13.2] [14.1–19.2]~[67.1–72.2] ins[73.1–77.2]" (printed bars and tactus beats).
    public string Describe(Return r)
    {
        string B(long tick, bool end)
        {
            if (end) tick -= 1;
            int c = grid.ColumnAt(tick); return c < 0 ? "?" : Show(c);
        }
        var parts = new List<string>();
        foreach (var e in r.Script)
            parts.Add(e.Op switch
            {
                PreparedPatternSong.MotifEdits.Match => $"[{B(e.SourceStart, false)}–{B(e.SourceEnd, true)}]~[{B(e.TargetStart, false)}–{B(e.TargetEnd, true)}]",
                PreparedPatternSong.MotifEdits.Substitute => $"sub[{B(e.SourceStart, false)}–{B(e.SourceEnd, true)}]~[{B(e.TargetStart, false)}–{B(e.TargetEnd, true)}]",
                PreparedPatternSong.MotifEdits.Delete => $"del[{B(e.SourceStart, false)}–{B(e.SourceEnd, true)}]",
                _ => $"ins[{B(e.TargetStart, false)}–{B(e.TargetEnd, true)}]"
            });
        return string.Join(" ", parts) + (r.K != 0 ? $" (k {r.K})" : "");
    }
    // "A [4, 45.5) | B [45.5, 126.5) | A′ [126.5, 158.5) coda 148.5" (bundle beats, whatever the culture).
    public string DescribeSections() => string.Join(" | ", Sections.Select(s =>
        FormattableString.Invariant($"{(s.Label.Length > 0 ? s.Label : "?")} [{s.Start / (double)input.Ppq:0.###}, {s.End / (double)input.Ppq:0.###}){(s.CodaStart >= 0 ? FormattableString.Invariant($" coda {s.CodaStart / (double)input.Ppq:0.###}") : "")}")));
}
