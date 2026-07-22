using System.Collections.ObjectModel;

namespace YamuraView;

/// <summary>
/// Result of a channel-selection page: the checked (run, channel) keys, plus the chosen
/// X (and, for XY charts, Y) axis channel - null for an axis that wasn't offered.
/// </summary>
public record SeriesSelectionResult(IReadOnlyList<string> SelectedKeys, string? XAxis, string? YAxis);

public partial class ChannelSelectionPage : ContentPage
{
    private readonly Action<SeriesSelectionResult> onApply;

    public ObservableCollection<SeriesGroup> Groups { get; }

    /// <summary>
    /// What the CollectionView actually shows: group headers (SeriesGroup) and channel rows
    /// (ChannelOption) interleaved in one flat list, dispatched by type to their templates.
    /// Collapsing a group removes its rows from here (never from the group itself), so row
    /// state survives. Flat because grouped CollectionViews misbehave on Windows - rows added
    /// to an initially-empty group don't show, and many groups break scrolling.
    /// </summary>
    public ObservableCollection<object> Rows { get; } = new();

    /// <summary>Shows a per-group "Graph" stepper (which Strip Chart subgraph band the channel
    /// stacks into), "Width" stepper (trace pen width for every run's instance of the channel),
    /// and on/off checkbox (toggles every run's instance of that channel at once)
    /// in the group header, plus a per-row trace color swatch (scoped to just that one
    /// channel/run pair) on each item. Offered for the Strip Chart picker, whose groups are
    /// channels; the XY pickers use <see cref="ShowRunControls"/> instead.</summary>
    public bool ShowChannelGroupControls { get; }

    /// <summary>Shows per-row Invert checkbox, Width stepper, and color swatch - for the XY
    /// chart pickers, where a row is a whole run so these settings are naturally per row
    /// (the Strip Chart puts invert/width on the group header instead).</summary>
    public bool ShowRunControls { get; }

    /// <summary>Whether rows show the trace color swatch - per-run color is offered by both
    /// picker flavors, so this is just "either mode is on".</summary>
    public bool ShowRowColor { get; }

    /// <param name="xAxisOptions">Channel names offered for the X axis. Null hides the X-axis picker entirely.</param>
    /// <param name="yAxisOptions">Channel names offered for the Y axis. Null hides the Y-axis picker (used for the Strip Chart, which has no single Y channel).</param>
    /// <param name="showChannelGroupControls">Shows the per-group Graph/on-off controls and per-row color swatch (Strip Chart only).</param>
    /// <param name="showRunControls">Shows per-row Invert/Width/color controls (XY chart pickers only).</param>
    public ChannelSelectionPage(
        string title,
        IReadOnlyList<SeriesGroup> groups,
        Action<SeriesSelectionResult> onApply,
        IReadOnlyList<string>? xAxisOptions = null,
        string? selectedXAxis = null,
        IReadOnlyList<string>? yAxisOptions = null,
        string? selectedYAxis = null,
        bool showChannelGroupControls = false,
        bool showRunControls = false)
    {
        InitializeComponent();
        Title = title;
        this.onApply = onApply;
        Groups = new ObservableCollection<SeriesGroup>(groups);
        ShowChannelGroupControls = showChannelGroupControls;
        ShowRunControls = showRunControls;
        ShowRowColor = showChannelGroupControls || showRunControls;
        foreach (SeriesGroup group in Groups)
        {
            Rows.Add(group);
            if (group.Expanded)
            {
                foreach (ChannelOption item in group.AllItems)
                {
                    Rows.Add(item);
                }
            }
        }
        BindingContext = this;

        if (xAxisOptions != null && xAxisOptions.Count > 0)
        {
            foreach (string option in xAxisOptions)
            {
                XAxisPicker.Items.Add(option);
            }
            XAxisPicker.SelectedItem = selectedXAxis;
            if (XAxisPicker.SelectedIndex < 0)
            {
                XAxisPicker.SelectedIndex = 0;
            }
        }
        else
        {
            XAxisRow.IsVisible = false;
        }

        if (yAxisOptions != null && yAxisOptions.Count > 0)
        {
            YAxisRow.IsVisible = true;
            foreach (string option in yAxisOptions)
            {
                YAxisPicker.Items.Add(option);
            }
            YAxisPicker.SelectedItem = selectedYAxis;
            if (YAxisPicker.SelectedIndex < 0)
            {
                YAxisPicker.SelectedIndex = 0;
            }
        }
    }

    private void OnSelectAllClicked(object? sender, EventArgs e)
    {
        foreach (SeriesGroup group in Groups)
        {
            foreach (ChannelOption item in group.AllItems)
            {
                item.IsSelected = true;
            }
        }
    }

    private void OnSelectNoneClicked(object? sender, EventArgs e)
    {
        foreach (SeriesGroup group in Groups)
        {
            foreach (ChannelOption item in group.AllItems)
            {
                item.IsSelected = false;
            }
        }
    }

    private void OnToggleGroupExpandClicked(object? sender, EventArgs e)
    {
        if (sender is not Element element || element.BindingContext is not SeriesGroup group)
        {
            return;
        }
        int headerIndex = Rows.IndexOf(group);
        if (headerIndex < 0)
        {
            return;
        }
        if (group.Expanded)
        {
            // a group's rows always sit directly after its header in the flat list
            for (int i = 0; i < group.AllItems.Count; i++)
            {
                Rows.RemoveAt(headerIndex + 1);
            }
            group.Expanded = false;
        }
        else
        {
            for (int i = 0; i < group.AllItems.Count; i++)
            {
                Rows.Insert(headerIndex + 1 + i, group.AllItems[i]);
            }
            group.Expanded = true;
        }
    }

    private async void OnChannelColorClicked(object? sender, EventArgs e)
    {
        if (sender is not Element element || element.BindingContext is not ChannelOption option)
        {
            return;
        }
        ColorPickerPage page = new(
            onPick: pickedColor => option.Color = pickedColor,
            onAutoPick: () => option.Color = null);
        await Navigation.PushModalAsync(page);
    }

    private async void OnDoneClicked(object? sender, EventArgs e)
    {
        // AllItems: every row counts toward the result, including rows hidden by a collapse
        List<string> selectedKeys = Groups.SelectMany(g => g.AllItems).Where(i => i.IsSelected).Select(i => i.Key).ToList();
        string? xAxis = XAxisRow.IsVisible ? (string?)XAxisPicker.SelectedItem : null;
        string? yAxis = YAxisRow.IsVisible ? (string?)YAxisPicker.SelectedItem : null;
        onApply(new SeriesSelectionResult(selectedKeys, xAxis, yAxis));
        await Navigation.PopModalAsync();
    }
}

/// <summary>Dispatches the flat Rows list by entry type: SeriesGroup entries get the group
/// header template, ChannelOption entries get the channel-row template.</summary>
public class ChannelRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GroupTemplate { get; set; }
    public DataTemplate? ItemTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
        (item is SeriesGroup ? GroupTemplate : ItemTemplate)!;
}
