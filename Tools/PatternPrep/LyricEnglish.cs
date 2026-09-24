using System.Text.RegularExpressions;

// Enough English for lyrics, without a pronouncing dictionary: syllables, lexical stress, a
// spelling-based rhyme key and meter. A lyric sheet can split syllables itself with hyphens
// (twin-kle); the rules below fill in the rest. Weak words (the, of, I, you…) are unstressed
// but may take a beat, so the meter scan treats them as either.
public static class LyricEnglish
{
    // Unstressed function words. Content words of one syllable are stressed.
    static readonly HashSet<string> Weak = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","and","or","but","nor","of","to","in","on","at","by","for","from","with","as","if","is","was","were","are","am","be","been",
        "it","its","it's","his","her","him","my","your","our","their","them","thee","thy","thou","you","he","she","we","they","me","us","i","i'm",
        "that","than","so","do","does","did","has","have","had","hath","can","could","shall","should","will","would","may","might","must","art",
        "what","which","who","whom","whose","when","then","there","where","up","out","too","more","such","this","these","those","not","all","o","oh",
        "into","onto","upon","till","nor","yet","just","like","let","how","each","some",
    };
    // Stress patterns (1 primary, 2 secondary, 0 unstressed) for words the rules miss, and the
    // public-domain lyrics used by the tests.
    static readonly Dictionary<string, string> Lexicon = new(StringComparer.OrdinalIgnoreCase)
    {
        ["above"]="01",["about"]="01",["again"]="01",["against"]="01",["alone"]="01",["along"]="01",["among"]="01",["around"]="01",["away"]="01",["awake"]="01",
        ["before"]="01",["behind"]="01",["below"]="01",["beneath"]="01",["beside"]="01",["between"]="01",["beyond"]="01",["become"]="01",["believe"]="01",
        ["compare"]="01",["complete"]="01",["complexion"]="010",["declines"]="01",["decline"]="01",["forget"]="01",["forgive"]="01",["upon"]="01",["until"]="01",
        ["untrimm'd"]="01",["untrimmed"]="01",["return"]="01",["remain"]="01",["remember"]="010",["tonight"]="01",["today"]="01",["tomorrow"]="010",
        ["temperate"]="100",["diamond"]="10",["traveller"]="100",["trav'ller"]="10",["every"]="10",["heaven"]="10",["sometime"]="10",["nokomis"]="010",
        ["bigseawater"]="1010",["pinetrees"]="11",["wigwam"]="10",["gitche"]="10",["gumee"]="10",["lovely"]="10",["summer's"]="10",["nature's"]="10",
        ["alabama"]="2010",["louisiana"]="02010",["susanna"]="010",["banjo"]="10",["hiawatha"]="2010",["into"]="10",["onto"]="10",["within"]="01",
        ["without"]="01",["myself"]="01",["yourself"]="01",["himself"]="01",["because"]="01",["hello"]="01",["guitar"]="01",["machine"]="01",
    };
    static readonly string[] SecondPrefixes = { "a", "be", "de", "re", "com", "con", "dis", "ex", "for", "im", "in", "un", "mis", "per", "pro", "pre", "sub", "sur", "trans", "with" };
    static readonly HashSet<string> FirstAnyway = new(StringComparer.OrdinalIgnoreCase)
    { "comfort","common","concert","conquer","consonant","constant","contact","contest","content","context","country","destiny","distance","dismal","exit","extra","expert","forest","forward","image","income","index","inner","instant","interest","into","permit","present","product","promise","province","record","refuge","rescue","decent","demon","desert","region","reason","reckon","under","other","over","ever","never" };

    public static string Clean(string word) => new string(word.ToLowerInvariant().Where(c => char.IsLetter(c) || c == '\'').ToArray()).Trim('\'');
    static bool Vowel(char c) => "aeiouy".IndexOf(c) >= 0;

    // Split a word into syllables, keeping the letters of the original spelling.
    public static string[] Syllables(string word)
    {
        string w = Clean(word); if (w.Length == 0) return Array.Empty<string>();
        // Vowel groups, with y a consonant at a word's start or before a vowel.
        var groups = new List<(int start, int end)>();
        for (int i = 0; i < w.Length;)
        {
            bool v = Vowel(w[i]) && !(w[i] == 'y' && (i == 0 || i + 1 < w.Length && Vowel(w[i + 1]) && w[i + 1] != 'y'));
            if (!v) { i++; continue; }
            int j = i + 1;
            // A y after a vowel belongs to it (day, key, boy) unless a vowel follows (may-or).
            while (j < w.Length && Vowel(w[j]) && (w[j] != 'y' || !(j + 1 < w.Length && Vowel(w[j + 1]))))
            {
                // Vowel pairs heard as two syllables (li-on, di-et, du-al), except after c, g, s, t, x (spe-cial, na-tion).
                string pair = w.Substring(j - 1, 2);
                bool split = pair is "ia" or "io" or "iu" or "eo" or "ua" or "uo" or "ie" && j + 1 < w.Length && !(j >= 2 && "cgstx".IndexOf(w[j - 2]) >= 0) && !(pair == "ie" && (w.EndsWith("ies") || w.EndsWith("ied") || w.EndsWith("ief") || w.EndsWith("ield") || j + 1 == w.Length - 1));
                if (pair == "ua" && j >= 2 && w[j - 2] == 'q') split = false;
                if (split) break;
                j++;
            }
            groups.Add((i, j)); i = j;
        }
        if (groups.Count == 0) return new[] { w };
        // Silent endings: a final e (but not consonant+le), -es and -ed where they add no vowel sound.
        var last = groups[^1];
        if (groups.Count > 1)
        {
            string tail = w[last.start..];
            bool consonantLe = tail == "e" && w.Length >= 3 && w[^2] == 'l' && !Vowel(w[^3]);
            if (tail == "e" && last.end == w.Length && !consonantLe) groups.RemoveAt(groups.Count - 1);
            else if (tail == "es" && w.Length >= 4 && !Regex.IsMatch(w, "(s|x|z|ch|sh|ce|ge|se|ze)es$") && !(w[^3] == 'l' && !Vowel(w[^4]))) groups.RemoveAt(groups.Count - 1);
            else if (tail == "ed" && w.Length >= 4 && w[^3] != 't' && w[^3] != 'd') groups.RemoveAt(groups.Count - 1);
        }
        if (groups.Count == 1) return new[] { w };
        // Boundaries between nuclei: V-CV, VC-CV, digraphs kept whole, consonant + le last.
        var cuts = new List<int>();
        for (int g = 0; g + 1 < groups.Count; g++)
        {
            int a = groups[g].end, b = groups[g + 1].start, count = b - a;
            // Consonant + le is a syllable of its own, with the consonant before it: lit-tle, ta-ble.
            if (g + 1 == groups.Count - 1 && w.EndsWith("le") && count >= 1 && groups[g + 1].start == w.Length - 1) { cuts.Add(count >= 2 ? b - 2 : b - 1); continue; }
            if (count == 0) { cuts.Add(a); continue; }
            if (count == 1) { cuts.Add(a); continue; }
            string cluster = w.Substring(a, count);
            // After an open prefix a blend starts the next syllable: de-clines, re-ply, a-pron.
            if (g == 0 && w[..a] is "a" or "be" or "de" or "pre" or "re" or "e" && Regex.IsMatch(cluster, "^(bl|br|cl|cr|dr|fl|fr|gl|gr|pl|pr|sc|sk|sl|sp|st|tr|tw)")) { cuts.Add(a); continue; }
            int cut = a + 1;
            foreach (string d in new[] { "ch", "sh", "th", "ph", "wh", "gh", "ck", "ng" })
                if (cluster.StartsWith(d)) { cut = a + 2; break; }
            if (count >= 3 && cut == a + 1 && Regex.IsMatch(cluster[1..], "^(str|spr|scr|thr|chr|shr)")) cut = a + 1;
            cuts.Add(Math.Min(cut, b));
        }
        var result = new List<string>(); int from = 0;
        foreach (int c in cuts) { if (c > from) { result.Add(w[from..c]); from = c; } }
        result.Add(w[from..]);
        return result.Where(s => s.Length > 0).ToArray();
    }

    public static bool IsWeak(string word) => Weak.Contains(Clean(word));

    // Lexical stress per syllable: 1 primary, 2 secondary, 0 unstressed.
    public static int[] Stress(string word, int syllables)
    {
        string w = Clean(word).Replace("-", "");
        if (syllables <= 0) return Array.Empty<int>();
        if (Lexicon.TryGetValue(w, out var known) && known.Length == syllables) return known.Select(c => c - '0').ToArray();
        if (syllables == 1) return new[] { Weak.Contains(w) ? 0 : 1 };
        var stress = new int[syllables];
        int primary = 0;
        string[] parts = Syllables(w);
        string first = parts.Length > 0 ? parts[0] : w;
        if (!FirstAnyway.Contains(w) && SecondPrefixes.Contains(first) && syllables >= 2) primary = 1;
        if (Regex.IsMatch(w, "(tion|sion|cian|cial|tial|cious|tious|ic|ical|ity|ian|ial)s?$") && syllables >= 3) primary = syllables - (Regex.IsMatch(w, "(ity|ical)$") ? 3 : 2);
        if (Regex.IsMatch(w, "(ee|eer|ese|ette|esque|oon)s?$")) primary = syllables - 1;
        primary = Math.Clamp(primary, 0, syllables - 1);
        stress[primary] = 1;
        for (int k = primary + 2; k < syllables - 1; k += 2) stress[k] = 2;
        return stress;
    }

    // Rhyme: the sound from a stressed vowel to the end of the word, from spelling.
    static readonly Dictionary<string, string> Sounds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["are"]="ar",["gone"]="on",["one"]="un",["done"]="un",["none"]="un",["won"]="un",["son"]="un",["come"]="um",["some"]="um",["love"]="uv",["dove"]="uv",["above"]="uv",
        ["move"]="oov",["prove"]="oov",["have"]="av",["give"]="iv",["live"]="iv",["you"]="oo",["through"]="oo",["to"]="oo",["do"]="oo",["who"]="oo",["two"]="oo",["too"]="oo",
        ["eye"]="ai",["i"]="ai",["said"]="ed",["again"]="en",["been"]="in",["were"]="er",["there"]="air",["where"]="air",["their"]="air",["here"]="eer",["heart"]="art",
        ["earth"]="erth",["own"]="oan",["grown"]="oan",["known"]="oan",["shown"]="oan",["blown"]="oan",["flown"]="oan",["town"]="aun",["down"]="aun",["crown"]="aun",
        ["how"]="au",["now"]="au",["cow"]="au",["know"]="oa",["grow"]="oa",["show"]="oa",["low"]="oa",["slow"]="oa",["snow"]="oa",["flow"]="oa",["go"]="oa",["so"]="oa",
        ["no"]="oa",["toe"]="oa",["ow'st"]="oast",["grow'st"]="oast",["wander'st"]="erst",["of"]="uv",["was"]="uz",["what"]="ut",["break"]="eik",["great"]="eit",["steak"]="eik",
        ["cry"]="ai",["my"]="ai",["by"]="ai",["why"]="ai",["sky"]="ai",["fly"]="ai",["die"]="ai",["lie"]="ai",["tie"]="ai",["me"]="ee",["be"]="ee",["he"]="ee",["she"]="ee",
        ["we"]="ee",["thee"]="ee",["the"]="uh",["a"]="uh",["knee"]="ee",["see"]="ee",["sea"]="ee",
    };
    // Letters to a rough sound class, applied to a rhyme's tail (onset removed).
    static readonly (string pattern, string sound)[] Rules =
    {
        // Sounds come out in capitals so later rules, which read letters, leave them alone.
        ("igh", "AI"), ("eigh", "EI"), ("ough", "O"), ("augh", "O"),
        ("tch", "ch"), ("dge", "j"), ("ck", "k"), ("ph", "f"), ("qu", "kw"),
        ("ay|ai|ey$|ei", "EI"), ("ee|ea|ie(?=.)", "EE"), ("oa|oe$|ow$", "OA"), ("oo", "OO"), ("ou|ow", "AU"), ("ue$|ew", "OO"), ("au|aw", "O"), ("oi|oy", "OI"),
        ("^a([^aeiouy])e(s?)$", "EI$1$2"), ("^i([^aeiouy])e(s?)$", "AI$1$2"), ("^o([^aeiouy])e(s?)$", "OA$1$2"), ("^u([^aeiouy])e(s?)$", "OO$1$2"), ("^e([^aeiouy])e(s?)$", "EE$1$2"),
        ("^y$", "AI"), ("y$", "EE"), ("e$", ""),
        (@"([bcdfgjklmnprstvz])\1", "$1"), ("'", ""),
    };
    public static string Sound(string tail)
    {
        string s = tail.ToLowerInvariant();
        foreach (var (pattern, sound) in Rules) s = Regex.Replace(s, pattern, sound);
        return (s.Length == 0 ? tail : s).ToLowerInvariant();
    }
    static string Tail(string syllable)
    {
        int v = 0; while (v < syllable.Length && !(Vowel(syllable[v]) && !(syllable[v] == 'y' && v == 0 && syllable.Length > 1))) v++;
        return v >= syllable.Length ? syllable : syllable[v..];
    }
    // The perfect-rhyme key: from the last stressed syllable to the end. Weak key: last syllable only.
    public static (string perfect, string weak) RhymeKey(string word, string[] syllables, int[] stress)
    {
        string w = Clean(word);
        if (Sounds.TryGetValue(w, out var known)) return (known, known);
        if (syllables.Length == 0) return ("", "");
        int from = Array.FindLastIndex(stress, s => s > 0); if (from < 0) from = syllables.Length - 1;
        string tail = Tail(syllables[from]) + string.Concat(syllables.Skip(from + 1));
        string lastSyl = syllables[^1];
        string weak = Sounds.TryGetValue(lastSyl, out var k2) ? k2 : Sound(Tail(lastSyl));
        return (Sound(tail), weak);
    }
    // The consonants a stressed syllable begins with (for alliteration): "shores" → "sh".
    public static string Onset(string syllable)
    {
        string s = syllable.ToLowerInvariant().Trim('\'');
        int v = 0; while (v < s.Length && !Vowel(s[v])) v++;
        string onset = s[..v];
        return onset switch { "c" when s.Length > 1 && "eiy".IndexOf(s[1]) >= 0 => "s", "c" or "k" or "ck" => "k", "ph" => "f", "wh" => "w", "kn" => "n", "wr" => "r", _ => onset };
    }

    // Meter: the foot and count that best explain a stress pattern, where '?' (a weak word)
    // may take either. Trochaic and dactylic lines may drop their last unstressed syllables
    // (catalexis); iambic and anapestic ones their first (headless), or add a feminine ending.
    static readonly (string name, string foot)[] Feet = { ("iambic", "x/"), ("trochaic", "/x"), ("anapestic", "xx/"), ("dactylic", "/xx") };
    static readonly string[] Counts = { "", "monometer", "dimeter", "trimeter", "tetrameter", "pentameter", "hexameter", "heptameter", "octameter" };
    public static (string meter, string foot, double score) Scan(string pattern, string prefer = null)
    {
        if (pattern.Length == 0) return ("", "", 0);
        var best = ("", "", -1.0);
        foreach (var (name, foot) in Feet)
            for (int count = 1; count <= 8; count++)
            {
                string ideal = string.Concat(Enumerable.Repeat(foot, count));
                var variants = new List<(string text, string note)> { (ideal, "") };
                if (foot[^1] == 'x') { variants.Add((ideal.TrimEnd('x'), "catalectic")); if (foot.Length == 3) variants.Add((ideal[..^1], "catalectic")); }
                else { variants.Add((ideal[1..], "headless")); variants.Add((ideal + "x", "feminine ending")); if (foot.Length == 3) variants.Add((ideal[2..], "headless")); }
                foreach (var (text, note) in variants)
                {
                    if (text.Length != pattern.Length) continue;
                    int match = 0, strong = 0; for (int i = 0; i < text.Length; i++) { if (pattern[i] == '?' || pattern[i] == text[i]) match++; if (pattern[i] != '?' && pattern[i] == text[i]) strong++; }
                    double score = match / (double)text.Length + .02 * strong / text.Length - (note == "" ? 0 : note == "catalectic" ? .01 : .02) + (prefer == name ? .03 : 0);
                    if (score > best.Item3) best = ($"{name} {Counts[count]}{(note.Length > 0 ? $" ({note})" : "")}", name, score);
                }
            }
        return best;
    }
}
