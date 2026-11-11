using System.Text.Json;
using Kinds = PreparedPatternSong.MotifKinds;
using Relations = PreparedPatternSong.MotifRelations;
using Sparks = PreparedPatternSong.MotifSparks;
using Edits = PreparedPatternSong.MotifEdits;

// Stage G of the motif reduction (Docs/MOTIF-REDUCTION.md §9): the description as a code, priced in bits, decoded back to
// every note (MotifVerify) and exported to the bundle.
// - The code: each coding stream (score voices, lane lines and rests, drum groups; textures and both hands are views of the
//   same notes) as its cells in time order. A prototype and a literal cell carry their literal; a member its paradigm,
//   element and residual edits. Every struck event carries the notes struck with its lead, their graces and the
//   articulation code (exact onset, duration, velocity), which the model never prices.
// - G1 bits: L_total = L_streams + Σ Lit(prototype) + Σ member [log2(paradigms + 1) + L(g) + L(ρ)] + Σ literal [Lit + 1]
//   + Σ powers [L_pow − references replaced] + Σ returns [L_return − references replaced]; articulation apart.
// - G2 baselines (literal, entropy, cells), G3 derived coverage and the caption, G5 export with its caps.
public sealed class MotifCode
{
    // One note's articulation (§9 G4): its analysed onset against the model's (Timing), the transcription's snap (Offset),
    // its duration in ticks and its velocity as 0–127 (−1: Exact holds the float, as a fixture's may be). Beat and Length
    // are NaN unless the note lies off the tick grid; Track and Channel are −1 for the stream's own lane.
    public struct Articulation
    {
        public long Timing, Offset, Duration;
        public int Velocity, Track, Channel;
        public float Exact;
        public double Beat, Length;
    }
    // A grace note, coded from its host: Before ticks ahead of it, Interval semitones from it.
    public struct Grace
    {
        public long Before;
        public int Interval;
        public Articulation A;
    }
    // One struck event of a cell: its lead's articulation, then each further note struck with it (its morph step from the
    // lead and its alteration; a drum onset's pitches are the cell's), and the graces of each note, the lead's first.
    public sealed class Event
    {
        public Articulation Lead;
        public int[] Steps = Array.Empty<int>(), Alters = Array.Empty<int>();
        public Articulation[] Chord = Array.Empty<Articulation>();
        public Grace[][] Graces;
    }
    // A residual edit as the decoder applies it (CellLexicon.Spark): Note is the prototype element it changes; an inserted
    // note sounds at Tick, Tied when it is the note held into the cell.
    public sealed class Spark
    {
        public int Kind, Note, Pitch, Morph;
        public long Tick, Shift;
        public bool Tied;
    }
    public sealed class Cell
    {
        public int Lexicon = -1, Paradigm = -1, Space;
        public bool Prototype, Drum;
        public long Start, Stop, Slot;
        public Transform G = Transform.Identity;
        public Spark[] Sparks = Array.Empty<Spark>();
        public int[] Slots = Array.Empty<int>(), Steps = Array.Empty<int>(), Alters = Array.Empty<int>();   // a literal's leads
        public int[][] Pitches = Array.Empty<int[]>();                                                       // a literal drum onset's notes
        public Event[] Events = Array.Empty<Event>();                                                        // struck events, as decoded
        // G1: the literal (prototypes and literal cells), the paradigm reference, the element, the residual (edits, the
        // further notes of a member, graces); Replaced: a power or a return restates this member, so its reference is theirs.
        public double Lit, Reference, Element, Residual;
        public bool Replaced;
        public double Bits => (Paradigm < 0 ? Lit + 1 : Prototype ? Lit + Reference : Replaced ? 0 : Reference + Element) + Residual;
    }
    public sealed class Stream
    {
        public int Id, Track, Channel;
        public string Kind = "", Name = "";
        public Cell[] Cells = Array.Empty<Cell>();
    }
    // A grace note without a host: coded on its own, its lane in its articulation.
    public sealed class Orphan
    {
        public long Tick;
        public int Pitch;
        public Articulation A;
    }

    public int Ppq, Notes;
    public MotifGrid Grid;
    public KeyFrames Frames;
    public Stream[] Streams = Array.Empty<Stream>();
    public Orphan[] Orphans = Array.Empty<Orphan>();
    // §9 G1–G3 results.
    public double BitsLiteral, BitsEntropy, BitsCells, BitsModel, BitsResidual, BitsArticulation, BitsStreams;
    public int DerivedNotes;
    public double[] ReturnBits = Array.Empty<double>(), ReturnSaved = Array.Empty<double>();       // per Returns.All
    public double[] RelationBits = Array.Empty<double>(), RelationSaved = Array.Empty<double>();   // per Iteration.Relations
    public double[][] EditBits = Array.Empty<double[]>();                                          // per Returns.All, per script edit
    Cell[] byLexicon = Array.Empty<Cell>();                                                        // the coded cell of each lexicon cell (null off the coding streams)
    readonly List<(int stream, double bits)> articulation = new();

    public static bool Coding(MotifStreams.Stream s) => s.Kind is MotifStreams.Voice or MotifStreams.Line or MotifStreams.Rest or MotifStreams.Drums;
    static int Gamma(long v) => CellLexicon.Gamma(v);

    // ---------------------------------------------------------------------------------------------------------------
    // The code (§9 G4) and the bits of its cells (§9 G1).

    public static MotifCode Encode(MotifInput input, CellLexicon lex, IReadOnlyList<MidiCycleAnalysis.Hit> original)
    {
        var ev = input.Events;
        var code = new MotifCode { Ppq = input.Ppq, Notes = original.Count, Grid = input.Grid, Frames = input.Frames, byLexicon = new Cell[lex.Cells.Length] };
        var e = new Encoder(code, input, lex, original);
        var streams = new List<Stream>();
        foreach (var s in input.Streams.All) if (Coding(s)) streams.Add(e.Stream(s));
        // A grace whose host is no coded note is coded on its own.
        var orphans = new List<Orphan>();
        for (int i = 0; i < ev.Length; i++)
            if (ev[i].Grace && !ev[i].Drum && !e.Attached[i]) orphans.Add(new Orphan { Tick = ev[i].Tick, Pitch = ev[i].Pitch, A = e.Art(i, 0, -1, -1) });
        code.Streams = streams.ToArray(); code.Orphans = orphans.ToArray();
        code.BitsStreams = e.StreamBits + orphans.Count * (2 + 7 + Math.Log2(Math.Max(2, input.Grid.Columns - input.Grid.FirstColumn)));
        code.articulation.AddRange(e.Articulation);
        return code;
    }

