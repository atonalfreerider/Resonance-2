// Repetition-first song structure: find what repeats before deciding where sections are.
//
// 1. Thumbnails. Every stretch of a phrase-grid length is compared with every other
//    position (all 12 transpositions). The stretch whose non-overlapping copies explain
//    the most bars, less the cost of describing it once and of each visit, becomes a
//    family. Its copies are claimed and the search repeats on the unclaimed bars, so
//    the most compressive pattern (a chorus heard six times) is found first and a
//    repeated strain (A A B B) is found even when its copies are adjacent.
// 2. A long thumbnail that contains a consistent change (a verse–chorus pair returning
//    as one block) is divided where all its copies change, so each section can link
//    with material heard elsewhere (a final chorus on its own).
// 3. Bars no thumbnail explains are through-composed; the novelty/phrase-grid DP divides
//    them, and a piece that matches an existing family (a partial return) joins it.
// 4. Consecutive visits of a short family are one section playing its loop again (the
//    verse is its four-bar loop twice), so they merge; section-length strains stay apart.
public static partial class FormAnalysis
{
    // Diagonal prefix sums of bar similarity: the mean similarity of any two equal-length
    // stretches, at any transposition, in constant time.
    sealed class Diagonals
    {
        readonly float[][,] full = new float[12][,], harmony = new float[12][,];
        public Diagonals(Bars bars)
        {
            int n = bars.Count;
            for (int t = 0; t < 12; t++)
            {
                var f = new float[n + 1, n + 1]; var h = new float[n + 1, n + 1];
                for (int i = 1; i <= n; i++)
                    for (int j = 1; j <= n; j++) { f[i, j] = f[i - 1, j - 1] + bars.Sim[t][i - 1, j - 1]; h[i, j] = h[i - 1, j - 1] + bars.Harm[t][i - 1, j - 1]; }
                full[t] = f; harmony[t] = h;
            }
        }
        public double Mean(int t, int i, int j, int length, bool harmonic)
        {
            var a = harmonic ? harmony[t] : full[t];
            return (a[i + length, j + length] - a[i, j]) / length;
        }
        double Score(int t, int i, int j, int length, bool classical)
        {
            double s = Mean(t, i, j, length, false);
            // Classical variations keep their harmony while the melody is ornamented.
            return classical ? Math.Max(s, Mean(t, i, j, length, true) - .08) : s;
        }
        // [i, i+length) transposed by t best matches [j, j+length). A copy must match all
        // the way through: every phrase-sized window, not just on average (half a verse
        // and a whole chorus is not a copy of a verse and a chorus).
        public (double score, int t) Match(int i, int j, int length, bool classical, int window)
        {
            double best = double.NegativeInfinity; int bestT = 0;
            for (int t = 0; t < 12; t++)
            {
                double s = Score(t, i, j, length, classical) - (t == 0 ? 0 : TransposePenalty);
                if (s <= best || s < CopyThreshold) continue;
                if (length > window)
                    for (int k = 0; k < length; k += window)
                    {
                        int size = Math.Min(window, length - k);
                        if (Score(t, i + k, j + k, size, classical) < CopyThreshold - .12) { s = double.NegativeInfinity; break; }
                    }
                if (s > best) { best = s; bestT = t; }
            }
            return (best, bestT);
        }
    }

    const double CopyThreshold = .74;
    static double Explained(double score) => Math.Clamp((score - .55) / .45, 0, 1);

    internal sealed class Visit { public int Start, Length, Family, T; public double Score; }

