using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    private double _workflowDiagramPulseT = 0.38;
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
    private Border? _workflowDiagramMasterGlow;
    private Ellipse? _workflowDiagramAurora;
    private readonly List<Border> _workflowDiagramNodeBorders = new();
    private readonly List<MindMapFlowTrack> _workflowDiagramFlowTracks = new();

    private sealed class MindMapFlowTrack
    {
        public PathGeometry Geometry = null!;
        public Ellipse Core = null!;
        public Ellipse Tail1 = null!;
        public Ellipse Tail2 = null!;
        public bool Reverse;
        public double Speed = 0.38;
        public double PhaseOffset;
    }

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
        SyncWorkflowDiagramDemoButton();
        ApplyWorkflowDiagramPhase(phase, round, maxRounds, activeWorkerId);
        RebuildWorkflowDiagramCanvas();
        UpdateWorkflowDiagramAnimationGate();
    }

    /// <summary>清除截图/测试用预览阶段，恢复真实工作流状态。</summary>
    public void ClearWorkflowDiagramPreviewPhaseForTest()
    {
        _workflowDiagramPreviewOverride = false;
        _workflowDiagramDemoMode = false;
        SyncWorkflowDiagramDemoButton();
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

    private void SyncWorkflowDiagramDemoButton()
    {
        if (WorkflowDiagramDemoBtn == null) return;
        bool running = _workflowDiagramDemoMode;
        WorkflowDiagramDemoBtn.ToolTip = running
            ? "停止演示，回到真实工作流状态"
            : "空闲态也可循环演示六阶段流光动画；再点一次停止并回到真实状态";
        WorkflowDiagramDemoBtn.Style = (Style)FindResource(running ? "IconToolBtnActive" : "IconToolBtn");
        if (WorkflowDiagramDemoIcon == null) return;
        WorkflowDiagramDemoIcon.Data = Geometry.Parse(running ? "M7,7 H17 V17 H7 Z" : "M8,5 L18,12 L8,19 Z");
        var brush = running ? Hex("#FDE68A") : Hex("#E6EDF7");
        WorkflowDiagramDemoIcon.Stroke = brush;
        WorkflowDiagramDemoIcon.Fill = running ? brush : Brushes.Transparent;
    }

    private void ToggleWorkflowDiagramDemoInternal()
    {
        _workflowDiagramDemoMode = !_workflowDiagramDemoMode;
        _workflowDiagramPreviewOverride = false;
        if (_workflowDiagramDemoMode)
        {
            _workflowDiagramDemoStep = 0;
            _workflowDiagramDemoStartUtc = DateTime.UtcNow;
            ApplyDemoPhaseStep();
            SyncWorkflowDiagramDemoButton();
        }
        else
        {
            SyncWorkflowDiagramDemoButton();
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
        _workflowDiagramFlowTracks.Clear();
        _workflowDiagramMasterGlow = null;
        _workflowDiagramAurora = null;

        double w = WorkflowDiagramCanvas.ActualWidth;
        if (w < 40) w = Math.Max(40, ActualWidth - 80);
        double h = WorkflowDiagramCanvas.ActualHeight;
        if (h < 40) h = 226;

        double cx = w * 0.5;
        double cy = h * 0.5;

        DrawMindMapStarGrid(w, h);
        DrawMindMapAurora(cx, cy);

        const double masterW = 192, masterH = 82;
        const double sideW = 164, sideH = 56;
        const double workerW = 180, workerH = 52;
        const double leftX = 0.16;
        const double rightX = 0.84;

        string projShort = ResolveProjectShortName(proj);
        string intakeSub = _workflowDiagramPhase == "paused"
            ? "已暂停"
            : (_workflowDiagramPhase == "idle" ? $"工程 · {projShort}" : "接单流转中");
        string verifySub = _workflowDiagramPhase switch
        {
            "reworking" => $"打回 R{_workflowDiagramRound}/{_workflowDiagramMaxRounds}",
            "verified_delivered" => "达标交付",
            "master_verifying" => "Diff·编译·实测",
            _ => "Diff·编译·实测"
        };

        // 左侧：需求入口 / 验收交付
        var intakeNode = AddMindMapSideNode(
            tag: "MindMapNode:intake",
            centerX: w * leftX,
            centerY: cy - 42,
            width: sideW,
            height: sideH,
            title: "需求入口",
            subtitle: intakeSub,
            tip: $"活跃工程：{proj?.Name ?? "未选择"}\n路径：{proj?.PrimaryRoot ?? "—"}\n状态：{intakeSub}",
            iconKind: "intake",
            accent: SideAccent("intake"),
            anchorOnRight: true,
            onClick: () => NavDialogs.IsChecked = true);

        var verifyNode = AddMindMapSideNode(
            tag: "MindMapNode:verify",
            centerX: w * leftX,
            centerY: cy + 42,
            width: sideW,
            height: sideH,
            title: "验收交付",
            subtitle: verifySub,
            tip: $"验收闸门：Diff / 编译 / 实测\n阶段：{_workflowDiagramPhaseLabel}",
            iconKind: "verify",
            accent: SideAccent("verify"),
            anchorOnRight: true,
            onClick: () => NavDialogs.IsChecked = true);

        // 右侧：执行 AI（最多 4，超出收敛 +N）
        var liveMap = AutoDispatchService.ReadMergedGlobalWorkStatesWithDesktopLive();
        var displayWorkers = BuildDisplayWorkers(workers);
        int n = Math.Max(1, displayWorkers.Count);
        double span = Math.Min(Math.Max(0, h - workerH - 8), Math.Max(workerH, (n - 1) * (workerH + 8)));
        double startY = cy - span / 2.0;
        var workerNodes = new List<(AgentRosterEntry? Agent, Border Card, Point LeftAnchor, bool IsOverflow)>();
        for (int i = 0; i < displayWorkers.Count; i++)
        {
            var item = displayWorkers[i];
            double yCenter = n == 1 ? cy : startY + (n == 1 ? 0 : i * (span / Math.Max(1, n - 1)));
            LiveAgentWorkState? live = null;
            if (item.Agent != null)
            {
                liveMap.TryGetValue(item.Agent.Id, out live);
                live ??= liveMap.Values.FirstOrDefault(v =>
                    string.Equals(v.AgentId, item.Agent.Id, StringComparison.OrdinalIgnoreCase));
            }
            var node = AddMindMapWorkerNode(
                centerX: w * rightX,
                centerY: yCenter,
                width: workerW,
                height: workerH,
                agent: item.Agent,
                overflowLabel: item.OverflowLabel,
                live: live,
                tipOverflow: item.OverflowTip);
            workerNodes.Add((item.Agent, node.Card, node.LeftAnchor, item.Agent == null));
        }

        // 中央主脑
        string masterName = master?.Name ?? "主脑";
        string masterTier = master != null
            ? RoutingService.EffortTierVisualLabel(master.PreferredEffortTier)
            : "—";
        string masterSub = string.IsNullOrWhiteSpace(_workflowDiagramPhaseLabel)
            ? masterTier
            : ShortName(_workflowDiagramPhaseLabel, 14);
        int masterTaskCount = 0;
        if (master != null && liveMap.TryGetValue(master.Id, out var masterLive))
            masterTaskCount = masterLive.ActiveTaskCount;
        var masterNode = AddMindMapMasterNode(
            centerX: cx,
            centerY: cy,
            width: masterW,
            height: masterH,
            master: master,
            title: ShortName(masterName, 12),
            subtitle: masterSub,
            tip: $"主脑：{masterName}\n模型档位：{masterTier}\n职责：深度分析 / 派单 / 终验\n当前阶段：{_workflowDiagramPhaseLabel} · 轮次 R{_workflowDiagramRound}/{_workflowDiagramMaxRounds}",
            onClick: () => NavDialogs.IsChecked = true,
            activeTaskCount: masterTaskCount);

        // 底层静默连线 + 激活彗星流光
        string? activeWorkerId = ResolveActiveWorkerId(workers);
        DrawMindMapEdge(intakeNode.RightAnchor, masterNode.LeftAnchor, IsEdgeActive("intake_master"), EdgeTone("intake_master"), reverse: false);
        DrawMindMapEdge(masterNode.LeftAnchor, verifyNode.RightAnchor, IsEdgeActive("master_verify"), EdgeTone("master_verify"), reverse: IsEdgeReverse("master_verify"));

        foreach (var wn in workerNodes)
        {
            bool isTarget = wn.Agent != null &&
                            !string.IsNullOrWhiteSpace(activeWorkerId) &&
                            string.Equals(wn.Agent.Id, activeWorkerId, StringComparison.OrdinalIgnoreCase);
            bool allWorkersHot = _workflowDiagramPhase == "verified_delivered";
            bool active = !paused && (allWorkersHot || isTarget ||
                                      (_workflowDiagramPhase is "dispatching" or "executing" or "reworking" or "master_verifying" && isTarget));
            if (_workflowDiagramPhase == "verified_delivered") active = !paused;
            else if (_workflowDiagramPhase is "dispatching" or "executing" or "reworking" or "master_verifying")
                active = !paused && isTarget;
            else
                active = false;

            bool reverse = _workflowDiagramPhase is "master_verifying" or "reworking";
            bool bidirectional = _workflowDiagramPhase == "executing" && isTarget;
            string tone = EdgeTone("master_worker");
            DrawMindMapEdge(masterNode.RightAnchor, wn.LeftAnchor, active, tone, reverse: reverse, bidirectional: bidirectional);
        }

        _workflowDiagramLayoutSig =
            $"{_workflowDiagramPhase}|{_workflowDiagramRound}|{_workflowDiagramMaxRounds}|{_workflowDiagramActiveWorkerId}|{_reg.UserConfig.WorkflowEngineEnabled}|{string.Join(",", _workflowDiagramWorkerIds)}|{_workflowDiagramDemoMode}";
    }

    private string? ResolveActiveWorkerId(List<AgentRosterEntry> workers)
    {
        if (!string.IsNullOrWhiteSpace(_workflowDiagramActiveWorkerId) &&
            workers.Any(w => string.Equals(w.Id, _workflowDiagramActiveWorkerId, StringComparison.OrdinalIgnoreCase)))
            return _workflowDiagramActiveWorkerId;
        return workers.FirstOrDefault()?.Id;
    }

    private static string ResolveProjectShortName(ProjectProfile? proj)
    {
        string projShort = string.IsNullOrWhiteSpace(proj?.Name) ? (proj?.Id ?? "—") : proj!.Name;
        if (!string.IsNullOrWhiteSpace(proj?.PrimaryRoot))
        {
            try
            {
                var leaf = System.IO.Path.GetFileName(proj.PrimaryRoot.TrimEnd('\\', '/'));
                if (!string.IsNullOrWhiteSpace(leaf)) projShort = leaf;
            }
            catch { /* ignore */ }
        }
        return ShortName(projShort, 12);
    }

    private sealed class DisplayWorkerItem
    {
        public AgentRosterEntry? Agent;
        public string? OverflowLabel;
        public string? OverflowTip;
    }

    private List<DisplayWorkerItem> BuildDisplayWorkers(List<AgentRosterEntry> workers)
    {
        var list = new List<DisplayWorkerItem>();
        if (workers.Count == 0)
        {
            list.Add(new DisplayWorkerItem { OverflowLabel = "暂无执行 AI", OverflowTip = "请在 AI 配置中启用执行工具" });
            return list;
        }
        if (workers.Count <= 4)
        {
            foreach (var w in workers)
                list.Add(new DisplayWorkerItem { Agent = w });
            return list;
        }
        foreach (var w in workers.Take(3))
            list.Add(new DisplayWorkerItem { Agent = w });
        var overflow = workers.Skip(3).ToList();
        list.Add(new DisplayWorkerItem
        {
            OverflowLabel = $"+{overflow.Count} 位成员",
            OverflowTip = string.Join("\n", overflow.Select(o => o.Name))
        });
        return list;
    }

    private void DrawMindMapStarGrid(double w, double h)
    {
        const double gap = 22;
        const double r = 1.4;
        var brush = Hex("#18253A");
        for (double y = 8; y < h; y += gap)
        {
            for (double x = 8; x < w; x += gap)
            {
                var dot = new Ellipse
                {
                    Width = r * 2,
                    Height = r * 2,
                    Fill = brush,
                    IsHitTestVisible = false,
                    Opacity = 0.5
                };
                WorkflowDiagramCanvas.Children.Add(dot);
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, y);
            }
        }
    }

    private void DrawMindMapAurora(double cx, double cy)
    {
        var (c0, c1, c2) = AuroraColors(_workflowDiagramPhase);
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.55,
            RadiusY = 0.55
        };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c0), 0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c1), 0.45));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c2), 1));

        _workflowDiagramAurora = new Ellipse
        {
            Width = 360,
            Height = 230,
            Fill = brush,
            IsHitTestVisible = false,
            Opacity = 0.55
        };
        WorkflowDiagramCanvas.Children.Add(_workflowDiagramAurora);
        Canvas.SetLeft(_workflowDiagramAurora, cx - 180);
        Canvas.SetTop(_workflowDiagramAurora, cy - 115);
        Panel.SetZIndex(_workflowDiagramAurora, 0);
    }

    private static (string C0, string C1, string C2) AuroraColors(string phase) => phase switch
    {
        "master_analyzing" => ("#6644CC88", "#312E8188", "#0F141D00"),
        "dispatching" or "executing" => ("#22D3EE66", "#0E749088", "#0F141D00"),
        "master_verifying" => ("#C084FC66", "#6D28D988", "#0F141D00"),
        "reworking" => ("#FBBF2466", "#B4530988", "#0F141D00"),
        "verified_delivered" => ("#34D39966", "#065F4688", "#0F141D00"),
        "paused" => ("#64748B44", "#1E293B66", "#0F141D00"),
        _ => ("#818CF666", "#4C1D9588", "#0F141D00") // idle 柔和蓝紫
    };

    private string? SideAccent(string kind)
    {
        if (_workflowDiagramPhase == "paused" && !_workflowDiagramDemoMode) return null;
        return kind switch
        {
            "intake" when _workflowDiagramPhase is "idle" or "master_analyzing" => "#818CF8",
            "verify" when _workflowDiagramPhase is "master_verifying" or "verified_delivered" =>
                _workflowDiagramPhase == "verified_delivered" ? "#10B981" : "#A78BFA",
            "verify" when _workflowDiagramPhase == "reworking" => "#F59E0B",
            _ => null
        };
    }

    private bool IsEdgeActive(string edge)
    {
        if (_workflowDiagramPhase == "paused" && !_workflowDiagramDemoMode) return false;
        return edge switch
        {
            "intake_master" => _workflowDiagramPhase is "master_analyzing" or "verified_delivered",
            "master_verify" => _workflowDiagramPhase is "master_verifying" or "reworking" or "verified_delivered",
            _ => false
        };
    }

    private bool IsEdgeReverse(string edge) =>
        edge == "master_verify" && _workflowDiagramPhase == "reworking";

    private string EdgeTone(string edge) => _workflowDiagramPhase switch
    {
        "reworking" => "#F59E0B",
        "verified_delivered" => "#10B981",
        "master_verifying" => "#A78BFA",
        "executing" or "dispatching" => "#22D3EE",
        "master_analyzing" => "#818CF8",
        _ => "#475569"
    };

    private (Border Card, Point LeftAnchor, Point RightAnchor) AddMindMapMasterNode(
        double centerX,
        double centerY,
        double width,
        double height,
        AgentRosterEntry? master,
        string title,
        string subtitle,
        string tip,
        Action? onClick,
        int activeTaskCount = 0)
    {
        double left = centerX - width / 2;
        double top = centerY - height / 2;

        _workflowDiagramMasterGlow = new Border
        {
            Width = width + 10,
            Height = height + 10,
            CornerRadius = new CornerRadius(16),
            BorderBrush = Hex("#818CF8"),
            BorderThickness = new Thickness(1.5),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Opacity = 0.35,
            Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString("#818CF8"),
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.55
            }
        };
        WorkflowDiagramCanvas.Children.Add(_workflowDiagramMasterGlow);
        Canvas.SetLeft(_workflowDiagramMasterGlow, left - 5);
        Canvas.SetTop(_workflowDiagramMasterGlow, top - 5);
        Panel.SetZIndex(_workflowDiagramMasterGlow, 1);

        var innerBg = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        innerBg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#1A2236"), 0));
        innerBg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#12182A"), 1));

        var grid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var avatar = master != null
            ? OfficialBrandIconService.CreateCircularAvatarVisual(master.Id, 32, master, OfficialBrandIconService.GetBrandColor(master.Id), 1.15)
            : MakeMindMapVectorIcon("master", 28, Hex("#C4B5FD"));
        avatar = DecorateAvatarWithTaskCount(avatar, master != null ? activeTaskCount : 0, 32);
        avatar.VerticalAlignment = VerticalAlignment.Center;
        avatar.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(avatar, 0);

        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Hex("#F8FAFC"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 96,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleRow.Children.Add(new Border
        {
            Background = Hex("#2E1065"),
            BorderBrush = Hex("#7C3AED"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "主脑",
                FontSize = 10.5,
                Foreground = Hex("#DDD6FE"),
                FontWeight = FontWeights.SemiBold
            }
        });
        textCol.Children.Add(titleRow);
        textCol.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11.5,
            Foreground = Hex("#94A3B8"),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(textCol, 1);
        grid.Children.Add(avatar);
        grid.Children.Add(textCol);

        var card = new Border
        {
            Tag = "MindMapNode:master",
            Width = width,
            Height = height,
            Background = innerBg,
            BorderBrush = Hex("#6366F1"),
            BorderThickness = new Thickness(1.4),
            CornerRadius = new CornerRadius(14),
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = BuildDarkTip(tip),
            Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString("#312E81"),
                BlurRadius = 16,
                ShadowDepth = 0,
                Opacity = 0.55
            }
        };
        if (onClick != null)
            card.MouseLeftButtonUp += (_, _) => onClick();
        _workflowDiagramNodeBorders.Add(card);
        WorkflowDiagramCanvas.Children.Add(card);
        Canvas.SetLeft(card, left);
        Canvas.SetTop(card, top);
        Panel.SetZIndex(card, 3);

        var leftAnchor = AddAnchorDot(left, centerY);
        var rightAnchor = AddAnchorDot(left + width, centerY);
        return (card, leftAnchor, rightAnchor);
    }

    private (Border Card, Point RightAnchor) AddMindMapSideNode(
        string tag,
        double centerX,
        double centerY,
        double width,
        double height,
        string title,
        string subtitle,
        string tip,
        string iconKind,
        string? accent,
        bool anchorOnRight,
        Action? onClick)
    {
        double left = centerX - width / 2;
        double top = centerY - height / 2;

        var row = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = MakeMindMapVectorIcon(iconKind, 22, Hex(accent ?? "#94A3B8"));
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(icon, 0);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Hex("#E2E8F0"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        texts.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = Hex("#94A3B8"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(texts, 1);
        row.Children.Add(icon);
        row.Children.Add(texts);

        var card = new Border
        {
            Tag = tag,
            Width = width,
            Height = height,
            Background = Hex("#121824"),
            BorderBrush = Hex(accent ?? "#24324B"),
            BorderThickness = new Thickness(accent != null ? 1.4 : 1),
            CornerRadius = new CornerRadius(12),
            Child = row,
            Cursor = Cursors.Hand,
            ToolTip = BuildDarkTip(tip)
        };
        if (accent != null)
        {
            card.Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString(accent),
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.45
            };
        }
        if (onClick != null)
            card.MouseLeftButtonUp += (_, _) => onClick();
        _workflowDiagramNodeBorders.Add(card);
        WorkflowDiagramCanvas.Children.Add(card);
        Canvas.SetLeft(card, left);
        Canvas.SetTop(card, top);
        Panel.SetZIndex(card, 3);

        Point anchor = anchorOnRight
            ? AddAnchorDot(left + width, centerY)
            : AddAnchorDot(left, centerY);
        return (card, anchor);
    }

    private (Border Card, Point LeftAnchor) AddMindMapWorkerNode(
        double centerX,
        double centerY,
        double width,
        double height,
        AgentRosterEntry? agent,
        string? overflowLabel,
        LiveAgentWorkState? live,
        string? tipOverflow)
    {
        double left = centerX - width / 2;
        double top = centerY - height / 2;

        bool hot = agent != null && (
            string.Equals(agent.Id, _workflowDiagramActiveWorkerId, StringComparison.OrdinalIgnoreCase) &&
            _workflowDiagramPhase is "executing" or "dispatching" or "reworking" or "master_verifying");
        bool done = _workflowDiagramPhase == "verified_delivered" ||
                    (live != null && (live.Status == "done" ||
                                      string.Equals(live.WorkflowPhase, "verified_delivered", StringComparison.OrdinalIgnoreCase)));
        string? accent = hot ? "#22D3EE" : (done ? "#10B981" : null);

        var grid = new Grid { Margin = new Thickness(10, 6, 10, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement avatar;
        string name;
        string sub;
        string tip;
        if (agent != null)
        {
            avatar = OfficialBrandIconService.CreateCircularAvatarVisual(
                agent.Id, 24, agent, OfficialBrandIconService.GetBrandColor(agent.Id), 1.05);
            avatar = DecorateAvatarWithTaskCount(avatar, live?.ActiveTaskCount ?? 0, 24);
            name = agent.Name;
            string tier = !string.IsNullOrWhiteSpace(live?.SelectedTierBadge)
                ? live!.SelectedTierBadge
                : RoutingService.EffortTierVisualLabel(agent.PreferredEffortTier);
            sub = ShortName(live?.SelectedModel ?? tier, 16);
            tip = DescribeWorkerTip(agent, live);
        }
        else
        {
            avatar = MakeMindMapVectorIcon("workers", 22, Hex("#94A3B8"));
            name = overflowLabel ?? "+N";
            sub = "更多成员";
            tip = tipOverflow ?? name;
        }
        avatar.VerticalAlignment = VerticalAlignment.Center;
        avatar.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(avatar, 0);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Hex("#F1F5F9"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        texts.Children.Add(new TextBlock
        {
            Text = sub,
            FontSize = 10.5,
            Foreground = Hex("#94A3B8"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(texts, 1);

        var statusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Hex(hot ? "#22D3EE" : (done ? "#10B981" : "#64748B")),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 4, 0, 0)
        };
        Grid.SetColumn(statusDot, 2);

        grid.Children.Add(avatar);
        grid.Children.Add(texts);
        grid.Children.Add(statusDot);

        var card = new Border
        {
            Tag = agent != null ? "MindMapNode:worker" : "MindMapNode:worker_overflow",
            Width = width,
            Height = height,
            Background = Hex("#121824"),
            BorderBrush = Hex(accent ?? "#24324B"),
            BorderThickness = new Thickness(accent != null ? 1.5 : 1),
            CornerRadius = new CornerRadius(12),
            Child = grid,
            Cursor = agent != null ? Cursors.Hand : Cursors.Arrow,
            ToolTip = BuildDarkTip(tip)
        };
        if (accent != null)
        {
            card.Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString(accent),
                BlurRadius = 14,
                ShadowDepth = 0,
                Opacity = 0.5
            };
        }
        if (agent != null)
        {
            var ag = agent;
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (AutoDispatchService.ResolveGuiExecutable(ag) != null)
                {
                    var proj = _reg.GetActiveProject();
                    SetStatus(_autoDispatch.EnsureOrActivateAgentWindow(ag, proj?.PrimaryRoot, activateIfRunning: true));
                }
                else
                {
                    OpenRosterAgentConfig(ag);
                }
            };
        }
        _workflowDiagramNodeBorders.Add(card);
        WorkflowDiagramCanvas.Children.Add(card);
        Canvas.SetLeft(card, left);
        Canvas.SetTop(card, top);
        Panel.SetZIndex(card, 3);

        var leftAnchor = AddAnchorDot(left, centerY);
        return (card, leftAnchor);
    }

    private Point AddAnchorDot(double x, double y)
    {
        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Hex("#64748B"),
            Stroke = Hex("#94A3B8"),
            StrokeThickness = 0.8,
            IsHitTestVisible = false
        };
        WorkflowDiagramCanvas.Children.Add(dot);
        Canvas.SetLeft(dot, x - 3);
        Canvas.SetTop(dot, y - 3);
        Panel.SetZIndex(dot, 4);
        return new Point(x, y);
    }

    private FrameworkElement MakeMindMapVectorIcon(string kind, double size, Brush stroke)
    {
        string data = kind switch
        {
            "intake" => "M4,6 H20 V16 H12 L8,20 V16 H4 Z",
            "verify" => "M12,3 L20,7 V12 C20,17 16,20 12,21 C8,20 4,17 4,12 V7 Z M8,12 L11,15 L16,9",
            "master" => "M12,3 L14,9 H20 L15,13 L17,20 L12,16 L7,20 L9,13 L4,9 H10 Z",
            _ => "M6,8 H18 V16 H6 Z M9,8 V16 M15,8 V16"
        };
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = new Path
            {
                Data = Geometry.Parse(data),
                Stroke = stroke,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                Stretch = Stretch.Uniform
            }
        };
    }

    private void DrawMindMapEdge(
        Point from,
        Point to,
        bool active,
        string toneHex,
        bool reverse = false,
        bool bidirectional = false)
    {
        // 水平切线三次贝塞尔（思维导图弧度）
        double dx = Math.Abs(to.X - from.X);
        double c1x = from.X + dx * 0.45;
        double c2x = to.X - dx * 0.45;
        var geo = new PathGeometry();
        var fig = new PathFigure
        {
            StartPoint = from,
            IsClosed = false,
            IsFilled = false
        };
        fig.Segments.Add(new BezierSegment(
            new Point(c1x, from.Y),
            new Point(c2x, to.Y),
            to,
            true));
        geo.Figures.Add(fig);

        // 静默暗蓝底层细线
        var basePath = new Path
        {
            Data = geo,
            Stroke = Hex("#1E3A5F"),
            StrokeThickness = 1.15,
            Opacity = 0.55,
            IsHitTestVisible = false
        };
        WorkflowDiagramCanvas.Children.Add(basePath);
        Panel.SetZIndex(basePath, 1);

        if (active)
        {
            // 柔光宽底晕
            var glowPath = new Path
            {
                Data = geo,
                Stroke = Hex(toneHex),
                StrokeThickness = 4.5,
                Opacity = 0.22,
                IsHitTestVisible = false
            };
            WorkflowDiagramCanvas.Children.Add(glowPath);
            Panel.SetZIndex(glowPath, 1);

            // 高亮流光线
            var hiPath = new Path
            {
                Data = geo,
                Stroke = Hex(toneHex),
                StrokeThickness = 1.7,
                Opacity = 0.9,
                IsHitTestVisible = false
            };
            WorkflowDiagramCanvas.Children.Add(hiPath);
            Panel.SetZIndex(hiPath, 2);

            AddCometTrack(geo, toneHex, reverse, bidirectional ? 0 : 0);
            if (bidirectional)
                AddCometTrack(geo, toneHex, reverse: !reverse, phaseOffset: 0.5);
        }
    }

    private void AddCometTrack(PathGeometry geo, string toneHex, bool reverse, double phaseOffset = 0)
    {
        Ellipse MakeDot(double size, double opacity) => new()
        {
            Width = size,
            Height = size,
            Fill = Hex(toneHex),
            IsHitTestVisible = false,
            Opacity = opacity,
            Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString(toneHex),
                BlurRadius = size * 2.2,
                ShadowDepth = 0,
                Opacity = 0.85
            }
        };

        var core = MakeDot(7, 1.0);
        var tail1 = MakeDot(5, 0.55);
        var tail2 = MakeDot(3.5, 0.28);
        WorkflowDiagramCanvas.Children.Add(tail2);
        WorkflowDiagramCanvas.Children.Add(tail1);
        WorkflowDiagramCanvas.Children.Add(core);
        Panel.SetZIndex(tail2, 5);
        Panel.SetZIndex(tail1, 5);
        Panel.SetZIndex(core, 6);

        var track = new MindMapFlowTrack
        {
            Geometry = geo,
            Core = core,
            Tail1 = tail1,
            Tail2 = tail2,
            Reverse = reverse,
            PhaseOffset = phaseOffset,
            Speed = 0.36
        };
        _workflowDiagramFlowTracks.Add(track);
        PlaceWorkflowDiagramComet(track);
    }

    private void PlaceWorkflowDiagramComet(MindMapFlowTrack track)
    {
        double t = ((_workflowDiagramPulseT * track.Speed) + track.PhaseOffset) % 1.0;
        if (t < 0) t += 1.0;
        if (track.Reverse) t = 1.0 - t;
        PlaceCometDot(track.Geometry, track.Core, t, track.Core.Width);
        PlaceCometDot(track.Geometry, track.Tail1, Wrap01(t - 0.055), track.Tail1.Width);
        PlaceCometDot(track.Geometry, track.Tail2, Wrap01(t - 0.11), track.Tail2.Width);
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
             _workflowDiagramPreviewOverride ||
             (_reg.UserConfig.WorkflowEngineEnabled && _workflowDiagramPhase == "idle"));

        // idle 也允许轻微呼吸（中心光晕），但自检隔离环境仍停表
        if (AppPaths.IsIsolatedTest)
        {
            StopWorkflowDiagramAnimTimer();
            return;
        }

        if (shouldRun)
            StartWorkflowDiagramAnimTimer();
        else
            StopWorkflowDiagramAnimTimer();
    }

    private void StartWorkflowDiagramAnimTimer()
    {
        if (_workflowDiagramAnimTimer != null) return;
        _workflowDiagramAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
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
        _workflowDiagramPulseT += 0.045;

        // 主脑外层呼吸：仅改 Opacity，绝不改尺寸
        if (_workflowDiagramMasterGlow != null)
        {
            double breathe = 0.28 + 0.42 * (0.5 + 0.5 * Math.Sin(_workflowDiagramPulseT * 2.1));
            _workflowDiagramMasterGlow.Opacity = breathe;
        }
        if (_workflowDiagramAurora != null)
        {
            double a = 0.42 + 0.22 * (0.5 + 0.5 * Math.Sin(_workflowDiagramPulseT * 1.4));
            _workflowDiagramAurora.Opacity = a;
        }

        if (WorkflowDiagramStatusDot != null)
        {
            double breathe = 0.72 + 0.28 * (0.5 + 0.5 * Math.Sin(_workflowDiagramPulseT * 2.2));
            WorkflowDiagramStatusDot.Opacity = breathe;
        }

        foreach (var track in _workflowDiagramFlowTracks)
            PlaceWorkflowDiagramComet(track);

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

    private static double Wrap01(double t)
    {
        t %= 1.0;
        if (t < 0) t += 1.0;
        return t;
    }

    private static void PlaceCometDot(PathGeometry geo, Ellipse dot, double t, double size)
    {
        geo.GetPointAtFractionLength(t, out Point pt, out _);
        Canvas.SetLeft(dot, pt.X - size / 2);
        Canvas.SetTop(dot, pt.Y - size / 2);
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
