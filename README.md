# CS2 烂梗助手

Windows WPF 控制面板，从 sb6657 接口获取随机文本并写入剪贴板。用户在 CS2 聊天框内粘贴并发送；程序不注入 CS2 进程，也不模拟键盘输入。

## 当前功能

- 多标签 OR 筛选（满足任一标签即可）、会话去重和单条预取缓存。当前内容准备完成后，后台立即请求下一条。
- 定时复制和“立即复制”，复制后自动切换到缓存内容。
- 全体聊天/队内聊天 CFG 内容生成，以及可选的实体键监听。
- Steam、CS2 和当前 Steam 用户自动检测，也支持手动选择。
- 发送键绑定前备份当前用户配置；换绑时恢复旧命令。
- 只处理带有本工具首行所有权标记的 CFG，不覆盖或删除同名的其他文件。
- 热键启停、托盘驻留、发送历史、接口状态和错误日志。
- Windows TLS 不可用时自动尝试 Python/OpenSSL 回退；接口地址默认只允许 HTTPS。

> 这不是服务器插件，不读写 CS2 进程内存。游戏、平台和服务器可能限制聊天，使用时请自行评估账号风险。

## 构建

安装 .NET 8 SDK 后，在仓库目录执行：

```powershell
dotnet build .\Sb6657Cs2Assistant\Sb6657Cs2Assistant.csproj -c Release
```

生成独立的 Windows x64 发布版本：

```powershell
dotnet publish .\Sb6657Cs2Assistant\Sb6657Cs2Assistant.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 使用

1. 启动程序，在左侧选择标签；不选择表示从全部内容随机。
2. 点击“启动”或“立即复制”，在游戏聊天框中粘贴发送。
3. 如需实体键发送，选择发送键并点击“应用按键绑定”。应用和删除前请完全退出 CS2。
4. 通过“全体聊天/队内聊天”选择 CFG 使用的命令；`Ctrl+Shift+F10` 默认切换定时复制。

用户配置、损坏配置备份和崩溃日志位于：

```text
%LocalAppData%\Sb6657Cs2Assistant
```

CFG 写入范围仅限选定 CS2 的 `game\csgo\cfg` 和当前 Steam 用户的 CS2 按键文件。删除功能要求所有权标记位于文件首行，并保留无关 CFG。
