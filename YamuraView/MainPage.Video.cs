using System.Globalization;
using System.Xml.Linq;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using YamuraView.Core;

namespace YamuraView;

/// <summary>
/// Synced action-camera video: a GoPro (or any camera's) clip plays in the Traction Circle's
/// pane, locked to one run's timeline.
/// <para>
/// Sync is one number per video: <see cref="videoOffset"/>, the run's raw log time at video
/// position 0, so raw time = offset + video position. It comes from, in order: a saved offset
/// for this video file and run (the user's own fine-tuning wins), the GoPro's GPMF GPS clock
/// against the run's GPS UTC anchor (<see cref="RunData.GpsUtcOffsetSeconds"/>), or the run's
/// first timestamp as a placeholder until the user syncs by hand. Manual sync: Unlock Sync so
/// the video and cursor move independently, park the video on a visible event (launch, a
/// cone), put the cursor on the same moment in the data, and Lock Sync. The +/-0.1 s buttons
/// trim it.
/// </para>
/// <para>
/// It works both ways. Moving the chart cursor seeks the video (pausing it), and playing the
/// video moves the cursor in real time. While the video is showing, the Replay controls drive
/// the video instead of the range-relative sweep, so playback runs at true speed.
/// </para>
/// </summary>
public partial class MainPage
{
    private const double VideoFrameStepSeconds = 1.0 / 30.0;
    private const double VideoNudgeSeconds = 0.1;

    private string? videoPath;
    private string? videoRunName;
    private double? videoOffset;
    private string videoSyncSource = "";
    private double videoDurationHint; // from the MP4 header, before the player reports one

    private bool videoPlaying;
    private IDispatcherTimer? videoTimer;
    // last position the player reported and when, so the cursor moves smoothly between the
    // player's (on some platforms infrequent) position updates
    private TimeSpan videoLastPosition;
    private DateTime videoLastPositionAt;

    // while unlocked, the cursor and video move independently (finding a sync point)
    private bool videoSyncUnlocked;

    private bool videoSeekInFlight;
    private TimeSpan? videoPendingSeek;

    // remembered sync offsets per (video file name, run name); file name rather than full
    // path because mobile pickers hand back a fresh cache copy each time
    private readonly Dictionary<(string Video, string Run), double> videoSavedOffsets = new();

    // last mute choice, saved in the config; videos start muted until the user turns sound on
    private bool videoMuted = true;

    private bool VideoLoaded => videoPath != null && videoOffset.HasValue;

    /// <summary>The Replay controls drive the video while one is loaded and on screen.</summary>
    private bool VideoDrivesReplay => VideoLoaded && VideoPane.IsVisible;

    private bool videoEventsHooked;

    private void HookVideoEvents()
    {
        if (videoEventsHooked)
        {
            return;
        }
        videoEventsHooked = true;
        VideoPlayer.PositionChanged += (_, e) =>
        {
            if (videoScrubbing)
            {
                return; // the drag owns the position; a lagging report would yank it back
            }
            videoLastPosition = e.Position;
            videoLastPositionAt = DateTime.UtcNow;
            if (!videoPlaying)
            {
                Dispatcher.Dispatch(UpdateVideoScrubber); // the timer does it while playing
            }
        };
        VideoPlayer.MediaOpened += (_, _) => Dispatcher.Dispatch(() =>
        {
            UpdateVideoStatus();
            VideoFollowCursor(); // show the frame for wherever the cursor already is
            UpdateVideoScrubber(); // the real duration is known now
        });
        VideoPlayer.MediaEnded += (_, _) => Dispatcher.Dispatch(() =>
        {
            if (replayLoop && videoPlaying)
            {
                videoLastPosition = TimeSpan.Zero;
                videoLastPositionAt = DateTime.UtcNow;
                RequestVideoSeek(TimeSpan.Zero);
                VideoPlayer.Play();
                return;
            }
            PauseVideo();
        });
        VideoPlayer.MediaFailed += (_, e) => Dispatcher.Dispatch(async () =>
        {
            AppLogger.Log($"Video failed: {e.ErrorMessage}");
            PauseVideo();
            await DisplayAlertAsync("Video", $"The video couldn't be played: {e.ErrorMessage}", "OK");
        });
        VideoPlayer.StateChanged += (_, e) => Dispatcher.Dispatch(() =>
        {
            // keep our flag honest if the platform pauses playback on its own (focus loss etc.)
            if (videoPlaying && e.NewState is MediaElementState.Paused or MediaElementState.Stopped or MediaElementState.Failed)
            {
                PauseVideo();
            }
        });
    }

