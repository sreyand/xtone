# Listening component checks

Run on Windows with the .NET 8 desktop runtime and an available audio output device:

```powershell
dotnet run --project tests/XTONE.ListeningTests/XTONE.ListeningTests.csproj
```

The harness references the actual app project. It creates its own `App`, `MainWindow`, Focus, and Mini windows on an STA dispatcher. Its windows have zero opacity, are moved offscreen, and are excluded from the taskbar. The harness calls the components' own actions and handlers; it does not use external UI automation, input injection, or other applications.

It generates a tagged, silent WAV and an isolated library under its output `fixtures/` folder. `XTONE_DATA_DIR` is overridden only for this test process and restored afterward. Audio is loaded paused at volume zero, and the test confirms view switching preserves the existing engine, audio path, playback version, and position. Its fixture directory is deleted after shutdown.

Checks cover shared metadata, view visibility, return/close behavior, native Focus fullscreen state/chrome/bounds restoration, Mini's own RangeValue peer seeking the paused engine, pin preference, volume synchronization, visualization persistence, and async shutdown with two delayed accepted writes. The RangeValue call is directly against the harness-owned component's provider; it does not locate or operate an external window.

The Windows media check starts only the harness-owned silent fixture at volume zero while Mini is active. It matches an XTONE source ID and the exact fixture title/artist before issuing native Pause, Play, and Pause commands. Other applications' metadata is not requested. Missing native publication or command handling fails the check and prints the app's own SMTC diagnostic; it is not reported as a functionality pass.

Exit codes are `0` for all checks passing, `1` for a failed check, and `2` when Windows denies native media-control access. On native access denial, the remaining component checks still run, but native functionality is explicitly reported as unverified.

Rendered PNGs of owned WPF content are saved under `renders/` inside the test build output (normally ignored by Git), at the requested main/focus/mini sizes when the screen work area supports them. Set `XTONE_RENDER_DIR` to an explicit absolute QA directory to override that location, for example:

```powershell
$env:XTONE_RENDER_DIR = Join-Path $env:TEMP 'xtone-listening-renders'
dotnet run --project tests/XTONE.ListeningTests/XTONE.ListeningTests.csproj
```

Required controls are checked for positive size and bounds inside each layout. Component renders do not verify taskbar behavior or an actual foreground desktop interaction.

Pointer-focus regressions exercise the rendered Main/Focus/Mini transports and the actual new-folder dialog primary action with component `Focus()` calls. They verify focused control chrome does not gain a persistent border while the shared keyboard-only `FocusVisualStyle` remains available. Mini pin checks preserve the checked appearance and clear the unchecked appearance. Music list geometry checks folder and loose-track icon alignment and horizontal insets. These are component-state checks, not injected mouse/keyboard input or proof of a live desktop gesture.
