using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;

namespace Sb6657Cs2Assistant;

public sealed class Cs2ConfigService
{
    public const string BindCfgName = "sb6657_miao_bind.cfg";
    public const string SendCfgName = "sb6657_miao_send.cfg";
    private const string ManagedMarker = "// SB6657_MIAO_MANAGED";
    private const string AutoexecMarker = "// SB6657_MIAO_AUTOEXEC";
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly bool _enforceGameStopped;

    public Cs2ConfigService(SettingsStore store, AppSettings settings, bool enforceGameStopped = true)
    {
        _store = store;
        _settings = settings;
        _enforceGameStopped = enforceGameStopped;
    }

    public string CfgDirectory => ResolveCfgDirectory();

    public IReadOnlyList<string> UserKeyFiles()
    {
        if (string.IsNullOrWhiteSpace(_settings.SteamPath) ||
            string.IsNullOrWhiteSpace(_settings.SteamUserId) ||
            !_settings.SteamUserId.All(char.IsDigit)) return [];
        var steamRoot = Path.GetFullPath(_settings.SteamPath);
        var root = Path.GetFullPath(Path.Combine(steamRoot, "userdata", _settings.SteamUserId, "730"));
        if (!IsUnder(root, steamRoot)) return [];
        var files = new List<string>();
        foreach (var folder in new[] { "local", "remote" })
        {
            var cfg = Path.Combine(root, folder, "cfg");
            if (!Directory.Exists(cfg)) continue;
            try
            {
                // Valve has changed slot suffixes across updates; only accept the
                // narrowly scoped CS2 user-key naming pattern.
                files.AddRange(Directory.EnumerateFiles(cfg, "cs2_user_keys*.vcfg", SearchOption.TopDirectoryOnly));
            }
            catch (UnauthorizedAccessException) { }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).Select(Path.GetFullPath).ToList();
    }

    private string ResolveCfgDirectory()
    {
        if (string.IsNullOrWhiteSpace(_settings.Cs2Path)) return string.Empty;
        var root = Path.GetFullPath(_settings.Cs2Path);
        var known = Path.Combine(root, "game", "csgo", "cfg");
        if (Directory.Exists(known)) return known;

        // Keep working if a future update moves the cfg folder inside game/.
        // The fallback stays under the selected CS2 root and prefers a csgo folder.
        var game = Path.Combine(root, "game");
        if (!Directory.Exists(game)) return known;
        try
        {
            var candidate = Directory.EnumerateDirectories(game, "cfg", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "csgo", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.Length)
                .FirstOrDefault();
            return candidate is null ? known : Path.GetFullPath(candidate);
        }
        catch (IOException) { return known; }
        catch (UnauthorizedAccessException) { return known; }
    }

    public void ApplyBinding(string newKey)
    {
        ValidateInstallation(requireUserKeys: true);
        EnsureGameStopped();
        newKey = NormalizeKey(newKey);
        var files = UserKeyFiles();
        var affected = files
            .Append(Path.Combine(CfgDirectory, "autoexec.cfg"))
            .Append(Path.Combine(CfgDirectory, BindCfgName))
            .ToList();
        var disk = CaptureFiles(affected);
        var state = CaptureState();

        try
        {
            Directory.CreateDirectory(CfgDirectory);
            AdoptLegacySendCfgIfKnown();
            EnsureManagedOrMissing(Path.Combine(CfgDirectory, BindCfgName));
            EnsureManagedOrMissing(Path.Combine(CfgDirectory, SendCfgName));
            BackupFilesOnce();

            RestoreOrphanedManagedBindings(files, newKey);

            if (!newKey.Equals(_settings.BoundKey, StringComparison.OrdinalIgnoreCase))
            {
                _settings.OriginalBindings = files.ToDictionary(
                    Path.GetFullPath,
                    file => { var old = ReadBinding(file, newKey); return new BindingSnapshot(old.Existed, old.Command); },
                    StringComparer.OrdinalIgnoreCase);
            }

            foreach (var file in files) SetBinding(file, newKey, $"exec {Path.GetFileNameWithoutExtension(SendCfgName)}");
            AtomicWrite(Path.Combine(CfgDirectory, BindCfgName),
                $"{ManagedMarker}\nbind \"{newKey}\" \"exec {Path.GetFileNameWithoutExtension(SendCfgName)}\"\n");
            EnsureAutoexec();
            _settings.BoundKey = newKey;
            _settings.SendKey = newKey;
            _store.Save(_settings);
        }
        catch
        {
            RestoreFiles(disk);
            RestoreState(state);
            throw;
        }
    }