    sealed class Encoder
    {
        readonly MotifCode code; readonly MotifInput input; readonly CellLexicon lex; readonly IReadOnlyList<MidiCycleAnalysis.Hit> original;
        readonly MotifEvent[] ev; readonly MotifGrid grid; readonly KeyFrames frames; readonly int ppq;
        readonly List<int>[] graces;
        public readonly bool[] Attached;
        public double StreamBits;
        public readonly List<(int stream, double bits)> Articulation = new();
        readonly List<MotifVerify.Note> scratch = new();
        // This stream's articulation symbols (order-0 entropy, §9 G1): timing against the model, the snap, duration against
        // its default (the prototype note's, the next onset's, the lead's) and velocity; escapes cost their bits outright.
        readonly List<long> timing = new(), offset = new(), duration = new(); readonly List<int> velocity = new(); double escapes;

        public Encoder(MotifCode code, MotifInput input, CellLexicon lex, IReadOnlyList<MidiCycleAnalysis.Hit> original)
        {
            this.code = code; this.input = input; this.lex = lex; this.original = original;
            ev = input.Events; grid = input.Grid; frames = input.Frames; ppq = input.Ppq;
            graces = new List<int>[ev.Length]; Attached = new bool[ev.Length];
            // Graces by host, in the events' (tick, pitch) order.
            for (int i = 0; i < ev.Length; i++)
            {
                int h = ev[i].Host;
                if (ev[i].Grace && !ev[i].Drum && h >= 0 && h < ev.Length && !ev[h].Grace && !ev[h].Drum) (graces[h] ??= new List<int>()).Add(i);
            }
        }

        public Stream Stream(MotifStreams.Stream s)
        {
            var cs = new Stream { Id = s.Id, Track = s.Track, Channel = s.Channel, Kind = s.Kind, Name = s.Name };
            scratch.Clear();   // the encoder's decoder only keeps state here; Verify decodes the finished code again
            var dec = new MotifVerify.Decoder(code, cs, scratch);
            var cells = new List<Cell>(); var used = new bool[s.Events];
            timing.Clear(); offset.Clear(); duration.Clear(); velocity.Clear(); escapes = 0;
            int paradigms = 0; foreach (var p in lex.Paradigms) if (p.Stream == s.Id) paradigms++;
            double reference = Math.Log2(paradigms + 1);
            long last = -1;
            foreach (var lc in lex.Of(s.Id))
            {
                var x = lc.W;
                var c = new Cell { Lexicon = lc.Id, Paradigm = lc.Paradigm, Space = x.Space, Drum = x.Space == CellLexicon.Drum, Start = x.Start, Stop = x.Stop, Slot = Math.Max(1, grid.SlotAt(x.Start)) };
                c.Prototype = lc.Paradigm >= 0 && lex.Paradigms[lc.Paradigm].Prototype == lc.Id;
                var events = new int[x.Count]; for (int q = 0; q < x.Count; q++) events[q] = x.First + q;
                if (lc.Paradigm >= 0 && !c.Prototype) Member(c, lc, s, events, dec, reference);
                else { Literal(c, s, events, dec); if (c.Prototype) c.Reference = reference; }
                foreach (int k in events) used[k] = true;
                if (last >= 0 && c.Start > last || last < 0 && c.Start > grid.SpanStart) StreamBits += 1;   // a rest before the cell
                last = c.Stop;
                dec.Cell(c);
                cells.Add(c); code.byLexicon[lc.Id] = c;
            }
            // Events no cell holds (past the grid's last column): one literal of their own.
            var rest = new List<int>(); for (int k = 0; k < s.Events; k++) if (!used[k]) rest.Add(k);
            if (rest.Count > 0)
            {
                long start = s.Onset[rest[0]];
                var c = new Cell { Space = s.Kind == MotifStreams.Drums ? CellLexicon.Drum : CellLexicon.Diatonic, Drum = s.Kind == MotifStreams.Drums, Start = start, Stop = s.Onset[rest[^1]] + 1, Slot = Math.Max(1, grid.SlotAt(start)) };
                Literal(c, s, rest.ToArray(), dec);
                dec.Cell(c); cells.Add(c);
            }
            cs.Cells = cells.ToArray();
            StreamBits += 8;   // the stream: its kind and lane
            Articulation.Add((s.Id, Entropy(timing) + Entropy(offset) + Entropy(duration) + Entropy(velocity) + escapes));
            return cs;
        }

        // A literal (and a prototype): each struck event's slot, the lead's morph step from the lead before (the decoder's) and
        // its alteration, the further notes struck with it; priced by §9 G1's event literal.
        void Literal(Cell c, MotifStreams.Stream s, int[] events, MotifVerify.Decoder dec)
        {
            int n = events.Length;
            c.Slots = new int[n]; c.Events = new Event[n];
            if (c.Drum) c.Pitches = new int[n][]; else { c.Steps = new int[n]; c.Alters = new int[n]; }
            int morph = dec.Started ? dec.Morph : 0, pitch = dec.Started ? dec.Pitch : 0;
            double bits = 4, position = Math.Log2(Math.Max(2, (c.Stop - c.Start) / (double)c.Slot));
            for (int q = 0; q < n; q++)
            {
                int k = events[q]; var lead = ev[s.Lead[k]];
                c.Slots[q] = MotifVerify.Slots(s.Onset[k] - c.Start, c.Slot);
                long timing = s.Onset[k] - (c.Start + c.Slots[q] * c.Slot);
                long next = k + 1 < s.Events ? s.Onset[k + 1] - s.Onset[k] : lead.Duration;
                bits += position;
                if (c.Drum)
                {
                    var pitches = new int[s.Count[k]]; for (int j = 0; j < pitches.Length; j++) pitches[j] = ev[s.Notes[s.First[k] + j]].Pitch;
                    c.Pitches[q] = pitches; bits += 2 * pitches.Length;
                    c.Events[q] = Event(s, k, timing, 0, true, next, c);
                    continue;
                }
                c.Steps[q] = lead.Morph - morph;
                c.Alters[q] = lead.Pitch - frames.ScalePitch(lead.Morph, frames.Region(lead.Tick));
                bits += Gamma(c.Space == CellLexicon.Chromatic ? lead.Pitch - pitch : c.Steps[q]) + (c.Alters[q] != 0 ? 3 : 0);
                morph = lead.Morph; pitch = lead.Pitch;
                c.Events[q] = Event(s, k, timing, lead.Morph, false, next, c);
            }
            c.Lit += bits;   // the further notes' bits are already in (Event)
        }

