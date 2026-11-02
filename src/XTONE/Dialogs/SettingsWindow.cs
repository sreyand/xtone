using System.Windows.Input;
using Microsoft.Win32;
using XTONE.Services;

namespace XTONE.Dialogs;

public sealed class SettingsWindow : Window
{
    private readonly ComboBox _outputs = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id", Height = 38 };
    private readonly ComboBox _layout = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id", Height = 38 };
    private readonly CheckBox _motion = new() { Content = "Reduce motion", Margin = new(0, 22, 0, 18) };
    private readonly TextBlock _folder = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    public string? OutputDeviceId => _outputs.SelectedValue as string is { Length: > 0 } id ? id : null;
    public bool ReducedMotion => _motion.IsChecked == true;
    public string? MusicRoot { get; private set; }
    public string AlbumLayout => _layout.SelectedValue as string ?? "AlbumFolders";

    public SettingsWindow(IReadOnlyList<AudioOutputDevice> devices, string? selectedId, bool reducedMotion, string? mediaError,
        string? musicRoot = null, string albumLayout = "AlbumFolders", bool chooseFolderOnOpen = false)
    {
        MusicRoot = musicRoot;
        Title = "Settings · XTONE"; Width = 520; Height = 600; MinWidth = 400; MinHeight = 380;
        MaxHeight = Math.Max(380, SystemParameters.WorkArea.Height - 40); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "AppFont"); SetResourceReference(BackgroundProperty, "PaperBrush"); SetResourceReference(ForegroundProperty, "InkBrush"); FontSize = 13;
        var layout = new Grid { Margin = new(28) };
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Settings", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 24) });
        var folderRow = new Grid(); folderRow.ColumnDefinitions.Add(new()); folderRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var browse = new Button { Content = "Choose folder", Margin = new(12, 0, 0, 0) }; browse.SetResourceReference(StyleProperty, "QuietButton");
        browse.Click += (_, _) => ChooseFolder(); Grid.SetColumn(browse, 1);
        panel.Children.Add(new Label { Content = "Music folder", Target = browse, Padding = new(0, 0, 0, 6) });
        folderRow.Children.Add(_folder); folderRow.Children.Add(browse); panel.Children.Add(folderRow); UpdateFolder();
        panel.Children.Add(new Label { Content = "Folder structure", Target = _layout, Padding = new(0, 16, 0, 8) });
        _layout.ItemsSource = new[] { new FolderLayout("AlbumFolders", "Folders directly inside Music"), new FolderLayout("ArtistAlbumFolders", "Music → Artist folder → Music folder") };
        _layout.SelectedValue = albumLayout;
        System.Windows.Automation.AutomationProperties.SetName(_layout, "Music folder structure"); panel.Children.Add(_layout);
        var explanation = new TextBlock { Text = "Folders shows directories containing music. Library includes all tracks. Files are never moved or changed.", TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 19, Margin = new(0, 10, 0, 24) };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(explanation);
        panel.Children.Add(new Label { Content = "Audio output", Target = _outputs, Padding = new(0, 0, 0, 8) });
        _outputs.ItemsSource = new[] { new AudioOutputDevice("", "System default") }.Concat(devices).ToList();
        _outputs.SelectedValue = devices.Any(device => device.Id == selectedId) ? selectedId : "";
        System.Windows.Automation.AutomationProperties.SetName(_outputs, "Audio output device"); panel.Children.Add(_outputs);
        _motion.IsChecked = reducedMotion; panel.Children.Add(_motion);
        var shortcuts = new Expander { Header = "Keyboard shortcuts", Margin = new(0, 0, 0, 14) };
        shortcuts.Content = new TextBlock { Text = "Ctrl+F   Search\nCtrl+O   Open music folder\nCtrl+Q   Queue\nCtrl+Shift+F   Focus mode\nCtrl+Shift+M   Mini-player\nF11   Fullscreen in Focus mode\nCtrl+D   Favorite current track\nCtrl+← / →   Previous / next\nSpace   Play / pause in track lists\nEnter   Play selected track\nF5   Rescan Music folder\nEscape   Close drawer or search", FontSize = 12, LineHeight = 23, Margin = new(0, 10, 0, 0) };
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
        if (chooseFolderOnOpen) ContentRendered += (_, _) => { Dispatcher.BeginInvoke(new Action(ChooseFolder)); };
    }
    private void ChooseFolder()
    {
        var picker = new OpenFolderDialog { Title = "Choose your Music folder", Multiselect = false };
        if (MusicRoot is not null) picker.InitialDirectory = MusicRoot;
        if (picker.ShowDialog(this) == true) { MusicRoot = picker.FolderName; UpdateFolder(); }
    }
    private void UpdateFolder() { _folder.Text = MusicRoot ?? "No folder selected"; _folder.ToolTip = MusicRoot; }
    private sealed record FolderLayout(string Id, string Name);
}
