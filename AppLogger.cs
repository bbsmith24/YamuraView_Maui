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

    // one persistent writer instead of open/append/close per message: a parse that logs
    // per bad record was spending most of its time reopening the log file
    private static StreamWriter? writer;

    public static void Init()
    {
        lock (LockObj)
        {
            try
            {
                writer?.Dispose();
                FileStream stream = new(LogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                writer = new StreamWriter(stream) { AutoFlush = true };
                writer.WriteLine($"YamuraView log started {DateTime.Now:G}");
            }
            catch (Exception ex)
            {
                writer = null;
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
                writer?.WriteLine($"{DateTime.Now:G}  {message}");
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
                if (!File.Exists(LogFilePath))
                {
                    return "";
                }
                // the writer holds the file open, so read with a share mode that allows it
                using FileStream stream = new(LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader reader = new(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                return $"Failed to read log file {LogFilePath}: {ex.Message}";
            }
        }
    }
}
