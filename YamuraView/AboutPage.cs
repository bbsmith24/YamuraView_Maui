namespace YamuraView;

/// <summary>
/// About dialog: app name and version, plus a read-only box listing the updates since the last
/// release - the check-in descriptions (commit subjects) since the most recent "Bump build
/// number" commit, embedded at build time as the ReleaseNotes.txt resource (see the
/// GenerateReleaseNotes target in the csproj).
/// </summary>
public sealed class AboutPage : ContentPage
{
    public AboutPage()
    {
        Title = "About YamuraView";

        Label name = new() { Text = "YamuraView", FontAttributes = FontAttributes.Bold, FontSize = 18 };
        Label version = new() { Text = $"Version {AppVersion.Number} ({AppVersion.Status})" };
        Label updatesHeader = new() { Text = "Updates since last release", FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) };
        Editor updates = new()
        {
            Text = LoadReleaseNotes(),
            IsReadOnly = true,
            AutoSize = EditorAutoSizeOption.Disabled,
            FontSize = 13,
        };

        Button ok = new() { Text = "OK", HorizontalOptions = LayoutOptions.End, WidthRequest = 100 };
        ok.Clicked += async (_, _) => await Navigation.PopModalAsync();

        Grid layout = new()
        {
            Padding = 16,
            RowSpacing = 6,
            MaximumWidthRequest = 700,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            }
        };
        layout.Add(name, 0, 0);
        layout.Add(version, 0, 1);
        layout.Add(updatesHeader, 0, 2);
        layout.Add(updates, 0, 3);
        layout.Add(ok, 0, 4);
        Content = layout;
    }

    /// <summary>The embedded update list as "• subject" lines, or a placeholder when the build
    /// had no git history to read (or nothing has changed since the release).</summary>
    private static string LoadReleaseNotes()
    {
        try
        {
            using Stream? stream = typeof(AboutPage).Assembly.GetManifestResourceStream("ReleaseNotes.txt");
            if (stream != null)
            {
                using StreamReader reader = new(stream);
                List<string> lines = reader.ReadToEnd()
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();
                if (lines.Count > 0)
                {
                    return string.Join(Environment.NewLine, lines.Select(l => "• " + l));
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Couldn't read release notes: {ex.Message}");
        }
        return "(no updates listed)";
    }
}
