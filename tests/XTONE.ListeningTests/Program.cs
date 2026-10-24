using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using XTONE;
using XTONE.Models;
using XTONE.Services;
using XTONE.Views;
using Windows.Media.Control;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _passed;
    private static readonly List<string> Renders = [];
    private static MainWindow? _main;
    private static XTONE.App? _app;
    private static int _exitCode = 1;
    private static string? _nativeUnavailableReason;

    [STAThread]
    private static int Main()
    {
        var originalRoot = Environment.GetEnvironmentVariable("XTONE_DATA_DIR");
        var fixtureParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
        var fixture = Path.Combine(fixtureParent, Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(fixture, "data");
        Directory.CreateDirectory(fixture);
        try
        {
            var project = FindProjectRoot();
            var renderRoot = Environment.GetEnvironmentVariable("XTONE_RENDER_DIR");
            renderRoot = string.IsNullOrWhiteSpace(renderRoot) ? Path.Combine(AppContext.BaseDirectory, "renders") : Path.GetFullPath(renderRoot);
            Directory.CreateDirectory(renderRoot);
            var music = Path.Combine(fixture, "music");
            Directory.CreateDirectory(music);
            var audio = CreateWav(Path.Combine(music, "glasshouse.wav"));
            using (var tagged = TagLib.File.Create(audio))
            {
                tagged.Tag.Title = "Harness Glasshouse";
                tagged.Tag.Performers = ["Mira Vale"];
                tagged.Tag.AlbumArtists = ["Mira Vale"];
                tagged.Tag.Album = "Blue Hour";
                tagged.Tag.Track = 1;
                tagged.Tag.Disc = 1;
                var logo = Path.Combine(project, "src", "XTONE", "Assets", "xtone-logo.png");
                if (File.Exists(logo)) tagged.Tag.Pictures = [new TagLib.Picture(logo)];
                tagged.Save();
            }
            var seeded = new LibraryStore(dataRoot);
            seeded.ImportAsync([music]).GetAwaiter().GetResult();
            seeded.SaveSession(new PlaybackSession
            {
                QueuePaths = [audio], CurrentIndex = 0, PositionSeconds = .75,
                Volume = 0, RepeatMode = "Off", LastSection = "Albums"
            });
            Environment.SetEnvironmentVariable("XTONE_DATA_DIR", dataRoot);

            _app = new XTONE.App();
            // Load the actual app dictionary directly. Calling its generated
            // InitializeComponent also schedules StartupUri's extra MainWindow.
            _app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/XTONE;component/Themes/DesignSystem.xaml", UriKind.Relative) });
            _app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // These are windows created by this component harness, not external apps.
            // Suppress their actual presentation while preserving real WPF layout.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => { if (sender is Window window) Conceal(window); }));
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
                try
                {
                    await RunChecksAsync(dataRoot, audio, renderRoot);
                    Console.WriteLine($"PASS: {_passed} listening integration checks.");
                    foreach (var path in Renders) Console.WriteLine("Render: " + path);
                    _exitCode = _nativeUnavailableReason is null ? 0 : 2;
                    if (_nativeUnavailableReason is not null)
                        Console.WriteLine("UNAVAILABLE native Windows media controls: " + _nativeUnavailableReason + " Native functionality was not verified; exit2.");
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    _exitCode = 1;
                }
                finally
                {
                    if (_main is not null && !Get<bool>(_main, "_allowClose"))
                    {
                        try
                        {
                            var closed = ClosedTask(_main);
                            _main.Close();
                            await closed.WaitAsync(TimeSpan.FromSeconds(15));
                        }
                        catch (Exception error) { Console.Error.WriteLine("Cleanup: " + error.Message); _exitCode = 1; }
                    }
                    _app.Shutdown(_exitCode);
                    // This harness owns Dispatcher.Run rather than Application.Run.
                    // Explicitly end that pump after the app finishes disposing.
                    _app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            }));
            Dispatcher.Run();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            _exitCode = 1;
            _app?.Shutdown(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XTONE_DATA_DIR", originalRoot);
            var resolved = Path.GetFullPath(fixture);
            if (resolved.StartsWith(fixtureParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(resolved, true); }
                catch (IOException error) { Console.Error.WriteLine("Fixture cleanup: " + error.Message); }
            }
        }
        return _exitCode;
    }

    private static async Task RunChecksAsync(string dataRoot, string audio, string renderRoot)
    {
        _main = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
        _app!.MainWindow = _main;
        _main.Show();
        await UntilAsync(() => Get<bool>(_main, "_libraryReady") && !Get<bool>(_main, "_isOpeningTrack")
            && Get<CancellationTokenSource?>(_main, "_scanCancellation") is null, "the library and paused player to load");
        var player = Get<AudioPlayer>(_main, "_player");
        var state = Get<NowPlayingState>(_main, "_listeningState");
        var originalVersion = player.PlaybackVersion;
        var originalPosition = player.Position;
        Check("loads the actual app with isolated metadata and a muted paused session", () =>
        {
            Equal(audio, player.CurrentPath);
            Equal(false, player.IsPlaying);
            Equal(0d, state.Volume);
            Equal("Harness Glasshouse", state.Title);
            Equal("Mira Vale", state.Artist);
            Equal("Blue Hour", state.Album);
            True(state.Quality.Contains("WAV") && state.Quality.Contains("Lossless"));
            True(Math.Abs(originalPosition.TotalSeconds - .75) < .15);
        });
        await RenderAsync(_main, 1060, 700, Path.Combine(renderRoot, "library-1060x700.png"));

        Invoke(_main, "ShowFocusView");
        var focus = Get<FocusWindow>(_main, "_focusWindow");
        Conceal(focus);
        await LayoutAsync(focus);
        Check("focus shows the shared paused track without replacing the audio engine", () =>
        {
            True(!_main.IsVisible && focus.IsVisible);
            True(ReferenceEquals(state, focus.DataContext));
            Equal("Harness Glasshouse", ((TextBlock)focus.FindName("TrackTitle")).Text);
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        await RenderAsync(focus, 1000, 800, Path.Combine(renderRoot, "focus-1000x800.png"));
        await RenderAsync(focus, 640, 600, Path.Combine(renderRoot, "focus-640x600.png"));
        var previousBounds = new Rect(focus.Left, focus.Top, focus.Width, focus.Height);
        var previousChrome = WindowChrome.GetWindowChrome(focus);
        var previousResizeMode = focus.ResizeMode;
        Invoke(focus, "ToggleFullScreen");
        await LayoutAsync(focus);
        Check("focus enters native maximized fullscreen without window chrome", () =>
        {
            Equal(WindowState.Maximized, focus.WindowState);
            True(WindowChrome.GetWindowChrome(focus) is null);
            True(Get<bool>(focus, "_fullScreen"));
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        focus.ExitFullScreen();
        await LayoutAsync(focus);
        Check("focus exits fullscreen with original chrome and window bounds restored", () =>
        {
            Equal(WindowState.Normal, focus.WindowState);
            Equal(previousResizeMode, focus.ResizeMode);
            True(ReferenceEquals(previousChrome, WindowChrome.GetWindowChrome(focus)));
            True(!Get<bool>(focus, "_fullScreen"));
            Near(previousBounds.Left, focus.Left); Near(previousBounds.Top, focus.Top);
            Near(previousBounds.Width, focus.Width); Near(previousBounds.Height, focus.Height);
        });

        Invoke(_main, "ShowMiniView");
        var mini = Get<MiniPlayerWindow>(_main, "_miniWindow");
        Conceal(mini);
        await LayoutAsync(mini);
        Check("mini hides focus and uses the same metadata and engine", () =>
        {
            True(mini.IsVisible && !focus.IsVisible && !_main.IsVisible);
            True(ReferenceEquals(state, mini.DataContext));
            True(ReferenceEquals(player, Get<AudioPlayer>(_main, "_player")));
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        await RenderAsync(mini, 440, 180, Path.Combine(renderRoot, "mini-440x180.png"));
        await RenderAsync(mini, 360, 160, Path.Combine(renderRoot, "mini-360x160.png"));
        var miniSeek = (Slider)mini.FindName("PositionSlider");
        var seekPeer = UIElementAutomationPeer.CreatePeerForElement(miniSeek) ?? throw new Exception("Mini seek has no automation peer.");
        var range = seekPeer.GetPattern(PatternInterface.RangeValue) as IRangeValueProvider
            ?? throw new Exception("Mini seek does not expose its own RangeValue pattern.");
        range.SetValue(4.25);
        await UntilAsync(() => Math.Abs(player.Position.TotalSeconds - 4.25) < .15, "the component RangeValue seek to reach the paused audio engine");
        Check("mini RangeValue seek reaches the existing engine while playback stays paused", () =>
        {
            True(ReferenceEquals(player, Get<AudioPlayer>(_main, "_player")));
            Equal(audio, player.CurrentPath);
            Equal(false, player.IsPlaying);
            Near(4.25, player.Position.TotalSeconds, .15);
            Near(4.25, miniSeek.Value, .15);
        });
        // A real seek intentionally flushes/reopens the device session, then view
        // changes must preserve that new version and paused position.
        originalVersion = player.PlaybackVersion;
        originalPosition = player.Position;
        await VerifyNativeMediaControlsAsync(mini, player, state, range);
        originalVersion = player.PlaybackVersion;
        originalPosition = player.Position;

        var pin = Descendants(mini).OfType<ToggleButton>().Single(control => AutomationProperties.GetName(control) == "Always on top");
        pin.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        await Get<Task>(_main, "_sessionSaveTask");
        Check("mini pin updates shared state and survives a session reload", () =>
        {
            True(state.MiniAlwaysOnTop && mini.Topmost);
            True(new LibraryStore(dataRoot).GetSession().MiniAlwaysOnTop);
        });

        Invoke(_main, "ShowFocusView");
        Conceal(focus);
        Invoke(focus, "Volume_AutomationValueCommitted", focus, new SliderCommittedEventArgs(.23));
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Check("focus volume commits synchronize through the existing main slider", () =>
        {
            Equal(.23, state.Volume);
            Equal(.23, ((Slider)_main.FindName("VolumeSlider")).Value);
            Equal(.23, ((Slider)focus.FindName("VolumeSlider")).Value);
            Equal(false, player.IsPlaying);
        });
        Invoke(focus, "Volume_AutomationValueCommitted", focus, new SliderCommittedEventArgs(0));
        state.Visualization = "Bars";
        await Get<Task>(_main, "_sessionSaveTask");
        Check("focus visualization preference survives a session reload", () => Equal("Bars", new LibraryStore(dataRoot).GetSession().FocusVisualization));

        Invoke(focus, "Library_Click", focus, new RoutedEventArgs());
        Conceal(_main);
        Check("focus Library action restores the main window", () => True(_main.IsVisible && !focus.IsVisible && !mini.IsVisible));
        Invoke(_main, "ShowFocusView");
        focus.Close();
        Conceal(_main);
        Check("closing focus restores the main window without stopping playback", () =>
        {
            True(_main.IsVisible && Get<FocusWindow?>(_main, "_focusWindow") is null);
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        Invoke(_main, "ShowMiniView");
        mini.Close();
        Conceal(_main);
        Check("closing mini restores the main window without stopping playback", () =>
        {
            True(_main.IsVisible && Get<MiniPlayerWindow?>(_main, "_miniWindow") is null);
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });

        var store = Get<LibraryStore>(_main, "_store");
        var firstWritten = false;
        var secondWritten = false;
        var first = (Task<bool>)Invoke(_main, "PersistLibraryChangeAsync", (Action)(() =>
        {
            Thread.Sleep(180);
            store.SavePlaylist(new CustomPlaylist { Id = "close-first", Name = "First pending write", SmartRule = "favorites" });
            firstWritten = true;
        }), "save the first component fixture")!;
        var second = (Task<bool>)Invoke(_main, "PersistLibraryChangeAsync", (Action)(() =>
        {
            Thread.Sleep(80);
            store.SavePlaylist(new CustomPlaylist { Id = "close-second", Name = "Second pending write", SmartRule = "favorites" });
            secondWritten = true;
        }), "save the second component fixture")!;
        var windowClosed = ClosedTask(_main);
        _main.Close();
        True(!windowClosed.IsCompleted);
        await windowClosed.WaitAsync(TimeSpan.FromSeconds(15));
        Check("closing drains active and queued CRUD writes before the actual Closed event", () =>
        {
            True(first.IsCompletedSuccessfully && first.Result && firstWritten);
            True(second.IsCompletedSuccessfully && second.Result && secondWritten);
            var persisted = new LibraryStore(dataRoot).Load();
            True(persisted.Playlists.Any(playlist => playlist.Id == "close-first"));
            True(persisted.Playlists.Any(playlist => playlist.Id == "close-second"));
            True(persisted.Session.MiniAlwaysOnTop);
            Equal("Bars", persisted.Session.FocusVisualization);
            Equal(0d, persisted.Session.Volume);
        });
    }

    private static void AssertPlayerUnchanged(AudioPlayer expected, string path, long version, TimeSpan position)
    {
        True(ReferenceEquals(expected, Get<AudioPlayer>(_main!, "_player")));
        Equal(path, expected.CurrentPath);
        Equal(version, expected.PlaybackVersion);
        Equal(false, expected.IsPlaying);
        True(Math.Abs(expected.Position.TotalSeconds - position.TotalSeconds) < .15);
    }

    private static async Task VerifyNativeMediaControlsAsync(MiniPlayerWindow mini, AudioPlayer player,
        NowPlayingState state, IRangeValueProvider seek)
    {
        var native = Get<WindowsMediaSession?>(_main!, "_mediaSession");
        Console.WriteLine("SMTC own component UnavailableReason before Play: " + (native?.UnavailableReason ?? "(none recorded)"));
        Equal(0d, state.Volume);
        seek.SetValue(.5);
        var actions = Get<PlaybackActions>(mini, "_actions");
        actions.PlayPause();
        await UntilAsync(() => player.IsPlaying, "the harness-owned muted Mini player to start");
        GlobalSystemMediaTransportControlsSessionManager manager;
        try { manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (UnauthorizedAccessException error)
        {
            if (player.IsPlaying) actions.PlayPause();
            _nativeUnavailableReason = "Session manager access was denied. Own component: "
                + (native?.UnavailableReason ?? "no component initialization error recorded") + " Manager: " + error.Message;
            Console.WriteLine("SMTC platform unavailable; no native controls sent.");
            return;
        }
        GlobalSystemMediaTransportControlsSession? own = null;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5) && own is null)
        {
            foreach (var candidate in manager.GetSessions())
            {
                // Never request media metadata or send commands for other apps.
                if (!candidate.SourceAppUserModelId.Contains("XTONE", StringComparison.OrdinalIgnoreCase)) continue;
                var metadata = await candidate.TryGetMediaPropertiesAsync();
                if (metadata.Title == "Harness Glasshouse" && metadata.Artist == "Mira Vale")
                { own = candidate; break; }
            }
            if (own is null) await Task.Delay(100);
        }
        if (own is null)
        {
            if (player.IsPlaying) actions.PlayPause();
            var reason = native?.UnavailableReason ?? "No initialization or update error was recorded.";
            Console.WriteLine("SMTC own component UnavailableReason after Play: " + reason);
            if (reason.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            {
                _nativeUnavailableReason = "The own component was denied SMTC access. " + reason;
                Console.WriteLine("SMTC platform unavailable; no native controls sent.");
                return;
            }
            throw new InvalidOperationException("Native SMTC session for exact harness metadata was not published while Mini played muted audio. No native controls were sent. " + reason);
        }
        Console.WriteLine("SMTC matched harness source: " + own.SourceAppUserModelId);
        True(await own.TryPauseAsync());
        await UntilAsync(() => !player.IsPlaying, "native SMTC Pause to reach the harness engine");
        True(await own.TryPlayAsync());
        await UntilAsync(() => player.IsPlaying, "native SMTC Play to reach the harness engine");
        True(await own.TryPauseAsync());
        await UntilAsync(() => !player.IsPlaying, "the final native SMTC Pause to reach the harness engine");
        Check("native Windows media Pause Play Pause controls reach only the exact muted Mini fixture", () =>
        {
            True(ReferenceEquals(player, Get<AudioPlayer>(_main!, "_player")));
            True(mini.IsVisible && !_main!.IsVisible);
            Equal(false, player.IsPlaying);
            Equal(0d, state.Volume);
        });
    }

    private static async Task RenderAsync(Window window, double width, double height, string path)
    {
        var area = SystemParameters.WorkArea;
        if (area.Width < width || area.Height < height)
        {
            Console.WriteLine($"SKIP render {width}×{height}: work area is {area.Width}×{area.Height} DIPs.");
            return;
        }
        window.WindowState = WindowState.Normal;
        window.Width = width; window.Height = height;
        Conceal(window);
        await LayoutAsync(window);
        // FitContent may ease artwork geometry after a resize. Capture its
        // settled layout instead of an intermediate animation frame.
        await Task.Delay(240);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        True(content.ActualWidth > 0 && content.ActualHeight > 0);
        True(content.ActualWidth <= width + 2 && content.ActualHeight <= height + 2);
        var requiredControls = window switch
        {
            FocusWindow => new[] { "TrackTitle", "ArtworkHost", "PlayPauseButton", "SeekSlider", "VolumeSlider" },
            MiniPlayerWindow => new[] { "PlayPauseButton", "PositionSlider", "ElapsedText" },
            _ => new[] { "PageTitle", "PositionSlider", "VolumeSlider", "NowPlayingTitle" }
        };
        Check($"{window.GetType().Name} keeps required controls inside {width}×{height} layout", () =>
        {
            foreach (var name in requiredControls)
            {
                var control = window.FindName(name) as FrameworkElement ?? throw new Exception($"Missing control {name}.");
                True(control.ActualWidth > 0 && control.ActualHeight > 0);
                var bounds = control.TransformToAncestor(content).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                if (bounds.Left < -2 || bounds.Top < -2 || bounds.Right > content.ActualWidth + 2 || bounds.Bottom > content.ActualHeight + 2)
                    throw new Exception($"{name} overflows {width}×{height}: {bounds}.");
            }
        });
        var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        using var output = File.Create(path);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        png.Save(output);
        Renders.Add(path);
        Console.WriteLine($"LAYOUT {Path.GetFileName(path)}: {content.ActualWidth:0.#}×{content.ActualHeight:0.#} DIPs, {bitmap.PixelWidth}×{bitmap.PixelHeight} pixels");
    }

    private static async Task LayoutAsync(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
    private static void Conceal(Window window)
    {
        window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Left = -30000; window.Top = -30000;
    }
    private static async Task UntilAsync(Func<bool> predicate, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(25))
            {
                var status = _main?.FindName("ScanStatus") as TextBlock;
                throw new TimeoutException($"Timed out waiting for {description}. Status: {status?.Text}");
            }
            await Task.Delay(20);
        }
    }
    private static Task ClosedTask(Window window)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => completion.TrySetResult();
        return completion.Task;
    }
    private static T Get<T>(object target, string name) => (T)(target.GetType().GetField(name, PrivateInstance)
        ?? throw new MissingFieldException(target.GetType().Name, name)).GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, PrivateInstance) ?? throw new MissingMethodException(target.GetType().Name, name);
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "XTONE", "XTONE.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not find the XTONE source project.");
    }
    private static string CreateWav(string path)
    {
        const int sampleRate = 44100, channels = 2, bits = 16, seconds = 8;
        const int dataBytes = sampleRate * channels * bits / 8 * seconds;
        using var writer = new BinaryWriter(File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)channels); writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bits / 8); writer.Write((ushort)(channels * bits / 8)); writer.Write((ushort)bits);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        return path;
    }
    private static void Check(string name, Action assertions)
    { assertions(); _passed++; Console.WriteLine("PASS " + name); }
    private static void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Near(double expected, double actual, double tolerance = 1)
    { if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected} ± {tolerance}; actual {actual}."); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
}
