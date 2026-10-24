# Motion component checks

```powershell
dotnet run --project tests/XTONE.MotionTests/XTONE.MotionTests.csproj
```

The harness references the actual WPF app and runs an STA dispatcher. It animates its own offscreen windows and controls, including fade reversal, pending-task completion, final dependency-property base values, preserved opacity bindings, motion-disabled behavior, privacy-preserving transparent hosts, pixel/star/Auto grid lengths, and modal `DialogResult` preservation during close fades.

It also loads a separate empty library at volume zero, verifies that Focus/Mini routing retains the existing audio engine, reverses a live Focus-to-Mini transition, and checks that the real main window finishes its fade before `Closed`. Its test windows are first shown with `ShowActivated=false`, outside the work area, and never explicitly activated. Fade tests change `ShowActivated` only after the own offscreen host is shown, without calling `Show` or `Activate` again.

`XTONE_DATA_DIR` is overridden only for this process, pointing at a unique build-output fixture directory. The original value is restored and fixture files are removed after all owned windows close. The harness does not use external UI automation, input injection, or media-session commands.
