using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>
/// Everything one video export needs (spec §7's optional video output). The range is in log timestamps —
/// microseconds, the unit the log and the player use everywhere — and everything else is either clamped or
/// normalised by <see cref="VideoExporter.Plan"/>, so a caller can pass what the user typed.
/// </summary>
public sealed record VideoExportOptions(string OutputPath, long FromUs, long ToUs)
{
    /// <summary>Capture cadence of the video: reconstructed frames written per second. Clamped to 1..120.</summary>
    public double Fps { get; init; } = 30;

    /// <summary>Playback speed — 2 exports twice as fast, 0.5 half. Clamped to 0.05..100.</summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>Uniform scale factor, aspect ratio preserved; null keeps the source size (evened for yuv420p).</summary>
    public double? Scale { get; init; }

    /// <summary>Target width in pixels, height follows the aspect ratio; takes precedence over <see cref="Scale"/>.</summary>
    public int? Width { get; init; }

    /// <summary>h264 | h265 | av1, or a raw ffmpeg encoder name (h264_nvenc, libvpx-vp9, ...).</summary>
    public string Codec { get; init; } = "h264";

    /// <summary>Encoder CRF — lower is better and bigger. Clamped to 0..63.</summary>
    public int Crf { get; init; } = 18;

    /// <summary>Encoder preset; null means veryfast for libx264/libx265 and the encoder's own default otherwise.</summary>
    public string? Preset { get; init; }

    /// <summary>Export one monitor; null composes every monitor into one virtual-desktop frame.</summary>
    public ushort? Monitor { get; init; }

    /// <summary>Draw the recorded pointer into the frames (the log carries pointer moves for exactly this).</summary>
    public bool IncludeCursor { get; init; } = true;

    /// <summary>
    /// Chroma format of the encoded video. <c>yuv420p</c> is what every player understands; <c>yuv444p</c> keeps full
    /// chroma resolution, which is what sharpens coloured text and thin UI edges (no chroma subsampling), at a
    /// modest size cost. Anything the encoder accepts is passed through.
    /// </summary>
    public string PixelFormat { get; init; } = "yuv420p";

    /// <summary>
    /// Encoder tune, e.g. <c>stillimage</c> or <c>animation</c> — both help screen content, where most of the frame
    /// stands still and the flat areas are large. Null leaves it to the encoder's defaults.
    /// </summary>
    public string? Tune { get; init; }

    /// <summary>
    /// Bake a "not recorded" card into frames that fall inside a stretch nobody recorded (see
    /// <see cref="SessionActivity"/>). Playback states it with a vector overlay; a video has no DOM, so here it has to
    /// be pixels — otherwise a held frame is indistinguishable from current content, which is the one thing the card
    /// exists to prevent. Turn it off to get the raw held frames.
    /// </summary>
    public bool MarkNotRecorded { get; init; } = true;

