using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Nexora.App.Services;
using Nexora.Core.Model;

namespace Nexora.App.Controls;

/// <summary>
/// A live picture (a video file, a capture card, an NDI stream). Frames arrive on a background thread,
/// are copied once, and every <see cref="VideoView"/> showing the feed draws the same bitmap, so the
/// audience screen, the Broadcast picture and the recording all share one decoder.
/// </summary>
public abstract class VideoFeed : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _back;
    private int _bw, _bh;
    private bool _dirty;
    private int _posted;

    public WriteableBitmap? Bitmap { get; private set; }
    public DateTime LastFrameUtc { get; private set; }
    public string? Problem { get; protected set; }
    public bool HasSignal => DateTime.UtcNow - LastFrameUtc < TimeSpan.FromSeconds(2);

    /// <summary>Raised on the UI thread after a new frame was copied into <see cref="Bitmap"/>.</summary>
    public event Action? Updated;

    /// <summary>Called from the decoder's thread with one BGRA (or BGRX) frame.</summary>
    protected unsafe void Publish(IntPtr data, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || data == IntPtr.Zero) return;
        lock (_gate)
        {
            var size = width * height * 4;
            if (_back is null || _back.Length != size) _back = new byte[size];
            fixed (byte* dst = _back)
            {
                if (stride == width * 4) Buffer.MemoryCopy((void*)data, dst, size, size);
                else
                    for (var y = 0; y < height; y++)
                        Buffer.MemoryCopy((byte*)data + (long)y * stride, dst + (long)y * width * 4, width * 4, width * 4);
            }
            _bw = width;
            _bh = height;
            _dirty = true;
        }
        LastFrameUtc = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _posted, 1) == 0) Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
    }

    private unsafe void Flush()
    {
        Interlocked.Exchange(ref _posted, 0);
        lock (_gate)
        {
            if (!_dirty || _back is null) return;
            _dirty = false;
            if (Bitmap is null || Bitmap.PixelSize.Width != _bw || Bitmap.PixelSize.Height != _bh)
                Bitmap = new WriteableBitmap(new PixelSize(_bw, _bh), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var fb = Bitmap.Lock();
            fixed (byte* src = _back)
            {
                var row = _bw * 4;
                if (fb.RowBytes == row) Buffer.MemoryCopy(src, (void*)fb.Address, (long)row * _bh, (long)row * _bh);
                else
                    for (var y = 0; y < _bh; y++)
                        Buffer.MemoryCopy(src + (long)y * row, (byte*)fb.Address + (long)y * fb.RowBytes, row, row);
            }
        }
        Updated?.Invoke();
    }

    public abstract void Dispose();
}

/// <summary>Shared, reference-counted feeds: the same key gives the same decoder.</summary>
public static class VideoFeeds
{
    private sealed class Entry(VideoFeed feed)
    {
        public VideoFeed Feed { get; } = feed;
        public int Count;
        public IDisposable? Release;
    }

    private static readonly Dictionary<string, Entry> All = new();

    public static VideoFeed Acquire(string key, Func<VideoFeed> create)
    {
        if (!All.TryGetValue(key, out var e))
        {
            e = new Entry(create());
            All[key] = e;
        }
        e.Count++;
        e.Release?.Dispose();
        e.Release = null;
        return e.Feed;
    }

    /// <summary>Lets go of a feed. It closes a few seconds later, so a screen rebuild does not restart a video.</summary>
    public static void Release(string key)
    {
        if (!All.TryGetValue(key, out var e)) return;
        e.Count--;
        if (e.Count > 0) return;
        e.Release = DispatcherTimer.RunOnce(() =>
        {
            if (e.Count > 0 || !All.TryGetValue(key, out var cur) || cur != e) return;
            All.Remove(key);
            try { e.Feed.Dispose(); } catch (Exception ex) { AppLog.Write("Video close: " + ex.Message); }
        }, TimeSpan.FromSeconds(3));
    }

    public static VideoFeed? Peek(string key) => All.TryGetValue(key, out var e) ? e.Feed : null;
}

/// <summary>Draws a feed, filling the area (cropping) or fitting inside it.</summary>
public sealed class VideoView : Control
{
    public static readonly StyledProperty<VideoFeed?> FeedProperty = AvaloniaProperty.Register<VideoView, VideoFeed?>(nameof(Feed));

    private VideoFeed? _feed;

    public bool Fill { get; set; } = true;

