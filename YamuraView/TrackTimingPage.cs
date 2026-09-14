using System.Text;
using YamuraView.Core;

namespace YamuraView;

/// <summary>
/// Read-only lap/sector timing of every loaded run against the active track map (see
/// <see cref="TrackMapAlignment.GetRunTimings"/>). Point-to-point maps show one Start→Finish lap
/// per run; circuit maps show a lap per start-line crossing. Sector rows show elapsed time from
/// the lap's start-line crossing and the delta from the previous sector.
/// </summary>
public sealed class TrackTimingPage : ContentPage
{
    public TrackTimingPage(DataLogger dataLogger, TrackMap map)
    {
        Title = "Lap/Sector Times";

        VerticalStackLayout list = new() { Spacing = 10 };
        list.Add(new Label
        {
            Text = $"Track map: {map.Name}  ({(map.SameStartFinish ? "circuit" : "point-to-point")})",
            FontAttributes = FontAttributes.Bold,
            FontSize = 16
        });

        foreach ((RunData run, RunTiming timing) in TrackMapAlignment.GetRunTimings(dataLogger, map))
        {
            list.Add(new Label { Text = run.runName, FontAttributes = FontAttributes.Bold, FontSize = 14, Margin = new Thickness(0, 6, 0, 0) });
            if (timing.Laps.Count == 0)
            {
                list.Add(new Label { Text = "  (no start-line crossing)", TextColor = Colors.Gray, FontSize = 13 });
                continue;
            }
            foreach (LapTiming lap in timing.Laps)
            {
                list.Add(new Label { Text = FormatLap(lap, map.SameStartFinish), FontSize = 13 });
            }
        }

        Button close = new() { Text = "Close", HorizontalOptions = LayoutOptions.End };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        Grid layout = new()
        {
            Padding = 12,
            RowSpacing = 8,
            RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) }
        };
        layout.Add(new ScrollView { Content = list }, 0, 0);
        layout.Add(close, 0, 1);
        Content = layout;
    }

    private static string FormatLap(LapTiming lap, bool circuit)
    {
        StringBuilder sb = new();
        string lapLabel = circuit ? $"Lap {lap.LapNumber}" : "Run";
        sb.Append("  ").Append(lapLabel).Append(": ");
        sb.Append(lap.LapTime.HasValue ? FormatTime(lap.LapTime.Value) : "(not finished)");

        float prevSplit = 0f;
        foreach (SectorSplit sector in lap.Sectors)
        {
            sb.AppendLine();
            float sectorTime = sector.SplitFromLapStart - prevSplit;
            sb.Append("      S").Append(sector.Order)
              .Append(":  +").Append(FormatTime(sector.SplitFromLapStart))
              .Append("   (sector ").Append(FormatTime(sectorTime)).Append(')');
            prevSplit = sector.SplitFromLapStart;
        }
        return sb.ToString();
    }

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
