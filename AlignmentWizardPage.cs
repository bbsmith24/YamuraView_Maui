using YamuraView.Core;

namespace YamuraView;

/// <summary>
/// Manual run-alignment wizard: steps through each loaded run so the user can mark one
/// point (the same physical event - a braking spike, corner entry, etc.) per run on the
/// current Strip Chart X axis, then shifts each run's Time or Distance offset so the marks
/// line up with the first run's mark. A fallback for when the automatic time/distance
/// alignment isn't right; position-based start/finish lines are planned to replace it.
/// Every run's mark starts at the same display value, so a run the user never adjusts
/// contributes a zero shift and keeps its current alignment.
/// </summary>
public class AlignmentWizardPage : ContentPage
{
    private readonly DataLogger dataLogger;
    private readonly string axisChannel;
    private readonly bool axisIsTime;
    private readonly IReadOnlyList<(RunData Run, HashSet<(string RunName, string ChannelName)> Series)> runs;
    private readonly Action onFinished;

    // chosen align point per run, in display space (offsets applied) - matching what the
    // user sees, so the shift to apply is simply (first run's value - this run's value)
    private readonly float[] chosenValues;

    // display-space axis sample values per run, for the one-sample nudge buttons
    private readonly List<float>[] axisSamples;

    private readonly StripChartDrawable drawable;
    private readonly GraphicsView chartView;
    private readonly Label headerLabel = new() { FontAttributes = FontAttributes.Bold, FontSize = 16 };
    private readonly Label valueLabel = new()
    {
        VerticalOptions = LayoutOptions.Center,
        WidthRequest = 140,
        HorizontalTextAlignment = TextAlignment.Center
    };
    private readonly Button backButton;
    private readonly Button nextButton;

    private int runIndex;
    private bool pointerPressed;
    private float panAnchorPixelX;

    public AlignmentWizardPage(
        DataLogger dataLogger,
        string axisChannel,
        IReadOnlyList<(RunData Run, HashSet<(string RunName, string ChannelName)> Series)> runs,
        Action onFinished)
    {
        Title = "Align Runs";
        this.dataLogger = dataLogger;
        this.axisChannel = axisChannel;
        axisIsTime = axisChannel == StripChartDrawable.TimeAxis;
        this.runs = runs;
        this.onFinished = onFinished;

        drawable = new StripChartDrawable { DataLogger = dataLogger, XAxisChannel = axisChannel };
        chartView = new GraphicsView { Drawable = drawable };

        axisSamples = new List<float>[runs.Count];
        for (int i = 0; i < runs.Count; i++)
        {
            axisSamples[i] = BuildAxisSamples(runs[i].Run, runs[i].Series);
        }

        // same starting value for every run: an unmoved mark then shifts its run by zero
        float initialValue = axisSamples[0].Count > 0
            ? (axisSamples[0][0] + axisSamples[0][^1]) / 2f
            : 0f;
        chosenValues = new float[runs.Count];
        Array.Fill(chosenValues, initialValue);

        backButton = new Button { Text = "Back" };
        backButton.Clicked += (_, _) => ShowRun(runIndex - 1);
        nextButton = new Button { Text = "Next" };
        nextButton.Clicked += async (_, _) =>
        {
            if (runIndex < runs.Count - 1)
            {
                ShowRun(runIndex + 1);
            }
            else
            {
                ApplyOffsets();
                this.onFinished();
                await Navigation.PopModalAsync();
            }
        };
        Button cancelButton = new() { Text = "Cancel" };
        cancelButton.Clicked += async (_, _) => await Navigation.PopModalAsync();
        Button nudgeLeft = new() { Text = "◀" };
        nudgeLeft.Clicked += (_, _) => Nudge(-1);
        Button nudgeRight = new() { Text = "▶" };
        nudgeRight.Clicked += (_, _) => Nudge(1);
        // zoom buttons instead of pinch: a pinch recognizer would put the view into
        // manipulation mode and stop pointer-move events, breaking touch drag-marking
        // (same platform behavior the main chart works around with a pan gesture)
        Button zoomOut = new() { Text = "−" };
        zoomOut.Clicked += (_, _) => ZoomBy(2f);
        Button zoomIn = new() { Text = "+" };
        zoomIn.Clicked += (_, _) => ZoomBy(0.5f);
        Button zoomFit = new() { Text = "Fit" };
        zoomFit.Clicked += (_, _) =>
        {
            drawable.ZoomMinX = null;
            drawable.ZoomMaxX = null;
            chartView.Invalidate();
        };

        // plain press/drag works for both mouse and touch here: this view has no pinch/pan
        // recognizers, so pointer-move events aren't swallowed by the gesture pipeline
        PointerGestureRecognizer pointer = new();
        pointer.PointerPressed += (_, e) =>
        {
            pointerPressed = true;
            SetChosenFromPosition(e.GetPosition(chartView));
        };
        pointer.PointerMoved += (_, e) =>
        {
            if (pointerPressed)
            {
                SetChosenFromPosition(e.GetPosition(chartView));
            }
        };
        pointer.PointerReleased += (_, e) =>
        {
            if (pointerPressed)
            {
                SetChosenFromPosition(e.GetPosition(chartView));
            }
            pointerPressed = false;
        };
        pointer.PointerExited += (_, _) => pointerPressed = false;
        chartView.GestureRecognizers.Add(pointer);

        // Android raises no pointer events for touch (pointer gestures are mouse/stylus-only
        // there), so touch marking needs tap (absolute placement) + pan (slides the mark
        // relative to where it is). On Windows these coexist with the pointer handlers: the
        // press puts the mark under the finger, and the pan continues seamlessly from there.
        TapGestureRecognizer tap = new();
        tap.Tapped += (_, e) => SetChosenFromPosition(e.GetPosition(chartView));
        chartView.GestureRecognizers.Add(tap);

        PanGestureRecognizer pan = new();
        pan.PanUpdated += OnPanUpdated;
        chartView.GestureRecognizers.Add(pan);

        Label instructions = new()
        {
            Text = "Tap or drag to mark the same event (braking point, corner entry, ...) in every run, "
                + "then Finish to line the marks up. Zoom in with + for precision (the view stays "
                + "centered on the mark) and use ◀ ▶ to fine-tune one sample at a time. "
                + "Runs you don't adjust keep their current alignment.",
            FontSize = 13
        };

        HorizontalStackLayout nudgeRow = new() { Spacing = 8, HorizontalOptions = LayoutOptions.Center };
        nudgeRow.Add(zoomOut);
        nudgeRow.Add(nudgeLeft);
        nudgeRow.Add(valueLabel);
        nudgeRow.Add(nudgeRight);
        nudgeRow.Add(zoomIn);
        nudgeRow.Add(zoomFit);

        HorizontalStackLayout buttonRow = new() { Spacing = 8, HorizontalOptions = LayoutOptions.End };
        buttonRow.Add(cancelButton);
        buttonRow.Add(backButton);
        buttonRow.Add(nextButton);

        Grid layout = new()
        {
            Padding = 12,
            RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            }
        };
        layout.Add(headerLabel, 0, 0);
        layout.Add(instructions, 0, 1);
        layout.Add(chartView, 0, 2);
        layout.Add(nudgeRow, 0, 3);
        layout.Add(buttonRow, 0, 4);
        Content = layout;

