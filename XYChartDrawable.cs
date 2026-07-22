using YamuraView.Core;

namespace YamuraView;

/// <summary>Physical unit of the XY chart background grid's spacing.</summary>
public enum GridSpacingUnit
{
    Feet,
    Meters,
}

/// <summary>
/// Plots one channel against another, point-for-point by matching timestamp, across the
/// selected runs (equivalent to the WinForms app's "Track Map" and "Traction Circle" XY
/// charts). With <see cref="EqualScale"/> set, X and Y use the same units-per-pixel scale
/// so a GPS track or G-G circle isn't visually distorted. <see cref="XChannel"/>/
/// <see cref="YChannel"/> default to a sensible pair (Longitude/Latitude, gX/gY) but are
/// user-selectable, as is <see cref="SelectedRuns"/> - both set by the page hosting this drawable.
/// </summary>
public class XYChartDrawable : IDrawable
{
    public DataLogger DataLogger { get; set; } = null!;
    public string XChannel { get; set; } = "";
    public string YChannel { get; set; } = "";
    public bool EqualScale { get; set; }
    public ISet<string> SelectedRuns { get; set; } = new HashSet<string>();

    /// <summary>Whether this chart's traces draw as connected lines or individual points - set
    /// independently per chart, not shared with the Strip Chart or the other XY chart.</summary>
    public ChartDisplayMode DisplayMode { get; set; } = ChartDisplayMode.Line;

    /// <summary>Draws a G-G reference overlay behind the traces: horizontal/vertical axis
    /// lines through the data origin plus circles at 0.5/1.0/1.5 G. Only meaningful for the
    /// Traction Circle (where the channels are lateral/longitudinal G); off for the Track Map.</summary>
    public bool ShowGReference { get; set; }

    /// <summary>Radius of the outermost G reference circle - the fitted range is expanded to
    /// keep it fully in view when <see cref="ShowGReference"/> is on.</summary>
    private const float MaxReferenceG = 1.5f;

    /// <summary>In <see cref="ChartDisplayMode.CursorTrail"/> mode, how many data points
    /// before the cursor point the trail line extends - the trail runs from N points earlier
    /// in time up to the cursor point (up to N+1 points), with no look-ahead.</summary>
    public int CursorTrailPointCount { get; set; } = 25;

    /// <summary>Spacing of a light-grey background grid as a physical distance in
    /// <see cref="GridSpacingUnit"/>, its lines anchored at multiples of the spacing.
    /// A Longitude/Latitude axis converts the distance to degrees internally (longitude
    /// scaled by the latitude's cosine, so grid cells are square on the ground); any other
    /// channel takes the value directly in its own data units. 0 or negative disables the
    /// grid (the default) - the Track Map turns it on, user-set on the Settings page.</summary>
    public float GridSpacing { get; set; }

    /// <summary>Unit <see cref="GridSpacing"/> is expressed in.</summary>
    public GridSpacingUnit GridSpacingUnit { get; set; } = GridSpacingUnit.Feet;

    private const float FeetToMeters = 0.3048f;
    private const float MetersPerDegreeLatitude = 111320f;

    /// <summary>
    /// Per-run raw timestamp to show a box cursor at (the nearest data point of that run),
    /// tracking the Strip Chart's cursor - matches the WinForms app's cross-chart mouse
    /// tracking (BOX cursor mode). Per run because this chart's data is keyed by raw,
    /// unoffset time: the same aligned cursor position is a different raw time in every
    /// run (see <see cref="StripChartDrawable.GetPerRunCursorTimes"/>). A run missing from
    /// the map gets no box; null hides the cursor entirely.
    /// </summary>
    public IReadOnlyDictionary<string, float>? CursorTimes { get; set; }

