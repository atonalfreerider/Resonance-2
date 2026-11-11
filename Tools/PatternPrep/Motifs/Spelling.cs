// Spelling, local keys and chord frames (Docs/MOTIF-REDUCTION.md §3 A5). Spelling lives on the line of fifths
// (C 0, G 1, D 2 …, F −1, B♭ −2): a note's letter and accidental follow from its position there, and a key's
// collection is seven neighbouring positions. Spelling feeds names and diatonic steps, never a boundary.
public static class Spelling
{
    public const string Sharp = "♯", Flat = "♭", Natural = "♮";
    static readonly int[] NaturalPc = { 0, 2, 4, 5, 7, 9, 11 };
    const string Letters = "CDEFGAB";
    public static int Mod(int n, int m) => (n % m + m) % m;
    public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    public static int Letter(int fifths) => Mod(4 * fifths, 7);              // C 0, D 1, E 2, F 3, G 4, A 5, B 6
    public static int Accidental(int fifths) => FloorDiv(fifths + 1, 7);     // F … B natural, F♯ … B♯ sharp, F♭ … B♭ flat
    public static int PitchClass(int fifths) => Mod(7 * fifths, 12);
    // Morphetic pitch: 7·octave + letter, the octave of the spelled note (B♯3 and C4 are both MIDI 60: 27 and 28).
    public static int Morph(int pitch, int fifths)
    {
        int letter = Letter(fifths);
        return 7 * (FloorDiv(pitch - NaturalPc[letter] - Accidental(fifths), 12) - 1) + letter;
    }
    public static int NaturalPitch(int morph) => 12 * (FloorDiv(morph, 7) + 1) + NaturalPc[Mod(morph, 7)];
    // The spelling of pitch class pc nearest a centre on the line of fifths (its positions are 12 apart); an exact
    // tie takes the sharp side.
    public static int Nearest(int pc, double centre)
    {
        int f = Mod(7 * pc, 12);
        f += 12 * (int)Math.Floor((centre - f) / 12);
        return Math.Abs(f + 12 - centre) <= Math.Abs(f - centre) + 1e-9 ? f + 12 : f;
    }
    public static string Accidentals(int a) => a switch { 0 => "", 1 => Sharp, 2 => "𝄪", -1 => Flat, -2 => "𝄫", _ => a > 0 ? string.Concat(Enumerable.Repeat(Sharp, a)) : string.Concat(Enumerable.Repeat(Flat, -a)) };
    // A note's name; signature is the key's accidental on its letter, so C in E major is C♮.
    public static string Name(int fifths, int signature = 0)
    {
        int a = Accidental(fifths);
        return Letters[Letter(fifths)] + (a == 0 && signature != 0 ? Natural : Accidentals(a));
    }

    // The line-of-fifths speller (§3 A5) for every pitched note the score did not spell. A pitch class with a spelling
    // inside the local key's span (its major collection, tonic −1 … +5, or the harmonic-minor span −4 … +5) takes it:
    // the leading tone of B♭ minor is A, not B𝄫, and B♭ in D♭ major is never C𝄫. Any other (chromatic) note takes the
    // spelling nearest a centre of gravity: the exponentially weighted mean of the positions heard before, halved per
    // tactus beat and re-seeded at every local key's start at the centre of its collection (tonic + 2 in major, + 0.5
    // in minor). The centre alone drifts flat in flat-heavy passages: on the Nocturne it spelled 17 of B♭ minor's 49
    // leading tones B𝄫. A chord is spelled as one, against the centre before it. Then every region's tonic is spelled
    // as its notes are, and morphs and alterations follow.
    public static void Spell(MotifEvent[] ev, MotifGrid grid, KeyFrames frames)
    {
        double sum = 0, weight = 0; long last = 0; int region = -1;
        for (int i = 0; i < ev.Length;)
        {
            long tick = ev[i].Tick; int j = i; while (j < ev.Length && ev[j].Tick == tick) j++;
            int r = frames.Region(tick);
            if (r != region) { region = r; sum = frames.Centre(r); weight = 1; }
            else { double decay = Math.Pow(.5, (tick - last) / (double)grid.TactusAt(tick)); sum *= decay; weight *= decay; }
            double centre = weight > 1e-9 ? sum / weight : frames.Centre(r);
            int low = frames.SpanLow(r), high = frames.SpanHigh(r);
            for (int k = i; k < j; k++)
            {
                if (ev[k].Drum || ev[k].Spelled) continue;
                int pc = Mod(ev[k].Pitch, 12), inside = low + Mod(7 * pc - low, 12);
                ev[k].Fifths = inside <= high ? inside : Nearest(pc, centre);
            }
            for (int k = i; k < j; k++) if (!ev[k].Drum) { sum += ev[k].Fifths; weight += 1; }
            last = tick; i = j;
        }
        frames.FitTonics(ev);
        for (int k = 0; k < ev.Length; k++)
        {
            if (ev[k].Drum) continue;
            ev[k].Morph = Morph(ev[k].Pitch, ev[k].Fifths);
            ev[k].Alter = frames.Alter(ev[k].Pitch, ev[k].Morph, frames.Region(ev[k].Tick));
        }
    }
}

