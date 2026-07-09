namespace YamuraView;

/// <summary>How a chart's traces are drawn: connected lines, or individual points with no
/// connecting line - set independently per chart (Strip Chart, Track Map, Traction Circle),
/// not shared across all of them.</summary>
public enum ChartDisplayMode
{
    Line,
    Point,
}
