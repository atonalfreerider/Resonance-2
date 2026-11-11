// Stage B of the motif reduction (Docs/MOTIF-REDUCTION.md §4): each line reduced to the notes that carry it, every
// removed note a typed diminution token (a Schenkerian quotient), and the arrivals of the principal line, where its
// phrases end. The reduction never decides losslessness: it names the figures (the families of §5 C7 and their
// chirality), tells an arrival from a prolonged note, and prices ornaments in the fuzzy join.
public static class Diminution
{
    // Token kinds. A turn removes two notes: the first carries the turn (T), the second its tail, which names nothing.
    public const byte Structural = 0, Rep = 1, Turn = 2, Appoggiatura = 3, Neighbour = 4, Passing = 5, TurnTail = 6;
    static readonly string[] Kinds = { "", "Rep", "T", "A", "N", "P", "T" };
    public static string KindName(byte kind) => Kinds[kind];

    // One stream's lead line reduced to a fixpoint. Per stream event: the token that removed it (Structural when it
    // stays), its sign (+ upper or rising, − lower or falling, 0 for a repetition) and the structural note it decorates
    // (itself when it stays). Tokens[k] lists, for a structural note, the notes it absorbed in the order it absorbed them,
    // each followed by what that note had absorbed: E4[N− Rep] F♯4[N+ Rep Rep] for the étude's bar 1.
    public sealed class Line
    {
        public int Stream = -1;
        public byte[] Kind = Array.Empty<byte>();
        public sbyte[] Sign = Array.Empty<sbyte>();
        public int[] Head = Array.Empty<int>();
        public int[][] Tokens = Array.Empty<int[]>();
        public int Events => Kind.Length;
        public bool IsStructural(int k) => Kind[k] == Structural;
        // The note's role as the bundle writes it (MotifNote.Role): "" structural, "N+", "P-", "T+", "A-", "Rep".
        public string Role(int k) => Kind[k] switch { Structural => "", Rep => "Rep", _ => Kinds[Kind[k]] + (Sign[k] < 0 ? "-" : "+") };
        // Whether a prolonging token (N, T or P) decorates this note: such a note is the head of a figure, never an arrival.
        public bool Prolonged(int k)
        {
            foreach (int x in Tokens[k]) if (Kind[x] is Neighbour or Turn or TurnTail or Passing) return true;
            return false;
        }
        // The diminution signature of the events [from, to) plus an optional tied-in note (§5 C7): the tokens of their
        // structural notes whose ornaments also lie inside, signs dropped and repetitions left out ("N", "A P N", "N T").
        // Sign: the first token's sign, 0 without one.
        public (string signature, int sign) Signature(int tie, int from, int to)
        {
            var parts = new List<string>(); int sign = 0;
            void Collect(int k)
            {
                if (k < 0 || k >= Events || !IsStructural(k)) return;
                foreach (int x in Tokens[k])
                {
                    if (x < from || x >= to || Kind[x] is Rep or TurnTail) continue;
                    parts.Add(Kinds[Kind[x]]);
                    if (sign == 0) sign = Sign[x];
                }
            }
            if (tie >= 0 && tie < from) Collect(tie);
            for (int k = from; k < to; k++) Collect(k);
            return (string.Join(" ", parts), sign);
        }
        // "E4[N− Rep]": a structural note and its tokens, for reports and tests.
        public string Describe(MotifInput input, MotifStreams.Stream s, int k)
        {
            var e = input.Events[s.Lead[k]]; int r = input.Frames.Region(e.Tick);
            string name = input.Frames.NoteName(e.Fifths, r).Replace(Spelling.Natural, "") + Spelling.FloorDiv(e.Morph, 7);
            if (Tokens[k].Length == 0) return name;
            var tokens = new List<string>();
            foreach (int x in Tokens[k]) if (Kind[x] != TurnTail) tokens.Add(Kind[x] == Rep ? "Rep" : Kinds[Kind[x]] + (Sign[x] < 0 ? "−" : "+"));
            return name + "[" + string.Join(" ", tokens) + "]";
        }
    }

