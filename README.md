# AIDeck · 多 AI 协同中心

英文品牌名 **AIDeck**。推荐 Git 仓库名 `ai-deck`。示例远程地址：`https://github.com/<owner>/ai-deck.git`（SSH：`git@github.com:<owner>/ai-deck.git`）。

面向 Windows 多项目开发的多 AI 协同管理与调度中心（.NET 8 + WPF 单文件发布，零外部运行时依赖），支持本机桌面 AI 与外部桥接工具的统一检测、特性路由、规则技能零拷贝同步与置顶悬浮监控。

## 核心特性（v1.9.14）

- **本机 AI 与外部桥接自动检测**：只读探测 Antigravity、Cursor、Grok Bot、Codex、Claude、Windsurf、Ollama、LM Studio 及外部桥接节点，自由勾选启停与配置模型档位。
- **版本号展示与自主检查更新**：顶栏保留版本按钮（当前 `v1.9.14`）与新版本徽章；可手动检查，启动后也会在后台静默检查 GitHub / Gitee / 自定义 JSON 更新源。
- **毫秒/秒级实时状态同步**：主界面与置顶悬浮窗按毫秒级探测、秒级刷新各 AI 的工作阶段，回执不串到其他工程。
- **飞行任务单显示具体任务名**：派单胶囊与飞行任务单展示提炼后的具体任务名。
- **全 AI（含 Grok Bot）纯后台零抢前台**：桌面端（含 Grok Bot）默认纯后台投递，不抢当前焦点。
  - 默认禁止桌面客户端 AI 静默降级至不可见的无头 CLI（`AllowHeadlessCliFallbackForDesktopAgents = false`），每条任务均在对应桌面软件会话内实时可见；
  - 进程内轻量 UIA 状态探测与跨工程分组隔离，消除多项目并行时的状态串扰并显著缩短回执延迟；
  - 默认开启严禁抢前台策略（`StrictNoForegroundStealOnDispatch = true`），后台操作优先无焦点 UIA / `SW_SHOWNOACTIVATE` + `HWND_BOTTOM` 与键鼠静默门控，不打断当前打字与点击。
- **桌面置顶悬浮窗（全形态锁定）**：支持折叠胶囊、极简单行、紧凑双行与完整展开四种形态，严格保持用户手动切换后的形态，任何任务开始/完成/异常刷新均不自动展开或收起。
- **长耗时交互体验统一优化**：通过 `LongActionTracker` 为 CLI 安装/更新、扫描本机、云端同步、规则同步、绿色包导出等耗时按钮提供跨重绘防重复点击互斥与实时秒表反馈。
- **DOLLARS 风格电钢柔和提示音**：内置 5 路 CC0-1.0 电钢柔和乐句（44100 Hz 16-bit PCM，总计约 633 KB），支持完整/轻量/静音三档与进程级静音硬闸门。
- **单一事实源规则同步 & 六大开放活口**：维护项目内一份规则、技能与经验库真身，零拷贝派发至各启用工具；`registry/*.json` 与 `plugins/*.ps1` 全明文开放扩展。

## 目录结构

```text
ai-collab-hub/
├── src/                    # .NET 8 WPF 源码（含内嵌图标与提示音资源）
│   ├── Assets/             # 图标与内嵌提示音（含 CREDITS.md）
│   ├── Models/             # 配置与数据结构定义
│   ├── Services/           # 自动派单、路由、长操作锁、音效、CLI 安装等核心服务
│   ├── UI/                 # 主界面、置顶悬浮窗与弹窗
│   ├── AICollabHub.csproj  # 项目文件
│   └── Program.cs          # 入口、CLI 命令与全量离线自检 (--self-test)
├── registry/               # 开放注册表（探测器、适配器、能力标签、工作流模板等）
├── plugins/                # 开放扩展脚本钩子
├── 启动协同中心.ps1         # 命令行启动与派单入口脚本
├── .gitignore              # 忽略编译产物、outputs/、work/ 与本机缓存
└── .gitattributes          # 跨平台换行符与二进制资源规则
```

> 运行时个人数据隔离存放在 `%LOCALAPPDATA%\AICollabHub`，不会污染仓库目录。

## 环境要求与构建

- **环境**：Windows 10/11 x64，[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 1. 编译（Release）
```powershell
dotnet build src/AICollabHub.csproj -c Release --nologo
```

### 2. 单文件发布
```powershell
dotnet publish src/AICollabHub.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=portable -p:DebugSymbols=true -o outputs/v1.9.14/AI协同中心 --nologo
```

### 3. 运行离线全量自检
```powershell
.\outputs\v1.9.14\AI协同中心\AI协同中心.exe --self-test .\work\self_test_out
```

### 4. 启动程序
直接双击发布目录中的 `AI协同中心.exe`，或使用命令行脚本启动：
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\启动协同中心.ps1
```
