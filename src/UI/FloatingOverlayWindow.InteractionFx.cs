using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
    private double _verifyRemainSec;
    private double _verifyTotalSec = 8;
    private bool _verifyBannerHoverPaused;
    private bool _verifyBubbleDismissArmed;
    private int _verifyBubbleEpoch;
    private bool _bubbleAnchoredBelow = true;
    private double _bubbleTailLeft = 154;
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
    public int InteractionFxChildCountForTest => InteractionFxCanvas?.Children.Count ?? 0;

    /// <summary>最近一次飞行的安全区几何（快速路径清理画布后仍可断言）。</summary>
    public readonly struct FlightGeometrySample
    {
        public double FromX { get; init; }
        public double FromY { get; init; }
        public double MidX { get; init; }
        public double MidY { get; init; }
        public double ToX { get; init; }
        public double ToY { get; init; }
        public double WinW { get; init; }
        public double WinH { get; init; }
        public double MaxRingDiameter { get; init; }
        public bool CompactMode { get; init; }
        public bool WithinSafeZone { get; init; }
    }

    public FlightGeometrySample LastFlightGeometryForTest { get; private set; }

    /// <summary>
    /// 距离自适应飞行耗时：短距不再拖到 1850ms，长距仍保持可跟随的上限。
    /// </summary>
    public static double ComputeAdaptiveFlightDurationMs(double distancePx, bool compactMode)
    {
        double dist = double.IsFinite(distancePx) ? Math.Max(0, distancePx) : 0;
        return Math.Clamp((compactMode ? 620.0 : 660.0) + dist * 2.0, 680.0, 1280.0);
    }

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

    /// <summary>离屏自检：触发飞行并保留路径几何，供安全区与残留断言。</summary>
    public int TriggerFlightForTest(string kind, string fromAgentId, string toAgentId, string? taskSummary = null)
        => TriggerInteractionAnimationForTest(kind, fromAgentId, toAgentId, taskSummary);

    /// <summary>
    /// 飞行胶囊文案：有精简任务名时显示「图标 + 任务名」（按窗口宽度截断），否则回退固定单名。
    /// </summary>
    public static string FormatFlightBadgeText(string? kind, string? taskSummary, bool ultraNarrow = false)
    {
        string k = (kind ?? "").Trim().ToLowerInvariant();
        string fallback = k switch
        {
            "deliver" or "delivery" or "master_verifying" or "verify" => "📦 交付包",
            "rework" or "reworking" => "🔧 重修单",
            "relay" or "handover" => "⚡ 接力",
            "verified_delivered" or "verified" => "✅ 已交付",
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
            case "verify":
            case "master_verifying":
                glow = "#A78BFA";
                break;
            case "rework":
            case "reworking":
                glow = "#F59E0B";
                break;
            case "verified_delivered":
            case "verified":
            case "done":
                glow = "#34D399";
                break;
            case "relay":
            case "handover":
                glow = "#22D3EE";
                break;
            default:
                glow = "#38BDF8";
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

    /// <summary>离屏自检：触发主脑验收通过气泡与外框流光。detailSummary 缺省时标题与正文仍分开写。</summary>
    public bool TriggerVerifyBannerForTest(string taskTitle, string? masterAgentId = null, string? workerAgentId = null, string? detailSummary = null)
    {
        EnsureShownForFx();
        ShowVerificationCelebration(taskTitle, masterAgentId ?? "antigravity", workerAgentId ?? "cursor", detailSummary);
        return VerifyCelebrationBanner.Visibility == Visibility.Visible || _verifyBannerCount > 0;
    }

    /// <summary>设置页试看：标题与正文分离的完成气泡。</summary>
    public void PreviewCompletionBubble()
    {
        EnsureShownForFx();
        ShowVerificationCelebration("悬浮窗气泡样式预览", "antigravity", "cursor", "已适配折叠胶囊与全部展开样式");
    }

    /// <summary>截图前把气泡定格为完全可见，避免入场 Opacity=0 拍成空图。</summary>
    public void SnapCompletionBubbleFullyVisibleForCapture()
    {
        _verifyBubbleDismissArmed = false;
        _verifyBannerHoverPaused = true;
        try { _verifyBannerTimer?.Stop(); } catch { }
        if (VerifyCelebrationBanner != null)
        {
            VerifyCelebrationBanner.BeginAnimation(OpacityProperty, null);
            VerifyCelebrationBanner.Opacity = 1;
            VerifyCelebrationBanner.Visibility = Visibility.Visible;
            if (VerifyCelebrationBanner.RenderTransform is TransformGroup group)
            {
                foreach (var child in group.Children)
                {
                    if (child is TranslateTransform tt)
                    {
                        tt.BeginAnimation(TranslateTransform.YProperty, null);
                        tt.Y = 0;
                    }
                    else if (child is ScaleTransform sc)
                    {
                        sc.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        sc.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        sc.ScaleX = 1;
                        sc.ScaleY = 1;
                    }
                }
            }
        }
        if (VerifyBubbleProgressScale != null)
        {
            VerifyBubbleProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            VerifyBubbleProgressScale.ScaleX = 0.64;
        }
        if (VerifyBubbleTailTop != null) VerifyBubbleTailTop.Visibility = Visibility.Visible;
        if (VerifyBubbleTailBottom != null) VerifyBubbleTailBottom.Visibility = Visibility.Collapsed;
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
            _verifyBubbleDismissArmed = false;
            _verifyBannerHoverPaused = false;
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
                        PlayInteractionKind("verified_delivered", ag.Id, masterId, lv?.TaskSummary);
                    }
                    string? detail = string.IsNullOrWhiteSpace(lv?.VerificationSummary) ? null : lv!.VerificationSummary;
                    ShowVerificationCelebration(ExtractConciseTaskTitle(lv?.TaskSummary), masterId, ag.Id, detail);
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

    public void LaunchBezierFlight(Point from, Point to, string label, string glowHex, string pulseTargetAgentId)
    {
        if (InteractionFxCanvas == null) return;
        SoundFxService.Play(_reg.UserConfig, SoundFxService.SoundKind.DispatchFlight);
        if (AppPaths.IsIsolatedTest)
        {
            LaunchBezierFlightIsolatedFast(from, to, label, glowHex, pulseTargetAgentId);
            return;
        }

        var plan = BuildFlightPlan(from, to, glowHex);
        RememberFlightGeometry(plan);
        double durationMs = plan.DurationMs;
        var spawned = new List<UIElement>();

        var arcOuter = CreateFlightArcPath(plan.From, plan.Mid, plan.To, CreateFlightStrokeBrush(plan.StartHex, plan.EndHex, 0x55), plan.Tight ? 3.0 : 4.2, null);
        var arcInner = CreateFlightArcPath(plan.From, plan.Mid, plan.To, CreateFlightStrokeBrush(plan.StartHex, plan.EndHex, 0xE8), 1.5, new DoubleCollection { 7, 9 });
        arcOuter.Opacity = 0;
        arcInner.Opacity = 0;
        InteractionFxCanvas.Children.Add(arcOuter);
        InteractionFxCanvas.Children.Add(arcInner);
        spawned.Add(arcOuter);
        spawned.Add(arcInner);

        var arcIn = new DoubleAnimation(0, 0.92, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
        };
        arcOuter.BeginAnimation(UIElement.OpacityProperty, arcIn);
        arcInner.BeginAnimation(UIElement.OpacityProperty, arcIn);
        var flow = new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        arcInner.BeginAnimation(Shape.StrokeDashOffsetProperty, flow);

        SpawnLaunchRing(plan.From, plan, spawned, isolatedImmediate: false);

        for (int i = 0; i < 8; i++)
        {
            double sz = 9.2 - i * 0.85;
            double peak = 0.78 - i * 0.08;
            var dot = new Ellipse
            {
                Width = sz,
                Height = sz,
                Fill = CreateTrailParticleBrush(plan.StartHex, plan.EndHex, peak),
                UseLayoutRounding = false,
                SnapsToDevicePixels = false,
                IsHitTestVisible = false,
                Opacity = 0
            };
            Canvas.SetLeft(dot, -sz / 2.0);
            Canvas.SetTop(dot, -sz / 2.0);
            var dotTt = new TranslateTransform(plan.From.X, plan.From.Y);
            dot.RenderTransform = dotTt;
            InteractionFxCanvas.Children.Add(dot);
            spawned.Add(dot);

            var delay = TimeSpan.FromMilliseconds(i * 28);
            dotTt.BeginAnimation(TranslateTransform.XProperty, BuildSmoothBezierTrack(plan.From.X, plan.Mid.X, plan.To.X, durationMs, delay));
            dotTt.BeginAnimation(TranslateTransform.YProperty, BuildSmoothBezierTrack(plan.From.Y, plan.Mid.Y, plan.To.Y, durationMs, delay));
            var opAnim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(durationMs),
                BeginTime = delay
            };
            opAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0)));
            opAnim.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(0.14)));
            opAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0)));
            dot.BeginAnimation(UIElement.OpacityProperty, opAnim);
        }

        var host = BuildCometHost(plan, label, out var badge, out var halo);
        InteractionFxCanvas.Children.Add(host);
        spawned.Add(host);

        var tt = new TranslateTransform(plan.From.X, plan.From.Y);
        host.RenderTransform = tt;
        var badgeScale = new ScaleTransform(0.94, 0.94);
        badge.RenderTransform = badgeScale;
        badge.RenderTransformOrigin = new Point(0.5, 0.5);
        var haloScale = new ScaleTransform(1, 1);
        halo.RenderTransform = haloScale;
        halo.RenderTransformOrigin = new Point(0.5, 0.5);

        var breath = new DoubleAnimation(0.96, 1.04, TimeSpan.FromMilliseconds(Math.Max(320, durationMs * 0.46)))
        {
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var haloPulse = new DoubleAnimation(0.9, 1.06, TimeSpan.FromMilliseconds(Math.Max(360, durationMs * 0.5)))
        {
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        var animX = BuildSmoothBezierTrack(plan.From.X, plan.Mid.X, plan.To.X, durationMs, TimeSpan.Zero);
        var animY = BuildSmoothBezierTrack(plan.From.Y, plan.Mid.Y, plan.To.Y, durationMs, TimeSpan.Zero);
        animY.Completed += (_, _) =>
        {
            var fadeEase = new SineEase { EasingMode = EasingMode.EaseInOut };
            var fadeInner = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = fadeEase
            };
            fadeInner.Completed += (_, _) =>
            {
                RemoveFlightVisuals(spawned);
                PulseAvatarRing(pulseTargetAgentId, glowHex);
                SoundFxService.Play(_reg.UserConfig, SoundFxService.SoundKind.StatusChange);
            };
            arcOuter.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            arcInner.BeginAnimation(UIElement.OpacityProperty, fadeInner);
            host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        };
        tt.BeginAnimation(TranslateTransform.XProperty, animX);
        tt.BeginAnimation(TranslateTransform.YProperty, animY);
        badgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
        badgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, breath);
        haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, haloPulse);
        haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, haloPulse);
    }

    /// <summary>离屏自检：同步生成安全区几何后立即拆掉临时视觉，避免 720ms 动画残留。</summary>
    private void LaunchBezierFlightIsolatedFast(Point from, Point to, string label, string glowHex, string pulseTargetAgentId)
    {
        if (InteractionFxCanvas == null) return;
        var plan = BuildFlightPlan(from, to, glowHex);
        RememberFlightGeometry(plan);
        var spawned = new List<UIElement>();
        try
        {
            var arcOuter = CreateFlightArcPath(plan.From, plan.Mid, plan.To, CreateFlightStrokeBrush(plan.StartHex, plan.EndHex, 0x55), plan.Tight ? 3.0 : 4.2, null);
            var arcInner = CreateFlightArcPath(plan.From, plan.Mid, plan.To, CreateFlightStrokeBrush(plan.StartHex, plan.EndHex, 0xE8), 1.5, new DoubleCollection { 7, 9 });
            InteractionFxCanvas.Children.Add(arcOuter);
            InteractionFxCanvas.Children.Add(arcInner);
            spawned.Add(arcOuter);
            spawned.Add(arcInner);
            SpawnLaunchRing(plan.From, plan, spawned, isolatedImmediate: true);
            for (int i = 0; i < 8; i++)
            {
                double sz = 9.2 - i * 0.85;
                var dot = new Ellipse
                {
                    Width = sz,
                    Height = sz,
                    Fill = CreateTrailParticleBrush(plan.StartHex, plan.EndHex, 0.7 - i * 0.07),
                    IsHitTestVisible = false,
                    Opacity = 0.4
                };
                Canvas.SetLeft(dot, plan.From.X - sz / 2.0);
                Canvas.SetTop(dot, plan.From.Y - sz / 2.0);
                InteractionFxCanvas.Children.Add(dot);
                spawned.Add(dot);
            }
            var host = BuildCometHost(plan, label, out _, out _);
            Canvas.SetLeft(host, plan.From.X);
            Canvas.SetTop(host, plan.From.Y);
            InteractionFxCanvas.Children.Add(host);
            spawned.Add(host);
        }
        finally
        {
            RemoveFlightVisuals(spawned);
        }
        PulseAvatarRing(pulseTargetAgentId, glowHex);
    }

    private readonly struct FlightPlan
    {
        public Point From { get; init; }
        public Point Mid { get; init; }
        public Point To { get; init; }
        public double WinW { get; init; }
        public double WinH { get; init; }
        public bool Tight { get; init; }
        public double Distance { get; init; }
        public double DurationMs { get; init; }
        public double MaxRing { get; init; }
        public bool WithinSafeZone { get; init; }
        public string StartHex { get; init; }
        public string EndHex { get; init; }
    }

    private void RememberFlightGeometry(FlightPlan plan)
    {
        LastFlightGeometryForTest = new FlightGeometrySample
        {
            FromX = plan.From.X,
            FromY = plan.From.Y,
            MidX = plan.Mid.X,
            MidY = plan.Mid.Y,
            ToX = plan.To.X,
            ToY = plan.To.Y,
            WinW = plan.WinW,
            WinH = plan.WinH,
            MaxRingDiameter = plan.MaxRing,
            CompactMode = plan.Tight,
            WithinSafeZone = plan.WithinSafeZone
        };
    }

    private (double WinW, double WinH) ReadFlightViewport()
    {
        double winW = ActualWidth > 40 ? ActualWidth : (Width > 40 ? Width : UltraWidthDefault);
        double winH;
        if (ActualHeight > 8) winH = ActualHeight;
        else if (!double.IsNaN(Height) && Height > 8) winH = Height;
        else if (_reg.UserConfig.FloatingWindowCollapsed) winH = 38;
        else winH = 120;
        return (Math.Max(48, winW), Math.Max(16, winH));
    }

    private FlightPlan BuildFlightPlan(Point from, Point to, string glowHex)
    {
        var (winW, winH) = ReadFlightViewport();
        bool tight = winH <= 64 || _reg.UserConfig.FloatingWindowCollapsed;
        var (startHex, endHex) = ResolveFlightGradient(glowHex);
        double cap = Math.Max(28, winH - 4);
        double preferred = Math.Min(cap, tight ? 30 : 46);
        double half = preferred / 2.0;
        double yLo = 4;
        double yHi = Math.Max(yLo + 1, winH - 4);
        double centerLo = Math.Max(yLo, Math.Min(yHi, half + 1));
        double centerHi = Math.Max(centerLo, Math.Min(yHi, winH - half - 1));
        double padX = tight ? 6 : Math.Max(12, Math.Min(28, winW * 0.06));
        double maxX = Math.Max(padX + 4, winW - padX);

        Point a = new Point(Math.Clamp(from.X, padX, maxX), Math.Clamp(from.Y, centerLo, centerHi));
        Point b = new Point(Math.Clamp(to.X, padX, maxX), Math.Clamp(to.Y, centerLo, centerHi));
        double dx = b.X - a.X;
        double lift = tight
            ? Math.Min(Math.Max(4, Math.Abs(dx) * 0.06), Math.Max(4, (centerHi - centerLo) * 0.45))
            : Math.Min(Math.Max(22, Math.Abs(dx) * 0.30), Math.Max(18, winH * 0.26));
        double midY = Math.Min(a.Y, b.Y) - lift;
        if (midY < yLo)
        {
            double down = Math.Max(a.Y, b.Y) + lift * 0.5;
            midY = down <= yHi ? down : (a.Y + b.Y) / 2.0;
        }
        double midX;
        if (Math.Abs(dx) < 48)
        {
            double side = tight ? Math.Min(16, winW * 0.12) : Math.Min(Math.Max(28, winW * 0.18), winW * 0.26);
            midX = Math.Clamp(Math.Max(a.X, b.X) + side * Math.Sign(dx == 0 ? 1 : dx), padX, maxX);
            midY = Math.Clamp((a.Y + b.Y) / 2.0, yLo, yHi);
        }
        else
        {
            midX = Math.Clamp((a.X + b.X) / 2.0, padX, maxX);
            midY = Math.Clamp(midY, yLo, yHi);
        }
        var mid = new Point(midX, midY);
        double dist = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        double maxRing = FitRingDiameter(b.Y, winH, preferred);
        bool zone = PointInVerticalSafeZone(a.Y, winH)
            && PointInVerticalSafeZone(mid.Y, winH)
            && PointInVerticalSafeZone(b.Y, winH)
            && maxRing <= cap + 0.01
            && maxRing > 0;
        return new FlightPlan
        {
            From = a,
            Mid = mid,
            To = b,
            WinW = winW,
            WinH = winH,
            Tight = tight,
            Distance = dist,
            DurationMs = ComputeAdaptiveFlightDurationMs(dist, tight),
            MaxRing = maxRing,
            WithinSafeZone = zone,
            StartHex = startHex,
            EndHex = endHex
        };
    }

    private static bool PointInVerticalSafeZone(double y, double winH)
    {
        double hi = Math.Max(4, winH - 4);
        return y >= 4 - 0.05 && y <= hi + 0.05;
    }

    private static double FitRingDiameter(double centerY, double winH, double desired)
    {
        double cap = Math.Max(28, winH - 4);
        double room = 2.0 * Math.Min(Math.Max(0, centerY - 1), Math.Max(0, winH - 1 - centerY));
        double fitted = Math.Min(Math.Min(desired, cap), Math.Max(10, room));
        return Math.Min(fitted, cap);
    }

    private static (string Start, string End) ResolveFlightGradient(string glowHex)
    {
        string key = (glowHex ?? "").Trim().ToUpperInvariant();
        return key switch
        {
            "#A78BFA" => ("#A78BFA", "#38BDF8"),
            "#F59E0B" => ("#F59E0B", "#FBBF24"),
            "#34D399" => ("#34D399", "#6EE7B7"),
            "#22D3EE" => ("#22D3EE", "#38BDF8"),
            _ => ("#38BDF8", "#818CF8")
        };
    }

    private Canvas BuildCometHost(FlightPlan plan, string label, out Border badge, out Ellipse halo)
    {
        bool tight = plan.Tight;
        double haloSz = tight ? 24 : 36;
        double coreSz = tight ? 7 : 10;
        halo = new Ellipse
        {
            Width = haloSz,
            Height = haloSz,
            Fill = CreateCometHaloBrush(plan.StartHex, plan.EndHex),
            IsHitTestVisible = false,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
            Opacity = 0.95
        };
        var core = new Ellipse
        {
            Width = coreSz,
            Height = coreSz,
            Fill = CreateCometCoreBrush(),
            IsHitTestVisible = false,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false
        };
        var badgeText = new TextBlock
        {
            Text = label,
            FontSize = tight ? 10.5 : 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Hex("#F8FAFC"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        badge = new Border
        {
            Background = Hex(tight ? "#D9111824" : "#EB111A2B"),
            BorderBrush = tight ? CreateFlightStrokeBrush(plan.StartHex, plan.EndHex, 0x99) : Hex("#66E2E8F0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(tight ? 8 : 10),
            Padding = tight ? new Thickness(6, 2, 6, 2) : new Thickness(8, 3, 8, 3),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Child = badgeText
        };
        if (!tight)
        {
            badge.Effect = new DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.5,
                Color = ParseFlightColor(plan.StartHex, 0xFF),
                RenderingBias = RenderingBias.Quality
            };
        }
        double minAx = Math.Min(plan.From.X, Math.Min(plan.Mid.X, plan.To.X));
        double maxAx = Math.Max(plan.From.X, Math.Max(plan.Mid.X, plan.To.X));
        FitFlightBadge(badge, badgeText, plan.WinW, minAx, maxAx, tight);
        badge.Measure(new Size(Math.Max(48, badge.MaxWidth), 40));
        double badgeH = badge.DesiredSize.Height;
        if (double.IsNaN(badgeH) || badgeH < 8) badgeH = tight ? 18 : 22;
        Canvas.SetTop(badge, ResolveBadgeTop(badgeH, plan.From.Y, plan.Mid.Y, plan.To.Y, plan.WinH));

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
        Canvas.SetLeft(halo, -haloSz / 2.0);
        Canvas.SetTop(halo, -haloSz / 2.0);
        Canvas.SetLeft(core, -coreSz / 2.0);
        Canvas.SetTop(core, -coreSz / 2.0);
        return host;
    }

    private static double ResolveBadgeTop(double badgeH, double fromY, double midY, double toY, double winH)
    {
        double top = -badgeH / 2.0;
        double minY = Math.Min(fromY, Math.Min(midY, toY));
        double maxY = Math.Max(fromY, Math.Max(midY, toY));
        if (minY + top < 2) top += 2 - (minY + top);
        if (maxY + top + badgeH > winH - 2) top -= (maxY + top + badgeH) - (winH - 2);
        if (minY + top < 1) top = 1 - minY;
        return top;
    }

    private void SpawnLaunchRing(Point at, FlightPlan plan, List<UIElement> spawned, bool isolatedImmediate)
    {
        if (InteractionFxCanvas == null) return;
        double baseSz = Math.Min(16, Math.Max(10, plan.MaxRing * 0.45));
        var ring = new Ellipse
        {
            Width = baseSz,
            Height = baseSz,
            Stroke = CreateFlightStrokeBrush(plan.StartHex, plan.EndHex, 0xDD),
            StrokeThickness = 1.6,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Opacity = 0.9
        };
        Canvas.SetLeft(ring, at.X - baseSz / 2.0);
        Canvas.SetTop(ring, at.Y - baseSz / 2.0);
        InteractionFxCanvas.Children.Add(ring);
        spawned.Add(ring);
        if (isolatedImmediate) return;
        double maxScale = Math.Max(1.05, plan.MaxRing / baseSz);
        var scale = new ScaleTransform(1, 1);
        ring.RenderTransform = scale;
        var grow = new DoubleAnimation(1, maxScale, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) =>
        {
            if (InteractionFxCanvas != null) InteractionFxCanvas.Children.Remove(ring);
            spawned.Remove(ring);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void RemoveFlightVisuals(List<UIElement> spawned)
    {
        if (InteractionFxCanvas == null) return;
        foreach (var el in spawned.ToArray())
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            InteractionFxCanvas.Children.Remove(el);
        }
        spawned.Clear();
    }

    /// <summary>飞行胶囊按实测宽度居中，并钳在窗口左右安全边内。</summary>
    private static void FitFlightBadge(Border badge, TextBlock text, double windowW, double minAnchorX, double maxAnchorX, bool tight)
    {
        double maxW = tight
            ? Math.Clamp(windowW - 8, 72, 168)
            : Math.Clamp(windowW * 0.56, 112, 236);
        maxW = Math.Min(maxW, Math.Max(72, windowW - 8));
        badge.MaxWidth = maxW;
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        double pad = badge.Padding.Left + badge.Padding.Right + badge.BorderThickness.Left + badge.BorderThickness.Right;
        text.MaxWidth = Math.Max(48, maxW - pad - 2);
        badge.Measure(new Size(maxW, 40));
        double actualW = badge.DesiredSize.Width;
        if (double.IsNaN(actualW) || actualW < 8) actualW = Math.Min(maxW, 96);
        actualW = Math.Min(actualW, maxW);
        double left = -actualW / 2.0;
        double minLeft = 3 - minAnchorX;
        double maxLeft = windowW - 3 - actualW - maxAnchorX;
        if (minLeft <= maxLeft) left = Math.Clamp(left, minLeft, maxLeft);
        else left = minLeft;
        Canvas.SetLeft(badge, left);
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
            StrokeLineJoin = PenLineJoin.Round,
            StrokeDashArray = dash,
            Fill = Brushes.Transparent,
            Opacity = 0,
            IsHitTestVisible = false,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false
        };
    }

    private static Color ParseFlightColor(string hex, byte alpha)
    {
        var clean = (hex ?? "").Trim().TrimStart('#');
        if (clean.Length == 8) clean = clean[2..];
        if (clean.Length != 6) clean = "38BDF8";
        byte r = byte.Parse(clean[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(clean.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(clean.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb(alpha, r, g, b);
    }

    private static LinearGradientBrush CreateFlightStrokeBrush(string startHex, string endHex, byte alpha)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        brush.GradientStops.Add(new GradientStop(ParseFlightColor(startHex, alpha), 0));
        brush.GradientStops.Add(new GradientStop(ParseFlightColor(endHex, alpha), 1));
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush CreateCometHaloBrush(string startHex, string endHex)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.38, 0.38),
            Center = new Point(0.45, 0.45),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(ParseFlightColor(startHex, 0x9C), 0));
        brush.GradientStops.Add(new GradientStop(ParseFlightColor(endHex, 0x48), 0.42));
        brush.GradientStops.Add(new GradientStop(ParseFlightColor(endHex, 0x00), 1));
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush CreateCometCoreBrush()
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.38, 0.32),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xF2, 0xF8, 0xFB, 0xFF), 0.28));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush CreateTrailParticleBrush(string startHex, string endHex, double peak)
    {
        byte a0 = (byte)Math.Clamp(peak * 210, 28, 210);
        byte a1 = (byte)Math.Clamp(peak * 90, 0, 120);
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.4, 0.4),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(ParseFlightColor(startHex, a0), 0));
        brush.GradientStops.Add(new GradientStop(ParseFlightColor(endHex, a1), 0.55));
        brush.GradientStops.Add(new GradientStop(ParseFlightColor(endHex, 0x00), 1));
        if (brush.CanFreeze) brush.Freeze();
        return brush;
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
        var (startHex, endHex) = ResolveFlightGradient(glowHex);
        var (_, winH) = ReadFlightViewport();
        double cap = Math.Max(28, winH - 4);
        double preferred = Math.Min(cap, winH <= 64 ? 30 : 46);
        double maxDiam = FitRingDiameter(center.Y, winH, preferred);
        center = new Point(center.X, Math.Clamp(center.Y, maxDiam / 2.0 + 1, Math.Max(maxDiam / 2.0 + 1, winH - maxDiam / 2.0 - 1)));
        for (int wave = 0; wave < 2; wave++)
        {
            double baseSz = Math.Max(12, maxDiam * (wave == 0 ? 0.46 : 0.36));
            double scaleTo = Math.Max(1.04, maxDiam / baseSz);
            var ripple = new Ellipse
            {
                Width = baseSz,
                Height = baseSz,
                Stroke = CreateFlightStrokeBrush(startHex, endHex, wave == 0 ? (byte)0xDD : (byte)0x99),
                StrokeThickness = 1.6,
                Fill = Brushes.Transparent,
                UseLayoutRounding = false,
                SnapsToDevicePixels = false,
                IsHitTestVisible = false,
                Opacity = wave == 0 ? 0.9 : 0.62,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            InteractionFxCanvas.Children.Add(ripple);
            Canvas.SetLeft(ripple, center.X - baseSz / 2.0);
            Canvas.SetTop(ripple, center.Y - baseSz / 2.0);
            var delay = TimeSpan.FromMilliseconds(wave * 90);
            var scale = new ScaleTransform(1, 1);
            ripple.RenderTransform = scale;
            var animScale = new DoubleAnimation(1, scaleTo, TimeSpan.FromMilliseconds(640))
            {
                BeginTime = delay,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            var animOpacity = new DoubleAnimation(ripple.Opacity, 0, TimeSpan.FromMilliseconds(640))
            {
                BeginTime = delay,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            animOpacity.Completed += (_, _) => InteractionFxCanvas.Children.Remove(ripple);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
            ripple.BeginAnimation(OpacityProperty, animOpacity);
        }
    }

    private void PulseAvatarRingIsolatedFast(string agentId, string glowHex)
    {
        if (InteractionFxCanvas == null) return;
        var (startHex, endHex) = ResolveFlightGradient(glowHex);
        var (_, winH) = ReadFlightViewport();
        Point center = new Point(winH / 2.0, winH / 2.0);
        if (_avatarHostsByAgent.TryGetValue(agentId, out var host) && host != null)
        {
            var resolved = ResolveAvatarCenter(agentId);
            if (resolved.HasValue) center = resolved.Value;
        }
        double cap = Math.Max(28, winH - 4);
        double preferred = Math.Min(cap, winH <= 64 ? 30 : 46);
        double maxDiam = FitRingDiameter(center.Y, winH, preferred);
        if (LastFlightGeometryForTest.MaxRingDiameter <= 0 || LastFlightGeometryForTest.MaxRingDiameter > cap + 0.01)
        {
            var prev = LastFlightGeometryForTest;
            LastFlightGeometryForTest = new FlightGeometrySample
            {
                FromX = prev.FromX,
                FromY = prev.FromY,
                MidX = prev.MidX,
                MidY = prev.MidY,
                ToX = prev.ToX,
                ToY = prev.ToY,
                WinW = prev.WinW,
                WinH = prev.WinH > 8 ? prev.WinH : winH,
                MaxRingDiameter = maxDiam,
                CompactMode = prev.CompactMode,
                WithinSafeZone = prev.WithinSafeZone && maxDiam <= cap + 0.01
            };
        }
        var spawned = new List<UIElement>();
        try
        {
            for (int wave = 0; wave < 2; wave++)
            {
                double baseSz = Math.Max(12, maxDiam * (wave == 0 ? 0.46 : 0.36));
                var ripple = new Ellipse
                {
                    Width = baseSz,
                    Height = baseSz,
                    Stroke = CreateFlightStrokeBrush(startHex, endHex, 0xCC),
                    StrokeThickness = 1.6,
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false,
                    Opacity = 0.8
                };
                Canvas.SetLeft(ripple, center.X - baseSz / 2.0);
                Canvas.SetTop(ripple, center.Y - baseSz / 2.0);
                InteractionFxCanvas.Children.Add(ripple);
                spawned.Add(ripple);
            }
        }
        finally
        {
            RemoveFlightVisuals(spawned);
        }
    }


    private void ShowVerificationCelebration(string taskTitle, string masterAgentId, string workerAgentId, string? detailSummary = null)
    {
        _verifyBubbleEpoch++;
        _verifyBannerCount++;
        string task = string.IsNullOrWhiteSpace(taskTitle) ? "任务" : taskTitle.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (task.Length > 28) task = task[..28] + "…";

        string title;
        string content;
        if (string.IsNullOrWhiteSpace(detailSummary))
        {
            title = "主脑验收通过 · 已交付";
            content = $"{task} · 已达标交付";
        }
        else
        {
            title = task;
            content = detailSummary.Trim().Replace('\r', ' ').Replace('\n', ' ');
            if (content.Length > 72) content = content[..72] + "…";
        }
        if (string.IsNullOrWhiteSpace(content)) content = "已达标交付";

        if (VerifyCelebrationTitle != null) VerifyCelebrationTitle.Text = title;
        if (VerifyCelebrationContent != null) VerifyCelebrationContent.Text = content;
        if (VerifyCelebrationText != null) VerifyCelebrationText.Text = content;
        if (VerifyCelebrationAgentBadge != null)
            VerifyCelebrationAgentBadge.Text = ResolveCelebrationAgentLabel(workerAgentId);

        SoundFxService.Play(_reg.UserConfig, SoundFxService.SoundKind.VerifyPassed);

        string celebrateTip = title + "\n" + content;
        if (CapsuleBriefText != null)
        {
            CapsuleBriefText.Text = content;
            CapsuleBriefText.Foreground = Hex("#D1FAE5");
            CapsuleBriefText.Visibility = Visibility.Collapsed;
            CapsuleBriefText.Width = 0;
            CapsuleBriefText.Height = 0;
        }
        if (CollapsedMiniCapsule != null)
        {
            CollapsedMiniCapsule.ToolTip = celebrateTip;
            ToolTipService.SetInitialShowDelay(CollapsedMiniCapsule, 280);
            ToolTipService.SetShowDuration(CollapsedMiniCapsule, 16000);
        }

        const double bubbleW = 324;
        VerifyCelebrationBanner.MinWidth = 310;
        VerifyCelebrationBanner.MaxWidth = 348;
        VerifyCelebrationBanner.Width = Math.Clamp(bubbleW, 310, 348);
        VerifyCelebrationBanner.HorizontalAlignment = HorizontalAlignment.Left;
        VerifyCelebrationBanner.BeginAnimation(OpacityProperty, null);
        VerifyCelebrationBanner.Visibility = Visibility.Visible;
        VerifyCelebrationBanner.Opacity = 0;
        VerifyCelebrationBanner.UpdateLayout();
        double trackW = VerifyBubbleProgressTrack != null && VerifyBubbleProgressTrack.ActualWidth > 8
            ? VerifyBubbleProgressTrack.ActualWidth
            : 292;
        if (VerifyBubbleProgressFill != null)
        {
            VerifyBubbleProgressFill.Width = trackW;
            VerifyBubbleProgressFill.HorizontalAlignment = HorizontalAlignment.Left;
        }
        if (VerifyBubbleProgressScale != null)
        {
            VerifyBubbleProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            VerifyBubbleProgressScale.ScaleX = 1;
        }

        _bubbleAnchoredBelow = EstimateBubbleAnchoredBelow(120);
        ApplyBubbleTailVisual(_bubbleAnchoredBelow, _bubbleTailLeft);
        if (!AppPaths.IsIsolatedTest && VerifyCelebrationPopup != null)
        {
            if (VerifyCelebrationPopup.CustomPopupPlacementCallback == null)
                VerifyCelebrationPopup.CustomPopupPlacementCallback = PlaceVerifyCelebrationBubble;
            VerifyCelebrationPopup.IsOpen = true;
        }

        double slideFrom = _bubbleAnchoredBelow ? -8 : 8;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(260);
        var scaleTf = new ScaleTransform(0.95, 0.95);
        var slideTf = new TranslateTransform(0, slideFrom);
        var group = new TransformGroup();
        group.Children.Add(scaleTf);
        group.Children.Add(slideTf);
        VerifyCelebrationBanner.RenderTransformOrigin = new Point(0.5, _bubbleAnchoredBelow ? 0 : 1);
        VerifyCelebrationBanner.RenderTransform = group;
        scaleTf.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, dur) { EasingFunction = ease });
        scaleTf.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1, dur) { EasingFunction = ease });
        slideTf.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slideFrom, 0, dur) { EasingFunction = ease });
        VerifyCelebrationBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });

        double staySec = UserHubConfig.NormalizeCompletionToastDuration(_reg.UserConfig.CompletionToastDurationSeconds);
        var glowBrush = new SolidColorBrush(((SolidColorBrush)Hex("#10B981")).Color);
        OuterFrame.BorderBrush = glowBrush;
        var breath = new ColorAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(staySec),
            RepeatBehavior = new RepeatBehavior(1)
        };
        breath.KeyFrames.Add(new EasingColorKeyFrame(Color.FromRgb(0x34, 0xD3, 0x99), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
        breath.KeyFrames.Add(new EasingColorKeyFrame(Color.FromRgb(0x6E, 0xE7, 0xB7), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(staySec * 0.24))));
        breath.KeyFrames.Add(new EasingColorKeyFrame(Color.FromRgb(0x10, 0xB9, 0x81), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(staySec * 0.48))));
        breath.KeyFrames.Add(new EasingColorKeyFrame(Color.FromRgb(0x34, 0xD3, 0x99), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(staySec * 0.74))));
        breath.KeyFrames.Add(new EasingColorKeyFrame(Color.FromRgb(0x2A, 0x3F, 0x5F), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(staySec))));
        glowBrush.BeginAnimation(SolidColorBrush.ColorProperty, breath);

        PulseAvatarRing(masterAgentId, "#34D399");
        PulseAvatarRing(workerAgentId, "#34D399");
        StartVerifyCountdown(staySec);
    }

    private string ResolveCelebrationAgentLabel(string? agentId)
    {
        string id = string.IsNullOrWhiteSpace(agentId) ? "cursor" : agentId.Trim();
        string? name = _reg.UserConfig.Agents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = id.ToLowerInvariant() switch
            {
                "cursor" => "Cursor",
                "antigravity" => "Antigravity",
                "grok_bot" => "Grok",
                _ => id
            };
        }
        name = ShortAgentName(name);
        return string.IsNullOrWhiteSpace(name) ? "AI" : name;
    }

    private void StartVerifyCountdown(double seconds)
    {
        _verifyBubbleDismissArmed = false;
        try { _verifyBannerTimer?.Stop(); } catch { }
        _verifyTotalSec = Math.Max(0.2, seconds);
        _verifyRemainSec = _verifyTotalSec;
        _verifyBannerHoverPaused = false;
        _verifyBubbleDismissArmed = true;
        _verifyBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _verifyBannerTimer.Tick += OnVerifyCountdownTick;
        _verifyBannerTimer.Start();
    }

    private void OnVerifyCountdownTick(object? sender, EventArgs e)
    {
        if (!_verifyBubbleDismissArmed)
        {
            try { _verifyBannerTimer?.Stop(); } catch { }
            return;
        }
        if (_verifyBannerHoverPaused) return;
        _verifyRemainSec -= 0.016;
        double ratio = _verifyTotalSec <= 0 ? 0 : Math.Clamp(_verifyRemainSec / _verifyTotalSec, 0, 1);
        if (VerifyBubbleProgressScale != null)
            VerifyBubbleProgressScale.ScaleX = ratio;
        if (_verifyRemainSec > 0) return;
        _verifyBubbleDismissArmed = false;
        try { _verifyBannerTimer?.Stop(); } catch { }
        FadeOutVerifyBubble();
    }

    private void VerifyCelebrationBanner_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_verifyBubbleDismissArmed) return;
        _verifyBannerHoverPaused = true;
    }

    private void VerifyCelebrationBanner_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_verifyBannerHoverPaused) return;
        _verifyBannerHoverPaused = false;
    }

    private void BtnDismissVerifyBubble_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _verifyBubbleDismissArmed = false;
        _verifyBannerHoverPaused = false;
        try { _verifyBannerTimer?.Stop(); } catch { }
        FadeOutVerifyBubble();
    }

    private void FadeOutVerifyBubble()
    {
        if (VerifyCelebrationBanner == null) return;
        int epoch = _verifyBubbleEpoch;
        var fadeOut = new DoubleAnimation(VerifyCelebrationBanner.Opacity, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (epoch != _verifyBubbleEpoch || _verifyBubbleDismissArmed) return;
            VerifyCelebrationBanner.Visibility = Visibility.Collapsed;
            if (VerifyCelebrationPopup != null) VerifyCelebrationPopup.IsOpen = false;
            try { ApplyThemeVisual(); } catch { }
        };
        VerifyCelebrationBanner.BeginAnimation(OpacityProperty, fadeOut);
    }

    private bool EstimateBubbleAnchoredBelow(double bubbleHeight)
    {
        if (!TryGetOverlayScreenRect(out double winLeft, out double winTop, out _, out double winH, out Rect wa))
            return true;
        bool offscreen = winLeft > wa.Right + 20 || winLeft + 40 < wa.Left - 20 || winTop > wa.Bottom + 20 || winTop + winH < wa.Top - 20;
        if (offscreen) return true;
        double spaceBelow = wa.Bottom - (winTop + winH) - 8;
        double spaceAbove = winTop - wa.Top - 8;
        return !(spaceBelow < bubbleHeight && spaceAbove > spaceBelow);
    }

    private bool TryGetOverlayScreenRect(out double winLeft, out double winTop, out double winW, out double winH, out Rect wa)
    {
        wa = SystemParameters.WorkArea;
        winW = OuterFrame != null && OuterFrame.ActualWidth > 20 ? OuterFrame.ActualWidth : (ActualWidth > 20 ? ActualWidth : Width);
        winH = OuterFrame != null && OuterFrame.ActualHeight > 16 ? OuterFrame.ActualHeight : (ActualHeight > 16 ? ActualHeight : 48);
        if (winW < 20) winW = 180;
        if (winH < 16) winH = 48;
        winLeft = Left;
        winTop = Top;
        try
        {
            if (OuterFrame != null && PresentationSource.FromVisual(OuterFrame) is PresentationSource src && src.CompositionTarget != null)
            {
                double dpiX = src.CompositionTarget.TransformToDevice.M11;
                double dpiY = src.CompositionTarget.TransformToDevice.M22;
                if (dpiX < 0.1) dpiX = 1;
                if (dpiY < 0.1) dpiY = 1;
                Point screenPx = OuterFrame.PointToScreen(new Point(0, 0));
                winLeft = screenPx.X / dpiX;
                winTop = screenPx.Y / dpiY;
            }
        }
        catch
        {
            winLeft = Left;
            winTop = Top;
        }
        return true;
    }

    private CustomPopupPlacement[] PlaceVerifyCelebrationBubble(Size popupSize, Size targetSize, Point offset)
    {
        _ = offset;
        double popW = popupSize.Width > 1 ? popupSize.Width : 324;
        double popH = popupSize.Height > 1 ? popupSize.Height : 118;
        double targetW = targetSize.Width > 1 ? targetSize.Width : (ActualWidth > 20 ? ActualWidth : 180);
        double targetH = targetSize.Height > 1 ? targetSize.Height : (ActualHeight > 16 ? ActualHeight : 48);
        TryGetOverlayScreenRect(out double winLeft, out double winTop, out _, out _, out Rect wa);
        double winW = targetW;
        double winH = targetH;
        bool offscreen = winLeft > wa.Right + 40 || winLeft + winW < wa.Left - 40 || winTop > wa.Bottom + 40 || winTop + winH < wa.Top - 40;
        const double gap = 8;
        const double margin = 6;
        bool placeBelow = true;
        if (!offscreen)
        {
            double spaceBelow = wa.Bottom - (winTop + winH) - gap - margin;
            double spaceAbove = winTop - wa.Top - gap - margin;
            if (spaceBelow < popH && spaceAbove > spaceBelow)
                placeBelow = false;
        }
        double bubbleLeft = winLeft + (winW - popW) / 2.0;
        if (!offscreen)
        {
            double minL = wa.Left + margin;
            double maxL = wa.Right - popW - margin;
            if (maxL < minL) maxL = minL;
            bubbleLeft = Math.Clamp(bubbleLeft, minL, maxL);
        }
        double x = bubbleLeft - winLeft;
        double y = placeBelow ? winH + gap : -popH - gap;
        double tailCenter = (winLeft + winW / 2.0) - bubbleLeft;
        tailCenter = Math.Clamp(tailCenter, 18, Math.Max(18, popW - 18));
        _bubbleAnchoredBelow = placeBelow;
        _bubbleTailLeft = tailCenter - 8;
        bool belowCopy = placeBelow;
        double leftCopy = _bubbleTailLeft;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => ApplyBubbleTailVisual(belowCopy, leftCopy)));
        return new[] { new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal) };
    }

    private void ApplyBubbleTailVisual(bool placeBelow, double tailLeft)
    {
        double width = VerifyCelebrationBanner != null && VerifyCelebrationBanner.Width > 40
            ? VerifyCelebrationBanner.Width
            : 324;
        double left = Math.Clamp(tailLeft, 10, Math.Max(10, width - 26));
        if (VerifyBubbleTailTop != null)
        {
            VerifyBubbleTailTop.Visibility = placeBelow ? Visibility.Visible : Visibility.Collapsed;
            VerifyBubbleTailTop.HorizontalAlignment = HorizontalAlignment.Left;
            VerifyBubbleTailTop.Margin = new Thickness(left, 0, 0, -1);
        }
        if (VerifyBubbleTailBottom != null)
        {
            VerifyBubbleTailBottom.Visibility = placeBelow ? Visibility.Collapsed : Visibility.Visible;
            VerifyBubbleTailBottom.HorizontalAlignment = HorizontalAlignment.Left;
            VerifyBubbleTailBottom.Margin = new Thickness(left, -1, 0, 0);
        }
    }
}