        // A member: its element and residual edits from the lexicon. Its struck events are paired with what the decoder
        // makes of its prototype (by slot and pitch), and each event's articulation is coded against its image's onset.
        void Member(Cell c, CellLexicon.Cell lc, MotifStreams.Stream s, int[] events, MotifVerify.Decoder dec, double reference)
        {
            var x = lc.W;
            c.G = lc.G; c.Reference = reference; c.Element = lc.G.Cost();
            c.Sparks = lc.Sparks.Select(p => new Spark { Kind = p.Kind, Note = p.Note, Pitch = p.Pitch, Morph = p.Morph, Tick = p.Tick, Shift = p.Shift, Tied = p.Kind == Sparks.Inserted && x.Tie >= 0 && p.Event == x.Tie }).ToArray();
            var elements = dec.Elements(c, out var image);
            string where = FormattableString.Invariant($"stream {s.Id} ({s.Name}): the member at beat {c.Start / (double)ppq:0.###}");
            if (c.Drum)
            {
                var hits = new List<(int slot, int pitch, long tick, int note)>();
                foreach (int k in events) for (int j = 0; j < s.Count[k]; j++) { int note = s.Notes[s.First[k] + j]; hits.Add((MotifVerify.Slots(ev[note].Tick - c.Start, c.Slot), ev[note].Pitch, ev[note].Tick, note)); }
                hits.Sort((a, b) => a.slot != b.slot ? a.slot.CompareTo(b.slot) : a.pitch != b.pitch ? a.pitch.CompareTo(b.pitch) : a.tick != b.tick ? a.tick.CompareTo(b.tick) : a.note.CompareTo(b.note));
                if (hits.Count != elements.Count) throw new InvalidDataException(FormattableString.Invariant($"{where} has {hits.Count} drum notes, its prototype {elements.Count}"));
                c.Events = new Event[hits.Count];
                for (int i = 0; i < hits.Count; i++)
                {
                    if (hits[i].slot != elements[i].Slot || hits[i].pitch != elements[i].Pitch) throw new InvalidDataException(FormattableString.Invariant($"{where} does not decode: hit {i} is {hits[i].pitch} in slot {hits[i].slot}, its prototype's {elements[i].Pitch} in slot {elements[i].Slot}"));
                    c.Events[i] = Hit(s, hits[i].note, hits[i].tick - (c.Start + elements[i].Onset));
                }
                return;
            }
            var actual = new List<(int slot, int pitch, long tick, int k)>();
            foreach (int k in events) actual.Add((MotifVerify.Slots(s.Onset[k] - c.Start, c.Slot), ev[s.Lead[k]].Pitch, s.Onset[k], k));
            actual.Sort((a, b) => a.slot != b.slot ? a.slot.CompareTo(b.slot) : a.pitch != b.pitch ? a.pitch.CompareTo(b.pitch) : a.tick.CompareTo(b.tick));
            if (actual.Count != elements.Count) throw new InvalidDataException(FormattableString.Invariant($"{where} decodes to {elements.Count} notes, the stream has {actual.Count} there"));
            c.Events = new Event[actual.Count];
            for (int i = 0; i < actual.Count; i++)
            {
                var el = elements[i];
                if (actual[i].slot != el.Slot || actual[i].pitch != el.Pitch) throw new InvalidDataException(FormattableString.Invariant($"{where} does not decode: note {i} is {actual[i].pitch} in slot {actual[i].slot}, decoded {el.Pitch} in slot {el.Slot}"));
                long proto = el.Index < image.Length ? image[el.Index].Length : 0;
                c.Events[i] = Event(s, actual[i].k, actual[i].tick - (c.Start + el.Onset), el.Morph, false, proto, c);
            }
            // L(ρ) of §9 G1: an inserted note 2 + log2(positions) + its event literal; a deleted one 2 + log2(positions); a
            // substitution 1 + log2(1 + |Δ steps|); a shift 1; an altered note 1.5 when the key names it, else 3.
            double position = Math.Log2(Math.Max(2, (c.Stop - c.Start) / (double)c.Slot)), rho = 0; int altered = 0;
            bool diatonic = c.Space == CellLexicon.Diatonic;
            foreach (var p in c.Sparks)
            {
                switch (p.Kind)
                {
                    case Sparks.Inserted:
                        {
                            int value = diatonic ? p.Morph : p.Pitch, before = value;
                            long best = long.MinValue;
                            foreach (var el in elements) if (el.Onset < p.Tick - c.Start && el.Onset > best) { best = el.Onset; before = diatonic ? el.Morph : el.Pitch; }
                            rho += 2 + Math.Log2(elements.Count + 1) + position + Gamma(value - before) + (frames.Alter(p.Pitch, p.Morph, frames.Region(p.Tick)) != 0 ? 3 : 0);
                            break;
                        }
                    case Sparks.Deleted: rho += 2 + Math.Log2(image.Length + 1); break;
                    case Sparks.Substituted:
                        {
                            int d = p.Note >= 0 && p.Note < image.Length ? (diatonic ? p.Morph - image[p.Note].Morph : p.Pitch - image[p.Note].Pitch) : 1;
                            rho += 1 + Math.Log2(1 + Math.Abs(d));
                            break;
                        }
                    case Sparks.Shifted: rho += 1; break;
                    case Sparks.Altered: rho += altered < lc.Altered.Length ? lc.Altered[altered++].Bits : 3; break;
                }
            }
            c.Residual += rho;
        }

        // One struck event: the lead's articulation, then the notes struck with it (a member pays them as residual: §9 G1's
        // γ(m − m_lead), 3 more when altered) and the graces of each note (2 + γ(interval) each).
        Event Event(MotifStreams.Stream s, int k, long timing, int leadMorph, bool drum, long expected, Cell c)
        {
            int first = s.First[k], count = s.Count[k], lead = s.Notes[first];
            var e = new Event { Lead = Art(lead, timing, s.Track, s.Channel), Chord = new MotifCode.Articulation[count - 1] };
            Symbols(e.Lead, ev[lead].Duration - expected);
            if (!drum) { e.Steps = new int[count - 1]; e.Alters = new int[count - 1]; }
            for (int j = 1; j < count; j++)
            {
                int note = s.Notes[first + j];
                e.Chord[j - 1] = Art(note, ev[note].Tick - ev[lead].Tick, s.Track, s.Channel);
                Symbols(e.Chord[j - 1], ev[note].Duration - ev[lead].Duration);
                if (drum) continue;
                e.Steps[j - 1] = ev[note].Morph - leadMorph;
                e.Alters[j - 1] = ev[note].Pitch - frames.ScalePitch(ev[note].Morph, frames.Region(ev[note].Tick));
                if (c.Paradigm >= 0 && !c.Prototype) c.Residual += Gamma(e.Steps[j - 1]) + (e.Alters[j - 1] != 0 ? 3 : 0);
                else c.Lit += Gamma(e.Steps[j - 1]) + (e.Alters[j - 1] != 0 ? 3 : 0);
            }
            for (int j = 0; j < count; j++)
            {
                int note = s.Notes[first + j];
                if (graces[note] == null) continue;
                e.Graces ??= new Grace[count][];
                var list = new Grace[graces[note].Count];
                for (int q = 0; q < list.Length; q++)
                {
                    int g = graces[note][q];
                    list[q] = new Grace { Before = ev[note].Tick - ev[g].Tick, Interval = ev[g].Pitch - ev[note].Pitch, A = Art(g, 0, s.Track, s.Channel) };
                    Symbols(list[q].A, ev[g].Duration);
                    c.Residual += 2 + Gamma(list[q].Interval);
                    Attached[g] = true;
                }
                e.Graces[j] = list;
            }
            return e;
        }
        Event Hit(MotifStreams.Stream s, int note, long timing)
        {
            var e = new Event { Lead = Art(note, timing, s.Track, s.Channel) };
            Symbols(e.Lead, ev[note].Duration);
            return e;
        }

