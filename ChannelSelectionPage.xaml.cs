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

    /// <summary>Shows a per-group "Graph" stepper (which Strip Chart subgraph band the channel
    /// stacks into) and on/off checkbox (toggles every run's instance of that channel at once)
    /// in the group header, plus a per-row trace color swatch (scoped to just that one
    /// channel/run pair) on each item. Not offered for pickers whose groups aren't channels
    /// (e.g. the XY charts' single "Runs" group).</summary>
    public bool ShowChannelGroupControls { get; }

    /// <param name="xAxisOptions">Channel names offered for the X axis. Null hides the X-axis picker entirely.</param>
    /// <param name="yAxisOptions">Channel names offered for the Y axis. Null hides the Y-axis picker (used for the Strip Chart, which has no single Y channel).</param>
    /// <param name="showChannelGroupControls">Shows the per-group Graph/on-off controls and per-row color swatch (Strip Chart only).</param>
    public ChannelSelectionPage(
        string title,
        IReadOnlyList<SeriesGroup> groups,
        Action<SeriesSelectionResult> onApply,
        IReadOnlyList<string>? xAxisOptions = null,
        string? selectedXAxis = null,
        IReadOnlyList<string>? yAxisOptions = null,
        string? selectedYAxis = null,
        bool showChannelGroupControls = false)
    {
        InitializeComponent();
        Title = title;
        this.onApply = onApply;
        Groups = new ObservableCollection<SeriesGroup>(groups);
        ShowChannelGroupControls = showChannelGroupControls;
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
            foreach (ChannelOption item in group)
            {
                item.IsSelected = true;
            }
        }
    }

    private void OnSelectNoneClicked(object? sender, EventArgs e)
    {
        foreach (SeriesGroup group in Groups)
        {
            foreach (ChannelOption item in group)
            {
                item.IsSelected = false;
            }
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
        List<string> selectedKeys = Groups.SelectMany(g => g).Where(i => i.IsSelected).Select(i => i.Key).ToList();
        string? xAxis = XAxisRow.IsVisible ? (string?)XAxisPicker.SelectedItem : null;
        string? yAxis = YAxisRow.IsVisible ? (string?)YAxisPicker.SelectedItem : null;
        onApply(new SeriesSelectionResult(selectedKeys, xAxis, yAxis));
        await Navigation.PopModalAsync();
    }
}
