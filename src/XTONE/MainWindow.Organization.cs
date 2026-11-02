using Microsoft.Win32;
using XTONE.Dialogs;
using XTONE.Models;
using XTONE.Services;

namespace XTONE;

public partial class MainWindow
{
    private bool _fileOperationInProgress;

    private async void CreateMusicFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_musicRoot is null) { ChooseMusicFolder_Click(sender, e); return; }
        if (_closed || _closingRequested || _fileOperationInProgress || _isDesignPreview) return;
        var root = _musicRoot;
        var artistLayout = _albumLayout == FolderLibrary.ArtistAlbumFolders;
        var dialog = new MusicFolderNameWindow(artistLayout) { Owner = this };
        if (ShowAnimatedDialog(dialog) != true) return;
        string? created = null;
        var succeeded = await RunMusicOperationAsync(async (progress, token) =>
        {
            created = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                MusicFileOrganizer.ValidateFolderName(dialog.FolderName);
                if (artistLayout) MusicFileOrganizer.ValidateFolderName(dialog.ArtistFolderName);
                string? parent = null;
                if (artistLayout)
                {
                    // Validate through the same folder-name policy even for an existing parent.
                    parent = MusicFileOrganizer.ValidateFolderPath(root, dialog.ArtistFolderName, root);
                    if (!Directory.Exists(parent)) parent = _store.CreateMusicFolder(dialog.ArtistFolderName);
                }
                return _store.CreateMusicFolder(dialog.FolderName, parent);
            }, token);
            return await _store.RefreshAsync(progress, token);
        }, "Creating folder…");
        if (succeeded && created is not null && !_closed && !_closingRequested)
        {
            ShowSection("Albums");
            if (_albumCards.FirstOrDefault(folder => folder.FolderPath is not null && SamePath(folder.FolderPath, created)) is { } folder) OpenAlbum(folder);
        }
    }

    private async void AddTracksToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAlbum?.FolderPath is not { } destination || _fileOperationInProgress || _closed || _closingRequested || _isDesignPreview) return;
        var picker = new OpenFileDialog { Title = $"Copy tracks into {_activeAlbum.Title}", Filter = "Audio files|*.flac;*.wav;*.mp3;*.m4a;*.mp4", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        await RunMusicOperationAsync((progress, token) => _store.CopyFilesToFolderAsync(picker.FileNames, destination, progress, token), "Adding tracks…");
    }

    private async void MoveTracksToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_musicRoot is null || _fileOperationInProgress || _closed || _closingRequested || _isDesignPreview) return;
        var list = _activeAlbum is null ? LibraryTracks : DetailTracks;
        var tracks = _section == "Albums" && _activeAlbum is null
            ? MusicList.SelectedItems.OfType<MusicBrowserItem>().Where(item => item.Track is not null).Select(item => item.Track!).ToList()
            : list.SelectedItems.OfType<Track>().ToList();
        if (_section == "Albums" && _activeAlbum is null && (sender as MenuItem)?.Tag is Track explicitTrack && !tracks.Any(track => track.Id == explicitTrack.Id))
            tracks = [explicitTrack];
        if (tracks.Count == 0 && ActionTrack(sender) is { } selected) tracks.Add(selected);
        if (tracks.Count == 0) return;
        var dialog = new MoveTracksWindow(_musicRoot, _albumCards, tracks.Count) { Owner = this };
        if (ShowAnimatedDialog(dialog) != true || dialog.Destination is not { } destination) return;
        await RunMusicOperationAsync((progress, token) => _store.MoveTracksToFolderAsync(tracks.Select(track => track.Path), destination, progress, token), "Organizing tracks…", stopPlayback: true);
    }

    private Task<bool> RunMusicOperationAsync(Func<IProgress<LibraryProgress>, CancellationToken, Task<LibrarySnapshot>> operation, string message, bool stopPlayback = false)
    {
        var task = RunMusicOperationCoreAsync(operation, message, stopPlayback);
        _pendingLibraryWrites.Add(task);
        _ = ForgetCompletedLibraryWriteAsync(task);
        return task;
    }

    private async Task<bool> RunMusicOperationCoreAsync(Func<IProgress<LibraryProgress>, CancellationToken, Task<LibrarySnapshot>> operation, string message, bool stopPlayback)
    {
        await _scanGate.WaitAsync();
        if (_closed || _closingRequested) { _scanGate.Release(); return false; }
        SavePlaybackSession(false);
        _fileOperationInProgress = true;
        var position = PlaybackPosition;
        var playing = _player.IsPlaying;
        var entryId = _queue.Current?.Id;
        var succeeded = false;
        _scanCancellation = new CancellationTokenSource();
        var cancellation = _scanCancellation;
        _showScanProgress = true;
        IsEnabled = false;
        if (TaskbarItemInfo is { } taskbar) foreach (var button in taskbar.ThumbButtonInfos) button.IsEnabled = false;
        var operationWindow = new MusicOperationWindow(message, () => cancellation.Cancel()) { Owner = this };
        operationWindow.Show();
        SetStatus(message, false);
        ScanProgress.IsIndeterminate = true;
        var progress = new Progress<LibraryProgress>(value =>
        {
            if (_closed || !ReferenceEquals(_scanCancellation, cancellation)) return;
            ScanStatus.Text = value.Message; operationWindow.Report(value.Message); RefreshInterfaceState();
        });
        try
        {
            // Invalidate rendered-boundary callbacks before yielding to the pending save.
            // The captured entry and position must describe the decoder we resume later.
            if (stopPlayback) { StopCurrentPlayback(); UpdatePlayPauseButton(); }
            await _sessionSaveTask;
            if (stopPlayback) { await _player.StopAsync(cancellation.Token); ResetHandledBoundary(); }
            var snapshot = await operation(progress, cancellation.Token);
            if (_closed || _closingRequested) return false;
            await ApplySnapshotAsync(snapshot);
            var byId = _tracks.ToDictionary(track => track.Id, StringComparer.Ordinal);
            _queue.RefreshTracks(track => byId.GetValueOrDefault(track.Id) ?? track);
            if (_queue.Current is { } current) _currentTrack = current.Track;
            UpdateNowPlaying();
            SetStatus("", false); _scanOwnedNotice = null;
            succeeded = true;
        }
        catch (OperationCanceledException) { if (!_closed) SetStatus("Operation cancelled. Existing tracks were kept intact."); }
        catch (Exception error) { if (!_closed) SetStatus(error.Message); }
        finally
        {
            operationWindow.Complete();
            cancellation.Dispose(); if (ReferenceEquals(_scanCancellation, cancellation)) _scanCancellation = null;
            _fileOperationInProgress = false; _showScanProgress = false;
            _scanGate.Release();
            if (!_closed && !_closingRequested)
            {
                IsEnabled = true;
                if (TaskbarItemInfo is { } restoredTaskbar) foreach (var button in restoredTaskbar.ThumbButtonInfos) button.IsEnabled = true;
                RefreshInterfaceState();
                if (stopPlayback && entryId == _queue.Current?.Id && _queue.Current?.Track.CanPlay == true)
                    await OpenCurrentTrackAsync(playing, position, false);
                SavePlaybackSession(false);
                if (succeeded) ShowNotice(stopPlayback ? "Tracks moved." : "Music updated.");
                if (_refreshPending) { _refreshPending = false; _ = ScanAsync(null, true); }
            }
        }
        return succeeded;
    }
}

