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
7. Demucs 4.0.1 (`htdemucs`) separates vocals, bass, drums and accompaniment. When reviewed MIDI has a `StemTracks` mapping in `song.json`, stem visuals are extracted from those existing tracks with exact event ticks preserved; no transcription runs. Otherwise YourMT3 transcribes each isolated stem in one local model session. The exact master tempo/meter map supplies ticks without warping note timestamps. High/low accompaniment views split at C4 (261.63 Hz) using complementary zero-phase filtering; these are register views, not inferred instruments. All-instruments combines bass, drums and accompaniment.
8. Validate MIDI/audio hashes, reconstructed notes, and section-region coverage, then publish the completed bundle atomically. Failed work remains in `SongLibraryData/jobs`; Unity never sees half-prepared analysis.
9. Review key, mode, lead vocal and section starts in the manager. Save creates a separate compiled revision. Export creates a ZIP under `Builds/SongBundles` with relative portable paths.

## Playback and review

Authored instrument solos can also use MIDI synthesis in the engine, with the recording as full mix. Run `midi_solos.py <bundle> <groups.json>` after alignment; the groups file is a list of objects with `id`, `name`, and zero-based aligned MIDI `tracks` (alignment inserts a conductor track). These solos preserve authored note timing and use the engine's tonal synth, without Demucs audio or neural transcription. They work on desktop and Android. The manifest marks them `midi-synthesis` and verifies MIDI/pattern hashes without requiring solo WAVs.

Completed imports live in `PreparedSongs/Library`. Restart Unity playback to refresh its prepared-song choices, select the new bundle, and load it. All expensive processing happens ahead of playback. Unity plays `recording.wav`, not synthesized MIDI. **Listen / visualize** selects a solo stem and its prepared pattern data. Full mix restores the original recording and score. Audio and visual stem data preload with the song. Solo changes crossfade synchronized sources without pausing or loading; stems use the same sample rate and exact sample count. The authoritative full-song key, chord progression and region shading remain in force, including while a stem is silent.

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

Use **Add / rebuild stems** beside an existing song in Song Workshop to create a separate stem-enabled revision. New imports include stems automatically. The original bundle stays playable if separation or transcription fails.

## CLI examples

```powershell
./Tools/SongLibrary/song-library.ps1 ingest 'D:/Music/song.mp3' --title 'Artist - Song'
./Tools/SongLibrary/song-library.ps1 ingest 'D:/Music/song.mp3' --midi 'D:/Scores/song.mid'
./Tools/SongLibrary/song-library.ps1 ingest 'D:/Music/piano.mp3' --midi 'D:/Scores/two-hand-score.mid' --piano-hands
./Tools/SongLibrary/song-library.ps1 ingest 'D:/Music/song.mp3' --neural --offline --meter 3
./Tools/SongLibrary/song-library.ps1 stems 'PreparedSongs/TicketToRide-Restored'
./Tools/SongLibrary/song-library.ps1 index 'D:/My MIDI Collection'
./Tools/SongLibrary/song-library.ps1 search 'Ticket to Ride'
./Tools/SongLibrary/song-library.ps1 review 'PreparedSongs/Library/my-song' 'review-settings.json'
./Tools/SongLibrary/song-library.ps1 export 'PreparedSongs/Library/my-song' 'Builds/SongBundles/my-song.zip'
./Tools/SongLibrary/song-library.ps1 doctor
```

`--offline` disables public lookup. `--neural` bypasses lookup/cache and runs a new transcription. Local MIDI catalog imports remain indexed across sessions. The public provider can be unavailable; its error is recorded and local transcription proceeds.

`--piano-hands` requires a ground-truth MIDI with exactly two authored pitched tracks. The higher-register track becomes **Right hand**, the lower becomes **Left hand**, and Right hand is the default white-highlight instrument. The derived aligned MIDI is labeled without modifying the supplied score. Complementary score-guided time/frequency masks create sample-aligned hand solo WAVs that add back to the full recording; shared overtones, pedal resonance, and room sound can still leak between hands.

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
| `stems/*.wav`, `stems/*.mid`, `stems/*.patterns.json` | Sample-aligned solo audio and offline isolated scores / wheels |
| `stems/separation.json` | Separator model, weight hashes, clock and register-filter provenance |
| `song.json` | Editable key, vocal and form settings |
| `library.json` | Method, model hash, lookup scores, provenance, review status |
| `analysis.json`, `report.html` | Quality evidence and review notes |
| `transcription.mid` | Original local neural output, where applicable |
| `timing-map.csv`, `fingerprints.png` | Companion-MIDI alignment diagnostics, where applicable |

