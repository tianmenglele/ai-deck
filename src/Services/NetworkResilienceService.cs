using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using AICollabHub.Models;

namespace AICollabHub.Services;

/// <summary>
/// 本机网络探测、失败分类、退避重连与任务链断点持久化。
/// </summary>
public static class NetworkResilienceService
{
    private static int _simulatedOfflineProbesRemaining;

    /// <summary>
    /// 测试钩子：大于 0 时每次探测减 1 并强制返回本机离线（供隔离自检）。
    /// </summary>
    public static int SimulatedOfflineProbesRemaining
    {
        get => Volatile.Read(ref _simulatedOfflineProbesRemaining);
        set => Volatile.Write(ref _simulatedOfflineProbesRemaining, Math.Max(0, value));
    }

    public static string LastCheckpointPath =>
        Path.Combine(AppPaths.UserDataDir, "last_checkpoint.json");

    public static string GetProjectCheckpointPath(ProjectProfile proj)
    {
        var collabRel = string.IsNullOrWhiteSpace(proj.CollabRelDir)
            ? "work/collab"
            : proj.CollabRelDir.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(proj.PrimaryRoot ?? "", collabRel, "TASK_CHAIN_CHECKPOINT.json");
    }

    public static (bool IsOnline, string Detail) ProbeLocalNetworkHealth(int timeoutMs = 600)
    {
        while (true)
        {
            var remaining = Volatile.Read(ref _simulatedOfflineProbesRemaining);
            if (remaining <= 0) break;
            if (Interlocked.CompareExchange(ref _simulatedOfflineProbesRemaining, remaining - 1, remaining) == remaining)
                return (false, "模拟离线探测（隔离自检钩子）");
        }

        if (!NetworkInterface.GetIsNetworkAvailable())
            return (false, "NetworkInterface 报告本机无可用网络");

        if (AppPaths.IsIsolatedTest)
            return (true, "隔离测试网络就绪");

        var endpoints = new (string Host, int Port)[]
        {
            ("223.5.5.5", 53),
            ("119.29.29.29", 53),
            ("1.1.1.1", 53)
        };

        using var cts = new CancellationTokenSource(Math.Max(50, timeoutMs));
        var tasks = endpoints.Select(ep => ProbeTcpAsync(ep.Host, ep.Port, Math.Max(50, timeoutMs), cts.Token)).ToList();
        try
        {
            while (tasks.Count > 0)
            {
                var finished = Task.WhenAny(tasks).GetAwaiter().GetResult();
                tasks.Remove(finished);
                if (finished.Status == TaskStatus.RanToCompletion && finished.Result)
                {
                    cts.Cancel();
                    return (true, "外网 TCP 探测成功（DNS 53）");
                }
            }
        }
        catch
        {
            // 探测异常视为离线
        }

        return (false, "外网 DNS 端点均不可达（223.5.5.5 / 119.29.29.29 / 1.1.1.1:53）");
    }

