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
using XTONE.Dialogs;
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
            var album = Path.Combine(music, "Folder album");
            Directory.CreateDirectory(album);
            var audio = CreateWav(Path.Combine(album, "glasshouse.wav"));
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
            CreateWav(Path.Combine(music, "Loose track.wav"));
            var seeded = new LibraryStore(dataRoot);
            seeded.ConfigureMusicFolderAsync(music).GetAwaiter().GetResult();
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

    private static async Task CheckPlaybackReliabilityAsync(string dataRoot, string audio, AudioPlayer player, PlaybackQueue queue)
    {
        var actual = Get<List<XTONE.Models.Track>>(_main!, "_tracks").Single(track => track.Path == audio);
        True((bool)Invoke(_main!, "TrySeek", TimeSpan.FromSeconds(1.4))!);
        Invoke(_main!, "StopCurrentPlayback"); Invoke(_main!, "UpdateNowPlaying");
        Invoke(_main!, "SavePlaybackSession", false); await Get<Task>(_main!, "_sessionSaveTask");
        Check("releasing a decoder retains the paused session position instead of saving zero", () =>
        {
            True(player.CurrentPath is null); Near(1.4, new LibraryStore(dataRoot).GetSession().PositionSeconds, .2);
            Near(1.4, Get<NowPlayingState>(_main!, "_listeningState").PositionSeconds, .2);
        });
        Invoke(_main!, "PlayPause_Click", _main, new RoutedEventArgs());
        await UntilAsync(() => !Get<bool>(_main!, "_isOpeningTrack") && player.CurrentPath == audio, "released playback to reopen");
        Check("play resumes a released decoder near the retained position", () => { True(player.IsPlaying); Near(1.4, player.Position.TotalSeconds, .5); });
        var priorPump = Get<SessionWritePump?>(_main!, "_sessionWritePump");
        using (var releaseSave = new ManualResetEventSlim())
        {
            var blockedPump = new SessionWritePump(_ => releaseSave.Wait(), _ => { });
            typeof(MainWindow).GetField("_sessionWritePump", PrivateInstance)!.SetValue(_main, blockedPump);
            var snapshot = new LibraryStore(dataRoot).Load();
            var versionBeforeOperation = player.PlaybackVersion;
            var operation = (Task<bool>)Invoke(_main!, "RunMusicOperationCoreAsync",
                new Func<IProgress<LibraryProgress>, CancellationToken, Task<LibrarySnapshot>>((_, _) => Task.FromResult(snapshot)),
                "Checking decoder release…", true)!;
            try
            {
                Check("file operations invalidate playback before awaiting a blocked session save", () =>
                {
                    True(!operation.IsCompleted); True(player.CurrentPath is null); True(!player.IsPlaying);
                    True(player.PlaybackVersion != versionBeforeOperation); True(Get<bool>(_main!, "_fileOperationInProgress"));
                });
            }
            finally { releaseSave.Set(); }
            True(await operation);
            await Get<Task>(_main!, "_sessionSaveTask");
            typeof(MainWindow).GetField("_sessionWritePump", PrivateInstance)!.SetValue(_main, priorPump);
        }
        Invoke(_main!, "PauseCurrentPlayback");
        True((bool)Invoke(_main!, "TrySeek", actual.Duration)!);
        await (Task)Invoke(_main!, "AdvanceQueueAsync", false)!;
        Invoke(_main!, "PlayPause_Click", _main, new RoutedEventArgs());
        await UntilAsync(() => !Get<bool>(_main!, "_isOpeningTrack") && player.CurrentPath == audio, "end-of-queue restart");
        Check("play at the end of a finished queue restarts the track rather than reopening at EOF", () => { True(player.IsPlaying); True(player.Position.TotalSeconds < .75); });
        Invoke(_main!, "PauseCurrentPlayback");
        var missing = new XTONE.Models.Track { Path = Path.Combine(Path.GetDirectoryName(audio)!, "not-present.wav"), Title = "Unavailable fixture", IsMissing = true, Duration = TimeSpan.FromSeconds(8) };
        Invoke(_main!, "StopCurrentPlayback"); queue.Replace([missing, actual]); queue.RepeatMode = QueueRepeatMode.One;
        await (Task)Invoke(_main!, "AdvanceQueueAsync", false)!;
        Check("repeat one skips an unavailable queued entry and reaches the next playable track", () => { Equal(audio, player.CurrentPath); Equal(actual.Id, queue.Current!.Track.Id); True(player.IsPlaying); });
        Invoke(_main!, "PauseCurrentPlayback"); Invoke(_main!, "StopCurrentPlayback");
        queue.Replace([missing, missing with { Path = missing.Path + ".other" }]); queue.RepeatMode = QueueRepeatMode.All;
        var stopwatch = Stopwatch.StartNew(); await (Task)Invoke(_main!, "AdvanceQueueAsync", false)!;
        Check("an entirely unavailable repeating queue terminates without leaving an open request", () =>
        {
            True(stopwatch.Elapsed < TimeSpan.FromSeconds(3)); True(!Get<bool>(_main!, "_isOpeningTrack")); True(player.CurrentPath is null);
            True(((TextBlock)_main!.FindName("ScanStatus")).Text.Contains("No available tracks"));
        });
        queue.Replace(Enumerable.Range(0, 25000).Select(index => missing with { Path = missing.Path + index }));
        queue.RepeatMode = QueueRepeatMode.All;
        var interrupted = false;
        var skipping = (Task)Invoke(_main!, "AdvanceQueueAsync", false)!;
        _ = _main!.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        { interrupted = true; Invoke(_main!, "PauseCurrentPlayback"); }));
        await skipping;
        Check("a 25000-entry unavailable queue yields to Pause and cancels stale traversal", () =>
        {
            True(interrupted); True(!Get<bool>(_main!, "_isOpeningTrack"));
            True(queue.CurrentIndex < 100); True(player.CurrentPath is null);
        });
        queue.RepeatMode = QueueRepeatMode.Off; queue.Replace([actual]);
        await (Task)Invoke(_main!, "OpenCurrentTrackAsync", false, TimeSpan.FromSeconds(.75), false)!;
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
            True(_main.Icon is not null && _main.Icon.Width > 0 && _main.Icon.Height > 0);
            Equal(audio, player.CurrentPath);
            Equal(false, player.IsPlaying);
            Equal(0d, state.Volume);
            Equal("Harness Glasshouse", state.Title);
            Equal("Mira Vale", state.Artist);
            Equal("Blue Hour", state.Album);
            True(state.Quality.Contains("WAV") && state.Quality.Contains("Lossless"));
            True(Math.Abs(originalPosition.TotalSeconds - .75) < .15);
        });
        await CheckButtonFocusAsync(_main, "main transport and toolbar", Descendants(_main).OfType<Button>()
            .Where(button => AutomationProperties.GetName(button) is "Next track" or "Previous track" or "Search library")
            .Concat(new[] { (Button)_main.FindName("ShuffleButton"), (Button)_main.FindName("RepeatButton"), (Button)_main.FindName("PlayPauseButton") }));
        var folderDialog = new MusicFolderNameWindow(false, "Focus fixture") { Owner = _main, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        Conceal(folderDialog); folderDialog.Show();
        await LayoutAsync(folderDialog);
        await CheckButtonFocusAsync(folderDialog, "folder dialog primary action", Descendants(folderDialog).OfType<Button>().Where(button => Equals(button.Content, "Create folder")));
        folderDialog.Close();
        Check("uses the configured folder album rather than the embedded album tag", () =>
        {
            Equal(Path.GetDirectoryName(Path.GetDirectoryName(audio)), Get<string?>(_main, "_musicRoot"));
            var albums = Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main, "_albumCards");
            Equal(1, albums.Count);
            Equal("Folder album", albums.Single().Title);
            Equal(Path.GetDirectoryName(audio), albums.Single().FolderPath);
        });
        var browserQueue = Get<PlaybackQueue>(_main, "_queue");
        var initialQueueIdentities = browserQueue.Entries.Select(entry => entry.Id).ToArray();
        Check("Music presents a real folder and a loose track as different item kinds", () =>
        {
            var items = Get<System.Collections.ObjectModel.ObservableCollection<MusicBrowserItem>>(_main, "_musicItems");
            Equal(2, items.Count);
            Equal(1, items.Count(item => item.IsFolder && !item.IsTrack));
            var looseItem = items.Single(item => item.IsTrack);
            True(looseItem.Folder is null && looseItem.Track!.Path.EndsWith("Loose track.wav", StringComparison.OrdinalIgnoreCase));
            Equal(1, Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main, "_albumCards").Count);
        });
        Invoke(_main, "SetMusicView", "List");
        await LayoutAsync(_main);
        await Get<Task>(_main, "_sessionSaveTask");
        Check("Music list shows the same folders and loose tracks without changing playback", () =>
        {
            Equal(Visibility.Visible, ((FrameworkElement)_main.FindName("MusicList")).Visibility);
            Equal(Visibility.Collapsed, ((FrameworkElement)_main.FindName("AlbumItems")).Visibility);
            var list = (ListBox)_main.FindName("MusicList");
            Equal(2, list.Items.Count);
            Equal(1, list.Items.OfType<MusicBrowserItem>().Count(item => item.IsTrack));
            True(browserQueue.Entries.Select(entry => entry.Id).SequenceEqual(initialQueueIdentities));
            Equal("List", new LibraryStore(dataRoot).GetSession().MusicView);
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        await CheckMusicFocusAsync(_main, "MusicList", "Chrome", "list row");
        Check("Music list keeps folder and track glyphs centered with consistent horizontal insets", () =>
        {
            var buttons = Descendants((ListBox)_main.FindName("MusicList")).OfType<Button>().Where(button => button.Tag is MusicBrowserItem).ToArray();
            Equal(2, buttons.Length);
            foreach (var button in buttons)
            {
                var content = (Grid)button.Content;
                var glyph = content.Children.OfType<System.Windows.Shapes.Path>().Single();
                var glyphBounds = glyph.TransformToAncestor(button).TransformBounds(new Rect(glyph.RenderSize));
                var contentBounds = content.TransformToAncestor(button).TransformBounds(new Rect(content.RenderSize));
                True(glyphBounds.Left >= 12);
                Near(contentBounds.Left + 16, glyphBounds.Left + glyphBounds.Width / 2, .75);
                True(button.Padding.Left >= 12 && button.Padding.Right >= 12);
                if (button.Tag is MusicBrowserItem { IsTrack: true })
                {
                    var duration = content.Children.OfType<TextBlock>().Single();
                    var durationBounds = duration.TransformToAncestor(button).TransformBounds(new Rect(duration.RenderSize));
                    True(duration.ActualWidth > 0 && durationBounds.Right <= button.ActualWidth - 12);
                }
            }
        });
        Invoke(_main, "SetMusicView", "Grid");
        await LayoutAsync(_main);
        Check("returning to Music grid keeps the mixed collection and current queue intact", () =>
        {
            Equal(Visibility.Collapsed, ((FrameworkElement)_main.FindName("MusicList")).Visibility);
            Equal(Visibility.Visible, ((FrameworkElement)_main.FindName("AlbumItems")).Visibility);
            Equal(2, Get<System.Collections.ObjectModel.ObservableCollection<MusicBrowserItem>>(_main, "_musicItems").Count);
            True(browserQueue.Entries.Select(entry => entry.Id).SequenceEqual(initialQueueIdentities));
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        await CheckMusicFocusAsync(_main, "AlbumItems", "Card", "grid card");

        var pinFolder = Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main, "_albumCards").Single();
        var pinBrowserItems = Get<System.Collections.ObjectModel.ObservableCollection<MusicBrowserItem>>(_main, "_musicItems");
        Check("only physical Music folders are accepted as sidebar drag candidates", () =>
        {
            var mouse = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left);
            Invoke(_main, "MusicFolder_MouseDown", new Button { Tag = pinBrowserItems.Single(item => item.IsFolder) }, mouse);
            True(ReferenceEquals(pinFolder, Get<AlbumCard?>(_main, "_folderDragCandidate")));
            Invoke(_main, "MusicFolder_MouseDown", new Button { Tag = pinBrowserItems.Single(item => item.IsTrack) }, mouse);
            True(Get<AlbumCard?>(_main, "_folderDragCandidate") is null);
            True(Invoke(_main, "DroppedFolder", new DataObject("XTONE.MusicFolder", pinFolder.FolderPath)) is AlbumCard);
            True(Invoke(_main, "DroppedFolder", new DataObject("XTONE.MusicFolder", pinBrowserItems.Single(item => item.IsTrack).Track!.Path)) is null);
            True(Invoke(_main, "DroppedFolder", new DataObject(DataFormats.FileDrop, new[] { pinFolder.FolderPath! })) is null);
        });
        await (Task)Invoke(_main, "PinFolderAsync", pinFolder, true)!;
        await LayoutAsync(_main);
        Check("pinning a folder creates a named sidebar shortcut with a folder icon and saved path", () =>
        {
            Equal(Visibility.Visible, ((FrameworkElement)_main.FindName("PinnedFoldersPanel")).Visibility);
            var pinnedItems = (ItemsControl)_main.FindName("PinnedFolderItems");
            Equal(1, pinnedItems.Items.Count);
            var shortcut = Descendants(pinnedItems).OfType<Button>().Single(button => button.Tag is AlbumCard);
            Equal(pinFolder.Title, AutomationProperties.GetName(shortcut));
            True(Descendants(shortcut).OfType<TextBlock>().Any(label => label.Text == pinFolder.Title));
            True(Descendants(shortcut).OfType<System.Windows.Shapes.Path>().Any(icon => icon.Data is not null && icon.ActualWidth > 0));
            True(new LibraryStore(dataRoot).Load().PinnedFolderPaths.Contains(pinFolder.FolderPath!, StringComparer.OrdinalIgnoreCase));
            True(browserQueue.Entries.Select(entry => entry.Id).SequenceEqual(initialQueueIdentities));
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        var reopenedPins = new LibraryStore(dataRoot).Load() with { Revision = Get<long>(_main, "_lastSnapshotRevision") };
        Get<List<string>>(_main, "_pinnedFolderPaths").Clear();
        Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main, "_pinnedFolderCards").Clear();
        await (Task)Invoke(_main, "ApplySnapshotAsync", reopenedPins)!;
        await LayoutAsync(_main);
        Check("a reopened saved library rebuilds its folder shortcut", () =>
        {
            var pin = Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main, "_pinnedFolderCards").Single();
            Equal(pinFolder.FolderPath, pin.FolderPath);
            Equal(pinFolder.Title, pin.Title);
            Equal(Visibility.Visible, ((FrameworkElement)_main.FindName("PinnedFoldersPanel")).Visibility);
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        await (Task)Invoke(_main, "PinFolderAsync", pinFolder, false)!;
        Check("unpinning removes the shortcut while retaining the folder and current playback", () =>
        {
            Equal(0, Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main, "_pinnedFolderCards").Count);
            Equal(0, new LibraryStore(dataRoot).Load().PinnedFolderPaths.Count);
            True(Directory.Exists(pinFolder.FolderPath));
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        await RenderAsync(_main, 1060, 700, Path.Combine(renderRoot, "library-1060x700.png"));

        var fixtureStore = Get<LibraryStore>(_main, "_store");
        var configuredRoot = Get<string>(_main, "_musicRoot");
        var looseRoot = Path.Combine(Path.GetDirectoryName(dataRoot)!, "loose-music");
        Directory.CreateDirectory(looseRoot);
        CreateWav(Path.Combine(looseRoot, "loose.wav"));
        var looseSnapshot = await fixtureStore.ConfigureMusicFolderAsync(looseRoot);
        await (Task)Invoke(_main, "ApplySnapshotAsync", looseSnapshot)!;
        Invoke(_main, "ShowSection", "Albums");
        await LayoutAsync(_main);
        Check("a loose-only root is visible in Music without manufacturing a folder or showing an empty page", () =>
        {
            Equal(1, Get<List<XTONE.Models.Track>>(_main, "_tracks").Count);
            Equal(0, Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main, "_albumCards").Count);
            var item = Get<System.Collections.ObjectModel.ObservableCollection<MusicBrowserItem>>(_main, "_musicItems").Single();
            True(item.IsTrack && item.Folder is null);
            Equal(Visibility.Collapsed, ((FrameworkElement)_main.FindName("EmptyState")).Visibility);
            Equal(1, ((ListBox)_main.FindName("MusicList")).Items.Count);
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        var restoredSnapshot = await fixtureStore.ConfigureMusicFolderAsync(configuredRoot);
        await (Task)Invoke(_main, "ApplySnapshotAsync", restoredSnapshot)!;
        Invoke(_main, "ShowSection", "Albums");

        var freshFolder = fixtureStore.CreateMusicFolder("Fresh album");
        var freshSnapshot = await fixtureStore.RefreshAsync();
        await (Task)Invoke(_main, "ApplySnapshotAsync", freshSnapshot)!;
        var freshCard = Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main, "_albumCards")
            .Single(folder => string.Equals(folder.FolderPath, freshFolder, StringComparison.OrdinalIgnoreCase));
        Invoke(_main, "OpenAlbum", freshCard);
        await LayoutAsync(_main);
        Check("a newly created physical folder opens empty and offers Add tracks", () =>
        {
            True(Directory.Exists(freshFolder));
            Equal(0, freshCard.Tracks.Count);
            Equal(Visibility.Visible, ((FrameworkElement)_main.FindName("DetailEmptyState")).Visibility);
            Equal(Visibility.Visible, ((FrameworkElement)_main.FindName("AddFolderTracksButton")).Visibility);
            Equal(Visibility.Visible, ((FrameworkElement)_main.FindName("OpenAlbumFolderButton")).Visibility);
        });
        var queue = Get<PlaybackQueue>(_main, "_queue");
        var queuedIdentities = queue.Entries.Select(entry => entry.Id).ToArray();
        var movingTrackId = queue.Current!.Track.Id;
        Func<IProgress<LibraryProgress>, CancellationToken, Task<LibrarySnapshot>> moveToFresh =
            (progress, token) => fixtureStore.MoveTracksToFolderAsync([audio], freshFolder, progress, token);
        True(await (Task<bool>)Invoke(_main, "RunMusicOperationAsync", moveToFresh, "Organizing fixture…", true)!);
        var movedAudio = Path.Combine(freshFolder, Path.GetFileName(audio));
        Check("organizing the loaded track releases its decoder and restores its paused queue identity", () =>
        {
            True(File.Exists(movedAudio) && !File.Exists(audio));
            Equal(movedAudio, player.CurrentPath);
            True(!player.IsPlaying && _main.IsEnabled && !Get<bool>(_main, "_fileOperationInProgress"));
            True(queue.Entries.Select(entry => entry.Id).SequenceEqual(queuedIdentities));
            Equal(movingTrackId, queue.Current!.Track.Id);
            Equal(movedAudio, queue.Current.Track.Path);
            True(Math.Abs(player.Position.TotalSeconds - originalPosition.TotalSeconds) < .15);
            True(_main.TaskbarItemInfo!.ThumbButtonInfos.All(button => button.IsEnabled));
        });
        Func<IProgress<LibraryProgress>, CancellationToken, Task<LibrarySnapshot>> moveBack =
            (progress, token) => fixtureStore.MoveTracksToFolderAsync([movedAudio], Path.GetDirectoryName(audio)!, progress, token);
        True(await (Task<bool>)Invoke(_main, "RunMusicOperationAsync", moveBack, "Restoring fixture…", true)!);
        await CheckFolderOrganizationAsync(fixtureStore, dataRoot, audio, player, queue);
        await CheckTrackDropOrganizationAsync(fixtureStore, dataRoot, audio, freshFolder, player, queue);
        originalVersion = player.PlaybackVersion;
        originalPosition = player.Position;
        Invoke(_main, "ShowSection", "Albums");

        var playableLooseItem = ((ListBox)_main.FindName("MusicList")).Items.OfType<MusicBrowserItem>().Single(item => item.IsTrack);
        Invoke(_main, "MusicItem_Click", new Button { Tag = playableLooseItem }, new RoutedEventArgs());
        await UntilAsync(() => !Get<bool>(_main, "_isOpeningTrack") && player.CurrentPath == playableLooseItem.Track!.Path,
            "the loose Music item to open through the existing player");
        Check("a loose Music item plays its real file without opening a synthetic folder", () =>
        {
            True(player.IsPlaying && state.Volume == 0);
            Equal(playableLooseItem.Track!.Path, queue.Current!.Track.Path);
            Equal(1, queue.Entries.Count);
            True(Get<AlbumCard?>(_main, "_activeAlbum") is null);
            True(ReferenceEquals(player, Get<AudioPlayer>(_main, "_player")));
        });
        Invoke(_main, "PauseCurrentPlayback");
        queue.Replace([Get<List<XTONE.Models.Track>>(_main, "_tracks").Single(track => track.Path == audio)]);
        await (Task)Invoke(_main, "OpenCurrentTrackAsync", false, originalPosition, false)!;
        originalVersion = player.PlaybackVersion;
        originalPosition = player.Position;

        await CheckPlaybackReliabilityAsync(dataRoot, audio, player, queue);
        originalVersion = player.PlaybackVersion; originalPosition = player.Position;

        Invoke(_main, "ShowFocusView");
        var focus = Get<FocusWindow>(_main, "_focusWindow");
        Conceal(focus);
        await LayoutAsync(focus);
        Check("focus shows the shared paused track without replacing the audio engine", () =>
        {
            True(focus.Icon is not null && focus.Icon.Width > 0 && focus.Icon.Height > 0);
            True(!_main.IsVisible && focus.IsVisible);
            True(ReferenceEquals(state, focus.DataContext));
            Equal("Harness Glasshouse", ((TextBlock)focus.FindName("TrackTitle")).Text);
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        await CheckButtonFocusAsync(focus, "Focus transport", Descendants(focus).OfType<Button>()
            .Where(button => AutomationProperties.GetName(button) is "Next track" or "Previous track")
            .Append((Button)focus.FindName("PlayPauseButton")));
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
            True(mini.Icon is not null && mini.Icon.Width > 0 && mini.Icon.Height > 0);
            True(mini.IsVisible && !focus.IsVisible && !_main.IsVisible);
            True(ReferenceEquals(state, mini.DataContext));
            True(ReferenceEquals(player, Get<AudioPlayer>(_main, "_player")));
            AssertPlayerUnchanged(player, audio, originalVersion, originalPosition);
        });
        await CheckButtonFocusAsync(mini, "Mini transport", Descendants(mini).OfType<Button>()
            .Where(button => AutomationProperties.GetName(button) is "Next track" or "Previous track")
            .Append((Button)mini.FindName("PlayPauseButton")));
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
        True(pin.Focus());
        await Dispatcher.Yield(DispatcherPriority.Input);
        Check("focused Mini pin retains its selected state without a persistent focus border", () =>
        {
            True(pin.IsKeyboardFocused && pin.FocusVisualStyle is not null && !pin.IsMouseOver);
            True(IsClearBrush(pin.BorderBrush));
            True(ReferenceEquals(mini.FindResource("SelectionBrush"), pin.Background));
            True(ReferenceEquals(mini.FindResource("SelectedTextBrush"), pin.Foreground));
        });
        pin.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Check("unchecking the focused Mini pin clears selection without leaving an outline", () =>
        {
            True(pin.IsKeyboardFocused && IsClearBrush(pin.BorderBrush) && IsClearBrush(pin.Background));
            True(!state.MiniAlwaysOnTop && !mini.Topmost);
        });
        pin.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        System.Windows.Input.Keyboard.ClearFocus();
        await Get<Task>(_main, "_sessionSaveTask");

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

    private static async Task CheckFolderOrganizationAsync(LibraryStore store, string dataRoot, string audio, AudioPlayer player, PlaybackQueue queue)
    {
        var root = Get<string>(_main!, "_musicRoot");
        var originalFolder = Path.GetDirectoryName(audio)!;
        var card = Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main!, "_albumCards")
            .Single(folder => folder.FolderPath == originalFolder);
        var sourceImage = Path.Combine(Path.GetDirectoryName(dataRoot)!, "chosen-artwork.png");
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 40, 60, 220, 255, 40, 60, 220, 255, 40, 60, 220, 255, 40, 60, 220, 255 }, 8);
        using (var output = File.Create(sourceImage))
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(output);
        }
        var sourceBytes = File.ReadAllBytes(sourceImage);
        var entryIds = queue.Entries.Select(entry => entry.Id).ToArray();
        var trackId = queue.Current!.Track.Id;
        var position = player.Position;
        await (Task)Invoke(_main!, "PinFolderAsync", card, true)!;
        Invoke(_main!, "OpenAlbum", card);
        True(await (Task<bool>)Invoke(_main!, "SetFolderArtworkAsync", card, sourceImage)!);
        var artSnapshot = new LibraryStore(dataRoot).Load();
        var cachedArtwork = artSnapshot.FolderArtworkPaths[originalFolder];
        Check("choosing folder artwork updates the browser and shared listening presentation without rewriting its source", () =>
        {
            True(File.Exists(cachedArtwork) && cachedArtwork != sourceImage);
            True(File.ReadAllBytes(sourceImage).SequenceEqual(sourceBytes));
            Equal(cachedArtwork, Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main!, "_albumCards").Single(folder => folder.FolderPath == originalFolder).ArtworkPath);
            Equal(cachedArtwork, ((XTONE.Controls.AlbumArtwork)_main!.FindName("NowPlayingArtwork")).SourcePath);
            Equal(cachedArtwork, Get<NowPlayingState>(_main!, "_listeningState").ArtworkPath);
            True(queue.Entries.Select(entry => entry.Id).SequenceEqual(entryIds));
            Equal(audio, player.CurrentPath); True(!player.IsPlaying);
        });
        card = Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main!, "_albumCards").Single(folder => folder.FolderPath == originalFolder);
        True(await (Task<bool>)Invoke(_main!, "RenameFolderAsync", card, "Renamed listening folder")!);
        var renamedFolder = Path.Combine(root, "Renamed listening folder");
        var renamedAudio = Path.Combine(renamedFolder, Path.GetFileName(audio));
        Check("renaming the loaded folder preserves active detail, audio position, queue identities, pinned shortcut and artwork", () =>
        {
            True(Directory.Exists(renamedFolder) && !Directory.Exists(originalFolder));
            Equal(renamedAudio, player.CurrentPath);
            Equal(trackId, queue.Current!.Track.Id);
            Equal(renamedAudio, queue.Current.Track.Path);
            True(queue.Entries.Select(entry => entry.Id).SequenceEqual(entryIds));
            True(!player.IsPlaying && Math.Abs(player.Position.TotalSeconds - position.TotalSeconds) < .15);
            Equal(renamedFolder, Get<AlbumCard?>(_main!, "_activeAlbum")?.FolderPath);
            Equal("Renamed listening folder", ((TextBlock)_main!.FindName("PageTitle")).Text);
            var shortcut = Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main!, "_pinnedFolderCards").Single();
            Equal(renamedFolder, shortcut.FolderPath); Equal("Renamed listening folder", shortcut.Title);
            var saved = new LibraryStore(dataRoot).Load();
            True(saved.PinnedFolderPaths.Contains(renamedFolder));
            Equal(cachedArtwork, saved.FolderArtworkPaths[renamedFolder]);
            True(!saved.FolderArtworkPaths.ContainsKey(originalFolder));
            Equal(cachedArtwork, Get<NowPlayingState>(_main!, "_listeningState").ArtworkPath);
            True(_main!.IsEnabled && _main.TaskbarItemInfo!.ThumbButtonInfos.All(button => button.IsEnabled));
        });
        var renamedCard = Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main!, "_albumCards").Single(folder => folder.FolderPath == renamedFolder);
        True(await (Task<bool>)Invoke(_main!, "SetFolderArtworkAsync", renamedCard, null)!);
        Check("resetting folder artwork removes the override and restores the embedded cover across listening views", () =>
        {
            True(!new LibraryStore(dataRoot).Load().FolderArtworkPaths.ContainsKey(renamedFolder));
            var expected = Get<List<XTONE.Models.Track>>(_main!, "_tracks").Single(track => track.Path == renamedAudio).ArtworkPath;
            True(expected is not null && expected != cachedArtwork);
            Equal(expected, Get<NowPlayingState>(_main!, "_listeningState").ArtworkPath);
            Equal(expected, ((XTONE.Controls.AlbumArtwork)_main!.FindName("NowPlayingArtwork")).SourcePath);
            True(File.Exists(sourceImage));
        });
        renamedCard = Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main!, "_albumCards").Single(folder => folder.FolderPath == renamedFolder);
        True(await (Task<bool>)Invoke(_main!, "RenameFolderAsync", renamedCard, Path.GetFileName(originalFolder))!);
        card = Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main!, "_albumCards").Single(folder => folder.FolderPath == originalFolder);
        await (Task)Invoke(_main!, "PinFolderAsync", card, false)!;
        Equal(audio, player.CurrentPath);
        Invoke(_main!, "ShowSection", "Albums");
    }

    private static async Task CheckTrackDropOrganizationAsync(LibraryStore store, string dataRoot, string audio, string destination, AudioPlayer player, PlaybackQueue queue)
    {
        var root = Get<string>(_main!, "_musicRoot");
        var loosePath = Path.Combine(root, "Loose track.wav");
        var loose = Get<List<XTONE.Models.Track>>(_main!, "_tracks").Single(track => track.Path == loosePath);
        var destinationCard = Get<System.Collections.ObjectModel.ObservableCollection<AlbumCard>>(_main!, "_albumCards").Single(folder => folder.FolderPath == destination);
        var dragData = (DataObject?)Invoke(_main!, "CreateTrackDragData", (object)new[] { loose });
        True(dragData is not null);
        var ids = queue.Entries.Select(entry => entry.Id).ToArray();
        var position = player.Position;
        Equal(DragDropEffects.Move, (DragDropEffects)Invoke(_main!, "GetFolderDropEffect", dragData, destinationCard)!);
        await (Task)Invoke(_main!, "DropTracksIntoFolderAsync", dragData, destinationCard)!;
        var movedPath = Path.Combine(destination, Path.GetFileName(loosePath));
        Check("dropping a current-library track into a folder moves the real file and retains playback and track identity", () =>
        {
            True(File.Exists(movedPath) && !File.Exists(loosePath));
            Equal(loose.Id, new LibraryStore(dataRoot).Load().Tracks.Single(track => track.Path == movedPath).Id);
            Equal(audio, player.CurrentPath); True(!player.IsPlaying);
            True(Math.Abs(player.Position.TotalSeconds - position.TotalSeconds) < .15);
            True(queue.Entries.Select(entry => entry.Id).SequenceEqual(ids));
        });
        var externalPath = CreateWav(Path.Combine(Path.GetDirectoryName(dataRoot)!, "External drop.wav"));
        var externalBytes = File.ReadAllBytes(externalPath);
        var externalData = new DataObject(DataFormats.FileDrop, new[] { externalPath });
        Equal(DragDropEffects.Copy, (DragDropEffects)Invoke(_main!, "GetFolderDropEffect", externalData, destinationCard)!);
        await (Task)Invoke(_main!, "DropTracksIntoFolderAsync", externalData, destinationCard)!;
        Check("dropping external audio copies it into the folder while preserving the original", () =>
        {
            True(File.Exists(Path.Combine(destination, Path.GetFileName(externalPath))));
            True(File.ReadAllBytes(externalPath).SequenceEqual(externalBytes));
            Equal(audio, player.CurrentPath); True(!player.IsPlaying);
            True(queue.Entries.Select(entry => entry.Id).SequenceEqual(ids));
        });
        var invalidPath = Path.Combine(Path.GetDirectoryName(dataRoot)!, "Unsupported.txt");
        File.WriteAllText(invalidPath, "An unsupported fixture, not an instruction.");
        var invalidData = new DataObject(DataFormats.FileDrop, new[] { invalidPath });
        Equal(DragDropEffects.None, (DragDropEffects)Invoke(_main!, "GetFolderDropEffect", invalidData, destinationCard)!);
        await (Task)Invoke(_main!, "DropTracksIntoFolderAsync", invalidData, destinationCard)!;
        Check("folder drops reject unsupported files and sidebar pin payloads without modifying files", () =>
        {
            True(!File.Exists(Path.Combine(destination, Path.GetFileName(invalidPath))));
            Equal(DragDropEffects.None, (DragDropEffects)Invoke(_main!, "GetFolderDropEffect", new DataObject("XTONE.MusicFolder", destination), destinationCard)!);
            True(File.Exists(invalidPath));
        });
        // Restore the loose fixture through the same serialized mutation coordinator.
        Func<IProgress<LibraryProgress>, CancellationToken, Task<LibrarySnapshot>> restore = (progress, token) => store.MoveTracksToFolderAsync([movedPath], root, progress, token);
        True(await (Task<bool>)Invoke(_main!, "RunMusicOperationAsync", restore, "Restoring drop fixture…", true)!);
        Invoke(_main!, "ShowSection", "Albums");
    }

    private static bool IsClearBrush(Brush? brush) => brush is null || brush is SolidColorBrush solid && solid.Color.A == 0;

    private static async Task CheckButtonFocusAsync(Window owner, string description, IEnumerable<Button> candidates)
    {
        var buttons = candidates.Distinct().ToArray();
        True(buttons.Length > 0);
        foreach (var button in buttons)
        {
            button.ApplyTemplate();
            True(button.Focus());
            await Dispatcher.Yield(DispatcherPriority.Input);
            owner.UpdateLayout();
            True(button.IsKeyboardFocused && !button.IsMouseOver);
            True(ReferenceEquals(owner.FindResource("KeyboardFocusStyle"), button.FocusVisualStyle));
            var chrome = button.Template.FindName("Chrome", button) as Border
                ?? throw new Exception($"The {description} button has no Chrome border.");
            if (!IsClearBrush(chrome.BorderBrush) && chrome.BorderThickness != new Thickness(0))
                throw new Exception($"Focused {description} button {AutomationProperties.GetName(button)} retained a visible border.");
        }
        Check($"focused {description} controls keep their chrome clear while retaining keyboard focus visuals", () => True(buttons.All(button => button.Focusable)));
        System.Windows.Input.Keyboard.ClearFocus();
    }

    private static async Task CheckMusicFocusAsync(MainWindow owner, string hostName, string borderName, string description)
    {
        var host = (FrameworkElement)owner.FindName(hostName);
        var button = Descendants(host).OfType<Button>().First(control => control.Tag is MusicBrowserItem { IsFolder: true });
        button.ApplyTemplate();
        System.Windows.Input.Keyboard.Focus(button);
        await Dispatcher.Yield(DispatcherPriority.Input);
        owner.UpdateLayout();
        Check($"programmatically focused Music {description} keeps its border clear and keyboard focus styling available", () =>
        {
            True(button.Focusable && button.IsKeyboardFocused && !button.IsMouseOver);
            True(ReferenceEquals(owner.FindResource("KeyboardFocusStyle"), button.FocusVisualStyle));
            var border = button.Template.FindName(borderName, button) as Border
                ?? throw new Exception($"Music {description} has no {borderName} border.");
            True(border.BorderBrush is null || border.BorderBrush is SolidColorBrush solid && solid.Color.A == 0);
        });
        System.Windows.Input.Keyboard.ClearFocus();
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

