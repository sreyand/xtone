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

    await Run("one configured root isolates active tracks while preserving legacy collections and references", async () =>
    {
        var legacy = WriteWav(Path.Combine(NewFolder("legacy-root"), "old.wav"), .05);
        var music = NewFolder("configured-root");
        var albumFolder = Path.Combine(music, "Physical album"); Directory.CreateDirectory(albumFolder);
        var active = WriteWav(Path.Combine(albumFolder, "new.wav"), .05);
        var store = NewStore("root-migration");
        await store.ImportAsync([legacy]);
        store.SaveAlbum(new CustomAlbum { Title = "Legacy collection", TrackPaths = [legacy] });
        store.SavePlaylist(new CustomPlaylist { Name = "Legacy playlist", TrackPaths = [legacy] });
        store.SetFavorite(legacy, true); store.RecordListening(legacy);
        store.SaveSession(new PlaybackSession { QueuePaths = [legacy, legacy], CurrentIndex = 1 });
        var snapshot = await store.ConfigureMusicFolderAsync(music);
        Equal(music, snapshot.MusicRoot); Equal(1, snapshot.Tracks.Count); Equal(active, snapshot.Tracks.Single().Path);
        Equal(1, snapshot.CustomAlbums.Count); Equal(1, snapshot.Playlists.Count);
        Equal(legacy, snapshot.History.Single().Path); Equal(2, snapshot.Session.QueuePaths.Count);
        var saved = File.ReadAllText(Path.Combine(DataRoot("root-migration"), "library.json"));
        True(saved.Contains("old.wav") && saved.Contains("Legacy playlist"));
        var restarted = new LibraryStore(DataRoot("root-migration")).Load();
        Equal(music, restarted.MusicRoot); Equal(active, restarted.Tracks.Single().Path);
        var restored = await store.ConfigureMusicFolderAsync(Path.GetDirectoryName(legacy)!);
        True(restored.Tracks.Single().IsFavorite);
    });

    await Run("folder albums ignore release tags, merge nested discs, and leave loose tracks ungrouped", async () =>
    {
        var music = NewFolder("folder-albums");
        var disc = Path.Combine(music, "Folder title", "Disc 2"); Directory.CreateDirectory(disc);
        var first = WriteWav(Path.Combine(Path.GetDirectoryName(disc)!, "01.wav"), .05);
        var second = WriteWav(Path.Combine(disc, "02.wav"), .05);
        var loose = WriteWav(Path.Combine(music, "loose.wav"), .05);
        var store = NewStore("folder-albums");
        var snapshot = await store.ConfigureMusicFolderAsync(music);
        var tagged = snapshot.Tracks.Select(track => track with { Album = track.Path == first ? "Wrong tag A" : "Wrong tag B" }).ToList();
        var albums = FolderLibrary.BuildAlbums(tagged, music);
        Equal(1, albums.Count); Equal("Folder title", albums.Single().Title); Equal(2, albums.Single().Tracks.Count);
        Equal(Path.GetDirectoryName(disc), albums.Single().FolderPath);
        True(albums.Single().Tracks.All(track => track.Path != loose));
        var id = snapshot.Tracks.Single(track => track.Path == first).Id;
        Equal(id, (await store.RefreshAsync()).Tracks.Single(track => track.Path == first).Id);
        True(snapshot.Tracks.Any(track => track.Path == second));
    });

    await Run("explicit artist-album layout uses second-level folders and folder artwork wins", async () =>
    {
        var music = NewFolder("artist-layout");
        var album = Path.Combine(music, "Artist", "Release"); Directory.CreateDirectory(album);
        var path = WriteWav(Path.Combine(album, "song.wav"), .05);
        var cover = Path.Combine(album, "COVER.PNG"); File.WriteAllBytes(cover, [1, 2, 3]);
        var snapshot = await NewStore("artist-layout").ConfigureMusicFolderAsync(music, "ArtistAlbumFolders");
        Equal("ArtistAlbumFolders", snapshot.AlbumLayout);
        var cards = FolderLibrary.BuildAlbums(snapshot.Tracks, music, snapshot.AlbumLayout);
        Equal("Release", cards.Single().Title); Equal(cover, cards.Single().ArtworkPath);
        var looseArtistTrack = new Track { Path = Path.Combine(music, "Artist", "loose.wav") };
        True(FolderLibrary.BuildAlbums([looseArtistTrack], music, snapshot.AlbumLayout).All(card => card.Tracks.Count == 0));
        True(FolderLibrary.BuildAlbums([new Track { Path = music + "-sibling\\song.wav" }], music).All(card => card.Tracks.Count == 0));
    });

    await Run("canceling a root switch preserves the configured root and completed library", async () =>
    {
        var first = NewFolder("root-before-cancel"); WriteWav(Path.Combine(first, "before.wav"), .05);
        var second = NewFolder("root-cancelled"); WriteWav(Path.Combine(second, "after.wav"), .05);
        var store = NewStore("root-cancel"); await store.ConfigureMusicFolderAsync(first);
        using var cancel = new CancellationTokenSource();
        var progress = new ImmediateProgress(update => { if (update.Done > 0) cancel.Cancel(); });
        try { await store.ConfigureMusicFolderAsync(second, "ArtistAlbumFolders", progress, cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Equal(first, store.Load().MusicRoot); Equal("AlbumFolders", store.Load().AlbumLayout);
        Equal("before", store.Load().Tracks.Single().Title);
    });

    await Run("a disconnected configured root remains configured and reconnects", async () =>
    {
        var music = NewFolder("disconnect"); var path = WriteWav(Path.Combine(music, "song.wav"), .05);
        var store = NewStore("disconnect"); await store.ConfigureMusicFolderAsync(music);
        var moved = music + "-offline"; Directory.Move(music, moved);
        var offline = await store.RefreshAsync();
        Equal(music, offline.MusicRoot); True(offline.Tracks.Single().IsMissing);
        Directory.Move(moved, music);
        True(!(await store.RefreshAsync()).Tracks.Single().IsMissing);
        True(File.Exists(path));
    });

    await Run("structured artist identities retain punctuation and ambiguous legacy credits", () =>
    {
        var track = new Track { Artist = "Misleading display", Performers = ["AC/DC", "Earth, Wind & Fire", "AC/DC", "  Guest  "] };
        Equal(3, track.ArtistCredits.Count); Equal("Earth, Wind & Fire", track.ArtistCredits[1]); Equal("Guest", track.ArtistCredits[2]);
        var ambiguous = new Track { Artist = "Fred again.., Skepta & LYNY" };
        Equal(1, ambiguous.ArtistCredits.Count); Equal(ambiguous.Artist, ambiguous.ArtistCredits.Single());
        Equal(0, new Track().ArtistCredits.Count);
        return Task.CompletedTask;
    });

    await Run("metadata reader version rereads unchanged files and keeps stable identity", async () =>
    {
        var music = NewFolder("metadata-version"); var path = WriteWav(Path.Combine(music, "song.wav"), .05);
        using (var file = TagLib.File.Create(path)) { file.Tag.Performers = ["AC/DC", "Earth, Wind & Fire"]; file.Save(); }
        var data = DataRoot("metadata-version"); var store = new LibraryStore(data);
        var before = (await store.ConfigureMusicFolderAsync(music)).Tracks.Single();
        True(before.Performers.Length > 0); True(before.ArtistCredits.Contains("AC/DC"));
        var jsonPath = Path.Combine(data, "library.json");
        File.WriteAllText(jsonPath, File.ReadAllText(jsonPath).Replace("\"MetadataVersion\": 2", "\"MetadataVersion\": 0"));
        var restarted = new LibraryStore(data); restarted.Load();
        var after = (await restarted.RefreshAsync()).Tracks.Single();
        Equal(1L, restarted.MetadataReadCount); Equal(before.Id, after.Id); Equal(2, after.MetadataVersion);
    });

    await Run("returned artist arrays are independent and duplicate Unicode spellings normalize", async () =>
    {
        var music = NewFolder("snapshot-arrays"); var path = WriteWav(Path.Combine(music, "song.wav"), .05);
        using (var file = TagLib.File.Create(path)) { file.Tag.Performers = ["Artist"]; file.Save(); }
        var store = NewStore("snapshot-arrays"); var snapshot = await store.ConfigureMusicFolderAsync(music);
        snapshot.Tracks.Single().Performers[0] = "Mutated";
        Equal("Artist", store.Load().Tracks.Single().Performers.Single());
        Equal(1, ArtistCredits.Clean(["Beyonc\u00e9", "Beyonce\u0301"]).Length);
    });

    await Run("deleted albums disappear while missing root retains cached folder albums", async () =>
    {
        var music = NewFolder("deleted-album"); var album = Path.Combine(music, "Release"); Directory.CreateDirectory(album);
        var path = WriteWav(Path.Combine(album, "song.wav"), .05); var store = NewStore("deleted-album");
        var snapshot = await store.ConfigureMusicFolderAsync(music); Equal(1, FolderLibrary.BuildAlbums(snapshot.Tracks, music).Count);
        File.Delete(path); Directory.Delete(album);
        snapshot = await store.RefreshAsync(); Equal(0, FolderLibrary.BuildAlbums(snapshot.Tracks, music).Count);
        Equal(1, snapshot.Tracks.Count); True(snapshot.Tracks.Single().IsMissing);
        var offline = music + "-offline"; Directory.Move(music, offline);
        snapshot = await store.RefreshAsync(); Equal(1, FolderLibrary.BuildAlbums(snapshot.Tracks, music).Count);
        Directory.Move(offline, music);
    });

    await Run("invalid replacement root leaves the last committed configuration intact", async () =>
    {
        var music = NewFolder("invalid-root"); WriteWav(Path.Combine(music, "song.wav"), .05);
        var store = NewStore("invalid-root"); await store.ConfigureMusicFolderAsync(music);
        try { await store.ConfigureMusicFolderAsync(Path.Combine(music, "does-not-exist")); throw new Exception("Expected missing folder error"); }
        catch (DirectoryNotFoundException) { }
        Equal(music, store.Load().MusicRoot); Equal(1, store.Load().Tracks.Count);
    });

    await Run("version two migration keeps a permanent original-state copy", async () =>
    {
        var music = NewFolder("version-two"); var path = WriteWav(Path.Combine(music, "song.wav"), .05);
        var data = DataRoot("version-two"); var store = new LibraryStore(data); await store.ImportAsync([path]);
        store.SavePlaylist(new CustomPlaylist { Name = "Preserved playlist", TrackPaths = [path] });
        var statePath = Path.Combine(data, "library.json");
        File.WriteAllText(statePath, File.ReadAllText(statePath).Replace("\"Version\": 3", "\"Version\": 2"));
        var original = File.ReadAllText(statePath); var restarted = new LibraryStore(data);
        Equal(1, restarted.Load().Playlists.Count); Equal<string?>(null, restarted.Load().MusicRoot);
        Equal(original, File.ReadAllText(statePath + ".pre-folder-migration"));
        await restarted.ConfigureMusicFolderAsync(music);
        Equal(original, File.ReadAllText(statePath + ".pre-folder-migration"));
    });

    await Run("snapshot revisions advance after committed library edits but not rollback", async () =>
    {
        var music = NewFolder("revisions"); var path = WriteWav(Path.Combine(music, "song.wav"), .05);
        var data = DataRoot("revisions"); var store = new LibraryStore(data);
        var initial = store.Load();
        var configured = await store.ConfigureMusicFolderAsync(music);
        True(configured.Revision > initial.Revision);
        store.SetFavorite(path, true); var favorite = store.Load();
        True(favorite.Revision > configured.Revision);
        using (var locked = File.Open(Path.Combine(data, "library.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { store.SetFavorite(path, false); throw new Exception("Expected sharing failure"); }
            catch (IOException) { }
            Equal(favorite.Revision, store.Load().Revision); True(store.Load().Tracks.Single().IsFavorite);
        }
        store.SaveSession(new PlaybackSession { Volume = .4 });
        Equal(favorite.Revision, store.Load().Revision);
        var anotherRoot = NewFolder("revision-switch");
        var changedRoot = await store.ConfigureMusicFolderAsync(anotherRoot);
        True(changedRoot.Revision > favorite.Revision);
    });

    await Run("physical album creation includes empty folders and rejects invalid Windows names", async () =>
    {
        var music = NewFolder("create-album"); var store = NewStore("create-album");
        await store.ConfigureMusicFolderAsync(music);
        var before = store.Load().Revision; var folder = store.CreateMusicFolder("Evening sessions");
        True(Directory.Exists(folder)); True(store.Load().Revision > before);
        var albums = FolderLibrary.BuildAlbums(store.Load().Tracks, music);
        Equal("Evening sessions", albums.Single().Title); Equal(0, albums.Single().Tracks.Count);
        try { store.CreateMusicFolder("Evening sessions"); throw new Exception("Expected collision"); } catch (IOException) { }
        foreach (var name in new[] { "", " ", ".", "..", "../escape", "a\\b", "a:b", "CON", "con.txt", "AUX", "NUL", "COM1", "LPT9", "COM¹", "ends.", "ends " })
            try { store.CreateMusicFolder(name); throw new Exception($"Accepted invalid folder name {name}"); } catch (ArgumentException) { }
        try { store.CreateMusicFolder("Outside", root); throw new Exception("Expected containment failure"); } catch (IOException) { }
        var artist = store.CreateMusicFolder("Artist"); var release = store.CreateMusicFolder("Release", artist);
        Equal(release, MusicFileOrganizer.ValidateFolderPath(music, "Release", artist));
        True(FolderLibrary.BuildAlbums([], music, "ArtistAlbumFolders").Any(album => album.FolderPath == release && album.Tracks.Count == 0));
    });

    await Run("moving tracks preserves IDs, favorites, history, duplicate queue entries and legacy collections", async () =>
    {
        var music = NewFolder("move-tracks"); var path = WriteWav(Path.Combine(music, "song.wav"), .05);
        var data = DataRoot("move-tracks"); var store = new LibraryStore(data); var old = (await store.ConfigureMusicFolderAsync(music)).Tracks.Single();
        store.SetFavorite(path, true); store.RecordListening(path);
        store.SaveAlbum(new CustomAlbum { Title = "A", TrackPaths = [path] });
        store.SavePlaylist(new CustomPlaylist { Name = "P", TrackPaths = [path] });
        store.SaveSession(new PlaybackSession { QueuePaths = [path, path], CurrentIndex = 1, PositionSeconds = .02 });
        var destination = store.CreateMusicFolder("Collection"); var target = Path.Combine(destination, "song.wav");
        var snapshot = await store.MoveTracksToFolderAsync([path], destination);
        True(!File.Exists(path) && File.Exists(target)); Equal(old.Id, snapshot.Tracks.Single().Id);
        True(snapshot.Tracks.Single().IsFavorite); Equal(target, snapshot.History.Single().Path);
        Equal(target, snapshot.CustomAlbums.Single().TrackPaths.Single()); Equal(target, snapshot.Playlists.Single().TrackPaths.Single());
        True(snapshot.Session.QueuePaths.SequenceEqual(new[] { target, target })); Equal(1, snapshot.Session.CurrentIndex);
        var restarted = new LibraryStore(data).Load();
        True(restarted.Session.QueuePaths.SequenceEqual(new[] { target, target })); Equal(old.Id, restarted.Tracks.Single().Id);
        var reads = store.MetadataReadCount; await store.RefreshAsync(); Equal(reads, store.MetadataReadCount);
    });

    await Run("external audio is explicitly copied without changing its original", async () =>
    {
        var music = NewFolder("copy-tracks"); var external = NewFolder("copy-source");
        var original = WriteWav(Path.Combine(external, "song.wav"), .05); var bytes = File.ReadAllBytes(original);
        var store = NewStore("copy-tracks"); await store.ConfigureMusicFolderAsync(music); var album = store.CreateMusicFolder("Album");
        var copied = await store.CopyFilesToFolderAsync([original], album); var target = Path.Combine(album, "song.wav");
        True(File.Exists(original) && File.Exists(target)); True(bytes.SequenceEqual(File.ReadAllBytes(original)));
        True(bytes.SequenceEqual(File.ReadAllBytes(target))); Equal(target, copied.Tracks.Single().Path);
        True(copied.Tracks.Single().CanPlay); Equal(1, FolderLibrary.BuildAlbums(copied.Tracks, music).Single().Tracks.Count);
        try { await store.CopyFilesToFolderAsync([original], album); throw new Exception("Expected collision"); } catch (IOException) { }
        True(bytes.SequenceEqual(File.ReadAllBytes(target)));
    });

    await Run("transfer preflight rejects collisions, duplicate names and paths outside the configured root", async () =>
    {
        var music = NewFolder("move-validation"); var a = Path.Combine(music, "A"); var b = Path.Combine(music, "B"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        var first = WriteWav(Path.Combine(a, "same.wav"), .05); var second = WriteWav(Path.Combine(b, "same.wav"), .05);
        var store = NewStore("move-validation"); await store.ConfigureMusicFolderAsync(music); var destination = store.CreateMusicFolder("Destination");
        try { await store.MoveTracksToFolderAsync([first, second], destination); throw new Exception("Expected duplicate collision"); } catch (IOException) { }
        True(File.Exists(first) && File.Exists(second)); Equal(0, Directory.GetFiles(destination).Length);
        try { await store.MoveTracksToFolderAsync([first], root); throw new Exception("Expected outside destination error"); } catch (IOException) { }
        var outside = WriteWav(Path.Combine(NewFolder("outside-source"), "external.wav"), .05);
        try { await store.MoveTracksToFolderAsync([outside], destination); throw new Exception("Expected outside source error"); } catch (IOException) { }
        True(File.Exists(outside));
        File.WriteAllText(Path.Combine(destination, "same.wav"), "untouched");
        try { await store.MoveTracksToFolderAsync([first], destination); throw new Exception("Expected existing target error"); } catch (IOException) { }
        Equal("untouched", File.ReadAllText(Path.Combine(destination, "same.wav")));
        var revision = store.Load().Revision; await store.MoveTracksToFolderAsync([first], a); Equal(revision, store.Load().Revision);
    });

    await Run("cancelled move batches return files and leave references unchanged", async () =>
    {
        var music = NewFolder("move-cancel"); var first = WriteWav(Path.Combine(music, "first.wav"), .05); var second = WriteWav(Path.Combine(music, "second.wav"), .05);
        var store = NewStore("move-cancel"); await store.ConfigureMusicFolderAsync(music); var destination = store.CreateMusicFolder("Target"); var revision = store.Load().Revision;
        using var cancellation = new CancellationTokenSource(); var progress = new ImmediateProgress(update => { if (update.Done == 1) cancellation.Cancel(); });
        try { await store.MoveTracksToFolderAsync([first, second], destination, progress, cancellation.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        True(File.Exists(first) && File.Exists(second)); Equal(0, Directory.GetFiles(destination).Length); Equal(revision, store.Load().Revision);
        True(store.Load().Tracks.All(track => track.Path == first || track.Path == second));
    });

    await Run("failed persistence rolls back physical moves and new copies", async () =>
    {
        var music = NewFolder("transfer-rollback"); var path = WriteWav(Path.Combine(music, "move.wav"), .05);
        var external = WriteWav(Path.Combine(NewFolder("rollback-source"), "copy.wav"), .05);
        var data = DataRoot("transfer-rollback"); var store = new LibraryStore(data); await store.ConfigureMusicFolderAsync(music); var target = store.CreateMusicFolder("Target");
        var revision = store.Load().Revision; var stateFile = Path.Combine(data, "library.json");
        using (var locked = File.Open(stateFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { await store.MoveTracksToFolderAsync([path], target); throw new Exception("Expected persistence failure"); } catch (IOException) { }
            True(File.Exists(path)); True(!File.Exists(Path.Combine(target, "move.wav"))); Equal(path, store.Load().Tracks.Single().Path);
            try { await store.CopyFilesToFolderAsync([external], target); throw new Exception("Expected persistence failure"); } catch (IOException) { }
            True(File.Exists(external)); Equal(0, Directory.GetFiles(target).Length); Equal(revision, store.Load().Revision);
        }
    });

    await Run("read-only source copy cancellation removes only new copies and preserves original attributes", async () =>
    {
        var music = NewFolder("readonly-copy"); var external = WriteWav(Path.Combine(NewFolder("readonly-source"), "read-only.wav"), .05);
        var store = NewStore("readonly-copy"); await store.ConfigureMusicFolderAsync(music); var target = store.CreateMusicFolder("Target");
        File.SetAttributes(external, FileAttributes.ReadOnly);
        try
        {
            using var cancel = new CancellationTokenSource(); var progress = new ImmediateProgress(update => cancel.Cancel());
            try { await store.CopyFilesToFolderAsync([external], target, progress, cancel.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            True(File.Exists(external)); True((File.GetAttributes(external) & FileAttributes.ReadOnly) != 0); Equal(0, Directory.GetFiles(target).Length);
        }
        finally { File.SetAttributes(external, FileAttributes.Normal); }
    });

    await Run("unreadable copied audio rolls back every newly created target", async () =>
    {
        var music = NewFolder("copy-invalid"); var external = NewFolder("invalid-copy-source");
        var valid = WriteWav(Path.Combine(external, "valid.wav"), .05); var invalid = Path.Combine(external, "broken.mp3"); File.WriteAllText(invalid, "This is not audio");
        var store = NewStore("copy-invalid"); await store.ConfigureMusicFolderAsync(music); var destination = store.CreateMusicFolder("Target");
        var revision = store.Load().Revision;
        try { await store.CopyFilesToFolderAsync([valid, invalid], destination); throw new Exception("Expected unreadable audio error"); } catch (InvalidDataException) { }
        True(File.Exists(valid) && File.Exists(invalid)); Equal(0, Directory.GetFiles(destination).Length);
        Equal(0, store.Load().Tracks.Count); Equal(revision, store.Load().Revision);
    });

    await Run("Music grid/list preference survives restart and invalid values reset to Grid", () =>
    {
        var data = DataRoot("music-view"); var store = new LibraryStore(data);
        Equal("Grid", store.GetSession().MusicView);
        store.SaveSession(new PlaybackSession { MusicView = "List" });
        Equal("List", new LibraryStore(data).GetSession().MusicView);
        store.SaveSession(new PlaybackSession { MusicView = "Unsupported" });
        Equal("Grid", new LibraryStore(data).GetSession().MusicView);
        store.SaveSession(new PlaybackSession { MusicView = "Grid" });
        Equal("Grid", store.Load().Session.MusicView);
        return Task.CompletedTask;
    });

    await Run("folder pins preserve order, deduplicate, survive restart and unpin without deleting folders", async () =>
    {
        var music = NewFolder("folder-pins"); var data = DataRoot("folder-pins"); var store = new LibraryStore(data);
        await store.ConfigureMusicFolderAsync(music);
        var first = store.CreateMusicFolder("First"); var second = store.CreateMusicFolder("Second");
        store.SetFolderPinned(first, true); store.SetFolderPinned(second, true); store.SetFolderPinned(first.ToUpperInvariant() + Path.DirectorySeparatorChar, true);
        True(store.Load().PinnedFolderPaths.SequenceEqual(new[] { first, second }));
        var restarted = new LibraryStore(data); True(restarted.Load().PinnedFolderPaths.SequenceEqual(new[] { first, second }));
        restarted.SetFolderPinned(first, false); True(Directory.Exists(first)); Equal(second, restarted.Load().PinnedFolderPaths.Single());
        var independent = restarted.Load(); independent.PinnedFolderPaths.Clear(); Equal(1, restarted.Load().PinnedFolderPaths.Count);
    });

    await Run("folder pin scope excludes the root and external folders but retains inactive root pins", async () =>
    {
        var music = NewFolder("pin-scope"); var store = NewStore("pin-scope"); await store.ConfigureMusicFolderAsync(music);
        var folder = store.CreateMusicFolder("Pinned"); store.SetFolderPinned(folder, true);
        try { store.SetFolderPinned(music, true); throw new Exception("Expected root rejection"); } catch (IOException) { }
        try { store.SetFolderPinned(root, true); throw new Exception("Expected outside rejection"); } catch (IOException) { }
        try { store.SetFolderPinned(Path.Combine(music, "Missing"), true); throw new Exception("Expected missing folder rejection"); } catch (DirectoryNotFoundException) { }
        var nextRoot = NewFolder("another-pin-root"); await store.ConfigureMusicFolderAsync(nextRoot);
        Equal(folder, store.Load().PinnedFolderPaths.Single());
        await store.ConfigureMusicFolderAsync(music); Equal(folder, store.Load().PinnedFolderPaths.Single());
        Directory.Delete(folder); store.SetFolderPinned(folder, false); Equal(0, store.Load().PinnedFolderPaths.Count);
    });

    await Run("failed pin persistence restores both saved pins and snapshot revision", async () =>
    {
        var music = NewFolder("pin-rollback"); var data = DataRoot("pin-rollback"); var store = new LibraryStore(data);
        await store.ConfigureMusicFolderAsync(music); var first = store.CreateMusicFolder("First"); var second = store.CreateMusicFolder("Second");
        store.SetFolderPinned(first, true); var revision = store.Load().Revision;
        using (var locked = File.Open(Path.Combine(data, "library.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { store.SetFolderPinned(second, true); throw new Exception("Expected save failure"); } catch (IOException) { }
            Equal(first, store.Load().PinnedFolderPaths.Single()); Equal(revision, store.Load().Revision);
            try { store.SetFolderPinned(first, false); throw new Exception("Expected save failure"); } catch (IOException) { }
            Equal(first, store.Load().PinnedFolderPaths.Single()); Equal(revision, store.Load().Revision);
        }
        Equal(first, new LibraryStore(data).Load().PinnedFolderPaths.Single());
    });

    await Run("renames a nested physical folder and preserves every saved track reference", async () =>
    {
        var music = NewFolder("rename-all"); var oldFolder = Path.Combine(music, "Old title"); var disc = Path.Combine(oldFolder, "Disc 1"); Directory.CreateDirectory(disc);
        var oldPath = WriteWav(Path.Combine(disc, "song.wav"), .2); File.WriteAllText(Path.Combine(oldFolder, "notes.txt"), "Keep liner notes");
        var data = DataRoot("rename-all"); var store = new LibraryStore(data); var initial = await store.ConfigureMusicFolderAsync(music);
        var id = initial.Tracks.Single().Id;
        store.SetFavorite(oldPath, true); store.RecordListening(oldPath);
        store.SaveAlbum(new CustomAlbum { Title = "legacy album", TrackPaths = [oldPath] });
        store.SavePlaylist(new CustomPlaylist { Name = "legacy playlist", TrackPaths = [oldPath, oldPath] });
        store.SetFolderPinned(oldFolder, true); store.SetFolderPinned(disc, true);
        var image = WriteImage(Path.Combine(NewFolder("rename-art"), "selected.png")); await store.SetFolderArtworkAsync(oldFolder, image);
        store.SaveSession(new PlaybackSession { QueuePaths = [oldPath, oldPath], CurrentIndex = 1, PositionSeconds = .12, MusicView = "List" });
        var renamed = await store.RenameMusicFolderAsync(oldFolder, "New title");
        var newFolder = Path.Combine(music, "New title"); var newPath = Path.Combine(newFolder, "Disc 1", "song.wav");
        True(!Directory.Exists(oldFolder) && Directory.Exists(newFolder) && File.Exists(newPath));
        Equal("Keep liner notes", File.ReadAllText(Path.Combine(newFolder, "notes.txt")));
        Equal(id, renamed.Tracks.Single().Id); Equal(newPath, renamed.Tracks.Single().Path); True(renamed.Tracks.Single().IsFavorite);
        Equal(newPath, renamed.History.Single().Path); Equal(newPath, renamed.CustomAlbums.Single().TrackPaths.Single());
        True(renamed.Playlists.Single().TrackPaths.All(path => path == newPath)); Equal(2, renamed.Session.QueuePaths.Count);
        True(renamed.Session.QueuePaths.All(path => path == newPath)); Equal(1, renamed.Session.CurrentIndex); Equal(.12, renamed.Session.PositionSeconds);
        True(renamed.PinnedFolderPaths.Contains(newFolder) && renamed.PinnedFolderPaths.Contains(Path.Combine(newFolder, "Disc 1")));
        True(!renamed.FolderArtworkPaths.ContainsKey(oldFolder) && File.Exists(renamed.FolderArtworkPaths[newFolder]));
        var restarted = new LibraryStore(data).Load(); Equal(newPath, restarted.Session.QueuePaths[0]); Equal(id, restarted.Tracks.Single().Id); Equal("List", restarted.Session.MusicView);
        var refreshed = await store.RefreshAsync(); Equal(1, refreshed.Tracks.Count); Equal(id, refreshed.Tracks.Single().Id);
    });

    await Run("rename rejects collisions reserved names root and case-only changes before mutation", async () =>
    {
        var music = NewFolder("rename-invalid"); var folder = Path.Combine(music, "Album"); Directory.CreateDirectory(folder); Directory.CreateDirectory(Path.Combine(music, "Taken"));
        var source = WriteWav(Path.Combine(folder, "song.wav"), .1); var store = NewStore("rename-invalid"); await store.ConfigureMusicFolderAsync(music);
        foreach (var name in new[] { "Taken", "CON", "../escape", "album", "trailing." })
        {
            try { await store.RenameMusicFolderAsync(folder, name); throw new Exception("Expected invalid rename rejection"); }
            catch (Exception error) when (error is IOException or ArgumentException) { }
            True(File.Exists(source) && Directory.Exists(folder));
        }
        try { await store.RenameMusicFolderAsync(music, "Root changed"); throw new Exception("Expected Music root rejection"); } catch (IOException) { }
        var revision = store.Load().Revision; await store.RenameMusicFolderAsync(folder, "Album"); Equal(revision, store.Load().Revision);
    });

    await Run("rename rolls the directory and all references back when persistence fails", async () =>
    {
        var music = NewFolder("rename-write-failure"); var folder = Path.Combine(music, "Original"); Directory.CreateDirectory(folder);
        var source = WriteWav(Path.Combine(folder, "song.wav"), .1); var data = DataRoot("rename-write-failure"); var store = new LibraryStore(data);
        await store.ConfigureMusicFolderAsync(music); store.SetFolderPinned(folder, true); var revision = store.Load().Revision;
        using (var blocked = new FileStream(Path.Combine(data, "library.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await store.RenameMusicFolderAsync(folder, "Renamed"); throw new Exception("Expected failed save"); } catch (IOException) { }
        }
        True(File.Exists(source) && !Directory.Exists(Path.Combine(music, "Renamed")));
        Equal(source, store.Load().Tracks.Single().Path); Equal(folder, store.Load().PinnedFolderPaths.Single()); Equal(revision, store.Load().Revision);
        Equal(source, new LibraryStore(data).Load().Tracks.Single().Path);
    });

    await Run("rename cancellation after directory movement restores the original folder", async () =>
    {
        var music = NewFolder("rename-cancel"); var folder = Path.Combine(music, "Original"); Directory.CreateDirectory(folder);
        var source = WriteWav(Path.Combine(folder, "song.wav"), .1); var store = NewStore("rename-cancel"); await store.ConfigureMusicFolderAsync(music);
        using var cancellation = new CancellationTokenSource();
        try { await store.RenameMusicFolderAsync(folder, "Cancelled", new ImmediateProgress(_ => cancellation.Cancel()), cancellation.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        True(File.Exists(source) && !Directory.Exists(Path.Combine(music, "Cancelled"))); Equal(source, store.Load().Tracks.Single().Path);
    });

    await Run("rename protects storage contained inside the selected folder", async () =>
    {
        var music = NewFolder("rename-store"); var folder = Path.Combine(music, "Container"); Directory.CreateDirectory(folder);
        var store = new LibraryStore(folder + Path.DirectorySeparatorChar); await store.ConfigureMusicFolderAsync(music);
        try { await store.RenameMusicFolderAsync(folder, "Moved"); throw new Exception("Expected storage protection"); } catch (IOException) { }
        True(Directory.Exists(folder) && File.Exists(Path.Combine(folder, "library.json")));
    });

    await Run("folder artwork is a validated cached override with reset and restart persistence", async () =>
    {
        var music = NewFolder("artwork-choice"); var folder = Path.Combine(music, "Album"); Directory.CreateDirectory(folder);
        WriteWav(Path.Combine(folder, "song.wav"), .1); var physicalCover = WriteImage(Path.Combine(folder, "cover.png"));
        var source = WriteImage(Path.Combine(NewFolder("artwork-source"), "chosen.jpg")); var originalBytes = File.ReadAllBytes(source);
        var data = DataRoot("artwork-choice"); var store = new LibraryStore(data); var before = await store.ConfigureMusicFolderAsync(music);
        Equal(physicalCover, FolderLibrary.BuildAlbums(before.Tracks, music).Single().ArtworkPath);
        var selected = await store.SetFolderArtworkAsync(folder, source); var cached = selected.FolderArtworkPaths[folder];
        True(cached.StartsWith(Path.Combine(data, "artwork"), StringComparison.OrdinalIgnoreCase) && File.Exists(cached));
        True(originalBytes.SequenceEqual(File.ReadAllBytes(source))); True(!File.Exists(Path.Combine(folder, "chosen.jpg")));
        Equal(cached, FolderLibrary.BuildAlbums(selected.Tracks, music, selected.AlbumLayout, selected.FolderArtworkPaths).Single().ArtworkPath);
        var restarted = new LibraryStore(data).Load(); Equal(cached, restarted.FolderArtworkPaths[folder]);
        selected.FolderArtworkPaths.Clear(); Equal(cached, store.Load().FolderArtworkPaths[folder]);
        var reset = await store.SetFolderArtworkAsync(folder, null); Equal(0, reset.FolderArtworkPaths.Count);
        Equal(physicalCover, FolderLibrary.BuildAlbums(reset.Tracks, music, reset.AlbumLayout, reset.FolderArtworkPaths).Single().ArtworkPath);
        True(File.Exists(source) && File.Exists(physicalCover));
    });

    await Run("invalid artwork and failed persistence leave the previous choice intact", async () =>
    {
        var music = NewFolder("artwork-failure"); var folder = Path.Combine(music, "Album"); Directory.CreateDirectory(folder);
        var data = DataRoot("artwork-failure"); var store = new LibraryStore(data); await store.ConfigureMusicFolderAsync(music);
        var good = WriteImage(Path.Combine(NewFolder("artwork-valid"), "valid.png")); var selected = await store.SetFolderArtworkAsync(folder, good); var cached = selected.FolderArtworkPaths[folder];
        var invalid = Path.Combine(NewFolder("artwork-invalid"), "not-image.png"); File.WriteAllText(invalid, "not an image");
        try { await store.SetFolderArtworkAsync(folder, invalid); throw new Exception("Expected invalid image rejection"); } catch (InvalidDataException) { }
        Equal(cached, store.Load().FolderArtworkPaths[folder]);
        try { await store.SetFolderArtworkAsync(music, good); throw new Exception("Expected root image rejection"); } catch (IOException) { }
        using (var blocked = new FileStream(Path.Combine(data, "library.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await store.SetFolderArtworkAsync(folder, null); throw new Exception("Expected failed reset save"); } catch (IOException) { }
            Equal(cached, store.Load().FolderArtworkPaths[folder]);
        }
        Equal(cached, new LibraryStore(data).Load().FolderArtworkPaths[folder]);
    });

    await Run("cancelled artwork selection never changes saved state", async () =>
    {
        var music = NewFolder("artwork-cancel"); var folder = Path.Combine(music, "Album"); Directory.CreateDirectory(folder);
        var store = NewStore("artwork-cancel"); await store.ConfigureMusicFolderAsync(music);
        var good = WriteImage(Path.Combine(NewFolder("artwork-cancel-source"), "valid.png"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await store.SetFolderArtworkAsync(folder, good, cancellation.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        Equal(0, store.Load().FolderArtworkPaths.Count);
    });

    await Run("queued refresh follows the newly committed Music root instead of restoring an old one", async () =>
    {
        var first = NewFolder("queued-refresh-first"); var second = NewFolder("queued-refresh-second");
        WriteWav(Path.Combine(first, "a.wav"), .001); var secondPath = WriteWav(Path.Combine(second, "b.wav"), .001);
        var store = NewStore("queued-refresh"); await store.ConfigureMusicFolderAsync(first);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var switching = store.ConfigureMusicFolderAsync(second, progress: new ImmediateProgress(value =>
        { if (value.Done > 0) { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var refreshing = store.RefreshAsync();
            release.Set();
            Equal(second, (await switching).MusicRoot);
            var current = await refreshing;
            Equal(second, current.MusicRoot); Equal(secondPath, current.Tracks.Single().Path);
            Equal(second, new LibraryStore(DataRoot("queued-refresh")).Load().MusicRoot);
        }
        finally { release.Set(); await switching; }
    });

    await Run("unchanged refresh neither rewrites the metadata cache nor advances its revision", async () =>
    {
        var music = NewFolder("no-op-refresh"); var path = WriteWav(Path.Combine(music, "same.wav"), .001);
        var data = DataRoot("no-op-refresh"); var store = new LibraryStore(data);
        var initial = await store.ConfigureMusicFolderAsync(music);
        var stateFile = Path.Combine(data, "library.json"); var original = File.ReadAllBytes(stateFile);
        var sentinel = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc); File.SetLastWriteTimeUtc(stateFile, sentinel);
        var reads = store.MetadataReadCount;
        var repeated = await store.RefreshAsync();
        Equal(initial.Revision, repeated.Revision); Equal(reads, store.MetadataReadCount);
        Equal(sentinel, File.GetLastWriteTimeUtc(stateFile)); True(original.SequenceEqual(File.ReadAllBytes(stateFile)));
        WriteWav(path, .002); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        var changed = await store.RefreshAsync();
        True(changed.Revision > repeated.Revision); Equal(reads + 1, store.MetadataReadCount);
        True(File.GetLastWriteTimeUtc(stateFile) != sentinel);
    });

    await Run("cached legacy import also avoids rewriting the complete library", async () =>
    {
        var path = WriteWav(Path.Combine(NewFolder("no-op-legacy"), "same.wav"), .001);
        var data = DataRoot("no-op-legacy"); var store = new LibraryStore(data); var initial = await store.ImportAsync([path]);
        var stateFile = Path.Combine(data, "library.json"); var sentinel = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(stateFile, sentinel);
        var repeated = await store.ImportAsync([path]);
        Equal(initial.Revision, repeated.Revision); Equal(sentinel, File.GetLastWriteTimeUtc(stateFile));
    });

    await Run("large scan progress is time-bounded while retaining first and final file reports", async () =>
    {
        var music = NewFolder("bounded-progress");
        for (var i = 0; i < 512; i++) WriteWav(Path.Combine(music, $"track-{i:0000}.wav"), .001);
        var updates = new List<LibraryProgress>(); var timer = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = await NewStore("bounded-progress").ConfigureMusicFolderAsync(music, progress: new ImmediateProgress(updates.Add));
        timer.Stop(); var files = updates.Where(value => value.Done > 0).ToList();
        Equal(512, snapshot.Tracks.Count); Equal(1, files.First().Done); Equal(512, files.Last().Done);
        True(files.Count <= 3 + (int)Math.Ceiling(timer.Elapsed.TotalMilliseconds / 100));
        Console.WriteLine($"  progress: {files.Count} file reports for 512 files in {timer.ElapsedMilliseconds} ms");
    });

    await Run("watcher configuration and disposal never wait for a blocked availability probe", async () =>
    {
        var first = NewFolder("watcher-blocked-a"); var second = NewFolder("watcher-blocked-b");
        using var entered = new ManualResetEventSlim(false); using var release = new ManualResetEventSlim(false);
        using var finished = new ManualResetEventSlim(false);
        var watcher = new LibraryWatcher(100, null, path =>
        { entered.Set(); try { release.Wait(TimeSpan.FromSeconds(10)); return Directory.Exists(path); } finally { finished.Set(); } });
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew(); watcher.Configure([first]); timer.Stop();
            True(timer.Elapsed < TimeSpan.FromSeconds(1));
            True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            timer.Restart(); watcher.Configure([second]); watcher.Dispose(); timer.Stop();
            True(timer.Elapsed < TimeSpan.FromSeconds(1));
            release.Set(); True(await Task.Run(() => finished.Wait(TimeSpan.FromSeconds(5))));
        }
        finally { release.Set(); watcher.Dispose(); }
    });

    await Run("watcher ignores its configured cache directory and still observes source audio", async () =>
    {
        var music = NewFolder("watcher-ignore-cache"); var cache = Path.Combine(music, "cache"); Directory.CreateDirectory(cache);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = 0;
        using var watcher = new LibraryWatcher(100, cache);
        watcher.Changed += (_, _) => { Interlocked.Increment(ref events); ready.TrySetResult(); sourceChanged.TrySetResult(); };
        watcher.Configure([music]); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Interlocked.Exchange(ref events, 0); sourceChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 0; i < 8; i++) File.WriteAllText(Path.Combine(cache, $"state-{i}.json"), "{}");
        await Task.Delay(350); Equal(0, Volatile.Read(ref events));
        WriteWav(Path.Combine(music, "new.wav"), .001); await sourceChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(Volatile.Read(ref events) > 0);
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

static string WriteImage(string path)
{
    var pixels = new byte[] { 255, 64, 32, 255, 0, 128, 255, 255, 0, 200, 128, 255, 64, 64, 64, 255 };
    var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 8);
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
    using var output = File.Create(path); encoder.Save(output); return Path.GetFullPath(path);
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
