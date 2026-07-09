namespace YamuraView;

/// <summary>
/// Folder picker for the autoload folder setting. Core .NET MAUI has no cross-platform
/// folder picker API (unlike FilePicker), and CommunityToolkit.Maui's FolderPicker isn't
/// available at a version compatible with this app's MAUI version, so this uses the native
/// WinRT folder picker directly on Windows. Other platforms fall back to manual path entry
/// in the Settings page (the Browse button shows a "not supported" message there).
/// </summary>
public static class AutoloadFolderPicker
{
#if WINDOWS
    public static bool IsSupported => true;
#else
    public static bool IsSupported => false;
#endif

    public static async Task<string?> PickAsync()
    {
#if WINDOWS
        Windows.Storage.Pickers.FolderPicker picker = new()
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");

        Microsoft.UI.Xaml.Window? window = (Application.Current?.Windows[0].Handler?.PlatformView) as Microsoft.UI.Xaml.Window;
        if (window != null)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
#else
        return await Task.FromResult<string?>(null);
#endif
    }
}
