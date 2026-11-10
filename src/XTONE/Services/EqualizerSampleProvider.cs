using System.Threading;
using NAudio.Wave;
using XTONE.Models;

namespace XTONE.Services;

/// <summary>Applies a prepared EQ to floating-point audio without changing decoder or output state.</summary>
internal sealed class EqualizerSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Branch _reference;
    private readonly Branch _tuned;
    private readonly LoudnessMatch _match;
    private readonly double _mixStep;
    private Plan _pending;
    private Plan _active;
    private double _tunedMix;
    private double _mixTarget;

    public EqualizerSampleProvider(ISampleProvider source, EqualizerSettings settings)
    {
        _source = source;
        _active = _pending = new Plan(settings, WaveFormat.SampleRate);
        var fadeFrames = Math.Max(1, WaveFormat.SampleRate * 30 / 1000);
        _mixStep = 1d / fadeFrames;
        _reference = new Branch(WaveFormat.Channels, fadeFrames, _active.Reference);
        _tuned = new Branch(WaveFormat.Channels, fadeFrames, _active.Tuned);
        _match = new LoudnessMatch(WaveFormat.SampleRate, WaveFormat.Channels);
        _tunedMix = _mixTarget = _active.PlayTuned ? 1 : 0;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    // Called by the control path. The real-time path only swaps immutable plans.
    public void Configure(EqualizerSettings settings, bool beforePlayback = false)
    {
        var previous = Volatile.Read(ref _pending);
        if (ReferenceEquals(settings, previous.Settings)) return;
        var replacement = new Plan(settings, WaveFormat.SampleRate, previous);
        if (beforePlayback)
        {
            _active = replacement;
            _tuned.Set(replacement.Tuned, false);
            _reference.Set(replacement.Settings.Enabled ? replacement.Reference : Plan.Dry, false);
            _tunedMix = _mixTarget = replacement.PlayTuned ? 1 : 0;
        }
        Volatile.Write(ref _pending, replacement);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read == 0) return 0;

        var next = Volatile.Read(ref _pending);
        if (!ReferenceEquals(next, _active) && !_tuned.IsFading && !_reference.IsFading)
        {
            // Turning off fades to the unchanged input. Keep the previous tuned
            // filters alive for that fade, then the bypass returns input verbatim.
            if (next.Settings.Enabled)
            {
                _tuned.Set(next.Tuned, _active.Settings.Enabled || _tunedMix > 0);
                _reference.Set(next.Reference, true);
            }
            else
                _reference.Set(Plan.Dry, true);
            _mixTarget = next.PlayTuned ? 1 : 0;
            _active = next;
        }

        if (!_active.Settings.Enabled && _tunedMix == 0 && !_reference.IsFading && _match.IsUnity)
            return read;

        var channels = WaveFormat.Channels;
        var match = _active.Settings.Enabled && _active.Settings.MatchLevel;
        for (var index = offset; index < offset + read;)
        {
            _tunedMix = _tunedMix < _mixTarget
                ? Math.Min(_mixTarget, _tunedMix + _mixStep)
                : Math.Max(_mixTarget, _tunedMix - _mixStep);
            for (var channel = 0; channel < channels && index < offset + read; channel++, index++)
            {
                var input = buffer[index];
                if (!float.IsFinite(input)) input = 0;
                var reference = _reference.Process(input, channel);
                var tuned = _tuned.Process(input, channel);
                if (match) _match.Observe(reference, tuned, channel);
                // A swept response estimates steady-state headroom. Protect
                // rare sample overshoots in processed audio with a soft ceiling.
                reference *= _match.ReferenceGain;
                tuned *= _match.TunedGain;
                if (!_reference.IsDry) reference = ProtectSamplePeak(reference);
                if (!_tuned.IsDry) tuned = ProtectSamplePeak(tuned);
                buffer[index] = (float)(reference * (1 - _tunedMix) + tuned * _tunedMix);
            }
            _reference.Advance();
            _tuned.Advance();
            _match.Advance(match);
        }
        return read;
    }

    private static double ProtectSamplePeak(double sample)
    {
        const double knee = .99;
        var magnitude = Math.Abs(sample);
        if (magnitude <= knee) return sample;
        var excess = magnitude - knee;
        return Math.CopySign(knee + excess / (1 + excess / (1 - knee)), sample);
    }

    private sealed record BranchPlan(EqualizerCoefficients[] Filters, double Gain);

    private sealed class Plan
    {
        public static BranchPlan Dry { get; } = new([], 1);

        public Plan(EqualizerSettings settings, int rate, Plan? previous = null)
        {
            Settings = settings;
            if (previous is not null && ReferenceEquals(settings.Bands, previous.Settings.Bands)
                && settings.PreampDb == previous.Settings.PreampDb)
                Tuned = previous.Tuned;
            else
                Tuned = CreateBranch(settings, rate);

            Reference = previous is not null && ReferenceEquals(settings.AuditionReference, previous.Settings.AuditionReference)
                ? previous.Reference
                : settings.AuditionReference is { Enabled: true } alternate ? CreateBranch(alternate, rate) : Dry;
        }

        public EqualizerSettings Settings { get; }
        public BranchPlan Tuned { get; }
        public BranchPlan Reference { get; }
        public bool PlayTuned => Settings.Enabled && !Settings.AuditionOriginal;

        private static BranchPlan CreateBranch(EqualizerSettings settings, int rate)
        {
            var response = new EqualizerResponse(settings, rate);
            return new BranchPlan(response.Filters, Math.Pow(10, response.HeadroomDb / 20));
        }
    }

    // The two cascades keep independent state during edits. No coefficient or
    // recursive state changes are made to the cascade that is still audible.
    private sealed class Branch
    {
        private readonly int _fadeFrames;
        private Cascade _current;
        private Cascade _next;
        private int _remaining;

        public Branch(int channels, int fadeFrames, BranchPlan plan)
        {
            _fadeFrames = fadeFrames;
            _current = new Cascade(channels, plan);
            _next = new Cascade(channels, plan);
        }

        public bool IsFading => _remaining > 0;
        public bool IsDry => !IsFading && _current.Plan.Filters.Length == 0 && _current.Plan.Gain == 1;

        public void Set(BranchPlan plan, bool smooth)
        {
            if (ReferenceEquals(_current.Plan, plan)
                || (_current.Plan.Gain == plan.Gain && _current.Plan.Filters.AsSpan().SequenceEqual(plan.Filters)))
                return;
            if (smooth)
            {
                _next.Reset(plan);
                _remaining = _fadeFrames;
            }
            else _current.Reset(plan);
        }

        public double Process(double sample, int channel)
        {
            var current = _current.Process(sample, channel);
            if (!IsFading) return current;
            var next = _next.Process(sample, channel);
            return current + (next - current) * (1d - (double)_remaining / _fadeFrames);
        }

        public void Advance()
        {
            if (_remaining > 0 && --_remaining == 0)
                (_current, _next) = (_next, _current);
        }
    }

    private sealed class Cascade(int channels, BranchPlan plan)
    {
        private readonly FilterMemory[] _state = new FilterMemory[channels * EqualizerSettings.MaxBands];
        public BranchPlan Plan { get; private set; } = plan;

        public void Reset(BranchPlan replacement)
        {
            Plan = replacement;
            Array.Clear(_state);
        }

        public double Process(double sample, int channel)
        {
            var start = channel * EqualizerSettings.MaxBands;
            var filters = Plan.Filters;
            for (var i = 0; i < filters.Length; i++)
                sample = _state[start + i].Process(filters[i], sample);
            return sample * Plan.Gain;
        }
    }

    private struct FilterMemory
    {
        private double _one;
        private double _two;

        public double Process(EqualizerCoefficients filter, double sample)
        {
            var output = filter.B0 * sample + _one;
            _one = filter.B1 * sample - filter.A1 * output + _two;
            _two = filter.B2 * sample - filter.A2 * output;
            // Prevent extremely long filter tails from entering the denormal range.
            if (sample == 0 && Math.Abs(_one) + Math.Abs(_two) < 1e-24)
                _one = _two = 0;
            return output;
        }
    }

    /// <summary>K-weighted short-term energy estimate for same-content audition, not a LUFS meter.</summary>
    private sealed class LoudnessMatch
    {
        private readonly FilterMemory[] _referenceShelf;
        private readonly FilterMemory[] _referenceHighPass;
        private readonly FilterMemory[] _tunedShelf;
        private readonly FilterMemory[] _tunedHighPass;
        private readonly EqualizerCoefficients _shelf;
        private readonly EqualizerCoefficients _highPass;
        private readonly double _energyAlpha;
        private readonly double _gainAlpha;
        private readonly int _updateFrames;
        private int _untilUpdate;
        private double _referenceFrame;
        private double _tunedFrame;
        private double _referenceEnergy;
        private double _tunedEnergy;
        private double _referenceTarget = 1;
        private double _tunedTarget = 1;
        private bool _measuring;

        public LoudnessMatch(int rate, int channels)
        {
            _referenceShelf = new FilterMemory[channels];
            _referenceHighPass = new FilterMemory[channels];
            _tunedShelf = new FilterMemory[channels];
            _tunedHighPass = new FilterMemory[channels];
            // BS.1770 K-weighting filter prototype, bilinearly mapped to the
            // endpoint rate. The estimator uses exponential energy averaging.
            var k = Math.Tan(Math.PI * Math.Min(1681.974450955533, rate * .4) / rate);
            const double shelfQ = .7071752369554196;
            var high = Math.Pow(10, 3.999843853973347 / 20);
            var middle = Math.Pow(high, .4996667741545416);
            _shelf = EqualizerCoefficients.Normalize(
                high + middle * k / shelfQ + k * k, 2 * (k * k - high), high - middle * k / shelfQ + k * k,
                1 + k / shelfQ + k * k, 2 * (k * k - 1), 1 - k / shelfQ + k * k);
            k = Math.Tan(Math.PI * Math.Min(38.13547087602444, rate * .4) / rate);
            const double passQ = .5003270373238773;
            var a0 = 1 + k / passQ + k * k;
            _highPass = new(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / passQ + k * k) / a0);
            _energyAlpha = 1 - Math.Exp(-1d / (rate * .4));
            _gainAlpha = 1 - Math.Exp(-1d / (rate * .08));
            _updateFrames = Math.Max(1, rate / 20);
            _untilUpdate = _updateFrames;
        }

        public double ReferenceGain { get; private set; } = 1;
        public double TunedGain { get; private set; } = 1;
        public bool IsUnity => ReferenceGain == 1 && TunedGain == 1;

        public void Observe(double reference, double tuned, int channel)
        {
            reference = _referenceHighPass[channel].Process(_highPass, _referenceShelf[channel].Process(_shelf, reference));
            tuned = _tunedHighPass[channel].Process(_highPass, _tunedShelf[channel].Process(_shelf, tuned));
            _referenceFrame += reference * reference;
            _tunedFrame += tuned * tuned;
        }

        public void Advance(bool enabled)
        {
            if (enabled)
            {
                _measuring = true;
                _referenceEnergy += (_referenceFrame - _referenceEnergy) * _energyAlpha;
                _tunedEnergy += (_tunedFrame - _tunedEnergy) * _energyAlpha;
                if (--_untilUpdate == 0)
                {
                    _untilUpdate = _updateFrames;
                    // Hold the last match through silence. Never amplify either
                    // branch or defeat the static headroom for boosted curves.
                    if (_referenceEnergy > 1e-8 && _tunedEnergy > 1e-8)
                    {
                        var ratio = Math.Sqrt(_tunedEnergy / _referenceEnergy);
                        _referenceTarget = Math.Clamp(ratio, .016, 1);
                        _tunedTarget = Math.Clamp(1 / ratio, .016, 1);
                    }
                }
            }
            else if (_measuring)
            {
                _measuring = false;
                _referenceTarget = _tunedTarget = 1;
                _referenceEnergy = _tunedEnergy = 0;
                Array.Clear(_referenceShelf);
                Array.Clear(_referenceHighPass);
                Array.Clear(_tunedShelf);
                Array.Clear(_tunedHighPass);
            }
            _referenceFrame = _tunedFrame = 0;
            ReferenceGain = MoveGain(ReferenceGain, _referenceTarget);
            TunedGain = MoveGain(TunedGain, _tunedTarget);
        }

        private double MoveGain(double gain, double target)
            => Math.Abs(target - gain) < .0001 ? target : gain + (target - gain) * _gainAlpha;
    }
}
