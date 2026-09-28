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
  in the video.

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
