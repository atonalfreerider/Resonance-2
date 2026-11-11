# Pattern wheels: review and design

Most music, pop and classical, is built from repeating patterns. A chord loop repeats
inside a section, sections return, and groups of sections (verse + chorus, a classical
part) come back. Around the returns sits material heard only once. The pattern wheels show
that structure as a rack and pinion driving a planetary gear train. Each gear turns once
per cycle of its level, so gear ratios are repetition counts.

## Review of the previous implementation

The metaphor was right, but the compression under it and the drawing on top of it did not
carry it.

**Compression**

- Each section visit detected its own loop independently. One family could get a 48-beat
  loop on one visit and a 4-beat vamp on the next, so the family had no single fundamental,
  and variations were never identified.
- Segmentation looked for novelty first and repetition second. Many short segments could
  always find a harmonic match somewhere. A copy adjacent to itself counted as nothing, so
  repeated strains (A A B B) and returns whose boundaries were shifted by a bar were
  missed. *Maple Leaf Rag* came out as 13 sections in 12 families, with no returns found.
- Verse + chorus groups were a carrier node without a name, visit numbers or grammar.

**Display**

- The rack scale was arbitrary (75 s per rack height), so the pinion's turn meant nothing.
- Every family wheel was the same size, and there were no moons, so sub-cycle repetition
  was invisible.
- The centre wheel was mostly per-instrument rhythm dots; the progression was a thin ring.
- Colour carried several meanings at once. Family identity and chord function competed.

## Offline: pattern compression (PatternPrep)

1. **Repetition first** (`FormStructure.cs`)
   - *Thumbnails.* The most compressive repeated stretch is found first: explained bars,
     minus the cost of describing it once and of each visit, over every phrase-grid length
     and all 12 transpositions. A copy must match window by window, not only on average.
   - *Cutting long blocks.* A long block is cut where a part of it returns on its own, such
     as a final chorus without its verse. The cut edges must match bar by bar. Otherwise it
     is cut at changes shared by every copy.
   - *Through-composed stretches.* Bars no thumbnail explains are divided by the older
     novelty dynamic programme, now with rewards at the edges of returns.
   - *Clean-up.* Adjacent passes of a short family merge into one section, which is then a
     loop played twice. Separately found families whose leading visits align are linked.
2. **Fundamentals and variations** (`FormPatterns.cs`)
   - Each family's fundamental is its shortest chord loop that every visit repeats. It is a
     per-position consensus, so a varied first pass does not become the reference.
   - Every visit is saved as passes of the loop, each with:
     - its offset (a lead-in starts late) and transposition;
     - whether it is partial (a tag);
     - the loop-relative beats whose harmony changed.
   - Variations are named against the family's reference visit: "new ending",
     "3 passes (usually 2)", "1-bar lead-in", "transposed +2", "varied bar 5".
3. **Groups and grammar**
   - Recurring runs of families become groups.
   - The song is written as a compressed grammar: `In (V C) (V′ C) Br (V C′) Out`. Primes
     mark varied returns and `×n` folds repeats.

Bundle version 3 adds `Patterns`, `Groups`, `FormGrammar`, `SongBars`/`FundamentalBars`,
and per-section `Passes`, `Variation`, `Group`/`GroupVisit` and `Short`. Older bundles still
load: each family's first visit is projected as its fundamental until the bundle is
regenerated. Version 4 adds `KeyChanges` and `Tensions` (see below); older bundles read their
key changes off the key frames and have no tensions until regenerated.

## Display: rack, pinion, orbits, planets and moons

| Gear | Cycle | Shows |
|---|---|---|
| Rack | the song, unrolled | one tooth per bar; section strips in family hatch; group brackets; key-change diamonds (`→ D major`); drag to seek |
| Pinion ring | one turn per song | every visit in its family hatch, with key changes named outside the rim; now meshes with the rack at nine o'clock |
| Orbits | one turn per group cycle | each recurring group (verse + chorus, A + B) on its own carrier ring; the playing family passes the gate at nine o'clock |
| Planet (centre) | one turn per visit | this visit's chords, pass ticks, changed bars outlined, lead-line onset comb |
| Moon | one turn per loop | the fundamental rolling inside the planet; its size is the pass count |
| Drum disc | one turn per bar | the groove as a music-box plate; fundamental ghosts and ringed variation strikes |