    /// <summary>
    /// Runs to actually draw a box cursor for, independent of <see cref="SelectedRuns"/> (which
    /// still controls the plotted line/track). This chart's own run selection can include more
    /// runs than are currently checked on the Strip Chart, which would otherwise show a box
    /// cursor for a run the user doesn't see as "displayed". Null means no restriction (show a
    /// box for every run in <see cref="SelectedRuns"/>).
    /// </summary>
    public ISet<string>? CursorEligibleRuns { get; set; }

    /// <summary>
    /// Trace color for a specific run, overriding the auto per-run color. A run missing from
    /// this map (or a null map) keeps the default.
    /// </summary>
    public IReadOnlyDictionary<string, Color>? RunColorOverride { get; set; }

    /// <summary>
    /// Run names whose trace (and box cursor) is flipped vertically within the plot - the XY
    /// equivalent of the Strip Chart's invert, but per run since an XY chart draws one trace
    /// per run. Only mirrors where the trace is drawn; the auto-fitted range is unaffected.
    /// </summary>
    public ISet<string>? InvertedRuns { get; set; }

    /// <summary>Trace pen width (point radius in point mode) per run. A run missing from
    /// this map (or a null map) draws at <see cref="StripChartDrawable.DefaultPenWidth"/>.</summary>
    public IReadOnlyDictionary<string, float>? RunPenWidth { get; set; }

    /// <summary>Display-only smoothing per channel name (see <see cref="ChannelFilter"/>),
    /// applied to either axis's channel when building the cached display data - the raw
    /// data is never modified. A channel missing from this map (or a null map) draws
    /// unfiltered. Shared with the other charts, keyed by channel name.</summary>
    public IReadOnlyDictionary<string, ChannelFilterSettings>? ChannelFilters { get; set; }

    private ChannelFilterSettings? FilterFor(string channelName) =>
        ChannelFilters != null && ChannelFilters.TryGetValue(channelName, out ChannelFilterSettings? settings) ? settings : null;

    private Color ColorFor(int runIndex, string runName) =>
        RunColorOverride != null && RunColorOverride.TryGetValue(runName, out Color? overrideColor)
            ? overrideColor
            : ChartColors.ForRunIndex(runIndex);

    private float PenWidthFor(string runName) =>
        RunPenWidth != null && RunPenWidth.TryGetValue(runName, out float width) ? width : StripChartDrawable.DefaultPenWidth;

    private bool IsInverted(string runName) => InvertedRuns?.Contains(runName) == true;

    /// <summary>
    /// Per-run real-timestamp window to zoom to - set from the Strip Chart's drag-to-zoom
    /// selection (converted per run via <see cref="StripChartDrawable.GetPerRunTimeRange"/>).
    /// Rescales this chart's own X/Y axes to fit only the points inside each run's window
    /// (still drawing the full trace, same as the WinForms app's zoom - points outside the
    /// window just fall outside the visible plot area). A run missing from this map (or a
    /// null map) auto-fits its full data range, unaffected.
    /// </summary>
    public IReadOnlyDictionary<string, (float TMin, float TMax)>? TimeRangeFilter { get; set; }

    // Draw()'s expensive step is joining X and Y by timestamp for every run (a dictionary
    // lookup per data point) - redoing that on every repaint made cursor tracking lag on big
    // logs, so the joined points and their full ranges are cached and rebuilt only when the
    // input fingerprint changes. Null fingerprint = cache invalid.
    private int? cachedDataFingerprint;
    private List<(RunData Run, int RunIndex, List<(float Time, float X, float Y)> Points)> cachedRunPoints = new();
    private float cachedFullMinX, cachedFullMaxX, cachedFullMinY, cachedFullMaxY;

    // the pixel-space trace paths are cached too (like the WinForms app's cached
    // GraphicsPaths) - cursor-tracking repaints don't change the data->pixel mapping, so
    // they just re-stroke this geometry. Keyed on the data fingerprint plus everything
    // else that moves pixels: view size, the fitted axis ranges (which fold in any
    // zoom-window filter), and which runs are inverted.
    private int? cachedPathKey;
    private List<(RunData Run, int RunIndex, PathF Path)> cachedPaths = new();

