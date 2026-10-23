using System.Windows.Media;

namespace XTONE.Models;

public sealed class Track
{
    public string Path { get; init; } = "";
    public string Title { get; init; } = "Untitled track";
    public string Artist { get; init; } = "Unknown artist";
    public string Album { get; init; } = "Unknown album";
    public string Format { get; init; } = "";
    public string? ArtworkPath { get; init; }
    public TimeSpan Duration { get; init; }
    public int BitRateKbps { get; init; }
    public int SampleRate { get; init; }
    public int BitDepth { get; init; }
    public int Channels { get; init; }

    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");
    public string QualityLabel => Format.ToUpperInvariant() switch
    {
        "MP3" => "Lossy",
        "FLAC" or "WAV" or "ALAC" => "Lossless",
        _ => "Unknown"
    };
    public string QualitySummary => string.Join(" · ", new[]
    {
        Format,
        QualityLabel,
        BitDepth > 0 ? $"{BitDepth}-bit" : null,
        SampleRate > 0 ? $"{SampleRate / 1000.0:0.#} kHz" : null
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
    public string DetailSummary => string.Join("  ·  ", new[]
    {
        BitRateKbps > 0 ? $"{BitRateKbps:N0} kb/s" : null,
        SampleRate > 0 ? $"{SampleRate / 1000.0:0.#} kHz" : null,
        BitDepth > 0 ? $"{BitDepth}-bit" : null,
        Channels > 0 ? (Channels == 1 ? "Mono" : $"{Channels} channels") : null
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
    public string PlayerQualitySummary => string.Join(" · ", new[]
    {
        Format,
        QualityLabel,
        string.Join(" / ", new[]
        {
            BitDepth > 0 ? $"{BitDepth}-bit" : null,
            SampleRate > 0 ? $"{SampleRate / 1000.0:0.#} kHz" : null
        }.Where(part => part is not null))
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
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
    public string Title { get; init; } = "Untitled album";
    public string Artist { get; init; } = "Unknown artist";
    public string? ArtworkPath { get; init; }
    public ImageSource? Artwork { get; init; }
    public string? Notes { get; init; }
    public bool IsCreatedLocally { get; init; }
    public List<Track> Tracks { get; init; } = [];
    public string TrackCountText => $"{Tracks.Count} {(Tracks.Count == 1 ? "track" : "tracks")}";
    public Brush CoverBrush
    {
        get
        {
            var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(Title + Artist) & int.MaxValue;
            return new SolidColorBrush(Palette[hash % Palette.Length]);
        }
    }
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
}

public sealed class LibraryState
{
    public List<string> TrackPaths { get; set; } = [];
    public List<string> FolderPaths { get; set; } = [];
    public List<CustomAlbum> CustomAlbums { get; set; } = [];
    public List<CustomPlaylist> Playlists { get; set; } = [];
}

public sealed record LibrarySnapshot(
    List<Track> Tracks,
    List<string> Folders,
    List<CustomAlbum> CustomAlbums,
    List<CustomPlaylist> Playlists);
