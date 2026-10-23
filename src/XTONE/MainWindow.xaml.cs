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
    private readonly List<CustomAlbum> _customAlbums = [];
    private readonly List<CustomPlaylist> _playlists = [];
    private readonly ObservableCollection<AlbumCard> _albumCards = [];
    private readonly ObservableCollection<ArtistSummary> _artistCards = [];
    private readonly ObservableCollection<PlaylistSummary> _playlistCards = [];
    private AlbumCard? _activeAlbum;
    private CustomPlaylist? _activePlaylist;
    private Track? _currentTrack;
    private string _section = "Albums";
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
        PlaylistItems.ItemsSource = _playlistCards; QueueItems.ItemsSource = _queue.Entries;
        _player.PlaybackStopped += Player_PlaybackStopped;
        _player.NextTrackStarted += Player_NextTrackStarted;
        _uiTimer.Tick += UiTimer_Tick;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeWindowsControls();
        try { if (_isDesignPreview) LoadDesignPreview(); else await LoadLibraryAsync(); }
        catch (Exception error) { if (!_closed) { ShowSection("Albums"); SetStatus($"Couldn't open the library: {error.Message}"); } }
        if (_closed) return;
        _uiTimer.Start(); DrawVisualizer();
    }
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SavePlaybackSession(); _closed = true; _scanCancellation?.Cancel();
        _watcher.Changed -= Watcher_Changed; _watcher.Dispose(); _uiTimer.Stop(); _mediaSession?.Dispose();
        if (_accessibility is not null) { _accessibility.Changed -= Accessibility_Changed; _accessibility.Dispose(); }
        _player.PlaybackStopped -= Player_PlaybackStopped; _player.NextTrackStarted -= Player_NextTrackStarted; _player.Dispose();
    }
    private void ShowSection(string section)
    {
        _section = section is "Albums" or "Library" or "Artists" or "Playlists" or "Favorites" or "History" ? section : "Albums";
        _activeAlbum = null; _activePlaylist = null;
        AlbumDetailPanel.Visibility = EmptyState.Visibility = Visibility.Collapsed;
        AlbumScroll.Visibility = _section == "Albums" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = _section is "Library" or "Favorites" or "History" ? Visibility.Visible : Visibility.Collapsed;
        ArtistsPanel.Visibility = _section == "Artists" ? Visibility.Visible : Visibility.Collapsed;
        PlaylistsPanel.Visibility = _section == "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        ViewFilter.Visibility = SortPicker.Visibility = _section is "Library" or "Favorites" or "History" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = _section; CreateButton.Content = _section == "Playlists" ? "Create playlist" : "Create album";
        CreateButton.Visibility = _section is "Albums" or "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Clear();
        foreach (var navigation in new[] { LibraryNav, AlbumsNav, ArtistsNav, PlaylistsNav, FavoritesNav, HistoryNav })
        {
            var selected = Equals(navigation.Tag, _section);
            navigation.Background = selected ? (Brush)FindResource("SelectionBrush") : Brushes.Transparent;
            navigation.Foreground = (Brush)FindResource(selected && ThemeAccessibility.HighContrast ? "SelectedTextBrush" : selected ? "BlueBrush" : "InkBrush");
            navigation.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        UpdateSectionSubtitle(); UpdateVisiblePage();
    }
    private void UpdateSectionSubtitle()
    {
        if (_activeAlbum is not null) return;
        PageSubtitle.Text = _section switch
        {
            "Library" => $"{_tracks.Count:N0} tracks", "Favorites" => $"{_tracks.Count(track => track.IsFavorite):N0} favorites",
            "History" => $"{_history.Count:N0} tracks played", "Artists" => $"{_artistCards.Count:N0} artists",
            "Playlists" => $"{_playlistCards.Count:N0} playlists",
            _ => _isDesignPreview ? "128 tracks · 32 albums" : $"{_tracks.Count:N0} tracks · {_albumCards.Count:N0} albums"
        };
    }
    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_isDesignPreview) return;
        var picker = new OpenFolderDialog { Title = "Choose a music folder", Multiselect = true };
        if (picker.ShowDialog(this) == true) await ScanAsync(picker.FolderNames);
    }
    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_isDesignPreview) return;
        var picker = new OpenFileDialog { Title = "Add music to XTONE", Filter = "Audio files|*.flac;*.wav;*.mp3;*.m4a;*.mp4|All files|*.*", Multiselect = true };
        if (picker.ShowDialog(this) == true) await ScanAsync(picker.FileNames);
    }
    private void Section_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is string section) ShowSection(section); }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) UpdateVisiblePage(); }
    private void Search_Click(object sender, RoutedEventArgs e) { SearchBox.Visibility = Visibility.Visible; SearchBox.Focus(); SearchBox.SelectAll(); }
    private void AlbumCard_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is AlbumCard album) OpenAlbum(album); }
    private void OpenAlbum(AlbumCard album)
    {
        _activeAlbum = album;
        AlbumScroll.Visibility = EmptyState.Visibility = ArtistsPanel.Visibility = PlaylistsPanel.Visibility = LibraryPanel.Visibility = Visibility.Collapsed;
        ViewFilter.Visibility = SortPicker.Visibility = Visibility.Collapsed; AlbumDetailPanel.Visibility = Visibility.Visible;
        PageTitle.Text = _activePlaylist is null ? "Album" : "Playlist";
        PageSubtitle.Text = _activePlaylist?.SmartRule is { } rule ? $"Smart playlist · {rule}" : album.IsCreatedLocally ? "A personal album, stored on this PC" : "From your local files";
        DetailTitle.Text = album.Title; DetailArtist.Text = album.Artist;
        DetailMeta.Text = $"{album.Tracks.Count:N0} tracks   ·   {FormatDuration(album.Tracks)}"; DetailNotes.Text = album.Notes ?? "";
        DetailArtwork.Source = album.Artwork; DetailArtwork.SourcePath = album.ArtworkPath; DetailTracks.ItemsSource = album.Tracks;
        EditCollectionButton.Content = _activePlaylist is not null || album.IsCreatedLocally ? "Edit" : "Make personal album";
        DeleteCollectionButton.Visibility = _activePlaylist is not null || album.IsCreatedLocally ? Visibility.Visible : Visibility.Collapsed;
    }
    private void BackToAlbums_Click(object sender, RoutedEventArgs e) => ShowSection(_section);
    private void Artist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ArtistSummary artist) return;
        ShowSection("Albums"); SearchBox.Visibility = Visibility.Visible; SearchBox.Text = artist.Name;
    }
    private void Playlist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PlaylistSummary playlist) return;
        _activePlaylist = playlist.Playlist;
        OpenAlbum(new AlbumCard { Id = playlist.Playlist.Id, Title = playlist.Name, Artist = playlist.Playlist.SmartRule is null ? "Private playlist" : "Smart playlist", Tracks = ResolvePlaylist(playlist.Playlist) });
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
    { if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item) { item.IsSelected = true; item.Focus(); } }
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
                case Key.F: Search_Click(sender, e); break; case Key.O: ImportFiles_Click(sender, e); break;
                case Key.Q: Queue_Click(sender, e); break; case Key.OemComma: Settings_Click(sender, e); break;
                case Key.D: if (_currentTrack is { } track) ToggleFavorite(track); break;
                case Key.Right: Next_Click(sender, e); break; case Key.Left: Previous_Click(sender, e); break;
                default: return;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (QueuePanel.Visibility == Visibility.Visible) QueuePanel.Visibility = Visibility.Collapsed;
            else if (SearchBox.Visibility == Visibility.Visible) { SearchBox.Clear(); SearchBox.Visibility = Visibility.Collapsed; SearchToolbarButton.Focus(); }
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
    }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MainContent is null) return;
        var compact = ActualWidth < 1240 || ActualHeight < 800;
        SidebarColumn.Width = new GridLength(compact ? 240 : 276);
        MainContent.Margin = compact ? new(26, 44, 22, 16) : new(42, 68, 28, 24);
        MainHeaderRow.Height = new GridLength(compact ? 88 : 110);
        PlayerDockRow.Height = new GridLength(compact ? 152 : 176);
        VisualizerRow.Height = QualitySpacerRow.Height = new GridLength(compact ? 38 : 58);
        TransportRow.Height = QualityInfoRow.Height = new GridLength(compact ? 64 : 68);
        Visualizer.Height = compact ? 38 : 58;
        PageTitle.FontSize = compact ? 48 : 64;
        PageTitle.LineHeight = compact ? 64 : 80;
        PageSubtitle.FontSize = compact ? 16 : 20;
        SidebarContents.Margin = compact ? new(12, 42, 12, 12) : new(12, 54, 12, 12);
        foreach (var nav in new[] { LibraryNav, AlbumsNav, ArtistsNav, PlaylistsNav }) { nav.Height = compact ? 46 : 58; nav.FontSize = compact ? 18 : 20; }
        foreach (var nav in new[] { FavoritesNav, HistoryNav }) { nav.Height = compact ? 38 : 44; nav.FontSize = compact ? 15 : 16; }
    }
    private sealed record ArtistSummary(string Name, int Count) { public string CountText => $"{Count:N0} {(Count == 1 ? "track" : "tracks")}"; }
    private sealed record PlaylistSummary(CustomPlaylist Playlist, List<Track> Tracks)
    { public string Name => Playlist.Name; public string TrackCountText => $"{Tracks.Count:N0} tracks" + (Playlist.SmartRule is null ? "" : " · Smart"); }
}
