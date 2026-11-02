using XTONE.Models;
using XTONE.Services;

var passed = 0;
var failed = 0;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

void Run(string name, Action scenario)
{
    try
    {
        scenario();
        passed++;
        Console.WriteLine("PASS " + name);
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {error.Message}");
    }
}

Track Song(string title) => new() { Title = title, Path = $@"C:\Music\{title}.flac" };

PlaybackQueue Queue(int count = 4, int current = 0)
{
    var queue = new PlaybackQueue();
    queue.Replace(Enumerable.Range(0, count).Select(index => Song("track-" + index)), current);
    return queue;
}

void CheckCurrent(PlaybackQueue queue, Guid? expected)
{
    Check(queue.Current?.Id == expected, "current queue-entry identity changed");
    Check(queue.Entries.Count(entry => entry.IsCurrent) == (expected is null ? 0 : 1), "current flags disagree");
    if (queue.Current is { } current)
        Check(current.IsCurrent && ReferenceEquals(current, queue.Entries[queue.CurrentIndex]), "current index/flag disagree");
    else
        Check(queue.CurrentIndex == -1, "empty selection must have index -1");
}

Run("duplicate tracks keep independent identities through selection and reordering", () =>
{
    var duplicate = Song("duplicate");
    var queue = new PlaybackQueue();
    queue.Replace([duplicate, duplicate, Song("other")]);
    Check(queue.Entries.Count == 3 && queue.Entries.Select(entry => entry.Id).Distinct().Count() == 3, "duplicates were collapsed");
    Check(ReferenceEquals(queue.Entries[0].Track, queue.Entries[1].Track), "fixture must reuse the same Track");
    var first = queue.Entries[0].Id;
    var second = queue.Entries[1].Id;
    queue.Select(1);
    CheckCurrent(queue, second);
    queue.Move(1, 0);
    CheckCurrent(queue, second);
    Check(queue.CurrentIndex == 0 && queue.Entries[1].Id == first, "reorder followed a track path instead of an entry");
});

Run("moving another entry preserves the playing entry", () =>
{
    var queue = Queue(5, 2);
    var current = queue.Current!.Id;
    queue.Move(4, 0);
    CheckCurrent(queue, current);
    Check(queue.CurrentIndex == 3, "current index did not follow the move");
    queue.Move(0, 4);
    CheckCurrent(queue, current);
    Check(queue.CurrentIndex == 2, "reverse move left the wrong current index");
});

Run("repeat off ends without wrapping or changing selection", () =>
{
    var queue = Queue(3, 2);
    var current = queue.Current!.Id;
    Check(queue.PeekNext() == -1 && queue.PeekNext(true) == -1, "repeat off wrapped at the end");
    CheckCurrent(queue, current);
});

Run("repeat all wraps from the final entry", () =>
{
    var queue = Queue(3, 2);
    queue.RepeatMode = QueueRepeatMode.All;
    Check(queue.PeekNext() == 0 && queue.PeekNext() == 0, "repeat all did not cache the wrapped successor");
    queue.Select(queue.PeekNext());
    CheckCurrent(queue, queue.Entries[0].Id);
    Check(queue.PeekNext() == 1, "selection did not clear the previous successor");
});

Run("repeat one repeats automatically and permits a manual skip", () =>
{
    var queue = Queue(3, 1);
    queue.RepeatMode = QueueRepeatMode.One;
    Check(queue.PeekNext() == 1, "automatic navigation did not repeat the current entry");
    Check(queue.PeekNext(true) == 2, "manual next did not escape repeat one");
    Check(queue.PeekNext() == 1, "cached manual skip overrode repeat one");
    queue.Select(2);
    Check(queue.PeekNext() == 2 && queue.PeekNext(true) == -1, "manual skip unexpectedly wrapped repeat one");
});

