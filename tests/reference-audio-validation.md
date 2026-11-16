# MIDI reference piano verification

Run `tests/run-reference-audio-tests.ps1`. Optionally pass `-WaveOutput` with an absolute path outside tracked sources to produce a four-second, deterministic PCM listening sample. The suite exercises pitch, audible energy, bounded chord output, raw-note snapshotting, speed scaling, approach silence, pause/resume, stop, backward/forward seek, invalid positions and pitches, stale-frame coalescing, and a frozen learning onset without repeated attacks.

The reference is a small locally synthesized piano-like instrument, with 24 voices and no external recording, soundfont, download, or keyboard speaker dependency. It does not reproduce the original MIDI instrument programs. The source is nonspatial and routes through Unity's ordinary Quest speaker/headphone output. Its sound toggle only controls the reference; MIDI input, falling notes, and learning remain independent.

## Unity Editor / Quest checks still required

1. Import/compile in the project's Unity version. Start a piano song with reference sound enabled: hear its melody/chords as notes reach the keyboard, after the visual approach delay. Check both bundled songs and imported MIDI; out-of-keyboard-range MIDI pitches can still sound in the guide.
2. Enter learning mode at a chord. The attack sounds once while waiting for input, then decays. Let it wait several seconds: no repeated attacks and no continuous stuck tone. Play the expected chord and verify the next onset sounds normally.
3. Pause during a held note: reference fades within about five milliseconds. Resume during the note: hear only notes still active at the paused timeline position. Quit and select a different song: no old notes leak into the new song.
4. Quit to the start lobby and disable/enable reference sound, then replay. Falling notes and MIDI input continue with either setting. Focus loss/recovery and headset removal must mute/resume through the manager's explicit pause workflow. The transport unit tests also cover changing sound at a current timeline position; the user-facing toggle is available before playback only.
5. Start at different supported speeds. Pitch remains the same; raw MIDI onset times and durations divide by speed once. Speed is captured by `BeginSong`, matching a playback session; changing speed in-session requires rebuilding the song snapshot.
6. Test a dense chord and a long MIDI on Quest. Listen for clipping/clicks, profile the audio thread and confirm no managed allocations in `ReferencePianoSynth.Render`. Test speakers and wired/Bluetooth headphones and account for device-specific output latency.

`Sync` consumes the manager's shared song clock. It schedules each crossed onset once; no additional wall-clock song scheduler or original backing-track player runs. Notes already started decay in real audio time while a learning timeline is frozen. Large jumps reconstruct a bounded set of currently active notes instead of playing a catch-up burst. A maximum of 512 recent notes are inspected during a seek and 24 retained, deliberately limiting work for pathological dense files.

Automated .NET tests establish the PCM and transport behavior. They do not establish Unity audio-device routing, real Quest latency, subjective piano quality, or on-device performance; those require the checks above.
