using System.Text.Json;
using System.Text.Json.Serialization;

// Every parameter of the motif reduction (Docs/MOTIF-REDUCTION.md §10, with the ingest constants of §3), so one
// value describes a run. song.json may override any of them under a "Motifs" object. That is for experiments
// only: no library song needs one, and an unknown name is an error rather than a silent default. The two values
// the analysis takes from the song itself (the transcription tolerance and the score's lead-in) come from
// SongSettings and cannot be overridden here.
public sealed record MotifSettings
{
    // §3 ingest
    public int SlotsPerTactus { get; init; } = 8;                 // slot = tactus/8: 32nds at a quarter tactus, where the étude's 10:8 tuplet and graces round cleanly
    public double ImportMatch { get; init; } = .98;               // a score import is trusted when this share of the notes matches one to one
    public int VoiceMinNotes { get; init; } = 8;                  // a score voice with fewer notes is not a stream of its own
    public double LineRankCost { get; init; } = 4;                // principal-line Viterbi: per rank from the outer note, against |pitch − previous pitch|
    public int BassMedianPitch { get; init; } = 55;               // a lane whose median pitch lies below G3 is a bass lane
    public int[] GridDivisions { get; init; } = { 4, 3, 6, 8, 12 };   // transcription grids per tactus (U/4 … U/12), tried in this order
    public double GridPointCost { get; init; } = .02;             // per grid point of a bar, in quarters: a finer grid must earn its points
    public double SnapSigmas { get; init; } = 2;                  // snap within max(tolerance, 2σ̂) of the grid point
    // §5 cells
    public double[] WindowBars { get; init; } = { .5, 1, 2 };     // window lengths besides one tactus: the motif scales of every test piece
    public int WindowMaxEvents { get; init; } = 24;
    public int EmRounds { get; init; } = 4;                       // converged by round 3 on the étude
    public double FuzzyDistance { get; init; } = .2;              // per event; each guard removed a false identity in a prototype
    public int FuzzyMinEvents { get; init; } = 4;
    public int FuzzyEditDivisor { get; init; } = 3;               // edits ≤ max(1, ⌊events/3⌋)
    public double FuzzyMdlMargin { get; init; } = 2;              // L(g) + L(ρ) + 2 < Lit(w)
    public double OrnamentIndel { get; init; } = .3;              // a repercussion or ornament is cheap, so ornamented restatements stay members
    public double ChromaticShare { get; init; } = .3;             // spelling is unreliable above this share off the local scale: name relations as real
    // §6 iteration
    public int[] BlockUnitBars { get; init; } = { 1, 2, 4, 8 };
    public double BlockConsistency { get; init; } = .75;
    public int RunMinEvents { get; init; } = 6;                   // catches the tritone chains and alternating sixths, not chance pairs
    public int RunMaxPeriod { get; init; } = 2;
    public int DoublingMinOnsets { get; init; } = 8;
    public double DoublingShare { get; init; } = .75;             // an isolated undoubled onset does not end a doubling (the étude's bar 51)
    public double ContraryShare { get; init; } = .75;
    public int ContraryMinEvents { get; init; } = 4;
    public int LiquidationMinBars { get; init; } = 3;
    public double LiquidationEnd { get; init; } = .25;            // ends at ≤ ¼ of its starting period: 8 → 4 → 2
    public double LiquidationShare { get; init; } = .75;          // a bar's period: ≥ 75% of its principal line repeats at that lag (or as its neighbour)
    // §7 returns
    public double ThetaQuantile { get; init; } = .95;             // the piece's own background similarity
    public double ThetaMin { get; init; } = .45;
    public double ThetaMax { get; init; } = .90;
    public double GapOpen { get; init; } = 4;                     // × (1 − θ): an 8-bar omission costs 8 strong beats
    public double GapExtend { get; init; } = .25;                 // × (1 − θ) per beat
    public int MinLagDivisor { get; init; } = 8;                  // min_lag = max(1 bar, round(L/8)), the scale rule
    public int DiscoveryPaths { get; init; } = 40;                // Waterman–Eggert: at most 40 paths a node …
    public double DiscoveryStop { get; init; } = 3;               // … and none scoring below 3(1 − θ)
    public int BarColumnsAbove { get; init; } = 2500;             // above this many tactus columns discovery runs on bars (§11)
    public double PathTrim { get; init; } = .15;                  // trim path ends whose best match is below θ + 0.15
    public double StructuralScore { get; init; } = 6;             // × (1 − θ)
    public double StructuralBars { get; init; } = 2;              // span ≥ max(2 bars, L/16): no one-bar coincidences
    public int StructuralDivisor { get; init; } = 16;
    public double SlideDelta { get; init; } = 1;                  // × (1 − θ): one strong beat of near ties for gap sliding
    public double CopyShare { get; init; } = .55;                 // a copy explains ≥ 55% of itself …
    public double PrototypeShare { get; init; } = .30;            // … and ≥ 30% of its prototype: short tails are no copies
    public double CodaShare { get; init; } = .25;                 // a longer tail is a section
    // §8 hierarchy
    public double PhraseMinBars { get; init; } = 2;
    public double PeriodLambda { get; init; } = .5;               // × the median evidence
    public double PeriodKappa { get; init; } = 1.25;              // tuned from 1.0 (step 7): the Nocturne's B in 4 periods (§15.2 N3), the étude's 5 kept; 0.5 would split 38–41 | 42–45
    public int[] PeriodBars { get; init; } = { 2, 4, 8, 12, 16 };
    public double SubPhraseBars { get; init; } = 6;               // a phrase this long splits into sub-phrases …
    public double FamilyHoldBars { get; init; } = 2;              // … where a new family of the principal line holds this long
    public double CrossReferenceShare { get; init; } = .75;       // one element relates this share of a span's columns to an earlier one's
    // §9 export
    public int MaxCells { get; init; } = 40000;
    public int MaxBytes { get; init; } = 4_000_000;               // the Motifs JSON alone: bounded Unity load time and bundle growth
    public int MaxSparksPerCell { get; init; } = 8;
    // From the song (song.json's own fields), never from the "Motifs" object.
    [JsonIgnore] public double TimingToleranceBeats { get; init; }   // PatternTimingToleranceBeats: > 0 is transcription mode (§3 A3)
    [JsonIgnore] public double ScoreLeadInBeats { get; init; }       // where the score import's tick 0 lies in the MIDI
    [JsonIgnore] public bool ReviewedSections { get; init; }         // SectionBoundaries is set: data.Sections are level 1 (§7 E6)
    [JsonIgnore] public bool Transcription => TimingToleranceBeats > 0;

