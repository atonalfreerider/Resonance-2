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

## Harmonic memory and cyclic track analysis

The field integrates each pitch class with `dE/dt = input - ln(2)/halfLife * E`. Default half-life is 1.8 seconds, adjustable under VIEW, with an energy-influence control and a clear-memory button. Held notes accumulate energy; released energy persists and blends with subsequent chords. Velocity, MIDI channel volume (CC7), expression (CC11), and master volume scale the input. Color contributions stay proportional before a final brightness shoulder. This is modeled MIDI energy, not measured acoustic loudness. The black tonal mask and umbilic geometry are preserved.

MIDI playback integrates every intermediate note-state interval, including notes shorter than a rendered frame. Seek/load clear stale memory; stop and pause release notes and let the wash decay. Playback speed changes retain the accumulated field. Scheduling lead time no longer moves the displayed playhead backward.

Note spheres now have a separate 0.7-second visual release. Their scale and HDR light decay after note-off, returning to the dim reference dot when idle notes are enabled. Chord lines dissipate over 2.4 seconds: vibration damps faster than the remaining light and thickness, with no shortening or endpoint contraction. Released lines remain in the pool's active set until their envelope finishes; retriggering reuses the same line. Both release times are adjustable under VIEW. Audio still stops on note-off.

TRACK ORRERY is a nested mechanical model. The key is the sun; the song planet makes exactly one revolution over the file's duration. Its section moon turns once over the current section, carrying a smaller moon that turns over the entire chord progression, potentially several times per section. A chord change recolors that moon without resetting its orbit. The enlarged progression wheel has fixed chord sectors and one moving hand. The solar band, section bodies, and section buttons carry ordered chord-color bands using the same I-blue / IV-red / V-green palette as the torus. The solar timeline accounts for tempo changes. Reduced motion retains static positions with live colors and timeline indication.

Song-form analysis uses MIDI markers when available. Otherwise it proposes 8-bar sections (4/8/16/32 are selectable), groups recurring harmonic profiles, and labels families A, B, etc. It does not claim to infer semantic verse/chorus/bridge roles. Under **Section names and boundaries**, rename a family or apply explicit 1-based bar boundaries such as `1 Intro`, `5 Verse`, `13 Chorus`, `21 Verse`. Repeated names link a family. These edits are runtime state; Copy current boundaries exposes a reusable text map. Chord labels are heuristic estimates from velocity- and duration-weighted pitched notes, excluding percussion; sustain pedal extension is not currently included in this offline chord estimator. Shorter progression periods are inferred from repeating harmonic bar profiles with compatible measure lengths.

The earlier per-track rhythm-pattern extractor remains available in the model, but no longer drives the primary orrery UI. It adapts ideas from the user's `AlgoRhythmAnalyzer` project, which is read-only and unchanged. The orrery and persistence integration check is available at **Tools → Resonance → Check persistence and orrery**, with results in `Temp/ResonanceChecks/orrery.txt`.

## Verification

The field now uses a soft spatial mask around the defined chord triangles. Diatonic major regions receive full emphasis, minor regions 28%, and Neapolitan and secondary-dominant regions 18%; the major dominant in minor receives 35%. Unclaimed surface fades completely to black, including its harmonic light and ribbons. This is a visual hierarchy, not a prohibition on playing other pitches. Geometry and note/interval bloom remain unchanged.

- `dotnet run --project Tests/ModelChecks/ModelChecks.csproj`: 770 checks covering all transpositions, movement carries, malformed grammar, coherence/sustain, MIDI state and dynamics, harmonic accumulation, persistence, visual release/retrigger, rhythm compression, tempo/meter changes, hierarchical verse/chorus fixtures, nested progression periods, marker provenance, and invalid boundaries.
- In Play Mode: **Tools → Resonance → Run runtime regression checks**. Results are written to `Temp/ResonanceChecks/runtime.txt`. The run passed 24 checks covering key poses, interrupted animation, interval endpoints, mesh/pool reuse, scroll layout, ii–V–I, and MIDI transport using the scene's existing MIDI file.
- The tonal-field and input-focus suite passed 67 checks, including synthetic keyboard events and surface continuity.
- The persistence/orrery suite passed 18 checks, including note shrink/fade, full-span chord dissipation, retrigger/pool reuse, keyboard-driven section seeking, nested orbital phases, shared tonal colors, and runtime harmonic memory.
- Unity compiled the project and the inspected runtime console had no errors. The Game-view layout was captured and visually inspected after correction.

Hardware live MIDI, standalone file import/builds, listening quality/latency, and sustained performance on target devices still require verification. No claim of a measured 60 FPS budget is made. Branch/reconvergence notation, disputed Classical Filter gating, automatic tonal-key estimation, saved-view presets, and a 3D history wake remain deferred; the current history is textual. Flat/sharp spelling is user-selected rather than full context-sensitive notation.

## Offline fingerprint preprocessing

