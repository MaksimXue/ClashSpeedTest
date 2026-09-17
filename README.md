# Clash 节点测速

> 本仓库 fork 自 [jx645879099-hub/ClashSpeedTest](https://github.com/jx645879099-hub/ClashSpeedTest)。
> 主要改动：新增 **Clash Party**（mihomo-party）客户端支持，并重构界面交互。原项目仅支持 Clash Verge Rev。

[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-2563EB)](https://github.com/MaksimXue/ClashSpeedTest/releases/latest)
[![Build](https://github.com/MaksimXue/ClashSpeedTest/actions/workflows/build.yml/badge.svg)](https://github.com/MaksimXue/ClashSpeedTest/actions/workflows/build.yml)

面向 Windows 的 Clash 节点**真实下载速度**批量测速工具（C# / .NET 8 + WPF 原生桌面应用）。

Clash 自带的"延迟测试"只能看 ping，测不出节点能不能下载。本工具逐个切换节点，
真实下载测速文件，测出每个节点的【延迟】和【真实下载速度】，并按速度排行。

## 功能

- **真实下载测速**：切换节点 → 通过本地代理下载 Cloudflare 50MB 测速文件 → 测出真实下载速度（MB/s / Mbps）
- **两阶段测速（省时间）**：先用 Clash 内核批量延迟检测（`/group/xxx/delay`，几秒出全部结果）→ error 节点自动标记跳过 → 只对可用节点按延迟从低到高逐个下载测速
- **手动重测**：延迟 error 的节点保留"重测"按钮，可对单个节点强制完整重测
- **机场识别**：自动读取 Clash 客户端订阅（Clash Party 的 `profile.yaml` / Clash Verge Rev 的 `profiles.yaml`），顶部显示当前激活的机场和订阅数；在 Clash 切换订阅后点"重新检测"即可刷新
- **节点勾选**：点击列表条目即可切换该节点是否参与测速（左侧色条绿色=检测，灰色=跳过），支持只测延迟模式
- **结果排行**：按下载速度排序，高亮最快节点，一键复制节点名
- **导出 CSV**：结果可导出（UTF-8，Excel 可直接打开）
- **自动恢复**：测速期间临时切换节点，测完全部自动恢复你原来的节点
- **M9A 风格界面**：浅色卡片、圆角、状态色块

## 使用

1. 从 [Releases](https://github.com/MaksimXue/ClashSpeedTest/releases/latest) 下载 ZIP 完整包（已含 .NET 运行时，任何 Win10/11 双击即用）
2. 解压，双击 `ClashSpeedTest.exe`
3. 保持 **Clash Party** 或 **Clash Verge Rev** 正常运行（自动通过命名管道对接，无需配置）
4. 点击【开始测速】即可

> 支持 **Clash Party**（原 Mihomo Party）与 **Clash Verge Rev**（均 Windows 版），自动识别内核命名管道与订阅。

## 从源码构建

需要 Windows + .NET 8 SDK：

```powershell
dotnet build .\ClashSpeedTest.csproj -c Release
dotnet publish .\ClashSpeedTest.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

仓库不提交编译产物（bin/obj/dist）。发版只需推送一个 `v` 开头的标签，GitHub Actions 会自动构建、打包并发布到 Releases：

```bash
git tag v1.1.0
git push origin v1.1.0
```

推送到 `main` 分支只会构建并上传构建产物（artifact），不会创建 Release。

## 技术要点

- 通过 **Windows 命名管道** 对接 Clash 内核：优先 `\\.\pipe\MihomoParty\mihomo`（Clash Party），再 `\\.\pipe\verge-mihomo`（Clash Verge Rev），失败回退 TCP 9097/9090，零配置
- 批量延迟接口 `GET /group/{组}/delay`（内核并发测），失败回退并发单节点测
- 下载测速前先"热身"1MB（Hysteria2/QUIC 连接提速），再正式计时
- 测速文件用 Cloudflare 官方 50MB（可在界面自定义测速源 URL）
