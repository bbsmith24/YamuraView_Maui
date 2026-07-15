using System.Xml.Linq;
using YamuraView.Core;

namespace YamuraView;

public partial class MainPage : ContentPage
{
    private static readonly HashSet<string> NonSelectableChannels = new() { "Time", "Latitude", "Longitude" };
    private const char KeySeparator = '\u001F'; // ASCII unit separator - won't collide with real names

    private readonly DataLogger dataLogger = new();
    private readonly LogFileParser parser = new();
    private readonly AppSettings settings;

    private readonly StripChartDrawable stripChartDrawable;
    private readonly XYChartDrawable trackMapDrawable;
    private readonly XYChartDrawable tractionCircleDrawable;

    // per-graph selection state: strip chart is selected per (run, channel); the XY charts
    // have a user-selectable channel pair (X/Y axis, defaulted below), plus which runs to
    // include - all "by graph, by run" like the WinForms app's per-chart channel tree.
    private readonly HashSet<(string RunName, string ChannelName)> stripChartSelection = new();
    private readonly HashSet<string> trackMapSelectedRuns = new();
    private readonly HashSet<string> tractionCircleSelectedRuns = new();

    // which stacked Strip Chart subgraph band (0-based) each channel name plots in - matches
    // the WinForms app's "Assign to Graph" feature; a channel not present here plots in band 0
    private readonly Dictionary<string, int> stripChartChannelGraphIndex = new();

    // trace color for one specific (run, channel) pair, overriding the normal per-run auto
    // color; a pair not present here keeps the default per-run auto color
    private readonly Dictionary<(string RunName, string ChannelName), Color> stripChartChannelColorOverride = new();

    // channel names whose trace is flipped vertically on the Y axis, across every run at once -
    // matches the WinForms app's "Invert" tree context menu item
    private readonly HashSet<string> stripChartInvertedChannels = new();

    // trace pen width per channel name, across every run at once; a channel not present here
    // draws at StripChartDrawable.DefaultPenWidth
    private readonly Dictionary<string, float> stripChartChannelPenWidth = new();

    // channel names whose group starts expanded in the Strip Chart channel picker - groups
    // default to collapsed; remembered from the last time the picker was applied (and across
    // restarts via config)
    private readonly HashSet<string> stripChartExpandedChannels = new();

    // per-run trace overrides for the XY charts (color/invert/pen width, set from their run
    // pickers) - keyed by run name, independent per chart
    private readonly Dictionary<string, Color> trackMapRunColor = new();
    private readonly HashSet<string> trackMapInvertedRuns = new();
    private readonly Dictionary<string, float> trackMapRunPenWidth = new();
    private readonly Dictionary<string, Color> tractionCircleRunColor = new();
    private readonly HashSet<string> tractionCircleInvertedRuns = new();
    private readonly Dictionary<string, float> tractionCircleRunPenWidth = new();

    // default channel names applied to a run's first appearance on the Strip Chart; starts
    // out as gX/gY/gZ but is overwritten by whatever the config file remembers from last time
    private HashSet<string> stripChartDefaultChannelNames = new() { "gX", "gY", "gZ" };

    private string stripChartXAxis = StripChartDrawable.TimeAxis;
    private string trackMapXAxis = "Longitude";
    private string trackMapYAxis = "Latitude";
    private string tractionCircleXAxis = "gX";
    private string tractionCircleYAxis = "gY";

    // line vs. point trace display - set independently per chart
    private ChartDisplayMode stripChartDisplayMode = ChartDisplayMode.Line;
    private ChartDisplayMode trackMapDisplayMode = ChartDisplayMode.Line;
    private ChartDisplayMode tractionCircleDisplayMode = ChartDisplayMode.Line;

    private bool stripChartCustomized;
    private bool trackMapCustomized;
    private bool tractionCircleCustomized;

    // full paths already seen in the autoload folder - seeded with whatever's already there
    // when autoload (re)starts, so only files that show up afterward get auto-loaded, matching
    // the WinForms app's FolderToWatchFiles behavior
    private static readonly string[] AutoloadExtensions = { ".txt", ".ylg", ".yl5" };
    private readonly HashSet<string> autoloadKnownFiles = new(StringComparer.OrdinalIgnoreCase);
    private IDispatcherTimer? autoloadTimer;

    public MainPage()
    {
        InitializeComponent();
        AppLogger.Init();
        AppLogger.Log("Application started");
        parser.Log = message => AppLogger.Log(message);

        settings = AppSettings.Load();
        LoadConfigIfPresent();

        stripChartDrawable = new StripChartDrawable
        {
            DataLogger = dataLogger,
            SelectedSeries = stripChartSelection,
            XAxisChannel = stripChartXAxis,
            ChannelGraphIndex = stripChartChannelGraphIndex,
            ChannelColorOverride = stripChartChannelColorOverride,
            InvertedChannels = stripChartInvertedChannels,
            ChannelPenWidth = stripChartChannelPenWidth,
            DisplayMode = stripChartDisplayMode
        };
        trackMapDrawable = new XYChartDrawable
        {
            DataLogger = dataLogger,
            XChannel = trackMapXAxis,
            YChannel = trackMapYAxis,
            EqualScale = true,
            SelectedRuns = trackMapSelectedRuns,
            DisplayMode = trackMapDisplayMode,
            RunColorOverride = trackMapRunColor,
            InvertedRuns = trackMapInvertedRuns,
            RunPenWidth = trackMapRunPenWidth
        };
        tractionCircleDrawable = new XYChartDrawable
        {
            DataLogger = dataLogger,
            XChannel = tractionCircleXAxis,
            YChannel = tractionCircleYAxis,
            EqualScale = true,
            SelectedRuns = tractionCircleSelectedRuns,
            DisplayMode = tractionCircleDisplayMode,
            RunColorOverride = tractionCircleRunColor,
            InvertedRuns = tractionCircleInvertedRuns,
            RunPenWidth = tractionCircleRunPenWidth
        };

        StripChartView.Drawable = stripChartDrawable;
        TrackMapView.Drawable = trackMapDrawable;
        TractionCircleView.Drawable = tractionCircleDrawable;

        // Strip Chart drives the cursor (vertical line); Track Map and Traction Circle just
        // track it (box cursor at the nearest point in time) - same relationship as the
        // WinForms app's VERTICAL/BOX cursor modes. Touch and mouse work differently:
        // one finger (or pen) always scrubs the cursor and a two-finger pinch zooms the
        // X window, while a mouse keeps hover-scrub plus the press-drag zoom band
        // (a mouse can't pinch).
        PointerGestureRecognizer stripChartPointer = new();
        stripChartPointer.PointerPressed += (_, e) => OnStripChartPointerPressed(e);
        stripChartPointer.PointerMoved += (_, e) => OnStripChartPointerMoved(e);
        stripChartPointer.PointerReleased += (_, e) => OnStripChartPointerReleased(e);
        stripChartPointer.PointerExited += (_, _) => OnStripChartPointerExited();
        StripChartView.GestureRecognizers.Add(stripChartPointer);

        PinchGestureRecognizer stripChartPinch = new();
        stripChartPinch.PinchUpdated += OnStripChartPinchUpdated;
        StripChartView.GestureRecognizers.Add(stripChartPinch);

        PanGestureRecognizer stripChartPan = new();
        stripChartPan.PanUpdated += OnStripChartPanUpdated;
        StripChartView.GestureRecognizers.Add(stripChartPan);

        // tap is the only touch input that reports an absolute position on Android (touch
        // raises no pointer events there - pointer gestures are mouse/stylus-only), so it
        // places the cursor outright on every platform
        TapGestureRecognizer stripChartTap = new();
        stripChartTap.Tapped += (_, e) => OnStripChartTapped(e.GetPosition(StripChartView));
        StripChartView.GestureRecognizers.Add(stripChartTap);

        StartAutoload();
    }