- **Orbits.** Up to two recurring groups each get an orbit. Their families are fixed on a
  carrier ring that turns once per group cycle, and every other family (intro, bridge, an
  interrupting C, outro) rides an outer orbit.
  - A B A B C A B C: the A/B carrier turns through A B A B.
  - When C interrupts, it swings to the gate on its own orbit while the A/B orbit holds its
    phase, drawn dashed.
  - The A/B orbit resumes from exactly that phase.
  - Carrier angles are computed from the song position, so seeking is exact.
- **Variations.** Where the moon touches the chord band, the fundamental meets the actual
  visit. A white spark means they differ.
- **Through-composition.** Families that return are drawn with solid outlines. Material
  heard once is dashed. When a group plays, every occurrence's bracket lights up.

## Instrument patterns and changers

The section families describe the song as a whole. The instrument patterns break it down
lane by lane (`InstrumentPatterns`, bundle version 5 `Parts`).

**Offline**

1. **Chords per lane.**
   - A lane's own pitch classes are read on the notated beat grid, with the song's harmony as
     context.
   - A single line (one or two pitch classes in a step) plays the song's chord, so its passing
     tones do not make chords of their own.
   - Chordal steps are decoded like the song timeline. A pad holding C through C–Am–F–G stays C.
2. **Windows.**
   - The song's loop passes cut each lane into windows, so its patterns turn with the song's
     loops.
   - A window whose chords repeat inside it is cut shorter: a one-chord vamp over a four-bar
     pass is a one-bar loop played four times.
3. **Patterns.**
   - By definition, a window that repeats the same chords is the same pattern. A transposed copy
     is too, when it moves between chords.
   - A window that changes one or more chords but keeps at least half is a variation (′, ″).
     Each distinct set of changed chords is its own variation.
   - The fundamental is the per-step consensus of the pattern's plays.
4. **Runs and grammar.**
   - Consecutive plays of one pattern are a run (repeat n of m).
   - Each lane is written as a grammar, for example keys `A B C×2 B C×2 C′ B′ D×2` and vocal
     `· A · A · A′ ·`, where `·` is a rest.
   - The bundle records each lane's bars against the bars of its fundamentals.

**Display: CD changers**

- **The stack.** Every pitched lane is a changer in the pattern-wheel panel, in the drum wheel's
  design. Its patterns are discs in a stack seen in perspective.
- **The top disc.** The pattern playing rises to the top and turns once per loop under a comb at
  twelve o'clock. It shows:
  - its chord band (colour is tonality);
  - changed chords outlined, with a white spark at the comb as they pass;
  - the lane's notes as dimples: radius is pitch, played dimples lit, the sounding one flashing.
    The vocal's disc also draws its melody as a curve.
- **The hub** carries the pattern with its primes (A, B′, C+2) and the repeat count (2/4): cyclic
  repetition.
- **Waiting discs** show their letters on the edge. When the lane moves to another pattern, its
  disc slides up and the old one sinks. When the lane rests, no disc is on top.
- **Below the stack**: the lane's name, its compression (`60 bars → 19`) and its grammar around
  the current token, which is lit and underlined.

The changers take a bay along the bottom of the panel. The ring shrinks to make room.
[Lyric mode](LYRIC-MODE.md) turns the vocal changer to face the viewer and lays the sung
syllables on its melody.

## Motif reduction and the structure wheel

The section families above describe a song by its chord loops. The motif reduction describes a
piece by its melody and texture: a small set of motifs (cell shapes stored once), each use of one
placed by a transposition, a mirror or the parallel mode, and the larger units built from them.
The full specification is [MOTIF-REDUCTION.md](MOTIF-REDUCTION.md).

**Offline (PatternPrep, bundle version 6 `Motifs`)**

1. **Ingest.** Notes in ticks, the bar and tactus grid, spelling on the line of fifths, and
   streams: score voices (from a MuseScore import), each lane's outer line and rest, hand
   textures and drum groups. The melody's stream is the principal stream.
2. **Reduction and cells.** Each line is reduced Schenker-fashion (neighbours, passing notes,
   turns, appoggiaturas, repeats), which finds its arrivals (cadences). A lexicon segments every
   stream into cells; a cell that restates another under a transposition or a mirror joins its
   paradigm, and a near copy joins it with named edits (the white sparks). Paradigms with one
   diminution signature form an atom (N neighbour, T turn, P passing or sigh, …), whose upper
   and lower forms are its chirality.
