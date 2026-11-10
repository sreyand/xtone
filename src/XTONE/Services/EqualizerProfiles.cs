using System.Text.Json;
using XTONE.Models;

namespace XTONE.Services;

public sealed class EqualizerProfiles
{
    public const int MaximumPresetNameLength = 80;

    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _writeGate = new();
    private readonly string _path;
    private ProfileSnapshot _snapshot;
    private bool _preserveBackup;
    private bool _dirty;

    public EqualizerProfiles(string directory)
    {
        _path = Path.Combine(Path.GetFullPath(directory), "equalizer.json");
        (_snapshot, LoadError, _preserveBackup) = Load(_path);
    }

    public string? LoadError { get; }

    public IReadOnlyList<string> PresetNames => Volatile.Read(ref _snapshot).Presets.Keys
        .OrderBy(name => name, KeyComparer).ToArray();

    public EqualizerSettings Get(string? endpoint)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Devices.GetValueOrDefault(EndpointKey(endpoint)) ?? EqualizerSettings.Flat;
    }

    public void Set(string? endpoint, EqualizerSettings settings)
    {
        var key = EndpointKey(endpoint);
        var value = PersistentSettings(settings);
        lock (_writeGate)
        {
            var current = _snapshot.Devices.GetValueOrDefault(key) ?? EqualizerSettings.Flat;
            if (SameSettings(current, value))
                return;

            // Dictionaries in published snapshots are never mutated. An audio
            // endpoint can look up its profile while the UI edits or saves one.
            var devices = new Dictionary<string, EqualizerSettings>(_snapshot.Devices, KeyComparer)
            {
                [key] = value
            };
            Volatile.Write(ref _snapshot, _snapshot with { Devices = devices });
            _dirty = true;
        }
    }

    public EqualizerSettings? GetPreset(string name)
    {
        var key = PresetKey(name);
        return key is null ? null : Volatile.Read(ref _snapshot).Presets.GetValueOrDefault(key);
    }

    public void SavePreset(string name, EqualizerSettings settings)
    {
        var key = PresetKey(name) ?? throw new ArgumentException(
            $"Enter a preset name with 1 to {MaximumPresetNameLength} characters.", nameof(name));
        var value = PersistentSettings(settings);
        lock (_writeGate)
        {
            var current = _snapshot.Presets.GetValueOrDefault(key);
            if (current is not null && SameSettings(current, value))
                return;

            var presets = new Dictionary<string, EqualizerSettings>(_snapshot.Presets, KeyComparer)
            {
                [key] = value
            };
            Volatile.Write(ref _snapshot, _snapshot with { Presets = presets });
            _dirty = true;
        }
    }

    public void RemovePreset(string name)
    {
        var key = PresetKey(name);
        if (key is null)
            return;

        lock (_writeGate)
        {
            if (!_snapshot.Presets.ContainsKey(key))
                return;

            var presets = new Dictionary<string, EqualizerSettings>(_snapshot.Presets, KeyComparer);
            presets.Remove(key);
            Volatile.Write(ref _snapshot, _snapshot with { Presets = presets });
            _dirty = true;
        }
    }

    public void Save()
    {
        lock (_writeGate)
        {
            if (!_dirty)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 32768, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, _snapshot, Json);
                    stream.Flush(flushToDisk: true);
                }

                if (!_preserveBackup && File.Exists(_path))
                    File.Replace(temporary, _path, _path + ".bak", ignoreMetadataErrors: true);
                else
                    File.Move(temporary, _path, overwrite: true);

                _preserveBackup = false;
                _dirty = false;
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static (ProfileSnapshot Snapshot, string? Error, bool PreserveBackup) Load(string path)
    {
        try
        {
            return (Read(path), null, false);
        }
        catch (Exception error) when (IsReadFailure(error))
        {
            try
            {
                return (Read(path + ".bak"), "Equalizer settings were recovered from a backup.", true);
            }
            catch (Exception backupError) when (IsReadFailure(backupError))
            {
                var missing = IsMissing(error) && IsMissing(backupError);
                var message = missing ? null : "Saved equalizer settings could not be read. Default settings are active.";
                return (new ProfileSnapshot(new(KeyComparer), new(KeyComparer)), message, !missing);
            }
        }
    }

    private static ProfileSnapshot Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var saved = JsonSerializer.Deserialize<ProfileSnapshot>(stream, Json);
        if (saved is null || saved.Version != 1)
            throw new JsonException("Unsupported equalizer settings format.");

        var devices = new Dictionary<string, EqualizerSettings>(KeyComparer);
        if (saved.Devices is not null)
            foreach (var (endpoint, value) in saved.Devices)
                if (value is not null)
                    devices[EndpointKey(endpoint)] = PersistentSettings(value);

        var presets = new Dictionary<string, EqualizerSettings>(KeyComparer);
        if (saved.Presets is not null)
            foreach (var (name, value) in saved.Presets)
                if (value is not null && PresetKey(name) is { } key)
                    presets[key] = PersistentSettings(value);

        return new ProfileSnapshot(devices, presets);
    }

    private static bool IsReadFailure(Exception error) =>
        error is IOException or JsonException or UnauthorizedAccessException or ArgumentException;

    private static bool IsMissing(Exception error) =>
        error is FileNotFoundException or DirectoryNotFoundException;

    private static string EndpointKey(string? endpoint) => endpoint?.Trim() ?? string.Empty;

    private static string? PresetKey(string? name)
    {
        var key = name?.Trim();
        return key is { Length: > 0 and <= MaximumPresetNameLength } && !key.Any(char.IsControl) ? key : null;
    }

    private static EqualizerSettings PersistentSettings(EqualizerSettings settings) =>
        (settings with { AuditionOriginal = false, AuditionReference = null }).Normalize();

    private static bool SameSettings(EqualizerSettings left, EqualizerSettings right) =>
        left.Enabled == right.Enabled && left.MatchLevel == right.MatchLevel &&
        left.PreampDb == right.PreampDb && left.Bands.SequenceEqual(right.Bands);

    private sealed record ProfileSnapshot(
        Dictionary<string, EqualizerSettings> Devices,
        Dictionary<string, EqualizerSettings> Presets)
    {
        public int Version { get; init; } = 1;
    }
}
