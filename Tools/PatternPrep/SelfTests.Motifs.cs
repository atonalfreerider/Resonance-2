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
        Console.WriteLine("PASS: motif contract (F24) — a version 6 layer round-trips with the §13.1 field names, HasMotifs is not written, a version 5 bundle reads as Schema 0");
        Console.WriteLine("PASS: motif ingest — tick grid, metric weights and printed bars after a silent lead-in, score import (scaled ticks, staves, voices, tpc, graces, directions), transcription snap, skyline and voice streams, principal stream; B♭ minor's leading tone is A (F28)");
        Console.WriteLine("PASS: motif transform group — costs, composition, images, stabilisers and orders; names: tonal sequence up a second (F3), the leading tone of A minor (F4), the parallel minor (F5)");
    }

    // Line(score, startBeat, "C4/8 B3/16 C4/16 D4/16*5 …"): a monophonic line, note after note. A token is
    // pitch/denominator, with *n for n such units (D4/16*5 lasts five sixteenths); "r" rests and "E5+C5/8" strikes a
    // dyad. Returns the beat after the last note.
    static double Line(Score score, double beat, string notes, int track = 3, int channel = 3, float velocity = 96 / 127f)
    {
        foreach (string token in notes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int slash = token.LastIndexOf('/'); if (slash <= 0) throw new ArgumentException("Line: pitch/denominator, not " + token);
            string rhythm = token[(slash + 1)..]; int times = 1, star = rhythm.IndexOf('*');
            if (star >= 0) { times = int.Parse(rhythm[(star + 1)..]); rhythm = rhythm[..star]; }
            double length = 4.0 / int.Parse(rhythm) * times;
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
}
