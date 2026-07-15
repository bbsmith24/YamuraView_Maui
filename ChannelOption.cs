using System.ComponentModel;
using System.Linq;

namespace YamuraView;

/// <summary>
/// One selectable row (e.g. a specific run's instance of a channel, or just a run name)
/// in a grouped channel-selection list. <see cref="Key"/> is what callers use to identify
/// the selection (e.g. an encoded "channel|run" pair); <see cref="Name"/> is what's shown.
/// </summary>
public class ChannelOption : INotifyPropertyChanged
{
    private bool isSelected;
    private Color? color;
    private bool inverted;
    private float penWidth = StripChartDrawable.DefaultPenWidth;

    public string Key { get; }
    public string Name { get; }

    /// <summary>Row text with an " (inv)" suffix while inverted, mirroring the group
    /// header's convention.</summary>
    public string DisplayName => inverted ? Name + " (inv)" : Name;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected != value)
            {
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    /// <summary>
    /// Trace color for this specific channel/data-set (run) pair, overriding the normal
    /// per-run auto-assigned color. Null means no override (use the auto color). Scoped to
    /// just this one row - not shared with other runs' instances of the same channel name.
    /// </summary>
    public Color? Color
    {
        get => color;
        set
        {
            if (color != value)
            {
                color = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayColor)));
            }
        }
    }

    /// <summary>The run's auto-assigned palette color - what the trace draws with when no
    /// override is set. Supplied by the picker builder so the row swatch can show the
    /// current effective color instead of sitting blank until an override exists.</summary>
    public Color? AutoColor { get; set; }

    /// <summary>Effective trace color for the row's swatch: the override when set, the
    /// auto-assigned color otherwise.</summary>
    public Color? DisplayColor => color ?? AutoColor;

    /// <summary>
    /// Flips this row's trace vertically - used by the XY charts, where a row is a whole
    /// run, so invert is naturally per row (the Strip Chart inverts per channel name via
    /// <see cref="SeriesGroup.Inverted"/> instead).
    /// </summary>
    public bool Inverted
    {
        get => inverted;
        set
        {
            if (inverted != value)
            {
                inverted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Inverted)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            }
        }
    }

    /// <summary>
    /// Trace pen width (point radius in point mode) for this row - used by the XY charts,
    /// per run for the same reason as <see cref="Inverted"/>.
    /// </summary>
    public float PenWidth
    {
        get => penWidth;
        set
        {
            if (penWidth != value)
            {
                penWidth = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PenWidth)));
            }
        }
    }

    public ChannelOption(string key, string name, bool isSelected)
    {
        Key = key;
        Name = name;
        this.isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// A header (e.g. a channel name) plus its selectable rows (e.g. one per run) - mirrors the
/// WinForms tri-state tree's parent/child shape (parent = channel name, children =
/// "channel (run)"). The hosting page flattens groups and rows into a single list (grouped
/// CollectionViews misbehave on Windows), so <see cref="Expanded"/> is just a flag - the
/// page inserts or removes this group's rows when it toggles, and the rows themselves
/// (selection, color) are never touched by a collapse.
/// </summary>
public class SeriesGroup : List<ChannelOption>, INotifyPropertyChanged
{
    private int graphIndex;
    private float penWidth = StripChartDrawable.DefaultPenWidth;
    private bool inverted;
    private bool groupChecked;
    private bool suppressGroupCheckedCascade;
    private bool expanded = true;

    public string Header { get; }

    /// <summary>Every row in the group, whether or not its group is currently expanded in
    /// the dialog - kept so result-reading callers make it explicit they include rows a
    /// collapse has hidden.</summary>
    public IReadOnlyList<ChannelOption> AllItems => this;

    /// <summary>Whether the group's rows are shown (mirrors the WinForms tree's
    /// expand/collapse). Only a flag - the hosting page shows/hides the rows.</summary>
    public bool Expanded
    {
        get => expanded;
        set
        {
            if (expanded != value)
            {
                expanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandGlyph)));
            }
        }
    }

    /// <summary>Expander indicator shown in the group header: ▼ expanded, ▶ collapsed.</summary>
    public string ExpandGlyph => expanded ? "▼" : "▶";

    /// <summary>Header text with an " (inv)" suffix while inverted, mirroring the WinForms
    /// tree's display convention for an inverted channel.</summary>
    public string DisplayHeader => inverted ? Header + " (inv)" : Header;

    /// <summary>
    /// Which Strip Chart subgraph band this channel is stacked into (0-based) - matches the
    /// WinForms app's "Assign to Graph" tree context menu, letting channels with unrelated
    /// units (e.g. RPM vs. G-force) share one Strip Chart without sharing a Y scale. Unused
    /// (always 0) for chart pickers that don't offer subgraphs, like the XY charts.
    /// </summary>
    public int GraphIndex
    {
        get => graphIndex;
        set
        {
            if (graphIndex != value)
            {
                graphIndex = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GraphIndex)));
            }
        }
    }

    /// <summary>
    /// Trace pen width (in pixels) for this channel name - applies to every run's instance
    /// of the channel at once, like <see cref="Inverted"/>. In point display mode it sets
    /// the point radius instead.
    /// </summary>
    public float PenWidth
    {
        get => penWidth;
        set
        {
            if (penWidth != value)
            {
                penWidth = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PenWidth)));
            }
        }
    }

    /// <summary>
    /// Flips this channel name's trace vertically on the Y axis (mirrors the WinForms tree's
    /// "Invert" context menu item) - applies to every run's instance of the channel at once,
    /// unlike <see cref="ChannelOption.Color"/> which is scoped to one run.
    /// </summary>
    public bool Inverted
    {
        get => inverted;
        set
        {
            if (inverted != value)
            {
                inverted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Inverted)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayHeader)));
            }
        }
    }

    /// <summary>
    /// On/off toggle for every run's instance of this channel name at once - checking or
    /// unchecking it sets <see cref="ChannelOption.IsSelected"/> on every row in the group,
    /// mirroring the WinForms tri-state tree's parent-node checkbox.
    /// </summary>
    public bool GroupChecked
    {
        get => groupChecked;
        set
        {
            if (groupChecked == value)
            {
                return;
            }
            groupChecked = value;
            if (!suppressGroupCheckedCascade)
            {
                foreach (ChannelOption item in this)
                {
                    item.IsSelected = value;
                }
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupChecked)));
        }
    }

    public SeriesGroup(string header, IEnumerable<ChannelOption> items) : base(items)
    {
        Header = header;
        suppressGroupCheckedCascade = true;
        groupChecked = Count > 0 && this.All(i => i.IsSelected);
        suppressGroupCheckedCascade = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
