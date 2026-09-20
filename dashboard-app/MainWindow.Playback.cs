using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ScreenRecall.Dashboard.Services;
using ScreenRecall.Player;

using MessageBox = System.Windows.MessageBox;

namespace ScreenRecall.Dashboard;

/// <summary>Playback, scrubbing, stepping and frame export.</summary>
public partial class MainWindow
{
    private void SeekTo(long timestampUs)
    {
        if (_replayer is null)
        {
            return;
        }

        _replayer.SeekTo(timestampUs);
        RenderCurrentFrame();

        if (!_scrubbing)
        {
            Scrubber.Value = Math.Clamp(_replayer.PositionUs - _replayer.FirstTimestampUs, 0, Scrubber.Maximum);
        }

        PositionText.Text = DateTimeOffset.UnixEpoch.AddTicks(_replayer.PositionUs * 10)
            .LocalDateTime.ToString("HH:mm:ss.fff");
    }

    private void RenderCurrentFrame()
    {
        if (_replayer is null)
        {
            return;
        }

        RenderedFrame frame = _replayer.RenderVirtualDesktop();
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            // Bgr32 ignores the alpha byte: compositor frames are frequently transparent-black outside
            // window content, and honouring that alpha would render them invisible.
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            FrameImage.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Stride, 0);
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_replayer is null)
        {
            return;
        }

        _isPlaying = !_isPlaying;
        PlayButton.Content = _isPlaying ? "⏸ Pause" : "▶ Play";
        if (_isPlaying)
        {
            _playbackTimer?.Start();
        }
        else
        {
            _playbackTimer?.Stop();
        }
    }

    private void AdvancePlayback()
    {
        if (_replayer is null || !_isPlaying || _playbackTimer is null)
        {
            return;
        }

        long step = (long)(_playbackTimer.Interval.TotalMilliseconds * 1000 * _speed);
        long target = _replayer.PositionUs + step;
        if (target >= _replayer.LastTimestampUs)
        {
            target = _replayer.LastTimestampUs;
            _isPlaying = false;
            PlayButton.Content = "▶ Play";
            _playbackTimer.Stop();
        }

        SeekTo(target);
    }

    private void OnSpeedChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _speed = SpeedBox.SelectedIndex switch
        {
            0 => 0.5,
            1 => 1.0,
            2 => 2.0,
            3 => 4.0,
            _ => 8.0,
        };
    }

    private void OnScrubStart(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => _scrubbing = true;

    private void OnScrubEnd(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _scrubbing = false;
        if (_replayer is not null)
        {
            SeekTo(_replayer.FirstTimestampUs + (long)Scrubber.Value);
        }
    }

    private void OnScrub(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_scrubbing || _replayer is null || !IsLoaded)
        {
            return;
        }

        SeekTo(_replayer.FirstTimestampUs + (long)e.NewValue);
    }

    private void OnStepForward(object sender, RoutedEventArgs e)
    {
        if (_replayer is null)
        {
            return;
        }

        long? next = _replayer.StepToNextEvent();
        if (next is null)
        {
            return;
        }

        RenderCurrentFrame();
        Scrubber.Value = Math.Clamp(next.Value - _replayer.FirstTimestampUs, 0, Scrubber.Maximum);
        PositionText.Text = DateTimeOffset.UnixEpoch.AddTicks(next.Value * 10).LocalDateTime.ToString("HH:mm:ss.fff");
    }

    private void OnStepBack(object sender, RoutedEventArgs e)
    {
        if (_replayer is null)
        {
            return;
        }

        // The log is forward-only, so stepping back costs a re-seek — the same trade a video makes when
        // you scrub back before the previous keyframe. One second is close enough for review.
        SeekTo(Math.Max(_replayer.FirstTimestampUs, _replayer.PositionUs - 1_000_000));
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
        if (FocusList.SelectedItem is FocusItem item && _replayer is not null)
        {
            SeekTo(item.StartTs * 1000);
        }
    }

    private void OnSaveFrame(object sender, RoutedEventArgs e)
    {
        if (_replayer is null)
        {
            return;
        }

        RenderedFrame frame = _replayer.RenderVirtualDesktop();
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            $"screen-recall-{_day:yyyy-MM-dd}-{PositionText.Text.Replace(':', '-')}.png");
        PngWriter.Write(path, frame.Width, frame.Height, frame.Bgra);
        MessageBox.Show($"Saved {path}", "Screen Recall", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
