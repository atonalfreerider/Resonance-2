# Motif reduction (the "fractal formula") and the structure wheel: specification

> Design record, chosen by a panel of four independent designs scored by three judges. Paths under `scratchpad/` refer to the design-stage evidence scripts and proposals, which are not in the repository. Deviations made during implementation are listed at the end.

Status: final specification for implementation. It replaces the four design proposals
(`mdl-grammar.md`, `alignment-recursion.md`, `transformation-group.md`, `fractal-ifs.md` in this folder).

Scope:
- offline analysis in `Tools/PatternPrep` (C#);
- bundle contract version 6 in `Assets/PreparedPatternSong.cs`;
- the desktop pattern wheels (`Assets/PatternWheelDeck.cs`) and the Quest wheel (`Assets/Quest/PatternWheel3D.cs`);
- Director annotations (`SongDirector`, `StoryAnnotations`, `Tools/SongLibrary/story.py`);
- stem inheritance (`Tools/SongLibrary/common.py`).

Notation: `b.k` = printed bar b, beat k. The étude is in 2/4 and its pickup is bar 0. Bundle beats include the
étude's silent 4-beat lead-in, so printed bar b (b ≥ 1) starts at bundle beat `4.5 + 2(b−1)` and the pickup is
[4.0, 4.5). All acceptance tests are stated in bundle beats, with printed bars in brackets.

---

## 0. Decisions

### 0.1 What the user gets

The analysis describes a piece as a small set of **atoms** (cell shapes stored once). Each use of an atom carries:
- a **group element**: a tonal or real transposition, a mirror (the chirality bit), augmentation, or the
  parallel-mode change;
- a short **named residual**: the room for variation, drawn as white sparks.

These atoms build:
- **powers**: `X^n` stepped by g, the fractal primitive, nestable;
- **phrases and periods**, cut where the music arrives;
- **sections**, related by **returns with edits**, so an abridged reprise is one relation:
  `A′ = A − (a2 a1′) + Coda`.

A copy inherits the subtree of what it copies, so the same schema `x · g(x)` recurs at every level. The whole
description is priced in bits, verified to decode back to every note, and printed as a formula with a
compression line.

The pattern wheel draws this description:
- nested rings for section, phrase and unit;
- at the centre, the playing cell's atom on a **pitch clock**, where rotation is transposition and reflection is
  inversion, exactly;
- sparks where the music departs from the formula;
- a ghost cursor that jumps across the omitted bars;
- the formula as caption.

### 0.2 Base design and grafts

Judges' scores available to this step:

| design | musical truth | engineering | role in the final spec |
|---|---|---|---|
| transformation-group (TPA) | **8.5** | not in the excerpt | **base**: paradigms (exact orbits) found by Ruwet's criterion, group with key/chord frames and musicians' names, arrivals, generator labels, texture runs, cycles, doubling, liquidation, pitch-clock glyph |
| mdl-grammar | 7.5 | 6.5 | powers as token exponents (nested), bit accounting and baselines, the Verify gate, deletion right-normalisation and earliness, ghost cursor, determinism rules |
| alignment-recursion | 7 | **7** | adaptive similarity (quantile θ, gap costs tied to 1−θ), the scale rule (lag ≥ L/8), hull + anchored alignment, prototypes first, projection with `Omits`, beat-precise edges, one renderer-agnostic model for desktop and Quest |
| fractal-ifs | 5 | — | generator order on the pitch circle (gear polygons), cross-scale flag, canonical sources inside a return |

The display-communication scores did not reach this step. The display decision rests on the display judge's
measured test (`scratchpad/judge-display/glyph_check.py`, re-run here). With time on the angle, a rigid mirror of
the neighbour cell `F♯~ G♯ G♯ F♯` equals a 90° rotation (distance 0.000): chirality would read as
transposition. On the pitch clock (angle = scale step, radius = time) the same mirror stays 0.293 away from every
rotation. **The glyph is therefore the pitch clock.**

### 0.3 Fatal flaws removed

1. **A copy's extent never decides a phrase boundary** (fractal-ifs glued bar 6 to a1 and bar 14 to a1′).
   Phrases come only from arrivals and internal-return edges (§8.1). "Bar 14 = bar 6 with a new head" and
   "bar 16 = bar 6 a sixth higher" are recorded as cross-references, not boundaries.
2. **Atoms are not n-gram censuses** (fractal-ifs). An atom is a family of exact orbits found by segmentation
   (§5). Chirality has two levels: the cell's inversion bit, and the diminution sign of the family (upper/lower
   neighbour).
