using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Media.Imaging;
using XTONE.Models;

namespace XTONE.Services;

/// <summary>
/// Owns the local library. Source audio moves only through explicit organizer operations; tags are never rewritten.
/// Metadata reads happen outside the state lock; each completed scan merges its
/// results so a simultaneous playlist edit or session save is retained.
/// </summary>
public sealed class LibraryStore
{
    private const int MetadataReaderVersion = 2;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".m4a", ".mp4", ".mp3"
    };
    private readonly string _root;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<string, string> _relinkedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private LibraryState _state = new();
    private LibraryState _committedState = new();
    private bool _loaded;
    private string? _recoveryNotice;
    private long _metadataReadCount;
    private long _revision;
    private string StateFile => Path.Combine(_root, "library.json");
    private string SessionFile => Path.Combine(_root, "session.json");
    private string ArtworkFolder => Path.Combine(_root, "artwork");

    public LibraryStore(string? rootPath = null) => _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTONE")));

    public string StorageDescription => _root;
    public long MetadataReadCount => Interlocked.Read(ref _metadataReadCount);

    public LibrarySnapshot Load()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            // One-time migration of libraries created before metadata caching.
            var changed = false;
            foreach (var path in _state.TrackPaths)
            {
                if (_state.MetadataCache.ContainsKey(path))
                    continue;
                _state.MetadataCache[path] = ReadTrack(path, null);
                changed = true;
            }
            if (changed)
                SaveLocked();
            return SnapshotLocked();
        }
    }

    public Task<LibrarySnapshot> ImportAsync(IEnumerable<string> paths,
        IProgress<LibraryProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ScanAsync(paths.ToArray(), false, progress, cancellationToken);

    public Task<LibrarySnapshot> RefreshAsync(IProgress<LibraryProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ScanAsync([], true, progress, cancellationToken);

    public Task<LibrarySnapshot> ConfigureMusicFolderAsync(string root, string layout = "AlbumFolders",
        IProgress<LibraryProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ScanMusicRootAsync(NormalizePath(root), FolderLibrary.NormalizeLayout(layout), true, progress, cancellationToken);

    public string CreateMusicFolder(string name, string? parent = null)
    {
        _scanGate.Wait();
        try
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                var folder = MusicFileOrganizer.CreateFolder(_state.MusicRoot ?? throw new InvalidOperationException("Choose a Music folder first."), name, parent);
                try { SaveLocked(); }
                catch (Exception original)
                {
                    try { Directory.Delete(folder, false); }
                    catch (Exception rollback) { throw new AggregateException("The folder was created, but its saved state could not be updated. Check the Music folder.", original, rollback); }
                    throw;
                }
                return folder;
            }
        }
        finally { _scanGate.Release(); }
    }

    public async Task<LibrarySnapshot> RenameMusicFolderAsync(string folderPath, string newName,
        IProgress<LibraryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                lock (_gate)
                {
                    EnsureLoadedLocked(); cancellationToken.ThrowIfCancellationRequested();
                    var root = _state.MusicRoot ?? throw new InvalidOperationException("Choose a Music folder first.");
                    var source = MusicFileOrganizer.ValidateDirectory(root, folderPath);
                    if (SamePath(_root, source) || FolderLibrary.IsWithinRoot(_root, source))
                        throw new IOException("The XTONE storage folder cannot be moved by renaming this folder.");
                    var destination = MusicFileOrganizer.PrepareFolderRename(root, source, newName, cancellationToken);
                    if (string.Equals(source, destination, StringComparison.Ordinal)) return SnapshotLocked();
                    string Remap(string path) => SamePath(path, source) ? destination : FolderLibrary.IsWithinRoot(path, source)
                        ? Path.Combine(destination, Path.GetRelativePath(source, path)) : path;
                    var originalTracks = _state.TrackPaths.Where(path => FolderLibrary.IsWithinRoot(path, source)).ToArray();
                    var untouched = _state.TrackPaths.Except(originalTracks, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (originalTracks.Any(path => untouched.Contains(Remap(path))))
                        throw new IOException("The renamed location already has saved tracks. Reconnect those files before using this name.");
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.Move(source, destination);
                    try
                    {
                        progress?.Report(new(1, 1, "Updating folder references…"));
                        cancellationToken.ThrowIfCancellationRequested();
                        _state.TrackPaths = _state.TrackPaths.Select(Remap).ToList();
                        _state.FolderPaths = _state.FolderPaths.Select(Remap).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        _state.PinnedFolderPaths = _state.PinnedFolderPaths.Select(Remap).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        _state.FavoritePaths = _state.FavoritePaths.Select(Remap).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        _state.Session.QueuePaths = _state.Session.QueuePaths.Select(Remap).ToList();
                        foreach (var entry in _state.History) entry.Path = Remap(entry.Path);
                        _state.History = _state.History.GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                            .Select(group => new ListeningHistoryEntry { Path = group.Key, LastPlayedUtc = group.Max(entry => entry.LastPlayedUtc), PlayCount = group.Sum(entry => entry.PlayCount) }).ToList();
                        foreach (var album in _state.CustomAlbums)
                        { album.TrackPaths = album.TrackPaths.Select(Remap).ToList(); if (album.ArtworkPath is not null) album.ArtworkPath = Remap(album.ArtworkPath); }
                        foreach (var playlist in _state.Playlists) playlist.TrackPaths = playlist.TrackPaths.Select(Remap).ToList();
                        _state.MetadataCache = _state.MetadataCache.ToDictionary(pair => Remap(pair.Key), pair => pair.Value with
                        { Path = Remap(pair.Value.Path), ArtworkPath = pair.Value.ArtworkPath is { } art ? Remap(art) : null }, StringComparer.OrdinalIgnoreCase);
                        _state.FolderArtworkPaths = _state.FolderArtworkPaths.ToDictionary(pair => Remap(pair.Key), pair => Remap(pair.Value), StringComparer.OrdinalIgnoreCase);
                        var now = DateTime.UtcNow;
                        _state.Session.SavedUtc = now > _state.Session.SavedUtc ? now : _state.Session.SavedUtc.AddTicks(1);
                        cancellationToken.ThrowIfCancellationRequested(); SaveLocked();
                    }
                    catch (Exception original)
                    {
                        _state = CloneState(_committedState);
                        try { Directory.Move(destination, source); }
                        catch (Exception rollback) { throw new AggregateException("The rename could not be saved or fully restored. Check both folder names.", original, rollback); }
                        throw;
                    }
                    return SnapshotLocked();
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _scanGate.Release(); }
    }

    public async Task<LibrarySnapshot> SetFolderArtworkAsync(string folderPath, string? imagePath,
        CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                lock (_gate)
                {
                    EnsureLoadedLocked(); cancellationToken.ThrowIfCancellationRequested();
                    var root = _state.MusicRoot ?? throw new InvalidOperationException("Choose a Music folder first.");
                    var folder = Path.TrimEndingDirectorySeparator(NormalizePath(folderPath));
                    if (!FolderLibrary.IsWithinRoot(folder, root)) throw new IOException("Choose a folder inside your Music folder.");
                    if (imagePath is null)
                    {
                        if (_state.FolderArtworkPaths.Remove(folder)) SaveLocked();
                        return SnapshotLocked();
                    }
                    MusicFileOrganizer.ValidateDirectory(root, folder);
                    var cached = CacheFolderArtwork(imagePath, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    _state.FolderArtworkPaths[folder] = cached; SaveLocked();
                    return SnapshotLocked();
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _scanGate.Release(); }
    }

    private string CacheFolderArtwork(string imagePath, CancellationToken token)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 32 * 1024 * 1024) throw new InvalidDataException("Choose an image smaller than 32 MB.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); token.ThrowIfCancellationRequested(); memory.Position = 0;
        try
        {
            var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames.FirstOrDefault() ?? throw new InvalidDataException("This image has no readable frame.");
            var width = frame.PixelWidth; var height = frame.PixelHeight;
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > 16_000_000)
                throw new InvalidDataException("Choose artwork with no more than 16 million pixels.");
            memory.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            if (Math.Max(width, height) > 1200)
            { if (width >= height) bitmap.DecodePixelWidth = 1200; else bitmap.DecodePixelHeight = 1200; }
            bitmap.EndInit(); bitmap.Freeze(); token.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream(); encoder.Save(output); var bytes = output.ToArray();
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(ArtworkFolder, "folder-" + Convert.ToHexString(SHA256.HashData(bytes))[..32] + ".png");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); token.ThrowIfCancellationRequested(); File.Move(temporary, target, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return target;
        }
        catch (Exception error) when (error is NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException)
        { throw new InvalidDataException("Choose a readable PNG, JPEG, or BMP image.", error); }
    }

    public void SetFolderPinned(string path, bool pinned)
    {
        path = Path.TrimEndingDirectorySeparator(NormalizePath(path));
        lock (_gate)
        {
            EnsureLoadedLocked();
            var root = _state.MusicRoot ?? throw new InvalidOperationException("Choose a Music folder first.");
            if (!FolderLibrary.IsWithinRoot(path, root))
                throw new IOException("Pin a folder inside your Music folder.");
            if (pinned)
            {
                MusicFileOrganizer.ValidateDirectory(root, path);
                if (_state.PinnedFolderPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
                _state.PinnedFolderPaths.Add(path);
            }
            else if (_state.PinnedFolderPaths.RemoveAll(item => SamePath(item, path)) == 0) return;
            SaveLocked();
        }
    }

    public Task<LibrarySnapshot> MoveTracksToFolderAsync(IEnumerable<string> paths, string destination,
        IProgress<LibraryProgress>? progress = null, CancellationToken cancellationToken = default) =>
        TransferTracksAsync(paths.ToArray(), destination, false, progress, cancellationToken);

    public Task<LibrarySnapshot> CopyFilesToFolderAsync(IEnumerable<string> paths, string destination,
        IProgress<LibraryProgress>? progress = null, CancellationToken cancellationToken = default) =>
        TransferTracksAsync(paths.ToArray(), destination, true, progress, cancellationToken);

    private async Task<LibrarySnapshot> TransferTracksAsync(string[] paths, string destination, bool copy,
        IProgress<LibraryProgress>? progress, CancellationToken token)
    {
        await _scanGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                lock (_gate)
                {
                    EnsureLoadedLocked(); token.ThrowIfCancellationRequested();
                    var root = _state.MusicRoot ?? throw new InvalidOperationException("Choose a Music folder first.");
                    var transfers = MusicFileOrganizer.PrepareTransfers(root, paths, destination, copy);
                    foreach (var item in transfers)
                    {
                        if (_state.TrackPaths.Contains(item.Destination, StringComparer.OrdinalIgnoreCase))
                            throw new IOException($"{Path.GetFileName(item.Destination)} already has a saved library entry. Reconnect it with Locate file instead.");
                        if (copy && !IsSupported(item.Source)) throw new InvalidDataException("Choose supported FLAC, WAV, ALAC, or MP3 audio files.");
                        if (!copy && !_state.TrackPaths.Contains(item.Source, StringComparer.OrdinalIgnoreCase))
                            throw new InvalidOperationException("A selected track is no longer in the library. Refresh it and try again.");
                    }
                    if (transfers.Count == 0) return SnapshotLocked();
                    Action<int> report = done => progress?.Report(new(done, transfers.Count, copy ? "Copying tracks…" : "Moving tracks…"));
                    // The organizer rolls back a partially completed filesystem batch itself.
                    if (copy) MusicFileOrganizer.ExecuteCopies(transfers, report, token);
                    else MusicFileOrganizer.ExecuteMoves(transfers, report, token);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (var item in transfers)
                        {
                            token.ThrowIfCancellationRequested();
                            if (copy)
                            {
                                var track = ReadTrack(item.Destination, null);
                                if (!track.CanPlay) throw new InvalidDataException($"Could not read {Path.GetFileName(item.Source)}: {track.Error}");
                                if (!_state.TrackPaths.Contains(item.Destination, StringComparer.OrdinalIgnoreCase)) _state.TrackPaths.Add(item.Destination);
                                _state.MetadataCache[item.Destination] = track;
                            }
                            else
                            {
                                var previous = _state.MetadataCache.GetValueOrDefault(item.Source) ?? Placeholder(item.Source);
                                RemapTrackLocked(item.Source, item.Destination, previous with { Path = item.Destination, IsMissing = false });
                            }
                        }
                        token.ThrowIfCancellationRequested();
                        if (!copy)
                        {
                            var now = DateTime.UtcNow;
                            _state.Session.SavedUtc = now > _state.Session.SavedUtc ? now : _state.Session.SavedUtc.AddTicks(1);
                        }
                        SaveLocked();
                    }
                    catch (Exception original)
                    {
                        _state = CloneState(_committedState);
                        try { if (copy) MusicFileOrganizer.RollbackCopies(transfers); else MusicFileOrganizer.RollbackMoves(transfers); }
                        catch (Exception rollback) { throw new AggregateException("Changes could not be saved or fully rolled back. Check the listed file paths.", original, rollback); }
                        throw;
                    }
                    return SnapshotLocked();
                }
            }, token).ConfigureAwait(false);
        }
        finally { _scanGate.Release(); }
    }

    private void RemapTrackLocked(string oldPath, string newPath, Track replacement)
    {
        ReplacePath(_state.TrackPaths, oldPath, newPath, true);
        foreach (var album in _state.CustomAlbums) ReplacePath(album.TrackPaths, oldPath, newPath);
        foreach (var playlist in _state.Playlists) ReplacePath(playlist.TrackPaths, oldPath, newPath);
        ReplacePath(_state.FavoritePaths, oldPath, newPath, true);
        ReplacePath(_state.Session.QueuePaths, oldPath, newPath);
        foreach (var entry in _state.History.Where(item => SamePath(item.Path, oldPath))) entry.Path = newPath;
        _state.MetadataCache.Remove(oldPath); _state.MetadataCache[newPath] = replacement;
    }

    private async Task<LibrarySnapshot> ScanMusicRootAsync(string musicRoot, string layout, bool choosing,
        IProgress<LibraryProgress>? progress, CancellationToken token)
    {
        await _scanGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                // A refresh may have waited behind a root switch. Resolve its root
                // only after owning the scan gate, never resurrect an older root.
                if (!choosing)
                    lock (_gate)
                    {
                        EnsureLoadedLocked();
                        musicRoot = _state.MusicRoot ?? musicRoot;
                        layout = _state.AlbumLayout;
                    }
                return ScanMusicRootCore(musicRoot, layout, choosing, progress, token);
            }, token).ConfigureAwait(false);
        }
        finally { _scanGate.Release(); }
    }

    private LibrarySnapshot ScanMusicRootCore(string musicRoot, string layout, bool choosing,
        IProgress<LibraryProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (choosing && !Directory.Exists(musicRoot))
            throw new DirectoryNotFoundException("Choose an available Music folder.");
        Dictionary<string, Track> cache;
        HashSet<string> candidates;
        lock (_gate)
        {
            EnsureLoadedLocked(); _relinkedPaths.Clear();
            cache = new(_state.MetadataCache, StringComparer.OrdinalIgnoreCase);
            candidates = _state.TrackPaths.Where(path => FolderLibrary.IsWithinRoot(path, musicRoot))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        progress?.Report(new(0, 0, "Checking your Music folder…"));
        if (!Directory.Exists(musicRoot))
            progress?.Report(new(0, 0, $"Folder unavailable: {musicRoot}"));
        else
        {
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
                foreach (var path in Directory.EnumerateFiles(musicRoot, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (HasSupportedExtension(path) && !FolderLibrary.IsWithinRoot(path, _root))
                        candidates.Add(NormalizePath(path));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Never turn a partial/inaccessible scan into a destructive replacement.
                if (choosing) throw new IOException("XTONE could not read the selected Music folder.", error);
                progress?.Report(new(0, 0, $"Folder unavailable: {musicRoot}"));
            }
        }
        var ordered = candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        var updates = new ScanProgressReporter(progress);
        for (var i = 0; i < ordered.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var path = ordered[i]; results[path] = ReadOrReuse(path, cache.GetValueOrDefault(path));
            updates.FileRead(i + 1, ordered.Length, Path.GetFileName(path));
        }
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            var known = _state.TrackPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = !SamePath(_state.MusicRoot ?? "", musicRoot) || _state.AlbumLayout != layout;
            foreach (var pair in results)
            {
                if (_relinkedPaths.ContainsKey(pair.Key)) continue;
                if (known.Add(pair.Key)) { _state.TrackPaths.Add(pair.Key); changed = true; }
                if (!_state.MetadataCache.TryGetValue(pair.Key, out var existing) || existing != pair.Value)
                { _state.MetadataCache[pair.Key] = pair.Value; changed = true; }
            }
            // Legacy paths, collections and references remain stored for recovery.
            _state.MusicRoot = musicRoot; _state.AlbumLayout = layout;
            if (changed) SaveLocked();
            return SnapshotLocked();
        }
    }

    private async Task<LibrarySnapshot> ScanAsync(string[] inputs, bool refresh,
        IProgress<LibraryProgress>? progress, CancellationToken token)
    {
        await _scanGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                string? currentRoot = null;
                string layout = FolderLibrary.AlbumFolders;
                if (refresh)
                    lock (_gate) { EnsureLoadedLocked(); currentRoot = _state.MusicRoot; layout = _state.AlbumLayout; }
                return currentRoot is null ? ScanCore(inputs, refresh, progress, token)
                    : ScanMusicRootCore(currentRoot, layout, false, progress, token);
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private LibrarySnapshot ScanCore(string[] inputs, bool refresh,
        IProgress<LibraryProgress>? progress, CancellationToken token)
    {
        Dictionary<string, Track> cache;
        HashSet<string> candidates;
        HashSet<string> folders;
        lock (_gate)
        {
            EnsureLoadedLocked();
            // A relink only invalidates results from the currently running scan.
            // A later import may intentionally add a file at its former path.
            _relinkedPaths.Clear();
            cache = _state.MetadataCache.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.OrdinalIgnoreCase);
            candidates = new HashSet<string>(_state.TrackPaths, StringComparer.OrdinalIgnoreCase);
            folders = new HashSet<string>(_state.FolderPaths, StringComparer.OrdinalIgnoreCase);
        }

        var foldersToScan = new HashSet<string>(refresh ? folders : [], StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            var path = NormalizePath(input);
            if (Directory.Exists(path) || !File.Exists(path) && !HasSupportedExtension(path))
            {
                folders.Add(path);
                foldersToScan.Add(path);
            }
            else if (HasSupportedExtension(path))
            {
                candidates.Add(path);
            }
        }

        foreach (var folder in foldersToScan)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new LibraryProgress(0, 0, $"Looking for music in {Path.GetFileName(folder)}"));
            if (!Directory.Exists(folder))
                continue;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    ReturnSpecialDirectories = false
                };
                foreach (var path in Directory.EnumerateFiles(folder, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (HasSupportedExtension(path))
                        candidates.Add(NormalizePath(path));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                progress?.Report(new LibraryProgress(0, 0, $"Folder unavailable: {folder}"));
            }
        }

        var ordered = candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        var updates = new ScanProgressReporter(progress);
        for (var index = 0; index < ordered.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var path = ordered[index];
            cache.TryGetValue(path, out var previous);
            results[path] = ReadOrReuse(path, previous);
            updates.FileRead(index + 1, ordered.Length, Path.GetFileName(path));
        }
        token.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // No imports or metadata changes are committed after a cancelled scan.
            token.ThrowIfCancellationRequested();
            var known = new HashSet<string>(_state.TrackPaths, StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var result in results)
            {
                // A relink performed while this scan was reading keeps its new path.
                if (_relinkedPaths.ContainsKey(result.Key))
                    continue;
                if (known.Add(result.Key)) { _state.TrackPaths.Add(result.Key); changed = true; }
                if (!_state.MetadataCache.TryGetValue(result.Key, out var existing) || existing != result.Value)
                { _state.MetadataCache[result.Key] = result.Value; changed = true; }
            }
            foreach (var folder in folders)
                if (!_state.FolderPaths.Contains(folder, StringComparer.OrdinalIgnoreCase))
                { _state.FolderPaths.Add(folder); changed = true; }
            if (changed) SaveLocked();
            progress?.Report(new LibraryProgress(ordered.Length, ordered.Length, "Library ready"));
            return SnapshotLocked();
        }
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            var known = new HashSet<string>(_state.TrackPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var input in paths)
            {
                var path = NormalizePath(input);
                if (!HasSupportedExtension(path))
                    continue;
                if (known.Add(path))
                    _state.TrackPaths.Add(path);
                _state.MetadataCache.TryGetValue(path, out var previous);
                _state.MetadataCache[path] = ReadOrReuse(path, previous);
            }
            SaveLocked();
        }
    }

    public void AddFolder(string path)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            path = NormalizePath(path);
            if (!_state.FolderPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                _state.FolderPaths.Add(path);
            SaveLocked();
        }
    }

    public void SaveAlbum(CustomAlbum album)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            var saved = CloneAlbum(album);
            if (!string.IsNullOrWhiteSpace(saved.ArtworkPath) && File.Exists(saved.ArtworkPath))
                saved.ArtworkPath = CopyArtwork(saved.ArtworkPath);
            _state.CustomAlbums.RemoveAll(item => item.Id == saved.Id);
            _state.CustomAlbums.Add(saved);
            SaveLocked();
        }
    }

    public void SavePlaylist(CustomPlaylist playlist)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            var saved = ClonePlaylist(playlist);
            _state.Playlists.RemoveAll(item => item.Id == saved.Id);
            _state.Playlists.Add(saved);
            SaveLocked();
        }
    }

    public void DeleteAlbum(string id)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            _state.CustomAlbums.RemoveAll(item => item.Id == id);
            SaveLocked();
        }
    }

    public void DeletePlaylist(string id)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            _state.Playlists.RemoveAll(item => item.Id == id);
            SaveLocked();
        }
    }

    public void SetFavorite(string path, bool favorite)
    {
        path = NormalizePath(path);
        lock (_gate)
        {
            EnsureLoadedLocked();
            _state.FavoritePaths.RemoveAll(item => SamePath(item, path));
            if (favorite)
                _state.FavoritePaths.Add(path);
            SaveLocked();
        }
    }

    public void RecordListening(string path)
    {
        path = NormalizePath(path);
        lock (_gate)
        {
            EnsureLoadedLocked();
            var entry = _state.History.FirstOrDefault(item => SamePath(item.Path, path));
            if (entry is null)
            {
                entry = new ListeningHistoryEntry { Path = path };
                _state.History.Add(entry);
            }
            entry.LastPlayedUtc = DateTime.UtcNow;
            entry.PlayCount++;
            SaveLocked();
        }
    }

    public PlaybackSession GetSession()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            return CloneSession(_state.Session);
        }
    }

    public void SaveSession(PlaybackSession session)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            var saved = CloneSession(session);
            NormalizeSession(saved);
            saved.SavedUtc = DateTime.UtcNow;
            // Frequent position saves do not rewrite the potentially large metadata cache.
            AtomicWrite(SessionFile, saved);
            _state.Session = saved;
            _committedState.Session = CloneSession(saved);
        }
    }

    public void RelinkTrack(string oldPath, string newPath)
    {
        oldPath = NormalizePath(oldPath);
        newPath = NormalizePath(newPath);
        if (!File.Exists(newPath))
            throw new FileNotFoundException("Choose an existing audio file.", newPath);
        if (!IsSupported(newPath))
            throw new InvalidDataException("Choose a FLAC, WAV, MP3, or ALAC audio file.");

        lock (_gate)
        {
            EnsureLoadedLocked();
            if (!_state.TrackPaths.Contains(oldPath, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("This track is no longer in the library.");
            _state.MetadataCache.TryGetValue(oldPath, out var previous);
            var replacement = ReadTrack(newPath, previous);
            if (!replacement.CanPlay)
                throw new InvalidDataException(replacement.Error ?? "The replacement file could not be read.");
            ReplacePath(_state.TrackPaths, oldPath, newPath, true);
            foreach (var album in _state.CustomAlbums)
                ReplacePath(album.TrackPaths, oldPath, newPath);
            foreach (var playlist in _state.Playlists)
                ReplacePath(playlist.TrackPaths, oldPath, newPath);
            ReplacePath(_state.FavoritePaths, oldPath, newPath, true);
            ReplacePath(_state.Session.QueuePaths, oldPath, newPath);
            foreach (var entry in _state.History.Where(item => SamePath(item.Path, oldPath)))
                entry.Path = newPath;
            _state.History = _state.History.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ListeningHistoryEntry
                {
                    Path = group.Key,
                    LastPlayedUtc = group.Max(item => item.LastPlayedUtc),
                    PlayCount = group.Sum(item => item.PlayCount)
                }).ToList();
            _state.MetadataCache.Remove(oldPath);
            _state.MetadataCache[newPath] = replacement;
            _state.Session.SavedUtc = DateTime.UtcNow;
            SaveLocked();
            if (!SamePath(oldPath, newPath))
                _relinkedPaths[oldPath] = newPath;
            // The library already contains this session with a newer timestamp.
            // A failed cache write must not misreport a successfully committed relink.
            try { AtomicWrite(SessionFile, _state.Session); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _recoveryNotice = "The track was reconnected. Its playback session will be recovered from the library on the next launch.";
            }
        }
    }

    public static bool HasSupportedExtension(string path) => Extensions.Contains(Path.GetExtension(path));

    public static bool IsSupported(string path)
    {
        if (!HasSupportedExtension(path))
            return false;
        var extension = Path.GetExtension(path);
        return !extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ReadMp4AudioCodec(path), "alac", StringComparison.Ordinal);
    }

    private Track ReadOrReuse(string path, Track? previous)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return (previous ?? Placeholder(path)) with { Path = path, IsMissing = true };
            if (previous is not null && !previous.IsMissing && string.IsNullOrWhiteSpace(previous.Error)
                && previous.MetadataVersion == MetadataReaderVersion
                && previous.FileSize == info.Length && previous.LastWriteUtc == info.LastWriteTimeUtc
                && (previous.ArtworkPath is null || File.Exists(previous.ArtworkPath)))
                return previous;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (previous ?? Placeholder(path)) with { IsMissing = false, Error = "XTONE cannot access this file." };
        }
        return ReadTrack(path, previous);
    }

    private Track ReadTrack(string path, Track? previous)
    {
        if (!File.Exists(path))
            return (previous ?? Placeholder(path)) with { Path = path, IsMissing = true };
        Interlocked.Increment(ref _metadataReadCount);
        long size = 0;
        DateTime written = default;
        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            written = info.LastWriteTimeUtc;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            string format = extension.TrimStart('.').ToUpperInvariant();
            string codec = format;
            Mp4AudioInfo? mp4Info = null;
            bool? lossless = extension switch { ".flac" => true, ".mp3" => false, _ => null };
            if (extension is ".m4a" or ".mp4")
            {
                mp4Info = ReadMp4AudioInfo(path);
                codec = mp4Info?.Codec ?? "Unknown MP4 audio";
                if (codec != "alac")
                    throw new InvalidDataException(codec == "mp4a"
                        ? "This M4A file contains AAC. XTONE currently supports ALAC in M4A containers."
                        : "No supported ALAC audio stream was found in this container.");
                format = "ALAC";
                codec = "Apple Lossless";
                lossless = true;
            }
            else if (extension == ".wav")
            {
                (codec, lossless) = ReadWaveCodec(path);
            }

            using var file = TagLib.File.Create(path);
            var properties = file.Properties;
            var tag = file.Tag;
            var duration = properties.Duration;
            if (properties.AudioSampleRate <= 0 || properties.AudioChannels <= 0)
                throw new InvalidDataException("The file does not contain a readable audio stream.");
            var bitrate = properties.AudioBitrate;
            if (bitrate <= 0 && duration.TotalSeconds > 0)
                bitrate = (int)Math.Round(size * 8 / duration.TotalSeconds / 1000d);
            string? artworkPath = null;
            var picture = tag.Pictures.FirstOrDefault();
            if (picture?.Data?.Data is { Length: > 0 } bytes)
            {
                try { artworkPath = SaveArtwork(bytes, picture.MimeType); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return new Track
            {
                Id = previous?.Id ?? Guid.NewGuid().ToString("N"),
                MetadataVersion = MetadataReaderVersion,
                Path = path,
                Title = string.IsNullOrWhiteSpace(tag.Title) ? Path.GetFileNameWithoutExtension(path) : tag.Title,
                Artist = ArtistCredits.Clean(tag.Performers) is { Length: > 0 } performers ? string.Join(", ", performers) : "Unknown artist",
                AlbumArtist = string.Join(", ", ArtistCredits.Clean(tag.AlbumArtists)),
                Performers = tag.Performers?.ToArray() ?? [],
                AlbumPerformers = tag.AlbumArtists?.ToArray() ?? [],
                Album = string.IsNullOrWhiteSpace(tag.Album) ? "Unknown album" : tag.Album,
                TrackNumber = tag.Track,
                DiscNumber = tag.Disc,
                Format = format,
                Codec = codec,
                IsLossless = lossless,
                Duration = duration,
                BitRateKbps = bitrate,
                SampleRate = properties.AudioSampleRate,
                BitDepth = lossless == false ? 0 : properties.BitsPerSample > 0 ? properties.BitsPerSample : mp4Info?.BitDepth ?? 0,
                Channels = properties.AudioChannels,
                ArtworkPath = artworkPath,
                FileSize = size,
                LastWriteUtc = written,
                AddedUtc = previous?.AddedUtc ?? DateTime.UtcNow
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return (previous ?? Placeholder(path)) with
            {
                Path = path, IsMissing = false,
                Error = error is UnauthorizedAccessException ? "XTONE cannot access this file." : error.Message,
                FileSize = size, LastWriteUtc = written
            };
        }
    }

    private static Track Placeholder(string path) => new()
    {
        Path = path,
        Title = Path.GetFileNameWithoutExtension(path),
        Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant()
    };

    private void EnsureLoadedLocked()
    {
        if (_loaded)
            return;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(ArtworkFolder);
        _state = ReadRecoverable<LibraryState>(StateFile) ?? new();
        if (_state.Version < 3 && File.Exists(StateFile) && !File.Exists(StateFile + ".pre-folder-migration"))
            File.Copy(StateFile, StateFile + ".pre-folder-migration", false);
        NormalizeState(_state);
        var session = ReadRecoverable<PlaybackSession>(SessionFile);
        if (session is not null && session.SavedUtc >= _state.Session.SavedUtc)
            _state.Session = session;
        NormalizeSession(_state.Session);
        _committedState = CloneState(_state);
        _loaded = true;
    }

    private T? ReadRecoverable<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return ReadJsonFile<T>(path)
                ?? throw new JsonException("The saved file was empty.");
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // Retain the damaged file for recovery rather than silently replacing it.
            try { File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"), false); }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException) { }
            var backup = path + ".bak";
            if (File.Exists(backup))
            {
                try
                {
                    var recovered = ReadJsonFile<T>(backup);
                    if (recovered is not null)
                    {
                        _recoveryNotice = "XTONE recovered your saved library from its backup.";
                        AtomicWrite(path, recovered, false);
                        return recovered;
                    }
                }
                catch (Exception backupError) when (backupError is IOException or JsonException or UnauthorizedAccessException) { }
            }
            _recoveryNotice = "A saved library file could not be read. A copy has been kept in the XTONE data folder.";
            return null;
        }
    }

    private T? ReadJsonFile<T>(string path)
    {
        // Keep large metadata caches in UTF-8 rather than materializing a second
        // complete UTF-16 string before deserialization. Atomic replacement may
        // proceed while this reader finishes its consistent old-file snapshot.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 65536, FileOptions.SequentialScan);
        return JsonSerializer.Deserialize<T>(stream, _json);
    }

    private static void NormalizeState(LibraryState state)
    {
        state.MusicRoot = TryNormalizePath(state.MusicRoot, out var root) ? root : null;
        state.AlbumLayout = FolderLibrary.NormalizeLayout(state.AlbumLayout);
        state.PinnedFolderPaths = DistinctPaths(state.PinnedFolderPaths ?? []).Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var artwork = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in state.FolderArtworkPaths ?? [])
            if (TryNormalizePath(pair.Key, out var folder) && TryNormalizePath(pair.Value, out var image))
                artwork[Path.TrimEndingDirectorySeparator(folder)] = image;
        state.FolderArtworkPaths = artwork;
        state.TrackPaths = DistinctPaths(state.TrackPaths ?? []);
        state.FolderPaths = DistinctPaths(state.FolderPaths ?? []);
        state.FavoritePaths = DistinctPaths(state.FavoritePaths ?? []);
        state.CustomAlbums = (state.CustomAlbums ?? []).Where(album => album is not null).ToList();
        state.Playlists = (state.Playlists ?? []).Where(playlist => playlist is not null).ToList();
        state.History = (state.History ?? []).Where(entry => entry is not null && TryNormalizePath(entry.Path, out _))
            .Select(entry => new ListeningHistoryEntry
            {
                Path = NormalizePath(entry.Path), LastPlayedUtc = entry.LastPlayedUtc, PlayCount = Math.Max(0, entry.PlayCount)
            }).ToList();
        state.Session ??= new();
        foreach (var album in state.CustomAlbums)
        {
            album.TrackPaths = DistinctPaths(album.TrackPaths ?? []);
            album.Title ??= "Untitled album";
            album.Artist ??= "Various artists";
            if (string.IsNullOrWhiteSpace(album.Id))
                album.Id = Guid.NewGuid().ToString("N");
        }
        foreach (var playlist in state.Playlists)
        {
            playlist.TrackPaths = DistinctPaths(playlist.TrackPaths ?? []);
            playlist.Name ??= "Untitled playlist";
            if (string.IsNullOrWhiteSpace(playlist.Id))
                playlist.Id = Guid.NewGuid().ToString("N");
        }
        NormalizeSession(state.Session);
        var cache = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in state.MetadataCache ?? [])
            if (pair.Value is not null && TryNormalizePath(pair.Key, out var path))
                cache[path] = pair.Value with
                {
                    Id = string.IsNullOrWhiteSpace(pair.Value.Id) ? Guid.NewGuid().ToString("N") : pair.Value.Id,
                    Path = path,
                    Title = pair.Value.Title ?? Path.GetFileNameWithoutExtension(path),
                    Artist = pair.Value.Artist ?? "Unknown artist",
                    AlbumArtist = pair.Value.AlbumArtist ?? "",
                    Performers = pair.Value.Performers ?? [],
                    AlbumPerformers = pair.Value.AlbumPerformers ?? [],
                    Album = pair.Value.Album ?? "Unknown album",
                    Format = pair.Value.Format ?? Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                    Codec = pair.Value.Codec ?? ""
                };
        state.MetadataCache = cache;
        state.Version = 3;
    }

    private static void NormalizeSession(PlaybackSession session)
    {
        session.MusicView = session.MusicView == "List" ? "List" : "Grid";
        session.FocusVisualization = session.FocusVisualization is "Bars" or "Off" ? session.FocusVisualization : "Ribbon";
        session.QueuePaths = (session.QueuePaths ?? []).Where(path => TryNormalizePath(path, out _)).Select(NormalizePath).ToList();
        session.Volume = double.IsFinite(session.Volume) ? Math.Clamp(session.Volume, 0, 1) : .72;
        session.PositionSeconds = double.IsFinite(session.PositionSeconds)
            ? Math.Clamp(session.PositionSeconds, 0, TimeSpan.FromDays(366).TotalSeconds) : 0;
        session.CurrentIndex = Math.Clamp(session.CurrentIndex, -1, Math.Max(-1, session.QueuePaths.Count - 1));
        if (session.RepeatMode is not ("Off" or "All" or "One"))
            session.RepeatMode = session.Repeat ? "One" : "Off";
        if (session.Repeat && session.RepeatMode == "Off")
            session.RepeatMode = "One";
        session.LastSection = session.LastSection is "Albums" or "Library" or "Favorites" or "History" or "Artists" ? session.LastSection : "Library";
    }

    private LibrarySnapshot SnapshotLocked()
    {
        var favorites = new HashSet<string>(_state.FavoritePaths, StringComparer.OrdinalIgnoreCase);
        return new LibrarySnapshot(
            _state.TrackPaths.Where(path => _state.MusicRoot is null || FolderLibrary.IsWithinRoot(path, _state.MusicRoot)).Select(path =>
            {
                var track = _state.MetadataCache.GetValueOrDefault(path) ?? Placeholder(path);
                // A snapshot is an in-memory view. RefreshAsync checks availability
                // in the background so large or disconnected libraries never force
                // one filesystem lookup per track while this state lock is held.
                return track with { IsFavorite = favorites.Contains(path), Performers = [.. track.Performers], AlbumPerformers = [.. track.AlbumPerformers] };
            }).ToList(),
            _state.MusicRoot is { } root ? [root] : [.. _state.FolderPaths],
            _state.CustomAlbums.Select(CloneAlbum).ToList(),
            _state.Playlists.Select(ClonePlaylist).ToList())
        {
            Revision = _revision,
            MusicRoot = _state.MusicRoot,
            AlbumLayout = _state.AlbumLayout,
            PinnedFolderPaths = [.. _state.PinnedFolderPaths],
            FolderArtworkPaths = new(_state.FolderArtworkPaths, StringComparer.OrdinalIgnoreCase),
            History = _state.History.Select(CloneHistory).OrderByDescending(item => item.LastPlayedUtc).ToList(),
            Session = CloneSession(_state.Session),
            RecoveryNotice = _recoveryNotice
        };
    }

    private void SaveLocked()
    {
        try
        {
            AtomicWrite(StateFile, _state);
            _committedState = CloneState(_state);
            ++_revision;
        }
        catch
        {
            _state = CloneState(_committedState);
            throw;
        }
    }

    private void AtomicWrite<T>(string path, T value, bool backupCurrent = true)
    {
        Directory.CreateDirectory(_root);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       32768, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, _json);
                stream.Flush(true);
            }
            if (File.Exists(path) && backupCurrent)
                File.Replace(temporary, path, path + ".bak", true);
            else
                File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private string SaveArtwork(byte[] data, string? mimeType)
    {
        var extension = mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png", "image/webp" => ".webp", "image/bmp" => ".bmp", _ => ".jpg"
        };
        var target = Path.Combine(ArtworkFolder, Convert.ToHexString(SHA256.HashData(data))[..32] + extension);
        if (!File.Exists(target))
            File.WriteAllBytes(target, data);
        return target;
    }

    private string CopyArtwork(string sourcePath)
    {
        var data = File.ReadAllBytes(sourcePath);
        var target = Path.Combine(ArtworkFolder,
            Convert.ToHexString(SHA256.HashData(data))[..32] + Path.GetExtension(sourcePath).ToLowerInvariant());
        if (!File.Exists(target))
            File.WriteAllBytes(target, data);
        return target;
    }

    private static (string Codec, bool? Lossless) ReadWaveCodec(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) is not ("RIFF" or "RF64"))
            throw new InvalidDataException("Invalid WAV header.");
        reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("Invalid WAV header.");
        while (stream.Position + 8 <= stream.Length)
        {
            var name = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadUInt32();
            var start = stream.Position;
            if (length > stream.Length - start)
                break;
            if (name == "fmt " && length >= 16)
            {
                var tag = reader.ReadUInt16();
                if (tag == 0xfffe && length >= 40)
                {
                    stream.Position = start + 24;
                    tag = reader.ReadUInt16();
                }
                return tag switch
                {
                    1 => ("PCM", true), 3 => ("IEEE float PCM", true),
                    2 => ("Microsoft ADPCM", false), 17 => ("IMA ADPCM", false),
                    6 => ("A-law", false), 7 => ("Mu-law", false),
                    _ => ($"WAV codec 0x{tag:X4}", null)
                };
            }
            stream.Position = start + length + (length & 1);
        }
        throw new InvalidDataException("No WAV audio format chunk was found.");
    }

    private sealed record Mp4AudioInfo(string Codec, int BitDepth = 0);

    private static string? ReadMp4AudioCodec(string path) => ReadMp4AudioInfo(path)?.Codec;

    private static Mp4AudioInfo? ReadMp4AudioInfo(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return FindMp4Codec(stream, 0, stream.Length, 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static Mp4AudioInfo? FindMp4Codec(Stream stream, long start, long end, int depth)
    {
        if (depth > 12)
            return null;
        var header = new byte[16];
        Mp4AudioInfo? fallback = null;
        for (var position = start; position + 8 <= end;)
        {
            stream.Position = position;
            stream.ReadExactly(header, 0, 8);
            var smallSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            long headerSize = 8;
            long size = smallSize;
            if (smallSize == 1)
            {
                if (position + 16 > end)
                    return fallback;
                stream.ReadExactly(header, 8, 8);
                var extended = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                if (extended > long.MaxValue)
                    return fallback;
                size = (long)extended;
                headerSize = 16;
            }
            else if (smallSize == 0)
                size = end - position;
            if (size < headerSize || size > end - position)
                return fallback;
            var content = position + headerSize;
            if (type == "stsd" && size >= headerSize + 8)
            {
                stream.Position = content;
                stream.ReadExactly(header, 0, 8);
                var entries = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
                var found = FindMp4SampleEntry(stream, content + 8, position + size, entries);
                if (found?.Codec == "alac")
                    return found;
                fallback ??= found;
            }
            else if (type is "moov" or "trak" or "mdia" or "minf" or "stbl")
            {
                var found = FindMp4Codec(stream, content, position + size, depth + 1);
                if (found?.Codec == "alac")
                    return found;
                fallback ??= found;
            }
            position += size;
        }
        return fallback;
    }

    private static Mp4AudioInfo? FindMp4SampleEntry(Stream stream, long start, long end, uint entries)
    {
        Span<byte> header = stackalloc byte[8];
        Mp4AudioInfo? fallback = null;
        var position = start;
        for (uint entry = 0; entry < entries && position + 8 <= end; entry++)
        {
            stream.Position = position;
            stream.ReadExactly(header);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (size < 8 || size > end - position)
                break;
            var codec = Encoding.ASCII.GetString(header[4..]);
            if (codec == "alac")
                return new Mp4AudioInfo(codec, ReadAlacBitDepth(stream, position + 8, position + size));
            if (codec is "mp4a" or "enca")
                fallback = new Mp4AudioInfo(codec);
            position += size;
        }
        return fallback;
    }

    private static int ReadAlacBitDepth(Stream stream, long start, long end)
    {
        // AudioSampleEntry has a fixed 28-byte header in version 0. Its ALAC
        // configuration box carries the authoritative depth when TagLib omits it.
        if (end - start < 28)
            return 0;
        Span<byte> sample = stackalloc byte[28];
        stream.Position = start;
        stream.ReadExactly(sample);
        var version = BinaryPrimitives.ReadUInt16BigEndian(sample[8..10]);
        var depth = (int)BinaryPrimitives.ReadUInt16BigEndian(sample[18..20]);
        var child = start + 28 + (version == 1 ? 16 : version == 2 ? 36 : 0);
        Span<byte> header = stackalloc byte[8];
        while (child + 8 <= end)
        {
            stream.Position = child;
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (length < 8 || length > end - child)
                break;
            if (Encoding.ASCII.GetString(header[4..]) == "alac" && length >= 36)
            {
                // Skip the box header, full-box flags, and frameLength, then
                // read compatibleVersion followed by bitDepth in ALACSpecificConfig.
                stream.Position = child + 17;
                depth = stream.ReadByte();
                break;
            }
            child += length;
        }
        return depth is > 0 and <= 64 ? depth : 0;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);
    private static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            normalized = NormalizePath(path);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static List<string> DistinctPaths(IEnumerable<string> paths) => paths.Where(path => TryNormalizePath(path, out _))
        .Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void ReplacePath(List<string> paths, string oldPath, string newPath, bool deduplicate = false)
    {
        for (var i = 0; i < paths.Count; i++)
            if (SamePath(paths[i], oldPath))
                paths[i] = newPath;
        if (deduplicate)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            paths.RemoveAll(path => !seen.Add(path));
        }
    }

    private static CustomAlbum CloneAlbum(CustomAlbum album) => new()
    {
        Id = album.Id, Title = album.Title, Artist = album.Artist,
        Notes = album.Notes, ArtworkPath = album.ArtworkPath, TrackPaths = [.. album.TrackPaths]
    };
    private static CustomPlaylist ClonePlaylist(CustomPlaylist playlist) => new()
    {
        Id = playlist.Id, Name = playlist.Name, TrackPaths = [.. playlist.TrackPaths], SmartRule = playlist.SmartRule
    };
    private static ListeningHistoryEntry CloneHistory(ListeningHistoryEntry item) => new()
    {
        Path = item.Path, LastPlayedUtc = item.LastPlayedUtc, PlayCount = item.PlayCount
    };
    private static LibraryState CloneState(LibraryState state) => new()
    {
        Version = state.Version,
        MusicRoot = state.MusicRoot, AlbumLayout = state.AlbumLayout,
        PinnedFolderPaths = [.. state.PinnedFolderPaths],
        FolderArtworkPaths = new(state.FolderArtworkPaths, StringComparer.OrdinalIgnoreCase),
        TrackPaths = [.. state.TrackPaths], FolderPaths = [.. state.FolderPaths],
        CustomAlbums = state.CustomAlbums.Select(CloneAlbum).ToList(),
        Playlists = state.Playlists.Select(ClonePlaylist).ToList(),
        FavoritePaths = [.. state.FavoritePaths],
        History = state.History.Select(CloneHistory).ToList(),
        MetadataCache = state.MetadataCache.ToDictionary(pair => pair.Key,
            pair => pair.Value with { Performers = [.. pair.Value.Performers], AlbumPerformers = [.. pair.Value.AlbumPerformers] }, StringComparer.OrdinalIgnoreCase),
        Session = CloneSession(state.Session)
    };
    private static PlaybackSession CloneSession(PlaybackSession session) => new()
    {
        QueuePaths = [.. session.QueuePaths ?? []], CurrentIndex = session.CurrentIndex,
        PositionSeconds = session.PositionSeconds, Volume = session.Volume, Shuffle = session.Shuffle,
        Repeat = session.Repeat, RepeatMode = session.RepeatMode,
        OutputDeviceId = session.OutputDeviceId, ReducedMotion = session.ReducedMotion,
        MiniAlwaysOnTop = session.MiniAlwaysOnTop, FocusVisualization = session.FocusVisualization,
        LastSection = session.LastSection, MusicView = session.MusicView, SavedUtc = session.SavedUtc
    };
    private sealed class ScanProgressReporter(IProgress<LibraryProgress>? progress)
    {
        private long _last;
        public void FileRead(int done, int total, string message)
        {
            if (progress is null) return;
            var now = Stopwatch.GetTimestamp();
            if (done != 1 && done != total && Stopwatch.GetElapsedTime(_last, now) < TimeSpan.FromMilliseconds(100)) return;
            _last = now;
            progress.Report(new(done, total, message));
        }
    }

}
