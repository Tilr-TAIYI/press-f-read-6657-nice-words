using Sb6657Cs2Assistant;

var root = Path.Combine(Path.GetTempPath(), "sb6657-config-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    var steam = Path.Combine(root, "Steam");
    var cs2 = Path.Combine(root, "Counter-Strike Global Offensive");
    var local = Path.Combine(steam, "userdata", "123", "730", "local", "cfg", "cs2_user_keys_0_slot0.vcfg");
    var inactiveLocal = Path.Combine(steam, "userdata", "123", "730", "local", "cfg", "cs2_user_keys_0_slot1.vcfg");
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
    File.WriteAllText(inactiveLocal, vcfg);
    File.WriteAllText(remote, remoteVcfg);
    File.WriteAllText(Path.Combine(cfg, "autoexec.cfg"), "echo keep\n");
    File.WriteAllText(Path.Combine(cfg, "keep.cfg"), "echo unrelated\n");

    var settings = new AppSettings { SteamPath = steam, Cs2Path = cs2, SteamUserId = "123" };
    var store = new SettingsStore(Path.Combine(root, "settings"));
    var service = new Cs2ConfigService(store, settings, enforceGameStopped: false);

    service.ApplyBinding("F8");
    Assert(service.IsBindingApplied(out _), "binding status did not report applied");
    Assert(File.ReadAllText(local).Contains("\"F8\"\t\t\"exec sb6657_miao_send\""), "F8 binding missing");
    Assert(!File.ReadAllText(inactiveLocal).Contains("\"F8\"\t\t\"exec sb6657_miao_send\""), "inactive key slot was modified");
    Assert(File.ReadAllText(remote).Contains("\"F8\"\t\t\"exec sb6657_miao_send\""), "remote F8 binding missing");
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
    Assert(File.ReadAllText(remote).Contains("\"T\"\t\t\"exec sb6657_miao_send\""), "remote T binding was not applied");
    Assert(File.ReadAllText(Path.Combine(cfg, Cs2ConfigService.BindCfgName)).Contains("bind \"T\" \"exec sb6657_miao_send\""), "T bind cfg command missing");
    service.WriteSendCommand("t-auto-send", "All");
    Assert(File.ReadAllText(Path.Combine(cfg, Cs2ConfigService.SendCfgName)).Contains("say \"t-auto-send\""), "T send cfg content missing");
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

    var library = Path.Combine(root, "CustomLibrary");
    var detectedCs2 = Path.Combine(library, "steamapps", "common", "CS2-Custom-Install-Name");
    Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
    Directory.CreateDirectory(Path.Combine(detectedCs2, "game", "bin", "win64"));
    File.WriteAllText(Path.Combine(detectedCs2, "game", "bin", "win64", "cs2.exe"), "test");
    File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
        $"\"libraryfolders\"\n{{\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{library.Replace("\\", "\\\\")}\"\n\t}}\n}}\n");
    File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_730.acf"),
        "\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"installdir\"\t\t\"CS2-Custom-Install-Name\"\n}\n");
    var installation = new InstallationService();
    Assert(installation.DetectCs2Path(steam) == Path.GetFullPath(detectedCs2), "appmanifest-based CS2 detection failed");
    var saved = installation.ResolveSaved(steam, cs2, "123");
    Assert(saved?.Cs2Path == Path.GetFullPath(cs2), "valid saved CS2 path was not preserved");
    Assert(saved?.SteamUserId == "123", "valid saved Steam user was not preserved");
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

if (Environment.GetEnvironmentVariable("SB6657_INSTALLATION_SMOKE") == "1")
{
    var detected = new InstallationService().Detect();
    Assert(detected is not null, "installed Steam/CS2 was not detected");
    Assert(File.Exists(Path.Combine(detected!.Cs2Path, "game", "bin", "win64", "cs2.exe")), "detected CS2 path has no executable");
    Assert(Directory.Exists(Path.Combine(detected.SteamPath, "userdata", detected.SteamUserId, "730")), "detected Steam user has no CS2 data");
    Console.WriteLine($"installation-smoke: PASS ({detected.Cs2Path}, user {detected.SteamUserId})");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
