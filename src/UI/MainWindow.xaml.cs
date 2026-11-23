using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using AICollabHub.Models;
using AICollabHub.Services;

namespace AICollabHub.UI;

public partial class MainWindow : Window
{
    private readonly RegistryService _reg;
    private readonly DiscoveryService _discovery = new();
    private readonly RoutingService _routing = new();
    private readonly SyncService _sync = new();
    private readonly CollabBusService _bus = new();
    private readonly AutoDispatchService _autoDispatch = new();
    private readonly SessionDialogService _sessionSvc = new();
    private readonly ShareExportService _share = new();
    private readonly StartupAndShortcutService _startupSvc = new();
    private readonly CloudRecommendationService _cloudRecSvc = new();
    private TrayIconService? _traySvc;

    private bool _suppressEvents;
    private bool _allowFullExit;
    private bool _suppressMinimizeToTray;
    private bool _sessionEnding;
    private string? _selectedRuleSkillFilePath;
    private string? _selectedDialogSessionId;
    private string? _selectedDialogFilePath;
    private bool _isPreviewingNonTaskContent;
    private DispatcherTimer? _livePollTimer;
    private DispatcherTimer? _wakeTimer;
    private DispatcherTimer? _longActionPulseTimer;
    private DispatcherTimer? _appUpdateStartupTimer;
    private AppUpdateCheckResult? _lastAppUpdate;
    private int _shutdownCleaned;
    private DateTime _lastSummaryStatsTime = DateTime.MinValue;
    private string _cachedSummaryStatsText = "";
    private FloatingOverlayWindow? _floatingWindow;
    private DateTime _lastNetProbeUtc = DateTime.MinValue;
    private bool _lastKnownNetOnline = true;
    private volatile bool _isResumingCheckpointInBackground;
    private volatile bool _isRescanningAgents;
    /// <summary>roster 筛选：installed / all / enabled / disabled / catalog。默认只显示已装或已启用。</summary>
    private string _rosterFilterMode = "installed";

    // 性能优化与防卡顿缓存（避免 UI 线程每 1.5s 重建卡片树、遍历全系统进程或同步扫 100+ 对话）
    private string _lastOverviewSignature = "";
    private Dictionary<string, (bool IsRunning, int Pid, string WindowBadge)> _cachedWindowStates = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, bool> _cachedDesktopBusyFlags = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastWindowProbeUtc = DateTime.MinValue;
    private volatile bool _isProbingWindowsAsync;
    private List<ExternalDialogSessionInfo> _cachedProjectSessions = new();
    private string _cachedSessionsProjectId = "";
    private DateTime _lastSessionsScanUtc = DateTime.MinValue;
    private volatile bool _isLoadingSessionsAsync;
    private int _cachedSkillCount;
    private int _cachedRuleCount;
    private bool _hasInventoryCounts;
    private bool _showAllDialogs;
    private bool _dialogStructuredMode = true;
    private int _dialogLoadSeq;
    private int _dispatchPollGen;
    private string _pendingRawMarkdown = "";
    private readonly Dictionary<string, DialogCardEntry> _dialogCardEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _dialogTranscriptLru = new();
    private readonly Dictionary<string, (List<(string Role, string Time, string Text)> Turns, string Markdown)> _dialogTranscriptCache = new(StringComparer.Ordinal);
    private int _syncPageSeq;
    private List<SyncService.SyncHealthItem>? _cachedSyncHealth;
    private List<SyncService.RuleSkillInventoryEntry>? _cachedSyncInventory;
    private int _opsPageSeq;
    private bool _opsActionBusy;
    private int _rulePreviewSeq;
    private DispatcherTimer? _dialogSearchDebounceTimer;
    private bool _hasUnsavedSettingsChanges;

    public (bool IsRunning, int Pid, string WindowBadge) GetCachedWindowState(AgentRosterEntry agent)
    {
        if (_cachedWindowStates.TryGetValue(agent.Id, out var cw))
        {
            return cw;
        }
        if (AppPaths.IsIsolatedTest && AutoDispatchService.ResolveGuiExecutable(agent) != null)
        {
            var probe = _autoDispatch.ProbeWindowProcessOnly(agent);
            _cachedWindowStates[agent.Id] = probe;
            return probe;
        }
        return (false, 0, "⚪ 窗口未启动");
    }

    /// <summary>
    /// 供离屏实机截图预热会话列表与资产计数（不影响真实 GUI 启动速度）。
    /// </summary>
    public void PreloadCachesForLiveCapture()
    {
        var proj = _reg.GetActiveProject();
        if (proj == null || string.IsNullOrWhiteSpace(proj.PrimaryRoot) || !Directory.Exists(proj.PrimaryRoot)) return;
        _cachedProjectSessions = _sessionSvc.DiscoverProjectSessions(proj, null);
        _cachedSessionsProjectId = proj.Id;
        _lastSessionsScanUtc = DateTime.UtcNow;
        var inv = _sync.InspectRulesAndSkillsInventory(_reg, proj);
        _cachedSkillCount = inv.Count(x => x.Kind == "skill");
        _cachedRuleCount = inv.Count(x => x.Kind == "rule" || x.Kind == "global_rule");
        _hasInventoryCounts = true;
        _lastSummaryStatsTime = DateTime.MinValue;
    }

