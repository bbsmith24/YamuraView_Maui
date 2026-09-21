using YamuraView.Core;

namespace YamuraView;

/// <summary>
/// Saves and opens ".ytm" track-map files across platforms. Windows uses the native WinRT
/// pickers bound to the app window (same approach as <see cref="LogFilePicker"/>, which works
/// around a MAUI picker crash). On mobile/Mac there is no core save-picker, so a save writes the
/// file into the app's cache and hands it to the system share sheet - the natural way to get a
/// map authored on a phone off to an analysis machine.
/// </summary>
public static class TrackMapFilePicker
{
    /// <summary>Saves <paramref name="map"/> as a ".ytm". Returns the written path, or null if
    /// the user cancelled. On mobile/Mac the file is written to the cache and offered via the
    /// share sheet. The map's <see cref="TrackMap.Name"/> attribute is set to the saved file's
    /// base name before writing, so the name stored in the file always matches the file the user
    /// actually chose (they can rename it in the save dialog).</summary>
    public static async Task<string?> SaveAsync(TrackMap map, string suggestedName)
    {
        string safeName = MakeSafeFileName(string.IsNullOrWhiteSpace(suggestedName) ? "TrackMap" : suggestedName);
#if WINDOWS
        Windows.Storage.Pickers.FileSavePicker picker = new()
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = safeName
        };
        picker.FileTypeChoices.Add("Yamura Track Map", new List<string> { TrackMapFile.Extension });

        Microsoft.UI.Xaml.Window? window = (Application.Current?.Windows[0].Handler?.PlatformView) as Microsoft.UI.Xaml.Window;
        if (window != null)
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return null;
        }
        // sync the map's Name attribute to the file the user actually chose (they may have
        // renamed it in the dialog), so the name stored in the file matches the file name
        map.Name = Path.GetFileNameWithoutExtension(file.Path);
        TrackMapFile.Write(map, file.Path);
        return file.Path;
#else
        string path = Path.Combine(FileSystem.CacheDirectory, safeName + TrackMapFile.Extension);
        map.Name = Path.GetFileNameWithoutExtension(path);
        TrackMapFile.Write(map, path);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "Save Track Map",
            File = new ShareFile(path)
        });
        return path;
#endif
    }

    /// <summary>Prompts for a ".ytm" to open and returns its path, or null if cancelled.</summary>
    public static async Task<string?> PickOpenAsync()
    {
#if WINDOWS
        Windows.Storage.Pickers.FileOpenPicker picker = new()
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(TrackMapFile.Extension);

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
            { DevicePlatform.Android, new[] { "*/*" } },
            { DevicePlatform.iOS, new[] { "public.data" } },
            { DevicePlatform.MacCatalyst, new[] { "public.data" } },
        });
        FileResult? result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Select a track map (.ytm)",
            FileTypes = fileType
        });
        return result?.FullPath;
#endif
    }

    private static string MakeSafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }
}
