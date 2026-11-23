using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;

namespace AICollabHub.Services;

/// <summary>
/// 跨页面重绘仍保留的长操作互斥锁。按钮被拆掉重建后，只要操作键仍占用，新按钮必须继续渲染为禁用态。
/// 仅 <see cref="EndAndRestore"/> 触碰控件树，必须在 UI 线程调用；<see cref="TryBegin"/> / <see cref="End"/> / <see cref="IsBusy"/> 可在任意线程调用。
/// </summary>
public static class LongActionTracker
{
    public const string ScanLocal = "scan_local";
    public const string CloudSync = "cloud_sync";
    public const string ExportShare = "export_share";
    public const string RulesSync = "rules_sync";
    public const string RulesInit = "rules_init";
    public const string RulesRestore = "rules_restore";
    public const string HeadlessDispatch = "headless_dispatch";
    public const string NetProbe = "net_probe";
    public const string DismissModals = "dismiss_modals";
    public const string ResumeCheckpoint = "resume_checkpoint";
    public const string StallResolve = "stall_resolve";
    public const string OpsSnapshot = "ops_snapshot";
    public const string CapsulePack = "capsule_pack";
    public const string ModelCatalogScan = "model_catalog_scan";
    public const string AppUpdate = "app_update";

    public static string CliUpdate(string agentId) =>
        "cli_update:" + (agentId ?? "").Trim().ToLowerInvariant();

    public static string CloudSyncAgent(string agentId) =>
        "cloud_sync:" + (agentId ?? "").Trim().ToLowerInvariant();

    /// <summary>挂在按钮 Tag 上。纯文字按钮恢复 IdleContent；矢量图标 / StackPanel 保留原 Content 对象，忙时只改 ToolTip 与 IsEnabled。</summary>
    public sealed class LongActionTag
    {
        public LongActionTag(string key, string idleContent, object? preservedContent, object? originalToolTip)
        {
            Key = key ?? "";
            IdleContent = idleContent ?? "";
            PreservedContent = preservedContent;
            OriginalToolTip = originalToolTip;
        }

        public string Key { get; }
        public string IdleContent { get; }
        public object? PreservedContent { get; }
        public object? OriginalToolTip { get; }
        public bool KeepVisual => PreservedContent != null;
    }

    private sealed class Entry
    {
        public DateTime StartedUtc;
        public string Verb = "处理中";
    }

    private static readonly ConcurrentDictionary<string, Entry> Busy =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool HasAnyBusy => !Busy.IsEmpty;

    public static bool TryBegin(string key, string? busyVerb = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var entry = new Entry
        {
            StartedUtc = DateTime.UtcNow,
            Verb = string.IsNullOrWhiteSpace(busyVerb) ? "处理中" : busyVerb.Trim()
        };
        return Busy.TryAdd(key.Trim(), entry);
    }

    public static void End(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        Busy.TryRemove(key.Trim(), out _);
    }

    /// <summary>释放锁并按 Tag 把所有窗口里对应按钮恢复成可点空闲文案。必须在 UI 线程调用。</summary>
    public static void EndAndRestore(string key)
    {
        End(key);
        RestoreButtonsInAllWindows(key);
    }

    public static bool IsBusy(string key) =>
        !string.IsNullOrWhiteSpace(key) && Busy.ContainsKey(key.Trim());

    public static int ElapsedSeconds(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !Busy.TryGetValue(key.Trim(), out var entry)) return 0;
        return Math.Max(0, (int)(DateTime.UtcNow - entry.StartedUtc).TotalSeconds);
    }

    public static string PeekVerb(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !Busy.TryGetValue(key.Trim(), out var entry)) return "";
        return entry.Verb ?? "";
    }

    public static string FormatBusyLabel(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !Busy.TryGetValue(key.Trim(), out var entry)) return "";
        var verb = string.IsNullOrWhiteSpace(entry.Verb) ? "处理中" : entry.Verb.Trim();
        int sec = Math.Max(0, (int)(DateTime.UtcNow - entry.StartedUtc).TotalSeconds);
        return $"⏳ {verb} {sec}s...";
    }

    public static string DuplicateClickHint(string key)
    {
        var label = FormatBusyLabel(key);
        return string.IsNullOrEmpty(label)
            ? "该操作正在进行，请勿重复点击。"
            : $"{label} 请勿重复点击。";
    }

    public static void Arm(Button? btn, string key, string? idleContent = null)
    {
        if (btn == null || string.IsNullOrWhiteSpace(key)) return;
        object? content = btn.Content;
        bool visual = content != null && content is not string;
        var idle = idleContent ?? (content as string ?? "");
        btn.Tag = new LongActionTag(key.Trim(), idle, visual ? content : null, btn.ToolTip);
    }

    public static void ShowBusy(Button? btn)
    {
        if (btn?.Tag is not LongActionTag tag || !IsBusy(tag.Key)) return;
        btn.IsEnabled = false;
        var label = FormatBusyLabel(tag.Key);
        if (string.IsNullOrEmpty(label)) return;
        if (tag.KeepVisual)
        {
            btn.ToolTip = label;
            return;
        }
        btn.Content = label;
    }

    public static void ApplyBusyIfNeeded(Button? btn, string key, string idleContent)
    {
        Arm(btn, key, idleContent);
        if (IsBusy(key)) ShowBusy(btn);
    }

    public static void PulseButtonsInAllWindows()
    {
        if (!HasAnyBusy) return;
        VisitAllWindows(btn =>
        {
            if (btn.Tag is not LongActionTag tag || !IsBusy(tag.Key)) return;
            btn.IsEnabled = false;
            var label = FormatBusyLabel(tag.Key);
            if (string.IsNullOrEmpty(label)) return;
            if (tag.KeepVisual)
            {
                if (!Equals(btn.ToolTip, label)) btn.ToolTip = label;
                return;
            }
            if (!string.Equals(btn.Content as string, label, StringComparison.Ordinal))
                btn.Content = label;
        });
    }

    public static void RestoreButtonsInAllWindows(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var wanted = key.Trim();
        VisitAllWindows(btn =>
        {
            if (btn.Tag is not LongActionTag tag) return;
            if (!tag.Key.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return;
            btn.IsEnabled = true;
            if (tag.KeepVisual)
            {
                if (!ReferenceEquals(btn.Content, tag.PreservedContent))
                    btn.Content = tag.PreservedContent;
                btn.ToolTip = tag.OriginalToolTip;
                return;
            }
            btn.Content = tag.IdleContent;
        });
    }

    public static void ResetForTest() => Busy.Clear();

    private static void VisitAllWindows(Action<Button> visit)
    {
        var app = Application.Current;
        if (app == null) return;
        foreach (Window w in app.Windows)
        {
            try { WalkLogical(w, visit); }
            catch { /* 窗口关闭途中跳过 */ }
        }
    }

    private static void WalkLogical(DependencyObject node, Action<Button> visit)
    {
        if (node is Button btn) visit(btn);
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject d)
                WalkLogical(d, visit);
        }
    }
}