    /// <summary>
    /// (Re)starts watching the configured autoload folder, matching the WinForms app's
    /// checkAutoAddTimer: every 30 seconds, load the first not-yet-seen .txt/.ylg/.yl5 file
    /// found there. Files already present when this starts are marked as seen (not loaded),
    /// so only files that show up afterward get picked up. Called once at startup and again
    /// whenever the Settings page changes the autoload folder.
    /// </summary>
    private void StartAutoload()
    {
        autoloadTimer?.Stop();
        autoloadKnownFiles.Clear();

        if (string.IsNullOrWhiteSpace(settings.AutoloadFolderPath) || !Directory.Exists(settings.AutoloadFolderPath))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(settings.AutoloadFolderPath))
        {
            autoloadKnownFiles.Add(file);
        }

        autoloadTimer ??= Dispatcher.CreateTimer();
        autoloadTimer.Interval = TimeSpan.FromSeconds(30);
        autoloadTimer.Tick -= OnAutoloadTimerTick;
        autoloadTimer.Tick += OnAutoloadTimerTick;
        autoloadTimer.Start();
    }

    /// <summary>
    /// Loads at most one new file per tick (same as the WinForms app - keeps a single locked/
    /// mid-upload file from blocking the rest, and naturally spreads loads across ticks). A
    /// file that can't be opened yet (e.g. still being written) is left unmarked so it's
    /// retried on a later tick instead of being skipped forever.
    /// </summary>
    private async void OnAutoloadTimerTick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(settings.AutoloadFolderPath) || !Directory.Exists(settings.AutoloadFolderPath))
        {
            return;
        }

        string? newFile = null;
        foreach (string file in Directory.GetFiles(settings.AutoloadFolderPath))
        {
            if (!autoloadKnownFiles.Contains(file) && AutoloadExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                newFile = file;
                break;
            }
        }
        if (newFile == null)
        {
            AppLogger.Log($"Autoload: checked {settings.AutoloadFolderPath}, no new files found");
            return;
        }

        try
        {
            using FileStream testOpen = File.Open(newFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException ex)
        {
            AppLogger.Log($"Autoload: {newFile} not ready yet, will retry: {ex.Message}");
            return;
        }

        autoloadKnownFiles.Add(newFile);
        string? warning = await ParseFileAsync(newFile);
        RefreshSelectionDefaults();
        RefreshCharts();
        if (!string.IsNullOrWhiteSpace(warning))
        {
            await DisplayAlertAsync("Parse Warnings", SummarizeWarnings(new[] { warning }), "OK");
        }
    }

    private const float MinDragPixels = 6;

    /// <summary>
    /// What a press on the Strip Chart is doing. Touch has no hover, so one finger (or pen)
    /// always scrubs the cursor (TouchScrub) and zooming is a two-finger pinch instead; only
    /// a real mouse press starts the drag-to-zoom band (MouseZoomDrag), since a mouse can't
    /// pinch and still has hover for scrubbing.
    /// </summary>
    private enum StripChartGesture { None, TouchScrub, MouseZoomDrag }

    private StripChartGesture stripChartGesture = StripChartGesture.None;

    /// <summary>True from a pinch's Started until its Completed/Canceled - single-pointer
    /// handlers stand down so the two pinching fingers don't also scrub the cursor.</summary>
    private bool stripChartPinchActive;

    /// <summary>Pixel X where the current touch press landed - the anchor the pan-driven
    /// scrub offsets from, since pan events report translation, not position.</summary>
    private float stripChartTouchStartPixelX;

    // pinch-start snapshot: the visible X window and the axis value under the pinch center
    // (as a value + its fraction across the window), so each update rescales from a stable
    // baseline instead of compounding rounding on the live window
    private float pinchStartWidth;
    private float pinchAnchorAxis;
    private float pinchAnchorFraction;
    private float pinchTotalScale;

    /// <summary>Touch and pen scrub the cursor; only a real mouse gets the drag-zoom band.
    /// Non-Windows platforms are touch-first, so everything scrubs there.</summary>
    private static bool IsTouchLikePointer(PointerEventArgs e)
    {
#if WINDOWS
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs? platformArgs = e.PlatformArgs?.PointerRoutedEventArgs;
        return platformArgs != null
            && platformArgs.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse;
#else
        return true;
#endif
    }

    private void OnStripChartPointerPressed(PointerEventArgs e)
    {
        if (stripChartPinchActive)
        {
            return;
        }
        Point? position = e.GetPosition(StripChartView);
        if (!position.HasValue)
        {
            return;
        }
        if (IsTouchLikePointer(e))
        {
            // the cursor jumps to the finger immediately, so a plain tap places it too;
            // the press pixel anchors the pan-driven scrub (see OnStripChartPanUpdated)
            stripChartGesture = StripChartGesture.TouchScrub;
            stripChartTouchStartPixelX = (float)position.Value.X;
            SetCursorTime(stripChartDrawable.PixelXToTime(stripChartTouchStartPixelX));
        }
        else
        {
            stripChartGesture = StripChartGesture.MouseZoomDrag;
            stripChartDrawable.DragStartPixelX = (float)position.Value.X;
            stripChartDrawable.DragCurrentPixelX = (float)position.Value.X;
        }
    }

    private void OnStripChartPointerMoved(PointerEventArgs e)
    {
        if (stripChartPinchActive)
        {
            return;
        }
        Point? position = e.GetPosition(StripChartView);
        switch (stripChartGesture)
        {
            case StripChartGesture.MouseZoomDrag:
                if (position.HasValue)
                {
                    stripChartDrawable.DragCurrentPixelX = (float)position.Value.X;
                }
                RefreshCharts();
                break;
            case StripChartGesture.TouchScrub:
            case StripChartGesture.None:
                // None = mouse hovering with no press - the cursor tracks the pointer
                // either way
                SetCursorTime(position.HasValue ? stripChartDrawable.PixelXToTime((float)position.Value.X) : null);
                break;
        }
    }

    private void OnStripChartPointerReleased(PointerEventArgs e)
    {
        if (stripChartGesture == StripChartGesture.MouseZoomDrag)
        {
            FinishDragZoom(e.GetPosition(StripChartView));
        }
        stripChartGesture = StripChartGesture.None;
    }

    private void OnStripChartPointerExited()
    {
        CancelDragZoom();
        stripChartGesture = StripChartGesture.None;
        // deliberately keeps the cursor where it was rather than clearing it: on touch,
        // lifting the finger exits the control, and clearing here would erase the cursor
        // the user just placed (a side effect: leaving with the mouse also freezes the
        // cursor at its last position instead of hiding it, which keeps the readouts up)
    }

    private void OnStripChartTapped(Point? position)
    {
        if (stripChartPinchActive || !position.HasValue)
        {
            return;
        }
        SetCursorTime(stripChartDrawable.PixelXToTime((float)position.Value.X));
    }

    /// <summary>
    /// One-finger drag scrub for touch, driven by the pan gesture because raw pointer-move
    /// events aren't available during touch drags: on Windows the pinch recognizer's
    /// manipulation mode swallows them mid-drag, and on Android touch raises no pointer
    /// events at all. On Windows the pointer press has already anchored the scrub at the
    /// finger (the cursor jumped there); on Android the pan itself starts the scrub,
    /// anchored at the current cursor so a drag slides it from where it was (tap places it
    /// outright). Mouse drags don't produce pan events, and the state checks keep this from
    /// fighting the mouse zoom band or a pinch.
    /// </summary>
    private void OnStripChartPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (stripChartPinchActive || stripChartGesture == StripChartGesture.MouseZoomDrag)
        {
            return;
        }
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                if (stripChartGesture != StripChartGesture.TouchScrub)
                {
                    stripChartGesture = StripChartGesture.TouchScrub;
                    float? anchor = stripChartDrawable.CursorTime.HasValue
                        ? stripChartDrawable.TimeToPixelX(stripChartDrawable.CursorTime.Value)
                        : null;
                    stripChartTouchStartPixelX = anchor ?? (float)(StripChartView.Width / 2);
                }
                break;
            case GestureStatus.Running:
                if (stripChartGesture == StripChartGesture.TouchScrub)
                {
                    SetCursorTime(stripChartDrawable.PixelXToTime(stripChartTouchStartPixelX + (float)e.TotalX));
                }
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                stripChartGesture = StripChartGesture.None;
                break;
        }
    }

    /// <summary>
    /// Two-finger pinch: rescales the Strip Chart's X window around the axis value under the
    /// pinch center - spreading zooms in, pinching zooms out, and zooming all the way out
    /// clears the window entirely (same as Zoom All). Track Map/Traction Circle narrow to the
    /// window's data as it changes, exactly like a drag-zoom.
    /// </summary>
    private void OnStripChartPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
            {
                float? dataMin = stripChartDrawable.DataMinX;
                float? dataMax = stripChartDrawable.DataMaxX;
                if (!dataMin.HasValue || !dataMax.HasValue)
                {
                    return; // nothing plotted yet
                }
                stripChartGesture = StripChartGesture.None;
                CancelDragZoom();
                float visibleMin = stripChartDrawable.ZoomMinX ?? dataMin.Value;
                float visibleMax = stripChartDrawable.ZoomMaxX ?? dataMax.Value;
                pinchStartWidth = Math.Max(visibleMax - visibleMin, 1e-6f);
                float pixelX = (float)(e.ScaleOrigin.X * StripChartView.Width);
                pinchAnchorAxis = stripChartDrawable.PixelXToTime(pixelX) ?? (visibleMin + pinchStartWidth / 2);
                pinchAnchorFraction = (pinchAnchorAxis - visibleMin) / pinchStartWidth;
                pinchTotalScale = 1f;
                stripChartPinchActive = true;
                break;
            }
            case GestureStatus.Running when stripChartPinchActive:
                // e.Scale is the change since the last update, so accumulate
                pinchTotalScale *= (float)e.Scale;
                ApplyPinchWindow();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                stripChartPinchActive = false;
                break;
        }
    }

    private void ApplyPinchWindow()
    {
        float? dataMin = stripChartDrawable.DataMinX;
        float? dataMax = stripChartDrawable.DataMaxX;
        if (!dataMin.HasValue || !dataMax.HasValue)
        {
            return;
        }
        float fullWidth = dataMax.Value - dataMin.Value;

        // fingers spreading (scale > 1) shrinks the window = zoom in
        float newWidth = pinchStartWidth / Math.Max(pinchTotalScale, 0.01f);
        if (newWidth >= fullWidth)
        {
            ResetZoomToFull();
            return;
        }
        newWidth = Math.Max(newWidth, fullWidth / 1000f); // cap zoom-in depth

        // keep the axis value that started under the pinch center at the same relative
        // position in the window, so the chart zooms "around the fingers"
        float newMin = pinchAnchorAxis - pinchAnchorFraction * newWidth;
        newMin = Math.Clamp(newMin, dataMin.Value, dataMax.Value - newWidth);
        ApplyZoom(newMin, newMin + newWidth);
    }

    /// <summary>
    /// Ends a Strip Chart drag-to-zoom gesture: too small a drag is treated as a click (no
    /// zoom change); otherwise the dragged pixel range becomes the Strip Chart's new X window,
    /// and Track Map/Traction Circle are narrowed to just that window's data per run.
    /// </summary>
    private void FinishDragZoom(Point? releasePosition)
    {
        if (!stripChartDrawable.DragStartPixelX.HasValue)
        {
            return;
        }
        float startPixel = stripChartDrawable.DragStartPixelX.Value;
        float endPixel = releasePosition.HasValue ? (float)releasePosition.Value.X : stripChartDrawable.DragCurrentPixelX ?? startPixel;

        stripChartDrawable.DragStartPixelX = null;
        stripChartDrawable.DragCurrentPixelX = null;

        if (Math.Abs(endPixel - startPixel) < MinDragPixels)
        {
            RefreshCharts();
            return;
        }

        float? axisA = stripChartDrawable.PixelXToTime(startPixel);
        float? axisB = stripChartDrawable.PixelXToTime(endPixel);
        if (!axisA.HasValue || !axisB.HasValue)
        {
            RefreshCharts();
            return;
        }

        ApplyZoom(Math.Min(axisA.Value, axisB.Value), Math.Max(axisA.Value, axisB.Value));
    }

    private void CancelDragZoom()
    {
        if (!stripChartDrawable.DragStartPixelX.HasValue)
        {
            return;
        }
        stripChartDrawable.DragStartPixelX = null;
        stripChartDrawable.DragCurrentPixelX = null;
        RefreshCharts();
    }

    // true while the scrollbar's own Value is being set from code (e.g. after a new drag-zoom
    // or a Zoom All reset) - suppresses OnStripChartScrollBarValueChanged so it doesn't treat
    // that programmatic move as a user pan and re-apply the same window redundantly
    private bool suppressScrollBarEvent;

    /// <summary>
    /// Applies a Strip Chart X-axis zoom window and narrows Track Map/Traction Circle to just
    /// that window's data, per run (a Time-axis selection is in TimeOffset-shifted display
    /// units per run, so each run needs its own converted window - see
    /// <see cref="StripChartDrawable.GetPerRunTimeRange"/>). Also resizes the pan scrollbar to
    /// the new window width, since this is called for a fresh drag-zoom selection (as opposed
    /// to just panning an existing window, which keeps the same width).
    /// </summary>
    private void ApplyZoom(float axisMin, float axisMax)
    {
        stripChartDrawable.ZoomMinX = axisMin;
        stripChartDrawable.ZoomMaxX = axisMax;
        ApplyTimeRangeFilter(axisMin, axisMax);
        UpdateScrollBarRange();
        RefreshCharts();
    }

    private void ApplyTimeRangeFilter(float axisMin, float axisMax)
    {
        IReadOnlyDictionary<string, (float TMin, float TMax)> timeRanges = stripChartDrawable.GetPerRunTimeRange(axisMin, axisMax);
        trackMapDrawable.TimeRangeFilter = timeRanges;
        tractionCircleDrawable.TimeRangeFilter = timeRanges;
    }

    /// <summary>
    /// Sizes the pan scrollbar to the current zoom window: its range covers every possible
    /// window start position (so the thumb can slide from the very start to the very end of
    /// the full data), and it's hidden entirely when not zoomed in (nothing to pan).
    /// </summary>
    private void UpdateScrollBarRange()
    {
        float? dataMin = stripChartDrawable.DataMinX;
        float? dataMax = stripChartDrawable.DataMaxX;
        float? zoomMin = stripChartDrawable.ZoomMinX;
        float? zoomMax = stripChartDrawable.ZoomMaxX;
        if (!dataMin.HasValue || !dataMax.HasValue || !zoomMin.HasValue || !zoomMax.HasValue)
        {
            StripChartScrollBar.IsVisible = false;
            return;
        }

        float windowWidth = zoomMax.Value - zoomMin.Value;
        float fullWidth = dataMax.Value - dataMin.Value;
        if (windowWidth <= 0 || windowWidth >= fullWidth - 0.0001f)
        {
            StripChartScrollBar.IsVisible = false;
            return;
        }

        suppressScrollBarEvent = true;
        StripChartScrollBar.Minimum = dataMin.Value;
        StripChartScrollBar.Maximum = dataMax.Value - windowWidth;
        StripChartScrollBar.Value = Math.Clamp(zoomMin.Value, StripChartScrollBar.Minimum, StripChartScrollBar.Maximum);
        suppressScrollBarEvent = false;
        StripChartScrollBar.IsVisible = true;
    }

    /// <summary>Pans the current zoom window (keeping its width) as the scrollbar is dragged.</summary>
    private void OnStripChartScrollBarValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (suppressScrollBarEvent || !stripChartDrawable.ZoomMinX.HasValue || !stripChartDrawable.ZoomMaxX.HasValue)
        {
            return;
        }
        float width = stripChartDrawable.ZoomMaxX.Value - stripChartDrawable.ZoomMinX.Value;
        float newMin = (float)e.NewValue;
        float newMax = newMin + width;

        stripChartDrawable.ZoomMinX = newMin;
        stripChartDrawable.ZoomMaxX = newMax;
        ApplyTimeRangeFilter(newMin, newMax);
        RefreshCharts();
    }

    /// <summary>
    /// Updates the Strip Chart's own cursor line (in whatever units its X axis uses - e.g.
    /// seconds for Time, or arbitrary units for Distance) plus the other two charts' box
    /// cursor. The other charts always key their data by raw timestamp, so an X-axis value
    /// (e.g. a distance) can't be handed to them directly - it always has to be converted
    /// to the nearest actual timestamp first.
    /// </summary>
    private void SetCursorTime(float? axisValue)
    {
        stripChartDrawable.CursorTime = axisValue;
        float? rawTime = axisValue.HasValue ? stripChartDrawable.ConvertToTime(axisValue.Value) : null;
        trackMapDrawable.CursorTime = rawTime;
        tractionCircleDrawable.CursorTime = rawTime;
        RefreshCharts();
    }

    /// <summary>
    /// Keeps the Track Map/Traction Circle box cursor scoped to runs actually displayed on the
    /// Strip Chart. Those two charts have their own independent run selection (which usually
    /// defaults to every loaded run), so without this a run with no checked Strip Chart channel
    /// would still show a box cursor - looking like an extra, unexplained marker.
    /// </summary>
    private void UpdateCursorEligibleRuns()
    {
        HashSet<string> runNames = stripChartSelection.Select(s => s.RunName).ToHashSet();
        trackMapDrawable.CursorEligibleRuns = runNames;
        tractionCircleDrawable.CursorEligibleRuns = runNames;
    }

    private async void OnOpenFilesClicked(object? sender, EventArgs e)
    {
        IReadOnlyList<string> filePaths;
        try
        {
            filePaths = await LogFilePicker.PickMultipleAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"OnOpenFilesClicked: file picker failed: {ex.Message}");
            await DisplayAlertAsync("Open Failed", ex.Message, "OK");
            return;
        }

        // Parse every file before touching the charts even once: redrawing the GraphicsViews
        // after each file in quick succession (once per file, back-to-back with no time for the
        // native WinUI compositor to settle) is what was crashing the whole process natively
        // (STATUS_STOWED_EXCEPTION inside Microsoft.UI.Xaml.dll) when several files were opened
        // at once. Loading one file at a time never hit it, since the picker's own dialog gave
        // the UI a break between redraws.
        List<string> warnings = new();
        foreach (string filePath in filePaths)
        {
            string? warning = await ParseFileAsync(filePath);
            if (!string.IsNullOrWhiteSpace(warning))
            {
                warnings.Add(warning);
            }
        }

        if (filePaths.Count > 0)
        {
            RefreshSelectionDefaults();
            RefreshCharts();
        }

        if (warnings.Count > 0)
        {
            await DisplayAlertAsync("Parse Warnings", SummarizeWarnings(warnings), "OK");
        }
    }

    /// <summary>
    /// Caps parse-warning text to something an alert dialog can actually lay out: a badly
    /// mismatched file produces a warning line per record, and handing DisplayAlert
    /// megabytes of text pinned the UI thread in layout - the app looked frozen right after
    /// the charts drew. The full text is in the app log (see ParseFileAsync).
    /// </summary>
    private static string SummarizeWarnings(IReadOnlyList<string> warnings)
    {
        const int maxLinesPerFile = 12;
        List<string> trimmed = new();
        foreach (string warning in warnings)
        {
            string[] lines = warning.Split('\n');
            if (lines.Length <= maxLinesPerFile)
            {
                trimmed.Add(warning);
            }
            else
            {
                trimmed.Add(string.Join("\n", lines.Take(maxLinesPerFile))
                    + $"\n... and {lines.Length - maxLinesPerFile} more warning lines (see the app log)");
            }
        }
        return string.Join("\n\n", trimmed);
    }

    /// <summary>Parses one log file into <see cref="dataLogger"/>. Returns any parse warning
    /// text (or null), and does not touch the charts - callers refresh them once, afterward.</summary>
    private async Task<string?> ParseFileAsync(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        AppLogger.Log($"Opening file {filePath}");
        try
        {
            string? warning = extension switch
            {
                ".txt" => await Task.Run(() => parser.ReadTXTFile(dataLogger, filePath)),
                ".ylg" => await Task.Run(() => parser.ReadYLGFile(dataLogger, filePath)),
                ".yl5" => await Task.Run(() => parser.ReadYL5File(dataLogger, filePath)),
                _ => throw new NotSupportedException($"Unsupported file type \"{extension}\".")
            };
            AppLogger.Log($"Opened file {filePath}");
            if (!string.IsNullOrWhiteSpace(warning))
            {
                // full warning text lives here; the alert only shows a capped summary
                AppLogger.Log($"Parse warnings for {filePath}:{Environment.NewLine}{warning}");
            }
            return warning;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Failed to open {filePath}: {ex.Message}");
            await DisplayAlertAsync("Load Failed", $"{Path.GetFileName(filePath)}:\n{ex.Message}", "OK");
            return null;
        }
    }

    /// <summary>
    /// Picks sensible default run/channel selections the first time data is loaded for each
    /// graph independently. Once a graph's picker has been used (Done clicked), that graph's
    /// selection is left alone on later loads - newly appearing runs/channels just won't
    /// show until manually checked. X/Y axis channel choices are separate fields and persist
    /// as-is regardless of this method (they only change when the user picks a new one).
    /// </summary>
    private void RefreshSelectionDefaults()
    {
        if (!stripChartCustomized)
        {
            List<string> preferred = new();
            foreach (RunData run in dataLogger.runData)
            {
                foreach (string name in run.channels.Keys)
                {
                    if (stripChartDefaultChannelNames.Contains(name) && !preferred.Contains(name))
                    {
                        preferred.Add(name);
                    }
                }
            }
            IEnumerable<string> channelsToDefault = preferred.Count > 0
                ? preferred
                : dataLogger.runData.SelectMany(r => r.channels.Keys).Where(n => !NonSelectableChannels.Contains(n)).Distinct();

            stripChartSelection.Clear();
            foreach (RunData run in dataLogger.runData)
            {
                foreach (string name in channelsToDefault)
                {
                    if (run.channels.ContainsKey(name))
                    {
                        stripChartSelection.Add((run.runName, name));
                    }
                }
            }
        }

        if (!trackMapCustomized)
        {
            trackMapSelectedRuns.Clear();
            foreach (RunData run in dataLogger.runData)
            {
                trackMapSelectedRuns.Add(run.runName);
            }
        }

        if (!tractionCircleCustomized)
        {
            tractionCircleSelectedRuns.Clear();
            foreach (RunData run in dataLogger.runData)
            {
                tractionCircleSelectedRuns.Add(run.runName);
            }
        }

        UpdateCursorEligibleRuns();
    }

    private static string EncodeSeriesKey(string channelName, string runName) => channelName + KeySeparator + runName;

    private static (string ChannelName, string RunName) DecodeSeriesKey(string key)
    {
        int idx = key.IndexOf(KeySeparator);
        return (key[..idx], key[(idx + 1)..]);
    }

    private List<string> GetAllChannelNames()
    {
        SortedSet<string> names = new();
        foreach (RunData run in dataLogger.runData)
        {
            foreach (string name in run.channels.Keys)
            {
                names.Add(name);
            }
        }
        return names.ToList();
    }

    private async void OnSelectStripChartChannelsClicked(object? sender, EventArgs e)
    {
        if (dataLogger.runData.Count == 0)
        {
            await DisplayAlertAsync("Select Channels", "No channels available yet - load a log file first.", "OK");
            return;
        }

        SortedDictionary<string, List<ChannelOption>> byChannel = new();
        foreach (RunData run in dataLogger.runData)
        {
            foreach (string name in run.channels.Keys)
            {
                if (NonSelectableChannels.Contains(name))
                {
                    continue;
                }
                if (!byChannel.TryGetValue(name, out List<ChannelOption>? items))
                {
                    items = new List<ChannelOption>();
                    byChannel[name] = items;
                }
                string key = EncodeSeriesKey(name, run.runName);
                ChannelOption option = new(key, run.runName, stripChartSelection.Contains((run.runName, name)));
                if (stripChartChannelColorOverride.TryGetValue((run.runName, name), out Color? color))
                {
                    option.Color = color;
                }
                items.Add(option);
            }
        }
        List<SeriesGroup> groups = byChannel.Select(kv =>
        {
            SeriesGroup group = new(kv.Key, kv.Value);
            if (stripChartChannelGraphIndex.TryGetValue(kv.Key, out int graphIndex))
            {
                group.GraphIndex = graphIndex;
            }
            group.Inverted = stripChartInvertedChannels.Contains(kv.Key);
            if (stripChartChannelPenWidth.TryGetValue(kv.Key, out float penWidth))
            {
                group.PenWidth = penWidth;
            }
            group.Expanded = stripChartExpandedChannels.Contains(kv.Key);
            return group;
        }).ToList();
        // the Strip Chart's X axis only makes sense against Time or a distance channel -
        // plotting channel-vs-channel belongs to the XY charts. Distance-GPS is excluded:
        // only the calculated Distance channel is offered.
        List<string> xAxisOptions = new() { StripChartDrawable.TimeAxis };
        xAxisOptions.AddRange(GetAllChannelNames()
            .Where(n => n.StartsWith("Distance", StringComparison.OrdinalIgnoreCase)
                     && !n.Equals("Distance-GPS", StringComparison.OrdinalIgnoreCase)));

        ChannelSelectionPage page = new("Strip Chart Channels", groups, result =>
        {
            stripChartCustomized = true;
            stripChartSelection.Clear();
            foreach (string key in result.SelectedKeys)
            {
                (string channelName, string runName) = DecodeSeriesKey(key);
                stripChartSelection.Add((runName, channelName));
            }
            if (result.XAxis != null)
            {
                stripChartXAxis = result.XAxis;
                stripChartDrawable.XAxisChannel = stripChartXAxis;
            }
            foreach (SeriesGroup group in groups)
            {
                stripChartChannelGraphIndex[group.Header] = group.GraphIndex;
                stripChartChannelPenWidth[group.Header] = group.PenWidth;
                if (group.Inverted)
                {
                    stripChartInvertedChannels.Add(group.Header);
                }
                else
                {
                    stripChartInvertedChannels.Remove(group.Header);
                }
                if (group.Expanded)
                {
                    stripChartExpandedChannels.Add(group.Header);
                }
                else
                {
                    stripChartExpandedChannels.Remove(group.Header);
                }
                // AllItems: rows hidden by a collapsed group still carry color overrides
                foreach (ChannelOption option in group.AllItems)
                {
                    (string channelName, string runName) = DecodeSeriesKey(option.Key);
                    if (option.Color != null)
                    {
                        stripChartChannelColorOverride[(runName, channelName)] = option.Color;
                    }
                    else
                    {
                        stripChartChannelColorOverride.Remove((runName, channelName));
                    }
                }
            }
            stripChartDefaultChannelNames = stripChartSelection.Select(s => s.ChannelName).ToHashSet();
            UpdateCursorEligibleRuns();
            SaveConfig();
            RefreshCharts();
        }, xAxisOptions, stripChartXAxis, showChannelGroupControls: true);
        await Navigation.PushModalAsync(page);
    }

    private async void OnSelectTrackMapChannelsClicked(object? sender, EventArgs e)
    {
        await SelectXYChannelsAsync("Track Map Channels", trackMapSelectedRuns, trackMapXAxis, trackMapYAxis,
            trackMapRunColor, trackMapInvertedRuns, trackMapRunPenWidth, result =>
        {
            trackMapCustomized = true;
            trackMapSelectedRuns.Clear();
            foreach (string runName in result.SelectedKeys)
            {
                trackMapSelectedRuns.Add(runName);
            }
            if (result.XAxis != null)
            {
                trackMapXAxis = result.XAxis;
                trackMapDrawable.XChannel = trackMapXAxis;
            }
            if (result.YAxis != null)
            {
                trackMapYAxis = result.YAxis;
                trackMapDrawable.YChannel = trackMapYAxis;
            }
            SaveConfig();
            TrackMapView.Invalidate();
        });
    }

    private async void OnSelectTractionCircleChannelsClicked(object? sender, EventArgs e)
    {
        await SelectXYChannelsAsync("Traction Circle Channels", tractionCircleSelectedRuns, tractionCircleXAxis, tractionCircleYAxis,
            tractionCircleRunColor, tractionCircleInvertedRuns, tractionCircleRunPenWidth, result =>
        {
            tractionCircleCustomized = true;
            tractionCircleSelectedRuns.Clear();
            foreach (string runName in result.SelectedKeys)
            {
                tractionCircleSelectedRuns.Add(runName);
            }
            if (result.XAxis != null)
            {
                tractionCircleXAxis = result.XAxis;
                tractionCircleDrawable.XChannel = tractionCircleXAxis;
            }
            if (result.YAxis != null)
            {
                tractionCircleYAxis = result.YAxis;
                tractionCircleDrawable.YChannel = tractionCircleYAxis;
            }
            SaveConfig();
            TractionCircleView.Invalidate();
        });
    }

    /// <summary>
    /// Channel picker for the XY charts (Track Map, Traction Circle): lets the user pick
    /// the X and Y axis channel (defaulted to Longitude/Latitude or gX/gY), which runs to
    /// include, and per-run trace overrides (invert, pen width, color - written back into
    /// the caller's collections when Done is pressed, before onApply runs so its SaveConfig
    /// sees them). Runs aren't filtered by channel availability up front - a run missing
    /// the chosen channels simply draws nothing, so switching axes later doesn't strand a
    /// run that was fine for the previous choice.
    /// </summary>
    private async Task SelectXYChannelsAsync(
        string title,
        HashSet<string> currentRunSelection,
        string currentXAxis,
        string currentYAxis,
        Dictionary<string, Color> runColors,
        HashSet<string> invertedRuns,
        Dictionary<string, float> runPenWidths,
        Action<SeriesSelectionResult> onApply)
    {
        if (dataLogger.runData.Count == 0)
        {
            await DisplayAlertAsync(title, "No data available yet - load a log file first.", "OK");
            return;
        }

        List<string> axisOptions = GetAllChannelNames();
        List<ChannelOption> items = dataLogger.runData.Select(r =>
        {
            ChannelOption option = new(r.runName, r.runName, currentRunSelection.Contains(r.runName));
            if (runColors.TryGetValue(r.runName, out Color? color))
            {
                option.Color = color;
            }
            option.Inverted = invertedRuns.Contains(r.runName);
            if (runPenWidths.TryGetValue(r.runName, out float penWidth))
            {
                option.PenWidth = penWidth;
            }
            return option;
        }).ToList();
        List<SeriesGroup> groups = new() { new SeriesGroup("Runs", items) };

        ChannelSelectionPage page = new(title, groups, result =>
        {
            foreach (ChannelOption option in items)
            {
                if (option.Color != null)
                {
                    runColors[option.Key] = option.Color;
                }
                else
                {
                    runColors.Remove(option.Key);
                }
                if (option.Inverted)
                {
                    invertedRuns.Add(option.Key);
                }
                else
                {
                    invertedRuns.Remove(option.Key);
                }
                runPenWidths[option.Key] = option.PenWidth;
            }
            onApply(result);
        }, axisOptions, currentXAxis, axisOptions, currentYAxis, showRunControls: true);
        await Navigation.PushModalAsync(page);
    }

    private void RefreshCharts()
    {
        StripChartView.Invalidate();
        TrackMapView.Invalidate();
        TractionCircleView.Invalidate();
    }

    private void OnZoomAllClicked(object? sender, EventArgs e) => ResetZoomToFull();

    /// <summary>
    /// Opens the manual alignment wizard: one point per run on the current Strip Chart X
    /// axis, and the runs' Time or Distance offsets shift so the points line up. A manual
    /// fallback for when automatic alignment gets it wrong (proper position-based
    /// start/finish lines are a planned replacement). Each run shows its currently selected
    /// Strip Chart channels, falling back to a default/first channel so there's always a
    /// trace to mark against.
    /// </summary>
    private async void OnAlignRunsClicked(object? sender, EventArgs e)
    {
        if (dataLogger.runData.Count < 2)
        {
            await DisplayAlertAsync("Align Runs", "Load at least two runs to align.", "OK");
            return;
        }

        bool axisIsTime = stripChartXAxis == StripChartDrawable.TimeAxis;
        List<(RunData Run, HashSet<(string RunName, string ChannelName)> Series)> wizardRuns = new();
        foreach (RunData run in dataLogger.runData)
        {
            if (!axisIsTime &&
                (!run.channels.TryGetValue(stripChartXAxis, out DataChannel? axisData) || axisData.DataPoints.Count == 0))
            {
                continue; // can't distance-align a run that has no distance data
            }
            HashSet<(string RunName, string ChannelName)> series = stripChartSelection
                .Where(s => s.RunName == run.runName && run.channels.ContainsKey(s.ChannelName))
                .ToHashSet();
            if (series.Count == 0)
            {
                string? fallback = stripChartDefaultChannelNames.FirstOrDefault(n => run.channels.ContainsKey(n))
                    ?? run.channels.Keys.FirstOrDefault(n => !NonSelectableChannels.Contains(n));
                if (fallback == null)
                {
                    continue;
                }
                series.Add((run.runName, fallback));
            }
            wizardRuns.Add((run, series));
        }
        if (wizardRuns.Count < 2)
        {
            await DisplayAlertAsync("Align Runs", $"Need at least two runs with {stripChartXAxis} data to align.", "OK");
            return;
        }

        AlignmentWizardPage page = new(dataLogger, stripChartXAxis, wizardRuns, onFinished: () =>
        {
            // the offsets just changed, so a Delta-T built from the old offsets is stale
            RecomputeDeltaTime();
            RefreshCharts();
        });
        await Navigation.PushModalAsync(page);
    }

    // base run of the most recent Delta-T computation, so anything that changes the
    // alignment offsets Delta-T was built from can recompute it automatically
    private string? deltaTimeBaseRunName;

    private void RecomputeDeltaTime()
    {
        if (deltaTimeBaseRunName != null && dataLogger.runData.Any(r => r.runName == deltaTimeBaseRunName))
        {
            DeltaTime.Compute(dataLogger, deltaTimeBaseRunName);
        }
    }

    /// <summary>
    /// Computes the calculated Delta-T channel (time gained/lost versus a base run at the
    /// same distance, like the WinForms app's delta time) after asking which run is the
    /// base. The channel lands in every run with distance data - including the base run,
    /// whose flat zero trace is the reference line - and is auto-selected onto the Strip
    /// Chart in its own subgraph band.
    /// </summary>
    private async void OnDeltaTimeClicked(object? sender, EventArgs e)
    {
        if (dataLogger.runData.Count < 2)
        {
            await DisplayAlertAsync("Delta Time", "Load at least two runs to compare.", "OK");
            return;
        }
        string[] runNames = dataLogger.runData.Select(r => r.runName).ToArray();
        string choice = await DisplayActionSheetAsync("Delta Time - pick the base run", "Cancel", null, runNames);
        if (string.IsNullOrEmpty(choice) || choice == "Cancel")
        {
            return;
        }

        string? warning = DeltaTime.Compute(dataLogger, choice);
        deltaTimeBaseRunName = choice;

        // show the result immediately: select Delta-T for every run that got it, stacked
        // into its own subgraph band (seconds gained/lost shouldn't share a Y scale with
        // RPM or G-force)
        if (!stripChartChannelGraphIndex.ContainsKey(DeltaTime.ChannelName))
        {
            int nextBand = 0;
            foreach ((string RunName, string ChannelName) selected in stripChartSelection)
            {
                int band = stripChartChannelGraphIndex.TryGetValue(selected.ChannelName, out int g) ? g : 0;
                nextBand = Math.Max(nextBand, band + 1);
            }
            stripChartChannelGraphIndex[DeltaTime.ChannelName] = Math.Min(nextBand, 7);
        }
        foreach (RunData run in dataLogger.runData)
        {
            if (run.channels.ContainsKey(DeltaTime.ChannelName))
            {
                stripChartSelection.Add((run.runName, DeltaTime.ChannelName));
            }
        }
        stripChartCustomized = true;
        stripChartDefaultChannelNames = stripChartSelection.Select(s => s.ChannelName).ToHashSet();
        UpdateCursorEligibleRuns();
        SaveConfig();
        RefreshCharts();

        if (warning != null)
        {
            await DisplayAlertAsync("Delta Time", warning, "OK");
        }
    }

    /// <summary>
    /// Clears any zoom window (Strip Chart X range, Track Map/Traction Circle time filter)
    /// back to auto-fitting the full data range, then forces a redraw. Reached via the
    /// Zoom All button or by pinching all the way back out.
    /// </summary>
    private void ResetZoomToFull()
    {
        stripChartDrawable.ZoomMinX = null;
        stripChartDrawable.ZoomMaxX = null;
        trackMapDrawable.TimeRangeFilter = null;
        tractionCircleDrawable.TimeRangeFilter = null;
        StripChartScrollBar.IsVisible = false;
        RefreshCharts();
    }

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        SettingsPage page = new(
            settings.ConfigFilePath,
            settings.AutoloadFolderPath,
            ChartColors.Palette,
            stripChartDisplayMode,
            trackMapDisplayMode,
            tractionCircleDisplayMode,
            (path, autoloadFolder, colors, stripDisplay, trackMapDisplay, tractionCircleDisplay) =>
            {
                bool autoloadFolderChanged = autoloadFolder != settings.AutoloadFolderPath;
                settings.ConfigFilePath = path;
                settings.AutoloadFolderPath = autoloadFolder;
                ChartColors.Palette = colors;
                stripChartDisplayMode = stripDisplay;
                trackMapDisplayMode = trackMapDisplay;
                tractionCircleDisplayMode = tractionCircleDisplay;
                stripChartDrawable.DisplayMode = stripChartDisplayMode;
                trackMapDrawable.DisplayMode = trackMapDisplayMode;
                tractionCircleDrawable.DisplayMode = tractionCircleDisplayMode;
                SaveConfig();
                if (autoloadFolderChanged)
                {
                    StartAutoload();
                }
                RefreshCharts();
            });
        await Navigation.PushModalAsync(page);
    }

    private async void OnAboutClicked(object? sender, EventArgs e)
    {
        await DisplayAlertAsync(
            "About YamuraView",
            $"YamuraView\nVersion {AppVersion.Number} ({AppVersion.Status})",
            "OK");
    }

    /// <summary>
    /// Loads the config file (colors, remembered Strip Chart channel names, XY chart axis
    /// choices) if the ini-remembered path exists. Called once at startup, before the
    /// drawables are created, so their initial X/Y axis fields already reflect it.
    /// </summary>
    private void LoadConfigIfPresent()
    {
        if (!File.Exists(settings.ConfigFilePath))
        {
            return;
        }
        try
        {
            XDocument doc = XDocument.Load(settings.ConfigFilePath);
            XElement? root = doc.Element("Config");
            if (root == null)
            {
                return;
            }

            List<Color> colors = root.Element("AutoColors")?.Elements("Color")
                .Select(el => Color.Parse(el.Value))
                .ToList() ?? new List<Color>();
            if (colors.Count > 0)
            {
                ChartColors.Palette = colors.ToArray();
            }

            XElement? stripChart = root.Element("StripChart");
            if (stripChart != null)
            {
                stripChartXAxis = (string?)stripChart.Attribute("XAxis") ?? stripChartXAxis;
                if (Enum.TryParse((string?)stripChart.Attribute("DisplayMode"), out ChartDisplayMode stripDisplayMode))
                {
                    stripChartDisplayMode = stripDisplayMode;
                }
                List<string> channelNames = stripChart.Elements("Channel")
                    .Select(el => (string?)el.Attribute("Name"))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToList();
                if (channelNames.Count > 0)
                {
                    stripChartDefaultChannelNames = channelNames.ToHashSet();
                }

                stripChartChannelGraphIndex.Clear();
                stripChartChannelColorOverride.Clear();
                stripChartInvertedChannels.Clear();
                stripChartChannelPenWidth.Clear();
                stripChartExpandedChannels.Clear();
                foreach (XElement channelElement in stripChart.Elements("Channel"))
                {
                    string? name = (string?)channelElement.Attribute("Name");
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }
                    if (int.TryParse((string?)channelElement.Attribute("Graph"), out int graphIndex))
                    {
                        stripChartChannelGraphIndex[name] = graphIndex;
                    }
                    if (bool.TryParse((string?)channelElement.Attribute("Invert"), out bool inverted) && inverted)
                    {
                        stripChartInvertedChannels.Add(name);
                    }
                    if (float.TryParse((string?)channelElement.Attribute("PenWidth"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float penWidth))
                    {
                        stripChartChannelPenWidth[name] = penWidth;
                    }
                    if (bool.TryParse((string?)channelElement.Attribute("Expanded"), out bool expanded) && expanded)
                    {
                        stripChartExpandedChannels.Add(name);
                    }
                    foreach (XElement runColorElement in channelElement.Elements("RunColor"))
                    {
                        string? runName = (string?)runColorElement.Attribute("Name");
                        string? colorHex = (string?)runColorElement.Attribute("Color");
                        if (!string.IsNullOrEmpty(runName) && !string.IsNullOrEmpty(colorHex))
                        {
                            stripChartChannelColorOverride[(runName, name)] = Color.Parse(colorHex);
                        }
                    }
                }
            }

            XElement? trackMap = root.Element("TrackMap");
            if (trackMap != null)
            {
                trackMapXAxis = (string?)trackMap.Attribute("XAxis") ?? trackMapXAxis;
                trackMapYAxis = (string?)trackMap.Attribute("YAxis") ?? trackMapYAxis;
                if (Enum.TryParse((string?)trackMap.Attribute("DisplayMode"), out ChartDisplayMode trackMapDisplay))
                {
                    trackMapDisplayMode = trackMapDisplay;
                }
                LoadRunSettings(trackMap, trackMapRunColor, trackMapInvertedRuns, trackMapRunPenWidth);
            }

            XElement? tractionCircle = root.Element("TractionCircle");
            if (tractionCircle != null)
            {
                tractionCircleXAxis = (string?)tractionCircle.Attribute("XAxis") ?? tractionCircleXAxis;
                tractionCircleYAxis = (string?)tractionCircle.Attribute("YAxis") ?? tractionCircleYAxis;
                if (Enum.TryParse((string?)tractionCircle.Attribute("DisplayMode"), out ChartDisplayMode tractionCircleDisplay))
                {
                    tractionCircleDisplayMode = tractionCircleDisplay;
                }
                LoadRunSettings(tractionCircle, tractionCircleRunColor, tractionCircleInvertedRuns, tractionCircleRunPenWidth);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Failed to load config {settings.ConfigFilePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves colors, remembered Strip Chart channel names, and XY chart axis choices to the
    /// config file, and updates the ini file to point at it. Called whenever a channel/axis
    /// picker or the Settings page is applied.
    /// </summary>
    private void SaveConfig()
    {
        try
        {
            HashSet<string> channelNames = stripChartSelection.Select(s => s.ChannelName).Distinct().ToHashSet();
            if (channelNames.Count == 0)
            {
                channelNames = stripChartDefaultChannelNames;
            }

            XDocument doc = new(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("Config",
                    new XElement("AutoColors", ChartColors.Palette.Select(c => new XElement("Color", c.ToHex()))),
                    new XElement("StripChart",
                        new XAttribute("XAxis", stripChartXAxis),
                        new XAttribute("DisplayMode", stripChartDisplayMode.ToString()),
                        channelNames.Select(n => new XElement("Channel",
                            new XAttribute("Name", n),
                            new XAttribute("Graph", stripChartChannelGraphIndex.TryGetValue(n, out int g) ? g : 0),
                            new XAttribute("Invert", stripChartInvertedChannels.Contains(n)),
                            new XAttribute("PenWidth", stripChartChannelPenWidth.TryGetValue(n, out float w) ? w : StripChartDrawable.DefaultPenWidth),
                            new XAttribute("Expanded", stripChartExpandedChannels.Contains(n)),
                            stripChartChannelColorOverride
                                .Where(kv => kv.Key.ChannelName == n)
                                .Select(kv => new XElement("RunColor",
                                    new XAttribute("Name", kv.Key.RunName),
                                    new XAttribute("Color", kv.Value.ToHex())))))),
                    new XElement("TrackMap",
                        new XAttribute("XAxis", trackMapXAxis),
                        new XAttribute("YAxis", trackMapYAxis),
                        new XAttribute("DisplayMode", trackMapDisplayMode.ToString()),
                        BuildRunSettingElements(trackMapRunColor, trackMapInvertedRuns, trackMapRunPenWidth)),
                    new XElement("TractionCircle",
                        new XAttribute("XAxis", tractionCircleXAxis),
                        new XAttribute("YAxis", tractionCircleYAxis),
                        new XAttribute("DisplayMode", tractionCircleDisplayMode.ToString()),
                        BuildRunSettingElements(tractionCircleRunColor, tractionCircleInvertedRuns, tractionCircleRunPenWidth))));

            string? dir = Path.GetDirectoryName(settings.ConfigFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            doc.Save(settings.ConfigFilePath);
            settings.Save();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Failed to save config {settings.ConfigFilePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// One config Run element per run that has any non-default trace override (color,
    /// invert, or pen width) on an XY chart - runs with all defaults aren't saved, since
    /// absent means default on load.
    /// </summary>
    private static IEnumerable<XElement> BuildRunSettingElements(
        Dictionary<string, Color> runColors, HashSet<string> invertedRuns, Dictionary<string, float> runPenWidths)
    {
        SortedSet<string> names = new(runColors.Keys);
        names.UnionWith(invertedRuns);
        foreach ((string name, float width) in runPenWidths)
        {
            if (width != StripChartDrawable.DefaultPenWidth)
            {
                names.Add(name);
            }
        }
        foreach (string name in names)
        {
            XElement element = new("Run", new XAttribute("Name", name));
            if (runColors.TryGetValue(name, out Color? color))
            {
                element.Add(new XAttribute("Color", color.ToHex()));
            }
            element.Add(new XAttribute("Invert", invertedRuns.Contains(name)));
            element.Add(new XAttribute("PenWidth",
                runPenWidths.TryGetValue(name, out float width) ? width : StripChartDrawable.DefaultPenWidth));
            yield return element;
        }
    }

    private static void LoadRunSettings(
        XElement chartElement, Dictionary<string, Color> runColors, HashSet<string> invertedRuns, Dictionary<string, float> runPenWidths)
    {
        runColors.Clear();
        invertedRuns.Clear();
        runPenWidths.Clear();
        foreach (XElement runElement in chartElement.Elements("Run"))
        {
            string? name = (string?)runElement.Attribute("Name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            string? colorHex = (string?)runElement.Attribute("Color");
            if (!string.IsNullOrEmpty(colorHex))
            {
                runColors[name] = Color.Parse(colorHex);
            }
            if (bool.TryParse((string?)runElement.Attribute("Invert"), out bool inverted) && inverted)
            {
                invertedRuns.Add(name);
            }
            if (float.TryParse((string?)runElement.Attribute("PenWidth"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float penWidth))
            {
                runPenWidths[name] = penWidth;
            }
        }
    }
}