// The local keys and chords a note sounds in (§3 A5). Regions come from KeyAnalysis.Regions, computed unlocked even
// when the song's key is reviewed; they serve spelling, naming and arrivals only and are never written to
// KeyChanges. Keys and chord roots are A-based like the rest of PatternPrep (pitch class = key + 9); Tonic is the
// tonic's position on the line of fifths. A key's scale is its signature's collection (natural minor in minor
// keys), so a minor key's leading tone is an alteration: the one §5 C6 names "the leading tone of …".
public sealed class KeyFrames
{
    public long[] Start = Array.Empty<long>(), End = Array.Empty<long>();
    public int[] Key = Array.Empty<int>(), Tonic = Array.Empty<int>();
    public bool[] Minor = Array.Empty<bool>();
    public long[] ChordStart = Array.Empty<long>(), ChordEnd = Array.Empty<long>();
    public int[] ChordRoot = Array.Empty<int>();              // A-based, −1 for a rest
    public string[] ChordQuality = Array.Empty<string>();
    long[] bassStart = Array.Empty<long>(), bassEnd = Array.Empty<long>();
    int[] bass = Array.Empty<int>();
    int[][] signature = Array.Empty<int[]>();                 // per region: the accidental its collection gives each letter
    public int Regions => Start.Length;
    public int Chords => ChordStart.Length;