    /// <summary>ffmpeg executable; null searches PATH, then C:\ffmpeg\bin\ffmpeg.exe.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Replace an existing output file instead of refusing to touch it.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>
/// A resolved export: range, sizes, frame count, and the exact ffmpeg invocation. Planning is separate from
/// running so a UI or <c>--dry-run</c> can show — and a caller can confirm — the numbers before any file is
/// touched, and so the frame count a progress bar uses is the count that will actually be written.
/// </summary>
public sealed record VideoExportPlan(
    DateOnly Day,
    string OutputPath,
    bool Overwrite,
    long FromUs,
    long ToUs,
    double Fps,
    double Speed,
    ushort? Monitor,
    bool IncludeCursor,
    int SourceWidth,
    int SourceHeight,
    int OutputWidth,
    int OutputHeight,
    long FrameCount,
    string Codec,
    string Encoder,
    int Crf,
    string Preset,
    string FfmpegPath,
    IReadOnlyList<string> Arguments)
{
    /// <summary>Seconds of video this plan produces (frames ÷ fps).</summary>
    public double VideoSeconds => FrameCount / Fps;

    /// <summary>Seconds of recording each output frame consumes (speed ÷ fps).</summary>
    public double StepSeconds => Speed / Fps;

    /// <summary>The full command line, quoted, for display and copy-paste.</summary>
    public string CommandLine => FfmpegPath + " " + string.Join(' ', Arguments.Select(Quote));

    private static string Quote(string argument)
        => argument.Length > 0 && !argument.Contains('"') && !argument.Any(ch => char.IsWhiteSpace(ch))
            ? argument
            : '"' + argument.Replace("\"", "\\\"") + '"';
}

/// <summary>One tick per written frame; the callback fires for every frame and callers throttle their own display.</summary>
public sealed record VideoExportProgress(long FramesWritten, long TotalFrames, long PositionUs, double ElapsedSeconds);

/// <summary>What an export produced, in the units a CLI or GUI reports.</summary>
public sealed record VideoExportResult(
    long Frames,
    double VideoSeconds,
    string OutputPath,
    long OutputBytes,
    TimeSpan Elapsed,
    double AchievedFps,
    long NotRecordedFrames);

/// <summary>
/// Exports a recorded range as a real video file (spec §7's optional video output).
/// </summary>
/// <remarks>
/// The recording stays what it is — a lossless log of reconstructed frames — and this is a *transcode* of a range of
/// it: frames are reconstructed exactly as playback reconstructs them (checkpoint + replay, pointer blended in) and
/// piped as raw BGRA into ffmpeg, which does the encoding. Nothing here re-encodes the store, and nothing here is
/// part of capture: the recorder's tile path is untouched, and every export is reproducible from the log.
///
/// Frames are written at a fixed cadence, so the output is a constant-frame-rate video even where the recording has
/// nothing new to show (a quiet stretch becomes the still frame it is, exactly as playback shows it). ffmpeg is the
/// encoder because it is the one tool on a Windows machine that can produce H.264, H.265 and AV1 with user-chosen
/// quality settings; the exporter is honest about needing it rather than shipping a worse encoder of its own.
/// </remarks>
public static class VideoExporter
{
    /// <summary>Plans an export without touching the file system: range, sizes, frame count and the ffmpeg command.</summary>
    public static VideoExportPlan Plan(string root, DateOnly day, VideoExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using SessionReplayer replayer = new(root, day);
        long first = replayer.FirstTimestampUs;
        long last = replayer.LastTimestampUs;
        if (first == 0 || last <= first)
        {
            throw new InvalidDataException($"{day:yyyy-MM-dd} holds no log entries to export.");
        }

        long fromUs = options.FromUs > 0 ? Math.Max(options.FromUs, first) : first;
        long toUs = options.ToUs > 0 ? Math.Min(options.ToUs, last) : last;
        if (toUs <= fromUs)
        {
            throw new ArgumentException(
                $"The range ends at or before it starts ({fromUs} -> {toUs} us). Widen --from/--to.", nameof(options));
        }

        (int sourceWidth, int sourceHeight) = SourceSize(replayer, options.Monitor);

        double fps = Math.Clamp(options.Fps, 1, 120);
        double speed = Math.Clamp(options.Speed, 0.05, 100);
        long stepUs = Math.Max(1, (long)Math.Round(speed / fps * 1_000_000d));
        long frames = Math.Max(1, (toUs - fromUs) / stepUs);

        (int outputWidth, int outputHeight) = OutputSize(sourceWidth, sourceHeight, options);
        string encoder = ResolveEncoder(options.Codec);
        string pixelFormat = ResolvePixelFormat(options.PixelFormat);
        string? tune = string.IsNullOrWhiteSpace(options.Tune) ? null : options.Tune.Trim();

        // A slower preset is the free half of a quality upgrade: CRF means "this much distortion", so a better
        // encoder setting reaches it in fewer bits — the picture improves and the file usually shrinks. The export
        // loop reconstructs frames in managed code, which costs far more than the encoder does, so the extra encode
        // time is barely visible in the total. `medium` over `veryfast`, not `veryslow`: past a point the gain is
        // fractions of a percent.
        string preset = options.Preset
                        ?? (encoder.StartsWith("libx26", StringComparison.Ordinal) ? "medium" : string.Empty);

        List<string> arguments = new()
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
        };

