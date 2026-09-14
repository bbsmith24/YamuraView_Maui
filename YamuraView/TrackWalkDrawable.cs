using YamuraView.Core;

namespace YamuraView;

/// <summary>
/// Renders a <see cref="TrackMap"/> being authored on the track-walk page: the recorded GPS
/// breadcrumb trail as the track outline, the start/sector/finish lines (as segments with a
/// travel-direction arrow), the note pins, and the live GPS position. Uses one equal-scale
/// projection (equirectangular meters, longitude scaled by cos(latitude)) so the map isn't
/// distorted, and exposes <see cref="PixelToGeo"/>/<see cref="GeoToPixel"/> so the page can
/// hit-test taps and drag placed items. The transform is (re)computed on each <see cref="Draw"/>
/// and cached for those conversions.
/// </summary>
public sealed class TrackWalkDrawable : IDrawable
{
    private const double MetersPerDegreeLatitude = 111320.0;

    public TrackMap Map { get; set; } = new();
    /// <summary>The live GPS fix, drawn as a marker; null when not recording / no fix yet.</summary>
    public (double Lat, double Lon)? CurrentPosition { get; set; }
    /// <summary>The currently selected line/note, drawn highlighted.</summary>
    public TrackLine? SelectedLine { get; set; }
    public TrackNote? SelectedNote { get; set; }

    // cached transform from the last Draw, for pixel<->geo conversion
    private bool hasTransform;
    private double refLat, refLon, cosRef;
    private double minEast, minNorth, unitsPerPixel;
    private float offsetX, offsetY, usedHeight;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(dirtyRect);

        // gather every coordinate that should be in view
        List<(double Lat, double Lon)> all = new();
        foreach (TrackPoint p in Map.Walk)
        {
            all.Add((p.Latitude, p.Longitude));
        }
        foreach (TrackLine line in Map.Lines)
        {
            (var a, var b) = TrackMapGeometry.GetLineEndpoints(line, Map.Units);
            all.Add(a);
            all.Add(b);
        }
        foreach (TrackNote note in Map.Notes)
        {
            all.Add((note.Latitude, note.Longitude));
        }
        if (CurrentPosition.HasValue)
        {
            all.Add(CurrentPosition.Value);
        }

        if (all.Count == 0)
        {
            hasTransform = false;
            canvas.FontColor = Colors.Gray;
            canvas.FontSize = 14;
            canvas.DrawString("Start recording to capture the track walk", dirtyRect,
                HorizontalAlignment.Center, VerticalAlignment.Center);
            return;
        }

        refLat = all.Average(p => p.Lat);
        refLon = all.Average(p => p.Lon);
        cosRef = CosLat(refLat);

        double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
        foreach ((double lat, double lon) in all)
        {
            (double e, double n) = Project(lat, lon);
            minE = Math.Min(minE, e);
            maxE = Math.Max(maxE, e);
            minN = Math.Min(minN, n);
            maxN = Math.Max(maxN, n);
        }

        const float margin = 16f;
        float plotLeft = margin, plotTop = margin;
        float plotW = dirtyRect.Width - 2 * margin;
        float plotH = dirtyRect.Height - 2 * margin;
        if (plotW <= 0 || plotH <= 0)
        {
            hasTransform = false;
            return;
        }

        double rangeE = maxE - minE, rangeN = maxN - minN;
        // pad a degenerate (single point / straight line) range so there's something to scale
        if (rangeE < 1.0) { double c = (minE + maxE) / 2; minE = c - 10; maxE = c + 10; rangeE = 20; }
        if (rangeN < 1.0) { double c = (minN + maxN) / 2; minN = c - 10; maxN = c + 10; rangeN = 20; }

        unitsPerPixel = Math.Max(rangeE / plotW, rangeN / plotH);
        float usedW = (float)(rangeE / unitsPerPixel);
        usedHeight = (float)(rangeN / unitsPerPixel);
        offsetX = plotLeft + (plotW - usedW) / 2f;
        offsetY = plotTop + (plotH - usedHeight) / 2f;
        minEast = minE;
        minNorth = minN;
        hasTransform = true;

