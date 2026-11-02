using XTONE.Models;

namespace XTONE.Services;

/// <summary>Album identity comes from a root-relative directory, never embedded release tags.</summary>
public static class FolderLibrary
{
    public const string AlbumFolders = "AlbumFolders";
    public const string ArtistAlbumFolders = "ArtistAlbumFolders";

    public static string NormalizeLayout(string? layout) => layout == ArtistAlbumFolders ? ArtistAlbumFolders : AlbumFolders;

    public static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static List<AlbumCard> BuildAlbums(IEnumerable<Track> source, string? musicRoot, string layout = AlbumFolders, IReadOnlyDictionary<string, string>? artworkOverrides = null)
    {
        if (string.IsNullOrWhiteSpace(musicRoot)) return [];
        var root = Path.GetFullPath(musicRoot);
        var rootAvailable = Directory.Exists(root);
        var depth = NormalizeLayout(layout) == ArtistAlbumFolders ? 2 : 1;
        string? AlbumFolder(Track track)
        {
            if (!IsWithinRoot(track.Path, root)) return null;
            var segments = Path.GetRelativePath(root, track.Path)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > depth ? Path.Combine(new[] { root }.Concat(segments.Take(depth)).ToArray()) : null;
        }
        var folders = source.Select(track => (Track: track, Folder: AlbumFolder(track)))
            .Where(item => item.Folder is not null)
            .GroupBy(item => item.Folder!, StringComparer.OrdinalIgnoreCase)
            .Where(group => !rootAvailable || group.Any(item => !item.Track.IsMissing) || Directory.Exists(group.Key))
            .ToDictionary(group => group.Key, group => group.Select(item => item.Track).ToList(), StringComparer.OrdinalIgnoreCase);
        if (rootAvailable)
        {
            var children = ChildFolders(root);
            foreach (var folder in depth == 1 ? children : children.SelectMany(ChildFolders))
                folders.TryAdd(folder, []);
        }
        return folders.OrderBy(group => Path.GetFileName(group.Key), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var tracks = group.Value.OrderBy(track => track.DiscNumber)
                    .ThenBy(track => track.TrackNumber == 0 ? uint.MaxValue : track.TrackNumber)
                    .ThenBy(track => Path.GetRelativePath(group.Key, track.Path), StringComparer.OrdinalIgnoreCase).ToList();
                var albumArtists = ArtistCredits.Clean(tracks.SelectMany(track => track.AlbumPerformers));
                var artists = albumArtists.Length > 0 ? albumArtists : ArtistCredits.Clean(tracks.SelectMany(track => track.ArtistCredits));
                return new AlbumCard
                {
                    Id = "folder:" + Path.GetFullPath(group.Key).ToUpperInvariant(),
                    FolderPath = group.Key, Title = Path.GetFileName(group.Key),
                    Artist = artists.Length == 1 ? artists[0] : artists.Length > 1 ? "Various artists" : "",
                    ArtworkPath = (artworkOverrides is not null && artworkOverrides.TryGetValue(group.Key, out var chosen) && File.Exists(chosen) ? chosen : null) ?? FindCover(group.Key) ?? tracks.Select(track => track.ArtworkPath).FirstOrDefault(path => path is not null),
                    Tracks = tracks
                };
            }).ToList();
    }

    private static List<string> ChildFolders(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
            }).Where(path => !Path.GetFileName(path).StartsWith(".xtone-folder-", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string? FindCover(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            // Scan directory names once and retain only the small cover-name set.
            // A huge album directory must not allocate an array containing every file.
            var names = new[] { "cover.jpg", "cover.jpeg", "cover.png", "cover.bmp",
                "folder.jpg", "folder.jpeg", "folder.png", "folder.bmp",
                "front.jpg", "front.jpeg", "front.png", "front.bmp" };
            var matches = new string?[names.Length];
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                var index = Array.FindIndex(names, candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (index == 0) return path;
                matches[index] = path;
            }
            return matches.FirstOrDefault(path => path is not null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