        if (options.Overwrite)
        {
            arguments.Add("-y");
        }

        arguments.AddRange(new[]
        {
            "-f", "rawvideo",
            "-pixel_format", "bgra",
            "-video_size", $"{sourceWidth}x{sourceHeight}",
            "-framerate", fps.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            "-an",
            "-c:v", encoder,
            "-crf", Math.Clamp(options.Crf, 0, 63).ToString(CultureInfo.InvariantCulture),
        });

        if (preset.Length > 0)
        {
            arguments.Add("-preset");
            arguments.Add(preset);
        }

        if (tune is not null)
        {
            arguments.Add("-tune");
            arguments.Add(tune);
        }

        if (outputWidth != sourceWidth || outputHeight != sourceHeight)
        {
            // Scaling in ffmpeg rather than in managed code: it is the same pixels either way, and this keeps the
            // frame loop allocation-free and honest about what it writes.
            arguments.Add("-vf");
            arguments.Add($"scale={outputWidth}:{outputHeight}:flags=lanczos");
        }

        arguments.AddRange(new[] { "-pix_fmt", pixelFormat, "-movflags", "+faststart", options.OutputPath });

        return new VideoExportPlan(
            day,
            options.OutputPath,
            options.Overwrite,
            fromUs,
            toUs,
            fps,
            speed,
            options.Monitor,
            options.IncludeCursor,
            sourceWidth,
            sourceHeight,
            outputWidth,
            outputHeight,
            frames,
            options.Codec,
            encoder,
            Math.Clamp(options.Crf, 0, 63),
            preset,
            ResolveFfmpeg(options.FfmpegPath),
            arguments);
    }

