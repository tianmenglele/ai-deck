using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AICollabHub.Models;
using AICollabHub.Services;

namespace AICollabHub.UI;

public enum FloatingRichFxKind
{
    MasterStandby,
    MasterAnalyzing,
    Thinking,
    Dispatching,
    Coding,
    Delivering,
    MasterVerifying,
    Reworking,
    VerifiedDelivered,
    IdleReady,
    IdleOffline,
    Independent,
    Error
}

public readonly record struct FloatingRichStatus(
    FloatingRichFxKind Kind,
    string BadgeText,
    string StatusLine,
    string BadgeBgHex,
    string AccentHex,
    string TextHex,
    string DotHex,
    bool IsActiveWork);

public partial class FloatingOverlayWindow
{
    /// <summary>生成标准 WPF #AARRGGBB 透明色字符串（防止误写成 #RRGGBBAA 导致颜色发绿错乱）。</summary>
    public static string WithAlphaHex(string rgbHex, byte alpha)
    {
        var clean = (rgbHex ?? "").Trim().TrimStart('#');
        if (clean.Length == 8) clean = clean[2..];
        if (clean.Length != 6) clean = "475569";
        return $"#{alpha:X2}{clean.ToUpperInvariant()}";
    }

    /// <summary>自检/UI 共用：根据 Status / WorkflowPhase / LatestLogTail 细分生动状态。</summary>
    public static FloatingRichStatus ResolveRichStatusForTest(
        AgentRosterEntry ag,
        LiveAgentWorkState? live,
        bool winRunning,
        bool workflowEnabled,
        bool isMaster,
        LiveAgentWorkState? activeWorkerOrNull = null,
        bool recentWorkerDelivered = false)
    {
        string dot = winRunning ? "#10B981" : "#64748B";
        if (!workflowEnabled)
        {
            return new FloatingRichStatus(
                FloatingRichFxKind.Independent,
                "⚪ 独立运行",
                "独立模式（协同工作流已停用）",
                "#334155",
                "#64748B",
                "#F1F5F9",
                dot,
                false);
        }

        string phase = live?.WorkflowPhase ?? "";
        string status = live?.Status ?? "idle";
        string log = live?.LatestLogTail ?? "";
        string task = AutoDispatchService.ExtractCleanTaskBrief(live?.TaskSummary, 18);
        if (string.IsNullOrWhiteSpace(task)) task = "任务";

        // 主脑视角：跟随子 AI 阶段映射专属徽章
        if (isMaster)
        {
            if (activeWorkerOrNull != null)
            {
                var workerRich = ResolveWorkerRich(activeWorkerOrNull, winRunning);
                return activeWorkerOrNull.WorkflowPhase switch
                {
                    "master_verifying" => workerRich with
                    {
                        Kind = FloatingRichFxKind.MasterVerifying,
                        BadgeText = "🔍 严格审查中",
                        StatusLine = $"正在严格审查 {ShortAgentNameStatic(activeWorkerOrNull.AgentName)} 的「{AutoDispatchService.ExtractCleanTaskBrief(activeWorkerOrNull.TaskSummary, 16)}」",
                        BadgeBgHex = "#713F12",
                        AccentHex = "#FDE047",
                        TextHex = "#FFFFFF",
                        DotHex = "#FACC15",
                        IsActiveWork = true
                    },
                    "reworking" => workerRich with
                    {
                        Kind = FloatingRichFxKind.Reworking,
                        BadgeText = "🔧 打回精修中",
                        StatusLine = $"已打回 {ShortAgentNameStatic(activeWorkerOrNull.AgentName)} 精修「{AutoDispatchService.ExtractCleanTaskBrief(activeWorkerOrNull.TaskSummary, 16)}」",
                        BadgeBgHex = "#9A3412",
                        AccentHex = "#FB923C",
                        TextHex = "#FFFFFF",
                        DotHex = "#F97316",
                        IsActiveWork = true
                    },
                    "master_analyzing" => new FloatingRichStatus(
                        FloatingRichFxKind.MasterAnalyzing,
                        "🧠 深度分析中",
                        "主脑正在深度拆解推演任务与路由",
                        "#701A75",
                        "#F472B6",
                        "#FFFFFF",
                        "#E879F9",
                        true),
                    _ => new FloatingRichStatus(
                        FloatingRichFxKind.Dispatching,
                        "⚡ 派单调度中",
                        $"已派单调度 {ShortAgentNameStatic(activeWorkerOrNull.AgentName)} 执行「{AutoDispatchService.ExtractCleanTaskBrief(activeWorkerOrNull.TaskSummary, 16)}」",
                        "#312E81",
                        "#818CF8",
                        "#FFFFFF",
                        "#6366F1",
                        true)
                };
            }

            // 主脑自己仍在 working/reworking、但还没有子 AI 承接时，保持深度分析，避免档位微标掉回自动选档
            if (live != null && (status is "working" or "reworking"))
            {
                return new FloatingRichStatus(
                    FloatingRichFxKind.MasterAnalyzing,
                    "🧠 深度分析中",
                    "主脑正在深度分析与模型档位匹配",
                    "#701A75",
                    "#F472B6",
                    "#FFFFFF",
                    "#E879F9",
                    true);
            }

            if (live != null && (phase.Equals("verified_delivered", StringComparison.OrdinalIgnoreCase) || status == "done"))
            {
                return new FloatingRichStatus(
                    FloatingRichFxKind.VerifiedDelivered,
                    "✅ 达标已交付",
                    $"「{task}」已验收通过并向用户交付",
                    "#065F46",
                    "#34D399",
                    "#FFFFFF",
                    "#10B981",
                    false);
            }

            if (phase.Equals("master_analyzing", StringComparison.OrdinalIgnoreCase) ||
                LogLooksLike(log, "analyz", "规划", "拆解", "推演", "planning"))
            {
                return new FloatingRichStatus(
                    FloatingRichFxKind.MasterAnalyzing,
                    "🧠 深度分析中",
                    "主脑正在深度分析与模型档位匹配",
                    "#701A75",
                    "#F472B6",
                    "#FFFFFF",
                    "#E879F9",
                    true);
            }

            // 主脑空闲：皇家深紫底 + 纯白高对比粗字，静止不闪
            string masterIdleLine = recentWorkerDelivered
                ? "全入口统领主脑 · 已终验放行最新交付"
                : "全入口统领主脑 · 随时接单拆解与终验";
            return new FloatingRichStatus(
                FloatingRichFxKind.MasterStandby,
                "👑 主脑待命",
                masterIdleLine,
                "#5B21B6",
                "#C084FC",
                "#FFFFFF",
                "#A855F7",
                false);
        }

        return ResolveWorkerRich(live, winRunning);
    }

