using System.Windows.Media;

namespace XTONE.Models;

/// <summary>A physical music folder or an unfiled audio track; a track is never a synthetic album.</summary>
public sealed class MusicBrowserItem
{
    public AlbumCard? Folder { get; init; }
    public Track? Track { get; init; }
    public bool IsFolder => Folder is not null;
    public bool IsTrack => Track is not null;
    public string Id => Folder?.Id ?? Track?.Id ?? "";
    public string Title => Folder?.Title ?? Track?.Title ?? "";
    public string Artist => (Folder?.Artist ?? Track?.Artist) is { Length: > 0 } name && !name.Equals("Unknown artist", StringComparison.OrdinalIgnoreCase) ? name : "";
    public string? ArtworkPath => Folder?.ArtworkPath ?? Track?.ArtworkPath;
    public ImageSource? Artwork => Folder?.Artwork;
    public string DurationText => Track?.DurationText ?? "";
    public Brush CoverBrush => Folder?.CoverBrush ?? new SolidColorBrush(Color.FromRgb(63, 80, 103));
}
