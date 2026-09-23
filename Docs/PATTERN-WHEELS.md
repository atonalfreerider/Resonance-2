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
regenerated.

## Display: rack, pinion, planets and moons

| Gear | Cycle | Shows |
|---|---|---|
| Rack | the song, unrolled | one tooth per bar; section strips in family hatch; group brackets; drag to seek |
| Pinion ring | one turn per song | every visit in its family hatch; now meshes with the rack at nine o'clock |
| Planet (centre) | one turn per visit | this visit's chords, pass ticks, changed bars outlined, lead-line onset comb |
| Moon | one turn per loop | the fundamental rolling inside the planet; its size is the pass count |
| Parked planets | — | every other family's fundamental, with one dot per visit |
| Drum disc | one turn per bar | the groove family; fundamental ghosts and ringed variation strikes |

- **Variations.** Where the moon touches the chord band, the fundamental meets the actual
  visit. A white spark means they differ.
- **Through-composition.** Families that return are drawn with solid outlines. Material
  heard once is dashed. When a group plays, every occurrence's bracket lights up, so the
  verse + chorus pair is seen coming back.

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
- **The drum wheel speaks the same language.** Drums carry no tonality, so it is all teal
  ink. Each groove disc wears the hatch of the section family it mostly plays in, with that
  section's short name at the hub. The groove's first bar is its fundamental.
- One generator (`FormHatch`) draws the ring sectors, rack strips, side-panel swatches and
  the drum-disc rim meshes.

## Verification

- `dotnet run --project Tools/PatternPrep -- --self-test` checks:
  - the pattern compression fixtures: new ending, extra pass, lead-in, transposed return,
    groups and grammar;
  - repetition-first structure on pop, varied verse–chorus and A A B B A C C strain fixtures.
- `dotnet run --project Tools/PatternPrep -- --sweep "<midi>|Tools/PatternPrep/reference-forms/maple-leaf-rag.json"`
  compares boundaries with reviewed forms. The Maple Leaf Rag MIDI is exported from the
  music21 corpus, public domain.
- **Tools → Resonance → Check pattern wheels (generated song)**, in Play Mode:
  1. writes and prepares an original generated song;
  2. visits every section, checking the planet, the group and rack/ring coupling;
  3. checks that the drum discs wear the matching section hatch.
