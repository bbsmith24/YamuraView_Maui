namespace YamuraView;

/// <summary>
/// Picks one or more YamuraLog files to open. On Windows this uses the native WinRT file
/// picker directly, explicitly bound to the app's window handle - .NET MAUI's own
/// FilePicker.PickMultipleAsync crashes the whole process (STATUS_STOWED_EXCEPTION inside
/// Microsoft.UI.Xaml.dll) when several files are selected, because it never associates the
/// picker with an owner window (the same issue <see cref="AutoloadFolderPicker"/> works around
/// for folder selection). Other platforms use the standard MAUI FilePicker, which doesn't hit
/// this bug.
/// </summary>
public static class LogFilePicker
{
    public static async Task<IReadOnlyList<string>> PickMultipleAsync()
    {
#if WINDOWS
        Windows.Storage.Pickers.FileOpenPicker picker = new()
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".ylg");
        picker.FileTypeFilter.Add(".yl5");

        Microsoft.UI.Xaml.Window? window = (Application.Current?.Windows[0].Handler?.PlatformView) as Microsoft.UI.Xaml.Window;
        if (window != null)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).ToList();
#else
        FilePickerFileType fileType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.Android, new[] { "*/*" } },
            { DevicePlatform.iOS, new[] { "public.data" } },
            { DevicePlatform.MacCatalyst, new[] { "public.data" } },
        });

        IEnumerable<FileResult?> results = await FilePicker.Default.PickMultipleAsync(new PickOptions
        {
            PickerTitle = "Select YamuraLog file(s)",
            FileTypes = fileType
        });
        return results.Where(r => r != null).Select(r => r!.FullPath).ToList();
#endif
    }
}
