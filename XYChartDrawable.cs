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
    /// Per-run real-timestamp window to zoom to - set from the Strip Chart's drag-to-zoom
    /// selection (converted per run via <see cref="StripChartDrawable.GetPerRunTimeRange"/>).
    /// Rescales this chart's own X/Y axes to fit only the points inside each run's window
    /// (still drawing the full trace, same as the WinForms app's zoom - points outside the
    /// window just fall outside the visible plot area). A run missing from this map (or a
    /// null map) auto-fits its full data range, unaffected.
    /// </summary>
    public IReadOnlyDictionary<string, (float TMin, float TMax)>? TimeRangeFilter { get; set; }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(dirtyRect);

        if (SelectedRuns.Count == 0)
        {
            DrawCenteredMessage(canvas, dirtyRect, "No runs selected");
            return;
        }

        bool anySelectedRunHasBothChannels = DataLogger.runData.Any(r =>
            SelectedRuns.Contains(r.runName) && r.channels.ContainsKey(XChannel) && r.channels.ContainsKey(YChannel));
        if (!anySelectedRunHasBothChannels)
        {
            DrawCenteredMessage(canvas, dirtyRect, $"No {XChannel}/{YChannel} data for the selected run(s)");
            return;
        }

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (RunData run in DataLogger.runData)
        {
            if (!SelectedRuns.Contains(run.runName) ||
                !run.channels.TryGetValue(XChannel, out DataChannel? xChan) ||
                !run.channels.TryGetValue(YChannel, out DataChannel? yChan) ||
                xChan.DataPoints.Count == 0 || yChan.DataPoints.Count == 0)
            {
                continue;
            }

            if (TimeRangeFilter != null && TimeRangeFilter.TryGetValue(run.runName, out (float TMin, float TMax) range))
            {
                // zoomed: auto-fit to only the points inside this run's selected time window
                foreach (KeyValuePair<float, float> point in xChan.DataPoints)
                {
                    if (point.Key < range.TMin || point.Key > range.TMax ||
                        !yChan.DataPoints.TryGetValue(point.Key, out float yVal))
                    {
                        continue;
                    }
                    minX = Math.Min(minX, point.Value);
                    maxX = Math.Max(maxX, point.Value);
                    minY = Math.Min(minY, yVal);
                    maxY = Math.Max(maxY, yVal);
                }
            }
            else
            {
                minX = Math.Min(minX, xChan.YRange[0]);
                maxX = Math.Max(maxX, xChan.YRange[1]);
                minY = Math.Min(minY, yChan.YRange[0]);
                maxY = Math.Max(maxY, yChan.YRange[1]);
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

        for (int runIdx = 0; runIdx < DataLogger.runData.Count; runIdx++)
        {
            RunData run = DataLogger.runData[runIdx];
            if (!SelectedRuns.Contains(run.runName) ||
                !run.channels.TryGetValue(XChannel, out DataChannel? xChan) ||
                !run.channels.TryGetValue(YChannel, out DataChannel? yChan))
            {
                continue;
            }

            Color color = ChartColors.ForRunIndex(runIdx);
            if (DisplayMode == ChartDisplayMode.Point)
            {
                canvas.FillColor = color;
                foreach (KeyValuePair<float, float> point in xChan.DataPoints)
                {
                    if (!yChan.DataPoints.TryGetValue(point.Key, out float yVal))
                    {
                        continue;
                    }
                    canvas.FillCircle(scaleX(point.Value), scaleY(yVal), 1.5f);
                }
            }
            else
            {
                canvas.StrokeColor = color;
                canvas.StrokeSize = 1.5f;
                PathF path = new();
                bool first = true;
                foreach (KeyValuePair<float, float> point in xChan.DataPoints)
                {
                    if (!yChan.DataPoints.TryGetValue(point.Key, out float yVal))
                    {
                        continue;
                    }
                    float px = scaleX(point.Value);
                    float py = scaleY(yVal);
                    if (first)
                    {
                        path.MoveTo(px, py);
                        first = false;
                    }
                    else
                    {
                        path.LineTo(px, py);
                    }
                }
                canvas.DrawPath(path);
            }
        }

        if (CursorTime.HasValue)
        {
            const float boxSize = 8;
            for (int runIdx = 0; runIdx < DataLogger.runData.Count; runIdx++)
            {
                RunData run = DataLogger.runData[runIdx];
                if (!SelectedRuns.Contains(run.runName) ||
                    (CursorEligibleRuns != null && !CursorEligibleRuns.Contains(run.runName)) ||
                    !run.channels.TryGetValue(XChannel, out DataChannel? xChan) ||
                    !run.channels.TryGetValue(YChannel, out DataChannel? yChan) ||
                    xChan.DataPoints.Count == 0)
                {
                    continue;
                }
                float? nearestKey = FindNearestKey(xChan.DataPoints.Keys, CursorTime.Value);
                if (nearestKey == null ||
                    !xChan.DataPoints.TryGetValue(nearestKey.Value, out float xVal) ||
                    !yChan.DataPoints.TryGetValue(nearestKey.Value, out float yVal))
                {
                    continue;
                }
                float px = scaleX(xVal);
                float py = scaleY(yVal);
                canvas.StrokeColor = ChartColors.ForRunIndex(runIdx);
                canvas.StrokeSize = 2;
                canvas.DrawRectangle(px - boxSize / 2, py - boxSize / 2, boxSize, boxSize);
            }
        }
    }

    private static float? FindNearestKey(IList<float> keys, float target)
    {
        if (keys.Count == 0)
        {
            return null;
        }
        int lo = 0, hi = keys.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid] < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        if (lo > 0 && Math.Abs(keys[lo - 1] - target) < Math.Abs(keys[lo] - target))
        {
            return keys[lo - 1];
        }
        return keys[lo];
    }

    private static void DrawCenteredMessage(ICanvas canvas, RectF dirtyRect, string message)
    {
        canvas.FontColor = Colors.Gray;
        canvas.FontSize = 14;
        canvas.DrawString(message, dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
    }
}
