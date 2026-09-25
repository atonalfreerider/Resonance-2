# Lyric mode

Lyric mode (**View → Lyrics**) focuses on the words. A fast reader on the drum wheel takes most
of the screen: one lyric line joined to the drum wheel. The syllable being heard is centred, the
ones before build to the left as a fading trail, and each lands with a slanted white strike, down
on a beat and up off one. Below it, a lyric graph shows the words' own structure (stanzas, rhymes, refrains) and lights each word as
it is heard, beside a small vocal wheel carrying the melody. Lyrics are shown only in lyric
mode.

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
  beats when spoken.
- **Names.** A capitalised word inside a line is a name ("the buds of May") and is never a weak
  word.

## Sync (PatternPrep)

Timing comes from the best evidence available, in this order:

1. **MIDI lyric events** (karaoke `Lyric` meta events, or `@K` text events). Each event times
   one syllable; events and sheet syllables are aligned by their letters.
2. **A forced alignment guiding the vocal's notes.** When `lyrics.timing.json` comes from a
   phonetic forced aligner (MMS), its syllable times decide which note each sung syllable takes:
   the note assignment below runs with a cost for straying from the aligned time. The notes then
   give the exact time.
3. **The vocal's notes.** Sung syllables are laid on the notes in order by dynamic programming:
   - one syllable per note, a held syllable taking several (cheapest on a stressed or `_`
     syllable);
   - a note may go unsung;
   - lines end where the melody breathes.

   The vocal lane is the lead vocal, else the lane the lyric events sit on. MIDI vocal notes
   aligned to the recording are an exact clock. Laying syllables on them by phrase and breath
   proved more reliable than syllable onsets heard in a stem. In Fireflies, the stem's onsets put
   the first line 24 beats before the vocal enters. With the MMS aligner guiding the notes, all
   431 syllables of its 12 stanzas land in order on the vocal's notes.
4. **Audio alignment**, from `lyrics.timing.json` (written by `Tools/SongLibrary/lyric_sync.py`),
   for what remains: spoken lines, and sung lines when the vocal has no notes. The aligner's
   per-syllable times are used.
5. **Estimated placement.** Spoken lines with no timing are laid on the untaken bars: stresses on
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

**Links** join words for the lyric graph (each word by its first syllable):

- **end**: each line end to the next line end of its rhyme group in the stanza;
- **front**: each line opening to the next of its front group;
- **internal**: a word inside a line rhyming (perfect key) with another inside the same line, or
  with the end of its own or a neighbouring line; weak words never count;
- **repeat**: a line heard again (the same letters, over six of them) to its last hearing.

In the fixture: 15 end, 9 front, 1 internal and 6 repeat links.

**Matchups** compare the same line of two stanzas:

- stanzas sung to one pattern (the same section family): verse 1 with verse 2, verse 2 with
  verse 3;
- consecutive spoken stanzas.

The score weights beat alignment 0.6 and stress alignment 0.4. The note names what differs:
"+1 syllable · trochaic tetrameter → iambic tetrameter", or "same beats, 2 stresses move".

## Display

