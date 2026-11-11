using System.Text.Json;

// Stage A of the motif reduction (Docs/MOTIF-REDUCTION.md §3): the score as notes in integer ticks on the
// measures' tactus grid, joined to a MuseScore import when one lies beside the MIDI, snapped to a grid in
// transcription mode, spelled and keyed (Spelling.cs) and split into streams (MotifStreams.cs). Later stages
// read only what MotifInput.Read builds. The tempo map is never read: everything is in score ticks.

// One note. Tick and Duration are score ticks at the MIDI header's PPQ; Offset is the performed onset minus Tick,
// the timing residual a transcription's snap leaves (0 in a score). Fifths is the spelling on the line of fifths
// (C 0, G 1, F −1), Spelled when it came from the score; Morph the morphetic pitch (7·octave + letter, C4 = 28);
// Alter the alteration against the local key's scale. Hand: 0 upper, 1 lower, −1 drums. Staff and Voice come
// from the import (−1 without). Host: for a grace note, the note it ornaments. NoteIndex: the cycles.Notes entry.
public struct MotifEvent
{
    public long Tick, Duration, Offset;
    public int Pitch, Fifths, Morph, Alter, Track, Channel, Staff, Voice, Hand, Host, NoteIndex;
    public float Velocity;
    public bool Grace, Drum, Spelled;
}

public sealed class MotifInput
{
    public MotifSettings Settings;
    public int Ppq;
    public MotifGrid Grid;
    public MotifEvent[] Events = Array.Empty<MotifEvent>();   // every note, drums and graces too, by (Tick, Pitch, NoteIndex)
    public ScoreImport Import;                                // null without an accepted score-import.json
    public KeyFrames Frames;
    public MotifStreams Streams;
    public int Snapped;                                       // onsets the transcription grid moved
    public readonly List<string> Warnings = new();

    public static long Ticks(double beats, int ppq) => (long)Math.Round(beats * ppq, MidpointRounding.AwayFromZero);

