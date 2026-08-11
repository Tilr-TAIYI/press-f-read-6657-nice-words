using Microsoft.Win32;
using System.IO;
using System.Text.RegularExpressions;

namespace Sb6657Cs2Assistant;

public sealed record InstallationInfo(string SteamPath, string Cs2Path, string SteamUserId);

public sealed class InstallationService
{
    public InstallationInfo? Detect()
    {
        foreach (var steam in DetectSteamPaths())
        {
            var cs2 = DetectCs2Path(steam);
            if (cs2 is null) continue;
            return new InstallationInfo(steam, cs2, DetectSteamUser(steam) ?? string.Empty);
        }
        return null;
    }

    public InstallationInfo? ResolveSaved(string steamPath, string cs2Path, string steamUserId)
    {
        var steam = NormalizeExistingDirectory(steamPath);
        var cs2 = ResolveCs2Path(cs2Path);
        if (steam is null || cs2 is null || !LooksLikeSteamRoot(steam)) return null;

        var user = HasCs2UserData(steam, steamUserId)
            ? steamUserId
            : DetectSteamUser(steam) ?? string.Empty;
        return new InstallationInfo(steam, cs2, user);
    }

    public string? DetectSteamPath() => DetectSteamPaths().FirstOrDefault();

    public string? DetectCs2Path(string steamPath)
    {
        var steam = NormalizeExistingDirectory(steamPath);
        if (steam is null) return null;

        foreach (var library in ReadSteamLibraries(steam))
        {
            var steamApps = Path.Combine(library, "steamapps");
            var manifest = Path.Combine(steamApps, "appmanifest_730.acf");
            if (File.Exists(manifest))
            {
                try
                {
                    var match = Regex.Match(File.ReadAllText(manifest), "\\\"installdir\\\"\\s+\\\"(?<dir>[^\\\"]+)\\\"", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        var fromManifest = ResolveCs2Path(Path.Combine(steamApps, "common", DecodeVdfPath(match.Groups["dir"].Value)));
                        if (fromManifest is not null) return fromManifest;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            var conventional = ResolveCs2Path(Path.Combine(steamApps, "common", "Counter-Strike Global Offensive"));
            if (conventional is not null) return conventional;
        }
        return null;
    }

    public string? ResolveCs2Path(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var selected = value.Trim().Trim('"');
            var current = File.Exists(selected) ? Path.GetDirectoryName(Path.GetFullPath(selected)) : NormalizeExistingDirectory(selected);
            for (var depth = 0; depth < 6 && current is not null; depth++)
            {
                if (File.Exists(Path.Combine(current, "game", "bin", "win64", "cs2.exe")))
                    return Path.GetFullPath(current);
                current = Directory.GetParent(current)?.FullName;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    public string? DetectSteamUser(string steamPath)
    {
        var steam = NormalizeExistingDirectory(steamPath);
        if (steam is null) return null;

        string? recentAccount = null;
        var loginUsers = Path.Combine(steam, "config", "loginusers.vdf");
        if (File.Exists(loginUsers))
        {
            try
            {
                var text = File.ReadAllText(loginUsers);
                foreach (Match user in Regex.Matches(
                    text,
                    "\\\"(?<id>\\d{5,})\\\"\\s*\\{(?<body>.*?)^\\s*\\}",
                    RegexOptions.Singleline | RegexOptions.Multiline))
                {
                    if (!Regex.IsMatch(user.Groups["body"].Value, "\\\"MostRecent\\\"\\s+\\\"1\\\"", RegexOptions.IgnoreCase)) continue;
                    recentAccount = SteamId64ToAccountId(user.Groups["id"].Value);
                    if (HasCs2UserData(steam, recentAccount)) return recentAccount;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata)) return recentAccount;
        try
        {
            var accounts = Directory.EnumerateDirectories(userdata)
                .Select(path => new { Path = path, Id = Path.GetFileName(path) })
                .Where(x => !string.IsNullOrEmpty(x.Id) && x.Id.All(char.IsDigit))
                .OrderByDescending(x => HasCs2UserData(steam, x.Id))
                .ThenByDescending(x => SafeLastWriteTimeUtc(Path.Combine(x.Path, "730")))
                .ToList();
            return accounts.FirstOrDefault(x => HasCs2UserData(steam, x.Id))?.Id
                ?? accounts.FirstOrDefault(x => x.Id.Equals(recentAccount, StringComparison.Ordinal))?.Id
                ?? accounts.FirstOrDefault()?.Id
                ?? recentAccount;
        }
        catch (IOException) { return recentAccount; }
        catch (UnauthorizedAccessException) { return recentAccount; }
    }

    private static IReadOnlyList<string> DetectSteamPaths()
    {
        var paths = new List<string>();
        var keys = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam")
        };
        foreach (var (hive, view, path) in keys)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(path);
                AddDirectory(paths, key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string);
                var executable = key?.GetValue("SteamExe") as string;
                if (!string.IsNullOrWhiteSpace(executable)) AddDirectory(paths, Path.GetDirectoryName(executable.Trim('"')));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return paths;
    }

    private static IReadOnlyList<string> ReadSteamLibraries(string steamPath)
    {
        var libraries = new List<string>();
        AddDirectory(libraries, steamPath);
        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFile)) return libraries;
        try
        {
            var text = File.ReadAllText(libraryFile);
            foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                AddDirectory(libraries, DecodeVdfPath(match.Groups["path"].Value));
            foreach (Match match in Regex.Matches(text, "(?m)^\\s*\\\"\\d{1,3}\\\"\\s+\\\"(?<path>[^\\\"]+)\\\""))
                AddDirectory(libraries, DecodeVdfPath(match.Groups["path"].Value));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return libraries;
    }

    private static bool LooksLikeSteamRoot(string path) =>
        File.Exists(Path.Combine(path, "steam.exe")) ||
        Directory.Exists(Path.Combine(path, "steamapps")) ||
        Directory.Exists(Path.Combine(path, "userdata"));

    private static bool IsSteamUserDirectory(string steamPath, string userId) =>
        !string.IsNullOrWhiteSpace(userId) &&
        userId.All(char.IsDigit) &&
        Directory.Exists(Path.Combine(steamPath, "userdata", userId));

    private static bool HasCs2UserData(string steamPath, string userId) =>
        IsSteamUserDirectory(steamPath, userId) &&
        Directory.Exists(Path.Combine(steamPath, "userdata", userId, "730"));

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private static string? NormalizeExistingDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var path = Path.GetFullPath(value.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static void AddDirectory(ICollection<string> paths, string? value)
    {
        var path = NormalizeExistingDirectory(value is null ? null : DecodeVdfPath(value));
        if (path is not null && !paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
    }

    private static string DecodeVdfPath(string value) => value.Replace("\\\\", "\\", StringComparison.Ordinal);

    private static string SteamId64ToAccountId(string id)
    {
        return ulong.TryParse(id, out var steamId) && steamId > 76561197960265728UL
            ? (steamId - 76561197960265728UL).ToString()
            : id;
    }
}
