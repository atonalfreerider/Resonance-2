using System;
using System.Collections.Generic;
using System.Linq;

// Umbilic-surface harmony grammar. Pitch classes use the renderer convention: A=0, C=3.
// Surfaces sit on the circle of fourths; a movement of n steps is (s,r) with n=s+4r,
// so the destination surface is T+5n (mod 12). One rotation (S) is four fourth-steps,
// which walks the directed augmented triangles C→Ab→E, F→Db→A, D→Bb→Gb, G→Eb→B.
public static class HarmonyModel
{
    static readonly string[] Sharps = { "A", "A#", "B", "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#" };
    static readonly string[] Flats = { "A", "Bb", "B", "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab" };
    public const string Objects = "TMmcedl";
    public static int Mod(int n, int m = 12) => (n % m + m) % m;
    public static string Name(int pc, bool flats = false) => (flats ? Flats : Sharps)[Mod(pc)];
    public static int Move(int surface, int stations, int rotations) => Mod(surface + 5 * (stations + 4 * rotations));
    public static (int station, int rotation) Decompose(int steps) { int n = Mod(steps); return (n % 4, n / 4); }
    // Fourth-steps from one surface to another: 5 is its own inverse mod 12.
    public static int Steps(int from, int to) => Mod(5 * (to - from));
    public static int[] Surface(int root) => new[] { root, root + 4, root + 7, root + 11 }.Select(x => Mod(x)).ToArray();
    static int[] Offsets(char token) => token switch
    {
        'T' => new[] { 0 }, 'c' => new[] { 0, 7 }, 'e' => new[] { 0, 4 },
        'd' => new[] { 4, 7 }, 'l' => new[] { 11, 0 },
        'M' => new[] { 0, 4, 7 }, 'm' => new[] { 4, 7, 11 },
        _ => throw new ArgumentException($"'{token}' is not a surface object. Use T, c, e, d, l, M or m.")
    };
    public static int[] Object(int root, char token) => Offsets(token).Select(x => Mod(root + x)).ToArray();

    // Shortest explicit spelling of one movement-indexed object (rule 5):
    // (1,0,X) is written X, (0,0,X) is 0X, (1,S,X) is SX, otherwise {s}{r}{o}.
    public static string Token(int steps, char obj)
    {
        var (s, r) = Decompose(steps);
        string rotation = r == 0 ? "" : r == 1 ? "S" : "P";
        if (s == 1) return rotation + obj;
        return s + rotation + obj;
    }
    public static string Token(int from, int to, char obj) => Token(Steps(from, to), obj);

    // Every pitch-class set that is exactly one local object has exactly one home (§11.2).
    public static List<(int surface, char obj)> Identify(IEnumerable<int> pitches)
    {
        var pcs = new HashSet<int>(pitches.Select(p => Mod(p)));
        var found = new List<(int, char)>();
        if (pcs.Count == 0) return found;
        for (int s = 0; s < 12; s++) foreach (char o in Objects)
            if (pcs.SetEquals(Object(s, o))) found.Add((s, o));
        return found;
    }
    // Home surface/object of an analysed chord. Minor triads read m on the surface a
    // major third below their root (the missing-fundamental axiom): Dm = m on Bb'.
    // Sevenths keep the home of their triad; diminished triads keep their d dyad.
    public static (int surface, char obj) Home(int root, string quality)
    {
        quality = (quality ?? "").Trim();
        if (quality == "tone" || quality == "dyad") return (Mod(root), 'T');
        if (quality.StartsWith("dim") || quality == "m7b5") return (Mod(root - 4), 'd');
        if (quality.StartsWith("m") && !quality.StartsWith("maj")) return (Mod(root - 4), 'm');
        return (Mod(root), 'M');
    }
    public static (int surface, char obj) KeyHome(int key, bool minor) => Home(key, minor ? "m" : "");

