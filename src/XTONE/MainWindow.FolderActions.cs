using Microsoft.Win32;
using XTONE.Dialogs;
using XTONE.Models;

namespace XTONE;

public partial class MainWindow
{
    private readonly Dictionary<string, string> _folderArtworkByTrack = new(StringComparer.Ordinal);
    private AlbumCard? FolderAction(object sender) => (sender as MenuItem)?.Tag as AlbumCard ?? _activeAlbum;

    private async void RenameMusicFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderAction(sender) is not { FolderPath: not null } folder || _fileOperationInProgress || _closed || _closingRequested || _isDesignPreview) return;
        var dialog = new MusicFolderNameWindow(false, folder.Title, rename: true) { Owner = this };
        if (ShowAnimatedDialog(dialog) == true) await RenameFolderAsync(folder, dialog.FolderName);
    }
    private async Task<bool> RenameFolderAsync(AlbumCard folder, string name)
    {
        if (folder.FolderPath is not { } oldPath || _musicRoot is null || _fileOperationInProgress || _closed || _closingRequested || _isDesignPreview) return false;
        var selectedRoot = _musicRoot;
        var wasViewing = _activeAlbum?.FolderPath is { } active && SamePath(active, oldPath);
        var succeeded = await RunMusicOperationAsync((progress, token) => _store.RenameMusicFolderAsync(oldPath, name, progress, token), "Renaming folder…", stopPlayback: true);
        if (succeeded && !_closed && !_closingRequested && selectedRoot == _musicRoot)
        {
            var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, name);
            if (wasViewing && _albumCards.FirstOrDefault(item => item.FolderPath is not null && SamePath(item.FolderPath, newPath)) is { } renamed) OpenAlbum(renamed);
            ShowNotice("Folder renamed.");
        }
        return succeeded;
    }
    private async void ChooseFolderArtwork_Click(object sender, RoutedEventArgs e)
    {
        if (FolderAction(sender) is not { FolderPath: not null } folder || _fileOperationInProgress || _closed || _closingRequested || _isDesignPreview) return;
        var picker = new OpenFileDialog { Title = $"Choose artwork for {folder.Title}", Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*", Multiselect = false };
        if (picker.ShowDialog(this) == true) await SetFolderArtworkAsync(folder, picker.FileName);
    }
    private async void ResetFolderArtwork_Click(object sender, RoutedEventArgs e)
    { if (FolderAction(sender) is { FolderPath: not null } folder) await SetFolderArtworkAsync(folder, null); }
    private async Task<bool> SetFolderArtworkAsync(AlbumCard folder, string? imagePath)
    {
        if (folder.FolderPath is not { } path || _musicRoot is null || _fileOperationInProgress || _closed || _closingRequested || _isDesignPreview) return false;
        var succeeded = await RunMusicOperationAsync((_, token) => _store.SetFolderArtworkAsync(path, imagePath, token), imagePath is null ? "Resetting artwork…" : "Updating artwork…");
        if (succeeded && !_closed && !_closingRequested) ShowNotice(imagePath is null ? "Artwork reset." : "Artwork updated.");
        return succeeded;
    }
    private void RebuildFolderArtwork()
    {
        _folderArtworkByTrack.Clear();
        foreach (var folder in _albumCards)
            if (folder.ArtworkPath is { } artwork)
                foreach (var track in folder.Tracks) _folderArtworkByTrack[track.Id] = artwork;
        if (_currentTrack is { } current)
        {
            NowPlayingArtwork.SourcePath = ArtworkForTrack(current);
            RefreshListeningState();
        }
    }
    private string? ArtworkForTrack(Track track) => _folderArtworkByTrack.GetValueOrDefault(track.Id) ?? track.ArtworkPath;
}
