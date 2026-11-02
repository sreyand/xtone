using System.Text.Json.Serialization;
using System.Windows.Media;

namespace XTONE.Models;

public sealed record Track
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int MetadataVersion { get; init; }
    public string Path { get; init; } = "";
    public string Title { get; init; } = "Untitled track";
    public string Artist { get; init; } = "Unknown artist";
    public string AlbumArtist { get; init; } = "";
    public string[] Performers { get; init; } = [];
    public string[] AlbumPerformers { get; init; } = [];
    public string Album { get; init; } = "Unknown album";
    public uint TrackNumber { get; init; }
    public uint DiscNumber { get; init; }
    public string Format { get; init; } = "";
    public string Codec { get; init; } = "";
    public bool? IsLossless { get; init; }
    public string? ArtworkPath { get; init; }
    public TimeSpan Duration { get; init; }
    public int BitRateKbps { get; init; }
    public int SampleRate { get; init; }
    public int BitDepth { get; init; }
    public int Channels { get; init; }
    public long FileSize { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public DateTime AddedUtc { get; init; } = DateTime.UtcNow;
    public bool IsMissing { get; init; }
    public string? Error { get; init; }
    public bool IsFavorite { get; set; }

    [JsonIgnore] public IReadOnlyList<string> ArtistCredits => XTONE.Models.ArtistCredits.ForTrack(this);

    [JsonIgnore] public bool CanPlay => !IsMissing && string.IsNullOrWhiteSpace(Error);
    [JsonIgnore] public string Availability => IsMissing ? "Missing file" : !string.IsNullOrWhiteSpace(Error) ? "Unreadable file" : "";
    [JsonIgnore] public string AlbumGroupingArtist => string.IsNullOrWhiteSpace(AlbumArtist) ? Artist : AlbumArtist;
    [JsonIgnore] public string DurationText => Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss");
    [JsonIgnore] public string QualityLabel => !string.IsNullOrWhiteSpace(Error) ? "Unknown" : IsLossless switch
    {
        true => "Lossless",
        false => "Lossy",
        _ => Format.ToUpperInvariant() switch
        {
            "MP3" => "Lossy",
            "FLAC" or "ALAC" => "Lossless",
            _ => "Unknown"
        }
    };
    [JsonIgnore] public string QualitySummary => string.Join(" · ", new[]
    {
        Format, QualityLabel,
        BitDepth > 0 ? $"{BitDepth}-bit" : null,
        SampleRate > 0 ? $"{SampleRate / 1000.0:0.#} kHz" : null
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
    [JsonIgnore] public string DetailSummary => string.Join("  ·  ", new[]
    {
        BitRateKbps > 0 ? $"{BitRateKbps:N0} kb/s" : null,
        SampleRate > 0 ? $"{SampleRate / 1000.0:0.#} kHz" : null,
        BitDepth > 0 ? $"{BitDepth}-bit" : null,
        Channels > 0 ? (Channels == 1 ? "Mono" : $"{Channels} channels") : null
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
    [JsonIgnore] public string PlayerQualitySummary => string.Join(" · ", new[]
    {
        Format, QualityLabel,
        string.Join(" / ", new[]
        {
            BitDepth > 0 ? $"{BitDepth}-bit" : null,
            SampleRate > 0 ? $"{SampleRate / 1000.0:0.#} kHz" : null
        }.Where(part => part is not null))
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

/// <summary>Only structured tag values define separate identities; punctuation is preserved.</summary>
public static class ArtistCredits
{
    public static IReadOnlyList<string> ForTrack(Track track) => Clean(track.Performers is { Length: > 0 }
        ? track.Performers : [track.Artist]);

    public static string[] Clean(IEnumerable<string>? values) => (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim().Normalize(System.Text.NormalizationForm.FormC))
        .Where(value => !value.Equals("Unknown artist", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed class AlbumCard
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(42, 69, 102), Color.FromRgb(112, 111, 96),
        Color.FromRgb(47, 61, 89), Color.FromRgb(157, 154, 143),
        Color.FromRgb(110, 74, 68), Color.FromRgb(74, 99, 96)
    ];
    public string Id { get; init; } = "";
    public string? FolderPath { get; init; }
    public string Title { get; init; } = "Untitled album";
    public string Artist { get; init; } = "Unknown artist";
    public string? ArtworkPath { get; init; }
    public ImageSource? Artwork { get; init; }
    public string? Notes { get; init; }
    public bool IsCreatedLocally { get; init; }
    public List<Track> Tracks { get; init; } = [];
    public string TrackCountText => $"{Tracks.Count} {(Tracks.Count == 1 ? "track" : "tracks")}";
    public Brush CoverBrush => new SolidColorBrush(Palette[(StringComparer.OrdinalIgnoreCase.GetHashCode(Title + Artist) & int.MaxValue) % Palette.Length]);
}

public sealed class CustomAlbum
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? Notes { get; set; }
    public string? ArtworkPath { get; set; }
    public List<string> TrackPaths { get; set; } = [];
}

public sealed class CustomPlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> TrackPaths { get; set; } = [];
    public string? SmartRule { get; set; }
}

public sealed class PlaybackSession
{
    public List<string> QueuePaths { get; set; } = [];
    public int CurrentIndex { get; set; } = -1;
    public double PositionSeconds { get; set; }
    public double Volume { get; set; } = .72;
    public bool Shuffle { get; set; }
    public bool Repeat { get; set; }
    public string RepeatMode { get; set; } = "Off";
    public string? OutputDeviceId { get; set; }
    public bool ReducedMotion { get; set; }
    public bool MiniAlwaysOnTop { get; set; }
    public string FocusVisualization { get; set; } = "Ribbon";
    public string LastSection { get; set; } = "Albums";
    public string MusicView { get; set; } = "Grid";
    public DateTime SavedUtc { get; set; }
}

public sealed class ListeningHistoryEntry
{
    public string Path { get; set; } = "";
    public DateTime LastPlayedUtc { get; set; }
    public int PlayCount { get; set; }
}

public sealed class LibraryState
{
    public int Version { get; set; } = 3;
    public string? MusicRoot { get; set; }
    public string AlbumLayout { get; set; } = "AlbumFolders";
    public List<string> PinnedFolderPaths { get; set; } = [];
    public Dictionary<string, string> FolderArtworkPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> TrackPaths { get; set; } = [];
    public List<string> FolderPaths { get; set; } = [];
    public List<CustomAlbum> CustomAlbums { get; set; } = [];
    public List<CustomPlaylist> Playlists { get; set; } = [];
    public Dictionary<string, Track> MetadataCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> FavoritePaths { get; set; } = [];
    public List<ListeningHistoryEntry> History { get; set; } = [];
    public PlaybackSession Session { get; set; } = new();
}

public sealed record LibraryProgress(int Done, int Total, string Message);

public sealed record LibrarySnapshot(
    List<Track> Tracks,
    List<string> Folders,
    List<CustomAlbum> CustomAlbums,
    List<CustomPlaylist> Playlists)
{
    public long Revision { get; init; }
    public string? MusicRoot { get; init; }
    public string AlbumLayout { get; init; } = "AlbumFolders";
    public List<string> PinnedFolderPaths { get; init; } = [];
    public Dictionary<string, string> FolderArtworkPaths { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ListeningHistoryEntry> History { get; init; } = [];
    public PlaybackSession Session { get; init; } = new();
    public string? RecoveryNotice { get; init; }
}
