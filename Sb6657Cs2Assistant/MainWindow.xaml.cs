using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace Sb6657Cs2Assistant;

public partial class MainWindow : Window
{
    private const int HotkeyId = 6657;
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;
    private readonly MemeApiClient _api;
    private readonly GameInputService _game = new();
    private readonly ObservableCollection<MemeTag> _tags = [];
    private readonly ObservableCollection<string> _logs = [];
    private readonly ObservableCollection<string> _history = [];
    private readonly InstallationService _installation = new();
    private readonly Cs2ConfigService _config;
    private readonly KeyboardMonitorService _keyboardMonitor = new();
    private readonly HashSet<string> _sentIds = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly DispatcherTimer _timer;
    private readonly Forms.NotifyIcon _tray;
    private CancellationTokenSource _shutdown = new();
    private bool _enabled;
    private bool _exiting;
    private int _remaining;
    private int? _filteredTotal;
    private readonly Counters _counters = new();
    private ICollectionView? _tagView;
    private bool _windowHookAdded;
    private int? _activeHotkeyId;
    private uint _activeHotkeyModifiers;
    private uint _activeHotkeyKey;
    private bool _capturingSendKey;
    private string? _currentMessage;
    private string? _currentMemeId;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.Load();
        _api = new MemeApiClient(_settings.ApiBaseUrl, _settings.RequestTimeoutSeconds);
        _config = new Cs2ConfigService(_store, _settings);
        TagsList.ItemsSource = _tags;
        LogList.ItemsSource = _logs;
        HistoryList.ItemsSource = _history;
        LoadSettingsIntoUi();

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Text = "CS2 烂梗助手 - 已暂停",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _tray.DoubleClick += (_, _) => ShowPanel();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RestorePosition();
        DetectInstallation(false);
        _keyboardMonitor.SetWatchedKey(_settings.SendKey);
        _keyboardMonitor.WatchedKeyReleased += PhysicalSendKeyReleased;
        try { _keyboardMonitor.Start(); }
        catch (Exception ex) { AddLog("发送键监听启动失败：" + ex.Message); }
        if (!TryRegisterToggleHotkey())
        {
            _settings.ToggleHotkey = "Ctrl+Shift+F10";
            HotkeyBox.Text = _settings.ToggleHotkey;
            if (TryRegisterToggleHotkey())
            {
                _store.Save(_settings);
                AddLog("原热键不可用，已自动改用 Ctrl+Shift+F10");
            }
        }
        await LoadTagsAsync();
        await RunCycleAsync(false, false);
        if (_settings.StartEnabled) Start();
        AddLog("助手已启动，当前处于" + (_enabled ? "运行" : "暂停") + "状态");
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示面板", null, (_, _) => Dispatcher.Invoke(ShowPanel));
        menu.Items.Add("启动", null, (_, _) => Dispatcher.Invoke(Start));
        menu.Items.Add("暂停", null, (_, _) => Dispatcher.Invoke(Pause));
        menu.Items.Add("立即发送", null, (_, _) => Dispatcher.Invoke(() => _ = RunCycleAsync(true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        return menu;
    }

    private void ShowPanel()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        GameStatusText.Text = !_game.IsCs2Running ? "未运行" : _game.IsCs2Foreground ? "前台就绪" : "后台运行";
        if (!_enabled)
        {
            CountdownText.Text = "--";
            return;
        }

        _remaining--;
        if (_remaining <= 0)
        {
            _remaining = Math.Clamp(_settings.IntervalSeconds, 10, 3600);
            _ = RunCycleAsync(false);
        }
        CountdownText.Text = $"{_remaining} 秒";
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Start();
    private void Pause_Click(object sender, RoutedEventArgs e) => Pause();
    private void SendNow_Click(object sender, RoutedEventArgs e) => _ = RunCycleAsync(true);
    private async void RefreshTags_Click(object sender, RoutedEventArgs e) => await LoadTagsAsync();

    private void Start()
    {
        SaveSettingsFromUi();
        if (!_config.IsBindingApplied(out var bindingReason))
        {
            AddLog("无法启动发送：" + bindingReason);
            System.Windows.MessageBox.Show(
                bindingReason + "\n\n请完全退出 CS2，再点击“应用按键绑定”，成功后重新启动 CS2。",
                "发送配置未生效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        _enabled = true;
        _remaining = Math.Clamp(_settings.IntervalSeconds, 10, 3600);
        RunStatusText.Text = "运行中";
        RunStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        StartButton.IsEnabled = false;
        PauseButton.IsEnabled = true;
        _tray.Text = "CS2 烂梗助手 - 运行中";
        AddLog("自动发送已启动");
    }

    private void Pause()
    {
        _enabled = false;
        RunStatusText.Text = "已暂停";
        RunStatusText.Foreground = System.Windows.Media.Brushes.Black;
        StartButton.IsEnabled = true;
        PauseButton.IsEnabled = false;
        _tray.Text = "CS2 烂梗助手 - 已暂停";
        AddLog("自动发送已暂停");
    }

    private async Task RunCycleAsync(bool manual, bool allowSend = true)
    {
        if (!await _sendLock.WaitAsync(0))
        {
            CountSkip("上一轮尚未完成，本轮已跳过");
            return;
        }

        try
        {
            if (!allowSend)
            {
                await FetchNextMemeAsync();
                return;
            }
            if (string.IsNullOrWhiteSpace(_currentMessage) || string.IsNullOrWhiteSpace(_currentMemeId))
            {
                await FetchNextMemeAsync();
                if (string.IsNullOrWhiteSpace(_currentMessage) || string.IsNullOrWhiteSpace(_currentMemeId)) return;
            }
            if (manual && !_game.IsCs2Foreground)
            {
                AddLog("手动发送：正在激活 CS2 窗口");
                if (!_game.TryFocusCs2())
                {
                    CountSkip("找不到可激活的 CS2 窗口");
                    return;
                }
                await Task.Delay(300, _shutdown.Token);
            }
            if (!_game.IsCs2Foreground)
            {
                CountSkip($"当前 ID {_currentMemeId}，但 CS2 不在前台，未触发");
                return;
            }
            if (string.IsNullOrWhiteSpace(_settings.BoundKey) || !_settings.BoundKey.Equals(_settings.SendKey, StringComparison.OrdinalIgnoreCase))
            {
                CountFailure("发送键尚未安全写入 CS2 配置；请退出 CS2 后点击“应用按键绑定”");
                return;
            }
            _config.WriteSendCommand(_currentMessage, _settings.ChatChannel);
            var sent = await _game.TriggerBoundKeyAsync(_settings.SendKey, _shutdown.Token);
            if (!sent)
            {
                CountSkip("发送前 CS2 失去焦点");
                return;
            }

            RecordTriggeredCurrent(manual ? "手动" : "自动");
            ResetCountdown();
            await FetchNextMemeAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CountFailure(ex.Message);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task FetchNextMemeAsync()
    {
        NetworkStatusText.Text = "正在请求";
        var meme = await GetUniqueMemeAsync(_shutdown.Token);
        if (meme is null) { CountFailure("接口未返回可用烂梗"); return; }
        var message = Sanitize(_settings.ChatPrefix + meme.Barrage, _settings.MaxMessageLength);
        if (string.IsNullOrWhiteSpace(message)) { CountFailure("文本清理后为空"); return; }

        _currentMessage = message;
        _currentMemeId = meme.Id;
        CurrentMemeText.Text = message;
        CurrentMetaText.Text = $"ID: {meme.Id}    候选数量: {(_filteredTotal?.ToString() ?? "全部")}";
        NetworkStatusText.Text = "正常";
        if (_config.IsBindingApplied(out _)) _config.WriteSendCommand(message, _settings.ChatChannel);
        AddLog($"下一条已准备，ID {meme.Id}");
    }

    private void RecordTriggeredCurrent(string source)
    {
        if (string.IsNullOrWhiteSpace(_currentMessage) || string.IsNullOrWhiteSpace(_currentMemeId)) return;
        _sentIds.Add(_currentMemeId);
        _counters.Success++;
        NetworkStatusText.Text = "正常";
        AddHistory(_currentMessage, true);
        AddLog($"{source}触发 {(_settings.ChatChannel == "Team" ? "say_team" : "say")}，ID {_currentMemeId}");
        UpdateCounters();
    }

    private void ResetCountdown()
    {
        _remaining = Math.Clamp(_settings.IntervalSeconds, 10, 3600);
        CountdownText.Text = $"{_remaining} 秒";
    }

    private void PhysicalSendKeyReleased()
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (!_game.IsCs2Foreground || !_config.IsBindingApplied(out _)) return;
            if (!await _sendLock.WaitAsync(0)) return;
            try
            {
                if (string.IsNullOrWhiteSpace(_currentMessage) || string.IsNullOrWhiteSpace(_currentMemeId)) return;
                await Task.Delay(250, _shutdown.Token);
                RecordTriggeredCurrent("物理按键");
                ResetCountdown();
                await FetchNextMemeAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { CountFailure("物理发送后获取下一条失败：" + ex.Message); }
            finally { _sendLock.Release(); }
        });
    }

    private async Task<Meme?> GetUniqueMemeAsync(CancellationToken token)
    {
        var selected = _tags.Where(x => x.IsSelected).Select(x => x.DictValue).ToArray();
        if (selected.Length == 0 || selected.Length == _tags.Count)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var meme = await _api.GetRandomAsync(token);
                if (meme is null || _sentIds.Contains(meme.Id)) { _counters.Redrawn++; continue; }
                return meme;
            }
            return null;
        }

        var tagParam = string.Join(',', selected);
        if (_filteredTotal is null)
        {
            var first = await _api.GetFilteredPageAsync(tagParam, 1, token);
            _filteredTotal = first.Total;
            if (_filteredTotal <= 0)
                throw new InvalidOperationException("所选标签组合没有烂梗；多个标签要求内容同时包含这些标签");
        }
        if (_sentIds.Count >= _filteredTotal) _sentIds.Clear();

        for (var attempt = 0; attempt < Math.Min(12, Math.Max(1, _filteredTotal.Value)); attempt++)
        {
            var page = Random.Shared.Next(1, _filteredTotal.Value + 1);
            var result = await _api.GetFilteredPageAsync(tagParam, page, token);
            _filteredTotal = result.Total;
            if (result.Meme is null || _sentIds.Contains(result.Meme.Id)) { _counters.Redrawn++; continue; }
            return result.Meme;
        }
        return null;
    }

    public static string Sanitize(string value, int maxLength)
    {
        var clean = Regex.Replace(value, @"[\x00-\x1F\x7F]+", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        var limit = Math.Clamp(maxLength, 20, 500);
        return clean.Length <= limit ? clean : clean[..limit];
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            NetworkStatusText.Text = "加载标签";
            var selected = _tags.Where(x => x.IsSelected).Select(x => x.DictValue)
                .Concat(_settings.SelectedTagValues).ToHashSet();
            var tags = await _api.GetTagsAsync(_shutdown.Token);
            _tags.Clear();
            foreach (var tag in tags.OrderBy(x => x.DictLabel))
            {
                tag.IsSelected = selected.Contains(tag.DictValue);
                _tags.Add(tag);
            }
            _tagView = CollectionViewSource.GetDefaultView(_tags);
            TagsList.ItemsSource = _tagView;
            NetworkStatusText.Text = "正常";
            AddLog($"已加载 {_tags.Count} 个标签");
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = "标签失败";
            AddLog("标签加载失败：" + ex.Message);
        }
    }

    private void TagSelection_Changed(object sender, RoutedEventArgs e)
    {
        ResetFilterState();
        SaveSettingsFromUi();
    }

    private void SelectAllTags_Click(object sender, RoutedEventArgs e)
    {
        foreach (var tag in _tags) tag.IsSelected = true;
        TagsList.Items.Refresh();
        ResetFilterState();
    }

    private void ClearTags_Click(object sender, RoutedEventArgs e)
    {
        foreach (var tag in _tags) tag.IsSelected = false;
        TagsList.Items.Refresh();
        ResetFilterState();
    }

    private void ResetFilterState()
    {
        _filteredTotal = null;
        _sentIds.Clear();
        _remaining = 1;
        CurrentMetaText.Text = "ID: --    候选数量: 待刷新";
    }

    private void TagSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_tagView is null) return;
        var text = TagSearchBox.Text.Trim();
        _tagView.Filter = item => item is MemeTag tag && (text.Length == 0 || tag.DictLabel.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private void Settings_LostFocus(object sender, RoutedEventArgs e)
    {
        var oldHotkey = _settings.ToggleHotkey;
        SaveSettingsFromUi();
        if (IsLoaded && !oldHotkey.Equals(_settings.ToggleHotkey, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryRegisterToggleHotkey())
            {
                _settings.ToggleHotkey = oldHotkey;
                HotkeyBox.Text = oldHotkey;
                _store.Save(_settings);
            }
        }
    }

    private void LoadSettingsIntoUi()
    {
        IntervalBox.Text = _settings.IntervalSeconds.ToString();
        TimeoutBox.Text = _settings.RequestTimeoutSeconds.ToString();
        CaptureKeyButton.Content = _settings.SendKey;
        HotkeyBox.Text = _settings.ToggleHotkey;
        PrefixBox.Text = _settings.ChatPrefix;
        MaxLengthBox.Text = _settings.MaxMessageLength.ToString();
        SteamPathBox.Text = _settings.SteamPath;
        Cs2PathBox.Text = _settings.Cs2Path;
        ChannelBox.SelectedIndex = _settings.ChatChannel == "Team" ? 1 : 0;
        foreach (var item in _settings.SendHistory.OrderByDescending(x => x.Time).Take(100))
            _history.Add($"{item.Time:MM-dd HH:mm:ss}  [{(item.Channel == "Team" ? "队内" : "全体")}]  {item.Text}");
    }

    private void SaveSettingsFromUi()
    {
        if (int.TryParse(IntervalBox.Text, out var interval)) _settings.IntervalSeconds = Math.Clamp(interval, 10, 3600);
        IntervalBox.Text = _settings.IntervalSeconds.ToString();
        if (int.TryParse(TimeoutBox.Text, out var timeout)) _settings.RequestTimeoutSeconds = Math.Clamp(timeout, 1, 60);
        if (int.TryParse(MaxLengthBox.Text, out var max)) _settings.MaxMessageLength = Math.Clamp(max, 20, 500);
        _settings.SendKey = CaptureKeyButton.Content?.ToString() ?? "F8";
        _settings.ToggleHotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? "Ctrl+Shift+F10" : HotkeyBox.Text.Trim();
        _settings.ChatPrefix = PrefixBox.Text;
        _settings.SelectedTagValues = _tags.Where(x => x.IsSelected).Select(x => x.DictValue).ToList();
        _api.Configure(_settings.ApiBaseUrl, _settings.RequestTimeoutSeconds);
        try { _store.Save(_settings); } catch (Exception ex) { AddLog("保存配置失败：" + ex.Message); }
    }

    private void RestorePosition()
    {
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top)
        {
            Left = left;
            Top = top;
        }
    }

    private void CountSkip(string message) { _counters.Skipped++; NetworkStatusText.Text = "等待"; AddLog(message); UpdateCounters(); }
    private void CountFailure(string message) { _counters.Failed++; NetworkStatusText.Text = "请求失败"; AddLog("失败：" + message); UpdateCounters(); }
    private void UpdateCounters()
    {
        SuccessText.Text = _counters.Success.ToString();
        SkippedText.Text = _counters.Skipped.ToString();
        FailedText.Text = _counters.Failed.ToString();
        RedrawnText.Text = _counters.Redrawn.ToString();
    }

    private void AddLog(string text)
    {
        _logs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (_logs.Count > 200) _logs.RemoveAt(_logs.Count - 1);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _logs.Clear();

    private void AddHistory(string text, bool triggered)
    {
        var entry = new SendHistory(DateTime.Now, text, _settings.ChatChannel, triggered);
        _settings.SendHistory.Insert(0, entry);
        if (_settings.SendHistory.Count > 100) _settings.SendHistory.RemoveRange(100, _settings.SendHistory.Count - 100);
        _history.Insert(0, $"{entry.Time:MM-dd HH:mm:ss}  [{(entry.Channel == "Team" ? "队内" : "全体")}]  {entry.Text}");
        while (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
        _store.Save(_settings);
    }

    private void CaptureKey_Click(object sender, RoutedEventArgs e)
    {
        _capturingSendKey = true;
        CaptureKeyButton.Content = "请按键...";
        Keyboard.Focus(this);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturingSendKey) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        var name = key.ToString().ToUpperInvariant();
        _settings.SendKey = name;
        CaptureKeyButton.Content = name;
        _keyboardMonitor.SetWatchedKey(name);
        _capturingSendKey = false;
        SaveSettingsFromUi();
        AddLog($"发送键已选择为 {name}，点击“应用按键绑定”写入 CS2 配置");
        e.Handled = true;
    }

    private void Channel_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ChannelBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        _settings.ChatChannel = item.Tag?.ToString() ?? "All";
        SaveSettingsFromUi();
        if (!string.IsNullOrWhiteSpace(_currentMessage) && _config.IsBindingApplied(out _))
        {
            try { _config.WriteSendCommand(_currentMessage, _settings.ChatChannel); }
            catch (Exception ex) { AddLog("更新聊天频道失败：" + ex.Message); }
        }
    }

    private void DetectInstall_Click(object sender, RoutedEventArgs e) => DetectInstallation(true);

    private void DetectInstallation(bool notify)
    {
        var info = _installation.Detect();
        if (info is null) { if (notify) AddLog("未自动检测到 Steam/CS2，请手动选择"); return; }
        _settings.SteamPath = info.SteamPath;
        _settings.Cs2Path = info.Cs2Path;
        _settings.SteamUserId = info.SteamUserId;
        SteamPathBox.Text = info.SteamPath;
        Cs2PathBox.Text = info.Cs2Path;
        _store.Save(_settings);
        if (notify) AddLog($"已检测 CS2，Steam 用户 {_settings.SteamUserId}");
    }

    private void BrowseSteam_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择 Steam 安装目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _settings.SteamPath = dialog.SelectedPath;
        _settings.Cs2Path = _installation.DetectCs2Path(dialog.SelectedPath) ?? _settings.Cs2Path;
        _settings.SteamUserId = _installation.DetectSteamUser(dialog.SelectedPath) ?? _settings.SteamUserId;
        SteamPathBox.Text = _settings.SteamPath; Cs2PathBox.Text = _settings.Cs2Path; _store.Save(_settings);
    }