        public MotifCode.Articulation Art(int i, long timing, int track, int channel)
        {
            var e = ev[i]; var h = original[e.NoteIndex];
            var a = new MotifCode.Articulation { Timing = timing, Offset = e.Offset, Duration = e.Duration, Track = e.Track == track ? -1 : e.Track, Channel = e.Channel == channel ? -1 : e.Channel, Beat = double.NaN, Length = double.NaN };
            int v = (int)Math.Round(h.Velocity * 127.0);
            if (v >= 0 && v <= 127 && v / 127f == h.Velocity) a.Velocity = v; else { a.Velocity = -1; a.Exact = h.Velocity; }
            if (Math.Abs((e.Tick + e.Offset) / (double)ppq - h.Beat) > 1e-9) a.Beat = h.Beat;
            if (Math.Abs(e.Duration / (double)ppq - h.Length) > 1e-9) a.Length = h.Length;
            return a;
        }
        void Symbols(in MotifCode.Articulation a, long durationDelta)
        {
            timing.Add(a.Timing); offset.Add(a.Offset); duration.Add(durationDelta); velocity.Add(a.Velocity);
            if (a.Velocity < 0) escapes += 32;
            if (!double.IsNaN(a.Beat)) escapes += 64;
            if (!double.IsNaN(a.Length)) escapes += 64;
            if (a.Track >= 0) escapes += Gamma(a.Track);
            if (a.Channel >= 0) escapes += Gamma(a.Channel);
        }
    }

