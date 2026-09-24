using NAudio.Midi;

// A generated song for the lyric displays, on public-domain words and tunes: "The Star"
// (Jane Taylor, 1806) sung to the traditional "Ah! vous dirai-je, maman" melody; the opening
// of "Hiawatha's Childhood" (Longfellow, 1855, trochaic tetrameter) and Shakespeare's
// Sonnet 18 (lines 1–4 and 13–14, iambic pentameter) spoken over a drum groove. The
// arrangement and MIDI are original. Sung syllables are karaoke lyric events on the vocal's
// notes (long notes carry pitch-bend vibrato); spoken syllables are lyric events on a
// "Rap Vocal" track with no notes, stresses on the beats.
public static partial class SelfTests
{
    public const string LyricSheet = @"# The Star (Jane Taylor, 1806) · Hiawatha's Childhood (Longfellow, 1855) · Sonnet 18 (Shakespeare)
[Verse 1]
Twin-kle, twin-kle, lit-tle star,
How I won-der what you are!
Up a-bove the world so high,
Like a dia-mond in the sky.
Twin-kle, twin-kle, lit-tle star,
How I won-der what you are!

[Rap 1 | spoken]
By the shores of Git-che Gu-mee,
By the shin-ing Big-Sea-Wa-ter,
Stood the wig-wam of No-ko-mis,
Daugh-ter of the Moon, No-ko-mis.
Dark be-hind it rose the for-est,
Rose the black and gloom-y pine-trees,
Rose the firs with cones up-on them;
Bright be-fore it beat the wa-ter.

[Verse 2]
When the blaz-ing sun is gone,
When he noth-ing shines up-on,
Then you show your lit-tle light,
Twin-kle, twin-kle, all the night.
Twin-kle, twin-kle, lit-tle star,
How I won-der what you are!

[Rap 2 | spoken]
Shall I com-pare thee to a sum-mer's day_?
Thou art more love-ly and more tem-per-ate_:
Rough winds do shake the dar-ling buds of May_,
And sum-mer's lease hath all too short a date_;
So long as men can breathe or eyes can see_,
So long lives this, and this gives life to thee_.

[Verse 3]
Then the trav-ller in the dark
Thanks you for your ti-ny spark;
He could not see which way to go,
If you did not twin-kle so.
Twin-kle, twin-kle, lit-tle star,
How I won-der what you are!
";
    public const string LyricBoundaries = "1 Intro\n3 Verse\n15 Rap\n23 Verse\n35 Rap\n47 Verse\n59 Outro";

    sealed class LyricScore
    {
        public readonly Score Score = new();
        public readonly List<(double beat, string text, int track)> Words = new();
        public readonly List<(double beat, int value)> Bends = new();
    }

    // Stanza syllables as the sheet splits them (hyphens), punctuation dropped.
    static List<string> SheetSyllables(string stanza)
    {
        var sheet = Lyrics.Parse(LyricSheet).First(s => s.Name == stanza);
        return sheet.Lines.SelectMany(l => l.Words.SelectMany(w => w.Syllables)).ToList();
    }

