using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using XTONE.Models;
using Track = XTONE.Models.Track;

namespace XTONE.Dialogs;

public enum CollectionKind
{
    Album,
    Playlist
}

/// <summary>
/// Edits a local collection draft. The window never writes audio, artwork or library files.
/// </summary>
public partial class CollectionEditorWindow : Window
{
    private const string DragFormat = "XTONE.CollectionEditor.Track";
    private readonly CollectionKind _kind;
    private readonly string _id;
    private readonly List<TrackChoice> _catalog;
    private readonly ObservableCollection<TrackChoice> _ordered = [];
    private readonly ICollectionView _availableView;
    private HashSet<string> _selectedKeys = new(StringComparer.OrdinalIgnoreCase);
    private string? _artworkPath;
    private bool _artworkChanged;
    private bool _ready;
    private bool _batching;
    private Point _dragOrigin;
    private TrackChoice? _dragCandidate;

    public CustomAlbum? AlbumResult { get; private set; }
    public CustomPlaylist? PlaylistResult { get; private set; }

    public CollectionEditorWindow(IEnumerable<Track> tracks, CollectionKind kind,
        CustomAlbum? album = null, CustomPlaylist? playlist = null)
    {
        InitializeComponent();
        _kind = kind;
        var editing = kind == CollectionKind.Album ? album is not null : playlist is not null;
        _id = kind == CollectionKind.Album ? album?.Id ?? Guid.NewGuid().ToString("N")
            : playlist?.Id ?? Guid.NewGuid().ToString("N");

        _catalog = tracks
            .GroupBy(track => NormalizePath(track.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => new TrackChoice(group.First().Path, group.First()))
            .OrderBy(choice => choice.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var originalPaths = kind == CollectionKind.Album ? album?.TrackPaths : playlist?.TrackPaths;
        if (originalPaths is not null)
        {
            var byPath = _catalog.ToDictionary(choice => choice.PathKey, StringComparer.OrdinalIgnoreCase);
            foreach (var path in originalPaths)
            {
                if (!byPath.TryGetValue(NormalizePath(path), out var choice))
                {
                    choice = new TrackChoice(path, null);
                    _catalog.Add(choice);
                    byPath[choice.PathKey] = choice;
                }
                // Each entry owns its position, including deliberate duplicate references.
                _ordered.Add(choice.Copy());
            }
        }

        _availableView = CollectionViewSource.GetDefaultView(_catalog);
        _availableView.Filter = AvailableFilter;
        AvailableList.ItemsSource = _availableView;
        OrderedList.ItemsSource = _ordered;
        _ordered.CollectionChanged += (_, _) =>
        {
            if (!_batching)
                RefreshSelection();
        };

        TitleBox.Text = kind == CollectionKind.Album ? album?.Title ?? "" : playlist?.Name ?? "";
        ArtistBox.Text = album?.Artist ?? "";
        NotesBox.Text = album?.Notes ?? "";
        _artworkPath = album?.ArtworkPath;
        UpdateArtworkPreview(false);

        var noun = kind == CollectionKind.Album ? "album" : "playlist";
        Title = $"{(editing ? "Edit" : "Create")} {noun} · XTONE";
        HeadingText.Text = $"{(editing ? "Edit" : "Create")} {noun}";
        SaveButton.Content = editing ? "Save changes" : $"Create {noun}";
        DescriptionText.Text = kind == CollectionKind.Album
            ? "Give your local album its own artwork, notes, and track order."
            : "Arrange a personal playlist, or let a live rule keep it up to date.";

        if (kind == CollectionKind.Playlist)
        {
            TitleLabel.Content = "_Playlist name";
            ArtistField.Visibility = Visibility.Collapsed;
            Grid.SetColumnSpan(TitleField, 3);
            AlbumFields.Visibility = Visibility.Collapsed;
            PlaylistOptions.Visibility = Visibility.Visible;
        }

        SmartRuleBox.ItemsSource = new[]
        {
            new SmartRuleChoice("favorites", "Favorite tracks", "Includes every track you have marked as a favorite."),
            new SmartRuleChoice("lossless", "Lossless audio", "Includes the lossless files in your local library."),
            new SmartRuleChoice("recent", "Recently played", "Follows the music you have listened to recently."),
            new SmartRuleChoice("unplayed", "Not played yet", "Finds the tracks you have not listened to in XTONE.")
        };
        SmartRuleBox.SelectedValue = playlist?.SmartRule ?? "favorites";
        SmartModeBox.IsChecked = kind == CollectionKind.Playlist && !string.IsNullOrWhiteSpace(playlist?.SmartRule);

        _ready = true;
        UpdateSmartMode();
        RefreshSelection();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(400, workArea.Width - 40);
        var maxHeight = Math.Max(360, workArea.Height - 48);
        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        Width = Math.Min(Width, maxWidth);
        Height = Math.Min(Height, maxHeight);
        // CenterOwner uses the initial size before Loaded; shrinking a tall dialog can otherwise
        // leave its title bar above the screen even though the new height fits the work area.
        Left = Math.Clamp(double.IsFinite(Left) ? Left : workArea.Left, workArea.Left,
            Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(double.IsFinite(Top) ? Top : workArea.Top, workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - Height));
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private bool AvailableFilter(object candidate)
    {
        if (candidate is not TrackChoice choice || _selectedKeys.Contains(choice.PathKey))
            return false;
        var query = SearchBox.Text.Trim();
        return query.Length == 0
            || choice.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || choice.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (choice.Track?.Album.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || choice.Path.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshSelection()
    {
        _selectedKeys = _ordered.Select(item => item.PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _ordered.Count; index++)
            _ordered[index].Position = index + 1;
        _availableView.Refresh();
        SelectedCountText.Text = $"{_ordered.Count:N0} {(_ordered.Count == 1 ? "track" : "tracks")} selected";
        AddResultsButton.IsEnabled = _availableView.Cast<TrackChoice>().Any();
        AddSelectedButton.IsEnabled = AvailableList.SelectedItems.Count > 0;
        UpdateMoveButtons();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready)
            RefreshSelection();
    }

    private void AddSelected_Click(object sender, RoutedEventArgs e) =>
        AddTracks(AvailableList.SelectedItems.Cast<TrackChoice>().ToList());

    private void AddResults_Click(object sender, RoutedEventArgs e) =>
        AddTracks(_availableView.Cast<TrackChoice>().ToList());

    private void AddTracks(IEnumerable<TrackChoice> candidates)
    {
        TrackChoice? lastAdded = null;
        var known = _ordered.Select(choice => choice.PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _batching = true;
        try
        {
            foreach (var candidate in candidates)
            {
                if (!known.Add(candidate.PathKey))
                    continue;
                lastAdded = candidate.Copy();
                _ordered.Add(lastAdded);
            }
        }
        finally { _batching = false; }
        if (lastAdded is not null)
        {
            OrderedList.SelectedItem = lastAdded;
            OrderedList.ScrollIntoView(lastAdded);
        }
        RefreshSelection();
    }

    private void AvailableList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindItem(e.OriginalSource as DependencyObject, AvailableList) is TrackChoice)
        {
            AddTracks(AvailableList.SelectedItems.Cast<TrackChoice>().ToList());
            e.Handled = true;
        }
    }

    private void AvailableList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AvailableList.SelectedItems.Count > 0)
        {
            AddTracks(AvailableList.SelectedItems.Cast<TrackChoice>().ToList());
            e.Handled = true;
        }
    }

    private void AvailableList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            AddSelectedButton.IsEnabled = AvailableList.SelectedItems.Count > 0;
    }