Run("changing repeat mode invalidates a previously wrapped successor", () =>
{
    var queue = Queue(3, 2);
    queue.RepeatMode = QueueRepeatMode.All;
    Check(queue.PeekNext() == 0, "fixture did not cache a wrapped next entry");
    queue.RepeatMode = QueueRepeatMode.Off;
    Check(queue.PeekNext() == -1, "repeat-off retained a cached repeat-all wrap");
});

Run("changing shuffle invalidates its cached successor", () =>
{
    var queue = Queue(3);
    queue.Select(2);
    queue.Shuffle = true;
    Check(queue.PeekNext() == 1, "fixture requires exactly one unplayed entry");
    queue.Shuffle = false;
    Check(queue.PeekNext() == -1, "sequential navigation reused a shuffled successor");
});

Run("shuffle peeking is stable and each entry plays once with repeat off", () =>
{
    for (var attempt = 0; attempt < 30; attempt++)
    {
        var queue = Queue(12);
        queue.Shuffle = true;
        var visited = new HashSet<Guid> { queue.Current!.Id };
        for (var step = 1; step < queue.Entries.Count; step++)
        {
            var next = queue.PeekNext();
            Check(next >= 0, "shuffle ended before every entry played");
            var nextId = queue.Entries[next].Id;
            for (var peek = 0; peek < 10; peek++)
                Check(queue.Entries[queue.PeekNext()].Id == nextId, "prefetched shuffle choice changed between peeks");
            Check(visited.Add(nextId), "shuffle repeated an entry in the same pass");
            queue.Select(next);
            CheckCurrent(queue, nextId);
        }
        Check(visited.Count == queue.Entries.Count && queue.PeekNext() == -1, "shuffle did not finish a complete finite pass");
    }
});

Run("shuffle treats duplicate paths as distinct queue entries", () =>
{
    var queue = new PlaybackQueue { Shuffle = true };
    var duplicate = Song("duplicate");
    queue.Replace([duplicate, duplicate, duplicate, duplicate]);
    var visited = new HashSet<Guid> { queue.Current!.Id };
    while (queue.PeekNext() is var next && next >= 0)
    {
        Check(visited.Add(queue.Entries[next].Id), "duplicate entry repeated");
        queue.Select(next);
    }
    Check(visited.Count == 4, "shuffle collapsed duplicate paths");
});

Run("shuffle repeat all starts new passes without immediately repeating", () =>
{
    var queue = Queue(7);
    queue.Shuffle = true;
    queue.RepeatMode = QueueRepeatMode.All;
    for (var pass = 0; pass < 5; pass++)
    {
        var visited = new HashSet<Guid> { queue.Current!.Id };
        for (var step = 1; step < queue.Entries.Count; step++)
        {
            var next = queue.PeekNext();
            Check(next >= 0 && visited.Add(queue.Entries[next].Id), "repeat-all pass repeated or skipped an entry");
            queue.Select(next);
        }
        var previous = queue.Current!.Id;
        var wrap = queue.PeekNext();
        Check(wrap >= 0 && queue.Entries[wrap].Id != previous, "repeat-all shuffle immediately replayed the last entry");
        queue.Select(wrap);
    }
});

Run("single-entry repeat and shuffle remain finite or repeat as requested", () =>
{
    var queue = Queue(1);
    queue.Shuffle = true;
    Check(queue.PeekNext() == -1, "single-entry shuffle repeated with repeat off");
    queue.RepeatMode = QueueRepeatMode.All;
    Check(queue.PeekNext() == 0, "single-entry repeat all did not wrap");
    queue.RepeatMode = QueueRepeatMode.One;
    Check(queue.PeekNext() == 0 && queue.PeekNext(true) == -1, "single-entry repeat one manual skip did not end");
});