    public VideoFeed? Feed
    {
        get => GetValue(FeedProperty);
        set => SetValue(FeedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != FeedProperty) return;
        if (_feed is not null) _feed.Updated -= InvalidateVisual;
        _feed = change.GetNewValue<VideoFeed?>();
        if (_feed is not null && this.IsAttachedToVisualTree()) _feed.Updated += InvalidateVisual;
        _faded = false;
        Opacity = 1;
        InvalidateVisual();
    }

    /// <summary>
    /// When the picture stops (a cable or the network drops), hold the last frame for this long, then fade
    /// to whatever is underneath. Zero keeps the last frame for ever.
    /// </summary>
    public TimeSpan HoldOnLoss { get; set; } = TimeSpan.FromSeconds(4);

    private DispatcherTimer? _watch;
    private bool _faded;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_feed is not null) { _feed.Updated -= InvalidateVisual; _feed.Updated += InvalidateVisual; }
        Transitions ??= [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(800) }];
        _watch = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Watch());
        _watch.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_feed is not null) _feed.Updated -= InvalidateVisual;
        _watch?.Stop();
        _watch = null;
    }

    private void Watch()
    {
        if (_feed is null || HoldOnLoss <= TimeSpan.Zero || _feed.Bitmap is null) return;
        var lost = DateTime.UtcNow - _feed.LastFrameUtc > TimeSpan.FromSeconds(2) + HoldOnLoss;
        if (lost == _faded) return;
        _faded = lost;
        Opacity = lost ? 0 : 1;
    }

    public override void Render(DrawingContext context)
    {
        var bmp = _feed?.Bitmap;
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Black, bounds);
        if (bmp is null) return;
        var src = new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height);
        var scale = Fill
            ? Math.Max(bounds.Width / src.Width, bounds.Height / src.Height)
            : Math.Min(bounds.Width / src.Width, bounds.Height / src.Height);
        var w = src.Width * scale;
        var h = src.Height * scale;
        var dest = new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
        using (context.PushClip(bounds))
            context.DrawImage(bmp, src, dest);
    }
}

/// <summary>Video through LibVLC: files, capture cards (DirectShow on Windows) and network streams.</summary>
public sealed class VlcFeed : VideoFeed
{
    private static LibVLC? _vlc;
    private static readonly object Init = new();
    private static string? _initProblem;

    private readonly MediaPlayer _player;
    private readonly Media _media;
    private readonly MediaPlayer.LibVLCVideoFormatCb _format;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    private readonly MediaPlayer.LibVLCVideoLockCb _lock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;
    private IntPtr _buffer;
    private int _w, _h;
    private bool _disposed;

    /// <summary>LibVLC, started once. Null (with <see cref="InitProblem"/>) when it cannot load on this PC.</summary>
    public static LibVLC? Vlc
    {
        get
        {
            lock (Init)
            {
                if (_vlc is not null || _initProblem is not null) return _vlc;
                try
                {
                    LibVLCSharp.Shared.Core.Initialize();
                    _vlc = new LibVLC("--no-video-title-show", "--no-osd", "--quiet", "--no-snapshot-preview", "--aout=" + (OperatingSystem.IsWindows() ? "mmdevice" : "any"));
                }
                catch (Exception ex)
                {
                    _initProblem = "Video playback is not available: " + ex.Message;
                    AppLog.Write(_initProblem);
                }
                return _vlc;
            }
        }
    }

    public static string? InitProblem => _initProblem;

    public MediaPlayer Player => _player;

