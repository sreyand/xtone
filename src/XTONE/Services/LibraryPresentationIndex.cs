using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using XTONE.Models;

namespace XTONE.Services;

public sealed record ArtistCount(string Name, int Count);

/// <summary>Prepared off the dispatcher. Queries use stable orderings and bounded cancellation checks.</summary>
public sealed class LibraryPresentationIndex
{
    private readonly Dictionary<string, Track> _byPath;
    private readonly Dictionary<string, Track[]> _byArtist;
    private readonly Track[] _titleOrder, _artistOrder, _albumOrder, _addedOrder;
    private readonly string[] _musicSearch;
    public IReadOnlyDictionary<string, Track> ByPath => _byPath;
    public MusicBrowserItem[] MusicItems { get; }
    public ArtistCount[] Artists { get; }

    private LibraryPresentationIndex(Track[] tracks, IReadOnlyList<AlbumCard> albums, bool rootUnavailable, CancellationToken token)
    {
        _byPath = new(StringComparer.OrdinalIgnoreCase);
        var artists = new Dictionary<string, Dictionary<string, Track>>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            token.ThrowIfCancellationRequested(); _byPath.TryAdd(track.Path, track);
            foreach (var name in track.ArtistCredits)
            {
                if (!artists.TryGetValue(name, out var entries)) artists[name] = entries = new(StringComparer.OrdinalIgnoreCase);
                entries.TryAdd(track.Path, track);
            }
        }
        _titleOrder = tracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        _artistOrder = tracks.OrderBy(t => t.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Album, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TrackNumber).ToArray();
        _albumOrder = tracks.OrderBy(t => t.Album, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToArray();
        _addedOrder = tracks.OrderByDescending(t => t.AddedUtc).ToArray();
        _byArtist = artists.ToDictionary(p => p.Key, p => p.Value.Values.ToArray(), StringComparer.OrdinalIgnoreCase);
        Artists = artists.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new ArtistCount(p.Key, p.Value.Count)).ToArray();
        var filed = albums.SelectMany(a => a.Tracks).Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        MusicItems = albums.Select(a => new MusicBrowserItem { Folder = a }).Concat(_titleOrder.Where(t => !filed.Contains(t.Path) && (!t.IsMissing || rootUnavailable)).Select(t => new MusicBrowserItem { Track = t })).ToArray();
        _musicSearch = new string[MusicItems.Length];
        for (var i = 0; i < MusicItems.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var item = MusicItems[i];
            _musicSearch[i] = item.Folder is { } folder
                ? string.Join('\0', new[] { folder.Title, folder.Artist }.Concat(folder.Tracks.SelectMany(t => new[] { t.Title, t.Artist })))
                : string.Join('\0', item.Title, item.Artist);
        }
        token.ThrowIfCancellationRequested();
    }
    public static LibraryPresentationIndex Build(IEnumerable<Track> tracks, IReadOnlyList<AlbumCard> albums, bool rootUnavailable = false, CancellationToken token = default)
        => new(tracks.ToArray(), albums, rootUnavailable, token);
    public IReadOnlyList<Track> GetArtistTracks(string name) => _byArtist.GetValueOrDefault(name) ?? [];
    public MusicBrowserItem[] QueryMusic(string query, CancellationToken token = default)
    {
        if (query.Length == 0) return MusicItems;
        var result = new List<MusicBrowserItem>();
        for (var i = 0; i < MusicItems.Length; i++)
        { if ((i & 127) == 0) token.ThrowIfCancellationRequested(); if (_musicSearch[i].Contains(query, StringComparison.OrdinalIgnoreCase)) result.Add(MusicItems[i]); }
        token.ThrowIfCancellationRequested(); return result.ToArray();
    }
    public ArtistCount[] QueryArtists(string query, CancellationToken token = default)
    {
        if (query.Length == 0) return Artists;
        var result = new List<ArtistCount>();
        for (var i = 0; i < Artists.Length; i++)
        { if ((i & 127) == 0) token.ThrowIfCancellationRequested(); if (Artists[i].Name.Contains(query, StringComparison.OrdinalIgnoreCase)) result.Add(Artists[i]); }
        token.ThrowIfCancellationRequested(); return result.ToArray();
    }
    public Track[] QueryTracks(string section, string filter, string sort, string query, IReadOnlyList<Track> recent, IReadOnlySet<string> played, CancellationToken token = default)
    {
        IEnumerable<Track> source = sort switch { "artist" => _artistOrder, "album" => _albumOrder, "added" => _addedOrder, _ => section == "History" ? recent : _titleOrder };
        var result = new List<Track>(); var counter = 0;
        foreach (var track in source)
        {
            if ((counter++ & 127) == 0) token.ThrowIfCancellationRequested();
            if (section == "Favorites" && !track.IsFavorite || section == "History" && !played.Contains(track.Path)) continue;
            if (filter == "lossless" && track.QualityLabel != "Lossless" || filter == "missing" && !track.IsMissing && string.IsNullOrWhiteSpace(track.Error) || filter == "unplayed" && played.Contains(track.Path)) continue;
            if (query.Length > 0 && !track.Title.Contains(query, StringComparison.OrdinalIgnoreCase) && !track.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) && !track.Album.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(track);
        }
        token.ThrowIfCancellationRequested(); return result.ToArray();
    }
}
