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

    public string Key { get; }
    public string Name { get; }

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
/// A header (e.g. a channel name) plus its selectable rows (e.g. one per run) for a
/// grouped CollectionView - mirrors the WinForms tri-state tree's parent/child shape
/// (parent = channel name, children = "channel (run)").
/// </summary>
public class SeriesGroup : List<ChannelOption>, INotifyPropertyChanged
{
    private int graphIndex;
    private bool inverted;
    private bool groupChecked;
    private bool suppressGroupCheckedCascade;

    public string Header { get; }

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
