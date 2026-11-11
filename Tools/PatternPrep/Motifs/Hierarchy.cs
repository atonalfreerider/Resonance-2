using System.Text;
using Kinds = PreparedPatternSong.MotifKinds;

// Stage F of the motif reduction (Docs/MOTIF-REDUCTION.md §8): the tree the structure wheel draws, from stage E's sections
// down to stage D's units.
// - F1: a returning (or pop) section is cut into phrases at stage E's cut sources (arrivals, internal-return edges, a pop
//   section's phrase grid); a phrase an internal return restates is a primed copy of its source.
// - F2: any other section is cut into periods by a hypermetric DP over generator-label changes, arrivals and internal-return
//   edges; texture novelty is no input.
// - F3: a long phrase splits into sub-phrases where the principal line's family changes for good; the tandem blocks inside a
//   phrase, sub-phrase or period are its units: power nodes, nested, with their iterations. Cells no block covers stay cells.
// - F4: a copy inherits its prototype's subtree through its return's canonical script: children the script deletes are
//   omitted, varied or truncated ones primed; inserted spans (the coda) are analysed afresh.
// - F5: a unit or bar no power or return explains is matched with earlier spans of its length: a cross-reference.
// - F6: labels, names, each node's formula line, the formula and the caption's piece part; F7: the flattened array.
// Everything stays in score ticks; the export (§9 G5) converts to beats and maps the indices onto the bundle's relations.
public sealed class Hierarchy
{
    const double Eps = 1e-9;
    // §8 F2's evidence of a period cut: a generator-label change, a full or half arrival, an internal return's edge; a marked
    // direction adds its bonus. PeriodScale weighs the pull of P towards four periods a section.
    const double LabelEvidence = .6, FullEvidence = 1, HalfEvidence = .8, ReturnEvidence = .9, DirectionBonus = .3, PeriodScale = .03;
    const int SighFamily = -2, MaxTokens = 12;   // a formula line names at most MaxTokens items, then "…"
    const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹", Subscripts = "₀₁₂₃₄₅₆₇₈₉", UnitLetters = "UVWXYZ";

    // One node (MotifNode, in ticks). Section: its stage E section (level 1); Return: the Returns.All index of the return that
    // makes it a copy (a copy section, a primed phrase); Power: the Iteration.Relations index of the power it states;
    // CrossReference: the cross-reference naming it. Source: the node it restates (Prototype once flattened). Exact: a
    // projected node restates its prototype unedited. StepReal and StepInverted: a power's step (Real and Inverted describe
    // the restatement of the prototype).
    public sealed class Node
    {
        public int Id = -1, Parent = -1, FirstChild = -1, ChildCount, Depth, Level, Kind;
        public long Start, End;
        public string Label = "", Name = "", Text = "", Omits = "";
        public int Prototype = -1, Paradigm = -1, Steps, Semitones;
        public bool Inverted, Real, Projected, Exact = true, StepReal, StepInverted;
        public int Count = 1, Iteration, StepSteps, StepSemitones, Order;
        public double Evidence = 1;
        public int Section = -1, Return = -1, Power = -1, CrossReference = -1;
        public Node Source;
        public readonly List<Node> Children = new();
        internal string Letter = "";
    }

    // A cross-reference (§8 F5): [Start, End) restates [SourceStart, SourceEnd) under G on the principal line, Share of the
    // columns related, Altered notes off the image. Node: the unit stating it, or the innermost node holding the bar; Source:
    // the node of exactly the source span (−1 when none). Level: the node's.
    public sealed class CrossReference
    {
        public int Id, Node = -1, Source = -1, Level, Altered;
        public long Start, End, SourceStart, SourceEnd;
        public Transform G = Transform.Identity;
        public double Share;
        public string Text = "";
        internal Node Target;
    }

    public static readonly string[] Levels = { "Piece", "Section", "Phrase", "Unit" };   // MotifLayer.Levels: names of Node.Level 0–3 (§8 F7)
    public Node[] Nodes = Array.Empty<Node>();                 // preorder, children contiguous (§8 F7)
    public CrossReference[] CrossReferences = Array.Empty<CrossReference>();
    public string[] ReturnText = Array.Empty<string>();         // per Returns.All: the musician's sentence
    public int[] ReturnNode = Array.Empty<int>(), ReturnSource = Array.Empty<int>();   // per Returns.All: the copy's node and its prototype's (−1: none)
    public int[] PowerNode = Array.Empty<int>();                // per Iteration.Relations: the node stating that power (−1: a relation only)
    public string Formula = "", Line = "";                      // the formula lines; Line: the caption's piece part (§9 G3)
    public static Action<string> Trace;

    MotifInput input; MotifGrid grid; MotifSettings settings; CellLexicon lex; Iteration it; Returns ret;
    CellLexicon.Cell[] cells = Array.Empty<CellLexicon.Cell>();   // the principal stream's, in time order
    int[] cellPower = Array.Empty<int>();                          // per lexicon cell: the principal cell power it belongs to (−1)
    List<int>[] nested = Array.Empty<List<int>>();                 // per relation: the unit powers nested in it
    sbyte[] falls = Array.Empty<sbyte>();                          // per paradigm: its prototype falls (1), not (−1), unknown (0)
    Node root; Node[] sections = Array.Empty<Node>();
    readonly List<CrossReference> crossReferences = new();
    readonly List<string> stems = new(); readonly List<int> stemCount = new();   // phrase labels used per stem
    int letters;
    // The principal line per column (§8 F5): the leads struck in it (their slot from the column start) and the lead held into it.
    struct LineNote { public int Slot, Pitch, Morph, Alter; public long Tick; public bool Tied; }
    int[] lineFirst = Array.Empty<int>(), lineSize = Array.Empty<int>(); LineNote[] line = Array.Empty<LineNote>();

    // data: its Title names the piece and its Style == "pop" cuts every section into phrases (§8 F1).
    public static Hierarchy Build(MotifInput input, CellLexicon lex, Iteration it, Returns ret, PreparedPatternSong data = null)
    {
        var h = new Hierarchy { input = input, grid = input.Grid, settings = input.Settings, lex = lex, it = it, ret = ret };
        h.Prepare();
        h.root = new Node { Kind = Kinds.Piece, Start = h.grid.SpanStart, End = h.grid.SpanEnd, Label = string.IsNullOrWhiteSpace(data?.Title) ? "Piece" : data.Title.Trim() };
        int n = ret.Sections.Length; h.sections = new Node[n];
        for (int i = 0; i < n; i++)
        {
            var s = ret.Sections[i];
            h.sections[i] = new Node { Kind = s.Kind == Returns.IntroKind ? Kinds.Intro : Kinds.Section, Level = 1, Start = s.Start, End = s.End, Label = s.Label, Section = i };
            h.root.Children.Add(h.sections[i]);
        }
        bool pop = data?.Style == "pop";
        for (int i = 0; i < n; i++) if (!h.IsCopy(i)) h.Segment(i, pop);
        for (int i = 0; i < n; i++) if (h.IsCopy(i)) h.Copy(i);   // a prototype always precedes its copies
        h.LabelUnits(h.root);
        h.LabelProjected(h.root);
        h.CrossReferencesOf();
        h.Describe(h.root);
        h.Flatten();
        h.Write();
        return h;
    }

