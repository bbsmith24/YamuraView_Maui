namespace YamuraView;

/// <summary>
/// Picks an action-camera video (MP4/MOV) for the synced video pane. Windows uses the native
/// WinRT picker bound to the app window, the same way as <see cref="LogFilePicker"/> (MAUI's
/// own picker has no owner window there). iOS and Mac Catalyst open the file in place with a
/// UIDocumentPicker and keep its security-scoped access until <see cref="Release"/>. Android
/// uses the MAUI FilePicker, which copies the picked file into the app cache first, so a long
/// 4K clip takes a moment to open.
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
#elif IOS || MACCATALYST
        // iOS / Mac: open the video in place with our own document picker and hold its
        // security-scoped access for as long as it's loaded. The MAUI picker's path is only
        // readable inside its own OpenReadAsync, so AVPlayer and the GPMF reader were refused
        // ("you don't have permission to view it") - and it would copy a multi-GB clip anyway.
        TaskCompletionSource<Foundation.NSUrl?> tcs = new();
        UIKit.UIDocumentPickerViewController picker = new(
            new[] { UniformTypeIdentifiers.UTTypes.Movie }, asCopy: false)
        {
            AllowsMultipleSelection = false
        };
        pickerDelegate = new PickerDelegate(tcs);
        picker.Delegate = pickerDelegate;
        UIKit.UIViewController? presenter = Platform.GetCurrentUIViewController();
        if (presenter == null)
        {
            return null;
        }
        presenter.PresentViewController(picker, true, null);

        Foundation.NSUrl? url = await tcs.Task;
        pickerDelegate = null;
        if (url?.Path == null)
        {
            return null;
        }
        Release(); // done with the previous video
        if (url.StartAccessingSecurityScopedResource())
        {
            accessedUrl = url;
        }
        return url.Path;
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

    /// <summary>Gives up access to the current video's file (iOS/Mac; no-op elsewhere).
    /// Called when the video is closed or replaced.</summary>
    public static void Release()
    {
#if IOS || MACCATALYST
        accessedUrl?.StopAccessingSecurityScopedResource();
        accessedUrl = null;
#endif
    }

#if IOS || MACCATALYST
    private static Foundation.NSUrl? accessedUrl;
    private static PickerDelegate? pickerDelegate; // kept alive while the picker is up

    private sealed class PickerDelegate(TaskCompletionSource<Foundation.NSUrl?> tcs) : UIKit.UIDocumentPickerDelegate
    {
        public override void DidPickDocument(UIKit.UIDocumentPickerViewController controller, Foundation.NSUrl[] urls)
            => tcs.TrySetResult(urls.FirstOrDefault());

        public override void WasCancelled(UIKit.UIDocumentPickerViewController controller)
            => tcs.TrySetResult(null);
    }
#endif
}
