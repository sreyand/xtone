# XTONE

XTONE is a minimal Windows music player for an existing local Music folder. Music shows the actual folders on disk together with loose audio files; All tracks provides a flat track list across the collection. You can create and rename folders, choose their artwork, copy audio into them, and move tracks between folders from the app. File information stays available in details, and the library, Focus, and Mini views share one playback engine. The desktop app is built with WPF and .NET 8.

## Launch and build

Open **Open XTONE.cmd** in the project folder. That launcher opens **app/XTONE.exe**, the canonical delivered build. Older executables under `src/XTONE/bin/` can contain a different interface; use the launcher when checking the delivered app.

For development, install the .NET 8 SDK on Windows and run from the project folder:

```powershell
dotnet run --project src/XTONE/XTONE.csproj
```

Build the canonical output with:

```powershell
dotnet build src/XTONE/XTONE.csproj -c Release -o app
```

Close the executable from that output before replacing it. This remains a native Windows application. Installer creation, signing, and distribution packages are deliberately deferred while features are still being developed. The executable includes a simple blue-and-white XTONE icon.

## Choose your Music folder

On first launch, choose one Music folder and save its settings. In **Settings**, select the folder structure that matches the collection:

- **Folders directly inside Music:** each immediate subfolder appears in Music. Tracks in its nested directories stay grouped with that folder.
- **Music → Artist folder → Music folder:** the music folders one level beneath each artist directory appear in Music. Tracks in their nested directories stay grouped with the containing music folder.

Music is the primary browser and folder organizer. It shows the actual directories inside your configured Music root, including empty folders, together with loose audio files that sit outside those folder groups. Folder items use the directory name and open that folder's tracks and **Add tracks** action. File items play the track directly and offer track actions such as **Move to folder…**. A loose file is never wrapped in a fabricated album or folder.

Use **Grid view** or **List view** in the Music header to change the presentation. Both views show the same folders and loose tracks, distinguished by folder/play icons. The chosen view is saved across restarts. **All tracks** remains a flat list of every indexed track within the configured root, including tracks inside nested folders.

**Open folder** opens the physical directory in Explorer. Embedded album tags do not create additional groups or merge separate directories.

Folder artwork uses a chosen XTONE artwork override first, then `cover`, `folder`, or `front` images in that directory (JPG, JPEG, PNG, or BMP), then embedded artwork, and finally a quiet generated placeholder. Tags supply track titles, credits, track/disc numbers, and audio specifications. Tracks with known disc and track numbers sort accordingly; relative filenames provide deterministic ordering for the remainder.

**Ctrl+O** or **Open music folder** in the header options menu opens the configured root in Explorer. **Open folder** on a music card opens its physical directory. Choose a different root or folder structure in Settings. The header options menu also offers **Choose music folder…** and **Rescan music folder**.

## Organize music in the app

- In **Music**, choose **New folder**, enter its name, and create it. This creates a physical directory in the configured root. With the artist-folder structure selected, supply an artist-folder name as well.
- Open a folder and choose **Add tracks** to select supported audio files. This explicitly copies the selected files into that folder and keeps their originals. Existing destination files are not overwritten.
- Select one or several tracks in **All tracks** or a folder's track list, or use a loose file's context menu in Music, then choose **Move to folder…**. Select a destination music folder, or **Music folder (loose tracks)** to move them directly into the root. This moves the files on disk within the configured root; favorites, history, and queue references follow them.

Choose **Rename…** from a folder card's context menu, its pinned shortcut, or the options menu inside the folder. Renaming changes the physical directory while preserving track identities, favorites, listening history, queue entries, pinned shortcuts, and chosen artwork. A conflicting name is rejected rather than merged. To change only capitalization, use an intermediate name first. Folder deletion is not exposed in XTONE.

Choose **Choose artwork…** from the same menus to select an image, or **Reset artwork** to return to the normal folder/embedded cover. XTONE validates and stores its own artwork copy in the app cache; the selected image and audio tags are untouched. Folder artwork appears in Music, folder details, and the Main, Focus, and Mini listening views.

Drag tracks from Music, All tracks, or a folder's track list onto a folder card, folder list row, pinned folder shortcut, or the header inside an open folder. Internal drags **move** the files within Music and preserve their saved references. Audio files dragged from Explorer **copy** into the destination and leave their originals intact. Unsupported files, folder trees, and conflicting filenames are not imported by this drop workflow. Folder dragging to the sidebar still pins a shortcut rather than moving the folder.

Copy/move operations show progress with a Cancel action. Failed or canceled batches attempt to roll back completed changes rather than silently leaving a partially organized collection. Do not remove or modify the source/destination folders from another app while an operation is running. A conflicting destination filename produces an error instead of an overwrite.