    // point-display-mode analogue of cachedPaths: the visible, deduped pixel positions per
    // run - without it every repaint (cursor moves included) rescaled and hit-tested every
    // data point again
    private int? cachedPointKey;
    private List<(RunData Run, int RunIndex, List<(float X, float Y)> Pixels)> cachedPointRuns = new();

    // fitted ranges for the current TimeRangeFilter (a Strip Chart zoom) - recomputing
    // them scanned every data point on every repaint while zoomed, which made cursor
    // tracking lag badly the moment the user zoomed in
    private int? cachedFilterKey;
    private float cachedFilteredMinX, cachedFilteredMaxX, cachedFilteredMinY, cachedFilteredMaxY;

    private int ComputeFilterKey(int dataFingerprint, IReadOnlyDictionary<string, (float TMin, float TMax)> filter)
    {
        HashCode hash = new();
        hash.Add(dataFingerprint);
        foreach ((string runName, (float tMin, float tMax)) in filter)
        {
            hash.Add(runName);
            hash.Add(tMin);
            hash.Add(tMax);
        }
        return hash.ToHashCode();
    }

    private int ComputePathKey(int dataFingerprint, RectF dirtyRect, float minX, float maxX, float minY, float maxY)
    {
        HashCode hash = new();
        hash.Add(dataFingerprint);
        hash.Add(dirtyRect.Width);
        hash.Add(dirtyRect.Height);
        hash.Add(minX);
        hash.Add(maxX);
        hash.Add(minY);
        hash.Add(maxY);
        if (InvertedRuns != null)
        {
            foreach (string name in InvertedRuns)
            {
                hash.Add(name);
            }
        }
        return hash.ToHashCode();
    }

    /// <summary>Cheap fingerprint of the cached join's inputs - channel pair, selected runs,
    /// and their point counts (data is only ever added wholesale, never edited in place).</summary>
    private int ComputeDataFingerprint()
    {
        HashCode hash = new();
        hash.Add(XChannel);
        hash.Add(YChannel);
        hash.Add(DataLogger.runData.Count);
        hash.Add(SelectedRuns.Count);
        foreach (RunData run in DataLogger.runData)
        {
            if (!SelectedRuns.Contains(run.runName))
            {
                continue;
            }
            hash.Add(run.runName);
            if (run.channels.TryGetValue(XChannel, out DataChannel? xChan))
            {
                hash.Add(xChan.DataPoints.Count);
            }
            if (run.channels.TryGetValue(YChannel, out DataChannel? yChan))
            {
                hash.Add(yChan.DataPoints.Count);
            }
        }
        if (ChannelFilters != null)
        {
            foreach ((string channelName, ChannelFilterSettings settings) in ChannelFilters)
            {
                hash.Add(channelName);
                hash.Add(settings.Type);
                hash.Add(settings.WindowSize);
            }
        }
        return hash.ToHashCode();
    }