    /// <param name="mrl">A file path, or a URL such as dshow:// for a capture card.</param>
    /// <param name="options">LibVLC media options (":dshow-vdev=Camera 1", ":input-repeat=65535").</param>
    public VlcFeed(string mrl, IEnumerable<string>? options = null, bool mute = false, int maxWidth = 1920, int maxHeight = 1080)
    {
        var vlc = Vlc ?? throw new InvalidOperationException(_initProblem ?? "Video playback is not available.");
        _media = mrl.Contains("://") ? new Media(vlc, mrl, FromType.FromLocation) : new Media(vlc, mrl, FromType.FromPath);
        foreach (var o in options ?? []) _media.AddOption(o);
        _player = new MediaPlayer(_media) { EnableHardwareDecoding = true };
        if (mute) _player.Mute = true;

        _format = (ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines) =>
        {
            // Ask for 32-bit BGRA at the source size, no larger than full HD.
            Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
            double w = width, h = height;
            var s = Math.Min(1, Math.Min(maxWidth / Math.Max(1, w), maxHeight / Math.Max(1, h)));
            width = (uint)Math.Max(2, Math.Round(w * s / 2) * 2);
            height = (uint)Math.Max(2, Math.Round(h * s / 2) * 2);
            _w = (int)width;
            _h = (int)height;
            pitches = width * 4;
            lines = height;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal((int)(pitches * lines));
            return 1;
        };
        _cleanup = (ref IntPtr opaque) =>
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        };
        _lock = (IntPtr opaque, IntPtr planes) =>
        {
            Marshal.WriteIntPtr(planes, _buffer);
            return IntPtr.Zero;
        };
        _display = (IntPtr opaque, IntPtr picture) =>
        {
            if (!_disposed) Publish(_buffer, _w, _h, _w * 4);
        };
        _player.SetVideoFormatCallbacks(_format, _cleanup);
        _player.SetVideoCallbacks(_lock, null, _display);
        _player.EncounteredError += (_, _) => Problem = "This video could not be played.";
    }

    public void Play() => _player.Play();

    public void Pause() => _player.SetPause(true);

    public void Resume() => _player.SetPause(false);

    /// <summary>Seconds into the video.</summary>
    public double Time => Math.Max(0, _player.Time / 1000.0);

    public void Seek(double seconds)
    {
        if (_player.IsSeekable) _player.Time = (long)(seconds * 1000);
    }

    public int Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 125);
    }

    /// <summary>Plays the sound through a named output (as listed by <see cref="AudioOutputs"/>).</summary>
    public void UseAudioDevice(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return;
        try
        {
            var dev = _player.AudioOutputDeviceEnum.FirstOrDefault(d => d.Description == description);
            if (dev.DeviceIdentifier is { } id) _player.SetOutputDevice(id);
        }
        catch (Exception ex) { AppLog.Write("Audio device: " + ex.Message); }
    }

    public static IReadOnlyList<string> AudioOutputs()
    {
        try
        {
            if (Vlc is not { } vlc) return [];
            using var mp = new MediaPlayer(vlc);
            return mp.AudioOutputDeviceEnum.Select(d => d.Description).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToList();
        }
        catch { return []; }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var p = _player;
        var m = _media;
        // Stopping LibVLC can take a moment: never on the UI thread.
        Task.Run(() =>
        {
            try { p.Stop(); } catch { }
            p.Dispose();
            m.Dispose();
        });
    }
}

/// <summary>
/// Keeps each playing video in step with the Live Output PC's clock: started, paused, moved and
/// looped from the <see cref="MediaPlay"/> state alone, so every output window shows the same frame.
/// </summary>
public static class MediaSync
{
    private sealed class Track
    {
        public required VlcFeed Feed;
        public required MediaPlay Play;
        public bool Started;
    }

    private static readonly Dictionary<long, Track> Tracks = new();
    private static DispatcherTimer? _timer;

    public static string Key(MediaPlay p) => "media:" + p.Id;

    /// <summary>The decoder for this play (shared by every window), started at the right place.</summary>
    public static VideoFeed Acquire(MediaPlay p)
    {
        var path = Path.Combine(Nexora.Core.Config.AppPaths.VideosDir, p.Media.File);
        var feed = VideoFeeds.Acquire(Key(p), () =>
        {
            if (!File.Exists(path)) return new MissingFeed("The video file is missing on this PC: " + p.Media.File);
            try
            {
                var start = p.Position(DateTime.UtcNow);
                var opts = new List<string> { $":start-time={start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}" };
                if (p.Loop) opts.Add(":input-repeat=65535");
                return new VlcFeed(path, opts);
            }
            catch (Exception ex)
            {
                return new MissingFeed(ex.Message);
            }
        });
        if (feed is VlcFeed v) Apply(v, p);
        return feed;
    }

    public static void Release(MediaPlay p) => VideoFeeds.Release(Key(p));

