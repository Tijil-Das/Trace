using System.IO;
using System.Text.Json;

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

using ScreenRecall.Player;

namespace ScreenRecall.Dashboard.Services;

/// <summary>One recorded day as the web UI wants it.</summary>
public sealed record DayRow(
    string Day,
    long FirstUs,
    long LastUs,
    long SpanUs,
    long LogBytes,
    long SessionBytes,
    long Entries,
    int Checkpoints,
    int Monitors);

/// <summary>One recorded stretch of a day, for the replay list.</summary>
public sealed record ReplayRow(
    string FileName,
    long StartUs,
    long EndUs,
    long DurationUs,
    long EntryCount,
    int DistinctWindows,
    long Bytes);

/// <summary>A quiet stretch of a day (no entries in it).</summary>
public sealed record IdleRow(long StartUs, long EndUs, long DurationUs);

/// <summary>A stretch of a day nobody recorded, from <c>{cmd:'gaps'}</c>. Different from <see cref="IdleRow"/>: there
/// the screen stood still, here it went on changing and none of it exists.</summary>
public sealed record GapRow(long StartUs, long EndUs, long DurationUs, string Reason);

/// <summary>A focus span, for jump-to-focus.</summary>
public sealed record SpanRow(string AppName, string WindowTitle, long StartUs, long EndUs, int Count);

/// <summary>State of the recorder process and its sign-in registration, for the settings section.</summary>
public sealed record RecorderRow(
    bool Running,
    bool StartWithWindows,
    string? Executable,
    string? StartupCommand,
    string? Message);

/// <summary>
/// Hosts the React dashboard inside the WPF shell and bridges it to the recording engine.
/// </summary>
/// <remarks>
/// The seam is deliberately narrow: the page sends JSON commands through <c>chrome.webview.postMessage</c> and
/// receives state back as JSON — except for frames, which travel as shared memory. Raw pixels over a message
/// would mean base64 (a third more bytes) plus a decode pass per frame, for a payload that only has to be copied
/// once here.
///
/// The browser context is locked down: no host objects are exposed to the page, the UI is served from a virtual
/// host rather than <c>file://</c> so it has an origin of its own, and zoom, status bar and the default context
/// menu are off. Only an environment variable turns the Vite dev server on.
/// </remarks>
public sealed partial class WebDashboardBridge : IDisposable
{
    /// <summary>Name of the environment variable that points the host at a Vite dev server.</summary>
    public const string DevServerVariable = "SCREENRECALL_UI_DEV";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WebView2 _view;
    private readonly CaptureClient _client;
    private readonly Func<string> _storageRoot;
    private CoreWebView2SharedBuffer? _frameBufferA;
    private CoreWebView2SharedBuffer? _frameBufferB;
    private int _bufferCapacity;
    private bool _useSecondBuffer;
    private bool _disposed;
    private long _lastTransportPushTicks;
    private List<DayRow>? _daysCache;
    private string? _daysCacheRoot;
    private long _daysCacheTicks;

    /// <summary>
    /// How long a built day list stays valid. It summarises files that only change while a recording runs, so a short
    /// window keeps a refresh cheap without showing stale days for long.
    /// </summary>
    private const int DaysCacheMs = 30_000;

    public WebDashboardBridge(WebView2 view, CaptureClient client, Func<string> storageRoot)
    {
        _view = view;
        _client = client;
        _storageRoot = storageRoot;
    }

    /// <summary>True once the page has been navigated and can receive messages.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Opens a day for playback. Supplied by the window so both dashboards share one code path for loading a
    /// session: the web UI asks for a day, it does not reach into the store itself.
    /// </summary>
    public Func<string, SessionPlayer?>? OpenDay { get; set; }

    /// <summary>The player currently being shown, owned by the window.</summary>
    public SessionPlayer? Player { get; set; }

    /// <summary>
    /// Called when the page enters or leaves fullscreen — the player's own button, <c>F</c>, a double-click on
    /// the picture, or Esc.
    /// </summary>
    /// <remarks>
    /// WebView2 gives the fullscreen element the whole control and stops there; making the control the whole
    /// screen is the shell's job, so the change is reported rather than acted on here.
    /// </remarks>
    public Action<bool>? FullScreenChanged { get; set; }