    static readonly string[] MajorDegrees = { "I", "bII", "II", "bIII", "III", "IV", "#IV", "V", "bVI", "VI", "bVII", "VII" };
    static readonly string[] MinorDegrees = { "I", "bII", "II", "III", "#III", "IV", "#IV", "V", "VI", "#VI", "VII", "#VII" };
    // Roman numeral relative to the tonic, upper case for major/dominant, lower for minor/diminished.
    public static string Roman(int root, string quality, int key, bool minor)
    {
        if (root < 0) return "–";
        quality = (quality ?? "").Trim();
        string degree = (minor ? MinorDegrees : MajorDegrees)[Mod(root - key)];
        bool lower = quality.StartsWith("dim") || quality == "m7b5" || quality.StartsWith("m") && !quality.StartsWith("maj");
        if (lower) degree = degree.ToLowerInvariant().Replace("b", "♭").Replace("#", "♯");
        else degree = degree.Replace("b", "♭").Replace("#", "♯");
        string suffix = quality switch { "dim" => "°", "m7b5" => "ø7", "7" => "7", "maj7" => "Δ7", "m7" => "7", _ => "" };
        return degree + suffix;
    }

    public static string Notes(IEnumerable<int> notes, bool flats = false) => string.Join(" · ", notes.Select(n => Name(n, flats)));
    public static string Chord(IEnumerable<int> pitches, bool flats = false)
    {
        var pcs = new HashSet<int>(pitches.Select(n => Mod(n)));
        var matches = new List<string>();
        var types = new[] { ("", new[] {0,4,7}), ("m", new[] {0,3,7}), ("dim", new[] {0,3,6}),
            ("maj7", new[] {0,4,7,11}), ("7", new[] {0,4,7,10}), ("m7", new[] {0,3,7,10}), ("m7b5", new[] {0,3,6,10}) };
        for (int r = 0; r < 12; r++) foreach (var type in types)
            if (pcs.SetEquals(type.Item2.Select(x => Mod(x + r)))) matches.Add(Name(r, flats) + type.Item1);
        return matches.Count == 0 ? (pcs.Count == 0 ? "Silence" : "Pitch set: " + Notes(pcs.OrderBy(x => x), flats)) : string.Join(" / ", matches);
    }
    public static List<int> CompatibleCollections(IEnumerable<int> pitches)
    {
        var pcs = pitches.Select(n => Mod(n)).Distinct().ToArray();
        if (pcs.Length == 0) return new List<int>();
        return Enumerable.Range(0, 12).Where(r => pcs.All(p => new[] {0,2,4,5,7,9,11}.Contains(Mod(p-r)))).ToList();
    }

    public sealed class Frame
    {
        public int Surface;            // where the event's final object was read
        public int CanonicalSurface;   // start + net movement, remainders excluded (rule 8.3)
        public int CanonicalSteps;     // that net movement in fourth-steps, carried mod 12
        public char FinalObject;
        public bool Anchored;          // the final object was read from a (remainder) anchor
        public int[] Pitches;          // sounding union, including sustained remainders
        public int[] Held;             // remainder pitch classes sustained into later events
        public string Trace;
        public (int station, int rotation) CanonicalMove => Decompose(CanonicalSteps);
    }
    // Proposition 9: same canonical movement sums and the same final local object.
    // This is grammatical identity; the sounding union can still differ (0M vs 0m0M).
    public static bool SameChord(Frame a, Frame b) => a.CanonicalSteps == b.CanonicalSteps && a.FinalObject == b.FinalObject;

