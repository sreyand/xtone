using XTONE.Models;

namespace XTONE.Services;

/// <summary>Digital filter response evaluated at the output device's mix rate.</summary>
public sealed class EqualizerResponse
{
    private readonly int _sampleRate;
    internal EqualizerCoefficients[] Filters { get; }
    public double HeadroomDb { get; }

    public EqualizerResponse(EqualizerSettings settings, int sampleRate = 48000)
        : this(settings, sampleRate, true) { }

    private EqualizerResponse(EqualizerSettings settings, int sampleRate, bool calculateHeadroom)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        settings = settings.Normalize();
        Filters = settings.Bands.Where(band => band.GainDb != 0)
            .Select(band => EqualizerCoefficients.FromBand(band, sampleRate)).ToArray();
        HeadroomDb = settings.PreampDb;
        if (Filters.Length == 0 || !calculateHeadroom) return;

        // Include DC, Nyquist and every filter center. The log sweep also detects
        // boosts from overlapping bands, including shelf overshoot.
        var maximum = Math.Max(GainDbAt(0), GainDbAt(sampleRate / 2d));
        foreach (var band in settings.Bands)
            maximum = Math.Max(maximum, GainDbAt(Math.Min(band.FrequencyHz, sampleRate * .475)));
        const int intervals = 1024;
        var lower = Math.Min(10, sampleRate * .01);
        var ratio = Math.Pow(sampleRate / (2 * lower), 1d / intervals);
        for (var step = 0; step < intervals; step++, lower *= ratio)
            maximum = Math.Max(maximum, GainDbAt(lower));

        if (maximum > .01)
            HeadroomDb = Math.Min(HeadroomDb, -maximum - .2);
    }

    public double GainDbAt(double frequencyHz)
    {
        var omega = 2 * Math.PI * Math.Clamp(frequencyHz, 0, _sampleRate / 2d) / _sampleRate;
        var cosine = Math.Cos(omega);
        var sine = Math.Sin(omega);
        var cosine2 = 2 * cosine * cosine - 1;
        var sine2 = 2 * sine * cosine;
        var power = 1d;
        foreach (var filter in Filters)
        {
            var nr = filter.B0 + filter.B1 * cosine + filter.B2 * cosine2;
            var ni = filter.B1 * sine + filter.B2 * sine2;
            var dr = 1 + filter.A1 * cosine + filter.A2 * cosine2;
            var di = filter.A1 * sine + filter.A2 * sine2;
            power *= (nr * nr + ni * ni) / (dr * dr + di * di);
        }
        return 10 * Math.Log10(power);
    }

    public static double GetGainDb(EqualizerSettings settings, double frequencyHz, int sampleRate = 48000)
        => new EqualizerResponse(settings, sampleRate, false).GainDbAt(frequencyHz);

    public static double GetHeadroomDb(EqualizerSettings settings, int sampleRate = 48000)
        => new EqualizerResponse(settings, sampleRate).HeadroomDb;
}

internal readonly record struct EqualizerCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    public static EqualizerCoefficients FromBand(EqualizerBand band, int rate)
    {
        // Robert Bristow-Johnson's Audio EQ Cookbook. Use the shelf's equivalent
        // Q in alpha so imported parametric profiles retain their specified shape.
        var omega = 2 * Math.PI * Math.Min(band.FrequencyHz, rate * .475) / rate;
        var a = Math.Pow(10, band.GainDb / 40);
        var c = Math.Cos(omega);
        var alpha = Math.Sin(omega) / (2 * band.Q);
        var beta = 2 * Math.Sqrt(a) * alpha;
        var sum = a + 1;
        var diff = a - 1;
        return band.Type switch
        {
            EqualizerFilterType.LowShelf => Normalize(
                a * (sum - diff * c + beta), 2 * a * (diff - sum * c), a * (sum - diff * c - beta),
                sum + diff * c + beta, -2 * (diff + sum * c), sum + diff * c - beta),
            EqualizerFilterType.HighShelf => Normalize(
                a * (sum + diff * c + beta), -2 * a * (diff + sum * c), a * (sum + diff * c - beta),
                sum - diff * c + beta, 2 * (diff - sum * c), sum - diff * c - beta),
            _ => Normalize(1 + alpha * a, -2 * c, 1 - alpha * a,
                1 + alpha / a, -2 * c, 1 - alpha / a)
        };
    }

    public static EqualizerCoefficients Normalize(double b0, double b1, double b2, double a0, double a1, double a2)
        => new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
}
