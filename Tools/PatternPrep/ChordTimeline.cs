// Offline chord estimation on the notated beat grid.
// Each beat is scored against root/quality templates, then a Viterbi pass chooses the
// most plausible chord path: changing chord costs something, and costs least on a
// downbeat. This removes one-beat flicker from arpeggios, passing tones and pedalled
// figuration, so section progressions are short, stable and repeatable.
public static class ChordTimeline
{
    public static readonly (string quality, int[] notes)[] Qualities =
    {
        ("", new[] { 0, 4, 7 }), ("m", new[] { 0, 3, 7 }), ("7", new[] { 0, 4, 7, 10 }),
        ("maj7", new[] { 0, 4, 7, 11 }), ("m7", new[] { 0, 3, 7, 10 }), ("dim", new[] { 0, 3, 6 }),
    };
    public const int Rest = -1;
    static int Q => Qualities.Length;
    public static int State(int root, int quality) => HarmonyModel.Mod(root) * Q + quality;
    public static int Root(int state) => state < 0 ? -1 : state / Q;
    public static string Quality(int state) => state < 0 ? "" : Qualities[state % Q].quality;
    public static int Transpose(int state, int semitones) => state < 0 ? state : State(Root(state) + semitones, state % Q);

    public sealed class Grid
    {
        public double[] Start = Array.Empty<double>(), End = Array.Empty<double>();
        public double[][] Chroma = Array.Empty<double[]>();
        public int[] Bass = Array.Empty<int>(), Bar = Array.Empty<int>(), State = Array.Empty<int>();
        public double[] Weight = Array.Empty<double>();
        public int Count => Start.Length;
        public int[] StepsOfBar(int bar) => Enumerable.Range(0, Count).Where(i => Bar[i] == bar).ToArray();
    }

    public static double BeatUnit(MidiCycleAnalysis.Bar bar)
    {
        // Compound meters (6/8, 9/8, 12/8) are felt in dotted quarters.
        if (bar.Denominator == 8 && bar.Numerator % 3 == 0 && bar.Numerator > 3) return 1.5;
        return Math.Max(.5, 4.0 / bar.Denominator);
    }

    public static Grid Build(MidiCycleAnalysis cycles, int key = -1, bool minor = false)
    {
        var starts = new List<double>(); var ends = new List<double>(); var bars = new List<int>();
        for (int b = 0; b < cycles.Measures.Count; b++)
        {
            var bar = cycles.Measures[b]; double unit = BeatUnit(bar);
            for (double s = bar.Start; s < bar.End - 1e-6; s += unit) { starts.Add(s); ends.Add(Math.Min(bar.End, s + unit)); bars.Add(b); }
        }
        var grid = new Grid { Start = starts.ToArray(), End = ends.ToArray(), Bar = bars.ToArray() };
        int n = grid.Count;
        grid.Chroma = Enumerable.Range(0, n).Select(_ => new double[12]).ToArray();
        grid.Bass = Enumerable.Repeat(int.MaxValue, n).ToArray();
        grid.Weight = new double[n];
        foreach (var note in cycles.Notes.Where(h => h.Channel != 10))
        {
            int pc = HarmonyModel.Mod(note.Pitch - 21);
            double on = note.Beat, off = note.Beat + note.Length;
            // A short resonance tail stands in for pedal and room sound at half weight.
            double tail = off + Math.Min(1, Math.Max(.25, note.Length));
            int i = Array.BinarySearch(grid.End, on); if (i < 0) i = ~i;
            for (; i < n && grid.Start[i] < tail; i++)
            {
                double body = Math.Max(0, Math.Min(grid.End[i], off) - Math.Max(grid.Start[i], on));
                double decay = Math.Max(0, Math.Min(grid.End[i], tail) - Math.Max(grid.Start[i], off));
                double w = note.Velocity * (body + .5 * decay);
                if (w <= 0) continue;
                grid.Chroma[i][pc] += w; grid.Weight[i] += w;
                if (body > 0) grid.Bass[i] = Math.Min(grid.Bass[i], note.Pitch);
            }
        }
        grid.State = Decode(grid, cycles, key, minor);
        return grid;
    }

