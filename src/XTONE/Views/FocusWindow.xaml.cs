using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using XTONE.Controls;
using XTONE.Models;
using XTONE.Services;

namespace XTONE.Views;

/// <summary>A presentation of the shared player; it owns no playback engine or animation timer.</summary>
public partial class FocusWindow : Window
{
    private readonly NowPlayingState _state;
    private readonly PlaybackActions _actions;
    private bool _closed;
    private bool _seeking;
    private bool _volumeGesture;
    private bool _settingVolume;
    private double _lastObservedSeekPosition;
    private bool _fullScreen;
    private Rect _previousBounds;
    private WindowState _previousState;
    private ResizeMode _previousResizeMode;
    private WindowChrome? _previousChrome;
    private bool _fittingContent;
    private bool _fitViewportHooked;
    private int _fullScreenGeneration;
    private bool? _fullScreenIntent;

    public FocusWindow(NowPlayingState state, PlaybackActions actions)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(actions);
        _state = state; _actions = actions;
        InitializeComponent();
        DataContext = state;
        _state.PropertyChanged += State_PropertyChanged;
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Seek_DragCompleted), true);
        ApplyVisualization();
    }

    public void SetSpectrum(float[] values, bool active)
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess())
        {
            var copy = values is null ? [] : (float[])values.Clone();
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => SetSpectrum(copy, active)));
            return;
        }
        if (!IsVisible) return;
        ApplyVisualization();
        var show = _state.Visualization != "Off" && _state.MotionEnabled;
        Visualizer.SetSpectrum(show ? values : [], show && active);
    }

    private void ApplyVisualization()
    {
        Visualizer.Visualization = _state.Visualization == "Bars" ? SignalVisualization.Bars : SignalVisualization.Ribbon;
        Visualizer.Visibility = _state.Visualization == "Off" ? Visibility.Hidden : Visibility.Visible;
        if (!_state.MotionEnabled || _state.Visualization == "Off") Visualizer.SetSpectrum([], false);
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => State_PropertyChanged(sender, e)));
            return;
        }
        if (e.PropertyName is nameof(NowPlayingState.MotionEnabled) or nameof(NowPlayingState.Visualization))
            ApplyVisualization();
        if (!_seeking) return;
        if (e.PropertyName is nameof(NowPlayingState.TrackIdentity) or nameof(NowPlayingState.Title) or nameof(NowPlayingState.Artist)
            or nameof(NowPlayingState.Album) or nameof(NowPlayingState.DurationSeconds)
            or nameof(NowPlayingState.HasTrack) or nameof(NowPlayingState.IsOpening) or nameof(NowPlayingState.CanSeek))
            CancelSeek();
        else if (e.PropertyName == nameof(NowPlayingState.PositionSeconds))
        {
            // A repeated copy of the same file may retain identical metadata.
            // Its rendered position still resets at the track boundary.
            if (_state.PositionSeconds + .2 < _lastObservedSeekPosition) CancelSeek();
            _lastObservedSeekPosition = _state.PositionSeconds;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(640, workArea.Width); MinHeight = Math.Min(600, workArea.Height);
        Width = Math.Min(Width, workArea.Width); Height = Math.Min(Height, workArea.Height);
        FitContent();
    }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => FitContent();
    private void FitContent()
    {
        if (ArtworkHost is null || TrackTitle is null || Visualizer is null || PlaybackControls is null
            || FooterBar is null || HeroScroll is null || ActualWidth <= 0 || ActualHeight <= 0 || _fittingContent) return;
        if (!_fitViewportHooked)
        {
            HeroScroll.SizeChanged += FitViewport_SizeChanged;
            _fitViewportHooked = true;
        }
        _fittingContent = true;
        try
        {
            var space = Math.Clamp((ActualHeight - 600) / 320, 0, 1);
            double SizeBetween(double small, double large) => small + (large - small) * space;
            const int duration = 0; // Geometry follows native resize without queued layout clocks.
            void Number(DependencyObject target, DependencyProperty property, double value) => UiMotion.AnimateDouble(target, property, value, duration);
            void Space(FrameworkElement target, DependencyProperty property, Thickness value) => UiMotion.AnimateThickness(target, property, value, duration);
            UiMotion.AnimateGridLength(HeaderRow, RowDefinition.HeightProperty, new GridLength(52), duration);
            var contentWidth = Math.Clamp(ActualWidth - 58, 1, 660);
            HeroContent.Width = contentWidth;
            TrackTitle.MaxWidth = TrackArtist.MaxWidth = TrackAlbum.MaxWidth = contentWidth;
            PlaybackControls.Width = Math.Clamp(contentWidth, 280, 660);
            Space(PlaybackControls, MarginProperty, new Thickness(28, 0, 28, SizeBetween(10, 18)));
            Number(PlayPauseButton, WidthProperty, 52);
            Number(PlayPauseButton, HeightProperty, 52);
            Space(PlaybackCaption, MarginProperty, new Thickness(0, 0, 0, SizeBetween(10, 18)));
            Space(MetadataPanel, MarginProperty, new Thickness(0, SizeBetween(12, 20), 0, 0));
            Number(TrackTitle, TextBlock.FontSizeProperty, SizeBetween(24, 28));
            Number(TrackArtist, TextBlock.FontSizeProperty, 14);
            Number(TrackAlbum, TextBlock.FontSizeProperty, 12);
            Number(Visualizer, HeightProperty, SizeBetween(24, 40));
            Space(Visualizer, MarginProperty, new Thickness(0, 8, 0, 0));
            Space(HeroScroll, Control.PaddingProperty, new Thickness(28, 8, 28, SizeBetween(8, 18)));
            Space(SeekPanel, MarginProperty, new Thickness(0, 12, 0, 0));

            // Fit art against measured controls and footer. Constraining hero width
            // above also makes long metadata ellipsize inside the viewport.
            var measure = new Size(contentWidth, double.PositiveInfinity);
            PlaybackControls.Measure(new Size(Math.Max(1, ActualWidth), double.PositiveInfinity));
            FooterBar.Measure(new Size(Math.Max(1, ActualWidth), double.PositiveInfinity));
            PlaybackCaption.Measure(measure); MetadataPanel.Measure(measure); Visualizer.Measure(measure);
            var nonArtHeight = PlaybackCaption.DesiredSize.Height + MetadataPanel.DesiredSize.Height + Visualizer.DesiredSize.Height;
            var available = ActualHeight - HeaderRow.Height.Value - PlaybackControls.DesiredSize.Height
                - FooterBar.DesiredSize.Height - HeroScroll.Padding.Top - HeroScroll.Padding.Bottom - nonArtHeight - 2;
            var artworkSize = Math.Clamp(Math.Min(contentWidth, available), 112, 360);
            if (Math.Abs(ArtworkHost.Width - artworkSize) > .1 || double.IsNaN(ArtworkHost.Width)) ArtworkHost.Width = artworkSize;
            if (Math.Abs(ArtworkHost.Height - artworkSize) > .1 || double.IsNaN(ArtworkHost.Height)) ArtworkHost.Height = artworkSize;
        }
        finally { _fittingContent = false; }
    }

    private void FitViewport_SizeChanged(object sender, SizeChangedEventArgs e) => FitContent();

    private void BeginSeek()
    {
        if (_closed || _seeking || !_state.CanSeek || !_state.HasTrack || _state.IsOpening) return;
        var position = _state.PositionSeconds;
        BindingOperations.ClearBinding(SeekSlider, Slider.ValueProperty);
        SeekSlider.Value = Math.Clamp(position, 0, SeekSlider.Maximum);
        _lastObservedSeekPosition = position;
        _seeking = true;
    }
    private void RestoreSeekBinding() => BindingOperations.SetBinding(SeekSlider, Slider.ValueProperty,
        new Binding(nameof(NowPlayingState.PositionSeconds)) { Source = _state, Mode = BindingMode.OneWay });
    private void CancelSeek()
    {
        if (!_seeking) return;
        _seeking = false; RestoreSeekBinding();
    }
    private void CommitSeek()
    {
        if (!_seeking) return;
        var position = Math.Clamp(SeekSlider.Value, 0, Math.Max(0, _state.DurationSeconds));
        _seeking = false; RestoreSeekBinding();
        if (!_closed && _state.CanSeek && _state.HasTrack && !_state.IsOpening) _actions.Seek(position);
    }
    private static bool IsSliderKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    private void Seek_MouseDown(object sender, MouseButtonEventArgs e) => BeginSeek();
    private void Seek_MouseUp(object sender, MouseButtonEventArgs e) => CommitSeek();
    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    { if (e.Canceled) CancelSeek(); else CommitSeek(); }
    private void Seek_KeyDown(object sender, KeyEventArgs e) { if (IsSliderKey(e.Key)) BeginSeek(); }
    private void Seek_KeyUp(object sender, KeyEventArgs e) { if (IsSliderKey(e.Key)) CommitSeek(); }
    private void Seek_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CancelSeek();
    private void Seek_AutomationValueCommitted(object? sender, SliderCommittedEventArgs e)
    {
        CancelSeek();
        if (!_closed && _state.HasTrack && _state.CanSeek && !_state.IsOpening)
            _actions.Seek(Math.Clamp(e.Value, 0, Math.Max(0, _state.DurationSeconds)));
    }

    private void Volume_MouseDown(object sender, MouseButtonEventArgs e) => _volumeGesture = true;
    private void Volume_MouseUp(object sender, MouseButtonEventArgs e) { SetUserVolume(); _volumeGesture = false; }
    private void Volume_KeyDown(object sender, KeyEventArgs e) { if (IsSliderKey(e.Key)) _volumeGesture = true; }
    private void Volume_KeyUp(object sender, KeyEventArgs e) { if (IsSliderKey(e.Key)) { SetUserVolume(); _volumeGesture = false; } }
    private void Volume_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _volumeGesture = false;
    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SetUserVolume();
    private void Volume_AutomationValueCommitted(object? sender, SliderCommittedEventArgs e)
    { if (!_closed) _actions.SetVolume(Math.Clamp(e.Value, 0, 1)); }
    private void SetUserVolume()
    {
        if (_closed || !_volumeGesture || _settingVolume || VolumeSlider is null) return;
        _settingVolume = true;
        try { _actions.SetVolume(Math.Clamp(VolumeSlider.Value, 0, 1)); }
        finally { _settingVolume = false; }
    }

    private void Library_Click(object sender, RoutedEventArgs e) { CancelSeek(); _actions.ShowLibrary(); }
    private void Mini_Click(object sender, RoutedEventArgs e) { CancelSeek(); _actions.ShowMini(); }
    private void Queue_Click(object sender, RoutedEventArgs e) { CancelSeek(); _actions.ShowQueue(); }
    private void Favorite_Click(object sender, RoutedEventArgs e) => _actions.ToggleFavorite();
    private void PlayPause_Click(object sender, RoutedEventArgs e) { CancelSeek(); _actions.PlayPause(); }
    private void Previous_Click(object sender, RoutedEventArgs e) { CancelSeek(); _actions.Previous(); }
    private void Next_Click(object sender, RoutedEventArgs e) { CancelSeek(); _actions.Next(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen() => RequestFullScreen(!(_fullScreenIntent ?? _fullScreen));

    private async void RequestFullScreen(bool fullScreen)
    {
        CancelSeek();
        var version = ++_fullScreenGeneration;
        _fullScreenIntent = fullScreen;
        var animate = UiMotion.Enabled && IsLoaded && IsVisible && ShowActivated && !_closed;
        if (!animate)
        {
            ApplyFullScreen(fullScreen);
            _fullScreenIntent = null;
            return;
        }
        try
        {
            if (!await UiMotion.FadeOutAsync(this, 100) || version != _fullScreenGeneration || _closed || !IsVisible) return;
            ApplyFullScreen(fullScreen);
            if (version != _fullScreenGeneration || _closed || !IsVisible) return;
            await UiMotion.FadeInAsync(this, 180);
        }
        finally
        {
            if (version == _fullScreenGeneration)
            {
                _fullScreenIntent = null;
            }
        }
    }

    /// <summary>Stops an in-flight fullscreen request before the root switches or closes views.</summary>
    public void PrepareToHide()
    {
        VerifyAccess();
        _fullScreenGeneration++;
        _fullScreenIntent = null;
        CancelSeek();
    }

    public void ExitFullScreen()
    {
        PrepareToHide();
        if (!_closed) ApplyFullScreen(false);
    }

    private void ApplyFullScreen(bool fullScreen)
    {
        if (_fullScreen == fullScreen || _closed) return;
        if (fullScreen)
        {
            _previousState = WindowState;
            _previousBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            _previousResizeMode = ResizeMode; _previousChrome = WindowChrome.GetWindowChrome(this);
            WindowState = WindowState.Normal;
            WindowChrome.SetWindowChrome(this, null);
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _fullScreen = true; WindowFrame.CornerRadius = new CornerRadius(0);
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowChrome.SetWindowChrome(this, _previousChrome);
            ResizeMode = _previousResizeMode;
            if (!_previousBounds.IsEmpty && double.IsFinite(_previousBounds.Left) && double.IsFinite(_previousBounds.Top))
            { Left = _previousBounds.Left; Top = _previousBounds.Top; Width = _previousBounds.Width; Height = _previousBounds.Height; }
            WindowState = _previousState;
            _fullScreen = false; WindowFrame.CornerRadius = new CornerRadius(7);
        }
        UpdateFullScreenLabel();
    }
    private void UpdateFullScreenLabel()
    {
        FullScreenButton.ToolTip = _fullScreen ? "Exit full screen (F11)" : "Full screen (F11)";
        AutomationProperties.SetName(FullScreenButton, _fullScreen ? "Exit full screen" : "Enter full screen");
        FullScreenGlyph.Data = Geometry.Parse(_fullScreen
            ? "M1,7 H7 V1 M15,1 V7 H21 M21,15 H15 V21 M7,21 V15 H1"
            : "M1,7 V1 H7 M15,1 H21 V7 M21,15 V21 H15 M7,21 H1 V15");
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { if (!e.IsRepeat) ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && !VisualizationPicker.IsDropDownOpen)
        { CancelSeek(); if (_fullScreen || _fullScreenIntent == true) RequestFullScreen(false); else _actions.ShowLibrary(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.L: CancelSeek(); _actions.ShowLibrary(); break;
                case Key.M: CancelSeek(); _actions.ShowMini(); break;
                case Key.Q: CancelSeek(); _actions.ShowQueue(); break;
                case Key.D: if (_state.HasTrack) _actions.ToggleFavorite(); break;
                case Key.Left: CancelSeek(); _actions.Previous(); break;
                case Key.Right: CancelSeek(); _actions.Next(); break;
                default: return;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None
                 && Keyboard.FocusedElement is not Button and not Slider and not ComboBox and not TextBox)
        { CancelSeek(); _actions.PlayPause(); e.Handled = true; }
    }
    private void Window_Deactivated(object? sender, EventArgs e) { CancelSeek(); _volumeGesture = false; }
    protected override void OnClosing(CancelEventArgs e)
    { PrepareToHide(); base.OnClosing(e); if (!e.Cancel) ExitFullScreen(); }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true; _state.PropertyChanged -= State_PropertyChanged;
        if (_fitViewportHooked) HeroScroll.SizeChanged -= FitViewport_SizeChanged;
        base.OnClosed(e);
    }
}
