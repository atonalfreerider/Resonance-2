using System.Text.RegularExpressions;

// Streams (Docs/MOTIF-REDUCTION.md §3 A4), in priority order: the score's voices with their principal lines; for
// lanes without voices, each lane's outer line and the rest under it (a sustain-aware skyline); one texture per
// staff or lane; both hands of a piano as one texture, each note tagged by hand; drums by General MIDI group. Then
// the principal stream, the one nearest the melody, which drives arrivals and the default glyph. Grace notes never
// enter a stream.
public sealed class MotifStreams
{
    // One stream as a sequence of events, one per onset: Onset[k] in ticks (ascending); Lead[k] the note that carries the
    // stream there (the principal note of a voice, the outer note of a lane, the lowest note of a texture); and
    // Notes[First[k] .. First[k] + Count[k]) every note struck at that onset, Lead first, then the others away from it.
    public sealed class Stream
    {
        public int Id, Track = -1, Channel = -1, Staff = -1, Voice = -1, Hand = -1, Drum = -1;
        public string Name = "", Kind = "";
        public bool Bass;
        public long[] Onset = Array.Empty<long>();
        public int[] Lead = Array.Empty<int>(), First = Array.Empty<int>(), Count = Array.Empty<int>(), Notes = Array.Empty<int>();
        public int Events => Onset.Length;
    }
    public const string Voice = "voice", Line = "line", Rest = "rest", Texture = "texture", Both = "both", Drums = "drums";
    public static readonly string[] DrumGroups = { "kick", "snare", "hats", "cymbals", "toms", "other" };
    public static int DrumGroup(int pitch) => pitch switch
    {
        35 or 36 => 0, >= 37 and <= 40 => 1, 42 or 44 or 46 => 2, 49 or 51 or 52 or 53 or 55 or 57 or 59 => 3, 41 or 43 or 45 or 47 or 48 or 50 => 4, _ => 5
    };
    static readonly Regex BassName = new(@"\bbass\b|left[\s-]*hand|\blh\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex LeftName = new(@"left[\s-]*hand|\blh\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex RightName = new(@"right[\s-]*hand|\brh\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public Stream[] All = Array.Empty<Stream>();
    public int Principal = -1;
    public Stream this[int id] => All[id];

    public static MotifStreams Build(MotifInput input, PreparedPatternSong data, MidiCycleAnalysis cycles)
    {
        var ev = input.Events; var settings = input.Settings; var import = input.Import;
        string[] names = data.TrackNames ?? Array.Empty<string>();
        string TrackName(int track) => track >= 0 && track < names.Length ? names[track] ?? "" : "";
        int[] piano = data.PianoTracks ?? Array.Empty<int>();
        int Median(List<int> notes) { var p = notes.Select(i => ev[i].Pitch).OrderBy(x => x).ToList(); return p.Count == 0 ? 60 : p[(p.Count - 1) / 2]; }

        // Lanes in (track, channel) order; a bass lane is named bass or left hand, is the second piano track, or lies low.
        var laneNotes = new SortedDictionary<(int track, int channel), List<int>>();
        for (int i = 0; i < ev.Length; i++)
        {
            if (ev[i].Drum || ev[i].Grace) continue;
            if (!laneNotes.TryGetValue((ev[i].Track, ev[i].Channel), out var list)) laneNotes[(ev[i].Track, ev[i].Channel)] = list = new List<int>();
            list.Add(i);
        }
        var lanes = laneNotes.Keys.ToList();
        var laneBass = new Dictionary<(int, int), bool>(); var laneName = new Dictionary<(int, int), string>(); var laneMedian = new Dictionary<(int, int), int>();
        foreach (var lane in lanes)
        {
            string track = TrackName(lane.track);
            laneMedian[lane] = Median(laneNotes[lane]);
            laneBass[lane] = BassName.IsMatch(track) || piano.Length >= 2 && piano[1] == lane.track || laneMedian[lane] < settings.BassMedianPitch;
            laneName[lane] = (track.Length > 0 ? track : $"Track {lane.track + 1}") + (lanes.Count(l => l.track == lane.track) > 1 ? $" ch {lane.channel}" : "");
        }
        // Staves of the import: the lowest staff of a keyboard part is the lower hand; a single staff is low when it lies low.
        var staffNotes = new SortedDictionary<int, List<int>>();
        for (int i = 0; i < ev.Length; i++) if (!ev[i].Drum && !ev[i].Grace && ev[i].Staff >= 0) { if (!staffNotes.TryGetValue(ev[i].Staff, out var list)) staffNotes[ev[i].Staff] = list = new List<int>(); list.Add(i); }
        var staffLow = new Dictionary<int, bool>();   // lookups only
        foreach (var (staff, list) in staffNotes)
        {
            var info = import?.StaffOf(staff);
            staffLow[staff] = info != null && info.Keyboard ? info.Lowest : info != null && BassName.IsMatch(info.Name) || Median(list) < settings.BassMedianPitch;
        }
        bool StaffBass(int staff) => staffLow.TryGetValue(staff, out bool low) && low;
        string StaffName(int staff) { var info = import?.StaffOf(staff); return info != null && info.Name.Length > 0 ? info.Name : $"Staff {staff + 1}"; }
        for (int i = 0; i < ev.Length; i++)
            ev[i].Hand = ev[i].Drum ? -1 : ev[i].Staff >= 0 ? (StaffBass(ev[i].Staff) ? 1 : 0) : laneBass.TryGetValue((ev[i].Track, ev[i].Channel), out bool low) && low ? 1 : 0;

        var streams = new List<Stream>();
        void Add(Stream s, string kind, string name, int track, int channel, bool bass, int hand)
        {
            if (s.Events == 0) return;
            s.Id = streams.Count; s.Kind = kind; s.Name = name; s.Bass = bass; s.Hand = hand;
            if (track < 0) (track, channel) = MostCommonLane(ev, s.Notes);
            s.Track = track; s.Channel = channel;
            streams.Add(s);
        }

        // 1. Score voices holding enough notes, each with its principal line; a staff's smaller voices are its rest.
        foreach (var (staff, notes) in staffNotes)
        {
            bool bass = StaffBass(staff);
            var voices = new SortedDictionary<int, List<int>>();
            foreach (int i in notes) { if (!voices.TryGetValue(ev[i].Voice, out var list)) voices[ev[i].Voice] = list = new List<int>(); list.Add(i); }
            var others = new List<int>();
            foreach (var (voice, list) in voices)
            {
                if (list.Count < settings.VoiceMinNotes) { others.AddRange(list); continue; }
                var s = Make(ev, list, !bass, settings.LineRankCost); s.Staff = staff; s.Voice = voice;
                Add(s, Voice, $"{StaffName(staff)} · voice {voice + 1}", -1, -1, bass, bass ? 1 : 0);
            }
            if (others.Count > 0) { var s = Make(ev, others, !bass); s.Staff = staff; Add(s, Rest, $"{StaffName(staff)} · other voices", -1, -1, bass, bass ? 1 : 0); }
        }
        // 2. Lanes without voices (no import, or notes the import did not match): the outer line and the rest.
        foreach (var lane in lanes)
        {
            var voiceless = laneNotes[lane].Where(i => ev[i].Staff < 0).ToList();
            if (voiceless.Count == 0) continue;
            bool bass = laneBass[lane];
            var (line, rest) = Skyline(ev, voiceless, bass, input.Grid);
            Add(Make(ev, line, !bass), Line, laneName[lane] + " · line", lane.track, lane.channel, bass, bass ? 1 : 0);
            Add(Make(ev, rest, !bass), Rest, laneName[lane] + " · rest", lane.track, lane.channel, bass, bass ? 1 : 0);
        }
        // 3. Textures: one sonority per onset, per staff with an import, per lane without.
        foreach (var (staff, notes) in staffNotes) { var s = Make(ev, notes, false); s.Staff = staff; Add(s, Texture, StaffName(staff) + " · texture", -1, -1, StaffBass(staff), StaffBass(staff) ? 1 : 0); }
        foreach (var lane in lanes)
        {
            var voiceless = laneNotes[lane].Where(i => ev[i].Staff < 0).ToList();
            if (voiceless.Count > 0) Add(Make(ev, voiceless, false), Texture, laneName[lane] + " · texture", lane.track, lane.channel, laneBass[lane], laneBass[lane] ? 1 : 0);
        }
        // 4. Both hands of a piano: a keyboard part's staves, else the two piano tracks, else tracks named right and left hand.
        var keyboardParts = import == null ? new List<int>() : import.Staves.Where(s => s.Keyboard).Select(s => s.Part).Distinct().OrderBy(p => p).ToList();
        foreach (int part in keyboardParts)
        {
            var staves = import.Staves.Where(s => s.Part == part).Select(s => s.Index).ToHashSet();
            var notes = staffNotes.Where(p => staves.Contains(p.Key)).SelectMany(p => p.Value).ToList();
            if (notes.Count > 0) Add(Make(ev, notes, false), Both, "Both hands", -1, -1, false, -1);
        }
        if (keyboardParts.Count == 0)
        {
            int right = -1, left = -1;
            if (piano.Length >= 2) (right, left) = (piano[0], piano[1]);
            else
            {
                var tracks = lanes.Select(l => l.track).Distinct().ToList();
                right = tracks.FirstOrDefault(t => RightName.IsMatch(TrackName(t)), -1); left = tracks.FirstOrDefault(t => t != right && LeftName.IsMatch(TrackName(t)), -1);
            }
            if (right >= 0 && left >= 0 && right != left)
            {
                var notes = lanes.Where(l => l.track == right || l.track == left).SelectMany(l => laneNotes[l]).ToList();
                foreach (int i in notes) if (ev[i].Staff < 0) ev[i].Hand = ev[i].Track == left ? 1 : 0;
                Add(Make(ev, notes, false), Both, "Both hands", -1, -1, false, -1);
            }
        }
        // 5. Drums, one stream per General MIDI group: only identity and time shifts act on them.
        var drums = new List<int>[DrumGroups.Length];
        for (int i = 0; i < ev.Length; i++) if (ev[i].Drum) (drums[DrumGroup(ev[i].Pitch)] ??= new List<int>()).Add(i);
        for (int g = 0; g < drums.Length; g++)
            if (drums[g] != null) { var s = Make(ev, drums[g], false); s.Drum = g; Add(s, Drums, "Drums · " + DrumGroups[g], -1, -1, false, -1); }

        var result = new MotifStreams { All = streams.ToArray() };
        result.Principal = ChoosePrincipal(input, result.All, data, cycles, laneMedian, piano, keyboardParts);
        return result;
    }

    // Notes into events, each onset's notes ranked from the outer edge (top-down when `top`, else bottom-up). The lead is
    // rank 0, or, for a score voice (rankCost ≥ 0), the voice-leading Viterbi's choice: cost |pitch − previous lead| +
    // rankCost·rank (§3 A4), which takes bar 2's G♯4 of the étude, not E4.
    static Stream Make(MotifEvent[] ev, List<int> notes, bool top, double rankCost = -1)
    {
        var sorted = notes.ToArray();
        Array.Sort(sorted, (x, y) => ev[x].Tick != ev[y].Tick ? ev[x].Tick.CompareTo(ev[y].Tick) : ev[x].Pitch != ev[y].Pitch ? (top ? ev[y].Pitch.CompareTo(ev[x].Pitch) : ev[x].Pitch.CompareTo(ev[y].Pitch)) : x.CompareTo(y));
        var onset = new List<long>(); var first = new List<int>(); var count = new List<int>();
        for (int i = 0; i < sorted.Length;)
        {
            int j = i; while (j < sorted.Length && ev[sorted[j]].Tick == ev[sorted[i]].Tick) j++;
            onset.Add(ev[sorted[i]].Tick); first.Add(i); count.Add(j - i); i = j;
        }
        int events = onset.Count; var rank = new int[events];
        if (rankCost >= 0 && events > 0)
        {
            var cost = new double[sorted.Length]; var back = new int[sorted.Length];
            for (int k = 0; k < events; k++)
                for (int r = 0; r < count[k]; r++)
                {
                    int i = first[k] + r; double best = 0; int from = -1;
                    if (k > 0)
                    {
                        best = double.MaxValue;
                        for (int q = 0; q < count[k - 1]; q++)
                        {
                            int p = first[k - 1] + q; double v = cost[p] + Math.Abs(ev[sorted[i]].Pitch - ev[sorted[p]].Pitch);
                            if (v < best - 1e-9) { best = v; from = q; }
                        }
                    }
                    cost[i] = best + rankCost * r; back[i] = from;
                }
            int last = 0;
            for (int r = 1; r < count[events - 1]; r++) if (cost[first[events - 1] + r] < cost[first[events - 1] + last] - 1e-9) last = r;
            for (int k = events - 1; k >= 0; k--) { rank[k] = last; if (k > 0) last = back[first[k] + last]; }
        }
        var lead = new int[events]; var members = new int[sorted.Length];
        for (int k = 0; k < events; k++)
        {
            int f = first[k], c = count[k], r = rank[k];
            lead[k] = members[f] = sorted[f + r];
            for (int q = 0, w = f + 1; q < c; q++) if (q != r) members[w++] = sorted[f + q];
        }
        return new Stream { Onset = onset.ToArray(), Lead = lead, First = first.ToArray(), Count = count.ToArray(), Notes = members };
    }

    // The sustain-aware skyline (§3 A4): a note is on the outer line when it is the outermost of its onset and no earlier
    // note of the lane still sounds further out. "Still sounds" means for more than a slot past the onset, so a legato
    // release a few ticks late (as a transcription has) does not hide the next note of the line.
    static (List<int> line, List<int> rest) Skyline(MotifEvent[] ev, List<int> notes, bool bass, MotifGrid grid)
    {
        var sorted = notes.ToArray();
        Array.Sort(sorted, (x, y) => ev[x].Tick != ev[y].Tick ? ev[x].Tick.CompareTo(ev[y].Tick) : ev[x].Pitch != ev[y].Pitch ? (bass ? ev[x].Pitch.CompareTo(ev[y].Pitch) : ev[y].Pitch.CompareTo(ev[x].Pitch)) : x.CompareTo(y));
        var line = new List<int>(); var rest = new List<int>(); var sounding = new List<int>();
        for (int i = 0; i < sorted.Length;)
        {
            long tick = ev[sorted[i]].Tick; int j = i; while (j < sorted.Length && ev[sorted[j]].Tick == tick) j++;
            long limit = tick + grid.SlotAt(tick);
            sounding.RemoveAll(k => ev[k].Tick + ev[k].Duration <= tick);
            int outer = sorted[i]; bool hidden = false;
            foreach (int k in sounding) if (ev[k].Tick + ev[k].Duration > limit && (bass ? ev[k].Pitch < ev[outer].Pitch : ev[k].Pitch > ev[outer].Pitch)) { hidden = true; break; }
            (hidden ? rest : line).Add(outer);
            for (int k = i + 1; k < j; k++) rest.Add(sorted[k]);
            for (int k = i; k < j; k++) sounding.Add(sorted[k]);
            i = j;
        }
        return (line, rest);
    }

    static (int track, int channel) MostCommonLane(MotifEvent[] ev, int[] notes)
    {
        var counts = new Dictionary<(int, int), int>(); (int, int) best = (-1, -1); int most = 0;
        foreach (int i in notes)
        {
            var key = (ev[i].Track, ev[i].Channel); counts[key] = counts.GetValueOrDefault(key) + 1;
            if (counts[key] > most || counts[key] == most && key.CompareTo(best) < 0) { most = counts[key]; best = key; }
        }
        return best;
    }

    // The principal stream (§3 A4): the voice or line whose leads coincide most often with the melody's notes (onset within
    // half a slot, same pitch; melody times are seconds, mapped through the tempo map). Without a melody: the outer line
    // of the lead vocal track; for a piano, its upper staff's voice 1 (its right-hand line without an import); otherwise
    // the line of the lane with the highest median pitch.
    static int ChoosePrincipal(MotifInput input, Stream[] streams, PreparedPatternSong data, MidiCycleAnalysis cycles, Dictionary<(int, int), int> laneMedian, int[] piano, List<int> keyboardParts)
    {
        var ev = input.Events; var grid = input.Grid;
        var lines = streams.Where(s => s.Kind is Voice or Line).ToList();
        if (lines.Count == 0) return -1;
        var melody = (data.Melody ?? Array.Empty<PreparedPatternSong.MelodyStrand>()).Where(s => s?.Notes != null).SelectMany(s => s.Notes)
            .Select(n => (tick: MotifInput.Ticks(cycles.BeatAt(n.Start), input.Ppq), pitch: n.Pitch)).OrderBy(m => m.tick).ThenBy(m => m.pitch).ToArray();
        if (melody.Length > 0)
        {
            var ticks = melody.Select(m => m.tick).ToArray();
            int best = -1, bestCount = 0;
            foreach (var s in lines)
            {
                int hits = 0;
                for (int k = 0; k < s.Events; k++)
                {
                    long tolerance = grid.SlotAt(s.Onset[k]) / 2; int pitch = ev[s.Lead[k]].Pitch;
                    int i = Array.BinarySearch(ticks, s.Onset[k] - tolerance); if (i < 0) i = ~i; else while (i > 0 && ticks[i - 1] == ticks[i]) i--;
                    for (; i < ticks.Length && ticks[i] <= s.Onset[k] + tolerance; i++) if (melody[i].pitch == pitch) { hits++; break; }
                }
                if (hits > bestCount) { bestCount = hits; best = s.Id; }
            }
            if (best >= 0) return best;
        }
        if (data.LeadVocalTrack >= 0)
        {
            var vocal = lines.FirstOrDefault(s => s.Kind == Line && s.Track == data.LeadVocalTrack) ?? lines.FirstOrDefault(s => s.Kind == Voice && s.Track == data.LeadVocalTrack && s.Voice == 0);
            if (vocal != null) return vocal.Id;
        }
        if (keyboardParts.Count > 0)
        {
            int upper = input.Import.Staves.Where(s => s.Part == keyboardParts[0]).Min(s => s.Index);
            var voice = lines.FirstOrDefault(s => s.Kind == Voice && s.Staff == upper && s.Voice == 0) ?? lines.FirstOrDefault(s => s.Kind == Voice && s.Staff == upper);
            if (voice != null) return voice.Id;
        }
        if (piano.Length >= 1)
        {
            var right = lines.FirstOrDefault(s => s.Kind == Line && s.Track == piano[0]);
            if (right != null) return right.Id;
        }
        var top = lines.Where(s => s.Kind == Line).OrderByDescending(s => laneMedian.GetValueOrDefault((s.Track, s.Channel), 0)).ThenBy(s => s.Id).FirstOrDefault();
        return (top ?? lines[0]).Id;
    }
}
