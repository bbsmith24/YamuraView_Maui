using YamuraView.Core;

namespace YamuraView;

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

    /// <summary>
    /// Time value to show a box cursor at (the nearest data point for each selected run),
    /// tracking the Strip Chart's cursor - matches the WinForms app's cross-chart mouse
    /// tracking (BOX cursor mode). Null hides it.
    /// </summary>
    public float? CursorTime { get; set; }

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
            foreach (KeyValuePair<float, float> point in xChan.DataPoints)
            {
                if (!yChan.DataPoints.TryGetValue(point.Key, out float yVal))
                {
                    continue;
                }
                points.Add((point.Key, point.Value, yVal));
                minX = Math.Min(minX, point.Value);
                maxX = Math.Max(maxX, point.Value);
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
            // (a run missing from the filter auto-fits its full data, unaffected)
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
        }
        else
        {
            scaleX = x => plotLeft + (x - minX) / rangeX * plotWidth;
            scaleY = y => plotBottom - (y - minY) / rangeY * plotHeight;
        }

        canvas.StrokeColor = Colors.DimGray;
        canvas.StrokeSize = 1;
        canvas.DrawRectangle(plotLeft, plotTop, plotWidth, plotHeight);

        // an inverted run mirrors its trace vertically, reflecting the normal pixel Y
        // around the plot's vertical center (with EqualScale the used area is centered
        // in the plot, so this reflects around the used area's center too)
        if (DisplayMode == ChartDisplayMode.Point)
        {
            foreach ((RunData run, int runIdx, List<(float Time, float X, float Y)> runPoints) in cachedRunPoints)
            {
                float penWidth = PenWidthFor(run.runName);
                bool invertedRun = IsInverted(run.runName);
                canvas.FillColor = ColorFor(runIdx, run.runName);
                float lastPx = float.MinValue, lastPy = float.MinValue;
                foreach ((_, float xVal, float yVal) in runPoints)
                {
                    float px = scaleX(xVal);
                    float py = invertedRun ? plotTop + plotBottom - scaleY(yVal) : scaleY(yVal);
                    if (px < plotLeft - penWidth || px > plotRight + penWidth ||
                        py < plotTop - penWidth || py > plotBottom + penWidth)
                    {
                        continue; // outside the visible plot (when zoomed)
                    }
                    if (Math.Abs(px - lastPx) < 0.5f && Math.Abs(py - lastPy) < 0.5f)
                    {
                        continue; // sub-pixel duplicate of the previous drawn point
                    }
                    canvas.FillCircle(px, py, penWidth);
                    lastPx = px;
                    lastPy = py;
                }
            }
        }
        else
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

        if (CursorTime.HasValue)
        {
            const float boxSize = 8;
            foreach ((RunData run, int runIdx, List<(float Time, float X, float Y)> runPoints) in cachedRunPoints)
            {
                if (CursorEligibleRuns != null && !CursorEligibleRuns.Contains(run.runName))
                {
                    continue;
                }
                (float _, float xVal, float yVal) = FindNearestPoint(runPoints, CursorTime.Value);
                float px = scaleX(xVal);
                // the box cursor mirrors with its run so it lands on the drawn trace
                float py = IsInverted(run.runName) ? plotTop + plotBottom - scaleY(yVal) : scaleY(yVal);
                canvas.StrokeColor = ColorFor(runIdx, run.runName);
                canvas.StrokeSize = 2;
                canvas.DrawRectangle(px - boxSize / 2, py - boxSize / 2, boxSize, boxSize);
            }
        }
    }

    /// <summary>Nearest point by timestamp - the list is time-sorted (built from a
    /// SortedList), so a binary search works. Callers guarantee it's non-empty.</summary>
    private static (float Time, float X, float Y) FindNearestPoint(List<(float Time, float X, float Y)> points, float target)
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
            return points[lo - 1];
        }
        return points[lo];
    }

    private static void DrawCenteredMessage(ICanvas canvas, RectF dirtyRect, string message)
    {
        canvas.FontColor = Colors.Gray;
        canvas.FontSize = 14;
        canvas.DrawString(message, dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
    }
}
