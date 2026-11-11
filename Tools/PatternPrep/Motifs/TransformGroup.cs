// The group that places a cell (Docs/MOTIF-REDUCTION.md §5 C6): tonal and real transposition, the octave, the chord
// frame, the mirror about the first note, retrograde, augmentation and the parallel mode, priced in bits and named
// as a musician would. A transform is stored from the prototype: Steps and Semitones carry its first note onto the
// image's first note, and the image's intervals from its first note are the prototype's, mirrored (I) and reversed
// (R). Mirror and reversal commute and each undoes itself, so elements compose componentwise.
public readonly struct Transform : IEquatable<Transform>
{
    public const int Tonal = 0, Real = 1, Chord = 2;
    public readonly int Steps, Semitones, Frame;   // Frame: Tonal (t_k, the local scale), Real (T_k), Chord (t_k^H, interval above the sounding root)
    public readonly int Parallel;                  // −1 into the parallel minor, +1 into the parallel major (degrees 3, 6, 7 move by one)
    public readonly bool Inverted, Retrograde, Head;   // Head: the held first note lengthened (an augmented head)
    public readonly double Scale;                  // augmentation: 1, or ½, 2, 3/2

    public Transform(int steps, int semitones, int frame = Tonal, bool inverted = false, bool retrograde = false, double scale = 1, int parallel = 0, bool head = false)
    {
        Steps = steps; Semitones = semitones; Frame = frame; Inverted = inverted; Retrograde = retrograde; Scale = scale; Parallel = Math.Sign(parallel); Head = head;
    }
    public static readonly Transform Identity = new(0, 0);
    // Whole octaves when the image is the prototype moved by octaves (t±7 / T±12 per octave), else 0.
    public int Octaves => Steps != 0 && Steps % 7 == 0 && Semitones == Steps / 7 * 12 ? Steps / 7 : 0;
    // The chord root's move under the chord frame, in semitones folded to −5 … +6.
    public int RootMove { get { int m = Spelling.Mod(Semitones, 12); return m > 6 ? m - 12 : m; } }

    // Bits of the element (§5 C6): O 1 per octave; t_k 1.5 + log2(1+|k|); T_k 2.5 + log2(1+|k|); t_k^H 2 + log2(1+|root
    // move|); I +2; R +4; A_r 3·|log2 r|, a lengthened head 2; P +2 (the sixth alone 1.5: Placement.Bits takes the half off).
    public double Cost()
    {
        double bits = 0;
        int octaves = Octaves;
        if (octaves != 0) bits += Math.Abs(octaves);
        else if (Steps != 0 || Semitones != 0 && Frame != Tonal)
            bits += Frame switch { Real => 2.5 + Math.Log2(1 + Math.Abs(Semitones)), Chord => 2 + Math.Log2(1 + Math.Abs(RootMove)), _ => 1.5 + Math.Log2(1 + Math.Abs(Steps)) };
        if (Inverted) bits += 2;
        if (Retrograde) bits += 4;
        if (Math.Abs(Scale - 1) > 1e-9) bits += 3 * Math.Abs(Math.Log2(Scale));
        if (Head) bits += 2;
        if (Parallel != 0) bits += 2;
        return bits;
    }
    // This element, then `next`. Mixed frames compose to the looser one (a real step after a tonal one is no longer tonal).
    public Transform Then(in Transform next) => new(Steps + next.Steps, Semitones + next.Semitones, Math.Max(Frame, next.Frame), Inverted ^ next.Inverted, Retrograde ^ next.Retrograde,
        Scale * next.Scale, Math.Clamp(Parallel + next.Parallel, -1, 1), Head || next.Head);
    public static Transform Compose(in Transform first, in Transform second) => first.Then(second);
    public Transform Inverse() => new(-Steps, -Semitones, Frame, Inverted, Retrograde, 1 / Scale, -Parallel, Head);

    public bool Equals(Transform o) => Steps == o.Steps && Semitones == o.Semitones && Frame == o.Frame && Parallel == o.Parallel && Inverted == o.Inverted && Retrograde == o.Retrograde && Head == o.Head && Math.Abs(Scale - o.Scale) < 1e-9;
    public override bool Equals(object o) => o is Transform t && Equals(t);
    public override int GetHashCode() => HashCode.Combine(Steps, Semitones, Frame, Parallel, Inverted, Retrograde, Head);
    public override string ToString() => TransformGroup.Name(this);
}

