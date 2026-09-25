# Lyric mode

Lyric mode (**View → Lyrics**) focuses on the words. Sung lines ride the vocal's pattern
wheel along its melody. Spoken or rapped lines ride a sawtooth rack meshed with the drum
wheel, where meter shows as wells and ramps under the words. A rhyme board lays each stanza on
its beats, with end rhyme, front rhyme and meter matchups between stanzas. Lyrics are shown only
in lyric mode.

## The lyric sheet

A `lyrics.txt` beside the MIDI (or `Lyrics` in `song.json`) is the source of the words:

```text
[Verse 1]
Twin-kle, twin-kle, lit-tle star,
How I won-der what you are!

[Rap 1 | spoken]
By the shores of Git-che Gu-mee,
By the shin-ing Big-Sea-Wa-ter,

[Rap 2 | spoken]
Shall I com-pare thee to a sum-mer's day_?
```

- **Stanzas.** A `[Name]` header starts each stanza. `| spoken` (or a name containing *rap* or
  *spoken*) marks spoken lines; `| sung` overrides. Lines without a header form stanzas split
  at blank lines.
- **Syllables.** Hyphens split syllables. Unhyphenated words are split by rule (lit-tle,
  de-clines, shines). Hyphenate names and anything the rules might miss.
- **Holds.** A trailing `_` holds a syllable: over the next note when sung, and across several
  teeth of the rack when spoken.
- **Names.** A capitalised word inside a line is a name ("the buds of May") and is never a weak
  word.

## Sync (PatternPrep)

Timing comes from the best evidence available, in this order:

1. **MIDI lyric events** (karaoke `Lyric` meta events, or `@K` text events). Each event times
   one syllable; events and sheet syllables are aligned by their letters.
2. **Audio alignment**, from `lyrics.timing.json` (written by `Tools/SongLibrary/lyric_sync.py`).
   The aligner's per-syllable times are used. Where the vocal also has MIDI notes, a sung
   syllable snaps to a note onset within a third of a beat.
3. **The vocal's notes.** Sung syllables are laid on the notes in order by dynamic programming:
   - one syllable per note, a held syllable taking several (cheapest on a stressed or `_`
     syllable);
   - a note may go unsung;
   - lines end where the melody breathes.

   The vocal lane is the lead vocal, else the lane the lyric events sit on.
4. **Estimated placement.** Spoken lines with no timing are laid on the untaken bars: stresses on
   beats, the other syllables on upbeats. The bundle says so.

`Lyrics.Sync` records which were used.

## Reading the words

Each **syllable** carries:

- **Stress**: lexical stress from rules plus a small lexicon. Weak function words (the, of, I,
  you…) are unstressed but may take a beat.
- **Metric** and **Position**: where the syllable falls in the bar (2 downbeat, 1 beat,
  0 upbeat, −1 between).
- **Teeth**: how many eighth-note teeth it is held across.
- **Emphasis**: stress, metrical weight, length, loudness, and a melodic peak.
- **Vibrato**: from pitch bends on the vocal (at least one and a half cycles, a tenth of a
  semitone deep), or from the audio's pitch.

Each **line** carries:

- **Stresses**, the lexical pattern (`/x/x/x/`), and **Beats**, the delivered one. Beats marks
  the strongest metrical level that at most about half the syllables reach: the beats for a line
  in eighths, beats 1 and 3 for a line in quarters.
- **Meter**, scanned against iambic, trochaic, anapestic and dactylic feet with catalexis, a
  headless start or a feminine ending. The stanza's most common foot breaks ties.
- **End rhyme**, from a spelling-based rhyme key for the sound from the last stressed vowel.
  - A perfect rhyme shares that key (star/are, high/sky, gone/upon).
  - A repeated word rhymes with itself (Nokomis/Nokomis).
  - A weak rhyme shares only the last syllable (temperate/date).
  - Letters give each stanza's scheme (AABBAA, ABABCC).
- **Front rhyme**, strongest kind first across the stanza: repeated opening words ("By the",
  "Rose the", "So long"), a head rhyme on the first stressed syllable, or alliteration
  (Daughter / Dark).

**Matchups** compare the same line of two stanzas:

- stanzas sung to one pattern (the same section family): verse 1 with verse 2, verse 2 with
  verse 3;
- consecutive spoken stanzas.

The score weights beat alignment 0.6 and stress alignment 0.4. The note names what differs:
"+1 syllable · trochaic tetrameter → iambic tetrameter", or "same beats, 2 stresses move".

## Display

