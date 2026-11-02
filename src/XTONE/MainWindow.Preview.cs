using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTONE.Models;
namespace XTONE;
public partial class MainWindow
{
    private void LoadDesignPreview()
    {
        // In-memory visual fixture: never loads or saves the user's library.
        var args = Environment.GetCommandLineArgs();
        var previewIndex = Array.IndexOf(args, "--preview");
        var reference = previewIndex >= 0 && previewIndex + 1 < args.Length
            ? LoadImage(args[previewIndex + 1]) as BitmapSource : null;
        var names = new[] { "Blue Hour", "Room Studies", "Night Drive", "Soft Focus" };
        var artists = new[] { "Mira Vale", "North Window", "Low Season", "Aster Lane" };
        var referenceRects = new[]
        {
            new Rect(344, 251, 278, 276), new Rect(645, 251, 278, 276),
            new Rect(945, 251, 278, 276), new Rect(1246, 251, 278, 276)
        };
        _albumCards.Clear();
        for (var i = 0; i < names.Length; i++)
        {
            ImageSource? artwork = null;
            if (reference is not null)
            {
                var rect = referenceRects[i];
                var scaleX = reference.PixelWidth / 1586d;
                var scaleY = reference.PixelHeight / 992d;
                var crop = new Int32Rect((int)(rect.X * scaleX), (int)(rect.Y * scaleY),
                    (int)(rect.Width * scaleX), (int)(rect.Height * scaleY));
                var bitmap = new CroppedBitmap(reference, crop);
                bitmap.Freeze();
                artwork = bitmap;
            }
            _albumCards.Add(new AlbumCard { Id = $"preview-{i}", Title = names[i], Artist = artists[i], Artwork = artwork });
        }
        RebuildMusicItems(); ShowSection("Albums");
        NowPlayingTitle.Text = "Glasshouse";
        NowPlayingArtist.Text = "Mira Vale";
        NowPlayingArtwork.Source = _albumCards[0].Artwork;
        QualityText.Text = "FLAC · Lossless · 24-bit / 96 kHz";
        PositionSlider.Maximum = 324;
        PositionSlider.Value = 138;
        ElapsedText.Text = "2:18";
        DurationText.Text = "5:24";
        VolumeSlider.Value = .42;
        UpdatePlayPauseButton();
    }

    private static string FormatDuration(IEnumerable<Track> tracks)
    {
        var duration = TimeSpan.FromSeconds(tracks.Sum(track => track.Duration.TotalSeconds));
        return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
    }

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? time.ToString(@"h\:mm\:ss")
        : time.ToString(@"m\:ss");

    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string? ExistingFile(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    private static System.Windows.Media.ImageSource? LoadImage(string? path)
    {
        if (ExistingFile(path) is not { } existing)
            return null;
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(existing, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

}

