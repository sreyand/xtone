using System.Collections.ObjectModel;
using System.Windows.Input;
using XTONE.Models;
using XTONE.Services;

namespace XTONE;

public partial class MainWindow
{
    private readonly LibraryWatcher _watcher = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private CancellationTokenSource? _scanCancellation;
    private readonly List<ListeningHistoryEntry> _history = [];
    private readonly ObservableCollection<AlbumRow> _albumRows = [];
    private int _albumColumns = 4;
    private bool _closed;
    private bool _libraryReady;
    private bool _refreshPending;
    private bool _watcherSubscribed;

    private async Task LoadLibraryAsync()
    {
        var snapshot = await Task.Run(_store.Load);
        if (_closed || _closingRequested) return;
        ApplySnapshot(snapshot);
        if (_snapshotSession is { } session) await RestoreSessionAsync(session);
        if (_closed || _closingRequested) return;
        _libraryReady = true;
        RefreshInterfaceState();
        if (!_watcherSubscribed) { _watcher.Changed += Watcher_Changed; _watcherSubscribed = true; }
        _watcher.Configure(_folders);
        await ScanAsync(null, quiet: true);
    }

    private PlaybackSession? _snapshotSession;
    private void ApplySnapshot(LibrarySnapshot snapshot)
    {
        _tracks.Clear(); _tracks.AddRange(snapshot.Tracks);
        OverlayPendingFavorites();
        _folders.Clear(); _folders.AddRange(snapshot.Folders);
        _customAlbums.Clear(); _customAlbums.AddRange(snapshot.CustomAlbums);
        _playlists.Clear(); _playlists.AddRange(snapshot.Playlists);
        _history.Clear(); _history.AddRange(snapshot.History);
        _snapshotSession = snapshot.Session;
        RebuildCollections();
        _watcher.Configure(_folders);
        if (!string.IsNullOrEmpty(snapshot.RecoveryNotice)) SetStatus(snapshot.RecoveryNotice);
    }

    private async void Watcher_Changed(object? sender, EventArgs e)
    {
        if (_closed || _closingRequested) return;
        await Dispatcher.InvokeAsync(async () =>
        {
            if (_scanCancellation is not null) { _refreshPending = true; return; }
            await ScanAsync(null, quiet: true);
        });
    }

