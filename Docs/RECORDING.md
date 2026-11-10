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
   title, channel name and buttons; the torus is framed above them.
2. `RecordingSession` switches to the stacked layout for a vertical video (side by side for a
   horizontal one) and makes the tutorial or tour ready.
3. The Recorder starts (Game View input at the video's size, 30 fps, constant frame rate, H.264
   MP4 with audio) and the session plays it from the top. The Recorder renders every frame and
   the audio in step with it (AudioRenderer), and the app's visuals follow the audio clock, so the
   video never drops frames or drifts from the sound, however heavy a frame is.
4. When the tutorial or tour ends the Recorder stops.

Output, in `Recordings/Videos/`:

- `<name>-<orientation>-<time>.mp4`, the whole take;
- `<name>-<orientation>-<time>.json`, every tutorial step (or tour cue) with its start and end
  in the video, the song's bundle and where its audio starts in the video (tours);
- `<name>-<orientation>-<time>-edit/`, the editor's package (below), built when the take ends.

## The editor's package: re-edit the mix outside Resonance

The MP4 is a finished mix. Every take also gets the parts of that mix, so an external editor
(Premiere Pro, DaVinci Resolve, Final Cut) can re-balance, re-cut or re-caption it. Every file starts
at the video's first frame and lasts as long as the video, so they line up when dropped at 00:00;
audio is 48 kHz, 24-bit.

| File | What it is |
|---|---|
| `narration.wav` | the voice alone (a tour's narration track; the tutorial's step clips) |
| `music.wav` | the original music, never ducked: the song's recording (tours); the app's own synth sound, captured live during the take (tutorial, Quest performances) |
| `music-as-played.wav` | tours whose cues solo a stem: the music with Director mode's solos and crossfades, never ducked |
| `ducking.csv` | one row per spoken line: the music starts to dip 0.12 s before it (`duck_starts`), reaches `music_gain` (also in dB) by `duck_reached`, starts back 0.15 s after it (`release_starts`) and is back by `release_done` |
| `ducking-envelope.csv` | the exact music gain at every video frame |
| `captions.srt` | the spoken words in captions of up to two 42-character lines |
| `timeline.xml` | an FCP7 XML sequence: video on V1, the MP4's own mix on A1 (muted, for reference), the music on A2 with the ducking as Audio Levels keyframes, the narration on A3 at its level in the take |
| `check.json` | how the parts were lined up with the video (cross-correlation against the MP4's own audio), the music and narration levels the take used, how closely the parts rebuild its sound (SNR in dB), and per spoken line the music level measured under the voice against the cue sheet |

The take's mix is music × ducking + narration × a level measured from the take: the app asks for
0.95 of the music's volume, and Unity plays the mono voice into both stereo channels at −3 dB each, so
a tour's narration sits near 0.67 of the music. On the Chopin tour take the parts rebuild the MP4's
sound to 24 dB SNR, and every line's measured duck matches the cue sheet within 0.003. A tour's
parts are rebuilt exactly from its bundle (the recording, the stems, the narration and its measured
ducking), placed where the song started in the video; the tutorial's synth sound exists in no file,
so `RecordingStemTap` captures it while the take records. A Quest performance has no voice, so its
package is its sound and a timeline. Build or rebuild a package by hand:

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