// One note of a cell: onset and length in ticks from the cell's start, morph and pitch as heard, the alteration
// against the local key, and the interval above the sounding chord root (−1 without a chord).
public struct CellNote
{
    public long Onset, Length;
    public int Morph, Pitch, Alter, Interval;
}

// An altered note a placement leaves: where the member's pitch departs from its image by Delta semitones (notes the
// parallel mode moves are part of the image, not alterations). Kind says how the regional key names it: borrowed from
// the parallel minor or a leading tone (1.5 bits each, §9 G1), or not at all (3 bits, named by its note).
public readonly struct Alteration
{
    public const int Unexplained = 0, Borrowed = 1, LeadingTone = 2;
    public readonly int Note, Pitch, Delta, Kind;
    public readonly string Text;
    public Alteration(int note, int pitch, int delta, int kind, string text) { Note = note; Pitch = pitch; Delta = delta; Kind = kind; Text = text ?? ""; }
    public double Bits => Kind is Borrowed or LeadingTone ? 1.5 : 3;
}

// A member placed against a prototype: the element, the altered notes it leaves, their bits and the sentence.
public sealed class Placement
{
    public Transform G;
    public Alteration[] Altered = Array.Empty<Alteration>();
    public string Mode = "";        // "C major → C minor" when G moves into the parallel mode
    public double Bits;             // the element's bits plus the altered notes'
    public string Text = "";
}

public static class TransformGroup
{
    // The image of a prototype under g, for a cell starting at `start` (decoding a member, §9 G4): onsets and lengths
    // from the cell start, morph, pitch and alteration. A tonal image takes its pitches from the local key at each image
    // note, the prototype's alterations kept (and mirrored with their notes); the chord frame keeps each note's interval
    // above the sounding root; the parallel mode then moves degrees 3, 6 and 7 above the first-beat chord's root.
    public static CellNote[] Apply(in Transform g, CellNote[] prototype, long start, KeyFrames frames)
    {
        int n = prototype.Length; var image = new CellNote[n];
        if (n == 0) return image;
        long extent = 0; foreach (var p in prototype) extent = Math.Max(extent, p.Onset + p.Length);
        int sign = g.Inverted ? -1 : 1;
        var head = prototype[g.Retrograde ? n - 1 : 0];
        int morph0 = prototype[0].Morph + g.Steps, pitch0 = prototype[0].Pitch + g.Semitones;
        for (int j = 0; j < n; j++)
        {
            var q = prototype[g.Retrograde ? n - 1 - j : j];
            long onset = g.Retrograde ? extent - q.Onset - q.Length : q.Onset;
            onset = (long)Math.Round(onset * g.Scale); long length = Math.Max(1, (long)Math.Round(q.Length * g.Scale));
            int morph = morph0 + sign * (q.Morph - head.Morph), r = frames.Region(start + onset), pitch;
            if (g.Frame == Transform.Real) pitch = pitch0 + sign * (q.Pitch - head.Pitch);
            else
            {
                pitch = frames.ScalePitch(morph, r) + sign * q.Alter;
                int root = g.Frame == Transform.Chord ? frames.RootPc(frames.Chord(start + onset)) : -1;
                if (root >= 0 && q.Interval >= 0) pitch += Spelling.Mod(root + q.Interval - pitch + 6, 12) - 6;   // the nearest pitch keeping the interval
            }
            image[j] = new CellNote { Onset = onset, Length = length, Morph = morph, Pitch = pitch };
        }
        if (g.Parallel != 0)
        {
            int chord = frames.Chord(start), root = frames.RootPc(chord), r = frames.Region(start);
            if (root >= 0)
            {
                int letter = Spelling.Letter(frames.Spell(root, r));
                for (int j = 0; j < n; j++) if (Spelling.Mod(Spelling.Mod(image[j].Morph, 7) - letter, 7) is 2 or 5 or 6) image[j].Pitch += g.Parallel;
            }
        }
        for (int j = 0; j < n; j++)
        {
            int r = frames.Region(start + image[j].Onset), root = frames.RootPc(frames.Chord(start + image[j].Onset));
            image[j].Alter = frames.Alter(image[j].Pitch, image[j].Morph, r);
            image[j].Interval = root < 0 ? -1 : Spelling.Mod(image[j].Pitch - root, 12);
        }
        return image;
    }