    private static async Task<bool> ProbeTcpAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static (string FailureKind, string BadgeLabel, bool IsRetryable, string Reason)
        ClassifyFailure(
            int exitCode,
            string stdOut,
            string stdErr,
            Exception? ex,
            bool emptyOutput,
            bool localNetOnline)
    {
        var blob = string.Join('\n',
            stdOut ?? "",
            stdErr ?? "",
            ex?.Message ?? "",
            ex?.ToString() ?? "");
        var lower = blob.ToLowerInvariant();

        if (IsPermanentConfigError(blob, lower, exitCode))
        {
            return ("permanent_config_error", "配置错误", false, Summarize("未找到可执行文件或 RunnerCmd 未配置", blob));
        }

        if (!localNetOnline)
        {
            return ("local_network_offline", "本机断网", true, "本机网络不可用，等待恢复后可重试");
        }

        // 严禁把 Cursor 桌面/未装 CLI 或「并非 Cursor 没额度」文案误判为 Cursor 额度耗尽
        if (ContainsAny(blob, "并非 Cursor 没额度", "并非 Cursor 额度", "Cursor 桌面端后台通道暂未就绪",
                "未安装 cursor-agent", "Cursor 官方 CLI 与桌面 UIA 均未就绪", "禁止用 Codex 冒充"))
        {
            return ("execution_stalled", "📡 桌面通道未就绪", true,
                Summarize("Cursor 桌面通道未就绪或未装 CLI（非 Cursor 额度问题）", blob));
        }

        if (ContainsAny(lower, "usage_limit_exceeded", "hit your usage limit", "upgrade to plus",
                "insufficient_quota", "billing_hard_limit", "exceeded your current quota",
                "配额已耗尽", "额度已满", "账号额度已达上限"))
        {
            bool isCodexEngine = ContainsAny(lower, "codex", "openai", "gpt-") ||
                                ContainsAny(blob, "执行引擎", "备用 Codex", "Codex 官方");
            string badge = isCodexEngine ? "⚠️ 备用 Codex 引擎配额耗尽" : "⚠️ 账号配额耗尽";
            string reason = isCodexEngine
                ? "备用 Codex 执行引擎配额已耗尽（并非 Cursor 额度）"
                : "执行引擎账号配额已耗尽，跳过原地重复重试";
            return ("quota_exceeded", badge, false, Summarize(reason, blob));
        }

        if (ContainsAny(lower, "429", "rate limit", "rate_limit", "ratelimit", "overloaded",
                "too many requests", "resource_exhausted"))
        {
            return ("rate_limited", "限流", true, Summarize("触发限流，可稍后重试", blob));
        }
        // 单独的 "quota" 过宽，易误伤；仅在明确引擎上下文时记限流
        if (ContainsAny(lower, "quota") && ContainsAny(lower, "codex", "openai", "anthropic"))
        {
            return ("rate_limited", "限流/配额", true, Summarize("触发限流或配额波动", blob));
        }

        if (AutoDispatchService.IsInteractiveGateOrTrustError(blob))
        {
            return ("workspace_trust_required", "🔒 需信任工程目录", false, Summarize("CLI 要求工程目录信任授权（已在软件设置默认启用自动信任参数）", blob));
        }

        if (ContainsAny(lower, "not signed in", "grok login", "authentication required", "please sign in"))
        {
            return ("cli_not_signed_in", "🔑 CLI未登录", false, Summarize("无头 CLI 尚未登录认证，已自动切换备用通道", blob));
        }

        if (ContainsAny(blob, "EXECUTION_STALLED", "MODAL_BLOCKED", "STUCK_MODAL")
            || ContainsAny(lower, "无响应已自动熔断", "无输出假死", "stalled", "modal blocked"))
        {
            return ("execution_stalled", "⏳ 卡住自愈重试", true, Summarize("检测到客户端模态框阻塞或执行卡住，正在自动清理并换方式重试", blob));
        }

        if (exitCode == -1
            || ContainsAny(lower, "etimedout", "timed out", "timeout", "超时", "time-out", "deadline exceeded"))
        {
            return ("timeout", "超时", true, Summarize("执行超时", blob));
        }

        if (ContainsAny(blob, "NO_SEND_BTN", "SEND_NOT_CLEARED", "NO_PROMPT_EDIT", "NO_WINDOW", "WRONG_WORKSPACE_CONTEXT", "NO_SUITABLE_DIALOG")
            || ContainsAny(lower, "broken pipe", "brokenpipe", "message not sent"))
        {
            return ("message_not_sent", "📨 消息未送达", true, Summarize("客户端消息未成功发出或会话管道中断", blob));
        }

        if (ContainsAny(lower,
                "500", "502", "503", "504",
                "econnreset", "connection", "socket", "tls", "ssl",
                "stream disconnected", "network", "fetch failed",
                "connection reset", "connection refused", "connection aborted",
                "name or service not known", "temporary failure", "bad gateway", "service unavailable"))
        {
            return ("remote_server_error", "🌐 远端服务波动", true, Summarize("AI 服务端网络波动或连接重置", blob));
        }

        if (emptyOutput)
        {
            return ("message_not_sent", "📨 响应断流/未送达", true, Summarize("未收到有效返回内容（疑似断流或消息未送达）", blob));
        }

        if (exitCode != 0 || ex != null)
        {
            return ("remote_server_error", "远端服务异常", true, Summarize("非零退出或异常，按远端波动处理", blob));
        }

        return ("none", "正常", false, "无失败");
    }

