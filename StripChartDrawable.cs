using YamuraView.Core;

namespace YamuraView;

/// <summary>
/// Plot of one or more channels across all loaded runs against a shared X-axis channel
/// (equivalent to the WinForms app's "Strip Chart" with its X-axis combo box). When
/// <see cref="XAxisChannel"/> is "Time" (the default), each run's TimeOffset (from
/// AlignTime) shifts its data so runs line up; for any other X-axis channel, points are
/// joined to each Y channel by matching timestamp, same as the XY charts. Which (run,
/// channel) combinations are plotted is driven by <see cref="SelectedSeries"/>, matching
/// the WinForms tri-state tree's per-run channel selection - set by the page hosting
/// this drawable.
/// </summary>
public class StripChartDrawable : IDrawable
{
    public const string TimeAxis = "Time";

    public DataLogger DataLogger { get; set; } = null!;
    public string XAxisChannel { get; set; } = TimeAxis;
    public ISet<(string RunName, string ChannelName)> SelectedSeries { get; set; } = new HashSet<(string, string)>();

    /// <summary>X-axis value to draw the vertical cursor line at; null hides it.</summary>
    public float? CursorTime { get; set; }

    /// <summary>Whether this chart's traces draw as connected lines or individual points - set
    /// independently per chart, not shared with Track Map/Traction Circle.</summary>
    public ChartDisplayMode DisplayMode { get; set; } = ChartDisplayMode.Line;

    /// <summary>
    /// Which stacked subgraph band (0-based) each channel name plots in - matches the WinForms
    /// app's "Assign to Graph" feature, letting channels with unrelated units (e.g. RPM vs.
    /// G-force) share the Strip Chart without sharing a Y scale. A channel missing from this
    /// map (or a null map) plots in band 0, so this is fully backward compatible with a single,
    /// full-height graph.
    /// </summary>
    public IReadOnlyDictionary<string, int>? ChannelGraphIndex { get; set; }

    private int GraphIndexFor(string channelName) =>
        ChannelGraphIndex != null && ChannelGraphIndex.TryGetValue(channelName, out int idx) ? idx : 0;

    /// <summary>
    /// Trace color for one specific (run, channel) pair, overriding the normal per-run
    /// auto-assigned color - scoped to just that one data set's instance of the channel, not
    /// shared with other runs' instances of the same channel name. A pair missing from this
    /// map (or a null map) keeps the default per-run color.
    /// </summary>
    public IReadOnlyDictionary<(string RunName, string ChannelName), Color>? ChannelColorOverride { get; set; }

    private Color ColorFor(RunData run, string channelName) =>
        ChannelColorOverride != null && ChannelColorOverride.TryGetValue((run.runName, channelName), out Color? overrideColor)
            ? overrideColor
            : ChartColors.ForRunIndex(DataLogger.runData.IndexOf(run));

    /// <summary>
    /// Channel names whose trace is flipped vertically within its subgraph band - matches the
    /// WinForms tree's "Invert" context menu item, which also applies to every run's instance
    /// of the channel at once. Only mirrors where the trace is drawn; the band's shared Y-axis
    /// min/max labels are unaffected (same as WinForms, which only marks the channel name).
    /// </summary>
    public ISet<string>? InvertedChannels { get; set; }

    private bool IsInverted(string channelName) => InvertedChannels?.Contains(channelName) == true;

    /// <summary>
    /// Manual X-axis zoom window (axis-space, same units as <see cref="XAxisChannel"/>), set by
    /// dragging a region on the chart. Only the X axis is affected - each band's Y range still
    /// auto-fits the full data, matching the WinForms app (a Strip Chart time-zoom never
    /// rescales Y). Null (either) means auto-fit to the full data range ("Zoom All").
    /// </summary>
    public float? ZoomMinX { get; set; }
    public float? ZoomMaxX { get; set; }

    /// <summary>
    /// Pixel X coordinates of an in-progress drag-to-zoom selection (both set while dragging),
    /// drawn as a translucent band; null when not dragging. Set by the page hosting this
    /// drawable from pointer-press/move/release events.
    /// </summary>
    public float? DragStartPixelX { get; set; }
    public float? DragCurrentPixelX { get; set; }

    /// <summary>Full (unzoomed) X-axis data range from the most recent Draw(), for sizing a
    /// pan scrollbar; null until something has been plotted.</summary>
    public float? DataMinX => hasValidScale ? cachedFullMinX : null;
    public float? DataMaxX => hasValidScale ? cachedFullMaxX : null;

