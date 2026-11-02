using XTONE.Models;

namespace XTONE.Services;

/// <summary>One active write and one latest pending snapshot; close can drain accepted work.</summary>
public sealed class SessionWritePump(Action<PlaybackSession> write, Action<Exception> reportError)
{
    private readonly object _gate = new();
    private PlaybackSession? _pending;
    private bool _running;
    private Task _drain = Task.CompletedTask;

    public Task Enqueue(PlaybackSession snapshot)
    {
        lock (_gate)
        {
            _pending = snapshot;
            if (!_running)
            {
                _running = true;
                _drain = Task.Run(Drain);
            }
            return _drain;
        }
    }
    private void Drain()
    {
        while (true)
        {
            PlaybackSession snapshot;
            lock (_gate)
            {
                if (_pending is null) { _running = false; return; }
                snapshot = _pending; _pending = null;
            }
            try { write(snapshot); }
            catch (Exception error)
            {
                // A failed disk write must not prevent the next accepted state from saving.
                try { reportError(error); } catch { }
            }
        }
    }
}
