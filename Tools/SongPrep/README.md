# Offline song preparation

Run this **before opening or interacting with Unity**. Requires Python 3.11/3.12 and FFmpeg on PATH. Dependencies are isolated in `.venv` and pinned in `requirements.txt`.

```powershell
./Tools/SongPrep/prepare-song.ps1 -Audio "C:/music/song.mp3" -Midi "C:/music/score.mid" -Output "PreparedSongs/MySong"
```

Open the generated `report.html` before Unity. It provides fingerprints, timing corrections, weak-match windows, and a stereo listening check (recording left; retimed MIDI attacks right). Corrections can be supplied in a CSV with header `score_seconds,audio_seconds`, containing increasing pairs of matching musical cues. Rerun the same command with `-Anchors cues.csv`. All analysis and correction happen offline.

Load **aligned.mid** using Unity's SONG panel. Its sidecar selects **recording.wav**, the canonical PCM decode of the original MP3. The WAV is not synthesized MIDI audio. Unity checks SHA-256 hashes, refuses unprocessed/mismatched pairs, and uses an identity time map: there is no second timing warp or duration scaling. Keep the sidecar and WAV with the exported MIDI.

The preprocessor uses Sync Toolbox's tuning-aware pitch/chroma and DLNCO attack fingerprints with memory-restricted multiscale DTW. Features run at 100 Hz by default, with explicit correction for the filterbank's integer sample-hop clock. A replacement MIDI tempo map preserves musical tick positions, note pitches, velocities, note-offs, channels, instruments, controllers, signatures and markers. Original inputs are never overwritten. A higher output PPQ limits timing quantization; export is reparsed and every original event payload is checked.

Recordings over three minutes also receive a local 200 Hz opening pass (first 15 seconds). A detected quiet prefix is encoded as a silent four-beat measure, preserving later bar boundaries and event payloads. `song.json` records `ScoreLeadInBeats` so score accents still match. Use `--opening-seconds 0` to disable this pass; explicit cue anchors take precedence.

For classical scores, pass `--mscz original.mscz` to `prepare_song.py`: Resonance reads the score itself (`musescore_import.py`, every MuseScore version) and marks the notes under accents, marcato and sforzato (and sf/sfz dynamics) in `score-accents.json`, after simplified and orchestral-piano staves are excluded. PatternPrep creates a virtual Melody highlight with at most two voices, preserving the complete sounding score. Classical instrument wheels use section boundaries near 32 bars, allowing spans up to 64 rather than reducing them to pop-sized harmonic cells. Set `PianoConcerto`, `PianoTracks`, and `OrchestraTracks` in `song.json` for dual-torus playback.

`timing-map.csv` records the original MIDI seconds → recording seconds relationship. `analysis.json` distinguishes **musical matching diagnostics** from **MIDI serialization error**. A 10 ms feature grid and tiny export error do not establish 10 ms musical accuracy. Approximate/missing notes, ambiguous repeated passages, altered arrangements and fade-outs cannot be made perfectly identical by retiming alone. The tool does not invent missing notes or silently transpose the score; it reports a transposition diagnostic and weak windows. Use reviewed matching cues for those passages.

Outputs also include an ignored fingerprint cache and ignored PCM files. Regenerate the WAV/cache from the supplied sources when transferring the project. Do not commit copyrighted source recordings or audition files.

Tests:

```powershell
./Tools/SongPrep/.venv/Scripts/python.exe Tools/SongPrep/test_prepare.py
```

Method reference: [Sync Toolbox](https://github.com/groupmm/synctoolbox), MIT licensed; [audio–score example](https://github.com/groupmm/synctoolbox/blob/master/sync_audio_score_full.ipynb). Python dependencies are used as installed packages, not copied into Unity.

## MuseScore scores

`musescore_import.py` reads `.mscz`/`.mscx` files of every MuseScore version without MuseScore: 1.x/2.x here,
3.x/4.x in `musescore_modern.py` (repeats and jumps unrolled as MuseScore plays them). It writes a type-1 MIDI
(the score's tempo map and signatures; one track per staff, a piano's staves as Right
hand / Left hand) and a timing JSON: every note with its score tick, printed measure number (a pickup is 0),
beat and seconds of the score's own tempo map, plus the measures, tempos, expression texts and a report of any
corrupted measure it repaired (voice 1 must fill its measure; other voices may stop early; nothing may cross
the bar line). The source score is never modified. Ottava lines move the sounding pitch.

Old files MuseScore 4 refuses as corrupted are often complete: MuseScore 1.3 wrote a bar's annotations first,
each at its own tick, and let the following tuplet's `<tick>` bring the reading position back; MuseScore 4's
1.x reader ignores a tuplet's tick, so the tuplet lands late and overruns the bar (Chopin's Op. 10 No. 3, bar
20: "Incomplete measure, measure 21, staff 1"). The timing JSON's `museScore4` list names each such measure;
`--musescore4-copy fixed.mscz` writes a copy MuseScore 4 opens (the misread chord gets its own `<tick>`, which
changes nothing under 1.x rules).

```powershell
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongPrep/musescore_import.py score.mscz --midi score.mid --json score-import.json
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongPrep/musescore_import.py old.mscz --musescore4-copy for-musescore4.mscz
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongPrep/test_musescore_import.py
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongPrep/test_musescore_modern.py
```

MuseScore 3.x/4.x files are played the way MuseScore 4 plays them (`musescore_modern.py`): repeats, voltas and
D.C./D.S. jumps unrolled (each played bar keeps its printed number, its pass and its own time signature), ties,
grace notes, articulation lengths, arpeggios, tremolos, fermatas, gradual tempo changes and pauses (as tempo-map
time, so bars keep their ticks). Trills, other ornaments and glissandos keep the written note. Checked against
MuseScore 4.7.2's own MIDI export on 21 real scores (piano solos to 29-staff concertos): at least 99.99%
precision and 98.3% recall, every difference an ornament. `test_musescore_modern.py` runs synthetic scores, and
compares with an installed MuseScore 4 on the scores in `RESONANCE_MUSESCORE_CORPUS` (default: five; `all` for
every score, about 4.5 minutes); it never writes into the scores folder.

`Tools/SongLibrary/library.py ingest <recording> --score <score.mscz>` imports the score, aligns it to the
recording (the recording is the source of truth for time; every tick is kept) and keeps `score-import.json`
in the bundle for tours and reports.