    // cached from the most recent successful Draw(), so PixelXToTime/ConvertToTime can
    // invert mouse moves without redoing the whole data scan/range calc on every pointer event
    private float cachedMinX, cachedMaxX, cachedPlotLeft, cachedPlotRight;
    private float cachedFullMinX, cachedFullMaxX;
    private bool hasValidScale;
    private List<(RunData Run, string ChannelName, List<(float X, float Y)> Points)> cachedSeries = new();

    // one (time, axisValue) curve per run, built solely from XAxisChannel's own data - used
    // to convert an axis-space value (e.g. a distance) back to the actual timestamp it came
    // from, since Track Map/Traction Circle always key their data by real time regardless of
    // what the Strip Chart's X axis currently is
    private List<(RunData Run, List<(float RawTime, float AxisValue)> Points)> cachedAxisSeries = new();

    /// <summary>
    /// Converts a pixel X coordinate (from a pointer/mouse event over the GraphicsView)
    /// to the corresponding X-axis value (in <see cref="XAxisChannel"/>'s own units - e.g.
    /// seconds when it's Time, but whatever unit the chosen channel uses otherwise), using
    /// the scale from the last Draw() call. Returns null if nothing has been plotted yet.
    /// </summary>
    public float? PixelXToTime(float pixelX)
    {
        if (!hasValidScale || cachedPlotRight <= cachedPlotLeft)
        {
            return null;
        }
        float t = cachedMinX + (pixelX - cachedPlotLeft) / (cachedPlotRight - cachedPlotLeft) * (cachedMaxX - cachedMinX);
        return Math.Clamp(t, cachedMinX, cachedMaxX);
    }

