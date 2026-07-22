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
                settings.ConfigFilePath = configPath;
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
                    new XAttribute("Config", ConfigFilePath),
                    new XAttribute("AutoloadFolder", AutoloadFolderPath)));
            doc.Save(IniPath);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Failed to save {IniPath}: {ex.Message}");
        }
    }
}
