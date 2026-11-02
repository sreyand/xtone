using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace XTONE.Services;

internal sealed class AudioSegment : IDisposable
{
    private readonly IDisposable? _owner;
    private int _disposed;

    public AudioSegment(string path, TimeSpan duration, TimeSpan sourceOffset, ISampleProvider source, IDisposable? owner = null)
    {
        Path = path;
        Duration = duration;
        SourceOffset = sourceOffset;
        Source = source;
        _owner = owner;
    }

    public string Path { get; }
    public TimeSpan Duration { get; }
    public TimeSpan SourceOffset { get; }
    public ISampleProvider Source { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _owner?.Dispose();
    }
}

internal sealed record AudioBoundary(string Path, TimeSpan Duration, TimeSpan SourceOffset, double StartSeconds);

/// <summary>
/// Concatenates decoded samples inside the same output read. Switching tracks never
/// stops the audio device or inserts a block of zero samples between the providers.
/// </summary>
internal sealed class GaplessSampleProvider : ISampleProvider, IDisposable
{
    private readonly object _sync = new();
    private AudioSegment? _current;
    private AudioSegment? _next;
    private long _samplesDelivered;
    private long _sequence;
    private bool _disposed;
    private bool _endOfStream;

    public GaplessSampleProvider(AudioSegment first)
    {
        _current = first;
        WaveFormat = first.Source.WaveFormat;
    }

    public event Action<AudioBoundary>? BoundaryReached;
    public WaveFormat WaveFormat { get; }
    public long Sequence { get { lock (_sync) return _sequence; } }
    public bool EndOfStream { get { lock (_sync) return _endOfStream; } }
    public double DeliveredSeconds { get { lock (_sync) return _samplesDelivered / (double)(WaveFormat.SampleRate * WaveFormat.Channels); } }

    /// <summary>Consumes ownership even when the request became stale.</summary>
    public bool SetNext(long expectedSequence, AudioSegment? next)
    {
        AudioSegment? release;
        bool accepted;
        bool incompatible;
        lock (_sync)
        {
            accepted = !_disposed && !_endOfStream && expectedSequence == _sequence;
            incompatible = accepted && next is not null
                && (next.Source.WaveFormat.SampleRate != WaveFormat.SampleRate || next.Source.WaveFormat.Channels != WaveFormat.Channels);
            release = accepted && !incompatible ? _next : next;
            if (accepted && !incompatible) _next = next;
        }
        // Ownership transfers on every call, including a rejected format. Dispose
        // outside the audio lock without dropping a previously valid preparation.
        release?.Dispose();
        if (incompatible) throw new ArgumentException("Queued audio must use the output sample rate and channel count.", nameof(next));
        return accepted;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var capacity = Buffer.ByteLength(buffer) / sizeof(float); // also supports NAudio's byte/float alias
        if (offset < 0 || count < 0 || offset > capacity - count) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return 0;
        List<AudioSegment>? release = null;
        List<AudioBoundary>? boundaries = null;
        var total = 0;
        try
        {
            lock (_sync)
            {
                if (_disposed || _current is null || _endOfStream)
                    return 0;
                while (total < count)
                {
                    var read = _current.Source.Read(buffer, offset + total, count - total);
                    if (read > 0)
                    {
                        total += read;
                        _samplesDelivered += read;
                        continue;
                    }

                    if (_next is null)
                    {
                        _endOfStream = true;
                        break;
                    }

                    (release ??= []).Add(_current);
                    _current = _next;
                    _next = null;
                    _sequence++;
                    (boundaries ??= []).Add(new AudioBoundary(_current.Path, _current.Duration,
                        _current.SourceOffset, _samplesDelivered / (double)(WaveFormat.SampleRate * WaveFormat.Channels)));
                }
            }
        }
        finally
        {
            if (release is not null)
                foreach (var segment in release)
                    segment.Dispose();
            // The owner receives markers before the output submits this block.
            // Notify outside the provider lock so playback control cannot deadlock it.
            if (boundaries is not null)
                foreach (var boundary in boundaries)
                    BoundaryReached?.Invoke(boundary);
        }
        return total;
    }

    public void Dispose()
    {
        AudioSegment? current;
        AudioSegment? next;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            current = _current;
            next = _next;
            _current = null;
            _next = null;
        }
        current?.Dispose();
        next?.Dispose();
    }
}

