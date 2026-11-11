// Stage D of the motif reduction (Docs/MOTIF-REDUCTION.md §6): how the piece iterates.
// - Columns (one tactus of every pitched note) relate exactly, by real or tonal transposition, or, given the piece's
//   background similarity θ (§7 E2), by soft similarity (§7 E1).
// - Tandem blocks U·g(U)·g²(U)… become unit powers, nested when a block's units are blocks themselves or consecutive equal
//   blocks repeat under one h; a paradigm's consecutive cells with a constant step become cell powers (the loops of pop).
// - Texture runs and their cycles modulo the hand shape's symmetry, doubling and contrary motion between textures, hands
//   alternating in a run, liquidation (the principal line's period shrinking bar by bar) and the cross-scale flag.
// - Every detection's generator order, and each bar's dominant generator label: the input of §8 F2's period DP.
// Nothing here decides a section or a phrase; the hierarchy (§8) makes nodes of the powers.
public sealed class Iteration
{
    // How a block's step relates its columns: real transposition (K semitones, 0 exact), tonal transposition (D steps),
    // or soft similarity at K semitones (near, only with θ).
    public const int RealKind = 0, TonalKind = 1, NearKind = 2;
    // What a power iterates: a tandem block's units, blocks (consecutive equal ones, or the units of an outer block), or a
    // paradigm's consecutive cells in one stream.
    public const string FromBlock = "block", FromBlocks = "nested", FromCells = "cells";

    // One note of a column (§6 D1): its hand class (0 upper, 1 lower; 0 for every note in transcription mode, where hands
    // are not trusted), its onset slot from the column start (0 for a note held in), whether it is held in, and its pitch,
    // morph and alteration.
    public struct ColumnNote
    {
        public int Event, Hand, Slot, Pitch, Morph, Alter;
        public bool Tied;
    }

    // How column v relates to column u (§6 D1). Empty: both silent, no evidence either way. Real: v is u moved by K
    // semitones note for note at the same hands and slots (K = 0 is the exact relation, the tied flag ignored). Tonal: v is
    // u moved by D scale steps, Altered of its notes off the tonal image (allowed, and counted). Near: bit K + 6 set when
    // sim_K(u, v) ≥ θ, K = −6 … +6.
    public struct Pair
    {
        public bool Empty, Real, Tonal;
        public int K, D, Altered, Near;
        public bool Holds(int kind, int value) => kind switch { RealKind => Real && K == value, TonalKind => Tonal && D == value, _ => value >= -6 && value <= 6 && (Near >> (value + 6) & 1) != 0 };
    }

    // The columns of the piece (one per grid column) and their relations, computed on demand and kept per pair.
    public sealed class Columns
    {
        public int Count;
        public int[] First = Array.Empty<int>(), Size = Array.Empty<int>(), Struck = Array.Empty<int>();
        public ColumnNote[] Notes = Array.Empty<ColumnNote>();   // per column, by (hand, slot, pitch, morph)
        public double Theta = double.NaN;                         // §7 E2's θ; NaN: no near relation
        int[] byMorph = Array.Empty<int>();                      // per column, its notes by (hand, slot, morph, pitch)
        MotifGrid grid; KeyFrames frames; MotifEvent[] events;
        readonly Dictionary<long, Pair> cache = new();            // (u, v) → their relation: looked up only

        // A note belongs to the column it is struck in, and is held into each later column it still sounds in more than
        // half a slot past the start (the tie rule of §5 C1's windows).
        public static Columns Build(MotifInput input, double theta = double.NaN)
        {
            var grid = input.Grid; var ev = input.Events; bool hands = !input.Settings.Transcription;
            int count = grid.Columns;
            var lists = new List<ColumnNote>[count];
            for (int i = 0; i < ev.Length; i++)
            {
                var e = ev[i]; if (e.Drum || e.Grace) continue;
                int c = grid.ColumnAt(e.Tick); if (c < 0) continue;
                int hand = hands && e.Hand == 1 ? 1 : 0;
                long slot = Math.Max(1, grid.SlotAt(grid.ColumnStart[c]));
                (lists[c] ??= new List<ColumnNote>()).Add(new ColumnNote { Event = i, Hand = hand, Slot = (int)Math.Round((e.Tick - grid.ColumnStart[c]) / (double)slot, MidpointRounding.AwayFromZero), Pitch = e.Pitch, Morph = e.Morph, Alter = e.Alter });
                long end = e.Tick + e.Duration;
                for (int d = c + 1; d < count && grid.ColumnStart[d] < end; d++)
                {
                    if (end * 2 <= 2 * grid.ColumnStart[d] + Math.Max(1, grid.SlotAt(grid.ColumnStart[d]))) break;
                    (lists[d] ??= new List<ColumnNote>()).Add(new ColumnNote { Event = i, Hand = hand, Tied = true, Pitch = e.Pitch, Morph = e.Morph, Alter = e.Alter });
                }
            }
            var cols = new Columns { Count = count, First = new int[count], Size = new int[count], Struck = new int[count], Theta = theta, grid = grid, frames = input.Frames, events = ev };
            var notes = new List<ColumnNote>(); var byMorph = new List<int>();
            for (int c = 0; c < count; c++)
            {
                var list = lists[c] ?? new List<ColumnNote>();
                list.Sort((a, b) => a.Hand != b.Hand ? a.Hand.CompareTo(b.Hand) : a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Pitch != b.Pitch ? a.Pitch.CompareTo(b.Pitch) : a.Morph != b.Morph ? a.Morph.CompareTo(b.Morph) : a.Event.CompareTo(b.Event));
                cols.First[c] = notes.Count; cols.Size[c] = list.Count;
                foreach (var x in list) if (!x.Tied) cols.Struck[c]++;
                var order = new int[list.Count]; for (int k = 0; k < order.Length; k++) order[k] = notes.Count + k;
                notes.AddRange(list);
                Array.Sort(order, (p, q) =>
                {
                    var a = notes[p]; var b = notes[q];
                    return a.Hand != b.Hand ? a.Hand.CompareTo(b.Hand) : a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Morph != b.Morph ? a.Morph.CompareTo(b.Morph) : a.Pitch != b.Pitch ? a.Pitch.CompareTo(b.Pitch) : p.CompareTo(q);
                });
                byMorph.AddRange(order);
            }
            cols.Notes = notes.ToArray(); cols.byMorph = byMorph.ToArray();
            return cols;
        }

        public Pair Relate(int u, int v)
        {
            long key = (long)u << 32 | (uint)v;
            if (!cache.TryGetValue(key, out var r)) cache[key] = r = Compute(u, v);
            return r;
        }
        Pair Compute(int u, int v)
        {
            var r = new Pair();
            int n = Size[u];
            if (n == 0 && Size[v] == 0) { r.Empty = true; return r; }
            if (n > 0 && n == Size[v])
            {
                int fu = First[u], fv = First[v], k = Notes[fv].Pitch - Notes[fu].Pitch;
                bool real = true;
                for (int i = 0; i < n && real; i++) { var a = Notes[fu + i]; var b = Notes[fv + i]; real = a.Hand == b.Hand && a.Slot == b.Slot && b.Pitch - a.Pitch == k; }
                if (real) { r.Real = true; r.K = k; }
                // Tonal: the morphs move by one D and each note's pitch is checked against the image in the key where it
                // sounds, the source's alteration kept (§5 C6's t_k).
                int d = Notes[byMorph[fv]].Morph - Notes[byMorph[fu]].Morph, altered = 0;
                bool tonal = true;
                for (int i = 0; i < n && tonal; i++)
                {
                    var a = Notes[byMorph[fu + i]]; var b = Notes[byMorph[fv + i]];
                    tonal = a.Hand == b.Hand && a.Slot == b.Slot && b.Morph - a.Morph == d;
                    long at = b.Tied ? grid.ColumnStart[v] : events[b.Event].Tick;
                    if (tonal && b.Pitch != frames.ScalePitch(a.Morph + d, frames.Region(at)) + a.Alter) altered++;
                }
                if (tonal) { r.Tonal = true; r.D = d; r.Altered = altered; }
            }
            if (!double.IsNaN(Theta) && n > 0 && Size[v] > 0)
            {
                Span<double> sims = stackalloc double[13];
                Similarities(u, v, -6, 6, sims);
                for (int k = -6; k <= 6; k++) if (sims[k + 6] >= Theta - 1e-9) r.Near |= 1 << (k + 6);
            }
            return r;
        }

