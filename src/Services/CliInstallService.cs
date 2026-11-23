using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AICollabHub.Models;

namespace AICollabHub.Services;

/// <summary>官方 CLI 状态探测、智能版本更新检查与带进度异步安装。</summary>
public static class CliInstallService
{
    public sealed record CliInstallSource(
        string AgentId,
        string DisplayName,
        string OfficialUrl,
        string InstallCommandHint,
        string ProgressLabel);

    public sealed record CliUpdateStatus(
        string AgentId,
        bool IsInstalled,
        string InstalledPath,
        string LocalVersion,
        string LatestVersion,
        bool HasUpdate,
        DateTime CheckedAtUtc);

    private static readonly ConcurrentDictionary<string, CliUpdateStatus> UpdateCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, CliUpdateStatus> MockUpdateOverrides =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<CliInstallSource> Catalog { get; } = new[]
    {
        new CliInstallSource(
            "cursor",
            "Cursor Agent CLI",
            "https://cursor.com/docs/cli",
            "irm 'https://cursor.com/install?win32=true' | iex",
            "cursor-agent"),
        new CliInstallSource(
            "claude_code",
            "Claude Code CLI",
            "https://docs.anthropic.com/en/docs/claude-code",
            "npm install -g @anthropic-ai/claude-code",
            "claude"),
        new CliInstallSource(
            "codex_cli",
            "Codex CLI",
            "https://github.com/openai/codex",
            "winget install OpenAI.Codex",
            "codex"),
        new CliInstallSource(
            "grok_bot",
            "Grok CLI",
            "https://grok.x.ai/",
            "npm install -g @xai/grok",
            "grok")
    };