    private static FloatingRichStatus ResolveWorkerRich(LiveAgentWorkState? live, bool winRunning)
    {
        if (live == null || string.IsNullOrWhiteSpace(live.TaskSummary))
        {
            return winRunning
                ? new FloatingRichStatus(FloatingRichFxKind.IdleReady, "☕ 空闲待命中", "等待主脑分配实操任务", "#1E3A5F", "#60A5FA", "#F8FAFC", "#38BDF8", false)
                : new FloatingRichStatus(FloatingRichFxKind.IdleOffline, "💤 未启动", "窗口未运行 · 待命", "#1E293B", "#475569", "#CBD5E1", "#64748B", false);
        }

        string phase = live.WorkflowPhase ?? "";
        string status = live.Status ?? "idle";
        string log = live.LatestLogTail ?? "";
        string task = AutoDispatchService.ExtractCleanTaskBrief(live.TaskSummary, 18);
        if (string.IsNullOrWhiteSpace(task)) task = "任务";

        if (status == "error")
        {
            return new FloatingRichStatus(
                FloatingRichFxKind.Error,
                "⚠ 异常",
                $"「{task}」异常 ({SanitizeFailureBadgeForDisplay(live.AgentId, live.LastFailureBadge)})",
                "#991B1B",
                "#F87171",
                "#FFFFFF",
                "#EF4444",
                false);
        }

        if (phase.Equals("verified_delivered", StringComparison.OrdinalIgnoreCase) || status == "done")
        {
            return new FloatingRichStatus(
                FloatingRichFxKind.VerifiedDelivered,
                "✅ 达标已交付",
                $"「{task}」验收通过已交付",
                "#065F46",
                "#34D399",
                "#FFFFFF",
                "#10B981",
                false);
        }

        if (phase.Equals("reworking", StringComparison.OrdinalIgnoreCase) || (live.CurrentRound > 1 && status is "working" or "reworking"))
        {
            return new FloatingRichStatus(
                FloatingRichFxKind.Reworking,
                "🔧 打回精修中",
                $"主脑打回 · 第{live.CurrentRound}轮精修「{task}」",
                "#9A3412",
                "#FB923C",
                "#FFFFFF",
                "#F97316",
                true);
        }

        if (phase.Equals("master_verifying", StringComparison.OrdinalIgnoreCase))
        {
            return new FloatingRichStatus(
                FloatingRichFxKind.MasterVerifying,
                "🔍 严格审查中",
                $"「{task}」已交主脑严格审查 Diff/自检",
                "#713F12",
                "#FDE047",
                "#FFFFFF",
                "#FACC15",
                true);
        }

        if (phase.Equals("master_analyzing", StringComparison.OrdinalIgnoreCase))
        {
            return new FloatingRichStatus(
                FloatingRichFxKind.MasterAnalyzing,
                "🧠 深度分析中",
                "主脑深度分析调度中",
                "#701A75",
                "#F472B6",
                "#FFFFFF",
                "#E879F9",
                true);
        }

        // 交付中：青碧深底 + 纯白亮字
        if (LogLooksLike(log, "提交主脑", "交付", "inbox", "落盘完成", "OutputInbox", "提请验收") ||
            (status == "working" && (log?.Length ?? 0) >= 220 && !phase.Equals("executing", StringComparison.OrdinalIgnoreCase)))
        {
            return new FloatingRichStatus(
                FloatingRichFxKind.Delivering,
                "📦 正在交付中",
                $"「{task}」落盘完成 · 正在回传交付包",
                "#115E59",
                "#2DD4BF",
                "#FFFFFF",
                "#14B8A6",
                true);
        }

        // 思考推演：深海天青
        if (LogLooksLike(log, "Thinking", "Thought", "Planning", "Exploring", "思考", "推演", "分析中"))
        {
            return new FloatingRichStatus(
                FloatingRichFxKind.Thinking,
                "💭 思考推演中",
                $"「{task}」思考推演中…",
                "#0C4A6E",
                "#22D3EE",
                "#FFFFFF",
                "#38BDF8",
                true);
        }

        // 编码实操：深琥珀金
        if (phase.Equals("executing", StringComparison.OrdinalIgnoreCase) ||
            LogLooksLike(log, "Editing", "Running", "写入", "修改", "实操", "Applying", "Write", "StrReplace"))
        {
            return new FloatingRichStatus(
                FloatingRichFxKind.Coding,
                "🔨 编码实操中",
                $"正在编码实操修改「{task}」",
                "#92400E",
                "#FBBF24",
                "#FFFFFF",
                "#F59E0B",
                true);
        }

        if (status is "working" or "reworking")
        {
            if ((log?.Trim().Length ?? 0) < 35)
            {
                return new FloatingRichStatus(
                    FloatingRichFxKind.Thinking,
                    "💭 思考推演中",
                    $"已接单「{task}」· 思考推演启动中",
                    "#0C4A6E",
                    "#22D3EE",
                    "#FFFFFF",
                    "#38BDF8",
                    true);
            }
            return new FloatingRichStatus(
                FloatingRichFxKind.Coding,
                "🔨 编码实操中",
                $"「{task}」实操进行中",
                "#92400E",
                "#FBBF24",
                "#FFFFFF",
                "#F59E0B",
                true);
        }

        return winRunning
            ? new FloatingRichStatus(FloatingRichFxKind.IdleReady, "☕ 空闲待命中", "等待主脑分配实操任务", "#1E3A5F", "#60A5FA", "#F8FAFC", "#38BDF8", false)
            : new FloatingRichStatus(FloatingRichFxKind.IdleOffline, "💤 未启动", "窗口未运行 · 待命", "#1E293B", "#475569", "#CBD5E1", "#64748B", false);
    }

