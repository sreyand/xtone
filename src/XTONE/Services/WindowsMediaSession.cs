using System.Windows.Interop;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using XTONE.Models;

namespace XTONE.Services;

/// <summary>Native now-playing information and transport buttons for this desktop window.</summary>
public sealed class WindowsMediaSession : IDisposable
{
    private readonly Window _window;
    private readonly Action<string> _command;
    private readonly Action<TimeSpan> _seek;
    private SystemMediaTransportControls? _controls;
    private bool _disposed;
    private int _trackVersion;
    public string? UnavailableReason { get; private set; }

    public WindowsMediaSession(Window window, Action<string> command, Action<TimeSpan> seek)
    {
        _window = window;
        _command = command;
        _seek = seek;
        try
        {
            _controls = SystemMediaTransportControlsInterop.GetForWindow(new WindowInteropHelper(window).EnsureHandle());
            _controls.IsEnabled = true;
            _controls.IsPlayEnabled = true;
            _controls.IsPauseEnabled = true;
            _controls.IsNextEnabled = true;
            _controls.IsPreviousEnabled = true;
            _controls.IsStopEnabled = true;
            _controls.PlaybackStatus = MediaPlaybackStatus.Stopped;
            _controls.ButtonPressed += ButtonPressed;
            _controls.PlaybackPositionChangeRequested += PositionRequested;
        }
        catch (Exception error) { UnavailableReason = error.Message; _controls = null; }
    }

    public async Task SetTrackAsync(Track track)
    {
        if (_controls is null || _disposed) return;
        var version = ++_trackVersion;
        try
        {
            var updater = _controls.DisplayUpdater;
            updater.ClearAll();
            updater.Type = MediaPlaybackType.Music;
            updater.AppMediaId = "XTONE";
            updater.MusicProperties.Title = track.Title;
            updater.MusicProperties.Artist = track.Artist;
            updater.MusicProperties.AlbumTitle = track.Album;
            updater.Update();
            if (track.ArtworkPath is { } artwork && File.Exists(artwork))
            {
                var file = await StorageFile.GetFileFromPathAsync(artwork);
                if (_disposed || version != _trackVersion) return;
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                updater.Update();
            }
        }
        catch (Exception error) { UnavailableReason = error.Message; }
    }

    public void Update(bool playing, TimeSpan position, TimeSpan duration)
    {
        if (_controls is null || _disposed) return;
        try
        {
            _controls.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            var end = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
            _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero, MinSeekTime = TimeSpan.Zero, EndTime = end, MaxSeekTime = end,
                Position = position < TimeSpan.Zero ? TimeSpan.Zero : position > end ? end : position
            });
        }
        catch (Exception error) { UnavailableReason = error.Message; }
    }

    private void ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var action = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => "play",
            SystemMediaTransportControlsButton.Pause => "pause",
            SystemMediaTransportControlsButton.Stop => "stop",
            SystemMediaTransportControlsButton.Next => "next",
            SystemMediaTransportControlsButton.Previous => "previous",
            _ => ""
        };
        if (!_disposed && action.Length > 0) _window.Dispatcher.BeginInvoke(new Action(() => _command(action)));
    }
    private void PositionRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
    {
        if (!_disposed) _window.Dispatcher.BeginInvoke(new Action(() => _seek(args.RequestedPlaybackPosition)));
    }
    public void Dispose()
    {
        _disposed = true;
        if (_controls is null) return;
        _controls.ButtonPressed -= ButtonPressed;
        _controls.PlaybackPositionChangeRequested -= PositionRequested;
        try { _controls.IsEnabled = false; _controls.DisplayUpdater.ClearAll(); _controls.DisplayUpdater.Update(); } catch { }
        _controls = null;
    }
}