    /// <summary>Rebuilds the timestamp-joined (time, x, y) list per selected run, plus the
    /// unfiltered X/Y ranges - everything Draw() needs that costs O(points) to make.</summary>
    private void RebuildDataCache()
    {
        cachedRunPoints = new();
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int runIdx = 0; runIdx < DataLogger.runData.Count; runIdx++)
        {
            RunData run = DataLogger.runData[runIdx];
            if (!SelectedRuns.Contains(run.runName) ||
                !run.channels.TryGetValue(XChannel, out DataChannel? xChan) ||
                !run.channels.TryGetValue(YChannel, out DataChannel? yChan) ||
                xChan.DataPoints.Count == 0 || yChan.DataPoints.Count == 0)
            {
                continue;
            }
            List<(float Time, float X, float Y)> points = new();
            // display filters run over each channel's full value sequence (index-aligned
            // with its timestamps) before the timestamp join, so a filtered value reflects
            // its channel's own neighboring samples even where the other channel has gaps
            IList<float> xValues = ChannelFilter.Apply(xChan.DataPoints.Values, FilterFor(XChannel));
            IList<float> yValues = ChannelFilter.Apply(yChan.DataPoints.Values, FilterFor(YChannel));
            IList<float> xTimes = xChan.DataPoints.Keys;
            for (int i = 0; i < xTimes.Count; i++)
            {
                float time = xTimes[i];
                int yIdx = yChan.DataPoints.IndexOfKey(time);
                if (yIdx < 0)
                {
                    continue;
                }
                float xVal = xValues[i];
                float yVal = yValues[yIdx];
                points.Add((time, xVal, yVal));
                minX = Math.Min(minX, xVal);
                maxX = Math.Max(maxX, xVal);
                minY = Math.Min(minY, yVal);
                maxY = Math.Max(maxY, yVal);
            }
            if (points.Count > 0)
            {
                cachedRunPoints.Add((run, runIdx, points));
            }
        }
        cachedFullMinX = minX;
        cachedFullMaxX = maxX;
        cachedFullMinY = minY;
        cachedFullMaxY = maxY;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(dirtyRect);

        if (SelectedRuns.Count == 0)
        {
            DrawCenteredMessage(canvas, dirtyRect, "No runs selected");
            return;
        }

        int fingerprint = ComputeDataFingerprint();
        if (fingerprint != cachedDataFingerprint)
        {
            RebuildDataCache();
            cachedDataFingerprint = fingerprint;
        }

        if (cachedRunPoints.Count == 0)
        {
            DrawCenteredMessage(canvas, dirtyRect, $"No {XChannel}/{YChannel} data for the selected run(s)");
            return;
        }

        float minX, maxX, minY, maxY;
        if (TimeRangeFilter == null)
        {
            minX = cachedFullMinX;
            maxX = cachedFullMaxX;
            minY = cachedFullMinY;
            maxY = cachedFullMaxY;
        }
        else
        {
            // zoomed: auto-fit to only the points inside each run's selected time window
            // (a run missing from the filter auto-fits its full data, unaffected). The scan
            // touches every point, so it's cached - cursor-only repaints skip it entirely.
            int filterKey = ComputeFilterKey(fingerprint, TimeRangeFilter);
            if (filterKey != cachedFilterKey)
            {
                minX = float.MaxValue;
                maxX = float.MinValue;
                minY = float.MaxValue;
                maxY = float.MinValue;
                foreach ((RunData run, _, List<(float Time, float X, float Y)> points) in cachedRunPoints)
                {
                    bool hasWindow = TimeRangeFilter.TryGetValue(run.runName, out (float TMin, float TMax) range);
                    foreach ((float time, float xVal, float yVal) in points)
                    {
                        if (hasWindow && (time < range.TMin || time > range.TMax))
                        {
                            continue;
                        }
                        minX = Math.Min(minX, xVal);
                        maxX = Math.Max(maxX, xVal);
                        minY = Math.Min(minY, yVal);
                        maxY = Math.Max(maxY, yVal);
                    }
                }
                cachedFilteredMinX = minX;
                cachedFilteredMaxX = maxX;
                cachedFilteredMinY = minY;
                cachedFilteredMaxY = maxY;
                cachedFilterKey = filterKey;
            }
            else
            {
                minX = cachedFilteredMinX;
                maxX = cachedFilteredMaxX;
                minY = cachedFilteredMinY;
                maxY = cachedFilteredMaxY;
            }
        }

        if (ShowGReference)
        {
            // always fit the outermost reference circle, so the G-G overlay is fully
            // visible even when the data never reaches that many G (also rescues the
            // degenerate all-points-filtered-out case with a usable range)
            minX = Math.Min(minX, -MaxReferenceG);
            maxX = Math.Max(maxX, MaxReferenceG);
            minY = Math.Min(minY, -MaxReferenceG);
            maxY = Math.Max(maxY, MaxReferenceG);
        }