    public static CliInstallSource? FindSource(string agentId) =>
        Catalog.FirstOrDefault(c => c.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase));

    public static string? ProbeInstalledBinary(string agentId) => agentId.ToLowerInvariant() switch
    {
        "cursor" => AutoDispatchService.ResolveCursorAgentCliBinary(null),
        "claude_code" => AutoDispatchService.ResolveClaudeCodeCliBinary(null),
        "codex_cli" => AutoDispatchService.ResolveCodexBinary(null),
        "grok_bot" => AutoDispatchService.ResolveGrokCliBinary(null),
        _ => null
    };

    public static string BuildMissingCliGuidance(string agentName) =>
        $"当前「{agentName}」本地 CLI 通道暂未就绪，已跳过该通道并交由其他在岗 AI 执行。";

    /// <summary>
    /// 仅当工具已装 CLI，或本机确实安装了对应的桌面客户端时才展示 CLI 状态/更新/安装入口；
    /// 对本机未安装任何实体的工具（如未安装的 Claude Code），一律不展示 CLI 安装提示。
    /// </summary>
    public static bool ShouldOfferCliControl(AgentRosterEntry agent)
    {
        if (agent == null || FindSource(agent.Id) == null) return false;
        if (!string.IsNullOrWhiteSpace(ProbeInstalledBinary(agent.Id))) return true;
        return agent.Detected && AutoDispatchService.ResolveGuiExecutable(agent) != null;
    }

    public static void SetMockUpdateStatusForTest(string agentId, CliUpdateStatus? status)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;
        if (status == null)
        {
            MockUpdateOverrides.TryRemove(agentId, out _);
            UpdateCache.TryRemove(agentId, out _);
        }
        else
        {
            MockUpdateOverrides[agentId] = status;
            UpdateCache[agentId] = status;
        }
    }

    public static CliUpdateStatus GetCachedOrInitialStatus(string agentId)
    {
        if (MockUpdateOverrides.TryGetValue(agentId, out var mocked))
            return mocked;
        if (UpdateCache.TryGetValue(agentId, out var cached))
            return cached;

        var bin = ProbeInstalledBinary(agentId);
        bool installed = !string.IsNullOrWhiteSpace(bin);
        string localVer = installed ? ResolveLocalCliVersionFast(agentId, bin!) : "";
        return new CliUpdateStatus(agentId, installed, bin ?? "", localVer, localVer, false, DateTime.MinValue);
    }

    public static void MarkUpdatedToLatest(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;
        MockUpdateOverrides.TryRemove(agentId, out _);
        var bin = ProbeInstalledBinary(agentId);
        bool installed = !string.IsNullOrWhiteSpace(bin);
        string localVer = installed ? ResolveLocalCliVersionFast(agentId, bin!) : "";
        UpdateCache[agentId] = new CliUpdateStatus(agentId, installed, bin ?? "", localVer, localVer, false, DateTime.UtcNow);
    }

    /// <summary>安装或更新结束后丢弃本地版本缓存并立刻重探，避免卡片仍停在「有更新」。</summary>
    public static CliUpdateStatus InvalidateAndReloadLocalStatus(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return new CliUpdateStatus(agentId ?? "", false, "", "", "", false, DateTime.UtcNow);
        MockUpdateOverrides.TryRemove(agentId, out _);
        UpdateCache.TryRemove(agentId, out _);
        RefreshProcessPathFromEnvironment();
        MarkUpdatedToLatest(agentId);
        return GetCachedOrInitialStatus(agentId);
    }

    public static async Task<CliUpdateStatus> CheckUpdateAsync(
        string agentId,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        if (MockUpdateOverrides.TryGetValue(agentId, out var mocked))
            return mocked;

        if (!forceRefresh && UpdateCache.TryGetValue(agentId, out var cached) &&
            (DateTime.UtcNow - cached.CheckedAtUtc).TotalMinutes < 30)
        {
            return cached;
        }

        RefreshProcessPathFromEnvironment();
        var bin = ProbeInstalledBinary(agentId);
        if (string.IsNullOrWhiteSpace(bin) || !File.Exists(bin))
        {
            var emptySt = new CliUpdateStatus(agentId, false, "", "", "", false, DateTime.UtcNow);
            UpdateCache[agentId] = emptySt;
            return emptySt;
        }

        var (pkgName, localVer) = ResolveLocalPackageAndVersion(agentId, bin);
        string latestVer = localVer;
        bool hasUpdate = false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"AICollabHub/{AppPaths.AppVersion}");

            if (!string.IsNullOrWhiteSpace(pkgName) && !string.IsNullOrWhiteSpace(localVer))
            {
                var url = $"https://registry.npmjs.org/{Uri.EscapeDataString(pkgName)}/latest";
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("version", out var vEl))
                    {
                        var remoteVer = vEl.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(remoteVer))
                        {
                            latestVer = remoteVer;
                            hasUpdate = TryCompareSemVer(remoteVer, localVer) > 0;
                        }
                    }
                }
            }
            else if (agentId.Equals("codex_cli", StringComparison.OrdinalIgnoreCase))
            {
                bool hostedByStoreOrApp =
                    bin.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                    bin.Contains(".plugin-appserver", StringComparison.OrdinalIgnoreCase);
                if (!hostedByStoreOrApp && !string.IsNullOrWhiteSpace(localVer))
                {
                    using var resp = await http.GetAsync("https://api.github.com/repos/openai/codex/releases/latest", ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var tag = doc.RootElement.TryGetProperty("tag_name", out var tEl) ? tEl.GetString() ?? "" : "";
                        var m = Regex.Match(tag, @"(\d+\.\d+\.\d+)");
                        if (m.Success)
                        {
                            latestVer = m.Groups[1].Value;
                            hasUpdate = TryCompareSemVer(latestVer, localVer) > 0;
                        }
                    }
                }
            }
            else if (agentId.Equals("cursor", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(localVer))
            {
                using var resp = await http.GetAsync("https://cursor.com/install?win32=true", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var script = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var m = Regex.Match(script, @"(\d{4}\.\d{1,2}\.\d{1,2}(?:-\d{2}-\d{2}-\d{2})?-[a-f0-9]{6,})");
                    if (m.Success)
                    {
                        latestVer = m.Groups[1].Value;
                        hasUpdate = CompareDateBuildVersion(latestVer, localVer) > 0;
                    }
                }
            }
        }
        catch
        {
            // 网络不可达或远端格式未明确匹配时，一律稳态保持无更新，绝不误报
            hasUpdate = false;
        }

        var st = new CliUpdateStatus(agentId, true, bin, localVer, latestVer, hasUpdate, DateTime.UtcNow);
        UpdateCache[agentId] = st;
        return st;
    }

    private static string ResolveLocalCliVersionFast(string agentId, string binPath) =>
        ResolveLocalPackageAndVersion(agentId, binPath).Version;

    private static (string PackageName, string Version) ResolveLocalPackageAndVersion(string agentId, string binPath)
    {
        try
        {
            if (agentId.Equals("cursor", StringComparison.OrdinalIgnoreCase))
            {
                var versRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cursor-agent", "versions");
                if (Directory.Exists(versRoot))
                {
                    var latestDir = Directory.GetDirectories(versRoot)
                        .Select(Path.GetFileName)
                        .Where(n => !string.IsNullOrWhiteSpace(n) && Regex.IsMatch(n, @"^\d{4}\.\d{1,2}\.\d{1,2}"))
                        .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(latestDir))
                        return ("", latestDir);
                }
            }

            // 查找 npm 全局包 package.json
            var dir = Path.GetDirectoryName(binPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string[] scopedCandidates = agentId.ToLowerInvariant() switch
                {
                    "grok_bot" => new[] { Path.Combine(dir, "node_modules", "@xai", "grok", "package.json") },
                    "claude_code" => new[] { Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "package.json") },
                    "codex_cli" => new[] { Path.Combine(dir, "node_modules", "@openai", "codex", "package.json") },
                    _ => Array.Empty<string>()
                };
                foreach (var pj in scopedCandidates)
                {
                    if (!File.Exists(pj)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(pj, Encoding.UTF8));
                    var nm = doc.RootElement.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    var vr = doc.RootElement.TryGetProperty("version", out var vEl) ? vEl.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(vr)) return (nm, vr);
                }
            }

            if (binPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var fv = FileVersionInfo.GetVersionInfo(binPath);
                if (!string.IsNullOrWhiteSpace(fv.ProductVersion))
                {
                    var m = Regex.Match(fv.ProductVersion, @"(\d+\.\d+\.\d+)");
                    if (m.Success) return ("", m.Groups[1].Value);
                }
            }
        }
        catch { }
        return ("", "");
    }

    private static int TryCompareSemVer(string remote, string local)
    {
        var mr = Regex.Match(remote ?? "", @"(\d+)\.(\d+)\.(\d+)");
        var ml = Regex.Match(local ?? "", @"(\d+)\.(\d+)\.(\d+)");
        if (!mr.Success || !ml.Success) return 0;
        for (int i = 1; i <= 3; i++)
        {
            int rv = int.Parse(mr.Groups[i].Value);
            int lv = int.Parse(ml.Groups[i].Value);
            if (rv != lv) return rv.CompareTo(lv);
        }
        return 0;
    }

    private static int CompareDateBuildVersion(string remote, string local)
    {
        var mr = Regex.Match(remote ?? "", @"^(\d{4})\.(\d{1,2})\.(\d{1,2})");
        var ml = Regex.Match(local ?? "", @"^(\d{4})\.(\d{1,2})\.(\d{1,2})");
        if (!mr.Success || !ml.Success) return 0;
        for (int i = 1; i <= 3; i++)
        {
            int rv = int.Parse(mr.Groups[i].Value);
            int lv = int.Parse(ml.Groups[i].Value);
            if (rv != lv) return rv.CompareTo(lv);
        }
        return string.Equals(remote?.Trim(), local?.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 0;
    }

    /// <summary>同步刷新当前进程 PATH（纳入用户级/系统级环境变量与常见 CLI/Node 目录）。</summary>
    public static void RefreshProcessPathFromEnvironment()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var extraDirs = new List<string>
            {
                Path.Combine(userHome, ".local", "bin"),
                Path.Combine(local, "cursor-agent"),
                Path.Combine(appData, "npm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs")
            };

            string reaNodeParent = Path.Combine(local, "Programs", "rea-runtime");
            if (Directory.Exists(reaNodeParent))
            {
                foreach (var nd in Directory.GetDirectories(reaNodeParent, "node-*"))
                {
                    extraDirs.Add(nd);
                }
            }

            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in new[]
            {
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
            })
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                foreach (var seg in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var d = seg.Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(d) && seen.Add(d)) ordered.Add(d);
                }
            }
            foreach (var ex in extraDirs)
            {
                if (!string.IsNullOrWhiteSpace(ex) && Directory.Exists(ex) && seen.Add(ex)) ordered.Add(ex);
            }
            Environment.SetEnvironmentVariable("PATH", string.Join(";", ordered));
        }
        catch { }
    }

    private static string? FindNpmCommandPath()
    {
        RefreshProcessPathFromEnvironment();
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "npm.cmd")
        };
        var reaRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "rea-runtime");
        if (Directory.Exists(reaRoot))
        {
            try
            {
                foreach (var nd in Directory.GetDirectories(reaRoot, "node-*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(Path.Combine(nd, "npm.cmd"));
                }
            }
            catch { }
        }
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim().Trim('"'), "npm.cmd"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>打开独立终端窗口运行 CLI 官方登录命令（仅当本机确已安装对应 CLI 二进制时执行）。</summary>
    public static void LaunchInteractiveCliLogin(string agentId)
    {
        RefreshProcessPathFromEnvironment();
        string? bin = ProbeInstalledBinary(agentId);
        if (string.IsNullOrWhiteSpace(bin) || !File.Exists(bin))
        {
            return;
        }
        string loginCmd = $"& '{bin.Replace("'", "''")}' login";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Write-Host '正在启动登录向导：{loginCmd}'; {loginCmd}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>
    /// 后台下载并执行官方安装源，通过 progress 回调实时汇报下载/安装百分比（0~1）与字节/阶段状态文案。
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(
        string agentId,
        IProgress<(double Progress, string Status)>? progress,
        CancellationToken ct = default)
    {
        var src = FindSource(agentId);
        if (src == null) return (false, "该工具暂不支持一键 CLI 安装");

        RefreshProcessPathFromEnvironment();
        progress?.Report((0.04, $"准备安装 {src.DisplayName}，正在解析官方下载源…"));

        string? downloadedScriptPath = null;
        string? installBody = null;
        string? directDownloadUrl = agentId.Equals("cursor", StringComparison.OrdinalIgnoreCase)
            ? "https://cursor.com/install?win32=true"
            : null;

        try
        {
            if (!string.IsNullOrWhiteSpace(directDownloadUrl))
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AICollabHub-Installer/1.9.11");
                    progress?.Report((0.08, $"正在连接官方下载源 {directDownloadUrl} …"));
                    using var resp = await http.GetAsync(directDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    long totalBytes = resp.Content.Headers.ContentLength ?? -1;
                    await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    var buf = new byte[8192];
                    long readTotal = 0;
                    int n;
                    while ((n = await netStream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        readTotal += n;
                        double kb = readTotal / 1024.0;
                        double frac = totalBytes > 0
                            ? Math.Clamp((double)readTotal / totalBytes, 0.05, 1.0)
                            : Math.Min(0.95, 0.15 + readTotal / 65536.0);
                        string pctLabel = totalBytes > 0
                            ? $"正在下载官方安装脚本 {(int)(frac * 100)}% ({kb:F1} / {totalBytes / 1024.0:F1} KB)"
                            : $"正在下载官方安装脚本 ({kb:F1} KB)…";
                        progress?.Report((0.10 + frac * 0.28, pctLabel));
                    }

                    downloadedScriptPath = Path.Combine(Path.GetTempPath(), $"aicollab_dl_install_{Guid.NewGuid():N}.ps1");
                    await File.WriteAllBytesAsync(downloadedScriptPath, ms.ToArray(), ct).ConfigureAwait(false);
                    progress?.Report((0.40, $"官方安装脚本下载完成 ({readTotal / 1024.0:F1} KB)，正在启动安装…"));
                }
                catch (Exception ex)
                {
                    progress?.Report((0.25, $"流式下载回退至 PowerShell 直接拉取（{Truncate(ex.Message, 40)}）"));
                    downloadedScriptPath = null;
                }
            }
            else
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    progress?.Report((0.12, $"正在检测官方源 {src.OfficialUrl} …"));
                    using var resp = await http.GetAsync(src.OfficialUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    progress?.Report((0.25, resp.IsSuccessStatusCode ? "官方源在线 · 准备拉取安装包" : "官方源跳过探测 · 直接执行本地安装包管理器"));
                }
                catch
                {
                    progress?.Report((0.25, "网络探测跳过 · 直接执行官方安装命令"));
                }
            }

            if (downloadedScriptPath != null && File.Exists(downloadedScriptPath))
            {
                installBody = $"& '{downloadedScriptPath.Replace("'", "''")}'";
            }
            else if (agentId.Equals("claude_code", StringComparison.OrdinalIgnoreCase) ||
                     agentId.Equals("grok_bot", StringComparison.OrdinalIgnoreCase))
            {
                var npmCmd = FindNpmCommandPath();
                string pkg = agentId.Equals("grok_bot", StringComparison.OrdinalIgnoreCase)
                    ? "@xai/grok"
                    : "@anthropic-ai/claude-code";
                if (!string.IsNullOrWhiteSpace(npmCmd))
                {
                    installBody = $"& '{npmCmd.Replace("'", "''")}' install -g {pkg} --loglevel info";
                    progress?.Report((0.35, $"已定位 npm ({Path.GetFileName(npmCmd)}) · 开始下载 {pkg}…"));
                }
                else if (agentId.Equals("claude_code", StringComparison.OrdinalIgnoreCase))
                {
                    installBody = "irm https://claude.ai/install.ps1 | iex";
                    progress?.Report((0.35, "未找到 npm · 自动改用 Claude 官方 PowerShell 安装脚本…"));
                }
                else
                {
                    installBody = src.InstallCommandHint;
                    progress?.Report((0.35, $"未检测到 npm.cmd，尝试直连命令：{src.InstallCommandHint}"));
                }
            }
            else if (agentId.Equals("codex_cli", StringComparison.OrdinalIgnoreCase))
            {
                var npmCmd = FindNpmCommandPath();
                string npmFallback = !string.IsNullOrWhiteSpace(npmCmd)
                    ? $"\nif ($LASTEXITCODE -ne 0) {{ Write-Host 'winget fallback to npm...'; & '{npmCmd.Replace("'", "''")}' install -g @openai/codex --loglevel info }}"
                    : "";
                installBody = $"winget install --id OpenAI.Codex -e --accept-source-agreements --accept-package-agreements{npmFallback}";
                progress?.Report((0.35, "正在通过 winget / npm 拉取 OpenAI Codex CLI…"));
            }
            else
            {
                installBody = src.InstallCommandHint;
                progress?.Report((0.38, $"正在下载并安装：{src.InstallCommandHint}"));
            }

            string ps = $@"
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:PATH = [Environment]::GetEnvironmentVariable('Path','User') + ';' + [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + $env:PATH
Write-Host 'CLI_INSTALL_START'
{installBody}
$code = $LASTEXITCODE
if ($null -eq $code) {{ $code = 0 }}
Write-Host (""CLI_INSTALL_EXIT="" + $code)
";
            var tmp = Path.Combine(Path.GetTempPath(), $"aicollab_cli_install_{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(tmp, ps, new UTF8Encoding(true), ct).ConfigureAwait(false);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tmp}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (false, "无法启动安装进程");

                var sb = new StringBuilder();
                double currentPct = 0.42;
                string lastStageMsg = $"正在下载并安装 {src.DisplayName}…";

                void HandleLine(string? line, bool isErr)
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    sb.AppendLine(line);
                    if (line.Contains("CLI_INSTALL_START", StringComparison.Ordinal))
                    {
                        currentPct = Math.Max(currentPct, 0.48);
                        lastStageMsg = "正在执行官方安装器…";
                    }
                    else
                    {
                        var pctMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\d{1,3}(?:\.\d+)?)\s*%");
                        if (pctMatch.Success && double.TryParse(pctMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rawPct))
                        {
                            double mapped = 0.45 + Math.Clamp(rawPct, 0, 100) / 100.0 * 0.47;
                            currentPct = Math.Max(currentPct, mapped);
                        }
                        else if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("http fetch", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("Installing", StringComparison.OrdinalIgnoreCase))
                        {
                            currentPct = Math.Min(0.90, currentPct + 0.04);
                        }
                        if (!isErr || line.Contains("Downloading", StringComparison.OrdinalIgnoreCase) || line.Contains("npm", StringComparison.OrdinalIgnoreCase))
                        {
                            lastStageMsg = Truncate(line.Trim(), 78);
                        }
                    }
                    progress?.Report((currentPct, lastStageMsg));
                }

                proc.OutputDataReceived += (_, e) => HandleLine(e.Data, false);
                proc.ErrorDataReceived += (_, e) => HandleLine(e.Data, true);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                var sw = Stopwatch.StartNew();
                while (!proc.HasExited)
                {
                    ct.ThrowIfCancellationRequested();
                    double pulse = Math.Min(0.92, Math.Max(currentPct, 0.45 + sw.Elapsed.TotalSeconds / 90.0 * 0.45));
                    currentPct = pulse;
                    progress?.Report((pulse, $"安装进行中 {(int)(pulse * 100)}% · {lastStageMsg}"));
                    await Task.Delay(700, ct).ConfigureAwait(false);
                }
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                progress?.Report((0.95, "正在刷新环境变量并重检本机 CLI…"));
                RefreshProcessPathFromEnvironment();
                await Task.Delay(400, ct).ConfigureAwait(false);
                var bin = ProbeInstalledBinary(agentId);
                bool ok = !string.IsNullOrWhiteSpace(bin) && File.Exists(bin);
                progress?.Report((1.0, ok ? $"安装成功：{bin}" : "安装已结束但未检测到可执行文件，请检查网络或提示命令"));
                return (ok, ok ? $"已安装 {src.DisplayName} ({bin})" : $"安装流程结束但未探测到 {src.ProgressLabel}，可尝试手动执行：{src.InstallCommandHint}");
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        finally
        {
            try { if (downloadedScriptPath != null && File.Exists(downloadedScriptPath)) File.Delete(downloadedScriptPath); } catch { }
        }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
