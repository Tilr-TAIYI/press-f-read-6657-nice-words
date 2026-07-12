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
    const string vcfg = "\"config\"\n{\n\t\"bindings\"\n\t{\n\t\t\"F10\"\t\t\"cs_quit_prompt\"\n\t}\n\t\"analogbindings\"\n\t{\n\t}\n}\n";
    const string remoteVcfg = "\"config\"\n{\n\t\"bindings\"\n\t{\n\t\t\"F10\"\t\t\"remote_quit\"\n\t}\n\t\"analogbindings\"\n\t{\n\t}\n}\n";
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

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