    public void WriteSendCommand(string text, string channel)
    {
        ValidateInstallation(requireUserKeys: false);
        var target = Path.Combine(CfgDirectory, SendCfgName);
        AdoptLegacySendCfgIfKnown();
        EnsureManagedOrMissing(target);
        var command = channel.Equals("Team", StringComparison.OrdinalIgnoreCase) ? "say_team" : "say";
        var escaped = EscapeCfgText(text);
        AtomicWrite(target, $"{ManagedMarker}\n{command} \"{escaped}\"\n");
    }

    public bool IsBindingApplied(out string reason)
    {
        if (string.IsNullOrWhiteSpace(_settings.BoundKey)) { reason = "尚未应用发送键绑定"; return false; }
        var expected = $"exec {Path.GetFileNameWithoutExtension(SendCfgName)}";
        var files = UserKeyFiles();
        if (files.Count == 0) { reason = "未找到当前 Steam 用户按键配置"; return false; }
        foreach (var file in files)
        {
            var binding = ReadBinding(file, _settings.BoundKey);
            if (!binding.Existed || !expected.Equals(binding.Command, StringComparison.OrdinalIgnoreCase))
            { reason = $"{Path.GetFileName(file)} 中没有 {_settings.BoundKey} 绑定"; return false; }
        }
        var bindCfg = Path.Combine(CfgDirectory, BindCfgName);
        if (!File.Exists(bindCfg) || !IsManagedFile(bindCfg))
        { reason = $"缺少 {BindCfgName}"; return false; }
        var autoexec = Path.Combine(CfgDirectory, "autoexec.cfg");
        if (!File.Exists(autoexec) || !File.ReadLines(autoexec).Any(IsManagedAutoexecLine))
        { reason = "autoexec.cfg 尚未加载本工具绑定"; return false; }
        reason = "配置已应用";
        return true;
    }

    public void RemoveCreatedConfiguration()
    {
        ValidateInstallation(requireUserKeys: false);
        EnsureGameStopped();
        var files = UserKeyFiles();
        var managed = new[]
        {
            Path.Combine(CfgDirectory, BindCfgName),
            Path.Combine(CfgDirectory, SendCfgName)
        };
        var affected = files.Append(Path.Combine(CfgDirectory, "autoexec.cfg")).Concat(managed).ToList();
        var disk = CaptureFiles(affected);
        var state = CaptureState();

        try
        {
            RestoreOrphanedManagedBindings(files, keepKey: null);
            foreach (var path in managed) DeleteOnlyManagedFile(path);
            RemoveAutoexecMarker();
            _settings.BoundKey = "";
            _settings.OriginalBindings.Clear();
            _settings.OriginalBindingCommand = null;
            _settings.OriginalBindingExisted = false;
            _settings.AutoexecCreatedByTool = false;
            _store.Save(_settings);
        }
        catch
        {
            RestoreFiles(disk);
            RestoreState(state);
            throw;
        }
    }

    private void RestoreStoredBindings(IReadOnlyList<string> files, string key)
    {
        foreach (var file in files)
        {
            if (_settings.OriginalBindings.TryGetValue(Path.GetFullPath(file), out var snapshot))
                RestoreBinding(file, key, snapshot.Command, snapshot.Existed);
            else
                RestoreBinding(file, key, _settings.OriginalBindingCommand, _settings.OriginalBindingExisted);
        }
    }

