using XTONE.Services;

namespace XTONE.Dialogs;

public sealed class MusicFolderNameWindow : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _artist = new();
    public string FolderName => _name.Text.Trim();
    public string ArtistFolderName => _artist.Text.Trim();
    public MusicFolderNameWindow(bool artistLayout, string? initialName = null, bool rename = false)
    {
        Title = rename ? "Rename folder · XTONE" : "New folder · XTONE"; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "AppFont"); SetResourceReference(BackgroundProperty, "PaperBrush"); SetResourceReference(ForegroundProperty, "InkBrush");
        var panel = new StackPanel { Margin = new(28) };
        panel.Children.Add(new TextBlock { Text = rename ? "Rename folder" : "New folder", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new(0,0,0,22) });
        void AddInput(string label, TextBox input)
        {
            panel.Children.Add(new Label { Content = label, Target = input, Padding = new(0,0,0,8) });
            input.SetResourceReference(StyleProperty, "TextInput"); input.Height = 38; input.Margin = new(0,0,0,16);
            System.Windows.Automation.AutomationProperties.SetName(input, label); panel.Children.Add(input);
        }
        if (artistLayout) AddInput("Artist folder", _artist);
        AddInput("Folder name", _name);
        var hint = new TextBlock { Text = rename ? "Renames this folder and keeps its tracks and shortcuts together." : artistLayout ? "Created inside the artist folder in your Music folder." : "Created directly inside your Music folder.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0,0,0,24) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(hint);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new(0,0,8,0) }; cancel.SetResourceReference(StyleProperty, "QuietButton"); actions.Children.Add(cancel);
        var create = new Button { Content = rename ? "Rename" : "Create folder", IsDefault = true }; create.SetResourceReference(StyleProperty, "PrimaryButton");
        create.Click += (_,_) => { if (FolderName.Length > 0 && (!artistLayout || ArtistFolderName.Length > 0)) DialogResult = true; };
        create.IsEnabled = false;
        void Update() => create.IsEnabled = FolderName.Length > 0 && (!artistLayout || ArtistFolderName.Length > 0) && (!rename || FolderName != initialName);
        _name.TextChanged += (_,_) => Update(); _artist.TextChanged += (_,_) => Update(); _name.Text = initialName ?? ""; Update(); actions.Children.Add(create); panel.Children.Add(actions);
        Content = panel; Loaded += (_,_) => { if (artistLayout) _artist.Focus(); else { _name.Focus(); _name.SelectAll(); } };
    }
}

public sealed class MoveTracksWindow : Window
{
    private readonly ComboBox _folders = new() { DisplayMemberPath = "Name", SelectedValuePath = "Path", Height = 38 };
    public string? Destination => _folders.SelectedValue as string;
    public MoveTracksWindow(string root, IEnumerable<XTONE.Models.AlbumCard> folders, int count)
    {
        Title = "Move tracks · XTONE"; Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "AppFont"); SetResourceReference(BackgroundProperty, "PaperBrush"); SetResourceReference(ForegroundProperty, "InkBrush");
        var panel = new StackPanel { Margin = new(28) };
        panel.Children.Add(new TextBlock { Text = $"Move {count} {(count == 1 ? "track" : "tracks")}", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new(0,0,0,22) });
        panel.Children.Add(new Label { Content = "Destination folder", Target = _folders, Padding = new(0,0,0,8) });
        _folders.ItemsSource = new[] { new DestinationFolder("Music folder (loose tracks)", root) }.Concat(folders.Where(f => f.FolderPath is not null).Select(f => new DestinationFolder(System.IO.Path.GetRelativePath(root, f.FolderPath!), f.FolderPath!))).ToList();
        _folders.SelectedIndex = 0; System.Windows.Automation.AutomationProperties.SetName(_folders, "Destination folder"); panel.Children.Add(_folders);
        var hint = new TextBlock { Text = "Moves the files inside your Music folder. Favorites and queue entries follow them.", TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 19, Margin = new(0,12,0,24) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(hint);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new(0,0,8,0) }; cancel.SetResourceReference(StyleProperty, "QuietButton"); actions.Children.Add(cancel);
        var move = new Button { Content = "Move", IsDefault = true, Width = 88 }; move.SetResourceReference(StyleProperty, "PrimaryButton"); move.Click += (_,_) => { if (Destination is not null) DialogResult = true; }; actions.Children.Add(move);
        panel.Children.Add(actions); Content = panel;
    }
    private sealed record DestinationFolder(string Name, string Path);
}

public sealed class MusicOperationWindow : Window
{
    private bool _finished;
    private bool _cancelling;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new(0,0,0,18) };
    private readonly Button _cancel = new() { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0,20,0,0), Width = 88 };
    private readonly Action _requestCancel;
    public MusicOperationWindow(string message, Action requestCancel)
    {
        _requestCancel = requestCancel; Title = "Organizing music · XTONE"; Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(FontFamilyProperty, "AppFont"); SetResourceReference(BackgroundProperty, "PaperBrush"); SetResourceReference(ForegroundProperty, "InkBrush");
        var panel = new StackPanel { Margin = new(26) }; _message.Text = message; panel.Children.Add(_message);
        var progress = new ProgressBar { IsIndeterminate = true, Height = 3 }; progress.SetResourceReference(ForegroundProperty, "BlueBrush"); panel.Children.Add(progress);
        _cancel.SetResourceReference(StyleProperty, "QuietButton"); _cancel.Click += (_,_) => Cancel(); panel.Children.Add(_cancel); Content = panel;
    }
    public void Report(string message) { if (!_cancelling) _message.Text = message; }
    private void Cancel() { if (_cancelling) return; _cancelling = true; _cancel.IsEnabled = false; _message.Text = "Cancelling…"; _requestCancel(); }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (!_finished) { e.Cancel = true; Cancel(); } base.OnClosing(e); }
    public void Complete() { _finished = true; Close(); }
}
