using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using XTONE.Models;

namespace XTONE.Dialogs;

/// <summary>Read-only facts about a local file; opening this window never changes its tags.</summary>
public sealed class FileDetailsWindow : Window
{
    private readonly string _path;
    private readonly TextBlock _status;

    public FileDetailsWindow(Track track)
    {
        _path = track.Path;
        Title = "File details — XTONE";
        Width = 680;
        Height = 760;
        MinWidth = 340;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        FontFamily = TryFindResource("AppFont") as FontFamily ?? new FontFamily("Segoe UI");
        FontSize = 14;
        SetResourceReference(BackgroundProperty, "PaperBrush");
        SetResourceReference(ForegroundProperty, "InkBrush");
        AutomationProperties.SetName(this, "Audio file details");

        var layout = new Grid { Margin = new Thickness(26) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        KeyboardNavigation.SetTabNavigation(layout, KeyboardNavigationMode.Cycle);
        Content = layout;

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        heading.Children.Add(new TextBlock { Text = "File details", FontSize = 26, FontWeight = FontWeights.Bold });
        var subtitle = new TextBlock
        {
            Text = track.Title + " · " + track.Artist,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        heading.Children.Add(subtitle);
        layout.Children.Add(heading);

        var body = new StackPanel();
        var details = new Grid();
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(details);
        var available = File.Exists(_path);
        AddDetail("Format", string.IsNullOrWhiteSpace(track.Format) ? "Not reported" : track.Format);
        AddDetail("Codec", string.IsNullOrWhiteSpace(track.Codec) ? "Not reported" : track.Codec);
        AddDetail("Encoding", track.QualityLabel);
        AddDetail("Bitrate", track.BitRateKbps > 0 ? $"{track.BitRateKbps:N0} kb/s" : "Not reported");
        AddDetail("Sample rate", track.SampleRate > 0 ? $"{track.SampleRate:N0} Hz" : "Not reported");
        AddDetail("Bit depth", track.BitDepth > 0 ? $"{track.BitDepth}-bit" : track.IsLossless == false ? "Not applicable to this lossy codec" : "Not reported");
        AddDetail("Channels", track.Channels switch { 1 => "1 · Mono", 2 => "2 · Stereo", > 2 => $"{track.Channels} channels", _ => "Not reported" });
        AddDetail("Duration", track.Duration > TimeSpan.Zero ? $"{track.DurationText} ({track.Duration.TotalSeconds:0.###} seconds)" : "Not reported");
        AddDetail("File size", track.FileSize > 0 ? FormatSize(track.FileSize) + $" ({track.FileSize:N0} bytes)" : "Not reported");
        AddDetail("Availability", !available || track.IsMissing ? "Missing file — locate it to play again" : !string.IsNullOrWhiteSpace(track.Error) ? "Unreadable file" : "Available locally");
        if (!string.IsNullOrWhiteSpace(track.Error))
            AddDetail("Read error", track.Error);
        AddDetail("Path", _path);

        var explanation = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 18, 0, 8),
            Child = new TextBlock
            {
                Text = "Lossless describes the file’s encoding. It cannot establish whether the recording was transcoded, upsampled, or sourced from a lossless master.\n\nDetails come from the file’s audio headers and metadata. Bitrate is the reported or average bitrate; cached details remain available when a file is missing.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 19
            }
        };
        explanation.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        body.Children.Add(explanation);
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 0)
        };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        footer.Children.Add(_status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var copy = MakeButton("Copy path", "Copy the audio file path to the clipboard");
        copy.IsEnabled = !string.IsNullOrWhiteSpace(_path);
        copy.Click += (_, _) => CopyPath();
        var show = MakeButton("Show in folder", "Select the audio file in File Explorer");
        show.IsEnabled = available;
        if (!available)
            show.ToolTip = "The file is missing. Its saved path can still be copied.";
        show.Click += (_, _) => ShowInFolder();
        var close = MakeButton("Close", "Close file details");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(show);
        buttons.Children.Add(close);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape)
                return;
            args.Handled = true;
            Close();
        };
        Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            MaxWidth = workArea.Width;
            MaxHeight = workArea.Height;
            MinWidth = Math.Min(MinWidth, workArea.Width);
            MinHeight = Math.Min(MinHeight, workArea.Height);
            Width = Math.Min(Width, workArea.Width);
            Height = Math.Min(Height, workArea.Height);
            Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - ActualWidth));
            Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
            copy.Focus();
        };

        void AddDetail(string label, string value)
        {
            var row = details.RowDefinitions.Count;
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Margin = new Thickness(0, 7, 14, 7), TextWrapping = TextWrapping.Wrap };
            name.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var text = new TextBox
            {
                Text = value,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(3, 6, 3, 6),
                Margin = new Thickness(0, 0, 0, 2),
                VerticalContentAlignment = VerticalAlignment.Top,
                FontSize = 14
            };
            text.SetResourceReference(TextBox.ForegroundProperty, "InkBrush");
            text.SetResourceReference(TextBox.SelectionBrushProperty, "SelectionBrush");
            text.SetResourceReference(TextBox.SelectionTextBrushProperty, "SelectedTextBrush");
            AutomationProperties.SetName(text, label);
            AutomationProperties.SetHelpText(text, "Read-only. Select text and press Control C to copy.");
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            details.Children.Add(name);
            details.Children.Add(text);
        }
    }

    private Button MakeButton(string text, string accessibleName)
    {
        var button = new Button { Content = text, Margin = new Thickness(6, 3, 0, 3), MinHeight = 42, Padding = new Thickness(13, 8, 13, 8), FontSize = 13 };
        if (TryFindResource("QuietButton") is Style style)
            button.Style = style;
        button.SetResourceReference(Button.ForegroundProperty, "InkBrush");
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    private void CopyPath()
    {
        try
        {
            Clipboard.SetText(_path);
            _status.Text = "Path copied.";
        }
        catch (Exception error) when (error is ExternalException or System.Threading.ThreadStateException)
        {
            _status.Text = "The clipboard is busy. Select the path above and press Control C.";
        }
    }

    private void ShowInFolder()
    {
        if (!File.Exists(_path))
        {
            _status.Text = "This file is no longer at its saved path. You can still copy the path above.";
            return;
        }
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(Path.GetFullPath(_path));
            using var process = Process.Start(start);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            _status.Text = "File Explorer could not be opened. Copy the path above to locate this file.";
        }
    }

    private static string FormatSize(long bytes) => bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824d:0.##} GiB" :
        bytes >= 1_048_576 ? $"{bytes / 1_048_576d:0.##} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.##} KiB" : $"{bytes} bytes";
}