    static LyricScore LyricsScore()
    {
        var song = new LyricScore(); var s = song.Score;
        const int Keys = 1, Bass = 2, Voice = 3, Drums = 4, Rap = 5;
        const int c4 = 60, d4 = 62, e4 = 64, f4 = 65, g4 = 67, a4 = 69;
        void Chord(double at, double length, int root, bool minor, float velocity)
        {
            foreach (int x in new[] { 0, minor ? 3 : 4, 7 }) s.Note(at, length, root + 12 + x, velocity, Keys, Keys);
        }
        void Groove(double at, bool rap)
        {
            if (rap)
            {
                s.Note(at, .1, 36, .9f, 10, Drums); s.Note(at + 1.5, .1, 36, .7f, 10, Drums); s.Note(at + 2.5, .1, 36, .75f, 10, Drums);
                s.Note(at + 1, .1, 38, .85f, 10, Drums); s.Note(at + 3, .1, 38, .85f, 10, Drums);
                for (int k = 0; k < 8; k++) s.Note(at + k * .5, .1, 42, k % 2 == 0 ? .55f : .35f, 10, Drums);
            }
            else
            {
                s.Note(at, .1, 36, .7f, 10, Drums); s.Note(at + 2, .1, 36, .6f, 10, Drums);
                s.Note(at + 1, .1, 38, .45f, 10, Drums); s.Note(at + 3, .1, 38, .45f, 10, Drums);
                for (int k = 0; k < 4; k++) s.Note(at + k, .1, 42, .4f, 10, Drums);
            }
        }
        // One bar: its chords (root, minor, beats) for keys and bass.
        void Bar((int root, bool minor, double beats)[] chords, bool rap, bool drums = true)
        {
            s.Bar(4); double at = s.Beat;
            foreach (var (root, minor, beats) in chords)
            {
                if (rap) { Chord(at, .9, root, minor, .5f); Chord(at + 1.5, .4, root, minor, .4f); }
                else Chord(at, beats, root, minor, .45f);
                s.Note(at, rap ? .9 : beats, root - 12, .7f, Bass, Bass);
                if (rap) { s.Note(at + 1.5, .4, root, .55f, Bass, Bass); s.Note(at + 2, .9, root - 12, .65f, Bass, Bass); s.Note(at + 3, .9, root - 5, .6f, Bass, Bass); }
                at += beats;
            }
            if (drums) Groove(s.Beat, rap);
            s.Beat += 4;
        }
        const int C = 48, D = 50, F = 53, G = 55, A = 57;
        // Twinkle's melody in two-bar lines: six quarter notes and a half note each.
        int[][] tune = { new[] { c4, c4, g4, g4, a4, a4, g4 }, new[] { f4, f4, e4, e4, d4, d4, c4 }, new[] { g4, g4, f4, f4, e4, e4, d4 }, new[] { g4, g4, f4, f4, e4, e4, d4 }, new[] { c4, c4, g4, g4, a4, a4, g4 }, new[] { f4, f4, e4, e4, d4, d4, c4 } };
        (int, bool, double)[][] verseChords =
        {
            new[] { (C, false, 4.0) }, new[] { (F, false, 2.0), (C, false, 2.0) }, new[] { (F, false, 2.0), (C, false, 2.0) }, new[] { (G, false, 2.0), (C, false, 2.0) },
            new[] { (C, false, 2.0), (F, false, 2.0) }, new[] { (C, false, 2.0), (G, false, 2.0) }, new[] { (C, false, 2.0), (F, false, 2.0) }, new[] { (C, false, 2.0), (G, false, 2.0) },
            new[] { (C, false, 4.0) }, new[] { (F, false, 2.0), (C, false, 2.0) }, new[] { (F, false, 2.0), (C, false, 2.0) }, new[] { (G, false, 2.0), (C, false, 2.0) },
        };
        void Verse(string stanza, bool third)
        {
            var words = SheetSyllables(stanza); int w = 0; double start = s.Beat;
            for (int bar = 0; bar < 12; bar++)
                // The third verse ends its B phrase on Am – D (D is V/V): a chord variation.
                Bar(third && bar == 7 ? new[] { (A, true, 2.0), (D, false, 2.0) } : verseChords[bar], false);
            for (int line = 0; line < 6; line++)
            {
                double at = start + line * 8;
                var notes = tune[line].Select((p, k) => (pitch: p, beat: at + k, length: k == 6 ? 2.0 : 1.0)).ToList();
                // "He could not see which way to go" has eight syllables: the half note splits.
                if (third && line == 2) { notes[6] = (d4, at + 6, 1.0); notes.Add((d4, at + 7, 1.0)); }
                foreach (var (pitch, beat, length) in notes)
                {
                    s.Note(beat, length * .95, pitch, .8f, Voice, Voice);
                    song.Words.Add((beat, words[w++], Voice));
                    // A long note swells into vibrato: 5.5 Hz, a third of a semitone, from 40% of the note.
                    if (length >= 2)
                        for (double t = beat + length * .4; t < beat + length * .95; t += 1 / 48.0)
                        {
                            double seconds = (t - beat - length * .4) * .6;
                            song.Bends.Add((t, 8192 + (int)Math.Round(Math.Sin(2 * Math.PI * 5.5 * seconds) * Math.Min(1, seconds / .25) * .35 / 2 * 8191)));
                        }
                }
                song.Bends.Add((at + 7.98, 8192));
            }
        }
        // Spoken lines: trochees one bar each (stresses on the beats, the rest on the upbeats);
        // pentameter lines two bars each, from an upbeat pickup, the last stress held.
        void Trochees(string stanza, (int, bool, double)[][] loop, int passes)
        {
            var words = SheetSyllables(stanza); double start = s.Beat;
            for (int p = 0; p < passes; p++) foreach (var bar in loop) Bar(bar, true);
            for (int k = 0; k < words.Count; k++) song.Words.Add((start + k * .5, words[k], Rap));
        }
        void Pentameter(string stanza, (int, bool, double)[][][] passes)
        {
            var words = SheetSyllables(stanza); double start = s.Beat;
            foreach (var pass in passes) foreach (var bar in pass) Bar(bar, true);
            for (int line = 0; line < words.Count / 10; line++)
            {
                double at = start + line * 8;
                song.Words.Add((at - .5, words[line * 10], Rap));
                for (int k = 1; k < 10; k++) song.Words.Add((at + (k - 1) * .5, words[line * 10 + k], Rap));
            }
        }
        var rapLoop = new[] { new[] { (A, true, 4.0) }, new[] { (F, false, 4.0) }, new[] { (C, false, 4.0) }, new[] { (G, false, 4.0) } };
        var rapEnding = new[] { new[] { (A, true, 4.0) }, new[] { (F, false, 4.0) }, new[] { (G, false, 4.0) }, new[] { (G, false, 4.0) } };
        Bar(new[] { (C, false, 4.0) }, false); Bar(new[] { (G, false, 4.0) }, false);   // bars 1–2 intro
        Verse("Verse 1", false);                                                             // 3
        Trochees("Rap 1", rapLoop, 2);                                                       // 15
        Verse("Verse 2", false);                                                             // 23
        Pentameter("Rap 2", new[] { rapLoop, rapLoop, rapEnding });                          // 35: the third pass ends on G
        Verse("Verse 3", true);                                                              // 47
        Bar(new[] { (C, false, 4.0) }, false, false); Bar(new[] { (C, false, 4.0) }, false, false); // 59 outro
        return song;
    }

