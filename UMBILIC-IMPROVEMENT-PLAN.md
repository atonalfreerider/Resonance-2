# Umbilic Torus visualizer: evaluation and improvement plan

Implementation follow-up: the foundation, explorer, sequential grammar lessons, and MIDI improvements have been implemented. See [implementation notes](IMPLEMENTATION-NOTES.md) for shipped behavior, verification, and explicitly deferred features. The assessment below records the original pre-change findings.

Reviewed September 18, 2026. Scope: evaluate the existing project and propose improvements; no runtime code, scenes, packages, or reference documents changed.

## Assessment

The project is a promising instrument-style visualization prototype. It already connects procedural torus geometry, eight registers of notes, keyboard input, MIDI-file playback, sine-wave audio, animated interval lines, translucent harmonic regions, and animated key changes. The next milestone should make the geometry explainable and musically dependable.

The defining color convention is present: I/key is blue, IV is red, V is green. Preserve this convention. Distinguish the selected tonal center, selected surface, sounding chord, and inferred key: these are different states and should not implicitly replace one another.

Evidence: reviewed the nine project C# files, scene configuration, package/render settings, README, repository preview, and both supplied documents. Unity CLI confirms that Resonance-2 is running without the Pipeline package. Live evaluation through that API is unavailable. A native screenshot attempt failed with `SetIsBorderRequired ... No such interface supported`; the repository preview is historical, not evidence of the current runtime. No play-mode tests, audio listening, build, or performance measurements were performed. Findings below distinguish source-level defects from validation work.

The supplied documents are design references, not instructions authorizing implementation. Their proposed stylistic rules should be represented as a selectable interpretation, with their mathematical definitions reconciled first.

## Findings tied to the implementation

| Priority | Finding and evidence | Consequence / response |
| --- | --- | --- |
| P0 | Held-line geometry becomes stale. `Chord.cs:40` generates straight-line positions at initialization. `Main.cs:603` updates existing third-line colors only; `Main.cs:623` skips existing fifths entirely. | During key animation, notes move but their held interval lines retain old base positions. Recompute positions on geometry changes; refresh fifth colors and intensity as well. |
| P0 | Key changes can accumulate an incorrect displacement. `Main.cs:356–411` stops the old coroutine, retains its partial visual pose, and computes the next displacement from `currentKey`, which updates only at completion. | Rapid retargeting may drift from the intended orientation. Resolve every key to a canonical target pose and interpolate from the current pose; commit musical state independently of interpolation. |
| P0 | The musical model is implicit inside rendering. `Main.cs:274–287` selects major/minor regions by key-relative intervals; `PlayKeys` classifies pairs as octaves, major thirds, or fifths. | There is no explicit surface identity, local object model, grammar evaluator, or trace of movement. Minor-third `d` and major-seventh `l` objects are not represented as first-class relationships. |
| P1 | Semantic colors mix with performance effects. `Main.cs:415` blends note hues with other sounding notes; `Main.cs:746` keeps label roles fixed. | A blue tonic label can accompany a differently colored tonic note. Keep a stable role-colored outline/label, and put amplitude and relational effects in a separate glow layer. |
| P1 | Mode is discarded. `MidiPlayer.cs:122` uses major/minor to calculate a root, then stores only that root. The region renderer continues using its major-key pattern. | A minor key signature does not produce a distinct minor interpretation. Store tonic, mode, signature/diatonic collection, and provenance explicitly. |
| P1 | MIDI playback loses event semantics. `MidiPlayer.cs:111–158` tracks activity only by pitch; channels and sustain controllers are not retained. Playback samples 10 ms snapshots once per rendered frame. | Same-pitch voices on different channels can silence each other; pedal behavior is missing; short notes can disappear between sampled frames. Schedule ordered events and track voices by channel and note, with explicit overlap and pedal policies. |
| P1 | Import and transport are prototype-level. Scene `midiPath` points to a user-specific absolute path; `LoadMidi` has no import error handling. Space restarts playback. | Add file selection, error feedback, play/pause/stop, seek, loop, and a master mute. Missing key metadata must not silently imply A as an inferred result. |
| P1 | Geometry/resource churn. `Main.cs:333` replaces meshes on each geometry update without explicit destruction/reuse; `Chord.cs:114` allocates a position array every frame. Chord objects are created/destroyed with input changes. | Reuse meshes and arrays, pool interval renderers, and manage material lifetimes. Profile before setting quality budgets; this is a code-level risk, not a measured FPS claim. |
| P2 | Navigation is keyboard-only in the reviewed input code; only natural-key letter shortcuts are present. Labels always use sharps. | Add all 12 keys, context-appropriate enharmonic spelling, pointer selection, camera reset, and discoverable controls. Guard absent keyboard devices. |

