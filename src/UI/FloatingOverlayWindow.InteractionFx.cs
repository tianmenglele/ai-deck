using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AICollabHub.Models;
using AICollabHub.Services;

namespace AICollabHub.UI;

public partial class FloatingOverlayWindow
{
    public const double ExpandedWidthMin = 340;
    public const double ExpandedWidthMax = 860;
    public const double ExpandedWidthDefault = 420;
    public const double CompactWidthDefault = 356;
    public const double UltraWidthMin = 216;
    public const double UltraWidthMax = 380;
    public const double UltraWidthDefault = 232;
    public const double ExpandedHeightMin = 165;
    public const double ExpandedHeightMax = 760;
    public const double CollapsedWidthMin = 128;
    public const double CollapsedWidthMax = 680;
    public const double CollapsedWidthDefault = 180;
    public const double CompactCardHeightThreshold = 70;
    public const double UltraCardHeightThreshold = 50;

    public enum FloatingCompactMode
    {
        Full = 0,
        Compact = 1,
        Ultra = 2
    }

    private FloatingCompactMode _compactMode = FloatingCompactMode.Full;
    public FloatingCompactMode CurrentCompactModeForTest => _compactMode;

    /// <summary>按启用 AI 数量动态计算最低高度（Ultra 极简单行下确保全员完整可见无裁切）。</summary>
    public static double ComputeDynamicMinHeight(int enabledAgentCount)
    {
        int n = Math.Max(1, enabledAgentCount);
        // 顶栏+外框≈65 + 每卡Ultra行(28高+2框+2距+余量)≈36
        double h = 65 + n * 36;
        return Math.Clamp(h, ExpandedHeightMin, ExpandedHeightMax);
    }

    public static FloatingCompactMode ResolveCompactMode(double windowHeight, int enabledAgentCount)
    {
        int n = Math.Max(1, enabledAgentCount);
        double usable = Math.Max(20, windowHeight - 88);
        double perCard = usable / n;
        if (perCard < UltraCardHeightThreshold)
            return FloatingCompactMode.Ultra;
        if (perCard < CompactCardHeightThreshold)
            return FloatingCompactMode.Compact;
        return FloatingCompactMode.Full;
    }

    public void EnsureDynamicMinHeight()
    {
        int n = Math.Max(1, _reg.UserConfig.Agents.Count(a => a.Enabled));
        double dyn = ComputeDynamicMinHeight(n);
        if (!_reg.UserConfig.FloatingWindowCollapsed)
        {
            bool hasCustomHeight = _reg.UserConfig.FloatingCustomHeight is double ch && ch > 0;
            MinHeight = hasCustomHeight ? dyn : 0;
            string explicitMode = (_reg.UserConfig.FloatingLayoutMode ?? "auto").Trim().ToLowerInvariant();
            if (explicitMode == "ultra")
            {
                _compactMode = FloatingCompactMode.Ultra;
            }
            else if (explicitMode == "compact")
            {
                _compactMode = FloatingCompactMode.Compact;
            }
            else if (explicitMode == "full")
            {
                _compactMode = FloatingCompactMode.Full;
            }
            else if (_reg.UserConfig.FloatingCustomHeight is double customH && customH > 0)
            {
                if (!double.IsNaN(Height) && Height > 0 && Height < dyn)
                    Height = dyn;
                double effectiveH = !double.IsNaN(Height) && Height > 40 ? Height : Math.Max(customH, dyn);
                _compactMode = ResolveCompactMode(effectiveH, n);
            }
            else
            {
                _compactMode = FloatingCompactMode.Full;
            }
            if (ActivePolicyStrip != null)
            {
                ActivePolicyStrip.Visibility = _compactMode == FloatingCompactMode.Ultra
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }
    }

    private readonly Dictionary<string, string> _prevPhaseByAgent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _prevRoundByAgent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _prevStatusByAgent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Status, string Phase)> _desktopOverlayBaselineByAgent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _avatarHostsByAgent = new(StringComparer.OrdinalIgnoreCase);
    private bool _interactionBaselineReady;
    private bool _sizeDragActive;
    private DispatcherTimer? _verifyBannerTimer;
    private int _interactionAnimCount;
    private int _verifyBannerCount;
    private string? _lastCelebratedSignature;
    /// <summary>隔离自检：离屏 Show 仅允许一次，严禁内部 Close 后再 Show。</summary>
    private bool _isolatedShellShownOnce;

    public static double ClampExpandedWidth(double width) => Math.Clamp(width, ExpandedWidthMin, ExpandedWidthMax);
    public static double ClampUltraWidth(double width) => Math.Clamp(width, UltraWidthMin, UltraWidthMax);
    public static double ClampExpandedHeight(double height) => Math.Clamp(height, ExpandedHeightMin, ExpandedHeightMax);

    /// <summary>自检：按启用数钳制高度不低于动态下限。</summary>
    public static double ClampExpandedHeightForAgents(double height, int enabledAgentCount) =>
        Math.Clamp(height, ComputeDynamicMinHeight(enabledAgentCount), ExpandedHeightMax);
    public static double ClampCollapsedWidth(double width) => Math.Clamp(width, CollapsedWidthMin, CollapsedWidthMax);

    /// <summary>按启用 AI 头像数量估算最紧凑胶囊宽度（未手动拖宽时）。</summary>
    public static double ComputeDynamicCollapsedWidth(int enabledAgentCount)
    {
        int n = Math.Max(1, enabledAgentCount);
        // 边框(3)+外内边距(10)+间距(4)+右侧三键(65)+末位光环余量(8)≈90 + 每头像(28+2间距)=30 → 3头像=180 / 4头像=210
        double w = 90 + n * 30;
        return Math.Clamp(w, CollapsedWidthMin, Math.Min(CollapsedWidthMax, 280));
    }

    public int InteractionAnimationTriggerCountForTest => _interactionAnimCount;
    public int VerifyBannerTriggerCountForTest => _verifyBannerCount;

    public (double MinW, double MaxW, double DefaultW, double UltraMinW, double UltraDefaultW, double MinH, double MaxH, double MinCollapsedW, double MaxCollapsedW, double DefaultCollapsedW)
        GetSizeLimitsForTest() =>
        (ExpandedWidthMin, ExpandedWidthMax, ExpandedWidthDefault, UltraWidthMin, UltraWidthDefault,
            ExpandedHeightMin, ExpandedHeightMax, CollapsedWidthMin, CollapsedWidthMax, CollapsedWidthDefault);

    public void ApplyCustomSizeForTest(double width, double? height, bool collapsed)
    {
        if (collapsed)
        {
            _reg.UserConfig.FloatingCollapsedCustomWidth = ClampCollapsedWidth(width);
            _reg.UserConfig.FloatingWindowCollapsed = true;
        }
        else
        {
            _reg.UserConfig.FloatingCustomHeight = height.HasValue ? ClampExpandedHeight(height.Value) : null;
            _reg.UserConfig.FloatingLayoutMode = "auto";
            _reg.UserConfig.FloatingWindowCollapsed = false;
            var peekMode = PeekExpandedCompactMode();
            if (peekMode == FloatingCompactMode.Ultra)
            {
                _reg.UserConfig.FloatingUltraCustomWidth = ClampUltraWidth(width);
            }
            else
            {
                _reg.UserConfig.FloatingCustomWidth = ClampExpandedWidth(width);
            }
        }
        ApplyCollapsedVisual(_reg.UserConfig.FloatingWindowCollapsed, preserveRightAnchor: false);
        _lastDiskFloatingCollapsed = _reg.UserConfig.FloatingWindowCollapsed;
        ApplyAdaptiveContentLayout();
        UpdateLayout();
    }

    public void ResetAdaptiveHeightForTest()
    {
        _reg.UserConfig.FloatingCustomHeight = null;
        _reg.UserConfig.FloatingLayoutMode = "auto";
        ApplyExpandedHeightMode();
        SyncExpandedWidthToCurrentMode(preserveRightAnchor: false);
        UpdateLayout();
    }

    /// <summary>离屏自检：触发飞行交互动画。kind=dispatch|deliver|rework|relay</summary>
    public int TriggerInteractionAnimationForTest(string kind, string fromAgentId, string toAgentId, string? taskSummary = null)
    {
        EnsureShownForFx();
        UpdateLayout();
        CacheAvatarHostsFromVisualTree();
        PlayInteractionKind(kind, fromAgentId, toAgentId, taskSummary);
        return _interactionAnimCount;
    }

