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

The pattern wheels are a rack and pinion driving a planetary train, one gear per level of repetition (see [Docs/PATTERN-WHEELS.md](../../Docs/PATTERN-WHEELS.md)). The rack on the left is the song unrolled, one tooth per bar; drag upward to seek forward, downward to rewind, or scroll. The ring it meshes with is the same song wound once around, each section visit drawn in its family's hatch, with brackets over recurring groups such as verse + chorus. The centre planet is the section playing (one turn per visit, this visit's chords on its band); its moon is the family's fundamental loop, one turn per loop, so its size is the repetition count. A spark where moon meets band marks a variation. Parked planets show every other family's fundamental. Colour is reserved for tonality (chord function); form is drawn in a light muted teal by hatch pattern, dashed for material heard only once. Channel 10 is excluded from the torus and overlay.

An optional `song.json` beside the MIDI supplies `LeadVocalTrack` (optional zero-based MIDI track index), `Key` (A-based pitch class, A=0), `Minor`, `KeySource`, `SectionBoundaries` (one-based bar/name pairs), `SectionSource`, and optional per-section `SectionParents` paths. Nested paths group sections into larger parts (movements, recurring groups). Changes require offline regeneration. Ticket to Ride explicitly uses the reviewed A-major context; its verse/chorus/bridge boundaries remain editable estimates.

Pattern compression runs before rhythm templating. Repetition decides the sections: the most compressive repeated stretches (a chorus heard six times, a strain played twice) become families, a long repeated block is cut where a part of it returns on its own, and only the through-composed bars between returns are divided by novelty. Each family is then reduced to one fundamental chord loop, and every visit is saved as passes of it with their transposition, lead-ins, tags and changed beats. `Patterns`, `Groups`, `FormGrammar` (for example `In (V C)×2 Br C′ Out`) and per-section `Passes`/`Variation` are bundle version 3. Run `dotnet run --project Tools/PatternPrep -- --self-test` for the compression fixtures, `--fixture pop|variation|strain|ternary` for a verbose analysis, and `--write-fixture variation out.mid` for a playable generated song.

The compiler deduplicates rhythm slots across transposition and chord-quality changes, storing exact timing, pitch and velocity residuals. It reconstructs and compares every event before saving. Ticket to Ride's 5,279 notes use 977 rhythm slots in 105 templates, plus variations and occurrences. This is structural reuse, not a claim that the complete JSON is smaller than MIDI.

The drum changer sits below the torus in the XZ plane: a music-box disc in the same light teal ink, a dark steel plate with a dimple wherever the groove strikes, turning under a comb at twelve o'clock, with radial lines on the beats (brightest on the downbeat) and fainter ones on the upbeats. The torus hides the plate's far edge; the struck dimples stay in view. Each disc is a groove family and the hub names the section it mostly plays in. The groove's first bar is its fundamental: its strikes missing from the current bar remain as faint empty dimples, and strikes it does not have are ringed and spark when hit. Dimple positions use the saved drum notes, and strikes trigger from the same recording clock that rotates the disc. A small triangle marks twelve o’clock. A broad bass-drum crest and subtler trailing pond ripples replace the bright radial playhead and sharp single rings. White waves originate at each instrument lane’s twelve-o’clock strike point and stay in the disc plane. Kick waves are large, snare/tom waves smaller, hats and shakers tiny and short-lived, and cymbals intermediate. Broad kick pulses, denser snare waves, and fine hat/cymbal ripples use precomputed **General MIDI instrument-band classifications**, not measured spectra of individual recording hits. Pause/seek/loop clear obsolete waves.

Section names without MIDI markers are estimates; they are not asserted to be verse/chorus labels. Fingerprint reports distinguish encoding precision from musical matching confidence.

Validation: Python restoration and retiming fixtures; 785 model checks; Unity route, note-attack, release, pattern-navigation, drum-plane, recorded-playback and keyboard-focus checks.

DrumBars and DrumFamilies are prepared offline: one complete notated measure per revolution (four counts in 4/4, three in 3/4), including a padded final partial measure. Similar bars reuse matched variation slots; family changes slide real discs vertically into the playing position. The kick lane and count ticks remain visible. Instrument callouts are omitted, and remaining wheel labels have transparent backgrounds.

RegionPhases is a separate, continuous display timeline prepared per section. Single-tone/dyad observations and rests do not erase harmonic context: retain the preceding triad until another triad is established, seed the section opening from its first triad, and use the reviewed key if an entire section has no triad. Seventh/extension changes share their root/third/fifth region. Runtime performs only interval lookup.


Tonal context: reviewed song keys remain locked; several authored MIDI key signatures are preserved; a single opening signature, or none, is only a starting point. Key changes are then detected from the chord timeline (`KeyAnalysis`): a Viterbi pass over 24 keys where secondary dominants, leading-tone chords, the Neapolitan and borrowed chords are cheap tonicizations and a new key must hold four bars. The bundle saves `KeyChanges` (with evidence) and `Tensions` (V/V, ♭II…, their target key and whether a key change completes them): the torus leans toward a tension's key and relaxes, or completes the twist when the key change follows. Set `InferKeyChanges: false` to hold a configured key. Short (<0.75 beat) passing chords remain in note/chord data but do not interrupt the contextual region. Existing bundles are not automatically regenerated. Run `dotnet run --project Tools/PatternPrep -- --test-harmony` for synthetic checks.