Run("previous follows listening history after duplicate selection and reorder", () =>
{
    var duplicate = Song("duplicate");
    var queue = new PlaybackQueue();
    queue.Replace([duplicate, duplicate, Song("last")]);
    var first = queue.Entries[0].Id;
    var second = queue.Entries[1].Id;
    queue.Select(1);
    queue.Select(2);
    queue.Move(0, 2);
    Check(queue.Previous(), "previous did not find second entry");
    CheckCurrent(queue, second);
    Check(queue.Previous(), "previous did not find first entry");
    CheckCurrent(queue, first);
});

Run("previous ignores removed history entries", () =>
{
    var queue = Queue(4);
    var first = queue.Entries[0].Id;
    queue.Select(1);
    queue.Select(2);
    Check(queue.Remove(1), "fixture history entry could not be removed");
    Check(queue.Previous(), "previous failed after a history item was removed");
    CheckCurrent(queue, first);
});

Run("the playing entry cannot be removed", () =>
{
    var queue = Queue(4, 2);
    var current = queue.Current!.Id;
    Check(!queue.Remove(2), "remove accepted the playing entry");
    Check(queue.Entries.Count == 4, "failed removal changed the queue");
    CheckCurrent(queue, current);
});

Run("removing a cached upcoming entry chooses the new successor", () =>
{
    var queue = Queue(3);
    var last = queue.Entries[2].Id;
    Check(queue.PeekNext() == 1 && queue.Remove(1), "fixture could not remove the prefetched entry");
    Check(queue.PeekNext() == 1 && queue.Entries[queue.PeekNext()].Id == last, "removed entry remained cached");
});

Run("clear upcoming retains the exact playing duplicate and clears navigation", () =>
{
    var duplicate = Song("duplicate");
    var queue = new PlaybackQueue();
    queue.Replace([duplicate, duplicate, Song("third")], 1);
    var current = queue.Current!;
    queue.PeekNext();
    queue.ClearUpcoming();
    Check(queue.Entries.Count == 1 && ReferenceEquals(queue.Entries[0], current), "clear kept the wrong duplicate");
    CheckCurrent(queue, current.Id);
    Check(queue.PeekNext() == -1, "clear retained an upcoming successor");
});

Run("insert-next and append preserve the current identity and invalidate next", () =>
{
    var queue = Queue(3);
    var current = queue.Current!.Id;
    queue.PeekNext();
    var inserted = Song("inserted");
    queue.InsertNext(inserted);
    Check(queue.PeekNext() == 1 && ReferenceEquals(queue.Entries[1].Track, inserted), "insert-next was not the next entry");
    CheckCurrent(queue, current);
    var appended = Song("appended");
    queue.Append(appended);
    Check(ReferenceEquals(queue.Entries[^1].Track, appended), "append did not add at the end");
    CheckCurrent(queue, current);
});

Run("play next overrides shuffle and remains stable while peeking", () =>
{
    var queue = Queue(8);
    queue.Shuffle = true;
    queue.PeekNext();
    var requested = Song("play-next");
    queue.InsertNext(requested);
    var next = queue.PeekNext();
    Check(next >= 0 && ReferenceEquals(queue.Entries[next].Track, requested), "shuffle ignored explicit play next");
    var requestedId = queue.Entries[next].Id;
    for (var peek = 0; peek < 10; peek++)
        Check(queue.Entries[queue.PeekNext()].Id == requestedId, "play next changed between prefetch peeks");
    queue.Select(next);
    CheckCurrent(queue, requestedId);
});

Run("unrelated queue edits preserve an explicit play-next request", () =>
{
    // Repeated invariants exercise the random order without assuming a particular RNG result.
    for (var attempt = 0; attempt < 30; attempt++)
    {
        var queue = Queue(4);
        for (var index = 1; index < queue.Entries.Count; index++) queue.Select(index);
        queue.Shuffle = true;
        var requested = Song("requested-next");
        queue.InsertNext(requested);
        var requestedId = queue.Entries.Single(entry => ReferenceEquals(entry.Track, requested)).Id;
        queue.Append(Song("later"));
        queue.Move(queue.Entries.Count - 1, 0);
        Check(queue.Remove(1), "fixture could not remove a previously heard entry");
        var next = queue.PeekNext();
        Check(next >= 0 && queue.Entries[next].Id == requestedId, "unrelated append, reorder or removal dropped the explicit request");
    }
});

