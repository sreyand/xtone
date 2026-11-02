using System.ComponentModel;
using System.Windows.Media;
using XTONE.Models;
using XTONE.Services;
using XTONE.Views;

namespace XTONE;

public partial class MainWindow
{
    private readonly NowPlayingState _listeningState = new();
    private FocusWindow? _focusWindow;
    private MiniPlayerWindow? _miniWindow;
    private long _viewTransitionVersion;
    private readonly HashSet<Window> _closingListeningWindows = [];
    private bool _syncingListeningState;
    private string _listeningView = "Library";
    private WindowState _libraryWindowState = WindowState.Normal;
    private string? _presentedTrackPath;

    private void InitializeListeningViews()
    {
        _listeningState.PropertyChanged += ListeningState_Changed;
        RefreshListeningState();
    }

    private PlaybackActions CreateListeningActions() => new(
        () =>
        {
            if (_closed || _closingRequested) return;
            if (_tracks.Count == 0 && _queue.Entries.Count == 0 && !_isDesignPreview) ShowLibraryView();
            PlayPause_Click(this, new());
        },
        () => Previous_Click(this, new()), () => Next_Click(this, new()),
        () => { if (_currentTrack is { } track) ToggleFavorite(track); },
        ShowLibraryView, ShowFocusView, ShowMiniView, ShowQueueFromListening,
        seconds => { if (double.IsFinite(seconds)) TrySeek(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DisplayDuration.TotalSeconds))); },
        value => { if (!_closed && double.IsFinite(value)) VolumeSlider.Value = Math.Clamp(value, 0, 1); });

    private async void ShowFocusView() => await SwitchListeningViewAsync("Focus");
    private async void ShowMiniView() => await SwitchListeningViewAsync("Mini");
    private async void ShowLibraryView() => await SwitchListeningViewAsync("Library");

    private async Task SwitchListeningViewAsync(string view)
    {
        if (_closed || _closingRequested) return;
        var version = ++_viewTransitionVersion;
        RefreshListeningState();
        if (IsVisible && WindowState != WindowState.Minimized) _libraryWindowState = WindowState;
        Window target;
        if (view == "Focus")
        {
            if (_focusWindow is null)
            {
                var window = new FocusWindow(_listeningState, CreateListeningActions()) { ShowActivated = ShowActivated };
                _focusWindow = window;
                window.Closing += ListeningWindow_Closing;
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_focusWindow, window)) _focusWindow = null;
                    if (!_closed && !_closingRequested && _listeningView == "Focus") ShowLibraryView();
                };
                window.StateChanged += ListeningWindow_StateChanged;
            }
            target = _focusWindow;
        }
        else if (view == "Mini")
        {
            if (_miniWindow is null)
            {
                var window = new MiniPlayerWindow(_listeningState, CreateListeningActions()) { ShowActivated = ShowActivated };
                _miniWindow = window;
                window.Closing += ListeningWindow_Closing;
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_miniWindow, window)) _miniWindow = null;
                    if (!_closed && !_closingRequested && _listeningView == "Mini") ShowLibraryView();
                };
                window.StateChanged += ListeningWindow_StateChanged;
            }
            target = _miniWindow;
        }
        else { view = "Library"; target = this; }

        _listeningView = view;
        target.ShowInTaskbar = true;
        if (!target.IsVisible) { UiMotion.PrepareFadeIn(target); target.Show(); }
        if (target.WindowState == WindowState.Minimized)
            target.WindowState = ReferenceEquals(target, this) ? _libraryWindowState : WindowState.Normal;
        if (ShowActivated) target.Activate();
        var outgoing = ListeningWindows().Where(window => !ReferenceEquals(window, target) && window.IsVisible).ToArray();
        foreach (var window in outgoing)
        {
            if (window is FocusWindow focus) focus.PrepareToHide();
            if (window is MiniPlayerWindow mini) mini.PrepareToHide();
        }
        UpdateListeningActivity();
        await Task.WhenAll(outgoing.Select(window => UiMotion.FadeOutAsync(window)).Append(UiMotion.FadeInAsync(target)));
        // A newer request may have reversed one of these fades. Only its
        // completion may hide windows, so rapid mode changes stay reversible.
        if (version != _viewTransitionVersion || _closed || _closingRequested) return;
        foreach (var window in ListeningWindows().Where(window => !ReferenceEquals(window, target)))
        {
            if (window is MiniPlayerWindow mini) mini.PrepareToHide();
            if (window is FocusWindow focus) focus.ExitFullScreen();
            window.ShowInTaskbar = false;
            window.Hide();
        }
        UpdateListeningActivity();
    }

    private IEnumerable<Window> ListeningWindows()
    {
        yield return this;
        if (_focusWindow is not null) yield return _focusWindow;
        if (_miniWindow is not null) yield return _miniWindow;
    }

    private async void ListeningWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (sender is not Window window || _closed || _closingRequested || !ShowActivated
            || !UiMotion.Enabled || _closingListeningWindows.Contains(window)) return;
        e.Cancel = true;
        await SwitchListeningViewAsync("Library");
        // Even an already transparent view may have no fade to await.
        await Task.Yield();
        // Reopening a view during its close fade keeps that view alive.
        if (_closed || _closingRequested || window.IsVisible) return;
        _closingListeningWindows.Add(window);
        try { window.Close(); }
        finally { _closingListeningWindows.Remove(window); }
    }

    private async void ShowQueueFromListening()
    {
        var version = _viewTransitionVersion + 1;
        await SwitchListeningViewAsync("Library");
        if (_closed || _closingRequested || _listeningView != "Library" || version != _viewTransitionVersion) return;
        await SetQueueVisibleAsync(true);
    }
    private void FocusMode_Click(object sender, RoutedEventArgs e) => ShowFocusView();
    private void MiniPlayer_Click(object sender, RoutedEventArgs e) => ShowMiniView();
    private void MainSeek_AutomationCommitted(object? sender, SliderCommittedEventArgs e)
    { if (double.IsFinite(e.Value)) TrySeek(TimeSpan.FromSeconds(Math.Clamp(e.Value, 0, DisplayDuration.TotalSeconds))); }
    private void ListeningWindow_StateChanged(object? sender, EventArgs e) => UpdateListeningActivity();

    private void ListeningState_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed || _syncingListeningState) return;
        if (e.PropertyName == nameof(NowPlayingState.Volume)) VolumeSlider.Value = _listeningState.Volume;
        if (e.PropertyName is nameof(NowPlayingState.MiniAlwaysOnTop) or nameof(NowPlayingState.Visualization))
        { UpdateListeningActivity(); SavePlaybackSession(false); }
    }

    private void RefreshListeningState()
    {
        if (_closed || NowPlayingTitle is null) return;
        _syncingListeningState = true;
        try
        {
            var track = _currentTrack;
            _listeningState.TrackIdentity = _queue.Current?.Id.ToString("N") ?? (_isDesignPreview ? "preview" : "");
            _listeningState.Title = NowPlayingTitle.Text;
            _listeningState.Artist = NowPlayingArtist.Text;
            _listeningState.Album = track?.Album is "Unknown album" ? "" : track?.Album ?? "";
            _listeningState.Quality = QualityText.Text;
            _listeningState.HasTrack = track is not null || _isDesignPreview;
            _listeningState.IsOpening = _isOpeningTrack;
            _listeningState.IsPlaying = _isDesignPreview ? _previewPlaying : _player.IsPlaying;
            _listeningState.IsFavorite = track?.IsFavorite == true;
            _listeningState.ArtworkPath = NowPlayingArtwork.SourcePath;
            _listeningState.Artwork = NowPlayingArtwork.Source;
            _listeningState.Volume = VolumeSlider.Value;
            _listeningState.CanSeek = !_isDesignPreview && !_isOpeningTrack && _player.CurrentPath is not null && DisplayDuration > TimeSpan.Zero;
            _listeningState.DurationSeconds = _isDesignPreview ? PositionSlider.Maximum : DisplayDuration.TotalSeconds;
            _listeningState.PositionSeconds = _isDesignPreview ? PositionSlider.Value : PlaybackPosition.TotalSeconds;
            _listeningState.Status = _player.LastError ?? track?.Error ?? (track?.IsMissing == true ? "This file is missing. Locate it from the library to reconnect it."
                : ScanStatus.Text.StartsWith("Couldn't", StringComparison.OrdinalIgnoreCase)
                || ScanStatus.Text.StartsWith("Can't", StringComparison.OrdinalIgnoreCase) ? ScanStatus.Text : "");
            if (!string.Equals(_presentedTrackPath, track?.Path, StringComparison.OrdinalIgnoreCase))
            {
                _presentedTrackPath = track?.Path;
                _listeningState.CoverBrush = track is null ? Brushes.SlateGray
                    : new AlbumCard { Title = track.Album, Artist = track.AlbumGroupingArtist }.CoverBrush;
            }
        }
        finally { _syncingListeningState = false; }
    }

    private void UpdateListeningActivity()
    {
        if (_closed || _closingRequested) return;
        var mainVisible = IsVisible && WindowState != WindowState.Minimized;
        var focusVisible = _focusWindow is { IsVisible: true } && _focusWindow.WindowState != WindowState.Minimized;
        var motion = !_reducedMotion && ThemeAccessibility.AnimationsEnabled;
        UiMotion.Enabled = motion;
        XTONE.Controls.FluidLayout.MotionEnabled = motion;
        _listeningState.MotionEnabled = motion;
        _player.AnalysisEnabled = motion && (mainVisible && Visualizer.IsVisible || focusVisible && _listeningState.Visualization != "Off");
        _uiTimer.Interval = TimeSpan.FromMilliseconds(mainVisible || focusVisible ? 40 : _miniWindow is { IsVisible: true } ? 120 : 250);
        RefreshListeningState();
    }

    private void CloseListeningViews()
    {
        _listeningState.PropertyChanged -= ListeningState_Changed;
        _miniWindow?.Close(); _focusWindow?.Close();
        _miniWindow = null; _focusWindow = null;
    }
}
