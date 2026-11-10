using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using XTONE.Models;

namespace XTONE.Services;

/// <summary>Interchanges self-contained parametric presets with AutoEq and Equalizer APO.</summary>
public static class EqualizerApoPreset
{
    private const int MaxPresetSize = 64 * 1024;
    private const string Number = @"[+-]?(?:[0-9]+(?:[.,][0-9]*)?|[.,][0-9]+)(?:[eE][+-]?[0-9]+)?";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex FilterCommand = new(@"\AFilter(?:\s+[0-9]+)?\z", Options);
    private static readonly Regex InactiveFilter = new(@"\A(?:OFF(?:\s+.*)?|ON\s+None)\z", Options);
    private static readonly Regex Filter = new(
        $@"\AON\s+(?<type>PK|PEQ|LSC|HSC|LS|HS)(?:\s+(?<slope>{Number})\s*dB)?\s+Fc\s*(?<frequency>{Number})\s*Hz\s+Gain\s*(?<gain>{Number})\s*dB(?:\s+Q\s*(?<q>{Number}))?\z", Options);
    private static readonly Regex Preamp = new($@"\A(?<gain>{Number})\s*dB\z", Options);

    public static EqualizerSettings Load(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaxPresetSize) throw new FormatException("The preset exceeds 64 KB.");

        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        var buffer = new char[MaxPresetSize + 1];
        try
        {
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            return Parse(new string(buffer, 0, count));
        }
        catch (DecoderFallbackException error)
        {
            throw new FormatException("The preset is not a readable text file.", error);
        }
    }

    public static EqualizerSettings Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxPresetSize) throw new FormatException("The preset exceeds 64 KB.");

        var bands = new List<EqualizerBand>();
        var preampDb = 0d;
        var hasCommand = false;
        using var reader = new StringReader(text.TrimStart('\uFEFF'));
        var number = 0;
        while (reader.ReadLine() is { } rawLine)
        {
            number++;
            var comment = rawLine.IndexOf('#');
            var line = (comment >= 0 ? rawLine[..comment] : rawLine).Trim();
            if (line.Length == 0) continue;

            var colon = line.IndexOf(':');
            if (colon < 0) throw Error(number, "Expected a filter or preamp command.");
            var command = line[..colon].Trim();
            var parameters = line[(colon + 1)..].Trim();

            if (command.Equals("Preamp", StringComparison.OrdinalIgnoreCase))
            {
                var match = Preamp.Match(parameters);
                if (!match.Success) throw Error(number, "Use a preamp value in dB.");
                preampDb += ReadValue(match.Groups["gain"], -60, 0, "Preamp", number);
                if (preampDb < -60) throw Error(number, "Combined preamp attenuation exceeds 60 dB.");
            }
            else if (FilterCommand.IsMatch(command))
            {
                if (!InactiveFilter.IsMatch(parameters))
                {
                    if (bands.Count == EqualizerSettings.MaxBands)
                        throw Error(number, $"XTONE supports up to {EqualizerSettings.MaxBands} active filters.");
                    bands.Add(ReadFilter(parameters, number));
                }
            }
            else
            {
                throw Error(number, "Only standalone filter and preamp commands are supported.");
            }

            hasCommand = true;
        }

        if (!hasCommand) throw new FormatException("The preset contains no filters or preamp.");
        return new EqualizerSettings(true, bands.Count == 0 ? EqualizerSettings.Flat.Bands : bands.ToArray())
        {
            PreampDb = preampDb
        };
    }

    /// <summary>Exports the selected curve with fixed headroom, independent of audition state.</summary>
    public static string Format(EqualizerSettings settings, int sampleRate = 48000)
    {
        var tuning = settings.Normalize() with { Enabled = true, MatchLevel = false };
        var attenuation = EqualizerResponse.GetHeadroomDb(tuning, sampleRate);
        var output = new StringBuilder();
        output.AppendLine("# XTONE parametric EQ. Filters and headroom use the source output sample rate.");
        output.AppendLine($"# Output sample rate: {sampleRate.ToString(CultureInfo.InvariantCulture)} Hz");
        output.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Preamp: {attenuation:0.000} dB"));
        for (var index = 0; index < tuning.Bands.Count; index++)
        {
            var band = tuning.Bands[index];
            var type = band.Type switch
            {
                EqualizerFilterType.LowShelf => "LSC",
                EqualizerFilterType.HighShelf => "HSC",
                _ => "PK"
            };
            var frequency = Math.Min(band.FrequencyHz, sampleRate * .475).ToString("R", CultureInfo.InvariantCulture);
            // Equalizer APO accepts REW's period thousands separator. A fourth decimal
            // digit makes a fractional frequency unambiguous in the exported text.
            if (UsesRewThousandsSeparator(frequency)) frequency += "0";
            output.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Filter {index + 1}: ON {type} Fc {frequency} Hz Gain {band.GainDb:R} dB Q {band.Q:R}"));
        }

        return output.ToString();
    }

    private static EqualizerBand ReadFilter(string parameters, int line)
    {
        var match = Filter.Match(parameters);
        if (!match.Success) throw Error(line, "Use a peaking, low shelf, or high shelf filter with frequency, gain and Q.");

        var typeName = match.Groups["type"].Value.ToUpperInvariant();
        var type = typeName switch
        {
            "LS" or "LSC" => EqualizerFilterType.LowShelf,
            "HS" or "HSC" => EqualizerFilterType.HighShelf,
            _ => EqualizerFilterType.Peak
        };
        var frequency = ReadValue(match.Groups["frequency"], 20, 20000, "Frequency", line, rewFrequency: true);
        var gain = ReadValue(match.Groups["gain"], -18, 18, "Gain", line);
        var qGroup = match.Groups["q"];
        var slopeGroup = match.Groups["slope"];
        if (type == EqualizerFilterType.Peak)
        {
            if (!qGroup.Success || slopeGroup.Success) throw Error(line, "Peaking filters require Q.");
            return new EqualizerBand(frequency, gain, ReadValue(qGroup, .1, 20, "Q", line), type);
        }

        if (qGroup.Success && slopeGroup.Success) throw Error(line, "Use either shelf Q or slope, not both.");

        // APO's default shelf slope is 0.9. The Audio EQ Cookbook relates shelf
        // slope S and Q through the same alpha term, so either can be represented.
        var amplitude = Math.Pow(10, gain / 40);
        var reciprocal = amplitude + 1 / amplitude;
        var slope = slopeGroup.Success ? ReadValue(slopeGroup, .01, 12, "Shelf slope", line) / 12 : .9;
        var q = qGroup.Success
            ? ReadValue(qGroup, .1, 1, "Shelf Q", line)
            : 1 / Math.Sqrt((1 / slope - 1) * reciprocal + 2);

        // In the APO syntax, LS/HS with an explicit width uses the corner frequency.
        // XTONE and the LSC/HSC forms use the frequency at the shelf midpoint.
        if ((typeName is "LS" or "HS") && (qGroup.Success || slopeGroup.Success))
        {
            var inverseSlope = qGroup.Success ? (1 / (q * q) - 2) / reciprocal + 1 : 1 / slope;
            var shift = Math.Pow(10, Math.Abs(gain) * inverseSlope / 80);
            frequency = type == EqualizerFilterType.LowShelf ? frequency * shift : frequency / shift;
        }

        if (q < .1 || q > 1) throw Error(line, "The shelf slope is outside XTONE's supported width range.");
        if (frequency < 20 || frequency > 20000) throw Error(line, "The shelf midpoint must be between 20 and 20000 Hz.");
        return new EqualizerBand(frequency, gain, q, type);
    }

    private static double ReadValue(Group group, double min, double max, string field, int line, bool rewFrequency = false)
    {
        var valueText = group.Value.Replace(',', '.');
        if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw Error(line, $"{field} must be a finite number.");

        if (rewFrequency && UsesRewThousandsSeparator(valueText))
            value *= 1000;
        if (value < min || value > max)
            throw Error(line, string.Create(CultureInfo.InvariantCulture, $"{field} must be between {min} and {max}."));
        return value;
    }

    private static bool UsesRewThousandsSeparator(string value)
        => value.Length >= 5 && value[^4] == '.' && !value.Contains('e', StringComparison.OrdinalIgnoreCase);

    private static FormatException Error(int line, string message) => new($"Preset line {line}. {message}");
}
