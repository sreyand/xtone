using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using XTONE.Dialogs;
using XTONE.Models;
using XTONE.Services;

namespace XTONE;

public partial class MainWindow : Window
{
    private readonly LibraryStore _store = new();
    private readonly AudioPlayer _player = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly List<Track> _tracks = [];
    private readonly List<string> _folders = [];
    private readonly List<CustomAlbum> _customAlbums = [];
    private readonly List<CustomPlaylist> _playlists = [];
    private readonly ObservableCollection<AlbumCard> _albumCards = [];
    private readonly ObservableCollection<ArtistSummary> _artistCards = [];
    private readonly ObservableCollection<PlaylistSummary> _playlistCards = [];
    private List<Track> _queue = [];
    private AlbumCard? _activeAlbum;
    private Track? _currentTrack;
    private int _currentIndex = -1;
    private string _section = "Albums";
    private bool _isSeeking;
    private bool _shuffle;
    private bool _repeat;
    private readonly bool _isDesignPreview = Environment.GetCommandLineArgs().Contains("--preview");
    private bool _previewPlaying = true;
    private int _previewFrame;

    public MainWindow()
    {
        InitializeComponent();
        AlbumItems.ItemsSource = _albumCards;
        ArtistItems.ItemsSource = _artistCards;
        PlaylistItems.ItemsSource = _playlistCards;
        _player.PlaybackStopped += Player_PlaybackStopped;
        _uiTimer.Tick += UiTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDesignPreview)
            LoadDesignPreview();
        else
        {
            await RefreshLibraryAsync();
            ShowSection("Albums");
        }
        _uiTimer.Start();
        DrawVisualizer();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer.Stop();
        _player.PlaybackStopped -= Player_PlaybackStopped;
        _player.Dispose();
    }

    private async Task RefreshLibraryAsync()
    {
        var snapshot = await Task.Run(_store.Load);
        _tracks.Clear();
        _tracks.AddRange(snapshot.Tracks);
        _folders.Clear();
        _folders.AddRange(snapshot.Folders);
        _customAlbums.Clear();
        _customAlbums.AddRange(snapshot.CustomAlbums);
        _playlists.Clear();
        _playlists.AddRange(snapshot.Playlists);
        RebuildCollections();
    }

    private void RebuildCollections()
    {
        _albumCards.Clear();
        foreach (var custom in _customAlbums)
        {
            var customTracks = custom.TrackPaths
                .Select(path => _tracks.FirstOrDefault(track => SamePath(track.Path, path)))
                .Where(track => track is not null).Cast<Track>().ToList();
            if (customTracks.Count == 0)
                continue;
            _albumCards.Add(new AlbumCard
            {
                Id = custom.Id,
                Title = custom.Title,
                Artist = custom.Artist,
                ArtworkPath = ExistingFile(custom.ArtworkPath),
                Artwork = LoadImage(custom.ArtworkPath),
                Notes = custom.Notes,
                IsCreatedLocally = true,
                Tracks = customTracks
            });
        }

        var groups = _tracks.GroupBy(track =>
        {
            var title = track.Album == "Unknown album"
                ? Path.GetFileName(Path.GetDirectoryName(track.Path)) ?? "Unsorted"
                : track.Album;
            return (Title: title, Artist: track.Artist);
        });
        foreach (var group in groups.OrderBy(group => group.Key.Title, StringComparer.OrdinalIgnoreCase))
        {
            var groupTracks = group.OrderBy(track => track.Title, StringComparer.OrdinalIgnoreCase).ToList();
            _albumCards.Add(new AlbumCard
            {
                Id = "files:" + group.Key.Title + "|" + group.Key.Artist,
                Title = group.Key.Title,
                Artist = group.Key.Artist,
                ArtworkPath = groupTracks.Select(track => ExistingFile(track.ArtworkPath)).FirstOrDefault(path => path is not null),
                Artwork = LoadImage(groupTracks.Select(track => ExistingFile(track.ArtworkPath)).FirstOrDefault(path => path is not null)),
                Tracks = groupTracks
            });
        }

        _artistCards.Clear();
        foreach (var group in _tracks.GroupBy(track => track.Artist, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            _artistCards.Add(new ArtistSummary(group.Key, group.Count()));

        _playlistCards.Clear();
        foreach (var playlist in _playlists)
        {
            var playlistTracks = playlist.TrackPaths
                .Select(path => _tracks.FirstOrDefault(track => SamePath(track.Path, path)))
                .Where(track => track is not null).Cast<Track>().ToList();
            _playlistCards.Add(new PlaylistSummary(playlist, playlistTracks));
        }

        if (_folders.Count == 0)
        {
            FolderSummary.Text = "Music folder";
        }
        else
        {
            var folderNames = _folders.Take(2).Select(path =>
            {
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.IsNullOrWhiteSpace(name) ? path : name;
            }).ToList();
            if (_folders.Count > 2)
                folderNames.Add($"and {_folders.Count - 2} more");
            FolderSummary.Text = folderNames[0] + (_folders.Count > 1 ? $" (+{_folders.Count - 1})" : "");
        }
        FolderSummary.ToolTip = _folders.Count == 0 ? "Choose a music folder" : string.Join("\n", _folders);
        UpdateSectionSubtitle();
        UpdateVisiblePage();
    }

    private void ShowSection(string section)
    {
        _section = section;
        _activeAlbum = null;
        AlbumDetailPanel.Visibility = Visibility.Collapsed;
        AlbumScroll.Visibility = section == "Albums" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = section == "Library" ? Visibility.Visible : Visibility.Collapsed;
        ArtistsPanel.Visibility = section == "Artists" ? Visibility.Visible : Visibility.Collapsed;
        PlaylistsPanel.Visibility = section == "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        PageTitle.Text = section;
        UpdateSectionSubtitle();
        CreateButton.Content = section == "Playlists" ? "Create playlist" : "Create album";
        CreateButton.Visibility = section is "Albums" or "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        CreateButton.Click -= CreateAlbum_Click;
        CreateButton.Click -= CreatePlaylist_Click;
        if (section == "Playlists")
            CreateButton.Click += CreatePlaylist_Click;
        else
            CreateButton.Click += CreateAlbum_Click;
        SearchBox.Visibility = Visibility.Collapsed;
        SearchBox.Clear();
        foreach (var navigation in new[] { LibraryNav, AlbumsNav, ArtistsNav, PlaylistsNav })
        {
            var selected = Equals(navigation.Tag, section);
            navigation.Background = selected ? new SolidColorBrush(Color.FromRgb(239, 243, 255)) : Brushes.Transparent;
            navigation.Foreground = selected ? (Brush)FindResource("BlueBrush") : (Brush)FindResource("InkBrush");
            navigation.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        UpdateVisiblePage();
    }

    private void UpdateVisiblePage()
    {
        if (_section == "Albums" && _activeAlbum is null)
        {
            var query = SearchBox.Text.Trim();
            var filtered = string.IsNullOrWhiteSpace(query)
                ? _albumCards.ToList()
                : _albumCards.Where(album => album.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || album.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || album.Tracks.Any(track => track.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || track.Artist.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
            AlbumItems.ItemsSource = filtered;
            EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AlbumScroll.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var emptyHeadline = EmptyState.Children.OfType<TextBlock>().FirstOrDefault();
            if (emptyHeadline is not null)
                emptyHeadline.Text = _tracks.Count == 0 ? "Your albums, in one place." : "No matches in this library.";
            EmptyDescription.Text = _tracks.Count == 0 ? "Add a music folder to start your collection." : "Try another title, artist, or track.";
        }
        else if (_section == "Library")
        {
            var query = SearchBox.Text.Trim();
            var filtered = _tracks.Where(track => string.IsNullOrEmpty(query)
                || track.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || track.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
                || track.Album.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            LibraryTracks.ItemsSource = filtered;
            EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (_section == "Playlists")
        {
            PlaylistEmptyState.Visibility = _playlistCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PlaylistItems.Visibility = _playlistCards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_isDesignPreview)
            return;
        var picker = new OpenFolderDialog { Title = "Choose a music folder", Multiselect = false };
        if (picker.ShowDialog(this) != true)
            return;

        var folder = picker.FolderName;
        PageSubtitle.Text = "Scanning local audio…";
        List<string> files;
        try
        {
            files = await Task.Run(() => Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(LibraryStore.IsSupported).ToList());
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"XTONE couldn't scan that folder.\n\n{error.Message}", "Folder import", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateSectionSubtitle();
            return;
        }

        await Task.Run(() =>
        {
            _store.AddFolder(folder);
            _store.AddFiles(files);
        });
        await RefreshLibraryAsync();
        UpdateSectionSubtitle();
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_isDesignPreview)
            return;
        var picker = new OpenFileDialog
        {
            Title = "Add music to XTONE",
            Filter = "Supported audio|*.flac;*.wav;*.mp3;*.m4a;*.mp4|All files|*.*",
            Multiselect = true
        };
        if (picker.ShowDialog(this) != true)
            return;
        var paths = picker.FileNames.Where(LibraryStore.IsSupported).ToList();
        await Task.Run(() => _store.AddFiles(paths));
        await RefreshLibraryAsync();
        UpdateSectionSubtitle();
    }

    private async void CreateAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count == 0)
        {
            MessageBox.Show(this, "Add some local music first, then you can make your own album from those tracks.", "Create album", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new CreateAlbumWindow(_tracks) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.CreatedAlbum is null)
            return;
        var album = dialog.CreatedAlbum;
        await Task.Run(() => _store.SaveAlbum(album));
        await RefreshLibraryAsync();
        ShowSection("Albums");
    }

    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count == 0)
        {
            MessageBox.Show(this, "Add local music before creating a playlist.", "Create playlist", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new CreatePlaylistWindow(_tracks) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.CreatedPlaylist is null)
            return;
        var playlist = dialog.CreatedPlaylist;
        await Task.Run(() => _store.SavePlaylist(playlist));
        await RefreshLibraryAsync();
        ShowSection("Playlists");
    }

    private void Section_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string section)
            ShowSection(section);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && (_section is "Albums" or "Library") && _activeAlbum is null)
            UpdateVisiblePage();
    }

    private void AlbumCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is AlbumCard album)
            OpenAlbum(album);
    }

    private void OpenAlbum(AlbumCard album)
    {
        _activeAlbum = album;
        AlbumScroll.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        ArtistsPanel.Visibility = Visibility.Collapsed;
        PlaylistsPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Collapsed;
        AlbumDetailPanel.Visibility = Visibility.Visible;
        PageTitle.Text = album.Title;
        PageSubtitle.Text = album.IsCreatedLocally ? "A personal album, stored on this PC" : "From your local files";
        DetailTitle.Text = album.Title;
        DetailArtist.Text = album.Artist;
        DetailMeta.Text = $"{album.Tracks.Count} tracks   ·   {FormatDuration(album.Tracks)}";
        DetailNotes.Text = album.Notes ?? "";
        DetailArtwork.Source = album.Artwork ?? LoadImage(album.ArtworkPath);
        DetailTracks.ItemsSource = album.Tracks;
    }

    private void BackToAlbums_Click(object sender, RoutedEventArgs e) => ShowSection(_section);

    private void TrackRow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Track track)
        {
            var queue = _activeAlbum?.Tracks ?? _tracks;
            PlayTrack(track, queue);
            e.Handled = true;
        }
    }

    private void Artist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ArtistSummary artist)
        {
            ShowSection("Albums");
            SearchBox.Visibility = Visibility.Visible;
            SearchBox.Text = artist.Name;
        }
    }

    private void Playlist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PlaylistSummary playlist)
        {
            _section = "Playlists";
            OpenAlbum(new AlbumCard
            {
                Id = playlist.Playlist.Id,
                Title = playlist.Playlist.Name,
                Artist = "Private playlist",
                Notes = "This playlist is saved locally. Its tracks remain in their original folders.",
                Tracks = playlist.Tracks
            });
        }
    }

    private void PlayTrack(Track track, IReadOnlyList<Track> queue)
    {
        _queue = queue.ToList();
        _currentIndex = _queue.FindIndex(candidate => SamePath(candidate.Path, track.Path));
        StartCurrentTrack();
    }

    private void StartCurrentTrack()
    {
        if (_currentIndex < 0 || _currentIndex >= _queue.Count)
            return;
        var selectedTrack = _queue[_currentIndex];
        _currentTrack = selectedTrack;
        try
        {
            _player.Play(_currentTrack.Path);
            _player.SetVolume((float)VolumeSlider.Value);
            NowPlayingTitle.Text = _currentTrack.Title;
            NowPlayingArtist.Text = _currentTrack.Artist;
            NowPlayingArtwork.Source = LoadImage(_currentTrack.ArtworkPath);
            QualityText.Text = _currentTrack.PlayerQualitySummary;
            QualityText.ToolTip = _currentTrack.DetailSummary;
            DurationText.Text = FormatTime(_player.Duration);
            ElapsedText.Text = "0:00";
            PositionSlider.Value = 0;
            PositionSlider.Maximum = Math.Max(1, _player.Duration.TotalSeconds);
            UpdatePlayPauseButton();
        }
        catch (Exception error)
        {
            _currentTrack = null;
            UpdatePlayPauseButton();
            MessageBox.Show(this,
                $"XTONE couldn't play this file. The Windows audio decoder may not be available for this format.\n\n{Path.GetFileName(selectedTrack.Path)}\n{error.Message}",
                "Playback unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isDesignPreview)
        {
            _previewPlaying = !_previewPlaying;
            UpdatePlayPauseButton();
            return;
        }
        if (_currentTrack is null)
        {
            if (_tracks.Count > 0)
                PlayTrack(_tracks[0], _tracks);
            else
                ImportFolder_Click(sender, e);
            return;
        }
        _player.Pause();
        UpdatePlayPauseButton();
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack is null || _queue.Count == 0)
            return;
        if (_player.Position.TotalSeconds > 3)
        {
            _player.Seek(TimeSpan.Zero);
            return;
        }
        _currentIndex = (_currentIndex - 1 + _queue.Count) % _queue.Count;
        StartCurrentTrack();
    }

    private void Next_Click(object sender, RoutedEventArgs e) => AdvanceQueue();

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _shuffle = !_shuffle;
        ShuffleButton.Foreground = (Brush)FindResource(_shuffle ? "BlueBrush" : "InkBrush");
        ShuffleButton.ToolTip = _shuffle ? "Shuffle on" : "Shuffle off";
    }

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _repeat = !_repeat;
        RepeatButton.Foreground = _repeat ? new SolidColorBrush(Color.FromRgb(40, 93, 255)) : new SolidColorBrush(Color.FromRgb(20, 20, 20));
        RepeatButton.ToolTip = _repeat ? "Repeat track on" : "Repeat track off";
    }

    private void AdvanceQueue()
    {
        if (_queue.Count == 0)
            return;
        if (_repeat)
        {
            StartCurrentTrack();
            return;
        }
        _currentIndex = _shuffle
            ? Random.Shared.Next(_queue.Count)
            : (_currentIndex + 1) % _queue.Count;
        StartCurrentTrack();
    }

    private void Player_PlaybackStopped(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_player.Duration > TimeSpan.Zero && _player.Position >= _player.Duration - TimeSpan.FromMilliseconds(400))
                AdvanceQueue();
            else
                UpdatePlayPauseButton();
        }));
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentTrack is not null)
        {
            if (!_isSeeking && _player.Duration.TotalSeconds > 0)
                PositionSlider.Value = Math.Clamp(_player.Position.TotalSeconds, 0, Math.Max(1, _player.Duration.TotalSeconds));
            ElapsedText.Text = FormatTime(_player.Position);
            DurationText.Text = FormatTime(_player.Duration);
        }
        DrawVisualizer();
    }

    private void PositionSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            _isSeeking = true;
    }

    private void PositionSlider_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_currentTrack is not null)
            _player.Seek(TimeSpan.FromSeconds(PositionSlider.Value));
        _isSeeking = false;
    }

    private void PositionSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            if (_currentTrack is not null)
                _player.Seek(TimeSpan.FromSeconds(PositionSlider.Value));
            _isSeeking = false;
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null)
            _player.SetVolume((float)e.NewValue);
    }

    private void UpdatePlayPauseButton()
    {
        var playing = _isDesignPreview ? _previewPlaying : _player.IsPlaying;
        PlayGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        PlayPauseButton.ToolTip = playing ? "Pause" : "Play";
    }

    private void DrawVisualizer()
    {
        if (_isDesignPreview)
        {
            var values = new float[32];
            for (var i = 0; i < values.Length; i++)
            {
                var x = i / 31d;
                var shape = Math.Exp(-Math.Pow((x - .20) / .06, 2)) * .45
                    + Math.Exp(-Math.Pow((x - .40) / .09, 2)) * .7
                    + Math.Exp(-Math.Pow((x - .59) / .07, 2)) * .95
                    + Math.Exp(-Math.Pow((x - .78) / .09, 2)) * .5;
                values[i] = (float)(shape * (.94 + .06 * Math.Sin(_previewFrame * .035 + i)));
            }
            _previewFrame++;
            Visualizer.SetSpectrum(values, _previewPlaying);
            return;
        }
        Visualizer.SetSpectrum(_player.GetSpectrum(), _player.IsPlaying);
    }

    private void UpdateSectionSubtitle()
    {
        if (_activeAlbum is not null)
            return;
        PageSubtitle.Text = _section switch
        {
            "Library" => $"{_tracks.Count:N0} tracks",
            "Artists" => $"{_artistCards.Count:N0} artists",
            "Playlists" => $"{_playlistCards.Count:N0} playlists",
            _ => _isDesignPreview ? "128 tracks · 32 albums" : $"{_tracks.Count:N0} tracks · {_albumCards.Count:N0} albums"
        };
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (ShellBorder is null)
            return;
        ShellBorder.CornerRadius = new CornerRadius(WindowState == WindowState.Maximized ? 0 : 7);
        MaximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M3,0 H12 V9 M0,3 H9 V12 H0 Z" : "M0,0 H11 V11 H0 Z");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            if (_activeAlbum is not null)
                ShowSection("Library");
            if (_section is "Albums" or "Library")
            {
                SearchBox.Visibility = Visibility.Visible;
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && SearchBox.Visibility == Visibility.Visible)
        {
            SearchBox.Clear();
            SearchBox.Visibility = Visibility.Collapsed;
            PageTitle.Focus();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            ImportFiles_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.FocusedElement == PositionSlider
                 && (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown))
        {
            _isSeeking = true;
        }
    }

    private void LoadDesignPreview()
    {
        // In-memory visual fixture: never loads or saves the user's library.
        var args = Environment.GetCommandLineArgs();
        var previewIndex = Array.IndexOf(args, "--preview");
        var reference = previewIndex >= 0 && previewIndex + 1 < args.Length
            ? LoadImage(args[previewIndex + 1]) as BitmapSource : null;
        var names = new[] { "Blue Hour", "Room Studies", "Night Drive", "Soft Focus" };
        var artists = new[] { "Mira Vale", "North Window", "Low Season", "Aster Lane" };
        var referenceRects = new[]
        {
            new Rect(344, 251, 278, 276), new Rect(645, 251, 278, 276),
            new Rect(945, 251, 278, 276), new Rect(1246, 251, 278, 276)
        };
        _albumCards.Clear();
        for (var i = 0; i < names.Length; i++)
        {
            ImageSource? artwork = null;
            if (reference is not null)
            {
                var rect = referenceRects[i];
                var scaleX = reference.PixelWidth / 1586d;
                var scaleY = reference.PixelHeight / 992d;
                var crop = new Int32Rect((int)(rect.X * scaleX), (int)(rect.Y * scaleY),
                    (int)(rect.Width * scaleX), (int)(rect.Height * scaleY));
                var bitmap = new CroppedBitmap(reference, crop);
                bitmap.Freeze();
                artwork = bitmap;
            }
            _albumCards.Add(new AlbumCard { Id = $"preview-{i}", Title = names[i], Artist = artists[i], Artwork = artwork });
        }
        ShowSection("Albums");
        NowPlayingTitle.Text = "Glasshouse";
        NowPlayingArtist.Text = "Mira Vale";
        NowPlayingArtwork.Source = _albumCards[0].Artwork;
        QualityText.Text = "FLAC · Lossless · 24-bit / 96 kHz";
        PositionSlider.Maximum = 324;
        PositionSlider.Value = 138;
        ElapsedText.Text = "2:18";
        DurationText.Text = "5:24";
        VolumeSlider.Value = .42;
        UpdatePlayPauseButton();
    }

    private static string FormatDuration(IEnumerable<Track> tracks)
    {
        var duration = TimeSpan.FromSeconds(tracks.Sum(track => track.Duration.TotalSeconds));
        return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
    }

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? time.ToString(@"h\:mm\:ss")
        : time.ToString(@"m\:ss");

    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string? ExistingFile(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    private static System.Windows.Media.ImageSource? LoadImage(string? path)
    {
        if (ExistingFile(path) is not { } existing)
            return null;
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(existing, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ArtistSummary(string Name, int Count)
    {
        public string CountText => $"{Count} {(Count == 1 ? "track" : "tracks")}";
    }

    private sealed record PlaylistSummary(CustomPlaylist Playlist, List<Track> Tracks)
    {
        public string Name => Playlist.Name;
        public string TrackCountText => $"{Tracks.Count} {(Tracks.Count == 1 ? "track" : "tracks")}";
    }
}