Run("explicit play next survives setting changes and overrides repeat one", () =>
{
    var queue = Queue(5);
    var requested = Song("requested");
    queue.InsertNext(requested);
    var requestedId = queue.Entries[1].Id;
    foreach (var repeat in new[] { QueueRepeatMode.All, QueueRepeatMode.One, QueueRepeatMode.Off })
    {
        queue.RepeatMode = repeat;
        foreach (var shuffle in new[] { true, false })
        {
            queue.Shuffle = shuffle;
            queue.InvalidateNext();
            Check(queue.Entries[queue.PeekNext()].Id == requestedId, "settings or prefetch invalidation discarded play next");
            Check(queue.Entries[queue.PeekNext(true)].Id == requestedId, "manual skip ignored play next");
        }
    }
    queue.RepeatMode = QueueRepeatMode.One;
    queue.Select(queue.PeekNext());
    CheckCurrent(queue, requestedId);
    Check(queue.PeekNext() == queue.CurrentIndex, "repeat one did not resume after the request was played");
    Check(queue.PeekNext(true) != queue.CurrentIndex, "consumed request remained pending");
});

Run("pending play next survives selection of another entry and clears when removed", () =>
{
    var queue = Queue(4);
    queue.InsertNext(Song("requested"));
    var requestedId = queue.Entries[1].Id;
    queue.Select(3);
    Check(queue.Entries[queue.PeekNext()].Id == requestedId, "selecting another entry consumed an unplayed request");
    Check(queue.Remove(queue.PeekNext()), "pending request could not be removed");
    Check(queue.PeekNext() == queue.CurrentIndex + 1, "removing requested entry left a stale next identity");
});

Run("replacing or clearing upcoming entries removes pending play next", () =>
{
    var queue = Queue(3);
    queue.InsertNext(Song("requested"));
    queue.ClearUpcoming();
    Check(queue.PeekNext() == -1, "clear upcoming retained play next");
    queue.InsertNext(Song("another-request"));
    queue.Replace([Song("replacement")]);
    Check(queue.PeekNext() == -1, "replace retained play next from the old queue");
});

Run("empty queue operations are safe and append does not start playback", () =>
{
    var queue = Queue(0);
    CheckCurrent(queue, null);
    Check(queue.PeekNext() == -1 && !queue.Previous() && !queue.Select(0) && !queue.Remove(0), "empty operation succeeded unexpectedly");
    queue.ClearUpcoming();
    queue.Append(Song("appended"));
    CheckCurrent(queue, null);
    Check(queue.Select(0), "appended entry could not be selected");
    CheckCurrent(queue, queue.Entries[0].Id);
});

Run("replace resets history and clamps the selected index", () =>
{
    var queue = Queue(4);
    queue.Shuffle = true;
    queue.Select(1);
    queue.PeekNext();
    queue.Replace([Song("new-a"), Song("new-b")], 999);
    Check(queue.CurrentIndex == 1, "replace did not clamp its index");
    Check(queue.Previous() && queue.CurrentIndex == 0, "replace retained old listening history");
    queue.Replace([Song("first")], -50);
    CheckCurrent(queue, queue.Entries[0].Id);
    queue.Replace([]);
    CheckCurrent(queue, null);
});

Run("invalid select, remove and move calls leave queue identity intact", () =>
{
    var queue = Queue(3, 1);
    var before = queue.Entries.Select(entry => entry.Id).ToArray();
    var current = queue.Current!.Id;
    Check(!queue.Select(-1) && !queue.Select(3) && !queue.Remove(-1) && !queue.Remove(3), "invalid index accepted");
    queue.Move(-1, 0);
    queue.Move(0, 3);
    queue.Move(1, 1);
    Check(queue.Entries.Select(entry => entry.Id).SequenceEqual(before), "invalid move changed order");
    CheckCurrent(queue, current);
});

