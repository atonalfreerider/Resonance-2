using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

// Rhyme families for the lyric graph, worked out once when a song loads from the syllables and
// stresses PatternPrep already wrote (no dictionary): each word's sound from its last stressed
// vowel to the end (the same spelling rules as PatternPrep's LyricEnglish), split into a vowel
// nucleus and a coda, and graded against other words:
//  • perfect   the whole sound matches (star / are);
//  • slant     the vowel matches and the codas are close: same consonant family (time / mine,
//              home / stone), or one only adds an s, t, d or z (feel / field, grind / time);
//  • imperfect the vowel matches but the codas differ (assonance: cold / hope), or the vowels are
//              near neighbours over the same coda (bit / beat).
// End words join across the whole stanza at any grade; words inside lines join as perfect or
// slant rhymes within two lines either side, and as assonance within the same or the next line.
// Joined words form families.
//
// Colour is the vowel: every rhyme sound's nucleus has a fixed place on one colour wheel, the
// same in every song, with neighbouring vowels in neighbouring hues (front vowels cool, open ones
// warm, back ones red to violet), so rhymes and near rhymes always share a region of the wheel.
public static class LyricRhymes
{
    public enum Grade { None = 0, Imperfect = 1, Slant = 2, Perfect = 3 }
    public sealed class Word { public string Text = "", Sound = "", Nucleus = "", Coda = ""; public int Line, Stanza, Index; public bool End, Weak; public int Family = -1; public float Hue, Shade; }
    // The vowel wheel (hue, 0..1): ee, i, ay, e, a, eye, uh, o/ah, aw, oh, oo, oy, round again.
    static readonly (string[] spellings, float hue)[] Wheel =
    {
        (new[]{"ee","ea","ie","y"}, .56f), (new[]{"i"}, .49f), (new[]{"ei","ey","ay"}, .40f), (new[]{"e"}, .31f), (new[]{"a"}, .20f),
        (new[]{"ai","ae"}, .12f), (new[]{"u"}, .05f), (new[]{"o","ar"}, .97f), (new[]{"au","aw"}, .89f), (new[]{"oa","ow"}, .81f),
        (new[]{"oo","ue","ew"}, .72f), (new[]{"oi","oy"}, .64f),
    };
    // The vowels as the wheel shows them: long vowels (and diphthongs) on the outer ring, short ones
    // on the inner, each at its hue, with an example word.
    public static readonly (string nucleus, string label, bool isLong, string example)[] WheelVowels =
    {
        ("ee","ee",true,"see"), ("i","ih",false,"sit"), ("ei","ay",true,"day"), ("e","eh",false,"red"), ("a","a",false,"cat"),
        ("ai","eye",true,"night"), ("u","uh",false,"cup"), ("o","ah",false,"hot"), ("au","ow",true,"down"), ("oa","oh",true,"go"),
        ("oo","oo",true,"blue"), ("oi","oy",true,"boy"),
    };
    public static float Hue(string nucleus)
    {
        foreach (var (spellings, hue) in Wheel) if (spellings.Contains(nucleus)) return hue;
        foreach (var (spellings, hue) in Wheel) if (nucleus.Length > 0 && spellings.Contains(nucleus.Substring(0, 1))) return hue;
        return .5f;
    }
    public sealed class Link { public int A, B; public Grade Grade; public bool End; }

