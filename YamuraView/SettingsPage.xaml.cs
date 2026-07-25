using YamuraView.Core;

namespace YamuraView;

public partial class SettingsPage : ContentPage
{
    private const int DefaultFilterWindow = 9;

    private readonly Action<string, string, Color[], ChartDisplayMode, ChartDisplayMode, ChartDisplayMode, int, float, GridSpacingUnit, Dictionary<string, ChannelFilterSettings>> onSave;
    private readonly Action<IReadOnlyList<string>> onRemoveRuns;
    private readonly List<Button> swatchButtons = new();
    private readonly List<(CheckBox Box, string RunName)> runRemovalChecks = new();
    private readonly int initialTractionCircleTrailPoints;
    private readonly float initialTrackMapGridSpacing;
    private readonly string initialAutoloadFolderPath;

    // one row per loaded channel name; channels with a filter but no loaded data keep
    // their entry in channelFilters untouched (no row is built for them)
    private readonly List<(string ChannelName, Picker TypePicker, Entry WindowEntry)> filterRows = new();
    private readonly Dictionary<string, ChannelFilterSettings> channelFilters;

    public SettingsPage(
        string configFilePath,
        string autoloadFolderPath,
        IReadOnlyList<Color> colors,
        ChartDisplayMode stripChartDisplayMode,
        ChartDisplayMode trackMapDisplayMode,
        ChartDisplayMode tractionCircleDisplayMode,
        int tractionCircleTrailPoints,
        float trackMapGridSpacing,
        GridSpacingUnit trackMapGridUnit,
        IReadOnlyList<string> runNames,
        IReadOnlyList<string> channelNames,
        IReadOnlyDictionary<string, ChannelFilterSettings> channelFilters,
        Action<string, string, Color[], ChartDisplayMode, ChartDisplayMode, ChartDisplayMode, int, float, GridSpacingUnit, Dictionary<string, ChannelFilterSettings>> onSave,
        Action<IReadOnlyList<string>> onRemoveRuns)
    {
        InitializeComponent();
        this.onSave = onSave;
        this.onRemoveRuns = onRemoveRuns;
        this.channelFilters = new Dictionary<string, ChannelFilterSettings>(channelFilters);
        initialTractionCircleTrailPoints = tractionCircleTrailPoints;
        initialTrackMapGridSpacing = trackMapGridSpacing;
        initialAutoloadFolderPath = autoloadFolderPath;
        ConfigPathEntry.Text = configFilePath;
        AutoloadFolderEntry.Text = autoloadFolderPath;
        TractionCircleTrailPointsEntry.Text = tractionCircleTrailPoints.ToString();
        TrackMapGridSpacingEntry.Text = trackMapGridSpacing.ToString();
        TrackMapGridUnitPicker.SelectedIndex = trackMapGridUnit == GridSpacingUnit.Meters ? 1 : 0;
        // both pickers list Line=0, Point=1, Line + Points=2
        StripChartDisplayModePicker.SelectedIndex = LinePointIndex(stripChartDisplayMode);
        TrackMapDisplayModePicker.SelectedIndex = LinePointIndex(trackMapDisplayMode);
        TractionCircleDisplayModePicker.SelectedIndex = tractionCircleDisplayMode switch
        {
            ChartDisplayMode.Point => 1,
            ChartDisplayMode.LinePoint => 2,
            ChartDisplayMode.CursorOnly => 3,
            ChartDisplayMode.CursorTrail => 4,
            _ => 0,
        };

        foreach (Color color in colors)
        {
            Button swatch = new()
            {
                BackgroundColor = color,
                BorderColor = Colors.Gray,
                BorderWidth = 1,
                WidthRequest = 40,
                HeightRequest = 40,
                Margin = 4,
            };
            swatch.Clicked += async (_, _) =>
            {
                ColorPickerPage page = new(picked => swatch.BackgroundColor = picked);
                await Navigation.PushModalAsync(page);
            };
            swatchButtons.Add(swatch);
            ColorSwatchLayout.Children.Add(swatch);
        }

        if (channelNames.Count == 0)
        {
            ChannelFilterLayout.Children.Add(new Label { Text = "(no channels loaded)", FontSize = 12 });
        }
        foreach (string channelName in channelNames)
        {
            channelFilters.TryGetValue(channelName, out ChannelFilterSettings? current);
            Picker typePicker = new()
            {
                ItemsSource = new List<string> { "None", "Moving Average", "Median", "Savitzky-Golay" },
                SelectedIndex = current?.Type switch
                {
                    ChannelFilterType.MovingAverage => 1,
                    ChannelFilterType.Median => 2,
                    ChannelFilterType.SavitzkyGolay => 3,
                    _ => 0,
                },
                WidthRequest = 140,
            };
            Entry windowEntry = new()
            {
                Text = (current?.WindowSize ?? DefaultFilterWindow).ToString(),
                Keyboard = Keyboard.Numeric,
                WidthRequest = 60,
            };
            Grid row = new()
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = new GridLength(140) },
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                ColumnSpacing = 8,
            };
            Label nameLabel = new() { Text = channelName, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
            row.Add(nameLabel, 0);
            row.Add(typePicker, 1);
            row.Add(windowEntry, 2);
            filterRows.Add((channelName, typePicker, windowEntry));
            ChannelFilterLayout.Children.Add(row);
        }

