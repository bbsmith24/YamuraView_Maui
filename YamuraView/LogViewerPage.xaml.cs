namespace YamuraView;

/// <summary>Shows the app log (file opens, caught exceptions) recorded by <see cref="AppLogger"/>,
/// opened from the Settings page's "View Log" button - equivalent to the WinForms app's
/// LogViewerDialog.</summary>
public partial class LogViewerPage : ContentPage
{
    public LogViewerPage()
    {
        InitializeComponent();
        LoadLog();
    }

    private void LoadLog()
    {
        string text = AppLogger.ReadAll();
        LogEditor.Text = text;
        LogEditor.CursorPosition = text.Length;
    }

    private void OnRefreshClicked(object? sender, EventArgs e)
    {
        LoadLog();
    }

    private async void OnCloseClicked(object? sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }
}