    // folder: where score-import.json would lie (null: none); harmony: FormAnalysis's chord grid (its Bass), may be null.
    public static MotifInput Read(PreparedPatternSong data, MidiCycleAnalysis cycles, ChordTimeline.Grid harmony, MotifSettings settings, string folder, int ppq)
    {
        if (ppq <= 0) throw new ArgumentException("the motif analysis needs the MIDI header's PPQ");
        var input = new MotifInput { Settings = settings, Ppq = ppq };
        var notes = cycles.Notes;
        var ev = new MotifEvent[notes.Count];
        for (int i = 0; i < ev.Length; i++)
        {
            var h = notes[i];
            ev[i] = new MotifEvent { Tick = Ticks(h.Beat, ppq), Duration = Math.Max(1, Ticks(h.Length, ppq)), Pitch = h.Pitch, Track = h.Track, Channel = h.Channel,
                Velocity = h.Velocity, NoteIndex = i, Staff = -1, Voice = -1, Hand = -1, Host = -1, Drum = h.Channel == 10 };
        }
        input.Grid = MotifGrid.Build(cycles.Measures, cycles.EndBeat, ppq, settings.SlotsPerTactus);
        // The import is matched on the score's own ticks, before any snapping.
        input.Import = ScoreImport.TryLoad(folder, cycles, settings.ScoreLeadInBeats, ppq, settings.ImportMatch, out string warning);
        if (warning.Length > 0) input.Warnings.Add(warning);
        if (input.Import != null)
            for (int i = 0; i < ev.Length; i++)
            {
                if (input.Import.Staff[i] < 0) continue;
                ev[i].Staff = input.Import.Staff[i]; ev[i].Voice = input.Import.Voice[i]; ev[i].Grace = input.Import.Grace[i];
                if (input.Import.Spelled[i] && !ev[i].Drum) { ev[i].Fifths = input.Import.Fifths[i]; ev[i].Spelled = true; }
            }
        if (settings.Transcription) input.Snapped = Quantiser.Snap(ev, input.Grid, settings, ppq);
        Array.Sort(ev, (a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Pitch != b.Pitch ? a.Pitch.CompareTo(b.Pitch) : a.NoteIndex.CompareTo(b.NoteIndex));
        input.Events = ev;
        input.Grid.Locate(ev, input.Import);
        MarkGraces(ev, input.Grid);
        input.Frames = KeyFrames.Build(data, harmony, input.Grid);
        Spelling.Spell(ev, input.Grid, input.Frames);
        input.Streams = MotifStreams.Build(input, data, cycles);
        return input;
    }

    // Grace notes never enter streams (§3 A2): each becomes an ornament residual of the note after it. The score marks
    // them. Without a score, a note is one when it lasts at most half a slot and its lane moves on within a slot, to a
    // longer note: a run of at most three such short onsets, a slot apart at most, leading into an onset with a longer
    // note. A longer run is a roll, tremolo or run, not a chain of graces (under the Rach2 Allegro's half-note tactus the
    // 32nd strokes of its timpani rolls would otherwise be 1,150 graces). The host is the next note of the same score
    // voice, else of the lane, the nearest in pitch among simultaneous ones.
    static void MarkGraces(MotifEvent[] ev, MotifGrid grid)
    {
        var lanes = new Dictionary<(int, int), List<int>>(); var order = new List<(int, int)>();   // insertion-ordered: never iterated as a dictionary
        for (int i = 0; i < ev.Length; i++)
        {
            if (ev[i].Drum) continue;
            var key = (ev[i].Track, ev[i].Channel);
            if (!lanes.TryGetValue(key, out var list)) { lanes[key] = list = new List<int>(); order.Add(key); }
            list.Add(i);
        }
        foreach (var key in order)
        {
            var list = lanes[key];
            var first = new List<int>();   // the lane's onsets: list[first[g] .. first[g + 1])
            for (int k = 0; k < list.Count; k++) if (k == 0 || ev[list[k]].Tick != ev[list[k - 1]].Tick) first.Add(k);
            first.Add(list.Count);
            int onsets = first.Count - 1;
            var tick = new long[onsets]; var brief = new bool[onsets];
            for (int g = 0; g < onsets; g++)
            {
                tick[g] = ev[list[first[g]]].Tick; long slot = grid.SlotAt(tick[g]); brief[g] = true;
                for (int k = first[g]; k < first[g + 1]; k++) if (ev[list[k]].Duration * 2 > slot) brief[g] = false;
            }
            for (int g = 0; g < onsets;)
            {
                if (!brief[g]) { g++; continue; }
                int b = g; while (b + 1 < onsets && brief[b + 1] && tick[b + 1] - tick[b] <= grid.SlotAt(tick[b])) b++;
                if (b - g < 3 && b + 1 < onsets && tick[b + 1] - tick[b] <= grid.SlotAt(tick[b]))
                    for (int k = first[g]; k < first[b + 1]; k++) if (ev[list[k]].Staff < 0) ev[list[k]].Grace = true;   // the score's own flags stand
                g = b + 1;
            }
            for (int k = 0; k < list.Count; k++)
            {
                int i = list[k]; if (!ev[i].Grace) continue;
                int host = -1;
                for (int pass = ev[i].Staff >= 0 ? 0 : 1; pass < 2 && host < 0; pass++)
                {
                    long at = -1;
                    for (int j = k + 1; j < list.Count; j++)
                    {
                        var e = ev[list[j]];
                        if (e.Tick <= ev[i].Tick || e.Grace || pass == 0 && (e.Staff != ev[i].Staff || e.Voice != ev[i].Voice)) continue;
                        if (at >= 0 && e.Tick > at) break;
                        if (host < 0 || Math.Abs(e.Pitch - ev[i].Pitch) < Math.Abs(ev[host].Pitch - ev[i].Pitch)) host = list[j];
                        at = e.Tick;
                    }
                }
                ev[i].Host = host;
            }
        }
    }
}

// The measures' tactus grid in ticks (§3 A1). Columns are each measure's tactus windows clipped to it (an irregular
// bar gives a partial column), built as ChordTimeline builds its steps. FirstBar is the first measure with an
// onset: the silent lead-in before it lies outside the analysed span [SpanStart, SpanEnd).
public sealed class MotifGrid
{
    public int Ppq, FirstBar, FirstColumn;
    public long SpanStart, SpanEnd;
    public long[] BarStart = Array.Empty<long>(), BarEnd = Array.Empty<long>(), Unit = Array.Empty<long>(), Slot = Array.Empty<long>();
    public int[] Printed = Array.Empty<int>();                // printed bar numbers (a pickup is bar 0)
    public bool[] Compound = Array.Empty<bool>();             // felt in dotted quarters (6/8, 9/8, 12/8)
    public long[] ColumnStart = Array.Empty<long>(), ColumnEnd = Array.Empty<long>();
    public int[] ColumnBar = Array.Empty<int>();
    public int Bars => BarStart.Length;
    public int Columns => ColumnStart.Length;

