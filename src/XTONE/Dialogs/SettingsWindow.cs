using System.Windows.Input;
using XTONE.Services;

namespace XTONE.Dialogs;

public sealed class SettingsWindow : Window
{
    private readonly ComboBox _outputs = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id", Height = 38 };
    private readonly CheckBox _motion = new() { Content = "Reduce motion and turn off the visualizer", Margin = new(0, 22, 0, 18) };
    public string? OutputDeviceId => _outputs.SelectedValue as string is { Length: > 0 } id ? id : null;
    public bool ReducedMotion => _motion.IsChecked == true;

    public SettingsWindow(IReadOnlyList<AudioOutputDevice> devices, string? selectedId, bool reducedMotion, string? mediaError)
    {
        Title = "XTONE settings"; Width = 540; Height = 590; MinWidth = 450; MinHeight = 400;
        MaxHeight = SystemParameters.WorkArea.Height - 40; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "AppFont"); SetResourceReference(BackgroundProperty, "PaperBrush"); SetResourceReference(ForegroundProperty, "InkBrush");
        var panel = new StackPanel { Margin = new(30) };
        panel.Children.Add(new TextBlock { Text = "Settings", FontSize = 30, FontWeight = FontWeights.Bold, Margin = new(0, 0, 0, 24) });
        panel.Children.Add(new Label { Content = "Audio output", Target = _outputs, Padding = new(0, 0, 0, 8) });
        _outputs.ItemsSource = new[] { new AudioOutputDevice("", "System default") }.Concat(devices).ToList();
        _outputs.SelectedValue = devices.Any(device => device.Id == selectedId) ? selectedId : "";
        System.Windows.Automation.AutomationProperties.SetName(_outputs, "Audio output device");
        panel.Children.Add(_outputs);
        var hint = new TextBlock { Text = "Playback uses the selected device’s Windows shared audio format. Original file details are shown in the player.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 10, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(hint);
        _motion.IsChecked = reducedMotion; panel.Children.Add(_motion);
        panel.Children.Add(new TextBlock { Text = "Keyboard shortcuts", FontWeight = FontWeights.Bold, FontSize = 18, Margin = new(0, 10, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "Ctrl+F   Search\nCtrl+O   Add files\nCtrl+Q   Queue\nCtrl+Shift+F   Focus mode\nCtrl+Shift+M   Mini-player\nF11   Fullscreen in Focus mode\nCtrl+D   Favorite current track\nCtrl+← / →   Previous / next\nSpace   Play / pause in track lists\nEnter   Play selected track\nF5   Rescan folders\nEscape   Close drawer or search", FontSize = 13, LineHeight = 23 });
        var native = new TextBlock { Text = mediaError is null ? "Windows media keys and now playing are enabled." : $"Windows media controls unavailable: {mediaError}", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 20, 0, 0) };
        native.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(native);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 26, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new(0, 0, 8, 0) }; cancel.SetResourceReference(StyleProperty, "QuietButton"); buttons.Children.Add(cancel);
        var save = new Button { Content = "Save", IsDefault = true, Width = 100 }; save.SetResourceReference(StyleProperty, "PrimaryButton"); save.Click += (_, _) => DialogResult = true; buttons.Children.Add(save);
        panel.Children.Add(buttons); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
    }
}
