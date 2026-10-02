using YamuraView.Core;

namespace YamuraView;

/// <summary>
/// Draws a <see cref="TrackMark"/> consistently on any canvas - the track-walk editor and the
/// analysis overlay both use this so a mark looks identical everywhere. A square is a standing
/// cone (or any non-directional marker); a triangle is a lay-down pointer cone whose apex points
/// along the mark's orientation. Orientation is degrees, 0 = up/north, increasing clockwise.
/// </summary>
internal static class TrackMarkRenderer
{
    /// <summary>Default mark size in pixels (a square's side); user-adjustable in Settings.</summary>
    public const float DefaultMarkSize = 16f;

    public static void Draw(ICanvas canvas, float cx, float cy, float half, float orientationDegrees,
        MarkShape shape, Color fill, Color stroke, float strokeSize = 1f)
    {
        canvas.SaveState();
        canvas.Translate(cx, cy);
        canvas.Rotate(orientationDegrees); // clockwise; 0 keeps the shape upright
        canvas.FillColor = fill;
        canvas.StrokeColor = stroke;
        canvas.StrokeSize = strokeSize;
        if (shape == MarkShape.Triangle)
        {
            // long and narrow so the pointing direction reads at a glance; the apex reaches
            // further from the mark position than the base does
            PathF tri = new();
            tri.MoveTo(0, -half * 1.6f);   // apex points up (toward the orientation bearing after rotation)
            tri.LineTo(half * 0.6f, half * 0.8f);
            tri.LineTo(-half * 0.6f, half * 0.8f);
            tri.Close();
            canvas.FillPath(tri);
            canvas.DrawPath(tri);
            // the cone's base plate: a bar across the base, 1.5x the marker size (half * 2) wide,
            // so the mark reads as a laid-down traffic cone
            float baseHalfWidth = half * 1.5f;
            float baseThickness = Math.Max(2f, half * 0.25f);
            canvas.FillRectangle(-baseHalfWidth, half * 0.8f, baseHalfWidth * 2, baseThickness);
            canvas.DrawRectangle(-baseHalfWidth, half * 0.8f, baseHalfWidth * 2, baseThickness);
        }
        else
        {
            canvas.FillRectangle(-half, -half, half * 2, half * 2);
            canvas.DrawRectangle(-half, -half, half * 2, half * 2);
        }
        canvas.RestoreState();
    }
}
