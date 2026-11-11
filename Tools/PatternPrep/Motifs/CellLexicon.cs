using System.Numerics;
using System.Text;

// Stage C of the motif reduction (Docs/MOTIF-REDUCTION.md §5): every stream cut into cells by Ruwet's criterion (a unit
// is what repeats), cells grouped into paradigms (exact orbits under tonal and real transposition and the mirror, plus
// variants joined with a residual), each member placed by its cheapest group element and named as a musician would,
// and paradigms gathered into atoms (families with one diminution signature) whose sign census is their chirality.
// Every cell keeps its source notes, and every member is decoded back from its prototype, element and sparks here, so
// the lossless gate of §9 G4 can be tested stage by stage.
public sealed class CellLexicon
{
    // A window's pitch space: nothing sounds; morph steps; semitone steps (a chromatic window, ≥ 30% of its notes off the
    // local scale); a texture (semitone steps of the lowest note and each sonority's hand shape); drums (rhythm and notes).
    public const int Rest = 0, Diatonic = 1, Chromatic = 2, Texture = 3, Drum = 4;
    public static readonly string[] SpaceNames = { "rest", "diatonic", "chromatic", "texture", "drums" };

    // Signed Elias-gamma length (§9 G1): γ(0) = 1, otherwise 2⌊log2(|v| + 1)⌋ + 2.
    public static int Gamma(long v) => v == 0 ? 1 : 2 * BitOperations.Log2((ulong)Math.Abs(v) + 1) + 2;

    // One candidate window of a stream (§5 C1): positions [Position, End) of the stream's grid, the stream events with
    // onsets inside, and the note held into its start (a tie pseudo-event). Its orbit key (§5 C2) leaves out the anchor:
    // (length, rhythm in slots with the tie flag, the steps or their mirror, whichever is lexicographically smaller).
    public sealed class Window
    {
        public int Stream, Index, Position, End;
        public long Start, Stop;
        public int First, Count, Tie = -1;
        public int Space;
        public string Key = "";
        public int KeyId = -1;            // the key interned per stream: orbits are counted by it
        public bool Inverted, Achiral, Oversize;
        public int Delta;                 // the first note's move from the note before (morphs, or semitones off the diatonic space)
        public int AnchorMorph, AnchorPitch;   // the first note (the tied-in one when there is one)
        public double Lit;
        internal int Orbit = -1;          // this round's exact orbit
        internal Join Join;               // this round's fuzzy join
        internal double Cost;             // this round's Viterbi cost
        internal Element[] Cached;        // its notes as the fuzzy join compares them
        public int Events => Count + (Tie >= 0 ? 1 : 0);
        public long Length => Stop - Start;
        public override string ToString() => $"{SpaceNames[Space]} [{Start}, {Stop}) {Events} events";
    }

    // One note of a window as the fuzzy join compares it: its slot from the window start, its value (morph, or pitch off
    // the diatonic space), whether it repeats the note before (interval 0) or is an ornament (spans at most a slot).
    internal struct Element
    {
        public long Onset;
        public int Slot, Value, Pitch, Morph, Alter, Event, Note;
        public bool Tied, Repeat, Ornament;
    }

    // A variant's place in a paradigm (§5 C4): the target is the prototype it joins; Pair maps each of the window's
    // elements to the prototype element it matches (−1: inserted), Deleted marks prototype elements it leaves out.
    internal sealed class Join
    {
        public Window Target;
        public bool Inverted, Head;
        public int Chi, Shift, Steps, Semitones, Edits;
        public double Distance, Bits, Rho;   // Bits: L(g) + L(ρ) for the MDL gate; Rho: what the Viterbi adds to a member's cost
        public int[] Pair = Array.Empty<int>();
        public bool[] Deleted = Array.Empty<bool>();
    }

    // A residual edit (a white spark, MotifSparks kinds). Note: the prototype element it concerns (−1 for an inserted
    // note; elements count the tied-in note first); Event: the member's stream event (−1 for a deleted note); Shift:
    // ticks a shifted note moved; Tag names it ("♭6 borrowed from C minor", "held 13/16", "P-").
    public struct Spark
    {
        public int Kind, Pitch, Morph, Note, Event;
        public long Tick, Shift;
        public string Tag;
    }

    // A cell of a stream's final tiling: a paradigm member (Paradigm ≥ 0), or literal material (−1). G places it from
    // the prototype (the transform fields of MotifCell); Text names its relation to the nearest earlier exact member.
    public sealed class Cell
    {
        public int Id, Stream, Paradigm = -1;
        public Window W;
        public int[] Notes = Array.Empty<int>();       // source notes: every input event struck in the cell (onset, pitch order)
        public bool Exact = true, Decoded;
        public Transform G = Transform.Identity;
        public Alteration[] Altered = Array.Empty<Alteration>();
        public string Mode = "", Text = "";
        public Spark[] Sparks = Array.Empty<Spark>();
        public int Sign, Degree;
        public double Bits;
        public long Start => W.Start;
        public long End => W.Stop;
        public bool Inverted => G.Inverted;
    }

    public sealed class Paradigm
    {
        public int Id, Stream, Space, Prototype, Atom = -1, Clock = 7, Sign, Mirrored, Variants;
        public string Key = "", Signature = "", Kind = "", Letter = "", Name = "";
        public bool Achiral, HeldHead;
        public int[] Members = Array.Empty<int>();     // cells in time order, the prototype first
        public int[] Symmetry = Array.Empty<int>();
    }

    public sealed class Atom
    {
        public int Id, Members, Positive, Negative, Neutral;
        public string Kind = "", Name = "", Signature = "", Letter = "";
        public int[] Paradigms = Array.Empty<int>();
    }

    public Cell[] Cells = Array.Empty<Cell>();
    public Paradigm[] Paradigms = Array.Empty<Paradigm>();
    public Atom[] Atoms = Array.Empty<Atom>();
    public int[] StreamFirst = Array.Empty<int>(), StreamCount = Array.Empty<int>();   // each stream's cells, in time order
    public Diminution.Line[] Lines = Array.Empty<Diminution.Line>();
    public string[] DecodeErrors = Array.Empty<string>();

    // When set, the EM rounds and the starts' costs of the principal stream are reported here (--verbose, reports).
    public static Action<string> Trace;

    sealed class StreamWork
    {
        public MotifStreams.Stream S;
        public Diminution.Line Line;
        public long[] Pos = Array.Empty<long>();
        public List<Window> Windows = new();
        public List<Window>[] EndingAt = Array.Empty<List<Window>>();
        public Window[] Tiling = Array.Empty<Window>();
        public readonly Dictionary<long, Join> Aligned = new();   // (prototype, window) → its join or null: looked up only
        public int Keys;                                          // distinct keys
        public double[] Cost = Array.Empty<double>();             // the edit DP's scratch rows, (WindowMaxEvents + 1)² each
        public int[] Edits = Array.Empty<int>();
        public byte[] Move = Array.Empty<byte>();
    }

