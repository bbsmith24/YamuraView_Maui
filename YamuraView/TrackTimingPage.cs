using YamuraView.Core;

namespace YamuraView;

/// <summary>
/// Read-only lap/sector timing of every loaded run against the active track map (see
/// <see cref="TrackMapAlignment.GetRunTimings"/>), shown as a table: one row per lap (runs down the
/// side), one column per timing segment across the top - S1 (Start→S1), S2 (S1→S2), … , Finish
/// (Sn→Finish) - plus a Lap total. Point-to-point maps show one row per run; circuit maps show a
/// row per start-line crossing. The single fastest lap and the fastest time in each segment column,
/// across every run and lap, are highlighted.
/// </summary>
public sealed class TrackTimingPage : ContentPage
{
    // motorsport-convention purple for the session best; a translucent fill so the cell reads as
    // highlighted in both light and dark themes without hurting text contrast, plus bold text
    private static readonly Color HighlightBackground = Color.FromRgba(0x9B, 0x30, 0xFF, 0x48);

    // two times count as the same when comparing against the computed minimum (float splits)
    private const float TimeEpsilon = 1e-4f;

    // segment key for the closing Sn→Finish span, so it's compared only against other laps'
    // finish segments (sector lines carry their own Order 1..N as the key)
    private const int FinishSegmentKey = int.MaxValue;

    /// <summary>One timing segment of a lap: the span between two consecutive timing lines
    /// (Start→S1, Si→Si+1, or Sn→Finish).</summary>
    private readonly record struct Segment(string Label, float SegmentTime, int Key);

