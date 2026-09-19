# Resonance Song Workshop

Double-click **Start Song Workshop.cmd** in the project root, or run:

```powershell
./Tools/SongLibrary/song-library.ps1
```

This opens the loopback-only manager at http://127.0.0.1:8765. Upload an MP3/WAV, enter an artist/title, and optionally supply a MIDI. The recording is processed locally. Online lookup sends only the search title to BitMidi; it never uploads audio.

## Pipeline

1. SHA-256 identifies an already prepared recording and reuses its validated bundle.
2. Search the local MIDI catalog, then the public BitMidi catalog when enabled. Embedded artist/title tags are used by the CLI when no title is supplied. This is title/metadata lookup plus musical validation, **not** a universal audio song-identification service.
3. Compare duration and chroma fingerprints before accepting a lookup candidate. A provided MIDI bypasses candidate selection but still goes through alignment and its diagnostic report.
4. A matching MIDI uses SongPrep's existing offline pitch/onset fingerprint alignment. Weak windows remain visible in `analysis.json` and `report.html`; ambiguous matches are not certified perfect.
5. If no candidate passes, YourMT3 transcribes the complete recording locally into instrument and drum tracks. The verified checkpoint is cached on this machine. Neural timing is preserved; an audio-estimated beat grid supplies musical bars without a second DTW warp.
6. PatternPrep generates section families, chord progressions, per-channel rhythm templates with pitch/timing/velocity variants, bar-sized drum families, and continuous harmonic region phases. Similar neural rhythms tolerate up to a quarter beat of jitter when choosing a template, while residuals reconstruct **every original note exactly**. This is lossless pattern representation, not a claim that every noisy transcription compresses well.
7. Validate MIDI/audio hashes, reconstructed notes, and section-region coverage, then publish the completed bundle atomically. Failed work remains in `SongLibraryData/jobs`; Unity never sees half-prepared analysis.
8. Review key, mode, lead vocal and section starts in the manager. Save creates a separate compiled revision. Export creates a ZIP under `Builds/SongBundles` with relative portable paths.

## Playback and review

Completed imports live in `PreparedSongs/Library`. Restart Unity playback to refresh its prepared-song choices, select the new bundle, and load it. All expensive processing happens ahead of playback. Unity plays `recording.wav`, not synthesized MIDI.

Automatic section boundaries are initially eight-bar candidates, with repeated-family and chord-cycle analysis. They do **not** reliably identify semantic verse/chorus/bridge roles. Review the recording and enter one section start per line:

```text
1 Intro
5 Verse
13 Chorus
21 Verse
29 Chorus
38 Bridge
```

Repeated labels share a section family. For larger forms, the settings JSON supports `SectionParents`, for example `Song/Movement I/Exposition`. Bar numbers refer to the prepared MIDI's bar grid. Neural meter defaults to the selected 4/4 or 3/4; beat/downbeat inference is provisional. The CLI review settings support `Meter` for rebuilding a neural bundle. For irregular/changing meters, provide a MIDI with an authored meter map.

Key uses the application's A-based pitch classes: A=0, B=2, C=3, D=5, E=7, F=8, G=10. An exact known recording keeps its previously reviewed key. A model track named Singing Voice is highlighted provisionally; use the review selector to correct lead-vocal assignment.

To use an export with the portable Windows player, extract it **beside Resonance.exe**, so its `PreparedSongs/<song>/` folder joins the player's existing library. Keep the WAV, MIDI, patterns, and manifest together.

## CLI examples

```powershell
./Tools/SongLibrary/song-library.ps1 ingest 'D:/Music/song.mp3' --title 'Artist - Song'
./Tools/SongLibrary/song-library.ps1 ingest 'D:/Music/song.mp3' --midi 'D:/Scores/song.mid'
./Tools/SongLibrary/song-library.ps1 ingest 'D:/Music/song.mp3' --neural --offline --meter 3
./Tools/SongLibrary/song-library.ps1 index 'D:/My MIDI Collection'
./Tools/SongLibrary/song-library.ps1 search 'Ticket to Ride'
./Tools/SongLibrary/song-library.ps1 review 'PreparedSongs/Library/my-song' 'review-settings.json'
./Tools/SongLibrary/song-library.ps1 export 'PreparedSongs/Library/my-song' 'Builds/SongBundles/my-song.zip'
./Tools/SongLibrary/song-library.ps1 doctor
```

