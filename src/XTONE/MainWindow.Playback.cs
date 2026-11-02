using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using XTONE.Dialogs;
using XTONE.Models;
using XTONE.Services;

namespace XTONE;

public partial class MainWindow
{
    private readonly PlaybackQueue _queue = new();
    private WindowsMediaSession? _mediaSession;
    private ThemeAccessibility? _accessibility;
    private Guid? _preparedId;
    private double _handledBoundaryStart;
    private long _handledPlaybackVersion;
    private long _openTrackRequest;
    private bool _isOpeningTrack;
    private string? _outputDeviceId;
    private bool _reducedMotion;
    private DateTime _lastSessionSave = DateTime.UtcNow;
    private DateTime _lastMediaUpdate;
    private Task _sessionSaveTask = Task.CompletedTask;
    private SessionWritePump? _sessionWritePump;
    private TimeSpan _retainedPosition;
    private Guid? _retainedPositionEntry;
    private ThumbButtonInfo? _taskbarPlay;

    private void InitializeWindowsControls()
    {
        _accessibility = new ThemeAccessibility(Application.Current);
        _accessibility.Changed += Accessibility_Changed;
        if (_isDesignPreview) return;
        _mediaSession = new WindowsMediaSession(this, MediaCommand, position => TrySeek(position));
        var taskbar = new TaskbarItemInfo();
        ThumbButtonInfo AddButton(string label, string geometry, RoutedEventHandler action)
        {
            var button = new ThumbButtonInfo { Description = label, ImageSource = new DrawingImage(new GeometryDrawing((Brush)FindResource("InkBrush"), null, (Geometry)FindResource(geometry))) };
            button.Click += (sender, _) => action(sender, new RoutedEventArgs()); taskbar.ThumbButtonInfos.Add(button); return button;
        }
        AddButton("Previous track", "PreviousIcon", Previous_Click);
        _taskbarPlay = AddButton("Play", "PlayIcon", PlayPause_Click);
        AddButton("Next track", "NextIcon", Next_Click);
        TaskbarItemInfo = taskbar;
    }
    private void Accessibility_Changed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _player.AnalysisEnabled = !_reducedMotion && ThemeAccessibility.AnimationsEnabled;
        ShowSelectedNavigation();
        UpdateListeningActivity();
    }
    private void ShowSelectedNavigation()
    {
        foreach (var navigation in new[] { LibraryNav, AlbumsNav, ArtistsNav, FavoritesNav, HistoryNav })
        {
            var selected = Equals(navigation.Tag, _section);
            navigation.Background = selected ? (Brush)FindResource("SelectionBrush") : Brushes.Transparent;
            navigation.Foreground = (Brush)FindResource(selected && ThemeAccessibility.HighContrast ? "SelectedTextBrush" : selected ? "BlueBrush" : "InkBrush");
        }
    }

    private async Task RestoreSessionAsync(PlaybackSession session)
    {
        if (_closed) return;
        VolumeSlider.Value = Math.Clamp(double.IsFinite(session.Volume) ? session.Volume : .72, 0, 1);
        _outputDeviceId = session.OutputDeviceId; _reducedMotion = session.ReducedMotion;
        _musicView = session.MusicView == "List" ? "List" : "Grid";
        UpdateListeningActivity();
        _syncingListeningState = true;
        _listeningState.MiniAlwaysOnTop = session.MiniAlwaysOnTop;
        _listeningState.Visualization = session.FocusVisualization;
        _syncingListeningState = false;
        _player.AnalysisEnabled = !_reducedMotion && ThemeAccessibility.AnimationsEnabled;
        try { _player.SetOutputDevice(_outputDeviceId); }
        catch (Exception error) { _outputDeviceId = null; SetStatus($"Using default output: {error.Message}"); }
        _queue.Shuffle = session.Shuffle;
        _queue.RepeatMode = Enum.TryParse<QueueRepeatMode>(session.RepeatMode, out var repeat) && Enum.IsDefined(repeat)
            ? repeat : session.Repeat ? QueueRepeatMode.One : QueueRepeatMode.Off;
        var lookup = _tracks.ToDictionary(track => track.Path, StringComparer.OrdinalIgnoreCase);
        _queue.Replace(session.QueuePaths.Select(path => lookup.GetValueOrDefault(path) ?? new Track { Path = path, Title = Path.GetFileNameWithoutExtension(path), IsMissing = true }), session.CurrentIndex);
        var restoredEntry = _queue.Current?.Id;
        var request = _openTrackRequest;
        if (_queue.Current is { Track.CanPlay: true } currentEntry)
        {
            var opening = OpenCurrentTrackAsync(false, TimeSpan.FromSeconds(Math.Clamp(double.IsFinite(session.PositionSeconds) ? session.PositionSeconds : 0, 0, Math.Max(0, currentEntry.Track.Duration.TotalSeconds))), false);
            request = _openTrackRequest;
            await opening;
        }
        else if (_queue.Current is { } current) { _currentTrack = current.Track; _retainedPositionEntry = current.Id; _retainedPosition = TimeSpan.FromSeconds(Math.Max(0, session.PositionSeconds)); UpdateNowPlaying(); }
        if (_closed || request != _openTrackRequest || restoredEntry != _queue.Current?.Id) return;
        UpdateQueueModes(); ShowSection(session.LastSection);
    }

    private PlaybackSession CaptureSession() => new()
    {
        QueuePaths = _queue.Entries.Select(entry => entry.Track.Path).ToList(), CurrentIndex = _queue.CurrentIndex,
        PositionSeconds = PlaybackPosition.TotalSeconds, Volume = VolumeSlider.Value, Shuffle = _queue.Shuffle,
        Repeat = _queue.RepeatMode == QueueRepeatMode.One, RepeatMode = _queue.RepeatMode.ToString(),
        OutputDeviceId = _outputDeviceId, ReducedMotion = _reducedMotion, LastSection = _section,
        MiniAlwaysOnTop = _listeningState.MiniAlwaysOnTop, FocusVisualization = _listeningState.Visualization, MusicView = _musicView
    };
    private void SavePlaybackSession(bool wait = true)
    {
        if (_isDesignPreview || !IsLoaded || !_libraryReady || _closed || _fileOperationInProgress) return;
        _sessionWritePump ??= new SessionWritePump(_store.SaveSession, error => OnUi(() => SetStatus($"Couldn't save session: {error.Message}")));
        if (!_isOpeningTrack && !ReconcileRenderedBoundary()) return;
        _sessionSaveTask = _sessionWritePump.Enqueue(CaptureSession());
        if (wait) _sessionSaveTask.GetAwaiter().GetResult();
    }
    private void PlayTrack(Track track, IReadOnlyList<Track> tracks)
    {
        if (_isDesignPreview || _closed || _closingRequested || _fileOperationInProgress) return;
        if (!track.CanPlay) { SetStatus("This track is unavailable. Use Locate file to reconnect it."); return; }
        var playable = tracks.Where(item => item.CanPlay).ToList();
        var index = playable.FindIndex(item => ReferenceEquals(item, track));
        if (index < 0) index = playable.FindIndex(item => SamePath(item.Path, track.Path));
        _queue.Replace(playable, index); StartCurrentTrack();
    }
    private async void StartCurrentTrack(bool playing = true, TimeSpan? position = null, bool record = true)
        => await OpenCurrentTrackAsync(playing, position, record);

    private async Task OpenCurrentTrackAsync(bool playing = true, TimeSpan? position = null, bool record = true)
    {
        if (_queue.Current is not { } entry || _closed || _closingRequested || _fileOperationInProgress) return;
        var request = ++_openTrackRequest;
        _isOpeningTrack = true;
        _currentTrack = entry.Track; _preparedId = null;
        _retainedPositionEntry = entry.Id; _retainedPosition = position ?? TimeSpan.Zero;
        try
        {
            // Stop flushes the previous output without reopening its decoder.
            _player.Stop(); ResetHandledBoundary(); UpdateNowPlaying(); UpdatePlayPauseButton();
            var opened = await _player.PlayAsync(entry.Track.Path, playing, position);
            if (!opened || _closed || _closingRequested || request != _openTrackRequest) return;
            if (_queue.Current?.Id != entry.Id) { StopCurrentPlayback(); return; }
            ResetHandledBoundary();
            _player.SetVolume((float)VolumeSlider.Value);
            UpdateNowPlaying(); PrepareNextTrack(); UpdatePlayPauseButton();
            if (record && playing) RecordListening(entry.Track);
            SavePlaybackSession(false);
        }
        catch (Exception error)
        {
            if (_closed || request != _openTrackRequest || _queue.Current?.Id != entry.Id) return;
            StopCurrentPlayback(); UpdateNowPlaying(); UpdatePlayPauseButton(); SetStatus($"Can't play {entry.Track.Title}: {error.Message}");
        }
        finally { if (request == _openTrackRequest) _isOpeningTrack = false; }
    }
    private TimeSpan PlaybackPosition => _player.CurrentPath is not null ? _player.Position
        : _queue.Current?.Id == _retainedPositionEntry ? _retainedPosition : TimeSpan.Zero;
    private void RememberPlaybackPosition()
    {
        if (_player.CurrentPath is { } path && _queue.Current is { } current && SamePath(current.Track.Path, path))
        { _retainedPosition = _player.Position; _retainedPositionEntry = _queue.Current?.Id; }
    }
    private TimeSpan DisplayDuration => _player.CurrentPath is null ? _currentTrack?.Duration ?? TimeSpan.Zero : _player.Duration;
    private void UpdateNowPlaying()
    {
        RefreshInterfaceState();
        if (_currentTrack is not { } track) return;
        NowPlayingTitle.Text = track.Title; NowPlayingArtist.Text = track.Artist;
        NowPlayingArtwork.Source = null; NowPlayingArtwork.SourcePath = ArtworkForTrack(track);
        QualityText.Text = track.PlayerQualitySummary; QualityText.ToolTip = track.DetailSummary;
        PositionSlider.Maximum = Math.Max(1, DisplayDuration.TotalSeconds);
        PositionSlider.Value = Math.Clamp(PlaybackPosition.TotalSeconds, 0, PositionSlider.Maximum);
        DurationText.Text = FormatTime(DisplayDuration); ElapsedText.Text = FormatTime(PlaybackPosition);
        if (_mediaSession is not null) _ = _mediaSession.SetTrackAsync(track);
        RefreshListeningState();
    }
    private void PrepareNextTrack()
    {
        if (_closed) return;
        var next = _queue.PeekNext();
        _preparedId = next >= 0 && _queue.Entries[next].Track.CanPlay ? _queue.Entries[next].Id : null;
        _player.PrepareNext(_preparedId is null ? null : _queue.Entries[next].Track.Path);
    }
    private void FlushPreparedAudio()
    {
        if (_closed || _isOpeningTrack || !ReconcileRenderedBoundary() || _player.CurrentPath is null) return;
        try { _player.Seek(_player.Position); ResetHandledBoundary(); }
        catch (Exception error)
        {
            StopCurrentPlayback(); UpdatePlayPauseButton();
            SetStatus($"Couldn't update playback queue: {error.Message}");
        }
    }
    private void ResetHandledBoundary()
    { _handledPlaybackVersion = _player.PlaybackVersion; _handledBoundaryStart = _player.CurrentBoundaryStart; }
    private bool ReconcileRenderedBoundary()
    {
        if (_closed) return false;
        if (_isOpeningTrack || _player.CurrentPath is not { } path) return true;
        // A transition may have reached the device before its dispatcher callback.
        // Reconcile the prior prepared entry before invalidating buffered audio.
        if (_player.PlaybackVersion != _handledPlaybackVersion || _player.CurrentBoundaryStart != _handledBoundaryStart)
        {
            var index = _queue.Entries.ToList().FindIndex(entry => entry.Id == _preparedId && SamePath(entry.Track.Path, path));
            if (index >= 0)
            {
                _queue.Select(index); _currentTrack = _queue.Current!.Track;
                ResetHandledBoundary(); _preparedId = null;
                RecordListening(_currentTrack); UpdateNowPlaying(); UpdatePlayPauseButton();
            }
            else
            {
                // The committed queued entry may have been removed, including a
                // deliberate duplicate of the same file. Do not adopt a sibling.
                StopCurrentPlayback(); UpdateNowPlaying(); UpdatePlayPauseButton(); return false;
            }
        }
        return true;
    }
    private void StopCurrentPlayback()
    {
        RememberPlaybackPosition();
        _openTrackRequest++; _isOpeningTrack = false; _preparedId = null;
        try { _player.Stop(); }
        catch (Exception error) { if (!_closed) SetStatus($"Couldn't stop the audio output: {error.Message}"); }
        ResetHandledBoundary();
    }
    private bool TrySeek(TimeSpan position)
    {
        if (_closed || _closingRequested || _fileOperationInProgress || _isDesignPreview || _isOpeningTrack || _player.CurrentPath is null || !ReconcileRenderedBoundary()) return false;
        try
        {
            _player.Seek(position); ResetHandledBoundary(); PrepareNextTrack();
            UpdateNowPlaying(); UpdatePlayPauseButton(); SavePlaybackSession(false); return true;
        }
        catch (Exception error)
        {
            StopCurrentPlayback(); UpdatePlayPauseButton();
            SetStatus($"Couldn't seek: {error.Message}"); return false;
        }
    }
    private void PauseCurrentPlayback()
    {
        if (_closed) return;
        if (_isOpeningTrack) { StopCurrentPlayback(); UpdatePlayPauseButton(); return; }
        if (!ReconcileRenderedBoundary()) return;
        try { _player.PausePlayback(); ResetHandledBoundary(); PrepareNextTrack(); UpdatePlayPauseButton(); SavePlaybackSession(false); }
        catch (Exception error) { StopCurrentPlayback(); UpdatePlayPauseButton(); SetStatus($"Couldn't pause: {error.Message}"); }
    }
    private void OnUi(Action action)
    {
        if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) { if (!_closed) action(); }
        else
        {
            try { Dispatcher.BeginInvoke(new Action(() => { if (!_closed) action(); })); }
            catch (InvalidOperationException) when (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { }
        }
    }
    private void Player_NextTrackStarted(object? sender, EventArgs e)
    {
        var transition = e as AudioTrackStartedEventArgs;
        var version = transition?.Version ?? _player.PlaybackVersion;
        var path = transition?.Path ?? _player.CurrentPath;
        var preparedId = _preparedId;
        OnUi(() =>
        {
            if (_closed || version != _player.PlaybackVersion) return;
            var boundaryStart = transition?.BoundaryStart ?? _player.CurrentBoundaryStart;
            if (_handledPlaybackVersion == version && _handledBoundaryStart == boundaryStart) return;
            var index = _queue.Entries.ToList().FindIndex(entry => entry.Id == preparedId);
            if (index < 0 || path is null || !SamePath(_queue.Entries[index].Track.Path, path)) return;
            _handledPlaybackVersion = version; _handledBoundaryStart = boundaryStart;
            _queue.Select(index); _currentTrack = _queue.Current!.Track;
            UpdateNowPlaying(); UpdatePlayPauseButton(); RecordListening(_currentTrack); PrepareNextTrack(); SavePlaybackSession(false);
        });
    }
    private void Player_PlaybackStopped(object? sender, EventArgs e)
    {
        var stopped = e as AudioPlaybackStoppedEventArgs;
        var version = stopped?.Version ?? _player.PlaybackVersion;
        var error = stopped?.Error ?? _player.LastError;
        OnUi(() =>
        {
            if (_closed || version != _player.PlaybackVersion) return;
            if (error is null && _player.Duration > TimeSpan.Zero && _player.Position >= _player.Duration - TimeSpan.FromMilliseconds(250)) AdvanceQueue(false);
            else { UpdatePlayPauseButton(); SetStatus(error ?? "Playback stopped. Check the output device or choose another track."); }
        });
    }
    private async void AdvanceQueue(bool manual = true) => await AdvanceQueueAsync(manual);
    private async Task AdvanceQueueAsync(bool manual = true)
    {
        if (_closed || _closingRequested || _fileOperationInProgress || !ReconcileRenderedBoundary()) return;
        StopCurrentPlayback();
        var request = _openTrackRequest;
        _isOpeningTrack = true;
        UpdatePlayPauseButton();
        var ignoreRepeatOne = manual;
        var limit = _queue.Entries.Count;
        try
        {
            for (var attempts = 0; attempts < limit; attempts++)
            {
                // Cached missing entries do not reach the asynchronous filesystem probe.
                // Yield in bounded batches so Pause/Close can interrupt a large skipped queue.
                if (attempts % 32 == 0)
                {
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    if (_closed || _closingRequested || _fileOperationInProgress || request != _openTrackRequest) return;
                }
                var next = _queue.PeekNext(ignoreRepeatOne);
                if (next < 0) { if (attempts > 0) SetStatus("No available tracks remain in this queue."); UpdateNowPlaying(); UpdatePlayPauseButton(); SavePlaybackSession(false); return; }
                _queue.Select(next);
                var entry = _queue.Current!;
                _currentTrack = entry.Track;
                // Disconnected/network paths must never block the dispatcher.
                var exists = entry.Track.CanPlay && await Task.Run(() => File.Exists(entry.Track.Path));
                if (_closed || _closingRequested || _fileOperationInProgress || request != _openTrackRequest || _queue.Current?.Id != entry.Id) return;
                if (exists) { await OpenCurrentTrackAsync(); return; }
                ignoreRepeatOne = true;
            }
            _currentTrack = _queue.Current?.Track; UpdateNowPlaying();
            SetStatus("No available tracks remain in this queue."); UpdatePlayPauseButton(); SavePlaybackSession(false);
        }
        finally
        {
            if (request == _openTrackRequest) { _isOpeningTrack = false; UpdatePlayPauseButton(); }
        }
    }
    private async void RecordListening(Track track)
    {
        if (!await PersistLibraryChangeAsync(() => _store.RecordListening(track.Path), "save listening history")) return;
        if (_closed) return;
        var history = _history.FirstOrDefault(entry => SamePath(entry.Path, track.Path));
        if (history is null) { history = new() { Path = track.Path }; _history.Add(history); }
        history.LastPlayedUtc = DateTime.UtcNow; history.PlayCount++;
        RefreshSmartViews();
    }
    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_fileOperationInProgress) return;
        if (_closed || _closingRequested) return;
        if (_isDesignPreview) { _previewPlaying = !_previewPlaying; UpdatePlayPauseButton(); return; }
        if (_isOpeningTrack) { PauseCurrentPlayback(); return; }
        if (!ReconcileRenderedBoundary()) return;
        if (_currentTrack is null)
        {
            if (_queue.Entries.Count > 0) { _queue.Select(0); StartCurrentTrack(); return; }
            var tracks = FilteredTracks().Where(track => track.CanPlay).ToList();
            if (tracks.Count > 0) PlayTrack(tracks[0], tracks); else ChooseMusicFolder_Click(sender, e);
            return;
        }
        if (_player.CurrentPath is null || (!_player.IsPlaying && (_player.LastError is not null || _player.Position >= _player.Duration)))
        { StartCurrentTrack(position: DisplayDuration > TimeSpan.Zero && PlaybackPosition >= DisplayDuration ? TimeSpan.Zero : PlaybackPosition); return; }
        if (_player.IsPlaying) { PauseCurrentPlayback(); return; }
        try { _player.Resume(); UpdatePlayPauseButton(); SavePlaybackSession(false); }
        catch (Exception error) { StopCurrentPlayback(); UpdatePlayPauseButton(); SetStatus($"Couldn't resume: {error.Message}"); }
    }
    private void Previous_Click(object sender, RoutedEventArgs e)
    { if (_closed || _closingRequested || _fileOperationInProgress || !ReconcileRenderedBoundary()) return; if (_player.Position.TotalSeconds > 3) TrySeek(TimeSpan.Zero); else if (_queue.Previous()) StartCurrentTrack(); }
    private void Next_Click(object sender, RoutedEventArgs e) => AdvanceQueue();
    private void Shuffle_Click(object sender, RoutedEventArgs e)
    { FlushPreparedAudio(); _queue.Shuffle = !_queue.Shuffle; _queue.InvalidateNext(); UpdateQueueModes(); PrepareNextTrack(); SavePlaybackSession(false); }
    private void Repeat_Click(object sender, RoutedEventArgs e)
    { FlushPreparedAudio(); _queue.RepeatMode = (QueueRepeatMode)(((int)_queue.RepeatMode + 1) % 3); _queue.InvalidateNext(); UpdateQueueModes(); PrepareNextTrack(); SavePlaybackSession(false); }
    private void UpdateQueueModes()
    {
        ShuffleButton.Foreground = (Brush)FindResource(_queue.Shuffle ? "BlueBrush" : "InkBrush");
        ShuffleButton.ToolTip = _queue.Shuffle ? "Shuffle on" : "Shuffle off";
        AutomationProperties.SetName(ShuffleButton, (string)ShuffleButton.ToolTip);
        RepeatButton.Foreground = (Brush)FindResource(_queue.RepeatMode == QueueRepeatMode.Off ? "InkBrush" : "BlueBrush");
        RepeatButton.ToolTip = $"Repeat: {_queue.RepeatMode}. Click to change.";
        AutomationProperties.SetName(RepeatButton, $"Repeat {_queue.RepeatMode}");
    }
    private void MediaCommand(string command)
    {
        if (_closed || _closingRequested || _fileOperationInProgress) return;
        switch (command)
        {
            case "play": if (!_player.IsPlaying && !_isOpeningTrack) PlayPause_Click(this, new()); break;
            case "pause": PauseCurrentPlayback(); break;
            case "stop": StopCurrentPlayback(); UpdateNowPlaying(); UpdatePlayPauseButton(); SavePlaybackSession(false); break;
            case "next": AdvanceQueue(); break; case "previous": Previous_Click(this, new()); break;
        }
    }
    private void UpdatePlayPauseButton()
    {
        var playing = _isDesignPreview ? _previewPlaying : _player.IsPlaying;
        PlayGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        PlayPauseButton.ToolTip = playing ? "Pause" : "Play"; AutomationProperties.SetName(PlayPauseButton, (string)PlayPauseButton.ToolTip);
        if (_taskbarPlay is not null)
        { _taskbarPlay.Description = playing ? "Pause" : "Play"; _taskbarPlay.ImageSource = new DrawingImage(new GeometryDrawing((Brush)FindResource("InkBrush"), null, (Geometry)FindResource(playing ? "PauseIcon" : "PlayIcon"))); }
        _mediaSession?.Update(playing, PlaybackPosition, DisplayDuration);
        RefreshListeningState();
        RefreshInterfaceState();
    }
    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (_closed) return;
        if (_currentTrack is not null)
        {
            if (!_isSeeking) PositionSlider.Value = Math.Clamp(PlaybackPosition.TotalSeconds, 0, Math.Max(1, DisplayDuration.TotalSeconds));
            ElapsedText.Text = FormatTime(PlaybackPosition); DurationText.Text = FormatTime(DisplayDuration);
        }
        if (DateTime.UtcNow - _lastMediaUpdate > TimeSpan.FromSeconds(1))
        { _lastMediaUpdate = DateTime.UtcNow; _mediaSession?.Update(_player.IsPlaying, PlaybackPosition, DisplayDuration); }
        if (DateTime.UtcNow - _lastSessionSave > TimeSpan.FromSeconds(5)) { _lastSessionSave = DateTime.UtcNow; SavePlaybackSession(false); }
        RefreshListeningState();
        DrawVisualizer();
    }
    private void DrawVisualizer()
    {
        if (_listeningView == "Mini") return;
        if (_reducedMotion || !ThemeAccessibility.AnimationsEnabled) { PresentSpectrum([], false); return; }
        if (_isDesignPreview)
        {
            var values = new float[32];
            for (var i = 0; i < values.Length; i++)
            {
                var x = i / 31d;
                values[i] = (float)((Math.Exp(-Math.Pow((x - .20) / .06, 2)) * .45 + Math.Exp(-Math.Pow((x - .40) / .09, 2)) * .7
                    + Math.Exp(-Math.Pow((x - .59) / .07, 2)) * .95 + Math.Exp(-Math.Pow((x - .78) / .09, 2)) * .5) * (.94 + .06 * Math.Sin(_previewFrame * .035 + i)));
            }
            _previewFrame++; PresentSpectrum(values, _previewPlaying); return;
        }
        PresentSpectrum(_player.GetSpectrum(), _player.IsPlaying);
    }
    private void PresentSpectrum(float[] values, bool active)
    {
        if (Visualizer.IsVisible && IsVisible && WindowState != WindowState.Minimized) Visualizer.SetSpectrum(values, active);
        if (_focusWindow is { IsVisible: true } && _focusWindow.WindowState != WindowState.Minimized) _focusWindow.SetSpectrum(values, active);
    }
    private void PositionSlider_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) _isSeeking = true; }
    private void PositionSlider_MouseUp(object sender, MouseButtonEventArgs e)
    { try { if (_currentTrack is not null) TrySeek(TimeSpan.FromSeconds(PositionSlider.Value)); } finally { _isSeeking = false; } }
    private void PositionSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)) return;
        try { if (_currentTrack is not null) TrySeek(TimeSpan.FromSeconds(PositionSlider.Value)); } finally { _isSeeking = false; }
    }
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { _player?.SetVolume((float)e.NewValue); if (_listeningState is not null) _listeningState.Volume = e.NewValue; }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _closingRequested || _fileOperationInProgress) return;
        IReadOnlyList<AudioOutputDevice> devices;
        try { devices = _player.GetOutputDevices(); }
        catch (Exception error) { SetStatus(error.Message); devices = []; }
        var dialog = new SettingsWindow(devices, _outputDeviceId, _reducedMotion, _mediaSession?.UnavailableReason, _musicRoot, _albumLayout, _chooseFolderOnSettingsOpen) { Owner = this };
        _chooseFolderOnSettingsOpen = false;
        if (ShowAnimatedDialog(dialog) != true || _closed) return;
        if (dialog.MusicRoot is not null && (!string.Equals(dialog.MusicRoot, _musicRoot, StringComparison.OrdinalIgnoreCase) || dialog.AlbumLayout != _albumLayout))
        {
            await ScanAsync([dialog.MusicRoot], layout: dialog.AlbumLayout);
            if (_musicRoot is not null && SamePath(_musicRoot, dialog.MusicRoot)) ShowSection("Albums");
        }
        try { if (ReconcileRenderedBoundary()) { _player.SetOutputDevice(dialog.OutputDeviceId); _outputDeviceId = dialog.OutputDeviceId; ResetHandledBoundary(); PrepareNextTrack(); } }
        catch (Exception error) { SetStatus($"Couldn't change output: {error.Message}"); }
        _reducedMotion = dialog.ReducedMotion; _player.AnalysisEnabled = !_reducedMotion && ThemeAccessibility.AnimationsEnabled; UpdatePlayPauseButton(); SavePlaybackSession(false);
        UpdateListeningActivity();
    }
}

