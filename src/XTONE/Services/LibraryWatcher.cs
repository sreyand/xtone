using System.Threading;

namespace XTONE.Services;

/// <summary>Debounces folder changes and reconciles watcher availability off the UI thread.</summary>
public sealed class LibraryWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _debounce;
    private readonly System.Threading.Timer _availability;
    private readonly int _delay;
    private readonly string _ignoredDirectory;
    private readonly string _ignoredRoot;
    private readonly Func<string, bool> _rootExists;
    private List<string> _folders = [];
    private long _generation;
    private bool _reconcileRunning;
    private bool _reconcilePending;
    private bool _disposed;
    public event EventHandler? Changed;

    public LibraryWatcher(int debounceMilliseconds = 750, string? ignoredDirectory = null)
        : this(debounceMilliseconds, ignoredDirectory, Directory.Exists) { }

    // An injected probe makes a blocked network root reproducible in isolated checks.
    internal LibraryWatcher(int debounceMilliseconds, string? ignoredDirectory, Func<string, bool> rootExists)
    {
        _delay = Math.Max(100, debounceMilliseconds);
        _rootExists = rootExists;
        _ignoredRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ignoredDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTONE")));
        _ignoredDirectory = Path.EndsInDirectorySeparator(_ignoredRoot) ? _ignoredRoot : _ignoredRoot + Path.DirectorySeparatorChar;
        _debounce = new System.Threading.Timer(_ => NotifyChanged(), null, Timeout.Infinite, Timeout.Infinite);
        _availability = new System.Threading.Timer(_ => RequestReconcile(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public void Configure(IEnumerable<string> folders)
    {
        var desired = folders.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (_gate)
        {
            if (_disposed || _folders.SequenceEqual(desired, StringComparer.OrdinalIgnoreCase)) return;
            _folders = desired; ++_generation;
        }
        RequestReconcile();
    }

    private void RequestReconcile()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _reconcilePending = true;
            if (_reconcileRunning) return;
            _reconcileRunning = true;
        }
        ThreadPool.QueueUserWorkItem(_ => ReconcileLoop());
    }

    private void ReconcileLoop()
    {
        while (true)
        {
            List<string> folders;
            List<FileSystemWatcher> retired;
            long generation;
            lock (_gate)
            {
                if (_disposed || !_reconcilePending) { _reconcileRunning = false; return; }
                _reconcilePending = false; folders = [.. _folders]; generation = _generation;
                var remove = _watchers.Keys.Where(path => !folders.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray();
                retired = remove.Select(path => _watchers[path]).ToList();
                foreach (var path in remove) _watchers.Remove(path);
            }
            DisposeWatchers(retired);
            var changed = false;
            foreach (var folder in folders)
            {
                // File/network I/O must never run while the configuration lock is held.
                bool exists;
                try { exists = _rootExists(folder); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { exists = false; }
                FileSystemWatcher? removed = null;
                bool add;
                lock (_gate)
                {
                    if (_disposed || generation != _generation) break;
                    if (!exists && _watchers.Remove(folder, out var old)) { removed = old; changed = true; }
                    add = exists && !_watchers.ContainsKey(folder);
                }
                removed?.Dispose();
                if (!add) continue;
                FileSystemWatcher? watcher = null;
                try
                {
                    watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 32768
                    };
                    watcher.Created += SourceChanged; watcher.Changed += SourceChanged;
                    watcher.Deleted += SourceChanged; watcher.Renamed += SourceRenamed; watcher.Error += WatcherError;
                    lock (_gate)
                    {
                        if (_disposed || generation != _generation || _watchers.ContainsKey(folder))
                        { /* Dispose outside the lock below. */ }
                        else { _watchers.Add(folder, watcher); add = false; }
                    }
                    if (add) { watcher.Dispose(); continue; }
                    watcher.EnableRaisingEvents = true;
                    // Setup is asynchronous: reconcile the gap before the watcher was armed.
                    changed = true;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
                {
                    lock (_gate)
                        if (watcher is not null && _watchers.GetValueOrDefault(folder) == watcher) _watchers.Remove(folder);
                    watcher?.Dispose();
                }
            }
            if (changed) Schedule();
        }
    }

    private void SourceChanged(object sender, FileSystemEventArgs args)
    { if (!IsIgnored(args.FullPath)) Schedule(); }
    private void SourceRenamed(object sender, RenamedEventArgs args)
    { if (!IsIgnored(args.FullPath) || !IsIgnored(args.OldFullPath)) Schedule(); }
    private bool IsIgnored(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Equals(_ignoredRoot, StringComparison.OrdinalIgnoreCase) || full.StartsWith(_ignoredDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private void WatcherError(object sender, ErrorEventArgs args)
    {
        FileSystemWatcher? retired = null;
        lock (_gate)
        {
            if (_disposed) return;
            var entry = _watchers.FirstOrDefault(pair => ReferenceEquals(pair.Value, sender));
            if (entry.Value is not null) { retired = entry.Value; _watchers.Remove(entry.Key); }
        }
        retired?.Dispose();
        Schedule(); RequestReconcile();
    }
    private void Schedule()
    { lock (_gate) if (!_disposed) _debounce.Change(_delay, Timeout.Infinite); }
    private void NotifyChanged()
    {
        EventHandler? handler;
        lock (_gate) handler = _disposed ? null : Changed;
        handler?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        List<FileSystemWatcher> watchers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; ++_generation;
            _debounce.Dispose(); _availability.Dispose();
            watchers = _watchers.Values.ToList(); _watchers.Clear();
        }
        // A disconnected network filesystem must not hold up window closing.
        ThreadPool.QueueUserWorkItem(_ => DisposeWatchers(watchers));
    }
    private static void DisposeWatchers(IEnumerable<FileSystemWatcher> watchers)
    { foreach (var watcher in watchers) watcher.Dispose(); }
}
