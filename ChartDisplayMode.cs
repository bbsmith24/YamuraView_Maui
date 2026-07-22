namespace YamuraView;

/// <summary>How a chart's traces are drawn: connected lines, individual points with no
/// connecting line, or <see cref="LinePoint"/> for both (points drawn on top of the line) -
/// set independently per chart (Strip Chart, Track Map, Traction Circle), not shared across
/// all of them. <see cref="CursorOnly"/> hides the traces entirely and shows just the
/// tracking box cursor; <see cref="CursorTrail"/> additionally draws a short line through
/// only the points surrounding the cursor - both offered only for the Traction Circle.</summary>
public enum ChartDisplayMode
{
    Line,
    Point,
    LinePoint,
    CursorOnly,
    CursorTrail,
}