    // The pitch classes of the chord sounding at a tick, as a 12-bit mask (0 when no chord is known: then every note is
    // a chord tone, so nothing is ranked or resolved as a dissonance).
    public static int ChordMask(KeyFrames frames, long tick)
    {
        int c = frames.Chord(tick), root = frames.RootPc(c);
        if (root < 0) return 0;
        int mask = 0;
        foreach (int x in ChordIntervals(frames.ChordQuality[c])) mask |= 1 << Spelling.Mod(root + x, 12);
        return mask;
    }
    static readonly int[] Major = { 0, 4, 7 }, MinorTriad = { 0, 3, 7 }, Seventh = { 0, 4, 7, 10 }, MajorSeventh = { 0, 4, 7, 11 }, MinorSeventh = { 0, 3, 7, 10 }, Diminished = { 0, 3, 6 },
        DiminishedSeventh = { 0, 3, 6, 9 }, HalfDiminished = { 0, 3, 6, 10 }, Augmented = { 0, 4, 8 }, Sus2 = { 0, 2, 7 }, Sus4 = { 0, 5, 7 }, Sixth = { 0, 4, 7, 9 }, MinorSixth = { 0, 3, 7, 9 };
    public static int[] ChordIntervals(string quality) => quality switch
    {
        "m" => MinorTriad, "7" => Seventh, "maj7" => MajorSeventh, "m7" => MinorSeventh, "dim" => Diminished, "dim7" => DiminishedSeventh, "m7b5" => HalfDiminished,
        "aug" => Augmented, "sus2" => Sus2, "sus4" => Sus4, "6" => Sixth, "m6" => MinorSixth,
        _ => quality.StartsWith("m", StringComparison.Ordinal) && !quality.StartsWith("maj", StringComparison.Ordinal) ? MinorTriad : Major
    };
    public static bool ChordTone(KeyFrames frames, long tick, int pitch) { int mask = ChordMask(frames, tick); return mask == 0 || (mask >> Spelling.Mod(pitch, 12) & 1) != 0; }

    // Every voice, line and rest stream reduced (null for textures, both hands and drums, which carry no line).
    public static Line[] ReduceAll(MotifInput input)
    {
        var all = input.Streams.All; var lines = new Line[all.Length];
        for (int s = 0; s < all.Length; s++) if (all[s].Kind is MotifStreams.Voice or MotifStreams.Line or MotifStreams.Rest) lines[s] = Reduce(input, all[s]);
        return lines;
    }

