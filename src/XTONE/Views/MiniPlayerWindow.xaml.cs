using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XTONE.Models;

namespace XTONE.Views;

/// <summary>A compact presentation of the existing player; playback remains owned by MainWindow.</summary>
public partial class MiniPlayerWindow : Window
{
    private readonly NowPlayingState _state;
    private readonly PlaybackActions _actions;
    private bool _isSeeking;
    private bool _mouseSeeking;
    private bool _closed;
    private double _lastAudibleVolume = .72;

    public MiniPlayerWindow(NowPlayingState state, PlaybackActions actions)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(actions);
        _state = state;
        _actions = actions;
        InitializeComponent();
        DataContext = state;
        _state.PropertyChanged += State_PropertyChanged;
        Closed += Window_Closed;
        SyncPosition();
    }

    /// <summary>Cancels an unfinished seek before the root hides or closes this view.</summary>
    public void PrepareToHide()
    {
        VerifyAccess();
        EndSeek(false);
        if (Mouse.Captured is DependencyObject captured && IsInsideSlider(captured))
            Mouse.Capture(null);
    }

    private bool CanSeek => !_closed && _state.HasTrack && _state.CanSeek && !_state.IsOpening && _state.DurationSeconds > 0;

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => State_PropertyChanged(sender, args)));
            return;
        }

        if (args.PropertyName is nameof(NowPlayingState.TrackIdentity) or nameof(NowPlayingState.Title) or nameof(NowPlayingState.Artist) or
            nameof(NowPlayingState.DurationSeconds) or nameof(NowPlayingState.HasTrack) or nameof(NowPlayingState.CanSeek))
        {
            PrepareToHide();
            SyncPosition();
        }
        else if (args.PropertyName == nameof(NowPlayingState.IsOpening))
        {
            if (_state.IsOpening) PrepareToHide();
            SyncPosition();
        }
        else if (args.PropertyName == nameof(NowPlayingState.PositionSeconds))
            SyncPosition();
        else if (args.PropertyName == nameof(NowPlayingState.Volume) && _state.Volume > 0)
            _lastAudibleVolume = _state.Volume;
    }

    private void SyncPosition()
    {
        if (_closed || _isSeeking || PositionSlider is null || ElapsedText is null) return;
        PositionSlider.Value = Math.Clamp(_state.PositionSeconds, 0, Math.Max(1, _state.DurationSeconds));
        ElapsedText.Text = _state.ElapsedText;
    }

    private void BeginSeek(bool mouse)
    {
        if (!CanSeek || _isSeeking) return;
        _isSeeking = true;
        _mouseSeeking = mouse;
        if (mouse)
        {
            // Thumb drags establish their own capture. Rail clicks need capture after Slider
            // finishes processing the press, so releasing outside the window still completes.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_isSeeking && _mouseSeeking && Mouse.LeftButton == MouseButtonState.Pressed && Mouse.Captured is null)
                    PositionSlider.CaptureMouse();
            }));
        }
    }

    private void EndSeek(bool commit)
    {
        if (!_isSeeking) return;
        var seconds = Math.Clamp(PositionSlider.Value, 0, Math.Max(0, _state.DurationSeconds));
        _isSeeking = false;
        _mouseSeeking = false;
        if (ReferenceEquals(Mouse.Captured, PositionSlider)) PositionSlider.ReleaseMouseCapture();
        if (commit && CanSeek)
        {
            _actions.Seek(seconds);
            // Keep the user's chosen position until the engine publishes its next position.
            PositionSlider.Value = seconds;
            SetElapsedText(seconds);
        }
        else SyncPosition();
    }

    private void Window_SeekMouseDown(object sender, MouseButtonEventArgs args)
    {
        // The window receives preview input before Slider's move-to-point class handler marks
        // it handled, so both rail clicks and thumb drags start the same guarded gesture.
        if (args.OriginalSource is DependencyObject target && IsInsideSlider(target)) BeginSeek(true);
    }

    private void Window_SeekMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (_mouseSeeking) EndSeek(true);
    }

    private void PositionSlider_LostMouseCapture(object sender, MouseEventArgs args)
    {
        if (_mouseSeeking) EndSeek(false);
    }

    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private void PositionSlider_KeyDown(object sender, KeyEventArgs args)
    {
        if (Keyboard.Modifiers == ModifierKeys.None && IsSeekKey(args.Key)) BeginSeek(false);
    }

    private void PositionSlider_KeyUp(object sender, KeyEventArgs args)
    {
        if (!_mouseSeeking && IsSeekKey(args.Key)) EndSeek(true);
    }

    private void PositionSlider_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args) => PrepareToHide();

    private void PositionSlider_MouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (CanSeek && !_mouseSeeking)
        {
            BeginSeek(false);
            PositionSlider.Value = Math.Clamp(PositionSlider.Value + Math.Sign(args.Delta) * PositionSlider.SmallChange, 0, _state.DurationSeconds);
            EndSeek(true);
        }
        args.Handled = true;
    }

    private void PositionSlider_AutomationValueCommitted(object? sender, SliderCommittedEventArgs args)
    {
        if (!CanSeek) return;
        PrepareToHide();
        var seconds = Math.Clamp(args.Value, 0, _state.DurationSeconds);
        _actions.Seek(seconds);
        PositionSlider.Value = seconds;
        SetElapsedText(seconds);
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!_isSeeking || ElapsedText is null) return;
        SetElapsedText(args.NewValue);
    }

    private void SetElapsedText(double value)
    {
        var seconds = TimeSpan.FromSeconds(Math.Clamp(value, 0, TimeSpan.MaxValue.TotalSeconds / 2));
        ElapsedText.Text = seconds.TotalHours >= 1 ? seconds.ToString(@"h\:mm\:ss") : seconds.ToString(@"m\:ss");
    }

    private bool IsInsideSlider(DependencyObject target)
    {
        DependencyObject? current = target;
        while (current is not null)
        {
            if (ReferenceEquals(current, PositionSlider)) return true;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs args)
    {
        if (_state.HasTrack || _state.IsOpening) { PrepareToHide(); _actions.PlayPause(); }
    }

    private void Previous_Click(object sender, RoutedEventArgs args)
    {
        if (_state.HasTrack && !_state.IsOpening) { PrepareToHide(); _actions.Previous(); }
    }

    private void Next_Click(object sender, RoutedEventArgs args)
    {
        if (_state.HasTrack && !_state.IsOpening) { PrepareToHide(); _actions.Next(); }
    }

    private void Library_Click(object sender, RoutedEventArgs args) { PrepareToHide(); _actions.ShowLibrary(); }
    private void Focus_Click(object sender, RoutedEventArgs args) { PrepareToHide(); _actions.ShowFocus(); }
    private void Close_Click(object sender, RoutedEventArgs args) => Close();
    private void Queue_Click(object sender, RoutedEventArgs args) { PrepareToHide(); _actions.ShowQueue(); }
    private void VolumeUp_Click(object sender, RoutedEventArgs args) => _actions.SetVolume(Math.Clamp(_state.Volume + .05, 0, 1));
    private void VolumeDown_Click(object sender, RoutedEventArgs args) => _actions.SetVolume(Math.Clamp(_state.Volume - .05, 0, 1));
    private void Mute_Click(object sender, RoutedEventArgs args)
    {
        if (_state.Volume > 0) { _lastAudibleVolume = _state.Volume; _actions.SetVolume(0); }
        else _actions.SetVolume(_lastAudibleVolume);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { Library_Click(sender, args); args.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && args.Key is Key.Left or Key.Right)
        {
            if (args.Key == Key.Left) Previous_Click(sender, args); else Next_Click(sender, args);
            args.Handled = true;
        }
        else if (args.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not ButtonBase)
        {
            if (!args.IsRepeat) PlayPause_Click(sender, args);
            args.Handled = true;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs args) => PrepareToHide();
    private void Window_Loaded(object sender, RoutedEventArgs args)
    {
        var area = SystemParameters.WorkArea;
        // Leave an explicitly positioned secondary-monitor window on its chosen display.
        if (!new Rect(Left, Top, Width, Height).IntersectsWith(area)) return;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private void Window_Closing(object? sender, CancelEventArgs args) => PrepareToHide();
    private void Window_Closed(object? sender, EventArgs args)
    {
        _closed = true;
        _state.PropertyChanged -= State_PropertyChanged;
        Closed -= Window_Closed;
    }
}
