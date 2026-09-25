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
2. **The vocal's notes.** Sung syllables are laid on the notes in order by dynamic programming:
   - one syllable per note, a held syllable taking several (cheapest on a stressed or `_`
     syllable);
   - a note may go unsung;
   - lines end where the melody breathes.

   The vocal lane is the lead vocal, else the lane the lyric events sit on. MIDI vocal notes
   aligned to the recording are an exact clock. Laying syllables on them by phrase and breath
   proved more reliable than syllable onsets heard in a stem. In Fireflies, the stem's onsets put
   the first line 24 beats before the vocal enters.
3. **Audio alignment**, from `lyrics.timing.json` (written by `Tools/SongLibrary/lyric_sync.py`),
   for what remains: spoken lines, and sung lines when the vocal has no notes. The aligner's
   per-syllable times are used.
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
| Vocal wheel | panel, left | The vocal changer's top disc facing the viewer, turning once per loop under the comb. The melody is a curve through the notes (radius is pitch): an arc per held note, then a bend to the next. Only the stretch around the comb is drawn: the line just sung and the next to come. Sung syllables sit on the curve at their notes, always upright. The one being sung is lit, with a glow that grows with its emphasis. Under vibrato its letters bob at the vibrato's rate and depth, staying upright. |
| Word graph | panel, right | The song's stanzas as tabs, the one being heard lit. Below them, that stanza's lines as small graphs of their words. Each word is a node on the line's beat grid, raised by its pitch (sung) or its stress (spoken). The word being heard is lit, and the end-rhyme letter sits at each line's end. |
| Drum rack | scene, a strip above the panel | A sawtooth rack meshes with the drum disc at twelve o'clock and slides at the rim's speed, so each syllable reaches the comb when it is spoken. The saw is cut by the drum hits themselves. Every hit, or group struck together, is a cliff whose height is its weight (kick tallest, then snare, toms, cymbals, hats) and velocity, with a ramp rising to it over at most the beat before. The disc's rim is a ratchet cut by the same hits in its bar. The line and its meter are written above the rack. |

On the rack:

- A syllable **on a beat** waits on the crest, then drops with gravity. It lands in its well
  exactly on its beat and stays low: an impact flash and a short squash, no bounce.
- A syllable **off the beat** rides the saw and is bumped up over it as it is spoken.
- A **held** syllable (three or more eighths) lands the same way, then floats in a bezier arc
  above the teeth while it lasts. It stays at the comb as the rack runs under it, then dives
  into the well where it ends.
- Emphasis blooms as each syllable lands; stressed syllables are brighter at rest.

Trochees read as well, ramp, well, ramp; iambs as ramp, well.

The rhyme, front-rhyme and meter analysis above stays in the bundle, as does the matchup of
lines between stanzas. The display keeps only the rhyme letters and each stanza's scheme and
meter.

**Layout.** Lyric mode stacks the screen: the drum strip above, running as wide as the window,
and the panel below. While a spoken stanza is heard the resting vocal wheel steps back and the
word graph widens. The torus steps back throughout.

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
  painter text instead of per-letter UI labels. The rack's text meshes are rebuilt only when a
  slot's syllable changes.

Measured in the editor at 2560 by 1256:

| State | Before | After |
|---|---|---|
| Bridge V/V tension | 8 fps, 300+ ms frames | 44 to 48 fps, p95 25 to 32 ms |
| Lyric mode, sung | 15 fps | 61 to 67 fps |
| Lyric mode, rap | 58 fps | 93 to 97 fps |

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
- **Forced alignment.** `--aligner mms` uses torchaudio's MMS forced aligner instead. Its weights
  (about 1.2 GB) are fetched only with `--download-aligner`.
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
