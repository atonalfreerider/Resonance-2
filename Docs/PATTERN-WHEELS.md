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

## Keys: changes and tensions

The pipeline (`KeyAnalysis`) detects key changes from the chord timeline:

- **Viterbi pass.** It runs over 24 key states. Diatonic chords fit their key and tonic
  chords are rewarded. Tonicizations fit at a small cost:
  - secondary dominants (V/V) and leading-tone chords (vii°/V);
  - the Neapolitan ♭II;
  - chords borrowed from the parallel minor.

  Changing key costs more than a few chromatic beats.
- **Modulations.** A new key must hold for four bars. A key change lands on the new tonic,
  and the dominant just before it is the tension that leads there.
- **Output.** `KeyChanges` (beat, from, to, evidence such as "D major held 10 bars · Chorus 3
  transposed +2") and `Tensions`. Each tension carries its span, target key, label
  (V/V, ♭II Neapolitan…) and whether a real key change completes it.
- **Signatures.** A reviewed key stays locked. Several authored key signatures are kept.
  One opening signature is only where detection starts.

**The torus.** A key change twists the torus fully into the new key. Over a tonicization it
leans toward the key the chord points at, growing until the next chord, then relaxes back
into its key. If the key change to that key follows, the lean hands over to it and the twist
completes. The rack and the ring mark every key change, and the caption names the key and
any current tension (`C major · V/V → G major`).

## Layout and depth

- **Overview.** The screen is split so the wheels and the 3D scene never overlap: side by
  side in a landscape window, stacked in a portrait one (the scene takes the height the
  wheels do not need). The scene camera renders only its part; a background camera clears
  the whole screen.
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

## Verification

- `dotnet run --project Tools/PatternPrep -- --self-test` checks:
  - the pattern compression fixtures: new ending, extra pass, lead-in, transposed return,
    groups and grammar;
  - repetition-first structure on pop, varied verse–chorus and A A B B A C C strain fixtures;
  - the A B A B C A B C alternating pair;
  - key analysis: V/V and Neapolitan tensions relax, a dominant pivot completes a
    modulation, and a lifted chorus changes key.
- `dotnet run --project Tools/PatternPrep -- --sweep "<midi>|Tools/PatternPrep/reference-forms/maple-leaf-rag.json"`
  compares boundaries with reviewed forms. The Maple Leaf Rag MIDI is exported from the
  music21 corpus, public domain.
- **Tools → Resonance → Check pattern wheels (generated song)**, in Play Mode:
  1. writes and prepares an original generated song;
  2. visits every section, checking the planet, the group and rack/ring coupling;
  3. checks the C → D key change, and the V/V lean and relaxation of the torus;
  4. checks the drum discs' section and beat lines;
  5. checks that the wheels and the 3D viewport do not overlap.
