using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using XTONE;
using XTONE.Models;
using XTONE.Services;
using XTONE.Views;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Application? _app;
    private static MainWindow? _main;
    private static int _passed;
    private static int _exitCode = 1;

    [STAThread]
    private static int Main()
    {
        var originalDataRoot = Environment.GetEnvironmentVariable("XTONE_DATA_DIR");
        var fixtureParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
        var fixture = Path.Combine(fixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        Environment.SetEnvironmentVariable("XTONE_DATA_DIR", fixture);
        var audio = WriteWav(Path.Combine(fixture, "motion.wav"));
        var seeded = new LibraryStore(fixture);
        seeded.ImportAsync([audio]).GetAwaiter().GetResult();
        seeded.SaveSession(new PlaybackSession
        { Volume = 0, ReducedMotion = false, QueuePaths = [audio, audio], CurrentIndex = 0, PositionSeconds = .25 });
        try
        {
            _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/XTONE;component/Themes/DesignSystem.xaml", UriKind.Relative) });
            _app.Resources[typeof(Window)] = new Style(typeof(Window))
            {
                Setters =
                {
                    new Setter(UIElement.OpacityProperty, 0d),
                    new Setter(Window.ShowActivatedProperty, false),
                    new Setter(Window.ShowInTaskbarProperty, false)
                }
            };
            _app.DispatcherUnhandledException += (_, args) =>
            {
                Console.Error.WriteLine("Dispatcher failure: " + args.Exception);
                args.Handled = true;
                _exitCode = 1;
                _app.Shutdown(1);
                _app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            _app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var originalMotion = UiMotion.Enabled;
                try
                {
                    await RunChecksAsync();
                    Console.WriteLine($"PASS: {_passed} motion checks.");
                    _exitCode = 0;
                }
                catch (Exception error) { Console.Error.WriteLine(error); _exitCode = 1; }
                finally
                {
                    UiMotion.Enabled = false;
                    foreach (var window in _app.Windows.Cast<Window>().ToArray())
                    {
                        try
                        {
                            var closed = ClosedTask(window);
                            window.Close();
                            await closed.WaitAsync(TimeSpan.FromSeconds(10));
                        }
                        catch (Exception error) { Console.Error.WriteLine("Cleanup: " + error.Message); _exitCode = 1; }
                    }
                    UiMotion.Enabled = originalMotion;
                    _app.Shutdown(_exitCode);
                    _app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            }));
            Dispatcher.Run();
        }
        catch (Exception error) { Console.Error.WriteLine(error); _exitCode = 1; _app?.Shutdown(1); }
        finally
        {
            Environment.SetEnvironmentVariable("XTONE_DATA_DIR", originalDataRoot);
            var resolved = Path.GetFullPath(fixture);
            if (resolved.StartsWith(fixtureParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(resolved, true);
        }
        return _exitCode;
    }

    private static async Task RunChecksAsync()
    {
        UiMotion.Enabled = true;
        var fade = await HostAsync(prepareFade: true);
        Near(0, fade.Opacity);
        True(await UiMotion.FadeInAsync(fade, 90).WaitAsync(TimeSpan.FromSeconds(3)));
        Check("fade-in reaches the resting opacity and releases its animation clock", () =>
        {
            Near(1, fade.Opacity);
            Near(1, (double)fade.GetAnimationBaseValue(UIElement.OpacityProperty));
            True(!fade.HasAnimatedProperties);
        });

        var fadingOut = UiMotion.FadeOutAsync(fade, 240);
        await Task.Delay(35);
        var fadingIn = UiMotion.FadeInAsync(fade, 100);
        var outcomes = await Task.WhenAll(fadingOut, fadingIn).WaitAsync(TimeSpan.FromSeconds(3));
        Check("reversed fades settle both tasks and only the latest destination wins", () =>
        {
            Equal(false, outcomes[0]); Equal(true, outcomes[1]);
            Near(1, fade.Opacity);
            Near(1, (double)fade.GetAnimationBaseValue(UIElement.OpacityProperty));
            True(!fade.HasAnimatedProperties);
        });

        var interrupted = UiMotion.FadeOutAsync(fade, 250);
        await Task.Delay(25);
        UiMotion.Enabled = false;
        var immediate = UiMotion.FadeInAsync(fade, 250);
        Check("disabling motion settles in-flight work and applies the final fade endpoint immediately", () =>
        {
            True(interrupted.IsCompleted && immediate.IsCompletedSuccessfully && immediate.Result);
            Near(1, fade.Opacity);
            Near(1, (double)fade.GetAnimationBaseValue(UIElement.OpacityProperty));
            True(!fade.HasAnimatedProperties);
        });
        fade.Close();

        var privacy = await HostAsync(transparent: true);
        UiMotion.Enabled = true;
        UiMotion.PrepareFadeIn(privacy);
        var privateIn = UiMotion.FadeInAsync(privacy, 200);
        var privateOut = UiMotion.FadeOutAsync(privacy, 200);
        Check("nonactivating transparent component hosts stay transparent", () =>
        {
            True(privateIn.IsCompletedSuccessfully && privateOut.IsCompletedSuccessfully);
            Near(0, privacy.Opacity);
            True(!privacy.HasAnimatedProperties);
        });
        privacy.Close();

        var propertyHost = await HostAsync();
        var border = (Border)propertyHost.Content;
        UiMotion.Enabled = true;
        UiMotion.AnimateDouble(border, UIElement.OpacityProperty, .2, 220);
        await Task.Delay(25);
        UiMotion.AnimateDouble(border, UIElement.OpacityProperty, .7, 70);
        UiMotion.AnimateThickness(border, FrameworkElement.MarginProperty, new Thickness(12, 6, 18, 8), 80);
        await Task.Delay(330);
        Check("property transitions finish at their latest double and thickness base values", () =>
        {
            Near(.7, border.Opacity);
            Near(.7, (double)border.GetAnimationBaseValue(UIElement.OpacityProperty));
            Equal(new Thickness(12, 6, 18, 8), border.Margin);
            True(!border.HasAnimatedProperties);
        });
        UiMotion.Enabled = false;
        UiMotion.AnimateDouble(border, UIElement.OpacityProperty, .4, 500);
        UiMotion.AnimateThickness(border, FrameworkElement.MarginProperty, new Thickness(3), 500);
        Check("motion-off property changes commit synchronously without held clocks", () =>
        {
            Near(.4, border.Opacity);
            Near(.4, (double)border.GetAnimationBaseValue(UIElement.OpacityProperty));
            Equal(new Thickness(3), border.Margin);
            True(!border.HasAnimatedProperties);
        });

        var boundSurface = new Border { Background = Brushes.White };
        var opacitySource = new OpacitySource { Value = 1 };
        BindingOperations.SetBinding(boundSurface, UIElement.OpacityProperty,
            new Binding(nameof(OpacitySource.Value)) { Source = opacitySource });
        propertyHost.Content = boundSurface;
        propertyHost.UpdateLayout();
        UiMotion.Enabled = true;
        var elementFade = UiMotion.FadeElementIn(boundSurface, 120);
        True(!elementFade.IsCompleted && boundSurface.HasAnimatedProperties);
        True(await elementFade.WaitAsync(TimeSpan.FromSeconds(3)));
        opacitySource.Value = .61;
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Check("element entry actually animates and then preserves its opacity binding", () =>
        {
            True(BindingOperations.IsDataBound(boundSurface, UIElement.OpacityProperty));
            Near(.61, boundSurface.Opacity);
            Near(.61, (double)boundSurface.GetAnimationBaseValue(UIElement.OpacityProperty));
            True(!boundSurface.HasAnimatedProperties);
        });

        var grid = new Grid();
        var row = new RowDefinition { Height = new GridLength(20) };
        grid.RowDefinitions.Add(row);
        propertyHost.Content = grid;
        propertyHost.UpdateLayout();
        UiMotion.Enabled = true;
        UiMotion.AnimateGridLength(row, RowDefinition.HeightProperty, new GridLength(80), 75);
        await Task.Delay(240);
        Check("pixel grid-length transitions settle at a real pixel base value", () => Equal(new GridLength(80), row.Height));
        UiMotion.AnimateGridLength(row, RowDefinition.HeightProperty, GridLength.Auto, 150);
        Check("auto grid-length transitions retain Auto semantics", () => True(row.Height.IsAuto));
        row.Height = new GridLength(1, GridUnitType.Star);
        UiMotion.AnimateGridLength(row, RowDefinition.HeightProperty, new GridLength(3, GridUnitType.Star), 70);
        await Task.Delay(220);
        Check("same-unit star transitions preserve star layout semantics", () => Equal(new GridLength(3, GridUnitType.Star), row.Height));
        propertyHost.Close();

        foreach (var accepted in new[] { true, false })
        {
            UiMotion.Enabled = true;
            var modal = new Window
            {
                Width = 260, Height = 140, ShowActivated = false, ShowInTaskbar = false,
                Opacity = 0, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
                Content = new TextBlock { Text = "Own offscreen modal fixture", Margin = new Thickness(20) }
            };
            UiMotion.EnableWindowFades(modal);
            var modalWatch = new Stopwatch();
            modal.Loaded += (_, _) =>
            {
                // The modal was already shown nonactivating. Do not activate or
                // show it again; opt its existing offscreen surface into closing motion.
                modal.ShowActivated = true; modal.Opacity = 1;
                modal.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                { modalWatch.Start(); modal.DialogResult = accepted; }));
            };
            var result = modal.ShowDialog();
            Check($"animated modal close preserves DialogResult {accepted}", () =>
            {
                Equal((bool?)accepted, result);
                True(modalWatch.Elapsed >= TimeSpan.FromMilliseconds(80));
                Near(0, modal.Opacity);
                True(!modal.HasAnimatedProperties);
            });
        }

        var fullScreen = await FocusHostAsync();
        Invoke(fullScreen, "ToggleFullScreen");
        await Task.Delay(25);
        Invoke(fullScreen, "ToggleFullScreen");
        await UntilAsync(() => Get<bool?>(fullScreen, "_fullScreenIntent") is null && Math.Abs(fullScreen.Opacity - 1) < .02,
            "the latest fullscreen toggle to return to normal");
        Check("rapid fullscreen toggles finish at the latest normal-window intent", () =>
        {
            Equal(WindowState.Normal, fullScreen.WindowState);
            True(!Get<bool>(fullScreen, "_fullScreen"));
            Near(1, fullScreen.Opacity);
        });
        await UiMotion.FadeOutAsync(fullScreen, 0);
        Near(0, fullScreen.Opacity);
        Invoke(fullScreen, "RequestFullScreen", false);
        await UntilAsync(() => Get<bool?>(fullScreen, "_fullScreenIntent") is null && Math.Abs(fullScreen.Opacity - 1) < .02,
            "an owned-zero fullscreen reversal to restore opacity");
        Check("fullscreen reversal from a UiMotion-owned zero opacity restores the surface", () =>
        {
            Equal(WindowState.Normal, fullScreen.WindowState);
            Near(1, fullScreen.Opacity);
            True(!fullScreen.HasAnimatedProperties);
        });
        Invoke(fullScreen, "ToggleFullScreen");
        await Task.Delay(25);
        fullScreen.PrepareToHide();
        fullScreen.Hide();
        await Task.Delay(330);
        Check("PrepareToHide prevents delayed fullscreen or opacity resurrection", () =>
        {
            True(!fullScreen.IsVisible && !Get<bool>(fullScreen, "_fullScreen"));
            Equal(WindowState.Normal, fullScreen.WindowState);
            Near(0, fullScreen.Opacity);
            True(!fullScreen.HasAnimatedProperties);
        });
        fullScreen.Close();

        // The app's nonactivating component mode intentionally skips presentation
        // fades. It still exercises actual mode routing and single-engine ownership.
        _main = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
        _app!.MainWindow = _main;
        _main.Show();
        await UntilAsync(() => Get<bool>(_main, "_libraryReady"), "the isolated empty app to load");
        var player = Get<AudioPlayer>(_main, "_player");
        var version = player.PlaybackVersion;
        var originalPath = player.CurrentPath;
        Invoke(_main, "ShowFocusView");
        await UntilAsync(() => Get<Window?>(_main, "_focusWindow") is { IsVisible: true }, "Focus to show");
        var focus = Get<Window>(_main, "_focusWindow");
        Conceal(focus);
        Invoke(_main, "ShowMiniView");
        await UntilAsync(() => Get<Window?>(_main, "_miniWindow") is { IsVisible: true } && !focus.IsVisible, "Mini to replace Focus");
        var mini = Get<Window>(_main, "_miniWindow");
        Conceal(mini);
        Invoke(_main, "ShowLibraryView");
        await UntilAsync(() => _main.IsVisible && !mini.IsVisible, "the library to return");
        Conceal(_main);
        Check("listening view transitions retain the main engine and create no child engine", () =>
        {
            True(ReferenceEquals(player, Get<AudioPlayer>(_main, "_player")));
            Equal(version, player.PlaybackVersion);
            Equal(originalPath, player.CurrentPath);
            foreach (var window in new[] { focus, mini })
                True(!window.GetType().GetFields(PrivateInstance).Any(field => typeof(AudioPlayer).IsAssignableFrom(field.FieldType)));
        });

        // Keep both existing child surfaces visible offscreen before enabling
        // their fades. Main remains nonactivating, so routing never calls Activate
        // or shows a window while its ShowActivated flag is true.
        foreach (var window in new[] { focus, mini })
        {
            Conceal(window);
            window.Opacity = 1;
            window.Show();
            window.ShowActivated = true;
        }
        UiMotion.Enabled = true;
        var toFocus = (Task)Invoke(_main, "SwitchListeningViewAsync", "Focus")!;
        await Task.Delay(35);
        var toMini = (Task)Invoke(_main, "SwitchListeningViewAsync", "Mini")!;
        await Task.WhenAll(toFocus, toMini).WaitAsync(TimeSpan.FromSeconds(4));
        Check("rapid real view transitions finish at the latest view without a second engine", () =>
        {
            True(mini.IsVisible && !focus.IsVisible && !_main.IsVisible);
            Equal("Mini", Get<string>(_main, "_listeningView"));
            Near(1, mini.Opacity);
            True(ReferenceEquals(player, Get<AudioPlayer>(_main, "_player")));
            Equal(version, player.PlaybackVersion);
        });
        await ((Task)Invoke(_main, "SwitchListeningViewAsync", "Library")!).WaitAsync(TimeSpan.FromSeconds(4));
        Conceal(_main);

        // Enable animation only after the existing window is shown offscreen.
        // Changing ShowActivated here does not call Activate or show it again.
        _main.ShowActivated = true;
        _main.Opacity = 1;
        _main.Left = -30000; _main.Top = -30000;
        UiMotion.Enabled = true;
        var store = Get<LibraryStore>(_main, "_store");
        var queuedWrite = (Task<bool>)Invoke(_main, "PersistLibraryChangeAsync", (Action)(() =>
        {
            Thread.Sleep(220);
            store.SavePlaylist(new CustomPlaylist { Name = "Close guard fixture", SmartRule = "favorites" });
        }), "finish the close guard fixture")!;
        var closed = ClosedTask(_main);
        var closeWatch = Stopwatch.StartNew();
        _main.Close();
        True(!closed.IsCompleted);
        var stoppingVersion = player.PlaybackVersion;
        var stoppingRequest = Get<long>(_main, "_openTrackRequest");
        var queue = Get<PlaybackQueue>(_main, "_queue");
        var stoppingEntry = queue.Current?.Id;
        foreach (var action in new[] { "PlayPause_Click", "Next_Click", "Previous_Click" })
            Invoke(_main, action, _main, new RoutedEventArgs());
        await Task.Delay(35);
        Check("playback entrypoints cannot reopen or move the queue during close fade and drain", () =>
        {
            True(Get<bool>(_main, "_closingRequested") && !closed.IsCompleted);
            Equal(stoppingVersion, player.PlaybackVersion);
            Equal(stoppingRequest, Get<long>(_main, "_openTrackRequest"));
            Equal(stoppingEntry, queue.Current?.Id);
            Equal(null, player.CurrentPath);
            True(!player.IsPlaying && !Get<bool>(_main, "_isOpeningTrack"));
            True(_main.TaskbarItemInfo.ThumbButtonInfos.All(button => !button.IsEnabled));
        });
        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        Check("the real main window waits for its close fade before Closed", () =>
        {
            True(closeWatch.Elapsed >= TimeSpan.FromMilliseconds(80));
            Near(0, _main.Opacity);
            True(!(_main.HasAnimatedProperties));
            True(queuedWrite.IsCompletedSuccessfully && queuedWrite.Result);
        });
    }

    private static async Task<Window> HostAsync(bool transparent = false, bool prepareFade = false)
    {
        var window = new Window
        {
            Width = 320, Height = 180, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
            Opacity = transparent ? 0 : 1,
            Content = new Border { Background = Brushes.WhiteSmoke, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) }
        };
        if (prepareFade)
        {
            // Prepare a cold surface before Show, then suppress activation of
            // that first Show. FadeIn is invoked only after the host is offscreen.
            window.ShowActivated = true;
            UiMotion.PrepareFadeIn(window);
            window.ShowActivated = false;
        }
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (!transparent) window.ShowActivated = true;
        return window;
    }
    private static void Conceal(Window window)
    { window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false; window.Left = -30000; window.Top = -30000; }
    private static Task ClosedTask(Window window)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => completion.TrySetResult();
        return completion.Task;
    }
    private static async Task<FocusWindow> FocusHostAsync()
    {
        var noop = () => { };
        var actions = new PlaybackActions(noop, noop, noop, noop, noop, noop, noop, noop, _ => { }, _ => { });
        var window = new FocusWindow(new NowPlayingState(), actions)
        {
            ShowActivated = false, ShowInTaskbar = false, Opacity = 1,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000
        };
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.ShowActivated = true;
        UiMotion.Enabled = true;
        return window;
    }
    private static async Task UntilAsync(Func<bool> predicate, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Timed out waiting for " + description + ".");
            await Task.Delay(15);
        }
    }
    private static T Get<T>(object target, string name) => (T)(target.GetType().GetField(name, PrivateInstance)
        ?? throw new MissingFieldException(target.GetType().Name, name)).GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] arguments) => (target.GetType().GetMethod(name, PrivateInstance)
        ?? throw new MissingMethodException(target.GetType().Name, name)).Invoke(target, arguments);
    private static void Check(string name, Action assertion) { assertion(); _passed++; Console.WriteLine("PASS " + name); }
    private static void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
    private static void Near(double expected, double actual, double tolerance = .015)
    { if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected} ± {tolerance}; actual {actual}."); }
    private static string WriteWav(string path)
    {
        const int rate = 44100, bytes = rate * 2 * 8;
        using var writer = new BinaryWriter(File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes); writer.Write(new byte[bytes]);
        return path;
    }

    private sealed class OpacitySource : INotifyPropertyChanged
    {
        private double _value;
        public double Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
