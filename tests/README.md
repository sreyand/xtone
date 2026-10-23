# Regression checks

These standalone .NET console harnesses link production services and use isolated state. They do not load the personal XTONE library. Run them on Windows with the .NET 8 SDK:

```powershell
dotnet run --project tests/XTONE.QueueTests
dotnet run --project tests/XTONE.LibraryTests
dotnet run --project tests/XTONE.AudioTests
```

Queue checks cover duplicate entries, stable current identity, reorder/remove, shuffle, repeat, history and explicit Play Next. Library checks cover caching, availability/relink, atomic persistence and recovery, cancellation, concurrent edits, codec classification and folder watching.

The library and audio harnesses also accept a fixture directory containing `fixture.wav`, `fixture.flac`, `fixture.m4a` (ALAC), and `fixture.mp3`. Use roughly two seconds of non-silent stereo 48 kHz audio. Fixture generation tools are not shipped with XTONE.

```powershell
dotnet run --project tests/XTONE.LibraryTests -- C:\path\to\fixtures
dotnet run --project tests/XTONE.AudioTests -- C:\path\to\fixtures --devices --playback
```

The audio device checks play muted and check real native decode/seek, rendered transition timing, paused restore, cancellation, endpoint switching and end events. See `XTONE.AudioTests/README.md` for output and padding limitations.
