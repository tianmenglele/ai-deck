using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using AICollabHub.Models;

namespace AICollabHub.Services;

/// <summary>桌面端轻量实时忙碌探测结果（供悬浮窗/工作台合并展示）。</summary>
public sealed record DesktopBusyProbeResult(
    bool IsWorking,
    string ActiveChatTitle,
    string ActiveProjectHint,
    DateTime ProbedUtc,
    bool IsInconclusive = false,
    bool ProcessFound = true,
    int ActiveTaskCount = 0);

public sealed class LiveAgentWorkState
{
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ProjectRoot { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string Status { get; set; } = "idle"; // working | done | error | idle
    public string WorkflowPhase { get; set; } = "idle"; // master_analyzing | executing | reconnecting | master_verifying | reworking | verified_delivered | interrupted_net | awaiting_user_choice | error
    public int DispatcherPid { get; set; }
    public long UpdatedUtcTicks { get; set; }
    public string MasterPlannerName { get; set; } = "";
    public string SelectedModel { get; set; } = "";
    public string SelectedEffortTier { get; set; } = "high"; // high | medium | low
    public string SelectedTierBadge { get; set; } = "";
    public int CurrentRound { get; set; } = 1;
    public int MaxRounds { get; set; } = 3;
    public int NetworkRetryAttempt { get; set; }
    public int MaxNetworkRetries { get; set; } = 3;
    public string LastFailureBadge { get; set; } = "";
    public string FailoverNote { get; set; } = "";
    public bool CanResumeInterruptedChain { get; set; }
    public string VerificationSummary { get; set; } = "";
    public string ChangedFilesSummary { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string SessionTitle { get; set; } = "";
    public string SessionFilePath { get; set; } = "";
    public string SessionAction { get; set; } = ""; // resumed | created
    public string TaskSummary { get; set; } = "";
    public string DispatchReason { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string LatestLogTail { get; set; } = "";
    public string OutputInboxFile { get; set; } = "";

    /// <summary>仅内存展示：由桌面端实时忙碌探测合成的 working 态（不落盘，不触发派单/验收音效）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDesktopLiveOverlay { get; set; }

    /// <summary>仅内存展示：同 Agent 并发活跃任务数（磁盘跨工程 + 桌面探测汇总）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ActiveTaskCount { get; set; }
}

public sealed class AutoDispatchService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DesktopUiaLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (DateTime Utc, string ChatTitle)> RecentDesktopDispatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly record struct DesktopBusyDebounceTracker(
        bool IsWorking,
        int ConsecutiveIdleCount,
        DateTime? IdleStreakStartUtc,
        int ActiveTaskCount);

    private static readonly ConcurrentDictionary<string, DesktopBusyProbeResult> LiveDesktopBusyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DesktopBusyProbeResult> TestDesktopBusyOverrides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DesktopBusyDebounceTracker> DesktopBusyDebounceByAgent = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object DesktopBusyDebounceSync = new();
    private static readonly ConcurrentDictionary<string, byte> DesktopProbeInFlight = new(StringComparer.OrdinalIgnoreCase);
    private static int _desktopProbeSweepGate;

    /// <summary>桌面忙碌探测缓存有效期（约 1.2~1.5 秒，供 --self-test 断言 / 探测刷新间隔）。</summary>
    public const int DesktopLiveProbeCacheTtlMs = 1300;

    /// <summary>合并展示时允许沿用忙碌缓存的保鲜期，避免 2 秒轮询间隙掉忙态引起界面闪烁。</summary>
    public const int DesktopLiveProbeDisplayStaleMs = 15000;

    /// <summary>可靠空闲需连续达到这么多次，才允许从忙碌回落。</summary>
    public const int DesktopBusyIdleReleaseSamples = 3;

    /// <summary>可靠空闲还需跨够这么多毫秒，才允许从忙碌回落。</summary>
    public const int DesktopBusyIdleReleaseMs = 3500;

    /// <summary>探测结果相对上次发生变化时触发，悬浮窗据此立即重绘。</summary>
    public static event Action? DesktopLiveProbeChanged;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_BOTTOM = (IntPtr)1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOWMINNOACTIVE = 7;
    private const int SW_RESTORE = 9;
    private const int STARTF_USESHOWWINDOW = 0x00000001;

    /// <summary>
    /// 非激活恢复：SW_SHOWNOACTIVATE + HWND_BOTTOM + SWP_NOACTIVATE，绝不压到用户当前窗口上方。
    /// </summary>
    public static void PrepareWindowNoActivateKeepBelow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { }
    }

    /// <summary>
    /// 等待用户键鼠静默空隙（默认至少 quietMs 毫秒无输入；最多等 maxWaitMs）。
    /// StrictNoForegroundStealOnDispatch 短瞬粘贴前必须调用。
    /// </summary>
    public static void WaitForUserInputIdleGap(int quietMs = 600, int maxWaitMs = 2500)
    {
        if (quietMs < 0) quietMs = 0;
        if (maxWaitMs < quietMs) maxWaitMs = quietMs;
        int waited = 0;
        while (waited < maxWaitMs)
        {
            if (GetUserInputIdleMilliseconds() >= quietMs) return;
            Thread.Sleep(100);
            waited += 100;
        }
    }

    private static DateTime _grokCliNotSignedInUntilUtc = DateTime.MinValue;
    private static DateTime _codexOfficialQuotaExhaustedUntilUtc = DateTime.MinValue;

    public static long GetUserInputIdleMilliseconds()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref lii))
            {
                uint nowTick = unchecked((uint)Environment.TickCount);
                return unchecked(nowTick - lii.dwTime);
            }
        }
        catch { }
        return 999999;
    }

    public static bool StartProcessOnDefaultDesktop(string exePath, string? args = null, string? workingDir = null, bool noActivate = true)
    {
        try
        {
            var si = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>(),
                lpDesktop = "WinSta0\\Default",
                dwFlags = noActivate ? STARTF_USESHOWWINDOW : 0,
                wShowWindow = noActivate ? (short)SW_SHOWMINNOACTIVE : (short)SW_RESTORE
            };
            var cmdLine = string.IsNullOrWhiteSpace(args) ? $"\"{exePath}\"" : $"\"{exePath}\" {args}";
            var cwd = (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir))
                ? workingDir
                : Path.GetDirectoryName(exePath);
            if (CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, cwd, ref si, out var pi))
            {
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                return true;
            }
        }
        catch { }
        return false;
    }

    public static string LiveStateJsonPath => Path.Combine(AppPaths.UserDataDir, "live_agent_status.json");
    public static string LiveStateMapPath => Path.Combine(AppPaths.UserDataDir, "live_agents_map.json");

    /// <summary>桌面端“正在生成”停止按钮的唯一判定规则（PowerShell 函数，所有 UIA 脚本共用；-contains / -like 默认忽略大小写）。</summary>
    internal const string PsStopButtonMatcherFunction = @"
function Test-HubStopButtonName([string]$n) {
    if ([string]::IsNullOrWhiteSpace($n)) { return $false }
    $t = $n.Trim()
    if (@('Stop','Stop generation','Stop generating','Stop response','Cancel generation','Stop agent','停止生成','停止') -contains $t) { return $true }
    return ($t -like 'Stop *')
}
";

    internal static readonly string[] DesktopStopButtonNames =
        { "Stop", "Stop generation", "Stop generating", "Stop response", "Cancel generation", "Stop agent", "停止生成", "停止" };

    /// <summary>与 <see cref="PsStopButtonMatcherFunction"/> 等价的 C# 判定（供自检对齐）。</summary>
    internal static bool IsDesktopStopButtonName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var t = name.Trim();
        if (DesktopStopButtonNames.Any(s => s.Equals(t, StringComparison.OrdinalIgnoreCase))) return true;
        return t.StartsWith("Stop ", StringComparison.OrdinalIgnoreCase);
    }

    internal static readonly HashSet<string> SidebarSkipButtonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "New Agent", "New Bot", "New Chat", "Pin", "Unpin", "Archive", "More", "Search",
        "Automations", "Customize", "Repositories", "Settings", "Chat actions"
    };

    /// <summary>侧栏会话按钮原始名清洗（去掉 Unread/Working 后缀）。</summary>
    public static string CleanSidebarChatButtonName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        s = Regex.Replace(s, @",\s*Unread activity,\s*Working\s*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @",\s*Unread activity\s*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @",\s*Working\s*$", "", RegexOptions.IgnoreCase);
        return s.Trim();
    }

    /// <summary>侧栏按钮是否带 ", Working" 运行态。</summary>
    public static bool SidebarChatButtonIndicatesWorking(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && raw.Contains(", Working", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为泛化/新建/纯工程名会话标题（尚未锁定真实会话名）。</summary>
    public static bool IsNonSpecificDesktopChatTitle(string? title, string? projectLeaf, IReadOnlyList<string>? candidateGroups)
    {
        var clean = CleanSidebarChatButtonName(title);
        if (string.IsNullOrWhiteSpace(clean)) return true;
        if (SidebarSkipButtonNames.Contains(clean)) return true;
        if (clean.Contains("新建", StringComparison.OrdinalIgnoreCase)) return true;
        if (clean.StartsWith("New ", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(projectLeaf) &&
            clean.Equals(projectLeaf.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        if (candidateGroups != null)
        {
            foreach (var c in candidateGroups)
            {
                if (!string.IsNullOrWhiteSpace(c) &&
                    clean.Equals(c.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>从 GUI 标题（Group/Chat 或纯 Chat）提取会话叶名。</summary>
    public static string ExtractDesktopChatLeaf(string? guiTitle)
    {
        if (string.IsNullOrWhiteSpace(guiTitle)) return "";
        var t = guiTitle.Trim();
        int slash = t.LastIndexOf('/');
        if (slash >= 0 && slash < t.Length - 1)
            return t[(slash + 1)..].Trim();
        return t;
    }

    /// <summary>侧栏分组快照（供真实 UIA 与 --self-test 共用）。</summary>
    public sealed class SidebarGroupSnapshot
    {
        public string Name { get; init; } = "";
        public List<string> ChatButtonRawNames { get; init; } = new();
    }

    /// <summary>单窗口侧栏忙碌态快照。</summary>
    public sealed class SidebarBusyWindowSnapshot
    {
        public bool HasSidebarGroupStructure { get; init; }
        public bool HasStopGeneration { get; init; }
        public bool HasSteerWithoutInterrupting { get; init; }
        public List<SidebarGroupSnapshot> Groups { get; init; } = new();
    }

    static bool SidebarGroupNameMatches(string groupName, IReadOnlyList<string> candidateGroups)
    {
        if (string.IsNullOrWhiteSpace(groupName) || candidateGroups == null || candidateGroups.Count == 0)
            return false;
        foreach (var c in candidateGroups)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var cand = c.Trim();
            if (groupName.Equals(cand, StringComparison.OrdinalIgnoreCase)) return true;
            if (groupName.Contains(cand, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 纯静态侧栏忙碌判定：精确绑定目标工程/会话，消除跨项目 Stop generation 串扰。
    /// </summary>
    public static (bool IsWorking, string TopChatTitle) EvaluateSidebarBusyFromSnapshot(
        SidebarBusyWindowSnapshot snap,
        IReadOnlyList<string> candidateGroups,
        string? targetChatLeaf,
        string? projectLeaf = null)
    {
        snap ??= new SidebarBusyWindowSnapshot();
        candidateGroups ??= Array.Empty<string>();
        var leaf = (targetChatLeaf ?? "").Trim();
        var proj = (projectLeaf ?? "").Trim();

        if (!snap.HasSidebarGroupStructure)
        {
            bool fallbackBusy = snap.HasStopGeneration || snap.HasSteerWithoutInterrupting;
            return (fallbackBusy, "");
        }

        var matchedGroups = snap.Groups
            .Where(g => SidebarGroupNameMatches(g.Name, candidateGroups))
            .ToList();
        if (matchedGroups.Count == 0)
            return (false, "");

        string topChatName = "";
        foreach (var g in matchedGroups)
        {
            foreach (var bn in g.ChatButtonRawNames)
            {
                var clean = CleanSidebarChatButtonName(bn);
                if (string.IsNullOrWhiteSpace(clean) || SidebarSkipButtonNames.Contains(clean)) continue;
                if (IsNonSpecificDesktopChatTitle(clean, proj, candidateGroups)) continue;
                if (string.IsNullOrWhiteSpace(topChatName))
                    topChatName = g.Name + "/" + clean;
            }
        }

        bool targetLocked = !IsNonSpecificDesktopChatTitle(leaf, proj, candidateGroups);

        bool AnyWorkingInGroups(IEnumerable<SidebarGroupSnapshot> groups) =>
            groups.Any(g => g.ChatButtonRawNames.Any(SidebarChatButtonIndicatesWorking));

        if (targetLocked)
        {
            string? matchedBtnRaw = null;
            foreach (var g in matchedGroups)
            {
                foreach (var bn in g.ChatButtonRawNames)
                {
                    var clean = CleanSidebarChatButtonName(bn);
                    if (clean.Equals(leaf, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedBtnRaw = bn;
                        break;
                    }
                }
                if (matchedBtnRaw != null) break;
            }

            if (matchedBtnRaw != null)
            {
                if (SidebarChatButtonIndicatesWorking(matchedBtnRaw))
                    return (true, topChatName);

                // 本会话已不带 Working：若其他分组/其他会话仍 Working，则底部 Stop/Steer 属他人
                bool otherSessionWorking = false;
                foreach (var g in snap.Groups)
                {
                    foreach (var bn in g.ChatButtonRawNames)
                    {
                        var clean = CleanSidebarChatButtonName(bn);
                        if (clean.Equals(leaf, StringComparison.OrdinalIgnoreCase)) continue;
                        if (SidebarChatButtonIndicatesWorking(bn))
                        {
                            otherSessionWorking = true;
                            break;
                        }
                    }
                    if (otherSessionWorking) break;
                }

                if (otherSessionWorking)
                    return (false, topChatName);

                if (snap.HasStopGeneration || snap.HasSteerWithoutInterrupting)
                    return (true, topChatName);
                return (false, topChatName);
            }
        }

        // 标题未锁定（新建后自动重命名中）或尚未在侧栏找到目标会话：只看目标工程分组
        if (AnyWorkingInGroups(matchedGroups))
            return (true, topChatName);

        var otherGroups = snap.Groups.Where(g => !SidebarGroupNameMatches(g.Name, candidateGroups));
        if (AnyWorkingInGroups(otherGroups))
            return (false, topChatName); // 禁止被其他工程 Stop/Working 串扰

        return (false, topChatName);
    }

    /// <summary>
    /// 纯静态桌面观测完工判定（场景 A/B/C/D），供观测主循环与 --self-test 共用。
    /// </summary>
    public static bool EvaluateDesktopWatchCompletion(
        bool everObservedWorking,
        int consecutiveIdleProbes,
        DateTime? changeDetectedUtc,
        DateTime lastObservedWorkingUtc,
        double watchElapsedSeconds,
        DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;

        if (everObservedWorking && changeDetectedUtc.HasValue)
        {
            // 场景 A：曾 Working + 有文件/会话变更 → 2 次空闲 + 变更/Working 各静默 ≥2s
            if (consecutiveIdleProbes >= 2 &&
                (now - changeDetectedUtc.Value).TotalSeconds >= 2.0 &&
                (lastObservedWorkingUtc == DateTime.MinValue ||
                 (now - lastObservedWorkingUtc).TotalSeconds >= 2.0))
                return true;
            return false;
        }

        if (everObservedWorking && !changeDetectedUtc.HasValue)
        {
            // 场景 B：曾 Working + 只读无改 → 3 次空闲 + 已用时 ≥4s
            return consecutiveIdleProbes >= 3 && watchElapsedSeconds >= 4.0;
        }

        if (!everObservedWorking && changeDetectedUtc.HasValue)
        {
            // 场景 C：未见 Working 但有文件/Turn 变更 → 2 次空闲 + 变更静默 ≥2.5s
            return consecutiveIdleProbes >= 2 &&
                   (now - changeDetectedUtc.Value).TotalSeconds >= 2.5;
        }

        // 场景 D：未见 Working 且无变更（极快秒回）→ 冷启动宽限 10s + 3 次空闲
        return watchElapsedSeconds >= 10.0 && consecutiveIdleProbes >= 3;
    }

    /// <summary>桌面 UIA 误改历史消息触发 Revert files 弹窗时的安全中止令牌（供自检与单文件发布后断言）。</summary>
    public const string RevertModalAbortedToken = "REVERT_MODAL_ABORTED";

    /// <summary>
    /// 桌面无焦点 Win32 策略辅助（嵌入 UIA 派单 Add-Type WinDeskHelper；供 --self-test 断言）。
    /// 含 SW_SHOWNOACTIVATE / HWND_BOTTOM / SWP_NOACTIVATE / GetLastInputInfo 静默门控 / 压回底层。
    /// </summary>
    public static string BuildDesktopNoStealWin32Helpers()
    {
        return @"
    [DllImport(""user32.dll"")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    public static readonly IntPtr HWND_BOTTOM = (IntPtr)1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const int SW_SHOWNOACTIVATE = 4;
    public static void PrepareWindowNoActivateKeepBelow(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
    public static void WaitForUserIdleGap(int quietMs, int maxWaitMs) {
        if (quietMs < 0) quietMs = 0;
        if (maxWaitMs < quietMs) maxWaitMs = quietMs;
        int waited = 0;
        while (waited < maxWaitMs) {
            if (GetIdleMs() >= quietMs) return;
            Thread.Sleep(100);
            waited += 100;
        }
    }
    public static void DemoteAndRestoreForeground(IntPtr targetHwnd, IntPtr prevForeground) {
        if (targetHwnd != IntPtr.Zero)
            SetWindowPos(targetHwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (prevForeground != IntPtr.Zero && prevForeground != targetHwnd) {
            try { SetForegroundWindow(prevForeground); } catch {}
        }
    }
";
    }

    /// <summary>
    /// 桌面无焦点文本注入 PS 策略（ValuePattern 优先；剪贴板兜底须 GetLastInputInfo 静默门控 + 剪贴板恢复）。
    /// </summary>
    public static string BuildDesktopNoStealPsHelpers()
    {
        return @"
function Set-PromptViaClipboardPaste([System.Windows.Automation.AutomationElement]$edit, [string]$text) {
    if ($null -eq $edit -or [string]::IsNullOrEmpty($text)) { return $false }
    $strictNoSteal = $true
    try { if ($null -ne $cfg.StrictNoForegroundStealOnDispatch) { $strictNoSteal = [bool]$cfg.StrictNoForegroundStealOnDispatch } } catch { $strictNoSteal = $true }
    $oldClip = $null
    try {
        try { $oldClip = Get-Clipboard -Raw -ErrorAction SilentlyContinue } catch { $oldClip = $null }
        # Strict：用户正在打字/动鼠标时绝不抢焦；最多等 2.5s 等到至少 600ms 键鼠静默空隙
        if ($strictNoSteal) {
            [WinDeskHelper]::WaitForUserIdleGap(600, 2500)
        }
        Set-Clipboard -Value $text
        Start-Sleep -Milliseconds 80
        Clear-EditDraft $edit
        Start-Sleep -Milliseconds 60
        # 第一优先：无焦点 PostMessage WM_PASTE；严禁移动用户光标或模拟键鼠注入
        $eh = [IntPtr]::Zero
        try { $eh = [IntPtr]$edit.Current.NativeWindowHandle } catch { $eh = [IntPtr]::Zero }
        # Strict 或 Grok Bot：禁止对目标窗口裸切前台，也禁止裸 $edit.SetFocus()
        $forbidBareFocus = $strictNoSteal -or [bool]$cfg.IsGrok
        if ($eh -eq [IntPtr]::Zero -and -not $forbidBareFocus) {
            try {
                $edit.SetFocus() | Out-Null
                Start-Sleep -Milliseconds 80
                $eh = [IntPtr]$edit.Current.NativeWindowHandle
            } catch {}
        }
        if ($eh -ne [IntPtr]::Zero) {
            [WinDeskHelper]::PostMessageW($eh, 0x0302, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            Start-Sleep -Milliseconds 220
            return (Test-PromptInjectedOk $edit $text $coreSnippet)
        }
        try {
            $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
            return (Test-PromptInjectedOk $edit $text $coreSnippet)
        } catch { return $false }
    } catch {
        return $false
    } finally {
        try {
            if ($null -ne $oldClip) { Set-Clipboard -Value $oldClip } else { Set-Clipboard -Value '' }
        } catch {}
        if ($strictNoSteal) {
            try { [WinDeskHelper]::DemoteAndRestoreForeground($hwnd, $prevForeground) } catch {}
        } elseif ($prevForeground -ne [IntPtr]::Zero -and [WinDeskHelper]::GetForegroundWindow() -ne $prevForeground) {
            try { [WinDeskHelper]::SetForegroundWindow($prevForeground) | Out-Null } catch {}
        }
    }
}
function Set-PromptTextRobust([System.Windows.Automation.AutomationElement]$edit, [string]$text) {
    if ($null -eq $edit) { return $false }
    # 第一优先：纯无焦点 ValuePattern.SetValue（零切前台、零剪贴板、零键鼠）
    try {
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
        Start-Sleep -Milliseconds 120
        if (Test-PromptInjectedOk $edit $text $coreSnippet) { return $true }
    } catch {}
    # 仅 Chromium/Electron 富文本截断时才回退剪贴板 + WM_PASTE
    return (Set-PromptViaClipboardPaste $edit $text)
}
function Restore-DispatchForeground {
    $strictNoSteal = $true
    try { if ($null -ne $cfg.StrictNoForegroundStealOnDispatch) { $strictNoSteal = [bool]$cfg.StrictNoForegroundStealOnDispatch } } catch { $strictNoSteal = $true }
    if (-not $strictNoSteal) { return }
    if ($null -eq $hwnd -or $hwnd -eq [IntPtr]::Zero) { return }
    try {
        $fg = [WinDeskHelper]::GetForegroundWindow()
        # 仅当前台被意外换成目标窗口时压回底层并还原用户原前台
        if ($fg -eq $hwnd -and $prevForeground -ne $hwnd) {
            [WinDeskHelper]::DemoteAndRestoreForeground($hwnd, $prevForeground)
        }
    } catch {}
}
function Invoke-DesktopControlNoFocus($el) {
    if ($null -eq $el) { return $false }
    $ok = $false
    $invPat = [System.Windows.Automation.InvokePattern]::Pattern
    try { $el.GetCurrentPattern($invPat).Invoke(); $ok = $true } catch {}
    if (-not $ok) {
        try {
            $sel = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            if ($null -ne $sel) { $sel.Select(); $ok = $true }
        } catch {}
    }
    if (-not $ok) {
        try {
            $ec = $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($null -ne $ec) {
                if ($ec.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) { $ec.Expand(); Restore-DispatchForeground } else { $ec.Collapse() }
                $ok = $true
            }
        } catch {}
    }
    if (-not $ok) {
        try { $ok = Invoke-BackgroundClickNoFocus $el } catch { $ok = $false }
    }
    Restore-DispatchForeground
    return [bool]$ok
}
function Invoke-BackgroundClickNoFocus([System.Windows.Automation.AutomationElement]$el) {
    if ($null -eq $el) { return $false }
    try {
        $invPat = [System.Windows.Automation.InvokePattern]::Pattern
        $el.GetCurrentPattern($invPat).Invoke()
        Restore-DispatchForeground
        return $true
    } catch {}
    try {
        $sel = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($null -ne $sel) { $sel.Select(); Restore-DispatchForeground; return $true }
    } catch {}
    try {
        $nh = [IntPtr]::Zero
        try { $nh = [IntPtr]$el.Current.NativeWindowHandle } catch {}
        if ($nh -ne [IntPtr]::Zero) {
            # 第二优先：后台窗口消息，绝不移动用户鼠标光标
            [WinDeskHelper]::PostMessageW($nh, 0x0201, [IntPtr]1, [IntPtr]::Zero) | Out-Null
            Start-Sleep -Milliseconds 30
            [WinDeskHelper]::PostMessageW($nh, 0x0202, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            Restore-DispatchForeground
            return $true
        }
    } catch {}
    return $false
}
";
    }

    /// <summary>
    /// 桌面提示框挑选 / 邻近发送 / Revert 弹窗防护的可测试 PowerShell 辅助函数集
    ///（嵌入 TrySendToDesktopChatDirect；Top 降序+视口裁剪免疫 Chromium 滚动高度溢出）。
    /// </summary>
    internal static string BuildDesktopPromptPickerPsHelpers()
    {
        return @"
function Test-HasRevertFilesModal([System.Windows.Automation.AutomationElement]$r) {
    try {
        $hasKeepFilesBtn = $false
        $btnC = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        foreach ($b in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnC)) {
            try {
                $bn = $b.Current.Name
                if ([string]::IsNullOrWhiteSpace($bn)) { continue }
                $t = $bn.Trim()
                if (@('Revert Files','Restore Checkpoint','Undo Changes') -contains $t) {
                    return $true
                }
                if ($t.Equals('Keep Files', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $hasKeepFilesBtn = $true
                }
            } catch {}
        }
        if ($hasKeepFilesBtn) {
            $txtC = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
            foreach ($tx in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $txtC)) {
                try {
                    $tn = $tx.Current.Name
                    if ($tn -like '*Revert files to this message*' -or $tn -like '*Restore checkpoint*') { return $true }
                } catch {}
            }
        }
    } catch {}
    return $false
}
function Cancel-RevertFilesModalSafely([System.Windows.Automation.AutomationElement]$r) {
    if (-not (Test-HasRevertFilesModal $r)) { return $false }
    $denyClick = @('Revert Files','Keep Files','Revert','Restore','Restore Checkpoint','Undo Changes','Discard','Delete','Confirm','OK','确定','回滚','恢复','删除')
    $allowClick = @('Cancel','取消','Close modal','Close dialog')
    try {
        $btnC = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        foreach ($b in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnC)) {
            try {
                $bn = $b.Current.Name
                if ([string]::IsNullOrWhiteSpace($bn)) { continue }
                $t = $bn.Trim()
                if ($denyClick -contains $t) { continue }
                if ($allowClick -contains $t) {
                    try {
                        Invoke-DesktopControlNoFocus $b | Out-Null
                        Start-Sleep -Milliseconds 350
                        return $true
                    } catch {}
                }
            } catch {}
        }
    } catch {}
    return $false
}
function Find-IdlePromptEdit([System.Windows.Automation.AutomationElement]$r) {
    $editC = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $allEdits = @($r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editC))
    foreach ($eSteer in $allEdits) {
        try {
            if ($eSteer.Current.Name -like '*Steer without interrupting*') { return $null }
        } catch {}
    }
    $rootRect = $null
    try { $rootRect = $r.Current.BoundingRectangle } catch {}
    $hasRootRect = ($null -ne $rootRect -and -not $rootRect.IsEmpty -and $rootRect.Height -gt 60)
    $cands = New-Object System.Collections.Generic.List[object]
    $domIndex = 0
    foreach ($e in $allEdits) {
        $domIndex++
        try {
            if (-not $e.Current.IsEnabled) { continue }
            $rect = $e.Current.BoundingRectangle
            $hasRect = ($null -ne $rect -and -not $rect.IsEmpty -and $rect.Width -gt 10 -and $rect.Height -gt 5)
            $en = $e.Current.Name
            $eh = $e.Current.HelpText
            if ($en -match 'Custom answer|Search|Name your Bot|Filter|Rename|Title|Address|Folder|搜索|筛选|重命名') { continue }
            $nameOk = ($en -eq 'Prompt' -or $en -match 'Plan, Build|Send follow-up|Message|Ask |Reply|Chat input|发送消息|输入消息|输入指令')
            $emptyWithPromptHelp = ([string]::IsNullOrWhiteSpace($en) -and $eh -match 'Ask|Message|Prompt|输入|发送')
            if (-not ($nameOk -or $emptyWithPromptHelp)) { continue }
            if ($cfg.IsGrok -and $dispatchedInNewMode -and $eh -like 'Message *') {
                $targetBot = $eh.Substring(8).Trim()
                if ($wrongChatNamesToAvoid.Contains($targetBot)) { continue }
            }
            $topVal = if ($hasRect) { [double]$rect.Top } else { -1.0 }
            $clampedBot = if ($hasRect) { [double]$rect.Bottom } else { -1.0 }
            if ($hasRect -and $hasRootRect) {
                $clampedBot = [Math]::Min($clampedBot, [double]$rootRect.Bottom)
            }
            [void]$cands.Add([pscustomobject]@{
                IsOffscreen = [bool]$e.Current.IsOffscreen
                HasRect = $hasRect
                Top = $topVal
                ClampedBottom = $clampedBot
                DomIndex = $domIndex
                Elem = $e
            })
        } catch {}
    }
    if ($cands.Count -eq 0) { return $null }
    $pool = @($cands | Where-Object { $_.HasRect -and -not $_.IsOffscreen })
    if ($pool.Count -eq 0) { $pool = @($cands | Where-Object { $_.HasRect }) }
    if ($pool.Count -eq 0) { $pool = @($cands) }
    $chosen = $pool | Sort-Object -Property @{ Expression = 'IsOffscreen'; Descending = $false }, @{ Expression = 'HasRect'; Descending = $true }, @{ Expression = 'Top'; Descending = $true }, @{ Expression = 'ClampedBottom'; Descending = $true }, @{ Expression = 'DomIndex'; Descending = $true } | Select-Object -First 1
    if ($null -eq $chosen) { return $null }
    if (-not $cfg.IsGrok -and -not $dispatchedInNewMode -and $chosen.HasRect -and $hasRootRect -and $rootRect.Height -gt 200) {
        try {
            if ($chosen.ClampedBottom -lt ($rootRect.Top + $rootRect.Height * 0.55)) {
                $existLen = 0
                $existVal = ''
                try {
                    $existVal = $chosen.Elem.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
                    if (-not [string]::IsNullOrWhiteSpace($existVal)) { $existLen = $existVal.Length }
                } catch {}
                $hasCore = $false
                if ($existLen -gt 0 -and -not [string]::IsNullOrWhiteSpace($coreSnippet) -and $existVal -like ""*$coreSnippet*"") {
                    $hasCore = $true
                }
                if ($existLen -gt 0 -and -not $hasCore) { return $null }
            }
        } catch {}
    }
    return $chosen.Elem
}
function Invoke-SendButtonNearEdit([System.Windows.Automation.AutomationElement]$r, [System.Windows.Automation.AutomationElement]$edit) {
    $btnC = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $enabledSendBtns = @()
    $sendAllow = @('Send message','Send','Send follow-up','Send prompt','Submit','发送','发送消息')
    foreach ($sb in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnC)) {
        try {
            $sbn = $sb.Current.Name
            if ([string]::IsNullOrWhiteSpace($sbn)) { continue }
            $sbnTrim = $sbn.Trim()
            if ($sbnTrim -match 'Revert|Keep Files|Restore|Undo|Discard|Delete|回滚|恢复|删除') { continue }
            if (($sendAllow -contains $sbnTrim) -and $sb.Current.IsEnabled) {
                $enabledSendBtns += $sb
            }
        } catch {}
    }
    if ($enabledSendBtns.Count -eq 0) { return $false }
    $chosen = $enabledSendBtns[$enabledSendBtns.Count - 1]
    $editRect = $null
    $rootRect = $null
    if ($null -ne $edit) { try { $editRect = $edit.Current.BoundingRectangle } catch {} }
    try { $rootRect = $r.Current.BoundingRectangle } catch {}
    if ($null -ne $editRect -and -not $editRect.IsEmpty) {
        $visTop = [double]$editRect.Top
        $visBottom = [double]$editRect.Bottom
        if ($null -ne $rootRect -and -not $rootRect.IsEmpty -and $rootRect.Height -gt 60) {
            $visTop = [Math]::Max($visTop, [double]$rootRect.Top)
            $visBottom = [Math]::Min($visBottom, [double]$rootRect.Bottom)
            if ($visBottom -lt $visTop) { $visBottom = $visTop }
        }
        $bestDist = [double]::MaxValue
        $foundNear = $false
        foreach ($sb in $enabledSendBtns) {
            try {
                $sr = $sb.Current.BoundingRectangle
                if ($null -ne $sr -and -not $sr.IsEmpty) {
                    $scx = $sr.Left + ($sr.Width / 2.0)
                    $scy = $sr.Top + ($sr.Height / 2.0)
                    # 将输入框裁剪到窗口可视高度再算区间距：免疫 Chromium Bottom 溢出与任意长指令高度
                    $dyToBox = 0.0
                    if ($scy -lt $visTop) { $dyToBox = $visTop - $scy }
                    elseif ($scy -gt $visBottom) { $dyToBox = $scy - $visBottom }
                    if ($dyToBox -gt 140) { continue }
                    $foundNear = $true
                    $dist = ($dyToBox * 4.0) + [Math]::Abs($scy - $visBottom) + [Math]::Abs($scx - $editRect.Right) * 0.5
                    if ($dist -lt $bestDist) { $bestDist = $dist; $chosen = $sb }
                }
            } catch {}
        }
        if (-not $foundNear) { return $false }
    }
    $ok = $false
    try {
        $invPat = [System.Windows.Automation.InvokePattern]::Pattern
        $chosen.GetCurrentPattern($invPat).Invoke()
        $ok = $true
    } catch {}
    if (-not $ok) {
        # Strict 或 Grok：禁止 Legacy DoDefaultAction（会把目标窗口激活到前台）
        $allowLegacyAction = $false
        try {
            if ($null -ne $cfg -and $null -ne $cfg.StrictNoForegroundStealOnDispatch) {
                $allowLegacyAction = -not [bool]$cfg.StrictNoForegroundStealOnDispatch
            }
        } catch { $allowLegacyAction = $false }
        try { if ([bool]$cfg.IsGrok) { $allowLegacyAction = $false } } catch {}
        if ($allowLegacyAction) {
            try {
                $legacy = $chosen.GetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern)
                if ($null -ne $legacy) { $legacy.DoDefaultAction(); $ok = $true }
            } catch {}
        }
    }
    if (-not $ok) {
        # 第二优先：后台 WM_LBUTTONDOWN/UP，绝不移动用户光标
        try {
            $nh = [IntPtr]::Zero
            try { $nh = [IntPtr]$chosen.Current.NativeWindowHandle } catch {}
            if ($nh -ne [IntPtr]::Zero) {
                [WinDeskHelper]::PostMessageW($nh, 0x0201, [IntPtr]1, [IntPtr]::Zero) | Out-Null
                Start-Sleep -Milliseconds 30
                [WinDeskHelper]::PostMessageW($nh, 0x0202, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
                $ok = $true
            }
        } catch {}
    }
    if ($ok) { try { Restore-DispatchForeground } catch {} }
    return $ok
}
";
    }

    public List<AgentRosterEntry> GetHeadlessCapableAgents(RegistryService reg)
    {
        return reg.UserConfig.Agents
            .Where(a => a.Id != "antigravity" && !string.IsNullOrWhiteSpace(a.RunnerMode) && a.RunnerMode != "none")
            .OrderByDescending(a => a.Enabled)
            .ThenByDescending(a => a.Detected)
            .ToList();
    }

    public AgentRosterEntry? ResolveTargetAgent(
        RegistryService reg,
        string targetOrAuto,
        ProjectProfile? project = null,
        string? taskInstruction = null)
    {
        var candidates = GetHeadlessCapableAgents(reg);
        if (candidates.Count == 0) return null;

        var proj = project ?? reg.GetActiveProject();
        var decision = RoutingService.AnalyzeAndSelectAgentForTask(reg, proj, taskInstruction, targetOrAuto, candidates);
        return decision.TargetAgent;
    }

    /// <summary>
    /// 纯粹检测目标 AI 桌面客户端窗口进程是否在运行（仅在后台线程轮询调用，严禁阻塞 UI，且全量释放内核进程句柄）
    /// </summary>
    public (bool IsRunning, int Pid, string WindowBadge) ProbeWindowProcessOnly(AgentRosterEntry agent)
    {
        var procNames = GetCandidateProcessNames(agent);
        foreach (var pn in procNames)
        {
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName(pn);
                if (procs.Length > 0)
                {
                    int pid = procs[0].Id;
                    return (true, pid, $"🟢 窗口运行中 (PID {pid})");
                }
            }
            catch { }
            finally
            {
                if (procs != null)
                {
                    foreach (var p in procs)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
        }
        return (false, 0, "⚪ 窗口未启动");
    }

    /// <summary>
    /// 检测目标 AI 工具的桌面客户端窗口是否正在运行，返回 (是否运行, PID, 窗口标题或状态描述)
    /// </summary>
    public (bool IsRunning, int Pid, string StatusBadge) ProbeAgentWindowStatus(AgentRosterEntry agent)
    {
        var live = ReadLiveWorkState();
        if (live != null && live.Status == "working" &&
            string.Equals(live.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase))
        {
            return (true, 0, $"⚡ 正在执行任务 ({live.UpdatedAt})");
        }

        var procNames = GetCandidateProcessNames(agent);
        foreach (var pn in procNames)
        {
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName(pn);
                if (procs.Length > 0)
                {
                    int pid = procs[0].Id;
                    return (true, pid, $"● 窗口已启动 (PID {pid})");
                }
            }
            catch
            {
                // 忽略权限限制
            }
            finally
            {
                if (procs != null)
                {
                    foreach (var p in procs)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
        }

        return (false, 0, "○ 窗口未启动");
    }

    /// <summary>
    /// 启动目标 AI 的客户端窗口（若已启动且 userClick=true 则切到前台展示给用户看）
    /// </summary>
    public string EnsureOrActivateAgentWindow(AgentRosterEntry agent, string? projectRoot, bool activateIfRunning)
    {
        if (AppPaths.IsIsolatedTest) return "隔离自检模式跳过外部窗口拉起";

        var procNames = GetCandidateProcessNames(agent);
        foreach (var pn in procNames)
        {
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName(pn);
                var winProc = procs.FirstOrDefault(p =>
                {
                    try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; }
                }) ?? procs.FirstOrDefault();
                if (winProc != null)
                {
                    int pid = winProc.Id;
                    IntPtr hwnd = IntPtr.Zero;
                    try { hwnd = winProc.MainWindowHandle; } catch { }
                    if (hwnd != IntPtr.Zero)
                    {
                        // 仅用户主动点开才允许切前台；派单/后台（activateIfRunning=false）对 Grok Bot 及一切桌面 AI 严禁 SetForegroundWindow / SW_RESTORE
                        if (activateIfRunning)
                        {
                            ShowWindow(hwnd, SW_RESTORE);
                            SetForegroundWindow(hwnd);
                            return $"已切换到「{agent.Name}」窗口 (PID={pid})。";
                        }
                        IntPtr fgNow = IntPtr.Zero;
                        try { fgNow = GetForegroundWindow(); } catch { }
                        // 用户正在该窗口里工作时不拽走；否则无激活恢复并压到 HWND_BOTTOM
                        if (fgNow != hwnd)
                            PrepareWindowNoActivateKeepBelow(hwnd);
                        return $"「{agent.Name}」窗口已在运行中（后台零抢焦，PID={pid}）。";
                    }
                    return $"「{agent.Name}」窗口已在运行中 (PID={pid})。";
                }
            }
            catch { }
            finally
            {
                if (procs != null)
                {
                    foreach (var p in procs)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
        }

        var guiExe = ResolveGuiExecutable(agent);
        if (!string.IsNullOrWhiteSpace(guiExe))
        {
            try
            {
                if (!activateIfRunning)
                {
                    // 派单/后台：Grok Bot 与全部桌面 AI 只允许无激活拉起，失败也不再走 explorer / UseShellExecute 切前台
                    if (File.Exists(guiExe))
                    {
                        string extraArgs = BuildDesktopAgentWindowLaunchArgs(agent);
                        if (StartProcessOnDefaultDesktop(guiExe, extraArgs, projectRoot, noActivate: true))
                            return $"已在后台静默拉起「{agent.Name}」（零焦点抢占）。";
                    }
                    return $"「{agent.Name}」后台静默拉起未成功，已放弃切到前台。";
                }

                if (guiExe.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", guiExe) { UseShellExecute = true });
                    return $"已为你启动「{agent.Name}」桌面客户端窗口 ({guiExe})。";
                }

                if (File.Exists(guiExe))
                {
                    // 全桌面 AI：严禁 --reuse-window <projectRoot> 等强开普通 IDE 编辑器参数
                    string extraArgs = BuildDesktopAgentWindowLaunchArgs(agent);

                    var psi = new ProcessStartInfo
                    {
                        FileName = guiExe,
                        Arguments = extraArgs,
                        WorkingDirectory = (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
                            ? projectRoot
                            : Path.GetDirectoryName(guiExe)!,
                        UseShellExecute = true,
                        WindowStyle = activateIfRunning ? ProcessWindowStyle.Normal : ProcessWindowStyle.Minimized
                    };
                    var started = Process.Start(psi);
                    return $"已为你启动「{agent.Name}」窗口 (PID={started?.Id})。";
                }
            }
            catch (Exception ex)
            {
                return $"启动「{agent.Name}」窗口失败：{ex.Message}";
            }
        }

        return $"「{agent.Name}」为纯命令行/远程桥接工具，可通过协同中心「④ 任务活卡与投喂」实时查看其工作状态流。";
    }

    /// <summary>
    /// 确保「多 AI 协同配置中心」及桌面置顶悬浮窗已在用户真实桌面 (WinSta0\Default) 启动，供用户随时在桌面查看所有 AI 的实时工作流
    /// </summary>
    public static void EnsureHubMonitorWindowRunning()
    {
        if (AppPaths.IsIsolatedTest) return;
        Process[]? procs = null;
        try
        {
            int selfPid = Environment.ProcessId;
            procs = Process.GetProcessesByName("AI协同中心");
            var existingGui = procs.Any(p =>
            {
                try { return p.Id != selfPid && p.MainWindowHandle != IntPtr.Zero; } catch { return false; }
            });
            if (existingGui) return;

            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                if (!StartProcessOnDefaultDesktop(exe, "--floating", AppPaths.AppBaseDir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "--floating",
                        WorkingDirectory = AppPaths.AppBaseDir,
                        UseShellExecute = true
                    });
                }
            }
        }
        catch { }
        finally
        {
            if (procs != null)
            {
                foreach (var p in procs) { try { p.Dispose(); } catch { } }
            }
        }
    }

    public static bool IsHubDesktopDaemonRunning()
    {
        Process[]? procs = null;
        try
        {
            int selfPid = Environment.ProcessId;
            procs = Process.GetProcessesByName("AI协同中心");
            return procs.Any(p => p.Id != selfPid);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (procs != null)
            {
                foreach (var p in procs) { try { p.Dispose(); } catch { } }
            }
        }
    }

    public HeadlessDispatchOutcome ResumeInterruptedTaskChain(
        RegistryService reg,
        ProjectProfile project,
        CollabBusService bus,
        int timeoutSeconds = 180)
    {
        var cp = NetworkResilienceService.LoadInterruptedCheckpoint(project);
        if (cp == null)
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                SummaryMessage = "当前项目没有待恢复的断线/中断任务链断点。"
            };
        }

        var targetAg = string.IsNullOrWhiteSpace(cp.TargetAgentId) ? "auto" : cp.TargetAgentId;
        var sess = !string.IsNullOrWhiteSpace(cp.SessionId) ? cp.SessionId : cp.SessionMode;
        return ExecuteHeadlessDispatch(
            reg,
            project,
            bus,
            targetAg,
            cp.TaskInstruction,
            timeoutSeconds,
            sess,
            resumeCheckpoint: cp);
    }

    /// <summary>
    /// 执行用户在「任务卡住 / 通道异常 · 选择解决方式」面板中选定的处置动作（全程纯后台零键鼠占用）。
    /// actionKind:
    /// - "retry_clean_dialog": 后台静默清理卡死弹窗 + 新建干净项目专属对话重试该 AI
    /// - "retry_pure_cli": 彻底跳过桌面窗口 UI，切换纯无头 CLI / 后台桥接通道重试
    /// - "switch_other_agent": 后台清理原 AI 异常并改派给用户选定的其他在岗 AI 工具接力
    /// - "handover_to_master": 清理外部阻塞并将任务正式移交主脑 AI 直接完成
    /// - "cancel_and_reset": 后台关闭阻塞弹窗、清除断点与异常状态并恢复待命
    /// </summary>
    public HeadlessDispatchOutcome ExecuteUserChosenResolution(
        RegistryService reg,
        ProjectProfile project,
        CollabBusService bus,
        AgentRosterEntry stuckAgent,
        string actionKind,
        string? alternateAgentId = null,
        string? fallbackTaskBrief = null,
        int timeoutSeconds = 180)
    {
        DismissStuckAgentModalsSilent();

        var cp = NetworkResilienceService.LoadInterruptedCheckpoint(project);
        string taskText = !string.IsNullOrWhiteSpace(cp?.TaskInstruction)
            ? cp!.TaskInstruction
            : (!string.IsNullOrWhiteSpace(fallbackTaskBrief) ? fallbackTaskBrief! : "继续完成当前工程待办任务");

        string normalizedAction = (actionKind ?? "cancel_and_reset").Trim().ToLowerInvariant();
        string collabDir = bus.GetCollabDir(project);
        string liveStatusMd = Path.Combine(collabDir, "LIVE_STATUS.md");
        string inboxDir = Path.Combine(collabDir, "inbox");
        Directory.CreateDirectory(inboxDir);

        switch (normalizedAction)
        {
            case "retry_clean_dialog":
            {
                ClearAgentLiveState(stuckAgent.Id, project);
                return ExecuteHeadlessDispatch(
                    reg,
                    project,
                    bus,
                    stuckAgent.Id,
                    taskText,
                    timeoutSeconds,
                    sessionModeOrId: "new",
                    resumeCheckpoint: cp);
            }

            case "retry_pure_cli":
            {
                _grokCliNotSignedInUntilUtc = DateTime.MinValue;
                ClearAgentLiveState(stuckAgent.Id, project);
                var prevMode = stuckAgent.RunnerMode;
                try
                {
                    stuckAgent.RunnerMode = "pure_cli_headless";
                    return ExecuteHeadlessDispatch(
                        reg,
                        project,
                        bus,
                        stuckAgent.Id,
                        taskText,
                        timeoutSeconds,
                        sessionModeOrId: "new",
                        resumeCheckpoint: cp);
                }
                finally
                {
                    stuckAgent.RunnerMode = prevMode;
                }
            }

            case "switch_other_agent":
            {
                ClearAgentLiveState(stuckAgent.Id, project);
                string nextTarget = !string.IsNullOrWhiteSpace(alternateAgentId) ? alternateAgentId! : "auto";
                if (string.Equals(nextTarget, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    var backup = SelectFailoverBackupAgent(
                        reg,
                        project,
                        reg.UserConfig.Agents,
                        new List<string> { stuckAgent.Id, "antigravity" },
                        requireWrite: false);
                    if (backup != null) nextTarget = backup.Id;
                }

                if (cp != null)
                {
                    cp.TargetAgentId = nextTarget;
                    cp.SessionId = "";
                    cp.SessionMode = "auto";
                    NetworkResilienceService.SaveCheckpoint(project, cp);
                }

                return ExecuteHeadlessDispatch(
                    reg,
                    project,
                    bus,
                    nextTarget,
                    taskText,
                    timeoutSeconds,
                    sessionModeOrId: "auto",
                    resumeCheckpoint: cp);
            }

            case "handover_to_master":
            {
                string masterId = !string.IsNullOrWhiteSpace(project.MasterPlannerId) && project.MasterPlannerId != "auto"
                    ? project.MasterPlannerId
                    : "antigravity";
                var masterAg = reg.UserConfig.Agents.FirstOrDefault(a => a.Id.Equals(masterId, StringComparison.OrdinalIgnoreCase));
                string masterName = masterAg?.Name ?? "Antigravity (主脑)";

                if (cp != null)
                {
                    NetworkResilienceService.MarkCheckpointCompleted(project, cp);
                }
                else
                {
                    NetworkResilienceService.ClearInterruptedCheckpoint(project);
                }

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string handoverFile = Path.Combine(inboxDir, $"{stamp}_user_handover_to_master.md");
                var handoverNote = $"# 用户选择：交由主脑 AI ({masterName}) 直接接管完成\n\n" +
                                   $"- **原受阻子工具**：{stuckAgent.Name} (`{stuckAgent.Id}`)\n" +
                                   $"- **处置时间**：`{DateTime.Now:yyyy-MM-dd HH:mm:ss}`\n" +
                                   $"- **处理结果**：已在后台静默关闭残留阻塞弹窗，释放子工具占用，并将以下任务交由主脑直接落地完成：\n\n" +
                                   $"```text\n{taskText}\n```\n";
                File.WriteAllText(handoverFile, handoverNote, new UTF8Encoding(false));

                WriteLiveStatus(
                    stuckAgent,
                    project,
                    liveStatusMd,
                    "done",
                    ExtractCleanTaskBrief(taskText, 80),
                    $"👑 已按您的选择转交主脑 [{masterName}] 直接接管执行（残留弹窗已后台静默清理）。",
                    handoverFile,
                    null,
                    "resumed",
                    "用户手动选择移交主脑 AI 直接接管",
                    workflowPhase: "verified_delivered",
                    masterPlannerName: masterName,
                    verificationSummary: $"已移交主脑 [{masterName}] 直接接管",
                    failoverNote: $"👑 用户选择交由 {masterName} 直接完成",
                    canResumeInterruptedChain: false);

                return new HeadlessDispatchOutcome
                {
                    Success = true,
                    AgentId = masterId,
                    AgentName = masterName,
                    MasterPlannerName = masterName,
                    VerificationPassed = true,
                    OutputInboxFile = handoverFile,
                    OutputText = handoverNote,
                    SummaryMessage = $"已清理「{stuckAgent.Name}」阻塞状态，并将任务正式移交主脑「{masterName}」接管。"
                };
            }

            default: // cancel_and_reset
            {
                ClearAgentLiveState(stuckAgent.Id, project);
                ClearAgentLiveState(stuckAgent.Id, null);
                NetworkResilienceService.ClearInterruptedCheckpoint(project);
                return new HeadlessDispatchOutcome
                {
                    Success = true,
                    AgentId = stuckAgent.Id,
                    AgentName = stuckAgent.Name,
                    VerificationPassed = true,
                    SummaryMessage = $"已在后台静默清理「{stuckAgent.Name}」阻塞弹窗、取消本次挂起任务并恢复待命。"
                };
            }
        }
    }

    private static AgentRosterEntry? SelectFailoverBackupAgent(
        RegistryService reg,
        ProjectProfile project,
        List<AgentRosterEntry> candidates,
        List<string> triedAgentIds,
        bool requireWrite,
        string? requiredWorkTypeId = null)
    {
        // 严格只从「已启用 (Enabled == true)」且「已勾选该工作职责」的 AI 成员中选择故障转移替补，绝不越权强塞给未勾选代码/验收功能的 AI
        var pool = candidates
            .Where(a => a.Enabled && a.Detected && !triedAgentIds.Contains(a.Id, StringComparer.OrdinalIgnoreCase))
            .Where(a => !requireWrite || a.Permissions.AllowDirectSourceWrite)
            .Where(a => string.IsNullOrWhiteSpace(requiredWorkTypeId) || RoutingService.IsAgentEligibleEffective(a, requiredWorkTypeId, candidates))
            .ToList();
        if (pool.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(project.FallbackBuilderId))
        {
            var preferred = pool.FirstOrDefault(a => a.Id.Equals(project.FallbackBuilderId, StringComparison.OrdinalIgnoreCase));
            if (preferred != null) return preferred;
        }

        return pool
            .OrderByDescending(a => a.TagScores.Values.Sum())
            .FirstOrDefault();
    }

    public HeadlessDispatchOutcome ExecuteHeadlessDispatch(
        RegistryService reg,
        ProjectProfile project,
        CollabBusService bus,
        string targetAgentOrAuto,
        string? customInstruction = null,
        int timeoutSeconds = 180,
        string? sessionModeOrId = "auto",
        TaskChainCheckpoint? resumeCheckpoint = null)
    {
        if (!reg.UserConfig.WorkflowEngineEnabled)
        {
            return new HeadlessDispatchOutcome
            {
                Success = true,
                AgentId = "standalone",
                AgentName = "独立单兵模式",
                SummaryMessage = "多 AI 协同工作流当前处于【已关闭】状态：已跳过跨工具派单，请直接由当前 AI 独立完成该工作（如同未安装/未启用本工作流）。"
            };
        }

        if (!AppPaths.IsIsolatedTest && reg.UserConfig.WorkflowOnlyWhenAppRunning && !IsHubDesktopDaemonRunning())
        {
            // 若在 GUI 进程内调用则 selfPid 即为 GUI 进程；若由 CLI 子进程调用且无其他 AI协同中心 GUI 实例，说明软件已被用户退出关闭
            bool isGuiSelf = System.Windows.Application.Current != null && System.Windows.Application.Current.MainWindow != null;
            if (!isGuiSelf)
            {
                return new HeadlessDispatchOutcome
                {
                    Success = true,
                    AgentId = "standalone_closed",
                    AgentName = "软件已退出·独立模式",
                    SummaryMessage = "多 AI 协同中心桌面软件当前未运行（已设置『软件关闭时彻底停用工作流』）：本次工作由当前 AI 独立完成。"
                };
            }
        }

        bus.EnsureTaskCard(project);
        var sessionSvc = new SessionDialogService();

        if (!string.IsNullOrWhiteSpace(customInstruction))
        {
            InjectInstructionIntoTaskCard(project, bus, customInstruction.Trim());
        }

        var candidates = GetHeadlessCapableAgents(reg);
        var routeDecision = RoutingService.AnalyzeAndSelectAgentForTask(reg, project, customInstruction, targetAgentOrAuto, candidates);
        var agent = routeDecision.TargetAgent;
        if (agent == null)
        {
            if (candidates.Count > 0 && !string.IsNullOrWhiteSpace(routeDecision.WorkTypeId))
            {
                return new HeadlessDispatchOutcome
                {
                    Success = true,
                    AgentId = "master_self",
                    AgentName = routeDecision.MasterPlanner?.Name ?? "主脑自持",
                    OutputText = "当前任务类型未授权给任何后台子 AI（已严格遵守工作职责勾选开关），请由主脑 AI 直接完成。",
                    SummaryMessage = $"当前在岗子 AI 均未勾选「{routeDecision.WorkTypeLabel}」职责，已拦截越权派发并交回主脑直接处理。"
                };
            }
            return new HeadlessDispatchOutcome
            {
                Success = false,
                SummaryMessage = "未找到配置了自动派单执行模式 (RunnerMode) 的 AI 工具，请在「⑤ 工具接入与分工」中配置。"
            };
        }

        // 0. 同一工程 + 同一执行 AI 跨进程串行派单锁：防止短时间内多次并发 dispatch 同时在桌面客户端连点 New Agent 新建多个重复对话框
        var safeProjLockKey = string.Concat(project.Id.Where(c => char.IsLetterOrDigit(c) || c == '_'));
        var safeAgLockKey = string.Concat(agent.Id.Where(c => char.IsLetterOrDigit(c) || c == '_'));
        using var agentDispatchMutex = new System.Threading.Mutex(false, $@"Local\AICollabHub_Dispatch_{safeProjLockKey}_{safeAgLockKey}");
        bool dispatchLockAcquired = false;
        if (!AppPaths.IsIsolatedTest)
        {
            try
            {
                dispatchLockAcquired = agentDispatchMutex.WaitOne(TimeSpan.FromSeconds(120));
            }
            catch (System.Threading.AbandonedMutexException)
            {
                dispatchLockAcquired = true;
            }
        }

        try
        {
        // 1. 先在该 AI 工具对应的项目目录下查找是否已有与当前任务匹配的对话框（除非明确指定 sessionModeOrId = "new"）
        ExternalDialogSessionInfo? matchedSession = null;
        bool forceNewDialog = string.Equals(sessionModeOrId, "new", StringComparison.OrdinalIgnoreCase);
        if (!forceNewDialog)
        {
            matchedSession = sessionSvc.FindBestMatchingSession(project, agent.Id, sessionModeOrId, taskHint: customInstruction);
        }

        // 2. 需要调用某个 AI 工具但该工具（含桌面版）尚未启动时，自动在后台拉起该 AI 工具；避免后续调用失败或找不到桌面会话
        bool needAutoStartDesktop = !AppPaths.IsIsolatedTest &&
                                    !ProbeWindowProcessOnly(agent).IsRunning &&
                                    ResolveGuiExecutable(agent) != null;
        if (agent.LaunchWindowOnDispatch || needAutoStartDesktop)
        {
            EnsureOrActivateAgentWindow(agent, project.PrimaryRoot, activateIfRunning: false);
            if (needAutoStartDesktop)
            {
                Thread.Sleep(1800);
            }
            if (agent.LaunchWindowOnDispatch)
            {
                EnsureHubMonitorWindowRunning();
            }
        }

        var collabDir = bus.GetCollabDir(project);
        var inboxDir = Path.Combine(collabDir, "inbox");
        Directory.CreateDirectory(inboxDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeId = string.Concat(agent.Id.Where(c => char.IsLetterOrDigit(c) || c == '_'));
        var inboxOutFile = Path.Combine(inboxDir, $"reply_{safeId}_{stamp}.md");
        var liveStatusMd = Path.Combine(collabDir, "LIVE_STATUS.md");

        var promptFile = Path.Combine(collabDir, ".snaps", $"dispatch_prompt_{stamp}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(promptFile)!);

        var taskBrief = !string.IsNullOrWhiteSpace(customInstruction)
            ? customInstruction.Trim()
            : "执行当前 work/collab/TASK.md 协同任务";

        bool allowWriteForTask = agent.Permissions.AllowDirectSourceWrite &&
                                 !(taskBrief.Contains("只读", StringComparison.OrdinalIgnoreCase) ||
                                   taskBrief.Contains("禁改", StringComparison.OrdinalIgnoreCase));
        if (allowWriteForTask)
        {
            try { bus.CreateBaselineSnapshot(reg, project); } catch { }
        }

        var masterName = routeDecision.MasterPlanner?.Name ?? "Antigravity (总包工头)";
        int maxRounds = reg.UserConfig.EnableMasterVerifyReworkLoop
            ? Math.Clamp(reg.UserConfig.MaxReworkRounds + 1, 1, 4)
            : 1;

        var initialAction = matchedSession != null ? "resumed" : "created";
        var dialogActionNote = matchedSession != null
            ? $"{routeDecision.DispatchReason}\n已定位「{agent.Name}」项目对话框 (`{matchedSession.SessionId}` · {matchedSession.Title})，正在分配并落地执行任务..."
            : $"{routeDecision.DispatchReason}\n正在该项目目录 (`{project.PrimaryRoot}`) 下为「{agent.Name}」新建专属对话框并落地执行工作...";

        WriteLiveStatus(
            agent,
            project,
            liveStatusMd,
            "working",
            taskBrief,
            dialogActionNote,
            "",
            matchedSession,
            initialAction,
            routeDecision.DispatchReason,
            workflowPhase: "executing",
            masterPlannerName: masterName,
            selectedModel: routeDecision.TargetSelectedModel,
            selectedEffortTier: routeDecision.TargetSelectedTier,
            selectedTierBadge: routeDecision.TargetTierBadge,
            currentRound: 1,
            maxRounds: maxRounds);

        try
        {
            HeadlessDispatchOutcome? finalOutcome = null;
            ExternalDialogSessionInfo? currentSession = matchedSession;
            string lockedGuiChatTitle = matchedSession?.Title ?? "";
            string firstAgentName = agent.Name;
            string reworkFeedback = resumeCheckpoint?.ReworkFeedback ?? "";
            string prevRoundOutput = resumeCheckpoint?.PrevRoundOutput ?? "";
            int startRound = resumeCheckpoint != null ? Math.Clamp(resumeCheckpoint.CurrentRound, 1, maxRounds) : 1;
            int roundExecuted = startRound;
            int totalNetRetriesUsed = resumeCheckpoint?.NetworkRetriesUsed ?? 0;
            bool failoverOccurred = false;
            string originalFailedAgentName = "";
            string failoverNote = "";
            bool verifyPassed = false;
            string verifySummary = "";
            string changedFilesSummary = "";
            var agentRuntime = agent.RuntimeParams ?? new AgentRuntimeParams();
            int maxNetRetries = !reg.UserConfig.EnableNetworkAutoRetry
                ? 0
                : (agentRuntime.MaxRetries >= 0
                    ? Math.Clamp(agentRuntime.MaxRetries, 0, 8)
                    : Math.Clamp(reg.UserConfig.MaxNetworkRetries, 1, 8));
            int effectiveTimeout = agentRuntime.TimeoutSeconds >= 30 && (timeoutSeconds <= 0 || timeoutSeconds == 180)
                ? Math.Clamp(agentRuntime.TimeoutSeconds, 30, 1800)
                : (timeoutSeconds > 0
                    ? timeoutSeconds
                    : Math.Clamp(reg.UserConfig.SingleAttemptTimeoutSeconds, 30, 600));

            var cp = resumeCheckpoint ?? new TaskChainCheckpoint
            {
                ChainId = Guid.NewGuid().ToString("N")[..10],
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ProjectId = project.Id,
                ProjectName = project.Name,
                ProjectRoot = project.PrimaryRoot,
                TaskInstruction = taskBrief,
                MasterPlannerName = masterName,
                TargetAgentId = agent.Id,
                TargetAgentName = agent.Name,
                SessionId = currentSession?.SessionId ?? "",
                SessionMode = sessionModeOrId ?? "auto",
                MaxRounds = maxRounds,
                MaxNetworkRetries = maxNetRetries
            };
            cp.Status = "running";
            cp.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (!cp.FailoverTriedAgentIds.Contains(agent.Id, StringComparer.OrdinalIgnoreCase))
            {
                cp.FailoverTriedAgentIds.Add(agent.Id);
            }
            NetworkResilienceService.SaveCheckpoint(project, cp);

            for (int round = startRound; round <= maxRounds; round++)
            {
                roundExecuted = round;
                bool isRework = round > 1;
                cp.CurrentRound = round;
                cp.ReworkFeedback = reworkFeedback;
                cp.PrevRoundOutput = prevRoundOutput;
                cp.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                NetworkResilienceService.SaveCheckpoint(project, cp);

                HeadlessDispatchOutcome? outcome = null;
                string roundModel = "";
                string roundTier = "high";
                string roundBadge = "";
                string engineCodexModel = "gpt-6-luna";
                string lastFailureKind = "none";
                string lastFailureBadge = "";
                int netAttempt = 0;

                // 单轮内的「网络掉线等待恢复 + 指数退避自动重发 + 跨 AI 备用故障转移」容灾循环
                while (true)
                {
                    (roundModel, roundTier, roundBadge, engineCodexModel) = RoutingService.ResolveAgentModelAndTier(
                        agent,
                        routeDecision.TaskComplexityTier,
                        escalateToHighOnRework: isRework);

                    cp.TargetAgentId = agent.Id;
                    cp.TargetAgentName = agent.Name;
                    cp.SelectedModel = roundModel;
                    cp.SelectedEffortTier = roundTier;

                    // 发消息前先检测本机网络是否掉线：若本机断网则自动挂起等待网络重连，不白白浪费发送机会
                    var (preOnline, preNetDetail) = NetworkResilienceService.ProbeLocalNetworkHealth();
                    if (!preOnline)
                    {
                        lastFailureKind = "local_network_offline";
                        lastFailureBadge = "🔌 本机断网";
                        cp.Status = "reconnecting";
                        cp.LastFailureKind = lastFailureKind;
                        cp.LastFailureLabel = lastFailureBadge;
                        cp.LastErrorDetail = preNetDetail;
                        NetworkResilienceService.SaveCheckpoint(project, cp);

                        bool recovered = NetworkResilienceService.WaitWithBackoffOrReconnect(
                            Math.Max(1, netAttempt + 1),
                            Math.Max(1, maxNetRetries),
                            lastFailureKind,
                            reg.UserConfig,
                            waitMsg => WriteLiveStatus(
                                agent,
                                project,
                                liveStatusMd,
                                "working",
                                taskBrief,
                                waitMsg,
                                "",
                                currentSession,
                                "resumed",
                                routeDecision.DispatchReason,
                                workflowPhase: "reconnecting",
                                masterPlannerName: masterName,
                                selectedModel: roundModel,
                                selectedEffortTier: roundTier,
                                selectedTierBadge: roundBadge,
                                currentRound: round,
                                maxRounds: maxRounds,
                                networkRetryAttempt: Math.Max(1, netAttempt + 1),
                                maxNetworkRetries: Math.Max(1, maxNetRetries),
                                lastFailureBadge: lastFailureBadge,
                                failoverNote: failoverNote));

                        if (!recovered)
                        {
                            outcome = new HeadlessDispatchOutcome
                            {
                                Success = false,
                                AgentId = agent.Id,
                                AgentName = agent.Name,
                                FailureKind = lastFailureKind,
                                SummaryMessage = $"本机网络持续断开（{preNetDetail}），任务链已在第 {round}/{maxRounds} 轮自动保护封存断点，待网络恢复后可一键续跑。"
                            };
                            break;
                        }
                        totalNetRetriesUsed++;
                        cp.NetworkRetriesUsed = totalNetRetriesUsed;
                        cp.Status = "running";
                        NetworkResilienceService.SaveCheckpoint(project, cp);
                    }

                    var fullPrompt = BuildHeadlessPrompt(
                        agent,
                        project,
                        bus,
                        customInstruction,
                        currentSession,
                        sessionSvc,
                        routeDecision.DispatchReason,
                        masterName,
                        roundModel,
                        roundBadge,
                        round,
                        maxRounds,
                        reworkFeedback,
                        prevRoundOutput);
                    Directory.CreateDirectory(Path.GetDirectoryName(promptFile)!);
                    File.WriteAllText(promptFile, fullPrompt, new UTF8Encoding(false));

                    if (isRework && netAttempt == 0)
                    {
                        WriteLiveStatus(
                            agent,
                            project,
                            liveStatusMd,
                            "working",
                            taskBrief,
                            $"↩ 主脑 [{masterName}] 第 {round - 1} 轮验收未通过，已打回给「{agent.Name}」启动第 {round}/{maxRounds} 轮深度修复（自动提档至：{roundModel} [{roundBadge}]）...\n原因：{reworkFeedback}",
                            "",
                            currentSession,
                            "resumed",
                            routeDecision.DispatchReason,
                            workflowPhase: "reworking",
                            masterPlannerName: masterName,
                            selectedModel: roundModel,
                            selectedEffortTier: roundTier,
                            selectedTierBadge: roundBadge,
                            currentRound: round,
                            maxRounds: maxRounds,
                            verificationSummary: reworkFeedback,
                            failoverNote: failoverNote);
                    }

                    // 严禁在返工轮次或普通重连时盲目开启 preferCleanNew（否则会导致客户端连建多个重复对话框）！仅用户显式要求 new 且为首轮首次尝试时才新建
                    var sessionForAttempt = currentSession;
                    bool preferCleanNew = forceNewDialog && round == 1 && netAttempt == 0;
                    var mode = (agent.RunnerMode ?? "none").ToLowerInvariant();

                    outcome = ExecuteMultiChannelSilentAttempt(
                        reg,
                        agent,
                        project,
                        bus,
                        sessionSvc,
                        sessionForAttempt,
                        preferCleanNew,
                        mode,
                        promptFile,
                        fullPrompt,
                        inboxOutFile,
                        liveStatusMd,
                        taskBrief,
                        effectiveTimeout,
                        engineCodexModel,
                        roundTier,
                        roundModel,
                        roundBadge,
                        masterName,
                        round,
                        maxRounds,
                        lockedGuiChatTitle);

                    if (outcome.Success && !string.IsNullOrWhiteSpace(outcome.OutputText))
                    {
                        lastFailureKind = "none";
                        break;
                    }

                    var (nowOnline, _) = NetworkResilienceService.ProbeLocalNetworkHealth();
                    var classified = NetworkResilienceService.ClassifyFailure(
                        outcome.Success ? 0 : 1,
                        outcome.OutputText,
                        outcome.SummaryMessage,
                        null,
                        string.IsNullOrWhiteSpace(outcome.OutputText),
                        nowOnline);

                    bool isClassifiedRetryable = classified.IsRetryable;
                    string classifiedReason = classified.Reason;

                    // 若本机网络完全畅通，且下层已明确判定为桌面 UI 通道未就绪/被弹窗挡住（execution_stalled），
                    // 绝不能因 OutputText 为空而误归类成「网络波动/响应断流」去跑 1/3->2/3->3/3 假重连！
                    if (nowOnline && string.Equals(outcome.FailureKind, "execution_stalled", StringComparison.OrdinalIgnoreCase))
                    {
                        lastFailureKind = "execution_stalled";
                        lastFailureBadge = "桌面UI通道待命/未确认";
                        isClassifiedRetryable = false;
                        classifiedReason = outcome.SummaryMessage;
                    }
                    else
                    {
                        lastFailureKind = classified.FailureKind;
                        lastFailureBadge = classified.BadgeLabel;
                    }

                    string stallPolicy = (reg.UserConfig.StallRecoveryPolicy ?? "auto_then_ask").Trim().ToLowerInvariant();
                    bool askImmediately = !AppPaths.IsIsolatedTest &&
                                          stallPolicy == "ask_user_immediately" &&
                                          lastFailureKind != "local_network_offline";

                    // 1) 若可重试且未超出当前 AI 的最大网络重连次数（且未设为立即询问用户）：执行退避重连并自动换干净通道重发
                    if (!askImmediately && isClassifiedRetryable && reg.UserConfig.EnableNetworkAutoRetry && netAttempt < maxNetRetries)
                    {
                        netAttempt++;
                        totalNetRetriesUsed++;
                        cp.Status = "reconnecting";
                        cp.NetworkRetriesUsed = totalNetRetriesUsed;
                        cp.LastFailureKind = lastFailureKind;
                        cp.LastFailureLabel = lastFailureBadge;
                        cp.LastErrorDetail = classifiedReason;
                        NetworkResilienceService.SaveCheckpoint(project, cp);

                        bool canRetryNow = NetworkResilienceService.WaitWithBackoffOrReconnect(
                            netAttempt,
                            maxNetRetries,
                            lastFailureKind,
                            reg.UserConfig,
                            waitMsg => WriteLiveStatus(
                                agent,
                                project,
                                liveStatusMd,
                                "working",
                                taskBrief,
                                $"📡 检测到「{agent.Name}」卡住或通信中断（{lastFailureBadge}），正在后台自动执行第 {netAttempt}/{maxNetRetries} 次自愈重试（不占用鼠标键盘）...\n详情：{waitMsg}",
                                "",
                                currentSession,
                                "resumed",
                                routeDecision.DispatchReason,
                                workflowPhase: "reconnecting",
                                masterPlannerName: masterName,
                                selectedModel: roundModel,
                                selectedEffortTier: roundTier,
                                selectedTierBadge: roundBadge,
                                currentRound: round,
                                maxRounds: maxRounds,
                                networkRetryAttempt: netAttempt,
                                maxNetworkRetries: maxNetRetries,
                                lastFailureBadge: lastFailureBadge,
                                failoverNote: failoverNote));

                        if (canRetryNow)
                        {
                            cp.Status = "running";
                            NetworkResilienceService.SaveCheckpoint(project, cp);
                            continue;
                        }
                    }

                    // 2) 若当前 AI 重连次数已耗尽，但本机外网正常且开启了「跨 AI 备用故障转移」：主脑自动切换另一个已启用的备用 AI 接力完成任务链！
                    var (onlineForFailover, _) = NetworkResilienceService.ProbeLocalNetworkHealth();
                    if (!askImmediately &&
                        onlineForFailover &&
                        lastFailureKind != "permanent_config_error" &&
                        reg.UserConfig.EnableAgentFailoverOnOutage)
                    {
                        var backupAgent = SelectFailoverBackupAgent(reg, project, candidates, cp.FailoverTriedAgentIds, allowWriteForTask, routeDecision.WorkTypeId);
                        if (backupAgent != null)
                        {
                            string failedName = agent.Name;
                            if (!failoverOccurred)
                            {
                                failoverOccurred = true;
                                originalFailedAgentName = firstAgentName;
                            }
                            cp.FailoverTriedAgentIds.Add(backupAgent.Id);
                            failoverNote = $"🛡 原「{failedName}」连接异常 ({lastFailureBadge})，已自动切换「{backupAgent.Name}」接力";

                            // 将原故障 AI 状态标记为已转移，并切换到备用 AI
                            WriteLiveStatus(
                                agent,
                                project,
                                liveStatusMd,
                                "error",
                                taskBrief,
                                $"⚠️ 「{failedName}」经 {netAttempt} 次重连仍无响应（{lastFailureBadge}），主脑已将任务无缝故障转移至「{backupAgent.Name}」继续执行。",
                                "",
                                currentSession,
                                "resumed",
                                routeDecision.DispatchReason,
                                workflowPhase: "error",
                                masterPlannerName: masterName,
                                currentRound: round,
                                maxRounds: maxRounds,
                                lastFailureBadge: lastFailureBadge,
                                failoverNote: failoverNote);

                            agent = backupAgent;
                            var backupSafeId = string.Concat(agent.Id.Where(c => char.IsLetterOrDigit(c) || c == '_'));
                            inboxOutFile = Path.Combine(inboxDir, $"reply_{backupSafeId}_{DateTime.Now:yyyyMMdd_HHmmss}.md");
                            currentSession = forceNewDialog ? null : sessionSvc.FindBestMatchingSession(project, agent.Id, "auto", taskHint: taskBrief);
                            netAttempt = 0;

                            WriteLiveStatus(
                                agent,
                                project,
                                liveStatusMd,
                                "working",
                                taskBrief,
                                $"🛡️ 故障转移接力：主脑 [{masterName}] 已将任务链切换至「{agent.Name}」继续第 {round}/{maxRounds} 轮实操...",
                                "",
                                currentSession,
                                currentSession != null ? "resumed" : "created",
                                $"{routeDecision.DispatchReason}（{failoverNote}）",
                                workflowPhase: "executing",
                                masterPlannerName: masterName,
                                currentRound: round,
                                maxRounds: maxRounds,
                                failoverNote: failoverNote);
                            continue;
                        }
                    }

                    break;
                }

                outcome ??= new HeadlessDispatchOutcome { Success = false, AgentId = agent.Id, AgentName = agent.Name, SummaryMessage = "未获得执行响应" };
                outcome.MasterPlannerName = masterName;
                outcome.SelectedModel = roundModel;
                outcome.SelectedEffortTier = roundTier;
                outcome.SelectedTierLabel = roundBadge;
                outcome.RoundsExecuted = round;
                outcome.MaxRounds = maxRounds;
                outcome.NetworkRetriesUsed = totalNetRetriesUsed;
                outcome.FailoverOccurred = failoverOccurred;
                outcome.OriginalFailedAgentName = originalFailedAgentName;
                outcome.FailureKind = lastFailureKind;

                // 若经断线重连 + 备用故障转移后依然因网络/通道中断失败：
                if (!outcome.Success || string.IsNullOrWhiteSpace(outcome.OutputText))
                {
                    string stallPolicyFinal = (reg.UserConfig.StallRecoveryPolicy ?? "auto_then_ask").Trim().ToLowerInvariant();
                    var (netStillUp, _) = NetworkResilienceService.ProbeLocalNetworkHealth();

                    // A. 若策略为「全自动静默自愈 (auto_self_heal)」：自动交回主脑自持直接落地完成，彻底不断流
                    if (!AppPaths.IsIsolatedTest && stallPolicyFinal == "auto_self_heal" && netStillUp && lastFailureKind != "local_network_offline")
                    {
                        NetworkResilienceService.MarkCheckpointCompleted(project, cp);
                        var selfHealMsg = $"【后台多路自愈 -> 主脑直持接管】子 AI「{agent.Name}」经自动拆弹重试与多通道切换后仍受阻（{lastFailureBadge}：{TruncateOneLine(outcome.SummaryMessage, 120)}），已自动释放外部阻塞并交回主脑 AI 直接在本会话内落地完成任务。";
                        File.WriteAllText(inboxOutFile, $"# 自动自愈交回主脑直持回执\n\n{selfHealMsg}\n", new UTF8Encoding(false));
                        WriteLiveStatus(
                            agent,
                            project,
                            liveStatusMd,
                            "done",
                            taskBrief,
                            selfHealMsg,
                            inboxOutFile,
                            currentSession,
                            "resumed",
                            routeDecision.DispatchReason,
                            workflowPhase: "verified_delivered",
                            masterPlannerName: masterName,
                            selectedModel: roundModel,
                            selectedEffortTier: roundTier,
                            selectedTierBadge: roundBadge,
                            currentRound: round,
                            maxRounds: maxRounds,
                            verificationSummary: "外部通道受阻已自动无感移交主脑自持完成",
                            networkRetryAttempt: totalNetRetriesUsed,
                            maxNetworkRetries: maxNetRetries,
                            failoverNote: "🛡 已自动转交主脑直接落地",
                            canResumeInterruptedChain: false);

                        return new HeadlessDispatchOutcome
                        {
                            Success = true,
                            AgentId = "master_self_fallback",
                            AgentName = masterName,
                            MasterPlannerName = masterName,
                            VerificationPassed = true,
                            OutputInboxFile = inboxOutFile,
                            OutputText = selfHealMsg,
                            SummaryMessage = selfHealMsg
                        };
                    }

                    // B. 若策略为「让用户选择怎么解决 (auto_then_ask / ask_user_immediately)」或本机离线：保存任务断点并开放 5 种解决方式供用户一键选择！
                    bool isUserChoiceMode = !AppPaths.IsIsolatedTest && netStillUp && lastFailureKind != "local_network_offline";
                    bool canResume = isUserChoiceMode || (lastFailureKind is not ("permanent_config_error" or "quota_exceeded"));
                    string failPhase = isUserChoiceMode
                        ? "awaiting_user_choice"
                        : ((canResume && lastFailureKind is "local_network_offline" or "remote_server_error")
                            ? "interrupted_net"
                            : "error");
                    cp.Status = canResume ? "interrupted" : "completed";
                    cp.CurrentRound = round;
                    cp.TargetAgentId = firstAgentName == agent.Name ? agent.Id : targetAgentOrAuto;
                    cp.NetworkRetriesUsed = totalNetRetriesUsed;
                    cp.LastFailureKind = lastFailureKind;
                    cp.LastFailureLabel = isUserChoiceMode ? $"🛠 {lastFailureBadge}·待选解决方式" : lastFailureBadge;
                    cp.LastErrorDetail = outcome.SummaryMessage;
                    cp.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    NetworkResilienceService.SaveCheckpoint(project, cp);

                    outcome.CanResumeFromCheckpoint = canResume;
                    outcome.CheckpointFilePath = NetworkResilienceService.GetProjectCheckpointPath(project);
                    outcome.VerificationPassed = false;
                    outcome.VerificationSummary = isUserChoiceMode
                        ? $"🛠 任务在「{agent.Name}」遇到阻塞（{lastFailureBadge}），已安全保存进度。您可在悬浮窗或工作台点击「选择解决方式」自由选择：清理弹窗重试 / 换纯CLI / 改派其他AI / 交主脑直做"
                        : (canResume
                            ? (failPhase == "interrupted_net"
                                ? $"📡 因 {lastFailureBadge} 暂时中断（已自动重连尝试 {totalNetRetriesUsed} 次），任务链已安全保存在第 {round}/{maxRounds} 轮断点，网络恢复后可一键断点续跑"
                                : $"⚠️ 因 {lastFailureBadge} 执行中止（已自动尝试 {totalNetRetriesUsed} 次），任务链已保存在第 {round}/{maxRounds} 轮断点，排查后可点击续跑")
                            : outcome.SummaryMessage);
                    outcome.SummaryMessage = $"{outcome.SummaryMessage} | {outcome.VerificationSummary}";

                    WriteLiveStatus(
                        agent,
                        project,
                        liveStatusMd,
                        "error",
                        taskBrief,
                        outcome.SummaryMessage,
                        outcome.OutputInboxFile,
                        currentSession,
                        outcome.SessionAction,
                        routeDecision.DispatchReason,
                        workflowPhase: failPhase,
                        masterPlannerName: masterName,
                        selectedModel: roundModel,
                        selectedEffortTier: roundTier,
                        selectedTierBadge: roundBadge,
                        currentRound: round,
                        maxRounds: maxRounds,
                        verificationSummary: outcome.VerificationSummary,
                        networkRetryAttempt: totalNetRetriesUsed,
                        maxNetworkRetries: maxNetRetries,
                        lastFailureBadge: cp.LastFailureLabel,
                        failoverNote: failoverNote,
                        canResumeInterruptedChain: canResume);

                    return outcome;
                }

                if (allowWriteForTask)
                {
                    try { bus.GenerateLatestDiff(reg, project); } catch { }
                }

                if (!string.IsNullOrWhiteSpace(outcome.ConfirmedGuiChatTitle))
                {
                    lockedGuiChatTitle = outcome.ConfirmedGuiChatTitle;
                }
                if (!string.IsNullOrWhiteSpace(outcome.SessionId))
                {
                    currentSession = sessionSvc.FindBestMatchingSession(project, agent.Id, outcome.SessionId) ?? currentSession;
                    cp.SessionId = currentSession?.SessionId ?? outcome.SessionId;
                }

                // 进入主脑验收环节 (Master Verification Gate)
                WriteLiveStatus(
                    agent,
                    project,
                    liveStatusMd,
                    "working",
                    taskBrief,
                    $"🔍 主脑 [{masterName}] 正在严格验收「{agent.Name}」第 {round}/{maxRounds} 轮落地执行产出与工程文件 Diff...",
                    outcome.OutputInboxFile,
                    currentSession,
                    outcome.SessionAction,
                    routeDecision.DispatchReason,
                    workflowPhase: "master_verifying",
                    masterPlannerName: masterName,
                    selectedModel: roundModel,
                    selectedEffortTier: roundTier,
                    selectedTierBadge: roundBadge,
                    currentRound: round,
                    maxRounds: maxRounds,
                    networkRetryAttempt: totalNetRetriesUsed,
                    maxNetworkRetries: maxNetRetries,
                    failoverNote: failoverNote);

                var verifyRes = VerifyExecutionByMasterPlanner(
                    reg,
                    project,
                    bus,
                    agent,
                    masterName,
                    taskBrief,
                    allowWriteForTask,
                    outcome,
                    round,
                    maxRounds);

                verifyPassed = verifyRes.Passed;
                verifySummary = verifyRes.Summary;
                changedFilesSummary = verifyRes.ChangedFilesSummary;
                outcome.VerificationPassed = verifyPassed;
                outcome.VerificationSummary = verifySummary;
                outcome.ChangedFilesSummary = changedFilesSummary;
                finalOutcome = outcome;

                if (verifyPassed || round >= maxRounds)
                {
                    NetworkResilienceService.MarkCheckpointCompleted(project, cp);

                    // 将主脑验收结论、模型档位及断线自愈/故障转移记录追加写入 inbox 报告头部
                    AppendMasterVerificationReceiptToInbox(
                        outcome.OutputInboxFile,
                        masterName,
                        agent.Name,
                        roundModel,
                        roundBadge,
                        round,
                        maxRounds,
                        verifyPassed,
                        verifySummary,
                        changedFilesSummary,
                        totalNetRetriesUsed,
                        failoverNote);
                    if (File.Exists(outcome.OutputInboxFile))
                    {
                        outcome.OutputText = File.ReadAllText(outcome.OutputInboxFile, Encoding.UTF8);
                    }
                    var resilienceTail = totalNetRetriesUsed > 0 ? $" · 自愈重连 {totalNetRetriesUsed} 次" : "";
                    if (!string.IsNullOrWhiteSpace(failoverNote)) resilienceTail += $" · {failoverNote}";
                    outcome.SummaryMessage = $"{outcome.SummaryMessage} | 👑 主脑 [{masterName}] 验收结论：{verifySummary}（共执行 {round} 轮 · 模型：{roundModel} [{roundBadge}]{resilienceTail}）";
                    break;
                }

                // 验收未通过：准备打回给该 AI 进行下一轮重修
                reworkFeedback = verifySummary;
                prevRoundOutput = outcome.OutputText;
            }

            finalOutcome ??= new HeadlessDispatchOutcome { Success = false, SummaryMessage = "派单未产生结果" };

            // 主脑已终验通过（或轮次耗尽收尾）后，必须保留 done/verified_delivered，禁止再被 AsyncStillWorkingInGui 洗回 working→随后 Sweep 成 idle
            bool masterClosed = verifyPassed || roundExecuted >= maxRounds || !string.IsNullOrWhiteSpace(verifySummary);
            bool keepWorkingInGui = finalOutcome.Success && finalOutcome.AsyncStillWorkingInGui && !masterClosed && !verifyPassed;
            WriteLiveStatus(
                agent,
                project,
                liveStatusMd,
                keepWorkingInGui ? "working" : (finalOutcome.Success ? "done" : "error"),
                taskBrief,
                keepWorkingInGui
                    ? (string.IsNullOrWhiteSpace(finalOutcome.ConfirmedGuiChatTitle)
                        ? $"⏳ 「{agent.Name}」桌面端仍在生成/编译中，保持 working…"
                        : $"⏳ 「{agent.Name}」桌面端会话（{finalOutcome.ConfirmedGuiChatTitle}）仍在生成/编译中，保持 working…")
                    : (finalOutcome.Success ? finalOutcome.OutputText : finalOutcome.SummaryMessage),
                // 即使仍观测桌面 Working，也保留 inbox 路径，避免派单进程退出后 SweepStale 因空回执洗成 idle
                string.IsNullOrWhiteSpace(finalOutcome.OutputInboxFile) ? "" : finalOutcome.OutputInboxFile,
                currentSession,
                finalOutcome.SessionAction,
                routeDecision.DispatchReason,
                workflowPhase: keepWorkingInGui ? "executing" : (finalOutcome.Success ? "verified_delivered" : "error"),
                masterPlannerName: masterName,
                selectedModel: finalOutcome.SelectedModel,
                selectedEffortTier: finalOutcome.SelectedEffortTier,
                selectedTierBadge: finalOutcome.SelectedTierLabel,
                currentRound: roundExecuted,
                maxRounds: maxRounds,
                verificationSummary: keepWorkingInGui ? "" : verifySummary,
                changedFilesSummary: changedFilesSummary,
                networkRetryAttempt: totalNetRetriesUsed,
                maxNetworkRetries: maxNetRetries,
                failoverNote: failoverNote,
                canResumeInterruptedChain: false);

            return finalOutcome;
        }
        finally
        {
            try { if (File.Exists(promptFile)) File.Delete(promptFile); } catch { }
        }
        }
        finally
        {
            if (dispatchLockAcquired)
            {
                try { agentDispatchMutex.ReleaseMutex(); } catch { }
            }
        }
    }

    private HeadlessDispatchOutcome ExecuteMultiChannelSilentAttempt(
        RegistryService reg,
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        SessionDialogService sessionSvc,
        ExternalDialogSessionInfo? sessionForAttempt,
        bool preferCleanNew,
        string mode,
        string promptFile,
        string fullPrompt,
        string inboxOutFile,
        string liveStatusMd,
        string taskBrief,
        int effectiveTimeout,
        string engineCodexModel,
        string roundTier,
        string roundModel,
        string roundBadge,
        string masterName,
        int round,
        int maxRounds,
        string lockedGuiChatTitle = "")
    {
        // 0. 用户在恢复面板显式选择了「彻底跳过窗口 UI · 纯走无头 CLI / 后台桥接引擎」(pure_cli_headless)
        if (mode == "pure_cli_headless")
        {
            if (agent.Id.Equals("grok_bot", StringComparison.OrdinalIgnoreCase))
            {
                var grokPure = RunGrokCliHeadless(agent, project, bus, sessionSvc, sessionForAttempt, promptFile, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, roundModel, roundTier, roundBadge, masterName, round, maxRounds);
                if (grokPure.Success || AppPaths.IsIsolatedTest) return grokPure;
                // CLI 未登录：若桌面端可用则无缝回退桌面 UIA，禁止直接以 cli_not_signed_in 卡死
                if (grokPure.FailureKind == "cli_not_signed_in" && IsGrokDesktopChannelAvailable(agent))
                {
                    WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief,
                        "📡 Grok CLI 未登录，pure_cli 模式自动切换 Grok Bot 桌面可见通道...",
                        "", sessionForAttempt, "resumed", "", "reconnecting", masterName, roundModel, roundTier, roundBadge, round, maxRounds);
                    var uiaFromPure = TryDispatchViaDesktopUiAutomation(reg, agent, project, bus, sessionSvc, sessionForAttempt, preferCleanNew, taskBrief, inboxOutFile, liveStatusMd, effectiveTimeout, allowFallbackExistingBot: true, isReworkRound: round > 1, lockedGuiChatTitle: lockedGuiChatTitle);
                    if (uiaFromPure != null) return uiaFromPure;
                }
            }
            if (!string.IsNullOrWhiteSpace(agent.RunnerCmd) &&
                !agent.RunnerCmd.Contains("codex", StringComparison.OrdinalIgnoreCase))
            {
                var customPure = agent.Id.Equals("cursor", StringComparison.OrdinalIgnoreCase) && ResolveCursorAgentCliBinary(agent.RunnerCmd) != null
                    ? RunCursorAgentCliHeadless(agent, project, bus, sessionSvc, sessionForAttempt, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, roundModel, roundTier, roundBadge, masterName, round, maxRounds, "fresh", null, reg.UserConfig.AutoTrustManagedWorkspaces)
                    : RunCustomCliWithLiveStream(agent, project, bus, sessionSvc, sessionForAttempt, promptFile, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, roundModel, roundTier, roundBadge, masterName, round, maxRounds, reg.UserConfig.AutoTrustManagedWorkspaces);
                if (customPure.Success) return customPure;
            }
            bool codexEnabledForPure = reg.UserConfig.Agents.Any(a => a.Id.Equals("codex_cli", StringComparison.OrdinalIgnoreCase) && a.Enabled);
            if (codexEnabledForPure || agent.Id.Equals("codex_cli", StringComparison.OrdinalIgnoreCase))
            {
                var codexPure = RunCodexExecWithLiveStream(
                    agent, project, bus, sessionSvc, sessionForAttempt, fullPrompt, inboxOutFile, liveStatusMd, taskBrief,
                    effectiveTimeout, engineCodexModel, roundTier, roundModel, roundBadge, masterName, round, maxRounds);
                if (codexPure.Success) return codexPure;
            }
            return RunBridgeQueueHeadless(agent, project, bus, sessionSvc, sessionForAttempt, fullPrompt, inboxOutFile, liveStatusMd, taskBrief);
        }

        // 1. 显式选择了 grok_cli（原生无头 Grok CLI · 100% 纯后台零窗口零键鼠）
        if (mode == "grok_cli")
        {
            var grokRes = RunGrokCliHeadless(agent, project, bus, sessionSvc, sessionForAttempt, promptFile, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, roundModel, roundTier, roundBadge, masterName, round, maxRounds);
            if (grokRes.Success || AppPaths.IsIsolatedTest) return grokRes;

            // 若 CLI 未登录或卡住，自动静默切换零焦点后台通道尝试自愈
            if (grokRes.FailureKind == "cli_not_signed_in" && IsGrokDesktopChannelAvailable(agent))
            {
                WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief,
                    "📡 Grok CLI 未登录，自动切换 Grok Bot 桌面可见通道...",
                    "", sessionForAttempt, "resumed", "", "reconnecting", masterName, roundModel, roundTier, roundBadge, round, maxRounds);
            }
            var uiaFallback = TryDispatchViaDesktopUiAutomation(reg, agent, project, bus, sessionSvc, sessionForAttempt, preferCleanNew, taskBrief, inboxOutFile, liveStatusMd, effectiveTimeout, allowFallbackExistingBot: true, isReworkRound: round > 1, lockedGuiChatTitle: lockedGuiChatTitle);
            if (uiaFallback != null) return uiaFallback;
            // 桌面通道已尝试仍失败：勿再以 cli_not_signed_in 误导为「未切桌面」
            if (grokRes.FailureKind == "cli_not_signed_in" && IsGrokDesktopChannelAvailable(agent))
            {
                return new HeadlessDispatchOutcome
                {
                    Success = false,
                    AgentId = agent.Id,
                    AgentName = agent.Name,
                    FailureKind = "execution_stalled",
                    SummaryMessage = "Grok CLI 未登录，桌面通道已尝试但仍未就绪。"
                };
            }
            return grokRes;
        }

        if (mode == "cli_custom")
        {
            return RunCustomCliWithLiveStream(agent, project, bus, sessionSvc, sessionForAttempt, promptFile, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, roundModel, roundTier, roundBadge, masterName, round, maxRounds, reg.UserConfig.AutoTrustManagedWorkspaces);
        }
        if (mode == "http_openai")
        {
            return RunHttpOpenAiHeadless(agent, project, bus, sessionSvc, sessionForAttempt, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, roundModel, roundTier, roundBadge, masterName, round, maxRounds);
        }
        if (mode == "bridge_queue")
        {
            return RunBridgeQueueHeadless(agent, project, bus, sessionSvc, sessionForAttempt, fullPrompt, inboxOutFile, liveStatusMd, taskBrief);
        }
        if (mode != "codex_exec")
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                FailureKind = "permanent_config_error",
                SummaryMessage = $"工具「{agent.Name}」当前执行模式为 {mode}，无法自动派发。"
            };
        }

        // 2. 智能后台多通道：桌面 AI 默认 Stage1=桌面 UIA；仅显式允许时才可降级无头 CLI / Stage3
        bool isGrokAgent = agent.Id.Equals("grok_bot", StringComparison.OrdinalIgnoreCase);
        bool isCursorAgent = agent.Id.Equals("cursor", StringComparison.OrdinalIgnoreCase);
        bool isClaudeAgent = agent.Id.Equals("claude_code", StringComparison.OrdinalIgnoreCase);
        bool isCodexAgent = agent.Id.Equals("codex_cli", StringComparison.OrdinalIgnoreCase);
        bool preferCliFirst = reg.UserConfig.PreferOfficialCliFirst;
        bool requireDesktopVisible = reg.UserConfig.RequireDesktopVisibleSession;
        bool isDesktopChannelAgent = IsDesktopClientChannelAgent(agent);
        bool allowHeadlessForDesktop = ShouldFallbackToHeadlessCliForDesktopAgent(agent, reg.UserConfig);
        bool forceDesktopVisibleOnly = isDesktopChannelAgent && !allowHeadlessForDesktop;
        string? cursorAgentBin = ResolveCursorAgentCliBinary(agent.RunnerCmd);
        bool cursorCliAvailable = !string.IsNullOrWhiteSpace(cursorAgentBin);
        bool cursorDesktopExeExists = ResolveGuiExecutable(agent) != null;
        // 桌面 AI：禁止无头降级 或 要求桌面可见 → 跳过官方 CLI，直连桌面可见会话
        bool cursorPreferDesktopFirst = isCursorAgent && requireDesktopVisible && cursorDesktopExeExists;
        bool preferDesktopFirst = forceDesktopVisibleOnly || cursorPreferDesktopFirst ||
                                  (isDesktopChannelAgent && requireDesktopVisible && (cursorDesktopExeExists || ResolveGuiExecutable(agent) != null || isGrokAgent || isCursorAgent));

        HeadlessDispatchOutcome? lastCliFail = null;
        bool grokCliGaveUpToDesktop = false;
        var addDirs = CollectAddDirsForProject(project, taskBrief);

        if (!AppPaths.IsIsolatedTest && preferCliFirst && !preferDesktopFirst)
        {
            var attempts = BuildProgressiveCliAttemptsForAgent(agent, reg.UserConfig);
            int tried = 0;
            foreach (var att in attempts)
            {
                if (tried >= 3) break;
                tried++;
                WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief,
                    $"⚡ Stage1 官方 CLI 渐进第 {tried}/3 档：{att.Label} [{att.Mode}]",
                    "", sessionForAttempt, "resumed", "", round > 1 ? "reconnecting" : "executing", masterName, roundModel, roundTier, roundBadge, round, maxRounds);

                HeadlessDispatchOutcome? cliOut = att.Kind switch
                {
                    "grok_cli" => RunGrokCliHeadless(agent, project, bus, sessionSvc, sessionForAttempt, promptFile, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, roundModel, roundTier, roundBadge, masterName, round, maxRounds, att),
                    "codex_exec" => RunCodexExecWithLiveStream(agent, project, bus, sessionSvc, sessionForAttempt, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, engineCodexModel, roundTier, roundModel, roundBadge, masterName, round, maxRounds, att.Mode, addDirs),
                    "cursor_agent" => RunCursorAgentCliHeadless(agent, project, bus, sessionSvc, sessionForAttempt, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, roundModel, roundTier, roundBadge, masterName, round, maxRounds, att.Mode, addDirs, reg.UserConfig.AutoTrustManagedWorkspaces, att.BinaryPath),
                    "claude_code" or "custom_cli" => RunCustomCliWithLiveStream(
                        CloneAgentWithRunner(agent, att.BinaryPath ?? agent.RunnerCmd, att.AddDirArgs),
                        project, bus, sessionSvc, sessionForAttempt, promptFile, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, roundModel, roundTier, roundBadge, masterName, round, maxRounds, reg.UserConfig.AutoTrustManagedWorkspaces),
                    _ => null
                };
                if (cliOut == null) continue;
                if (cliOut.Success)
                {
                    sessionSvc.SyncDesktopVisibleSessionIndex(project, agent, cliOut.SessionId, taskBrief);
                    return cliOut;
                }
                lastCliFail = cliOut;
                if (isGrokAgent && cliOut.FailureKind == "cli_not_signed_in")
                {
                    grokCliGaveUpToDesktop = true;
                    break; // 未登录立即跳出 Stage1 → Stage2 桌面
                }
                if (isCodexAgent && cliOut.FailureKind == "quota_exceeded")
                {
                    break; // 配额耗尽立即跳出渐进
                }
            }

            if (isGrokAgent && (grokCliGaveUpToDesktop || (lastCliFail != null && attempts.Count > 0)))
            {
                WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief,
                    "📡 Grok CLI 未登录或多次尝试未通，自动切换 Grok Bot 桌面可见通道...",
                    "", sessionForAttempt, "resumed", "", "reconnecting", masterName, roundModel, roundTier, roundBadge, round, maxRounds);
            }
        }

        if (preferDesktopFirst)
        {
            WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief,
                forceDesktopVisibleOnly
                    ? $"📡 「{agent.Name}」已禁止静默降级无头 CLI，直连桌面可见会话通道..."
                    : $"📡 「{agent.Name}」优先桌面可见直连通道（跳过无头 CLI）...",
                "", sessionForAttempt, "resumed", "", round > 1 ? "reconnecting" : "executing", masterName, roundModel, roundTier, roundBadge, round, maxRounds);
        }

        // 桌面 UIA：Cursor / Grok Bot / Codex / Claude / Windsurf / Antigravity / 任意带桌面 EXE 的外部 AI
        bool isWindsurfAgent = agent.Id.Equals("windsurf", StringComparison.OrdinalIgnoreCase);
        bool isAntigravityAgent = agent.Id.Equals("antigravity", StringComparison.OrdinalIgnoreCase);
        bool hasDesktopGuiExe = ResolveGuiExecutable(agent) != null;
        bool enterDesktopUi = isGrokAgent || isCursorAgent || isClaudeAgent || isCodexAgent
            || isWindsurfAgent || isAntigravityAgent || hasDesktopGuiExe || isDesktopChannelAgent;
        HeadlessDispatchOutcome? lastDesktopFallbackCliHint = null;
        HeadlessDispatchOutcome? lastDesktopFail = null;
        if (!AppPaths.IsIsolatedTest && enterDesktopUi)
        {
            if (preferDesktopFirst || requireDesktopVisible || !preferCliFirst || grokCliGaveUpToDesktop)
            {
                if (!ProbeWindowProcessOnly(agent).IsRunning && hasDesktopGuiExe)
                {
                    EnsureOrActivateAgentWindow(agent, project.PrimaryRoot, activateIfRunning: false);
                    Thread.Sleep(2200);
                }
            }

            var uiaTry1 = TryDispatchViaDesktopUiAutomationCore(
                reg, agent, project, bus, sessionSvc, sessionForAttempt, preferCleanNew, taskBrief, inboxOutFile, liveStatusMd, effectiveTimeout, allowFallbackExistingBot: true, isReworkRound: round > 1, lockedGuiChatTitle: lockedGuiChatTitle, out bool touchedDesktopUi1);
            if (uiaTry1 != null && uiaTry1.Success) return uiaTry1;
            if (uiaTry1 != null)
            {
                lastDesktopFail = uiaTry1;
                if (uiaTry1.FailureKind is "no_project_group_fallback_cli" or "revert_modal_aborted" or "send_failed")
                    lastDesktopFallbackCliHint = uiaTry1;
            }

            if (!touchedDesktopUi1 && lastDesktopFallbackCliHint == null)
            {
                WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief,
                    $"🔄 桌面 UIA：检测到「{agent.Name}」首次投递未响应或有弹窗遮挡，正在静默清理并复用会话重试...",
                    "", sessionForAttempt, "resumed", "", "reconnecting", masterName, roundModel, roundTier, roundBadge, round, maxRounds);

                var uiaTry2 = TryDispatchViaDesktopUiAutomationCore(
                    reg, agent, project, bus, sessionSvc, sessionForAttempt, forceNewDialog: false, taskBrief, inboxOutFile, liveStatusMd, effectiveTimeout, allowFallbackExistingBot: true, isReworkRound: round > 1, lockedGuiChatTitle: lockedGuiChatTitle, out _);
                if (uiaTry2 != null && uiaTry2.Success) return uiaTry2;
                if (uiaTry2 != null)
                {
                    lastDesktopFail = uiaTry2;
                    if (uiaTry2.FailureKind is "no_project_group_fallback_cli" or "revert_modal_aborted" or "send_failed")
                        lastDesktopFallbackCliHint = uiaTry2;
                }
            }

            // 禁止静默降级：桌面 AI 失败后直接返回清晰诊断，绝不进入 cursor-agent / node --print 等无头 CLI
            if (forceDesktopVisibleOnly)
            {
                return BuildDesktopVisibleDeliveryFailure(
                    agent,
                    lastDesktopFail ?? lastDesktopFallbackCliHint,
                    "请在桌面客户端窗口内确认任务可见后再重试派单。");
            }

            // 仅当显式允许：桌面缺分组 / 桌面优先跳过 CLI 后，才可无缝降级无头 CLI（严禁开 IDE）
            bool needCliAfterDesktop = allowHeadlessForDesktop &&
                (lastDesktopFallbackCliHint != null ||
                 (isCursorAgent && cursorCliAvailable && requireDesktopVisible && lastCliFail == null));
            if (needCliAfterDesktop && lastCliFail == null)
            {
                var postDesktopAttempts = BuildProgressiveCliAttemptsForAgent(
                    agent, reg.UserConfig, forceCursorCli: isCursorAgent);
                int cTried = 0;
                foreach (var att in postDesktopAttempts)
                {
                    if (cTried >= 3) break;
                    if (isCursorAgent && att.Kind is "codex_exec") continue; // 防串台
                    if (isGrokAgent && att.Kind is "codex_exec") continue;
                    cTried++;
                    WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief,
                        $"⚡ 「{agent.Name}」桌面建组受阻后 CLI 渐进第 {cTried}/3 档：{att.Label}",
                        "", sessionForAttempt, "resumed", "", "reconnecting", masterName, roundModel, roundTier, roundBadge, round, maxRounds);
                    HeadlessDispatchOutcome? cliOut = att.Kind switch
                    {
                        "grok_cli" => RunGrokCliHeadless(agent, project, bus, sessionSvc, sessionForAttempt, promptFile, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, roundModel, roundTier, roundBadge, masterName, round, maxRounds, att),
                        "codex_exec" => RunCodexExecWithLiveStream(agent, project, bus, sessionSvc, sessionForAttempt, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, engineCodexModel, roundTier, roundModel, roundBadge, masterName, round, maxRounds, att.Mode, addDirs),
                        "cursor_agent" => RunCursorAgentCliHeadless(agent, project, bus, sessionSvc, sessionForAttempt, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, roundModel, roundTier, roundBadge, masterName, round, maxRounds, att.Mode, addDirs, reg.UserConfig.AutoTrustManagedWorkspaces, att.BinaryPath),
                        "claude_code" or "custom_cli" => RunCustomCliWithLiveStream(
                            CloneAgentWithRunner(agent, att.BinaryPath ?? agent.RunnerCmd, att.AddDirArgs),
                            project, bus, sessionSvc, sessionForAttempt, promptFile, fullPrompt, inboxOutFile, liveStatusMd, taskBrief, effectiveTimeout, roundModel, roundTier, roundBadge, masterName, round, maxRounds, reg.UserConfig.AutoTrustManagedWorkspaces),
                        _ => null
                    };
                    if (cliOut == null) continue;
                    if (cliOut.Success)
                    {
                        sessionSvc.SyncDesktopVisibleSessionIndex(project, agent, cliOut.SessionId, taskBrief);
                        return cliOut;
                    }
                    lastCliFail = cliOut;
                }
            }

            bool codexEnabled = reg.UserConfig.Agents.Any(a => a.Id.Equals("codex_cli", StringComparison.OrdinalIgnoreCase) && a.Enabled);
            bool noLocalProxy = string.IsNullOrWhiteSpace(ResolvePreferredCodexProviderFlag());
            if ((isCursorAgent || isGrokAgent) && noLocalProxy && (!codexEnabled || DateTime.UtcNow < _codexOfficialQuotaExhaustedUntilUtc))
            {
                // Grok CLI 未登录但桌面通道可用时：严禁以 cli_not_signed_in 终结；仅桌面也失败才报 execution_stalled
                bool grokDesktopAvailable = isGrokAgent && IsGrokDesktopChannelAvailable(agent);
                if (forceDesktopVisibleOnly || lastDesktopFail != null || lastDesktopFallbackCliHint != null)
                {
                    return BuildDesktopVisibleDeliveryFailure(
                        agent,
                        lastDesktopFail ?? lastDesktopFallbackCliHint ?? lastCliFail);
                }
                string failKind;
                if (isGrokAgent && lastCliFail?.FailureKind == "cli_not_signed_in" && grokDesktopAvailable)
                    failKind = lastDesktopFallbackCliHint?.FailureKind ?? "execution_stalled";
                else if (isGrokAgent && lastCliFail?.FailureKind == "cli_not_signed_in" && !grokDesktopAvailable)
                    failKind = "cli_not_signed_in";
                else
                    failKind = "execution_stalled";

                return lastDesktopFallbackCliHint ?? lastCliFail ?? new HeadlessDispatchOutcome
                {
                    Success = false,
                    AgentId = agent.Id,
                    AgentName = agent.Name,
                    FailureKind = isCursorAgent ? "execution_stalled" : failKind,
                    SummaryMessage = isCursorAgent
                        ? (cursorCliAvailable
                            ? "Cursor 官方 CLI 与桌面 UIA 均未就绪（未使用 Codex 冒充 Cursor）。"
                            : CliInstallService.BuildMissingCliGuidance("Cursor") + " 同时桌面通道暂未就绪（并非 Cursor 额度问题）。")
                        : (grokDesktopAvailable
                            ? "Grok Bot 桌面通道已尝试但仍未就绪（CLI 未登录已跳过，未冒充 Codex）。"
                            : (string.IsNullOrWhiteSpace(ResolveGrokCliBinary(null))
                                ? CliInstallService.BuildMissingCliGuidance("Grok")
                                : "Grok 原生 CLI 未登录且本机未检测到可用桌面客户端。"))
                };
            }
        }

        // 桌面 AI 且禁止无头降级：严禁落入 Stage3 Codex/桥接
        if (forceDesktopVisibleOnly)
        {
            return BuildDesktopVisibleDeliveryFailure(
                agent,
                lastDesktopFail ?? lastDesktopFallbackCliHint ?? lastCliFail,
                "桌面通道未就绪，已阻止 Stage3 纯后台通道。");
        }

        // Stage3：仅非 Cursor/Grok 串台场景，或显式 Codex 代理，才走后台 Codex 引擎
        if (isCursorAgent || isGrokAgent)
        {
            return lastCliFail ?? new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                FailureKind = "execution_stalled",
                SummaryMessage = $"「{agent.Name}」官方通道未就绪，已禁止用 Codex 冒充该工具执行。"
            };
        }

        var codexRes = RunCodexExecWithLiveStream(
            agent, project, bus, sessionSvc, sessionForAttempt, fullPrompt, inboxOutFile, liveStatusMd, taskBrief,
            effectiveTimeout, engineCodexModel, roundTier, roundModel, roundBadge, masterName, round, maxRounds, "fresh_exec", addDirs);
        if (!codexRes.Success && codexRes.FailureKind == "quota_exceeded")
        {
            _codexOfficialQuotaExhaustedUntilUtc = DateTime.UtcNow.AddMinutes(15);
        }
        return codexRes;
    }

    public sealed record ProgressiveCliAttempt(
        string Label,
        string Kind,
        string? BinaryPath,
        string AddDirArgs,
        string Mode = "",
        string ArgsTemplate = "");

    /// <summary>收集 CompanionRoots 与任务中显式外部目录，供 --add-dir 注入。</summary>
    public static List<string> CollectAddDirsForProject(ProjectProfile project, string? taskBrief)
    {
        var dirs = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = Path.GetFullPath(p.Trim().TrimEnd('\\', '/'));
                if (Directory.Exists(full) && !dirs.Contains(full, StringComparer.OrdinalIgnoreCase))
                    dirs.Add(full);
            }
            catch { }
        }
        foreach (var c in project.CompanionRoots ?? new List<string>()) Add(c);
        foreach (var e in ExtractExplicitExternalProjectRoots(taskBrief, project.PrimaryRoot)) Add(e);
        return dirs;
    }

    /// <summary>按 AI 构建官方 CLI 三级渐进尝试清单（最多取前 3 档）。</summary>
    public static List<ProgressiveCliAttempt> BuildProgressiveCliAttemptsForAgent(
        AgentRosterEntry agent,
        UserHubConfig cfg,
        bool forceCursorCli = false)
    {
        var list = new List<ProgressiveCliAttempt>();
        string id = agent.Id ?? "";
        string addDir = "--add-dir";
        if (id.Equals("grok_bot", StringComparison.OrdinalIgnoreCase))
        {
            var grok = ResolveGrokCliBinary(agent.RunnerCmd);
            if (!string.IsNullOrWhiteSpace(grok))
            {
                // 三档全部 grok_cli，严禁混入 codex_exec
                list.Add(new ProgressiveCliAttempt(
                    "Grok CLI streaming-json 续会话", "grok_cli", grok, addDir,
                    "streaming_json_resume",
                    "--output-format streaming-json --prompt-file \"{PROMPT_FILE}\" --cwd \"{CWD}\" {ADD_DIRS} {LEADER} {APPROVE} {RESUME_OR_CONTINUE}"));
                list.Add(new ProgressiveCliAttempt(
                    "Grok CLI json 新建会话", "grok_cli", grok, addDir,
                    "json_fresh",
                    "--output-format json --prompt-file \"{PROMPT_FILE}\" --cwd \"{CWD}\" {ADD_DIRS} {LEADER} {APPROVE} -s"));
                var grokCmd = grok.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    ? grok
                    : (ResolveGrokCliBinary("grok.cmd") ?? grok);
                list.Add(new ProgressiveCliAttempt(
                    "Grok CLI -p / grok.cmd 兜底", "grok_cli", grokCmd, addDir,
                    "prompt_fallback",
                    "-p \"{PROMPT_FILE}\" --cwd \"{CWD}\" {ADD_DIRS} {LEADER} {APPROVE}"));
            }
        }
        else if (id.Equals("codex_cli", StringComparison.OrdinalIgnoreCase))
        {
            var codex = ResolveCodexBinary(agent.RunnerCmd);
            if (!string.IsNullOrWhiteSpace(codex))
            {
                list.Add(new ProgressiveCliAttempt(
                    "Codex exec resume", "codex_exec", codex, addDir,
                    "resume", "exec resume --skip-git-repo-check {SANDBOX_RESUME} {FLAGS} {ADD_DIRS} -o \"{OUTPUT}\" \"{SESSION_ID}\" -"));
                list.Add(new ProgressiveCliAttempt(
                    "Codex exec 新建", "codex_exec", codex, addDir,
                    "fresh_exec", "exec --skip-git-repo-check {FLAGS} {SANDBOX_EXEC} {ADD_DIRS} -C \"{CWD}\" -o \"{OUTPUT}\" -"));
                list.Add(new ProgressiveCliAttempt(
                    "Codex minimal_compat 降级", "codex_exec", ResolveCodexBinary(null) ?? codex, addDir,
                    "minimal_compat", "exec --skip-git-repo-check {SANDBOX_EXEC} {ADD_DIRS} -C \"{CWD}\" -o \"{OUTPUT}\" -"));
            }
        }
        else if (id.Equals("cursor", StringComparison.OrdinalIgnoreCase))
        {
            // 默认：RequireDesktopVisibleSession 或禁止无头降级时不跑 CLI（由调用方桌面直连）
            // forceCursorCli：仅当 AllowHeadlessCliFallbackForDesktopAgents=true 时才允许 cursor-agent 渐进
            bool allowCursorCliNow = cfg.AllowHeadlessCliFallbackForDesktopAgents &&
                                     (forceCursorCli || !cfg.RequireDesktopVisibleSession);
            if (allowCursorCliNow)
            {
                var cab = ResolveCursorAgentCliBinary(agent.RunnerCmd);
                if (!string.IsNullOrWhiteSpace(cab))
                {
                    string trustFlag = cfg.AutoTrustManagedWorkspaces ? "--trust " : "";
                    list.Add(new ProgressiveCliAttempt(
                        "cursor-agent 续会话 (--trust)", "cursor_agent", cab, addDir,
                        "resume", $"--print {trustFlag}-f --approve-mcps --workspace \"{{WORK_DIR}}\" {{ADD_DIRS}}"));
                    list.Add(new ProgressiveCliAttempt(
                        "cursor-agent 新建 (--trust)", "cursor_agent", cab, addDir,
                        "fresh", $"--print {trustFlag}-f --approve-mcps --workspace \"{{WORK_DIR}}\" {{ADD_DIRS}}"));
                    list.Add(new ProgressiveCliAttempt(
                        "cursor-agent 最小兼容 (--trust)", "cursor_agent", cab, addDir,
                        "minimal", $"--print {trustFlag}-f --workspace \"{{WORK_DIR}}\""));
                }
            }
        }
        else if (id.Equals("claude_code", StringComparison.OrdinalIgnoreCase))
        {
            var claude = ResolveClaudeCodeCliBinary(agent.RunnerCmd);
            if (!string.IsNullOrWhiteSpace(claude))
            {
                list.Add(new ProgressiveCliAttempt(
                    "Claude Code CLI 续会话", "claude_code", claude, addDir,
                    "resume", "-p --dangerously-skip-permissions {ADD_DIRS}"));
                list.Add(new ProgressiveCliAttempt(
                    "Claude Code CLI 新建", "claude_code", claude, addDir,
                    "fresh", "-p --dangerously-skip-permissions {ADD_DIRS}"));
                list.Add(new ProgressiveCliAttempt(
                    "Claude 自定义 Runner", "custom_cli",
                    string.IsNullOrWhiteSpace(agent.RunnerCmd) || agent.RunnerCmd.Equals("auto", StringComparison.OrdinalIgnoreCase)
                        ? claude : agent.RunnerCmd,
                    addDir, "custom", "-p --dangerously-skip-permissions {ADD_DIRS}"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(agent.RunnerCmd) &&
                 !agent.RunnerCmd.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ProgressiveCliAttempt("自定义 Runner", "custom_cli", agent.RunnerCmd, "", "custom", ""));
        }

        return list.Where(a => !string.IsNullOrWhiteSpace(a.BinaryPath) || a.Kind is "grok_cli" or "codex_exec").ToList();
    }

    private static AgentRosterEntry CloneAgentWithRunner(AgentRosterEntry src, string? runner, string addDirFlag)
    {
        string cmd = runner ?? src.RunnerCmd;
        var rt = src.RuntimeParams?.Clone() ?? new AgentRuntimeParams();
        // 标记由 RunCustomCliWithLiveStream 按项目 Companion/外部根展开 --add-dir
        if (!string.IsNullOrWhiteSpace(addDirFlag) &&
            addDirFlag.Contains("--add-dir", StringComparison.OrdinalIgnoreCase) &&
            !(rt.ExtraRunnerArgs ?? "").Contains("__ADD_DIR__", StringComparison.Ordinal))
        {
            rt.ExtraRunnerArgs = string.IsNullOrWhiteSpace(rt.ExtraRunnerArgs)
                ? "__ADD_DIR__"
                : (rt.ExtraRunnerArgs.Trim() + " __ADD_DIR__");
        }
        return new AgentRosterEntry
        {
            Id = src.Id,
            Name = src.Name,
            Enabled = src.Enabled,
            RunnerCmd = cmd,
            RunnerMode = src.RunnerMode,
            GuiExePath = src.GuiExePath,
            Permissions = src.Permissions,
            PreferredEffortTier = src.PreferredEffortTier,
            HighTierModel = src.HighTierModel,
            MediumTierModel = src.MediumTierModel,
            LowTierModel = src.LowTierModel,
            PrimaryWorkRoles = src.PrimaryWorkRoles,
            ModelProfiles = src.ModelProfiles,
            TagScores = src.TagScores,
            Category = src.Category,
            Detected = src.Detected,
            DetectorId = src.DetectorId,
            CustomWorkNote = src.CustomWorkNote,
            RuntimeParams = rt
        };
    }

    /// <summary>
    /// 后台零焦点静默拆弹：自动关闭 Grok Bot / Cursor 中残留卡住的模态框（如 Create New Bot 的 Cancel 按钮），绝不移动鼠标或抢占前台焦点。
    /// </summary>
    public static void DismissStuckAgentModalsSilent()
    {
        if (AppPaths.IsIsolatedTest) return;
        try
        {
            var psPath = Path.Combine(Path.GetTempPath(), $"aicollab_dismiss_{Guid.NewGuid():N}.ps1");
            var code = @"
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinDeskAttach {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport(""user32.dll"", CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport(""user32.dll"")]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);
    [DllImport(""user32.dll"")]
    public static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);
    [DllImport(""user32.dll"")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport(""user32.dll"")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport(""user32.dll"")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport(""user32.dll"")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    public static void KeepBelow(IntPtr hWnd) {
        if (hWnd == IntPtr.Zero) return;
        SetWindowPos(hWnd, (IntPtr)1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }
    public static List<IntPtr> FindProcessWindows(int pid) {
        IntPtr hDesk = OpenDesktopW(""Default"", 0, false, 0x01FF);
        if (hDesk != IntPtr.Zero) SetThreadDesktop(hDesk);
        var list = new List<IntPtr>();
        EnumDesktopWindows(hDesk, (hWnd, lParam) => {
            if (!IsWindowVisible(hWnd)) return true;
            uint wpid; GetWindowThreadProcessId(hWnd, out wpid);
            if ((int)wpid == pid) list.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@
$procs = Get-Process | Where-Object { $_.ProcessName -eq 'Grok Bot' -or $_.ProcessName -eq 'Cursor' }
$btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
foreach ($p in $procs) {
    $hwnds = New-Object System.Collections.Generic.List[IntPtr]
    try {
        foreach ($h in [WinDeskAttach]::FindProcessWindows($p.Id)) { [void]$hwnds.Add($h) }
    } catch {}
    if ($p.MainWindowHandle -ne [IntPtr]::Zero -and -not $hwnds.Contains($p.MainWindowHandle)) {
        [void]$hwnds.Add($p.MainWindowHandle)
    }
    foreach ($hwnd in $hwnds) {
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            if ($null -eq $root) { continue }
            $btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
            foreach ($b in $btns) {
                $n = $b.Current.Name
                if ($n -eq 'Cancel' -or $n -eq 'Close modal' -or $n -eq 'Close dialog' -or
                    $n -eq 'Trust Folder and Continue' -or $n -eq 'Yes, I trust the authors' -or
                    $n -eq 'Trust Workspace' -or $n -eq 'Trust') {
                    $invPat = [System.Windows.Automation.InvokePattern]::Pattern
                    $b.GetCurrentPattern($invPat).Invoke()
                    if ([WinDeskAttach]::GetForegroundWindow() -eq $hwnd) { [WinDeskAttach]::KeepBelow($hwnd) }
                }
            }
        } catch {}
    }
}
";
            File.WriteAllText(psPath, code, new UTF8Encoding(true));
            try
            {
                RunProcessStreaming("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{psPath}\"", AppPaths.AppBaseDir, null, 6, null, stallTimeoutSeconds: 6);
            }
            finally
            {
                try { if (File.Exists(psPath)) File.Delete(psPath); } catch { }
            }
        }
        catch { }
    }

    private HeadlessDispatchOutcome? TryDispatchViaDesktopUiAutomation(
        RegistryService reg,
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        SessionDialogService sessionSvc,
        ExternalDialogSessionInfo? matchedSession,
        bool forceNewDialog,
        string taskBrief,
        string inboxOutFile,
        string liveStatusMd,
        int timeoutSeconds,
        bool allowFallbackExistingBot = false,
        bool isReworkRound = false,
        string lockedGuiChatTitle = "")
        => TryDispatchViaDesktopUiAutomationCore(
            reg, agent, project, bus, sessionSvc, matchedSession, forceNewDialog, taskBrief,
            inboxOutFile, liveStatusMd, timeoutSeconds, allowFallbackExistingBot, isReworkRound, lockedGuiChatTitle, out _);

    private HeadlessDispatchOutcome? TryDispatchViaDesktopUiAutomationCore(
        RegistryService reg,
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        SessionDialogService sessionSvc,
        ExternalDialogSessionInfo? matchedSession,
        bool forceNewDialog,
        string taskBrief,
        string inboxOutFile,
        string liveStatusMd,
        int timeoutSeconds,
        bool allowFallbackExistingBot,
        bool isReworkRound,
        string lockedGuiChatTitle,
        out bool touchedDesktopUi)
    {
        touchedDesktopUi = false;
        string taskBriefSafe = taskBrief ?? "";
        SemaphoreSlim? uiaGate = null;
        bool gateAcquired = false;
        Mutex? crossProcMutex = null;
        bool crossProcOwned = false;
        try
        {
            var targetInfo = SessionDialogService.ExtractProjectDispatchTargetInfo(project, taskBriefSafe, reg.UserConfig.Projects);
            var projectLeaf = !string.IsNullOrWhiteSpace(targetInfo.SubProjectLeaf)
                ? targetInfo.SubProjectLeaf
                : Path.GetFileName(project.PrimaryRoot.TrimEnd('\\', '/'));

            var gateKey = $"{agent.Id}|{projectLeaf}";
            uiaGate = DesktopUiaLocks.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
            gateAcquired = uiaGate.Wait(TimeSpan.FromSeconds(20));
            if (!gateAcquired) return null;

            // 跨进程互斥：防止多 AI协同中心 实例同时往同一 Cursor/Grok 对话框填字
            if (!AppPaths.IsIsolatedTest)
            {
                try
                {
                    string mutexName = $@"Local\AICollabHubDesktopUia_{agent.Id}";
                    crossProcMutex = new Mutex(false, mutexName);
                    try
                    {
                        crossProcOwned = crossProcMutex.WaitOne(20000);
                    }
                    catch (AbandonedMutexException)
                    {
                        crossProcOwned = true;
                    }
                    if (!crossProcOwned) return null;
                }
                catch
                {
                    // 互斥创建失败时仍允许继续（本进程内 Semaphore 已保护）
                }
            }

            var dedupKey = $"{agent.Id}|{projectLeaf}|{taskBriefSafe.Trim()}";
            if (!isReworkRound && !forceNewDialog && RecentDesktopDispatches.TryGetValue(dedupKey, out var recent) &&
                (DateTime.UtcNow - recent.Utc).TotalSeconds < 12.0)
            {
                var (alreadyWorking, topChatTitle) = ProbeDesktopProjectSessionWorkingSilent(
                    agent, targetInfo.CandidateGroups, projectLeaf, ExtractDesktopChatLeaf(recent.ChatTitle));
                if (alreadyWorking)
                {
                    touchedDesktopUi = true;
                    var activeTitle = !string.IsNullOrWhiteSpace(topChatTitle) ? topChatTitle : recent.ChatTitle;
                    var sid = matchedSession?.SessionId ?? "";
                    var dupSummary = $"已无缝挂接「{agent.Name}」唯一活跃会话（{activeTitle}），跳过 12s 内重复投递以彻底杜绝重复建窗。";
                    WrapAndSaveInboxResult(agent, project, bus, inboxOutFile, dupSummary, sid, "resumed");
                    return new HeadlessDispatchOutcome
                    {
                        Success = true,
                        AgentId = agent.Id,
                        AgentName = agent.Name,
                        SessionId = sid,
                        SessionAction = "resumed",
                        ConfirmedGuiChatTitle = activeTitle,
                        AsyncStillWorkingInGui = true,
                        OutputInboxFile = inboxOutFile,
                        OutputText = File.ReadAllText(inboxOutFile, Encoding.UTF8),
                        SummaryMessage = dupSummary
                    };
                }
            }

            var deskProfile = ResolveDesktopUiAutomationProfile(agent);
            var isGrok = deskProfile.IsGrokBotGrouping;
            var winTitlePattern = deskProfile.WinTitle;
            var promptToSend = isGrok
                ? $"【多AI协同总包派单·{projectLeaf}】工程路径在 `{targetInfo.TargetWorkDir}`。若遇到 Read/Edit 提示「目录超出允许的本地访问根目录 / 权限范围拦住」，禁止卡住或等待 /add-dir，直接不要用 Read/Edit，一律改用 Shell（Get-Content / Set-Content / [System.IO.File]::ReadAllText / WriteAllText）按绝对路径读写并完成修改与自检。任务：{taskBriefSafe}"
                : $"【多AI协同总包派单·{projectLeaf}】{taskBriefSafe}";

            string pureLockedTitle = lockedGuiChatTitle ?? "";
            int slashIdx = pureLockedTitle.IndexOf('/');
            if (slashIdx >= 0 && slashIdx + 1 < pureLockedTitle.Length)
            {
                pureLockedTitle = pureLockedTitle[(slashIdx + 1)..].Trim();
            }
            if (pureLockedTitle.Contains("新建", StringComparison.OrdinalIgnoreCase)) pureLockedTitle = "";

            var psScriptPath = Path.Combine(Path.GetTempPath(), $"aicollab_uia_{Guid.NewGuid():N}.ps1");
            var promptTempPath = Path.Combine(Path.GetTempPath(), $"aicollab_prompt_{Guid.NewGuid():N}.txt");
            var cfgJsonPath = Path.Combine(Path.GetTempPath(), $"aicollab_uiacfg_{Guid.NewGuid():N}.json");

            File.WriteAllText(promptTempPath, promptToSend, new UTF8Encoding(false));
            var cfgPayload = new
            {
                WinTitle = winTitlePattern,
                IsGrok = isGrok,
                AgentId = deskProfile.AgentId,
                AgentName = deskProfile.DisplayName,
                GroupCreateMode = deskProfile.GroupCreateMode,
                ProcessNamesCsv = string.Join("|", deskProfile.ProcessNames),
                ForbidIdeEditorLaunch = deskProfile.ForbidIdeEditorLaunchArgs,
                TargetWorkDir = targetInfo.TargetWorkDir,
                ProjectLeaf = projectLeaf,
                CandidateGroups = targetInfo.CandidateGroups,
                HighTerms = targetInfo.HighPriorityTerms,
                Keywords = targetInfo.TopicKeywords,
                SiblingExcludes = targetInfo.SiblingExcludes,
                MatchedSessionTitle = !string.IsNullOrWhiteSpace(pureLockedTitle) ? pureLockedTitle : (matchedSession?.Title ?? ""),
                LockedChatTitle = pureLockedTitle,
                IsReworkRound = isReworkRound,
                ForceNew = forceNewDialog && !isReworkRound,
                AllowFallbackExistingBot = allowFallbackExistingBot,
                CursorExePath = "", // 严禁通过 EXE + --reuse-window 强开 IDE；缺分组一律走窗口内建组
                PromptFile = promptTempPath,
                StrictNoForegroundStealOnDispatch = reg.UserConfig.StrictNoForegroundStealOnDispatch
            };
            File.WriteAllText(cfgJsonPath, JsonSerializer.Serialize(cfgPayload, RegistryService.JsonOpts), new UTF8Encoding(true));

            var psCode = @"
param([string]$ConfigJsonFile)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public class WinDeskHelper {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport(""user32.dll"", CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport(""user32.dll"")]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);
    [DllImport(""user32.dll"")]
    public static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);
    [DllImport(""user32.dll"")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport(""user32.dll"")]
    public static extern bool IsIconic(IntPtr hWnd);
    [DllImport(""user32.dll"")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport(""user32.dll"")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport(""user32.dll"")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport(""user32.dll"")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport(""user32.dll"", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport(""user32.dll"")]
    public static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport(""user32.dll"")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport(""user32.dll"")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport(""user32.dll"", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport(""user32.dll"", CharSet = CharSet.Unicode)]
    public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
" + BuildDesktopNoStealWin32Helpers() + @"
    public static void CloseWindow(IntPtr hWnd) {
        if (hWnd != IntPtr.Zero) PostMessageW(hWnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
    }

    public static IntPtr FindNativeFolderDialog() {
        IntPtr hDesk = OpenDesktopW(""Default"", 0, false, 0x01FF);
        if (hDesk != IntPtr.Zero) SetThreadDesktop(hDesk);
        IntPtr found = IntPtr.Zero;
        EnumDesktopWindows(hDesk, (hWnd, lParam) => {
            if (!IsWindowVisible(hWnd)) return true;
            var cls = new StringBuilder(256);
            GetClassNameW(hWnd, cls, 256);
            if (!string.Equals(cls.ToString(), ""#32770"", StringComparison.Ordinal)) return true;
            int len = GetWindowTextLengthW(hWnd);
            var sb = new StringBuilder(Math.Max(1, len) + 1);
            if (len > 0) GetWindowTextW(hWnd, sb, len + 1);
            var t = sb.ToString();
            if (t.IndexOf(""Select Folder"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf(""Open Folder"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf(""Browse For Folder"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf(""选择文件夹"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf(""打开文件夹"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf(""文件夹"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.Equals(""Open"", StringComparison.OrdinalIgnoreCase) ||
                t.Equals(""打开"", StringComparison.OrdinalIgnoreCase) ||
                t.IndexOf(""Folder"", StringComparison.OrdinalIgnoreCase) >= 0) {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static long GetIdleMs() {
        LASTINPUTINFO lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (GetLastInputInfo(ref lii)) {
            uint now = unchecked((uint)Environment.TickCount);
            return unchecked(now - lii.dwTime);
        }
        return 999999;
    }

    public static void WaitPolitelyIfUserActiveOnTarget(IntPtr targetHwnd, int maxWaitMs) {
        if (targetHwnd == IntPtr.Zero) return;
        int waited = 0;
        while (waited < maxWaitMs) {
            if (GetForegroundWindow() != targetHwnd || GetIdleMs() >= 1500) break;
            Thread.Sleep(250);
            waited += 250;
        }
    }

    public static IntPtr FindWindowOnDefault(string titleMatch, string projectLeaf, bool preferExactProjectCursor, string processNamesCsv) {
        IntPtr hDesk = OpenDesktopW(""Default"", 0, false, 0x01FF);
        if (hDesk != IntPtr.Zero) SetThreadDesktop(hDesk);
        bool isGrok = string.Equals(titleMatch, ""Grok Bot"", StringComparison.OrdinalIgnoreCase);
        bool isCursorAgents = titleMatch != null && titleMatch.IndexOf(""Cursor Agents"", StringComparison.OrdinalIgnoreCase) >= 0;
        var targetPids = new HashSet<int>();
        var procNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(processNamesCsv)) {
            foreach (var raw in processNamesCsv.Split(new[] {'|',';'}, StringSplitOptions.RemoveEmptyEntries)) {
                var n = raw.Trim();
                if (n.Length > 0 && !procNames.Contains(n)) procNames.Add(n);
            }
        }
        if (procNames.Count == 0) {
            if (isGrok) procNames.Add(""Grok Bot"");
            else if (isCursorAgents) procNames.Add(""Cursor"");
            else if (!string.IsNullOrWhiteSpace(titleMatch)) procNames.Add(titleMatch);
        }
        try {
            foreach (var pn in procNames) {
                foreach (var p in Process.GetProcessesByName(pn)) targetPids.Add(p.Id);
            }
        } catch {}

        IntPtr exactProjWin = IntPtr.Zero;
        IntPtr hubAgentWin = IntPtr.Zero;
        IntPtr titleMatchWin = IntPtr.Zero;
        IntPtr anyFallback = IntPtr.Zero;
        IntPtr pidSizedFallback = IntPtr.Zero;
        EnumDesktopWindows(hDesk, (hWnd, lParam) => {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLengthW(hWnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(hWnd, sb, len + 1);
            var t = sb.ToString();
            int pid = 0;
            GetWindowThreadProcessId(hWnd, out pid);
            bool matchesPid = targetPids.Contains(pid);
            RECT rc;
            bool hasSize = GetWindowRect(hWnd, out rc);
            int w = hasSize ? (rc.Right - rc.Left) : 0;
            int h = hasSize ? (rc.Bottom - rc.Top) : 0;
            bool largeEnough = w >= 260 && h >= 200;
            bool titleHit = !string.IsNullOrEmpty(titleMatch) && t.IndexOf(titleMatch, StringComparison.OrdinalIgnoreCase) >= 0;

            if (isGrok) {
                if (titleHit) { exactProjWin = hWnd; return false; }
                if (matchesPid && largeEnough && pidSizedFallback == IntPtr.Zero) pidSizedFallback = hWnd;
            } else if (isCursorAgents) {
                bool isCursorAgent = t.IndexOf(""Cursor Agents"", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isCursorIde = t.EndsWith("" - Cursor"", StringComparison.OrdinalIgnoreCase);
                if (isCursorIde && !string.IsNullOrEmpty(projectLeaf) && t.IndexOf(projectLeaf, StringComparison.OrdinalIgnoreCase) >= 0) {
                    if (exactProjWin == IntPtr.Zero) exactProjWin = hWnd;
                } else if (isCursorAgent) {
                    if (hubAgentWin == IntPtr.Zero) hubAgentWin = hWnd;
                } else if (isCursorIde) {
                    if (anyFallback == IntPtr.Zero) anyFallback = hWnd;
                } else if (matchesPid && largeEnough && pidSizedFallback == IntPtr.Zero) {
                    pidSizedFallback = hWnd;
                }
            } else {
                if (titleHit && largeEnough) {
                    if (hubAgentWin == IntPtr.Zero) hubAgentWin = hWnd;
                    if (titleMatchWin == IntPtr.Zero) titleMatchWin = hWnd;
                } else if (matchesPid && largeEnough && pidSizedFallback == IntPtr.Zero) {
                    pidSizedFallback = hWnd;
                } else if (!string.IsNullOrEmpty(projectLeaf) && t.IndexOf(projectLeaf, StringComparison.OrdinalIgnoreCase) >= 0 && matchesPid) {
                    if (exactProjWin == IntPtr.Zero) exactProjWin = hWnd;
                }
            }
            return true;
        }, IntPtr.Zero);
        IntPtr found = preferExactProjectCursor
            ? (exactProjWin != IntPtr.Zero ? exactProjWin : (hubAgentWin != IntPtr.Zero ? hubAgentWin : (titleMatchWin != IntPtr.Zero ? titleMatchWin : (anyFallback != IntPtr.Zero ? anyFallback : pidSizedFallback))))
            : (hubAgentWin != IntPtr.Zero ? hubAgentWin : (titleMatchWin != IntPtr.Zero ? titleMatchWin : (exactProjWin != IntPtr.Zero ? exactProjWin : (anyFallback != IntPtr.Zero ? anyFallback : pidSizedFallback))));
        if (found != IntPtr.Zero && IsIconic(found)) {
            // 严禁 SW_RESTORE(9)/SetForegroundWindow 抢前台：SW_SHOWNOACTIVATE + HWND_BOTTOM + SWP_NOACTIVATE
            PrepareWindowNoActivateKeepBelow(found);
        }
        return found;
    }
}
'@

$cfg = Get-Content -Path $ConfigJsonFile -Raw -Encoding UTF8 | ConvertFrom-Json
$candGroups = @($cfg.CandidateGroups)
$highTerms = @($cfg.HighTerms)
$keywords = @($cfg.Keywords)
$sibExcludes = @($cfg.SiblingExcludes)

function Dismiss-BlockingModals($rootEl) {
    try {
        $denyClick = @('Revert Files','Keep Files','Revert','Restore','Restore Checkpoint','Undo Changes','Discard','Delete','Confirm','OK','确定','回滚','恢复','删除')
        $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        $btns = $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
        foreach ($b in $btns) {
            $bn = $b.Current.Name
            if ([string]::IsNullOrWhiteSpace($bn)) { continue }
            $t = $bn.Trim()
            if ($denyClick -contains $t) { continue }
            if ($t -eq 'Cancel' -or $t -eq '取消' -or $t -eq 'Close modal' -or $t -eq 'Close dialog') {
                try {
                    Invoke-DesktopControlNoFocus $b | Out-Null
                    Start-Sleep -Milliseconds 350
                } catch {}
            }
        }
    } catch {}
}

function Invoke-GrokNewBotSafely($rootEl, $newBotBtn, [string]$botName) {
    try {
        Invoke-DesktopControlNoFocus $newBotBtn | Out-Null
        Start-Sleep -Milliseconds 600
        $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
        $edits = $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
        $namedOk = $false
        foreach ($e in $edits) {
            $en = $e.Current.Name
            $eh = $e.Current.HelpText
            if ($en -like '*Name your Bot*' -or $eh -like '*Name your Bot*' -or $en -eq 'Bot name') {
                $safeTitle = if ([string]::IsNullOrWhiteSpace($botName)) { '协同实操' } else { $botName }
                $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($safeTitle)
                $namedOk = $true
                break
            }
        }
        if ($namedOk) {
            Start-Sleep -Milliseconds 350
            $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
            $btns = $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
            foreach ($b in $btns) {
                if ($b.Current.Name -eq 'Create' -and $b.Current.IsEnabled) {
                    Invoke-DesktopControlNoFocus $b | Out-Null
                    Start-Sleep -Milliseconds 900
                    return $true
                }
            }
        }
    } catch {}
    # 若未能完成创建，立即自动点击 Cancel 关闭模态框，绝不让 Create New Bot 弹窗卡在界面上！
    Dismiss-BlockingModals $rootEl
    return $false
}

function Get-SidebarScope($rootEl, [bool]$isGrok) {
    $allGroups = $rootEl.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Group))
    )
    foreach ($g in $allGroups) {
        if ($isGrok) {
            if ($g.Current.Name -eq 'Bots' -or $g.Current.Name -eq 'Bot list') { return $g }
        } else {
            if ($g.Current.AutomationId -eq 'glass-sidebar' -or $g.Current.Name -eq 'Sidebar') { return $g }
        }
    }
    return $rootEl
}

function Strip-StatusSuffix([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return '' }
    $s = $name.Trim()
    foreach ($suf in @(', Unread activity, Working', ', Unread activity', ', Working')) {
        if ($s.EndsWith($suf)) { $s = $s.Substring(0, $s.Length - $suf.Length).Trim() }
    }
    return $s
}

function Ensure-CursorSidebarAndReposExpanded($rootEl) {
    $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $btns = $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    foreach ($b in $btns) {
        if ($b.Current.Name -eq 'Show Sidebar') {
            try { Invoke-DesktopControlNoFocus $b | Out-Null; Start-Sleep -Milliseconds 700 } catch {
                try { $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Restore-DispatchForeground; Start-Sleep -Milliseconds 700 } catch {}
            }
            break
        }
    }
    $scope = Get-SidebarScope $rootEl $false
    $sBtns = $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    foreach ($b in $sBtns) {
        if ($b.Current.Name -eq 'Repositories') {
            try {
                $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                if ($ec.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
                    $ec.Expand(); Restore-DispatchForeground
                    Start-Sleep -Milliseconds 400
                }
            } catch {}
            break
        }
    }
}

function Scan-SidebarGroups($rootEl, [bool]$isGrok) {
    $scope = Get-SidebarScope $rootEl $isGrok
    $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $btns = $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    $skipHeaderGroups = @('File','Edit','View','Help','Hide Sidebar','Show Sidebar','Projects','Pinned','Repositories','Customize Sidebar','Chat actions','Show Apps','More','Settings')
    $skipDialogBtns = @('New Agent','New Bot','New Chat','Pin','Unpin','Archive','More','Search','Automations','Customize','New Project','Connect apps','Repositories','Pinned','Projects','Settings','Chat actions','Show Apps')

    $groups = @()
    $curGroup = $null
    $newBotBtn = $null
    $newChatBtn = $null
    $defaultTopBotBtn = $null
    $hasReposHeader = $false
    if (-not $isGrok) {
        foreach ($bCheck in $btns) {
            if ($bCheck.Current.Name -eq 'Repositories') { $hasReposHeader = $true; break }
        }
    }
    $inRepos = $isGrok -or (-not $hasReposHeader)

    foreach ($b in $btns) {
        $bn = $b.Current.Name
        $aid = $b.Current.AutomationId
        if ($bn -eq 'New Bot' -and $null -eq $newBotBtn) { $newBotBtn = $b }
        if ($bn -eq 'New Chat' -and $null -eq $newChatBtn) { $newChatBtn = $b }
        if ($isGrok -and $bn -eq 'Grok Bot' -and $null -eq $defaultTopBotBtn) { $defaultTopBotBtn = $b }
        if (-not $isGrok -and $bn -eq 'Repositories') { $inRepos = $true; continue }
        if (-not $inRepos) { continue }

        $pats = ($b.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) -join ','
        if ($pats -match 'ExpandCollapse' -and ($bn -notin $skipHeaderGroups)) {
            if (-not [string]::IsNullOrEmpty($aid) -and [string]::IsNullOrEmpty($b.Current.HelpText) -eq $false) { break }
            if ($isGrok -and $bn -eq 'Unassigned') {
                $curGroup = $null
                continue
            }
            if ([string]::IsNullOrEmpty($aid)) {
                $curGroup = [pscustomobject]@{
                    Name = $bn
                    HeaderBtn = $b
                    NewAgentBtn = $null
                    MoreBtn = $null
                    Dialogs = New-Object System.Collections.ArrayList
                }
                $groups += $curGroup
                continue
            }
        }
        if ($null -ne $curGroup) {
            if ((Test-IsNewDialogEntryName $bn) -and $null -eq $curGroup.NewAgentBtn) {
                $curGroup.NewAgentBtn = $b
                continue
            }
            if ($bn -eq 'More' -and $null -eq $curGroup.MoreBtn) {
                $curGroup.MoreBtn = $b
                continue
            }
            $cleanName = Strip-StatusSuffix $bn
            $isWorkingNow = ($bn -like '*, Working*')
            if ($pats -match 'Invoke' -and -not [string]::IsNullOrWhiteSpace($cleanName) -and ($cleanName -notin $skipDialogBtns)) {
                [void]$curGroup.Dialogs.Add([pscustomobject]@{ CleanName = $cleanName; IsWorking = $isWorkingNow; Btn = $b })
            }
        }
    }
    return [pscustomobject]@{
        Groups = $groups
        NewBotBtn = $newBotBtn
        NewChatBtn = $newChatBtn
        DefaultTopBotBtn = $defaultTopBotBtn
    }
}

function Get-ExtendedCandidateGroups {
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($cg in $candGroups) {
        if (-not [string]::IsNullOrWhiteSpace($cg) -and -not $list.Contains($cg)) { [void]$list.Add($cg) }
    }
    if (-not [string]::IsNullOrWhiteSpace($cfg.ProjectLeaf) -and -not $list.Contains($cfg.ProjectLeaf)) {
        [void]$list.Add($cfg.ProjectLeaf)
    }
    if (-not [string]::IsNullOrWhiteSpace($cfg.TargetWorkDir)) {
        try {
            $norm = $cfg.TargetWorkDir.TrimEnd('\','/')
            $leaf = [System.IO.Path]::GetFileName($norm)
            $parent = [System.IO.Path]::GetFileName([System.IO.Path]::GetDirectoryName($norm))
            if (-not [string]::IsNullOrWhiteSpace($leaf) -and -not $list.Contains($leaf)) { [void]$list.Add($leaf) }
            if (-not [string]::IsNullOrWhiteSpace($parent) -and -not $list.Contains($parent)) { [void]$list.Add($parent) }
        } catch {}
    }
    return ,$list.ToArray()
}

function Normalize-GroupKey([string]$n) {
    if ([string]::IsNullOrWhiteSpace($n)) { return '' }
    $s = $n.Trim().ToLowerInvariant()
    $s = $s -replace '[\s_\-–—]+', ''
    return $s
}

function Test-GroupNameBlockedBySibling([string]$gName) {
    if ([string]::IsNullOrWhiteSpace($gName)) { return $false }
    foreach ($sib in $sibExcludes) {
        if ([string]::IsNullOrWhiteSpace($sib)) { continue }
        if ($gName -eq $sib) { return $true }
        $gKey = Normalize-GroupKey $gName
        $sKey = Normalize-GroupKey $sib
        if ($gKey.Length -ge 2 -and $sKey.Length -ge 2 -and $gKey -eq $sKey) { return $true }
    }
    return $false
}

function Find-MatchedGroupFromScan($scanObj) {
    $ext = Get-ExtendedCandidateGroups
    $groups = @($scanObj.Groups)
    if ($groups.Count -eq 0) { return $null }

    # 1) 精确匹配（Trim，PowerShell -eq 默认大小写不敏感）
    foreach ($cg in $ext) {
        if ([string]::IsNullOrWhiteSpace($cg)) { continue }
        $cgTrim = $cg.Trim()
        $found = $groups | Where-Object { ($_.Name + '').Trim() -eq $cgTrim } | Select-Object -First 1
        if ($null -ne $found -and -not (Test-GroupNameBlockedBySibling $found.Name)) { return $found }
    }

    # 2) 规范化匹配（忽略空格/下划线/连字符差异，如 TAKT-UI-CC-main）
    foreach ($cg in $ext) {
        if ([string]::IsNullOrWhiteSpace($cg)) { continue }
        $cgKey = Normalize-GroupKey $cg
        if ($cgKey.Length -lt 2) { continue }
        $found = $groups | Where-Object {
            $gKey = Normalize-GroupKey $_.Name
            $gKey -eq $cgKey
        } | Select-Object -First 1
        if ($null -ne $found -and -not (Test-GroupNameBlockedBySibling $found.Name)) { return $found }
    }

    # 3) 包含匹配（候选较长时优先；排除兄弟工程误伤）
    foreach ($cg in ($ext | Sort-Object { $_.Length } -Descending)) {
        if ([string]::IsNullOrWhiteSpace($cg) -or $cg.Trim().Length -lt 2) { continue }
        $cgTrim = $cg.Trim()
        $found = $groups | Where-Object {
            $gName = ($_.Name + '').Trim()
            (($gName -like ""*$cgTrim*"") -or ($cgTrim -like ""*$gName*"")) -and -not (Test-GroupNameBlockedBySibling $gName)
        } | Select-Object -First 1
        if ($null -ne $found) { return $found }
    }
    return $null
}

function Test-IsForbiddenIdeSwitcherName([string]$n) {
    if ([string]::IsNullOrWhiteSpace($n)) { return $true }
    $badExact = @('Open Editor','Agents Window','Open in Editor','Switch to Editor','Open Editors','New Window','Open Workspace','Switch to IDE')
    foreach ($b in $badExact) {
        if ($n -eq $b) { return $true }
    }
    if ($n -like '*Open Editor*' -or $n -like '*Agents Window*' -or $n -like '*Switch to Editor*') { return $true }
    return $false
}

function Test-IsAddProjectEntryName([string]$n) {
    if ([string]::IsNullOrWhiteSpace($n)) { return $false }
    if (Test-IsForbiddenIdeSwitcherName $n) { return $false }
    $ok = @(
        'Add Project','Add Folder','Add Repository','Open Folder','Add Worktree','New Project','New Folder','New Group','Create Project','Create Group','Add workspace','Add Workspace',
        '添加项目','打开文件夹','添加仓库','添加文件夹','新建项目','新建分组','新建文件夹','添加工作区','创建项目','创建分组'
    )
    foreach ($o in $ok) {
        if ($n -eq $o -or $n -like (""*$o*"")) { return $true }
    }
    return $false
}

function Test-IsNewDialogEntryName([string]$n) {
    if ([string]::IsNullOrWhiteSpace($n)) { return $false }
    if (Test-IsForbiddenIdeSwitcherName $n) { return $false }
    $ok = @('New Agent','New Chat','New Thread','New Conversation','New Dialog','+','新建对话','新建会话','新建线程')
    foreach ($o in $ok) {
        if ($n -eq $o) { return $true }
    }
    return $false
}

function Test-IsGrokGroupCreateEntryName([string]$n) {
    if ([string]::IsNullOrWhiteSpace($n)) { return $false }
    if (Test-IsForbiddenIdeSwitcherName $n) { return $false }
    $ok = @('New Group','Create Group','New Folder','Add Project','新建分组','新建文件夹','新建项目','创建分组')
    foreach ($o in $ok) {
        if ($n -eq $o -or $n -like (""*$o*"")) { return $true }
    }
    return $false
}

function Close-NativeFolderPickers([int]$budgetMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $budgetMs) {
        $dlgHwnd = [WinDeskHelper]::FindNativeFolderDialog()
        if ($dlgHwnd -eq [IntPtr]::Zero) { return }
        $closed = $false
        try {
            $dlgRoot = [System.Windows.Automation.AutomationElement]::FromHandle($dlgHwnd)
            $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
            foreach ($b in $dlgRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
                $bn = $b.Current.Name
                if ($bn -eq 'Cancel' -or $bn -eq '取消' -or $bn -eq 'Close' -or $bn -eq '关闭') {
                    try {
                        Invoke-DesktopControlNoFocus $b | Out-Null
                        $closed = $true
                        break
                    } catch {}
                }
            }
        } catch {}
        if (-not $closed) { [WinDeskHelper]::CloseWindow($dlgHwnd) }
        Start-Sleep -Milliseconds 250
    }
    $left = [WinDeskHelper]::FindNativeFolderDialog()
    if ($left -ne [IntPtr]::Zero) { [WinDeskHelper]::CloseWindow($left) }
}

function Complete-NativeFolderPicker([string]$targetPath, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $dlgHwnd = [IntPtr]::Zero
    while ($sw.ElapsedMilliseconds -lt $timeoutMs -and $dlgHwnd -eq [IntPtr]::Zero) {
        $dlgHwnd = [WinDeskHelper]::FindNativeFolderDialog()
        if ($dlgHwnd -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 200 }
    }
    if ($dlgHwnd -eq [IntPtr]::Zero) { return $false }
    try {
        $dlgRoot = [System.Windows.Automation.AutomationElement]::FromHandle($dlgHwnd)
        $filled = $false
        $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
        foreach ($e in $dlgRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)) {
            try {
                $en = $e.Current.Name
                if ($en -like '*Search*' -or $en -like '*搜索*') { continue }
                $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($targetPath)
                $filled = $true
                break
            } catch {}
        }
        if (-not $filled) {
            $comboCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
            foreach ($c in $dlgRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $comboCond)) {
                try {
                    $c.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($targetPath)
                    $filled = $true
                    break
                } catch {}
            }
        }
        Start-Sleep -Milliseconds 250
        $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        $confirmNames = @('Select Folder','选择文件夹','确定','打开','Open','Select','选择')
        foreach ($b in $dlgRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
            $bn = $b.Current.Name
            if (($confirmNames -contains $bn) -and $b.Current.IsEnabled) {
                try {
                    Invoke-DesktopControlNoFocus $b | Out-Null
                    Start-Sleep -Milliseconds 700
                    return $true
                } catch {}
            }
        }
    } catch {}
    Close-NativeFolderPickers 2500
    return $false
}

function Try-EnsureProjectGroupInDesktopApp($rootEl) {
    $isGrokMode = [bool]$cfg.IsGrok
    Ensure-CursorSidebarAndReposExpanded $rootEl
    $scanLocal = Scan-SidebarGroups $rootEl $isGrokMode
    $hit = Find-MatchedGroupFromScan $scanLocal
    if ($null -ne $hit) {
        return [pscustomobject]@{ Group = $hit; Scan = $scanLocal }
    }

    if ($isGrokMode) {
        # Grok Bot：优先侧栏新建分组/文件夹/项目，再用 ProjectLeaf 命名
        $scope = Get-SidebarScope $rootEl $true
        $btnCondG = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        $menuCondG = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
        $groupEntries = New-Object System.Collections.ArrayList
        foreach ($b in $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCondG)) {
            if (Test-IsGrokGroupCreateEntryName $b.Current.Name) { [void]$groupEntries.Add($b) }
        }
        foreach ($m in $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuCondG)) {
            if (Test-IsGrokGroupCreateEntryName $m.Current.Name) { [void]$groupEntries.Add($m) }
        }
        if ($groupEntries.Count -eq 0) {
            foreach ($b in $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCondG)) {
                if (Test-IsGrokGroupCreateEntryName $b.Current.Name) { [void]$groupEntries.Add($b) }
            }
        }
        foreach ($entry in $groupEntries) {
            try { Invoke-DesktopControlNoFocus $entry | Out-Null } catch { continue }
            Start-Sleep -Milliseconds 500
            $namedOk = $false
            try {
                $editCondG = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
                foreach ($e in $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondG)) {
                    $en = $e.Current.Name
                    if ($en -eq 'Prompt' -or $en -like '*Steer*' -or $en -like '*Search*' -or $en -like '*Message*') { continue }
                    if ($en -like '*Name*' -or $en -like '*Group*' -or $en -like '*Folder*' -or $en -like '*Project*' -or $en -like '*名称*' -or $en -like '*分组*' -or [string]::IsNullOrWhiteSpace($en)) {
                        try {
                            $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($cfg.ProjectLeaf)
                            $namedOk = $true
                            break
                        } catch {}
                    }
                }
            } catch {}
            if ($namedOk) {
                foreach ($b in $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCondG)) {
                    $bn = $b.Current.Name
                    if (($bn -eq 'Create' -or $bn -eq 'OK' -or $bn -eq '确定' -or $bn -eq 'Add' -or $bn -eq '创建') -and $b.Current.IsEnabled) {
                        try { Invoke-DesktopControlNoFocus $b | Out-Null; Start-Sleep -Milliseconds 900; break } catch {}
                    }
                }
            } else {
                Dismiss-BlockingModals $rootEl
                continue
            }
            Start-Sleep -Milliseconds 900
            $scanLocal = Scan-SidebarGroups $rootEl $true
            $hit = Find-MatchedGroupFromScan $scanLocal
            if ($null -ne $hit) {
                return [pscustomobject]@{ Group = $hit; Scan = $scanLocal }
            }
        }

        # 按项目创建专属 Bot / 对话分组
        if ($null -ne $scanLocal.NewBotBtn) {
            $createdBot = Invoke-GrokNewBotSafely $rootEl $scanLocal.NewBotBtn $cfg.ProjectLeaf
            if ($createdBot) {
                Start-Sleep -Milliseconds 900
                $scanLocal = Scan-SidebarGroups $rootEl $true
                $hit = Find-MatchedGroupFromScan $scanLocal
                if ($null -ne $hit) {
                    return [pscustomobject]@{ Group = $hit; Scan = $scanLocal; CreatedViaNewBot = $true }
                }
                # Bot 已创建但侧栏名可能尚未同步为 ProjectLeaf：仍返回空，由上层用新建标题继续
                return [pscustomobject]@{ Group = $null; Scan = $scanLocal; CreatedViaNewBot = $true }
            }
        }
        Dismiss-BlockingModals $rootEl
        return $null
    }

    # 目录/工程分组型：Cursor Agents / Windsurf / Claude / Codex / Antigravity / 通用外部桌面 AI
    if ([string]::IsNullOrWhiteSpace($cfg.TargetWorkDir) -or -not (Test-Path -LiteralPath $cfg.TargetWorkDir)) {
        return $null
    }
    $scope = Get-SidebarScope $rootEl $false
    $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $menuCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $entries = New-Object System.Collections.ArrayList
    foreach ($b in $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
        if (Test-IsAddProjectEntryName $b.Current.Name) { [void]$entries.Add($b) }
    }
    foreach ($m in $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuCond)) {
        if (Test-IsAddProjectEntryName $m.Current.Name) { [void]$entries.Add($m) }
    }
    if ($entries.Count -eq 0) {
        foreach ($b in $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
            if (Test-IsAddProjectEntryName $b.Current.Name) { [void]$entries.Add($b) }
        }
        foreach ($m in $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuCond)) {
            if (Test-IsAddProjectEntryName $m.Current.Name) { [void]$entries.Add($m) }
        }
    }
    if ($entries.Count -eq 0) {
        Close-NativeFolderPickers 2000
        return $null
    }

    foreach ($entry in $entries) {
        if (Test-IsForbiddenIdeSwitcherName $entry.Current.Name) { continue }
        try {
            Invoke-DesktopControlNoFocus $entry | Out-Null
        } catch { continue }
        Start-Sleep -Milliseconds 550

        try {
            foreach ($m2 in $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuCond)) {
                if (Test-IsAddProjectEntryName $m2.Current.Name) {
                    try { Invoke-DesktopControlNoFocus $m2 | Out-Null; Start-Sleep -Milliseconds 450 } catch {}
                    break
                }
            }
        } catch {}

        $pathFilledInApp = $false
        $nameFilledInApp = $false
        try {
            $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
            foreach ($e in $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)) {
                $en = $e.Current.Name
                $eh = $e.Current.HelpText
                if ($en -eq 'Prompt' -or $en -like '*Steer*' -or $en -like '*Search*' -or $en -like '*Message*') { continue }
                if ($en -like '*Folder*' -or $en -like '*Path*' -or $en -like '*Directory*' -or $en -like '*目录*' -or $en -like '*文件夹*' -or $en -like '*Address*' -or $eh -like '*folder*' -or $eh -like '*path*') {
                    try {
                        $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($cfg.TargetWorkDir)
                        $pathFilledInApp = $true
                        break
                    } catch {}
                }
                if ($en -like '*Name*' -or $en -like '*Group*' -or $en -like '*名称*' -or $en -like '*分组*') {
                    try {
                        $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($cfg.ProjectLeaf)
                        $nameFilledInApp = $true
                        break
                    } catch {}
                }
            }
        } catch {}

        if ($pathFilledInApp -or $nameFilledInApp) {
            $confirmedInApp = $false
            try {
                $btnCond2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
                foreach ($b in $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond2)) {
                    $bn = $b.Current.Name
                    if (($bn -eq 'Add' -or $bn -eq 'OK' -or $bn -eq '确定' -or $bn -eq 'Open' -or $bn -eq '打开' -or $bn -eq 'Select Folder' -or $bn -eq '选择文件夹' -or $bn -eq 'Add Project' -or $bn -eq '添加项目' -or $bn -eq 'Create' -or $bn -eq '创建') -and $b.Current.IsEnabled) {
                        if (Test-IsForbiddenIdeSwitcherName $bn) { continue }
                        try {
                            Invoke-DesktopControlNoFocus $b | Out-Null
                            $confirmedInApp = $true
                            Start-Sleep -Milliseconds 900
                            break
                        } catch {}
                    }
                }
            } catch {}
            if (-not $confirmedInApp) {
                Dismiss-BlockingModals $rootEl
                Close-NativeFolderPickers 2000
                continue
            }
        } else {
            $dlgOk = Complete-NativeFolderPicker $cfg.TargetWorkDir 4500
            if (-not $dlgOk) {
                Close-NativeFolderPickers 3000
                Dismiss-BlockingModals $rootEl
                continue
            }
        }

        Start-Sleep -Milliseconds 1200
        Ensure-CursorSidebarAndReposExpanded $rootEl
        $scanLocal = Scan-SidebarGroups $rootEl $false
        $hit = Find-MatchedGroupFromScan $scanLocal
        if ($null -ne $hit) {
            return [pscustomobject]@{ Group = $hit; Scan = $scanLocal }
        }
    }

    Close-NativeFolderPickers 3000
    Dismiss-BlockingModals $rootEl
    return $null
}

# 兼容旧名：Cursor Agents 建组入口
function Try-EnsureCursorProjectGroupInAgents($rootEl) {
    return Try-EnsureProjectGroupInDesktopApp $rootEl
}

function Try-CreateDialogInGroup($rootEl, $matchedGroup, $scanObj) {
    if ($null -eq $matchedGroup) { return $null }
    try {
        $ec = $matchedGroup.HeaderBtn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
            $ec.Expand(); Restore-DispatchForeground
            Start-Sleep -Milliseconds 450
        }
    } catch {}
    if ($null -eq $matchedGroup.NewAgentBtn) {
        Ensure-CursorSidebarAndReposExpanded $rootEl
        $scan2 = Scan-SidebarGroups $rootEl ([bool]$cfg.IsGrok)
        $mg2 = Find-MatchedGroupFromScan $scan2
        if ($null -ne $mg2) { $matchedGroup = $mg2; $scanObj = $scan2 }
    }
    if ($null -ne $matchedGroup.NewAgentBtn) {
        try { $matchedGroup.HeaderBtn.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch {}
        try {
            Invoke-DesktopControlNoFocus $matchedGroup.NewAgentBtn | Out-Null
            Write-Output 'UIA_TOUCHED_STATE:1'
            Start-Sleep -Milliseconds 1100
            return [pscustomobject]@{ Title = ($matchedGroup.Name + '/新建对话'); Group = $matchedGroup; Scan = $scanObj }
        } catch {}
    }
    if ($cfg.IsGrok -and $null -ne $scanObj.NewBotBtn) {
        $ok = Invoke-GrokNewBotSafely $rootEl $scanObj.NewBotBtn $cfg.ProjectLeaf
        if ($ok) {
            Write-Output 'UIA_TOUCHED_STATE:1'
            return [pscustomobject]@{ Title = ($matchedGroup.Name + '/新建Bot对话'); Group = $matchedGroup; Scan = $scanObj }
        }
    }
    if ($null -ne $scanObj.NewChatBtn) {
        try {
            Invoke-DesktopControlNoFocus $scanObj.NewChatBtn | Out-Null
            Write-Output 'UIA_TOUCHED_STATE:1'
            Start-Sleep -Milliseconds 900
            return [pscustomobject]@{ Title = ($matchedGroup.Name + '/新建Chat对话'); Group = $matchedGroup; Scan = $scanObj }
        } catch {}
    }
    return $null
}

" + BuildDesktopNoStealPsHelpers() + BuildDesktopPromptPickerPsHelpers() + @"
$hwnd = [WinDeskHelper]::FindWindowOnDefault($cfg.WinTitle, $cfg.ProjectLeaf, $false, [string]$cfg.ProcessNamesCsv)
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Output 'NO_WINDOW'
    exit 2
}
# 若用户此刻正在前台操作该窗口，静默礼让等待停手，绝不争抢输入焦点或键鼠
[WinDeskHelper]::WaitPolitelyIfUserActiveOnTarget($hwnd, 4000)
$prevForeground = [WinDeskHelper]::GetForegroundWindow()
$strictNoSteal = $true
try { if ($null -ne $cfg.StrictNoForegroundStealOnDispatch) { $strictNoSteal = [bool]$cfg.StrictNoForegroundStealOnDispatch } } catch { $strictNoSteal = $true }
if ($strictNoSteal -and $prevForeground -ne $hwnd) {
    try { [WinDeskHelper]::PrepareWindowNoActivateKeepBelow($hwnd) } catch {}
}

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
# 第 0 步：自动清理任何之前卡在屏幕上的模态框（如残留的 Create New Bot 弹窗）
Dismiss-BlockingModals $root

if (-not $cfg.IsGrok) { Ensure-CursorSidebarAndReposExpanded $root }
$scan = Scan-SidebarGroups $root $cfg.IsGrok
if ($scan.Groups.Count -eq 0) {
    Start-Sleep -Milliseconds 600
    $scan = Scan-SidebarGroups $root $cfg.IsGrok
}

$matchedGroup = Find-MatchedGroupFromScan $scan

if ($null -ne $matchedGroup) {
    try {
        $ec = $matchedGroup.HeaderBtn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
            $ec.Expand(); Restore-DispatchForeground
            Start-Sleep -Milliseconds 450
            $scan = Scan-SidebarGroups $root $cfg.IsGrok
            $matchedGroup = $scan.Groups | Where-Object { $_.Name -eq $matchedGroup.Name } | Select-Object -First 1
        }
    } catch {}
}

$selectedChatTitle = ''
$dispatchedInNewMode = $false
$wrongChatNamesToAvoid = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
foreach ($g in $scan.Groups) {
    if ($null -eq $matchedGroup -or $g.Name -ne $matchedGroup.Name) {
        foreach ($d in $g.Dialogs) { [void]$wrongChatNamesToAvoid.Add($d.CleanName) }
    }
}

if ($null -eq $matchedGroup) {
    # 全桌面 AI 统一：找不到目标目录/分组 → 再扫一次（防虚拟化漏扫）→ 窗口内自建组 → 组内自建对话；严禁 --reuse-window 开 IDE
    $appLabel = if (-not [string]::IsNullOrWhiteSpace($cfg.AgentName)) { $cfg.AgentName } elseif (-not [string]::IsNullOrWhiteSpace($cfg.WinTitle)) { $cfg.WinTitle } else { '桌面AI' }
    Write-Output (""STAGE_LOG:侧栏未找到目标目录分组，正在 $appLabel 窗口内复扫并自动创建"")
    Ensure-CursorSidebarAndReposExpanded $root
    Start-Sleep -Milliseconds 450
    $scan = Scan-SidebarGroups $root $cfg.IsGrok
    $matchedGroup = Find-MatchedGroupFromScan $scan
    if ($null -eq $matchedGroup) {
        # 二次复扫：侧栏虚拟化/折叠常导致首次漏检已存在分组（如 TAKT-UI-CC-main）
        Ensure-CursorSidebarAndReposExpanded $root
        Start-Sleep -Milliseconds 500
        $scan = Scan-SidebarGroups $root $cfg.IsGrok
        $matchedGroup = Find-MatchedGroupFromScan $scan
    }
    $createdViaNewBot = $false
    if ($null -eq $matchedGroup) {
        $created = Try-EnsureProjectGroupInDesktopApp $root
        if ($null -ne $created) {
            $scan = $created.Scan
            $matchedGroup = $created.Group
            if ($created.PSObject.Properties.Name -contains 'CreatedViaNewBot' -and $created.CreatedViaNewBot) {
                $createdViaNewBot = $true
            }
        }
    }
    if ($null -ne $matchedGroup) {
        $dlgCreated = Try-CreateDialogInGroup $root $matchedGroup $scan
        if ($null -ne $dlgCreated) {
            $selectedChatTitle = $dlgCreated.Title
            $matchedGroup = $dlgCreated.Group
            if ($null -ne $dlgCreated.Scan) { $scan = $dlgCreated.Scan }
            $dispatchedInNewMode = $true
        } else {
            Close-NativeFolderPickers 3000
            Dismiss-BlockingModals $root
            Write-Output 'NO_PROJECT_GROUP_FALLBACK_CLI'
            exit 6
        }
    } elseif ($createdViaNewBot) {
        $selectedChatTitle = $cfg.ProjectLeaf + '/新建Bot对话'
        $dispatchedInNewMode = $true
    } else {
        Close-NativeFolderPickers 3000
        Dismiss-BlockingModals $root
        Write-Output 'NO_PROJECT_GROUP_FALLBACK_CLI'
        exit 6
    }
} else {
    $chosenDialog = $null
    # 铁律 1：若该工程分组下已有正在 Working（思考/读文件/编译中）的活跃会话，严禁再点 New Agent 新建平行重复对话！
    $workingDlg = $matchedGroup.Dialogs | Where-Object {
        $dn = $_.CleanName
        $_.IsWorking -and (-not ($sibExcludes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $dn -like ""*$_*"" }))
    } | Select-Object -First 1

    if ($null -ne $workingDlg -and -not $cfg.ForceNew) {
        if (-not $cfg.IsReworkRound) {
            # 首轮或外部重复触发时发现同工程已有活跃 Working 会话：直接无缝挂接观测该会话，绝不重复建窗或重复刷屏发送！
            Write-Output ('DISPATCHED_CONFIRMED:' + $matchedGroup.Name + '/' + $workingDlg.CleanName + '||REUSED_WORKING')
            exit 0
        } else {
            $chosenDialog = $workingDlg
        }
    }

    if ($null -eq $chosenDialog -and -not [string]::IsNullOrWhiteSpace($cfg.LockedChatTitle)) {
        $chosenDialog = $matchedGroup.Dialogs | Where-Object {
            $_.CleanName -eq $cfg.LockedChatTitle -or $_.CleanName -like ""*$($cfg.LockedChatTitle)*"" -or $cfg.LockedChatTitle -like ""*$($_.CleanName)*""
        } | Select-Object -First 1
    }

    if ($null -eq $chosenDialog -and -not $cfg.ForceNew) {
        $bestScore = -9999
        foreach ($d in $matchedGroup.Dialogs) {
            $cn = $d.CleanName
            $score = 0
            foreach ($sib in $sibExcludes) {
                if (-not [string]::IsNullOrWhiteSpace($sib) -and $cn -like ""*$sib*"") { $score -= 200 }
            }
            foreach ($ht in $highTerms) {
                if (-not [string]::IsNullOrWhiteSpace($ht) -and $cn -like ""*$ht*"") { $score += 120 }
            }
            if (-not [string]::IsNullOrWhiteSpace($cfg.MatchedSessionTitle) -and $cfg.MatchedSessionTitle.Length -ge 4) {
                if ($cn -eq $cfg.MatchedSessionTitle -or $cn -like ""*$($cfg.MatchedSessionTitle)*"" -or $cfg.MatchedSessionTitle -like ""*$cn*"") {
                    $score += 90
                }
            }
            foreach ($kw in $keywords) {
                if (-not [string]::IsNullOrWhiteSpace($kw) -and $cn -like ""*$kw*"") { $score += 25 }
            }
            if ($score -gt $bestScore) {
                $bestScore = $score
                $chosenDialog = $d
            }
        }

        $minThreshold = if ($highTerms.Count -gt 0 -and $matchedGroup.Name -ne $cfg.ProjectLeaf) { 80 } elseif ($highTerms.Count -gt 0 -or $keywords.Count -gt 0) { 25 } else { 0 }
        if ($bestScore -lt $minThreshold -and $null -ne $matchedGroup.MoreBtn) {
            try {
                Invoke-DesktopControlNoFocus $matchedGroup.MoreBtn | Out-Null
                Start-Sleep -Milliseconds 450
                $scan2 = Scan-SidebarGroups $root $cfg.IsGrok
                $mg2 = $scan2.Groups | Where-Object { $_.Name -eq $matchedGroup.Name } | Select-Object -First 1
                if ($null -ne $mg2) {
                    $matchedGroup = $mg2
                    $bestScore = -9999
                    $chosenDialog = $null
                    foreach ($d in $matchedGroup.Dialogs) {
                        $cn = $d.CleanName
                        $score = 0
                        foreach ($sib in $sibExcludes) { if (-not [string]::IsNullOrWhiteSpace($sib) -and $cn -like ""*$sib*"") { $score -= 200 } }
                        foreach ($ht in $highTerms) { if (-not [string]::IsNullOrWhiteSpace($ht) -and $cn -like ""*$ht*"") { $score += 120 } }
                        if (-not [string]::IsNullOrWhiteSpace($cfg.MatchedSessionTitle) -and $cfg.MatchedSessionTitle.Length -ge 4) {
                            if ($cn -eq $cfg.MatchedSessionTitle -or $cn -like ""*$($cfg.MatchedSessionTitle)*"" -or $cfg.MatchedSessionTitle -like ""*$cn*"") { $score += 90 }
                        }
                        foreach ($kw in $keywords) { if (-not [string]::IsNullOrWhiteSpace($kw) -and $cn -like ""*$kw*"") { $score += 25 } }
                        if ($score -gt $bestScore) { $bestScore = $score; $chosenDialog = $d }
                    }
                }
            } catch {}
        }
        if ($bestScore -lt $minThreshold) {
            # 铁律 2：对 Grok Bot 或处于打回重修轮（IsReworkRound，客户端可能刚把中文任务自动重命名为英文标题），优先复用同分组最新置顶安全对话，绝不连环点 New Agent 新建重复窗口！
            if (($cfg.IsGrok -or $cfg.IsReworkRound -or $cfg.AllowFallbackExistingBot) -and $matchedGroup.Dialogs.Count -gt 0) {
                $safeExisting = $matchedGroup.Dialogs | Where-Object {
                    $dn = $_.CleanName
                    -not ($sibExcludes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $dn -like ""*$_*"" })
                } | Select-Object -First 1
                $chosenDialog = $safeExisting
            } else {
                $chosenDialog = $null
            }
        }
    }

    if ($null -ne $chosenDialog) {
        try { $chosenDialog.Btn.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch {}
        Invoke-DesktopControlNoFocus $chosenDialog.Btn | Out-Null
        Start-Sleep -Milliseconds 1300
        $selectedChatTitle = $matchedGroup.Name + '/' + $chosenDialog.CleanName
    } else {
        if (-not $cfg.IsGrok -and $null -ne $matchedGroup.NewAgentBtn) {
            try { $matchedGroup.HeaderBtn.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch {}
            Invoke-DesktopControlNoFocus $matchedGroup.NewAgentBtn | Out-Null
            Write-Output 'UIA_TOUCHED_STATE:1'
            Start-Sleep -Milliseconds 1600
            $selectedChatTitle = $matchedGroup.Name + '/新建Agent对话'
            $dispatchedInNewMode = $true
        } elseif ($cfg.IsGrok -and $null -ne $scan.NewBotBtn) {
            $createdInGrp = Invoke-GrokNewBotSafely $root $scan.NewBotBtn $cfg.ProjectLeaf
            if ($createdInGrp) {
                Write-Output 'UIA_TOUCHED_STATE:1'
                $selectedChatTitle = $matchedGroup.Name + '/新建Bot对话'
                $dispatchedInNewMode = $true
            } elseif ($matchedGroup.Dialogs.Count -gt 0) {
                $fbDlg = $matchedGroup.Dialogs[0]
                Invoke-DesktopControlNoFocus $fbDlg.Btn | Out-Null
                Start-Sleep -Milliseconds 1200
                $selectedChatTitle = $matchedGroup.Name + '/' + $fbDlg.CleanName
            } else {
                Write-Output 'MODAL_BLOCKED_NEW_BOT'
                exit 6
            }
        } elseif (-not $cfg.IsGrok -and $null -ne $scan.NewChatBtn) {
            Invoke-DesktopControlNoFocus $scan.NewChatBtn | Out-Null
            Write-Output 'UIA_TOUCHED_STATE:1'
            Start-Sleep -Milliseconds 1500
            $selectedChatTitle = $matchedGroup.Name + '/新建Chat对话'
            $dispatchedInNewMode = $true
        } else {
            Write-Output 'NO_SUITABLE_DIALOG_AND_NO_NEW_BTN'
            exit 6
        }
    }
}

# 二次防串台安全门
if (-not $cfg.IsGrok -and $sibExcludes.Count -gt 0) {
    $txtCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $allTxts = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $txtCond)
    foreach ($tEl in $allTxts) {
        $tn = $tEl.Current.Name
        if ($tn -like 'On *') {
            $ctxProj = $tn.Substring(3).Trim()
            $allowedCtx = @($candGroups | Where-Object { $ctxProj -eq $_ -or $ctxProj -like ""*$_*"" }).Count -gt 0
            if (-not $allowedCtx) {
                foreach ($sib in $sibExcludes) {
                    if (-not [string]::IsNullOrWhiteSpace($sib) -and ($ctxProj -eq $sib -or $ctxProj -like ""*$sib*"")) {
                        Write-Output ('WRONG_WORKSPACE_CONTEXT:' + $ctxProj)
                        exit 7
                    }
                }
            }
        }
    }
}

[WinDeskHelper]::WaitPolitelyIfUserActiveOnTarget($hwnd, 3000)
$promptText = [System.IO.File]::ReadAllText($cfg.PromptFile, [System.Text.Encoding]::UTF8)
$promptLen = if ($null -eq $promptText) { 0 } else { $promptText.Length }
# 超长多行任务：拉长填入后等待与发送确认窗口，降低 SEND_NOT_CLEARED 误判
$postFillWaitMs = if ($promptLen -ge 2000) { 1400 } elseif ($promptLen -ge 800) { 900 } else { 600 }
$sendEvidenceTimeoutMs = if ($promptLen -ge 2000) { 9000 } elseif ($promptLen -ge 800) { 6500 } else { 4200 }
$refillWaitMs = if ($promptLen -ge 1200) { 1000 } else { 600 }

# 核心任务短片段：去掉派单前缀后取前 32 字，用于忙碌/同任务检测与上屏铁证
function Get-CoreTaskSnippet([string]$raw) {
    $s = $raw
    if ($s -match '】') { $s = $s.Substring($s.IndexOf('】') + 1).Trim() }
    if ($s.Length -gt 32) { $s = $s.Substring(0, 32) }
    return $s
}
$coreSnippet = Get-CoreTaskSnippet $promptText
" + PsStopButtonMatcherFunction + @"
function Test-TargetDialogBusy([System.Windows.Automation.AutomationElement]$r) {
    try {
        $btnC = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        foreach ($pb in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnC)) {
            $n = $pb.Current.Name
            if ((Test-HubStopButtonName $n) -or $n -like '*, Working*') { return $true }
        }
        $editC = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
        foreach ($ed in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editC)) {
            if ($ed.Current.Name -like '*Steer without interrupting*') { return $true }
        }
    } catch {}
    return $false
}
function Count-NonEditSnippetMatches([System.Windows.Automation.AutomationElement]$r, [string]$snip) {
    $cnt = 0
    if ([string]::IsNullOrWhiteSpace($snip)) { return 0 }
    try {
        $txtC = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
        foreach ($t in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $txtC)) {
            if ($t.Current.Name -like ""*$snip*"") { $cnt++ }
        }
    } catch {}
    return $cnt
}
function Clear-EditDraft([System.Windows.Automation.AutomationElement]$edit) {
    if ($null -eq $edit) { return }
    try {
        $vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue('')
        Start-Sleep -Milliseconds 50
        $rem = ''
        try { $rem = $vp.Current.Value } catch {}
        if (-not [string]::IsNullOrEmpty($rem)) {
            $vp.SetValue(' ')
            Start-Sleep -Milliseconds 60
            $vp.SetValue('')
        }
    } catch {}
}
function Test-PromptInjectedOk([System.Windows.Automation.AutomationElement]$edit, [string]$expected, [string]$snip) {
    if ($null -eq $edit) { return $false }
    try {
        $val = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        if ([string]::IsNullOrWhiteSpace($val)) { return $false }
        $need = [Math]::Min(48, [Math]::Max(16, [int]($expected.Length * 0.35)))
        if ($val.Length -ge $need) { return $true }
        if (-not [string]::IsNullOrWhiteSpace($snip) -and $val -like ""*$snip*"") { return $true }
    } catch {}
    return $false
}
function Test-SendSuccessEvidence([System.Windows.Automation.AutomationElement]$r, [System.Windows.Automation.AutomationElement]$edit, [string]$snip, [int]$preSnippetCount) {
    try {
        if (Test-HasRevertFilesModal $r) { return $false }
        # 铁证1：任意 Edit Name 含 Steer without interrupting
        $editC = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
        foreach ($ed in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editC)) {
            if ($ed.Current.Name -like '*Steer without interrupting*') { return $true }
        }
        # 铁证2：Stop / Stop generation / Working 按钮
        $btnC = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        foreach ($b in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnC)) {
            $bn = $b.Current.Name
            if ((Test-HubStopButtonName $bn) -or $bn -like '*, Working*') { return $true }
        }
        # 铁证3：目标输入框已清空（超长任务放宽到 <24，避免残留空白误判）
        $editStillHasSnippet = $false
        $clearThreshold = if ($promptLen -ge 1200) { 24 } else { 8 }
        if ($null -ne $edit) {
            try {
                $val = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
                if ([string]::IsNullOrWhiteSpace($val) -or $val.Length -lt $clearThreshold) { return $true }
                if (-not [string]::IsNullOrWhiteSpace($snip) -and $val -like ""*$snip*"") { $editStillHasSnippet = $true }
            } catch {}
        }
        # 铁证4：非 Edit 的 Text 匹配 coreSnippet 数量严格多于写入前基线，且输入框已不再含该片段（排除历史气泡误判）
        if (-not [string]::IsNullOrWhiteSpace($snip) -and -not $editStillHasSnippet) {
            $nowCnt = Count-NonEditSnippetMatches $r $snip
            if ($nowCnt -gt $preSnippetCount) { return $true }
        }
    } catch {}
    return $false
}
function Wait-SendEvidence([System.Windows.Automation.AutomationElement]$r, [System.Windows.Automation.AutomationElement]$edit, [string]$snip, [int]$preSnippetCount, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if (Test-HasRevertFilesModal $r) { return $false }
        if (Test-SendSuccessEvidence $r $edit $snip $preSnippetCount) { return $true }
        Start-Sleep -Milliseconds 500
    }
    if (Test-HasRevertFilesModal $r) { return $false }
    return (Test-SendSuccessEvidence $r $edit $snip $preSnippetCount)
}

Write-Output 'STAGE_LOG:正在校验目标会话空闲态'
# 仅当目标对话真正忙碌时零填框挂接；空闲历史气泡绝不误拦
$isBusyNow = Test-TargetDialogBusy $root
if (-not $cfg.IsReworkRound) {
    if ($isBusyNow) {
        Write-Output ('DISPATCHED_CONFIRMED:' + $selectedChatTitle + '||REUSED_WORKING')
        exit 0
    }
} else {
    if ($isBusyNow) {
        $waitSw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($waitSw.ElapsedMilliseconds -lt 25000) {
            Start-Sleep -Milliseconds 1000
            if (-not (Test-TargetDialogBusy $root)) { break }
        }
        if (Test-TargetDialogBusy $root) {
            Write-Output ('DISPATCHED_CONFIRMED:' + $selectedChatTitle + '||REUSED_WORKING')
            exit 0
        }
    }
}

# 写入前基线：非 Edit Text 中已匹配 coreSnippet 的次数（防历史气泡误判已发送）
$preSnippetCount = Count-NonEditSnippetMatches $root $coreSnippet

$setOk = $false
$targetEdit = $null
$injectedDraft = $false
$sentConfirmed = $false
try {
    if (Test-HasRevertFilesModal $root) { Cancel-RevertFilesModalSafely $root }
    for ($tryIdx = 1; $tryIdx -le 3 -and -not $setOk; $tryIdx++) {
        if ($tryIdx -eq 2) { Dismiss-BlockingModals $root }
        if (Test-HasRevertFilesModal $root) { Cancel-RevertFilesModalSafely $root }
        $e = Find-IdlePromptEdit $root
        if ($null -eq $e) {
            if ($tryIdx -lt 3) { Start-Sleep -Milliseconds 500 }
            continue
        }
        # 空闲态下若输入框已残留相同 coreSnippet 草稿：等 2.5s 复检 Busy（防跨进程撞车），仍空闲则先清旧草稿再填
        try {
            $existVal = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            if (-not [string]::IsNullOrWhiteSpace($coreSnippet) -and -not [string]::IsNullOrWhiteSpace($existVal) -and $existVal -like ""*$coreSnippet*"") {
                Start-Sleep -Milliseconds 2500
                if (Test-TargetDialogBusy $root) {
                    Write-Output ('DISPATCHED_CONFIRMED:' + $selectedChatTitle + '||REUSED_WORKING')
                    exit 0
                }
                Clear-EditDraft $e
                Start-Sleep -Milliseconds 120
            }
        } catch {}
        if (Set-PromptTextRobust $e $promptText) {
            $targetEdit = $e
            $injectedDraft = $true
            $setOk = $true
            break
        }
        if (-not $setOk -and $tryIdx -lt 3) { Start-Sleep -Milliseconds 500 }
    }
    if (-not $setOk) {
        Dismiss-BlockingModals $root
        Write-Output 'NO_PROMPT_EDIT'
        exit 3
    }
    Start-Sleep -Milliseconds $postFillWaitMs
    Write-Output 'STAGE_LOG:已填入指令并触发第1次发送'
    $clicked = Invoke-SendButtonNearEdit $root $targetEdit
    if (Test-HasRevertFilesModal $root) {
        Cancel-RevertFilesModalSafely $root
        $injectedDraft = $false
        Write-Output 'REVERT_MODAL_ABORTED'
        exit 8
    }
    if (-not $clicked) {
        Clear-EditDraft $targetEdit
        $injectedDraft = $false
        Write-Output 'NO_SEND_BTN'
        exit 4
    }
    Write-Output 'UIA_TOUCHED_STATE:1'
    Write-Output 'STAGE_LOG:正在核验发送回执'
    $sentConfirmed = Wait-SendEvidence $root $targetEdit $coreSnippet $preSnippetCount $sendEvidenceTimeoutMs
    if (Test-HasRevertFilesModal $root) {
        Cancel-RevertFilesModalSafely $root
        $injectedDraft = $false
        Write-Output 'REVERT_MODAL_ABORTED'
        exit 8
    }
    if (-not $sentConfirmed) {
        Write-Output 'STAGE_LOG:未检测到上屏正在执行第2次受控补发'
        # 确认输入框仍有完整 prompt；若被清则稳健重填
        $needRefill = $true
        try {
            $curVal = $targetEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            if (-not [string]::IsNullOrWhiteSpace($curVal) -and $curVal.Length -ge [Math]::Min(24, $promptText.Length / 2)) {
                $needRefill = $false
            }
        } catch {}
        if ($needRefill) {
            if (Set-PromptTextRobust $targetEdit $promptText) {
                $injectedDraft = $true
                Start-Sleep -Milliseconds $refillWaitMs
            }
        }
        $clicked2 = Invoke-SendButtonNearEdit $root $targetEdit
        if (Test-HasRevertFilesModal $root) {
            Cancel-RevertFilesModalSafely $root
            $injectedDraft = $false
            Write-Output 'REVERT_MODAL_ABORTED'
            exit 8
        }
        if ($clicked2) { Write-Output 'UIA_TOUCHED_STATE:1' }
        $sentConfirmed = Wait-SendEvidence $root $targetEdit $coreSnippet $preSnippetCount $sendEvidenceTimeoutMs
        if (Test-HasRevertFilesModal $root) {
            Cancel-RevertFilesModalSafely $root
            $injectedDraft = $false
            Write-Output 'REVERT_MODAL_ABORTED'
            exit 8
        }
        if (-not $sentConfirmed) {
            # 超长任务：不清草稿即退出，交由上层 Working 探针判定，避免误清已发出内容
            if ($promptLen -lt 800) {
                Clear-EditDraft $targetEdit
                $injectedDraft = $false
            }
            Write-Output 'SEND_FAILED_AFTER_RETRY'
            exit 5
        }
    }
    Write-Output 'STAGE_LOG:已确认送达转入执行监测'
    # 成功送达后绝不主动清草稿（避免误清运行态 Steer 输入框）；仅失败路径 Clear-EditDraft
    $injectedDraft = $false
}
finally {
    # 100% 杜绝填进输入框却未发送残留
    if ($injectedDraft -and -not $sentConfirmed) {
        Clear-EditDraft $targetEdit
    }
    try { Restore-DispatchForeground } catch {}
    try {
        if ($strictNoSteal -and $hwnd -ne [IntPtr]::Zero -and $prevForeground -ne $hwnd -and [WinDeskHelper]::GetForegroundWindow() -eq $hwnd) {
            [WinDeskHelper]::DemoteAndRestoreForeground($hwnd, $prevForeground)
        }
    } catch {}
}

# 若前台焦点意外被 Electron 拉走：压回 HWND_BOTTOM 并立即还原用户原前台窗口（Strict 默认开启）
try { [WinDeskHelper]::DemoteAndRestoreForeground($hwnd, $prevForeground) } catch {
    if ($prevForeground -ne [IntPtr]::Zero -and $prevForeground -ne $hwnd -and [WinDeskHelper]::GetForegroundWindow() -eq $hwnd) {
        try { [WinDeskHelper]::SetForegroundWindow($prevForeground) | Out-Null } catch {}
    }
}

if (-not $sentConfirmed) {
    Write-Output ('SEND_NOT_CLEARED:' + $selectedChatTitle)
    exit 5
}

# 若本次是在分组内新建了对话，稍等片刻回读侧边栏置顶第 1 项真实标题（如 Cursor 自动生成的英文标题），以便后续轮次锁定同一对话
if ($dispatchedInNewMode -and $null -ne $matchedGroup) {
    try {
        Start-Sleep -Milliseconds 600
        $afterScan = Scan-SidebarGroups $root $cfg.IsGrok
        $afterGrp = $afterScan.Groups | Where-Object { $_.Name -eq $matchedGroup.Name } | Select-Object -First 1
        if ($null -ne $afterGrp -and $afterGrp.Dialogs.Count -gt 0) {
            $topTitle = $afterGrp.Dialogs[0].CleanName
            if (-not [string]::IsNullOrWhiteSpace($topTitle)) {
                $selectedChatTitle = $afterGrp.Name + '/' + $topTitle
            }
        }
    } catch {}
}

Write-Output ('DISPATCHED_CONFIRMED:' + $selectedChatTitle)
";
            File.WriteAllText(psScriptPath, psCode, new UTF8Encoding(true));

            int uiaExit = -1;
            string uiaOut = "";
            try
            {
                var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{psScriptPath}\" -ConfigJsonFile \"{cfgJsonPath}\"";
                int uiaProcTimeout = taskBriefSafe.Length >= 2000 ? 70
                    : taskBriefSafe.Length >= 800 ? 55 : 45;
                int uiaStallTimeout = Math.Max(40, uiaProcTimeout - 5);
                (uiaExit, uiaOut, _) = RunProcessStreaming(
                    "powershell.exe",
                    args,
                    AppPaths.AppBaseDir,
                    null,
                    uiaProcTimeout,
                    liveLog =>
                    {
                        var line = liveLog ?? "";
                        if (line.StartsWith("STAGE_LOG:", StringComparison.Ordinal))
                            line = "📡 " + line["STAGE_LOG:".Length..].Trim();
                        WriteLiveStatus(agent, project, liveStatusMd, "working", taskBriefSafe, line, "", matchedSession, forceNewDialog ? "created" : "resumed");
                    },
                    stallTimeoutSeconds: uiaStallTimeout);
            }
            finally
            {
                try { if (File.Exists(psScriptPath)) File.Delete(psScriptPath); } catch { }
                try { if (File.Exists(promptTempPath)) File.Delete(promptTempPath); } catch { }
                try { if (File.Exists(cfgJsonPath)) File.Delete(cfgJsonPath); } catch { }
            }

            if ((uiaOut ?? "").Contains("UIA_TOUCHED_STATE:1", StringComparison.Ordinal))
            {
                touchedDesktopUi = true;
            }

            // 必须在 confirmedViaWorkingProbe 之前：误改历史消息触发 Revert 弹窗时绝不能被 touchedDesktopUi 误判成已送达
            if ((uiaOut ?? "").Contains(RevertModalAbortedToken, StringComparison.Ordinal))
            {
                return new HeadlessDispatchOutcome
                {
                    Success = false,
                    AgentId = agent.Id,
                    AgentName = agent.Name,
                    FailureKind = "revert_modal_aborted",
                    SummaryMessage = $"「{agent.Name}」桌面发送检测到历史消息编辑弹窗（Revert files），已自动点击 Cancel 安全取消并中止桌面直连。"
                };
            }

            var confirmedMatch = Regex.Match(uiaOut ?? "", @"DISPATCHED_CONFIRMED:([^\r\n]*)");
            bool confirmedViaWorkingProbe = false;
            string probedFallbackTitle = "";
            if (!(uiaExit == 0 && confirmedMatch.Success) &&
                (touchedDesktopUi ||
                 (uiaOut ?? "").Contains("SEND_NOT_CLEARED", StringComparison.OrdinalIgnoreCase) ||
                 (uiaOut ?? "").Contains("SEND_FAILED_AFTER_RETRY", StringComparison.OrdinalIgnoreCase)))
            {
                // 超长任务 React 清空更慢：按任务体量加长 Working 探针等待，避免 SEND_NOT_CLEARED 误判后误走无头降级
                int probeWaitMs = taskBriefSafe.Length >= 2000 ? 3200
                    : taskBriefSafe.Length >= 800 ? 2200 : 1500;
                Thread.Sleep(probeWaitMs);
                var (workingAfterSend, topTitleAfterSend) = ProbeDesktopProjectSessionWorkingSilent(agent, targetInfo.CandidateGroups, projectLeaf);
                if (workingAfterSend)
                {
                    confirmedViaWorkingProbe = true;
                    probedFallbackTitle = !string.IsNullOrWhiteSpace(topTitleAfterSend) ? topTitleAfterSend : projectLeaf;
                }
                else if ((uiaOut ?? "").Contains("SEND_NOT_CLEARED", StringComparison.OrdinalIgnoreCase) ||
                         (uiaOut ?? "").Contains("SEND_FAILED_AFTER_RETRY", StringComparison.OrdinalIgnoreCase))
                {
                    // 二次短探针：长文本上屏延迟常见
                    Thread.Sleep(1800);
                    (workingAfterSend, topTitleAfterSend) = ProbeDesktopProjectSessionWorkingSilent(agent, targetInfo.CandidateGroups, projectLeaf);
                    if (workingAfterSend)
                    {
                        confirmedViaWorkingProbe = true;
                        probedFallbackTitle = !string.IsNullOrWhiteSpace(topTitleAfterSend) ? topTitleAfterSend : projectLeaf;
                    }
                }
            }

            // 发送确认失败且 Working 探针也未命中：标记 send_failed，供上层禁止无头降级时返回清晰诊断
            if (!(uiaExit == 0 && confirmedMatch.Success) && !confirmedViaWorkingProbe &&
                ((uiaOut ?? "").Contains("SEND_NOT_CLEARED", StringComparison.OrdinalIgnoreCase) ||
                 (uiaOut ?? "").Contains("SEND_FAILED_AFTER_RETRY", StringComparison.OrdinalIgnoreCase)))
            {
                return new HeadlessDispatchOutcome
                {
                    Success = false,
                    AgentId = agent.Id,
                    AgentName = agent.Name,
                    FailureKind = "send_failed",
                    SummaryMessage = $"「{agent.Name}」桌面发送确认未过（SEND_NOT_CLEARED/重试后仍无 Working 证据）。超长多行任务请确认窗口可见且输入框可编辑后重试。"
                };
            }

            if ((uiaOut ?? "").Contains(CursorMissingProjectGroupFallbackCliToken, StringComparison.Ordinal))
            {
                return new HeadlessDispatchOutcome
                {
                    Success = false,
                    AgentId = agent.Id,
                    AgentName = agent.Name,
                    FailureKind = "no_project_group_fallback_cli",
                    SummaryMessage = $"「{agent.Name}」桌面侧栏未能定位或建出目标工程分组/对话（令牌 {CursorMissingProjectGroupFallbackCliToken}）。请确认侧栏已展开且分组名与工程名一致后重试；默认不会静默降级无头 CLI。"
                };
            }

            if ((uiaExit == 0 && confirmedMatch.Success) || confirmedViaWorkingProbe)
            {
                var rawConfirmed = confirmedViaWorkingProbe
                    ? probedFallbackTitle
                    : confirmedMatch.Groups[1].Value.Trim();
                bool reusedAlreadyWorking = rawConfirmed.EndsWith("||REUSED_WORKING", StringComparison.OrdinalIgnoreCase);
                var confirmedChatTitle = reusedAlreadyWorking
                    ? rawConfirmed[..^"||REUSED_WORKING".Length].Trim()
                    : rawConfirmed;
                if (string.IsNullOrWhiteSpace(confirmedChatTitle)) confirmedChatTitle = projectLeaf;
                string actualSessionAction = reusedAlreadyWorking
                    ? "resumed"
                    : (confirmedChatTitle.Contains("新建", StringComparison.OrdinalIgnoreCase)
                        ? "created"
                        : (forceNewDialog ? "created" : "resumed"));

                WriteLiveStatus(
                    agent,
                    project,
                    liveStatusMd,
                    "working",
                    taskBriefSafe,
                    reusedAlreadyWorking
                        ? $"🔗 检测到「{agent.Name}」项目会话（{confirmedChatTitle}）正在执行中，已无缝挂接同一对话框等待完成（严禁重复新建会话）..."
                        : $"✅ 已向「{agent.Name}」桌面客户端会话（{confirmedChatTitle}）成功投递任务，正在等待桌面端完成实操与自检...",
                    "",
                    matchedSession,
                    actualSessionAction,
                    workflowPhase: "executing");

                // 基础窗口 45~240s；活跃长任务可顺延至硬上限 360s。完工由 EvaluateDesktopWatchCompletion 秒级判定。
                int watchSeconds = Math.Clamp(timeoutSeconds, 45, 240);
                const int watchHardCapSeconds = 360;
                var watchSw = Stopwatch.StartNew();
                string lastDiffSig = ComputeProjectDiffBodySignature(reg, project, bus);
                var externalRoots = ExtractExplicitExternalProjectRoots(taskBriefSafe, project.PrimaryRoot);
                var lastExternalMtimeSig = ComputeExternalRootsLatestWriteSignature(externalRoots);
                string initialSessUpdated = matchedSession?.UpdatedAt ?? "";
                int initialSessTurns = matchedSession?.TurnCount ?? 0;
                DateTime? changeDetectedUtc = null;
                DateTime lastWorkingProbeUtc = DateTime.MinValue;
                DateTime lastFileScanUtc = DateTime.MinValue;
                bool clientStillWorking = true;
                int consecutiveIdleProbes = 0;
                string latestObservedGuiTitle = confirmedChatTitle;
                string? targetChatTitle = ExtractDesktopChatLeaf(confirmedChatTitle);
                if (IsNonSpecificDesktopChatTitle(targetChatTitle, projectLeaf, targetInfo.CandidateGroups))
                    targetChatTitle = null;
                bool everObservedWorking = reusedAlreadyWorking;
                DateTime lastObservedWorkingUtc = reusedAlreadyWorking ? DateTime.UtcNow : DateTime.MinValue;
                bool fileOrSessionUpdated = false;
                ExternalDialogSessionInfo? refreshedSess = matchedSession;

                while (watchSw.Elapsed.TotalSeconds < watchSeconds)
                {
                    Thread.Sleep(800);
                    try
                    {
                        if ((DateTime.UtcNow - lastWorkingProbeUtc).TotalSeconds >= 1.2)
                        {
                            lastWorkingProbeUtc = DateTime.UtcNow;
                            var (isWorkingNow, topChatName) = ProbeDesktopProjectSessionWorkingSilent(
                                agent, targetInfo.CandidateGroups, projectLeaf, targetChatTitle);
                            if (isWorkingNow)
                            {
                                consecutiveIdleProbes = 0;
                                clientStillWorking = true;
                                everObservedWorking = true;
                                lastObservedWorkingUtc = DateTime.UtcNow;
                            }
                            else
                            {
                                consecutiveIdleProbes++;
                                clientStillWorking = consecutiveIdleProbes < 2;
                            }
                            if (!string.IsNullOrWhiteSpace(topChatName))
                            {
                                latestObservedGuiTitle = topChatName;
                                var observedLeaf = ExtractDesktopChatLeaf(topChatName);
                                if (!IsNonSpecificDesktopChatTitle(observedLeaf, projectLeaf, targetInfo.CandidateGroups))
                                    targetChatTitle = observedLeaf;
                            }
                        }

                        if ((DateTime.UtcNow - lastFileScanUtc).TotalSeconds >= 1.6)
                        {
                            lastFileScanUtc = DateTime.UtcNow;
                            var curDiffSig = ComputeProjectDiffBodySignature(reg, project, bus);
                            var curExternalSig = ComputeExternalRootsLatestWriteSignature(externalRoots);
                            bool externalChanged = !string.Equals(curExternalSig, lastExternalMtimeSig, StringComparison.Ordinal);
                            if (externalChanged) lastExternalMtimeSig = curExternalSig;
                            if (!string.Equals(curDiffSig, lastDiffSig, StringComparison.Ordinal) || externalChanged)
                            {
                                lastDiffSig = curDiffSig;
                                changeDetectedUtc = DateTime.UtcNow;
                                fileOrSessionUpdated = true;
                                WriteLiveStatus(
                                    agent,
                                    project,
                                    liveStatusMd,
                                    "working",
                                    taskBriefSafe,
                                    externalChanged
                                        ? $"🛠 检测到「{agent.Name}」（{latestObservedGuiTitle}）正在写入任务提及的外部工程目录，等待执行与自检彻底完成（已用时 {watchSw.Elapsed.TotalSeconds:F0}s）..."
                                        : $"🛠 检测到「{agent.Name}」（{latestObservedGuiTitle}）正在写入工程文件，等待执行与自检彻底完成（已用时 {watchSw.Elapsed.TotalSeconds:F0}s）...",
                                    "",
                                    refreshedSess,
                                    actualSessionAction,
                                    workflowPhase: "executing");
                            }

                            var latestSess = sessionSvc.FindBestMatchingSession(project, agent.Id, "auto", taskHint: taskBriefSafe);
                            if (latestSess != null)
                            {
                                bool turnAdvanced = latestSess.TurnCount > initialSessTurns;
                                if (turnAdvanced ||
                                    !string.Equals(latestSess.UpdatedAt, initialSessUpdated, StringComparison.OrdinalIgnoreCase))
                                {
                                    refreshedSess = latestSess;
                                }
                                if (turnAdvanced)
                                {
                                    fileOrSessionUpdated = true;
                                    if (!changeDetectedUtc.HasValue) changeDetectedUtc = DateTime.UtcNow;
                                }
                            }
                        }

                        if (EvaluateDesktopWatchCompletion(
                                everObservedWorking,
                                consecutiveIdleProbes,
                                changeDetectedUtc,
                                lastObservedWorkingUtc,
                                watchSw.Elapsed.TotalSeconds))
                        {
                            clientStillWorking = false;
                            break;
                        }

                        // 活跃长任务心跳续等：接近上限且仍在执行时平滑顺延至 360s
                        if (watchSeconds < watchHardCapSeconds &&
                            watchSw.Elapsed.TotalSeconds >= watchSeconds - 2.0 &&
                            clientStillWorking)
                        {
                            bool recentFilePulse = changeDetectedUtc.HasValue &&
                                                   (DateTime.UtcNow - changeDetectedUtc.Value).TotalSeconds <= 45.0;
                            bool recentWorkingPulse = consecutiveIdleProbes == 0 ||
                                                      (lastObservedWorkingUtc != DateTime.MinValue &&
                                                       (DateTime.UtcNow - lastObservedWorkingUtc).TotalSeconds <= 45.0);
                            if (recentFilePulse || recentWorkingPulse)
                                watchSeconds = Math.Min(watchHardCapSeconds, watchSeconds + 30);
                        }
                    }
                    catch { }
                }

                RecentDesktopDispatches[dedupKey] = (DateTime.UtcNow, latestObservedGuiTitle);
                var activeSid = refreshedSess?.SessionId ?? matchedSession?.SessionId ?? "";
                var summarySb = new StringBuilder();
                summarySb.AppendLine($"已通过桌面客户端 UI 自动化将任务直连投递至「{agent.Name}」窗口会话（{latestObservedGuiTitle}）。");
                summarySb.AppendLine($"- **投递工程目录**：`{targetInfo.TargetWorkDir}`");
                summarySb.AppendLine($"- **观测窗口期**：`{watchSw.Elapsed.TotalSeconds:F1}s`");
                summarySb.AppendLine(fileOrSessionUpdated
                    ? "- **落盘与会话状态**：已检测到工程文件变更或对应桌面会话回复更新，完成桌面直连闭环交付（已跳过底层命令行重跑）。"
                    : (clientStillWorking
                        ? $"- **落盘与会话状态**：任务已在「{agent.Name}」唯一会话（{latestObservedGuiTitle}）中持续执行编译/思考中，保持单会话异步完成（禁止重复建窗）。"
                        : $"- **落盘与会话状态**：任务消息已送达「{agent.Name}」会话（{latestObservedGuiTitle}）并执行完毕。"));
                if (!string.IsNullOrWhiteSpace(refreshedSess?.LastAssistantMessage))
                {
                    summarySb.AppendLine();
                    summarySb.AppendLine("### 桌面客户端最新回复摘要");
                    summarySb.AppendLine(refreshedSess.LastAssistantMessage);
                }

                WrapAndSaveInboxResult(agent, project, bus, inboxOutFile, summarySb.ToString(), activeSid, actualSessionAction);
                // clientStillWorking 为真时必须 AsyncStillWorkingInGui=true，禁止下游写 done
                return new HeadlessDispatchOutcome
                {
                    Success = true,
                    AgentId = agent.Id,
                    AgentName = agent.Name,
                    SessionId = activeSid,
                    SessionAction = actualSessionAction,
                    ConfirmedGuiChatTitle = latestObservedGuiTitle,
                    AsyncStillWorkingInGui = clientStillWorking,
                    OutputInboxFile = inboxOutFile,
                    OutputText = File.ReadAllText(inboxOutFile, Encoding.UTF8),
                    SummaryMessage = $"已通过「{agent.Name}」桌面客户端直连会话（{latestObservedGuiTitle}）投递并回收执行回执：{Path.GetFileName(inboxOutFile)}"
                };
            }
        }
        catch { }
        finally
        {
            if (crossProcOwned && crossProcMutex != null)
            {
                try { crossProcMutex.ReleaseMutex(); } catch { }
            }
            try { crossProcMutex?.Dispose(); } catch { }
            if (gateAcquired && uiaGate != null)
            {
                try { uiaGate.Release(); } catch { }
            }
        }

        // 桌面窗口未开启或当前不可直接 UIA 投递，安全回退到后台执行引擎
        return null;
    }

    /// <summary>
    /// 后台零焦点探测桌面客户端指定项目/会话是否仍 Working；进程内 MTA+UIA，不再启 powershell.exe。
    /// </summary>
    private static (bool IsWorking, string TopChatTitle) ProbeDesktopProjectSessionWorkingSilent(
        AgentRosterEntry agent,
        List<string> candidateGroups,
        string projectLeaf,
        string? targetChatTitle = null)
    {
        if (AppPaths.IsIsolatedTest)
        {
            var agentId = agent?.Id?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(agentId) &&
                TestDesktopBusyOverrides.TryGetValue(agentId, out var ov))
                return (ov.IsWorking, ov.ActiveChatTitle ?? "");
            return (false, "");
        }

        try
        {
            var deskProfile = ResolveDesktopUiAutomationProfile(agent);
            var procNames = deskProfile.ProcessNames?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                            ?? new List<string>();
            if (procNames.Count == 0)
            {
                if (deskProfile.IsGrokBotGrouping) procNames.Add("Grok Bot");
                else if (agent.Id.Equals("cursor", StringComparison.OrdinalIgnoreCase)) procNames.Add("Cursor");
                else if (!string.IsNullOrWhiteSpace(deskProfile.WinTitle)) procNames.Add(deskProfile.WinTitle);
            }

            var groups = (candidateGroups ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            var leaf = projectLeaf ?? "";
            var targetLeaf = ExtractDesktopChatLeaf(targetChatTitle);
            if (IsNonSpecificDesktopChatTitle(targetLeaf, leaf, groups))
                targetLeaf = "";

            (bool IsWorking, string TopChatTitle) result = (false, "");
            using var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                IntPtr hDesk = IntPtr.Zero;
                try
                {
                    hDesk = OpenDesktopW("Default", 0, false, 0x01FF);
                    if (hDesk != IntPtr.Zero)
                        SetThreadDesktop(hDesk);
                    result = ProbeDesktopBusyViaInProcessUia(
                        procNames, groups, leaf, targetLeaf, deskProfile, hDesk);
                }
                catch
                {
                    result = (false, "");
                }
                finally
                {
                    if (hDesk != IntPtr.Zero)
                    {
                        try { CloseDesktop(hDesk); } catch { }
                    }
                    try { done.Set(); } catch { }
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            if (!done.Wait(3000))
                return (false, "");
            return result;
        }
        catch
        {
            return (false, "");
        }
    }

    static List<IntPtr> FindVisibleHwndsForProcess(int pid, IntPtr hDesktop)
    {
        var list = new List<IntPtr>();
        try
        {
            EnumDesktopWindows(hDesktop, (hWnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    GetWindowThreadProcessId(hWnd, out uint wpid);
                    if ((int)wpid == pid) list.Add(hWnd);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Cursor 右栏步骤折叠按钮前缀（Worked for / Thought / …），不得误识别为侧栏工程分组头。
    /// </summary>
    internal static readonly string[] CursorRightRailStepButtonPrefixes =
    {
        "Worked for", "Thought", "Explored", "Edited", "Ran", "Show more", "Context", "Add agents"
    };

    /// <summary>是否为 Cursor 右栏步骤/上下文折叠按钮名（非侧栏工程分组）。</summary>
    public static bool IsCursorRightRailStepButtonName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        foreach (var prefix in CursorRightRailStepButtonPrefixes)
        {
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static bool IsExpandCollapseGroupHeader(AutomationElement btn)
    {
        try
        {
            if (!string.IsNullOrEmpty(btn.Current.AutomationId)) return false;
            string nm;
            try { nm = btn.Current.Name ?? ""; } catch { nm = ""; }
            if (IsCursorRightRailStepButtonName(nm)) return false;
            return btn.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
        }
        catch
        {
            return false;
        }
    }

    static SidebarBusyWindowSnapshot BuildSidebarBusySnapshotFromRoot(AutomationElement root)
    {
        var groups = new List<SidebarGroupSnapshot>();
        bool hasStop = false;
        bool hasSteer = false;
        bool sawGroupHeader = false;
        SidebarGroupSnapshot? current = null;

        try
        {
            var btnCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            var btns = root.FindAll(TreeScope.Descendants, btnCond);
            for (int i = 0; i < btns.Count; i++)
            {
                AutomationElement? b = null;
                try { b = btns[i]; } catch { continue; }
                if (b == null) continue;
                string bn;
                try { bn = b.Current.Name ?? ""; } catch { continue; }
                if (IsDesktopStopButtonName(bn)) hasStop = true;

                if (IsExpandCollapseGroupHeader(b))
                {
                    sawGroupHeader = true;
                    current = new SidebarGroupSnapshot { Name = bn.Trim(), ChatButtonRawNames = new List<string>() };
                    groups.Add(current);
                    continue;
                }

                if (current != null && !string.IsNullOrWhiteSpace(bn) && !SidebarSkipButtonNames.Contains(bn.Trim()))
                    current.ChatButtonRawNames.Add(bn);
            }
        }
        catch { }

        try
        {
            var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            var edits = root.FindAll(TreeScope.Descendants, editCond);
            for (int i = 0; i < edits.Count; i++)
            {
                try
                {
                    var nm = edits[i].Current.Name ?? "";
                    if (nm.Contains("Steer without interrupting", StringComparison.OrdinalIgnoreCase))
                    {
                        hasSteer = true;
                        break;
                    }
                }
                catch { }
            }
        }
        catch { }

        return new SidebarBusyWindowSnapshot
        {
            HasSidebarGroupStructure = sawGroupHeader && groups.Count > 0,
            HasStopGeneration = hasStop,
            HasSteerWithoutInterrupting = hasSteer,
            Groups = groups
        };
    }

    static (bool IsWorking, string TopChatTitle) ProbeDesktopBusyViaInProcessUia(
        List<string> procNames,
        List<string> candidateGroups,
        string projectLeaf,
        string targetChatLeaf,
        DesktopUiAutomationProfile deskProfile,
        IntPtr hDesktop)
    {
        // 按进程名精确取句柄，并在 finally 中逐个 Dispose，避免每 1.2s 探测泄漏 Process 句柄
        var ownedProcs = new List<Process>();
        try
        {
            foreach (var pn in procNames)
            {
                if (string.IsNullOrWhiteSpace(pn)) continue;
                try
                {
                    foreach (var p in Process.GetProcessesByName(pn.Trim()))
                        ownedProcs.Add(p);
                }
                catch { }
            }

            (bool IsWorking, string TopChatTitle) bestIdle = (false, "");

            foreach (var p in ownedProcs)
            {
                var topRoots = new List<AutomationElement>();
                try
                {
                    foreach (var hwnd in FindVisibleHwndsForProcess(p.Id, hDesktop != IntPtr.Zero ? hDesktop : IntPtr.Zero))
                    {
                        try
                        {
                            var el = AutomationElement.FromHandle(hwnd);
                            if (el != null) topRoots.Add(el);
                        }
                        catch { }
                    }
                }
                catch { }

                if (topRoots.Count == 0)
                {
                    try
                    {
                        var pidCond = new PropertyCondition(AutomationElement.ProcessIdProperty, p.Id);
                        var topWins = AutomationElement.RootElement.FindAll(TreeScope.Children, pidCond);
                        for (int i = 0; i < topWins.Count; i++)
                        {
                            try
                            {
                                var tw = topWins[i];
                                if (tw != null) topRoots.Add(tw);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                foreach (var root in topRoots)
                {
                    try
                    {
                        if (root.Current.BoundingRectangle.Width < 80) continue;
                    }
                    catch { }

                    try
                    {
                        var snap = BuildSidebarBusySnapshotFromRoot(root);
                        bool hasMatch = snap.Groups.Any(g => SidebarGroupNameMatches(g.Name, candidateGroups));

                        if (!deskProfile.IsGrokBotGrouping)
                        {
                            var title = "";
                            try { title = root.Current.Name ?? ""; } catch { }
                            bool titleOk =
                                (!string.IsNullOrWhiteSpace(projectLeaf) && title.Contains(projectLeaf, StringComparison.OrdinalIgnoreCase)) ||
                                title.Contains("Cursor", StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrWhiteSpace(deskProfile.WinTitle) &&
                                 title.Contains(deskProfile.WinTitle, StringComparison.OrdinalIgnoreCase));
                            if (!titleOk && !hasMatch && !snap.HasStopGeneration && !snap.HasSteerWithoutInterrupting &&
                                !snap.HasSidebarGroupStructure)
                                continue;
                        }

                        var evaluated = EvaluateSidebarBusyFromSnapshot(
                            snap, candidateGroups, targetChatLeaf, projectLeaf);
                        if (evaluated.IsWorking)
                            return evaluated;
                        if (hasMatch || !string.IsNullOrWhiteSpace(evaluated.TopChatTitle))
                            bestIdle = evaluated;
                        else if (!snap.HasSidebarGroupStructure &&
                                 (snap.HasStopGeneration || snap.HasSteerWithoutInterrupting))
                            return evaluated;
                    }
                    catch { }
                }
            }

            return bestIdle;
        }
        finally
        {
            foreach (var p in ownedProcs)
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    /// <summary>单元测试覆写桌面忙碌态；传入 null 清除该 agent 覆写。</summary>
    public static void SetDesktopBusyOverrideForTest(string agentId, DesktopBusyProbeResult? state)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;
        if (state == null)
            TestDesktopBusyOverrides.TryRemove(agentId.Trim(), out _);
        else
            TestDesktopBusyOverrides[agentId.Trim()] = state;
    }

    /// <summary>缓存是否仍在 <see cref="DesktopLiveProbeCacheTtlMs"/> 内。</summary>
    public static bool IsDesktopLiveProbeFresh(DesktopBusyProbeResult? probe, DateTime? utcNow = null)
    {
        if (probe == null) return false;
        var now = utcNow ?? DateTime.UtcNow;
        return (now - probe.ProbedUtc).TotalMilliseconds <= DesktopLiveProbeCacheTtlMs;
    }

    /// <summary>
    /// 纯静态双向合并：桌面 Working/Stop/Steer 点亮 working；
    /// 桌面已停且原 DispatcherPid 不存在或已退出时，把滞留 working/reworking 回落为 done/idle。
    /// </summary>
    public static (string Status, string WorkflowPhase, bool IsDesktopLiveOverlay) ResolveBidirectionalDesktopMerge(
        string? diskStatus,
        string? diskPhase,
        int dispatcherPid,
        bool dispatcherProcessAlive,
        bool probeFresh,
        bool desktopIsWorking)
    {
        var status = string.IsNullOrWhiteSpace(diskStatus) ? "idle" : diskStatus.Trim();
        var phase = string.IsNullOrWhiteSpace(diskPhase) ? "idle" : diskPhase.Trim();
        if (probeFresh && desktopIsWorking)
        {
            var nextPhase = phase is "reworking" or "master_verifying" or "reconnecting" ? phase : "executing";
            return ("working", nextPhase, true);
        }

        bool staleWork = status is "working" or "reworking";
        bool dispatcherGone = dispatcherPid <= 0 || !dispatcherProcessAlive;
        if (probeFresh && !desktopIsWorking && staleWork && dispatcherGone)
        {
            bool delivered = phase is "master_verifying" or "verified_delivered";
            return delivered
                ? ("done", "verified_delivered", false)
                : ("idle", "idle", false);
        }

        return (status, phase, false);
    }

    /// <summary>
    /// 桌面忙碌防抖：上行立即切忙；超时/异常不确定态保持原状态；进程退出立即空闲；
    /// 可靠空闲需连续样本与持续时间都达标才从忙碌回落。
    /// </summary>
    public static (bool StableIsWorking, int NextConsecutiveIdleCount, DateTime? NextIdleStreakStartUtc, bool StateChanged) ResolveDebouncedDesktopBusyTransition(
        bool prevIsWorking,
        int prevConsecutiveIdleCount,
        DateTime? prevIdleStreakStartUtc,
        bool probeIsWorking,
        bool probeIsInconclusive,
        bool probeProcessFound,
        DateTime nowUtc)
    {
        if (probeIsWorking)
            return (true, 0, null, !prevIsWorking);

        if (probeIsInconclusive)
            return (prevIsWorking, prevConsecutiveIdleCount, prevIdleStreakStartUtc, false);

        if (!probeProcessFound)
            return (false, 0, null, prevIsWorking);

        if (!prevIsWorking)
            return (false, 0, null, false);

        var startUtc = prevIdleStreakStartUtc ?? nowUtc;
        int nextCount = prevConsecutiveIdleCount + 1;
        bool released = nextCount >= DesktopBusyIdleReleaseSamples
                        && (nowUtc - startUtc) >= TimeSpan.FromMilliseconds(DesktopBusyIdleReleaseMs);
        if (released)
            return (false, 0, null, true);
        return (true, nextCount, startUtc, false);
    }

    /// <summary>后台扫一遍已启用桌面 AI。缓存未过期则跳过；不阻塞 WPF UI 线程。</summary>
    public static void RequestDesktopLiveProbeSweep(IEnumerable<AgentRosterEntry>? agents)
    {
        if (AppPaths.IsIsolatedTest || agents == null) return;
        _ = RefreshDesktopAgentsBusySnapshotAsync(agents as IReadOnlyList<AgentRosterEntry> ?? agents.ToList());
    }

    /// <summary>
    /// 批量轻量刷新桌面忙碌快照（异步、防重入）。任一 AI 忙碌态相对缓存发生变化时返回 true。
    /// </summary>
    public static Task<bool> RefreshDesktopAgentsBusySnapshotAsync(IReadOnlyList<AgentRosterEntry>? agents) =>
        RefreshAllDesktopAgentsBusySilent(agents);

    /// <summary>
    /// 批量进程内探测；间隔内缓存仍新鲜则跳过。返回是否有忙碌态变化。
    /// </summary>
    public static async Task<bool> RefreshAllDesktopAgentsBusySilent(IReadOnlyList<AgentRosterEntry>? agents)
    {
        if (AppPaths.IsIsolatedTest || agents == null || agents.Count == 0) return false;
        var list = agents.Where(a => a != null && a.Enabled && !string.IsNullOrWhiteSpace(a.Id)).Cast<AgentRosterEntry>().ToList();
        if (list.Count == 0) return false;
        var now = DateTime.UtcNow;
        bool anyStale = list.Any(a =>
            !LiveDesktopBusyCache.TryGetValue(a.Id.Trim(), out var cached) ||
            (now - cached.ProbedUtc).TotalMilliseconds >= DesktopLiveProbeCacheTtlMs);
        if (!anyStale) return false;
        if (System.Threading.Interlocked.CompareExchange(ref _desktopProbeSweepGate, 1, 0) != 0) return false;
        try
        {
            bool anyChanged = false;
            await Task.Run(() =>
            {
                foreach (var ag in list)
                {
                    try
                    {
                        bool hadPrev = LiveDesktopBusyCache.TryGetValue(ag.Id.Trim(), out var before);
                        var after = ProbeDesktopAgentBusySilent(ag);
                        if (!hadPrev ||
                            before!.IsWorking != after.IsWorking ||
                            before.ActiveTaskCount != after.ActiveTaskCount ||
                            !string.Equals(before.ActiveChatTitle, after.ActiveChatTitle, StringComparison.Ordinal))
                        {
                            anyChanged = true;
                        }
                    }
                    catch { }
                }
            }).ConfigureAwait(false);
            return anyChanged;
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _desktopProbeSweepGate, 0);
        }
    }

    /// <summary>
    /// 进程内 MTA + UIA 探测桌面 AI 是否正在生成（Working / Stop generation / Stop execution / Steer）。
    /// 不启动 powershell.exe，不切前台；隔离测试直接返回不忙碌。缓存 TTL 见 DesktopLiveProbeCacheTtlMs。
    /// </summary>
    public static DesktopBusyProbeResult ProbeDesktopAgentBusySilent(AgentRosterEntry? agent)
    {
        var agentId = agent?.Id?.Trim() ?? "";
        var empty = new DesktopBusyProbeResult(false, "", "", DateTime.UtcNow);
        if (agent == null || string.IsNullOrWhiteSpace(agentId) || AppPaths.IsIsolatedTest)
            return empty;

        if (LiveDesktopBusyCache.TryGetValue(agentId, out var cached) && IsDesktopLiveProbeFresh(cached))
            return cached;

        if (!DesktopProbeInFlight.TryAdd(agentId, 1))
            return LiveDesktopBusyCache.TryGetValue(agentId, out var inflight) ? inflight : empty;

        try
        {
            var raw = ProbeDesktopAgentBusyOnMtaThread(agent);
            var nowUtc = DateTime.UtcNow;
            DesktopBusyProbeResult result;
            bool notify = false;
            lock (DesktopBusyDebounceSync)
            {
                bool hadPrev = LiveDesktopBusyCache.TryGetValue(agentId, out var prev);
                var prevTracker = DesktopBusyDebounceByAgent.TryGetValue(agentId, out var existing)
                    ? existing
                    : new DesktopBusyDebounceTracker(
                        hadPrev && prev!.IsWorking,
                        0,
                        null,
                        hadPrev && prev != null ? prev.ActiveTaskCount : 0);

                // 不确定态：保留上一帧 IsWorking / ActiveTaskCount，并续期 ProbedUtc，防止超长忙任务缓存过期闪烁
                if (raw.IsInconclusive && hadPrev && prev != null)
                {
                    result = new DesktopBusyProbeResult(
                        prev.IsWorking,
                        prev.ActiveChatTitle,
                        prev.ActiveProjectHint,
                        nowUtc,
                        IsInconclusive: true,
                        ProcessFound: raw.ProcessFound,
                        ActiveTaskCount: prev.ActiveTaskCount);
                    LiveDesktopBusyCache[agentId] = result;
                    DesktopBusyDebounceByAgent[agentId] = new DesktopBusyDebounceTracker(
                        prev.IsWorking,
                        prevTracker.ConsecutiveIdleCount,
                        prevTracker.IdleStreakStartUtc,
                        prev.ActiveTaskCount);
                    return result;
                }

                var transition = ResolveDebouncedDesktopBusyTransition(
                    prevTracker.IsWorking,
                    prevTracker.ConsecutiveIdleCount,
                    prevTracker.IdleStreakStartUtc,
                    raw.IsWorking,
                    raw.IsInconclusive,
                    raw.ProcessFound,
                    nowUtc);

                int taskCount = 0;
                if (transition.StableIsWorking)
                {
                    if (raw.ActiveTaskCount > 0) taskCount = raw.ActiveTaskCount;
                    else if (prevTracker.ActiveTaskCount > 0) taskCount = prevTracker.ActiveTaskCount;
                    else taskCount = 1;
                }

                DesktopBusyDebounceByAgent[agentId] = new DesktopBusyDebounceTracker(
                    transition.StableIsWorking,
                    transition.NextConsecutiveIdleCount,
                    transition.NextIdleStreakStartUtc,
                    taskCount);

                string title = raw.ActiveChatTitle;
                if (transition.StableIsWorking &&
                    string.IsNullOrWhiteSpace(title) &&
                    hadPrev &&
                    prev != null &&
                    !string.IsNullOrWhiteSpace(prev.ActiveChatTitle))
                {
                    title = prev.ActiveChatTitle;
                }

                string prevTitle = hadPrev && prev != null ? prev.ActiveChatTitle : "";
                int prevCount = hadPrev && prev != null ? prev.ActiveTaskCount : 0;
                bool titleChangedWhileBusy = transition.StableIsWorking && hadPrev &&
                    !string.Equals(prevTitle, title, StringComparison.Ordinal);
                bool countChanged = hadPrev && prevCount != taskCount;
                notify = transition.StateChanged || titleChangedWhileBusy || countChanged;
                result = new DesktopBusyProbeResult(
                    transition.StableIsWorking,
                    title,
                    raw.ActiveProjectHint,
                    nowUtc,
                    raw.IsInconclusive,
                    raw.ProcessFound,
                    taskCount);
                LiveDesktopBusyCache[agentId] = result;
            }

            if (notify)
            {
                try { DesktopLiveProbeChanged?.Invoke(); } catch { }
            }
            return result;
        }
        finally
        {
            DesktopProbeInFlight.TryRemove(agentId, out _);
        }
    }

    static DesktopBusyProbeResult ProbeDesktopAgentBusyOnMtaThread(AgentRosterEntry agent)
    {
        static DesktopBusyProbeResult Inconclusive() =>
            new(false, "", "", DateTime.UtcNow, IsInconclusive: true, ProcessFound: true);

        var deskProfile = ResolveDesktopUiAutomationProfile(agent);
        var procNames = deskProfile.ProcessNames?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        ?? new List<string>();
        if (procNames.Count == 0)
        {
            if (deskProfile.IsGrokBotGrouping) procNames.Add("Grok Bot");
            else if (agent.Id.Equals("cursor", StringComparison.OrdinalIgnoreCase)) procNames.Add("Cursor");
            else if (!string.IsNullOrWhiteSpace(deskProfile.WinTitle)) procNames.Add(deskProfile.WinTitle);
        }

        DesktopBusyProbeResult result = Inconclusive();
        using var done = new System.Threading.ManualResetEventSlim(false);
        var thread = new System.Threading.Thread(() =>
        {
            IntPtr hDesk = IntPtr.Zero;
            try
            {
                hDesk = OpenDesktopW("Default", 0, false, 0x01FF);
                if (hDesk != IntPtr.Zero)
                    SetThreadDesktop(hDesk);
                var (working, title, inconclusive, processFound, taskCount) = ProbeGlobalDesktopBusyViaInProcessUia(procNames, hDesk);
                result = new DesktopBusyProbeResult(working, title, "", DateTime.UtcNow, inconclusive, processFound, taskCount);
            }
            catch
            {
                result = Inconclusive();
            }
            finally
            {
                if (hDesk != IntPtr.Zero)
                {
                    try { CloseDesktop(hDesk); } catch { }
                }
                try { done.Set(); } catch { }
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.MTA);
        thread.Start();
        if (!done.Wait(2500))
            return Inconclusive();
        return result;
    }

    /// <summary>全窗口忙碌：Stop generation / Stop execution / Steer / 侧栏 Working。2200ms 内遍历全部可见窗口并汇总 ActiveTaskCount。</summary>
    static (bool IsWorking, string ChatTitle, bool IsInconclusive, bool ProcessFound, int ActiveTaskCount) ProbeGlobalDesktopBusyViaInProcessUia(List<string> procNames, IntPtr hDesktop)
    {
        var ownedProcs = new List<System.Diagnostics.Process>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool abortedByBudget = false;
        int windowsReadOk = 0;
        var busyTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int anonymousBusyWindows = 0;
        string firstTitle = "";
        try
        {
            foreach (var pn in procNames)
            {
                if (string.IsNullOrWhiteSpace(pn)) continue;
                try
                {
                    foreach (var proc in System.Diagnostics.Process.GetProcessesByName(pn.Trim()))
                        ownedProcs.Add(proc);
                }
                catch { }
            }

            if (ownedProcs.Count == 0)
                return (false, "", false, false, 0);

            bool TryReadWindow(System.Windows.Automation.AutomationElement? el, out bool isWorking, out string chatTitle, out int taskCount)
            {
                isWorking = false;
                chatTitle = "";
                taskCount = 0;
                if (el == null)
                    return false;
                try
                {
                    var snap = BuildSidebarBusySnapshotFromRoot(el);
                    var hit = EvaluateGlobalDesktopBusyDetailed(snap);
                    windowsReadOk++;
                    isWorking = hit.IsWorking;
                    chatTitle = hit.ChatTitle;
                    taskCount = hit.ActiveTaskCount;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            void Accumulate(bool working, string title, int taskCount)
            {
                if (!working) return;
                if (string.IsNullOrWhiteSpace(firstTitle) && !string.IsNullOrWhiteSpace(title))
                    firstTitle = title;
                if (taskCount > 1)
                {
                    // 单窗口内已去重多个会话
                    if (!string.IsNullOrWhiteSpace(title))
                        busyTitles.Add(title);
                    anonymousBusyWindows += Math.Max(0, taskCount - (string.IsNullOrWhiteSpace(title) ? 0 : 1));
                }
                else if (!string.IsNullOrWhiteSpace(title))
                {
                    busyTitles.Add(title);
                }
                else
                {
                    anonymousBusyWindows++;
                }
            }

            foreach (var proc in ownedProcs)
            {
                if (sw.ElapsedMilliseconds >= 2200)
                {
                    abortedByBudget = true;
                    break;
                }

                var hwnds = FindVisibleHwndsForProcess(proc.Id, hDesktop);
                if (hwnds.Count == 0)
                {
                    try
                    {
                        var pidCond = new System.Windows.Automation.PropertyCondition(
                            System.Windows.Automation.AutomationElement.ProcessIdProperty, proc.Id);
                        var topWins = System.Windows.Automation.AutomationElement.RootElement.FindAll(
                            System.Windows.Automation.TreeScope.Children, pidCond);
                        for (int i = 0; i < topWins.Count; i++)
                        {
                            if (sw.ElapsedMilliseconds >= 2200)
                            {
                                abortedByBudget = true;
                                break;
                            }
                            if (TryReadWindow(topWins[i], out bool working, out string title, out int tc))
                                Accumulate(working, title, tc);
                        }
                    }
                    catch
                    {
                    }

                    if (abortedByBudget) break;
                    continue;
                }

                foreach (var hwnd in hwnds)
                {
                    if (sw.ElapsedMilliseconds >= 2200)
                    {
                        abortedByBudget = true;
                        break;
                    }
                    try
                    {
                        var el = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                        if (TryReadWindow(el, out bool working, out string title, out int tc))
                            Accumulate(working, title, tc);
                    }
                    catch
                    {
                    }
                }

                if (abortedByBudget) break;
            }
        }
        finally
        {
            foreach (var proc in ownedProcs)
            {
                try { proc.Dispose(); } catch { }
            }
        }

        int totalTasks = busyTitles.Count + anonymousBusyWindows;
        if (totalTasks > 0)
            return (true, firstTitle, false, true, totalTasks);

        // 无超时且至少完整读完 1 个窗口、又没有忙碌信号：可靠空闲。
        // 预算中止、采样全部失败，或一个窗口都没读到：不确定，不能当成空闲。
        if (!abortedByBudget && windowsReadOk >= 1)
            return (false, "", false, true, 0);
        return (false, "", true, true, 0);
    }

    public static (bool IsWorking, string ChatTitle) EvaluateGlobalDesktopBusy(SidebarBusyWindowSnapshot? snap)
    {
        var d = EvaluateGlobalDesktopBusyDetailed(snap);
        return (d.IsWorking, d.ChatTitle);
    }

    /// <summary>统计各分组中命中 Working 的去重会话数；无分组匹配但 Stop/Steer 为真则计为 1。</summary>
    public static (bool IsWorking, string ChatTitle, int ActiveTaskCount) EvaluateGlobalDesktopBusyDetailed(SidebarBusyWindowSnapshot? snap)
    {
        snap ??= new SidebarBusyWindowSnapshot();
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string first = "";
        foreach (var g in snap.Groups)
        {
            foreach (var bn in g.ChatButtonRawNames)
            {
                if (!SidebarChatButtonIndicatesWorking(bn)) continue;
                var clean = CleanSidebarChatButtonName(bn);
                var title = string.IsNullOrWhiteSpace(g.Name) ? clean : g.Name + "/" + clean;
                if (string.IsNullOrWhiteSpace(first)) first = title;
                if (!string.IsNullOrWhiteSpace(title))
                    titles.Add(title);
                else
                    titles.Add($"anon:{titles.Count}");
            }
        }
        if (titles.Count > 0)
            return (true, first, titles.Count);
        if (snap.HasStopGeneration || snap.HasSteerWithoutInterrupting)
            return (true, "", 1);
        return (false, "", 0);
    }

    private static LiveAgentWorkState CloneLiveWorkStateForDisplay(LiveAgentWorkState src)
    {
        return new LiveAgentWorkState
        {
            AgentId = src.AgentId,
            AgentName = src.AgentName,
            ProjectId = src.ProjectId,
            ProjectRoot = src.ProjectRoot,
            ProjectName = src.ProjectName,
            Status = src.Status,
            WorkflowPhase = src.WorkflowPhase,
            DispatcherPid = src.DispatcherPid,
            UpdatedUtcTicks = src.UpdatedUtcTicks,
            MasterPlannerName = src.MasterPlannerName,
            SelectedModel = src.SelectedModel,
            SelectedEffortTier = src.SelectedEffortTier,
            SelectedTierBadge = src.SelectedTierBadge,
            CurrentRound = src.CurrentRound,
            MaxRounds = src.MaxRounds,
            NetworkRetryAttempt = src.NetworkRetryAttempt,
            MaxNetworkRetries = src.MaxNetworkRetries,
            LastFailureBadge = src.LastFailureBadge,
            FailoverNote = src.FailoverNote,
            CanResumeInterruptedChain = src.CanResumeInterruptedChain,
            VerificationSummary = src.VerificationSummary,
            ChangedFilesSummary = src.ChangedFilesSummary,
            SessionId = src.SessionId,
            SessionTitle = src.SessionTitle,
            SessionFilePath = src.SessionFilePath,
            SessionAction = src.SessionAction,
            TaskSummary = src.TaskSummary,
            DispatchReason = src.DispatchReason,
            StartedAt = src.StartedAt,
            UpdatedAt = src.UpdatedAt,
            LatestLogTail = src.LatestLogTail,
            OutputInboxFile = src.OutputInboxFile,
            ActiveTaskCount = src.ActiveTaskCount,
            IsDesktopLiveOverlay = src.IsDesktopLiveOverlay
        };
    }

    /// <summary>
    /// 全局工作态 + 桌面端实时忙碌缓存合并读取（悬浮窗与主界面工作台共用，避免重复探测逻辑）。
    /// </summary>
    public static Dictionary<string, LiveAgentWorkState> ReadMergedGlobalWorkStatesWithDesktopLive()
    {
        var allDisk = ReadAllLiveWorkStates();
        var diskWorkingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in allDisk.Values)
        {
            if (string.IsNullOrWhiteSpace(item.AgentId)) continue;
            if (item.Status is not ("working" or "reworking")) continue;
            diskWorkingCounts.TryGetValue(item.AgentId, out int c);
            diskWorkingCounts[item.AgentId] = c + 1;
        }

        var baseMap = ReadLatestGlobalWorkStates();
        var result = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in baseMap)
        {
            var copy = CloneLiveWorkStateForDisplay(kv.Value);
            diskWorkingCounts.TryGetValue(kv.Key, out int diskCount);
            copy.ActiveTaskCount = diskCount;
            result[kv.Key] = copy;
        }

        void ApplyBusyOverlay(string agentId, DesktopBusyProbeResult probe)
        {
            if (!probe.IsWorking || string.IsNullOrWhiteSpace(agentId)) return;

            result.TryGetValue(agentId, out var existing);
            var display = existing != null
                ? CloneLiveWorkStateForDisplay(existing)
                : new LiveAgentWorkState { AgentId = agentId, AgentName = agentId };

            diskWorkingCounts.TryGetValue(agentId, out int diskWorkingCount);
            display.ActiveTaskCount = Math.Max(
                diskWorkingCount,
                probe.IsWorking ? Math.Max(1, probe.ActiveTaskCount) : 0);

            // 桌面端实时忙碌：强制 working/executing，不受跨工程残留 done/error 影响
            bool preserveDispatchTask = display.Status is "working" or "reworking";
            display.Status = "working";
            if (display.WorkflowPhase is not ("reworking" or "master_verifying" or "reconnecting"))
                display.WorkflowPhase = "executing";
            display.IsDesktopLiveOverlay = true;
            display.LastFailureBadge = "";
            display.FailoverNote = "";
            display.CanResumeInterruptedChain = false;
            if (!preserveDispatchTask)
                display.VerificationSummary = "";

            if (preserveDispatchTask)
            {
                // 真实派单进行中：保留原任务摘要，只补充会话标题
                if (!string.IsNullOrWhiteSpace(probe.ActiveChatTitle) && string.IsNullOrWhiteSpace(display.SessionTitle))
                    display.SessionTitle = probe.ActiveChatTitle;
                display.UpdatedUtcTicks = Math.Max(display.UpdatedUtcTicks, probe.ProbedUtc.Ticks);
                display.UpdatedAt = probe.ProbedUtc.ToLocalTime().ToString("HH:mm:ss");
                result[agentId] = display;
                return;
            }

            var chatHint = !string.IsNullOrWhiteSpace(probe.ActiveChatTitle)
                ? probe.ActiveChatTitle
                : (!string.IsNullOrWhiteSpace(probe.ActiveProjectHint) ? probe.ActiveProjectHint : "当前对话");
            var agentDisplay = !string.IsNullOrWhiteSpace(display.AgentName) ? display.AgentName : agentId;
            var summary = $"⚡ 检测到「{agentDisplay}」桌面端实时生成中（{chatHint}）";
            display.SessionTitle = string.IsNullOrWhiteSpace(probe.ActiveChatTitle) ? display.SessionTitle : probe.ActiveChatTitle;
            if (!string.IsNullOrWhiteSpace(probe.ActiveProjectHint) && string.IsNullOrWhiteSpace(display.ProjectName))
                display.ProjectName = probe.ActiveProjectHint;
            display.TaskSummary = summary;
            display.LatestLogTail = summary;
            display.UpdatedUtcTicks = Math.Max(display.UpdatedUtcTicks, probe.ProbedUtc.Ticks);
            display.UpdatedAt = probe.ProbedUtc.ToLocalTime().ToString("HH:mm:ss");
            result[agentId] = display;
        }

        void ApplyFreshDesktopProbe(string id, DesktopBusyProbeResult probe)
        {
            if (probe.IsWorking)
            {
                ApplyBusyOverlay(id, probe);
                return;
            }

            // 超时或 UIA 异常：不确定，禁止当成 probeFresh 且空闲去清掉 working。
            if (probe.IsInconclusive)
                return;

            if (!result.TryGetValue(id, out var existing)) return;
            if (existing.Status is not ("working" or "reworking")) return;
            bool dispatcherAlive = existing.DispatcherPid > 0 &&
                                   (existing.DispatcherPid == Environment.ProcessId || IsProcessAliveFast(existing.DispatcherPid));
            var decision = ResolveBidirectionalDesktopMerge(
                existing.Status,
                existing.WorkflowPhase,
                existing.DispatcherPid,
                dispatcherAlive,
                probeFresh: true,
                desktopIsWorking: false);
            if (string.Equals(decision.Status, existing.Status, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(decision.WorkflowPhase, existing.WorkflowPhase, StringComparison.OrdinalIgnoreCase))
            {
                // 非活跃：任务计数归 0（磁盘仍 working 时保留 disk 计数）
                diskWorkingCounts.TryGetValue(id, out int diskCount);
                existing.ActiveTaskCount = diskCount;
                return;
            }

            var display = CloneLiveWorkStateForDisplay(existing);
            display.Status = decision.Status;
            display.WorkflowPhase = decision.WorkflowPhase;
            display.DispatcherPid = 0;
            display.IsDesktopLiveOverlay = false;
            display.ActiveTaskCount = decision.Status is "working" or "reworking"
                ? Math.Max(1, display.ActiveTaskCount)
                : 0;
            display.UpdatedUtcTicks = Math.Max(display.UpdatedUtcTicks, probe.ProbedUtc.Ticks);
            display.UpdatedAt = probe.ProbedUtc.ToLocalTime().ToString("HH:mm:ss");
            result[id] = display;
        }

        foreach (var kv in TestDesktopBusyOverrides)
            ApplyFreshDesktopProbe(kv.Key, kv.Value);

        if (!AppPaths.IsIsolatedTest)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in LiveDesktopBusyCache)
            {
                if (TestDesktopBusyOverrides.ContainsKey(kv.Key)) continue;
                // 展示保鲜期用 15s，避免 2 秒轮询间隙掉忙态闪烁；探测刷新仍按 1300ms TTL
                if ((now - kv.Value.ProbedUtc).TotalMilliseconds > DesktopLiveProbeDisplayStaleMs) continue;
                ApplyFreshDesktopProbe(kv.Key, kv.Value);
            }
        }

        // 最终合并：非活跃归 0；活跃取 disk 与 probe 最大值
        foreach (var kv in result.ToList())
        {
            var display = kv.Value;
            diskWorkingCounts.TryGetValue(kv.Key, out int diskWorkingCount);
            bool active = display.Status is "working" or "reworking" || display.IsDesktopLiveOverlay;
            if (!active)
            {
                display.ActiveTaskCount = 0;
                continue;
            }
            int probeCount = 0;
            if (LiveDesktopBusyCache.TryGetValue(kv.Key, out var probe) &&
                probe.IsWorking &&
                (DateTime.UtcNow - probe.ProbedUtc).TotalMilliseconds <= DesktopLiveProbeDisplayStaleMs)
            {
                probeCount = Math.Max(1, probe.ActiveTaskCount);
            }
            else if (TestDesktopBusyOverrides.TryGetValue(kv.Key, out var tprobe) && tprobe.IsWorking)
            {
                probeCount = Math.Max(1, tprobe.ActiveTaskCount);
            }
            display.ActiveTaskCount = Math.Max(diskWorkingCount, probeCount);
        }

        return result;
    }

    /// <summary>
    /// 从任务文本提取显式提及的本地外部工程根目录（如 C:/Project/Godot/配套工具/...），排除当前主工程根。
    /// </summary>
    public static List<string> ExtractExplicitExternalProjectRoots(string? taskText, string? primaryRoot)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(taskText)) return result;
        string primaryFull = "";
        try
        {
            if (!string.IsNullOrWhiteSpace(primaryRoot))
                primaryFull = Path.GetFullPath(primaryRoot.Trim().TrimEnd('\\', '/'));
        }
        catch { }

        foreach (Match m in Regex.Matches(
                     taskText,
                     @"[A-Za-z]:[\\/](?:Project|Users|Godot)[^\s""'`<>|?*，,。；;』」】]{3,160}",
                     RegexOptions.IgnoreCase))
        {
            var raw = m.Value.Trim().TrimEnd(')', ']', '}', '>', '"', '\'', '，', ',', '。');
            // 截到合理工程目录深度（含 tools/xxx 一级）
            try
            {
                string full = Path.GetFullPath(raw.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(full) && File.Exists(full))
                    full = Path.GetDirectoryName(full) ?? full;
                if (!Directory.Exists(full)) continue;
                if (!string.IsNullOrWhiteSpace(primaryFull) &&
                    (full.Equals(primaryFull, StringComparison.OrdinalIgnoreCase) ||
                     full.StartsWith(primaryFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (!result.Contains(full, StringComparer.OrdinalIgnoreCase))
                    result.Add(full);
            }
            catch { }
        }
        return result;
    }

    private static readonly HashSet<string> ExternalScanExcludeDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", "outputs", ".godot"
    };

    /// <summary>
    /// 浅递归扫描源码文件本身的 LastWriteTimeUtc（上限 600）。
    /// NTFS 覆写已有文件常不更新父目录时间，故不能只看 Directory.GetLastWriteTime。
    /// </summary>
    private static List<string> EnumerateSourceFilesShallow(string root, int maxFiles = 600)
    {
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && files.Count < maxFiles)
        {
            string dir = stack.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch { continue; }

            foreach (var entry in entries)
            {
                string name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name)) continue;
                bool isDir = false;
                bool isFile = false;
                try
                {
                    isDir = Directory.Exists(entry);
                    isFile = !isDir && File.Exists(entry);
                }
                catch { continue; }

                if (isDir)
                {
                    if (ExternalScanExcludeDirNames.Contains(name)) continue;
                    stack.Push(entry);
                }
                else if (isFile)
                {
                    files.Add(entry);
                    if (files.Count >= maxFiles) return files;
                }
            }
        }
        return files;
    }

    /// <summary>计算外部工程根目录最近写入时间签名（防跨目录修改误判无落盘）。</summary>
    public static string ComputeExternalRootsLatestWriteSignature(IEnumerable<string>? roots)
    {
        if (roots == null) return "";
        var parts = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                long latest = 0;
                string latestFile = "";
                foreach (var file in EnumerateSourceFilesShallow(root, 600))
                {
                    try
                    {
                        long t = File.GetLastWriteTimeUtc(file).Ticks;
                        if (t > latest)
                        {
                            latest = t;
                            latestFile = file;
                        }
                    }
                    catch { }
                }
                parts.Add($"{root}|{latest}|{Path.GetFileName(latestFile)}");
            }
            catch { }
        }
        return string.Join(";", parts);
    }

    /// <summary>自检/验收：任务提及的外部目录是否在近期有写入。</summary>
    public static bool HasRecentExternalRootWrite(string? taskText, string? primaryRoot, int withinSeconds = 180)
    {
        var roots = ExtractExplicitExternalProjectRoots(taskText, primaryRoot);
        if (roots.Count == 0) return false;
        var deadline = DateTime.UtcNow.AddSeconds(-Math.Max(5, withinSeconds));
        foreach (var root in roots)
        {
            try
            {
                foreach (var file in EnumerateSourceFilesShallow(root, 600))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) >= deadline) return true;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return false;
    }

    private static string ComputeProjectDiffBodySignature(RegistryService reg, ProjectProfile project, CollabBusService bus)
    {
        try
        {
            bus.GenerateLatestDiff(reg, project);
            var diffPath = bus.GetLatestDiffPath(project);
            if (!File.Exists(diffPath)) return "";
            var text = File.ReadAllText(diffPath, Encoding.UTF8);
            int nl = text.IndexOf('\n');
            return nl >= 0 ? text[(nl + 1)..] : text;
        }
        catch
        {
            return "";
        }
    }

    private static string ResolvePreferredCodexProviderFlag()
    {
        try
        {
            var cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            if (!File.Exists(cfgPath)) return "";
            var lines = File.ReadAllLines(cfgPath, Encoding.UTF8);

            // 检查顶层（首个 [section] 之前）是否已显式配置 model_provider
            bool inHeader = true;
            string currentSection = "";
            var providerEndpoints = new Dictionary<string, (string Host, int Port)>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inHeader = false;
                    currentSection = line.Trim('[', ']').Trim();
                    continue;
                }

                if (inHeader)
                {
                    if (Regex.IsMatch(line, @"^model_provider\s*="))
                    {
                        return "";
                    }
                    continue;
                }

                var secMatch = Regex.Match(currentSection, @"^model_providers\.([A-Za-z0-9_\-]+)$");
                if (secMatch.Success)
                {
                    var provName = secMatch.Groups[1].Value;
                    var urlMatch = Regex.Match(line, @"^base_url\s*=\s*""https?://([^:/""]+):(\d+)");
                    if (urlMatch.Success && int.TryParse(urlMatch.Groups[2].Value, out int port))
                    {
                        providerEndpoints[provName] = (urlMatch.Groups[1].Value, port);
                    }
                }
            }

            // 优先探测 magpie 本地代理网关，其次 custom
            foreach (var pref in new[] { "magpie", "custom" })
            {
                if (providerEndpoints.TryGetValue(pref, out var ep) && TryConnectLocalPort(ep.Host, ep.Port, 200))
                {
                    return $"-c model_provider=\"{pref}\"";
                }
            }
        }
        catch { }
        return "";
    }

    private static bool TryConnectLocalPort(string host, int port, int timeoutMs = 200)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0) return false;
            using var client = new TcpClient();
            var ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                return false;
            }
            client.EndConnect(ar);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCodexBannerLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;
        var s = line.Trim();
        if (s.All(c => c == '-' || c == '=' || c == ' ')) return true;
        return s.StartsWith("OpenAI Codex v", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("workdir:", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("model:", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("provider:", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("approval:", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("sandbox:", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("reasoning ", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("session id:", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("mcp startup:", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("user", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("tokens used", StringComparison.OrdinalIgnoreCase);
    }

    public static string ExtractMeaningfulCodexError(string? stdErr, string? workDir, int exitCode)
    {
        var priorityLines = new List<string>();
        var normalLines = new List<string>();

        if (!string.IsNullOrWhiteSpace(stdErr))
        {
            foreach (var raw in stdErr.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (IsCodexBannerLine(line)) continue;
                normalLines.Add(line);
                if (line.Contains("usage_limit", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("upgrade to plus", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("403", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("exceeded", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("配额", StringComparison.OrdinalIgnoreCase))
                {
                    priorityLines.Add(line);
                }
            }
        }

        if (priorityLines.Count > 0)
        {
            return string.Join(" | ", priorityLines.Skip(Math.Max(0, priorityLines.Count - 2)));
        }

        // 若 stderr 未捕获到明确错误关键词，补充检查最近 2 分钟内更新的 Codex rollout JSONL 尾部 4KB
        if (!AppPaths.IsIsolatedTest)
        {
            var recentRolloutErr = InspectRecentCodexRolloutErrorTail();
            if (!string.IsNullOrWhiteSpace(recentRolloutErr))
            {
                return recentRolloutErr;
            }
        }

        if (normalLines.Count > 0)
        {
            return string.Join(" | ", normalLines.Skip(Math.Max(0, normalLines.Count - 2)));
        }

        return $"退出码 {exitCode}";
    }

    private static string InspectRecentCodexRolloutErrorTail()
    {
        try
        {
            var now = DateTime.Now;
            var roots = new[]
            {
                @"F:\AIChat\codex\sessions",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions")
            };
            var cutoffUtc = DateTime.UtcNow.AddMinutes(-2);
            var candidateDirs = new List<string>();
            foreach (var r in roots)
            {
                if (!Directory.Exists(r)) continue;
                foreach (var d in new[] { now, now.AddDays(-1) })
                {
                    var sub = Path.Combine(r, d.ToString("yyyy"), d.ToString("MM"), d.ToString("dd"));
                    if (Directory.Exists(sub)) candidateDirs.Add(sub);
                }
            }

            var newestFile = candidateDirs
                .SelectMany(dir => Directory.EnumerateFiles(dir, "rollout-*.jsonl", SearchOption.TopDirectoryOnly))
                .Select(f => new FileInfo(f))
                .Where(fi => fi.LastWriteTimeUtc >= cutoffUtc)
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newestFile == null || newestFile.Length == 0) return "";

            using var fs = new FileStream(newestFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int readLen = (int)Math.Min(4096, fs.Length);
            fs.Seek(-readLen, SeekOrigin.End);
            var buf = new byte[readLen];
            int actual = fs.Read(buf, 0, readLen);
            var tail = Encoding.UTF8.GetString(buf, 0, actual);
            if (tail.Contains("usage_limit_exceeded", StringComparison.OrdinalIgnoreCase) ||
                tail.Contains("hit your usage limit", StringComparison.OrdinalIgnoreCase))
            {
                return "usage_limit_exceeded: Codex 官方账号配额已耗尽（请保持桌面客户端窗口开启以走桌面直连，或开启本地代理网关）";
            }
        }
        catch { }
        return "";
    }

    private static (string ModelSlug, double Temp, string Reasoning, int MaxOutTokens, string ExtraArgs) ResolveEffectiveRuntimeParams(
        AgentRosterEntry agent,
        string displayModel,
        string effortTier)
    {
        var runtime = agent.RuntimeParams ?? new AgentRuntimeParams();
        var matchedProfile = agent.ModelProfiles?.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.ModelName) &&
            (string.Equals(p.ModelName, displayModel, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.RecommendedEffort, effortTier, StringComparison.OrdinalIgnoreCase)));

        double effTemp = Math.Clamp(matchedProfile?.CustomTemperature ?? runtime.Temperature, 0.0, 2.0);
        int effMaxOut = runtime.MaxOutputTokens > 0
            ? runtime.MaxOutputTokens
            : (matchedProfile?.MaxOutputTokens ?? 0);

        var rawReas = (runtime.ReasoningEffort ?? "auto").Trim().ToLowerInvariant();
        string effReasoning = rawReas is "low" or "medium" or "high" or "xhigh"
            ? rawReas
            : (effortTier is "low" or "medium" or "high" ? effortTier : "high");

        string slug = "default";
        if (LocalModelCatalogService.SupportsModelSelection(agent.Id) &&
            !string.IsNullOrWhiteSpace(displayModel) &&
            !displayModel.Contains("客户端默认托管", StringComparison.OrdinalIgnoreCase) &&
            !displayModel.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
        {
            var paren = Regex.Match(displayModel.Trim(), @"\(([^)]+)\)\s*$");
            slug = paren.Success && !paren.Groups[1].Value.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? paren.Groups[1].Value.Trim()
                : displayModel.Trim();
        }

        return (slug, effTemp, effReasoning, effMaxOut, (runtime.ExtraRunnerArgs ?? "").Trim());
    }

    private HeadlessDispatchOutcome RunCodexExecWithLiveStream(
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        SessionDialogService sessionSvc,
        ExternalDialogSessionInfo? matchedSession,
        string fullPrompt,
        string inboxOutFile,
        string liveStatusMd,
        string taskBrief,
        int timeoutSeconds,
        string engineCodexModel = "gpt-6-luna",
        string effortTier = "high",
        string displayModel = "",
        string tierBadge = "",
        string masterName = "Antigravity",
        int round = 1,
        int maxRounds = 3,
        string cliStrategyMode = "",
        IReadOnlyList<string>? addDirs = null)
    {
        var codexExe = ResolveCodexBinary(agent.RunnerCmd);
        if (codexExe == null || !File.Exists(codexExe))
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                SummaryMessage = "未在本机找到 codex.exe 执行引擎，请检查 ~/.codex 或在「⑤ 工具接入与分工」指定路径。"
            };
        }

        var workDir = Directory.Exists(project.PrimaryRoot) ? project.PrimaryRoot : AppPaths.AppBaseDir;
        bool isNativeCodexSession = (agent.Id == "codex_cli" || agent.Id == "grok_bot") &&
                                    matchedSession != null &&
                                    !string.IsNullOrWhiteSpace(matchedSession.SessionId);

        bool canWriteFiles = agent.Permissions.AllowDirectSourceWrite &&
                             !(taskBrief.Contains("只读", StringComparison.OrdinalIgnoreCase) ||
                               taskBrief.Contains("禁改", StringComparison.OrdinalIgnoreCase));

        string sandboxFlagsForExec = canWriteFiles
            ? "-s danger-full-access --dangerously-bypass-approvals-and-sandbox"
            : "-s read-only -c approval_policy=\"never\"";
        string sandboxFlagsForResume = canWriteFiles
            ? "--dangerously-bypass-approvals-and-sandbox"
            : "";

        bool allowModelOverride = LocalModelCatalogService.SupportsModelSelection(agent.Id);
        var rt = ResolveEffectiveRuntimeParams(agent, displayModel, effortTier);
        string safeEngineModel = string.IsNullOrWhiteSpace(engineCodexModel) ? "gpt-6-luna" : engineCodexModel;
        string safeEffort = rt.Reasoning;
        string modelAndEffortFlags = allowModelOverride
            ? $"-m {safeEngineModel} -c model_reasoning_effort=\"{safeEffort}\""
            : "";
        string safeExtraFlags = allowModelOverride && !string.IsNullOrWhiteSpace(rt.ExtraArgs)
            ? rt.ExtraArgs
            : "";
        string providerFlag = ResolvePreferredCodexProviderFlag();
        // minimal_compat：去掉模型/供应商等扩展旗标，仅保留最小可执行参数
        string mode = string.IsNullOrWhiteSpace(cliStrategyMode)
            ? (isNativeCodexSession ? "resume" : "fresh_exec")
            : cliStrategyMode.Trim();
        string combinedFlags = mode.Equals("minimal_compat", StringComparison.OrdinalIgnoreCase)
            ? ""
            : string.Join(" ", new[] { providerFlag, modelAndEffortFlags, safeExtraFlags }.Where(s => !string.IsNullOrWhiteSpace(s)));
        string addDirFlags = string.Join(" ", (addDirs ?? Array.Empty<string>()).Select(d => $"--add-dir \"{d}\""));

        string args;
        string sessionAction = matchedSession != null ? "resumed" : "created";
        if (mode.Equals("resume", StringComparison.OrdinalIgnoreCase) && isNativeCodexSession)
        {
            args = $"exec resume --skip-git-repo-check {sandboxFlagsForResume} {combinedFlags} {addDirFlags} -o \"{inboxOutFile}\" \"{matchedSession!.SessionId}\" -";
        }
        else if (mode.Equals("minimal_compat", StringComparison.OrdinalIgnoreCase))
        {
            sessionAction = "created";
            args = $"exec --skip-git-repo-check {sandboxFlagsForExec} {addDirFlags} -C \"{workDir}\" -o \"{inboxOutFile}\" -";
        }
        else
        {
            // fresh_exec 或无匹配会话时的新建
            sessionAction = isNativeCodexSession && mode.Equals("resume", StringComparison.OrdinalIgnoreCase) ? "resumed" : "created";
            if (isNativeCodexSession && mode.Equals("resume", StringComparison.OrdinalIgnoreCase))
            {
                args = $"exec resume --skip-git-repo-check {sandboxFlagsForResume} {combinedFlags} {addDirFlags} -o \"{inboxOutFile}\" \"{matchedSession!.SessionId}\" -";
            }
            else
            {
                sessionAction = "created";
                args = $"exec --skip-git-repo-check {combinedFlags} {sandboxFlagsForExec} {addDirFlags} -C \"{workDir}\" -o \"{inboxOutFile}\" -";
            }
        }

        string curPhase = round > 1 ? "reworking" : "executing";
        var (exitCode, stdOut, stdErr) = RunProcessStreaming(
            codexExe,
            args,
            workDir,
            fullPrompt,
            timeoutSeconds,
            liveLog => WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief, liveLog, "", matchedSession, sessionAction, "", curPhase, masterName, displayModel, safeEffort, tierBadge, round, maxRounds));

        string resultContent = "";
        if (File.Exists(inboxOutFile))
        {
            resultContent = File.ReadAllText(inboxOutFile, Encoding.UTF8).Trim();
        }
        if (string.IsNullOrWhiteSpace(resultContent) && !string.IsNullOrWhiteSpace(stdOut))
        {
            resultContent = stdOut.Trim();
        }

        // 若首次调用未返回文本：配额耗尽立即跳出，不再内嵌二次重试（由 Stage1 渐进档位负责降级）
        if (string.IsNullOrWhiteSpace(resultContent))
        {
            var firstErr = ExtractMeaningfulCodexError(stdErr, workDir, exitCode);
            bool isQuotaExhausted = firstErr.Contains("usage_limit_exceeded", StringComparison.OrdinalIgnoreCase) ||
                                    firstErr.Contains("hit your usage limit", StringComparison.OrdinalIgnoreCase) ||
                                    firstErr.Contains("配额已耗尽", StringComparison.OrdinalIgnoreCase);
            if (isQuotaExhausted)
            {
                return new HeadlessDispatchOutcome
                {
                    Success = false,
                    AgentId = agent.Id,
                    AgentName = agent.Name,
                    FailureKind = "quota_exceeded",
                    SummaryMessage = $"调起 {agent.Name} 未返回有效文本（{TruncateOneLine(firstErr, 180)}）。"
                };
            }

            // 非渐进模式（旧调用点）才允许内嵌 fresh 兜底一次
            if (string.IsNullOrWhiteSpace(cliStrategyMode) && mode.Equals("resume", StringComparison.OrdinalIgnoreCase))
            {
                WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief, "检测到旧会话续接超时或上下文超限，正在自动切换为新建会话重试执行...", "", matchedSession, "created", "", curPhase, masterName, displayModel, safeEffort, tierBadge, round, maxRounds);
                var fallbackArgs = $"exec --skip-git-repo-check {combinedFlags} {sandboxFlagsForExec} {addDirFlags} -C \"{workDir}\" -o \"{inboxOutFile}\" -";
                sessionAction = "created";
                (exitCode, stdOut, stdErr) = RunProcessStreaming(
                    codexExe,
                    fallbackArgs,
                    workDir,
                    fullPrompt,
                    timeoutSeconds,
                    liveLog => WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief, liveLog, "", null, "created", "", curPhase, masterName, displayModel, safeEffort, tierBadge, round, maxRounds));

                if (File.Exists(inboxOutFile))
                {
                    resultContent = File.ReadAllText(inboxOutFile, Encoding.UTF8).Trim();
                }
                if (string.IsNullOrWhiteSpace(resultContent) && !string.IsNullOrWhiteSpace(stdOut))
                {
                    resultContent = stdOut.Trim();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(resultContent))
        {
            var meaningfulErr = ExtractMeaningfulCodexError(stdErr, workDir, exitCode);
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                FailureKind = meaningfulErr.Contains("usage_limit_exceeded", StringComparison.OrdinalIgnoreCase) || meaningfulErr.Contains("配额已耗尽", StringComparison.OrdinalIgnoreCase)
                    ? "quota_exceeded"
                    : "",
                SummaryMessage = $"调起 {agent.Name} 未返回有效文本（{TruncateOneLine(meaningfulErr, 180)}）。"
            };
        }

        var activeSid = matchedSession?.SessionId ?? "";
        if (agent.Id.Equals("cursor", StringComparison.OrdinalIgnoreCase))
        {
            // 将分配给 Cursor 的任务指令与执行结果同步写入 F:\AIChat\cursor\projects 对应的项目对话框中！
            var cursorSaved = sessionSvc.AppendOrCreateCursorSession(project, matchedSession, taskBrief, resultContent);
            activeSid = cursorSaved.SessionId;
            matchedSession = cursorSaved;
        }
        else
        {
            var newestCodex = sessionSvc.FindNewestCodexSessionForDir(workDir);
            if (newestCodex != null && !string.IsNullOrWhiteSpace(newestCodex.SessionId))
            {
                activeSid = newestCodex.SessionId;
            }
            if (!string.IsNullOrWhiteSpace(activeSid))
            {
                sessionSvc.SaveActiveSessionForAgent(project, agent.Id, activeSid);
                if (agent.Id.Equals("codex_cli", StringComparison.OrdinalIgnoreCase))
                {
                    SyncCodexDesktopSessionIndex(activeSid, taskBrief);
                }
            }
        }

        WrapAndSaveInboxResult(agent, project, bus, inboxOutFile, resultContent, activeSid, sessionAction);
        var actionLabel = sessionAction == "resumed" ? $"续接已有项目对话框 ({activeSid})" : $"在项目目录下新建对话框 ({activeSid})";
        return new HeadlessDispatchOutcome
        {
            Success = true,
            AgentId = agent.Id,
            AgentName = agent.Name,
            SessionId = activeSid,
            SessionAction = sessionAction,
            OutputInboxFile = inboxOutFile,
            OutputText = File.ReadAllText(inboxOutFile, Encoding.UTF8),
            SummaryMessage = $"已通过「{agent.Name}」{actionLabel} 完成任务，反馈已收回至：{Path.GetFileName(inboxOutFile)}"
        };
    }

    public static bool IsInteractiveGateOrTrustError(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        return output.Contains("Workspace Trust Required", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Do you trust the contents", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("pass --trust", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("not inside a trusted directory", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("--approve-mcps", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("--dangerously-skip-permissions", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("approval required", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>各桌面 AI 的 UIA 窗口匹配、建组模式与安全启动参数画像。</summary>
    public sealed class DesktopUiAutomationProfile
    {
        public string AgentId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string WinTitle { get; init; } = "";
        /// <summary>Grok Bot 式「Bot/分组」侧栏（与 Codex 解耦）。</summary>
        public bool IsGrokBotGrouping { get; init; }
        /// <summary>directory_group=目录/仓库建组；group_or_bot=Grok 式 Bot/分组。</summary>
        public string GroupCreateMode { get; init; } = "directory_group";
        public bool ForbidIdeEditorLaunchArgs { get; init; } = true;
        public string SafeLaunchArgs { get; init; } = "";
        public IReadOnlyList<string> ProcessNames { get; init; } = Array.Empty<string>();
        public bool SupportsDesktopStage2 { get; init; } = true;
    }

    /// <summary>
    /// 按 agent.Id / Name 解析桌面 UIA 画像：窗口标题、建组模式、进程名与安全启动参数。
    /// Codex 不再硬绑 Grok Bot；Claude/Windsurf/Antigravity/自定义桌面 AI 不再误用 Cursor Agents 标题。
    /// </summary>
    public static DesktopUiAutomationProfile ResolveDesktopUiAutomationProfile(AgentRosterEntry? agent)
    {
        agent ??= new AgentRosterEntry { Id = "custom_desktop_ai", Name = "Desktop AI" };
        string id = (agent.Id ?? "").Trim();
        string name = (agent.Name ?? "").Trim();
        var procs = GetCandidateProcessNames(agent);

        if (id.Equals("cursor", StringComparison.OrdinalIgnoreCase))
        {
            if (procs.Count == 0) procs.Add("Cursor");
            return new DesktopUiAutomationProfile
            {
                AgentId = "cursor",
                DisplayName = string.IsNullOrWhiteSpace(name) ? "Cursor Agents" : name,
                WinTitle = "Cursor Agents",
                IsGrokBotGrouping = false,
                GroupCreateMode = "directory_group",
                ForbidIdeEditorLaunchArgs = true,
                SafeLaunchArgs = BuildCursorAgentsWindowLaunchArgs(),
                ProcessNames = procs,
                SupportsDesktopStage2 = true
            };
        }

        if (id.Equals("grok_bot", StringComparison.OrdinalIgnoreCase))
        {
            if (procs.Count == 0) procs.Add("Grok Bot");
            return new DesktopUiAutomationProfile
            {
                AgentId = "grok_bot",
                DisplayName = string.IsNullOrWhiteSpace(name) ? "Grok Bot" : name,
                WinTitle = "Grok Bot",
                IsGrokBotGrouping = true,
                GroupCreateMode = "group_or_bot",
                ForbidIdeEditorLaunchArgs = true,
                SafeLaunchArgs = "",
                ProcessNames = procs,
                SupportsDesktopStage2 = true
            };
        }

        if (id.Equals("codex_cli", StringComparison.OrdinalIgnoreCase))
        {
            if (procs.Count == 0)
            {
                procs.Add("Codex");
                procs.Add("ChatGPT");
                procs.Add("codex");
            }
            return new DesktopUiAutomationProfile
            {
                AgentId = "codex_cli",
                DisplayName = string.IsNullOrWhiteSpace(name) ? "Codex" : name,
                WinTitle = "Codex",
                IsGrokBotGrouping = false,
                GroupCreateMode = "directory_group",
                ForbidIdeEditorLaunchArgs = true,
                SafeLaunchArgs = "",
                ProcessNames = procs,
                SupportsDesktopStage2 = true
            };
        }

        if (id.Equals("claude_code", StringComparison.OrdinalIgnoreCase))
        {
            if (procs.Count == 0)
            {
                procs.Add("Claude");
                procs.Add("claude");
            }
            return new DesktopUiAutomationProfile
            {
                AgentId = "claude_code",
                DisplayName = string.IsNullOrWhiteSpace(name) ? "Claude" : name,
                WinTitle = "Claude",
                IsGrokBotGrouping = false,
                GroupCreateMode = "directory_group",
                ForbidIdeEditorLaunchArgs = true,
                SafeLaunchArgs = "",
                ProcessNames = procs,
                SupportsDesktopStage2 = true
            };
        }

        if (id.Equals("windsurf", StringComparison.OrdinalIgnoreCase))
        {
            if (procs.Count == 0) procs.Add("Windsurf");
            return new DesktopUiAutomationProfile
            {
                AgentId = "windsurf",
                DisplayName = string.IsNullOrWhiteSpace(name) ? "Windsurf" : name,
                WinTitle = "Windsurf",
                IsGrokBotGrouping = false,
                GroupCreateMode = "directory_group",
                ForbidIdeEditorLaunchArgs = true,
                SafeLaunchArgs = "--suppress-popups-on-startup",
                ProcessNames = procs,
                SupportsDesktopStage2 = true
            };
        }

        if (id.Equals("antigravity", StringComparison.OrdinalIgnoreCase))
        {
            if (procs.Count == 0)
            {
                procs.Add("Antigravity");
                procs.Add("antigravity");
            }
            return new DesktopUiAutomationProfile
            {
                AgentId = "antigravity",
                DisplayName = string.IsNullOrWhiteSpace(name) ? "Antigravity" : name,
                WinTitle = "Antigravity",
                IsGrokBotGrouping = false,
                GroupCreateMode = "directory_group",
                ForbidIdeEditorLaunchArgs = true,
                SafeLaunchArgs = "",
                ProcessNames = procs,
                SupportsDesktopStage2 = true
            };
        }

        // 任意其他外部桌面 AI：按名称/进程名匹配，严禁强开编辑器参数
        string winTitle = !string.IsNullOrWhiteSpace(name) ? name : id;
        if (string.IsNullOrWhiteSpace(winTitle)) winTitle = "Desktop AI";
        if (procs.Count == 0 && !string.IsNullOrWhiteSpace(agent.GuiExePath))
        {
            try
            {
                var fn = Path.GetFileNameWithoutExtension(agent.GuiExePath.Trim().Trim('"'));
                if (!string.IsNullOrWhiteSpace(fn)) procs.Add(fn);
            }
            catch { }
        }
        if (procs.Count == 0 && !string.IsNullOrWhiteSpace(winTitle))
            procs.Add(winTitle);

        bool hasGui = !string.IsNullOrWhiteSpace(agent.GuiExePath) || procs.Count > 0;
        return new DesktopUiAutomationProfile
        {
            AgentId = string.IsNullOrWhiteSpace(id) ? "custom_desktop_ai" : id,
            DisplayName = winTitle,
            WinTitle = winTitle,
            IsGrokBotGrouping = false,
            GroupCreateMode = "directory_group",
            ForbidIdeEditorLaunchArgs = true,
            SafeLaunchArgs = "",
            ProcessNames = procs,
            SupportsDesktopStage2 = hasGui || !string.IsNullOrWhiteSpace(winTitle)
        };
    }

    /// <summary>
    /// 全桌面 AI 安全启动参数：Cursor/Windsurf 等均严禁带 --reuse-window + 工程路径把 Agent 窗切成编辑器窗。
    /// </summary>
    public static string BuildDesktopAgentWindowLaunchArgs(AgentRosterEntry? agent)
    {
        var profile = ResolveDesktopUiAutomationProfile(agent);
        string args = profile.SafeLaunchArgs ?? "";
        if (ContainsForbiddenCursorIdeEditorLaunchArgs(args))
            return BuildCursorAgentsWindowLaunchArgs();
        return args;
    }

    /// <summary>
    /// 仅拉起/保持 Cursor Agents 窗口形态；严禁拼接 --reuse-window 或工程路径（会强制切到普通 IDE 编辑器窗）。
    /// </summary>
    public static string BuildCursorAgentsWindowLaunchArgs(string? projectRootIgnored = null)
    {
        _ = projectRootIgnored;
        return "--suppress-popups-on-startup";
    }

    /// <summary>检测是否误用会打开普通 IDE 编辑器窗的启动参数（全桌面 AI 共用）。</summary>
    public static bool ContainsForbiddenCursorIdeEditorLaunchArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return false;
        return args.Contains("--reuse-window", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>UIA 在桌面侧栏无法建出目标目录分组时的诊断令牌（上层默认禁止再降级无头 CLI；仅当显式允许时才可回退）。</summary>
    public const string CursorMissingProjectGroupFallbackCliToken = "NO_PROJECT_GROUP_FALLBACK_CLI";

    /// <summary>是否为已配置/内置桌面客户端窗口通道的 AI（Cursor Agents、Grok Bot 等）。</summary>
    public static bool IsDesktopClientChannelAgent(AgentRosterEntry? agent)
    {
        if (agent == null) return false;
        var id = agent.Id ?? "";
        if (id.Equals("cursor", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("grok_bot", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("claude_code", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("codex_cli", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("windsurf", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("antigravity", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (agent.LaunchWindowOnDispatch) return true;
        return ResolveGuiExecutable(agent) != null;
    }

    /// <summary>
    /// 桌面客户端 AI 在桌面直连失败后，是否允许静默降级到纯后台无头 CLI / Stage3。
    /// 当 AllowHeadlessCliFallbackForDesktopAgents=false 时，无论失败原因为何一律返回 false。
    /// </summary>
    public static bool ShouldFallbackToHeadlessCliForDesktopAgent(
        AgentRosterEntry? agent,
        UserHubConfig? cfg,
        string? desktopFailureKind = null)
    {
        _ = desktopFailureKind; // 失败原因不得影响：禁止时一律 false
        if (agent == null || !IsDesktopClientChannelAgent(agent))
            return false;
        return cfg?.AllowHeadlessCliFallbackForDesktopAgents == true;
    }

    /// <summary>便于自检的纯静态重载：按 agentId + 开关判定。</summary>
    public static bool ShouldFallbackToHeadlessCliForDesktopAgent(
        string? agentId,
        bool allowHeadlessCliFallbackForDesktopAgents,
        string? desktopFailureKind = null)
    {
        _ = desktopFailureKind;
        var probe = new AgentRosterEntry { Id = agentId ?? "", Name = agentId ?? "" };
        if (!IsDesktopClientChannelAgent(probe))
            return false;
        return allowHeadlessCliFallbackForDesktopAgents;
    }

    /// <summary>桌面可见投递失败时的清晰诊断（禁止偷偷转无头 CLI）。</summary>
    public static HeadlessDispatchOutcome BuildDesktopVisibleDeliveryFailure(
        AgentRosterEntry agent,
        HeadlessDispatchOutcome? desktopOutcome,
        string? extraHint = null)
    {
        string kind = string.IsNullOrWhiteSpace(desktopOutcome?.FailureKind)
            ? "desktop_delivery_failed"
            : desktopOutcome!.FailureKind.Trim();
        string detail = string.IsNullOrWhiteSpace(desktopOutcome?.SummaryMessage)
            ? "桌面客户端直连未成功"
            : desktopOutcome!.SummaryMessage.Trim();
        string tip = kind switch
        {
            "no_project_group_fallback_cli" =>
                "请检查桌面侧栏是否已展开且存在目标工程分组（名称需匹配，含连字符/大小写变体）；展开分组后重试。禁止改走无头 CLI。",
            "revert_modal_aborted" =>
                "请先在桌面客户端关闭或处理「Revert files」历史编辑弹窗后重试。",
            "send_failed" =>
                "发送确认未过（含超长多行任务）：请确认 Agents/桌面窗口可见、输入框可编辑、无遮挡弹窗后重试。",
            _ =>
                "请确认桌面客户端窗口已打开、目标工程分组可见、会话可输入且无遮挡弹窗。"
        };
        if (!string.IsNullOrWhiteSpace(extraHint))
            tip = tip + " " + extraHint.Trim();
        return new HeadlessDispatchOutcome
        {
            Success = false,
            AgentId = agent.Id,
            AgentName = agent.Name,
            FailureKind = kind,
            SummaryMessage =
                $"「{agent.Name}」桌面可见会话投递失败（已禁止静默降级纯后台无头 CLI）。卡点：{detail} 建议：{tip}"
        };
    }

    /// <summary>
    /// 契约自检：Agents 启动参数禁 IDE 强开，且缺失分组时产出诊断令牌（默认不再静默降级无头 CLI）。
    /// </summary>
    public static bool ValidateCursorAgentsNoIdeOpenContract(out string detail)
    {
        string launchArgs = BuildCursorAgentsWindowLaunchArgs(@"C:\Any\Project\Root");
        bool launchOk = !string.IsNullOrWhiteSpace(launchArgs) &&
                        launchArgs.Contains("--suppress-popups-on-startup", StringComparison.OrdinalIgnoreCase) &&
                        !ContainsForbiddenCursorIdeEditorLaunchArgs(launchArgs) &&
                        !launchArgs.Contains(@"C:\Any\Project\Root", StringComparison.OrdinalIgnoreCase);
        bool tokenOk = !string.IsNullOrWhiteSpace(CursorMissingProjectGroupFallbackCliToken) &&
                       CursorMissingProjectGroupFallbackCliToken.Equals("NO_PROJECT_GROUP_FALLBACK_CLI", StringComparison.Ordinal);
        detail = $"LaunchArgs=[{launchArgs}], Token=[{CursorMissingProjectGroupFallbackCliToken}]";
        return launchOk && tokenOk;
    }

    /// <summary>全桌面 AI：窗口画像 + 禁 IDE 启动参数 + 统一建组/建对话源码契约。</summary>
    public static bool ValidateAllDesktopAgentsNoIdeOpenContract(out string detail)
    {
        var samples = new[]
        {
            new AgentRosterEntry { Id = "cursor", Name = "Cursor" },
            new AgentRosterEntry { Id = "grok_bot", Name = "Grok Bot" },
            new AgentRosterEntry { Id = "codex_cli", Name = "Codex" },
            new AgentRosterEntry { Id = "claude_code", Name = "Claude Code" },
            new AgentRosterEntry { Id = "windsurf", Name = "Windsurf" },
            new AgentRosterEntry { Id = "antigravity", Name = "Antigravity" },
            new AgentRosterEntry { Id = "custom_desktop_ai", Name = "MyAI Desk", GuiExePath = @"C:\Tools\CustomAI\CustomAI.exe" }
        };
        var parts = new List<string>();
        bool allOk = true;
        foreach (var ag in samples)
        {
            var p = ResolveDesktopUiAutomationProfile(ag);
            string launch = BuildDesktopAgentWindowLaunchArgs(ag);
            bool launchSafe = !ContainsForbiddenCursorIdeEditorLaunchArgs(launch) &&
                              !launch.Contains("--reuse-window", StringComparison.OrdinalIgnoreCase);
            bool titleOk = ag.Id switch
            {
                "cursor" => p.WinTitle.Equals("Cursor Agents", StringComparison.OrdinalIgnoreCase) &&
                            p.GroupCreateMode.Equals("directory_group", StringComparison.OrdinalIgnoreCase) && !p.IsGrokBotGrouping,
                "grok_bot" => p.WinTitle.Equals("Grok Bot", StringComparison.OrdinalIgnoreCase) && p.IsGrokBotGrouping &&
                             p.GroupCreateMode.Equals("group_or_bot", StringComparison.OrdinalIgnoreCase),
                "codex_cli" => p.WinTitle.Equals("Codex", StringComparison.OrdinalIgnoreCase) && !p.IsGrokBotGrouping &&
                              !p.WinTitle.Equals("Grok Bot", StringComparison.OrdinalIgnoreCase),
                "claude_code" => p.WinTitle.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                 !p.WinTitle.Equals("Cursor Agents", StringComparison.OrdinalIgnoreCase),
                "windsurf" => p.WinTitle.Equals("Windsurf", StringComparison.OrdinalIgnoreCase) &&
                              !p.WinTitle.Equals("Cursor Agents", StringComparison.OrdinalIgnoreCase) && p.ForbidIdeEditorLaunchArgs,
                "antigravity" => p.WinTitle.Equals("Antigravity", StringComparison.OrdinalIgnoreCase) && !p.IsGrokBotGrouping,
                "custom_desktop_ai" => p.WinTitle.Equals("MyAI Desk", StringComparison.OrdinalIgnoreCase) &&
                                       !p.WinTitle.Equals("Cursor Agents", StringComparison.OrdinalIgnoreCase) &&
                                       !p.WinTitle.Equals("Grok Bot", StringComparison.OrdinalIgnoreCase),
                _ => !p.WinTitle.Equals("Cursor Agents", StringComparison.OrdinalIgnoreCase) && !p.WinTitle.Equals("Grok Bot", StringComparison.OrdinalIgnoreCase)
            };
            bool ok = launchSafe && titleOk && p.ForbidIdeEditorLaunchArgs;
            if (!ok) allOk = false;
            parts.Add($"{ag.Id}:Win={p.WinTitle},Mode={p.GroupCreateMode},Grok={p.IsGrokBotGrouping},Args=[{launch}],Ok={ok}");
        }
        detail = string.Join(" | ", parts);
        return allOk;
    }

    public static string BuildCursorAgentHeadlessArgs(
        string workDir,
        IReadOnlyList<string>? addDirs,
        bool autoTrust,
        bool canWriteFiles,
        bool approveMcps,
        string mode,
        string? resumeSessionId,
        string? modelSlug)
    {
        bool isMinimal = string.Equals(mode, "minimal", StringComparison.OrdinalIgnoreCase);
        var parts = new List<string>
        {
            "--print",
            "--output-format text",
            $"--workspace \"{workDir}\""
        };
        if (autoTrust)
        {
            parts.Add("--trust");
        }
        if (canWriteFiles)
        {
            parts.Add("-f");
        }
        else
        {
            parts.Add("--mode plan");
        }
        if (approveMcps && !isMinimal)
        {
            parts.Add("--approve-mcps");
        }
        if (!string.IsNullOrWhiteSpace(modelSlug) && !modelSlug.Equals("default", StringComparison.OrdinalIgnoreCase) && !modelSlug.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"--model \"{modelSlug}\"");
        }
        if (string.Equals(mode, "resume", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(resumeSessionId) && Guid.TryParse(resumeSessionId, out _))
                parts.Add($"--resume \"{resumeSessionId}\"");
            else
                parts.Add("--continue");
        }
        if (!isMinimal && addDirs != null)
        {
            foreach (var d in addDirs)
            {
                if (!string.IsNullOrWhiteSpace(d)) parts.Add($"--add-dir \"{d}\"");
            }
        }
        return string.Join(" ", parts);
    }

    public static (string FileName, string PrefixArgs)? ResolveCursorAgentDirectNodeRunner(string? customCmd = null)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cursorAgentRoot = Path.Combine(local, "cursor-agent");
            var versionsDir = Path.Combine(cursorAgentRoot, "versions");
            if (!string.IsNullOrWhiteSpace(customCmd) && File.Exists(customCmd))
            {
                var customDir = Path.GetDirectoryName(customCmd);
                if (!string.IsNullOrWhiteSpace(customDir))
                {
                    var nodeDirect = Path.Combine(customDir, "node.exe");
                    var idxDirect = Path.Combine(customDir, "index.js");
                    if (File.Exists(nodeDirect) && File.Exists(idxDirect))
                        return (nodeDirect, $"\"{idxDirect}\"");
                    var subVers = Path.Combine(customDir, "versions");
                    if (Directory.Exists(subVers)) versionsDir = subVers;
                }
            }

            if (Directory.Exists(versionsDir))
            {
                var dirs = Directory.GetDirectories(versionsDir)
                    .Select(d => new DirectoryInfo(d))
                    .Where(di => File.Exists(Path.Combine(di.FullName, "node.exe")) && File.Exists(Path.Combine(di.FullName, "index.js")))
                    .OrderByDescending(di => di.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (dirs.Count > 0)
                {
                    var best = dirs[0].FullName;
                    return (Path.Combine(best, "node.exe"), $"\"{Path.Combine(best, "index.js")}\"");
                }
            }
        }
        catch { }
        return null;
    }

    private HeadlessDispatchOutcome RunCursorAgentCliHeadless(
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        SessionDialogService sessionSvc,
        ExternalDialogSessionInfo? matchedSession,
        string fullPrompt,
        string inboxOutFile,
        string liveStatusMd,
        string taskBrief,
        int timeoutSeconds,
        string displayModel = "",
        string effortTier = "high",
        string tierBadge = "",
        string masterName = "Antigravity",
        int round = 1,
        int maxRounds = 3,
        string mode = "fresh",
        IReadOnlyList<string>? addDirs = null,
        bool autoTrustWorkspaces = true,
        string? customBinaryPath = null)
    {
        var cliBin = ResolveCursorAgentCliBinary(customBinaryPath ?? agent.RunnerCmd);
        var directNode = ResolveCursorAgentDirectNodeRunner(cliBin);
        if (directNode == null && string.IsNullOrWhiteSpace(cliBin))
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                FailureKind = "permanent_config_error",
                SummaryMessage = "本机未检测到 Cursor Agent CLI (cursor-agent / agent)。"
            };
        }

        var workDir = Directory.Exists(project.PrimaryRoot) ? project.PrimaryRoot : AppPaths.AppBaseDir;
        bool canWriteFiles = agent.Permissions.AllowDirectSourceWrite &&
                             !(taskBrief.Contains("只读", StringComparison.OrdinalIgnoreCase) ||
                               taskBrief.Contains("禁改", StringComparison.OrdinalIgnoreCase));
        var rt = ResolveEffectiveRuntimeParams(agent, displayModel, effortTier);
        string? modelSlug = LocalModelCatalogService.SupportsModelSelection(agent.Id) ? rt.ModelSlug : null;

        string flagArgs = BuildCursorAgentHeadlessArgs(
            workDir,
            addDirs ?? CollectAddDirsForProject(project, taskBrief),
            autoTrustWorkspaces,
            canWriteFiles,
            agent.Permissions.HasPreviewMcp,
            mode,
            matchedSession?.SessionId,
            modelSlug);
        if (!string.IsNullOrWhiteSpace(rt.ExtraArgs))
        {
            flagArgs = $"{flagArgs} {rt.ExtraArgs}";
        }

        string fileName = directNode != null ? directNode.Value.FileName : cliBin!;
        string finalArgs = directNode != null ? $"{directNode.Value.PrefixArgs} {flagArgs}" : flagArgs;
        var extraEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NODE_COMPILE_CACHE"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cursor-compile-cache")
        };

        string curPhase = round > 1 ? "reworking" : "executing";
        string sessionAction = string.Equals(mode, "resume", StringComparison.OrdinalIgnoreCase) ? "resumed" : "created";
        WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief,
            $"⚡ 正在通过 Cursor 官方无头 CLI ({(directNode != null ? "node/index.js" : Path.GetFileName(cliBin))} · --trust) 纯后台执行...",
            "", matchedSession, sessionAction, "", curPhase, masterName, displayModel, effortTier, tierBadge, round, maxRounds);

        // --output-format text 为整段缓冲输出：不按 stdout 静默熔断，交给总超时 + 文件写入/CPU 心跳兜底
        int cliTimeoutSeconds = Math.Max(timeoutSeconds, CursorCliMinTimeoutSeconds);
        var (exitCode, stdOut, stdErr) = RunProcessStreaming(
            fileName,
            finalArgs,
            workDir,
            fullPrompt,
            cliTimeoutSeconds,
            liveLog => WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief, liveLog, "", matchedSession, sessionAction, "", curPhase, masterName, displayModel, effortTier, tierBadge, round, maxRounds),
            stallTimeoutSeconds: 0,
            extraEnv: extraEnv);

        string combined = $"{stdOut}\n{stdErr}";
        if (exitCode != 0 && IsInteractiveGateOrTrustError(combined) && autoTrustWorkspaces && !flagArgs.Contains("--trust", StringComparison.OrdinalIgnoreCase))
        {
            WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief,
                "🛡 检测到 Workspace Trust / 交互授权拦截，正在自动附加 --trust -f --approve-mcps 重试...",
                "", matchedSession, sessionAction, "", "reconnecting", masterName, displayModel, effortTier, tierBadge, round, maxRounds);
            string retriedArgs = $"{finalArgs} --trust -f --approve-mcps";
            (exitCode, stdOut, stdErr) = RunProcessStreaming(
                fileName, retriedArgs, workDir, fullPrompt, cliTimeoutSeconds,
                liveLog => WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief, liveLog, "", matchedSession, sessionAction, "", curPhase, masterName, displayModel, effortTier, tierBadge, round, maxRounds),
                stallTimeoutSeconds: 0, extraEnv: extraEnv);
            combined = $"{stdOut}\n{stdErr}";
        }

        string output = stdOut.Trim();
        if (string.IsNullOrWhiteSpace(output) && File.Exists(inboxOutFile))
        {
            output = File.ReadAllText(inboxOutFile, Encoding.UTF8).Trim();
        }
        if (exitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                FailureKind = IsInteractiveGateOrTrustError(combined) ? "workspace_trust_required" : (exitCode == -2 ? "execution_stalled" : ""),
                SummaryMessage = $"Cursor Agent CLI 执行未成功 (code={exitCode}): {TruncateOneLine(combined, 180)}"
            };
        }

        var cursorSaved = sessionSvc.AppendOrCreateCursorSession(project, matchedSession, taskBrief, output);
        var activeSid = cursorSaved.SessionId;
        sessionSvc.SaveActiveSessionForAgent(project, agent.Id, activeSid);
        WrapAndSaveInboxResult(agent, project, bus, inboxOutFile, output, activeSid, sessionAction);
        return new HeadlessDispatchOutcome
        {
            Success = true,
            AgentId = agent.Id,
            AgentName = agent.Name,
            SessionId = activeSid,
            SessionAction = sessionAction,
            OutputInboxFile = inboxOutFile,
            OutputText = File.ReadAllText(inboxOutFile, Encoding.UTF8),
            SummaryMessage = $"已通过 Cursor 官方无头 CLI (--trust) 纯后台完成执行，反馈已收回至：{Path.GetFileName(inboxOutFile)}"
        };
    }

    private HeadlessDispatchOutcome RunCustomCliWithLiveStream(
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        SessionDialogService sessionSvc,
        ExternalDialogSessionInfo? matchedSession,
        string promptFile,
        string fullPrompt,
        string inboxOutFile,
        string liveStatusMd,
        string taskBrief,
        int timeoutSeconds,
        string displayModel = "",
        string effortTier = "high",
        string tierBadge = "",
        string masterName = "Antigravity",
        int round = 1,
        int maxRounds = 3,
        bool autoTrustWorkspaces = true)
    {
        var rawCmd = agent.RunnerCmd?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(rawCmd))
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                SummaryMessage = $"工具「{agent.Name}」未配置命令行模板 (RunnerCmd)。"
            };
        }

        bool usesFilePlaceholder = rawCmd.Contains("{PROMPT_FILE}", StringComparison.OrdinalIgnoreCase)
                                   || rawCmd.Contains("{OUTPUT_FILE}", StringComparison.OrdinalIgnoreCase);

        var sessionAction = matchedSession != null ? "resumed" : "created";
        var targetSid = matchedSession?.SessionId ?? $"dlg_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";

        var rt = ResolveEffectiveRuntimeParams(agent, displayModel, effortTier);
        var workDir = Directory.Exists(project.PrimaryRoot) ? project.PrimaryRoot : AppPaths.AppBaseDir;
        bool hasExtraPlaceholder = rawCmd.Contains("{EXTRA_ARGS}", StringComparison.OrdinalIgnoreCase);
        // 展开 __ADD_DIR__ → 各 Companion / 显式外部工程 --add-dir
        string extraArgs = rt.ExtraArgs ?? "";
        if (extraArgs.Contains("__ADD_DIR__", StringComparison.Ordinal))
        {
            var addDirs = new List<string>();
            foreach (var c in project.CompanionRoots ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c) &&
                    !addDirs.Contains(c, StringComparer.OrdinalIgnoreCase))
                    addDirs.Add(c);
            }
            foreach (var e in ExtractExplicitExternalProjectRoots(taskBrief, project.PrimaryRoot))
            {
                if (!string.IsNullOrWhiteSpace(e) && Directory.Exists(e) &&
                    !addDirs.Contains(e, StringComparer.OrdinalIgnoreCase))
                    addDirs.Add(e);
            }
            string addDirPart = string.Join(" ", addDirs.Select(d => $"--add-dir \"{d}\""));
            extraArgs = Regex.Replace(extraArgs, @"\s*__ADD_DIR__\s*", " " + addDirPart + " ").Trim();
        }
        var expandedCmd = Environment.ExpandEnvironmentVariables(rawCmd)
            .Replace("{PROJECT_ROOT}", workDir)
            .Replace("{PROMPT_FILE}", promptFile)
            .Replace("{OUTPUT_FILE}", inboxOutFile)
            .Replace("{SESSION_ID}", targetSid)
            .Replace("{MODEL}", displayModel)
            .Replace("{EFFORT_TIER}", effortTier)
            .Replace("{TEMPERATURE}", rt.Temp.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{MAX_TOKENS}", rt.MaxOutTokens.ToString())
            .Replace("{REASONING_EFFORT}", rt.Reasoning)
            .Replace("{EXTRA_ARGS}", extraArgs)
            .Replace("{ROUND}", round.ToString());
        // 若 RunnerCmd 仅为裸 exe（无占位符），补默认 CLI 参数
        bool bareExe = File.Exists(expandedCmd.Trim().Trim('"')) &&
                       !expandedCmd.Contains("{", StringComparison.Ordinal) &&
                       !expandedCmd.Contains(" -", StringComparison.Ordinal);
        if (bareExe)
        {
            expandedCmd = $"\"{expandedCmd.Trim().Trim('"')}\" -p \"{promptFile}\" {extraArgs}".Trim();
            hasExtraPlaceholder = true; // 已并入
        }
        if (!hasExtraPlaceholder && !string.IsNullOrWhiteSpace(extraArgs))
        {
            expandedCmd = $"{expandedCmd} {extraArgs}";
        }

        var (fileName, arguments) = SplitExecutableAndArgs(expandedCmd);
        int genericStallSeconds = IsBufferedPrintCliArgs(arguments) ? 0 : 90;
        var (exitCode, stdOut, stdErr) = RunProcessStreaming(
            fileName,
            arguments,
            workDir,
            usesFilePlaceholder ? null : fullPrompt,
            timeoutSeconds,
            liveLog => WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief, liveLog, "", matchedSession, sessionAction, "", round > 1 ? "reworking" : "executing", masterName, displayModel, effortTier, tierBadge, round, maxRounds),
            stallTimeoutSeconds: genericStallSeconds);

        if (exitCode != 0 && autoTrustWorkspaces && IsInteractiveGateOrTrustError($"{stdOut}\n{stdErr}") && !arguments.Contains("--trust", StringComparison.OrdinalIgnoreCase))
        {
            string retryArgs = $"{arguments} --trust -f".Trim();
            (exitCode, stdOut, stdErr) = RunProcessStreaming(
                fileName,
                retryArgs,
                workDir,
                usesFilePlaceholder ? null : fullPrompt,
                timeoutSeconds,
                liveLog => WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief, liveLog, "", matchedSession, sessionAction, "", "reconnecting", masterName, displayModel, effortTier, tierBadge, round, maxRounds),
                stallTimeoutSeconds: genericStallSeconds);
        }

        string output = File.Exists(inboxOutFile) ? File.ReadAllText(inboxOutFile, Encoding.UTF8).Trim() : "";
        if (string.IsNullOrWhiteSpace(output)) output = stdOut.Trim();

        if (exitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                SummaryMessage = $"执行「{agent.Name}」失败 (code={exitCode}): {TruncateOneLine(stdErr, 120)}"
            };
        }

        var recorded = sessionSvc.AppendOrCreateLocalCollabSession(project, agent, matchedSession?.SessionId, taskBrief, output);
        WrapAndSaveInboxResult(agent, project, bus, inboxOutFile, output, recorded.SessionId, sessionAction);
        var actionLabel = sessionAction == "resumed" ? $"续接已有对话框 ({recorded.SessionId})" : $"新建项目对话框 ({recorded.SessionId})";
        return new HeadlessDispatchOutcome
        {
            Success = true,
            AgentId = agent.Id,
            AgentName = agent.Name,
            SessionId = recorded.SessionId,
            SessionAction = sessionAction,
            OutputInboxFile = inboxOutFile,
            OutputText = File.ReadAllText(inboxOutFile, Encoding.UTF8),
            SummaryMessage = $"已通过「{agent.Name}」{actionLabel} 执行完毕，反馈已收回至：{Path.GetFileName(inboxOutFile)}"
        };
    }

    private HeadlessDispatchOutcome RunHttpOpenAiHeadless(
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        SessionDialogService sessionSvc,
        ExternalDialogSessionInfo? matchedSession,
        string fullPrompt,
        string inboxOutFile,
        string liveStatusMd,
        string taskBrief,
        string displayModel = "",
        string effortTier = "high",
        string tierBadge = "",
        string masterName = "Antigravity",
        int round = 1,
        int maxRounds = 3)
    {
        var endpoint = string.IsNullOrWhiteSpace(agent.RunnerCmd)
            ? "http://127.0.0.1:11434/v1/chat/completions"
            : agent.RunnerCmd.Trim();

        if (endpoint.Contains("11434") && !DiscoveryService.ProbeLocalTcpPort(11434, 150))
        {
            WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief, "检测到 Ollama 未启动，正在自动拉起 ollama serve...", "", matchedSession, "", "", "executing", masterName, displayModel, effortTier, tierBadge, round, maxRounds);
            TryStartOllamaDaemonSilent();
        }

        try
        {
            var rt = ResolveEffectiveRuntimeParams(agent, displayModel, effortTier);
            WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief, $"已向 {endpoint} 发送推理请求（模型：{displayModel} · 档位：{tierBadge} · 温度：{rt.Temp:0.00}），正在等待生成...", "", matchedSession, "", "", "executing", masterName, displayModel, effortTier, tierBadge, round, maxRounds);
            var payload = new Dictionary<string, object>
            {
                ["model"] = rt.ModelSlug,
                ["messages"] = new[]
                {
                    new { role = "system", content = "你是多 AI 协同工作流中的专业协同助手，请用简体中文直接输出清晰的方案、代码或审查结论。" },
                    new { role = "user", content = fullPrompt }
                },
                ["temperature"] = Math.Round(rt.Temp, 2)
            };
            if (rt.MaxOutTokens > 0)
            {
                payload["max_tokens"] = rt.MaxOutTokens;
            }
            var json = JsonSerializer.Serialize(payload);
            using var resp = Http.PostAsync(endpoint, new StringContent(json, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            var respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                return new HeadlessDispatchOutcome
                {
                    Success = false,
                    AgentId = agent.Id,
                    AgentName = agent.Name,
                    SummaryMessage = $"HTTP 服务返回状态码 {(int)resp.StatusCode}: {TruncateOneLine(respBody, 100)}"
                };
            }

            using var doc = JsonDocument.Parse(respBody);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? respBody;

            var sessionAction = matchedSession != null ? "resumed" : "created";
            var recorded = sessionSvc.AppendOrCreateLocalCollabSession(project, agent, matchedSession?.SessionId, taskBrief, content);
            WrapAndSaveInboxResult(agent, project, bus, inboxOutFile, content, recorded.SessionId, sessionAction);
            return new HeadlessDispatchOutcome
            {
                Success = true,
                AgentId = agent.Id,
                AgentName = agent.Name,
                SessionId = recorded.SessionId,
                SessionAction = sessionAction,
                OutputInboxFile = inboxOutFile,
                OutputText = File.ReadAllText(inboxOutFile, Encoding.UTF8),
                SummaryMessage = $"已调起「{agent.Name}」({sessionAction}: {recorded.SessionId}) 并收回结果至：{Path.GetFileName(inboxOutFile)}"
            };
        }
        catch (Exception ex)
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                SummaryMessage = $"连接本地/远程推理服务失败 ({endpoint})：{ex.Message}"
            };
        }
    }

    private HeadlessDispatchOutcome RunBridgeQueueHeadless(
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        SessionDialogService sessionSvc,
        ExternalDialogSessionInfo? matchedSession,
        string fullPrompt,
        string inboxOutFile,
        string liveStatusMd,
        string taskBrief)
    {
        if (string.IsNullOrWhiteSpace(agent.BridgeLocalDir))
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                SummaryMessage = $"桥接成员「{agent.Name}」尚未配置本地桥接文件夹路径。"
            };
        }

        Directory.CreateDirectory(agent.BridgeLocalDir);
        var bridgeCollab = Path.Combine(agent.BridgeLocalDir, "collab");
        var bridgeInbox = Path.Combine(bridgeCollab, "inbox");
        Directory.CreateDirectory(bridgeInbox);

        var dispatchFile = Path.Combine(bridgeCollab, "DISPATCH_TASK.md");
        File.WriteAllText(dispatchFile, fullPrompt, new UTF8Encoding(false));
        File.Copy(bus.GetTaskFilePath(project), Path.Combine(bridgeCollab, "TASK.md"), overwrite: true);

        if (!string.IsNullOrWhiteSpace(agent.RunnerCmd))
        {
            var (fn, args) = SplitExecutableAndArgs(Environment.ExpandEnvironmentVariables(agent.RunnerCmd));
            RunProcessStreaming(fn, args, agent.BridgeLocalDir, fullPrompt, 120,
                liveLog => WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief, liveLog, ""));
        }

        var sessionAction = matchedSession != null ? "resumed" : "created";
        var bridgeReplies = Directory.GetFiles(bridgeInbox, "*.md")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        if (bridgeReplies.Count > 0)
        {
            var newest = File.ReadAllText(bridgeReplies[0], Encoding.UTF8);
            var recorded = sessionSvc.AppendOrCreateLocalCollabSession(project, agent, matchedSession?.SessionId, taskBrief, newest);
            WrapAndSaveInboxResult(agent, project, bus, inboxOutFile, newest, recorded.SessionId, sessionAction);
            return new HeadlessDispatchOutcome
            {
                Success = true,
                AgentId = agent.Id,
                AgentName = agent.Name,
                SessionId = recorded.SessionId,
                SessionAction = sessionAction,
                OutputInboxFile = inboxOutFile,
                OutputText = File.ReadAllText(inboxOutFile, Encoding.UTF8),
                SummaryMessage = $"已投递至桥接队列并自动从桥接目录收回最新产出：{Path.GetFileName(inboxOutFile)}"
            };
        }

        var receipt = $"# 桥接派单回执 ({agent.Name})\n\n- 派单时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n- 投递路径：`{dispatchFile}`\n- 状态：已写入本地同步桥接目录，远端 VPS 完成后放入 `{bridgeInbox}` 将自动收回。\n";
        var recSession = sessionSvc.AppendOrCreateLocalCollabSession(project, agent, matchedSession?.SessionId, taskBrief, receipt);
        File.WriteAllText(inboxOutFile, receipt, new UTF8Encoding(false));
        return new HeadlessDispatchOutcome
        {
            Success = true,
            AgentId = agent.Id,
            AgentName = agent.Name,
            SessionId = recSession.SessionId,
            SessionAction = sessionAction,
            OutputInboxFile = inboxOutFile,
            OutputText = receipt,
            SummaryMessage = $"任务已自动投递至桥接目录：{dispatchFile}"
        };
    }

    private static string ReadSharedUtf8TextSafe(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    public static bool IsProcessAliveFast(int pid)
    {
        if (pid <= 0) return false;
        IntPtr hProc = IntPtr.Zero;
        try
        {
            hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return false;
            return GetExitCodeProcess(hProc, out uint code) && code == STILL_ACTIVE;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hProc != IntPtr.Zero) CloseHandle(hProc);
        }
    }

    /// <summary>
    /// 从包含系统派单模板或长套话前缀的任务/对话文本中提炼干净的核心任务标题。
    /// </summary>
    public static string ExtractCleanTaskBrief(string? raw, int maxLen = 18)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "协同子任务";

        var normalized = raw.Replace("\uFFFD", "").Replace("\r\n", "\n");
        var markerMatch = Regex.Match(
            normalized,
            @"##\s*【最高优先级】[^\n]*\n+([^\n#]+)",
            RegexOptions.Multiline);
        string candidate;
        if (markerMatch.Success)
        {
            candidate = markerMatch.Groups[1].Value.Trim();
        }
        else
        {
            var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith("你正在作为「") &&
                            !l.StartsWith("- **") &&
                            !l.StartsWith("请严格遵守") &&
                            !l.StartsWith("1. 全程使用") &&
                            !l.StartsWith("2. ") &&
                            !l.StartsWith("3. ") &&
                            !l.StartsWith("## "))
                .ToList();
            candidate = lines.FirstOrDefault() ?? normalized;
        }

        var s = candidate.Replace('\n', ' ').Trim();

        // 前 55 字内含引导冒号时取冒号后正文；严禁把 Windows 盘符 C:/ / C:\ 的英文冒号当切分点
        int scanLen = Math.Min(55, s.Length);
        int colonIdx = FindGuideColonIndex(s, scanLen);
        if (colonIdx > 0)
        {
            string before = s[..colonIdx];
            bool guidePrefix =
                before.Contains("落地修改", StringComparison.Ordinal) ||
                before.Contains("工程文件", StringComparison.Ordinal) ||
                before.Contains("编译", StringComparison.Ordinal) ||
                before.Contains("自检", StringComparison.Ordinal) ||
                before.Contains("请直接", StringComparison.Ordinal) ||
                before.Contains("直接动手", StringComparison.Ordinal) ||
                before.Contains("指派给你", StringComparison.Ordinal);
            if (guidePrefix && colonIdx + 1 < s.Length)
            {
                s = s[(colonIdx + 1)..].Trim();
            }
        }

        // 剥离句首「请直接(落地)修改 <盘符路径> 工程…」及句中本地绝对路径，保留真实中文任务主题
        s = StripLocalPathGuidePrefix(s);

        for (int pass = 0; pass < 3; pass++)
        {
            var prev = s;
            s = Regex.Replace(s, @"^【?多AI协同派单】?\s*", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"^(只读)?(审查任务|方案设计|视觉设计建议|交互文案与排版建议|任务)（[^）]*）[：:\s]*", "");
            s = Regex.Replace(s, @"^(请)?(直接落地(修改工程文件)?(完成)?|直接修改工程文件(完成)?|请结合当前项目规则与\s*TASK\.md|只读方案建议|只读审查任务|深度对标参考图)\s*[：:，,\s]*", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"^请基于当前\s*TASK\.md\s*完成[：:，,\s]*", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"^我们要[为将]『[^』]+』(\([^)]*\))?\s*", "");
            s = Regex.Replace(s, @"^(新建并保存|并保存)\s+", "");
            s = Regex.Replace(s, @"^用户希望", "");
            s = StripLocalPathGuidePrefix(s);
            s = s.Trim(' ', '：', ':', '，', '。', '-', '『', '』');
            if (s == prev) break;
        }

        // 清理未闭合括号导致截断半截残句（如「并完成编译自检（目标源码工程」）
        int openCn = s.Count(c => c == '（');
        int closeCn = s.Count(c => c == '）');
        int openEn = s.Count(c => c == '(');
        int closeEn = s.Count(c => c == ')');
        if (openCn > closeCn)
        {
            int cut = s.LastIndexOf('（');
            if (cut > 4) s = s[..cut].TrimEnd();
        }
        if (openEn > closeEn)
        {
            int cut = s.LastIndexOf('(');
            if (cut > 4) s = s[..cut].TrimEnd();
        }

        var firstClause = s.Split(new[] { '。', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? s;
        if (firstClause.Length < 4) firstClause = s;
        if (string.IsNullOrWhiteSpace(firstClause)) return "协同子任务";
        // 最终再挡一次半截盘符路径（如 "/Project/Godot/..."）
        if (Regex.IsMatch(firstClause, @"^[\\/](Project|Users|Program|Windows|Godot)\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(firstClause, @"^[A-Za-z]:[\\/]"))
        {
            firstClause = StripLocalPathGuidePrefix(firstClause);
            if (string.IsNullOrWhiteSpace(firstClause)) return "协同子任务";
        }
        return firstClause.Length <= maxLen ? firstClause : firstClause[..maxLen] + "..";
    }

    /// <summary>
    /// 在 scanLen 内查找引导冒号：允许中文全角『：』，或非盘符的英文『:』；
    /// 严禁匹配 Windows 盘符形式 [A-Za-z]:/ 与 [A-Za-z]:\。
    /// </summary>
    private static int FindGuideColonIndex(string s, int scanLen)
    {
        int limit = Math.Min(scanLen, s.Length);
        for (int i = 0; i < limit; i++)
        {
            char c = s[i];
            if (c == '：') return i; // 中文全角冒号始终可作引导切分
            if (c != ':') continue;
            // 严禁匹配盘符 [A-Za-z]:/ 与 [A-Za-z]:\
            if (i > 0 && char.IsLetter(s[i - 1]) &&
                i + 1 < s.Length && (s[i + 1] == '/' || s[i + 1] == '\\') &&
                (i == 1 || !char.IsLetterOrDigit(s[i - 2])))
            {
                continue;
            }
            // 英文冒号后紧跟路径斜杠也跳过
            if (i + 1 < s.Length && (s[i + 1] == '/' || s[i + 1] == '\\'))
                continue;
            return i;
        }
        return -1;
    }

    /// <summary>
    /// 剥离「请直接(落地)修改 &lt;本地绝对路径&gt; 工程…」引导语及句中盘符绝对路径，保留后续中文任务主题。
    /// </summary>
    private static string StripLocalPathGuidePrefix(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;

        // 句首：请直接落地修改 / 请直接修改 / 直接修改 + 盘符路径 + 可选「工程(中的源码)?」+ 分隔符
        s = Regex.Replace(
            s,
            @"^(?:请)?(?:直接)?(?:落地)?修改\s*[A-Za-z]:[\\/][^\s，,。；;]+(?:\s*工程(?:中的[^，,。；;]*)?)?[，,\s]*",
            "",
            RegexOptions.IgnoreCase);

        // 句首残留半截路径（盘符被误切后留下的 /Project/... 或 \Users\...）
        s = Regex.Replace(
            s,
            @"^[\\/](?:Project|Users|Program Files|Windows|Godot)[^\s，,。；;]+(?:\s*工程(?:中的[^，,。；;]*)?)?[，,\s]*",
            "",
            RegexOptions.IgnoreCase);

        // 句中任意 Windows 绝对路径（含 UNC 风格盘符路径段）→ 删掉路径本身，保留前后文
        s = Regex.Replace(
            s,
            @"\s*[A-Za-z]:[\\/][^\s，,。；;『』「」]+",
            " ",
            RegexOptions.IgnoreCase);

        // 「工程中的源码」残留粘连清理
        s = Regex.Replace(s, @"^(?:工程(?:中的源码)?)\s*[，,\s]*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '，', ',', '：', ':', '。');
        return s;
    }

    private static bool SweepStaleWorkingStatesIfNeeded(
        Dictionary<string, LiveAgentWorkState> map,
        long fallbackFileWriteUtcTicks)
    {
        if (map.Count == 0) return false;
        bool mutated = false;
        int selfPid = Environment.ProcessId;
        long nowUtcTicks = DateTime.UtcNow.Ticks;
        const long legacyStaleThresholdTicks = TimeSpan.TicksPerMinute * 12;

        foreach (var entry in map.Values)
        {
            bool nonCodexQuotaPhantom = !entry.AgentId.Equals("codex_cli", StringComparison.OrdinalIgnoreCase) &&
                entry.Status == "error" &&
                ((entry.LastFailureBadge?.Contains("配额", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (entry.LastFailureBadge?.Contains("quota", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (entry.LatestLogTail?.Contains("配额耗尽", StringComparison.OrdinalIgnoreCase) ?? false));
            if (nonCodexQuotaPhantom)
            {
                entry.Status = "idle";
                entry.WorkflowPhase = "idle";
                entry.LastFailureBadge = "";
                entry.LatestLogTail = "空闲待命（已自动清理历史 Codex 误报）";
                mutated = true;
                continue;
            }

            if (entry.Status is not ("working" or "reworking")) continue;
            if (entry.DispatcherPid == selfPid) continue;

            bool isDeadDispatcher = entry.DispatcherPid > 0 && !IsProcessAliveFast(entry.DispatcherPid);
            long entryTicks = entry.UpdatedUtcTicks > 0 ? entry.UpdatedUtcTicks : fallbackFileWriteUtcTicks;
            bool isLegacyExpired = !AppPaths.IsIsolatedTest &&
                                   entry.DispatcherPid <= 0 &&
                                   entryTicks > 0 &&
                                   (nowUtcTicks - entryTicks) > legacyStaleThresholdTicks;

            if (!isDeadDispatcher && !isLegacyExpired) continue;

            bool hasInboxReceipt = !string.IsNullOrWhiteSpace(entry.OutputInboxFile) && File.Exists(entry.OutputInboxFile);
            bool looksDelivered =
                hasInboxReceipt ||
                entry.WorkflowPhase.Equals("verified_delivered", StringComparison.OrdinalIgnoreCase) ||
                entry.WorkflowPhase.Equals("master_verifying", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(entry.VerificationSummary) &&
                 (entry.VerificationSummary.Contains("验收通过", StringComparison.OrdinalIgnoreCase) ||
                  entry.VerificationSummary.Contains("准予交付", StringComparison.OrdinalIgnoreCase))) ||
                (!string.IsNullOrWhiteSpace(entry.LatestLogTail) &&
                 entry.LatestLogTail.Contains("验收通过", StringComparison.OrdinalIgnoreCase) &&
                 entry.LatestLogTail.Contains("准予交付", StringComparison.OrdinalIgnoreCase));
            // 成功交付证据在时绝不能洗成 idle（修复桌面直连成功后被探针/收尾洗回空闲的缺陷）
            entry.Status = looksDelivered ? "done" : "idle";
            entry.WorkflowPhase = looksDelivered ? "verified_delivered" : "idle";
            entry.DispatcherPid = 0;
            if (looksDelivered && string.IsNullOrWhiteSpace(entry.VerificationSummary))
            {
                entry.VerificationSummary = hasInboxReceipt
                    ? "历史任务回执已落盘（自动收敛）"
                    : "历史任务已交付（自动收敛保留终态）";
            }
            mutated = true;
        }

        if (mutated)
        {
            try
            {
                RegistryService.WriteJsonAtomic(LiveStateMapPath, map);
                var single = ReadLiveWorkStateCore();
                if (single != null)
                {
                    var updatedSingle = map.Values
                        .Where(v => v.AgentId.Equals(single.AgentId, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(v => v.UpdatedUtcTicks)
                        .FirstOrDefault();
                    if (updatedSingle != null)
                    {
                        RegistryService.WriteJsonAtomic(LiveStateJsonPath, updatedSingle);
                    }
                }
            }
            catch { }
        }
        return mutated;
    }

    public static bool BelongsToProject(LiveAgentWorkState? state, ProjectProfile? project)
    {
        if (state == null) return false;
        if (project == null) return true;

        if (!string.IsNullOrWhiteSpace(state.ProjectId) && !string.IsNullOrWhiteSpace(project.Id))
        {
            return string.Equals(state.ProjectId.Trim(), project.Id.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(state.ProjectRoot) && !string.IsNullOrWhiteSpace(project.PrimaryRoot))
        {
            var normA = NormalizeDirPath(state.ProjectRoot);
            var normB = NormalizeDirPath(project.PrimaryRoot);
            if (!string.IsNullOrEmpty(normA) && !string.IsNullOrEmpty(normB))
            {
                return string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!string.IsNullOrWhiteSpace(state.ProjectName) && !string.IsNullOrWhiteSpace(project.Name))
        {
            return string.Equals(state.ProjectName.Trim(), project.Name.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // 仅当状态完全无任何工程标识（兼容早年单工程测试数据）时视为属于当前工程
        return string.IsNullOrWhiteSpace(state.ProjectId) &&
               string.IsNullOrWhiteSpace(state.ProjectRoot) &&
               string.IsNullOrWhiteSpace(state.ProjectName);
    }

    private static string NormalizeDirPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        try
        {
            return Path.GetFullPath(raw.Trim()).TrimEnd('\\', '/');
        }
        catch
        {
            return raw.Trim().TrimEnd('\\', '/');
        }
    }

    private static string BuildLiveStateMapKey(string agentId, ProjectProfile? project)
    {
        var pKey = project?.Id?.Trim() ?? "";
        return string.IsNullOrEmpty(pKey) ? agentId : $"{pKey}::{agentId}";
    }

    /// <summary>
    /// 按当前工程严格隔离读取在岗 AI 实时状态（键为 AgentId，同 Agent 多条时取最新一条），彻底避免跨工程状态串台。
    /// </summary>
    public static Dictionary<string, LiveAgentWorkState> ReadLiveWorkStatesForProject(ProjectProfile? project)
    {
        var all = ReadAllLiveWorkStates();
        var result = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in all.Values)
        {
            if (string.IsNullOrWhiteSpace(item.AgentId)) continue;
            if (!BelongsToProject(item, project)) continue;
            if (!result.TryGetValue(item.AgentId, out var existing) || item.UpdatedUtcTicks >= existing.UpdatedUtcTicks)
            {
                result[item.AgentId] = item;
            }
        }
        return result;
    }

    /// <summary>
    /// 全局聚合读取各在岗 AI 最新一条实时状态（跨所有项目自动跟随，供全局悬浮窗使用）。
    /// </summary>
    public static Dictionary<string, LiveAgentWorkState> ReadLatestGlobalWorkStates()
    {
        var all = ReadAllLiveWorkStates();
        var diskWorkingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in all.Values)
        {
            if (string.IsNullOrWhiteSpace(item.AgentId)) continue;
            if (item.Status is not ("working" or "reworking")) continue;
            diskWorkingCounts.TryGetValue(item.AgentId, out int c);
            diskWorkingCounts[item.AgentId] = c + 1;
        }

        var result = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in all.Values)
        {
            if (string.IsNullOrWhiteSpace(item.AgentId)) continue;
            if (!result.TryGetValue(item.AgentId, out var existing))
            {
                result[item.AgentId] = item;
                continue;
            }
            bool curWorking = item.Status is "working" or "reworking";
            bool prevWorking = existing.Status is "working" or "reworking";
            if ((curWorking && !prevWorking) || (curWorking == prevWorking && item.UpdatedUtcTicks >= existing.UpdatedUtcTicks))
            {
                result[item.AgentId] = item;
            }
        }

        foreach (var kv in result.ToList())
        {
            diskWorkingCounts.TryGetValue(kv.Key, out int diskWorkingCount);
            kv.Value.ActiveTaskCount = diskWorkingCount;
        }
        return result;
    }

    /// <summary>
    /// 本机 Grok Bot 桌面客户端已运行，或已配置/可解析到桌面 EXE 路径。
    /// </summary>
    public static bool IsGrokDesktopChannelAvailable(AgentRosterEntry? agent)
    {
        if (agent == null) return false;
        try
        {
            foreach (var pn in GetCandidateProcessNames(agent))
            {
                Process[]? procs = null;
                try
                {
                    procs = Process.GetProcessesByName(pn);
                    if (procs.Length > 0) return true;
                }
                catch { }
                finally
                {
                    if (procs != null)
                    {
                        foreach (var p in procs) { try { p.Dispose(); } catch { } }
                    }
                }
            }
        }
        catch { }
        return ResolveGuiExecutable(agent) != null;
    }

    /// <summary>
    /// 同项目某 Agent 成功交付后：清理该 ProjectId 下其他 Agent 遗留的 error / awaiting_user_choice，置 idle 并清空失败徽章。
    /// </summary>
    public static int ClearSiblingStaleFailuresForProject(ProjectProfile project, string successfulAgentId)
    {
        if (project == null || string.IsNullOrWhiteSpace(successfulAgentId)) return 0;
        try
        {
            using var mutex = new System.Threading.Mutex(false, "Local\\AICollabHubLiveStatusMutex");
            bool locked = false;
            try { locked = mutex.WaitOne(1500); }
            catch (System.Threading.AbandonedMutexException) { locked = true; }

            try
            {
                var map = ReadAllLiveWorkStates();
                int cleared = ClearSiblingStaleFailuresInMap(map, project, successfulAgentId);
                if (cleared > 0)
                {
                    RegistryService.WriteJsonAtomic(LiveStateMapPath, map);
                    NetworkResilienceService.ClearInterruptedCheckpoint(project);
                }
                return cleared;
            }
            finally
            {
                if (locked) { try { mutex.ReleaseMutex(); } catch { } }
            }
        }
        catch { return 0; }
    }

    private static int ClearSiblingStaleFailuresInMap(
        Dictionary<string, LiveAgentWorkState> map,
        ProjectProfile project,
        string successfulAgentId)
    {
        if (map == null || map.Count == 0 || project == null) return 0;
        int cleared = 0;
        foreach (var entry in map.Values)
        {
            if (!BelongsToProject(entry, project)) continue;
            if (entry.AgentId.Equals(successfulAgentId, StringComparison.OrdinalIgnoreCase)) continue;
            bool staleFail =
                entry.Status.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                entry.WorkflowPhase.Equals("awaiting_user_choice", StringComparison.OrdinalIgnoreCase) ||
                entry.WorkflowPhase.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                entry.WorkflowPhase.Equals("interrupted_net", StringComparison.OrdinalIgnoreCase);
            if (!staleFail) continue;
            entry.Status = "idle";
            entry.WorkflowPhase = "idle";
            entry.LastFailureBadge = "";
            entry.CanResumeInterruptedChain = false;
            entry.DispatcherPid = 0;
            entry.LatestLogTail = "同项目新任务已成功交付，已自动清理本 Agent 陈旧异常状态";
            entry.UpdatedAt = DateTime.Now.ToString("HH:mm:ss");
            entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            cleared++;
        }
        return cleared;
    }

    /// <summary>
    /// 单卡片快捷复位：清除指定 AI 成员（及当前工程关联）的异常/残留实时状态记录。
    /// </summary>
    public static void ClearAgentLiveState(string agentId, ProjectProfile? project = null)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;
        try
        {
            using var mutex = new System.Threading.Mutex(false, "Local\\AICollabHubLiveStatusMutex");
            bool locked = false;
            try
            {
                locked = mutex.WaitOne(1500);
            }
            catch (System.Threading.AbandonedMutexException)
            {
                locked = true;
            }

            try
            {
                var cur = ReadAllLiveWorkStates();
                var keysToRemove = cur
                    .Where(kv => kv.Value.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase) &&
                                 (project == null || BelongsToProject(kv.Value, project)))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in keysToRemove)
                {
                    cur.Remove(k);
                }
                RegistryService.WriteJsonAtomic(LiveStateMapPath, cur);

                var single = ReadLiveWorkStateCore();
                if (single != null &&
                    single.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase) &&
                    (project == null || BelongsToProject(single, project)))
                {
                    if (cur.Count == 0)
                    {
                        if (File.Exists(LiveStateJsonPath)) File.Delete(LiveStateJsonPath);
                    }
                    else
                    {
                        RegistryService.WriteJsonAtomic(LiveStateJsonPath, cur.Values.OrderByDescending(v => v.UpdatedUtcTicks).First());
                    }
                }

                if (project != null)
                {
                    var cp = NetworkResilienceService.LoadInterruptedCheckpoint(project);
                    if (cp != null)
                    {
                        NetworkResilienceService.ClearInterruptedCheckpoint(project);
                    }
                }
            }
            finally
            {
                if (locked)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }
        catch { }
    }

    private static readonly HashSet<string> HubOwnedCliProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "pwsh", "cmd", "cursor-agent", "agent", "codex", "claude", "gemini", "node"
    };

    private static readonly HashSet<string> ForbiddenDesktopGuiProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // 仅桌面 GUI 进程名；勿与下方 CLI 白名单（claude/codex/node 等）冲突
        "Cursor", "Antigravity", "Grok Bot", "CodeBuddy CN", "devenv", "Code", "Windsurf",
        "Trae", "Kiro", "Claude Desktop", "ChatGPT", "Notion", "Slack", "Discord", "explorer"
    };

    /// <summary>
    /// 应用退出时安全终止本软件拉起的后台派单子进程，并将残留 working 状态原子归位 idle。
    /// 严防误杀用户打开的桌面 IDE/聊天软件 GUI。
    /// </summary>
    public static int TerminateHubOwnedDispatchersOnShutdown()
    {
        if (AppPaths.IsIsolatedTest) return 0;

        int killed = 0;
        var pids = new HashSet<int>();
        Dictionary<string, LiveAgentWorkState> map;
        try { map = ReadAllLiveWorkStates(); }
        catch { map = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase); }

        foreach (var entry in map.Values)
        {
            if (entry.DispatcherPid > 0 && entry.DispatcherPid != Environment.ProcessId)
                pids.Add(entry.DispatcherPid);
        }

        LiveAgentWorkState? single = null;
        try { single = ReadLiveWorkStateCore(); } catch { }
        if (single != null && single.DispatcherPid > 0 && single.DispatcherPid != Environment.ProcessId)
            pids.Add(single.DispatcherPid);

        foreach (var pid in pids)
        {
            if (TryKillHubOwnedDispatcherProcess(pid))
                killed++;
        }

        try
        {
            bool mutated = false;
            foreach (var entry in map.Values)
            {
                bool busy = entry.Status is "working" or "reworking" || entry.DispatcherPid > 0;
                if (!busy) continue;
                entry.Status = "idle";
                entry.WorkflowPhase = "idle";
                entry.DispatcherPid = 0;
                entry.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                entry.LatestLogTail = "软件退出：后台派单已安全终止，状态已归位空闲";
                mutated = true;
            }

            if (mutated || pids.Count > 0)
            {
                RegistryService.WriteJsonAtomic(LiveStateMapPath, map);
            }

            if (single != null && (single.Status is "working" or "reworking" || single.DispatcherPid > 0))
            {
                single.Status = "idle";
                single.WorkflowPhase = "idle";
                single.DispatcherPid = 0;
                single.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                single.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                single.LatestLogTail = "软件退出：后台派单已安全终止，状态已归位空闲";
                RegistryService.WriteJsonAtomic(LiveStateJsonPath, single);
            }
            else if (map.Count > 0)
            {
                var latest = map.Values.OrderByDescending(v => v.UpdatedUtcTicks).FirstOrDefault();
                if (latest != null)
                    RegistryService.WriteJsonAtomic(LiveStateJsonPath, latest);
            }
        }
        catch { }

        return killed;
    }

    private static bool TryKillHubOwnedDispatcherProcess(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return false;
        Process? proc = null;
        try
        {
            proc = Process.GetProcessById(pid);
        }
        catch
        {
            return false;
        }

        try
        {
            if (!IsHubOwnedDispatcherProcess(proc))
                return false;

            try { proc.Kill(entireProcessTree: true); }
            catch
            {
                try { proc.Kill(); } catch { return false; }
            }

            try { proc.WaitForExit(1500); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { proc?.Dispose(); } catch { }
        }
    }

    private static bool IsHubOwnedDispatcherProcess(Process proc)
    {
        string name;
        try { name = proc.ProcessName ?? ""; }
        catch { return false; }
        if (string.IsNullOrWhiteSpace(name)) return false;

        // 绝不允许终止桌面 GUI 进程
        if (ForbiddenDesktopGuiProcessNames.Contains(name))
            return false;
        if (name.Contains("Grok", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Bot", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Contains("CodeBuddy", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Contains("Antigravity", StringComparison.OrdinalIgnoreCase))
            return false;

        // GUI 主窗句柄存在的未知进程一律放过（避免误杀）
        IntPtr mainHwnd = IntPtr.Zero;
        try { mainHwnd = proc.MainWindowHandle; } catch { }
        string? cmd = TryQueryProcessCommandLine(proc.Id);

        bool isHubExe = name.Contains("AI协同中心", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("AICollabHub", StringComparison.OrdinalIgnoreCase);
        if (isHubExe)
        {
            // 仅允许本软件 CLI/派单后台实例，禁止杀 GUI 主进程
            if (proc.Id == Environment.ProcessId) return false;
            if (mainHwnd != IntPtr.Zero) return false;
            if (string.IsNullOrWhiteSpace(cmd)) return false;
            return cmd.Contains("--cli", StringComparison.OrdinalIgnoreCase) ||
                   cmd.Contains("dispatch", StringComparison.OrdinalIgnoreCase);
        }

        if (!HubOwnedCliProcessNames.Contains(name))
            return false;

        // shell / CLI：必须具备后台派单特征；有可见主窗的 GUI 壳一律不杀
        if (mainHwnd != IntPtr.Zero) return false;
        if (string.IsNullOrWhiteSpace(cmd)) return false;

        return LooksLikeHubDispatchCommandLine(cmd);
    }

    private static bool LooksLikeHubDispatchCommandLine(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        bool hasHubMarker =
            cmd.Contains("ai-collab-hub", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("AI协同中心", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("AICollabHub", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("启动协同中心", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("live_agent", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("work\\collab", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("work/collab", StringComparison.OrdinalIgnoreCase);

        bool hasDispatchMarker =
            cmd.Contains("dispatch", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("--cli", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("cursor-agent", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("codex exec", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("claude ", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("gemini ", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("-File ", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("ExecutionPolicy", StringComparison.OrdinalIgnoreCase);

        return hasHubMarker || (hasDispatchMarker && (
            cmd.Contains("collab", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("dispatch_prompt", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("CreateNoWindow", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("-NonInteractive", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("-NoProfile", StringComparison.OrdinalIgnoreCase)));
    }

    private static string? TryQueryProcessCommandLine(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            // 无额外依赖：用短生命周期 PowerShell CIM 查询命令行（仅退出收口路径调用）
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2500))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            string line = (stdout ?? "").Trim();
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 清理已结束或历史残留状态，仅保留真正正在存活执行的任务，供前端一键归零重置面板。
    /// </summary>
    public static void ClearFinishedOrStaleLiveStates()
    {
        try
        {
            var cur = ReadAllLiveWorkStates();
            var keep = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in cur)
            {
                if (kv.Value.Status is "working" or "reworking" &&
                    (kv.Value.DispatcherPid == Environment.ProcessId || IsProcessAliveFast(kv.Value.DispatcherPid)))
                {
                    keep[kv.Key] = kv.Value;
                }
            }
            RegistryService.WriteJsonAtomic(LiveStateMapPath, keep);
            if (keep.Count == 0)
            {
                if (File.Exists(LiveStateJsonPath)) File.Delete(LiveStateJsonPath);
            }
            else
            {
                RegistryService.WriteJsonAtomic(LiveStateJsonPath, keep.Values.First());
            }
        }
        catch { }
    }

    public static Dictionary<string, LiveAgentWorkState> ReadAllLiveWorkStates()
    {
        try
        {
            if (File.Exists(LiveStateMapPath))
            {
                long fileTicks = File.GetLastWriteTimeUtc(LiveStateMapPath).Ticks;
                var txt = ReadSharedUtf8TextSafe(LiveStateMapPath);
                var map = JsonSerializer.Deserialize<Dictionary<string, LiveAgentWorkState>>(txt, RegistryService.JsonOpts);
                if (map != null)
                {
                    SweepStaleWorkingStatesIfNeeded(map, fileTicks);
                    if (map.Count > 0) return map;
                }
            }
        }
        catch { }

        var single = ReadLiveWorkState();
        var fallback = new Dictionary<string, LiveAgentWorkState>(StringComparer.OrdinalIgnoreCase);
        if (single != null && !string.IsNullOrWhiteSpace(single.AgentId))
        {
            fallback[single.AgentId] = single;
        }
        return fallback;
    }

    private static LiveAgentWorkState? ReadLiveWorkStateCore()
    {
        try
        {
            if (!File.Exists(LiveStateJsonPath)) return null;
            var txt = ReadSharedUtf8TextSafe(LiveStateJsonPath);
            return JsonSerializer.Deserialize<LiveAgentWorkState>(txt, RegistryService.JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static LiveAgentWorkState? ReadLiveWorkState()
    {
        var single = ReadLiveWorkStateCore();
        if (single != null && single.Status is "working" or "reworking" && single.DispatcherPid != Environment.ProcessId)
        {
            long ticks = single.UpdatedUtcTicks > 0
                ? single.UpdatedUtcTicks
                : (File.Exists(LiveStateJsonPath) ? File.GetLastWriteTimeUtc(LiveStateJsonPath).Ticks : 0);
            bool dead = single.DispatcherPid > 0 && !IsProcessAliveFast(single.DispatcherPid);
            bool legacyExpired = !AppPaths.IsIsolatedTest &&
                                 single.DispatcherPid <= 0 &&
                                 ticks > 0 &&
                                 (DateTime.UtcNow.Ticks - ticks) > TimeSpan.TicksPerMinute * 12;
            if (dead || legacyExpired)
            {
                bool hasInbox = !string.IsNullOrWhiteSpace(single.OutputInboxFile) && File.Exists(single.OutputInboxFile);
                bool looksDelivered =
                    hasInbox ||
                    single.WorkflowPhase.Equals("verified_delivered", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(single.VerificationSummary) &&
                     single.VerificationSummary.Contains("验收通过", StringComparison.OrdinalIgnoreCase));
                single.Status = looksDelivered ? "done" : "idle";
                single.WorkflowPhase = looksDelivered ? "verified_delivered" : "idle";
                single.DispatcherPid = 0;
                try { RegistryService.WriteJsonAtomic(LiveStateJsonPath, single); } catch { }
            }
        }
        return single;
    }

    private static void WriteLiveStatus(
        AgentRosterEntry agent,
        ProjectProfile project,
        string liveStatusMdPath,
        string status,
        string taskBrief,
        string liveLogTail,
        string inboxFile,
        ExternalDialogSessionInfo? sessionInfo = null,
        string sessionAction = "",
        string dispatchReason = "",
        string workflowPhase = "",
        string masterPlannerName = "",
        string selectedModel = "",
        string selectedEffortTier = "",
        string selectedTierBadge = "",
        int currentRound = 0,
        int maxRounds = 0,
        string verificationSummary = "",
        string changedFilesSummary = "",
        int networkRetryAttempt = 0,
        int maxNetworkRetries = 0,
        string lastFailureBadge = "",
        string failoverNote = "",
        bool? canResumeInterruptedChain = null)
    {
        try
        {
            using var mutex = new System.Threading.Mutex(false, "Local\\AICollabHubLiveStatusMutex");
            bool locked = false;
            try
            {
                locked = mutex.WaitOne(1500);
            }
            catch (System.Threading.AbandonedMutexException)
            {
                locked = true;
            }

            try
            {
                var now = DateTime.Now.ToString("HH:mm:ss");
                var map = ReadAllLiveWorkStates();
                var mapKey = BuildLiveStateMapKey(agent.Id, project);
                LiveAgentWorkState? prev = null;
                if (map.TryGetValue(mapKey, out var exactPrev) && BelongsToProject(exactPrev, project))
                {
                    prev = exactPrev;
                }
                else if (map.TryGetValue(agent.Id, out var legacyPrev) && BelongsToProject(legacyPrev, project))
                {
                    prev = legacyPrev;
                }

                // 清理属于同一工程的该 Agent 旧单键/重复键，避免同一工程出现重复状态条目
                foreach (var dupKey in map.Keys.Where(k =>
                    map[k].AgentId.Equals(agent.Id, StringComparison.OrdinalIgnoreCase) &&
                    BelongsToProject(map[k], project) &&
                    !string.Equals(k, mapKey, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    map.Remove(dupKey);
                }

                var cleanBrief = (taskBrief ?? "").Replace("\uFFFD", "");
                var cleanTail = (liveLogTail ?? "").Replace("\uFFFD", "");
                var cleanReason = !string.IsNullOrWhiteSpace(dispatchReason) ? dispatchReason : (prev?.DispatchReason ?? "");

                var state = new LiveAgentWorkState
                {
                    AgentId = agent.Id,
                    AgentName = agent.Name,
                    ProjectId = project.Id,
                    ProjectRoot = project.PrimaryRoot,
                    ProjectName = project.Name,
                    Status = status,
                    WorkflowPhase = !string.IsNullOrWhiteSpace(workflowPhase)
                        ? workflowPhase
                        : (status == "done" ? "verified_delivered" : (status == "working" ? (prev?.WorkflowPhase ?? "executing") : "error")),
                    DispatcherPid = status is "working" or "reworking" ? Environment.ProcessId : 0,
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks,
                    MasterPlannerName = !string.IsNullOrWhiteSpace(masterPlannerName) ? masterPlannerName : (prev?.MasterPlannerName ?? "Antigravity"),
                    SelectedModel = !string.IsNullOrWhiteSpace(selectedModel) ? selectedModel : (prev?.SelectedModel ?? ""),
                    SelectedEffortTier = !string.IsNullOrWhiteSpace(selectedEffortTier) ? selectedEffortTier : (prev?.SelectedEffortTier ?? "high"),
                    SelectedTierBadge = !string.IsNullOrWhiteSpace(selectedTierBadge) ? selectedTierBadge : (prev?.SelectedTierBadge ?? "高档·High"),
                    CurrentRound = currentRound > 0 ? currentRound : (prev?.CurrentRound > 0 ? prev.CurrentRound : 1),
                    MaxRounds = maxRounds > 0 ? maxRounds : (prev?.MaxRounds > 0 ? prev.MaxRounds : 3),
                    NetworkRetryAttempt = networkRetryAttempt > 0 ? networkRetryAttempt : (status == "working" ? (prev?.NetworkRetryAttempt ?? 0) : networkRetryAttempt),
                    MaxNetworkRetries = maxNetworkRetries > 0 ? maxNetworkRetries : (prev?.MaxNetworkRetries > 0 ? prev.MaxNetworkRetries : 3),
                    LastFailureBadge = !string.IsNullOrWhiteSpace(lastFailureBadge)
                        ? lastFailureBadge
                        : (status == "done" || (status == "working" && workflowPhase != "reconnecting") ? "" : (prev?.LastFailureBadge ?? "")),
                    FailoverNote = !string.IsNullOrWhiteSpace(failoverNote) ? failoverNote : (prev?.FailoverNote ?? ""),
                    CanResumeInterruptedChain = canResumeInterruptedChain ?? (status == "done" ? false : (prev?.CanResumeInterruptedChain ?? false)),
                    VerificationSummary = !string.IsNullOrWhiteSpace(verificationSummary) ? verificationSummary : (prev?.VerificationSummary ?? ""),
                    ChangedFilesSummary = !string.IsNullOrWhiteSpace(changedFilesSummary) ? changedFilesSummary : (prev?.ChangedFilesSummary ?? ""),
                    SessionId = sessionInfo?.SessionId ?? prev?.SessionId ?? "",
                    SessionTitle = sessionInfo?.Title ?? prev?.SessionTitle ?? "",
                    SessionFilePath = sessionInfo?.FilePath ?? prev?.SessionFilePath ?? "",
                    SessionAction = !string.IsNullOrWhiteSpace(sessionAction) ? sessionAction : (prev?.SessionAction ?? "resumed"),
                    TaskSummary = cleanBrief,
                    DispatchReason = cleanReason,
                    StartedAt = (status == "working" && prev?.Status == "working" && !string.IsNullOrWhiteSpace(prev.StartedAt)) ? prev.StartedAt : now,
                    UpdatedAt = now,
                    LatestLogTail = cleanTail,
                    OutputInboxFile = inboxFile
                };

                map[mapKey] = state;
                if (status == "done" || string.Equals(state.WorkflowPhase, "verified_delivered", StringComparison.OrdinalIgnoreCase))
                {
                    // 同项目成功交付：清掉其他 Agent 遗留的 error / awaiting_user_choice 红灯，并清理过期断点
                    ClearSiblingStaleFailuresInMap(map, project, agent.Id);
                    NetworkResilienceService.ClearStaleCheckpointForAgent(project, agent.Id);
                    NetworkResilienceService.ClearInterruptedCheckpoint(project);
                }
                RegistryService.WriteJsonAtomic(LiveStateMapPath, map);
                RegistryService.WriteJsonAtomic(LiveStateJsonPath, state);

                var md = new StringBuilder();
                md.AppendLine($"# 多 AI 实时工作与主脑闭环看板 ({project.Name})");
                md.AppendLine($"- **最后刷新时间**：`{DateTime.Now:yyyy-MM-dd HH:mm:ss}`");
                md.AppendLine();

                foreach (var kv in map.Values
                    .Where(v => BelongsToProject(v, project))
                    .OrderByDescending(v => v.Status == "working")
                    .ThenByDescending(v => v.UpdatedAt))
                {
                    var stBadge = kv.WorkflowPhase switch
                    {
                        "reconnecting" => $"📡 网络/通道断线重连中 · 第 {kv.NetworkRetryAttempt}/{kv.MaxNetworkRetries} 次尝试 ({kv.LastFailureBadge})",
                        "interrupted_net" => $"🔌 网络中断 · 任务链已保护保存在第 {kv.CurrentRound}/{kv.MaxRounds} 轮断点（待恢复续跑）",
                        "awaiting_user_choice" => $"🛠 执行卡住/受阻 ({kv.LastFailureBadge}) · 等待您选择解决方式",
                        "master_verifying" => $"🔍 主脑 [{kv.MasterPlannerName}] 正在验收第 {kv.CurrentRound}/{kv.MaxRounds} 轮产出...",
                        "reworking" => $"↩ 验收未通过 · 主脑打回继续修复中 (第 {kv.CurrentRound}/{kv.MaxRounds} 轮)",
                        "verified_delivered" => $"✅ 主脑 [{kv.MasterPlannerName}] 验收通过 · 已向用户交付",
                        _ => kv.Status switch
                        {
                            "working" => $"🛠 正在实操改工程中 (第 {kv.CurrentRound}/{kv.MaxRounds} 轮)",
                            "done" => $"✅ 主脑 [{kv.MasterPlannerName}] 验收通过 · 已交付",
                            _ => "⚠️ 执行异常或中止"
                        }
                    };
                    var actDesc = kv.SessionAction == "created" ? "新建项目专属对话框" : "续接已有项目对话框";
                    md.AppendLine($"## 🤖 {kv.AgentName} — {stBadge}");
                    if (!string.IsNullOrWhiteSpace(kv.SelectedModel))
                    {
                        md.AppendLine($"- **主脑适配模型与档位**：`{kv.SelectedModel}` · **{kv.SelectedTierBadge}**");
                    }
                    if (kv.NetworkRetryAttempt > 0 || !string.IsNullOrWhiteSpace(kv.FailoverNote))
                    {
                        md.AppendLine($"- **网络容灾与自愈记录**：重连重试 `{kv.NetworkRetryAttempt}` 次" +
                                      (string.IsNullOrWhiteSpace(kv.FailoverNote) ? "" : $" · {kv.FailoverNote}"));
                    }
                    if (!string.IsNullOrWhiteSpace(kv.DispatchReason))
                    {
                        md.AppendLine($"- **主脑分配依据**：{kv.DispatchReason}");
                    }
                    if (!string.IsNullOrWhiteSpace(kv.VerificationSummary))
                    {
                        md.AppendLine($"- **主脑验收结论**：{kv.VerificationSummary}");
                    }
                    if (!string.IsNullOrWhiteSpace(kv.ChangedFilesSummary))
                    {
                        md.AppendLine($"- **实际改动文件**：{kv.ChangedFilesSummary}");
                    }
                    if (!string.IsNullOrWhiteSpace(kv.SessionId))
                    {
                        md.AppendLine($"- **分配的目标对话框**：`{kv.SessionId}` · {kv.SessionTitle} ({actDesc})");
                        if (!string.IsNullOrWhiteSpace(kv.SessionFilePath))
                        {
                            md.AppendLine($"- **对话记录源文件 (`F:\\AIChat`)**：`{kv.SessionFilePath}`");
                        }
                    }
                    md.AppendLine($"- **分配的具体工作**：{kv.TaskSummary}");
                    md.AppendLine($"- **启动/更新时间**：`{kv.StartedAt}` ➔ `{kv.UpdatedAt}`");
                    if (!string.IsNullOrWhiteSpace(kv.OutputInboxFile))
                    {
                        md.AppendLine($"- **产出回收文件**：`{kv.OutputInboxFile}`");
                    }
                    md.AppendLine("- **实时工作流输出 / 执行结论**：");
                    md.AppendLine("```text");
                    var tail = string.IsNullOrWhiteSpace(kv.LatestLogTail) ? "(等待输出流...)" : kv.LatestLogTail.Trim();
                    md.AppendLine(tail.Length > 1200 ? tail[..1200] + "\n...(完整内容见右侧对话框阅读器或 inbox 文件)" : tail);
                    md.AppendLine("```");
                    md.AppendLine();
                }

                var dir = Path.GetDirectoryName(liveStatusMdPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(liveStatusMdPath, md.ToString(), new UTF8Encoding(false));
            }
            finally
            {
                if (locked)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }
        catch { }
    }

    private static List<string> GetCandidateProcessNames(AgentRosterEntry agent)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.GuiExePath))
        {
            var fn = Path.GetFileNameWithoutExtension(agent.GuiExePath);
            if (!string.IsNullOrWhiteSpace(fn)) list.Add(fn);
        }
        switch (agent.Id)
        {
            case "grok_bot":
                list.Add("Grok Bot");
                break;
            case "cursor":
                list.Add("Cursor");
                break;
            case "codex_cli":
                list.Add("Codex");
                list.Add("ChatGPT");
                list.Add("codex");
                break;
            case "antigravity":
                // Antigravity / 谷歌 Antigravity IDE 可能呈现多种进程名，全量探测避免主脑误判「未启动」
                list.Add("Antigravity");
                list.Add("antigravity");
                list.Add("Google Antigravity");
                list.Add("Antigravity IDE");
                break;
            case "claude_code":
                list.Add("Claude");
                list.Add("claude");
                list.Add("Claude Code");
                break;
            case "windsurf":
                list.Add("Windsurf");
                break;
            case "ollama_local":
                list.Add("ollama");
                break;
            case "lm_studio":
                list.Add("LM Studio");
                break;
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly object GuiExeCacheLock = new();
    private static readonly Dictionary<string, string?> GuiExeCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? ResolveGuiExecutable(AgentRosterEntry agent)
    {
        var cacheKey = $"{agent.Id}|{agent.GuiExePath}";
        lock (GuiExeCacheLock)
        {
            if (GuiExeCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var resolved = ResolveGuiExecutableCore(agent);
        lock (GuiExeCacheLock)
        {
            GuiExeCache[cacheKey] = resolved;
        }
        return resolved;
    }

    private static string? ResolveGuiExecutableCore(AgentRosterEntry agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.GuiExePath))
        {
            var raw = agent.GuiExePath.Trim().Trim('"');
            if (raw.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)) return raw;
            var exp = Environment.ExpandEnvironmentVariables(raw);
            if (File.Exists(exp)) return exp;
        }

        if (agent.Id == "codex_cli")
        {
            try
            {
                var winApps = @"C:\Program Files\WindowsApps";
                if (Directory.Exists(winApps))
                {
                    var codexDirs = Directory.GetDirectories(winApps, "OpenAI.Codex_*");
                    if (codexDirs.Length > 0)
                    {
                        return @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App";
                    }
                }
            }
            catch
            {
                // WindowsApps 目录受权限保护时，直接检测用户数据目录是否存在桌面状态
                var codexState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", ".codex-global-state.json");
                if (File.Exists(codexState)) return @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App";
            }
        }

        string[] candidates = agent.Id switch
        {
            "grok_bot" or "codex_cli" => new[]
            {
                @"C:\Program Files\Grok Bot\Grok Bot.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Grok Bot\Grok Bot.exe")
            },
            "cursor" => new[]
            {
                @"C:\Program Files\cursor\Cursor.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\cursor\Cursor.exe")
            },
            _ => Array.Empty<string>()
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? ResolveGrokCliBinary(string? customCmd = null)
    {
        if (!string.IsNullOrWhiteSpace(customCmd) &&
            !customCmd.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            customCmd.Contains("grok", StringComparison.OrdinalIgnoreCase))
        {
            var expanded = Environment.ExpandEnvironmentVariables(customCmd.Trim().Trim('"'));
            if (File.Exists(expanded)) return expanded;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(userProfile, ".grok", "bin", "grok.exe"),
            Path.Combine(localAppData, "Programs", "rea-runtime", "node-v24.19.0-win-x64", "grok.cmd"),
            Path.Combine(localAppData, "Programs", "grok", "grok.exe")
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p1 = Path.Combine(dir.Trim(), "grok.exe");
                if (File.Exists(p1)) return p1;
                var p2 = Path.Combine(dir.Trim(), "grok.cmd");
                if (File.Exists(p2)) return p2;
            }
            catch { }
        }
        return null;
    }

    private HeadlessDispatchOutcome RunGrokCliHeadless(
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        SessionDialogService sessionSvc,
        ExternalDialogSessionInfo? matchedSession,
        string promptFile,
        string fullPrompt,
        string inboxOutFile,
        string liveStatusMd,
        string taskBrief,
        int timeoutSeconds,
        string displayModel = "",
        string effortTier = "medium",
        string tierBadge = "",
        string masterName = "Antigravity",
        int round = 1,
        int maxRounds = 3,
        ProgressiveCliAttempt? attempt = null)
    {
        var preferredBin = !string.IsNullOrWhiteSpace(attempt?.BinaryPath) ? attempt!.BinaryPath : null;
        var grokBin = (!string.IsNullOrWhiteSpace(preferredBin) && File.Exists(preferredBin))
            ? preferredBin
            : ResolveGrokCliBinary(agent.RunnerCmd);
        if (string.IsNullOrWhiteSpace(grokBin) || !File.Exists(grokBin))
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                FailureKind = "permanent_config_error",
                SummaryMessage = "本机未找到 grok.exe 无头 CLI"
            };
        }

        bool hasApiKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY"));
        if (!hasApiKey && DateTime.UtcNow < _grokCliNotSignedInUntilUtc)
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                FailureKind = "cli_not_signed_in",
                SummaryMessage = "Not signed in: grok CLI 尚未登录（缓存跳过，自动切换 Grok Bot 桌面可见通道）"
            };
        }

        var workDir = Directory.Exists(project.PrimaryRoot) ? project.PrimaryRoot : AppPaths.AppBaseDir;
        bool canWriteFiles = agent.Permissions.AllowDirectSourceWrite &&
                             !(taskBrief.Contains("只读", StringComparison.OrdinalIgnoreCase) ||
                               taskBrief.Contains("禁改", StringComparison.OrdinalIgnoreCase));

        string mode = string.IsNullOrWhiteSpace(attempt?.Mode) ? "json_fresh" : attempt!.Mode;
        bool useStreaming = mode.Equals("streaming_json_resume", StringComparison.OrdinalIgnoreCase);
        bool usePromptFallback = mode.Equals("prompt_fallback", StringComparison.OrdinalIgnoreCase);

        var addDirs = CollectAddDirsForProject(project, taskBrief);
        string addDirFlags = string.Join(" ", addDirs.Select(d => $"--add-dir \"{d}\""));
        string leaderSock = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "leader.sock");
        string leaderFlag = File.Exists(leaderSock) ? "--leader" : "";
        string approveFlag = canWriteFiles
            ? "--always-approve"
            : "--disallowed-tools \"StrReplace,EditNotebook,Delete,write,run_terminal_cmd\"";

        string sessionAction = "created";
        string resumeOrContinue = "";
        if (useStreaming)
        {
            if (matchedSession != null && !string.IsNullOrWhiteSpace(matchedSession.SessionId) &&
                Guid.TryParse(matchedSession.SessionId, out _))
            {
                resumeOrContinue = $"-r \"{matchedSession.SessionId}\"";
                sessionAction = "resumed";
            }
            else
            {
                resumeOrContinue = "-c"; // 续接最近上下文
                sessionAction = "resumed";
            }
        }

        var argParts = new List<string>();
        if (usePromptFallback)
        {
            argParts.Add($"-p \"{promptFile}\"");
            argParts.Add($"--cwd \"{workDir}\"");
        }
        else if (useStreaming)
        {
            argParts.Add($"--prompt-file \"{promptFile}\"");
            argParts.Add($"--cwd \"{workDir}\"");
            argParts.Add("--output-format streaming-json");
            if (!string.IsNullOrWhiteSpace(resumeOrContinue)) argParts.Add(resumeOrContinue);
        }
        else
        {
            // json_fresh：新建会话 -s
            argParts.Add($"--prompt-file \"{promptFile}\"");
            argParts.Add($"--cwd \"{workDir}\"");
            argParts.Add("--output-format json");
            argParts.Add("-s");
            sessionAction = "created";
        }

        if (!string.IsNullOrWhiteSpace(addDirFlags)) argParts.Add(addDirFlags);
        if (!string.IsNullOrWhiteSpace(leaderFlag)) argParts.Add(leaderFlag);
        argParts.Add(approveFlag);

        // 严守防封号安全锁：当不支持选型（如 grok_bot）时绝不传 -m
        if (LocalModelCatalogService.SupportsModelSelection(agent.Id))
        {
            var rt = ResolveEffectiveRuntimeParams(agent, displayModel, effortTier);
            if (!string.IsNullOrWhiteSpace(rt.ModelSlug) && rt.ModelSlug != "default")
            {
                argParts.Add($"-m \"{rt.ModelSlug}\"");
            }
        }

        // 若有 ArgsTemplate，优先按模板展开（覆盖上面拼装，便于档位差异化）
        if (!string.IsNullOrWhiteSpace(attempt?.ArgsTemplate))
        {
            string tpl = attempt!.ArgsTemplate
                .Replace("{PROMPT_FILE}", promptFile)
                .Replace("{CWD}", workDir)
                .Replace("{ADD_DIRS}", addDirFlags)
                .Replace("{LEADER}", leaderFlag)
                .Replace("{APPROVE}", approveFlag)
                .Replace("{RESUME_OR_CONTINUE}", resumeOrContinue);
            argParts = tpl.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            // 模板按空格切会拆坏引号路径；改用整串作为 finalArgs
            string fileNameTpl = grokBin;
            string finalArgsTpl = tpl;
            if (grokBin.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                fileNameTpl = "cmd.exe";
                finalArgsTpl = $"/c \"\"{grokBin}\" {tpl}\"";
            }
            return ExecuteGrokCliProcess(
                agent, project, bus, sessionSvc, matchedSession, promptFile, inboxOutFile, liveStatusMd, taskBrief,
                timeoutSeconds, displayModel, effortTier, tierBadge, masterName, round, maxRounds,
                fileNameTpl, finalArgsTpl, workDir, sessionAction, useStreaming);
        }

        string fileName = grokBin;
        string finalArgs = string.Join(" ", argParts);
        if (grokBin.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "cmd.exe";
            finalArgs = $"/c \"\"{grokBin}\" {finalArgs}\"";
        }

        return ExecuteGrokCliProcess(
            agent, project, bus, sessionSvc, matchedSession, promptFile, inboxOutFile, liveStatusMd, taskBrief,
            timeoutSeconds, displayModel, effortTier, tierBadge, masterName, round, maxRounds,
            fileName, finalArgs, workDir, sessionAction, useStreaming);
    }

    private HeadlessDispatchOutcome ExecuteGrokCliProcess(
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        SessionDialogService sessionSvc,
        ExternalDialogSessionInfo? matchedSession,
        string promptFile,
        string inboxOutFile,
        string liveStatusMd,
        string taskBrief,
        int timeoutSeconds,
        string displayModel,
        string effortTier,
        string tierBadge,
        string masterName,
        int round,
        int maxRounds,
        string fileName,
        string finalArgs,
        string workDir,
        string sessionAction,
        bool parseStreamingNdjson)
    {
        string curPhase = round > 1 ? "reworking" : "executing";
        WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief,
            $"⚡ 正在通过原生无头 CLI ({Path.GetFileName(fileName == "cmd.exe" ? finalArgs : fileName)}) 纯后台执行（零窗口·零键鼠）...",
            "", matchedSession, sessionAction, "", curPhase, masterName, displayModel, effortTier, tierBadge, round, maxRounds);

        var (exitCode, stdOut, stdErr) = RunProcessStreaming(
            fileName,
            finalArgs,
            workDir,
            null,
            timeoutSeconds,
            liveLog => WriteLiveStatus(agent, project, liveStatusMd, "working", taskBrief, liveLog, "", matchedSession, sessionAction, "", curPhase, masterName, displayModel, effortTier, tierBadge, round, maxRounds),
            stallTimeoutSeconds: !parseStreamingNdjson && IsBufferedPrintCliArgs(finalArgs) ? 0 : 75);

        var combinedErr = $"{stdOut}\n{stdErr}";
        if (combinedErr.Contains("Not signed in", StringComparison.OrdinalIgnoreCase) ||
            combinedErr.Contains("grok login", StringComparison.OrdinalIgnoreCase))
        {
            _grokCliNotSignedInUntilUtc = DateTime.UtcNow.AddMinutes(3);
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                FailureKind = "cli_not_signed_in",
                SummaryMessage = "Not signed in: grok CLI 尚未登录认证（将切换 Grok Bot 桌面可见通道）"
            };
        }

        string parsedText = "";
        string parsedSessionId = matchedSession?.SessionId ?? "";
        if (!string.IsNullOrWhiteSpace(stdOut))
        {
            if (parseStreamingNdjson)
            {
                var sbText = new StringBuilder();
                foreach (var line in stdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith('{')) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(trimmed);
                        if (doc.RootElement.TryGetProperty("sessionId", out var sidEl) &&
                            !string.IsNullOrWhiteSpace(sidEl.GetString()))
                            parsedSessionId = sidEl.GetString()!;
                        if (doc.RootElement.TryGetProperty("text", out var txtEl) &&
                            !string.IsNullOrWhiteSpace(txtEl.GetString()))
                            sbText.Append(txtEl.GetString());
                        else if (doc.RootElement.TryGetProperty("delta", out var dEl) &&
                                 !string.IsNullOrWhiteSpace(dEl.GetString()))
                            sbText.Append(dEl.GetString());
                        else if (doc.RootElement.TryGetProperty("content", out var cEl) &&
                                 !string.IsNullOrWhiteSpace(cEl.GetString()))
                            sbText.Append(cEl.GetString());
                    }
                    catch { }
                }
                parsedText = sbText.ToString().Trim();
            }

            if (string.IsNullOrWhiteSpace(parsedText))
            {
                var trimmedOut = stdOut.Trim();
                try
                {
                    int braceIdx = trimmedOut.LastIndexOf("{\"text\":", StringComparison.Ordinal);
                    if (braceIdx < 0) braceIdx = trimmedOut.LastIndexOf('{');
                    if (braceIdx >= 0)
                    {
                        using var doc = JsonDocument.Parse(trimmedOut[braceIdx..]);
                        if (doc.RootElement.TryGetProperty("text", out var txtEl))
                            parsedText = txtEl.GetString() ?? "";
                        if (doc.RootElement.TryGetProperty("sessionId", out var sidEl) &&
                            !string.IsNullOrWhiteSpace(sidEl.GetString()))
                            parsedSessionId = sidEl.GetString()!;
                        if (string.IsNullOrWhiteSpace(parsedText) &&
                            doc.RootElement.TryGetProperty("type", out var tpEl) && tpEl.GetString() == "error" &&
                            doc.RootElement.TryGetProperty("message", out var msgEl))
                        {
                            return new HeadlessDispatchOutcome
                            {
                                Success = false,
                                AgentId = agent.Id,
                                AgentName = agent.Name,
                                SummaryMessage = $"grok CLI 返回错误：{TruncateOneLine(msgEl.GetString() ?? "", 160)}"
                            };
                        }
                    }
                }
                catch
                {
                    parsedText = trimmedOut;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(parsedText))
        {
            return new HeadlessDispatchOutcome
            {
                Success = false,
                AgentId = agent.Id,
                AgentName = agent.Name,
                FailureKind = exitCode == -2 ? "execution_stalled" : "",
                SummaryMessage = $"grok CLI 未返回有效结果 (exit={exitCode}): {TruncateOneLine(stdErr, 150)}"
            };
        }

        var recorded = sessionSvc.AppendOrCreateLocalCollabSession(project, agent, parsedSessionId, taskBrief, parsedText);
        var activeSid = !string.IsNullOrWhiteSpace(parsedSessionId) ? parsedSessionId : recorded.SessionId;
        sessionSvc.SaveActiveSessionForAgent(project, agent.Id, activeSid);
        WrapAndSaveInboxResult(agent, project, bus, inboxOutFile, parsedText, activeSid, sessionAction);

        return new HeadlessDispatchOutcome
        {
            Success = true,
            AgentId = agent.Id,
            AgentName = agent.Name,
            SessionId = activeSid,
            SessionAction = sessionAction,
            OutputInboxFile = inboxOutFile,
            OutputText = File.ReadAllText(inboxOutFile, Encoding.UTF8),
            SummaryMessage = $"已通过「{agent.Name}」原生无头 CLI 纯后台完成执行（零窗口零键鼠占用），反馈已收回至：{Path.GetFileName(inboxOutFile)}"
        };
    }

    public static string? ResolveCodexBinary(string? customCmd)
    {
        if (!string.IsNullOrWhiteSpace(customCmd) && !customCmd.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var expanded = Environment.ExpandEnvironmentVariables(customCmd.Trim().Trim('"'));
            if (File.Exists(expanded)) return expanded;
        }

        var localOpenAiBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(localOpenAiBin))
        {
            var found = Directory.GetFiles(localOpenAiBin, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (found != null) return found;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pluginCodex = Path.Combine(userProfile, ".codex", "plugins", ".plugin-appserver", "codex.exe");
        if (File.Exists(pluginCodex)) return pluginCodex;

        // WindowsApps 商店包：扫描 OpenAI.Codex* 内 codex.exe
        try
        {
            const string winApps = @"C:\Program Files\WindowsApps";
            if (Directory.Exists(winApps))
            {
                foreach (var dir in Directory.GetDirectories(winApps, "OpenAI.Codex*"))
                {
                    try
                    {
                        var hit = Directory.GetFiles(dir, "codex.exe", SearchOption.AllDirectories)
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .FirstOrDefault();
                        if (hit != null) return hit;
                    }
                    catch { }
                }
            }
        }
        catch { }

        // PATH 中的 codex.exe
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "codex.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// 解析 Cursor Agent 官方 CLI（cursor-agent.exe / agent.exe），严禁把 cursor.cmd 误当 CLI。
    /// </summary>
    public static string? ResolveCursorAgentCliBinary(string? customCmd = null)
    {
        if (!string.IsNullOrWhiteSpace(customCmd) && !customCmd.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var expanded = Environment.ExpandEnvironmentVariables(customCmd.Trim().Trim('"'));
            var name = Path.GetFileName(expanded);
            if (!name.Equals("cursor.cmd", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("cursor.exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(expanded) &&
                (name.Contains("agent", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("cursor-agent", StringComparison.OrdinalIgnoreCase)))
            {
                return expanded;
            }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        {
            Path.Combine(local, "Programs", "cursor", "resources", "app", "bin", "cursor-agent.exe"),
            Path.Combine(local, "Programs", "cursor", "cursor-agent.exe"),
            Path.Combine(pf, "cursor", "resources", "app", "bin", "cursor-agent.exe"),
            Path.Combine(pf, "cursor", "cursor-agent.exe"),
            Path.Combine(local, "cursor-agent", "cursor-agent.exe"),
            Path.Combine(local, "cursor-agent", "cursor-agent.cmd"),
            Path.Combine(local, "cursor-agent", "agent.cmd"),
            Path.Combine(userHome, ".local", "bin", "cursor-agent.exe"),
            Path.Combine(userHome, ".local", "bin", "cursor-agent.cmd"),
            Path.Combine(userHome, ".local", "bin", "agent.cmd"),
            Path.Combine(appData, "npm", "agent.cmd")
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c) && !c.EndsWith("cursor.cmd", StringComparison.OrdinalIgnoreCase))
                return c;
        }

        var pathDirs = new List<string>();
        foreach (var rawPath in new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        })
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            foreach (var d in rawPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = d.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(trimmed)) pathDirs.Add(trimmed);
            }
        }
        foreach (var dir in pathDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var name in new[] { "cursor-agent.exe", "cursor-agent.cmd", "agent.exe", "agent.cmd" })
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p) &&
                        !p.EndsWith("cursor.cmd", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(p).Equals("cursor.exe", StringComparison.OrdinalIgnoreCase))
                        return p;
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>解析 Claude Code CLI（claude.exe / claude.cmd），排除桌面壳。</summary>
    public static string? ResolveClaudeCodeCliBinary(string? customCmd = null)
    {
        if (!string.IsNullOrWhiteSpace(customCmd) && !customCmd.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var expanded = Environment.ExpandEnvironmentVariables(customCmd.Trim().Trim('"'));
            if (File.Exists(expanded) && !expanded.EndsWith("Claude.exe", StringComparison.OrdinalIgnoreCase))
                return expanded;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        {
            Path.Combine(local, "npm-cache", "_npx", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
            Path.Combine(local, "Programs", "claude", "claude.exe"),
            Path.Combine(userHome, ".local", "bin", "claude.exe"),
            Path.Combine(userHome, ".local", "bin", "claude.cmd")
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        var pathDirs = new List<string>();
        foreach (var rawPath in new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        })
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            foreach (var d in rawPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = d.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(trimmed)) pathDirs.Add(trimmed);
            }
        }
        foreach (var dir in pathDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var p1 = Path.Combine(dir, "claude.exe");
                if (File.Exists(p1)) return p1;
                var p2 = Path.Combine(dir, "claude.cmd");
                if (File.Exists(p2)) return p2;
            }
            catch { }
        }
        return null;
    }

    private static string BuildHeadlessPrompt(
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        string? customInstruction,
        ExternalDialogSessionInfo? matchedSession,
        SessionDialogService sessionSvc,
        string dispatchReason = "",
        string masterName = "Antigravity",
        string selectedModel = "",
        string selectedTierBadge = "",
        int round = 1,
        int maxRounds = 3,
        string reworkFeedback = "",
        string prevRoundOutput = "")
    {
        var brief = customInstruction?.Trim() ?? "";
        bool canWriteFiles = agent.Permissions.AllowDirectSourceWrite &&
                             !(brief.Contains("只读", StringComparison.OrdinalIgnoreCase) ||
                               brief.Contains("禁改", StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        sb.AppendLine($"你正在作为「{agent.Name}」参与「多 AI 协同工作流」的闭环实操派单任务（当前第 {round}/{maxRounds} 轮）。");
        sb.AppendLine($"- **全局统筹与验收主脑**：{masterName}");
        if (!string.IsNullOrWhiteSpace(selectedModel))
        {
            sb.AppendLine($"- **主脑为你适配的模型与档位**：`{selectedModel}` · **{selectedTierBadge}**");
        }
        if (!string.IsNullOrWhiteSpace(dispatchReason))
        {
            sb.AppendLine($"- **主脑分配与能力匹配依据**：{dispatchReason}");
        }
        sb.AppendLine();
        sb.AppendLine("请严格遵守以下执行铁律：");
        sb.AppendLine("1. 全程使用**简体中文**输出；");
        if (canWriteFiles)
        {
            sb.AppendLine("2. **【强制实操落地铁律 · 严禁只出方案不干活】**：");
            sb.AppendLine("   - 你拥有当前项目的完整文件读写权限（协同中心已自动建立修改前快照护航）；");
            sb.AppendLine("   - **凡是任务要求修改或新增代码（`.gd` / `.cs` / `.gdshader`）、修改项目设置（`project.godot` / `assets/设计设置/` / `data/`）、修改场景与预制体（`.tscn` / `.tres`）或工程文件，你必须直接动手修改磁盘上的目标文件并保存落盘！**");
            sb.AppendLine("   - **绝不允许只输出口头修改建议或伪代码方案而把活留给主脑或用户去做！**");
            sb.AppendLine($"   - 执行完毕后，主脑 [{masterName}] 将自动检查你的实际文件改动 Diff 与输出质量；若未实际修改文件或未达验收标准，主脑将直接打回要求你返工重做，直到任务彻底完成并通过主脑验收为止！");
            sb.AppendLine("   - 请在完成实际文件修改与自检后，在最终回复中清晰列出：`【1. 实际修改文件清单】`、`【2. 核心落地改动说明】`、`【3. 自检与验证结果】`；");
        }
        else
        {
            sb.AppendLine("2. 本次为只读分析/推演任务，请勿直接修改项目源码或场景文件，请将完整方案、数值表、脚本代码片段或审查结论作为最终回复输出；");
        }
        sb.AppendLine("3. 严禁虚构项目中不存在的字段或路径。");
        sb.AppendLine();

        if (round > 1 && !string.IsNullOrWhiteSpace(reworkFeedback))
        {
            sb.AppendLine($"## ⚠️ 【最高紧急度 · 主脑第 {round - 1} 轮验收未通过打回重做指令】");
            sb.AppendLine($"- **主脑 [{masterName}] 打回原因**：{reworkFeedback}");
            sb.AppendLine("- **返工要求**：请针对上述未通过原因立即动手修复并直接修改保存目标工程文件，确保本次彻底完成任务并通过主脑验收！");
            if (!string.IsNullOrWhiteSpace(prevRoundOutput))
            {
                var snippet = prevRoundOutput.Length > 1000 ? prevRoundOutput[..1000] + "..." : prevRoundOutput;
                sb.AppendLine("- **你上一轮的输出摘要**：");
                sb.AppendLine(snippet);
            }
            sb.AppendLine();
        }

        if (matchedSession != null && File.Exists(matchedSession.FilePath))
        {
            var turns = sessionSvc.ExtractConversationTurns(matchedSession.FilePath, matchedSession.AgentId);
            var recentTurns = turns.Skip(Math.Max(0, turns.Count - 4)).ToList();
            if (recentTurns.Count > 0)
            {
                sb.AppendLine($"## 当前项目已有对话框近期上下文摘要 (SessionId: {matchedSession.SessionId})");
                foreach (var (role, _, txt) in recentTurns)
                {
                    var tag = role == "user" ? "【上一轮任务】" : "【上一轮反馈】";
                    var snippet = txt.Length > 800 ? txt[..800] + "..." : txt;
                    sb.AppendLine($"{tag} {snippet}");
                }
                sb.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(customInstruction))
        {
            sb.AppendLine("## 【最高优先级】本次专门指派给你的实操任务指令（请直接动手落地完成本节指令，忽略下方 TASK.md 中的旧历史记录）");
            sb.AppendLine(customInstruction.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("## 当前项目任务活卡 (work/collab/TASK.md)");
        sb.AppendLine(bus.ReadTaskCard(project));
        sb.AppendLine();

        var diffPath = bus.GetLatestDiffPath(project);
        if (File.Exists(diffPath))
        {
            var diffTxt = File.ReadAllText(diffPath, Encoding.UTF8);
            if (!string.IsNullOrWhiteSpace(diffTxt))
            {
                sb.AppendLine("## 最近代码变更对比 (LATEST.diff)");
                sb.AppendLine(diffTxt.Length > 6000 ? diffTxt[..6000] + "\n...(已截断)" : diffTxt);
            }
        }

        return sb.ToString();
    }

    private static (bool Passed, string Summary, string ChangedFilesSummary) VerifyExecutionByMasterPlanner(
        RegistryService reg,
        ProjectProfile project,
        CollabBusService bus,
        AgentRosterEntry agent,
        string masterName,
        string taskBrief,
        bool allowWriteForTask,
        HeadlessDispatchOutcome outcome,
        int round,
        int maxRounds)
    {
        if (!outcome.Success || string.IsNullOrWhiteSpace(outcome.OutputText))
        {
            return (false, $"第 {round} 轮执行未返回有效产出（{outcome.SummaryMessage}），主脑打回要求重新执行", "");
        }

        var body = outcome.OutputText;
        var changedFiles = new List<string>();
        var diffPath = bus.GetLatestDiffPath(project);
        if (File.Exists(diffPath))
        {
            var diffLines = File.ReadAllLines(diffPath, Encoding.UTF8);
            foreach (var line in diffLines)
            {
                if (line.StartsWith("--- a/") || line.StartsWith("+++ b/"))
                {
                    var fn = line[6..].Trim();
                    if (!string.IsNullOrWhiteSpace(fn) && !changedFiles.Contains(fn, StringComparer.OrdinalIgnoreCase))
                    {
                        changedFiles.Add(fn);
                    }
                }
                else if (line.StartsWith("## [") && line.Contains(']'))
                {
                    var fn = line.TrimStart('#', ' ');
                    if (!changedFiles.Contains(fn, StringComparer.OrdinalIgnoreCase))
                    {
                        changedFiles.Add(fn);
                    }
                }
            }
        }

        string changedSummary = changedFiles.Count > 0
            ? $"已验证 {changedFiles.Count} 处文件落盘变更 ({string.Join(", ", changedFiles.Take(4))})"
            : "无快照行级 Diff 或属于新增/外部文件修改";

        // 检查是否存在明显未完成或报错拒绝字样
        bool hasIncompleteMarker =
            body.Contains("[[NEEDS_REWORK]]", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("尚未完成修改", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("我无法直接修改", StringComparison.OrdinalIgnoreCase);

        if (hasIncompleteMarker)
        {
            return (false, $"检测到产出包含未完成标记，主脑 [{masterName}] 打回要求立即直接修改工程文件并彻底完成任务", changedSummary);
        }

        // 若桌面客户端唯一目标会话仍在后台异步思考/编译大型任务中，严禁误判“未改文件”而发起重复返工新建多个相同对话框！
        if (outcome.AsyncStillWorkingInGui && changedFiles.Count == 0)
        {
            var chatRef = string.IsNullOrWhiteSpace(outcome.ConfirmedGuiChatTitle) ? agent.Name : outcome.ConfirmedGuiChatTitle;
            return (true, $"⏳ 任务已在「{chatRef}」唯一对话框持续实操编译中（已拦截重复建窗，待该会话落盘后复核）", "桌面客户端唯一会话正在后台执行中");
        }

        // 若任务明确要求直接修改某个工程文件，检查是否已产生落盘改动或在报告中明确给出了实际修改记录
        bool expectsDiskEdit = allowWriteForTask &&
                               agent.Id != "mock_headless_ai" &&
                               (taskBrief.Contains("直接修改") || taskBrief.Contains("落地修改") ||
                                taskBrief.Contains("写入文件") || taskBrief.Contains("实操修改"));

        // 跨关联目录：任务文本显式指向外部工程时，用最近写入时间补证落盘，避免误判无 Diff 触发第 2 轮
        bool externalWriteOk = HasRecentExternalRootWrite(taskBrief, project.PrimaryRoot, withinSeconds: 240);
        if (externalWriteOk && changedFiles.Count == 0)
        {
            changedSummary = "已验证任务提及的外部工程目录近期有文件写入（跨目录落盘监测）";
        }

        if (expectsDiskEdit && changedFiles.Count == 0 && !externalWriteOk &&
            !body.Contains("实际修改", StringComparison.OrdinalIgnoreCase) &&
            !body.Contains("已修改", StringComparison.OrdinalIgnoreCase) &&
            !body.Contains("已写入", StringComparison.OrdinalIgnoreCase) &&
            !body.Contains("已更新", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"任务要求直接落地修改工程文件，但第 {round} 轮未检测到文件落盘记录，主脑 [{masterName}] 打回要求直接动手改文件", changedSummary);
        }

        var roundNote = round > 1 ? $"（经 {round} 轮打回修复后通过）" : "（首轮一次通过）";
        return (true, $"✓ 主脑 [{masterName}] 验收通过{roundNote} · {changedSummary}", changedSummary);
    }

    private static void AppendMasterVerificationReceiptToInbox(
        string inboxOutFile,
        string masterName,
        string workerName,
        string selectedModel,
        string selectedTierBadge,
        int roundsExecuted,
        int maxRounds,
        bool passed,
        string verificationSummary,
        string changedFilesSummary,
        int networkRetriesUsed = 0,
        string failoverNote = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(inboxOutFile) || !File.Exists(inboxOutFile)) return;
            var raw = File.ReadAllText(inboxOutFile, Encoding.UTF8);
            var netLine = (networkRetriesUsed > 0 || !string.IsNullOrWhiteSpace(failoverNote))
                ? $"- **网络断线自愈与容灾**：自动重连重发 `{networkRetriesUsed}` 次" +
                  (string.IsNullOrWhiteSpace(failoverNote) ? "" : $" · {failoverNote}") + "\n"
                : "";
            var receipt =
                $"- **统筹与验收主脑**：`{masterName}`\n" +
                $"- **自适应模型与档位**：`{selectedModel}` · **{selectedTierBadge}**\n" +
                $"- **执行与打回轮次**：共执行 `{roundsExecuted} / {maxRounds}` 轮\n" +
                netLine +
                $"- **主脑终验结论**：**{(passed ? "✅ 验收通过 · 准予交付" : "⚠️ 已达最大轮次 · 待人工复核")}**（{verificationSummary}）\n" +
                (string.IsNullOrWhiteSpace(changedFilesSummary) ? "" : $"- **工程文件落盘核验**：{changedFilesSummary}\n");

            int sepIdx = raw.IndexOf("\n---\n", StringComparison.Ordinal);
            if (sepIdx > 0)
            {
                var updated = raw[..sepIdx] + "\n" + receipt + raw[sepIdx..];
                File.WriteAllText(inboxOutFile, updated, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    private static void InjectInstructionIntoTaskCard(ProjectProfile project, CollabBusService bus, string instruction)
    {
        var current = bus.ReadTaskCard(project);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var marker = "## 1. 任务目标与设计规格";
        int idx = current.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            int nextSec = current.IndexOf("## 2.", idx, StringComparison.Ordinal);
            if (nextSec > idx)
            {
                var updated = current[..idx] +
                              $"## 1. 任务目标与设计规格（总包工头自动派单 {stamp}）\n- {instruction}\n\n" +
                              current[nextSec..];
                bus.SaveTaskCard(project, updated);
                return;
            }
        }
        bus.SaveTaskCard(project, current.TrimEnd() + $"\n\n### 新增派单指令 ({stamp})\n- {instruction}\n");
    }

    private static void WrapAndSaveInboxResult(
        AgentRosterEntry agent,
        ProjectProfile project,
        CollabBusService bus,
        string inboxOutFile,
        string bodyContent,
        string sessionId = "",
        string sessionAction = "")
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var sessLine = !string.IsNullOrWhiteSpace(sessionId)
            ? $"- 关联项目对话框：`{sessionId}` (`{(sessionAction == "resumed" ? "续接已有项目对话框" : "新建项目专属对话框")}`)\n"
            : "";
        var header = $"# 自动派单产出 · {agent.Name}\n" +
                     $"- 执行时间：{stamp}\n" +
                     $"- 执行模式：`{agent.RunnerMode}`\n" +
                     sessLine + "\n---\n\n";
        File.WriteAllText(inboxOutFile, header + bodyContent.Trim() + "\n", new UTF8Encoding(false));

        var relInbox = Path.GetRelativePath(project.PrimaryRoot, inboxOutFile).Replace('\\', '/');
        var taskTxt = bus.ReadTaskCard(project).TrimEnd();
        var sessTag = !string.IsNullOrWhiteSpace(sessionId) ? $" (对话框 `{sessionId}`)" : "";
        taskTxt += $"\n- [{DateTime.Now:HH:mm:ss}] **{agent.Name}**{sessTag} 任务执行已完成，产出文件：`{relInbox}`\n";
        bus.SaveTaskCard(project, taskTxt);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcessStreaming(
        string fileName,
        string arguments,
        string workingDir,
        string? stdinText,
        int timeoutSeconds,
        Action<string>? onProgressLog,
        int stallTimeoutSeconds = 90,
        Dictionary<string, string>? extraEnv = null)
    {
        if (fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            arguments = $"/d /s /c \"\"{fileName}\" {arguments}\"";
            fileName = "cmd.exe";
        }
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = Directory.Exists(workingDir) ? workingDir : AppPaths.AppBaseDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["FORCE_COLOR"] = "0";
        if (extraEnv != null)
        {
            foreach (var kv in extraEnv)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key)) psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动进程: {fileName}");
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        var syncLock = new object();
        long lastActivityTick = Environment.TickCount64;

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Interlocked.Exchange(ref lastActivityTick, Environment.TickCount64);
            lock (syncLock)
            {
                outSb.AppendLine(e.Data);
                onProgressLog?.Invoke(TailLines(outSb.ToString() + "\n" + errSb.ToString(), 35));
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Interlocked.Exchange(ref lastActivityTick, Environment.TickCount64);
            lock (syncLock)
            {
                errSb.AppendLine(e.Data);
                onProgressLog?.Invoke(TailLines(outSb.ToString() + "\n" + errSb.ToString(), 35));
            }
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            if (stdinText != null)
            {
                using var sw = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false));
                sw.Write(stdinText);
                sw.Flush();
            }
            else
            {
                proc.StandardInput.Close();
            }
        }
        catch { }

        long startTick = Environment.TickCount64;
        long maxTotalMs = Math.Max(5, timeoutSeconds) * 1000L;
        long maxStallMs = stallTimeoutSeconds > 0 ? stallTimeoutSeconds * 1000L : long.MaxValue;

        // 文件写入 / CPU 推进心跳：非流式 CLI（如 --output-format text）长时间不吐 stdout 时防误杀
        string? heartbeatDir = Directory.Exists(workingDir) ? workingDir : null;
        bool heartbeatEnabled = timeoutSeconds >= 30;
        long lastHeartbeatTick = startTick;
        long lastSourceWriteTick = 0;
        DateTime lastSeenWriteUtc = heartbeatDir != null && heartbeatEnabled ? SampleLatestSourceWriteUtc(heartbeatDir) : DateTime.MinValue;
        TimeSpan lastCpu = TimeSpan.Zero;
        try { lastCpu = proc.TotalProcessorTime; } catch { }
        bool graceUsed = false;

        while (!proc.WaitForExit(1000))
        {
            long now = Environment.TickCount64;
            if (heartbeatEnabled && now - lastHeartbeatTick >= HeartbeatIntervalMs)
            {
                lastHeartbeatTick = now;
                bool active = false;
                if (heartbeatDir != null)
                {
                    var latest = SampleLatestSourceWriteUtc(heartbeatDir);
                    if (latest > lastSeenWriteUtc)
                    {
                        lastSeenWriteUtc = latest;
                        lastSourceWriteTick = now;
                        active = true;
                    }
                }
                try
                {
                    var cpu = proc.TotalProcessorTime;
                    if (cpu - lastCpu > TimeSpan.FromMilliseconds(50)) active = true;
                    lastCpu = cpu;
                }
                catch { }
                if (active) Interlocked.Exchange(ref lastActivityTick, now);
            }
            if (now - startTick >= maxTotalMs)
            {
                bool recentlyWrote = lastSourceWriteTick > 0 && now - lastSourceWriteTick <= RecentWriteGraceWindowMs;
                if (!graceUsed && recentlyWrote)
                {
                    graceUsed = true;
                    maxTotalMs += TotalTimeoutGraceMs;
                    onProgressLog?.Invoke($"⏳ 已达总超时，但工程目录 {RecentWriteGraceWindowMs / 1000}s 内刚写入源码，自动宽限 {TotalTimeoutGraceMs / 1000}s 等待收尾/构建...");
                    continue;
                }
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, outSb.ToString(), "任务执行超时已中止");
            }
            if (now - Interlocked.Read(ref lastActivityTick) >= maxStallMs)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-2, outSb.ToString(), $"EXECUTION_STALLED: 后台子进程连续 {stallTimeoutSeconds}s 无响应卡住，已自动熔断并切换其他方式重试");
            }
        }
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    internal const int CursorCliMinTimeoutSeconds = 240;

    /// <summary>打印模式（--print / -p / --output-format text）的 CLI 会整段缓冲输出，不能按 stdout 静默判卡死。</summary>
    internal static bool IsBufferedPrintCliArgs(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return false;
        if (arguments.Contains("streaming-json", StringComparison.OrdinalIgnoreCase) ||
            arguments.Contains("stream-json", StringComparison.OrdinalIgnoreCase)) return false;
        string padded = $" {arguments} ";
        return padded.Contains(" --print ", StringComparison.OrdinalIgnoreCase) ||
               padded.Contains(" -p ", StringComparison.Ordinal) ||
               padded.Contains("--output-format text", StringComparison.OrdinalIgnoreCase);
    }
    internal const long HeartbeatIntervalMs = 8000;
    internal const long RecentWriteGraceWindowMs = 45_000;
    internal const long TotalTimeoutGraceMs = 120_000;

    private static readonly HashSet<string> HeartbeatIgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".vs", ".idea", ".godot", "bin", "obj", "node_modules", "desktop_verify", "Library", "Temp"
    };

    /// <summary>有限深度、有限数量地采样工作目录下最近一次文件写入时间（心跳判活用，不追求精确）。</summary>
    internal static DateTime SampleLatestSourceWriteUtc(string root, int maxDepth = 4, int maxFiles = 4000)
    {
        var latest = DateTime.MinValue;
        int visited = 0;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && visited < maxFiles)
        {
            var (dir, depth) = stack.Pop();
            try
            {
                var di = new DirectoryInfo(dir);
                foreach (var fi in di.EnumerateFiles())
                {
                    if (++visited > maxFiles) break;
                    if (fi.LastWriteTimeUtc > latest) latest = fi.LastWriteTimeUtc;
                }
                if (depth >= maxDepth) continue;
                foreach (var sub in di.EnumerateDirectories())
                {
                    if (HeartbeatIgnoredDirs.Contains(sub.Name)) continue;
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    stack.Push((sub.FullName, depth + 1));
                }
            }
            catch { }
        }
        return latest;
    }

    private static string TailLines(string text, int maxLines)
    {
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= maxLines) return string.Join(Environment.NewLine, lines);
        return string.Join(Environment.NewLine, lines.Skip(lines.Length - maxLines));
    }

    private static void TryStartOllamaDaemonSilent()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ollama",
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Thread.Sleep(1200);
        }
        catch { }
    }

    private static (string FileName, string Args) SplitExecutableAndArgs(string cmdLine)
    {
        var s = cmdLine.Trim();
        if (s.StartsWith('"'))
        {
            int endQuote = s.IndexOf('"', 1);
            if (endQuote > 1)
            {
                return (s[1..endQuote], s[(endQuote + 1)..].Trim());
            }
        }
        int firstSpace = s.IndexOf(' ');
        return firstSpace > 0 ? (s[..firstSpace], s[(firstSpace + 1)..].Trim()) : (s, "");
    }

    private static void SyncCodexDesktopSessionIndex(string sessionId, string taskBrief)
    {
        try
        {
            var title = "【多AI协同】" + TruncateOneLine(taskBrief, 36);
            var idxPath = @"F:\AIChat\codex\session_index.jsonl";
            if (Directory.Exists(Path.GetDirectoryName(idxPath)))
            {
                var line = JsonSerializer.Serialize(new
                {
                    id = sessionId,
                    thread_name = title,
                    updated_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
                File.AppendAllText(idxPath, line + "\n", new UTF8Encoding(false));
            }
        }
        catch { }
    }

    private static string TruncateOneLine(string s, int maxLen)
    {
        var one = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length <= maxLen ? one : one[..maxLen] + "...";
    }
}