    private static void Apply(VlcFeed feed, MediaPlay p)
    {
        if (!Tracks.TryGetValue(p.Id, out var t) || t.Feed != feed)
        {
            t = new Track { Feed = feed, Play = p };
            Tracks[p.Id] = t;
        }
        t.Play = p;
        try
        {
            feed.Volume = p.Volume;
            if (!t.Started)
            {
                feed.UseAudioDevice(p.AudioDevice);
                feed.Player.Mute = !p.Playing;
                feed.Play();
                t.Started = true;
            }
            else if (p.Playing)
            {
                feed.Player.Mute = false;
                feed.Resume();
            }
        }
        catch (Exception ex) { AppLog.Write("Video: " + ex.Message); }
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Check());
        _timer.Start();
    }

    private static void Check()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, t) in Tracks.ToList())
        {
            if (VideoFeeds.Peek(Key(t.Play)) != t.Feed)
            {
                Tracks.Remove(id);
                continue;
            }
            try
            {
                var p = t.Play;
                var want = p.Position(now);
                var at = t.Feed.Time;
                if (!p.Playing)
                {
                    // Cued or paused: hold the frame at the chosen place, silently.
                    if (t.Feed.Player.IsPlaying && at + 0.05 >= want) t.Feed.Pause();
                    else if (!t.Feed.Player.IsPlaying && Math.Abs(at - want) > 0.5) t.Feed.Seek(want);
                    continue;
                }
                if (!t.Feed.Player.IsPlaying) t.Feed.Resume();
                if (p.Media.DurationSeconds > 0 && want < p.Media.DurationSeconds - 1 && Math.Abs(at - want) > 0.75) t.Feed.Seek(want);
            }
            catch (Exception ex) { AppLog.Write("Video sync: " + ex.Message); }
        }
        if (Tracks.Count == 0) _timer?.Stop();
    }
}

/// <summary>A feed that never shows a picture, with the reason.</summary>
/// <summary>Video backgrounds behind Scripture and Lyrics: silent, looping for ever, one decoder per file.</summary>
public static class BackgroundLoops
{
    private static string Key(string file) => "loop:" + file;

    public static string? PathOf(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Contains('/') || file.Contains('\\') || file.Contains("..")) return null;
        foreach (var dir in new[] { Nexora.Core.Config.AppPaths.BuiltInBackgroundsDir, Nexora.Core.Config.AppPaths.MediaDir, Nexora.Core.Config.AppPaths.VideosDir })
        {
            var p = Path.Combine(dir, file);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static VideoFeed Acquire(string file) => VideoFeeds.Acquire(Key(file), () =>
    {
        if (PathOf(file) is not { } path) return new MissingFeed("The background video is missing on this PC: " + file);
        try
        {
            var feed = new VlcFeed(path, [":input-repeat=65535", ":no-audio"], mute: true);
            feed.Play();
            return feed;
        }
        catch (Exception ex) { return new MissingFeed(ex.Message); }
    });

    public static void Release(string file) => VideoFeeds.Release(Key(file));
}

public sealed class MissingFeed : VideoFeed
{
    public MissingFeed(string problem) => Problem = problem;

    public override void Dispose() { }
}

/// <summary>Opens the picture a <see cref="CameraFeed"/> names: an NDI stream, a capture card or a test file.</summary>
public static class CameraFeeds
{
    public static string Key(CameraFeed c) => $"cam:{c.Kind}:{c.Source}";

    public static VideoFeed Acquire(CameraFeed c) => VideoFeeds.Acquire(Key(c), () => Open(c.Kind, c.Source));

    public static void Release(CameraFeed c) => VideoFeeds.Release(Key(c));

    public static VideoFeed Open(CameraSourceKind kind, string source)
    {
        try
        {
            switch (kind)
            {
                case CameraSourceKind.Ndi:
                    return new NdiFeed(source);
                case CameraSourceKind.Device:
                {
                    // DirectShow on Windows; video4linux elsewhere (for testing).
                    var f = OperatingSystem.IsWindows()
                        ? new VlcFeed("dshow://", [$":dshow-vdev={source}", ":dshow-adev=none", ":live-caching=150", ":dshow-fps=30"], mute: true)
                        : new VlcFeed("v4l2://" + source, [":live-caching=150"], mute: true);
                    f.Play();
                    return f;
                }
                default:
                {
                    var path = File.Exists(source) ? source : Path.Combine(Nexora.Core.Config.AppPaths.VideosDir, source);
                    if (!File.Exists(path)) return new MissingFeed("File not found: " + source);
                    var f = new VlcFeed(path, [":input-repeat=65535"], mute: true);
                    f.Play();
                    return f;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Camera: " + ex.Message);
            return new MissingFeed(ex.Message);
        }
    }

    /// <summary>Capture cards and webcams on this PC (DirectShow names on Windows).</summary>
    public static IReadOnlyList<string> Devices()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return DirectShow.VideoDevices();
            return Directory.Exists("/dev") ? Directory.GetFiles("/dev", "video*").OrderBy(x => x).ToList() : [];
        }
        catch (Exception ex)
        {
            AppLog.Write("Devices: " + ex.Message);
            return [];
        }
    }
}