    public static void WriteLyricFixture(string path)
    {
        var song = LyricsScore(); var score = song.Score;
        const int ppq = 480, bpm = 100;
        var events = new MidiEventCollection(1, ppq);
        long Tick(double beat) => (long)Math.Round(beat * ppq);
        events.AddTrack(); events.AddEvent(new TempoEvent(60000000 / bpm, 0), 0);
        events.AddEvent(new KeySignatureEvent(0, 0, 0), 0);
        events.AddEvent(new TimeSignatureEvent(0, 4, 2, 24, 8), 0);
        events.AddEvent(new TextEvent("Twinkle, Hiawatha and Sonnet 18 (generated lyric fixture)", MetaEventType.SequenceTrackName, 0), 0);
        string[] names = { "", "Keys", "Bass", "Lead Vocal", "Drums", "Rap Vocal" };
        for (int track = 1; track <= 5; track++)
        {
            events.AddTrack(); events.AddEvent(new TextEvent(names[track], MetaEventType.SequenceTrackName, 0), track);
            foreach (var n in score.Notes.Where(x => x.Track == track).OrderBy(x => x.Beat))
            {
                var on = new NoteOnEvent(Tick(n.Beat), n.Channel, n.Pitch, Math.Clamp((int)Math.Round(n.Velocity * 127), 1, 127), Math.Max(1, (int)Tick(n.Length)));
                events.AddEvent(on, track); events.AddEvent(on.OffEvent, track);
            }
            foreach (var (beat, text, owner) in song.Words.Where(w => w.track == track)) events.AddEvent(new TextEvent(text, MetaEventType.Lyric, Tick(beat)), track);
            if (track == 3) foreach (var (beat, value) in song.Bends) events.AddEvent(new PitchWheelChangeEvent(Tick(beat), 3, Math.Clamp(value, 0, 16383)), track);
        }
        long end = Tick(score.Beat);
        for (int t = 0; t < events.Tracks; t++) events.AddEvent(new MetaEvent(MetaEventType.EndTrack, 0, end), t);
        events.PrepareForExport();
        MidiFile.Export(path, events);
        string folder = Path.GetDirectoryName(Path.GetFullPath(path));
        File.WriteAllText(Path.Combine(folder, "lyrics.txt"), LyricSheet);
        File.WriteAllText(Path.Combine(folder, "song.json"), System.Text.Json.JsonSerializer.Serialize(new { Key = 3, Minor = false, KeySource = "Generated in C major", LeadVocalTrack = 3, SectionBoundaries = LyricBoundaries, SectionSource = "Generated fixture boundaries" }));
        Console.WriteLine($"{path}: {score.Bars.Count} bars, {score.Notes.Count} notes, {song.Words.Count} lyric syllables, {song.Bends.Count} vibrato bends · lyrics.txt and song.json beside it");
    }
}
