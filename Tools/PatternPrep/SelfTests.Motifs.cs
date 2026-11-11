using System.Text.Json;

// Motif reduction fixtures (Docs/MOTIF-REDUCTION.md §16; PatternPrep --self-test-motifs, and --self-test runs them too).
// Every fixture is a SelfTests.Score at 120 bpm and 480 ticks per quarter, C major unless stated, with the melody on
// track 3 channel 3, the left hand or bass on track 2 channel 2, chords on track 1 channel 1 and drums on track 4
// channel 10. Each implementation step of §17 adds its fixtures here.
public static partial class SelfTests
{
    static readonly string[] MotifTrackNames = { "", "Chords", "Bass", "Melody", "Drums" };
    const int MotifPpq = 480;

    public static void RunMotifs(bool verbose = false)
    {
        MotifContract();
        MotifSettingsOverrides();
        MotifIngest(verbose);
        MotifSpelling();
        MotifGroup(verbose);
        MotifReduction(verbose);
        MotifCells(verbose);
        MotifIteration(verbose);
        MotifReturns(verbose);
        MotifHierarchy(verbose);
        Console.WriteLine("PASS: motif contract (F24) — a version 6 layer round-trips with the §13.1 field names, HasMotifs is not written, a version 5 bundle reads as Schema 0");
        Console.WriteLine("PASS: motif ingest — tick grid, metric weights and printed bars after a silent lead-in, score import (scaled ticks, staves, voices, tpc, graces, directions), transcription snap, skyline and voice streams, principal stream; B♭ minor's leading tone is A (F28)");
        Console.WriteLine("PASS: motif transform group — costs, composition, images, stabilisers and orders; names: tonal sequence up a second (F3), the leading tone of A minor (F4), the parallel minor (F5)");
        Console.WriteLine("PASS: motif reduction (F25) — diminution tokens E4[N− Rep] F4[N+ Rep Rep], appoggiaturas and turns; arrivals at bars 4 (half), 8, 12 (half), 16 cut a period into 4 + 4 + 4 + 4, never at a decorated held note");
        Console.WriteLine("PASS: motif cells (F1–F6, F14, F18, F26, F27) — neighbour chirality, exact mirror, tonal and tonicizing sequences, parallel minor, modal mixture, ornamented and lengthened-head variants, distinct figures, phase on beat 2; every member decodes from its prototype");
        Console.WriteLine("PASS: motif iteration (F7–F10, F15–F17, F20) — tritone and diminished-seventh cycles, an alternation, doubling with contrary motion, liquidation, a nested power and a two-cell unit power, drum loops; F3's sequence as a power; the near relation extends an exact sequence");
        Console.WriteLine("PASS: motif returns (F11–F13 at level 1, F19, F21) — the étude-shaped ternary is A B A′ with A′ = A − (a2 a1′) + coda and a1′ ≅ a1 inside A; a head-deleted reprise; the omission stays a2 + a1′ when a1′ equals a1 and under transcription noise; pop sections verbatim with (V2 C2) ← (V1 C1) and C3 ← C1 a whole tone up");
        Console.WriteLine("PASS: motif hierarchy (F11 in full, F12, F13) — phrases a1 a2 a1′ a3 with a3 cut at the sigh head, periods B1 … with B1 = (U²)² ↑1, A′ = a1 · a3′ · Coda omitting a2 and a1′ (also when a1′ equals a1), a head-deleted reprise omitting x; bar 16 = bar 6 a sixth higher; pop copies inherit their phrases; the formula and caption lines");
    }

    // Line(score, startBeat, "C4/8 B3/16 C4/16 D4/16*5 …"): a monophonic line, note after note. A token is
    // pitch/denominator, with *n for n such units (D4/16*5 lasts five sixteenths) and a final dot for a dotted value
    // (D5/4.); "r" rests and "E5+C5/8" strikes a dyad. Returns the beat after the last note.
    static double Line(Score score, double beat, string notes, int track = 3, int channel = 3, float velocity = 96 / 127f)
    {
        foreach (string token in notes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int slash = token.LastIndexOf('/'); if (slash <= 0) throw new ArgumentException("Line: pitch/denominator, not " + token);
            string rhythm = token[(slash + 1)..]; int times = 1, star = rhythm.IndexOf('*');
            bool dotted = rhythm.EndsWith('.'); if (dotted) rhythm = rhythm[..^1];
            if (star >= 0) { times = int.Parse(rhythm[(star + 1)..]); rhythm = rhythm[..star]; }
            double length = 4.0 / int.Parse(rhythm) * times * (dotted ? 1.5 : 1);
            if (token[..slash] != "r") foreach (string pitch in token[..slash].Split('+')) score.Note(beat, length, ScorePitch(pitch), velocity, channel, track);
            beat += length;
        }
        return beat;
    }
    // Scientific pitch (C4 = 60) with any number of #, ♯, b or ♭ after the letter.
    static int ScorePitch(string name)
    {
        int pc = "C D EF G A B".IndexOf(char.ToUpperInvariant(name[0])), i = 1, alter = 0;
        if (pc < 0) throw new ArgumentException("not a pitch: " + name);
        for (; i < name.Length && name[i] is '#' or '♯' or 'b' or '♭'; i++) alter += name[i] is '#' or '♯' ? 1 : -1;
        return 12 * (int.Parse(name[i..]) + 1) + pc + alter;
    }
    static void Bars(Score score, int count, int beats = 4) { for (int i = 0; i < count; i++) { score.Bar(beats); score.Beat += beats; } }
    // A chord step on bundle beats, its root named (A-based like ChordTimeline: A = 0, C = 3).
    static SongFormAnalysis.ChordStep Step(double start, double end, string root, string quality = "") =>
        new() { Start = start, End = end, Root = HarmonyModel.Mod(ScorePitch(root + "4") - 21), Quality = quality };
    static void Block(Score score, double beat, double length, string pitches, string bass)
    {
        foreach (string p in pitches.Split(' ')) score.Note(beat, length, ScorePitch(p), 64 / 127f, 1, 1);
        score.Note(beat, length, ScorePitch(bass), 80 / 127f, 2, 2);
    }

    static MotifInput Ingest(Score score, int key = 3, bool minor = false, SongFormAnalysis.ChordStep[] chords = null, SongSettings song = null, string folder = null, string[] names = null) =>
        Ingest(score, out _, key, minor, chords, song, folder, names);
    static MotifInput Ingest(Score score, out PreparedPatternSong data, int key = 3, bool minor = false, SongFormAnalysis.ChordStep[] chords = null, SongSettings song = null, string folder = null, string[] names = null)
    {
        var cycles = score.Cycles();
        var harmony = ChordTimeline.Build(cycles, key, minor);
        data = new PreparedPatternSong
        {
            Notes = cycles.Notes.ToArray(), Measures = cycles.Measures.ToArray(), EndBeat = score.Beat, TrackNames = names ?? MotifTrackNames, Key = key, Minor = minor, LeadVocalTrack = -1,
            Tempos = new[] { new PreparedPatternSong.Tempo { Beat = 0, Seconds = 0, Microseconds = 500000 } }, Chords = chords ?? ChordTimeline.Steps(harmony).ToArray()
        };
        MelodySelection.Build(data);
        return MotifInput.Read(data, cycles, harmony, MotifSettings.FromSong(song ?? new SongSettings()), folder, MotifPpq);
    }
    // The leads of a stream in [from, to) beats, as a cell starting at `from`.
    static (CellNote[] notes, long start) Cell(MotifInput input, int stream, double from, double to)
    {
        long start = MotifInput.Ticks(from, input.Ppq), end = MotifInput.Ticks(to, input.Ppq);
        var s = input.Streams[stream]; var leads = new List<int>();
        for (int k = 0; k < s.Events; k++) if (s.Onset[k] >= start && s.Onset[k] < end) leads.Add(s.Lead[k]);
        return (TransformGroup.Cell(input.Events, leads, start, input.Frames), start);
    }
    static Placement Relate(MotifInput input, double a, double b, double length)
    {
        var (x, xs) = Cell(input, input.Streams.Principal, a, a + length); var (y, ys) = Cell(input, input.Streams.Principal, b, b + length);
        return TransformGroup.Relate(x, y, xs, ys, input.Frames);
    }

