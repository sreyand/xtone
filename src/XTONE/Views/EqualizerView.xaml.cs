using System.Globalization;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using XTONE.Models;
using XTONE.Services;

namespace XTONE.Views;

public partial class EqualizerView : UserControl
{
    private EqualizerProfiles? _profiles;
    private Action<EqualizerSettings>? _apply;
    private Action? _save;
    private Action<string>? _notice;
    private Func<bool>? _isPlaying;
    private string? _endpoint;
    private int _sampleRate = 48000;
    private EqualizerSettings _settings = EqualizerSettings.Flat;
    private EqualizerTasteSession? _taste;
    private bool _original;
    private bool _reverseTaste;
    private bool _updating;
    private int _band;
    private string[] _presetNames = [];

    public EqualizerView()
    {
        InitializeComponent();
        ResponseGraph.BandSelected += index => { _band = index; RefreshControls(); };
        ResponseGraph.BandEdited += EditFromGraph;
        ResponseGraph.EditFinished += () => _save?.Invoke();
    }

    public void Configure(EqualizerProfiles profiles, Action<EqualizerSettings> apply, Action save,
        Action<string> notice, Func<bool> isPlaying)
    {
        _profiles = profiles;
        _apply = apply;
        _save = save;
        _notice = notice;
        _isPlaying = isPlaying;
        _settings = profiles.Get(null);
        RefreshPresets();
        RefreshControls();
    }

    public void SetOutput(string? endpoint, string name, int sampleRate)
    {
        if (string.Equals(_endpoint, endpoint, StringComparison.OrdinalIgnoreCase)
            && OutputName.Text == name && _sampleRate == sampleRate) return;
        if (!string.Equals(_endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
        {
            _endpoint = endpoint;
            _taste = null;
            _original = false;
            _settings = _profiles?.Get(endpoint) ?? EqualizerSettings.Flat;
            _band = 0;
            RefreshPresets();
            PresetSavePanel.Visibility = Visibility.Collapsed;
        }
        _sampleRate = sampleRate;
        OutputName.Text = name;
        RefreshControls();
    }

    public void FinishAudition()
    {
        if (_taste is null && !_original) return;
        _taste = null;
        _original = false;
        Apply();
        RefreshControls();
    }

    private void Apply()
    {
        if (_taste is { } taste)
        {
            var a = _reverseTaste ? taste.Current : taste.Reference;
            var b = _reverseTaste ? taste.Reference : taste.Current;
            _apply?.Invoke(b with { AuditionReference = a, AuditionOriginal = _original, MatchLevel = true });
            return;
        }
        _apply?.Invoke(_settings with { AuditionOriginal = _original });
    }

    private void SetSettings(EqualizerSettings settings, bool save = true)
    {
        _settings = settings.Normalize();
        _original = false;
        _profiles?.Set(_endpoint, _settings);
        Apply();
        RefreshControls();
        if (save) _save?.Invoke();
    }

    private void RefreshControls()
    {
        _updating = true;
        try
        {
            var guided = _taste is not null;
            EnabledBox.IsChecked = _settings.Enabled;
            EnabledBox.IsEnabled = PresetPicker.IsEnabled = !guided;
            MatchBox.IsChecked = guided || _settings.MatchLevel;
            MatchBox.IsEnabled = _settings.Enabled && !guided;
            CompareA.Content = guided ? "Listen A" : "Original";
            CompareB.Content = guided ? "Listen B" : "Tuned";
            CompareA.SetResourceReference(BackgroundProperty, _original ? "SelectionBrush" : "PaperBrush");
            CompareB.SetResourceReference(BackgroundProperty, !_original && (_settings.Enabled || guided) ? "SelectionBrush" : "PaperBrush");
            TasteButton.Visibility = guided ? Visibility.Collapsed : Visibility.Visible;
            TastePanel.Visibility = guided ? Visibility.Visible : Visibility.Collapsed;
            TastePrompt.Text = guided ? $"Step {_taste!.Step} of 3 · {_taste.Label}. Listen to A and B, then choose." : "";
            BandEditor.Visibility = GraphFrame.Visibility = guided ? Visibility.Collapsed : Visibility.Visible;
            CompareA.IsEnabled = CompareB.IsEnabled = guided ? _taste!.CanCompare : _settings.Enabled;
            PreferA.IsEnabled = PreferB.IsEnabled = guided && _taste!.CanCompare;
            _band = Math.Clamp(_band, 0, _settings.Bands.Count - 1);
            if (BandPicker.ItemsSource is not string[] old || old.Length != _settings.Bands.Count)
                BandPicker.ItemsSource = Enumerable.Range(1, _settings.Bands.Count).Select(i => $"Band {i}").ToArray();
            BandPicker.SelectedIndex = _band;
            var band = _settings.Bands[_band];
            TypePicker.SelectedIndex = (int)band.Type;
            FrequencyBox.Text = band.FrequencyHz.ToString("0.##");
            GainBox.Text = band.GainDb.ToString("0.##");
            WidthBox.Text = band.Q.ToString("0.###");
            var preview = _settings with { Enabled = true };
            ResponseGraph.Show(preview, _sampleRate, _band);
            var headroom = EqualizerResponse.GetHeadroomDb(preview, _sampleRate);
            HeadroomText.Text = guided ? "Loudness matching estimates level from the current audio."
                : _settings.Enabled ? $"Preamp {headroom:0.0} dB · automatic headroom" : "EQ off";
        }
        finally { _updating = false; }
    }

    private void EnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_updating && _profiles is not null)
            SetSettings(_settings with { Enabled = EnabledBox.IsChecked == true });
    }

