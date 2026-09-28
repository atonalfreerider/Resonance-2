// Key changes and tonal tension from the chord timeline.
//
// A Viterbi pass over the chords chooses a key (24 states) for every chord. Diatonic chords
// fit their key for free (the tonic chord is rewarded, most when a cadence prepares it);
// chromatic chords that a key explains as a brief tonicization — secondary dominants (V/V),
// leading-tone chords (vii°/V), the Neapolitan (♭II) and chords borrowed from the parallel
// minor (♭VI, ♭VII, iv) — fit at a small cost, smaller still when they resolve where they
// point, and the chord they resolve to is then read as prepared. So a V/V that resolves to V
// and comes home, or a ♭II that falls to V, is the home key's own colour: a tension, never a
// change. Anything else is expensive. A key region must last at least eight bars to be a
// modulation; shorter excursions fold back into the key around them. Each chromatic chord inside a key region becomes a tension: the
// torus leans toward the key it points at until the next chord, then relaxes — unless a real
// key change to that key follows, which the tension completes.
public static class KeyAnalysis
{
    public sealed class Region { public double Start, End; public int Key; public bool Minor; }

    static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 }, MinorScale = { 0, 2, 3, 5, 7, 8, 10 };
    static char Triad(string quality) => quality switch
    {
        "m" or "m7" => 'm', "dim" => 'd', " tone" or " dyad" => '?', _ => 'M'
    };
    // The diatonic triad on each scale degree (relative to the tonic), natural and harmonic minor.
    static bool Diatonic(int rel, char triad, bool minor, string quality)
    {
        if (triad == '?') return (minor ? MinorScale : MajorScale).Contains(rel) || minor && rel == 11;
        if (!minor)
            return rel switch { 0 or 5 or 7 => triad == 'M', 2 or 4 or 9 => triad == 'm', 11 => triad == 'd', _ => false };
        return rel switch { 0 or 5 => triad == 'm', 2 => triad == 'd', 3 or 8 or 10 => triad == 'M', 7 => triad is 'M' or 'm', 11 => triad == 'd', _ => false };
    }

    public enum Kind { Diatonic, Secondary, LeadingTone, Neapolitan, Borrowed, Foreign }
    // How a key explains one chord, and where a tonicization points (as a key: root and mode).
    public static (Kind kind, int target, bool targetMinor, string label) Explain(int root, string quality, int key, bool minor)
    {
        if (root < 0) return (Kind.Diatonic, key, minor, "");
        int rel = HarmonyModel.Mod(root - key); char triad = Triad(quality);
        if (Diatonic(rel, triad, minor, quality) && !(quality == "7" && rel == 0 && !minor)) return (Kind.Diatonic, key, minor, "");
        var scale = minor ? MinorScale : MajorScale;
        string Numeral(int degree)
        {
            string[] major = { "I", "♭II", "ii", "♭III", "iii", "IV", "♯IV", "V", "♭VI", "vi", "♭VII", "vii" };
            string[] minorNames = { "i", "♭II", "ii", "III", "♯iii", "iv", "♯iv", "v", "VI", "♮vi", "VII", "vii" };
            return (minor ? minorNames : major)[HarmonyModel.Mod(degree)];
        }
        bool DegreeMinor(int degree) => !minor ? degree is 2 or 4 or 9 : degree is 0 or 5 or 7;
        // Secondary dominant: a major (or dominant-seventh) chord a fifth above a diatonic degree.
        if (triad == 'M')
        {
            int resolves = HarmonyModel.Mod(rel + 5);
            if (resolves != 0 && scale.Contains(resolves) && !(minor && resolves == 11))
                return (Kind.Secondary, HarmonyModel.Mod(key + resolves), DegreeMinor(resolves), $"V{(quality == "7" ? "7" : "")}/{Numeral(resolves)}");
            if (rel == 0 && quality == "7") return (Kind.Secondary, HarmonyModel.Mod(key + 5), minor, $"V7/{Numeral(5)}");
            if (rel == 1) return (Kind.Neapolitan, root, false, "♭II Neapolitan");
            if (!minor && rel is 3 or 8 or 10) return (Kind.Borrowed, HarmonyModel.Mod(key + 3), false, $"{Numeral(rel)} borrowed");
        }
        if (triad == 'm' && !minor && (rel == 5 || rel == 0)) return (Kind.Borrowed, HarmonyModel.Mod(key + 3), false, $"{(rel == 5 ? "iv" : "i")} borrowed");
        if (triad == 'd')
        {
            int resolves = HarmonyModel.Mod(rel + 1);
            if (resolves != 0 && scale.Contains(resolves)) return (Kind.LeadingTone, HarmonyModel.Mod(key + resolves), DegreeMinor(resolves), $"vii°/{Numeral(resolves)}");
        }
        return (Kind.Foreign, key, minor, "");
    }

    static SongFormAnalysis.ChordStep Near(IReadOnlyList<SongFormAnalysis.ChordStep> steps, int i, int step)
    {
        for (int k = i + step; k >= 0 && k < steps.Count; k += step) if (!steps[k].Rest) return steps[k];
        return null;
    }
    // Where a tonicization resolves: a secondary dominant or leading-tone chord to the chord it
    // points at, the Neapolitan or a borrowed chord to the dominant or the tonic.
    static bool Resolves(Kind kind, int target, SongFormAnalysis.ChordStep next, int key)
    {
        if (next == null || next.Rest) return false;
        int rel = HarmonyModel.Mod(next.Root - key);
        return kind switch { Kind.Secondary or Kind.LeadingTone => HarmonyModel.Mod(next.Root) == target, Kind.Neapolitan or Kind.Borrowed => rel is 7 or 0, _ => false };
    }
    // The cost of hearing chord i in a key, weighted by its length (capped at a bar).
    static double Cost(IReadOnlyList<SongFormAnalysis.ChordStep> steps, int i, int key, bool minor)
    {
        var c = steps[i]; if (c.Rest) return 0;
        var (kind, target, _, _) = Explain(c.Root, c.Quality, key, minor);
        int rel = HarmonyModel.Mod(c.Root - key); char triad = Triad(c.Quality);
        var next = Near(steps, i, 1); var prev = Near(steps, i, -1);
        bool resolved = Resolves(kind, target, next, key);
        // A major seventh on the dominant degree is no dominant: it carries the leading tone of
        // the key a fifth up (E♭maj7 is I in E♭, not V in A♭), so it is chromatic here.
        if (kind == Kind.Diatonic && rel == 7 && c.Quality == "maj7") kind = Kind.Borrowed;
        double cost = kind switch
        {
            Kind.Diatonic => 0,
            Kind.Secondary or Kind.LeadingTone => resolved ? .3 : .6,
            Kind.Neapolitan or Kind.Borrowed => resolved ? .3 : .7,
            _ => 2.2
        };
        if (kind == Kind.Diatonic && triad != '?')
        {
            if (rel == 0) cost -= .45;                                            // the tonic chord confirms the key
            if (rel == 7 && triad == 'M') cost -= .12;                            // so does its dominant
            if (prev != null)
            {
                var (pk, pt, _, _) = Explain(prev.Root, prev.Quality, key, minor); int prel = HarmonyModel.Mod(prev.Root - key);
                // A cadence: the tonic after its dominant, leading-tone chord or subdominant.
                if (rel == 0 && pk == Kind.Diatonic && prel is 5 or 7 or 11) cost -= .3;
                // A chord its own tonicization prepared (V/V then V, the Neapolitan then V) belongs to this key.
                if (Resolves(pk, pt, c, key)) cost -= .2;
            }
        }
        if (triad == '?') cost *= .5;
        return cost * Math.Min(4, c.End - c.Start);
    }

    // A change must be worth more than a few bars of chromatic colour, and the new key must
    // then hold for eight bars: a four-bar tonicization is a lean, not a modulation. The song's
    // own key is also the default: a passage on the dominant (IV V iii vi V/V V ♭VII V) reads in
    // the home key unless the new key is clearly better for as long as it lasts.
    const double ChangeCost = 5, MinimumBars = 8, HomeBonus = .1;

    // Key regions over the chord timeline, starting in the given key.
    public static List<Region> Regions(IReadOnlyList<SongFormAnalysis.ChordStep> chords, int initial, bool initialMinor, double beatsPerBar)
    {
        var steps = chords.Where(c => c.End > c.Start).ToList();
        if (steps.Count == 0) return new() { new Region { Start = 0, End = 0, Key = Math.Max(0, initial), Minor = initialMinor } };
        // An unknown starting key (initial < 0) lets the opening chords choose it.
        int states = 24, start = initial < 0 ? -1 : initial + (initialMinor ? 12 : 0);
        var cost = new double[steps.Count, states]; var back = new int[steps.Count, states];
        double Home(int i, int s) => s == start ? HomeBonus * Math.Min(4, steps[i].End - steps[i].Start) : 0;
        for (int s = 0; s < states; s++) cost[0, s] = (start < 0 || s == start ? 0 : ChangeCost) + Cost(steps, 0, s % 12, s >= 12) - Home(0, s);
        for (int i = 1; i < steps.Count; i++)
            for (int s = 0; s < states; s++)
            {
                double best = double.PositiveInfinity; int from = s;
                for (int p = 0; p < states; p++) { double v = cost[i - 1, p] + (p == s ? 0 : ChangeCost); if (v < best) { best = v; from = p; } }
                cost[i, s] = best + Cost(steps, i, s % 12, s >= 12) - Home(i, s); back[i, s] = from;
            }
        var path = new int[steps.Count]; int last = 0;
        for (int s = 1; s < states; s++) if (cost[steps.Count - 1, s] < cost[steps.Count - 1, last]) last = s;
        path[^1] = last; for (int i = steps.Count - 1; i > 0; i--) path[i - 1] = back[i, path[i]];
        var regions = new List<Region>();
        for (int i = 0; i < steps.Count; i++)
        {
            int key = path[i] % 12; bool minor = path[i] >= 12;
            if (regions.Count > 0 && regions[^1].Key == key && regions[^1].Minor == minor) regions[^1].End = steps[i].End;
            else regions.Add(new Region { Start = steps[i].Start, End = steps[i].End, Key = key, Minor = minor });
        }
        // A key change lands on its new tonic: a dominant (or leading-tone chord) at the start
        // of a new key that the old key already explains as pointing there is the tension that
        // leads into the change, so it stays in the old key.
        for (int r = 1; r < regions.Count; r++)
        {
            var (before, after) = (regions[r - 1], regions[r]);
            for (int i = steps.FindIndex(c => c.Start >= after.Start - 1e-6); i >= 0 && i + 1 < steps.Count && steps[i].End <= after.End + 1e-6; i++)
            {
                var c = steps[i];
                if (c.Rest || HarmonyModel.Mod(c.Root - after.Key) == 0) break;
                var (kind, target, _, _) = Explain(c.Root, c.Quality, before.Key, before.Minor);
                if (kind is not (Kind.Secondary or Kind.LeadingTone) || target != after.Key) break;
                before.End = after.Start = steps[i + 1].Start;
            }
        }
        regions.RemoveAll(r => r.End <= r.Start + 1e-9);
        // A key held for less than eight bars is a tonicization of the key around it.
        double minimum = MinimumBars * Math.Max(1, beatsPerBar);
        // The opening key (given, or the song's own) is never merged away: a short intro in
        // the home key before a modulation is still the home key. Unless it never really
        // sounded: a stated key the song leaves within two bars was simply wrong.
        bool keepFirst = initial >= 0 && regions[0].Key == initial && regions[0].Minor == initialMinor && regions[0].End - regions[0].Start >= 2 * Math.Max(1, beatsPerBar);
        for (bool merged = true; merged && regions.Count > 1;)
        {
            merged = false;
            int shortest = Enumerable.Range(keepFirst ? 1 : 0, regions.Count - (keepFirst ? 1 : 0)).OrderBy(r => regions[r].End - regions[r].Start).First();
            if (regions[shortest].End - regions[shortest].Start >= minimum) break;
            int into = shortest == 0 ? 1 : shortest - 1;
            if (into < shortest) regions[into].End = regions[shortest].End; else regions[into].Start = regions[shortest].Start;
            regions.RemoveAt(shortest); merged = true;
            for (int r = regions.Count - 1; r > 0; r--)
                if (regions[r].Key == regions[r - 1].Key && regions[r].Minor == regions[r - 1].Minor) { regions[r - 1].End = regions[r].End; regions.RemoveAt(r); }
        }
        regions[0].Start = 0;
        return regions;
    }

    static string Name(int key, bool minor) => HarmonyModel.Name(key) + (minor ? " minor" : " major");

    // Key changes between regions and tensions inside them.
    public static (PreparedPatternSong.KeyChange[] changes, PreparedPatternSong.Tension[] tensions) Describe(IReadOnlyList<SongFormAnalysis.ChordStep> chords, List<Region> regions, PreparedPatternSong.Section[] sections, double beatsPerBar = 4)
    {
        var changes = new List<PreparedPatternSong.KeyChange>();
        for (int r = 1; r < regions.Count; r++)
        {
            var (a, b) = (regions[r - 1], regions[r]);
            double bars = (b.End - b.Start) / Math.Max(1, beatsPerBar);
            var section = sections?.FirstOrDefault(s => Math.Abs(s.Start - b.Start) <= 4);
            string evidence = $"{Name(b.Key, b.Minor)} held {bars:0} bars";
            if (section != null && section.Transpose != 0) evidence += $" · {section.DisplayName} transposed {(HarmonyModel.Mod(section.Transpose) <= 6 ? "+" + HarmonyModel.Mod(section.Transpose) : "−" + (12 - HarmonyModel.Mod(section.Transpose)))}";
            changes.Add(new PreparedPatternSong.KeyChange { Beat = b.Start, Key = b.Key, Minor = b.Minor, From = a.Key, FromMinor = a.Minor, Evidence = evidence });
        }
        var tensions = new List<PreparedPatternSong.Tension>();
        foreach (var c in chords)
        {
            if (c.Rest || c.End <= c.Start) continue;
            var region = regions.LastOrDefault(r => r.Start <= c.Start + 1e-6) ?? regions[0];
            var (kind, target, targetMinor, label) = Explain(c.Root, c.Quality, region.Key, region.Minor);
            if (kind is Kind.Diatonic or Kind.Foreign) continue;
            // A tension completes when the key it points at arrives right after it. A passing
            // tonicization is the slightest lean; the one that leads into a real change leans further.
            var next = changes.FirstOrDefault(k => k.Beat >= c.Start - 1e-6 && k.Beat <= c.End + 4);
            bool completes = next != null && next.Key == target && next.Minor == targetMinor;
            float amount = kind switch { Kind.Secondary => .2f, Kind.LeadingTone => .18f, Kind.Neapolitan => .18f, _ => .12f };
            if (completes) amount *= 2;
            tensions.Add(new PreparedPatternSong.Tension { Start = c.Start, End = c.End, Target = target, TargetMinor = targetMinor, Kind = label, Amount = amount, Completes = completes });
        }
        return (changes.ToArray(), tensions.ToArray());
    }

    public static void SelfTest()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); }
        const int A = 0, B = 2, C = 3, Db = 4, D = 5, E = 7, F = 8, G = 10;
        List<SongFormAnalysis.ChordStep> Progression(params (int root, string quality)[] chords)
        {
            var list = new List<SongFormAnalysis.ChordStep>(); double beat = 0;
            foreach (var (root, quality) in chords) { list.Add(new SongFormAnalysis.ChordStep { Start = beat, End = beat + 4, Root = root, Quality = quality }); beat += 4; }
            return list;
        }
        // C major with V/V and a Neapolitan: tensions, no key change.
        var home = Progression((C, ""), (F, ""), (G, ""), (C, ""), (C, ""), (D, "7"), (G, ""), (C, ""), (C, ""), (Db, ""), (G, "7"), (C, ""), (A, "m"), (F, ""), (G, ""), (C, ""));
        var regions = Regions(home, C, false, 4);
        Check(regions.Count == 1 && regions[0].Key == C && !regions[0].Minor, "V/V and ♭II do not change the key");
        var (changes, tensions) = Describe(home, regions, null);
        Check(changes.Length == 0, "no key changes in C major");
        var vv = tensions.SingleOrDefault(t => t.Start == 20); var np = tensions.SingleOrDefault(t => t.Start == 36);
        Check(vv != null && vv.Kind == "V7/V" && vv.Target == G && !vv.Completes, "D7 in C is V7/V leaning to G and relaxing: " + vv?.Kind);
        Check(np != null && np.Kind == "♭II Neapolitan" && np.Target == Db && !np.Completes, "D♭ in C is the Neapolitan");
        // Modulation to G through its dominant: the key changes, and the pivot's tension completes it.
        // (The new key is heard as itself: G on the downbeats, cadences into it, C only as its IV.)
        var modulation = Progression((C, ""), (F, ""), (G, ""), (C, ""), (A, "m"), (D, "7"),
            (G, ""), (D, ""), (G, ""), (E, "m"), (C, ""), (D, "7"), (G, ""), (A, "m"), (D, "7"), (G, ""), (G, ""));
        regions = Regions(modulation, C, false, 4);
        (changes, tensions) = Describe(modulation, regions, null);
        Check(changes.Length == 1 && changes[0].Key == G && changes[0].From == C && changes[0].Beat == 24, "sustained G major after its dominant is a key change at the G chord: " + string.Join(",", changes.Select(k => $"{k.Key}@{k.Beat}")));
        Check(tensions.Any(t => t.Start == 20 && t.Target == G && t.Completes), "the D7 pivot's tension completes into G");
        // A chorus a whole step up for eight bars is a key change.
        var lift = Progression((C, ""), (G, ""), (A, "m"), (F, ""), (C, ""), (G, ""), (A, "m"), (F, ""), (D, ""), (A, ""), (B, "m"), (G, ""), (D, ""), (A, ""), (B, "m"), (G, ""));
        (changes, _) = Describe(lift, Regions(lift, C, false, 4), null);
        Check(changes.Length == 1 && changes[0].Key == D && changes[0].Beat == 32, "a section lifted a whole step changes the key");
        Check(Explain(E, "", A, true).label == "V/iv" || Explain(E, "", A, true).kind == Kind.Diatonic, "harmonic-minor V stays diatonic in minor");
        // Loops that come home never change the key, however often they turn: vi V/V V I, and a ♭II–V–I vamp.
        var loops = Progression(Enumerable.Repeat(new[] { (A, "m"), (D, ""), (G, ""), (C, "") }, 10).SelectMany(x => x).ToArray());
        (changes, tensions) = Describe(loops, Regions(loops, C, false, 4), null);
        Check(changes.Length == 0 && tensions.Count(t => t.Kind == "V/V") == 10 && tensions.All(t => !t.Completes && t.Amount < .25f), "ten vi–V/V–V–I loops stay in C: ten slight leans, no key change");
        var vamp = Progression(Enumerable.Repeat(new[] { (Db, ""), (G, "7"), (C, "") }, 8).SelectMany(x => x).ToArray());
        Check(Regions(vamp, C, false, 4).Count == 1 && Regions(vamp, C, false, 4)[0].Key == C, "a ♭II–V7–I vamp stays in C");
        // A four-bar visit to G inside C is a tonicization; twelve bars of G with its own cadences is a key change.
        var brief = Progression((C, ""), (F, ""), (G, ""), (C, ""), (G, ""), (C, ""), (D, ""), (G, ""), (C, ""), (F, ""), (G, ""), (C, ""), (C, ""), (F, ""), (G, ""), (C, ""));
        Check(Regions(brief, C, false, 4).Count == 1, "four bars in G inside C are a lean, not a change");
        var longer = Progression((C, ""), (F, ""), (G, ""), (C, ""), (G, ""), (D, ""), (G, ""), (E, "m"), (A, "m"), (D, ""), (G, ""), (G, ""), (G, ""), (D, ""), (E, "m"), (D, ""), (C, ""), (F, ""), (G, ""), (C, ""), (C, ""), (F, ""), (G, ""), (C, ""));
        var longerChanges = Describe(longer, Regions(longer, C, false, 4), null).changes;
        Check(longerChanges.Length == 2 && longerChanges[0].Key == G && longerChanges[0].Beat == 16 && longerChanges[1].Key == C, "twelve bars of G with cadences change the key and come back: " + string.Join(",", longerChanges.Select(k => $"{HarmonyModel.Name(k.Key)}@{k.Beat}")));
        Console.WriteLine("PASS: key analysis — V/V and Neapolitan tensions relax (loops and vamps never change the key), a four-bar excursion is a lean, a sustained new key with cadences is a change, a lifted chorus changes key");
    }
}
