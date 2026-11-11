// The lossless gate of the motif reduction (Docs/MOTIF-REDUCTION.md §9 G4). Every note is decoded back from the description
// alone: each coding stream's cells in time order (a prototype from its literal, a member as its prototype's image under g
// with the residual edits applied, a literal cell from its literal), the further notes struck with each lead, the grace
// notes from their hosts, and the articulation code (exact onsets, durations, velocities). The result must equal
// cycles.Notes as SectionCompression.Verify compares them: sorted by (track, channel, beat, pitch, length, velocity); beat
// and length within 1e-8, pitch equal, velocity within 1e-6. A mismatch throws InvalidDataException.
//
// The decoder never reads input.Events: it sees the code (MotifCode), the grid, the key and chord frames, and the PPQ. The
// encoder runs the same decoder cell by cell, so a literal's first step is coded from what the decoder will hold, and a
// member's struck notes are paired with what its image decodes to.
public static class MotifVerify
{
    // F22's test hook: changes one decoded residual of the code before Check decodes it.
    public static Action<MotifCode> Mutate;

    public struct Note
    {
        public int Track, Channel, Pitch;
        public double Beat, Length;
        public float Velocity;
        public override string ToString() => FormattableString.Invariant($"(track {Track}, channel {Channel}, beat {Beat:0.########}, length {Length:0.########}, pitch {Pitch}, velocity {Velocity:0.######})");
    }

    // Strict use (--strict-motifs, the self-tests, the sweep) lets the exception through; the default path disables the layer.
    public static void Check(MotifCode code, IReadOnlyList<MidiCycleAnalysis.Hit> notes)
    {
        Mutate?.Invoke(code);
        Compare(Decode(code), notes);
    }

    public static List<Note> Decode(MotifCode code)
    {
        var output = new List<Note>(code.Notes);
        foreach (var s in code.Streams)
        {
            var d = new Decoder(code, s, output);
            foreach (var c in s.Cells) d.Cell(c);
        }
        foreach (var o in code.Orphans) output.Add(Decoder.Make(code.Ppq, o.Tick, o.Pitch, o.A, o.A.Track, o.A.Channel));
        return output;
    }

    static void Compare(List<Note> decoded, IReadOnlyList<MidiCycleAnalysis.Hit> notes)
    {
        var expected = new Note[notes.Count];
        for (int i = 0; i < expected.Length; i++) { var h = notes[i]; expected[i] = new Note { Track = h.Track, Channel = h.Channel, Beat = h.Beat, Length = h.Length, Pitch = h.Pitch, Velocity = h.Velocity }; }
        var actual = decoded.ToArray();
        // Sorted at the comparison's precision, so a few ulps never part a chord's notes (SectionCompression.Verify's order).
        static int Order(Note a, Note b)
        {
            int x = a.Track.CompareTo(b.Track); if (x != 0) return x;
            x = a.Channel.CompareTo(b.Channel); if (x != 0) return x;
            x = Math.Round(a.Beat, 8).CompareTo(Math.Round(b.Beat, 8)); if (x != 0) return x;
            x = a.Pitch.CompareTo(b.Pitch); if (x != 0) return x;
            x = Math.Round(a.Length, 8).CompareTo(Math.Round(b.Length, 8)); if (x != 0) return x;
            return a.Velocity.CompareTo(b.Velocity);
        }
        Array.Sort(expected, Order); Array.Sort(actual, Order);
        if (expected.Length != actual.Length)
        {
            string first = "";
            for (int i = 0; i < Math.Min(expected.Length, actual.Length) && first.Length == 0; i++) if (!Same(expected[i], actual[i])) first = $"; first difference at note {i}: expected {expected[i]}, decoded {actual[i]}";
            throw new InvalidDataException(FormattableString.Invariant($"{actual.Length} notes decoded, {expected.Length} in the score{first}"));
        }
        for (int i = 0; i < actual.Length; i++)
            if (!Same(expected[i], actual[i])) throw new InvalidDataException(FormattableString.Invariant($"note {i} decodes to {actual[i]}, the score has {expected[i]}"));
    }
    static bool Same(Note e, Note a) => e.Track == a.Track && e.Channel == a.Channel && e.Pitch == a.Pitch && Math.Abs(e.Beat - a.Beat) <= 1e-8 && Math.Abs(e.Length - a.Length) <= 1e-8 && Math.Abs(e.Velocity - a.Velocity) <= 1e-6;

    // One element of a decoded member: its onset in ticks from the cell start, the slot that is in, its pitch and morph.
    internal struct Element
    {
        public long Onset;
        public int Slot, Pitch, Morph, Index;
    }
    internal static int Slots(long ticks, long slot) => (int)Math.Round(ticks / (double)slot, MidpointRounding.AwayFromZero);