    void Prepare()
    {
        int p = input.Streams.Principal;
        cells = p >= 0 && p < lex.StreamCount.Length ? lex.Of(p).ToArray() : Array.Empty<CellLexicon.Cell>();
        cellPower = new int[lex.Cells.Length]; Array.Fill(cellPower, -1);
        nested = new List<int>[it.Relations.Length]; falls = new sbyte[lex.Paradigms.Length];
        foreach (var r in it.Relations)
        {
            if (r.IsPower && r.Source == Iteration.FromCells && r.StreamA == p) foreach (int c in r.Cells) if (c >= 0 && c < cellPower.Length && cellPower[c] < 0) cellPower[c] = r.Id;
            if (UnitPower(r) && r.Parent >= 0) (nested[r.Parent] ??= new List<int>()).Add(r.Id);
        }
    }
    static bool UnitPower(Iteration.Relation r) => r.IsPower && r.Source != Iteration.FromCells;
    bool IsCopy(int i) => ret.Sections[i].Prototype >= 0 && ret.Sections[i].Return >= 0;
    // A section returns when it is a family's prototype or the source of a reviewed return; pop sections all take phrases.
    bool Returning(int i, bool pop)
    {
        if (pop || ret.Sections[i].Kind == Returns.PrototypeKind) return true;
        foreach (var r in ret.All) if (r.Level == 1 && i >= r.SourceSection && i <= Math.Max(r.SourceSection, r.LastSourceSection)) return true;
        return false;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Grid helpers.

    int FirstColumnAt(long tick) { int c = Math.Max(0, grid.ColumnAt(tick)); return c < grid.Columns && grid.ColumnStart[c] < tick ? c + 1 : c; }
    // Bars from the grid's start, fractional inside a bar: lengths in bars whatever the meter.
    double BarPos(long tick)
    {
        if (grid.Bars == 0) return tick / (double)Math.Max(1, 4 * grid.Ppq);
        int b = Math.Clamp(grid.BarAt(tick), 0, grid.Bars - 1);
        return b + (tick - grid.BarStart[b]) / (double)Math.Max(1, grid.BarEnd[b] - grid.BarStart[b]);
    }
    double Bars(long a, long b) => BarPos(b) - BarPos(a);
    int FirstCell(long tick) { int lo = 0, hi = cells.Length; while (lo < hi) { int mid = (lo + hi) >> 1; if (cells[mid].Start < tick) lo = mid + 1; else hi = mid; } return lo; }
    // The paradigm of the principal cell spanning exactly [a, b) (−1: none, or a literal cell).
    int SingleCell(long a, long b) { int k = FirstCell(a); return k < cells.Length && cells[k].Start == a && cells[k].End == b ? cells[k].Paradigm : -1; }
    bool InsideUnitPower(long tick) { foreach (var r in it.Relations) if (UnitPower(r) && r.Start < tick && tick < r.End) return true; return false; }
    // "b.k" (printed bar, tactus beat), as the reports write positions.
    string Position(long tick)
    {
        int b = Math.Max(0, grid.BarAt(tick)); long off = tick - grid.BarStart[b];
        return FormattableString.Invariant($"{grid.Printed[b]}.{off / Math.Max(1, grid.Unit[b]) + 1}");
    }
    // "bar 16", "bars 6–13", or "16.1" and "16.1–17.2" for spans that are not whole bars.
    string Where(long a, long b)
    {
        if (grid.Bars == 0) return Position(a);
        int ba = Math.Max(0, grid.BarAt(a)), bb = Math.Max(0, grid.BarAt(b - 1));
        if (a == grid.BarStart[ba] && b == grid.BarEnd[bb]) return ba == bb ? $"bar {grid.Printed[ba]}" : $"bars {grid.Printed[ba]}–{grid.Printed[bb]}";
        int last = Math.Max(0, grid.ColumnAt(b - 1));
        return grid.ColumnStart[last] <= a ? Position(a) : Position(a) + "–" + Position(grid.ColumnStart[last]);
    }
    string BarRange(long a, long b) { int ba = Math.Max(0, grid.BarAt(a)), bb = Math.Max(0, grid.BarAt(b - 1)); return ba == bb ? $"{grid.Printed[ba]}" : $"{grid.Printed[ba]}–{grid.Printed[bb]}"; }
    // The lower-case stem of a section's phrase labels (a1, a2) and the upper-case one of its periods (B1, B2).
    static string Stem(Returns.Section s, bool lower)
    {
        string letter = s.Letter.Length > 0 ? s.Letter : s.Label.Length > 0 && char.IsLetter(s.Label[0]) ? s.Label[..1] : "P";
        return lower ? letter.ToLowerInvariant() : letter.ToUpperInvariant();
    }
    string NextPhrase(string stem)
    {
        int i = stems.IndexOf(stem); if (i < 0) { stems.Add(stem); stemCount.Add(0); i = stems.Count - 1; }
        return stem + ++stemCount[i];
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §8 F1, F2: phrases and periods.

    void Segment(int i, bool pop)
    {
        var s = sections[i]; var sec = ret.Sections[i];
        if (!Returning(i, pop)) { Periods(s, i); return; }
        var edges = new List<(long tick, double strength)> { (s.Start, 1) };
        foreach (var c in ret.Cuts[i]) if (c.Tick > s.Start && c.Tick < s.End) edges.Add((c.Tick, c.Strength));
        edges.Add((s.End, 1));
        for (int k = 0; k + 1 < edges.Count; k++) s.Children.Add(new Node { Kind = Kinds.Phrase, Level = 2, Start = edges[k].tick, End = edges[k + 1].tick, Evidence = edges[k].strength });
        InternalCopies(i, s.Children);
        string stem = Stem(sec, true);
        foreach (var p in s.Children)
        {
            if (p.Source == null) { p.Label = NextPhrase(stem); Analyse(p, true); continue; }
            int primes = 1; foreach (var q in s.Children) { if (q == p) break; if (q.Source == p.Source) primes++; }
            p.Label = p.Source.Label + new string('′', primes);
            p.Children.AddRange(Images(p.Source.Children, MapOf(p.Return), p.Start, p.End, null, null));   // a copy is never re-segmented
        }
    }

    // §7 E7's internal returns make phrases copies (a1′): a phrase at least half of whose columns an internal return restates,
    // from columns lying mostly in one earlier phrase, is that phrase's copy.
    void InternalCopies(int i, List<Node> phrases)
    {
        var votes = new int[phrases.Count];
        for (int x = 0; x < ret.All.Length; x++)
        {
            var r = ret.All[x]; if (r.Level != 2 || r.Section != i) continue;
            foreach (var t in phrases)
            {
                if (t.Source != null) continue;
                int columns = 0, matched = 0; Array.Clear(votes);
                for (int c = FirstColumnAt(t.Start); c < grid.Columns && grid.ColumnStart[c] < t.End; c++) columns++;
                for (int y = 0; y < r.Cols.Length; y++)
                {
                    long at = grid.ColumnStart[r.Cols[y]]; if (at < t.Start || at >= t.End) continue;
                    matched++; long from = grid.ColumnStart[r.Rows[y]];
                    for (int q = 0; q < phrases.Count; q++) if (from >= phrases[q].Start && from < phrases[q].End) { votes[q]++; break; }
                }
                if (columns == 0 || 2 * matched < columns) continue;
                int best = 0; for (int q = 1; q < phrases.Count; q++) if (votes[q] > votes[best]) best = q;
                if (votes[best] == 0 || phrases[best].Start >= t.Start) continue;
                t.Source = phrases[best]; t.Return = x;
            }
        }
    }

    // §8 F2: a section that does not return, cut into periods. Candidates: generator-label changes (§6 D9; a generator that
    // starts inside the bar before moves its cut to its start, so a period starts with its generator), arrivals and
    // internal-return edges, the strongest where they meet, a marked direction adding its bonus; none strictly inside a unit
    // power (a period holds whole powers). The DP then keeps the cuts maximising J.
    void Periods(Node s, int i)
    {
        var sec = ret.Sections[i];
        var cand = new List<(long tick, double ev)>();
        for (int b = Math.Max(1, grid.FirstBar + 1); b < grid.Bars; b++)
        {
            long at = grid.BarStart[b]; if (at <= s.Start) continue; if (at >= s.End) break;
            var label = it.Labels[b];
            if (label.Text.Length == 0 || label.Text == it.Labels[b - 1].Text) continue;
            if (label.Relation >= 0)
            {
                long start = it.Relations[label.Relation].Start;
                if (start > grid.BarStart[b - 1] && start < at && start > s.Start) at = grid.ColumnStart[Math.Max(0, grid.ColumnAt(start))];
            }
            cand.Add((at, LabelEvidence));
        }
        for (int k = 0; k < ret.ArrivalCuts.Length; k++) if (ret.ArrivalCuts[k] > s.Start && ret.ArrivalCuts[k] < s.End) cand.Add((ret.ArrivalCuts[k], ret.ArrivalFull[k] ? FullEvidence : HalfEvidence));
        foreach (var r in ret.All)
            if (r.Level == 2 && r.Section == i)
                foreach (long e in new[] { r.SourceStart, r.SourceEnd, r.Start, r.TailStart >= 0 ? r.TailStart : r.End })
                    if (e > s.Start && e < s.End) cand.Add((e, ReturnEvidence));
        cand.Sort((a, b) => a.tick != b.tick ? a.tick.CompareTo(b.tick) : b.ev.CompareTo(a.ev));
        var ticks = new List<long>(); var ev = new List<double>(); long last = long.MinValue;
        foreach (var (tick, e) in cand)
        {
            if (tick == last) continue;   // the strongest came first
            last = tick;
            if (InsideUnitPower(tick)) continue;
            double bonus = 0; long tactus = grid.TactusAt(tick);
            foreach (long d in ret.Directions) if (Math.Abs(d - tick) <= tactus) { bonus = DirectionBonus; break; }
            ticks.Add(tick); ev.Add(e + bonus);
        }
        var bounds = Partition(s, ticks, ev, out var evidence);
        string stem = Stem(sec, false);
        for (int k = 0; k + 1 < bounds.Count; k++)
        {
            var p = new Node { Kind = Kinds.Period, Level = 2, Start = bounds[k], End = bounds[k + 1], Label = stem + (k + 1), Evidence = evidence[k] };
            Analyse(p, false);
            s.Children.Add(p);
        }
    }

    // §8 F2's DP: J = Σ ev − λ·#cuts − κ·Σ ((len − P)/P)² over periods of at least PhraseMinBars bars, λ = PeriodLambda·the
    // median evidence, P from Period. Returns the period bounds (the section's ends included) and each period's evidence.
    List<long> Partition(Node s, List<long> ticks, List<double> ev, out List<double> evidence)
    {
        var bounds = new List<long> { s.Start }; evidence = new List<double> { 1 };
        int m = ticks.Count;
        if (m > 0)
        {
            double x0 = BarPos(s.Start), x1 = BarPos(s.End);
            var xs = new double[m]; for (int k = 0; k < m; k++) xs[k] = BarPos(ticks[k]);
            var sorted = ev.ToArray(); Array.Sort(sorted);
            double median = m % 2 == 1 ? sorted[m / 2] : (sorted[m / 2 - 1] + sorted[m / 2]) / 2, lambda = settings.PeriodLambda * median;
            double P = Period(s.Start, x0, x1, xs, ev);
            var best = new double[m + 2]; var from = new int[m + 2];
            Array.Fill(best, double.NegativeInfinity); best[0] = 0;
            double X(int j) => j == 0 ? x0 : j == m + 1 ? x1 : xs[j - 1];
            for (int j = 1; j <= m + 1; j++)
                for (int q = 0; q < j; q++)
                {
                    if (double.IsNegativeInfinity(best[q])) continue;
                    double length = X(j) - X(q); if (length < settings.PhraseMinBars - Eps) continue;
                    double off = (length - P) / P, v = best[q] - settings.PeriodKappa * off * off + (j <= m ? ev[j - 1] - lambda : 0);
                    if (v > best[j] + Eps) { best[j] = v; from[j] = q; }
                }
            if (!double.IsNegativeInfinity(best[m + 1]))
            {
                var chosen = new List<int>();
                for (int j = from[m + 1]; j > 0; j = from[j]) chosen.Add(j);
                chosen.Reverse();
                foreach (int j in chosen) { bounds.Add(ticks[j - 1]); evidence.Add(ev[j - 1]); }
            }
            Trace?.Invoke(FormattableString.Invariant($"  periods of {s.Label}: P {P} bars, λ {lambda:0.##}; candidates {string.Join(" ", ticks.Select((t, k) => $"{Position(t)}:{ev[k]:0.##}"))}; cuts {string.Join(" ", bounds.Skip(1).Select(Position))}"));
        }
        bounds.Add(s.End);
        return bounds;
    }

    // §8 F2's P: the period in bars, over phases in tactus steps, maximising the mean over its grid points (those lying in the
    // section, its ends included) of the most evidence within a bar, less 0.03·|log2(L/(4P))|. The section's own ends are
    // evidence 1 there: they are certain cuts. Without them the mean is highest for the sparsest grid through the two strongest
    // cuts: the étude's B takes P = 16 through bars 30 and 46 (0.75 against P = 8's 0.60), and the DP keeps three periods
    // against §8 F2's five. With them P = 8 and P = 16 both reach 0.83 there, and the log term chooses 8.
    double Period(long start, double x0, double x1, double[] xs, List<double> ev)
    {
        int bar = Math.Clamp(grid.BarAt(start), 0, Math.Max(0, grid.Bars - 1));
        double step = grid.Bars == 0 ? 1 : grid.Unit[bar] / (double)Math.Max(1, grid.BarEnd[bar] - grid.BarStart[bar]);
        double best = double.NegativeInfinity, chosen = settings.PeriodBars.Length > 0 ? settings.PeriodBars[0] : 4;
        foreach (int period in settings.PeriodBars)
        {
            if (period <= 0) continue;
            for (int q = 0; q * step < period - Eps; q++)
            {
                double sum = 0; int points = 0;
                for (int j = 0; ; j++)
                {
                    double x = x0 + q * step + j * period; if (x > x1 + Eps) break;
                    double e = Math.Abs(x - x0) <= 1 + Eps || Math.Abs(x1 - x) <= 1 + Eps ? 1 : 0;
                    for (int k = 0; k < xs.Length; k++) if (Math.Abs(xs[k] - x) <= 1 + Eps) e = Math.Max(e, ev[k]);
                    sum += e; points++;
                }
                if (points == 0) continue;
                double score = sum / points - PeriodScale * Math.Abs(Math.Log2(Math.Max(Eps, x1 - x0) / (4.0 * period)));
                if (score > best + Eps) { best = score; chosen = period; }
            }
        }
        return chosen;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §8 F3: sub-phrases and units.

    // Inside a phrase, a coda or an inserted span: sub-phrases where the principal line's family changes for good; then each
    // part's units. A period gets units only: its cut already follows the generators.
    void Analyse(Node p, bool subPhrases)
    {
        var cuts = subPhrases ? SubPhraseCuts(p) : new List<long>();
        if (cuts.Count == 0) { p.Children.AddRange(Units(p.Start, p.End)); return; }
        cuts.Add(p.End);
        long at = p.Start; char letter = 'a';
        string stem = p.Label + (p.Label.Length > 0 && char.IsLetter(p.Label[^1]) ? " " : "");
        foreach (long cut in cuts)
        {
            var part = new Node { Kind = Kinds.SubPhrase, Level = 3, Start = at, End = cut, Label = stem + letter++ };
            part.Children.AddRange(Units(at, cut));
            p.Children.Add(part); at = cut;
        }
    }

    // A cut where the principal line's family changes and the new family holds FamilyHoldBars (literal cells neither break nor
    // extend it), inside a phrase of SubPhraseBars or more, each part at least PhraseMinBars, never inside a unit power. A
    // family is the atom of the cell's paradigm, except that every figure opened by a held head and falling (§5 C7's sigh) is
    // one family whatever its first token: the étude's bar 18 (D♯5[N+ Rep] C♯5 G♯4) reduces to an upper neighbour, yet goes on
    // with the sigh of bar 17, and a3 splits at 17.1 only so (Leichtentritt's 7 = 3 + 4).
    List<long> SubPhraseCuts(Node p)
    {
        var cuts = new List<long>();
        if (Bars(p.Start, p.End) < settings.SubPhraseBars - Eps) return cuts;
        long sub = p.Start; int previous = int.MinValue;
        for (int k = FirstCell(p.Start); k < cells.Length && cells[k].Start < p.End; k++)
        {
            int f = Family(cells[k]); if (f == -1) continue;
            if (previous != int.MinValue && f != previous)
            {
                long at = cells[k].Start, held = at;
                for (int j = k; j < cells.Length && cells[j].Start < p.End; j++) { int g = Family(cells[j]); if (g == -1) continue; if (g != f) break; held = Math.Min(p.End, cells[j].End); }
                if (Bars(at, held) >= settings.FamilyHoldBars - Eps && Bars(sub, at) >= settings.PhraseMinBars - Eps && Bars(at, p.End) >= settings.PhraseMinBars - Eps && !InsideUnitPower(at))
                { cuts.Add(at); sub = at; }
            }
            previous = f;
        }
        return cuts;
    }
    int Family(CellLexicon.Cell c)
    {
        if (c.Paradigm < 0) return -1;
        var p = lex.Paradigms[c.Paradigm];
        if (falls[p.Id] == 0)
        {
            var proto = lex.Cells[p.Prototype]; var notes = CellLexicon.Notes(input, input.Streams[proto.Stream], proto.W);
            falls[p.Id] = (sbyte)(notes.Length >= 2 && notes[^1].Pitch < notes[0].Pitch ? 1 : -1);
        }
        return p.HeldHead && falls[p.Id] > 0 ? SighFamily : p.Atom;
    }

    // The unit powers lying wholly inside [c0, c1) that no enclosing unit power does (§8 F3): a block crossing an edge is a unit
    // of neither side (the étude's soft 8-bar repeat over bars 1–16), but the blocks inside its units may be.
    List<Node> Units(long c0, long c1)
    {
        var list = new List<Node>();
        foreach (var r in it.Relations)
        {
            if (!UnitPower(r) || r.Start < c0 || r.End > c1) continue;
            bool top = true;
            for (int q = r.Parent; q >= 0 && top; q = it.Relations[q].Parent) top = !(it.Relations[q].Start >= c0 && it.Relations[q].End <= c1);
            if (top) list.Add(PowerOf(r));
        }
        list.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End != b.End ? b.End.CompareTo(a.End) : a.Power.CompareTo(b.Power));
        return list;
    }

    // A power as a node (§6 D3): its iterations are its children, each the nested power filling it or a unit holding the powers
    // nested inside it; a later iteration restates the first under the cumulative step (Iteration: its index).
    Node PowerOf(Iteration.Relation r)
    {
        var n = new Node { Kind = Kinds.Power, Level = 3, Start = r.Start, End = r.End, Count = r.Count, StepSteps = r.Step.Steps, StepSemitones = r.Step.Semitones, Order = r.Order,
            StepReal = r.Real, StepInverted = r.Step.Inverted, Real = r.Real, Power = r.Id, Evidence = r.Exactness };
        Node first = null;
        for (int k = 0; k < r.Count; k++)
        {
            long a = r.Iterations[k], b = k + 1 < r.Count ? r.Iterations[k + 1] : r.End;
            Node child = null; var inside = new List<Node>();
            if (nested[r.Id] != null)
                foreach (int q in nested[r.Id])
                {
                    var x = it.Relations[q];
                    if (x.Start == a && x.End == b && child == null) child = PowerOf(x);
                    else if (x.Start >= a && x.End <= b) inside.Add(PowerOf(x));
                }
            if (child == null)
            {
                child = new Node { Kind = Kinds.Unit, Level = 3, Start = a, End = b, Paradigm = SingleCell(a, b) };
                inside.Sort((u, v) => u.Start != v.Start ? u.Start.CompareTo(v.Start) : u.Power.CompareTo(v.Power));
                child.Children.AddRange(inside);
            }
            child.Iteration = k;
            if (k == 0) first = child;
            else { child.Source = first; child.Steps = k * r.Step.Steps; child.Semitones = k * r.Step.Semitones; child.Inverted = r.Step.Inverted && k % 2 == 1; child.Real = r.Real; }
            n.Children.Add(child);
        }
        return n;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §8 F4: projection.

    // A return as a map of columns: To, the target column each source column is matched to (−1); Considered, the source
    // columns its alignment accounts for (a family's hull, the reviewed sections, or the span its script covers); Deleted;
    // Varied, the target columns not restated unedited (substitutions, and columns inserted before its tail).
    sealed class Map
    {
        public int K;
        public int[] To;
        public bool[] Considered, Deleted, Varied;
    }
    Map MapOf(int index)
    {
        var r = ret.All[index]; int n = grid.Columns;
        var m = new Map { K = r.K, To = new int[n], Considered = new bool[n], Deleted = new bool[n], Varied = new bool[n] };
        Array.Fill(m.To, -1);
        for (int x = 0; x < r.Rows.Length; x++) m.To[r.Rows[x]] = r.Cols[x];
        void Mark(bool[] set, long from, long to) { for (int c = FirstColumnAt(from); c < n && grid.ColumnStart[c] < to; c++) set[c] = true; }
        var source = r.SourceSection >= 0 && r.SourceSection < ret.Sections.Length ? ret.Sections[r.SourceSection] : null;
        if (r.Level == 1 && !r.Reviewed && source != null && source.HullFirst >= 0) for (int c = source.HullFirst; c <= source.HullLast && c < n; c++) m.Considered[c] = true;
        else if (r.Reviewed) for (int s = r.SourceSection; s <= Math.Max(r.SourceSection, r.LastSourceSection) && s < ret.Sections.Length; s++) Mark(m.Considered, ret.Sections[s].Start, ret.Sections[s].End);
        foreach (var e in r.Script)
        {
            if (e.Op == PreparedPatternSong.MotifEdits.Delete) { Mark(m.Deleted, e.SourceStart, e.SourceEnd); if (!r.Reviewed && r.Level == 2) Mark(m.Considered, e.SourceStart, e.SourceEnd); }
            else if (e.Op is PreparedPatternSong.MotifEdits.Match or PreparedPatternSong.MotifEdits.Substitute)
            {
                if (r.Level == 2) Mark(m.Considered, e.SourceStart, e.SourceEnd);
                if (e.Op == PreparedPatternSong.MotifEdits.Substitute) Mark(m.Varied, e.TargetStart, e.TargetEnd);
            }
        }
        var matched = new bool[n]; foreach (int c in r.Cols) matched[c] = true;
        long tail = r.TailStart >= 0 ? r.TailStart : r.End;
        for (int c = FirstColumnAt(r.Start); c < n && grid.ColumnStart[c] < tail; c++) if (!matched[c]) m.Varied[c] = true;
        return m;
    }

    // §8 F4: a copy section restates its prototype's subtree through its return; the children the script deletes are its
    // Omits; its coda (the tail insert) and any longer inserted span are analysed afresh (F1–F3).
    void Copy(int i)
    {
        var sec = ret.Sections[i]; var s = sections[i]; var r = ret.All[sec.Return]; var proto = sections[sec.Prototype];
        s.Source = proto; s.Return = sec.Return; Transpose(s, r.K);
        long end = sec.CodaStart > s.Start && sec.CodaStart < s.End ? sec.CodaStart : s.End;
        var omitted = new List<string>();
        s.Children.AddRange(Images(proto.Children, MapOf(sec.Return), s.Start, end, Stem(ret.Sections[sec.Prototype], true), omitted));
        if (end < s.End)
        {
            var coda = new Node { Kind = Kinds.Coda, Level = 2, Start = end, End = s.End, Label = "Coda" };
            Analyse(coda, true);
            s.Children.Add(coda);
        }
        s.Omits = string.Join(", ", omitted);
    }

    // The images of a prototype's children through a return, inside [t0, t1): each child's matched columns. A child with none
    // is omitted (named in `omitted` when the script deletes every column of it the alignment accounts for). Phrases and
    // sub-phrases tile their copy as they tile their prototype: a gap of inserted columns joins the image before it, or, among
    // a copy section's phrases (stem set), becomes a phrase of its own, analysed afresh, when it lasts PhraseMinBars or more.
    // Units keep their own images.
    List<Node> Images(List<Node> kids, Map map, long t0, long t1, string stem, List<string> omitted)
    {
        var result = new List<Node>(); if (kids.Count == 0 || t1 <= t0) return result;
        var images = new List<(Node source, long a, long b)>();
        foreach (var x in kids)
        {
            int first = -1, last = -1, aligned = 0, deleted = 0;
            for (int c = FirstColumnAt(x.Start); c < grid.Columns && grid.ColumnStart[c] < x.End; c++)
            {
                if (map.Considered[c]) { aligned++; if (map.Deleted[c]) deleted++; }
                int v = map.To[c]; if (v < 0) continue;
                if (first < 0 || v < first) first = v;
                if (v > last) last = v;
            }
            if (first < 0) { if (omitted != null && aligned > 0 && deleted == aligned) omitted.Add(x.Label); continue; }
            images.Add((x, grid.ColumnStart[first], grid.ColumnEnd[last]));
        }
        bool tiled = kids[0].Kind is Kinds.Phrase or Kinds.Period or Kinds.SubPhrase or Kinds.Coda;
        if (!tiled)
        {
            foreach (var (x, a, b) in images) if (a >= t0 && b <= t1) result.Add(Project(x, a, b, map));
            return result;
        }
        double min = settings.PhraseMinBars - Eps; long at = t0;
        for (int k = 0; k < images.Count; k++)
        {
            var (x, a, b) = images[k];
            a = Math.Clamp(a, at, t1); b = Math.Clamp(b, a, t1);
            if (a > at && stem != null && Bars(at, a) >= min) result.Add(Fresh(at, a, stem));   // a long insert before it
            else a = at;
            long next = k + 1 < images.Count ? Math.Clamp(images[k + 1].a, b, t1) : t1;
            if (!(stem != null && Bars(b, next) >= min)) b = next;                                   // a short gap joins this image
            if (b > a) result.Add(Project(x, a, b, map));
            at = Math.Max(at, b);
        }
        if (at < t1 && stem != null) result.Add(Fresh(at, t1, stem));
        return result;
    }
    Node Fresh(long a, long b, string stem)
    {
        var n = new Node { Kind = Kinds.Phrase, Level = 2, Start = a, End = b, Label = NextPhrase(stem) };
        Analyse(n, true);
        return n;
    }
    Node Project(Node x, long a, long b, Map map)
    {
        var y = new Node { Kind = x.Kind, Level = x.Level, Start = a, End = b, Source = x, Projected = true, Count = x.Count, StepSteps = x.StepSteps, StepSemitones = x.StepSemitones,
            StepReal = x.StepReal, StepInverted = x.StepInverted, Order = x.Order, Iteration = x.Iteration, Evidence = x.Evidence };
        Transpose(y, map.K);
        y.Exact = true;
        for (int c = FirstColumnAt(x.Start); c < grid.Columns && grid.ColumnStart[c] < x.End && y.Exact; c++) if (map.Considered[c] && map.To[c] < 0) y.Exact = false;
        for (int c = FirstColumnAt(a); c < grid.Columns && grid.ColumnStart[c] < b && y.Exact; c++) if (map.Varied[c]) y.Exact = false;
        if (y.Kind == Kinds.Power) foreach (var r in it.Relations) if (UnitPower(r) && r.Start == a && r.End == b && r.Count == x.Count) { y.Power = r.Id; break; }
        if (y.Kind == Kinds.Unit) y.Paradigm = SingleCell(a, b);
        y.Children.AddRange(Images(x.Children, map, a, b, null, null));
        return y;
    }
    // A restatement k semitones away: whole octaves keep the scale (7 steps each), anything else is real.
    static void Transpose(Node n, int k) { n.Semitones = k; n.Steps = k % 12 == 0 ? k / 12 * 7 : 0; n.Real = k % 12 != 0; }

    // ---------------------------------------------------------------------------------------------------------------
    // §8 F6: unit letters.

    // A power's units take a letter by first appearance (U, V, W, … then U₂ …), or the paradigm letter of the principal cell a
    // unit is; every unit of a power and of the powers filling its iterations shares it.
    void LabelUnits(Node n)
    {
        if (n.Projected) return;
        if (n.Kind == Kinds.Power && n.Letter.Length == 0)
        {
            var u = n; while (u.Kind == Kinds.Power && u.Children.Count > 0) u = u.Children[0];
            string letter;
            if (u.Kind == Kinds.Unit && u.Paradigm >= 0) letter = lex.Paradigms[u.Paradigm].Letter;
            else { letter = UnitLetters[letters % UnitLetters.Length] + (letters >= UnitLetters.Length ? Digits(letters / UnitLetters.Length + 1, Subscripts) : ""); letters++; }
            Letter(n, letter);
        }
        foreach (var k in n.Children) LabelUnits(k);
    }
    void Letter(Node n, string letter)
    {
        n.Letter = letter;
        n.Label = n.Kind == Kinds.Unit ? letter : "×" + n.Count + Step(StepOf(n));
        if (n.Kind == Kinds.Power) foreach (var k in n.Children) Letter(k, letter);
    }
    // A projected node keeps its prototype's label, primed when the script truncates or varies it.
    void LabelProjected(Node n)
    {
        if (n.Projected) { n.Label = n.Source.Label + (n.Exact ? "" : "′"); n.Letter = n.Source.Letter; }
        foreach (var k in n.Children) LabelProjected(k);
    }
    static Transform StepOf(Node n) => new(n.StepSteps, n.StepSemitones, n.StepReal ? Transform.Real : Transform.Tonal, n.StepInverted);

    // ---------------------------------------------------------------------------------------------------------------
    // §8 F5: cross-references.

    // A unit, or a whole bar of a phrase or period that no unit covers, which no power or return explains (more than half of its
    // columns in a later iteration of a power of the tree or of the principal line, or in a return's matched target), is
    // compared with every earlier span of its columns at the same place in the bar, on the principal line (the étude's bar 16 =
    // bar 6 holds there, not under the different accompaniment). When one element relates CrossReferenceShare of the column
    // pairs where either has notes (T_k, or t_d with at most one altered note a column), the best such span is its source: the
    // larger share, tonal before real, fewer altered notes, the smaller move, then the earliest. A block that is no unit (the
    // étude's soft repeat of bars 1–8 at 9–16 crosses its phrases) explains nothing. A cross-reference never cuts anything.
    void CrossReferencesOf()
    {
        Lines();
        var explained = new bool[grid.Columns];
        void Mark(long from, long to) { for (int c = FirstColumnAt(from); c < grid.Columns && grid.ColumnStart[c] < to; c++) explained[c] = true; }
        foreach (var r in ret.All) foreach (int c in r.Cols) explained[c] = true;
        foreach (var r in it.Relations)
            if (r.IsPower && r.Source == Iteration.FromCells && r.StreamA == input.Streams.Principal)
                for (int k = 1; k < r.Count; k++) Mark(r.Iterations[k], k + 1 < r.Count ? r.Iterations[k + 1] : r.End);
        void Powers(Node n)
        {
            if (n.Kind == Kinds.Power) for (int k = 1; k < n.Children.Count; k++) Mark(n.Children[k].Start, n.Children[k].End);
            foreach (var k in n.Children) Powers(k);
        }
        Powers(root);
        var candidates = new List<(long a, long b, Node node)>();
        void Collect(Node n)
        {
            if (n.Projected) return;
            if (n.Kind is Kinds.Power or Kinds.Unit) candidates.Add((n.Start, n.End, n));
            if (n.Level == 2)
            {
                var units = new List<Node>(); Gather(n, units);
                for (int b = Math.Max(0, grid.BarAt(n.Start)); b < grid.Bars && grid.BarStart[b] < n.End; b++)
                {
                    long a = grid.BarStart[b], e = grid.BarEnd[b];
                    if (a < n.Start || e > n.End) continue;
                    bool covered = false; foreach (var u in units) if (u.Start < e && u.End > a) { covered = true; break; }
                    if (!covered) candidates.Add((a, e, Innermost(n, a, e)));
                }
            }
            foreach (var k in n.Children) Collect(k);
        }
        Collect(root);
        foreach (var (a, b, node) in candidates) Match(a, b, node, explained);
    }
    static void Gather(Node n, List<Node> units) { foreach (var k in n.Children) { if (k.Kind is Kinds.Power or Kinds.Unit) units.Add(k); else Gather(k, units); } }
    static Node Innermost(Node n, long a, long b) { foreach (var k in n.Children) if (k.Start <= a && k.End >= b && k.Kind == Kinds.SubPhrase) return Innermost(k, a, b); return n; }

    void Lines()
    {
        int p = input.Streams.Principal, n = grid.Columns;
        var lists = new List<LineNote>[n];
        if (p >= 0)
        {
            var s = input.Streams[p];
            for (int k = 0; k < s.Events; k++)
            {
                var e = input.Events[s.Lead[k]]; int c = grid.ColumnAt(s.Onset[k]); if (c < 0) continue;
                long slot = Math.Max(1, grid.SlotAt(grid.ColumnStart[c]));
                (lists[c] ??= new List<LineNote>()).Add(new LineNote { Slot = (int)Math.Round((s.Onset[k] - grid.ColumnStart[c]) / (double)slot, MidpointRounding.AwayFromZero), Pitch = e.Pitch, Morph = e.Morph, Alter = e.Alter, Tick = s.Onset[k] });
                long end = e.Tick + e.Duration;
                for (int d = c + 1; d < n && grid.ColumnStart[d] < end; d++)
                {
                    if (end * 2 <= 2 * grid.ColumnStart[d] + Math.Max(1, grid.SlotAt(grid.ColumnStart[d]))) break;   // held more than half a slot in, as §6 D1's columns
                    (lists[d] ??= new List<LineNote>()).Add(new LineNote { Pitch = e.Pitch, Morph = e.Morph, Alter = e.Alter, Tick = grid.ColumnStart[d], Tied = true });
                }
            }
        }
        lineFirst = new int[n]; lineSize = new int[n]; var all = new List<LineNote>();
        for (int c = 0; c < n; c++)
        {
            var list = lists[c] ?? new List<LineNote>();
            list.Sort((a, b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Tied != b.Tied ? b.Tied.CompareTo(a.Tied) : a.Pitch.CompareTo(b.Pitch));
            lineFirst[c] = all.Count; lineSize[c] = list.Count; all.AddRange(list);
        }
        line = all.ToArray();
    }
    // How column v's principal line restates column u's: both silent is no evidence; otherwise the same slots and ties with
    // every pitch moved by one K (real), and every morph by one D with the pitches the local scale gives there, the source's
    // alterations kept (tonal; at most one altered note).
    (bool empty, bool real, int k, bool tonal, int d, int altered) LineRelate(int u, int v)
    {
        int nu = lineSize[u], nv = lineSize[v];
        if (nu == 0 && nv == 0) return (true, false, 0, false, 0, 0);
        if (nu != nv) return (false, false, 0, false, 0, 0);
        int fu = lineFirst[u], fv = lineFirst[v], k = line[fv].Pitch - line[fu].Pitch, d = line[fv].Morph - line[fu].Morph, altered = 0;
        bool real = true, tonal = true;
        for (int j = 0; j < nu; j++)
        {
            var a = line[fu + j]; var b = line[fv + j];
            if (a.Slot != b.Slot || a.Tied != b.Tied) return (false, false, 0, false, 0, 0);
            if (b.Pitch - a.Pitch != k) real = false;
            if (b.Morph - a.Morph != d) tonal = false;
            else if (b.Pitch != input.Frames.ScalePitch(a.Morph + d, input.Frames.Region(b.Tick)) + a.Alter) altered++;
        }
        return (false, real, k, tonal && altered <= 1, d, altered);
    }
    void Match(long a, long b, Node node, bool[] explained)
    {
        int c0 = FirstColumnAt(a), c1 = FirstColumnAt(b), n = c1 - c0;
        if (n <= 0) return;
        int ex = 0, struck = 0;
        for (int c = c0; c < c1; c++) { if (explained[c]) ex++; for (int j = 0; j < lineSize[c]; j++) if (!line[lineFirst[c] + j].Tied) struck++; }
        if (2 * ex > n || struck < 2) return;
        long offset = grid.ColumnStart[c0] - grid.BarStart[grid.ColumnBar[c0]];
        // best: (share, tonal, altered, |move|, source, kind value)
        double bestShare = 0; bool bestTonal = false; int bestAltered = 0, bestMove = 0, bestSource = -1, bestValue = 0;
        var kinds = new List<(bool tonal, int value, int count, int altered)>();
        for (int s = grid.FirstColumn; s + n <= c0; s++)
        {
            if (grid.ColumnStart[s] - grid.BarStart[grid.ColumnBar[s]] != offset) continue;
            bool same = true;
            for (int j = 0; j < n && same; j++) same = grid.ColumnEnd[s + j] - grid.ColumnStart[s + j] == grid.ColumnEnd[c0 + j] - grid.ColumnStart[c0 + j];
            if (!same) continue;
            kinds.Clear(); int compared = 0, misses = 0, allowed = (int)Math.Floor(n * (1 - settings.CrossReferenceShare) + Eps);
            for (int j = 0; j < n && misses <= allowed; j++)
            {
                var r = LineRelate(s + j, c0 + j); if (r.empty) continue;
                compared++;
                if (!r.real && !r.tonal) { misses++; continue; }
                if (r.tonal) Tally(kinds, true, r.d, r.altered);
                if (r.real) Tally(kinds, false, r.k, 0);
            }
            if (misses > allowed || compared == 0) continue;
            foreach (var (tonal, value, count, altered) in kinds)
            {
                double share = count / (double)compared;
                if (share < settings.CrossReferenceShare - Eps) continue;
                int move = Math.Abs(value);
                bool better = bestSource < 0 || share > bestShare + Eps
                    || Math.Abs(share - bestShare) <= Eps && (tonal && !bestTonal || tonal == bestTonal && (altered < bestAltered || altered == bestAltered && move < bestMove));
                if (better) { bestShare = share; bestTonal = tonal; bestAltered = altered; bestMove = move; bestSource = s; bestValue = value; }
            }
        }
        if (bestSource < 0) return;
        long sa = grid.ColumnStart[bestSource], sb = grid.ColumnEnd[bestSource + n - 1];
        var x = new CrossReference { Id = crossReferences.Count, Start = a, End = b, SourceStart = sa, SourceEnd = sb, Share = bestShare, Altered = bestAltered, Target = node, Level = node.Level };
        // The musician's name, from the leads of both spans when they place one another (TransformGroup.Relate).
        var leadsA = Leads(sa, sb); var leadsB = Leads(a, b); var frames = input.Frames;
        var placed = leadsA.Count > 0 && leadsA.Count == leadsB.Count ? TransformGroup.Relate(TransformGroup.Cell(input.Events, leadsA, sa, frames), TransformGroup.Cell(input.Events, leadsB, a, frames), sa, a, frames) : null;
        if (placed != null && (bestTonal ? placed.G.Frame != Transform.Real && placed.G.Steps == bestValue : placed.G.Semitones == bestValue)) { x.G = placed.G; x.Altered = placed.Altered.Length; x.Text = placed.Text; }
        else
        {
            x.G = bestTonal ? new Transform(bestValue, leadsA.Count > 0 && leadsB.Count > 0 ? input.Events[leadsB[0]].Pitch - input.Events[leadsA[0]].Pitch : 0, Transform.Tonal)
                : new Transform(bestValue % 12 == 0 ? bestValue / 12 * 7 : 0, bestValue, bestValue % 12 == 0 ? Transform.Tonal : Transform.Real);
            x.Text = TransformGroup.Name(x.G);
        }
        x.Text = $"{Where(a, b)} = {Where(sa, sb)}: {x.Text}";
        if (node.Kind is Kinds.Power or Kinds.Unit && node.Start == a && node.End == b) node.CrossReference = x.Id;
        crossReferences.Add(x);
    }
    static void Tally(List<(bool tonal, int value, int count, int altered)> kinds, bool tonal, int value, int altered)
    {
        for (int i = 0; i < kinds.Count; i++) if (kinds[i].tonal == tonal && kinds[i].value == value) { var e = kinds[i]; kinds[i] = (tonal, value, e.count + 1, e.altered + altered); return; }
        kinds.Add((tonal, value, 1, altered));
    }
    List<int> Leads(long a, long b)
    {
        var list = new List<int>(); int p = input.Streams.Principal; if (p < 0) return list;
        var s = input.Streams[p];
        for (int k = 0; k < s.Events; k++) if (s.Onset[k] >= a && s.Onset[k] < b) list.Add(s.Lead[k]);
        return list;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §8 F6: names and formula lines.

    void Describe(Node n)
    {
        foreach (var k in n.Children) Describe(k);
        n.Text = n.Kind switch
        {
            Kinds.Piece => PieceLine(),
            Kinds.Section or Kinds.Intro => SectionLine(n),
            Kinds.Power => PowerFormula(n),
            _ => Items(n)
        };
        n.Name = n.Kind switch
        {
            Kinds.Piece => n.Label,
            Kinds.Section => ret.Reviewed ? n.Label : $"section {n.Label}",
            Kinds.Intro => "introduction",
            Kinds.Phrase => n.Source != null && !n.Projected ? $"phrase {n.Label}, {n.Source.Label} again" : $"phrase {n.Label}",
            Kinds.Period => $"period {n.Label}",
            Kinds.SubPhrase => $"sub-phrase {n.Label}",
            Kinds.Coda => "coda",
            Kinds.Power => n.Power >= 0 ? it.Relations[n.Power].Text : n.Source?.Name ?? "",
            _ => n.Paradigm >= 0 ? $"{lex.Paradigms[n.Paradigm].Name} ({lex.Paradigms[n.Paradigm].Letter})" : $"unit {n.Label}"
        };
    }

    // "(U²)² ↑1": the unit, or the power filling its first iteration in brackets, then the count and the step. A unit that is one
    // cell placed away from its prototype shows the placement: "(β′ ↓8ve [♭6])²".
    string PowerFormula(Node n)
    {
        var first = n.Children.Count > 0 ? n.Children[0] : null;
        string core = first == null ? n.Letter : first.Kind == Kinds.Power ? "(" + PowerFormula(first) + ")" : first.Label;
        if (first != null && first.Kind == Kinds.Unit && first.Paradigm >= 0 && first.Label == lex.Paradigms[first.Paradigm].Letter)
        {
            int k = FirstCell(first.Start);
            if (k < cells.Length && cells[k].Start == first.Start) { string token = CellToken(cells[k]); if (token != core) core = "(" + token + ")"; }
        }
        return core + Digits(n.Count, Superscripts) + Step(StepOf(n));
    }
    // A step or placement in the formula grammar (§8 F6): ↑k tonal steps, ↑kc semitones (real), ↓8ve octaves, [P] the parallel
    // mode, [I] a mirroring step.
    static string Step(in Transform g)
    {
        if (g.Equals(Transform.Identity)) return "";
        var sb = new StringBuilder(); int octaves = g.Octaves;
        if (octaves != 0) sb.Append(octaves < 0 ? " ↓" : " ↑").Append(Math.Abs(octaves) == 1 ? "8ve" : Math.Abs(octaves) + "·8ve");
        else if (g.Frame == Transform.Real && g.Semitones != 0) sb.Append(g.Semitones < 0 ? " ↓" : " ↑").Append(Math.Abs(g.Semitones)).Append('c');
        else if (g.Steps != 0) sb.Append(g.Steps < 0 ? " ↓" : " ↑").Append(Math.Abs(g.Steps));
        if (g.Parallel != 0) sb.Append(" [P]");
        return sb.ToString();
    }
    static string Digits(int n, string set) { var s = n.ToString(System.Globalization.CultureInfo.InvariantCulture); var sb = new StringBuilder(); foreach (char c in s) sb.Append(set[c - '0']); return sb.ToString(); }

    // A node's items in time order: its children (a power by its formula, others by label), and between them the principal
    // cells: a cell power as "α⁺² ↑1", a member by its paradigm letter placed from its prototype (mirror, step, named
    // alterations, a variant's first tag), literal cells by where they lie ("⌐" for a pickup); "⊙" when an arrival closes it.
    string Items(Node n)
    {
        var tokens = new List<string>(); long at = n.Start; int k = 0, ci = FirstCell(n.Start);
        while (at < n.End && tokens.Count <= MaxTokens)
        {
            if (k < n.Children.Count && n.Children[k].Start <= at)
            {
                var kid = n.Children[k++]; tokens.Add(kid.Kind == Kinds.Power ? PowerFormula(kid) : kid.Label); at = Math.Max(at, kid.End); continue;
            }
            long limit = k < n.Children.Count ? n.Children[k].Start : n.End;
            while (ci < cells.Length && cells[ci].Start < at) ci++;
            if (ci >= cells.Length || cells[ci].Start >= limit) { at = limit; continue; }
            var c = cells[ci];
            if (c.Paradigm < 0)
            {
                long a = c.Start, b = c.End;
                while (ci + 1 < cells.Length && cells[ci + 1].Paradigm < 0 && cells[ci + 1].Start == b && cells[ci + 1].Start < limit) b = cells[++ci].End;
                ci++;
                int bar = Math.Max(0, grid.BarAt(a));
                bool pickup = bar == grid.FirstBar && bar + 1 < grid.Bars && grid.BarEnd[bar] - grid.BarStart[bar] < grid.BarEnd[bar + 1] - grid.BarStart[bar + 1] && b <= grid.BarEnd[bar];
                tokens.Add(pickup ? "⌐" : Where(a, Math.Min(b, limit)));
                at = Math.Min(b, limit); continue;
            }
            int pw = cellPower[c.Id];
            if (pw >= 0 && it.Relations[pw].Cells[0] == c.Id && it.Relations[pw].End <= limit)
            {
                var r = it.Relations[pw];
                tokens.Add(lex.Paradigms[c.Paradigm].Letter + Digits(r.Count, Superscripts) + Step(r.Step));
                while (ci < cells.Length && cells[ci].Start < r.End) ci++;
                at = r.End; continue;
            }
            tokens.Add(CellToken(c)); at = c.End; ci++;
        }
        if (tokens.Count > MaxTokens) { tokens.RemoveRange(MaxTokens, tokens.Count - MaxTokens); tokens.Add("…"); }
        string text = string.Join(" · ", tokens);
        // An arrival closes the node when its cut lies in the node's last bar (the coda's 77.1 closes it a tactus before its end).
        int end = Math.Max(0, grid.BarAt(n.End - 1)); long closing = grid.Bars == 0 ? 0 : grid.BarEnd[end] - grid.BarStart[end];
        foreach (long cut in ret.ArrivalCuts) if (cut <= n.End && cut > n.End - closing && cut > n.Start) { text += text.Length > 0 ? " ⊙" : "⊙"; break; }
        return text;
    }
    string CellToken(CellLexicon.Cell c)
    {
        var sb = new StringBuilder(lex.Paradigms[c.Paradigm].Letter);
        if (c.G.Inverted) sb.Append('\u0304');   // a mirror: X̄
        sb.Append(Step(c.G));
        var named = new List<string>();
        foreach (var x in c.Altered)
        {
            string text = x.Text.Length > 0 && (x.Text[0] is '♭' or '♯') ? x.Text.Split(' ')[0] : x.Text.Contains("leading tone", StringComparison.Ordinal) ? "LT" : x.Text;
            if (text.Length > 0 && !named.Contains(text)) named.Add(text);
        }
        foreach (var x in named) sb.Append(" [").Append(x).Append(']');
        if (!c.Exact) foreach (var s in c.Sparks) if (!string.IsNullOrEmpty(s.Tag)) { sb.Append(" (").Append(s.Tag).Append(')'); break; }
        return sb.ToString();
    }

    // "a1 · a2 · a1′ · a3    a1′ = a1 (bar 9 adds G♯4)"; periods with their generators' signs: "B1 · B2 · … · B5    → → ⬡ ⇄ ·".
    string SectionLine(Node s)
    {
        var parts = new StringBuilder(string.Join(" · ", s.Children.Select(k => k.Label)));
        if (s.Children.Count > 0 && s.Children.All(k => k.Kind == Kinds.Period)) return parts + "    " + string.Join(" ", s.Children.Select(Sign));
        foreach (var k in s.Children)
        {
            if (k.Source == null || k.Projected || k.Return < 0) continue;
            string how = Variation(ret.All[k.Return]);
            parts.Append($"    {k.Label} = {k.Source.Label}{Shift(ret.All[k.Return].K)}{(how.Length > 0 ? $" ({how})" : "")}");
        }
        return parts.ToString();
    }
    // A period's generator sign (§14.1): the polygon of a cycle (runs of order n ≥ 3 that close, covering a quarter of it
    // together; the larger n first), else the order of the generator labelling most of its bars (§6 D9), else of the iterated
    // detection overlapping it longest: → translation, · identity, ⇄ involution. A doubling iterates nothing: it has no sign.
    string Sign(Node p)
    {
        var cover = new long[13];
        foreach (var r in it.Relations)
            if (r.Kind == PreparedPatternSong.MotifRelations.Run && r.Period > 0 && r.Order >= 3 && r.Order <= 12)
                cover[r.Order] += Math.Max(0, Math.Min(p.End, r.End) - Math.Max(p.Start, r.Start));
        for (int order = 12; order >= 3; order--) if (4 * cover[order] >= p.End - p.Start && cover[order] > 0) return Polygon(order);
        var bars = new List<(int relation, int count)>();
        for (int b = Math.Max(0, grid.BarAt(p.Start)); b < grid.Bars && grid.BarStart[b] < p.End; b++)
        {
            int r = it.Labels[b].Relation; if (r < 0 || it.Relations[r].Kind == PreparedPatternSong.MotifRelations.Doubling) continue;
            int i = bars.FindIndex(x => x.relation == r);
            if (i < 0) bars.Add((r, 1)); else bars[i] = (r, bars[i].count + 1);
        }
        int chosen = -1, count = 0; foreach (var x in bars) if (x.count > count) { chosen = x.relation; count = x.count; }
        if (chosen < 0)
        {
            long longest = 0;
            foreach (var r in it.Relations)
            {
                if (r.Kind == PreparedPatternSong.MotifRelations.Doubling || r.IsPower && r.Source == Iteration.FromCells) continue;
                long o = Math.Min(p.End, r.End) - Math.Max(p.Start, r.Start);
                if (o > longest) { longest = o; chosen = r.Id; }
            }
        }
        return chosen < 0 ? "" : Polygon(it.Relations[chosen].Order);
    }
    static string Polygon(int order) => order switch { 0 => "→", 1 => "·", 2 => "⇄", 3 => "△", 4 => "□", 5 => "⬠", 6 => "⬡", _ => $"{order}-cycle" };
    static string Shift(int k) => k == 0 ? "" : k % 12 == 0 ? (k < 0 ? " ↓" : " ↑") + (Math.Abs(k) == 12 ? "8ve" : Math.Abs(k / 12) + "·8ve") : (k < 0 ? " ↓" : " ↑") + Math.Abs(k) + "c";

    // "A · B · A′    A′ = A − (a2 a1′) + Coda   (bars 6–13 left out)" (E16: it starts with the sections).
    string PieceLine()
    {
        var text = new StringBuilder(string.Join(" · ", root.Children.Select(s => s.Label)));
        var seen = new List<int>();
        foreach (var s in root.Children)
        {
            if (s.Return < 0 || seen.Contains(s.Return)) continue;
            seen.Add(s.Return);
            text.Append("    ").Append(Equation(s, out string left));
            if (left.Length > 0) text.Append($"   ({left} left out)");
        }
        return text.ToString();
    }
    // "A′ = A − (a2 a1′) + Coda" for a section copy; "(V′ C′) = (V C)" for a reviewed return over several sections. left: the
    // bars the copy leaves out ("bars 6–13").
    string Equation(Node s, out string left)
    {
        var r = ret.All[s.Return]; left = "";
        string Group(int from, int to) { var labels = new List<string>(); for (int i = from; i <= Math.Max(from, to) && i < sections.Length; i++) labels.Add(sections[i].Label); return labels.Count == 1 ? labels[0] : "(" + string.Join(" ", labels) + ")"; }
        string lhs = r.Reviewed ? Group(r.Section, r.LastSection) : s.Label, rhs = r.Reviewed ? Group(r.SourceSection, r.LastSourceSection) : s.Source.Label;
        var text = new StringBuilder($"{lhs} = {rhs}{Shift(r.K)}");
        if (s.Omits.Length > 0) text.Append(" − (").Append(s.Omits.Replace(", ", " ")).Append(')');
        foreach (var k in s.Children) if (!k.Projected) text.Append(" + ").Append(k.Label);
        var gone = r.Script.Where(e => e.Op == PreparedPatternSong.MotifEdits.Delete).Select(e => BarRange(e.SourceStart, e.SourceEnd)).ToList();
        if (gone.Count > 0) left = (gone.Count == 1 && !gone[0].Contains('–') ? "bar " : "bars ") + string.Join(", ", gone);
        return text.ToString();
    }
    // What an internal return varies: "bar 9 adds G♯4" for each substituted column (the notes added and dropped against the
    // source moved by its K), joined by "; ".
    string Variation(Returns.Return r)
    {
        var parts = new List<string>(); var cols = ret.Cols;
        foreach (var e in r.Script)
        {
            if (e.Op != PreparedPatternSong.MotifEdits.Substitute) continue;
            int u = FirstColumnAt(e.SourceStart), v = FirstColumnAt(e.TargetStart);
            for (; v < grid.Columns && grid.ColumnStart[v] < e.TargetEnd && u < grid.Columns; u++, v++)
            {
                var added = new List<int>(); var dropped = new List<int>();
                var source = new List<int>(); for (int j = 0; j < cols.Size[u]; j++) if (!cols.Notes[cols.First[u] + j].Tied) source.Add(cols.Notes[cols.First[u] + j].Event);
                var used = new bool[source.Count];
                for (int j = 0; j < cols.Size[v]; j++)
                {
                    var note = cols.Notes[cols.First[v] + j]; if (note.Tied) continue;
                    int q = -1; for (int x = 0; x < source.Count && q < 0; x++) if (!used[x] && input.Events[source[x]].Pitch + r.K == note.Pitch) q = x;
                    if (q >= 0) used[q] = true; else added.Add(note.Event);
                }
                for (int x = 0; x < source.Count; x++) if (!used[x]) dropped.Add(source[x]);
                string where = $"bar {grid.Printed[Math.Max(0, grid.ColumnBar[v])]}";
                string Names(List<int> ev) => string.Join(" ", ev.Select(NoteName));
                parts.Add(added.Count > 0 && dropped.Count == 0 ? $"{where} adds {Names(added)}" : dropped.Count > 0 && added.Count == 0 ? $"{where} drops {Names(dropped)}"
                    : added.Count == 1 && dropped.Count == 1 ? $"{where} has {Names(added)} for {Names(dropped)}" : $"{where} varied");
            }
        }
        var distinct = new List<string>(); foreach (var p in parts) if (!distinct.Contains(p)) distinct.Add(p);
        return string.Join("; ", distinct);
    }
    string NoteName(int e) => Spelling.Name(input.Events[e].Fifths) + Spelling.FloorDiv(input.Events[e].Morph, 7);

    // ---------------------------------------------------------------------------------------------------------------
    // §8 F7 and the texts that need node ids.

    // Preorder with contiguous children: each node's children are listed together, then each child's subtree in turn (Nodes[0]
    // is the piece; the §13.1 fragment: the sections are 1–3 and A's phrases start at 4).
    void Flatten()
    {
        var list = new List<Node> { root }; root.Id = 0; root.Parent = -1; root.Depth = 0;
        void Expand(Node n)
        {
            n.ChildCount = n.Children.Count; n.FirstChild = n.ChildCount > 0 ? list.Count : -1;
            foreach (var k in n.Children) { k.Id = list.Count; k.Parent = n.Id; k.Depth = n.Depth + 1; list.Add(k); }
            foreach (var k in n.Children) Expand(k);
        }
        Expand(root);
        Nodes = list.ToArray();
        foreach (var n in Nodes) n.Prototype = n.Source?.Id ?? -1;
        ReturnNode = new int[ret.All.Length]; ReturnSource = new int[ret.All.Length]; Array.Fill(ReturnNode, -1); Array.Fill(ReturnSource, -1);
        PowerNode = new int[it.Relations.Length]; Array.Fill(PowerNode, -1);
        foreach (var n in Nodes)
        {
            if (n.Return >= 0 && ReturnNode[n.Return] < 0) { ReturnNode[n.Return] = n.Id; ReturnSource[n.Return] = n.Prototype; }
            if (n.Power >= 0 && PowerNode[n.Power] < 0) PowerNode[n.Power] = n.Id;
        }
        foreach (var x in crossReferences)
        {
            x.Node = x.Target?.Id ?? -1;
            foreach (var n in Nodes) if (n.Start == x.SourceStart && n.End == x.SourceEnd && n.Kind is Kinds.Power or Kinds.Unit or Kinds.SubPhrase or Kinds.Phrase or Kinds.Period) { x.Source = n.Id; break; }
            var target = x.Target;
            if (target != null && target.Start == x.Start && target.End == x.End && target.Source == null && x.Source >= 0) { target.Source = Nodes[x.Source]; target.Prototype = x.Source; target.Steps = x.G.Steps; target.Semitones = x.G.Semitones; target.Inverted = x.G.Inverted; target.Real = x.G.Frame == Transform.Real; }
        }
        CrossReferences = crossReferences.ToArray();
    }

    // The relations' sentences, the formula (the piece line, then each section and each level-2 node with structure, as
    // "label = text") and the caption's piece part ("A · B · A′ (A′ = A − bars 6–13 + coda)").
    void Write()
    {
        ReturnText = new string[ret.All.Length];
        for (int i = 0; i < ret.All.Length; i++) ReturnText[i] = Sentence(i);
        var lines = new List<string> { root.Text };
        foreach (var s in root.Children) if (s.Children.Count > 0) lines.Add($"{s.Label} = {s.Text}");
        foreach (var s in root.Children) foreach (var p in s.Children) if (!p.Projected && p.Children.Count > 0) lines.Add($"{p.Label} = {p.Text}");
        Formula = string.Join("\n", lines);
        var caption = new StringBuilder(string.Join(" · ", root.Children.Select(s => s.Label)));
        var notes = new List<string>(); var seen = new List<int>();
        foreach (var s in root.Children)
        {
            if (s.Return < 0 || seen.Contains(s.Return)) continue;
            seen.Add(s.Return);
            string eq = Equation(s, out string left);
            var r = ret.All[s.Return];
            string lhs = eq[..eq.IndexOf(" = ", StringComparison.Ordinal)], rhs = r.Reviewed ? eq[(eq.IndexOf(" = ", StringComparison.Ordinal) + 3)..] : s.Source.Label + Shift(r.K);
            if (!r.Reviewed) { if (left.Length > 0) rhs += " − " + left; if (s.Children.Any(k => k.Kind == Kinds.Coda)) rhs += " + coda"; }
            notes.Add($"{lhs} = {rhs}");
        }
        if (notes.Count > 0) caption.Append(" (").Append(string.Join("; ", notes)).Append(')');
        Line = caption.ToString();
    }
    // "A′ restates A from bar 1, leaving out bars 6–13 (a2, a1′); the coda branches from bar 20 at bar 73" (§13.1); "a1′ restates
    // a1; bar 9 adds G♯4"; "Verse 2 + Chorus 2 restate Verse 1 + Chorus 1".
    string Sentence(int index)
    {
        var r = ret.All[index]; int node = ReturnNode[index];
        string Names(int from, int to) { var labels = new List<string>(); for (int i = from; i <= Math.Max(from, to) && i < sections.Length; i++) labels.Add(sections[i].Label); return string.Join(" + ", labels); }
        string interval = TransformGroup.ChromaticInterval(Math.Abs(r.K));
        string shift = r.K == 0 ? "" : $" {("aeiou".Contains(interval[0]) ? "an" : "a")} {interval} {(r.K < 0 ? "lower" : "higher")}";
        if (r.Reviewed) return $"{Names(r.Section, r.LastSection)} restate{(r.LastSection > r.Section ? "" : "s")} {Names(r.SourceSection, r.LastSourceSection)}{shift}";
        string copy = node >= 0 ? Nodes[node].Label : r.Level == 1 && r.Section >= 0 ? sections[r.Section].Label : Position(r.Start);
        string source = node >= 0 && Nodes[node].Prototype >= 0 ? Nodes[Nodes[node].Prototype].Label : r.Level == 1 && r.SourceSection >= 0 ? sections[r.SourceSection].Label : Position(r.SourceStart);
        if (r.Level != 1) { string how = Variation(r); return $"{copy} restates {source}{shift}{(how.Length > 0 ? "; " + how : "")}"; }
        var text = new StringBuilder($"{copy} restates {source}{shift}");
        static bool Matched(Returns.Edit e) => e.Op is PreparedPatternSong.MotifEdits.Match or PreparedPatternSong.MotifEdits.Substitute;
        int firstMatch = Array.FindIndex(r.Script, Matched), lastMatch = Array.FindLastIndex(r.Script, Matched);
        if (firstMatch >= 0) text.Append($" from bar {grid.Printed[Math.Max(0, grid.BarAt(r.Script[firstMatch].SourceStart))]}");
        var s = node >= 0 ? Nodes[node] : null; bool first = true;
        foreach (var e in r.Script)
        {
            if (e.Op != PreparedPatternSong.MotifEdits.Delete) continue;
            string range = BarRange(e.SourceStart, e.SourceEnd);
            text.Append(first ? ", leaving out " : " and ").Append(range.Contains('–') ? "bars " : "bar ").Append(range);
            first = false;
        }
        if (s != null && s.Omits.Length > 0) text.Append($" ({s.Omits})");
        // The coda branches from the source bar it replaces (the étude's 73.1 is 20.1 note for note), or follows a source restated
        // to its end.
        var coda = s?.Children.FirstOrDefault(k => k.Kind == Kinds.Coda);
        int codaBar = coda == null ? 0 : grid.Printed[Math.Max(0, grid.BarAt(coda.Start))];
        if (coda != null && lastMatch >= 0 && r.SourceSection >= 0 && r.Script[lastMatch].SourceEnd < ret.Sections[r.SourceSection].End)
            text.Append($"; the coda branches from bar {grid.Printed[Math.Max(0, grid.BarAt(r.Script[lastMatch].SourceEnd))]} at bar {codaBar}");
        else if (coda != null) text.Append($"; then a coda from bar {codaBar}");
        return text.ToString();
    }

    // The tree as indented lines, for reports and tests: "    a1′ [16.5, 26.5) phrase ← a1   9.1 · γ⁺² ↑1 · …", then the cross-references.
    public string Dump()
    {
        var sb = new StringBuilder(); double q = input.Ppq;
        foreach (var n in Nodes)
        {
            sb.Append(new string(' ', 2 * n.Depth)).Append(n.Label.Length > 0 ? n.Label : "?");
            sb.Append(FormattableString.Invariant($" [{n.Start / q:0.###}, {n.End / q:0.###}) {KindName(n.Kind)}"));
            if (n.Kind == Kinds.Power) sb.Append(FormattableString.Invariant($" ×{n.Count} step ({n.StepSteps},{n.StepSemitones}{(n.StepReal ? ",real" : "")}) order {n.Order}"));
            if (n.Prototype >= 0) sb.Append($" ← {Nodes[n.Prototype].Label}");
            if (n.Projected) sb.Append(n.Exact ? " projected" : " projected, edited");
            if (n.Omits.Length > 0) sb.Append($" omits {n.Omits}");
            if (n.Text.Length > 0) sb.Append("   ").Append(n.Text);
            sb.Append('\n');
        }
        foreach (var x in CrossReferences) sb.Append(FormattableString.Invariant($"cross-reference [{x.Start / q:0.###}, {x.End / q:0.###}) ← [{x.SourceStart / q:0.###}, {x.SourceEnd / q:0.###}) {x.Text}\n"));
        return sb.ToString();
    }
    static string KindName(int kind) => kind switch
    {
        Kinds.Piece => "piece", Kinds.Section => "section", Kinds.Phrase => "phrase", Kinds.Period => "period", Kinds.SubPhrase => "sub-phrase",
        Kinds.Power => "power", Kinds.Unit => "unit", Kinds.Coda => "coda", Kinds.Intro => "intro", _ => "arrival"
    };
}
