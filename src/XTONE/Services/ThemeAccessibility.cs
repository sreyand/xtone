using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace XTONE.Services;

/// <summary>Applies Windows contrast colors and exposes the user's animation preference.</summary>
public sealed class ThemeAccessibility : IDisposable
{
    private readonly Application _application;
    private readonly Dictionary<string, Brush> _normalBrushes = new();
    private readonly Dictionary<string, object?> _originalLocalResources = new();
    private readonly HashSet<string> _originalLocalKeys = new();
    private bool _disposed;
    private bool _lastHighContrast;
    private bool _lastAnimationsEnabled;

    // DynamicResource references receive live updates when Windows changes its theme.
    private static readonly Dictionary<string, Color> DefaultColors = new()
    {
        ["InkBrush"] = Color.FromRgb(16, 16, 16),
        ["MutedBrush"] = Color.FromRgb(98, 102, 109),
        ["LineBrush"] = Color.FromRgb(230, 231, 235),
        ["PaperBrush"] = Colors.White,
        ["BlueBrush"] = Color.FromRgb(37, 91, 255),
        ["HoverBrush"] = Color.FromRgb(243, 245, 249),
        ["SelectionBrush"] = Color.FromRgb(239, 243, 255),
        ["RowSelectionBrush"] = Color.FromRgb(239, 243, 255),
        ["SelectedTextBrush"] = Color.FromRgb(37, 91, 255),
        ["AccentTextBrush"] = Colors.White,
        ["FocusBrush"] = Color.FromRgb(37, 91, 255),
        ["SurfaceBrush"] = Color.FromRgb(241, 243, 246),
        ["ErrorBrush"] = Color.FromRgb(174, 52, 52)
    };

    public ThemeAccessibility(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Dispatcher.VerifyAccess();
        _application = application;
        foreach (var (key, fallback) in DefaultColors)
        {
            RememberLocalResource(key);
            _normalBrushes[key] = application.TryFindResource(key) as Brush ?? MakeBrush(fallback);
        }
        RememberLocalResource("KeyboardFocusStyle");
        application.Resources["KeyboardFocusStyle"] = CreateFocusStyle();
        _lastHighContrast = HighContrast;
        _lastAnimationsEnabled = AnimationsEnabled;
        Apply();
        SystemParameters.StaticPropertyChanged += OnSystemPreferenceChanged;
    }

    public static bool HighContrast => SystemParameters.HighContrast;

    public static bool AnimationsEnabled => !HighContrast && SystemParameters.ClientAreaAnimation;

    public event EventHandler? Changed;

    /// <summary>Refreshes theme resources. Calls from background events are dispatched safely.</summary>
    public void Apply()
    {
        if (_disposed || _application.Dispatcher.HasShutdownStarted)
            return;
        if (!_application.Dispatcher.CheckAccess())
        {
            _application.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Apply));
            return;
        }

        var highContrast = HighContrast;
        foreach (var (key, normal) in _normalBrushes)
        {
            _application.Resources[key] = highContrast ? MakeBrush(key switch
            {
                "InkBrush" or "LineBrush" or "ErrorBrush" => SystemColors.WindowTextColor,
                "MutedBrush" => SystemColors.GrayTextColor,
                "PaperBrush" or "HoverBrush" or "SurfaceBrush" or "RowSelectionBrush" => SystemColors.WindowColor,
                "AccentTextBrush" or "SelectedTextBrush" => SystemColors.HighlightTextColor,
                _ => SystemColors.HighlightColor
            }) : normal;
        }

        var animations = AnimationsEnabled;
        if (_lastHighContrast != highContrast || _lastAnimationsEnabled != animations)
        {
            _lastHighContrast = highContrast;
            _lastAnimationsEnabled = animations;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSystemPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) ||
            args.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
            Apply();
    }

    private void RememberLocalResource(string key)
    {
        // ResourceDictionary.Contains also searches merged dictionaries; Keys describes local entries.
        if (_application.Resources.Keys.Cast<object>().Contains(key))
        {
            _originalLocalKeys.Add(key);
            _originalLocalResources[key] = _application.Resources[key];
        }
    }

    private static Brush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Style CreateFocusStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetResourceReference(Border.BorderBrushProperty, "FocusBrush");
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        border.SetValue(UIElement.IsHitTestVisibleProperty, false);
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.TemplateProperty,
            new ControlTemplate(typeof(Control)) { VisualTree = border }));
        style.Seal();
        return style;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _application.Dispatcher.VerifyAccess();
        _disposed = true;
        SystemParameters.StaticPropertyChanged -= OnSystemPreferenceChanged;
        foreach (var key in DefaultColors.Keys.Append("KeyboardFocusStyle"))
        {
            if (_originalLocalKeys.Contains(key))
                _application.Resources[key] = _originalLocalResources[key];
            else
                _application.Resources.Remove(key);
        }
    }
}