The previous in-Unity timing estimator and anchor UI have been removed from song playback. Use Tools/SongPrep/prepare-song.ps1 before Unity. It extracts tuning-aware pitch and attack fingerprints with Sync Toolbox, aligns them using multiscale DTW at a 10 ms feature grid, and exports a new aligned.mid with a replacement tempo map. All original note/controller payloads and musical tick relationships are retained. The original MIDI and MP3 are untouched.

The output bundle includes recording.wav (the canonical decode of the MP3), aligned.mid, its prepared manifest with SHA-256 hashes, timing-map.csv, analysis.json, fingerprint plots, and report.html with a stereo listening comparison. Review/correct matching cues offline using the optional anchors CSV, then regenerate. No Unity interaction is required for preparation. See Tools/SongPrep/README.md for commands, dependencies and limitations.

Unity's SONG panel accepts the preprocessed MIDI and either the original MP3 or the prepared WAV. It validates hashes and plays the canonical recording alone. It uses an identity time map: timing is already baked into the MIDI, so no saved warp or duration scaling can be applied twice. It rejects raw or mismatched pairs with a preprocessing instruction. Section names/boundaries are saved separately from immutable prepared timing.

The supplied Ticket to Ride score has a 1.92-second initial rest and a 192-second timeline. The canonical recording is 190.1482086 seconds. Fingerprint extraction detects approximately -33 cents of tuning offset and no whole-semitone transposition. All 3,710 notes were preserved; the regenerated MIDI's serialization error against its computed map is below 0.001 ms. Pitch similarity improves from roughly 51% under simple duration scaling to 63% after fingerprint alignment. These are matching/encoding diagnostics, not a claim of perfect musical synchronization. Sparse approximations, repeated music and the quiet fade remain uncertain and are flagged in the offline report.

Tests: Tools/SongPrep/test_prepare.py checks nonlinear tempo conversion, onset/offset duration mapping and event-payload preservation with independent MIDI fixtures. The real output is reparsed and checked against every original event. Runtime paired-playback checks cover the sample clock, source isolation, pause/seek/end/loop, and original recording speed.

## Primary-tone color and chord motion

The sounding chord root (or weighted fundamentals/bass when auditioning) drives a smoothed global color wash. Upper partials no longer elect the dominant color by themselves. Notes and chords follow this primary hue strongly; VIEW exposes **Primary tone color influence**. Explicit key-relative violet/magenta minor hues replace the old normalized color kernel that let green spread across unrelated pitches. The orrery uses the same chord/minor palette. The three primary major regions retain substantial local blue/red/green identity, and the soft black mask remains intact.

Chord motion combines two transverse vibration axes and multiple traveling modes at approximately three times the old amplitude. Endpoints stay fixed, reduced motion remains supported, and release still dissipates the complete span through diminishing motion, width and brightness. Umbilic mesh positions and topology are unchanged.

Additional verification: `dotnet run --project Tests/ModelChecks/ModelChecks.csproj` passes 785 checks, including timing-map inversion, invalid anchors and independent synthetic audio with local tempo changes. **Tools → Resonance → Check paired recording playback** tests source isolation, exact sample duration, mapped position, seek/pause, synthesis muting, original-speed playback, end and loop behavior. Results: `Temp/ResonanceChecks/paired-audio.txt`. Automatic alignment of the real pair is exercised separately; this is not a listening certification of perfect sync.

## Orrery bloom and camera ownership

The floating orrery uses a small HDR render pass through the same URP volume settings as the torus. A separate composite converts the black bloom background to transparency. The active section's timeline arc, song/section/progression bodies, and current chord sector brighten with MIDI energy and decay after release. The geometry and labels remain crisp. The transparent overlay ignores pointer picking; the sidebar toggle returns the orrery to its inline position.

Keyboard ownership changes only on an actual UI pointer event or Tab, or when the user clicks the camera viewport. An incidental focus assignment from UI navigation cannot claim ownership. While the viewport owns the keyboard, a non-tab root focus target captures navigation, FocusController.IgnoreEvent prevents focus movement, and raw key events are blocked from UI controls. The regression holds an arrow for twelve consecutive intervals while repeatedly assigning UI targets and injecting navigation; the camera must continue moving in every interval.

## Precomputed CD pattern wheels and restored legacy library

The active UI now uses PatternWheelDeck: a song meta disc with a colored progression on top and child CD stacks for pitched MIDI track/channel lanes. DrumPatternDeck renders percussion only below the torus, with a fixed playhead, rotating dimples, and planar white waves whose density/decay comes from saved General MIDI drum-band classifications. Camera framing includes both levels.

All loaded MIDI now requires a matching .patterns.json prepared by Tools/PatternPrep. The offline bundle contains tempo/measure/section/chord/pattern data plus complete controller-aware transport frames and attack events. Unity reads and filters saved frames; it no longer calls MIDI pattern or song-form analyzers during loading. The fingerprint workflow invokes PatternPrep automatically. CSV restoration recovered all 12 legacy songs; authored patterns preserve substitutions and sequence variations. TicketToRide-Restored combines the MP3 with the restored full 5,279-note score, including 1,569 drum hits. See Tools/PatternPrep/README.md for commands and reconstruction limitations.