Additional checks: audio envelopes and clipping under polyphony; sound cleanup on focus loss; shader compatibility and build inclusion for the runtime `Shader.Find` calls under the configured URP pipeline; label billboarding during key animation, which currently updates positions without explicitly refreshing facing. These require runtime validation before being called observed defects.

## Reconcile the references before implementing grammar

Use an explicit circle-of-fourths index and a provisional movement definition `n = s + 4r (mod 12)`. Keep this separate from the current visual `pathMap`, which uses fifth steps and major-third twists.

1. **Default movement versus examples.** Document 01 defines bare `X` as `1 0 X`. Starting at G', bare `M` therefore reads C major, although document 02's opening traversal labels it G major. Use `0M` to read G major at G'. Likewise, read Cmaj7 on an already selected C' with `0m0M`; bare `m0M` first moves to F'.
2. **Pivot shorthand.** Under the stated defaults, `Pc` includes one station shift: from C' it reaches A', not E'. The pivot-bridge example needs `0Pc` to reach E' before `2d` reaches D'. Proposed explicit example from C': `0m0M > (0Pc) 2d`, subject to defining `>` and sustain release.
3. **Movement composition needs carries.** Four `(1,0)` moves equal `(0,1)`, not identity. Sum integer movements modulo 12, then recover `s = n mod 4`, `r = floor(n/4)`. Do not independently add the coordinate components without carrying.
4. **Final location does not establish chord equivalence.** Under the cohered-union interpretation, `0M` and `0m0M` from C' have the same endpoint and final object but produce C major and Cmaj7 respectively. Compare evaluated sounding pitch-class sets; preserve path/provenance separately.
5. **The common-tone claim needs a narrower scope.** Pure `0P` sends C' to E'; their Major7 sets share E and B, two pitch classes. The composite `1P` move from Bb' to G' shares D alone. Distinguish pure rotation, composite motion, surface intersections, and sounding-chord intersections.
6. **Separate topology and interpretation.** The references evolve from a general P-blocking rule to opposite-edge locking and exceptions such as `3P`. Define an admissibility table per complete movement and sounding state. Preserve active-diagonal and saturated-edge flags independently: both can be true.
7. **Correct demonstrable pitch-set errors.** Document 02's NRT L row says C major and E minor preserve C–E; their intersection is E–G. Its `Pm` hexatonic example also conflicts with the default shift: from C', `Pm` reaches A' and reads C# minor. `0Pm` reaches E' and reads G# minor, enharmonic to Ab minor.
8. **Specify temporal semantics.** Define what whitespace, contiguous tokens, `>`, parentheses, sustain release, and branch reconvergence do to sounding content. Keep a selected scale collection separate from tonal-center inference. A chromatic contradiction can invalidate a strict collection hypothesis without proving that the perceived tonal key has changed.

These are proposed reconciliations for a versioned model contract, not edits to the user's theory. Unresolved rules should be marked experimental and excluded from normative examples.

## Feature roadmap and acceptance criteria

### Milestone 1 — Reliable musical and rendering foundation

Dependencies: none for renderer/input fixes; grammar implementation depends on the reconciled contract.

- Extract pure C# types for `PitchClass`, `KeyContext`, `SurfaceDefinition`, `LocalObject`, `Movement`, and `SoundingState`. Use one documented pitch convention and an adapter for the existing A0-based note array.
- Define all 12 Major7 surfaces and their T/c/e/d/l/M/m subsets as data. A minor triad remains an object on its parent surface, rather than becoming a competing surface identity.
- Fix held-line tracking, interrupted key animations, resource ownership, input validation, and predictable sound release.
- Establish deterministic fixtures for model semantics before connecting them to visual animation.

Acceptance: in all 12 keys, I/IV/V labels retain their blue/red/green meanings; a held chord stays connected throughout key movement; rapid A→C→F changes finish at the same pose as a direct F selection; surface subsets and transposition are exact; mesh/material counts stabilize after repeated key changes.

### Milestone 2 — Explainable surface explorer (first user-facing release)

Dependencies: milestone 1 model and geometry fixes.

