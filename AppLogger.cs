namespace YamuraView;

/// <summary>
/// Simple app-wide log file, truncated at each application start - equivalent to the WinForms
/// app's AppLogger, but stored in the app's sandboxed data directory (the cross-platform
/// equivalent of %APPDATA%) instead of directly under %APPDATA%. Records file opens (attempted
/// and failed) and caught exceptions, viewable via the Settings page's "View Log" button.
/// </summary>
public static class AppLogger
{
    private static readonly string LogFilePath = Path.Combine(FileSystem.AppDataDirectory, "YamuraView.log");
    private static readonly object LockObj = new();

    public static void Init()
    {
        lock (LockObj)
        {
            try
            {
                File.WriteAllText(LogFilePath, $"YamuraView log started {DateTime.Now:G}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[YamuraView] Failed to init log file {LogFilePath}: {ex.Message}");
            }
        }
    }

    public static void Log(string message)
    {
        lock (LockObj)
        {
            try
            {
                File.AppendAllText(LogFilePath, $"{DateTime.Now:G}  {message}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[YamuraView] Failed to write log file {LogFilePath}: {ex.Message}");
            }
        }
    }

    public static string ReadAll()
    {
        lock (LockObj)
        {
            try
            {
                return File.Exists(LogFilePath) ? File.ReadAllText(LogFilePath) : "";
            }
            catch (Exception ex)
            {
                return $"Failed to read log file {LogFilePath}: {ex.Message}";
            }
        }
    }
}