        ShowRun(0);
    }

    /// <summary>Display-space axis values (offsets applied) of the run's data samples. For
    /// Time, any displayed channel's timestamps serve as the nudge steps; for a distance
    /// axis, the distance channel's own values do.</summary>
    private List<float> BuildAxisSamples(RunData run, HashSet<(string RunName, string ChannelName)> series)
    {
        List<float> samples = new();
        if (axisIsTime)
        {
            string channelName = series.Select(s => s.ChannelName).FirstOrDefault(n => run.channels.ContainsKey(n)) ?? "";
            if (run.channels.TryGetValue(channelName, out DataChannel? channel))
            {
                foreach (float key in channel.DataPoints.Keys)
                {
                    samples.Add(key + run.TimeOffset);
                }
            }
        }
        else if (run.channels.TryGetValue(axisChannel, out DataChannel? axisData))
        {
            foreach (float value in axisData.DataPoints.Values)
            {
                samples.Add(value + run.DistanceOffset);
            }
            samples.Sort();
        }
        return samples;
    }

    private void ShowRun(int index)
    {
        runIndex = index;
        (RunData run, HashSet<(string RunName, string ChannelName)> series) = runs[index];
        drawable.SelectedSeries = series;
        drawable.CursorTime = chosenValues[index];
        // each run starts unzoomed - run lengths and ranges differ
        drawable.ZoomMinX = null;
        drawable.ZoomMaxX = null;
        headerLabel.Text = $"Run {index + 1} of {runs.Count}: {run.runName}";
        backButton.IsEnabled = index > 0;
        nextButton.Text = index == runs.Count - 1 ? "Finish" : "Next";
        UpdateValueLabel();
        chartView.Invalidate();
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                panAnchorPixelX = drawable.TimeToPixelX(chosenValues[runIndex]) ?? (float)(chartView.Width / 2);
                break;
            case GestureStatus.Running:
                float? value = drawable.PixelXToTime(panAnchorPixelX + (float)e.TotalX);
                if (value.HasValue)
                {
                    SetChosen(value.Value);
                }
                break;
        }
    }

    private void SetChosenFromPosition(Point? position)
    {
        if (!position.HasValue)
        {
            return;
        }
        float? value = drawable.PixelXToTime((float)position.Value.X);
        if (value.HasValue)
        {
            SetChosen(value.Value);
        }
    }

    private void SetChosen(float value)
    {
        chosenValues[runIndex] = value;
        drawable.CursorTime = value;
        // keep the mark visible when a nudge walks it past the zoom window's edge
        if (drawable.ZoomMinX.HasValue && drawable.ZoomMaxX.HasValue &&
            (value < drawable.ZoomMinX.Value || value > drawable.ZoomMaxX.Value))
        {
            CenterZoomOn(value);
        }
        UpdateValueLabel();
        chartView.Invalidate();
    }

    private void UpdateValueLabel() =>
        valueLabel.Text = axisIsTime ? $"{chosenValues[runIndex]:0.000} s" : $"{chosenValues[runIndex]:0.00}";

    /// <summary>Zooms the chart in (factor &lt; 1) or out around the current mark; zooming
    /// out to or past the full range resets to unzoomed.</summary>
    private void ZoomBy(float factor)
    {
        float? dataMin = drawable.DataMinX;
        float? dataMax = drawable.DataMaxX;
        if (!dataMin.HasValue || !dataMax.HasValue)
        {
            return; // nothing drawn yet
        }
        float fullWidth = dataMax.Value - dataMin.Value;
        float currentMin = drawable.ZoomMinX ?? dataMin.Value;
        float currentMax = drawable.ZoomMaxX ?? dataMax.Value;
        float width = (currentMax - currentMin) * factor;
        if (width >= fullWidth)
        {
            drawable.ZoomMinX = null;
            drawable.ZoomMaxX = null;
        }
        else
        {
            width = Math.Max(width, fullWidth / 1000f); // cap zoom-in depth
            float newMin = Math.Clamp(chosenValues[runIndex] - width / 2f, dataMin.Value, dataMax.Value - width);
            drawable.ZoomMinX = newMin;
            drawable.ZoomMaxX = newMin + width;
        }
        chartView.Invalidate();
    }

    /// <summary>Slides the current zoom window (keeping its width) so it's centered on the
    /// given value, clamped to the data range.</summary>
    private void CenterZoomOn(float center)
    {
        float? dataMin = drawable.DataMinX;
        float? dataMax = drawable.DataMaxX;
        if (!dataMin.HasValue || !dataMax.HasValue ||
            !drawable.ZoomMinX.HasValue || !drawable.ZoomMaxX.HasValue)
        {
            return;
        }
        float width = drawable.ZoomMaxX.Value - drawable.ZoomMinX.Value;
        float newMin = Math.Clamp(center - width / 2f, dataMin.Value, Math.Max(dataMin.Value, dataMax.Value - width));
        drawable.ZoomMinX = newMin;
        drawable.ZoomMaxX = newMin + width;
    }

    /// <summary>
    /// Steps the mark to the run's previous/next *distinct* sample value. Distinct matters:
    /// a distance channel repeats the same value while the car is stationary, so stepping
    /// by raw index there would move nothing and the button would appear dead.
    /// </summary>
    private void Nudge(int direction)
    {
        List<float> samples = axisSamples[runIndex];
        if (samples.Count == 0)
        {
            return;
        }
        float current = chosenValues[runIndex];
        if (direction > 0)
        {
            int idx = FirstIndexGreaterThan(samples, current);
            if (idx < samples.Count)
            {
                SetChosen(samples[idx]);
            }
        }
        else
        {
            int idx = FirstIndexAtLeast(samples, current) - 1; // last value strictly less
            if (idx >= 0)
            {
                SetChosen(samples[idx]);
            }
        }
    }

    /// <summary>First index whose value is &gt;= target (samples.Count if none).</summary>
    private static int FirstIndexAtLeast(List<float> samples, float target)
    {
        int lo = 0, hi = samples.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (samples[mid] < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>First index whose value is &gt; target (samples.Count if none).</summary>
    private static int FirstIndexGreaterThan(List<float> samples, float target)
    {
        int lo = 0, hi = samples.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (samples[mid] <= target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>Shifts each run's offset so its mark lands on the first run's mark. Marks
    /// are in display space, so the current offset is simply adjusted by the difference.</summary>
    private void ApplyOffsets()
    {
        float reference = chosenValues[0];
        for (int i = 0; i < runs.Count; i++)
        {
            float delta = reference - chosenValues[i];
            if (axisIsTime)
            {
                runs[i].Run.TimeOffset += delta;
            }
            else
            {
                runs[i].Run.DistanceOffset += delta;
            }
        }
        if (!axisIsTime)
        {
            // the reference mark is the new distance align point (all marks now sit on it
            // in aligned space) - Delta-T only computes from here on
            dataLogger.DistanceAlignPoint = reference;
        }
    }
}
