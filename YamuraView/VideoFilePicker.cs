namespace YamuraView;

/// <summary>
/// Picks an action-camera video (MP4/MOV) for the synced video pane. Windows uses the native
/// WinRT picker bound to the app window, the same way as <see cref="LogFilePicker"/> (MAUI's
/// own picker has no owner window there). Other platforms use the MAUI FilePicker. On Android
/// and iOS that copies the picked file into the app cache first, so a long 4K clip takes a
/// moment to open.
/// </summary>
public static class VideoFilePicker
{
    public static async Task<string?> PickAsync()
    {
#if WINDOWS
        Windows.Storage.Pickers.FileOpenPicker picker = new()
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary
        };
        foreach (string extension in new[] { ".mp4", ".mov", ".m4v", ".lrv" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        Microsoft.UI.Xaml.Window? window = (Application.Current?.Windows[0].Handler?.PlatformView) as Microsoft.UI.Xaml.Window;
        if (window != null)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        return file?.Path;
#else
        FilePickerFileType fileType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.Android, new[] { "video/*" } },
            { DevicePlatform.iOS, new[] { "public.movie" } },
            { DevicePlatform.MacCatalyst, new[] { "public.movie" } },
        });
        FileResult? result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Select a video",
            FileTypes = fileType
        });
        return result?.FullPath;
#endif
    }
}
