using System.Buffers.Binary;
using System.Text;
using System.Threading;
using XTONE.Models;
using XTONE.Services;

var fixtureParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
Directory.CreateDirectory(fixtureParent);
var root = Path.Combine(fixtureParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
try
{
    await Run("imports real PCM WAV metadata and avoids duplicate paths", async () =>
    {
        var folder = NewFolder("import");
        var path = WriteWav(Path.Combine(folder, "song.wav"), 1);
        var store = NewStore("import");
        var snapshot = await store.ImportAsync([path, path, path.ToUpperInvariant()]);
        Equal(1, snapshot.Tracks.Count);
        var track = snapshot.Tracks.Single();
        Equal(44100, track.SampleRate);
        Equal(16, track.BitDepth);
        Equal(2, track.Channels);
        Equal(true, track.IsLossless);
        Equal("PCM", track.Codec);
        True(track.CanPlay && File.Exists(path));
    });

    await Run("reuses unchanged metadata and refreshes changed audio", async () =>
    {
        var path = WriteWav(Path.Combine(NewFolder("cache"), "cached.wav"), 1);
        var store = NewStore("cache");
        await store.ImportAsync([path]);
        var reads = store.MetadataReadCount;
        await store.RefreshAsync();
        Equal(reads, store.MetadataReadCount);
        WriteWav(path, 2);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        var refreshed = await store.RefreshAsync();
        Equal(reads + 1, store.MetadataReadCount);
        True(Math.Abs(refreshed.Tracks.Single().Duration.TotalSeconds - 2) < .02);
        // Metadata handles must have been disposed after import.
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    });

    await Run("preserves missing files and relinks all references without deleting audio", async () =>
    {
        var folder = NewFolder("relink");
        var oldPath = WriteWav(Path.Combine(folder, "old.wav"), 1);
        var store = NewStore("relink");
        await store.ImportAsync([folder]);
        var album = new CustomAlbum { Title = "A", TrackPaths = [oldPath] };
        var playlist = new CustomPlaylist { Name = "P", TrackPaths = [oldPath] };
        store.SaveAlbum(album);
        store.SavePlaylist(playlist);
        store.SetFavorite(oldPath, true);
        store.RecordListening(oldPath);
        store.SaveSession(new PlaybackSession { QueuePaths = [oldPath, oldPath], CurrentIndex = 1, PositionSeconds = .4, RepeatMode = "All" });
        File.Delete(oldPath);
        var missing = await store.RefreshAsync();
        Equal(1, missing.Tracks.Count);
        True(missing.Tracks.Single().IsMissing);
        Equal(1, missing.Folders.Count);
        var replacement = WriteWav(Path.Combine(folder, "new.wav"), 1);
        store.RelinkTrack(oldPath, replacement);
        var relinked = store.Load();
        Equal(replacement, relinked.Tracks.Single().Path);
        True(relinked.Tracks.Single().IsFavorite);
        Equal(replacement, relinked.CustomAlbums.Single().TrackPaths.Single());
        Equal(replacement, relinked.Playlists.Single().TrackPaths.Single());
        Equal(replacement, relinked.History.Single().Path);
        Equal(2, relinked.Session.QueuePaths.Count);
        Equal(1, relinked.Session.CurrentIndex);
        True(relinked.Session.QueuePaths.All(path => path == replacement));
        True(File.Exists(replacement));
    });

    await Run("loads cached availability and updates it only during background refresh", async () =>
    {
        var path = WriteWav(Path.Combine(NewFolder("cached-availability"), "cached.wav"), .1);
        var dataRoot = DataRoot("cached-availability");
        var store = new LibraryStore(dataRoot);
        await store.ImportAsync([path]);
        File.Delete(path);
        True(!store.Load().Tracks.Single().IsMissing);
        var restarted = new LibraryStore(dataRoot);
        True(!restarted.Load().Tracks.Single().IsMissing);
        True((await restarted.RefreshAsync()).Tracks.Single().IsMissing);
        WriteWav(path, .1);
        True(restarted.Load().Tracks.Single().IsMissing);
        True(!(await restarted.RefreshAsync()).Tracks.Single().IsMissing);
    });

    await Run("recovers a damaged library from the atomic backup", async () =>
    {
        var path = WriteWav(Path.Combine(NewFolder("backup"), "backup.wav"), 1);
        var dataRoot = DataRoot("backup");
        var store = new LibraryStore(dataRoot);
        await store.ImportAsync([path]);
        store.SaveAlbum(new CustomAlbum { Title = "Keep this album", TrackPaths = [path] });
        store.SavePlaylist(new CustomPlaylist { Name = "Latest write", TrackPaths = [path] });
        File.WriteAllText(Path.Combine(dataRoot, "library.json"), "{\"truncated\":");
        var recovered = new LibraryStore(dataRoot).Load();
        Equal(1, recovered.Tracks.Count);
        Equal("Keep this album", recovered.CustomAlbums.Single().Title);
        True(recovered.RecoveryNotice?.Contains("backup") == true);
        True(Directory.EnumerateFiles(dataRoot, "library.json.corrupt-*").Any());
        // A second failure should still have a known-good backup, even if no changes
        // were made after recovery.
        File.WriteAllText(Path.Combine(dataRoot, "library.json"), "{");
        var recoveredAgain = new LibraryStore(dataRoot).Load();
        Equal("Keep this album", recoveredAgain.CustomAlbums.Single().Title);
    });

    await Run("cancels an import without committing a partial library", async () =>
    {
        var folder = NewFolder("cancel");
        for (var i = 0; i < 20; i++)
            WriteWav(Path.Combine(folder, $"track-{i}.wav"), .03);
        var store = NewStore("cancel");
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(update => { if (update.Done >= 1) cancellation.Cancel(); });
        try
        {
            await store.ImportAsync([folder], progress, cancellation.Token);
            throw new Exception("Expected cancellation.");
        }
        catch (OperationCanceledException) { }
        Equal(0, store.Load().Tracks.Count);
        Equal(20, Directory.EnumerateFiles(folder).Count());
    });

    await Run("keeps unreadable files visible and retries when repaired", async () =>
    {
        var path = Path.Combine(NewFolder("corrupt"), "broken.wav");
        File.WriteAllText(path, "This is not a wave file.");
        var store = NewStore("corrupt");
        var broken = await store.ImportAsync([path]);
        True(!broken.Tracks.Single().CanPlay);
        True(!string.IsNullOrWhiteSpace(broken.Tracks.Single().Error));
        WriteWav(path, .2);
        var repaired = await store.RefreshAsync();
        True(repaired.Tracks.Single().CanPlay);
    });

    await Run("retries unchanged audio after a transient file lock is released", async () =>
    {
        var path = WriteWav(Path.Combine(NewFolder("transient-lock"), "locked.wav"), .1);
        var store = NewStore("transient-lock");
        using (var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unavailable = await store.ImportAsync([path]);
            True(!unavailable.Tracks.Single().CanPlay);
            True(!string.IsNullOrWhiteSpace(unavailable.Tracks.Single().Error));
        }
        var ready = await store.RefreshAsync();
        True(ready.Tracks.Single().CanPlay);
        Equal(44100, ready.Tracks.Single().SampleRate);
    });

    await Run("recognizes ALAC sample entries rather than matching text in media data", () =>
    {
        var folder = NewFolder("mp4");
        var alac = Path.Combine(folder, "alac.m4a");
        File.WriteAllBytes(alac, Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stsd",
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }.Concat(Box("alac", new byte[28])).ToArray())))))));
        True(LibraryStore.IsSupported(alac));
        var aac = Path.Combine(folder, "aac.m4a");
        var moov = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stsd",
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }.Concat(Box("mp4a", new byte[28])).ToArray()))))));
        File.WriteAllBytes(aac, Box("mdat", Encoding.ASCII.GetBytes("accidental alac text")).Concat(moov).ToArray());
        True(!LibraryStore.IsSupported(aac));
        var bad = Path.Combine(folder, "bad.m4a");
        File.WriteAllText(bad, "alac");
        True(!LibraryStore.IsSupported(bad));
        var undeclared = Path.Combine(folder, "undeclared.m4a");
        File.WriteAllBytes(undeclared, Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stsd",
            new byte[8].Concat(Box("alac", new byte[28])).ToArray())))))));
        True(!LibraryStore.IsSupported(undeclared));
        return Task.CompletedTask;
    });

    await Run("classifies companded WAV as lossy instead of assuming WAV is lossless", async () =>
    {
        var path = WriteWav(Path.Combine(NewFolder("mulaw"), "mulaw.wav"), 1, 7);
        var snapshot = await NewStore("mulaw").ImportAsync([path]);
        var track = snapshot.Tracks.Single();
        True(track.CanPlay);
        Equal(false, track.IsLossless);
        Equal("Lossy", track.QualityLabel);
        Equal(0, track.BitDepth);
    });

    await Run("merges edits made while a background scan is in progress", async () =>
    {
        var folder = NewFolder("concurrency");
        var path = WriteWav(Path.Combine(folder, "first.wav"), .1);
        WriteWav(Path.Combine(folder, "second.wav"), .1);
        var store = NewStore("concurrency");
        var edited = false;
        var progress = new ImmediateProgress(update =>
        {
            if (update.Done == 1 && !edited)
            {
                edited = true;
                store.SavePlaylist(new CustomPlaylist { Name = "During scan", TrackPaths = [path] });
                store.SetFavorite(path, true);
                store.SaveSession(new PlaybackSession { QueuePaths = [path], CurrentIndex = 0, PositionSeconds = .06 });
            }
        });
        var result = await store.ImportAsync([folder], progress);
        Equal("During scan", result.Playlists.Single().Name);
        True(result.Tracks.Single(track => track.Path == path).IsFavorite);
        Equal(.06, result.Session.PositionSeconds);
    });

    await Run("returns independent snapshots and restores listening settings", async () =>
    {
        var path = WriteWav(Path.Combine(NewFolder("snapshot"), "safe.wav"), .1);
        var dataRoot = DataRoot("snapshot");
        var store = new LibraryStore(dataRoot);
        await store.ImportAsync([path]);
        store.SaveAlbum(new CustomAlbum { Title = "Safe album", TrackPaths = [path] });
        store.SetFavorite(path, true);
        store.SaveSession(new PlaybackSession { QueuePaths = [path], CurrentIndex = 0, Volume = .35, RepeatMode = "One", ReducedMotion = true });
        var first = store.Load();
        first.CustomAlbums[0].TrackPaths.Clear();
        first.Session.QueuePaths.Clear();
        first.Tracks[0].IsFavorite = false;
        var second = new LibraryStore(dataRoot).Load();
        Equal(1, second.CustomAlbums[0].TrackPaths.Count);
        Equal(1, second.Session.QueuePaths.Count);
        Equal(.35, second.Session.Volume);
        Equal("One", second.Session.RepeatMode);
        True(second.Session.ReducedMotion && second.Tracks[0].IsFavorite);
    });

    await Run("does not resurrect a relinked path when its scan completes", async () =>
    {
        var folder = NewFolder("relink-scan");
        var oldPath = WriteWav(Path.Combine(folder, "old.wav"), .1);
        var store = NewStore("relink-scan");
        await store.ImportAsync([oldPath]);
        var newPath = Path.Combine(folder, "new.wav");
        File.Move(oldPath, newPath);
        var relinked = false;
        var progress = new ImmediateProgress(update =>
        {
            if (update.Done == 1 && !relinked)
            {
                relinked = true;
                store.RelinkTrack(oldPath, newPath);
            }
        });
        var scanned = await store.RefreshAsync(progress);
        Equal(newPath, scanned.Tracks.Single().Path);
        WriteWav(oldPath, .1);
        var importedAgain = await store.ImportAsync([oldPath]);
        Equal(2, importedAgain.Tracks.Count);
    });

    await Run("repairs nullable entries in a readable saved library", () =>
    {
        var dataRoot = DataRoot("nullable");
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "library.json"),
            """{"TrackPaths":[null,"\u0000"],"FolderPaths":null,"CustomAlbums":[null,{"Id":null,"TrackPaths":null}],"Playlists":[null,{"TrackPaths":null}],"History":[null,{"Path":null}],"MetadataCache":{"invalid":null},"Session":{"QueuePaths":null,"CurrentIndex":999}}""");
        var loaded = new LibraryStore(dataRoot).Load();
        Equal(0, loaded.Tracks.Count);
        Equal(1, loaded.CustomAlbums.Count);
        Equal(1, loaded.Playlists.Count);
        Equal(0, loaded.History.Count);
        Equal(-1, loaded.Session.CurrentIndex);
        var store = new LibraryStore(dataRoot);
        store.SaveSession(new PlaybackSession { PositionSeconds = 1e300, Volume = double.PositiveInfinity });
        var session = new LibraryStore(dataRoot).GetSession();
        True(session.PositionSeconds <= TimeSpan.FromDays(366).TotalSeconds);
        Equal(.72, session.Volume);
        return Task.CompletedTask;
    });

    await Run("rolls back memory when an atomic library write fails", async () =>
    {
        var path = WriteWav(Path.Combine(NewFolder("failed-write"), "song.wav"), .1);
        var dataRoot = DataRoot("failed-write");
        var store = new LibraryStore(dataRoot);
        await store.ImportAsync([path]);
        using (var locked = File.Open(Path.Combine(dataRoot, "library.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { store.SetFavorite(path, true); throw new Exception("Expected a sharing failure."); }
            catch (IOException) { }
            True(!store.Load().Tracks.Single().IsFavorite);
            try { store.SaveAlbum(new CustomAlbum { Title = "Not saved", TrackPaths = [path] }); throw new Exception("Expected a sharing failure."); }
            catch (IOException) { }
            Equal(0, store.Load().CustomAlbums.Count);
        }
        var loaded = new LibraryStore(dataRoot).Load();
        True(!loaded.Tracks.Single().IsFavorite);
        Equal(0, loaded.CustomAlbums.Count);
    });

    await Run("folder watcher reports a newly added audio file", async () =>
    {
        var folder = NewFolder("watch");
        using var watcher = new LibraryWatcher(100);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += (_, _) => changed.TrySetResult();
        watcher.Configure([folder]);
        WriteWav(Path.Combine(folder, "added.wav"), .02);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    });

    if (args.Length > 0)
    {
        await Run("reads generated WAV, FLAC, ALAC, and MP3 audio fixtures", async () =>
        {
            var source = Path.GetFullPath(args[0]);
            var paths = new[] { "fixture.wav", "fixture.flac", "fixture.m4a", "fixture.mp3" }
                .Select(name => Path.Combine(source, name)).ToArray();
            var snapshot = await NewStore("real-codecs").ImportAsync(paths);
            Equal(4, snapshot.Tracks.Count);
            foreach (var track in snapshot.Tracks)
            {
                True(track.CanPlay);
                Equal(48000, track.SampleRate);
                Equal(2, track.Channels);
                if (track.Format == "MP3")
                {
                    Equal(false, track.IsLossless);
                    Equal(0, track.BitDepth);
                    Equal(320, track.BitRateKbps);
                }
                else
                {
                    Equal(true, track.IsLossless);
                    Equal(24, track.BitDepth);
                }
                Console.WriteLine($"  {track.Format}: {track.Codec}, {track.QualitySummary}");
            }
            Equal("ALAC", snapshot.Tracks.Single(track => track.Path.EndsWith(".m4a")).Format);
        });
    }

    Console.WriteLine($"PASS: {passed} library checks.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    var resolved = Path.GetFullPath(root);
    if (resolved.StartsWith(fixtureParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        Directory.Delete(resolved, true);
}

async Task Run(string name, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine("PASS " + name);
}

string NewFolder(string name)
{
    var path = Path.Combine(root, "music-" + name);
    Directory.CreateDirectory(path);
    return path;
}
string DataRoot(string name) => Path.Combine(root, "state-" + name);
LibraryStore NewStore(string name) => new(DataRoot(name));
void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }

static string WriteWav(string path, double seconds, ushort format = 1)
{
    var channels = format == 1 ? (ushort)2 : (ushort)1;
    var bits = format == 1 ? (ushort)16 : (ushort)8;
    var sampleRate = format == 1 ? 44100 : 8000;
    var frames = (int)(sampleRate * seconds);
    var bytes = frames * channels * bits / 8;
    using var writer = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None));
    writer.Write(Encoding.ASCII.GetBytes("RIFF"));
    writer.Write(36 + bytes);
    writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
    writer.Write(16);
    writer.Write(format);
    writer.Write(channels);
    writer.Write(sampleRate);
    writer.Write(sampleRate * channels * bits / 8);
    writer.Write((ushort)(channels * bits / 8));
    writer.Write(bits);
    writer.Write(Encoding.ASCII.GetBytes("data"));
    writer.Write(bytes);
    for (var i = 0; i < bytes; i++)
        writer.Write((byte)0);
    return Path.GetFullPath(path);
}

static byte[] Box(string type, byte[] content)
{
    var result = new byte[8 + content.Length];
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)result.Length);
    Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
    content.CopyTo(result, 8);
    return result;
}

sealed class ImmediateProgress(Action<LibraryProgress> handler) : IProgress<LibraryProgress>
{
    public void Report(LibraryProgress value) => handler(value);
}
