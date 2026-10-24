using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XTONE.Controls;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var canvas = new Canvas { Background = Brushes.White, ClipToBounds = true };
            var window = new Window { Width = 440, Height = 300, Content = canvas, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            try
            {
                var existing = new TranslateTransform(2, 3);
                var card = Card(canvas, "alpha", 20, 20);
                card.RenderTransform = existing;
                window.Show();
                await Flush(window);
                Check(!Animated(card), "first layout settles without an entrance jump");
                Check(Near(Target(card).X, 20), "target measurement excludes the existing render transform");

                Canvas.SetLeft(card, 100);
                card.Width = 120;
                await Flush(window);
                Check(Animated(card), "layout move and resize start render-transform clocks");
                Check(card.RenderTransform is TransformGroup group && group.Children.Contains(existing), "layout motion preserves the existing transform object");
                Check(Near(Target(card).X, 100) && Near(Target(card).Width, 120), "stored geometry is the new layout target");
                var beforeReverse = Displayed(card);
                Canvas.SetLeft(card, 40);
                card.Width = 90;
                await Flush(window);
                var afterReverse = Displayed(card);
                Check(Animated(card) && Math.Abs(afterReverse.X - beforeReverse.X) < 28,
                    "rapid reversal starts from current animation instead of the previous target");
                Check(Near(Target(card).X, 40) && Near(Target(card).Width, 90), "reversal never feeds transformed bounds into layout measurement");
                await Task.Delay(300);
                Check(!Animated(card) && Near(Displayed(card).X, 40), "completed animation clocks are removed at the final target");

                Canvas.SetLeft(card, 150);
                await Flush(window);
                FluidLayout.MotionEnabled = false;
                Check(!Animated(card), "disabling motion immediately removes active clocks");
                Canvas.SetLeft(card, 70);
                await Flush(window);
                Check(!Animated(card) && Near(Target(card).X, 70), "disabled motion applies the latest layout directly");
                FluidLayout.MotionEnabled = true;
                await Flush(window);
                Canvas.SetLeft(card, 160);
                await Flush(window);
                Check(Animated(card), "motion can resume after being disabled");
                FluidLayout.Reset(canvas);
                Check(!Animated(card), "explicit reset removes transform clocks");
                await Flush(window);

                Canvas.SetLeft(card, 180);
                await Flush(window);
                await Task.Delay(250);
                canvas.Children.Remove(card);
                var replacement = Card(canvas, "alpha", 35, 20);
                window.Width = 470;
                await Flush(window);
                Check(Animated(replacement) && Displayed(replacement).X > 65, "new row instances hand off the prior keyed card geometry");
                Check(ReferenceEquals(card.RenderTransform, existing), "unloaded cards restore the original transform");
                await Task.Delay(250);

                // Adding/removing keys at the same viewport width is filtering, not reflow.
                canvas.Children.Remove(replacement);
                var filtered = Card(canvas, "beta", 100, 20);
                await Flush(window);
                Check(!Animated(filtered), "content filtering settles without animating stale cards");
                canvas.Children.Remove(filtered);
                var restored = Card(canvas, "alpha", 20, 20);
                await Flush(window);
                Check(!Animated(restored), "returning from a filter does not reuse stale geometry");

                var scrollCanvas = new Canvas { Width = 420, Height = 1000, Background = Brushes.White };
                var scroll = new ScrollViewer { Content = scrollCanvas, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                var scrollCard = Card(scrollCanvas, "scroll", 20, 60, scroll);
                window.Content = scroll;
                await Flush(window);
                Canvas.SetLeft(scrollCard, 100);
                await Flush(window);
                Check(Animated(scrollCard), "scroll test begins with an active reflow");
                scroll.ScrollToVerticalOffset(30);
                await Flush(window);
                Check(!Animated(scrollCard) && Near(Target(scrollCard).Y, 30), "scrolling settles motion and records the scrolled layout directly");

                var offscreen = Card(scrollCanvas, "offscreen", 20, 900, scroll);
                await Flush(window);
                Canvas.SetLeft(offscreen, 100);
                await Flush(window);
                Check(!Animated(offscreen), "cards outside the viewport do not animate");

                FluidLayout.SetIsEnabled(scrollCard, false);
                Check(!Animated(scrollCard), "detaching the behavior removes its clocks");

                var cacheCanvas = new Canvas { Background = Brushes.White };
                var many = new List<Border>();
                for (var index = 0; index < 600; index++)
                {
                    var item = Card(cacheCanvas, "cache-" + index, index % 40 * 10, index / 40 * 10);
                    item.Width = item.Height = 8;
                    many.Add(item);
                }
                window.Content = cacheCanvas;
                await Flush(window);
                Check(CacheCount(cacheCanvas) == 512, "keyed geometry cache is bounded to 512 entries");
                foreach (var item in many) FluidLayout.SetIsEnabled(item, false);
                Check(CacheCount(cacheCanvas) == 0, "detaching all cards releases their cached geometry");
            }
            catch (Exception error) { Check(false, error.ToString()); }
            finally
            {
                FluidLayout.MotionEnabled = true;
                window.Close();
                Console.WriteLine($"Fluid layout tests: {_passed} passed, {_failed} failed.");
                app.Shutdown(_failed == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static Border Card(Canvas canvas, string key, double x, double y, FrameworkElement? scope = null)
    {
        var card = new Border { Width = 80, Height = 60, Background = Brushes.CornflowerBlue, RenderTransformOrigin = new Point(.5, .5) };
        Canvas.SetLeft(card, x); Canvas.SetTop(card, y);
        FluidLayout.SetKey(card, key); FluidLayout.SetScope(card, scope ?? canvas); FluidLayout.SetIsEnabled(card, true);
        canvas.Children.Add(card);
        return card;
    }

    private static async Task Flush(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    }

    private static object State(FrameworkElement element)
    {
        var table = typeof(FluidLayout).GetField("Elements", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object?[] args = [element, null];
        table.GetType().GetMethod("TryGetValue")!.Invoke(table, args);
        return args[1]!;
    }

    private static int CacheCount(FrameworkElement scope)
    {
        var table = typeof(FluidLayout).GetField("Scopes", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object?[] args = [scope, null];
        table.GetType().GetMethod("TryGetValue")!.Invoke(table, args);
        var cache = (System.Collections.IDictionary)args[1]!.GetType().GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(args[1])!;
        return cache.Count;
    }

    private static Rect Target(FrameworkElement element) => (Rect)State(element).GetType().GetProperty("TargetRect")!.GetValue(State(element))!;
    private static Rect Displayed(FrameworkElement element) => (Rect)State(element).GetType().GetProperty("DisplayedRect")!.GetValue(State(element))!;
    private static bool Animated(FrameworkElement element) => element.RenderTransform is TransformGroup group && group.Children.Any(transform => transform.HasAnimatedProperties);
    private static bool Near(double value, double expected) => Math.Abs(value - expected) < .6;
    private static void Check(bool condition, string message)
    {
        if (condition) { _passed++; Console.WriteLine("PASS " + message); }
        else { _failed++; Console.Error.WriteLine("FAIL " + message); }
    }
}