    private void RestoreOrphanedManagedBindings(IReadOnlyList<string> files, string? keepKey)
    {
        var expected = $"exec {Path.GetFileNameWithoutExtension(SendCfgName)}";
        foreach (var file in files)
        {
            foreach (var key in FindBindingsWithCommand(file, expected))
            {
                if (key.Equals(keepKey, StringComparison.OrdinalIgnoreCase)) continue;
                BindingSnapshot? snapshot = null;
                if (key.Equals(_settings.BoundKey, StringComparison.OrdinalIgnoreCase) &&
                    _settings.OriginalBindings.TryGetValue(Path.GetFullPath(file), out var stored))
                    snapshot = stored;
                snapshot ??= ReadInitialBackupBinding(file, key);
                if (snapshot is null)
                    throw new InvalidOperationException($"发现遗留绑定 {key}，但找不到其初始备份，已拒绝覆盖");
                RestoreBinding(file, key, snapshot.Command, snapshot.Existed);
            }
        }
    }

    private static IReadOnlyList<string> FindBindingsWithCommand(string file, string command)
    {
        var matches = Regex.Matches(
            File.ReadAllText(file),
            "(?m)^[ \\t]*\\\"(?<key>[^\\\"]+)\\\"[ \\t]+\\\"" + Regex.Escape(command) + "\\\"[ \\t]*\\r?$",
            RegexOptions.IgnoreCase);
        return matches.Select(x => x.Groups["key"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private BindingSnapshot? ReadInitialBackupBinding(string userKeyFile, string key)
    {
        var prefix = userKeyFile.Contains("remote", StringComparison.OrdinalIgnoreCase) ? "remote_" : "local_";
        var backup = Path.Combine(_store.DirectoryPath, "backups", prefix + Path.GetFileName(userKeyFile) + ".original.bak");
        if (!File.Exists(backup)) return null;
        var original = ReadBinding(backup, key);
        return new BindingSnapshot(original.Existed, original.Command);
    }

    private void BackupFilesOnce()
    {
        var backup = Path.Combine(_store.DirectoryPath, "backups");
        Directory.CreateDirectory(backup);
        foreach (var file in UserKeyFiles().Append(Path.Combine(CfgDirectory, "autoexec.cfg")).Where(File.Exists))
        {
            var relativeName = file.Contains("remote", StringComparison.OrdinalIgnoreCase)
                ? "remote_" + Path.GetFileName(file)
                : file.Contains("local", StringComparison.OrdinalIgnoreCase)
                    ? "local_" + Path.GetFileName(file)
                    : Path.GetFileName(file);
            var destination = Path.Combine(backup, relativeName + ".original.bak");
            if (!File.Exists(destination)) File.Copy(file, destination);
        }
    }

    private void EnsureAutoexec()
    {
        var path = Path.Combine(CfgDirectory, "autoexec.cfg");
        var existed = File.Exists(path);
        var text = existed ? File.ReadAllText(path) : "";
        if (text.Split(["\r\n", "\n"], StringSplitOptions.None).Any(IsManagedAutoexecLine)) return;
        _settings.AutoexecCreatedByTool = !existed;
        var line = $"exec {Path.GetFileNameWithoutExtension(BindCfgName)} {AutoexecMarker}";
        AtomicWrite(path, text + (text.Length == 0 || text.EndsWith('\n') ? "" : Environment.NewLine) + line + Environment.NewLine);
    }

    private void RemoveAutoexecMarker()
    {
        var path = Path.Combine(CfgDirectory, "autoexec.cfg");
        if (!File.Exists(path)) return;
        var remaining = File.ReadAllLines(path).Where(x => !IsManagedAutoexecLine(x)).ToArray();
        if (_settings.AutoexecCreatedByTool && remaining.All(string.IsNullOrWhiteSpace)) File.Delete(path);
        else AtomicWrite(path, string.Join(Environment.NewLine, remaining) + (remaining.Length > 0 ? Environment.NewLine : ""));
    }

    private static (bool Existed, string? Command) ReadBinding(string file, string key)
    {
        var match = Regex.Match(File.ReadAllText(file), $"(?m)^[ \\t]*\\\"{Regex.Escape(key)}\\\"[ \\t]+\\\"(?<cmd>[^\\\"]*)\\\"", RegexOptions.IgnoreCase);
        return match.Success ? (true, match.Groups["cmd"].Value) : (false, null);
    }

    private static void SetBinding(string file, string key, string command)
    {
        var text = File.ReadAllText(file);
        var pattern = $"(?m)^(?<indent>[ \\t]*)\\\"{Regex.Escape(key)}\\\"[ \\t]+\\\"[^\\\"]*\\\"";
        if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
            text = Regex.Replace(text, pattern, $"${{indent}}\"{key}\"\t\t\"{command}\"", RegexOptions.IgnoreCase);
        else
        {
            var block = Regex.Match(text, "(?s)\\\"bindings\\\"\\s*\\{(?<body>.*?)(?<close>\\r?\\n\\s*\\}\\s*\\r?\\n\\s*\\\"analogbindings\\\")");
            if (!block.Success) throw new InvalidDataException("无法定位 bindings 配置块");
            text = text.Insert(block.Groups["body"].Index + block.Groups["body"].Length,
                $"{Environment.NewLine}\t\t\"{key}\"\t\t\"{command}\"");
        }
        AtomicWrite(file, text);
    }

    private static void RestoreBinding(string file, string key, string? command, bool existed)
    {
        if (existed) SetBinding(file, key, command ?? "<unbound>");
        else
        {
            var text = Regex.Replace(File.ReadAllText(file), $"(?m)^[ \\t]*\\\"{Regex.Escape(key)}\\\"[ \\t]+\\\"[^\\\"]*\\\"[ \\t]*\\r?\\n?", "", RegexOptions.IgnoreCase);
            AtomicWrite(file, text);
        }
    }

    private void ValidateInstallation(bool requireUserKeys)
    {
        if (string.IsNullOrWhiteSpace(_settings.Cs2Path) || string.IsNullOrWhiteSpace(_settings.SteamPath))
            throw new DirectoryNotFoundException("请先选择有效的 Steam 和 CS2 安装目录");
        var cs2Root = Path.GetFullPath(_settings.Cs2Path);
        var steamRoot = Path.GetFullPath(_settings.SteamPath);
        var cs2Exe = Path.Combine(cs2Root, "game", "bin", "win64", "cs2.exe");
        if (!IsUnder(CfgDirectory, cs2Root))
            throw new InvalidOperationException("CFG 目录不在选定的 CS2 安装目录内，已拒绝写入");
        if (!IsUnder(Path.GetFullPath(Path.Combine(steamRoot, "userdata")), steamRoot))
            throw new InvalidOperationException("Steam 用户目录无效，已拒绝写入");
        if (string.IsNullOrWhiteSpace(_settings.Cs2Path) || !File.Exists(cs2Exe) || !Directory.Exists(CfgDirectory))
            throw new DirectoryNotFoundException("CS2 安装目录无效");
        if (requireUserKeys && UserKeyFiles().Count == 0)
            throw new FileNotFoundException("未找到当前 Steam 用户的 CS2 按键配置，已拒绝写入");
    }

    private void EnsureGameStopped()
    {
        if (_enforceGameStopped && Process.GetProcessesByName("cs2").Length > 0)
            throw new InvalidOperationException("为防止 CS2 覆盖配置，请先完全退出 CS2 再应用、换绑或删除配置");
    }

    private static void EnsureManagedOrMissing(string path)
    {
        if (File.Exists(path) && !IsManagedFile(path))
            throw new IOException($"文件 {Path.GetFileName(path)} 已存在但不是本工具创建，已拒绝覆盖");
    }

    private void AdoptLegacySendCfgIfKnown()
    {
        var path = Path.Combine(CfgDirectory, SendCfgName);
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        if (IsManagedFile(path)) return;
        var isLegacyShape = Regex.IsMatch(text, "^\\s*(say|say_team)\\s+\\\"[^\\r\\n]*\\\"\\s*$", RegexOptions.IgnoreCase);
        if (!isLegacyShape || _settings.SendHistory.Count == 0) return;
        var backupDir = Path.Combine(_store.DirectoryPath, "backups");
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, SendCfgName + ".legacy.bak");
        if (!File.Exists(backup)) File.Copy(path, backup);
        AtomicWrite(path, ManagedMarker + Environment.NewLine + text.Trim() + Environment.NewLine);
    }

    private static void DeleteOnlyManagedFile(string path)
    {
        if (!File.Exists(path)) return;
        if (!IsManagedFile(path))
            throw new IOException($"文件 {Path.GetFileName(path)} 不含所有权标记，已拒绝删除");
        File.Delete(path);
    }

    private static Dictionary<string, byte[]?> CaptureFiles(IEnumerable<string> paths) =>
        paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(
            Path.GetFullPath,
            path => File.Exists(path) ? File.ReadAllBytes(path) : null,
            StringComparer.OrdinalIgnoreCase);

    private static void RestoreFiles(Dictionary<string, byte[]?> snapshots)
    {
        foreach (var (path, data) in snapshots)
        {
            if (data is null) { if (File.Exists(path)) File.Delete(path); }
            else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, data); }
        }
    }

