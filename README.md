# XTONE

XTONE is a small Windows music player for a local library. The first version includes a minimal album shelf, local album and playlist creation, file details, and an audio reactive player dock.

## Run

Install the .NET 8 SDK on Windows, then run:

```powershell
dotnet run --project src/XTONE/XTONE.csproj
```

Build the app with:

```powershell
dotnet build src/XTONE/XTONE.csproj
```

## Supported audio

XTONE accepts MP3, WAV, FLAC, and ALAC in an M4A/MP4 container. Playback uses Windows Media Foundation through NAudio, so a Windows installation needs the decoder for the file's format. Windows 11 is the recommended target. ALAC is checked in the MP4 container before import.

The track details show the file's codec, bitrate, sample rate, bit depth, and channel count when those values are available. XTONE labels the codec as lossless or lossy; it does not guess whether a file was upsampled or converted from another source.

## Local library

Imported tracks remain in their original folders. XTONE stores track paths, custom album details, playlists, and artwork under `%LOCALAPPDATA%\XTONE`. A custom album groups existing tracks and can have its own title, artist, artwork, and private liner notes.

## Stack

- WPF on .NET 8
- NAudio for Windows audio playback and sample analysis
- TagLibSharp for audio metadata and embedded artwork

## Interface

The desktop interface uses the supplied XTONE wordmark and bundled Open Sauce One fonts, an adaptive album grid, vector icons, custom playback controls, and an FFT-driven ribbon visualizer. The font license is included at `src/XTONE/Assets/Fonts/OFL.txt`; the original font source is [Open Sauce Fonts](https://github.com/marcologous/Open-Sauce-Fonts).

Use **Ctrl+F** to search albums or the track library, **Esc** to close search, and **Ctrl+O** to add individual audio files. Right-click **Add music** for the file picker.

For visual comparison with the supplied mockup, an optional in-memory preview accepts the reference image:

```powershell
dotnet run --project src/XTONE/XTONE.csproj -- --preview "path\to\reference.png"
```

Preview mode displays the reference album examples and a simulated signal for design inspection. It does not load, import, or save the personal music library. Normal startup always shows the real local collection.
