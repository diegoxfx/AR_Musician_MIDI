# Windows piano simulator

The Windows x64 simulator runs the Unity project's `MainScene`, including its world-space menus, bundled and personal song catalog, practice options, countdown and falling notes. It supplies a desktop camera, a mouse pointer and a 61-key virtual MIDI keyboard. Quest camera detection, tracking, USB and multiplayer require separate device verification.

## Build

Use Unity 6000.2.8f1 with Windows Build Support (Mono). Select **Windows x86_64** as the active build target and choose **AR Musician → Build Windows Simulator** in the editor. Keep the executable, its `_Data` folder and the accompanying DLLs together.

For a batch build, set `$unity` to the editor executable, `$project` to the Unity project directory containing `Assets`, and `$output` to an absolute path ending in `AR-Musician-Simulator.exe`.

```powershell
& $unity -batchmode -nographics -quit -projectPath $project -buildTarget Win64 `
    -executeMethod DesktopSimulationBuild.Run -simOutput $output -logFile "$output.build.log"
```

The build command prepares a temporary copy of `MainScene` and deletes the copy when packaging ends. The source scene and Android XR settings are kept as authored. The first Windows import can take a few minutes.

## Practice

1. Open piano setup from the menu. In manual setup, click the virtual keyboard's back-left, back-right and front-right corners, or press **F2** to supply those three exact points. Press **Enter** to confirm. **F2** can also supply a synthetic detection in automatic setup.
2. Press **F3** to calibrate the simulated MIDI range, or press **B** and click the first and last keys. This sends notes 36 and 96 through the same MIDI calibration used by the Quest player.
3. Confirming the pose selects Piano and returns to preparation. Use **Browse songs**, choose a song, then set **Left / Both / Right**, **Learn / Play** and speed. **Start practice** hides the panel for countdown and playback. Click the keys, or type **A W S E D F T G Y H U J K** to play from C4 to C5. **[** and **]** shift the typing octave across the keyboard.
4. **Space** pauses and resumes. **F5** replays the last started song during a session or from song selection after it ends. **F1** toggles this control list.

The desktop player's personal song folder is `%USERPROFILE%\AppData\LocalLow\DefaultCompany\AR-Musician Desktop Simulator\Music` with the project's default company name. Add `.mid` or `.midi` files there and restart the simulator to import them. `import-report.txt` reports the folder, imported count and any skipped files.

**EN / ES** changes the interface language and saves it between launches. **Settings** contains guide sound, MIDI indicators and the fallback pitch split for tracks without hand labels. MIDI calibration is optional for **Play** and required for **Learn**. Pause offers resume, restart and return to the song library.

Launch once with `-simFixture` to add **AR Musician - Repeated Notes QA** to the personal library. It has labeled left- and right-hand tracks, a sustained C3 and three contiguous G4 strikes. This exercises the real MIDI import, hand filters, note-bar spacing and release/repress behavior. The simulator leaves an existing file with that name in place.

## Capture and verify

**F12** saves a PNG of the rendered player. `-simCaptureDir` selects the destination. By default, images go to the `Captures` folder beside `Music` in the player's data directory.

The `-simAudit` flow captures preparation, piano and MIDI setup, the library, English and Spanish settings, countdown, waterfall, and pause. It checks that the pointer reaches the visible controls and exercises selection, practice modes, language switching, resume, restart and return to the library. The audit also supports captures from the original scene UI.

```powershell
& $output -screen-fullscreen 0 -screen-width 1440 -screen-height 900 `
    -simAudit -simCaptureDir $captures -logFile "$captures.player.log"
```

Use `-simVerify` in place of `-simAudit` to import the fixture and verify repeated-note spacing, fresh strikes in learning mode, hand filters, pause, resume and replay. This check also captures the waterfall and the paused view. The player exits with a nonzero code if a check fails.

Use `-simVerifyShutdown` to close the actual player during active learning and check the player log for teardown exceptions after the confirmed shutdown checkpoint.
