# XTONE

XTONE is a lightweight Windows music player for a local library, built with WPF and .NET 8. It includes an adaptive album shelf, editable personal albums and playlists, a playback queue, favorites and listening history, file details, and an audio reactive player dock.

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

XTONE accepts MP3, WAV, FLAC, and ALAC in an M4A/MP4 container. Playback uses Windows Media Foundation through NAudio, so a Windows installation needs the decoder for the file's format. Windows 11 is the recommended target. ALAC is checked structurally in the MP4 container before import. Unreadable files stay visible with an error so they can be repaired or reconnected.

Playback uses shared WASAPI, normalized to the selected endpoint's mix rate and stereo. Settings lets you select an output device. The engine prepares the next decoder in the background and joins prepared tracks without restarting output. Silence and encoder padding already in the source can remain; MP3 gapless padding is not trimmed. This is not an exclusive or bit-perfect output mode.

The track details show the file's codec, bitrate, sample rate, bit depth, and channel count when those values are available. XTONE labels the codec as lossless or lossy; it does not guess whether a file was upsampled or converted from another source.

## Local library

Imported tracks remain in their original folders. XTONE stores cached metadata, custom album details, playlists, favorites, history, session state and artwork under `%LOCALAPPDATA%\XTONE`, with atomic writes and a recovery backup. A custom album groups existing tracks and can have its own title, artist, artwork, private liner notes and track order. Editing collections does not modify audio tags or move files.

Folder imports run in the background with progress and cancellation. Watched folders refresh automatically; unchanged files reuse cached metadata. Missing files stay in collections and can be reconnected using **Locate file** from a track's context menu. Importing the same path again does not add a duplicate library entry.

Create ordinary playlists or smart playlists based on favorites, lossless encoding, recently played tracks or unplayed tracks. Queue entries can be reordered by dragging or with **Alt+Up/Down**. **Play next** remains explicit even with shuffle or repeat enabled. The current queue, track position, volume and playback settings restore on launch, paused. Windows now-playing controls, media keys and taskbar transport buttons are supported when available.

## Stack

- WPF on .NET 8
- NAudio for Windows audio playback and sample analysis
- TagLibSharp for audio metadata and embedded artwork

## Interface

The desktop interface uses the supplied XTONE wordmark and bundled Open Sauce One fonts, an adaptive album grid, vector icons, custom playback controls, and an FFT-driven ribbon visualizer. The font license is included at `src/XTONE/Assets/Fonts/OFL.txt`; the original font source is [Open Sauce Fonts](https://github.com/marcologous/Open-Sauce-Fonts).

Motion is enabled by default. Library, Focus and Mini crossfade between views; app dialogs fade in and out, fullscreen changes use a clean fade, and closing the app finishes its fade before releasing its windows. Sidebar, typography and player spacing ease between responsive sizes. Album covers glide into their new positions as the shelf resizes or changes column count. Hover, page and queue transitions use the same restrained easing. Rapid view changes reverse the active fade without restarting playback. Windows reduced-animation preferences and **Reduce motion** in Settings settle transitions immediately.

Use **Ctrl+F** to search, **Ctrl+O** to add files, **Ctrl+Q** for the queue, **Ctrl+D** to favorite the current track, **Ctrl+,** for settings, **Ctrl+Left/Right** for previous/next, and **F5** to rescan. Double-click a track or press **Enter** to play it. **Space** toggles playback in track lists; **Esc** closes the drawer, search or collection detail. Track context menus expose play next, queue, favorite, file details and relink actions. Windows high contrast and animation preferences are respected; settings also offers reduced motion.

## Listening views

Choose **Focus** or press **Ctrl+Shift+F** for a dedicated listening view with large artwork, favorite control, playback position, volume, and a measured audio visualizer. Choose Ribbon, Bars, or Off. **F11** toggles fullscreen; Escape exits fullscreen first, then returns to the library. The transport and seek controls stay visible in smaller windows.

Choose **Mini** or press **Ctrl+Shift+M** for the compact player. Its **On top** toggle keeps it above other windows when wanted. Use Library or the close button to return to the library, and Focus to expand the listening view. A context menu offers queue, volume and mute controls. The pin and visualizer preferences are saved; startup opens the library paused.

All views share one engine and queue. Switching views preserves playback, position, volume, favorites and Windows now-playing controls. The mini player disables unused spectrum analysis, and minimized views update less frequently. Seek gestures commit only on explicit input, including accessible range-value commands. Closing the main app waits for already accepted library and history writes before releasing playback resources.

Launch with `XTONE.exe --focus` or `XTONE.exe --mini` to open a listening view directly. Playback still restores paused.

For isolated development checks, set `XTONE_DATA_DIR` to a temporary directory before starting the app. It overrides the library storage directory for that process. The regression harnesses under `tests/` use their own temporary state. See `tests/XTONE.AudioTests/README.md` for native codec/device checks.

For visual comparison with the supplied mockup, an optional in-memory preview accepts the reference image:

```powershell
dotnet run --project src/XTONE/XTONE.csproj -- --preview "path\to\reference.png"
```

Preview mode displays the reference album examples and a simulated signal for design inspection. It does not load, import, or save the personal music library. Normal startup always shows the real local collection.
