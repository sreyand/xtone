using System.Windows.Input;
using XTONE.Dialogs;

namespace XTONE;

public partial class MainWindow
{
    private bool _showScanProgress;
    private bool _statusNeedsAttention;
    private string? _emptyImportNotice;
    private bool _libraryLoadFailed;
    private string? _libraryLoadError;
    private string? _scanOwnedNotice;

    private void RefreshInterfaceState()
    {
        if (PlayerDock is null || _closed) return;
        var music = _tracks.Count > 0 || _isDesignPreview;
        var collections = music || _customAlbums.Count > 0 || _playlists.Count > 0;
        var welcome = !collections && _activeAlbum is null;
        var ready = (_libraryReady || _isDesignPreview) && !_libraryLoadFailed;
        var scanning = _scanCancellation is not null && _showScanProgress;
        var selected = _currentTrack is not null || _isDesignPreview;
        PlayerDock.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        PlayerDockRow.Height = new GridLength(selected ? 104 : 0);
        PageHeader.Visibility = welcome ? Visibility.Collapsed : Visibility.Visible;
        MainHeaderRow.Height = new GridLength(welcome ? 0 : 64);
        PageSubtitle.Visibility = Visibility.Collapsed;
        AlbumsNav.Visibility = music || _customAlbums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistsNav.Visibility = collections ? Visibility.Visible : Visibility.Collapsed;
        DiscoveryNavigation.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        ArtistsNav.Visibility = _artistCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FavoritesNav.Visibility = _tracks.Any(track => track.IsFavorite) || _section == "Favorites" ? Visibility.Visible : Visibility.Collapsed;
        HistoryNav.Visibility = _history.Count > 0 || _section == "History" ? Visibility.Visible : Visibility.Collapsed;
        var searching = music && (SearchBox.Visibility == Visibility.Visible || ViewFilter.SelectedIndex > 0);
        SearchControlsPanel.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        LibraryToolbar.Visibility = searching || (_statusNeedsAttention || scanning) && !welcome ? Visibility.Visible : Visibility.Collapsed;
        SearchToolbarButton.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        QueueToolbarButton.Visibility = Visibility.Collapsed;
        HeaderQueueMenu.Visibility = _queue.Entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderMoreButton.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        ImportStatusPanel.Visibility = scanning || _statusNeedsAttention ? Visibility.Visible : Visibility.Collapsed;
        ViewFilter.Visibility = SortPicker.Visibility = searching && _activeAlbum is null && _section is "Library" or "Favorites" or "History" ? Visibility.Visible : Visibility.Collapsed;
        CreateButton.Visibility = music && _activeAlbum is null && _section == "Playlists" && _playlistCards.Count == 0 && SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderAddMusicButton.IsEnabled = ready && !scanning;
        RetryLoadButton.Visibility = _libraryLoadFailed ? Visibility.Visible : Visibility.Collapsed;
        WelcomeRetryButton.Visibility = welcome && _libraryLoadFailed ? Visibility.Visible : Visibility.Collapsed;
        DetailEmptyState.Visibility = _activeAlbum is { Tracks.Count: 0 } ? Visibility.Visible : Visibility.Collapsed;
        EmptyAddMusicButton.Visibility = welcome && ready && !scanning ? Visibility.Visible : Visibility.Collapsed;
        DropMusicHint.Visibility = welcome && ready && !scanning ? Visibility.Visible : Visibility.Collapsed;
        WelcomeProgress.Visibility = welcome && ((!ready && !_libraryLoadFailed) || scanning) ? Visibility.Visible : Visibility.Collapsed;
        WelcomeCancelButton.Visibility = welcome && scanning ? Visibility.Visible : Visibility.Collapsed;
        ClearResultsButton.Visibility = !welcome && EmptyState.Visibility == Visibility.Visible &&
            (SearchBox.Text.Length > 0 || ViewFilter.SelectedIndex > 0) ? Visibility.Visible : Visibility.Collapsed;
        PlaylistEmptyState.Visibility = Visibility.Collapsed;
        EmptyHeadline.FontSize = welcome ? 26 : 14;
        EmptyHeadline.FontWeight = welcome ? FontWeights.SemiBold : FontWeights.Normal;
        EmptyHeadline.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, welcome ? "InkBrush" : "MutedBrush");
        EmptyDescription.Visibility = welcome ? Visibility.Visible : Visibility.Collapsed;
        if (welcome)
        {
            EmptyState.Visibility = Visibility.Visible;
            AlbumScroll.Visibility = LibraryPanel.Visibility = ArtistsPanel.Visibility = PlaylistsPanel.Visibility = Visibility.Collapsed;
            EmptyHeadline.Text = _libraryLoadFailed ? "Couldn’t open your library" : !ready ? "Opening your library" : scanning ? "Adding your music" : "Your music, here.";
            EmptyDescription.Text = _libraryLoadFailed ? $"{_libraryLoadError} Try again to reopen it." : !ready ? "Just a moment…" : scanning ? ScanStatus.Text :
                _emptyImportNotice ?? "Add files or a folder to start listening. Your music stays where it is.";
            LibraryNav.Background = (System.Windows.Media.Brush)FindResource("SelectionBrush");
            LibraryNav.Foreground = (System.Windows.Media.Brush)FindResource(XTONE.Services.ThemeAccessibility.HighContrast ? "SelectedTextBrush" : "BlueBrush");
        }
        else if (_activeAlbum is null)
        {
            ShowSelectedNavigation();
            AlbumScroll.Visibility = _section == "Albums" ? Visibility.Visible : Visibility.Collapsed;
            LibraryPanel.Visibility = _section is "Library" or "Favorites" or "History" ? Visibility.Visible : Visibility.Collapsed;
            ArtistsPanel.Visibility = _section == "Artists" ? Visibility.Visible : Visibility.Collapsed;
            PlaylistsPanel.Visibility = _section == "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void AddMusic_Click(object sender, RoutedEventArgs e)
    {
        if (_isDesignPreview || _closed || _closingRequested || !_libraryReady || _libraryLoadFailed || _scanCancellation is not null && _showScanProgress) return;
        var choice = new AddMusicWindow { Owner = this };
        if (ShowAnimatedDialog(choice) != true) return;
        if (choice.ChooseFolder) ImportFolder_Click(sender, e); else ImportFiles_Click(sender, e);
    }

    private async void RetryLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _closingRequested || !_libraryLoadFailed) return;
        _libraryLoadFailed = false; _libraryLoadError = null; _libraryReady = false;
        _emptyImportNotice = null; SetStatus("", false);
        ScanProgress.IsIndeterminate = true; RefreshInterfaceState();
        try { await LoadLibraryAsync(); }
        catch (Exception error)
        {
            _libraryLoadFailed = true; _libraryLoadError = error.Message;
            SetStatus($"Couldn’t open your library: {error.Message}");
        }
        RefreshInterfaceState();
    }

    private void ClearResults_Click(object sender, RoutedEventArgs e)
    { SearchBox.Clear(); ViewFilter.SelectedIndex = 0; UpdateVisiblePage(); }

    private System.Windows.Threading.DispatcherTimer? _noticeTimer;
    private long _noticeVersion;
    private void ShowNotice(string text)
    {
        if (_closed || _closingRequested) return;
        ++_noticeVersion;
        NoticeText.Text = text; NoticePanel.Visibility = Visibility.Visible;
        _ = XTONE.Services.UiMotion.FadeElementIn(NoticePanel, 180);
        if (_noticeTimer is null)
        {
            _noticeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _noticeTimer.Tick += (_, _) => DismissNotice();
        }
        _noticeTimer.Stop(); _noticeTimer.Start();
    }
    private void DismissNotice_Click(object sender, RoutedEventArgs e) => DismissNotice();
    private async void DismissNotice()
    {
        _noticeTimer?.Stop(); var version = ++_noticeVersion;
        XTONE.Services.UiMotion.AnimateDouble(NoticePanel, OpacityProperty, 0, 150);
        if (XTONE.Services.UiMotion.Enabled) await Task.Delay(150);
        if (_closed || version != _noticeVersion) return;
        NoticePanel.Visibility = Visibility.Collapsed;
    }
    private void HeaderMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; }
    }

    private void PlayerMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; menu.IsOpen = true; }
    }

    private void Music_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var allowed = _libraryReady && !_closed && !_closingRequested && !_isDesignPreview && !_libraryLoadFailed && (_scanCancellation is null || !_showScanProgress);
        e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        MusicDropOverlay.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }
    private void Music_DragLeave(object sender, DragEventArgs e) => MusicDropOverlay.Visibility = Visibility.Collapsed;
    private async void Music_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true; MusicDropOverlay.Visibility = Visibility.Collapsed;
        if (!_libraryReady || _closed || _closingRequested || _isDesignPreview || _libraryLoadFailed || _scanCancellation is not null && _showScanProgress) return;
        var inputs = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        // Accept folders or supported audio only; never turn an unsupported file into a watched folder.
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".mp3", ".m4a", ".mp4" };
        var accepted = inputs.Where(path => Directory.Exists(path) || File.Exists(path) && supported.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (accepted.Length == 0)
        {
            _emptyImportNotice = "Choose FLAC, WAV, ALAC (M4A), or MP3 files, or a folder containing music.";
            SetStatus(_emptyImportNotice); RefreshInterfaceState(); return;
        }
        await ScanAsync(accepted);
    }
}