    /// <summary>
    /// Runs an export. <paramref name="progress"/> fires once per written frame; the caller throttles its own display,
    /// because the producer is a replay loop that can run far faster than real time.
    /// </summary>
    public static VideoExportResult Run(
        string root,
        DateOnly day,
        VideoExportOptions options,
        Action<VideoExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        VideoExportPlan plan = Plan(root, day, options);
        if (File.Exists(plan.OutputPath) && !plan.Overwrite)
        {
            throw new IOException($"{plan.OutputPath} already exists. Pass --overwrite to replace it.");
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(plan.OutputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using SessionReplayer replayer = new(root, day);
        replayer.Renderer.IncludeCursor = plan.IncludeCursor;
        replayer.SeekTo(plan.FromUs);

        // One sequential pass over the day's records (no tiles, no rendering): enough to know which frames are the
        // recorder's and which are a stretch nobody recorded, where a held frame would otherwise pass for content.
        IReadOnlyList<RecordingGap> gaps = options.MarkNotRecorded
            ? SessionActivity.NotRecordedGaps(root, day)
            : Array.Empty<RecordingGap>();
        long notRecorded = 0;

        ProcessStartInfo startInfo = new(plan.FfmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        long stepUs = Math.Max(1, (long)Math.Round(plan.StepSeconds * 1_000_000d));
        byte[]? frameBuffer = null;
        Stopwatch clock = Stopwatch.StartNew();
        long written = 0;

        using Process encoder = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start {plan.FfmpegPath}");

        // stderr is drained on another thread: a chatty encoder that filled its pipe would block, and the export
        // would look like it had hung rather than like an encoder with something to say.
        Task<string> diagnostics = encoder.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            Stream input = encoder.StandardInput.BaseStream;
            for (long index = 0; index < plan.FrameCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long position = plan.FromUs + (index * stepUs);
                replayer.AdvanceTo(position);
                RenderedFrame frame = plan.Monitor is { } monitorId
                    ? replayer.Render(monitorId)
                    : RenderComposed(replayer, ref frameBuffer, plan.OutputWidth, plan.OutputHeight);

                if (gaps.Count > 0 && SessionActivity.GapAt(gaps, position) is { } stretch)
                {
                    NotRecordedSlate.Draw(
                        frame.Bgra,
                        frame.Width,
                        frame.Height,
                        frame.Stride,
                        "NOT RECORDED",
                        NotRecordedLabel(stretch.DurationUs),
                        stretch.Reason.ToUpperInvariant());
                    notRecorded++;
                }

                WriteFrame(input, frame);
                written++;

                progress?.Invoke(new VideoExportProgress(written, plan.FrameCount, position, clock.Elapsed.TotalSeconds));
            }
        }
        catch (IOException)
        {
            // A dead encoder arrives here as a broken pipe. Its own words are in `diagnostics` and are what a caller
            // can act on, so the exit-code check below reports those instead of this exception.
        }
        finally
        {
            try
            {
                encoder.StandardInput.Close();
            }
            catch (IOException)
            {
                // Already gone.
            }
        }

        encoder.WaitForExit();
        if (encoder.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg exited with {encoder.ExitCode}: {LastLines(diagnostics.GetAwaiter().GetResult(), 6)}");
        }

        clock.Stop();
        FileInfo output = new(plan.OutputPath);
        return new VideoExportResult(
            written,
            written / plan.Fps,
            plan.OutputPath,
            output.Exists ? output.Length : 0,
            clock.Elapsed,
            clock.Elapsed.TotalSeconds > 0 ? written / clock.Elapsed.TotalSeconds : 0,
            notRecorded);
    }

    /// <summary>How long the stretch nobody recorded was, in the card's own words.</summary>
    private static string NotRecordedLabel(long durationUs)
    {
        TimeSpan span = TimeSpan.FromSeconds(durationUs / 1_000_000d);
        return span.TotalHours >= 1
            ? $"FOR {span.TotalHours:0}:{span.Minutes:00}:{span.Seconds:00}"
            : $"FOR {span.Minutes}:{span.Seconds:00}";
    }

    /// <summary>
    /// Renders every monitor into one virtual-desktop frame, reusing the caller's buffer. The buffer is kept across
    /// frames on purpose: a 1366x768 frame is 4 MB, and one per frame at 30 fps is 120 MB/s of garbage — the kind of
    /// garbage that turns into a pause in the middle of an export.
    /// </summary>
    private static RenderedFrame RenderComposed(SessionReplayer replayer, ref byte[]? buffer, int fallbackWidth, int fallbackHeight)
    {
        List<MonitorInfo> monitors = replayer.Canvas.Monitors.ToList();
        int width = Math.Max(1, monitors.Count == 0
            ? fallbackWidth
            : monitors.Max(monitor => monitor.X + monitor.Width) - monitors.Min(monitor => monitor.X));
        int height = Math.Max(1, monitors.Count == 0
            ? fallbackHeight
            : monitors.Max(monitor => monitor.Y + monitor.Height) - monitors.Min(monitor => monitor.Y));

        if (buffer is null || buffer.Length < width * height * 4)
        {
            buffer = new byte[width * height * 4];
        }

        return replayer.Renderer.RenderVirtualDesktopInto(replayer.Canvas, buffer);
    }

    /// <summary>
    /// Writes one frame at its own size, row by row, honouring the frame's stride rather than assuming it — the
    /// compositing path hands back a buffer that can be longer than the frame it describes. Scaling is ffmpeg's job
    /// (it was told the input size in the plan), so what is written here is always the reconstructed pixels.
    /// </summary>
    private static void WriteFrame(Stream destination, RenderedFrame frame)
    {
        int rowBytes = frame.Width * 4;
        for (int y = 0; y < frame.Height; y++)
        {
            int offset = y * frame.Stride;
            if (offset + rowBytes > frame.Bgra.Length)
            {
                break; // a frame smaller than it claims: stop rather than write someone else's bytes
            }

            destination.Write(frame.Bgra, offset, rowBytes);
        }
    }

    private static (int Width, int Height) SourceSize(SessionReplayer replayer, ushort? monitorId)
    {
        if (monitorId is { } id)
        {
            MonitorInfo? monitor = replayer.Canvas.Monitor(id);
            if (monitor is null)
            {
                throw new ArgumentException($"monitor {id} is not part of this session.", nameof(monitorId));
            }

            return (monitor.Width, monitor.Height);
        }

        List<MonitorInfo> monitors = replayer.Canvas.Monitors.ToList();
        if (monitors.Count == 0)
        {
            throw new InvalidDataException("this session has no monitor geometry to export.");
        }

        int left = monitors.Min(monitor => monitor.X);
        int top = monitors.Min(monitor => monitor.Y);
        return (Math.Max(1, monitors.Max(monitor => monitor.X + monitor.Width) - left),
            Math.Max(1, monitors.Max(monitor => monitor.Y + monitor.Height) - top));
    }

    /// <summary>
    /// Target size: an explicit width wins, then a scale factor, then the source size. Both ends are evened down
    /// because yuv420p cannot represent an odd dimension, and never below 2.
    /// </summary>
    private static (int Width, int Height) OutputSize(int sourceWidth, int sourceHeight, VideoExportOptions options)
    {
        double factor = 1;
        if (options.Width is { } width && width > 0)
        {
            factor = width / (double)sourceWidth;
        }
        else if (options.Scale is { } scale && scale > 0)
        {
            factor = scale;
        }

        int targetWidth = Math.Max(2, (int)Math.Round(sourceWidth * factor));
        int targetHeight = Math.Max(2, (int)Math.Round(sourceHeight * factor));
        return (targetWidth & ~1, targetHeight & ~1);
    }

    /// <summary>
    /// Normalises the chromatic format: lower case, and back to the universally playable one if it was left blank.
    /// </summary>
    private static string ResolvePixelFormat(string? pixelFormat)
        => string.IsNullOrWhiteSpace(pixelFormat) ? "yuv420p" : pixelFormat.Trim().ToLowerInvariant();

    /// <summary>Maps the friendly codec names to ffmpeg encoders; anything else is passed through as the encoder.</summary>
    private static string ResolveEncoder(string codec)
        => codec.ToLowerInvariant() switch
        {
            "h264" or "avc" or "x264" => "libx264",
            "h265" or "hevc" or "x265" => "libx265",
            "av1" or "svt-av1" or "svtav1" => "libsvtav1",
            "" => "libx264",
            _ => codec,
        };

    /// <summary>
    /// Finds ffmpeg: the caller's path first, then PATH, then the usual install location. A missing encoder deserves
    /// one clear sentence — an export is the only thing in this project that needs a tool it does not ship.
    /// </summary>
    private static string ResolveFfmpeg(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured))
            {
                throw new FileNotFoundException($"ffmpeg was not found at {configured}.", configured);
            }

            return configured;
        }

        string? onPath = SearchPath("ffmpeg.exe");
        if (onPath is not null)
        {
            return onPath;
        }

        const string usual = @"C:\ffmpeg\bin\ffmpeg.exe";
        if (File.Exists(usual))
        {
            return usual;
        }

        throw new FileNotFoundException(
            "ffmpeg was not found. Install it, put it on PATH, or pass --ffmpeg <path to ffmpeg.exe>.");
    }

    private static string? SearchPath(string executable)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim('"'), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing an export over.
            }
        }

        return null;
    }

    /// <summary>The tail of ffmpeg's diagnostics: enough to say what it refused to do, and why.</summary>
    private static string LastLines(string text, int count)
        => string.Join(
            " | ",
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .TakeLast(count));
}