    internal static (List<(int start, int length)> segments, int[] family, int[] transpose, double[] similarity) RepeatStructure(SegmentModel m, Tuning w)
    {
        var bars = m.Bars; int n = bars.Count, unit = m.Unit; bool classical = m.Classical;
        if (n < 4) { var one = new List<(int, int)> { (0, n) }; return (one, new[] { 0 }, new[] { 0 }, new[] { 1.0 }); }
        // Steps 1–2 do not depend on the weights: sweeps reuse them.
        m.Thumbnails ??= Thumbnails(m);
        var visits = m.Thumbnails.visits.Select(v => new Visit { Start = v.Start, Length = v.Length, Family = v.Family, T = v.T, Score = v.Score }).ToList();
        int families = m.Thumbnails.families, minLength = Math.Max(2, unit / 2);
        // 3. Through-composed stretches between the thumbnails.
        var pieces = new List<Visit>();
        for (int a = 0; a < n;)
        {
            var covering = visits.FirstOrDefault(v => v.Start <= a && a < v.Start + v.Length);
            if (covering != null) { a = covering.Start + covering.Length; continue; }
            int b = a; while (b < n && !visits.Any(v => v.Start <= b && b < v.Start + v.Length)) b++;
            // A bar or two between returns is a lead-in or a tag of its neighbour, not a
            // section. At the very start or end it stands alone (a count-in, a final chord).
            if (b - a < minLength && a > 0 && b < n)
            {
                var before = visits.FirstOrDefault(v => v.Start + v.Length == a); var after = visits.FirstOrDefault(v => v.Start == b);
                var host = a == 0 || before == null ? after : before;
                if (host != null) { if (host == after) host.Start = a; host.Length += b - a; a = b; continue; }
            }
            foreach (var (start, length) in Solve(m, w, a, b)) pieces.Add(new Visit { Start = start, Length = length, Family = -1, Score = 1 });
            a = b;
        }
        // A through-composed piece that matches a family (or another piece) joins it.
        var firsts = visits.GroupBy(v => v.Family).ToDictionary(g => g.Key, g => g.OrderBy(v => v.Start).First());
        foreach (var piece in pieces)
        {
            double bestScore = 0; Visit match = null; int matchT = 0;
            foreach (var other in firsts.Values.Concat(pieces.Where(p => p.Family >= 0 && p != piece)))
            {
                var (score, t, _) = Align(bars, (other.Start, other.Length), (piece.Start, piece.Length));
                if (score >= .78 && score > bestScore) { bestScore = score; match = other; matchT = t; }
            }
            if (match != null) { piece.Family = match.Family; piece.T = HarmonyModel.Mod(match.T + matchT); piece.Score = bestScore; }
            else { piece.Family = families++; firsts[piece.Family] = piece; }
        }
        var all = visits.Concat(pieces).OrderBy(v => v.Start).ToList();
        // 4. A short family visited back to back is one section looping (the verse is its
        // four-bar loop twice). Merged before linking, so whole sections are compared.
        int mergeLimit = classical ? unit * 2 - 1 : unit * 2;
        void MergeLoops()
        {
            var typical = all.GroupBy(v => v.Family).ToDictionary(g => g.Key, g => g.Min(v => v.Length));
            for (int i = all.Count - 1; i > 0; i--)
            {
                var (x, y) = (all[i - 1], all[i]);
                if (x.Family == y.Family && x.T == y.T && typical[x.Family] <= mergeLimit) { x.Length += y.Length; x.Score = Math.Min(x.Score, y.Score); all.RemoveAt(i); }
            }
        }
        MergeLoops();
        // Families found separately can be one family: a recapitulation's theme, or a final
        // chorus, was claimed by its own thumbnail before it could be compared with the first.
        var leaders = all.GroupBy(v => v.Family).Select(g => g.First()).ToList();
        for (int i = 0; i < leaders.Count; i++)
            for (int k = i + 1; k < leaders.Count; k++)
            {
                if (leaders[i].Family == leaders[k].Family) continue;
                var (score, t, _) = Align(bars, (leaders[i].Start, leaders[i].Length), (leaders[k].Start, leaders[k].Length));
                if (score < .8) continue;
                int from = leaders[k].Family, into = leaders[i].Family, shift = HarmonyModel.Mod(leaders[i].T + t - leaders[k].T);
                foreach (var v in all.Where(v => v.Family == from)) { v.Family = into; v.T = HarmonyModel.Mod(v.T + shift); v.Score = Math.Min(v.Score, score); }
                foreach (var l in leaders.Where(l => l.Family == from)) l.Family = into;
            }
        MergeLoops();
        // Families in order of first appearance, transpositions relative to the first visit.
        var order = new Dictionary<int, int>();
        foreach (var v in all) if (!order.ContainsKey(v.Family)) order[v.Family] = order.Count;
        var segments = all.Select(v => (v.Start, v.Length)).ToList();
        var family = all.Select(v => order[v.Family]).ToArray();
        var firstT = all.GroupBy(v => v.Family).ToDictionary(g => g.Key, g => g.First().T);
        var transpose = all.Select(v => HarmonyModel.Mod(v.T - firstT[v.Family])).ToArray();
        var similarity = all.Select((v, i) => all.FindIndex(x => x.Family == v.Family) == i ? 1 : Math.Clamp(v.Score, 0, 1)).ToArray();
        if (Verbose)
            foreach (var v in all) Console.WriteLine($"  structure {v.Start + 1,3}+{v.Length,-3} family {order[v.Family]} t{v.T} score {v.Score:0.00}");
        return (segments, family, transpose, similarity);
    }
    internal sealed class ThumbnailSet { public List<Visit> visits; public int families; }
    static ThumbnailSet Thumbnails(SegmentModel m)
    {
        var bars = m.Bars; int n = bars.Count, unit = m.Unit; bool classical = m.Classical;
        var d = new Diagonals(bars);
        var owner = Enumerable.Repeat(-1, n).ToArray(); var claimed = new int[n + 1];
        int Claimed(int a, int length) => claimed[a + length] - claimed[a];
        var visits = new List<Visit>();
        int minLength = Math.Max(2, unit / 2);
        // Phrase-grid lengths from half a phrase up to a whole exposition.
        var lengths = new[] { unit / 2, unit, unit * 3 / 2, unit * 2, unit * 3, unit * 4, unit * 6, unit * 8, unit * 12, unit * 16 }.Where(x => x >= minLength && x * 2 <= n).Distinct().ToArray();
        double perVisit = Math.Max(1, unit / 4.0), align = .5;
        double Start(int b) => b == 0 ? 1 : Math.Min(1, m.Novelty[b]);
        int families = 0;
        // 1. Thumbnails, most compressive first.
        while (true)
        {
            double bestFit = 1e-6; List<Visit> best = null;
            foreach (int length in lengths)
                for (int s = 0; s + length <= n; s++)
                {
                    if (Claimed(s, length) > 0) continue;
                    var copies = new List<(int j, int t, double score)>();
                    for (int j = 0; j + length <= n; j++)
                    {
                        if (Math.Abs(j - s) < length || Claimed(j, length) > 0) continue;
                        var (score, t) = d.Match(s, j, length, classical, unit);
                        if (score >= CopyThreshold) copies.Add((j, t, score));
                    }
                    if (copies.Count == 0) continue;
                    var chosen = new List<Visit> { new() { Start = s, Length = length, T = 0, Score = 1 } };
                    foreach (var c in copies.OrderByDescending(c => c.score + .05 * Start(c.j)).ThenBy(c => c.j))
                        if (chosen.All(v => Math.Abs(v.Start - c.j) >= length)) chosen.Add(new Visit { Start = c.j, Length = length, T = c.t, Score = c.score });
                    double fit = chosen.Sum(v => length * Explained(v.Score)) - length - perVisit * chosen.Count + align * chosen.Sum(v => Start(v.Start));
                    if (fit > bestFit + 1e-9) { bestFit = fit; best = chosen; }
                }
            if (best == null) break;
            foreach (var v in best) { v.Family = families; visits.Add(v); for (int k = v.Start; k < v.Start + v.Length; k++) owner[k] = families; }
            for (int k = 0; k < n; k++) claimed[k + 1] = claimed[k] + (owner[k] >= 0 ? 1 : 0);
            families++;
        }
        // 2. Divide long thumbnails where every copy changes together.
        foreach (int f in visits.Select(v => v.Family).Distinct().ToList())
        {
            var copies = visits.Where(v => v.Family == f).ToList();
            int length = copies[0].Length;
            if (length < unit * 3) continue;
            var cuts = new SortedSet<int>();
            var inside = new int[n + 1]; foreach (var v in copies) for (int k = v.Start; k < v.Start + v.Length; k++) inside[k + 1] = 1;
            for (int k = 0; k < n; k++) inside[k + 1] += inside[k];
            // A part of this block heard on its own elsewhere (a chorus that returns without
            // its verse) marks the block's inner boundaries exactly: cut where it begins and ends.
            void PartialReturns(int from, int to)
            {
                // The return that explains the most: its length weighted by how well it matches.
                int bestPart = 0, bestR = -1; double bestValue = 0;
                for (int part = to - from - (from == 0 && to == length ? unit : 0); part >= unit; part--)
                    for (int r = from; r + part <= to; r++)
                    {
                        if (r > 0 && r < unit || r + part < length && r + part > length - unit) continue;
                        for (int j = 0; j + part <= n; j++)
                        {
                            if (inside[j + part] - inside[j] > 0) continue;
                            var (score, t) = d.Match(copies[0].Start + r, j, part, classical, unit);
                            if (score < CopyThreshold) continue;
                            // A return begins and ends on bars that really match, not on a
                            // neighbour that happens to share a chord.
                            int a = copies[0].Start + r;
                            if (d.Mean(t, a, j, 1, false) < CopyThreshold || d.Mean(t, a + part - 1, j + part - 1, 1, false) < CopyThreshold) continue;
                            // Between equally good returns, cut where the music changes.
                            double value = part * Explained(score) + (r > 0 ? m.Change[copies[0].Start + r] : 0) + (r + part < length ? m.Change[copies[0].Start + r + part] : 0);
                            if (score >= CopyThreshold && value > bestValue + 1e-9) { bestValue = value; bestPart = part; bestR = r; }
                        }
                    }
                if (bestR < 0) return;
                if (bestR > from) { cuts.Add(bestR); PartialReturns(from, bestR); }
                if (bestR + bestPart < to) { cuts.Add(bestR + bestPart); PartialReturns(bestR + bestPart, to); }
            }
            PartialReturns(0, length);
            // Otherwise cut at clear changes that every copy shares (the change curve averaged
            // over the copies), into parts of at least a phrase.
            if (cuts.Count == 0)
                for (int r = unit; r <= length - unit; r++)
                {
                    double Change(int x) => copies.Average(v => m.Change[v.Start + x]);
                    double change = Change(r);
                    if (change < .55 || Change(r - 1) > change || Change(r + 1) > change) continue;
                    if (cuts.Count == 0 || r - cuts.Max >= unit) cuts.Add(r);
                }
            cuts.RemoveWhere(c => c <= 0 || c >= length);
            if (cuts.Count == 0) continue;
            var edges = cuts.Prepend(0).Append(length).Distinct().OrderBy(x => x).ToList();
            foreach (var v in copies)
            {
                visits.Remove(v);
                for (int k = 0; k + 1 < edges.Count; k++)
                    visits.Add(new Visit { Start = v.Start + edges[k], Length = edges[k + 1] - edges[k], Family = k == 0 ? f : families + k - 1, T = v.T, Score = v.Score });
            }
            families += edges.Count - 2;
        }
        return new ThumbnailSet { visits = visits, families = families };
    }
}
