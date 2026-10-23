using System.Threading;

namespace XTONE.Services;

/// <summary>Debounces source folder changes; all scans remain the store's responsibility.</summary>
public sealed class LibraryWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _debounce;
    private readonly System.Threading.Timer _availability;
    private readonly int _delay;
    private readonly string _ignoredDirectory;
    private List<string> _folders = [];
    private bool _disposed;
    public event EventHandler? Changed;

    public LibraryWatcher(int debounceMilliseconds = 750, string? ignoredDirectory = null)
    {
        _delay = Math.Max(100, debounceMilliseconds);
        _ignoredDirectory = Path.GetFullPath(ignoredDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTONE"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _debounce = new System.Threading.Timer(_ => NotifyChanged(), null, Timeout.Infinite, Timeout.Infinite);
        _availability = new System.Threading.Timer(_ => CheckAvailableRoots(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public void Configure(IEnumerable<string> folders)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _folders = folders.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var keep = new HashSet<string>(_folders, StringComparer.OrdinalIgnoreCase);
            foreach (var path in _watchers.Keys.Where(path => !keep.Contains(path)).ToArray())
            {
                _watchers[path].Dispose();
                _watchers.Remove(path);
            }
            foreach (var folder in _folders)
                AddWatcherLocked(folder);
        }
    }

    private bool AddWatcherLocked(string folder)
    {
        if (_watchers.ContainsKey(folder) || !Directory.Exists(folder))
            return false;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 32768
            };
            watcher.Created += SourceChanged;
            watcher.Changed += SourceChanged;
            watcher.Deleted += SourceChanged;
            watcher.Renamed += SourceRenamed;
            watcher.Error += WatcherError;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(folder, watcher);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private void SourceChanged(object sender, FileSystemEventArgs args)
    {
        if (!IsIgnored(args.FullPath))
            Schedule();
    }

    private void SourceRenamed(object sender, RenamedEventArgs args)
    {
        if (!IsIgnored(args.FullPath) || !IsIgnored(args.OldFullPath))
            Schedule();
    }

    private bool IsIgnored(string path) =>
        Path.GetFullPath(path).StartsWith(_ignoredDirectory, StringComparison.OrdinalIgnoreCase);

    private void WatcherError(object sender, ErrorEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            // Overflow or disconnected roots need a full refresh and a fresh watcher.
            var entry = _watchers.FirstOrDefault(pair => ReferenceEquals(pair.Value, sender));
            if (entry.Value is not null)
            {
                entry.Value.Dispose();
                _watchers.Remove(entry.Key);
            }
        }
        Schedule();
    }

    private void CheckAvailableRoots()
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed)
                return;
            foreach (var folder in _folders)
            {
                if (_watchers.TryGetValue(folder, out var watcher) && !Directory.Exists(folder))
                {
                    watcher.Dispose();
                    _watchers.Remove(folder);
                    changed = true;
                }
                else if (AddWatcherLocked(folder))
                    changed = true;
            }
        }
        if (changed)
            Schedule();
    }

    private void Schedule()
    {
        lock (_gate)
            if (!_disposed)
                _debounce.Change(_delay, Timeout.Infinite);
    }

    private void NotifyChanged()
    {
        EventHandler? handler;
        lock (_gate)
            handler = _disposed ? null : Changed;
        handler?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _debounce.Dispose();
            _availability.Dispose();
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
        }
    }
}