CSV is useful for alignment inspection, but JSON is the authoritative analysis format because the section/template/variant hierarchy is nested.

## Lyrics

Put a `lyrics.txt` in a bundle to see its words in lyric mode (View → Lyrics). Use `[Verse 1]`, `[Rap | spoken]` stanzas and hy-phen-at-ed syllables; [Docs/LYRIC-MODE.md](../../Docs/LYRIC-MODE.md) has the format. A MIDI with karaoke lyric events is synced from those events. Otherwise the sheet is synced to the recording before PatternPrep runs:

```powershell
Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/lyric_sync.py PreparedSongs/Library/my-song
```

- **Beats and downbeats.** [beat_this](https://github.com/CPJKU/beat_this), installed in this environment; its 77 MB `final0` checkpoint is cached under `SongLibraryData/models/torch`.
- **Words.** The syllables are aligned to the vocal stem's syllable onsets. Nothing is downloaded.
- **Forced alignment.** `--aligner mms --download-aligner` uses torchaudio's MMS forced aligner instead, fetching about 1.2 GB of weights once.
- **Vibrato.** Read from pYIN pitch.

The result, `lyrics.timing.json`, is evidence to review, not a certified transcription. The pipeline runs this step automatically when a bundle has `lyrics.txt`, `recording.wav` and no MIDI lyric events. Stems carry the master's lyrics.

## Verification

```powershell
Tools/SongPrep/.venv/Scripts/python.exe -m unittest discover -s Tools/SongLibrary -p 'test_*.py' -v
# Audio lyric sync on the rendered public-domain fixture (about two minutes; needs this environment):
Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/test_lyric_sync.py
```

Tests cover timing/payload preservation, drums, meter, bundle hashes and continuous regions, catalog identity, provider link mapping, wrong-harmony rejection, and path containment. PatternPrep independently verifies lossless reconstruction on every build.

The full Ticket to Ride recording was tested through local inference: 3,430 detected notes, including 2,166 drum hits. This demonstrates end-to-end execution, not transcription correctness. The established companion-MIDI bundle remains available and preferred for normal playback.

## Model and provider provenance

- [Demucs](https://github.com/facebookresearch/demucs): MIT-licensed separation implementation, pinned `demucs==4.0.1`; official `htdemucs` weights cached locally (~80 MB). Four instrument stems are estimates and can have leakage. The six-stem model was not chosen because its piano separation is documented as experimental.
- [MT3-Infer](https://github.com/openmirlab/mt3-infer), pinned commit `3675ad860ea7c0adcaec8498b798ff605d0a0f42`; upstream inference software is alpha.
- [YourMT3 checkpoint/source](https://huggingface.co/spaces/mimbres/YourMT3), YPTF MoE multi-instrument no-pitch-shift checkpoint, SHA-256 `ae38e415c79efd5592dcb9b658cdb99ddb11d4c4e1eaa364cab04a052473fc25`.
- [BitMidi API implementation](https://github.com/feross/bitmidi.com): published search and download fields; no page scraping or invented download links. Availability was intermittent during setup.

Model, wrapper, MIDI arrangements, and recordings have separate upstream licenses/rights. The song export includes the user's recording and derived score, not the model runtime or weights. Neural note/instrument/drum errors, uncertain downbeats, and semantic section labels still require listening and review.


Repeated section sequences are grouped offline into composite orrery carriers, including verse/chorus and longer repeating forms. The active child remains featured at full size while companion wheels stay dimmed nearby.

Prepared pattern bundles include `MelodyStrands`: independent, pitch-ordered voice histories per track/channel, with exact score-second onsets and ends. Simultaneous notes are assigned from low to high; missing voices retain nearest-register continuity. This assumes non-crossing harmony parts, not semantic singer recognition. Runtime only follows these prepared routes. `song.json` accepts `TrackAliases` (original MIDI name to display name); Ticket to Ride maps `Lead Organ` to `Lead Vocals`. Re-run PatternPrep when changing these settings.

Ticket to Ride uses the reviewed companion MIDI for all solo visuals: 515 vocal notes in two strands (320 lower / 195 upper), 305 bass notes, 1,569 drum hits and 2,375 accompaniment notes. The duplicate vocal doubling track is excluded. The original master MIDI is unchanged. All seven audio views contain exactly 8,385,536 stereo samples at 44.1 kHz. Audio and prepared visual frames preload at song load; switching uses synchronized sources without decoding or seeking. High/low audio sums back to accompaniment within floating-point precision. Lookup was tested live against BitMidi on 2026-09-19 and returned two Ticket to Ride arrangements. Public MIDI availability does not imply transcription quality; the existing fingerprint gate still applies.

## Existing recording-folder batch

`Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/process_existing_folder.py "C:/Users/johnb/Desktop/resonance-music/StreamingAssets/WAV"` enriches the six already aligned recordings with local Demucs stems and prepared solo views. It verifies source recording hashes, preserves the existing MIDI bytes, derives stem track membership from authored track names, and publishes completed revisions under `PreparedSongs/Library/*-prepared-stems`. It does not run neural retranscription. Failed or interrupted work remains resumable under `SongLibraryData/jobs/current-song-folder`; `batch-report.json` records each result and inherited alignment confidence. New/unmatched recordings require the regular ingest command.
# Listening stories and Director Mode

After alignment, pattern compilation and stem preparation, generate the story:

```powershell
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/library.py story PreparedSongs/<song> --key-file C:/private/openai-key.txt
```

The key file contains only the API key. It is read by the offline request process;
it is never copied into a bundle, Unity, request logs or Git. The Responses API
receives a compact analysis summary (section timing, estimated stable chords,
scored vocal intervals and available stems), not recordings, MIDI, local paths or
lyrics. Requests use `store: false`. The model defaults to `gpt-4.1` and can be
changed with `--model`. Generation is a separate, explicit API operation, never
part of playback or an automatic retranscription of existing scores.

For the workshop's **Generate listening story** action, launch:

```powershell
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/library.py serve --story-key-file C:/private/openai-key.txt
```

For the normal desktop launcher, an optional local `SongLibraryData/settings.json`
can hold `{"storyKeyFile":"C:/private/openai-key.txt"}`. This ignored file stores
only the path; the key stays in its original private file. Restart an already
running workshop after configuring it.

Review the generated `story.json` captions before presentation: generated musical
interpretations can be wrong even with valid JSON. Timings are recording seconds;
`stem: ""` means full mix. Views are `Overview`, `Torus`, `TorusLyrics`, `Timeline`,
`Drums`, and `Lyrics`. `TorusLyrics` keeps harmony primary while showing only the lyric
sawtooth beneath it. Only Torus supports `uncoil`. Short uncoil passages are suppressed so the animation
can settle. The script is bound to audio, MIDI and pattern-analysis hashes; edits
to the analysis require regenerating the story. Existing valid stories survive a
failed request. Exports include the story automatically.

In Unity, open **Song structure → Director mode**. Bottom captions, views and audio
plus visual solos follow the recording clock, including seeks and replay. Turn it
off to restore the previous view and solo. **Reload prepared story** reads edits
without reloading audio. Full-song chord colors remain authoritative during solos.

Song media and all generated scores/analysis belong in local `PreparedSongs/` and
`SongLibraryData/`; per-song reports belong in `Reports/SongProcessing/`. These are
ignored by Git. Keep only the synthetic `Examples/SongBundle/format-guide.json` as
a format specimen, alongside the analysis code and tests. Removing files from Git
tracking does not erase older commits or local media.

## Feeling-led stories, voice and guide arrows

Optional `story.context.json` supplies an `interpretiveLens`, verified `sources`
(id/title/HTTPS URL), and `facts` with supporting `sourceIds`. History stays sourced;
metaphors are presented as listening interpretations. An optional `scenePlan`
fixes timing and choreography while the model writes the captions. Allow about
0.4 seconds per spoken word, plus pauses. Review before rendering speech.

Generate a cached AI voice track after approving the story:

```powershell
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/library.py narrate PreparedSongs/<song> --key-file C:/private/openai-key.txt
```

The workshop also offers **Generate AI narration** using the same private key-file
configuration. Only narration text is sent to the speech API. Default voice is
`onyx` using `gpt-4o-mini-tts`, directed as a deep masculine baritone; no artist impersonation. Prepared WAV and MP3 files
contain narration and timed silence, without the song mixed in. `narration.json`
binds the WAV to the story and recording hashes. Long speech is gently fitted
without pitch changes; excessively long captions are rejected for editing.

In Unity, reload the story and enable **Director mode → AI voice narration**.
The voice follows the recording DSP clock through playback, seeks and pauses.
Music dips during speech and returns between passages. Playback needs no API key
or network. The UI explicitly identifies the voice as AI-generated.

Optional cue fields `annotationTarget` (`none`, `melody`, `drums`, `patterns`) and
`annotationLabel` draw curved arrows to the featured visual. Two vocal strands
receive separate pointers. Labels stay at a fixed upper-left reading anchor; only the pointer tips follow moving notes. Arrows hide during coil transitions and can be disabled
with **Story guide arrows**. No visual-tree changes occur inside paint callbacks.

## Directed tours: hand-written stories, narrators, pictures and ducking

Every song in `PreparedSongs/Library` can carry a hand-directed tour instead of a generated
story. Its source is `story.draft.json` in the bundle: the narrator, the sources, the history
pictures and the timed cues (the same cue fields as above, plus `speech` for a spoken wording
that differs from the caption, e.g. a pronunciation). Publish and narrate it:

```powershell
Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/publish_story.py PreparedSongs/Library/<song>
Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/narration.py PreparedSongs/Library/<song>
```

- **Narrator.** The draft's `narrator` (`name`, Cartesia `voice` ID, `language`) is written
  into `story.json` and wins over the workshop's default voice; `--voice` on the command line
  overrides it. A narrator may name its `provider` (`cartesia`, `elevenlabs`, `openai`); the key
  comes from `<provider>KeyFile` in `SongLibraryData/settings.json`. Songs from the Black American
  tradition use Joshua, an ElevenLabs library voice (`5f49vYETqZvJYfUP28fO`, `eleven_multilingual_v2`);
  the rest use Archie (Cartesia, British male, `en-GB`), the tutorial's voice.
- **History pictures.** A cue names a picture by key. The picture (freely licensed, from
  Wikimedia Commons) is copied to `story/pictures/<key>.jpg`, at most 720 px on its long edge,
  and the cue gets `image`, `imageCaption` and `imageCredit`; `story.json` lists every picture
  with its licence and Commons page. Unity (`StoryPictures`) shows it as a captioned card with
  its credit while the cue plays: above the lyric strip in the overview and lyric views, at
  the top right beside the torus, drum wheel or pattern wheels, or where `imagePlacement`
  (`right`, `left`, `center`) says. Pictures are read when the story loads and decoded one a
  frame; playback only fades the card.
- **Ducking.** For every spoken line the renderer measures the speech band (300 Hz – 4 kHz)
  of the voice and of what plays under it (the recording, or the soloed stem) and stores the
  music gain that puts the voice 10 dB clear, between 0.14 and 0.8, as the segment's `duck`.
  `SongNarration` ducks to it from 0.12 s before the line (16/s) and releases after it (4/s);
  older manifests without `duck` use 0.42. `preview.mp3` beside the narration is the mix
  Director mode plays, for checking the balance by ear.
- **Tours in the song list.** A song with a story and narration shows a **▶ Tour** button on
  its card: it loads the song, waits for the story, stems and voice, and plays the tour from
  its first cue's start in Director mode (a recorded take starts there too, so a silent head
  before the first cue is never played), the camera slowly orbiting the torus in the Torus and
  overview views (paused while it uncoils). The card shows the story's title.
- **Spoken forms.** A draft's `speech` wording is what Cartesia reads: JAY-Z as "Jay Z", R&B as
  "R and B", pronunciations of names.
- **Arrows that leave.** A cue's arrow and label (`annotationTarget`) stay for the whole cue
  unless it sets `annotationUntil` (recording seconds inside the cue; 0 or absent keeps them to
  the cue's end): once the song passes it, `StoryAnnotations` fades them out over 0.4 s and
  keeps them hidden for the rest of the cue. Seeking back inside the cue shows them again.

### Tours written in bars (songs ingested from a score)

For a song ingested with `library.py ingest <recording> --score <score.mscz>`, a director writes cues in printed
bars instead of seconds: `Tools/SongLibrary/directors/<song>.json` (committed) holds the draft with a `bundle`
glob, and each cue says `"from": "17", "to": "21:2"` (bar 21, beat 2; `"9#2"` is the second time bar 9 is
played; `"end"` the end of the last bar). `soloUntil`, `narrationStart`, `narrationEnd` and `annotationUntil` may
be bar specs too (`"annotationUntil": "75:2"` takes an arrow away once the note it points at has played).
`score_bars.BarClock` turns them into recording seconds through the bundle's score import and aligned tempo
map, so every cue follows the performer's timing.

```powershell
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/score_bars.py PreparedSongs/Library/<song> 17 21:2 62
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/build_director_tour.py Tools/SongLibrary/directors/chopin-op10-3.json
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/narration.py PreparedSongs/Library/<song>
```

`build_director_tour.py` writes `story.draft.json` into the newest matching bundle, publishes it and writes
the tour script with every cue's time and words to `Reports/SongProcessing/<bundle>/`.

**Two passages side by side.** A cue's `compare` shows two passages as wheels on one card (`PassageComparison`),
for a point made by comparison, such as an abridged return:

```json
"compare": {"first": ["1", "21:2"], "second": ["62", "end"], "highlight": ["6", "14"], "missingAt": "67",
            "added": ["73", "end"], "follow": [["62", "1", 5], ["67", "14", 6]],
            "labels": ["A", "A′", "bars 6–13", "cut here", "title", "note under the wheels", "outro"]}
```

`highlight` is lit on the first wheel and `missingAt` is cut open on the second; `added` is what the second adds.
`follow` lists runs of bars the second plays as the first did (`[second bar, first bar, bars]`): a ghost playhead
on the first wheel follows the music through them, and where the second skips, the ghost leaps across the lit
span while the cut flashes. Keep the card up across the skip itself (two cues with the same `compare` keep one
card). In the Torus view the card, like a history picture, takes its own part of the screen (the top in a
portrait layout, the left in a landscape one) and the torus moves into the rest.

**A pianist's hand.** A cue that talks about fingering can show the hand (`PianistHand`): a hand seen from above,
palm down as it lies on the keys (a right hand has its thumb at the left), with the finger numbers 1 (thumb) to
5 (little finger) on it, and an arrow from each group's label to each of its fingertips, which glow in the group's
colour (gold, sea green, violet, in the order the groups are written):

```json
"hand": {"side": "right", "groups": [{"fingers": [4, 5], "label": "melody"}, {"fingers": [1, 2], "label": "soft pulse"}]}
"hand": {"side": "right", "alternate": true, "groups": [{"fingers": [1, 4], "label": "thumb + ring"}, {"fingers": [2, 5], "label": "index + little"}]}
```

`side` is `right` or `left`; one to three groups, each of fingers 1–5 (a finger in one group only) and a label of
up to 40 characters (the finger numbers when it is empty or absent). With `alternate` (two groups or more) the
groups take turns with the music: at each onset of that hand's notes (the score track named for it, "Right hand"),
counted from the cue's start, so the first group has the first note; without such a track, at each beat. The
song clock decides, so seeks and recordings agree. The card shows for the whole cue; two cues in a row with the
same `hand` keep one card and count on through both. One graphic per cue: a cue with a `hand` cannot also have a
picture or a `compare` (publishing refuses it, as Director mode does). In the Torus view the card takes its own
part of the screen as a picture does.

**Checking a tour with TypeSafe (Jev).** `tour_check.py` asks TypeSafe's System One model two kinds of question,
with the decisions kept in code and the answers cached by request in `SongLibraryData/typesafe-cache.json`:

```powershell
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/tour_check.py claims <director.json> <evidence.json>
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/tour_check.py beats <director.json> <evidence.json>
```

- `claims`: every spoken sentence against the evidence of its cue's `sourceIds` (a Noul "is this a factual
  statement?" and a Choice supports / contradicts / says nothing); verdicts below 0.8 confidence are marked
  for review.
- `beats`: each beat of the director's story in their own words ("the second swell, with dissonant tension")
  against candidate passages described from the score (a Choice), compared with the bars the cue uses.

The evidence file (sources' passages, the beats, the candidate passages) lives with the song's source files in
`SongLibraryData/scores/<song>/evidence.json`, not in git. The key is `typesafeKeyFile` in settings.

### Narration takes, duck floors, word timing and the narration check

```powershell
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/narration.py PreparedSongs/Library/<song> --check
Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/narration_check.py PreparedSongs/Library/<song>
Tools/SongLibrary/.venv/Scripts/python.exe Tools/SongLibrary/story_timing.py PreparedSongs/Library/<song>
```

- **A new take of one line.** A cue's `take` (a whole number, 0 when absent) is part of its voice's cache identity:
  give a line `"take": 1` (then 2, ...) and `narration.py` renders that line again while every other line comes from
  the cache. ElevenLabs also gets the take as the request's `seed`, so a take is a reading of its own and (best effort)
  the same reading if it is ever rendered again. Take 0 adds nothing, so voices rendered before takes existed are reused.
- **Duck floors.** A story's `duckFloor` (top level of the draft or director file) and a cue's own `duckFloor` (which
  wins) keep a passage's music up under the voice: a linear music gain above 0 and at most 0.8, and the line's `duck`
  becomes the larger of the measured duck and the floor, even where the voice then stands less than 10 dB clear.
  narration.json keeps `duckMeasured` and `duckFloor` beside `duck` on such a line. SongNarration, `preview.mp3` and
  the editor's package all play `duck`; changing a floor renders no speech again.
- **Word timing.** After every render, `narration.py` runs `story_timing.py` in the model environment: each spoken
  line's words (its `speech`, or its caption when it has none) are force-aligned in the voice track by the MMS aligner
  (on the GPU when there is one; about ten seconds for a tour), and its segment gets `spokenWords`, `spokenStarts` and
  `spokenEnds` (seconds from the line's start). `wordStarts`/`wordEnds` carry the same times for SongRecordingCaptions,
  so the lit word follows the voice even where the speech is worded differently from the caption; the editor's
  package reads `spokenStarts`. Run `story_timing.py` by hand to align an existing narration again.
- **Hearing it back.** `narration_check.py` (or `--check` after a render) transcribes every line offline with Whisper
  (`openai/whisper-small.en` from the local Hugging Face cache) and compares it with the script: a phrase said twice,
  words missing, added or changed, and, critical, a changed note name, major/minor or number. What Whisper changes on
  its own is tolerated: punctuation, case, hyphens, Op./Opus, No./Number, digits, dates and $, small words it drops
  or swaps (an/in), homophones (sighs/size) and its spelling of names and foreign words (Gorlatch/Gorlach,
  étude/etude). A repeated single word is flagged, not critical. Transcriptions are cached by each clip's
  samples in `SongLibraryData/narration-check-cache`, so after a new take only that line is heard again. The report
  (`narration-check.txt`, and `.json`) goes to `Reports/SongProcessing/<bundle>/`, with each issue's time in the
  recording; the exit status is 1 on a critical issue and 2 when the narration cannot be checked (a narration older
  than its story).

### Piano concerto tours

`concertoFocus` selects `piano-only`, `orchestra-only`, `piano`, `orchestra`, or `balanced`. These
layouts animate two visual toruses in their existing shared concerto world;
ordinary songs keep their usual cameras. `melodyTarget` selects the guide arrows
for `piano`, `orchestra`, `both`, or `three`. The last option uses the story's
explicit `pianoMelody` (two strands) and `orchestraMelody` (one strand) for a
brief score-based demonstration, then restores each torus's normal melody.
Audio remains the recording unless a cue explicitly selects a MIDI solo.
Ordinary playback keeps both toruses visible and balanced, including rests.
Only Director cues or direct `ConcertoTori.SetFocus` input feature a section.
Director focus changes have a twelve-score-second minimum hold and gradual
viewport and depth transitions. Overview combines both
sections into the original torus. Only piano concerto metadata enables the pair.
`directedMelody: true` uses the story's explicit melody arrays throughout a cue,
including the second movement's flute-to-clarinet handoff.

The first-movement Rubinstein script lives in `directors/rach2-moderato.json`.
Publish it with `build_rach2_tour.py`, using the verified images in
`SongLibraryData/jobs/rach2-tour/images`. Its ElevenLabs narrator is JohnV4;
`delivery` supplies the v4 voice tag directing calm downward sentence endings.
Render with `narration.py <bundle> --provider elevenlabs --key-file <private key>`.
The generated preview includes the recording and measured narration ducking;
`narration.json` reports actual speech and music-only durations.

The second-movement script is `directors/rach2-adagio.json`; publish it with
`build_rach2_adagio_tour.py`. Its authored speech windows reserve half the
recording for clean listening. Both builders retain editable source scripts,
human-readable report scripts, and cached offline voice tracks.

The tutorial's word timings (for the lit word in its caption) come from
`Tools/SongLibrary/tutorial_timing.py`, run after `tutorial_narration.py`.

## Recovering a quiet part a stem transcription missed

`register_notes.py <bundle> [--stem other-high] [--name ...]` detects the onsets in a register
stem's audio, takes each one's pitch from a constant-Q spectrum in that register, adds them to the
stem's notes as their own track and rebuilds only that stem's pattern wheels and manifest entry
(the original notes stay beside it as `<stem>-notes.orig.mid`). Used for Rollout's synth
arpeggiator. A story cue can lift a quiet solo with `soloGain` (up to 4; Unity caps a source's
volume at 1, so the lift is bounded by the master volume).

## Recovering a missed bass stem

If separation leaves bass in accompaniment, review the bass MIDI assignment first,
then run `library.py repair-bass PreparedSongs/<song>`. This extracts recorded
energy around the reviewed bass notes and their harmonics, subtracting the same
signal from accompaniment to preserve the combined mix. It does not synthesize
bass or rewrite MIDI. Shared harmonics can retain instrumental bleed. Backups and
hashes are retained under ignored local data; a repeated identical repair is a
no-op. Rebuilding separation requires reviewing/reapplying the repair. New stem
jobs warn when bass energy is unusually low compared with low-register audio.

## Music-only passages and Cartesia speech

Narration targets two thirds of the recording; the remainder is reserved for
listening. A cue may set `narrate: false` while retaining its caption, view and
solo controls. Optional `narrationStart` / `narrationEnd` (recording seconds)
reserve silence within a cue. All speech must stay inside its cue. The renderer
measures spoken segment durations and rejects a voice track above the target
fraction plus 2.5 percentage points; it does not trim words to meet the quota.
`narration.json` records narratedSeconds, musicOnlySeconds and narrationFraction.
These count speech passages including natural sentence pauses, not phoneme time.

Cartesia is available as a second offline speech provider:

```powershell
Tools/SongPrep/.venv/Scripts/python.exe Tools/SongLibrary/library.py narrate PreparedSongs/<song> --provider cartesia --key-file C:/private/cartesia-key.txt
```

The default Cartesia voice is Miles, a deep mature British male, using Sonic 3.6.
The catalogue does not verify a northern English regional accent. Set `--voice`
to another Cartesia voice ID when desired. Generation is serial to respect lower
concurrency quotas; cached successful clips are reused after a failed job.
The API key is only sent to the selected provider's endpoint.

For the workshop, ignored `SongLibraryData/settings.json` accepts
`narrationProvider: "cartesia"`, `cartesiaKeyFile` (private file path), and optional
`narrationVoice` (voice ID). The separate storyKeyFile still configures OpenAI
story writing. Restart an already-running workshop after changing these settings.
No speech provider or key is needed for Unity playback.

A cue can set `releaseSoloAfterNarration: true` to return audio and visuals to
the full mix at that cue's rendered speech endpoint. This is used for the Ticket
to Ride introduction. The view/caption can remain until the next scene; the solo
and music ducking end with the voice. Seeking backwards restores the solo.

The third-movement script is `directors/rach2-finale.json`, published with
`build_rach2_finale_tour.py`, with JohnV4 downward delivery and half clean music.
Its `song.json` includes score-reviewed `ReviewedKeyRegions` (Beat, Key, Minor,
Evidence); `PatternPrep --key-only <patterns.json>` reapplies only tonal context.
All concerto key regions reach playback, including brief reviewed changes.
Cue `annotationTarget: chord` and `annotationPitchClass` (A=0 through G-sharp=11)
point to both toruses: G=10 for the dominant buildup, C=3 for its resolution.

Recording captions use the spoken narration text. In portrait, a visible concerto
duet centers them between the two toruses; single-torus scenes retain the usual
placement. `annotationTarget: note` plus `annotationMidiPitch` points to an exact
register (the opening bell is left-hand F1, MIDI 29). World-space chord outlines,
melody trails and key guides update after the dual layout, preventing stale-position
flashes. Explicit seek revisions distinguish navigation from slow rendering frames.
