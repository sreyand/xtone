using System.Windows.Input;
using Microsoft.Win32;
using XTONE.Dialogs;
using XTONE.Models;
using XTONE.Services;

namespace XTONE;

public partial class MainWindow
{
    private readonly SemaphoreSlim _libraryWriteGate = new(1, 1);
    private readonly HashSet<Task> _pendingLibraryWrites = [];
    private readonly Dictionary<string, long> _favoriteRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _pendingFavorites = new(StringComparer.OrdinalIgnoreCase);
    private async Task<bool> ReloadSavedLibraryAsync()
    {
        if (_isDesignPreview || _closed) return false;
        try
        {
            var snapshot = await Task.Run(_store.Load);
            if (_closed) return false;
            ApplySnapshot(snapshot);
            return true;
        }
        catch (Exception error)
        {
            if (!_closed) SetStatus($"Couldn't reload your library: {error.Message}");
            return false;
        }
    }
    private Task<bool> PersistLibraryChangeAsync(Action change, string description)
    {
        // Register the complete operation, including time waiting for the write
        // gate, so an async close can drain every accepted user action.
        var write = PersistLibraryChangeCoreAsync(change, description);
        _pendingLibraryWrites.Add(write);
        _ = ForgetCompletedLibraryWriteAsync(write);
        return write;
    }
    private async Task ForgetCompletedLibraryWriteAsync(Task write)
    {
        try { await write; }
        catch { /* The core reports write errors; draining still observes the task. */ }
        finally { _pendingLibraryWrites.Remove(write); }
    }
    private async Task DrainLibraryWritesAsync()
    {
        while (_pendingLibraryWrites.Count > 0)
        {
            await Task.WhenAll(_pendingLibraryWrites.ToArray());
            _pendingLibraryWrites.RemoveWhere(write => write.IsCompleted);
        }
    }
    private async Task<bool> PersistLibraryChangeCoreAsync(Action change, string description)
    {
        await _libraryWriteGate.WaitAsync();
        try
        {
            await Task.Run(change);
            return true;
        }
        catch (Exception error)
        {
            if (!_closed) SetStatus($"Couldn't {description}: {error.Message}");
            return false;
        }
        finally { _libraryWriteGate.Release(); }
    }
    private void CreateCollection_Click(object sender, RoutedEventArgs e)
    { if (_section == "Playlists") CreatePlaylist_Click(sender, e); else CreateAlbum_Click(sender, e); }
    private async void CreateAlbum_Click(object sender, RoutedEventArgs e) => await EditAlbumAsync(null);
    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e) => await EditPlaylistAsync(null);
    private async Task EditAlbumAsync(AlbumCard? album)
    {
        if (_isDesignPreview) return;
        CustomAlbum? draft = album is null ? null : _customAlbums.FirstOrDefault(item => item.Id == album.Id)
            ?? new CustomAlbum { Title = album.Title, Artist = album.Artist, Notes = album.Notes, ArtworkPath = album.ArtworkPath, TrackPaths = album.Tracks.Select(track => track.Path).ToList() };
        var dialog = new CollectionEditorWindow(_tracks, CollectionKind.Album, album: draft) { Owner = this };
        if (ShowAnimatedDialog(dialog) != true || dialog.AlbumResult is not { } result) return;
        if (!await PersistLibraryChangeAsync(() => _store.SaveAlbum(result), "save this album")
            || !await ReloadSavedLibraryAsync()) return;
        ShowSection("Albums");
        if (_albumCards.FirstOrDefault(card => card.Id == result.Id) is { } saved) OpenAlbum(saved);
    }
    private async Task EditPlaylistAsync(CustomPlaylist? playlist)
    {
        if (_isDesignPreview) return;
        var dialog = new CollectionEditorWindow(_tracks, CollectionKind.Playlist, playlist: playlist) { Owner = this };
        if (ShowAnimatedDialog(dialog) != true || dialog.PlaylistResult is not { } result) return;
        if (!await PersistLibraryChangeAsync(() => _store.SavePlaylist(result), "save this playlist")
            || !await ReloadSavedLibraryAsync()) return;
        ShowSection("Playlists");
        _activePlaylist = result;
        OpenAlbum(new AlbumCard { Id = result.Id, Title = result.Name, Artist = result.SmartRule is null ? "Private playlist" : "Smart playlist", Tracks = ResolvePlaylist(result) });
    }
    private async void EditCollection_Click(object sender, RoutedEventArgs e)
    { if (_activePlaylist is { } playlist) await EditPlaylistAsync(playlist); else await EditAlbumAsync(_activeAlbum); }
    private async void AlbumEdit_Click(object sender, RoutedEventArgs e)
    { if ((sender as MenuItem)?.Tag is AlbumCard album) await EditAlbumAsync(album); }
    private async void DeleteCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAlbum is not { } album || _isDesignPreview) return;
        if (MessageBox.Show(this, $"Delete ‘{album.Title}’ from your collections? Your audio files will stay in their folders.", "Delete collection", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var saved = _activePlaylist is { } playlist
            ? await PersistLibraryChangeAsync(() => _store.DeletePlaylist(playlist.Id), "delete this playlist")
            : album.IsCreatedLocally && await PersistLibraryChangeAsync(() => _store.DeleteAlbum(album.Id), "delete this album");
        if (!saved || _closed) return;
        ShowSection(_section); await ReloadSavedLibraryAsync();
    }
    private void PlayCollection_Click(object sender, RoutedEventArgs e)
    { if (_activeAlbum?.Tracks.FirstOrDefault(track => track.CanPlay) is { } track) PlayTrack(track, _activeAlbum.Tracks); }
    private void AlbumPlay_Click(object sender, RoutedEventArgs e)
    { if ((sender as MenuItem)?.Tag is AlbumCard album && album.Tracks.FirstOrDefault(track => track.CanPlay) is { } track) PlayTrack(track, album.Tracks); }
    private void AlbumQueue_Click(object sender, RoutedEventArgs e)
    { if ((sender as MenuItem)?.Tag is AlbumCard album) { foreach (var track in album.Tracks.Where(track => track.CanPlay)) _queue.Append(track); QueueChanged(); ShowNotice($"Added {album.Title} to queue"); } }
    private Track? ActionTrack(object sender) => (sender as MenuItem)?.Tag as Track ?? (_activeAlbum is null ? LibraryTracks.SelectedItem : DetailTracks.SelectedItem) as Track;
    private void TrackPlay_Click(object sender, RoutedEventArgs e)
    { if (ActionTrack(sender) is { } track) PlayTrack(track, _activeAlbum?.Tracks ?? LibraryTracks.Items.OfType<Track>().ToList()); }
    private void TrackNext_Click(object sender, RoutedEventArgs e)
    { if (ActionTrack(sender) is { CanPlay: true } track) { _queue.InsertNext(track); QueueChanged(); ShowNotice($"Playing next: {track.Title}"); } }
    private void TrackQueue_Click(object sender, RoutedEventArgs e)
    { if (ActionTrack(sender) is { CanPlay: true } track) { _queue.Append(track); QueueChanged(); ShowNotice($"Added to queue: {track.Title}"); } }
    private void TrackFavorite_Click(object sender, RoutedEventArgs e) { if (ActionTrack(sender) is { } track) ToggleFavorite(track); }
    private async void ToggleFavorite(Track track)
    {
        if (_isDesignPreview || _closed) return;
        var path = track.Path;
        var favorite = !(_pendingFavorites.TryGetValue(path, out var pending) ? pending
            : _tracks.FirstOrDefault(item => SamePath(item.Path, path))?.IsFavorite ?? track.IsFavorite);
        var version = _favoriteRequests.GetValueOrDefault(path) + 1;
        _favoriteRequests[path] = version;
        _pendingFavorites[path] = favorite;
        SetFavoriteInView(path, favorite);
        var saved = await PersistLibraryChangeAsync(() => _store.SetFavorite(path, favorite), "save this favorite");
        if (_closed || _favoriteRequests.GetValueOrDefault(path) != version) return;
        if (!saved)
        {
            try
            {
                var snapshot = await Task.Run(_store.Load);
                if (_closed || _favoriteRequests.GetValueOrDefault(path) != version) return;
                SetFavoriteInView(path, snapshot.Tracks.FirstOrDefault(item => SamePath(item.Path, path))?.IsFavorite ?? false);
            }
            catch (Exception error) { if (!_closed) SetStatus($"Couldn't restore this favorite: {error.Message}"); }
        }
        if (_closed) return;
        _pendingFavorites.Remove(path);
        if (saved) SetFavoriteInView(path, favorite);
        RebuildCollections(); LibraryTracks.Items.Refresh(); DetailTracks.Items.Refresh();
        if (saved) ShowNotice(favorite ? $"Added {track.Title} to favorites" : $"Removed {track.Title} from favorites");
    }
    private void OverlayPendingFavorites()
    {
        foreach (var pending in _pendingFavorites)
            foreach (var track in _tracks.Where(track => SamePath(track.Path, pending.Key)))
                track.IsFavorite = pending.Value;
    }
    private void SetFavoriteInView(string path, bool favorite)
    {
        foreach (var match in _tracks.Where(item => SamePath(item.Path, path))) match.IsFavorite = favorite;
        foreach (var match in _queue.Entries.Where(entry => SamePath(entry.Track.Path, path))) match.Track.IsFavorite = favorite;
        if (_currentTrack is { } current && SamePath(current.Path, path)) current.IsFavorite = favorite;
        LibraryTracks.Items.Refresh(); DetailTracks.Items.Refresh();
    }
    private void TrackDetails_Click(object sender, RoutedEventArgs e) { if (ActionTrack(sender) is { } track) ShowAnimatedDialog(new FileDetailsWindow(track) { Owner = this }); }
    private void NowPlayingDetails_Click(object sender, RoutedEventArgs e) { if (_currentTrack is { } track) ShowAnimatedDialog(new FileDetailsWindow(track) { Owner = this }); }
    private async void TrackRelink_Click(object sender, RoutedEventArgs e)
    {
        if (_isDesignPreview || ActionTrack(sender) is not { } track) return;
        var picker = new OpenFileDialog { Title = $"Locate {track.Title}", Filter = "Audio files|*.flac;*.wav;*.mp3;*.m4a;*.mp4;*.alac|All files|*.*", FileName = Path.GetFileName(track.Path) };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (!await PersistLibraryChangeAsync(() => _store.RelinkTrack(track.Path, picker.FileName), "reconnect this file") || _closed) return;
            var relinksCurrent = _currentTrack is { } current && SamePath(current.Path, track.Path);
            var position = _player.Position;
            if (relinksCurrent) { _player.Stop(); UpdatePlayPauseButton(); }
            if (!await ReloadSavedLibraryAsync()) return;
            // A user may have chosen another track while the saved library reloaded.
            relinksCurrent = relinksCurrent && _currentTrack is { } stillCurrent && SamePath(stillCurrent.Path, track.Path)
                && _player.CurrentPath is null;
            // Rebuild queue references while keeping deliberate duplicates and position.
            var index = _queue.CurrentIndex;
            var paths = _queue.Entries.Select(entry => SamePath(entry.Track.Path, track.Path) ? picker.FileName : entry.Track.Path).ToList();
            var lookup = _tracks.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
            _queue.Replace(paths.Select(path => lookup.GetValueOrDefault(path) ?? new Track { Path = path, Title = Path.GetFileNameWithoutExtension(path), IsMissing = true }), index);
            if (relinksCurrent) await OpenCurrentTrackAsync(false, position, false);
            else if (_queue.Current?.Track is { } refreshed && _currentTrack is { } playing && SamePath(refreshed.Path, playing.Path))
            { _currentTrack = refreshed; UpdateNowPlaying(); }
            QueueChanged(); ShowNotice(relinksCurrent ? "File reconnected. Playback is paused at your previous position." : "File reconnected across your library and collections");
        }
        catch (Exception error) { SetStatus($"Couldn't reconnect this file: {error.Message}"); }
    }
    private void Queue_Click(object sender, RoutedEventArgs e)
    { if (_queue.Entries.Count > 0) _ = SetQueueVisibleAsync(!_queueRequested); }
    private void QueueChanged() { FlushPreparedAudio(); PrepareNextTrack(); RefreshInterfaceState(); SavePlaybackSession(false); }
    private void QueuePlay_Click(object sender, RoutedEventArgs e) { if (_queue.Select(QueueItems.SelectedIndex)) StartCurrentTrack(); }
    private void QueueUp_Click(object sender, RoutedEventArgs e) => MoveQueued(-1);
    private void QueueDown_Click(object sender, RoutedEventArgs e) => MoveQueued(1);
    private void MoveQueued(int direction)
    {
        var index = QueueItems.SelectedIndex; var target = index + direction;
        if (index < 0 || target < 0 || target >= _queue.Entries.Count) return;
        _queue.Move(index, target); QueueItems.SelectedIndex = target; QueueChanged();
    }
    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    { if (!_queue.Remove(QueueItems.SelectedIndex)) ShowNotice("Choose another queued track to remove. The current track stays in the queue."); else QueueChanged(); }
    private void QueueClear_Click(object sender, RoutedEventArgs e) { _queue.ClearUpcoming(); QueueChanged(); }
    private void Queue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) QueuePlay_Click(sender, e);
        else if (e.Key == Key.Delete) QueueRemove_Click(sender, e);
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key is Key.Up or Key.Down) MoveQueued(e.Key == Key.Up ? -1 : 1);
        else return;
        e.Handled = true;
    }
    private Point _queueDragOrigin;
    private QueueEntry? _queueDragEntry;
    private void Queue_MouseDown(object sender, MouseButtonEventArgs e)
    { _queueDragOrigin = e.GetPosition(QueueItems); _queueDragEntry = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as QueueEntry; }
    private void Queue_MouseMove(object sender, MouseEventArgs e)
    {
        if (_queueDragEntry is null || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(QueueItems);
        if (Math.Abs(position.X - _queueDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _queueDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var entry = _queueDragEntry; _queueDragEntry = null;
        DragDrop.DoDragDrop(QueueItems, new DataObject("XTONE.QueueEntry", entry), DragDropEffects.Move);
    }
    private void Queue_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("XTONE.QueueEntry") is not QueueEntry entry) return;
        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as QueueEntry;
        var from = _queue.Entries.IndexOf(entry); var to = target is null ? _queue.Entries.Count - 1 : _queue.Entries.IndexOf(target);
        _queue.Move(from, to); QueueItems.SelectedItem = entry; QueueChanged(); e.Handled = true;
    }
}
