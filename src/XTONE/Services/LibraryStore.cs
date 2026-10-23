using System.Security.Cryptography;
using System.Text.Json;
using XTONE.Models;

namespace XTONE.Services;

public sealed class LibraryStore
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".m4a", ".mp4", ".mp3"
    };

    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTONE");
    private string StateFile => Path.Combine(_root, "library.json");
    private string ArtworkFolder => Path.Combine(_root, "artwork");
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private LibraryState _state = new();

    public string StorageDescription => _root;

    public LibrarySnapshot Load()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(ArtworkFolder);
        if (File.Exists(StateFile))
        {
            try
            {
                _state = JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(StateFile), _jsonOptions) ?? new();
            }
            catch
            {
                _state = new();
            }
        }

        _state.TrackPaths = _state.TrackPaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _state.FolderPaths = _state.FolderPaths.Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var tracks = _state.TrackPaths.Select(ReadTrack).Where(track => track is not null).Cast<Track>().ToList();
        Save();
        return new LibrarySnapshot(tracks, [.. _state.FolderPaths], [.. _state.CustomAlbums], [.. _state.Playlists]);
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var known = new HashSet<string>(_state.TrackPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !SupportedExtensions.Contains(Path.GetExtension(path)))
                continue;
            var fullPath = Path.GetFullPath(path);
            if (known.Add(fullPath))
                _state.TrackPaths.Add(fullPath);
        }
        Save();
    }

    public void AddFolder(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!_state.FolderPaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            _state.FolderPaths.Add(fullPath);
        Save();
    }

    public void SaveAlbum(CustomAlbum album)
    {
        if (!string.IsNullOrWhiteSpace(album.ArtworkPath) && File.Exists(album.ArtworkPath))
            album.ArtworkPath = CopyArtwork(album.ArtworkPath);
        _state.CustomAlbums.RemoveAll(item => item.Id == album.Id);
        _state.CustomAlbums.Add(album);
        Save();
    }

    public void SavePlaylist(CustomPlaylist playlist)
    {
        _state.Playlists.RemoveAll(item => item.Id == playlist.Id);
        _state.Playlists.Add(playlist);
        Save();
    }

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension))
            return false;
        if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            return IsAlacContainer(path);
        return true;
    }

    private Track? ReadTrack(string path)
    {
        try
        {
            var file = TagLib.File.Create(path);
            var properties = file.Properties;
            var tag = file.Tag;
            var duration = properties.Duration;
            var bitrate = properties.AudioBitrate;
            if (bitrate <= 0 && duration.TotalSeconds > 0)
                bitrate = (int)Math.Round(new FileInfo(path).Length * 8 / duration.TotalSeconds / 1000d);

            string? artworkPath = null;
            var picture = tag.Pictures.FirstOrDefault();
            if (picture?.Data?.Data is { Length: > 0 } imageBytes)
                artworkPath = SaveArtwork(path, imageBytes, picture.MimeType);

            return new Track
            {
                Path = path,
                Title = string.IsNullOrWhiteSpace(tag.Title) ? Path.GetFileNameWithoutExtension(path) : tag.Title,
                Artist = string.IsNullOrWhiteSpace(tag.FirstPerformer) ? "Unknown artist" : tag.FirstPerformer,
                Album = string.IsNullOrWhiteSpace(tag.Album) ? "Unknown album" : tag.Album,
                Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant() switch
                {
                    "M4A" or "MP4" => IsAlacContainer(path) ? "ALAC" : "M4A",
                    var extension => extension
                },
                Duration = duration,
                BitRateKbps = bitrate,
                SampleRate = properties.AudioSampleRate,
                BitDepth = properties.BitsPerSample,
                Channels = properties.AudioChannels,
                ArtworkPath = artworkPath
            };
        }
        catch
        {
            return null;
        }
    }

    private string SaveArtwork(string sourcePath, byte[] data, string? mimeType)
    {
        Directory.CreateDirectory(ArtworkFolder);
        var extension = mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg"
        };
        var identity = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourcePath.ToUpperInvariant()));
        var target = Path.Combine(ArtworkFolder, Convert.ToHexString(identity)[..20] + extension);
        if (!File.Exists(target))
            File.WriteAllBytes(target, data);
        return target;
    }

    private string CopyArtwork(string sourcePath)
    {
        Directory.CreateDirectory(ArtworkFolder);
        var extension = Path.GetExtension(sourcePath);
        var identity = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath).ToUpperInvariant()));
        var target = Path.Combine(ArtworkFolder, Convert.ToHexString(identity)[..20] + extension);
        if (!File.Exists(target))
            File.Copy(sourcePath, target);
        return target;
    }

    private static bool IsAlacContainer(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[1024 * 1024];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (buffer.AsSpan(0, read).IndexOf("alac"u8) >= 0)
                return true;
            if (stream.Length > buffer.Length)
            {
                stream.Position = Math.Max(0, stream.Length - buffer.Length);
                read = stream.Read(buffer, 0, buffer.Length);
                if (buffer.AsSpan(0, read).IndexOf("alac"u8) >= 0)
                    return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private void Save()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(_state, _jsonOptions));
    }
}
