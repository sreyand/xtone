using System.Windows.Input;
using XTONE.Services;

namespace XTONE.Dialogs;

public sealed class SettingsWindow : Window
{
    private readonly ComboBox _outputs = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id", Height = 38 };
    private readonly CheckBox _motion = new() { Content = "Reduce motion", Margin = new(0, 24, 0, 18) };
    public string? OutputDeviceId => _outputs.SelectedValue as string is { Length: > 0 } id ? id : null;
    public bool ReducedMotion => _motion.IsChecked == true;

    public SettingsWindow(IReadOnlyList<AudioOutputDevice> devices, string? selectedId, bool reducedMotion, string? mediaError)
    {
        Title = "Settings · XTONE"; Width = 480; Height = 430; MinWidth = 380; MinHeight = 340;
        MaxHeight = Math.Max(340, SystemParameters.WorkArea.Height - 40); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "AppFont"); SetResourceReference(BackgroundProperty, "PaperBrush"); SetResourceReference(ForegroundProperty, "InkBrush"); FontSize = 13;
        var layout = new Grid { Margin = new(28) };
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Settings", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 24) });
        panel.Children.Add(new Label { Content = "Audio output", Target = _outputs, Padding = new(0, 0, 0, 8) });
        _outputs.ItemsSource = new[] { new AudioOutputDevice("", "System default") }.Concat(devices).ToList();
        _outputs.SelectedValue = devices.Any(device => device.Id == selectedId) ? selectedId : "";
        System.Windows.Automation.AutomationProperties.SetName(_outputs, "Audio output device");
        panel.Children.Add(_outputs); _motion.IsChecked = reducedMotion; panel.Children.Add(_motion);
        var shortcuts = new Expander { Header = "Keyboard shortcuts", Margin = new(0, 0, 0, 14) };
        shortcuts.Content = new TextBlock { Text = "Ctrl+F   Search\nCtrl+O   Add music\nCtrl+Q   Queue\nCtrl+Shift+F   Focus mode\nCtrl+Shift+M   Mini-player\nF11   Fullscreen in Focus mode\nCtrl+D   Favorite current track\nCtrl+← / →   Previous / next\nSpace   Play / pause in track lists\nEnter   Play selected track\nF5   Rescan folders\nEscape   Close drawer or search", FontSize = 12, LineHeight = 23, Margin = new(0, 10, 0, 0) };
        panel.Children.Add(shortcuts);
        if (mediaError is not null)
        {
            var native = new TextBlock { Text = $"Windows media controls unavailable: {mediaError}", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 10, 0, 0) };
            native.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(native);
        }
        layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new(0, 0, 8, 0) }; cancel.SetResourceReference(StyleProperty, "QuietButton"); buttons.Children.Add(cancel);
        var save = new Button { Content = "Save", IsDefault = true, Width = 88 }; save.SetResourceReference(StyleProperty, "PrimaryButton"); save.Click += (_, _) => DialogResult = true; buttons.Children.Add(save);
        Grid.SetRow(buttons, 1); layout.Children.Add(buttons); Content = layout;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
    }
}