    // n·H over a list of symbols (order-0 entropy, in bits).
    static double Entropy<T>(List<T> symbols) where T : IComparable<T>
    {
        int n = symbols.Count; if (n == 0) return 0;
        var sorted = symbols.ToArray(); Array.Sort(sorted);
        double bits = 0;
        for (int i = 0; i < n;)
        {
            int j = i; while (j < n && sorted[j].CompareTo(sorted[i]) == 0) j++;
            double p = (j - i) / (double)n; bits -= (j - i) * Math.Log2(p); i = j;
        }
        return bits;
    }
    // The same with a codebook of 8 bits per distinct symbol (§9 G2's entropy baseline).
    static double Coded(List<long> symbols)
    {
        var distinct = symbols.Distinct().Count();
        return Entropy(symbols) + 8 * distinct;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §9 G1 for the relations, G2 baselines and G3 derived coverage.

    public void Price(MotifInput input, CellLexicon lex, Returns ret, Iteration it, Hierarchy tree)
    {
        var grid = input.Grid; var ev = input.Events;
        double columns = Math.Log2(Math.Max(2, grid.Columns - grid.FirstColumn));
        int Columns(long a, long b) { if (b <= a) return 0; int x = grid.ColumnAt(a), y = grid.ColumnAt(b - 1); return Math.Max(1, y - Math.Max(0, x) + 1); }
        // Cells by start per coding stream, for a counterpart lookup.
        var starts = new Dictionary<int, (long[] start, Cell[] cell)>();   // looked up only
        foreach (var s in Streams) starts[s.Id] = (s.Cells.Select(c => c.Start).ToArray(), s.Cells);
        Cell At(int stream, long start)
        {
            if (!starts.TryGetValue(stream, out var x)) return null;
            int i = Array.BinarySearch(x.start, start); return i >= 0 ? x.cell[i] : null;
        }
        // A stream's cells lying in [from, to), found by their starts (each stream's cells are in time order).
        IEnumerable<Cell> Within(Stream s, long from, long to)
        {
            var x = starts[s.Id].start; int i = Array.BinarySearch(x, from); if (i < 0) i = ~i;
            for (; i < x.Length && x[i] < to; i++) if (s.Cells[i].Stop <= to) yield return s.Cells[i];
        }
        static bool Member(Cell c) => c != null && c.Paradigm >= 0 && !c.Prototype && !c.Replaced;
        // c restates `source` under g, as their elements from one prototype tell: a real g by its semitones, a tonal one by its
        // steps, the identity by both.
        static bool Restates(Cell source, Cell c, in Transform g)
        {
            if (source == null || source.Paradigm != c.Paradigm || source.Stop - source.Start != c.Stop - c.Start) return false;
            if ((c.G.Inverted != source.G.Inverted) != g.Inverted || c.G.Parallel - source.G.Parallel != g.Parallel) return false;
            int steps = c.G.Steps - source.G.Steps, semitones = c.G.Semitones - source.G.Semitones;
            if (g.Steps == 0 && g.Semitones == 0) return steps == 0 && semitones == 0;
            return g.Frame == Transform.Real ? semitones == g.Semitones : steps == g.Steps;
        }

        // Returns (level 1, then level 2): log2(#columns) + L(g) + per edit its run (γ of the matched length) or its gap
        // (log2(#columns) + γ of its length); each member a match restates under the return's transposition is replaced.
        int nr = ret.All.Length;
        ReturnBits = new double[nr]; ReturnSaved = new double[nr]; EditBits = new double[nr][];
        for (int i = 0; i < nr; i++)
        {
            var r = ret.All[i];
            var shift = r.K == 0 ? Transform.Identity : new Transform(r.K % 12 == 0 ? r.K / 12 * 7 : 0, r.K, Transform.Real);
            double bits = columns + shift.Cost();
            EditBits[i] = new double[r.Script.Length];
            for (int j = 0; j < r.Script.Length; j++)
            {
                var e = r.Script[j];
                long a = e.Op == Edits.Delete ? e.SourceStart : e.TargetStart, b = e.Op == Edits.Delete ? e.SourceEnd : e.TargetEnd;
                double x = (e.Op == Edits.Match ? 0 : columns) + Gamma(Columns(a, b));
                EditBits[i][j] = x; bits += x;
                if (e.Op != Edits.Match) continue;
                foreach (var s in Streams)
                    foreach (var c in Within(s, e.TargetStart, e.TargetEnd))
                        if (Member(c) && Restates(At(s.Id, e.SourceStart + (c.Start - e.TargetStart)), c, shift)) { c.Replaced = true; ReturnSaved[i] += c.Reference + c.Element; }
            }
            ReturnBits[i] = bits;
        }
        // Powers: L_pow (its bits from stage D); the members of each later iteration that restate the one before under the
        // step are replaced (a cell power's later cells; a unit power's members one unit after a member of their paradigm).
        int nl = it.Relations.Length;
        RelationBits = new double[nl]; RelationSaved = new double[nl];
        for (int j = 0; j < nl; j++)
        {
            var r = it.Relations[j];
            if (!r.IsPower) continue;
            RelationBits[j] = r.Bits;
            if (r.Source == Iteration.FromCells)
            {
                for (int q = 1; q < r.Cells.Length; q++) { var c = r.Cells[q] >= 0 && r.Cells[q] < byLexicon.Length ? byLexicon[r.Cells[q]] : null; if (Member(c)) { c.Replaced = true; RelationSaved[j] += c.Reference + c.Element; } }
                continue;
            }
            for (int q = 1; q < r.Iterations.Length; q++)
            {
                long from = r.Iterations[q], to = from + r.Unit;
                foreach (var s in Streams)
                    foreach (var c in Within(s, from, to))
                        if (Member(c) && Restates(At(s.Id, c.Start - r.Unit), c, r.Step)) { c.Replaced = true; RelationSaved[j] += c.Reference + c.Element; }
            }
        }

        // The totals.
        double cells = 0, residual = 0;
        foreach (var s in Streams) foreach (var c in s.Cells) { cells += c.Bits + (c.Replaced ? c.Reference + c.Element : 0); residual += c.Residual; }
        BitsCells = BitsStreams + cells;
        double relations = 0;
        for (int i = 0; i < nr; i++) relations += ReturnBits[i] - ReturnSaved[i];
        for (int j = 0; j < nl; j++) relations += RelationBits[j] - RelationSaved[j];
        BitsModel = Math.Max(0, BitsCells + relations);
        BitsResidual = residual;
        BitsArticulation = articulation.Sum(a => a.bits);

        Baselines(input);
        Derive(input, lex, ret, it, tree);
    }

    // §9 G2. Literal: each note alone, by its stream, IOI and duration in slots, pitch step (every note of a coding stream and
    // its graces, in onset order). Entropy: each stream's order-0 coder of the same three symbols, with an 8-bit codebook.
    void Baselines(MotifInput input)
    {
        var ev = input.Events; var grid = input.Grid;
        double stream = Math.Log2(Math.Max(1, Streams.Length + (Orphans.Length > 0 ? 1 : 0)));
        double literal = 0, entropy = 0;
        void Add(List<int> notes)
        {
            notes.Sort((a, b) => ev[a].Tick != ev[b].Tick ? ev[a].Tick.CompareTo(ev[b].Tick) : ev[a].Pitch != ev[b].Pitch ? ev[a].Pitch.CompareTo(ev[b].Pitch) : a.CompareTo(b));
            var ioi = new List<long>(); var step = new List<long>(); var length = new List<long>();
            long tick = grid.SpanStart; int pitch = 60;
            foreach (int i in notes)
            {
                long slot = Math.Max(1, grid.SlotAt(ev[i].Tick));
                long a = (long)Math.Round((ev[i].Tick - tick) / (double)slot), b = ev[i].Pitch - pitch, c = (long)Math.Round(ev[i].Duration / (double)slot);
                literal += stream + Gamma(a) + Gamma(b) + Gamma(c);
                ioi.Add(a); step.Add(b); length.Add(c);
                tick = ev[i].Tick; pitch = ev[i].Pitch;
            }
            entropy += Coded(ioi) + Coded(step) + Coded(length);
        }
        var byStream = new List<int>[input.Streams.All.Length]; var orphans = new List<int>();
        var home = new int[ev.Length]; Array.Fill(home, -1);
        foreach (var s in input.Streams.All)
            if (Coding(s)) for (int k = 0; k < s.Notes.Length; k++) home[s.Notes[k]] = s.Id;
        for (int i = 0; i < ev.Length; i++)
        {
            int h = home[i];
            if (h < 0 && ev[i].Grace && ev[i].Host >= 0) h = home[ev[i].Host];
            if (h < 0) orphans.Add(i); else (byStream[h] ??= new List<int>()).Add(i);
        }
        foreach (var list in byStream) if (list != null) Add(list);
        if (orphans.Count > 0) Add(orphans);
        BitsLiteral = literal; BitsEntropy = entropy;
    }

    // §9 G3: a note is derived when a non-prototype paradigm member, a power iteration after the first, a return target's
    // match, a cross-reference target (the principal line's notes) or a doubling target holds it; a grace follows its host.
    void Derive(MotifInput input, CellLexicon lex, Returns ret, Iteration it, Hierarchy tree)
    {
        var ev = input.Events; var derived = new bool[ev.Length];
        var ticks = new long[ev.Length]; for (int i = 0; i < ev.Length; i++) ticks[i] = ev[i].Tick;
        void Span(long a, long b)
        {
            int i = Array.BinarySearch(ticks, a); if (i < 0) i = ~i; else while (i > 0 && ticks[i - 1] == a) i--;
            for (; i < ev.Length && ticks[i] < b; i++) if (!ev[i].Drum && !ev[i].Grace) derived[i] = true;
        }
        void Stream(int stream, long a, long b)
        {
            if (stream < 0 || stream >= input.Streams.All.Length) return;
            var s = input.Streams[stream];
            for (int k = 0; k < s.Events; k++) if (s.Onset[k] >= a && s.Onset[k] < b) for (int q = 0; q < s.Count[k]; q++) derived[s.Notes[s.First[k] + q]] = true;
        }
        foreach (var p in lex.Paradigms) foreach (int m in p.Members) if (m != p.Prototype) foreach (int i in lex.Cells[m].Notes) derived[i] = true;
        foreach (var r in it.Relations)
        {
            if (r.IsPower && r.Source == Iteration.FromCells) { for (int q = 1; q < r.Cells.Length; q++) foreach (int i in lex.Cells[r.Cells[q]].Notes) derived[i] = true; }
            else if (r.IsPower && r.Iterations.Length > 1) Span(r.Iterations[1], r.End);
            else if (r.Kind == Relations.Doubling) Stream(r.StreamB, r.Start, r.End);
        }
        foreach (var r in ret.All) foreach (var e in r.Script) if (e.Op == Edits.Match) Span(e.TargetStart, e.TargetEnd);
        foreach (var x in tree.CrossReferences) Stream(input.Streams.Principal, x.Start, x.End);
        int count = 0;
        for (int i = 0; i < ev.Length; i++) { if (!derived[i] && ev[i].Grace && ev[i].Host >= 0 && derived[ev[i].Host]) derived[i] = true; if (derived[i]) count++; }
        DerivedNotes = count;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // §9 G5: the export. Cells are the members of the paradigms of every lane's outer line, the principal stream and the
    // textures a relation names (literal cells stay out: a gap is literal material), with at most MaxSparksPerCell sparks
    // each; above MaxCells cells or MaxBytes of JSON, the cells of non-principal streams go first, the least covering first.

    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    public PreparedPatternSong.MotifLayer Export(MotifInput input, CellLexicon lex, Returns ret, Iteration it, Hierarchy tree)
    {
        var settings = input.Settings; var all = input.Streams.All; int principal = input.Streams.Principal;
        var relations = new List<PreparedPatternSong.MotifRelation>(); var edits = new List<PreparedPatternSong.MotifEdit>();
        double ppq = input.Ppq;
        double B(long tick) => tick < 0 ? -1 : tick / ppq;
        // Relations: the returns, stage D's detections, the cross-references; nodes are filled in once their indices are known.
        int[] returnIndex = new int[ret.All.Length], relationIndex = new int[it.Relations.Length], crossIndex = new int[tree.CrossReferences.Length];
        for (int i = 0; i < ret.All.Length; i++)
        {
            var r = ret.All[i];
            var m = new PreparedPatternSong.MotifRelation
            {
                Id = relations.Count, Kind = Relations.Return, Level = r.Level, Node = tree.ReturnNode[i], Source = tree.ReturnSource[i], Count = 2,
                Steps = r.K % 12 == 0 ? r.K / 12 * 7 : 0, Semitones = r.K, Real = r.K % 12 != 0, Start = B(r.Start), End = B(r.End), SourceStart = B(r.SourceStart), SourceEnd = B(r.SourceEnd),
                FirstEdit = edits.Count, EditCount = r.Script.Length, Bits = (float)Finite(ReturnBits[i]), SavedBits = (float)Finite(ReturnSaved[i]), Text = tree.ReturnText[i]
            };
            for (int j = 0; j < r.Script.Length; j++)
            {
                var e = r.Script[j];
                edits.Add(new PreparedPatternSong.MotifEdit { Op = e.Op, SourceStart = B(e.SourceStart), SourceEnd = B(e.SourceEnd), TargetStart = B(e.TargetStart), TargetEnd = B(e.TargetEnd), Bits = (float)Finite(EditBits[i][j]) });
            }
            returnIndex[i] = m.Id; relations.Add(m);
        }
        var nodes = tree.Nodes;
        int Level(long start, long end)
        {
            int n = 0;
            for (bool down = true; down;)
            {
                down = false; var x = nodes[n];
                for (int k = 0; k < x.ChildCount; k++) { var y = nodes[x.FirstChild + k]; if (y.Start <= start && end <= y.End) { n = y.Id; down = true; break; } }
            }
            return Math.Min(3, nodes[n].Level + 1);
        }
        for (int j = 0; j < it.Relations.Length; j++)
        {
            var r = it.Relations[j]; int node = r.IsPower ? tree.PowerNode[j] : -1;
            var m = new PreparedPatternSong.MotifRelation
            {
                Id = relations.Count, Kind = r.Kind, Level = node >= 0 ? nodes[node].Level : Level(r.Start, r.End), Node = node, StreamA = r.StreamA, StreamB = r.StreamB,
                Steps = r.Step.Steps, Semitones = r.Step.Semitones, Count = r.Count, Period = r.Period, Order = r.Order, Start = B(r.Start), End = B(r.End),
                SourceStart = r.IsPower ? B(r.Start) : -1, SourceEnd = r.IsPower ? B(r.Start + r.Unit) : -1,
                Inverted = r.Step.Inverted, Real = r.Real, ModeChange = r.Step.Parallel != 0, CrossScale = r.CrossScale, Contrary = r.Contrary,
                Bits = (float)Finite(RelationBits[j]), SavedBits = (float)Finite(RelationSaved[j]), Text = r.Text, Pattern = r.Pattern, Symmetry = r.Symmetry.ToArray()
            };
            relationIndex[j] = m.Id; relations.Add(m);
        }
        for (int k = 0; k < tree.CrossReferences.Length; k++)
        {
            var x = tree.CrossReferences[k];
            var m = new PreparedPatternSong.MotifRelation
            {
                Id = relations.Count, Kind = Relations.CrossReference, Level = x.Level, Node = x.Node, Source = x.Source, Count = 2, Steps = x.G.Steps, Semitones = x.G.Semitones,
                Inverted = x.G.Inverted, Real = x.G.Frame == Transform.Real, ModeChange = x.G.Parallel != 0, Start = B(x.Start), End = B(x.End), SourceStart = B(x.SourceStart), SourceEnd = B(x.SourceEnd), Text = x.Text
            };
            crossIndex[k] = m.Id; relations.Add(m);
        }

        // Which streams' cells are exported, and the order in which the caps drop them.
        var referenced = new bool[all.Length];
        foreach (var r in it.Relations) { if (r.StreamA >= 0 && r.StreamA < all.Length) referenced[r.StreamA] = true; if (r.StreamB >= 0 && r.StreamB < all.Length) referenced[r.StreamB] = true; }
        var chosen = new List<int>();
        foreach (var s in all) if (s.Kind == MotifStreams.Line || s.Id == principal || s.Kind == MotifStreams.Texture && referenced[s.Id]) chosen.Add(s.Id);
        int Covered(int s) { int n = 0; foreach (var c in lex.Of(s)) if (c.Paradigm >= 0) n += c.Notes.Length; return n; }
        var droppable = chosen.Where(s => s != principal).OrderBy(Covered).ThenByDescending(s => s).ToList();
        int MemberCount(IEnumerable<int> streams) => streams.Sum(s => lex.Of(s).Count(c => c.Paradigm >= 0));
        int forCells = 0, forBytes = 0;
        while (MemberCount(chosen) > settings.MaxCells && droppable.Count > 0) { chosen.Remove(droppable[0]); droppable.RemoveAt(0); forCells++; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        bool estimated = false;
        while (true)
        {
            var layer = Layer(input, lex, ret, it, tree, chosen, relations, edits, returnIndex, relationIndex, crossIndex);
            int bytes = JsonSerializer.SerializeToUtf8Bytes(layer, Json).Length;
            if (bytes > settings.MaxBytes && droppable.Count > 0 && !estimated)
            {
                // Each stream's share of the JSON (its cells, sparks and paradigms) from this layer: enough streams go at once, so
                // the cap costs a few serialisations, not one per stream.
                estimated = true;
                var share = new long[all.Length];
                foreach (var c in layer.Cells) share[c.Stream] += JsonSerializer.SerializeToUtf8Bytes(c, Json).Length + 1;
                foreach (var x in layer.Sparks) share[layer.Cells[x.Cell].Stream] += JsonSerializer.SerializeToUtf8Bytes(x, Json).Length + 1;
                foreach (var p in layer.Paradigms) share[p.Stream] += JsonSerializer.SerializeToUtf8Bytes(p, Json).Length + 1;
                long excess = bytes - settings.MaxBytes;
                while (excess > 0 && droppable.Count > 0) { excess -= share[droppable[0]]; chosen.Remove(droppable[0]); droppable.RemoveAt(0); forBytes++; }
                continue;
            }
            if (bytes <= settings.MaxBytes || droppable.Count == 0)
            {
                var status = new List<string>();
                if (forCells > 0) status.Add($"the cells of {forCells} stream{(forCells == 1 ? "" : "s")} left out (more than {settings.MaxCells.ToString("N0", inv)} cells)");
                if (forBytes > 0) status.Add($"the cells of {forBytes} {(forCells > 0 ? "more " : "")}stream{(forBytes == 1 ? "" : "s")} left out (the layer stays under {(settings.MaxBytes / 1e6).ToString("0.#", inv)} MB)");
                if (bytes > settings.MaxBytes) status.Add($"the layer is {(bytes / 1e6).ToString("0.0", inv)} MB, above the {(settings.MaxBytes / 1e6).ToString("0.#", inv)} MB cap");
                layer.Status = string.Join("; ", status);
                return layer;
            }
            chosen.Remove(droppable[0]); droppable.RemoveAt(0); forBytes++;
        }
    }

    PreparedPatternSong.MotifLayer Layer(MotifInput input, CellLexicon lex, Returns ret, Iteration it, Hierarchy tree, List<int> chosen, List<PreparedPatternSong.MotifRelation> relations,
        List<PreparedPatternSong.MotifEdit> edits, int[] returnIndex, int[] relationIndex, int[] crossIndex)
    {
        var settings = input.Settings; var all = input.Streams.All; var ev = input.Events; double ppq = input.Ppq;
        var exported = new bool[all.Length]; foreach (int s in chosen) exported[s] = true;
        // Paradigms of exported streams, in lexicon order; atoms that hold one of them.
        var paradigmIndex = new int[lex.Paradigms.Length]; Array.Fill(paradigmIndex, -1);
        var atomIndex = new int[lex.Atoms.Length]; Array.Fill(atomIndex, -1);
        var paradigms = new List<PreparedPatternSong.MotifParadigm>(); var atoms = new List<PreparedPatternSong.MotifAtom>();
        foreach (var a in lex.Atoms)
            if (a.Paradigms.Any(p => exported[lex.Paradigms[p].Stream]))
            {
                atomIndex[a.Id] = atoms.Count;
                atoms.Add(new PreparedPatternSong.MotifAtom { Id = atoms.Count, Kind = a.Kind, Name = a.Name, Signature = a.Signature });
            }
        var noteOf = new int[lex.Paradigms.Length][];   // per paradigm: element → its lead's index in MotifParadigm.Notes
        foreach (var p in lex.Paradigms)
        {
            if (!exported[p.Stream]) continue;
            paradigmIndex[p.Id] = paradigms.Count;
            var proto = lex.Cells[p.Prototype];
            var m = new PreparedPatternSong.MotifParadigm
            {
                Id = paradigms.Count, Atom = p.Atom >= 0 ? atomIndex[p.Atom] : -1, Stream = p.Stream, Mirrored = p.Mirrored, Variants = p.Variants, Clock = p.Clock, Sign = p.Sign,
                Letter = p.Letter, Name = p.Name, Signature = p.Signature, Beats = proto.W.Length / ppq, Achiral = p.Achiral, Symmetry = p.Symmetry.ToArray()
            };
            m.Notes = PrototypeNotes(input, lex, p, proto, out noteOf[p.Id]);
            paradigms.Add(m);
        }
        // Cells, grouped by stream (exported streams only) in time order, and their sparks.
        var cells = new List<PreparedPatternSong.MotifCell>(); var sparks = new List<PreparedPatternSong.MotifSpark>();
        var streams = new PreparedPatternSong.MotifStream[all.Length];
        var cellPower = new int[lex.Cells.Length]; Array.Fill(cellPower, -1);
        for (int j = 0; j < it.Relations.Length; j++) { var r = it.Relations[j]; if (r.IsPower && r.Source == Iteration.FromCells) foreach (int c in r.Cells) if (c >= 0 && c < cellPower.Length && cellPower[c] < 0) cellPower[c] = relationIndex[j]; }
        var nodes = tree.Nodes;
        int NodeAt(long tick)
        {
            int n = 0;
            for (bool down = true; down;)
            {
                down = false; var x = nodes[n];
                for (int k = 0; k < x.ChildCount; k++) { var y = nodes[x.FirstChild + k]; if (y.Start <= tick && tick < y.End) { n = y.Id; down = true; break; } }
            }
            return n;
        }
        int mirrored = 0;
        foreach (var s in all)
        {
            var ms = new PreparedPatternSong.MotifStream { Id = s.Id, Track = s.Track, Channel = s.Channel, Staff = s.Staff, Voice = s.Voice, Name = s.Name, Kind = s.Kind, Bass = s.Bass, FirstCell = cells.Count };
            streams[s.Id] = ms;
            if (!exported[s.Id]) continue;
            foreach (var c in lex.Of(s.Id))
            {
                if (c.Paradigm < 0) continue;
                var p = lex.Paradigms[c.Paradigm];
                var m = new PreparedPatternSong.MotifCell
                {
                    Stream = s.Id, Paradigm = paradigmIndex[c.Paradigm], Node = nodes.Length > 0 ? NodeAt(c.Start) : -1, Steps = c.G.Steps, Semitones = c.G.Semitones, Degree = c.Degree,
                    Relation = cellPower[c.Id], Start = c.Start / ppq, End = c.End / ppq, Inverted = c.G.Inverted, Exact = c.Exact, Real = c.G.Frame == Transform.Real,
                    ModeChange = c.G.Parallel != 0, Scale = (float)c.G.Scale, Text = c.Text, FirstSpark = sparks.Count
                };
                var kept = c.Sparks.Select((x, i) => (x, i)).ToList();
                if (kept.Count > settings.MaxSparksPerCell)
                {
                    // The largest edits stay: insertions and deletions, then substitutions, alterations, a held head, shifts.
                    static int Weight(int kind) => kind switch { Sparks.Inserted => 0, Sparks.Deleted => 1, Sparks.Substituted => 2, Sparks.Altered => 3, Sparks.Lengthened => 4, Sparks.Octave => 5, Sparks.Ornament => 6, _ => 7 };
                    kept = kept.OrderBy(k => Weight(k.x.Kind)).ThenBy(k => k.x.Tick).ThenBy(k => k.i).Take(settings.MaxSparksPerCell).OrderBy(k => k.x.Tick).ThenBy(k => k.i).ToList();
                    m.Exact = false;
                }
                foreach (var (x, _) in kept)
                    sparks.Add(new PreparedPatternSong.MotifSpark { Beat = x.Tick / ppq, Cell = cells.Count, Kind = x.Kind, Pitch = x.Pitch, Note = x.Kind == Sparks.Inserted || x.Note < 0 || x.Note >= noteOf[p.Id].Length ? -1 : noteOf[p.Id][x.Note], Tag = x.Tag ?? "" });
                m.SparkCount = sparks.Count - m.FirstSpark;
                if (m.SparkCount == 0) m.FirstSpark = -1;
                if (m.Inverted) mirrored++;
                var mp = paradigms[m.Paradigm]; mp.Members++;
                if (c.Id == p.Prototype) mp.PrototypeCell = cells.Count;
                if (p.Atom >= 0)
                {
                    var a = atoms[atomIndex[p.Atom]]; a.Members++;
                    if (c.Sign > 0) a.Positive++; else if (c.Sign < 0) a.Negative++; else a.Neutral++;
                }
                cells.Add(m);
            }
            ms.CellCount = cells.Count - ms.FirstCell;
        }
        foreach (var p in paradigms) if (p.Atom >= 0) atoms[p.Atom].Paradigms++;

        // Nodes in the tree's preorder; a node's relation is the return, power or cross-reference it states.
        var bits = NodeBits(input, it, tree, returnIndex, relationIndex);
        var nodeArray = new PreparedPatternSong.MotifNode[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i];
            int relation = n.Return >= 0 ? returnIndex[n.Return] : n.Power >= 0 ? relationIndex[n.Power] : n.CrossReference >= 0 ? crossIndex[n.CrossReference] : -1;
            nodeArray[i] = new PreparedPatternSong.MotifNode
            {
                Id = n.Id, Parent = n.Parent, FirstChild = n.FirstChild, ChildCount = n.ChildCount, Depth = n.Depth, Level = n.Level, Kind = n.Kind, Start = n.Start / ppq, End = n.End / ppq,
                Label = n.Label, Name = n.Name, Text = n.Text, Omits = n.Omits, Prototype = n.Prototype, Relation = relation, Paradigm = n.Paradigm >= 0 && n.Paradigm < paradigmIndex.Length ? paradigmIndex[n.Paradigm] : -1,
                Steps = n.Steps, Semitones = n.Semitones, Inverted = n.Inverted, Real = n.Real, Projected = n.Projected, Count = n.Count, Iteration = n.Iteration,
                StepSteps = n.StepSteps, StepSemitones = n.StepSemitones, Order = n.Order, Bits = (float)Finite(bits[i]), Evidence = (float)Finite(n.Evidence)
            };
        }

        var rel = relations.ToArray();
        var stats = new PreparedPatternSong.MotifStats
        {
            Notes = Notes, Streams = all.Length, Cells = cells.Count, Paradigms = paradigms.Count, Atoms = atoms.Count, Nodes = nodeArray.Length, Relations = rel.Length,
            Powers = rel.Count(r => r.Kind == Relations.Power), Returns = rel.Count(r => r.Kind == Relations.Return), MirroredCells = mirrored, DerivedNotes = DerivedNotes,
            BitsLiteral = (float)Finite(BitsLiteral), BitsEntropy = (float)Finite(BitsEntropy), BitsCells = (float)Finite(BitsCells), BitsModel = (float)Finite(BitsModel),
            BitsResidual = (float)Finite(BitsResidual), BitsArticulation = (float)Finite(BitsArticulation), Derived = (float)Finite(Notes == 0 ? 0 : DerivedNotes / (double)Notes)
        };
        return new PreparedPatternSong.MotifLayer
        {
            Schema = 1, Formula = tree.Formula, Caption = Caption(tree, stats), Levels = Hierarchy.Levels.ToArray(), PrincipalStream = input.Streams.Principal,
            Streams = streams, Atoms = atoms.ToArray(), Paradigms = paradigms.ToArray(), Nodes = nodeArray, Cells = cells.ToArray(), Sparks = sparks.ToArray(),
            Relations = rel, Edits = edits.ToArray(), Stats = stats
        };
    }

    // The prototype's notes as MotifParadigm.Notes: per element (the tied-in note first) its lead, then the notes struck with
    // it away from the lead; onsets and lengths in beats from the cell start, steps and semitones from the first note.
    static PreparedPatternSong.MotifNote[] PrototypeNotes(MotifInput input, CellLexicon lex, CellLexicon.Paradigm p, CellLexicon.Cell proto, out int[] noteOf)
    {
        var s = input.Streams[p.Stream]; var ev = input.Events; var x = proto.W; double ppq = input.Ppq;
        var line = p.Stream < lex.Lines.Length ? lex.Lines[p.Stream] : null;
        var list = new List<PreparedPatternSong.MotifNote>(); var map = new List<int>();
        int firstMorph = 0, firstPitch = 0; bool first = true;
        var elements = new List<int>(); if (x.Tie >= 0) elements.Add(x.Tie); for (int k = x.First; k < x.First + x.Count; k++) elements.Add(k);
        foreach (int k in elements)
        {
            bool tied = k == x.Tie;
            map.Add(list.Count);
            for (int q = 0; q < (tied ? 1 : s.Count[k]); q++)
            {
                var e = ev[s.Notes[s.First[k] + q]];
                if (first) { firstMorph = e.Morph; firstPitch = e.Pitch; first = false; }
                long onset = tied ? 0 : e.Tick - x.Start, length = tied ? e.Tick + e.Duration - x.Start : e.Duration;
                list.Add(new PreparedPatternSong.MotifNote
                {
                    Onset = onset / ppq, Length = length / ppq, Step = e.Morph - firstMorph, Semitones = e.Pitch - firstPitch, Alter = e.Drum ? 0 : e.Alter, Chord = q, Tied = tied,
                    Role = q == 0 && line != null && k < line.Events ? line.Role(k) : ""
                });
            }
        }
        noteOf = map.ToArray();
        return list.ToArray();
    }

    // A node's bits: its coding cells' (members a power or return restates cost only their residual) and those of the
    // relations it states.
    double[] NodeBits(MotifInput input, Iteration it, Hierarchy tree, int[] returnIndex, int[] relationIndex)
    {
        var nodes = tree.Nodes; var result = new double[nodes.Length];
        var all = new List<(long start, double bits)>();
        foreach (var s in Streams) foreach (var c in s.Cells) all.Add((c.Start, c.Bits));
        all.Sort((a, b) => a.start.CompareTo(b.start));
        var starts = all.Select(a => a.start).ToArray(); var prefix = new double[all.Count + 1];
        for (int i = 0; i < all.Count; i++) prefix[i + 1] = prefix[i] + all[i].bits;
        int Lower(long t) { int i = Array.BinarySearch(starts, t); if (i < 0) return ~i; while (i > 0 && starts[i - 1] == t) i--; return i; }
        for (int i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i];
            result[i] = prefix[Lower(n.End)] - prefix[Lower(n.Start)];
            if (n.Return >= 0 && n.Return < ReturnBits.Length) result[i] += ReturnBits[n.Return];
            if (n.Power >= 0 && n.Power < RelationBits.Length) result[i] += RelationBits[n.Power];
        }
        return result;
    }

    // §9 G3: "<piece line> · <Notes> notes → <Paradigms> cells in <Atoms> atoms · <sections> sections · <phrases> phrases ·
    // <Derived>% derived · <BitsLiteral/BitsModel>× smaller".
    static string Caption(Hierarchy tree, PreparedPatternSong.MotifStats stats)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int sections = tree.Nodes.Count(n => n.Level == 1), phrases = tree.Nodes.Count(n => n.Level == 2);
        double ratio = stats.BitsModel > 0 ? stats.BitsLiteral / stats.BitsModel : 0;
        string Plural(int n, string word) => n.ToString("N0", inv) + " " + word + (n == 1 ? "" : "s");
        return string.Join(" · ", new[]
        {
            tree.Line, $"{Plural(stats.Notes, "note")} → {Plural(stats.Paradigms, "cell")} in {Plural(stats.Atoms, "atom")}", Plural(sections, "section"), Plural(phrases, "phrase"),
            string.Format(inv, "{0:0}% derived", 100 * stats.Derived), string.Format(inv, "{0:0.0}× smaller", ratio)
        }.Where(x => x.Length > 0));
    }
    static double Finite(double x) => double.IsFinite(x) ? x : 0;
}