        if (minX >= maxX && minY >= maxY)
        {
            DrawCenteredMessage(canvas, dirtyRect, "Not enough data to plot");
            return;
        }
        // guard degenerate single-point ranges on either axis
        if (minX >= maxX)
        {
            minX -= 1f;
            maxX += 1f;
        }
        if (minY >= maxY)
        {
            minY -= 1f;
            maxY += 1f;
        }

        const float margin = 8;
        float plotLeft = margin, plotTop = margin;
        float plotRight = dirtyRect.Width - margin;
        float plotBottom = dirtyRect.Height - margin;
        float plotWidth = plotRight - plotLeft;
        float plotHeight = plotBottom - plotTop;

        float rangeX = maxX - minX;
        float rangeY = maxY - minY;

        Func<float, float> scaleX;
        Func<float, float> scaleY;
        // data values at the plot area's edges - matches the fitted range except with
        // EqualScale, where the narrower axis's visible span is wider than its data span
        float visMinX, visMaxX, visMinY, visMaxY;

        if (EqualScale)
        {
            // use one units-per-pixel scale for both axes, so shapes aren't stretched;
            // center the (possibly narrower) axis within the available space
            float unitsPerPixel = Math.Max(rangeX / plotWidth, rangeY / plotHeight);
            float usedWidth = rangeX / unitsPerPixel;
            float usedHeight = rangeY / unitsPerPixel;
            float offsetX = plotLeft + (plotWidth - usedWidth) / 2f;
            float offsetY = plotTop + (plotHeight - usedHeight) / 2f;
            scaleX = x => offsetX + (x - minX) / unitsPerPixel;
            scaleY = y => offsetY + usedHeight - (y - minY) / unitsPerPixel;
            visMinX = minX - (offsetX - plotLeft) * unitsPerPixel;
            visMaxX = minX + (plotRight - offsetX) * unitsPerPixel;
            visMinY = minY + (offsetY + usedHeight - plotBottom) * unitsPerPixel;
            visMaxY = minY + (offsetY + usedHeight - plotTop) * unitsPerPixel;
        }
        else
        {
            scaleX = x => plotLeft + (x - minX) / rangeX * plotWidth;
            scaleY = y => plotBottom - (y - minY) / rangeY * plotHeight;
            visMinX = minX;
            visMaxX = maxX;
            visMinY = minY;
            visMaxY = maxY;
        }

        canvas.StrokeColor = Colors.DimGray;
        canvas.StrokeSize = 1;
        canvas.DrawRectangle(plotLeft, plotTop, plotWidth, plotHeight);

