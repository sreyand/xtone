using System.Windows.Input;
using XTONE.Dialogs;
using XTONE.Services;

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
        var welcome = _musicRoot is null && !_isDesignPreview;
        var ready = (_libraryReady || _isDesignPreview) && !_libraryLoadFailed;
        var scanning = _scanCancellation is not null && _showScanProgress;
        var selected = _currentTrack is not null || _isDesignPreview;
        PlayerDock.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        PlayerDockRow.Height = new GridLength(selected ? 104 : 0);
        PageHeader.Visibility = welcome ? Visibility.Collapsed : Visibility.Visible;
        MainHeaderRow.Height = new GridLength(welcome ? 0 : 64);
        PageSubtitle.Visibility = Visibility.Collapsed;
        MusicViewToggle.Visibility = !welcome && _section == "Albums" && _activeAlbum is null ? Visibility.Visible : Visibility.Collapsed;
        AlbumItems.Visibility = _musicView == "List" ? Visibility.Collapsed : Visibility.Visible;
        MusicList.Visibility = _musicView == "List" ? Visibility.Visible : Visibility.Collapsed;
        MusicGridButton.SetResourceReference(Button.BackgroundProperty, _musicView == "Grid" ? "SelectionBrush" : "PaperBrush");
        MusicListButton.SetResourceReference(Button.BackgroundProperty, _musicView == "List" ? "SelectionBrush" : "PaperBrush");
        AlbumsNav.Visibility = !welcome ? Visibility.Visible : Visibility.Collapsed;
        DiscoveryNavigation.Visibility = !welcome ? Visibility.Visible : Visibility.Collapsed;
        ArtistsNav.Visibility = _artistCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FavoritesNav.Visibility = _hasFavorites || _section == "Favorites" ? Visibility.Visible : Visibility.Collapsed;
        HistoryNav.Visibility = _hasHistory || _section == "History" ? Visibility.Visible : Visibility.Collapsed;
        var searching = music && (SearchBox.Visibility == Visibility.Visible || ViewFilter.SelectedIndex > 0);
        SearchControlsPanel.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        LibraryToolbar.Visibility = searching || (_statusNeedsAttention || scanning) && !welcome ? Visibility.Visible : Visibility.Collapsed;
        SearchToolbarButton.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        QueueToolbarButton.Visibility = Visibility.Collapsed;
        HeaderQueueMenu.Visibility = _queue.Entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderMoreButton.Visibility = !welcome ? Visibility.Visible : Visibility.Collapsed;
        ImportStatusPanel.Visibility = scanning || _statusNeedsAttention ? Visibility.Visible : Visibility.Collapsed;
        ViewFilter.Visibility = SortPicker.Visibility = searching && _activeAlbum is null && _section is "Library" or "Favorites" or "History" ? Visibility.Visible : Visibility.Collapsed;

        HeaderAddMusicButton.Visibility = !welcome && _section == "Albums" && _activeAlbum is null ? Visibility.Visible : Visibility.Collapsed;
        HeaderAddMusicButton.IsEnabled = ready && !scanning && !_fileOperationInProgress;
        RetryLoadButton.Visibility = _libraryLoadFailed ? Visibility.Visible : Visibility.Collapsed;
        WelcomeRetryButton.Visibility = welcome && _libraryLoadFailed ? Visibility.Visible : Visibility.Collapsed;
        DetailEmptyState.Visibility = _activeAlbum is { Tracks.Count: 0 } ? Visibility.Visible : Visibility.Collapsed;
        var emptyFolderPage = EmptyState.Visibility == Visibility.Visible && _activeAlbum is null && SearchBox.Text.Length == 0 && ViewFilter.SelectedIndex == 0 && (_tracks.Count == 0 || _section == "Albums" && _musicItems.Count == 0);
        EmptyAddMusicButton.Visibility = ready && !scanning && (welcome || emptyFolderPage && !_rootUnavailable) ? Visibility.Visible : Visibility.Collapsed;
        EmptyAddMusicButton.Content = welcome ? "Choose music folder" : _section == "Albums" ? "Create folder" : "Open music folder";
        DropMusicHint.Visibility = Visibility.Collapsed;
        WelcomeProgress.Visibility = welcome && ((!ready && !_libraryLoadFailed) || scanning) ? Visibility.Visible : Visibility.Collapsed;
        WelcomeCancelButton.Visibility = welcome && scanning ? Visibility.Visible : Visibility.Collapsed;
        ClearResultsButton.Visibility = !welcome && EmptyState.Visibility == Visibility.Visible &&
            (SearchBox.Text.Length > 0 || ViewFilter.SelectedIndex > 0) ? Visibility.Visible : Visibility.Collapsed;

        EmptyHeadline.FontSize = welcome ? 26 : 14;
        EmptyHeadline.FontWeight = welcome ? FontWeights.SemiBold : FontWeights.Normal;
        EmptyHeadline.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, welcome ? "InkBrush" : "MutedBrush");
        EmptyDescription.Visibility = welcome || emptyFolderPage ? Visibility.Visible : Visibility.Collapsed;
        if (welcome)
        {
            EmptyState.Visibility = Visibility.Visible;
            AlbumScroll.Visibility = LibraryPanel.Visibility = ArtistsPanel.Visibility = Visibility.Collapsed;
            EmptyHeadline.Text = _libraryLoadFailed ? "Couldn’t open your library" : !ready ? "Opening your library" : scanning ? "Reading your music" : "Choose your Music folder";
            EmptyDescription.Text = _libraryLoadFailed ? $"{_libraryLoadError} Try again to reopen it." : !ready ? "Just a moment…" : scanning ? ScanStatus.Text :
                _emptyImportNotice ?? "Browse your tracks and folders. Your files stay where they are.";
            LibraryNav.Background = (System.Windows.Media.Brush)FindResource("SelectionBrush");
            LibraryNav.Foreground = (System.Windows.Media.Brush)FindResource(XTONE.Services.ThemeAccessibility.HighContrast ? "SelectedTextBrush" : "BlueBrush");
        }
        else if (_activeAlbum is null)
        {
            ShowSelectedNavigation();
            AlbumScroll.Visibility = _section == "Albums" ? Visibility.Visible : Visibility.Collapsed;
            LibraryPanel.Visibility = _section is "Library" or "Favorites" or "History" ? Visibility.Visible : Visibility.Collapsed;
            ArtistsPanel.Visibility = _section == "Artists" ? Visibility.Visible : Visibility.Collapsed;

        }
    }

    private bool _chooseFolderOnSettingsOpen;
    private void AddMusic_Click(object sender, RoutedEventArgs e) => ChooseMusicFolder_Click(sender, e);
    private void ChooseMusicFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_isDesignPreview || _closed || _closingRequested || !_libraryReady || _libraryLoadFailed || _scanCancellation is not null && _showScanProgress) return;
        if (sender == EmptyAddMusicButton && _musicRoot is not null) { if (_section == "Albums") CreateMusicFolder_Click(sender, e); else OpenMusicFolder_Click(sender, e); return; }
        _chooseFolderOnSettingsOpen = true;
        Settings_Click(sender, e);
    }
    private void OpenMusicFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_musicRoot is null) { ChooseMusicFolder_Click(sender, e); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_musicRoot) { UseShellExecute = true }); }
        catch (Exception error) { SetStatus($"Couldn’t open the Music folder: {error.Message}"); }
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
        if (FindFolderDropTarget(e.OriginalSource as DependencyObject) is not null) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var allowed = _libraryReady && !_closed && !_closingRequested && !_isDesignPreview && !_libraryLoadFailed && (_scanCancellation is null || !_showScanProgress);
        e.Effects = allowed ? DragDropEffects.Link : DragDropEffects.None;
        MusicDropOverlay.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }
    private void Music_DragLeave(object sender, DragEventArgs e) => MusicDropOverlay.Visibility = Visibility.Collapsed;
    private async void Music_Drop(object sender, DragEventArgs e)
    {
        if (FindFolderDropTarget(e.OriginalSource as DependencyObject) is not null) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true; MusicDropOverlay.Visibility = Visibility.Collapsed;
        if (!_libraryReady || _closed || _closingRequested || _isDesignPreview || _libraryLoadFailed || _scanCancellation is not null && _showScanProgress) return;
        // Dropping never copies files or silently adds another root.
        // Inside-root changes are discovered by rescan; outside files are managed in Explorer.
        var inputs = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        if (_musicRoot is not null && inputs.All(path => SamePath(path, _musicRoot) || FolderLibrary.IsWithinRoot(path, _musicRoot)))
            await ScanAsync(null);
        else
            ShowNotice(_musicRoot is null ? "Choose your Music folder in Settings." : "Place music inside your Music folder, then rescan.");
    }
}
