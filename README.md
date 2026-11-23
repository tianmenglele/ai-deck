<div align="center">

<img src="docs/images/banner.png" alt="AIDeck Banner" width="100%" />

<h1>AIDeck · 多 AI 协同中心</h1>

<p>面向 Windows 多工程开发的多 AI 协同调度、特性路由与桌面悬浮监控中心（.NET 8 + WPF 单文件绿色发布）</p>

<p>
  <a href="https://github.com/tianmenglele/ai-deck/releases"><img src="https://img.shields.io/badge/Version-v1.9.15-blue" alt="Version v1.9.15" /></a>
  <img src="https://img.shields.io/badge/Platform-Windows_10%2F11_x64-0078D6" alt="Platform Windows 10/11 x64" />
  <img src="https://img.shields.io/badge/Framework-.NET_8_%2B_WPF-512BD4" alt="Framework .NET 8 + WPF" />
  <img src="https://img.shields.io/badge/Self--Test-364%2F364_PASS-brightgreen" alt="Self-Test 364/364 PASS" />
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-yellow" alt="License MIT" /></a>
</p>

<p>
  <strong>⬇ <a href="https://github.com/tianmenglele/ai-deck/releases/latest">直接下载开箱即用版</a></strong>
  · 独立免装单文件
  <code>AIDeck-v1.9.15-win-x64.exe</code>
  · 绿色便携包
  <code>AIDeck-v1.9.15-win-x64-portable.zip</code>
</p>

<p>
  <a href="#核心特性">核心特性</a>
  ·
  <a href="#实机预览">实机预览</a>
  ·
  <a href="#协同架构">协同架构</a>
  ·
  <a href="#快速开始">快速开始</a>
  ·
  <a href="#开放扩展">开放扩展</a>
  ·
  <a href="#借鉴学习与开源致谢">借鉴与致谢</a>
  ·
  <a href="#致谢与协议">致谢与协议</a>
</p>

</div>

<a id="核心特性"></a>

## 核心特性

当前版本 **v1.9.15**。主脑负责拆解、派单与终验；执行端留在各自桌面会话里完成后台改动，悬浮窗只同步状态，不抢当前焦点。

