using System.ComponentModel;
using System.Windows.Media;
using XTONE.Models;
using XTONE.Services;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
        var root = Path.Combine(fixtures, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Run("starts with a useful empty player presentation", () =>
            {
                var state = new NowPlayingState();
                Equal(false, state.HasTrack);
                Equal(false, state.IsPlaying);
                Equal("Choose a track", state.Title);
                Equal("Play", state.PlayPauseLabel);
                Equal("Your music, ready when you are.", state.PlaybackLabel);
                Equal("0:00", state.ElapsedText);
                Equal("0:00", state.DurationText);
                Equal(1d, state.SeekMaximum);
            });

            Run("publishes current metadata and artwork changes without redundant notifications", () =>
            {
                var state = new NowPlayingState();
                using var notifications = new Notifications(state);
                var artwork = new DrawingImage(new GeometryDrawing(Brushes.CornflowerBlue, null, Geometry.Parse("M0,0 L10,0 10,10 0,10 Z")));
                artwork.Freeze();
                state.Title = "Glasshouse";
                state.Artist = "Mira Vale";
                state.Album = "Blue Hour";
                state.Quality = "FLAC · Lossless · 24-bit / 96 kHz";
                state.Status = "Available locally";
                state.ArtworkPath = Path.Combine(root, "cover.png");
                state.Artwork = artwork;
                state.CoverBrush = Brushes.CornflowerBlue;
                notifications.Contains("Title", "Artist", "Album", "Quality", "Status", "ArtworkPath", "Artwork", "CoverBrush");
                Equal("Glasshouse", state.Title);
                Equal("Mira Vale", state.Artist);
                Equal("Blue Hour", state.Album);
                True(ReferenceEquals(artwork, state.Artwork));
                notifications.Clear();
                state.Title = "Glasshouse";
                state.Artwork = artwork;
                Equal(0, notifications.Names.Count);
            });

            Run("updates elapsed duration and remaining labels together", () =>
            {
                var state = new NowPlayingState();
                using var notifications = new Notifications(state);
                state.DurationSeconds = 90;
                notifications.Contains("DurationSeconds", "DurationText", "RemainingText", "SeekMaximum");
                notifications.Clear();
                state.PositionSeconds = 35;
                notifications.Contains("PositionSeconds", "ElapsedText", "RemainingText");
                Equal("0:35", state.ElapsedText);
                Equal("1:30", state.DurationText);
                Equal("−0:55", state.RemainingText);
                Equal(90d, state.SeekMaximum);
                notifications.Clear();
                state.PositionSeconds = 35;
                state.DurationSeconds = 90;
                Equal(0, notifications.Names.Count);
                state.DurationSeconds = 3661.25;
                Equal("1:01:01", state.DurationText);
            });

            Run("publishes queue identity changes even when repeated-track metadata is identical", () =>
            {
                var state = new NowPlayingState { Title = "Glasshouse", Artist = "Mira Vale", TrackIdentity = "queue-entry-1" };
                using var notifications = new Notifications(state);
                state.Title = "Glasshouse";
                state.Artist = "Mira Vale";
                state.TrackIdentity = "queue-entry-2";
                Equal(1, notifications.Names.Count);
                notifications.Contains("TrackIdentity");
                notifications.Clear();
                state.TrackIdentity = "queue-entry-2";
                Equal(0, notifications.Names.Count);
            });

            Run("sanitizes invalid time and formats extreme finite times without overflow", () =>
            {
                var state = new NowPlayingState();
                foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -10d })
                {
                    state.DurationSeconds = 30;
                    state.PositionSeconds = 12;
                    state.DurationSeconds = invalid;
                    state.PositionSeconds = invalid;
                    Equal(0d, state.DurationSeconds);
                    Equal(0d, state.PositionSeconds);
                    Equal("0:00", state.ElapsedText);
                    Equal("0:00", state.DurationText);
                    Equal("−0:00", state.RemainingText);
                    Equal(1d, state.SeekMaximum);
                }
                state.DurationSeconds = 1e300;
                state.PositionSeconds = 1e299;
                True(!string.IsNullOrWhiteSpace(state.DurationText));
                True(!string.IsNullOrWhiteSpace(state.ElapsedText));
                True(!string.IsNullOrWhiteSpace(state.RemainingText));
                True(double.IsFinite(state.SeekMaximum));
            });

            Run("clamps volume while notifying only effective changes", () =>
            {
                var state = new NowPlayingState();
                using var notifications = new Notifications(state);
                state.Volume = 5;
                Equal(1d, state.Volume);
                notifications.Contains("Volume");
                notifications.Clear();
                state.Volume = 2;
                Equal(0, notifications.Names.Count);
                state.Volume = -.2;
                Equal(0d, state.Volume);
                foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    state.Volume = .5;
                    state.Volume = invalid;
                    Equal(0d, state.Volume);
                }
            });

            Run("keeps playback loading and favorite labels consistent across views", () =>
            {
                var state = new NowPlayingState();
                using var notifications = new Notifications(state);
                state.HasTrack = true;
                Equal("Paused", state.PlaybackLabel);
                notifications.Contains("HasTrack", "PlaybackLabel");
                notifications.Clear();
                state.IsPlaying = true;
                Equal("Pause", state.PlayPauseLabel);
                Equal("Now playing", state.PlaybackLabel);
                notifications.Contains("IsPlaying", "PlayPauseLabel", "PlaybackLabel");
                notifications.Clear();
                state.IsOpening = true;
                Equal("Cancel loading", state.PlayPauseLabel);
                Equal("Loading audio…", state.PlaybackLabel);
                notifications.Contains("IsOpening", "PlayPauseLabel", "PlaybackLabel");
                state.IsOpening = false;
                state.IsPlaying = false;
                Equal("Play", state.PlayPauseLabel);
                Equal("Paused", state.PlaybackLabel);
                notifications.Clear();
                state.IsFavorite = true;
                Equal("Remove from favorites", state.FavoriteLabel);
                notifications.Contains("IsFavorite", "FavoriteLabel");
                state.IsFavorite = false;
                Equal("Add to favorites", state.FavoriteLabel);
            });

            Run("accepts supported visualization modes and preserves reduced-motion controls", () =>
            {
                var state = new NowPlayingState();
                using var notifications = new Notifications(state);
                state.Visualization = "Bars";
                Equal("Bars", state.Visualization);
                notifications.Contains("Visualization");
                state.Visualization = "Off";
                Equal("Off", state.Visualization);
                state.Visualization = "unknown";
                Equal("Ribbon", state.Visualization);
                state.Visualization = null!;
                Equal("Ribbon", state.Visualization);
                notifications.Clear();
                state.MotionEnabled = false;
                state.CanSeek = true;
                state.MiniAlwaysOnTop = true;
                notifications.Contains("MotionEnabled", "CanSeek", "MiniAlwaysOnTop");
                True(!state.MotionEnabled && state.CanSeek && state.MiniAlwaysOnTop);
            });

            Run("roundtrips listening preferences with independent session copies", () =>
            {
                var data = Path.Combine(root, "roundtrip");
                var a = Path.Combine(root, "not-opened-a.flac");
                var b = Path.Combine(root, "not-opened-b.wav");
                var store = new LibraryStore(data);
                store.SaveSession(new PlaybackSession
                {
                    QueuePaths = [a, b, a], CurrentIndex = 2, PositionSeconds = 12.5,
                    Volume = .33, Shuffle = true, RepeatMode = "All", OutputDeviceId = "fixture-output",
                    ReducedMotion = true, LastSection = "Favorites", MiniAlwaysOnTop = true, FocusVisualization = "Bars"
                });
                var restored = new LibraryStore(data).GetSession();
                Equal(3, restored.QueuePaths.Count);
                Equal(a, restored.QueuePaths[2]);
                Equal(2, restored.CurrentIndex);
                Equal(12.5, restored.PositionSeconds);
                Equal(.33, restored.Volume);
                True(restored.Shuffle && restored.ReducedMotion && restored.MiniAlwaysOnTop);
                Equal("All", restored.RepeatMode);
                Equal("Bars", restored.FocusVisualization);
                Equal("Favorites", restored.LastSection);
                Equal("fixture-output", restored.OutputDeviceId);
                restored.QueuePaths.Clear();
                restored.MiniAlwaysOnTop = false;
                restored.FocusVisualization = "Off";
                var untouched = store.GetSession();
                Equal(3, untouched.QueuePaths.Count);
                True(untouched.MiniAlwaysOnTop);
                Equal("Bars", untouched.FocusVisualization);
            });

            Run("retains new session fields when recovering through the saved library", () =>
            {
                var data = Path.Combine(root, "session-in-library");
                var store = new LibraryStore(data);
                store.SaveSession(new PlaybackSession { MiniAlwaysOnTop = true, FocusVisualization = "Off" });
                store.SavePlaylist(new CustomPlaylist { Name = "Fixture", SmartRule = "favorites" });
                File.Delete(Path.Combine(data, "session.json"));
                var restored = new LibraryStore(data).Load().Session;
                True(restored.MiniAlwaysOnTop);
                Equal("Off", restored.FocusVisualization);
            });

            Run("loads legacy sessions with safe listening-mode defaults", () =>
            {
                var data = Path.Combine(root, "legacy-session");
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "session.json"), """{"QueuePaths":[],"CurrentIndex":-1,"Volume":0.4}""");
                var restored = new LibraryStore(data).GetSession();
                Equal(false, restored.MiniAlwaysOnTop);
                Equal("Ribbon", restored.FocusVisualization);
                Equal(.4, restored.Volume);
            });

            Run("normalizes unknown persisted visualization modes", () =>
            {
                foreach (var value in new[] { "unknown", "bars", "", null })
                {
                    var data = Path.Combine(root, "invalid-visualization-" + Guid.NewGuid().ToString("N"));
                    var store = new LibraryStore(data);
                    store.SaveSession(new PlaybackSession { FocusVisualization = value! });
                    Equal("Ribbon", new LibraryStore(data).GetSession().FocusVisualization);
                }
            });

            Console.WriteLine($"PASS: {_passed} presentation checks.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(fixtures + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(resolved, true);
        }
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine("PASS " + name);
    }
    private static void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; actual {actual}.");
    }

    private sealed class Notifications : IDisposable
    {
        private readonly INotifyPropertyChanged _source;
        public List<string> Names { get; } = [];
        public Notifications(INotifyPropertyChanged source) { _source = source; source.PropertyChanged += Changed; }
        private void Changed(object? sender, PropertyChangedEventArgs args) => Names.Add(args.PropertyName ?? "");
        public void Clear() => Names.Clear();
        public void Contains(params string[] expected)
        {
            foreach (var name in expected)
                if (!Names.Contains(name))
                    throw new Exception($"Expected a notification for {name}; received {string.Join(", ", Names)}.");
        }
        public void Dispose() => _source.PropertyChanged -= Changed;
    }
}