Explorer remains available for organizing the collection externally. Indexing itself does not copy, move, rewrite tags, or delete source audio. Explicit organization actions and valid folder drops perform the requested copies, moves, and renames. There is no separate app-created collection system, and dropping files never adds another hidden Music root. Changes made externally can be rescanned.

## Pin folders to the sidebar

Drag a folder from Music, in either Grid or List view, onto the left sidebar. The drop hint appears while a valid folder is over the sidebar. You can also choose **Pin to sidebar** from the folder's context menu, including with the keyboard.

Pinned folders appear as compact shortcuts with a folder icon and name. Click one to open its tracks. Choose **Unpin** from the shortcut's context menu to remove the shortcut; it does not move or delete the folder or its music. Pins are saved across restarts and shown for the currently configured Music root.

## Library updates and recovery

Scanning runs in the background with cancellation. Filesystem changes trigger debounced refreshes, and unchanged files reuse cached metadata. A completed root scan commits its results together. Canceling or failing a root change retains the previous saved configuration.

A disconnected Music folder stays configured. Reconnect it and rescan rather than choosing a different empty folder. Missing tracks retain their saved references and remain distinguishable from readable tracks; removed directories are omitted from Music while the root is available. A missing file can be reconnected with **Locate file…** in its context menu. Keep replacements within the configured root if they should appear in the active library.

State lives under `%LOCALAPPDATA%\XTONE`: `library.json`, `session.json`, cached metadata, and artwork. Saves are atomic and retain recovery backups. Schema 3 introduces the Music root and explicit folder layout. On the first migration from an older schema, XTONE keeps a `library.json.pre-folder-migration` copy before normalizing the saved data.

Playlists, smart playlists, and personal-album creation have been removed from the interface. Existing legacy collection records are retained in saved data. Previous track paths, favorites, listening history, and session references are retained for recovery; once a Music root is configured, the active library shows tracks within that root. Choosing a new root does not delete the old files or erase legacy records.

## Artists

XTONE preserves all separately tagged performers and album artists. A track with several structured performer values can appear under each credited artist without duplicating the library file. The original display credit remains available.

Artist names are not blindly split on commas, ampersands, or other punctuation: those characters can belong to a legitimate name. If a file contains one combined collaboration string rather than separate performer values, XTONE keeps that credit intact. Unknown/empty credits do not create artist entries. Selecting an artist shows tracks associated with that exact structured credit.

The metadata cache records its reader version, so files cached by an older reader can be re-read even when their size and modification time have not changed.

## Supported audio and playback

XTONE accepts MP3, WAV, FLAC, and ALAC in an M4A/MP4 container. M4A/MP4 audio is checked structurally for ALAC; AAC in that container is currently unsupported. Playback uses Windows Media Foundation through NAudio, so Windows needs the decoder for the file's format. Windows 11 is the recommended target. Unreadable audio remains visible with an error rather than being presented as playable.

Output uses shared WASAPI, normalized to the selected endpoint's mix rate and stereo. Settings offers an output-device selector. The engine prepares the next decoder in the background and joins prepared tracks without restarting output. Silence and encoder padding in source files can remain; MP3 gapless padding is not trimmed. This output path does not provide exclusive or bit-perfect playback.

**File details…** exposes codec, bitrate, sample rate, bit depth, channel count, and location when available. Lossless/lossy labels describe the codec; they do not prove that a source is authentic, was never transcoded, or was not upsampled.

The immediate playback queue supports deliberate duplicate entries, **Play next**, drag reordering, and **Alt+Up/Down**. Favorites and recently played tracks provide lightweight navigation. Queue, track position, volume, and playback settings restore on launch, paused. Windows now-playing controls, media keys, and taskbar transport buttons are supported when available.

## Playback and large-library reliability

Metadata-cache loading streams UTF-8 JSON. An unchanged refresh checks file information without rereading audio metadata or rewriting the full cache. Scan progress is throttled; folder watchers configure and probe availability in the background. Queued refreshes resolve the active Music root after acquiring the scan gate, preventing an older refresh from restoring a previous root.

The UI prepares library ordering, artist membership, and searchable folder/track data off the dispatcher. Large searches are debounced and cancellable, with stale results rejected. Music and artist views virtualize visible rows, and bulk updates use a single reset. Playback controls use cached visibility/history state rather than scanning the collection. Queue lookups keep cached entry indices and retain duplicate-entry identities through reordering and metadata refresh.