    // §4 B1. The line is the stream's leads, one per onset; a note's length is its span to the next note still standing,
    // so a note grows as it absorbs the ones after it, and a short note before a rest is no ornament. Each pass merges
    // repetitions, then turns, then appoggiaturas (each over the whole line), then removes one rank class of neighbours
    // and passing notes, non-adjacent; passes repeat until nothing changes. Rank: non-chord tones first, closed figures
    // (N) before open ones (P), then the weakest metric position. Steps are diatonic (morphs).
    public static Line Reduce(MotifInput input, MotifStreams.Stream s)
    {
        int n = s.Events;
        var line = new Line { Stream = s.Id, Kind = new byte[n], Sign = new sbyte[n], Head = new int[n], Tokens = new int[n][] };
        if (n == 0) return line;
        var ev = input.Events; var grid = input.Grid; var frames = input.Frames;
        var on = (long[])s.Onset.Clone();
        var m = new int[n]; var p = new int[n]; var ct = new bool[n];
        long lastEnd = 0;
        for (int k = 0; k < n; k++)
        {
            var e = ev[s.Lead[k]]; m[k] = e.Morph; p[k] = e.Pitch; ct[k] = ChordTone(frames, s.Onset[k], e.Pitch);
            lastEnd = Math.Max(lastEnd, e.Tick + e.Duration);
        }
        var prev = new int[n]; var next = new int[n]; var parent = new int[n];
        var tokens = new List<int>[n];
        for (int k = 0; k < n; k++) { prev[k] = k - 1; next[k] = k + 1 < n ? k + 1 : -1; parent[k] = -1; tokens[k] = new List<int>(); }
        int first = 0;
        long Span(int k) => next[k] >= 0 ? on[next[k]] - on[k] : Math.Max(1, lastEnd - on[k]);
        bool Short(int k) => Span(k) * 2 <= grid.TactusAt(on[k]);
        int W(int k) => grid.Weight(on[k]);
        void Remove(int x)
        {
            if (prev[x] >= 0) next[prev[x]] = next[x]; else first = next[x];
            if (next[x] >= 0) prev[next[x]] = prev[x];
        }
        // x closes a neighbour or passing figure with the notes around it (the N/P conditions below).
        bool Figure(int x)
        {
            int a = prev[x], b = next[x];
            if (a < 0 || b < 0) return false;
            int d1 = m[x] - m[a], d2 = m[b] - m[x];
            return Math.Abs(d1) == 1 && Math.Abs(d2) == 1 && Short(x) && W(x) < W(a);
        }
        void Absorb(int head, int x, byte kind, int sign)
        {
            line.Kind[x] = kind; line.Sign[x] = (sbyte)sign; parent[x] = head;
            tokens[head].Add(x); tokens[head].AddRange(tokens[x]); tokens[x].Clear();
            Remove(x);
        }

        var candidates = new List<(int ct, int kind, int w, int x, int before, byte token, int sign)>();
        var taken = new bool[n];
        bool changed = true;
        while (changed)
        {
            changed = false;
            // Repetitions: the same pitch again, short, no stronger than the note it repeats (or at most on a beat).
            for (int x = first; x >= 0;)
            {
                int a = prev[x], after = next[x];
                if (a >= 0 && p[x] == p[a] && Short(x) && W(x) <= Math.Max(W(a), 2)) { Absorb(a, x, Rep, 0); changed = true; }
                x = after;
            }
            // Turns x, x±1, x∓1, x: both middle notes short and weaker than x, which absorbs them; the return stays.
            for (int x = first; x >= 0; x = next[x])
            {
                int b = next[x]; if (b < 0) break;
                int c = next[b]; if (c < 0) break;
                int d = next[c]; if (d < 0) break;
                int up = m[b] - m[x];
                if (Math.Abs(up) == 1 && m[c] - m[x] == -up && m[d] == m[x] && Short(b) && Short(c) && W(b) < W(x) && W(c) < W(x))
                {
                    Absorb(x, b, Turn, up); Absorb(x, c, TurnTail, -up); changed = true;
                }
            }
            // Appoggiaturas: a non-chord tone on a stronger position than the next note, which resolves it by step to a
            // chord tone; the resolution is structural and inherits the onset. Both span at most a tactus (a longer note
            // is a held head, like the étude's F♯4 at 1.2), and a note that also closes a neighbour or passing figure with
            // the note before it is that figure (the étude's E4 D♯4 E4 is N−, not an appoggiatura to the second E).
            for (int x = first; x >= 0;)
            {
                int r = next[x], after = next[x];
                if (r >= 0 && Math.Abs(m[r] - m[x]) == 1 && W(x) > W(r) && !ct[x] && ct[r] && Span(x) <= grid.TactusAt(on[x]) && Span(r) <= grid.TactusAt(on[r]) && !Figure(x))
                {
                    line.Kind[x] = Appoggiatura; line.Sign[x] = (sbyte)Math.Sign(m[r] - m[x]); parent[x] = r;
                    var moved = new List<int> { x }; moved.AddRange(tokens[x]); tokens[x].Clear();
                    tokens[r].InsertRange(0, moved);
                    on[r] = on[x]; Remove(x); changed = true;
                    after = next[r];
                }
                x = after;
            }
            // Neighbours (step away and back) and passing notes (two steps one way), short and weaker than the note
            // before, which absorbs them: one rank class per pass, non-adjacent.
            candidates.Clear();
            for (int x = first; x >= 0; x = next[x])
            {
                int a = prev[x], b = next[x];
                if (a < 0 || b < 0) continue;
                int d1 = m[x] - m[a], d2 = m[b] - m[x];
                if (Math.Abs(d1) != 1 || !Short(x) || W(x) >= W(a)) continue;
                if (d2 == -d1) candidates.Add((ct[x] ? 1 : 0, 0, W(x), x, a, Neighbour, d1));
                else if (d2 == d1) candidates.Add((ct[x] ? 1 : 0, 1, W(x), x, a, Passing, d1));
            }
            if (candidates.Count > 0)
            {
                var best = candidates[0];
                foreach (var c in candidates) if ((c.ct, c.kind, c.w).CompareTo((best.ct, best.kind, best.w)) < 0) best = c;
                // In line order, so of two adjacent candidates the earlier goes; the later waits for the next pass.
                foreach (var c in candidates)
                {
                    if ((c.ct, c.kind, c.w) != (best.ct, best.kind, best.w) || taken[c.before]) continue;
                    Absorb(c.before, c.x, c.token, c.sign); taken[c.x] = true; changed = true;
                }
                foreach (var c in candidates) taken[c.x] = false;
            }
        }
        for (int k = 0; k < n; k++)
        {
            int h = k; while (parent[h] >= 0) h = parent[h];
            line.Head[k] = h;
            line.Tokens[k] = line.Kind[k] == Structural ? tokens[k].ToArray() : Array.Empty<int>();
        }
        return line;
    }