    public static MotifGrid Build(IReadOnlyList<MidiCycleAnalysis.Bar> measures, double endBeat, int ppq, int slotsPerTactus)
    {
        int n = measures.Count;
        var g = new MotifGrid { Ppq = ppq, BarStart = new long[n], BarEnd = new long[n], Unit = new long[n], Slot = new long[n], Printed = new int[n], Compound = new bool[n] };
        var starts = new List<long>(); var ends = new List<long>(); var bars = new List<int>();
        for (int b = 0; b < n; b++)
        {
            var m = measures[b];
            long start = MotifInput.Ticks(m.Start, ppq), end = Math.Max(start + 1, MotifInput.Ticks(m.End, ppq)), unit = Math.Max(1, MotifInput.Ticks(ChordTimeline.BeatUnit(m), ppq));
            g.BarStart[b] = start; g.BarEnd[b] = end; g.Unit[b] = unit; g.Slot[b] = Math.Max(1, (long)Math.Round(unit / (double)slotsPerTactus));
            g.Compound[b] = m.Denominator == 8 && m.Numerator % 3 == 0 && m.Numerator > 3;
            g.Printed[b] = b + 1;
            for (long s = start; s < end; s += unit) { starts.Add(s); ends.Add(Math.Min(end, s + unit)); bars.Add(b); }
        }
        g.ColumnStart = starts.ToArray(); g.ColumnEnd = ends.ToArray(); g.ColumnBar = bars.ToArray();
        g.SpanEnd = Math.Max(n > 0 ? g.BarEnd[n - 1] : 0, MotifInput.Ticks(endBeat, ppq));
        return g;
    }

    // The analysed span starts at the first measure with an onset. Printed numbers count from there (§3 A1): an
    // anacrusis (a first measure shorter than the next) is bar 0. A score import's own numbers win where it has them.
    public void Locate(MotifEvent[] events, ScoreImport import)
    {
        if (Bars == 0) return;
        FirstBar = events.Length == 0 ? 0 : Math.Max(0, BarAt(events[0].Tick));
        bool anacrusis = FirstBar + 1 < Bars && BarEnd[FirstBar] - BarStart[FirstBar] < BarEnd[FirstBar + 1] - BarStart[FirstBar + 1];
        for (int m = 0; m < Bars; m++) Printed[m] = m - FirstBar + (anacrusis ? 0 : 1);
        if (import != null)
            for (int k = 0; k < import.MeasureTick.Length; k++)
            {
                int m = BarAt(import.MeasureTick[k]);
                if (m >= FirstBar && Math.Abs(BarStart[m] - import.MeasureTick[k]) <= 1) Printed[m] = import.MeasureNumber[k];
            }
        FirstColumn = Math.Max(0, Array.IndexOf(ColumnBar, FirstBar));
        SpanStart = BarStart[FirstBar];
    }

