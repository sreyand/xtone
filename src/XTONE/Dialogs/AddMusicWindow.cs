using System.Windows.Automation;

namespace XTONE.Dialogs;

/// <summary>One entry point for referencing existing music on this PC.</summary>
public sealed class AddMusicWindow : Window
{
    public bool ChooseFolder { get; private set; }
    public AddMusicWindow()
    {
        Title = "Add music · XTONE"; Width = 440; SizeToContent = SizeToContent.Height; MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 48); ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "AppFont"); SetResourceReference(BackgroundProperty, "PaperBrush");
        SetResourceReference(ForegroundProperty, "InkBrush"); FontSize = 13;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Add your music", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var hint = new TextBlock { Text = "Choose a folder to keep in sync, or select individual audio files. Your files stay where they are.", TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 12, 0, 22) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(hint);
        var folder = Choice("Choose folder", true); folder.IsDefault = true; panel.Children.Add(folder);
        panel.Children.Add(Choice("Choose files", false));
        var cancel = new Button { Content = "Cancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        cancel.SetResourceReference(StyleProperty, "QuietButton"); panel.Children.Add(cancel); Content = panel;
    }
    private Button Choice(string text, bool folder)
    {
        var button = new Button { Content = text, Height = 38, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        button.SetResourceReference(StyleProperty, "QuietButton"); button.SetResourceReference(BorderBrushProperty, "LineBrush");
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => { ChooseFolder = folder; DialogResult = true; }; return button;
    }
}
