using System.IO;
using System.Text.Json;

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

using ScreenRecall.Player;
using ScreenRecall.Storage;

namespace ScreenRecall.Dashboard.Services;

/// <summary>Command handling for the web dashboard: the page asks, the engine answers.</summary>
public sealed partial class WebDashboardBridge
{
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return;
        }

        try
        {
            Handle(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException
                                      or ArgumentException or InvalidDataException)
        {
            PushNotice($"command failed: {ex.Message}");
        }
    }

    private void Handle(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string command = root.TryGetProperty("cmd", out JsonElement cmd) ? cmd.GetString() ?? string.Empty : string.Empty;

        switch (command)
        {
            case "days":
                PushDays();
                break;
            case "replays":
                PushReplays(DayOf(root));
                break;
            case "open":
                OpenDayFromUi(DayOf(root));
                break;
            case "play":
                Player?.Play();
                PushTransport();
                break;
            case "pause":
                Player?.Pause();
                PushTransport();
                break;
            case "togglePlay":
                Player?.TogglePlay();
                PushTransport();
                break;
            case "seek":
                if (TryLong(root, "positionUs", out long position))
                {
                    Player?.SeekTo(position);
                    PushFrameFromPlayer();
                }

                PushTransport();
                break;
            case "seekProgress":
                if (TryDouble(root, "progress", out double progress))
                {
                    Player?.SeekToProgress(progress);
                    PushFrameFromPlayer();
                }

                PushTransport();
                break;
            case "speed":
                if (TryDouble(root, "value", out double speed))
                {
                    Player?.SetSpeed(speed);
                    PushTransport();
                }

                break;
            case "step":
                Step(root);
                break;
            case "skipIdle":
                SkipIdle();
                break;
            case "idles":
                PushIdles();
                break;
            case "spans":
                PushSpans();
                break;
            case "savePng":
                SavePng();
                break;
            case "status":
                PushStatus();
                break;
            case "config.get":
                PushConfig();
                break;
            case "config.set":
                SaveConfig(root);
                break;
            case "capture.pause":
                SetCapturePaused(true);
                break;
            case "capture.resume":
                SetCapturePaused(false);
                break;
            case "purgeRecent":
                PurgeRecent(root);
                break;
            case "prune":
                Prune();
                break;
            case "openStorage":
                OpenStorage();
                break;
            case "recorder.get":
                PushRecorder();
                break;
            case "recorder.start":
                StartRecorder();
                break;
            case "recorder.stop":
                StopRecorder();
                break;
            case "recorder.startup":
                SetRecorderStartup(root);
                break;
            case "refresh":
                PushDays();
                PushStatus();
                break;
            default:
                PushNotice($"unknown command '{command}'");
                break;
        }
    }

    /// <summary>Sends the frame at the playhead — used after seeks and steps, where pixels must follow immediately.</summary>
    private void PushFrameFromPlayer()
    {
        if (IsActive && Player is not null)
        {
            PushFrame(Player.Render(), Player);
        }
    }

    private void Step(JsonElement root)
    {
        if (Player is null)
        {
            return;
        }

        bool forward = !TryLong(root, "direction", out long direction) || direction >= 0;
        bool moved = forward ? Player.StepForward() : Player.StepBackward();
        if (moved)
        {
            PushFrameFromPlayer();
        }

        PushTransport();
    }

    private void SkipIdle()
    {
        if (Player is null)
        {
            return;
        }

        IdleSpan? span = Player.IdleSpanAfter(Player.PositionUs);
        if (span is null)
        {
            PushNotice("no quiet stretch ahead");
            return;
        }

        Player.SeekTo(span.EndUs);
        PushFrameFromPlayer();
        PushTransport();
        PushNotice($"skipped {span.DurationUs / 1_000_000.0:0.0}s with no change");
    }

    private void SavePng()
    {
        if (Player is null)
        {
            return;
        }

        RenderedFrame frame = Player.Render();
        string stamp = DateTimeOffset.UnixEpoch.AddTicks(Player.PositionUs * 10).LocalDateTime.ToString("HH-mm-ss-fff");
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            $"screen-recall-{Player.Day:yyyy-MM-dd}-{stamp}.png");
        PngWriter.Write(path, frame.Width, frame.Height, frame.Bgra);
        PushNotice($"saved {path}");
    }

    /// <summary>Days list, newest first. Read off the log files only — no tile decoding, no store walk.</summary>
    private void PushDays()
        => PushOffThread(() => new { type = "days", days = DaysFor(_storageRoot()) });

    /// <summary>
    /// The day list for a root, cached briefly.
    ///
    /// Building it is not cheap on a long-running store: every log segment of every day is scanned for its entry count
    /// (a 697 MB day log on this machine) and every day folder is walked for its size. That can outlast the page's
    /// eight-second reply budget, so the first request after startup may lose that race — and without a cache the
    /// user's retry re-ran the whole thing and lost it again, which is why refreshing never produced a list. The first
    /// build is still slow; every request until the window expires is now answered from memory.
    /// </summary>
    private List<DayRow> DaysFor(string root)
    {
        long now = Environment.TickCount64;
        if (_daysCache is not null
            && string.Equals(_daysCacheRoot, root, StringComparison.OrdinalIgnoreCase)
            && now - _daysCacheTicks < DaysCacheMs)
        {
            return _daysCache;
        }

        List<DayRow> rows = BuildDays(root);
        _daysCache = rows;
        _daysCacheRoot = root;
        _daysCacheTicks = now;
        return rows;
    }

    private void PushReplays(string day)
        => PushOffThread(() => new
        {
            type = "replays",
            day,
            checkpoints = CountCheckpoints(day),
            replays = BuildReplays(day),
        });

    private void PushStatus()
        => PushOffThread(() =>
        {
            // The pipe has a 1.5s connect timeout, so this must never run on the UI thread: asking a stopped
            // service for its status would otherwise freeze the window for a second and a half, twice a second.
            CaptureStatus? status = _client.GetStatus();
            return new { type = "status", running = status is not null, status };
        });

    private void PushConfig()
        => PushOffThread(() =>
        {
            RecallConfig? config = _client.GetConfig();
            return config is not null
                ? new { type = "config", config, serviceRunning = true }
                : new
                {
                    type = "config",
                    config = new RecallConfig(),
                    serviceRunning = false,
                    message = "the capture service is not running — showing defaults",
                };
        });

    private void SaveConfig(JsonElement root)
    {
        if (!root.TryGetProperty("config", out JsonElement configElement))
        {
            PushNotice("no config supplied");
            return;
        }

        RecallConfig? config = configElement.Deserialize<RecallConfig>(Json);
        if (config is null)
        {
            PushNotice("config could not be read");
            return;
        }

        RecallConfig normalized = config.Normalize();
        PushOffThread(() =>
        {
            RecallConfig? saved = _client.SetConfig(normalized);
            if (saved is not null)
            {
                return new { type = "config", config = saved, serviceRunning = true };
            }

            // Service not reachable: stage the change in the per-user config so the next start picks it up
            // instead of throwing the user's edits away.
            string path = RecallConfig.UserConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(normalized, Json));
            return new
            {
                type = "notice",
                message = "the capture service is not running — settings saved for the next start",
            };
        });
    }

    private void SetCapturePaused(bool paused)
        => PushOffThread(() =>
        {
            bool ok = _client.SetPaused(paused);
            return new
            {
                type = "notice",
                message = ok
                    ? (paused ? "capture paused" : "capture resumed")
                    : "the capture service is not running",
            };
        });

    private void PurgeRecent(JsonElement root)
    {
        int minutes = TryLong(root, "minutes", out long value) ? (int)Math.Clamp(value, 1, 60 * 24 * 30) : 15;
        PushOffThread(() =>
        {
            PruneInfo? report = _client.PurgeRecent(minutes);
            return report is null
                ? new { type = "notice", message = "the capture service is not running" }
                : new
                {
                    type = "pruned",
                    daysDeleted = report.DaysDeleted,
                    assetsDeleted = report.AssetsDeleted,
                    bytesReclaimed = report.BytesReclaimed,
                    message = report.Describe(),
                };
        });
    }

    private void Prune()
        => PushOffThread(() =>
        {
            PruneInfo? report = _client.Prune();
            return report is null
                ? new { type = "notice", message = "the capture service is not running" }
                : new
                {
                    type = "pruned",
                    daysDeleted = report.DaysDeleted,
                    assetsDeleted = report.AssetsDeleted,
                    bytesReclaimed = report.BytesReclaimed,
                    message = report.Describe(),
                };
        });

    private void OpenStorage()
    {
        string root = _storageRoot();
        try
        {
            Directory.CreateDirectory(root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root) { UseShellExecute = true });
            PushNotice($"opened {root}");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            PushNotice($"could not open {root}: {ex.Message}");
        }
    }

    /* ------------------------------------------------------------------------ */
    /* Recorder process                                                         */
    /* ------------------------------------------------------------------------ */

    /// <summary>
    /// Reports the recorder's process state and its sign-in registration. Deliberately separate from `status`:
    /// the named pipe can only answer while the recorder is up, so "not running" has to be answerable without it.
    /// </summary>
    private void PushRecorder() => PushOffThread(() => BuildRecorderState(null));

    private void StartRecorder()
        => PushOffThread(() =>
        {
            CaptureProcessHost.Start(out string message);
            return BuildRecorderState(message);
        });

    private void StopRecorder()
        // The grace period is why this runs off the UI thread: a recorder with a full write queue takes a moment to
        // drain, and the window must not freeze while it does.
        => PushOffThread(() =>
        {
            CaptureProcessHost.Stop(_client, TimeSpan.FromSeconds(8), out string message);
            return BuildRecorderState(message);
        });

    private void SetRecorderStartup(JsonElement root)
    {
        bool enabled = root.TryGetProperty("enabled", out JsonElement value)
                       && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                       && value.GetBoolean();

        PushOffThread(() =>
        {
            CaptureProcessHost.SetStartsWithWindows(enabled, out string message);
            return BuildRecorderState(message);
        });
    }

    private static object BuildRecorderState(string? message) => new
    {
        type = "recorder",
        running = CaptureProcessHost.IsRunning(),
        startWithWindows = CaptureProcessHost.StartsWithWindows(),
        executable = CaptureProcessHost.FindExecutable(),
        startupCommand = CaptureProcessHost.StartupCommand(),
        message,
    };

    private void PushIdles()
    {
        if (Player is null)
        {
            return;
        }

        List<IdleRow> spans = Player.IdleSpans()
            .Select(span => new IdleRow(span.StartUs, span.EndUs, span.DurationUs))
            .ToList();
        Post(new { type = "idles", day = Player.Day.ToString("yyyy-MM-dd"), spans });
    }

    private void PushSpans()
    {
        if (Player is null)
        {
            return;
        }

        List<SpanRow> spans = Player.Replayer.WindowSpans()
            .Select(span => new SpanRow(span.AppName, span.WindowTitle, span.StartTs, span.EndTs, 0))
            .ToList();
        Post(new { type = "spans", spans });
    }

    private void OpenDayFromUi(string day)
    {
        SessionPlayer? player = OpenDay?.Invoke(day);
        if (player is null)
        {
            Post(new { type = "error", message = $"could not open {day}" });
            return;
        }

        Player = player;
        (int width, int height) = player.VirtualDesktopSize();
        Post(new
        {
            type = "opened",
            day = player.Day.ToString("yyyy-MM-dd"),
            firstUs = player.FirstTimestampUs,
            lastUs = player.LastTimestampUs,
            durationUs = player.DurationUs,
            checkpoints = player.Checkpoints.Count,
            width,
            height,
            replays = BuildReplays(player.Day.ToString("yyyy-MM-dd")),
        });

        PushFrameFromPlayer();
        PushTransport();
        PushIdles();
        PushSpans();
    }

    /// <summary>
    /// Runs the payload builder on a worker and posts the result on the UI thread. Everything the engine answers
    /// with touches the file system or a named pipe, and the UI thread is the one thing the window cannot afford
    /// to block.
    /// </summary>
    private void PushOffThread(Func<object> payload)
    {
        if (_disposed)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            object result;
            try
            {
                result = payload();
            }
            catch (Exception ex)
            {
                result = new { type = "error", message = ex.Message };
            }

            try
            {
                _view.Dispatcher.Invoke(() => Post(result));
            }
            catch (TaskCanceledException)
            {
                // The dispatcher shut down while the work was in flight: the window is gone, drop the answer.
            }
        });
    }

    private List<DayRow> BuildDays(string root)
    {
        List<DayRow> rows = new();
        foreach (DateOnly day in SessionBrowser.Days(root))
        {
            SessionStore store = SessionStore.Open(root, day);
            store.ReloadMeta();

            long first = 0;
            long last = 0;
            long logBytes = 0;
            long entries = 0;
            foreach (string segment in store.LogSegments())
            {
                SessionLogStats stats = SessionLogReader.Scan(segment);
                entries += stats.EntryCount;
                logBytes += stats.FileBytes;
                if (stats.FirstTimestampUs > 0 && (first == 0 || stats.FirstTimestampUs < first))
                {
                    first = stats.FirstTimestampUs;
                }

                if (stats.LastTimestampUs > last)
                {
                    last = stats.LastTimestampUs;
                }
            }

            rows.Add(new DayRow(
                day.ToString("yyyy-MM-dd"),
                first,
                last,
                last > first ? last - first : 0,
                logBytes,
                SessionLayout.SessionBytes(root, day),
                entries,
                CheckpointFormat.List(store.SessionDir).Count,
                store.Meta.AllMonitors().Count));
        }

        return rows;
    }

    private List<ReplayRow> BuildReplays(string day)
        => DateOnly.TryParseExact(day, "yyyy-MM-dd", out DateOnly parsed)
            ? SessionPlayer.Replays(_storageRoot(), parsed)
                .Select(replay => new ReplayRow(
                    replay.FileName,
                    replay.StartUs,
                    replay.EndUs,
                    replay.DurationUs,
                    replay.EntryCount,
                    replay.DistinctWindows,
                    replay.Bytes))
                .ToList()
            : new List<ReplayRow>();

    private int CountCheckpoints(string day)
        => DateOnly.TryParseExact(day, "yyyy-MM-dd", out DateOnly parsed)
            ? CheckpointFormat.List(SessionLayout.CheckpointDir(_storageRoot(), parsed)).Count
            : 0;

    private static string DayOf(JsonElement root)
        => root.TryGetProperty("day", out JsonElement day) && day.ValueKind == JsonValueKind.String
            ? day.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryLong(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt64(out value);
    }

    private static bool TryDouble(JsonElement root, string name, out double value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetDouble(out value);
    }
}
