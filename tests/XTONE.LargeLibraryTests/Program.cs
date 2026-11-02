using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XTONE.Models;
using XTONE.Services;

internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _exit = 1;
    private static int _passed;
    [STAThread] private static int Main()
    {
        var original = Environment.GetEnvironmentVariable("XTONE_DATA_DIR");
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture); Environment.SetEnvironmentVariable("XTONE_DATA_DIR", Path.Combine(fixture, "data"));
        var app = new XTONE.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/XTONE;component/Themes/DesignSystem.xaml", UriKind.Relative) });
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var window = new XTONE.MainWindow { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Width = 1060, Height = 700 };
            try
            {
                window.Show(); await Until(() => Get<bool>(window, "_libraryReady"));
                var measurements = new List<object>();
                foreach (var count in new[] { 10_000, 25_000 }) measurements.Add(await Exercise(window, fixture, count));
                var report = Path.Combine(AppContext.BaseDirectory, "large-library-results.json");
                File.WriteAllText(report, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {_passed} large-library behavior checks. Measurements: {report}"); _exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { window.Close(); await Task.Delay(150); app.Shutdown(_exit); }
        }));
        app.Run(); Environment.SetEnvironmentVariable("XTONE_DATA_DIR", original);
        return _exit;
    }
    private static async Task<object> Exercise(XTONE.MainWindow window, string fixture, int count)
    {
        var root = Path.Combine(fixture, "unavailable-music-" + count);
        var tracks = Enumerable.Range(0, count).Select(i => new Track
        {
            Id = "track-" + i, Path = Path.Combine(root, i % 10 == 0 ? "" : "Folder " + i / 50, $"{i:D5}.flac"),
            Title = "Track " + i.ToString("D5"), Artist = "Artist " + i % 2000,
            Performers = ["Artist " + i % 2000], Album = "Tag album " + i / 50,
            IsFavorite = i % 13 == 0, Duration = TimeSpan.FromMinutes(3), Format = "FLAC", IsLossless = true
        }).ToList();
        var history = tracks.Where((_, i) => i % 9 == 0).Select((track, i) => new ListeningHistoryEntry { Path = track.Path, LastPlayedUtc = DateTime.UtcNow.AddMinutes(-i), PlayCount = 1 }).ToList();
        var snapshot = new LibrarySnapshot(tracks, [root], [], []) { Revision = count, MusicRoot = root, History = history };
        var intervals = new List<double>(); var clock = Stopwatch.StartNew(); var last = clock.Elapsed.TotalMilliseconds;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; intervals.Add(now - last); last = now; }; timer.Start();
        var apply = Stopwatch.StartNew(); await (Task)Call(window, "ApplySnapshotAsync", snapshot)!; apply.Stop();
        await Task.Delay(100);
        Check(Get<List<Track>>(window, "_tracks").Count == count, count + " cached tracks publish without opening decoders");
        Check(Get<LibraryPresentationIndex>(window, "_presentationIndex").ByPath.Count == count, "path index retains all entries");
        var refresh = Stopwatch.StartNew(); for (var i = 0; i < 100; i++) Call(window, "RefreshInterfaceState"); refresh.Stop();
        Call(window, "ShowSection", "Artists"); window.UpdateLayout(); await Task.Delay(30);
        var artists = (ListBox)window.FindName("ArtistItems");
        var realizedArtists = Descendants(artists).OfType<ListBoxItem>().Count();
        Check(artists.Items.Count == 2000 && realizedArtists < 100, "artist rows virtualize instead of creating the whole collection");
        Call(window, "ShowSection", "Library");
        var search = (TextBox)window.FindName("SearchBox"); search.Visibility = Visibility.Visible;
        var query = Stopwatch.StartNew(); search.Text = "Track 00001"; search.Text = $"Track {count - 1:D5}";
        var list = (ListBox)window.FindName("LibraryTracks");
        await Until(() => list.Items.Count == 1 && list.Items[0] is Track item && item.Id == "track-" + (count - 1)); query.Stop();
        Check(list.Items.Count == 1, "only the newest rapid search is published");
        search.Text = "no matching audio"; search.Clear(); Call(window, "UpdateVisiblePage");
        await Until(() => list.Items.Count == count); await Task.Delay(200);
        Check(list.Items.Count == count, "clearing search cannot be overwritten by an older worker");
        Call(window, "ShowSection", "Albums"); Call(window, "SetMusicView", "List"); window.UpdateLayout();
        var music = (ListBox)window.FindName("MusicList"); var realizedMusic = Descendants(music).OfType<ListBoxItem>().Count();
        Check(music.Items.OfType<MusicBrowserItem>().Count(item => item.Track is not null) == count / 10 && realizedMusic < 100, "mixed music list keeps loose tracks and virtualizes rows");
        Call(window, "SetMusicView", "Grid"); window.UpdateLayout();
        var grid = (ListBox)window.FindName("AlbumItems"); var realizedGrid = Descendants(grid).OfType<ListBoxItem>().Count();
        Check(realizedGrid < 100, "grid virtualizes prepared rows");
        timer.Stop(); intervals.Sort();
        var p95 = intervals.Count == 0 ? 0 : intervals[(int)Math.Min(intervals.Count - 1, Math.Ceiling(intervals.Count * .95) - 1)];
        var result = new { tracks = count, applyMs = apply.Elapsed.TotalMilliseconds, refresh100Ms = refresh.Elapsed.TotalMilliseconds, latestSearchMs = query.Elapsed.TotalMilliseconds, dispatcherSamples = intervals.Count, dispatcherP95IntervalMs = p95, dispatcherMaxIntervalMs = intervals.DefaultIfEmpty().Max(), realizedArtists, realizedMusic, realizedGrid };
        Console.WriteLine(JsonSerializer.Serialize(result)); return result;
    }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); _passed++; Console.WriteLine("PASS " + label); }
    private static async Task Until(Func<bool> test) { var watch = Stopwatch.StartNew(); while (!test()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(); await Task.Delay(10); } }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) { try { return target.GetType().GetMethod(name, Private)!.Invoke(target, args); } catch (TargetInvocationException e) { throw e.InnerException!; } }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
}
