using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace XTONE.Services;

public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private MediaFoundationReader? _reader;
    private AudioTapProvider? _tap;
    private bool _isStopping;

    public event EventHandler? PlaybackStopped;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public void Play(string path)
    {
        Stop();
        _reader = new MediaFoundationReader(path);
        _tap = new AudioTapProvider(_reader.ToSampleProvider());
        _output = new WaveOutEvent { DesiredLatency = 110 };
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(new SampleToWaveProvider(_tap));
        _output.Play();
    }

    public void Pause()
    {
        if (_output?.PlaybackState == PlaybackState.Playing)
            _output.Pause();
        else if (_output?.PlaybackState == PlaybackState.Paused)
            _output.Play();
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is null)
            return;
        _reader.CurrentTime = position < TimeSpan.Zero ? TimeSpan.Zero :
            position > _reader.TotalTime ? _reader.TotalTime : position;
    }

    public void SetVolume(float volume)
    {
        if (_output is not null)
            _output.Volume = Math.Clamp(volume, 0, 1);
    }

    public float[] GetSpectrum() => _tap?.GetSpectrum() ?? new float[32];

    public void Stop()
    {
        _isStopping = true;
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Stop();
            _output.Dispose();
            _output = null;
        }
        _reader?.Dispose();
        _reader = null;
        _tap = null;
        _isStopping = false;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (!_isStopping)
            PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();
}

internal sealed class AudioTapProvider : ISampleProvider
{
    private const int FftSize = 1024;
    private const int BandCount = 32;
    private readonly ISampleProvider _source;
    private readonly float[] _spectrum = new float[BandCount];
    private readonly object _sync = new();

    public AudioTapProvider(ISampleProvider source) => _source = source;
    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var channels = Math.Max(1, WaveFormat.Channels);
        var frames = read / channels;
        for (var frameStart = 0; frameStart + FftSize <= frames; frameStart += FftSize)
            Analyze(buffer, offset, frameStart, channels);
        return read;
    }

    public float[] GetSpectrum()
    {
        lock (_sync)
            return [.. _spectrum];
    }

    private void Analyze(float[] samples, int offset, int frameStart, int channels)
    {
        var real = new double[FftSize];
        var imaginary = new double[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            double mono = 0;
            for (var channel = 0; channel < channels; channel++)
                mono += samples[offset + (frameStart + i) * channels + channel];
            mono /= channels;
            var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
            real[i] = mono * window;
        }

        Transform(real, imaginary);
        var next = new float[BandCount];
        for (var band = 0; band < BandCount; band++)
        {
            var low = Math.Max(1, (int)Math.Pow(FftSize / 2d, band / (double)BandCount));
            var high = Math.Max(low + 1, (int)Math.Pow(FftSize / 2d, (band + 1) / (double)BandCount));
            double energy = 0;
            var bins = 0;
            for (var bin = low; bin < Math.Min(high, FftSize / 2); bin++)
            {
                energy += Math.Sqrt(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]);
                bins++;
            }
            var magnitude = bins == 0 ? 0 : energy / bins / FftSize;
            next[band] = (float)Math.Clamp(Math.Log10(1 + magnitude * 40000) / 2.5, 0, 1);
        }

        lock (_sync)
        {
            for (var i = 0; i < BandCount; i++)
                _spectrum[i] = Math.Max(next[i], _spectrum[i] * 0.62f);
        }
    }

    private static void Transform(double[] real, double[] imaginary)
    {
        var n = real.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var rootReal = Math.Cos(angle);
            var rootImaginary = Math.Sin(angle);
            for (var start = 0; start < n; start += length)
            {
                var weightReal = 1d;
                var weightImaginary = 0d;
                for (var offset = 0; offset < length / 2; offset++)
                {
                    var even = start + offset;
                    var odd = even + length / 2;
                    var oddReal = real[odd] * weightReal - imaginary[odd] * weightImaginary;
                    var oddImaginary = real[odd] * weightImaginary + imaginary[odd] * weightReal;
                    real[odd] = real[even] - oddReal;
                    imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal;
                    imaginary[even] += oddImaginary;
                    (weightReal, weightImaginary) = (
                        weightReal * rootReal - weightImaginary * rootImaginary,
                        weightReal * rootImaginary + weightImaginary * rootReal);
                }
            }
        }
    }
}
