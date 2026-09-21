using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ScreenRecall.Dashboard.Services;
using ScreenRecall.Player;

using MessageBox = System.Windows.MessageBox;

namespace ScreenRecall.Dashboard;

/// <summary>
/// Playback, scrubbing, stepping and frame export. The engine is <see cref="SessionPlayer"/>: a wall-clock
/// playhead over the raw log that renders on the display's own tick, so a quiet stretch of the day holds one
/// still frame for its full duration instead of costing a canvas rebuild per tick.
/// </summary>
public partial class MainWindow
{
    private void SeekTo(long timestampUs)
    {
        if (_player is null)
        {
            return;
        }

        _player.SeekTo(timestampUs);
        RenderCurrentFrame();

        if (!_scrubbing)
        {
            Scrubber.Value = _player.PositionUs - _player.FirstTimestampUs;
        }

        UpdatePositionText();
    }

    /// <summary>
    /// Draws the frame at the playhead. The pixel buffer belongs to the player and is reused on every frame, so
    /// playback does not allocate a screen-sized array sixty times a second.
    /// </summary>
    private void RenderCurrentFrame()
    {
        if (_player is null)
        {
            return;
        }

        RenderedFrame frame = _player.Render();
        _needsRender = false;
        if (_bridge is not null)
        {
            // With the React dashboard up, the WebView owns the pixels: pushing the frame there is the whole
            // render, and drawing the now-hidden WPF image as well would be work nobody ever sees.
            _bridge.PushFrame(frame, _player);
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            // Bgr32 ignores the alpha byte: compositor frames are frequently transparent-black outside
            // window content, and honouring that alpha would render them invisible.
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            FrameImage.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Stride, 0);
    }

    /// <summary>
    /// One display tick: move the playhead by real elapsed time and redraw only when the canvas actually
    /// changed. Held frames cost nothing here, which is what makes a quiet minute cheap to sit through.
    /// </summary>
    private void OnRenderTick()
    {
        if (_player is null || !IsLoaded)
        {
            return;
        }

        PlaybackAdvance advance = _player.Advance();
        _bridge?.Tick();
        if (advance.CanvasChanged || _needsRender)
        {
            RenderCurrentFrame();
        }
        else if (!_player.IsPlaying)
        {
            return;
        }

        SyncPlayheadUi();
    }

    /// <summary>Mirrors the playhead into the scrubber and the clock label.</summary>
    private void SyncPlayheadUi()
    {
        if (_player is null)
        {
            return;
        }

        if (!_scrubbing)
        {
            Scrubber.Value = Math.Clamp(_player.PositionUs - _player.FirstTimestampUs, 0, Scrubber.Maximum);
        }

        UpdatePositionText();
        PlayButton.Content = _player.IsPlaying ? "⏸ Pause" : "▶ Play";
    }

    private void UpdatePositionText()
    {
        if (_player is null)
        {
            return;
        }

        PositionText.Text = DateTimeOffset.UnixEpoch.AddTicks(_player.PositionUs * 10)
            .LocalDateTime.ToString("HH:mm:ss.fff");
        PlaybackInfoText.Text = $"{_player.Speed:0.##}x  ·  {_player.SeekCount} seek(s)";
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_player is null)
        {
            FramePlaceholder.Text = "Select a recorded day first.";
            FramePlaceholder.Visibility = Visibility.Visible;
            return;
        }

        if (_player.IsEmpty)
        {
            FramePlaceholder.Text = "This day holds nothing recorded to play.";
            FramePlaceholder.Visibility = Visibility.Visible;
            return;
        }

        _player.TogglePlay();
        _needsRender = true;
        SyncPlayheadUi();
    }

    private void OnSpeedChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _player?.SetSpeed(SpeedBox.SelectedIndex switch
        {
            0 => 0.5,
            1 => 1.0,
            2 => 2.0,
            3 => 4.0,
            _ => 8.0,
        });

        UpdatePositionText();
    }

    private void OnScrubStart(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => _scrubbing = true;

    private void OnScrubEnd(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _scrubbing = false;
        if (_player is not null)
        {
            SeekTo(_player.FirstTimestampUs + (long)Scrubber.Value);
        }
    }

    private void OnScrub(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_scrubbing || _player is null || !IsLoaded)
        {
            return;
        }

        SeekTo(_player.FirstTimestampUs + (long)e.NewValue);
    }

    private void OnStepForward(object sender, RoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        if (_player.StepForward())
        {
            RenderCurrentFrame();
            SyncPlayheadUi();
        }
    }

    private void OnStepBack(object sender, RoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        // One event, not one second: the log is forward-only, so this costs a re-seek — the same trade a video
        // makes when you scrub back before the previous keyframe.
        if (_player.StepBackward())
        {
            RenderCurrentFrame();
            SyncPlayheadUi();
        }
    }

    /// <summary>Jumps past the next quiet stretch, so a silent hour does not have to be sat through.</summary>
    private void OnSkipIdle(object sender, RoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        IdleSpan? span = _player.IdleSpanAfter(_player.PositionUs);
        if (span is null)
        {
            PlaybackInfoText.Text = "no quiet stretch ahead";
            return;
        }

        SeekTo(span.EndUs);
        PlaybackInfoText.Text = $"skipped {span.DurationUs / 1_000_000.0:0.0}s with no change";
    }

    private void OnPreviousDay(object sender, RoutedEventArgs e) => StepDay(1);

    private void OnNextDay(object sender, RoutedEventArgs e) => StepDay(-1);

    private void StepDay(int direction)
    {
        if (DayList.ItemsSource is not List<DayInfo> days || days.Count == 0)
        {
            return;
        }

        int index = days.FindIndex(day => day.Day == _day.ToString("yyyy-MM-dd"));
        DayList.SelectedIndex = Math.Clamp(index + direction, 0, days.Count - 1);
    }

    private void OnFocusDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FocusList.SelectedItem is FocusItem item && _player is not null)
        {
            SeekTo(item.StartTs * 1000);
        }
    }

    private void OnSaveFrame(object sender, RoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        RenderedFrame frame = _player.Render();
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            $"screen-recall-{_day:yyyy-MM-dd}-{PositionText.Text.Replace(':', '-')}.png");
        PngWriter.Write(path, frame.Width, frame.Height, frame.Bgra);
        MessageBox.Show($"Saved {path}", "Screen Recall", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