| 模块 | 说明 |
| --- | --- |
| 主脑统筹与严格验收闭环 | Antigravity 主脑完成分析、派单，以及 Diff、编译与 `--self-test` 终验。不达标自动打回同一会话重修，最多 3 轮。 |
| 全 AI（含 Grok Bot）纯后台零抢前台 | `StrictNoForegroundStealOnDispatch = true`，`AllowHeadlessCliFallbackForDesktopAgents = false`。进程内轻量 UIA 与键鼠静默门控，桌面工具不降级到看不见的无头 CLI，也不打断当前输入。 |
| 桌面置顶悬浮窗全形态锁定 | 折叠胶囊、极简单行、紧凑双行、完整展开。手动切换后永久锁定，任务开始、完成或异常刷新都不会自动弹出抢屏。 |
| 毫秒/秒级实时状态同步与跨工程防串扰 | 主界面与悬浮窗按毫秒级探测、秒级刷新各 AI 阶段；回执按工程隔离，不串到其他项目。 |
| 飞行任务单显示具体任务名 | 跨工具派单动画胶囊实时展示提炼后的任务标题，而不是笼统的「处理中」。 |
| 版本号展示与自主检查更新 | 顶栏版本按钮显示 `v1.9.15`。启动后可对 [Releases](https://github.com/tianmenglele/ai-deck/releases) 做后台静默检查，检查动作带防重入锁。 |
| Magpie 风格中心辐射工作流脑图 | 工作流以中心辐射拓扑展示主脑与执行端分工，阶段关系一眼可读。 |
| 3 步首次使用新手向导 | 首次打开按 3 步引导完成上手，之后也可从顶栏再次进入。 |
| 全站 Crisp 矢量图标与 4 分页独立设置页 | 全站使用 Crisp 矢量图标；设置拆成 4 个独立分页，各自维护互不挤占。 |
| 折叠胶囊自适应磨砂完成气泡 | 标题与正文分离，悬停暂停倒计时，留屏时长可在 4 到 30 秒之间设置，宽度随文案自适应。 |
| 默认窗口零纵向滚动紧凑布局 | 默认窗口在常规高度下，主界面无需纵向滚动即可浏览。 |
| 悬浮窗防闪烁非对称去抖与丝滑自适应飞行动画 | 状态切换用非对称去抖抑制闪烁，派单飞行动画按路径自适应。 |
| 内置官方 GitHub 仓库自动更新源 | 默认指向官方仓库，启动后自动后台检查，无需手填更新源。 |
| DOLLARS 风格电钢柔和提示旋律 | 5 路 CC0-1.0 柔和提示音轨，支持完整、轻量、静音三档，并有独立静音闸门。 |
| 单一事实源规则技能零拷贝同步 | 项目内只保留一份规则、技能与经验库真身，同步到各已启用工具；停用即剥离，不在每个工具里各存一份副本。 |

<a id="实机预览"></a>

## 实机预览

### 1. 协同工作台

五步动态工作流运行图、在岗 AI 成员与最新协同回执总览。

<img src="docs/images/01-workbench.png" alt="协同工作台" width="100%" />

### 2. 桌面置顶悬浮窗（四形态全锁与实时状态监控）

支持完整展开 / 紧凑双行 / 极简单行 / 收起胶囊四态，以及手切永久锁定与实时透明度调节；各 AI 工作阶段毫秒～秒级同步，不抢前台焦点。

<img src="docs/images/06-floating-hud.png" alt="桌面置顶悬浮窗四形态与实时状态监控" width="100%" />

### 3. AI 阵容与特性路由

多 AI 工具自动探测、模型档位配置与能力星级路由矩阵。

<img src="docs/images/02-roster-routing.png" alt="AI 阵容与特性路由" width="100%" />

### 4. 规则与技能单一事实源

共享技能、项目规则与经验库多工具生效状态及实时预览。

<img src="docs/images/03-rules-skills.png" alt="规则与技能单一事实源" width="100%" />

### 5. 跨工具对话与自动派单

统一管理各 AI 历史会话、指令/回复气泡与智能派单入口。

<img src="docs/images/04-dialogs-dispatch.png" alt="跨工具对话与自动派单" width="100%" />

### 6. 代码快照保护与协同任务单

改动 Diff 快照基准、一键回滚与协同任务活卡管理。

<img src="docs/images/05-snapshots-task.png" alt="代码快照保护与协同任务单" width="100%" />

### 7. 磨砂完成气泡通知与自定义留屏时长

折叠胶囊完成时弹出磨砂气泡：标题与正文分离，悬停暂停，留屏时长可在 4 到 30 秒之间设置。

<img src="docs/images/07-completion-bubble.png" alt="磨砂完成气泡通知与自定义留屏时长" width="100%" />

### 8. 3 步首次使用新手向导

首次打开按 3 步引导完成上手，之后也可从顶栏再次进入。

<img src="docs/images/08-onboarding-wizard.png" alt="3 步首次使用新手向导" width="100%" />

<a id="协同架构"></a>

## 协同架构

需求从入口进入主脑。主脑拆解后按特性选档，多个 AI 在后台并发执行。终验看 Diff、编译和自检：达标即交付，未达标则打回精修，最多 3 轮后回到执行。

```mermaid
flowchart TD
    A["需求入口"] --> B["主脑统筹拆解"]
    B --> C["特性路由与自动选档"]
    C --> D["多 AI 纯后台并发执行"]
    D --> E["主脑 Diff / 编译 / 自检终验"]
    E --> F{"达标交付 / 打回精修 (<=3 轮)"}
    F -->|"达标"| G["达标交付"]
    F -->|"未达标"| H["打回精修 (<=3 轮)"]
    H --> D
```

<a id="快速开始"></a>

## 快速开始

### 直接下载开箱即用版（推荐普通用户）

无需克隆仓库、不必本机安装 .NET SDK。打开最新 Release 页即可下载：

**[https://github.com/tianmenglele/ai-deck/releases/latest](https://github.com/tianmenglele/ai-deck/releases/latest)**

| 资源 | 说明 |
| --- | --- |
| `AIDeck-v1.9.15-win-x64.exe` | **独立免装运行时单文件版**（自包含 .NET 8，双击即可运行） |
| `AIDeck-v1.9.15-win-x64-portable.zip` | **绿色便携压缩包**（含主程序、`registry/`、`plugins/`、启动脚本与说明） |
| `AIDeck-v1.9.15-win-x64-lite.zip` | 轻量版（需本机已安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)） |
| `SHA256SUMS.txt` | 上述产物的 SHA256 校验清单 |

下载后建议对照 `SHA256SUMS.txt` 校验完整性，再启动 `AI协同中心.exe`（便携包内）或直接运行独立单文件 `AIDeck-v1.9.15-win-x64.exe`。

### 从源码编译（开发者）

环境：Windows 10/11 x64，以及 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
git clone https://github.com/tianmenglele/ai-deck.git
cd ai-deck
```

Release 编译：

```powershell
dotnet build src/AICollabHub.csproj -c Release --nologo
```

单文件发布到 `outputs/v1.9.15/AI协同中心`（依赖本机 .NET 8 桌面运行时，不带安装向导）：

```powershell
dotnet publish src/AICollabHub.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=portable -p:DebugSymbols=true -o outputs/v1.9.15/AI协同中心 --nologo
```

全量离线自检。v1.9.15 基线为 **364/364 PASS**。命令不改写正在使用的程序，报告写到 `work/self_test_out`（该目录已被 `.gitignore` 忽略）：

```powershell
.\outputs\v1.9.15\AI协同中心\AI协同中心.exe --self-test .\work\self_test_out
```

启动：双击发布目录中的 `AI协同中心.exe`，或：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\启动协同中心.ps1
```

### 软件内自主检查更新

从 **v1.9.15** 起已内置默认官方仓库地址 `https://github.com/tianmenglele/ai-deck`，检查目标对应 [Releases](https://github.com/tianmenglele/ai-deck/releases)。无需手动填写更新源。默认在启动 4 秒后自动后台检查；顶栏版本按钮显示当前 `v1.9.15`。发现新版本时在顶栏展示提示，并支持一键下载、替换后重启。同一时刻只允许一路检查进入，占用中的重复触发会被防重入锁拒绝，结束后才能再次检查。

<a id="开放扩展"></a>

## 开放扩展

注册表与插件都是明文，改完即被运行时读取，不必改 `src/` 才能接入新工具或新流程。

| 扩展口 | 路径 | 用途 |
| --- | --- | --- |
| 工具探测 | `registry/detectors.json` | 本机与桥接工具的路径、进程、命令和默认能力 |
| 挂载适配 | `registry/adapters.json` | 规则与技能写到各工具原生目录的方式 |
| 能力标签 | `registry/capability_tags.json` | 特性路由使用的能力维度 |
| 工作流模板 | `registry/workflows.json` | 分档协同阶段与交付物 |
| 工程预设 | `registry/project_presets.json` | 快照扩展名、排除目录、工程类型差异 |
| 脚本钩子 | `plugins/*.ps1` | 明文脚本扩展；现有 `export_config_digest.ps1` 用于导出配置结构速查 |

### 目录结构

```text
ai-deck/
├── .github/
│   └── workflows/
│       └── release.yml
├── docs/
│   └── images/
│       ├── banner.png
│       ├── 01-workbench.png
│       ├── 02-roster-routing.png
│       ├── 03-rules-skills.png
│       ├── 04-dialogs-dispatch.png
│       ├── 05-snapshots-task.png
│       ├── 06-floating-hud.png
│       ├── 07-completion-bubble.png
│       └── 08-onboarding-wizard.png
├── plugins/
│   └── export_config_digest.ps1
├── registry/
│   ├── adapters.json
│   ├── capability_tags.json
│   ├── detectors.json
│   ├── project_presets.json
│   └── workflows.json
├── src/
│   ├── Assets/
│   │   ├── icons/
│   │   ├── Sounds/
│   │   │   └── CREDITS.md
│   │   └── app_icon.ico
│   ├── Models/
│   ├── Services/
│   ├── UI/
│   ├── AICollabHub.csproj
│   ├── app.manifest
│   └── Program.cs
├── .gitattributes
├── .gitignore
├── AGENTS.md
├── LICENSE
├── README.md
└── 启动协同中心.ps1
```

`outputs/`、`work/`、编译中间目录和本机自动生成的 `.agents/`、`.cursor/` 不进入版本库。

### 个人数据

花名册、更新源、窗口形态和项目记录写在 `%LOCALAPPDATA%\AICollabHub`，与仓库目录分开。绿色分享包不携带这份个人数据。

<a id="借鉴学习与开源致谢"></a>

## 借鉴学习与开源致谢

AIDeck 站在诸多优秀开源项目与开发者生态之上。以下仓库与社区对本项目的架构思路、数据源与工具链有直接启发或对接价值，在此致以诚挚感谢（仓库地址均为真实公开仓库，按字母/相关度罗列）：

- **[nrslib/takt](https://github.com/nrslib/takt)**（TAKT Agent Koordination Topology）：借鉴其多 Agent 声明式分工编排、审查与打回精修闭环防无限空转、以及外部工作节点桥接理念（软件内置 `takt_vps_bridge` 桥接支持）。
- **[BerriAI/litellm](https://github.com/BerriAI/litellm)**：感谢 LiteLLM 开源社区维护的公开模型规格与上下文窗口清单（用于云端模型能力推荐数据源之一）。
- **[openai/codex](https://github.com/openai/codex)** 与 **[anthropics/claude-code](https://github.com/anthropics/claude-code)**：感谢 OpenAI Codex CLI 与 Claude Code CLI 团队打造的多 Agent 终端工具生态，AIDeck 原生支持其状态探测、版本检查与规则挂载。
- **[ollama/ollama](https://github.com/ollama/ollama)**：感谢 Ollama 本地开源大模型运行时社区，AIDeck 开箱支持本地 OpenAI 兼容服务探测。
- **[dotnet/wpf](https://github.com/dotnet/wpf)**：感谢 .NET 8 与 WPF 开源社区提供稳定的 Windows 桌面端单文件发布与 UI Automation 底层能力。
- **[yetone/magpie](https://github.com/yetone/magpie)**：感谢其中心拓扑脑图视觉设计思路的启发。

同时感谢 **OpenRouter**、**Cursor**、**Antigravity**、**Grok Bot**、**LM Studio**、**Windsurf** 等开发者生态，以及公开分享 `AGENTS.md` / `.cursor/rules` / `.cursor/skills` 规范的开源贡献者。

<a id="致谢与协议"></a>

## 致谢与协议

5 路内嵌提示音为 DOLLARS 风格电钢柔和乐句，格式 44100 Hz / 16-bit PCM，按 **CC0-1.0** 分发。曲目、时长与触发场景见 [src/Assets/Sounds/CREDITS.md](src/Assets/Sounds/CREDITS.md)。

本仓库以 [MIT License](LICENSE) 授权，版权声明为 `Copyright (c) 2026 tianmenglele`。