    public static KeyFrames Build(PreparedPatternSong data, ChordTimeline.Grid harmony, MotifGrid grid)
    {
        int ppq = grid.Ppq;
        var chords = (data.Chords ?? Array.Empty<SongFormAnalysis.ChordStep>()).Where(c => c.End > c.Start).OrderBy(c => c.Start).ToList();
        double beatsPerBar = grid.Bars == 0 ? 4 : Enumerable.Range(0, grid.Bars).GroupBy(b => grid.BarEnd[b] - grid.BarStart[b]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key / (double)ppq;
        // An unknown key lets the chords choose; with no chords at all, C major (no signature) rather than KeyAnalysis's A.
        var regions = KeyAnalysis.Regions(chords, data.Key >= 0 || chords.Count > 0 ? data.Key : 3, data.Minor && data.Key >= 0, beatsPerBar);
        int n = regions.Count;
        var f = new KeyFrames { Start = new long[n], End = new long[n], Key = new int[n], Tonic = new int[n], Minor = new bool[n], signature = new int[n][] };
        for (int r = 0; r < n; r++)
        {
            f.Start[r] = r == 0 ? long.MinValue : MotifInput.Ticks(regions[r].Start, ppq);
            f.End[r] = r == n - 1 ? long.MaxValue : MotifInput.Ticks(regions[r].End, ppq);
            f.Key[r] = Spelling.Mod(regions[r].Key, 12); f.Minor[r] = regions[r].Minor;
            // The first key as its signature is usually written; each later one spelled nearest the key it leaves, so a
            // modulation from D♭ major goes to G♭ major, not F♯ (the line of fifths has no enharmonic seam inside a piece).
            int pc = Spelling.Mod(regions[r].Key + 9, 12);
            f.SetTonic(r, r == 0 ? DefaultTonic(pc, regions[r].Minor) : Spelling.Nearest(pc, f.Tonic[r - 1]));
        }
        f.ChordStart = chords.Select(c => MotifInput.Ticks(c.Start, ppq)).ToArray(); f.ChordEnd = chords.Select(c => MotifInput.Ticks(c.End, ppq)).ToArray();
        f.ChordRoot = chords.Select(c => c.Root < 0 ? -1 : Spelling.Mod(c.Root, 12)).ToArray(); f.ChordQuality = chords.Select(c => (c.Quality ?? "").Trim()).ToArray();
        if (harmony != null && harmony.Count > 0)
        {
            f.bassStart = harmony.Start.Select(b => MotifInput.Ticks(b, ppq)).ToArray(); f.bassEnd = harmony.End.Select(b => MotifInput.Ticks(b, ppq)).ToArray();
            f.bass = harmony.Bass.Select(b => b == int.MaxValue ? -1 : b).ToArray();
        }
        return f;
    }

    // A key's tonic as its signature is usually written: majors from D♭ (5 flats) to F♯ (6 sharps), minors from
    // E♭ minor (6 flats) to G♯ minor (5 sharps). FitTonics then follows the score's own spelling.
    static int DefaultTonic(int pc, bool minor)
    {
        int f = Spelling.Mod(7 * pc, 12);
        int low = minor ? -3 : -5;
        while (f > low + 11) f -= 12;
        while (f < low) f += 12;
        return f;
    }
    void SetTonic(int r, int tonic)
    {
        Tonic[r] = tonic;
        int low = tonic - (Minor[r] ? 4 : 1);
        signature[r] = new int[7];
        for (int k = 0; k < 7; k++) signature[r][Spelling.Letter(low + k)] = Spelling.Accidental(low + k);
    }
    // Each region's tonic takes the spelling (12 apart on the line) whose span holds most of the region's notes: G♭
    // major in a score written in G♭, F♯ major in one written in F♯.
    public void FitTonics(MotifEvent[] ev)
    {
        var count = new int[Regions, 3];
        foreach (var e in ev)
        {
            if (e.Drum) continue;
            int r = Region(e.Tick);
            for (int k = 0; k < 3; k++) { int shift = 12 * (k - 1); if (e.Fifths >= SpanLow(r) + shift && e.Fifths <= SpanHigh(r) + shift) count[r, k]++; }
        }
        for (int r = 0; r < Regions; r++)
        {
            int best = 1;
            if (count[r, 0] > count[r, best]) best = 0;
            if (count[r, 2] > count[r, best]) best = 2;
            if (best != 1) SetTonic(r, Tonic[r] + 12 * (best - 1));
        }
    }
    // The key's span on the line of fifths: its major collection (tonic −1 … +5) or the harmonic-minor span (−4 … +5).
    public int SpanLow(int r) => r < 0 ? -1 : Tonic[r] - (Minor[r] ? 4 : 1);
    public int SpanHigh(int r) => r < 0 ? 5 : Tonic[r] + 5;

    public int Region(long tick)
    {
        if (Regions == 0) return -1;
        int i = Array.BinarySearch(Start, tick);
        return Math.Max(0, i >= 0 ? i : ~i - 1);
    }
    public double Centre(int r) => r < 0 ? 2 : Tonic[r] + (Minor[r] ? .5 : 2);
    public int TonicPc(int r) => r < 0 ? 0 : Spelling.PitchClass(Tonic[r]);
    public int DominantPc(int r) => Spelling.Mod(TonicPc(r) + 7, 12);
    public int ScalePitch(int morph, int r) => Spelling.NaturalPitch(morph) + (r < 0 ? 0 : signature[r][Spelling.Mod(morph, 7)]);
    public int Alter(int pitch, int morph, int r) => pitch - ScalePitch(morph, r);
    // Scale degree above the tonic (0 = tonic), by letter: the glyph's angle on the 7-clock.
    public int Degree(int morph, int r) => Spelling.Mod(Spelling.Mod(morph, 7) - Spelling.Letter(r < 0 ? 0 : Tonic[r]), 7);
    public int Signature(int letter, int r) => r < 0 ? 0 : signature[r][letter];
    // A pitch class spelled as this region's key would write it.
    public int Spell(int pc, int r) => Spelling.Nearest(pc, Centre(r));
    public string KeyName(int r) => r < 0 ? "C major" : Spelling.Name(Tonic[r]) + (Minor[r] ? " minor" : " major");
    public string NoteName(int fifths, int r) => Spelling.Name(fifths, Signature(Spelling.Letter(fifths), r));

    // The chord step sounding at tick (−1 between steps or before the first).
    public int Chord(long tick)
    {
        if (Chords == 0) return -1;
        int i = Array.BinarySearch(ChordStart, tick); i = i >= 0 ? i : ~i - 1;
        return i >= 0 && tick < ChordEnd[i] ? i : -1;
    }
    public int RootPc(int chord) => chord < 0 || ChordRoot[chord] < 0 ? -1 : Spelling.Mod(ChordRoot[chord] + 9, 12);
    // Major (+1: major, dominant- and major-seventh chords), minor (−1) or neither (0): what the parallel mode compares.
    public int Mode(int chord) => chord < 0 || ChordRoot[chord] < 0 ? 0 : ChordQuality[chord] switch { "" or "7" or "maj7" => 1, "m" or "m7" => -1, _ => 0 };
    // The lowest note sounding in the tactus step at tick (Grid.Bass), −1 when unknown.
    public int Bass(long tick)
    {
        if (bassStart.Length == 0) return -1;
        int i = Array.BinarySearch(bassStart, tick); i = i >= 0 ? i : ~i - 1;
        return i >= 0 && tick < bassEnd[i] ? bass[i] : -1;
    }

    // An altered note named by the regional key (rule 3 of §5 C6): a lowered 3, 6 or 7 in a major key is borrowed from
    // its parallel minor; a raised note that is the leading tone of the local key, or of a key a chord in [from, to)
    // tonicizes (a secondary dominant or leading-tone chord), is that leading tone. Anything else is named by its note.
    public Alteration Explain(int note, int pitch, int fifths, int morph, int delta, long tick, long from, long to)
    {
        int r = Region(tick), alter = Alter(pitch, morph, r), pc = Spelling.Mod(pitch, 12);
        if (alter == -1 && r >= 0 && !Minor[r])
        {
            int degree = Degree(morph, r) + 1;
            if (degree is 3 or 6 or 7) return new Alteration(note, pitch, delta, Alteration.Borrowed, $"{Spelling.Flat}{degree} borrowed from {Spelling.Name(Tonic[r])} minor");
        }
        if (alter == 1 && r >= 0)
        {
            if (pc == Spelling.Mod(TonicPc(r) - 1, 12)) return new Alteration(note, pitch, delta, Alteration.LeadingTone, "the leading tone of " + KeyName(r));
            int c = Chord(from); if (c < 0) { c = Array.BinarySearch(ChordEnd, from); c = c >= 0 ? c + 1 : ~c; }
            for (; c >= 0 && c < Chords && ChordStart[c] < to; c++)
            {
                if (ChordRoot[c] < 0) continue;
                var (kind, target, targetMinor, _) = KeyAnalysis.Explain(ChordRoot[c], ChordQuality[c], Key[r], Minor[r]);
                if (kind is not (KeyAnalysis.Kind.Secondary or KeyAnalysis.Kind.LeadingTone)) continue;
                int t = Spelling.Mod(target + 9, 12);
                if (pc == Spelling.Mod(t - 1, 12))
                    return new Alteration(note, pitch, delta, Alteration.LeadingTone, $"the leading tone of {Spelling.Name(Spell(t, r))} {(targetMinor ? "minor" : "major")} (the sequence tonicizes it)");
            }
        }
        return new Alteration(note, pitch, delta, Alteration.Unexplained, NoteName(fifths, r));
    }
}
