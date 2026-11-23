using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AICollabHub.Models;
using AICollabHub.Services;

namespace AICollabHub.UI;

public partial class MainWindow
{
    private DispatcherTimer? _workflowDiagramAnimTimer;
    private bool _workflowDiagramDemoMode;
    private int _workflowDiagramDemoStep;
    private DateTime _workflowDiagramDemoStartUtc;
    private const double WorkflowDiagramDemoStepSeconds = 1.5;
    private double _workflowDiagramDashOffset;
    private double _workflowDiagramPulseT;
    private string _workflowDiagramPhase = "idle";
    private int _workflowDiagramRound = 1;
    private int _workflowDiagramMaxRounds = 2;
    private string? _workflowDiagramActiveWorkerId;
    private string _workflowDiagramMasterId = "";
    private readonly List<string> _workflowDiagramWorkerIds = new();
    private string _workflowDiagramPhaseLabel = "空闲待命";
    private string _workflowDiagramSubFull = "";
    private string _workflowDiagramLayoutSig = "";
    private bool _workflowDiagramPreviewOverride;
    private bool _workflowDiagramHooksWired;
    private Path? _workflowDiagramActiveFlowPath;
    private Path? _workflowDiagramReworkPath;
    private Ellipse? _workflowDiagramPulseDot;
    private readonly List<Border> _workflowDiagramNodeBorders = new();

    private static readonly string[] WorkflowDiagramDemoPhases =
    {
        "master_analyzing",
        "dispatching",
        "executing",
        "master_verifying",
        "reworking",
        "verified_delivered"
    };

    public void RefreshWorkflowDiagram()
    {
        EnsureWorkflowDiagramHooks();
        if (_workflowDiagramPreviewOverride && !_workflowDiagramDemoMode) { RebuildWorkflowDiagramCanvas(); return; }
        if (!_workflowDiagramDemoMode)
            ResolveWorkflowDiagramLivePhase();
        RebuildWorkflowDiagramCanvas();
        UpdateWorkflowDiagramAnimationGate();
    }

    public void SetWorkflowDiagramPreviewPhaseForTest(
        string phase,
        int round = 1,
        int maxRounds = 2,
        string? activeWorkerId = null)
    {
        _workflowDiagramPreviewOverride = true;
        _workflowDiagramDemoMode = false;
        if (WorkflowDiagramDemoBtn != null)
            WorkflowDiagramDemoBtn.Content = "▶ 演示流转";
        ApplyWorkflowDiagramPhase(phase, round, maxRounds, activeWorkerId);
        RebuildWorkflowDiagramCanvas();
        UpdateWorkflowDiagramAnimationGate();
    }

    /// <summary>清除截图/测试用预览阶段，恢复真实工作流状态。</summary>
    public void ClearWorkflowDiagramPreviewPhaseForTest()
    {
        _workflowDiagramPreviewOverride = false;
        _workflowDiagramDemoMode = false;
        if (WorkflowDiagramDemoBtn != null)
            WorkflowDiagramDemoBtn.Content = "▶ 演示流转";
        ResolveWorkflowDiagramLivePhase();
        RebuildWorkflowDiagramCanvas();
        UpdateWorkflowDiagramAnimationGate();
    }

    public bool ToggleWorkflowDiagramDemoForTest()
    {
        ToggleWorkflowDiagramDemoInternal();
        return _workflowDiagramDemoMode;
    }

    public (string MasterId, List<string> WorkerIds, string PhaseText, bool DemoActive, int CanvasChildCount) GetWorkflowDiagramNodeSummaryForTest()
        => (_workflowDiagramMasterId,
            _workflowDiagramWorkerIds.ToList(),
            _workflowDiagramPhaseLabel,
            _workflowDiagramDemoMode,
            WorkflowDiagramCanvas?.Children.Count ?? 0);

    private void EnsureWorkflowDiagramHooks()
    {
        if (_workflowDiagramHooksWired) return;
        _workflowDiagramHooksWired = true;
        IsVisibleChanged += (_, _) => UpdateWorkflowDiagramAnimationGate();
        StateChanged += (_, _) => UpdateWorkflowDiagramAnimationGate();
        Closing += (_, _) => StopWorkflowDiagramAnimTimer();
        Closed += (_, _) => StopWorkflowDiagramAnimTimer();
    }