    // The measure holding tick (the last one for ticks past the end, −1 before the first).
    public int BarAt(long tick)
    {
        if (Bars == 0 || tick < BarStart[0]) return -1;
        int i = Array.BinarySearch(BarStart, tick);
        return i >= 0 ? i : ~i - 1;
    }
    public int ColumnAt(long tick)
    {
        if (Columns == 0 || tick < ColumnStart[0]) return -1;
        int i = Array.BinarySearch(ColumnStart, tick);
        return i >= 0 ? i : ~i - 1;
    }
    public long TactusAt(long tick) => Bars == 0 ? Ppq : Unit[Math.Max(0, BarAt(tick))];
    public long SlotAt(long tick) => Bars == 0 ? Math.Max(1, Ppq / 8) : Slot[Math.Max(0, BarAt(tick))];

    // Metric weight w(τ) (§3 A1): 4 on the downbeat; 3 mid-bar when the bar holds an even number of tactus beats (beat 2
    // in 2/4, beat 3 in 4/4); 2 on other tactus beats; 1 on the tactus's own subdivision (its half in simple time, its
    // thirds in compound time, where the eighths divide a dotted quarter); 0 otherwise.
    public int Weight(long tick)
    {
        int b = BarAt(tick); if (b < 0) return 0;
        long off = tick - BarStart[b], u = Unit[b], length = BarEnd[b] - BarStart[b];
        if (off == 0) return 4;
        if (off % u == 0)
        {
            long beats = length % u == 0 ? length / u : 0;
            return beats >= 2 && beats % 2 == 0 && off == beats / 2 * u ? 3 : 2;
        }
        return off * (Compound[b] ? 3 : 2) % u == 0 ? 1 : 0;
    }
}

// A MuseScore import (score-import.json, format resonance.score-import/1) beside the MIDI (§3 A2). Its notes are
// matched one to one to the MIDI notes by (tick + lead-in, pitch), in (tick, staff, voice) order; when at least
// ImportMatch of the notes match, each matched note takes the score's staff, voice, spelling (tpc), grace flag and
// printed bar, and the tempo, expression and dynamic texts become boundary evidence. The import's ticks are in the
// score's division and are scaled to the MIDI's PPQ (480 against 32,640 for the étude's aligned MIDI).
public sealed class ScoreImport
{
    public sealed class Direction { public long Tick; public int Staff; public string Kind = "", Text = ""; }
    // Keyboard: the staff's part has two or more staves (a piano). Lowest: the last staff of such a part (the left hand).
    public sealed class StaffInfo { public int Index, Part; public string Name = ""; public bool Keyboard, Lowest; }
    public int Division, ImportNotes, MidiNotes, Matched;
    public double Share;
    public int[] Staff = Array.Empty<int>(), Voice = Array.Empty<int>(), Fifths = Array.Empty<int>();   // per cycles.Notes index; Staff −1: unmatched
    public bool[] Grace = Array.Empty<bool>(), Spelled = Array.Empty<bool>();
    public StaffInfo[] Staves = Array.Empty<StaffInfo>();
    public Direction[] Directions = Array.Empty<Direction>();
    public long[] MeasureTick = Array.Empty<long>();
    public int[] MeasureNumber = Array.Empty<int>();
    public StaffInfo StaffOf(int staff) => Array.Find(Staves, s => s.Index == staff);

