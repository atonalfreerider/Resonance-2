using System;
using System.Collections.Generic;
using System.Linq;

// Pitch classes use the existing renderer convention: A=0, C=3.
public static class HarmonyModel
{
    static readonly string[] Sharps = { "A", "A#", "B", "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#" };
    static readonly string[] Flats = { "A", "Bb", "B", "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab" };
    public static int Mod(int n, int m = 12) => (n % m + m) % m;
    public static string Name(int pc, bool flats = false) => (flats ? Flats : Sharps)[Mod(pc)];
    public static int Move(int surface, int stations, int rotations) => Mod(surface + 5 * (stations + 4 * rotations));
    public static (int station, int rotation) Decompose(int steps) { int n = Mod(steps); return (n % 4, n / 4); }
    public static int[] Surface(int root) => new[] { root, root + 4, root + 7, root + 11 }.Select(x => Mod(x)).ToArray();
    public static int[] Object(int root, char token)
    {
        int[] offsets = token switch
        {
            'T' => new[] { 0 }, 'c' => new[] { 0, 7 }, 'e' => new[] { 0, 4 },
            'd' => new[] { 4, 7 }, 'l' => new[] { 11, 0 },
            'M' => new[] { 0, 4, 7 }, 'm' => new[] { 4, 7, 11 },
            _ => throw new ArgumentException("Use T, c, e, d, l, M or m.")
        };
        return offsets.Select(x => Mod(root + x)).ToArray();
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
        public int Surface;
        public int[] Pitches;
        public string Trace;
    }
    // Explicit v1 contract: > begins a new event; adjacent objects cohere.
    // Parenthesized objects sustain to the end of the run. Branches are rejected.
    public static List<Frame> Evaluate(int start, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter a sequence, e.g. 0m > PM > M.");
        var frames = new List<Frame>();
        var held = new HashSet<int>();
        int surface = Mod(start);
        foreach (string group in text.Split('>'))
        {
            var sounding = new HashSet<int>(held);
            var trace = new List<string>();
            bool sustain = false; bool found = false;
            int i = 0;
            while (i < group.Length)
            {
                if (char.IsWhiteSpace(group[i])) { i++; continue; }
                if (group[i] == '(') { if (sustain) throw new ArgumentException("Nested sustain is unsupported."); sustain = true; i++; continue; }
                if (group[i] == ')') { if (!sustain) throw new ArgumentException("Unmatched closing parenthesis."); sustain = false; i++; continue; }
                int s = 1, r = 0;
                if (char.IsDigit(group[i])) { s = group[i++] - '0'; if (s > 3) throw new ArgumentException("Station must be 0–3."); }
                while (i < group.Length && char.IsWhiteSpace(group[i])) i++;
                if (i < group.Length && (group[i] == 'S' || group[i] == 'P')) r = group[i++] == 'S' ? 1 : 2;
                else if (i < group.Length && char.IsDigit(group[i])) { r = group[i++] - '0'; if (r > 2) throw new ArgumentException("Rotation must be 0–2."); }
                while (i < group.Length && char.IsWhiteSpace(group[i])) i++;
                if (i == group.Length) throw new ArgumentException("Movement needs an object.");
                char obj = group[i++];
                int origin = surface;
                surface = Move(surface, s, r);
                int[] notes = Object(surface, obj);
                sounding.UnionWith(notes); if (sustain) held.UnionWith(notes);
                trace.Add($"{Name(origin)}' → ({s},{r},{obj}) → {Name(surface)}'");
                found = true;
            }
            if (sustain) throw new ArgumentException("Close the sustain parenthesis before >.");
            if (!found) throw new ArgumentException("Empty progression step.");
            frames.Add(new Frame { Surface = surface, Pitches = sounding.OrderBy(x => x).ToArray(), Trace = string.Join("; ", trace) });
        }
        return frames;
    }
}