3. **Iteration.** Units repeated in a row are powers (`X² ↑1`, nestable: `(U²)² ↑1`); texture
   runs, cycles, doubling, hand alternation and liquidation are relations of their own.
4. **Returns and sections.** A self-similarity matrix finds what returns; a return is an edit
   script (match, delete, insert, substitute), so an abridged reprise is one relation:
   `A′ = A − (a2 a1′) + Coda`.
5. **Hierarchy.** Sections, then phrases (cut at arrivals) or periods, then sub-phrases and
   units. A copy inherits its source's children; what the reprise leaves out is its `Omits`.
6. **Code.** The whole description is priced in bits against literal and entropy baselines,
   checked to decode back to every note (Verify), and written as a formula and a caption:
   `A · B · A′ (A′ = A − bars 6–13 + coda) · 707 notes → 57 cells in 14 atoms · … · 4.8× smaller`.

A Verify failure leaves the bundle a valid version-6 bundle with an empty layer (`Schema 0`,
the reason in `Status`), so the wheel stays classic; `--strict-motifs` makes it an error.
Version-5 bundles still load and keep the classic wheels.

**Display: the structure wheel**

For a bundle with motifs, and with **Structure wheel (motifs)** on in the deck's panel (the
default, kept per viewer), the wheel draws the reduction instead of orbits, planet and moon.
The rack, the ring and the instrument changers keep their places, so the layout around the
torus does not change.

