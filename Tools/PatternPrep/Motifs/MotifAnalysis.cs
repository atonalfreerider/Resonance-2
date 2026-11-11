using System.Diagnostics;
using System.Text;
using System.Text.Json;

// The motif reduction's entry point (Docs/MOTIF-REDUCTION.md §2, §12.1): stages A–G on one prepared bundle, the lossless gate
// and the export. Program.cs calls Build after MelodySelection.Build and writes data.Motifs with the bundle. By default an
// analysis or Verify failure disables the layer (Schema 0, Status saying why: the bundle shows the classic wheels) so one bug
// cannot block the library pipeline; strict callers (--strict-motifs, --motifs-report, the self-tests, the sweep) get the
// exception instead. Nothing here writes a file.
public static class MotifAnalysis
{
    // What one run made, for reports and tests.
    public sealed class Result
    {
        public MotifInput Input;
        public CellLexicon Lexicon;
        public Returns Returns;
        public Iteration Iteration;
        public Hierarchy Tree;
        public MotifCode Code;
        public PreparedPatternSong.MotifLayer Layer;
        public readonly List<(string Stage, double Ms)> Timings = new();
        public double Seconds => Timings.Sum(t => t.Ms) / 1000;
    }
    // A bundle as Program.cs prepares it before the motif stage (the sweep runs the same preparation, writing nothing).
    public sealed record Prepared(PreparedPatternSong Data, MidiCycleAnalysis Cycles, FormAnalysis.Result Analysis, SongSettings Settings, string Folder, int Ppq);

    public static PreparedPatternSong.MotifLayer Build(PreparedPatternSong data, MidiCycleAnalysis cycles, FormAnalysis.Result analysis, SongSettings song, string folder, int ppq, bool strict)
    {
        try
        {
            var result = Run(data, cycles, analysis?.Grid, MotifSettings.FromSong(song, Overrides(folder)), folder, ppq);
            if (FormAnalysis.Verbose) Console.WriteLine("  motif stages: " + Timings(result));
            return result.Layer;
        }
        catch (Exception e) when (!strict)
        {
            return new PreparedPatternSong.MotifLayer { Status = (e is InvalidDataException ? "verify failed: " : $"analysis failed: {e.GetType().Name}: ") + e.Message };
        }
    }

    // song.json's "Motifs" object of parameter overrides (SongSettings does not read it), or null.
    public static JsonElement? Overrides(string folder)
    {
        string path = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "song.json");
        if (path == null || !File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Motifs", out var motifs) ? motifs.Clone() : null;
    }

    // Stages A–G (§2): ingest, reduction and cells, returns (θ and the provisional sections first, which the iteration needs),
    // iteration, hierarchy, then the code, its bits, the lossless gate and the export. Verify throws InvalidDataException.
    public static Result Run(PreparedPatternSong data, MidiCycleAnalysis cycles, ChordTimeline.Grid harmony, MotifSettings settings, string folder, int ppq)
    {
        var r = new Result(); var clock = Stopwatch.StartNew();
        void Lap(string stage) { r.Timings.Add((stage, clock.Elapsed.TotalMilliseconds)); clock.Restart(); }
        r.Input = MotifInput.Read(data, cycles, harmony, settings, folder, ppq); Lap("A ingest");
        r.Lexicon = CellLexicon.Build(r.Input); Lap("B–C cells");
        if (r.Lexicon.DecodeErrors.Length > 0) throw new InvalidDataException($"{r.Lexicon.DecodeErrors.Length} member{(r.Lexicon.DecodeErrors.Length == 1 ? "" : "s")} do not decode: {r.Lexicon.DecodeErrors[0]}");
        r.Returns = Returns.Build(r.Input, r.Lexicon, data); Lap("E returns");
        r.Iteration = Iteration.Build(r.Input, r.Lexicon, r.Returns.Theta, r.Returns.Boundaries); Lap("D iteration");
        r.Tree = Hierarchy.Build(r.Input, r.Lexicon, r.Iteration, r.Returns, data); Lap("F hierarchy");
        r.Code = MotifCode.Encode(r.Input, r.Lexicon, cycles.Notes);
        r.Code.Price(r.Input, r.Lexicon, r.Returns, r.Iteration, r.Tree); Lap("G code");
        MotifVerify.Check(r.Code, cycles.Notes); Lap("G verify");
        r.Layer = r.Code.Export(r.Input, r.Lexicon, r.Returns, r.Iteration, r.Tree); Lap("G export");
        if (r.Input.Warnings.Count > 0) r.Layer.Status = string.Join("; ", r.Input.Warnings.Append(r.Layer.Status).Where(x => x.Length > 0));
        return r;
    }
    public static string Timings(Result r) => string.Join(", ", r.Timings.Select(t => FormattableString.Invariant($"{t.Stage} {t.Ms:0} ms"))) + FormattableString.Invariant($" · {r.Seconds:0.00} s");