    public static ScoreImport TryLoad(string folder, MidiCycleAnalysis cycles, double leadInBeats, int ppq, double minimumShare, out string warning)
    {
        warning = "";
        string path = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "score-import.json");
        if (path == null || !File.Exists(path)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException e) { warning = "score-import.json ignored: " + e.Message; return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != "resonance.score-import/1")
            { warning = "score-import.json ignored: not resonance.score-import/1"; return null; }
            int division = Int(root, "division", 0); if (division <= 0) division = ppq;
            double scale = ppq / (double)division, lead = leadInBeats * division;
            // As the importer writes its MIDI: score ticks scaled and rounded half to even.
            long Tick(double scoreTick) => (long)Math.Round((scoreTick + lead) * scale, MidpointRounding.ToEven);
            var import = new ScoreImport { Division = division };
            var parts = new Dictionary<int, int>();   // part index → its staff count (lookups only)
            if (root.TryGetProperty("parts", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray()) parts[Int(p, "index", -1)] = p.TryGetProperty("staves", out var st) && st.ValueKind == JsonValueKind.Array ? st.GetArrayLength() : 1;
            var staves = new List<StaffInfo>();
            if (root.TryGetProperty("staves", out var ss) && ss.ValueKind == JsonValueKind.Array)
                foreach (var s in ss.EnumerateArray()) staves.Add(new StaffInfo { Index = Int(s, "index", staves.Count), Part = Int(s, "part", 0), Name = Str(s, "name") });
            foreach (var s in staves)
            {
                s.Keyboard = parts.TryGetValue(s.Part, out int count) && count >= 2;
                s.Lowest = s.Keyboard && staves.TrueForAll(o => o.Part != s.Part || o.Index <= s.Index);
            }
            import.Staves = staves.ToArray();
            var directions = new List<Direction>();
            if (root.TryGetProperty("directions", out var ds) && ds.ValueKind == JsonValueKind.Array)
                foreach (var d in ds.EnumerateArray()) directions.Add(new Direction { Tick = Tick(Num(d, "tick")), Staff = Int(d, "staff", -1), Kind = Str(d, "kind"), Text = Str(d, "text") });
            import.Directions = directions.ToArray();
            var measureTick = new List<long>(); var measureNumber = new List<int>();
            if (root.TryGetProperty("measures", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray()) { measureTick.Add(Tick(Num(m, "tick"))); measureNumber.Add(Int(m, "number", measureNumber.Count)); }
            import.MeasureTick = measureTick.ToArray(); import.MeasureNumber = measureNumber.ToArray();

            var wanted = new List<(long tick, long length, int staff, int voice, int pitch, int fifths, bool spelled, bool grace, int id)>();
            if (root.TryGetProperty("notes", out var ns) && ns.ValueKind == JsonValueKind.Array)
                foreach (var x in ns.EnumerateArray())
                {
                    bool spelled = x.TryGetProperty("tpc", out var tpc) && tpc.ValueKind == JsonValueKind.Number;
                    // MuseScore's tpc puts C at 14 on the line of fifths; here C is 0.
                    wanted.Add((Tick(Num(x, "tick")), (long)Math.Round(Num(x, "ticks") * scale), Int(x, "staff", -1), Int(x, "voice", 0), Int(x, "pitch", -1),
                        spelled ? tpc.GetInt32() - 14 : 0, spelled, x.TryGetProperty("grace", out var g) && g.ValueKind == JsonValueKind.True, wanted.Count));
                }
            wanted.Sort((a, b) => a.tick != b.tick ? a.tick.CompareTo(b.tick) : a.staff != b.staff ? a.staff.CompareTo(b.staff) : a.voice != b.voice ? a.voice.CompareTo(b.voice) : a.id.CompareTo(b.id));

            var midi = cycles.Notes; int n = midi.Count;
            import.Staff = new int[n]; import.Voice = new int[n]; import.Fifths = new int[n]; import.Grace = new bool[n]; import.Spelled = new bool[n];
            Array.Fill(import.Staff, -1); Array.Fill(import.Voice, -1);
            var at = new Dictionary<(long, int), List<int>>();   // (tick, pitch) → MIDI notes, lookups only
            for (int i = 0; i < n; i++)
            {
                var key = (MotifInput.Ticks(midi[i].Beat, ppq), midi[i].Pitch);
                if (!at.TryGetValue(key, out var list)) at[key] = list = new List<int>();
                list.Add(i);
            }
            var used = new bool[n];
            foreach (var w in wanted)
            {
                // Same tick and pitch (a tick either way absorbs rounding); among unison doubles in two voices, the
                // MIDI note whose length is nearest the written one.
                int best = -1; long gap = long.MaxValue;
                foreach (long dt in new long[] { 0, -1, 1 })
                {
                    if (!at.TryGetValue((w.tick + dt, w.pitch), out var list)) continue;
                    foreach (int i in list)
                    {
                        if (used[i]) continue;
                        long d = Math.Abs(MotifInput.Ticks(midi[i].Length, ppq) - w.length);
                        if (d < gap) { best = i; gap = d; }
                    }
                    if (best >= 0) break;
                }
                if (best < 0) continue;
                used[best] = true; import.Matched++;
                import.Staff[best] = Math.Max(0, w.staff); import.Voice[best] = Math.Max(0, w.voice); import.Fifths[best] = w.fifths; import.Spelled[best] = w.spelled; import.Grace[best] = w.grace;
            }
            import.ImportNotes = wanted.Count; import.MidiNotes = n;
            import.Share = import.Matched / (double)Math.Max(1, Math.Max(wanted.Count, n));
            if (import.Share < minimumShare)
            {
                warning = $"score-import.json ignored: {import.Matched} of {Math.Max(wanted.Count, n)} notes match the MIDI ({import.Share:P1}, below {minimumShare:P0})";
                return null;
            }
            return import;
        }
    }
    static int Int(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (v.TryGetInt32(out int i) ? i : (int)Math.Round(v.GetDouble())) : fallback;
    static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

// Transcription mode (§3 A3): per bar, the grid among U/4, U/3, U/6, U/8 and U/12 whose snapping error, plus a price
// per grid point, is least; an onset snaps when it lies within max(tolerance, 2σ̂) of its grid point, σ̂ the robust
// spread (1.4826 × the median distance) over the piece. The exact offset stays on the event as its timing residual.
public static class Quantiser
{
    public static int Snap(MotifEvent[] ev, MotifGrid grid, MotifSettings settings, int ppq)
    {
        int n = ev.Length; if (n == 0 || grid.Bars == 0) return 0;
        var target = new long[n]; var error = new double[n];
        var byBar = new List<int>[grid.Bars];
        for (int i = 0; i < n; i++) { int b = Math.Max(0, grid.BarAt(ev[i].Tick)); (byBar[b] ??= new List<int>()).Add(i); }
        for (int b = 0; b < grid.Bars; b++)
        {
            var list = byBar[b]; if (list == null) continue;
            long start = grid.BarStart[b]; double length = grid.BarEnd[b] - start, unit = grid.Unit[b];
            double best = double.PositiveInfinity, step = unit;
            foreach (int d in settings.GridDivisions)
            {
                double g = unit / d, cost = settings.GridPointCost * ppq * (length / g);
                foreach (int i in list) { double off = ev[i].Tick - start; cost += Math.Abs(off - Math.Round(off / g) * g); }
                if (cost < best - 1e-9) { best = cost; step = g; }
            }
            foreach (int i in list)
            {
                double off = ev[i].Tick - start, on = Math.Round(off / step) * step;
                target[i] = start + (long)Math.Round(on); error[i] = Math.Abs(off - on);
            }
        }
        var sorted = (double[])error.Clone(); Array.Sort(sorted);
        double median = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
        double limit = Math.Max(settings.TimingToleranceBeats * ppq, settings.SnapSigmas * 1.4826 * median) + 1e-9;
        int moved = 0;
        for (int i = 0; i < n; i++)
            if (error[i] <= limit && target[i] != ev[i].Tick) { ev[i].Offset = ev[i].Tick - target[i]; ev[i].Tick = target[i]; moved++; }
        return moved;
    }
}