    // Evaluation contract (see Docs/GRAMMAR-COMPLIANCE.md):
    // - '>' separates sounding events; objects inside one event cohere into a pitch union.
    // - Movement accumulates left to right with carries (n = s + 4r mod 12).
    // - (X) sustains X's result. X starts where the last object was read, and the next
    //   unparenthesized object Y is read from X's final surface (anchor rule), but X's
    //   movement never enters the canonical running surface (remainder/exclusion rules):
    //   everything after Y continues from the canonical surface.
    // - Note names are §11.2 targets: G (tone, T on G'), BDG (cluster, the one surface
    //   object with those notes), "C Major"/"D Minor" (M on C', m on Bb'). Targets compile
    //   to the explicit movement that reaches them.
    public static List<Frame> Evaluate(int start, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter a sequence, e.g. 0m > PM > M.");
        var frames = new List<Frame>();
        var held = new HashSet<int>();
        int canonical = Mod(start), read = Mod(start), origin = Mod(start);
        int? anchor = null;
        char last = '\0';
        foreach (string group in text.Split('>'))
        {
            var sounding = new HashSet<int>(held);
            var trace = new List<string>();
            bool inside = false, found = false, anchoredLast = false;
            int i = 0;
            while (i < group.Length)
            {
                char c = group[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '(') { if (inside) throw new ArgumentException("Nested sustain is unsupported."); inside = true; i++; continue; }
                if (c == ')')
                {
                    if (!inside) throw new ArgumentException("Unmatched closing parenthesis.");
                    inside = false; anchor = read; i++; continue;
                }
                if (c == '.') throw new ArgumentException("Branch notation (A B.C) is recognised but not evaluated yet.");
                int from = inside ? read : anchor ?? canonical;
                int steps; char obj; string label;
                if (c >= 'A' && c <= 'G')
                {
                    var (surface, o, name) = ReadTarget(group, ref i);
                    steps = Steps(from, surface); obj = o; label = $"{name} ⇒ {Token(steps, o)}";
                }
                else
                {
                    int s = 1, r = 0;
                    if (char.IsDigit(c)) { s = c - '0'; i++; if (s > 3) throw new ArgumentException("Station must be 0–3."); }
                    while (i < group.Length && char.IsWhiteSpace(group[i])) i++;
                    if (i < group.Length && (group[i] == 'S' || group[i] == 'P')) r = group[i++] == 'S' ? 1 : 2;
                    else if (i < group.Length && char.IsDigit(group[i])) { r = group[i++] - '0'; if (r > 2) throw new ArgumentException("Rotation must be 0, S (1) or P (2)."); }
                    while (i < group.Length && char.IsWhiteSpace(group[i])) i++;
                    if (i == group.Length) throw new ArgumentException("Movement needs an object.");
                    obj = group[i++];
                    Offsets(obj);
                    steps = s + 4 * r;
                    label = $"({s},{(r == 0 ? "0" : r == 1 ? "S" : "P")},{obj})";
                }
                int to = Mod(from + 5 * steps);
                var notes = Object(to, obj);
                sounding.UnionWith(notes);
                string how = inside ? "remainder" : anchor.HasValue ? "anchored" : "";
                if (inside) held.UnionWith(notes);
                else
                {
                    anchoredLast = anchor.HasValue; anchor = null;
                    canonical = Mod(canonical + 5 * steps);
                    last = obj;
                }
                read = to; found = true;
                trace.Add($"{Name(from)}' {label} → {Name(to)}'{(how.Length > 0 ? " [" + how + "]" : "")}");
            }
            if (inside) throw new ArgumentException("Close the sustain parenthesis before >.");
            if (!found) throw new ArgumentException("Empty progression step.");
            int net = Steps(origin, canonical);
            if (last != '\0') trace.Add($"canonical {Name(canonical)}' = start {Token(net, last)}");
            frames.Add(new Frame
            {
                Surface = read, CanonicalSurface = canonical, CanonicalSteps = net, FinalObject = last, Anchored = anchoredLast,
                Pitches = sounding.OrderBy(x => x).ToArray(), Held = held.OrderBy(x => x).ToArray(), Trace = string.Join("; ", trace)
            });
        }
        return frames;
    }

    static int ReadNote(string text, ref int i)
    {
        int pc = "A_BC_D_EF_G_".IndexOf(text[i++]);
        if (i < text.Length && text[i] == '#') { pc++; i++; }
        else if (i < text.Length && text[i] == 'b') { pc--; i++; }
        return Mod(pc);
    }
    static (int surface, char obj, string name) ReadTarget(string text, ref int i)
    {
        int begin = i;
        var pcs = new List<int> { ReadNote(text, ref i) };
        while (i < text.Length && text[i] >= 'A' && text[i] <= 'G') pcs.Add(ReadNote(text, ref i));
        int j = i;
        while (j < text.Length && text[j] == ' ') j++;
        foreach (var (word, quality) in new[] { ("Major", ""), ("major", ""), ("Minor", "m"), ("minor", "m") })
        {
            if (pcs.Count != 1 || string.CompareOrdinal(text, j, word, 0, word.Length) != 0) continue;
            i = j + word.Length;
            var home = Home(pcs[0], quality);
            return (home.surface, home.obj, $"{Name(pcs[0])} {word}");
        }
        string cluster = text.Substring(begin, i - begin);
        if (pcs.Count == 1) return (pcs[0], 'T', cluster);
        var homes = Identify(pcs);
        if (homes.Count == 0) throw new ArgumentException($"{cluster} is not one local object on any surface; write it as a cohered sequence of objects.");
        return (homes[0].surface, homes[0].obj, cluster);
    }
}