    static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };
    static bool Diatonic(int root, string quality, int key, bool minor)
    {
        if (key < 0) return false;
        int tonic = minor ? key + 3 : key; // relative major collection
        var tones = Qualities.First(q => q.quality == quality).notes.Take(3).Select(x => HarmonyModel.Mod(root + x - tonic));
        bool natural = tones.All(t => MajorScale.Contains(t));
        // Harmonic minor: major V and diminished vii° on the raised leading tone.
        bool raised = minor && HarmonyModel.Mod(root - key) is 7 or 11 && (quality is "" or "7" or "dim");
        return natural || raised;
    }

    static double Emission(Grid g, int i, int root, int q, int key, bool minor)
    {
        var c = g.Chroma[i]; double norm = Math.Sqrt(c.Sum(x => x * x)); if (norm <= 0) return 0;
        var (quality, notes) = Qualities[q];
        double[] weights = { 1, .85, .7, .55 };
        double dot = 0, wn = 0;
        for (int k = 0; k < notes.Length; k++) { dot += c[HarmonyModel.Mod(root + notes[k])] * weights[k]; wn += weights[k] * weights[k]; }
        double score = dot / (norm * Math.Sqrt(wn));
        if (g.Bass[i] != int.MaxValue)
        {
            int bass = HarmonyModel.Mod(g.Bass[i] - 21);
            if (bass == HarmonyModel.Mod(root)) score += .1;
            else if (notes.Any(x => HarmonyModel.Mod(root + x) == bass)) score += .02;
            else score -= .03;
        }
        if (notes.Length == 4) score -= .03;
        if (Diatonic(root, quality, key, minor)) score += .04;
        return score;
    }

    static int[] Decode(Grid g, MidiCycleAnalysis cycles, int key, bool minor)
    {
        int n = g.Count, states = 12 * Q;
        var result = Enumerable.Repeat(Rest, n).ToArray();
        if (n == 0) return result;
        double loud = g.Weight.Where(w => w > 0).DefaultIfEmpty(0).Average();
        var score = new double[states]; var next = new double[states];
        var back = new int[n, states];
        var emission = new double[states];
        bool[] silent = g.Weight.Select(w => w < Math.Max(1e-4, loud * .02)).ToArray();
        for (int i = 0; i < n; i++)
        {
            for (int s = 0; s < states; s++) emission[s] = silent[i] ? 0 : Emission(g, i, s / Q, s % Q, key, minor);
            if (i == 0) { Array.Copy(emission, score, states); continue; }
            var bar = cycles.Measures[g.Bar[i]];
            bool downbeat = Math.Abs(g.Start[i] - bar.Start) < 1e-6;
            bool half = Math.Abs(g.Start[i] - (bar.Start + bar.End) / 2) < 1e-6;
            double change = .24 * (downbeat ? .45 : half ? .7 : 1);
            int best = 0; for (int s = 1; s < states; s++) if (score[s] > score[best]) best = s;
            for (int s = 0; s < states; s++)
            {
                // Staying is free, a same-root quality change (C → C7) costs half, a new root costs one change.
                double value = score[s]; int from = s;
                double jump = score[best] - (best / Q == s / Q ? change * .5 : change);
                if (best != s && jump > value) { value = jump; from = best; }
                for (int t = s / Q * Q; t < s / Q * Q + Q; t++)
                    if (t != s && score[t] - change * .5 > value) { value = score[t] - change * .5; from = t; }
                next[s] = value + emission[s];
                back[i, s] = from;
            }
            (score, next) = (next, score);
        }
        int state = 0; for (int s = 1; s < states; s++) if (score[s] > score[state]) state = s;
        for (int i = n - 1; i >= 0; i--) { result[i] = state; if (i > 0) state = back[i, state]; }
        for (int i = 0; i < n; i++) if (silent[i]) result[i] = Rest;
        return result;
    }

    // Merge equal neighbouring steps into chord events on absolute beats.
    public static List<SongFormAnalysis.ChordStep> Steps(Grid g)
    {
        var output = new List<SongFormAnalysis.ChordStep>();
        for (int i = 0; i < g.Count; i++)
        {
            int s = g.State[i];
            double energy = g.Weight[i] / Math.Max(1e-6, g.End[i] - g.Start[i]);
            if (output.Count > 0 && output[^1].Root == Root(s) && output[^1].Quality == Quality(s) && Math.Abs(output[^1].End - g.Start[i]) < 1e-6)
            {
                var last = output[^1];
                last.Energy = (float)((last.Energy * (last.End - last.Start) + energy * (g.End[i] - g.Start[i])) / (g.End[i] - last.Start));
                last.End = g.End[i];
            }
            else output.Add(new SongFormAnalysis.ChordStep { Start = g.Start[i], End = g.End[i], Root = Root(s), Quality = Quality(s), Energy = (float)energy });
        }
        return output;
    }
}