        if (GridSpacing > 0)
        {
            // the spacing is a physical distance - GPS degree axes convert it to degrees
            // (longitude degrees shrink with latitude, so cells stay square on the ground);
            // any other channel is assumed to already be in the grid's physical units
            float spacingMeters = GridSpacingUnit == GridSpacingUnit.Feet ? GridSpacing * FeetToMeters : GridSpacing;
            bool xIsLongitude = XChannel.Equals("Longitude", StringComparison.OrdinalIgnoreCase);
            bool xIsLatitude = XChannel.Equals("Latitude", StringComparison.OrdinalIgnoreCase);
            bool yIsLongitude = YChannel.Equals("Longitude", StringComparison.OrdinalIgnoreCase);
            bool yIsLatitude = YChannel.Equals("Latitude", StringComparison.OrdinalIgnoreCase);
            float midLatitudeDeg = yIsLatitude ? (visMinY + visMaxY) / 2f
                : xIsLatitude ? (visMinX + visMaxX) / 2f
                : 0f;
            // clamped so a garbage latitude can't collapse the longitude spacing to zero
            float cosLatitude = MathF.Max(0.05f, MathF.Cos(midLatitudeDeg * MathF.PI / 180f));
            float spacingX = xIsLatitude ? spacingMeters / MetersPerDegreeLatitude
                : xIsLongitude ? spacingMeters / (MetersPerDegreeLatitude * cosLatitude)
                : GridSpacing;
            float spacingY = yIsLatitude ? spacingMeters / MetersPerDegreeLatitude
                : yIsLongitude ? spacingMeters / (MetersPerDegreeLatitude * cosLatitude)
                : GridSpacing;

            // grid lines at multiples of the spacing (anchored at 0 in data space), behind
            // the traces; skipped when the spacing is tiny relative to the visible range so
            // a bad value can't wedge the repaint drawing thousands of lines
            const int maxGridLines = 200;
            if ((visMaxX - visMinX) / spacingX <= maxGridLines &&
                (visMaxY - visMinY) / spacingY <= maxGridLines)
            {
                canvas.StrokeColor = Colors.LightGray;
                canvas.StrokeSize = 0.5f;
                // line positions come from an integer multiple in double - GPS axes put
                // large coordinates (e.g. -122°) over tiny spacings, where accumulating
                // x += spacing in float drifts visibly across the plot
                for (double k = Math.Ceiling(visMinX / (double)spacingX); k * spacingX <= visMaxX; k++)
                {
                    float px = scaleX((float)(k * spacingX));
                    canvas.DrawLine(px, plotTop, px, plotBottom);
                }
                for (double k = Math.Ceiling(visMinY / (double)spacingY); k * spacingY <= visMaxY; k++)
                {
                    float py = scaleY((float)(k * spacingY));
                    canvas.DrawLine(plotLeft, py, plotRight, py);
                }
            }
        }

        if (ShowGReference)
        {
            // axis lines through the data origin and circles at 0.5/1.0/1.5 G, drawn
            // before the traces so they sit behind them; clipped to the plot area since
            // the outer circles can extend past the fitted data range
            canvas.SaveState();
            canvas.ClipRectangle(plotLeft, plotTop, plotWidth, plotHeight);
            canvas.StrokeColor = Colors.LightGray;
            canvas.StrokeSize = 1;
            float originPx = scaleX(0f);
            float originPy = scaleY(0f);
            canvas.DrawLine(plotLeft, originPy, plotRight, originPy);
            canvas.DrawLine(originPx, plotTop, originPx, plotBottom);
            foreach (float g in new[] { 0.5f, 1.0f, MaxReferenceG })
            {
                // radii from the scale functions so this stays correct even without
                // EqualScale (where the "circle" is an ellipse in pixel space)
                float radiusX = scaleX(g) - originPx;
                float radiusY = originPy - scaleY(g);
                canvas.DrawEllipse(originPx - radiusX, originPy - radiusY, radiusX * 2, radiusY * 2);
            }
            canvas.RestoreState();
        }