    public static CellLexicon Build(MotifInput input, Diminution.Line[] lines = null)
    {
        var all = input.Streams.All;
        lines ??= Diminution.ReduceAll(input);
        var work = new StreamWork[all.Length];
        // Streams are independent here: each slot is written by its own index and read after the loop (§9 G6).
        Parallel.For(0, all.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, s =>
        {
            if (all[s].Kind == MotifStreams.Both) return;
            var w = new StreamWork { S = all[s], Line = lines[s] };
            Windows(input, w);
            Segment(input, w);
            work[s] = w;
        });
        var lex = new CellLexicon { Lines = lines };
        lex.Assemble(input, work);
        return lex;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §5 C1–C2: windows and orbit keys.

    static void Windows(MotifInput input, StreamWork w)
    {
        var s = w.S; var grid = input.Grid; var settings = input.Settings;
        bool drums = s.Kind == MotifStreams.Drums, texture = s.Kind == MotifStreams.Texture;
        var pos = new List<long>();
        if (grid.Columns > 0)
        {
            if (drums) { for (int b = grid.FirstBar; b < grid.Bars; b++) pos.Add(grid.BarStart[b]); pos.Add(grid.BarEnd[grid.Bars - 1]); }
            else
            {
                for (int c = grid.FirstColumn; c < grid.Columns; c++)
                {
                    pos.Add(grid.ColumnStart[c]);
                    long half = (grid.ColumnEnd[c] - grid.ColumnStart[c]) / 2;
                    if (texture && half > 0 && grid.ColumnStart[c] + half < grid.ColumnEnd[c]) pos.Add(grid.ColumnStart[c] + half);
                }
                pos.Add(grid.ColumnEnd[grid.Columns - 1]);
            }
        }
        pos.Sort();
        for (int i = pos.Count - 1; i > 0; i--) if (pos[i] == pos[i - 1]) pos.RemoveAt(i);
        w.Pos = pos.ToArray();
        int P = w.Pos.Length - 1;
        w.EndingAt = new List<Window>[Math.Max(1, P + 1)];
        for (int j = 0; j < w.EndingAt.Length; j++) w.EndingAt[j] = new List<Window>();
        if (P <= 0) return;
        bool anacrusis = grid.FirstBar + 1 < grid.Bars && grid.BarEnd[grid.FirstBar] - grid.BarStart[grid.FirstBar] < grid.BarEnd[grid.FirstBar + 1] - grid.BarStart[grid.FirstBar + 1];
        var ends = new List<int>();
        for (int i = 0; i < P; i++)
        {
            ends.Clear(); ends.Add(i + 1);
            int b = Math.Max(0, grid.BarAt(w.Pos[i]));
            long unit = grid.Unit[b], bar = grid.BarEnd[b] - grid.BarStart[b];
            void Try(long length) { if (length <= 0) return; int j = Array.BinarySearch(w.Pos, w.Pos[i] + length); if (j > i) ends.Add(j); }
            if (drums) Try(bar);
            else
            {
                Try(unit);
                if (texture) Try(unit / 2);   // a half-tactus window lets a texture's tiling reach its half-tactus starts
                foreach (double x in settings.WindowBars) Try((long)Math.Round(x * bar));
                if (i == 0 && anacrusis) Try(grid.BarStart[grid.FirstBar + 1] - w.Pos[0]);
            }
            ends.Sort();
            for (int e = 0; e < ends.Count; e++)
            {
                if (e > 0 && ends[e] == ends[e - 1]) continue;
                var x = Make(input, w, i, ends[e]);
                if (x.Oversize && ends[e] != i + 1) continue;   // more than 24 events: only the one-step window stays, as a literal
                x.Index = w.Windows.Count; w.Windows.Add(x); w.EndingAt[x.End].Add(x);
            }
        }
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);   // looked up only
        foreach (var x in w.Windows) if (x.Space != Rest) { if (!ids.TryGetValue(x.Key, out int id)) ids[x.Key] = id = ids.Count; x.KeyId = id; }
        w.Keys = ids.Count;
        int size = (settings.WindowMaxEvents + 1) * (settings.WindowMaxEvents + 1);
        w.Cost = new double[size]; w.Edits = new int[size]; w.Move = new byte[size];
    }