    /// <summary>严禁把 Codex 配额文案误展示为 Cursor 额度耗尽。</summary>
    public static string SanitizeFailureBadgeForDisplay(string? agentId, string? badge)
    {
        if (string.IsNullOrWhiteSpace(badge)) return "";
        string b = badge.Trim();
        bool looksQuota = b.Contains("配额", StringComparison.OrdinalIgnoreCase) ||
                          b.Contains("额度", StringComparison.OrdinalIgnoreCase) ||
                          b.Contains("usage_limit", StringComparison.OrdinalIgnoreCase) ||
                          b.Contains("quota", StringComparison.OrdinalIgnoreCase);
        if (!looksQuota) return b;
        if (agentId != null && agentId.Equals("cursor", StringComparison.OrdinalIgnoreCase) &&
            (b.Contains("Codex", StringComparison.OrdinalIgnoreCase) ||
             b.Contains("执行引擎", StringComparison.OrdinalIgnoreCase) ||
             b.Contains("备用", StringComparison.OrdinalIgnoreCase) ||
             !b.Contains("Cursor", StringComparison.OrdinalIgnoreCase)))
        {
            return "备用 Codex 引擎配额耗尽（非 Cursor 额度）";
        }
        if (b.Contains("Cursor", StringComparison.OrdinalIgnoreCase) &&
            (b.Contains("并非", StringComparison.Ordinal) || b.Contains("桌面", StringComparison.Ordinal)))
        {
            return "Cursor 桌面通道未就绪 / 未装 CLI";
        }
        return b;
    }