        // an inverted run mirrors its trace vertically, reflecting the normal pixel Y
        // around the plot's vertical center (with EqualScale the used area is centered
        // in the plot, so this reflects around the used area's center too)
        if (DisplayMode is ChartDisplayMode.CursorOnly or ChartDisplayMode.CursorTrail)
        {
            // full traces hidden - only the reference overlay, the box cursor, and (in
            // CursorTrail mode) the short trail around the cursor draw below
        }
        else
        {
            // LinePoint draws both, points on top of the line
            bool drawPoints = DisplayMode is ChartDisplayMode.Point or ChartDisplayMode.LinePoint;
            bool drawLine = DisplayMode is ChartDisplayMode.Line or ChartDisplayMode.LinePoint;

            // lines first so points sit on top of them in LinePoint mode
            if (drawLine)
            {
                int pathKey = ComputePathKey(fingerprint, dirtyRect, minX, maxX, minY, maxY);
                if (pathKey != cachedPathKey)
                {
                    cachedPaths = new();
                    foreach ((RunData run, int runIdx, List<(float Time, float X, float Y)> runPoints) in cachedRunPoints)
                    {
                        bool invertedRun = IsInverted(run.runName);
                        PathF path = new();
                        // no figure is opened until there's a visible segment to draw: every
                        // MoveTo must be followed by at least one LineTo before the next MoveTo,
                        // or Win2D's path builder throws ("A call to BeginFigure occurred, when
                        // the figure was already begun") - which happened when a trace's first
                        // point sat outside the zoom window
                        bool haveLast = false;
                        bool pendingMove = true;
                        float lastPx = 0, lastPy = 0;
                        foreach ((_, float xVal, float yVal) in runPoints)
                        {
                            float px = scaleX(xVal);
                            float py = invertedRun ? plotTop + plotBottom - scaleY(yVal) : scaleY(yVal);
                            if (!haveLast)
                            {
                                haveLast = true;
                            }
                            else if ((px < plotLeft && lastPx < plotLeft) || (px > plotRight && lastPx > plotRight) ||
                                     (py < plotTop && lastPy < plotTop) || (py > plotBottom && lastPy > plotBottom))
                            {
                                // both endpoints off the same side of the plot (zoomed): the whole
                                // segment is invisible - drop it and restart the path on re-entry
                                pendingMove = true;
                            }
                            else if (!pendingMove && Math.Abs(px - lastPx) < 0.5f && Math.Abs(py - lastPy) < 0.5f)
                            {
                                continue; // sub-pixel move - drop it, keep the previous point as anchor
                            }
                            else
                            {
                                if (pendingMove)
                                {
                                    // start (or restart) the figure from the previous point so the
                                    // first/crossing segment enters at the correct angle
                                    path.MoveTo(lastPx, lastPy);
                                    pendingMove = false;
                                }
                                path.LineTo(px, py);
                            }
                            lastPx = px;
                            lastPy = py;
                        }
                        if (path.Count > 0)
                        {
                            cachedPaths.Add((run, runIdx, path));
                        }
                    }
                    cachedPathKey = pathKey;
                }

                // color and pen width are looked up at stroke time, so changing them doesn't
                // invalidate the cached geometry
                foreach ((RunData run, int runIdx, PathF path) in cachedPaths)
                {
                    canvas.StrokeColor = ColorFor(runIdx, run.runName);
                    canvas.StrokeSize = PenWidthFor(run.runName);
                    canvas.DrawPath(path);
                }
            }

            if (drawPoints)
            {
                // visible pixel positions are cached like the line-mode paths - cursor-only
                // repaints just replay them instead of rescaling every data point. The
                // visibility margin is the stepper's max pen width, so the cache stays valid
                // when the pen width changes (radius is still looked up at draw time).
                const float maxPenWidth = 5f;
                int pointKey = ComputePathKey(fingerprint, dirtyRect, minX, maxX, minY, maxY);
                if (pointKey != cachedPointKey)
                {
                    cachedPointRuns = new();
                    foreach ((RunData run, int runIdx, List<(float Time, float X, float Y)> runPoints) in cachedRunPoints)
                    {
                        bool invertedRun = IsInverted(run.runName);
                        List<(float X, float Y)> pixels = new();
                        float lastPx = float.MinValue, lastPy = float.MinValue;
                        foreach ((_, float xVal, float yVal) in runPoints)
                        {
                            float px = scaleX(xVal);
                            float py = invertedRun ? plotTop + plotBottom - scaleY(yVal) : scaleY(yVal);
                            if (px < plotLeft - maxPenWidth || px > plotRight + maxPenWidth ||
                                py < plotTop - maxPenWidth || py > plotBottom + maxPenWidth)
                            {
                                continue; // outside the visible plot (when zoomed)
                            }
                            if (Math.Abs(px - lastPx) < 0.5f && Math.Abs(py - lastPy) < 0.5f)
                            {
                                continue; // sub-pixel duplicate of the previous drawn point
                            }
                            pixels.Add((px, py));
                            lastPx = px;
                            lastPy = py;
                        }
                        if (pixels.Count > 0)
                        {
                            cachedPointRuns.Add((run, runIdx, pixels));
                        }
                    }
                    cachedPointKey = pointKey;
                }

                foreach ((RunData run, int runIdx, List<(float X, float Y)> pixels) in cachedPointRuns)
                {
                    float penWidth = PenWidthFor(run.runName);
                    canvas.FillColor = ColorFor(runIdx, run.runName);
                    foreach ((float px, float py) in pixels)
                    {
                        canvas.FillCircle(px, py, penWidth);
                    }
                }
            }
        }

