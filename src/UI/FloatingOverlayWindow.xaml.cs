using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AICollabHub.Models;
using AICollabHub.Services;

namespace AICollabHub.UI;

public partial class FloatingOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly RegistryService _reg;
    private readonly MainWindow _mainWin;
    private readonly AutoDispatchService _autoDispatch = new();

    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _fastRefreshDebounce;
    private FileSystemWatcher? _liveMapWatcher;
    private string _lastStateSignature = "";
    private string _lastRowStructureSig = "";
    private readonly Dictionary<string, string> _rowStructSigs = new(StringComparer.OrdinalIgnoreCase);
    private bool _appExitShutdown;
    private bool _lastDiskFloatingCollapsed;
    private int _desktopBusyRefreshGate;
    private long _lastDesktopBusyRefreshTicks;

    public FloatingOverlayWindow(RegistryService reg, MainWindow mainWin)
    {
        _reg = reg;
        _mainWin = mainWin;
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNativeRoundedCornersAndAlpha();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ResolveOverlayFallbackPollMs(_reg.UserConfig.FloatingRefreshIntervalMs))
        };
        _fastRefreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };

        SizeChanged += (_, _) =>
        {
            if (!_sizeDragActive)
            {
                ApplyAdaptiveContentLayout();
            }
        };
        LocationChanged += (_, _) =>
        {
            if (VerifyCelebrationPopup?.IsOpen == true)
            {
                double cur = VerifyCelebrationPopup.HorizontalOffset;
                VerifyCelebrationPopup.HorizontalOffset = cur + 0.01;
                VerifyCelebrationPopup.HorizontalOffset = cur;
            }
        };

        if (HeaderLegendDots != null)
        {
            HeaderLegendDots.MouseLeftButtonDown += HeaderLegendDots_MouseLeftButtonDown;
        }

        if (VerifyCelebrationPopup != null)
            VerifyCelebrationPopup.CustomPopupPlacementCallback = PlaceVerifyCelebrationBubble;

        ApplySavedPositionAndAppearance();
        SyncQuickSoundButtonUi();
        SoundFxService.ApplySoundPreference(_reg.UserConfig);
        RefreshOverlayData(force: true);

        _fastRefreshDebounce.Tick += (_, _) =>
        {
            _fastRefreshDebounce.Stop();
            if (IsVisible) RefreshOverlayData(force: true);
        };
        _pollTimer.Tick += async (_, _) =>
        {
            if (!IsVisible) return;
            RefreshOverlayData(force: false);
            try { await TriggerDesktopBusyRefreshAsync().ConfigureAwait(true); }
            catch { }
        };
        if (!AppPaths.IsIsolatedTest)
        {
            StartLiveStateMapWatcher();
            AutoDispatchService.DesktopLiveProbeChanged += OnDesktopLiveProbeChanged;
            _pollTimer.Start();
        }
        Closing += (_, e) =>
        {
            // 应用退出收口时绕过任何取消拦截，确保悬浮窗真正关闭
            if (_appExitShutdown) e.Cancel = false;
        };
        Closed += (_, _) =>
        {
            DisposeLiveSyncHooks();
            try { SoundFxService.StopAll(); } catch { }
            try { if (VerifyCelebrationPopup != null) VerifyCelebrationPopup.IsOpen = false; } catch { }
            try { if (OpacityPopup != null) OpacityPopup.IsOpen = false; } catch { }
        };
    }

    /// <summary>默认兜底 1000ms；用户自定义更大或更小值仍夹在 500~10000。</summary>
    internal static int ResolveOverlayFallbackPollMs(int configured)
    {
        if (configured <= 0) return 1000;
        return Math.Clamp(Math.Min(configured, 1000), 500, 10000);
    }

    /// <summary>异步桌面忙碌探测：防重入，间隔 >= DesktopLiveProbeCacheTtlMs，变化时立即刷新悬浮窗。</summary>
    private async Task TriggerDesktopBusyRefreshAsync()
    {
        if (AppPaths.IsIsolatedTest || !IsVisible) return;
        long nowTicks = Environment.TickCount64;
        if (nowTicks - _lastDesktopBusyRefreshTicks < AutoDispatchService.DesktopLiveProbeCacheTtlMs)
            return;
        if (System.Threading.Interlocked.CompareExchange(ref _desktopBusyRefreshGate, 1, 0) != 0)
            return;
        try
        {
            _lastDesktopBusyRefreshTicks = nowTicks;
            var agents = _reg.UserConfig.Agents;
            bool changed = await AutoDispatchService.RefreshAllDesktopAgentsBusySilent(agents).ConfigureAwait(true);
            if (changed && IsVisible && !Dispatcher.HasShutdownStarted)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { if (IsVisible) RefreshOverlayData(force: true); } catch { }
                }));
            }
        }
        catch { }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _desktopBusyRefreshGate, 0);
        }
    }

    private void StartLiveStateMapWatcher()
    {
        try
        {
            string path = AutoDispatchService.LiveStateMapPath;
            string? dir = System.IO.Path.GetDirectoryName(path);
            string file = System.IO.Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(file)) return;
            Directory.CreateDirectory(dir);
            _liveMapWatcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _liveMapWatcher.Changed += OnLiveMapFileEvent;
            _liveMapWatcher.Created += OnLiveMapFileEvent;
            _liveMapWatcher.Renamed += OnLiveMapFileEvent;
            _liveMapWatcher.Deleted += OnLiveMapFileEvent;
        }
        catch { }
    }

    private void OnLiveMapFileEvent(object sender, FileSystemEventArgs e) => ScheduleFastOverlayRefresh();

    private void OnDesktopLiveProbeChanged() => ScheduleFastOverlayRefresh();

    private void ScheduleFastOverlayRefresh()
    {
        try
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() =>
            {
                _fastRefreshDebounce.Stop();
                _fastRefreshDebounce.Start();
            });
        }
        catch { }
    }

    private void DisposeLiveSyncHooks()
    {
        try { _pollTimer.Stop(); } catch { }
        try { _fastRefreshDebounce.Stop(); } catch { }
        try { _verifyBannerTimer?.Stop(); } catch { }
        try { AutoDispatchService.DesktopLiveProbeChanged -= OnDesktopLiveProbeChanged; } catch { }
        if (_liveMapWatcher != null)
        {
            try { _liveMapWatcher.EnableRaisingEvents = false; } catch { }
            try { _liveMapWatcher.Changed -= OnLiveMapFileEvent; } catch { }
            try { _liveMapWatcher.Created -= OnLiveMapFileEvent; } catch { }
            try { _liveMapWatcher.Renamed -= OnLiveMapFileEvent; } catch { }
            try { _liveMapWatcher.Deleted -= OnLiveMapFileEvent; } catch { }
            try { _liveMapWatcher.Dispose(); } catch { }
            _liveMapWatcher = null;
        }
    }

    /// <summary>应用退出时彻底关闭悬浮窗：停轮询、关 Popup、绕过拦截并 Close。</summary>
    public void ShutdownForAppExit()
    {
        _appExitShutdown = true;
        DisposeLiveSyncHooks();
        try { SoundFxService.StopAll(); } catch { }
        try { if (OpacityPopup != null) OpacityPopup.IsOpen = false; } catch { }
        try { if (VerifyCelebrationPopup != null) VerifyCelebrationPopup.IsOpen = false; } catch { }
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() =>
                {
                    try { Close(); } catch { }
                });
            }
            else
            {
                Close();
            }
        }
        catch { }
    }

    private static Brush Hex(string h) => UiBrushPool.Get(h);

    public void ApplyUserPreferencesFromSettings()
    {
        Topmost = _reg.UserConfig.FloatingWindowTopmost;
        int interval = ResolveOverlayFallbackPollMs(_reg.UserConfig.FloatingRefreshIntervalMs);
        if ((int)_pollTimer.Interval.TotalMilliseconds != interval)
        {
            _pollTimer.Interval = TimeSpan.FromMilliseconds(interval);
        }

        ApplyTranslucentVisual();
        ApplyCollapsedVisual(_reg.UserConfig.FloatingWindowCollapsed, preserveRightAnchor: true);
        _lastDiskFloatingCollapsed = _reg.UserConfig.FloatingWindowCollapsed;
        RefreshOverlayData(force: true);
    }

    public void ResetPositionToTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        double w = _reg.UserConfig.FloatingWindowCollapsed ? ResolveCollapsedWidth() : ResolveExpandedWidth();
        if (!_reg.UserConfig.FloatingWindowCollapsed)
        {
            ApplyExpandedHeightMode();
        }
        ApplyTargetWindowWidth(w, preserveRightAnchor: false);
        Left = Math.Max(workArea.Left + 12, workArea.Right - w - 16);
        Top = workArea.Top + 42;
        ClampWindowInsideWorkArea();
        _reg.UserConfig.FloatingWindowLeft = Left;
        _reg.UserConfig.FloatingWindowTop = Top;
        _reg.SaveUserConfig();
    }

    private void ApplySavedPositionAndAppearance()
    {
        if (!AppPaths.IsIsolatedTest)
        {
            _reg.UserConfig.FloatingCustomWidth = null;
            _reg.UserConfig.FloatingUltraCustomWidth = null;
            _reg.UserConfig.FloatingCustomHeight = null;
            _reg.UserConfig.FloatingCollapsedCustomWidth = null;
        }
        Topmost = _reg.UserConfig.FloatingWindowTopmost;
        ApplyTranslucentVisual();
        ApplyCollapsedVisual(_reg.UserConfig.FloatingWindowCollapsed, preserveRightAnchor: false);

        var primaryWa = SystemParameters.WorkArea;
        double currentW = Width;
        double defaultLeft = Math.Max(primaryWa.Left + 12, primaryWa.Right - currentW - 16);
        double defaultTop = primaryWa.Top + 42;

        double targetLeft = _reg.UserConfig.FloatingWindowLeft ?? defaultLeft;
        double targetTop = _reg.UserConfig.FloatingWindowTop ?? defaultTop;

        double vLeft = SystemParameters.VirtualScreenLeft;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vTop = SystemParameters.VirtualScreenTop;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        if (targetLeft < vLeft - 20 || targetLeft > vRight - 80)
            targetLeft = defaultLeft;
        if (targetTop < vTop - 10 || targetTop > vBottom - 60)
            targetTop = defaultTop;

        Left = targetLeft;
        Top = targetTop;
        ClampWindowInsideWorkArea();
        _lastDiskFloatingCollapsed = _reg.UserConfig.FloatingWindowCollapsed;
    }

    private bool _suppressOpacitySliderEvent;

    private void ApplyNativeRoundedCornersAndAlpha()
    {
        ApplyTranslucentVisual();
    }

    private void ApplyTranslucentVisual()
    {
        int pct = Math.Clamp(_reg.UserConfig.FloatingOpacityPercent, 25, 100);
        double op = pct / 100.0;
        OuterFrame.Opacity = 1.0;
        Opacity = op;

        bool isTranslucent = pct < 92;
        _reg.UserConfig.FloatingWindowTranslucent = isTranslucent;
        TranslucentBtn.Content = "◐";
        TranslucentBtn.ToolTip = $"手动调节透明度（当前 {pct}% · 点击弹出纵向滑块，上下拖动实时生效，点击外部自动保存关闭）";
        TranslucentBtn.Foreground = isTranslucent ? Hex("#38BDF8") : Hex("#CBD5E1");
        if (CapsuleOpacityBtn != null)
        {
            CapsuleOpacityBtn.ToolTip = $"手动调节透明度（当前 {pct}% · 点击弹出纵向滑块）";
            CapsuleOpacityBtn.Foreground = isTranslucent ? Hex("#38BDF8") : Hex("#CBD5E1");
        }

        _suppressOpacitySliderEvent = true;
        try
        {
            if (InlineOpacitySlider != null && Math.Abs(InlineOpacitySlider.Value - pct) > 0.5)
            {
                InlineOpacitySlider.Value = pct;
            }
            if (InlineOpacityValueText != null)
            {
                InlineOpacityValueText.Text = $"{pct}%";
            }
            if (PreviewOpacitySlider != null && Math.Abs(PreviewOpacitySlider.Value - pct) > 0.5)
            {
                PreviewOpacitySlider.Value = pct;
            }
            if (PreviewOpacityValueText != null)
            {
                PreviewOpacityValueText.Text = $"{pct}%";
            }
        }
        finally
        {
            _suppressOpacitySliderEvent = false;
        }
        SyncQuickSoundButtonUi();
    }

    public void SetOpacityPercentManual(int pct, bool saveToDisk = true)
    {
        int clamped = Math.Clamp(pct, 25, 100);
        _reg.UserConfig.FloatingOpacityPercent = clamped;
        _reg.UserConfig.FloatingWindowTranslucent = clamped < 92;
        ApplyTranslucentVisual();
        if (saveToDisk)
        {
            _reg.SaveUserConfig();
        }
        _mainWin.SyncAppSettingsControlsIfLoaded();
    }

    public void ApplyCollapsedVisual(bool collapsed, bool preserveRightAnchor = true)
    {
        double oldRight = Left + (ActualWidth > 40 ? ActualWidth : Width);
        double targetW = collapsed ? ResolveCollapsedWidth() : ResolveExpandedWidth();

        CollapsedMiniCapsule.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        ExpandedFullContainer.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (collapsed)
        {
            OuterFrame.CornerRadius = new CornerRadius(16);
            OuterFrame.Padding = new Thickness(5, 3, 5, 3);
        }
        else
        {
            // 先按配置档位预设，随后 ApplyAdaptiveContentLayout 按实际 _compactMode 精确对齐
            switch (PeekExpandedCompactMode())
            {
                case FloatingCompactMode.Ultra:
                    OuterFrame.Padding = new Thickness(5, 4, 5, 4);
                    OuterFrame.CornerRadius = new CornerRadius(10);
                    break;
                case FloatingCompactMode.Compact:
                    OuterFrame.Padding = new Thickness(6, 6, 6, 6);
                    OuterFrame.CornerRadius = new CornerRadius(11);
                    break;
                default:
                    OuterFrame.Padding = new Thickness(8, 8, 8, 8);
                    OuterFrame.CornerRadius = new CornerRadius(12);
                    break;
            }
        }

        // 收起态与展开态高度自适应控制
        if (collapsed)
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            MinHeight = 0;
            MaxHeight = double.PositiveInfinity;
            Height = double.NaN;
            if (ResizeThumbBottom != null) ResizeThumbBottom.Visibility = Visibility.Collapsed;
            if (ResizeThumbCorner != null) ResizeThumbCorner.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (ResizeThumbBottom != null) ResizeThumbBottom.Visibility = Visibility.Collapsed;
            if (ResizeThumbCorner != null) ResizeThumbCorner.Visibility = Visibility.Collapsed;
            ApplyExpandedHeightMode();
        }

        ApplyTargetWindowWidth(targetW, preserveRightAnchor, oldRight, autoHeight: collapsed || _reg.UserConfig.FloatingCustomHeight is not double ch || ch <= 0);
        ClampWindowInsideWorkArea();
        ApplyAdaptiveContentLayout();
    }

    public void RefreshOverlayData(bool force = false)
    {
        if (_reg.ReloadUserConfigFromDiskIfNeeded())
        {
            // 热重载：仅当磁盘收起开关相对上次已读值真正变化时才跟随；
            // 状态轮询/派单刷新绝不可用运行时视觉态回写配置，也绝不可因任务态自动改形态。
            bool diskCollapsed = _reg.UserConfig.FloatingWindowCollapsed;
            if (diskCollapsed != _lastDiskFloatingCollapsed)
            {
                _lastDiskFloatingCollapsed = diskCollapsed;
                ApplyCollapsedVisual(diskCollapsed, preserveRightAnchor: true);
            }

            Topmost = _reg.UserConfig.FloatingWindowTopmost;
            ApplyTranslucentVisual();
            SyncQuickSoundButtonUi();
            // 热重载后立即按磁盘最新静音开关硬切断（含工作循环音），禁止仅改图标不落停播
            SoundFxService.ApplySoundPreference(_reg.UserConfig);
            EnsureDynamicMinHeight();
            SyncLayoutModeButtonUi();
        }

        var liveMap = AutoDispatchService.ReadMergedGlobalWorkStatesWithDesktopLive();
        var proj = ResolveMostActiveOverlayProject(liveMap);
        var enabledAgents = _reg.UserConfig.Agents.Where(a => a.Enabled).ToList();

        var activeTp = _reg.UserConfig.TeamProfiles?.FirstOrDefault(p =>
            string.Equals(p.Id, _reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase));
        string policyDisplayName = activeTp != null
            ? activeTp.Name
            : (_reg.UserConfig.MaxReworkRounds <= 1 ? "省主脑额度模式" : "常规均衡模式");

        var sigBuilder = new StringBuilder();
        sigBuilder.Append(proj?.Id).Append('|').Append(_reg.UserConfig.FloatingWindowCollapsed)
                  .Append('|').Append(_reg.UserConfig.WorkflowEngineEnabled)
                  .Append('|').Append(_reg.UserConfig.FloatingThemeStyle)
                  .Append('|').Append(_reg.UserConfig.ActiveTeamProfileId)
                  .Append('|').Append(policyDisplayName)
                  .Append('|').Append(_reg.UserConfig.MaxReworkRounds).Append('|');

        var winStates = new Dictionary<string, (bool IsRunning, int Pid, string WindowBadge)>();
        foreach (var ag in enabledAgents)
        {
            var ws = _mainWin.GetCachedWindowState(ag);
            winStates[ag.Id] = ws;
            sigBuilder.Append(ag.Id).Append(':').Append(ws.IsRunning).Append(':').Append(ws.Pid).Append(';');
            if (liveMap.TryGetValue(ag.Id, out var lv))
            {
                sigBuilder.Append(lv.Status).Append(':').Append(lv.WorkflowPhase).Append(':').Append(lv.LastFailureBadge).Append(':').Append(lv.SessionId).Append(':').Append(lv.UpdatedAt)
                          .Append(':').Append(lv.TaskSummary).Append(':').Append(lv.LatestLogTail?.Length ?? 0).Append(';');
            }
        }

        var currentSig = sigBuilder.ToString();
        if (!force && currentSig == _lastStateSignature)
        {
            return;
        }
        _lastStateSignature = currentSig;

        ApplyThemeVisual();
        bool wfOn = _reg.UserConfig.WorkflowEngineEnabled;
        if (HeaderWorkflowBtn != null)
        {
            HeaderWorkflowBtn.Content = wfOn ? "⚡开" : "○关";
            HeaderWorkflowBtn.Foreground = wfOn ? Hex("#4ADE80") : Hex("#94A3B8");
            HeaderWorkflowBtn.ToolTip = wfOn
                ? "协同工作流已开启（点击可停用并恢复各 AI 独立模式）"
                : "协同工作流已停用 · 独立模式（点击可重新开启多 AI 协同）";
        }

        var activeWorkingState = liveMap.Values.FirstOrDefault(v => (v.Status == "working" || v.Status == "reworking") && !string.IsNullOrWhiteSpace(v.ProjectName));
        if (HeaderProjBadgeHost != null)
        {
            if (activeWorkingState != null)
            {
                HeaderProjBadge.Text = activeWorkingState.ProjectName.Split(' ')[0];
                HeaderProjBadgeHost.Visibility = Visibility.Visible;
            }
            else
            {
                HeaderProjBadge.Text = "";
                HeaderProjBadgeHost.Visibility = Visibility.Collapsed;
            }
        }
        SyncLayoutModeButtonUi();
        if (HeaderPolicyText != null && HeaderPolicyMetaText != null && ActivePolicyStrip != null)
        {
            HeaderPolicyText.Text = policyDisplayName;
            string masterShort = ShortAgentName(RoutingService.ResolveMasterPlanner(_reg, proj)?.Name ?? "Antigravity");
            HeaderPolicyMetaText.Text = $"主脑 {masterShort} · ≤{_reg.UserConfig.MaxReworkRounds + 1}轮";
            ActivePolicyStrip.ToolTip = activeTp != null
                ? $"当前生效总策略：{activeTp.Name}\n{activeTp.Description}\n（全工程通用，可在主界面「AI 阵容与路由」顶部切换）"
                : $"当前生效总策略：{policyDisplayName}（全工程通用）";
        }
        var workingEntries = liveMap.Values.Where(v => v.Status == "working" && v.AgentId != "antigravity").ToList();
        int errorCount = liveMap.Values.Count(v => v.Status == "error" && v.AgentId != "antigravity");
        bool anyWorking = wfOn && workingEntries.Count > 0;

        HeaderPulseDot.Fill = !wfOn ? Hex("#64748B") : (anyWorking ? Hex("#F59E0B") : (errorCount > 0 ? Hex("#EF4444") : Hex("#10B981")));

        // 形态绝对锁定：idle/working/done/error/多项目/IPC/定时刷新均不得自动展开、收起或互切；
        // FloatingAutoExpandOnWorking / FloatingAutoCollapseOnDone 永久失效，形态仅用户手动切换或启动读盘。

        // 1. 渲染收起态：一排小头像 + 最新一句话动态
        RenderCollapsedCapsule(enabledAgents, liveMap, winStates, workingEntries);

        // 双保险不变量 1：配置标志与控件显隐不一致时，先对齐视觉再调宽
        bool visualCollapsedNow = CollapsedMiniCapsule.Visibility == Visibility.Visible;
        if (visualCollapsedNow != _reg.UserConfig.FloatingWindowCollapsed)
        {
            ApplyCollapsedVisual(_reg.UserConfig.FloatingWindowCollapsed, preserveRightAnchor: true);
        }

        // 2. 渲染展开态：按布局档位与结构签名增量更新，避免全清重建导致的动画卡顿与微标重播
        EnsureDynamicMinHeight();
        if (!_reg.UserConfig.FloatingWindowCollapsed && !_sizeDragActive)
        {
            double wantW = ResolveExpandedWidth();
            if (IsWindowWidthOff(wantW))
                SyncExpandedWidthToCurrentMode(preserveRightAnchor: true);
        }
        else if (_reg.UserConfig.FloatingWindowCollapsed && !_sizeDragActive)
        {
            double wantW = ResolveCollapsedWidth();
            if (IsWindowWidthOff(wantW))
            {
                ApplyTargetWindowWidth(wantW, preserveRightAnchor: true);
                ClampWindowInsideWorkArea();
            }
        }
        var activeStructSig = $"{(int)_compactMode}|{wfOn}|{_reg.UserConfig.FloatingThemeStyle}|{string.Join(",", enabledAgents.Select(a => a.Id))}";
        bool needFullRowRebuild = force
            || !string.Equals(activeStructSig, _lastRowStructureSig, StringComparison.Ordinal)
            || FloatingAgentsStack.Children.Count != enabledAgents.Count;
        bool anyRowReplaced = false;

        if (needFullRowRebuild)
        {
            FloatingAgentsStack.Children.Clear();
            _rowStructSigs.Clear();
            for (int idx = 0; idx < enabledAgents.Count; idx++)
            {
                var ag = enabledAgents[idx];
                bool isLast = idx == enabledAgents.Count - 1;
                liveMap.TryGetValue(ag.Id, out var agLive);
                if (!winStates.TryGetValue(ag.Id, out var winSt))
                {
                    winSt = _mainWin.GetCachedWindowState(ag);
                }
                var rich = ResolveRichStatus(ag, agLive, winSt.IsRunning, liveMap);
                _rowStructSigs[ag.Id] = BuildRowStructuralSignature(ag, agLive, winSt, rich, isLast);
                FloatingAgentsStack.Children.Add(BuildCompactAvatarStatusRow(ag, proj, agLive, winSt, liveMap, rich, isLast));
            }
            _lastRowStructureSig = activeStructSig;
            anyRowReplaced = true;
        }
        else
        {
            for (int i = 0; i < enabledAgents.Count; i++)
            {
                var ag = enabledAgents[i];
                bool isLast = i == enabledAgents.Count - 1;
                liveMap.TryGetValue(ag.Id, out var agLive);
                if (!winStates.TryGetValue(ag.Id, out var winSt))
                {
                    winSt = _mainWin.GetCachedWindowState(ag);
                }
                var rich = ResolveRichStatus(ag, agLive, winSt.IsRunning, liveMap);
                var nextRowSig = BuildRowStructuralSignature(ag, agLive, winSt, rich, isLast);

                if (_rowStructSigs.TryGetValue(ag.Id, out var prevRowSig)
                    && string.Equals(prevRowSig, nextRowSig, StringComparison.Ordinal)
                    && FloatingAgentsStack.Children[i] is Border existingRow)
                {
                    UpdateRowDynamicContentInPlace(existingRow, ag, agLive, winSt, rich);
                }
                else
                {
                    _rowStructSigs[ag.Id] = nextRowSig;
                    FloatingAgentsStack.Children.RemoveAt(i);
                    FloatingAgentsStack.Children.Insert(i, BuildCompactAvatarStatusRow(ag, proj, agLive, winSt, liveMap, rich, isLast));
                    anyRowReplaced = true;
                }
            }
        }

        if (anyRowReplaced)
        {
            UpdateLayout();
            ApplyAdaptiveContentLayout();
        }
        DetectAndPlayInteractionFx(enabledAgents, liveMap);
    }

    private ProjectProfile? ResolveMostActiveOverlayProject(Dictionary<string, LiveAgentWorkState> allStates)
    {
        var fallbackProj = _reg.GetActiveProject();
        try
        {
            // 1. 优先展示当前正在 working / reworking 的项目（复用已读内存字典，零重复磁盘读取）
            var workingEntry = allStates.Values
                .Where(v => (v.Status == "working" || v.Status == "reworking") && v.UpdatedUtcTicks > 0)
                .OrderByDescending(v => v.UpdatedUtcTicks)
                .FirstOrDefault();
            if (workingEntry != null)
            {
                var matched = _reg.UserConfig.Projects.FirstOrDefault(p =>
                    (!string.IsNullOrWhiteSpace(workingEntry.ProjectId) && string.Equals(p.Id, workingEntry.ProjectId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(workingEntry.ProjectRoot) && string.Equals(p.PrimaryRoot, workingEntry.ProjectRoot, StringComparison.OrdinalIgnoreCase)));
                if (matched != null) return matched;
            }
        }
        catch { }
        return fallbackProj;
    }

    private void RenderCollapsedCapsule(
        List<AgentRosterEntry> enabledAgents,
        Dictionary<string, LiveAgentWorkState> liveMap,
        Dictionary<string, (bool IsRunning, int Pid, string WindowBadge)> winStates,
        List<LiveAgentWorkState> workingEntries)
    {
        CapsuleAvatarsPanel.Children.Clear();
        FloatingRichStatus? briefRich = null;
        var tipLines = new List<string>();
        for (int idx = 0; idx < enabledAgents.Count; idx++)
        {
            var ag = enabledAgents[idx];
            liveMap.TryGetValue(ag.Id, out var lv);
            winStates.TryGetValue(ag.Id, out var ws);
            var rich = ResolveRichStatus(ag, lv, ws.IsRunning, liveMap);
            CapsuleAvatarsPanel.Children.Add(BuildRichAvatarWithFx(ag, 20, 6.5, rich,
                marginRight: idx == enabledAgents.Count - 1 ? 0 : 2));
            if (rich.IsActiveWork && briefRich == null) briefRich = rich;
            tipLines.Add($"{ShortAgentName(ag.Name)} · {rich.BadgeText} · {rich.StatusLine}");
        }

        string briefText;
        Brush briefFg;
        if (!_reg.UserConfig.WorkflowEngineEnabled)
        {
            briefText = "协同已关闭 · 各 AI 独立运行中";
            briefFg = Hex("#94A3B8");
        }
        else if (workingEntries.Count > 0)
        {
            var w = workingEntries[0];
            string shortAg = ShortAgentName(w.AgentName);
            string taskTitle = ExtractConciseTaskTitle(w.TaskSummary);
            var agEntry = enabledAgents.FirstOrDefault(a => a.Id == w.AgentId);
            var rich = agEntry != null
                ? ResolveRichStatus(agEntry, w, true, liveMap)
                : ResolveWorkerRich(w, true);
            briefText = $"{shortAg} · {CompactCapsuleStage(rich.Kind)}「{taskTitle}」";
            briefFg = Hex(rich.TextHex);
        }
        else
        {
            var latestDone = liveMap.Values
                .Where(v => (v.Status == "done" || v.WorkflowPhase == "verified_delivered") && v.AgentId != "antigravity")
                .OrderByDescending(v => v.UpdatedAt)
                .FirstOrDefault();
            if (latestDone != null)
            {
                string shortAg = ShortAgentName(latestDone.AgentName);
                string taskTitle = ExtractConciseTaskTitle(latestDone.TaskSummary);
                briefText = $"{shortAg} · ✅ 已交付「{taskTitle}」";
                briefFg = Hex("#86EFAC");
            }
            else if (briefRich != null)
            {
                briefText = $"进行中 · {CompactCapsuleStage(briefRich.Value.Kind)}";
                briefFg = Hex(briefRich.Value.TextHex);
            }
            else
            {
                briefText = "☕ 空闲待命 · 说话即自动分配";
                briefFg = Hex("#93C5FD");
            }
        }

        // 摘要仅挂 ToolTip，胶囊界面本身不显示任务文案
        if (CapsuleBriefText != null)
        {
            CapsuleBriefText.Text = briefText;
            CapsuleBriefText.Foreground = briefFg;
            CapsuleBriefText.Visibility = Visibility.Collapsed;
        }
        string capsuleTip = tipLines.Count > 0
            ? briefText + "\n\n" + string.Join("\n", tipLines)
            : briefText;
        CollapsedMiniCapsule.ToolTip = capsuleTip;
        ToolTipService.SetInitialShowDelay(CollapsedMiniCapsule, 280);
        ToolTipService.SetShowDuration(CollapsedMiniCapsule, 16000);

        // 未手动拖宽时按头像数动态收窄（须同时真的处于胶囊视觉态，避免展开态被压成窄宽）
        if (_reg.UserConfig.FloatingWindowCollapsed
            && CollapsedMiniCapsule.Visibility == Visibility.Visible
            && _reg.UserConfig.FloatingCollapsedCustomWidth == null)
        {
            double dynW = ResolveCollapsedWidth();
            if (IsWindowWidthOff(dynW))
            {
                ApplyTargetWindowWidth(dynW, preserveRightAnchor: true);
                ClampWindowInsideWorkArea();
            }
        }
    }

    private static string CompactCapsuleStage(FloatingRichFxKind kind) => kind switch
    {
        FloatingRichFxKind.Thinking => "💭 思考中",
        FloatingRichFxKind.Coding => "🔨 编码中",
        FloatingRichFxKind.Delivering => "📦 交付中",
        FloatingRichFxKind.MasterVerifying => "🔍 审查中",
        FloatingRichFxKind.Reworking => "🔧 精修中",
        FloatingRichFxKind.MasterAnalyzing => "🧠 分析中",
        FloatingRichFxKind.Dispatching => "⚡ 派单中",
        FloatingRichFxKind.VerifiedDelivered => "✅ 已交付",
        _ => "进行中"
    };

    private string BuildRowStructuralSignature(
        AgentRosterEntry ag,
        LiveAgentWorkState? agLive,
        (bool IsRunning, int Pid, string WindowBadge) winSt,
        FloatingRichStatus rich,
        bool isLastRow)
    {
        bool canResume = agLive != null && (agLive.WorkflowPhase == "interrupted_net" || agLive.CanResumeInterruptedChain);
        bool hasError = agLive != null && (agLive.Status == "error" || agLive.WorkflowPhase == "awaiting_user_choice");
        bool hasSession = agLive != null && !string.IsNullOrWhiteSpace(agLive.SessionId);
        return string.Join("|",
            ag.Id,
            (int)_compactMode,
            isLastRow,
            (int)rich.Kind,
            rich.BadgeText,
            rich.AccentHex,
            rich.BadgeBgHex,
            rich.IsActiveWork,
            winSt.IsRunning,
            ag.PreferredEffortTier,
            agLive?.SelectedEffortTier ?? "",
            agLive?.SelectedModel ?? "",
            canResume,
            hasError,
            hasSession);
    }

    private void UpdateRowDynamicContentInPlace(
        Border rowBorder,
        AgentRosterEntry ag,
        LiveAgentWorkState? agLive,
        (bool IsRunning, int Pid, string WindowBadge) winSt,
        FloatingRichStatus rich)
    {
        var statusTb = FindNamedDescendant<TextBlock>(rowBorder, "TaskStatusLine");
        if (statusTb != null)
        {
            statusTb.Text = rich.StatusLine;
            statusTb.Foreground = Hex(rich.TextHex);
        }
        var timeTb = FindNamedDescendant<TextBlock>(rowBorder, "UpdatedTimeText");
        if (timeTb != null && agLive != null)
        {
            timeTb.Text = agLive.UpdatedAt;
        }
        var badgeTb = FindNamedDescendant<TextBlock>(rowBorder, "BadgeLabelText");
        if (badgeTb != null)
        {
            badgeTb.Text = rich.BadgeText;
        }
        rowBorder.ToolTip = BuildRichTooltipCard(ag, agLive, winSt);
    }

    private UIElement BuildCompactAvatarStatusRow(
        AgentRosterEntry ag,
        ProjectProfile? proj,
        LiveAgentWorkState? agLive,
        (bool IsRunning, int Pid, string WindowBadge) winSt,
        Dictionary<string, LiveAgentWorkState> liveMap,
        FloatingRichStatus? precomputedRich = null,
        bool isLastRow = false)
    {
        var rich = precomputedRich ?? ResolveRichStatus(ag, agLive, winSt.IsRunning, liveMap);
        var pal = ThemeService.Resolve(_reg.UserConfig.FloatingThemeStyle);
        string statusLine = rich.StatusLine;
        string ringHex = rich.AccentHex;
        string textHex = rich.TextHex;
        bool isWorking = rich.IsActiveWork;

        var idleBrush = Hex(pal.FloatHeaderBorderHex);
        var activeBrush = Hex(ringHex);
        var compact = _compactMode;
        bool isUltra = compact == FloatingCompactMode.Ultra;
        bool isFull = compact == FloatingCompactMode.Full;
        double padY = isUltra ? 2 : (isFull ? 6 : 4);
        double padX = isUltra ? 5 : 8;
        var rowBorder = new Border
        {
            Tag = ag.Id,
            Background = Hex(isWorking ? pal.FloatCardActiveBgHex : pal.FloatCardIdleBgHex),
            BorderBrush = isWorking ? activeBrush : idleBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(isUltra ? 6 : 10),
            Padding = new Thickness(padX, padY, padX, padY),
            Margin = new Thickness(0, 0, 0, isLastRow ? 0 : (isUltra ? 2 : (isFull ? 5 : 3))),
            ToolTip = BuildRichTooltipCard(ag, agLive, winSt),
            ClipToBounds = true
        };
        rowBorder.MouseEnter += (_, _) =>
        {
            rowBorder.BorderBrush = Hex(rich.AccentHex);
            rowBorder.BorderThickness = new Thickness(1.4);
        };
        rowBorder.MouseLeave += (_, _) =>
        {
            rowBorder.BorderBrush = isWorking ? activeBrush : idleBrush;
            rowBorder.BorderThickness = new Thickness(1);
        };
        ToolTipService.SetInitialShowDelay(rowBorder, 320);
        ToolTipService.SetShowDuration(rowBorder, 15000);
        ToolTipService.SetPlacement(rowBorder, System.Windows.Controls.Primitives.PlacementMode.Left);
        ToolTipService.SetHorizontalOffset(rowBorder, -8);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 内嵌能量灯柱
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 头像
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 卡片内部左侧能量灯柱（圆角镶嵌，无毛刺）
        var energyBar = new Border
        {
            Width = isUltra ? 2.5 : 3.5,
            Height = isUltra ? 13 : (isFull ? 22 : 18),
            Background = Hex(rich.AccentHex),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, isUltra ? 4 : 7, 0),
            Opacity = isWorking ? 1.0 : 0.42
        };
        Grid.SetColumn(energyBar, 0);
        grid.Children.Add(energyBar);

        // 头像 + 细分状态微动效环（Ultra/Compact 自动缩尺寸保证极低高度仍完整可见）
        double avatarSize = isUltra ? 18 : (isFull ? 30 : 24);
        double dotSize = isUltra ? 6 : (isFull ? 9 : 7.5);
        var avatar = BuildRichAvatarWithFx(ag, avatarSize, dotSize, rich, marginRight: isUltra ? 4 : (isFull ? 8 : 7));
        Grid.SetColumn(avatar, 1);
        grid.Children.Add(avatar);

        // 模型与高中低档位微标
        string tierShortTag;
        if (!LocalModelCatalogService.SupportsModelSelection(ag.Id))
        {
            tierShortTag = "🔒默认托管";
        }
        else if (agLive != null && agLive.Status == "working" && !string.IsNullOrWhiteSpace(agLive.SelectedEffortTier))
        {
            tierShortTag = agLive.SelectedEffortTier switch
            {
                "high" => "🔥高档",
                "low" => "🚀低档",
                _ => "⚡中档"
            };
        }
        else if (!string.IsNullOrWhiteSpace(ag.PreferredEffortTier) && !ag.PreferredEffortTier.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            tierShortTag = ag.PreferredEffortTier.ToLowerInvariant() switch
            {
                "high" => "🔥优先高档",
                "low" => "🚀优先低档",
                _ => "⚡优先中档"
            };
        }
        else
        {
            tierShortTag = "★自动选档";
        }
        string modelTip = agLive != null && !string.IsNullOrWhiteSpace(agLive.SelectedModel)
            ? $"当前任务适配模型：{agLive.SelectedModel} ({agLive.SelectedTierBadge})"
            : $"高档：{ag.HighTierModel} | 中档：{ag.MediumTierModel} | 低档：{ag.LowTierModel}";

        var midStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, isUltra ? 0 : 6, 0) };

        if (isUltra)
        {
            // 极简单行态：仅 [名称] + [状态胶囊]，任务描述走行级 ToolTip，保持窄宽
            var leftInline = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            leftInline.Children.Add(new TextBlock
            {
                Text = ShortAgentName(ag.Name),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = Hex("#F8FAFC"),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            leftInline.Children.Add(BuildStatusBadgeWithMicroFx(rich, ultraCompact: true));
            midStack.Children.Add(leftInline);
        }
        else
        {
            var nameRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            nameRow.Children.Add(new TextBlock
            {
                Text = ShortAgentName(ag.Name),
                FontSize = isFull ? 13 : 12,
                FontWeight = FontWeights.Bold,
                Foreground = Hex("#F8FAFC"),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            nameRow.Children.Add(BuildStatusBadgeWithMicroFx(rich));

            nameRow.Children.Add(new Border
            {
                Background = Hex("#1E293B"),
                BorderBrush = Hex("#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = modelTip,
                Child = new TextBlock
                {
                    Text = tierShortTag,
                    FontSize = 10,
                    Foreground = Hex("#93C5FD")
                }
            });

            if (isFull && agLive != null && !string.IsNullOrWhiteSpace(agLive.UpdatedAt))
            {
                nameRow.Children.Add(new TextBlock
                {
                    Name = "UpdatedTimeText",
                    Text = agLive.UpdatedAt,
                    FontSize = 10.5,
                    Foreground = Hex("#64748B"),
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            midStack.Children.Add(nameRow);

            var statusTb = new TextBlock
            {
                Name = "TaskStatusLine",
                Text = statusLine,
                FontSize = isFull ? 13 : 12,
                Foreground = Hex(textHex),
                Margin = new Thickness(0, isFull ? 3 : 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = isFull && Width >= 500 ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MaxHeight = isFull ? (Width >= 500 ? 40 : 20) : 18
            };
            midStack.Children.Add(statusTb);

            // 编码实操能量条：仅完整模式下显示
            if (isFull && rich.Kind == FloatingRichFxKind.Coding)
            {
                midStack.Children.Add(BuildCodingEnergyBar(rich.AccentHex, Math.Max(120, Width - 160)));
            }
        }

        Grid.SetColumn(midStack, 2);
        grid.Children.Add(midStack);

        // 3. 右侧：纯图标极简按钮（精简/极简模式隐藏次要按钮，保证行高与防裁剪）
        var btnWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Style? actionBtnStyle = TryFindResource("ActionBtn") as Style;
        Style? primaryBtnStyle = TryFindResource("PrimaryBtn") as Style;
        Style? amberBtnStyle = TryFindResource("AmberBtn") as Style;
        bool showSecondaryBtns = isFull;
        bool showPrimaryActionOnly = !isUltra;
        if (showPrimaryActionOnly && agLive != null && (agLive.WorkflowPhase == "interrupted_net" || agLive.CanResumeInterruptedChain))
        {
            var retryBtn = new Button
            {
                Content = "↻",
                Style = amberBtnStyle,
                Padding = new Thickness(5, 1, 5, 1),
                FontSize = 10.5,
                Margin = new Thickness(0, 0, 3, 0),
                ToolTip = "网络恢复后点击此处：从已保护的断点立即重连续跑该任务链"
            };
            if (LongActionTracker.IsBusy(LongActionTracker.ResumeCheckpoint) || LongActionTracker.IsBusy(LongActionTracker.StallResolve))
                retryBtn.IsEnabled = false;
            retryBtn.Click += (_, _) => _mainWin.TriggerResumeCheckpointFromOverlay();
            btnWrap.Children.Add(retryBtn);
        }

        if (showPrimaryActionOnly && agLive != null && (agLive.Status == "error" || agLive.WorkflowPhase == "awaiting_user_choice"))
        {
            var solveBtn = new Button
            {
                Content = "解决",
                Style = primaryBtnStyle,
                Padding = new Thickness(6, 1, 6, 1),
                FontSize = 10.5,
                Margin = new Thickness(0, 0, 3, 0),
                ToolTip = "选择解决方式：清理弹窗后台重试 / 切纯 CLI 通道 / 改派其他 AI / 交由主脑接管"
            };
            solveBtn.Click += (_, _) => _mainWin.OpenStallResolutionDialogAndExecute(ag, this);
            btnWrap.Children.Add(solveBtn);

            if (showSecondaryBtns)
            {
                var clearErrBtn = new Button
                {
                    Content = "✕",
                    Style = actionBtnStyle,
                    Padding = new Thickness(5, 1, 5, 1),
                    FontSize = 10.5,
                    Margin = new Thickness(0, 0, 3, 0),
                    ToolTip = "后台关闭残留弹窗并清除此异常状态"
                };
                clearErrBtn.Click += (_, _) =>
                {
                    _ = System.Threading.Tasks.Task.Run(AutoDispatchService.DismissStuckAgentModalsSilent);
                    AutoDispatchService.ClearAgentLiveState(ag.Id, proj);
                    RefreshOverlayData(force: true);
                };
                btnWrap.Children.Add(clearErrBtn);
            }
        }

        if (showSecondaryBtns && agLive != null && !string.IsNullOrWhiteSpace(agLive.SessionId))
        {
            var sid = agLive.SessionId;
            var chatBtn = new Button
            {
                Content = "💬",
                Style = actionBtnStyle,
                Padding = new Thickness(5, 1, 5, 1),
                FontSize = 10.5,
                Margin = new Thickness(0, 0, 3, 0),
                ToolTip = $"查看此任务的对话框记录与交付结论 ({sid})"
            };
            chatBtn.Click += (_, _) => _mainWin.ActivateAndInspectDialogSession(sid, ag.Name);
            btnWrap.Children.Add(chatBtn);
        }

        if (showSecondaryBtns && AutoDispatchService.ResolveGuiExecutable(ag) != null)
        {
            var winBtn = new Button
            {
                Content = "↗",
                Style = actionBtnStyle,
                Padding = new Thickness(5, 1, 5, 1),
                FontSize = 10.5,
                ToolTip = winSt.IsRunning ? $"切到 {ag.Name} 窗口 (PID {winSt.Pid})" : $"启动 {ag.Name} 窗口"
            };
            winBtn.Click += (_, _) =>
            {
                _autoDispatch.EnsureOrActivateAgentWindow(ag, proj?.PrimaryRoot, activateIfRunning: true);
                RefreshOverlayData(force: true);
            };
            btnWrap.Children.Add(winBtn);
        }

        Grid.SetColumn(btnWrap, 3);
        grid.Children.Add(btnWrap);

        rowBorder.Child = grid;
        return rowBorder;
    }

    private async void HeaderLegendDots_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        // 一键演示：📋派单 → 📦交付 → ✅主脑验收
        try
        {
            string masterId = ResolveMasterAgentId();
            string workerId = _reg.UserConfig.Agents.FirstOrDefault(a => a.Enabled && a.Id != masterId)?.Id ?? "cursor";
            TriggerInteractionAnimationForTest("dispatch", masterId, workerId);
            await System.Threading.Tasks.Task.Delay(780);
            TriggerInteractionAnimationForTest("deliver", workerId, masterId);
            await System.Threading.Tasks.Task.Delay(780);
            TriggerVerifyBannerForTest("状态流转演示预览", masterId, workerId);
        }
        catch { }
    }

    private (string StatusLine, string StageTag, string RingHex, string DotHex, string TextHex) ResolveVisualState(
        AgentRosterEntry ag,
        LiveAgentWorkState? agLive,
        bool winRunning,
        Dictionary<string, LiveAgentWorkState> liveMap)
    {
        string dotHex = winRunning ? "#10B981" : "#64748B";

        if (!_reg.UserConfig.WorkflowEngineEnabled)
        {
            return (
                "独立模式（协同工作流已停用，由该 AI 独立执行）",
                "⚪ 独立运行",
                "#475569",
                dotHex,
                "#94A3B8"
            );
        }

        var activeProj = _reg.GetActiveProject();
        string masterId = activeProj != null && !string.IsNullOrWhiteSpace(activeProj.MasterPlannerId) && activeProj.MasterPlannerId != "auto"
            ? activeProj.MasterPlannerId
            : "antigravity";

        if (ag.Id.Equals(masterId, StringComparison.OrdinalIgnoreCase))
        {
            var activeWorker = liveMap.Values
                .Where(v => (v.Status == "working" || v.Status == "reworking") && !v.AgentId.Equals(masterId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.UpdatedAt)
                .FirstOrDefault();

            if (activeWorker != null)
            {
                string targetName = ShortAgentName(activeWorker.AgentName);
                string taskTitle = ExtractConciseTaskTitle(activeWorker.TaskSummary);
                if (activeWorker.WorkflowPhase == "reconnecting")
                {
                    return (
                        $"检测到 {targetName} 网络波动 ({activeWorker.LastFailureBadge}) · 正在第{activeWorker.NetworkRetryAttempt}/{activeWorker.MaxNetworkRetries}次自动重连",
                        "📡 自动重连",
                        "#F97316",
                        dotHex,
                        "#FDBA74"
                    );
                }
                if (activeWorker.WorkflowPhase == "master_verifying")
                {
                    return (
                        $"正在验收 {targetName} 第{Math.Max(1, activeWorker.CurrentRound)}轮实操落盘产出「{taskTitle}」",
                        "🔍 验收中",
                        "#C084FC",
                        dotHex,
                        "#F3E8FF"
                    );
                }
                if (activeWorker.WorkflowPhase == "reworking" || activeWorker.CurrentRound > 1)
                {
                    return (
                        $"验收未通过 · 已打回 {targetName} 第{activeWorker.CurrentRound}轮继续修复「{taskTitle}」",
                        "↩ 督办返工",
                        "#F97316",
                        dotHex,
                        "#FED7AA"
                    );
                }
                return (
                    $"已分析并派给 {targetName} 实操执行「{taskTitle}」",
                    "📤 已派单",
                    "#A855F7",
                    dotHex,
                    "#E9D5FF"
                );
            }

            var latestDone = liveMap.Values
                .Where(v => v.Status == "done" && !v.AgentId.Equals(masterId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.UpdatedAt)
                .FirstOrDefault();

            if (latestDone != null)
            {
                string workerName = ShortAgentName(latestDone.AgentName);
                string taskTitle = ExtractConciseTaskTitle(latestDone.TaskSummary);
                string roundNote = latestDone.CurrentRound > 1 ? $"({latestDone.CurrentRound}轮闭环)" : "";
                return (
                    $"已验收通过 {workerName} 实操完成的「{taskTitle}」{roundNote}并向用户交付",
                    "✅ 验收交付",
                    "#10B981",
                    dotHex,
                    "#A7F3D0"
                );
            }

            return (
                "全入口汇总主脑：深度分析 · 自动选模型档位 · 派单实操与验收",
                "🟣 主脑待命",
                "#8B5CF6",
                dotHex,
                "#CBD5E1"
            );
        }

        if (agLive != null && !string.IsNullOrWhiteSpace(agLive.TaskSummary))
        {
            string taskTitle = ExtractConciseTaskTitle(agLive.TaskSummary);

            if (agLive.Status == "working" || agLive.Status == "reworking")
            {
                var (fullLine, _, tag, ring, fg) = ResolveWorkerStageText(agLive, taskTitle);
                return (fullLine, tag, ring, dotHex, fg);
            }

            if (agLive.Status == "done")
            {
                string roundBadge = agLive.CurrentRound > 1 ? $"第{agLive.CurrentRound}轮" : "首轮";
                return (
                    $"「{taskTitle}」实操落盘完成 · 主脑{roundBadge}验收通过已交付",
                    "✅ 验收通过",
                    "#10B981",
                    dotHex,
                    "#86EFAC"
                );
            }

            if (agLive.Status == "error")
            {
                var errBadge = !string.IsNullOrWhiteSpace(agLive.LastFailureBadge) ? agLive.LastFailureBadge : "执行异常";
                if (agLive.WorkflowPhase == "awaiting_user_choice")
                {
                    return (
                        $"「{taskTitle}」卡住/受阻 ({errBadge}) · 点右侧「解决」选择处理方式",
                        "🛠 待选解决",
                        "#F97316",
                        "#F97316",
                        "#FDBA74"
                    );
                }
                if (agLive.WorkflowPhase == "interrupted_net" && errBadge.Contains("断网"))
                {
                    return (
                        $"「{taskTitle}」因 {errBadge} 暂停在第{agLive.CurrentRound}轮断点 · 点右侧 ↻ 续跑或「解决」",
                        "🔌 断线待续",
                        "#F97316",
                        "#F97316",
                        "#FDBA74"
                    );
                }
                if (agLive.CanResumeInterruptedChain)
                {
                    return (
                        $"「{taskTitle}」任务暂停 ({errBadge}) · 点右侧「解决」选择处理方式",
                        "⏸ 异常待续",
                        "#F97316",
                        "#F97316",
                        "#FDBA74"
                    );
                }
                return (
                    $"「{taskTitle}」异常中止 ({errBadge}) · 点右侧「解决」或 ✕ 复位",
                    "⚠ 异常",
                    "#EF4444",
                    "#EF4444",
                    "#FCA5A5"
                );
            }
        }

        return (
            "等待主脑分配实操任务（自动匹配模型与高中低档位）",
            "🕒 待命",
            winRunning ? "#334155" : "#1E293B",
            dotHex,
            "#94A3B8"
        );
    }

    private static (string FullLine, string ShortLine, string StageTag, string RingHex, string TextHex) ResolveWorkerStageText(
        LiveAgentWorkState state,
        string taskTitle)
    {
        if (state.WorkflowPhase == "reconnecting")
        {
            return (
                $"通信波动 ({state.LastFailureBadge}) · 正在第 {state.NetworkRetryAttempt}/{state.MaxNetworkRetries} 次断线自动重连并重发「{taskTitle}」",
                $"第{state.NetworkRetryAttempt}次断线重连中",
                $"📡 重连({state.NetworkRetryAttempt}/{state.MaxNetworkRetries})",
                "#F97316",
                "#FDBA74"
            );
        }

        if (state.WorkflowPhase == "master_verifying")
        {
            return (
                $"「{taskTitle}」第{Math.Max(1, state.CurrentRound)}轮执行完毕 · 正在交由主脑严格验收",
                $"「{taskTitle}」主脑验收中",
                "🔍 主脑验收",
                "#C084FC",
                "#F3E8FF"
            );
        }

        if (state.WorkflowPhase == "reworking" || state.CurrentRound > 1)
        {
            return (
                $"主脑打回重修 · 第{state.CurrentRound}/{Math.Max(2, state.MaxRounds)}轮使用高档模型继续实操修复「{taskTitle}」",
                $"第{state.CurrentRound}轮实操修复中",
                $"↩ 第{state.CurrentRound}轮重修",
                "#F97316",
                "#FED7AA"
            );
        }

        int tailLen = state.LatestLogTail?.Trim().Length ?? 0;

        // 阶段 1：刚收到任务开始启动
        if (tailLen < 35)
        {
            return (
                $"收到主脑分配的「{taskTitle}」实操任务 · 已启动 ({state.SelectedModel})",
                $"收到「{taskTitle}」· 已启动",
                "📥 已接单",
                "#38BDF8",
                "#7DD3FC"
            );
        }

        // 阶段 2：实操修改工程文件中
        if (tailLen < 220)
        {
            return (
                $"正在实操修改工程文件完成「{taskTitle}」...",
                $"「{taskTitle}」实操改工程中",
                "🛠 实操中",
                "#F59E0B",
                "#FDE68A"
            );
        }

        // 阶段 3：正在整理落盘变更提交主脑验收
        return (
            $"「{taskTitle}」实操完成，正在提交主脑核验工程变更...",
            $"「{taskTitle}」提交主脑验收",
            "🔍 提请验收",
            "#FBBF24",
            "#FEF08A"
        );
    }

    private UIElement BuildAvatarBadge(AgentRosterEntry ag, double size, double dotSize, string ringHex, string dotHex, double marginRight, string? tagAgentId = null)
    {
        var container = new Grid
        {
            Width = size,
            Height = size,
            Margin = new Thickness(0, 0, marginRight, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = string.IsNullOrWhiteSpace(tagAgentId) ? null : $"avatar:{tagAgentId}"
        };

        container.Children.Add(OfficialBrandIconService.CreateCircularAvatarVisual(
            ag.Id, size, ag, ringHex, size >= 28 ? 2.0 : 1.5));

        var statusDot = new Border
        {
            Width = dotSize,
            Height = dotSize,
            CornerRadius = new CornerRadius(dotSize / 2.0),
            Background = Hex(dotHex),
            BorderBrush = Hex("#0F1521"),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        container.Children.Add(statusDot);

        return container;
    }

    private static (string BgHex, string Glyph) GetAgentAvatarStyle(string id, string name)
    {
        return id switch
        {
            "antigravity" => ("#581C87", "✦"),
            "cursor" => ("#0369A1", "C"),
            "grok_bot" => ("#B45309", "G"),
            "codex_cli" => ("#047857", "X"),
            _ => ("#334155", string.IsNullOrWhiteSpace(name) ? "AI" : name[..1].ToUpperInvariant())
        };
    }

    private static string ShortAgentName(string rawName)
    {
        return rawName
            .Replace(" (桌面端)", "")
            .Replace(" (总包工头)", "")
            .Replace(" CLI", "")
            .Trim();
    }

    /// <summary>
    /// 从长任务指令中提炼简洁的核心任务名
    /// </summary>
    private static string ExtractConciseTaskTitle(string? rawSummary)
        => AutoDispatchService.ExtractCleanTaskBrief(rawSummary, 15);

    public static ToolTip BuildRichTooltipCard(
        AgentRosterEntry ag,
        LiveAgentWorkState? agLive,
        (bool IsRunning, int Pid, string WindowBadge) winSt)
    {
        var body = BuildRichTooltipContent(ag, agLive, winSt);
        return new ToolTip
        {
            Content = body,
            Background = Hex("#0F172A"),
            BorderBrush = Hex("#334155"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            MaxWidth = 400
        };
    }

    public static StackPanel BuildRichTooltipContent(
        AgentRosterEntry ag,
        LiveAgentWorkState? agLive,
        (bool IsRunning, int Pid, string WindowBadge) winSt)
    {
        var root = new StackPanel { Width = 372 };

        // 1. 顶部：AI 名称 + 运行状态徽章 + 模型标签
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        var rightBadge = new Border
        {
            Background = Hex(winSt.IsRunning ? "#0F2F26" : "#1E293B"),
            BorderBrush = Hex(winSt.IsRunning ? "#10B981" : "#334155"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = winSt.IsRunning ? $"● 在线 PID {winSt.Pid}" : "○ 未启动",
                FontSize = 10,
                Foreground = Hex(winSt.IsRunning ? "#6EE7B7" : "#94A3B8")
            }
        };
        DockPanel.SetDock(rightBadge, Dock.Right);
        header.Children.Add(rightBadge);

        header.Children.Add(new TextBlock
        {
            Text = ag.Name,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Hex("#F8FAFC"),
            VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(header);

        // 2. 模型与权限紧凑徽标行
        var metaWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        void AddPill(string text, string bgHex, string fgHex)
        {
            metaWrap.Children.Add(new Border
            {
                Background = Hex(bgHex),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 5, 4),
                Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = Hex(fgHex) }
            });
        }

        string activeModelText = (agLive != null && !string.IsNullOrWhiteSpace(agLive.SelectedModel))
            ? agLive.SelectedModel
            : (LocalModelCatalogService.SupportsModelSelection(ag.Id) ? ag.HighTierModel : "客户端默认托管");
        AddPill($"模型: {TrimEllipsis(activeModelText, 28)}", "#17253B", "#93C5FD");
        AddPill(ag.Permissions.AllowDirectSourceWrite ? "可改工程" : "只读", "#1E293B", ag.Permissions.AllowDirectSourceWrite ? "#86EFAC" : "#CBD5E1");
        if (ag.Permissions.AllowShellBuild) AddPill("可构建测试", "#1E293B", "#CBD5E1");
        if (ag.Permissions.HasPreviewMcp) AddPill("MCP验收", "#1E293B", "#C4B5FD");
        root.Children.Add(metaWrap);

        // 3. 若有当前任务状态，结构化呈现核心信息（自动剥离 Markdown 杂音）
        if (agLive != null && !string.IsNullOrWhiteSpace(agLive.TaskSummary))
        {
            void AddKeyValueRow(string label, string val, string valColor = "#CBD5E1")
            {
                if (string.IsNullOrWhiteSpace(val)) return;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var lbl = new TextBlock { Text = label, FontSize = 11, Foreground = Hex("#64748B") };
                var txt = new TextBlock
                {
                    Text = val,
                    FontSize = 11,
                    Foreground = Hex(valColor),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(lbl, 0);
                Grid.SetColumn(txt, 1);
                row.Children.Add(lbl);
                row.Children.Add(txt);
                root.Children.Add(row);
            }

            if (!string.IsNullOrWhiteSpace(agLive.SessionTitle) || !string.IsNullOrWhiteSpace(agLive.SessionId))
            {
                var dlgName = !string.IsNullOrWhiteSpace(agLive.SessionTitle)
                    ? TrimEllipsis(agLive.SessionTitle, 32)
                    : TrimEllipsis(agLive.SessionId, 16);
                AddKeyValueRow("所属对话", dlgName, "#7DD3FC");
            }
            if (!string.IsNullOrWhiteSpace(agLive.DispatchReason))
            {
                AddKeyValueRow("路由依据", TrimEllipsis(CleanMarkdownPreview(agLive.DispatchReason, 70), 70));
            }
            if (!string.IsNullOrWhiteSpace(agLive.VerificationSummary))
            {
                AddKeyValueRow("主脑验收", TrimEllipsis(CleanMarkdownPreview(agLive.VerificationSummary, 72), 72), "#86EFAC");
            }

            // 任务摘要小卡片
            var taskText = TrimEllipsis(CleanMarkdownPreview(agLive.TaskSummary, 110), 110);
            root.Children.Add(new Border
            {
                Background = Hex("#141E2E"),
                BorderBrush = Hex("#223047"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 3, 0, 0),
                Child = new TextBlock
                {
                    Text = $"任务：{taskText}",
                    FontSize = 11,
                    Foreground = Hex("#E2E8F0"),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 16
                }
            });

            if (!string.IsNullOrWhiteSpace(agLive.LatestLogTail))
            {
                var cleanTail = CleanMarkdownPreview(agLive.LatestLogTail, 130);
                if (!string.IsNullOrWhiteSpace(cleanTail))
                {
                    root.Children.Add(new TextBlock
                    {
                        Text = $"最新动态：{cleanTail}",
                        FontSize = 10.5,
                        Foreground = Hex("#94A3B8"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 5, 0, 0),
                        LineHeight = 15
                    });
                }
            }
        }
        else
        {
            root.Children.Add(new TextBlock
            {
                Text = "当前空闲待命 · 等待主脑分配实操任务",
                FontSize = 11,
                Foreground = Hex("#94A3B8")
            });
        }

        return root;
    }

    private static string TrimEllipsis(string? text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim();
        return s.Length <= maxLen ? s : s[..maxLen] + "...";
    }

    private static string CleanMarkdownPreview(string? raw, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var lines = raw.Replace("\uFFFD", "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => Regex.Replace(l.Trim(), @"^[#>*\-•\s]+", "").Replace("**", "").Replace("`", "").Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l)
                     && !l.StartsWith("自动派单产出")
                     && !l.StartsWith("执行时间:")
                     && !l.StartsWith("执行时间：")
                     && !l.StartsWith("执行模式:")
                     && !l.StartsWith("执行模式：")
                     && !l.StartsWith("关联项目对话框:")
                     && !l.StartsWith("关联项目对话框：")
                     && !l.StartsWith("统筹与验收主脑:")
                     && !l.StartsWith("统筹与验收主脑："))
            .ToList();
        var joined = string.Join(" · ", lines);
        joined = Regex.Replace(joined, @"\s{2,}", " ").Trim();
        return TrimEllipsis(joined, maxLen);
    }

    // ---------------- 交互事件：拖拽、吸附、折叠/展开、半透、打开设置/主面板 ----------------

    private void DragHeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleCollapseState();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
                SnapToScreenEdgesAndSave();
            }
            catch { }
        }
    }

    private void CollapsedMiniCapsule_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleCollapseState();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
                SnapToScreenEdgesAndSave();
            }
            catch { }
        }
    }

    private void DragHeaderBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SnapToScreenEdgesAndSave();
    }

    private void SnapToScreenEdgesAndSave()
    {
        var wa = GetActiveMonitorWorkArea();
        const double snapMargin = 22;

        double newLeft = Left;
        double newTop = Top;

        if (Math.Abs(newLeft - wa.Left) <= snapMargin) newLeft = wa.Left + 6;
        else if (Math.Abs((newLeft + ActualWidth) - wa.Right) <= snapMargin) newLeft = wa.Right - ActualWidth - 6;

        if (Math.Abs(newTop - wa.Top) <= snapMargin) newTop = wa.Top + 6;
        else if (newTop + 48 > wa.Bottom) newTop = wa.Bottom - 68;

        Left = newLeft;
        Top = newTop;

        _reg.UserConfig.FloatingWindowLeft = Left;
        _reg.UserConfig.FloatingWindowTop = Top;
        _reg.SaveUserConfig();
    }

    public void SetOpacityBarVisibleForPreview(bool visible)
    {
        ApplyTranslucentVisual();
        PreviewPopupMirror.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TranslucentBtn_Click(object sender, RoutedEventArgs e)
    {
        ApplyTranslucentVisual();
        OpacityPopup.PlacementTarget = TranslucentBtn;
        OpacityPopup.IsOpen = !OpacityPopup.IsOpen;
    }

    private void CapsuleOpacityBtn_Click(object sender, RoutedEventArgs e)
    {
        ApplyTranslucentVisual();
        OpacityPopup.PlacementTarget = CapsuleOpacityBtn;
        OpacityPopup.IsOpen = !OpacityPopup.IsOpen;
    }

    private void InlineOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressOpacitySliderEvent || !IsLoaded) return;
        int pct = (int)Math.Round(e.NewValue);
        SetOpacityPercentManual(pct, saveToDisk: false);
    }

    private void OpacityPopup_Closed(object? sender, EventArgs e)
    {
        _reg.SaveUserConfig();
        _mainWin.SyncAppSettingsControlsIfLoaded();
    }

    private void OpacityControl_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        int step = e.Delta > 0 ? 5 : -5;
        int next = Math.Clamp(_reg.UserConfig.FloatingOpacityPercent + step, 25, 100);
        SetOpacityPercentManual(next, saveToDisk: true);
        e.Handled = true;
    }

    private void CollapseToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        ToggleCollapseState();
    }

    public void ToggleCollapseState()
    {
        _reg.UserConfig.FloatingWindowCollapsed = !_reg.UserConfig.FloatingWindowCollapsed;
        ApplyCollapsedVisual(_reg.UserConfig.FloatingWindowCollapsed, preserveRightAnchor: true);
        _lastDiskFloatingCollapsed = _reg.UserConfig.FloatingWindowCollapsed;
        _reg.SaveUserConfig();
        _mainWin.SyncAppSettingsControlsIfLoaded();
    }

    private void ApplyThemeVisual()
    {
        var p = ThemeService.Resolve(_reg.UserConfig.FloatingThemeStyle);
        var bgBrush = Hex(p.FloatOuterBgHex);
        Background = System.Windows.Media.Brushes.Transparent;
        OuterFrame.Background = bgBrush;
        OuterFrame.BorderBrush = Hex(p.FloatOuterBorderHex);
        DragHeaderBar.Background = Hex(p.FloatHeaderBgHex);
        DragHeaderBar.BorderBrush = Hex(p.FloatHeaderBorderHex);
        if (ActivePolicyStrip != null)
        {
            ActivePolicyStrip.Background = Hex(p.FloatPolicyStripBgHex);
            ActivePolicyStrip.BorderBrush = Hex(p.FloatHeaderBorderHex);
        }
    }

    private void HeaderWorkflowBtn_Click(object sender, RoutedEventArgs e)
    {
        _mainWin.ToggleWorkflowEngineFromQuickAction();
    }

    private void SyncLayoutModeButtonUi()
    {
        if (LayoutModeBtn == null) return;
        EnsureDynamicMinHeight();
        var (barCount, barHeight, gap, barHex, tip) = _compactMode switch
        {
            FloatingCompactMode.Compact => (3, 2.0, 1.8, "#38BDF8", "当前视图：紧凑双行（点击切换为「极简单行」）"),
            FloatingCompactMode.Ultra => (4, 1.3, 1.2, "#34D399", "当前视图：极简单行（点击切换回「宽松完整」）"),
            _ => (2, 3.8, 2.4, "#93C5FD", "当前视图：宽松完整（点击切换为「紧凑双行」）")
        };
        var iconStack = new StackPanel
        {
            Width = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var barBrush = Hex(barHex);
        for (int i = 0; i < barCount; i++)
        {
            iconStack.Children.Add(new Border
            {
                Width = 12,
                Height = barHeight,
                CornerRadius = new CornerRadius(1),
                Background = barBrush,
                Margin = new Thickness(0, i == 0 ? 0 : gap, 0, 0)
            });
        }
        LayoutModeBtn.Content = iconStack;
        LayoutModeBtn.ToolTip = tip;
    }

    private void LayoutModeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_reg.UserConfig.FloatingWindowCollapsed)
        {
            _reg.UserConfig.FloatingWindowCollapsed = false;
            ApplyCollapsedVisual(false, preserveRightAnchor: true);
        }

        string nextMode = _compactMode switch
        {
            FloatingCompactMode.Full => "compact",
            FloatingCompactMode.Compact => "ultra",
            _ => "full"
        };
        _reg.UserConfig.FloatingLayoutMode = nextMode;
        _reg.UserConfig.FloatingCustomHeight = null;
        SizeToContent = SizeToContent.WidthAndHeight;
        Height = double.NaN;
        if (AgentsRowDef != null) AgentsRowDef.Height = GridLength.Auto;
        if (FloatingAgentsScroller != null) FloatingAgentsScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        _reg.SaveUserConfig();
        EnsureDynamicMinHeight();
        SyncExpandedWidthToCurrentMode(preserveRightAnchor: true);
        RefreshOverlayData(force: true);
        UpdateLayout();
        ClampWindowInsideWorkArea();
    }

    private void OpenSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        _mainWin.ActivateAndShowAppSettings();
    }

    private void OpenMainBtn_Click(object sender, RoutedEventArgs e)
    {
        _mainWin.RestoreAndActivateMainWindow();
    }

    internal void SyncQuickSoundButtonUi()
    {
        bool enabled = _reg.UserConfig.EnableSoundEffects;
        bool doneOnly = _reg.UserConfig.SoundLightweightMode;
        string icon = !enabled ? "🔕" : (doneOnly ? "🎵" : "🔔");
        string fgHex = !enabled ? "#94A3B8" : (doneOnly ? "#34D399" : "#38BDF8");
        string tip = !enabled
            ? "音效：已静音（已关闭协同中心提示音；如仍听到提示音请检查 Cursor/IDE 自身声音设置）"
            : (doneOnly
                ? "音效：轻量模式（仅保留达标交付提示音 · 点击静音）"
                : "音效：完整提示音（点击切换为轻量仅完成音）");

        foreach (var btn in new[] { HeaderSoundBtn, CapsuleSoundBtn })
        {
            if (btn == null) continue;
            btn.Content = icon;
            btn.Foreground = Hex(fgHex);
            btn.ToolTip = tip;
        }

        // 定时刷新只同步图标；硬闸门/停播统一走 ApplySoundPreference，避免反复 unmute 干扰
        if (_lastAppliedProcessMute != !enabled)
        {
            _lastAppliedProcessMute = !enabled;
            SoundFxService.ApplySoundPreference(_reg.UserConfig);
        }
    }

    private bool? _lastAppliedProcessMute;

    private void QuickSoundBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_reg.UserConfig.EnableSoundEffects)
        {
            _reg.UserConfig.EnableSoundEffects = true;
            _reg.UserConfig.SoundLightweightMode = false;
            SoundFxService.ApplySoundPreference(_reg.UserConfig);
            SoundFxService.Play(_reg.UserConfig, SoundFxService.SoundKind.DispatchFlight);
        }
        else if (!_reg.UserConfig.SoundLightweightMode)
        {
            _reg.UserConfig.EnableSoundEffects = true;
            _reg.UserConfig.SoundLightweightMode = true;
            // 轻量化：仅保留完成类提示音
            SoundFxService.ApplySoundPreference(_reg.UserConfig);
            SoundFxService.Play(_reg.UserConfig, SoundFxService.SoundKind.VerifyPassed);
        }
        else
        {
            _reg.UserConfig.EnableSoundEffects = false;
            _reg.UserConfig.SoundLightweightMode = false;
            SoundFxService.ApplySoundPreference(_reg.UserConfig);
        }

        _lastAppliedProcessMute = !_reg.UserConfig.EnableSoundEffects;
        ApplyTranslucentVisual();
        _reg.SaveUserConfig();
        _mainWin.SyncAppSettingsControlsIfLoaded();
    }
}
