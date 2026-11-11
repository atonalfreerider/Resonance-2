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
        Console.WriteLine("PASS: motif contract (F24) — a version 6 layer round-trips with the §13.1 field names, HasMotifs is not written, a version 5 bundle reads as Schema 0");
        Console.WriteLine("PASS: motif ingest — tick grid, metric weights and printed bars after a silent lead-in, score import (scaled ticks, staves, voices, tpc, graces, directions), transcription snap, skyline and voice streams, principal stream; B♭ minor's leading tone is A (F28)");
        Console.WriteLine("PASS: motif transform group — costs, composition, images, stabilisers and orders; names: tonal sequence up a second (F3), the leading tone of A minor (F4), the parallel minor (F5)");
        Console.WriteLine("PASS: motif reduction (F25) — diminution tokens E4[N− Rep] F4[N+ Rep Rep], appoggiaturas and turns; arrivals at bars 4 (half), 8, 12 (half), 16 cut a period into 4 + 4 + 4 + 4, never at a decorated held note");
        Console.WriteLine("PASS: motif cells (F1–F6, F14, F18, F26, F27) — neighbour chirality, exact mirror, tonal and tonicizing sequences, parallel minor, modal mixture, ornamented and lengthened-head variants, distinct figures, phase on beat 2; every member decodes from its prototype");
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

    static MotifInput Ingest(Score score, int key = 3, bool minor = false, SongFormAnalysis.ChordStep[] chords = null, SongSettings song = null, string folder = null, string[] names = null)
    {
        var cycles = score.Cycles();
        var harmony = ChordTimeline.Build(cycles, key, minor);
        var data = new PreparedPatternSong
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
}
