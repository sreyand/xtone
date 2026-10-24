using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace XTONE.Controls;

/// <summary>Animates layout changes with render transforms, keeping the layout engine authoritative.</summary>
public static class FluidLayout
{
    private const int CacheLimit = 512;
    private const long HandoffLifetime = 750;
    private static readonly ConditionalWeakTable<FrameworkElement, ElementState> Elements = new();
    private static readonly ConditionalWeakTable<FrameworkElement, ScopeState> Scopes = new();
    private static readonly List<WeakReference<ScopeState>> ActiveScopes = [];
    private static readonly object ScopeLock = new();
    private static readonly CubicEase Ease = CreateEase();
    private static int _motionEnabled = 1;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(FluidLayout), new PropertyMetadata(false, EnabledChanged));
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(FluidLayout), new PropertyMetadata(null, ConfigurationChanged));
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(FrameworkElement), typeof(FluidLayout), new PropertyMetadata(null, ConfigurationChanged));

    public static bool GetIsEnabled(DependencyObject value) => (bool)value.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject value, bool enabled) => value.SetValue(IsEnabledProperty, enabled);
    public static string? GetKey(DependencyObject value) => (string?)value.GetValue(KeyProperty);
    public static void SetKey(DependencyObject value, string? key) => value.SetValue(KeyProperty, key);
    public static FrameworkElement? GetScope(DependencyObject value) => (FrameworkElement?)value.GetValue(ScopeProperty);
    public static void SetScope(DependencyObject value, FrameworkElement? scope) => value.SetValue(ScopeProperty, scope);

    public static bool MotionEnabled
    {
        get => Volatile.Read(ref _motionEnabled) != 0;
        set
        {
            if (Interlocked.Exchange(ref _motionEnabled, value ? 1 : 0) == (value ? 1 : 0)) return;
            ScopeState[] live;
            lock (ScopeLock)
            {
                ActiveScopes.RemoveAll(reference => !reference.TryGetTarget(out _));
                live = ActiveScopes.Select(reference => reference.TryGetTarget(out var scope) ? scope : null)
                    .OfType<ScopeState>().ToArray();
            }
            foreach (var scope in live) scope.ResetOnDispatcher();
        }
    }

    /// <summary>Forgets a scope's old geometry, for example before replacing its entire data source.</summary>
    public static void Reset(FrameworkElement scope)
    {
        if (Scopes.TryGetValue(scope, out var state)) state.ResetOnDispatcher();
    }

    private static void EnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)args.NewValue) Elements.GetValue(element, static owner => new ElementState(owner)).Attach();
        else if (Elements.TryGetValue(element, out var state)) { state.Detach(); Elements.Remove(element); }
    }

    private static void ConfigurationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element && Elements.TryGetValue(element, out var state)) state.Reconfigure();
    }

    private static ScopeState FindScope(FrameworkElement element)
    {
        var owner = GetScope(element) ?? Window.GetWindow(element) ?? element.Parent as FrameworkElement ?? element;
        return Scopes.GetValue(owner, static scope =>
        {
            var state = new ScopeState(scope);
            lock (ScopeLock)
            {
                ActiveScopes.RemoveAll(reference => !reference.TryGetTarget(out _));
                ActiveScopes.Add(new(state));
            }
            return state;
        });
    }

    private static CubicEase CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private readonly record struct Geometry(Rect Rect, double ParentScaleX, double ParentScaleY, Point Origin);
    private sealed record Snapshot(ElementState Element, Geometry Geometry);

    private sealed class CacheEntry(string key, Geometry target, ElementState owner)
    {
        public string Key { get; } = key;
        public Geometry Target { get; set; } = target;
        public Rect Displayed { get; set; } = target.Rect;
        public WeakReference<ElementState> Owner { get; set; } = new(owner);
        public long Seen { get; set; } = Environment.TickCount64;
    }

    private sealed class ScopeState
    {
        private readonly WeakReference<FrameworkElement> _scope;
        private readonly HashSet<ElementState> _members = [];
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.Ordinal);
        private readonly LinkedList<CacheEntry> _recent = [];
        private HashSet<string> _lastKeys = new(StringComparer.Ordinal);
        private WeakReference<ScrollViewer>? _scroll;
        private DispatcherOperation? _pending;
        private double _lastWidth;
        private bool _hasSnapshot;
        private bool _scrolled;

        public ScopeState(FrameworkElement scope)
        {
            _scope = new(scope);
            scope.IsVisibleChanged += ScopeVisibilityChanged;
            scope.Unloaded += ScopeUnloaded;
        }

        public void Add(ElementState state)
        {
            _members.Add(state);
            if (_scroll is null || !_scroll.TryGetTarget(out _))
            {
                DependencyObject? parent = state.Host;
                while (parent is not null)
                {
                    if (parent is ScrollViewer viewer)
                    {
                        _scroll = new(viewer);
                        viewer.ScrollChanged += ScrollChanged;
                        break;
                    }
                    if (_scope.TryGetTarget(out var scope) && ReferenceEquals(parent, scope)) break;
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }
            Schedule();
        }

        public void Remove(ElementState state, bool retain)
        {
            if (state.Key is { Length: > 0 } key && _cache.TryGetValue(key, out var node) &&
                node.Value.Owner.TryGetTarget(out var owner) && ReferenceEquals(owner, state))
            {
                if (retain)
                {
                    Preserve(state);
                }
                else RemoveEntry(node);
            }
            _members.Remove(state);
        }

        public void Preserve(ElementState state)
        {
            if (state.HasGeometry && state.Key is { Length: > 0 } key && _cache.TryGetValue(key, out var node) &&
                node.Value.Owner.TryGetTarget(out var owner) && ReferenceEquals(owner, state))
            {
                node.Value.Displayed = state.DisplayedRect;
                node.Value.Seen = Environment.TickCount64;
            }
        }

        public void Schedule()
        {
            if (_pending is not null || !_scope.TryGetTarget(out var scope) || scope.Dispatcher.HasShutdownStarted) return;
            _pending = scope.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Process));
        }

        private void Process()
        {
            _pending = null;
            if (!_scope.TryGetTarget(out var scope) || !scope.IsVisible) { ResetCore(); return; }
            var viewport = new Rect(scope.RenderSize);
            var samples = new List<Snapshot>();
            foreach (var state in _members.ToArray())
            {
                if (state.TryMeasure(scope, out var geometry) && InViewport(geometry.Rect, viewport))
                    samples.Add(new(state, geometry));
                else state.Settle(null);
            }
            // Row regrouping can briefly have no loaded children. Preserve the prior snapshot
            // until the replacement rows arrive rather than treating that gap as a new page.
            if (samples.Count == 0) return;
            var keys = samples.Select(sample => sample.Element.Identity).ToHashSet(StringComparer.Ordinal);
            var duplicateKeys = samples.GroupBy(sample => sample.Element.Identity).Where(group => group.Count() > 1)
                .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            var widthChanged = _hasSnapshot && Math.Abs(scope.RenderSize.Width - _lastWidth) > .2;
            var contentChanged = _hasSnapshot && !_lastKeys.SetEquals(keys) && !widthChanged;
            var settle = !MotionEnabled || !_hasSnapshot || _scrolled || contentChanged;
            if (_scrolled || contentChanged) { _cache.Clear(); _recent.Clear(); }

            foreach (var sample in samples)
            {
                var state = sample.Element;
                Rect? previous = state.HasGeometry ? state.DisplayedRect : null;
                if (previous is null && state.Key is { Length: > 0 } key && !duplicateKeys.Contains(state.Identity) &&
                    _cache.TryGetValue(key, out var cached) && Environment.TickCount64 - cached.Value.Seen <= HandoffLifetime)
                {
                    var entry = cached.Value;
                    previous = entry.Owner.TryGetTarget(out var owner) && owner.HasGeometry && owner.Key == key
                        ? owner.DisplayedRect : entry.Displayed;
                }
                if (settle || previous is null || !InViewport(previous.Value, viewport)) state.Settle(sample.Geometry);
                else if (!state.HasGeometry || Different(state.TargetRect, sample.Geometry.Rect)) state.Animate(previous.Value, sample.Geometry);
                else state.Accept(sample.Geometry);

                if (state.Key is { Length: > 0 } cacheKey && !duplicateKeys.Contains(state.Identity))
                    Remember(cacheKey, sample.Geometry, state);
            }
            _lastWidth = scope.RenderSize.Width;
            _lastKeys = keys;
            _hasSnapshot = true;
            _scrolled = false;
        }

        private void Remember(string key, Geometry geometry, ElementState state)
        {
            if (_cache.TryGetValue(key, out var existing)) RemoveEntry(existing);
            var entry = new CacheEntry(key, geometry, state) { Displayed = state.DisplayedRect };
            _cache[key] = _recent.AddFirst(entry);
            while (_cache.Count > CacheLimit && _recent.Last is { } oldest) RemoveEntry(oldest);
        }

        private void RemoveEntry(LinkedListNode<CacheEntry> node)
        {
            _cache.Remove(node.Value.Key);
            _recent.Remove(node);
        }

        private void ScrollChanged(object sender, ScrollChangedEventArgs args)
        {
            if (Math.Abs(args.HorizontalChange) < .01 && Math.Abs(args.VerticalChange) < .01) return;
            _scrolled = true;
            foreach (var member in _members) member.Settle(null);
            Schedule();
        }

        private void ScopeVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue) ResetCore(); else Schedule();
        }

        private void ScopeUnloaded(object sender, RoutedEventArgs args)
        {
            if (_scroll is not null && _scroll.TryGetTarget(out var viewer)) viewer.ScrollChanged -= ScrollChanged;
            _scroll = null;
            ResetCore();
        }

        public void ResetOnDispatcher()
        {
            if (!_scope.TryGetTarget(out var scope) || scope.Dispatcher.HasShutdownStarted) return;
            void ResetAndCapture()
            {
                ResetCore();
                if (scope.IsVisible) Schedule();
            }
            if (scope.Dispatcher.CheckAccess()) ResetAndCapture();
            else scope.Dispatcher.BeginInvoke(new Action(ResetAndCapture));
        }

        private void ResetCore()
        {
            _pending?.Abort(); _pending = null;
            foreach (var member in _members) member.Settle(null);
            _cache.Clear(); _recent.Clear(); _lastKeys.Clear();
            _hasSnapshot = false; _scrolled = false;
        }
    }

    private sealed class ElementState(FrameworkElement host)
    {
        private readonly string _identity = "\0" + Guid.NewGuid().ToString("N");
        private ScopeState? _scope;
        private Geometry? _geometry;
        private Transform? _original;
        private TransformGroup? _group;
        private ScaleTransform? _scale;
        private TranslateTransform? _translate;
        private int _generation;
        private bool _attached;
        public FrameworkElement Host { get; } = host;
        public string? Key { get; private set; }
        public string Identity => string.IsNullOrEmpty(Key) ? _identity : Key;
        public bool HasGeometry => _geometry is not null;
        public Rect TargetRect => _geometry?.Rect ?? Rect.Empty;
        public Rect DisplayedRect
        {
            get
            {
                if (_geometry is not { } geometry) return Rect.Empty;
                if (_scale is null || _translate is null || !ReferenceEquals(Host.RenderTransform, _group)) return geometry.Rect;
                var rect = geometry.Rect;
                return new Rect(rect.X + (1 - _scale.ScaleX) * rect.Width * geometry.Origin.X + _translate.X * geometry.ParentScaleX,
                    rect.Y + (1 - _scale.ScaleY) * rect.Height * geometry.Origin.Y + _translate.Y * geometry.ParentScaleY,
                    Math.Max(0, rect.Width * _scale.ScaleX), Math.Max(0, rect.Height * _scale.ScaleY));
            }
        }

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            Host.Loaded += Loaded; Host.Unloaded += Unloaded;
            Host.LayoutUpdated += LayoutUpdated; Host.IsVisibleChanged += VisibilityChanged;
            if (Host.IsLoaded) Register();
        }

        public void Detach()
        {
            _scope?.Remove(this, false); _scope = null;
            RestoreTransform(); _geometry = null;
            Host.Loaded -= Loaded; Host.Unloaded -= Unloaded;
            Host.LayoutUpdated -= LayoutUpdated; Host.IsVisibleChanged -= VisibilityChanged;
            _attached = false;
        }

        public void Reconfigure()
        {
            _scope?.Remove(this, true); _scope = null;
            RestoreTransform(); _geometry = null;
            if (Host.IsLoaded) Register();
        }

        private void Register()
        {
            Key = GetKey(Host);
            _scope = FindScope(Host);
            _scope.Add(this);
        }

        private void Loaded(object sender, RoutedEventArgs args) { if (_scope is null) Register(); }
        private void Unloaded(object sender, RoutedEventArgs args)
        {
            _scope?.Remove(this, true); _scope = null;
            RestoreTransform(); _geometry = null;
        }
        private void LayoutUpdated(object? sender, EventArgs args) => _scope?.Schedule();
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue) { _scope?.Preserve(this); Settle(null); }
            _scope?.Schedule();
        }

        public bool TryMeasure(FrameworkElement scope, out Geometry geometry)
        {
            geometry = default;
            if (!Host.IsLoaded || !Host.IsVisible || Host.RenderSize.Width < 1 || Host.RenderSize.Height < 1) return false;
            try
            {
                var offset = VisualTreeHelper.GetOffset(Host);
                var parent = VisualTreeHelper.GetParent(Host) as Visual;
                if (parent is null) return false;
                // The parent's transform plus VisualOffset excludes this element's entire
                // RenderTransform, so animation never feeds back into measured layout.
                var transform = ReferenceEquals(parent, scope) ? Transform.Identity : parent.TransformToAncestor(scope);
                var origin = transform.Transform(new Point(0, 0));
                var x = transform.Transform(new Point(1, 0));
                var y = transform.Transform(new Point(0, 1));
                if (Math.Abs(x.Y - origin.Y) > .001 || Math.Abs(y.X - origin.X) > .001 || x.X <= origin.X || y.Y <= origin.Y) return false;
                var rect = transform.TransformBounds(new Rect(new Point(offset.X, offset.Y), Host.RenderSize));
                if (!double.IsFinite(rect.X + rect.Y + rect.Width + rect.Height)) return false;
                geometry = new(rect, x.X - origin.X, y.Y - origin.Y, Host.RenderTransformOrigin);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        public void Accept(Geometry geometry) => _geometry = geometry;

        public void Settle(Geometry? geometry)
        {
            StopClocks();
            _geometry = geometry;
        }

        public void Animate(Rect previous, Geometry target)
        {
            if (!Different(previous, target.Rect)) { Settle(target); return; }
            EnsureTransform();
            var scaleX = Math.Clamp(previous.Width / target.Rect.Width, .15, 6);
            var scaleY = Math.Clamp(previous.Height / target.Rect.Height, .15, 6);
            var translateX = (previous.X - target.Rect.X) / target.ParentScaleX
                - (1 - scaleX) * Host.RenderSize.Width * target.Origin.X;
            var translateY = (previous.Y - target.Rect.Y) / target.ParentScaleY
                - (1 - scaleY) * Host.RenderSize.Height * target.Origin.Y;
            StopClocks();
            _geometry = target;
            var version = _generation;
            var finish = Animation(translateY, 0);
            finish.Completed += (_, _) => { if (version == _generation) StopClocks(); };
            finish.Freeze();
            _scale!.BeginAnimation(ScaleTransform.ScaleXProperty, FrozenAnimation(scaleX, 1));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, FrozenAnimation(scaleY, 1));
            _translate!.BeginAnimation(TranslateTransform.XProperty, FrozenAnimation(translateX, 0));
            _translate.BeginAnimation(TranslateTransform.YProperty, finish);
        }

        private void EnsureTransform()
        {
            if (_group is not null && ReferenceEquals(Host.RenderTransform, _group)) return;
            StopClocks();
            _original = Host.RenderTransform;
            _scale = new ScaleTransform(1, 1); _translate = new TranslateTransform();
            _group = new TransformGroup();
            if (_original is not null) _group.Children.Add(_original);
            _group.Children.Add(_scale); _group.Children.Add(_translate);
            Host.SetCurrentValue(UIElement.RenderTransformProperty, _group);
        }

        private void StopClocks()
        {
            _generation++;
            if (_scale is not null)
            {
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _scale.ScaleX = 1;
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); _scale.ScaleY = 1;
            }
            if (_translate is not null)
            {
                _translate.BeginAnimation(TranslateTransform.XProperty, null); _translate.X = 0;
                _translate.BeginAnimation(TranslateTransform.YProperty, null); _translate.Y = 0;
            }
        }

        private void RestoreTransform()
        {
            StopClocks();
            if (_group is not null && ReferenceEquals(Host.RenderTransform, _group))
                Host.SetCurrentValue(UIElement.RenderTransformProperty, _original);
            _group = null; _scale = null; _translate = null; _original = null;
        }
    }

    private static bool InViewport(Rect rect, Rect viewport) => !rect.IsEmpty && rect.Width > 1 && rect.Height > 1 && rect.IntersectsWith(viewport);
    private static bool Different(Rect first, Rect second) => Math.Abs(first.X - second.X) > .15 || Math.Abs(first.Y - second.Y) > .15
        || Math.Abs(first.Width - second.Width) > .15 || Math.Abs(first.Height - second.Height) > .15;
    private static DoubleAnimation Animation(double from, double to) => new(from, to, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop };
    private static DoubleAnimation FrozenAnimation(double from, double to) { var animation = Animation(from, to); animation.Freeze(); return animation; }
}