Non-cross-section chord intervals now always use sampled umbilic curves. Octaves and major-third triangle edges may remain straight. Note attacks briefly enlarge and whiten, including repeated and sub-frame attacks, before settling to their tonal hue and release envelope. The umbilic mesh remains unchanged.


## Section-family rack and shortest chord paths

- Offline `SectionCompression` groups all instrument channels within section visits, reuses transposition/quality variants and verifies lossless note reconstruction. `song.json` supplies reviewed key context and editable section/group paths. All 14 prepared library bundles regenerated.
- Ticket to Ride retains A major; section-role boundaries are estimates. Runtime consumes saved hierarchy and variants only.
- The rack sits beside the left controls. Pointer capture supports mouse/touch drag seeking, mouse-wheel seeking and local Play/Pause. Inactive family wheels retain colored notes, outlines and readable labels.
- Chords use the shortest major-angle arc (at most half a physical orbit) with interpolation over the triangular surface edge. Equal-angle candidates choose the shorter sampled route. The original parameter wound three times around the hole, causing excessive wraps. Octaves and major-third triangle edges retain their direct cross-section paths.
- Default UI Toolkit layout is imported, with dark popup inner-container styling to prevent overlapping or low-contrast song choices.
- Drum ripple origins follow the twelve-o'clock instrument-lane collision point. Saved GM classifications control size, frequency and decay; these are not measured audio spectra.
- Validation: 785 model checks, five Python preparation/restoration/compression tests, per-bundle reconstruction checks, and Unity section/key/popup/drum/rack/route integration checks. See `Temp/ResonanceChecks/section-wheels.txt` for the latest runtime result.


## Overhead framing, moving rack and supplied recordings

Camera reset now frames the torus farther right/lower with a 48-degree elevation. Drum impacts emit a wide low-frequency bass crest and two weaker trailing crests; other instruments use quieter thin waves. The playhead is a small triangle.

Rack teeth scroll continuously from recording time. Boundary pins pass a fixed trigger and select the saved section route. Section take dials and channel pattern/variant dials retain the mechanical-clock metaphor. Button text/enabled updates run in TickControls, outside generateVisualContent, fixing the reported render-tree mutation exception.

All six WAVs from the legacy StreamingAssets/WAV folder are prepared under PreparedSongs/Recordings. Library entries prefer complete recording bundles and show the actual source filename. Extreme automatic fingerprint knots are merged to satisfy MIDI’s tempo encoding limit, with deviations recorded explicitly; finer PPQ retains sub-2ms export precision. One dangling terminal percussion note does not block pitched fingerprint extraction. Original inputs and MIDI payloads are preserved. These exports are not certified musically exact: many fingerprint windows are weak and need reviewed anchors.


## Upper-right torus and overhead percussion

The default/reset camera is closer (5-unit radius) and frames the torus in the upper right. Percussion is isolated on layer 30 and rendered by a square, orthographic, straight-down camera in the bottom right. Main-camera orbit no longer changes the drum view. Kicks use a single broad crest with a 0.24-second envelope; follow crests are disabled for kicks only.

Chord creation/retrigger starts a white HDR flash with a fast exponential decay into the harmonic color. Pattern notes retain their onset dots plus a fading circular duration ribbon calculated from each saved slot length and variant length residual. Notes longer than a full cycle carry a cycle-count label.


Drum layout revision: restored the shared 3D camera and removed the separate overhead viewport. The wheel center is now 2.8 units below the torus instead of 1.3, preserving the current kick/ripple animations.


## Rolling gear, vocal emphasis and harmonic outline

Meta-wheel rotation now follows rack displacement divided by circumference (opposite sign for upward rack travel), with tooth spacing matched to the gear. The featured section is enlarged at the hub; dim section wheels orbit around it. Radial guide lines lead to horizontal, neutral high-contrast labels. Track/channel IDs no longer assign color: pitches retain harmonic color and the chosen vocal lane has the strongest brightness/bloom.

Prepared bundles now retain MIDI track names and optional zero-based LeadVocalTrack in song.json. The UI provides a persistent per-song lead-vocal selector. Explicit vocal metadata takes priority; an unconfirmed lead-instrument name is a provisional fallback (Ticket to Ride: Lead Organ). No vocal identity is inferred from the audio.

DominantChordOutline draws the current root, third and fifth on the outer/highest-register surface using Main’s shortest surface routes. Minor thirds and diminished fifths follow chord quality; secondary dominants and Neapolitan chords use their actual roots relative to the key. Lines are thin, solid, subtly colored and unmodulated, fading during rests. Drum-wheel scale is 1.25.

Checks: all 785 model checks, lossless reconstruction of all 20 prepared bundles, compression fixture, and Unity gear/center/drum-scale plus major/minor/secondary-dominant/Neapolitan outline endpoints passed.