    /// <summary>
    /// 飞行胶囊文案：有精简任务名时显示「图标 + 任务名」（按窗口宽度截断），否则回退固定单名。
    /// </summary>
    public static string FormatFlightBadgeText(string? kind, string? taskSummary, bool ultraNarrow = false)
    {
        string k = (kind ?? "").Trim().ToLowerInvariant();
        string fallback = k switch
        {
            "deliver" or "delivery" or "master_verifying" => "📦 交付包",
            "rework" or "reworking" => "🔧 重修单",
            "relay" or "handover" => "⚡ 接力",
            _ => "📋 任务单"
        };
        if (string.IsNullOrWhiteSpace(taskSummary)) return fallback;

        string title = ExtractConciseTaskTitle(taskSummary).Trim();
        if (title.EndsWith("..", StringComparison.Ordinal))
            title = title[..^2].TrimEnd();
        if (string.IsNullOrWhiteSpace(title) || title == "协同子任务") return fallback;

        int maxChars = ultraNarrow ? 6 : 12;
        if (title.Length > maxChars)
            title = title[..maxChars] + "…";
        int space = fallback.IndexOf(' ');
        string icon = space > 0 ? fallback[..space] : "📋";
        return icon + " " + title;
    }

    private void PlayInteractionKind(string kind, string fromAgentId, string toAgentId, string? taskSummary = null)
    {
        string glow;
        switch ((kind ?? "").Trim().ToLowerInvariant())
        {
            case "deliver":
            case "delivery":
            case "master_verifying":
                glow = "#34D399";
                break;
            case "rework":
            case "reworking":
                glow = "#F59E0B";
                break;
            case "relay":
            case "handover":
                glow = "#22D3EE";
                break;
            default:
                glow = "#A78BFA";
                break;
        }

        double winW = ActualWidth > 40 ? ActualWidth : (Width > 40 ? Width : UltraWidthDefault);
        bool ultraNarrow = _compactMode == FloatingCompactMode.Ultra || winW < 320;
        string emoji = FormatFlightBadgeText(kind, taskSummary, ultraNarrow);

        var from = ResolveAvatarCenter(fromAgentId) ?? new Point(36, 48);
        var to = ResolveAvatarCenter(toAgentId) ?? new Point(Math.Max(120, ActualWidth - 48), 96);
        LaunchBezierFlight(from, to, emoji, glow, toAgentId);
        _interactionAnimCount++;
    }

    /// <summary>离屏自检：触发主脑验收通过横幅与外框流光。</summary>
    public bool TriggerVerifyBannerForTest(string taskTitle, string? masterAgentId = null, string? workerAgentId = null)
    {
        EnsureShownForFx();
        ShowVerificationCelebration(taskTitle, masterAgentId ?? "antigravity", workerAgentId ?? "cursor");
        return VerifyCelebrationBanner.Visibility == Visibility.Visible || _verifyBannerCount > 0;
    }

    private void EnsureShownForFx()
    {
        if (AppPaths.IsIsolatedTest)
        {
            // 自检严禁弹到屏幕可见区 / 抢焦点：仅离屏布局，绝不二次抢 Show
            EnsureOffscreenIsolatedShell();
            ApplyCollapsedVisual(_reg.UserConfig.FloatingWindowCollapsed, preserveRightAnchor: false);
            RefreshOverlayData(force: true);
            ForceOffscreenLayoutPass();
            return;
        }

        // 形态/显隐绝对锁定：用户隐藏后，特效路径严禁 Show() 弹窗；仅在已可见时刷新文字/徽标
        if (!IsVisible || !_reg.UserConfig.FloatingWindowEnabled)
            return;

        ApplyCollapsedVisual(_reg.UserConfig.FloatingWindowCollapsed, preserveRightAnchor: false);
        RefreshOverlayData(force: true);
        UpdateLayout();
    }

    /// <summary>隔离自检：窗口钉在屏外；整次自检生命周期内最多 Show 一次，绝不 Close。</summary>
    private void EnsureOffscreenIsolatedShell()
    {
        // 严禁在 ResolveAvatarCenterForTest / EnsureShownForFx 内 Close
        try
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -12000;
            Top = -12000;
        }
        catch
        {
            // 忽略坐标设置瞬态异常，继续 Measure
        }

