using System.Collections.ObjectModel;
using System.Windows.Input;
using XTONE.Models;
using XTONE.Services;

namespace XTONE;

public partial class MainWindow
{
    private const string FolderPinFormat = "XTONE.MusicFolder";
    private readonly List<string> _pinnedFolderPaths = [];
    private readonly ObservableCollection<AlbumCard> _pinnedFolderCards = [];
    private Point _folderDragOrigin;
    private AlbumCard? _folderDragCandidate;

    private void RefreshPinnedFolders()
    {
        PinnedFolderItems.ItemsSource = _pinnedFolderCards;
        _pinnedFolderCards.Clear();
        if (_musicRoot is not null)
        {
            foreach (var path in _pinnedFolderPaths.Where(path => FolderLibrary.IsWithinRoot(path, _musicRoot)))
            {
                var folder = _albumCards.FirstOrDefault(item => item.FolderPath is not null && SamePath(item.FolderPath, path))
                    ?? new AlbumCard { Id = "folder:" + path.ToUpperInvariant(), FolderPath = path, Title = Path.GetFileName(path), Artist = "", Tracks = _tracks.Where(track => FolderLibrary.IsWithinRoot(track.Path, path)).ToList() };
                _pinnedFolderCards.Add(folder);
            }
        }
        PinnedFoldersPanel.Visibility = _pinnedFolderCards.Count > 0 || SidebarPinHint.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task PinFolderAsync(AlbumCard folder, bool pinned)
    {
        if (_musicRoot is null || folder.FolderPath is not { } path || _isDesignPreview || _closed || _closingRequested || _fileOperationInProgress) return;
        if (await PersistLibraryChangeAsync(() => _store.SetFolderPinned(path, pinned), pinned ? "pin this folder" : "unpin this folder"))
            await ReloadSavedLibraryAsync();
    }
    private async void PinFolder_Click(object sender, RoutedEventArgs e)
    { if ((sender as MenuItem)?.Tag is AlbumCard folder) await PinFolderAsync(folder, true); }
    private async void UnpinFolder_Click(object sender, RoutedEventArgs e)
    { if ((sender as MenuItem)?.Tag is AlbumCard folder) await PinFolderAsync(folder, false); }
    private async void PinnedFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AlbumCard folder || _fileOperationInProgress) return;
        var selectedRoot = _musicRoot;
        var available = await Task.Run(() => Directory.Exists(folder.FolderPath));
        if (_closed || _closingRequested || selectedRoot != _musicRoot || !_pinnedFolderPaths.Contains(folder.FolderPath!, StringComparer.OrdinalIgnoreCase)) return;
        if (!available) { SetStatus("This folder is unavailable. Reconnect it or unpin its shortcut."); return; }
        ShowSection("Albums"); OpenAlbum(folder);
    }

    private void MusicFolder_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _folderDragOrigin = e.GetPosition(this);
        _folderDragCandidate = (sender as Button)?.Tag is MusicBrowserItem { Folder: { FolderPath: not null } folder } ? folder : null;
        if (sender is Button trackButton) BeginMusicTrackDrag(trackButton, e);
    }
    private void MusicFolder_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is Button trackButton && TryStartMusicTrackDrag(trackButton, e)) return;
        if (_folderDragCandidate?.FolderPath is not { } path || e.LeftButton != MouseButtonState.Pressed || sender is not Button button || _fileOperationInProgress) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _folderDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _folderDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _folderDragCandidate = null; e.Handled = true; button.ReleaseMouseCapture();
        try { DragDrop.DoDragDrop(button, new DataObject(FolderPinFormat, path), DragDropEffects.Link); }
        finally { HideSidebarPinHint(); }
    }
    private AlbumCard? DroppedFolder(IDataObject data)
    {
        if (!data.GetDataPresent(FolderPinFormat) || data.GetData(FolderPinFormat) is not string path) return null;
        return _albumCards.FirstOrDefault(folder => folder.FolderPath is not null && string.Equals(folder.FolderPath, path, StringComparison.OrdinalIgnoreCase));
    }
    private void SidebarFolder_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(FolderPinFormat)) return;
        e.Handled = true;
        var allowed = !_fileOperationInProgress && !_closingRequested && DroppedFolder(e.Data) is not null;
        e.Effects = allowed ? DragDropEffects.Link : DragDropEffects.None;
        SidebarPinHint.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        PinnedFoldersPanel.Visibility = allowed || _pinnedFolderCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SidebarFolder_DragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(MusicSidebarDropZone);
        if (point.X < 0 || point.Y < 0 || point.X >= MusicSidebarDropZone.ActualWidth || point.Y >= MusicSidebarDropZone.ActualHeight) HideSidebarPinHint();
    }
    private async void SidebarFolder_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(FolderPinFormat)) return;
        e.Handled = true; var folder = DroppedFolder(e.Data); HideSidebarPinHint();
        if (folder is not null) await PinFolderAsync(folder, true);
    }
    private void HideSidebarPinHint()
    {
        SidebarPinHint.Visibility = Visibility.Collapsed;
        PinnedFoldersPanel.Visibility = _pinnedFolderCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