    static Window Make(MotifInput input, StreamWork w, int i, int j)
    {
        var s = w.S; var ev = input.Events; var grid = input.Grid;
        var x = new Window { Stream = s.Id, Position = i, End = j, Start = w.Pos[i], Stop = w.Pos[j] };
        x.First = LowerBound(s.Onset, x.Start); x.Count = LowerBound(s.Onset, x.Stop) - x.First;
        long slot = Math.Max(1, grid.SlotAt(x.Start));
        bool drums = s.Kind == MotifStreams.Drums, texture = s.Kind == MotifStreams.Texture;
        if (!drums && x.First > 0)
        {
            int k = x.First - 1; long held = 0;
            if (texture) for (int q = 0; q < s.Count[k]; q++) { var e = ev[s.Notes[s.First[k] + q]]; held = Math.Max(held, e.Tick + e.Duration); }
            else { var e = ev[s.Lead[k]]; held = e.Tick + e.Duration; }
            if (held * 2 > 2 * x.Start + slot) x.Tie = k;   // sounding more than half a slot past the start: held over, not a late release
        }
        x.Space = x.Events == 0 ? Rest : drums ? Drum : texture ? Texture : OffScale(input, s, x) ? Chromatic : Diatonic;
        x.Oversize = x.Events > input.Settings.WindowMaxEvents;
        if (x.Space == Rest) { x.Key = "R" + x.Length; return x; }
        Key(input, w, x, slot);
        // The first note's move from the note before the window (0 for a tied-in note, which is that note): the literal
        // codes the first note by it.
        if (x.Tie < 0 && x.First > 0 && !drums) x.Delta = Value(input, s, x, x.First) - Value(input, s, x, x.First - 1);
        if (!drums) { var a0 = ev[s.Lead[x.Tie >= 0 ? x.Tie : x.First]]; x.AnchorMorph = a0.Morph; x.AnchorPitch = a0.Pitch; }
        x.Lit = Literal(input, w, x, slot);
        return x;
    }
    static int LowerBound(long[] a, long v) { int lo = 0, hi = a.Length; while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] < v) lo = mid + 1; else hi = mid; } return lo; }
    // A stream event's value in the window's space: its lead's morph (diatonic) or pitch.
    static int Value(MotifInput input, MotifStreams.Stream s, Window x, int k) => x.Space == Diatonic ? input.Events[s.Lead[k]].Morph : input.Events[s.Lead[k]].Pitch;
    // The window's events in order: the tied-in note first.
    static IEnumerable<int> EventsOf(Window x) { if (x.Tie >= 0) yield return x.Tie; for (int k = x.First; k < x.First + x.Count; k++) yield return k; }

    // A voice window is chromatic when ≥ 30% of its notes lie off the local scale (§5 C2). A note the sounding chord
    // explains (E♭ over C minor in C major, the parallel minor's third) or a minor key's raised sixth or seventh (its
    // melodic and harmonic forms) is not off the scale: only notes neither the key nor the chord accounts for are.
    static bool OffScale(MotifInput input, MotifStreams.Stream s, Window x)
    {
        int off = 0, total = 0; var frames = input.Frames;
        foreach (int k in EventsOf(x))
        {
            var e = input.Events[s.Lead[k]]; total++;
            if (e.Alter == 0) continue;
            int r = frames.Region(e.Tick);
            if (Diminution.ChordMask(frames, e.Tick) is int mask && mask != 0 && (mask >> Spelling.Mod(e.Pitch, 12) & 1) != 0) continue;
            if (r >= 0 && frames.Minor[r] && e.Alter == 1 && frames.Degree(e.Morph, r) is 5 or 6) continue;
            off++;
        }
        return total > 0 && off >= input.Settings.ChromaticShare * total - 1e-9;
    }

    // The orbit key: space, length, events, rhythm (slot, tie) and min_lex(steps, −steps) (textures: the lowest note's
    // steps with each sonority's shape, against the mirror's; drums: each onset's notes, no mirror).
    static void Key(MotifInput input, StreamWork w, Window x, long slot)
    {
        var s = w.S; var ev = input.Events;
        var sb = new StringBuilder();
        // The slot's size is part of the key: equal slot counts under another tactus are another rhythm.
        sb.Append((char)('A' + x.Space)).Append(x.Length).Append('/').Append(slot).Append(':').Append((char)(0x100 + x.Events));
        foreach (int k in EventsOf(x))
        {
            long at = k == x.Tie ? 0 : (long)Math.Round((s.Onset[k] - x.Start) / (double)slot, MidpointRounding.AwayFromZero);
            sb.Append((char)(0x100 + at)).Append(k == x.Tie ? 'T' : 'a');
        }
        int n = x.Events;
        if (x.Space == Drum)
        {
            foreach (int k in EventsOf(x)) { sb.Append((char)(0x100 + s.Count[k])); for (int q = 0; q < s.Count[k]; q++) sb.Append((char)(0x100 + ev[s.Notes[s.First[k] + q]].Pitch)); }
            x.Key = sb.ToString(); return;
        }
        var up = new List<int>(); var down = new List<int>();
        if (x.Space == Texture)
        {
            // The mirror maps each sonority's top note to its new bottom: its steps are the negated top steps, its shape
            // the intervals below the top.
            var low = new List<int>(); var high = new List<int>(); var shapes = new List<int[]>();
            foreach (int k in EventsOf(x))
            {
                var notes = Sonority(input, s, x, k);
                low.Add(notes[0]); high.Add(notes[^1]);
                var shape = new int[notes.Length]; for (int q = 0; q < notes.Length; q++) shape[q] = notes[q] - notes[0];
                shapes.Add(shape);
            }
            for (int q = 1; q < n; q++) { up.Add(low[q] - low[q - 1]); down.Add(-(high[q] - high[q - 1])); }
            foreach (var shape in shapes) { up.Add(1000 + shape.Length); foreach (int v in shape) up.Add(v); }
            foreach (var shape in shapes) { down.Add(1000 + shape.Length); for (int q = shape.Length - 1; q >= 0; q--) down.Add(shape[^1] - shape[q]); }
        }
        else
        {
            int previous = 0, q = 0;
            foreach (int k in EventsOf(x))
            {
                int v = Value(input, s, x, k);
                if (q++ > 0) { up.Add(v - previous); down.Add(previous - v); }
                previous = v;
            }
        }
        int cmp = Compare(down, up);
        x.Inverted = cmp < 0; x.Achiral = cmp == 0;
        foreach (int v in x.Inverted ? down : up) sb.Append((char)(0x8000 + v));
        x.Key = sb.ToString();
    }
    static int Compare(List<int> a, List<int> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Count.CompareTo(b.Count);
    }
    // A texture event's pitches, low to high; a tied-in event keeps only the notes still sounding at the window start.
    static int[] Sonority(MotifInput input, MotifStreams.Stream s, Window x, int k)
    {
        var list = new List<int>();
        long slot = Math.Max(1, input.Grid.SlotAt(x.Start));
        for (int q = 0; q < s.Count[k]; q++)
        {
            var e = input.Events[s.Notes[s.First[k] + q]];
            if (k == x.Tie && (e.Tick + e.Duration) * 2 <= 2 * x.Start + slot) continue;
            list.Add(e.Pitch);
        }
        if (list.Count == 0) list.Add(input.Events[s.Lead[k]].Pitch);
        list.Sort();
        return list.ToArray();
    }

    // A window's literal (§5 C3's Lit, with §9 G1's event literal for each note): 4 bits, then per note its onset among the
    // window's slots (log2 of their number), its step from the note before as an Elias-gamma code (the first note's from
    // the note before the window), 3 bits if it is altered, and γ(m − m_lead) for each further note struck with it; a drum
    // onset codes each note in 2 bits. The stream's empirical entropies that §5 C3 names are cheaper (2–5 bits a note on
    // the fixtures): with them a phrase stated twice outweighed its motifs, and the étude came out in 2-bar windows.
    static double Literal(MotifInput input, StreamWork w, Window x, long slot)
    {
        var s = w.S; var ev = input.Events;
        double bits = 4, position = Math.Log2(Math.Max(2, x.Length / (double)slot));
        int previous = 0, q = 0;
        foreach (int k in EventsOf(x))
        {
            bits += position;
            if (x.Space == Drum) { bits += 2 * s.Count[k]; continue; }
            int v = x.Space == Diatonic ? ev[s.Lead[k]].Morph : ev[s.Lead[k]].Pitch;
            bits += Gamma(q++ > 0 ? v - previous : x.Delta) + (x.Space is Diatonic or Chromatic && ev[s.Lead[k]].Alter != 0 ? 3 : 0);
            for (int c = 1; c < s.Count[k]; c++) bits += Gamma(ev[s.Notes[s.First[k] + c]].Morph - ev[s.Lead[k]].Morph);
            previous = v;
        }
        return bits;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §5 C3–C4: the lexicon Viterbi with EM rounds, and the fuzzy join.

    // The counts of one round, by key id: an orbit is a key some counted window has, its prototype the first of them.
    sealed class Lexicon
    {
        public int[] Count = Array.Empty<int>();
        public Window[] Prototype = Array.Empty<Window>();
        public double F;
        public int Orbit(Window x) => x.KeyId >= 0 && Count[x.KeyId] > 0 ? x.KeyId : -1;
        // Counted windows in start order.
        public static Lexicon Of(List<Window> counted, int keys)
        {
            var lex = new Lexicon { Count = new int[keys], Prototype = new Window[keys], F = counted.Count };
            foreach (var x in counted) if (lex.Count[x.KeyId]++ == 0) lex.Prototype[x.KeyId] = x;
            return lex;
        }
    }

    // §5 C3. Round 0 counts every candidate window, later rounds the tiling the round before chose; each round counts,
    // lets singletons join a prototype (§5 C4), then cuts the stream with the Viterbi. Hard EM settles in the first
    // tiling it favours: from round 0 a phrase stated three times makes its 2-bar windows look cheaper than the motifs
    // inside it, though the motifs cost less once counted (the étude's bars 1–2), while elsewhere 2-bar units are the
    // motifs (the étude's B1). So EM also starts from the windows up to a bar and then grows: a round counts its tiling
    // plus every longer window, and EM goes on from there. Of the tilings, the one that costs least under its own
    // counts stays (the earlier on a tie).
    static void Segment(MotifInput input, StreamWork w)
    {
        var first = Em(input, w, FirstCount(input, w, 0), "all");
        bool longer = false;
        foreach (var x in w.Windows) if (x.Space != Rest && x.Length > BarLength(input, x)) { longer = true; break; }
        if (!longer) { Evaluate(input, w, first); w.Tiling = first; return; }
        var fine = Em(input, w, FirstCount(input, w, -1), "fine");
        var seed = new List<Window>();
        foreach (var x in fine) if (x.Space != Rest && !x.Oversize) seed.Add(x);
        seed.AddRange(FirstCount(input, w, 1));
        seed.Sort((p, q) => p.Index.CompareTo(q.Index));
        var grown = Em(input, w, seed, "grown");
        var best = first; double cost = Evaluate(input, w, first);
        foreach (var t in new[] { grown, fine })
        {
            double c = Evaluate(input, w, t);
            if (Trace != null && w.S.Id == input.Streams.Principal) Trace($"start costs {(t == grown ? "grown" : "fine")} {c:0.0} against {cost:0.0}");
            if (c < cost - 1e-9) { best = t; cost = c; }
        }
        Evaluate(input, w, best);
        w.Tiling = best;
    }
    static long BarLength(MotifInput input, Window x) { int b = Math.Max(0, input.Grid.BarAt(x.Start)); return input.Grid.BarEnd[b] - input.Grid.BarStart[b]; }

    // Round 0's count (scale 0 every window, −1 those up to a bar, 1 the longer ones): each key's windows that do not
    // overlap one another, since a key can fill a tiling no more often than that (overlapping counts made the 2-note
    // windows of F3's phase-shifted pairs look twice as frequent as they can be).
    static List<Window> FirstCount(MotifInput input, StreamWork w, int scale)
    {
        var counted = new List<Window>();
        var reach = new Dictionary<string, long>(StringComparer.Ordinal);   // looked up only: where each key's last counted window ends
        foreach (var x in w.Windows)
        {
            if (x.Space == Rest || x.Oversize || scale != 0 && x.Length > BarLength(input, x) != scale > 0) continue;
            if (reach.TryGetValue(x.Key, out long end) && x.Start < end) continue;
            reach[x.Key] = x.Stop; counted.Add(x);
        }
        return counted;
    }
    static Window[] Em(MotifInput input, StreamWork w, List<Window> counted, string label)
    {
        Window[] tiling = Array.Empty<Window>();
        for (int round = 0; round < Math.Max(1, input.Settings.EmRounds); round++)
        {
            var lex = Count(input, w, counted);
            tiling = Viterbi(w, lex);
            if (Trace != null && w.S.Id == input.Streams.Principal) Trace($"{label} round {round} F {lex.F}: " + string.Join(" ", tiling.Select(x => $"[{x.Start / (double)input.Ppq:0.##},{x.Stop / (double)input.Ppq:0.##}){(x.Join != null ? "~" : "")}{Cost(x, lex):0.0}/f{(x.Space == Rest ? 0 : (x.Join != null ? x.Join.Target.Orbit : x.Orbit) is int o && o >= 0 ? lex.Count[o] : 0)}")));
            counted = new List<Window>();
            foreach (var x in tiling) if (x.Space != Rest && !x.Oversize) counted.Add(x);
        }
        return tiling;
    }
    // The lexicon of the counted windows: exact orbits, then the singletons' joins moving their counts to their targets.
    static Lexicon Count(MotifInput input, StreamWork w, List<Window> counted)
    {
        var lex = Lexicon.Of(counted, w.Keys);
        foreach (var x in w.Windows) { x.Orbit = x.Space == Rest ? -1 : lex.Orbit(x); x.Join = null; }
        JoinSingletons(input, w, lex);
        foreach (var x in counted) if (x.Join != null) { lex.Count[x.Orbit]--; lex.Count[x.Join.Target.Orbit]++; }
        return lex;
    }
    // A tiling's cost under its own counts (its joins recomputed against them): what EM's three tilings are compared by.
    static double Evaluate(MotifInput input, StreamWork w, Window[] tiling)
    {
        var counted = new List<Window>();
        foreach (var x in tiling) if (x.Space != Rest && !x.Oversize) counted.Add(x);
        var lex = Count(input, w, counted);
        double total = 0;
        foreach (var x in tiling) { x.Cost = Cost(x, lex); total += x.Cost; }
        return total;
    }

    // best[q] = min over windows w ending at q of best[q − L_w] + cost(w); ties: the longer window, then the smaller key.
    static Window[] Viterbi(StreamWork w, Lexicon lex)
    {
        int P = w.Pos.Length - 1; if (P <= 0) return Array.Empty<Window>();
        var best = new double[P + 1]; var back = new Window[P + 1];
        for (int j = 1; j <= P; j++) best[j] = double.PositiveInfinity;
        for (int j = 1; j <= P; j++)
            foreach (var x in w.EndingAt[j])
            {
                if (double.IsPositiveInfinity(best[x.Position])) continue;
                double v = best[x.Position] + Cost(x, lex);
                var b = back[j];
                if (b == null || v < best[j] - 1e-9 || v <= best[j] + 1e-9 && (x.Length > b.Length || x.Length == b.Length && string.CompareOrdinal(x.Key, b.Key) < 0)) { best[j] = v; back[j] = x; }
            }
        var path = new List<Window>();
        for (int j = P; j > 0;) { var x = back[j]; if (x == null) break; path.Add(x); j = x.Position; }
        path.Reverse();
        return path.ToArray();
    }

    // cost(w) = 1 for a rest; for a member of an orbit counted f ≥ 2 times −log2(f/F) + Lit(π)/f + L(g) + L(ρ), where L(g)
    // is 1 + the bits of the element placing it from the prototype as §5 C6 prices them (a repeat 0, t_k 1.5 + log2(1 + |k|),
    // the mirror 2 …: what §9 G1 charges a member) and L(ρ) a variant's edits; otherwise Lit(w) + 4. §5 C3 writes L(g) as
    // 1 + γ of the anchor's move from the previous cell's last note: that charges ~5 bits per cell even for a repeat, so a
    // phrase stated three times (the étude's a1, a1′ and the reprise) came out cheaper as 2-bar windows than as the
    // motifs §5 C3 measures inside it (the upper neighbour at 1.2 ×9, the lower at 1.1 and 62.1).
    static double Cost(Window x, Lexicon lex)
    {
        if (x.Space == Rest) return 1;
        if (x.Oversize) return x.Lit + 4;
        int o = x.Join != null ? x.Join.Target.Orbit : x.Orbit;
        double f = o >= 0 ? lex.Count[o] : 0;
        if (f >= 2) return -Math.Log2(f / lex.F) + lex.Prototype[o].Lit / f + 1 + ElementBits(x, lex.Prototype[o]) + (x.Join?.Rho ?? 0);
        return x.Lit + 4;
    }

    // The element placing a member from its orbit's prototype, priced as §5 C6 prices it (identity 0, an octave 1, t_k
    // 1.5 + log2(1 + |k|), T_k 2.5 + log2(1 + |k|), the mirror 2, a lengthened head 2): a variant's comes from its join.
    // Alterations are left to the relations; this prices the transposition the anchors imply.
    static double ElementBits(Window x, Window proto)
    {
        if (x.Space == Drum) return 0;
        if (x.Join != null) return new Transform(x.Join.Steps, x.Join.Semitones, x.Space == Diatonic ? Transform.Tonal : Transform.Real, x.Join.Inverted, head: x.Join.Head).Cost();
        int steps = x.AnchorMorph - proto.AnchorMorph, semitones = x.AnchorPitch - proto.AnchorPitch;
        double mirror = x.Inverted != proto.Inverted && !proto.Achiral ? 2 : 0;
        if (x.Space == Diatonic) return new Transform(steps, semitones, Transform.Tonal).Cost() + mirror;
        if (semitones == 0) return mirror;
        return (semitones % 12 == 0 ? Math.Abs(semitones / 12) : 2.5 + Math.Log2(1 + Math.Abs(semitones))) + mirror;
    }

    // §5 C4. Windows are visited in start order; a window of ≥ 4 events whose exact orbit is counted at most once (or not
    // at all this round) tries the nearest earlier prototype of its length and space, and a counted window that joins none
    // and is its orbit's prototype becomes a target itself, a singleton included (F14 and F27 state their cell once before
    // the variant). Prototypes are bucketed by event count, and the 32 nearest in each bucket within the edit cap are
    // tried: those passing the rhythm bound are aligned, each pair once for every round and start.
    static void JoinSingletons(MotifInput input, StreamWork w, Lexicon lex)
    {
        var settings = input.Settings;
        var buckets = new Dictionary<(int space, long length, int events), List<Window>>();   // looked up only
        foreach (var x in w.Windows)
        {
            if (x.Space is not (Diatonic or Chromatic) || x.Oversize) continue;
            int count = x.Orbit >= 0 ? lex.Count[x.Orbit] : 0;   // a window this round did not count is a singleton too
            if (x.Events >= settings.FuzzyMinEvents && count <= 1)
            {
                int edits = Math.Max(1, x.Events / settings.FuzzyEditDivisor);
                Join best = null;
                for (int e = x.Events - edits; e <= x.Events + edits; e++)
                {
                    if (!buckets.TryGetValue((x.Space, x.Length, e), out var list)) continue;
                    int tried = 0;
                    for (int t = list.Count - 1; t >= 0 && tried < 32; t--)
                    {
                        var target = list[t];
                        if (target.Start >= x.Start) continue;
                        tried++;
                        var j = Aligned(input, w, target, x, edits);
                        if (j != null && (best == null || j.Distance < best.Distance - 1e-9 || Math.Abs(j.Distance - best.Distance) <= 1e-9 && j.Target.Start < best.Target.Start)) best = j;
                    }
                }
                x.Join = best;
            }
            if (x.Join == null && x.Orbit >= 0 && lex.Prototype[x.Orbit] == x && x.Events >= 2)
            {
                if (!buckets.TryGetValue((x.Space, x.Length, x.Events), out var list)) buckets[(x.Space, x.Length, x.Events)] = list = new List<Window>();
                list.Add(x);
            }
        }
    }

    // Onsets with no partner within two slots must be inserted or deleted: if they alone exceed the edit cap, no
    // alignment can join.
    static bool RhythmBound(MotifInput input, StreamWork w, Window a, Window b, int edits)
    {
        var ea = Elements(input, w.S, a); var eb = Elements(input, w.S, b);
        int lone = 0;
        foreach (var x in ea) { bool near = false; foreach (var y in eb) if (Math.Abs(x.Slot - y.Slot) <= 2) { near = true; break; } if (!near && ++lone > edits) return false; }
        foreach (var y in eb) { bool near = false; foreach (var x in ea) if (Math.Abs(x.Slot - y.Slot) <= 2) { near = true; break; } if (!near && ++lone > edits) return false; }
        return true;
    }

    // A window's notes as the fuzzy join compares them (computed once: windows never change).
    internal static Element[] Elements(MotifInput input, MotifStreams.Stream s, Window x)
    {
        if (x.Cached != null) return x.Cached;
        var ev = input.Events; long slot = Math.Max(1, input.Grid.SlotAt(x.Start));
        var r = new Element[x.Events]; int q = 0;
        foreach (int k in EventsOf(x))
        {
            var e = ev[s.Lead[k]];
            long span = k + 1 < s.Events ? s.Onset[k + 1] - s.Onset[k] : e.Duration;
            r[q] = new Element
            {
                Onset = k == x.Tie ? 0 : s.Onset[k] - x.Start, Slot = k == x.Tie ? 0 : (int)Math.Round((s.Onset[k] - x.Start) / (double)slot, MidpointRounding.AwayFromZero),
                Value = x.Space == Diatonic ? e.Morph : e.Pitch, Pitch = e.Pitch, Morph = e.Morph, Alter = e.Alter, Event = k, Note = s.Lead[k], Tied = k == x.Tie,
                Ornament = k != x.Tie && span <= slot
            };
            r[q].Repeat = q > 0 && r[q].Value == r[q - 1].Value;
            q++;
        }
        return x.Cached = r;
    }

    // A pair's outcome depends on the two windows alone, so every round and every EM start shares it.
    static Join Aligned(MotifInput input, StreamWork w, Window target, Window x, int edits)
    {
        long key = (long)target.Index << 32 | (uint)x.Index;
        if (!w.Aligned.TryGetValue(key, out var j)) w.Aligned[key] = j = RhythmBound(input, w, target, x, edits) ? Align(input, w, target, x) : null;
        return j;
    }

    // The distance of §5 C4 and its guards: the least edit distance over both chiralities and the two most common value
    // offsets of onset-matched pairs, with onset shifts of ≤ 2 slots at 0.5, substitutions at min(1, 0.5·|Δ|), and
    // insertions or deletions at 1 (0.3 for a repetition or an ornament). The window joins only if the distance is at
    // most 0.2 per event, it needs at most max(1, ⌊events/3⌋) edits, every pair of consecutive matched notes moves the
    // same way (octave-folded), and L(g) + L(ρ) + 2 < Lit(w). A tie at the start against an attack is a lengthened
    // (or shortened) head: an element of the group (2 bits), not an edit.
    static Join Align(MotifInput input, StreamWork w, Window target, Window x)
    {
        var settings = input.Settings;
        var a = Elements(input, w.S, target); var b = Elements(input, w.S, x);
        Join best = null;
        int events = x.Events;
        double limit = settings.FuzzyDistance * events + 1e-9;
        foreach (int chi in new[] { 1, -1 })
        {
            foreach (int shift in Offsets(a, b, chi))
            {
                var j = Edit(w, a, b, chi, shift, settings.OrnamentIndel, best == null ? limit : Math.Min(limit, best.Distance + 1e-9));
                if (j != null && (best == null || j.Distance < best.Distance - 1e-9 || Math.Abs(j.Distance - best.Distance) <= 1e-9 && j.Edits < best.Edits)) best = j;
            }
        }
        if (best == null) return null;
        if (best.Distance > settings.FuzzyDistance * events + 1e-9 || best.Edits > Math.Max(1, events / settings.FuzzyEditDivisor)) return null;
        // Contour invariance over consecutive matched pairs.
        int modulus = x.Space == Diatonic ? 7 : 12, pi = -1, pj = -1;
        for (int jx = 0; jx < b.Length; jx++)
        {
            int ia = best.Pair[jx]; if (ia < 0) continue;
            if (pi >= 0 && Math.Sign(Fold(a[ia].Value - a[pi].Value, modulus)) != Math.Sign(Fold(best.Chi * (b[jx].Value - b[pj].Value), modulus))) return null;
            pi = ia; pj = jx;
        }
        best.Target = target;
        best.Inverted = best.Chi < 0;
        // The element: the first prototype note onto the window's first note (mirrored about it when inverted).
        int a0 = a[0].Value, b0 = best.Inverted ? -a0 - best.Shift : a0 + best.Shift;
        best.Steps = x.Space == Diatonic ? b0 - a0 : 0;
        // The first notes matched exactly: the element moves the one onto the other; else by the transposition alone.
        bool exactFirst = best.Pair.Length > 0 && best.Pair[0] == 0 && best.Chi * b[0].Value - a[0].Value - best.Shift == 0;
        best.Semitones = x.Space == Diatonic ? (exactFirst ? b[0].Pitch - a[0].Pitch : SemitonesOf(best.Steps)) : b0 - a0;
        // The head: the window's note matched to the prototype's first, tied where that one is struck or the other way round. It
        // is the window's first note unless a held-over note the prototype lacks comes before it (an inserted tie).
        int head = Array.IndexOf(best.Pair, 0);
        best.Head = head >= 0 && a[0].Tied != b[head].Tied;
        var g = new Transform(best.Steps, best.Semitones, x.Space == Diatonic ? Transform.Tonal : Transform.Real, best.Inverted, head: best.Head);
        double rho = Residual(input, x, a, b, best);
        best.Bits = g.Cost() + rho;
        best.Rho = rho;
        if (!(best.Bits + settings.FuzzyMdlMargin < x.Lit)) return null;
        return best;
    }
    static int SemitonesOf(int steps) => Spelling.NaturalPitch(28 + steps) - 60;
    static int Fold(int d, int modulus) { int h = modulus / 2; return Spelling.Mod(d + h, modulus) - h; }

    // The two most common offsets b − χ·a over pairs at the same slot (the likely transpositions); ties: the smaller
    // offset in size, then the lower. Without such pairs, the first notes' offset.
    static int[] Offsets(Element[] a, Element[] b, int chi)
    {
        int first = 0, firstCount = 0, second = 0, secondCount = 0;
        bool Better(int d, int c, int o, int oc) => c != oc ? c > oc : Math.Abs(d) != Math.Abs(o) ? Math.Abs(d) < Math.Abs(o) : d < o;
        Span<int> seen = stackalloc int[a.Length * b.Length]; int n = 0;
        foreach (var x in a)
            foreach (var y in b)
                if (x.Slot == y.Slot) seen[n++] = chi * y.Value - x.Value;
        if (n == 0) return new[] { chi * b[0].Value - a[0].Value };
        seen[..n].Sort();
        for (int i = 0; i < n;)
        {
            int j = i; while (j < n && seen[j] == seen[i]) j++;
            int d = seen[i], c = j - i;
            if (firstCount == 0 || Better(d, c, first, firstCount)) { second = first; secondCount = firstCount; first = d; firstCount = c; }
            else if (secondCount == 0 || Better(d, c, second, secondCount)) { second = d; secondCount = c; }
            i = j;
        }
        return secondCount > 0 ? new[] { first, second } : new[] { first };
    }

    // Edit distance between the prototype's (slot, value + shift) and the window's (slot, χ·value), minimising cost,
    // then the number of edits. Costs never fall along a path, so once a whole row exceeds the limit no alignment can
    // join, and the search stops (null). The rows live in the stream's scratch buffers.
    static Join Edit(StreamWork w, Element[] a, Element[] b, int chi, int shift, double ornament, double limit)
    {
        int n = a.Length, m = b.Length, stride = m + 1;
        var cost = w.Cost; var edits = w.Edits; var move = w.Move;
        double Indel(in Element e) => e.Repeat || e.Ornament ? ornament : 1;
        cost[0] = 0; edits[0] = 0; move[0] = 0;
        for (int j = 1; j <= m; j++) { cost[j] = cost[j - 1] + Indel(b[j - 1]); edits[j] = j; move[j] = 2; }
        for (int i = 1; i <= n; i++)
        {
            int row = i * stride, up = row - stride;
            cost[row] = cost[up] + Indel(a[i - 1]); edits[row] = i; move[row] = 1;
            double least = cost[row];
            for (int j = 1; j <= m; j++)
            {
                double c = cost[up + j] + Indel(a[i - 1]); int e = edits[up + j] + 1; byte mv = 1;
                double c2 = cost[row + j - 1] + Indel(b[j - 1]); int e2 = edits[row + j - 1] + 1;
                if (c2 < c - 1e-9 || Math.Abs(c2 - c) <= 1e-9 && e2 < e) { c = c2; e = e2; mv = 2; }
                int ds = Math.Abs(a[i - 1].Slot - b[j - 1].Slot);
                if (ds <= 2)
                {
                    int dv = Math.Abs(chi * b[j - 1].Value - a[i - 1].Value - shift);
                    double sub = (ds > 0 ? .5 : 0) + (dv > 0 ? Math.Min(1, .5 * dv) : 0);
                    double c3 = cost[up + j - 1] + sub; int e3 = edits[up + j - 1] + (ds > 0 ? 1 : 0) + (dv > 0 ? 1 : 0);
                    if (c3 < c - 1e-9 || Math.Abs(c3 - c) <= 1e-9 && e3 <= e) { c = c3; e = e3; mv = 3; }
                }
                cost[row + j] = c; edits[row + j] = e; move[row + j] = mv;
                if (c < least) least = c;
            }
            if (least > limit) return null;
        }
        int last = n * stride + m;
        if (cost[last] > limit) return null;
        var join = new Join { Chi = chi, Shift = shift, Distance = cost[last], Edits = edits[last], Pair = new int[m], Deleted = new bool[n] };
        Array.Fill(join.Pair, -1);
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            byte mv = move[i * stride + j];
            if (mv == 3) { join.Pair[j - 1] = i - 1; i--; j--; }
            else if (mv == 1) { join.Deleted[i - 1] = true; i--; }
            else j--;
        }
        return join;
    }

    // L(ρ) of §9 G1: an inserted note 2 + log2(positions) + its literal; a deleted one 2 + log2(positions); a substitution
    // 1 + log2(1 + |Δsteps|); a shift 1.
    static double Residual(MotifInput input, Window x, Element[] a, Element[] b, Join j)
    {
        double bits = 0, position = Math.Log2(Math.Max(2, x.Length / (double)Math.Max(1, input.Grid.SlotAt(x.Start))));
        for (int q = 0; q < b.Length; q++)
        {
            int i = j.Pair[q];
            if (i < 0) { bits += 2 + Math.Log2(b.Length + 1) + position + Gamma(q > 0 ? b[q].Value - b[q - 1].Value : 0) + (b[q].Alter != 0 ? 3 : 0); continue; }
            int dv = Math.Abs(j.Chi * b[q].Value - a[i].Value - j.Shift);
            if (dv > 0) bits += 1 + Math.Log2(1 + dv);
            if (a[i].Slot != b[q].Slot) bits += 1;
        }
        foreach (bool d in j.Deleted) if (d) bits += 2 + Math.Log2(a.Length + 1);
        return bits;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §5 C5–C7: paradigms, relations and names, atoms.

    void Assemble(MotifInput input, StreamWork[] work)
    {
        var cells = new List<Cell>(); var paradigms = new List<Paradigm>(); var errors = new List<string>();
        StreamFirst = new int[work.Length]; StreamCount = new int[work.Length];
        for (int s = 0; s < work.Length; s++)
        {
            StreamFirst[s] = cells.Count;
            var w = work[s];
            if (w == null) continue;
            var mine = new List<Cell>();
            foreach (var x in w.Tiling)
            {
                if (x.Space == Rest) continue;
                var c = new Cell { Id = cells.Count + mine.Count, Stream = s, W = x, Notes = SourceNotes(input, w.S, x), Bits = x.Cost };
                mine.Add(c);
            }
            // Orbits of the final tiling: exact keys, and the keys variants joined.
            var index = new Dictionary<string, int>(StringComparer.Ordinal); var keys = new List<string>(); var exact = new List<List<Cell>>(); var fuzzy = new List<List<Cell>>();
            int Orbit(string key) { if (!index.TryGetValue(key, out int o)) { o = keys.Count; index[key] = o; keys.Add(key); exact.Add(new List<Cell>()); fuzzy.Add(new List<Cell>()); } return o; }
            foreach (var c in mine) if (!c.W.Oversize) { if (c.W.Join != null) fuzzy[Orbit(c.W.Join.Target.Key)].Add(c); else exact[Orbit(c.W.Key)].Add(c); }
            for (int o = 0; o < keys.Count; o++)
            {
                if (exact[o].Count == 0 || exact[o].Count + fuzzy[o].Count < 2) continue;
                var proto = exact[o][0];
                // A variant is re-aligned against the final prototype; one that no longer passes the guards stays literal.
                var variants = new List<(Cell cell, Join join)>();
                foreach (var c in fuzzy[o])
                {
                    var j = c.W.Join.Target == proto.W ? c.W.Join : Aligned(input, w, proto.W, c.W, Math.Max(1, c.W.Events / input.Settings.FuzzyEditDivisor));
                    if (j != null && proto.W.Start < c.W.Start) variants.Add((c, j));
                }
                if (exact[o].Count + variants.Count < 2) continue;
                var p = new Paradigm { Id = paradigms.Count, Stream = s, Space = proto.W.Space, Key = keys[o], Prototype = proto.Id, Achiral = proto.W.Achiral, Clock = proto.W.Space == Diatonic ? 7 : 12 };
                var members = new List<Cell>(exact[o]);
                foreach (var (c, _) in variants) members.Add(c);
                members.Sort((x, y) => x.Start.CompareTo(y.Start));
                p.Members = members.Select(c => c.Id).ToArray();
                foreach (var c in members) c.Paradigm = p.Id;
                Relations(input, w, p, proto, members, variants, errors);
                paradigms.Add(p);
            }
            cells.AddRange(mine);
            StreamCount[s] = mine.Count;
        }
        Cells = cells.ToArray(); Paradigms = paradigms.ToArray(); DecodeErrors = errors.ToArray();
        Families(input, work);
    }

    static int[] SourceNotes(MotifInput input, MotifStreams.Stream s, Window x)
    {
        var notes = new List<int>();
        for (int k = x.First; k < x.First + x.Count; k++) for (int q = 0; q < s.Count[k]; q++) notes.Add(s.Notes[s.First[k] + q]);
        var ev = input.Events;
        notes.Sort((p, q) => ev[p].Tick != ev[q].Tick ? ev[p].Tick.CompareTo(ev[q].Tick) : ev[p].Pitch != ev[q].Pitch ? ev[p].Pitch.CompareTo(ev[q].Pitch) : p.CompareTo(q));
        return notes.ToArray();
    }

    // A window's leads as TransformGroup reads them; the tied-in note sounds from the start.
    internal static CellNote[] Notes(MotifInput input, MotifStreams.Stream s, Window x)
    {
        var ev = input.Events; var frames = input.Frames;
        var r = new CellNote[x.Events]; int q = 0;
        foreach (int k in EventsOf(x))
        {
            var e = ev[s.Lead[k]]; long onset = k == x.Tie ? 0 : e.Tick - x.Start;
            int root = frames.RootPc(frames.Chord(x.Start + onset));
            r[q++] = new CellNote { Onset = onset, Length = k == x.Tie ? e.Tick + e.Duration - x.Start : e.Duration, Morph = e.Morph, Pitch = e.Pitch, Alter = e.Alter, Interval = root < 0 ? -1 : Spelling.Mod(e.Pitch - root, 12) };
        }
        return r;
    }

    // §5 C6. Each member's element from the prototype (its transform fields) and from its nearest earlier exact member
    // (its text); altered notes and a variant's edits become sparks; then every member is decoded back.
    static void Relations(MotifInput input, StreamWork w, Paradigm p, Cell proto, List<Cell> members, List<(Cell cell, Join join)> variants, List<string> errors)
    {
        var s = w.S; var frames = input.Frames;
        var protoNotes = Notes(input, s, proto.W);
        Cell neighbour = null;
        foreach (var c in members)
        {
            var notes = Notes(input, s, c.W);
            int vi = variants.FindIndex(v => v.cell == c);
            if (vi < 0)
            {
                if (c == proto) { c.G = Transform.Identity; c.Text = ""; }
                else
                {
                    var placed = Place(input, s, proto, c, protoNotes, notes);
                    c.G = placed.G; c.Altered = placed.Altered; c.Mode = placed.Mode;
                    var near = Place(input, s, neighbour, c, Notes(input, s, neighbour.W), notes);
                    c.Text = near.Text;
                    var sparks = new List<Spark>();
                    foreach (var x in placed.Altered)
                        sparks.Add(new Spark { Kind = PreparedPatternSong.MotifSparks.Altered, Note = x.Note, Event = c.W.Tie >= 0 && x.Note == 0 ? c.W.Tie : EventAt(c.W, x.Note), Pitch = x.Pitch, Morph = notes[x.Note].Morph, Tick = c.Start + notes[x.Note].Onset, Tag = x.Text });
                    c.Sparks = sparks.ToArray();
                }
                neighbour = c;
            }
            else Variant(input, w, p, proto, c, variants[vi].join, protoNotes, notes, neighbour);
            c.Degree = p.Clock == 7 ? frames.Degree(notes[0].Morph, frames.Region(c.Start)) : Spelling.Mod(notes[0].Pitch - frames.TonicPc(frames.Region(c.Start)), 12);
            c.Decoded = Decode(input, s, proto, c, protoNotes, notes, out string why);
            if (!c.Decoded) errors.Add($"stream {s.Id} {SpaceNames[c.W.Space]} {(c.Exact ? "member" : "variant")} at {c.Start / (double)input.Ppq:0.###} of the prototype at {proto.Start / (double)input.Ppq:0.###}, g ({c.G.Steps}, {c.G.Semitones}, frame {c.G.Frame}{(c.G.Inverted ? ", mirrored" : "")}{(c.G.Parallel != 0 ? ", parallel" : "")}), {c.Sparks.Length} sparks: {why}");
        }
        p.Mirrored = members.Count(c => c.G.Inverted);
        p.Variants = variants.Count;
    }
    static int EventAt(Window x, int element) => x.Tie >= 0 ? (element == 0 ? x.Tie : x.First + element - 1) : x.First + element;

    // The cheapest element placing an exact member of the orbit from another (§5 C6): tonal, real, the chord frame or
    // the parallel mode for lines, real transposition for chromatic windows and textures, identity for drums.
    static Placement Place(MotifInput input, MotifStreams.Stream s, Cell from, Cell to, CellNote[] a, CellNote[] b)
    {
        if (from.W.Space == Drum) return new Placement { G = Transform.Identity, Text = "repeat" };
        if (from.W.Space == Texture)
        {
            var sa = Sonority(input, s, from.W, from.W.Tie >= 0 ? from.W.Tie : from.W.First);
            var sb = Sonority(input, s, to.W, to.W.Tie >= 0 ? to.W.Tie : to.W.First);
            bool inverted = from.W.Inverted != to.W.Inverted && !from.W.Achiral;
            int semitones = sb[0] - sa[0], steps = b[0].Morph - a[0].Morph;
            bool plain = semitones == 0 || semitones % 12 == 0 && steps == semitones / 12 * 7;
            var g = new Transform(plain ? steps : 0, semitones, plain ? Transform.Tonal : Transform.Real, inverted);
            return new Placement { G = g, Bits = g.Cost(), Text = TransformGroup.Name(g) };
        }
        var placed = TransformGroup.Relate(a, b, from.Start, to.Start, input.Frames, from.W.Space == Chromatic);
        if (placed != null) return placed;
        // An achiral orbit relates both ways; Relate only fails if the steps differ, which an exact orbit rules out.
        var fallback = new Transform(b[0].Morph - a[0].Morph, b[0].Pitch - a[0].Pitch, Transform.Real);
        return new Placement { G = fallback, Bits = fallback.Cost(), Text = TransformGroup.Name(fallback) };
    }

    // A variant: its element comes from the join; each matched note that moved is a shift, each that changed its step a
    // substitution, each with the same step but another pitch an altered note (named by the regional key); inserted and
    // deleted notes and a lengthened head are sparks too.
    static void Variant(MotifInput input, StreamWork w, Paradigm p, Cell proto, Cell c, Join j, CellNote[] protoNotes, CellNote[] notes, Cell neighbour)
    {
        var s = w.S; var frames = input.Frames; var line = w.Line;
        c.Exact = false;
        bool diatonic = c.W.Space == Diatonic;
        c.G = new Transform(j.Steps, j.Semitones, diatonic ? Transform.Tonal : Transform.Real, j.Inverted, head: j.Head);
        var image = TransformGroup.Apply(new Transform(j.Steps, j.Semitones, diatonic ? Transform.Tonal : Transform.Real, j.Inverted), protoNotes, c.Start, frames);
        var sparks = new List<Spark>(); var altered = new List<Alteration>();
        var b = Elements(input, s, c.W);
        for (int q = 0; q < notes.Length; q++)
        {
            int i = j.Pair[q], k = b[q].Event;
            if (i < 0)
            {
                string role = line != null && k < line.Events ? line.Role(k) : "";
                if (role.Length == 0 || role == "Rep") role = LocalRole(notes, q);
                sparks.Add(new Spark { Kind = PreparedPatternSong.MotifSparks.Inserted, Note = -1, Event = k, Pitch = notes[q].Pitch, Morph = notes[q].Morph, Tick = c.Start + notes[q].Onset, Tag = role });
                continue;
            }
            long slot = Math.Max(1, input.Grid.SlotAt(c.Start));
            if (Slots(notes[q].Onset, slot) != Slots(image[i].Onset, slot))
                sparks.Add(new Spark { Kind = PreparedPatternSong.MotifSparks.Shifted, Note = i, Event = k, Pitch = notes[q].Pitch, Morph = notes[q].Morph, Tick = c.Start + notes[q].Onset, Shift = notes[q].Onset - image[i].Onset, Tag = "" });
            if (notes[q].Pitch != image[i].Pitch)
            {
                if (diatonic && notes[q].Morph == image[i].Morph)
                {
                    var x = frames.Explain(q, notes[q].Pitch, Fifths(notes[q]), notes[q].Morph, notes[q].Pitch - image[i].Pitch, c.Start + notes[q].Onset, c.Start, c.End);
                    altered.Add(x);
                    sparks.Add(new Spark { Kind = PreparedPatternSong.MotifSparks.Altered, Note = i, Event = k, Pitch = notes[q].Pitch, Morph = notes[q].Morph, Tick = c.Start + notes[q].Onset, Tag = x.Text });
                }
                else sparks.Add(new Spark { Kind = PreparedPatternSong.MotifSparks.Substituted, Note = i, Event = k, Pitch = notes[q].Pitch, Morph = notes[q].Morph, Tick = c.Start + notes[q].Onset, Tag = Signed(notes[q].Pitch - image[i].Pitch) });
            }
        }
        for (int i = 0; i < j.Deleted.Length; i++)
            if (j.Deleted[i]) sparks.Add(new Spark { Kind = PreparedPatternSong.MotifSparks.Deleted, Note = i, Event = -1, Pitch = image[i].Pitch, Morph = image[i].Morph, Tick = c.Start + image[i].Onset, Tag = "" });
        if (j.Head)
        {
            int h = Math.Max(0, Array.IndexOf(j.Pair, 0));
            var e = input.Events[s.Lead[b[h].Event]];
            long sixteenth = Math.Max(1, input.Ppq / 4), held = e.Duration;
            string tag = held % sixteenth == 0 ? $"held {held / sixteenth}/16" : $"held {Math.Round(held * 8.0 / input.Ppq)}/32";
            sparks.Insert(0, new Spark { Kind = PreparedPatternSong.MotifSparks.Lengthened, Note = 0, Event = b[h].Event, Pitch = notes[h].Pitch, Morph = notes[h].Morph, Tick = c.Start + notes[h].Onset, Tag = tag });
        }
        c.Sparks = sparks.ToArray(); c.Altered = altered.ToArray();
        // The text names the variant from its nearest earlier exact member: the two elements from the prototype, the one
        // undone and the other applied (their steps and semitones subtract; the mirror flips when exactly one mirrors).
        var from = neighbour ?? proto;
        var rel = new Transform(c.G.Steps - from.G.Steps, c.G.Semitones - from.G.Semitones, diatonic && from.G.Frame != Transform.Real ? Transform.Tonal : Transform.Real, c.G.Inverted != from.G.Inverted, head: j.Head);
        c.Text = TransformGroup.Name(rel, altered, "", j.Edits);
    }
    // The bundle is byte-identical under any culture (§9 G6): sv-SE writes a negative number with U+2212.
    static string Signed(int v) => (v > 0 ? "+" : "") + v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    static int Fifths(CellNote x)
    {
        int letter = Spelling.Mod(x.Morph, 7), accidental = x.Pitch - Spelling.NaturalPitch(x.Morph);
        return new[] { 0, 2, 4, -1, 1, 3, 5 }[letter] + 7 * accidental;
    }
    // An inserted note's figure from its neighbours in the member: a step on in the same direction is passing, a step
    // away and back a neighbour, the same pitch a repetition.
    static string LocalRole(CellNote[] notes, int q)
    {
        if (q == 0 || q + 1 >= notes.Length) return "";
        int d1 = notes[q].Morph - notes[q - 1].Morph, d2 = notes[q + 1].Morph - notes[q].Morph;
        if (d1 == 0) return "Rep";
        if (Math.Abs(d1) != 1) return "";
        return (d2 == d1 ? "P" : d2 == -d1 ? "N" : "") is { Length: > 0 } k ? k + (d1 < 0 ? "-" : "+") : "";
    }

    // The member decoded from the prototype: its image under g, then its sparks, must give the member's notes (pitches,
    // onsets in slots from the cell start, and which one is tied in). Textures compare whole sonorities; drums their
    // notes. Onsets within a slot are articulation: the orbit key rounds them, and the exact ticks are the articulation
    // code's (§9 G4), like durations and velocities.
    static long Slots(long ticks, long slot) => (long)Math.Round(ticks / (double)slot, MidpointRounding.AwayFromZero);
    static bool Decode(MotifInput input, MotifStreams.Stream s, Cell proto, Cell c, CellNote[] protoNotes, CellNote[] notes, out string why)
    {
        why = "";
        long slot = Math.Max(1, input.Grid.SlotAt(c.Start));
        if (c.W.Space == Drum)
        {
            var a = DrumHits(input, s, proto.W, slot); var b = DrumHits(input, s, c.W, slot);
            if (!a.SequenceEqual(b)) { why = "drum hits differ from the prototype's"; return false; }
            return true;
        }
        if (c.W.Space == Texture)
        {
            int n = proto.W.Events;
            if (c.W.Events != n) { why = "texture events differ"; return false; }
            var pe = EventsOf(proto.W).ToArray(); var ce = EventsOf(c.W).ToArray();
            var first = Sonority(input, s, proto.W, pe[0]);
            for (int q = 0; q < n; q++)
            {
                var sa = Sonority(input, s, proto.W, pe[q]); var sb = Sonority(input, s, c.W, ce[q]);
                var image = new int[sa.Length];
                for (int i = 0; i < sa.Length; i++) image[i] = c.G.Inverted ? first[0] + c.G.Semitones + first[^1] - sa[i] : sa[i] + c.G.Semitones;
                Array.Sort(image);
                if (!image.SequenceEqual(sb)) { why = $"sonority {q} decodes to {string.Join(" ", image)}, not {string.Join(" ", sb)}"; return false; }
                if (Slots(notes[q].Onset, slot) != Slots(protoNotes[q].Onset, slot)) { why = $"onset {q} moved"; return false; }
            }
            return true;
        }
        var g = new Transform(c.G.Steps, c.G.Semitones, c.G.Frame, c.G.Inverted, c.G.Retrograde, c.G.Scale, c.G.Parallel);
        var img = TransformGroup.Apply(g, protoNotes, c.Start, input.Frames);
        var decoded = new List<(long onset, int pitch, bool tied)>();
        var gone = new bool[img.Length]; var pitch = new int[img.Length]; var onset = new long[img.Length];
        for (int i = 0; i < img.Length; i++) { pitch[i] = img[i].Pitch; onset[i] = img[i].Onset; }
        bool lengthened = false;
        foreach (var x in c.Sparks)
        {
            switch (x.Kind)
            {
                case PreparedPatternSong.MotifSparks.Deleted: gone[x.Note] = true; break;
                case PreparedPatternSong.MotifSparks.Altered or PreparedPatternSong.MotifSparks.Substituted: pitch[x.Note] = x.Pitch; break;
                case PreparedPatternSong.MotifSparks.Shifted: onset[x.Note] += x.Shift; break;
                case PreparedPatternSong.MotifSparks.Inserted: decoded.Add((Slots(x.Tick - c.Start, slot), x.Pitch, x.Event == c.W.Tie && c.W.Tie >= 0)); break;   // a held-over note the prototype lacks
                case PreparedPatternSong.MotifSparks.Lengthened: lengthened = true; break;
            }
        }
        bool tiedHead = proto.W.Tie >= 0 ^ lengthened;
        for (int i = 0; i < img.Length; i++) if (!gone[i]) decoded.Add((Slots(onset[i], slot), pitch[i], i == 0 && tiedHead));
        // The tie is part of the key: a held-over note and its struck repetition share onset and pitch (List.Sort is unstable).
        decoded.Sort((x, y) => x.onset != y.onset ? x.onset.CompareTo(y.onset) : x.pitch != y.pitch ? x.pitch.CompareTo(y.pitch) : x.tied.CompareTo(y.tied));
        var actual = new List<(long onset, int pitch, bool tied)>();
        for (int q = 0; q < notes.Length; q++) actual.Add((Slots(notes[q].Onset, slot), notes[q].Pitch, q == 0 && c.W.Tie >= 0));
        actual.Sort((x, y) => x.onset != y.onset ? x.onset.CompareTo(y.onset) : x.pitch != y.pitch ? x.pitch.CompareTo(y.pitch) : x.tied.CompareTo(y.tied));
        if (decoded.Count != actual.Count) { why = $"{decoded.Count} notes decoded, {actual.Count} played"; return false; }
        for (int q = 0; q < actual.Count; q++)
            if (decoded[q] != actual[q]) { why = $"note {q} decodes to {decoded[q]}, played {actual[q]}"; return false; }
        return true;
    }
    static List<(long onset, int pitch)> DrumHits(MotifInput input, MotifStreams.Stream s, Window x, long slot)
    {
        var r = new List<(long, int)>();
        for (int k = x.First; k < x.First + x.Count; k++) for (int q = 0; q < s.Count[k]; q++) r.Add((Slots(s.Onset[k] - x.Start, slot), input.Events[s.Notes[s.First[k] + q]].Pitch));
        r.Sort();
        return r;
    }

    // §5 C7. A paradigm's signature is its prototype's diminution tokens, signs dropped (no repetitions); without tokens,
    // its contour names it (Hold, Osc, Arp, or a plain line P); a texture is a Shape; drums form no atom. Paradigms with
    // one signature form an atom whose Kind is the first token (a run of neighbours only is an oscillation). A member's
    // sign is the prototype's, flipped when the member is mirrored; achiral members are neutral.
    void Families(MotifInput input, StreamWork[] work)
    {
        var atoms = new List<Atom>(); var index = new Dictionary<string, int>(StringComparer.Ordinal);   // looked up only
        var order = Paradigms.OrderBy(p => Cells[p.Prototype].Start).ThenBy(p => p.Stream).ThenBy(p => p.Id).ToList();
        foreach (var p in order)
        {
            var proto = Cells[p.Prototype]; var w = work[p.Stream]; var s = w.S;
            if (p.Space == Drum) { p.Kind = "Groove"; p.Signature = ""; p.Name = "groove"; continue; }
            string signature; int sign;
            if (p.Space == Texture) { signature = "Shape"; sign = 0; p.Symmetry = TextureSymmetry(input, s, proto.W); }
            else
            {
                (signature, sign) = w.Line != null ? w.Line.Signature(proto.W.Tie, proto.W.First, proto.W.First + proto.W.Count) : ("", 0);
                if (signature.Length == 0) (signature, sign) = Contour(Notes(input, s, proto.W), p.Space == Diatonic);
            }
            // A neighbour iterated (N N …) is an oscillation, one family with the contour's oscillations.
            var tokens = signature.Split(' ');
            if (tokens.Length >= 2 && tokens.All(t => t == "N")) { signature = "Osc"; tokens = new[] { "Osc" }; }
            p.Signature = signature;
            p.Kind = tokens[0];
            p.Sign = p.Achiral ? 0 : sign;
            var notes = Notes(input, s, proto.W);
            p.HeldHead = notes.Length >= 2 && (proto.W.Tie >= 0 || Span(notes, 0) >= 2 * Median(notes));
            if (!index.TryGetValue(signature, out int a))
            {
                a = atoms.Count; index[signature] = a;
                atoms.Add(new Atom { Id = a, Kind = p.Kind, Signature = signature, Name = AtomName(p) });
            }
            p.Atom = a;
            var atom = atoms[a];
            atom.Paradigms = atom.Paradigms.Append(p.Id).ToArray();
            foreach (int m in p.Members)
            {
                var c = Cells[m];
                c.Sign = c.W.Achiral || p.Sign == 0 ? 0 : c.G.Inverted ? -p.Sign : p.Sign;
                atom.Members++;
                if (c.Sign > 0) atom.Positive++; else if (c.Sign < 0) atom.Negative++; else atom.Neutral++;
            }
        }
        // Letters by first appearance: an atom takes the next Greek letter; its paradigms add the sign when the atom holds
        // both signs, and primes when one letter repeats.
        const string Greek = "αβγδεζηθικλμνξοπρστυφχψω";
        for (int a = 0; a < atoms.Count; a++) atoms[a].Letter = Greek[a % Greek.Length] + (a >= Greek.Length ? (a / Greek.Length + 1).ToString() : "");
        var used = new List<string>();
        foreach (var p in order)
        {
            if (p.Atom < 0) continue;
            var atom = atoms[p.Atom];
            bool both = atom.Paradigms.Any(q => Paradigms[q].Sign > 0) && atom.Paradigms.Any(q => Paradigms[q].Sign < 0);
            string letter = atom.Letter + (both ? p.Sign > 0 ? "⁺" : p.Sign < 0 ? "⁻" : "" : "");
            string l = letter; while (used.Contains(l)) l += "′";
            used.Add(l); p.Letter = l;
            p.Name = ParadigmName(p);
        }
        // A P family holding a falling line opened by a held head is the sigh (§5 C7), whichever paradigm came first.
        foreach (var atom in atoms) if (atom.Kind == "P" && atom.Paradigms.Any(q => Paradigms[q].Sign < 0 && Paradigms[q].HeldHead)) atom.Name = "sigh";
        Atoms = atoms.ToArray();
    }
    static long Span(CellNote[] notes, int q) => q + 1 < notes.Length ? notes[q + 1].Onset - notes[q].Onset : notes[q].Length;
    static double Median(CellNote[] notes)
    {
        var spans = new List<long>(); for (int q = 1; q < notes.Length; q++) spans.Add(Span(notes, q));
        if (spans.Count == 0) return double.MaxValue;
        spans.Sort(); return spans[(spans.Count - 1) / 2];
    }
    static (string, int) Contour(CellNote[] notes, bool diatonic)
    {
        var steps = new List<int>();
        for (int q = 1; q < notes.Length; q++) steps.Add(diatonic ? notes[q].Morph - notes[q - 1].Morph : notes[q].Pitch - notes[q - 1].Pitch);
        if (steps.Count == 0 || steps.All(d => d == 0)) return ("Hold", 0);
        if (steps.Count >= 3 && steps.All(d => Math.Abs(d) == Math.Abs(steps[0]) && d != 0) && steps.Zip(steps.Skip(1)).All(p => p.First == -p.Second)) return ("Osc", Math.Sign(steps[0]));
        int leaps = steps.Count(d => Math.Abs(d) >= (diatonic ? 2 : 3));
        int net = diatonic ? notes[^1].Morph - notes[0].Morph : notes[^1].Pitch - notes[0].Pitch;
        int sign = Math.Sign(net != 0 ? net : steps.First(d => d != 0));
        return (leaps * 2 >= steps.Count ? "Arp" : "P", sign);
    }
    static int[] TextureSymmetry(MotifInput input, MotifStreams.Stream s, Window x)
    {
        int[] stab = null;
        foreach (int k in EventsOf(x))
        {
            var here = TransformGroup.Stabiliser(Sonority(input, s, x, k));
            stab = stab == null ? here : stab.Intersect(here).ToArray();
        }
        return stab ?? new[] { 0 };
    }
    static string AtomName(Paradigm p) => p.Kind switch
    {
        "N" => "neighbour figure", "T" => "turn", "P" => p.Sign < 0 && p.HeldHead ? "sigh" : "passing line", "A" => "appoggiatura figure",
        "Osc" => "oscillation", "Arp" => "arpeggio", "Shape" => "hand shape", "Hold" => "held note", _ => p.Kind
    };
    static string ParadigmName(Paradigm p) => p.Kind switch
    {
        "N" => p.Sign > 0 ? "upper neighbour" : p.Sign < 0 ? "lower neighbour" : "neighbour",
        "P" => p.Sign < 0 && p.HeldHead ? "sigh" : p.Sign < 0 ? "falling line" : "rising line",
        "T" => "turn", "A" => "appoggiatura", "Osc" => "oscillation", "Arp" => "arpeggio", "Shape" => "hand shape", "Hold" => "held note", _ => p.Kind
    };

    // Reports and tests: the cells of one stream, and a paradigm's members.
    public IEnumerable<Cell> Of(int stream) { for (int i = StreamFirst[stream]; i < StreamFirst[stream] + StreamCount[stream]; i++) yield return Cells[i]; }
    public IEnumerable<Cell> MembersOf(Paradigm p) { foreach (int m in p.Members) yield return Cells[m]; }
}