        // trail outline
        if (Map.Walk.Count > 1)
        {
            PathF path = new();
            bool started = false;
            foreach (TrackPoint p in Map.Walk)
            {
                PointF px = ToPixel(p.Latitude, p.Longitude);
                if (!started) { path.MoveTo(px.X, px.Y); started = true; }
                else { path.LineTo(px.X, px.Y); }
            }
            canvas.StrokeColor = Color.FromArgb("#4FC3F7");
            canvas.StrokeSize = 2;
            canvas.DrawPath(path);
        }

        // timing lines
        foreach (TrackLine line in Map.Lines)
        {
            (var a, var b) = TrackMapGeometry.GetLineEndpoints(line, Map.Units);
            PointF pa = ToPixel(a.Lat, a.Lon);
            PointF pb = ToPixel(b.Lat, b.Lon);
            Color color = line.Type switch
            {
                LineType.Start => Colors.LimeGreen,
                LineType.Finish => Colors.Red,
                _ => Colors.Gold,
            };
            bool selected = ReferenceEquals(line, SelectedLine);
            if (selected)
            {
                canvas.StrokeColor = Colors.White;
                canvas.StrokeSize = 6;
                canvas.DrawLine(pa, pb);
            }
            canvas.StrokeColor = color;
            canvas.StrokeSize = selected ? 3 : 2;
            canvas.DrawLine(pa, pb);

            // travel-direction arrow from the line center
            PointF center = ToPixel(line.Latitude, line.Longitude);
            double theta = line.Heading * Math.PI / 180.0;
            float ax = (float)Math.Sin(theta); // east
            float ay = (float)-Math.Cos(theta); // north is -pixelY
            const float arrow = 18f;
            canvas.DrawLine(center, new PointF(center.X + ax * arrow, center.Y + ay * arrow));
        }

        // note pins
        foreach (TrackNote note in Map.Notes)
        {
            PointF p = ToPixel(note.Latitude, note.Longitude);
            bool selected = ReferenceEquals(note, SelectedNote);
            canvas.FillColor = selected ? Colors.White : Colors.Orange;
            canvas.FillCircle(p.X, p.Y, selected ? 7 : 5);
            canvas.StrokeColor = Colors.Black;
            canvas.StrokeSize = 1;
            canvas.DrawCircle(p.X, p.Y, selected ? 7 : 5);
        }

        // live position
        if (CurrentPosition.HasValue)
        {
            PointF p = ToPixel(CurrentPosition.Value.Lat, CurrentPosition.Value.Lon);
            canvas.FillColor = Colors.Cyan;
            canvas.FillCircle(p.X, p.Y, 6);
            canvas.StrokeColor = Colors.Black;
            canvas.StrokeSize = 1;
            canvas.DrawCircle(p.X, p.Y, 6);
        }
    }

    /// <summary>Geographic coordinate at a pixel position on the last-drawn map, or null if
    /// nothing has been drawn yet.</summary>
    public (double Lat, double Lon)? PixelToGeo(Point p)
    {
        if (!hasTransform)
        {
            return null;
        }
        double e = minEast + (p.X - offsetX) * unitsPerPixel;
        double n = minNorth + (offsetY + usedHeight - p.Y) * unitsPerPixel;
        return (refLat + n / MetersPerDegreeLatitude, refLon + e / (MetersPerDegreeLatitude * cosRef));
    }

    /// <summary>Pixel position of a geographic coordinate on the last-drawn map, or null if
    /// nothing has been drawn yet.</summary>
    public PointF? GeoToPixel(double lat, double lon) => hasTransform ? ToPixel(lat, lon) : null;

    private PointF ToPixel(double lat, double lon)
    {
        (double e, double n) = Project(lat, lon);
        float px = offsetX + (float)((e - minEast) / unitsPerPixel);
        float py = offsetY + usedHeight - (float)((n - minNorth) / unitsPerPixel);
        return new PointF(px, py);
    }

    private (double East, double North) Project(double lat, double lon) =>
        ((lon - refLon) * MetersPerDegreeLatitude * cosRef, (lat - refLat) * MetersPerDegreeLatitude);

    private static double CosLat(double latDegrees)
    {
        double c = Math.Cos(latDegrees * Math.PI / 180.0);
        return Math.Abs(c) < 0.05 ? 0.05 : c;
    }
}