    public MainWindow(RegistryService reg, bool suppressAutoShowFloating = false)
    {
        _reg = reg;
        InitializeComponent();
        ThemeService.ApplyGlobalTheme(_reg.UserConfig.FloatingThemeStyle);
        ApplyAppUpdateChrome();
        Title = $"AIDeck · 多 AI 协同中心 {AppUpdateService.GetCurrentVersionDisplay()}";

        RightDataPathText.Text = $"个人配置目录：{AppPaths.UserDataDir}";
        if (AppUserDataPathText != null)
            AppUserDataPathText.Text = AppPaths.UserDataDir;
        AiChatRootBadge.Text = Directory.Exists(@"F:\AIChat")
            ? "✓ 已连接 F:\\AIChat 聊天库"
            : "● 使用系统默认聊天目录";
        AiChatRootBadge.Foreground = Directory.Exists(@"F:\AIChat") ? Hex("#34D399") : Hex("#93C5FD");

        if (_reg.UserConfig.Agents.Count == 0)
        {
            _discovery.RunDiscovery(_reg);
        }
        else if (!AppPaths.IsIsolatedTest)
        {
            _discovery.RefreshStaleDetectionStates(_reg);
        }

        // 启动时同步从本机各客户端真实模型库校准列表，并自动修复旧版遗留的错误/截断模型名
        foreach (var ag in _reg.UserConfig.Agents)
        {
            CloudRecommendationService.EnsureAgentModelTiersPopulated(ag);
            if (string.IsNullOrWhiteSpace(ag.RecommendedSummary))
            {
                ag.RecommendedSummary = CloudRecommendationService.BuildRecommendationForAgent(ag).RoleSummary;
            }
        }
        MigrateAndSyncCloseButtonAction(_reg.UserConfig);
        _reg.SaveUserConfig();

        RefreshProjectCombo();
        if (AppPaths.IsIsolatedTest || suppressAutoShowFloating)
        {
            RefreshAllViews();
        }
        else
        {
            // 启动秒开：构造函数仅做轻量首屏渲染，全盘对话扫描延迟到窗体显示后异步完成
            RenderOverviewPage(fastPoll: true);
            RenderSettingsPage();
            RenderAppSettingsPage();
        }
        SoundFxService.ApplySoundPreference(_reg.UserConfig);
        UpdateFloatingButtonBadge();
        UpdateWorkflowSwitchBadges();
        ArmStaticLongActionButtons();
        if (!AppPaths.IsIsolatedTest)
        {
            _longActionPulseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _longActionPulseTimer.Tick += (_, _) =>
            {
                if (!LongActionTracker.HasAnyBusy) return;
                LongActionTracker.PulseButtonsInAllWindows();
            };
            _longActionPulseTimer.Start();
        }

        if (!AppPaths.IsIsolatedTest && !suppressAutoShowFloating)
        {
            _wakeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _wakeTimer.Tick += (_, _) => CheckAndConsumeWakeupSignal();
            _wakeTimer.Start();

            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized
                    && !_allowFullExit
                    && !AppPaths.IsIsolatedTest
                    && !_suppressMinimizeToTray
                    && IsLoaded)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (WindowState == WindowState.Minimized
                            && !_allowFullExit
                            && !_suppressMinimizeToTray
                            && IsLoaded)
                        {
                            MinimizeMainWindowToTray();
                        }
                    }), DispatcherPriority.Background);
                }
            };

            Loaded += (_, _) =>
            {
                try
                {
                    _traySvc = new TrayIconService(
                        onOpenMain: RestoreAndActivateMainWindow,
                        onToggleFloating: () => ToggleFloatingBtn_Click(this, new RoutedEventArgs()),
                        onToggleWorkflow: ToggleWorkflowEngineFromQuickAction,
                        onExitApp: ForceCloseApp,
                        isWorkflowEnabled: () => _reg.UserConfig.WorkflowEngineEnabled);
                    _traySvc.Initialize(this);
                }
                catch { }

                // 启动时根据工作流开关状态自动挂载或剥离锚点
                ApplyWorkflowAnchorsByCurrentState(silent: true);

                if (_reg.UserConfig.FloatingWindowEnabled)
                {
                    ShowFloatingOverlayWindow();
                }

                // 启动直进悬浮窗 / --floating：托盘与悬浮窗就绪后平滑隐藏主界面到系统托盘
                if (WindowState == WindowState.Minimized && !_allowFullExit)
                {
                    MinimizeMainWindowToTray(ensureFloatingVisible: true);
                }

                // 窗体已显示后在后台线程异步探测窗口进程、MCP、CLI 更新与历史对话框缓存（零阻塞 UI 线程）
                TriggerBackgroundProbesAsync(refreshOverviewWhenChanged: true);
                TriggerBackgroundCliUpdateChecksAsync(forceRefresh: false);
                TriggerAsyncSessionsScanIfNeeded(force: false);

                // 启动约 600ms 后静默全量发现（不阻塞首屏 / AppHangB1），自动检出本机最新主流 AI 工具
                TriggerStartupSilentFullDiscovery();
                ScheduleStartupAppUpdateCheck();

                if (_reg.UserConfig.AutoSyncCloudRecommendations &&
                    LongActionTracker.TryBegin(LongActionTracker.CloudSync, "同步中"))
                {
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        string fail = "";
                        (bool FetchedOnline, int OnlineModelsAnalyzed, string SourceName) net = default;
                        try
                        {
                            net = _cloudRecSvc.FetchOnlineHintsOnly();
                        }
                        catch (Exception ex)
                        {
                            fail = ex.Message;
                        }
                        Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                if (!IsLoaded) return;
                                if (!string.IsNullOrEmpty(fail))
                                {
                                    SetStatus($"⚠ 启动云端同步失败：{TruncateStatus(fail)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
                                    return;
                                }
                                _cloudRecSvc.BuildAndApplyReportFromCache(_reg, applyToAgents: false, net.FetchedOnline, net.OnlineModelsAnalyzed, net.SourceName);
                                if (NavSettings.IsChecked == true) RenderSettingsPage();
                                SetStatus(net.FetchedOnline
                                    ? $"✅ 已同步云端推荐（{net.SourceName}，{net.OnlineModelsAnalyzed} 个模型，耗时 {(int)sw.Elapsed.TotalSeconds}s）"
                                    : $"⚠ 云端推荐暂未拉到在线数据，已保留本地基准（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
                            }
                            finally
                            {
                                EndLongAction(LongActionTracker.CloudSync);
                            }
                        });
                    });
                }

            };

            Closing += (_, e) =>
            {
                if (_allowFullExit || AppPaths.IsIsolatedTest || _sessionEnding)
                {
                    return;
                }

                string action = ResolveCloseActionChoice(_reg.UserConfig);
                if (string.Equals(action, "minimize_to_tray", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    MinimizeMainWindowToTray();
                    return;
                }

                if (string.Equals(action, "exit", StringComparison.OrdinalIgnoreCase))
                {
                    // 直接放行：Closed 会走 PerformShutdownWorkflowCleanup
                    return;
                }

                // ask（默认）：弹确认框
                e.Cancel = true;
                var choice = Dialogs.ShowCloseConfirmDialog(this);
                ApplyCloseDialogDecision(_reg, choice, syncSettingsUi: SyncCloseButtonActionControlsFromConfig);
                if (choice == null || choice.Kind == Dialogs.CloseConfirmChoiceKind.Cancel)
                {
                    return;
                }

                if (choice.Kind == Dialogs.CloseConfirmChoiceKind.MinimizeToTray)
                {
                    MinimizeMainWindowToTray();
                    return;
                }

                if (choice.Kind == Dialogs.CloseConfirmChoiceKind.ExitApp)
                {
                    // 当前 Closing 已 Cancel：延后一拍以 _allowFullExit 真正退出，避免重入 Closing 卡死
                    Dispatcher.BeginInvoke(new Action(ForceCloseApp), DispatcherPriority.ApplicationIdle);
                }
            };

            if (Application.Current != null)
            {
                Application.Current.SessionEnding += (_, _) =>
                {
                    _sessionEnding = true;
                    PerformShutdownWorkflowCleanup();
                };
            }

            _livePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _livePollTimer.Tick += (_, _) =>
            {
                CheckAndConsumeWakeupSignal();

                if (_reg.ReloadUserConfigFromDiskIfNeeded())
                {
                    SoundFxService.ApplySoundPreference(_reg.UserConfig);
                    _floatingWindow?.SyncQuickSoundButtonUi();
                    RefreshProjectCombo();
                    UpdateWorkflowSwitchBadges();
                    SyncAppSettingsControlsIfLoaded();
                }
                if ((DateTime.UtcNow - _lastNetProbeUtc).TotalSeconds >= 10)
                {
                    _lastNetProbeUtc = DateTime.UtcNow;
                    PollNetworkHealthAndAutoResumeAsync();
                }
                if ((DateTime.UtcNow - _lastWindowProbeUtc).TotalSeconds >= 4.5)
                {
                    _lastWindowProbeUtc = DateTime.UtcNow;
                    TriggerBackgroundProbesAsync(refreshOverviewWhenChanged: NavOverview.IsChecked == true);
                }
                if (IsVisible && NavOverview.IsChecked == true)
                {
                    RenderOverviewPage(fastPoll: true);
                }
            };
            _livePollTimer.Start();
            Closed += (_, _) =>
            {
                PerformShutdownWorkflowCleanup();
            };
        }

        if (!AppPaths.IsIsolatedTest)
        {
            Loaded += (_, _) =>
                Dispatcher.BeginInvoke(new Action(TryAutoShowOnboarding), DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>所有退出通路（× 放行后 Closed / 托盘退出 / 系统注销）统一收口，幂等仅执行一次。</summary>
    private void PerformShutdownWorkflowCleanup()
    {
        if (Interlocked.Exchange(ref _shutdownCleaned, 1) != 0) return;

        try { SoundFxService.MarkShuttingDown(true); } catch { }

        try { _livePollTimer?.Stop(); } catch { }
        try { _longActionPulseTimer?.Stop(); } catch { }
        try { _wakeTimer?.Stop(); } catch { }
        try { _dialogSearchDebounceTimer?.Stop(); } catch { }
        try { StopWorkflowDiagramAnimTimer(); } catch { }

        try { _floatingWindow?.ShutdownForAppExit(); } catch { }
        _floatingWindow = null;

        try { _traySvc?.Dispose(); } catch { }
        _traySvc = null;

        if (AppPaths.IsIsolatedTest) return;

        try { AutoDispatchService.TerminateHubOwnedDispatchersOnShutdown(); } catch { }
        try
        {
            if (_reg.UserConfig.WorkflowOnlyWhenAppRunning || !_reg.UserConfig.WorkflowEngineEnabled)
            {
                _sync.DeactivateAndStripAllCollabAnchors(_reg, _reg.GetActiveProject());
            }
        }
        catch { }

        try { Application.Current?.Shutdown(0); } catch { }
        try
        {
            var exitGuard = new Thread(() =>
            {
                try { Thread.Sleep(450); } catch { }
                try { Environment.Exit(0); } catch { }
            })
            {
                IsBackground = true,
                Name = "HubExitGuard"
            };
            exitGuard.Start();
        }
        catch { }
    }

    public void ApplyWorkflowAnchorsByCurrentState(bool silent = false)
    {
        var proj = _reg.GetActiveProject();
        if (_reg.UserConfig.WorkflowEngineEnabled)
        {
            if (proj != null)
            {
                var msg = _sync.ExecuteSyncAll(_reg, proj, _bus);
                if (!silent) SetStatus($"🟢 已开启「多 AI 协同工作流」：{msg}");
            }
        }
        else
        {
            var msg = _sync.DeactivateAndStripAllCollabAnchors(_reg, proj);
            if (!silent) SetStatus($"⚪ {msg}");
        }
        UpdateWorkflowSwitchBadges();
        _floatingWindow?.RefreshOverlayData(force: true);
    }

    public void ToggleWorkflowEngineFromQuickAction()
    {
        _reg.UserConfig.WorkflowEngineEnabled = !_reg.UserConfig.WorkflowEngineEnabled;
        _reg.SaveUserConfig();
        ApplyWorkflowAnchorsByCurrentState(silent: false);
        RefreshAllViews();
        RefreshWorkflowDiagram();
    }

    public void UpdateWorkflowSwitchBadges()
    {
        bool wfOn = _reg.UserConfig.WorkflowEngineEnabled;
        bool onlyRun = _reg.UserConfig.WorkflowOnlyWhenAppRunning;
        if (WorkflowSwitchBtn != null)
        {
            WorkflowSwitchBtn.Content = "协同开关";
            WorkflowSwitchBtn.Style = (Style)FindResource(wfOn ? "SuccessBtn" : "ActionBtn");
            WorkflowSwitchBtn.ToolTip = wfOn ? "协同已开启，点击关闭" : "协同已关闭，点击开启";
        }
        if (HeaderStatusDot != null)
        {
            HeaderStatusDot.Fill = wfOn ? Hex("#10B981") : Hex("#64748B");
        }
        if (SidebarWorkflowBadge != null)
        {
            SidebarWorkflowBadge.Text = wfOn ? "协同运行" : "独立模式";
            SidebarWorkflowBadge.ToolTip = wfOn
                ? (onlyRun ? "协同已启用，随软件启停" : "协同已启用，常驻生效")
                : "协同已停用，各 AI 独立工作";
            SidebarWorkflowBadge.Foreground = wfOn ? Hex("#34D399") : Hex("#94A3B8");
        }
        _traySvc?.UpdateTooltip(wfOn
            ? $"AIDeck · 多 AI 协同中心 v{AppPaths.AppVersion} · 协同工作流运行中"
            : $"AIDeck · 多 AI 协同中心 v{AppPaths.AppVersion} · 已停用 (独立模式)");
    }

    public void ForceCloseApp()
    {
        _allowFullExit = true;
        Close();
    }

    /// <summary>
    /// 解析关闭按钮行为：合法 ask / minimize_to_tray / exit；
    /// 旧配置 CloseButtonAction 为空且 CloseMainWindowToFloating=true 时兼容为 minimize_to_tray。
    /// </summary>
    public static string ResolveCloseActionChoice(UserHubConfig cfg)
    {
        var raw = (cfg.CloseButtonAction ?? "").Trim().ToLowerInvariant();
        if (raw is "ask" or "minimize_to_tray" or "exit")
        {
            return raw;
        }

        // 空/未设置：按旧布尔字段兼容
        return cfg.CloseMainWindowToFloating ? "minimize_to_tray" : "ask";
    }

    /// <summary>将 CloseButtonAction 与 CloseMainWindowToFloating 双向协调到一致状态。</summary>
    public static void SyncCloseButtonActionFields(UserHubConfig cfg, string? action = null)
    {
        string resolved = string.IsNullOrWhiteSpace(action)
            ? ResolveCloseActionChoice(cfg)
            : action!.Trim().ToLowerInvariant();
        if (resolved is not ("ask" or "minimize_to_tray" or "exit"))
        {
            resolved = cfg.CloseMainWindowToFloating ? "minimize_to_tray" : "ask";
        }

        cfg.CloseButtonAction = resolved;
        cfg.CloseMainWindowToFloating = resolved == "minimize_to_tray";
    }

    /// <summary>读取旧配置后的一次性迁移与字段同步。</summary>
    public static void MigrateAndSyncCloseButtonAction(UserHubConfig cfg)
    {
        var raw = (cfg.CloseButtonAction ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw))
        {
            // 空/未设置：旧布尔 true → 托盘，否则询问
            cfg.CloseButtonAction = cfg.CloseMainWindowToFloating ? "minimize_to_tray" : "ask";
        }
        else if (raw == "ask" && cfg.CloseMainWindowToFloating)
        {
            // 旧配置无 close_button_action 字段时 STJ 缺省为 ask，但布尔仍为 true → 保留最小化语义
            cfg.CloseButtonAction = "minimize_to_tray";
        }
        else if (raw is not ("ask" or "minimize_to_tray" or "exit"))
        {
            cfg.CloseButtonAction = cfg.CloseMainWindowToFloating ? "minimize_to_tray" : "ask";
        }

        SyncCloseButtonActionFields(cfg);
    }

    /// <summary>
    /// 应用关闭确认弹窗决策：仅在选择最小化/退出且勾选「记住」时落盘；取消绝不改写。
    /// </summary>
    public static void ApplyCloseDialogDecision(
        RegistryService reg,
        Dialogs.CloseConfirmChoice? choice,
        Action? syncSettingsUi = null)
    {
        if (choice == null || choice.Kind == Dialogs.CloseConfirmChoiceKind.Cancel)
        {
            return;
        }

        if (!choice.Remember) return;

        if (choice.Kind == Dialogs.CloseConfirmChoiceKind.MinimizeToTray)
        {
            SyncCloseButtonActionFields(reg.UserConfig, "minimize_to_tray");
            reg.SaveUserConfig();
            syncSettingsUi?.Invoke();
        }
        else if (choice.Kind == Dialogs.CloseConfirmChoiceKind.ExitApp)
        {
            SyncCloseButtonActionFields(reg.UserConfig, "exit");
            reg.SaveUserConfig();
            syncSettingsUi?.Invoke();
        }
    }

    /// <summary>将主界面隐藏到系统托盘（任务栏不占位）。</summary>
    public void MinimizeMainWindowToTray(bool ensureFloatingVisible = true)
    {
        _suppressMinimizeToTray = true;
        try
        {
            if (!AppPaths.IsIsolatedTest)
            {
                try
                {
                    if (_traySvc == null)
                    {
                        _traySvc = new TrayIconService(
                            onOpenMain: RestoreAndActivateMainWindow,
                            onToggleFloating: () => ToggleFloatingBtn_Click(this, new RoutedEventArgs()),
                            onToggleWorkflow: ToggleWorkflowEngineFromQuickAction,
                            onExitApp: ForceCloseApp,
                            isWorkflowEnabled: () => _reg.UserConfig.WorkflowEngineEnabled);
                        _traySvc.Initialize(this);
                    }
                }
                catch { }
            }

            ShowInTaskbar = false;
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Hide();

            if (ensureFloatingVisible && _reg.UserConfig.FloatingWindowEnabled)
            {
                ShowFloatingOverlayWindow();
            }

            SetStatus("主界面已缩小到系统托盘（右下角通知区域），桌面悬浮窗与后台协同继续运行。");
        }
        finally
        {
            Dispatcher.BeginInvoke(new Action(() => { _suppressMinimizeToTray = false; }), DispatcherPriority.ApplicationIdle);
        }
    }

    private void SyncCloseButtonActionControlsFromConfig()
    {
        if (!IsLoaded) return;
        _suppressEvents = true;
        try
        {
            string action = ResolveCloseActionChoice(_reg.UserConfig);
            if (ComboCloseButtonAction != null)
            {
                SelectComboByTag(ComboCloseButtonAction, action);
            }
            if (ChkCloseToFloating != null)
            {
                ChkCloseToFloating.IsChecked = _reg.UserConfig.CloseMainWindowToFloating;
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void WorkflowSwitchBtn_Click(object sender, RoutedEventArgs e)
    {
        ToggleWorkflowEngineFromQuickAction();
    }

    private void CloudRecTopBtn_Click(object sender, RoutedEventArgs e)
    {
        bool applied = Dialogs.ShowCloudRecommendationDialog(this, _reg, _cloudRecSvc);
        if (applied)
        {
            _reg.SyncActiveTeamProfileWithCurrentState(saveToDisk: true);
            SetStatus("✨ 已应用云端推荐配置并同步保存至当前激活方案。");
        }
        RefreshAllViews();
        _floatingWindow?.RefreshOverlayData(force: true);
    }

    private void ApplyAllRecommendedBtn_Click(object sender, RoutedEventArgs e)
    {
        var rep = _cloudRecSvc.BuildAndApplyReportFromCache(_reg, applyToAgents: true, saveToDisk: false);
        MarkSettingsDirty($"✨ 已预填云端推荐配置（来源：{rep.SourceName}）· 请点击「💾 保存配置」正式写入生效");
        RenderSettingsPage();

        if (!AppPaths.IsIsolatedTest && LongActionTracker.TryBegin(LongActionTracker.CloudSync, "同步中"))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                string fail = "";
                (bool FetchedOnline, int OnlineModelsAnalyzed, string SourceName) net = default;
                try
                {
                    net = _cloudRecSvc.FetchOnlineHintsOnly();
                }
                catch (Exception ex)
                {
                    fail = ex.Message;
                }
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (!IsLoaded) return;
                        if (!string.IsNullOrEmpty(fail))
                        {
                            SetStatus($"⚠ 云端同步失败：{TruncateStatus(fail)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
                            return;
                        }
                        if (!net.FetchedOnline)
                        {
                            SetStatus($"⚠ 云端同步未拉到在线数据（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
                            return;
                        }
                        var freshRep = _cloudRecSvc.BuildAndApplyReportFromCache(_reg, applyToAgents: true, net.FetchedOnline, net.OnlineModelsAnalyzed, net.SourceName, saveToDisk: false);
                        MarkSettingsDirty($"✨ 已同步最新在线评测预填推荐（来源：{freshRep.SourceName}，耗时 {(int)sw.Elapsed.TotalSeconds}s）· 请点击「💾 保存配置」正式生效");
                        RenderSettingsPage();
                    }
                    finally
                    {
                        EndLongAction(LongActionTracker.CloudSync);
                    }
                });
            });
        }
        else if (!AppPaths.IsIsolatedTest && LongActionTracker.IsBusy(LongActionTracker.CloudSync))
        {
            SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.CloudSync));
        }
    }

    public FloatingOverlayWindow GetOrCreateFloatingWindow()
    {
        if (_floatingWindow == null)
        {
            _floatingWindow = new FloatingOverlayWindow(_reg, this);
        }
        return _floatingWindow;
    }

    public void ShowFloatingOverlayWindow()
    {
        var fw = GetOrCreateFloatingWindow();
        if (!fw.IsVisible)
        {
            fw.Show();
        }
        fw.RefreshOverlayData(force: true);
        UpdateFloatingButtonBadge();
    }

    private bool _onboardingAutoPrompted;

    /// <summary>全新首次启动时弹出 3 步引导。隔离测试、已完成、以及已有启用项的老配置都不自动打扰。</summary>
    private void TryAutoShowOnboarding()
    {
        if (_onboardingAutoPrompted || !IsLoaded || AppPaths.IsIsolatedTest) return;
        _onboardingAutoPrompted = true;
        if (!OnboardingWizardDialog.ShouldAutoShowOnboarding(
                _reg.UserConfig,
                _reg.ConfigFileExistedAtLoad,
                AppPaths.IsIsolatedTest))
            return;
        OpenOnboardingWizard();
    }

    private void BtnReopenOnboarding_Click(object sender, RoutedEventArgs e) => OpenOnboardingWizard();

    private void HeaderOnboardingBtn_Click(object sender, RoutedEventArgs e) => OpenOnboardingWizard();

    public void OpenOnboardingWizard()
    {
        var dlg = new OnboardingWizardDialog(_reg, _discovery, this);
        if (AppPaths.IsIsolatedTest)
        {
            return;
        }
        if (IsLoaded)
        {
            dlg.Owner = this;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        bool accepted = false;
        try
        {
            accepted = dlg.ShowDialog() == true;
        }
        catch
        {
            accepted = _reg.UserConfig.HasCompletedOnboarding;
        }
        if (!accepted) return;
        ApplyOnboardingClosed();
    }

    private void ApplyOnboardingClosed()
    {
        _lastOverviewSignature = "";
        try { RenderOverviewPage(fastPoll: true); } catch { /* 向导关闭后的刷新不应打断主界面 */ }
        try { RenderSettingsPage(); } catch { }
        try { SyncAppSettingsControlsIfLoaded(); } catch { }
        if (_reg.UserConfig.FloatingWindowVisible)
        {
            if (!AppPaths.IsIsolatedTest)
                ShowFloatingOverlayWindow();
        }
        else if (_floatingWindow != null)
        {
            _floatingWindow.Hide();
            UpdateFloatingButtonBadge();
        }
    }

    public void UpdateFloatingButtonBadge()
    {
        bool enabled = _reg.UserConfig.FloatingWindowEnabled && (_floatingWindow?.IsVisible ?? _reg.UserConfig.FloatingWindowEnabled);
        ToggleFloatingBtn.Content = "悬浮窗";
        ToggleFloatingBtn.ToolTip = enabled ? "悬浮窗已开启，点击关闭" : "悬浮窗已关闭，点击开启";
        ToggleFloatingBtn.Style = (Style)FindResource("ActionBtn");
    }

    private void ToggleFloatingBtn_Click(object sender, RoutedEventArgs e)
    {
        bool currentlyVisible = _floatingWindow != null && _floatingWindow.IsVisible;
        if (currentlyVisible)
        {
            _reg.UserConfig.FloatingWindowEnabled = false;
            _reg.SaveUserConfig();
            _floatingWindow!.Hide();
            SetStatus("已隐藏桌面置顶悬浮窗（可随时点顶部「📌 桌面悬浮窗」重新开启）。");
        }
        else
        {
            _reg.UserConfig.FloatingWindowEnabled = true;
            _reg.SaveUserConfig();
            ShowFloatingOverlayWindow();
            SetStatus("已在桌面右上角开启置顶「多 AI 协同悬浮监控窗」。");
        }
        UpdateFloatingButtonBadge();
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src)
        {
            for (var cur = src; cur != null; cur = VisualTreeHelper.GetParent(cur))
            {
                if (cur is System.Windows.Controls.Primitives.ButtonBase || cur is ComboBox || cur is TextBox)
                    return;
            }
        }
        if (e.ClickCount == 2)
        {
            MaximizeRestoreBtn_Click(sender, e);
            return;
        }
        try { DragMove(); } catch { }
    }

    private void MaximizeRestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        if (MaximizeRestoreBtn != null)
        {
            MaximizeRestoreBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }
    }

    private void TopCloseWindowBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MinimizeToFloatBtn_Click(object sender, RoutedEventArgs e)
    {
        MinimizeMainWindowToTray(ensureFloatingVisible: true);
    }

    private void CheckAndConsumeWakeupSignal()
    {
        var wakeSignalPath = Path.Combine(AppPaths.UserDataDir, "wakeup_ui.signal");
        if (!File.Exists(wakeSignalPath)) return;

        string mode = "main";
        try
        {
            mode = File.ReadAllText(wakeSignalPath).Trim();
            File.Delete(wakeSignalPath);
        }
        catch { }

        ShowFloatingOverlayWindow();
        if (mode != "floating")
        {
            RestoreAndActivateMainWindow();
        }
    }

    public void RestoreAndActivateMainWindow()
    {
        _suppressMinimizeToTray = true;
        try
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }
        finally
        {
            Dispatcher.BeginInvoke(new Action(() => { _suppressMinimizeToTray = false; }), DispatcherPriority.ApplicationIdle);
        }
    }

    public void ActivateAndInspectDialogSession(string sessionId, string agentName)
    {
        RestoreAndActivateMainWindow();
        _selectedDialogSessionId = sessionId;
        NavDialogs.IsChecked = true;
        RefreshAllViews();
        SetStatus($"已从桌面悬浮窗定位到「{agentName}」分配工作的对话框：{sessionId}");
    }

    public void ActivateAndShowLatestInbox()
    {
        RestoreAndActivateMainWindow();
        OverviewOpenLatestInboxBtn_Click(this, new RoutedEventArgs());
    }

    public void ActivateAndShowAllDialogs()
    {
        RestoreAndActivateMainWindow();
        NavDialogs.IsChecked = true;
        RefreshAllViews();
    }

    public void ActivateAndShowAppSettings()
    {
        RestoreAndActivateMainWindow();
        NavAppSettings.IsChecked = true;
        RefreshAllViews();
    }

    private void ComboCloseButtonAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        if (ComboCloseButtonAction.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            SyncCloseButtonActionFields(_reg.UserConfig, tag);
            if (ChkCloseToFloating != null)
            {
                _suppressEvents = true;
                try { ChkCloseToFloating.IsChecked = _reg.UserConfig.CloseMainWindowToFloating; }
                finally { _suppressEvents = false; }
            }
            _reg.SaveUserConfig();
            string label = item.Content?.ToString() ?? tag;
            SetStatus($"已保存关闭按钮行为：{label}");
        }
    }

    private void TopOpenSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        NavAppSettings.IsChecked = true;
        RefreshAllViews();
    }

    public void SwitchToTabForTest(int index)
    {
        _suppressEvents = true;
        NavOverview.IsChecked = index == 1;
        NavDialogs.IsChecked = index == 2;
        NavSync.IsChecked = index == 3;
        NavOps.IsChecked = index == 4;
        NavSettings.IsChecked = index == 5;
        NavAppSettings.IsChecked = index == 6;
        if (NavAdvancedExpander != null && (index == 3 || index == 4))
            NavAdvancedExpander.IsExpanded = true;
        _suppressEvents = false;

        PageOverview.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageDialogs.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageSync.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageOps.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        PageAppSettings.Visibility = index == 6 ? Visibility.Visible : Visibility.Collapsed;
        RefreshAllViews();
    }

    private static Brush Hex(string h) => UiBrushPool.Get(h);

    private void SetStatus(string msg)
    {
        StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {msg}";
    }

    public void ReportStatus(string msg) => SetStatus(msg);

    private void ArmStaticLongActionButtons()
    {
        LongActionTracker.Arm(RescanBtn, LongActionTracker.ScanLocal, "扫描本机");
        LongActionTracker.Arm(SyncAllNowBtn, LongActionTracker.RulesSync, "⚡ 立即挂载同步");
        LongActionTracker.Arm(SyncAnchorsOverviewBtn, LongActionTracker.RulesSync, "同步规则锚点");
        LongActionTracker.Arm(InitFoundationBtn, LongActionTracker.RulesInit, "初始化工程目录");
        LongActionTracker.Arm(RestoreSyncBackupBtn, LongActionTracker.RulesRestore, "恢复上次备份");
        LongActionTracker.Arm(ExportShareZipBtn, LongActionTracker.ExportShare, "导出分享包");
        LongActionTracker.Arm(RunHeadlessDispatchBtn, LongActionTracker.HeadlessDispatch, "⚡ 分配任务并执行");
        LongActionTracker.Arm(BtnProbeNetworkNow, LongActionTracker.NetProbe, "检测网络");
        LongActionTracker.Arm(BtnDismissStuckModalsNow, LongActionTracker.DismissModals, "🧹 清理卡住弹窗");
        LongActionTracker.Arm(BtnResumeCheckpoint, LongActionTracker.ResumeCheckpoint, "断点续跑");
        LongActionTracker.Arm(CopyCapsuleBtn, LongActionTracker.CapsulePack, "复制脱敏上下文包");
        LongActionTracker.ApplyBusyIfNeeded(BtnCheckAppUpdate, LongActionTracker.AppUpdate, "立即检查更新");
    }

    private static void EndLongAction(string key) => LongActionTracker.EndAndRestore(key);

    private static string TruncateStatus(string? text, int max = 220)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= max ? t : t.Substring(0, max) + "…";
    }

    private string BuildCliFinishStatus(string agentName, bool isUpdateMode, bool ok, string failMsg, string version, int seconds)
    {
        if (!ok)
            return $"⚠ {agentName} CLI {(isUpdateMode ? "更新" : "安装")}失败：{TruncateStatus(failMsg)}（耗时 {seconds}s）";
        var shown = (version ?? "").Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(shown))
            return $"✅ {agentName} CLI {(isUpdateMode ? "已更新" : "已安装")}（耗时 {seconds}s）";
        return isUpdateMode
            ? $"✅ {agentName} CLI 已更新至 v{shown}（耗时 {seconds}s）"
            : $"✅ {agentName} CLI 已安装至 v{shown}（耗时 {seconds}s）";
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;

        // 若从「AI 阵容与路由」切出且存在未保存的配置草稿，先询问用户是否保存
        if (PageSettings.Visibility == Visibility.Visible &&
            NavSettings.IsChecked != true && _hasUnsavedSettingsChanges && !AppPaths.IsIsolatedTest)
        {
            var ans = MessageBox.Show(
                this,
                "您在「AI 阵容与路由」中有尚未保存的配置修改。\n\n• 点「是」：立即保存配置并生效\n• 点「否」：放弃未保存的修改并还原\n• 点「取消」：留在本页继续调整",
                "未保存的配置修改",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (ans == MessageBoxResult.Cancel)
            {
                _suppressEvents = true;
                NavSettings.IsChecked = true;
                _suppressEvents = false;
                return;
            }
            if (ans == MessageBoxResult.Yes)
            {
                CommitRosterSettingsSave();
            }
            else
            {
                DiscardRosterSettingsChanges();
            }
        }

        if (NavAdvancedExpander != null && (NavSync.IsChecked == true || NavOps.IsChecked == true))
            NavAdvancedExpander.IsExpanded = true;

        PageOverview.Visibility = NavOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageDialogs.Visibility = NavDialogs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSync.Visibility = NavSync.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageOps.Visibility = NavOps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAppSettings.Visibility = NavAppSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshCurrentVisiblePage();
        UpdateWorkflowDiagramAnimationGate();
        if (NavOverview.IsChecked == true) RefreshWorkflowDiagram();
    }

    private void RefreshCurrentVisiblePage()
    {
        if (NavOverview.IsChecked == true) { _lastOverviewSignature = ""; RenderOverviewPage(); }
        else if (NavDialogs.IsChecked == true) RenderDialogsPage();
        else if (NavSync.IsChecked == true) RenderSyncPage();
        else if (NavOps.IsChecked == true) RenderOpsPage();
        else if (NavSettings.IsChecked == true)
        {
            if (AppPaths.IsIsolatedTest)
            {
                LocalModelCatalogService.SyncAllAgentsWithLocalClientModels(_reg.UserConfig.Agents, invalidateCache: true);
                RenderSettingsPage();
            }
            else
            {
                RenderSettingsPage();
                var beforeSig = ModelCatalogSignature(_reg.UserConfig.Agents);
                var agents = _reg.UserConfig.Agents;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    LocalModelCatalogService.SyncAllAgentsWithLocalClientModels(agents, invalidateCache: true);
                    return !string.Equals(beforeSig, ModelCatalogSignature(agents), StringComparison.Ordinal);
                }).ContinueWith(t =>
                {
                    if (t.IsFaulted || !t.Result) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (IsLoaded && NavSettings.IsChecked == true) RenderSettingsPage();
                    });
                });
            }
        }
        else if (NavAppSettings.IsChecked == true) RenderAppSettingsPage();
    }

    private void RefreshProjectCombo()
    {
        _suppressEvents = true;
        ProjectCombo.Items.Clear();
        var active = _reg.GetActiveProject();
        foreach (var p in _reg.UserConfig.Projects)
        {
            var item = new ComboBoxItem { Content = p.Name, Tag = p.Id };
            ProjectCombo.Items.Add(item);
            if (active != null && p.Id == active.Id) ProjectCombo.SelectedItem = item;
        }
        _suppressEvents = false;
    }

    public void RefreshAllViews()
    {
        _lastOverviewSignature = "";
        TriggerBackgroundProbesAsync(refreshOverviewWhenChanged: false);

        if (AppPaths.IsIsolatedTest)
        {
            var proj = _reg.GetActiveProject();
            var mcp = _discovery.ProbeProjectMcp(proj);
            McpBadgeText.Text = mcp.Summary;
            McpBadgeText.Foreground = (mcp.GodotHttpPortOpen || mcp.GodotWsPortOpen) ? Hex("#34D399") : Hex("#38BDF8");
            UpdateWorkflowSwitchBadges();
            RenderOverviewPage();
            RenderDialogsPage();
            RenderSyncPage();
            RenderOpsPage();
            RenderSettingsPage();
            RenderAppSettingsPage();
            return;
        }

        UpdateWorkflowSwitchBadges();
        RefreshCurrentVisiblePage();
    }

    private void TriggerBackgroundProbesAsync(bool refreshOverviewWhenChanged)
    {
        if (_isProbingWindowsAsync || AppPaths.IsIsolatedTest) return;
        _isProbingWindowsAsync = true;
        var proj = _reg.GetActiveProject();
        var agentsCopy = _reg.UserConfig.Agents.Where(a => a.Enabled || a.Detected).ToList();

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var nextWinMap = new Dictionary<string, (bool IsRunning, int Pid, string WindowBadge)>(StringComparer.OrdinalIgnoreCase);
                foreach (var ag in agentsCopy)
                {
                    if (AutoDispatchService.ResolveGuiExecutable(ag) != null)
                    {
                        nextWinMap[ag.Id] = _autoDispatch.ProbeWindowProcessOnly(ag);
                    }
                }

                var nextBusyMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var ag in agentsCopy)
                {
                    if (!nextWinMap.TryGetValue(ag.Id, out var winSt) || !winSt.IsRunning) continue;
                    if (AutoDispatchService.ResolveGuiExecutable(ag) == null) continue;
                    var busy = AutoDispatchService.ProbeDesktopAgentBusySilent(ag);
                    nextBusyMap[ag.Id] = busy.IsWorking;
                }

                var mcp = _discovery.ProbeProjectMcp(proj);
                Dispatcher.BeginInvoke(() =>
                {
                    bool winChanged = nextWinMap.Count != _cachedWindowStates.Count ||
                                      nextWinMap.Any(kv => !_cachedWindowStates.TryGetValue(kv.Key, out var old) ||
                                                           old.IsRunning != kv.Value.IsRunning ||
                                                           old.Pid != kv.Value.Pid ||
                                                           old.WindowBadge != kv.Value.WindowBadge);
                    bool busyChanged = nextBusyMap.Count != _cachedDesktopBusyFlags.Count ||
                                       nextBusyMap.Any(kv => !_cachedDesktopBusyFlags.TryGetValue(kv.Key, out var oldBusy) || oldBusy != kv.Value) ||
                                       _cachedDesktopBusyFlags.Keys.Any(id => !nextBusyMap.ContainsKey(id));
                    _cachedWindowStates = nextWinMap;
                    _cachedDesktopBusyFlags = nextBusyMap;
                    if (McpBadgeText != null)
                    {
                        McpBadgeText.Text = mcp.Summary;
                        McpBadgeText.Foreground = (mcp.GodotHttpPortOpen || mcp.GodotWsPortOpen) ? Hex("#34D399") : Hex("#38BDF8");
                    }
                    if (winChanged || busyChanged)
                    {
                        if (refreshOverviewWhenChanged && IsLoaded && NavOverview.IsChecked == true)
                        {
                            _lastOverviewSignature = "";
                            RenderOverviewPage(fastPoll: true);
                        }
                        if (IsLoaded && NavSettings.IsChecked == true)
                        {
                            RenderScanSection();
                        }
                        _floatingWindow?.RefreshOverlayData(force: busyChanged);
                    }
                });
            }
            catch { }
            finally
            {
                _isProbingWindowsAsync = false;
            }
        });
    }

    private void TriggerAsyncSessionsScanIfNeeded(bool force)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null || string.IsNullOrWhiteSpace(proj.PrimaryRoot) || !Directory.Exists(proj.PrimaryRoot)) return;
        if (_isLoadingSessionsAsync) return;
        if (!force &&
            string.Equals(_cachedSessionsProjectId, proj.Id, StringComparison.OrdinalIgnoreCase) &&
            (DateTime.UtcNow - _lastSessionsScanUtc).TotalSeconds < 6)
        {
            return;
        }

        _isLoadingSessionsAsync = true;
        var targetProjId = proj.Id;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var list = _sessionSvc.DiscoverProjectSessions(proj, null);
                var inv = _sync.InspectRulesAndSkillsInventory(_reg, proj);
                int skillCnt = inv.Count(x => x.Kind == "skill");
                int ruleCnt = inv.Count(x => x.Kind == "rule" || x.Kind == "global_rule");

                Dispatcher.BeginInvoke(() =>
                {
                    _cachedProjectSessions = list;
                    _cachedSessionsProjectId = targetProjId;
                    _lastSessionsScanUtc = DateTime.UtcNow;
                    _cachedSkillCount = skillCnt;
                    _cachedRuleCount = ruleCnt;
                    _hasInventoryCounts = true;
                    _lastSummaryStatsTime = DateTime.MinValue;
                    if (IsLoaded && NavOverview.IsChecked == true)
                    {
                        _lastOverviewSignature = "";
                        RenderOverviewPage(fastPoll: true);
                    }
                    if (IsLoaded && NavDialogs.IsChecked == true)
                    {
                        RenderDialogsPage(skipAsyncTrigger: true);
                    }
                });
            }
            catch { }
            finally
            {
                _isLoadingSessionsAsync = false;
            }
        });
    }

    // ==================== PAGE 1: 协同总览与实时状态 ====================

    private void RenderOverviewPage(bool fastPoll = false)
    {
        var proj = _reg.GetActiveProject();
        var liveMap = AutoDispatchService.ReadMergedGlobalWorkStatesWithDesktopLive();
        var enabledAgents = _reg.UserConfig.Agents.Where(a => a.Enabled).ToList();
        var masterAg = RoutingService.ResolveMasterPlanner(_reg, proj);
        bool wfOn = _reg.UserConfig.WorkflowEngineEnabled;
        var interruptedCp = NetworkResilienceService.LoadInterruptedCheckpoint(proj);

        var sigBuilder = new StringBuilder();
        sigBuilder.Append(proj?.Id ?? "").Append('|').Append(wfOn).Append('|').Append(masterAg?.Id ?? "");
        sigBuilder.Append('|').Append(interruptedCp?.ChainId ?? "").Append(':').Append(interruptedCp?.UpdatedAt ?? "").Append(':').Append(_isResumingCheckpointInBackground);
        foreach (var ag in enabledAgents)
        {
            sigBuilder.Append(';').Append(ag.Id).Append(':').Append(ag.PreferredEffortTier);
            if (liveMap.TryGetValue(ag.Id, out var lv))
            {
                sigBuilder.Append(':').Append(lv.Status).Append(':').Append(lv.WorkflowPhase).Append(':').Append(lv.LastFailureBadge).Append(':').Append(lv.UpdatedAt).Append(':').Append(lv.CurrentRound).Append(':').Append(lv.SessionId);
            }
            if (_cachedWindowStates.TryGetValue(ag.Id, out var ws))
            {
                sigBuilder.Append(':').Append(ws.IsRunning ? "1" : "0");
            }
        }
        var currentSig = sigBuilder.ToString();
        if (fastPoll && string.Equals(_lastOverviewSignature, currentSig, StringComparison.Ordinal))
        {
            // 签名未变时仍轻量刷新流程图动画闸门（阶段可能仅由全局态驱动）
            SyncWorkflowDiagramAfterOverviewRender();
            return;
        }
        _lastOverviewSignature = currentSig;

        OverviewAgentsCards.Children.Clear();

        if (OverviewModeBadge != null && OverviewModeBadgeText != null && OverviewMasterBrainText != null)
        {
            OverviewModeBadge.Background = wfOn ? Hex("#12352C") : Hex("#242E40");
            OverviewModeBadgeText.Text = wfOn ? "运行中" : "独立模式";
            OverviewMasterBrainText.Text = wfOn
                ? (masterAg?.Name ?? "未指定")
                : "未开启";
            OverviewMasterBrainText.ToolTip = wfOn
                ? $"{masterAg?.Name ?? "未指定"} 负责接单、分工和验收"
                : "协同已关闭，各 AI 独立工作";
        }

        if (InterruptedCheckpointBanner != null)
        {
            if (interruptedCp != null && !_isResumingCheckpointInBackground)
            {
                InterruptedCheckpointBanner.Visibility = Visibility.Visible;
                CheckpointBannerTitle.Text = $"📡 检测到因「{interruptedCp.LastFailureLabel}」暂停的任务链断点（第 {interruptedCp.CurrentRound}/{interruptedCp.MaxRounds} 轮 · 目标：{interruptedCp.TargetAgentName}）";
                var shortInst = interruptedCp.TaskInstruction.Length > 85
                    ? interruptedCp.TaskInstruction[..85] + "..."
                    : interruptedCp.TaskInstruction;
                CheckpointBannerDetail.Text = $"暂停时间：{interruptedCp.UpdatedAt} | 已自动重连尝试 {interruptedCp.NetworkRetriesUsed} 次 | 任务内容：{shortInst}";
            }
            else
            {
                InterruptedCheckpointBanner.Visibility = Visibility.Collapsed;
            }
        }

        for (int i = 0; i < enabledAgents.Count; i++)
        {
            var ag = enabledAgents[i];
            bool isLast = i == enabledAgents.Count - 1;
            liveMap.TryGetValue(ag.Id, out var agLive);

            bool isWorking = agLive != null && agLive.Status == "working";

            var rowBorder = new Border
            {
                Background = isWorking ? Hex("#1B1914") : Hex("#141B26"),
                BorderBrush = isWorking ? Hex("#B45309") : Hex("#222C3D"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 0, 0, isLast ? 0 : 12)
            };

            var stack = new StackPanel();
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleWrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var overviewAvatar = OfficialBrandIconService.CreateCircularAvatarVisual(
                ag.Id, 22, ag, isWorking ? "#B45309" : "#334155", 1.2);
            overviewAvatar.Margin = new Thickness(0, 0, 8, 0);
            overviewAvatar.VerticalAlignment = VerticalAlignment.Center;
            titleWrap.Children.Add(overviewAvatar);
            titleWrap.Children.Add(new TextBlock
            {
                Text = ag.Name,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Hex("#F1F5F9"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            bool isMaster = masterAg != null && string.Equals(masterAg.Id, ag.Id, StringComparison.OrdinalIgnoreCase);
            var roleTag = isMaster
                ? "主脑"
                : (isWorking ? "执行中" : (agLive?.Status == "error" ? "异常" : "在岗"));
            titleWrap.Children.Add(new Border
            {
                Background = Hex("#1E2736"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = roleTag,
                    FontSize = 11,
                    Foreground = isMaster ? Hex("#C4B5FD") : Hex("#94A3B8")
                }
            });

            var cachedWin = GetCachedWindowState(ag);
            Grid.SetColumn(titleWrap, 0);

            var actionWrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            if (agLive != null && (agLive.Status == "error" || agLive.WorkflowPhase == "awaiting_user_choice"))
            {
                var resolveBtn = new Button
                {
                    Content = "🛠 解决",
                    Style = (Style)FindResource("PrimaryBtn"),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    FontSize = 11.5,
                    ToolTip = "选择解决方式：清理卡住弹窗后台重试 / 切纯 CLI 通道 / 改派其他 AI / 交由主脑接管"
                };
                resolveBtn.Click += (_, _) => OpenStallResolutionDialogAndExecute(ag, this);
                actionWrap.Children.Add(resolveBtn);

                var resetErrBtn = new Button
                {
                    Content = "复位",
                    Style = (Style)FindResource("AmberBtn"),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    FontSize = 11.5,
                    ToolTip = "后台静默清理卡死弹窗并清除此 AI 的异常状态记录"
                };
                resetErrBtn.Click += (_, _) =>
                {
                    _ = System.Threading.Tasks.Task.Run(AutoDispatchService.DismissStuckAgentModalsSilent);
                    AutoDispatchService.ClearAgentLiveState(ag.Id, proj);
                    _lastOverviewSignature = "";
                    RenderOverviewPage();
                    _floatingWindow?.RefreshOverlayData(force: true);
                    SetStatus($"已清理「{ag.Name}」的异常状态记录（并在后台静默关闭残留弹窗），恢复待命。");
                };
                actionWrap.Children.Add(resetErrBtn);
            }

            if (agLive != null && !string.IsNullOrWhiteSpace(agLive.SessionId))
            {
                var targetSid = agLive.SessionId;
                var viewDlgBtn = new Button
                {
                    Content = "对话",
                    Style = (Style)FindResource("ActionBtn"),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    FontSize = 11.5
                };
                viewDlgBtn.Click += (_, _) =>
                {
                    _selectedDialogSessionId = targetSid;
                    NavDialogs.IsChecked = true;
                    SetStatus($"已定位打开「{ag.Name}」分配工作的对话框：{targetSid}");
                };
                actionWrap.Children.Add(viewDlgBtn);
            }

            if (AutoDispatchService.ResolveGuiExecutable(ag) != null)
            {
                var openWinBtn = new Button
                {
                    Content = cachedWin.IsRunning ? "窗口" : "启动",
                    Style = (Style)FindResource("ActionBtn"),
                    Padding = new Thickness(8, 3, 8, 3),
                    FontSize = 11.5
                };
                openWinBtn.Click += (_, _) =>
                {
                    var msg = _autoDispatch.EnsureOrActivateAgentWindow(ag, proj?.PrimaryRoot, activateIfRunning: true);
                    TriggerBackgroundProbesAsync(refreshOverviewWhenChanged: true);
                    SetStatus(msg);
                };
                actionWrap.Children.Add(openWinBtn);
            }
            else
            {
                var cfgOverviewBtn = new Button
                {
                    Content = "配置",
                    Style = (Style)FindResource("ActionBtn"),
                    Padding = new Thickness(8, 3, 8, 3),
                    FontSize = 12,
                    ToolTip = "打开这个 AI 的模型和权限"
                };
                var cfgAgent = ag;
                cfgOverviewBtn.Click += (_, _) => OpenRosterAgentConfig(cfgAgent);
                actionWrap.Children.Add(cfgOverviewBtn);
            }
            Grid.SetColumn(actionWrap, 1);
            topGrid.Children.Add(titleWrap);
            topGrid.Children.Add(actionWrap);
            stack.Children.Add(topGrid);

            // 精简第 2 行：仅 1 句核心状态/工作摘要 + 模型简名（克制去噪）
            CloudRecommendationService.EnsureAgentModelTiersPopulated(ag);
            string activeModelBrief = (agLive != null && !string.IsNullOrWhiteSpace(agLive.SelectedModel))
                ? agLive.SelectedModel
                : (ag.PreferredEffortTier == "auto" ? $"自动选档 ({ag.HighTierModel})" : ag.HighTierModel);
            activeModelBrief = TruncateOneLine(activeModelBrief, 36);

            string summaryLine;
            Brush summaryBrush = Hex("#94A3B8");
            if (ag.Id == "antigravity")
            {
                var workingOthers = liveMap.Values.Count(v => v.Status == "working" && v.AgentId != "antigravity");
                summaryLine = workingOthers > 0
                    ? $"正在统筹并监控 {workingOthers} 个实操任务   ·   模型：{activeModelBrief}"
                    : $"主脑待命（负责接单分析、按工作定位路由与验收）   ·   模型：{activeModelBrief}";
            }
            else if (agLive != null && !string.IsNullOrWhiteSpace(agLive.TaskSummary))
            {
                if (agLive.Status == "working")
                {
                    var shortTask = AutoDispatchService.ExtractCleanTaskBrief(agLive.TaskSummary, 28);
                    bool desktopLive = agLive.IsDesktopLiveOverlay
                                       || (agLive.TaskSummary ?? "").Contains("桌面端实时生成中", StringComparison.Ordinal);
                    summaryLine = desktopLive
                        ? $"编码实操中：{shortTask}   ·   {activeModelBrief}"
                        : $"执行中 (第 {agLive.CurrentRound}/{agLive.MaxRounds} 轮)：{shortTask}   ·   {activeModelBrief}";
                    summaryBrush = Hex("#FCD34D");
                }
                else if (agLive.Status == "error")
                {
                    var errBadge = !string.IsNullOrWhiteSpace(agLive.LastFailureBadge) ? agLive.LastFailureBadge : "执行中断";
                    if (agLive.WorkflowPhase == "awaiting_user_choice")
                    {
                        summaryLine = $"执行卡住待处理 ({errBadge}) · 点右上「🛠 解决」选择解决方式   ·   {activeModelBrief}";
                    }
                    else if (agLive.WorkflowPhase == "interrupted_net" && errBadge.Contains("断网"))
                    {
                        summaryLine = $"断网暂停保护 (第 {agLive.CurrentRound} 轮待续跑)   ·   {activeModelBrief}";
                    }
                    else if (agLive.CanResumeInterruptedChain)
                    {
                        summaryLine = $"任务暂停 ({errBadge} · 第 {agLive.CurrentRound} 轮待处理) · 点「🛠 解决」   ·   {activeModelBrief}";
                    }
                    else
                    {
                        summaryLine = $"最近任务异常：{errBadge} ({agLive.UpdatedAt}) · 点右上「🛠 解决」或「复位」   ·   {activeModelBrief}";
                    }
                    summaryBrush = Hex("#FCA5A5");

                    var detailTip = new StringBuilder();
                    detailTip.AppendLine($"异常类型：{errBadge}");
                    if (!string.IsNullOrWhiteSpace(agLive.UpdatedAt)) detailTip.AppendLine($"发生时间：{agLive.UpdatedAt}");
                    if (!string.IsNullOrWhiteSpace(agLive.FailoverNote)) detailTip.AppendLine($"故障转移：{agLive.FailoverNote}");
                    if (!string.IsNullOrWhiteSpace(agLive.LatestLogTail))
                    {
                        var tailSnippet = agLive.LatestLogTail.Trim();
                        if (tailSnippet.Length > 360) tailSnippet = tailSnippet[..360] + "...";
                        detailTip.AppendLine($"诊断详情：{tailSnippet}");
                    }
                    detailTip.Append("处理建议：若属偶发超时或账号配额耗尽，点击右上「复位」即可清除历史状态；派单前建议保持桌面端窗口开启或切换本地网关。");
                    rowBorder.ToolTip = detailTip.ToString();
                }
                else if (agLive.Status == "done")
                {
                    var shortTask = AutoDispatchService.ExtractCleanTaskBrief(agLive.TaskSummary, 28);
                    var roundTxt = agLive.CurrentRound > 1 ? $"{agLive.CurrentRound}轮验收通过" : "首轮验收通过";
                    summaryLine = $"{roundTxt}：{shortTask}   ·   {activeModelBrief}";
                    summaryBrush = Hex("#A7F3D0");
                }
                else
                {
                    summaryLine = $"空闲待命 · 定位：{FormatAgentRolesSummary(ag, " / ", 4)}   ·   {activeModelBrief}";
                }
            }
            else
            {
                summaryLine = $"空闲待命 · 定位：{FormatAgentRolesSummary(ag, " / ", 4)}   ·   {activeModelBrief}";
            }

            string stateWord = agLive?.Status switch
            {
                "working" => "正在执行",
                "error" => "待处理",
                "done" => "刚完成",
                _ => "空闲待命"
            };
            var modelShort = TruncateOneLine(activeModelBrief, 18);
            summaryLine = $"{modelShort} · {stateWord}";
            rowBorder.ToolTip ??= $"{ag.Name}\n{activeModelBrief}\n{stateWord}";
            stack.Children.Add(new TextBlock
            {
                Text = summaryLine,
                FontSize = 12.5,
                Foreground = summaryBrush,
                Margin = new Thickness(0, 8, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = rowBorder.ToolTip
            });

            rowBorder.Child = stack;
            OverviewAgentsCards.Children.Add(rowBorder);
        }

        // 右栏：最近一次跨 AI 派单与反馈卡片
        if (proj != null && Directory.Exists(proj.PrimaryRoot))
        {
            var workingList = liveMap.Values.Where(v => v.Status == "working").ToList();
            var inboxDir = Path.Combine(_bus.GetCollabDir(proj), "inbox");
            var latestInbox = Directory.Exists(inboxDir)
                ? Directory.GetFiles(inboxDir, "*.md").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;

            if (workingList.Count > 0)
            {
                var names = string.Join("、", workingList.Select(w => w.AgentName));
                LatestDispatchHeaderText.Text = $"⚡ 正在协同工作中（共 {workingList.Count} 个 AI 工具）：{names}";
                LatestDispatchHeaderText.Foreground = Hex("#FBBF24");
                var firstW = workingList[0];
                var shortTask = AutoDispatchService.ExtractCleanTaskBrief(firstW.TaskSummary, 32);
                LatestDispatchMetaText.Text = $"对话框：{firstW.SessionId} | 当前任务：{shortTask}";
                LatestDispatchSnippetText.Text = string.Join("\n", workingList.Select(w =>
                    $"• [{w.AgentName} · 对话框 {(string.IsNullOrEmpty(w.SessionId) ? "寻址中" : w.SessionId[..Math.Min(8, w.SessionId.Length)])}]：{TruncateOneLine(ExtractCleanPreviewSnippet(w.LatestLogTail), 85)}"));
            }
            else if (latestInbox != null)
            {
                var fi = new FileInfo(latestInbox);
                var lines = File.ReadAllLines(latestInbox, Encoding.UTF8);
                var sessLine = lines.FirstOrDefault(l => l.Contains("关联项目对话框"))?.TrimStart('-', ' ') ?? "";
                var bodyLines = lines.SkipWhile(l => !l.StartsWith("---")).Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).Take(4);
                LatestDispatchHeaderText.Text = $"✅ 最近反馈文件：{fi.Name}";
                LatestDispatchHeaderText.Foreground = Hex("#6EE7B7");
                LatestDispatchMetaText.Text = $"回收时间：{fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}" + (string.IsNullOrEmpty(sessLine) ? "" : $" | {sessLine}");
                LatestDispatchSnippetText.Text = string.Join("\n", bodyLines);
            }
            else
            {
                LatestDispatchHeaderText.Text = "尚无历史自动派单反馈记录";
                LatestDispatchHeaderText.Foreground = Hex("#93C5FD");
                LatestDispatchMetaText.Text = "还没有派单回执";
                LatestDispatchSnippetText.Text = "";
            }

            if (AppPaths.IsIsolatedTest && !_hasInventoryCounts)
            {
                var inv = _sync.InspectRulesAndSkillsInventory(_reg, proj);
                _cachedSkillCount = inv.Count(x => x.Kind == "skill");
                _cachedRuleCount = inv.Count(x => x.Kind == "rule" || x.Kind == "global_rule");
                _cachedProjectSessions = _sessionSvc.DiscoverProjectSessions(proj, null);
                _cachedSessionsProjectId = proj.Id;
                _hasInventoryCounts = true;
            }
            else if (!AppPaths.IsIsolatedTest && (!fastPoll || !_hasInventoryCounts || (DateTime.Now - _lastSummaryStatsTime).TotalSeconds >= 20))
            {
                TriggerAsyncSessionsScanIfNeeded(force: !_hasInventoryCounts);
            }

            if (string.IsNullOrEmpty(_cachedSummaryStatsText) || _lastSummaryStatsTime == DateTime.MinValue || (DateTime.Now - _lastSummaryStatsTime).TotalSeconds >= 15)
            {
                var sessions = _cachedProjectSessions;
                int agCnt = sessions.Count(s => s.AgentId == "antigravity");
                int codexCnt = sessions.Count(s => s.AgentId == "codex_cli");
                int curCnt = sessions.Count(s => s.AgentId == "cursor");
                string sessSummary = sessions.Count > 0 || AppPaths.IsIsolatedTest
                    ? $"共 {sessions.Count} 个（Antigravity {agCnt} · Codex/Grok {codexCnt} · Cursor {curCnt}）"
                    : (_isLoadingSessionsAsync ? "正在后台检索统计中..." : "0 个");
                string assetSummary = _hasInventoryCounts
                    ? $"{_cachedSkillCount} 个技能 (Skills) · {_cachedRuleCount} 条规则 (Rules，含全局 GEMINI.md 强制协同铁律)"
                    : "正在后台清点共享规则与技能...";

                _cachedSummaryStatsText =
                    $"• 当前工程目录：{proj.PrimaryRoot}\n" +
                    $"• F:\\AIChat 项目对话框：{sessSummary}\n" +
                    $"• 已同步共享资产：{assetSummary}";
                _lastSummaryStatsTime = DateTime.Now;
            }
            OverviewProjectSummaryText.Text = _cachedSummaryStatsText;
        }

        SyncWorkflowDiagramAfterOverviewRender();
    }

    private static string ExtractCleanPreviewSnippet(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) &&
                        !l.StartsWith("# 自动派单产出") &&
                        !l.StartsWith("- 执行时间") &&
                        !l.StartsWith("- 执行模式") &&
                        !l.StartsWith("- 关联项目对话框") &&
                        l != "---")
            .ToList();
        return lines.Count > 0 ? string.Join(" ", lines.Take(2)) : raw.Trim();
    }

    private static string TruncateOneLine(string? s, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var one = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length <= maxLen ? one : one[..maxLen] + "...";
    }

    private Border MakeStatusPill(string text, string bg, string bd, string fg)
    {
        return new Border
        {
            Background = Hex(bg),
            BorderBrush = Hex(bd),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 6, 2),
            Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = Hex(fg) }
        };
    }

    private void OverviewRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        _lastOverviewSignature = "";
        TriggerBackgroundProbesAsync(refreshOverviewWhenChanged: true);
        TriggerAsyncSessionsScanIfNeeded(force: true);
        RenderOverviewPage();
        SetStatus("已刷新所有 AI 窗口状态与最近派单进度。");
    }

    private void ClearLiveStatusBtn_Click(object sender, RoutedEventArgs e)
    {
        AutoDispatchService.ClearFinishedOrStaleLiveStates();
        NetworkResilienceService.ClearInterruptedCheckpoint(_reg.GetActiveProject());
        _lastOverviewSignature = "";
        RenderOverviewPage();
        _floatingWindow?.RefreshOverlayData(force: true);
        SetStatus("已清理历史完成与残留状态记录，工作台与悬浮窗已归零。");
    }

    private void JumpToDialogsBtn_Click(object sender, RoutedEventArgs e) => NavDialogs.IsChecked = true;
    private void JumpToSyncBtn_Click(object sender, RoutedEventArgs e) => NavSync.IsChecked = true;
    private void JumpToSettingsBtn_Click(object sender, RoutedEventArgs e) => NavSettings.IsChecked = true;

    private void OpenRosterAgentConfig(AgentRosterEntry agent)
    {
        if (!Dialogs.ShowAgentConfigDialog(this, _reg, agent, _cloudRecSvc)) return;
        _lastOverviewSignature = "";
        RefreshAllViews();
        _floatingWindow?.RefreshOverlayData(force: true);
    }

    private void OverviewOpenLatestInboxBtn_Click(object sender, RoutedEventArgs e)
    {
        NavDialogs.IsChecked = true;
        ShowLatestInboxInReaderBtn_Click(sender, e);
    }

    // ==================== PAGE 2: 项目对话框与聊天记录 (F:\AIChat 专职浏览器) ====================

    private void RenderDialogsPage(bool skipAsyncTrigger = false)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null || string.IsNullOrWhiteSpace(proj.PrimaryRoot) || !Directory.Exists(proj.PrimaryRoot)) return;

        // 渲染底部派单栏下拉框
        _suppressEvents = true;
        var prevTarget = (HeadlessTargetCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        HeadlessTargetCombo.Items.Clear();
        var bestAuto = _autoDispatch.ResolveTargetAgent(_reg, "auto");
        var autoLabel = bestAuto != null ? $"● 自动优选 ({bestAuto.Name})" : "● 自动优选后台可用 AI";
        HeadlessTargetCombo.Items.Add(new ComboBoxItem { Content = autoLabel, Tag = "auto" });

        foreach (var ag in _autoDispatch.GetHeadlessCapableAgents(_reg))
        {
            var cbi = new ComboBoxItem { Content = $"▸ {ag.Name}", Tag = ag.Id };
            HeadlessTargetCombo.Items.Add(cbi);
            if (ag.Id == prevTarget) HeadlessTargetCombo.SelectedItem = cbi;
        }
        if (HeadlessTargetCombo.SelectedIndex < 0) HeadlessTargetCombo.SelectedIndex = 0;
        _suppressEvents = false;

        List<ExternalDialogSessionInfo> allSessions;
        if (AppPaths.IsIsolatedTest)
        {
            allSessions = _sessionSvc.DiscoverProjectSessions(proj, null);
            _cachedProjectSessions = allSessions;
            _cachedSessionsProjectId = proj.Id;
            _lastSessionsScanUtc = DateTime.UtcNow;
        }
        else if (!string.Equals(_cachedSessionsProjectId, proj.Id, StringComparison.OrdinalIgnoreCase) || _cachedProjectSessions.Count == 0)
        {
            allSessions = _cachedProjectSessions;
            if (!skipAsyncTrigger) TriggerAsyncSessionsScanIfNeeded(force: true);
        }
        else
        {
            if (!skipAsyncTrigger) TriggerAsyncSessionsScanIfNeeded(force: false);
            allSessions = _cachedProjectSessions;
        }
        RefreshTargetDialogCombo(proj, allSessions);

        // 按左侧筛选条件过滤会话列表
        var filterTag = (DialogAgentFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var keyword = DialogSearchBox?.Text?.Trim() ?? "";

        var liveMap = AutoDispatchService.ReadLiveWorkStatesForProject(proj);
        var activeAssignedIds = new HashSet<string>(
            liveMap.Values.Where(v => !string.IsNullOrWhiteSpace(v.SessionId)).Select(v => v.SessionId),
            StringComparer.OrdinalIgnoreCase);

        var filtered = allSessions.Where(s =>
        {
            if (filterTag != "all" && !string.Equals(s.AgentId, filterTag, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(keyword))
            {
                return s.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                       s.SessionId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                       s.AgentName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                       s.LastAssistantMessage.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        })
        .OrderByDescending(s => activeAssignedIds.Contains(s.SessionId))
        .ThenByDescending(s => s.UpdatedAt, StringComparer.OrdinalIgnoreCase)
        .ToList();

        int renderLimit = _showAllDialogs ? filtered.Count : Math.Min(filtered.Count, 45);
        DialogBrowserCountTitle.Text = allSessions.Count == 0 && _isLoadingSessionsAsync
            ? "正在读取对话"
            : $"对话 {filtered.Count}/{allSessions.Count}";
        DialogCardsStack.Children.Clear();
        _dialogCardEntries.Clear();

        if (filtered.Count == 0)
        {
            DialogCardsStack.Children.Add(new TextBlock
            {
                Text = _isLoadingSessionsAsync
                    ? "正在读取对话"
                    : "没有匹配的对话",
                Foreground = Hex("#8C9BB3"),
                FontSize = 12,
                Margin = new Thickness(4, 12, 4, 0),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        for (int i = 0; i < renderLimit; i++)
        {
            var sess = filtered[i];
            bool isSelected = string.Equals(sess.SessionId, _selectedDialogSessionId, StringComparison.OrdinalIgnoreCase);
            var matchedLive = liveMap.Values.FirstOrDefault(v => string.Equals(v.SessionId, sess.SessionId, StringComparison.OrdinalIgnoreCase));

            var card = new Border
            {
                Background = isSelected ? Hex("#1E3148") : (matchedLive != null ? Hex("#1A2636") : Hex("#151C29")),
                BorderBrush = isSelected ? Hex("#38BDF8") : (matchedLive?.Status == "working" ? Hex("#F59E0B") : (matchedLive != null ? Hex("#10B981") : Hex("#243044"))),
                BorderThickness = new Thickness(isSelected || matchedLive != null ? 1.5 : 1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"SessionId: {sess.SessionId}\n记录: {sess.FilePath}\n所属工程: {sess.ProjectDir}"
            };

            string liveMark = matchedLive == null ? "" : (matchedLive.Status == "working" ? " · 执行中" : " · 已派单");
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = sess.Title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Hex("#E6EDF7"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            sp.Children.Add(new TextBlock
            {
                Text = $"{sess.AgentName} · {sess.UpdatedAt} · {sess.TurnCount} 轮{liveMark}",
                FontSize = 11.5,
                Foreground = Hex("#8C9BB3"),
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = sess.FilePath
            });

            card.Child = sp;
            var entry = new DialogCardEntry
            {
                Card = card,
                SessionId = sess.SessionId,
                HasLive = matchedLive != null,
                LiveStatus = matchedLive?.Status
            };
            _dialogCardEntries[sess.SessionId] = entry;
            card.MouseLeftButtonUp += (_, _) => SelectAndDisplayDialogSession(sess, refreshListHighlight: true);
            DialogCardsStack.Children.Add(card);
        }

        if (!_showAllDialogs && filtered.Count > renderLimit)
        {
            var showAllBtn = new Button
            {
                Content = $"📂 展开其余 {filtered.Count - renderLimit} 条历史对话框（当前仅展示最近 {renderLimit} 条以保持流畅）",
                Style = (Style)FindResource("ActionBtn"),
                Margin = new Thickness(0, 4, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            showAllBtn.Click += (_, _) =>
            {
                _showAllDialogs = true;
                RenderDialogsPage(skipAsyncTrigger: true);
            };
            DialogCardsStack.Children.Add(showAllBtn);
        }

        var currentSel = filtered.FirstOrDefault(s => string.Equals(s.SessionId, _selectedDialogSessionId, StringComparison.OrdinalIgnoreCase))
                         ?? filtered[0];
        SelectAndDisplayDialogSession(currentSel, refreshListHighlight: false);
    }

    private ExternalDialogSessionInfo? _currentDisplayedSession;
    private string _latestReplyClipboardText = "";

    private void SelectAndDisplayDialogSession(ExternalDialogSessionInfo sess, bool refreshListHighlight)
    {
        var previousId = _selectedDialogSessionId;
        _selectedDialogSessionId = sess.SessionId;
        _selectedDialogFilePath = sess.FilePath;
        _currentDisplayedSession = sess;

        SelectedDialogTitleText.Text = sess.Title;
        SelectedDialogTitleText.ToolTip = $"{sess.AgentName}\n记录: {sess.FilePath}\nSessionId: {sess.SessionId}";
        SelectedDialogPathText.Text = $"记录: {sess.FilePath}";
        SelectedDialogPathText.ToolTip = sess.FilePath;
        if (refreshListHighlight)
            UpdateDialogCardSelection(previousId, sess.SessionId);

        if (AppPaths.IsIsolatedTest)
        {
            var turns = File.Exists(sess.FilePath)
                ? _sessionSvc.ExtractConversationTurns(sess.FilePath, sess.AgentId)
                : new List<(string Role, string Time, string Text)>();
            var rawMarkdown = _sessionSvc.BuildTranscriptMarkdownFromTurns(sess, turns, 20);
            _pendingRawMarkdown = rawMarkdown;
            DialogTranscriptReaderBox.Text = rawMarkdown;
            RenderStructuredDialogBubbles(sess, turns);
            ApplyDialogViewModeVisibility(isStructured: _dialogStructuredMode);
            return;
        }

        var seq = ++_dialogLoadSeq;
        long length = 0;
        long writeTicks = 0;
        try
        {
            if (File.Exists(sess.FilePath))
            {
                var fi = new FileInfo(sess.FilePath);
                length = fi.Length;
                writeTicks = fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch { }

        var cacheKey = DialogTranscriptCacheKey(sess.FilePath, length, writeTicks);
        if (_dialogTranscriptCache.TryGetValue(cacheKey, out var cached))
        {
            TouchDialogTranscriptCache(cacheKey);
            _pendingRawMarkdown = cached.Markdown;
            if (!_dialogStructuredMode)
                DialogTranscriptReaderBox.Text = cached.Markdown;
            RenderStructuredDialogBubbles(sess, cached.Turns);
            ApplyDialogViewModeVisibility(isStructured: _dialogStructuredMode);
            return;
        }

        _pendingRawMarkdown = "";
        if (_dialogStructuredMode)
            ShowDialogLoadingBubbles();
        else if (DialogTranscriptReaderBox != null)
            DialogTranscriptReaderBox.Text = "正在加载对话内容…";
        ApplyDialogViewModeVisibility(isStructured: _dialogStructuredMode);

        var filePath = sess.FilePath;
        var agentId = sess.AgentId;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var turns = File.Exists(filePath)
                ? _sessionSvc.ExtractConversationTurns(filePath, agentId, maxRecentTurns: 24)
                : new List<(string Role, string Time, string Text)>();
            var markdown = _sessionSvc.BuildTranscriptMarkdownFromTurns(sess, turns, 20);
            return (turns, markdown);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (seq != _dialogLoadSeq) return;
                RememberDialogTranscript(cacheKey, t.Result.turns, t.Result.markdown);
                _pendingRawMarkdown = t.Result.markdown;
                if (!_dialogStructuredMode && DialogTranscriptReaderBox != null)
                    DialogTranscriptReaderBox.Text = t.Result.markdown;
                RenderStructuredDialogBubbles(sess, t.Result.turns);
                ApplyDialogViewModeVisibility(isStructured: _dialogStructuredMode);
            });
        });
    }

    private static string DialogTranscriptCacheKey(string filePath, long length, long writeTicks)
        => $"{filePath.ToLowerInvariant()}|{length}|{writeTicks}";

    private void TouchDialogTranscriptCache(string key)
    {
        _dialogTranscriptLru.Remove(key);
        _dialogTranscriptLru.AddFirst(key);
    }

    private void RememberDialogTranscript(string key, List<(string Role, string Time, string Text)> turns, string markdown)
    {
        if (_dialogTranscriptCache.ContainsKey(key))
            _dialogTranscriptLru.Remove(key);
        _dialogTranscriptLru.AddFirst(key);
        _dialogTranscriptCache[key] = (turns, markdown);
        while (_dialogTranscriptCache.Count > 24 && _dialogTranscriptLru.Last != null)
        {
            var old = _dialogTranscriptLru.Last.Value;
            _dialogTranscriptLru.RemoveLast();
            _dialogTranscriptCache.Remove(old);
        }
    }

    private void UpdateDialogCardSelection(string? previousId, string newId)
    {
        if (!string.IsNullOrEmpty(previousId) &&
            !string.Equals(previousId, newId, StringComparison.OrdinalIgnoreCase) &&
            _dialogCardEntries.TryGetValue(previousId, out var prev))
        {
            PaintDialogCard(prev, false);
        }
        if (_dialogCardEntries.TryGetValue(newId, out var cur))
            PaintDialogCard(cur, true);
    }

    private void PaintDialogCard(DialogCardEntry entry, bool selected)
    {
        if (selected)
        {
            entry.Card.Background = Hex("#1E3148");
            entry.Card.BorderBrush = Hex("#38BDF8");
            entry.Card.BorderThickness = new Thickness(1.5);
            return;
        }
        entry.Card.Background = entry.HasLive ? Hex("#1A2636") : Hex("#151C29");
        entry.Card.BorderBrush = entry.LiveStatus == "working"
            ? Hex("#F59E0B")
            : (entry.HasLive ? Hex("#10B981") : Hex("#243044"));
        entry.Card.BorderThickness = new Thickness(entry.HasLive ? 1.5 : 1);
    }

    private void ShowDialogLoadingBubbles()
    {
        if (DialogBubblesPanel == null) return;
        DialogBubblesPanel.Children.Clear();
        DialogBubblesPanel.Children.Add(new TextBlock
        {
            Text = "正在加载对话内容…",
            Foreground = Hex("#94A3B8"),
            FontSize = 13,
            Margin = new Thickness(4, 16, 4, 0)
        });
    }

    private sealed class DialogCardEntry
    {
        public Border Card { get; init; } = null!;
        public string SessionId { get; init; } = "";
        public bool HasLive { get; init; }
        public string? LiveStatus { get; init; }
    }

    private void ApplyDialogViewModeVisibility(bool isStructured)
    {
        if (DialogStructuredScroll == null || DialogTranscriptReaderBox == null || ToggleDialogViewModeBtn == null) return;
        DialogStructuredScroll.Visibility = isStructured ? Visibility.Visible : Visibility.Collapsed;
        DialogTranscriptReaderBox.Visibility = isStructured ? Visibility.Collapsed : Visibility.Visible;
        ToggleDialogViewModeBtn.Content = isStructured ? "📄 原始记录" : "💬 对话视图";
    }

    private void RenderStructuredDialogBubbles(ExternalDialogSessionInfo sess, List<(string Role, string Time, string Text)> turns)
    {
        if (DialogBubblesPanel == null) return;
        DialogBubblesPanel.Children.Clear();
        _latestReplyClipboardText = "";

        // 顶部紧凑元数据卡片
        var metaCard = new Border
        {
            Background = Hex("#111927"),
            BorderBrush = Hex("#233248"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 10)
        };
        metaCard.Child = new TextBlock
        {
            Text = $"{sess.AgentName} · 共 {turns.Count} 轮发言 · 最后活跃：{sess.UpdatedAt}\n工程目录：{sess.ProjectDir}",
            FontSize = 11.5,
            Foreground = Hex("#94A3B8"),
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap
        };
        DialogBubblesPanel.Children.Add(metaCard);

        if (turns.Count == 0)
        {
            DialogBubblesPanel.Children.Add(new TextBlock
            {
                Text = "当前对话框尚无可解析的文本消息（可点击上方「📄 原始记录」查看源文件）。",
                Foreground = Hex("#64748B"),
                FontSize = 12,
                Margin = new Thickness(4, 12, 4, 0)
            });
            return;
        }

        var lastAst = turns.LastOrDefault(t => t.Role == "assistant");
        _latestReplyClipboardText = lastAst.Text ?? "";

        // 仅展示最近 14 轮，保持界面流畅，最新一轮高亮边框
        int startIdx = Math.Max(0, turns.Count - 14);
        int lastUserIdx = turns.FindLastIndex(t => t.Role == "user");
        for (int i = startIdx; i < turns.Count; i++)
        {
            var (role, time, text) = turns[i];
            bool isUser = role == "user";
            bool isLatestRound = lastUserIdx >= 0 && i >= lastUserIdx;

            var cleanBody = isUser && text.Contains("## 【最高优先级】")
                ? AutoDispatchService.ExtractCleanTaskBrief(text, 220)
                : text.Trim();

            var bubble = new Border
            {
                Background = isUser ? Hex("#132033") : Hex("#10221D"),
                BorderBrush = isUser
                    ? Hex(isLatestRound ? "#38BDF8" : "#223956")
                    : Hex(isLatestRound ? "#10B981" : "#1D4236"),
                BorderThickness = new Thickness(isLatestRound ? 1.5 : 1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(11, 8, 11, 8),
                Margin = new Thickness(isUser ? 0 : 18, 0, isUser ? 18 : 0, 8)
            };

            var stack = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            header.Children.Add(new TextBlock
            {
                Text = isUser ? "👤 指令 / 提问" : $"🤖 {sess.AgentName} 回复",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Hex(isUser ? "#7DD3FC" : "#6EE7B7")
            });
            if (!string.IsNullOrWhiteSpace(time))
            {
                header.Children.Add(new TextBlock
                {
                    Text = isLatestRound ? $"[最新] {time}" : time,
                    FontSize = 10.5,
                    Foreground = Hex(isLatestRound ? "#FBBF24" : "#64748B"),
                    HorizontalAlignment = HorizontalAlignment.Right
                });
            }
            stack.Children.Add(header);

            var bodyBox = new TextBox
            {
                Text = cleanBody.Length > 2600 ? cleanBody[..2600] + "\n...(内容较长，完整文本请切换顶部「📄 原始记录」查看)" : cleanBody,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Hex("#E2E8F0"),
                FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI, Consolas")
            };
            stack.Children.Add(bodyBox);
            bubble.Child = stack;
            DialogBubblesPanel.Children.Add(bubble);
        }
    }

    private void ToggleDialogViewModeBtn_Click(object sender, RoutedEventArgs e)
    {
        _dialogStructuredMode = !_dialogStructuredMode;
        if (!_dialogStructuredMode && DialogTranscriptReaderBox != null && !string.IsNullOrEmpty(_pendingRawMarkdown))
            DialogTranscriptReaderBox.Text = _pendingRawMarkdown;
        ApplyDialogViewModeVisibility(_dialogStructuredMode);
    }

    private void CopyLatestReplyBtn_Click(object sender, RoutedEventArgs e)
    {
        var copyText = !string.IsNullOrWhiteSpace(_latestReplyClipboardText)
            ? _latestReplyClipboardText
            : DialogTranscriptReaderBox.Text;
        if (string.IsNullOrWhiteSpace(copyText))
        {
            SetStatus("当前没有可复制的回复内容。");
            return;
        }
        Clipboard.SetText(copyText);
        SetStatus($"已复制最新回复至剪贴板（{copyText.Length} 字符）。");
    }

    private void ClearDialogSearchBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogSearchBox.Clear();
    }

    private void RefreshTargetDialogCombo(ProjectProfile proj, List<ExternalDialogSessionInfo>? preloaded = null)
    {
        var prevSid = (TargetDialogCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        TargetDialogCombo.Items.Clear();

        var selAgentTag = (HeadlessTargetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var resolvedAg = _autoDispatch.ResolveTargetAgent(_reg, selAgentTag);
        var filterAgId = resolvedAg?.Id ?? "auto";

        var sessions = preloaded ?? _cachedProjectSessions;
        if (sessions.Count == 0 && AppPaths.IsIsolatedTest)
        {
            sessions = _sessionSvc.DiscoverProjectSessions(proj, null);
        }
        var bestMatch = _sessionSvc.FindBestMatchingSession(proj, filterAgId, "auto", sessions);
        var autoText = bestMatch != null
            ? $"● 自动续接项目对话框 ({bestMatch.Title})"
            : "● 自动寻址（无已有会话则自动新建）";

        TargetDialogCombo.Items.Add(new ComboBoxItem { Content = autoText, Tag = "auto" });
        TargetDialogCombo.Items.Add(new ComboBoxItem { Content = "＋ 强制新建一个专属项目对话框", Tag = "new" });

        foreach (var s in sessions.Where(x => x.AgentId != "antigravity").Take(30))
        {
            var cbi = new ComboBoxItem
            {
                Content = $"[{s.AgentName}] {s.Title} ({s.UpdatedAt})",
                Tag = s.SessionId,
                ToolTip = $"SessionId: {s.SessionId}\n对话记录文件: {s.FilePath}\n所属工程目录: {s.ProjectDir}\n最新反馈: {s.LastAssistantMessage}"
            };
            TargetDialogCombo.Items.Add(cbi);
            if (s.SessionId == prevSid) TargetDialogCombo.SelectedItem = cbi;
        }

        if (TargetDialogCombo.SelectedIndex < 0)
        {
            TargetDialogCombo.SelectedIndex = prevSid == "new" ? 1 : 0;
        }
    }

    private void RefreshDialogsListBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AppPaths.IsIsolatedTest)
        {
            RenderDialogsPage();
        }
        else
        {
            TriggerAsyncSessionsScanIfNeeded(force: true);
        }
        SetStatus("正在后台同步刷新 F:\\AIChat 下的全部项目对话框记录...");
    }

    private void DialogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressEvents) return;
        RenderDialogsPage(skipAsyncTrigger: true);
    }

    private void DialogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = string.IsNullOrEmpty(DialogSearchBox.Text);
        if (DialogSearchPlaceholder != null)
            DialogSearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (ClearDialogSearchBtn != null)
            ClearDialogSearchBtn.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        if (!IsLoaded || _suppressEvents) return;
        if (_dialogSearchDebounceTimer == null)
        {
            _dialogSearchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _dialogSearchDebounceTimer.Tick += (_, _) =>
            {
                _dialogSearchDebounceTimer.Stop();
                if (IsLoaded && NavDialogs.IsChecked == true)
                {
                    RenderDialogsPage(skipAsyncTrigger: true);
                }
            };
        }
        _dialogSearchDebounceTimer.Stop();
        _dialogSearchDebounceTimer.Start();
    }

    private void OpenDialogFileInExplorerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_selectedDialogFilePath) && File.Exists(_selectedDialogFilePath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_selectedDialogFilePath}\"") { UseShellExecute = true });
        }
        else if (Directory.Exists(@"F:\AIChat"))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", @"F:\AIChat") { UseShellExecute = true });
        }
    }

    private void ShowLatestInboxInReaderBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null) return;
        var inboxDir = Path.Combine(_bus.GetCollabDir(proj), "inbox");
        if (!Directory.Exists(inboxDir))
        {
            SetStatus("当前项目 work/collab/inbox/ 目录尚无反馈文件。");
            return;
        }
        var latest = Directory.GetFiles(inboxDir, "*.md")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (latest == null)
        {
            SetStatus("当前项目 work/collab/inbox/ 目录尚无反馈文件。");
            return;
        }
        _selectedDialogFilePath = latest;
        SelectedDialogTitleText.Text = $"◆ 最新收回的协同反馈：{Path.GetFileName(latest)}";
        SelectedDialogPathText.Text = $"文件路径：{latest}（点击左侧任意对话框可切回聊天记录）";
        DialogTranscriptReaderBox.Text = File.ReadAllText(latest, Encoding.UTF8);
        ApplyDialogViewModeVisibility(isStructured: false);
        SetStatus($"已展示最新收回的反馈文件：{Path.GetFileName(latest)}");
    }

    private void HeadlessTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var proj = _reg.GetActiveProject();
        if (proj != null) RefreshTargetDialogCombo(proj);
    }

    private async void RunHeadlessDispatchBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null) return;

        var target = (HeadlessTargetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var dialogMode = (TargetDialogCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var promptText = QuickDispatchPromptBox.Text.Trim();

        bool trackDispatch = !AppPaths.IsIsolatedTest;
        if (trackDispatch && !LongActionTracker.TryBegin(LongActionTracker.HeadlessDispatch, "执行中"))
        {
            SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.HeadlessDispatch));
            return;
        }
        if (trackDispatch) LongActionTracker.ShowBusy(RunHeadlessDispatchBtn);
        else RunHeadlessDispatchBtn.IsEnabled = false;
        SelectedDialogTitleText.Text = "⚡ 正在调用外部 AI 工具并自动拉起窗口执行任务...";
        SelectedDialogPathText.Text = "实时进度来自 work/collab/LIVE_STATUS.md";
        SetStatus("正在寻找/新建项目对话框并启动目标 AI 窗口执行任务...");
        ShowDispatchProgress("正在启动派单，等待 LIVE_STATUS.md …");

        var liveFile = Path.Combine(_bus.GetCollabDir(proj), "LIVE_STATUS.md");
        var pollGen = ++_dispatchPollGen;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        var liveBusy = false;
        var lastLive = "";
        timer.Tick += (_, _) =>
        {
            if (liveBusy || pollGen != _dispatchPollGen) return;
            liveBusy = true;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    return File.Exists(liveFile) ? File.ReadAllText(liveFile, Encoding.UTF8) : "";
                }
                catch
                {
                    return "";
                }
            }).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    liveBusy = false;
                    if (pollGen != _dispatchPollGen || t.IsFaulted) return;
                    var text = t.Result ?? "";
                    if (text.Length == 0 || string.Equals(text, lastLive, StringComparison.Ordinal)) return;
                    lastLive = text;
                    ShowDispatchProgress(text);
                });
            });
        };
        timer.Start();

        try
        {
            var outcome = await System.Threading.Tasks.Task.Run(() =>
                _autoDispatch.ExecuteHeadlessDispatch(
                    _reg,
                    proj,
                    _bus,
                    target,
                    string.IsNullOrWhiteSpace(promptText) ? null : promptText,
                    180,
                    dialogMode));

            if (!string.IsNullOrWhiteSpace(outcome.SessionId))
            {
                _selectedDialogSessionId = outcome.SessionId;
            }

            if (AppPaths.IsIsolatedTest)
                RefreshAllViews();
            else
                TriggerAsyncSessionsScanIfNeeded(force: true);
            int sec = trackDispatch ? LongActionTracker.ElapsedSeconds(LongActionTracker.HeadlessDispatch) : 0;
            SetStatus(trackDispatch ? $"{outcome.SummaryMessage}（耗时 {sec}s）" : outcome.SummaryMessage);

            if (outcome.Success && !string.IsNullOrWhiteSpace(outcome.OutputText))
            {
                SelectedDialogTitleText.Text = $"✅ 派单完成 · {outcome.AgentName} ({outcome.SessionAction})";
                SelectedDialogPathText.Text = $"结果已存入：{outcome.OutputInboxFile} | 关联 SessionId：{outcome.SessionId}";
                _pendingRawMarkdown = outcome.OutputText;
                if (AppPaths.IsIsolatedTest || !_dialogStructuredMode)
                    DialogTranscriptReaderBox.Text = outcome.OutputText;
                else
                    ShowDispatchProgress(outcome.OutputText);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ 派单执行失败：{TruncateStatus(ex.Message)}");
        }
        finally
        {
            timer.Stop();
            _dispatchPollGen++;
            if (trackDispatch) EndLongAction(LongActionTracker.HeadlessDispatch);
            else if (RunHeadlessDispatchBtn != null) RunHeadlessDispatchBtn.IsEnabled = true;
        }
    }

    private void ShowDispatchProgress(string text)
    {
        if (_dialogStructuredMode && DialogBubblesPanel != null)
        {
            ApplyDialogViewModeVisibility(isStructured: true);
            DialogBubblesPanel.Children.Clear();
            DialogBubblesPanel.Children.Add(new Border
            {
                Background = Hex("#111927"),
                BorderBrush = Hex("#F59E0B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Child = new TextBox
                {
                    Text = text,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Hex("#FDE68A"),
                    FontSize = 12.5,
                    FontFamily = new FontFamily("Microsoft YaHei UI, Consolas")
                }
            });
            return;
        }
        if (DialogTranscriptReaderBox != null)
            DialogTranscriptReaderBox.Text = text;
    }

    // ==================== PAGE 3: 规则与技能状态 ====================

    private void RenderSyncPage()
    {
        var proj = _reg.GetActiveProject();
        if (proj == null)
        {
            SyncHealthList.Children.Clear();
            RuleSkillInventoryPanel.Children.Clear();
            return;
        }

        if (AppPaths.IsIsolatedTest)
        {
            ApplySyncPageVisual(proj, _sync.InspectSyncHealth(_reg, proj), _sync.InspectRulesAndSkillsInventory(_reg, proj));
            return;
        }

        var seq = ++_syncPageSeq;
        if (_cachedSyncHealth != null && _cachedSyncInventory != null)
        {
            ApplySyncPageVisual(proj, _cachedSyncHealth, _cachedSyncInventory);
        }
        else
        {
            SyncHealthList.Children.Clear();
            RuleSkillInventoryPanel.Children.Clear();
            SsotPathsSummary.Text = "正在后台检查规则与技能…";
            RuleSkillListTitle.Text = "◆ 技能与规则明细（正在加载…）";
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var items = _sync.InspectSyncHealth(_reg, proj);
            var allInv = _sync.InspectRulesAndSkillsInventory(_reg, proj);
            return (items, allInv);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (seq != _syncPageSeq || !IsLoaded) return;
                _cachedSyncHealth = t.Result.items;
                _cachedSyncInventory = t.Result.allInv;
                if (NavSync.IsChecked == true)
                    ApplySyncPageVisual(proj, t.Result.items, t.Result.allInv);
            });
        });
    }

    private void ApplySyncPageVisual(ProjectProfile proj, List<SyncService.SyncHealthItem> items, List<SyncService.RuleSkillInventoryEntry> allInv)
    {
        SyncHealthList.Children.Clear();
        RuleSkillInventoryPanel.Children.Clear();

        var compInfo = proj.CompanionRoots.Count > 0
            ? $" | 关联工程：{string.Join("、", proj.CompanionRoots)}"
            : "";
        SsotPathsSummary.Text = $"当前工程：{proj.PrimaryRoot}{compInfo}   |   规则目录：`{proj.RulesRelDir}` · 技能目录：`{proj.SkillsRelDir}` · 全局铁律：`~/.gemini/GEMINI.md`";
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            bool isLast = i == items.Count - 1;
            var bd = new Border
            {
                BorderBrush = Hex("#242F42"),
                BorderThickness = isLast ? new Thickness(0) : new Thickness(0, 0, 0, 1),
                Padding = new Thickness(4, 5, 4, 5)
            };
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8,
                Fill = it.IsHealthy ? Hex("#10B981") : Hex("#F59E0B"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var nameBlock = new TextBlock { Text = it.AgentName, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var detailBlock = new TextBlock { Text = it.Detail, FontSize = 11.5, Foreground = Hex("#9AA8BE"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) };
            var badge = new Border
            {
                Background = it.IsHealthy ? Hex("#064E3B") : Hex("#78350F"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = it.StatusText,
                    FontSize = 11.5,
                    Foreground = it.IsHealthy ? Hex("#6EE7B7") : Hex("#FDE68A")
                }
            };

            Grid.SetColumn(dot, 0);
            Grid.SetColumn(nameBlock, 1);
            Grid.SetColumn(detailBlock, 2);
            Grid.SetColumn(badge, 3);
            rowGrid.Children.Add(dot);
            rowGrid.Children.Add(nameBlock);
            rowGrid.Children.Add(detailBlock);
            rowGrid.Children.Add(badge);
            bd.Child = rowGrid;
            SyncHealthList.Children.Add(bd);
        }

        int skillCount = allInv.Count(x => x.Kind == "skill");
        int ruleCount = allInv.Count(x => x.Kind == "rule" || x.Kind == "global_rule");
        int memCount = allInv.Count(x => x.Kind == "memory");
        RuleSkillListTitle.Text = $"◆ 技能与规则明细（共 {skillCount} 个技能 · {ruleCount} 条规则 · {memCount} 份经验）";

        var filterTag = (RuleSkillFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var filtered = filterTag switch
        {
            "skill" => allInv.Where(x => x.Kind == "skill").ToList(),
            "rule" => allInv.Where(x => x.Kind == "rule").ToList(),
            "global" => allInv.Where(x => x.Kind == "global_rule" || x.Kind == "memory").ToList(),
            _ => allInv
        };

        for (int i = 0; i < filtered.Count; i++)
        {
            var entry = filtered[i];
            bool isLast = i == filtered.Count - 1;
            var itemBorder = new Border
            {
                Background = string.Equals(_selectedRuleSkillFilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase)
                    ? Hex("#1A2639")
                    : Brushes.Transparent,
                BorderBrush = Hex("#242F42"),
                BorderThickness = isLast ? new Thickness(0) : new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 6, 6, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var stack = new StackPanel();
            var topLine = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };

            var (kindBg, kindFg) = entry.Kind switch
            {
                "skill" => ("#0C4A6E", "#7DD3FC"),
                "global_rule" => ("#3B0764", "#E9D5FF"),
                "memory" => ("#78350F", "#FDE68A"),
                _ => ("#1E293B", "#CBD5E1")
            };

            topLine.Children.Add(new Border
            {
                Background = Hex(kindBg),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = entry.KindLabel, FontSize = 11, Foreground = Hex(kindFg) }
            });
            topLine.Children.Add(new TextBlock
            {
                Text = entry.IdOrName,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Hex("#E6EDF7"),
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(topLine);

            stack.Children.Add(new TextBlock
            {
                Text = entry.Summary,
                FontSize = 11.5,
                Foreground = Hex("#9AA8BE"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            });

            var appliedNames = entry.AgentApplicationStates.Where(x => x.Applied).Select(x => x.AgentName).ToList();
            stack.Children.Add(new TextBlock
            {
                Text = appliedNames.Count > 0
                    ? $"已生效：{string.Join(" · ", appliedNames)}"
                    : "暂未挂载到已启用工具",
                FontSize = 11,
                Foreground = appliedNames.Count > 0 ? Hex("#6EE7B7") : Hex("#64748B")
            });

            itemBorder.Child = stack;
            itemBorder.MouseLeftButtonUp += (_, _) => SelectAndPreviewRuleSkill(entry);
            RuleSkillInventoryPanel.Children.Add(itemBorder);
        }

        if (filtered.Count > 0)
        {
            var cur = filtered.FirstOrDefault(x => string.Equals(x.FilePath, _selectedRuleSkillFilePath, StringComparison.OrdinalIgnoreCase))
                      ?? filtered[0];
            SelectAndPreviewRuleSkill(cur, refreshListHighlight: false);
        }
        else
        {
            RuleSkillPreviewTitle.Text = "◆ 暂无匹配的规则或技能条目";
            RuleSkillPreviewBox.Text = "";
        }
    }

    private void SelectAndPreviewRuleSkill(SyncService.RuleSkillInventoryEntry entry, bool refreshListHighlight = true)
    {
        _selectedRuleSkillFilePath = entry.FilePath;
        RuleSkillPreviewTitle.Text = $"◆ {entry.IdOrName}  ·  {entry.KindLabel}";

        var sb = new StringBuilder();
        sb.AppendLine($"# {entry.IdOrName}");
        sb.AppendLine($"- **分类**：{entry.KindLabel}");
        sb.AppendLine($"- **源文件路径**：`{entry.FilePath}`");
        sb.AppendLine();
        sb.AppendLine("## 在各 AI 工具上的生效状态");
        foreach (var st in entry.AgentApplicationStates)
        {
            sb.AppendLine($"- **{(st.Applied ? "✓ 已生效" : "○ 待同步")} · {st.AgentName}**：{st.How}");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        if (AppPaths.IsIsolatedTest || !File.Exists(entry.FilePath))
        {
            if (File.Exists(entry.FilePath))
            {
                try
                {
                    sb.AppendLine(File.ReadAllText(entry.FilePath, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"(读取文件失败: {ex.Message})");
                }
            }
            RuleSkillPreviewBox.Text = sb.ToString();
        }
        else
        {
            var header = sb.ToString();
            var path = entry.FilePath;
            var seq = ++_rulePreviewSeq;
            RuleSkillPreviewBox.Text = header + "正在加载原文…";
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { return File.ReadAllText(path, Encoding.UTF8); }
                catch (Exception ex) { return $"(读取文件失败: {ex.Message})"; }
            }).ContinueWith(t =>
            {
                if (t.IsFaulted) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (seq != _rulePreviewSeq) return;
                    if (!string.Equals(_selectedRuleSkillFilePath, path, StringComparison.OrdinalIgnoreCase)) return;
                    RuleSkillPreviewBox.Text = header + t.Result;
                });
            });
        }

        if (refreshListHighlight)
        {
            RenderSyncPage();
        }
    }

    private void RuleSkillFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressEvents) return;
        RenderSyncPage();
    }

    private void OpenRuleSkillFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_selectedRuleSkillFilePath) && File.Exists(_selectedRuleSkillFilePath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_selectedRuleSkillFilePath}\"") { UseShellExecute = true });
        }
    }

    // ==================== PAGE 4: 任务单与版本保护 ====================

    private void EnterRightPreviewMode(string title, string content)
    {
        _isPreviewingNonTaskContent = true;
        RightEditorTitle.Text = $"◆ {title}";
        RightEditorTitle.Foreground = Hex("#38BDF8");
        BackToTaskCardBtn.Visibility = Visibility.Visible;
        SaveTaskBtn.Visibility = Visibility.Collapsed;
        TaskEditorBox.IsReadOnly = true;
        TaskEditorBox.Text = content;
    }

    private void ReturnToTaskCardEditor()
    {
        _isPreviewingNonTaskContent = false;
        RightEditorTitle.Text = "◆ 当前项目协同任务单 (work/collab/TASK.md)";
        RightEditorTitle.Foreground = Hex("#E6EDF7");
        BackToTaskCardBtn.Visibility = Visibility.Collapsed;
        SaveTaskBtn.Visibility = Visibility.Visible;
        TaskEditorBox.IsReadOnly = false;
        var proj = _reg.GetActiveProject();
        if (proj != null)
        {
            TaskEditorBox.Text = _bus.ReadTaskCard(proj);
        }
    }

    private void RenderOpsPage()
    {
        var proj = _reg.GetActiveProject();
        if (proj == null || string.IsNullOrWhiteSpace(proj.PrimaryRoot) || !Directory.Exists(proj.PrimaryRoot)) return;

        bool needTask = !_isPreviewingNonTaskContent && string.IsNullOrWhiteSpace(TaskEditorBox.Text);
        if (AppPaths.IsIsolatedTest)
        {
            bool isGit = _bus.IsGitRepo(proj);
            VcsModeBadge.Text = isGit ? "● 原生 Git 对比" : "● 内置快照对比";
            EditorConflictText.Text = _bus.CheckEditorConflictWarning(proj);
            if (needTask) TaskEditorBox.Text = _bus.ReadTaskCard(proj);
            return;
        }

        var seq = ++_opsPageSeq;
        VcsModeBadge.Text = "● 正在检测版本库…";
        EditorConflictText.Text = "正在检查编辑器占用…";
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            bool isGit = _bus.IsGitRepo(proj);
            string warn = _bus.CheckEditorConflictWarning(proj);
            string? task = needTask ? _bus.ReadTaskCard(proj) : null;
            return (isGit, warn, task);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (seq != _opsPageSeq || !IsLoaded || NavOps.IsChecked != true) return;
                VcsModeBadge.Text = t.Result.isGit ? "● 原生 Git 对比" : "● 内置快照对比";
                EditorConflictText.Text = t.Result.warn;
                if (t.Result.task != null && !_isPreviewingNonTaskContent && string.IsNullOrWhiteSpace(TaskEditorBox.Text))
                    TaskEditorBox.Text = t.Result.task;
            });
        });
    }

    private void SetOpsActionButtonsEnabled(bool enabled)
    {
        if (CreateSnapBtn != null) CreateSnapBtn.IsEnabled = enabled;
        if (GenDiffBtn != null) GenDiffBtn.IsEnabled = enabled;
        if (RollbackSnapBtn != null) RollbackSnapBtn.IsEnabled = enabled;
    }

    private bool BeginOpsAction(Button? btn, string idle, string verb)
    {
        if (_opsActionBusy || !LongActionTracker.TryBegin(LongActionTracker.OpsSnapshot, verb))
        {
            SetStatus("⏳ 快照操作进行中，请勿重复点击。");
            return false;
        }
        _opsActionBusy = true;
        LongActionTracker.Arm(btn, LongActionTracker.OpsSnapshot, idle);
        LongActionTracker.ShowBusy(btn);
        SetOpsActionButtonsEnabled(false);
        return true;
    }

    private void EndOpsAction()
    {
        _opsActionBusy = false;
        EndLongAction(LongActionTracker.OpsSnapshot);
        SetOpsActionButtonsEnabled(true);
    }

    private async void CreateSnapBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null) return;
        if (AppPaths.IsIsolatedTest)
        {
            SetStatus(_bus.CreateBaselineSnapshot(_reg, proj));
            return;
        }
        if (!BeginOpsAction(CreateSnapBtn, "新建基准快照", "快照中")) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var msg = await System.Threading.Tasks.Task.Run(() => _bus.CreateBaselineSnapshot(_reg, proj));
            SetStatus($"{msg}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ 新建基准快照失败：{TruncateStatus(ex.Message)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        finally
        {
            EndOpsAction();
        }
    }

    private async void GenDiffBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null) return;
        if (AppPaths.IsIsolatedTest)
        {
            var msgSync = _bus.GenerateLatestDiff(_reg, proj);
            var diffSync = _bus.GetLatestDiffPath(proj);
            if (File.Exists(diffSync))
            {
                EnterRightPreviewMode("正在查看：最近代码改动对比 (LATEST.diff)", File.ReadAllText(diffSync, Encoding.UTF8));
                SetStatus(msgSync + "（点击右上角「⬅ 返回任务单」可切回编辑）");
            }
            else SetStatus(msgSync);
            return;
        }
        if (!BeginOpsAction(GenDiffBtn, "查看最近代码改动 (Diff)", "对比中")) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() =>
            {
                var msg = _bus.GenerateLatestDiff(_reg, proj);
                var diffFile = _bus.GetLatestDiffPath(proj);
                string? body = File.Exists(diffFile) ? File.ReadAllText(diffFile, Encoding.UTF8) : null;
                return (msg, body);
            });
            if (result.body != null)
            {
                EnterRightPreviewMode("正在查看：最近代码改动对比 (LATEST.diff)", result.body);
                SetStatus(result.msg + $"（耗时 {(int)sw.Elapsed.TotalSeconds}s，点击右上角「⬅ 返回任务单」可切回编辑）");
            }
            else SetStatus($"{result.msg}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ 生成代码对比失败：{TruncateStatus(ex.Message)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        finally
        {
            EndOpsAction();
        }
    }

    private async void RollbackSnapBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null) return;
        if (AppPaths.IsIsolatedTest)
        {
            SetStatus(_bus.RollbackToBaseline(_reg, proj));
            return;
        }
        if (!BeginOpsAction(RollbackSnapBtn, "一键回滚至基准", "回滚中")) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var msg = await System.Threading.Tasks.Task.Run(() => _bus.RollbackToBaseline(_reg, proj));
            SetStatus($"{msg}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ 回滚失败：{TruncateStatus(ex.Message)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        finally
        {
            EndOpsAction();
        }
    }

    private void BackToTaskCardBtn_Click(object sender, RoutedEventArgs e)
    {
        ReturnToTaskCardEditor();
        SetStatus("已返回当前项目任务单 (work/collab/TASK.md)。");
    }

    private void ReloadTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        ReturnToTaskCardEditor();
        SetStatus("已刷新读取当前 TASK.md。");
    }

    private void SaveTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null || _isPreviewingNonTaskContent) return;
        _bus.SaveTaskCard(proj, TaskEditorBox.Text);
        SetStatus("已保存当前 TASK.md。");
    }

    private void ArchiveTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null) return;
        if (!_isPreviewingNonTaskContent)
        {
            _bus.SaveTaskCard(proj, TaskEditorBox.Text);
        }
        var msg = _bus.ArchiveCurrentTask(proj, NewPitfallBox.Text);
        NewPitfallBox.Clear();
        ReturnToTaskCardEditor();
        SetStatus(msg);
    }

    private async void CopyCapsuleBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null) return;
        bool incRules = PackIncRules.IsChecked == true;
        bool incSchema = PackIncSchema.IsChecked == true;
        bool incDiff = PackIncTaskDiff.IsChecked == true;
        if (AppPaths.IsIsolatedTest)
        {
            var pathSync = _bus.BuildSanitizedRemotePack(_reg, proj, incRules, incSchema, incDiff);
            var txtSync = File.ReadAllText(pathSync, Encoding.UTF8);
            Clipboard.SetText(txtSync);
            SetStatus($"已生成脱敏上下文包并复制到剪贴板（共 {txtSync.Length} 字符）。");
            return;
        }
        if (!LongActionTracker.TryBegin(LongActionTracker.CapsulePack, "打包中"))
        {
            SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.CapsulePack));
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LongActionTracker.ShowBusy(CopyCapsuleBtn);
        try
        {
            var txt = await System.Threading.Tasks.Task.Run(() =>
            {
                var path = _bus.BuildSanitizedRemotePack(_reg, proj, incRules, incSchema, incDiff);
                return File.ReadAllText(path, Encoding.UTF8);
            });
            Clipboard.SetText(txt);
            SetStatus($"✅ 已生成脱敏上下文包并复制到剪贴板（共 {txt.Length} 字符，耗时 {(int)sw.Elapsed.TotalSeconds}s）。");
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ 生成脱敏上下文包失败：{TruncateStatus(ex.Message)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        finally
        {
            EndLongAction(LongActionTracker.CapsulePack);
        }
    }

    private void SaveToInboxBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null || string.IsNullOrWhiteSpace(InboxPasteBox.Text)) return;
        var saved = _bus.SaveRemoteReplyToInbox(proj, "external_ai", InboxPasteBox.Text);
        InboxPasteBox.Clear();
        SetStatus($"已存入 inbox 隔离区：{saved}");
    }

    // ==================== PAGE 5: 工具接入与分工设置 ====================

    private void RosterRouteView_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool byRoute = RadioViewByWorkRoute?.IsChecked == true;
        if (PanelViewByAgent != null) PanelViewByAgent.Visibility = byRoute ? Visibility.Collapsed : Visibility.Visible;
        if (PanelViewByWorkRoute != null) PanelViewByWorkRoute.Visibility = byRoute ? Visibility.Visible : Visibility.Collapsed;
        if (AgentViewToolbar != null) AgentViewToolbar.Visibility = byRoute ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SwitchSettingsSubViewForTest(bool byWorkRoute)
    {
        if (RadioViewByAgent != null) RadioViewByAgent.IsChecked = !byWorkRoute;
        if (RadioViewByWorkRoute != null) RadioViewByWorkRoute.IsChecked = byWorkRoute;
        if (PanelViewByAgent != null) PanelViewByAgent.Visibility = byWorkRoute ? Visibility.Collapsed : Visibility.Visible;
        if (PanelViewByWorkRoute != null) PanelViewByWorkRoute.Visibility = byWorkRoute ? Visibility.Visible : Visibility.Collapsed;
        if (AgentViewToolbar != null) AgentViewToolbar.Visibility = byWorkRoute ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string ModelCatalogSignature(IEnumerable<AgentRosterEntry> agents)
    {
        var sb = new StringBuilder();
        foreach (var a in agents.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(a.Id).Append('|').Append(a.AvailableModels?.Count ?? 0).Append('|');
            if (a.AvailableModels != null)
            {
                foreach (var m in a.AvailableModels) sb.Append(m).Append('\u001f');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private void RenderSettingsPage()
    {
        RefreshTeamProfileCombo();
        RenderScanSection();
        RenderMagpieWorkRoutesSection();
        RenderRoleSection();
        RenderExtSection();
        UpdateSettingsSaveBarVisual();
    }

    private void RefreshTeamProfileCombo()
    {
        if (TeamProfileCombo == null) return;
        _suppressEvents = true;
        try
        {
            TeamProfileCombo.Items.Clear();
            var profiles = _reg.UserConfig.TeamProfiles ?? new List<TeamConfigProfile>();
            if (profiles.Count == 0)
            {
                TeamProfileCombo.Items.Add(new ComboBoxItem { Content = "（暂无已存方案，点右侧「⚙ 配置方案」创建）", Tag = "" });
                TeamProfileCombo.SelectedIndex = 0;
                return;
            }

            ComboBoxItem? selItem = null;
            foreach (var tp in profiles)
            {
                var cbi = new ComboBoxItem
                {
                    Content = tp.Name,
                    Tag = tp.Id,
                    ToolTip = string.IsNullOrWhiteSpace(tp.Description) ? $"更新时间：{tp.UpdatedAt}" : $"{tp.Description}\n更新时间：{tp.UpdatedAt}"
                };
                TeamProfileCombo.Items.Add(cbi);
                if (string.Equals(tp.Id, _reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    selItem = cbi;
                }
            }
            TeamProfileCombo.SelectedItem = selItem ?? TeamProfileCombo.Items[0];
            UpdateApplyTeamProfileButtonState();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void UpdateApplyTeamProfileButtonState()
    {
        if (BtnApplyTeamProfile == null || TeamProfileCombo == null) return;
        var selId = (TeamProfileCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        bool isAlreadyActive = !string.IsNullOrWhiteSpace(selId) &&
                               string.Equals(selId, _reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(selId) || isAlreadyActive)
        {
            BtnApplyTeamProfile.IsEnabled = false;
            BtnApplyTeamProfile.Style = (Style)FindResource("ActionBtn");
            BtnApplyTeamProfile.Content = "✓ 当前使用中";
        }
        else
        {
            BtnApplyTeamProfile.IsEnabled = true;
            BtnApplyTeamProfile.Style = (Style)FindResource("PrimaryBtn");
            BtnApplyTeamProfile.Content = "⚡ 切换启用";
        }
    }

    private bool _cliUpdateCheckRunning;

    private TeamConfigProfile? ResolveDisplayedTeamProfile(out bool isPreviewingUnapplied)
    {
        var profiles = _reg.UserConfig.TeamProfiles ?? new List<TeamConfigProfile>();
        var activeProfile = profiles.FirstOrDefault(p => string.Equals(p.Id, _reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault();

        TeamConfigProfile? selectedProfile = null;
        if (TeamProfileCombo?.SelectedItem is ComboBoxItem cbi && cbi.Tag is string selId && !string.IsNullOrWhiteSpace(selId))
            selectedProfile = profiles.FirstOrDefault(p => string.Equals(p.Id, selId, StringComparison.OrdinalIgnoreCase));

        if (selectedProfile != null && (activeProfile == null || !string.Equals(selectedProfile.Id, activeProfile.Id, StringComparison.OrdinalIgnoreCase)))
        {
            isPreviewingUnapplied = true;
            return selectedProfile;
        }
        isPreviewingUnapplied = false;
        return activeProfile;
    }

    private async void TriggerBackgroundCliUpdateChecksAsync(bool forceRefresh)
    {
        if (_cliUpdateCheckRunning || AppPaths.IsIsolatedTest) return;
        _cliUpdateCheckRunning = true;
        try
        {
            var targets = _reg.UserConfig.Agents
                .Where(a => a.Detected || string.IsNullOrEmpty(a.DetectorId))
                .Where(CliInstallService.ShouldOfferCliControl)
                .ToList();
            if (targets.Count == 0) return;

            bool anyUpdateChanged = false;
            await Task.Run(async () =>
            {
                foreach (var agent in targets)
                {
                    if (string.IsNullOrWhiteSpace(CliInstallService.ProbeInstalledBinary(agent.Id))) continue;
                    var prev = CliInstallService.GetCachedOrInitialStatus(agent.Id).HasUpdate;
                    var status = await CliInstallService.CheckUpdateAsync(agent.Id, forceRefresh);
                    if (status.HasUpdate != prev || forceRefresh)
                        anyUpdateChanged = true;
                }
            });

            if (anyUpdateChanged && IsLoaded)
            {
                Dispatcher.Invoke(() =>
                {
                    RenderScanSection();
                });
            }
        }
        catch { }
        finally
        {
            _cliUpdateCheckRunning = false;
        }
    }

    private void TeamProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        UpdateApplyTeamProfileButtonState();
        RenderScanSection();
        RenderMagpieWorkRoutesSection();
        UpdateSettingsSaveBarVisual();

        if (TeamProfileCombo.SelectedItem is not ComboBoxItem cbi || cbi.Tag is not string id || string.IsNullOrWhiteSpace(id)) return;
        bool isAlreadyActive = string.Equals(id, _reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase);
        if (isAlreadyActive)
        {
            SetStatus($"当前正在使用配置方案「{cbi.Content}」。");
            return;
        }
        SetStatus($"正在预览方案「{cbi.Content}」（尚未启用，点击上方「⚡ 切换启用」正式生效）。");
    }

    private void BtnApplyTeamProfile_Click(object sender, RoutedEventArgs e)
    {
        if (TeamProfileCombo.SelectedItem is not ComboBoxItem cbi || cbi.Tag is not string id || string.IsNullOrWhiteSpace(id)) return;
        var profile = _reg.UserConfig.TeamProfiles?.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile == null) return;

        if (_hasUnsavedSettingsChanges && !AppPaths.IsIsolatedTest)
        {
            var ask = MessageBox.Show(
                this,
                $"您当前有未保存的局部配置调整，确定要切换并启用方案「{profile.Name}」吗？\n\n切换后将立即写入生效。",
                "确认切换团队配置方案",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;
        }

        _reg.ApplyTeamProfile(profile, saveToDisk: true);
        _hasUnsavedSettingsChanges = false;
        RefreshAllViews();
        _floatingWindow?.RefreshOverlayData(force: true);
        ShowSettingsSavedBanner($"✅ 已成功切换并启用配置方案「{profile.Name}」！（所有工程与悬浮窗已同步更新）");
        if (!AppPaths.IsIsolatedTest)
        {
            MessageBox.Show(
                this,
                $"已正式切换并启用配置方案：\n\n「{profile.Name}」\n\n{profile.Description}",
                "切换配置成功",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void BtnManageTeamProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.ShowTeamProfileManagerDialog(this, _reg, _cloudRecSvc))
        {
            _hasUnsavedSettingsChanges = false;
            RefreshTeamProfileCombo();
            UpdateSettingsSaveBarVisual();
            RefreshAllViews();
            _floatingWindow?.RefreshOverlayData(force: true);
            var activeName = _reg.UserConfig.TeamProfiles?.FirstOrDefault(p => p.Id == _reg.UserConfig.ActiveTeamProfileId)?.Name ?? "当前方案";
            ShowSettingsSavedBanner($"✅ 已保存配置方案「{activeName}」（共 {(_reg.UserConfig.TeamProfiles?.Count ?? 1)} 套方案，全部模型与路由已同步）！");
        }
        else
        {
            RefreshTeamProfileCombo();
            UpdateApplyTeamProfileButtonState();
        }
    }

    private void BtnSaveTeamProfileAs_Click(object sender, RoutedEventArgs e)
    {
        var curName = _reg.UserConfig.TeamProfiles?.FirstOrDefault(p => p.Id == _reg.UserConfig.ActiveTeamProfileId)?.Name ?? "";
        var input = Dialogs.PromptTextInputDialog(
            this,
            "另存为团队配置方案",
            "请输入新配置方案名称（将完整保存主脑、故障替补、全工程路由及各 AI 职责权限；同名将覆盖更新）：",
            string.IsNullOrWhiteSpace(curName) ? "我的自定义团队方案" : curName);
        if (string.IsNullOrWhiteSpace(input)) return;

        var saved = _reg.CaptureCurrentAsTeamProfile(input.Trim(), "用户手动保存的多套团队配置快照");
        _reg.SaveUserConfig();
        _hasUnsavedSettingsChanges = false;
        RefreshTeamProfileCombo();
        UpdateSettingsSaveBarVisual();
        RefreshAllViews();
        _floatingWindow?.RefreshOverlayData(force: true);
        SetStatus($"💾 已保存并启用团队配置方案「{saved.Name}」（共 {(_reg.UserConfig.TeamProfiles?.Count ?? 1)} 套方案可随时切换）。");
        ShowSettingsSavedBanner($"💾 已另存为并启用团队配置方案「{saved.Name}」（共 {(_reg.UserConfig.TeamProfiles?.Count ?? 1)} 套方案）");
        if (!AppPaths.IsIsolatedTest)
        {
            MessageBox.Show(
                this,
                $"已将当前配置完整另存为方案：\n\n「{saved.Name}」\n\n今后可随时在顶部下拉框选择并点击「⚡ 切换启用」一键切回。",
                "另存方案成功",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void BtnDeleteTeamProfile_Click(object sender, RoutedEventArgs e)
    {
        if (TeamProfileCombo.SelectedItem is not ComboBoxItem cbi || cbi.Tag is not string id || string.IsNullOrWhiteSpace(id)) return;
        if ((_reg.UserConfig.TeamProfiles?.Count ?? 0) <= 1)
        {
            SetStatus("至少保留 1 套团队配置方案。");
            return;
        }
        var targetName = cbi.Content?.ToString() ?? id;
        if (_reg.DeleteTeamProfile(id))
        {
            _reg.SaveUserConfig();
            RefreshTeamProfileCombo();
            SetStatus($"🗑 已删除团队配置方案「{targetName}」。");
        }
    }

    private void RenderScanSection()
    {
        DetectedAgentsList.Children.Clear();
        UndetectedAgentsWrap.Children.Clear();
        if (RosterFilterBar != null) RosterFilterBar.Children.Clear();

        var proj = _reg.GetActiveProject();
        var displayProfile = ResolveDisplayedTeamProfile(out bool isPreviewing);
        var snapMap = (displayProfile?.AgentSnapshots ?? new List<TeamAgentSnapshot>())
            .Where(s => !string.IsNullOrWhiteSpace(s.AgentId))
            .GroupBy(s => s.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        bool GetSnapEnabled(AgentRosterEntry a) =>
            snapMap.TryGetValue(a.Id, out var s) ? s.Enabled : a.Enabled;
        List<ModelWorkProfile> GetSnapModelProfiles(AgentRosterEntry a)
        {
            var raw = snapMap.TryGetValue(a.Id, out var s) && s.ModelProfiles != null && s.ModelProfiles.Count > 0
                ? s.ModelProfiles
                : (a.ModelProfiles ?? new List<ModelWorkProfile>());
            var list = raw.Select(m => m.Clone()).ToList();
            for (int idx = 0; idx < list.Count; idx++)
                CloudRecommendationService.EnsureModelProfileEnriched(a.Id, list[idx], idx, a.Category);
            return list;
        }
        List<string> GetSnapRoles(AgentRosterEntry a) =>
            snapMap.TryGetValue(a.Id, out var s) && s.PrimaryWorkRoles != null
                ? s.PrimaryWorkRoles
                : (a.PrimaryWorkRoles ?? new List<string>());
        string GetSnapNote(AgentRosterEntry a) =>
            snapMap.TryGetValue(a.Id, out var s) ? (s.CustomWorkNote ?? "") : (a.CustomWorkNote ?? "");
        AgentPermissions GetSnapPerms(AgentRosterEntry a) =>
            snapMap.TryGetValue(a.Id, out var s) && s.Permissions != null
                ? s.Permissions
                : (a.Permissions ?? new AgentPermissions());

        // 本机已检出 / 外部自定义；筛选条可切到「可扫描目录」浏览未安装项
        var detectedBase = _reg.UserConfig.Agents
            .Where(a => a.Detected || string.IsNullOrEmpty(a.DetectorId))
            .ToList();
        int catalogCount = _reg.Detectors.Detectors?.Count ?? 0;
        int enabledCount = detectedBase.Count(GetSnapEnabled);
        int disabledCount = detectedBase.Count(a => !GetSnapEnabled(a));

        int installedCount = detectedBase.Count(a => a.Detected || GetSnapEnabled(a) || string.IsNullOrEmpty(a.DetectorId));
        RenderRosterFilterBar(detectedBase.Count, enabledCount, disabledCount, catalogCount, installedCount);

        List<AgentRosterEntry> visibleList;
        if (_rosterFilterMode == "catalog")
        {
            visibleList = new List<AgentRosterEntry>(detectedBase);
            foreach (var spec in (_reg.Detectors.Detectors ?? new List<DetectorSpec>()))
            {
                if (visibleList.Any(a => string.Equals(a.Id, spec.Id, StringComparison.OrdinalIgnoreCase))) continue;
                visibleList.Add(new AgentRosterEntry
                {
                    Id = spec.Id,
                    DetectorId = spec.Id,
                    Name = spec.Name,
                    Category = spec.Category,
                    Detected = false,
                    Enabled = false,
                    Evidence = "可扫描目录项（本机未安装）",
                    TagScores = new Dictionary<string, int>(spec.DefaultTags ?? new Dictionary<string, int>())
                });
            }
        }
        else
        {
            visibleList = detectedBase
                .Where(a => _rosterFilterMode switch
                {
                    "installed" => a.Detected || GetSnapEnabled(a) || string.IsNullOrEmpty(a.DetectorId),
                    "enabled" => GetSnapEnabled(a),
                    "disabled" => !GetSnapEnabled(a),
                    _ => true
                })
                .ToList();
        }
        var enabledInProfile = detectedBase.Where(GetSnapEnabled).ToList();

        string resolvedMasterId = displayProfile?.MasterPlannerId ?? _reg.UserConfig.GlobalMasterPlannerId;
        if (string.IsNullOrWhiteSpace(resolvedMasterId) || resolvedMasterId.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            resolvedMasterId = !isPreviewing
                ? (RoutingService.ResolveMasterPlanner(_reg, proj)?.Id ?? "")
                : (enabledInProfile
                    .OrderByDescending(a => (a.TagScores.TryGetValue("brain_analysis", out var bs) ? bs : 0) * 100
                        + (GetSnapPerms(a).HasPreviewMcp ? 30 : 0)
                        + (a.Id.Equals("antigravity", StringComparison.OrdinalIgnoreCase) ? 20 : 0))
                    .FirstOrDefault()?.Id ?? "");
        }
        string resolvedFallbackId = displayProfile?.FallbackBuilderId ?? _reg.UserConfig.GlobalFallbackBuilderId ?? "";

        var detectedOrCustom = visibleList
            .OrderByDescending(GetSnapEnabled)
            .ThenByDescending(a => !string.IsNullOrWhiteSpace(resolvedMasterId) && string.Equals(resolvedMasterId, a.Id, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => GetCachedWindowState(a).IsRunning)
            .ToList();
        var missing = _reg.UserConfig.Agents.Except(detectedOrCustom).ToList();

        ScanSummaryTitle.Text = (isPreviewing ? "预览方案启用 " : "当前方案已启用 ") + $"{enabledInProfile.Count} / {detectedBase.Count} 项工具";

        if (CloudRecStatusSummaryText != null)
        {
            var masterName = detectedOrCustom.FirstOrDefault(a => string.Equals(a.Id, resolvedMasterId, StringComparison.OrdinalIgnoreCase))?.Name ?? "Antigravity";
            CloudRecStatusSummaryText.Text = $"👑 当前主脑：{masterName}";
        }

        for (int i = 0; i < detectedOrCustom.Count; i++)
        {
            var agent = detectedOrCustom[i];
            bool isLast = i == detectedOrCustom.Count - 1;
            bool dispEnabled = GetSnapEnabled(agent);
            bool isMaster = !string.IsNullOrWhiteSpace(resolvedMasterId) && string.Equals(resolvedMasterId, agent.Id, StringComparison.OrdinalIgnoreCase);
            bool isFallback = !string.IsNullOrWhiteSpace(resolvedFallbackId) && string.Equals(resolvedFallbackId, agent.Id, StringComparison.OrdinalIgnoreCase);
            var dispPerms = GetSnapPerms(agent);
            var dispRoles = GetSnapRoles(agent);
            var dispNote = GetSnapNote(agent);
            var dispModels = GetSnapModelProfiles(agent).Where(m => m.Enabled).ToList();

            var rowBorder = new Border
            {
                Background = dispEnabled ? Hex("#121824") : Hex("#0E131D"),
                BorderBrush = Hex(dispEnabled ? "#1E293B" : "#18202E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, isLast ? 0 : 8)
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Col 0: 品牌头像
            var rosterAvatar = OfficialBrandIconService.CreateCircularAvatarVisual(
                agent.Id, dispEnabled ? 26 : 22, agent, isMaster && dispEnabled ? "#8B5CF6" : (dispEnabled ? "#3B82F6" : "#475569"), 1.2);
            rosterAvatar.Margin = new Thickness(0, 1, 12, 0);
            rosterAvatar.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(rosterAvatar, 0);

            var infoStack = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleLine.Children.Add(new TextBlock
            {
                Text = agent.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Hex(dispEnabled ? "#F8FAFC" : "#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            var badgeItems = new List<(string Text, string Bg, string Fg)>();
            if (isMaster) badgeItems.Add(("主脑", "#241B3A", "#C4B5FD"));
            else if (isFallback) badgeItems.Add(("替补", "#15253B", "#93C5FD"));
            else badgeItems.Add((dispEnabled ? "在岗" : "未启用", "#1E2736", "#94A3B8"));

            var normalizedRoles = (dispRoles ?? new List<string>())
                .Where(rk => !string.IsNullOrWhiteSpace(rk))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(rk => WorkSpecialtyCatalog.Items.TryGetValue(rk, out var rn) ? rn : rk)
                .ToList();
            if (badgeItems.Count < 3 && normalizedRoles.Count > 0)
            {
                var roleBadge = normalizedRoles[0];
                if (roleBadge.Length > 6) roleBadge = roleBadge[..6];
                badgeItems.Add((roleBadge, "#1E2736", "#CBD5E1"));
            }
            var winStForBadge = GetCachedWindowState(agent);
            if (badgeItems.Count < 3 && AutoDispatchService.ResolveGuiExecutable(agent) != null && winStForBadge.IsRunning)
                badgeItems.Add(("运行中", "#12352C", "#A7F3D0"));

            foreach (var b in badgeItems.Take(3))
            {
                titleLine.Children.Add(new Border
                {
                    Background = Hex(b.Bg),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = b.Text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Hex(b.Fg) }
                });
            }
            infoStack.Children.Add(titleLine);

            string modelShown = "未选模型";
            if (dispModels.Count > 0)
            {
                var rawName = dispModels[0].ModelName ?? "";
                if (rawName == LocalModelCatalogService.ManagedDefaultModelDisplay) modelShown = "默认托管";
                else
                {
                    int parenIdx = rawName.IndexOf('(');
                    var clean = parenIdx > 1 ? rawName[..parenIdx].Trim() : rawName.Trim();
                    modelShown = string.IsNullOrWhiteSpace(clean) ? "自动" : TruncateOneLine(clean, 16);
                }
            }
            string posRaw = !string.IsNullOrWhiteSpace(dispNote) ? dispNote.Trim() : (normalizedRoles.FirstOrDefault() ?? "待分工");
            if (posRaw.Length > 18) posRaw = posRaw[..18];
            infoStack.Children.Add(new TextBlock
            {
                Text = $"{modelShown} · {posRaw}",
                FontSize = 12.5,
                Foreground = Hex("#94A3B8"),
                Margin = new Thickness(0, 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var tipSb = new StringBuilder();
            tipSb.AppendLine(agent.Name);
            if (dispModels.Count == 0) tipSb.AppendLine("模型：尚未配置");
            else
            {
                tipSb.AppendLine("模型：");
                foreach (var modelRow in dispModels)
                {
                    var own = LocalModelCatalogService.ClassifyModelOwnership(agent.Id, modelRow.ModelName, modelRow.OwnershipKind, modelRow.VendorName, agent.Category);
                    var caps = string.Join(" ", WorkSpecialtyCatalog.OrderedItems
                        .Select(w => (w.Label, Score: modelRow.GetCapabilityScore(w.Id)))
                        .Where(x => x.Score > 0)
                        .OrderByDescending(x => x.Score)
                        .Take(2)
                        .Select(c => $"{c.Label}{c.Score}★"));
                    tipSb.AppendLine("  " + modelRow.ModelName + " · " + own.BadgeText + (string.IsNullOrWhiteSpace(caps) ? "" : " · " + caps));
                }
            }
            tipSb.AppendLine("职责：" + (normalizedRoles.Count == 0 ? "未分配" : string.Join("、", normalizedRoles)));
            var permParts = new List<string> { dispPerms.AllowDirectSourceWrite ? "改工程" : "只读" };
            if (dispPerms.AllowMultiDir) permParts.Add("跨目录");
            if (dispPerms.AllowShellBuild) permParts.Add("终端");
            if (dispPerms.HasPreviewMcp) permParts.Add("MCP截图");
            tipSb.Append("权限：" + string.Join(" · ", permParts));
            rowBorder.ToolTip = tipSb.ToString().Trim();
            Grid.SetColumn(infoStack, 1);

            // Col 2: 工作流胶囊开关 + 非配置运维功能
            var rightWrap = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };

            bool canWorkflowToggle = agent.Detected || string.IsNullOrEmpty(agent.DetectorId);
            var workflowToggle = Dialogs.CreateWorkflowPillToggle(agent.Id, dispEnabled, canWorkflowToggle);
            workflowToggle.IsEnabled = canWorkflowToggle;
            workflowToggle.Margin = new Thickness(0, 0, 10, 0);
            if (canWorkflowToggle)
            {
                string toggleAgentId = agent.Id;
                workflowToggle.Click += (_, _) =>
                {
                    bool want = workflowToggle.IsChecked == true;
                    var result = _reg.SetAgentWorkflowEnabled(toggleAgentId, want);
                    if (!result.Ok)
                    {
                        workflowToggle.IsChecked = result.Enabled;
                        SetStatus(result.Message);
                        return;
                    }
                    RefreshAllViews();
                    _floatingWindow?.RefreshOverlayData(force: true);
                    SetStatus(result.Message);
                };
            }
            rightWrap.Children.Add(workflowToggle);
            var cfgBtn = new Button
            {
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 4, 12, 4),
                Style = (Style)FindResource("ActionBtn"),
                ToolTip = "查看模型、星级与权限",
                Content = new TextBlock
                {
                    Text = "配置",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold
                }
            };
            var cfgAgent = agent;
            cfgBtn.Click += (_, _) => OpenRosterAgentConfig(cfgAgent);
            rightWrap.Children.Add(cfgBtn);

            var winSt = GetCachedWindowState(agent);

            bool offerCli = CliInstallService.ShouldOfferCliControl(agent);
            var cliSrc = offerCli ? CliInstallService.FindSource(agent.Id) : null;
            var cliDrawer = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 8, 0, 0)
            };
            if (cliSrc != null)
            {
                var cliSt = CliInstallService.GetCachedOrInitialStatus(agent.Id);
                string cliKey = LongActionTracker.CliUpdate(agent.Id);
                bool cliBusy = LongActionTracker.IsBusy(cliKey);

                if (cliSt.IsInstalled && !cliSt.HasUpdate && !cliBusy)
                {
                    var verTag = !string.IsNullOrWhiteSpace(cliSt.LocalVersion) ? $" (v{cliSt.LocalVersion})" : "";
                    if (rowBorder.ToolTip is string tip0)
                        rowBorder.ToolTip = tip0 + $"\nCLI 已安装{verTag}：{cliSt.InstalledPath}";
                    rightWrap.Children.Add(new Border
                    {
                        Background = Hex("#064E3B"),
                        BorderBrush = Hex("#10B981"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(8, 3, 8, 3),
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = $"已安装 {cliSrc.DisplayName}{verTag}",
                        Child = new TextBlock
                        {
                            Text = "✓ CLI已装",
                            FontSize = 11.5,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Hex("#A7F3D0")
                        }
                    });
                }
                else
                {
                    bool isUpdateMode = cliSt.IsInstalled && cliSt.HasUpdate;
                    if (cliBusy)
                    {
                        var verb = LongActionTracker.PeekVerb(cliKey);
                        if (verb.Contains("安装", StringComparison.Ordinal)) isUpdateMode = false;
                        else if (verb.Contains("更新", StringComparison.Ordinal) || cliSt.IsInstalled) isUpdateMode = true;
                    }
                    string idleLabel = isUpdateMode ? "⬆ 更新 CLI" : "⬇ 安装 CLI";
                    var cliBtn = new Button
                    {
                        Content = idleLabel,
                        Style = (Style)FindResource("ActionBtn"),
                        Padding = new Thickness(9, 3, 9, 3),
                        Margin = new Thickness(0, 0, 6, 0),
                        FontSize = 11.5,
                        Background = isUpdateMode ? Hex("#3B240E") : Hex("#2B1C0E"),
                        BorderBrush = Hex("#F59E0B"),
                        Foreground = Hex("#FDE68A"),
                        ToolTip = isUpdateMode
                            ? $"检测到 {cliSrc.DisplayName} 有新版本可用：v{cliSt.LocalVersion} ➔ v{cliSt.LatestVersion}\n点击立即在线升级至最新版本"
                            : $"本机已安装桌面端但尚未就绪 {cliSrc.DisplayName}，点击自动下载安装\n官方命令：{cliSrc.InstallCommandHint}"
                    };
                    LongActionTracker.Arm(cliBtn, cliKey, idleLabel);
                    if (cliBusy) LongActionTracker.ShowBusy(cliBtn);

                    var cliTopLine = new DockPanel { LastChildFill = true };
                    var cliOfficialLink = new TextBlock
                    {
                        Text = "🔗 官方说明",
                        FontSize = 10.5,
                        Foreground = Hex("#7DD3FC"),
                        Cursor = System.Windows.Input.Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0),
                        ToolTip = cliSrc.OfficialUrl
                    };
                    cliOfficialLink.MouseLeftButtonUp += (_, _) =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cliSrc.OfficialUrl) { UseShellExecute = true }); } catch { }
                    };
                    DockPanel.SetDock(cliOfficialLink, Dock.Right);
                    var cliProgressLbl = new TextBlock
                    {
                        Text = isUpdateMode ? "准备下载最新版本…" : "准备下载安装源…",
                        FontSize = 11,
                        Foreground = Hex("#BAE6FD"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    cliTopLine.Children.Add(cliOfficialLink);
                    cliTopLine.Children.Add(cliProgressLbl);

                    var cliProgressBar = new ProgressBar
                    {
                        Height = 7,
                        Minimum = 0,
                        Maximum = 1,
                        Value = 0,
                        Margin = new Thickness(0, 5, 0, 2),
                        Foreground = Hex("#38BDF8"),
                        Background = Hex("#0F172A")
                    };

                    cliDrawer.Children.Add(new Border
                    {
                        BorderBrush = Hex("#24324B"),
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        Padding = new Thickness(0, 6, 0, 0),
                        Child = new StackPanel { Children = { cliTopLine, cliProgressBar } }
                    });

                    cliBtn.Click += async (_, _) =>
                    {
                        if (!LongActionTracker.TryBegin(cliKey, isUpdateMode ? "更新中" : "安装中"))
                        {
                            SetStatus(LongActionTracker.DuplicateClickHint(cliKey));
                            return;
                        }
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        bool ok = false;
                        string msg = "";
                        string ver = "";
                        try
                        {
                            LongActionTracker.ShowBusy(cliBtn);
                            cliDrawer.Visibility = Visibility.Visible;
                            cliProgressBar.Value = 0.03;
                            cliProgressLbl.Foreground = Hex("#BAE6FD");
                            cliProgressLbl.Text = $"正在在线{(isUpdateMode ? "更新" : "安装")} {cliSrc.DisplayName}…";
                            SetStatus($"正在在线{(isUpdateMode ? "更新" : "安装")} {cliSrc.DisplayName}…");
                            var prog = new Progress<(double Progress, string Status)>(p =>
                            {
                                cliProgressBar.Value = Math.Clamp(p.Progress, 0, 1);
                                cliProgressLbl.Text = p.Status;
                            });
                            (ok, msg) = await CliInstallService.InstallAsync(agent.Id, prog);
                            cliProgressBar.Value = 1.0;
                            cliProgressLbl.Foreground = ok ? Hex("#86EFAC") : Hex("#FBBF24");
                            cliProgressLbl.Text = msg;
                            if (ok)
                            {
                                var st = CliInstallService.InvalidateAndReloadLocalStatus(agent.Id);
                                ver = st.LocalVersion ?? "";
                            }
                        }
                        catch (Exception ex)
                        {
                            ok = false;
                            msg = ex.Message;
                            cliProgressLbl.Foreground = Hex("#F87171");
                            cliProgressLbl.Text = $"{(isUpdateMode ? "更新" : "安装")}异常：{ex.Message}";
                        }
                        finally
                        {
                            EndLongAction(cliKey);
                        }
                        int sec = Math.Max(0, (int)sw.Elapsed.TotalSeconds);
                        SetStatus(BuildCliFinishStatus(agent.Name, isUpdateMode, ok, msg, ver, sec));
                        RefreshAllViews();
                        _floatingWindow?.RefreshOverlayData(force: true);
                    };
                    rightWrap.Children.Add(cliBtn);
                }
            }

            if (AutoDispatchService.ResolveGuiExecutable(agent) != null)
            {
                var winBtn = new Button
                {
                    Content = winSt.IsRunning ? "↗ 窗口" : "启动",
                    Style = (Style)FindResource("ActionBtn"),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(0, 0, 4, 2),
                    FontSize = 11.5
                };
                winBtn.Click += (_, _) =>
                {
                    var curProj = _reg.GetActiveProject();
                    var msg = _autoDispatch.EnsureOrActivateAgentWindow(agent, curProj?.PrimaryRoot, activateIfRunning: true);
                    RefreshAllViews();
                    SetStatus(msg);
                };
                rightWrap.Children.Add(winBtn);
            }

            if (string.IsNullOrEmpty(agent.DetectorId))
            {
                var delBtn = new Button
                {
                    Content = "×",
                    Style = (Style)FindResource("ActionBtn"),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(2, 0, 0, 2),
                    ToolTip = "移除此自定义外部 AI 节点"
                };
                delBtn.Click += (_, _) =>
                {
                    _reg.UserConfig.Agents.Remove(agent);
                    _reg.SaveUserConfig();
                    _reg.SyncActiveTeamProfileWithCurrentState(saveToDisk: true);
                    RefreshAllViews();
                    SetStatus($"已移除外部自定义 AI：{agent.Name}");
                };
                rightWrap.Children.Add(delBtn);
            }
            Grid.SetColumn(rightWrap, 2);

            rowGrid.Children.Add(rosterAvatar);
            rowGrid.Children.Add(infoStack);
            rowGrid.Children.Add(rightWrap);
            if (cliDrawer.Children.Count > 0)
            {
                var outerStack = new StackPanel();
                outerStack.Children.Add(rowGrid);
                outerStack.Children.Add(cliDrawer);
                rowBorder.Child = outerStack;
            }
            else
            {
                rowBorder.Child = rowGrid;
            }
            DetectedAgentsList.Children.Add(rowBorder);
        }

        // 本机未安装工具：仅展示简洁纯文本状态徽章，彻底消除「安装 CLI」诱导按钮
        foreach (var m in missing)
        {
            UndetectedAgentsWrap.Children.Add(new Border
            {
                Background = Hex("#141A25"),
                BorderBrush = Hex("#242F42"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 4),
                ToolTip = $"本机未安装 {m.Name}，系统不会为其推荐或弹窗提示安装",
                Child = new TextBlock { Text = $"{m.Name} (本机未安装)", Foreground = Hex("#6D7C94"), FontSize = 11.5 }
            });
        }
    }

    private static string MapCategoryLabel(string cat) => cat switch
    {
        "local_ide" => "AI IDE / 编辑器",
        "local_agent" => "AI 桌面客户端",
        "local_cli" => "AI 命令行 CLI / Agent",
        "ide_extension" => "本地大模型与扩展",
        "local_llm" => "本机离线大模型",
        "vps_bridge" => "远程服务器节点",
        "remote_capsule" => "远程网页版",
        _ => "扩展工具"
    };

    private void RenderRosterFilterBar(int detectedN, int enabledN, int disabledN, int catalogN, int installedN)
    {
        if (RosterFilterBar == null) return;
        RosterFilterBar.Children.Clear();
        void AddChip(string mode, string label)
        {
            bool on = string.Equals(_rosterFilterMode, mode, StringComparison.OrdinalIgnoreCase);
            var btn = new Button
            {
                Content = label,
                Tag = "RosterFilter:" + mode,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 4),
                FontSize = 11.5,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                Background = Hex(on ? "#1F324D" : "#121824"),
                BorderBrush = Hex(on ? "#3B82F6" : "#24324B"),
                BorderThickness = new Thickness(1),
                Foreground = Hex(on ? "#E0F2FE" : "#94A3B8"),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += (_, _) =>
            {
                _rosterFilterMode = mode;
                RenderScanSection();
            };
            RosterFilterBar.Children.Add(btn);
        }
        AddChip("installed", $"已装可用 ({installedN})");
        AddChip("all", $"全部已检出 ({detectedN})");
        AddChip("enabled", $"工作流已开启 ({enabledN})");
        AddChip("disabled", $"未加入工作流 ({disabledN})");
        AddChip("catalog", $"可扫描目录 ({catalogN})");
    }

    private static string FormatAgentRolesSummary(AgentRosterEntry agent, string sep, int takeMax)
    {
        var list = (agent.PrimaryWorkRoles ?? new List<string>())
            .Where(rk => !string.IsNullOrWhiteSpace(rk))
            .Select(rk => WorkSpecialtyCatalog.Items.TryGetValue(rk, out var rn) ? rn : rk)
            .Distinct()
            .ToList();
        if (list.Count == 0) return "未配置职责";
        if (list.Count <= takeMax) return string.Join(sep, list);
        return $"{string.Join(sep, list.Take(takeMax))} 等{list.Count}项";
    }

    private void RenderMagpieWorkRoutesSection()
    {
        if (MagpieWorkRoutesPanel == null) return;
        MagpieWorkRoutesPanel.Children.Clear();
        RenderRouteOverviewLegend();
        var proj = _reg.GetActiveProject();
        var displayProfile = ResolveDisplayedTeamProfile(out bool isPreviewing);

        // 若正在预览未启用方案，则在内存中构建临时候选镜像与路由项目副本（不改写 _reg）
        List<AgentRosterEntry>? previewPool = null;
        ProjectProfile? previewProj = null;
        Dictionary<string, WorkRouteRule> routeLookup = new(StringComparer.OrdinalIgnoreCase);

        if (isPreviewing && displayProfile != null)
        {
            var snapMap = (displayProfile.AgentSnapshots ?? new List<TeamAgentSnapshot>())
                .Where(s => !string.IsNullOrWhiteSpace(s.AgentId))
                .GroupBy(s => s.AgentId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // 预览池只看方案 Enabled：未扫描 / 远程 / 自定义外部 AI 只要 snap.Enabled 或运行态 Enabled 就必须保留，绝不因 Detected=false 过滤
            previewPool = _reg.UserConfig.Agents
                .Select(a =>
                {
                    var clone = new AgentRosterEntry
                    {
                        Id = a.Id,
                        DetectorId = a.DetectorId,
                        Name = a.Name,
                        Category = a.Category,
                        Detected = a.Detected,
                        Enabled = snapMap.TryGetValue(a.Id, out var s) ? s.Enabled : a.Enabled,
                        PreferredEffortTier = snapMap.TryGetValue(a.Id, out var s2) && !string.IsNullOrWhiteSpace(s2.PreferredEffortTier) ? s2.PreferredEffortTier : a.PreferredEffortTier,
                        HighTierModel = snapMap.TryGetValue(a.Id, out var s3) && !string.IsNullOrWhiteSpace(s3.HighTierModel) ? s3.HighTierModel : a.HighTierModel,
                        MediumTierModel = snapMap.TryGetValue(a.Id, out var s4) && !string.IsNullOrWhiteSpace(s4.MediumTierModel) ? s4.MediumTierModel : a.MediumTierModel,
                        LowTierModel = snapMap.TryGetValue(a.Id, out var s5) && !string.IsNullOrWhiteSpace(s5.LowTierModel) ? s5.LowTierModel : a.LowTierModel,
                        PrimaryWorkRoles = snapMap.TryGetValue(a.Id, out var s6) && s6.PrimaryWorkRoles != null ? new List<string>(s6.PrimaryWorkRoles) : new List<string>(a.PrimaryWorkRoles ?? new()),
                        CustomWorkNote = snapMap.TryGetValue(a.Id, out var s7) ? (s7.CustomWorkNote ?? "") : a.CustomWorkNote,
                        Permissions = (snapMap.TryGetValue(a.Id, out var s8) && s8.Permissions != null ? s8.Permissions : a.Permissions).Clone(),
                        ModelProfiles = (snapMap.TryGetValue(a.Id, out var s9) && s9.ModelProfiles != null ? s9.ModelProfiles : a.ModelProfiles).Select(mp => mp.Clone()).ToList(),
                        TagScores = new Dictionary<string, int>(snapMap.TryGetValue(a.Id, out var s10) && s10.TagScores != null ? s10.TagScores : a.TagScores, StringComparer.OrdinalIgnoreCase),
                        AvailableModels = new List<string>(a.AvailableModels ?? new())
                    };
                    return clone;
                })
                .Where(a => a.Enabled)
                .ToList();

            previewProj = new ProjectProfile { WorkTypeRoutes = new Dictionary<string, WorkRouteRule>(StringComparer.OrdinalIgnoreCase) };
            foreach (var item in WorkSpecialtyCatalog.OrderedItems)
            {
                if (displayProfile.WorkTypeRoutes != null && displayProfile.WorkTypeRoutes.TryGetValue(item.Id, out var pr) && pr != null)
                {
                    var cloned = RegistryService.CloneRouteRule(pr);
                    previewProj.WorkTypeRoutes[item.Id] = cloned;
                    routeLookup[item.Id] = cloned;
                }
                else
                {
                    // 明确写入 auto 规则以阻止回落读取当前激活方案的 GlobalWorkTypeRoutes
                    previewProj.WorkTypeRoutes[item.Id] = new WorkRouteRule
                    {
                        WorkTypeId = item.Id,
                        TargetAgentId = "auto",
                        TargetModelName = "auto",
                        TargetEffortTier = "auto"
                    };
                }
            }
        }
        else if (displayProfile?.WorkTypeRoutes != null)
        {
            foreach (var kv in displayProfile.WorkTypeRoutes)
                routeLookup[kv.Key] = kv.Value;
        }

        foreach (var item in WorkSpecialtyCatalog.OrderedItems)
        {
            var workTypeId = item.Id;
            var workLabel = item.Label;
            var icon = item.Icon;

            WorkRouteRule? existingRule = null;
            if (!isPreviewing)
            {
                if (proj?.WorkTypeRoutes == null || !proj.WorkTypeRoutes.TryGetValue(workTypeId, out existingRule) || existingRule == null)
                    _reg.UserConfig.GlobalWorkTypeRoutes.TryGetValue(workTypeId, out existingRule);
            }
            else
            {
                routeLookup.TryGetValue(workTypeId, out existingRule);
            }

            bool isExplicitAgent = existingRule != null &&
                                   !string.IsNullOrWhiteSpace(existingRule.TargetAgentId) &&
                                   !string.Equals(existingRule.TargetAgentId, "auto", StringComparison.OrdinalIgnoreCase);
            bool isExplicitModel = existingRule != null &&
                                   !string.IsNullOrWhiteSpace(existingRule.TargetModelName) &&
                                   !string.Equals(existingRule.TargetModelName, "auto", StringComparison.OrdinalIgnoreCase);

            var overviewCandidates = isPreviewing && previewProj != null && previewPool != null
                ? RoutingService.ListCapableRouteCandidatesForOverview(_reg, previewProj, workTypeId, candidates: previewPool)
                : RoutingService.ListCapableRouteCandidatesForOverview(_reg, proj, workTypeId);

            var rowBorder = new Border
            {
                Background = Hex("#101827"),
                BorderBrush = Hex("#22324B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Tag = "route-overview-row"
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });

            var leftStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                ToolTip = $"{icon} {workLabel}：{item.Tooltip}"
            };
            leftStack.Children.Add(new TextBlock
            {
                Text = $"{icon} {workLabel}",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Hex("#E6EDF7")
            });
            leftStack.Children.Add(new TextBlock
            {
                Text = item.Tooltip,
                FontSize = 11,
                Foreground = Hex("#64748B"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });

            var modeBadge = new Border
            {
                Background = isExplicitAgent || isExplicitModel ? Hex("#172B47") : Hex("#0D2A23"),
                BorderBrush = isExplicitAgent || isExplicitModel ? Hex("#2563EB") : Hex("#059669"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = isExplicitAgent ? "📌 方案指定执行" : (isExplicitModel ? "📌 指定模型" : "★ 按角色择优"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = isExplicitAgent || isExplicitModel ? Hex("#93C5FD") : Hex("#6EE7B7")
                }
            };

            var targetPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Tag = "route-overview-candidates"
            };
            if (overviewCandidates.Count > 0)
            {
                foreach (var cand in overviewCandidates)
                    targetPanel.Children.Add(BuildRouteOverviewCandidateCapsule(cand));
            }
            else
            {
                targetPanel.Children.Add(new TextBlock
                {
                    Text = "暂无已启用合规 AI 可路由",
                    FontSize = 11.5,
                    Foreground = Hex("#64748B"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            Grid.SetColumn(leftStack, 0);
            Grid.SetColumn(modeBadge, 1);
            Grid.SetColumn(targetPanel, 3);
            grid.Children.Add(leftStack);
            grid.Children.Add(modeBadge);
            grid.Children.Add(targetPanel);
            rowBorder.Child = grid;
            MagpieWorkRoutesPanel.Children.Add(rowBorder);
        }
    }

    private void RenderRouteOverviewLegend()
    {
        if (RouteOverviewLegendPanel == null) return;
        RouteOverviewLegendPanel.Children.Clear();

        void AddDot(string color, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 2), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Fill = Hex(color), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = Hex("#94A3B8"), VerticalAlignment = VerticalAlignment.Center });
            RouteOverviewLegendPanel.Children.Add(sp);
        }

        AddDot("#F59E0B", "5★金");
        AddDot("#10B981", "4★绿");
        AddDot("#38BDF8", "3★蓝");
        AddDot("#94A3B8", "2★灰");
        AddDot("#64748B", "1★暗");

        var tierSp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 10, 2), VerticalAlignment = VerticalAlignment.Center };
        tierSp.Children.Add(BuildEffortTierBars("high", 10));
        tierSp.Children.Add(new TextBlock { Text = "阶梯=高/中/低档", FontSize = 10.5, Foreground = Hex("#94A3B8"), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        RouteOverviewLegendPanel.Children.Add(tierSp);

        var primarySp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 2), VerticalAlignment = VerticalAlignment.Center };
        primarySp.Children.Add(new Border
        {
            Width = 14, Height = 10, CornerRadius = new CornerRadius(3),
            BorderBrush = Hex("#38BDF8"), BorderThickness = new Thickness(1.5),
            Background = Hex("#1A2740"), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center
        });
        primarySp.Children.Add(new TextBlock { Text = "亮框=首选", FontSize = 10.5, Foreground = Hex("#94A3B8"), VerticalAlignment = VerticalAlignment.Center });
        RouteOverviewLegendPanel.Children.Add(primarySp);

        RouteOverviewLegendPanel.Children.Add(new TextBlock
        {
            Text = "悬停看全量详情",
            FontSize = 10.5,
            Foreground = Hex("#64748B"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2)
        });
    }

    private static string StarColorHex(int stars) => stars switch
    {
        5 => "#F59E0B",
        4 => "#10B981",
        3 => "#38BDF8",
        2 => "#94A3B8",
        _ => "#64748B"
    };

    private static string EffortTierColorHex(string? tier) => (tier ?? "medium").Trim().ToLowerInvariant() switch
    {
        "high" => "#A78BFA",
        "low" => "#34D399",
        _ => "#38BDF8"
    };

    private FrameworkElement BuildEffortTierBars(string? tier, double barMaxH = 11)
    {
        int lit = (tier ?? "medium").Trim().ToLowerInvariant() switch
        {
            "high" => 3,
            "low" => 1,
            _ => 2
        };
        // 托管未知档：按中档 2 格中性青灰
        bool unknownManaged = string.Equals(tier, "managed", StringComparison.OrdinalIgnoreCase);
        var color = unknownManaged ? Hex("#64748B") : Hex(EffortTierColorHex(tier));
        var dim = Hex("#1E293B");
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 0)
        };
        double[] heights = { barMaxH * 0.4, barMaxH * 0.7, barMaxH };
        for (int i = 0; i < 3; i++)
        {
            panel.Children.Add(new Border
            {
                Width = 2.4,
                Height = heights[i],
                CornerRadius = new CornerRadius(0.8),
                Background = i < lit ? color : dim,
                Margin = new Thickness(i == 0 ? 0 : 1.2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            });
        }
        return panel;
    }

    private FrameworkElement BuildStarDots(int stars)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var lit = Hex(StarColorHex(stars));
        var dim = Hex("#1E293B");
        for (int i = 1; i <= 5; i++)
        {
            panel.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 4.2,
                Height = 4.2,
                Fill = i <= stars ? lit : dim,
                Margin = new Thickness(i == 1 ? 0 : 1.5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        return panel;
    }

    private Border BuildRouteOverviewCandidateCapsule(RoutingService.WorkRouteOverviewCandidate cand)
    {
        bool primary = cand.IsPrimary;
        int stars = Math.Clamp(cand.BestCapabilityStars, 1, 5);
        string starColor = StarColorHex(stars);
        string effort = cand.EffortTier;
        if (!LocalModelCatalogService.SupportsModelSelection(cand.Agent.Id))
            effort = "medium"; // 托管默认用中档 2 格青灰阶梯视觉时再特判
        bool isManaged = !LocalModelCatalogService.SupportsModelSelection(cand.Agent.Id);
        // 含「仅复杂攻坚/重修」模型时显示菱形角标（详情在 ToolTip）
        bool showComplexMark = cand.CapableModels.Any(m =>
            string.Equals(m.UsagePolicy, "complex_or_rework_only", StringComparison.OrdinalIgnoreCase));

        var capsule = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 4, 7, 4),
            Margin = new Thickness(0, 2, 5, 2),
            BorderThickness = new Thickness(primary ? 1.6 : 1),
            BorderBrush = primary ? Hex("#38BDF8") : Hex("#334155"),
            Background = primary ? Hex("#1A2740") : Hex("#121A28"),
            Opacity = primary ? 1.0 : 0.88,
            Tag = $"route-cand:{cand.Agent.Id}",
            VerticalAlignment = VerticalAlignment.Center
        };
        if (primary)
        {
            capsule.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString("#38BDF8")!,
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.35
            };
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var avatar = OfficialBrandIconService.CreateCircularAvatarVisual(
            cand.Agent.Id, 18, cand.Agent, primary ? "#38BDF8" : "#475569", primary ? 1.15 : 0.95);
        avatar.Margin = new Thickness(0, 0, 5, 0);
        avatar.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(avatar);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var starLine = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        starLine.Children.Add(BuildStarDots(stars));
        if (showComplexMark)
        {
            starLine.Children.Add(new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection { new Point(4, 0), new Point(8, 4), new Point(4, 8), new Point(0, 4) },
                Fill = Hex("#F59E0B"),
                Width = 8,
                Height = 8,
                Stretch = Stretch.Fill,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.9,
                ToolTip = "含「仅复杂攻坚/重修」模型"
            });
        }
        int extra = Math.Max(0, cand.CapableModels.Count - 1);
        if (extra > 0)
        {
            starLine.Children.Add(new Border
            {
                Background = Hex("#0F172A"),
                BorderBrush = Hex(starColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"+{extra}",
                    FontSize = 8.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Hex(starColor)
                }
            });
        }
        mid.Children.Add(starLine);

        var modelLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        modelLine.Children.Add(BuildEffortTierBars(isManaged ? "managed" : effort));
        modelLine.Children.Add(new TextBlock
        {
            Text = RoutingService.CompactModelCoreName(cand.RepresentativeModelName),
            FontSize = 10.5,
            Foreground = Hex("#CBD5E1"),
            MaxWidth = 104,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        mid.Children.Add(modelLine);
        row.Children.Add(mid);

        capsule.Child = row;
        capsule.ToolTip = BuildRouteCandidateToolTip(cand);
        ToolTipService.SetInitialShowDelay(capsule, 280);
        ToolTipService.SetShowDuration(capsule, 20000);
        return capsule;
    }

    private ToolTip BuildRouteCandidateToolTip(RoutingService.WorkRouteOverviewCandidate cand)
    {
        var tipRoot = new Border
        {
            Background = Hex("#0B1220"),
            BorderBrush = Hex("#334155"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            MaxWidth = 360
        };
        var stack = new StackPanel();
        string roleLabel = cand.IsPrimary
            ? (cand.IsManualOverride ? $"#{cand.Rank} 方案指定首选" : $"#{cand.Rank} 首选推荐")
            : $"#{cand.Rank} 协同可用";
        stack.Children.Add(new TextBlock
        {
            Text = $"{cand.Agent.Name}  ·  {roleLabel}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Hex("#F1F5F9"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var own = LocalModelCatalogService.ClassifyModelOwnership(
            cand.Agent.Id, cand.RepresentativeModelName,
            cand.RepresentativeProfile?.OwnershipKind, cand.RepresentativeProfile?.VendorName, cand.Agent.Category);
        string ownLabel = own.IsNativeOwn ? "自有" : "第三方";
        string policyLabel = ModelRuleCatalog.UsagePolicies
            .FirstOrDefault(p => string.Equals(p.Id, cand.RepresentativeProfile?.UsagePolicy ?? "primary_default", StringComparison.OrdinalIgnoreCase)).Label;
        if (string.IsNullOrWhiteSpace(policyLabel)) policyLabel = ModelRuleCatalog.ShortUsagePolicyLabel(cand.RepresentativeProfile?.UsagePolicy);

        stack.Children.Add(new TextBlock
        {
            Text = $"代表模型：{cand.RepresentativeModelName}",
            FontSize = 11.5,
            Foreground = Hex("#E2E8F0"),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"来源：{ownLabel}  ·  档位：{RoutingService.EffortTierVisualLabel(cand.EffortTier)}  ·  规则：{policyLabel}",
            FontSize = 11,
            Foreground = Hex("#94A3B8"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"本类能力：{cand.BestCapabilityStars}★ {ModelRuleCatalog.ShortStrengthWord(cand.BestCapabilityStars)}",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Hex(StarColorHex(cand.BestCapabilityStars)),
            Margin = new Thickness(0, 6, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = cand.RankReason,
            FontSize = 11,
            Foreground = Hex("#64748B"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });

        if (cand.CapableModels.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "本类已配置模型：",
                FontSize = 11,
                Foreground = Hex("#94A3B8"),
                Margin = new Thickness(0, 8, 0, 3)
            });
            foreach (var m in cand.CapableModels)
            {
                var mOwn = LocalModelCatalogService.ClassifyModelOwnership(
                    cand.Agent.Id, m.ModelName, m.OwnershipKind, m.Profile?.VendorName, cand.Agent.Category);
                string mPol = ModelRuleCatalog.UsagePolicies
                    .FirstOrDefault(p => string.Equals(p.Id, m.UsagePolicy, StringComparison.OrdinalIgnoreCase)).Label;
                if (string.IsNullOrWhiteSpace(mPol)) mPol = ModelRuleCatalog.ShortUsagePolicyLabel(m.UsagePolicy);
                stack.Children.Add(new TextBlock
                {
                    Text = $"· {m.ModelName}  ·  {m.Stars}★  ·  {RoutingService.EffortTierVisualLabel(m.RecommendedEffort)}  ·  {mPol}  ·  {(mOwn.IsNativeOwn ? "自有" : "第三方")}",
                    FontSize = 10.5,
                    Foreground = Hex("#CBD5E1"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2)
                });
            }
        }

        tipRoot.Child = stack;
        return new ToolTip
        {
            Content = tipRoot,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
    }

    private void RenderRoleSection()
    {
        _suppressEvents = true;
        var enabled = _reg.UserConfig.Agents.Where(a => a.Enabled).ToList();
        var configurableAgents = _reg.UserConfig.Agents.Where(a => a.Enabled || a.Detected || string.IsNullOrEmpty(a.DetectorId)).ToList();
        var prevAgentId = (RoleAgentCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        RoleAgentCombo.Items.Clear();
        foreach (var a in configurableAgents)
        {
            var statusSuffix = a.Enabled ? "" : " (已接通·待命未启用)";
            var cbi = new ComboBoxItem { Content = a.Name + statusSuffix, Tag = a.Id };
            RoleAgentCombo.Items.Add(cbi);
            if (a.Id == prevAgentId) RoleAgentCombo.SelectedItem = cbi;
        }
        if (RoleAgentCombo.SelectedIndex < 0 && RoleAgentCombo.Items.Count > 0) RoleAgentCombo.SelectedIndex = 0;

        var proj = _reg.GetActiveProject();
        var bestMaster = RoutingService.ResolveMasterPlanner(_reg, null);
        var bestMasterName = bestMaster?.Name ?? "Antigravity";
        MasterPlannerCombo.Items.Clear();
        var autoMasterItem = new ComboBoxItem
        {
            Content = $"★ 自动最强主脑 (当前: {bestMasterName})",
            Tag = "auto"
        };
        MasterPlannerCombo.Items.Add(autoMasterItem);
        foreach (var a in enabled.OrderByDescending(RoutingService.ComputeMasterPlannerPowerScore))
        {
            int pScore = RoutingService.ComputeMasterPlannerPowerScore(a);
            var cbi = new ComboBoxItem { Content = $"▸ {a.Name} (分析力 {pScore}分)", Tag = a.Id };
            MasterPlannerCombo.Items.Add(cbi);
            if (proj != null && string.Equals(proj.MasterPlannerId, a.Id, StringComparison.OrdinalIgnoreCase))
            {
                MasterPlannerCombo.SelectedItem = cbi;
            }
        }
        if (MasterPlannerCombo.SelectedIndex < 0) MasterPlannerCombo.SelectedIndex = 0;

        var prevWfId = (WorkflowSelectCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        WorkflowSelectCombo.Items.Clear();
        foreach (var wf in _reg.Workflows.Workflows)
        {
            var cbi = new ComboBoxItem { Content = wf.Name, Tag = wf.Id };
            WorkflowSelectCombo.Items.Add(cbi);
            if (wf.Id == prevWfId) WorkflowSelectCombo.SelectedItem = cbi;
        }
        if (WorkflowSelectCombo.SelectedIndex < 0 && WorkflowSelectCombo.Items.Count > 1) WorkflowSelectCombo.SelectedIndex = 1;
        else if (WorkflowSelectCombo.SelectedIndex < 0 && WorkflowSelectCombo.Items.Count > 0) WorkflowSelectCombo.SelectedIndex = 0;

        FallbackAgentCombo.Items.Clear();
        FallbackAgentCombo.Items.Add(new ComboBoxItem { Content = "● 自动顺延可用 AI (推荐)", Tag = "" });
        foreach (var a in configurableAgents)
        {
            var suffix = a.Enabled ? "" : " (待命)";
            var cbi = new ComboBoxItem { Content = a.Name + suffix, Tag = a.Id };
            FallbackAgentCombo.Items.Add(cbi);
            if (proj != null && proj.FallbackBuilderId == a.Id) FallbackAgentCombo.SelectedItem = cbi;
        }
        if (FallbackAgentCombo.SelectedIndex < 0) FallbackAgentCombo.SelectedIndex = 0;
        _suppressEvents = false;

        RenderSingleAgentCapEditor();
        RenderWorkflowStageRoutes();
    }

    private void RenderSingleAgentCapEditor()
    {
        AgentCapEditorPanel.Children.Clear();
        var selId = (RoleAgentCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var agent = _reg.UserConfig.Agents.FirstOrDefault(a => a.Id == selId);
        if (agent == null)
        {
            AgentCapEditorPanel.Children.Add(new TextBlock { Text = "请先在上方勾选启用至少一个 AI 工具。", Foreground = Hex("#9AA8BE") });
            return;
        }

        AgentCapEditorPanel.Children.Add(new TextBlock { Text = "安全权限边界：", Foreground = Hex("#38BDF8"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        AgentCapEditorPanel.Children.Add(MakePermCheck("允许直接修改项目源码与场景文件", agent.Permissions.AllowDirectSourceWrite, v => { agent.Permissions.AllowDirectSourceWrite = v; SaveAndRefreshRoutes(); }));
        AgentCapEditorPanel.Children.Add(MakePermCheck("允许跨副工程目录读写", agent.Permissions.AllowMultiDir, v => { agent.Permissions.AllowMultiDir = v; SaveAndRefreshRoutes(); }));
        AgentCapEditorPanel.Children.Add(MakePermCheck("允许后台运行测试与打包脚本", agent.Permissions.AllowShellBuild, v => { agent.Permissions.AllowShellBuild = v; SaveAndRefreshRoutes(); }));
        AgentCapEditorPanel.Children.Add(MakePermCheck("具备游戏画面截图验证能力 (MCP)", agent.Permissions.HasPreviewMcp, v => { agent.Permissions.HasPreviewMcp = v; SaveAndRefreshRoutes(); }));

        var runnerPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        runnerPanel.Children.Add(new TextBlock { Text = "派单时的窗口与后台执行方式：", Foreground = Hex("#38BDF8"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        runnerPanel.Children.Add(MakePermCheck("派单时若客户端未运行，允许后台最小化静默启动（不抢焦点）", agent.LaunchWindowOnDispatch, v => { agent.LaunchWindowOnDispatch = v; _reg.SaveUserConfig(); }));

        var modeCombo = new ComboBox { Margin = new Thickness(0, 3, 0, 5) };
        var modes = new (string Id, string Label)[]
        {
            ("codex_exec", "● 自动调用 Codex 引擎并在项目对话框留存记录"),
            ("cli_custom", "● 自定义命令行自动执行"),
            ("http_openai", "● 本地/远程 HTTP 接口调用"),
            ("bridge_queue", "● 桥接文件夹自动投递与回收"),
            ("none", "○ 仅人工交互（不自动后台执行）")
        };
        foreach (var m in modes)
        {
            var cbi = new ComboBoxItem { Content = m.Label, Tag = m.Id };
            modeCombo.Items.Add(cbi);
            if (string.Equals(agent.RunnerMode, m.Id, StringComparison.OrdinalIgnoreCase)) modeCombo.SelectedItem = cbi;
        }
        if (modeCombo.SelectedIndex < 0) modeCombo.SelectedIndex = modes.Length - 1;

        var cmdBox = new TextBox { Text = agent.RunnerCmd, ToolTip = "命令路径或接口地址（默认填 auto 自动定位）" };
        modeCombo.SelectionChanged += (_, _) =>
        {
            if (modeCombo.SelectedItem is ComboBoxItem sel && sel.Tag is string mId)
            {
                agent.RunnerMode = mId;
                _reg.SaveUserConfig();
            }
        };
        cmdBox.LostFocus += (_, _) =>
        {
            agent.RunnerCmd = cmdBox.Text.Trim();
            _reg.SaveUserConfig();
        };
        runnerPanel.Children.Add(modeCombo);
        runnerPanel.Children.Add(cmdBox);
        AgentCapEditorPanel.Children.Add(runnerPanel);

        // 官方 CLI 一键安装（带实时进度）
        var cliSrc = CliInstallService.FindSource(agent.Id);
        if (cliSrc != null)
        {
            var cliPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 4) };
            cliPanel.Children.Add(new TextBlock
            {
                Text = $"官方 CLI：{cliSrc.DisplayName}",
                Foreground = Hex("#38BDF8"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var probed = CliInstallService.ProbeInstalledBinary(agent.Id);
            var statusTb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(probed)
                    ? CliInstallService.BuildMissingCliGuidance(agent.Name)
                    : $"已检测到：{probed}",
                Foreground = string.IsNullOrWhiteSpace(probed) ? Hex("#FBBF24") : Hex("#86EFAC"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            var linkTb = new TextBlock
            {
                Text = $"官方文档：{cliSrc.OfficialUrl}",
                Foreground = Hex("#93C5FD"),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "点击打开官方安装说明"
            };
            linkTb.MouseLeftButtonUp += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cliSrc.OfficialUrl) { UseShellExecute = true });
                }
                catch { }
            };
            var progress = new ProgressBar
            {
                Height = 10,
                Minimum = 0,
                Maximum = 1,
                Value = 0,
                Margin = new Thickness(0, 0, 0, 4),
                Visibility = Visibility.Collapsed
            };
            var progressLbl = new TextBlock
            {
                Text = "",
                Foreground = Hex("#CBD5E1"),
                FontSize = 10.5,
                Margin = new Thickness(0, 0, 0, 4)
            };
            bool capUpdateMode = !string.IsNullOrWhiteSpace(probed);
            string capIdle = capUpdateMode ? "更新 CLI 并重检" : "安装 / 更新 CLI";
            string capKey = LongActionTracker.CliUpdate(agent.Id);
            var installBtn = new Button
            {
                Content = capIdle,
                Height = 28,
                Margin = new Thickness(0, 0, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 2, 12, 2)
            };
            LongActionTracker.ApplyBusyIfNeeded(installBtn, capKey, capIdle);
            installBtn.Click += async (_, _) =>
            {
                if (!LongActionTracker.TryBegin(capKey, capUpdateMode ? "更新中" : "安装中"))
                {
                    SetStatus(LongActionTracker.DuplicateClickHint(capKey));
                    return;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = false;
                string msg = "";
                string ver = "";
                try
                {
                    LongActionTracker.ShowBusy(installBtn);
                    progress.Visibility = Visibility.Visible;
                    progress.Value = 0;
                    progressLbl.Text = "准备安装…";
                    var prog = new Progress<(double Progress, string Status)>(p =>
                    {
                        progress.Value = p.Progress;
                        progressLbl.Text = p.Status;
                    });
                    (ok, msg) = await CliInstallService.InstallAsync(agent.Id, prog);
                    if (ok)
                    {
                        var st = CliInstallService.InvalidateAndReloadLocalStatus(agent.Id);
                        ver = st.LocalVersion ?? "";
                        var again = st.InstalledPath;
                        statusTb.Text = string.IsNullOrWhiteSpace(again) ? msg : $"安装成功并已重检：{again}";
                        statusTb.Foreground = Hex("#86EFAC");
                    }
                    else
                    {
                        statusTb.Text = msg;
                        statusTb.Foreground = Hex("#FBBF24");
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    msg = ex.Message;
                    statusTb.Text = "安装失败：" + ex.Message;
                    statusTb.Foreground = Hex("#FCA5A5");
                }
                finally
                {
                    EndLongAction(capKey);
                }
                int sec = Math.Max(0, (int)sw.Elapsed.TotalSeconds);
                SetStatus(BuildCliFinishStatus(agent.Name, capUpdateMode, ok, msg, ver, sec));
                RefreshAllViews();
            };
            cliPanel.Children.Add(statusTb);
            cliPanel.Children.Add(linkTb);
            cliPanel.Children.Add(progress);
            cliPanel.Children.Add(progressLbl);
            cliPanel.Children.Add(installBtn);
            cliPanel.Children.Add(new TextBlock
            {
                Text = $"安装命令参考：{cliSrc.InstallCommandHint}",
                Foreground = Hex("#64748B"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            });
            AgentCapEditorPanel.Children.Add(cliPanel);
        }

        AgentCapEditorPanel.Children.Add(new Border { Height = 1, Background = Hex("#242F42"), Margin = new Thickness(0, 8, 0, 6) });
        AgentCapEditorPanel.Children.Add(new TextBlock { Text = "擅长领域星级（用于自动匹配推荐 AI）：", Foreground = Hex("#38BDF8"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });

        foreach (var tag in _reg.Tags.Tags)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(98) });

            var lbl = new TextBlock { Text = tag.Name, ToolTip = tag.Description, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            var starCombo = new ComboBox { Height = 25 };
            int curStar = agent.TagScores.TryGetValue(tag.Id, out var sc) ? sc : 0;
            for (int s = 0; s <= 5; s++)
            {
                var item = new ComboBoxItem { Content = s == 0 ? "0 (不参与)" : $"{s} ★", Tag = s };
                starCombo.Items.Add(item);
                if (s == curStar) starCombo.SelectedItem = item;
            }
            starCombo.SelectionChanged += (_, _) =>
            {
                if (starCombo.SelectedItem is ComboBoxItem ci && ci.Tag is int val)
                {
                    agent.TagScores[tag.Id] = val;
                    SaveAndRefreshRoutes();
                }
            };

            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(starCombo, 1);
            row.Children.Add(lbl);
            row.Children.Add(starCombo);
            AgentCapEditorPanel.Children.Add(row);
        }
    }

    private CheckBox MakePermCheck(string text, bool initial, Action<bool> onChanged)
    {
        var cb = new CheckBox { Content = text, IsChecked = initial, Margin = new Thickness(0, 3, 0, 3) };
        cb.Click += (_, _) => onChanged(cb.IsChecked == true);
        return cb;
    }

    private void SaveAndRefreshRoutes()
    {
        _reg.SyncActiveTeamProfileWithCurrentState(saveToDisk: true);
        RenderWorkflowStageRoutes();
    }

    private void RenderWorkflowStageRoutes()
    {
        StageRoutingList.Children.Clear();
        var proj = _reg.GetActiveProject();
        var wfId = (WorkflowSelectCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var wf = _reg.Workflows.Workflows.FirstOrDefault(w => w.Id == wfId);
        if (proj == null || wf == null) return;

        WorkflowDescText.Text = wf.Description;
        var results = _routing.ComputeDispatch(_reg, proj, wf);
        var enabled = _reg.UserConfig.Agents.Where(a => a.Enabled).ToList();

        for (int i = 0; i < wf.Stages.Count; i++)
        {
            var st = wf.Stages[i];
            var res = results[i];
            bool isLast = i == wf.Stages.Count - 1;

            var card = new Border
            {
                BorderBrush = Hex("#242F42"),
                BorderThickness = isLast ? new Thickness(0) : new Thickness(0, 0, 0, 1),
                Padding = new Thickness(4, 7, 4, 8)
            };

            var sp = new StackPanel();
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            titleStack.Children.Add(new TextBlock { Text = st.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            titleStack.Children.Add(new TextBlock { Text = $"交付方式：{st.Deliverable}", Foreground = Hex("#8C9BB3"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });

            var overrideKey = $"{wf.Id}::{st.Id}";
            proj.StageOverrides.TryGetValue(overrideKey, out var curOverride);
            if (string.IsNullOrWhiteSpace(curOverride)) curOverride = "auto";

            var routeCombo = new ComboBox { VerticalAlignment = VerticalAlignment.Top };
            var autoItem = new ComboBoxItem { Content = $"● 自动推荐 ({res.AssignedAgentName})", Tag = "auto" };
            routeCombo.Items.Add(autoItem);
            if (curOverride == "auto") routeCombo.SelectedItem = autoItem;

            foreach (var ag in enabled)
            {
                var cbi = new ComboBoxItem { Content = $"▸ 固定指派：{ag.Name}", Tag = ag.Id };
                routeCombo.Items.Add(cbi);
                if (curOverride == ag.Id) routeCombo.SelectedItem = cbi;
            }
            if (routeCombo.SelectedIndex < 0) routeCombo.SelectedIndex = 0;

            routeCombo.SelectionChanged += (_, _) =>
            {
                if (routeCombo.SelectedItem is ComboBoxItem sel && sel.Tag is string val)
                {
                    proj.StageOverrides[overrideKey] = val;
                    _reg.SaveUserConfig();
                    RenderWorkflowStageRoutes();
                    SetStatus($"已更新阶段「{st.Name}」负责 AI。");
                }
            };

            Grid.SetColumn(titleStack, 0);
            Grid.SetColumn(routeCombo, 1);
            topGrid.Children.Add(titleStack);
            topGrid.Children.Add(routeCombo);
            sp.Children.Add(topGrid);

            sp.Children.Add(new TextBlock
            {
                Text = $"当前负责：{res.AssignedAgentName} | 安全说明：{res.GuardrailNote}",
                Foreground = res.GuardrailNote.Contains("➔") ? Hex("#FBBF24") : Hex("#6EE7B7"),
                FontSize = 11.5,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            card.Child = sp;
            StageRoutingList.Children.Add(card);
        }
    }

    private void RenderExtSection()
    {
        ExtTagsWrap.Children.Clear();
        foreach (var t in _reg.Tags.Tags)
        {
            var pill = new Border
            {
                Background = Hex("#162032"),
                BorderBrush = Hex("#2B3B55"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 6, 6)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = $"{t.Name} ({t.Id})", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });

            if (_reg.Tags.Tags.Count > 3)
            {
                var del = new Button
                {
                    Content = "×",
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = Hex("#F87171"),
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(5, 0, 2, 0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                del.Click += (_, _) =>
                {
                    _reg.Tags.Tags.Remove(t);
                    _reg.SaveRegistries();
                    RefreshAllViews();
                    SetStatus($"已移除擅长标签：{t.Name}");
                };
                sp.Children.Add(del);
            }
            pill.Child = sp;
            ExtTagsWrap.Children.Add(pill);
        }

        ExtDetectorsSummary.Text = $"当前内置 {_reg.Detectors.Detectors.Count} 条自动探测规则（覆盖 Antigravity、Cursor、Grok Bot、Codex、Claude、Windsurf、Ollama、LM Studio 等）。";

        ExtHooksList.Children.Clear();
        var proj = _reg.GetActiveProject();
        if (proj != null && Directory.Exists(AppPaths.PluginsDir))
        {
            foreach (var sc in Directory.GetFiles(AppPaths.PluginsDir, "*.ps1"))
            {
                var fn = Path.GetFileName(sc);
                var cb = new CheckBox
                {
                    Content = $"{fn}（导出上下文时自动生成摘要）",
                    IsChecked = proj.EnabledHooks.Contains(fn, StringComparer.OrdinalIgnoreCase),
                    Margin = new Thickness(0, 3, 0, 3)
                };
                cb.Click += (_, _) =>
                {
                    if (cb.IsChecked == true && !proj.EnabledHooks.Contains(fn, StringComparer.OrdinalIgnoreCase))
                        proj.EnabledHooks.Add(fn);
                    else if (cb.IsChecked != true)
                        proj.EnabledHooks.RemoveAll(x => string.Equals(x, fn, StringComparison.OrdinalIgnoreCase));
                    _reg.SaveUserConfig();
                    SetStatus($"已更新插件钩子：{fn}");
                };
                ExtHooksList.Children.Add(cb);
            }
        }
    }

    // ==================== 顶部与通用按钮事件 ====================

    private void ProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (ProjectCombo.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            _reg.UserConfig.ActiveProjectId = id;
            _reg.SaveUserConfig();
            _selectedDialogSessionId = null;
            ReturnToTaskCardEditor();
            RefreshAllViews();
            SetStatus($"已切换至工程：{item.Content}");
        }
    }

    private void EditProjBtn_Click(object sender, RoutedEventArgs e)
    {
        var cur = _reg.GetActiveProject();
        var edited = Dialogs.ShowProjectEditor(this, _reg, cur);
        if (edited != null)
        {
            var idx = _reg.UserConfig.Projects.FindIndex(p => p.Id == edited.Id);
            if (idx >= 0) _reg.UserConfig.Projects[idx] = edited;
            else _reg.UserConfig.Projects.Add(edited);
            _reg.UserConfig.ActiveProjectId = edited.Id;
            _reg.SaveUserConfig();
            ReturnToTaskCardEditor();
            RefreshProjectCombo();
            RefreshAllViews();
            SetStatus($"已保存工程路径设置：{edited.Name}");
        }
    }

    private void NewProjBtn_Click(object sender, RoutedEventArgs e)
    {
        var created = Dialogs.ShowProjectEditor(this, _reg, null);
        if (created != null)
        {
            _reg.UserConfig.Projects.Add(created);
            _reg.UserConfig.ActiveProjectId = created.Id;
            _reg.SaveUserConfig();
            _selectedDialogSessionId = null;
            ReturnToTaskCardEditor();
            RefreshProjectCombo();
            RefreshAllViews();
            SetStatus($"已新增并切换至工程：{created.Name}");
        }
    }

    /// <summary>
    /// 非隔离测试启动时：延迟约 600ms 后台全量 Probe → UI 线程写回花名册并刷新阵容/流程图/悬浮窗。
    /// </summary>
    private void TriggerStartupSilentFullDiscovery()
    {
        if (AppPaths.IsIsolatedTest || _isRescanningAgents || LongActionTracker.IsBusy(LongActionTracker.ScanLocal)) return;
        var specsSnapshot = (_reg.Detectors.Detectors ?? new List<DetectorSpec>()).ToList();
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            bool mine = false;
            try
            {
                await System.Threading.Tasks.Task.Delay(600).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsLoaded || _isRescanningAgents) return;
                    if (!LongActionTracker.TryBegin(LongActionTracker.ScanLocal, "扫描中")) return;
                    mine = true;
                    _isRescanningAgents = true;
                    LongActionTracker.ShowBusy(RescanBtn);
                });
                if (!mine) return;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                Dictionary<string, (bool Found, string Evidence)>? preProbed = null;
                Exception? error = null;
                try
                {
                    preProbed = _discovery.ProbeAllSpecsSnapshot(specsSnapshot);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (error != null)
                        {
                            SetStatus($"⚠ 启动扫描失败：{TruncateStatus(error.Message)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
                            return;
                        }
                        if (!IsLoaded) return;
                        var stats = _discovery.RunDiscoveryWithStats(_reg, preProbed);
                        RefreshAllViews();
                        RefreshWorkflowDiagram();
                        _floatingWindow?.RefreshOverlayData(force: true);
                        if (!string.IsNullOrWhiteSpace(stats.StatusText))
                            SetStatus($"✅ 启动静默扫描完成：{stats.StatusText}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"⚠ 启动扫描失败：{TruncateStatus(ex.Message)}");
                    }
                    finally
                    {
                        _isRescanningAgents = false;
                        EndLongAction(LongActionTracker.ScanLocal);
                        mine = false;
                    }
                });
            }
            catch
            {
                if (mine)
                {
                    _isRescanningAgents = false;
                    LongActionTracker.End(LongActionTracker.ScanLocal);
                }
            }
        });
    }

    private void RescanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AppPaths.IsIsolatedTest)
        {
            var stats = _discovery.RunDiscoveryWithStats(_reg);
            RefreshAllViews();
            SetStatus(stats.StatusText);
            return;
        }

        if (_isRescanningAgents || !LongActionTracker.TryBegin(LongActionTracker.ScanLocal, "扫描中"))
        {
            SetStatus(LongActionTracker.IsBusy(LongActionTracker.ScanLocal)
                ? LongActionTracker.DuplicateClickHint(LongActionTracker.ScanLocal)
                : "⏳ 正在扫描本机，请勿重复点击。");
            return;
        }

        _isRescanningAgents = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LongActionTracker.ShowBusy(RescanBtn);
        SetStatus("正在后台彻底扫描本机 AI 客户端、扩展与模型目录...");
        var specsSnapshot = _reg.Detectors.Detectors.ToList();
        var agentIdsSnapshot = _reg.UserConfig.Agents.Select(a => a.Id).ToList();

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            Dictionary<string, (bool Found, string Evidence)>? preProbed = null;
            Exception? error = null;
            try
            {
                preProbed = _discovery.ProbeAllSpecsSnapshot(specsSnapshot);
                foreach (var id in agentIdsSnapshot)
                {
                    try { LocalModelCatalogService.ProbeLocalModelsForAgent(id, forceRefresh: true); } catch { }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (error != null)
                    {
                        SetStatus($"⚠ 本机扫描失败：{TruncateStatus(error.Message)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
                        return;
                    }
                    if (!IsLoaded) return;
                    var stats = _discovery.RunDiscoveryWithStats(_reg, preProbed);
                    TriggerBackgroundProbesAsync(refreshOverviewWhenChanged: true);
                    TriggerBackgroundCliUpdateChecksAsync(forceRefresh: true);
                    RefreshAllViews();
                    int sec = Math.Max(0, (int)sw.Elapsed.TotalSeconds);
                    var summary = string.IsNullOrWhiteSpace(stats.StatusText) ? "扫描完成" : stats.StatusText;
                    SetStatus($"✅ {summary}（耗时 {sec}s）");
                }
                catch (Exception ex)
                {
                    SetStatus($"⚠ 本机扫描失败：{TruncateStatus(ex.Message)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
                }
                finally
                {
                    _isRescanningAgents = false;
                    EndLongAction(LongActionTracker.ScanLocal);
                }
            });
        });
    }

    private void WorkflowSwitchManagerBtn_Click(object sender, RoutedEventArgs e)
    {
        bool changed = Dialogs.ShowWorkflowToolsSwitchDialog(
            this, _reg, _discovery,
            onRescanRequested: () =>
            {
                // 与「扫描本机」同级的彻底扫描：探测规则快照 + 本地模型刷新 + 写回花名册，供弹窗列表即时重建
                var specsSnapshot = (_reg.Detectors.Detectors ?? new List<DetectorSpec>()).ToList();
                var preProbed = _discovery.ProbeAllSpecsSnapshot(specsSnapshot);
                foreach (var id in _reg.UserConfig.Agents.Select(a => a.Id).ToList())
                {
                    try { LocalModelCatalogService.ProbeLocalModelsForAgent(id, forceRefresh: true); } catch { }
                }
                var stats = _discovery.RunDiscoveryWithStats(_reg, preProbed);
                return stats.StatusText;
            });

        RefreshAllViews();
        RefreshWorkflowDiagram();
        _floatingWindow?.RefreshOverlayData(force: true);
        if (changed)
        {
            int en = _reg.UserConfig.Agents.Count(a => a.Enabled);
            int total = _reg.UserConfig.Agents.Count(a => a.Detected || string.IsNullOrEmpty(a.DetectorId));
            SetStatus($"工作流开关已保存并应用：当前方案已启用 {en} / {total} 项工具");
        }
    }

    private void AddCustomAiBtn_Click(object sender, RoutedEventArgs e)
    {
        var added = Dialogs.ShowAddCustomAgentDialog(this, _reg);
        if (added != null)
        {
            _reg.UserConfig.Agents.Add(added);
            _reg.SyncActiveTeamProfileWithCurrentState(saveToDisk: true);
            RefreshAllViews();
            SetStatus($"已添加并纳入当前配置方案：{added.Name}");
        }
    }

    private void RoleAgentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        RenderSingleAgentCapEditor();
    }

    private void MasterPlannerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var proj = _reg.GetActiveProject();
        if (proj != null && MasterPlannerCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string id)
        {
            _reg.UserConfig.GlobalMasterPlannerId = id;
            foreach (var p in _reg.UserConfig.Projects) p.MasterPlannerId = id;
            var master = RoutingService.ResolveMasterPlanner(_reg, proj);
            MarkSettingsDirty($"已调整全局主脑 AI 为「{master?.Name ?? "自动"}」（暂存待保存，请点击「💾 保存配置」生效）");
        }
    }

    private void WorkflowSelectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        RenderWorkflowStageRoutes();
    }

    private void FallbackAgentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var proj = _reg.GetActiveProject();
        if (proj != null && FallbackAgentCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string id)
        {
            _reg.UserConfig.GlobalFallbackBuilderId = id;
            foreach (var p in _reg.UserConfig.Projects) p.FallbackBuilderId = id;
            MarkSettingsDirty("已调整全局故障替补 AI（暂存待保存，请点击「💾 保存配置」生效）");
        }
    }

    private void MarkSettingsDirty(string statusMsg)
    {
        _hasUnsavedSettingsChanges = true;
        UpdateSettingsSaveBarVisual();
        SetStatus(statusMsg);
    }

    private void UpdateSettingsSaveBarVisual()
    {
        var profile = ResolveDisplayedTeamProfile(out bool isPreviewing);

        if (ActiveProfileSummaryBar != null)
        {
            ActiveProfileSummaryBar.Children.Clear();
            if (profile != null)
            {
                var snapMap = (profile.AgentSnapshots ?? new List<TeamAgentSnapshot>())
                    .Where(s => !string.IsNullOrWhiteSpace(s.AgentId))
                    .GroupBy(s => s.AgentId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                bool IsSnapEnabled(AgentRosterEntry a) =>
                    (snapMap.TryGetValue(a.Id, out var s) ? s.Enabled : a.Enabled)
                    && (a.Detected || string.IsNullOrEmpty(a.DetectorId));

                var visibleEnabled = _reg.UserConfig.Agents.Where(IsSnapEnabled).ToList();
                var visibleCount = _reg.UserConfig.Agents.Count(a => a.Detected || string.IsNullOrEmpty(a.DetectorId));

                string ResolveAgentLabel(string agentId, string autoFallback)
                {
                    if (string.IsNullOrWhiteSpace(agentId) || string.Equals(agentId, "auto", StringComparison.OrdinalIgnoreCase))
                        return autoFallback;
                    var ag = _reg.UserConfig.Agents.FirstOrDefault(a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));
                    return ag?.Name ?? agentId;
                }

                var autoBestMaster = visibleEnabled
                    .OrderByDescending(a => (a.TagScores.TryGetValue("brain_analysis", out var bs) ? bs : 0) * 100 + (a.Id.Equals("antigravity", StringComparison.OrdinalIgnoreCase) ? 20 : 0))
                    .FirstOrDefault();
                string masterLabel = string.IsNullOrWhiteSpace(profile.MasterPlannerId) || string.Equals(profile.MasterPlannerId, "auto", StringComparison.OrdinalIgnoreCase)
                    ? $"自动最强 ({autoBestMaster?.Name ?? "Antigravity"})"
                    : ResolveAgentLabel(profile.MasterPlannerId, "自动最强");

                string fallbackLabel = string.IsNullOrWhiteSpace(profile.FallbackBuilderId)
                    ? "自动降级替补"
                    : ResolveAgentLabel(profile.FallbackBuilderId, "自动降级替补");

                void AddSummaryItem(string label, string val, string valHex, bool isFirst = false)
                {
                    if (!isFirst)
                    {
                        ActiveProfileSummaryBar.Children.Add(new TextBlock
                        {
                            Text = "·",
                            FontSize = 11.5,
                            Foreground = Hex("#334155"),
                            Margin = new Thickness(8, 0, 8, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        });
                    }
                    var pair = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    pair.Children.Add(new TextBlock { Text = label + " ", FontSize = 11.2, Foreground = Hex("#64748B"), VerticalAlignment = VerticalAlignment.Center });
                    pair.Children.Add(new TextBlock { Text = val, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Hex(valHex), VerticalAlignment = VerticalAlignment.Center });
                    ActiveProfileSummaryBar.Children.Add(pair);
                }

                AddSummaryItem("主脑", masterLabel, "#E2E8F0", isFirst: true);
                AddSummaryItem("替补", fallbackLabel, "#CBD5E1");
                AddSummaryItem("精修", $"≤{Math.Max(0, profile.MaxReworkRounds)} 轮", "#CBD5E1");
                AddSummaryItem("已启用", $"{visibleEnabled.Count}/{visibleCount} 项", "#6EE7B7");
            }
        }

        if (SettingsSaveBar == null || SettingsSaveHintText == null)
            return;

        if (isPreviewing && profile != null)
        {
            SettingsSaveBar.Background = System.Windows.Media.Brushes.Transparent;
            SettingsSaveBar.BorderBrush = Hex("#2563EB");
            SettingsSaveHintText.Text = $"预览方案「{profile.Name}」· 点上方「切换启用」生效";
            SettingsSaveHintText.Foreground = Hex("#93C5FD");
        }
        else
        {
            SettingsSaveBar.Background = System.Windows.Media.Brushes.Transparent;
            SettingsSaveBar.SetResourceReference(Border.BorderBrushProperty, "DividerRowBrush");
            SettingsSaveHintText.Text = !string.IsNullOrWhiteSpace(profile?.Description) ? profile.Description : "";
            SettingsSaveHintText.Foreground = Hex("#64748B");
        }
    }

    private void ShowSettingsSavedBanner(string msg)
    {
        if (SettingsSaveBar != null && SettingsSaveHintText != null)
        {
            SettingsSaveBar.Background = System.Windows.Media.Brushes.Transparent;
            SettingsSaveBar.SetResourceReference(Border.BorderBrushProperty, "DividerRowBrush");
            SettingsSaveHintText.Text = msg;
            SettingsSaveHintText.Foreground = Hex("#6EE7B7");
        }
        SetStatus(msg);
    }

    private void CommitRosterSettingsSave()
    {
        _reg.SyncActiveTeamProfileWithCurrentState(saveToDisk: true);
        _hasUnsavedSettingsChanges = false;
        UpdateSettingsSaveBarVisual();
        RefreshAllViews();
        _floatingWindow?.RefreshOverlayData(force: true);
        var activeName = _reg.UserConfig.TeamProfiles?.FirstOrDefault(p => p.Id == _reg.UserConfig.ActiveTeamProfileId)?.Name ?? "当前方案";
        ShowSettingsSavedBanner($"✅ 已保存并同步写入当前配置方案「{activeName}」！");
        if (!AppPaths.IsIsolatedTest)
        {
            MessageBox.Show(
                this,
                $"当前配置与模型档位已保存至激活方案「{activeName}」并正式生效！\n\n（所有工程路由与桌面悬浮窗已同步更新）",
                "保存配置成功",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void DiscardRosterSettingsChanges()
    {
        _reg.ReloadUserConfigFromDiskForce();
        _hasUnsavedSettingsChanges = false;
        UpdateSettingsSaveBarVisual();
        RefreshAllViews();
        _floatingWindow?.RefreshOverlayData(force: true);
        SetStatus("↩ 已放弃未保存修改并恢复上次已保存配置。");
    }

    private void BtnSaveRosterSettings_Click(object sender, RoutedEventArgs e)
    {
        CommitRosterSettingsSave();
    }

    private void BtnDiscardRosterSettings_Click(object sender, RoutedEventArgs e)
    {
        DiscardRosterSettingsChanges();
    }

    private async void InitFoundationBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null) return;
        if (AppPaths.IsIsolatedTest)
        {
            SetStatus(_sync.InitializeSharedFoundation(_reg, proj));
            RefreshAllViews();
            return;
        }
        if (!LongActionTracker.TryBegin(LongActionTracker.RulesInit, "初始化中"))
        {
            SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.RulesInit));
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LongActionTracker.ShowBusy(InitFoundationBtn);
        try
        {
            var msg = await System.Threading.Tasks.Task.Run(() => _sync.InitializeSharedFoundation(_reg, proj));
            RefreshAllViews();
            SetStatus($"✅ {msg}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ 初始化工程目录失败：{TruncateStatus(ex.Message)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        finally
        {
            EndLongAction(LongActionTracker.RulesInit);
        }
    }

    private async void SyncAllNowBtn_Click(object sender, RoutedEventArgs e)
    {
        var projects = _reg.UserConfig.Projects.Where(p => !string.IsNullOrWhiteSpace(p.PrimaryRoot) && Directory.Exists(p.PrimaryRoot)).ToList();
        if (projects.Count == 0)
        {
            var cur = _reg.GetActiveProject();
            if (cur != null) projects.Add(cur);
        }
        if (projects.Count == 0)
        {
            SetStatus("没有可同步的工程目录。");
            return;
        }
        if (AppPaths.IsIsolatedTest)
        {
            string lastMsg = "";
            int syncedCount = 0;
            foreach (var p in projects)
            {
                lastMsg = _sync.ExecuteSyncAll(_reg, p, _bus);
                syncedCount++;
            }
            RefreshAllViews();
            SetStatus(syncedCount > 1
                ? $"✅ 已完成全局规则（GEMINI.md / shared-hard-gates.md）及全部 {syncedCount} 个工程目录的一键同步。"
                : lastMsg);
            return;
        }
        if (!LongActionTracker.TryBegin(LongActionTracker.RulesSync, "同步中"))
        {
            SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.RulesSync));
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LongActionTracker.ShowBusy(SyncAllNowBtn);
        LongActionTracker.ShowBusy(SyncAnchorsOverviewBtn);
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() =>
            {
                string last = "";
                int n = 0;
                foreach (var p in projects)
                {
                    last = _sync.ExecuteSyncAll(_reg, p, _bus);
                    n++;
                }
                return (last, n);
            });
            RefreshAllViews();
            int sec = Math.Max(0, (int)sw.Elapsed.TotalSeconds);
            SetStatus(result.n > 1
                ? $"✅ 已完成全局规则（GEMINI.md / shared-hard-gates.md）及全部 {result.n} 个工程目录的一键同步（耗时 {sec}s）。"
                : $"✅ {result.last}（耗时 {sec}s）");
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ 规则与技能同步失败：{TruncateStatus(ex.Message)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        finally
        {
            EndLongAction(LongActionTracker.RulesSync);
        }
    }

    private async void RestoreSyncBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AppPaths.IsIsolatedTest)
        {
            SetStatus(_sync.RestoreLatestBackup());
            RefreshAllViews();
            return;
        }
        if (!LongActionTracker.TryBegin(LongActionTracker.RulesRestore, "恢复中"))
        {
            SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.RulesRestore));
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LongActionTracker.ShowBusy(RestoreSyncBackupBtn);
        try
        {
            var msg = await System.Threading.Tasks.Task.Run(() => _sync.RestoreLatestBackup());
            RefreshAllViews();
            SetStatus($"✅ {msg}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ 恢复备份失败：{TruncateStatus(ex.Message)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        finally
        {
            EndLongAction(LongActionTracker.RulesRestore);
        }
    }

    private void AddTagBtn_Click(object sender, RoutedEventArgs e)
    {
        var id = NewTagIdBox.Text.Trim();
        var name = NewTagNameBox.Text.Trim();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return;
        if (_reg.Tags.Tags.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"标签 ID「{id}」已存在。");
            return;
        }
        _reg.Tags.Tags.Add(new CapabilityTagSpec
        {
            Id = id,
            Name = name,
            Description = NewTagDescBox.Text.Trim()
        });
        _reg.SaveRegistries();
        RefreshAllViews();
        SetStatus($"已新增擅长标签：{name} ({id})");
    }

    private void AddDetectorRuleBtn_Click(object sender, RoutedEventArgs e)
    {
        var added = Dialogs.ShowAddCustomAgentDialog(this, _reg);
        if (added != null)
        {
            _reg.UserConfig.Agents.Add(added);
            _reg.SaveUserConfig();
            RefreshAllViews();
            SetStatus($"已添加探测成员：{added.Name}");
        }
    }

    private void ExportPresetBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出工作流与标签预设模板",
            Filter = "AI 协同预设包 (*.aicollab.json)|*.aicollab.json",
            FileName = "workflow_preset.aicollab.json"
        };
        if (dlg.ShowDialog(this) == true)
        {
            _share.ExportWorkflowTemplateBundle(_reg, dlg.FileName);
            SetStatus($"已导出脱敏工作流预设模板：{dlg.FileName}");
        }
    }

    private void ImportPresetBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入工作流与标签预设模板",
            Filter = "AI 协同预设包 (*.aicollab.json)|*.aicollab.json"
        };
        if (dlg.ShowDialog(this) == true)
        {
            var msg = _share.ImportWorkflowTemplateBundle(_reg, dlg.FileName);
            RefreshAllViews();
            SetStatus(msg);
        }
    }

    private async void ExportShareZipBtn_Click(object sender, RoutedEventArgs e)
    {
        if (LongActionTracker.IsBusy(LongActionTracker.ExportShare))
        {
            SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.ExportShare));
            return;
        }
        var defaultDir = Path.Combine(AppPaths.UserDataDir, "shares");
        Directory.CreateDirectory(defaultDir);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出可分享给他人的纯净绿色压缩包（不含任何个人路径与项目记录）",
            InitialDirectory = defaultDir,
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"AI协同中心_v{AppPaths.AppVersion}_Win64绿色分享版.zip"
        };
        if (dlg.ShowDialog(this) != true) return;
        if (!LongActionTracker.TryBegin(LongActionTracker.ExportShare, "导出中"))
        {
            SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.ExportShare));
            return;
        }
        var dest = dlg.FileName;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LongActionTracker.ShowBusy(ExportShareZipBtn);
        SetStatus("正在生成绿色分享包…");
        try
        {
            var outZip = await System.Threading.Tasks.Task.Run(() => _share.ExportCleanPortableZip(dest));
            SetStatus($"✅ 已生成纯净绿色分享包（耗时 {(int)sw.Elapsed.TotalSeconds}s）：{outZip}");
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ 导出绿色分享包失败：{TruncateStatus(ex.Message)}（耗时 {(int)sw.Elapsed.TotalSeconds}s）");
        }
        finally
        {
            EndLongAction(LongActionTracker.ExportShare);
        }
    }

    private void OpenProjectDirBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj != null && Directory.Exists(proj.PrimaryRoot))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{proj.PrimaryRoot}\"") { UseShellExecute = true });
    }

    private void OpenCollabFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null) return;
        _bus.EnsureTaskCard(proj);
        var dir = _bus.GetCollabDir(proj);
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void OpenRegistryDirBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(AppPaths.RegistryDir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.RegistryDir}\"") { UseShellExecute = true });
    }

    // ==================== 软件版本展示与自检更新 ====================

    private void ApplyAppUpdateChrome()
    {
        var display = AppUpdateService.GetCurrentVersionDisplay();
        if (HeaderVersionText != null) HeaderVersionText.Text = display;
        if (TextAboutCurrentVersion != null) TextAboutCurrentVersion.Text = $"AIDeck · 多 AI 协同中心 {display}";
        if (TextUpdateRepoUrl != null && !TextUpdateRepoUrl.IsKeyboardFocusWithin)
            TextUpdateRepoUrl.Text = _reg.UserConfig.AppUpdateRepoUrl ?? "";
        if (ChkAutoCheckAppUpdate != null)
            ChkAutoCheckAppUpdate.IsChecked = _reg.UserConfig.AutoCheckAppUpdateOnStartup;
        if (TextAppUpdateStatus != null && _lastAppUpdate != null && !string.IsNullOrWhiteSpace(_lastAppUpdate.Message))
            TextAppUpdateStatus.Text = _lastAppUpdate.Message;
        LongActionTracker.ApplyBusyIfNeeded(BtnCheckAppUpdate, LongActionTracker.AppUpdate, "立即检查更新");
    }

    private void ScheduleStartupAppUpdateCheck()
    {
        if (AppPaths.IsIsolatedTest) return;
        _appUpdateStartupTimer?.Stop();
        _appUpdateStartupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _appUpdateStartupTimer.Tick += (_, _) =>
        {
            _appUpdateStartupTimer?.Stop();
            if (!IsLoaded || AppPaths.IsIsolatedTest) return;
            if (!_reg.UserConfig.AutoCheckAppUpdateOnStartup) return;
            BeginAppUpdateCheck(silent: true);
        };
        _appUpdateStartupTimer.Start();
    }

    private void HeaderVersionBtn_Click(object sender, RoutedEventArgs e) => BeginAppUpdateCheck(silent: false);

    private void BtnCheckAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        PersistUpdateRepoUrlFromUi();
        BeginAppUpdateCheck(silent: false);
    }

    private void TextUpdateRepoUrl_LostFocus(object sender, RoutedEventArgs e) => PersistUpdateRepoUrlFromUi();

    private void PersistUpdateRepoUrlFromUi()
    {
        if (TextUpdateRepoUrl == null || _suppressEvents) return;
        var text = (TextUpdateRepoUrl.Text ?? "").Trim();
        if (string.Equals(text, _reg.UserConfig.AppUpdateRepoUrl ?? "", StringComparison.Ordinal)) return;
        _reg.UserConfig.AppUpdateRepoUrl = text;
        _reg.SaveUserConfig();
    }

    private void BeginAppUpdateCheck(bool silent)
    {
        if (AppPaths.IsIsolatedTest)
        {
            if (!silent) SetStatus(AppUpdateService.IsolatedSkipMessage);
            return;
        }

        if (!LongActionTracker.TryBegin(LongActionTracker.AppUpdate, "检查更新"))
        {
            if (!silent) SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.AppUpdate));
            return;
        }

        if (!silent)
        {
            LongActionTracker.ShowBusy(BtnCheckAppUpdate);
            if (HeaderVersionBtn != null) HeaderVersionBtn.IsEnabled = false;
            SetStatus("正在检查软件更新…");
            if (TextAppUpdateStatus != null) TextAppUpdateStatus.Text = "正在检查软件更新…";
        }

        var repo = (TextUpdateRepoUrl?.Text ?? _reg.UserConfig.AppUpdateRepoUrl ?? "").Trim();
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            AppUpdateCheckResult result;
            try
            {
                result = await AppUpdateService.CheckForUpdateAsync(repo).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new AppUpdateCheckResult
                {
                    Status = AppUpdateStatus.NetworkFailed,
                    Message = "检查更新失败：" + ex.Message,
                    CurrentVersion = AppUpdateService.GetCurrentVersion()
                };
            }

            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!IsLoaded) return;
                    ApplyAppUpdateCheckResult(result, silent);
                }
                finally
                {
                    if (HeaderVersionBtn != null) HeaderVersionBtn.IsEnabled = true;
                    EndLongAction(LongActionTracker.AppUpdate);
                }
            });
        });
    }

    private void ApplyAppUpdateCheckResult(AppUpdateCheckResult result, bool silent)
    {
        _lastAppUpdate = result;
        if (TextAppUpdateStatus != null && (!silent || result.Status == AppUpdateStatus.UpdateAvailable))
            TextAppUpdateStatus.Text = result.Message;

        if (result.Status == AppUpdateStatus.UpdateAvailable)
        {
            var latest = string.IsNullOrWhiteSpace(result.LatestVersion)
                ? "新版本"
                : (result.LatestVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    ? result.LatestVersion
                    : "v" + result.LatestVersion);
            if (HeaderUpdateBadgeBtn != null)
            {
                HeaderUpdateBadgeBtn.Visibility = Visibility.Visible;
                HeaderUpdateBadgeBtn.Content = $"发现新版本 {latest}";
                HeaderUpdateBadgeBtn.ToolTip = string.IsNullOrWhiteSpace(result.Notes)
                    ? result.Message
                    : result.Message + "\n" + result.Notes;
            }
            SetStatus(result.Message);
            return;
        }

        if (!silent)
        {
            if (HeaderUpdateBadgeBtn != null) HeaderUpdateBadgeBtn.Visibility = Visibility.Collapsed;
            SetStatus(result.Message);
        }
    }

    private async void HeaderUpdateBadgeBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = _lastAppUpdate;
        if (result == null || result.Status != AppUpdateStatus.UpdateAvailable)
        {
            BeginAppUpdateCheck(silent: false);
            return;
        }

        string latest = string.IsNullOrWhiteSpace(result.LatestVersion) ? "" : " " + result.LatestVersion.Trim();
        string notes = string.IsNullOrWhiteSpace(result.Notes) ? "" : "\n\n" + TruncateStatus(result.Notes, 280);
        string prompt = string.IsNullOrWhiteSpace(result.DownloadUrl)
            ? $"发现新版本{latest}。没有安装包直链，是否打开发布页？{notes}"
            : $"发现新版本{latest}。是否下载单文件，退出后替换并重启？{notes}";
        if (MessageBox.Show(this, prompt, "软件更新", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;

        if (!LongActionTracker.TryBegin(LongActionTracker.AppUpdate, "下载更新"))
        {
            SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.AppUpdate));
            return;
        }

        LongActionTracker.ShowBusy(BtnCheckAppUpdate);
        if (HeaderUpdateBadgeBtn != null) HeaderUpdateBadgeBtn.IsEnabled = false;
        try
        {
            var apply = await AppUpdateService.DownloadAndApplyExeUpdateAsync(result.DownloadUrl, result.ReleasePageUrl);
            if (TextAppUpdateStatus != null) TextAppUpdateStatus.Text = apply.Message;
            SetStatus(apply.Message);
            if (apply.RestartScheduled)
            {
                _allowFullExit = true;
                Application.Current?.Shutdown();
            }
        }
        catch (Exception ex)
        {
            SetStatus("下载更新失败：" + TruncateStatus(ex.Message));
        }
        finally
        {
            if (HeaderUpdateBadgeBtn != null) HeaderUpdateBadgeBtn.IsEnabled = true;
            EndLongAction(LongActionTracker.AppUpdate);
        }
    }

    // ==================== PAGE 6: 系统与悬浮窗设置（开机自启 / 展开收起形态 / 透明度 / 快捷方式） ====================

    public void SyncAppSettingsControlsIfLoaded()
    {
        if (!IsLoaded) return;
        RenderAppSettingsPage();
    }

    private void RenderAppSettingsPage()
    {
        _suppressEvents = true;
        try
        {
            var cfg = _reg.UserConfig;
            var regSt = _startupSvc.QueryStartupRegistryStatus();

            // 如果系统注册表中已经开启自启，则同步显示状态
            if (regSt.IsRegistered && !cfg.RunAtStartup)
            {
                cfg.RunAtStartup = true;
            }

            ChkWorkflowEngineEnabled.IsChecked = cfg.WorkflowEngineEnabled;
            ChkWorkflowOnlyWhenRunning.IsChecked = cfg.WorkflowOnlyWhenAppRunning;

            ChkRunAtStartup.IsChecked = cfg.RunAtStartup;
            ChkStartupDirectFloating.IsChecked = cfg.StartDirectlyIntoFloatingMode;
            MigrateAndSyncCloseButtonAction(cfg);
            ChkCloseToFloating.IsChecked = cfg.CloseMainWindowToFloating;
            if (ComboCloseButtonAction != null)
            {
                SelectComboByTag(ComboCloseButtonAction, ResolveCloseActionChoice(cfg));
            }

            ChkFloatingEnabled.IsChecked = cfg.FloatingWindowEnabled;
            ChkFloatingTopmost.IsChecked = cfg.FloatingWindowTopmost;
            ChkFloatingShowWorkflows.IsChecked = cfg.FloatingShowWorkflowsStrip;
            // 形态绝对锁定：自动展开/收起永久停用，设置页仅展示禁用态
            cfg.FloatingAutoExpandOnWorking = false;
            cfg.FloatingAutoCollapseOnDone = false;
            ChkFloatingAutoExpand.IsChecked = false;
            ChkFloatingAutoCollapse.IsChecked = false;
            ChkFloatingAutoExpand.IsEnabled = false;
            ChkFloatingAutoCollapse.IsEnabled = false;
            ChkAutoSyncCloudRec.IsChecked = cfg.AutoSyncCloudRecommendations;
            if (ChkEnableSoundEffects != null) ChkEnableSoundEffects.IsChecked = cfg.EnableSoundEffects;
            if (ChkSoundLightweight != null) ChkSoundLightweight.IsChecked = cfg.SoundLightweightMode;
            ApplyAppUpdateChrome();
            SoundFxService.ApplySoundPreference(cfg);

            var curThemeId = string.IsNullOrWhiteSpace(cfg.FloatingThemeStyle) ? "dark_slate" : cfg.FloatingThemeStyle;
            SelectComboByTag(ComboFloatingTheme, curThemeId);
            RenderThemeSwatches(curThemeId);
            SelectComboByTag(ComboFloatingMode, cfg.FloatingWindowCollapsed ? "collapsed" : "expanded");
            int clampedOpacity = Math.Clamp(cfg.FloatingOpacityPercent, 25, 100);
            SliderFloatingOpacity.Value = clampedOpacity;
            TextFloatingOpacityPct.Text = $"{clampedOpacity}%";
            SelectComboByTag(ComboFloatingRefresh, cfg.FloatingRefreshIntervalMs.ToString());

            ChkEnableNetRetry.IsChecked = cfg.EnableNetworkAutoRetry;
            ChkWaitLocalNetRecover.IsChecked = cfg.WaitForLocalNetworkRecovery;
            ChkEnableAgentFailover.IsChecked = cfg.EnableAgentFailoverOnOutage;
            ChkAutoResumeOnNetRecover.IsChecked = cfg.AutoResumeInterruptedChainOnNetRecover;
            if (ChkAutoTrustWorkspaces != null) ChkAutoTrustWorkspaces.IsChecked = cfg.AutoTrustManagedWorkspaces;
            SelectComboByTag(ComboMaxNetRetries, Math.Clamp(cfg.MaxNetworkRetries, 2, 8).ToString());
            SelectComboByTag(ComboSingleTimeout, Math.Clamp(cfg.SingleAttemptTimeoutSeconds, 120, 300).ToString());
            SelectComboByTag(ComboStallRecoveryPolicy, string.IsNullOrWhiteSpace(cfg.StallRecoveryPolicy) ? "auto_then_ask" : cfg.StallRecoveryPolicy);

            RefreshStartupRegistryStatusBadge(regSt);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void WorkflowLifecycleCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        var cfg = _reg.UserConfig;
        bool prevWf = cfg.WorkflowEngineEnabled;
        cfg.WorkflowEngineEnabled = ChkWorkflowEngineEnabled.IsChecked == true;
        cfg.WorkflowOnlyWhenAppRunning = ChkWorkflowOnlyWhenRunning.IsChecked == true;
        _reg.SaveUserConfig();

        if (cfg.WorkflowEngineEnabled != prevWf)
        {
            ApplyWorkflowAnchorsByCurrentState(silent: false);
            RefreshAllViews();
            RefreshWorkflowDiagram();
        }
        else
        {
            UpdateWorkflowSwitchBadges();
            SetStatus(cfg.WorkflowOnlyWhenAppRunning
                ? "已设置：协同工作流仅在本软件运行时成立（关闭退出本软件将自动卸载锚点并恢复各 AI 原生独立状态）。"
                : "已设置：关闭软件后仍保留协同规则文件。");
        }
    }

    private void ComboFloatingTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        if (ComboFloatingTheme.SelectedItem is ComboBoxItem item && item.Tag is string themeTag)
        {
            ApplySelectedGlobalTheme(themeTag, item.Content?.ToString() ?? themeTag);
        }
    }

    private void ApplySelectedGlobalTheme(string themeTag, string displayLabel)
    {
        _reg.UserConfig.FloatingThemeStyle = themeTag;
        _reg.SaveUserConfig();
        ThemeService.ApplyGlobalTheme(themeTag);
        RenderThemeSwatches(themeTag);
        _floatingWindow?.ApplyUserPreferencesFromSettings();
        SetStatus($"🎨 已切换全局界面与悬浮窗颜色主题：{displayLabel}");
    }

    private void RenderThemeSwatches(string activeThemeId)
    {
        if (ThemeSwatchesPanel == null) return;
        ThemeSwatchesPanel.Children.Clear();
        var resolvedActive = ThemeService.Resolve(activeThemeId).Id;

        foreach (var theme in ThemeService.Catalog)
        {
            bool isSelected = string.Equals(theme.Id, resolvedActive, StringComparison.OrdinalIgnoreCase);
            var chip = new Border
            {
                Background = Hex(theme.SectionBgHex),
                BorderBrush = isSelected ? Hex(theme.AccentPrimaryHex) : Hex(theme.BorderSectionHex),
                BorderThickness = new Thickness(isSelected ? 1.6 : 1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 9, 4),
                Margin = new Thickness(0, 0, 6, 5),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{theme.DisplayName}（点击立即切换主界面与悬浮窗颜色主题）"
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Hex(theme.PreviewDotHex),
                Stroke = Hex("#E2E8F0"),
                StrokeThickness = isSelected ? 1.2 : 0.4,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = isSelected ? $"✓ {theme.ShortTag}" : theme.ShortTag,
                FontSize = 11.2,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isSelected ? Hex("#F8FAFC") : Hex("#CBD5E1"),
                VerticalAlignment = VerticalAlignment.Center
            });
            chip.Child = sp;
            var capturedTheme = theme;
            chip.MouseLeftButtonUp += (_, _) =>
            {
                _suppressEvents = true;
                try { SelectComboByTag(ComboFloatingTheme, capturedTheme.Id); }
                finally { _suppressEvents = false; }
                ApplySelectedGlobalTheme(capturedTheme.Id, capturedTheme.DisplayName);
            };
            ThemeSwatchesPanel.Children.Add(chip);
        }
    }

    private static void SelectComboByTag(ComboBox combo, string targetTag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), targetTag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0 && combo.SelectedIndex < 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void RefreshStartupRegistryStatusBadge((bool IsRegistered, string CommandLine) regSt)
    {
        if (regSt.IsRegistered)
        {
            StartupRegStatusTitle.Text = "开机自启已开";
            StartupRegStatusTitle.ToolTip = "登录系统后自动运行";
            StartupRegStatusTitle.Foreground = Hex("#34D399");
            StartupRegCommandText.Text = regSt.CommandLine;
            StartupRegCommandText.ToolTip = regSt.CommandLine;
        }
        else
        {
            StartupRegStatusTitle.Text = "开机自启未开";
            StartupRegStatusTitle.ToolTip = "勾选「开机自动启动」后写入当前用户启动项";
            StartupRegStatusTitle.Foreground = Hex("#93C5FD");
            StartupRegCommandText.Text = "HKCU\\...\\Run\\AICollabHub";
            StartupRegCommandText.ToolTip = string.IsNullOrWhiteSpace(regSt.CommandLine)
                ? "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\AICollabHub"
                : regSt.CommandLine;
        }
    }

    private void AppSettingCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;

        var cfg = _reg.UserConfig;
        bool prevRun = cfg.RunAtStartup;
        bool prevDirect = cfg.StartDirectlyIntoFloatingMode;

        cfg.RunAtStartup = ChkRunAtStartup.IsChecked == true;
        cfg.StartDirectlyIntoFloatingMode = ChkStartupDirectFloating.IsChecked == true;
        // 旧复选框若被程序改写，仍同步到 CloseButtonAction；正常路径以 Combo 为准
        if (ComboCloseButtonAction?.SelectedItem is ComboBoxItem closeItem && closeItem.Tag is string closeTag)
        {
            SyncCloseButtonActionFields(cfg, closeTag);
            ChkCloseToFloating.IsChecked = cfg.CloseMainWindowToFloating;
        }
        else
        {
            cfg.CloseMainWindowToFloating = ChkCloseToFloating.IsChecked == true;
            SyncCloseButtonActionFields(cfg, cfg.CloseMainWindowToFloating ? "minimize_to_tray" : "ask");
        }

        cfg.FloatingWindowEnabled = ChkFloatingEnabled.IsChecked == true;
        cfg.FloatingWindowTopmost = ChkFloatingTopmost.IsChecked == true;
        cfg.FloatingShowWorkflowsStrip = ChkFloatingShowWorkflows.IsChecked == true;
        // 形态绝对锁定：忽略勾选，强制永久关闭自动展开/收起
        cfg.FloatingAutoExpandOnWorking = false;
        cfg.FloatingAutoCollapseOnDone = false;
        if (ChkFloatingAutoExpand != null) { ChkFloatingAutoExpand.IsChecked = false; ChkFloatingAutoExpand.IsEnabled = false; }
        if (ChkFloatingAutoCollapse != null) { ChkFloatingAutoCollapse.IsChecked = false; ChkFloatingAutoCollapse.IsEnabled = false; }
        cfg.AutoSyncCloudRecommendations = ChkAutoSyncCloudRec.IsChecked == true;
        if (ChkEnableSoundEffects != null) cfg.EnableSoundEffects = ChkEnableSoundEffects.IsChecked == true;
        if (ChkSoundLightweight != null) cfg.SoundLightweightMode = ChkSoundLightweight.IsChecked == true;
        if (ChkAutoCheckAppUpdate != null) cfg.AutoCheckAppUpdateOnStartup = ChkAutoCheckAppUpdate.IsChecked == true;
        // 设置页开关变更：立即写硬闸门并停播循环音，再落盘；定时刷新不得回写覆盖（此处只写内存已确认值）
        SoundFxService.ApplySoundPreference(cfg);
        _floatingWindow?.SyncQuickSoundButtonUi();

        _reg.SaveUserConfig();

        if (cfg.RunAtStartup != prevRun || (cfg.RunAtStartup && cfg.StartDirectlyIntoFloatingMode != prevDirect))
        {
            var res = _startupSvc.ApplyStartupSetting(cfg.RunAtStartup, cfg.StartDirectlyIntoFloatingMode);
            RefreshStartupRegistryStatusBadge(_startupSvc.QueryStartupRegistryStatus());
            SetStatus(res.Message);
        }
        else
        {
            SetStatus("已保存并立即应用悬浮窗与系统常驻设置。");
        }

        if (cfg.FloatingWindowEnabled)
        {
            ShowFloatingOverlayWindow();
            _floatingWindow?.ApplyUserPreferencesFromSettings();
        }
        else
        {
            _floatingWindow?.Hide();
        }
        UpdateFloatingButtonBadge();
    }

    private void MuteCursorChimeBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (ok, message) = SoundFxService.SilenceCursorAccessibilityAudio();
            SetStatus(ok ? $"✅ {message}" : $"❌ {message}");
        }
        catch (Exception ex)
        {
            SetStatus($"❌ 写入 Cursor 用户设置失败：{ex.Message}");
        }
    }

    private void ComboFloatingMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        if (ComboFloatingMode.SelectedItem is ComboBoxItem item)
        {
            bool collapsed = string.Equals(item.Tag?.ToString(), "collapsed", StringComparison.OrdinalIgnoreCase);
            _reg.UserConfig.FloatingWindowCollapsed = collapsed;
            _reg.SaveUserConfig();
            if (_reg.UserConfig.FloatingWindowEnabled)
            {
                ShowFloatingOverlayWindow();
                _floatingWindow?.ApplyCollapsedVisual(collapsed, preserveRightAnchor: true);
            }
            SetStatus(collapsed
                ? "已将桌面悬浮窗切换为「收起极简胶囊态」（仅单行简要显示在岗数与最新一句进度）。"
                : "已将桌面悬浮窗切换为「展开完整监控态」（显示全部工作流与各 AI 卡片详情）。");
        }
    }

    private void SliderFloatingOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || !IsLoaded) return;
        int pct = Math.Clamp((int)Math.Round(e.NewValue), 25, 100);
        TextFloatingOpacityPct.Text = $"{pct}%";
        _reg.UserConfig.FloatingOpacityPercent = pct;
        _reg.UserConfig.FloatingWindowTranslucent = pct < 92;
        _reg.SaveUserConfig();
        _floatingWindow?.ApplyUserPreferencesFromSettings();
        SetStatus($"已将桌面悬浮窗透明度手动调整为 {pct}%。");
    }

    private void BtnOpacityPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int pct))
        {
            SliderFloatingOpacity.Value = Math.Clamp(pct, 25, 100);
        }
    }

    private void ComboFloatingRefresh_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        if (ComboFloatingRefresh.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int ms))
        {
            _reg.UserConfig.FloatingRefreshIntervalMs = ms;
            _reg.SaveUserConfig();
            _floatingWindow?.ApplyUserPreferencesFromSettings();
            SetStatus($"已将实时状态刷新频率调整为 {ms / 1000.0:0.0} 秒/次。");
        }
    }

    private void BtnPreviewExpandFloating_Click(object sender, RoutedEventArgs e)
    {
        _reg.UserConfig.FloatingWindowEnabled = true;
        _reg.UserConfig.FloatingWindowCollapsed = false;
        _reg.SaveUserConfig();
        ShowFloatingOverlayWindow();
        _floatingWindow?.ApplyCollapsedVisual(false, preserveRightAnchor: true);
        RenderAppSettingsPage();
        UpdateFloatingButtonBadge();
        SetStatus("桌面悬浮窗已展开为完整监控面板。");
    }

    private void BtnPreviewCollapseFloating_Click(object sender, RoutedEventArgs e)
    {
        _reg.UserConfig.FloatingWindowEnabled = true;
        _reg.UserConfig.FloatingWindowCollapsed = true;
        _reg.SaveUserConfig();
        ShowFloatingOverlayWindow();
        _floatingWindow?.ApplyCollapsedVisual(true, preserveRightAnchor: true);
        RenderAppSettingsPage();
        UpdateFloatingButtonBadge();
        SetStatus("桌面悬浮窗已收起为单行极简胶囊条。");
    }

    private void BtnResetFloatingPos_Click(object sender, RoutedEventArgs e)
    {
        _reg.UserConfig.FloatingWindowEnabled = true;
        _reg.SaveUserConfig();
        ShowFloatingOverlayWindow();
        _floatingWindow?.ResetPositionToTopRight();
        UpdateFloatingButtonBadge();
        SetStatus("已将桌面悬浮窗复位到主显示器右上角。");
    }

    private void BtnCreateDesktopShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var res = _startupSvc.CreateDesktopShortcuts();
        SetStatus(res.Message);
    }

    private void BtnVerifyStartupReg_Click(object sender, RoutedEventArgs e)
    {
        var regSt = _startupSvc.QueryStartupRegistryStatus();
        RefreshStartupRegistryStatusBadge(regSt);
        SetStatus(regSt.IsRegistered ? $"开机自启已生效：{regSt.CommandLine}" : "当前未启用开机自启（勾选上方开关即可启用）。");
    }

    private void BtnForceQuitApp_Click(object sender, RoutedEventArgs e)
    {
        ForceCloseApp();
    }

    private void BtnOpenUserDataDir_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(AppPaths.UserDataDir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.UserDataDir}\"") { UseShellExecute = true });
    }

    private void BtnOpenAiChatDir_Click(object sender, RoutedEventArgs e)
    {
        var chatDir = @"F:\AIChat";
        if (Directory.Exists(chatDir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{chatDir}\"") { UseShellExecute = true });
    }

    // ==================== 网络容灾、断线重连检测与任务链断点续跑 ====================

    private void PollNetworkHealthAndAutoResumeAsync()
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var (online, detail) = NetworkResilienceService.ProbeLocalNetworkHealth(500);
            bool recoveredFromOffline = !_lastKnownNetOnline && online;
            _lastKnownNetOnline = online;

            Dispatcher.BeginInvoke(() =>
            {
                UpdateNetworkHealthBadgeUi(online, detail);

                if (recoveredFromOffline &&
                    _reg.UserConfig.WorkflowEngineEnabled &&
                    _reg.UserConfig.AutoResumeInterruptedChainOnNetRecover &&
                    !_isResumingCheckpointInBackground)
                {
                    var proj = _reg.GetActiveProject();
                    var cp = NetworkResilienceService.LoadInterruptedCheckpoint(proj);
                    if (cp != null)
                    {
                        TriggerResumeCheckpointAsync(autoTriggered: true);
                    }
                }
            });
        });
    }

    private void UpdateNetworkHealthBadgeUi(bool online, string detail)
    {
        var retrySummary = _reg.UserConfig.EnableNetworkAutoRetry
            ? $"最多重连 {_reg.UserConfig.MaxNetworkRetries} 次"
            : "自动重连已关";

        if (NetworkHealthBadge != null)
        {
            NetworkHealthBadge.Text = online ? "网络在线" : "网络中断";
            NetworkHealthBadge.ToolTip = online ? retrySummary : "本机断网，任务会挂起等待恢复";
            NetworkHealthBadge.Foreground = online ? Hex("#34D399") : Hex("#FB923C");
        }

        if (TextNetDiagStatus != null)
        {
            TextNetDiagStatus.Text = online ? "网络正常" : "网络中断";
            TextNetDiagStatus.ToolTip = online ? $"{detail} · {retrySummary}" : detail;
            TextNetDiagStatus.Foreground = online ? Hex("#34D399") : Hex("#FB923C");
        }
    }

    private void NetworkResilienceSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        var cfg = _reg.UserConfig;
        cfg.EnableNetworkAutoRetry = ChkEnableNetRetry.IsChecked == true;
        cfg.WaitForLocalNetworkRecovery = ChkWaitLocalNetRecover.IsChecked == true;
        cfg.EnableAgentFailoverOnOutage = ChkEnableAgentFailover.IsChecked == true;
        cfg.AutoResumeInterruptedChainOnNetRecover = ChkAutoResumeOnNetRecover.IsChecked == true;
        if (ChkAutoTrustWorkspaces != null) cfg.AutoTrustManagedWorkspaces = ChkAutoTrustWorkspaces.IsChecked == true;
        _reg.SaveUserConfig();

        UpdateNetworkHealthBadgeUi(_lastKnownNetOnline, "配置已实时生效");
        SetStatus("已保存网络断线自动重连、故障转移与断点续跑设置。");
    }

    private void ComboNetworkResilience_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        var cfg = _reg.UserConfig;
        if (ComboMaxNetRetries.SelectedItem is ComboBoxItem rItem && int.TryParse(rItem.Tag?.ToString(), out int maxR))
        {
            cfg.MaxNetworkRetries = Math.Clamp(maxR, 1, 8);
        }
        if (ComboSingleTimeout.SelectedItem is ComboBoxItem tItem && int.TryParse(tItem.Tag?.ToString(), out int timeoutS))
        {
            cfg.SingleAttemptTimeoutSeconds = Math.Clamp(timeoutS, 60, 600);
        }
        if (ComboStallRecoveryPolicy.SelectedItem is ComboBoxItem sItem && sItem.Tag is string stallPol && !string.IsNullOrWhiteSpace(stallPol))
        {
            cfg.StallRecoveryPolicy = stallPol;
        }
        _reg.SaveUserConfig();
        UpdateNetworkHealthBadgeUi(_lastKnownNetOnline, "参数已更新");
        SetStatus($"已更新容灾与防卡死策略：最大重连 {cfg.MaxNetworkRetries} 次 · 超时 {cfg.SingleAttemptTimeoutSeconds} 秒 · 策略 {cfg.StallRecoveryPolicy}。");
    }

    private void BtnDismissStuckModalsNow_Click(object sender, RoutedEventArgs e)
    {
        if (!LongActionTracker.TryBegin(LongActionTracker.DismissModals, "清理中"))
        {
            SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.DismissModals));
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LongActionTracker.ShowBusy(BtnDismissStuckModalsNow);
        SetStatus("🧹 正在后台零焦点静默扫描并关闭外部 AI 客户端残留的阻塞弹窗（不占用鼠标键盘）...");
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            Exception? error = null;
            try { AutoDispatchService.DismissStuckAgentModalsSilent(); }
            catch (Exception ex) { error = ex; }
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    int sec = Math.Max(0, (int)sw.Elapsed.TotalSeconds);
                    SetStatus(error == null
                        ? $"✓ 已完成后台阻塞弹窗静默清理（全程未移动鼠标或切换窗口焦点，耗时 {sec}s）。"
                        : $"⚠ 清理卡住弹窗失败：{TruncateStatus(error.Message)}（耗时 {sec}s）");
                }
                finally
                {
                    EndLongAction(LongActionTracker.DismissModals);
                }
            });
        });
    }

    private void BtnChooseCheckpointResolution_Click(object sender, RoutedEventArgs e)
    {
        OpenStallResolutionDialogAndExecute(null, this);
    }

    public void OpenStallResolutionDialogAndExecute(AgentRosterEntry? targetAgent = null, Window? dialogOwner = null)
    {
        var proj = _reg.GetActiveProject();
        var globalLiveMap = AutoDispatchService.ReadLatestGlobalWorkStates();
        if (targetAgent != null &&
            globalLiveMap.TryGetValue(targetAgent.Id, out var targetGlobalLive) &&
            (targetGlobalLive.Status == "error" || targetGlobalLive.WorkflowPhase == "awaiting_user_choice") &&
            !string.IsNullOrWhiteSpace(targetGlobalLive.ProjectRoot))
        {
            var matchedProj = _reg.UserConfig.Projects.FirstOrDefault(p =>
                string.Equals(p.Id, targetGlobalLive.ProjectId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.PrimaryRoot?.TrimEnd('\\', '/'), targetGlobalLive.ProjectRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (matchedProj != null) proj = matchedProj;
        }
        if (proj == null) return;

        var cp = NetworkResilienceService.LoadInterruptedCheckpoint(proj);
        var liveMap = AutoDispatchService.ReadLiveWorkStatesForProject(proj);
        var agents = _reg.UserConfig.Agents;

        var stuckAgent = targetAgent;
        if (stuckAgent == null && cp != null && !string.IsNullOrWhiteSpace(cp.TargetAgentId))
        {
            stuckAgent = agents.FirstOrDefault(a => a.Id.Equals(cp.TargetAgentId, StringComparison.OrdinalIgnoreCase));
        }
        if (stuckAgent == null)
        {
            var errLive = liveMap.Values.FirstOrDefault(v => v.Status == "error" || v.WorkflowPhase == "awaiting_user_choice");
            if (errLive != null)
            {
                stuckAgent = agents.FirstOrDefault(a => a.Id.Equals(errLive.AgentId, StringComparison.OrdinalIgnoreCase));
            }
        }
        stuckAgent ??= agents.FirstOrDefault(a => a.Enabled && !a.Id.Equals("antigravity", StringComparison.OrdinalIgnoreCase)) ?? agents.FirstOrDefault();
        if (stuckAgent == null) return;

        liveMap.TryGetValue(stuckAgent.Id, out var agLive);
        var effectiveOwner = dialogOwner ?? (IsVisible ? this : null);
        var choice = Dialogs.ShowStallResolutionDialog(effectiveOwner, _reg, proj, stuckAgent, agLive, cp);
        if (choice == null || string.IsNullOrWhiteSpace(choice.ActionKind))
        {
            RenderAppSettingsPage();
            return;
        }

        RenderAppSettingsPage();
        string taskText = !string.IsNullOrWhiteSpace(cp?.TaskInstruction)
            ? cp!.TaskInstruction
            : (agLive?.TaskSummary ?? "");
        if (choice.CopyTaskToClipboard && !string.IsNullOrWhiteSpace(taskText))
        {
            try { Clipboard.SetText(taskText); } catch { }
        }

        bool isLongRunning = choice.ActionKind is "retry_clean_dialog" or "retry_pure_cli" or "switch_other_agent";
        if (isLongRunning && (_isResumingCheckpointInBackground || !LongActionTracker.TryBegin(LongActionTracker.StallResolve, "处理中")))
        {
            SetStatus("后台正在执行恢复任务，请稍候...");
            return;
        }
        if (isLongRunning)
        {
            _isResumingCheckpointInBackground = true;
            LongActionTracker.Arm(BtnChooseCheckpointResolution, LongActionTracker.StallResolve, "🛠 选择解决方式");
            LongActionTracker.ShowBusy(BtnChooseCheckpointResolution);
            if (InterruptedCheckpointBanner != null) InterruptedCheckpointBanner.Visibility = Visibility.Collapsed;
        }

        var stallSw = System.Diagnostics.Stopwatch.StartNew();
        SetStatus($"🛠 正在后台执行您选择的解决方式 ({choice.ActionKind})，全程零鼠标键盘抢占...");
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var res = _autoDispatch.ExecuteUserChosenResolution(
                    _reg,
                    proj,
                    _bus,
                    stuckAgent,
                    choice.ActionKind,
                    choice.TargetAgentId,
                    taskText,
                    _reg.UserConfig.SingleAttemptTimeoutSeconds);

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _isResumingCheckpointInBackground = false;
                        _lastOverviewSignature = "";
                        RefreshAllViews();
                        _floatingWindow?.RefreshOverlayData(force: true);
                        int sec = isLongRunning ? Math.Max(0, (int)stallSw.Elapsed.TotalSeconds) : 0;
                        SetStatus(res.Success
                            ? $"✅ 已完成您选择的解决动作：{res.SummaryMessage}{(isLongRunning ? $"（耗时 {sec}s）" : "")}"
                            : $"⚠ 执行后仍需关注：{res.SummaryMessage}{(isLongRunning ? $"（耗时 {sec}s）" : "")}");
                    }
                    finally
                    {
                        if (isLongRunning) EndLongAction(LongActionTracker.StallResolve);
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _isResumingCheckpointInBackground = false;
                        _lastOverviewSignature = "";
                        RefreshAllViews();
                        _floatingWindow?.RefreshOverlayData(force: true);
                        SetStatus($"⚠ 执行解决动作出错：{TruncateStatus(ex.Message)}（耗时 {(int)stallSw.Elapsed.TotalSeconds}s）");
                    }
                    finally
                    {
                        if (isLongRunning) EndLongAction(LongActionTracker.StallResolve);
                    }
                });
            }
        });
    }

    private void BtnProbeNetworkNow_Click(object sender, RoutedEventArgs e)
    {
        if (!LongActionTracker.TryBegin(LongActionTracker.NetProbe, "检测中"))
        {
            SetStatus(LongActionTracker.DuplicateClickHint(LongActionTracker.NetProbe));
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LongActionTracker.ShowBusy(BtnProbeNetworkNow);
        SetStatus("📡 正在探测本机网卡、DNS 网关与云端 AI 模型库连通性...");
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            bool online = false;
            string detail = "";
            Exception? error = null;
            try
            {
                (online, detail) = NetworkResilienceService.ProbeLocalNetworkHealth(900);
                _lastKnownNetOnline = online;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    int sec = Math.Max(0, (int)sw.Elapsed.TotalSeconds);
                    if (error != null)
                    {
                        SetStatus($"⚠ 网络检测失败：{TruncateStatus(error.Message)}（耗时 {sec}s）");
                        return;
                    }
                    UpdateNetworkHealthBadgeUi(online, detail);
                    SetStatus(online
                        ? $"✓ 网络检测通过：{detail}（自动断线重连与断点保护正常待命，耗时 {sec}s）"
                        : $"⚠ 检测到本机网络异常：{detail}（派单时将自动挂起等待重连并保护任务链断点，耗时 {sec}s）");
                }
                finally
                {
                    EndLongAction(LongActionTracker.NetProbe);
                }
            });
        });
    }

    public void TriggerResumeCheckpointFromOverlay()
    {
        TriggerResumeCheckpointAsync(autoTriggered: false);
    }

    private void BtnResumeCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        TriggerResumeCheckpointAsync(autoTriggered: false);
    }

    private void BtnDismissCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        var proj = _reg.GetActiveProject();
        NetworkResilienceService.ClearInterruptedCheckpoint(proj);
        RenderOverviewPage();
        _floatingWindow?.RefreshOverlayData(force: true);
        SetStatus("已清除暂停的任务链断点记录。");
    }

    private void TriggerResumeCheckpointAsync(bool autoTriggered)
    {
        var proj = _reg.GetActiveProject();
        if (proj == null) return;

        var cp = NetworkResilienceService.LoadInterruptedCheckpoint(proj);
        if (cp == null)
        {
            SetStatus("当前项目没有待续跑的断线任务链断点。");
            return;
        }
        if (_isResumingCheckpointInBackground || !LongActionTracker.TryBegin(LongActionTracker.ResumeCheckpoint, "续跑中"))
        {
            SetStatus("断点任务链正在后台重连续跑中，请稍候...");
            return;
        }

        _isResumingCheckpointInBackground = true;
        LongActionTracker.ShowBusy(BtnResumeCheckpoint);
        if (InterruptedCheckpointBanner != null) InterruptedCheckpointBanner.Visibility = Visibility.Collapsed;
        var resumeSw = System.Diagnostics.Stopwatch.StartNew();
        SetStatus(autoTriggered
            ? $"🌐 检测到网络已恢复，正在自动从第 {cp.CurrentRound}/{cp.MaxRounds} 轮断点续跑「{cp.TargetAgentName}」任务链..."
            : $"↻ 正在从第 {cp.CurrentRound}/{cp.MaxRounds} 轮断点重连续跑「{cp.TargetAgentName}」任务链...");

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var res = _autoDispatch.ResumeInterruptedTaskChain(_reg, proj, _bus, _reg.UserConfig.SingleAttemptTimeoutSeconds);
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _isResumingCheckpointInBackground = false;
                        RefreshAllViews();
                        _floatingWindow?.RefreshOverlayData(force: true);
                        int sec = Math.Max(0, (int)resumeSw.Elapsed.TotalSeconds);
                        SetStatus(res.Success
                            ? $"✅ 断点续跑完成并通过主脑验收：{res.SummaryMessage}（耗时 {sec}s）"
                            : $"⚠ 断点续跑再次遇到网络异常，断点已继续保留：{res.SummaryMessage}（耗时 {sec}s）");
                    }
                    finally
                    {
                        EndLongAction(LongActionTracker.ResumeCheckpoint);
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _isResumingCheckpointInBackground = false;
                        RefreshAllViews();
                        SetStatus($"⚠ 断点续跑执行出错：{TruncateStatus(ex.Message)}（耗时 {(int)resumeSw.Elapsed.TotalSeconds}s）");
                    }
                    finally
                    {
                        EndLongAction(LongActionTracker.ResumeCheckpoint);
                    }
                });
            }
        });
    }
}