    // --motifs-report: the layer as text (caption, formula, tree, relations, paradigms, atoms, stats). A dry run: nothing is
    // written.
    public static void Report(PreparedPatternSong.MotifLayer m, TextWriter w)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string F(double x) => x.ToString("0.###", inv);
        if (m.Schema == 0) { w.WriteLine($"MOTIFS DISABLED: {m.Status}"); return; }
        if (m.Status.Length > 0) w.WriteLine("status: " + m.Status);
        w.WriteLine("caption: " + m.Caption);
        w.WriteLine("formula:");
        foreach (var line in m.Formula.Split('\n')) w.WriteLine("  " + line);
        w.WriteLine("tree:");
        string[] kinds = { "piece", "section", "phrase", "period", "sub-phrase", "power", "unit", "coda", "intro", "arrival" };
        // Depth first (the array lists each node's children together, then their subtrees).
        void Node(int id)
        {
            var n = m.Nodes[id];
            var sb = new StringBuilder(new string(' ', 2 + 2 * n.Depth)).Append(n.Label.Length > 0 ? n.Label : "?").Append($" [{F(n.Start)}, {F(n.End)}) {(n.Kind >= 0 && n.Kind < kinds.Length ? kinds[n.Kind] : n.Kind.ToString(inv))}");
            if (n.Kind == PreparedPatternSong.MotifKinds.Power) sb.Append($" ×{n.Count} step ({n.StepSteps},{n.StepSemitones}) order {n.Order}");
            if (n.Prototype >= 0) sb.Append($" ← {m.Nodes[n.Prototype].Label}");
            if (n.Projected) sb.Append(" projected");
            if (n.Omits.Length > 0) sb.Append($" omits {n.Omits}");
            sb.Append($" · {F(n.Bits)} bits");
            if (n.Text.Length > 0) sb.Append("   ").Append(n.Text);
            w.WriteLine(sb);
            for (int k = 0; k < n.ChildCount; k++) Node(n.FirstChild + k);
        }
        if (m.Nodes.Length > 0) Node(0);
        w.WriteLine("relations:");
        string[] relationKinds = { "Return", "Power", "Run", "Doubling", "Alternation", "Liquidation", "CrossReference" };
        foreach (var r in m.Relations)
        {
            var sb = new StringBuilder($"  {r.Id} {(r.Kind >= 0 && r.Kind < relationKinds.Length ? relationKinds[r.Kind] : r.Kind.ToString(inv))} L{r.Level} [{F(r.Start)}, {F(r.End)})");
            if (r.SourceStart >= 0) sb.Append($" ← [{F(r.SourceStart)}, {F(r.SourceEnd)})");
            sb.Append($" ×{r.Count} ({r.Steps},{r.Semitones}{(r.Real ? " real" : "")}{(r.Inverted ? " mirrored" : "")}{(r.ModeChange ? " P" : "")})");
            if (r.Period > 0 || r.Order > 0) sb.Append($" period {r.Period} order {r.Order}");
            if (r.Symmetry.Length > 1) sb.Append($" sym {{{string.Join(",", r.Symmetry)}}}");
            if (r.Pattern.Length > 0) sb.Append($" \"{r.Pattern}\"");
            if (r.StreamA >= 0) sb.Append($" streams {r.StreamA}{(r.StreamB >= 0 ? "/" + r.StreamB : "")}");
            if (r.Contrary) sb.Append(" contrary");
            if (r.CrossScale) sb.Append(" cross-scale");
            if (r.Node >= 0) sb.Append($" node {m.Nodes[r.Node].Label}");
            sb.Append($" · {F(r.Bits)} bits, saves {F(r.SavedBits)}");
            if (r.Text.Length > 0) sb.Append(" — ").Append(r.Text);
            w.WriteLine(sb);
            for (int e = r.FirstEdit; e >= 0 && e < r.FirstEdit + r.EditCount; e++)
            {
                var x = m.Edits[e]; string op = x.Op switch { 0 => "match", 1 => "delete", 2 => "insert", _ => "substitute" };
                w.WriteLine($"      {op} {(x.SourceStart >= 0 ? $"[{F(x.SourceStart)}, {F(x.SourceEnd)})" : "")}{(x.TargetStart >= 0 ? $" → [{F(x.TargetStart)}, {F(x.TargetEnd)})" : "")}");
            }
        }
        w.WriteLine("paradigms:");
        foreach (var p in m.Paradigms)
        {
            var members = new List<string>();
            for (int c = 0; c < m.Cells.Length; c++)
                if (m.Cells[c].Paradigm == p.Id)
                {
                    var x = m.Cells[c];
                    members.Add(F(x.Start) + (x.Steps != 0 || x.Semitones != 0 ? $"({x.Steps},{x.Semitones}{(x.Real ? "r" : "")})" : "") + (x.Inverted ? "I" : "") + (x.Exact ? "" : "~"));
                }
            w.WriteLine($"  {p.Letter} {p.Name} {(p.Atom >= 0 ? m.Atoms[p.Atom].Kind : "-")} \"{p.Signature}\" sign {p.Sign} stream {p.Stream} clock {p.Clock}: {p.Members} members at {string.Join(" ", members)}");
        }
        w.WriteLine("atoms: " + string.Join("; ", m.Atoms.Select(a => $"{a.Kind} ({a.Name}, \"{a.Signature}\") {a.Paradigms} paradigms, {a.Members} members +{a.Positive} −{a.Negative} ·{a.Neutral}")));
        var s = m.Stats;
        int bytes = JsonSerializer.SerializeToUtf8Bytes(m, new JsonSerializerOptions { IncludeFields = true }).Length;
        w.WriteLine(FormattableString.Invariant($"stats: {s.Notes} notes, {s.Streams} streams, {s.Cells} cells, {s.Paradigms} paradigms, {s.Atoms} atoms, {s.Nodes} nodes, {s.Relations} relations ({s.Powers} powers, {s.Returns} returns), {s.MirroredCells} mirrored cells; the layer is {bytes / 1e6:0.###} MB of JSON"));
        w.WriteLine(FormattableString.Invariant($"bits: literal {s.BitsLiteral:0}, entropy {s.BitsEntropy:0}, cells {s.BitsCells:0}, model {s.BitsModel:0} (residual {s.BitsResidual:0}), articulation {s.BitsArticulation:0}; model/literal {(s.BitsLiteral > 0 ? s.BitsModel / s.BitsLiteral : 0):0.###}, model/entropy {(s.BitsEntropy > 0 ? s.BitsModel / s.BitsEntropy : 0):0.###}, {(s.Notes > 0 ? s.BitsModel / s.Notes : 0):0.##} bits a note; derived {s.DerivedNotes} notes ({100 * s.Derived:0.#}%)"));
    }
}
