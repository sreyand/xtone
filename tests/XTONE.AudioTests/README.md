# Audio checks

This console harness links the production audio services directly. It covers queue concatenation, prebuffer handoff, stale/rejected-format preparation ownership, stable end-of-stream reads, invalid/zero read handling, byte/float buffer aliases, FFT accumulation and reduced motion without starting an audio device.

```powershell
dotnet run --project tests/XTONE.AudioTests
```

Provide a directory containing `fixture.wav`, `fixture.flac`, `fixture.m4a` (ALAC), and `fixture.mp3` to additionally check real Windows Media Foundation decoding and seeking. The fixtures should contain roughly two seconds of non-silent stereo 48 kHz audio. Add `--devices` to check endpoint enumeration and `--playback` to exercise the real WASAPI endpoint, muted, including transition timing, initial paused restore, bursts of overlapping async opens, stop/dispose supersession, repeated same-track preparation, seek/resume, and output selection failures. The failed-next-file scenario verifies that preparing an unreadable future file does not interrupt current playback.

```powershell
dotnet run --project tests/XTONE.AudioTests -- C:\path\to\fixtures --devices --playback
```

Playback uses shared WASAPI and stereo at the endpoint mix rate. The queue provider inserts no silence at a prepared transition. Silence and encoder padding already contained in a source file can remain; MP3 gapless metadata is not trimmed. Unsupported Windows codec variants produce actionable playback errors.


Asynchronous track opens and next-track preparations share one awaited native decoder gate. Queued requests check their generation before opening resources; identical pending next-track requests are reused for the current concatenation sequence. Synchronous pause/seek/device controls remain serialized separately. A slow native open cannot be forcibly canceled, but stale results are released instead of being installed.

Current reliability validation used isolated two-second WAV, FLAC, ALAC, and MP3 fixtures and the harness's muted Realtek shared-mode output. It passed 54 audio checks, including 48 overlapping requests, stopping 32 queued opens, and disposing during 24 opens. These are correctness/stress checks, not an end-to-end latency guarantee. Physically unplugging a device, Bluetooth/USB reconnection, and codec variants beyond these fixtures still need dedicated hardware checks. An unavailable device selection is tested; it preserves the existing session when rejected before replacement.


`StopAsync(token)` stops playback and invalidates pending requests immediately, then waits asynchronously for the decoder gate so stale in-flight native opens have released their files. File-management callers must block new playback requests until mutation finishes. Canceling this wait leaves playback stopped; it does not force-cancel native decoder work. The native drain regression uses a private WAV copy and verifies an exclusive file handle can be acquired after the drain.