3. **No canonical-source composition across unrelated material** (fractal-ifs: "p′ 25.1 ← 5.2, elided
   6.1–21.1"). Canonicalisation runs only inside an accepted return, by gap sliding (§7.5).
4. **No Re-Pair** (mdl-grammar). Its binary nodes are not phrases. Units come from tandem blocks and powers (§6),
   phrases from arrivals.
5. **Texture novelty never splits a non-returning section** (alignment-recursion cut 14–15 | 16–17 and buried
   46). Periods come from generator labels with a hypermetric DP (§8.2).
6. **The headline reprise no longer rests on a 50-bit margin.** Gap sliding with an explicit tie-break (§7.5)
   always yields `A′ = A[1.1–20.1] − [6.1–13.2] + coda`, demonstrated below on the clean étude and six noisy
   transcriptions.
7. **No false identities.**
   - Contour invariance, an edit cap and an MDL gate guard every fuzzy join.
   - Fixtures assert that these pairs stay distinct: the left hand's chromatic dyads (38–41) and the
     retransition E–D♯–C♯ (58–59); the interlocking arpeggios (32/33) and the turn; the sigh and the neighbour.
   - Bar 1.1 (`E` 8th + `D♯ E` 16ths) is **not** claimed as a literal mirror of `F♯~ G♯ G♯ F♯`. It is the same
     family with the opposite sign.

### 0.4 Evidence produced by this step

Two scripts in `scratchpad/design/final/` checked the two decisions no single design had demonstrated. Both run
with the SongPrep venv, `-I`.

`align_check.py` runs stages E1–E5 of this spec on beat columns. Inputs: the clean étude import and the six
perturbed imports the engineering judge used (`judge-robust/noisyA–F.json`). Perturbation: σ = 24–48 ticks of
onset jitter, re-quantised to 16ths, 3–5% of notes dropped, 2–3% spurious. Results:

| input | θ | discovery paths (best two) | plain DP | after gap sliding (§7.5) |
|---|---|---|---|---|
| clean | 0.50 | [8.2–20.1]~[61.2–73.1], [1.1–6.2]~[62.1–67.2] | del 6.1–13.2 | **del 6.1–13.2**, ins 73.2–77.2 |
| noisy A | 0.50 | same | del 6.1–13.2 | **del 6.1–13.2** |
| noisy B | 0.45 | same | del 2.1–9.2 | **del 6.1–13.2** |
| noisy C | 0.45 | [9.1–20.1]~[62.1–73.1], … | del 5.1–12.2 | **del 6.1–13.2** |
| noisy D | 0.45 | same as C | del 5.1–12.2 | **del 6.1–13.2** |
| noisy E | 0.48 | as clean | del 4.2–12.1 | **del 6.1–13.2** |
| noisy F | 0.48 | as clean | del 1.2–9.1 | **del 6.1–13.2** |

Notes on these results:
- Noise moves the deletion by up to 5 bars inside the equal-content region a1 ≅ a1′. Score alone cannot place
  it: 12–13 placements fall within one matched beat of the best.
- The tie-break pins it every time: gap ends on phrase edges, then an exact first beat, then the earliest source.
- Without phrase edges, the remaining rules (exact first, earliest, rightmost) give 7.1–14.2, one bar late: bar
  14.2 equals bar 6.2 and bar 14 keeps bar 6's bass. Arrivals are what make the statement exact.
- In every run the hull was [1.1–20.1] and the target started at 62.1.
- The script's insert starts at 73.2 because 73.1 equals 20.1 note for note. The cell snap of §7.6 then moves the
  coda's start to 73.1, where its sigh cell begins.

`arrivals_check.py` runs the arrival rule of §4.2 on the principal line:
- On the clean étude the cuts in A are exactly 5.2 (E, full), 8.2 (B, half), 13.2 (E, full) and 21.1 (E, full).
  Everywhere else the rule finds only 66.2 and 77.1. That is Leichtentritt's (5+3)+(5+7).
- Under noise, arrivals degrade:
  - noisy A: identical to clean;
  - noisy B: one cut moves to 9.2;
  - noisy C: only 8.2 and 13.2 remain;
  - noisy D: a single false cut at 17.1;
  - noisy E: 13.2 is lost;
  - noisy F: all four found.

  So phrase edges in returning sections also come from internal-return edges (§8.1). The internal return
  a1 ≅ a1′ supplies 5.2|6.1, 8.2|9.1 and 13.2|14.1, and the phrase level degrades gracefully while the form level
  (above) holds.

---

## 1. Terms

| term | meaning |
|---|---|
| tick | 1/480 quarter (the MIDI PPQ of every bundle; read `DeltaTicksPerQuarterNote`, never assume) |
| bundle beat | quarter notes from the MIDI start, the time base of `Notes`, `Measures`, `Sections` |
| tactus | `ChordTimeline.BeatUnit(bar)`: 1.5 in compound meters, otherwise max(0.5, 4/denominator). In 2/4 one quarter; for the étude's 1/8 pickup, 0.5 |
| column | one tactus window of all pitched notes (the unit of returns and blocks) |
| slot | tactus/8 (60 ticks for a quarter tactus): the onset grid of rhythm keys |
| stream | a monophonic line or a texture: a score voice, a lane's outer line, a lane's rest, a hand texture, drums |
| cell | one window of one stream chosen by the segmentation (§5.3): 1 tactus to 2 bars |
| paradigm | an exact orbit: a prototype cell and every cell that is the prototype under a group element g, plus fuzzy members with a residual |
| atom | a family of paradigms with the same diminution signature, signs dropped (`N` neighbour, `T` turn, `P` passing or sigh, …); the sign census is its chirality |
| chirality | (a) cell level: the inversion bit of g (exact mirror); (b) atom level: the diminution direction (upper or lower neighbour, rising or falling) |
| unit | a level-3 node: a power iteration, a block unit or a sub-phrase |
| power | `X^n g`: n statements of X, each g of the previous one. Its cost does not grow with n. Nestable: `(U²)² ↑1` |
| return | a later span described as an earlier span under g with an edit script (match, delete, insert, substitute) |
| generator order | how often a generator acts before the shape returns to its pitch classes: 0 translation, 1 identity (loop), 2 involution (alternation), n ≥ 3 cycle |
| arrival | a principal-line note that ends a phrase (§4.2) |
| derived note | a note explained by an earlier statement: a non-prototype paradigm member, a power iteration after the first, or the target of a return, cross-reference or doubling |

---

## 2. Pipeline

```
Program.cs (after MelodySelection.Build, before the atomic write)
  └─ MotifAnalysis.Build(data, cycles, analysis, settings, folder, midiPpq) → data.Motifs
       A  Ingest      events in ticks, grid, streams, spelling, frames           (§3)
       B  Reduce      diminution tokens, arrivals                               (§4)
       C  Cells       windows → orbit keys → lexicon Viterbi → paradigms → atoms (§5)
       D  Iterate     columns, blocks, powers, runs, cycles, doubling, liquidation (§6)
       E  Returns     similarity → discovery → hull → sections → canonical script (§7)
       F  Hierarchy   phrases, periods, units, projection, cross-references, labels (§8)
       G  Code        bits, stats, formula, Verify, export, size caps             (§9)
```

Stage order:
- A → B → C → D run once, per stream where possible.
- E3–E4 (provisional sections) run before F1 (phrases of the prototype sections).
- E5 (the canonical script) needs the prototype's phrase edges, so it runs after F1.
- F4 (projection) follows E5.

Everything is in score ticks. The tempo map (rubato) is never read.

---

## 3. Stage A: ingest

### A1. Time and grid
- `ppq` = the MIDI header's PPQ (480 in every bundle seen). Note onset τ = round(Beat·ppq), duration
  δ = round(Length·ppq). All analysis arithmetic is integer ticks.
- Measures come from `cycles.Measures`. The tactus U(bar) = `ChordTimeline.BeatUnit(bar)·ppq`. Columns are
  [bar.Start + k·U, …) clipped to the bar, so an irregular bar gives a partial column (the étude's pickup is one
  0.5-beat column).
- **Silent leading measures** have no onset and are excluded from the analysed span (the étude's 4-beat lead-in).
  The root node spans [first measure with an onset].Start to EndBeat.
- **Metric weight** w(τ):
  - 4 at a bar start;
  - 3 at the bar's midpoint when the bar holds an even number ≥ 2 of tactus beats (beat 2 in 2/4, beat 3 in 4/4);
  - 2 at other tactus beats;
  - 1 at half-tactus;
  - 0 otherwise.
- **Printed bar numbers** for labels and formulas. Let m0 be the first measure with an onset. If m0 is shorter than
  m0+1 (an anacrusis), printed(m) = m − m0; otherwise printed(m) = m − m0 + 1. For the étude: m0 = 1 (the
  pickup), so measure 2 is printed bar 1.

### A2. Notes and the score import
- Events: `cycles.Notes` (Track, Channel, Beat, Length, Pitch, Velocity).
- If `score-import.json` exists beside the MIDI (format `resonance.score-import/1`), match its notes one-to-one by
  (tick + ScoreLeadInBeats·ppq, pitch), in order of (tick, staff, voice). On the étude all 1,932 match. If ≥ 98%
  match, each event takes the import's staff, voice, tpc (spelling), grace flag and printed measure number, and
  the import's `directions` (tempo, expression and dynamic texts with ticks) become boundary evidence (§7.6).
  Otherwise the import is ignored, with a warning.
- **Grace notes** (import flag, or no import and duration ≤ slot/2 with the next onset of the same stream within
  one slot) never enter streams. Each is attached to the following note as an ornament residual (the A♯4
  graces of bars 7–8 and 23–29).

### A3. Transcription mode
Active when `settings.PatternTimingToleranceBeats > 0`.
- Per bar, choose the grid g ∈ {U/4, U/3, U/6, U/8, U/12} minimising Σ|onset − snap(onset)| + 0.02·ppq·(grid points
  per bar).
- Snap an onset when its error ≤ max(tolerance, 2σ̂), where σ̂ = 1.4826·median |error|.
- The exact offset is kept as a timing residual (§9.1).
- Hands are not trusted: column similarity ignores staff (§7.1) and a lane's outer line uses the skyline.

### A4. Streams
In priority order:
1. **Score voices**: with an import, one voice stream per (staff, voice) holding ≥ 8 notes. Its principal line
   takes, per onset chord, the note chosen by a voice-leading Viterbi: cost |pitch − previous pitch| + 4·rank from
   the top (from the bottom for the lowest staff). This takes bar 2's G♯4, not E4. **The principal line never
   takes a note from another (staff, voice).** That removes the cross-voice line the musical judge found under
   bars 53/60–61.
2. **Lanes without voices**: each pitched (Track, Channel) splits into an **outer line** and a **rest** stream by
   the sustain-aware skyline (measured on the étude against MuseScore voice 1: precision 0.991, recall 1.000).
   - A note is on the outer line if it is the outermost of its onset group and no still-sounding note of the lane
     lies further out.
   - "Outer" means the bottom for bass lanes, which are: tracks named bass, left hand or LH; the second of
     `PianoTracks`; or a median pitch below 55. Otherwise it means the top.
3. **Texture streams**: per staff or lane, one sonority per onset (sorted pitches). Its hand shape is the
   interval vector above the lowest note. Only texture streams feed runs, cycles and doubling (§6).
4. **Both hands**: for pianos (two staves, or the two `PianoTracks`, or tracks named right and left hand), one
   sonority per onset across both, each note tagged by hand (for alternation).
5. **Drums**: channel 10, one stream per GM group: kick {35, 36}, snare {37–40}, hats {42, 44, 46}, cymbals {49, 51,
   52, 53, 55, 57, 59}, toms {41, 43, 45, 47, 48, 50}, other. Events are (group, onset). Only identity and time
   shift act on them.

The **principal stream** is the stream with the largest onset overlap with `data.Melody`. Melody times are seconds;
map them with `cycles.BeatAt`. Without a melody, use the outer line of `LeadVocalTrack`; for pianos, the upper
staff's voice 1; otherwise the top line of the lane with the highest median pitch. The principal stream drives
arrivals and the default glyph.

### A5. Spelling, morphetic pitch, frames
- **Spelling**: the import's tpc when present. Otherwise a line-of-fifths speller:
  - each pitch class takes the tpc t ≡ 7·pc (mod 12) nearest to a centre of gravity c;
  - c is an exponentially weighted mean of previous tpcs, decaying ×0.5 per tactus beat;
  - c is re-seeded at every local-key region start, at the centre of that key's collection on the line of fifths:
    tonic + 2 for major, tonic + 0.5 for minor (the harmonic-minor span −4 … +5). The leading tone of B♭ minor is
    then A, not B𝄫.
  - Measured on the étude by mdl-grammar: 97.2% agreement. Its errors are enharmonic, in chromatic passages,
    where real transposition takes over.
- **Morphetic pitch** m = 7·octave + letter (C = 0 … B = 6). **Alteration** α = pitch − scalePitch(m) in the local
  key (0 for diatonic notes).
- **Local key regions**: `KeyAnalysis.Regions(data.Chords, data.Key, data.Minor, beatsPerBar)`, computed unlocked
  even when the song's key is reviewed. They are used only for spelling, naming and arrivals, and are never
  written to `KeyChanges`. Key numbering in this codebase is A-based: pitch class = (Key + 9) mod 12.
- **Chord frame** per tactus: the `data.Chords` step sounding at the onset (root, quality, bass from
  `analysis.Grid.Bass`).

---

## 4. Stage B: reduction and arrivals

### B1. Diminution tokens (Schenkerian quotient)
Each voice stream's principal line is reduced to a fixpoint. Each pass removes the best-ranked non-adjacent
candidates. A token records its kind and sign: + = upper or ascending, − = lower or descending.

| rule | pattern | conditions | result |
|---|---|---|---|
| Rep | c_i = c_{i−1} | length_i ≤ ½ tactus, w_i ≤ max(w_{i−1}, 2) | merge into i−1 |
| T± (turn) | x, x±1, x∓1, x (steps) | both middle notes ≤ ½ tactus and weaker than x | absorb both |
| A± (appoggiatura) | a non-chord tone on a stronger position than the next note, resolving by step to a chord tone | — | the resolution is structural and inherits the onset |
| N± (neighbour) | \|Δ⁻\| = 1 step, Δ⁺ = −Δ⁻ | length ≤ ½ tactus, w_i < w_{i−1} | absorb into the previous note |
| P± (passing) | \|Δ⁻\| = 1, Δ⁺ = Δ⁻ | same | absorb |

Ranking: non-chord tones first, closed figures (N) before open ones (P), then the weakest metric position.

Chord tones: pitch classes of the chord step at the onset.

The reduction never decides losslessness. It supplies:
- family signatures (§5.7);
- the prolongation test of arrivals (B2);
- cheap ornament edits in the fuzzy join (§5.4).

Measured on the étude (transformation-group): bar 1 = `E4[N− Rep] F♯4[N+ Rep Rep]`; bar 17 = `G♯5[P−] E5 C♯5`;
bars 58–60 = D♯4 decorated by E (A−) and C♯ (N−).

### B2. Arrivals (tested: `final/arrivals_check.py`)
A principal-line note n at onset τ is an **arrival** if all of these hold:
1. w(τ) ≥ 2 (on a tactus beat) and n is not tied in;
2. the next principal-line onset is ≥ 1 tactus after τ;
3. n is not prolonged. The next principal note is not one diatonic step away (|Δm| ≠ 1), and B1 attached no
   N/T/P token to n.
4. The bass at τ (lowest sounding note of the lowest pitched stream; `Grid.Bass` when available) is:
   - the local tonic, giving a **full** arrival;
   - or the local dominant under a major or dominant-seventh chord, giving a **half** arrival.

Arrivals closer than 2 bars apart: keep the stronger one (full beats half; at equal strength, keep the later).

Étude (clean):
- in A: 5.2 E4 full, 8.2 B4 half, 13.2 E4 full, 21.1 E4 full;
- elsewhere only 66.2 and 77.1.
- 1.2, 2.2, 17.1, 18.1, 19.1, 20.1 and 73.1 are rejected by rule 3: each is the head of a figure.

---

## 5. Stage C: cells, paradigms and atoms

### C1. Candidate windows
For each voice, line and rest stream:
- **Starts**: every tactus beat. Texture streams start every half tactus.
- **Lengths**: L ∈ {1 tactus, ½ bar, 1 bar, 2 bars}, deduplicated (in 2/4, ½ bar = 1 tactus).
- **Anacrusis window**: one extra candidate from the span start to the first downbeat.
- A window holds the onsets in [start, start+L), plus a **tie pseudo-event** when a stream note sounds at the
  start. Windows with more than 24 events are not candidates.
- **Drum streams** get 1-bar windows starting at bar starts only. Their key is the rhythm with the GM group; only
  identity acts on them. Their paradigms are grooves, and fills join as variants or stay literal.

### C2. Orbit keys (exact, invariant under t, T, I)
```
rhythm = [(round((onset − start)/slot), tied)]                durations excluded (articulation lives in residuals)
steps  = [m_{j+1} − m_j] over the events' principal notes      voice and line streams, diatonic
       = [p_{j+1} − p_j] (semitones) + hand shape per event     texture streams, or voice windows with ≥ 30%
                                                                notes off the local scale ("chromatic")
key    = (L, rhythm, min_lex(steps, −steps));   inverted = (min_lex chose −steps)
```
- The anchor (first m, p, α) is not part of the key. It becomes the occurrence's transform.
- A window equal to its own mirror up to transposition is **achiral**.

### C3. Lexicon Viterbi (Ruwet's criterion: a unit is what repeats)
Per stream, over tactus positions q:
```
best[q] = min over windows w ending at q of best[q − L_w] + cost(w)

cost(w) = 1                                                    w is a rest
        = −log2(f/F) + Lit(π_O)/f + L(g_w) + L(ρ_w)            w ∈ orbit O with f ≥ 2 counted members
        = Lit(w) + 4                                           otherwise (singleton)

Lit(c)  = 4 + Σ_events [ H_on + H_int + 3·[α ≠ 0] ]            H_on, H_int = the stream's empirical entropies of onset
                                                                slots and step intervals, each ≥ 1 bit (adaptive)
L(g_w)  = 1 + γ(Δm of the anchor from the previous cell's last note)   (melodic continuity; §9.1 for γ)
F       = Σ f over the lexicon
```
- EM, 4 rounds. Round 0 counts every candidate window (overlaps allowed); rounds 1–3 count only the chosen tiling.
- Ties: longer window, then earlier start, then the lexicographically smaller key.
- Repetition discovers the phase. Measured on the étude:
  - the upper-neighbour cell is cut at beat 2 (it crosses the barline), nine times;
  - the sighs are cut at the downbeat;
  - B1's sixths are cut at beat 2.

### C4. Fuzzy join (variants) and its guards
After each round, a window with ≥ 4 events whose exact orbit is a singleton tries the nearest prototype of equal
length.

The distance is the minimum, over χ ∈ {+,−} and over the two most common onset-matched step offsets, of the edit
distance between (onset slot, step) sequences:
- onset shift ≤ 2 slots: 0.5;
- substitution: min(1, 0.5·|Δsteps|);
- insertion or deletion: 1, or 0.3 when the note is a repetition (interval 0) or an ornament (≤ slot).

It joins **only if all** of these hold:
- distance ≤ 0.2·events;
- edit count ≤ max(1, ⌊events/3⌋);
- **contour invariance**: every pair of consecutive matched events moves in the same direction (octave-folded);
- **MDL gate**: L(g) + L(ρ) + 2 < Lit(w).

Members joined this way carry `Exact = false` and their residual edits.

### C5. Paradigms
- An exact orbit with ≥ 2 members, plus its fuzzy members, is a **paradigm**.
- Its prototype is the earliest exact member: the fundamental is its first statement.
- Clock = 7 when the orbit key is diatonic, 12 when chromatic.
- Texture paradigms get **Symmetry** = Stab(shape) = {k : T_k(pcs) = pcs}: {0,6} for a tritone, {0,3,6,9} for a
  diminished seventh, {0,4,8} for an augmented triad.
- Singletons stay literal cells and are **not exported** (§9.5).

### C6. Relations and names
Each member gets the **cheapest group element** relative to its syntagmatic neighbour (the nearest earlier exact
member) and relative to the prototype. The text names the neighbour relation; the transform fields store the one
from the prototype.

| element | action | bits |
|---|---|---|
| identity | — | 0 |
| O | ±1 octave (t±7 / T±12) | 1 |
| t_k | tonal: m+k, chromatic value from the local scale, alterations kept | 1.5 + log2(1+\|k\|) |
| T_k | real: p+k | 2.5 + log2(1+\|k\|) (no extra when it equals t_k) |
| t_k^H | chord frame: interval above the sounding root kept | 2 + log2(1+\|root move\|) |
| I | mirror about the first note (diatonic or real) | +2 |
| R | retrograde | +4 (offered only for windows ≥ 1 bar) |
| A_r | augmentation or diminution r ∈ {½, 2, 3/2}, or held-head lengthening | 3·\|log2 r\|; head lengthening 2 |
| P | parallel mode (alter degrees 3, 6, 7 by ∓1) | +2 (P₆, ♭6 alone: +1.5) |
| residual | unexplained altered note 3 · ornament inserted or deleted 2 + log2(positions) · onset shift 1 · substitution 1 + log2(1+\|Δsteps\|) | |

Alterations are named in this order:
1. P, when first-beat chords change major ↔ minor on the same root;
2. the chord frame;
3. the regional key:
   - a lowered 3, 6 or 7 in major is "♭6 (or ♭3, ♭7) borrowed from X minor";
   - a raised note that is the leading tone of the member's local key or tonicization target is "the leading
     tone of Y (the sequence tonicizes Y)".

**Text templates**: these sentences are what tours quote.
- `repeat`
- `tonal sequence up/down a {second, third, fourth, fifth, sixth, seventh}` (k steps = interval k+1)
- `real sequence up/down a {semitone, whole tone, minor third, major third, fourth, tritone, …}`
- `an octave lower/higher`
- `mirrored (inversion)`
- `in the parallel minor (A major → A minor)`
- `with 1 altered note: ♭6 borrowed from E minor`
- `with 1 altered note: the leading tone of C♯ minor (the sequence tonicizes it)`
- `held note lengthened (augmented head)`
- `variant (n edits)`

Étude names measured by transformation-group:
- 15 = 14 "up a third, B♯ the leading tone of C♯ minor";
- 31 = 30 "parallel minor";
- 73/74 = 17 "an octave lower with C♮, ♭6 borrowed from E minor";
- 19 = 18 t−2;
- 16 = 6 t+5.

### C7. Atom families and chirality
- A paradigm's **signature** is its prototype's diminution tokens with signs dropped (`N`, `T`, `P`, `A N`,
  `N T`, …).
- Paradigms with equal signatures form an **atom**, whose Kind is the first token.
- **Sign census**: a member's sign is the prototype token's sign, times −1 when the member is inverted.
  Positive, Negative and Neutral counts are kept per atom (Neutral for achiral members).
- On the étude, the N family holds α⁺ (1.2, ×9, +) and α⁻ (1.1 and 62.1, −).
- Atom names come from the Kind:
  - N: neighbour figure;
  - T: turn;
  - P: passing line (a falling P line opened by a held head is named **sigh**);
  - A: appoggiatura figure;
  - Osc: oscillation;
  - Arp: arpeggio;
  - Shape: hand shape (texture);
  - Hold: held note.

---

## 6. Stage D: iteration (blocks, powers, runs, cycles, doubling, liquidation)

### D1. Columns and column relations
A column (one tactus window) is the multiset of (hand class h, onset slot, tied, pitch p, morph m) over all pitched
streams. h ∈ {upper, lower} comes from the staff or lane role. In transcription mode h is ignored.

Two columns u, v are related by:
- **exact**: equal multisets, ignoring the tied flag;
- **T_k**: equal slots, with v's pitches = u's + k;
- **t_d**: equal slots, morphs shifted by d, chromatic values from the local scale. Altered notes are allowed and
  counted.
- **near**: soft similarity sim_k(u,v) ≥ θ (§7.1).

Relations are cached per (u, v, k) on demand.

### D2. Tandem blocks
For unit lengths U ∈ {1, 2, 4, 8} bars (and 1 tactus inside phrases of ≤ 4 bars), a block is
`U · g(U) · g²(U) …` with n ≥ 2 and one common relation g = (kind, k) that relates ≥ 75% of each unit's columns to
the next unit's.

Among overlapping blocks keep:
1. the largest coverage n·U;
2. then the higher exactness with respect to g;
3. then a downbeat start;
4. then the earliest.

Blocks never cross a section boundary.

Étude (measured by transformation-group):
- 21.2–25.1 and 25.2–29.1: 2-bar unit repeated;
- 30–37: 4-bar unit then T+2;
- 38–40: 1-bar unit ×3 at T−1;
- 47–50: 2-bar unit then T−1;
- 18–19: t−2;
- 58–59 and 73–74: repeats.

### D3. Powers (the fractal primitive)
- **Unit powers**: every block becomes a Power node (count n, step g), with its units as iteration children.
- **Cell powers**: consecutive principal-stream cells of one paradigm with a constant step become a Power
  *relation*, not a node. For example α⁺ at 1.2 → 2.2 is `α⁺² ↑1`. The same rule on every lane's outer line and
  on drum streams gives the loops of pop: a riff ×25, a groove ×13, with StreamA set.
- **Nesting**: two or more consecutive blocks with equal (U, n, g), where block_{i+1} ≅ h(block_i) on ≥ 75% of the
  columns for one constant h, become an outer Power (count, h) whose iterations are the inner powers.
  - B1 = 21.2–25.1, 25.2–29.1 = `(U²)² ↑1`.
  - B5 = 54–55, 56–57 = `(λ²)² ↓5c`, where λ is the bar-54 retransition figure.
- **Cost** (mdl-grammar): L_pow = γ(n−2) + 1 + L(step) + 1. It does not grow with n, which is why even n = 2 folds
  ("the two swells").

### D4. Texture runs and cycles
A **run**, on a texture or both-hands stream:
- a maximal `x, g x, …, g^{n−1} x` with a constant hand shape;
- a step sequence periodic with period 1 or 2;
- n ≥ 6 events, with gaps ≤ ½ tactus.

A **cycle** is the same run in pitch-class space modulo Stab(shape). Its period is
```
n* = (12/|Stab|) / gcd(12/|Stab|, |j|)       j = net step per period of the run
```
Examples: a tritone with j = −1 gives n* = 6; a diminished seventh with j = −1 gives 3.

The Run relation stores Semitones = the per-event step, `Period` = n* (0 when the run never closes), and
`Symmetry` = Stab(shape).

Étude:
- LH 38.1–41.2: T−1 per sixteenth on tritones, a period-6 cycle;
- RH: alternating T−1/T+5, net T+4 per pair, which is order 3 (an augmented triad);
- 52.2–53: alternating sixths ±6, order 2.

### D5. Doubling and contrary motion
Stream B **doubles** stream A over ≥ 8 consecutive shared onsets when each sonority of B equals A's transposed by
K_i ≡ 0 (mod 12). The K_i (the registers) are listed.

If the top-line steps of A and B have opposite signs on ≥ 75% of the steps over ≥ 4 consecutive events, that
sub-span is flagged **Contrary**: the hands are in opposite chirality.

Étude: LH = RH −24 over 46.1–52.2, then K alternating −24/−12 over 52.2–53.2, flagged Contrary (RH C♯6 → G5 falls
a tritone while LH C♯4 → G4 rises).

### D6. Hand alternation
A both-hands run whose events alternate staves in a periodic pattern (R R L L …) is recorded with that pattern.
Étude 42.1–43.1: sixths falling T−6, hands R R L L R R L L.

### D7. Liquidation
Per bar, the period p is the shortest of {tactus/2, tactus} (8th or quarter in 2/4) at which ≥ 75% of the
principal line repeats. Otherwise p = 1 bar if the bar repeats its neighbour; otherwise no period.

A liquidation is ≥ 3 consecutive bars with a non-increasing p that ends at ≤ ¼ of its start.

Étude 54–61: 8 → 4 → 2 sixteenths ("it stabilizes").

### D8. Cross-scale flag
A generator g that acts as a texture run at lag a and as a unit power at lag b ≥ 4a, over overlapping time, sets
`CrossScale` on both relations. Étude 38–40: T−1 per sixteenth (LH) and T−1 per bar.

### D9. Generator order and the per-bar label
- **Order**: 1 for identity (a loop); 2 for an alternation or involution; n* for a cycle that closes within its
  count; 0 for a translation (a sequence that keeps rising or falling).
- **Dominant generator per bar** (input of §8.2): the widest detection covering ≥ 50% of the bar. Priority on
  equal width: block, then run or cycle, then doubling, then alternation, then liquidation.
- **Label** = (kind, unit, step). For example: `block 2 bars ×2 =`, `block 4 bars ×2 T+2`, `doubling`,
  `liquidation`. An unlabelled bar takes its predecessor's label.

Étude B (transformation-group's measured labels):

| bars | label |
|---|---|
| 21–29 | block 2 bars |
| 30–37 | block 4 bars T+2 |
| 38–40 | block 1 bar T−1 |
| 41 | run T−1/T+5 |
| 42–45 | alternation (44–45 unlabelled, inherit) |
| 46–53 | doubling |
| 54–61 | liquidation |

---

## 7. Stage E: returns and sections

### E1. Column similarity (soft, transposition-aware)
For columns u, v and k ∈ {−6 … +6}: greedily match the notes of u to the notes of v with the same hand class,
|slot difference| ≤ 1 and pitch relation:
- p_v = p_u + k: weight 1.0;
- p_v = p_u + k ± 12: weight 0.8 (octave tolerance);
- |p_v − (p_u + k)| = 1 with morph shift equal to the column's modal shift: weight 0.7 (tonal credit).

```
sim_k(u,v) = 2·Σ weights / (|u| + |v|)          Dice; 0 when either column is empty (silence is no evidence)
S(u,v)     = max over k of [ sim_k(u,v) − 0.04·[k ≠ 0] ],    K(u,v) = the argmax
```

### E2. Adaptive threshold and gap costs (alignment-recursion)
```
θ  = clamp( quantile_0.95 { S(u,v) : |u − v| ≥ 4 beats }, 0.45, 0.90 )      étude 0.50; noisy 0.45–0.48
go = 4·(1 − θ)          gap open
ge = 0.25·(1 − θ)       gap extend, per beat
match score = S(u,v) − θ
```
Why these values:
- θ is the piece's own background similarity, so textures that resemble everything (sixths, Alberti basses) do
  not make returns. It is the null model the fractal-ifs design lacked.
- An 8-bar omission costs go + 16·ge = 8(1−θ), worth 8 strong beats, so an abridged copy is still one copy.
- A 40-bar contrasting section is never bridged.

### E3. Discovery (Gotoh Smith–Waterman + Waterman–Eggert)
- At a node of length L (the whole piece first):
  - local alignment on the upper triangle with lag ≥ **min_lag = max(1 bar, round(L/8))** (the scale rule);
  - one transposition k per path: a diagonal step must keep K or score S_k within 0.05 of S;
  - after each path, mask it with a ±1 band; repeat up to 40 paths or until the score < 3(1 − θ).
- **Trim** each path's ends while the end column's best match to the source rows is < θ + 0.15. The étude path
  [8.2–20.1]~[61.2–73.1] trims to start at 62.1.
- A path is **structural** at this node when it scores ≥ 6(1 − θ) and spans ≥ max(2 bars, L/16).
- Repetitions with lag < min_lag protect their spans from being cut at this node and are used one level down
  (§7.7). Each repetition enters the tree once, at its own scale.
- Above 2,500 tactus columns, discovery runs on bar columns. A band of ±2 bars around each chosen path is then
  refined at tactus resolution.

### E4. Prototype hull and occurrences
- The structural paths are grouped by target region; the **best family** is the one explaining the most beats.
- Its **hull** is the union of the sources of paths that map into the same target region, with source gaps
  ≤ max(2 bars, L/8). The hull starts at the first downbeat of the piece when it begins within min_lag of it.
- Étude: [1.1–6.2]~[62.1–67.2] ∪ [8.2–20.1]~[61.2–73.1] gives hull [1.1–20.1] and target from 62.1 (clean and all
  six noisy files).
- Further occurrences (rondo, pop) come from **anchored semi-global alignment** of the hull against the remaining
  columns:
  - every hull row is consumed, matched or deleted, at affine cost;
  - an occurrence needs ≥ 50% of its rows matched;
  - an inserted span that recurs elsewhere (a bridge heard twice) cuts the occurrence;
  - an occurrence counts as a copy only if it explains ≥ 55% of itself **and** ≥ 30% of the prototype. This stops
    a 3-bar tail being labelled a second A′ (alignment-recursion, noisy run).

### E5. The canonical edit script (gap sliding; tested in `final/align_check.py`)
This runs after F1 has cut the prototype section into phrases.
1. **Anchored affine DP**: rows = hull columns (global; deleted rows cost go + ge·len); columns = the target from
   the trimmed start (local end, so the unaligned suffix is an insertion). The score is Σ(S − θ) over matches.
2. **Gap sliding**: for each deletion of length g, try every placement [a, a+g) of the hull rows that keeps the
   copy's column count. The near-tie set is the placements scoring ≥ best − **δ**, with δ = 1 − θ (one strong
   beat). Choose lexicographically:
   1. the number of gap ends on prototype phrase edges (a is a phrase start; a+g−1 is a phrase end), descending.
      An edge backed by an arrival counts 1; an edge only from an internal return counts 0.5;
   2. whether the copy's first column equals its source exactly, descending;
   3. the first matched source row, ascending (earliest: the reprise restates its source from the start);
   4. the larger a (right-normalised, mdl-grammar's indel rule).

   With several gaps, slide them one at a time, left to right, two passes. This is mdl-grammar's indel
   right-normalisation and alignment-recursion's phrase-edge bonus as one explicit rule.

   Measured (§0.4): with arrival-backed edges, 7 of 7 inputs give 6.1–13.2. With no phrase edges at all, rules 2–4
   give 7.1–14.2 on 6 inputs and 6.2–14.1 on one: one bar late, because bar 14 shares bar 6's accompaniment and
   second beat. Phrase edges are therefore required for the exact statement, and the noise gate allows ±2 beats.
3. **Script**:
   - matched runs become `match` segments (columns with S < 0.999 are listed as `substitute`, which become
     sparks);
   - deleted runs become `delete` (source span);
   - unmatched target columns inside the copy become `insert`, and the trailing ones the tail insert (a coda
     candidate).
4. **Omits** = the labels of prototype phrases lying entirely inside deletions: `"a2, a1′"`.

Étude result (clean and noisy A–F): `[1.1–5.2]~[62.1–66.2] del[6.1–13.2] [14.1–20.1]~[67.1–73.1] ins[73.2–77.2]`.

### E6. Sections
- **Families**: in order of explained beats. Each prototype hull is a section (A, …) and each copy its primed
  return (A′, A″).
- **Complements**: the spans between them become sections lettered by first appearance (B, C, …). A complement
  that is itself returned elsewhere takes that family's letter.
- **Head**: ≤ 1 bar (or ≤ min_lag) joins the first section as its anacrusis (the étude's pickup B3). A longer
  unique head is an **Intro**. Silent measures never form an Intro.
- **Tail**: the copy's trailing insert becomes a **Coda** child of the copy section when it is ≤ 25% of the piece
  and starts no recurring family. Otherwise it is its own section.
- **Cell snap**: a section or coda boundary b that falls strictly inside a principal-stream cell [c0, c1) with
  b − c0 ≤ 1 tactus moves to c0, when no member of that cell's paradigm lies in the source span aligned to it.
  The coda therefore starts with the sigh at **73.1**: the sigh's G♯4 is also bar 20's G♯4, an elision that the
  relation text records ("the coda branches from bar 20's cadential six-four").
- **Direction snap** (score imports only): a boundary within ±1 tactus of a tempo or expression direction matching
  `/a tempo|tempo i|più|meno|animato|mosso|bravura|legatissimo|coda|rit|rall|lento|allegro|andante/i` moves to the
  direction's beat, unless that would cut a cell. Such a direction also adds +0.3 evidence in the DPs. Étude:
  `poco più animato` at 21.2 and `Tempo I` at 62.1 both confirm.
- **Reviewed or pop forms**: when `settings.SectionBoundaries` is non-empty or `data.Style == "pop"`, the level-1
  nodes are `data.Sections` verbatim. Returns are then computed only between unions of consecutive sections and
  recorded as Return relations. A recurring verse + chorus is one relation spanning both (drawn as a bracket).
  Pop sections, groups and grammar therefore stay exactly as today.

### E7. Internal returns
Inside each classical section of ≥ 8 bars, E3–E5 run again with the section's own L. The accepted returns:
- become phrase-level relations, for example a1′ ≅ a1 with a substitution at 9.1 (G♯4 added);
- supply phrase edges for F1.

---

## 8. Stage F: hierarchy

### F1. Phrases in returning (and pop) sections
- **Cuts** come from three sources:
  - arrivals (§4.2): a cut at the next tactus after the arrival note, which closes its phrase;
  - internal-return edges (§7.7): both ends of each prototype and copy, after path trimming. An edge snaps to an
    arrival within ±1 bar (strength 0.9); an unsnapped edge is weaker (0.6), because a copy's extent can overshoot
    by a shared bar (6.2 = 14.2);
  - for pop, also the `Section.PhraseBars` grid.
- Minimum phrase length 2 bars. When cuts collide, keep the stronger: full arrival 1.0, snapped return edge 0.9,
  half arrival 0.8, unsnapped return edge 0.6, grid 0.3; at equal strength, the later.
- **Copy extent never adds a cut** (fatal flaw 1).

Étude A: 0.1–5.2 | 6.1–8.2 | 9.1–13.2 | 14.1–21.1, labelled a1, a2, a1′, a3.

### F2. Periods in non-returning sections
1. Candidate cuts, with evidence ev:
   - generator-label changes (D9): 0.6;
   - arrivals: 1.0 or 0.8;
   - internal-return edges: 0.9;
   - a direction snap adds +0.3.
2. Choose the cuts that maximise
   ```
   J = Σ_cuts ev − λ·#cuts − κ·Σ_periods ((len − P)/P)²
   λ = 0.5·median(ev over candidates),  κ = 1.0,
   P = argmax over P ∈ {2, 4, 8, 12, 16} bars and phase of [mean over grid points of the max ev within ±1 bar]
       − 0.03·|log2(L/(4P))|
   ```
   Periods are ≥ 2 bars. Texture novelty is **not** an input.
3. Labels: the section letter plus an index (B1 … B5).

Étude B, from transformation-group's measured labels:
- the cuts at 30, 38, 46 and 54 fit P = 8;
- 41 (J −0.23) and 42 (J −0.20) are rejected;
- result: **21.2–29 | 30–37 | 38–45 | 46–53 | 54–61**, the literature's five periods.

### F3. Sub-phrases and units
- **Sub-phrases**: inside a phrase of ≥ 6 bars, cut where the principal line's atom family changes and the new
  family holds for ≥ 2 bars. a3 splits into 14.1–16.2 (neighbour and turn) and 17.1–21.1 (sigh), Leichtentritt's
  7 = 3 + 4.
- **Units**: the blocks and powers inside a phrase or sub-phrase become level-3 nodes, with power nodes nesting.
  Cells covered by no block stay cells, not nodes.

### F4. Projection (copies inherit, reprises omit)
- A copy's children are the images of its prototype's children through the canonical script:
  - a child inside a deletion is listed in `Omits`;
  - a truncated or varied child keeps its label with a prime;
  - an exact child keeps its label.
- Units are projected recursively (`Projected = true`). A copy is never re-segmented.
- Inserted spans (the coda) are analysed afresh by F1–F3.

Étude:
- A′ = a1 (62.1–66.2) · a3′ (67.1–72.2, a3 without its cadence) · Coda (73.1–77.2);
- the coda's units: 73–74 = `β′↓8ve[♭6]²`, then 75–76 the held-head variant, then the arrival at 77.1.

### F5. Cross-references
A unit (or a bar of a phrase without units) that no power or return explains is checked against earlier units of
the same length. If ≥ 75% of the columns relate by one g, a CrossReference relation is recorded. Example:
"bar 16 = bar 6 a sixth higher (t+5, in octaves)". It is also checked against the coda: 73 against 17. A
cross-reference never creates a boundary.

### F6. Labels and the formula
- **Labels**:
  - sections by first appearance (A, B, …), a copy primed (A′);
  - phrases in lower case with an index (a1, a2), a copy of an earlier phrase primed (a1′);
  - periods as B1 … B5;
  - paradigms by Greek letter in order of first appearance among paradigms with ≥ 2 members in principal
    streams (α … ω, then α₂ …);
  - multi-cell units as U, V, W, … by first appearance;
  - Coda and Intro.
- **Formula grammar**:

  | notation | meaning |
  |---|---|
  | `·` | concatenation |
  | `Xⁿ ↑k` | power stepped by k tonal steps |
  | `Xⁿ ↑kc` | power stepped by k semitones (real) |
  | `(…)` | grouping, for nested powers |
  | `X′` | copy with edits |
  | `X − (…) + Coda` | omission and insertion |
  | `X̄` | mirror |
  | `[♭6]`, `[P]` | named alteration or parallel mode |
  | `↓8ve` | octave |
  | `⊙` | arrival |
  | `⌐` | anacrusis |
  | atom signs | `N⁺` / `N⁻` |

- Each node's `Text` is its formula line. `MotifLayer.Formula` is the piece line, then one line per section and
  per phrase or period that has structure, joined by `\n`. `MotifLayer.Caption` is the piece line, a space-dot
  separator, and the compression summary (§9.3).
- The expected étude formula is in Appendix A.

### F7. Flattening
- Nodes are stored in preorder: `Nodes[0]` is the piece and children are contiguous (`FirstChild`, `ChildCount`).
- Level: 0 piece, 1 section, 2 phrase or period, 3 sub-phrase, power or unit (nesting allowed); `Depth` is the
  tree depth.
- `Levels` = {"Piece", "Section", "Phrase", "Unit"}.

---

## 9. Stage G: code, statistics, verification, export

### G1. Description length
- γ(v) = signed Elias-gamma length: γ(0) = 1; otherwise 2⌊log2(|v|+1)⌋ + 2.
- Event literal: log2(slots per window) + γ(Δm) + 3·[α ≠ 0], plus γ(m − m_top) per extra chord note.
```
L_total  = L_streams + Σ_paradigms Lit(prototype)
         + Σ_member cells [ log2(#paradigms in the stream + 1) + L(g) + L(ρ) ]
         + Σ_literal cells [ Lit(cell) + 1 ]
         + Σ_powers L_pow  − (the member references the power replaces)
         + Σ_returns [ log2(#columns) + γ(len) + L(g) + Σ_gaps (log2(#columns) + γ(gap)) + L(column substitutions) ]
           − (the cell references inside the copy, which the return replaces)
         + L_articulation
L(ρ)     = Σ edits:  insert 2 + log2(positions) + event literal · delete 2 + log2(positions) · substitute
           1 + log2(1 + |Δsteps|) · shift 1 · unexplained alteration 3 (named P 2, named ♭6 1.5, leading tone 1.5)
L_articulation = order-0 entropy, per stream, of (duration − prototype default) in slots, of the velocity code
           (0–127) and of the off-grid timing offsets (transcription mode)
```
- **BitsModel** = L_total − L_articulation (the headline).
- **BitsArticulation** is reported separately and never drives a decision.

### G2. Baselines (honest comparisons)
- **BitsLiteral**: Σ over notes of [log2(#streams) + γ(IOI in slots) + γ(Δpitch in semitones) + γ(duration in
  slots)].
- **BitsEntropy**: Σ over streams of n·(H(IOI) + H(Δpitch) + H(duration)), plus an 8-bit-per-symbol codebook.
- **BitsCells**: paradigms + member references + residuals + literal cells, with no powers or returns.

Reference numbers measured by mdl-grammar's variant of these definitions, on the étude: literal 35,900, entropy
14,126, atoms-only 13,886, grammar 8,677 bits.

### G3. Derived coverage and the caption
- `DerivedNotes` counts each note once if it lies in a non-prototype paradigm member, a power iteration after the
  first, a return target's match segment, a cross-reference target or a doubling target. Measured on the étude:
  transformation-group 71% (cells and returns), mdl-grammar 83% (inside repeated rules).
- **Caption**: `<piece line> · <Notes> notes → <Paradigms> cells in <Atoms> atoms · <sections> sections ·
  <phrases> phrases · <Derived>% derived · <BitsLiteral/BitsModel>× smaller`. For example: `A · B · A′ (A′ = A −
  bars 6–13 + coda) · 1,932 notes → 24 cells in 9 atoms · 3 sections · 11 phrases · 74% derived · 4.0× smaller`.

### G4. Verify (lossless gate)
`MotifVerify.Check` decodes every stream from:
- prototype literals;
- members: prototype under g, then residual edits;
- literal cells;
- grace residuals;
- the articulation code: exact onset offsets, durations and velocities as integers in ticks and 1/127.

It compares the result with `cycles.Notes` exactly as `SectionCompression.Verify` does: sorted by (track, channel,
beat, pitch, length, velocity); beat and length within 1e-8; pitch equal; velocity within 1e-6.
- **Strict** (`--strict-motifs`, `--self-test`, `--sweep-motifs`, the library regeneration script): a mismatch
  throws before the write.
- **Default**: a mismatch writes `Motifs.Schema = 0` with `Status = "verify failed: <first mismatch>"` and prints
  `MOTIFS DISABLED: …`. The bundle stays a valid v6 bundle that shows the classic wheel, so one analysis bug cannot
  block the library pipeline.

### G5. Export and size caps
- **Cells exported**: members of paradigms with ≥ 2 members, from:
  - every lane's outer-line stream;
  - the principal stream;
  - texture streams referenced by a relation.

  Literal cells are not exported: a gap means literal material.
- **Sparks**: only for exported cells, at most 8 per cell (a cell with more is exported as `Exact = false` with
  its 8 largest).
- **Caps**: ≤ 40,000 cells, and the Motifs JSON ≤ 4 MB (measured by serialising the layer alone). Above that,
  cells of non-principal streams are dropped first, lowest coverage first, and `Status` says so.
- Expected size: about 0.2–0.3 MB for the étude (bundle 3.6 MB); about 2–3 MB for the Rach2 Allegro (bundles
  23–107 MB).

### G6. Determinism
- No randomness.
- Never iterate a `Dictionary` or `HashSet`. Sort keys, or keep insertion-ordered lists.
- All sorts are stable, with total-order tie-breaks ending in integer ids.
- Floating comparisons use ε = 1e-9, then an integer tie-break.
- `Parallel.For` only over streams or similarity rows, writing to per-index slots, merged in index order.
- No timestamps or durations in the output.
- Every float written is finite. NaN or ∞ would make System.Text.Json throw, so bits are clamped and a 0/0
  becomes 0.
- Two runs give a byte-identical bundle (a self-test, F23). Story hashes depend on it.

---

## 10. Parameters (defaults and why)

| parameter | default | why |
|---|---|---|
| slot | tactus/8 | 32nds at a quarter tactus; the étude's 10:8 tuplet and graces round cleanly |
| window lengths | 1 tactus, ½ bar, 1 bar, 2 bars | the motif scales seen in every test piece; longer units come from blocks |
| Viterbi EM rounds | 4 | converged by round 3 on the étude (transformation-group) |
| fuzzy join | dist ≤ 0.2·events, events ≥ 4, edits ≤ max(1, ⌊events/3⌋), contour invariant, MDL gate | each guard removed a false identity in a prototype (bar-42 cell into the retransition at 0.25; sigh as "mirrored neighbour" without contour invariance) |
| ornament indel | 0.3 | makes a repercussion or ornament cheap, so ornamented restatements stay members |
| chromatic window | ≥ 30% notes off the local scale | spelling is unreliable there; name relations as real |
| block consistency | ≥ 75% of unit columns under one g | transformation-group's tested threshold |
| run length | n ≥ 6 events, period 1 or 2 | catches the tritone chains and alternating sixths, not chance pairs |
| doubling | ≥ 8 shared onsets; contrary if ≥ 75% opposite steps over ≥ 4 events | 46–53 found as 62 events; contrary on 52.2–53 |
| liquidation | ≥ 3 bars, ends ≤ ¼ of the start period | 8 → 4 → 2 at 54–61 |
| θ | q95 of off-diagonal S, clamped to [0.45, 0.90] | the piece's own background; robust in the noise test |
| go, ge | 4(1−θ), 0.25(1−θ) per beat | an 8-bar omission costs 8 strong beats; tested clean and noisy |
| min_lag | max(1 bar, round(L/8)) | alignment-recursion's scale rule; top level stable for L/4 … L/16 in the engineering judge's sweep |
| path trim | best match < θ + 0.15 | removed the spurious 61.2 ~ 8.2 start |
| structural path | score ≥ 6(1−θ), span ≥ max(2 bars, L/16) | rejects one-bar coincidences |
| gap sliding δ | 1 − θ | one strong beat; 12–13 placements tie on the étude |
| copy acceptance | ≥ 55% of the copy and ≥ 30% of the prototype explained | stops short tails becoming copies |
| coda | tail ≤ 25% of the piece | larger tails are sections |
| phrase minimum | 2 bars | stops one-bar phrases at nested arrivals |
| period DP | λ = 0.5·median ev, κ = 1.0, P from {2, 4, 8, 12, 16} bars | gives the five B periods; κ = 0.5 would split 38–41 \| 42–45 |
| export caps | 40,000 cells, 4 MB | keeps Unity load time and bundle growth bounded |

All parameters live in one `MotifSettings` record with these defaults. `song.json` can override them under a
`Motifs` object. Overrides are for experiments only, never needed by a library song.

---

## 11. Complexity and runtime budget

n = notes, C = columns (tactus beats), W = candidate windows per stream (about 4C), k = events per window (≤ 24),
Π = prototypes per length bucket.

| stage | cost | étude (C = 155) | Rach2 Allegro (C = 2,026, about 10 k notes) |
|---|---|---|---|
| A, B | O(n log n) | < 5 ms | about 30 ms |
| C keys and Viterbi | O(W·k + rounds·C·4) per stream | about 10 ms | about 0.3 s (22 streams, parallel) |
| C fuzzy join | O(rounds·W_single·Π·k²), bucketed by (events ±1, first three contour signs), ≤ 32 nearest | about 30 ms | about 1.5 s |
| D blocks, runs | O(C·Σ U) + O(n) | < 10 ms | about 0.2 s |
| E similarity | O(C²·13) dot products, computed on demand, rows parallel | about 20 ms | about 1.5 s (4.1 M pairs) |
| E discovery | ≤ 40 paths × O(C²) | about 50 ms | about 2 s (bar columns beyond 2,500) |
| E5, F | small | < 10 ms | < 0.2 s |
| G Verify, export | O(n log n) | < 20 ms | about 0.2 s |

- **Budget**: ≤ 1.5 s added to the étude's PatternPrep run (warm Release); ≤ 10 s for the Rach2 Allegro on 4
  cores; ≤ 30 s and 600 MB for 50 k notes.
- The full PatternPrep run is 3–8 s today.
- Above budget, the stage reports its timings with `--verbose`. Similarity is never cached as 13 full matrices:
  only the max and argmax S, K are kept (5 bytes per pair).

---

## 12. C# structure in `Tools/PatternPrep`

The project compiles every `*.cs` under its folder (SDK globbing), and it links `Assets/PreparedPatternSong.cs`, so
the contract classes of §13 are shared with Unity unchanged. New code lives in `Tools/PatternPrep/Motifs/`. It
follows the existing style: static classes, plain arrays, no LINQ in loops above O(n).

| file | contents | ≈ lines |
|---|---|---|
| `Motifs/MotifSettings.cs` | `sealed record MotifSettings` with every §10 default; `FromSong(SongSettings, JsonElement?)` | 60 |
| `Motifs/MotifInput.cs` | `MotifEvent` (Tick, Duration, Pitch, Morph, Alter, Track, Channel, Staff, Voice, Hand, Velocity, Grace, NoteIndex); `MotifGrid` (columns, tactus, slot, metric weight, printed bars, silent lead-in); `ScoreImport.TryLoad(folder, cycles, leadInBeats, ppq)` (matching, tpc, grace, directions); `Quantiser` (§3 A3) | 300 |
| `Motifs/Spelling.cs` | line-of-fifths speller; `KeyFrames` (regions via `KeyAnalysis.Regions`, scalePitch, alteration, local tonic and dominant); chord frame from `data.Chords` and `Grid.Bass` | 170 |
| `Motifs/MotifStreams.cs` | voice streams with the principal-line Viterbi; sustain-aware skyline; rest, texture, both-hands and drum streams; principal-stream choice from `data.Melody`, `LeadVocalTrack`, `PianoTracks` and track names | 240 |
| `Motifs/TransformGroup.cs` | `readonly struct Transform` (Steps, Semitones, Inverted, Retrograde, Parallel, Scale, Frame); `Cost()`; `Apply()`; `Compose()`; `Name()` (the §5 C6 templates); `Stabiliser(pcs)`; `Order(step, stab)` | 240 |
| `Motifs/Diminution.cs` | `Reduce(line, frames)` → tokens per note; `Arrivals(principal, frames)` → `List<Arrival>` | 220 |
| `Motifs/CellLexicon.cs` | windows; `OrbitKey` (an ordinal-comparable byte key); Viterbi EM; fuzzy join with bucketing and guards; `Paradigm`, `Member`, atoms and census; relation naming | 450 |
| `Motifs/Iteration.cs` | column relations; tandem blocks; powers (cell, unit, nested); texture runs and cycles; doubling and contrary; alternation; liquidation; cross-scale; per-bar generator labels | 380 |
| `Motifs/Returns.cs` | E1 similarity (on demand, rows parallel); θ; Gotoh SW with Waterman–Eggert; trimming; hull; anchored affine DP; gap sliding; edit scripts; sections; cell and direction snaps; reviewed and pop mode; internal returns | 450 |
| `Motifs/Hierarchy.cs` | phrases (F1), period DP (F2), sub-phrases and units (F3), projection (F4), cross-references (F5), labels and formula (F6), flattening (F7) | 420 |
| `Motifs/MotifCode.cs` | bits (G1), baselines (G2), derived and caption (G3), export with caps (G5) | 260 |
| `Motifs/MotifVerify.cs` | lossless decode and exact compare (G4) | 160 |
| `Motifs/MotifAnalysis.cs` | orchestrator: `Build(...)`, `Report(...)`, timings with `--verbose`, fail-soft and strict handling | 140 |
| `SelfTests.Motifs.cs` | fixtures F1–F28 (§16), `RunMotifs(bool verbose)`, `SweepMotifs(string[] references)`, fixture writer `motif-ternary` | 700 |
| `reference-forms/*.motifs.json` | golden expectations for the étude, the Nocturne and Ticket to Ride (§15) | — |

### 12.1 Entry point
`Program.cs`, keeping the existing order:
```csharp
// top of Program.cs, before every other branch: flags that may appear anywhere
bool strictMotifs=args.Contains("--strict-motifs");args=args.Where(a=>a!="--strict-motifs").ToArray();
bool motifReport=args.Length>=1&&args[0]=="--motifs-report";if(motifReport)args=args.Skip(1).ToArray();
// new modes beside --fixture / --sweep
if(args.Length>=1&&args[0]=="--self-test-motifs"){SelfTests.RunMotifs(args.Contains("--verbose"));return;}
if(args.Length>=1&&args[0]=="--sweep-motifs"){SelfTests.SweepMotifs(args.Skip(1).ToArray());return;}
// --self-test also runs SelfTests.RunMotifs() after SelfTests.Run(...)

// after MelodySelection.Build(data,accents);  (today line 115, before the atomic write at 116)
data.Motifs=MotifAnalysis.Build(data,cycles,analysis,settings,folder,midi.DeltaTicksPerQuarterNote,strictMotifs||motifReport);
if(motifReport){MotifAnalysis.Report(data.Motifs,Console.Out);return;}   // dry run: nothing written
// after the existing summary lines
Console.WriteLine(data.Motifs.Schema>0?$"  motifs: {data.Motifs.Caption}":$"  MOTIFS DISABLED: {data.Motifs.Status}");
```
`data.Version` becomes 6 automatically through `CurrentVersion`.

### 12.2 Inputs, all already in scope at that point

| input | use |
|---|---|
| `cycles.Notes`, `cycles.Measures` | events and grid |
| `data.Chords`, `analysis.Grid` (`Bass`, `Bar`) | chord frames, arrivals |
| `data.Key`, `data.Minor`, `KeyAnalysis.Regions(...)` | local keys |
| `data.Melody` (seconds → beats through `cycles.BeatAt`) | principal stream |
| `data.LeadVocalTrack`, `data.PianoTracks`, `data.TrackNames` | lane roles |
| `data.Sections` and `data.Style` | pop or reviewed level 1 |
| `settings.PatternTimingToleranceBeats` | transcription mode |
| `settings.ScoreLeadInBeats` | score-import matching |
| `score-import.json` beside the MIDI | voices, tpc, grace, directions |
| `MelodyStrands` | **not used**: seconds, performance time |

### 12.3 Determinism, threading, runtime
- §9 G6 applies throughout.
- `Parallel.For` with `MaxDegreeOfParallelism = Environment.ProcessorCount` (4 on the target machine), only in
  CellLexicon (per stream) and Returns (similarity rows).
- With `--verbose`, a `Stopwatch` per stage prints to the console, never into the bundle.

---

## 13. Bundle contract (version 6)

### 13.1 Exact additions to `Assets/PreparedPatternSong.cs`
Insert after the `Version 5` comment and the `CurrentVersion` line. Every class is nested in `PreparedPatternSong`,
like `Pattern` and `Pass`. JSON field names equal the C# field names (System.Text.Json `IncludeFields`, Unity
`JsonUtility`).

```csharp
    // Version 6 adds the motif layer: the piece reduced to atoms (cell shapes with chirality) and the
    // transformations that place them (tonal or real transposition, mirror, augmentation, the parallel mode),
    // nested powers (a unit stated n times, each a step from the last), phrases and periods, and sections
    // related by returns with edits (A′ = A − bars 6–13 + coda). Every note is checked to decode from it.
    // Readers accept 5 and 6. A version-5 bundle has an empty layer (Schema 0): the wheels stay classic.
    public const int CurrentVersion=6,MinimumReadableVersion=5;
    public MotifLayer Motifs=new MotifLayer();
    public bool HasMotifs()=>Motifs!=null&&Motifs.Schema>=1&&Motifs.Nodes!=null&&Motifs.Nodes.Length>1;

    public static class MotifKinds { public const int Piece=0,Section=1,Phrase=2,Period=3,SubPhrase=4,Power=5,Unit=6,Coda=7,Intro=8,Arrival=9; }
    public static class MotifRelations { public const int Return=0,Power=1,Run=2,Doubling=3,Alternation=4,Liquidation=5,CrossReference=6; }
    public static class MotifSparks { public const int Inserted=0,Deleted=1,Substituted=2,Altered=3,Shifted=4,Lengthened=5,Octave=6,Ornament=7; }
    public static class MotifEdits { public const int Match=0,Delete=1,Insert=2,Substitute=3; }

    [Serializable] public sealed class MotifLayer
    {
        public int Schema;                                    // 0: none (version 5, or the analysis was disabled: Status); 1: this layout
        public string Status="",Formula="",Caption="";       // Formula: the piece line, then section and phrase lines, joined by '\n'
        public string[] Levels=Array.Empty<string>();        // names of node levels: Piece, Section, Phrase, Unit
        public int PrincipalStream=-1;                       // the stream the glyph follows by default (the melody)
        public MotifStream[] Streams=Array.Empty<MotifStream>();
        public MotifAtom[] Atoms=Array.Empty<MotifAtom>();
        public MotifParadigm[] Paradigms=Array.Empty<MotifParadigm>();
        public MotifNode[] Nodes=Array.Empty<MotifNode>();          // preorder; Nodes[0] = the piece; children contiguous
        public MotifCell[] Cells=Array.Empty<MotifCell>();          // grouped by stream (MotifStream.FirstCell, CellCount), by Start
        public MotifSpark[] Sparks=Array.Empty<MotifSpark>();       // grouped by cell (MotifCell.FirstSpark, SparkCount)
        public MotifRelation[] Relations=Array.Empty<MotifRelation>();
        public MotifEdit[] Edits=Array.Empty<MotifEdit>();          // return scripts (MotifRelation.FirstEdit, EditCount)
        public MotifStats Stats=new MotifStats();
    }
    // One monophonic line or texture. Kind: voice (a score voice), line (a lane's outer line), rest, texture,
    // both (both hands), drums. Staff and Voice come from a MuseScore import (-1 otherwise).
    [Serializable] public sealed class MotifStream
    {
        public int Id,Track,Channel,Staff=-1,Voice=-1,FirstCell,CellCount;
        public string Name="",Kind="";
        public bool Bass;
    }
    // A family of paradigms with one diminution signature, signs dropped (N neighbour, T turn, P passing or
    // sigh, A appoggiatura, Osc, Arp, Shape, Hold). The census is its chirality: members whose figure turns
    // upward (Positive) or downward (Negative), Neutral for achiral ones.
    [Serializable] public sealed class MotifAtom
    {
        public int Id,Paradigms,Members,Positive,Negative,Neutral;
        public string Kind="",Name="",Signature="";
    }
    // An exact orbit: its prototype (the first statement, Notes relative to its first note) and every cell
    // that is the prototype transposed, mirrored or augmented, plus variants with residual edits. Clock is
    // the glyph's pitch clock: 7 (scale steps) or 12 (semitones). Symmetry: the transpositions that fix a
    // texture shape ({0,6} a tritone, {0,3,6,9} a diminished seventh).
    [Serializable] public sealed class MotifParadigm
    {
        public int Id,Atom=-1,Stream,Members,Mirrored,Variants,Clock=7,Sign,PrototypeCell=-1;
        public string Letter="",Name="",Signature="";
        public double Beats;
        public bool Achiral;
        public MotifNote[] Notes=Array.Empty<MotifNote>();
        public int[] Symmetry=Array.Empty<int>();
    }
    // Onset and Length in beats from the cell start; Step and Semitones from the first note; Alter: the
    // chromatic alteration against the local scale; Chord: 0 the line's note, 1.. chord notes below it.
    // Role: the diminution token ("" structural, "N+", "N-", "P-", "T+", "A-", "Rep").
    [Serializable] public sealed class MotifNote
    {
        public double Onset,Length;
        public int Step,Semitones,Alter,Chord;
        public bool Tied;
        public string Role="";
    }
    // One cell of a stream: which paradigm it states and how (from the prototype). Degree is the scale degree
    // of its first note in the local key (0 = tonic; for Clock 12 the semitone above the tonic): the glyph's
    // angle. Real: the transposition is named chromatically. ModeChange: the parallel mode (P). Relation: the
    // power this cell is an iteration of. Text: the musician's name of the relation to its previous statement
    // ("tonal sequence up a second", "in the parallel minor", "an octave lower with C♮: ♭6 borrowed from E minor").
    [Serializable] public sealed class MotifCell
    {
        public int Stream,Paradigm,Node=-1,Steps,Semitones,Degree,FirstSpark=-1,SparkCount,Relation=-1;
        public double Start,End;
        public bool Inverted,Exact=true,Real,ModeChange;
        public float Scale=1;
        public string Text="";
    }
    // A residual edit: where the music departs from the formula (the white sparks). Note: the prototype note
    // it concerns (-1 for an inserted note). Tag names it ("C♮ = ♭6 from E minor", "held 13/16", "P+").
    [Serializable] public sealed class MotifSpark
    {
        public double Beat;
        public int Cell=-1,Kind,Pitch=-1,Note=-1;
        public string Tag="";
    }
    // The flattened tree. Level: 0 piece, 1 section, 2 phrase or period, 3 sub-phrase, power or unit (powers
    // nest; Depth is the tree depth). Prototype: the node this one restates (A for A′, a1 for a1′, the first
    // iteration for later ones) under Steps/Semitones/Inverted. Projected: inherited from the prototype through
    // the return's script. Omits: prototype children the copy leaves out ("a2, a1′"). Powers: Count statements,
    // each StepSteps/StepSemitones from the last; Iteration is a child's index in its parent power. Order: the
    // generator's order (0 translation, 1 loop, 2 alternation, n a cycle). Text: the node's formula line.
    [Serializable] public sealed class MotifNode
    {
        public int Id,Parent=-1,FirstChild=-1,ChildCount,Depth,Level,Kind;
        public double Start,End;
        public string Label="",Name="",Text="",Omits="";
        public int Prototype=-1,Relation=-1,Paradigm=-1,Steps,Semitones;
        public bool Inverted,Real,Projected;
        public int Count=1,Iteration,StepSteps,StepSemitones,Order;
        public float Bits,Evidence;
    }
    // A relation between spans or streams: Return (Node restates Source with the Edits script), Power (Count
    // statements stepped by Steps/Semitones), Run (a texture chain stepped by Semitones per event; Period > 0
    // when it cycles back to its pitch classes; Symmetry: the transpositions that fix its hand shape), Doubling
    // (StreamB doubles StreamA; Pattern lists the octave registers, "-24,-12"; Contrary marks mirror motion),
    // Alternation (Pattern "RRLL"), Liquidation (Pattern "8,4,2": the shrinking period in quarters of a tactus),
    // CrossReference (bar 16 = bar 6 a sixth higher). Text is the musician's sentence for captions and tours.
    [Serializable] public sealed class MotifRelation
    {
        public int Id,Kind,Level,Node=-1,Source=-1,StreamA=-1,StreamB=-1,Steps,Semitones,Count=1,Period,Order,FirstEdit=-1,EditCount;
        public double Start,End,SourceStart=-1,SourceEnd=-1;
        public bool Inverted,Real,ModeChange,CrossScale,Contrary;
        public float Bits,SavedBits;
        public string Text="",Pattern="";
        public int[] Symmetry=Array.Empty<int>();
    }
    [Serializable] public sealed class MotifEdit
    {
        public int Op;                                       // MotifEdits: match, delete (source not restated), insert (new target), substitute
        public double SourceStart=-1,SourceEnd=-1,TargetStart=-1,TargetEnd=-1;
        public float Bits;
    }
    // Description lengths in bits. Literal: every note coded alone; Entropy: order-0 entropy coder; Cells:
    // paradigms without powers or returns; Model: the full reduction (articulation excluded, reported apart).
    [Serializable] public sealed class MotifStats
    {
        public int Notes,Streams,Cells,Paradigms,Atoms,Nodes,Relations,Powers,Returns,MirroredCells,DerivedNotes;
        public float BitsLiteral,BitsEntropy,BitsCells,BitsModel,BitsResidual,BitsArticulation,Derived;
    }
```

Contract rules:
- Arrays of plain classes only; no dictionaries, polymorphism or jagged arrays; nesting depth ≤ 3 below
  `PreparedPatternSong`.
- Every field has an initialiser, so a v5 bundle deserialises to `Schema = 0` and empty arrays. Unity's
  serializer never yields null for a `[Serializable]` class field.
- `HasMotifs()` is a method, not a property, so System.Text.Json does not write it.

A JSON fragment of the étude (illustrative):
```json
"Motifs":{"Schema":1,"Status":"","Formula":"A · B · A′    A′ = A − (a2 a1′) + Coda\n...","Caption":"A · B · A′ (A′ = A − bars 6–13 + coda) · 1,932 notes → ...",
 "Levels":["Piece","Section","Phrase","Unit"],"PrincipalStream":0,
 "Nodes":[{"Id":0,"Parent":-1,"FirstChild":1,"ChildCount":3,"Depth":0,"Level":0,"Kind":0,"Start":4.0,"End":158.5,"Label":"Étude",...},
          {"Id":1,"Parent":0,"FirstChild":4,"ChildCount":4,"Depth":1,"Level":1,"Kind":1,"Start":4.0,"End":45.5,"Label":"A",...},
          ...
          {"Id":3,"Parent":0,"Depth":1,"Level":1,"Kind":1,"Start":126.5,"End":158.5,"Label":"A′","Prototype":1,"Relation":0,"Omits":"a2, a1′",...}],
 "Relations":[{"Id":0,"Kind":0,"Level":1,"Node":3,"Source":1,"Start":126.5,"End":158.5,"SourceStart":4.5,"SourceEnd":42.5,"FirstEdit":0,"EditCount":4,
               "Text":"A′ restates A from bar 1, leaving out bars 6–13 (a2, a1′); the coda branches from bar 20 at bar 73",...}],
 "Edits":[{"Op":0,"SourceStart":4.5,"SourceEnd":14.5,"TargetStart":126.5,"TargetEnd":136.5},
          {"Op":1,"SourceStart":14.5,"SourceEnd":30.5,"TargetStart":-1,"TargetEnd":-1},
          {"Op":0,"SourceStart":30.5,"SourceEnd":42.5,"TargetStart":136.5,"TargetEnd":148.5},
          {"Op":2,"SourceStart":-1,"SourceEnd":-1,"TargetStart":148.5,"TargetEnd":158.5}]}
```

### 13.2 Version gates and tools

| place | change |
|---|---|
| `Assets/SongLibraryPanel.cs:90` | `>=PreparedPatternSong.MinimumReadableVersion` (today `>=CurrentVersion`, which would hide every v5 bundle) |
| `Assets/MidiPlayer.cs:103`, `Assets/StemPlayback.cs:49,72` | unchanged: `Version<1 \|\| Version>CurrentVersion` rejects only future versions |
| `Assets/Editor/LyricModeValidation.cs:40` | `data.Version>=PreparedPatternSong.MinimumReadableVersion` (the lyric fixture bundle is not regenerated by the check) |
| `Assets/Editor/PatternWheelValidation.cs:36` | unchanged: the check regenerates its own fixture, which is v6 |
| `Tools/SongLibrary/common.py:63` | add `'Motifs'` to `MASTER_FIELDS`: a stem shows the master's structure, as it already shows the master's form and harmony |
| `Tools/SongLibrary/deploy_quest.py:34` | keep `BUNDLE_VERSION = 5`, comment `# PreparedPatternSong.MinimumReadableVersion` |
| `Tools/SongLibrary/story.py:113` | accept the new cue field `annotationAnchor` (§14.8) |

Rollout:
1. Ship the Unity build that reads 5 and 6, on desktop and Quest. Older builds reject v6.
2. Regenerate bundles one by one. **Unity never requires regenerating any bundle**: v5 bundles keep the classic
   wheels.
3. Regenerating changes a bundle's SHA-256. Republish its tour in the same step:
   - `Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/build_director_tour.py Tools/SongLibrary/directors/<song>.json`
     for score-ingested songs;
   - `publish_story.py PreparedSongs/Library/<song>` for the others.

   Both only restamp `patternsSha256` when the cues are unchanged. Narration audio is reused unless cue text
   changes.

---

## 14. Unity: the structure wheel

### 14.1 What replaces what, for bundles with `HasMotifs()`

| classic part | structure mode | rate |
|---|---|---|
| **Rack** (song unrolled) | kept; strips from level-1 nodes (copies share their prototype's hatch); phrase ticks from level 2 | per frame (as today) |
| **Pinion ring** | kept as **ring 1** (sections): arcs from level-1 nodes. A′ shares A's hatch. The **omission slit** shows on A's arc: A's omitted span (6.1–13.2) is drawn in a light hatch with a thin bridge chord between its ends, and A′'s arc carries a notch where those bars would be. The **ghost cursor** runs on A's arc while A′ plays and jumps across the slit. Return brackets replace group brackets (pop: (V C)′ ← (V C)) | one turn per song; now at nine o'clock |
| **Orbits** | replaced by **ring 2** (phrases or periods of the current section): arcs hatched by phrase identity (a projected or primed phrase uses its prototype's hatch); each period's generator sign at the arc middle (→ translation, a polygon with n sides for a cycle of order n, ⇄ involution, · identity); labels a1 a2 a1′ a3 or B1 … B5 when the arc is ≥ 24 px | one turn per section visit |
| **Planet** | replaced by **ring 3** (units of the current phrase, or its principal cells when it has no units): leaf units as arcs; powers as **staircases** (each iteration's arc stepped radially by its cumulative step × 3 px, outward rising, inward falling); nested powers and sub-phrases as brackets labelled `×2 ↑1` or `a3b`; a mini glyph (≥ 10 px) at each arc's middle, rotated and mirrored like the centre glyph; the phrase's chords as a colour band on the ring's inner edge (colour stays tonality) | one turn per phrase |
| **Moon** | replaced by the **centre glyph**: the playing cell's paradigm on the pitch clock (§14.2) | changes per cell |
| **Caption** | four lines: the piece formula; the current section or phrase line (`node.Text`); the current cell (atom name, relation text, for example "N⁺ upper-neighbour figure · tonal sequence up a second"); the compression line (§9 G3) | rewritten only when the path changes |

`FeaturedCenter` is the centre and `FeaturedRadius` the radius of ring 3's inner edge (about 0.34·ring, as the
planet today), so `PatternWheelValidation` and `StoryAnnotations` keep working. `ActiveFamilyNode` and
`ActiveGroup` are still computed from `Sections`.

A toggle "Structure wheel (motifs)" sits in the deck's panel, on by default when the bundle has motifs. Its value is
kept in `PlayerPrefs` as a per-viewer convenience. Off, or for a v5 bundle, the wheel is exactly the classic one.

In structure mode, the side panel's chapter buttons list the level-1 and level-2 nodes, hatched as on the rings.

### 14.2 The pitch-clock glyph
The paradigm's prototype is drawn in the glyph's local frame:
- **angle** = 360°·(Step mod Clock)/Clock, clockwise, with the prototype's first note at twelve o'clock;
- **radius** = r0 + (r1 − r0)·Onset/Beats: time spirals outward, r0 = 0.25R, r1 = R;
- a step (|Δ| = 1) is drawn as an arc along the clock, a leap as a chord;
- structural notes are filled beads, ornament notes (Role ≠ "") hollow beads, and a tied note is a short tail at r0;
- the clock face carries Clock ticks, with the tonic tick brighter.

Each occurrence is a pure transform of this cached geometry:

| cell field | drawing |
|---|---|
| Degree | rotate the glyph by 360°·Degree/Clock, so the first note sits on its scale degree. Two occurrences then differ by exactly their transposition: rotation is transposition |
| Inverted | `scale.x = −1` in the local frame (reflection about the radius through the first note): mirror is inversion, never a rotation (judge test: ν stays 0.293 from every rotation) |
| \|Steps\| ≥ 7 | a small `8vb` or `8va` tag, since a 7-step rotation is a full turn |
| Scale ≠ 1, or a Lengthened spark | the spiral's radial span is scaled by Scale (or the held head drawn longer) |
| sparks | white sparks at the edited note's angle and radius. An Altered spark is offset by ±360°/24 from its diatonic position, so the C♮ of bar 73 visibly sits a half step below the C♯ slot |
| Real (Clock 12) | the same with 12 positions; symmetric texture shapes become polygons: a tritone is a diameter, a diminished seventh a square |

The glyph also shows:
- a **playhead bead** travelling along the spiral at the playing position inside the cell;
- **cycle gear** (should): while a Run with Period > 0 plays, its polygon turns by the step angle at each event,
  so the 38–41 diameter turns 30° per sixteenth and closes after six;
- **two hands in opposite chirality** (should): while a Contrary doubling plays, a smaller mirrored glyph of the
  second stream sits at four o'clock (52.2–53.2).

### 14.3 The playing path
- The arc under the gate (nine o'clock) on each ring is lit, with `bloom.Arc` as for today's playing section.
- A thin radial "path" line joins the lit arcs to the glyph.
- The gate never moves; the rings turn.

### 14.4 `MotifWheelModel` (new, `Assets/MotifWheelModel.cs`, about 300 lines, shared by desktop and Quest)
```csharp
public sealed class MotifWheelModel
{
    public static MotifWheelModel For(PreparedPatternSong song);   // cached per bundle instance (ConditionalWeakTable)
    public bool Valid { get; }                                      // song.HasMotifs()
    public struct Path { public int Section, Phrase, Unit, Cell, Paradigm; public double SectionU, PhraseU, UnitU, CellU; public bool ChangedSinceLast; }
    public void Resolve(double beat, int featuredTrack, int featuredChannel, ref Path path);   // allocation-free
    public bool Ghost(double beat, out double sourceBeat, out bool jumped);                  // through the return's match edits
    public GlyphGeometry Glyph(int paradigm);                        // cached polyline (≤ 64 points), beads, roles
    public float GlyphAngle(int cell);                               // 360·Degree/Clock
    public bool GlyphMirrored(int cell);
    public int StreamFor(int track, int channel);                    // the lane's outer-line stream, else the principal stream
    public bool TryGetAnchor(string anchor, double beat, out AnchorTarget target);   // §14.8
}
```
At load (once per bundle, under 2 ms for the étude):
- per-node child ranges are already contiguous;
- per-stream cell slices;
- return relations by target node;
- label lookup.

Per frame, `Resolve` does:
- a binary search among the current node's children at each level (≤ 4 levels, ≤ log2 40 steps each);
- a binary search in the featured stream's cell slice;
- it sets `ChangedSinceLast` when any index changed.

`Ghost` walks ≤ 16 edits.

### 14.5 Desktop drawing and cost
- Rings 2 and 3 and the glyph are **retained child `VisualElement`s** of `WheelCanvas`, each with
  `usageHints = UsageHints.DynamicTransform`. Their `generateVisualContent` runs only when their content changes:
  - ring 2 when the section changes;
  - ring 3 when the phrase changes;
  - the glyph when the paradigm changes.

  Motion is only `style.rotate` (and `style.scale` for the mirror).
- Rack, ring 1 and the changers stay in the existing immediate `Draw`, at today's cost. In structure mode
  `DrawOrbits` and `DrawPlanet` are skipped, so ring 1's slit and ghost cost about what the orbits cost before.
- A small dynamic layer draws the playhead bead, the ghost cursor and ≤ 8 sparks per frame.
- Caption `Label`s change text only on `ChangedSinceLast`. Strings are cached per node and per cell.
- No LINQ, closures or allocations in `Tick` or `Draw`. Geometry arrays are pooled per bundle.
- New profiler marker `Perf.WheelsStructure = new("Resonance.PatternWheels.Structure")`, added to `Perf.Names`.
- **Budget**: ≤ 0.3 ms per frame added in Play Mode on the étude and on the generated fixture, including the
  key-change scenario of `PerformanceValidation`. A rebuild (section or phrase change) ≤ 1.5 ms, amortised.
- Levels whose arcs are < 3 px collapse to a tick band (LOD). Labels only on arcs ≥ 24 px (the existing rule).

### 14.6 Interaction
- Annulus picking: the radius picks the ring, the angle picks the node.
  - A click on an arc seeks to the node's start:
    `midi.Seek(midi.AudioTime(midi.Cycles.SecondsAt(node.Start)))`.
  - A click on the ghost cursor seeks to its source beat ("hear the original").
  - A click on the glyph seeks to the next occurrence of the playing paradigm.
- A hover tooltip gives the node's `Name` and `Text`, and for the slit, "bars 6–13 (a2, a1′) are left out in A′".

### 14.7 Quest (`Assets/Quest/PatternWheel3D.cs`)
- The same `MotifWheelModel` drives it.
- Ring 2 and ring 3 are one mesh each, built with the existing `Builder` and rebuilt only on a section or phrase
  change.
- Glyph meshes are cached per paradigm and instanced: `localRotation` for Degree, `localScale.x = −1` for
  Inverted. The `VrWheel` shader is `Cull Off` (`Assets/Resources/VrWheel.shader:20`).
- Sparks go into the existing `glowMesh`.
- The caption `TextBox` is updated only on a path change.
- A pinch on a ring seeks, as the rack does today.
- Budget ≤ 1 ms per frame on the headset; the flat panels stay in their unseen sink.

### 14.8 Director annotations
- **Cue field**: `SongDirector.Cue` gains `public string annotationAnchor;` (optional).
- **Grammar**: `^(section|phrase|node|omission|run|atom|relation):[^\s].{0,38}$` or `^(ghost|glyph)$`.
  `SongDirector.Read` and `story.py` (`validate_cues`) reject anything else. `build_director_tour.py` passes it
  through, since director cues are copied verbatim.

  | anchor | meaning |
  |---|---|
  | `section:A′` | the arc of the section with that label |
  | `phrase:a3` | a phrase or period arc in the current section (falling back to the first with that label) |
  | `node:A′/a1` | a label path |
  | `omission:A′` | the slit on the prototype's arc |
  | `run:B1` | the staircase of the power under that node |
  | `atom:N` | the glyph while a cell of that atom plays, otherwise its next arc on ring 3 |
  | `relation:<id>` | a relation's target span |
  | `ghost`, `glyph` | the ghost cursor, the centre glyph |

- `PatternWheelDeck.TryGetAnchor(string anchor, out Vector2 local)` resolves through `MotifWheelModel` and the
  current ring rotations to a point in the overlay's local space.
- `StoryAnnotations.LateUpdate`: when `cue.annotationTarget=="patterns"` and `annotationAnchor` is set and
  resolves, the arrow targets that point. Otherwise it keeps today's `FeaturedCenter + up·FeaturedRadius·0.65`.
- Étude tour (`Tools/SongLibrary/directors/chopin-op10-3.json`): the 62–67 cue ("A′: bars 6–13 cut") gains
  `"annotationAnchor": "omission:A′"`.

### 14.9 Unity validations
- New `Assets/Editor/MotifWheelValidation.cs`, menu **Tools → Resonance → Check motif wheel (generated ternary
  fixture)**, modelled on `PatternWheelValidation`. It writes and prepares `--write-fixture motif-ternary` and
  checks U1–U9 of §15.4.
- `PerformanceValidation` gains the scenario "Overview · motif wheel (ternary fixture)" at the fixture's A′ return
  and reports the new marker.

---

## 15. Acceptance tests

### 15.0 How the tests run
Real-piece tests run through `--sweep-motifs` with golden files in `Tools/PatternPrep/reference-forms/*.motifs.json`.
- A sweep locates its bundle by glob in `PreparedSongs/Library` and analyses that bundle's `aligned.mid` with
  `song.json` and `score-import.json`, in strict mode, writing nothing.
- `PreparedSongs` is git-ignored, so a missing bundle **skips** with a message; it does not fail.
- The étude's file holds the exact beats below. Tolerances are stated per assertion.
- "Should" items print WARN instead of failing. Everything else is a hard assertion.

Golden file shape:
```json
{ "title": "Chopin Op. 10 No. 3", "bundle": "Chopin---Etude-Op-10-No-3-in-E-major-*",
  "sections": [["A", 4.0, 45.5], ["B", 45.5, 126.5], ["A′", 126.5, 158.5]], "tolerance": 0.5,
  "return": { "node": "A′", "source": "A", "omits": "a2, a1′", "tolerance": 1.0,
              "edits": [["match", 4.5, 14.5, 126.5, 136.5], ["delete", 14.5, 30.5], ["match", 30.5, 42.5, 136.5, 148.5], ["insert", 148.5, 158.5]] },
  "phrases": { "A": [4.0, 14.5, 20.5, 30.5, 45.5], "B": [45.5, 62.5, 78.5, 94.5, 110.5, 126.5] },
  "paradigms": [ { "members": [5.5, 7.5, 11.5, 21.5, 23.5, 27.5, 127.5, 129.5, 133.5], "atom": "N", "sign": 1,
                   "steps": { "7.5": 1, "23.5": 1, "129.5": 1 } } ],
  "relations": [ { "kind": "Power", "start": 45.5, "count": 2, "stepSteps": 1, "nestedCount": 2 } ],
  "stats": { "notes": 1932, "maxModelOverLiteral": 0.3333, "maxModelOverEntropy": 0.77, "minDerived": 0.70 },
  "noise": [ { "seed": 1, "jitter": 24, "drop": 0.0, "extra": 0.0 }, { "seed": 2, "jitter": 30, "drop": 0.03, "extra": 0.02 },
             { "seed": 3, "jitter": 30, "drop": 0.03, "extra": 0.02 }, { "seed": 4, "jitter": 48, "drop": 0.05, "extra": 0.03 },
             { "seed": 5, "jitter": 48, "drop": 0.05, "extra": 0.03 }, { "seed": 6, "jitter": 48, "drop": 0.05, "extra": 0.03 } ],
  "noisePass": 5 }
```

### 15.1 The étude (`reference-forms/chopin-op10-3.motifs.json`)
Bundle beats, printed bars in brackets. Pickup [4.0, 4.5); bar b starts at 4.5 + 2(b−1).

| id | assertion |
|---|---|
| E1 form | exactly three level-1 nodes: **A [4.0, 45.5) (0.1–21.1), B [45.5, 126.5) (21.2–61.2), A′ [126.5, 158.5) (62.1–77.2)**, labels `A`, `B`, `A′`, ±0.5 beat. This beats the baseline: bar 21's downbeat closes A and B starts on its second beat, where today's bundle puts bar 21 under "Part A" with eight flat sections |
| E2 return | one level-1 Return, Node = A′, Source = A, edits in order: match [4.5, 14.5)→[126.5, 136.5) (1.1–5.2 → 62.1–66.2); **delete [14.5, 30.5) (6.1–13.2)**; match [30.5, 42.5)→[136.5, 148.5) (14.1–19.2 → 67.1–72.2), ±1 beat at the inner ends; insert [148.5, 158.5) (73.1–77.2), start ±1 beat. `A′.Omits == "a2, a1′"`. The first match starts at 4.5: the reprise restates A from bar 1, not from bar 9 |
| E3 phrases of A | children a1 [4.0, 14.5), a2 [14.5, 20.5), a1′ [20.5, 30.5), a3 [30.5, 45.5), ±0.5 beat. a1′.Prototype = a1, with one substitution spark at 20.5 (9.1, G♯4 added). a3 has two SubPhrase children split at 36.5 ±1 (17.1): Leichtentritt's (5+3)+(5+7) |
| E4 children of A′ | a1 [126.5, 136.5) Projected, Prototype a1; a3′ [136.5, 148.5 ±1) Projected, Prototype a3; Coda [148.5 ±1, 158.5) Kind Coda |
| E5 periods of B | five level-2 children starting at 45.5, 62.5, 78.5, 94.5, 110.5 (21.2, 30, 38, 46, 54), ±1 beat, labels B1 … B5 |
| E6 B1 | a Power node in B1 starting at 45.5 ±1, Count 2, StepSteps +1 (tonal), whose two iterations are Power nodes with Count 2, step 0 and 4-beat units: `(U²)² ↑1` |
| E7 B2 | a Power (or Return) over [62.5, 78.5): Count 2, StepSemitones +2, 8-beat unit. A cell or relation starting at 64.5 or 72.5 (31.1, 35.1) has ModeChange, its Text containing "parallel minor" |
| E8 B3 | a Power at 78.5 ±0.5: 2-beat unit, Count 3, StepSemitones −1 (38 → 39 → 40). A Run on a lower-staff texture inside [78.5, 88.5): Semitones −1 per event, Period 6, Symmetry {0, 6}. Should: an Alternation in [86.5, 88.5) with Pattern "RRLL"; CrossScale on the T−1 run |
| E9 B4 | a Doubling (StreamA upper, StreamB lower) covering ≥ [95.0, 107.5), Pattern containing −24, with Contrary inside [107.5, 110.5) (52.2–53.2). A Run with Order 2 (alternating sixths) inside [107.5, 110.5). Should: a Power 96.5 → 100.5 (47–48 → 49–50), StepSemitones −1 |
| E10 B5 | a Liquidation covering ≥ [110.5, 126.5) with Pattern "8,4,2". A Power with Count 2 and step 0 at 118.5 (58–59). Should: `(λ²)² ↓5c` at 110.5 |
| E11 neighbour atom | an Atom with Kind "N", Positive ≥ 9, Negative ≥ 2. Its positive principal-stream paradigm has members starting exactly at **5.5, 7.5, 11.5, 21.5, 23.5, 27.5, 127.5, 129.5, 133.5** (1.2, 2.2, 4.2, 9.2, 10.2, 12.2, 62.2, 63.2, 65.2); those at 7.5, 23.5 and 129.5 have Steps +1 and Real false. Its negative paradigm has members at **4.5 and 126.5** (1.1, 62.1). The two paradigms are distinct, and neither is coded as an inversion of the other (same family, opposite sign, not a literal mirror) |
| E12 exact mirror (should) | the oscillation paradigm with members at 108.5 and 109.5 (53.1, 53.2) has Inverted members at 122.5, 123.5, 124.5 (60.1, 60.2, 61.1) |
| E13 sigh | **β′** (sigh head): exact members at 36.5 and 142.5 (17.1, 70.1); members at **148.5 and 150.5** (73.1, 74.1) with Steps −7 and exactly one Altered spark each (Pitch 60, C4; Tag containing "♭6"). **β**: members at 38.5, 40.5 (Steps −2), 144.5, 146.5 (Steps −2), i.e. 18, 19, 71, 72. Both lie in an Atom of Kind "P" named "sigh" (not "N"). Power relations: Count 2 with StepSteps −2 at 38.5 and at 144.5; Count 2 with step 0 at 148.5 (the coda). The relation Text at 148.5 contains "octave lower" and "♭6". Should: the window at 154.5 (76.1, its head tied from 75.1) is a variant member of β′ (Exact false) with a Lengthened spark ("held 13/16") |
| E14 rising sequence | a Power at 30.5 (14.1), Count 2, StepSteps +2. A CrossReference starting at 34.5 (16.1) with SourceStart 14.5 (6.1), Steps +5, Text containing "sixth". Should: Text at 32.5 containing "leading tone of C♯ minor" |
| E15 distinctness | no paradigm has members both in the left hand's 38–41 dyads [78.5, 86.5) and in the 58–59 E–D♯–C♯ figure [118.5, 122.5); the turn paradigm (prototype at 14.5, 6.1) has no member at 64.5 or 66.5 (the 32.1 and 33.1 arpeggios); no sigh paradigm lies in the N atom |
| E16 formula | the first line of `Formula` starts with "A · B · A′"; `Caption` contains "A′ = A" and "6–13"; `Stats.Notes` = 1932 |
| E17 compression | BitsModel ≤ BitsLiteral / 3.0 and ≤ BitsEntropy / 1.3; Derived ≥ 0.70; MirroredCells ≥ 3 |
| E18 integrity | strict Verify passes; two runs are byte-identical; the motif stage takes ≤ 1.5 s (warm Release) |
| E19 noise | the six seeded perturbations of the golden file (jitter re-quantised to 16ths, notes dropped and added, durations ±20%; transcription mode with tolerance 0.125 beat; the import's voices and tpc removed, staves kept as lanes). On **≥ 5 of 6**, level 1 is A B A′, its boundaries are within ±2 beats of 45.5 and 126.5, and the delete edit is within ±2 beats of [14.5, 30.5). Measured in §0.4 for stage E: with phrase edges given, 7 of 7 exact; with none, 7 of 7 within one bar (7.1–14.2 or 6.2–14.1) |

### 15.2 The Nocturne (`reference-forms/chopin-nocturne-op9-1.motifs.json`)
Bundle `F-Chopin---Nocturne-Op-9-No-1-in-B-flat-minor-Rubinstein-*`: MIDI only, 6/4. Measure 0 is the 3/4 pickup
and bar b ≥ 1 starts at beat 3 + 6(b−1). Bars below are measure indices.

| id | assertion |
|---|---|
| N1 form | three level-1 sections A, B, A′ (a Coda child of A′ allowed). The A/B boundary is at bar 18 or 19 (beats 105–111). A′ starts at bar 70 ±1 (beat 417 ±6). Today's bundle puts bars 19–49 in "Part A" |
| N2 reprise | a Return A′ ← A whose match edits map source bars ≈ 8–18 onto ≈ 70–80 (±1 bar), with ≥ 6 bars before bar 8 deleted (the head-deleted reprise found by alignment-recursion, mdl-grammar and transformation-group), `Omits` non-empty. Today's C′ D′ M become one relation |
| N3 fewer, truer families | the distinct level-2 prototypes (level-2 nodes neither Projected nor with a Prototype) number **≤ 9**, against today's 14 families. Inside bars 18–52 a Return or Power has an 8-bar (±1) unit and Count ≥ 2: the refrain that today is F G F′ H F H F′ |
| N4 no new families in the reprise | inside A′ (beat ≥ 417), every level-2 node except the coda is Projected |
| N5 code | BitsModel / Notes ≤ 7.0 (mdl-grammar measured 5.54); strict Verify passes; ≤ 3 s |

### 15.3 Pop
`reference-forms/ticket-to-ride.motifs.json` (bundle `Ticket-to-Ride---The-Beatles`: 4/4, 101 bars, reviewed
`SectionBoundaries`).

| id | assertion |
|---|---|
| T1 sections unchanged | level 1 is `data.Sections` exactly: 12 nodes with the same Start, End and labels. `FormGrammar` is still "In (V C)×2 Br (V′ C′) Br (V′ C′) Out" |
| T2 returns | ≥ 3 Return relations among verse + chorus spans with ≥ 75% matched beats, including one from the first verse + chorus (bar 5) to the second (bar 21) (mdl-grammar measured 20–35 = 4–19). Should: bars 63–87 ← 38–62 (bridge, verse, chorus) |
| T3 loops | a Power with a unit ≤ 2 bars and Count ≥ 6 inside the Outro (bar 88 on), on the bass or a drum stream (measured: riff ^25, groove ^13). Should: every Verse and Chorus contains a Power with Count ≥ 2 |
| T4 code | BitsModel / Notes ≤ 3.5 (measured 2.94); strict Verify passes; ≤ 3 s |
| T5 Umbrella (sanity) | runs; Verify passes; level 1 = `data.Sections`; ≥ 1 Power with Count ≥ 4 |
| P1 synthetic pop (self-test F19) | see §16 |

### 15.4 Unity (`MotifWheelValidation`, `PatternWheelValidation`, `PerformanceValidation`)

| id | assertion |
|---|---|
| U1 | with an unregenerated library, the song list shows the same songs as before the change (v5 bundles listed through `MinimumReadableVersion`) |
| U2 | the generated `motif-ternary` fixture loads: Version 6, `HasMotifs()` |
| U3 | at 12 sampled beats the lit arcs of rings 1–3 are the model's `Resolve` path, and the rings advance monotonically while playing |
| U4 | a member with Steps +1 on Clock 7 is drawn rotated 360/7° ± 0.5° from its prototype; an Inverted member has `scale.x = −1`; an Altered spark is drawn on the coda cell |
| U5 | during the fixture's A′, `Ghost(beat)` maps into A and jumps across the omitted span exactly once |
| U6 | `TryGetAnchor("omission:A′")` returns a point on ring 1 inside the omitted span's angle; a cue with that anchor points the story arrow there |
| U7 | a click on a ring arc seeks to that node's Start (within one frame) |
| U8 | a v5 bundle renders classic with the toggle on, without exceptions |
| U9 | `PatternWheelValidation` and `LyricModeValidation` pass; `PerformanceValidation` shows `Resonance.PatternWheels.Structure` ≤ 0.3 ms mean and no per-frame GC allocation after load |
| U10 (manual) | the Quest build compiles. On the headset, with the user wearing it (Guardian holds the app otherwise), the étude shows the rings and glyph at 72 fps |

---

## 16. PatternPrep self-test fixtures (`SelfTests.Motifs.cs`)

Conventions:
- All fixtures use `SelfTests.Score` (2/4 or 4/4, C major unless stated, 120 bpm).
- Tracks: melody on track 3 channel 3, left hand or bass on track 2 channel 2, chords on track 1 channel 1, drums on
  track 4 channel 10.
- A small helper `Line(score, startBeat, "C4/8 B3/16 C4/16 D4/16*5 …")` writes a monophonic line. The notation is
  pitch/denominator, with `*n` for n units.
- Every fixture also asserts Verify (F22) and determinism (F23).

| id | construction | assertions |
|---|---|---|
| F1 neighbour chirality | 2/4. Lower neighbour `E4/8 D4/16 E4/16` on beat 1, then upper neighbour `F4/16*5 G4/16 G4/16 F4/16` from beat 2 across the barline, its t+1 copy from the next beat 2, an arrival C4 (1 beat over C2). Bars 4–6 repeat bars 1–3 | an Atom of Kind "N" with Positive ≥ 4 and Negative ≥ 2; two distinct paradigms (the lower one has 8th + 16ths); no Inverted member; the t+1 copy has Steps +1 |
| F2 exact mirror | `E4 F4 E4 F4` (16ths) twice, later `D5 C5 D5 C5` twice | one paradigm; the later members Inverted; Mirrored = 2; Text contains "mirrored" |
| F3 tonal sequence | 4/4 `C4 D4 E4 C4`, `D4 E4 F4 D4`, `E4 F4 G4 E4` (quarters; semitone patterns differ) | one paradigm with Steps 0, +1, +2, Real false; a Power with Count 3, StepSteps +1; Text "tonal sequence up a second" |
| F4 tonicizing sequence | 2/4 `G4 A4 A4 G4 F4 G4 E4 F4` over C7 → F, then `B4 C5 C5 B4 A4 B4 G♯4 A4` over E7 → Am | Steps +2, Text contains "leading tone of A minor" |
| F5 parallel minor | dotted thirds `E5/C5` (dotted 8th, 16th) `D5/B4` (dotted 8th, 16th) over C major, then the same with E♭5 over C minor | ModeChange; Text contains "parallel minor" |
| F6 modal mixture | sigh head `E5/16*5 D5/16 C5/16 A4/16` over F (bars 1–2); 2 bars of other material; the same an octave lower with A♭3 (bars 5–6) | Steps −7; one Altered spark, Pitch 56, Tag contains "♭6"; Atom Kind "P" |
| F7 tritone cycle | 24 left-hand sixteenth dyads B3/F3, B♭3/E3, A3/E♭3, … each T−1 | a Run with Semitones −1, Count 24, Period 6, Order 6; Symmetry {0, 6} |
| F8 dim7 cycle | 12 quarter-note diminished sevenths, each T−1 | Symmetry {0, 3, 6, 9}; Period 3 |
| F9 alternation | {C5, E5} ↔ {G4, B♭4} ×8 (16ths) | a Run with Order 2 |
| F10 doubling, contrary | 3 bars of RH dyads; the LH equals RH −24 for 2 bars, then alternates −24 and −12 so that its top rises while the RH falls (4 events) | a Doubling covering ≥ 90% of LH notes, Pattern containing −24; Contrary over the last ≥ 4 events |
| F11 étude-shaped ternary (`motif-ternary`, also the Unity fixture) | 2/4, C major: the étude's principal line and bass for bars 0–20 and 62–77, transposed −4 semitones (public domain). B is replaced by 20 synthetic bars: a 2-bar sixths unit U ×2 then t+1 ×2; 4 bars of LH tritone dyads T−1 per 16th; a 2-bar alternating-sixths unit ×2; 4 bars liquidating 8 → 4 → 2 | level 1 A B A′; one Return whose delete is exactly a2 + a1′ (±1 beat); Omits "a2, a1′"; A's phrases 5 + 3 + 5 + 7 bars (+ pickup); a3's sub-phrase cut at the sigh head; B1 = (U²)² ↑1; tritone Run with Period 6; two coda cells with Steps −7 and an Altered spark (A♭, "♭6"); the lengthened-head variant |
| F12 head-deleted reprise | x (8 bars) y (12) \| B (16, new material) \| y (12) coda (4) | Return deleting x; Omits = x's label; level 1 A B A′ |
| F13 equal-content tie | F11 with a1′ made identical to a1 (no exact-surface hint) | the delete is still a2 + a1′ (phrase edges, then the earliest source) |
| F14 ornamented variant | a 1-bar cell, then its restatement with one added passing 32nd | same paradigm, Exact false, one Inserted spark tagged "P+" or "P-" |
| F15 liquidation | 2-bar unit, then 1 bar ×2, then ½ bar ×4, then 1 beat ×8 | a Liquidation over ≥ 3 bars whose Pattern decreases to ≤ ¼ of its start |
| F16 nested power | a 2-bar unit U ×2, then U t+1 ×2 | Power(Count 2, StepSteps +1) whose iterations are Power(Count 2, step 0) |
| F17 two-cell unit | bars of tritone pairs (+5, −1, +5) stepping −1 semitone per bar, 4 bars | a Power with a 1-bar unit, Count 4, StepSemitones −1, Real |
| F18 distinctness | (a) `x, x−1, x−2, x−4` against `x, x+1, x+1, x`; (b) LH chromatic dyads 4 per beat against a melodic E D♯ C♯ (16th, 8th, 16th); (c) interlocking C♯–E–G–B♭ arpeggios between the hands against a turn `x x+1 x−1 x`; each figure stated ≥ 2 times | no shared paradigm in any pair; (a)'s two lie in different atoms |
| F19 pop | the existing `PopScore` | level 1 = the 8 sections; one Return (V2 C2) ← (V1 C1) spanning both; the verse holds a Power with Count 2 (4-bar progression ×2); C3 ← C1 with Semitones +2; drums: a Power with Count ≥ 8; the existing pop assertions (SelfTests.cs:237) still pass |
| F20 drums | 8 bars of one groove, 1 fill bar, 8 bars of the groove | a drum Power with Count 8; the fill a variant or literal; DrumBars and DrumFamilies unchanged |
| F21 noise | F11 perturbed with `new Random(7)`: onset jitter σ = 1/8 beat re-quantised to 16ths, 3% dropped, 2% spurious (±1, ±2, +12 semitones); transcription mode (tolerance 0.125); voices removed | level 1 A B A′, boundaries ±1 beat; delete ±2 beats of a2 + a1′ |
| F22 Verify | every fixture; then one decoded residual is mutated through a test hook | passes; the mutation throws `InvalidDataException` |
| F23 determinism | F11 twice | byte-identical serialised `MotifLayer` |
| F24 contract | serialise and deserialise `MotifLayer` with System.Text.Json (`IncludeFields`); deserialise v5-shaped JSON without "Motifs" | round-trip equality; `HasMotifs` not written; field names as §13.1; v5 gives Schema 0 |
| F25 arrivals | a 16-bar period: arrivals at bars 4 (half, V), 8 (full), 12 (half), 16 (full); held notes inside the phrases decorated by neighbours | phrases 4 + 4 + 4 + 4; no cut at a decorated held note |
| F26 phase | a figure starting on beat 2 and crossing the barline, ×6 | its cells start on beat 2 |
| F27 lengthened head | sigh head held 5/16, then restated held 13/16 | same paradigm, Exact false, Lengthened spark |
| F28 spelling | B♭ minor with the leading tone (MIDI only) | spelled A (letter 5), not B𝄫 |

### 16.1 Commands
From the repository root (`C:\Users\johnb\Desktop\Resonance-2`):
```
dotnet run --project Tools/PatternPrep -- --self-test                       # existing fixtures + F1–F28
dotnet run --project Tools/PatternPrep -- --self-test-motifs --verbose      # motif fixtures only, with tree dumps and stage timings
dotnet run --project Tools/PatternPrep -- --write-fixture motif-ternary Temp/MotifWheel/ternary.mid
dotnet run --project Tools/PatternPrep -- --motifs-report "PreparedSongs/Library/<song>/aligned.mid"     # dry run: tree, formula, relations, stats
dotnet run --project Tools/PatternPrep -- --sweep-motifs Tools/PatternPrep/reference-forms/chopin-op10-3.motifs.json Tools/PatternPrep/reference-forms/chopin-nocturne-op9-1.motifs.json Tools/PatternPrep/reference-forms/ticket-to-ride.motifs.json
dotnet run --project Tools/PatternPrep -- --strict-motifs "PreparedSongs/Library/<song>/aligned.mid"     # regenerate one bundle as v6
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/build_director_tour.py Tools/SongLibrary/directors/chopin-op10-3.json   # restamp the étude tour
```
Then in Unity Play Mode:
- Tools → Resonance → **Check motif wheel (generated ternary fixture)**;
- Tools → Resonance → **Check pattern wheels (generated song)**;
- Tools → Resonance → **Measure frame time (overview, lyric mode, key change)**.

The design-stage evidence scripts (§0.4) are kept beside this spec (SongPrep venv, `-I`):
- `scratchpad/design/final/align_check.py <import.json>…`
- `scratchpad/design/final/arrivals_check.py <import.json>…`

---

## 17. Implementation steps (in order; each ends green)

1. **Contract and gates.**
   - Add §13.1 to `PreparedPatternSong.cs`.
   - Update the gates: `SongLibraryPanel.cs:90`, `LyricModeValidation.cs:40`, the `deploy_quest.py` comment,
     `MASTER_FIELDS`.
   - Add `SongDirector.Cue.annotationAnchor` with validation in `SongDirector.Read` and `story.py`.
   - Build desktop and Quest; check U1 and U8 on the unregenerated library; ship both builds.
2. **Ingest.**
   - `Motifs/MotifSettings.cs`, `MotifInput.cs` (grid, score import, quantiser), `Spelling.cs`, `MotifStreams.cs`,
     `TransformGroup.cs`.
   - Fixtures F24, F28 and the transform and naming units (F3–F5 naming parts).
3. **Reduction and cells.**
   - `Diminution.cs` (F25), `CellLexicon.cs`.
   - Fixtures F1–F6, F14, F18, F26, F27.
4. **Iteration.**
   - `Iteration.cs`.
   - Fixtures F7–F10, F15–F17, F20.
5. **Returns.**
   - `Returns.cs`: similarity, θ, discovery, trimming, hull, anchored DP, gap sliding, sections, snaps, pop mode,
     internal returns.
   - Fixtures F11–F13 (level 1), F19, F21.
6. **Hierarchy.**
   - `Hierarchy.cs`.
   - The full F11 assertions.
7. **Code, verify, export.**
   - `MotifCode.cs`, `MotifVerify.cs`, `MotifAnalysis.cs`; Program.cs wiring; `--motifs-report`, `--sweep-motifs`,
     the golden files.
   - Fixtures F22, F23.
   - Run the étude, Nocturne, Ticket to Ride and Umbrella sweeps, plus a Rach2 Adagio report. Tune only inside
     the §10 ranges, and record any change in the golden files.
8. **Desktop wheel.**
   - `MotifWheelModel.cs`; structure mode in `PatternWheelDeck.cs` (retained layers, glyph, slit, ghost, caption,
     picking, anchors); `Perf.WheelsStructure`.
   - `MotifWheelValidation.cs` and the `PerformanceValidation` scenario (U2–U9).
9. **Quest wheel.** Structure mode in `PatternWheel3D.cs` (U10).
10. **Documentation.** A "Motif reduction and the structure wheel" section in `Docs/PATTERN-WHEELS.md`; the
    `Tools/PatternPrep/README.md` modes.
11. **Library migration.**
    - Regenerate each bundle with `--strict-motifs`, one at a time.
    - Republish its tour (§13.2).
    - Redeploy the Quest songs with `deploy_quest.py`.

Expected size: about 3,900 lines of C# in PatternPrep (including about 700 of tests) and about 900 in Unity.

---

## 18. Risks and mitigations

| risk | mitigation |
|---|---|
| false identities (over-merging) | contour invariance, edit cap, the 0.2·events distance, MDL gate; fixtures F18 and E15 |
| arrivals degrade under transcription noise (measured: B, C, D in §0.4) | internal-return edges also cut phrases; the 2-bar minimum; the noise gate (E19) checks only level 1 and the omission, where the noisy runs were 7 of 7 |
| gap placement ambiguity | gap sliding with an explicit tie-break (§7.5), measured on 7 inputs; fixture F13 removes the exact-surface hint |
| MIDI-only spelling errors | real naming in chromatic windows; Clock 12 glyphs; spelling never changes structure, only names |
| voice crossing (32–37) and cross-voice lines | score voices when imported; the principal line never leaves its voice; skyline errors cost residual bits only |
| pop regressions | level 1 = `data.Sections` for pop and reviewed forms; FormGrammar, Groups and Sections untouched; `PatternWheelValidation` unchanged |
| version 6 fallout | `MinimumReadableVersion`; Unity ships first; stories restamped in the same step; Quest redeployed |
| bundle size and Unity load time | export caps (§9 G5); measure the Rach2 Allegro in step 7 |
| frame time | retained layers, rotations only, marker and budget, LOD |
| implementation size | staged steps with fixtures per stage; fail-soft by default so a motif bug never blocks the library; strict in tests |
| overfitting to the étude | the same constants on the Nocturne, Ticket to Ride, Umbrella and the Rach2 Adagio report; the noise sweep; `song.json` overrides only for experiments |
| Verify complexity | the articulation code carries exact integers; every cell keeps its source note indices during analysis, so decode is tested per stage from step 3 |

---

## Appendix A. Expected étude result

Measured by the base design (transformation-group) and the grafts, plus the §0.4 checks. Runtime labels use Greek
letters by first appearance; descriptive names are used here.

```
Étude [4.0, 158.5)
├─ A   [4.0, 45.5)     0.1–21.1
│  ├─ a1  [4.0, 14.5)    ⌐B3 · N⁻(1.1) · N⁺(1.2)² ↑1 · μ(3.2, falling line) · N⁺(4.2) · ⊙E(5.2, full)
│  ├─ a2  [14.5, 20.5)   T(6.1)² ↑1 · bar 7 · bar 8 ⊙B(8.2, half)            (excursion toward B major)
│  ├─ a1′ [20.5, 30.5)   = a1, bar 9 adds G♯4 (one substitution)
│  └─ a3  [30.5, 45.5)
│     ├─ a3a [30.5, 36.5)  (N T)(14)² ↑2 [15: B♯ = leading tone of C♯ minor] · 16 = 6 ↑5 (cross-reference)
│     └─ a3b [36.5, 45.5)  S′(17) · S(18)² ↓2 · 20 (cadential six-four) · ⊙E(21.1, full)
├─ B   [45.5, 126.5)   21.2–61.2   poco più animato
│  ├─ B1 [45.5, 62.5)   (U²)² ↑1          U = the 2-bar sixths motif from 21.2              translation (→)
│  ├─ B2 [62.5, 78.5)   W · W ↑2c         W = 30 · 31[P] · 32 · 33 (parallel minor in 31, 35)  translation (→)
│  ├─ B3 [78.5, 94.5)   (bar)³ ↓1c · 41 · (sixths ↓6c, RRLL) …   LH tritones T−1 per 16th: a 6-cycle; RH T+4 per pair: a 3-cycle; cross-scale
│  ├─ B4 [94.5, 110.5)  LH doubles RH (−24) · (2 bars)² ↓1c · … · sixths ⇄ (order 2), hands contrary 52.2–53.2
│  └─ B5 [110.5, 126.5) (λ²)² ↓5c · (58)² · oscillation (53's mirrored an octave lower) · liquidation 8 → 4 → 2
└─ A′  [126.5, 158.5)  62.1–77.2 = A − (a2 a1′) + Coda
   ├─ a1   [126.5, 136.5)  projected
   ├─ a3′  [136.5, 148.5)  projected (a3 without its cadence)
   └─ Coda [148.5, 158.5)  (S′ ↓8ve [♭6])² · S′ (held 13/16, ends on B3) · ⊙E(77.1)
```

Formula lines (the shape of `MotifLayer.Formula`):
```
Étude = A · B · A′          A′ = A − (a2 a1′) + Coda   (bars 6–13 left out)
A  = a1 · a2 · a1′ · a3      a1′ = a1 (bar 9 adds G♯4)
B  = B1 · B2 · B3 · B4 · B5  → → ⬡ ⇄ ·   (translation, translation, cycle, involution, identity)
B1 = (U²)² ↑1                B2 = W · W ↑2c
Co = (S′ ↓8ve [♭6])² · S′ (held 13/16) · ⊙
```

The B line is the user's hearing:
- two swelling moments (translations upward);
- dissonant spinning (finite cycles: tritone 6, augmented triad 3);
- resolved by the alternating sixths (an involution);
- stabilising (identity) before A′.

Atom families: N (neighbour: + ×9 on the upper figure, − on 1.1 and 62.1), T (turn), P (sigh: 17, 18–19, 70–72,
73–74, 75–76), A (B1's appoggiaturas), Osc (53, mirrored at 60–61), Shape (tritone {0,6}; double sixths).

## Appendix B. Formula notation

| symbol | meaning |
|---|---|
| `·` | then |
| `Xⁿ ↑k` / `↓k` | X stated n times, each k scale steps higher or lower (tonal) |
| `↑kc` | k semitones (real) |
| `(…)` | grouping; `(U²)² ↑1` is a nested power |
| `X′` | X again with edits |
| `X − (…) + Coda` | the parts left out and the part added |
| `X̄` | mirror (inversion) |
| `N⁺` / `N⁻` | atom with upper or lower chirality |
| `[♭6]`, `[P]` | named alteration (modal mixture) or parallel mode |
| `↓8ve` | an octave lower |
| `⊙` | arrival (cadence) |
| `⌐` | anacrusis |
| `⟂` | two streams in opposite chirality at once |

Generator order signs on the wheel:

| sign | meaning |
|---|---|
| → | translation |
| polygon with n sides | a cycle of order n |
| ⇄ | involution |
| · | identity (loop) |

## Deviations during implementation

- §13.1 (step 1): the `Motifs` field, `HasMotifs()` and the motif classes follow the `Version` field instead of
  sitting directly under the `CurrentVersion` line. System.Text.Json writes fields in declaration order, and
  `SongLibraryPanel.CurrentBundle` and `deploy_quest.py` read `"Version"` from a bundle's first 64 KB: a motif layer
  of 0.2–4 MB written ahead of it would hide every v6 bundle. `Version` stays the first key of every bundle.
- §14.8 (step 1): an empty `annotationAnchor` is no anchor, like a missing one: `SongDirector.Read` and `validate_cues`
  accept it instead of rejecting it as outside the grammar. Every other value must match the grammar against the whole
  string (`\z` in C#, `re.fullmatch` in Python), since `$` alone also accepts a trailing newline.