    private void BrowseCs2_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择 Counter-Strike Global Offensive 根目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _settings.Cs2Path = dialog.SelectedPath;
        Cs2PathBox.Text = _settings.Cs2Path;
        _store.Save(_settings);
    }

    private void ApplyBinding_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _config.ApplyBinding(_settings.SendKey);
            _keyboardMonitor.SetWatchedKey(_settings.SendKey);
            if (!string.IsNullOrWhiteSpace(_currentMessage)) _config.WriteSendCommand(_currentMessage, _settings.ChatChannel);
            AddLog($"已绑定 {_settings.SendKey} -> exec {Cs2ConfigService.SendCfgName}");
            System.Windows.MessageBox.Show("按键配置写入成功。现在可以启动 CS2，再点击“启动”。", "配置成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            CountFailure("应用绑定失败：" + ex.Message);
            System.Windows.MessageBox.Show(ex.Message, "配置写入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TestApi_Click(object sender, RoutedEventArgs e)
    {
        try { var meme = await _api.GetRandomAsync(_shutdown.Token); AddLog(meme is null ? "接口响应为空" : $"接口正常：ID {meme.Id}"); }
        catch (Exception ex) { AddLog("接口测试失败：" + ex.Message); }
    }

    private void RemoveCfg_Click(object sender, RoutedEventArgs e)
    {
        try { _config.RemoveCreatedConfiguration(); AddLog("已恢复原按键并删除本工具创建的 CFG"); }
        catch (Exception ex) { CountFailure("删除 CFG 失败：" + ex.Message); }
    }

    private bool TryRegisterToggleHotkey()
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (!_windowHookAdded)
        {
            source.AddHook(WndProc);
            _windowHookAdded = true;
        }
        var parts = _settings.ToggleHotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        uint modifiers = 0;
        uint key = 0;
        foreach (var part in parts)
        {
            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0001;
            else if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0002;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0004;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0008;
            else if (Enum.TryParse<Forms.Keys>(part, true, out var parsed) && parsed != Forms.Keys.None) key = (uint)parsed;
        }
        if (modifiers == 0 || key == 0)
        {
            AddLog("热键格式无效，请使用 Ctrl+Alt+M 这样的格式");
            return false;
        }

        if (_activeHotkeyId is not null && modifiers == _activeHotkeyModifiers && key == _activeHotkeyKey)
        {
            HotkeyHintText.Text = $"热键 {_settings.ToggleHotkey} 可切换启停；只有 CS2 位于前台时才发送。";
            return true;
        }

        var candidateId = _activeHotkeyId == HotkeyId ? HotkeyId + 1 : HotkeyId;
        if (!RegisterHotKey(source.Handle, candidateId, modifiers, key))
        {
            AddLog("启停热键注册失败，可能已被其他程序占用");
            return false;
        }
        if (_activeHotkeyId is int oldId) UnregisterHotKey(source.Handle, oldId);
        _activeHotkeyId = candidateId;
        _activeHotkeyModifiers = modifiers;
        _activeHotkeyKey = key;
        HotkeyHintText.Text = $"热键 {_settings.ToggleHotkey} 可切换启停；只有 CS2 位于前台时才发送。";
        AddLog($"启停热键已设为 {_settings.ToggleHotkey}");
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && _activeHotkeyId == wParam.ToInt32())
        {
            if (_enabled) Pause(); else Start();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        Hide();
        _tray.ShowBalloonTip(1500, "CS2 烂梗助手", "程序仍在托盘中运行。", Forms.ToolTipIcon.Info);
    }

    private void ExitApplication()
    {
        _exiting = true;
        try
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            SaveSettingsFromUi();
        }
        catch (Exception ex) { AddLog("退出时保存配置失败：" + ex.Message); }
        finally
        {
            _shutdown.Cancel();
            _timer.Stop();
            _keyboardMonitor.Dispose();
            if (_activeHotkeyId is int id) UnregisterHotKey(new WindowInteropHelper(this).Handle, id);
            _tray.Visible = false;
            _tray.Dispose();
            Close();
            System.Windows.Application.Current.Shutdown();
        }
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
