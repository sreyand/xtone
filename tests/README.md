# Regression checks

These standalone .NET console harnesses link production services and use isolated state. They do not load the personal XTONE library. Run them on Windows with the .NET 8 SDK:

```powershell
dotnet run --project tests/XTONE.QueueTests
dotnet run --project tests/XTONE.LibraryTests
dotnet run --project tests/XTONE.AudioTests
dotnet run --project tests/XTONE.ArtworkTests
dotnet run --project tests/XTONE.PresentationTests
dotnet run --project tests/XTONE.ListeningTests
dotnet run --project tests/XTONE.MotionTests
dotnet run --project tests/XTONE.FluidLayoutTests
```

Queue checks cover duplicate entries, stable current identity, reorder/remove, shuffle, repeat, history and explicit Play Next. Library checks cover caching, availability/relink, atomic persistence and recovery, cancellation, concurrent edits, codec classification and folder watching.

The library and audio harnesses also accept a fixture directory containing `fixture.wav`, `fixture.flac`, `fixture.m4a` (ALAC), and `fixture.mp3`. Use roughly two seconds of non-silent stereo 48 kHz audio. Fixture generation tools are not shipped with XTONE.

```powershell
dotnet run --project tests/XTONE.LibraryTests -- C:\path\to\fixtures
dotnet run --project tests/XTONE.AudioTests -- C:\path\to\fixtures --devices --playback
```

The audio device checks play muted and check real native decode/seek, rendered transition timing, paused restore, cancellation, endpoint switching and end events. See `XTONE.AudioTests/README.md` for output and padding limitations.

Presentation checks cover shared metadata, notifications, time/volume sanitization and stored listening preferences. Listening component checks instantiate the actual app and its windows on an STA dispatcher, with their own muted library and offscreen windows. They exercise view switching, shared playback identity, preferences, layouts and draining pending writes on close. See `XTONE.ListeningTests/README.md` for the distinction between component rendering and native desktop checks.

Motion checks cover fade reversals and cleanup, reduced motion, bound properties, dialog results, shared-engine view transitions and delayed window closing. Fluid-layout checks cover card movement and resize, reversal from the current rendered position, row regrouping, preserved transforms, scroll/filter behavior and clock cleanup. All test windows use isolated offscreen surfaces.
