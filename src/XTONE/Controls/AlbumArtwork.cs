using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTONE.Controls;

/// <summary>Square album art with rounded image clipping and a quiet cover fallback.</summary>
public sealed class AlbumArtwork : FrameworkElement
{
    private const int ThumbnailSize = 560;
    private const int MaximumCacheEntries = 128;
    private const long MaximumCacheBytes = 48L * 1024 * 1024;
    private static readonly SemaphoreSlim DecodeSlots = new(3, 3);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CachedArtwork>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<CachedArtwork> RecentArtwork = new();
    private static long _cacheBytes;

    private static readonly Brush DefaultFallback = Frozen(new SolidColorBrush(Color.FromRgb(72, 88, 109)));
    private static readonly Brush FallbackLight = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(55, 255, 255, 255), 0),
            new(Color.FromArgb(0, 255, 255, 255), 0.48),
            new(Color.FromArgb(74, 0, 0, 0), 1)
        }, new Point(0, 0), new Point(1, 1)));
    private static readonly Brush SoftShape = Frozen(new SolidColorBrush(Color.FromArgb(21, 255, 255, 255)));
    private static readonly Pen FineLine = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(29, 255, 255, 255)), 1));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(AlbumArtwork),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(AlbumArtwork),
        new FrameworkPropertyMetadata(null, OnSourcePathChanged));

    public static readonly DependencyProperty FallbackBrushProperty = DependencyProperty.Register(
        nameof(FallbackBrush), typeof(Brush), typeof(AlbumArtwork),
        new FrameworkPropertyMetadata(DefaultFallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private CancellationTokenSource? _loadCancellation;
    private BitmapSource? _thumbnail;
    private long _loadVersion;

    public AlbumArtwork()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>A local image path loaded as a cached thumbnail. An explicit Source takes precedence.</summary>
    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public Brush? FallbackBrush
    {
        get => (Brush?)GetValue(FallbackBrushProperty);
        set => SetValue(FallbackBrushProperty, value);
    }

    /// <summary>Rechecks a file that changed without changing its path.</summary>
    public void Refresh()
    {
        VerifyAccess();
        StartLoading();
    }

    private static void OnSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var artwork = (AlbumArtwork)sender;
        artwork.StartLoading();
    }

    private static void OnSourcePathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var artwork = (AlbumArtwork)sender;
        artwork.StartLoading();
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => StartLoading();

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        CancelLoading();
        _thumbnail = null;
        InvalidateVisual();
    }

    private void CancelLoading()
    {
        _loadVersion++;
        var cancellation = _loadCancellation;
        _loadCancellation = null;
        cancellation?.Cancel();
        // LoadAsync owns disposal so a queued worker never observes a disposed source.
    }

    private void StartLoading()
    {
        CancelLoading();
        _thumbnail = null;
        InvalidateVisual();
        if (!IsLoaded || Source is not null || string.IsNullOrWhiteSpace(SourcePath))
            return;

        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _ = LoadAsync(SourcePath, _loadVersion, cancellation);
    }

    private async Task LoadAsync(string path, long version, CancellationTokenSource cancellation)
    {
        try
        {
            var thumbnail = await GetThumbnailAsync(path, cancellation.Token);
            if (!cancellation.IsCancellationRequested && IsLoaded && Source is null &&
                version == _loadVersion && ReferenceEquals(_loadCancellation, cancellation))
            {
                _thumbnail = thumbnail;
                InvalidateVisual();
            }
        }
        catch (OperationCanceledException)
        {
            // A recycled row or closed window no longer needs this image.
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
                _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    private static async Task<BitmapSource?> GetThumbnailAsync(string path, CancellationToken cancellation)
    {
        await DecodeSlots.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // File metadata and WIC decoding both stay off the dispatcher, including cache checks.
            return await Task.Run(() => ReadThumbnail(path, cancellation), cancellation).ConfigureAwait(false);
        }
        finally
        {
            DecodeSlots.Release();
        }
    }

    private static BitmapSource? ReadThumbnail(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var fullPath = NormalizePath(path);
            if (fullPath is null)
                return null;
            var fingerprint = Fingerprint.Read(fullPath);
            if (fingerprint is null)
            {
                RemoveCached(fullPath);
                return null;
            }
            if (TryGetCached(fullPath, fingerprint.Value, out var cached))
                return cached;

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            // Read dimensions without materializing the original's pixel buffer. Constrain the
            // longer edge so unusual portrait artwork cannot allocate an oversized thumbnail.
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
                return null;
            var frame = decoder.Frames[0];
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            if (width <= 0 || height <= 0)
                return null;
            cancellation.ThrowIfCancellationRequested();
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // StreamSource has no URI cache entry. IgnoreImageCache would make WPF attempt
            // to remove a null URI from its cache during EndInit and reject a valid image.
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width >= height)
                image.DecodePixelWidth = Math.Min(ThumbnailSize, width);
            else
                image.DecodePixelHeight = Math.Min(ThumbnailSize, height);
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            cancellation.ThrowIfCancellationRequested();

            // A file replaced during decoding must not poison the cache for its replacement.
            if (Fingerprint.Read(fullPath) == fingerprint)
                StoreCached(fullPath, fingerprint.Value, image);
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or FileFormatException or COMException or
            InvalidOperationException or System.Security.SecurityException)
        {
            // Missing, unreadable and malformed artwork share the existing cover fallback.
            return null;
        }
    }

    private static string? NormalizePath(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
                return null;
            path = uri.LocalPath;
        }
        return Path.GetFullPath(path);
    }

    private static bool TryGetCached(string path, Fingerprint fingerprint, out BitmapSource? image)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out var node))
            {
                if (node.Value.Fingerprint == fingerprint)
                {
                    RecentArtwork.Remove(node);
                    RecentArtwork.AddFirst(node);
                    image = node.Value.Image;
                    return true;
                }
                RemoveCachedNode(node);
            }
        }
        image = null;
        return false;
    }

    private static void StoreCached(string path, Fingerprint fingerprint, BitmapSource image)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out var existing))
                RemoveCachedNode(existing);
            var bytes = (long)image.PixelWidth * image.PixelHeight * Math.Max(4, (image.Format.BitsPerPixel + 7) / 8);
            var node = RecentArtwork.AddFirst(new CachedArtwork(path, fingerprint, image, bytes));
            Cache[path] = node;
            _cacheBytes += bytes;
            while ((Cache.Count > MaximumCacheEntries || _cacheBytes > MaximumCacheBytes) &&
                RecentArtwork.Last is { } oldest)
                RemoveCachedNode(oldest);
        }
    }

    private static void RemoveCached(string path)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out var node))
                RemoveCachedNode(node);
        }
    }

    private static void RemoveCachedNode(LinkedListNode<CachedArtwork> node)
    {
        Cache.Remove(node.Value.Path);
        RecentArtwork.Remove(node);
        _cacheBytes -= node.Value.Bytes;
    }

    private readonly record struct Fingerprint(long Length, long LastWriteTicks)
    {
        public static Fingerprint? Read(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? new(file.Length, file.LastWriteTimeUtc.Ticks) : null;
        }
    }

    private sealed record CachedArtwork(string Path, Fingerprint Fingerprint, BitmapSource Image, long Bytes);

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width)
            : double.IsFinite(availableSize.Height) ? Math.Max(0, availableSize.Height) : 264;
        if (double.IsFinite(availableSize.Height))
            side = Math.Min(side, Math.Max(0, availableSize.Height));
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(RenderSize);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        drawingContext.PushClip(new RectangleGeometry(bounds, 6, 6));
        if ((Source ?? _thumbnail) is { } image && image.Width > 0 && image.Height > 0)
        {
            var scale = Math.Max(bounds.Width / image.Width, bounds.Height / image.Height);
            var width = image.Width * scale;
            var height = image.Height * scale;
            drawingContext.DrawImage(image, new Rect(
                (bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height));
        }
        else
        {
            drawingContext.DrawRectangle(FallbackBrush ?? DefaultFallback, null, bounds);
            drawingContext.DrawRectangle(FallbackLight, null, bounds);
            drawingContext.DrawEllipse(SoftShape, null,
                new Point(bounds.Width * 0.19, bounds.Height * 0.98),
                bounds.Width * 0.7, bounds.Height * 0.7);
            drawingContext.DrawEllipse(null, FineLine,
                new Point(bounds.Width * 0.79, bounds.Height * 0.25),
                bounds.Width * 0.47, bounds.Height * 0.47);
            drawingContext.DrawLine(FineLine,
                new Point(0, bounds.Height * 0.66), new Point(bounds.Width, bounds.Height * 0.66));
        }
        drawingContext.Pop();
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