    // A cell's notes from the events (one per stream event: its lead), as Relate and Apply read them.
    public static CellNote[] Cell(MotifEvent[] ev, IReadOnlyList<int> notes, long start, KeyFrames frames)
    {
        var cell = new CellNote[notes.Count];
        for (int j = 0; j < cell.Length; j++)
        {
            var e = ev[notes[j]]; int root = frames.RootPc(frames.Chord(e.Tick));
            cell[j] = new CellNote { Onset = e.Tick - start, Length = e.Duration, Morph = e.Morph, Pitch = e.Pitch, Alter = e.Alter, Interval = root < 0 ? -1 : Spelling.Mod(e.Pitch - root, 12) };
        }
        return cell;
    }

    // The cheapest element placing cell b (at bStart) as an image of cell a (at aStart). Both have the same notes in
    // the same rhythm, as members of one exact orbit do; null when b's steps are neither a's nor their mirror.
    // - Real or octave when every semitone step is kept (a repeat, an octave, else T_k); the only choice in a
    //   chromatic window, where spelling is unreliable.
    // - Tonal otherwise (t_k, alterations kept): the parallel mode names its altered notes first, when the first-beat
    //   chords change between major and minor on the same root and P leaves fewer altered notes; the regional key names
    //   the rest (borrowed ♭3/♭6/♭7, a leading tone).
    // - The chord frame (t_k^H) when every note keeps its interval above the sounding root and neither transposition
    //   explains the member without altered notes.
    // Ties keep the earlier description, so a tonal image that is also real is named tonal.
    public static Placement Relate(CellNote[] a, CellNote[] b, long aStart, long bStart, KeyFrames frames, bool chromatic = false)
    {
        int n = a.Length; if (n == 0 || b.Length != n) return null;
        bool same = true, mirror = true, real = true;
        for (int j = 1; j < n; j++)
        {
            int da = chromatic ? a[j].Pitch - a[0].Pitch : a[j].Morph - a[0].Morph, db = chromatic ? b[j].Pitch - b[0].Pitch : b[j].Morph - b[0].Morph;
            if (db != da) same = false;
            if (db != -da) mirror = false;
        }
        if (!same && !mirror) return null;
        bool inverted = !same; int sign = inverted ? -1 : 1;
        for (int j = 1; j < n; j++) if (b[j].Pitch - b[0].Pitch != sign * (a[j].Pitch - a[0].Pitch)) real = false;
        int steps = b[0].Morph - a[0].Morph, semitones = b[0].Pitch - a[0].Pitch;
        Placement best = null;
        if (real)
        {
            bool plain = semitones == 0 && steps == 0 || semitones != 0 && semitones % 12 == 0 && steps == semitones / 12 * 7;
            var g = new Transform(steps, semitones, plain ? Transform.Tonal : Transform.Real, inverted);
            best = new Placement { G = g, Bits = g.Cost() };
        }
        if (!chromatic && (best == null || best.G.Frame == Transform.Real))
        {
            var tonal = Tonal(a, b, aStart, bStart, frames, steps, inverted);
            if (best == null || tonal.Bits < best.Bits - 1e-9) best = tonal;
            if (best.Altered.Length > 0 && !inverted)
            {
                bool kept = true;
                for (int j = 0; j < n && kept; j++) kept = a[j].Interval >= 0 && a[j].Interval == b[j].Interval;
                var g = new Transform(steps, semitones, Transform.Chord);
                if (kept)
                {
                    var image = Apply(g, a, bStart, frames);   // exact only: the register must come out too
                    for (int j = 0; j < n && kept; j++) kept = image[j].Pitch == b[j].Pitch;
                }
                if (kept) best = new Placement { G = g, Bits = g.Cost() };
            }
        }
        best.Text = Name(best.G, best.Altered, best.Mode);
        return best;
    }

