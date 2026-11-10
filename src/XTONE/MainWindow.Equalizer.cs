using System.Windows.Threading;
using XTONE.Models;
using XTONE.Services;

namespace XTONE;

public partial class MainWindow
{
    private EqualizerProfiles? _equalizerProfiles;
    private readonly DispatcherTimer _equalizerSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private Task _equalizerSave = Task.CompletedTask;
    private AudioOutputFormat? _shownEqualizerOutput;
    private int _equalizerOutputRequest;
    private long _equalizerPlaybackVersion = -1;

    private void InitializeEqualizer()
    {
        _equalizerProfiles = new EqualizerProfiles(_store.StorageDescription);
        _player.EqualizerForDevice = endpoint => _equalizerProfiles.Get(endpoint);
        EqualizerPage.Configure(_equalizerProfiles, ApplyEqualizer, QueueEqualizerSave, ShowEqualizerNotice, () => _player.IsPlaying);
        EqualizerPage.IsEnabled = false;
        _equalizerSaveTimer.Tick += (_, _) => FlushEqualizerSave();
    }

    private void ShowEqualizerNotice(string message) => ShowNotice(message);

    private void ApplyEqualizer(EqualizerSettings settings)
    {
        // A device change can finish between a slider gesture and the UI refresh.
        // The new stream already has its own stored profile.
        if (_shownEqualizerOutput is { } output) _player.SetEqualizer(settings, output.Id);
    }

    private void RefreshLiveEqualizerOutput()
    {
        if (_section != "Equalizer" || _player.CurrentPath is null || _player.LastError is not null
            || _player.EffectiveOutputDeviceId is not { } id) return;
        var rate = _player.OutputSampleRate;
        var name = _player.EffectiveOutputDeviceName ?? "Windows audio output";
        var version = _player.PlaybackVersion;
        var sameOutput = _shownEqualizerOutput is { } shown && shown.Id == id && shown.SampleRate == rate && shown.Name == name;
        if (!sameOutput) ShowEqualizerOutput(new AudioOutputFormat(id, name, rate));
        else if (_equalizerPlaybackVersion >= 0 && version != _equalizerPlaybackVersion)
            EqualizerPage.FinishAudition();
        EqualizerPage.IsEnabled = true;
        _equalizerPlaybackVersion = version;
    }

    private async Task ResolveEqualizerOutputAsync()
    {
        if (_section != "Equalizer" || _closingRequested) return;
        var request = ++_equalizerOutputRequest;
        if (_player.CurrentPath is not null && _player.LastError is null && _player.EffectiveOutputDeviceId is not null)
        {
            RefreshLiveEqualizerOutput();
            return;
        }
        EqualizerPage.IsEnabled = false;
        try
        {
            var output = await Task.Run(_player.GetEqualizerOutput);
            if (request != _equalizerOutputRequest || _closed || _closingRequested || _section != "Equalizer") return;
            if (_player.CurrentPath is null || _player.LastError is not null || _player.EffectiveOutputDeviceId is null) ShowEqualizerOutput(output);
            else RefreshLiveEqualizerOutput();
        }
        catch (Exception error) when (error is AudioPlaybackException or System.Runtime.InteropServices.COMException)
        {
            if (request != _equalizerOutputRequest || _closed || _closingRequested || _section != "Equalizer") return;
            if (_player.CurrentPath is not null && _player.LastError is null && _player.EffectiveOutputDeviceId is not null) RefreshLiveEqualizerOutput();
            else
            {
                _shownEqualizerOutput = null;
                EqualizerPage.SetOutput(null, "No audio output · choose a device in Settings", 48000);
            }
        }
    }

    private void ShowEqualizerOutput(AudioOutputFormat output)
    {
        _shownEqualizerOutput = output;
        EqualizerPage.SetOutput(output.Id, output.Name, output.SampleRate);
        EqualizerPage.IsEnabled = true;
    }

    private void QueueEqualizerSave()
    {
        _equalizerSaveTimer.Stop();
        if (!_closingRequested) _equalizerSaveTimer.Start();
    }

    private void FlushEqualizerSave()
    {
        _equalizerSaveTimer.Stop();
        if (_equalizerProfiles is { } profiles && !_isDesignPreview)
            _equalizerSave = SaveEqualizerAsync(profiles);
    }

    private async Task SaveEqualizerAsync(EqualizerProfiles profiles)
    {
        try { await Task.Run(profiles.Save); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!_closed && !_closingRequested) ShowNotice("Couldn’t save equalizer settings. Your current tuning is still active.");
        }
    }
}
