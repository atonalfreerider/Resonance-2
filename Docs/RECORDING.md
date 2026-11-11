# Recording videos

Videos of the tutorial and of any song's tour, vertical (1080×1920, for YouTube Shorts, Reels,
TikTok) or horizontal (1920×1080), recorded with the **Unity Recorder** (`com.unity.recorder`).

## Record

In the editor: **Tools → Resonance → Record →**

- *Tutorial · vertical (1080×1920)* / *Tutorial · horizontal (1920×1080)*
- *Tour of the loaded song · vertical* / *· horizontal* (load a song with a tour first)

The menu enters Play mode, waits for the app, and then:

1. **Recording mode** (`RecordingMode`) takes the app's controls out of the frame: the View
   bar, the tuck buttons, the song list, the transport, and the tutorial's Back / Next / Skip,
   step count and their row. In a vertical frame the captions start about two thirds of the way
   down (`RecordingMode.CaptionTop`), clear of the bottom of the frame where Shorts put their
   title, channel name and buttons; the torus is framed above them. A tour's pictures and passage
   comparisons take their own part of the frame (the top of a vertical one, the left of a horizontal
   one) and the torus glides into the rest, so the two never cover each other
   (`VisualizationViews.ShareScreen`).
2. `RecordingSession` switches to the stacked layout for a vertical video (side by side for a
   horizontal one) and makes the tutorial or tour ready. A tour whose story or voice was rebuilt
   since its song loaded is read again first, as the song list's Tour button does
   (`SongDirector.NeedsReload`). A tour take whose `narration.json` is stale or still rendering
   is refused before the Recorder starts (run `narration.py`, then record), unless the AI voice
   narration is switched off.
3. The Recorder starts (Game View input at the video's size, 30 fps, constant frame rate, H.264
   MP4 with audio) and the session plays it: the tutorial from the top, a tour from its first cue
   (so a silent head before the music is never filmed). The Recorder renders every frame and
   the audio in step with it (AudioRenderer), and the app's visuals follow the audio clock, so the
   video never drops frames or drifts from the sound, however heavy a frame is.
4. When the tutorial or tour ends the Recorder stops. It also stops when a tour's song clock
   stands still for 5 s of video, and the report's `error` and the console then say "the song
   never sounded" or where it stopped. With the editor in the background the Recorder renders
   about a frame a second and no sound, so keep the editor in front while it records.

Output, in `Recordings/Videos/`:

- `<name>-<orientation>-<time>.mp4`, the whole take;
- `<name>-<orientation>-<time>.json`, every tutorial step (or tour cue) with its start and end
  in the video; for tours also the song's bundle, where in the song the take starts (`songStart`,
  −1 when the song never sounded), when song time 0 sounds in the video (`audioStart`, negative
  when the take starts mid-song, −1 with `songStart`), and what the take played (`storySha256`,
  `narrationSha256`, `narrationPlayed`), as documented on `RecordingSession.Report`;
- `<name>-<orientation>-<time>-edit/`, the editor's package (below), built when the take ends.

## The editor's package: re-edit the mix outside Resonance

The MP4 is a finished mix. Every take also gets the parts of that mix, so an external editor
(Premiere Pro, DaVinci Resolve or Final Cut Pro 7; Final Cut Pro X/11 needs the timeline converted to
FCPXML first, e.g. with SendToX) can re-balance, re-cut or re-caption it. Every file starts at the
video's first frame and lasts as long as its picture, so they line up when dropped at 00:00. Audio is
48 kHz: the narration 24-bit, the music 32-bit float (so the resampled music's peaks over full scale
do not clip).

| File | What it is |
|---|---|
| `narration.wav` | the voice alone (a tour's narration; the tutorial's step clips) |
| `music.wav` | the original music, never ducked: what the take played of the song's recording (tours); the app's own synth sound, captured live during the take (tutorial); the take's own sound, after the performance recorder's loudness pass (Quest performances) |
| `music-as-played.wav` | tours whose cues solo a stem: the music as Director mode played it, never ducked (each solo at its `soloGain`, clamped to 0.25–4 as `StemPlayback` does, and dropped to 1 as the solo fades out; the crossfades) |
| `ducking.csv` | one row per spoken line the take played, read off the take: when the music starts to dip (`duck_starts_s`), reaches the line's level (`duck_reached_s`), starts back up (`release_starts_s`: 0.15 s after the line, or at its end once a `releaseSoloAfterNarration` cue's lines are over) and is back (`release_done_s`); a time is blank where the take never gets there (the next line dips first, or the take ends during the line). Times are seconds from the video's first frame, `music_gain` is linear amplitude (1 = unducked) and also in dB, `cue` is the story's 0-based cue index and `line` counts from 1; the 95% points are where the music has made 95% of the move |
| `ducking-envelope.csv` | the music gain at every video frame, as the take applied it |
| `captions.srt` | the spoken words, at most two 42-character lines a caption, UTF-8 with a BOM and CRLF line ends (how Premiere reads accented text); the narration's own words and their aligned times when `narration.json` has them (`spokenWords` and `spokenStarts`, from `story_timing.py`: the speech the voice was rendered from, so a story edited since does not change them), else the story's speech spread over each line by characters; a `[direction]` inside a line is left out; a take that starts mid-line captions from the first word it played; no file when nothing is spoken |
| `timeline.xml` | an FCP7 XML sequence: the video on V1 linked to the MP4's own sound (muted on the clip and its tracks, for reference), then the music with the ducking as Audio Levels keyframes, the narration, and for a soloing tour the original recording (muted), each stereo file as a linked pair of tracks (left, right) that Premiere joins into one stereo clip, at the take's own levels so the timeline plays as loud as the MP4; a marker at each cue. Paths are absolute: a moved folder asks to be relinked |
| `check.json` | how the parts were lined up with the video, the levels the take used, how closely they rebuild its sound (reproduction SNR in dB), per spoken line the music level heard under the voice against the cue sheet (`offDb`; a line over a rest in the music, with no level to hear, is not measured: `musicShareDb` under −30), and whether the parts match the take at all (`matchesTake`, `problems`) |

