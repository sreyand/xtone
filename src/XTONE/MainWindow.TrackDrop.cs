using System.Windows.Input;
using System.Windows.Media;
using XTONE.Models;
using XTONE.Services;

namespace XTONE;

public partial class MainWindow
{
    private const string TrackMoveFormat = "XTONE.MusicTracks";
    private const string TrackMoveRootFormat = "XTONE.MusicTracks.Root";
    private const string TrackMoveTokenFormat = "XTONE.MusicTracks.Token";
    private readonly string _trackMoveToken = Guid.NewGuid().ToString("N");
    private Point _trackDragOrigin;
    private string[] _trackDragPaths = [];
    private readonly HashSet<DependencyObject> _trackDropHighlights = [];

    public static readonly DependencyProperty IsMusicDropTargetProperty = DependencyProperty.RegisterAttached(
        "IsMusicDropTarget", typeof(bool), typeof(MainWindow), new FrameworkPropertyMetadata(false));
    public static bool GetIsMusicDropTarget(DependencyObject target) => (bool)target.GetValue(IsMusicDropTargetProperty);
    public static void SetIsMusicDropTarget(DependencyObject target, bool value) => target.SetValue(IsMusicDropTargetProperty, value);

    private DataObject? CreateTrackDragData(IEnumerable<Track> tracks)
    {
        if (_musicRoot is null || _fileOperationInProgress || _closed || _closingRequested || _isDesignPreview) return null;
        var known = _tracks.ToDictionary(track => track.Path, StringComparer.OrdinalIgnoreCase);
        var paths = tracks.Select(track => track.Path).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => known.TryGetValue(path, out var track) && !track.IsMissing && FolderLibrary.IsWithinRoot(path, _musicRoot)).ToArray();
        if (paths.Length == 0) return null;
        var data = new DataObject(); data.SetData(TrackMoveFormat, paths);
        data.SetData(TrackMoveRootFormat, _musicRoot); data.SetData(TrackMoveTokenFormat, _trackMoveToken);
        return data;
    }

    private void BeginMusicTrackDrag(Button button, MouseButtonEventArgs e)
    {
        _trackDragOrigin = e.GetPosition(this);
        _trackDragPaths = button.Tag is MusicBrowserItem { Track: { } track } ? [track.Path] : [];
    }
    private bool TryStartMusicTrackDrag(Button button, MouseEventArgs e)
    {
        if (button.Tag is not MusicBrowserItem { Track: not null }) return false;
        return TryStartTrackDrag(button, e);
    }
    private void TrackSource_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _trackDragOrigin = e.GetPosition(this); _trackDragPaths = [];
        if (sender is not ListBox list || FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not Track track) return;
        _trackDragPaths = list.SelectedItems.OfType<Track>().Any(item => SamePath(item.Path, track.Path))
            ? list.SelectedItems.OfType<Track>().Select(item => item.Path).ToArray() : [track.Path];
    }
    private void TrackSource_MouseMove(object sender, MouseEventArgs e)
    { if (sender is FrameworkElement source) TryStartTrackDrag(source, e); }
    private bool TryStartTrackDrag(FrameworkElement source, MouseEventArgs e)
    {
        if (_trackDragPaths.Length == 0 || e.LeftButton != MouseButtonState.Pressed || _fileOperationInProgress) return false;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _trackDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _trackDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
        var paths = _trackDragPaths; _trackDragPaths = [];
        var tracks = _tracks.Where(track => paths.Contains(track.Path, StringComparer.OrdinalIgnoreCase));
        if (CreateTrackDragData(tracks) is not { } data) return false;
        e.Handled = true; source.ReleaseMouseCapture();
        try { DragDrop.DoDragDrop(source, data, DragDropEffects.Move); }
        finally { ClearTrackDropHighlights(); }
        return true;
    }

    private static AlbumCard? FolderFromDropElement(object? sender) => (sender as FrameworkElement)?.Tag switch
    {
        MusicBrowserItem { Folder: { } folder } => folder,
        AlbumCard folder => folder,
        _ => null
    };
    private AlbumCard? FindFolderDropTarget(DependencyObject? source)
    {
        while (source is not null)
        {
            if (FolderFromDropElement(source) is { FolderPath: not null } folder) return folder;
            source = source is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(source);
        }
        return null;
    }
    private bool TryReadTrackDrop(IDataObject data, out string[] paths, out bool copy)
    {
        paths = []; copy = false;
        if (_musicRoot is null || _closed || _closingRequested || _isDesignPreview || _fileOperationInProgress) return false;
        try
        {
            if (data.GetDataPresent(TrackMoveFormat))
            {
                if (data.GetData(TrackMoveTokenFormat) as string != _trackMoveToken
                    || !string.Equals(data.GetData(TrackMoveRootFormat) as string, _musicRoot, StringComparison.OrdinalIgnoreCase)
                    || data.GetData(TrackMoveFormat) is not string[] moved || moved.Length == 0) return false;
                paths = moved.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var known = _tracks.Where(track => !track.IsMissing).Select(track => track.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return paths.All(path => known.Contains(path) && FolderLibrary.IsWithinRoot(path, _musicRoot));
            }
            if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return false;
            paths = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            copy = true;
            return paths.All(LibraryStore.HasSupportedExtension);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or System.Runtime.InteropServices.COMException) { return false; }
    }
    private DragDropEffects GetFolderDropEffect(IDataObject data, AlbumCard? folder)
    {
        if (folder?.FolderPath is not { } path || _musicRoot is null || !FolderLibrary.IsWithinRoot(path, _musicRoot)
            || !_albumCards.Any(item => string.Equals(item.FolderPath, path, StringComparison.OrdinalIgnoreCase))
               && !_pinnedFolderPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            || !TryReadTrackDrop(data, out var paths, out var copy)) return DragDropEffects.None;
        if (paths.All(source => string.Equals(Path.GetDirectoryName(source), path, StringComparison.OrdinalIgnoreCase))) return DragDropEffects.None;
        return copy ? DragDropEffects.Copy : DragDropEffects.Move;
    }
    private async Task DropTracksIntoFolderAsync(IDataObject data, AlbumCard folder)
    {
        if (GetFolderDropEffect(data, folder) == DragDropEffects.None || !TryReadTrackDrop(data, out var paths, out var copy)) return;
        var root = _musicRoot; var destination = folder.FolderPath!;
        await RunMusicOperationAsync((progress, token) =>
        {
            if (!string.Equals(_musicRoot, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Music folder changed. Drop the tracks again in the current collection.");
            return copy ? _store.CopyFilesToFolderAsync(paths, destination, progress, token)
                : _store.MoveTracksToFolderAsync(paths, destination, progress, token);
        }, copy ? "Adding tracks…" : "Organizing tracks…", stopPlayback: !copy);
    }

    private void FolderTrack_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TrackMoveFormat) && !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true; MusicDropOverlay.Visibility = Visibility.Collapsed;
        var effect = GetFolderDropEffect(e.Data, FolderFromDropElement(sender)); e.Effects = effect;
        if (sender is DependencyObject target)
        {
            SetIsMusicDropTarget(target, effect != DragDropEffects.None);
            if (effect != DragDropEffects.None) _trackDropHighlights.Add(target); else _trackDropHighlights.Remove(target);
        }
    }
    private void FolderTrack_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement target) return;
        SetIsMusicDropTarget(target, false); _trackDropHighlights.Remove(target);
    }
    private async void FolderTrack_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TrackMoveFormat) && !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true; MusicDropOverlay.Visibility = Visibility.Collapsed; ClearTrackDropHighlights();
        if (FolderFromDropElement(sender) is { } folder) await DropTracksIntoFolderAsync(e.Data, folder);
    }
    private void ClearTrackDropHighlights()
    {
        foreach (var target in _trackDropHighlights) SetIsMusicDropTarget(target, false);
        _trackDropHighlights.Clear();
    }
}