    private void MatchChanged(object sender, RoutedEventArgs e)
    {
        if (_updating || _profiles is null) return;
        _settings = _settings with { MatchLevel = MatchBox.IsChecked == true };
        _profiles.Set(_endpoint, _settings);
        Apply();
        _save?.Invoke();
    }

    private void CompareClick(object sender, RoutedEventArgs e)
    {
        _original = Equals(((Button)sender).Tag, "A");
        Apply();
        RefreshControls();
    }

    private void BandChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || BandPicker.SelectedIndex < 0) return;
        _band = BandPicker.SelectedIndex;
        RefreshControls();
    }

    private void TypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _profiles is null || TypePicker.SelectedIndex < 0) return;
        EditBand(_settings.Bands[_band] with { Type = (EqualizerFilterType)TypePicker.SelectedIndex });
    }

    private void AddBandClick(object sender, RoutedEventArgs e)
    {
        if (_settings.Bands.Count >= EqualizerSettings.MaxBands)
        {
            _notice?.Invoke($"This curve already has {EqualizerSettings.MaxBands} bands.");
            return;
        }
        _band = _settings.Bands.Count;
        ReplaceBands([.. _settings.Bands, new EqualizerBand(1000, 0, 1)]);
    }

    private void RemoveBandClick(object sender, RoutedEventArgs e)
    {
        if (_settings.Bands.Count <= 1) { _notice?.Invoke("Keep at least one band or reset the curve."); return; }
        ReplaceBands(_settings.Bands.Where((_, index) => index != _band).ToArray());
    }

    private void ReplaceBands(IReadOnlyList<EqualizerBand> bands)
    {
        RefreshPresets();
        SetSettings(new EqualizerSettings(_settings.Enabled, bands) { MatchLevel = _settings.MatchLevel, PreampDb = _settings.PreampDb });
    }

    private void EditBand(EqualizerBand band, bool save = true)
    {
        if (PresetPicker.SelectedIndex > 0) RefreshPresets();
        SetSettings(_settings.WithBand(_band, band), save);
    }

    private void EditFromGraph(int index, double frequency, double gain)
    {
        if (_taste is not null) return;
        _band = index;
        var band = _settings.Bands[index];
        EditBand(band with { FrequencyHz = frequency, GainDb = gain }, save: false);
    }

    private void FieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitFields(); e.Handled = true; }
        else if (e.Key == Key.Escape) { RefreshControls(); e.Handled = true; }
    }

    private void FieldLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitFields();

    private void CommitFields()
    {
        if (_updating || _taste is not null || _profiles is null) return;
        var band = _settings.Bands[_band];
        if (!TryNumber(FrequencyBox.Text, out var frequency) || !TryNumber(GainBox.Text, out var gain)
            || !TryNumber(WidthBox.Text, out var q))
        {
            _notice?.Invoke("Enter a number for frequency, gain and Q.");
            RefreshControls();
            return;
        }
        var edited = band with
        {
            FrequencyHz = FrequencyBox.Text == band.FrequencyHz.ToString("0.##") ? band.FrequencyHz : frequency,
            GainDb = GainBox.Text == band.GainDb.ToString("0.##") ? band.GainDb : gain,
            Q = WidthBox.Text == band.Q.ToString("0.###") ? band.Q : q
        };
        if (edited != band) EditBand(edited);
    }

    private static bool TryNumber(string value, out double result)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result) && double.IsFinite(result);

    private void RefreshPresets(string? selected = null)
    {
        _updating = true;
        try
        {
            _presetNames = _profiles?.PresetNames.ToArray() ?? [];
            PresetPicker.ItemsSource = new[] { "Presets", "Flat" }.Concat(_presetNames).ToArray();
            PresetPicker.SelectedIndex = selected is null ? 0 : Array.IndexOf(_presetNames, selected) + 2;
        }
        finally { _updating = false; }
    }

    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _profiles is null || _taste is not null) return;
        var index = PresetPicker.SelectedIndex;
        if (index == 1) SetSettings(EqualizerSettings.Flat with { Enabled = _settings.Enabled, MatchLevel = _settings.MatchLevel });
        else if (index >= 2 && _profiles.GetPreset(_presetNames[index - 2]) is { } preset)
            SetSettings(preset with { Enabled = true, MatchLevel = _settings.MatchLevel });
    }

    private void OptionsClick(object sender, RoutedEventArgs e)
    {
        if (_taste is not null || sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SavePresetClick(object sender, RoutedEventArgs e)
    {
        PresetSavePanel.Visibility = Visibility.Visible;
        PresetName.Clear();
        PresetName.Focus();
        PresetName.BringIntoView();
    }

    private void PresetNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ConfirmPresetClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelPresetClick(sender, e); e.Handled = true; }
    }

    private void ConfirmPresetClick(object sender, RoutedEventArgs e)
    {
        var name = PresetName.Text.Trim();
        if (name.Length == 0 || name.Equals("Flat", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Presets", StringComparison.OrdinalIgnoreCase))
        {
            _notice?.Invoke("Choose a name for the preset.");
            return;
        }
        if (_profiles?.GetPreset(name) is not null)
        {
            _notice?.Invoke("A preset already uses that name.");
            return;
        }
        try
        {
            _profiles?.SavePreset(name, _settings);
            _save?.Invoke();
            RefreshPresets(name);
            PresetSavePanel.Visibility = Visibility.Collapsed;
        }
        catch (ArgumentException error) { _notice?.Invoke(error.Message); }
    }

    private void CancelPresetClick(object sender, RoutedEventArgs e) => PresetSavePanel.Visibility = Visibility.Collapsed;

    private void RemovePresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetPicker.SelectedIndex < 2) { _notice?.Invoke("Select a saved preset to remove."); return; }
        _profiles?.RemovePreset(_presetNames[PresetPicker.SelectedIndex - 2]);
        RefreshPresets();
        _save?.Invoke();
    }

    private void ResetClick(object sender, RoutedEventArgs e)
    {
        RefreshPresets();
        SetSettings(EqualizerSettings.Flat with { Enabled = _settings.Enabled, MatchLevel = _settings.MatchLevel });
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Parametric EQ preset (*.txt)|*.txt", Title = "Import headphone or EQ preset" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            RefreshPresets();
            SetSettings(EqualizerApoPreset.Load(picker.FileName) with { MatchLevel = _settings.MatchLevel });
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        { _notice?.Invoke(error.Message); }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Filter = "Equalizer APO preset (*.txt)|*.txt", FileName = "XTONE-EQ.txt",
            Title = "Export preset for a configured Equalizer APO output"
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(picker.FileName, EqualizerApoPreset.Format(_settings, _sampleRate));
            _notice?.Invoke("Preset exported. Load it in Equalizer APO and switch XTONE EQ off to avoid applying it twice.");
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        { _notice?.Invoke(error.Message); }
    }

    private void StartTasteClick(object sender, RoutedEventArgs e)
    {
        if (_isPlaying?.Invoke() != true)
        {
            _notice?.Invoke("Play a familiar track before tuning by ear.");
            return;
        }
        _taste = new EqualizerTasteSession(_settings);
        StartTasteStep();
    }

    private void StartTasteStep()
    {
        _reverseTaste = Random.Shared.Next(2) == 0;
        _original = true;
        Apply();
        RefreshControls();
        CompareA.Focus();
    }

    private void TasteChoiceClick(object sender, RoutedEventArgs e)
    {
        _taste?.Choose(Equals(((Button)sender).Tag, "B") != _reverseTaste);
        FinishTasteStep();
    }

    private void TasteSkipClick(object sender, RoutedEventArgs e)
    {
        _taste?.Skip();
        FinishTasteStep();
    }

    private void FinishTasteStep()
    {
        if (_taste is null) return;
        if (!_taste.IsComplete) { StartTasteStep(); return; }
        var result = _taste.Result;
        _taste = null;
        RefreshPresets();
        SetSettings(result with { MatchLevel = _settings.MatchLevel });
        TasteButton.Focus();
        _notice?.Invoke("Your tuning has been applied to this output.");
    }

    private void TasteCancelClick(object sender, RoutedEventArgs e)
    {
        FinishAudition();
        TasteButton.Focus();
    }
}
