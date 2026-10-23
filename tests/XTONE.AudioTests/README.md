# Audio checks

This console harness links the production audio services directly. It covers queue concatenation, prebuffer handoff, stale preparation disposal, FFT accumulation and reduced motion without starting an audio device.

```powershell
dotnet run --project tests/XTONE.AudioTests
```

Provide a directory containing `fixture.wav`, `fixture.flac`, `fixture.m4a` (ALAC), and `fixture.mp3` to additionally check real Windows Media Foundation decoding and seeking. The fixtures should contain roughly two seconds of non-silent stereo 48 kHz audio. Add `--devices` to check endpoint enumeration and `--playback` to exercise the real WASAPI endpoint, muted, including transition timing, initial paused restore, overlapping async opens, seek/resume and device switching.

```powershell
dotnet run --project tests/XTONE.AudioTests -- C:\path\to\fixtures --devices --playback
```

Playback uses shared WASAPI and stereo at the endpoint mix rate. The queue provider inserts no silence at a prepared transition. Silence and encoder padding already contained in a source file can remain; MP3 gapless metadata is not trimmed. Unsupported Windows codec variants produce actionable playback errors.
