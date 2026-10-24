using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace XTONE.Services;

/// <summary>Small, reversible UI transitions. All entry points belong on the owning dispatcher.</summary>
public static class UiMotion
{
    private static readonly ConditionalWeakTable<DependencyObject, MotionState> States = new();
    private static bool _enabled = true;
    private static long _generation;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            Application.Current?.Dispatcher.VerifyAccess();
            if (_enabled == value) return;
            _enabled = value;
            if (value) return;
            foreach (var (target, state) in States)
            {
                target.VerifyAccess();
                foreach (var motion in state.Properties.Values.ToArray()) Finish(target, state, motion, true);
                if (state.Prepared && target is Window window && window.ShowActivated)
                    window.SetCurrentValue(UIElement.OpacityProperty, state.RestingOpacity ?? 1d);
                state.Prepared = false;
                if (state.HoverEnabled && target is ButtonBase button && state.HoverOpacity is { } opacity)
                    button.SetCurrentValue(UIElement.OpacityProperty, opacity);
            }
        }
    }

    /// <summary>Call before Show. Intentional transparent/inactive test windows remain untouched.</summary>
    public static void PrepareFadeIn(Window window)
    {
        ArgumentNullException.ThrowIfNull(window); window.VerifyAccess();
        var state = StateFor(window);
        if (!WindowMayFade(window, state)) { PreserveWindowBase(window, state); return; }
        RememberOpacity(window, state);
        if (!Enabled)
        {
            SetImmediately(window, UIElement.OpacityProperty, state.RestingOpacity!.Value);
            state.Prepared = false; return;
        }
        // A reversing, still-visible fade keeps its current rendered opacity.
        if (state.Properties.ContainsKey(UIElement.OpacityProperty) || window.IsVisible) return;
        window.SetCurrentValue(UIElement.OpacityProperty, 0d);
        state.Prepared = true;
    }

    /// <returns>False when a newer transition superseded this request; never leaves an older await hanging.</returns>
    public static Task<bool> FadeInAsync(Window window, int milliseconds = 180)
    {
        ArgumentNullException.ThrowIfNull(window); window.VerifyAccess();
        var state = StateFor(window);
        if (!WindowMayFade(window, state)) { PreserveWindowBase(window, state); return Task.FromResult(true); }
        var alreadyOwned = state.OwnsOpacity;
        RememberOpacity(window, state);
        var target = state.RestingOpacity!.Value;
        var from = window.Opacity;
        if (!state.Prepared && !state.Properties.ContainsKey(UIElement.OpacityProperty) && !alreadyOwned) from = 0;
        state.Prepared = false;
        return DoubleMotion(window, UIElement.OpacityProperty, target, milliseconds, from);
    }

    public static Task<bool> FadeOutAsync(Window window, int milliseconds = 150)
    {
        ArgumentNullException.ThrowIfNull(window); window.VerifyAccess();
        var state = StateFor(window);
        if (!WindowMayFade(window, state)) { PreserveWindowBase(window, state); return Task.FromResult(true); }
        RememberOpacity(window, state); state.Prepared = false;
        return DoubleMotion(window, UIElement.OpacityProperty, 0, milliseconds);
    }

    public static Task<bool> FadeElementIn(FrameworkElement target, int milliseconds = 180)
    {
        ArgumentNullException.ThrowIfNull(target); target.VerifyAccess();
        if (target is Window window) return FadeInAsync(window, milliseconds);
        var state = StateFor(target);
        var opacity = AnimationBase(target, UIElement.OpacityProperty) is double value ? value : 1;
        if (opacity <= 0 && !state.OwnsOpacity) return Task.FromResult(true);
        RememberOpacity(target, state);
        var from = state.Properties.ContainsKey(UIElement.OpacityProperty) ? target.Opacity : 0;
        return DoubleMotion(target, UIElement.OpacityProperty, state.RestingOpacity!.Value, milliseconds, from);
    }

    public static void AnimateDouble(DependencyObject target, DependencyProperty property, double to, int milliseconds = 180)
        => _ = DoubleMotion(target, property, to, milliseconds);

    public static void AnimateThickness(FrameworkElement target, DependencyProperty property, Thickness to, int milliseconds = 180)
    {
        Validate(target, property, typeof(Thickness));
        var from = (Thickness)target.GetValue(property);
        Start(target, property, to, milliseconds, new ThicknessAnimation(from, to, DurationFor(milliseconds))
        { EasingFunction = Ease(), FillBehavior = FillBehavior.Stop });
    }

    public static void AnimateGridLength(DependencyObject target, DependencyProperty property, GridLength to, int milliseconds = 180)
    {
        Validate(target, property, typeof(GridLength));
        var from = (GridLength)target.GetValue(property);
        if (to.IsAbsolute && !from.IsAbsolute)
            from = new GridLength(target switch
            {
                ColumnDefinition column => Math.Max(0, column.ActualWidth),
                RowDefinition row => Math.Max(0, row.ActualHeight),
                _ => Math.Max(0, from.Value)
            });
        // Auto is a layout instruction, not an interpolable number. A change
        // into Star from a different unit also needs a fresh grid measurement.
        if (to.IsAuto || from.IsAuto || from.GridUnitType != to.GridUnitType)
        { SetImmediately(target, property, to); return; }
        Start(target, property, to, milliseconds, new GridLengthAnimation
        { From = from, To = to, Duration = DurationFor(milliseconds), EasingFunction = Ease(), FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Opt-in dialog lifecycle. Main listening windows manage their own switching.</summary>
    public static void EnableWindowFades(Window window)
    {
        ArgumentNullException.ThrowIfNull(window); window.VerifyAccess();
        var state = StateFor(window);
        if (state.ModalEnabled) return;
        state.ModalEnabled = true;
        window.Loaded += ManagedWindowLoaded;
        window.Closing += ManagedWindowClosing;
        if (!window.IsLoaded) PrepareFadeIn(window);
    }

    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
        "Hover", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, HoverChanged));
    public static bool GetHover(DependencyObject target) => (bool)target.GetValue(HoverProperty);
    public static void SetHover(DependencyObject target, bool value) => target.SetValue(HoverProperty, value);

    private static Task<bool> DoubleMotion(DependencyObject target, DependencyProperty property, double to, int milliseconds, double? initial = null)
    {
        Validate(target, property, typeof(double));
        var from = initial ?? (double)target.GetValue(property);
        if (!double.IsFinite(from) && target is FrameworkElement element)
            from = property == FrameworkElement.WidthProperty ? element.ActualWidth
                : property == FrameworkElement.HeightProperty ? element.ActualHeight : from;
        if (!double.IsFinite(from) || !double.IsFinite(to)) return SetImmediately(target, property, to);
        return Start(target, property, to, milliseconds, new DoubleAnimation(from, to, DurationFor(milliseconds))
        { EasingFunction = Ease(), FillBehavior = FillBehavior.Stop }, initial.HasValue && !Same(from, to));
    }

    private static Task<bool> Start(DependencyObject target, DependencyProperty property, object to, int milliseconds, AnimationTimeline animation, bool force = false)
    {
        var state = StateFor(target);
        if (!Enabled || milliseconds <= 0) return SetImmediately(target, property, to);
        if (state.Properties.TryGetValue(property, out var previous))
        {
            if (Same(previous.To, to))
            {
                // Keep the precise latest base value without restarting a clock
                // for subpixel layout noise during native resize/reflow.
                previous.To = to; target.SetCurrentValue(property, to);
                return previous.Completion.Task;
            }
            var current = target.GetValue(property);
            Finish(target, state, previous, false, current);
        }
        if (!force && Same(target.GetValue(property), to) && Same(AnimationBase(target, property), to))
            return SetImmediately(target, property, to);

        target.SetCurrentValue(property, to);
        var clock = animation.CreateClock();
        var motion = new PropertyMotion(property, to, clock, ++_generation);
        var weakTarget = new WeakReference<DependencyObject>(target);
        EventHandler? completed = null;
        completed = (_, _) =>
        {
            if (weakTarget.TryGetTarget(out var owner)) Finish(owner, state, motion, true);
            else
            {
                clock.Completed -= completed;
                clock.Controller?.Remove();
                state.Properties.Remove(property);
                motion.Completion.TrySetResult(false);
            }
        };
        motion.CompletedHandler = completed;
        state.Properties[property] = motion;
        clock.Completed += completed;
        try { ((IAnimatable)target).ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace); }
        catch
        {
            Finish(target, state, motion, false);
            throw;
        }
        return motion.Completion.Task;
    }

    private static void Finish(DependencyObject target, MotionState state, PropertyMotion motion, bool completed, object? replacement = null)
    {
        if (!state.Properties.TryGetValue(motion.Property, out var current) || current.Generation != motion.Generation)
        { motion.Completion.TrySetResult(false); return; }
        state.Properties.Remove(motion.Property);
        motion.Clock.Completed -= motion.CompletedHandler;
        ((IAnimatable)target).ApplyAnimationClock(motion.Property, null);
        motion.Clock.Controller?.Remove();
        target.SetCurrentValue(motion.Property, replacement ?? motion.To);
        motion.Completion.TrySetResult(completed);
    }

    private static Task<bool> SetImmediately(DependencyObject target, DependencyProperty property, object to)
    {
        var state = StateFor(target);
        if (state.Properties.TryGetValue(property, out var previous)) Finish(target, state, previous, false, to);
        else target.SetCurrentValue(property, to);
        return Task.FromResult(true);
    }
    private static object AnimationBase(DependencyObject target, DependencyProperty property) => ((IAnimatable)target).GetAnimationBaseValue(property);
    private static Duration DurationFor(int milliseconds) => new(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)));
    private static IEasingFunction Ease() => new CubicEase { EasingMode = EasingMode.EaseOut };
    private static bool Same(object first, object second) => (first, second) switch
    {
        (double a, double b) => a == b || double.IsNaN(a) && double.IsNaN(b) || Math.Abs(a - b) < .01,
        (GridLength a, GridLength b) => a.GridUnitType == b.GridUnitType && Math.Abs(a.Value - b.Value) < .01,
        (Thickness a, Thickness b) => Math.Abs(a.Left - b.Left) < .01 && Math.Abs(a.Top - b.Top) < .01
            && Math.Abs(a.Right - b.Right) < .01 && Math.Abs(a.Bottom - b.Bottom) < .01,
        _ => Equals(first, second)
    };
    private static void Validate(DependencyObject target, DependencyProperty property, Type type)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(property); target.VerifyAccess();
        if (target is not IAnimatable || property.PropertyType != type || property.ReadOnly)
            throw new ArgumentException("The property must be a writable animation property of the requested type.", nameof(property));
    }
    private static MotionState StateFor(DependencyObject target)
    {
        var state = States.GetOrCreateValue(target);
        if (target is Window window && !state.WindowEventsAttached)
        { state.WindowEventsAttached = true; window.Closed += WindowClosed; }
        return state;
    }
    private static bool WindowMayFade(Window window, MotionState state) => !state.Closed && window.ShowActivated
        && (state.OwnsOpacity || (double)AnimationBase(window, UIElement.OpacityProperty) > 0);
    private static void RememberOpacity(DependencyObject target, MotionState state)
    {
        if (!state.OwnsOpacity)
        {
            state.RestingOpacity = (double)AnimationBase(target, UIElement.OpacityProperty);
            state.OwnsOpacity = true;
        }
    }
    private static void PreserveWindowBase(Window window, MotionState state)
    {
        if (state.Properties.TryGetValue(UIElement.OpacityProperty, out var motion))
            Finish(window, state, motion, false, AnimationBase(window, UIElement.OpacityProperty));
        state.Prepared = false;
    }
    private static void WindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window || !States.TryGetValue(window, out var state)) return;
        state.Closed = true;
        foreach (var motion in state.Properties.Values.ToArray()) Finish(window, state, motion, false);
        window.Closed -= WindowClosed; window.Loaded -= ManagedWindowLoaded; window.Closing -= ManagedWindowClosing;
    }
    private static void ManagedWindowLoaded(object sender, RoutedEventArgs e)
    { if (sender is Window window) _ = FadeInAsync(window); }
    private static async void ManagedWindowClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not Window window || e.Cancel) return;
        var state = StateFor(window);
        if (state.AllowModalClose || !Enabled || !WindowMayFade(window, state) || !window.IsVisible) return;
        e.Cancel = true;
        state.PendingDialogResult = window.DialogResult;
        if (state.ModalClosePending) return;
        state.ModalClosePending = true;
        var finished = await FadeOutAsync(window);
        // A zero-duration/already-zero fade can finish inside the first Closing
        // stack. Defer the real Close until that canceled event has unwound.
        await Task.Yield();
        if (!finished || state.Closed) { state.ModalClosePending = false; return; }
        state.AllowModalClose = true;
        try
        {
            if (state.PendingDialogResult is { } result) window.DialogResult = result;
            else window.Close();
        }
        catch (InvalidOperationException) when (state.Closed || !window.IsVisible) { }
        finally
        {
            state.ModalClosePending = false;
            if (!state.Closed)
            {
                // A separate validation handler canceled the second close.
                state.AllowModalClose = false;
                _ = FadeInAsync(window);
            }
        }
    }

    private static void HoverChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ButtonBase button) return;
        var state = StateFor(button);
        state.HoverEnabled = (bool)e.NewValue;
        if (state.HoverEnabled)
        {
            state.HoverOpacity = (double)AnimationBase(button, UIElement.OpacityProperty);
            button.MouseEnter += HoverMouse; button.MouseLeave += HoverMouse;
            button.PreviewMouseLeftButtonDown += HoverPressed; button.PreviewMouseLeftButtonUp += HoverPressed;
            button.IsEnabledChanged += HoverEnabledChanged; button.Unloaded += HoverUnloaded;
        }
        else
        {
            button.MouseEnter -= HoverMouse; button.MouseLeave -= HoverMouse;
            button.PreviewMouseLeftButtonDown -= HoverPressed; button.PreviewMouseLeftButtonUp -= HoverPressed;
            button.IsEnabledChanged -= HoverEnabledChanged; button.Unloaded -= HoverUnloaded;
            RestoreHover(button, state);
        }
    }
    private static void HoverMouse(object sender, MouseEventArgs e) { if (sender is ButtonBase button) UpdateHover(button); }
    private static void HoverPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ButtonBase button) return;
        var state = StateFor(button); var rest = state.HoverOpacity ?? 1;
        AnimateDouble(button, UIElement.OpacityProperty, Enabled && button.IsEnabled
            ? rest * (e.ButtonState == MouseButtonState.Pressed ? .87 : button.IsMouseOver ? .95 : 1) : rest, 100);
    }
    private static void HoverEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) { if (sender is ButtonBase button) UpdateHover(button); }
    private static void HoverUnloaded(object sender, RoutedEventArgs e) { if (sender is ButtonBase button) RestoreHover(button, StateFor(button)); }
    private static void UpdateHover(ButtonBase button)
    {
        var state = StateFor(button); var rest = state.HoverOpacity ?? 1;
        AnimateDouble(button, UIElement.OpacityProperty, Enabled && button.IsEnabled && button.IsMouseOver ? rest * .95 : rest, 130);
    }
    private static void RestoreHover(ButtonBase button, MotionState state)
    { if (state.HoverOpacity is { } opacity) SetImmediately(button, UIElement.OpacityProperty, opacity); }

    private sealed class MotionState
    {
        public Dictionary<DependencyProperty, PropertyMotion> Properties { get; } = [];
        public double? RestingOpacity;
        public bool OwnsOpacity, Prepared, WindowEventsAttached, Closed;
        public bool ModalEnabled, ModalClosePending, AllowModalClose;
        public bool? PendingDialogResult;
        public bool HoverEnabled;
        public double? HoverOpacity;
    }
    private sealed class PropertyMotion(DependencyProperty property, object to, AnimationClock clock, long generation)
    {
        public DependencyProperty Property { get; } = property;
        public object To { get; set; } = to;
        public AnimationClock Clock { get; } = clock;
        public long Generation { get; } = generation;
        public EventHandler? CompletedHandler;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Interpolates compatible pixel or star grid lengths; Auto is resolved by UiMotion.</summary>
public sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(nameof(From), typeof(GridLength), typeof(GridLengthAnimation), new PropertyMetadata(new GridLength(0)));
    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(nameof(To), typeof(GridLength), typeof(GridLengthAnimation), new PropertyMetadata(new GridLength(0)));
    public static readonly DependencyProperty EasingFunctionProperty = DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimation));
    public GridLength From { get => (GridLength)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public GridLength To { get => (GridLength)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public IEasingFunction? EasingFunction { get => (IEasingFunction?)GetValue(EasingFunctionProperty); set => SetValue(EasingFunctionProperty, value); }
    public override Type TargetPropertyType => typeof(GridLength);
    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();
    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        if (From.IsAuto || To.IsAuto || From.GridUnitType != To.GridUnitType) return To;
        var progress = animationClock.CurrentProgress ?? 1;
        progress = EasingFunction?.Ease(progress) ?? progress;
        return new GridLength(Math.Max(0, From.Value + (To.Value - From.Value) * progress), To.GridUnitType);
    }
}
