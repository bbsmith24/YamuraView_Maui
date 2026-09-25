using System.Xml.Linq;

namespace YamuraView;

/// <summary>
/// App-level settings - equivalent to the WinForms app's %APPDATA%\YamuraView.ini. Remembers
/// where the "config file" (chart colors, channel/axis preferences) lives, plus the autoload
/// folder path (equivalent to the WinForms app's FolderToWatch), so both survive a restart.
/// Stored in the app's sandboxed data directory, the cross-platform equivalent of %APPDATA%.
/// </summary>
public class AppSettings
{
    private static string IniPath => Path.Combine(FileSystem.AppDataDirectory, "YamuraView.ini");

    public string ConfigFilePath { get; set; } = Path.Combine(FileSystem.AppDataDirectory, "YamuraViewConfig.xml");
    public string AutoloadFolderPath { get; set; } = "";

    public static AppSettings Load()
    {
        if (!File.Exists(IniPath))
        {
            return new AppSettings();
        }
        try
        {
            XDocument doc = XDocument.Load(IniPath);
            XElement? root = doc.Element("Setup");
            string? configPath = (string?)root?.Attribute("Config");
            string? autoloadFolder = (string?)root?.Attribute("AutoloadFolder");
            AppSettings settings = new();
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                settings.ConfigFilePath = ResolveConfigPath(configPath);
            }
            if (!string.IsNullOrWhiteSpace(autoloadFolder))
            {
                settings.AutoloadFolderPath = autoloadFolder;
            }
            return settings;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Failed to load {IniPath}: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            XDocument doc = new(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("Setup",
                    new XAttribute("Config", ToStoredConfigPath(ConfigFilePath)),
                    new XAttribute("AutoloadFolder", AutoloadFolderPath)));
            doc.Save(IniPath);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Failed to save {IniPath}: {ex.Message}");
        }
    }

    // The app data directory isn't a stable absolute path everywhere: on iOS it sits under a
    // per-install container GUID that changes on every reinstall/update, so an absolute path
    // saved by the previous install points at a folder that no longer exists. A config inside
    // the app data directory is therefore stored relative to it, and resolved against the
    // current one on load.

    /// <summary>The path as written to the ini: relative when inside the app data directory.</summary>
    private static string ToStoredConfigPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string relative = Path.GetRelativePath(FileSystem.AppDataDirectory, full);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return relative;
            }
        }
        catch (Exception)
        {
            // unparseable path - store as entered
        }
        return path;
    }

    /// <summary>
    /// The ini's stored path made usable on this install: a relative path is resolved against the
    /// app data directory; an absolute path that no longer exists (an ini written by an older
    /// build before an iOS container move) falls back to the same file name in the current app
    /// data directory if that file is there.
    /// </summary>
    private static string ResolveConfigPath(string stored)
    {
        if (!Path.IsPathRooted(stored))
        {
            return Path.Combine(FileSystem.AppDataDirectory, stored);
        }
        if (!File.Exists(stored))
        {
            string local = Path.Combine(FileSystem.AppDataDirectory, Path.GetFileName(stored));
            if (File.Exists(local))
            {
                AppLogger.Log($"Config path {stored} not found; using {local}");
                return local;
            }
        }
        return stored;
    }
}
