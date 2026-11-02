using System.Collections.ObjectModel;
using System.Windows.Input;
using XTONE.Models;
using XTONE.Services;

namespace XTONE;

public partial class MainWindow
{
    private readonly LibraryWatcher _watcher = new(ignoredDirectory: Environment.GetEnvironmentVariable("XTONE_DATA_DIR"));
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private CancellationTokenSource? _scanCancellation;
    private readonly List<ListeningHistoryEntry> _history = [];
    private readonly ResettableObservableCollection<AlbumRow> _albumRows = [];
    private int _albumColumns = 4;
    private readonly ResettableObservableCollection<MusicBrowserItem> _musicItems = [];
    private string _musicView = "Grid";
    private bool _closed;
    private bool _libraryReady;
    private bool _refreshPending;
    private bool _watcherSubscribed;
    private string? _musicRoot;
    private string _albumLayout = "AlbumFolders";
    private bool _rootUnavailable;
    private long _snapshotApplyVersion;
    private long _lastSnapshotRevision = -1;
    private LibraryPresentationIndex? _presentationIndex;
    private Track[] _recentTracks = [];
    private HashSet<string> _playedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasFavorites, _hasHistory;
    private CancellationTokenSource? _pageQueryCancellation;
    private long _pageQueryVersion;
    private System.Windows.Threading.DispatcherTimer? _searchDebounce;
    private string? _publishedPageKey;

    private async Task LoadLibraryAsync()
    {
        var snapshot = await Task.Run(_store.Load);
        if (_closed || _closingRequested) return;
        await ApplySnapshotAsync(snapshot);
        if (_snapshotSession is { } session) await RestoreSessionAsync(session);
        if (_closed || _closingRequested) return;
        _libraryReady = true;
        RefreshInterfaceState();
        if (!_watcherSubscribed) { _watcher.Changed += Watcher_Changed; _watcherSubscribed = true; }
        _watcher.Configure(_folders);
        await ScanAsync(null, quiet: true);
    }

