using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Midi;

// A lyric sheet synced to the music and read for meter and rhyme.
//
// Sheet (lyrics.txt beside the MIDI): stanzas under [Name] headers; [Rap 1 | spoken] or a
// name containing "rap" or "spoken" marks spoken lines, the rest are sung. Hyphens split
// syllables (twin-kle) and a trailing _ holds a syllable over the next note (star_).
//
// Sync, best evidence first:
//   1. MIDI lyric events (karaoke): each event times one syllable.
//   2. The vocal lane's notes: sung syllables are laid on notes in order, one per note, a
//      held syllable taking several, lines ending where the melody breathes.
//   3. Audio alignment (lyrics.timing.json from Tools/SongLibrary/lyric_sync.py): word times,
//      for spoken lines and for sung lines when the vocal has no notes.
//   4. Spoken lines with no timing are placed on the beat grid, stresses on downbeats.
//
// Reading: every syllable's lexical stress, where it falls in the bar, how long it is held
// (teeth of the drum rack), its emphasis and any vibrato (from pitch bends or the audio);
// every line's meter (iambic pentameter, trochaic tetrameter…), end rhyme and front rhyme
// (repeated opening words, a head rhyme or alliteration); and matchups between the same
// lines of stanzas that share a pattern, beat against beat.
public static class Lyrics
{
    public sealed class SheetStanza { public string Name = ""; public bool Spoken; public List<SheetLine> Lines = new(); }
    public sealed class SheetLine { public string Text = ""; public List<SheetWord> Words = new(); }
    public sealed class SheetWord { public string Text = "", Key = ""; public string[] Syllables = Array.Empty<string>(); public bool[] Hold = Array.Empty<bool>(); public int[] Stress = Array.Empty<int>(); public bool Weak; }

