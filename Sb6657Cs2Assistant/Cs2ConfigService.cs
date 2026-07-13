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

    public string CfgDirectory => Path.Combine(_settings.Cs2Path, "game", "csgo", "cfg");

    public IReadOnlyList<string> UserKeyFiles()
    {
        if (string.IsNullOrWhiteSpace(_settings.SteamPath) || string.IsNullOrWhiteSpace(_settings.SteamUserId)) return [];
        var root = Path.GetFullPath(Path.Combine(_settings.SteamPath, "userdata", _settings.SteamUserId, "730"));
        return new[]
        {
            Path.Combine(root, "local", "cfg", "cs2_user_keys_0_slot0.vcfg"),
            Path.Combine(root, "remote", "cs2_user_keys.vcfg")
        }.Where(File.Exists).Select(Path.GetFullPath).ToList();
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
        var escaped = text.Replace("\\", "\\\\").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
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
        if (!File.Exists(bindCfg) || !File.ReadAllText(bindCfg).Contains(ManagedMarker, StringComparison.Ordinal))
        { reason = $"缺少 {BindCfgName}"; return false; }
        var autoexec = Path.Combine(CfgDirectory, "autoexec.cfg");
        if (!File.Exists(autoexec) || !File.ReadAllText(autoexec).Contains(AutoexecMarker, StringComparison.Ordinal))
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
            Path.Combine(CfgDirectory, SendCfgName),
            Path.Combine(CfgDirectory, SendCfgName + ".tmp")
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
        _settings.AutoexecCreatedByTool = !existed;
        if (text.Contains(AutoexecMarker, StringComparison.Ordinal)) return;
        var line = $"exec {Path.GetFileNameWithoutExtension(BindCfgName)} {AutoexecMarker}";
        AtomicWrite(path, text + (text.Length == 0 || text.EndsWith('\n') ? "" : Environment.NewLine) + line + Environment.NewLine);
    }

    private void RemoveAutoexecMarker()
    {
        var path = Path.Combine(CfgDirectory, "autoexec.cfg");
        if (!File.Exists(path)) return;
        var remaining = File.ReadAllLines(path).Where(x => !x.Contains(AutoexecMarker, StringComparison.Ordinal)).ToArray();
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
        var cs2Exe = Path.Combine(_settings.Cs2Path, "game", "bin", "win64", "cs2.exe");
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
        if (File.Exists(path) && !File.ReadAllText(path).Contains(ManagedMarker, StringComparison.Ordinal))
            throw new IOException($"文件 {Path.GetFileName(path)} 已存在但不是本工具创建，已拒绝覆盖");
    }

    private void AdoptLegacySendCfgIfKnown()
    {
        var path = Path.Combine(CfgDirectory, SendCfgName);
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        if (text.Contains(ManagedMarker, StringComparison.Ordinal)) return;
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
        if (!File.ReadAllText(path).Contains(ManagedMarker, StringComparison.Ordinal))
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
        var temporary = path + ".sb6657_write_tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static string NormalizeKey(string key) => string.IsNullOrWhiteSpace(key) ? "F8" : key.Trim().ToUpperInvariant();

    private sealed record SettingsState(
        string BoundKey,
        string SendKey,
        string? LegacyCommand,
        bool LegacyExisted,
        Dictionary<string, BindingSnapshot> Bindings,
        bool AutoexecCreated);
}
