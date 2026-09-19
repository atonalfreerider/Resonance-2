# Offline pattern-wheel library

The playback bundle is **WAV + MIDI + JSON**. CSV remains a source/archive format; it is not parsed during playback. The original MP3 can be kept as the recording source. Fingerprint preparation decodes it once to a canonical sample-clock WAV.

- `aligned.mid`: fingerprint-retimed musical events, including percussion.
- `aligned.mid.prepared.json`: recording/MIDI hashes and provenance.
- `aligned.mid.patterns.json`: precomputed transport frames, attacks, tempo map, measures, section families, chord progressions, per-track/channel pattern discs, occurrences and note variations. Drum frequency bands and decay are also saved here.
- `recording.wav`: the actual recording, used as the master transport clock.
- Fingerprint report/timing CSV: review and correction evidence.

Unity verifies the MIDI hash and consumes these results. It does not discover patterns, infer song sections, or fingerprint audio while loading or playing. Raw or modified MIDI is rejected until its analysis is regenerated. Restored MIDI-only bundles can be previewed without a recording, and are labeled **restored score** in the prepared library.

## Restore the legacy library

From the project root, after installing the SongPrep Python dependencies:

```powershell
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongPrep/restore_legacy.py --source "C:/Users/johnb/Desktop/resonance-music/StreamingAssets/CSV" --output PreparedSongs/Legacy
```

All 12 supplied CSV songs have been restored. Six are MIDI-event exports; six are authored pattern tables. Each output includes `restoration.json`, with source hash and reconstruction assumptions.

Event exports retain note pitch, channel, velocity, timing, tempo, meter, recognizable controllers and pitch bend. The legacy notation is **C0 = MIDI 0**, so C3/D3/F#3 map to kick/snare/closed hat. System/vendor metadata without unambiguous encoding is reported; Bank Select's missing MSB/LSB distinction cannot be recovered exactly.

Authored tables preserve the track sequence, pattern group, variation index, subdivision length and pitch substitutions. `-` keeps the row pitch; `x` silences it. A `group[sequence]` expands the corresponding sequence of variations. Negative sequence slots are silent cycles. Beats are one-based; pickups before a cycle are retained. Missing velocities/programs use velocity 100 and default programs. These are musical reconstructions, not byte-for-byte recovery of a lost original MIDI.

## Analyze a MIDI outside Unity

Requires the .NET 10 SDK (the same SDK used by the project's model checks):

```powershell
dotnet run --project Tools/PatternPrep/PatternPrep.csproj -- "PreparedSongs/MySong/score.mid"
# Preserve the authored disc grouping when available:
dotnet run --project Tools/PatternPrep/PatternPrep.csproj -- "PreparedSongs/MySong/score.mid" "PreparedSongs/MySong/authored-patterns.json"
```

`Tools/SongPrep/prepare-song.ps1` now runs this step automatically after fingerprint alignment, and uses authored pattern data beside the source MIDI when present.

The restored Ticket to Ride score is fingerprint-prepared with its recording in `PreparedSongs/TicketToRide-Restored`: 5,279 notes including 1,569 drum hits. Its first 3,710 pitched notes match the earlier percussion-free score. Six supplied WAVs now have recording bundles in `PreparedSongs/Recordings`: ComeFirst, Drank, JustAnotherInterlude, SaySo, Sexual, and TouchxBeMyBaby. The library prefers these to duplicate score previews. Other entries without supplied recordings remain score previews. Automatic alignment reports contain substantial weak regions; review the listening previews and use matching anchors for tighter synchronization.

## Display

The left-side time rack advances a mechanical meta wheel through the saved section sequence. Drag upward to seek forward, downward to rewind, or scroll with the mouse wheel. Play/Pause sits above the rack. Repeated verses reuse their section-family wheel, with all pitched instrument channels inside it. The current family is enlarged at the center and lights up; other family wheels remain visible but dimmed. Rhythm slots rotate toward twelve o'clock and pitch variants slide radially. Channel 10 is excluded from the torus and overlay.

An optional `song.json` beside the MIDI supplies `LeadVocalTrack` (optional zero-based MIDI track index), `Key` (A-based pitch class, A=0), `Minor`, `KeySource`, `SectionBoundaries` (one-based bar/name pairs), `SectionSource`, and optional per-section `SectionParents` paths. Nested paths support movements and larger forms; select the hierarchy in **Form level**. Changes require offline regeneration. Ticket to Ride explicitly uses the reviewed A-major context; its verse/chorus/bridge boundaries remain editable estimates.

The compiler deduplicates rhythm slots across transposition and chord-quality changes, storing exact timing, pitch and velocity residuals. It reconstructs and compares every event before saving. Ticket to Ride's 5,279 notes use 977 rhythm slots in 105 templates, plus variations and occurrences. This is structural reuse, not a claim that the complete JSON is smaller than MIDI.

The drum changer sits below the torus in the XZ plane. Dimple positions use the saved drum notes, and strikes trigger from the same recording clock that rotates the disc. A small triangle marks twelve o’clock. A broad bass-drum crest and subtler trailing pond ripples replace the bright radial playhead and sharp single rings. White waves originate at each instrument lane’s twelve-o’clock strike point and stay in the disc plane. Kick waves are large, snare/tom waves smaller, hats and shakers tiny and short-lived, and cymbals intermediate. Broad kick pulses, denser snare waves, and fine hat/cymbal ripples use precomputed **General MIDI instrument-band classifications**, not measured spectra of individual recording hits. Pause/seek/loop clear obsolete waves.

Section names without MIDI markers are estimates; they are not asserted to be verse/chorus labels. Fingerprint reports distinguish encoding precision from musical matching confidence.

Validation: Python restoration and retiming fixtures; 785 model checks; Unity route, note-attack, release, pattern-navigation, drum-plane, recorded-playback and keyboard-focus checks.
