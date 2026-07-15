namespace YamuraView;

public partial class SettingsPage : ContentPage
{
    private readonly Action<string, string, Color[], ChartDisplayMode, ChartDisplayMode, ChartDisplayMode> onSave;
    private readonly Action<IReadOnlyList<string>> onRemoveRuns;
    private readonly List<Button> swatchButtons = new();
    private readonly List<(CheckBox Box, string RunName)> runRemovalChecks = new();

    public SettingsPage(
        string configFilePath,
        string autoloadFolderPath,
        IReadOnlyList<Color> colors,
        ChartDisplayMode stripChartDisplayMode,
        ChartDisplayMode trackMapDisplayMode,
        ChartDisplayMode tractionCircleDisplayMode,
        IReadOnlyList<string> runNames,
        Action<string, string, Color[], ChartDisplayMode, ChartDisplayMode, ChartDisplayMode> onSave,
        Action<IReadOnlyList<string>> onRemoveRuns)
    {
        InitializeComponent();
        this.onSave = onSave;
        this.onRemoveRuns = onRemoveRuns;
        ConfigPathEntry.Text = configFilePath;
        AutoloadFolderEntry.Text = autoloadFolderPath;
        StripChartDisplayModePicker.SelectedIndex = stripChartDisplayMode == ChartDisplayMode.Point ? 1 : 0;
        TrackMapDisplayModePicker.SelectedIndex = trackMapDisplayMode == ChartDisplayMode.Point ? 1 : 0;
        TractionCircleDisplayModePicker.SelectedIndex = tractionCircleDisplayMode == ChartDisplayMode.Point ? 1 : 0;

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
        Color[] colors = swatchButtons.Select(b => b.BackgroundColor).ToArray();
        ChartDisplayMode stripChartDisplayMode = StripChartDisplayModePicker.SelectedIndex == 1 ? ChartDisplayMode.Point : ChartDisplayMode.Line;
        ChartDisplayMode trackMapDisplayMode = TrackMapDisplayModePicker.SelectedIndex == 1 ? ChartDisplayMode.Point : ChartDisplayMode.Line;
        ChartDisplayMode tractionCircleDisplayMode = TractionCircleDisplayModePicker.SelectedIndex == 1 ? ChartDisplayMode.Point : ChartDisplayMode.Line;

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

        onSave(path, autoloadFolder, colors, stripChartDisplayMode, trackMapDisplayMode, tractionCircleDisplayMode);
        await Navigation.PopModalAsync();
    }
}
