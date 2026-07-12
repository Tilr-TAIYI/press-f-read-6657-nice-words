using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.IO;

namespace Sb6657Cs2Assistant;

public sealed record InstallationInfo(string SteamPath, string Cs2Path, string SteamUserId);

public sealed class InstallationService
{
    public InstallationInfo? Detect()
    {
        var steam = DetectSteamPath();
        if (steam is null) return null;
        var cs2 = DetectCs2Path(steam);
        var user = DetectSteamUser(steam);
        return cs2 is null ? null : new InstallationInfo(steam, cs2, user ?? "");
    }

    public string? DetectSteamPath()
    {
        var keys = new[]
        {
            (RegistryHive.CurrentUser, @"Software\Valve\Steam"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam")
        };
        foreach (var (hive, path) in keys)
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = root.OpenSubKey(path);
            var value = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value)) return Path.GetFullPath(value);
        }
        return null;
    }

    public string? DetectCs2Path(string steamPath)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamPath };
        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(libraryFile), "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\""))
                libraries.Add(match.Groups["path"].Value.Replace("\\\\", "\\"));
        }
        foreach (var library in libraries)
        {
            var root = Path.Combine(library, "steamapps", "common", "Counter-Strike Global Offensive");
            if (File.Exists(Path.Combine(root, "game", "bin", "win64", "cs2.exe"))) return root;
        }
        return null;
    }

    public string? DetectSteamUser(string steamPath)
    {
        var loginUsers = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (File.Exists(loginUsers))
        {
            var text = File.ReadAllText(loginUsers);
            foreach (Match user in Regex.Matches(text, "\\\"(?<id>\\d{5,})\\\"\\s*\\{(?<body>.*?)\\n\\}", RegexOptions.Singleline))
                if (Regex.IsMatch(user.Groups["body"].Value, "\\\"MostRecent\\\"\\s+\\\"1\\\"", RegexOptions.IgnoreCase))
                    return SteamId64ToAccountId(user.Groups["id"].Value);
        }
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return null;
        return Directory.GetDirectories(userdata)
            .Select(x => Path.GetFileName(x))
            .FirstOrDefault(x => !string.IsNullOrEmpty(x) && x.All(char.IsDigit));
    }

    private static string SteamId64ToAccountId(string id)
    {
        return ulong.TryParse(id, out var steamId) && steamId > 76561197960265728UL
            ? (steamId - 76561197960265728UL).ToString()
            : id;
    }
}
