# Meta Quest (hands only)

Resonance runs on a Meta Quest 3 as an OpenXR app driven by hand tracking alone. The desktop
build is unchanged: the headset session starts only when an XR display is running (or when the
editor's simulation is switched on).

## What you see

- **The torus** stands in front of you at chest height, about 2.2 m across. Its near rim is
  about 35 cm from you, so you can reach into it or walk around it.
- **The drum wheel** stands under it.
- **The lyric line** floats just above it. The rhyme graph is not shown in the headset.
- **The pattern wheels** stand behind the torus as a solid object facing you, their lowest edge
  just above the torus so nothing hides them (`PatternWheel3D`). The pinion is a thick toothed
  ring, the song wound once around, one slab per section visit; a family's relief and stripe
  spacing stand in for the desktop's hatch. The rack runs down its left side. The orbit carriers
  float at their own depths in front of the ring, with each family as a small planet, and the
  section playing is the planet at the centre, nearest you, its loop rolling inside it as a moon.
  The instrument stacks stand in a grid to the right, under the caption.
- **The room** shows through (passthrough), or everything outside the scene is black (blackout).

The scene is seen through a rig scaled so 1 m in the room is 2 scene units (`VrSession.Scale`).
The desktop's flat panels are switched off in the headset. They cost most of a frame on the
headset's CPU, and nothing in the headset shows them.

## Hands

- **Palm menu.** Turn your left palm toward your face. A column of buttons stands above it:
  Songs, Play/Pause (a loading percentage while a song loads), Play the torus / Song view,
  Passthrough / Blackout, Recenter. Push a button with your right index fingertip.
- **Song list.** Opens by itself when no song is loaded, and from the palm menu. Each card shows
  the title and the song's chord progression as a strip of colours, ten to a page. Poke a card
  to load it.
- **Seek.** Pinch the rack beside the pattern wheels and drag up to go forward, down to go back.
- **Recenter** puts the torus back in front of you, wherever you are facing.

## Torus play

Choose *Play the torus* on the palm menu. The song pauses and the torus stands alone.

- **Play notes.** Move a fingertip over the torus. The note nearest the finger sounds, louder
  the closer you are. It never bends: a new note takes over only once it is clearly nearer.
  Each hand plays its own note.
- **Change key.** Reach just outside the torus's outer rim, where there are no notes. Small
  handles appear under your finger. Pinch and drag:
  - around the ring (clockwise seen from above) for a fifth up per step, back for a fifth down;
  - up or down around the tube for a major third up or down per step.
  Every step twists the torus into the new key. Keep dragging for more steps.

## Building and installing

The build runs in batch mode in a copy of the project, so the open editor never switches
platform. From Git Bash in the repository:

```bash
powershell -Command "foreach($d in 'Assets','Packages','ProjectSettings'){ robocopy $d ..\Resonance-quest\$d /MIR /NFL /NDL /NJH /NP }"
```

```bash
"/c/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Unity.exe" -batchmode -quit -projectPath ../Resonance-quest -buildTarget Android -executeMethod QuestBuild.Build -logFile ../Resonance-quest/build.log
```

```bash
adb install -r ../Resonance-quest/Builds/Quest/Resonance.apk
```

`QuestBuild.Build` configures the Android side first (menu: *Tools/Resonance/Quest/Configure
Android XR*): the OpenXR loader, the Meta Quest, hand tracking, hand interaction and
passthrough features, and IL2CPP / ARM64 / Vulkan / ASTC player settings. Controller
interaction profiles are switched off, since the app is hands only. The package is
`com.primitive.resonance`. `adb` ships with Unity's Android support under
`Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools`.

## Songs on the headset

Songs are copied over USB, not built in. Only fully prepared songs are copied, and only what
playback needs: the aligned score, its sidecars, the recording, and the story title. Stems and
narration stay on the computer, since decoded stems would not fit the headset's memory.

```bash
python Tools/SongLibrary/deploy_quest.py
```

It takes `--only <part of a folder name>`, `--list`, `--remove <name>` and `--dry-run`.
Files already on the headset at the same size are skipped. The headset reads them from
`/sdcard/Android/data/com.primitive.resonance/files/Library/`.

## Rendering on the headset

On the device the app swaps in `Assets/Quest/Resources/QuestPipeline.asset`: the desktop URP
asset without HDR, with 4x MSAA, and with alpha kept through post-processing so passthrough
shows where nothing is drawn. Make it again after changing the desktop pipeline with
*Tools/Resonance/Quest/Make headset pipeline*.

Only the eye camera and the lyric strip's own camera render. Every UI Toolkit panel is given a
copy of its settings that renders into a tiny unseen texture with its content hidden, so no
panel can draw over the eye buffer and the desktop's assets stay unchanged.

The app logs what decides passthrough when a session starts and when passthrough is switched
on: the pipeline, the eye camera's clear colour and formats, the AR session state, and anything
else still drawing to the screen.

```bash
adb logcat -s Unity | grep "VR session\|VR passthrough"
```

Frame rate, from the headset's own counter:

```bash
adb logcat -s VrApi | grep FPS
```

## Trying it without a headset

Tick *Tools/Resonance/Quest/Simulate in editor* and enter play mode. The view becomes the
headset's, the mouse is the right index fingertip at arm's length, and the left button pinches.
`HandInput.SimulatedPalm` holds the left palm up, which shows the palm menu.

## Code

| File | Role |
| --- | --- |
| `Assets/Quest/VrSession.cs` | Starts the headset session, builds the rig, places everything, passthrough, modes |
| `Assets/Quest/HandInput.cs` | Fingertips, palms and pinches from XR Hands, and the editor stand-in |
| `Assets/Quest/VrButton.cs` | A button pressed by pushing a fingertip through it |
| `Assets/Quest/VrMenu.cs` | The palm menu and the song list |
| `Assets/Quest/TorusTheremin.cs` | Torus play: nearest-note theremin and the key-change handles |
| `Assets/Quest/PatternWheel3D.cs` | The pattern wheels and instrument stacks as 3D geometry |
| `Assets/Editor/QuestBuild.cs` | Android XR configuration, the headset pipeline, the batch build |
| `Tools/SongLibrary/deploy_quest.py` | Copies prepared songs to the headset |