        if (CursorTimes != null)
        {
            const float boxSize = 8;
            foreach ((RunData run, int runIdx, List<(float Time, float X, float Y)> runPoints) in cachedRunPoints)
            {
                if (CursorEligibleRuns != null && !CursorEligibleRuns.Contains(run.runName))
                {
                    continue;
                }
                if (!CursorTimes.TryGetValue(run.runName, out float runCursorTime))
                {
                    continue; // no data on the Strip Chart's current axis - no box for it
                }
                int cursorIdx = FindNearestPointIndex(runPoints, runCursorTime);
                bool invertedRun = IsInverted(run.runName);
                (float _, float xVal, float yVal) = runPoints[cursorIdx];
                float px = scaleX(xVal);
                // the box cursor mirrors with its run so it lands on the drawn trace
                float py = invertedRun ? plotTop + plotBottom - scaleY(yVal) : scaleY(yVal);

                if (DisplayMode == ChartDisplayMode.CursorTrail && CursorTrailPointCount > 0)
                {
                    // a short line through only the points leading up to the cursor (earlier
                    // in time, not the look-ahead), ending at the cursor point - rebuilt every
                    // repaint since it moves with the cursor, but it's at most N+1 points so
                    // there's nothing worth caching
                    int first = Math.Max(0, cursorIdx - CursorTrailPointCount);
                    int last = cursorIdx;
                    if (last > first)
                    {
                        PathF trail = new();
                        for (int i = first; i <= last; i++)
                        {
                            (float _, float trailX, float trailY) = runPoints[i];
                            float trailPx = scaleX(trailX);
                            float trailPy = invertedRun ? plotTop + plotBottom - scaleY(trailY) : scaleY(trailY);
                            if (i == first)
                            {
                                trail.MoveTo(trailPx, trailPy);
                            }
                            else
                            {
                                trail.LineTo(trailPx, trailPy);
                            }
                        }
                        // clip - when zoomed, trail points can fall outside the plot area
                        canvas.SaveState();
                        canvas.ClipRectangle(plotLeft, plotTop, plotWidth, plotHeight);
                        canvas.StrokeColor = ColorFor(runIdx, run.runName);
                        canvas.StrokeSize = PenWidthFor(run.runName);
                        canvas.DrawPath(trail);
                        canvas.RestoreState();
                    }
                }

                canvas.StrokeColor = ColorFor(runIdx, run.runName);
                canvas.StrokeSize = 2;
                canvas.DrawRectangle(px - boxSize / 2, py - boxSize / 2, boxSize, boxSize);
            }
        }
    }

    /// <summary>Index of the nearest point by timestamp - the list is time-sorted (built
    /// from a SortedList), so a binary search works. Callers guarantee it's non-empty.</summary>
    private static int FindNearestPointIndex(List<(float Time, float X, float Y)> points, float target)
    {
        int lo = 0, hi = points.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (points[mid].Time < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        if (lo > 0 && Math.Abs(points[lo - 1].Time - target) < Math.Abs(points[lo].Time - target))
        {
            return lo - 1;
        }
        return lo;
    }

    private static void DrawCenteredMessage(ICanvas canvas, RectF dirtyRect, string message)
    {
        canvas.FontColor = Colors.Gray;
        canvas.FontSize = 14;
        canvas.DrawString(message, dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
    }
}