| Part | Turns | Shows |
|---|---|---|
| Rack | with the song | level-1 strips (a reprise in its source's hatch), a tick where each phrase begins, return brackets on its left edge |
| Ring 1 | once per song | the sections; `A′ ← A` names a reprise; the bars it leaves out are a **slit** in A's arc, bridged by a chord, and A′'s arc is notched where it jumps them; return brackets inside the rim |
| Ring 2 | once per section | the phrases or periods of the section playing, hatched by identity (a1′ and a projected a1 in a1's hatch); a period's generator sign at its middle: → translation, ⇄ involution, · identity, a polygon for a cycle |
| Ring 3 | once per phrase | the units of the phrase playing: powers as staircases (each iteration stepped out by its cumulative step, 3 px a step, in when it falls), brackets over powers and sub-phrases (`×2 ↑2`, `a3b`), the stream's cells where no unit is, a small motif on each arc, the phrase's chords along the inner edge |
| Centre | per cell | the playing cell's paradigm on a **pitch clock** |
| Caption | when the path changes | the piece's formula, the phrase's line (`a1 = ⌐ · γ⁻ · γ⁺² ↑1 · …`), the playing cell (`N⁺ upper neighbour γ⁺`, then how it relates to its last statement), the compression |

- **The gate.** As on the classic ring, now is at nine o'clock: on each ring the arc under it
  is the one playing, lit with the bloom, and a thin radial line joins the lit arcs to the
  glyph. The gate never moves; the rings turn.
- **The pitch clock.** Angle is the scale step (seven positions, or twelve semitones for a real
  or chromatic figure), time spirals outward from a quarter of the radius, a step moves along
  the clock and a leap is a chord. The tonic is at twelve o'clock. Each statement is the
  prototype turned so its first note sits on its scale degree, so a sequence up a step turns
  the glyph by a seventh of a turn: rotation is transposition. An inversion is the same glyph
  mirrored, never a rotation. A playhead bead travels the spiral; white sparks sit at the notes
  a statement changes, an altered note a quarter tone off its degree (the coda's ♭6 below the
  sixth). A cycling run turns its hand shape by its step at each event (a tritone's diameter
  closes after six). `8va` and `8vb` mark a statement an octave away.
- **The skip.** While a reprise plays, a ghost cursor runs on its source's arc where the music
  is in the source. When the reprise jumps the bars it leaves out, the ghost leaps across the
  slit's bridge, the slit flashes and "skipped" appears. The slit is lit from the reprise's
  start until the music it rejoins has been heard, so it is there before, during and after the
  jump.
- **Clicks.** The radius picks the ring and the angle the arc. A click seeks to an arc's start;
  on the slit, to the bars left out; on the ghost, to where it is in the source ("hear the
  original"); on the glyph, to the playing motif's next statement. Hovering names the arc
  (`phrase a1′, a1 again · 9.1 · γ⁺² ↑1 · …`) or the slit (`bars 6–13 (a2, a1′) are left out
  in A′`).
- **Chapters.** The side panel lists the sections and their phrases or periods, hatched as on
  the rings.
- **Director anchors.** A tour cue with `annotationTarget: "patterns"` can set
  `annotationAnchor`: `section:A′`, `phrase:a3`, `node:A′/a1`, `omission:A′`, `run:B1`,
  `atom:N`, `relation:3`, `ghost` or `glyph`. The story arrow follows that part of the wheel as
  it turns. An anchor that does not resolve (a classic wheel, a part not shown now) points into
  the centre as before.
- **Cost.** Rings 2 and 3 and the glyph are retained elements: they are redrawn only when the
  section, phrase or paradigm changes, and otherwise only turn (`style.rotate`). Their labels
  ride them and are turned back upright. A small live layer draws the lit edges, the playhead,
  the sparks and the gear each frame. The rack and ring 1 stay in the immediate draw; orbits and
  planet are skipped. Strings and geometry are made when the bundle loads, so the structure code
  allocates nothing per frame. Its profiler marker is `Resonance.PatternWheels.Structure`
  (budget 0.3 ms a frame). Arcs under 3 px collapse to a tick, and labels need 24 px.

`MotifWheelModel` (the layer indexed for drawing: the path at a beat, the ghost, the glyph
geometry, ring 3's items, the anchors) is shared with the Quest wheel.

## Keys: changes and tensions

The pipeline (`KeyAnalysis`) detects key changes from the chord timeline:

- **Viterbi pass.** It runs over 24 key states. Diatonic chords fit their key for free; the
  tonic chord is rewarded, most when a cadence (V, vii° or IV) prepares it. Tonicizations fit
  at a small cost, smaller still when they resolve where they point, and the chord they
  resolve to is then read as prepared:
  - secondary dominants (V/V) and leading-tone chords (vii°/V), resolving to their target;
  - the Neapolitan ♭II, falling to V or I;
  - chords borrowed from the parallel minor, resolving to I or V.

  So vi–V/V–V–I loops and ♭II–V–I vamps are the home key's own colour however often they
  turn: a tension every time, never a key change. Anything else is expensive, and a major
  seventh on the dominant degree counts as chromatic (E♭maj7 is I in E♭, not V in A♭).
- **Modulations.** A change must be worth more than a few bars of chromatic colour, and the
  new key must then hold for eight bars with its own cadences: a four-bar excursion is a lean.
  The song's own key is the default: a bridge on the dominant (IV V iii vi V/V V ♭VII V) reads
  in the home key unless the new key is clearly better for as long as it lasts. The opening key
  is never merged away. A key change lands on the new tonic, and the dominant
  just before it is the tension that leads there.
- **Output.** `KeyChanges` (beat, from, to, evidence such as "D major held 10 bars · Chorus 3
  transposed +2") and `Tensions`. Each tension carries its span, target key, label
  (V/V, ♭II Neapolitan…), an amount (slight for a passing tonicization, twice that for the
  one that leads into a real change) and whether a real key change completes it.
- **Signatures.** A reviewed key stays locked. Several authored key signatures are kept.
  One opening signature is only where detection starts.

**Chord quality.** The chord timeline (`ChordTimeline`) names each beat's chord from the
notes sounding in it. The bass names the root (C–E–G–A with C in the bass is C6, not Am7 over
its third), a seventh must be heard to be named, and a note's release tail lasts at most half a beat at a quarter weight, so the third of one
chord no longer colours the next (F's F♮ used to turn the D of a IV–V/V into D minor). A chord
with no third (a bare fifth, a suspension) is not snapped to the key's quality.

**The torus.** Only a settled key change moves the torus: one whose new key holds for sixteen
bars, or eight bars to the end of the song. Then it twists fully into the new key, and a
**height scale** stands through the old key's label along the y axis: a thin rule with the
chromatic scale on it, a tick and a note name per semitone, faint, most present where it
meets the torus's edge and fading quickly above and below. It stands where the label was as
the change began and never turns with the torus; it slides as the torus turns, by the
semitones between the keys the shorter way, so the new key's tick (the longer one) arrives at
the edge as the turn completes, then fades. Tonicizations (V/V, the Neapolitan, borrowed
chords) and shorter key regions never move the torus; the caption alone names them
(`C major · V/V → G major`), and the rack and the ring mark every detected key change.

## Section boundaries

Repetition-based segmentation (`FormStructure`) finds repeated blocks first. Their edges are
then checked against where the music changes: a block whose copies would all match a bar
earlier or later, where the novelty at both edges is higher, slides there together (a pickup
or fill bar that differs between copies no longer pushes a chorus a bar late). A bar or two
between blocks joins the section it changes into (a lead-in) or out of (a tag), by the
novelty at its two ends. Reviewed labels "Chorus 1" and "Chorus 2" name one family. A MIDI
marker within a beat of the next bar line belongs to that bar. An outro is only the run of
one-off sections after the last return of a repeated family.

## Torus labels

Every pitch class is labelled by what it is to the key: `C  Key`, `F  4th` (red) and `G  5th`
(green) for the chords the colours are built on, and the others by interval: `D♭ Neapolitan`,
`D  2nd maj dom` (the second, which acts as V/V), `E♭ min 3rd`, `E  maj 3rd`, `G♭ tritone`,
`A♭ min 6th`, `A  maj 6th`, `B♭ min 7th`, `B  maj 7th`. The key, 4th and 5th keep their colours
and sizes; the other labels are small and white until their note sounds, when they take the
note's tonal colour and grow with its loudness.

## Tutorial

The song list's **Tutorial** button plays a narrated tour of the surface: the twelve tones as a
circle, the four augmented-triad triangles that cover them, the triangles spinning a third of
a turn per revolution into a three-sided band (the umbilic torus), the key at the top with
every tone named by its relation, why the colours are what they are (I blue, IV red, V green,
the rest blended by position), what position means (fifths along the edge, thirds across the
band), a key change as a twist measured on its scale, the circle of fifths compared, and the
shape's provenance (Zeeman's umbilic bracelet, 1976; the model credited to Dimitrios Cholidis).
The caption shows the sentence being spoken at three times the old size, the word being said
lit with a bloom (its times from `Tools/SongLibrary/tutorial_timing.py`, which force-aligns each
step's WAV with the MMS aligner lyric sync uses); the caption is semi-transparent. The diagram's
points and triangles flare in with glow halos, and lit tones are struck so the torus's bloom
catches them. During the band's rotation the edge the triangle's corners sweep is traced as a
shimmering trail, gold arcs circle the band's cross-section the way it turns, and coloured
sparks flash on the torus. In a portrait window (or with Vertical on) the diagram stands above
the torus, which fills the top two thirds at 1.6 times its usual size, and the caption keeps the
last third; the torus's frame depends only on the window, never on the caption's height.
The script (`Assets/StreamingAssets/Tutorial/script.json`) drives a diagram overlay, the torus
(lit degrees and chords, a key change up a fifth and back) and a slow orbit; the narration
beside it (one WAV per step, `manifest.json` with durations) is made by
`Tools/SongLibrary/tutorial_narration.py` with Cartesia, in a male British voice picked from
the voice library (`--voice` to choose another). Without the WAVs the tour still runs, timed by
the text.

## Layout and depth

- **Intro and song list.** The app opens on a song selector listing the fully prepared songs of
  `PreparedSongs/Library` (an aligned score, its recording manifest and a current pattern
  bundle). Choosing one loads it; the list then docks at the right edge behind its own tuck
  button. The side menu at the left starts tucked.
- **Overview.** The pattern wheels, the lyric strip and the 3D scene share the screen without
  overlapping: wheels, strip and torus side by side in a landscape window; the torus above, the
  strip between and the wheels below in a portrait one. The scene camera renders only its part;
  a background camera clears the whole screen through the same post-processing, so no part ends
  at a hard edge.
- **Occlusion.** The torus hides what lies behind it: a depth-only copy of its surface is
  drawn after its own glow and before the drum wheel. The drum plate is dark and sits low
  enough that the torus covers only its far edge, and the struck dimples stay in view.
- **Drum wheel.** A music-box disc: a steel plate with a dimple wherever the groove
  strikes, turning under a comb at twelve o'clock. Radial lines mark the beats (brightest
  on the downbeat) and shorter, fainter lines mark the upbeats. Each drum voice has an
  engraved track, and the rim is riveted with drive holes.

## Design language

- **Colour is tonality.** Only chord bands and tonal glows use colour: I blue, IV red,
  V green, and so on.
- **Form is texture.** Each section family has one hatch, drawn in a light, muted teal:
  - verse: diagonal;
  - chorus and refrain: cross-hatch;
  - bridge: dots;
  - pre-chorus: rings;
  - intro, outro and coda: radial ticks;
  - links and solos: zigzag;
  - classical letters take the patterns in order.
- **Text** is soft white. **White sparks** mean "differs from the fundamental".
- **The drum wheel shares the ink.** Drums carry no tonality, so it is all teal and no
  hatching. The hub names the section family its groove mostly plays in, and the groove's
  first bar is its fundamental.
- One generator (`FormHatch`) draws the ring sectors, rack strips, planet edges and the
  side-panel swatches. The hatch sits in a thin band at the edge of each disc.
- **The instrument changers and lyrics share it too.** Chord bands carry colour; stacks,
  stress marks, brackets and the drum rack are teal; the syllable being sung is white.

## Verification

- `dotnet run --project Tools/PatternPrep -- --self-test` checks:
  - the pattern compression fixtures: new ending, extra pass, lead-in, transposed return,
    groups and grammar;
  - repetition-first structure on pop, varied verse–chorus and A A B B A C C strain fixtures;
  - the A B A B C A B C alternating pair;
  - key analysis: V/V and Neapolitan tensions relax, vi–V/V–V–I loops and ♭II vamps never
    change the key, a four-bar excursion is a lean, a sustained new key with cadences changes
    and comes back, a dominant pivot completes a modulation, and a lifted chorus changes key;
  - instrument patterns and lyrics on the public-domain lyric fixture: each lane's grammar,
    runs and changed spans, and the lyric sync, meter and rhyme (see
    [LYRIC-MODE.md](LYRIC-MODE.md)).
  - the motif reduction's fixtures (F1–F28 of [MOTIF-REDUCTION.md](MOTIF-REDUCTION.md) §16), each
    decoded back to every note and run twice for a byte-identical layer; `--self-test-motifs`
    runs only these.
- `dotnet run --project Tools/PatternPrep -- --sweep "<midi>|Tools/PatternPrep/reference-forms/maple-leaf-rag.json"`
  compares boundaries with reviewed forms. The Maple Leaf Rag MIDI is exported from the
  music21 corpus, public domain.
- `dotnet run --project Tools/PatternPrep -- --sweep-motifs Tools/PatternPrep/reference-forms/*.motifs.json`
  checks the motif reduction of library pieces (the étude, the Nocturne, Ticket to Ride,
  Umbrella) against their golden files.
- **Tools → Resonance → Check pattern wheels (generated song)**, in Play Mode:
  1. writes and prepares an original generated song;
  2. visits every section, checking the planet, the group and rack/ring coupling;
  3. checks the C → D key change, and the V/V lean and relaxation of the torus;
  4. checks the drum discs' section and beat lines;
  5. checks that the wheels and the 3D viewport do not overlap.
- **Tools → Resonance → Check motif wheel (generated ternary fixture)**, in Play Mode, writes and
  prepares PatternPrep's `motif-ternary` fixture (the étude's principal line and bass for A and
  A′, public domain, around a synthetic B) and checks:
  1. that the library's version-5 bundles are still listed, and that one draws the classic wheel
     with the toggle on;
  2. that the fixture is version 6 with motifs, and that at 12 beats the lit arcs of rings 1–3
     are the model's path, with the rings only advancing while it plays;
  3. that a statement a scale step above its prototype is drawn turned 360/7°, an inversion
     mirrored, and the coda's altered note as a spark;
  4. that the ghost maps A′ into A and leaps the omitted bars once, the slit lit before and
     after the jump;
  5. that `omission:A′` resolves into the slit and a cue with it points the story arrow there;
  6. that a click on a ring's arc seeks to its start;
  7. that the structure wheel's marker allocates nothing while it plays.
- **Tools → Resonance → Measure frame time** includes the structure wheel across the fixture's
  A′ return and reports its marker against the 0.3 ms budget, and its GC allocation.