    private static bool LogLooksLike(string? log, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(log)) return false;
        foreach (var k in keys)
        {
            if (log.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string ShortAgentNameStatic(string rawName) =>
        rawName.Replace(" (桌面端)", "").Replace(" (总包工头)", "").Replace(" CLI", "").Trim();

    private FloatingRichStatus ResolveRichStatus(
        AgentRosterEntry ag,
        LiveAgentWorkState? agLive,
        bool winRunning,
        Dictionary<string, LiveAgentWorkState> liveMap)
    {
        string masterId = ResolveMasterAgentId();
        bool isMaster = ag.Id.Equals(masterId, StringComparison.OrdinalIgnoreCase);
        LiveAgentWorkState? activeWorker = null;
        bool recentDelivered = false;
        if (isMaster)
        {
            activeWorker = liveMap.Values
                .Where(v => (v.Status == "working" || v.Status == "reworking") &&
                            !v.AgentId.Equals(masterId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.UpdatedAt)
                .FirstOrDefault();
            recentDelivered = liveMap.Values.Any(v =>
                !v.AgentId.Equals(masterId, StringComparison.OrdinalIgnoreCase) &&
                (v.Status == "done" || v.WorkflowPhase.Equals("verified_delivered", StringComparison.OrdinalIgnoreCase)));
        }
        return ResolveRichStatusForTest(ag, agLive, winRunning, _reg.UserConfig.WorkflowEngineEnabled, isMaster, activeWorker, recentDelivered);
    }

    private UIElement BuildRichAvatarWithFx(AgentRosterEntry ag, double size, double dotSize, FloatingRichStatus rich, double marginRight, int activeTaskCount = 0)
    {
        var host = new Grid
        {
            Width = size + 8,
            Height = size + 8,
            Margin = new Thickness(0, 0, marginRight, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = $"avatar:{ag.Id}"
        };

        // 双层动效环贴合头像安全框（宿主 size+8），描边 1.6–2.0，缩放不越出裁切
        var innerHalo = new Ellipse
        {
            Width = size + 1.5,
            Height = size + 1.5,
            Stroke = Hex(WithAlphaHex(rich.AccentHex, 0x55)),
            StrokeThickness = 1.6,
            Fill = Brushes.Transparent,
            Opacity = AllowsLoopingStatusAnimation(rich.Kind) ? 0.55 : 0.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
            IsHitTestVisible = false
        };

        var fxRing = new Ellipse
        {
            Width = size + 4,
            Height = size + 4,
            Stroke = Hex(rich.AccentHex),
            StrokeThickness = 1.8,
            Fill = Brushes.Transparent,
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
            IsHitTestVisible = false
        };

        var circleHost = new Border
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = OfficialBrandIconService.CreateCircularAvatarVisual(
                ag.Id, size, ag, rich.AccentHex, size >= 28 ? 2.0 : 1.5)
        };

        var statusDot = new Border
        {
            Width = dotSize,
            Height = dotSize,
            CornerRadius = new CornerRadius(dotSize / 2.0),
            Background = Hex(rich.DotHex),
            BorderBrush = Hex("#0F1521"),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 2)
        };

        host.Children.Add(innerHalo);
        host.Children.Add(fxRing);
        host.Children.Add(circleHost);
        host.Children.Add(statusDot);
        var taskBadge = TryBuildAvatarTaskCountBadge(activeTaskCount, size);
        if (taskBadge != null) host.Children.Add(taskBadge);
        ApplyAvatarMicroFx(fxRing, innerHalo, statusDot, circleHost, rich.Kind, rich.AccentHex);
        return host;
    }

    /// <summary>并行任务数 ≥ 2 时在头像右上角叠数字角标，右下角状态点保持可见。</summary>
    public static Border? TryBuildAvatarTaskCountBadge(int activeTaskCount, double avatarSize)
    {
        if (activeTaskCount < 2) return null;
        string label = activeTaskCount > 9 ? "9+" : activeTaskCount.ToString();
        double side = Math.Clamp(avatarSize * 0.42, 11, 16);
        if (label.Length > 1) side = Math.Clamp(Math.Max(side, avatarSize * 0.5), 13, 18);
        double font = label.Length > 1 ? Math.Max(7.0, side * 0.42) : Math.Max(8.0, side * 0.52);
        return new Border
        {
            Name = "AvatarTaskCountBadge",
            Tag = "task_count:" + activeTaskCount,
            Width = side,
            Height = side,
            MinWidth = side,
            CornerRadius = new CornerRadius(side / 2.0),
            Background = Hex("#EA580C"),
            BorderBrush = Hex("#0F1521"),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = font,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Padding = new Thickness(0)
            }
        };
    }

    public static FrameworkElement WrapAvatarWithTaskBadge(FrameworkElement avatar, int activeTaskCount, double avatarSize)
    {
        var badge = TryBuildAvatarTaskCountBadge(activeTaskCount, avatarSize);
        if (badge == null) return avatar;
        var host = new Grid
        {
            Width = avatarSize + 8,
            Height = avatarSize + 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        avatar.HorizontalAlignment = HorizontalAlignment.Center;
        avatar.VerticalAlignment = VerticalAlignment.Center;
        host.Children.Add(avatar);
        host.Children.Add(badge);
        return host;
    }

    public FrameworkElement BuildRichAvatarForTest(AgentRosterEntry ag, FloatingRichStatus rich, double size, int activeTaskCount)
        => (FrameworkElement)BuildRichAvatarWithFx(ag, size, Math.Max(6, size * 0.28), rich, 0, activeTaskCount);

    /// <summary>仅活跃工作态允许循环动画；静止/终态（待命/空闲/已交付/离线）必须关闭循环闪烁。</summary>
    public static bool AllowsLoopingStatusAnimation(FloatingRichFxKind kind) => kind switch
    {
        FloatingRichFxKind.MasterAnalyzing or
        FloatingRichFxKind.Thinking or
        FloatingRichFxKind.Dispatching or
        FloatingRichFxKind.Coding or
        FloatingRichFxKind.Delivering or
        FloatingRichFxKind.MasterVerifying or
        FloatingRichFxKind.Reworking => true,
        _ => false
    };

    private void ApplyAvatarMicroFx(Ellipse fxRing, Ellipse innerHalo, Border statusDot, FrameworkElement circle, FloatingRichFxKind kind, string accentHex)
    {
        if (kind == FloatingRichFxKind.Coding)
        {
            // 只保留 RenderTransformOrigin 一处圆心；RotateTransform 中心必须为 0，否则双重原点会偏心乱转
            fxRing.Tag = "avatar_fx_ring";
            fxRing.StrokeThickness = 1.8;
            fxRing.StrokeDashArray = new DoubleCollection { 3.2, 2.2 };
            fxRing.RenderTransformOrigin = new Point(0.5, 0.5);
            var rot = new RotateTransform(0);
            fxRing.RenderTransform = rot;
            if (!AppPaths.IsIsolatedTest)
            {
                var rotAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.6))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                rot.BeginAnimation(RotateTransform.AngleProperty, rotAnim);
                StartOpacityBreath(innerHalo, 0.30, 0.8, 900, loop: true);
            }
            return;
        }

        if (AppPaths.IsIsolatedTest) return;

        double ringHi = Math.Min(1.04, (fxRing.Width + 2.6) / Math.Max(8, fxRing.Width));
        double haloHi = Math.Min(1.03, (innerHalo.Width + 2.2) / Math.Max(8, innerHalo.Width));
        switch (kind)
        {
            case FloatingRichFxKind.MasterAnalyzing:
                fxRing.StrokeThickness = 1.8;
                fxRing.StrokeDashArray = new DoubleCollection { 2.2, 2.4 };
                StartPulseScale(fxRing, 0.985, ringHi, 1100, loop: true);
                StartOpacityBreath(innerHalo, 0.28, 0.72, 1100, loop: true);
                break;
            case FloatingRichFxKind.Thinking:
                fxRing.StrokeThickness = 1.6;
                StartOpacityBreath(fxRing, 0.46, 0.9, 980, loop: true);
                StartPulseScale(innerHalo, 0.99, haloHi, 980, loop: true);
                break;
            case FloatingRichFxKind.Dispatching:
                fxRing.StrokeThickness = 2.0;
                StartPulseScale(fxRing, 0.99, ringHi, 920, loop: true);
                StartOpacityBreath(innerHalo, 0.32, 0.78, 920, loop: true);
                break;
            case FloatingRichFxKind.Delivering:
                fxRing.StrokeThickness = 1.6;
                StartOpacityBreath(circle, 0.9, 1.0, 860, loop: true);
                StartPulseScale(fxRing, 0.99, ringHi, 860, loop: true);
                StartOpacityBreath(innerHalo, 0.34, 0.82, 860, loop: true);
                break;
            case FloatingRichFxKind.MasterVerifying:
                fxRing.StrokeThickness = 1.8;
                fxRing.StrokeDashArray = new DoubleCollection { 4, 2.4 };
                StartOpacityBreath(fxRing, 0.5, 0.95, 900, loop: true);
                StartPulseScale(innerHalo, 0.99, haloHi, 900, loop: true);
                break;
            case FloatingRichFxKind.Reworking:
                fxRing.StrokeThickness = 2.0;
                StartOpacityBreath(fxRing, 0.55, 0.95, 840, loop: true);
                StartPulseScale(innerHalo, 0.99, haloHi, 840, loop: true);
                fxRing.Stroke = Hex("#FB923C");
                break;
            case FloatingRichFxKind.VerifiedDelivered:
                fxRing.StrokeThickness = 1.6;
                fxRing.Opacity = 0.78;
                innerHalo.Opacity = 0.35;
                StartOpacityBreath(fxRing, 0.62, 0.84, 720, loop: false);
                break;
            case FloatingRichFxKind.MasterStandby:
            case FloatingRichFxKind.IdleReady:
            case FloatingRichFxKind.IdleOffline:
            case FloatingRichFxKind.Independent:
            case FloatingRichFxKind.Error:
            default:
                // 静止/完成态：100% 静止不闪烁，避免达标已交付/主脑待命/空闲态持续闪动
                fxRing.BeginAnimation(UIElement.OpacityProperty, null);
                innerHalo.BeginAnimation(UIElement.OpacityProperty, null);
                statusDot.BeginAnimation(UIElement.OpacityProperty, null);
                circle.BeginAnimation(UIElement.OpacityProperty, null);
                innerHalo.Opacity = 0.0;
                fxRing.Opacity = kind switch
                {
                    FloatingRichFxKind.VerifiedDelivered => 0.72,
                    FloatingRichFxKind.MasterStandby => 0.65,
                    FloatingRichFxKind.IdleReady => 0.42,
                    FloatingRichFxKind.Error => 0.78,
                    _ => 0.22
                };
                statusDot.Opacity = 1.0;
                circle.Opacity = 1.0;
                break;
        }
    }