- Add a persistent legend: **I / tonic — blue; IV / subdominant — red; V / dominant — green**, with text and distinct outline patterns so color is not the sole cue.
- Add selection/hover details: `C' surface: C E G B`, selected object, sounding notes, and chord label. Show surface ownership explicitly, e.g. `Am = m on F'`.
- Offer geometry layers: structural c/e borders, internal d/l diagonals, Major7 surface fill, register detail, and sounding-only content. Use animation/line style for activity; preserve harmonic hues.
- Add a diatonic-strip view: in C major/A natural minor, half G', full C', full F', half Bb'. Keep that collection view separate from tonic/function coloring.
- Add a 12-key selector, major/minor context, spelling toggle/automatic spelling, camera presets/reset, and reduced-motion controls. Use one octave/12 pitch classes as the simple view; reveal all registers on demand.

Acceptance: a user can select C', audition C major and E minor, identify their shared notes, and distinguish C' from C without consulting the grammar document. Red/green distinctions remain understandable in grayscale.

### Milestone 3 — Guided cadence player and grammar workbench

Dependencies: milestones 1–2 and agreed timing/sustain rules.

- Add editable progression cards, step/back/play, tempo, loop, and a movement trace that expands shorthand into explicit `(s,r,object)` tokens.
- Start with I–IV–V–I, ii–V–I, and I–vi–IV–V; add E major→A minor and C major→C minor as mode-change demonstrations.
- Animate origin, station shift, triangle rotation, destination surface, and common tones distinctly. Keep a motion trail and optional voice-leading display.
- Expose topology exploration and the document's Classical Filter separately. Explain a gated move with the responsible sounding dyad; never silently suppress a player's input.
- Defer branching/reconvergence until sequential evaluation and sustain are dependable.

Acceptance: from Bb', `0m > PM > M` traces Dm→G→C under the declared defaults. From E', `0M > Sm` yields E→Am. From C', `0Sm` yields C minor. Every step exposes actual pitch sets, surface, token expansion, and source of any functional interpretation.

### Milestone 4 — Dependable performance and analysis

Dependencies: stable sounding-state model; can begin after milestone 1 alongside explorer work.

- Replace snapshot-driven MIDI playback with ordered event scheduling, channel-aware voice state, sustain handling, and deterministic seek reconstruction. Retain the existing tempo-event support and add regression fixtures.
- Add transport/file import, track/channel selection, percussion handling, speed changes that preserve current position, volume, and audio envelopes. Add live MIDI input after file playback is reliable.
- Add analysis as a separate layer: chord candidates, bass/inversion, common-tone motion, and ambiguous/unknown states. Distinguish manual key, MIDI-declared key, and estimated key.
- Offer the reference document's strict current-collection veto as an experimental analysis mode. A contextual tonal estimate can be another mode; neither should silently rotate the view unless auto-follow is enabled.
- Add optional history/wake, exportable progression data, and saved view presets after analysis states are explainable.

Acceptance: overlapping same-pitch notes across channels and pedal release behave correctly; notes do not hang after stop/seek; tempo changes are retained; short notes survive slow render frames; missing metadata is reported as unknown/manual; relative major/minor ambiguity is visible.

## Suggested architecture

`Keyboard / MIDI / Grammar → sounding-state controller → pure harmonic model → torus, inspector, history, and audio`

Keep model evaluation independent of Unity transforms. A renderer receives an immutable evaluated state and a target pose; animation cannot change the musical answer. Model tests should not require a scene. Reuse `UmbilicTorus` as the geometric mapping and evolve the current `Main` into a small coordinator, moving playback, interpretation, presentation, and geometry ownership into separate components.

## Verification and sequencing

Implement milestone 1 first, then ship milestone 2 with one ii–V–I lesson from milestone 3 as the first complete experience. Delay automatic key detection, branch notation, and elaborate history effects until that experience is trustworthy.

Model tests: all 12 transpositions; negative movement normalization; station carry; movement inverses; local pitch sets; coherent unions; sustain; explicit contradictory examples. Play-mode checks: held notes during retargeting, pointer selection, audio release, absent keyboard, imports and transport. Performance checks: idle, dense chords, repeated key animation, and long MIDI sessions; compare frame time, managed allocations, and native resource counts before/after pooling on the intended hardware.

Initial performance target, to validate rather than assume: smooth 60 Hz interaction on the user's desktop, no unbounded resource growth, and no avoidable per-frame line-buffer allocations. Confirm deployment platform before committing to platform-specific input or audio backends.