        if (_isolatedShellShownOnce || IsLoaded) return;
        try
        {
            Show();
            Left = -12000;
            Top = -12000;
            _isolatedShellShownOnce = true;
        }
        catch (InvalidOperationException)
        {
            // 若外部已关闭则不再 Show，仅靠 Measure/Arrange
            _isolatedShellShownOnce = true;
        }
    }

    /// <summary>离屏强制布局：不依赖 Window.IsVisible，零闪屏零抢焦点。</summary>
    private void ForceOffscreenLayoutPass()
    {
        double layoutW = Width > 40 && !double.IsNaN(Width) ? Width : ResolveExpandedWidth();
        double layoutH = (!double.IsNaN(Height) && Height > 40)
            ? Height
            : Math.Max(280, ExpandedHeightMin + 80);
        if (RootChromeGrid != null)
        {
            RootChromeGrid.Measure(new Size(layoutW, layoutH));
            RootChromeGrid.Arrange(new Rect(0, 0, layoutW, layoutH));
        }
        if (Content is UIElement rootContent)
        {
            rootContent.Measure(new Size(layoutW, layoutH));
            rootContent.Arrange(new Rect(0, 0, layoutW, layoutH));
        }
        UpdateLayout();
        Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        UpdateLayout();
    }

    /// <summary>无副作用窥探当前展开档位（宽度路由用；优先配置的 layout/height，避免旧 Height 污染 Ultra 宽度）。</summary>
    private FloatingCompactMode PeekExpandedCompactMode()
    {
        int n = Math.Max(1, _reg.UserConfig.Agents.Count(a => a.Enabled));
        string explicitMode = (_reg.UserConfig.FloatingLayoutMode ?? "auto").Trim().ToLowerInvariant();
        if (explicitMode == "ultra") return FloatingCompactMode.Ultra;
        if (explicitMode == "compact") return FloatingCompactMode.Compact;
        if (explicitMode == "full") return FloatingCompactMode.Full;
        if (_reg.UserConfig.FloatingCustomHeight is double customH && customH > 0)
        {
            double dyn = ComputeDynamicMinHeight(n);
            return ResolveCompactMode(Math.Max(customH, dyn), n);
        }
        return FloatingCompactMode.Full;
    }

    private double ResolveExpandedWidth()
    {
        var mode = PeekExpandedCompactMode();
        if (mode == FloatingCompactMode.Ultra)
            return ClampUltraWidth(_reg.UserConfig.FloatingUltraCustomWidth ?? UltraWidthDefault);
        if (mode == FloatingCompactMode.Compact)
            return ClampExpandedWidth(_reg.UserConfig.FloatingCustomWidth ?? CompactWidthDefault);
        return ClampExpandedWidth(_reg.UserConfig.FloatingCustomWidth ?? ExpandedWidthDefault);
    }

    private double ResolveCollapsedWidth()
    {
        if (_reg.UserConfig.FloatingCollapsedCustomWidth is double custom)
            return ClampCollapsedWidth(custom);
        int n = Math.Max(1, _reg.UserConfig.Agents.Count(a => a.Enabled));
        return ClampCollapsedWidth(ComputeDynamicCollapsedWidth(n));
    }

    /// <summary>按当前档位同步展开宽度（Full/Compact 与 Ultra 隔离存储）。</summary>
    private void SyncExpandedWidthToCurrentMode(bool preserveRightAnchor)
    {
        if (_reg.UserConfig.FloatingWindowCollapsed) return;
        ApplyTargetWindowWidth(ResolveExpandedWidth(), preserveRightAnchor);
        ClampWindowInsideWorkArea();
        ApplyAdaptiveContentLayout();
    }

    private bool IsAutoHeightSizing =>
        _reg.UserConfig.FloatingWindowCollapsed || _reg.UserConfig.FloatingCustomHeight is not double ch || ch <= 0;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// 统一改宽入口。AllowsTransparency(WS_EX_LAYERED) 下只改 Width 或 Min/Max 锁宽时 HWND 宽度会停在旧值，
    /// 故以 OuterFrame.Width 定内容宽、自适应高度时交给 SizeToContent.WidthAndHeight 同步测量 HWND，再按 DPI 像素兜底。
    /// </summary>
    private bool IsWindowWidthOff(double wantW) =>
        Math.Abs(Width - wantW) > 1.0
        || (ActualWidth > 40 && Math.Abs(ActualWidth - wantW) > 1.5)
        || (!double.IsNaN(OuterFrame.Width) && Math.Abs(OuterFrame.Width - wantW) > 1.0);

    private void ApplyTargetWindowWidth(double targetW, bool preserveRightAnchor, double? anchorRight = null, bool? autoHeight = null)
    {
        if (double.IsNaN(targetW) || targetW < 40) return;

        // 双保险不变量 3：展开态可见时禁止把宽度压到胶囊窄宽
        if (ExpandedFullContainer != null
            && ExpandedFullContainer.Visibility == Visibility.Visible
            && (CollapsedMiniCapsule == null || CollapsedMiniCapsule.Visibility != Visibility.Visible))
        {
            double minExpandedW = PeekExpandedCompactMode() == FloatingCompactMode.Ultra ? UltraWidthMin : ExpandedWidthMin;
            targetW = Math.Max(targetW, minExpandedW);
        }

        double curW = ActualWidth > 40 ? ActualWidth : Width;
        double oldRight = anchorRight ?? (Left + curW);

        OuterFrame.Width = targetW;
        MinWidth = 0;
        MaxWidth = double.PositiveInfinity;
        Width = targetW;
        if (autoHeight ?? IsAutoHeightSizing)
        {
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
        }

        if (preserveRightAnchor && !double.IsNaN(oldRight) && oldRight > -20000)
        {
            var wa = GetActiveMonitorWorkArea();
            double newLeft = oldRight - targetW;
            if (newLeft < wa.Left + 6) newLeft = wa.Left + 6;
            if (newLeft + targetW > wa.Right - 6) newLeft = wa.Right - targetW - 6;
            Left = newLeft;
        }

        if (!IsLoaded) return;
        UpdateLayout();
        if (AppPaths.IsIsolatedTest) return;
        if (Math.Abs(ActualWidth - targetW) <= 1.5) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ct = PresentationSource.FromVisual(this)?.CompositionTarget;
        double dpiX = ct?.TransformToDevice.M11 ?? 1.0;
        double dpiY = ct?.TransformToDevice.M22 ?? 1.0;
        double hDip = ActualHeight > 10 ? ActualHeight : (double.IsNaN(Height) ? 48 : Height);
        int pxW = (int)Math.Round(targetW * dpiX);
        int pxH = (int)Math.Round(hDip * dpiY);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, pxW, pxH, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        UpdateLayout();
    }

    private void ApplyExpandedHeightMode()
    {
        var wa = GetActiveMonitorWorkArea();
        int enabledCount = _reg.UserConfig.Agents.Count(a => a.Enabled);
        double dynMin = ComputeDynamicMinHeight(enabledCount);
        double maxAllowed = Math.Min(ExpandedHeightMax, Math.Max(dynMin, wa.Height - 24));
        if (_reg.UserConfig.FloatingCustomHeight is double customH && customH > 0)
        {
            double h = ClampExpandedHeightForAgents(Math.Min(customH, maxAllowed), enabledCount);
            SizeToContent = SizeToContent.Manual;
            MinHeight = dynMin;
            MaxHeight = maxAllowed;
            Height = Math.Max(h, dynMin);
            if (AgentsRowDef != null)
            {
                AgentsRowDef.Height = new GridLength(1, GridUnitType.Star);
            }
            if (FloatingAgentsScroller != null)
            {
                FloatingAgentsScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; // 靠动态最低高度保证全员可见
                FloatingAgentsScroller.MaxHeight = double.PositiveInfinity;
            }
        }
        else
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            MinHeight = 0;
            MaxHeight = maxAllowed;
            Height = double.NaN;
            if (AgentsRowDef != null)
            {
                AgentsRowDef.Height = GridLength.Auto;
            }
            if (FloatingAgentsScroller != null)
            {
                FloatingAgentsScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                FloatingAgentsScroller.MaxHeight = double.PositiveInfinity;
            }
        }
        EnsureDynamicMinHeight();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WIN_POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WIN_RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WIN_MONITORINFO
    {
        public int cbSize;
        public WIN_RECT rcMonitor;
        public WIN_RECT rcWork;
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(WIN_POINT pt, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref WIN_MONITORINFO lpmi);

    private Rect GetActiveMonitorWorkArea(double? probeLeft = null, double? probeTop = null)
    {
        try
        {
            double l = probeLeft ?? Left;
            double t = probeTop ?? Top;
            if (AppPaths.IsIsolatedTest && (l < -5000 || t < -5000))
                return SystemParameters.WorkArea;

            double dpiX = 1.0, dpiY = 1.0;
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                dpiX = src.CompositionTarget.TransformToDevice.M11;
                dpiY = src.CompositionTarget.TransformToDevice.M22;
            }
            double w = Width > 40 ? Width : 420;
            double h = ActualHeight > 40 ? ActualHeight : (Height > 40 ? Height : 200);
            var pt = new WIN_POINT
            {
                X = (int)Math.Round((l + w / 2.0) * dpiX),
                Y = (int)Math.Round((t + h / 2.0) * dpiY)
            };
            var hMon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new WIN_MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<WIN_MONITORINFO>() };
            if (hMon != IntPtr.Zero && GetMonitorInfoW(hMon, ref mi))
            {
                return new Rect(
                    mi.rcWork.Left / dpiX,
                    mi.rcWork.Top / dpiY,
                    Math.Max(320, (mi.rcWork.Right - mi.rcWork.Left) / dpiX),
                    Math.Max(240, (mi.rcWork.Bottom - mi.rcWork.Top) / dpiY));
            }
        }
        catch { }
        return SystemParameters.WorkArea;
    }

    private void ClampWindowInsideWorkArea()
    {
        if (AppPaths.IsIsolatedTest && (Left < -5000 || Top < -5000)) return;
        var wa = GetActiveMonitorWorkArea();
        if (Width > wa.Width - 12)
        {
            Width = Math.Max(CollapsedWidthMin, wa.Width - 12);
            OuterFrame.Width = Width;
        }
        if (!double.IsNaN(Height) && Height > wa.Height - 12)
            Height = Math.Max(ExpandedHeightMin, wa.Height - 12);

        if (Left + Width > wa.Right - 4) Left = Math.Max(wa.Left + 4, wa.Right - Width - 4);
        if (Left < wa.Left + 4) Left = wa.Left + 4;
        if (Top + Math.Max(ActualHeight, 48) > wa.Bottom - 4)
            Top = Math.Max(wa.Top + 4, wa.Bottom - Math.Max(ActualHeight, 48) - 4);
        if (Top < wa.Top + 4) Top = wa.Top + 4;
    }

    private void ApplyAdaptiveContentLayout()
    {
        double rawW = ActualWidth > 40 ? ActualWidth : Width;
        double contentW = Math.Max(200, rawW);
        bool ultraNarrow = contentW < 320 || _compactMode == FloatingCompactMode.Ultra;
        bool wide = contentW >= 500;
        bool compactHeader = contentW < 460 || ultraNarrow;

        if (HeaderTitleText != null)
        {
            // Ultra 窄宽：隐藏长标题，仅保留状态圆点 + 项目徽章，避免顶栏按钮裁切
            if (ultraNarrow)
            {
                HeaderTitleText.Visibility = Visibility.Collapsed;
                HeaderTitleText.Text = "";
            }
            else
            {
                HeaderTitleText.Visibility = Visibility.Visible;
                HeaderTitleText.Text = contentW < 395 ? "协同" : "AI 协同动态";
            }
        }
        if (HeaderLegendDots != null)
        {
            HeaderLegendDots.Visibility = compactHeader ? Visibility.Collapsed : Visibility.Visible;
        }
        if (!_reg.UserConfig.FloatingWindowCollapsed)
        {
            switch (_compactMode)
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
            if (FloatingAgentsScroller != null)
            {
                FloatingAgentsScroller.Margin = _compactMode switch
                {
                    FloatingCompactMode.Ultra => new Thickness(0, 3, 0, 0),
                    FloatingCompactMode.Compact => new Thickness(0, 4, 0, 0),
                    _ => new Thickness(0, 5, 0, 0)
                };
            }
        }
        if (HeaderPulseDot != null)
        {
            HeaderPulseDot.Width = HeaderPulseDot.Height = ultraNarrow ? 7 : 8;
            HeaderPulseDot.Margin = ultraNarrow ? new Thickness(0, 0, 4, 0) : new Thickness(0, 0, 6, 0);
        }
        if (HeaderProjBadgeHost != null)
        {
            HeaderProjBadgeHost.Margin = ultraNarrow ? new Thickness(0, 0, 3, 0) : new Thickness(6, 0, 0, 0);
            HeaderProjBadgeHost.MinWidth = ultraNarrow ? 18 : 0;
            HeaderProjBadgeHost.MaxWidth = ultraNarrow
                ? Math.Clamp(contentW * 0.22, 32, 48)
                : (compactHeader
                    ? Math.Max(110, contentW * 0.34)
                    : Math.Max(96, contentW * 0.30));
        }
        if (DragHeaderBar != null)
        {
            DragHeaderBar.Padding = ultraNarrow ? new Thickness(5, 2, 3, 2) : new Thickness(8, 4, 8, 4);
        }
        // Ultra 窄宽压缩顶栏按钮；非 Ultra 必须还原常规值，否则切回 Full 后按钮仍偏小
        void TuneHeaderBtn(System.Windows.Controls.Button? btn, double normalMarginRight, double normalPadX, double normalPadY, double ultraPadX, double ultraPadY)
        {
            if (btn == null) return;
            if (ultraNarrow)
            {
                btn.Margin = new Thickness(0, 0, normalMarginRight > 0 ? 2 : 0, 0);
                btn.Padding = new Thickness(ultraPadX, ultraPadY, ultraPadX, ultraPadY);
            }
            else
            {
                btn.Margin = new Thickness(0, 0, normalMarginRight, 0);
                btn.Padding = new Thickness(normalPadX, normalPadY, normalPadX, normalPadY);
            }
        }
        TuneHeaderBtn(HeaderSoundBtn, 3, 5, 1, 3.5, 1);
        TuneHeaderBtn(HeaderWorkflowBtn, 3, 5, 1, 3.5, 1);
        TuneHeaderBtn(LayoutModeBtn, 3, 6, 3, 4, 2.5);
        TuneHeaderBtn(TranslucentBtn, 3, 6, 1, 3.5, 1);
        TuneHeaderBtn(CollapseToggleBtn, 3, 6, 1, 3.5, 1);
        TuneHeaderBtn(OpenSettingsBtn, 0, 5, 1, 3.5, 1);
        // 收起胶囊态音效/透明度按钮始终保持紧凑
        foreach (var capBtn in new[] { CapsuleSoundBtn, CapsuleOpacityBtn })
        {
            if (capBtn == null) continue;
            capBtn.Margin = new Thickness(0, 0, 2, 0);
            capBtn.Padding = new Thickness(4, 1, 4, 1);
        }
        if (HeaderPolicyText != null)
        {
            HeaderPolicyText.MaxWidth = Math.Max(120, contentW - 180);
        }
        if (HeaderPolicyMetaText != null)
        {
            HeaderPolicyMetaText.MaxWidth = Math.Max(70, contentW * 0.28);
        }

        foreach (var child in FloatingAgentsStack.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is not string) continue;
            var statusTb = FindNamedDescendant<TextBlock>(child, "TaskStatusLine");
            if (statusTb != null)
            {
                if (_compactMode == FloatingCompactMode.Full && wide)
                {
                    statusTb.TextWrapping = TextWrapping.Wrap;
                    statusTb.MaxHeight = 36;
                    statusTb.TextTrimming = TextTrimming.CharacterEllipsis;
                }
                else
                {
                    statusTb.TextWrapping = TextWrapping.NoWrap;
                    statusTb.MaxHeight = 18;
                    statusTb.TextTrimming = TextTrimming.CharacterEllipsis;
                }
            }
        }
    }

    private static T? FindNamedDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T fe && fe.Name == name) return fe;
            var nested = FindNamedDescendant<T>(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    private void ResizeThumbRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _sizeDragActive = true;
        double baseW = ActualWidth > 40 ? ActualWidth : Width;
        if (_reg.UserConfig.FloatingWindowCollapsed)
        {
            double w = ClampCollapsedWidth(baseW + e.HorizontalChange);
            _reg.UserConfig.FloatingCollapsedCustomWidth = w;
            ApplyTargetWindowWidth(w, preserveRightAnchor: false);
        }
        else if (PeekExpandedCompactMode() == FloatingCompactMode.Ultra)
        {
            double w = ClampUltraWidth(baseW + e.HorizontalChange);
            _reg.UserConfig.FloatingUltraCustomWidth = w;
            ApplyTargetWindowWidth(w, preserveRightAnchor: false);
            ApplyAdaptiveContentLayout();
        }
        else
        {
            double w = ClampExpandedWidth(baseW + e.HorizontalChange);
            _reg.UserConfig.FloatingCustomWidth = w;
            ApplyTargetWindowWidth(w, preserveRightAnchor: false);
            ApplyAdaptiveContentLayout();
        }
        ClampWindowInsideWorkArea();
    }

    private void ResizeThumbBottom_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_reg.UserConfig.FloatingWindowCollapsed) return;
        _sizeDragActive = true;
        var prevMode = _compactMode;
        double baseH = double.IsNaN(Height) || Height <= 0 ? Math.Max(ActualHeight, ExpandedHeightMin) : Height;
        SizeToContent = SizeToContent.Manual;
        int enabledN = _reg.UserConfig.Agents.Count(a => a.Enabled);
        Height = ClampExpandedHeightForAgents(baseH + e.VerticalChange, enabledN);
        _reg.UserConfig.FloatingCustomHeight = Height;
        _reg.UserConfig.FloatingLayoutMode = "auto";
        EnsureDynamicMinHeight();
        if (AgentsRowDef != null)
            AgentsRowDef.Height = new GridLength(1, GridUnitType.Star);
        if (FloatingAgentsScroller != null)
            FloatingAgentsScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        if (_compactMode != prevMode)
        {
            RefreshOverlayData(force: true);
        }
        ClampWindowInsideWorkArea();
    }

    private void ResizeThumbCorner_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeThumbRight_DragDelta(sender, e);
        if (!_reg.UserConfig.FloatingWindowCollapsed)
            ResizeThumbBottom_DragDelta(sender, e);
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _sizeDragActive = false;
        ClampWindowInsideWorkArea();
        ApplyAdaptiveContentLayout();
        _reg.UserConfig.FloatingWindowLeft = Left;
        _reg.UserConfig.FloatingWindowTop = Top;
        _reg.SaveUserConfig();
        _mainWin.SyncAppSettingsControlsIfLoaded();
    }

    private void ResizeThumbCorner_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_reg.UserConfig.FloatingWindowCollapsed) return;
        _reg.UserConfig.FloatingCustomHeight = null;
        ApplyExpandedHeightMode();
        UpdateLayout();
        ClampWindowInsideWorkArea();
        _reg.SaveUserConfig();
        e.Handled = true;
    }

    private bool _captureQuietMode;

    /// <summary>快照截图静默模式：关闭自动飞行胶囊与验收横幅触发并清空残留浮层动画。</summary>
    public void SetCaptureQuietModeForTest(bool quiet)
    {
        _captureQuietMode = quiet;
        if (!quiet) return;
        try
        {
            _verifyBannerTimer?.Stop();
            InteractionFxCanvas?.Children.Clear();
            if (VerifyCelebrationBanner != null)
            {
                VerifyCelebrationBanner.BeginAnimation(OpacityProperty, null);
                VerifyCelebrationBanner.Visibility = Visibility.Collapsed;
                VerifyCelebrationBanner.Opacity = 0;
            }
            if (VerifyCelebrationPopup != null)
            {
                VerifyCelebrationPopup.IsOpen = false;
            }
            if (OuterFrame?.BorderBrush is SolidColorBrush b)
            {
                b.BeginAnimation(SolidColorBrush.ColorProperty, null);
            }
            ApplyThemeVisual();
        }
        catch { }
    }

    private void DetectAndPlayInteractionFx(
        IReadOnlyList<AgentRosterEntry> enabledAgents,
        Dictionary<string, LiveAgentWorkState> liveMap)
    {
        string masterId = ResolveMasterAgentId();
        CacheAvatarHostsFromVisualTree();

        if (!_interactionBaselineReady || _captureQuietMode)
        {
            Dictionary<string, LiveAgentWorkState>? bareMap = null;
            foreach (var ag in enabledAgents)
            {
                liveMap.TryGetValue(ag.Id, out var lv);
                _prevStatusByAgent[ag.Id] = lv?.Status ?? "idle";
                _prevPhaseByAgent[ag.Id] = lv?.WorkflowPhase ?? "idle";
                _prevRoundByAgent[ag.Id] = lv?.CurrentRound ?? 1;
                if (lv?.IsDesktopLiveOverlay == true)
                {
                    bareMap ??= AutoDispatchService.ReadLatestGlobalWorkStates();
                    bareMap.TryGetValue(ag.Id, out var bareLv);
                    _desktopOverlayBaselineByAgent[ag.Id] = (bareLv?.Status ?? "idle", bareLv?.WorkflowPhase ?? "idle");
                }
            }
            _interactionBaselineReady = true;
            return;
        }

        string? previousWorkingId = _prevStatusByAgent
            .Where(kv => kv.Value is "working" or "reworking" && !kv.Key.Equals(masterId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .FirstOrDefault();

        foreach (var ag in enabledAgents)
        {
            liveMap.TryGetValue(ag.Id, out var lv);
            string curStatus = lv?.Status ?? "idle";
            string curPhase = lv?.WorkflowPhase ?? "idle";
            int curRound = lv?.CurrentRound ?? 1;
            _prevStatusByAgent.TryGetValue(ag.Id, out var prevStatus);
            _prevPhaseByAgent.TryGetValue(ag.Id, out var prevPhase);
            _prevRoundByAgent.TryGetValue(ag.Id, out var prevRound);
            prevStatus ??= "idle";
            prevPhase ??= "idle";
            if (prevRound <= 0) prevRound = 1;

            // 桌面端实时忙碌叠加：记录叠加前的真实基线；叠加结束回落历史态时与基线比较，避免误播验收/派单特效与音效
            bool curIsDesktopOverlay = lv?.IsDesktopLiveOverlay == true;
            bool hadDesktopBaseline = _desktopOverlayBaselineByAgent.TryGetValue(ag.Id, out var overlayBaseline);
            if (curIsDesktopOverlay && !hadDesktopBaseline)
            {
                _desktopOverlayBaselineByAgent[ag.Id] = (prevStatus, prevPhase);
            }
            else if (!curIsDesktopOverlay && hadDesktopBaseline)
            {
                _desktopOverlayBaselineByAgent.Remove(ag.Id);
                prevStatus = overlayBaseline.Status;
                prevPhase = overlayBaseline.Phase;
            }

            bool phaseChanged = !string.Equals(prevPhase, curPhase, StringComparison.OrdinalIgnoreCase);
            bool statusChanged = !string.Equals(prevStatus, curStatus, StringComparison.OrdinalIgnoreCase);
            bool roundUp = curRound > prevRound;

            if (!phaseChanged && !statusChanged && !roundUp)
            {
                _prevStatusByAgent[ag.Id] = curStatus;
                _prevPhaseByAgent[ag.Id] = curPhase;
                _prevRoundByAgent[ag.Id] = curRound;
                continue;
            }

            // 桌面忙态叠加期间只同步状态，不触发派单/验收特效与音效
            if (curIsDesktopOverlay)
            {
                _prevStatusByAgent[ag.Id] = curStatus;
                _prevPhaseByAgent[ag.Id] = curPhase;
                _prevRoundByAgent[ag.Id] = curRound;
                continue;
            }

            // 派单：进入 executing / working
            if ((curPhase.Equals("executing", StringComparison.OrdinalIgnoreCase) ||
                 (curStatus.Equals("working", StringComparison.OrdinalIgnoreCase) && curRound <= 1))
                && (phaseChanged || statusChanged)
                && !prevPhase.Equals("executing", StringComparison.OrdinalIgnoreCase)
                && !prevPhase.Equals("reworking", StringComparison.OrdinalIgnoreCase)
                && !ag.Id.Equals(masterId, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(previousWorkingId) &&
                    !previousWorkingId.Equals(ag.Id, StringComparison.OrdinalIgnoreCase) &&
                    statusChanged)
                {
                    PlayInteractionKind("relay", previousWorkingId, ag.Id, lv?.TaskSummary);
                }
                else
                {
                    PlayInteractionKind("dispatch", masterId, ag.Id, lv?.TaskSummary);
                }
            }
            else if (curPhase.Equals("master_verifying", StringComparison.OrdinalIgnoreCase) && phaseChanged)
            {
                PlayInteractionKind("deliver", ag.Id, masterId, lv?.TaskSummary);
            }
            else if ((curPhase.Equals("reworking", StringComparison.OrdinalIgnoreCase) || roundUp) &&
                     !ag.Id.Equals(masterId, StringComparison.OrdinalIgnoreCase) &&
                     (phaseChanged || roundUp))
            {
                PlayInteractionKind("rework", masterId, ag.Id, lv?.TaskSummary);
            }
            else if (!string.IsNullOrWhiteSpace(lv?.FailoverNote) && statusChanged &&
                     curStatus.Equals("working", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(previousWorkingId) &&
                     !previousWorkingId.Equals(ag.Id, StringComparison.OrdinalIgnoreCase))
            {
                PlayInteractionKind("relay", previousWorkingId, ag.Id, lv?.TaskSummary);
            }

            // 验收通过庆典（轮询可能跨过短暂 master_verifying，需补播交付包飞行）
            // 桌面忙态结束回落时 prev 已还原为叠加前基线，与历史态一致则不会误触
            bool justVerified =
                (curPhase.Equals("verified_delivered", StringComparison.OrdinalIgnoreCase) && phaseChanged) ||
                (curStatus.Equals("done", StringComparison.OrdinalIgnoreCase) &&
                 statusChanged &&
                 (curPhase.Equals("verified_delivered", StringComparison.OrdinalIgnoreCase) ||
                  !string.IsNullOrWhiteSpace(lv?.VerificationSummary)));
            if (justVerified && !ag.Id.Equals(masterId, StringComparison.OrdinalIgnoreCase))
            {
                string sig = $"{ag.Id}|{lv?.UpdatedAt}|{lv?.TaskSummary}|{curPhase}";
                if (!string.Equals(sig, _lastCelebratedSignature, StringComparison.Ordinal))
                {
                    _lastCelebratedSignature = sig;
                    bool skippedVerifying =
                        !prevPhase.Equals("master_verifying", StringComparison.OrdinalIgnoreCase) &&
                        (prevStatus is "working" or "reworking" ||
                         prevPhase is "executing" or "reworking" or "working");
                    if (skippedVerifying)
                    {
                        PlayInteractionKind("deliver", ag.Id, masterId, lv?.TaskSummary);
                    }
                    ShowVerificationCelebration(ExtractConciseTaskTitle(lv?.TaskSummary), masterId, ag.Id);
                }
            }

            _prevStatusByAgent[ag.Id] = curStatus;
            _prevPhaseByAgent[ag.Id] = curPhase;
            _prevRoundByAgent[ag.Id] = curRound;
        }

        // 同步未出现在本轮的旧条目
        foreach (var ag in enabledAgents)
        {
            if (!_prevStatusByAgent.ContainsKey(ag.Id))
            {
                liveMap.TryGetValue(ag.Id, out var lv);
                _prevStatusByAgent[ag.Id] = lv?.Status ?? "idle";
                _prevPhaseByAgent[ag.Id] = lv?.WorkflowPhase ?? "idle";
                _prevRoundByAgent[ag.Id] = lv?.CurrentRound ?? 1;
            }
        }
    }

    private string ResolveMasterAgentId()
    {
        var activeProj = _reg.GetActiveProject();
        if (activeProj != null && !string.IsNullOrWhiteSpace(activeProj.MasterPlannerId) &&
            !activeProj.MasterPlannerId.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return activeProj.MasterPlannerId;
        }
        var routed = RoutingService.ResolveMasterPlanner(_reg, activeProj);
        return routed?.Id ?? "antigravity";
    }

    /// <summary>
    /// 沿视觉树向上检查 Visibility，不依赖 Window.Show 后的 IsVisible（自检离屏时 IsVisible 恒为 false）。
    /// </summary>
    private static bool IsSubtreeVisibleToRoot(DependencyObject? el, DependencyObject? stopRoot)
    {
        if (el == null) return false;
        DependencyObject? cur = el;
        while (cur != null)
        {
            if (cur is UIElement ui && ui.Visibility != Visibility.Visible)
                return false;
            if (stopRoot != null && ReferenceEquals(cur, stopRoot))
                return true;
            cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
        }
        // 未遇到 stopRoot 时：若整条链 Visibility 均为 Visible 也视为激活态可见
        return true;
    }

    private bool IsAvatarHostActivelyVisible(FrameworkElement fe)
    {
        if (fe == null) return false;
        if (!IsSubtreeVisibleToRoot(fe, RootChromeGrid)) return false;
        double w = fe.ActualWidth > 0 ? fe.ActualWidth : (double.IsNaN(fe.Width) ? 0 : fe.Width);
        return w > 0;
    }

    private void CacheAvatarHostsFromVisualTree()
    {
        _avatarHostsByAgent.Clear();
        foreach (var fe in EnumerateAllAvatarCandidates())
        {
            if (fe.Tag is string agentId && agentId.StartsWith("avatar:", StringComparison.OrdinalIgnoreCase))
            {
                var id = agentId["avatar:".Length..];
                // 激活态可见（Visibility 链）优先；收起胶囊态下禁止被隐藏展开态头像覆盖
                if (IsAvatarHostActivelyVisible(fe))
                {
                    _avatarHostsByAgent[id] = fe;
                }
                else if (!_avatarHostsByAgent.ContainsKey(id))
                {
                    // 尚无可见项时才允许不可见节点兜底占位
                    _avatarHostsByAgent[id] = fe;
                }
            }
        }
    }

    private IEnumerable<FrameworkElement> EnumerateAllAvatarCandidates()
    {
        var seen = new HashSet<FrameworkElement>();
        foreach (var root in new DependencyObject?[] { CapsuleAvatarsPanel, FloatingAgentsStack, RootChromeGrid })
        {
            if (root == null) continue;
            foreach (var fe in EnumerateVisualDescendants(root).OfType<FrameworkElement>()
                         .Concat(EnumerateLogicalDescendants(root).OfType<FrameworkElement>()))
            {
                if (seen.Add(fe)) yield return fe;
            }
        }
    }

    /// <summary>离屏自检：强制重建头像缓存并解析中心坐标（展开/收起态均应非空）。严禁内部 Close。</summary>
    public Point? ResolveAvatarCenterForTest(string agentId)
    {
        // 禁止 Close()：由 Program.cs 全部断言结束后再外部 Close
        if (AppPaths.IsIsolatedTest)
        {
            EnsureOffscreenIsolatedShell();
        }

        ApplyCollapsedVisual(_reg.UserConfig.FloatingWindowCollapsed, preserveRightAnchor: false);
        RefreshOverlayData(force: true);
        ForceOffscreenLayoutPass();

        // 按当前折叠态优先在对应面板内定位
        FrameworkElement? preferred = null;
        var searchRoots = _reg.UserConfig.FloatingWindowCollapsed
            ? new DependencyObject?[] { CapsuleAvatarsPanel, RootChromeGrid, FloatingAgentsStack }
            : new DependencyObject?[] { FloatingAgentsStack, RootChromeGrid, CapsuleAvatarsPanel };

        foreach (var root in searchRoots)
        {
            if (root == null) continue;
            foreach (var fe in EnumerateVisualDescendants(root).OfType<FrameworkElement>()
                         .Concat(EnumerateLogicalDescendants(root).OfType<FrameworkElement>()))
            {
                if (fe.Tag is not string tag ||
                    !tag.Equals($"avatar:{agentId}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
                {
                    fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    fe.Arrange(new Rect(fe.DesiredSize));
                }
                bool active = IsSubtreeVisibleToRoot(fe, RootChromeGrid);
                bool prefActive = preferred != null && IsSubtreeVisibleToRoot(preferred, RootChromeGrid);
                if (preferred == null || (active && !prefActive) ||
                    (active == prefActive && fe.ActualWidth > preferred.ActualWidth))
                {
                    preferred = fe;
                }
                if (active && fe.ActualWidth > 0) break;
            }
            if (preferred != null && IsSubtreeVisibleToRoot(preferred, RootChromeGrid) && preferred.ActualWidth > 0)
                break;
        }

        if (preferred != null)
        {
            _avatarHostsByAgent[agentId] = preferred;
            return ComputeAvatarCenterPoint(preferred, requireActiveSubtree: true);
        }

        CacheAvatarHostsFromVisualTree();
        return ResolveAvatarCenter(agentId);
    }

    private static IEnumerable<DependencyObject> EnumerateVisualDescendants(DependencyObject root)
    {
        if (root == null) yield break;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in EnumerateVisualDescendants(child))
                yield return nested;
        }
    }

    private static IEnumerable<DependencyObject> EnumerateLogicalDescendants(DependencyObject root)
    {
        if (root == null) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in EnumerateLogicalDescendants(child))
                yield return nested;
        }
    }

    private Point? ResolveAvatarCenter(string agentId)
    {
        if (!_avatarHostsByAgent.TryGetValue(agentId, out var host) || host == null)
            return null;
        return ComputeAvatarCenterPoint(host, requireActiveSubtree: true);
    }

    private Point? ComputeAvatarCenterPoint(FrameworkElement host, bool requireActiveSubtree)
    {
        try
        {
            if (requireActiveSubtree && !IsSubtreeVisibleToRoot(host, RootChromeGrid))
                return null;
            double w = host.ActualWidth > 0 ? host.ActualWidth : (double.IsNaN(host.Width) ? 0 : host.Width);
            double h = host.ActualHeight > 0 ? host.ActualHeight : (double.IsNaN(host.Height) ? 0 : host.Height);
            if (w <= 0) w = 30;
            if (h <= 0) h = 30;
            FrameworkElement? target = InteractionFxCanvas as FrameworkElement
                ?? RootChromeGrid as FrameworkElement
                ?? OuterFrame as FrameworkElement;
            if (target == null) return new Point(w / 2.0, h / 2.0);
            var p = host.TranslatePoint(new Point(w / 2.0, h / 2.0), target);
            if (double.IsNaN(p.X) || double.IsNaN(p.Y)) return new Point(w / 2.0, h / 2.0);
            return p;
        }
        catch
        {
            double w = host.ActualWidth > 0 ? host.ActualWidth : 30;
            double h = host.ActualHeight > 0 ? host.ActualHeight : 30;
            return new Point(w / 2.0, h / 2.0);
        }
    }

    private void LaunchBezierFlight(Point from, Point to, string label, string glowHex, string pulseTargetAgentId)
    {
        if (InteractionFxCanvas == null) return;
        SoundFxService.Play(_reg.UserConfig, SoundFxService.SoundKind.DispatchFlight);
        if (AppPaths.IsIsolatedTest)
        {
            LaunchBezierFlightIsolatedFast(from, to, label, glowHex, pulseTargetAgentId);
            return;
        }

        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double winW = ActualWidth > 40 ? ActualWidth : Math.Max(UltraWidthDefault, Width);
        double winH = ActualHeight > 40 ? ActualHeight : Math.Max(80, Height > 40 ? Height : 120);
        double padX = Math.Max(16, Math.Min(28, winW * 0.08));
        double maxMidX = Math.Max(padX + 8, winW - padX);
        double maxMidY = Math.Max(12, winH - 12);
        Point mid;
        if (Math.Abs(dx) < 60)
        {
            double sideLift = Math.Min(Math.Max(36, winW * 0.28), Math.Max(24, maxMidX - Math.Max(from.X, to.X)));
            double midX = Math.Clamp(Math.Max(from.X, to.X) + sideLift, padX, maxMidX);
            mid = new Point(midX, Math.Clamp((from.Y + to.Y) / 2.0 - 12, 10, maxMidY));
        }
        else
        {
            double lift = Math.Min(Math.Max(28, Math.Abs(dx) * 0.36), Math.Max(24, winH * 0.35));
            double midY = Math.Min(from.Y, to.Y) - lift;
            if (midY < 8) midY = Math.Min(maxMidY, Math.Max(from.Y, to.Y) + lift * 0.55);
            mid = new Point(Math.Clamp((from.X + to.X) / 2.0, padX, maxMidX), Math.Clamp(midY, 8, maxMidY));
        }
        const double durationMs = 1850;
        _ = dy;

        var arcOuter = CreateFlightArcPath(from, mid, to, Hex(WithAlphaHex(glowHex, 0x66)), 6, null);
        var arcInner = CreateFlightArcPath(from, mid, to, Hex(glowHex), 2.2, new DoubleCollection { 6, 4 });
        InteractionFxCanvas.Children.Add(arcOuter);
        InteractionFxCanvas.Children.Add(arcInner);
        var arcIn = new DoubleAnimation(0, 0.85, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        arcOuter.BeginAnimation(UIElement.OpacityProperty, arcIn);
        arcInner.BeginAnimation(UIElement.OpacityProperty, arcIn);

        var launchRing = new Ellipse
        {
            Width = 24,
            Height = 24,
            Stroke = Hex(glowHex),
            StrokeThickness = 2.2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        Canvas.SetLeft(launchRing, from.X - 12);
        Canvas.SetTop(launchRing, from.Y - 12);
        var launchScale = new ScaleTransform(1, 1);
        launchRing.RenderTransform = launchScale;
        InteractionFxCanvas.Children.Add(launchRing);
        var launchGrow = new DoubleAnimation(1, 62.0 / 24.0, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var launchFade = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(420));
        launchFade.Completed += (_, _) => InteractionFxCanvas.Children.Remove(launchRing);
        launchScale.BeginAnimation(ScaleTransform.ScaleXProperty, launchGrow);
        launchScale.BeginAnimation(ScaleTransform.ScaleYProperty, launchGrow);
        launchRing.BeginAnimation(UIElement.OpacityProperty, launchFade);

        var trailDots = new List<Ellipse>();
        for (int i = 0; i < 10; i++)
        {
            double sz = 14.0 - i * (10.0 / 9.0);
            double peak = 0.95 - i * (0.75 / 9.0);
            var dot = new Ellipse
            {
                Width = sz,
                Height = sz,
                Fill = Hex(WithAlphaHex(glowHex, (byte)Math.Clamp(peak * 255, 40, 255))),
                UseLayoutRounding = false,
                SnapsToDevicePixels = false,
                IsHitTestVisible = false,
                Opacity = 0.0
            };
            Canvas.SetLeft(dot, -sz / 2.0);
            Canvas.SetTop(dot, -sz / 2.0);
            var dotTt = new TranslateTransform(from.X, from.Y);
            dot.RenderTransform = dotTt;
            InteractionFxCanvas.Children.Add(dot);
            trailDots.Add(dot);

            var delay = TimeSpan.FromMilliseconds(i * 32);
            dotTt.BeginAnimation(TranslateTransform.XProperty, BuildSmoothBezierTrack(from.X, mid.X, to.X, durationMs, delay));
            dotTt.BeginAnimation(TranslateTransform.YProperty, BuildSmoothBezierTrack(from.Y, mid.Y, to.Y, durationMs, delay));
            var opAnim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(durationMs),
                BeginTime = delay
            };
            opAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0)));
            opAnim.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(0.12)));
            opAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0)));
            dot.BeginAnimation(UIElement.OpacityProperty, opAnim);
        }

        var halo = new Ellipse
        {
            Width = 56,
            Height = 56,
            Fill = Hex(WithAlphaHex(glowHex, 0x44)),
            Opacity = 0.9,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var core = new Ellipse
        {
            Width = 34,
            Height = 34,
            Fill = Hex(WithAlphaHex(glowHex, 0xD0)),
            IsHitTestVisible = false,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false
        };
        var badgeText = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = Hex("#FFFFFF")
        };
        var badge = new Border
        {
            Background = Hex("#F2152033"),
            BorderBrush = Hex(glowHex),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            Opacity = 1,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Child = badgeText
        };
        FitFlightBadge(badge, badgeText, winW);

        var host = new Canvas
        {
            Width = 1,
            Height = 1,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
            IsHitTestVisible = false
        };
        host.Children.Add(halo);
        host.Children.Add(core);
        host.Children.Add(badge);
        Canvas.SetLeft(halo, -28);
        Canvas.SetTop(halo, -28);
        Canvas.SetLeft(core, -17);
        Canvas.SetTop(core, -17);
        Canvas.SetTop(badge, -16);
        InteractionFxCanvas.Children.Add(host);

        var tt = new TranslateTransform(from.X, from.Y);
        host.RenderTransform = tt;
        var badgeScale = new ScaleTransform(0.85, 0.85);
        badge.RenderTransform = badgeScale;
        var haloScale = new ScaleTransform(1, 1);
        halo.RenderTransform = haloScale;

        var breath = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(durationMs) };
        breath.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromPercent(0)));
        breath.KeyFrames.Add(new EasingDoubleKeyFrame(1.32, KeyTime.FromPercent(0.42)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        breath.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromPercent(1)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        var haloPulse = new DoubleAnimation(0.86, 1.16, TimeSpan.FromMilliseconds(460))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        var animX = BuildSmoothBezierTrack(from.X, mid.X, to.X, durationMs, TimeSpan.Zero);
        var animY = BuildSmoothBezierTrack(from.Y, mid.Y, to.Y, durationMs, TimeSpan.Zero);
        animY.Completed += (_, _) =>
        {
            var fade = new DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(320));
            fade.Completed += (_, _) =>
            {
                InteractionFxCanvas.Children.Remove(arcOuter);
                InteractionFxCanvas.Children.Remove(arcInner);
                InteractionFxCanvas.Children.Remove(host);
                InteractionFxCanvas.Children.Remove(launchRing);
                foreach (var d in trailDots) InteractionFxCanvas.Children.Remove(d);
                PulseAvatarRing(pulseTargetAgentId, glowHex);
                SoundFxService.Play(_reg.UserConfig, SoundFxService.SoundKind.StatusChange);
            };
            arcOuter.BeginAnimation(UIElement.OpacityProperty, fade);
            arcInner.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        tt.BeginAnimation(TranslateTransform.XProperty, animX);
        tt.BeginAnimation(TranslateTransform.YProperty, animY);
        badgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
        badgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, breath);
        haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, haloPulse);
        haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, haloPulse);
    }

    /// <summary>离屏自检沿用原 720ms 轻量轨迹，不追加航迹、彗尾和礼花，Anim 计数保持一次触发。</summary>
    private void LaunchBezierFlightIsolatedFast(Point from, Point to, string label, string glowHex, string pulseTargetAgentId)
    {
        double winW = ActualWidth > 40 ? ActualWidth : Math.Max(UltraWidthDefault, Width);
        double winH = ActualHeight > 40 ? ActualHeight : Math.Max(80, Height > 40 ? Height : 120);
        double padX = Math.Max(14, Math.Min(24, winW * 0.08));
        double midX = Math.Clamp((from.X + to.X) / 2.0, padX, Math.Max(padX + 8, winW - padX));
        double lift = Math.Min(Math.Max(22, Math.Abs(to.X - from.X) * 0.18), Math.Max(18, winH * 0.28));
        double midY = Math.Clamp(Math.Min(from.Y, to.Y) - lift, 8, Math.Max(12, winH - 12));
        var mid = new Point(midX, midY);
        const double durationMs = 720;

        // 层次 1：3 颗渐缩彗星拖尾粒子（沿连续二次贝塞尔曲线原生 VSync 丝滑追随）
        var trailDots = new List<Ellipse>();
        for (int i = 3; i >= 1; i--)
        {
            double sz = 3.0 + (4 - i) * 1.8;
            var dot = new Ellipse
            {
                Width = sz,
                Height = sz,
                Fill = Hex(WithAlphaHex(glowHex, (byte)(0xB0 - i * 0x26))),
                UseLayoutRounding = false,
                SnapsToDevicePixels = false,
                IsHitTestVisible = false,
                Opacity = 0.0
            };
            Canvas.SetLeft(dot, -sz / 2.0);
            Canvas.SetTop(dot, -sz / 2.0);
            var dotTt = new TranslateTransform(from.X, from.Y);
            dot.RenderTransform = dotTt;
            InteractionFxCanvas.Children.Add(dot);
            trailDots.Add(dot);

            if (!AppPaths.IsIsolatedTest)
            {
                var delay = TimeSpan.FromMilliseconds(i * 38);
                var dx = BuildSmoothBezierTrack(from.X, mid.X, to.X, durationMs, delay);
                var dy = BuildSmoothBezierTrack(from.Y, mid.Y, to.Y, durationMs, delay);
                var opAnim = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    BeginTime = delay
                };
                opAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0)));
                opAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.84 - i * 0.16, KeyTime.FromPercent(0.18)));
                opAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0)));
                dotTt.BeginAnimation(TranslateTransform.XProperty, dx);
                dotTt.BeginAnimation(TranslateTransform.YProperty, dy);
                dot.BeginAnimation(UIElement.OpacityProperty, opAnim);
            }
        }

        // 层次 2：柔和光晕底座
        var halo = new Ellipse
        {
            Width = 28,
            Height = 28,
            Fill = Hex(WithAlphaHex(glowHex, 0x48)),
            Opacity = 0.72,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
            IsHitTestVisible = false
        };
        // 层次 3：清晰高对比圆角指令胶囊
        var badgeText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Hex("#FFFFFF")
        };
        var badge = new Border
        {
            Background = Hex("#F2152033"),
            BorderBrush = Hex(glowHex),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Opacity = 0.98,
            IsHitTestVisible = false,
            Child = badgeText
        };
        FitFlightBadge(badge, badgeText, winW);

        var host = new Canvas
        {
            Width = 1,
            Height = 1,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
            IsHitTestVisible = false
        };
        host.Children.Add(halo);
        host.Children.Add(badge);
        Canvas.SetLeft(halo, -14);
        Canvas.SetTop(halo, -14);
        Canvas.SetTop(badge, -12);
        InteractionFxCanvas.Children.Add(host);

        var tt = new TranslateTransform(from.X, from.Y);
        host.RenderTransform = tt;
        var scale = new ScaleTransform(0.92, 0.92);
        badge.RenderTransform = scale;
        badge.RenderTransformOrigin = new Point(0.5, 0.5);

        var animX = BuildSmoothBezierTrack(from.X, mid.X, to.X, durationMs, TimeSpan.Zero);
        var animY = BuildSmoothBezierTrack(from.Y, mid.Y, to.Y, durationMs, TimeSpan.Zero);
        var breath = new DoubleAnimation(0.92, 1.12, TimeSpan.FromMilliseconds(durationMs / 2))
        {
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var haloFade = new DoubleAnimation(0.72, 0.08, TimeSpan.FromMilliseconds(durationMs));

        animY.Completed += (_, _) =>
        {
            InteractionFxCanvas.Children.Remove(host);
            foreach (var d in trailDots) InteractionFxCanvas.Children.Remove(d);
            PulseAvatarRing(pulseTargetAgentId, glowHex);
            SoundFxService.Play(_reg.UserConfig, SoundFxService.SoundKind.StatusChange);
        };
        tt.BeginAnimation(TranslateTransform.XProperty, animX);
        tt.BeginAnimation(TranslateTransform.YProperty, animY);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, breath);
        halo.BeginAnimation(UIElement.OpacityProperty, haloFade);
    }

    /// <summary>飞行胶囊最大宽度随窗口收紧，单行省略，避免撑破或裁出悬浮窗。</summary>
    private static void FitFlightBadge(Border badge, TextBlock text, double windowW)
    {
        bool ultra = windowW < 320;
        double maxW = ultra
            ? Math.Clamp(windowW * 0.78, 96, 168)
            : Math.Clamp(windowW * 0.56, 128, 236);
        badge.MaxWidth = maxW;
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.MaxWidth = Math.Max(64, maxW - 22);
        Canvas.SetLeft(badge, -maxW / 2.0);
    }

    /// <summary>生成无停顿连续二次贝塞尔曲线轨迹（余弦缓入缓出，原生显示器 VSync 满帧运行）。</summary>
    private static DoubleAnimationUsingKeyFrames BuildSmoothBezierTrack(
        double p0, double p1, double p2, double durationMs, TimeSpan beginTime)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            BeginTime = beginTime
        };
        const int samples = 24;
        for (int s = 0; s <= samples; s++)
        {
            double pct = (double)s / samples;
            double u = 0.5 - 0.5 * Math.Cos(Math.PI * pct);
            double inv = 1.0 - u;
            double val = inv * inv * p0 + 2.0 * inv * u * p1 + u * u * p2;
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(val, KeyTime.FromPercent(pct)));
        }
        return anim;
    }

    private static Path CreateFlightArcPath(Point from, Point mid, Point to, Brush stroke, double thickness, DoubleCollection? dash)
    {
        var fig = new PathFigure { StartPoint = from, IsFilled = false };
        fig.Segments.Add(new QuadraticBezierSegment(mid, to, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return new Path
        {
            Data = geo,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashArray = dash,
            Fill = Brushes.Transparent,
            Opacity = 0,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false
        };
    }

    private void PulseAvatarRing(string agentId, string glowHex)
    {
        if (AppPaths.IsIsolatedTest)
        {
            PulseAvatarRingIsolatedFast(agentId, glowHex);
            return;
        }
        if (!_avatarHostsByAgent.TryGetValue(agentId, out var host) || host == null || InteractionFxCanvas == null) return;
        var center = ResolveAvatarCenter(agentId) ?? new Point(40, 40);
        for (int wave = 0; wave < 3; wave++)
        {
            double baseSz = Math.Max(26, host.ActualWidth + 8 - wave * 3);
            var ripple = new Ellipse
            {
                Width = baseSz,
                Height = baseSz,
                Stroke = Hex(glowHex),
                StrokeThickness = 2.8,
                Fill = Brushes.Transparent,
                UseLayoutRounding = false,
                SnapsToDevicePixels = false,
                IsHitTestVisible = false,
                Opacity = 0.96 - wave * 0.12,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            InteractionFxCanvas.Children.Add(ripple);
            Canvas.SetLeft(ripple, center.X - ripple.Width / 2);
            Canvas.SetTop(ripple, center.Y - ripple.Height / 2);

            var delay = TimeSpan.FromMilliseconds(wave * 90);
            var scale = new ScaleTransform(1, 1);
            ripple.RenderTransform = scale;
            var animScale = new DoubleAnimation(1, 2.4, TimeSpan.FromMilliseconds(820))
            {
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var animThick = new DoubleAnimation(2.8, 1.4, TimeSpan.FromMilliseconds(820))
            {
                BeginTime = delay,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var animOpacity = new DoubleAnimation(ripple.Opacity, 0, TimeSpan.FromMilliseconds(820))
            {
                BeginTime = delay,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            animOpacity.Completed += (_, _) => InteractionFxCanvas.Children.Remove(ripple);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
            ripple.BeginAnimation(Shape.StrokeThicknessProperty, animThick);
            ripple.BeginAnimation(OpacityProperty, animOpacity);
        }

        for (int i = 0; i < 6; i++)
        {
            double ang = i * Math.PI / 3.0;
            double dist = 28 + (i % 3) * 7;
            var star = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = Hex(glowHex),
                IsHitTestVisible = false,
                UseLayoutRounding = false,
                SnapsToDevicePixels = false,
                Opacity = 0.95
            };
            Canvas.SetLeft(star, -3);
            Canvas.SetTop(star, -3);
            var starTt = new TranslateTransform(center.X, center.Y);
            star.RenderTransform = starTt;
            InteractionFxCanvas.Children.Add(star);
            var moveX = new DoubleAnimation(center.X, center.X + Math.Cos(ang) * dist, TimeSpan.FromMilliseconds(520))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var moveY = new DoubleAnimation(center.Y, center.Y + Math.Sin(ang) * dist, TimeSpan.FromMilliseconds(520))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var starFade = new DoubleAnimation(0.95, 0, TimeSpan.FromMilliseconds(520));
            starFade.Completed += (_, _) => InteractionFxCanvas.Children.Remove(star);
            starTt.BeginAnimation(TranslateTransform.XProperty, moveX);
            starTt.BeginAnimation(TranslateTransform.YProperty, moveY);
            star.BeginAnimation(UIElement.OpacityProperty, starFade);
        }
    }

    private void PulseAvatarRingIsolatedFast(string agentId, string glowHex)
    {
        if (!_avatarHostsByAgent.TryGetValue(agentId, out var host) || host == null) return;
        var center = ResolveAvatarCenter(agentId) ?? new Point(40, 40);
        for (int wave = 0; wave < 2; wave++)
        {
            double baseSz = Math.Max(22, host.ActualWidth + 4 - wave * 4);
            var ripple = new Ellipse
            {
                Width = baseSz,
                Height = baseSz,
                Stroke = Hex(glowHex),
                StrokeThickness = wave == 0 ? 2.2 : 1.4,
                Fill = Brushes.Transparent,
                UseLayoutRounding = false,
                SnapsToDevicePixels = false,
                IsHitTestVisible = false,
                Opacity = wave == 0 ? 0.92 : 0.72
            };
            InteractionFxCanvas.Children.Add(ripple);
            Canvas.SetLeft(ripple, center.X - ripple.Width / 2);
            Canvas.SetTop(ripple, center.Y - ripple.Height / 2);

            var delay = TimeSpan.FromMilliseconds(wave * 110);
            var scale = new ScaleTransform(1, 1, ripple.Width / 2, ripple.Height / 2);
            ripple.RenderTransform = scale;
            var animScale = new DoubleAnimation(1, wave == 0 ? 1.78 : 1.52, TimeSpan.FromMilliseconds(520))
            {
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var animOpacity = new DoubleAnimation(ripple.Opacity, 0, TimeSpan.FromMilliseconds(520))
            {
                BeginTime = delay,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            animOpacity.Completed += (_, _) => InteractionFxCanvas.Children.Remove(ripple);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
            ripple.BeginAnimation(OpacityProperty, animOpacity);
        }
    }

    private void ShowVerificationCelebration(string taskTitle, string masterAgentId, string workerAgentId)
    {
        _verifyBannerCount++;
        string shortTitle = string.IsNullOrWhiteSpace(taskTitle) ? "任务" : taskTitle.Trim();
        if (shortTitle.Length > 24) shortTitle = shortTitle[..24] + "…";
        VerifyCelebrationText.Text = $"✅ 主脑验收通过 · {shortTitle} 已达标交付";
        SoundFxService.Play(_reg.UserConfig, SoundFxService.SoundKind.VerifyPassed);
        if (_reg.UserConfig.FloatingWindowCollapsed && CapsuleBriefText != null)
        {
            string celebrateTip = $"✅ 主脑验收通过 · {shortTitle}";
            CapsuleBriefText.Text = celebrateTip;
            CapsuleBriefText.Foreground = Hex("#ECFDF5");
            CollapsedMiniCapsule.ToolTip = celebrateTip;
        }

        double bannerW = ActualWidth > 40 ? ActualWidth : Width;
        VerifyCelebrationBanner.Width = Math.Clamp(bannerW, Math.Min(196, bannerW), 860);
        VerifyCelebrationBanner.Visibility = Visibility.Visible;
        VerifyCelebrationBanner.Opacity = 0;
        if (!AppPaths.IsIsolatedTest && VerifyCelebrationPopup != null)
        {
            VerifyCelebrationPopup.IsOpen = true;
        }
        var slideY = new TranslateTransform(0, -8);
        VerifyCelebrationBanner.RenderTransform = slideY;
        var slide = new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240));
        slideY.BeginAnimation(TranslateTransform.YProperty, slide);
        VerifyCelebrationBanner.BeginAnimation(OpacityProperty, fadeIn);

        // 外框翠绿流光呼吸（仅播放 1 次后自动回到稳态边框）
        var glowBrush = new SolidColorBrush(((SolidColorBrush)Hex("#10B981")).Color);
        OuterFrame.BorderBrush = glowBrush;
        var breath = new ColorAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(4.5),
            RepeatBehavior = new RepeatBehavior(1)
        };
        breath.KeyFrames.Add(new EasingColorKeyFrame(Color.FromRgb(0x34, 0xD3, 0x99), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
        breath.KeyFrames.Add(new EasingColorKeyFrame(Color.FromRgb(0x6E, 0xE7, 0xB7), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.1))));
        breath.KeyFrames.Add(new EasingColorKeyFrame(Color.FromRgb(0x10, 0xB9, 0x81), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.2))));
        breath.KeyFrames.Add(new EasingColorKeyFrame(Color.FromRgb(0x34, 0xD3, 0x99), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.3))));
        breath.KeyFrames.Add(new EasingColorKeyFrame(Color.FromRgb(0x2A, 0x3F, 0x5F), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.5))));
        glowBrush.BeginAnimation(SolidColorBrush.ColorProperty, breath);

        PulseAvatarRing(masterAgentId, "#34D399");
        PulseAvatarRing(workerAgentId, "#34D399");

        _verifyBannerTimer?.Stop();
        _verifyBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _verifyBannerTimer.Tick += (_, _) =>
        {
            _verifyBannerTimer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (_, _) =>
            {
                VerifyCelebrationBanner.Visibility = Visibility.Collapsed;
                if (VerifyCelebrationPopup != null) VerifyCelebrationPopup.IsOpen = false;
                ApplyThemeVisual();
            };
            VerifyCelebrationBanner.BeginAnimation(OpacityProperty, fadeOut);
        };
        _verifyBannerTimer.Start();
    }
}
