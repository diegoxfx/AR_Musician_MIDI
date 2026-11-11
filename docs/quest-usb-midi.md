# Quest USB-MIDI extension

This extends the original AR-Musician Unity application from
`MixedRealityETHZ/AR_Musician`, starting at `bce92f5` (Code Release).
It targets single-player practice with a 61-key Casio CT-S200 and a standalone
Quest 3. A computer is needed to build/install the Android app, but is not part
of the proposed practice connection. No PCVR service or audio-to-MIDI model is used.

## Why MIDI is useful here

The existing vision/manual setup estimates the keyboard's position, orientation
and width. It does not need to recognize a finger pressing each key. MIDI supplies
the exact note number and press/release events. Two physical endpoint presses
associate MIDI pitches with the detected rectangle. Both falling notes and key
feedback then use the same layout and song clock.

This is a useful extension because it enables immediate press feedback and
basic timing scores without training a new vision model. It does not fix a
misdetected rectangle: verify alignment at the low, middle and high keys and use
the existing manual plane setup if automatic detection is inaccurate. The current
layout assumes uniformly sized white keys, with black keys over their boundaries.

## Connection

The CT-S200 provides USB-MIDI through a **Micro-B** port. Use a data-capable
USB-C to Micro-B cable, or a USB-C host adapter plus a USB-A to Micro-B data cable.
Power the keyboard from its own adapter/batteries. The proposed path is:

```text
CT-S200 USB Micro-B -- USB data cable -- Quest 3 USB-C (host)
```