    private SettingsState CaptureState() => new(
        _settings.BoundKey,
        _settings.SendKey,
        _settings.OriginalBindingCommand,
        _settings.OriginalBindingExisted,
        new Dictionary<string, BindingSnapshot>(_settings.OriginalBindings, StringComparer.OrdinalIgnoreCase),
        _settings.AutoexecCreatedByTool);

    private void RestoreState(SettingsState state)
    {
        _settings.BoundKey = state.BoundKey;
        _settings.SendKey = state.SendKey;
        _settings.OriginalBindingCommand = state.LegacyCommand;
        _settings.OriginalBindingExisted = state.LegacyExisted;
        _settings.OriginalBindings = state.Bindings;
        _settings.AutoexecCreatedByTool = state.AutoexecCreated;
    }

    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".sb6657.tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string NormalizeKey(string key)
    {
        var normalized = string.IsNullOrWhiteSpace(key) ? "F8" : key.Trim().ToUpperInvariant();
        if (normalized.Length > 24 || !Regex.IsMatch(normalized, "^[A-Z0-9_]+$"))
            throw new ArgumentException("发送键名称无效，只允许字母、数字和下划线");
        return normalized;
    }

    private static string EscapeCfgText(string text)
    {
        var clean = Regex.Replace(text ?? string.Empty, @"[\x00-\x1F\x7F]+", " ");
        clean = clean.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "'", StringComparison.Ordinal);
        return clean.Length <= 500 ? clean : clean[..500];
    }

    private static bool IsManagedFile(string path)
    {
        if (!File.Exists(path)) return false;
        var first = File.ReadLines(path).FirstOrDefault();
        return string.Equals(first?.Trim(), ManagedMarker, StringComparison.Ordinal);
    }

    private static bool IsManagedAutoexecLine(string line)
    {
        var trimmed = line.Trim();
        return Regex.IsMatch(
            trimmed,
            "^exec\\s+" + Regex.Escape(Path.GetFileNameWithoutExtension(BindCfgName)) + "\\s+" + Regex.Escape(AutoexecMarker) + "$",
            RegexOptions.IgnoreCase);
    }

    private static bool IsUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SettingsState(
        string BoundKey,
        string SendKey,
        string? LegacyCommand,
        bool LegacyExisted,
        Dictionary<string, BindingSnapshot> Bindings,
        bool AutoexecCreated);
}
