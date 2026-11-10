using XTONE.Models;

namespace XTONE.Services;

public sealed class EqualizerTasteSession
{
    private const double TrialGainDb = 1.5;
    private const double MaximumGainDb = 18;

    private static readonly (string Label, EqualizerBand Filter)[] Axes =
    [
        ("Bass", new EqualizerBand(120, 0, 0.707, EqualizerFilterType.LowShelf)),
        ("Presence", new EqualizerBand(3000, 0, 0.8, EqualizerFilterType.Peak)),
        ("Air", new EqualizerBand(8500, 0, 0.707, EqualizerFilterType.HighShelf))
    ];

    private readonly bool _startsWithBoost = Random.Shared.Next(2) == 0;
    private int _axis;

    public EqualizerTasteSession(EqualizerSettings baseline)
    {
        Result = (baseline with { AuditionOriginal = false, AuditionReference = null }).Normalize();
        Reference = Current = Result;
        SetOptions();
    }

    public int Step => Math.Min(_axis + 1, Axes.Length);
    public string Label => Axes[Step - 1].Label;
    public bool IsComplete => _axis >= Axes.Length;
    public bool CanCompare => !ReferenceEquals(Reference, Current);
    public EqualizerSettings Result { get; private set; }
    public EqualizerSettings Reference { get; private set; }
    public EqualizerSettings Current { get; private set; }

    public void Choose(bool preferCandidate)
    {
        if (IsComplete)
            return;

        Result = preferCandidate ? Current : Reference;
        Skip();
    }

    public void Skip()
    {
        if (IsComplete)
            return;

        _axis++;
        SetOptions();
    }

    private void SetOptions()
    {
        if (IsComplete)
        {
            Reference = Current = Result;
            return;
        }

        var filter = Axes[_axis].Filter;
        var referenceGain = (_axis % 2 == 0) == _startsWithBoost ? TrialGainDb : -TrialGainDb;
        Reference = CreateOption(filter, referenceGain);
        Current = CreateOption(filter, -referenceGain);
    }

    private EqualizerSettings CreateOption(EqualizerBand filter, double gain)
    {
        var settings = Result with { Enabled = true };
        for (var index = 0; index < settings.Bands.Count; index++)
        {
            var existing = settings.Bands[index];
            if (existing.Type == filter.Type && existing.FrequencyHz == filter.FrequencyHz &&
                existing.Q == filter.Q && Math.Abs(existing.GainDb) <= MaximumGainDb - TrialGainDb)
                return settings.WithBand(index, existing with { GainDb = existing.GainDb + gain });
        }

        if (settings.Bands.Count < EqualizerSettings.MaxBands)
            return new EqualizerSettings(true, [.. settings.Bands, filter with { GainDb = gain }])
            {
                MatchLevel = settings.MatchLevel,
                PreampDb = settings.PreampDb
            };

        // At the filter limit, keep every existing band and use the nearest
        // compatible band with room for both directions of the comparison.
        var nearest = -1;
        var nearestDistance = double.MaxValue;
        for (var index = 0; index < settings.Bands.Count; index++)
        {
            var existing = settings.Bands[index];
            if (existing.Type != filter.Type || Math.Abs(existing.GainDb) > MaximumGainDb - TrialGainDb)
                continue;

            var distance = Math.Abs(Math.Log2(existing.FrequencyHz / filter.FrequencyHz));
            if (distance <= 1 && distance < nearestDistance)
            {
                nearest = index;
                nearestDistance = distance;
            }
        }

        if (nearest < 0)
            return Result;

        var band = settings.Bands[nearest];
        return settings.WithBand(nearest, band with { GainDb = band.GainDb + gain });
    }
}