        if (runNames.Count == 0)
        {
            RunRemovalLayout.Children.Add(new Label { Text = "(no runs loaded)", FontSize = 12 });
        }
        foreach (string runName in runNames)
        {
            CheckBox box = new() { VerticalOptions = LayoutOptions.Center };
            Label label = new() { Text = runName, VerticalOptions = LayoutOptions.Center };
            // tapping the name toggles too - the bare checkbox is a small touch target
            TapGestureRecognizer tap = new();
            tap.Tapped += (_, _) => box.IsChecked = !box.IsChecked;
            label.GestureRecognizers.Add(tap);
            HorizontalStackLayout row = new() { Spacing = 4 };
            row.Add(box);
            row.Add(label);
            runRemovalChecks.Add((box, runName));
            RunRemovalLayout.Children.Add(row);
        }
    }

    // shared Line=0 / Point=1 / Line + Points=2 mapping for the Strip Chart and Track Map
    // pickers (the Traction Circle picker adds its cursor modes and maps inline)
    private static int LinePointIndex(ChartDisplayMode mode) => mode switch
    {
        ChartDisplayMode.Point => 1,
        ChartDisplayMode.LinePoint => 2,
        _ => 0,
    };

    private static ChartDisplayMode LinePointMode(int index) => index switch
    {
        1 => ChartDisplayMode.Point,
        2 => ChartDisplayMode.LinePoint,
        _ => ChartDisplayMode.Line,
    };

    private async void OnBrowseConfigClicked(object? sender, EventArgs e)
    {
        try
        {
            FileResult? result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select config file",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".xml" } },
                    { DevicePlatform.Android, new[] { "*/*" } },
                    { DevicePlatform.iOS, new[] { "public.data" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.data" } },
                })
            });
            if (result != null)
            {
                ConfigPathEntry.Text = result.FullPath;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Browse Failed: {ex.Message}");
            await DisplayAlertAsync("Browse Failed", ex.Message, "OK");
        }
    }

    private async void OnBrowseAutoloadFolderClicked(object? sender, EventArgs e)
    {
        if (!AutoloadFolderPicker.IsSupported)
        {
            await DisplayAlertAsync("Browse", "Folder browsing isn't available on this platform yet - type the path directly.", "OK");
            return;
        }
        try
        {
            string? folder = await AutoloadFolderPicker.PickAsync();
            if (folder != null)
            {
                AutoloadFolderEntry.Text = folder;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Browse Failed: {ex.Message}");
            await DisplayAlertAsync("Browse Failed", ex.Message, "OK");
        }
    }

    private async void OnViewLogClicked(object? sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new LogViewerPage());
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        string path = ConfigPathEntry.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(path))
        {
            await DisplayAlertAsync("Settings", "Enter a config file path.", "OK");
            return;
        }
        string autoloadFolder = AutoloadFolderEntry.Text?.Trim() ?? "";
        // when the user changes the autoload folder to a non-empty path, make sure it exists
        // and can actually be read before saving it - an empty path is valid (autoload off).
        // On any error, clear the path and warn rather than silently disabling autoload later.
        if (!string.IsNullOrEmpty(autoloadFolder) && autoloadFolder != initialAutoloadFolderPath)
        {
            bool accessible;
            try
            {
                // Directory.Exists swallows access errors (returns false); enumerating forces
                // permission/path problems to surface as an exception we can report
                accessible = Directory.Exists(autoloadFolder);
                if (accessible)
                {
                    Directory.EnumerateFileSystemEntries(autoloadFolder).FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Autoload folder not accessible: {autoloadFolder}: {ex.Message}");
                accessible = false;
            }
            if (!accessible)
            {
                autoloadFolder = "";
                AutoloadFolderEntry.Text = "";
                await DisplayAlertAsync(
                    "Autoload Folder",
                    "That folder doesn't exist or can't be accessed. The autoload folder has been cleared.",
                    "OK");
            }
        }
        Color[] colors = swatchButtons.Select(b => b.BackgroundColor).ToArray();
        ChartDisplayMode stripChartDisplayMode = LinePointMode(StripChartDisplayModePicker.SelectedIndex);
        ChartDisplayMode trackMapDisplayMode = LinePointMode(TrackMapDisplayModePicker.SelectedIndex);
        ChartDisplayMode tractionCircleDisplayMode = TractionCircleDisplayModePicker.SelectedIndex switch
        {
            1 => ChartDisplayMode.Point,
            2 => ChartDisplayMode.LinePoint,
            3 => ChartDisplayMode.CursorOnly,
            4 => ChartDisplayMode.CursorTrail,
            _ => ChartDisplayMode.Line,
        };
        // unparseable or non-positive input keeps the previous value rather than erroring
        int tractionCircleTrailPoints = int.TryParse(TractionCircleTrailPointsEntry.Text?.Trim(), out int trailPoints) && trailPoints > 0
            ? trailPoints
            : initialTractionCircleTrailPoints;
        // 0 is valid (grid off); unparseable or negative input keeps the previous value
        float trackMapGridSpacing = float.TryParse(TrackMapGridSpacingEntry.Text?.Trim(), out float gridSpacing) && gridSpacing >= 0
            ? gridSpacing
            : initialTrackMapGridSpacing;
        GridSpacingUnit trackMapGridUnit = TrackMapGridUnitPicker.SelectedIndex == 1 ? GridSpacingUnit.Meters : GridSpacingUnit.Feet;

        // merge the filter rows into the map - channels without a row (a saved filter whose
        // channel isn't currently loaded) keep their existing entry
        foreach ((string channelName, Picker typePicker, Entry windowEntry) in filterRows)
        {
            ChannelFilterType type = typePicker.SelectedIndex switch
            {
                1 => ChannelFilterType.MovingAverage,
                2 => ChannelFilterType.Median,
                3 => ChannelFilterType.SavitzkyGolay,
                _ => ChannelFilterType.None,
            };
            if (type == ChannelFilterType.None)
            {
                channelFilters.Remove(channelName);
                continue;
            }
            // window below 3 does nothing, so treat bad input as the default
            int window = int.TryParse(windowEntry.Text?.Trim(), out int parsed) && parsed >= 3 ? parsed : DefaultFilterWindow;
            channelFilters[channelName] = new ChannelFilterSettings(type, window);
        }

        List<string> runsToRemove = runRemovalChecks.Where(c => c.Box.IsChecked).Select(c => c.RunName).ToList();
        if (runsToRemove.Count > 0)
        {
            bool confirmed = await DisplayAlertAsync(
                "Remove Runs",
                $"Remove {runsToRemove.Count} run(s)?\n{string.Join("\n", runsToRemove)}\n\nReload the log file to get a run back.",
                "Remove", "Cancel");
            if (!confirmed)
            {
                return;
            }
            // remove before onSave so the config it writes reflects the surviving runs
            onRemoveRuns(runsToRemove);
        }

        onSave(path, autoloadFolder, colors, stripChartDisplayMode, trackMapDisplayMode, tractionCircleDisplayMode, tractionCircleTrailPoints, trackMapGridSpacing, trackMapGridUnit, channelFilters);
        await Navigation.PopModalAsync();
    }
}