    /// <summary>
    /// Converts an X-axis-space value (whatever <see cref="PixelXToTime"/> returned) to the
    /// real timestamp it corresponds to, by finding the nearest point on XAxisChannel's own
    /// (time, value) curve for whichever run is the closest match. Always converts, even when
    /// the axis is already Time, since AlignTime's per-run TimeOffset is display-only and
    /// isn't applied to the other charts' data.
    /// </summary>
    public float? ConvertToTime(float axisValue)
    {
        float bestTime = 0;
        float bestDist = float.MaxValue;
        bool found = false;
        foreach ((_, List<(float RawTime, float AxisValue)> points) in cachedAxisSeries)
        {
            if (points.Count == 0)
            {
                continue;
            }
            int lo = 0, hi = points.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (points[mid].AxisValue < axisValue)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            for (int idx = Math.Max(0, lo - 1); idx <= lo; idx++)
            {
                float dist = Math.Abs(points[idx].AxisValue - axisValue);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestTime = points[idx].RawTime;
                    found = true;
                }
            }
        }
        return found ? bestTime : null;
    }

    /// <summary>
    /// For each run, converts an axis-space [axisMin, axisMax] window (a Strip Chart
    /// drag-to-zoom selection) to that run's own real-timestamp window, by finding the nearest
    /// raw time for each boundary on the run's own XAxisChannel curve. Mirrors the WinForms
    /// app's per-dataset time-range filter, needed because a Time-axis selection is in
    /// TimeOffset-shifted display units while Track Map/Traction Circle are keyed by unshifted
    /// raw time - handing them a single shared window would be wrong whenever runs are aligned
    /// with different offsets.
    /// </summary>
    public IReadOnlyDictionary<string, (float TMin, float TMax)> GetPerRunTimeRange(float axisMin, float axisMax)
    {
        Dictionary<string, (float, float)> result = new();
        foreach ((RunData run, List<(float RawTime, float AxisValue)> points) in cachedAxisSeries)
        {
            float? tAtMin = FindNearestRawTime(points, axisMin);
            float? tAtMax = FindNearestRawTime(points, axisMax);
            if (tAtMin.HasValue && tAtMax.HasValue)
            {
                result[run.runName] = (Math.Min(tAtMin.Value, tAtMax.Value), Math.Max(tAtMin.Value, tAtMax.Value));
            }
        }
        return result;
    }

    private static float? FindNearestRawTime(List<(float RawTime, float AxisValue)> points, float axisValue)
    {
        if (points.Count == 0)
        {
            return null;
        }
        int lo = 0, hi = points.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (points[mid].AxisValue < axisValue)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        float bestDist = float.MaxValue;
        float bestTime = points[lo].RawTime;
        for (int idx = Math.Max(0, lo - 1); idx <= lo; idx++)
        {
            float dist = Math.Abs(points[idx].AxisValue - axisValue);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestTime = points[idx].RawTime;
            }
        }
        return bestTime;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(dirtyRect);

        if (DataLogger.runData.Count == 0)
        {
            hasValidScale = false;
            cachedSeries = new();
            cachedAxisSeries = new();
            DrawCenteredMessage(canvas, dirtyRect, "No data loaded");
            return;
        }

        if (SelectedSeries.Count == 0)
        {
            hasValidScale = false;
            cachedSeries = new();
            cachedAxisSeries = new();
            DrawCenteredMessage(canvas, dirtyRect, "No channels selected");
            return;
        }

        bool xIsTime = XAxisChannel == TimeAxis;

        // gather (x, y) points per run/channel once, since building them requires a join
        // when the X axis isn't Time - reused below for range calc and drawing
        List<(RunData Run, string ChannelName, List<(float X, float Y)> Points)> series = new();
        List<(RunData Run, List<(float RawTime, float AxisValue)> Points)> axisSeries = new();
        foreach (RunData run in DataLogger.runData)
        {
            DataChannel? xChan = null;
            if (xIsTime)
            {
                run.channels.TryGetValue(TimeAxis, out xChan);
            }
            else if (!run.channels.TryGetValue(XAxisChannel, out xChan))
            {
                continue;
            }

            if (xChan != null && xChan.DataPoints.Count > 0)
            {
                List<(float, float)> axisPoints = new();
                foreach (KeyValuePair<float, float> point in xChan.DataPoints)
                {
                    float axisVal = xIsTime ? point.Key + run.TimeOffset : point.Value;
                    axisPoints.Add((point.Key, axisVal));
                }
                axisSeries.Add((run, axisPoints));
            }

            foreach ((string channelName, DataChannel yChan) in run.channels)
            {
                if (!SelectedSeries.Contains((run.runName, channelName)) || yChan.DataPoints.Count == 0)
                {
                    continue;
                }
                List<(float, float)> points = new();
                foreach (KeyValuePair<float, float> point in yChan.DataPoints)
                {
                    float xVal;
                    if (xIsTime)
                    {
                        xVal = point.Key + run.TimeOffset;
                    }
                    else if (xChan == null || !xChan.DataPoints.TryGetValue(point.Key, out xVal))
                    {
                        continue;
                    }
                    points.Add((xVal, point.Value));
                }
                if (points.Count > 0)
                {
                    series.Add((run, channelName, points));
                }
            }
        }

        cachedAxisSeries = axisSeries;

        if (series.Count == 0)
        {
            hasValidScale = false;
            cachedSeries = new();
            DrawCenteredMessage(canvas, dirtyRect, "Not enough data to plot");
            return;
        }

        cachedSeries = series;

        float minX = float.MaxValue, maxX = float.MinValue;
        foreach ((_, _, List<(float X, float Y)> points) in series)
        {
            foreach ((float x, _) in points)
            {
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
            }
        }
        if (minX >= maxX)
        {
            minX -= 1f;
            maxX += 1f;
        }
        cachedFullMinX = minX;
        cachedFullMaxX = maxX;

        // a drag-to-zoom selection narrows the visible X window without touching the data
        // itself - points outside it just fall outside the drawn/plot area, same as any zoom
        if (ZoomMinX.HasValue && ZoomMaxX.HasValue && ZoomMaxX.Value > ZoomMinX.Value)
        {
            minX = ZoomMinX.Value;
            maxX = ZoomMaxX.Value;
        }

        // one Y range per stacked subgraph band, from only the channels assigned to it -
        // matches the WinForms app's "Assign to Graph" feature (e.g. RPM in its own band so
        // it doesn't get flattened by sharing a Y scale with G-force channels)
        int graphCount = 1;
        foreach ((_, string channelName, _) in series)
        {
            graphCount = Math.Max(graphCount, GraphIndexFor(channelName) + 1);
        }
        float[] bandMinY = new float[graphCount];
        float[] bandMaxY = new float[graphCount];
        for (int g = 0; g < graphCount; g++)
        {
            bandMinY[g] = float.MaxValue;
            bandMaxY[g] = float.MinValue;
        }
        foreach ((_, string channelName, List<(float X, float Y)> points) in series)
        {
            int g = GraphIndexFor(channelName);
            foreach ((_, float y) in points)
            {
                bandMinY[g] = Math.Min(bandMinY[g], y);
                bandMaxY[g] = Math.Max(bandMaxY[g], y);
            }
        }
        for (int g = 0; g < graphCount; g++)
        {
            if (bandMinY[g] > bandMaxY[g])
            {
                // no channel ended up assigned to this band (a gap in saved graph indices)
                bandMinY[g] = -1f;
                bandMaxY[g] = 1f;
            }
            else if (bandMinY[g] == bandMaxY[g])
            {
                // flat data - pad so it doesn't collapse to a single line
                bandMinY[g] -= 1f;
                bandMaxY[g] += 1f;
            }
        }

        const float plotLeft = 46, plotTop = 8, rightMargin = 8, bottomMargin = 22;
        float plotRight = dirtyRect.Width - rightMargin;
        float plotBottom = dirtyRect.Height - bottomMargin;

        cachedMinX = minX;
        cachedMaxX = maxX;
        cachedPlotLeft = plotLeft;
        cachedPlotRight = plotRight;
        hasValidScale = true;

        const float bandGap = 8;
        float totalGap = graphCount > 1 ? bandGap * (graphCount - 1) : 0;
        float bandHeight = (plotBottom - plotTop - totalGap) / graphCount;

        float BandTop(int g) => plotTop + g * (bandHeight + bandGap);
        float BandBottom(int g) => BandTop(g) + bandHeight;

        for (int g = 0; g < graphCount; g++)
        {
            DrawBandAxis(canvas, plotLeft, BandTop(g), plotRight, BandBottom(g), bandMinY[g], bandMaxY[g], minX, maxX, xIsTime, isLastBand: g == graphCount - 1);
        }

        float ScaleX(float x) => plotLeft + (x - minX) / (maxX - minX) * (plotRight - plotLeft);
        float ScaleYForBand(int g, float y) => BandBottom(g) - (y - bandMinY[g]) / (bandMaxY[g] - bandMinY[g]) * bandHeight;
        // an inverted channel mirrors its trace vertically within its own band, reflecting the
        // normal pixel Y around the band's vertical center - the band's shared axis labels
        // (drawn above from the un-mirrored bandMinY/bandMaxY) don't change
        float ScaleYForChannel(int g, float y, bool inverted) =>
            inverted ? BandTop(g) + BandBottom(g) - ScaleYForBand(g, y) : ScaleYForBand(g, y);

        List<string>[] bandChannelNames = new List<string>[graphCount];
        for (int g = 0; g < graphCount; g++)
        {
            bandChannelNames[g] = new List<string>();
        }

        bool pointMode = DisplayMode == ChartDisplayMode.Point;
        foreach ((RunData run, string channelName, List<(float X, float Y)> points) in series)
        {
            if (points.Count < (pointMode ? 1 : 2))
            {
                continue;
            }
            int g = GraphIndexFor(channelName);
            bool inverted = IsInverted(channelName);
            string displayName = inverted ? channelName + " (inv)" : channelName;
            if (!bandChannelNames[g].Contains(displayName))
            {
                bandChannelNames[g].Add(displayName);
            }
            Color color = ColorFor(run, channelName);
            if (pointMode)
            {
                canvas.FillColor = color;
                foreach ((float x, float y) in points)
                {
                    canvas.FillCircle(ScaleX(x), ScaleYForChannel(g, y, inverted), 1.5f);
                }
            }
            else
            {
                canvas.StrokeColor = color;
                canvas.StrokeSize = 1.5f;
                PathF path = new();
                bool first = true;
                foreach ((float x, float y) in points)
                {
                    float px = ScaleX(x);
                    float py = ScaleYForChannel(g, y, inverted);
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

        for (int g = 0; g < graphCount; g++)
        {
            DrawLegend(canvas, plotLeft, BandTop(g), bandChannelNames[g]);
        }

        if (CursorTime.HasValue)
        {
            float cursorX = ScaleX(Math.Clamp(CursorTime.Value, minX, maxX));
            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = 1;
            canvas.DrawLine(cursorX, plotTop, cursorX, plotBottom);

            const float labelWidth = 140;
            bool labelsOnRight = cursorX < (plotLeft + plotRight) / 2;
            float labelX = labelsOnRight ? cursorX + 4 : cursorX - labelWidth;
            float labelY = plotTop + 2;

            // X-axis value readout at the top of the cursor line, so it's clear exactly
            // which moment (or distance, etc.) the tracked values below belong to
            string axisSuffix = xIsTime ? " s" : "";
            canvas.FontColor = Colors.White;
            canvas.FontSize = 12;
            canvas.DrawString($"{XAxisChannel}: {CursorTime.Value:0.##}{axisSuffix}", labelX, labelY, labelWidth, 16, HorizontalAlignment.Left, VerticalAlignment.Top);
            labelY += 16;

            foreach ((RunData run, string channelName, List<(float X, float Y)> points) in series)
            {
                if (points.Count == 0)
                {
                    continue;
                }
                float nearestY = FindNearestY(points, CursorTime.Value);
                string displayName = IsInverted(channelName) ? channelName + " (inv)" : channelName;
                canvas.FontColor = ColorFor(run, channelName);
                canvas.FontSize = 11;
                canvas.DrawString($"{displayName}={nearestY:0.##}", labelX, labelY, labelWidth, 14, HorizontalAlignment.Left, VerticalAlignment.Top);
                labelY += 14;
            }
        }

        // in-progress drag-to-zoom selection - a translucent band the user is still dragging
        // out, applied as the actual zoom window on release
        if (DragStartPixelX.HasValue && DragCurrentPixelX.HasValue)
        {
            float dragLeft = Math.Min(DragStartPixelX.Value, DragCurrentPixelX.Value);
            float dragRight = Math.Max(DragStartPixelX.Value, DragCurrentPixelX.Value);
            canvas.FillColor = Colors.White.WithAlpha(0.15f);
            canvas.FillRectangle(dragLeft, plotTop, dragRight - dragLeft, plotBottom - plotTop);
            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = 1;
            canvas.DrawRectangle(dragLeft, plotTop, dragRight - dragLeft, plotBottom - plotTop);
        }
    }

    private static float FindNearestY(List<(float X, float Y)> points, float target)
    {
        int lo = 0, hi = points.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (points[mid].X < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        if (lo > 0 && Math.Abs(points[lo - 1].X - target) < Math.Abs(points[lo].X - target))
        {
            return points[lo - 1].Y;
        }
        return points[lo].Y;
    }

    /// <summary>Draws one stacked subgraph band's border and Y min/max labels; only the
    /// bottom-most band also gets the shared X-axis min/max labels below it.</summary>
    private static void DrawBandAxis(ICanvas canvas, float left, float top, float right, float bottom, float minY, float maxY, float minX, float maxX, bool xIsTime, bool isLastBand)
    {
        canvas.StrokeColor = Colors.Gray;
        canvas.StrokeSize = 1;
        canvas.DrawLine(left, bottom, right, bottom);
        canvas.DrawLine(left, top, left, bottom);

        canvas.FontColor = Colors.LightGray;
        canvas.FontSize = 10;
        canvas.DrawString(maxY.ToString("0.##"), 2, top, left - 6, 14, HorizontalAlignment.Right, VerticalAlignment.Top);
        canvas.DrawString(minY.ToString("0.##"), 2, bottom - 14, left - 6, 14, HorizontalAlignment.Right, VerticalAlignment.Bottom);

        if (isLastBand)
        {
            string suffix = xIsTime ? " s" : "";
            canvas.DrawString(minX.ToString("0.0"), left, bottom + 2, 60, 16, HorizontalAlignment.Left, VerticalAlignment.Top);
            canvas.DrawString(maxX.ToString("0.0") + suffix, right - 60, bottom + 2, 60, 16, HorizontalAlignment.Right, VerticalAlignment.Top);
        }
    }

    private static void DrawLegend(ICanvas canvas, float left, float top, IReadOnlyList<string> channelNames)
    {
        // plain white: a channel name can have different per-run color overrides, so there's
        // no single color left to show here once color is scoped to a specific (run, channel) pair
        canvas.FontColor = Colors.White;
        canvas.FontSize = 11;
        float y = top + 2;
        foreach (string name in channelNames)
        {
            canvas.DrawString(name, left + 4, y, 120, 14, HorizontalAlignment.Left, VerticalAlignment.Top);
            y += 14;
        }
    }

    private static void DrawCenteredMessage(ICanvas canvas, RectF dirtyRect, string message)
    {
        canvas.FontColor = Colors.Gray;
        canvas.FontSize = 14;
        canvas.DrawString(message, dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center);
    }
}