    // F24: the bundle contract. A populated layer survives System.Text.Json unchanged with exactly the §13.1 field names,
    // HasMotifs() is not written, Version stays the bundle's first key, and a version-5 bundle reads as an empty layer.
    static void MotifContract()
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        string json = JsonSerializer.Serialize(SampleLayer(), options);
        Check(JsonSerializer.Serialize(JsonSerializer.Deserialize<PreparedPatternSong.MotifLayer>(json, options), options) == json, "F24: a populated motif layer round-trips through System.Text.Json unchanged");
        using (var doc = JsonDocument.Parse(json))
            foreach (var (path, names) in new[]
            {
                ("", "Schema,Status,Formula,Caption,Levels,PrincipalStream,Streams,Atoms,Paradigms,Nodes,Cells,Sparks,Relations,Edits,Stats"),
                ("Streams", "Id,Track,Channel,Staff,Voice,FirstCell,CellCount,Name,Kind,Bass"),
                ("Atoms", "Id,Paradigms,Members,Positive,Negative,Neutral,Kind,Name,Signature"),
                ("Paradigms", "Id,Atom,Stream,Members,Mirrored,Variants,Clock,Sign,PrototypeCell,Letter,Name,Signature,Beats,Achiral,Notes,Symmetry"),
                ("Paradigms.Notes", "Onset,Length,Step,Semitones,Alter,Chord,Tied,Role"),
                ("Nodes", "Id,Parent,FirstChild,ChildCount,Depth,Level,Kind,Start,End,Label,Name,Text,Omits,Prototype,Relation,Paradigm,Steps,Semitones,Inverted,Real,Projected,Count,Iteration,StepSteps,StepSemitones,Order,Bits,Evidence"),
                ("Cells", "Stream,Paradigm,Node,Steps,Semitones,Degree,FirstSpark,SparkCount,Relation,Start,End,Inverted,Exact,Real,ModeChange,Scale,Text"),
                ("Sparks", "Beat,Cell,Kind,Pitch,Note,Tag"),
                ("Relations", "Id,Kind,Level,Node,Source,StreamA,StreamB,Steps,Semitones,Count,Period,Order,FirstEdit,EditCount,Start,End,SourceStart,SourceEnd,Inverted,Real,ModeChange,CrossScale,Contrary,Bits,SavedBits,Text,Pattern,Symmetry"),
                ("Edits", "Op,SourceStart,SourceEnd,TargetStart,TargetEnd,Bits"),
                ("Stats", "Notes,Streams,Cells,Paradigms,Atoms,Nodes,Relations,Powers,Returns,MirroredCells,DerivedNotes,BitsLiteral,BitsEntropy,BitsCells,BitsModel,BitsResidual,BitsArticulation,Derived"),
            })
            {
                var e = doc.RootElement;
                foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries)) { e = e.GetProperty(part); if (e.ValueKind == JsonValueKind.Array) e = e[0]; }
                string got = string.Join(",", e.EnumerateObject().Select(p => p.Name));
                Check(got == names, $"F24: {(path.Length == 0 ? "MotifLayer" : path)} fields are §13.1's: {got}");
            }
        Check(PreparedPatternSong.MotifKinds.Arrival == 9 && PreparedPatternSong.MotifRelations.CrossReference == 6 && PreparedPatternSong.MotifSparks.Ornament == 7 && PreparedPatternSong.MotifEdits.Substitute == 3, "F24: the kind constants of §13.1");
        string bundle = JsonSerializer.Serialize(new PreparedPatternSong { TrackCount = 4, Motifs = SampleLayer() }, options);
        Check(bundle.StartsWith("{\"Version\":6,") && bundle.Contains("\"Motifs\":{\"Schema\":1,") && !bundle.Contains("HasMotifs"), "F24: a v6 bundle starts with its Version, carries the layer and never writes HasMotifs");
        var read = JsonSerializer.Deserialize<PreparedPatternSong>(bundle, options);
        Check(read.HasMotifs() && JsonSerializer.Serialize(read.Motifs, options) == json && JsonSerializer.Serialize(read, options) == bundle, "F24: a v6 bundle round-trips with its layer");
        var old = JsonSerializer.Deserialize<PreparedPatternSong>("{\"Version\":5,\"TrackCount\":2,\"LeadVocalTrack\":-1,\"Style\":\"classical\",\"Notes\":[{\"Beat\":0,\"Length\":1,\"Pitch\":60,\"Track\":1,\"Channel\":1,\"Velocity\":0.5}]}", options);
        var m = old.Motifs;
        Check(old.Version == 5 && old.Version >= PreparedPatternSong.MinimumReadableVersion && PreparedPatternSong.CurrentVersion == 6 && m != null && m.Schema == 0 && m.Status == "" && m.Formula == "" && m.PrincipalStream == -1 && m.Stats != null
            && m.Levels.Length + m.Streams.Length + m.Atoms.Length + m.Paradigms.Length + m.Nodes.Length + m.Cells.Length + m.Sparks.Length + m.Relations.Length + m.Edits.Length == 0 && !old.HasMotifs(), "F24: a version 5 bundle without \"Motifs\" reads as Schema 0 with empty arrays");
    }
    // Every field set away from its default, so a field the serializer dropped would show.
    static PreparedPatternSong.MotifLayer SampleLayer() => new()
    {
        Schema = 1, Status = "ok", Formula = "Étude = A · B · A′    A′ = A − (a2 a1′) + Coda\nA = a1 · a2 · a1′ · a3", Caption = "A · B · A′ (A′ = A − bars 6–13 + coda) · 12 notes → 2 cells",
        Levels = new[] { "Piece", "Section", "Phrase", "Unit" }, PrincipalStream = 0,
        Streams = new[] { new PreparedPatternSong.MotifStream { Id = 1, Track = 3, Channel = 2, Staff = 0, Voice = 1, FirstCell = 2, CellCount = 5, Name = "Right hand · voice 1", Kind = "voice", Bass = true } },
        Atoms = new[] { new PreparedPatternSong.MotifAtom { Id = 1, Paradigms = 2, Members = 11, Positive = 9, Negative = 2, Neutral = 1, Kind = "N", Name = "neighbour figure", Signature = "N Rep" } },
        Paradigms = new[]
        {
            new PreparedPatternSong.MotifParadigm
            {
                Id = 1, Atom = 0, Stream = 2, Members = 9, Mirrored = 1, Variants = 2, Clock = 12, Sign = -1, PrototypeCell = 3, Letter = "α", Name = "upper neighbour", Signature = "N", Beats = 2.5, Achiral = true,
                Notes = new[] { new PreparedPatternSong.MotifNote { Onset = .75, Length = .25, Step = 1, Semitones = 2, Alter = -1, Chord = 1, Tied = true, Role = "N+" } }, Symmetry = new[] { 0, 6 }
            }
        },
        Nodes = new[]
        {
            new PreparedPatternSong.MotifNode { Id = 0, Parent = -1, FirstChild = 1, ChildCount = 1, Kind = PreparedPatternSong.MotifKinds.Piece, Start = 4, End = 158.5, Label = "Étude", Name = "piece", Text = "A · B · A′" },
            new PreparedPatternSong.MotifNode
            {
                Id = 1, Parent = 0, FirstChild = 2, ChildCount = 3, Depth = 1, Level = 1, Kind = PreparedPatternSong.MotifKinds.Section, Start = 126.5, End = 158.5, Label = "A′", Name = "reprise", Text = "A′ = A − (a2 a1′) + Coda", Omits = "a2, a1′",
                Prototype = 0, Relation = 0, Paradigm = 1, Steps = 1, Semitones = 2, Inverted = true, Real = true, Projected = true, Count = 2, Iteration = 1, StepSteps = -2, StepSemitones = -3, Order = 6, Bits = 12.5f, Evidence = .9f
            }
        },
        Cells = new[] { new PreparedPatternSong.MotifCell { Stream = 1, Paradigm = 1, Node = 1, Steps = 1, Semitones = 2, Degree = 3, FirstSpark = 0, SparkCount = 1, Relation = 0, Start = 5.5, End = 7.5, Inverted = true, Exact = false, Real = true, ModeChange = true, Scale = .5f, Text = "tonal sequence up a second" } },
        Sparks = new[] { new PreparedPatternSong.MotifSpark { Beat = 148.75, Cell = 0, Kind = PreparedPatternSong.MotifSparks.Altered, Pitch = 60, Note = 2, Tag = "C♮ = ♭6 from E minor" } },
        Relations = new[]
        {
            new PreparedPatternSong.MotifRelation
            {
                Id = 1, Kind = PreparedPatternSong.MotifRelations.Doubling, Level = 1, Node = 1, Source = 0, StreamA = 0, StreamB = 1, Steps = -7, Semitones = -12, Count = 2, Period = 6, Order = 6, FirstEdit = 0, EditCount = 1,
                Start = 126.5, End = 158.5, SourceStart = 4.5, SourceEnd = 42.5, Inverted = true, Real = true, ModeChange = true, CrossScale = true, Contrary = true, Bits = 33.25f, SavedBits = 200.5f, Text = "A′ restates A from bar 1", Pattern = "-24,-12", Symmetry = new[] { 0, 6 }
            }
        },
        Edits = new[] { new PreparedPatternSong.MotifEdit { Op = PreparedPatternSong.MotifEdits.Delete, SourceStart = 14.5, SourceEnd = 30.5, TargetStart = 1, TargetEnd = 2, Bits = 7.75f } },
        Stats = new PreparedPatternSong.MotifStats
        {
            Notes = 1932, Streams = 8, Cells = 24, Paradigms = 9, Atoms = 5, Nodes = 30, Relations = 12, Powers = 6, Returns = 1, MirroredCells = 3, DerivedNotes = 1400,
            BitsLiteral = 35900, BitsEntropy = 14126, BitsCells = 13886, BitsModel = 8677, BitsResidual = 512.5f, BitsArticulation = 2048.25f, Derived = .74f
        },
    };

    static void MotifSettingsOverrides()
    {
        var song = new SongSettings { PatternTimingToleranceBeats = .125, ScoreLeadInBeats = 4 };
        var plain = MotifSettings.FromSong(song);
        Check(plain.SlotsPerTactus == 8 && plain.EmRounds == 4 && plain.ThetaMin == .45 && plain.ThetaMax == .9 && plain.MaxCells == 40000 && plain.PeriodBars.SequenceEqual(new[] { 2, 4, 8, 12, 16 }) && plain.Transcription && plain.ScoreLeadInBeats == 4,
            "motif settings: the §10 defaults, and the song's transcription tolerance and lead-in");
        using (var doc = JsonDocument.Parse("{\"Motifs\":{\"emRounds\":3,\"WindowBars\":[1,2]}}"))
        {
            var tuned = MotifSettings.FromSong(song, doc.RootElement.GetProperty("Motifs"));
            Check(tuned.EmRounds == 3 && tuned.WindowBars.SequenceEqual(new[] { 1.0, 2.0 }) && tuned.ThetaMax == .9 && tuned.TimingToleranceBeats == .125, "motif settings: song.json overrides one parameter and keeps the rest");
        }
        bool Refused(string json)
        {
            using var doc = JsonDocument.Parse(json);
            try { MotifSettings.FromSong(song, doc.RootElement); return false; } catch (ArgumentException) { return true; }
        }
        Check(Refused("{\"EmRuonds\":3}") && Refused("{\"TimingToleranceBeats\":1}") && Refused("[1]") && Refused("{\"SlotsPerTactus\":0}"), "motif settings: unknown, song-owned and impossible overrides are refused");
    }

    // Stage A on a small piano piece: a silent 4/4 lead-in, a one-beat pickup, then 2/4. The melody has a MIDI grace
    // note, a held note over an inner one, and a legato overlap; the bass lane lies low.
    static Score IngestScore()
    {
        var s = new Score();
        s.Bar(4); s.Beat += 4; s.Bar(1); s.Beat += 1; Bars(s, 4, 2);
        s.Note(4, 1, ScorePitch("G4"), 96 / 127f, 3, 3);
        Line(s, 5, "C5/8 D5/16 E5/16 F5/4");
        s.Note(7, 1.9375, ScorePitch("E5"), 96 / 127f, 3, 3); s.Note(8.9375, .0625, ScorePitch("A#4"), 96 / 127f, 3, 3);   // a grace: 30 ticks, the next note a slot later
        s.Note(9, .5, ScorePitch("B4"), 96 / 127f, 3, 3); s.Note(9.5, 1.5, ScorePitch("E5"), 96 / 127f, 3, 3); s.Note(10, .5, ScorePitch("C5"), 96 / 127f, 3, 3);   // C5 under the held E5
        s.Note(11, .52, ScorePitch("D5"), 96 / 127f, 3, 3); s.Note(11.5, 1.5, ScorePitch("C5"), 96 / 127f, 3, 3);   // D5 released 10 ticks late
        foreach (var (beat, bass) in new[] { (5.0, "C3"), (7.0, "G2"), (9.0, "C3"), (11.0, "G2") }) s.Note(beat, 2, ScorePitch(bass), 80 / 127f, 2, 2);
        return s;
    }

    static void MotifIngest(bool verbose)
    {
        var input = Ingest(IngestScore());
        var g = input.Grid; long q = MotifPpq;
        Check(g.Bars == 6 && g.FirstBar == 1 && g.Printed[1] == 0 && g.Printed[2] == 1 && g.Printed[5] == 4 && g.SpanStart == 4 * q && g.SpanEnd == 13 * q, "motif grid: the silent lead-in is outside the span and the pickup is bar 0: " + string.Join(",", g.Printed));
        Check(g.Columns == 4 + 1 + 8 && g.FirstColumn == 4 && g.ColumnStart[4] == 4 * q && g.ColumnEnd[4] == 5 * q && g.Slot[2] == 60 && g.Unit[1] == q, "motif grid: tactus columns clipped to each bar, slots of a 32nd");
        int W(double beat) => g.Weight(MotifInput.Ticks(beat, MotifPpq));
        Check(W(5) == 4 && W(6) == 3 && W(5.5) == 1 && W(5.25) == 0 && W(0) == 4 && W(2) == 3 && W(1) == 2 && W(3) == 2 && W(4) == 4 && W(4.5) == 1, "motif grid: metric weights 4 downbeat, 3 mid-bar, 2 beat, 1 half-beat, 0 otherwise");
        var compound = MotifGrid.Build(new[] { new MidiCycleAnalysis.Bar { Start = 0, End = 3, Numerator = 6, Denominator = 8 } }, 3, MotifPpq, 8);
        Check(compound.Unit[0] == 720 && compound.Weight(0) == 4 && compound.Weight(720) == 3 && compound.Weight(240) == 1 && compound.Weight(480) == 1 && compound.Weight(360) == 0, "motif grid: in 6/8 the tactus is a dotted quarter and the eighths divide it");

        var ev = input.Events;
        int At(double beat, string pitch) => Array.FindIndex(ev, e => e.Tick == MotifInput.Ticks(beat, MotifPpq) && e.Pitch == ScorePitch(pitch));
        int grace = At(8.9375, "A#4");
        Check(grace >= 0 && ev[grace].Grace && ev[grace].Host == At(9, "B4") && ev.Count(e => e.Grace) == 1, "motif ingest: a 30-tick note a slot before the next is a grace, ornamenting it");
        var roll = new Score(); Bars(roll, 2);
        for (int k = 0; k < 8; k++) roll.Note(k * .125, .0625, 45, 96 / 127f, 3, 3);
        roll.Note(1, 1, 45, 96 / 127f, 3, 3); roll.Note(2.875, .0625, ScorePitch("D4"), 96 / 127f, 3, 3); roll.Note(2.9375, .0625, ScorePitch("D#4"), 96 / 127f, 3, 3); roll.Note(3, 1, ScorePitch("E4"), 96 / 127f, 3, 3);
        var rolled = Ingest(roll).Events;
        Check(rolled.Count(e => e.Grace) == 2 && rolled.Where(e => e.Grace).All(e => e.Host >= 0 && rolled[e.Host].Pitch == ScorePitch("E4")), "motif ingest: an eight-stroke roll keeps its notes, a two-note slide into E4 ornaments it");
        var streams = input.Streams.All;
        var melody = streams.Single(s => s.Kind == MotifStreams.Line && s.Track == 3); var inner = streams.Single(s => s.Kind == MotifStreams.Rest && s.Track == 3);
        var bassLine = streams.Single(s => s.Kind == MotifStreams.Line && s.Track == 2);
        double[] beats = melody.Onset.Select(t => t / (double)MotifPpq).ToArray();
        Check(beats.SequenceEqual(new[] { 4, 5, 5.5, 5.75, 6, 7, 9, 9.5, 11, 11.5 }) && inner.Events == 1 && inner.Onset[0] == 10 * q, "motif streams: the skyline keeps the held E5 over C5 and lets a note released ten ticks late go: " + string.Join(" ", beats));
        Check(bassLine.Bass && bassLine.Events == 4 && !melody.Bass && ev.Where(e => e.Track == 2).All(e => e.Hand == 1) && ev.Where(e => e.Track == 3).All(e => e.Hand == 0), "motif streams: the bass lane is low and its hand the lower one");
        Check(streams.Count(s => s.Kind == MotifStreams.Texture) == 2 && !streams.Any(s => s.Kind is MotifStreams.Both or MotifStreams.Voice) && input.Streams.Principal == melody.Id, "motif streams: a texture per lane, no hands without a piano, the melody's line is principal");
        Check(ev.Where(e => !e.Drum).All(e => Spelling.Morph(e.Pitch, e.Fifths) == e.Morph) && ev[At(5, "C5")].Morph == 35 && ev[At(4, "G4")].Morph == 32 && ev[grace].Fifths == -2 && ev[grace].Morph == 34,
            "motif ingest: C5 is morph 35, and the speller writes the chromatic grace as B♭4 (nearest the C major centre)");

        // The same piece with a MuseScore import beside it, at half the MIDI's resolution and starting at the pickup.
        string folder = Path.Combine(Path.GetTempPath(), "PatternPrepMotifs"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "score-import.json"), ImportJson(input, 240, matched: true));
        var song = new SongSettings { ScoreLeadInBeats = 4 };
        var scored = Ingest(IngestScore(), song: song, folder: folder);
        var im = scored.Import;
        Check(im != null && im.Matched == 16 && im.Share == 1 && scored.Warnings.Count == 0, "score import: every note matches at the scaled ticks: " + (im == null ? string.Join("; ", scored.Warnings) : im.Matched.ToString()));
        Check(im.Directions.Length == 1 && im.Directions[0].Tick == 7 * q && im.Directions[0].Text == "a tempo" && scored.Grid.Printed[5] == 9 && scored.Grid.Printed[2] == 1, "score import: directions move to MIDI ticks and the score's printed numbers win");
        var se = scored.Events;
        Check(se.All(e => e.Staff >= 0 && e.Spelled) && se.Single(e => e.Grace).Pitch == ScorePitch("A#4") && se.Single(e => e.Tick == 10 * q).Voice == 1 && se.Where(e => e.Track == 2).All(e => e.Staff == 1 && e.Hand == 1)
            && se.Single(e => e.Grace).Fifths == 10 && se.Single(e => e.Grace).Morph == 33, "score import: staff, voice, grace flag and spelling (the score writes A♯4 where the speller wrote B♭4)");
        var voices = scored.Streams.All;
        var upper = voices.Single(s => s.Kind == MotifStreams.Voice);
        Check(upper.Staff == 0 && upper.Voice == 0 && upper.Events == 10 && voices.Count(s => s.Kind == MotifStreams.Rest) == 2 && voices.Any(s => s.Kind == MotifStreams.Both) && scored.Streams.Principal == upper.Id,
            "score import: the upper voice is a stream (the inner note and the four bass notes are too few) and principal; both hands form one texture");
        File.WriteAllText(Path.Combine(folder, "score-import.json"), ImportJson(input, 240, matched: false));
        var rejected = Ingest(IngestScore(), song: song, folder: folder);
        Check(rejected.Import == null && rejected.Warnings.Count == 1 && rejected.Warnings[0].Contains("ignored") && rejected.Events.All(e => e.Staff < 0), "score import: below 98% matched notes the import is ignored with a warning");
        File.Delete(Path.Combine(folder, "score-import.json"));

        // Transcription mode: sixteenths and triplet eighths a few ticks off snap to their grids, the offsets kept.
        var played = new Score(); Bars(played, 2, 2);
        int[] jitter = { 7, -5, 3, 0, -9, 4, -2, 6, 5, -4, 0, 3, -6, 2 };
        for (int k = 0; k < 8; k++) played.Note((k * 120 + jitter[k]) / 480.0, .2, 60 + k, 96 / 127f, 3, 3);
        for (int k = 0; k < 6; k++) played.Note((960 + k * 160 + jitter[8 + k]) / 480.0, .25, 70 + k, 96 / 127f, 3, 3);
        var snapped = Ingest(played, song: new SongSettings { PatternTimingToleranceBeats = .125 });
        var pe = snapped.Events.OrderBy(e => e.NoteIndex).ToArray();
        Check(snapped.Snapped == 12 && pe.Take(8).Select((e, k) => e.Tick == k * 120 && e.Offset == jitter[k]).All(x => x) && pe.Skip(8).Select((e, k) => e.Tick == 960 + k * 160 && e.Offset == jitter[8 + k]).All(x => x),
            "transcription mode: each bar snaps to its own grid (sixteenths, then triplets) and keeps the offsets: " + string.Join(" ", pe.Select(e => e.Tick)));
        if (verbose) foreach (var s in voices) Console.WriteLine($"  stream {s.Id} {s.Kind} {s.Name}: {s.Events} events");
    }
    // The score import of IngestScore as MuseScore would write it: ticks at `division` per quarter from the pickup, the
    // inner C5 in voice 2, the bass on the lower staff, the grace marked and spelled A♯. Unmatched: half the pitches moved.
    static string ImportJson(MotifInput input, int division, bool matched)
    {
        var notes = new List<string>(); int id = 0;
        foreach (var e in input.Events.OrderBy(e => e.Tick).ThenBy(e => e.Pitch))
        {
            double tick = (e.Tick / (double)MotifPpq - 4) * division, ticks = Math.Round(e.Duration / (double)MotifPpq * 4) / 4 * division;
            bool bass = e.Track == 2, grace = e.Grace;
            int voice = e.Tick == 10 * MotifPpq ? 1 : 0, tpc = grace ? 24 : e.Fifths + 14, pitch = matched || id % 2 == 0 ? e.Pitch : e.Pitch + 1;
            notes.Add(FormattableString.Invariant($"{{\"id\":{id++},\"staff\":{(bass ? 1 : 0)},\"voice\":{voice},\"pitch\":{pitch},\"tpc\":{tpc},\"tick\":{tick},\"ticks\":{ticks},\"measure\":0,\"grace\":{(grace ? "true" : "false")}}}"));
        }
        string measures = string.Join(",", new[] { (0, 0), (240, 1), (720, 2), (1200, 3), (1680, 9) }.Select(m => $"{{\"index\":{m.Item2},\"number\":{m.Item2},\"tick\":{m.Item1 * division / 240}}}"));
        return "{\"format\":\"resonance.score-import/1\",\"division\":" + division + ",\"parts\":[{\"index\":0,\"name\":\"Piano\",\"staves\":[0,1]}],"
            + "\"staves\":[{\"index\":0,\"part\":0,\"name\":\"Right hand\"},{\"index\":1,\"part\":0,\"name\":\"Left hand\"}],\"measures\":[" + measures + "],"
            + "\"directions\":[{\"tick\":" + 3 * division + ",\"staff\":0,\"kind\":\"tempo\",\"text\":\"a tempo\"}],\"notes\":[" + string.Join(",", notes) + "]}";
    }

    // F28: B♭ minor with the leading tone, MIDI only. The key's harmonic-minor span keeps the leading tone A however
    // flat the context around it (G♭ before F7 in bar 3): on the line of fifths it is B♭ + 5, and B𝄫 lies outside.
    static Score MinorScore()
    {
        var s = new Score(); Bars(s, 4);
        Line(s, 0, "Bb4/8 C5/8 Db5/8 Eb5/8 F5/4 Eb5/8 Db5/8 C5/8 Db5/8 Bb4/8 A4/8 Bb4/2 Gb5/4 F5/8 Eb5/8 Db5/4 C5/4 Bb4/8 A4/8 Bb4/8 C5/8 Bb4/2");
        Block(s, 0, 4, "Bb3 Db4 F4", "Bb2"); Block(s, 4, 2, "F3 A3 C4 Eb4", "F2"); Block(s, 6, 2, "Bb3 Db4 F4", "Bb2");
        Block(s, 8, 2, "Gb3 Bb3 Db4", "Gb2"); Block(s, 10, 2, "F3 A3 C4 Eb4", "F2"); Block(s, 12, 2, "F3 A3 C4 Eb4", "F2"); Block(s, 14, 2, "Bb3 Db4 F4", "Bb2");
        return s;
    }
    static void MotifSpelling()
    {
        var chords = new[] { Step(0, 4, "Bb", "m"), Step(4, 6, "F", "7"), Step(6, 8, "Bb", "m"), Step(8, 10, "Gb"), Step(10, 12, "F", "7"), Step(12, 14, "F", "7"), Step(14, 16, "Bb", "m") };
        var input = Ingest(MinorScore(), key: HarmonyModel.Mod(ScorePitch("Bb4") - 21), minor: true, chords: chords);
        var f = input.Frames; var ev = input.Events;
        Check(f.Regions == 1 && f.KeyName(0) == "B♭ minor" && f.Tonic[0] == -2, "F28: one region, B♭ minor: " + f.KeyName(0));
        var a = ev.Where(e => Spelling.Mod(e.Pitch, 12) == 9).ToArray();
        Check(a.Length == 5 && a.All(e => e.Fifths == 3 && Spelling.Letter(e.Fifths) == 5 && Spelling.Mod(e.Morph, 7) == 5 && e.Alter == 1), "F28: every A of B♭ minor is spelled A (letter 5), raised against the key: " + string.Join(" ", a.Select(e => Spelling.Name(e.Fifths))));
        foreach (var (pc, fifths) in new[] { (1, -5), (3, -3), (6, -6), (10, -2), (0, 0), (5, -1) })
            Check(ev.Where(e => Spelling.Mod(e.Pitch, 12) == pc).All(e => e.Fifths == fifths), $"F28: pitch class {pc} is spelled {Spelling.Name(fifths)}");
        var lead = a[0];
        var named = f.Explain(0, lead.Pitch, lead.Fifths, lead.Morph, 1, lead.Tick, lead.Tick, lead.Tick + 1);
        Check(named.Kind == Alteration.LeadingTone && named.Text == "the leading tone of B♭ minor", "F28: the raised A is named as B♭ minor's leading tone: " + named.Text);
        var again = Ingest(MinorScore(), key: HarmonyModel.Mod(ScorePitch("Bb4") - 21), minor: true, chords: chords);
        Check(again.Events.SequenceEqual(input.Events) && again.Streams.Principal == input.Streams.Principal && again.Streams.All.Length == input.Streams.All.Length
            && again.Streams.All.Zip(input.Streams.All).All(p => p.First.Notes.SequenceEqual(p.Second.Notes) && p.First.Lead.SequenceEqual(p.Second.Lead) && p.First.Onset.SequenceEqual(p.Second.Onset)), "motif ingest: two runs are identical");
    }

    static void MotifGroup(bool verbose)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
        var t1 = new Transform(1, 2); var t2 = new Transform(-2, -3); var T2 = new Transform(1, 2, Transform.Real);
        Check(Transform.Identity.Cost() == 0 && Near(t1.Cost(), 2.5) && Near(t2.Cost(), 1.5 + Math.Log2(3)) && Near(T2.Cost(), 2.5 + Math.Log2(3)) && Near(new Transform(-7, -12).Cost(), 1) && Near(new Transform(14, 24, Transform.Real).Cost(), 2)
            && Near(new Transform(2, 4, Transform.Chord).Cost(), 2 + Math.Log2(5)) && Near(new Transform(0, 0, inverted: true).Cost(), 2) && Near(new Transform(0, 0, retrograde: true).Cost(), 4) && Near(new Transform(0, 0, scale: 2).Cost(), 3)
            && Near(new Transform(0, 0, scale: .5).Cost(), 3) && Near(new Transform(0, 0, scale: 1.5).Cost(), 3 * Math.Log2(1.5)) && Near(new Transform(0, 0, head: true).Cost(), 2) && Near(new Transform(0, 0, parallel: -1).Cost(), 2),
            "transform costs: identity 0, octave 1, t_k 1.5 + log2(1+|k|), T_k 2.5 + log2(1+|k|), chord frame 2 + log2(1+|root move|), I 2, R 4, A_r 3|log2 r|, head 2, P 2");
        var mixed = new Transform(2, 3, Transform.Tonal, inverted: true, retrograde: true, scale: 2, parallel: -1);
        Check(t1.Then(t1).Equals(new Transform(2, 4)) && mixed.Then(mixed.Inverse()).Equals(Transform.Identity) && new Transform(0, 0, inverted: true).Then(new Transform(0, 0, inverted: true)).Equals(Transform.Identity)
            && Transform.Compose(t1, T2).Frame == Transform.Real && t1.Then(t2).Steps == -1 && Transform.Compose(new Transform(0, 0, retrograde: true), new Transform(0, 0, retrograde: true)).Equals(Transform.Identity),
            "transform composition: steps add, mirror and retrograde undo themselves, an element and its inverse give the identity");
        Check(TransformGroup.Stabiliser(new[] { 59, 65 }).SequenceEqual(new[] { 0, 6 }) && TransformGroup.Stabiliser(new[] { 60, 63, 66, 69 }).SequenceEqual(new[] { 0, 3, 6, 9 }) && TransformGroup.Stabiliser(new[] { 60, 64, 68 }).SequenceEqual(new[] { 0, 4, 8 })
            && TransformGroup.Stabiliser(new[] { 60, 64, 67 }).SequenceEqual(new[] { 0 }), "stabilisers: tritone {0,6}, diminished seventh {0,3,6,9}, augmented triad {0,4,8}, major triad {0}");
        Check(TransformGroup.Order(-1, new[] { 0, 6 }) == 6 && TransformGroup.Order(-1, new[] { 0, 3, 6, 9 }) == 3 && TransformGroup.Order(4, new[] { 0 }) == 3 && TransformGroup.Order(0, new[] { 0 }) == 1 && TransformGroup.Order(6, new[] { 0 }) == 2
            && TransformGroup.Order(-1, new[] { 0 }) == 12 && TransformGroup.Order(1, 7) == 7, "generator orders: a tritone stepped −1 returns after 6, a diminished seventh after 3, T+4 after 3, T+6 alternates, T0 loops");
        Check(TransformGroup.Name(Transform.Identity) == "repeat" && TransformGroup.Name(t2) == "tonal sequence down a third" && TransformGroup.Name(T2) == "real sequence up a whole tone" && TransformGroup.Name(new Transform(0, -1, Transform.Real)) == "real sequence down a semitone"
            && TransformGroup.Name(new Transform(-7, -12)) == "an octave lower" && TransformGroup.Name(new Transform(0, 0, inverted: true)) == "mirrored (inversion)" && TransformGroup.Name(new Transform(0, -1, parallel: -1), mode: "A major → A minor") == "in the parallel minor (A major → A minor)"
            && TransformGroup.Name(new Transform(-7, -12), new[] { new Alteration(2, 60, -1, Alteration.Borrowed, "♭6 borrowed from E minor") }) == "an octave lower with 1 altered note: ♭6 borrowed from E minor"
            && TransformGroup.Name(new Transform(0, 0, head: true)) == "held note lengthened (augmented head)" && TransformGroup.Name(Transform.Identity, edits: 2) == "variant (2 edits)" && TransformGroup.Name(new Transform(5, 9)) == "tonal sequence up a sixth"
            && TransformGroup.Name(new Transform(0, 0, scale: .5)) == "diminished (note values ×0.5)" && TransformGroup.Name(new Transform(-1, -2, retrograde: true)) == "tonal sequence down a second, in retrograde",
            "transform names: the §5 C6 templates");

        // F3: a tonal sequence up a second, its semitones differing at each step.
        var f3 = new Score(); Bars(f3, 3);
        Line(f3, 0, "C4/4 D4/4 E4/4 C4/4 D4/4 E4/4 F4/4 D4/4 E4/4 F4/4 G4/4 E4/4");
        var input = Ingest(f3);
        var bar2 = Relate(input, 0, 4, 4); var bar3 = Relate(input, 0, 8, 4); var step = Relate(input, 4, 8, 4);
        Check(bar2.G.Steps == 1 && bar3.G.Steps == 2 && step.G.Steps == 1 && bar2.G.Frame == Transform.Tonal && bar3.G.Frame == Transform.Tonal && bar2.Altered.Length == 0 && step.Text == "tonal sequence up a second" && bar3.Text == "tonal sequence up a third",
            "F3: Steps +1 and +2 from the prototype, not real; the neighbour relation reads \"tonal sequence up a second\": " + step.Text);
        var (proto, protoStart) = Cell(input, input.Streams.Principal, 0, 4); var (third, thirdStart) = Cell(input, input.Streams.Principal, 8, 12);
        Check(TransformGroup.Apply(bar3.G, proto, thirdStart, input.Frames).Select(n => n.Pitch).SequenceEqual(third.Select(n => n.Pitch)) && TransformGroup.Apply(T2, proto, thirdStart, input.Frames).Select(n => n.Pitch).SequenceEqual(new[] { 62, 64, 66, 62 }),
            "transform images: t+2 of C D E C is E F G E in C major; T+2 is D E F♯ D");
        var mirror = TransformGroup.Relate(proto.Take(3).ToArray(), proto.Take(3).Reverse().Select((n, j) => n with { Onset = j * MotifPpq }).ToArray(), protoStart, thirdStart, input.Frames);
        var chromatic = TransformGroup.Relate(new[] { new CellNote { Morph = 28, Pitch = 60, Length = 1, Interval = -1 }, new CellNote { Onset = 1, Morph = 28, Pitch = 61, Length = 1, Interval = -1 }, new CellNote { Onset = 2, Morph = 29, Pitch = 62, Length = 1, Interval = -1 } },
            new[] { new CellNote { Morph = 29, Pitch = 62, Length = 1, Interval = -1 }, new CellNote { Onset = 1, Morph = 29, Pitch = 63, Length = 1, Interval = -1 }, new CellNote { Onset = 2, Morph = 30, Pitch = 64, Length = 1, Interval = -1 } }, 0, 4 * q(), input.Frames, chromatic: true);
        Check(mirror.G.Inverted && mirror.G.Steps == 2 && mirror.Text == "tonal sequence up a third, mirrored (inversion)" && chromatic.G.Frame == Transform.Real && chromatic.Text == "real sequence up a whole tone",
            "relations: C D E → E D C is a mirror a third up; a chromatic figure moves by real transposition: " + mirror.Text + " / " + chromatic.Text);

        // F4: a tonicizing sequence (the étude's bars 14–15 a third lower), G♯ the leading tone of A minor.
        var f4 = new Score(); Bars(f4, 2, 2);
        Line(f4, 0, "G4/16 A4/16 A4/16 G4/16 F4/16 G4/16 E4/16 F4/16 B4/16 C5/16 C5/16 B4/16 A4/16 B4/16 G#4/16 A4/16");
        Block(f4, 0, 1, "C3 E3 G3 Bb3", "C2"); Block(f4, 1, 1, "F3 A3 C4", "F2"); Block(f4, 2, 1, "E3 G#3 B3 D4", "E2"); Block(f4, 3, 1, "A3 C4 E4", "A2");
        input = Ingest(f4, chords: new[] { Step(0, 1, "C", "7"), Step(1, 2, "F"), Step(2, 3, "E", "7"), Step(3, 4, "A", "m") });
        var tonicized = Relate(input, 0, 2, 2);
        Check(tonicized.G.Steps == 2 && tonicized.G.Frame == Transform.Tonal && tonicized.Altered.Length == 1 && tonicized.Altered[0].Kind == Alteration.LeadingTone && tonicized.Altered[0].Pitch == 68
            && tonicized.Text == "tonal sequence up a third with 1 altered note: the leading tone of A minor (the sequence tonicizes it)", "F4: Steps +2, G♯ named the leading tone of A minor: " + tonicized.Text);

        // F5: dotted thirds over C major, then the same over C minor with E♭.
        var f5 = new Score(); Bars(f5, 2, 2);
        Line(f5, 0, "E5+C5/16*3 D5+B4/16 E5+C5/16*3 D5+B4/16 Eb5+C5/16*3 D5+B4/16 Eb5+C5/16*3 D5+B4/16");
        Block(f5, 0, 2, "C4 E4 G4", "C3"); Block(f5, 2, 2, "C4 Eb4 G4", "C3");
        input = Ingest(f5, chords: new[] { Step(0, 2, "C"), Step(2, 4, "C", "m") });
        var parallel = Relate(input, 0, 2, 2);
        var (major, majorStart) = Cell(input, input.Streams.Principal, 0, 2); var (minorCell, minorStart) = Cell(input, input.Streams.Principal, 2, 4);
        Check(parallel.G.Parallel == -1 && parallel.G.Steps == 0 && parallel.Altered.Length == 0 && parallel.Text == "in the parallel minor (C major → C minor)"
            && TransformGroup.Apply(parallel.G, major, minorStart, input.Frames).Select(n => n.Pitch).SequenceEqual(minorCell.Select(n => n.Pitch)), "F5: ModeChange, named the parallel minor, and its image is the minor bar: " + parallel.Text);
        // The chord frame: each note the third of its chord (E4 G♯4 over C, E; A4 C5 over F, A♭), so neither transposition
        // is exact. The cells are written out, so the test does not lean on the speller's choice between G♯ and A♭.
        var thirds = new Score(); Bars(thirds, 2, 2);
        Line(thirds, 0, "E4/4 G#4/4 A4/4 C5/4");
        Block(thirds, 0, 1, "C3 G3", "C2"); Block(thirds, 1, 1, "E3 B3", "E2"); Block(thirds, 2, 1, "F3 C4", "F2"); Block(thirds, 3, 1, "Ab3 Eb4", "Ab2");
        input = Ingest(thirds, chords: new[] { Step(0, 1, "C"), Step(1, 2, "E"), Step(2, 3, "F"), Step(3, 4, "Ab") });
        CellNote Third(long onset, int morph, int pitch, int alter) => new() { Onset = onset, Length = MotifPpq, Morph = morph, Pitch = pitch, Alter = alter, Interval = 4 };
        var harmonic = TransformGroup.Relate(new[] { Third(0, 30, 64, 0), Third(MotifPpq, 32, 68, 1) }, new[] { Third(0, 33, 69, 0), Third(MotifPpq, 35, 72, 0) }, 0, 2 * MotifPpq, input.Frames);
        Check(harmonic.G.Frame == Transform.Chord && harmonic.Altered.Length == 0 && harmonic.G.Steps == 3 && harmonic.Text == "harmonic sequence up a fourth (the figure follows the chords)",
            "relations: a figure that keeps each note's interval above a moving root is placed by the chord frame: " + harmonic.Text);
        if (verbose) foreach (var p in new[] { bar2, step, tonicized, parallel, mirror, chromatic, harmonic }) Console.WriteLine($"  {p.Text} ({p.Bits:0.00} bits)");
        static long q() => MotifPpq;
    }

    // Stages B and C on a fixture: the ingest, the reductions and the lexicon. Every member must decode from its prototype,
    // its element and its sparks (§18: decode is tested stage by stage).
    static (MotifInput input, CellLexicon lex) Lexicon(Score score, string name, bool verbose, int key = 3, bool minor = false, SongFormAnalysis.ChordStep[] chords = null)
    {
        var input = Ingest(score, key, minor, chords);
        var lex = CellLexicon.Build(input);
        if (verbose) DumpCells(input, lex, name);
        Check(lex.DecodeErrors.Length == 0 && lex.Cells.Where(c => c.Paradigm >= 0).All(c => c.Decoded), $"{name}: every member decodes from its prototype, element and sparks: " + string.Join("; ", lex.DecodeErrors));
        return (input, lex);
    }
    static CellLexicon.Cell CellAt(CellLexicon lex, int stream, double beat) => lex.Of(stream).FirstOrDefault(c => c.Start == MotifInput.Ticks(beat, MotifPpq));
    static CellLexicon.Paradigm ParadigmOf(CellLexicon lex, CellLexicon.Cell c) => c == null || c.Paradigm < 0 ? null : lex.Paradigms[c.Paradigm];
    static string Beats(CellLexicon lex, CellLexicon.Paradigm p) => p == null ? "none" : string.Join(" ", lex.MembersOf(p).Select(c => (c.Start / (double)MotifPpq).ToString("0.###")));
    static void DumpCells(MotifInput input, CellLexicon lex, string name)
    {
        Console.WriteLine($"  {name}:");
        foreach (var s in input.Streams.All)
        {
            if (lex.StreamCount[s.Id] == 0) continue;
            var line = lex.Lines[s.Id];
            string reduced = line == null ? "" : " · " + string.Join(" ", Enumerable.Range(0, line.Events).Where(line.IsStructural).Select(k => $"{line.Describe(input, s, k)}@{s.Onset[k] / (double)input.Ppq:0.###}"));
            Console.WriteLine($"    stream {s.Id} {s.Kind} {s.Name}{(s.Id == input.Streams.Principal ? " (principal)" : "")}{reduced}");
            foreach (var c in lex.Of(s.Id))
            {
                var p = ParadigmOf(lex, c);
                string sparks = string.Join(", ", c.Sparks.Select(x => $"{x.Kind}:{x.Pitch}:{x.Tag}"));
                Console.WriteLine($"      [{c.Start / (double)input.Ppq:0.###}, {c.End / (double)input.Ppq:0.###}) {CellLexicon.SpaceNames[c.W.Space]} {c.W.Events}ev {(p == null ? "literal" : p.Letter + (c.Id == p.Prototype ? " proto" : c.Exact ? "" : " variant"))} g=({c.G.Steps},{c.G.Semitones}{(c.G.Inverted ? ",I" : "")}{(c.G.Frame == Transform.Real ? ",real" : "")}{(c.G.Parallel != 0 ? ",P" : "")}{(c.G.Head ? ",head" : "")}) \"{c.Text}\" {sparks} ({c.Bits:0.0})");
            }
        }
        foreach (var p in lex.Paradigms) Console.WriteLine($"    paradigm {p.Letter} ({p.Name}, {p.Kind} \"{p.Signature}\", sign {p.Sign}) stream {p.Stream}: {p.Members.Length} members at {Beats(lex, p)}; mirrored {p.Mirrored}, variants {p.Variants}");
        foreach (var a in lex.Atoms) Console.WriteLine($"    atom {a.Letter} {a.Kind} ({a.Name}, \"{a.Signature}\"): {a.Paradigms.Length} paradigms, {a.Members} members, +{a.Positive} −{a.Negative} ·{a.Neutral}");
    }

    // F1's opening (the étude's bars 1–3 a major third lower): a lower neighbour on beat 1, an upper neighbour from beat 2
    // across the barline, its t+1 copy, an arrival on C4; bars 4–6 repeat bars 1–3.
    static Score NeighbourScore()
    {
        var s = new Score(); Bars(s, 6, 2);
        foreach (double at in new[] { 0.0, 6.0 })
        {
            Line(s, at, "E4/8 D4/16 E4/16 F4/16*5 G4/16 G4/16 F4/16 G4/16*5 A4/16 A4/16 G4/16 C4/4");
            Block(s, at, 2, "C3 E3 G3", "C2"); Block(s, at + 2, 3, "B2 D3 F3", "G1"); Block(s, at + 5, 1, "C3 E3 G3", "C2");
        }
        return s;
    }
    static SongFormAnalysis.ChordStep[] NeighbourChords() => new[] { Step(0, 2, "C"), Step(2, 5, "G", "7"), Step(5, 8, "C"), Step(8, 11, "G", "7"), Step(11, 12, "C") };

    // §4: diminution tokens on F1's line, and F25's arrivals.
    static void MotifReduction(bool verbose)
    {
        var input = Ingest(NeighbourScore(), chords: NeighbourChords());
        var s = input.Streams[input.Streams.Principal]; var line = Diminution.Reduce(input, s);
        string Structural(double from, double to) => string.Join(" ", Enumerable.Range(0, line.Events).Where(k => line.IsStructural(k) && s.Onset[k] >= from * MotifPpq && s.Onset[k] < to * MotifPpq).Select(k => line.Describe(input, s, k)));
        Check(Structural(0, 3) == "E4[N− Rep] F4[N+ Rep Rep]" && Structural(3, 5) == "G4[N+ Rep Rep]", "diminution: bar 1 reduces to E4[N− Rep] F4[N+ Rep Rep], as the étude's: " + Structural(0, 5));
        int d = Array.FindIndex(s.Onset, t => t == MotifInput.Ticks(.5, MotifPpq));
        Check(line.Role(d) == "N-" && line.Head[d] == 0 && line.Role(d + 1) == "Rep" && line.Signature(-1, 0, 3) == ("N", -1) && line.Signature(-1, 3, 7) == ("N", 1), "diminution: D4 is the lower neighbour of E4 (N-), the signatures are N− and N+");
        // An appoggiatura (A4 on the downbeat, approached by leap, resolving down to G4 over C) and a turn.
        var app = new Score(); Bars(app, 2, 2);
        Line(app, 0, "C5/4 A4/8 G4/8 E4/16 F4/16 D4/16 E4/16 C4/4");
        Block(app, 0, 4, "C3 E3 G3", "C2");
        input = Ingest(app, chords: new[] { Step(0, 4, "C") });
        s = input.Streams[input.Streams.Principal]; line = Diminution.Reduce(input, s);
        Check(Structural(0, 4) == "C5 G4[A−] E4[T+ Rep] C4", "diminution: an accented A4 resolving down to G4 is an appoggiatura (G4 takes its onset), E F D E a turn: " + Structural(0, 4));
        int a4 = Array.FindIndex(s.Onset, t => t == MotifInput.Ticks(1, MotifPpq));
        Check(line.Role(a4) == "A-" && line.Kind[a4 + 1] == Diminution.Structural && line.Signature(-1, a4, a4 + 2).signature == "A", "diminution: the appoggiatura's role is A- and the resolution stays structural");

        // F25: a 16-bar period in 4/4; cadences on D5 over G7 (bars 4, 12) and C5 over C (bars 8, 16); held notes inside the
        // phrases (bars 2, 6, 10, 14) decorated by neighbours, two of them over the tonic, one over the dominant, one by a
        // repetition first (only its N token stops it). The arrivals are the cuts; the phrase nodes they make are §8 F1's
        // (step 6).
        var period = new Score(); Bars(period, 16);
        Line(period, 0, "C5/8 D5/8 E5/8 G5/8 E5/8 D5/8 C5/8 E5/8 E5/4 E5/16 F5/16 E5/8 D5/8 C5/8 B4/8 C5/8 D5/8 E5/8 F5/8 D5/8 B4/8 C5/8 D5/8 B4/8 D5/1");
        Line(period, 16, "G4/8 C5/8 E5/8 C5/8 G4/8 C5/8 E5/8 G5/8 D5/4. E5/16 D5/16 C5/8 B4/8 D5/8 F5/8 E5/8 G5/8 F5/8 D5/8 C5/8 E5/8 D5/8 B4/8 C5/1");
        Line(period, 32, "E5/8 G5/8 C6/8 G5/8 E5/8 G5/8 C6/8 B5/8 G5/4 G5/16 A5/16 G5/8 F5/8 E5/8 D5/8 E5/8 F5/8 E5/8 D5/8 F5/8 E5/8 D5/8 C5/8 B4/8 D5/1");
        Line(period, 48, "G4/8 C5/8 E5/8 G5/8 C6/8 G5/8 E5/8 C5/8 E5/4. F5/16 E5/16 D5/8 C5/8 D5/8 E5/8 F5/8 A5/8 G5/8 F5/8 E5/8 D5/8 C5/8 B4/8 C5/1");
        var harmony = new List<SongFormAnalysis.ChordStep>();
        foreach (var (start, end, root, quality) in new[] { (0, 8, "C", ""), (8, 12, "D", "m"), (12, 16, "G", "7"), (16, 20, "C", ""), (20, 24, "G", "7"), (24, 26, "D", "m"), (26, 28, "G", "7"), (28, 40, "C", ""),
            (40, 42, "D", "m"), (42, 48, "G", "7"), (48, 56, "C", ""), (56, 58, "F", ""), (58, 60, "G", "7"), (60, 64, "C", "") })
        {
            harmony.Add(Step(start, end, root, quality));
            string tones = quality == "m" ? "D3 F3 A3" : quality == "7" ? "B2 D3 F3" : root == "F" ? "A2 C3 F3" : "C3 E3 G3";
            Block(period, start, end - start, tones, root == "G" ? "G1" : root + "2");
        }
        input = Ingest(period, chords: harmony.ToArray());
        s = input.Streams[input.Streams.Principal]; line = Diminution.Reduce(input, s);
        var arrivals = Diminution.Arrivals(input, line);
        string Arrived() => string.Join(", ", arrivals.Select(x => $"bar {x.Tick / (4 * MotifPpq) + 1} {(x.Full ? "full" : "half")}"));
        if (verbose) Console.WriteLine("  F25 arrivals: " + Arrived());
        Check(arrivals.Select(x => (x.Tick, x.Full)).SequenceEqual(new[] { (12L * MotifPpq, false), (28L * MotifPpq, true), (44L * MotifPpq, false), (60L * MotifPpq, true) }),
            "F25: arrivals at bars 4 (half), 8 (full), 12 (half), 16 (full), so the period's phrases are 4 + 4 + 4 + 4: " + Arrived());
        foreach (double held in new[] { 4.0, 20.0, 36.0, 52.0 })
        {
            int k = Array.FindIndex(s.Onset, t => t == MotifInput.Ticks(held, MotifPpq));
            Check(k >= 0 && line.Prolonged(k) && !arrivals.Any(x => x.Event == k), $"F25: the held note at beat {held} is decorated (prolonged) and no arrival");
        }
    }

    static void MotifCells(bool verbose)
    {
        // F1: neighbour chirality. The lower and the upper neighbour are two paradigms of one N atom, of opposite signs;
        // neither is a mirror of the other.
        var (input, lex) = Lexicon(NeighbourScore(), "F1", verbose, chords: NeighbourChords());
        int melody = input.Streams.Principal;
        var lower = ParadigmOf(lex, CellAt(lex, melody, 0)); var upper = ParadigmOf(lex, CellAt(lex, melody, 1));
        Check(lower != null && upper != null && lower != upper && lower.Kind == "N" && upper.Kind == "N" && lower.Atom == upper.Atom && lower.Sign < 0 && upper.Sign > 0,
            $"F1: a lower-neighbour and an upper-neighbour paradigm in one N atom: lower {Beats(lex, lower)}, upper {Beats(lex, upper)}");
        var protoLower = lower == null ? null : lex.Cells[lower.Prototype];
        Check(Beats(lex, lower) == "0 6" && Beats(lex, upper) == "1 3 7 9" && protoLower?.W.Events == 3 && protoLower?.W.Length == MotifPpq, "F1: the lower neighbour (8th + 16ths) at beats 0 and 6, the upper at 1, 3, 7, 9");
        var atom = upper == null || upper.Atom < 0 ? null : lex.Atoms[upper.Atom];
        Check(atom != null && atom.Kind == "N" && atom.Positive >= 4 && atom.Negative >= 2, $"F1: the N atom's census is +{atom?.Positive} −{atom?.Negative}");
        Check(lower != null && upper != null && lex.MembersOf(lower).Concat(lex.MembersOf(upper)).All(c => !c.G.Inverted && c.Exact) && CellAt(lex, melody, 3)?.G.Steps == 1 && CellAt(lex, melody, 3)?.G.Frame == Transform.Tonal && CellAt(lex, melody, 9)?.G.Steps == 1,
            "F1: no member is inverted, and the t+1 copy has Steps +1: " + CellAt(lex, melody, 3)?.Text);

        // F2: an exact mirror. E F E F twice, later D C D C twice: one paradigm whose later members are inverted.
        var f2 = new Score(); Bars(f2, 4, 2);
        Line(f2, 0, "E4/16 F4/16 E4/16 F4/16 E4/16 F4/16 E4/16 F4/16 G4/4 C5/4 D5/16 C5/16 D5/16 C5/16 D5/16 C5/16 D5/16 C5/16 B4/4 C5/4");
        Block(f2, 0, 8, "C3 E3 G3", "C2");
        (input, lex) = Lexicon(f2, "F2", verbose, chords: new[] { Step(0, 8, "C") });
        melody = input.Streams.Principal;
        var mirror = ParadigmOf(lex, CellAt(lex, melody, 0));
        Check(mirror != null && Beats(lex, mirror) == "0 1 4 5" && mirror.Mirrored == 2 && CellAt(lex, melody, 1)?.G.Inverted == false && CellAt(lex, melody, 4)?.G.Inverted == true && CellAt(lex, melody, 5)?.G.Inverted == true,
            $"F2: one paradigm at {Beats(lex, mirror)}, its two later members inverted (Mirrored {mirror?.Mirrored})");
        Check(CellAt(lex, melody, 4)?.Text.Contains("mirrored") == true, "F2: the first mirrored member's text names the mirror: " + CellAt(lex, melody, 4)?.Text);

        // F3: a tonal sequence (its semitone patterns differ: C D E C, D E F D, E F G E). Its Power (Count 3, StepSteps +1)
        // is stage D's (§6 D3, step 4).
        var f3 = new Score(); Bars(f3, 3);
        Line(f3, 0, "C4/4 D4/4 E4/4 C4/4 D4/4 E4/4 F4/4 D4/4 E4/4 F4/4 G4/4 E4/4");
        (input, lex) = Lexicon(f3, "F3", verbose);
        melody = input.Streams.Principal;
        var sequence = ParadigmOf(lex, CellAt(lex, melody, 0));
        Check(sequence != null && Beats(lex, sequence) == "0 4 8" && lex.MembersOf(sequence).Select(c => c.G.Steps).SequenceEqual(new[] { 0, 1, 2 }) && lex.MembersOf(sequence).All(c => c.G.Frame != Transform.Real),
            $"F3: one paradigm at {Beats(lex, sequence)} with Steps 0, +1, +2, not real");
        Check(CellAt(lex, melody, 4)?.Text == "tonal sequence up a second" && CellAt(lex, melody, 8)?.Text == "tonal sequence up a second", "F3: each statement is a tonal sequence up a second from the one before: " + CellAt(lex, melody, 4)?.Text);

        // F4: a tonicizing sequence (the étude's bars 14–15 a third lower).
        var f4 = new Score(); Bars(f4, 2, 2);
        Line(f4, 0, "G4/16 A4/16 A4/16 G4/16 F4/16 G4/16 E4/16 F4/16 B4/16 C5/16 C5/16 B4/16 A4/16 B4/16 G#4/16 A4/16");
        Block(f4, 0, 1, "C3 E3 G3 Bb3", "C2"); Block(f4, 1, 1, "F3 A3 C4", "F2"); Block(f4, 2, 1, "E3 G#3 B3 D4", "E2"); Block(f4, 3, 1, "A3 C4 E4", "A2");
        (input, lex) = Lexicon(f4, "F4", verbose, chords: new[] { Step(0, 1, "C", "7"), Step(1, 2, "F"), Step(2, 3, "E", "7"), Step(3, 4, "A", "m") });
        melody = input.Streams.Principal;
        var tonicized = CellAt(lex, melody, 2);
        Check(tonicized != null && tonicized.Paradigm >= 0 && tonicized.Paradigm == CellAt(lex, melody, 0)?.Paradigm && tonicized.G.Steps == 2 && tonicized.Text.Contains("leading tone of A minor"),
            "F4: the second bar is the first a third up, G♯ the leading tone of A minor: " + tonicized?.Text);

        // F5: the parallel minor (dotted thirds over C major, then over C minor with E♭).
        var f5 = new Score(); Bars(f5, 2, 2);
        Line(f5, 0, "E5+C5/16*3 D5+B4/16 E5+C5/16*3 D5+B4/16 Eb5+C5/16*3 D5+B4/16 Eb5+C5/16*3 D5+B4/16");
        Block(f5, 0, 2, "C4 E4 G4", "C3"); Block(f5, 2, 2, "C4 Eb4 G4", "C3");
        (input, lex) = Lexicon(f5, "F5", verbose, chords: new[] { Step(0, 2, "C"), Step(2, 4, "C", "m") });
        melody = input.Streams.Principal;
        var minorCell = CellAt(lex, melody, 2);
        Check(minorCell != null && minorCell.Paradigm >= 0 && minorCell.Paradigm == CellAt(lex, melody, 0)?.Paradigm && minorCell.G.Parallel < 0 && minorCell.Text.Contains("parallel minor"),
            "F5: the C minor statement changes mode (ModeChange) and its text names the parallel minor: " + minorCell?.Text);

        // F6: modal mixture. A sigh head over F (bars 1–2), other material (G7, C), then the head an octave lower with A♭3
        // over F minor (bars 5–6): ♭6 borrowed from C minor.
        var f6 = new Score(); Bars(f6, 8, 2);
        Line(f6, 0, "E5/16*5 D5/16 C5/16 A4/16 E5/16*5 D5/16 C5/16 A4/16 B4/8 D5/8 G5/8 F5/8 E5/4 G4/4 E4/16*5 D4/16 C4/16 Ab3/16 E4/16*5 D4/16 C4/16 Ab3/16 B3/8 D4/8 F4/8 D4/8 C4/2");
        Block(f6, 0, 4, "A3 C4 F4", "F2"); Block(f6, 4, 2, "B3 D4 F4", "G2"); Block(f6, 6, 2, "C4 E4 G4", "C3"); Block(f6, 8, 4, "Ab3 C4 F4", "F2"); Block(f6, 12, 2, "B3 D4 F4", "G2"); Block(f6, 14, 2, "C4 E4 G4", "C3");
        (input, lex) = Lexicon(f6, "F6", verbose, chords: new[] { Step(0, 4, "F"), Step(4, 6, "G", "7"), Step(6, 8, "C"), Step(8, 12, "F", "m"), Step(12, 14, "G", "7"), Step(14, 16, "C") });
        melody = input.Streams.Principal;
        var sigh = ParadigmOf(lex, CellAt(lex, melody, 0));
        Check(sigh != null && Beats(lex, sigh) == "0 2 8 10" && sigh.Kind == "P" && sigh.Atom >= 0 && lex.Atoms[sigh.Atom].Kind == "P" && lex.Atoms[sigh.Atom].Name == "sigh", $"F6: the sigh head's paradigm at {Beats(lex, sigh)}, in a P atom named {(sigh == null || sigh.Atom < 0 ? "" : lex.Atoms[sigh.Atom].Name)}");
        foreach (double at in new[] { 8.0, 10.0 })
        {
            var c = CellAt(lex, melody, at);
            Check(c != null && c.G.Steps == -7 && c.Sparks.Count(x => x.Kind == PreparedPatternSong.MotifSparks.Altered) == 1 && c.Sparks.Single(x => x.Kind == PreparedPatternSong.MotifSparks.Altered) is var x && x.Pitch == 56 && x.Tag.Contains("♭6"),
                $"F6: the statement at beat {at} is an octave lower (Steps −7) with one altered A♭3 named ♭6: {c?.Text} {string.Join(",", c?.Sparks.Select(x => x.Tag) ?? Array.Empty<string>())}");
        }

        // F14: an ornamented variant. A one-bar cell, then its restatement with one added passing 32nd (D5 between C5 and E5).
        var f14 = new Score(); Bars(f14, 4, 2);
        Line(f14, 0, "C5/8 E5/16 G5/16 F5/16 D5/16 B4/8 C5/16. D5/32 E5/16 G5/16 F5/16 D5/16 B4/8 A4/4 G4/4 C5/2");
        Block(f14, 0, 1, "C3 E3 G3", "C2"); Block(f14, 1, 1, "B2 D3 F3", "G1"); Block(f14, 2, 1, "C3 E3 G3", "C2"); Block(f14, 3, 1, "B2 D3 F3", "G1"); Block(f14, 4, 2, "D3 F3 A3", "D2"); Block(f14, 6, 2, "C3 E3 G3", "C2");
        (input, lex) = Lexicon(f14, "F14", verbose, chords: new[] { Step(0, 1, "C"), Step(1, 2, "G", "7"), Step(2, 3, "C"), Step(3, 4, "G", "7"), Step(4, 6, "D", "m"), Step(6, 8, "C") });
        melody = input.Streams.Principal;
        var ornamented = CellAt(lex, melody, 2);
        Check(ornamented != null && ornamented.Paradigm >= 0 && ornamented.Paradigm == CellAt(lex, melody, 0)?.Paradigm && !ornamented.Exact
            && ornamented.Sparks.Count(x => x.Kind == PreparedPatternSong.MotifSparks.Inserted) == 1 && ornamented.Sparks.Single(x => x.Kind == PreparedPatternSong.MotifSparks.Inserted).Tag is "P+" or "P-",
            $"F14: the restatement is a variant of the cell with one inserted passing note: {ornamented?.Text} {string.Join(",", ornamented?.Sparks.Select(x => x.Tag) ?? Array.Empty<string>())}");

        // F18: distinct figures (E major). (a) a falling line x x−1 x−2 x−4 against an upper neighbour x x+1 x+1 x; (b) left-hand
        // chromatic dyads, four a beat, against a melodic E D♯ C♯ in the same hand; (c) C♯–E–G–B♭ arpeggios interlocking
        // between the hands against a turn x x+1 x−1 x. Each figure is stated twice.
        var f18 = new Score(); Bars(f18, 7, 2);
        Line(f18, 0, "B4/16 A4/16 G#4/16 E4/16 B4/16 A4/16 G#4/16 E4/16 B4/16 C#5/16 C#5/16 B4/16 B4/16 C#5/16 C#5/16 B4/16 E5/2 E5/2");
        Line(f18, 4, "F3+B3/16 E3+Bb3/16 Eb3+A3/16 D3+Ab3/16 F3+B3/16 E3+Bb3/16 Eb3+A3/16 D3+Ab3/16 E3/16 D#3/8 C#3/16 E3/16 D#3/8 C#3/16", 2, 2);
        Line(f18, 8, "A4/16 B4/16 G#4/16 A4/16 A4/16 B4/16 G#4/16 A4/16");
        for (int k = 0; k < 2; k++)
        {
            Line(f18, 10 + 2 * k, "E4/16 r/16 Bb4/16 r/16 E5/16 r/16 Bb4/16 r/16");
            Line(f18, 10 + 2 * k, "r/16 C#4/16 r/16 G4/16 r/16 C#5/16 r/16 G4/16", 2, 2);
        }
        // Chords without a bass line: the left hand's lane holds only its figures.
        foreach (var (beat, length, tones) in new[] { (0, 4, "G#3 B3 E4"), (4, 4, "B3 D#4 F#4"), (8, 2, "A3 C#4 E4"), (10, 4, "C#3 E3 G3 Bb3") })
            foreach (string p in tones.Split(' ')) f18.Note(beat, length, ScorePitch(p), 64 / 127f, 1, 1);
        (input, lex) = Lexicon(f18, "F18", verbose, key: HarmonyModel.Mod(ScorePitch("E4") - 21), chords: new[] { Step(0, 4, "E"), Step(4, 8, "B", "7"), Step(8, 10, "A"), Step(10, 14, "C#", "dim7") });
        melody = input.Streams.Principal;
        int left = input.Streams.All.Single(x => x.Kind == MotifStreams.Line && x.Track == 2).Id;
        bool Distinct(int stream, double a0, double a1, double b0, double b1)
        {
            var pa = lex.Of(stream).Where(c => c.Paradigm >= 0 && c.Start >= a0 * MotifPpq && c.Start < a1 * MotifPpq).Select(c => c.Paradigm).ToHashSet();
            var pb = lex.Of(stream).Where(c => c.Paradigm >= 0 && c.Start >= b0 * MotifPpq && c.Start < b1 * MotifPpq).Select(c => c.Paradigm).ToHashSet();
            return pa.Count > 0 && pb.Count > 0 && !pa.Overlaps(pb);
        }
        Check(Distinct(melody, 0, 2, 2, 4), "F18 (a): the falling line and the upper neighbour share no paradigm");
        var fall = ParadigmOf(lex, CellAt(lex, melody, 0)); var neighbour = ParadigmOf(lex, CellAt(lex, melody, 2));
        Check(fall != null && neighbour != null && fall.Atom != neighbour.Atom, $"F18 (a): they lie in different atoms ({fall?.Kind}, {neighbour?.Kind})");
        Check(Distinct(left, 4, 6, 6, 8), "F18 (b): the chromatic dyads and the melodic E D♯ C♯ share no paradigm");
        Check(Distinct(melody, 8, 10, 10, 14) || !lex.Of(melody).Any(c => c.Paradigm >= 0 && c.Start >= 10 * MotifPpq && c.Start < 14 * MotifPpq) && ParadigmOf(lex, CellAt(lex, melody, 8)) is { } turn && !lex.MembersOf(turn).Any(c => c.Start >= 10 * MotifPpq),
            "F18 (c): the turn and the interlocking arpeggios share no paradigm");

        // F26: phase. A figure starting on beat 2 and crossing the barline, six times: its cells start on beat 2.
        var f26 = new Score(); Bars(f26, 7, 2);
        Line(f26, 0, "C4/4" + string.Concat(Enumerable.Repeat(" E4/16*5 F4/16 F4/16 E4/16", 6)) + " C4/4");
        Block(f26, 0, 14, "C3 E3 G3", "C2");
        (input, lex) = Lexicon(f26, "F26", verbose, chords: new[] { Step(0, 14, "C") });
        melody = input.Streams.Principal;
        var figure = lex.Of(melody).Where(c => c.Start >= MotifPpq && c.Start < 13 * MotifPpq).ToList();
        Check(figure.Count > 0 && figure.All(c => c.Start % (2 * MotifPpq) == MotifPpq && c.Paradigm >= 0) && figure[0].Start == MotifPpq && figure[^1].End == 13 * MotifPpq,
            "F26: the figure's cells start on beat 2: " + string.Join(" ", figure.Select(c => c.Start / (double)MotifPpq)));

        // F27: a lengthened head. A sigh head held 5/16, then restated held 13/16 (tied into the bar where the figure falls).
        var f27 = new Score(); Bars(f27, 5, 2);
        Line(f27, 0, "E5/16*5 D5/16 C5/16 A4/16 G4/8 B4/8 D5/8 F5/8 E5/16*13 D5/16 C5/16 A4/16 B4/4 C5/4");
        Block(f27, 0, 2, "A3 C4 F4", "F2"); Block(f27, 2, 2, "B3 D4 F4", "G2"); Block(f27, 4, 4, "A3 C4 F4", "F2"); Block(f27, 8, 2, "C4 E4 G4", "C3");
        (input, lex) = Lexicon(f27, "F27", verbose, chords: new[] { Step(0, 2, "F"), Step(2, 4, "G", "7"), Step(4, 8, "F"), Step(8, 10, "C") });
        melody = input.Streams.Principal;
        var held = CellAt(lex, melody, 6);
        Check(held != null && held.Paradigm >= 0 && held.Paradigm == CellAt(lex, melody, 0)?.Paradigm && !held.Exact && held.G.Head
            && held.Sparks.Any(x => x.Kind == PreparedPatternSong.MotifSparks.Lengthened && x.Tag == "held 13/16"), $"F27: the restatement is a variant with a lengthened head: {held?.Text} {string.Join(",", held?.Sparks.Select(x => x.Tag) ?? Array.Empty<string>())}");

        // Determinism (§9 G6): the lexicon twice gives the same cells, paradigms and atoms.
        var again = CellLexicon.Build(input);
        Check(again.Cells.Length == lex.Cells.Length && again.Cells.Zip(lex.Cells).All(p => p.First.Start == p.Second.Start && p.First.End == p.Second.End && p.First.Paradigm == p.Second.Paradigm && p.First.Text == p.Second.Text)
            && again.Atoms.Select(a => a.Letter + a.Kind + a.Members).SequenceEqual(lex.Atoms.Select(a => a.Letter + a.Kind + a.Members)), "motif cells: two runs give the same lexicon");
    }

    // Stages B–D on a fixture: the lexicon (every member decoding) and the iteration.
    static (MotifInput input, CellLexicon lex, Iteration it) Iterate(Score score, string name, bool verbose, int key = 3, bool minor = false, SongFormAnalysis.ChordStep[] chords = null)
    {
        var (input, lex) = Lexicon(score, name, false, key, minor, chords);
        var it = Iteration.Build(input, lex);
        if (verbose) DumpIteration(input, it, name);
        return (input, lex, it);
    }
    static readonly string[] RelationKinds = { "Return", "Power", "Run", "Doubling", "Alternation", "Liquidation", "CrossReference" };
    static void DumpIteration(MotifInput input, Iteration it, string name)
    {
        double q = input.Ppq;
        Console.WriteLine($"  {name} iteration:");
        foreach (var r in it.Relations)
            Console.WriteLine($"    {r.Id} {RelationKinds[r.Kind]}{(r.Source.Length > 0 ? "/" + r.Source : "")} [{r.Start / q:0.###}, {r.End / q:0.###}) ×{r.Count} unit {r.Unit / q:0.###} step ({r.Step.Steps},{r.Step.Semitones}{(r.Step.Frame == Transform.Real ? " real" : "")}{(r.Step.Inverted ? " I" : "")}) "
                + $"period {r.Period} order {r.Order} sym {{{string.Join(",", r.Symmetry)}}} \"{r.Pattern}\" streams {r.StreamA}/{r.StreamB}{(r.Contrary ? " contrary" : "")}{(r.CrossScale ? " cross-scale" : "")} exact {r.Exactness:0.##} parent {r.Parent} children [{string.Join(",", r.Children)}] {r.Bits:0.0} bits — {r.Text}");
        var g = input.Grid;
        Console.WriteLine("    bars: " + string.Join(" | ", Enumerable.Range(g.FirstBar, g.Bars - g.FirstBar).Select(b => $"{g.Printed[b]} {it.Labels[b].Text}{(it.Labels[b].Inherited ? "*" : "")}")));
    }

    // Stage D (§6): runs and cycles (F7–F9), doubling and contrary motion (F10), liquidation (F15), a nested power (F16), a
    // unit of two cells stepping by a semitone (F17), drum loops (F20) and F3's tonal sequence as a cell power.
    static void MotifIteration(bool verbose)
    {
        static Iteration.Relation[] Of(Iteration it, int kind) => it.Relations.Where(r => r.Kind == kind).ToArray();
        const int Power = PreparedPatternSong.MotifRelations.Power, Run = PreparedPatternSong.MotifRelations.Run, Doubling = PreparedPatternSong.MotifRelations.Doubling;
        static string Show(IEnumerable<Iteration.Relation> rs) => string.Join("; ", rs.Select(r => $"{RelationKinds[r.Kind]} {r.Source} [{r.Start / (double)MotifPpq:0.##}, {r.End / (double)MotifPpq:0.##}) ×{r.Count} ({r.Step.Steps},{r.Step.Semitones}) P{r.Period} O{r.Order} {{{string.Join(",", r.Symmetry)}}} \"{r.Pattern}\""));

        // F3: the tonal sequence (C D E C, D E F D, E F G E) is one paradigm whose consecutive cells step +1: a power.
        var f3 = new Score(); Bars(f3, 3);
        Line(f3, 0, "C4/4 D4/4 E4/4 C4/4 D4/4 E4/4 F4/4 D4/4 E4/4 F4/4 G4/4 E4/4");
        var (input, lex, it) = Iterate(f3, "F3", verbose);
        var cellPower = Of(it, Power).FirstOrDefault(r => r.Source == Iteration.FromCells && r.StreamA == input.Streams.Principal);
        Check(cellPower != null && cellPower.Count == 3 && cellPower.Step.Steps == 1 && cellPower.Step.Frame == Transform.Tonal && cellPower.Start == 0 && cellPower.Unit == 4 * MotifPpq && cellPower.Order == 0,
            "F3: a power of three statements stepping +1 tonally (a translation): " + Show(Of(it, Power)));

        // F7: 24 left-hand sixteenth tritones, each a semitone below the one before: a 6-cycle on the tritone's symmetry.
        var f7 = new Score(); Bars(f7, 3, 2);
        for (int k = 0; k < 24; k++) foreach (string p in new[] { "F3", "B3" }) f7.Note(k / 4.0, .25, ScorePitch(p) - k, 80 / 127f, 2, 2);
        (input, lex, it) = Iterate(f7, "F7", verbose);
        int bass = input.Streams.All.Single(s => s.Kind == MotifStreams.Texture && s.Track == 2).Id;
        var tritones = Of(it, Run).Where(r => r.StreamA == bass).ToArray();
        Check(tritones.Length == 1 && tritones[0].Count == 24 && tritones[0].Real && tritones[0].Step.Semitones == -1 && tritones[0].Pattern == "" && tritones[0].Period == 6 && tritones[0].Order == 6 && tritones[0].Symmetry.SequenceEqual(new[] { 0, 6 }),
            "F7: one run of 24 tritones, Semitones −1, Period 6, Order 6, Symmetry {0, 6}: " + Show(Of(it, Run)));

        // F8: twelve quarter-note diminished sevenths, each a semitone lower: a 3-cycle on {0, 3, 6, 9}.
        var f8 = new Score(); Bars(f8, 3);
        for (int k = 0; k < 12; k++) foreach (string p in new[] { "B3", "D4", "F4", "Ab4" }) f8.Note(k, 1, ScorePitch(p) - k, 64 / 127f, 1, 1);
        (input, lex, it) = Iterate(f8, "F8", verbose);
        var sevenths = Of(it, Run);
        Check(sevenths.Length == 1 && sevenths[0].Count == 12 && sevenths[0].Real && sevenths[0].Step.Semitones == -1 && sevenths[0].Symmetry.SequenceEqual(new[] { 0, 3, 6, 9 }) && sevenths[0].Period == 3 && sevenths[0].Order == 3,
            "F8: one run of 12 diminished sevenths, Symmetry {0, 3, 6, 9}, Period 3: " + Show(sevenths));

        // F9: {C5, E5} ↔ {G4, B♭4} eight times in sixteenths: a major and a minor third are one hand shape in scale steps, so
        // the run is tonal (a fourth down and up in steps, whatever the semitones); an alternation.
        var f9 = new Score(); Bars(f9, 2, 2);
        for (int k = 0; k < 16; k++) Line(f9, k / 4.0, k % 2 == 0 ? "C5+E5/16" : "G4+Bb4/16");
        (input, lex, it) = Iterate(f9, "F9", verbose);
        var alternation = Of(it, Run);
        Check(alternation.Length == 1 && alternation[0].Count == 16 && alternation[0].Order == 2 && !alternation[0].Real && alternation[0].Pattern == "-3,+3" && alternation[0].Symmetry.SequenceEqual(new[] { 0 }),
            "F9: one tonal run of 16 alternating thirds, Order 2: " + Show(alternation));

        // F10: three bars of right-hand sixths; the left hand doubles them two octaves lower for two bars, then alternately one
        // and two octaves lower, so that its top rises where the right hand's falls (and back): each of the last four notes is
        // reached in contrary motion.
        var f10 = new Score(); Bars(f10, 3, 2);
        string[] sixths = { "E5+C6", "D5+B5", "C5+A5", "B4+G5", "C5+A5", "D5+B5", "E5+C6", "F5+D6", "A#4+G5", "E5+C#6", "A#4+G5", "E5+C#6" };
        for (int k = 0; k < sixths.Length; k++)
        {
            Line(f10, k / 2.0, sixths[k] + "/8");
            int register = k < 8 || k % 2 == 1 ? -24 : -12;
            foreach (string p in sixths[k].Split('+')) f10.Note(k / 2.0, .5, ScorePitch(p) + register, 80 / 127f, 2, 2);
        }
        (input, lex, it) = Iterate(f10, "F10", verbose);
        int upper = input.Streams.All.Single(s => s.Kind == MotifStreams.Texture && s.Track == 3).Id, lower = input.Streams.All.Single(s => s.Kind == MotifStreams.Texture && s.Track == 2).Id;
        var doubling = Of(it, Doubling).Where(r => !r.Contrary).ToArray();
        int lowerNotes = input.Events.Count(e => e.Track == 2);
        int covered = doubling.Length == 0 ? 0 : input.Events.Count(e => e.Track == 2 && e.Tick >= doubling[0].Start && e.Tick < doubling[0].End);
        Check(doubling.Length == 1 && doubling[0].StreamA == upper && doubling[0].StreamB == lower && covered >= .9 * lowerNotes && doubling[0].Pattern.Split(',').Contains("-24") && doubling[0].Pattern == "-24,-12",
            $"F10: the left hand doubles the right ({covered} of {lowerNotes} notes) at registers -24,-12: " + Show(Of(it, Doubling)));
        var contrary = Of(it, Doubling).Where(r => r.Contrary).ToArray();
        Check(contrary.Length == 1 && contrary[0].Count == 4 && contrary[0].Start == 4 * MotifPpq && contrary[0].End == 6 * MotifPpq && contrary[0].Pattern == "-12,-24",
            "F10: the last four events move in contrary motion: " + Show(contrary));
        // A doubled onset the left hand does not copy, alone among copies, leaves the doubling whole (the étude's bar 51).
        var f10b = new Score(); Bars(f10b, 3, 2);
        for (int k = 0; k < 12; k++)
        {
            Line(f10b, k / 2.0, sixths[k % 8] + "/8");
            foreach (string p in sixths[k % 8].Split('+')) f10b.Note(k / 2.0, .5, ScorePitch(p) - 24 - (k == 5 && p == sixths[5].Split('+')[0] ? 1 : 0), 80 / 127f, 2, 2);
        }
        (input, lex, it) = Iterate(f10b, "F10 (one undoubled note)", verbose);
        var whole = Of(it, Doubling).Where(r => !r.Contrary).ToArray();
        Check(whole.Length == 1 && whole[0].Start == 0 && whole[0].End == 6 * MotifPpq && whole[0].Count == 12 && whole[0].Pattern == "-24", "F10: one undoubled note inside a doubling does not split it: " + Show(Of(it, Doubling)));

        // F15: a 2-bar unit, then 1 bar ×2, ½ bar ×4 and 1 beat ×8 (4/4): the principal line's period shrinks from the bar to
        // the beat.
        var f15 = new Score(); Bars(f15, 8);
        Line(f15, 0, "C5/8 D5/8 E5/4 G5/4 E5/8 C5/8 D5/4 F5/8 E5/8 D5/2" + string.Concat(Enumerable.Repeat(" E5/8 F5/8 G5/4 A5/8 G5/8 F5/4", 2))
            + string.Concat(Enumerable.Repeat(" G5/8 A5/8 B5/4", 4)) + string.Concat(Enumerable.Repeat(" C6/8 B5/16 A5/16", 8)));
        (input, lex, it) = Iterate(f15, "F15", verbose);
        var liquidation = Of(it, PreparedPatternSong.MotifRelations.Liquidation);
        var periods = liquidation.Length == 1 ? liquidation[0].Pattern.Split(',').Select(int.Parse).ToArray() : Array.Empty<int>();
        Check(liquidation.Length == 1 && liquidation[0].Count >= 3 && periods.Length >= 2 && periods.Zip(periods.Skip(1)).All(p => p.Second < p.First) && periods[^1] * 4 <= periods[0]
            && liquidation[0].Start == 8 * MotifPpq && liquidation[0].End == 32 * MotifPpq && liquidation[0].Pattern == "16,4", "F15: a liquidation over bars 3–8 whose period shrinks to a quarter: " + Show(liquidation));

        // F16: a 2-bar unit U twice, then U a step higher twice (2/4): (U²)² ↑1.
        var f16 = new Score(); Bars(f16, 8, 2);
        for (int k = 0; k < 4; k++)
        {
            bool up = k >= 2;
            Line(f16, 4 * k, up ? "F5/8 A5/8 G5/16 F5/16 E5/8 D5/4 A4/4" : "E5/8 G5/8 F5/16 E5/16 D5/8 C5/4 G4/4");
            Line(f16, 4 * k, up ? "D3/2 A2/2" : "C3/2 G2/2", 2, 2);
        }
        (input, lex, it) = Iterate(f16, "F16", verbose);
        var nested = Of(it, Power).Where(r => r.Source != Iteration.FromCells && r.Count == 2 && r.Step.Steps == 1 && r.Step.Frame == Transform.Tonal).ToArray();
        Check(nested.Length == 1 && nested[0].Start == 0 && nested[0].Unit == 8 * MotifPpq && nested[0].Children.Length == 2
            && nested[0].Children.All(c => it.Relations[c].IsPower && it.Relations[c].Count == 2 && it.Relations[c].Step.Equals(Transform.Identity) && it.Relations[c].Unit == 4 * MotifPpq && it.Relations[c].Parent == nested[0].Id),
            "F16: Power(Count 2, StepSteps +1) whose iterations are Power(Count 2, step 0): " + Show(Of(it, Power).Where(r => r.Source != Iteration.FromCells)));

        // F17: bars of tritone dyads stepping +5, −1, +5 (two cells of two), each bar a semitone below the one before.
        var f17 = new Score(); Bars(f17, 4, 2);
        for (int bar = 0; bar < 4; bar++)
            for (int k = 0; k < 4; k++)
            {
                int low = ScorePitch("C4") + new[] { 0, 5, 4, 9 }[k] - bar;
                f17.Note(2 * bar + k / 2.0, .5, low, 96 / 127f, 3, 3); f17.Note(2 * bar + k / 2.0, .5, low + 6, 96 / 127f, 3, 3);
            }
        (input, lex, it) = Iterate(f17, "F17", verbose);
        var stepping = Of(it, Power).Where(r => r.Source == Iteration.FromBlock && r.Unit == 2 * MotifPpq && r.Count == 4 && r.Step.Semitones == -1 && r.Step.Frame == Transform.Real).ToArray();
        Check(stepping.Length == 1 && stepping[0].Start == 0 && Of(it, Run).Length == 0, "F17: a unit power of the bar, Count 4, StepSemitones −1, real (and no run): " + Show(it.Relations));

        // The near relation (§6 D1 with §7 E1's similarity and a θ): a bar stated three times, each a semitone lower, the first
        // with another first note (as the étude's bar 38 begins with the chord that ends the bars before). Exact relations
        // reach only bars 2–3; with θ the soft relation carries the sequence back to bar 1, extending the exact block rather
        // than displacing it.
        var near = new Score(); Bars(near, 5, 2);
        for (int bar = 0; bar < 3; bar++)
            for (int k = 0; k < 8; k++) near.Note(2 * bar + k / 4.0, .25, ScorePitch(new[] { "C5", "E5", "G5", "E5" }[k % 4]) - bar - (bar == 0 && k == 0 ? 3 : 0), 96 / 127f, 3, 3);
        Line(near, 6, "C5/2 G4/2");
        (input, lex) = Lexicon(near, "near", false);
        var exactOnly = Iteration.Build(input, lex); var soft = Iteration.Build(input, lex, .5);
        if (verbose) { DumpIteration(input, exactOnly, "near (no θ)"); DumpIteration(input, soft, "near (θ 0.5)"); }
        static bool Falling(Iteration.Relation r) => r.Source == Iteration.FromBlock && r.Unit == 2 * MotifPpq && r.Step.Semitones == -1;   // the bar, a semitone lower
        var hardBlocks = Of(exactOnly, Power).Where(Falling).ToArray(); var softBlocks = Of(soft, Power).Where(Falling).ToArray();
        Check(hardBlocks.Length == 1 && hardBlocks[0].Start == 2 * MotifPpq && hardBlocks[0].Count == 2 && hardBlocks[0].Step.Semitones == -1 && hardBlocks[0].Real && hardBlocks[0].Exactness == 1,
            "near: without θ the exact relation finds bars 2–3 a semitone apart: " + Show(hardBlocks));
        Check(softBlocks.Length == 1 && softBlocks[0].Start == 0 && softBlocks[0].Count == 3 && softBlocks[0].Step.Semitones == -1 && softBlocks[0].Real && Math.Abs(softBlocks[0].Exactness - .75) < 1e-9,
            "near: with θ the sequence reaches back to bar 1, three bars at T−1, three of four column pairs exact: " + Show(softBlocks));
        var cols = soft.Cols; int c11 = input.Grid.ColumnAt(0), c21 = input.Grid.ColumnAt(2 * MotifPpq), c22 = input.Grid.ColumnAt(3 * MotifPpq);
        var (best, at) = cols.Best(c11, c21);
        Check(Math.Abs(cols.Similarity(c11, c21, -1) - .75) < 1e-9 && Math.Abs(cols.Similarity(c11, c21, 0) - .525) < 1e-9 && Math.Abs(cols.Similarity(c21, c22, 0) - 1) < 1e-9 && at == -1 && Math.Abs(best - .71) < 1e-9
            && cols.Relate(c11, c21).Holds(Iteration.NearKind, -1) && !cols.Relate(c11, c21).Real,
            $"near: §7 E1's sim_k counts exact matches 1 and tonal credit 0.7 (Dice); S takes 0.04 off a transposed match: sim −1 {cols.Similarity(c11, c21, -1):0.###}, sim 0 {cols.Similarity(c11, c21, 0):0.###}, S {best:0.###} at {at}");

        // F20: eight bars of one groove, a fill, eight bars of the groove. Every drum group loops eight times on each side of
        // the fill, which is literal there; the drum bars of the form analysis are untouched.
        var f20 = new Score(); Bars(f20, 17);
        for (int bar = 0; bar < 17; bar++) f20.Groove(4 * bar, 4, 1, bar == 8);
        var drumsInput = Ingest(f20, out var drumsData);
        var drumOptions = new JsonSerializerOptions { IncludeFields = true };
        DrumCompression.Build(drumsData);
        string drumBars = JsonSerializer.Serialize(drumsData.DrumBars, drumOptions) + JsonSerializer.Serialize(drumsData.DrumFamilies, drumOptions);
        var drumsLex = CellLexicon.Build(drumsInput);
        var drumsIt = Iteration.Build(drumsInput, drumsLex);
        if (verbose) { DumpCells(drumsInput, drumsLex, "F20"); DumpIteration(drumsInput, drumsIt, "F20"); }
        Check(drumsLex.DecodeErrors.Length == 0, "F20: every drum member decodes: " + string.Join("; ", drumsLex.DecodeErrors));
        var drumStreams = drumsInput.Streams.All.Where(s => s.Kind == MotifStreams.Drums).Select(s => s.Id).ToArray();
        var loops = Of(drumsIt, Power).Where(r => r.Source == Iteration.FromCells && drumStreams.Contains(r.StreamA)).ToArray();
        Check(loops.Length >= 2 && loops.All(r => r.Count == 8 && r.Step.Equals(Transform.Identity) && r.Order == 1 && (r.Start == 0 || r.Start == 36 * MotifPpq)) && loops.Any(r => r.Start == 0) && loops.Any(r => r.Start == 36 * MotifPpq),
            "F20: drum powers of Count 8 on either side of the fill: " + Show(loops));
        foreach (int s in drumStreams)
        {
            var fill = drumsLex.Of(s).FirstOrDefault(c => c.Start == 32 * MotifPpq); var groove = drumsLex.Of(s).FirstOrDefault(c => c.Start == 0);
            Check(fill == null || fill.Paradigm < 0 || !fill.Exact || groove == null || fill.Paradigm != groove.Paradigm, $"F20: the fill bar of drum stream {s} is literal or a variant, not the groove");
        }
        DrumCompression.Build(drumsData);
        Check(JsonSerializer.Serialize(drumsData.DrumBars, drumOptions) + JsonSerializer.Serialize(drumsData.DrumFamilies, drumOptions) == drumBars, "F20: DrumBars and DrumFamilies are unchanged by the motif stages");

        // Determinism (§9 G6): the iteration twice gives the same relations and labels.
        var again = Iteration.Build(drumsInput, drumsLex);
        Check(again.Relations.Length == drumsIt.Relations.Length && again.Relations.Zip(drumsIt.Relations).All(p => p.First.Start == p.Second.Start && p.First.End == p.Second.End && p.First.Kind == p.Second.Kind && p.First.Count == p.Second.Count && p.First.Text == p.Second.Text)
            && again.Labels.Select(l => l.Text).SequenceEqual(drumsIt.Labels.Select(l => l.Text)), "motif iteration: two runs give the same relations");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Stage E (§7): returns and sections.

    // F11's A and A′: the étude's principal line (the right hand's first voice) and bass (the left hand's lowest note at each of
    // its onsets), transposed down a major third to C major; Chopin's Op. 10 No. 3 is in the public domain. Bars 0–20 and the
    // arrival on 21.1 (its first beat only), then bars 62–77. Tokens: pitch@onset/length in sixteenths from the bar line; a
    // grace note is a sixty-fourth just before its host (A♯4 → F♯4 in bars 7–8), as a MIDI-only grace is found by its length.
    static readonly (string melody, string bass)[] EtudeA =
    {
        ("G3@0/2", ""),   // 0, the pickup
        ("C4@0/2 B3@2/1 C4@3/1 D4@4/5", "C2@0/4 G2@1/2 G2@3/1 G1@4/4 G2@5/2 G2@7/1"),
        ("E4@1/1 E4@2/1 D4@3/1 C4@4/4 E4@4/5", "G1@0/4 G2@1/2 G2@3/1 C2@4/4 G2@5/2 G2@7/1"),
        ("F4@1/1 F4@2/1 E4@3/1 A4@4/3 G4@7/1", "C2@0/4 G2@1/2 G2@3/1 G1@4/4 G2@5/2 G2@7/1"),
        ("F4@0/1 E4@1/1 B3@2/1 C4@3/1 D4@4/5", "C2@0/4 G2@1/2 G2@3/1 G1@4/4 G2@5/2 G2@7/1"),
        ("E4@1/1 E4@2/1 D4@3/1 C4@4/4", "G1@0/4 G2@1/2 G2@3/1 C2@4/4 G2@5/2 G2@7/1"),   // 5, arrival on 5.2
        ("E4@0/1 F4@1/1 D4@2/1 E4@3/1 F4@4/1 G4@5/1 E4@6/1 F4@7/1", "C2@0/4 C3@1/2 C3@3/1 F2@4/4 C3@5/2 C3@7/1"),
        ("A4@0/2 D4@2/4 F#4@5.75/0.25 E4@6/1 D4@7/3", "F2@0/4 D3@1/2 D3@3/1 G2@4/4 D3@5/2 D3@7/1"),
        ("F#4@1.75/0.25 E4@2/1 D4@3/1 B3@4/4 G4@4/4", "A2@0/4 D3@1/2 D3@3/1 G2@4/4 D3@5/2 D3@7/1"),   // 8, half arrival on 8.2
        ("C4@0/2 E4@0/2 B3@2/1 C4@3/1 D4@4/5", "C2@0/4 G2@1/2 G2@3/1 G1@4/4 G2@5/2 G2@7/1"),   // 9 = 1 with E4 added
        ("E4@1/1 E4@2/1 D4@3/1 C4@4/4 E4@4/5", "G1@0/4 G2@1/2 G2@3/1 C2@4/4 G2@5/2 G2@7/1"),
        ("F4@1/1 F4@2/1 E4@3/1 A4@4/3 G4@7/1", "C2@0/4 G2@1/2 G2@3/1 G1@4/4 G2@5/2 G2@7/1"),
        ("F4@0/1 E4@1/1 B3@2/1 C4@3/1 D4@4/5", "C2@0/4 G2@1/2 G2@3/1 G1@4/4 G2@5/2 G2@7/1"),
        ("E4@1/1 E4@2/1 D4@3/1 C4@4/4", "G1@0/4 G2@1/2 G2@3/1 C2@4/4 G2@5/2 G2@7/1"),   // 13, arrival on 13.2
        ("G4@0/1 A4@1/1 A4@2/1 G4@3/1 F4@4/1 G4@5/1 E4@6/1 F4@7/1", "C2@0/4 C3@1/2 C3@3/1 F2@4/4 C3@5/2 C3@7/1"),
        ("B4@0/1 C5@1/1 C5@2/1 B4@3/1 A4@4/1 B4@5/1 G#4@6/1 A4@7/1", "E2@0/4 E3@1/2 E3@3/1 A2@4/4 E3@5/2 E3@7/1"),
        ("C4@0/1 F#4@0/1 C5@0/1 D4@1/1 F#4@1/1 D5@1/1 B3@2/1 F#4@2/1 B4@2/1 C4@3/1 F#4@3/1 C5@3/1 D4@4/1 F#4@4/1 D5@4/1 E4@5/1 F#4@5/1 E5@5/1 C4@6/1 F#4@6/1 C5@6/1 D4@7/1 F#4@7/1 D5@7/1", "A2@0/1 A2@1/1 A2@2/1 A2@3/1 G#2@4/1 G#2@5/1 G#2@6/1 G#2@7/1"),
        ("C5@0/4 E5@0/5 D5@5/1 C5@6/1 A4@7/1", "G2@0/8"),   // 17, the sigh head
        ("B4@0/4 C5@4/1 B4@5/1 A4@6/1 E4@7/1", "D3@0/1 G3@1/1 G2@2/1 G3@3/1 A2@4/1 E3@5/1 E2@6/1 E3@7/1"),
        ("G4@0/4 A4@4/1 G4@5/1 F4@6/1 C4@7/1", "B2@0/1 E3@1/1 E2@2/1 E3@3/1 F2@4/1 C3@5/1 C2@6/1 C3@7/1"),
        ("E4@0/7.2 D4@7.2/0.8", "G2@0/1 C3@1/1 C2@2/1 C3@3/1 G2@4/1 C3@5/1 C2@6/6 G2@7/5"),   // 20, the cadential six-four
        ("C4@0/4", "C3@0/4"),   // 21.1, the arrival that closes A
    };
    static readonly (string melody, string bass)[] EtudeReprise =
    {
        ("C4@0/2 B3@2/1 C4@3/1 D4@4/5", "C2@0/4 G2@1/2 G2@3/1 G1@4/4 G2@5/2 G2@7/1"),   // 62, Tempo I
        ("E4@1/1 E4@2/1 D4@3/1 C4@4/4 E4@4/5", "G1@0/4 G2@1/2 G2@3/1 C2@4/4 G2@5/2 G2@7/1"),
        ("F4@1/1 F4@2/1 E4@3/1 A4@4/3 G4@7/1", "C2@0/4 G2@1/2 G2@3/1 G1@4/4 G2@5/2 G2@7/1"),
        ("F4@0/1 E4@1/1 B3@2/1 C4@3/1 D4@4/5", "C2@0/4 G2@1/2 G2@3/1 G1@4/4 G2@5/2 G2@7/1"),
        ("E4@1/1 E4@2/1 D4@3/1 C4@4/4", "G1@0/4 G2@1/2 G2@3/1 C2@4/4 G2@5/2 G2@7/1"),
        ("G4@0/1 A4@1/1 A4@2/1 G4@3/1 F4@4/1 G4@5/1 E4@6/1 F4@7/1", "C2@0/4 C3@1/2 C3@3/1 F2@4/4 C3@5/2 C3@7/1"),
        ("B4@0/1 C5@1/1 C5@2/1 B4@3/1 A4@4/1 B4@5/1 G#4@6/1 A4@7/1", "E2@0/4 E3@1/2 E3@3/1 A2@4/4 E3@5/2 E3@7/1"),
        ("C4@0/1 F#4@0/1 C5@0/1 D4@1/1 F#4@1/1 D5@1/1 B3@2/1 F#4@2/1 B4@2/1 C4@3/1 F#4@3/1 C5@3/1 D4@4/1 F#4@4/1 D5@4/1 E4@5/1 F#4@5/1 E5@5/1 C4@6/1 F#4@6/1 C5@6/1 D4@7/1 F#4@7/1 D5@7/1", "A2@0/1 A2@1/1 A2@2/1 A2@3/1 G#2@4/1 G#2@5/1 G#2@6/1 G#2@7/1"),
        ("C5@0/4 E5@0/5 D5@5/1 C5@6/1 A4@7/1", "G2@0/8"),
        ("B4@0/4 C5@4/1 B4@5/1 A4@6/1 E4@7/1", "D3@0/1 G3@1/1 G2@2/1 G3@3/1 A2@4/1 E3@5/1 E2@6/1 E3@7/1"),
        ("G4@0/4 A4@4/1 G4@5/1 F4@6/1 C4@7/1", "B2@0/1 E3@1/1 E2@2/1 E3@3/1 F2@4/1 C3@5/1 C2@6/1 C3@7/1"),
        ("E4@0/5 D4@5/1 C4@6/1 G#3@7/1", "G2@0/1 C3@1/1 C2@2/1 C3@3/1 G#2@4/1 C3@5/1 C2@6/1 C3@7/1"),   // 73, the coda: the sigh an octave lower, A♭3 = ♭6
        ("E4@0/5 D4@5/1 C4@6/1 G#3@7/1", "G2@0/1 C3@1/1 C2@2/1 C3@3/1 G#2@4/1 C3@5/1 C2@6/1 C3@7/1"),
        ("E4@0/13", "G2@0/1 C3@1/1 C2@2/1 C3@3/1 G2@4/1 C3@5/1 C2@6/1 C3@7/1"),   // 75, the head held 13/16
        ("D4@5/1 C4@6/1 G3@7/1", "G2@0/1 C3@1/1 C2@2/1 C3@3/1 G2@4/1 C3@5/1 C2@6/1 G2@7/9"),
        ("E3@0/8 C4@0/8", "C2@0/8"),   // 77, the final arrival
    };
    // Writes a bar's "pitch@onset/length" tokens (sixteenths) from the bar's start beat.
    static void Tokens(Score score, double bar, string tokens, int track, int channel, float velocity)
    {
        foreach (string token in tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = token.IndexOf('@'), slash = token.IndexOf('/');
            double onset = double.Parse(token[(at + 1)..slash], System.Globalization.CultureInfo.InvariantCulture), length = double.Parse(token[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            score.Note(bar + onset / 4, length / 4, ScorePitch(token[..at]), velocity, channel, track);
        }
    }
    // F11 (motif-ternary, also the Unity fixture): 2/4 after a 1/8 pickup, C major. A is the étude's bars 0–21.1; B is twenty
    // synthetic bars from 21.2: a 2-bar unit of lilting sixths U twice, then a step higher twice ((U²)² ↑1, 21.2–29.1); left-hand
    // tritones falling a semitone each sixteenth under falling right-hand quarters (29.2–33.1); a 2-bar unit of alternating
    // sixths twice (33.2–37.1); a beat on the dominant; four bars liquidating 8 → 4 → 2 over a dominant pedal (38–41). A′ is the
    // étude's bars 62–77 (fixture bars 42–57). Printed bar b ≥ 1 starts at beat 0.5 + 2(b − 1). plainNine: F13, bar 9 without
    // its added E4, so a1′ equals a1.
    static Score TernaryMotifScore(bool plainNine = false)
    {
        var s = new Score();
        s.Bars.Add(new MidiCycleAnalysis.Bar { Start = 0, End = .5, Numerator = 1, Denominator = 8 }); s.Beat = .5;
        Bars(s, 57, 2);
        static double BarAt(int b) => b == 0 ? 0 : .5 + 2 * (b - 1);
        const float melody = 96 / 127f, low = 80 / 127f;
        for (int b = 0; b < EtudeA.Length; b++)
        {
            Tokens(s, BarAt(b), plainNine && b == 9 ? EtudeA[1].melody : EtudeA[b].melody, 3, 3, melody);
            Tokens(s, BarAt(b), EtudeA[b].bass, 2, 2, low);
        }
        for (int b = 0; b < EtudeReprise.Length; b++) { Tokens(s, BarAt(42 + b), EtudeReprise[b].melody, 3, 3, melody); Tokens(s, BarAt(42 + b), EtudeReprise[b].bass, 2, 2, low); }
        const string U = "E4+C5/16 D4+B4/16 E4+C5/8 B3+G4/8 C4+A4/8 D4+B4/16 C4+A4/16 D4+B4/8 G3+E4/4", U1 = "F4+D5/16 E4+C5/16 F4+D5/8 C4+A4/8 D4+B4/8 E4+C5/16 D4+B4/16 E4+C5/8 A3+F4/4";
        double at = BarAt(21) + 1;
        foreach (bool up in new[] { false, false, true, true }) { Line(s, at, up ? U1 : U); Line(s, at, up ? "D3/4 B2/4 A2/4 D3/4" : "C3/4 A2/4 G2/4 C3/4", 2, 2, low); at += 4; }
        for (int k = 0; k < 32; k++) foreach (string p in new[] { "F#4", "C5" }) s.Note(at + k / 4.0, .25, ScorePitch(p) - k, low, 2, 2);
        Line(s, at, "B4/4 A#4/4 A4/4 G#4/4 G4/4 F#4/4 F4/4 E4/4"); at += 8;
        for (int k = 0; k < 2; k++)
        {
            Line(s, at, string.Concat(Enumerable.Repeat("E4+C5/16 G4+E5/16 ", 4)) + string.Concat(Enumerable.Repeat("D4+B4/16 F4+D5/16 ", 4)));
            Line(s, at, "C3/2 G2/2", 2, 2, low); at += 4;
        }
        Line(s, at, "B3+G4/4"); Line(s, at, "G2/4", 2, 2, low); at += 1;
        Line(s, at, "G4/16 A4/16 B4/16 D5/16 C5/16 B4/16 A4/16 G4/16 G4/16 A4/16 B4/16 D5/16 C5/16 B4/16 A4/16 G4/16 B4/16 C5/16 D5/16 C5/16 B4/16 C5/16 D5/16 C5/16"
            + string.Concat(Enumerable.Repeat(" D5/16 C5/16", 4)));
        Line(s, at, "G2/2 G2/2 G2/2 G2/2", 2, 2, low);
        return s;
    }

    // Stages A–E on a fixture: the lexicon (every member decoding) and the returns.
    static (MotifInput input, CellLexicon lex, Returns ret) Returned(Score score, string name, bool verbose, SongSettings song = null)
    {
        var input = Ingest(score, out var data, song: song);
        var lex = CellLexicon.Build(input);
        Check(lex.DecodeErrors.Length == 0, $"{name}: every member decodes: " + string.Join("; ", lex.DecodeErrors));
        if (verbose) { Console.WriteLine($"  {name} returns:"); Returns.Trace = x => Console.WriteLine("  " + x); }
        var ret = Returns.Build(input, lex, data);
        Returns.Trace = null;
        if (verbose) DumpReturns(input, ret, lex);
        var again = Returns.Build(input, lex, data);
        Check(again.DescribeSections() == ret.DescribeSections() && again.All.Select(again.Describe).SequenceEqual(ret.All.Select(ret.Describe)) && again.Theta == ret.Theta, $"{name}: two runs give the same sections and scripts");
        return (input, lex, ret);
    }
    static void DumpReturns(MotifInput input, Returns ret, CellLexicon lex = null)
    {
        double q = input.Ppq;
        int principal = input.Streams.Principal;
        if (lex != null && principal >= 0 && lex.Lines[principal] != null)
            Console.WriteLine("    arrivals: " + string.Join(", ", Diminution.Arrivals(input, lex.Lines[principal]).Select(a => $"{a.Tick / q:0.###}{(a.Full ? "" : " (half)")}")));
        Console.WriteLine($"    θ {ret.Theta:0.###}; sections {ret.DescribeSections()}");
        foreach (var r in ret.All) Console.WriteLine($"    level {r.Level} return {r.Id}: [{r.SourceStart / q:0.###}, {r.SourceEnd / q:0.###}) → [{r.Start / q:0.###}, {r.End / q:0.###}) k {r.K}: {ret.Describe(r)}; omits {string.Join(", ", r.Omitted.Select(o => $"[{o.Start / q:0.###}, {o.End / q:0.###})"))}");
        for (int i = 0; i < ret.Sections.Length; i++) Console.WriteLine($"    cuts of {ret.Sections[i].Label}: {string.Join(" ", ret.Cuts[i].Select(c => $"{c.Tick / q:0.###}{(c.Arrival ? "" : "*")}"))}");
    }
    static bool Near(long tick, double beat, double tolerance) => Math.Abs(tick / (double)MotifPpq - beat) <= tolerance + 1e-9;
    // The level-1 returns' deletions, in beats.
    static (double start, double end)[] Deleted(Returns ret) => ret.All.Where(r => r.Level == 1).SelectMany(r => r.Script).Where(e => e.Op == PreparedPatternSong.MotifEdits.Delete)
        .Select(e => (e.SourceStart / (double)MotifPpq, e.SourceEnd / (double)MotifPpq)).ToArray();
    // Level 1 is A B A′, A′ restating A, with the boundaries within tolerance of a and b.
    static void Ternary(Returns ret, string name, double a, double b, double tolerance)
    {
        var s = ret.Sections;
        Check(s.Length == 3 && s[0].Label == "A" && s[1].Label == "B" && s[2].Label == "A′" && s[0].Kind == Returns.PrototypeKind && s[1].Kind == Returns.ComplementKind && s[2].Kind == Returns.CopyKind && s[2].Prototype == 0
            && Near(s[1].Start, a, tolerance) && Near(s[2].Start, b, tolerance), $"{name}: level 1 is A B A′ with A′ restating A, boundaries at {a} and {b} (±{tolerance}): {ret.DescribeSections()}");
    }
    static string Omits(Returns.Return r) => string.Join(", ", r.Omitted.Select(o => $"[{o.Start / (double)MotifPpq}, {o.End / (double)MotifPpq})"));

    static void MotifReturns(bool verbose)
    {
        const int Match = PreparedPatternSong.MotifEdits.Match, Delete = PreparedPatternSong.MotifEdits.Delete, Insert = PreparedPatternSong.MotifEdits.Insert, Substitute = PreparedPatternSong.MotifEdits.Substitute;

        // F11: the étude-shaped ternary. A [0, 41.5) closes on its arrival at 21.1, B from 21.2, A′ from 42.1 (82.5), its coda
        // from 53.1 (104.5).
        var (_, _, ret) = Returned(TernaryMotifScore(), "F11", verbose);
        Ternary(ret, "F11", 41.5, 82.5, .5);
        var level1 = ret.All.Where(r => r.Level == 1).ToArray();
        Check(level1.Length == 1 && level1[0].Section == 2 && level1[0].SourceSection == 0 && level1[0].K == 0 && ret.Sections[2].Return == level1[0].Id, "F11: one level-1 return, A′ ← A");
        var deleted = Deleted(ret);
        Check(deleted.Length == 1 && Math.Abs(deleted[0].start - 10.5) <= 1 && Math.Abs(deleted[0].end - 26.5) <= 1, $"F11: the return deletes exactly a2 + a1′, bars 6–13 = [10.5, 26.5) (±1 beat): {ret.Describe(level1[0])}");
        var script = level1[0].Script;
        Check(script.Length == 4 && script[0].Op == Match && Near(script[0].SourceStart, .5, 0) && Near(script[0].TargetStart, 82.5, 0) && script[1].Op == Delete && script[2].Op == Match
            && script[3].Op == Insert && Near(script[3].TargetStart, 104.5, 1) && Near(script[3].TargetEnd, 114.5, 0),
            $"F11: A′ restates A from bar 1, leaves out bars 6–13 and ends with the coda's insert from 53.1: {ret.Describe(level1[0])}");
        Check(level1[0].Omitted.Length == 2 && Near(level1[0].Omitted[0].Start, 10.5, 0) && Near(level1[0].Omitted[0].End, 16.5, 0) && Near(level1[0].Omitted[1].Start, 16.5, 0) && Near(level1[0].Omitted[1].End, 26.5, 0)
            && ret.Sections[2].CodaStart >= 0 && Near(ret.Sections[2].CodaStart, 104.5, 1),
            $"F11: the omitted phrases are a2 [10.5, 16.5) and a1′ [16.5, 26.5), and the coda starts with the sigh at 53.1: omits {Omits(level1[0])}, coda {ret.Sections[2].CodaStart / (double)MotifPpq}");
        var inner = ret.All.Where(r => r.Level == 2 && r.Section == 0).ToArray();
        Check(inner.Length == 1 && Near(inner[0].SourceStart, .5, 0) && Near(inner[0].Start, 16.5, 0) && Near(inner[0].End, 26.5, 0)
            && inner[0].Script.Count(e => e.Op == Substitute) == 1 && inner[0].Script.Single(e => e.Op == Substitute) is var sub && Near(sub.TargetStart, 16.5, 0) && Near(sub.TargetEnd, 17.5, 0),
            "F11: inside A, a1′ ≅ a1 with one substitution at 9.1 (the added E4): " + string.Join("; ", inner.Select(ret.Describe)));
        Check(ret.Cuts[0].Select(c => c.Tick / (double)MotifPpq).SequenceEqual(new[] { 10.5, 16.5, 26.5 }), "F11: A's phrase cuts are 6.1, 9.1 and 14.1 (the pickup and 5 + 3 + 5 + 7 bars): " + string.Join(" ", ret.Cuts[0].Select(c => c.Tick / (double)MotifPpq)));

        // F13: the same with a1′ identical to a1. With no exact-surface hint, the phrase edges and then the earliest source still
        // place the deletion on a2 + a1′.
        (_, _, ret) = Returned(TernaryMotifScore(plainNine: true), "F13", verbose);
        Ternary(ret, "F13", 41.5, 82.5, .5);
        deleted = Deleted(ret);
        Check(deleted.Length == 1 && Math.Abs(deleted[0].start - 10.5) <= 1 && Math.Abs(deleted[0].end - 26.5) <= 1, "F13: with a1′ = a1 the return still deletes a2 + a1′: " + string.Join("; ", ret.All.Where(r => r.Level == 1).Select(ret.Describe)));

        // F12: a head-deleted reprise (HeadDeletedScore).
        (_, _, ret) = Returned(HeadDeletedScore(), "F12", verbose);
        Ternary(ret, "F12", 40, 104, .5);
        var reprise = ret.All.Single(r => r.Level == 1);
        Check(reprise.Script.Length == 3 && reprise.Script[0].Op == Delete && Near(reprise.Script[0].SourceStart, 0, 0) && Near(reprise.Script[0].SourceEnd, 16, 0)
            && reprise.Script[1].Op == Match && Near(reprise.Script[1].TargetStart, 104, 0) && Near(reprise.Script[1].TargetEnd, 128, 0) && reprise.Script[2].Op == Insert
            && reprise.Omitted.Length == 1 && Near(reprise.Omitted[0].Start, 0, 0) && Near(reprise.Omitted[0].End, 16, 0) && Near(ret.Sections[2].CodaStart, 128, 0),
            $"F12: A′ deletes x (omitting its phrase), restates y and adds its coda: {ret.Describe(reprise)}; omits {Omits(reprise)}");

        // F21: F11 under transcription noise (new Random(7)): onsets jittered by σ = ⅛ beat and re-quantised to sixteenths, 3% of
        // the notes dropped, 2% spurious (±1, ±2 or +12 semitones), transcription mode at 0.125 beat; no voices (MIDI only).
        (_, _, ret) = Returned(Noisy(TernaryMotifScore(), new Random(7)), "F21", verbose, new SongSettings { PatternTimingToleranceBeats = .125 });
        Ternary(ret, "F21", 41.5, 82.5, 1);
        deleted = Deleted(ret);
        Check(deleted.Length == 1 && Math.Abs(deleted[0].start - 10.5) <= 2 && Math.Abs(deleted[0].end - 26.5) <= 2, "F21: under noise the return still deletes a2 + a1′ (±2 beats): " + string.Join("; ", ret.All.Where(r => r.Level == 1).Select(ret.Describe)));

        // F19: pop. Level 1 is data.Sections verbatim; (V2 C2) ← (V1 C1) is one return spanning both; C3 ← C1 a whole tone up; the
        // verse holds its 4-bar progression twice; the drums loop.
        var (input, data) = PopMotifInput();
        var lex = CellLexicon.Build(input);
        if (verbose) { Console.WriteLine("  F19 returns:"); Returns.Trace = t => Console.WriteLine("  " + t); }
        ret = Returns.Build(input, lex, data);
        Returns.Trace = null;
        if (verbose) DumpReturns(input, ret);
        Check(ret.Reviewed && ret.Sections.Length == data.Sections.Length && Enumerable.Range(0, data.Sections.Length).All(i => ret.Sections[i].Start == MotifInput.Ticks(data.Sections[i].Start, MotifPpq) && ret.Sections[i].End == MotifInput.Ticks(data.Sections[i].End, MotifPpq) && ret.Sections[i].Label == data.Sections[i].DisplayName)
            && string.Join(",", ret.Sections.Select(s => s.Label)) == "Intro,Verse 1,Chorus 1,Verse 2,Chorus 2,Bridge,Chorus 3,Outro", "F19: level 1 is the eight pop sections verbatim: " + ret.DescribeSections());
        level1 = ret.All.Where(r => r.Level == 1).ToArray();
        string Pairs() => string.Join("; ", level1.Select(r => $"{r.SourceSection}–{r.LastSourceSection} → {r.Section}–{r.LastSection} k {r.K}"));
        Check(level1.Count(r => r.Section == 3) == 1 && level1.Single(r => r.Section == 3) is var vc && vc.LastSection == 4 && vc.SourceSection == 1 && vc.LastSourceSection == 2 && vc.K == 0
            && vc.Start == ret.Sections[3].Start && vc.End == ret.Sections[4].End, "F19: (V2 C2) ← (V1 C1) is one return spanning both: " + Pairs());
        Check(level1.Count(r => r.Section == 6) == 1 && level1.Single(r => r.Section == 6) is var c3 && c3.LastSection == 6 && c3.SourceSection == 2 && c3.K == 2, "F19: C3 ← C1 with Semitones +2: " + Pairs());
        var it = Iteration.Build(input, lex, ret.Theta, ret.Boundaries);
        Check(it.Relations.Any(r => r.IsPower && r.Source != Iteration.FromCells && r.Count == 2 && r.Unit == 16 * MotifPpq && r.Start >= ret.Sections[1].Start && r.End <= ret.Sections[1].End), "F19: the verse holds its 4-bar progression twice (a power of Count 2)");
        var drums = input.Streams.All.Where(s => s.Kind == MotifStreams.Drums).Select(s => s.Id).ToArray();
        Check(it.Relations.Any(r => r.IsPower && drums.Contains(r.StreamA) && r.Count >= 8), "F19: a drum power with Count ≥ 8");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Stage F (§8): the hierarchy.

    const int PieceKind = PreparedPatternSong.MotifKinds.Piece, SectionKind = PreparedPatternSong.MotifKinds.Section, PhraseKind = PreparedPatternSong.MotifKinds.Phrase,
        PeriodKind = PreparedPatternSong.MotifKinds.Period, SubPhraseKind = PreparedPatternSong.MotifKinds.SubPhrase, PowerKind = PreparedPatternSong.MotifKinds.Power,
        UnitKind = PreparedPatternSong.MotifKinds.Unit, CodaKind = PreparedPatternSong.MotifKinds.Coda, IntroKind = PreparedPatternSong.MotifKinds.Intro;

    // Stages A–F on a fixture: the tree's shape is checked, and a second run must give the same tree, texts and relations.
    static (MotifInput input, CellLexicon lex, Returns ret, Iteration it, global::Hierarchy tree) Tree(MotifInput input, PreparedPatternSong data, string name, bool verbose)
    {
        var lex = CellLexicon.Build(input);
        Check(lex.DecodeErrors.Length == 0, $"{name}: every member decodes: " + string.Join("; ", lex.DecodeErrors));
        var ret = Returns.Build(input, lex, data);
        var it = Iteration.Build(input, lex, ret.Theta, ret.Boundaries);
        if (verbose) { Console.WriteLine($"  {name} hierarchy:"); global::Hierarchy.Trace = x => Console.WriteLine("  " + x); }
        var tree = global::Hierarchy.Build(input, lex, it, ret, data);
        global::Hierarchy.Trace = null;
        if (verbose)
        {
            foreach (var line in tree.Dump().Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("    " + line);
            Console.WriteLine("    formula:\n      " + tree.Formula.Replace("\n", "\n      "));
            Console.WriteLine("    caption: " + tree.Line);
            foreach (var text in tree.ReturnText) Console.WriteLine("    return: " + text);
        }
        CheckTree(tree, name);
        var again = global::Hierarchy.Build(input, lex, it, ret, data);
        Check(again.Dump() == tree.Dump() && again.Formula == tree.Formula && again.Line == tree.Line && again.ReturnText.SequenceEqual(tree.ReturnText)
            && again.PowerNode.SequenceEqual(tree.PowerNode) && again.ReturnNode.SequenceEqual(tree.ReturnNode), $"{name}: two runs give the same tree");
        return (input, lex, ret, it, tree);
    }
    // §8 F7: Nodes[0] is the piece; each node's children are contiguous, in time order, inside it, one level down or nested at
    // level 3 (Level 1 sections, 2 phrases, periods and codas, 3 sub-phrases, powers and units), and Depth counts the tree.
    static void CheckTree(global::Hierarchy t, string name)
    {
        var n = t.Nodes; bool ok = n.Length > 0 && n[0].Kind == PieceKind && n[0].Parent == -1 && n[0].Level == 0 && n[0].Depth == 0; string bad = "";
        for (int i = 0; i < n.Length && ok; i++)
        {
            var x = n[i];
            ok = x.Id == i && x.Start < x.End && x.Prototype < n.Length && (x.Kind == PieceKind) == (i == 0)
                && (x.Level == 1) == (x.Kind is SectionKind or IntroKind) && (x.Level == 2) == (x.Kind is PhraseKind or PeriodKind or CodaKind) && (x.Level == 3) == (x.Kind is SubPhraseKind or PowerKind or UnitKind);
            for (int j = 0; j < x.ChildCount && ok; j++)
            {
                var k = n[x.FirstChild + j];
                ok = k.Parent == i && k.Depth == x.Depth + 1 && (k.Level == x.Level + 1 || k.Level == 3 && x.Level == 3) && k.Start >= x.Start && k.End <= x.End
                    && (j == 0 || n[x.FirstChild + j - 1].End <= k.Start);
            }
            if (!ok) bad = $"{x.Label} [{x.Start / (double)MotifPpq}, {x.End / (double)MotifPpq}) kind {x.Kind} level {x.Level}";
        }
        Check(ok, $"{name}: the tree is a preorder of contiguous children in time order, each inside its parent at its level: {bad}");
    }

    static void MotifHierarchy(bool verbose)
    {
        global::Hierarchy.Node[] nodes = Array.Empty<global::Hierarchy.Node>();
        global::Hierarchy.Node[] Kids(global::Hierarchy.Node x) => x.ChildCount == 0 ? Array.Empty<global::Hierarchy.Node>() : nodes[x.FirstChild..(x.FirstChild + x.ChildCount)];
        static string Show(IEnumerable<global::Hierarchy.Node> xs) => string.Join(" | ", xs.Select(x => FormattableString.Invariant($"{x.Label} [{x.Start / (double)MotifPpq}, {x.End / (double)MotifPpq})")));
        const int Altered = PreparedPatternSong.MotifSparks.Altered, Lengthened = PreparedPatternSong.MotifSparks.Lengthened;

        // F11 in full: level 1 A B A′; one return whose deletion is a2 + a1′, which A′ omits; A's phrases 5 + 3 + 5 + 7 bars after
        // the pickup, a1′ restating a1; a3's sub-phrase cut at the sigh head; B1 = (U²)² ↑1; the tritone run's 6-cycle in B; A′ =
        // a1 · a3′ · Coda, the coda's two cells at Steps −7 with A♭3 named ♭6 and its lengthened-head variant; bar 16 = bar 6 a
        // sixth higher (E14's cross-reference); the formula and caption lines.
        var f11 = Ingest(TernaryMotifScore(), out var f11Data);
        var (input, lex, ret, it, tree) = Tree(f11, f11Data, "F11", verbose);
        nodes = tree.Nodes;
        var top = Kids(nodes[0]);
        Check(top.Length == 3 && top[0].Label == "A" && top[1].Label == "B" && top[2].Label == "A′" && top.All(x => x.Kind == SectionKind)
            && Near(top[0].End, 41.5, .5) && Near(top[2].Start, 82.5, .5) && top[2].Prototype == top[0].Id && top[2].Return >= 0 && ret.All[top[2].Return].Level == 1
            && tree.ReturnNode[top[2].Return] == top[2].Id && tree.ReturnSource[top[2].Return] == top[0].Id, "F11: level 1 is A B A′, A′ the copy of A through the level-1 return: " + Show(top));
        var deleted = Deleted(ret);
        Check(top[2].Omits == "a2, a1′" && deleted.Length == 1 && Math.Abs(deleted[0].start - 10.5) <= 1 && Math.Abs(deleted[0].end - 26.5) <= 1,
            $"F11: the return deletes a2 + a1′ and A′ omits them: \"{top[2].Omits}\"");
        var a = Kids(top[0]);
        Check(a.Length == 4 && string.Join(",", a.Select(x => x.Label)) == "a1,a2,a1′,a3" && a.All(x => x.Kind == PhraseKind)
            && Near(a[0].Start, 0, 0) && Near(a[0].End, 10.5, .5) && Near(a[1].End, 16.5, .5) && Near(a[2].End, 26.5, .5) && Near(a[3].End, 41.5, .5)
            && a[2].Prototype == a[0].Id && a[2].Return >= 0 && ret.All[a[2].Return].Level == 2 && !a[2].Projected,
            "F11: A's phrases are a1 a2 a1′ a3, the pickup and 5 + 3 + 5 + 7 bars, a1′ restating a1: " + Show(a));
        var sub = Kids(a[3]);
        Check(sub.Length == 2 && sub.All(x => x.Kind == SubPhraseKind) && sub[0].Label == "a3a" && sub[1].Label == "a3b" && Near(sub[1].Start, 32.5, 1),
            "F11: a3 splits into two sub-phrases at the sigh head, 17.1 (beat 32.5): " + Show(sub));
        // B1 = (U²)² ↑1 as §15.1 E6 states it for the étude: a Power node in B1 at its start, Count 2, StepSteps +1 (tonal), whose
        // two iterations are powers of Count 2 with step 0 and 4-beat units.
        var periods = Kids(top[1]);
        var b1 = periods.Length > 0 ? periods[0] : null;
        var power = b1 == null ? null : Kids(b1).FirstOrDefault(x => x.Kind == PowerKind);
        var inner = power == null ? Array.Empty<global::Hierarchy.Node>() : Kids(power);
        Check(periods.Length >= 2 && periods.All(x => x.Kind == PeriodKind) && b1.Label == "B1" && Near(b1.Start, 41.5, 0)
            && power != null && Near(power.Start, 41.5, 1) && power.Count == 2 && power.StepSteps == 1 && !power.StepReal && power.Power >= 0 && tree.PowerNode[power.Power] == power.Id
            && inner.Length == 2 && inner.All(x => x.Kind == PowerKind && x.Count == 2 && x.StepSteps == 0 && x.StepSemitones == 0 && Kids(x).All(u => u.Kind == UnitKind && u.End - u.Start == 4 * MotifPpq))
            && inner[1].Prototype == inner[0].Id && inner[1].Steps == 1 && System.Text.RegularExpressions.Regex.IsMatch(power.Text, @"^\(\S+²\)² ↑1$")
            && b1.Text.StartsWith(power.Text, StringComparison.Ordinal),
            $"F11: B1 is (U²)² ↑1, a power of two powers of two 2-bar units a step apart: {Show(periods)}; {power?.Text}; B1 = {b1?.Text}");
        Check(it.Relations.Any(r => r.Kind == PreparedPatternSong.MotifRelations.Run && r.Period == 6 && r.Symmetry.SequenceEqual(new[] { 0, 6 }) && periods.Any(p => r.Start >= p.Start && r.End <= p.End)),
            "F11: the left hand's tritone run (Period 6, Symmetry {0, 6}) lies in a period of B");
        var reprise = Kids(top[2]);
        Check(reprise.Length == 3 && reprise[0].Label == "a1" && reprise[0].Projected && reprise[0].Prototype == a[0].Id && Near(reprise[0].Start, 82.5, 0) && Near(reprise[0].End, 92.5, .5)
            && reprise[1].Label == "a3′" && reprise[1].Projected && reprise[1].Prototype == a[3].Id && Near(reprise[1].End, 104.5, 1)
            && reprise[2].Kind == CodaKind && reprise[2].Label == "Coda" && Near(reprise[2].Start, 104.5, 1) && Near(reprise[2].End, 114.5, 0)
            && Kids(reprise[1]).Length == 2 && Kids(reprise[1]).All(x => x.Projected && x.Kind == SubPhraseKind),
            "F11: A′ = a1 (projected) · a3′ (projected, without its cadence, its sub-phrases too) · Coda: " + Show(reprise));
        var coda = reprise[2];
        var codaCells = lex.Of(input.Streams.Principal).Where(c => c.Start >= coda.Start && c.End <= coda.End).ToArray();
        Check(codaCells.Count(c => c.Paradigm >= 0 && c.G.Steps == -7 && c.Sparks.Any(s => s.Kind == Altered && s.Pitch == 56 && s.Tag.Contains('♭') && s.Tag.Contains('6'))) >= 2
            && codaCells.Any(c => c.Paradigm >= 0 && !c.Exact && c.Sparks.Any(s => s.Kind == Lengthened)),
            "F11: the coda holds two cells at Steps −7 whose A♭3 is named ♭6, and the lengthened-head variant");
        Check(tree.Formula.StartsWith("A · B · A′", StringComparison.Ordinal) && tree.Line.Contains("A′ = A", StringComparison.Ordinal) && tree.Line.Contains("6–13", StringComparison.Ordinal)
            && tree.ReturnText[top[2].Return].StartsWith("A′ restates A from bar 1, leaving out bars 6–13 (a2, a1′)", StringComparison.Ordinal),
            $"F11: the formula starts with A · B · A′ and the caption reads A′ = A − bars 6–13: \"{tree.Formula.Split('\n')[0]}\", \"{tree.Line}\", \"{tree.ReturnText[top[2].Return]}\"");
        Check(tree.CrossReferences.Any(x => Near(x.Start, 30.5, 0) && Near(x.End, 32.5, 0) && Near(x.SourceStart, 10.5, 0) && x.G.Steps == 5 && x.G.Frame != Transform.Real && x.Text.Contains("sixth", StringComparison.Ordinal)),
            "F11: bar 16 = bar 6 a sixth higher (a cross-reference): " + string.Join("; ", tree.CrossReferences.Select(x => x.Text)));

        // F13: with a1′ equal to a1 the deletion is still a2 + a1′, and A′ omits them by those labels.
        (input, lex, ret, it, tree) = Tree(Ingest(TernaryMotifScore(plainNine: true), out var f13Data), f13Data, "F13", verbose);
        nodes = tree.Nodes; top = Kids(nodes[0]); a = top.Length > 0 ? Kids(top[0]) : a;
        deleted = Deleted(ret);
        Check(top.Length == 3 && top[2].Omits == "a2, a1′" && string.Join(",", a.Select(x => x.Label)) == "a1,a2,a1′,a3" && a[2].Prototype == a[0].Id
            && deleted.Length == 1 && Math.Abs(deleted[0].start - 10.5) <= 1 && Math.Abs(deleted[0].end - 26.5) <= 1,
            $"F13: with a1′ = a1 the deletion is a2 + a1′ and A′ omits them: \"{(top.Length == 3 ? top[2].Omits : "")}\"; {Show(a)}");

        // F12: a head-deleted reprise. Level 1 A B A′; the return deletes x, A′ omits x's label and restates y's phrases.
        (input, lex, ret, it, tree) = Tree(Ingest(HeadDeletedScore(), out var f12Data), f12Data, "F12", verbose);
        nodes = tree.Nodes; top = Kids(nodes[0]);
        var x0 = top.Length == 3 ? Kids(top[0]) : Array.Empty<global::Hierarchy.Node>();
        var reprised = top.Length == 3 ? Kids(top[2]) : Array.Empty<global::Hierarchy.Node>();
        Check(top.Length == 3 && string.Join(",", top.Select(x => x.Label)) == "A,B,A′" && x0.Length > 0 && Near(x0[0].Start, 0, 0) && Near(x0[0].End, 16, 0)
            && top[2].Omits == x0[0].Label && reprised.Length >= 2 && reprised[^1].Kind == CodaKind && reprised.Take(reprised.Length - 1).All(x => x.Projected && x.Prototype > x0[0].Id),
            $"F12: A′ deletes x and omits its label \"{(x0.Length > 0 ? x0[0].Label : "")}\": omits \"{(top.Length == 3 ? top[2].Omits : "")}\"; {Show(reprised)}");

        // F21 (noise) and F19 (pop): the tree holds its shape; pop sections stay level 1, copies inherit their phrases.
        Tree(Ingest(Noisy(TernaryMotifScore(), new Random(7)), out var f21Data, song: new SongSettings { PatternTimingToleranceBeats = .125 }), f21Data, "F21", verbose);
        var (popInput, popData) = PopMotifInput();
        (_, _, ret, _, tree) = Tree(popInput, popData, "F19", verbose);
        nodes = tree.Nodes; top = Kids(nodes[0]);
        Check(top.Length == popData.Sections.Length && top.All(x => x.Kind == SectionKind) && string.Join(",", top.Select(x => x.Label)) == "Intro,Verse 1,Chorus 1,Verse 2,Chorus 2,Bridge,Chorus 3,Outro"
            && top[3].Prototype == top[1].Id && top[4].Prototype == top[2].Id && top[6].Prototype == top[2].Id && top[6].Semitones == 2 && top[3].Return == top[4].Return
            && top[3].ChildCount == top[1].ChildCount && Kids(top[3]).All(x => x.Projected) && Kids(top[1]).All(x => x.Kind == PhraseKind),
            "F19: the eight pop sections are level 1, (V2 C2) and C3 inherit the phrases of V1 and C1: " + Show(top) + "; " + Show(Kids(top[3])));
    }

    // F12: a head-deleted reprise in 2/4: x (8 bars, a half cadence) y (12, a full cadence) | B (32 bars of new material) |
    // y (12) coda (4). B is 32 bars, not §16's 16: the head x joins the hull only within min_lag = L/8 of the first downbeat,
    // and among 52 bars its 8 would make it an Intro (see the deviations).
    static Score HeadDeletedScore()
    {
        var f12 = new Score(); Bars(f12, 68, 2);
        const string x = "E4/8 G4/8 C5/4 B4/8 A4/8 G4/4 A4/8 C5/8 F5/4 E5/8 D5/8 C5/4 D5/8 E5/8 F5/8 D5/8 C5/8 B4/8 A4/4 G4/8 A4/8 B4/8 C5/8 D5/2";
        const string y = "G5/8 F5/8 E5/8 D5/8 C5/4 E5/4 A4/8 B4/8 C5/8 D5/8 E5/4 C5/4 F5/8 E5/8 D5/8 C5/8 B4/4 G4/4 A4/8 G4/8 F4/8 E4/8 D4/4 G4/4 C5/8 D5/8 E5/8 F5/8 G5/4 E5/4 D5/8 C5/8 B4/8 D5/8 C5/2";
        const string yBass = "C3/2 A2/2 F2/2 C3/2 D3/2 G2/2 F2/2 G2/2 A2/2 E2/2 G2/2 C2/2";
        const float bassVelocity = 80 / 127f;
        Line(f12, 0, x); Line(f12, 0, "C3/4 E3/4 G2/4 B2/4 F2/4 A2/4 C3/4 E3/4 F2/4 D2/4 A2/4 F2/4 G2/2 G2/2", 2, 2, bassVelocity);
        Line(f12, 16, y); Line(f12, 16, yBass, 2, 2, bassVelocity);
        var random = new Random(12); string[] minor = { "A", "B", "C", "D", "E", "F", "G#" };
        for (int b = 0; b < 32; b++)
        {
            double start = 40 + 2 * b;
            for (int k = 0; k < 4; k++) f12.Note(start + k / 2.0, .5, ScorePitch(minor[random.Next(7)] + (5 + random.Next(2))), 96 / 127f, 3, 3);
            f12.Note(start, 2, ScorePitch(minor[random.Next(7)] + "2"), bassVelocity, 2, 2);
        }
        Line(f12, 104, y); Line(f12, 104, yBass, 2, 2, bassVelocity);
        Line(f12, 128, "C5/4 G4/4 E4/4 C4/4 G4/2 C4/2"); Line(f12, 128, "C3/2 C3/2 G2/2 C2/2", 2, 2, bassVelocity);
        return f12;
    }

    // F19's input: the existing PopScore prepared as PatternPrep prepares a pop song (form, sections, melody), C major.
    static (MotifInput input, PreparedPatternSong data) PopMotifInput()
    {
        var names = new[] { "", "Keys", "Bass", "Lead Vocal", "Drums" };
        var cycles = PopScore().Cycles();
        var form = FormAnalysis.Build(cycles, new SongSettings { Key = 3 }, names);
        var data = PreparedPatternSong.Capture(cycles, form.Form);
        data.TrackNames = names; data.Style = form.Style; data.Key = 3; data.LeadVocalTrack = 3;
        data.Tempos = new[] { new PreparedPatternSong.Tempo { Beat = 0, Seconds = 0, Microseconds = 500000 } };
        for (int i = 0; i < data.Sections.Length; i++) { var info = form.Sections[i]; data.Sections[i].Label = info.Label; data.Sections[i].Role = info.Role; data.Sections[i].Letter = info.Letter; data.Sections[i].PhraseBars = info.PhraseBars; }
        FormAnalysis.Apply(form, data);
        MelodySelection.Build(data);
        return (MotifInput.Read(data, cycles, form.Grid, MotifSettings.FromSong(new SongSettings { Key = 3 }), null, MotifPpq), data);
    }

    // F21's perturbation of a score (§16): each note dropped with probability 0.03, else its onset moved by a normal jitter of
    // σ = ⅛ beat and rounded to the sixteenth (never before 0); then 2% of the kept notes (rounded) struck again a semitone or
    // a whole tone either way, or an octave up.
    static Score Noisy(Score clean, Random random)
    {
        var s = new Score { Beat = clean.Beat }; s.Bars.AddRange(clean.Bars);
        var kept = new List<MidiCycleAnalysis.Hit>();
        foreach (var n in clean.Notes)
        {
            if (random.NextDouble() < .03) continue;
            double u1 = 1 - random.NextDouble(), u2 = random.NextDouble(), jitter = .125 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            double beat = Math.Max(0, Math.Round((n.Beat + jitter) * 4, MidpointRounding.AwayFromZero) / 4);
            kept.Add(new MidiCycleAnalysis.Hit { Beat = beat, Length = n.Length, Pitch = n.Pitch, Velocity = n.Velocity, Channel = n.Channel, Track = n.Track });
        }
        int extra = (int)Math.Round(.02 * kept.Count); int[] moves = { -1, 1, -2, 2, 12 };
        for (int i = 0; i < extra; i++)
        {
            var n = kept[random.Next(kept.Count)];
            kept.Add(new MidiCycleAnalysis.Hit { Beat = n.Beat, Length = n.Length, Pitch = n.Pitch + moves[random.Next(moves.Length)], Velocity = n.Velocity, Channel = n.Channel, Track = n.Track });
        }
        s.Notes.AddRange(kept);
        return s;
    }
}