    private static bool IsPermanentConfigError(string blob, string lower, int exitCode)
    {
        if (ContainsAny(lower,
                "未找到可执行文件", "未找到可执行", "executable not found",
                "cannot find the file", "system cannot find the file",
                "no such file or directory", "不是内部或外部命令"))
            return true;

        if (ContainsAny(blob, "RunnerCmd") && ContainsAny(lower, "未配置", "not configured", "missing", "empty"))
            return true;

        if (ContainsAny(lower, "未配置runnercmd", "runnercmd 未配置", "runnercmd未配置"))
            return true;

        // 常见“找不到命令”退出码（Windows 9009）
        if (exitCode == 9009) return true;

        return false;
    }

    public static bool WaitWithBackoffOrReconnect(
        int attempt,
        int maxRetries,
        string failureKind,
        UserHubConfig cfg,
        Action<string>? onStatus)
    {
        if (string.Equals(failureKind, "permanent_config_error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(failureKind, "quota_exceeded", StringComparison.OrdinalIgnoreCase))
        {
            onStatus?.Invoke("配置错误或账号配额已耗尽，跳过原地等待与重复重试");
            return false;
        }

        if (cfg == null || !cfg.EnableNetworkAutoRetry)
        {
            onStatus?.Invoke("未启用网络自动重试");
            return false;
        }

        var effectiveMax = maxRetries > 0 ? maxRetries : Math.Max(1, cfg.MaxNetworkRetries);
        if (attempt > effectiveMax)
        {
            onStatus?.Invoke($"已达最大重试次数 {effectiveMax}");
            return false;
        }

        var isolated = AppPaths.IsIsolatedTest;

        if (string.Equals(failureKind, "local_network_offline", StringComparison.OrdinalIgnoreCase))
        {
            if (!cfg.WaitForLocalNetworkRecovery)
            {
                onStatus?.Invoke("本机断网且未启用等待恢复");
                return false;
            }

            var maxWaitMs = isolated
                ? 1200
                : Math.Max(2000, cfg.LocalNetworkWaitMaxSeconds * 1000);
            var pollMs = isolated ? 35 : 1500;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            onStatus?.Invoke("🔌 检测到本机网络掉线，正在挂起等待本机网络恢复重连…");

            while (sw.ElapsedMilliseconds < maxWaitMs)
            {
                var (online, detail) = ProbeLocalNetworkHealth(isolated ? 40 : 600);
                if (online)
                {
                    onStatus?.Invoke($"🌐 本机网络已恢复连通（{detail}），立即自动重发任务！");
                    return true;
                }
                onStatus?.Invoke($"🔌 本机网络仍处于断开状态，正在自动重连检测（已等待 {Math.Max(1, sw.ElapsedMilliseconds / 1000)}s）…");
                Thread.Sleep(pollMs);
            }

            onStatus?.Invoke("⚠️ 等待本机网络恢复超时，已转入断点保护保存");
            return false;
        }

        // 服务端波动 / 限流 / 超时等：指数退避
        int delayMs;
        if (isolated)
        {
            // 隔离自检：40~80ms 快速退避
            var baseMs = 40;
            delayMs = (int)Math.Min(80, baseMs * Math.Pow(2, Math.Max(0, attempt - 1)));
            if (delayMs < 40) delayMs = 40;
        }
        else
        {
            // 1s, 2s, 4s … 封顶 15s；限流略加长
            var baseMs = string.Equals(failureKind, "rate_limited", StringComparison.OrdinalIgnoreCase) ? 2000 : 1000;
            delayMs = (int)Math.Min(15_000, baseMs * Math.Pow(2, Math.Max(0, attempt - 1)));
        }

        onStatus?.Invoke($"第 {attempt}/{effectiveMax} 次退避等待 {delayMs}ms（{failureKind}）");
        Thread.Sleep(delayMs);
        return true;
    }

    public static void SaveCheckpoint(ProjectProfile? proj, TaskChainCheckpoint cp)
    {
        if (cp == null) throw new ArgumentNullException(nameof(cp));
        if (string.IsNullOrWhiteSpace(cp.UpdatedAt))
            cp.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        if (proj != null && !string.IsNullOrWhiteSpace(proj.PrimaryRoot))
        {
            var projectPath = GetProjectCheckpointPath(proj);
            RegistryService.WriteJsonAtomic(projectPath, cp);
        }

        RegistryService.WriteJsonAtomic(LastCheckpointPath, cp);
    }

    public static void MarkCheckpointCompleted(ProjectProfile? proj, TaskChainCheckpoint cp)
    {
        cp.Status = "completed";
        cp.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        SaveCheckpoint(proj, cp);
    }

    public static TaskChainCheckpoint? LoadInterruptedCheckpoint(ProjectProfile? proj)
    {
        TaskChainCheckpoint? loaded = null;

        if (proj != null && !string.IsNullOrWhiteSpace(proj.PrimaryRoot))
        {
            loaded = TryReadCheckpoint(GetProjectCheckpointPath(proj));
        }

        loaded ??= TryReadCheckpoint(LastCheckpointPath);
        if (loaded == null) return null;

        if (proj != null && !string.IsNullOrWhiteSpace(loaded.ProjectRoot) &&
            !string.Equals(Path.GetFullPath(loaded.ProjectRoot).TrimEnd('\\', '/'),
                           Path.GetFullPath(proj.PrimaryRoot).TrimEnd('\\', '/'),
                           StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var status = (loaded.Status ?? "").Trim().ToLowerInvariant();
        if (status == "interrupted")
            return loaded;

        // 若上次进程在 running / reconnecting 期间意外崩溃退出（超过 6 分钟未更新），也视为可恢复断点
        if (status is "reconnecting" or "running" &&
            DateTime.TryParse(loaded.UpdatedAt, out var dt) &&
            (DateTime.Now - dt).TotalMinutes > 6)
        {
            return loaded;
        }

        return null;
    }

    public static void ClearInterruptedCheckpoint(ProjectProfile? proj)
    {
        if (proj != null && !string.IsNullOrWhiteSpace(proj.PrimaryRoot))
        {
            TryDelete(GetProjectCheckpointPath(proj));
        }
        TryDelete(LastCheckpointPath);
    }

    /// <summary>
    /// 同一工程、同一目标 AI 成功交付后，清理其遗留的 interrupted / running / reconnecting 断点，避免过期恢复横幅常驻。
    /// 其他工程或其他 AI 的断点保持不动。
    /// </summary>
    public static void ClearStaleCheckpointForAgent(ProjectProfile? proj, string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;
        var paths = new List<string>();
        if (proj != null && !string.IsNullOrWhiteSpace(proj.PrimaryRoot))
            paths.Add(GetProjectCheckpointPath(proj));
        paths.Add(LastCheckpointPath);

        foreach (var path in paths)
        {
            var cp = TryReadCheckpoint(path);
            if (cp == null) continue;
            if (!string.Equals(cp.TargetAgentId, agentId, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(cp.Status, "completed", StringComparison.OrdinalIgnoreCase)) continue;
            if (proj != null && !IsSameProject(cp, proj)) continue;
            TryDelete(path);
        }
    }

    private static bool IsSameProject(TaskChainCheckpoint cp, ProjectProfile proj)
    {
        if (!string.IsNullOrWhiteSpace(cp.ProjectId) && !string.IsNullOrWhiteSpace(proj.Id))
            return string.Equals(cp.ProjectId, proj.Id, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(cp.ProjectRoot) || string.IsNullOrWhiteSpace(proj.PrimaryRoot)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(cp.ProjectRoot).TrimEnd('\\', '/'),
                                 Path.GetFullPath(proj.PrimaryRoot).TrimEnd('\\', '/'),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static TaskChainCheckpoint? TryReadCheckpoint(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var txt = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(txt)) return null;
            return JsonSerializer.Deserialize<TaskChainCheckpoint>(txt, RegistryService.JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch
        {
            // 忽略占用
        }
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        if (string.IsNullOrEmpty(text) || needles.Length == 0) return false;
        foreach (var n in needles)
        {
            if (string.IsNullOrEmpty(n)) continue;
            if (text.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Summarize(string head, string blob)
    {
        var oneLine = (blob ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length > 180) oneLine = oneLine[..180] + "…";
        return string.IsNullOrWhiteSpace(oneLine) ? head : $"{head}：{oneLine}";
    }
}