    // One stream's decoder. Its state is the stream's last decoded lead (the tie of the next window, and the note a
    // literal's first step starts from); each prototype is kept as TransformGroup.Apply reads it.
    internal sealed class Decoder
    {
        readonly MotifCode.Stream stream; readonly List<Note> output;
        readonly KeyFrames frames; readonly int ppq;
        public bool Started;
        public int Morph, Pitch, Alter;
        public long Tick, End;
        sealed class Prototype
        {
            public CellNote[] Notes = Array.Empty<CellNote>();   // leads, the tied-in note first
            public bool Tied;
            public long[] Hits = Array.Empty<long>();             // drums: each note's onset from the cell start …
            public int[] HitPitch = Array.Empty<int>();           // … and pitch, by (onset, pitch)
        }
        readonly Dictionary<int, Prototype> prototypes = new();   // by paradigm, looked up only

        public Decoder(MotifCode code, MotifCode.Stream stream, List<Note> output)
        {
            this.stream = stream; this.output = output;
            frames = code.Frames; ppq = code.Ppq;
        }

        // A tie as §5 C1's windows have it: the last lead still sounds more than half a slot past the cell start.
        bool TiedInto(MotifCode.Cell c) => Started && End * 2 > 2 * c.Start + c.Slot;

        // A member's struck elements: its prototype's image under g (the head's lengthening aside, which only moves the tie),
        // then the residual edits; the tied-in note is no note. In the order the encoder pairs them: slot, pitch, onset.
        public List<Element> Elements(MotifCode.Cell c) => Elements(c, out _);
        public List<Element> Elements(MotifCode.Cell c, out CellNote[] image)
        {
            image = Array.Empty<CellNote>();
            if (!prototypes.TryGetValue(c.Paradigm, out var p)) throw new InvalidDataException(FormattableString.Invariant($"stream {stream.Id}: a member at beat {c.Start / (double)ppq:0.###} precedes its prototype"));
            var list = new List<Element>();
            if (c.Drum)
            {
                for (int i = 0; i < p.Hits.Length; i++) list.Add(new Element { Onset = p.Hits[i], Slot = Slots(p.Hits[i], c.Slot), Pitch = p.HitPitch[i], Index = i });
            }
            else
            {
                var g = new Transform(c.G.Steps, c.G.Semitones, c.G.Frame, c.G.Inverted, c.G.Retrograde, c.G.Scale, c.G.Parallel);
                image = TransformGroup.Apply(g, p.Notes, c.Start, frames);
                int n = image.Length;
                var gone = new bool[n]; var onset = new long[n]; var pitch = new int[n]; var morph = new int[n];
                for (int i = 0; i < n; i++) { onset[i] = image[i].Onset; pitch[i] = image[i].Pitch; morph[i] = image[i].Morph; }
                bool lengthened = false;
                foreach (var x in c.Sparks)
                {
                    bool inside = x.Note >= 0 && x.Note < n;
                    switch (x.Kind)
                    {
                        case PreparedPatternSong.MotifSparks.Deleted: if (inside) gone[x.Note] = true; break;
                        case PreparedPatternSong.MotifSparks.Altered or PreparedPatternSong.MotifSparks.Substituted: if (inside) { pitch[x.Note] = x.Pitch; morph[x.Note] = x.Morph; } break;
                        case PreparedPatternSong.MotifSparks.Shifted: if (inside) onset[x.Note] += x.Shift; break;
                        case PreparedPatternSong.MotifSparks.Inserted: if (!x.Tied) list.Add(new Element { Onset = x.Tick - c.Start, Pitch = x.Pitch, Morph = x.Morph, Index = n + list.Count }); break;
                        case PreparedPatternSong.MotifSparks.Lengthened: lengthened = true; break;
                    }
                }
                bool tiedHead = p.Tied ^ lengthened;
                for (int i = 0; i < n; i++) if (!gone[i] && !(i == 0 && tiedHead)) list.Add(new Element { Onset = onset[i], Pitch = pitch[i], Morph = morph[i], Index = i });
                for (int i = 0; i < list.Count; i++) { var e = list[i]; e.Slot = Slots(e.Onset, c.Slot); list[i] = e; }
            }
            list.Sort((a, b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Pitch != b.Pitch ? a.Pitch.CompareTo(b.Pitch) : a.Onset != b.Onset ? a.Onset.CompareTo(b.Onset) : a.Index.CompareTo(b.Index));
            return list;
        }

        public void Cell(MotifCode.Cell c)
        {
            bool tied = !c.Drum && TiedInto(c);
            var tiedNote = tied ? new CellNote { Onset = 0, Length = End - c.Start, Morph = Morph, Pitch = Pitch, Alter = Alter, Interval = Interval(Pitch, c.Start) } : default;
            var leads = new List<CellNote>(); var hits = new List<(long onset, int pitch)>();
            if (c.Paradigm >= 0 && !c.Prototype)
            {
                var elements = Elements(c);
                if (elements.Count != c.Events.Length) throw new InvalidDataException(FormattableString.Invariant($"stream {stream.Id}: the member at beat {c.Start / (double)ppq:0.###} decodes to {elements.Count} notes, its code has {c.Events.Length}"));
                for (int k = 0; k < elements.Count; k++)
                {
                    var el = elements[k]; var e = c.Events[k];
                    long tick = c.Start + el.Onset + e.Lead.Timing;
                    Emit(tick, el.Pitch, el.Morph, e, c.Drum, null);
                }
                return;
            }
            // A literal: each struck event's slot, its lead's morph step from the lead before and alteration (a drum onset: its
            // pitches); a prototype is then kept for its members.
            int morph = Started ? Morph : 0;
            for (int k = 0; k < c.Events.Length; k++)
            {
                var e = c.Events[k];
                long tick = c.Start + c.Slots[k] * c.Slot + e.Lead.Timing;
                if (c.Drum)
                {
                    var pitches = c.Pitches[k];
                    Emit(tick, pitches[0], 0, e, true, pitches);
                    hits.Add((tick - c.Start, pitches[0]));
                    for (int j = 1; j < pitches.Length; j++) hits.Add((tick + e.Chord[j - 1].Timing - c.Start, pitches[j]));
                    continue;
                }
                morph += c.Steps[k];
                int pitch = frames.ScalePitch(morph, frames.Region(tick)) + c.Alters[k];
                Emit(tick, pitch, morph, e, false, null);
                if (c.Prototype) leads.Add(new CellNote { Onset = tick - c.Start, Length = e.Lead.Duration, Morph = morph, Pitch = pitch, Alter = frames.Alter(pitch, morph, frames.Region(tick)), Interval = Interval(pitch, tick) });
            }
            if (!c.Prototype) return;
            var p = new Prototype { Tied = tied };
            if (c.Drum)
            {
                hits.Sort((a, b) => a.onset != b.onset ? a.onset.CompareTo(b.onset) : a.pitch.CompareTo(b.pitch));
                p.Hits = hits.Select(h => h.onset).ToArray(); p.HitPitch = hits.Select(h => h.pitch).ToArray();
            }
            else
            {
                if (tied) leads.Insert(0, tiedNote);
                p.Notes = leads.ToArray();
            }
            prototypes[c.Paradigm] = p;
        }
        int Interval(int pitch, long tick) { int root = frames.RootPc(frames.Chord(tick)); return root < 0 ? -1 : Spelling.Mod(pitch - root, 12); }

        // One struck event: its lead, the notes struck with it (morph steps and alterations from the lead, or a drum onset's
        // pitches) and their graces. The state follows the latest lead.
        void Emit(long tick, int pitch, int morph, MotifCode.Event e, bool drum, int[] drumPitches)
        {
            Add(tick, pitch, e.Lead, e.Graces?[0]);
            for (int j = 0; j < e.Chord.Length; j++)
            {
                var a = e.Chord[j]; long at = tick + a.Timing;
                int p = drum ? drumPitches[j + 1] : frames.ScalePitch(morph + e.Steps[j], frames.Region(at)) + e.Alters[j];
                Add(at, p, a, e.Graces?[j + 1]);
            }
            if (!Started || tick >= Tick)
            {
                Started = true; Tick = tick; End = tick + e.Lead.Duration; Pitch = pitch; Morph = morph;
                Alter = drum ? 0 : frames.Alter(pitch, morph, frames.Region(tick));
            }
        }
        void Add(long tick, int pitch, in MotifCode.Articulation a, MotifCode.Grace[] graces)
        {
            output.Add(Make(ppq, tick, pitch, a, a.Track >= 0 ? a.Track : stream.Track, a.Channel >= 0 ? a.Channel : stream.Channel));
            if (graces == null) return;
            foreach (var g in graces) output.Add(Make(ppq, tick - g.Before, pitch + g.Interval, g.A, g.A.Track >= 0 ? g.A.Track : stream.Track, g.A.Channel >= 0 ? g.A.Channel : stream.Channel));
        }

        // A note from its analysed tick and articulation: the performed onset is the analysed one plus the transcription's
        // offset, in ticks, unless the note lies off the tick grid (then its exact beat and length are in the code).
        public static Note Make(int ppq, long tick, int pitch, in MotifCode.Articulation a, int track, int channel) => new()
        {
            Track = track, Channel = channel, Pitch = pitch,
            Beat = double.IsNaN(a.Beat) ? (tick + a.Offset) / (double)ppq : a.Beat,
            Length = double.IsNaN(a.Length) ? a.Duration / (double)ppq : a.Length,
            Velocity = a.Velocity >= 0 ? a.Velocity / 127f : a.Exact
        };
    }
}