    // ===== Video menu =====

    private async void OnVideoMenuClicked(object? sender, EventArgs e)
    {
        const string load = "Load Video";
        const string autoSync = "Auto-Sync (GoPro GPS)";
        const string setOffset = "Set Sync Offset";
        const string showVideo = "Show Video";
        const string showCircle = "Show Traction Circle";
        const string close = "Close Video";

        List<string> options = new() { load };
        if (videoPath != null)
        {
            options.Add(autoSync);
            options.Add(setOffset);
            options.Add(VideoPane.IsVisible ? showCircle : showVideo);
            options.Add(close);
        }

        string choice = await DisplayActionSheetAsync("Video", "Cancel", null, options.ToArray());
        switch (choice)
        {
            case load:
                await LoadVideoAsync();
                break;
            case autoSync:
                await AutoSyncVideoAsync();
                break;
            case setOffset:
                await PromptVideoOffsetAsync();
                break;
            case showVideo:
                ShowVideoPane(true);
                break;
            case showCircle:
                ShowVideoPane(false);
                break;
            case close:
                CloseVideo();
                break;
        }
    }

    private async Task LoadVideoAsync()
    {
        if (dataLogger.runData.Count == 0)
        {
            await DisplayAlertAsync("Video", "Load a log file first, then the video to sync with it.", "OK");
            return;
        }
        string? path;
        try
        {
            path = await VideoFilePicker.PickAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"LoadVideoAsync: picker failed: {ex.Message}");
            await DisplayAlertAsync("Video", ex.Message, "OK");
            return;
        }
        if (path == null)
        {
            return;
        }

        VideoGpsTime gps = await Task.Run(() => GoProGpmf.ReadVideoStartUtc(path));
        AppLogger.Log($"Video {path}: {gps.Message}");
        videoDurationHint = gps.DurationSeconds;

        // a GoPro clip with GPS time picks its own run when exactly one loaded run overlaps it
        List<RunData> overlapping = gps.UtcAtVideoStart is double utc
            ? dataLogger.runData.Where(r => GpsOffsetFor(r, utc) is double o && VideoOverlapsRun(r, o, gps.DurationSeconds)).ToList()
            : new List<RunData>();
        RunData? run = overlapping.Count == 1 ? overlapping[0] : await PickVideoRunAsync(overlapping);
        if (run == null)
        {
            return;
        }

        PauseVideo();
        videoPath = path;
        videoRunName = run.runName;
        string fileName = Path.GetFileName(path);
        if (videoSavedOffsets.TryGetValue((fileName, run.runName), out double saved))
        {
            videoOffset = saved;
            videoSyncSource = "saved";
        }
        else if (gps.UtcAtVideoStart is double startUtc && GpsOffsetFor(run, startUtc) is double gpsOffset)
        {
            videoOffset = gpsOffset;
            videoSyncSource = "GoPro GPS";
        }
        else
        {
            videoOffset = RunRawRange(run)?.Min ?? 0;
            videoSyncSource = "not synced";
        }

        HookVideoEvents();
        videoLastPosition = TimeSpan.Zero;
        videoLastPositionAt = DateTime.UtcNow;
        VideoPlayer.Source = MediaSource.FromFile(path);
        VideoPlayer.Speed = replaySpeed;
        ApplyVideoMute();
        UpdateVideoScrubber(); // header duration until the player reports its own
        ShowVideoPane(true);
        // an unsynced video starts unlocked, ready for the user to find a sync point
        SetVideoSyncUnlocked(videoSyncSource == "not synced");