    private void OrderedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            UpdateMoveButtons();
    }

    private void UpdateMoveButtons()
    {
        var index = OrderedList.SelectedIndex;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < _ordered.Count - 1;
        RemoveTrackButton.IsEnabled = index >= 0;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        var index = OrderedList.SelectedIndex;
        var destination = index + delta;
        if (index < 0 || destination < 0 || destination >= _ordered.Count)
            return;
        var selected = _ordered[index];
        _ordered.Move(index, destination);
        OrderedList.SelectedItem = selected;
        OrderedList.ScrollIntoView(selected);
        OrderedList.Focus();
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        var index = OrderedList.SelectedIndex;
        if (index < 0)
            return;
        _ordered.RemoveAt(index);
        if (_ordered.Count > 0)
            OrderedList.SelectedIndex = Math.Min(index, _ordered.Count - 1);
        OrderedList.Focus();
    }

    private void OrderedList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
        {
            MoveSelected(key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private void OrderedList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(OrderedList);
        _dragCandidate = FindItem(e.OriginalSource as DependencyObject, OrderedList);
    }

    private void OrderedList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null)
            return;
        var point = e.GetPosition(OrderedList);
        if (Math.Abs(point.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var candidate = _dragCandidate;
        _dragCandidate = null;
        if (_ordered.Contains(candidate))
            DragDrop.DoDragDrop(OrderedList, new DataObject(DragFormat, candidate), DragDropEffects.Move);
    }

    private void OrderedList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) && e.Data.GetData(DragFormat) is TrackChoice choice && _ordered.Contains(choice)
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OrderedList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat) || e.Data.GetData(DragFormat) is not TrackChoice moving)
            return;
        var oldIndex = _ordered.IndexOf(moving);
        if (oldIndex < 0)
            return;
        var target = FindItem(e.OriginalSource as DependencyObject, OrderedList);
        var insertionIndex = target is null ? _ordered.Count : _ordered.IndexOf(target);
        if (target is not null && OrderedList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem row
            && e.GetPosition(row).Y > row.ActualHeight / 2)
            insertionIndex++;
        if (oldIndex < insertionIndex)
            insertionIndex--;
        var newIndex = Math.Clamp(insertionIndex, 0, _ordered.Count - 1);
        if (oldIndex != newIndex)
            _ordered.Move(oldIndex, newIndex);
        OrderedList.SelectedItem = moving;
        OrderedList.ScrollIntoView(moving);
        e.Handled = true;
    }

    private static TrackChoice? FindItem(DependencyObject? element, ItemsControl owner)
    {
        while (element is not null && element != owner)
        {
            if (element is ScrollBar or Thumb)
                return null;
            if (element is ListBoxItem row && ItemsControl.ItemsControlFromItemContainer(row) == owner)
                return row.DataContext as TrackChoice;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private void ChooseArtwork_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose album artwork",
            Filter = "Artwork images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true)
            return;
        try
        {
            var image = DecodeArtwork(picker.FileName);
            _artworkPath = picker.FileName;
            _artworkChanged = true;
            CoverPreview.Source = image;
            ArtworkText.Text = Path.GetFileName(_artworkPath);
            ArtworkText.ToolTip = _artworkPath;
            ClearArtworkButton.IsEnabled = true;
            ErrorText.Text = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException
                                     or System.IO.FileFormatException or ArgumentException)
        {
            ErrorText.Text = "This image could not be opened. Choose a PNG, JPEG, BMP, GIF, or TIFF image.";
        }
    }

    private void ClearArtwork_Click(object sender, RoutedEventArgs e)
    {
        _artworkPath = null;
        _artworkChanged = true;
        UpdateArtworkPreview(false);
        ErrorText.Text = "";
    }

    private void UpdateArtworkPreview(bool showError)
    {
        CoverPreview.Source = null;
        ClearArtworkButton.IsEnabled = !string.IsNullOrWhiteSpace(_artworkPath);
        ArtworkText.ToolTip = _artworkPath;
        if (string.IsNullOrWhiteSpace(_artworkPath))
        {
            ArtworkText.Text = "No cover selected";
            return;
        }
        try
        {
            CoverPreview.Source = DecodeArtwork(_artworkPath);
            ArtworkText.Text = Path.GetFileName(_artworkPath);
        }
        catch
        {
            ArtworkText.Text = "Artwork unavailable · choose another image or clear it";
            if (showError)
                ErrorText.Text = "The selected artwork is unavailable.";
        }
    }

    private static BitmapSource DecodeArtwork(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 256;
        bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void SmartMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready)
            UpdateSmartMode();
    }

    private void SmartRule_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            UpdateSmartRuleDescription();
    }

    private void UpdateSmartMode()
    {
        var smart = _kind == CollectionKind.Playlist && SmartModeBox.IsChecked == true;
        SmartRulePanel.Visibility = smart ? Visibility.Visible : Visibility.Collapsed;
        TrackPickerPanel.Visibility = smart ? Visibility.Collapsed : Visibility.Visible;
        UpdateSmartRuleDescription();
    }

    private void UpdateSmartRuleDescription() =>
        SmartRuleDescription.Text = (SmartRuleBox.SelectedItem as SmartRuleChoice)?.Description
            ?? "Choose a supported rule to keep this playlist up to date.";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            ErrorText.Text = _kind == CollectionKind.Album ? "Give this album a title." : "Give this playlist a name.";
            TitleBox.Focus();
            return;
        }

        var smart = _kind == CollectionKind.Playlist && SmartModeBox.IsChecked == true;
        if (!smart && _ordered.Count == 0)
        {
            ErrorText.Text = "Add at least one track to this collection.";
            SearchBox.Focus();
            return;
        }
        if (smart && SmartRuleBox.SelectedItem is not SmartRuleChoice)
        {
            ErrorText.Text = "Choose a smart playlist rule.";
            SmartRuleBox.Focus();
            return;
        }
        if (_kind == CollectionKind.Album && _artworkChanged && !string.IsNullOrWhiteSpace(_artworkPath)
            && !File.Exists(_artworkPath))
        {
            ErrorText.Text = "The chosen artwork is no longer available. Choose another image or clear it.";
            return;
        }

        if (_kind == CollectionKind.Album)
        {
            AlbumResult = new CustomAlbum
            {
                Id = _id,
                Title = title,
                Artist = string.IsNullOrWhiteSpace(ArtistBox.Text) ? "Various artists" : ArtistBox.Text.Trim(),
                Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(),
                ArtworkPath = _artworkPath,
                TrackPaths = _ordered.Select(choice => choice.Path).ToList()
            };
        }
        else
        {
            PlaylistResult = new CustomPlaylist
            {
                Id = _id,
                Name = title,
                SmartRule = smart ? (SmartRuleBox.SelectedItem as SmartRuleChoice)?.Key : null,
                TrackPaths = _ordered.Select(choice => choice.Path).ToList()
            };
        }
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F && TrackPickerPanel.Visibility == Visibility.Visible)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private sealed record SmartRuleChoice(string Key, string Label, string Description);

    private sealed class TrackChoice : INotifyPropertyChanged
    {
        private int _position;
        public TrackChoice(string path, Track? track)
        {
            Path = path;
            PathKey = NormalizePath(path);
            Track = track;
        }
        public string Path { get; }
        public string PathKey { get; }
        public Track? Track { get; }
        public string Title => Track?.Title ?? System.IO.Path.GetFileNameWithoutExtension(Path);
        public string Artist => Track?.Artist ?? "Unknown artist";
        public string Subtitle => Artist + (Track is null || Track.IsMissing ? " · Missing file" : "");
        public string AccessibleName => (_position > 0 ? $"Track {_position}, " : "") + Title + ", " + Subtitle;
        public int Position
        {
            get => _position;
            set
            {
                if (_position == value)
                    return;
                _position = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
        public string PositionText => _position.ToString();
        public TrackChoice Copy() => new(Path, Track);
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