    static readonly HashSet<string> WeakWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","and","or","but","nor","of","to","in","on","at","by","for","from","with","as","if","is","was","were","are","am","be","been",
        "it","its","it's","his","her","him","my","your","our","their","them","you","he","she","we","they","me","us","i","i'm","that","than","so",
        "do","does","did","has","have","had","can","could","shall","should","will","would","may","might","must","what","which","who","when","then",
        "there","where","up","out","too","this","these","those","not","all","o","oh","into","just","like","let","how","each","some","yeah","uh","ah","ooh","huh",
    };
    static readonly Dictionary<string, string> Sounds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["are"]="ar",["gone"]="on",["one"]="un",["done"]="un",["none"]="un",["won"]="un",["son"]="un",["come"]="um",["some"]="um",["love"]="uv",["dove"]="uv",["above"]="uv",
        ["move"]="oov",["prove"]="oov",["have"]="av",["give"]="iv",["live"]="iv",["you"]="oo",["through"]="oo",["to"]="oo",["do"]="oo",["who"]="oo",["two"]="oo",["too"]="oo",
        ["eye"]="ai",["i"]="ai",["said"]="ed",["again"]="en",["been"]="in",["were"]="er",["there"]="air",["where"]="air",["their"]="air",["here"]="eer",["heart"]="art",
        ["earth"]="erth",["own"]="oan",["grown"]="oan",["known"]="oan",["shown"]="oan",["blown"]="oan",["flown"]="oan",["town"]="aun",["down"]="aun",["crown"]="aun",
        ["how"]="au",["now"]="au",["cow"]="au",["know"]="oa",["grow"]="oa",["show"]="oa",["low"]="oa",["slow"]="oa",["snow"]="oa",["flow"]="oa",["go"]="oa",["so"]="oa",
        ["no"]="oa",["toe"]="oa",["of"]="uv",["was"]="uz",["what"]="ut",["break"]="eik",["great"]="eit",["steak"]="eik",
        ["cry"]="ai",["my"]="ai",["by"]="ai",["why"]="ai",["sky"]="ai",["fly"]="ai",["die"]="ai",["lie"]="ai",["tie"]="ai",["me"]="ee",["be"]="ee",["he"]="ee",["she"]="ee",
        ["we"]="ee",["thee"]="ee",["knee"]="ee",["see"]="ee",["sea"]="ee",
    };
    static readonly (Regex pattern, string sound)[] Rules = new (string, string)[]
    {
        ("igh", "AI"), ("eigh", "EI"), ("ough", "O"), ("augh", "O"),
        ("tch", "ch"), ("dge", "j"), ("ck", "k"), ("ph", "f"), ("qu", "kw"),
        ("ay|ai|ey$|ei", "EI"), ("ee|ea|ie(?=.)", "EE"), ("oa|oe$|ow$", "OA"), ("oo", "OO"), ("ou|ow", "AU"), ("ue$|ew", "OO"), ("au|aw", "O"), ("oi|oy", "OI"),
        ("^a([^aeiouy])e(s?)$", "EI$1$2"), ("^i([^aeiouy])e(s?)$", "AI$1$2"), ("^o([^aeiouy])e(s?)$", "OA$1$2"), ("^u([^aeiouy])e(s?)$", "OO$1$2"), ("^e([^aeiouy])e(s?)$", "EE$1$2"),
        ("^y$", "AI"), ("y$", "EE"), ("e$", ""), (@"([bcdfgjklmnprstvz])\1", "$1"), ("'", ""),
    }.Select(r => (new Regex(r.Item1, RegexOptions.Compiled), r.Item2)).ToArray();

    static bool Vowel(char c) => "aeiouy".IndexOf(c) >= 0;
    public static string Clean(string word) => new string(word.ToLowerInvariant().Where(c => char.IsLetter(c) || c == '\'').ToArray()).Trim('\'');
    static string Sound(string tail)
    {
        string s = tail.ToLowerInvariant();
        foreach (var (pattern, sound) in Rules) s = pattern.Replace(s, sound);
        return (s.Length == 0 ? tail : s).ToLowerInvariant();
    }
    static string Tail(string syllable)
    {
        int v = 0; while (v < syllable.Length && !(Vowel(syllable[v]) && !(syllable[v] == 'y' && v == 0 && syllable.Length > 1))) v++;
        return v >= syllable.Length ? syllable : syllable.Substring(v);
    }
    // The sound from the last stressed syllable to the end of the word.
    public static string RhymeSound(string word, IReadOnlyList<string> syllables, IReadOnlyList<int> stress)
    {
        string w = Clean(word);
        if (Sounds.TryGetValue(w, out var known)) return known;
        if (syllables.Count == 0) return w;
        int from = -1; for (int i = 0; i < stress.Count; i++) if (stress[i] > 0) from = i;
        if (from < 0 || from >= syllables.Count) from = syllables.Count - 1;
        string tail = Tail(Clean(syllables[from])) + string.Concat(syllables.Skip(from + 1).Select(Clean));
        return Sound(tail);
    }
    static (string nucleus, string coda) Split(string sound)
    {
        int v = 0; while (v < sound.Length && !Vowel(sound[v])) v++;
        int e = v; while (e < sound.Length && Vowel(sound[e])) e++;
        return (sound.Substring(v, e - v), sound.Substring(e));
    }
    // Consonants by family, so time / mine or cup / cut read as close codas.
    static string Family(string coda)
    {
        string c = Regex.Replace(coda, "[aeiouy]", "");
        c = c.Replace("ng", "N").Replace("th", "T").Replace("sh", "S").Replace("ch", "T");
        var sb = new System.Text.StringBuilder();
        foreach (char ch in c) sb.Append(ch switch { 'm' or 'n' or 'N' => 'n', 'b' or 'p' => 'p', 'd' or 't' or 'T' => 't', 'g' or 'k' or 'c' or 'q' or 'x' => 'k', 'v' or 'f' => 'f', 'z' or 's' or 'S' or 'j' => 's', _ => ch });
        return sb.ToString();
    }
    static readonly string[][] NearVowels = { new[] { "ee", "i", "ea", "y" }, new[] { "ei", "e", "ai" }, new[] { "oo", "u", "ue" }, new[] { "o", "au", "aw", "a" }, new[] { "oa", "o" } };
    static bool Near(string a, string b) => NearVowels.Any(g => g.Contains(a) && g.Contains(b));
    public static Grade Compare(Word a, Word b)
    {
        if (a.Sound.Length == 0 || b.Sound.Length == 0 || Clean(a.Text) == Clean(b.Text)) return Grade.None;
        if (a.Sound == b.Sound) return Grade.Perfect;
        if (a.Nucleus.Length > 0 && a.Nucleus == b.Nucleus)
        {
            string fa = Family(a.Coda), fb = Family(b.Coda);
            if (fa == fb) return Grade.Slant;
            string ta = fa.TrimEnd('s', 't'), tb = fb.TrimEnd('s', 't');
            if (ta == tb || ta == fb || fa == tb) return Grade.Slant;
            return Grade.Imperfect;
        }
        if (a.Nucleus.Length > 0 && b.Nucleus.Length > 0 && Near(a.Nucleus, b.Nucleus) && Family(a.Coda) == Family(b.Coda) && a.Coda.Length > 0) return Grade.Imperfect;
        return Grade.None;
    }

    public sealed class Result { public Word[] Words = Array.Empty<Word>(); public Link[] Links = Array.Empty<Link>(); public int Families; }
    // words: per line, the words (text, syllable texts, stresses); stanzaOfLine maps lines to stanzas.
    public static Result Analyse(IReadOnlyList<(int line, int stanza, string text, string[] syllables, int[] stress)> input, int lines)
    {
        var words = new Word[input.Count];
        var lastOfLine = new int[lines]; for (int i = 0; i < lines; i++) lastOfLine[i] = -1;
        for (int i = 0; i < input.Count; i++)
        {
            var (line, stanza, text, syl, stress) = input[i];
            string sound = RhymeSound(text, syl, stress); var (n, c) = Split(sound);
            words[i] = new Word { Text = text, Sound = sound, Nucleus = n, Coda = c, Line = line, Stanza = stanza, Index = i, Weak = WeakWords.Contains(Clean(text)) || Clean(text).Length < 2, Hue = Hue(n) };
            // Different codas on one vowel differ a little in brightness, never in hue.
            words[i].Shade = (Family(c).Aggregate(17, (h, ch) => h * 31 + ch) & 3) / 3f;
            if (line >= 0 && line < lines) lastOfLine[line] = i;
        }
        foreach (int i in lastOfLine) if (i >= 0) words[i].End = true;
        var links = new List<Link>(); var parent = Enumerable.Range(0, words.Length).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Join(int a, int b, Grade g, bool end) { links.Add(new Link { A = a, B = b, Grade = g, End = end }); parent[Find(a)] = Find(b); }
        // End words: each to the next end word in its stanza it rhymes with (at any grade).
        var ends = words.Where(w => w.End).ToArray();
        for (int i = 0; i < ends.Length; i++)
            for (int j = i + 1; j < ends.Length && ends[j].Stanza == ends[i].Stanza; j++)
            { var g = Compare(ends[i], ends[j]); if (g != Grade.None) { Join(ends[i].Index, ends[j].Index, g, true); break; } }
        // Inside lines: stressed content words, perfect or slant within two lines, assonance within
        // the same or the next line; each word to its two nearest partners.
        var content = words.Where(w => !w.Weak).ToArray();
        for (int i = 0; i < content.Length; i++)
        {
            int partners = 0;
            for (int j = i + 1; j < content.Length && partners < 2; j++)
            {
                var a = content[i]; var b = content[j];
                if (b.Stanza != a.Stanza || b.Line - a.Line > 2) break;
                if (a.End && b.End) continue;
                var g = Compare(a, b);
                if (g >= Grade.Slant || g == Grade.Imperfect && b.Line - a.Line <= 1) { Join(a.Index, b.Index, g, false); partners++; }
            }
        }
        // Families: joined sets of two or more, numbered in order of first appearance.
        var number = new Dictionary<int, int>(); var size = new Dictionary<int, int>();
        foreach (var w in words) { int r = Find(w.Index); size[r] = size.TryGetValue(r, out var s) ? s + 1 : 1; }
        foreach (var w in words) { int r = Find(w.Index); if (size[r] < 2) continue; if (!number.TryGetValue(r, out var f)) number[r] = f = number.Count; w.Family = f; }
        return new Result { Words = words, Links = links.ToArray(), Families = number.Count };
    }
}
