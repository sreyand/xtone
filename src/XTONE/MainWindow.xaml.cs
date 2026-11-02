using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using XTONE.Models;
using XTONE.Services;

namespace XTONE;

public partial class MainWindow : Window
{
    private readonly LibraryStore _store = new(Environment.GetEnvironmentVariable("XTONE_DATA_DIR"));
    private readonly AudioPlayer _player = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly List<Track> _tracks = [];
    private readonly List<string> _folders = [];
    private readonly ResettableObservableCollection<AlbumCard> _albumCards = [];
    private readonly ResettableObservableCollection<ArtistSummary> _artistCards = [];
    private AlbumCard? _activeAlbum;
    private Track? _currentTrack;
    private string _section = "Albums";
    private Button HeaderAddMusicButton => NewFolderButton;
    private bool _isSeeking;
    private readonly bool _isDesignPreview = Environment.GetCommandLineArgs().Contains("--preview");
    private bool _previewPlaying = true;
    private int _previewFrame;

    public MainWindow()
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        AlbumItems.ItemsSource = _albumRows; ArtistItems.ItemsSource = _artistCards;
        QueueItems.ItemsSource = _queue.Entries;
        ScanProgress.IsIndeterminate = true;
        InitializeListeningViews();
        RefreshInterfaceState();
        _player.PlaybackStopped += Player_PlaybackStopped;
        _player.NextTrackStarted += Player_NextTrackStarted;
        _uiTimer.Tick += UiTimer_Tick;
        UiMotion.Enabled = ThemeAccessibility.AnimationsEnabled;
        UiMotion.PrepareFadeIn(this);
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeWindowsControls();
        _ = UiMotion.FadeInAsync(this);
        try { if (_isDesignPreview) LoadDesignPreview(); else await LoadLibraryAsync(); }
        catch (Exception error) { if (!_closed) { _libraryLoadFailed = true; _libraryLoadError = error.Message; ShowSection("Library"); SetStatus($"Couldn’t open your library: {error.Message}"); } }
        if (_closed || _closingRequested) return;
        _uiTimer.Start(); UpdateListeningActivity(); DrawVisualizer();
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--mini")) ShowMiniView();
        else if (arguments.Contains("--focus")) ShowFocusView();
    }
    private bool _closingRequested;
    private bool _allowClose;
    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closingRequested) return;
        _closingRequested = true;
        CancelLibraryPresentation();
        ++_viewTransitionVersion;
        SavePlaybackSession(false);
        IsEnabled = false;
        if (TaskbarItemInfo is { } taskbar) foreach (var button in taskbar.ThumbButtonInfos) button.IsEnabled = false;
        if (_focusWindow is not null) _focusWindow.IsEnabled = false;
        if (_miniWindow is not null) _miniWindow.IsEnabled = false;
        _focusWindow?.PrepareToHide(); _miniWindow?.PrepareToHide();
        _noticeTimer?.Stop();
        _scanCancellation?.Cancel(); _uiTimer.Stop(); StopCurrentPlayback();
        var fades = ListeningWindows().Where(window => window.IsVisible).Select(window => UiMotion.FadeOutAsync(window)).ToArray();
        // Let this cancelled Closing event return before issuing the final Close.
        await Task.Yield();
        try { await Task.WhenAll(fades); await DrainLibraryWritesAsync(); await _sessionSaveTask; }
        finally
        {
            _closed = true; CloseListeningViews();
            _watcher.Changed -= Watcher_Changed; _watcher.Dispose(); _uiTimer.Stop(); _mediaSession?.Dispose();
            if (_accessibility is not null) { _accessibility.Changed -= Accessibility_Changed; _accessibility.Dispose(); }
            _player.PlaybackStopped -= Player_PlaybackStopped; _player.NextTrackStarted -= Player_NextTrackStarted; _player.Dispose();
            _allowClose = true; Close();
        }
    }
    private void ShowSection(string section)
    {
        _section = section is "Albums" or "Library" or "Artists" or "Favorites" or "History" ? section : "Albums";
        if (_section == "Artists" && _artistCards.Count == 0) _section = "Library";
        _activeAlbum = null;
        AlbumDetailPanel.Visibility = EmptyState.Visibility = Visibility.Collapsed;
        AlbumScroll.Visibility = _section == "Albums" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = _section is "Library" or "Favorites" or "History" ? Visibility.Visible : Visibility.Collapsed;
        ArtistsPanel.Visibility = _section == "Artists" ? Visibility.Visible : Visibility.Collapsed;
        ViewFilter.Visibility = SortPicker.Visibility = _section is "Library" or "Favorites" or "History" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = _section == "Albums" ? "Music" : _section == "Library" ? "All tracks" : _section == "History" ? "Recently played" : _section;
        SearchBox.Clear(); SearchBox.Visibility = Visibility.Collapsed; ViewFilter.SelectedIndex = 0;
        foreach (var navigation in new[] { LibraryNav, AlbumsNav, ArtistsNav, FavoritesNav, HistoryNav })
        {
            var selected = Equals(navigation.Tag, _section);
            navigation.Background = selected ? (Brush)FindResource("SelectionBrush") : Brushes.Transparent;
            navigation.Foreground = (Brush)FindResource(selected && ThemeAccessibility.HighContrast ? "SelectedTextBrush" : selected ? "BlueBrush" : "InkBrush");
            navigation.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        UpdateSectionSubtitle(); UpdateVisiblePage();
        RefreshInterfaceState();
        FadePageIn(_section switch { "Albums" => AlbumScroll, "Artists" => ArtistsPanel, _ => LibraryPanel });
    }
    private void UpdateSectionSubtitle()
    { PageSubtitle.Text = ""; PageSubtitle.Visibility = Visibility.Collapsed; }

    private void Section_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is string section) ShowSection(section); }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) QueueVisiblePageRefresh(); }
    private void Search_Click(object sender, RoutedEventArgs e) { if (_tracks.Count == 0 && !_isDesignPreview) return; if (_activeAlbum is not null) ShowSection("Library"); SearchBox.Visibility = Visibility.Visible; RefreshInterfaceState(); SearchBox.Focus(); SearchBox.SelectAll(); }
    private void AlbumCard_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is AlbumCard album) OpenAlbum(album); }
    private void OpenAlbum(AlbumCard album)
    {
        _activeAlbum = album;
        DetailHero.Tag = album;
        FolderOptionsButton.Tag = album;
        AlbumScroll.Visibility = EmptyState.Visibility = ArtistsPanel.Visibility = LibraryPanel.Visibility = Visibility.Collapsed;
        ViewFilter.Visibility = SortPicker.Visibility = Visibility.Collapsed; AlbumDetailPanel.Visibility = Visibility.Visible;
        PageTitle.Text = album.Title;
        PageSubtitle.Text = "";
        DetailArtist.Text = album.Artist;
        DetailMeta.Text = $"{album.Tracks.Count:N0} {(album.Tracks.Count == 1 ? "track" : "tracks")} · {FormatDuration(album.Tracks)}";
        DetailArtwork.Source = album.Artwork; DetailArtwork.SourcePath = album.ArtworkPath; DetailTracks.ItemsSource = album.Tracks;
        var folderAlbum = !string.IsNullOrWhiteSpace(album.FolderPath) || _isDesignPreview;
        System.Windows.Automation.AutomationProperties.SetName(DetailTracks, folderAlbum ? "Folder tracks" : "Artist tracks");
        DetailEmptyState.Text = folderAlbum ? "No tracks in this folder." : "No tracks for this artist.";
        DetailArtwork.Visibility = folderAlbum ? Visibility.Visible : Visibility.Collapsed;
        DetailArtworkColumn.Width = new GridLength(folderAlbum ? 160 : 0);
        DetailSummary.Margin = folderAlbum ? new Thickness(24, 3, 0, 0) : new Thickness(0);
        OpenAlbumFolderButton.Visibility = AddFolderTracksButton.Visibility = FolderOptionsButton.Visibility = !string.IsNullOrWhiteSpace(album.FolderPath) ? Visibility.Visible : Visibility.Collapsed;
        RefreshInterfaceState();
        FadePageIn(AlbumDetailPanel);
    }
    private void BackToAlbums_Click(object sender, RoutedEventArgs e) => ShowSection(_section);
    private void Artist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ArtistSummary artist) return;
        ShowSection("Artists");
        var tracks = _tracks.Where(track => track.ArtistCredits.Contains(artist.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(track => track.Album, StringComparer.OrdinalIgnoreCase).ThenBy(track => track.DiscNumber)
            .ThenBy(track => track.TrackNumber).ThenBy(track => track.Title, StringComparer.OrdinalIgnoreCase).ToList();
        OpenAlbum(new AlbumCard { Id = "artist:" + artist.Name, Title = artist.Name, Artist = "", Tracks = tracks });
    }
    private void Tracks_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is Track track && FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null)
            PlayTrack(track, ((ListBox)sender).Items.OfType<Track>().ToList());
    }
    private void Tracks_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (sender as ListBox)?.SelectedItem is Track track)
        { PlayTrack(track, ((ListBox)sender).Items.OfType<Track>().ToList()); e.Handled = true; }
    }
    private void Track_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } item) return;
        if (!item.IsSelected && sender is ListBox list) { list.SelectedItems.Clear(); item.IsSelected = true; }
        item.Focus();
    }
    private static T? FindAncestor<T>(DependencyObject? item) where T : DependencyObject
    {
        while (item is not null) { if (item is T result) return result; item = VisualTreeHelper.GetParent(item); }
        return null;
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.F: Search_Click(sender, e); break; case Key.O: OpenMusicFolder_Click(sender, e); break;
                case Key.Q: Queue_Click(sender, e); break; case Key.OemComma: Settings_Click(sender, e); break;
                case Key.D: if (_currentTrack is { } track) ToggleFavorite(track); break;
                case Key.Right: Next_Click(sender, e); break; case Key.Left: Previous_Click(sender, e); break;
                default: return;
            }
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key is Key.F or Key.M)
        { if (e.Key == Key.F) ShowFocusView(); else ShowMiniView(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            if (QueuePanel.Visibility == Visibility.Visible) _ = SetQueueVisibleAsync(false);
            else if (SearchBox.Visibility == Visibility.Visible) { SearchBox.Clear(); SearchBox.Visibility = Visibility.Collapsed; ViewFilter.SelectedIndex = 0; RefreshInterfaceState(); SearchToolbarButton.Focus(); }
            else if (_activeAlbum is not null) ShowSection(_section); else return;
            e.Handled = true;
        }
        else if (e.Key == Key.F5) { Rescan_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox and not ComboBox and not Slider and not Button)
        { PlayPause_Click(sender, e); e.Handled = true; }
        else if (Keyboard.FocusedElement == PositionSlider && e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) _isSeeking = true;
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (ShellBorder is null) return;
        ShellBorder.CornerRadius = new(WindowState == WindowState.Maximized ? 0 : 7);
        MaximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized ? "M3,0 H12 V9 M0,3 H9 V12 H0 Z" : "M0,0 H11 V11 H0 Z");
        UpdateListeningActivity();
    }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MainContent is null) return;
        // Layout follows the pointer directly. Only visual feedback is animated;
        // interpolating layout clocks during native resize made the shell lag behind.
        var scale = Math.Clamp((ActualWidth - 960) / 640, 0, 1);
        scale = scale * scale * (3 - 2 * scale);
        double Between(double small, double large) => small + (large - small) * scale;
        SidebarColumn.Width = new GridLength(Between(205, 236));
        MainContent.Margin = new Thickness(Between(28, 48), 44, Between(24, 40), 20);
        MainHeaderRow.Height = new GridLength(64);
        VisualizerRow.Height = QualitySpacerRow.Height = new GridLength(0);
        TransportRow.Height = new GridLength(48);
        QualityInfoRow.Height = new GridLength(0);
        Visualizer.Height = 0;
        PageTitle.FontSize = 32;
        PageTitle.LineHeight = 42;
        PageSubtitle.FontSize = 13;
        SidebarContents.Margin = new Thickness(14, 38, 14, 16);
        foreach (var nav in new[] { LibraryNav, AlbumsNav, ArtistsNav, FavoritesNav, HistoryNav })
        { nav.Height = 44; nav.FontSize = 14; }
        MusicDropOverlay.Margin = new Thickness(SidebarColumn.Width.Value + 20, 52, 24, 24);
        RefreshInterfaceState();
    }

    private void FadePageIn(FrameworkElement element)
    {
        if (!IsLoaded || !ShowActivated || !UiMotion.Enabled) return;
        _ = UiMotion.FadeElementIn(element, UiMotion.SurfaceMilliseconds);
    }

    private bool? ShowAnimatedDialog(Window dialog)
    {
        UiMotion.EnableWindowFades(dialog);
        return dialog.ShowDialog();
    }

    private long _queueTransitionVersion;
    private bool _queueRequested;
    private async Task SetQueueVisibleAsync(bool visible)
    {
        var version = ++_queueTransitionVersion;
        _queueRequested = visible;
        if (visible)
        {
            if (QueuePanel.Visibility != Visibility.Visible)
            { QueuePanel.Visibility = Visibility.Visible; FadePageIn(QueuePanel); }
            else UiMotion.AnimateDouble(QueuePanel, OpacityProperty, 1, 140);
            QueueItems.Focus();
            return;
        }
        var animate = UiMotion.Enabled && ShowActivated && IsLoaded;
        UiMotion.AnimateDouble(QueuePanel, OpacityProperty, 0, animate ? 140 : 0);
        if (animate) await Task.Delay(140);
        if (version != _queueTransitionVersion || _closed) return;
        QueuePanel.Visibility = Visibility.Collapsed;
        UiMotion.AnimateDouble(QueuePanel, OpacityProperty, 1, 0);
    }
    private sealed record ArtistSummary(string Name, int Count) { public string CountText => $"{Count:N0} {(Count == 1 ? "track" : "tracks")}"; }
}