| Part | Where | Shows |
|---|---|---|
| Vocal wheel | panel, left | The vocal changer's top disc facing the viewer, turning once per loop under the comb. The melody is a curve through the notes (radius is pitch): an arc per held note, then a cubic bend to the next. Sung syllables ride it letter by letter, turned with the curve. The one being sung is lit, with a glowing stroke along its stretch of curve that grows with its emphasis. Under vibrato its letters shiver at the vibrato's rate and depth. |
| Rhyme board | panel, right | The stanza being heard, a row per line on its beat grid. A bar over each stressed syllable and a cup over each unstressed one; a dot under each syllable on a beat (larger on a downbeat). A line over held syllables. End-rhyme letters circled and bracketed on the right; front-rhyme brackets and labels on the left; meters under the lines. The matching line of the other stanza is ghosted beneath on the same beats, with its score and note. |
| Drum rack | scene, a strip above the panel | The drum disc becomes a ratchet, one tooth per beat. A sawtooth rack meshes with it at twelve o'clock and slides at the rim's speed, so each syllable reaches the comb when it is spoken. Each tooth ramps up through its beat (the upbeat halfway) to a crest, then drops into the next downbeat's well. The line and its meter are written above the rack. |

On the rack:

- A **downbeat** syllable waits on the crest, then drops into its well with a bounce.
- An **upbeat** syllable rides the ramp and is bumped up over the saw.
- A **held** syllable floats in a bezier arc above the teeth while it lasts. It stays at the
  comb as the rack runs under it, then dives into the well where it ends.
- Emphasis blooms as each syllable lands; stressed syllables are brighter at rest.

Trochees read as well, ramp, well, ramp; iambs as ramp, well.

**Layout.** Lyric mode stacks the screen: the drum strip above, running as wide as the window,
and the panel below. While a spoken stanza is heard the resting vocal wheel steps back and the
board widens. The torus steps back throughout.

## Audio sync (`Tools/SongLibrary/lyric_sync.py`)

For recordings whose MIDI has no lyric timing:

```powershell
Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/lyric_sync.py PreparedSongs/Library/my-song
```

- **Beats and downbeats.** beat_this (`final0`, cached under `SongLibraryData/models/torch`) on
  the recording.
- **Words.** The sheet's syllables are aligned in order to the vocal stem's syllable onsets
  (spectral-flux attacks, pitch steps, energy dips) by dynamic programming.
  - Onsets inside vibrato are the vibrato, not new syllables.
  - The costs cover:
    - unused onsets, by strength;
    - syllables closer than 0.1 s;
    - long gaps inside a line;
    - a sung line starting without its last note held or a breath;
    - sung syllables on onsets without a steady pitch after them;
    - onsets off the eighth-note grid (strict for rap);
    - rapped gaps that do not fit one syllable per eighth.
- **Forced alignment.** `--aligner mms` uses torchaudio's MMS forced aligner instead. Its weights
  (about 1.2 GB) are fetched only with `--download-aligner`.
- **Vibrato.** pYIN pitch. A voiced stretch oscillating at 4–8.5 Hz, at least a tenth of a
  semitone around its moving average, is a vibrato span.

The Song Workshop runs this automatically before PatternPrep when a bundle has a `lyrics.txt`,
a `recording.wav` and no MIDI lyric events. Stems carry the master's lyrics.

## Verification

- **PatternPrep self-test** (`dotnet run --project Tools/PatternPrep -- --self-test`) covers the
  public-domain lyric fixture:
  - syllables, rhyme keys and the meter scan;
  - sync from events, and from notes alone (every sung syllable lands on the same note);
  - AABBAA trochaic tetrameter (Twinkle); trochaic Hiawatha with its anaphora and alliteration;
    ABABCC iambic pentameter (Sonnet 18);
  - the "+1 syllable · trochaic → iambic" matchup;
  - vibrato on held notes, and the sonnet's held line ends.

  `--write-fixture lyrics out.mid` writes the song with its `lyrics.txt` and `song.json`.
- **Audio sync test** (`Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/test_lyric_sync.py`)
  renders the fixture and syncs from audio. On this run:
  - beats sit on the 100-bpm grid and downbeats come every 2.4 s;
  - sung words are all within 100 ms; spoken words 89% within 100 ms (median 18 ms);
  - vibrato is found on the held notes;
  - PatternPrep, with the lyric events stripped, lands at least 95% of sung syllables on their notes.
- **Unity** (Tools → Resonance → Check lyric mode and instrument changers, in Play Mode):
  - the changers show variations and repeats;
  - lyric mode lights the sung syllable, with vibrato, and follows the rhyme board;
  - trochees drop into wells and are bumped over the saw;
  - a held sonnet syllable floats at the comb, then dives;
  - the strip and panel do not overlap.

  Screenshots go to `Temp/ResonanceChecks`.

## Test material

The fixture is an original arrangement of public-domain texts and tunes:

- Jane Taylor's "The Star" (1806), sung to the traditional "Ah! vous dirai-je, maman";
- the opening of "Hiawatha's Childhood" (Longfellow, 1855);
- Shakespeare's Sonnet 18, lines 1–4 and 13–14.

The rendered audio uses a formant voice and the offline Windows speech synthesizer. No
proprietary recordings or lyrics are used in tests.

To try lyric mode, build the fixture into the prepared library:

```powershell
Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/add_lyric_fixture.py
```

Then select **lyric-fixture-public-domain · recording** in Unity's Prepared library, load it, and choose **View → Lyrics**.