        // §7 E1's sim_k(u, v) for k = from … to, into sims[k − from]: the notes of u matched greedily to those of v in the same
        // hand class at most a slot apart, first at the pitch moved by k (weight 1), then an octave off it (0.8), then a
        // semitone off it where the morph moves by the pair's modal shift (0.7, tonal credit: the modal shift of the exact
        // matches, else of the semitone-off pairs); Dice over both columns, 0 when either is silent. Each note of u takes the
        // unused fitting note of v nearest in slot (the earlier on a tie). The candidate pairs do not depend on k, so they are
        // listed once and each k walks only them. It allocates nothing for ordinary columns and only reads, so stage E may
        // call it from parallel rows (Relate, with its cache, is stage D's alone).
        public void Similarities(int u, int v, int from, int to, Span<double> sims)
        {
            int nu = Size[u], nv = Size[v], fu = First[u], fv = First[v];
            if (nu == 0 || nv == 0) { sims[..(to - from + 1)].Clear(); return; }
            int capacity = nu * nv;
            Span<int> first = nu < 256 ? stackalloc int[nu + 1] : new int[nu + 1];
            Span<int> partner = capacity <= 1024 ? stackalloc int[capacity] : new int[capacity];
            Span<int> dp = capacity <= 1024 ? stackalloc int[capacity] : new int[capacity];
            Span<int> dm = capacity <= 1024 ? stackalloc int[capacity] : new int[capacity];
            Span<int> shifts = capacity <= 1024 ? stackalloc int[capacity] : new int[capacity];
            Span<bool> usedU = nu <= 256 ? stackalloc bool[nu] : new bool[nu], usedV = nv <= 256 ? stackalloc bool[nv] : new bool[nv];
            int n = 0;
            for (int i = 0; i < nu; i++)
            {
                first[i] = n; var a = Notes[fu + i];
                for (int gap = 0; gap <= 1; gap++)   // the nearest slot first, then the earlier note
                    for (int j = 0; j < nv; j++)
                    {
                        var b = Notes[fv + j];
                        if (b.Hand == a.Hand && Math.Abs(b.Slot - a.Slot) == gap) { partner[n] = j; dp[n] = b.Pitch - a.Pitch; dm[n] = b.Morph - a.Morph; n++; }
                    }
            }
            first[nu] = n;
            for (int k = from; k <= to; k++)
            {
                usedU.Clear(); usedV.Clear();
                int exact = 0, octave = 0, tonal = 0, count = 0;
                for (int i = 0; i < nu; i++)
                    for (int c = first[i]; c < first[i + 1]; c++)
                        if (!usedV[partner[c]] && dp[c] == k) { usedU[i] = usedV[partner[c]] = true; exact++; shifts[count++] = dm[c]; break; }
                for (int i = 0; i < nu; i++)
                    if (!usedU[i])
                        for (int c = first[i]; c < first[i + 1]; c++)
                            if (!usedV[partner[c]] && Math.Abs(dp[c] - k) == 12) { usedU[i] = usedV[partner[c]] = true; octave++; break; }
                if (count == 0)
                    for (int i = 0; i < nu; i++)
                        if (!usedU[i])
                            for (int c = first[i]; c < first[i + 1]; c++)
                                if (!usedV[partner[c]] && Math.Abs(dp[c] - k) == 1) shifts[count++] = dm[c];
                if (count > 0)
                {
                    var found = shifts[..count]; found.Sort();
                    int modal = found[0], most = 0;
                    for (int i = 0; i < count;)
                    {
                        int j = i; while (j < count && found[j] == found[i]) j++;
                        if (j - i > most || j - i == most && Math.Abs(found[i]) < Math.Abs(modal)) { most = j - i; modal = found[i]; }
                        i = j;
                    }
                    for (int i = 0; i < nu; i++)
                        if (!usedU[i])
                            for (int c = first[i]; c < first[i + 1]; c++)
                                if (!usedV[partner[c]] && Math.Abs(dp[c] - k) == 1 && dm[c] == modal) { usedU[i] = usedV[partner[c]] = true; tonal++; break; }
                }
                sims[k - from] = 2 * (exact + .8 * octave + .7 * tonal) / (nu + nv);
            }
        }
        public double Similarity(int u, int v, int k) { Span<double> sim = stackalloc double[1]; Similarities(u, v, k, k, sim); return sim[0]; }
        // §7 E1's S(u, v) = max over k of sim_k − 0.04 for a transposed match, and K(u, v), its argmax (the smaller |k| on a
        // tie, the lower first).
        public (double s, int k) Best(int u, int v)
        {
            Span<double> sims = stackalloc double[13];
            Similarities(u, v, -6, 6, sims);
            double best = double.NegativeInfinity; int at = 0;
            for (int i = 0; i <= 12; i++)
            {
                int k = (i + 1) / 2 * (i % 2 == 1 ? -1 : 1);   // 0, −1, +1, −2, +2, …
                double s = sims[k + 6] - (k != 0 ? .04 : 0);
                if (s > best + 1e-9) { best = s; at = k; }
            }
            return (Math.Max(0, best), at);
        }
    }

    // A tandem block (§6 D2): Count units of Span columns from Column, Unit ticks apart, each the one before under G. Bars:
    // the unit in bars (0: one tactus). Kind and Value: the column relation G stands for. Exactness: the share of compared
    // column pairs G relates exactly. Parent: the block whose unit holds this one, Iteration: which of its units.
    public sealed class Block
    {
        public int Id, Column, Span, Count, Bars, Kind, Value, Parent = -1, Iteration = -1, Relation = -1;
        public long Start, Unit;
        public bool Downbeat;
        public double Exactness;
        public Transform G = Transform.Identity;
        public long End => Start + Count * Unit;
    }

    // One detection, as MotifRelation carries it (ticks here, beats in the bundle). Kind: MotifRelations.Power, Run,
    // Doubling, Alternation or Liquidation.
    // - Power: Count statements Unit apart (Iterations: their starts), each Step from the one before. Source: FromBlock (a
    //   tandem block's units, Block), FromBlocks (Children: the inner powers, one per iteration) or FromCells (a paradigm's
    //   consecutive cells in StreamA: Cells, Paradigm). Parent and Iteration place a power inside an outer one.
    // - Run: Count events of the texture StreamA about Unit apart, each Step from the one before, or alternately the two steps
    //   of Pattern ("-1,+5"): semitones for a Real run, scale steps for a tonal one. Period = n* when the chain returns to its
    //   pitch classes modulo Symmetry (the hand shape's stabiliser), 0 when it never does within its count.
    // - Doubling: StreamB doubles StreamA at the octave registers Pattern lists ("-24,-12") over Count shared onsets. A
    //   Contrary one is a sub-span where their top lines move in opposite directions (the hands in opposite chirality).
    // - Alternation: a both-hands run whose events alternate hands in Pattern ("RRLL").
    // - Liquidation: the principal line's period per bar shrinking as Pattern lists it, in quarters of a tactus ("8,4,2").
    // Order (§6 D9): 1 identity (a loop), 2 an alternation or involution, n a cycle closing within its count, 0 a
    // translation. Label: the generator label (kind, unit, step) the bar labels use.
    public sealed class Relation
    {
        public int Id, Kind, StreamA = -1, StreamB = -1, Count = 1, Period, Order, Paradigm = -1, Block = -1, Parent = -1, Iteration = -1;
        public string Source = "", Text = "", Pattern = "", Label = "";
        public long Start, End, Unit;
        public Transform Step = Transform.Identity;
        public double Bits, Exactness = 1;
        public bool Real, CrossScale, Contrary;   // Real: the step is in semitones (a tonal one counts scale steps)
        public int[] Symmetry = Array.Empty<int>(), Children = Array.Empty<int>(), Cells = Array.Empty<int>();
        public long[] Iterations = Array.Empty<long>();
        public bool IsPower => Kind == PreparedPatternSong.MotifRelations.Power;
    }