| Part | Where | Shows |
|---|---|---|
| Reader | scene, on the drum wheel, most of the screen | One lyric line runs across the top of the strip, just above the drum disc. A stem joins it to the disc's comb at twelve o'clock (where the dimples are struck) and flashes with every drum hit, brightest for the kick. The syllable being heard is always centred, with its optimal recognition point (the letter a fast reader fixes on: the second of 2 to 5 letters, the third of 6 to 9, and so on) on the stem. It is set bold, upper case, in the colour of the chord of the moment. The syllables before it build to the left as a trail that dims and fades with distance, and each new syllable slides in along a diagonal (a beat from the upper left, an off-beat from the lower right) as the trail makes room. The slide takes the last 40 to 100 ms before its onset, and the syllable lands exactly on it with a small bloom. As it lands, a slanted strike slices across it: down (\\) on a beat, up (/) off one. The slice cuts in within 25 ms, flares white and widens, then blooms out in about a quarter of a second as its tail chases its head off the end. It is brighter and wider when a drum hit lands with it. The next drum hit comes in from the right at the rim's speed as a translucent steep sawtooth tooth, taller for a heavier hit, whose cliff reaches the stem as it is struck. A held syllable trails a bar that runs out with it and trembles under vibrato. |
| Lyric graph | panel, right | The words' structure, not the music's: no chord colours, no pitch. Each stanza is a column headed by its name and rhyme scheme, and each line a row of its words in reading order. End rhymes are arcs on the right from line end to line end, with the rhyme letter beyond. Front rhymes are arcs on the left, internal rhymes dip under the two words, and a line heard before is marked ↺ with how many times. The word being heard is lit and underlined, and the links of the line being heard glow. The columns slide to keep the stanza being heard second from the left. |
| Vocal wheel | panel, left, small | The vocal changer's top disc facing the viewer, turning once per loop under the comb. The melody is a curve through the notes (radius is pitch). Sung syllables sit on the curve at their notes, always upright. The one being sung is lit, and under vibrato its letters bob at the vibrato's rate. |

The rhyme, front-rhyme and meter analysis stays in the bundle, as does the matchup of lines
between stanzas. The graph shows the rhymes as links and each stanza's scheme.

**Layout.** Lyric mode stacks the screen. The reader and the drum wheel (framed whole, with the
lyric line above it) take a strip of about 62% of the height, running as wide as the window; the lyric graph and the vocal wheel take the
panel below. The torus steps back throughout.

**Highlighting an instrument.** Click an instrument changer in the pattern-wheel panel to make
its lane the highlighted instrument: white notes and comet trail on the torus, white dimples on
its changer, which is ringed in white and labelled HIGHLIGHT. The changer under the pointer gets
a faint ring. The selection is the same as the **Highlight instrument / channel** dropdown, which
follows it, and it is remembered per song.

## Performance

Lyric mode and the instrument changers run inside the same frame budget as the torus. That
budget is measured by **Tools > Resonance > Measure frame time**. The benchmark covers the
overview, lyric mode sung and rapped, a key change and a V/V lean, plus the first library song
with a lyric sheet. It reports frame times, every `Resonance.*` profiler marker, and what the
worst frame spent its time on.

The key-change and tension frames were the slow ones: the torus pose moves every frame there.
- **Chord aurora.** Its particles are laid on the surface by the vertex shader from a 17 by 17
  grid of surface points, so a moving pose uploads the grid instead of rebuilding
  40,000-vertex meshes.
- **Field and outline.** The field's coverage follows the pose's parameters and is refreshed,
  in slices over four frames, once the pose settles. The chord outline is rebuilt only when the
  chord, pose or camera changes.
- **Trail.** The melody trail keeps its routes per pose. Only its last second is recomputed
  while the pose moves.
- **Lyric displays.** They prepare everything per song at load. The sung syllables are
  painter text instead of per-letter UI labels. The reader's text meshes are rebuilt only when a
  slot's syllable changes; the shove, bloom and colour are transform and material changes. The
  lyric graph paints into its own element, repainted only when the lit word, the line, the
  stanza or the columns' scroll changes. Each frame only its glow is drawn (0.02 ms).

Measured in the editor at 2560 by 1256:

| State | Before | After |
|---|---|---|
| Bridge V/V tension | 8 fps, 300+ ms frames | 44 to 48 fps, p95 25 to 32 ms |
| Lyric mode, sung | 15 fps | 105 to 117 fps |
| Lyric mode, rap | 58 fps | 129 to 133 fps |
| Lyric mode, V/V tension | | 85 to 88 fps |
| Lyric mode, Fireflies | | 108 to 114 fps |

