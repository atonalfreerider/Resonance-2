# Offline song preparation

Run this **before opening or interacting with Unity**. Requires Python 3.11/3.12 and FFmpeg on PATH. Dependencies are isolated in `.venv` and pinned in `requirements.txt`.

```powershell
./Tools/SongPrep/prepare-song.ps1 -Audio "C:/music/song.mp3" -Midi "C:/music/score.mid" -Output "PreparedSongs/MySong"
```

Open the generated `report.html` before Unity. It provides fingerprints, timing corrections, weak-match windows, and a stereo listening check (recording left; retimed MIDI attacks right). Corrections can be supplied in a CSV with header `score_seconds,audio_seconds`, containing increasing pairs of matching musical cues. Rerun the same command with `-Anchors cues.csv`. All analysis and correction happen offline.

Load **aligned.mid** using Unity's SONG panel. Its sidecar selects **recording.wav**, the canonical PCM decode of the original MP3. The WAV is not synthesized MIDI audio. Unity checks SHA-256 hashes, refuses unprocessed/mismatched pairs, and uses an identity time map: there is no second timing warp or duration scaling. Keep the sidecar and WAV with the exported MIDI.

The preprocessor uses Sync Toolbox's tuning-aware pitch/chroma and DLNCO attack fingerprints with memory-restricted multiscale DTW. Features run at 100 Hz by default, with explicit correction for the filterbank's integer sample-hop clock. A replacement MIDI tempo map preserves musical tick positions, note pitches, velocities, note-offs, channels, instruments, controllers, signatures and markers. Original inputs are never overwritten. A higher output PPQ limits timing quantization; export is reparsed and every original event payload is checked.

`timing-map.csv` records the original MIDI seconds → recording seconds relationship. `analysis.json` distinguishes **musical matching diagnostics** from **MIDI serialization error**. A 10 ms feature grid and tiny export error do not establish 10 ms musical accuracy. Approximate/missing notes, ambiguous repeated passages, altered arrangements and fade-outs cannot be made perfectly identical by retiming alone. The tool does not invent missing notes or silently transpose the score; it reports a transposition diagnostic and weak windows. Use reviewed matching cues for those passages.

Outputs also include an ignored fingerprint cache and ignored PCM files. Regenerate the WAV/cache from the supplied sources when transferring the project. Do not commit copyrighted source recordings or audition files.

Tests:

```powershell
./Tools/SongPrep/.venv/Scripts/python.exe Tools/SongPrep/test_prepare.py
```

Method reference: [Sync Toolbox](https://github.com/groupmm/synctoolbox), MIT licensed; [audio–score example](https://github.com/groupmm/synctoolbox/blob/master/sync_audio_score_full.ipynb). Python dependencies are used as installed packages, not copied into Unity.