    private void WorkflowDiagramCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged || e.HeightChanged)
            RebuildWorkflowDiagramCanvas();
    }

    private void WorkflowDiagramDemoBtn_Click(object sender, RoutedEventArgs e)
        => ToggleWorkflowDiagramDemoInternal();

    private void ToggleWorkflowDiagramDemoInternal()
    {
        _workflowDiagramDemoMode = !_workflowDiagramDemoMode;
        _workflowDiagramPreviewOverride = false;
        if (_workflowDiagramDemoMode)
        {
            _workflowDiagramDemoStep = 0;
            _workflowDiagramDemoStartUtc = DateTime.UtcNow;
            ApplyDemoPhaseStep();
            if (WorkflowDiagramDemoBtn != null)
                WorkflowDiagramDemoBtn.Content = "⏹ 停止演示";
        }
        else
        {
            if (WorkflowDiagramDemoBtn != null)
                WorkflowDiagramDemoBtn.Content = "▶ 演示流转";
            ResolveWorkflowDiagramLivePhase();
        }
        RebuildWorkflowDiagramCanvas();
        UpdateWorkflowDiagramAnimationGate();
    }

    private void ResolveWorkflowDiagramLivePhase()
    {
        bool wfOn = _reg.UserConfig.WorkflowEngineEnabled;
        int maxRounds = Math.Clamp(_reg.UserConfig.MaxReworkRounds + 1, 1, 4);
        if (!wfOn)
        {
            ApplyWorkflowDiagramPhase("paused", 1, maxRounds, null);
            return;
        }

        var liveMap = AutoDispatchService.ReadMergedGlobalWorkStatesWithDesktopLive();
        string phase = "idle";
        int round = 1;
        string? activeWorker = null;
        string? bestTask = null;
        long bestTicks = 0;

        foreach (var kv in liveMap)
        {
            var st = kv.Value;
            if (st == null) continue;
            string p = (st.WorkflowPhase ?? "").Trim().ToLowerInvariant();
            string status = (st.Status ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(p) && status == "working") p = "executing";
            if (status == "done" && string.IsNullOrEmpty(p)) p = "verified_delivered";

            int rank = PhaseRank(p, status);
            long ticks = st.UpdatedUtcTicks > 0 ? st.UpdatedUtcTicks : 0;
            if (rank > PhaseRank(phase, "") || (rank == PhaseRank(phase, "") && ticks >= bestTicks && rank > 0))
            {
                if (rank > 0 || ticks >= bestTicks)
                {
                    phase = string.IsNullOrEmpty(p) ? "idle" : p;
                    round = Math.Max(1, st.CurrentRound);
                    if (st.MaxRounds > 0) maxRounds = st.MaxRounds;
                    bestTicks = ticks;
                    bestTask = st.TaskSummary;
                    if (!string.Equals(st.AgentId, RoutingService.ResolveMasterPlanner(_reg, _reg.GetActiveProject())?.Id, StringComparison.OrdinalIgnoreCase))
                        activeWorker = st.AgentId;
                    else if (phase is "executing" or "dispatching" or "reworking")
                        activeWorker = st.AgentId;
                }
            }
        }

        // 活跃工作优先：working 中的最高阶段
        var working = liveMap.Values
            .Where(v => v != null && (v.Status == "working" || v.Status == "reworking" ||
                                      string.Equals(v.WorkflowPhase, "reworking", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(v.WorkflowPhase, "master_verifying", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(v.WorkflowPhase, "master_analyzing", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(v.WorkflowPhase, "dispatching", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(v.WorkflowPhase, "executing", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(v => PhaseRank(v.WorkflowPhase, v.Status))
            .ThenByDescending(v => v.UpdatedUtcTicks)
            .FirstOrDefault();
        if (working != null)
        {
            phase = string.IsNullOrWhiteSpace(working.WorkflowPhase) ? "executing" : working.WorkflowPhase.Trim().ToLowerInvariant();
            round = Math.Max(1, working.CurrentRound);
            if (working.MaxRounds > 0) maxRounds = working.MaxRounds;
            bestTask = working.TaskSummary;
            var masterId = RoutingService.ResolveMasterPlanner(_reg, _reg.GetActiveProject())?.Id;
            if (!string.Equals(working.AgentId, masterId, StringComparison.OrdinalIgnoreCase) ||
                phase is "executing" or "dispatching" or "reworking")
                activeWorker = working.AgentId;
        }
        else
        {
            var delivered = liveMap.Values
                .Where(v => v != null && (v.Status == "done" ||
                                          string.Equals(v.WorkflowPhase, "verified_delivered", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(v => v.UpdatedUtcTicks)
                .FirstOrDefault();
            if (delivered != null && delivered.UpdatedUtcTicks > 0 &&
                (DateTime.UtcNow - new DateTime(delivered.UpdatedUtcTicks, DateTimeKind.Utc)).TotalMinutes < 30)
            {
                phase = "verified_delivered";
                bestTask = delivered.TaskSummary;
                round = Math.Max(1, delivered.CurrentRound);
                if (delivered.MaxRounds > 0) maxRounds = delivered.MaxRounds;
            }
            else if (phase != "paused")
            {
                phase = "idle";
                bestTask = null;
            }
        }

        ApplyWorkflowDiagramPhase(phase, round, maxRounds, activeWorker, bestTask);
    }

    private static int PhaseRank(string? phase, string? status)
    {
        string p = (phase ?? "").Trim().ToLowerInvariant();
        string s = (status ?? "").Trim().ToLowerInvariant();
        if (p == "reworking" || s == "reworking") return 60;
        if (p == "master_verifying") return 50;
        if (p == "executing" || s == "working") return 40;
        if (p == "dispatching") return 30;
        if (p == "master_analyzing") return 20;
        if (p == "verified_delivered" || s == "done") return 10;
        if (p == "paused") return 5;
        return 0;
    }

    private void ApplyDemoPhaseStep()
    {
        string phase = WorkflowDiagramDemoPhases[_workflowDiagramDemoStep % WorkflowDiagramDemoPhases.Length];
        var workers = ResolveWorkflowDiagramWorkers();
        string? wid = workers.Count > 0 ? workers[_workflowDiagramDemoStep % workers.Count].Id : "cursor";
        int maxRounds = Math.Clamp(_reg.UserConfig.MaxReworkRounds + 1, 1, 4);
        int round = phase == "reworking" ? Math.Min(maxRounds, 2) : 1;
        string task = phase switch
        {
            "master_analyzing" => "演示：主脑深度拆解任务与路由",
            "dispatching" => "演示：派发实操执行任务",
            "executing" => "演示：执行 AI 正在改工程",
            "master_verifying" => "演示：主脑 Diff/编译/实测验收",
            "reworking" => "演示：验收未通过，打回精修",
            "verified_delivered" => "演示：终验通过，达标交付",
            _ => "演示流转"
        };
        ApplyWorkflowDiagramPhase(phase, round, maxRounds, wid, task);
    }

    private void ApplyWorkflowDiagramPhase(
        string phase,
        int round,
        int maxRounds,
        string? activeWorkerId,
        string? taskSummary = null)
    {
        _workflowDiagramPhase = (phase ?? "idle").Trim().ToLowerInvariant();
        _workflowDiagramRound = Math.Max(1, round);
        _workflowDiagramMaxRounds = Math.Max(1, maxRounds);
        _workflowDiagramActiveWorkerId = activeWorkerId;

        _workflowDiagramPhaseLabel = _workflowDiagramPhase switch
        {
            "master_analyzing" => "主脑深度分析",
            "dispatching" => "派发实操执行",
            "executing" => "实操编码中",
            "master_verifying" => "主脑严格验收",
            "reworking" => $"打回精修 R{_workflowDiagramRound}/{_workflowDiagramMaxRounds}",
            "verified_delivered" => "达标交付",
            "paused" => "工作流已暂停",
            _ => "空闲待命"
        };

        string brief = AutoDispatchService.ExtractCleanTaskBrief(taskSummary ?? "", 18);
        _workflowDiagramSubFull = _workflowDiagramPhase switch
        {
            "paused" => "协同已关闭",
            "idle" => string.IsNullOrWhiteSpace(brief) ? "等待接单" : brief,
            "master_analyzing" => string.IsNullOrWhiteSpace(brief) ? "主脑分析中" : brief,
            "dispatching" => string.IsNullOrWhiteSpace(brief) ? "正在派发" : brief,
            "executing" => string.IsNullOrWhiteSpace(brief) ? "正在改工程" : brief,
            "master_verifying" => string.IsNullOrWhiteSpace(brief) ? "主脑验收中" : brief,
            "reworking" => string.IsNullOrWhiteSpace(brief)
                ? $"打回精修 R{_workflowDiagramRound}/{_workflowDiagramMaxRounds}"
                : $"{brief} · R{_workflowDiagramRound}",
            "verified_delivered" => string.IsNullOrWhiteSpace(brief) ? "已交付" : brief,
            _ => brief
        };
    }

    private List<AgentRosterEntry> ResolveWorkflowDiagramWorkers()
    {
        var proj = _reg.GetActiveProject();
        var master = RoutingService.ResolveMasterPlanner(_reg, proj);
        var enabled = _reg.UserConfig.Agents.Where(a => a.Enabled).ToList();
        // 主脑不重复列入执行阵列；只剩主脑一人时它兼任执行
        var workers = enabled
            .Where(a => master == null || !string.Equals(a.Id, master.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (workers.Count == 0) workers = enabled;
        return workers
            .OrderByDescending(a => a.Detected)
            .ThenBy(a => a.Name)
            .ToList();
    }

    private void RebuildWorkflowDiagramCanvas()
    {
        if (WorkflowDiagramCanvas == null) return;

        var proj = _reg.GetActiveProject();
        var master = RoutingService.ResolveMasterPlanner(_reg, proj);
        var workers = ResolveWorkflowDiagramWorkers();
        _workflowDiagramMasterId = master?.Id ?? "";
        _workflowDiagramWorkerIds.Clear();
        _workflowDiagramWorkerIds.AddRange(workers.Select(w => w.Id));

        int masterCount = master != null && master.Enabled ? 1 : 0;
        if (WorkflowDiagramRosterSummary != null)
            WorkflowDiagramRosterSummary.Text = $"主脑 {masterCount} · 执行 {workers.Count}";

        if (WorkflowDiagramPhaseBadge != null)
        {
            var (bg, border, fg) = PhaseBadgeColors(_workflowDiagramPhase);
            WorkflowDiagramPhaseBadge.Background = Hex(bg);
            WorkflowDiagramPhaseBadge.BorderBrush = Hex(border);
            if (WorkflowDiagramPhaseBadgeText != null)
            {
                WorkflowDiagramPhaseBadgeText.Text = _workflowDiagramPhaseLabel;
                WorkflowDiagramPhaseBadgeText.Foreground = Hex(fg);
            }
        }
        else if (WorkflowDiagramPhaseBadgeText != null)
        {
            WorkflowDiagramPhaseBadgeText.Text = _workflowDiagramPhaseLabel;
        }
        if (WorkflowDiagramSubText != null)
        {
            WorkflowDiagramSubText.Text = _workflowDiagramSubFull;
            WorkflowDiagramSubText.ToolTip = _workflowDiagramSubFull;
        }
        if (WorkflowDiagramStatusDot != null)
        {
            WorkflowDiagramStatusDot.Fill = Hex(_workflowDiagramPhase switch
            {
                "paused" => "#64748B",
                "idle" => "#34D399",
                "reworking" => "#F97316",
                "verified_delivered" => "#10B981",
                "master_verifying" => "#06B6D4",
                "executing" or "dispatching" => "#F59E0B",
                _ => "#818CF8"
            });
        }

        bool paused = !_workflowDiagramDemoMode &&
                      (_workflowDiagramPhase == "paused" || !_reg.UserConfig.WorkflowEngineEnabled);
        if (WorkflowDiagramCard != null)
            WorkflowDiagramCard.Opacity = paused ? 0.72 : 1.0;

        WorkflowDiagramCanvas.Children.Clear();
        _workflowDiagramNodeBorders.Clear();
        _workflowDiagramActiveFlowPath = null;
        _workflowDiagramReworkPath = null;
        _workflowDiagramPulseDot = null;

        double w = WorkflowDiagramCanvas.ActualWidth;
        if (w < 40) w = Math.Max(40, ActualWidth - 80);
        double h = WorkflowDiagramCanvas.ActualHeight;
        if (h < 40) h = 158;

        double padX = 10;
        double usable = Math.Max(200, w - padX * 2);
        double nodeW = Math.Clamp(usable / 5.35, 96, 168);
        double gap = Math.Max(10, (usable - nodeW * 5) / 4.0);
        double y = 10;
        double nodeH = 86;
        double[] xs = new double[5];
        for (int i = 0; i < 5; i++)
            xs[i] = padX + i * (nodeW + gap);

        string projShort = string.IsNullOrWhiteSpace(proj?.Name) ? (proj?.Id ?? "—") : proj!.Name;
        if (projShort.Length > 11) projShort = projShort[..10] + "…";
        // Prefer folder leaf for XX-style roots
        if (!string.IsNullOrWhiteSpace(proj?.PrimaryRoot))
        {
            try
            {
                var leaf = System.IO.Path.GetFileName(proj.PrimaryRoot.TrimEnd('\\', '/'));
                if (!string.IsNullOrWhiteSpace(leaf)) projShort = leaf.Length > 11 ? leaf[..10] + "…" : leaf;
            }
            catch { }
        }

        var liveMap = AutoDispatchService.ReadMergedGlobalWorkStatesWithDesktopLive();
        string intakeState = _workflowDiagramPhase == "idle" || _workflowDiagramPhase == "paused"
            ? (_workflowDiagramPhase == "paused" ? "已暂停" : "待命接单")
            : "接单流转中";

        // ① 全入口接单
        AddDiagramNode(0, xs[0], y, nodeW, nodeH,
            title: "① 全入口接单",
            body: $"{projShort}\n{intakeState}",
            tip: $"活跃工程：{proj?.Name ?? "未选择"}\n路径：{proj?.PrimaryRoot ?? "—"}\n状态：{intakeState}",
            accent: ActiveAccentForNode(0),
            onClick: () => NavDialogs.IsChecked = true);

        // ② 主脑统筹分析
        string masterName = master?.Name ?? "主脑";
        string masterTier = master != null
            ? RoutingService.EffortTierVisualLabel(master.PreferredEffortTier)
            : "—";
        var masterVisual = master != null
            ? OfficialBrandIconService.CreateCircularAvatarVisual(master.Id, 22, master, OfficialBrandIconService.GetBrandColor(master.Id), 1.15)
            : null;
        AddDiagramNode(1, xs[1], y, nodeW, nodeH,
            title: "② 主脑统筹",
            body: $"{ShortName(masterName, 14)}\n{masterTier}",
            tip: $"主脑：{masterName}\n模型档位：{masterTier}\n职责：深度分析 / 派单 / 终验\n当前阶段：{_workflowDiagramPhaseLabel} · 轮次 R{_workflowDiagramRound}/{_workflowDiagramMaxRounds}\n点击跳转「对话与派单」",
            accent: ActiveAccentForNode(1),
            avatar: masterVisual,
            onClick: () => NavDialogs.IsChecked = true);

        // ③ 实操执行阵列
        AddWorkerArrayNode(2, xs[2], y, nodeW, nodeH, workers, liveMap);

        // ④ 主脑严格验收
        AddDiagramNode(3, xs[3], y, nodeW, nodeH,
            title: "④ 严格验收",
            body: "Diff·编译·实测",
            tip: "主脑验收闸门：Diff 落盘核对 / 编译构建 / 实测画面与行为",
            accent: ActiveAccentForNode(3),
            onClick: () => NavDialogs.IsChecked = true);

        // ⑤ 达标交付
        string deliverBody = _workflowDiagramPhase == "verified_delivered"
            ? AutoDispatchService.ExtractCleanTaskBrief(_workflowDiagramSubFull, 14)
            : "终验通过";
        AddDiagramNode(4, xs[4], y, nodeW, nodeH,
            title: "⑤ 达标交付",
            body: deliverBody,
            tip: $"交付摘要：{_workflowDiagramSubFull}",
            accent: ActiveAccentForNode(4),
            onClick: () => NavDialogs.IsChecked = true);

        // Forward edges ①→②→③→④→⑤
        int activeEdge = ActiveEdgeIndex();
        for (int i = 0; i < 4; i++)
        {
            double x1 = xs[i] + nodeW;
            double x2 = xs[i + 1];
            double midY = y + nodeH * 0.45;
            bool active = !paused && activeEdge == i;
            var path = DrawFlowEdge(x1, midY, x2, midY, active, reverse: false);
            if (active) _workflowDiagramActiveFlowPath = path;
        }

        // Rework loop ④ → ③ (bottom arc)
        double reworkY = y + nodeH + 8;
        double rx1 = xs[3] + nodeW * 0.45;
        double rx2 = xs[2] + nodeW * 0.55;
        bool reworkActive = !paused && _workflowDiagramPhase == "reworking";
        var reworkPath = DrawReworkArc(rx1, y + nodeH, rx2, y + nodeH, reworkY + 18, reworkActive);
        _workflowDiagramReworkPath = reworkPath;

        string reworkLabel = reworkActive
            ? $"打回精修 R{_workflowDiagramRound}/{_workflowDiagramMaxRounds}"
            : $"打回精修 (≤{_workflowDiagramMaxRounds}轮)";
        var reworkTb = new TextBlock
        {
            Text = reworkLabel,
            FontSize = 10.5,
            FontWeight = reworkActive ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Hex(reworkActive ? "#FB923C" : "#64748B"),
            ToolTip = "验收未通过时从④回环至③继续精修，轮次受配置方案上限约束"
        };
        WorkflowDiagramCanvas.Children.Add(reworkTb);
        double labelX = (rx1 + rx2) / 2 - 48;
        Canvas.SetLeft(reworkTb, Math.Max(xs[2], labelX));
        Canvas.SetTop(reworkTb, reworkY + 20);

        // Pulse dot on active edge
        if (!paused && (_workflowDiagramPhase is not "idle" and not "paused" || _workflowDiagramDemoMode))
        {
            string glow = reworkActive ? "#F97316" : "#818CF8";
            _workflowDiagramPulseDot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Hex(reworkActive ? "#FB923C" : "#A5B4FC"),
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString(glow),
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = 0.9
                }
            };
            WorkflowDiagramCanvas.Children.Add(_workflowDiagramPulseDot);
            Canvas.SetLeft(_workflowDiagramPulseDot, -20);
            Canvas.SetTop(_workflowDiagramPulseDot, -20);
        }

        _workflowDiagramLayoutSig =
            $"{_workflowDiagramPhase}|{_workflowDiagramRound}|{_workflowDiagramMaxRounds}|{_workflowDiagramActiveWorkerId}|{_reg.UserConfig.WorkflowEngineEnabled}|{string.Join(",", _workflowDiagramWorkerIds)}|{_workflowDiagramDemoMode}";
    }

    private (string Bg, string Border, string Fg) PhaseBadgeColors(string phase) => phase switch
    {
        "paused" => ("#1E293B", "#475569", "#94A3B8"),
        "idle" => ("#12352C", "#1D6B55", "#A7F3D0"),
        "master_analyzing" => ("#312E81", "#6366F1", "#C7D2FE"),
        "dispatching" => ("#312E81", "#818CF8", "#E0E7FF"),
        "executing" => ("#7C2D12", "#F59E0B", "#FDE68A"),
        "master_verifying" => ("#164E63", "#06B6D4", "#A5F3FC"),
        "reworking" => ("#7C2D12", "#F97316", "#FED7AA"),
        "verified_delivered" => ("#064E3B", "#10B981", "#A7F3D0"),
        _ => ("#1E293B", "#334155", "#94A3B8")
    };

    private string? ActiveAccentForNode(int nodeIndex)
    {
        if (_workflowDiagramPhase == "paused" && !_workflowDiagramDemoMode) return null;
        return (_workflowDiagramPhase, nodeIndex) switch
        {
            ("master_analyzing", 1) => "#6366F1",
            ("dispatching", 1) or ("dispatching", 2) => "#6366F1",
            ("executing", 2) => "#F59E0B",
            ("reworking", 2) => "#F97316",
            ("master_verifying", 3) => "#06B6D4",
            ("verified_delivered", 4) => "#10B981",
            ("idle", 0) => "#34D399",
            _ => null
        };
    }

    private int ActiveEdgeIndex() => _workflowDiagramPhase switch
    {
        "master_analyzing" => 0,
        "dispatching" => 1,
        "executing" or "reworking" => 2,
        "master_verifying" => 3,
        "verified_delivered" => 3,
        _ => -1
    };

    private void AddWorkerArrayNode(
        int index,
        double x,
        double y,
        double nodeW,
        double nodeH,
        List<AgentRosterEntry> workers,
        Dictionary<string, LiveAgentWorkState> liveMap)
    {
        var panel = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = "③ 实操执行",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Hex("#E2E8F0"),
            Margin = new Thickness(0, 0, 0, 4)
        });

        var tip = new StringBuilder();
        tip.AppendLine($"工作流已启用执行 AI：{workers.Count}");
        const int maxShow = 3;
        var show = workers.Take(maxShow).ToList();
        var overflow = workers.Skip(maxShow).ToList();

        if (workers.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "暂无启用执行 AI",
                FontSize = 10.5,
                Foreground = Hex("#64748B")
            });
            tip.Append("请在「工作流开关」或「AI 阵容与路由」启用具备直改工程权限的工具");
        }
        else
        {
            LiveAgentWorkState? LiveOf(AgentRosterEntry a)
            {
                liveMap.TryGetValue(a.Id, out var lv);
                return lv ?? liveMap.Values.FirstOrDefault(v =>
                    string.Equals(v.AgentId, a.Id, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var w in workers)
                tip.AppendLine($"· {DescribeWorkerTip(w, LiveOf(w))}");
            tip.Append("点击跳转「AI 阵容与路由」");

            if (workers.Count <= 3)
            {
                foreach (var w in show)
                    panel.Children.Add(BuildMiniWorkerChip(w, LiveOf(w)));
            }
            else
            {
                // 双列紧凑：恰好 4 个全部展示，更多时前 3 个 + 溢出胶囊
                var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
                var visible = workers.Count == 4 ? workers : show;
                foreach (var w in visible)
                    grid.Children.Add(BuildMiniWorkerChip(w, LiveOf(w), compact: true));
                if (workers.Count > 4)
                {
                    grid.Children.Add(new Border
                    {
                        Background = Hex("#1E293B"),
                        BorderBrush = Hex("#334155"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(6, 0, 6, 1),
                        Margin = new Thickness(0, 1, 2, 1),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = $"+{overflow.Count} 项",
                            FontSize = 10,
                            Foreground = Hex("#94A3B8")
                        },
                        ToolTip = BuildDarkTip(string.Join("\n", overflow.Select(o => DescribeWorkerTip(o, LiveOf(o)))))
                    });
                }
                panel.Children.Add(grid);
            }
        }

        bool workerHot = _workflowDiagramPhase is "executing" or "dispatching" or "reworking";
        string? accent = ActiveAccentForNode(index);
        if (workerHot && !string.IsNullOrWhiteSpace(_workflowDiagramActiveWorkerId))
            accent = _workflowDiagramPhase == "reworking" ? "#F97316" : "#F59E0B";

        var border = new Border
        {
            Width = nodeW,
            Height = nodeH,
            Background = Hex("#121824"),
            BorderBrush = Hex(accent ?? "#24324B"),
            BorderThickness = new Thickness(accent != null ? 1.6 : 1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
            Cursor = Cursors.Hand,
            ToolTip = BuildDarkTip(tip.ToString().TrimEnd())
        };
        border.MouseLeftButtonUp += (_, _) => NavSettings.IsChecked = true;
        _workflowDiagramNodeBorders.Add(border);
        WorkflowDiagramCanvas.Children.Add(border);
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);
    }

    private FrameworkElement BuildMiniWorkerChip(AgentRosterEntry agent, LiveAgentWorkState? live, bool compact = false)
    {
        bool hot = live != null && (
            string.Equals(live.WorkflowPhase, "executing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(live.WorkflowPhase, "dispatching", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(live.WorkflowPhase, "reworking", StringComparison.OrdinalIgnoreCase) ||
            live.Status == "working" || live.Status == "reworking" ||
            string.Equals(agent.Id, _workflowDiagramActiveWorkerId, StringComparison.OrdinalIgnoreCase));

        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, compact ? 2 : 0, 2),
            ToolTip = BuildDarkTip(DescribeWorkerTip(agent, live))
        };
        var avatar = OfficialBrandIconService.CreateCircularAvatarVisual(
            agent.Id, compact ? 16 : 18, agent, OfficialBrandIconService.GetBrandColor(agent.Id), 1.0);
        avatar.Margin = new Thickness(0, 0, 4, 0);
        sp.Children.Add(avatar);

        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Hex(hot ? "#F59E0B" : (live?.Status == "done" ? "#10B981" : "#64748B")),
            Margin = new Thickness(0, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        sp.Children.Add(dot);
        sp.Children.Add(new TextBlock
        {
            Text = ShortName(agent.Name, compact ? 5 : 9),
            FontSize = 10,
            Foreground = Hex(hot ? "#FDE68A" : "#CBD5E1"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return sp;
    }

    private string DescribeWorkerTip(AgentRosterEntry agent, LiveAgentWorkState? live)
    {
        string tier = !string.IsNullOrWhiteSpace(live?.SelectedTierBadge)
            ? live!.SelectedTierBadge
            : RoutingService.EffortTierVisualLabel(agent.PreferredEffortTier);
        var sb = new StringBuilder($"{agent.Name} · 执行 AI · {tier}");
        if (!string.IsNullOrWhiteSpace(live?.SelectedModel)) sb.Append($" · {live!.SelectedModel}");
        sb.Append($" · {DescribeWorkerStatus(live)}");
        if (live != null)
        {
            sb.Append($" · R{Math.Max(1, live.CurrentRound)}/{Math.Max(1, live.MaxRounds)}");
            if (DateTime.TryParse(live.StartedAt, out var started) &&
                (live.Status == "working" || live.Status == "reworking"))
            {
                var span = DateTime.Now - started;
                if (span.TotalSeconds > 0)
                    sb.Append(span.TotalMinutes >= 1 ? $" · 已耗时 {(int)span.TotalMinutes} 分" : $" · 已耗时 {(int)span.TotalSeconds} 秒");
            }
        }
        return sb.ToString();
    }

    private static string DescribeWorkerStatus(LiveAgentWorkState? live)
    {
        if (live == null) return "待命";
        string p = (live.WorkflowPhase ?? "").ToLowerInvariant();
        if (p is "executing" or "dispatching" || live.Status == "working") return "实操中";
        if (p == "reworking" || live.Status == "reworking") return "精修中";
        if (p == "master_verifying") return "待验收";
        if (p == "verified_delivered" || live.Status == "done") return "已交付";
        return live.Status ?? "待命";
    }

    private void AddDiagramNode(
        int index,
        double x,
        double y,
        double nodeW,
        double nodeH,
        string title,
        string body,
        string tip,
        string? accent,
        FrameworkElement? avatar = null,
        Action? onClick = null)
    {
        var panel = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        if (avatar != null)
        {
            avatar.Margin = new Thickness(0, 0, 5, 0);
            avatar.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(avatar);
        }
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Hex("#E2E8F0"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(titleRow);
        panel.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 10.5,
            Foreground = Hex("#94A3B8"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 14,
            MaxHeight = 40,
            MaxWidth = Math.Max(40, nodeW - 16),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var border = new Border
        {
            Width = nodeW,
            Height = nodeH,
            Background = Hex("#121824"),
            BorderBrush = Hex(accent ?? "#24324B"),
            BorderThickness = new Thickness(accent != null ? 1.6 : 1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
            Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow,
            ToolTip = BuildDarkTip(tip)
        };
        if (onClick != null)
            border.MouseLeftButtonUp += (_, _) => onClick();
        _workflowDiagramNodeBorders.Add(border);
        WorkflowDiagramCanvas.Children.Add(border);
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);
    }

    private Path DrawFlowEdge(double x1, double y1, double x2, double y2, bool active, bool reverse)
    {
        double cx = (x1 + x2) / 2;
        var geo = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(x1 + 2, y1), IsClosed = false };
        fig.Segments.Add(new BezierSegment(
            new Point(cx - 8, y1),
            new Point(cx + 8, y2),
            new Point(x2 - 10, y2),
            true));
        geo.Figures.Add(fig);

        var path = new Path
        {
            Data = geo,
            Stroke = Hex(active ? "#818CF8" : "#334155"),
            StrokeThickness = active ? 1.8 : 1.2,
            StrokeDashArray = active ? new DoubleCollection { 4, 3 } : new DoubleCollection { 2, 4 },
            Opacity = active ? 1.0 : 0.75
        };
        WorkflowDiagramCanvas.Children.Add(path);

        // Arrow head
        var arrow = new Polygon
        {
            Points = new PointCollection
            {
                new Point(x2 - 2, y2),
                new Point(x2 - 9, y2 - 4),
                new Point(x2 - 9, y2 + 4)
            },
            Fill = Hex(active ? "#A5B4FC" : "#475569")
        };
        WorkflowDiagramCanvas.Children.Add(arrow);
        return path;
    }

    private Path DrawReworkArc(double xFrom, double yFrom, double xTo, double yTo, double dipY, bool active)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(xFrom, yFrom), IsClosed = false };
        fig.Segments.Add(new BezierSegment(
            new Point(xFrom + 12, dipY),
            new Point(xTo - 12, dipY),
            new Point(xTo, yTo),
            true));
        geo.Figures.Add(fig);
        var path = new Path
        {
            Data = geo,
            Stroke = Hex(active ? "#F97316" : "#3F3F46"),
            StrokeThickness = active ? 1.8 : 1.15,
            StrokeDashArray = active ? new DoubleCollection { 4, 3 } : new DoubleCollection { 3, 5 },
            Opacity = active ? 1.0 : 0.7
        };
        WorkflowDiagramCanvas.Children.Add(path);

        var arrow = new Polygon
        {
            Points = new PointCollection
            {
                new Point(xTo, yTo),
                new Point(xTo + 6, yTo + 7),
                new Point(xTo - 5, yTo + 5)
            },
            Fill = Hex(active ? "#FB923C" : "#52525B")
        };
        WorkflowDiagramCanvas.Children.Add(arrow);
        return path;
    }

    private static ToolTip BuildDarkTip(string text) => new()
    {
        Content = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Foreground = HexStatic("#E2E8F0"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320
        },
        Background = HexStatic("#0F172A"),
        BorderBrush = HexStatic("#334155"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 8, 10, 8)
    };

    private static Brush HexStatic(string h) => UiBrushPool.Get(h);

    private static string ShortName(string name, int max)
    {
        if (string.IsNullOrWhiteSpace(name)) return "—";
        name = name.Trim();
        return name.Length <= max ? name : name[..Math.Max(1, max - 1)] + "…";
    }

    private void UpdateWorkflowDiagramAnimationGate()
    {
        bool shouldRun =
            !AppPaths.IsIsolatedTest &&
            IsLoaded &&
            IsVisible &&
            WindowState != WindowState.Minimized &&
            NavOverview.IsChecked == true &&
            (_workflowDiagramDemoMode ||
             (_reg.UserConfig.WorkflowEngineEnabled &&
              _workflowDiagramPhase is not "idle" and not "paused") ||
             _workflowDiagramPreviewOverride);

        if (shouldRun)
            StartWorkflowDiagramAnimTimer();
        else
            StopWorkflowDiagramAnimTimer();
    }

    private void StartWorkflowDiagramAnimTimer()
    {
        if (_workflowDiagramAnimTimer != null) return;
        _workflowDiagramAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(55) };
        _workflowDiagramAnimTimer.Tick += WorkflowDiagramAnimTimer_Tick;
        _workflowDiagramAnimTimer.Start();
    }

    private void StopWorkflowDiagramAnimTimer()
    {
        if (_workflowDiagramAnimTimer == null) return;
        _workflowDiagramAnimTimer.Stop();
        _workflowDiagramAnimTimer.Tick -= WorkflowDiagramAnimTimer_Tick;
        _workflowDiagramAnimTimer = null;
    }

    private void WorkflowDiagramAnimTimer_Tick(object? sender, EventArgs e)
    {
        if (AppPaths.IsIsolatedTest || !IsVisible || WindowState == WindowState.Minimized ||
            NavOverview.IsChecked != true)
        {
            StopWorkflowDiagramAnimTimer();
            return;
        }

        _workflowDiagramDashOffset -= 0.55;
        if (_workflowDiagramDashOffset < -40) _workflowDiagramDashOffset = 0;
        _workflowDiagramPulseT += 0.055;

        if (_workflowDiagramActiveFlowPath != null)
            _workflowDiagramActiveFlowPath.StrokeDashOffset = _workflowDiagramDashOffset;
        if (_workflowDiagramReworkPath != null && _workflowDiagramPhase == "reworking")
            _workflowDiagramReworkPath.StrokeDashOffset = -_workflowDiagramDashOffset;

        // Breathing status dot
        if (WorkflowDiagramStatusDot != null)
        {
            double breathe = 0.72 + 0.28 * (0.5 + 0.5 * Math.Sin(_workflowDiagramPulseT * 2.2));
            WorkflowDiagramStatusDot.Opacity = breathe;
        }

        // Pulse border opacity on active nodes
        foreach (var b in _workflowDiagramNodeBorders)
        {
            if (b.BorderThickness.Left >= 1.5)
            {
                double pulse = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(_workflowDiagramPulseT * 3.0));
                b.Opacity = 0.82 + 0.18 * pulse;
            }
        }

        if (_workflowDiagramPulseDot != null && WorkflowDiagramCanvas != null)
        {
            double w = WorkflowDiagramCanvas.ActualWidth;
            if (w < 40) w = Math.Max(40, ActualWidth - 80);
            double padX = 10;
            double usable = Math.Max(200, w - padX * 2);
            double nodeW = Math.Clamp(usable / 5.35, 96, 168);
            double gap = Math.Max(10, (usable - nodeW * 5) / 4.0);
            double yMid = 10 + 86 * 0.45 - 4;
            if (_workflowDiagramPhase == "reworking")
            {
                double xs3 = padX + 3 * (nodeW + gap);
                double xs2 = padX + 2 * (nodeW + gap);
                double t = (_workflowDiagramPulseT * 0.35) % 1.0;
                double x = xs3 + nodeW * 0.45 + (xs2 + nodeW * 0.55 - (xs3 + nodeW * 0.45)) * t;
                double baseY = 10 + 86;
                double dip = baseY + 26;
                double yy = baseY + Math.Sin(t * Math.PI) * (dip - baseY);
                Canvas.SetLeft(_workflowDiagramPulseDot, x);
                Canvas.SetTop(_workflowDiagramPulseDot, yy);
            }
            else
            {
                int edge = ActiveEdgeIndex();
                if (edge >= 0)
                {
                    double x1 = padX + edge * (nodeW + gap) + nodeW;
                    double x2 = padX + (edge + 1) * (nodeW + gap);
                    double t = (_workflowDiagramPulseT * 0.4) % 1.0;
                    Canvas.SetLeft(_workflowDiagramPulseDot, x1 + (x2 - x1) * t);
                    Canvas.SetTop(_workflowDiagramPulseDot, yMid);
                }
            }
        }

        if (_workflowDiagramDemoMode)
        {
            double elapsed = (DateTime.UtcNow - _workflowDiagramDemoStartUtc).TotalSeconds;
            int step = (int)(elapsed / WorkflowDiagramDemoStepSeconds) % WorkflowDiagramDemoPhases.Length;
            if (step != _workflowDiagramDemoStep)
            {
                _workflowDiagramDemoStep = step;
                ApplyDemoPhaseStep();
                RebuildWorkflowDiagramCanvas();
            }
        }
    }

    private void SyncWorkflowDiagramAfterOverviewRender()
    {
        EnsureWorkflowDiagramHooks();
        if (!_workflowDiagramDemoMode && !_workflowDiagramPreviewOverride)
            ResolveWorkflowDiagramLivePhase();

        var workers = ResolveWorkflowDiagramWorkers();
        string sig = $"{_workflowDiagramPhase}|{_workflowDiagramRound}|{_workflowDiagramMaxRounds}|{_workflowDiagramActiveWorkerId}|{_reg.UserConfig.WorkflowEngineEnabled}|{string.Join(",", workers.Select(w => w.Id))}|{_workflowDiagramDemoMode}";
        if (!string.Equals(sig, _workflowDiagramLayoutSig, StringComparison.Ordinal) ||
            WorkflowDiagramCanvas?.Children.Count == 0)
        {
            _workflowDiagramLayoutSig = sig;
            RebuildWorkflowDiagramCanvas();
        }
        UpdateWorkflowDiagramAnimationGate();
    }
}
