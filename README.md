# CS2 烂梗助手

一个面向普通 Windows 用户的 CS2 聊天辅助面板：从固定的 HTTPS 接口获取随机文本，复制到剪贴板，用户在 CS2 聊天框中自行粘贴并发送。

## 直接使用

1. 从 Release 下载 `Sb6657Cs2Assistant.exe`。
2. 双击 EXE，不需要安装 .NET、Python 或其他运行环境。
3. 等待标签和当前内容加载完成，点击“一键启动”。
4. 需要发送时，在 CS2 聊天框内按 `Ctrl+V`，再由用户自己确认发送。

程序会自动查找 Steam 和 CS2 安装目录。找不到时仍可使用剪贴板模式，也可以在窗口底部手动选择目录。

## 可选的 CS2 按键绑定

如果希望在 CS2 内按一个实体键切换下一条内容，可以先完全退出 CS2，再点击“应用按键绑定”。程序只会修改当前 Steam 用户的 CS2 按键文件和本工具自己的 CFG 文件，并在文件中写入所有权标记。

- 默认不启用全局键盘监听。
- 只有用户明确点击“应用按键绑定”后才会启用监听。
- 删除配置前会检查游戏是否已退出，并且只删除带有本工具标记的文件。
- 原始按键和 `autoexec.cfg` 会先备份，失败时会回滚。

## 安全和兼容性边界

- 不注入 CS2 进程、不读写游戏内存、不模拟键盘输入，也不修改 Steam 文件。
- 网络请求只访问内置的官方 HTTPS 地址，并限制响应大小和超时；不会调用 Python、PowerShell 或其他外部脚本。
- CFG 写入被限制在自动发现的 CS2 安装目录和当前 Steam 用户目录内。检测不到有效目录时拒绝写入。
- CS2 普通版本更新通常不会影响功能，因为程序使用公开的 CFG 和 VCFG 文件接口，并会重新发现 Steam 库与用户键位文件。若 Valve 改变文件格式，程序会停止写入并提示，而不是冒险覆盖未知文件。

这不能替代 Valve 对第三方工具的政策判断。请自行确认所在服务器和账号允许使用聊天辅助工具。

## 数据位置

用户配置、备份、发送历史和崩溃日志位于：

```text
%LocalAppData%\Sb6657Cs2Assistant
```

## 开发者发布

在仓库根目录执行：

```powershell
dotnet publish .\Sb6657Cs2Assistant\Sb6657Cs2Assistant.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=None -o .\release
```

发布目录中的 `Sb6657Cs2Assistant.exe` 是 Windows x64 自包含单文件，可直接复制到其他电脑运行。发布时不要把 `bin`、`obj`、SDK 或 `.dotnet` 目录打包给最终用户。

面向公众分发前，应使用受信任的 Authenticode 代码签名证书签署 EXE。没有证书时程序仍可运行，但 Windows SmartScreen 可能显示“未知发布者”；自签名证书不能消除该提示。
