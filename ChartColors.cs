namespace YamuraView;

/// <summary>
/// Run auto-assign palette (defaults match the WinForms app's Chart.AutoColors), so runs
/// get consistent colors across panels. Mutable/settable so the Settings page can persist
/// and restore a user-edited palette, like the WinForms "Edit Settings" dialog's color swatches.
/// </summary>
public static class ChartColors
{
    public static Color[] Palette { get; set; } =
    {
        Colors.Pink,
        Colors.LimeGreen,
        Colors.Cyan,
        Colors.Yellow,
        Colors.LightSkyBlue,
        Colors.LightGray,
        Colors.LightPink,
    };

    /// <summary>Preset choices offered by the color-swatch picker.</summary>
    public static readonly IReadOnlyList<Color> PresetChoices = new[]
    {
        Colors.Pink, Colors.Red, Colors.OrangeRed, Colors.Orange, Colors.Yellow, Colors.YellowGreen,
        Colors.LimeGreen, Colors.Green, Colors.Teal, Colors.Cyan, Colors.LightSkyBlue, Colors.DodgerBlue,
        Colors.Blue, Colors.Purple, Colors.Magenta, Colors.HotPink, Colors.Brown, Colors.White,
        Colors.LightGray, Colors.Gray,
    };

    public static Color ForRunIndex(int index) => Palette[index % Palette.Length];
}