    private static void StartPulseScale(UIElement el, double from, double to, int ms, bool loop)
    {
        var t = new ScaleTransform(1, 1);
        el.RenderTransform = t;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            AutoReverse = true,
            RepeatBehavior = loop ? RepeatBehavior.Forever : new RepeatBehavior(1),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        t.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        t.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static void StartOpacityBreath(UIElement el, double from, double to, int ms, bool loop)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            AutoReverse = true,
            RepeatBehavior = loop ? RepeatBehavior.Forever : new RepeatBehavior(1),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        el.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private Border BuildStatusBadgeWithMicroFx(FloatingRichStatus rich, bool ultraCompact = false)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var micro = AllowsLoopingStatusAnimation(rich.Kind) ? BuildBadgeMicroWidget(rich.Kind, rich.AccentHex) : null;
        if (micro != null)
        {
            row.Children.Add(micro);
        }
        var label = new TextBlock
        {
            Name = "BadgeLabelText",
            Text = rich.BadgeText,
            FontSize = ultraCompact ? 10.5 : 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = Hex(rich.TextHex),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(micro != null ? (ultraCompact ? 3 : 4) : 0, 0, 0, 0)
        };
        row.Children.Add(label);
        return new Border
        {
            Name = "StatusBadgePill",
            Background = Hex(rich.BadgeBgHex),
            BorderBrush = Hex(rich.AccentHex),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = ultraCompact ? new Thickness(5, 0.5, 5, 0.5) : new Thickness(7, 1.5, 8, 1.5),
            Margin = ultraCompact ? new Thickness(0) : new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = row
        };
    }