internal static class AudioDecoder
{
    public static AudioSegment Open(string path, int outputSampleRate, TimeSpan position, bool prebuffer)
    {
        if (!File.Exists(path))
            throw new AudioPlaybackException($"The audio file is missing: {System.IO.Path.GetFileName(path)}. Relink it in the library.");

        AccurateMediaFoundationReader? reader = null;
        try
        {
            reader = new AccurateMediaFoundationReader(path);
            var duration = reader.TotalTime;
            position = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
            if (position > TimeSpan.Zero)
                reader.CurrentTime = position;

            ISampleProvider source = reader.ToSampleProvider();
            source = source.WaveFormat.Channels switch
            {
                1 => new MonoToStereoSampleProvider(source),
                2 => source,
                _ => new StereoDownmixProvider(source)
            };
            if (source.WaveFormat.SampleRate != outputSampleRate)
                source = new WdlResamplingSampleProvider(source, outputSampleRate);
            source = new DecoderErrorProvider(source, path);
            if (prebuffer)
                source = PrefetchedSampleProvider.Create(source, outputSampleRate * 2 / 4);
            return new AudioSegment(System.IO.Path.GetFullPath(path), duration, position, source, reader);
        }
        catch (AudioPlaybackException)
        {
            reader?.Dispose();
            throw;
        }
        catch (Exception error)
        {
            reader?.Dispose();
            throw new AudioPlaybackException(
                $"Windows could not decode {System.IO.Path.GetFileName(path)}. The file may be damaged, protected, or require an unavailable media codec.",
                error);
        }
    }
}

internal sealed class DecoderErrorProvider(ISampleProvider source, string path) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(float[] buffer, int offset, int count)
    {
        try { return source.Read(buffer, offset, count); }
        catch (AudioPlaybackException) { throw; }
        catch (Exception error)
        {
            throw new AudioPlaybackException($"Windows could not read audio from {System.IO.Path.GetFileName(path)}. The file may be damaged or use an unsupported codec variant.", error);
        }
    }
}

internal sealed class PrefetchedSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float[] _buffer;
    private readonly int _length;
    private int _position;

    private PrefetchedSampleProvider(ISampleProvider source, float[] buffer, int length)
    {
        _source = source;
        _buffer = buffer;
        _length = length;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public static ISampleProvider Create(ISampleProvider source, int sampleCount)
    {
        var buffer = new float[sampleCount];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = source.Read(buffer, length, buffer.Length - length);
            if (read == 0)
                break;
            length += read;
        }
        return new PrefetchedSampleProvider(source, buffer, length);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var copied = Math.Min(count, _length - _position);
        if (copied > 0)
        {
            // SampleToWaveProvider uses NAudio's byte/float WaveBuffer alias.
            // Array.Copy inspects its underlying byte[] runtime type and fails.
            Buffer.BlockCopy(_buffer, _position * sizeof(float), buffer, offset * sizeof(float), copied * sizeof(float));
            _position += copied;
        }
        return copied == count ? copied : copied + _source.Read(buffer, offset + copied, count - copied);
    }
}

internal sealed class StereoDownmixProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _buffer = [];
    public StereoDownmixProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }
    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var channels = _source.WaveFormat.Channels;
        var requested = count / 2 * channels;
        if (_buffer.Length < requested)
            _buffer = new float[requested];
        var frames = _source.Read(_buffer, 0, requested) / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var start = frame * channels;
            var left = _buffer[start];
            var right = _buffer[start + 1];
            var gain = 1f;
            if (channels == 4)
            {
                left += _buffer[start + 2] * .707f;
                right += _buffer[start + 3] * .707f;
                gain += .707f;
            }
            else
            {
                left += _buffer[start + 2] * .707f;
                right += _buffer[start + 2] * .707f;
                gain += .707f;
                // Standard 5.0 / 5.1 speaker order. LFE is omitted from the stereo fold-down.
                var surround = channels == 5 ? 3 : 4;
                if (channels > surround + 1)
                {
                    left += _buffer[start + surround] * .707f;
                    right += _buffer[start + surround + 1] * .707f;
                    gain += .707f;
                }
                if (channels >= 8)
                {
                    left += _buffer[start + 6] * .5f;
                    right += _buffer[start + 7] * .5f;
                    gain += .5f;
                }
            }
            buffer[offset + frame * 2] = left / gain;
            buffer[offset + frame * 2 + 1] = right / gain;
        }
        return frames * 2;
    }
}