Session writes retain only the newest pending snapshot behind the active write and are drained on close. Releasing a decoder retains a recoverable position; replaying an ended queue starts from the beginning. Unavailable entries are skipped with bounded traversal, including Repeat One. Native decoder creation is serialized, obsolete requests are rejected, and repeated preparations coalesce. File moves/renames asynchronously wait for pending decoder handles to drain before changing paths.

Validation includes real WAV/FLAC/ALAC/MP3 fixtures, muted shared-mode WASAPI, burst/stop/dispose tests, 10,000/25,000-track presentation fixtures, and 25,000 cached physical tiny-WAV files. See `tests/XTONE.AudioTests`, `tests/XTONE.LargeLibraryTests`, and `tests/XTONE.LibraryScaleTests` for protocols and limitations. Local cached-fixture timings are not first-import, network-drive, high-resolution decoding, or universal frame-time guarantees. Physically unplugging and reconnecting USB/Bluetooth devices still needs dedicated hardware validation.

## Interface and keyboard

The interface retains the XTONE wordmark, bundled Open Sauce One fonts, white surfaces, and blue accent. FIELD informs compact proportions and restrained feedback: 14px navigation, 32px page headings, a 205–236px sidebar, and a 104px player dock. The dock appears when a track is selected and remains available while paused. File specifications stay in details rather than filling track rows or the dock.

Search opens on demand. Routine successes use temporary notices; loading, failures, and unavailable-folder recovery remain distinct states. Windows high contrast and animation preferences are respected, with **Reduce motion** available in Settings.

Motion is enabled by default. Native resizing updates layout directly rather than accumulating animation clocks on fonts, margins, or grid tracks. Folder-card reflow animates position while keeping text sharp and artwork unstretched. Hover surfaces fade without dimming text. Page, window, dialog, and fullscreen transitions use reversible fades.

- **Ctrl+F:** search.
- **Ctrl+O:** open the Music folder; choose one if none is configured.
- **Ctrl+Q:** queue.
- **Ctrl+D:** favorite the current track.
- **Ctrl+,:** settings.
- **Ctrl+Left/Right:** previous/next track.
- **F5:** rescan the Music folder.
- **Enter** or double-click a track: play it.
- **Space:** play/pause from a track list.
- **Esc:** close the queue, search, or detail view.

Track context menus expose play, play next, queue, move to folder, favorite, file details, and relink actions. Track lists support multiple selection with Ctrl/Shift for organization.

## Focus and Mini

**Focus**, or **Ctrl+Shift+F**, opens a dedicated listening view with large artwork, favorite control, transport, seek, volume, and an audio-reactive visualizer. Choose Ribbon, Bars, or Off. **F11** toggles fullscreen. Escape exits fullscreen first, then returns to the library.

**Mini**, or **Ctrl+Shift+M**, opens the compact player. **On top** keeps it above other windows when wanted. Library or the close button returns to the library; Focus expands the listening view. A context menu offers queue, volume, and mute controls. Pin and visualizer preferences are saved.

All views share one engine and queue. Switching views preserves playback, position, volume, favorites, and Windows now-playing controls. Mini disables unused spectrum analysis, and minimized views update less frequently. Seek gestures commit on explicit input, including accessible range-value commands. Closing the main application waits for accepted library/history writes before releasing playback resources.

Use `app/XTONE.exe --focus` or `app/XTONE.exe --mini` to open a listening view directly. Playback still restores paused.

## Development checks

Set `XTONE_DATA_DIR` to a temporary directory before launching for an isolated review. It overrides the storage directory for that process, leaving the personal library separate. Regression harnesses under `tests/` use temporary state. See `tests/XTONE.AudioTests/README.md` for native codec/device checks. A successful build or harness run does not establish that every Windows device, codec, or desktop layout has been checked.

An optional in-memory preview accepts the reference image:

```powershell
dotnet run --project src/XTONE/XTONE.csproj -- --preview "path/to/reference.png"
```

Preview uses example folder cards and a simulated signal for design comparison. It does not load, index, or save the personal library. Normal startup uses the actual configured collection.

## Planned sound features

Manual/static EQ, validated headphone profiles, system-wide Windows EQ for other applications, and later adaptive EQ/local ML remain planned work. They are not implemented by this folder-library release. System-wide processing needs a separately verified Windows audio backend; XTONE's existing playback chain only processes its own decoded samples.

## Stack and assets

- WPF on .NET 8.
- NAudio for Windows playback and sample analysis.
- TagLibSharp for metadata and embedded artwork.

The Open Sauce font license is included at `src/XTONE/Assets/Fonts/OFL.txt`; the original source is [Open Sauce Fonts](https://github.com/marcologous/Open-Sauce-Fonts).
