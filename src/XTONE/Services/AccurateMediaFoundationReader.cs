using System;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace XTONE.Services;

/// <summary>
/// Uses decoded block timestamps to trim a native seek to the requested frame.
/// Some Windows FLAC sources seek to an earlier block (occasionally to zero).
/// MediaFoundationReader alone exposes no timestamp correction for that case.
/// </summary>
internal sealed class AccurateMediaFoundationReader : MediaFoundationReader
{
    private static readonly MediaFoundationReaderSettings ReaderSettings = new()
    {
        RequestFloatOutput = true,
        RepositionInRead = true
    };
    private IMFSourceReader? _sourceReader;
    private byte[] _decoded = [];
    private int _available;
    private int _offset;
    private long _position;
    private long? _seekPosition;
    private long _discardBefore;
    private bool _ended;
    private bool _disposed;

    public AccurateMediaFoundationReader(string path) : base(path, ReaderSettings) { }

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            _position = Math.Min(value, Length) / WaveFormat.BlockAlign * WaveFormat.BlockAlign;
            _seekPosition = _position;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        _sourceReader ??= CreateReader(ReaderSettings);
        if (_seekPosition is { } requested)
        {
            _discardBefore = requested * 10_000_000L / WaveFormat.AverageBytesPerSecond;
            SetNativePosition(_sourceReader, _discardBefore);
            _available = 0;
            _offset = 0;
            _ended = false;
            _seekPosition = null;
        }

        var written = 0;
        while (written < count)
        {
            if (_available > 0)
            {
                var copied = Math.Min(count - written, _available);
                Buffer.BlockCopy(_decoded, _offset, buffer, offset + written, copied);
                _offset += copied;
                _available -= copied;
                written += copied;
                continue;
            }
            if (_ended)
                break;
            ReadDecodedBlock();
        }
        _position += written;
        return written;
    }

    private void ReadDecodedBlock()
    {
        IMFSample? sample = null;
        IMFMediaBuffer? nativeBuffer = null;
        var locked = false;
        try
        {
            _sourceReader!.ReadSample(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
                out _, out var flags, out var timestamp, out sample);
            if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
                throw new InvalidDataException("The Windows decoder reported an invalid audio block.");
            if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                throw new InvalidDataException("The file changes its decoded audio format during playback.");
            _ended = (flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
            if (sample is null)
                return;

            sample.ConvertToContiguousBuffer(out nativeBuffer);
            nativeBuffer.Lock(out var address, out _, out var length);
            locked = true;
            if (_decoded.Length < length)
                _decoded = new byte[length];
            Marshal.Copy(address, _decoded, 0, length);
            _offset = 0;
            _available = length;

            // MF timestamps are expressed in 100ns units. Trim complete PCM
            // frames so an early native seek cannot play pre-target audio.
            var earlyTicks = _discardBefore - unchecked((long)timestamp);
            if (earlyTicks > 0)
            {
                var skipFrames = (long)Math.Ceiling(earlyTicks * (double)WaveFormat.SampleRate / 10_000_000);
                var skipBytes = (int)Math.Min(length, skipFrames * WaveFormat.BlockAlign);
                _offset = skipBytes;
                _available -= skipBytes;
            }
            if (_available > 0)
                _discardBefore = 0;
        }
        finally
        {
            if (locked)
                nativeBuffer!.Unlock();
            if (nativeBuffer is not null)
                Marshal.ReleaseComObject(nativeBuffer);
            if (sample is not null)
                Marshal.ReleaseComObject(sample);
        }
    }

    private static void SetNativePosition(IMFSourceReader reader, long timestamp)
    {
        var variant = PropVariant.FromLong(timestamp);
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            Marshal.StructureToPtr(variant, pointer, false);
            reader.SetCurrentPosition(Guid.Empty, pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_sourceReader is not null)
                Marshal.ReleaseComObject(_sourceReader);
            _sourceReader = null;
        }
        base.Dispose(disposing);
    }
}
