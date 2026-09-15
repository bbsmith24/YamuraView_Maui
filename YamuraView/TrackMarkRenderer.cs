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
            PathF tri = new();
            tri.MoveTo(0, -half);   // apex points up (toward the orientation bearing after rotation)
            tri.LineTo(half, half);
            tri.LineTo(-half, half);
            tri.Close();
            canvas.FillPath(tri);
            canvas.DrawPath(tri);
        }
        else
        {
            canvas.FillRectangle(-half, -half, half * 2, half * 2);
            canvas.DrawRectangle(-half, -half, half * 2, half * 2);
        }
        canvas.RestoreState();
    }
}
