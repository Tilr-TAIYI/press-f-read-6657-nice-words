using System.Text.Json;
using System.IO;
using System.Text;

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
    public string? LastLoadError { get; private set; }

    public AppSettings Load()
    {
        LastLoadError = null;
        try
        {
            var source = File.Exists(FilePath)
                ? FilePath
                : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(source))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source), options) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            try
            {
                if (File.Exists(FilePath))
                    File.Copy(FilePath, FilePath + ".corrupt." + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            }
            catch { }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
