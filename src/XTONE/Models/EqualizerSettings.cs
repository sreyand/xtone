using System.Text.Json.Serialization;

namespace XTONE.Models;

public enum EqualizerFilterType { Peak, LowShelf, HighShelf }

public sealed record EqualizerBand(double FrequencyHz, double GainDb, double Q, EqualizerFilterType Type = EqualizerFilterType.Peak);

/// <summary>An immutable curve plus its playback and audition settings.</summary>
public sealed record EqualizerSettings
{
    public const int MaxBands = 32;

    public EqualizerSettings(bool enabled, IReadOnlyList<EqualizerBand>? bands)
    {
        Enabled = enabled;
        Bands = Array.AsReadOnly(bands is { Count: > 0 }
            ? bands.Take(MaxBands).Select(NormalizeBand).ToArray()
            : DefaultBands());
    }

    public static EqualizerSettings Flat { get; } = new(false, null) { MatchLevel = true };
    public bool Enabled { get; init; }
    public IReadOnlyList<EqualizerBand> Bands { get; }
    public bool MatchLevel { get; init; }
    [JsonIgnore] public bool AuditionOriginal { get; init; }
    [JsonIgnore] public EqualizerSettings? AuditionReference { get; init; }
    public double PreampDb { get; init; }

    public EqualizerSettings Normalize()
    {
        var preamp = FiniteClamp(PreampDb, -60, 0, 0);
        return preamp == PreampDb ? this : this with { PreampDb = preamp };
    }

    public EqualizerSettings WithBand(int index, EqualizerBand band)
    {
        var replacement = Bands.ToArray();
        replacement[index] = band;
        return new EqualizerSettings(Enabled, replacement)
        {
            PreampDb = PreampDb,
            MatchLevel = MatchLevel,
            AuditionOriginal = AuditionOriginal,
            AuditionReference = AuditionReference
        }.Normalize();
    }

    private static EqualizerBand NormalizeBand(EqualizerBand? band)
    {
        if (band is null) return new(1000, 0, 1);
        var type = Enum.IsDefined(band.Type) ? band.Type : EqualizerFilterType.Peak;
        return new(
            FiniteClamp(band.FrequencyHz, 20, 20000, 1000),
            FiniteClamp(band.GainDb, -18, 18, 0),
            FiniteClamp(band.Q, .1, type == EqualizerFilterType.Peak ? 20 : 1, .70710678),
            type);
    }

    private static double FiniteClamp(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static EqualizerBand[] DefaultBands() =>
    [
        new(80, 0, .70710678, EqualizerFilterType.LowShelf),
        new(200, 0, 1),
        new(630, 0, 1),
        new(2000, 0, 1),
        new(5000, 0, 1),
        new(10000, 0, .70710678, EqualizerFilterType.HighShelf)
    ];
}