    private async Task ScanAsync(IEnumerable<string>? paths, bool quiet = false)
    {
        if (_isDesignPreview || _closed || _closingRequested) return;
        if (paths is null && _folders.Count == 0 && _tracks.Count == 0) { RefreshInterfaceState(); return; }
        // A manual import must retain its selected inputs while another scan finishes.
        if (paths is not null)
        {
            paths = paths.ToList();
            if (_scanCancellation is not null) SetStatus("Music queued. Finishing the current folder refresh…");
            await _scanGate.WaitAsync();
        }
        else if (!await _scanGate.WaitAsync(0)) { _refreshPending = true; return; }
        if (_closed || _closingRequested) { _scanGate.Release(); return; }
        _scanCancellation = new CancellationTokenSource();
        var cancellation = _scanCancellation;
        var priorPaths = _tracks.Select(track => track.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _showScanProgress = paths is not null || !quiet;
        if (_showScanProgress) _emptyImportNotice = null;
        ScanCancelButton.Visibility = _showScanProgress ? Visibility.Visible : Visibility.Collapsed;
        ScanProgress.Visibility = _showScanProgress ? Visibility.Visible : Visibility.Collapsed;
        ScanProgress.IsIndeterminate = true;
        if (_showScanProgress) SetStatus(paths is null ? "Checking music folders…" : "Reading your music…", false);
        RefreshInterfaceState();
        var progress = new Progress<LibraryProgress>(value =>
        {
            if (_closed || !ReferenceEquals(_scanCancellation, cancellation)) return;
            if (value.Message.StartsWith("Folder unavailable:", StringComparison.OrdinalIgnoreCase)) warnings.Add(value.Message);
            if (!_showScanProgress) return;
            ScanStatus.Text = value.Total > 0 ? $"{value.Message}  {value.Done:N0}/{value.Total:N0}" : value.Message;
            ScanProgress.IsIndeterminate = value.Total == 0;
            ScanProgress.Maximum = Math.Max(1, value.Total);
            ScanProgress.Value = value.Done;
            RefreshInterfaceState();
        });
        try
        {
            var token = _scanCancellation.Token;
            var snapshot = paths is null ? await _store.RefreshAsync(progress, token)
                : await _store.ImportAsync(paths, progress, token);
            if (_closed || _closingRequested) return;
            ApplySnapshot(snapshot);
            var added = _tracks.Count(track => !priorPaths.Contains(track.Path));
            var issues = _tracks.Count(track => track.IsMissing || !string.IsNullOrWhiteSpace(track.Error));
            if (paths is not null)
            {
                var summary = added > 0 ? $"Added {added:N0} {(added == 1 ? "track" : "tracks")}." : "No new tracks found. These files may already be in your library, or contain no supported music.";
                if (issues > 0) summary += $" {issues:N0} {(issues == 1 ? "file needs" : "files need")} attention. Use File details or Locate file.";
                if (warnings.Count > 0) summary += $" {warnings.Count:N0} {(warnings.Count == 1 ? "folder was" : "folders were")} unavailable.";
                _emptyImportNotice = _tracks.Count == 0 ? summary : null;
                if (issues > 0 || warnings.Count > 0) SetScanStatus(summary);
                else
                {
                    SetStatus("", false); _scanOwnedNotice = null;
                    if (_tracks.Count == 0) _emptyImportNotice = "No supported audio found. Choose FLAC, WAV, ALAC (M4A), or MP3 files.";
                    ShowNotice(added > 0 ? summary : "No new tracks found.");
                }
            }
            else if (issues > 0 || warnings.Count > 0)
                SetScanStatus(issues > 0 ? $"{issues:N0} files need attention. Use File details or Locate file." : "A music folder is unavailable. Reconnect it and rescan.");
            else if (!quiet) { SetStatus("", false); _scanOwnedNotice = null; ShowNotice("Library refreshed."); }
            else if (_scanOwnedNotice is not null && ScanStatus.Text == _scanOwnedNotice)
            { SetStatus("", false); _scanOwnedNotice = null; _emptyImportNotice = null; }
        }
        catch (OperationCanceledException) { if (!_closed) SetStatus("Scan cancelled. Your saved library is intact."); }
        catch (Exception error) { if (!_closed) SetStatus($"Couldn't update the library: {error.Message}"); }
        finally
        {
            _scanCancellation?.Dispose(); _scanCancellation = null;
            _scanGate.Release();
            if (!_closed)
            {
                _showScanProgress = false;
                ScanCancelButton.Visibility = Visibility.Collapsed;
                ScanProgress.Visibility = Visibility.Collapsed;
                RefreshInterfaceState();
                if (_refreshPending) { _refreshPending = false; _ = ScanAsync(null, true); }
            }
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) { _refreshPending = false; _scanCancellation?.Cancel(); }
    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync(null);
    private void SetScanStatus(string message)
    { SetStatus(message); _scanOwnedNotice = message; }

    private void SetStatus(string message, bool attention = true)
    {
        ScanStatus.Text = message; ScanStatus.ToolTip = message;
        _statusNeedsAttention = attention && !string.IsNullOrWhiteSpace(message) && message != "Library up to date";
        if (_tracks.Count == 0 && attention) _emptyImportNotice = message;
        RefreshInterfaceState();
    }

    private void RebuildCollections()
    {
        var byPath = _tracks.ToDictionary(track => track.Path, StringComparer.OrdinalIgnoreCase);
        List<Track> Resolve(IEnumerable<string> paths) => paths.Select(path => byPath.GetValueOrDefault(path))
            .Where(track => track is not null).Cast<Track>().ToList();
        _albumCards.Clear();
        foreach (var custom in _customAlbums)
            _albumCards.Add(new AlbumCard { Id = custom.Id, Title = custom.Title, Artist = custom.Artist,
                ArtworkPath = custom.ArtworkPath, Notes = custom.Notes, IsCreatedLocally = true, Tracks = Resolve(custom.TrackPaths) });
        foreach (var group in _tracks.GroupBy(track => (Title: track.Album == "Unknown album"
                     ? Path.GetFileName(Path.GetDirectoryName(track.Path)) ?? "Unsorted" : track.Album,
                     Artist: track.AlbumGroupingArtist)).OrderBy(group => group.Key.Title, StringComparer.OrdinalIgnoreCase))
        {
            var tracks = group.OrderBy(track => track.DiscNumber).ThenBy(track => track.TrackNumber)
                .ThenBy(track => track.Title, StringComparer.OrdinalIgnoreCase).ToList();
            _albumCards.Add(new AlbumCard { Id = "files:" + group.Key.Title + "|" + group.Key.Artist,
                Title = group.Key.Title, Artist = group.Key.Artist, Tracks = tracks,
                ArtworkPath = tracks.Select(track => track.ArtworkPath).FirstOrDefault(path => path is not null) });
        }
        _artistCards.Clear();
        foreach (var group in _tracks.Where(track => !string.IsNullOrWhiteSpace(track.Artist) && !track.Artist.Equals("Unknown artist", StringComparison.OrdinalIgnoreCase)).GroupBy(track => track.Artist, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key))
            _artistCards.Add(new(group.Key, group.Count()));
        _playlistCards.Clear();
        foreach (var playlist in _playlists) _playlistCards.Add(new(playlist, ResolvePlaylist(playlist)));
        FolderSummary.Text = _folders.Count == 0 ? "Music folder" : Path.GetFileName(_folders[0].TrimEnd('\\', '/'))
            + (_folders.Count > 1 ? $" (+{_folders.Count - 1})" : "");
        FolderSummary.ToolTip = _folders.Count == 0 ? "Choose a music folder" : string.Join("\n", _folders);
        if (_activeAlbum is { } active)
        {
            if (_activePlaylist is { } previousPlaylist)
            {
                _activePlaylist = _playlists.FirstOrDefault(playlist => playlist.Id == previousPlaylist.Id);
                if (_activePlaylist is null) { ShowSection(_section); return; }
            }
            var refreshed = _activePlaylist is { } playlist ? new AlbumCard { Id = playlist.Id, Title = playlist.Name,
                Artist = playlist.SmartRule is null ? "Private playlist" : "Smart playlist", Tracks = ResolvePlaylist(playlist) }
                : _albumCards.FirstOrDefault(album => album.Id == active.Id);
            if (refreshed is not null) OpenAlbum(refreshed); else ShowSection(_section);
        }
        UpdateSectionSubtitle(); UpdateVisiblePage();
    }

    private List<Track> ResolvePlaylist(CustomPlaylist playlist)
    {
        var lookup = _tracks.ToDictionary(track => track.Path, StringComparer.OrdinalIgnoreCase);
        var played = _history.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return playlist.SmartRule switch
        {
            "favorites" => _tracks.Where(track => track.IsFavorite).OrderBy(track => track.Artist).ThenBy(track => track.Title).ToList(),
            "lossless" => _tracks.Where(track => track.QualityLabel == "Lossless").OrderBy(track => track.Album).ThenBy(track => track.TrackNumber).ToList(),
            "recent" => RecentlyPlayed(),
            "unplayed" => _tracks.Where(track => !played.Contains(track.Path)).OrderBy(track => track.Title).ToList(),
            _ => playlist.TrackPaths.Select(path => lookup.GetValueOrDefault(path)).Where(track => track is not null).Cast<Track>().ToList()
        };
    }

    private List<Track> RecentlyPlayed()
    {
        var byPath = _tracks.ToDictionary(track => track.Path, StringComparer.OrdinalIgnoreCase);
        return _history.OrderByDescending(entry => entry.LastPlayedUtc).Select(entry => byPath.GetValueOrDefault(entry.Path))
            .Where(track => track is not null).Cast<Track>().ToList();
    }

    private void RefreshSmartViews()
    {
        _playlistCards.Clear();
        foreach (var playlist in _playlists) _playlistCards.Add(new(playlist, ResolvePlaylist(playlist)));
        if (_activePlaylist is { SmartRule: not null } active)
            OpenAlbum(new AlbumCard { Id = active.Id, Title = active.Name, Artist = "Smart playlist", Tracks = ResolvePlaylist(active) });
        else { UpdateSectionSubtitle(); UpdateVisiblePage(); }
    }

    private IEnumerable<Track> FilteredTracks()
    {
        var query = SearchBox.Text.Trim();
        IEnumerable<Track> tracks = _section switch { "Favorites" => _tracks.Where(track => track.IsFavorite), "History" => RecentlyPlayed(), _ => _tracks };
        var filter = (ViewFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var played = _history.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        tracks = filter switch
        {
            "lossless" => tracks.Where(track => track.QualityLabel == "Lossless"),
            "missing" => tracks.Where(track => track.IsMissing || !string.IsNullOrWhiteSpace(track.Error)),
            "unplayed" => tracks.Where(track => !played.Contains(track.Path)),
            _ => tracks
        };
        tracks = tracks.Where(track => query.Length == 0 || track.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || track.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) || track.Album.Contains(query, StringComparison.OrdinalIgnoreCase));
        return ((SortPicker.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "artist" => tracks.OrderBy(track => track.Artist).ThenBy(track => track.Album).ThenBy(track => track.TrackNumber),
            "album" => tracks.OrderBy(track => track.Album).ThenBy(track => track.DiscNumber).ThenBy(track => track.TrackNumber),
            "added" => tracks.OrderByDescending(track => track.AddedUtc),
            _ => _section == "History" ? tracks : tracks.OrderBy(track => track.Title)
        };
    }

    private void UpdateVisiblePage()
    {
        if (!IsInitialized || SearchBox is null) return;
        RefreshInterfaceState();
        var query = SearchBox.Text.Trim();
        if (_activeAlbum is not null) return;
        if (_section == "Albums")
        {
            var albums = _albumCards.Where(album => query.Length == 0 || album.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || album.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) || album.Tracks.Any(track => track.Title.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
            _albumRows.Clear();
            for (var i = 0; i < albums.Count; i += _albumColumns) _albumRows.Add(new(albums.Skip(i).Take(_albumColumns).ToList(), _albumColumns));
            SetEmpty(albums.Count == 0, _tracks.Count == 0 ? "Your albums, in one place." : "No results");
        }
        else if (_section is "Library" or "Favorites" or "History")
        {
            var tracks = FilteredTracks().ToList(); LibraryTracks.ItemsSource = tracks;
            SetEmpty(tracks.Count == 0, _section == "Favorites" ? "No favorites yet" : _section == "History" ? "No listening history yet" : "No tracks");
        }
        else if (_section == "Artists")
        {
            var artists = _artistCards.Where(artist => query.Length == 0 || artist.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            ArtistItems.ItemsSource = artists; SetEmpty(artists.Count == 0, "No results");
        }
        else if (_section == "Playlists")
        {
            var playlists = _playlistCards.Where(playlist => query.Length == 0 || playlist.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            PlaylistItems.ItemsSource = playlists;
            SetEmpty(playlists.Count == 0, query.Length > 0 ? "No results" : "No playlists yet");
        }
    }

    private void SetEmpty(bool empty, string headline)
    {
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyHeadline.Text = headline;
        EmptyDescription.Text = SearchBox.Text.Length > 0 || ViewFilter.SelectedIndex > 0 ? "Try another search or clear your filters."
            : _section == "Favorites" ? "Favorite a track to keep it here."
            : _section == "History" ? "Tracks you listen to will appear here."
            : _section == "Playlists" ? "Use Create playlist to bring a few tracks together." : "Add music to grow your library.";
        RefreshInterfaceState();
    }
    private void AlbumViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var columns = Math.Clamp((int)Math.Floor((e.NewSize.Width + 20) / 220), 1, 6);
        if (columns == _albumColumns) return;
        _albumColumns = columns; UpdateVisiblePage();
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateVisiblePage(); }
    private sealed record AlbumRow(List<AlbumCard> Albums, int Columns);
}