Keep the keyboard in normal playing mode with transpose/octave changes disabled;
do not run its internal song playback during this test. Casio documents USB-MIDI
and notes that MIDI exchange is unavailable during internal song playback in the
[CT-S200 guide, EN-30](https://www.casio.com/content/dam/casio/global/support/manuals/electronic-musical-instruments/pdf/008-en/c/CTS200_usersguide_B_EN.pdf).
Its [product page](https://www.casio.com/us/electronic-musical-instruments/product.CT-S200BK/)
lists 61 keys, USB-MIDI and separate power options.

The Android bridge uses the platform
[MIDI API](https://developer.android.com/reference/android/media/midi/package-summary)
and opens a USB device's **output** port to receive notes. Availability depends on
the headset OS exposing that device through `MidiManager`; Android's
[MIDI implementation](https://source.android.com/docs/core/audio/midi) explains the
USB host dependency. This exact Quest/Casio combination has **not been tested on
hardware**. If the MIDI service is absent or the keyboard is not enumerated, the
HUD reports it and vision-only playback remains available. There is no raw USB
driver fallback in this version. Do not assume a successful Android compilation
proves hardware compatibility.

MIDI carries performance events here, not the keyboard's audio. Listen to the
Casio's own speakers/headphones. The extension neither synthesizes nor records audio.

## Build and setup

1. Follow the upstream README using Unity **6000.2.8f1** with Android build support.
2. Open the `AR Musicians` project. Download and assign the upstream ONNX models
   as described in the README; they are not bundled in this branch.
3. Build `Assets/Scenes/MainScene.unity`. This branch enables that existing scene
   and removes the missing `TestScene.unity` build entry. Check any active Build
   Profile that overrides the shared scene list.
4. Build/install to Quest, then disconnect the computer and connect the keyboard.
   This is an Android standalone build, not a Windows/PCVR build. The Editor shows
   that USB input requires the Android player.
5. Open **Configure Instrument**, enable **CV Detection**, and select piano.
   Allow both headset camera and spatial-data permissions. Look at the whole
   keyboard and press **X on the left controller** to detect it; then press
   **A on the right controller** to confirm the detected surface. Detection must
   locate all three required corners in 3D before confirmation is possible.
   A camera preview alone is not a completed calibration. Retry with X if the
   surface is incomplete, or disable CV Detection for manual setup.
   The scene defaults to **61 keys, C2**. Detected corners must span the playable
   keyboard, not the outer case. MIDI calibration then verifies the actual range.
6. Wait for the HUD to report the connected USB MIDI keyboard.
7. While no song is active, press **B** on the right controller. Press and release
   the physical leftmost key, then the rightmost key. They must be white keys
   separated by 60 semitones. An invalid upper key can be retried; B restarts.
8. Check a low, middle and high key. Each press should light the matching location.
   Press **X** on the left controller if the horizontal direction is reversed.
   If positions still disagree, redo the visual/manual plane setup.
9. Open **Singleplayer > Piano**, choose a song with **< / >**, press **Select**,
   then **Start**. **Y** toggles MIDI feedback while idle. **A** retains
   the existing pause-menu action. Setup controls are disabled during an active
   song, including the start/resume countdowns and while paused.

Calibration is deliberately not persisted across app restarts, USB resets or
disconnections. A reset clears held keys and disables scoring. If it happens
during a song, quit that song, reconnect if needed, recalibrate with B, and restart.
Suspending the single-player app cancels pending countdowns. Before playback it
returns to the start lobby; during a session it exposes the pause menu. Returning
to the app never starts or resumes a song automatically. A normal A-button pause
retains calibration and can resume; notes pressed during the pause do not score.
Opening the Quest Universal Menu also pauses practice and ignores MIDI input.
Focus-only interruptions retain completed calibration, clear held keys/flashes,
and cancel incomplete calibration. Actual app suspension resets the USB connection
and requires the recalibration/restart workflow above. App pause, Unity focus and
Meta input focus are independent gates: all must be restored before accepting input.
See Meta's [lifecycle events](https://developers.meta.com/vr/documentation/unity/unity-lifecycle/).
Multiplayer keeps its existing RPC-coordinated pause/resume policy: local focus
loss gates MIDI input but does not pause only that peer's running song clock.
Pending countdowns still cancel when interrupted. Networked recovery remains
outside this extension's tested scope.

## Automated test APK build

The editor entry point `QuestApkBuild.Build` builds `MainScene` as one ARM64/IL2CPP
development APK with Android debug signing. It sets package `com.armusician.midi`,
version 1.1.1 / code 4, minimum API 32 and target API 35. Install Unity 6000.2.8f1
with Android Build Support, SDK/NDK tools and OpenJDK, and activate a Unity license.

Download these files from the upstream README's public Polybox share into
`AR Musicians/Assets/Models/Quest/` (do not commit the large model binaries):

- `conv_shuffle-vits16_240x320_merged.onnx` (101,972,607 bytes), SHA-256
  `aac14d75ca2be301aa2c4eddc6fe3d47905d888995f9908ab6bdd63363d414ad`.
- `bongo_detector_240x320_384d_bilinear_kpts_merged.onnx` (195,404,132 bytes), SHA-256
  `8a39c40551c1485ffeefd219aa8a95ab0291bddeca3310c65f11347cd6d0c220`.

Run the editor from PowerShell, replacing the three absolute paths below:

```powershell
& '<Unity editor>/Unity.exe' -batchmode -quit `
  -projectPath '<checkout>/AR Musicians' -buildTarget Android `
  -executeMethod QuestApkBuild.Build -androidTargetApi 35 `
  -apkOutput '<output>/AR-Musician-MIDI.apk' -logFile '<output>/unity-build.log'
```

Use a local build checkout outside synced folders and an output directory outside
Assets/Packages. The method validates the Android OpenXR loader, imports and assigns
both models through Unity's asset API, saves the scene and applies the test-build
settings. It reopens the saved scene and verifies live model references and scene
dependencies, then requires model weights in Unity's packed-assets report. This
guards against an apparently successful APK that omits vision models. It changes
that checkout's scene/player settings. It also writes
`<apk>.build-report.json` and exits nonzero on failure. This creates an APK without
installing it on a headset or generating a separate OBB file.

With a Quest connected and USB debugging authorized, install the resulting file
using the Android SDK's `platform-tools/adb`:

```powershell
adb install -r '<output>/AR-Musician-MIDI.apk'
```

Disconnect the computer after installation and connect the CT-S200 directly to
the headset. The APK is a development build for local testing, not a store release.

## Personal MIDI songs on Quest (1.1.0)

The app scans `Application.persistentDataPath/Music` once on startup, in addition
to its bundled songs. For package `com.armusician.midi`, the primary Quest user's
folder is:

```text
/sdcard/Android/data/com.armusician.midi/files/Music/
```

Install the updated APK with `adb install -r`, open it once to create the folder,
then copy standard MIDI files (`.mid` or `.midi`, case-insensitive) there. No Unity
build, JSON conversion or catalog editing is needed. Only files directly inside
Music are scanned, not nested folders. Fully close and reopen the app after
adding, replacing or removing songs; resuming from the Quest menu does not rescan.

In PowerShell with Android's `adb` on PATH:

```powershell
adb push 'C:\Music\my-song.mid' '/sdcard/Android/data/com.armusician.midi/files/Music/'
```

Alternatively, from the repository root, the helper copies one or more MIDIs and
any accompanying metadata files. Supply the actual `adb.exe` path if not on PATH:

```powershell
./tools/copy-quest-songs.ps1 -MidiFiles 'C:\Music\one.mid','C:\Music\two.midi' -Adb '<Android SDK>/platform-tools/adb.exe'
```

Use `-Serial '<device serial>'` when more than one ADB device is connected. The
helper does not terminate the running app. To restart it explicitly after copying:

```powershell
adb shell am force-stop com.armusician.midi
adb shell am start -n com.armusician.midi/com.unity3d.player.appui.AppUIGameActivity
```

Choose **single-player**. Personal songs appear after the bundled songs in filename
order, labelled `Personal MIDI`. Their default title is the filename and their
duration is calculated from actual note ends and MIDI tempo changes. The importer
reads all tracks, including percussion if present; use a piano arrangement suited
to the keyboard. Existing calibrated-range rules skip notes outside the 61 keys.

Optional metadata belongs beside the MIDI as `my-song_metadata.json`:

```json
{
  "title": "My song",
  "artists": ["Composer"],
  "releaseYear": 2026,
  "difficulty": "Easy"
}
```

Difficulty may be `Beginner`, `Easy`, `Medium`, `Hard`, `VeryHard` or `Professional`.
Missing or invalid metadata falls back to the filename, calculated duration and
`Unknown` difficulty. Duration in a sidecar never overrides the MIDI duration.
External song JSON caches are neither required nor read: replacing a MIDI is
reflected at the next startup even if an old same-name JSON is present. The app
never rewrites the source MIDI or metadata. Songs are immutable in-memory
snapshots for the running session, so copying or deleting a file cannot alter a
song during playback.

A malformed or unsupported MIDI is skipped without blocking valid songs or the
bundled catalog. Import diagnostics are saved as `Music/import-report.txt`:

```powershell
adb pull '/sdcard/Android/data/com.armusician.midi/files/Music/import-report.txt' .
```

To bound startup parsing and memory, the importer accepts at most 1 MiB per MIDI,
64 KiB per metadata file, 256 MIDI candidates, 100,000 notes per song, 250,000 notes
and 32 MiB of MIDI input per scan, and 24 hours per song. A directory exceeding
4,096 entries is rejected. Limits and per-file errors appear in the report.

Personal songs are excluded from multiplayer because the original network code
synchronizes catalog indices rather than transferring song content. Mode changes
also block starting/resuming an already loaded personal song in multiplayer.

The folder uses app-specific storage and needs no additional storage permission.
It is preserved by an in-place APK update with the same package/signature;
uninstalling the app or clearing its storage removes it. Keep source MIDIs on
your computer. File-manager access to Android/data varies by Horizon OS version;
the documented transfer method is ADB, not an unrestricted Downloads-folder scan.

## Feedback and songs

- Held keys light green. An unmatched press flashes red during a scored song.
- A matching pitch within **300 ms before or after** the scheduled onset counts
  once as a hit. Events from multiple tracks at the exact same pitch and onset
  count as one physical press. Distinct onsets, however close together, remain
  separate. Missed onsets and extra presses have separate counters.
- Chords can match multiple pitches. Sustained duration, pedal, dynamics and
  fingering are not scored. There is no automatic wait-for-correct-note mode.
- Notes outside the calibrated range are skipped visually and excluded from the
  score; the HUD counts them separately. Complex arrangements may contain tracks
  unsuitable for a 61-key beginner part. Start with `all-my-ducks`.
- Song speed affects visual timing and scoring together. Replaying a song clones
  its original events, so duration scaling does not accumulate between attempts.
- MIDI ports/channels are not separate instruments in this UI. One USB device and
  its first output port are selected; Casio is preferred on initial discovery,
  and the selected device stays stable until removal. All 16 channels are accepted.

New JSON exports include a canonical `midiNumber` (middle C = 60) and a scientific
pitch name (C4). Original bundled JSON used names one octave above this convention.
`PianoStrategy.legacySongOctaveOffset = -12` corrects those old key-name-only songs;
numeric `midiNumber` takes precedence. All 13,819 note pitches in the three bundled
JSON/MIDI pairs were compared as histograms and agree with this conversion. That
check validates pitch content, not event ordering or timing. For third-party JSON
already using scientific names, supply `midiNumber`, regenerate with the updated
converter, or explicitly set the legacy offset to 0.

## Architecture and limits

- `QuestMidi.java` / `MidiStreamDecoder.java`: Android USB discovery, byte-stream
  parsing, bounded polling queue, hotplug and stale-callback guards. Handles split
  packets, running status, interleaved realtime bytes, velocity-zero note-offs,
  and channel reset messages. Disconnect/overflow clears held state.
- `MidiCore.cs`: pitch parsing, white/black geometry, endpoint calibration,
  channel-aware held state, one-use onset matcher; no Unity dependency.
- `PianoMidiFeedback.cs`: runtime-created HUD/markers, controller setup, lifecycle
  and optional scoring. No additional scene wiring or paid MIDI plugin required.
- `PlaneController`, `PianoStrategy`, `RhythmGameManager`, `CubeFallBehavior` and
  `MidiUtils`: shared layout/pitch/timing integration into the existing application.

Playback has one authoritative state in `RhythmGameManager`. Single-player
transitions and recovery follow this table:

| State | Transition | Interruption recovery |
| --- | --- | --- |
| Idle | Start -> Starting | Stay idle; input/setup blocked until focus returns |
| Starting | Valid countdown finishes -> Playing | Cancel generation -> Idle and start lobby |
| Playing | Pause -> Paused; finish/quit -> Idle | Pause clock and show pause menu |
| Paused | Resume -> Resuming; quit -> Idle | Remain paused |
| Resuming | Valid countdown finishes -> Playing | Cancel generation -> Paused and pause menu |

Each countdown carries a generation token. Quit, song replacement, component
disable and lifecycle interruption cancel it; an old coroutine cannot start or
resume a later session. The song clock stays frozen throughout pause and resume
countdown. Instrument selection is accepted only while idle with input available;
the manager updates both the strategy and shared instrument setting together.
Song preprocessing happens when playback starts, using that accepted strategy.
The scene's Start/Resume buttons hide menus before requesting their countdown.
If loading, readiness or focus prevents the request, the countdown controller can
restore the appropriate lobby/pause menu without a later button listener hiding it.

Input is scored when Unity drains the native queue. Transport latency and frame
stalls can therefore affect scores; this is learning feedback rather than a
precision performance measurement. A bounded overflow cancels scoring and asks
for recalibration rather than leaving stuck keys or silently trusting lost events.

The project currently disables Android minification. If enabling R8/ProGuard,
retain `com.armusicians.midi.QuestMidi` and its public static methods, which Unity
calls by name. MIDI and USB-host manifest features are optional so the original
application can still run without MIDI.

## Verification

Run from the repository root:

```powershell
./tests/run-core-tests.ps1
pwsh -File ./tests/run-integration-tests.ps1
pwsh -File ./tests/run-song-import-tests.ps1 -NewtonsoftDll '<Unity Newtonsoft package>/Runtime/Newtonsoft.Json.dll'
pwsh -File ./tests/run-song-loading-tests.ps1 -NewtonsoftDll '<Unity Newtonsoft package>/Runtime/Newtonsoft.Json.dll'
./tests/java/test.ps1 -JdkBin '<JDK bin directory>' -AndroidJar '<Android SDK platform>/android.jar'
```

The core suite compiles the actual pure C# code with warnings treated as errors.
The Java suite tests the actual decoder, tests device lifecycle/queue behavior
against small simulated Android classes, then optionally compiles the actual
bridge against the official Android API jar. No test output is written into Assets.

The integration suite compiles the actual Unity-facing classes, including the
countdown, instrument selector and menu controller, against lightweight stubs with
PowerShell 7 / Roslyn. It exercises calibration, resets, pause/resume, interrupted
countdowns, stale coroutine generations, focus/pause callback order, instrument
selection, replay, speed scaling, late-frame placement and scoring. It also checks
the scene's serialized selector references/callbacks and scores each unique onset
in bundled Sandstorm once (999 duplicate track events must not become misses).
It does not exercise real rendering, Android calls, JSON/network loading or the
Unity engine lifecycle; callbacks and coroutine advancement are simulated.

Verified in development: 365 core assertions, 134 integration assertions, 40
scene-wiring assertions, the bundled Sandstorm scoring regression, Java decoder and simulated-device
regressions, and bridge compilation against Android API 35. These source-level
checks do not validate headset behavior, timing or visual alignment.

External-song tests add 28 import assertions using the shipped DryWetMidi parser
and Newtonsoft, plus 57 loading/selector assertions against actual production
sources with Unity stubs. They cover tempo changes, chords, overwritten/deleted
files, bounds and corrupt inputs, metadata fallback, startup publication, copied
snapshots, empty/loading menus, stale song-load completion and multiplayer mode
changes before loading, countdown, playback or resume. The scene Select button
now invokes only the guarded selection handler: loading/empty states cannot
navigate prematurely into the play lobby.

The initial 2026-11-10 Unity 6000.2.8f1 build produced the standalone ARM64/IL2CPP development
APK (version 1.0.1, package `com.armusician.midi`, 341,800,066 bytes). Unity returned
exit code 0 and `BuildResult.Succeeded`. Reopened scene references/dependencies
were verified; the packed-assets report contains 100,765,836 bytes for the piano
model and 89,208,460 bytes for the optimized bongo model. Android `apksigner`
verified the debug signature; `aapt2` confirmed ARM64, minimum API 32, target API
35 and the VR launch activity. APK SHA-256:
`f82bb9c610f2d4f860c2564b1bf707b240accc110bff369d5b6aec8e9ed8a5bb`.

The successful build was not error-log-free: its report contains 358 warnings and
one caught Meta Voice/Conduit editor preprocessing exception (duplicate
`Assembly-CSharp` key). The app's piano/MIDI scripts do not use Conduit voice
commands. Preserve the report and log when investigating headset issues; a
successful build and package inspection do not establish runtime compatibility.

The external-song update was built on the same date as version 1.1.0 / code 3,
with the same application ID and signing certificate for in-place updates. The
final APK is 342,001,458 bytes, SHA-256
`05c80a2e5db3b93ec9ed33570aea4c351095daefa404b37cbb4619a5f812d1f9`.
Unity exited 0 with `Succeeded`; both model references and packed model weights
were verified. This build reports 361 warnings and the same one caught Conduit
exception. Android tools verified the signature, ARM64 ABI and version. The new
folder scan has source-level integration coverage, but transferring and importing
a MIDI on the physical Quest remains a device acceptance test.

The 1.1.1 setup fix addresses two reproduced configuration defects. The headset
had camera access but lacked `USE_SCENE`, so camera inference could run while
environment raycasts could not produce the three corners needed for A to confirm.
CV setup now requests both permissions, reports missing data on screen, and only
accepts a complete valid set of three hits. Repeating or canceling setup discards
unconfirmed points. During setup, MIDI does not also interpret X as a key-layout
reversal; returning to the menu restores the MIDI controls.

A Unity Editor probe of the actual scene also reproduced an
`ArgumentNullException` in `PianoMidiFeedback.Initialize`: the note prefab referred
to a missing material. The prefab now references a tracked material, and MIDI
initialization validates its resources and cleans up safely on failure. The APK
build runs `QuestRuntimeValidation.Run` before packaging to verify the real scene,
material, shader, font and feedback initialization. This Editor check is separate
from the controller/state tests and does not validate Android USB or physical
keyboard detection.

Before treating the feature as ready to use, complete this device check:

- Install the generated MainScene APK and check launch, controller input and camera access.
- Connect CT-S200 directly to Quest and confirm the connected status and events.
- Calibrate endpoints and check alignment for low/middle/high white and black keys.
- Play a short melody and a chord; verify hits, misses, extra presses and release.
- Pause/resume, replay at another speed, and check final-note scoring.
- Unplug while holding a key, reconnect, and suspend/resume the headset; verify
  cleared markers, a reachable pause menu, and the recalibration/restart workflow.
- Disable MIDI feedback and verify ordinary visual playback and bongo selection.

Quest/CT-S200 hardware validation remains pending. The build preserves upstream
dependencies and input settings: Unity warns about Android input handling `Both`
and 16 KB alignment in `libUnityARFoundationMeta.so` and `libhaptics_sdk.so`.
The upstream bongo model has a two-dimensional keypoint output while the common
parser indexes three dimensions; bongo inference is outside this piano/MIDI test
and is not validated by a successful APK build.