        if (videoSyncSource == "not synced")
        {
            string why = gps.UtcAtVideoStart.HasValue
                ? $"The video's GPS time doesn't line up with {run.runName}'s GPS time."
                : gps.Message;
            await DisplayAlertAsync("Video",
                $"{why}\n\nTo sync by hand: play or frame-step the video to something you can also find in " +
                "the data (launch, a bump, a cone), put the chart cursor on that same moment, then tap Lock Sync.", "OK");
        }
    }

    /// <summary>Asks which run the video belongs to, offering the GPS-overlapping runs if
    /// there are several, otherwise every loaded run. A lone run is picked without asking.</summary>
    private async Task<RunData?> PickVideoRunAsync(List<RunData> preferred)
    {
        List<RunData> choices = preferred.Count > 1 ? preferred : dataLogger.runData;
        if (choices.Count == 1)
        {
            return choices[0];
        }
        string choice = await DisplayActionSheetAsync("Sync video to which run?", "Cancel", null,
            choices.Select(r => r.runName).ToArray());
        return choices.FirstOrDefault(r => r.runName == choice);
    }

    private async Task AutoSyncVideoAsync()
    {
        if (videoPath == null || FindVideoRun() is not RunData run)
        {
            return;
        }
        string path = videoPath;
        VideoGpsTime gps = await Task.Run(() => GoProGpmf.ReadVideoStartUtc(path));
        if (gps.UtcAtVideoStart is not double utc)
        {
            await DisplayAlertAsync("Auto-Sync", gps.Message, "OK");
            return;
        }
        if (GpsOffsetFor(run, utc) is not double offset)
        {
            await DisplayAlertAsync("Auto-Sync", $"{run.runName} has no GPS time to sync against.", "OK");
            return;
        }
        double duration = VideoDurationSeconds();
        if (!VideoOverlapsRun(run, offset, duration))
        {
            (float min, float max) = RunRawRange(run) ?? (0f, 0f);
            double gap = offset > max ? offset - max : min - (offset + duration);
            string when = offset > max ? "after the run ends" : "before the run starts";
            await DisplayAlertAsync("Auto-Sync",
                $"By GPS time this video is {FormatDuration(gap)} {when} - it doesn't cover {run.runName}.", "OK");
            return;
        }
        SetVideoOffset(offset, "GoPro GPS");
        await DisplayAlertAsync("Auto-Sync", $"Synced to {run.runName} by GPS time ({gps.SampleCount} samples).", "OK");
    }

    private async Task PromptVideoOffsetAsync()
    {
        if (!VideoLoaded)
        {
            return;
        }
        string? text = await DisplayPromptAsync("Sync Offset",
            $"Log time (s, raw {videoRunName} time) at the start of the video:",
            "OK", "Cancel", initialValue: videoOffset!.Value.ToString("0.000", CultureInfo.InvariantCulture),
            keyboard: Keyboard.Numeric);
        if (text != null &&
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double offset))
        {
            SetVideoOffset(offset, "manual");
        }
    }

    private void ShowVideoPane(bool show)
    {
        if (!show)
        {
            PauseVideo();
        }
        VideoPane.IsVisible = show;
        TractionCircleView.IsVisible = !show;
    }

    private void CloseVideo()
    {
        PauseVideo();
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        VideoFilePicker.Release();
        videoPath = null;
        videoRunName = null;
        videoOffset = null;
        SetVideoSyncUnlocked(false);
        ShowVideoPane(false);
    }

    // ===== Sync controls =====

    /// <summary>
    /// Unlock: the video and the cursor move independently, so you can find the same moment
    /// in each (frame-step or play the video, scrub the chart). Lock: that pair becomes the
    /// sync - offset = the cursor's raw time minus the video position.
    /// </summary>
    private void OnVideoSyncLockClicked(object? sender, EventArgs e)
    {
        if (!VideoLoaded)
        {
            return;
        }
        if (!videoSyncUnlocked)
        {
            SetVideoSyncUnlocked(true);
            return;
        }
        if (stripChartDrawable.CursorTime is not float cursor ||
            !stripChartDrawable.GetPerRunCursorTimes(cursor).TryGetValue(videoRunName!, out float rawCursor))
        {
            VideoStatusLabel.Text = "Put the cursor on the moment the video shows, then tap Lock Sync.";
            return;
        }
        PauseVideo();
        SetVideoSyncUnlocked(false);
        SetVideoOffset(rawCursor - CurrentVideoPosition().TotalSeconds, "manual");
    }

    private void SetVideoSyncUnlocked(bool unlocked)
    {
        videoSyncUnlocked = unlocked;
        VideoSyncButton.Text = unlocked ? "Lock Sync" : "Unlock Sync";
        UpdateVideoStatus();
    }

    private void OnVideoNudgeBackClicked(object? sender, EventArgs e) => NudgeVideoOffset(-VideoNudgeSeconds);
    private void OnVideoNudgeForwardClicked(object? sender, EventArgs e) => NudgeVideoOffset(+VideoNudgeSeconds);

    /// <summary>Trims the sync; the video frame stays put and the cursor moves to match.</summary>
    private void NudgeVideoOffset(double seconds)
    {
        if (!VideoLoaded)
        {
            return;
        }
        SetVideoOffset(videoOffset!.Value + seconds, "manual");
    }

    private void SetVideoOffset(double offset, string source)
    {
        videoOffset = offset;
        videoSyncSource = source;
        if (videoPath != null && videoRunName != null)
        {
            videoSavedOffsets[(Path.GetFileName(videoPath), videoRunName)] = offset;
            SaveConfig();
        }
        UpdateVideoStatus();
        if (!videoPlaying)
        {
            MoveCursorToVideo(CurrentVideoPosition());
        }
    }

    private void OnVideoMuteClicked(object? sender, EventArgs e)
    {
        videoMuted = !videoMuted;
        ApplyVideoMute();
        SaveConfig(); // remembered across sessions
    }

    private void ApplyVideoMute()
    {
        VideoPlayer.ShouldMute = videoMuted;
        VideoMuteButton.Text = videoMuted ? "🔇" : "🔊";
    }

    // ===== Playback =====

    private void OnVideoPlayPauseClicked(object? sender, EventArgs e) => ToggleVideoPlayback();

    private void ToggleVideoPlayback()
    {
        if (!VideoLoaded)
        {
            return;
        }
        if (videoPlaying)
        {
            PauseVideo();
            return;
        }
        VideoPlayer.Speed = replaySpeed;
        videoLastPosition = VideoPlayer.Position;
        videoLastPositionAt = DateTime.UtcNow;
        VideoPlayer.Play();
        videoPlaying = true;
        VideoPlayPauseButton.Text = "⏸";
        ReplayPlayPauseButton.Text = "⏸";
        videoTimer ??= CreateVideoTimer();
        videoTimer.Start();
    }

    private void PauseVideo()
    {
        if (!videoPlaying)
        {
            return;
        }
        videoPlaying = false;
        videoTimer?.Stop();
        VideoPlayer.Pause();
        videoLastPosition = VideoPlayer.Position;
        videoLastPositionAt = DateTime.UtcNow;
        VideoPlayPauseButton.Text = "▶";
        ReplayPlayPauseButton.Text = "▶";
    }

    private IDispatcherTimer CreateVideoTimer()
    {
        IDispatcherTimer timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(ReplayFrameMs);
        timer.Tick += (_, _) =>
        {
            if (videoPlaying)
            {
                MoveCursorToVideo(CurrentVideoPosition());
                UpdateVideoScrubber();
            }
        };
        return timer;
    }

    private void OnVideoFrameBackClicked(object? sender, EventArgs e) => StepVideo(-VideoFrameStepSeconds);
    private void OnVideoFrameForwardClicked(object? sender, EventArgs e) => StepVideo(+VideoFrameStepSeconds);

    private void StepVideo(double seconds)
    {
        if (!VideoLoaded)
        {
            return;
        }
        PauseVideo();
        TimeSpan target = ClampVideoPosition(CurrentVideoPosition().TotalSeconds + seconds);
        videoLastPosition = target;
        videoLastPositionAt = DateTime.UtcNow;
        RequestVideoSeek(target);
        MoveCursorToVideo(target);
        UpdateVideoScrubber();
    }

    // ===== Scrub bar =====

    private bool videoScrubberUpdating; // set while code (not the user) moves the slider
    private bool videoScrubbing;        // a drag is in progress
    private bool videoResumeAfterScrub;

    /// <summary>Puts the scrub bar at the video's current position (and its range at the
    /// video's length). Left alone mid-drag so it doesn't fight the user's finger.</summary>
    private void UpdateVideoScrubber()
    {
        if (videoScrubbing)
        {
            return;
        }
        videoScrubberUpdating = true;
        try
        {
            double duration = VideoDurationSeconds();
            if (duration > 0 && VideoScrubber.Maximum != duration)
            {
                VideoScrubber.Maximum = duration;
            }
            VideoScrubber.Value = Math.Min(CurrentVideoPosition().TotalSeconds, VideoScrubber.Maximum);
        }
        finally
        {
            videoScrubberUpdating = false;
        }
    }

    private void OnVideoScrubberDragStarted(object? sender, EventArgs e)
    {
        videoScrubbing = true;
        // playback resumes after the drag if it was playing when the drag began
        videoResumeAfterScrub = videoPlaying;
        PauseVideo();
    }

    private void OnVideoScrubberDragCompleted(object? sender, EventArgs e)
    {
        videoScrubbing = false;
        if (videoResumeAfterScrub)
        {
            videoResumeAfterScrub = false;
            ToggleVideoPlayback();
        }
    }

    /// <summary>The user dragged or tapped the scrub bar: seek there, and while sync is
    /// locked carry the cursor along (unlocked, only the video moves - for finding a sync
    /// point).</summary>
    private void OnVideoScrubberValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (videoScrubberUpdating || !VideoLoaded)
        {
            return;
        }
        PauseVideo();
        TimeSpan target = ClampVideoPosition(e.NewValue);
        videoLastPosition = target;
        videoLastPositionAt = DateTime.UtcNow;
        RequestVideoSeek(target);
        MoveCursorToVideo(target);
    }

    /// <summary>Where the video is now: the last reported position, carried forward by the
    /// time since while playing.</summary>
    private TimeSpan CurrentVideoPosition()
    {
        if (!videoPlaying)
        {
            return videoLastPosition;
        }
        double elapsed = (DateTime.UtcNow - videoLastPositionAt).TotalSeconds * VideoPlayer.Speed;
        return ClampVideoPosition(videoLastPosition.TotalSeconds + elapsed);
    }

    private double VideoDurationSeconds()
    {
        double reported = VideoPlayer.Duration.TotalSeconds;
        return reported > 0 ? reported : videoDurationHint;
    }

    private TimeSpan ClampVideoPosition(double seconds)
    {
        double duration = VideoDurationSeconds();
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, duration > 0 ? duration : double.MaxValue));
    }

    // ===== Cursor <-> video =====

    /// <summary>Video -> data: puts the chart cursor on the video's current moment.</summary>
    private void MoveCursorToVideo(TimeSpan position)
    {
        if (!VideoLoaded || videoSyncUnlocked || FindVideoRun() is not RunData run)
        {
            return;
        }
        float raw = (float)(videoOffset!.Value + position.TotalSeconds);
        if (stripChartDrawable.AxisValueForRawTime(run, raw) is float axisValue)
        {
            // zoomed in: slide the window along with the video so the cursor keeps its
            // place on screen instead of running off the edge
            PanZoomWindowWithCursor(stripChartDrawable.CursorTime, axisValue);
            SetCursorTime(axisValue, fromVideo: true);
        }
    }

    /// <summary>Data -> video: called when the user moves the cursor; seeks the video to
    /// the same moment (pausing it - a scrub means "show me this frame").</summary>
    private void VideoFollowCursor(IReadOnlyDictionary<string, float>? cursorTimes = null)
    {
        if (!VideoLoaded || videoSyncUnlocked)
        {
            return;
        }
        if (cursorTimes == null)
        {
            if (stripChartDrawable.CursorTime is not float cursor)
            {
                return;
            }
            cursorTimes = stripChartDrawable.GetPerRunCursorTimes(cursor);
        }
        if (!cursorTimes.TryGetValue(videoRunName!, out float raw))
        {
            return;
        }
        PauseVideo();
        TimeSpan target = ClampVideoPosition(raw - videoOffset!.Value);
        videoLastPosition = target;
        videoLastPositionAt = DateTime.UtcNow;
        RequestVideoSeek(target);
        UpdateVideoScrubber();
    }

    /// <summary>Seeks without piling up requests: while a seek is running only the latest
    /// target is kept, so a fast scrub lands on where the cursor ended up.</summary>
    private async void RequestVideoSeek(TimeSpan target)
    {
        videoPendingSeek = target;
        if (videoSeekInFlight)
        {
            return;
        }
        videoSeekInFlight = true;
        try
        {
            while (videoPendingSeek is TimeSpan next)
            {
                videoPendingSeek = null;
                // some platforms never signal completion for a seek on a not-yet-opened video
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
                await VideoPlayer.SeekTo(next, timeout.Token);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Video seek: {ex.Message}");
        }
        finally
        {
            videoSeekInFlight = false;
        }
    }

    // ===== Helpers =====

    private RunData? FindVideoRun() => dataLogger.runData.FirstOrDefault(r => r.runName == videoRunName);

    /// <summary>Raw log time at video start for a video whose first frame is at
    /// <paramref name="videoStartUtc"/>, or null when the run has no GPS clock.</summary>
    private static double? GpsOffsetFor(RunData run, double videoStartUtc) =>
        double.IsNaN(run.GpsUtcOffsetSeconds) ? null : videoStartUtc - run.GpsUtcOffsetSeconds;

    private static (float Min, float Max)? RunRawRange(RunData run)
    {
        if (!run.channels.TryGetValue("Time", out DataChannel? time) || time.DataPoints.Count == 0)
        {
            return null;
        }
        return (time.DataPoints.Keys[0], time.DataPoints.Keys[^1]);
    }

    private static bool VideoOverlapsRun(RunData run, double offset, double duration) =>
        RunRawRange(run) is (float min, float max) && offset <= max && offset + Math.Max(duration, 0) >= min;

    private static string FormatDuration(double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Abs(seconds));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
             : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s"
             : $"{span.TotalSeconds:0.0}s";
    }

    private void UpdateVideoStatus()
    {
        if (!VideoLoaded)
        {
            VideoStatusLabel.Text = "";
            return;
        }
        VideoStatusLabel.Text = videoSyncUnlocked
            ? "Unlocked: find the same moment in the video and the data, then Lock Sync"
            : $"{Path.GetFileName(videoPath)} → {videoRunName} · start = {videoOffset!.Value:0.000}s ({videoSyncSource})";
    }

    /// <summary>A run being unloaded takes its video with it.</summary>
    private void OnRunsRemovedForVideo(HashSet<string> names)
    {
        if (videoRunName != null && names.Contains(videoRunName))
        {
            CloseVideo();
        }
    }

    // ===== Config =====

    private XElement BuildVideoConfigElement() =>
        new("Video", new XAttribute("Muted", videoMuted), videoSavedOffsets.OrderBy(kv => kv.Key.Video).ThenBy(kv => kv.Key.Run).Select(kv =>
            new XElement("Sync",
                new XAttribute("File", kv.Key.Video),
                new XAttribute("Run", kv.Key.Run),
                new XAttribute("Offset", kv.Value.ToString("R", CultureInfo.InvariantCulture)))));

    private void LoadVideoConfig(XElement root)
    {
        videoSavedOffsets.Clear();
        if (bool.TryParse((string?)root.Element("Video")?.Attribute("Muted"), out bool muted))
        {
            videoMuted = muted; // applied to the player when a video is loaded
        }
        foreach (XElement sync in root.Element("Video")?.Elements("Sync") ?? Enumerable.Empty<XElement>())
        {
            string? file = (string?)sync.Attribute("File");
            string? run = (string?)sync.Attribute("Run");
            if (!string.IsNullOrEmpty(file) && !string.IsNullOrEmpty(run) &&
                double.TryParse((string?)sync.Attribute("Offset"), NumberStyles.Float, CultureInfo.InvariantCulture, out double offset))
            {
                videoSavedOffsets[(file, run)] = offset;
            }
        }
    }
}