    /// <summary>
    /// Locates the built React app by walking up from the executable, so <c>dotnet run</c> from the repo and a
    /// published layout both work with no configuration. Null when the UI has not been built yet.
    /// </summary>
    public static string? FindUiFolder()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++)
        {
            string candidate = Path.Combine(directory.FullName, "dashboard-ui", "dist");
            if (File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Brings up the WebView and navigates to the UI. Returns false when there is nothing to show (the UI has not
    /// been built) or no WebView2 runtime is installed — the caller then keeps the WPF shell.
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        string? folder = FindUiFolder();
        string? devServer = Environment.GetEnvironmentVariable(DevServerVariable);
        if (folder is null && string.IsNullOrWhiteSpace(devServer))
        {
            return false;
        }

        try
        {
            // User data goes outside the install directory: the default is next to the executable, which is not
            // writable once this is packaged, and a stale folder there would be replaced by every build.
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Path.GetTempPath(), "ScreenRecall.WebView2"));
            await _view.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException
                                      or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        CoreWebView2 core = _view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.WebMessageReceived += OnWebMessageReceived;

        // The player's fullscreen button reaches the host as this event. The window follows it through
        // FullScreenChanged: without that, "fullscreen" would only mean a bigger panel inside the window.
        core.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;

        if (!string.IsNullOrWhiteSpace(devServer))
        {
            core.Navigate(devServer);
        }
        else
        {
            core.SetVirtualHostNameToFolderMapping(
                "screenrecall.local", folder!, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate("https://screenrecall.local/index.html");
        }

        IsActive = true;
        return true;
    }

    /// <summary>
    /// Sends one reconstructed frame to the page. <paramref name="frame"/> is BGRA with rows of
    /// <see cref="RenderedFrame.Stride"/>; the page wants RGBA, so this converts while copying into shared memory
    /// and forces alpha opaque — a compositor frame is transparent-black outside window content, and honouring
    /// that alpha would punch holes in the picture.
    /// </summary>
    public void PushFrame(RenderedFrame frame, SessionPlayer player)
    {
        if (!IsActive || _disposed)
        {
            return;
        }

        int pixels = frame.Width * frame.Height;
        if (pixels <= 0)
        {
            return;
        }

        int bytes = pixels * 4;
        if (_frameBufferA is null || _bufferCapacity < bytes)
        {
            _frameBufferA?.Dispose();
            _frameBufferB?.Dispose();
            _frameBufferA = _view.CoreWebView2.Environment.CreateSharedBuffer((ulong)bytes);
            _frameBufferB = _view.CoreWebView2.Environment.CreateSharedBuffer((ulong)bytes);
            _bufferCapacity = bytes;
        }

        // Two buffers alternating: the page holds a view of the last one until its next paint, and writing over
        // the buffer it is still reading would tear the picture.
        CoreWebView2SharedBuffer target = _useSecondBuffer ? _frameBufferB! : _frameBufferA!;
        _useSecondBuffer = !_useSecondBuffer;

        byte[] source = frame.Bgra;
        int stride = frame.Stride;
        byte[] row = new byte[stride];
        using (Stream stream = target.OpenStream())
        {
            for (int y = 0; y < frame.Height; y++)
            {
                int offset = y * stride;
                int copy = Math.Min(stride, Math.Max(0, source.Length - offset));
                for (int i = 0; i + 3 < copy; i += 4)
                {
                    row[i] = source[offset + i + 2];     // R
                    row[i + 1] = source[offset + i + 1]; // G
                    row[i + 2] = source[offset + i];     // B
                    row[i + 3] = 255;                    // opaque
                }

                stream.Write(row, 0, copy);
            }
        }

        string meta = JsonSerializer.Serialize(new
        {
            type = "frame",
            width = frame.Width,
            height = frame.Height,
            firstUs = player.FirstTimestampUs,
            positionUs = player.PositionUs,
            durationUs = player.DurationUs,
            playing = player.IsPlaying,
            speed = player.Speed,
        }, Json);

        _view.CoreWebView2.PostSharedBufferToScript(target, CoreWebView2SharedBufferAccess.ReadOnly, meta);
    }

    /// <summary>
    /// Pushes transport state at a fixed low rate. The page moves its own playhead between pushes, so this only
    /// corrects drift and reports play/pause — and it keeps running while nothing on screen changes.
    /// </summary>
    public void Tick()
    {
        if (!IsActive || _disposed || Player is null || !Player.IsPlaying)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastTransportPushTicks < 250)
        {
            return;
        }

        _lastTransportPushTicks = now;
        PushTransport();
    }

    /// <summary>Sends the current transport state to the page.</summary>
    public void PushTransport()
    {
        if (!IsActive || _disposed || Player is null)
        {
            return;
        }

        Post(new
        {
            type = "transport",
            playing = Player.IsPlaying,
            positionUs = Player.PositionUs,
            durationUs = Player.DurationUs,
            speed = Player.Speed,
            firstUs = Player.FirstTimestampUs,
            lastUs = Player.LastTimestampUs,
        });
    }

    /// <summary>Sends a one-line notice to the page.</summary>
    public void PushNotice(string message) => Post(new { type = "notice", message });

    /// <summary>
    /// The page entered or left fullscreen. Esc lands here too: WebView2 leaves fullscreen on its own when the
    /// user presses it, which is exactly the behaviour a player should have.
    /// </summary>
    private void OnContainsFullScreenElementChanged(object? sender, object e)
    {
        try
        {
            FullScreenChanged?.Invoke(_view.CoreWebView2.ContainsFullScreenElement);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The WebView is going away: there is no window left to resize.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsActive = false;
        try
        {
            if (_view.CoreWebView2 is not null)
            {
                _view.CoreWebView2.ContainsFullScreenElementChanged -= OnContainsFullScreenElementChanged;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Already torn down with the WebView.
        }

        _frameBufferA?.Dispose();
        _frameBufferB?.Dispose();
        _frameBufferA = null;
        _frameBufferB = null;
    }

    private void Post(object payload)
    {
        try
        {
            _view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, Json));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The WebView is going away (window closing): there is nothing left to tell the page.
            IsActive = false;
        }
    }
}
