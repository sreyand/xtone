using System.Collections.ObjectModel;
using System.ComponentModel;
using XTONE.Models;

namespace XTONE.Services;

public enum QueueRepeatMode { Off, All, One }

public sealed class QueueEntry(Track track) : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public Track Track { get; private set; } = track;
    internal void RefreshTrack(Track replacement)
    { Track = replacement; PropertyChanged?.Invoke(this, new(nameof(Track))); }
    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set { _isCurrent = value; PropertyChanged?.Invoke(this, new(nameof(IsCurrent))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Queue order and navigation, independent of the audio device.</summary>
public sealed class PlaybackQueue
{
    private Guid? _currentId;
    private readonly Dictionary<Guid, int> _indices = [];
    private bool _indicesDirty = true;
    private Guid? _nextId;
    private Guid? _requestedNextId;
    private readonly HashSet<Guid> _played = [];
    private readonly Stack<Guid> _history = [];
    public ObservableCollection<QueueEntry> Entries { get; } = new ResettableObservableCollection<QueueEntry>();
    public PlaybackQueue() => Entries.CollectionChanged += (_, _) => _indicesDirty = true;
    private int IndexOf(Guid id)
    {
        if (_indicesDirty)
        {
            _indices.Clear();
            for (var index = 0; index < Entries.Count; index++) _indices[Entries[index].Id] = index;
            _indicesDirty = false;
        }
        return _indices.GetValueOrDefault(id, -1);
    }
    private bool _shuffle;
    private QueueRepeatMode _repeatMode;
    public bool Shuffle { get => _shuffle; set { if (_shuffle != value) { _shuffle = value; _nextId = null; } } }
    public QueueRepeatMode RepeatMode { get => _repeatMode; set { if (_repeatMode != value) { _repeatMode = value; _nextId = null; } } }
    public int CurrentIndex => _currentId is { } id ? IndexOf(id) : -1;
    public QueueEntry? Current => CurrentIndex is var index && index >= 0 ? Entries[index] : null;

    public void Replace(IEnumerable<Track> tracks, int currentIndex = 0)
    {
        var replacements = tracks.Select(track => new QueueEntry(track)).ToArray();
        ((ResettableObservableCollection<QueueEntry>)Entries).ReplaceAll(replacements);
        _currentId = null;
        _played.Clear();
        _history.Clear();
        _nextId = null;
        _requestedNextId = null;
        if (Entries.Count > 0) Select(Math.Clamp(currentIndex, 0, Entries.Count - 1), false);
    }

    public bool Select(int index, bool recordHistory = true)
    {
        if (index < 0 || index >= Entries.Count) return false;
        var previousEntry = Current;
        if (recordHistory && _currentId is { } previous && previous != Entries[index].Id) _history.Push(previous);
        _currentId = Entries[index].Id;
        _played.Add(_currentId.Value);
        _nextId = null;
        if (_requestedNextId == _currentId) _requestedNextId = null;
        if (previousEntry is not null && previousEntry.Id != _currentId) previousEntry.IsCurrent = false;
        Entries[index].IsCurrent = true;
        return true;
    }

    public int PeekNext(bool manualSkip = false)
    {
        if (CurrentIndex < 0 || Entries.Count == 0) return -1;
        // An explicit request survives predictive-cache invalidation and wins over repeat one.
        if (_requestedNextId is { } requested)
        {
            var match = IndexOf(requested);
            if (match >= 0 && requested != _currentId) return match;
            _requestedNextId = null;
        }
        if (!manualSkip && RepeatMode == QueueRepeatMode.One) return CurrentIndex;
        if (_nextId is { } cached)
        {
            var match = IndexOf(cached);
            if (match >= 0) return match;
        }
        int next;
        if (Shuffle)
        {
            var candidates = Entries.Select((entry, index) => (entry, index))
                .Where(item => !_played.Contains(item.entry.Id) && item.entry.Id != _currentId).ToList();
            if (candidates.Count == 0 && RepeatMode == QueueRepeatMode.All)
            {
                _played.Clear();
                candidates = Entries.Select((entry, index) => (entry, index))
                    .Where(item => item.entry.Id != _currentId || Entries.Count == 1).ToList();
            }
            next = candidates.Count == 0 ? -1 : candidates[Random.Shared.Next(candidates.Count)].index;
        }
        else
        {
            next = CurrentIndex + 1;
            if (next >= Entries.Count) next = RepeatMode == QueueRepeatMode.All ? 0 : -1;
        }
        if (next >= 0) _nextId = Entries[next].Id;
        return next;
    }

    public bool Previous()
    {
        while (_history.TryPop(out var id))
        {
            var index = IndexOf(id);
            if (index >= 0) return Select(index, false);
        }
        return Select(Math.Max(0, CurrentIndex - 1), false);
    }

    public void InsertNext(Track track)
    {
        var entry = new QueueEntry(track);
        Entries.Insert(CurrentIndex < 0 ? Entries.Count : CurrentIndex + 1, entry);
        _requestedNextId = entry.Id;
        _nextId = null;
    }
    public void Append(Track track) { Entries.Add(new(track)); _nextId = null; }
    public void Move(int from, int to)
    {
        if (from < 0 || to < 0 || from >= Entries.Count || to >= Entries.Count || from == to) return;
        Entries.Move(from, to);
        _nextId = null;
    }
    public bool Remove(int index)
    {
        if (index < 0 || index >= Entries.Count || Entries[index].Id == _currentId) return false;
        if (Entries[index].Id == _requestedNextId) _requestedNextId = null;
        _played.Remove(Entries[index].Id);
        Entries.RemoveAt(index);
        _nextId = null;
        return true;
    }
    public void ClearUpcoming()
    {
        var current = Current;
        ((ResettableObservableCollection<QueueEntry>)Entries).ReplaceAll(current is null ? [] : [current]);
        _nextId = null;
        _requestedNextId = null;
    }
    public void RefreshTracks(Func<Track, Track> resolve)
    {
        foreach (var entry in Entries) entry.RefreshTrack(resolve(entry.Track));
        _nextId = null;
    }
    public void InvalidateNext() => _nextId = null;
}
