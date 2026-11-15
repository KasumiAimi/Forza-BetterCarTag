# FH6 车库编辑器

《极限竞速：地平线 6》的中文 Windows 车库工具。自动读取本机存档和车辆缩略图，为每台车设置赛事类型与性能倾向，并在游戏的原生车辆卡片上显示彩色标签。

**[下载源码 ZIP](https://github.com/KasumiAimi/Forza-better-cartage/archive/refs/heads/main.zip)** · [在 Windows 上构建](#在-windows-上构建)

当前提供源码下载。现有的 Windows EXE 包含 Forza Mod Tool 的运行库，等待确认分发授权后再发布。

## 使用方法

1. 打开 `FH6GarageEditor.exe`，程序会自动查找本机存档并列出车库内的车辆。检测到多个档案时可以在右上角切换。自动查找不到时，可手动选择整份 `C_ProfileData` 文件。
2. 使用制造商、车辆等级、车型搜索或「仅显示收藏」找到车辆。游戏内车库或收藏有变动时，点击「刷新车库」同步。
3. 选中车卡，在左栏设置「赛事类型」和「性能倾向」。标签立即保存在本机。选择「未标记」可清除对应类别。同车型、同等级和同 PI 的多辆车可分别标记。
4. 退出游戏，点击「应用到游戏」，然后重新启动 FH6。已标记的车辆会在原生车库和赛前选车卡片上显示标签。编辑标签和标签管理在这个 Windows 工具中进行。

| 标签 | 可选项 |
| --- | --- |
| 赛事类型 | 公路、拉力、越野、直线加速、漂移 |
| 性能倾向 | 操控、均衡、动力 |

缩略图优先使用本机车库缓存。缺少缓存时会显示原厂车型图，外观可能与玩家当前使用的涂装不同。

## 存档与游戏文件

- 标签保存在 `%LOCALAPPDATA%\FH6GarageEditor\tags`。工具读取游戏存档和收藏。
- 「应用到游戏」从原版 `media\UI.zip` 读取车卡模板并在 `mediapc\UI\Resources\Anthem\Shared_CarSelect.xaml` 生成覆盖文件。退出游戏后移走本工具生成的覆盖文件即可撤下游戏内标签。
- 自动定位及游戏内覆盖已在 Windows Steam 版验证。游戏更新后，模板变动可能需要下一次适配。

## 在 Windows 上构建

需要 Windows x64、Git 和 [.NET 8 SDK](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)。下载并解压源码后，在项目根目录打开 PowerShell，执行以下命令：

```powershell
git clone --depth 1 --branch ForzaModToolV1.3.0.6 https://github.com/HTTPJXRDN/Forza-Mod-Tool.git .deps/Forza-Mod-Tool
git -C .deps/Forza-Mod-Tool rev-parse HEAD
dotnet publish Editor/FH6GarageEditor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

第二条命令应显示 `09cf655d964942346cc31995cd322062904bf893`。如果已经下载过该版本，可从第二条命令开始。构建后双击 `dist\FH6GarageEditor.exe` 启动。

## 致谢

存档与缩略图读取使用 [Forza Mod Tool](https://github.com/HTTPJXRDN/Forza-Mod-Tool) 的组件。构建时从上游获取这些依赖。

本项目是玩家制作的非官方工具。

