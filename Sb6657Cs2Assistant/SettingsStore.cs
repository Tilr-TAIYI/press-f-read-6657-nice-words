using System.Text.Json;
using System.IO;

namespace Sb6657Cs2Assistant;

public sealed class SettingsStore
{
    public SettingsStore(string? directoryPath = null)
    {
        DirectoryPath = directoryPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sb6657Cs2Assistant");
    }

    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "appsettings.json");

    public AppSettings Load()
    {
        try
        {
            var source = File.Exists(FilePath)
                ? FilePath
                : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(source))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, FilePath, true);
    }
}
