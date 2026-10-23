using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTONE.Controls;

internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly MethodInfo ReadThumbnail = typeof(AlbumArtwork).GetMethod("ReadThumbnail", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly FieldInfo Thumbnail = typeof(AlbumArtwork).GetField("_thumbnail", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [STAThread]
    private static int Main(string[] args)
    {
        var requestedPath = args.FirstOrDefault(argument => !argument.StartsWith("--"))
            ?? System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "xtone-logo.png");
        if (!System.IO.File.Exists(requestedPath))
        {
            Console.Error.WriteLine("Pass an existing artwork image path.");
            return 2;
        }
        if (args.Contains("--trace"))
            AppDomain.CurrentDomain.FirstChanceException += Trace;
        var path = System.IO.Path.GetFullPath(requestedPath);
        var decoded = DecodeAsync(path).GetAwaiter().GetResult();
        Check(decoded is not null, "background decoder returns the saved artwork");
        if (decoded is not null)
            Check(decoded.IsFrozen && decoded.PixelWidth <= 560 && decoded.PixelHeight <= 560, "thumbnail is frozen and bounded");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var artwork = new AlbumArtwork { SourcePath = path, Width = 180, Height = 180 };
            var window = new Window
            {
                Title = "XTONE artwork checks", Width = 220, Height = 220,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
                Content = artwork
            };
            try
            {
                window.Show();
                await WaitFor(() => Thumbnail.GetValue(artwork) is BitmapSource);
                Check(Thumbnail.GetValue(artwork) is BitmapSource, "loaded SourcePath control receives its thumbnail");
                Check(ReferenceEquals(decoded, await DecodeAsync(path)), "unchanged file reuses the cached thumbnail");

                var missing = path + ".missing";
                artwork.SourcePath = missing;
                await WaitFor(() => IsIdle(artwork));
                Check(Thumbnail.GetValue(artwork) is null, "missing artwork clears the previous image and falls back");
                artwork.SourcePath = path;
                await WaitFor(() => Thumbnail.GetValue(artwork) is BitmapSource);
                Check(Thumbnail.GetValue(artwork) is BitmapSource, "reused control loads a new valid path");

                artwork.Source = decoded;
                artwork.SourcePath = missing;
                Check(ReferenceEquals(artwork.Source, decoded) && Thumbnail.GetValue(artwork) is null, "explicit Source preview takes precedence over SourcePath");
                artwork.SourcePath = path;
                artwork.Source = null;
                await WaitFor(() => Thumbnail.GetValue(artwork) is BitmapSource);
                Check(Thumbnail.GetValue(artwork) is BitmapSource, "clearing preview resumes SourcePath loading");

                window.Content = null;
                await WaitFor(() => !artwork.IsLoaded);
                Check(Thumbnail.GetValue(artwork) is null, "unloaded controls release their thumbnail reference");
                window.Content = artwork;
                await WaitFor(() => Thumbnail.GetValue(artwork) is BitmapSource);
                Check(Thumbnail.GetValue(artwork) is BitmapSource, "reloaded controls request artwork again");

                var slots = (SemaphoreSlim)typeof(AlbumArtwork).GetField("DecodeSlots", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                for (var slot = 0; slot < 3; slot++) await slots.WaitAsync();
                try
                {
                    artwork.Refresh();
                    artwork.SourcePath = missing;
                    window.Content = null;
                    await WaitFor(() => !artwork.IsLoaded);
                }
                finally { slots.Release(3); }
                await Task.Delay(100);
                Check(!artwork.IsLoaded && Thumbnail.GetValue(artwork) is null, "cancelled queued loads cannot repopulate an unloaded control");
                artwork.SourcePath = path;
                window.Content = artwork;
                await WaitFor(() => Thumbnail.GetValue(artwork) is BitmapSource);
                Check(Thumbnail.GetValue(artwork) is BitmapSource, "recycled control recovers after queued-load cancellation");

                await CheckFingerprintInvalidation(path);
            }
            catch (Exception error)
            {
                Check(false, error.ToString());
            }
            finally
            {
                window.Close();
                Console.WriteLine($"Artwork tests: {_passed} passed, {_failed} failed.");
                app.Shutdown(_failed == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    private static Task<BitmapSource?> DecodeAsync(string path) =>
        Task.Run(() => (BitmapSource?)ReadThumbnail.Invoke(null, [path, CancellationToken.None]));

    private static bool IsIdle(AlbumArtwork artwork) =>
        typeof(AlbumArtwork).GetField("_loadCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(artwork) is null;

    private static async Task CheckFingerprintInvalidation(string originalPath)
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XTONE.ArtworkTests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, "cover.png");
        try
        {
            System.IO.File.Copy(originalPath, path);
            var original = await DecodeAsync(path);
            var pixels = new byte[8 * 4 * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset + 1] = 180;
                pixels[offset + 3] = 255;
            }
            var replacement = BitmapSource.Create(8, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(replacement));
            using (var output = System.IO.File.Create(path)) encoder.Save(output);
            System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            var refreshed = await DecodeAsync(path);
            Check(original is not null && refreshed is { PixelWidth: 8, PixelHeight: 4 } && !ReferenceEquals(original, refreshed),
                "same-path file replacement invalidates the cached fingerprint");
            using var exclusive = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
            Check(exclusive.Length > 0, "thumbnail decoding releases the source file handle");
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.Directory.Delete(folder);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (condition) { _passed++; Console.WriteLine("PASS " + message); }
        else { _failed++; Console.Error.WriteLine("FAIL " + message); }
    }

    private static void Trace(object? sender, FirstChanceExceptionEventArgs args)
    {
        Console.WriteLine("TRACE " + args.Exception + Environment.NewLine + Environment.StackTrace);
    }
}