    // A principal-line note that ends a phrase (§4 B2): Full over the local tonic, else a half arrival over the dominant.
    public readonly struct Arrival
    {
        public readonly int Event, Note, Pitch, Bass;   // stream event, its lead (input event), its pitch, the bass under it
        public readonly long Tick;
        public readonly bool Full;
        public Arrival(int @event, int note, int pitch, int bass, long tick, bool full) { Event = @event; Note = note; Pitch = pitch; Bass = bass; Tick = tick; Full = full; }
    }

    // §4 B2. A principal note n at τ arrives when it lies on a tactus beat (weight ≥ 2), the line's next onset is a tactus
    // or more away, n is not prolonged (its next note is not a diatonic step away, and no N, T or P decorates it), and the
    // bass at τ is the local tonic (full) or the local dominant under a major or dominant-seventh chord on it (half). Of
    // two arrivals closer than two bars the stronger stays (full over half; the later at equal strength). A MIDI note is
    // never tied in: ties are already one note.
    public static List<Arrival> Arrivals(MotifInput input, Line line)
    {
        var result = new List<Arrival>();
        var streams = input.Streams;
        if (line == null || line.Stream < 0 || line.Events == 0) return result;
        var s = streams[line.Stream]; var ev = input.Events; var grid = input.Grid; var frames = input.Frames;
        // Without Grid.Bass: the lowest pitched note sounding at τ.
        var pitched = new List<int>();
        for (int i = 0; i < ev.Length; i++) if (!ev[i].Drum && !ev[i].Grace) pitched.Add(i);
        long longest = 0; foreach (int i in pitched) longest = Math.Max(longest, ev[i].Duration);
        int Lowest(long tick)
        {
            int low = -1;
            int hi = UpperBound(ev, pitched, tick);
            for (int j = hi - 1; j >= 0 && ev[pitched[j]].Tick >= tick - longest; j--)
            {
                var e = ev[pitched[j]];
                if (e.Tick <= tick && tick < e.Tick + e.Duration && (low < 0 || e.Pitch < low)) low = e.Pitch;
            }
            return low;
        }
        for (int k = 0; k < line.Events; k++)
        {
            if (!line.IsStructural(k)) continue;
            long tau = s.Onset[k];
            if (grid.Weight(tau) < 2) continue;
            if (k + 1 < s.Events)
            {
                if (s.Onset[k + 1] - tau < grid.TactusAt(tau)) continue;
                if (Math.Abs(ev[s.Lead[k + 1]].Morph - ev[s.Lead[k]].Morph) == 1) continue;
            }
            if (line.Prolonged(k)) continue;
            int bass = frames.Bass(tau); if (bass < 0) bass = Lowest(tau);
            if (bass < 0) continue;
            int r = frames.Region(tau), pc = Spelling.Mod(bass, 12);
            bool full = pc == frames.TonicPc(r);
            if (!full)
            {
                // Without any chords the bass alone speaks; with them, the chord on τ must be the dominant's (V or V7).
                int c = frames.Chord(tau);
                if (pc != frames.DominantPc(r)) continue;
                if (frames.Chords > 0 && (frames.RootPc(c) != frames.DominantPc(r) || frames.ChordQuality[c] is not ("" or "7"))) continue;
            }
            var a = new Arrival(k, s.Lead[k], ev[s.Lead[k]].Pitch, bass, tau, full);
            // The span's start bounds the first phrase like an arrival: none within two bars of it (the étude's pickup B3
            // over the dominant would otherwise close a phrase of no length).
            int at = Math.Max(0, grid.BarAt(tau));
            if (tau - grid.SpanStart < input.Settings.PhraseMinBars * (grid.BarEnd[at] - grid.BarStart[at])) continue;
            if (result.Count > 0)
            {
                var last = result[^1]; int bar = Math.Max(0, grid.BarAt(last.Tick));
                if (tau - last.Tick < input.Settings.PhraseMinBars * (grid.BarEnd[bar] - grid.BarStart[bar]))
                {
                    if (full || !last.Full) result[^1] = a;
                    continue;
                }
            }
            result.Add(a);
        }
        return result;
    }
    // The first index in `sorted` (event indices ordered by tick) whose tick lies after `tick`.
    static int UpperBound(MotifEvent[] ev, List<int> sorted, long tick)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (ev[sorted[mid]].Tick <= tick) lo = mid + 1; else hi = mid; }
        return lo;
    }
}