    static readonly JsonSerializerOptions Overrides = new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static MotifSettings FromSong(SongSettings song, JsonElement? motifs = null)
    {
        var settings = new MotifSettings();
        if (motifs is { ValueKind: JsonValueKind.Object } json)
        {
            // System.Text.Json skips ignored members silently; these belong to song.json itself.
            foreach (var p in json.EnumerateObject())
                if (p.Name.Equals(nameof(TimingToleranceBeats), StringComparison.OrdinalIgnoreCase) || p.Name.Equals(nameof(ScoreLeadInBeats), StringComparison.OrdinalIgnoreCase) || p.Name.Equals(nameof(Transcription), StringComparison.OrdinalIgnoreCase)
                    || p.Name.Equals(nameof(ReviewedSections), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"song.json \"Motifs\": {p.Name} comes from the song's own PatternTimingToleranceBeats, ScoreLeadInBeats and SectionBoundaries");
            try { settings = json.Deserialize<MotifSettings>(Overrides) ?? settings; }
            catch (JsonException e) { throw new ArgumentException("song.json \"Motifs\" overrides: " + e.Message, e); }
        }
        else if (motifs is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) }) throw new ArgumentException("song.json \"Motifs\" must be an object of parameter overrides");
        if (settings.SlotsPerTactus < 1 || settings.GridDivisions.Length == 0 || settings.WindowBars.Length == 0 || settings.DiscoveryPaths < 1 || settings.BarColumnsAbove < 2) throw new ArgumentException("song.json \"Motifs\": slots, grids, window lengths and path counts must be positive");
        return settings with { TimingToleranceBeats = Math.Max(0, song?.PatternTimingToleranceBeats ?? 0), ScoreLeadInBeats = song?.ScoreLeadInBeats ?? 0, ReviewedSections = !string.IsNullOrWhiteSpace(song?.SectionBoundaries) };
    }
}
