namespace YamuraView;

public partial class ColorPickerPage : ContentPage
{
    /// <param name="onAutoPick">When non-null, adds an "Auto" swatch that clears a per-channel
    /// color override back to the default per-run auto-assigned color.</param>
    public ColorPickerPage(Action<Color> onPick, Action? onAutoPick = null)
    {
        InitializeComponent();

        if (onAutoPick != null)
        {
            Button autoSwatch = new()
            {
                Text = "Auto",
                FontSize = 11,
                WidthRequest = 44,
                HeightRequest = 44,
                Margin = 4,
            };
            autoSwatch.Clicked += async (_, _) =>
            {
                onAutoPick();
                await Navigation.PopModalAsync();
            };
            SwatchLayout.Children.Add(autoSwatch);
        }

        foreach (Color color in ChartColors.PresetChoices)
        {
            Button swatch = new()
            {
                BackgroundColor = color,
                WidthRequest = 44,
                HeightRequest = 44,
                Margin = 4,
            };
            swatch.Clicked += async (_, _) =>
            {
                onPick(color);
                await Navigation.PopModalAsync();
            };
            SwatchLayout.Children.Add(swatch);
        }
    }
}