    // A bar's dominant generator (§6 D9): the label of the widest detection covering at least half the bar, or the bar
    // before's (Inherited); "" before anything is detected.
    public sealed class BarLabel
    {
        public string Text = "";
        public int Relation = -1;
        public bool Inherited;
    }

    public Columns Cols;
    public Block[] Blocks = Array.Empty<Block>();
    public Relation[] Relations = Array.Empty<Relation>();
    public BarLabel[] Labels = Array.Empty<BarLabel>();   // per measure
    public long[] Phrases = Array.Empty<long>();          // the provisional phrase cuts (span start, arrivals, span end)

    // theta: §7 E2's θ for the near relation (NaN: blocks relate by exact, real and tonal transposition only); boundaries:
    // section boundaries no block may cross (reviewed or pop sections, or stage E's provisional ones).
    public static Iteration Build(MotifInput input, CellLexicon lex, double theta = double.NaN, long[] boundaries = null)
    {
        var it = new Iteration { Cols = Columns.Build(input, theta) };
        var relations = new List<Relation>();
        it.Phrases = ProvisionalPhrases(input, lex);
        it.Blocks = SelectBlocks(input, it.Cols, boundaries ?? Array.Empty<long>(), it.Phrases);
        UnitPowers(input, it, relations);
        CellPowers(input, lex, relations);
        Runs(input, relations);
        Doublings(input, relations);
        Liquidations(input, relations);
        CrossScale(relations);
        it.Relations = relations.ToArray();
        it.Labels = LabelBars(input, it.Relations);
        return it;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §6 D2: tandem blocks.

    // One-tactus units are tried only inside phrases of at most four bars (§6 D2). The phrases are provisional here: §8 F1
    // cuts after each arrival of the principal line (§4 B2), at the end of its tactus; internal returns come later.
    static long[] ProvisionalPhrases(MotifInput input, CellLexicon lex)
    {
        var grid = input.Grid; var cuts = new List<long>();
        if (grid.Columns == 0) return Array.Empty<long>();
        cuts.Add(grid.SpanStart);
        int p = input.Streams.Principal;
        if (p >= 0 && p < lex.Lines.Length && lex.Lines[p] != null)
            foreach (var a in Diminution.Arrivals(input, lex.Lines[p]))
            {
                int c = grid.ColumnAt(a.Tick);
                if (c >= 0 && grid.ColumnEnd[c] > cuts[^1] && grid.ColumnEnd[c] < grid.SpanEnd) cuts.Add(grid.ColumnEnd[c]);
            }
        cuts.Add(grid.SpanEnd);
        return cuts.ToArray();
    }
    static bool InShortPhrase(MotifInput input, long[] phrases, long from, long to)
    {
        int i = Array.BinarySearch(phrases, from); i = i >= 0 ? i : ~i - 1;
        if (i < 0 || i + 1 >= phrases.Length || to > phrases[i + 1]) return false;
        int b = Math.Max(0, input.Grid.BarAt(phrases[i]));
        return phrases[i + 1] - phrases[i] <= 4 * (input.Grid.BarEnd[b] - input.Grid.BarStart[b]);
    }

    // The relations between two units, the columns [c0, c0 + span) against the span after, tallied by kind and value over
    // the column pairs not both silent: how many each relates and how many exactly. Null when the units cannot be compared:
    // the second runs past the last column or over a meter change, or either has no attack (a held chord is no repetition).
    sealed class Tally
    {
        public int Denominator;
        public readonly List<(int kind, int value, int related, int exact)> Entries = new();
        public void Add(int kind, int value, bool exact)
        {
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i].kind == kind && Entries[i].value == value) { var e = Entries[i]; Entries[i] = (kind, value, e.related + 1, e.exact + (exact ? 1 : 0)); return; }
            Entries.Add((kind, value, 1, exact ? 1 : 0));
        }
        public bool Qualifies(int kind, int value, double share, out int exact)
        {
            foreach (var e in Entries) if (e.kind == kind && e.value == value) { exact = e.exact; return e.related >= share * Denominator - 1e-9; }
            exact = 0; return false;
        }
    }
    static Tally Compare(MotifGrid grid, Columns cols, int c0, int span, long unit)
    {
        if (span <= 0 || c0 < 0 || c0 + 2 * span > grid.Columns) return null;
        int struckA = 0, struckB = 0; var t = new Tally();
        for (int j = 0; j < span; j++)
        {
            int u = c0 + j, v = u + span;
            if (grid.ColumnStart[v] - grid.ColumnStart[u] != unit || grid.ColumnEnd[v] - grid.ColumnStart[v] != grid.ColumnEnd[u] - grid.ColumnStart[u]) return null;
            struckA += cols.Struck[u]; struckB += cols.Struck[v];
            var p = cols.Relate(u, v);
            if (p.Empty) continue;
            t.Denominator++;
            if (p.Real) t.Add(RealKind, p.K, true);
            if (p.Tonal) t.Add(TonalKind, p.D, p.Altered == 0);
            for (int k = -6; k <= 6; k++) if (p.Holds(NearKind, k)) t.Add(NearKind, k, p.Real && p.K == k);
        }
        return struckA == 0 || struckB == 0 || t.Denominator == 0 ? null : t;
    }
    // On equal counts the identity reads first, then tonal before real (§5 C6: a tonal image that is also real is named
    // tonal), then the soft relation.
    static int Preference(int kind, int value) => kind == RealKind && value == 0 ? 0 : kind == TonalKind ? 1 : kind == RealKind ? 2 : 3;
    // One relation followed from a block's first pair of units: per pair, the column pairs it relates exactly and those
    // compared. Share(n): its exactness over the first n pairs.
    sealed class Chain : IComparable<Chain>
    {
        public readonly int Kind, Value;
        public bool Open = true;
        readonly List<int> exact = new(), compared = new();
        public Chain(int kind, int value) { Kind = kind; Value = value; }
        public int Pairs => exact.Count;
        public Chain Add(int x, int n) { exact.Add(x); compared.Add(n); return this; }
        public double Share(int pairs) { int x = 0, n = 0; for (int i = 0; i < pairs; i++) { x += exact[i]; n += compared[i]; } return n == 0 ? 0 : x / (double)n; }
        public int CompareTo(Chain o) => Preference(Kind, Value) != Preference(o.Kind, o.Value) ? Preference(Kind, Value).CompareTo(Preference(o.Kind, o.Value))
            : Math.Abs(Value) != Math.Abs(o.Value) ? Math.Abs(Value).CompareTo(Math.Abs(o.Value)) : Value.CompareTo(o.Value);
    }
    static Transform StepOf(int kind, int value)
    {
        if (kind == TonalKind) return value == 0 ? Transform.Identity : new Transform(value, Spelling.NaturalPitch(28 + value) - 60, Transform.Tonal);
        if (value == 0) return Transform.Identity;
        return new Transform(value % 12 == 0 ? value / 12 * 7 : 0, value, Transform.Real);
    }

    // For each unit length (one tactus, then BlockUnitBars) and each start column, the longest chain of units in which one
    // relation g relates ≥ 75% of each unit's columns to the next unit's; among relations reaching the same count, the more
    // exact. Overlapping blocks are then chosen by §6 D2's order (coverage n·U, exactness, a downbeat start, the earlier),
    // then the shorter unit (X⁸ over (X²)⁴: a power's cost does not grow with n), the relation's preference and its size.
    // A chosen block conflicts with every other it overlaps unless one lies inside a single unit of the other: that one
    // is nested there (its parent's iteration), so (U²)² ↑1 comes out as an outer block whose units are blocks. No block
    // crosses a section boundary.
    static Block[] SelectBlocks(MotifInput input, Columns cols, long[] boundaries, long[] phrases)
    {
        var grid = input.Grid; var settings = input.Settings; double share = settings.BlockConsistency;
        var candidates = new List<Block>();
        var sizes = new List<int> { 0 };
        foreach (int b in settings.BlockUnitBars) if (b > 0 && !sizes.Contains(b)) sizes.Add(b);
        bool Crosses(long from, long to) { foreach (long b in boundaries) if (b > from && b < to) return true; return false; }
        foreach (int bars in sizes)
            for (int c = grid.FirstColumn; c < grid.Columns; c++)
            {
                long start = grid.ColumnStart[c]; int bar = grid.ColumnBar[c];
                long unit = bars == 0 ? grid.ColumnEnd[c] - start : bars * (grid.BarEnd[bar] - grid.BarStart[bar]);
                int end = grid.ColumnAt(start + unit);
                if (unit <= 0 || end <= c || end >= grid.Columns || grid.ColumnStart[end] != start + unit) continue;
                int span = end - c;
                // Every relation qualifying between the first two units, followed while it holds: per pair of units, the
                // column pairs it relates exactly and those compared.
                var chains = new List<Chain>();
                for (int i = 0; ; i++)
                {
                    long from = start + i * unit, to = from + 2 * unit;
                    bool allowed = !Crosses(from, to) && to <= grid.SpanEnd && (bars > 0 || InShortPhrase(input, phrases, from, to));
                    var t = allowed ? Compare(grid, cols, c + i * span, span, unit) : null;
                    if (i == 0)
                    {
                        if (t == null) break;
                        foreach (var e in t.Entries) if (e.related >= share * t.Denominator - 1e-9) chains.Add(new Chain(e.kind, e.value).Add(e.exact, t.Denominator));
                    }
                    else
                        foreach (var ch in chains)
                        {
                            if (!ch.Open) continue;
                            if (t != null && t.Qualifies(ch.Kind, ch.Value, share, out int exact)) ch.Add(exact, t.Denominator); else ch.Open = false;
                        }
                    if (!chains.Exists(ch => ch.Open)) break;
                }
                // A candidate for every count the chains reach, each with the best relation reaching it: a shorter chain may
                // nest where the longest crosses another block's unit (B1's first 2-bar repeat inside its 4-bar sequence).
                int longest = 0; foreach (var ch in chains) longest = Math.Max(longest, ch.Pairs);
                for (int pairs = 1; pairs <= longest; pairs++)
                {
                    Chain best = null; double bestShare = 0;
                    foreach (var ch in chains)
                    {
                        if (ch.Pairs < pairs) continue;
                        double x = ch.Share(pairs);
                        if (best == null || x > bestShare + 1e-9 || Math.Abs(x - bestShare) <= 1e-9 && ch.CompareTo(best) < 0) { best = ch; bestShare = x; }
                    }
                    candidates.Add(new Block
                    {
                        Column = c, Span = span, Count = pairs + 1, Bars = bars, Kind = best.Kind, Value = best.Value, Start = start, Unit = unit,
                        Downbeat = start == grid.BarStart[bar], Exactness = bestShare, G = StepOf(best.Kind, best.Value)
                    });
                }
            }
        candidates.Sort((a, b) =>
        {
            int x = (b.Count * b.Unit).CompareTo(a.Count * a.Unit); if (x != 0) return x;
            if (Math.Abs(a.Exactness - b.Exactness) > 1e-9) return b.Exactness.CompareTo(a.Exactness);
            x = b.Downbeat.CompareTo(a.Downbeat); if (x != 0) return x;
            x = a.Start.CompareTo(b.Start); if (x != 0) return x;
            x = a.Unit.CompareTo(b.Unit); if (x != 0) return x;
            x = Preference(a.Kind, a.Value).CompareTo(Preference(b.Kind, b.Value)); if (x != 0) return x;
            x = Math.Abs(a.Value).CompareTo(Math.Abs(b.Value)); if (x != 0) return x;
            x = a.Value.CompareTo(b.Value); if (x != 0) return x;
            return a.Bars.CompareTo(b.Bars);   // a tactus as long as the bar (3/8) gives both sizes one unit
        });
        // Exact, real and tonal blocks are chosen first; soft (near) blocks then fill in. A near block never displaces a block
        // found by an exact relation, except one it extends: the same step, units on its grid, wholly inside it (the étude's
        // 39→40 T−1, which the near relation carries back to 38). Otherwise the shifted soft chains that coverage favours
        // (17.2 → 18.2 → 19.2 at T−3) would push out the exact tonal sequence of bars 18–19.
        var chosen = new List<Block>();
        foreach (bool soft in new[] { false, true })
            foreach (var x in candidates)
            {
                if ((x.Kind == NearKind) != soft) continue;
                var extended = new List<Block>(); bool fits = true;
                foreach (var a in chosen)
                {
                    if (x.End <= a.Start || a.End <= x.Start || Inside(x, a) || Inside(a, x)) continue;
                    if (soft && a.Kind != NearKind && a.Unit == x.Unit && a.G.Equals(x.G) && a.Start >= x.Start && a.End <= x.End && (a.Start - x.Start) % x.Unit == 0) { extended.Add(a); continue; }
                    fits = false; break;
                }
                if (!fits) continue;
                foreach (var a in extended) chosen.Remove(a);
                chosen.Add(x);
            }
        // In time order, outer blocks before the blocks inside them; each block's parent is the innermost block holding it
        // within one unit.
        var result = chosen.OrderBy(b => b.Start).ThenByDescending(b => b.Count * b.Unit).ThenBy(b => b.Unit).ThenBy(b => b.Column).ToArray();
        for (int k = 0; k < result.Length; k++)
        {
            var b = result[k]; b.Id = k; b.Parent = b.Iteration = -1;
            for (int p = 0; p < result.Length; p++)
                if (p != k && Inside(b, result[p]) && (b.Parent < 0 || result[p].Unit < result[b.Parent].Unit)) { b.Parent = p; b.Iteration = (int)((b.Start - result[p].Start) / result[p].Unit); }
        }
        return result;
    }
    // Whether x lies within one unit of a.
    static bool Inside(Block x, Block a)
    {
        if (x.Start < a.Start || x.End > a.End || x == a) return false;
        long i = (x.Start - a.Start) / a.Unit;
        return x.End <= a.Start + (i + 1) * a.Unit;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §6 D3: powers.

    // Every block is a unit power whose units are its iterations; a block filling one unit of another is that power's
    // iteration (a child), so nested powers come out of the block choice. Consecutive top-level blocks with equal unit,
    // count and step, each the one before under one h on ≥ 75% of their columns, become an outer power too (§6 D3).
    static void UnitPowers(MotifInput input, Iteration it, List<Relation> rel)
    {
        var grid = input.Grid; var blocks = it.Blocks;
        foreach (var b in blocks)
        {
            var r = Power(rel, FromBlock, b.Count, b.Unit, b.G, b.Start);
            r.Block = b.Id; r.Exactness = b.Exactness; b.Relation = r.Id;
            r.Label = $"block {Length(grid, b.Start, b.Unit)} {StepText(b.G)}";
            r.Text = $"{Capital(UnitName(grid, b.Start, b.Unit))} {Times(b.Count)}, {How(b.G)}";
        }
        foreach (var b in blocks)
        {
            if (b.Parent < 0) continue;
            var parent = blocks[b.Parent]; var r = rel[b.Relation]; var outer = rel[parent.Relation];
            r.Parent = outer.Id; r.Iteration = b.Iteration;
            if (b.Start == parent.Start + b.Iteration * parent.Unit && b.End == b.Start + parent.Unit) outer.Children = outer.Children.Append(r.Id).ToArray();
        }
        var tops = blocks.Where(b => b.Parent < 0).ToList();   // in time order
        for (int i = 0; i < tops.Count;)
        {
            int j = i; int kind = -1, value = 0; double exact = 0;
            while (j + 1 < tops.Count)
            {
                var a = tops[j]; var b = tops[j + 1];
                if (b.Start != a.End || b.Unit != a.Unit || b.Count != a.Count || !b.G.Equals(a.G)) break;
                var t = Compare(grid, it.Cols, a.Column, a.Count * a.Span, a.Count * a.Unit);
                if (t == null) break;
                if (kind < 0)
                {
                    var q = t.Entries.Where(e => e.related >= input.Settings.BlockConsistency * t.Denominator - 1e-9)
                        .OrderByDescending(e => e.exact).ThenBy(e => Preference(e.kind, e.value)).ThenBy(e => Math.Abs(e.value)).ThenBy(e => e.value).ToList();
                    if (q.Count == 0) break;
                    (kind, value) = (q[0].kind, q[0].value); exact += q[0].exact / (double)t.Denominator;
                }
                else if (t.Qualifies(kind, value, input.Settings.BlockConsistency, out int x)) exact += x / (double)t.Denominator;
                else break;
                j++;
            }
            if (j > i)
            {
                var first = tops[i]; long unit = first.End - first.Start; var h = StepOf(kind, value);
                var r = Power(rel, FromBlocks, j - i + 1, unit, h, first.Start);
                r.Exactness = exact / (j - i);
                r.Children = Enumerable.Range(i, j - i + 1).Select(k => tops[k].Relation).ToArray();
                for (int k = i; k <= j; k++) { var c = rel[tops[k].Relation]; c.Parent = r.Id; c.Iteration = k - i; }
                r.Label = $"block {Length(grid, first.Start, unit)} {StepText(h)}";
                r.Text = $"{Capital(UnitName(grid, first.Start, unit))} ({Length(grid, first.Start, first.Unit)} ×{first.Count}) {Times(j - i + 1)}, {How(h)}";
            }
            i = j + 1;
        }
    }

    // A power's relation; its bits are mdl-grammar's L_pow = γ(n − 2) + 1 + L(step) + 1, which does not grow with n.
    static Relation Power(List<Relation> rel, string source, int count, long unit, Transform step, long start)
    {
        var r = new Relation
        {
            Id = rel.Count, Kind = PreparedPatternSong.MotifRelations.Power, Source = source, Count = count, Unit = unit, Step = step, Start = start, End = start + count * unit,
            Iterations = Enumerable.Range(0, count).Select(k => start + k * unit).ToArray(), Order = PowerOrder(step, count),
            Bits = CellLexicon.Gamma(count - 2) + 1 + step.Cost() + 1, Real = step.Frame == Transform.Real
        };
        rel.Add(r);
        return r;
    }

    // §6 D9's order of a power's step: 1 for the identity; 2 for a mirror or the parallel mode alone (involutions); else the
    // step's order on its clock (12 semitones, or 7 scale steps for a tonal step) when the power returns to its pitch
    // classes within its count, and 0 (a translation) when it keeps rising or falling.
    static int PowerOrder(Transform step, int count)
    {
        if (step.Equals(Transform.Identity)) return 1;
        if (step.Inverted) return 2;   // a mirror about a point, shifted or not, undoes itself
        bool tonal = step.Frame != Transform.Real;
        int shift = tonal ? step.Steps : step.Semitones;
        if (shift == 0) return step.Parallel != 0 ? 2 : 1;
        int order = TransformGroup.Order(shift, tonal ? 7 : 12);
        return count >= order + 1 ? order : 0;
    }

    // A paradigm's consecutive cells in one stream (each starting where the one before ends) whose element from the one
    // before is the same form a cell power (§6 D3: α⁺ at 1.2 → 2.2 is α⁺² ↑1), on the principal stream, every lane's outer
    // line and the drum streams (the loops of pop). The step is real when the paradigm lives in semitones or either member
    // is placed really, else tonal (its scale steps must agree; the semitones of a tonal step may differ). A cell joins
    // one power at most.
    static void CellPowers(MotifInput input, CellLexicon lex, List<Relation> rel)
    {
        var streams = input.Streams.All;
        for (int s = 0; s < streams.Length && s < lex.StreamCount.Length; s++)
        {
            if (s != input.Streams.Principal && streams[s].Kind is not (MotifStreams.Line or MotifStreams.Drums)) continue;
            var cells = lex.Of(s).ToList();
            for (int i = 0; i < cells.Count;)
            {
                var a = cells[i];
                if (a.Paradigm < 0) { i++; continue; }
                var step = Between(lex, a, a); int j = i + 1;
                while (j < cells.Count && cells[j].Paradigm == a.Paradigm && cells[j].Start == cells[j - 1].End)
                {
                    var here = Between(lex, cells[j - 1], cells[j]);
                    if (j == i + 1) step = here; else if (!Same(here, step)) break;
                    j++;
                }
                if (j - i < 2) { i++; continue; }
                var p = lex.Paradigms[a.Paradigm];
                var r = Power(rel, FromCells, j - i, a.End - a.Start, step, a.Start);
                r.StreamA = s; r.Paradigm = p.Id; r.Cells = cells.Skip(i).Take(j - i).Select(c => c.Id).ToArray();
                r.Iterations = cells.Skip(i).Take(j - i).Select(c => c.Start).ToArray(); r.End = cells[j - 1].End;
                r.Label = $"{p.Letter} {StepText(step)}";
                r.Text = $"{p.Name} ({p.Letter}) {Times(j - i)}, {How(step)}";
                i = j;
            }
        }
    }
    static Transform Between(CellLexicon lex, CellLexicon.Cell a, CellLexicon.Cell b)
    {
        var p = lex.Paradigms[a.Paradigm];
        bool real = p.Space != CellLexicon.Diatonic || a.G.Frame == Transform.Real || b.G.Frame == Transform.Real;
        int steps = b.G.Steps - a.G.Steps, semitones = b.G.Semitones - a.G.Semitones, parallel = b.G.Parallel - a.G.Parallel;
        bool inverted = a.G.Inverted != b.G.Inverted;
        if (semitones == 0 && steps == 0 && parallel == 0 && !inverted) return Transform.Identity;
        return new Transform(steps, semitones, real ? Transform.Real : Transform.Tonal, inverted, parallel: parallel);
    }
    static bool Same(Transform a, Transform b) => a.Frame == b.Frame && a.Inverted == b.Inverted && a.Parallel == b.Parallel && (a.Frame == Transform.Real ? a.Semitones == b.Semitones : a.Steps == b.Steps);

    // ---------------------------------------------------------------------------------------------------------------
    // §6 D4–D6: runs, cycles and alternation.

    // A run on a texture or both-hands stream: a maximal chain x, g x, …, g^{n−1} x of at least RunMinEvents sonorities with
    // one hand shape, the steps of its lowest note periodic with period 1 or 2, and no silence longer than half a tactus
    // between one sonority's end and the next onset. As §6 D1 relates columns, g is real or tonal: a real run keeps its
    // shape (the intervals above the lowest note) and its periodic steps in semitones, a tonal run keeps both in scale steps,
    // its alterations free. F7's tritones are real (the speller writes them alternately as augmented fourths and diminished
    // fifths); F9's major and minor thirds, and the étude's sixths in 53 (major, then minor, the lowest note moving by a
    // tritone, then a fourth, always a fifth in steps), are tonal. A run that is both is real. The cycle is the run in
    // pitch-class space modulo Stab(shape) (§6 D4): with m = 12/|Stab| (7 for a tonal run, on the scale) and j the net step
    // per period, n* = m / gcd(m, |j|) periods; the run closes when it lasts that many periods. A run of one hand on the
    // both-hands stream repeats its texture's run and is left out; one alternating hands periodically is also an
    // Alternation (§6 D6).
    static void Runs(MotifInput input, List<Relation> rel)
    {
        var ev = input.Events; var grid = input.Grid; int min = Math.Max(3, input.Settings.RunMinEvents), period = Math.Clamp(input.Settings.RunMaxPeriod, 1, 2);
        foreach (var s in input.Streams.All)
        {
            if (s.Kind is not (MotifStreams.Texture or MotifStreams.Both)) continue;
            int n = s.Events; if (n < min) continue;
            var pitch = new int[n][]; var morph = new int[n][]; var end = new long[n]; var hand = new char[n];
            for (int k = 0; k < n; k++)
            {
                var notes = new int[s.Count[k]]; for (int q = 0; q < notes.Length; q++) notes[q] = s.Notes[s.First[k] + q];
                Array.Sort(notes, (x, y) => ev[x].Pitch != ev[y].Pitch ? ev[x].Pitch.CompareTo(ev[y].Pitch) : ev[x].Morph != ev[y].Morph ? ev[x].Morph.CompareTo(ev[y].Morph) : x.CompareTo(y));
                pitch[k] = new int[notes.Length]; morph[k] = new int[notes.Length]; bool upper = false, lower = false;
                for (int q = 0; q < notes.Length; q++)
                {
                    var e = ev[notes[q]]; pitch[k][q] = e.Pitch; morph[k][q] = e.Morph; end[k] = Math.Max(end[k], e.Tick + e.Duration);
                    if (e.Hand == 1) lower = true; else upper = true;
                }
                hand[k] = upper && lower ? 'B' : lower ? 'L' : 'R';
            }
            // Whether b has a's shape in semitones and in scale steps.
            (bool semitones, bool steps) Shape(int a, int b)
            {
                if (pitch[a].Length != pitch[b].Length) return (false, false);
                bool semitones = true, steps = true;
                for (int q = 1; q < pitch[a].Length; q++)
                {
                    if (pitch[a][q] - pitch[a][0] != pitch[b][q] - pitch[b][0]) semitones = false;
                    if (morph[a][q] - morph[a][0] != morph[b][q] - morph[b][0]) steps = false;
                }
                return (semitones, steps);
            }
            int Step(int k) => pitch[k + 1][0] - pitch[k][0];
            int Degree(int k) => morph[k + 1][0] - morph[k][0];
            for (int i = 0; i + min <= n;)
            {
                // Real and tonal followed side by side; the run goes on while either holds (F7's tritones end at their
                // fourth, a fourth in steps too but no longer a tritone).
                int j = i; bool real = true, tonal = true;
                while (j + 1 < n && s.Onset[j + 1] - end[j] <= grid.TactusAt(s.Onset[j]) / 2)
                {
                    var (semitones, steps) = Shape(j, j + 1);
                    bool r = real && semitones && (j - i < period || Step(j) == Step(j - period)), t = tonal && steps && (j - i < period || Degree(j) == Degree(j - period));
                    if (!r && !t) break;
                    real = r; tonal = t; j++;
                }
                if (j - i + 1 < min) { i++; continue; }
                bool alternating = false; for (int k = i + 1; k < j; k++) if (real ? Step(k) != Step(i) : Degree(k) != Degree(i)) alternating = true;
                bool oneHand = s.Kind == MotifStreams.Both && hand[i] != 'B';
                for (int k = i; k <= j && oneHand; k++) oneHand = hand[k] == hand[i];
                if (!oneHand) AddRun(input, rel, s, i, j, real, alternating, pitch, morph, end, hand);
                i = j;
            }
        }
    }
    static void AddRun(MotifInput input, List<Relation> rel, MotifStreams.Stream s, int i, int j, bool real, bool alternating, int[][] pitch, int[][] morph, long[] end, char[] hand)
    {
        var step = real ? pitch : morph;   // the lowest note's steps: semitones, or scale steps for a tonal run
        int n = j - i + 1, a = step[i + 1][0] - step[i][0], b = alternating ? step[i + 2][0] - step[i + 1][0] : a;
        int[] stab = new[] { 0 };
        if (real) { stab = null; for (int k = i; k <= j; k++) { var here = TransformGroup.Stabiliser(pitch[k]); stab = stab == null ? here : stab.Intersect(here).ToArray(); } }
        int m = real ? 12 / Math.Max(1, stab.Length) : 7, net = alternating ? a + b : a;
        int periods = alternating ? (n - 1) / 2 : n - 1, cycle = TransformGroup.Order(net, m);
        bool loop = Spelling.Mod(net, m) == 0, closes = periods >= cycle;
        var gaps = new List<long>(); for (int k = i; k < j; k++) gaps.Add(s.Onset[k + 1] - s.Onset[k]);
        gaps.Sort();
        int semitones = pitch[i + 1][0] - pitch[i][0];
        var r = new Relation
        {
            Id = rel.Count, Kind = PreparedPatternSong.MotifRelations.Run, StreamA = s.Id, Count = n, Start = s.Onset[i], End = end[j], Unit = gaps[(gaps.Count - 1) / 2],
            Step = a == 0 && (real || semitones == 0) ? Transform.Identity : real ? new Transform(0, a, Transform.Real) : new Transform(a, semitones, Transform.Tonal), Symmetry = stab,
            Period = closes ? cycle : 0, Order = loop ? (alternating ? 2 : 1) : closes ? cycle : 0, Pattern = alternating ? Plain(a) + "," + Plain(b) : "",
            Iterations = Enumerable.Range(i, n).Select(k => s.Onset[k]).ToArray(), Real = real
        };
        string shape = ShapeName(pitch[i], real ? stab : Array.Empty<int>()), letter = real ? "T" : "t";
        r.Label = "run " + (alternating ? $"{letter}{Signed(a)}/{letter}{Signed(b)}" : a == 0 ? "=" : letter + Signed(a));
        string Move(int v) => real ? Moves(v) : v == 0 ? "in place" : $"{(v < 0 ? "falling" : "rising")} {A(TransformGroup.DiatonicInterval(Math.Abs(v)))}";
        r.Text = alternating
            ? $"{shape} alternating {Move(a)} and {Move(b)}" + (loop ? " (an alternation)" : closes ? $", {Move(net)} a pair: a {cycle}-cycle" : "")
            : $"{shape} {(a == 0 ? "repeated" : "each " + Move(a))}" + (closes && !loop ? $": a {cycle}-cycle" : "");
        rel.Add(r);
        if (s.Kind != MotifStreams.Both) return;
        // §6 D6: the hands alternating in a periodic pattern (R R L L …).
        for (int k = i; k <= j; k++) if (hand[k] == 'B') return;
        for (int q = 2; 2 * q <= n; q++)
        {
            bool periodic = true;
            for (int k = i + q; k <= j && periodic; k++) periodic = hand[k] == hand[k - q];
            string pattern = new string(hand, i, q);
            if (!periodic || !pattern.Contains('R') || !pattern.Contains('L')) continue;
            rel.Add(new Relation
            {
                Id = rel.Count, Kind = PreparedPatternSong.MotifRelations.Alternation, StreamA = s.Id, Count = n, Start = r.Start, End = r.End, Unit = r.Unit, Order = 2, Pattern = pattern,
                Label = "alternation", Text = $"the hands alternate {string.Join(" ", pattern.ToCharArray())} in {shape} {(alternating || a == 0 ? "" : "each " + Move(a))}".TrimEnd(), Iterations = r.Iterations, Real = real
            });
            return;
        }
    }

    // §6 D5. Stream B doubles stream A over at least DoublingMinOnsets consecutive shared onsets (no onset of either between)
    // when each of B's sonorities is A's moved by a whole number of octaves K_i, listed in Pattern in order of appearance;
    // A is the upper hand (else the part above). A shared onset B does not double (the étude's bar 51 has one: G♯3 under
    // the right hand's A5) is tolerated when isolated: a doubling is a stretch of shared onsets of which at least
    // DoublingShare are octave copies (Stretches), starting and ending on one. Inside a doubling, Contrary marks the
    // stretches of at least ContraryMinEvents events of which at least ContraryShare are reached by opposite steps of the two
    // top lines: from the first note the hands reach moving apart (52.2 in the étude), not the note they leave together.
    static void Doublings(MotifInput input, List<Relation> rel)
    {
        var ev = input.Events; var settings = input.Settings;
        var textures = input.Streams.All.Where(s => s.Kind == MotifStreams.Texture).ToList();
        int[] Sonority(MotifStreams.Stream s, int k)
        {
            var p = new int[s.Count[k]]; for (int q = 0; q < p.Length; q++) p[q] = ev[s.Notes[s.First[k] + q]].Pitch;
            Array.Sort(p); return p;
        }
        long End(MotifStreams.Stream s, int k) { long e = 0; for (int q = 0; q < s.Count[k]; q++) { var x = ev[s.Notes[s.First[k] + q]]; e = Math.Max(e, x.Tick + x.Duration); } return e; }
        for (int x = 0; x < textures.Count; x++)
            for (int y = x + 1; y < textures.Count; y++)
            {
                var A = textures[x]; var B = textures[y];
                var shared = new List<(int a, int b, int k, bool copy)>();
                void Close()
                {
                    var copy = shared.Select(t => t.copy).ToArray();
                    foreach (var (first, last) in Stretches(copy, settings.DoublingShare, settings.DoublingMinOnsets, 1))
                        AddDoubling(input, rel, A, B, shared.GetRange(first, last - first + 1), Sonority, End);
                    shared.Clear();
                }
                for (int p = 0, q = 0; p < A.Events || q < B.Events;)
                {
                    if (p < A.Events && q < B.Events && A.Onset[p] == B.Onset[q])
                    {
                        var sa = Sonority(A, p); var sb = Sonority(B, q); int k = sb[0] - sa[0]; bool copy = sa.Length == sb.Length && Spelling.Mod(k, 12) == 0;
                        for (int i = 1; i < sa.Length && copy; i++) copy = sb[i] - sa[i] == k;
                        shared.Add((p, q, k, copy));
                        p++; q++;
                    }
                    else
                    {
                        Close();
                        if (q >= B.Events || p < A.Events && A.Onset[p] < B.Onset[q]) p++; else q++;
                    }
                }
                Close();
            }
    }
    static void AddDoubling(MotifInput input, List<Relation> rel, MotifStreams.Stream A, MotifStreams.Stream B, List<(int a, int b, int k, bool copy)> shared, Func<MotifStreams.Stream, int, int[]> sonority, Func<MotifStreams.Stream, int, long> end)
    {
        // The upper part is A: the upper hand, else the part above (K ≤ 0).
        var first = shared.First(t => t.copy);
        bool swap = A.Hand == 1 && B.Hand == 0 || A.Hand == B.Hand && first.k > 0;
        var upper = swap ? B : A; var lower = swap ? A : B; int sign = swap ? -1 : 1;
        var list = shared.Select(t => (a: swap ? t.b : t.a, b: swap ? t.a : t.b, k: sign * t.k, t.copy)).ToList();
        string Registers(int from, int to) { var r = new List<int>(); for (int k = from; k <= to; k++) if (list[k].copy && !r.Contains(list[k].k)) r.Add(list[k].k); return string.Join(",", r.Select(Plain)); }
        string pattern = Registers(0, list.Count - 1);
        rel.Add(new Relation
        {
            Id = rel.Count, Kind = PreparedPatternSong.MotifRelations.Doubling, StreamA = upper.Id, StreamB = lower.Id, Count = list.Count, Start = upper.Onset[list[0].a],
            End = Math.Max(end(upper, list[^1].a), end(lower, list[^1].b)), Pattern = pattern, Label = "doubling", Iterations = list.Select(t => upper.Onset[t.a]).ToArray(),
            Exactness = list.Count(t => t.copy) / (double)list.Count,
            Text = $"{lower.Name} doubles {upper.Name} " + string.Join(", then ", pattern.Split(',').Select(Number).Select(Register))
        });
        // Each event is reached by a step of both top lines (the first is reached by none).
        var opposite = new bool[list.Count];
        for (int k = 1; k < list.Count; k++)
        {
            int da = sonority(upper, list[k].a)[^1] - sonority(upper, list[k - 1].a)[^1], db = sonority(lower, list[k].b)[^1] - sonority(lower, list[k - 1].b)[^1];
            opposite[k] = Math.Sign(da) * Math.Sign(db) < 0;
        }
        foreach (var (i, j) in Stretches(opposite, input.Settings.ContraryShare, 1, input.Settings.ContraryMinEvents))
            rel.Add(new Relation
            {
                Id = rel.Count, Kind = PreparedPatternSong.MotifRelations.Doubling, StreamA = upper.Id, StreamB = lower.Id, Count = j - i + 1, Contrary = true, Order = 2,
                Start = upper.Onset[list[i].a], End = Math.Max(end(upper, list[j].a), end(lower, list[j].b)), Label = "doubling", Pattern = Registers(i, j),
                Iterations = list.Skip(i).Take(j - i + 1).Select(t => upper.Onset[t.a]).ToArray(), Text = $"{lower.Name} and {upper.Name} move in contrary motion (opposite chirality)"
            });
    }

    // The best-scoring stretches of a sequence of hits and misses, a hit scoring +1 and a miss −share/(1 − share), so that a
    // stretch scoring ≥ 0 holds at least that share of hits: the best first, then the best on either side of it, each
    // starting and ending on a hit, with at least `hits` hits and `length` entries. Ties: the longer, then the earlier.
    // An isolated miss is bridged by enough hits around it; a run of misses ends the stretch.
    static List<(int first, int last)> Stretches(bool[] hit, double share, int hits, int length)
    {
        var found = new List<(int first, int last)>();
        double miss = -share / Math.Max(1e-9, 1 - share);
        void Search(int from, int to)
        {
            if (to - from < Math.Max(hits, length)) return;
            double best = 0; int bi = -1, bj = -1;
            for (int i = from; i < to; i++)
            {
                if (!hit[i] || i > from && hit[i - 1]) continue;   // a stretch starting after a hit is beaten by the one starting there
                double sum = 0; int count = 0;
                for (int j = i; j < to; j++)
                {
                    sum += hit[j] ? 1 : miss; if (hit[j]) count++;
                    if (sum < -1e-9) break;
                    if (hit[j] && count >= hits && j - i + 1 >= length && (bi < 0 || sum > best + 1e-9 || Math.Abs(sum - best) <= 1e-9 && j - i > bj - bi)) { best = sum; bi = i; bj = j; }
                }
            }
            if (bi < 0) return;
            found.Add((bi, bj)); Search(from, bi); Search(bj + 1, to);
        }
        Search(0, hit.Length);
        found.Sort();
        return found;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §6 D7: liquidation.

    // Per bar, the principal line's period: the shorter of half a tactus and a tactus at which ≥ 75% of the bar's notes
    // repeat (a note repeats when the same pitch sounds that far before or after it within the bar); else the bar when the
    // bar repeats a neighbour (≥ 75% of the two bars' notes at the same places, as a Dice share), so a bar whose first
    // note is held over from the bar before still repeats it (the étude's 54–55); else none. A liquidation is at least
    // LiquidationMinBars consecutive bars whose period never grows and ends at ≤ ¼ of where it started; Pattern lists the
    // periods in quarters of a tactus ("8,4,2": bar, beat, half beat in 2/4).
    static void Liquidations(MotifInput input, List<Relation> rel)
    {
        int p = input.Streams.Principal; if (p < 0) return;
        var s = input.Streams[p]; var grid = input.Grid; var ev = input.Events; var settings = input.Settings;
        (int first, int count) Range(int b) { int f = LowerBound(s.Onset, grid.BarStart[b]); return (f, LowerBound(s.Onset, grid.BarEnd[b]) - f); }
        bool At(int f, int count, long tick, int pitch) { for (int k = f; k < f + count; k++) if (s.Onset[k] == tick && ev[s.Lead[k]].Pitch == pitch) return true; return false; }
        var period = new int[grid.Bars];
        for (int b = grid.FirstBar; b < grid.Bars; b++)
        {
            var (f, count) = Range(b); if (count == 0) continue;
            long quarter = Math.Max(1, grid.Unit[b] / 4);
            foreach (long lag in new[] { grid.Unit[b] / 2, grid.Unit[b] })
            {
                if (lag <= 0) continue;
                int repeated = 0;
                for (int k = f; k < f + count; k++) if (At(f, count, s.Onset[k] - lag, ev[s.Lead[k]].Pitch) || At(f, count, s.Onset[k] + lag, ev[s.Lead[k]].Pitch)) repeated++;
                if (repeated >= settings.LiquidationShare * count - 1e-9) { period[b] = (int)(lag / quarter); break; }
            }
            if (period[b] > 0) continue;
            long length = grid.BarEnd[b] - grid.BarStart[b];
            foreach (int nb in new[] { b - 1, b + 1 })
            {
                if (nb < grid.FirstBar || nb >= grid.Bars || grid.BarEnd[nb] - grid.BarStart[nb] != length) continue;
                var (g, other) = Range(nb); if (other == 0) continue;
                int same = 0;
                for (int k = f; k < f + count; k++) if (At(g, other, s.Onset[k] - grid.BarStart[b] + grid.BarStart[nb], ev[s.Lead[k]].Pitch)) same++;
                if (2.0 * same >= settings.LiquidationShare * (count + other) - 1e-9) { period[b] = (int)(length / quarter); break; }
            }
        }
        for (int b = grid.FirstBar; b < grid.Bars;)
        {
            if (period[b] == 0) { b++; continue; }
            int e = b; while (e + 1 < grid.Bars && period[e + 1] > 0 && period[e + 1] <= period[e]) e++;
            if (e - b + 1 >= settings.LiquidationMinBars && period[e] <= settings.LiquidationEnd * period[b] + 1e-9)
            {
                var values = new List<int>(); for (int k = b; k <= e; k++) if (!values.Contains(period[k])) values.Add(period[k]);
                int bar = (int)((grid.BarEnd[b] - grid.BarStart[b]) / Math.Max(1, grid.Unit[b] / 4));
                rel.Add(new Relation
                {
                    Id = rel.Count, Kind = PreparedPatternSong.MotifRelations.Liquidation, StreamA = p, Count = e - b + 1, Start = grid.BarStart[b], End = grid.BarEnd[e], Unit = grid.BarEnd[b] - grid.BarStart[b],
                    Order = 1, Pattern = string.Join(",", values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))), Label = "liquidation", Iterations = Enumerable.Range(b, e - b + 1).Select(k => grid.BarStart[k]).ToArray(),
                    Text = "the figure liquidates: its period shrinks from " + string.Join(" to ", values.Select(v => v == bar ? "the bar" : v == 4 ? "the beat" : v == 2 ? "half a beat" : (v / 4.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " beats"))
                });
                b = e + 1;
            }
            else b++;
        }
    }
    static int LowerBound(long[] a, long v) { int lo = 0, hi = a.Length; while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] < v) lo = mid + 1; else hi = mid; } return lo; }

    // ---------------------------------------------------------------------------------------------------------------
    // §6 D8–D9: the cross-scale flag and the bar labels.

    // A step that moves a texture run at lag a (per event, or per pair for an alternating run) and a unit power at lag
    // b ≥ 4a, over overlapping time, is one generator at two scales: both relations are flagged (the étude's T−1 per
    // sixteenth in the left hand and per bar, 38–40). Real steps compare in semitones, tonal ones in scale steps. The
    // identity is left out: every loop inside a loop would qualify.
    static void CrossScale(List<Relation> rel)
    {
        foreach (var run in rel)
        {
            if (run.Kind != PreparedPatternSong.MotifRelations.Run || run.Order == 1 && run.Pattern.Length == 0) continue;
            bool alternating = run.Pattern.Length > 0;
            int step = alternating ? run.Pattern.Split(',').Sum(Number) : run.Real ? run.Step.Semitones : run.Step.Steps;   // semitones, or scale steps for a tonal run
            long lag = alternating ? 2 * run.Unit : run.Unit;
            if (step == 0) continue;
            foreach (var power in rel)
            {
                if (!power.IsPower || power.Source == FromCells || power.Real != run.Real || (run.Real ? power.Step.Semitones : power.Step.Steps) != step || power.Step.Inverted) continue;
                if (power.Unit < 4 * lag || power.End <= run.Start || run.End <= power.Start) continue;
                power.CrossScale = run.CrossScale = true;
            }
        }
    }

    // §6 D9. Each bar takes the label of the widest detection (span) covering at least half of it: a unit power, a run, a
    // doubling, an alternation or a liquidation, in that priority on equal width, then the earlier. Cell powers and
    // contrary sub-spans are details, not a bar's generator. An unlabelled bar takes the label of the bar before.
    static BarLabel[] LabelBars(MotifInput input, Relation[] rel)
    {
        var grid = input.Grid; var labels = new BarLabel[grid.Bars];
        int Priority(Relation r) => r.Kind switch
        {
            PreparedPatternSong.MotifRelations.Power => 0, PreparedPatternSong.MotifRelations.Run => 1, PreparedPatternSong.MotifRelations.Doubling => 2,
            PreparedPatternSong.MotifRelations.Alternation => 3, _ => 4
        };
        var detections = rel.Where(r => (r.Kind != PreparedPatternSong.MotifRelations.Power || r.Source != FromCells) && !r.Contrary).ToList();
        for (int b = 0; b < grid.Bars; b++)
        {
            labels[b] = new BarLabel();
            if (b < grid.FirstBar) continue;
            long from = grid.BarStart[b], to = grid.BarEnd[b];
            Relation best = null;
            foreach (var r in detections)
            {
                long covered = Math.Min(to, r.End) - Math.Max(from, r.Start);
                if (2 * covered < to - from) continue;
                if (best == null || r.End - r.Start > best.End - best.Start || r.End - r.Start == best.End - best.Start && (Priority(r) < Priority(best) || Priority(r) == Priority(best) && (r.Start < best.Start || r.Start == best.Start && r.Id < best.Id))) best = r;
            }
            if (best != null) { labels[b].Text = best.Label; labels[b].Relation = best.Id; }
            else if (b > grid.FirstBar && labels[b - 1].Text.Length > 0) { labels[b].Text = labels[b - 1].Text; labels[b].Relation = labels[b - 1].Relation; labels[b].Inherited = true; }
        }
        return labels;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Names: the musician's sentences of the relations.

    static string Signed(int v) => v < 0 ? "−" + -v : "+" + v;
    // Patterns are read back (CrossScale sums a run's steps), so they are written and read with ASCII signs whatever the
    // culture (some write U+2212 for minus): "-1,+5", the contract's "-24,-12".
    static string Plain(int v) => v.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture);
    static int Number(string s) => int.Parse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture);
    static string StepText(Transform g) => g.Equals(Transform.Identity) ? "=" : g.Octaves != 0 ? $"{(g.Octaves < 0 ? "−" : "+")}{Math.Abs(g.Octaves)}8ve"
        : g.Frame == Transform.Real ? "T" + Signed(g.Semitones) + (g.Inverted ? " I" : "") : (g.Steps != 0 ? "t" + Signed(g.Steps) : "") + (g.Inverted ? " I" : "") + (g.Parallel != 0 ? " P" : "");
    static string Times(int n) => n == 2 ? "twice" : n == 3 ? "three times" : $"{n} times";
    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    static string How(Transform g)
    {
        if (g.Equals(Transform.Identity)) return "repeated";
        string name = TransformGroup.Name(g);
        return "each " + (name.StartsWith("tonal", StringComparison.Ordinal) || name.StartsWith("real", StringComparison.Ordinal) || name.StartsWith("harmonic", StringComparison.Ordinal) ? "a " : "") + name + " from the one before";
    }
    static string UnitName(MotifGrid grid, long start, long unit)
    {
        int b = Math.Max(0, grid.BarAt(start)); long bar = grid.BarEnd[b] - grid.BarStart[b];
        if (unit % bar == 0) return unit == bar ? "the bar" : $"the {unit / bar}-bar unit";
        if (unit == grid.Unit[b]) return "the beat";
        return $"the {(unit / (double)grid.Unit[b]).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}-beat unit";
    }
    // A unit's length as the bar labels write it (§6 D9: "block 2 bars", "block 1 bar"): bars, else beats.
    static string Length(MotifGrid grid, long start, long unit)
    {
        int b = Math.Max(0, grid.BarAt(start)); long bar = grid.BarEnd[b] - grid.BarStart[b];
        if (unit % bar == 0) return unit == bar ? "1 bar" : $"{unit / bar} bars";
        double beats = unit / (double)grid.Unit[b];
        return beats == 1 ? "1 beat" : beats.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " beats";
    }
    static string Moves(int semitones) => semitones == 0 ? "in place" : $"{(semitones < 0 ? "falling" : "rising")} {A(TransformGroup.ChromaticInterval(Math.Abs(semitones)))}";
    static string A(string word) => ("aeiou".Contains(word[0]) ? "an " : "a ") + word;
    static string Register(int k) => k == 0 ? "in unison" : $"{(Math.Abs(k) == 12 ? "an octave" : $"{Math.Abs(k) / 12} octaves")} {(k < 0 ? "lower" : "higher")}";
    static string ShapeName(int[] pitches, int[] stab)
    {
        if (pitches.Length == 1) return "notes";
        if (pitches.Length == 2)
        {
            int d = pitches[1] - pitches[0];
            return d switch { 3 or 4 => "thirds", 5 => "fourths", 6 => "tritones", 7 => "fifths", 8 or 9 => "sixths", 10 or 11 => "sevenths", 12 => "octaves", _ => "dyads" };
        }
        if (stab.Length == 4) return "diminished sevenths";
        if (stab.Length == 3) return "augmented triads";
        return "chords";
    }
}
