using System.Diagnostics;
using System.Text;
using System.Text.Json;
using XTONE.Models;
using XTONE.Services;

var count = args.Length == 0 ? 10000 : int.Parse(args[0]);
if (count is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(count));
var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
var fixture = Path.Combine(parent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    var music = Path.Combine(fixture, "music"); var data = Path.Combine(fixture, "data");
    Directory.CreateDirectory(music);
    var bytes = CreateTinyWav(); var seed = Path.Combine(fixture, "seed.wav"); File.WriteAllBytes(seed, bytes);
    var seedStore = new LibraryStore(data); var template = (await seedStore.ImportAsync([seed])).Tracks.Single();
    var state = new LibraryState { MusicRoot = music };
    var timer = Stopwatch.StartNew();
    for (var i = 0; i < count; i++)
    {
        var album = Path.Combine(music, $"Album {i / 100:0000}"); Directory.CreateDirectory(album);
        var path = Path.Combine(album, $"Track {i:000000}.wav"); File.WriteAllBytes(path, bytes);
        state.TrackPaths.Add(path);
        state.MetadataCache[path] = template with { Id = Guid.NewGuid().ToString("N"), Path = path, Title = $"Track {i:000000}", LastWriteUtc = File.GetLastWriteTimeUtc(path) };
    }
    File.WriteAllText(Path.Combine(data, "library.json"), JsonSerializer.Serialize(state));
    timer.Stop(); var setupMs = timer.ElapsedMilliseconds;
    var cacheFile = Path.Combine(data, "library.json"); var sentinel = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc); File.SetLastWriteTimeUtc(cacheFile, sentinel);
    var allocated = GC.GetTotalAllocatedBytes(true);
    var store = new LibraryStore(data);
    timer.Restart(); var loaded = store.Load(); timer.Stop(); var loadMs = timer.ElapsedMilliseconds;
    var loadAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
    if (loaded.Tracks.Count != count || store.MetadataReadCount != 0) throw new Exception("Cached load reread source metadata.");
    var reports = 0;
    timer.Restart(); var refreshed = await store.RefreshAsync(new ImmediateProgress(_ => reports++)); timer.Stop(); var refreshMs = timer.ElapsedMilliseconds;
    if (refreshed.Tracks.Count != count || store.MetadataReadCount != 0 || refreshed.Revision != loaded.Revision || File.GetLastWriteTimeUtc(cacheFile) != sentinel)
        throw new Exception("Unchanged refresh rewrote the library or reread metadata.");
    if (reports > 5 + Math.Ceiling(refreshMs / 100d)) throw new Exception("Progress reports exceeded the time bound.");
    timer.Restart(); var albums = FolderLibrary.BuildAlbums(refreshed.Tracks, music); timer.Stop(); var albumsMs = timer.ElapsedMilliseconds;
    if (albums.Count != (count + 99) / 100 || albums.Sum(album => album.Tracks.Count) != count) throw new Exception("Folder grouping lost tracks.");
    timer.Restart(); store.SaveSession(new PlaybackSession { QueuePaths = [state.TrackPaths[0], state.TrackPaths[0]], CurrentIndex = 1, PositionSeconds = .0005 }); timer.Stop(); var sessionMs = timer.ElapsedMilliseconds;
    if (File.GetLastWriteTimeUtc(cacheFile) != sentinel) throw new Exception("Session save rewrote metadata.");
    var result = new { Tracks = count, PhysicalTinyWavFiles = count, Folders = albums.Count, SetupExcludedMs = setupMs, CachedLoadMs = loadMs,
        LoadAllocatedBytes = loadAllocated, UnchangedRefreshMs = refreshMs, FolderGroupingMs = albumsMs, SessionSaveMs = sessionMs,
        MetadataReads = store.MetadataReadCount, ProgressReports = reports, CacheBytes = new FileInfo(cacheFile).Length,
        Note = "Isolated local tiny-WAV metadata-cache fixture, not real-world decoder or network-drive throughput." };
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    if (args.Length > 1) File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS scaled cache/load/refresh/folder/session checks.");
    return 0;
}
finally
{
    if (!Path.GetFullPath(fixture).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture escaped its parent.");
    Directory.Delete(fixture, true);
}

static byte[] CreateTinyWav()
{
    const int frames = 44, channels = 2, sampleRate = 44100, bits = 16, dataBytes = frames * channels * bits / 8;
    using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Encoding.ASCII, true);
    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataBytes); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
    writer.Write((ushort)1); writer.Write((ushort)channels); writer.Write(sampleRate); writer.Write(sampleRate * channels * bits / 8);
    writer.Write((ushort)(channels * bits / 8)); writer.Write((ushort)bits); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataBytes); writer.Write(new byte[dataBytes]);
    return memory.ToArray();
}
sealed class ImmediateProgress(Action<LibraryProgress> handler) : IProgress<LibraryProgress>
{ public void Report(LibraryProgress value) => handler(value); }