    public static List<SheetStanza> Parse(string text)
    {
        var stanzas = new List<SheetStanza>(); SheetStanza current = null;
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#")) continue;
            if (line.Length == 0) { if (current != null && current.Lines.Count > 0) current = null; continue; }
            var header = Regex.Match(line, @"^\[(.+)\]$");
            if (header.Success)
            {
                var parts = header.Groups[1].Value.Split('|').Select(p => p.Trim()).ToArray();
                string name = parts[0], mode = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
                bool spoken = mode.Contains("spoken") || mode.Contains("rap") || (mode.Length == 0 && Regex.IsMatch(name, @"\b(rap|spoken)\b", RegexOptions.IgnoreCase));
                current = new SheetStanza { Name = name, Spoken = spoken && !mode.Contains("sung") }; stanzas.Add(current); continue;
            }
            if (current == null) { current = new SheetStanza { Name = $"Stanza {stanzas.Count + 1}" }; stanzas.Add(current); }
            var sheetLine = new SheetLine { Text = line };
            foreach (string token in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = Regex.Replace(token, @"[^\p{L}'\-_]", "").Trim('-', '\'');
                if (t.Length == 0 || t.All(c => c == '_' || c == '-')) continue;
                var parts = t.Contains('-') ? t.Split('-', StringSplitOptions.RemoveEmptyEntries) : null;
                string[] syllables; bool[] hold;
                if (parts != null) { syllables = parts.Select(p => p.TrimEnd('_')).ToArray(); hold = parts.Select(p => p.EndsWith("_")).ToArray(); }
                else
                {
                    bool held = t.EndsWith("_"); string plain = t.TrimEnd('_');
                    var split = LyricEnglish.Syllables(plain);
                    // Keep the sheet's own capitals: cut the original spelling where the rules cut.
                    syllables = new string[split.Length]; int at = 0; string letters = new string(plain.Where(c => char.IsLetter(c) || c == '\'').ToArray()).Trim('\'');
                    for (int k = 0; k < split.Length; k++) { int len = Math.Min(split[k].Length, letters.Length - at); syllables[k] = letters.Substring(at, Math.Max(0, len)); at += len; }
                    if (at < letters.Length) syllables[^1] += letters[at..];
                    hold = syllables.Select((_, k) => held && k == syllables.Length - 1).ToArray();
                }
                string word = string.Concat(syllables);
                // A capital inside the line is a name (the buds of May), never a weak word.
                bool name = sheetLine.Words.Count > 0 && word.Length > 0 && char.IsUpper(word[0]) && word != "I";
                var stress = LyricEnglish.Stress(word, syllables.Length);
                if (name && syllables.Length == 1) stress[0] = 1;
                sheetLine.Words.Add(new SheetWord { Text = word, Key = LyricEnglish.Clean(word), Syllables = syllables, Hold = hold, Stress = stress, Weak = syllables.Length == 1 && !name && LyricEnglish.IsWeak(word) });
            }
            if (sheetLine.Words.Count > 0) current.Lines.Add(sheetLine);
        }
        return stanzas.Where(s => s.Lines.Count > 0).ToList();
    }

    public sealed class LyricEvent { public double Beat; public string Text = ""; public int Track; }
    // Lyric meta events, or karaoke text events (after a @K header), in beats.
    public static List<LyricEvent> ReadEvents(MidiFile midi)
    {
        var lyric = new List<LyricEvent>(); var text = new List<LyricEvent>(); bool karaoke = false;
        for (int t = 0; t < midi.Tracks; t++)
            foreach (var e in midi.Events[t].OfType<TextEvent>())
            {
                var item = new LyricEvent { Beat = e.AbsoluteTime / (double)midi.DeltaTicksPerQuarterNote, Text = e.Text ?? "", Track = t };
                if (e.MetaEventType == MetaEventType.Lyric) lyric.Add(item);
                else if (e.MetaEventType == MetaEventType.TextEvent) { if (item.Text.StartsWith("@K")) karaoke = true; else if (!item.Text.StartsWith("@")) text.Add(item); }
            }
        return (lyric.Count > 0 ? lyric : karaoke ? text : new List<LyricEvent>()).OrderBy(e => e.Beat).ToList();
    }

    // Audio alignment written by lyric_sync.py: words (and optionally syllables) in seconds,
    // and vibrato spans found in the vocal's pitch track.
    public sealed class Timing
    {
        public sealed class Item { public string text { get; set; } = ""; public double start { get; set; } public double end { get; set; } public float confidence { get; set; } = 1; public List<double> syllables { get; set; } = new(); }
        public sealed class Wobble { public double start { get; set; } public double end { get; set; } public float rate { get; set; } public float depth { get; set; } }
        public List<Item> words { get; set; } = new();
        public List<Wobble> vibrato { get; set; } = new();
        public string method { get; set; } = "";
    }

    sealed class Flat
    {
        public int Stanza, Line, Word, Index; public SheetWord Source; public string Text = "";
        public double Start = double.NaN, End = double.NaN; public int Pitch = -1; public float Velocity = .7f; public bool Spoken, Hold;
        public float Vibrato, VibratoRate; public double VibratoStart;
    }
    static string Letters(string s) => new string(s.ToLowerInvariant().Where(char.IsLetter).ToArray());

    public static void Build(PreparedPatternSong song, MidiCycleAnalysis cycles, MidiFile midi, string sheetPath, string timingPath, int bendRange = 2)
    {
        song.Lyrics = null;
        if (sheetPath == null || !File.Exists(sheetPath)) return;
        var stanzas = Parse(File.ReadAllText(sheetPath));
        if (stanzas.Count == 0) return;
        var flat = new List<Flat>();
        int lineIndex = 0;
        for (int s = 0; s < stanzas.Count; s++)
            foreach (var line in stanzas[s].Lines)
            {
                for (int w = 0; w < line.Words.Count; w++)
                    for (int k = 0; k < line.Words[w].Syllables.Length; k++)
                        flat.Add(new Flat { Stanza = s, Line = lineIndex, Word = w, Index = k, Source = line.Words[w], Text = line.Words[w].Syllables[k], Spoken = stanzas[s].Spoken, Hold = line.Words[w].Hold[k] });
                lineIndex++;
            }
        var sheet = new PreparedPatternSong.LyricSheet { Source = Path.GetFileName(sheetPath) };
        var syncs = new List<string>();
        // The vocal lane: explicit lead vocal, a track named vocal, else the lane the lyric events sit on.
        var lanes = song.Notes.Where(n => n.Channel != 10).GroupBy(n => (n.Track, n.Channel)).ToDictionary(g => g.Key, g => g.OrderBy(n => n.Beat).ToArray());
        var events = ReadEvents(midi);
        (int track, int channel) vocal = (-1, -1);
        if (events.Count > 0)
        {
            // The lane whose onsets the events keep landing on, and that lands on them in turn
            // (keys also strike at many syllables, but most of their chords carry none).
            var onsets = events.Select(e => Math.Round(e.Beat * 48)).ToHashSet();
            double Fit(MidiCycleAnalysis.Hit[] n) { var own = n.Select(x => Math.Round(x.Beat * 48)).ToHashSet(); return own.Count(onsets.Contains) / Math.Sqrt(own.Count * (double)onsets.Count); }
            var byEvents = lanes.OrderByDescending(l => Fit(l.Value)).FirstOrDefault();
            if (byEvents.Value != null && Fit(byEvents.Value) > .2) vocal = byEvents.Key;
        }
        if (vocal.track < 0)
        {
            var named = lanes.Keys.Where(k => k.Track == song.LeadVocalTrack || k.Track < song.TrackNames.Length && Regex.IsMatch(song.TrackNames[k.Track], "vocal|voice|sing", RegexOptions.IgnoreCase) && !Regex.IsMatch(song.TrackNames[k.Track], "back|rap", RegexOptions.IgnoreCase)).ToList();
            vocal = named.Count > 0 ? named.OrderByDescending(k => lanes[k].Length).First() : lanes.Count > 0 ? lanes.OrderByDescending(l => l.Value.Average(n => n.Pitch)).First().Key : (-1, -1);
        }
        sheet.Track = vocal.track; sheet.Channel = vocal.channel;
        var notes = vocal.track >= 0 ? lanes[vocal] : Array.Empty<MidiCycleAnalysis.Hit>();

        // 1. Lyric events.
        if (events.Count > 0 && SyncEvents(flat, events)) syncs.Add("MIDI lyric events");
        // 2. The vocal's notes for sung syllables still untimed: MIDI vocal notes aligned to the
        //    recording are exact, and laying the syllables on them by phrase and breath is more
        //    reliable than syllable onsets heard in a stem.
        if (flat.Any(f => !f.Spoken && double.IsNaN(f.Start)) && notes.Length > 0 && SyncNotes(flat.Where(f => !f.Spoken && double.IsNaN(f.Start)).ToList(), notes)) syncs.Add("vocal notes");
        // 3. Audio alignment for what remains: spoken lines, or sung lines with no vocal notes.
        Timing timing = null;
        if (timingPath != null && File.Exists(timingPath))
        {
            timing = JsonSerializer.Deserialize<Timing>(File.ReadAllText(timingPath));
            if (timing != null && flat.Any(f => double.IsNaN(f.Start)) && SyncWords(flat, timing, cycles)) syncs.Add("audio alignment" + (timing.method.Length > 0 ? $" ({timing.method})" : ""));
        }
        // Every sung syllable takes its note's pitch, length and loudness; a held syllable runs
        // through the notes that follow it until the next syllable.
        var timed = flat.Where(f => !double.IsNaN(f.Start)).OrderBy(f => f.Start).ToList();
        for (int i = 0; i < timed.Count; i++)
        {
            var f = timed[i]; double next = i + 1 < timed.Count ? timed[i + 1].Start : double.PositiveInfinity;
            var note = f.Spoken ? null : notes.FirstOrDefault(n => Math.Abs(n.Beat - f.Start) < 1 / 24.0) ?? notes.LastOrDefault(n => n.Beat <= f.Start + 1e-6 && n.Beat + n.Length > f.Start + 1e-6);
            if (note != null)
            {
                f.Pitch = note.Pitch; f.Velocity = note.Velocity;
                double end = note.Beat + note.Length;
                foreach (var n in notes.Where(n => n.Beat > note.Beat + 1e-6 && n.Beat < next - 1e-6)) end = Math.Max(end, n.Beat + n.Length);
                f.End = double.IsNaN(f.End) ? Math.Min(end, next) : f.End;
            }
            // A held syllable (star_) runs up to a bar; a spoken one otherwise lasts an eighth.
            if (double.IsNaN(f.End)) f.End = Math.Min(next, f.Start + (f.Hold ? 4 : f.Spoken ? .5 : 1));
            f.End = Math.Max(f.End, f.Start + 1 / 16.0);
        }
        // 4. Spoken stanzas still untimed are placed on the beat grid.
        if (flat.Any(f => f.Spoken && double.IsNaN(f.Start)) && Place(song, cycles, flat)) syncs.Add("estimated spoken placement");
        flat.RemoveAll(f => double.IsNaN(f.Start));
        if (flat.Count == 0) { Console.WriteLine($"  lyrics: {sheet.Source} could not be synced (no lyric events, timing or vocal notes)"); return; }
        sheet.Sync = string.Join(" + ", syncs);
        Vibrato(flat, midi, vocal, cycles, bendRange, timing);
        Read(song, cycles, sheet, stanzas, flat);
        song.Lyrics = sheet;
    }

    // Align event syllables to sheet syllables by their letters; melisma events extend the last.
    static bool SyncEvents(List<Flat> flat, List<LyricEvent> events)
    {
        var items = new List<(double beat, string letters)>();
        foreach (var e in events)
        {
            string t = e.Text.Trim().TrimStart('/', '\\').Trim();
            string letters = Letters(t);
            if (letters.Length == 0) continue;
            items.Add((e.Beat, letters));
        }
        if (items.Count == 0) return false;
        int n = flat.Count, m = items.Count;
        var cost = new double[n + 1, m + 1]; var move = new byte[n + 1, m + 1];
        for (int i = 1; i <= n; i++) { cost[i, 0] = i; move[i, 0] = 1; }
        for (int j = 1; j <= m; j++) { cost[0, j] = j; move[0, j] = 2; }
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                double match = cost[i - 1, j - 1] + Distance(Letters(flat[i - 1].Text), items[j - 1].letters);
                double skipSheet = cost[i - 1, j] + 1, skipEvent = cost[i, j - 1] + 1;
                if (match <= skipSheet && match <= skipEvent) { cost[i, j] = match; move[i, j] = 0; }
                else if (skipSheet <= skipEvent) { cost[i, j] = skipSheet; move[i, j] = 1; }
                else { cost[i, j] = skipEvent; move[i, j] = 2; }
            }
        int matched = 0;
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && move[i, j] == 0) { if (Distance(Letters(flat[i - 1].Text), items[j - 1].letters) < .5) { flat[i - 1].Start = items[j - 1].beat; matched++; } i--; j--; }
            else if (i > 0 && (j == 0 || move[i, j] == 1)) i--;
            else j--;
        }
        return matched > 0;
    }
    static double Distance(string a, string b)
    {
        if (a == b) return 0;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++) for (int j = 1; j <= b.Length; j++) d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length] / (double)Math.Max(1, Math.Max(a.Length, b.Length));
    }

    // Word times from the audio: align words, then share each word's span among its syllables
    // by their letters.
    static bool SyncWords(List<Flat> flat, Timing timing, MidiCycleAnalysis cycles)
    {
        var words = flat.GroupBy(f => (f.Line, f.Word)).Select(g => g.ToList()).ToList();
        var items = timing.words.Where(w => Letters(w.text).Length > 0).ToList();
        if (items.Count == 0) return false;
        int n = words.Count, m = items.Count;
        var cost = new double[n + 1, m + 1]; var move = new byte[n + 1, m + 1];
        for (int i = 1; i <= n; i++) { cost[i, 0] = i; move[i, 0] = 1; }
        for (int j = 1; j <= m; j++) { cost[0, j] = j; move[0, j] = 2; }
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                double match = cost[i - 1, j - 1] + Distance(Letters(words[i - 1][0].Source.Text), Letters(items[j - 1].text)) * 1.5;
                double a = cost[i - 1, j] + 1, b = cost[i, j - 1] + 1;
                if (match <= a && match <= b) { cost[i, j] = match; move[i, j] = 0; } else if (a <= b) { cost[i, j] = a; move[i, j] = 1; } else { cost[i, j] = b; move[i, j] = 2; }
            }
        int matched = 0;
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && move[i, j] == 0)
            {
                var word = words[i - 1]; var item = items[j - 1];
                if (double.IsNaN(word[0].Start) && Distance(Letters(word[0].Source.Text), Letters(item.text)) < .6)
                {
                    double start = cycles.BeatAt(item.start), end = cycles.BeatAt(Math.Max(item.end, item.start + .05));
                    if (item.syllables != null && item.syllables.Count == word.Count)
                        // The aligner timed each syllable: each lasts until the next, the last until the word ends.
                        for (int k = 0; k < word.Count; k++) { word[k].Start = cycles.BeatAt(item.syllables[k]); word[k].End = k + 1 < word.Count ? cycles.BeatAt(item.syllables[k + 1]) : Math.Max(end, word[k].Start + 1 / 16.0); }
                    else
                    {
                        // Otherwise share the word's span among its syllables by their letters.
                        double total = word.Sum(f => Math.Max(1, f.Text.Length)), at = start;
                        foreach (var f in word) { double share = (end - start) * Math.Max(1, f.Text.Length) / total; f.Start = at; f.End = at + share; at += share; }
                    }
                    matched++;
                }
                i--; j--;
            }
            else if (i > 0 && (j == 0 || move[i, j] == 1)) i--; else j--;
        }
        return matched > 0;
    }

    // Sung syllables on the vocal notes, in order. A syllable may hold over several notes
    // (cheaper on a stressed or marked syllable), a note may go unsung (costly), and lines
    // prefer to end where the melody breathes rather than breathe inside a line.
    static bool SyncNotes(List<Flat> syllables, MidiCycleAnalysis.Hit[] all)
    {
        // Chords in the vocal lane: sing the top note.
        var notes = all.GroupBy(n => Math.Round(n.Beat * 48)).Select(g => g.OrderByDescending(n => n.Pitch).First()).OrderBy(n => n.Beat).ToArray();
        int s = syllables.Count, m = notes.Length; if (s == 0 || m == 0) return false;
        bool Breath(int j) => j + 1 >= m || notes[j + 1].Beat - (notes[j].Beat + notes[j].Length) >= .75 || notes[j + 1].Beat - notes[j].Beat >= 3;
        const int Most = 6;
        var cost = new double[s + 1, m + 1]; var back = new int[s + 1, m + 1];
        for (int i = 0; i <= s; i++) for (int j = 0; j <= m; j++) cost[i, j] = double.PositiveInfinity;
        cost[0, 0] = 0;
        for (int j = 1; j <= m; j++) { cost[0, j] = cost[0, j - 1] + (Breath(j - 1) ? .5 : 1.5); back[0, j] = -1; }
        for (int i = 1; i <= s; i++)
        {
            var f = syllables[i - 1]; bool lineEnd = i == s || syllables[i].Line != f.Line;
            bool stressed = f.Source.Stress.Length > f.Index && f.Source.Stress[f.Index] > 0;
            for (int j = 1; j <= m; j++)
            {
                // Skip an unsung note.
                double best = cost[i, j - 1] + 2.5; int from = -1;
                for (int k = 1; k <= Most && k <= j; k++)
                {
                    double before = cost[i - 1, j - k]; if (double.IsInfinity(before)) continue;
                    double c = before + (k - 1) * (f.Hold ? .1 : stressed ? .7 : 1.3);
                    for (int x = j - k; x < j - 1; x++) if (Breath(x)) c += 2;
                    if (lineEnd && !Breath(j - 1)) c += 1.2;
                    if (!lineEnd && Breath(j - 1)) c += 1.6;
                    if (c < best) { best = c; from = k; }
                }
                cost[i, j] = best; back[i, j] = from;
            }
        }
        for (int i = s, j = m; i > 0 && j > 0;)
        {
            int k = back[i, j];
            if (k <= 0) { j--; continue; }
            var f = syllables[i - 1]; f.Start = notes[j - k].Beat; f.End = notes[j - 1].Beat + notes[j - 1].Length;
            i--; j -= k;
        }
        return syllables.Any(f => !double.IsNaN(f.Start));
    }

    // Spoken stanzas with no timing: one line after another in the song's untaken bars, each
    // stressed syllable on a beat and the unstressed ones on the upbeats between.
    static bool Place(PreparedPatternSong song, MidiCycleAnalysis cycles, List<Flat> flat)
    {
        var taken = flat.Where(f => !double.IsNaN(f.Start)).Select(f => cycles.Measures.FindLastIndex(b => b.Start <= f.Start + 1e-6)).ToHashSet();
        int bar = 0; bool placed = false;
        foreach (var line in flat.Where(f => f.Spoken && double.IsNaN(f.Start)).GroupBy(f => f.Line))
        {
            while (bar < cycles.Measures.Count && taken.Contains(bar)) bar++;
            if (bar >= cycles.Measures.Count) break;
            double at = cycles.Measures[bar].Start;
            foreach (var f in line)
            {
                bool stressed = f.Source.Stress.Length > f.Index && f.Source.Stress[f.Index] > 0;
                if (stressed && at % 1 > 1e-6) at = Math.Ceiling(at);
                f.Start = at; f.End = at + .5; at += .5;
            }
            bar = cycles.Measures.FindLastIndex(b => b.Start <= at) + 1; placed = true;
        }
        return placed;
    }

    // Vibrato: a pitch bend oscillating around the note (at least one and a half cycles), or
    // the spans the audio pitch track found.
    static void Vibrato(List<Flat> flat, MidiFile midi, (int track, int channel) vocal, MidiCycleAnalysis cycles, int range, Timing timing)
    {
        var bends = new List<(double beat, double semitones)>();
        if (vocal.track >= 0)
            for (int t = 0; t < midi.Tracks; t++)
                foreach (var e in midi.Events[t].OfType<PitchWheelChangeEvent>())
                    if (e.Channel == vocal.channel && (t == vocal.track || midi.Events[t].OfType<NoteOnEvent>().All(n => n.Channel != vocal.channel)))
                        bends.Add((e.AbsoluteTime / (double)midi.DeltaTicksPerQuarterNote, (e.Pitch - 8192) / 8192.0 * range));
        bends.Sort((a, b) => a.beat.CompareTo(b.beat));
        foreach (var f in flat.Where(f => !f.Spoken))
        {
            var span = bends.Where(b => b.beat >= f.Start - 1e-6 && b.beat < f.End).ToList();
            if (span.Count >= 6)
            {
                double mean = span.Average(b => b.semitones); int crossings = 0; double first = double.NaN;
                for (int i = 1; i < span.Count; i++) if (Math.Sign(span[i].semitones - mean) != Math.Sign(span[i - 1].semitones - mean) && Math.Sign(span[i].semitones - mean) != 0) { crossings++; if (double.IsNaN(first)) first = span[i - 1].beat; }
                double depth = (span.Max(b => b.semitones) - span.Min(b => b.semitones)) / 2;
                if (crossings >= 3 && depth >= .1)
                {
                    double seconds = cycles.SecondsAt(span[^1].beat) - cycles.SecondsAt(first);
                    f.Vibrato = (float)depth; f.VibratoRate = (float)(crossings / 2.0 / Math.Max(.05, seconds)); f.VibratoStart = first;
                }
            }
            if (f.Vibrato <= 0 && timing?.vibrato != null)
                foreach (var v in timing.vibrato)
                {
                    double a = cycles.BeatAt(v.start), b = cycles.BeatAt(v.end);
                    if (b > f.Start && a < f.End && b - a >= .25) { f.Vibrato = v.depth; f.VibratoRate = v.rate; f.VibratoStart = Math.Max(a, f.Start); break; }
                }
        }
    }

    static readonly string[] Letters26 = Enumerable.Range(0, 26).Select(i => ((char)('A' + i)).ToString()).ToArray();

    static void Read(PreparedPatternSong song, MidiCycleAnalysis cycles, PreparedPatternSong.LyricSheet sheet, List<SheetStanza> stanzas, List<Flat> flat)
    {
        flat.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Start.CompareTo(b.Start));
        var syllables = new List<PreparedPatternSong.Syllable>();
        double maxHold = Math.Max(1, flat.Where(f => !f.Spoken).Select(f => f.End - f.Start).DefaultIfEmpty(1).Max());
        foreach (var f in flat)
        {
            int b = Math.Max(0, cycles.Measures.FindLastIndex(m => m.Start <= f.Start + 1e-6)); var bar = cycles.Measures[b];
            double unit = ChordTimeline.BeatUnit(bar), position = f.Start - bar.Start;
            bool On(double grid) => Math.Abs(position / grid - Math.Round(position / grid)) < .04;
            int metric = position < .04 ? 2 : On(unit) ? 1 : On(unit / 2) ? 0 : -1;
            int stress = f.Source.Stress.Length > f.Index ? f.Source.Stress[f.Index] : 0;
            double lexical = stress == 1 ? 1 : stress == 2 ? .6 : f.Source.Weak ? .25 : .1;
            double beat = metric switch { 2 => 1, 1 => .75, 0 => .35, _ => .15 };
            double hold = Math.Min(1, (f.End - f.Start) / Math.Min(2, maxHold));
            float emphasis = (float)Math.Clamp(.4 * lexical + .25 * beat + .2 * hold + .15 * f.Velocity, 0, 1);
            syllables.Add(new PreparedPatternSong.Syllable
            {
                Text = f.Text, Word = f.Source.Text, Line = f.Line, WordIndex = f.Word, Index = f.Index, Pitch = f.Pitch, Stress = stress, Metric = metric,
                Teeth = Math.Max(1, (int)Math.Round((f.End - f.Start) / .5)), Start = f.Start, End = f.End, Emphasis = emphasis, Position = (float)position,
                Spoken = f.Spoken, WordStart = f.Index == 0, WordEnd = f.Index == f.Source.Syllables.Length - 1, Vibrato = f.Vibrato, VibratoRate = f.VibratoRate, VibratoStart = f.VibratoStart,
            });
        }
        // Sung emphasis also rises at a melodic peak within the line.
        foreach (var line in syllables.Where(x => x.Pitch >= 0).GroupBy(x => x.Line))
        {
            int top = line.Max(x => x.Pitch);
            foreach (var x in line) if (x.Pitch == top) x.Emphasis = Math.Min(1, x.Emphasis + .1f);
        }
        sheet.Syllables = syllables.ToArray();
        // Lines.
        var lines = new List<PreparedPatternSong.LyricLine>(); int index = 0;
        for (int s = 0; s < stanzas.Count; s++)
            foreach (var sheetLine in stanzas[s].Lines)
            {
                int first = syllables.FindIndex(x => x.Line == index), count = syllables.Count(x => x.Line == index);
                var own = syllables.Where(x => x.Line == index).ToList();
                var line = new PreparedPatternSong.LyricLine { Stanza = s, First = Math.Max(0, first), Count = count, Text = sheetLine.Text, Spoken = stanzas[s].Spoken };
                if (count > 0)
                {
                    line.Start = own[0].Start; line.End = own[^1].End;
                    line.Stresses = string.Concat(own.Select(x => x.Stress > 0 ? '/' : 'x'));
                    // Delivered accents: the strongest metrical level that at most about half the
                    // line's syllables reach (beats for a line in eighths, beats 1 and 3 for a
                    // line in quarters).
                    var weights = own.Select(x => Weight(cycles, x.Start)).ToList();
                    int level = Enumerable.Range(-1, 5).Where(v => weights.Count(w => w >= v) <= Math.Ceiling(count * .6)).DefaultIfEmpty(3).First();
                    line.Beats = string.Concat(weights.Select(w => w >= level ? '/' : 'x'));
                }
                lines.Add(line); index++;
            }
        sheet.Lines = lines.ToArray();
        // Meter: each line scanned, then again leaning toward its stanza's most common foot.
        string Pattern(int li) => string.Concat(sheet.Syllables.Where(x => x.Line == li).Select(x => { var w = FindWord(stanzas, lines[li], x); return w != null && w.Weak ? '?' : x.Stress > 0 ? '/' : 'x'; }));
        var scans = lines.Select((l, i) => LyricEnglish.Scan(Pattern(i))).ToList();
        for (int s = 0; s < stanzas.Count; s++)
        {
            var own = lines.Select((l, i) => (l, i)).Where(x => x.l.Stanza == s).ToList();
            string foot = own.Select(x => scans[x.i].foot).Where(f => f.Length > 0).GroupBy(f => f).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            foreach (var (l, i) in own) l.Meter = LyricEnglish.Scan(Pattern(i), foot).meter;
        }
        // End rhyme within each stanza: perfect keys first, then the last syllables.
        int groups = 0;
        for (int s = 0; s < stanzas.Count; s++)
        {
            var own = lines.Select((l, i) => (l, i)).Where(x => x.l.Stanza == s && x.l.Count > 0).ToList();
            var keys = own.Select(x => EndKey(stanzas, sheet, x.l, x.i)).ToList();
            var letterOf = new Dictionary<int, string>(); int next = 0;
            for (int a = 0; a < own.Count; a++)
            {
                own[a].l.EndRhyme = keys[a].perfect;
                int partner = -1;
                for (int b = 0; b < a && partner < 0; b++) if (Rhymes(keys[a], keys[b])) partner = b;
                if (partner >= 0) { own[a].l.RhymeGroup = own[partner].l.RhymeGroup; own[a].l.Letter = own[partner].l.Letter; continue; }
                own[a].l.Letter = next < 26 ? Letters26[next] : "Z"; next++;
                bool paired = Enumerable.Range(a + 1, own.Count - a - 1).Any(b => Rhymes(keys[a], keys[b]));
                own[a].l.RhymeGroup = paired ? groups++ : -1;
            }
        }
        // Front rhyme within each stanza, strongest kind first over the whole stanza: repeated
        // opening words (anaphora), then a head rhyme, then alliteration.
        int fronts = 0;
        for (int s = 0; s < stanzas.Count; s++)
        {
            var own = lines.Select((l, i) => (l, i)).Where(x => x.l.Stanza == s && x.l.Count > 0).ToList();
            var heads = own.Select(x => Head(sheet, x.i)).ToList();
            foreach (var (test, name) in new (Func<Opening, string>, string)[] { (h => h.Words2, "repeat"), (h => h.Word1, "repeat"), (h => h.Rhyme, "head rhyme"), (h => h.Onset, "alliteration") })
                for (int a = 0; a < own.Count; a++)
                {
                    string key = test(heads[a]); if (own[a].l.FrontGroup >= 0 || string.IsNullOrEmpty(key)) continue;
                    var mates = Enumerable.Range(a + 1, own.Count - a - 1).Where(b => own[b].l.FrontGroup < 0 && test(heads[b]) == key).ToList();
                    if (mates.Count == 0) continue;
                    string label = name == "repeat" ? key : name == "head rhyme" ? "-" + key : key + "-";
                    foreach (int b in mates.Prepend(a)) { own[b].l.FrontGroup = fronts; own[b].l.FrontKind = name; own[b].l.FrontRhyme = label; }
                    fronts++;
                }
        }
        // Stanzas: scheme, meter, the song section they sit in.
        sheet.Stanzas = stanzas.Select((st, s) =>
        {
            var own = lines.Where(l => l.Stanza == s).ToList();
            // The section holding most of the stanza (a pickup may start in the one before).
            int section = sheet.Syllables.Where(x => own.Any(l => l.Count > 0 && x.Line == lines.IndexOf(l))).Select(x => Array.FindLastIndex(song.Sections, q => q.Start <= x.Start + 1e-6))
                .GroupBy(k => k).OrderByDescending(g => g.Count()).Select(g => g.Key).DefaultIfEmpty(-1).First();
            string meter = own.Select(l => l.Meter).Where(m => m.Length > 0).GroupBy(m => Regex.Replace(m, @" \(.*\)$", "")).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "";
            return new PreparedPatternSong.Stanza { Name = st.Name, Spoken = st.Spoken, FirstLine = lines.IndexOf(own[0]), Lines = own.Count, Section = section, Meter = meter, Scheme = string.Concat(own.Select(l => l.Letter.Length > 0 ? l.Letter : "·")) };
        }).ToArray();
        sheet.Matches = Matchups(song, sheet).ToArray();
    }

    // Metrical weight: 3 on the bar's downbeat, 2 mid-bar, 1 on a beat, 0 on an upbeat, -1 between.
    static int Weight(MidiCycleAnalysis cycles, double beat)
    {
        var bar = cycles.Measures[Math.Max(0, cycles.Measures.FindLastIndex(m => m.Start <= beat + 1e-6))];
        double unit = ChordTimeline.BeatUnit(bar), position = beat - bar.Start, length = bar.End - bar.Start;
        bool On(double grid) => grid > 0 && Math.Abs(position / grid - Math.Round(position / grid)) < .04;
        int beats = (int)Math.Round(length / unit);
        return position < .04 ? 3 : beats % 2 == 0 && beats > 2 && On(length / 2) ? 2 : On(unit) ? 1 : On(unit / 2) ? 0 : -1;
    }

    static SheetWord FindWord(List<SheetStanza> stanzas, PreparedPatternSong.LyricLine line, PreparedPatternSong.Syllable x)
    {
        var sheetLine = stanzas[line.Stanza].Lines.FirstOrDefault(l => l.Text == line.Text);
        return sheetLine != null && x.WordIndex < sheetLine.Words.Count ? sheetLine.Words[x.WordIndex] : null;
    }
    static (string perfect, string weak, string word) EndKey(List<SheetStanza> stanzas, PreparedPatternSong.LyricSheet sheet, PreparedPatternSong.LyricLine line, int index)
    {
        var own = sheet.Syllables.Where(x => x.Line == index).ToList(); if (own.Count == 0) return ("", "", "");
        var last = own[^1]; var word = own.Where(x => x.WordIndex == last.WordIndex).ToList();
        var (perfect, weak) = LyricEnglish.RhymeKey(last.Word, word.Select(x => x.Text.ToLowerInvariant()).ToArray(), word.Select(x => x.Stress).ToArray());
        return (perfect, weak, LyricEnglish.Clean(last.Word));
    }
    // Perfect rhymes share their stressed tail; the same word repeated counts; a weak rhyme
    // shares only its last syllable (temperate / date).
    static bool Rhymes((string perfect, string weak, string word) a, (string perfect, string weak, string word) b) =>
        a.perfect.Length > 0 && (a.perfect == b.perfect || a.word == b.word || a.weak.Length > 1 && a.weak == b.weak);

    sealed class Opening { public string Words2 = "", Word1 = "", Rhyme = "", Onset = ""; }
    static Opening Head(PreparedPatternSong.LyricSheet sheet, int index)
    {
        var own = sheet.Syllables.Where(x => x.Line == index).ToList(); var head = new Opening(); if (own.Count == 0) return head;
        var words = own.GroupBy(x => x.WordIndex).Select(g => g.ToList()).ToList();
        head.Word1 = words[0][0].Word.Length > 0 ? char.ToUpperInvariant(words[0][0].Word[0]) + words[0][0].Word[1..].ToLowerInvariant() : "";
        if (words.Count > 1) head.Words2 = head.Word1 + " " + words[1][0].Word.ToLowerInvariant();
        var stressed = own.FirstOrDefault(x => x.Stress > 0) ?? own[0];
        var word = own.Where(x => x.WordIndex == stressed.WordIndex).ToList();
        head.Rhyme = LyricEnglish.RhymeKey(stressed.Word, word.Select(x => x.Text.ToLowerInvariant()).ToArray(), word.Select(x => x.Stress).ToArray()).perfect;
        head.Onset = LyricEnglish.Onset(stressed.Text);
        return head;
    }

    // The same lines of stanzas sung to one pattern (the same section family), and of
    // consecutive spoken stanzas: stresses against beats, syllable counts and meters.
    static IEnumerable<PreparedPatternSong.MeterMatch> Matchups(PreparedPatternSong song, PreparedPatternSong.LyricSheet sheet)
    {
        int Family(PreparedPatternSong.Stanza s) => s.Section >= 0 ? song.Sections[s.Section].Family : -1;
        for (int a = 0; a < sheet.Stanzas.Length; a++)
        {
            var sa = sheet.Stanzas[a];
            int b = Enumerable.Range(a + 1, sheet.Stanzas.Length - a - 1).FirstOrDefault(k => sheet.Stanzas[k].Spoken == sa.Spoken && (sa.Spoken || Family(sheet.Stanzas[k]) == Family(sa) && Family(sa) >= 0), -1);
            if (b < 0) continue;
            var sb = sheet.Stanzas[b];
            for (int i = 0; i < Math.Min(sa.Lines, sb.Lines); i++)
            {
                var la = sheet.Lines[sa.FirstLine + i]; var lb = sheet.Lines[sb.FirstLine + i];
                if (la.Count == 0 || lb.Count == 0) continue;
                double beats = Align(la.Beats, lb.Beats), stresses = Align(la.Stresses, lb.Stresses);
                var notes = new List<string>();
                int diff = lb.Count - la.Count;
                if (diff != 0) notes.Add($"{(diff > 0 ? "+" : "−")}{Math.Abs(diff)} syllable{(Math.Abs(diff) > 1 ? "s" : "")}");
                string ma = Regex.Replace(la.Meter, @" \(.*\)$", ""), mb = Regex.Replace(lb.Meter, @" \(.*\)$", "");
                if (ma != mb && ma.Length > 0 && mb.Length > 0) notes.Add($"{ma} → {mb}");
                int moved = la.Stresses.Length == lb.Stresses.Length ? la.Stresses.Zip(lb.Stresses).Count(p => p.First != p.Second) : 0;
                string stressNote = moved == 0 ? "" : $", {moved} stress{(moved > 1 ? "es" : "")} move{(moved > 1 ? "" : "s")}";
                if (notes.Count == 0) notes.Add(beats >= .999 && stresses >= .999 ? "same meter, same beats" : beats >= .999 ? "same beats" + stressNote : "same meter, beats shift");
                yield return new PreparedPatternSong.MeterMatch { A = sa.FirstLine + i, B = sb.FirstLine + i, Score = (float)(.6 * beats + .4 * stresses), Note = string.Join(" · ", notes) };
            }
        }
    }
    // Global alignment score of two stress strings, 0..1.
    static double Align(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = -i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = -j;
        for (int i = 1; i <= a.Length; i++) for (int j = 1; j <= b.Length; j++) d[i, j] = Math.Max(Math.Max(d[i - 1, j] - 1, d[i, j - 1] - 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 1 : -1));
        return Math.Clamp(d[a.Length, b.Length] / (double)Math.Max(a.Length, b.Length), 0, 1);
    }
}
