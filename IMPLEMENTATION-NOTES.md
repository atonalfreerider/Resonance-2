# Resonance explorer implementation

## Run

Open `Assets/Scenes/Resonance.unity` and enter Play Mode. The existing Main object creates the explorer panel, selected-surface overlay, and shared synthesizer at runtime; no scene rewiring is required. Scroll the left panel for lessons, view layers, and MIDI controls.

- Key and mode control the tonal context. I/key remains blue, IV red, V green. Surface selection is independent.
- Select a surface using its dropdown or by clicking a note sphere. T, M, m, c, e, d, l audition the named local object. The white overlay labels the selected surface's borders and diagonals.
- Prepare a lesson or sequence, then Step, Back, or Play. Stop releases sustained objects. Changing the selected surface sets the next sequence's starting surface.
- View options include a continuous tonal field, harmonic-partial light, field density, soft diatonic emphasis, optional selected-surface guide, structural wireframe, sounding diagonals, registers, sounding-only notes, flat spelling, reduced motion, and camera reset.
- MIDI files can be opened with the Editor file picker or a file path. Play/pause, stop, seek, loop, tempo scaling, channel/track filters, percussion exclusion, and declared-key following are available. Standalone players use the file-path field.
- Windows MIDI devices are listed in the live-input dropdown. Live input, lessons, and MIDI-file playback switch ownership explicitly. Live input disconnects on focus loss to prevent stuck notes; reconnect through the dropdown when needed.
- Click the torus viewport to enable instrument shortcuts: number-row keys play chromatic notes relative to the tonic, A–G choose natural tonics, arrows orbit/zoom, Page Up/Down change elevation, Space toggles MIDI, and Escape releases notes. Right-drag orbits and the wheel zooms inside the viewport.
- Clicking any panel control, or pressing Tab, gives the UI keyboard ownership until the viewport is clicked again. This covers text/numeric inputs, sliders, dropdowns, buttons, and UI navigation. Keyboard-held notes release when ownership changes. Ctrl+Escape always stops sound, including live MIDI.
- The analysis panel shows exact chord matches, bass, and compatible major/relative-minor collections. These are compatibility results, not a claim that a key has been established. History can be exported to the application persistent-data folder.

## Grammar v1 contract

Pitch classes internally use A=0, C=3, matching the original renderer. Each surface is `{T,T+4,T+7,T+11}`. Movement is `n=s+4r` along the circle of fourths, so the destination pitch class is `T+5n mod 12`. Bare objects use `(1,0)`. Use `0M` for the current surface. S/P encode rotations 1/2. Explicit numeric `(station,rotation)` prefixes can be separated by spaces, e.g. `0 2 c`.

Adjacent objects accumulate a pitch-class union. `>` starts a new sounding event. Parenthesized objects sustain through subsequent events until Stop or a new run. Parentheses must close within one event. Branches, commentary anchors, and malformed tokens are rejected with visible feedback. This is a declared experimental resolution of reference-document ambiguities, not an assertion that every example in those documents agrees.

Examples: from Bb', `0m > PM > M` gives Dm→G→C. From E', `0M > Sm` gives E→Am. From C', `0M > 0Sm` gives C→Cm. From C', `0m0M > (0Pc) 2d` preserves E–B while adding F#–A.

## Engineering changes

### Continuous tonal field and restored bloom

The former separately colored region volumes have been replaced by one closed, ruled triangular surface sampled from the existing umbilic path. Neither sound nor color displaces its geometry. Shared-edge colors are evaluated from the same local-space field; there are no per-section color assignments. The optional surface guide is off by default.

Each pitch receives a smoothly blended hue relative to the chosen key, with I blue, IV red, and V green as the color anchors. A smooth spatial kernel blends those hues across the surface. Active voices add the first eight ideal harmonic partials, weighted by `1 / harmonic^1.35`, folded into the nearest 12-TET pitch classes. Shared partials accumulate with soft compression, and their light decays smoothly after release. Streamlines show a slowed visual phase on the surface; these are not audio-frequency oscillations or a measured spectrum. The audio synthesizer remains unchanged.

HDR notes and interval gradients restore the visible glow. The existing global Volume profile uses Bloom intensity 1.25, threshold 0.85, scatter 0.72, high-quality filtering, and clamp 32. The existing URP asset supports HDR and the main camera has post-processing enabled. The camera now frames the entire surface with room for the panel.

Additional validation: **Tools → Resonance → Check tonal field and input focus** runs geometry seam, transposed color-anchor, excitation, and real Input System keyboard-event checks. Results are written to `Temp/ResonanceChecks/field-input.txt`.

Key animation now interpolates to canonical poses and can be retargeted. Held intervals follow note positions; curved fifths refresh with geometry and velocity. Meshes, line buffers, and inactive interval renderers are reused. Note colors retain key-relative hues instead of blending their identities. Minor context retains its mode.

One synthesizer replaces 96 separate audio sources. Timestamped MIDI snapshots are scheduled against the DSP clock, with envelopes and a soft limiter. MIDI parsing retains tempo changes, channel voice ownership, repeated notes, velocity-zero releases, and sustain/controller behavior. Format-2 and SMPTE-timed MIDI are rejected with an explanation. Audio is scheduled ahead by half a second; a main-thread stall longer than the scheduling window can still cause late playback.

The Unity Pipeline package `0.7.0-exp.1` was installed for local Editor validation. It and its resolved dependencies are recorded in the package manifest and lockfile.

## Verification

The field now uses a soft spatial mask around the defined chord triangles. Diatonic major regions receive full emphasis, minor regions 28%, and Neapolitan and secondary-dominant regions 18%; the major dominant in minor receives 35%. Unclaimed surface fades completely to black, including its harmonic light and ribbons. This is a visual hierarchy, not a prohibition on playing other pitches. Geometry and note/interval bloom remain unchanged.

- `dotnet run --project Tests/ModelChecks/ModelChecks.csproj`: 741 checks covering all transpositions, movement carries, malformed grammar, coherence/sustain, MIDI channel/pedal/retrigger state, harmonic amplitude ordering, and partial accumulation.
- In Play Mode: **Tools → Resonance → Run runtime regression checks**. Results are written to `Temp/ResonanceChecks/runtime.txt`. The run passed 24 checks covering key poses, interrupted animation, interval endpoints, mesh/pool reuse, scroll layout, ii–V–I, and MIDI transport using the scene's existing MIDI file.
- The tonal-field and input-focus suite passed 67 checks, including synthetic keyboard events and surface continuity.
- Unity compiled the project and the inspected runtime console had no errors. The Game-view layout was captured and visually inspected after correction.

Hardware live MIDI, standalone file import/builds, listening quality/latency, and sustained performance on target devices still require verification. No claim of a measured 60 FPS budget is made. Branch/reconvergence notation, disputed Classical Filter gating, automatic tonal-key estimation, saved-view presets, and a 3D history wake remain deferred; the current history is textual. Flat/sharp spelling is user-selected rather than full context-sensitive notation.