Run("refreshing moved files preserves duplicate entry identities, selection, play-next and listening history", () =>
{
    var duplicate = Song("moved-duplicate");
    var queue = new PlaybackQueue { Shuffle = true, RepeatMode = QueueRepeatMode.All };
    queue.Replace([duplicate, Song("other"), duplicate, Song("last")]);
    var firstDuplicate = queue.Entries[0].Id;
    queue.Select(2);
    var currentDuplicate = queue.Current!.Id;
    queue.InsertNext(Song("requested-next"));
    var requested = queue.Entries[queue.PeekNext()].Id;
    var identities = queue.Entries.Select(entry => entry.Id).ToArray();
    var replacement = duplicate with { Path = @"C:\Music\Organized\moved-duplicate.flac", IsFavorite = true };
    var changedDuplicates = 0;
    foreach (var entry in queue.Entries.Where(entry => entry.Track.Id == duplicate.Id))
        entry.PropertyChanged += (_, args) => { if (args.PropertyName == "Track") changedDuplicates++; };

    queue.RefreshTracks(track => track.Id == duplicate.Id ? replacement : track);

    Check(queue.Entries.Select(entry => entry.Id).SequenceEqual(identities), "refresh changed entry identities or order");
    CheckCurrent(queue, currentDuplicate);
    Check(queue.CurrentIndex == 2, "refresh changed the selected duplicate");
    var duplicates = queue.Entries.Where(entry => entry.Track.Id == duplicate.Id).ToArray();
    Check(duplicates.Length == 2 && duplicates.All(entry => ReferenceEquals(entry.Track, replacement)), "both duplicate entries did not adopt the moved track");
    Check(changedDuplicates == 2, "bound duplicate rows were not notified of replacement metadata");
    Check(queue.Entries[queue.PeekNext()].Id == requested, "refresh dropped the explicit play-next request");
    Check(queue.Shuffle && queue.RepeatMode == QueueRepeatMode.All, "refresh changed queue modes");
    queue.Select(queue.PeekNext());
    Check(queue.Previous(), "refresh lost listening history");
    CheckCurrent(queue, currentDuplicate);
    Check(queue.Previous(), "refresh lost the earlier duplicate history entry");
    CheckCurrent(queue, firstDuplicate);
});

Run("large queue reads allocate no per-lookup copies and collection mutations invalidate indices", () =>
{
    var queue = Queue(25000, 12500);
    var selected = queue.Current!.Id;
    var bytes = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 20000; i++) { Check(queue.Current!.Id == selected, "current identity changed"); Check(queue.CurrentIndex == 12500, "index changed"); }
    Check(GC.GetAllocatedBytesForCurrentThread() - bytes < 4096, "queue lookup still allocates per-read copies");
    queue.Entries.Move(12500, 4); Check(queue.CurrentIndex == 4, "move left stale index");
    queue.Entries.RemoveAt(0); Check(queue.CurrentIndex == 3, "remove left stale index");
    queue.ClearUpcoming(); Check(queue.Entries.Count == 1 && queue.Current!.Id == selected && queue.CurrentIndex == 0, "clear lost current identity");
});
Run("large queue replacement publishes one collection reset", () =>
{
    var queue = new PlaybackQueue(); var changes = 0;
    queue.Entries.CollectionChanged += (_,_) => changes++;
    queue.Replace(Enumerable.Range(0, 25000).Select(i => Song("bulk-" + i)), 24999);
    Check(changes == 1 && queue.CurrentIndex == 24999, "bulk replacement emitted per-track changes");
    queue.Select(0); CheckCurrent(queue, queue.Entries[0].Id);
});

Console.WriteLine($"Queue tests: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
