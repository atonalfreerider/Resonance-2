# Resonance explorer implementation

## Run

Open `Assets/Scenes/Resonance.unity` and enter Play Mode. The existing Main object creates the explorer panel, selected-surface overlay, and shared synthesizer at runtime; no scene rewiring is required. Scroll the left panel for lessons, view layers, and MIDI controls.

- Key and mode control the tonal context. I/key remains blue, IV red, V green. Surface selection is independent.
- Select a surface using its dropdown or by clicking a note sphere. T, M, m, c, e, d, l audition the named local object. The white overlay labels the selected surface's borders and diagonals.
- Prepare a lesson or sequence, then Step, Back, or Play. Stop releases sustained objects. Changing the selected surface sets the next sequence's starting surface.
- View options include a diatonic strip, structural wireframe, sounding diagonals, registers, sounding-only notes, flat spelling, reduced motion, and camera reset.
- MIDI files can be opened with the Editor file picker or a file path. Play/pause, stop, seek, loop, tempo scaling, channel/track filters, percussion exclusion, and declared-key following are available. Standalone players use the file-path field.
- Windows MIDI devices are listed in the live-input dropdown. Live input, lessons, and MIDI-file playback switch ownership explicitly. Live input disconnects on focus loss to prevent stuck notes; reconnect through the dropdown when needed.
- Number-row keys play chromatic notes relative to the tonic. A–G choose natural tonics. Arrows orbit/zoom, Page Up/Down change elevation, Space toggles MIDI, and Escape releases notes.
- The analysis panel shows exact chord matches, bass, and compatible major/relative-minor collections. These are compatibility results, not a claim that a key has been established. History can be exported to the application persistent-data folder.

## Grammar v1 contract

Pitch classes internally use A=0, C=3, matching the original renderer. Each surface is `{T,T+4,T+7,T+11}`. Movement is `n=s+4r` along the circle of fourths, so the destination pitch class is `T+5n mod 12`. Bare objects use `(1,0)`. Use `0M` for the current surface. S/P encode rotations 1/2. Explicit numeric `(station,rotation)` prefixes can be separated by spaces, e.g. `0 2 c`.

Adjacent objects accumulate a pitch-class union. `>` starts a new sounding event. Parenthesized objects sustain through subsequent events until Stop or a new run. Parentheses must close within one event. Branches, commentary anchors, and malformed tokens are rejected with visible feedback. This is a declared experimental resolution of reference-document ambiguities, not an assertion that every example in those documents agrees.

Examples: from Bb', `0m > PM > M` gives Dm→G→C. From E', `0M > Sm` gives E→Am. From C', `0M > 0Sm` gives C→Cm. From C', `0m0M > (0Pc) 2d` preserves E–B while adding F#–A.

## Engineering changes

Key animation now interpolates to canonical poses and can be retargeted. Held intervals follow note positions; curved fifths refresh with geometry and velocity. Meshes, line buffers, and inactive interval renderers are reused. Note colors retain key-relative hues instead of blending their identities. Minor context retains its mode.

One synthesizer replaces 96 separate audio sources. Timestamped MIDI snapshots are scheduled against the DSP clock, with envelopes and a soft limiter. MIDI parsing retains tempo changes, channel voice ownership, repeated notes, velocity-zero releases, and sustain/controller behavior. Format-2 and SMPTE-timed MIDI are rejected with an explanation. Audio is scheduled ahead by half a second; a main-thread stall longer than the scheduling window can still cause late playback.

The Unity Pipeline package `0.7.0-exp.1` was installed for local Editor validation. It and its resolved dependencies are recorded in the package manifest and lockfile.

## Verification

- `dotnet run --project Tests/ModelChecks/ModelChecks.csproj`: 726 checks covering all transpositions, movement carries, malformed grammar, coherence/sustain, and MIDI channel/pedal/retrigger state.
- In Play Mode: **Tools → Resonance → Run runtime regression checks**. Results are written to `Temp/ResonanceChecks/runtime.txt`. The run passed 23 checks covering key poses, interrupted animation, interval endpoints, mesh/pool reuse, scroll layout, ii–V–I, and MIDI transport using the scene's existing MIDI file.
- Unity compiled the project and the inspected runtime console had no errors. The Game-view layout was captured and visually inspected after correction.

Hardware live MIDI, standalone file import/builds, listening quality/latency, and sustained performance on target devices still require verification. No claim of a measured 60 FPS budget is made. Branch/reconvergence notation, disputed Classical Filter gating, automatic tonal-key estimation, saved-view presets, and a 3D history wake remain deferred; the current history is textual. Flat/sharp spelling is user-selected rather than full context-sensitive notation.
