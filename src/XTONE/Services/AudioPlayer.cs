using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace XTONE.Services;

public sealed record AudioOutputDevice(string Id, string Name);

public sealed class AudioPlaybackException : Exception
{
    public AudioPlaybackException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class AudioPlaybackErrorEventArgs(string message, Exception? exception = null) : EventArgs
{
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}

public sealed class AudioTrackStartedEventArgs(string path, long version, double boundaryStart) : EventArgs
{
    public string Path { get; } = path;
    public long Version { get; } = version;
    public double BoundaryStart { get; } = boundaryStart;
}

public sealed class AudioPlaybackStoppedEventArgs(long version, string? error) : EventArgs
{
    public long Version { get; } = version;
    public string? Error { get; } = error;
}

/// <summary>
/// Windows shared-mode WASAPI player. Source files are decoded to floating point,
/// normalized to the endpoint mix rate, and concatenated without restarting the device.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly object _stateLock = new();
    private readonly object _controlLock = new();
    private readonly SynchronizationContext? _eventContext = SynchronizationContext.Current;
    private readonly Timer _clockTimer;
    private PlaybackSession? _session;
    private string? _deviceId;
    private string? _lastError;
    private float _volume = .72f;
    private bool _analysisEnabled = true;
    private long _prepareVersion;
    private long _playbackVersion;
    private long _openVersion;
    private bool _disposed;

    public AudioPlayer() => _clockTimer = new Timer(PollClock, null, 20, 20);

    /// <summary>Raised for a genuine stream end or output failure, never a manual stop.</summary>
    public event EventHandler? PlaybackStopped;
    /// <summary>Raised when the device clock reaches the next track, rather than when it is read ahead.</summary>
    public event EventHandler? NextTrackStarted;
    public event EventHandler<AudioPlaybackErrorEventArgs>? PlaybackError;

