# CS2 烂梗助手

Windows 图形化聊天助手，从 sb6657.cn 后端获取随机烂梗，并在 CS2 位于前台时模拟全局聊天输入。

> 这不是服务器插件，也不会注入或读写 CS2 进程。自动聊天仍可能受到服务器限流、静音或举报，请自行评估账号风险。

## 功能

- 图形面板显示运行、游戏、网络、倒计时和发送统计。
- 从 `/machine/dictList` 动态加载标签。
- 无标签时使用 `/machine/getRandOne`；选择标签时在 `/machine/Page` 的完整筛选结果中随机抽取。
- 当前会话内避免重复，候选全部使用后自动开启新一轮。
- 自动检测 Steam、CS2 和当前 Steam 用户，也可手动选择目录。
- 默认绑定 `F8` 执行专用 CFG，通过 CS2 原生 `say` / `say_team` 命令发送。
- 点击发送键按钮后直接按键即可换绑；换绑和删除时恢复该键原有命令。
- 自动备份用户按键配置和 `autoexec.cfg`，删除功能只处理 `sb6657_miao_*` CFG 及带标记的 autoexec 行。
- 支持接口测试、全体/队内频道、发送历史和本地配置持久化。
- `Ctrl+Shift+F10` 切换启动与暂停，关闭窗口后继续驻留托盘；热键可在面板中修改。
- Windows TLS 不可用时会自动调用本机 `python` 的 OpenSSL 通道；此回退不需要第三方 Python 包。

## 构建

安装 .NET 8 SDK 后，在仓库目录执行：

```powershell
dotnet build .\Sb6657Cs2Assistant\Sb6657Cs2Assistant.csproj -c Release
```

生成独立的 Windows x64 发布目录：

```powershell
dotnet publish .\Sb6657Cs2Assistant\Sb6657Cs2Assistant.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 使用

当前可运行版本位于 `release/Sb6657Cs2Assistant.exe`。

1. 启动程序，在左侧选择需要的标签；不选择表示从全部内容随机。
2. 确认自动检测的 Steam/CS2 路径，点击发送键按钮并按下需要的键，默认 `F8`。
3. 点击“应用按键绑定”，首次应用或换绑后重启一次 CS2，让配置稳定加载。
4. 选择全体聊天或队内聊天，再点击“启动”。程序会重写自己的发送 CFG 并触发绑定键。
5. 使用 `Ctrl+Shift+F10` 随时暂停，或从托盘菜单退出。

首次启动时会读取程序目录的默认 `appsettings.json`，用户修改后的配置保存在
`%LocalAppData%\Sb6657Cs2Assistant\appsettings.json`。第三方调用不会携带原网站前端专用的统计请求头。