    public TrackTimingPage(DataLogger dataLogger, TrackMap map)
    {
        Title = "Lap/Sector Times";

        List<(RunData Run, RunTiming Timing)> timings = TrackMapAlignment.GetRunTimings(dataLogger, map).ToList();

        // segment columns come from the map's sector lines (Order 1..N), so every lap lines up in
        // the same columns even if a lap missed a sector; the closing Sn→Finish span is a column too
        List<int> sectorOrders = map.Lines
            .Where(l => l.Type == LineType.Sector)
            .Select(l => l.Order)
            .Distinct()
            .OrderBy(o => o)
            .ToList();

        // first pass: the fastest finished lap overall, and the fastest time for each segment
        // (keyed by sector Order, or FinishSegmentKey for the closing span), across every lap
        float? fastestLap = null;
        Dictionary<int, float> fastestSegment = new();
        foreach ((_, RunTiming timing) in timings)
        {
            foreach (LapTiming lap in timing.Laps)
            {
                if (lap.LapTime.HasValue && (!fastestLap.HasValue || lap.LapTime.Value < fastestLap.Value))
                {
                    fastestLap = lap.LapTime.Value;
                }
                foreach (Segment segment in Segments(lap))
                {
                    if (!fastestSegment.TryGetValue(segment.Key, out float best) || segment.SegmentTime < best)
                    {
                        fastestSegment[segment.Key] = segment.SegmentTime;
                    }
                }
            }
        }

        // column layout: 0 = run/lap, 1..N = sector segments, N+1 = Finish segment, N+2 = Lap total
        int finishCol = 1 + sectorOrders.Count;
        int lapCol = finishCol + 1;
        int totalCols = lapCol + 1;

        Grid grid = new() { ColumnSpacing = 10, RowSpacing = 4 };
        for (int c = 0; c < totalCols; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        int row = 0;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Add(Cell("Run", header: true), 0, row);
        for (int i = 0; i < sectorOrders.Count; i++)
        {
            grid.Add(Cell($"S{sectorOrders[i]}", header: true, numeric: true), 1 + i, row);
        }
        grid.Add(Cell("Finish", header: true, numeric: true), finishCol, row);
        grid.Add(Cell("Lap", header: true, numeric: true), lapCol, row);
        row++;

        foreach ((RunData run, RunTiming timing) in timings)
        {
            if (timing.Laps.Count == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.Add(Cell(run.runName), 0, row);
                Label noCrossing = Cell("(no start-line crossing)", muted: true);
                grid.Add(noCrossing, 1, row);
                Grid.SetColumnSpan(noCrossing, Math.Max(1, totalCols - 1));
                row++;
                continue;
            }
            foreach (LapTiming lap in timing.Laps)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                string rowLabel = map.SameStartFinish ? $"{run.runName}  ·  Lap {lap.LapNumber}" : run.runName;
                grid.Add(Cell(rowLabel), 0, row);

                foreach (Segment segment in Segments(lap))
                {
                    int col = segment.Key == FinishSegmentKey ? finishCol : 1 + sectorOrders.IndexOf(segment.Key);
                    if (col < 1)
                    {
                        continue; // a segment for a sector not in the map's list (shouldn't happen)
                    }
                    bool segmentIsFastest = fastestSegment.TryGetValue(segment.Key, out float best) && NearlyEqual(segment.SegmentTime, best);
                    grid.Add(Cell(FormatTime(segment.SegmentTime), numeric: true, highlight: segmentIsFastest), col, row);
                }

                bool lapIsFastest = lap.LapTime.HasValue && fastestLap.HasValue && NearlyEqual(lap.LapTime.Value, fastestLap.Value);
                grid.Add(Cell(lap.LapTime.HasValue ? FormatTime(lap.LapTime.Value) : "—", numeric: true, highlight: lapIsFastest), lapCol, row);
                row++;
            }
        }

        VerticalStackLayout head = new() { Spacing = 6 };
        head.Add(new Label
        {
            Text = $"Track map: {map.Name}  ({(map.SameStartFinish ? "circuit" : "point-to-point")})",
            FontAttributes = FontAttributes.Bold,
            FontSize = 16
        });
        head.Add(new Label
        {
            Text = "Fastest lap and fastest sector times (across all runs) are highlighted. Columns are the segments between timing lines (S1 = Start→S1, … , Finish = last sector→Finish).",
            FontSize = 12
        });

        Button close = new() { Text = "Close", HorizontalOptions = LayoutOptions.End };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        Grid layout = new()
        {
            Padding = 12,
            RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            }
        };
        layout.Add(head, 0, 0);
        // scroll both ways: many sectors overflow horizontally, many laps vertically
        layout.Add(new ScrollView { Orientation = ScrollOrientation.Both, Content = grid }, 0, 1);
        layout.Add(close, 0, 2);
        Content = layout;
    }

    /// <summary>Builds one table cell. Numeric cells are right-aligned; a highlighted cell gets the
    /// session-best fill and bold text; a muted cell is gray (e.g. the no-crossing note).</summary>
    private static Label Cell(string text, bool header = false, bool numeric = false, bool highlight = false, bool muted = false)
    {
        Label label = new()
        {
            Text = text,
            FontSize = 13,
            Padding = new Thickness(6, 3),
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = numeric ? TextAlignment.End : TextAlignment.Start,
        };
        if (header)
        {
            label.FontAttributes = FontAttributes.Bold;
        }
        if (muted)
        {
            label.TextColor = Colors.Gray;
        }
        if (highlight)
        {
            label.FontAttributes = FontAttributes.Bold;
            label.BackgroundColor = HighlightBackground;
        }
        return label;
    }

    /// <summary>
    /// Yields a lap's timing segments in order: Start→S1, S1→S2, … , plus the closing Sn→Finish
    /// span when the lap finished. Each segment carries its own time (this line's split minus the
    /// previous line's) and a key for cross-lap "fastest segment" comparison (the sector Order, or
    /// FinishSegmentKey for the closing span). A lap with no sector lines yields only the closing
    /// span (the whole lap).
    /// </summary>
    private static IEnumerable<Segment> Segments(LapTiming lap)
    {
        float prevSplit = 0f;
        foreach (SectorSplit sector in lap.Sectors)
        {
            yield return new Segment($"S{sector.Order}", sector.SplitFromLapStart - prevSplit, sector.Order);
            prevSplit = sector.SplitFromLapStart;
        }
        if (lap.LapTime.HasValue)
        {
            yield return new Segment("Finish", lap.LapTime.Value - prevSplit, FinishSegmentKey);
        }
    }

    private static bool NearlyEqual(float a, float b) => Math.Abs(a - b) < TimeEpsilon;

    private static string FormatTime(float seconds)
    {
        if (seconds >= 60f)
        {
            int minutes = (int)(seconds / 60f);
            float rem = seconds - minutes * 60f;
            return $"{minutes}:{rem:00.000}";
        }
        return $"{seconds:0.000}s";
    }
}