    public string? CurrentPath { get { lock (_stateLock) return _session?.CurrentBoundary.Path; } }
    public string? LastError { get { lock (_stateLock) return _lastError; } }
    public string? OutputDeviceId { get { lock (_stateLock) return _deviceId; } }
    /// <summary>Changes whenever the output session is replaced or manually stopped.</summary>
    public long PlaybackVersion { get { lock (_stateLock) return _playbackVersion; } }
    public double CurrentBoundaryStart { get { lock (_stateLock) return _session?.CurrentBoundary.StartSeconds ?? 0; } }
    public bool IsPlaying
    {
        get
        {
            var session = CurrentSession;
            return session is not null && !session.Ended && session.Output.PlaybackState == PlaybackState.Playing;
        }
    }
    public TimeSpan Duration { get { lock (_stateLock) return _session?.CurrentBoundary.Duration ?? TimeSpan.Zero; } }
    public TimeSpan Position
    {
        get
        {
            var session = CurrentSession;
            if (session is null)
                return TimeSpan.Zero;
            var clock = session.ReadClock();
            lock (_stateLock)
            {
                if (_session != session)
                    return TimeSpan.Zero;
                var boundary = session.CurrentBoundary;
                if (session.Ended && session.Error is null)
                    return boundary.Duration;
                var seconds = boundary.SourceOffset.TotalSeconds + Math.Max(0, clock - boundary.StartSeconds);
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, boundary.Duration.TotalSeconds));
            }
        }
    }

    public bool AnalysisEnabled
    {
        get { lock (_stateLock) return _analysisEnabled; }
        set
        {
            lock (_stateLock)
            {
                _analysisEnabled = value;
                if (_session is not null)
                    _session.Tap.Enabled = value;
            }
        }
    }

    private PlaybackSession? CurrentSession { get { lock (_stateLock) return _session; } }

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var result = new List<AudioOutputDevice>();
            foreach (var device in devices)
            {
                using (device)
                    result.Add(new AudioOutputDevice(device.ID, device.FriendlyName));
            }
            return result;
        }
        catch (Exception error)
        {
            throw new AudioPlaybackException("Windows could not list audio output devices.", error);
        }
    }

    public void Play(string path, bool startPlaying = true)
    {
        lock (_controlLock)
        {
            ThrowIfDisposed();
            StartSession(path, TimeSpan.Zero, startPlaying);
        }
    }

    /// <summary>
    /// Opens the native decoder and device off the UI thread. Returns false if a
    /// newer open, seek, or stop superseded this request before it was ready.
    /// </summary>
    public Task<bool> PlayAsync(string path, bool startPlaying = true, TimeSpan? position = null)
    {
        long request;
        lock (_stateLock)
        {
            ThrowIfDisposed();
            request = ++_openVersion;
        }
        return Task.Run(() =>
        {
            PlaybackSession? prepared = null;
            try
            {
                prepared = CreateSession(path, position ?? TimeSpan.Zero);
                lock (_controlLock)
                {
                    if (!InstallSession(prepared, startPlaying, request))
                        return false;
                    prepared = null;
                    return true;
                }
            }
            catch (Exception error)
            {
                lock (_stateLock)
                {
                    if (_session == prepared)
                    {
                        _session = null;
                        _playbackVersion++;
                    }
                    if (_disposed || request != _openVersion)
                        return false;
                    var playbackError = WrapOutputError(error);
                    _lastError = playbackError.Message;
                    throw playbackError;
                }
            }
            finally
            {
                prepared?.Dispose();
            }
        });
    }

    public void Pause()
    {
        if (IsPlaying)
            PausePlayback();
        else
            Resume();
    }

    public void PausePlayback()
    {
        lock (_controlLock)
        {
            ThrowIfDisposed();
            var session = CurrentSession;
            if (session is null || !IsPlaying)
                return;
            var next = session.NextRequestedPath;
            // WasapiOut.Pause only stops filling the buffer: its device clock
            // continues through silence. Flush at the audible position instead
            // so resume cannot skip buffered audio or drift during a long pause.
            StartSession(session.CurrentBoundary.Path, Position, false);
            PrepareNext(next);
        }
    }

    public void Resume()
    {
        lock (_controlLock)
        {
            ThrowIfDisposed();
            var session = CurrentSession;
            if (session is null)
                return;
            if (session.Ended)
                StartSession(session.CurrentBoundary.Path, TimeSpan.Zero, true);
            else
                session.Output.Play();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_controlLock)
        {
            ThrowIfDisposed();
            var session = CurrentSession;
            if (session is null)
                return;
            var playing = IsPlaying;
            var next = session.NextRequestedPath;
            // A new device buffer flushes all read-ahead. The decoder is never
            // repositioned concurrently with its audio-thread Read call.
            StartSession(session.CurrentBoundary.Path, position, playing);
            PrepareNext(next);
        }
    }

    public void SetVolume(float volume)
    {
        lock (_stateLock)
        {
            _volume = float.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 0;
            if (_session is not null)
                _session.Volume.Volume = _volume;
        }
    }

    public float[] GetSpectrum() => CurrentSession?.Tap.GetSpectrum() ?? new float[32];

    public void SetOutputDevice(string? id)
    {
        lock (_controlLock)
        {
            ThrowIfDisposed();
            var newId = string.IsNullOrWhiteSpace(id) ? null : id;
            if (string.Equals(newId, _deviceId, StringComparison.Ordinal))
                return;
            var session = CurrentSession;
            var position = Position;
            var playing = IsPlaying;
            var previous = _deviceId;
            var next = session?.NextRequestedPath;
            try
            {
                if (session is null)
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var device = ResolveDevice(enumerator, newId);
                }
                lock (_stateLock)
                {
                    _deviceId = newId;
                    _openVersion++;
                }
                if (session is not null)
                {
                    StartSession(session.CurrentBoundary.Path, position, playing);
                    PrepareNext(next);
                }
            }
            catch
            {
                lock (_stateLock)
                    _deviceId = previous;
                throw;
            }
        }
    }

    /// <summary>
    /// Pre-opens and pre-reads the next decoder on a worker thread. Repeated calls
    /// replace the pending track; null clears it. Stale preparations dispose themselves.
    /// </summary>
    public void PrepareNext(string? path)
    {
        PlaybackSession? session;
        long sequence;
        long request;
        lock (_stateLock)
        {
            if (_disposed || _session is null)
                return;
            session = _session;
            request = ++_prepareVersion;
            sequence = session.Pipeline.Sequence;
            session.NextRequestedPath = string.IsNullOrWhiteSpace(path) ? null : path;
            session.Pipeline.SetNext(sequence, null);
        }
        if (string.IsNullOrWhiteSpace(path))
            return;

        _ = Task.Run(() =>
        {
            AudioSegment? prepared = null;
            try
            {
                prepared = AudioDecoder.Open(path, session.SampleRate, TimeSpan.Zero, true);
                lock (_stateLock)
                {
                    if (_disposed || _session != session || request != _prepareVersion)
                        return;
                    session.Pipeline.SetNext(sequence, prepared);
                    prepared = null; // SetNext consumed ownership, including rejection.
                }
            }
            catch (Exception error)
            {
                // Keep the current track playing. A failed next-track preparation
                // is surfaced if/when the queue tries to play that file normally.
                lock (_stateLock)
                {
                    if (_session == session && request == _prepareVersion)
                        session.PreparationError = error.Message;
                }
            }
            finally
            {
                prepared?.Dispose();
            }
        });
    }

    public void Stop()
    {
        lock (_controlLock)
        {
            PlaybackSession? old;
            lock (_stateLock)
            {
                old = _session;
                _session = null;
                _prepareVersion++;
                _openVersion++;
                _playbackVersion++;
            }
            old?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_controlLock)
        {
            if (_disposed)
                return;
            lock (_stateLock)
                _disposed = true;
            _clockTimer.Dispose();
            Stop();
        }
    }

    private void StartSession(string path, TimeSpan position, bool playing)
    {
        lock (_stateLock)
            _openVersion++;
        PlaybackSession? replacement = null;
        try
        {
            replacement = CreateSession(path, position);
            InstallSession(replacement, playing);
        }
        catch (Exception error)
        {
            if (replacement is not null)
            {
                lock (_stateLock)
                {
                    if (_session == replacement)
                        _session = null;
                }
                replacement.Dispose();
            }
            var playbackError = WrapOutputError(error);
            lock (_stateLock)
                _lastError = playbackError.Message;
            throw playbackError;
        }
    }

    private bool InstallSession(PlaybackSession replacement, bool playing, long? openRequest = null)
    {
        PlaybackSession? old;
        lock (_stateLock)
        {
            if (_disposed || (openRequest is { } request && request != _openVersion))
                return false;
            old = _session;
            _session = replacement;
            replacement.Volume.Volume = _volume;
            replacement.Tap.Enabled = _analysisEnabled;
            _lastError = null;
            _prepareVersion++;
            replacement.Version = ++_playbackVersion;
        }
        old?.Dispose();
        if (playing)
            replacement.Output.Play();
        return true;
    }

    private static AudioPlaybackException WrapOutputError(Exception error) => error as AudioPlaybackException
        ?? new AudioPlaybackException("Windows could not open the audio output. Choose another output device or check that it is connected.", error);

    private PlaybackSession CreateSession(string path, TimeSpan position)
    {
        MMDevice? device = null;
        AudioSegment? first = null;
        PlaybackSession? session = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = ResolveDevice(enumerator, _deviceId);
            var sampleRate = device.AudioClient.MixFormat.SampleRate;
            first = AudioDecoder.Open(path, sampleRate, position, false);
            var pipeline = new GaplessSampleProvider(first);
            var tap = new AudioTapProvider(pipeline) { Enabled = _analysisEnabled };
            var volume = new VolumeSampleProvider(tap) { Volume = _volume };
            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 120);
            session = new PlaybackSession(output, device, pipeline, tap, volume, sampleRate,
                new AudioBoundary(first.Path, first.Duration, first.SourceOffset, 0));
            var captured = session;
            session.StoppedHandler = (_, args) => OnOutputStopped(captured, args);
            output.PlaybackStopped += session.StoppedHandler;
            pipeline.BoundaryReached += boundary =>
            {
                lock (_stateLock)
                {
                    if (_session == captured)
                        captured.PendingBoundaries.Enqueue(boundary);
                }
            };
            output.Init(new SampleToWaveProvider(volume));
            return session;
        }
        catch
        {
            if (session is not null)
                session.Dispose();
            else
            {
                first?.Dispose();
                device?.Dispose();
            }
            throw;
        }
    }

    private static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, string? id)
    {
        try
        {
            var device = string.IsNullOrWhiteSpace(id)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(id);
            if (device.State != DeviceState.Active)
            {
                device.Dispose();
                throw new AudioPlaybackException("The selected audio output is disconnected. Choose an available output in Settings.");
            }
            return device;
        }
        catch (AudioPlaybackException) { throw; }
        catch (Exception error)
        {
            throw new AudioPlaybackException("No usable Windows audio output was found. Connect an audio device or select another output in Settings.", error);
        }
    }

    private void PollClock(object? state)
    {
        var session = CurrentSession;
        if (session is null || session.Ended)
            return;
        var seconds = session.ReadClock();
        List<AudioBoundary> changed = [];
        lock (_stateLock)
        {
            if (_session == session)
                changed = UpdateBoundaries(session, seconds);
        }
        foreach (var boundary in changed)
            Raise(NextTrackStarted, session, new AudioTrackStartedEventArgs(boundary.Path, session.Version, boundary.StartSeconds));
    }

    private static List<AudioBoundary> UpdateBoundaries(PlaybackSession session, double seconds)
    {
        List<AudioBoundary> changed = [];
        while (session.PendingBoundaries.TryPeek(out var boundary) && boundary.StartSeconds <= seconds + .002)
        {
            session.CurrentBoundary = session.PendingBoundaries.Dequeue();
            changed.Add(session.CurrentBoundary);
        }
        return changed;
    }

    private void OnOutputStopped(PlaybackSession session, StoppedEventArgs args)
    {
        List<AudioBoundary> changed = [];
        string? error = null;
        lock (_stateLock)
        {
            if (_session != session || session.Suppressed || _disposed)
                return;
            changed = UpdateBoundaries(session, args.Exception is null
                ? session.Pipeline.DeliveredSeconds : session.ReadClock());
            session.Ended = true;
            if (args.Exception is not null)
            {
                error = args.Exception is AudioPlaybackException decoderError ? decoderError.Message
                    : "Audio output stopped unexpectedly. Check the output device and try playing the track again.";
                session.Error = args.Exception;
                _lastError = error;
            }
        }
        foreach (var boundary in changed)
            Raise(NextTrackStarted, session, new AudioTrackStartedEventArgs(boundary.Path, session.Version, boundary.StartSeconds));
        if (error is not null)
        {
            var message = error;
            Post(() =>
            {
                if (IsCurrent(session))
                    PlaybackError?.Invoke(this, new AudioPlaybackErrorEventArgs(message, args.Exception));
            });
        }
        Raise(PlaybackStopped, session, new AudioPlaybackStoppedEventArgs(session.Version, error));
    }

    private void Raise(EventHandler? handler, PlaybackSession session, EventArgs? args = null)
    {
        if (handler is null)
            return;
        Post(() =>
        {
            if (IsCurrent(session))
                handler(this, args ?? EventArgs.Empty);
        });
    }

    private bool IsCurrent(PlaybackSession session)
    {
        lock (_stateLock)
            return !_disposed && _session == session && !session.Suppressed;
    }

    private void Post(Action action)
    {
        if (_eventContext is { } context)
            context.Post(_ => action(), null);
        else
            ThreadPool.QueueUserWorkItem(_ => action());
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));
    }

    private sealed class PlaybackSession(
        WasapiOut output, MMDevice device, GaplessSampleProvider pipeline, AudioTapProvider tap,
        VolumeSampleProvider volume, int sampleRate, AudioBoundary first) : IDisposable
    {
        private readonly object _clockLock = new();
        private double _lastClock;
        private int _disposed;
        public WasapiOut Output { get; } = output;
        public GaplessSampleProvider Pipeline { get; } = pipeline;
        public AudioTapProvider Tap { get; } = tap;
        public VolumeSampleProvider Volume { get; } = volume;
        public int SampleRate { get; } = sampleRate;
        public long Version { get; set; }
        public AudioBoundary CurrentBoundary { get; set; } = first;
        public Queue<AudioBoundary> PendingBoundaries { get; } = new();
        public string? NextRequestedPath { get; set; }
        public string? PreparationError { get; set; }
        public Exception? Error { get; set; }
        public volatile bool Ended;
        public volatile bool Suppressed;
        public EventHandler<StoppedEventArgs>? StoppedHandler { get; set; }

        public double ReadClock()
        {
            lock (_clockLock)
            {
                try
                {
                    var bytesPerSecond = Output.OutputWaveFormat.AverageBytesPerSecond;
                    if (bytesPerSecond > 0)
                        _lastClock = Math.Max(_lastClock, Output.GetPosition() / (double)bytesPerSecond);
                }
                catch (Exception) { /* Device removal is reported by PlaybackStopped. */ }
                return _lastClock;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Suppressed = true;
            if (StoppedHandler is not null)
                Output.PlaybackStopped -= StoppedHandler;
            try { Output.Stop(); }
            finally
            {
                try { Output.Dispose(); }
                finally
                {
                    try { Pipeline.Dispose(); }
                    finally { device.Dispose(); }
                }
            }
        }
    }
}