A single long frame just after a song loads remains; none of the profiled systems accounts
for it.

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
- **Intro and outro.** Onsets before the first syllable and after the last are cheap to leave
  unused, since stems carry intro and outro bleed.
- **Forced alignment.** `--aligner mms` uses torchaudio's MMS forced aligner (wav2vec2, CTC)
  instead. Its weights (about 1.2 GB) are fetched only with `--download-aligner`.
  - Emissions are computed in 30 s chunks with 1.5 s of overlap, so a whole song fits in memory,
    and the transcript is aligned against the joined emissions in one pass.
  - A wildcard token between lines absorbs what the sheet does not write (ad-libs, oohs).
  - Each syllable starts at its first letter's span, split by the sheet's hyphens (else an even
    share of letters).
  - On the fixture, 85% of spoken words land within 100 ms. The sung words cannot be scored
    there, since the synthetic voice sings "ah". PatternPrep then uses the aligned times to guide
    the sung syllables onto the vocal's notes.
- **Vibrato.** pYIN pitch. A voiced stretch oscillating at 4–8.5 Hz, at least a tenth of a
  semitone around its moving average, is a vibrato span.

The Song Workshop runs this automatically before PatternPrep when a bundle has a `lyrics.txt`,
a `recording.wav` and no MIDI lyric events. Stems carry the master's lyrics.

After editing a sheet, or when the sync improves, re-derive a bundle's lyrics:

```powershell
Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/relyric.py PreparedSongs/Library/my-song [--resync-audio]
```

It backs up what it replaces to `SongLibraryData/backups`, then recompiles the patterns. It
copies the master's lyrics, harmony and form into each stem, refreshes the stem hashes in the
manifest, and validates the bundle.

## Verification

- **PatternPrep self-test** (`dotnet run --project Tools/PatternPrep -- --self-test`) covers the
  public-domain lyric fixture:
  - syllables, rhyme keys and the meter scan;
  - sync from events, and from notes alone (every sung syllable lands on the same note);
  - AABBAA trochaic tetrameter (Twinkle); trochaic Hiawatha with its anaphora and alliteration;
    ABABCC iambic pentameter (Sonnet 18);
  - the "+1 syllable · trochaic → iambic" matchup;
  - the rhyme links (star–are, By the / By the, the refrain heard again);
  - vibrato on held notes, and the sonnet's held line ends.

  `--write-fixture lyrics out.mid` writes the song with its `lyrics.txt` and `song.json`.
- **Audio sync test** (`Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/test_lyric_sync.py`)
  renders the fixture and syncs from audio. On this run:
  - beats sit on the 100-bpm grid and downbeats come every 2.4 s;
  - sung words are all within 100 ms; spoken words 89% within 100 ms (median 18 ms);
  - vibrato is found on the held notes;
  - PatternPrep, with the lyric events stripped, lands at least 95% of sung syllables on their notes.
- **Unity** (Tools → Resonance → Check lyric mode and instrument changers, in Play Mode):
  - the changers show variations and repeats, and a click on one highlights its lane;
  - the reader's strip takes about two thirds of the height, above the panel without overlap;
  - the lyric graph lays out every stanza and its links, and lights the word being heard with
    its line's links glowing, following from Verse 1 into Rap 1;
  - the vocal wheel lights the sung syllable, with vibrato; the reader holds it with its bar;
  - a syllable lands with a small bloom into the chord's colour, and its white strike is gone 0.3 s after it lands;
  - 80 ms before their onsets, "Stood" slides in from the upper left and "the" from the lower right;
  - the downbeat "Stood" lands centred on the stem with a down strike, the off-beat "the" with
    an up strike, and "Stood" joins the trail to its left;
  - the next drum hit's tooth comes in from the right and reaches the stem as it is struck;
  - played in real time, each syllable is centred on its onset;
  - the stem from the comb flashes on a drum hit and fades before the next;
  - the held sonnet "day" stays on the line with its hold bar.

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