`--offline` disables public lookup. `--neural` bypasses lookup/cache and runs a new transcription. Local MIDI catalog imports remain indexed across sessions. The public provider can be unavailable; its error is recorded and local transcription proceeds.

## Setup / dependencies

Already installed on this machine: GPU PyTorch 2.7.1 CUDA 12.6, YourMT3 model, and the existing SongPrep environment. GPU tested: RTX 2080 Ti. A fresh setup needs Python 3.12, .NET SDK and FFmpeg/FFprobe on PATH:

```powershell
./Tools/SongLibrary/setup.ps1 -Python 'C:/Path/to/python.exe'
# For machines without an NVIDIA GPU:
./Tools/SongLibrary/setup.ps1 -Python 'C:/Path/to/python.exe' -Cpu
```

Setup uses two isolated environments; inference dependencies do not replace the existing alignment runtime. CUDA wheels need about 3 GB download, checkpoint about 536 MB, plus installed files and per-song WAV/analysis storage. CPU fallback works through the same model adapter but is substantially slower. The setup script pins the model wrapper to a source commit and verifies checkpoint SHA-256 before inference. Model/cache/job state is git-ignored.

## Bundle files

| File | Purpose |
|---|---|
| `recording.wav` | Portable playback audio |
| `aligned.mid` | Timed score; pitches are not silently transposed |
| `aligned.mid.patterns.json` | Unity's precomputed visualization contract |
| `aligned.mid.prepared.json` | Relative paths, hashes, audio duration |
| `song.json` | Editable key, vocal and form settings |
| `library.json` | Method, model hash, lookup scores, provenance, review status |
| `analysis.json`, `report.html` | Quality evidence and review notes |
| `transcription.mid` | Original local neural output, where applicable |
| `timing-map.csv`, `fingerprints.png` | Companion-MIDI alignment diagnostics, where applicable |

CSV is useful for alignment inspection, but JSON is the authoritative analysis format because the section/template/variant hierarchy is nested.

## Verification

```powershell
Tools/SongPrep/.venv/Scripts/python.exe -m unittest discover -s Tools/SongLibrary -p test_pipeline.py -v
```

Tests cover timing/payload preservation, drums, meter, bundle hashes and continuous regions, catalog identity, provider link mapping, wrong-harmony rejection, and path containment. PatternPrep independently verifies lossless reconstruction on every build.

The full Ticket to Ride recording was tested through local inference: 3,430 detected notes, including 2,166 drum hits. This demonstrates end-to-end execution, not transcription correctness. The established companion-MIDI bundle remains available and preferred for normal playback.

## Model and provider provenance

- [MT3-Infer](https://github.com/openmirlab/mt3-infer), pinned commit `3675ad860ea7c0adcaec8498b798ff605d0a0f42`; upstream inference software is alpha.
- [YourMT3 checkpoint/source](https://huggingface.co/spaces/mimbres/YourMT3), YPTF MoE multi-instrument no-pitch-shift checkpoint, SHA-256 `ae38e415c79efd5592dcb9b658cdb99ddb11d4c4e1eaa364cab04a052473fc25`.
- [BitMidi API implementation](https://github.com/feross/bitmidi.com): published search and download fields; no page scraping or invented download links. Availability was intermittent during setup.

Model, wrapper, MIDI arrangements, and recordings have separate upstream licenses/rights. The song export includes the user's recording and derived score, not the model runtime or weights. Neural note/instrument/drum errors, uncertain downbeats, and semantic section labels still require listening and review.


Repeated section sequences are grouped offline into composite orrery carriers, including verse/chorus and longer repeating forms. The active child remains featured at full size while companion wheels stay dimmed nearby.

Prepared pattern bundles include `MelodyStrands`: independent, pitch-ordered voice histories per track/channel, with exact score-second onsets and ends. Simultaneous notes are assigned from low to high; missing voices retain nearest-register continuity. This assumes non-crossing harmony parts, not semantic singer recognition. Runtime only follows these prepared routes. `song.json` accepts `TrackAliases` (original MIDI name to display name); Ticket to Ride maps `Lead Organ` to `Lead Vocals`. Re-run PatternPrep when changing these settings.