    static Placement Tonal(CellNote[] a, CellNote[] b, long aStart, long bStart, KeyFrames frames, int steps, bool inverted)
    {
        int n = a.Length;
        var plain = new Transform(steps, 0, Transform.Tonal, inverted);
        var image = Apply(plain, a, bStart, frames);
        // The element moves the first note by what its image moves it: an altered first note is a residual, not a step.
        var g = new Transform(steps, image[0].Pitch - a[0].Pitch, Transform.Tonal, inverted);
        string mode = ""; bool sixthOnly = false;
        int ca = frames.Chord(aStart), cb = frames.Chord(bStart), ma = frames.Mode(ca), mb = frames.Mode(cb);
        if (ma != 0 && mb == -ma && Spelling.Mod(frames.RootPc(cb) - frames.RootPc(ca) - g.Semitones, 12) == 0)
        {
            var withP = new Transform(steps, g.Semitones, Transform.Tonal, inverted, parallel: mb);
            var moved = Apply(withP, a, bStart, frames);
            if (Count(moved, b) < Count(image, b))
            {
                int letter = Spelling.Letter(frames.Spell(frames.RootPc(cb), frames.Region(bStart)));
                sixthOnly = true;
                for (int j = 0; j < n; j++) if (moved[j].Pitch != image[j].Pitch && Spelling.Mod(Spelling.Mod(moved[j].Morph, 7) - letter, 7) != 5) sixthOnly = false;
                int ra = frames.Region(aStart), rb = frames.Region(bStart);
                mode = $"{Spelling.Name(frames.Spell(frames.RootPc(ca), ra))} {(ma > 0 ? "major" : "minor")} → {Spelling.Name(frames.Spell(frames.RootPc(cb), rb))} {(mb > 0 ? "major" : "minor")}";
                g = new Transform(steps, moved[0].Pitch - a[0].Pitch, Transform.Tonal, inverted, parallel: mb);
                image = moved;
            }
        }
        var altered = new List<Alteration>();
        for (int j = 0; j < n; j++)
        {
            int delta = b[j].Pitch - image[j].Pitch; if (delta == 0) continue;
            int fifths = FifthsOf(b[j]);
            altered.Add(frames.Explain(j, b[j].Pitch, fifths, b[j].Morph, delta, bStart + b[j].Onset, bStart, bStart + Extent(b)));
        }
        double bits = g.Cost() - (sixthOnly ? .5 : 0);
        foreach (var x in altered) bits += x.Bits;
        return new Placement { G = g, Altered = altered.ToArray(), Mode = mode, Bits = bits };
    }
    static int Count(CellNote[] image, CellNote[] b) { int c = 0; for (int j = 0; j < b.Length; j++) if (image[j].Pitch != b[j].Pitch) c++; return c; }
    static long Extent(CellNote[] c) { long e = 1; foreach (var x in c) e = Math.Max(e, x.Onset + x.Length); return e; }
    // The spelling a note's morph and pitch imply (its letter is the morph's; the accidental is the remainder).
    static int FifthsOf(CellNote x)
    {
        int letter = Spelling.Mod(x.Morph, 7), accidental = x.Pitch - Spelling.NaturalPitch(x.Morph);
        int natural = new[] { 0, 2, 4, -1, 1, 3, 5 }[letter];
        return natural + 7 * accidental;
    }

