using System;
using NAudio.Wave;

namespace XTONE.Services;

internal sealed class AudioTapProvider : ISampleProvider
{
    private const int FftSize = 1024;
    private const int BandCount = 32;
    private readonly ISampleProvider _source;
    private readonly float[] _spectrum = new float[BandCount];
    private readonly double[] _window = new double[FftSize];
    private readonly double[] _real = new double[FftSize];
    private readonly double[] _imaginary = new double[FftSize];
    private readonly object _sync = new();
    private int _windowPosition;
    private int _channelPosition;
    private double _channelSum;
    private volatile bool _enabled = true;

    public AudioTapProvider(ISampleProvider source) => _source = source;
    public WaveFormat WaveFormat => _source.WaveFormat;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                lock (_sync)
                    Array.Clear(_spectrum);
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (!_enabled)
        {
            _windowPosition = 0;
            _channelPosition = 0;
            _channelSum = 0;
            return read;
        }
        var channels = Math.Max(1, WaveFormat.Channels);
        for (var index = offset; index < offset + read; index++)
        {
            _channelSum += buffer[index];
            if (++_channelPosition < channels)
                continue;
            _window[_windowPosition++] = _channelSum / channels;
            _channelPosition = 0;
            _channelSum = 0;
            if (_windowPosition == FftSize)
            {
                Analyze();
                _windowPosition = 0;
            }
        }
        return read;
    }

    public float[] GetSpectrum()
    {
        lock (_sync)
            return (float[])_spectrum.Clone();
    }

    private void Analyze()
    {
        Array.Clear(_imaginary);
        for (var i = 0; i < FftSize; i++)
            _real[i] = _window[i] * (.5 - .5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
        Transform(_real, _imaginary);
        lock (_sync)
        {
            if (!_enabled)
                return;
            for (var band = 0; band < BandCount; band++)
            {
                var low = Math.Max(1, (int)Math.Pow(FftSize / 2d, band / (double)BandCount));
                var high = Math.Max(low + 1, (int)Math.Pow(FftSize / 2d, (band + 1) / (double)BandCount));
                double energy = 0;
                var bins = 0;
                for (var bin = low; bin < Math.Min(high, FftSize / 2); bin++)
                {
                    energy += Math.Sqrt(_real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin]);
                    bins++;
                }
                var magnitude = bins == 0 ? 0 : energy / bins / FftSize;
                var value = (float)Math.Clamp(Math.Log10(1 + magnitude * 40000) / 2.5, 0, 1);
                _spectrum[band] = Math.Max(value, _spectrum[band] * .62f);
            }
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