The package models the take from the runtime's own rules. The music and the voice are found in the
MP4's sound on their own, each to a fraction of a sample (cross-correlation at 8 kHz, refined at
48 kHz, placed with a windowed-sinc fractional delay): the voice is scheduled on the song clock, which
sits 39 ms ahead of an MP3 recording's first sample (the app's NAudio decoder keeps the encoder delay
that the exporter's decoder trims), and one sample off costs a rebuild 3 to 9 dB, which is what held
the horizontal Chopin take of 10 November at 17 dB. Director mode decides the ducking and the stem
crossfades once a frame, and the music plays each frame's volumes from the 1024-sample DSP block where
that frame stood; takes made before the report's `duckLagFrames` existed (it is 0 now that `SongAudio`
and `StemPlayback` set the volumes in `LateUpdate`) applied them a frame later. A take that starts
mid-song (`songStart`) played nothing before it: song time t sits at `audioStart` + t. Unity plays the
mono voice into both channels at −3 dB, so a tour's narration sits near 0.95 × 0.71 = 0.67 of the
music. The tutorial's synth sound exists in no file, so `RecordingStemTap` captures it while the take
records (a tutorial take from before that capture gets its narration only, which its README notes);
a Quest performance has no voice, so its package is its sound and a timeline.

Whether the parts match the take is checked, not assumed. A take whose report names a `story.json` or
a narration the bundle no longer has (`storySha256`, `narrationSha256`) gets no package: the tool
prints why and exits 2. A take that played no narration gets a package without narration, captions or
ducking. Every other package is measured against the MP4: when the parts rebuild its sound to less
than 15 dB SNR, the narration fits at less than 0.3 or more than 1.5 of the music, narration.wav's
voice is not where the take has it, a line's measured duck is more than 0.5 dB off the cue sheet, or a
cue marker is not where the story puts it, `check.json` and `README.txt` say so and the tool exits 2,
which Unity logs as an error. So does a bundle whose `narration.json` was rendered for an earlier
`story.json` than it has now (its `storySha256`): the app refuses such a narration as stale, so a
narrated take played the story the voice was rendered for, and a story edited since (the speech fixed,
say, before the voice is rendered again) is not the take's. This check and the voice, duck and marker
checks are how older reports, without the hashes, catch a bundle changed since the take. Such a
package never replaces an earlier one that matched its take: it goes beside it as
`<take>-edit-failed`. A package is built in a temporary folder beside the take and moved into place
only when complete. The Chopin tour takes of 10 November rebuild to 26.6 dB SNR, vertical and
horizontal alike, and the Rachmaninoff tour of 3 October (an MP3 bundle) to 23.2 dB; every line's
measured duck matches the cue sheet within 0.08 dB. Building one needs about 1.3 MB of memory per
second of take (1.6 with a soloed stem; it needed 4.6). Build or rebuild a package by hand:

```powershell
Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/edit_package.py Recordings/Videos/<take>.json
Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/edit_package.py Recordings/Videos/<performance>-pov.mp4
Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/test_edit_package.py
```

## Shorts from the tutorial

After a tutorial take, `Tools/Video/cut_shorts.py` runs on its JSON and cuts, on step
boundaries, into a folder named after the take:

| Clip | Steps | Opens on |
|---|---|---|
| Short 1 · The 4 Triangles Hiding in Every Octave | twelve tones → the three-sided band | the band snapping shut |
| Short 2 · What a Key Change Looks Like in 3D | colours → position → key change | the key-change twist |
| Short 3 · Every Note Is Named by Its Relationship to the Key | the key and its labels | — |
| Short 4 · The 1976 Math Shape That Maps All of Harmony | circle of fifths → the credit | — |
| Full tour | everything but the app outro (keeps it under the 3-minute Shorts limit) | — |

Each hook is about a second taken from later in the clip's own span, then the clip plays from its
start. `posting-plan.md` beside the clips has each title, YouTube description and X caption, the
X thread order (Short 1 first, the full-tour link in the last reply) and the two-week schedule.
Run it by hand on any take:

```powershell
Tools/SongLibrary/.venv/Scripts/python.exe Tools/Video/cut_shorts.py Recordings/Videos/tutorial-vertical-<time>.json
```

Posting is not automated: upload the clips yourself.