    static readonly string[] Diatonic = { "unison", "second", "third", "fourth", "fifth", "sixth", "seventh", "octave", "ninth", "tenth", "eleventh", "twelfth", "thirteenth", "fourteenth", "double octave" };
    static readonly string[] Chromatic = { "unison", "semitone", "whole tone", "minor third", "major third", "fourth", "tritone", "fifth", "minor sixth", "major sixth", "minor seventh", "major seventh",
        "octave", "minor ninth", "major ninth", "minor tenth", "major tenth", "eleventh", "augmented eleventh", "twelfth", "minor thirteenth", "major thirteenth", "minor fourteenth", "major fourteenth", "double octave" };
    static readonly string[] Numbers = { "no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };
    static string A(string word) => char.IsDigit(word[0]) ? word : ("aeiou".Contains(word[0]) ? "an " : "a ") + word;
    public static string DiatonicInterval(int steps) => steps < Diatonic.Length ? Diatonic[steps] : $"{steps + 1}th";
    public static string ChromaticInterval(int semitones) => semitones < Chromatic.Length ? Chromatic[semitones] : $"{semitones} semitones";

    // The musician's sentence for an element (§5 C6 templates, what tours quote): "repeat", "tonal sequence up a second",
    // "real sequence down a whole tone", "an octave lower", "mirrored (inversion)", "in the parallel minor (A major → A
    // minor)", "… with 1 altered note: ♭6 borrowed from E minor", "held note lengthened (augmented head)", "variant (2 edits)".
    public static string Name(in Transform g, IReadOnlyList<Alteration> altered = null, string mode = "", int edits = 0)
    {
        var parts = new List<string>();
        int octaves = g.Octaves;
        if (octaves != 0) parts.Add((Math.Abs(octaves) == 1 ? "an octave" : (Math.Abs(octaves) < Numbers.Length ? Numbers[Math.Abs(octaves)] : Math.Abs(octaves).ToString()) + " octaves") + (octaves < 0 ? " lower" : " higher"));
        else if (g.Frame == Transform.Real && g.Semitones != 0) parts.Add($"real sequence {(g.Semitones < 0 ? "down" : "up")} {A(ChromaticInterval(Math.Abs(g.Semitones)))}");
        else if (g.Frame == Transform.Chord && (g.Steps != 0 || g.Semitones != 0)) parts.Add($"harmonic sequence {(g.Steps < 0 || g.Steps == 0 && g.Semitones < 0 ? "down" : "up")} {A(g.Steps != 0 ? DiatonicInterval(Math.Abs(g.Steps)) : ChromaticInterval(Math.Abs(g.Semitones)))} (the figure follows the chords)");
        else if (g.Frame != Transform.Real && g.Steps != 0) parts.Add($"tonal sequence {(g.Steps < 0 ? "down" : "up")} {A(DiatonicInterval(Math.Abs(g.Steps)))}");
        if (g.Inverted) parts.Add("mirrored (inversion)");
        if (g.Retrograde) parts.Add("in retrograde");
        if (Math.Abs(g.Scale - 1) > 1e-9) parts.Add((g.Scale > 1 ? "augmented" : "diminished") + " (note values ×" + g.Scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")");
        if (g.Head) parts.Add("held note lengthened (augmented head)");
        if (g.Parallel != 0) parts.Add($"in the parallel {(g.Parallel < 0 ? "minor" : "major")}" + (string.IsNullOrEmpty(mode) ? "" : $" ({mode})"));
        string text = string.Join(", ", parts);
        if (altered != null && altered.Count > 0)
        {
            var names = new List<string>();
            foreach (var x in altered) if (!names.Contains(x.Text)) names.Add(x.Text);
            text = (text.Length == 0 ? "repeat" : text) + $" with {altered.Count} altered note{(altered.Count == 1 ? "" : "s")}: {string.Join("; ", names)}";
        }
        if (edits > 0) text += (text.Length == 0 ? "" : ", ") + $"variant ({edits} edit{(edits == 1 ? "" : "s")})";
        return text.Length == 0 ? "repeat" : text;
    }

    // Stab(shape) = {k : T_k(pcs) = pcs} (§5 C5): the transpositions that fix a sonority's pitch classes, {0, 6} for a
    // tritone, {0, 3, 6, 9} for a diminished seventh, {0, 4, 8} for an augmented triad.
    public static int[] Stabiliser(IEnumerable<int> pitches)
    {
        int mask = 0; foreach (int p in pitches) mask |= 1 << Spelling.Mod(p, 12);
        var stab = new List<int>();
        for (int k = 0; k < 12; k++) if ((((mask << k) | (mask >> (12 - k))) & 0xFFF) == mask) stab.Add(k);
        return stab.ToArray();
    }
    // The order of a generator stepping `step` semitones on the pitch-class circle modulo the shape's symmetry (§6 D4):
    // n* = (12/|Stab|) / gcd(12/|Stab|, |j|). A tritone stepped −1 returns after 6, a diminished seventh after 3, a
    // step of 4 after 3 (an augmented triad); 1 is a loop. Order(step, 7) is the same on the diatonic clock.
    public static int Order(int step, int[] stabiliser) => Order(step, 12 / Math.Max(1, stabiliser?.Length ?? 1));
    public static int Order(int step, int modulus)
    {
        if (modulus <= 1) return 1;
        int j = Spelling.Mod(step, modulus);
        return modulus / Gcd(modulus, j);
    }
    static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }
}
