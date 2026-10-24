using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace XTONE.Models;

/// <summary>A shared presentation of the single playback engine, used by all listening views.</summary>
public sealed class NowPlayingState : INotifyPropertyChanged
{
    private string _title = "Choose a track";
    private string _artist = "Your local music";
    private string _album = "";
    private string _quality = "Local audio";
    private string _status = "";
    private string _trackIdentity = "";
    private string? _artworkPath;
    private ImageSource? _artwork;
    private Brush _coverBrush = Brushes.SlateGray;
    private double _positionSeconds;
    private double _durationSeconds;
    private double _volume = .72;
    private bool _isPlaying;
    private bool _hasTrack;
    private bool _isOpening;
    private bool _isFavorite;
    private bool _motionEnabled = true;
    private bool _canSeek;
    private bool _miniAlwaysOnTop;
    private string _visualization = "Ribbon";

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Artist { get => _artist; set => Set(ref _artist, value); }
    public string Album { get => _album; set => Set(ref _album, value); }
    public string Quality { get => _quality; set => Set(ref _quality, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string TrackIdentity { get => _trackIdentity; set => Set(ref _trackIdentity, value); }
    public string? ArtworkPath { get => _artworkPath; set => Set(ref _artworkPath, value); }
    public ImageSource? Artwork { get => _artwork; set => Set(ref _artwork, value); }
    public Brush CoverBrush { get => _coverBrush; set => Set(ref _coverBrush, value); }
    public double PositionSeconds
    {
        get => _positionSeconds;
        set { if (Set(ref _positionSeconds, double.IsFinite(value) ? Math.Max(0, value) : 0)) { Raise(nameof(ElapsedText)); Raise(nameof(RemainingText)); } }
    }
    public double DurationSeconds
    {
        get => _durationSeconds;
        set { if (Set(ref _durationSeconds, double.IsFinite(value) ? Math.Max(0, value) : 0)) { Raise(nameof(DurationText)); Raise(nameof(RemainingText)); Raise(nameof(SeekMaximum)); } }
    }
    public double Volume { get => _volume; set => Set(ref _volume, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0); }
    public bool IsPlaying { get => _isPlaying; set { if (Set(ref _isPlaying, value)) { Raise(nameof(PlayPauseLabel)); Raise(nameof(PlaybackLabel)); } } }
    public bool HasTrack { get => _hasTrack; set { if (Set(ref _hasTrack, value)) Raise(nameof(PlaybackLabel)); } }
    public bool IsOpening { get => _isOpening; set { if (Set(ref _isOpening, value)) { Raise(nameof(PlayPauseLabel)); Raise(nameof(PlaybackLabel)); } } }
    public bool IsFavorite { get => _isFavorite; set { if (Set(ref _isFavorite, value)) Raise(nameof(FavoriteLabel)); } }
    public bool MotionEnabled { get => _motionEnabled; set => Set(ref _motionEnabled, value); }
    public bool CanSeek { get => _canSeek; set => Set(ref _canSeek, value); }
    public bool MiniAlwaysOnTop { get => _miniAlwaysOnTop; set => Set(ref _miniAlwaysOnTop, value); }
    public string Visualization { get => _visualization; set => Set(ref _visualization, value is "Bars" or "Off" ? value : "Ribbon"); }
    public string ElapsedText => FormatTime(PositionSeconds);
    public string DurationText => FormatTime(DurationSeconds);
    public string RemainingText => "−" + FormatTime(Math.Max(0, DurationSeconds - PositionSeconds));
    public double SeekMaximum => Math.Max(1, DurationSeconds);
    public string PlayPauseLabel => IsOpening ? "Cancel loading" : IsPlaying ? "Pause" : "Play";
    public string PlaybackLabel => IsOpening ? "Loading audio…" : !HasTrack ? "Your music, ready when you are." : IsPlaying ? "Now playing" : "Paused";
    public string FavoriteLabel => IsFavorite ? "Remove from favorites" : "Add to favorites";

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, TimeSpan.MaxValue.TotalSeconds / 2));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
    private void Raise(string? name) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record PlaybackActions(
    Action PlayPause, Action Previous, Action Next, Action ToggleFavorite,
    Action ShowLibrary, Action ShowFocus, Action ShowMini, Action ShowQueue,
    Action<double> Seek, Action<double> SetVolume);