    private UIElement? BuildBadgeMicroWidget(FloatingRichFxKind kind, string accentHex)
    {
        switch (kind)
        {
            case FloatingRichFxKind.Thinking:
            {
                var canvas = new Canvas
                {
                    Width = 14,
                    Height = 10,
                    Margin = new Thickness(0, 0, 0, 0),
                    UseLayoutRounding = false,
                    SnapsToDevicePixels = false
                };
                for (int i = 0; i < 3; i++)
                {
                    var dot = new Ellipse
                    {
                        Width = 3.2,
                        Height = 3.2,
                        Fill = Hex(accentHex),
                        Opacity = 0.92,
                        UseLayoutRounding = false,
                        SnapsToDevicePixels = false
                    };
                    Canvas.SetLeft(dot, i * 4.5);
                    Canvas.SetTop(dot, 3.5);
                    canvas.Children.Add(dot);
                    if (!AppPaths.IsIsolatedTest)
                    {
                        var tt = new TranslateTransform();
                        dot.RenderTransform = tt;
                        var anim = new DoubleAnimation(-2.2, 2.2, TimeSpan.FromMilliseconds(420))
                        {
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                            BeginTime = TimeSpan.FromMilliseconds(i * 120),
                            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                        };
                        tt.BeginAnimation(TranslateTransform.YProperty, anim);
                    }
                }
                return canvas;
            }
            case FloatingRichFxKind.Coding:
            {
                var gear = new TextBlock
                {
                    Text = "✦",
                    FontSize = 10,
                    Foreground = Hex(accentHex),
                    FontWeight = FontWeights.Bold,
                    Width = 12,
                    Height = 12,
                    TextAlignment = TextAlignment.Center,
                    UseLayoutRounding = false,
                    SnapsToDevicePixels = false
                };
                gear.RenderTransformOrigin = new Point(0.5, 0.5);
                var rot = new RotateTransform(0);
                gear.RenderTransform = rot;
                if (!AppPaths.IsIsolatedTest)
                {
                    var rotAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.6))
                    {
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    rot.BeginAnimation(RotateTransform.AngleProperty, rotAnim);
                }
                return gear;
            }
            case FloatingRichFxKind.MasterAnalyzing:
            {
                var ring = new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Stroke = Hex(accentHex),
                    StrokeThickness = 1.4,
                    Fill = Brushes.Transparent,
                    UseLayoutRounding = false,
                    SnapsToDevicePixels = false
                };
                if (!AppPaths.IsIsolatedTest)
                {
                    StartPulseScale(ring, 0.78, 1.18, 700, loop: true);
                }
                return ring;
            }
            default:
                return null;
        }
    }

    private UIElement BuildCodingEnergyBar(string accentHex, double trackWidth = 180)
    {
        double w = Math.Clamp(trackWidth, 80, 520);
        double glowW = Math.Max(36, w * 0.24);
        var track = new Border
        {
            Height = 2.5,
            Width = w,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(1.2),
            Background = Hex("#1E293B"),
            Margin = new Thickness(0, 4, 0, 0),
            ClipToBounds = true,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false
        };
        var glow = new Border
        {
            Width = glowW,
            Height = 2.5,
            CornerRadius = new CornerRadius(1.2),
            Background = Hex(accentHex),
            HorizontalAlignment = HorizontalAlignment.Left,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false
        };
        var tt = new TranslateTransform(-glowW, 0);
        glow.RenderTransform = tt;
        track.Child = glow;
        if (!AppPaths.IsIsolatedTest)
        {
            double travel = Math.Max(40, w - glowW);
            var anim = new DoubleAnimation(-glowW, travel, TimeSpan.FromMilliseconds(1400))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            tt.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        return track;
    }

    public int FloatingAgentsRowCountForTest => FloatingAgentsStack?.Children.Count ?? 0;

    /// <summary>自检：返回所有 13 种细分状态配置（用于对比度与互异校验）。</summary>
    public static IReadOnlyList<FloatingRichStatus> GetAllRichStatusSpecsForTest()
    {
        var master = new AgentRosterEntry { Id = "antigravity", Name = "Antigravity", Enabled = true };
        var worker = new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true };
        return new[]
        {
            ResolveRichStatusForTest(master, null, true, true, true),
            ResolveRichStatusForTest(master, new LiveAgentWorkState { AgentId = "antigravity", Status = "working", WorkflowPhase = "master_analyzing", TaskSummary = "分析" }, true, true, true),
            ResolveRichStatusForTest(master, null, true, true, true, activeWorkerOrNull: new LiveAgentWorkState { AgentId = "cursor", AgentName = "Cursor", Status = "working", WorkflowPhase = "executing", TaskSummary = "调度" }),
            ResolveRichStatusForTest(worker, new LiveAgentWorkState { AgentId = "cursor", Status = "working", WorkflowPhase = "executing", TaskSummary = "思考", LatestLogTail = "Thinking" }, true, true, false),
            ResolveRichStatusForTest(worker, new LiveAgentWorkState { AgentId = "cursor", Status = "working", WorkflowPhase = "executing", TaskSummary = "改码", LatestLogTail = "Editing" }, true, true, false),
            ResolveRichStatusForTest(worker, new LiveAgentWorkState { AgentId = "cursor", Status = "working", WorkflowPhase = "executing", TaskSummary = "交付", LatestLogTail = "提交主脑验收 inbox" }, true, true, false),
            ResolveRichStatusForTest(worker, new LiveAgentWorkState { AgentId = "cursor", Status = "working", WorkflowPhase = "master_verifying", TaskSummary = "审查" }, true, true, false),
            ResolveRichStatusForTest(worker, new LiveAgentWorkState { AgentId = "cursor", Status = "working", WorkflowPhase = "reworking", TaskSummary = "精修", CurrentRound = 2 }, true, true, false),
            ResolveRichStatusForTest(worker, new LiveAgentWorkState { AgentId = "cursor", Status = "done", WorkflowPhase = "verified_delivered", TaskSummary = "完成" }, true, true, false),
            ResolveRichStatusForTest(worker, null, true, true, false),
            ResolveRichStatusForTest(worker, null, false, true, false),
            ResolveRichStatusForTest(worker, null, true, false, false),
            ResolveRichStatusForTest(worker, new LiveAgentWorkState { AgentId = "cursor", Status = "error", TaskSummary = "异常", LastFailureBadge = "超时" }, true, true, false)
        };
    }

    /// <summary>自检：构建一张细分状态卡片并返回徽章文案（不依赖屏幕焦点）。</summary>
    public string BuildRichStatusBadgeTextForTest(FloatingRichFxKind kind)
    {
        if (kind == FloatingRichFxKind.MasterStandby) return "👑 主脑待命";
        if (kind == FloatingRichFxKind.Dispatching) return "⚡ 派单调度中";
        if (kind == FloatingRichFxKind.IdleReady) return "☕ 空闲待命中";
        if (kind == FloatingRichFxKind.IdleOffline) return "💤 未启动";

        var sample = new LiveAgentWorkState
        {
            AgentId = "cursor",
            AgentName = "Cursor",
            Status = kind switch
            {
                FloatingRichFxKind.VerifiedDelivered => "done",
                FloatingRichFxKind.Error => "error",
                FloatingRichFxKind.IdleReady or FloatingRichFxKind.IdleOffline => "idle",
                _ => "working"
            },
            WorkflowPhase = kind switch
            {
                FloatingRichFxKind.MasterAnalyzing => "master_analyzing",
                FloatingRichFxKind.MasterVerifying => "master_verifying",
                FloatingRichFxKind.Reworking => "reworking",
                FloatingRichFxKind.VerifiedDelivered => "verified_delivered",
                FloatingRichFxKind.Coding => "executing",
                FloatingRichFxKind.Delivering => "executing",
                _ => "executing"
            },
            TaskSummary = "细分状态自检任务",
            LatestLogTail = kind switch
            {
                FloatingRichFxKind.Thinking => "Thinking about the next edit…",
                FloatingRichFxKind.Coding => "Editing FloatingOverlayWindow.xaml.cs\nRunning build",
                FloatingRichFxKind.Delivering => "落盘完成，正在提交主脑验收 inbox",
                FloatingRichFxKind.MasterAnalyzing => "深度拆解推演任务路由",
                _ => "working"
            },
            CurrentRound = kind == FloatingRichFxKind.Reworking ? 2 : 1
        };
        var ag = new AgentRosterEntry { Id = "cursor", Name = "Cursor", Enabled = true };
        bool isMaster = kind is FloatingRichFxKind.MasterAnalyzing or FloatingRichFxKind.Dispatching;
        var rich = ResolveRichStatusForTest(ag, sample, winRunning: kind != FloatingRichFxKind.IdleOffline, workflowEnabled: true, isMaster: isMaster,
            activeWorkerOrNull: isMaster ? sample : null);
        return rich.BadgeText;
    }
}
