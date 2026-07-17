using Sb6657Cs2Assistant;

var root = Path.Combine(Path.GetTempPath(), "sb6657-config-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    var steam = Path.Combine(root, "Steam");
    var cs2 = Path.Combine(root, "Counter-Strike Global Offensive");
    var local = Path.Combine(steam, "userdata", "123", "730", "local", "cfg", "cs2_user_keys_0_slot0.vcfg");
    var remote = Path.Combine(steam, "userdata", "123", "730", "remote", "cs2_user_keys.vcfg");
    var cfg = Path.Combine(cs2, "game", "csgo", "cfg");
    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
    Directory.CreateDirectory(Path.GetDirectoryName(remote)!);
    Directory.CreateDirectory(cfg);
    Directory.CreateDirectory(Path.Combine(cs2, "game", "bin", "win64"));
    File.WriteAllText(Path.Combine(cs2, "game", "bin", "win64", "cs2.exe"), "test");
    const string vcfg = "\"config\"\n{\n\t\"bindings\"\n\t{\n\t\t\"T\"\t\t\"toggleradarscale\"\n\t\t\"F7\"\t\t\"load quick\"\n\t\t\"F10\"\t\t\"cs_quit_prompt\"\n\t}\n\t\"analogbindings\"\n\t{\n\t}\n}\n";
    const string remoteVcfg = "\"config\"\n{\n\t\"bindings\"\n\t{\n\t\t\"T\"\t\t\"toggleradarscale\"\n\t\t\"F7\"\t\t\"load quick\"\n\t\t\"F10\"\t\t\"remote_quit\"\n\t}\n\t\"analogbindings\"\n\t{\n\t}\n}\n";
    File.WriteAllText(local, vcfg);
    File.WriteAllText(remote, remoteVcfg);
    File.WriteAllText(Path.Combine(cfg, "autoexec.cfg"), "echo keep\n");
    File.WriteAllText(Path.Combine(cfg, "keep.cfg"), "echo unrelated\n");

    var settings = new AppSettings { SteamPath = steam, Cs2Path = cs2, SteamUserId = "123" };
    var store = new SettingsStore(Path.Combine(root, "settings"));
    var service = new Cs2ConfigService(store, settings, enforceGameStopped: false);

    service.ApplyBinding("F8");
    Assert(service.IsBindingApplied(out _), "binding status did not report applied");
    Assert(File.ReadAllText(local).Contains("\"F8\"\t\t\"exec sb6657_miao_send\""), "F8 binding missing");
    service.WriteSendCommand("hello", "All");
    Assert(File.ReadAllText(Path.Combine(cfg, Cs2ConfigService.SendCfgName)).Contains("say \"hello\""), "say command missing");

    File.WriteAllText(Path.Combine(cfg, Cs2ConfigService.SendCfgName), "echo foreign same-name file\n");
    var removeRefused = false;
    try { service.RemoveCreatedConfiguration(); } catch (IOException) { removeRefused = true; }
    Assert(removeRefused, "removal did not refuse unowned send cfg");
    Assert(File.ReadAllText(local).Contains("\"F8\"\t\t\"exec sb6657_miao_send\""), "failed removal did not roll back binding");
    Assert(File.Exists(Path.Combine(cfg, Cs2ConfigService.BindCfgName)), "failed removal did not roll back bind cfg");
    Assert(File.ReadAllText(Path.Combine(cfg, Cs2ConfigService.SendCfgName)).Contains("foreign"), "unowned send cfg was changed");
    File.WriteAllText(Path.Combine(cfg, Cs2ConfigService.SendCfgName), "// SB6657_MIAO_MANAGED\nsay \"hello\"\n");

    service.ApplyBinding("F10");
    Assert(!File.ReadAllText(local).Contains("\"F8\""), "F8 was not restored");
    service.WriteSendCommand("team", "Team");
    Assert(File.ReadAllText(Path.Combine(cfg, Cs2ConfigService.SendCfgName)).Contains("say_team \"team\""), "say_team command missing");

    service.RemoveCreatedConfiguration();
    var restored = File.ReadAllText(local);
    Assert(restored.Contains("\"F10\"\t\t\"cs_quit_prompt\""), "F10 original command not restored");
    Assert(File.ReadAllText(remote).Contains("\"F10\"\t\t\"remote_quit\""), "remote F10 original command not restored");
    Assert(!File.Exists(Path.Combine(cfg, Cs2ConfigService.BindCfgName)), "bind cfg not removed");
    Assert(!File.Exists(Path.Combine(cfg, Cs2ConfigService.SendCfgName)), "send cfg not removed");
    Assert(File.Exists(Path.Combine(cfg, "keep.cfg")), "unrelated cfg was removed");
    Assert(File.ReadAllText(Path.Combine(cfg, "autoexec.cfg")).Contains("echo keep"), "autoexec content damaged");

    File.Delete(Path.Combine(cfg, "autoexec.cfg"));
    settings.SendHistory.Add(new SendHistory(DateTime.Now, "legacy", "All", true));
    File.WriteAllText(Path.Combine(cfg, Cs2ConfigService.SendCfgName), "say \"legacy\"\n");
    service.ApplyBinding("F8");
    Assert(File.ReadAllText(Path.Combine(cfg, Cs2ConfigService.SendCfgName)).Contains("SB6657_MIAO_MANAGED"), "legacy send cfg was not safely adopted");
    service.RemoveCreatedConfiguration();
    Assert(!File.Exists(Path.Combine(cfg, "autoexec.cfg")), "tool-created autoexec was not removed");
    Assert(!service.IsBindingApplied(out _), "binding status remained applied after removal");

    service.ApplyBinding("F7");
    settings.BoundKey = "T";
    settings.SendKey = "T";
    settings.OriginalBindings = new Dictionary<string, BindingSnapshot>(StringComparer.OrdinalIgnoreCase)
    {
        [Path.GetFullPath(local)] = new(true, "toggleradarscale"),
        [Path.GetFullPath(remote)] = new(true, "toggleradarscale")
    };
    service.ApplyBinding("T");
    Assert(File.ReadAllText(local).Contains("\"F7\"\t\t\"load quick\""), "orphaned F7 binding was not restored from initial backup");
    Assert(File.ReadAllText(local).Contains("\"T\"\t\t\"exec sb6657_miao_send\""), "T binding was not applied after reconciliation");
    service.RemoveCreatedConfiguration();
    Assert(File.ReadAllText(local).Contains("\"T\"\t\t\"toggleradarscale\""), "T original binding was not restored after reconciliation");

    var invalidKey = false;
    try { service.ApplyBinding("F8\""); } catch (ArgumentException) { invalidKey = true; }
    Assert(invalidKey, "invalid key text was accepted");

    File.WriteAllText(Path.Combine(cfg, Cs2ConfigService.BindCfgName), "echo // SB6657_MIAO_MANAGED\nbind \"F8\" \"foreign\"\n");
    var fakeMarkerRefused = false;
    try { service.ApplyBinding("F9"); } catch (IOException) { fakeMarkerRefused = true; }
    Assert(fakeMarkerRefused, "marker not on the first line was treated as ownership");
    File.Delete(Path.Combine(cfg, Cs2ConfigService.BindCfgName));

    var brokenStore = new SettingsStore(Path.Combine(root, "broken-settings"));
    Directory.CreateDirectory(brokenStore.DirectoryPath);
    File.WriteAllText(brokenStore.FilePath, "{ invalid json");
    _ = brokenStore.Load();
    Assert(!string.IsNullOrWhiteSpace(brokenStore.LastLoadError), "broken settings did not expose a load error");
    Assert(Directory.GetFiles(brokenStore.DirectoryPath, "appsettings.json.corrupt.*").Length == 1, "broken settings were not backed up");

    File.WriteAllText(Path.Combine(cfg, Cs2ConfigService.BindCfgName), "echo unrelated same name\n");
    var refused = false;
    try { service.ApplyBinding("F8"); } catch (IOException) { refused = true; }
    Assert(refused, "unowned same-name cfg was not refused");
    Assert(File.ReadAllText(Path.Combine(cfg, Cs2ConfigService.BindCfgName)).Contains("unrelated"), "unowned cfg was overwritten");
    Console.WriteLine("config-smoke: PASS");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

if (Environment.GetEnvironmentVariable("SB6657_NETWORK_SMOKE") == "1")
{
    using var client = new MemeApiClient("https://hguofichp.cn:10086", 10);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var tags = await client.GetTagsAsync(timeout.Token);
    Assert(tags.Count > 0, "network smoke returned no tags");
    for (var i = 0; i < 5; i++)
    {
        var meme = await client.GetRandomAsync(timeout.Token);
        Assert(meme is not null && !string.IsNullOrWhiteSpace(meme.Barrage), "network smoke returned no meme");
    }
    var selected = tags.Take(2).ToArray();
    var queue = new MemeQueueService(client);
    queue.Configure(tags, selected.Select(x => x.DictValue), string.Empty, 220);
    var filtered = await queue.GetNextAsync(timeout.Token);
    if (filtered is null) throw new InvalidOperationException("OR tag smoke returned no meme");
    var resultTags = filtered.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    Assert(selected.Any(x => resultTags.Contains(x.DictValue, StringComparer.OrdinalIgnoreCase)), "OR tag smoke did not match any selected tag");
    timeout.Cancel();
    Console.WriteLine("network-smoke: PASS");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