    private PlaybackSession? _snapshotSession;
    private async Task ApplySnapshotAsync(LibrarySnapshot snapshot)
    {
        if (snapshot.Revision < _lastSnapshotRevision) return;
        var version = ++_snapshotApplyVersion;
        CancelVisiblePageQuery();
        var prepared = await Task.Run(() =>
        {
            var albums = FolderLibrary.BuildAlbums(snapshot.Tracks, snapshot.MusicRoot, snapshot.AlbumLayout, snapshot.FolderArtworkPaths);
            var unavailable = snapshot.MusicRoot is not null && !Directory.Exists(snapshot.MusicRoot);
            return (Albums: albums, Unavailable: unavailable, Index: LibraryPresentationIndex.Build(snapshot.Tracks, albums, unavailable));
        });
        if (_closed || _closingRequested || version != _snapshotApplyVersion || snapshot.Revision < _lastSnapshotRevision) return;
        _lastSnapshotRevision = snapshot.Revision;
        _rootUnavailable = prepared.Unavailable;
        _presentationIndex = prepared.Index;
        _publishedPageKey = null;
        _tracks.Clear(); _tracks.AddRange(snapshot.Tracks);
        OverlayPendingFavorites();
        _musicRoot = snapshot.MusicRoot; _albumLayout = snapshot.AlbumLayout;
        _pinnedFolderPaths.Clear(); _pinnedFolderPaths.AddRange(snapshot.PinnedFolderPaths);
        _folders.Clear(); if (_musicRoot is not null) _folders.Add(_musicRoot);
        _history.Clear(); _history.AddRange(snapshot.History);
        RefreshHistoryCache();
        _snapshotSession = snapshot.Session;
        RebuildCollections(prepared.Albums);
        if (_queue.Entries.Count > 0)
        {
            _queue.RefreshTracks(track => prepared.Index.ByPath.GetValueOrDefault(track.Path) ?? track);
            if (_queue.Current is { } current) _currentTrack = current.Track;
            UpdateNowPlaying();
        }
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

    private async Task ScanAsync(IEnumerable<string>? paths, bool quiet = false, string? layout = null)
    {
        if (_isDesignPreview || _closed || _closingRequested) return;
        if (paths is null && _musicRoot is null) { RefreshInterfaceState(); return; }
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
                : await _store.ConfigureMusicFolderAsync(paths.Single(), layout ?? _albumLayout, progress, token);
            if (_closed || _closingRequested) return;
            await ApplySnapshotAsync(snapshot);
            if (_closed || _closingRequested) return;
            var issues = _tracks.Count(track => track.IsMissing || !string.IsNullOrWhiteSpace(track.Error));
            if (_rootUnavailable)
                SetScanStatus("Music folder unavailable. Reconnect it, then rescan.");
            else if (issues > 0 || warnings.Count > 0)
                SetScanStatus(issues > 0 ? $"{issues:N0} {(issues == 1 ? "file needs" : "files need")} attention. Use File details or Locate file." : "Part of your Music folder could not be read. Check folder access and rescan.");
            else
            {
                if (!quiet || _scanOwnedNotice is not null && ScanStatus.Text == _scanOwnedNotice)
                { SetStatus("", false); _scanOwnedNotice = null; _emptyImportNotice = null; }
                if (paths is not null) ShowNotice("Music folder updated.");
                else if (!quiet) ShowNotice("Library refreshed.");
            }
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

    private void RebuildCollections(List<AlbumCard>? albums = null)
    {
        CancelVisiblePageQuery(); _publishedPageKey = null;
        _hasFavorites = _tracks.Any(track => track.IsFavorite);
        if (albums is null)
        {
            // Favorite changes do not alter folder membership or artist identity.
            UpdateVisiblePage(); return;
        }
        _albumCards.ReplaceAll(albums);
        RebuildMusicItems(); RefreshPinnedFolders(); RebuildFolderArtwork();
        _artistCards.ReplaceAll((_presentationIndex?.Artists ?? []).Select(artist => new ArtistSummary(artist.Name, artist.Count)));
        FolderSummary.Text = _musicRoot is null ? "Choose music folder" : Path.GetFileName(_musicRoot.TrimEnd('\\', '/'));
        FolderSummary.ToolTip = _musicRoot ?? "Choose your Music folder";
        if (_activeAlbum is { } active)
        {
            if (active.Id.StartsWith("artist:", StringComparison.Ordinal))
            {
                var name = active.Id[7..];
                var artistTracks = _tracks.Where(track => ArtistCredits.ForTrack(track).Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
                if (artistTracks.Count > 0) OpenAlbum(new AlbumCard { Id = active.Id, Title = name, Artist = "", Tracks = artistTracks });
                else ShowSection("Artists");
            }
            else if (_albumCards.FirstOrDefault(album => album.Id == active.Id) is { } refreshed) OpenAlbum(refreshed);
            else ShowSection(_section);
        }
        UpdateSectionSubtitle(); UpdateVisiblePage();
    }

    private void RefreshHistoryCache()
    {
        var lookup = _presentationIndex?.ByPath;
        _playedPaths = _history.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _recentTracks = lookup is null ? [] : _history.OrderByDescending(entry => entry.LastPlayedUtc)
            .Select(entry => lookup.GetValueOrDefault(entry.Path)).Where(track => track is not null).Cast<Track>().ToArray();
        _hasHistory = _recentTracks.Length > 0;
    }
    private List<Track> RecentlyPlayed() => _recentTracks.ToList();
    private void RefreshSmartViews()
    { RefreshHistoryCache(); _publishedPageKey = null; UpdateSectionSubtitle(); UpdateVisiblePage(); }
    private IEnumerable<Track> FilteredTracks()
    {
        if (_presentationIndex is null) return [];
        return _presentationIndex.QueryTracks(_section, (ViewFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all",
            (SortPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "title", SearchBox.Text.Trim(), _recentTracks, _playedPaths);
    }
    private void QueueVisiblePageRefresh()
    {
        if (_tracks.Count < 2000) { UpdateVisiblePage(); return; }
        CancelVisiblePageQuery();
        _searchDebounce ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Tick -= SearchDebounce_Tick; _searchDebounce.Tick += SearchDebounce_Tick;
        _searchDebounce.Stop(); _searchDebounce.Start();
    }
    private void SearchDebounce_Tick(object? sender, EventArgs e)
    { _searchDebounce?.Stop(); if (!_closed && !_closingRequested) UpdateVisiblePage(); }
    private void CancelVisiblePageQuery()
    { ++_pageQueryVersion; _pageQueryCancellation?.Cancel(); }
    private void CancelLibraryPresentation()
    { _searchDebounce?.Stop(); CancelVisiblePageQuery(); }

    private sealed record PageResult(MusicBrowserItem[]? Music = null, Track[]? Tracks = null, ArtistCount[]? Artists = null);
    private async void UpdateVisiblePage()
    {
        if (!IsInitialized || SearchBox is null || _closed || _closingRequested) return;
        _searchDebounce?.Stop(); RefreshInterfaceState();
        if (_activeAlbum is not null) { CancelVisiblePageQuery(); return; }
        var query = SearchBox.Text.Trim(); var section = _section; var view = _musicView; var columns = _albumColumns;
        var filter = (ViewFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var sort = (SortPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "title";
        var key = $"{section}\0{query}\0{filter}\0{sort}\0{view}\0{columns}";
        if (_publishedPageKey == key) return;
        CancelVisiblePageQuery(); var version = _pageQueryVersion;
        var cancellation = new CancellationTokenSource(); _pageQueryCancellation = cancellation;
        var index = _presentationIndex; var recent = _recentTracks; var played = _playedPaths;
        PageResult Query()
        {
            var token = cancellation.Token; token.ThrowIfCancellationRequested();
            return section switch
            {
                "Albums" => new(Music: index?.QueryMusic(query, token) ?? _musicItems.Where(item => query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray()),
                "Artists" => new(Artists: index?.QueryArtists(query, token) ?? []),
                _ => new(Tracks: index?.QueryTracks(section, filter, sort, query, recent, played, token) ?? [])
            };
        }
        try
        {
            // A single debounced worker prevents rapid typing from tying up the dispatcher.
            var result = _tracks.Count >= 2000 && query.Length > 0 ? await Task.Run(Query, cancellation.Token) : Query();
            if (_closed || _closingRequested || version != _pageQueryVersion || _activeAlbum is not null) return;
            _publishedPageKey = key;
            if (section == "Albums")
            {
                var items = result.Music ?? [];
                MusicList.ItemsSource = items;
                if (view == "Grid") _albumRows.ReplaceAll(Enumerable.Range(0, (items.Length + columns - 1) / columns)
                    .Select(row => new AlbumRow(items.Skip(row * columns).Take(columns).ToList(), columns)));
                SetEmpty(items.Length == 0, query.Length > 0 ? "No results" : "Add your music");
            }
            else if (section == "Artists")
            {
                var artists = result.Artists ?? [];
                ArtistItems.ItemsSource = artists.Select(artist => new ArtistSummary(artist.Name, artist.Count)).ToArray();
                SetEmpty(artists.Length == 0, "No results");
            }
            else
            {
                var tracks = result.Tracks ?? []; LibraryTracks.ItemsSource = tracks;
                SetEmpty(tracks.Length == 0, section == "Favorites" ? "No favorites yet" : section == "History" ? "No listening history yet" : query.Length > 0 ? "No results" : "No music yet");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed && !_closingRequested && version == _pageQueryVersion) SetStatus($"Couldn’t update this view: {error.Message}"); }
        finally
        { if (ReferenceEquals(_pageQueryCancellation, cancellation)) _pageQueryCancellation = null; cancellation.Dispose(); }
    }

    private void RebuildMusicItems()
    {
        if (_presentationIndex is not null) _musicItems.ReplaceAll(_presentationIndex.MusicItems);
        else
        {
            var filed = _albumCards.SelectMany(folder => folder.Tracks).Select(track => track.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _musicItems.ReplaceAll(_albumCards.Select(folder => new MusicBrowserItem { Folder = folder }).Concat(_tracks.Where(track => !filed.Contains(track.Path) && (!track.IsMissing || _rootUnavailable))
                .OrderBy(track => track.Title, StringComparer.OrdinalIgnoreCase).Select(track => new MusicBrowserItem { Track = track })));
        }
    }

    private void MusicGridView_Click(object sender, RoutedEventArgs e) => SetMusicView("Grid");
    private void MusicListView_Click(object sender, RoutedEventArgs e) => SetMusicView("List");
    private void SetMusicView(string view)
    {
        _musicView = view == "List" ? "List" : "Grid";
        UpdateVisiblePage(); SavePlaybackSession(false);
    }
    private void MusicItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MusicBrowserItem item) return;
        if (item.Folder is { } folder) OpenAlbum(folder);
        else if (item.Track is { } track) PlayTrack(track, MusicList.Items.OfType<MusicBrowserItem>().Where(entry => entry.Track is not null).Select(entry => entry.Track!).ToList());
    }

    private void SetEmpty(bool empty, string headline)
    {
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyHeadline.Text = headline;
        EmptyDescription.Text = SearchBox.Text.Length > 0 || ViewFilter.SelectedIndex > 0 ? "Try another search or clear your filters."
            : _section == "Favorites" ? "Favorite a track to keep it here."
            : _section == "History" ? "Tracks you listen to will appear here."
            : _section == "Albums" ? (_albumLayout == "ArtistAlbumFolders" ? "Create an album folder inside an artist folder, then add tracks." : "Create a folder, then add tracks to it. It is saved inside your Music folder.") : "Add audio to your Music folder to start listening.";
        RefreshInterfaceState();
    }
    private void AlbumViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var columns = Math.Clamp((int)Math.Floor((e.NewSize.Width + 20) / 220), 1, 6);
        if (columns == _albumColumns) return;
        _albumColumns = columns; UpdateVisiblePage();
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateVisiblePage(); }
    private sealed record AlbumRow(List<MusicBrowserItem> Albums, int Columns);
}
