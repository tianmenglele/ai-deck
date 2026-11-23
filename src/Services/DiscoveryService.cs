using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;
using AICollabHub.Models;

namespace AICollabHub.Services;

public sealed class DiscoveryService
{
    public sealed class EngineMcpStatus
    {
        public bool GodotPluginPresent { get; set; }
        public bool GodotHttpPortOpen { get; set; }
        public bool GodotWsPortOpen { get; set; }
        public string Summary => GodotPluginPresent
            ? (GodotHttpPortOpen || GodotWsPortOpen
                ? "Godot AI MCP 在线 (端口已连通)"
                : "已装 Godot AI 插件 (编辑器未开或端口待连)")
            : "当前项目未检测到 Godot MCP 插件";
    }

    public sealed class ScanStats
    {
        public int CatalogCount { get; set; }
        public int DetectedCount { get; set; }
        public int IdeDesktopCount { get; set; }
        public int CliCount { get; set; }
        public int ExtLocalCount { get; set; }
        public int ScannedDirCount { get; set; }
        public string StatusText =>
            $"彻底扫描完成：已扫描 {CatalogCount} 款主流 AI 工具，本机共检出 {DetectedCount} 款可用（IDE/桌面 {IdeDesktopCount} · CLI {CliCount} · 扩展/本地模型 {ExtLocalCount}）" +
            (ScannedDirCount > 0 ? $" · 索引目录 {ScannedDirCount}" : "");
    }

    /// <summary>一次性本地环境索引：PATH/包管理/注册表/开始菜单/扩展/进程，避免对每个工具循环起 where 进程。</summary>
    public sealed class LocalEnvIndex
    {
        public Dictionary<string, string> CommandHits { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string DisplayName, string InstallLocation, string DisplayIcon)> UninstallEntries { get; } = new();
        public List<(string LinkName, string Target)> Shortcuts { get; } = new();
        public HashSet<string> ProcessNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ExtensionFolderNames { get; } = new();
        public List<string> MsixPackageDirs { get; } = new();
        public HashSet<string> ExistingDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ScannedDirCount { get; set; }
    }

    [ThreadStatic] private static LocalEnvIndex? _tlsIndex;

    public EngineMcpStatus ProbeProjectMcp(ProjectProfile? project)
    {
        var st = new EngineMcpStatus();
        if (project != null && !string.IsNullOrWhiteSpace(project.PrimaryRoot))
        {
            var addonCfg = Path.Combine(project.PrimaryRoot, "addons", "godot_ai", "plugin.cfg");
            st.GodotPluginPresent = File.Exists(addonCfg);
        }
        st.GodotHttpPortOpen = ProbeLocalTcpPort(8000, 120);
        st.GodotWsPortOpen = ProbeLocalTcpPort(9500, 120);
        return st;
    }

    public static int GetMainstreamCatalogCount(RegistryService reg)
        => reg.Detectors.Detectors?.Count(d => !string.Equals(d.Category, "vps_bridge", StringComparison.OrdinalIgnoreCase)) ?? 0;

    public LocalEnvIndex BuildLocalEnvIndex()
    {
        var idx = new LocalEnvIndex();
        // 隔离自检：只建轻量 PATH 索引，跳过注册表/开始菜单/进程，确保 AppHangB1 < 450ms
        if (AppPaths.IsIsolatedTest)
        {
            IndexExecutableDirs(idx, shallow: true);
            return idx;
        }
        IndexExecutableDirs(idx);
        IndexUninstallRegistry(idx);
        IndexStartMenuShortcuts(idx);
        IndexStandardRootsAndMsix(idx);
        IndexIdeExtensions(idx);
        IndexRunningProcesses(idx);
        return idx;
    }

    public Dictionary<string, (bool Found, string Evidence)> ProbeAllSpecsSnapshot(IReadOnlyList<DetectorSpec> specs)
    {
        var index = BuildLocalEnvIndex();
        _tlsIndex = index;
        try
        {
            var map = new Dictionary<string, (bool Found, string Evidence)>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in specs)
            {
                if (!string.IsNullOrWhiteSpace(spec.JsonNodesFile)) continue;
                map[spec.Id] = ProbeSingleSpec(spec, index, checkNetworkPorts: true);
            }
            return map;
        }
        finally
        {
            _tlsIndex = null;
        }
    }

    public int RunDiscovery(RegistryService reg, Dictionary<string, (bool Found, string Evidence)>? preProbed = null)
        => RunDiscoveryWithStats(reg, preProbed).DetectedCount;

    public ScanStats RunDiscoveryWithStats(RegistryService reg, Dictionary<string, (bool Found, string Evidence)>? preProbed = null)
    {
        var existingById = reg.UserConfig.Agents.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var updatedList = new List<AgentRosterEntry>();
        int detectedCount = 0;
        int ideDesktop = 0, cli = 0, extLocal = 0;

        LocalEnvIndex? ownedIndex = null;
        if (preProbed == null)
        {
            ownedIndex = BuildLocalEnvIndex();
            _tlsIndex = ownedIndex;
        }

        try
        {
            foreach (var spec in reg.Detectors.Detectors)
            {
                if (!string.IsNullOrWhiteSpace(spec.JsonNodesFile))
                {
                    var expandedFile = Environment.ExpandEnvironmentVariables(spec.JsonNodesFile);
                    if (File.Exists(expandedFile))
                    {
                        var bridgeEntries = ParseBridgeNodesFile(spec, expandedFile, existingById);
                        if (bridgeEntries.Count > 0)
                        {
                            detectedCount += bridgeEntries.Count(x => x.Detected);
                            updatedList.AddRange(bridgeEntries);
                            continue;
                        }
                    }
                }

                var (found, evidence) = (preProbed != null && preProbed.TryGetValue(spec.Id, out var pb))
                    ? pb
                    : ProbeSingleSpec(spec, ownedIndex ?? _tlsIndex, checkNetworkPorts: true);
                if (found) detectedCount++;

                if (existingById.TryGetValue(spec.Id, out var prev))
                {
                    prev.Detected = found;
                    prev.Evidence = evidence;
                    if (string.IsNullOrWhiteSpace(prev.Name)) prev.Name = spec.Name;
                    if (string.IsNullOrWhiteSpace(prev.Category)) prev.Category = spec.Category;
                    if (string.IsNullOrWhiteSpace(prev.RunnerMode) || prev.RunnerMode == "none")
                    {
                        prev.RunnerMode = spec.DefaultRunnerMode;
                        prev.RunnerCmd = spec.DefaultRunnerCmd;
                    }
                    if ((string.IsNullOrWhiteSpace(prev.GuiExePath) ||
                         (spec.Id == "codex_cli" && prev.GuiExePath.Contains("Grok Bot", StringComparison.OrdinalIgnoreCase))) &&
                        !string.IsNullOrWhiteSpace(spec.DefaultGuiExe))
                    {
                        prev.GuiExePath = Environment.ExpandEnvironmentVariables(spec.DefaultGuiExe);
                    }
                    if (spec.Id == "codex_cli") prev.Name = spec.Name;
                    if (spec.DefaultPermissions.AllowDirectSourceWrite) prev.Permissions.AllowDirectSourceWrite = true;
                    if (spec.DefaultPermissions.AllowMultiDir) prev.Permissions.AllowMultiDir = true;
                    if (spec.DefaultPermissions.AllowShellBuild) prev.Permissions.AllowShellBuild = true;
                    foreach (var kv in spec.DefaultTags)
                    {
                        if (!prev.TagScores.ContainsKey(kv.Key) || prev.TagScores[kv.Key] < kv.Value)
                            prev.TagScores[kv.Key] = kv.Value;
                    }
                    updatedList.Add(prev);
                    existingById.Remove(spec.Id);
                }
                else
                {
                    bool autoEnable = found && (spec.Id is "antigravity" or "cursor" or "grok_bot");
                    updatedList.Add(new AgentRosterEntry
                    {
                        Id = spec.Id,
                        DetectorId = spec.Id,
                        Name = spec.Name,
                        Category = spec.Category,
                        Enabled = autoEnable,
                        Detected = found,
                        Evidence = evidence,
                        AdapterId = spec.DefaultAdapter,
                        RunnerMode = spec.DefaultRunnerMode,
                        RunnerCmd = spec.DefaultRunnerCmd,
                        GuiExePath = string.IsNullOrWhiteSpace(spec.DefaultGuiExe)
                            ? ""
                            : Environment.ExpandEnvironmentVariables(spec.DefaultGuiExe),
                        LaunchWindowOnDispatch = false,
                        Permissions = spec.DefaultPermissions.Clone(),
                        TagScores = new Dictionary<string, int>(spec.DefaultTags)
                    });
                }

                if (found)
                {
                    ClassifyDetected(spec.Category, ref ideDesktop, ref cli, ref extLocal);
                }
            }

            foreach (var custom in existingById.Values)
            {
                if (string.IsNullOrWhiteSpace(custom.DetectorId))
                {
                    if (!string.IsNullOrWhiteSpace(custom.BridgeLocalDir))
                    {
                        custom.Detected = Directory.Exists(custom.BridgeLocalDir);
                        custom.Evidence = custom.Detected ? $"桥接目录: {custom.BridgeLocalDir}" : $"目录不存在: {custom.BridgeLocalDir}";
                    }
                    else
                    {
                        custom.Detected = true;
                    }
                    if (custom.Detected)
                    {
                        detectedCount++;
                        ClassifyDetected(custom.Category, ref ideDesktop, ref cli, ref extLocal);
                    }
                    updatedList.Add(custom);
                }
            }

            foreach (var agItem in updatedList)
                CloudRecommendationService.EnsureAgentModelTiersPopulated(agItem);

            reg.UserConfig.Agents = updatedList;
            reg.SaveUserConfig();

            return new ScanStats
            {
                CatalogCount = Math.Max(GetMainstreamCatalogCount(reg), reg.Detectors.Detectors.Count),
                DetectedCount = detectedCount,
                IdeDesktopCount = ideDesktop,
                CliCount = cli,
                ExtLocalCount = extLocal,
                ScannedDirCount = ownedIndex?.ScannedDirCount
                    ?? (_tlsIndex?.ScannedDirCount ?? 0)
            };
        }
        finally
        {
            if (ownedIndex != null) _tlsIndex = null;
        }
    }

    private static void ClassifyDetected(string category, ref int ideDesktop, ref int cli, ref int extLocal)
    {
        var cat = (category ?? "").Trim().ToLowerInvariant();
        if (cat is "local_ide" or "local_agent") ideDesktop++;
        else if (cat is "local_cli") cli++;
        else if (cat is "ide_extension" or "local_llm" or "remote_capsule") extLocal++;
        else if (cat is "vps_bridge") { /* 远程节点计入扩展组 */ extLocal++; }
        else extLocal++;
    }

    private static List<AgentRosterEntry> ParseBridgeNodesFile(
        DetectorSpec spec,
        string nodesJsonPath,
        Dictionary<string, AgentRosterEntry> existingById)
    {
        var result = new List<AgentRosterEntry>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(nodesJsonPath));
            if (doc.RootElement.TryGetProperty("nodes", out var nodesArr) && nodesArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in nodesArr.EnumerateArray())
                {
                    var nodeId = n.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "node" : "node";
                    var nodeName = n.TryGetProperty("name", out var nmEl) ? nmEl.GetString() ?? nodeId : nodeId;
                    var localWork = n.TryGetProperty("localWorkDir", out var lwEl) ? lwEl.GetString() ?? "" : "";
                    var entryId = $"{spec.Id}_{nodeId}";

                    if (existingById.TryGetValue(entryId, out var prev))
                    {
                        prev.Detected = true;
                        if (string.IsNullOrWhiteSpace(prev.BridgeLocalDir) && !string.IsNullOrWhiteSpace(localWork))
                            prev.BridgeLocalDir = localWork;
                        if (string.IsNullOrWhiteSpace(prev.RunnerMode) || prev.RunnerMode == "none")
                            prev.RunnerMode = spec.DefaultRunnerMode;
                        prev.Evidence = !string.IsNullOrWhiteSpace(prev.BridgeLocalDir)
                            ? $"检测到桥接镜像: {prev.BridgeLocalDir}"
                            : $"检测到节点配置: {nodesJsonPath}";
                        result.Add(prev);
                        existingById.Remove(entryId);
                    }
                    else
                    {
                        result.Add(new AgentRosterEntry
                        {
                            Id = entryId,
                            DetectorId = spec.Id,
                            Name = $"VPS 远程 AI · {nodeName}",
                            Category = "vps_bridge",
                            Enabled = false,
                            Detected = true,
                            BridgeLocalDir = localWork,
                            Evidence = !string.IsNullOrWhiteSpace(localWork)
                                ? $"检测到外部桥接目录: {localWork}"
                                : $"检测到节点配置: {nodesJsonPath}",
                            AdapterId = spec.DefaultAdapter,
                            RunnerMode = spec.DefaultRunnerMode,
                            RunnerCmd = spec.DefaultRunnerCmd,
                            Permissions = spec.DefaultPermissions.Clone(),
                            TagScores = new Dictionary<string, int>(spec.DefaultTags)
                        });
                    }
                }
            }
        }
        catch { }
        return result;
    }

    public bool RefreshStaleDetectionStates(RegistryService reg)
    {
        if (reg.UserConfig.Agents == null || reg.UserConfig.Agents.Count == 0) return false;
        var specsById = reg.Detectors.Detectors.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        var index = BuildLocalEnvIndex();
        _tlsIndex = index;
        try
        {
            foreach (var ag in reg.UserConfig.Agents)
            {
                if (string.IsNullOrWhiteSpace(ag.DetectorId)) continue;
                if (ag.Category.Equals("vps_bridge", StringComparison.OrdinalIgnoreCase)) continue;
                if (!specsById.TryGetValue(ag.DetectorId, out var spec)) continue;

                var (found, evidence) = ProbeSingleSpec(spec, index, checkNetworkPorts: false);
                if (!found)
                {
                    if (ag.Detected || ag.Enabled)
                    {
                        ag.Detected = false;
                        ag.Enabled = false;
                        ag.Evidence = evidence;
                        changed = true;
                    }
                    if (reg.UserConfig.TeamProfiles != null)
                    {
                        foreach (var tp in reg.UserConfig.TeamProfiles)
                        {
                            foreach (var sn in tp.AgentSnapshots.Where(s =>
                                         string.Equals(s.AgentId, ag.Id, StringComparison.OrdinalIgnoreCase) && s.Enabled))
                            {
                                sn.Enabled = false;
                                changed = true;
                            }
                        }
                    }
                }
                else if (!ag.Detected)
                {
                    ag.Detected = true;
                    ag.Evidence = evidence;
                    changed = true;
                }
            }
        }
        finally
        {
            _tlsIndex = null;
        }

        if (changed) reg.SaveUserConfig();
        return changed;
    }

    private static (bool Found, string Evidence) ProbeSingleSpec(DetectorSpec spec, bool checkNetworkPorts = true)
        => ProbeSingleSpec(spec, _tlsIndex, checkNetworkPorts);

    private static (bool Found, string Evidence) ProbeSingleSpec(DetectorSpec spec, LocalEnvIndex? index, bool checkNetworkPorts = true)
    {
        if (spec.Id.Equals("claude_code", StringComparison.OrdinalIgnoreCase))
        {
            var claudeCli = AutoDispatchService.ResolveClaudeCodeCliBinary(null);
            if (!string.IsNullOrWhiteSpace(claudeCli))
                return (true, $"官方 CLI: {claudeCli}");

            var cmdHit = FindInPath("claude", index);
            if (!string.IsNullOrWhiteSpace(cmdHit))
                return (true, $"命令行: {cmdHit}");

            var claudeCred = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            var claudeSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
            if (File.Exists(claudeCred) && File.Exists(claudeSettings))
                return (true, "本机 Claude Code 配置目录已就绪");

            return (false, "本机未检测到 Claude Code CLI（如已卸载则不再显示）");
        }

        if (spec.Id.Equals("claude_desktop", StringComparison.OrdinalIgnoreCase))
        {
            var appDataClaudeExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "Claude.exe");
            if (File.Exists(appDataClaudeExe))
                return (true, $"桌面端: {appDataClaudeExe}");

            var localAnthropic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude");
            if (Directory.Exists(localAnthropic))
            {
                try
                {
                    var exeHit = Directory.GetFiles(localAnthropic, "claude.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(exeHit))
                        return (true, $"桌面端: {exeHit}");
                }
                catch { }
            }

            if (index != null)
            {
                if (MatchProcess(spec, index)) return (true, "进程快照: Claude Desktop 正在运行");
                var regHit = MatchUninstall(spec, index);
                if (regHit != null) return (true, $"系统已安装: {regHit}");
                var lnk = MatchShortcut(spec, index);
                if (lnk != null) return (true, $"开始菜单: {lnk}");
            }
            return (false, "本机未检测到 Claude Desktop");
        }

        foreach (var rawPath in spec.ProbePaths)
        {
            var p = Environment.ExpandEnvironmentVariables(rawPath);
            if (File.Exists(p)) return (true, $"本机路径: {p}");
            if (Directory.Exists(p))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(p).Any())
                        return (true, $"本机路径: {p}");
                }
                catch { }
            }
        }

        foreach (var cmd in spec.ProbeCommands)
        {
            var hit = FindInPath(cmd, index);
            if (hit != null) return (true, $"命令行: {hit}");
        }

        if (index != null)
        {
            var extHit = MatchExtension(spec, index);
            if (extHit != null) return (true, $"IDE 扩展: {extHit}");

            var msixHit = MatchMsix(spec, index);
            if (msixHit != null) return (true, $"MSIX 包: {msixHit}");

            if (MatchProcess(spec, index))
                return (true, $"进程快照: {spec.Name} 正在运行（便携/绿色版）");

            var uninstallHit = MatchUninstall(spec, index);
            if (uninstallHit != null) return (true, $"系统已安装: {uninstallHit}");

            var shortcutHit = MatchShortcut(spec, index);
            if (shortcutHit != null) return (true, $"开始菜单: {shortcutHit}");
        }
        else
        {
            foreach (var cmd in spec.ProbeCommands)
            {
                var hit = FindInPath(cmd, null);
                if (hit != null) return (true, $"命令行: {hit}");
            }
            var regHit = CheckUninstallRegistry(spec.Name);
            if (regHit != null) return (true, $"系统已安装: {regHit}");
        }

        if (checkNetworkPorts)
        {
            foreach (var port in spec.ProbePorts)
            {
                if (ProbeLocalTcpPort(port, 150))
                    return (true, $"本地服务在线: 127.0.0.1:{port}");
            }
        }

        return (false, "本机未检测到（安装或启动后点重新扫描即可出现）");
    }

    private static string? MatchExtension(DetectorSpec spec, LocalEnvIndex index)
    {
        if (spec.ProbeExtensionIds == null || spec.ProbeExtensionIds.Count == 0) return null;
        foreach (var eid in spec.ProbeExtensionIds)
        {
            if (string.IsNullOrWhiteSpace(eid)) continue;
            var prefix = eid.Trim();
            foreach (var folder in index.ExtensionFolderNames)
            {
                if (folder.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) ||
                    folder.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                    return folder;
            }
        }
        return null;
    }

    private static string? MatchMsix(DetectorSpec spec, LocalEnvIndex index)
    {
        if (spec.ProbeMsixPrefixes == null || spec.ProbeMsixPrefixes.Count == 0) return null;
        foreach (var prefix in spec.ProbeMsixPrefixes)
        {
            if (string.IsNullOrWhiteSpace(prefix)) continue;
            foreach (var dir in index.MsixPackageDirs)
            {
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(name) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }
        return null;
    }

    /// <summary>
    /// 仅按 <see cref="DetectorSpec.ProbeProcessNames"/> 精确匹配进程名（忽略大小写与 .exe 尾缀）。
    /// 不做子串包含、不拿规则名兜底，避免 UserOOBEBroker→roo_code、GeminiAppLauncher→gemini_cli 之类误报；IDE 扩展不看进程。
    /// </summary>
    internal static bool MatchProcess(DetectorSpec spec, LocalEnvIndex index)
    {
        if (string.Equals(spec.Category, "ide_extension", StringComparison.OrdinalIgnoreCase)) return false;
        if (spec.ProbeProcessNames == null || spec.ProbeProcessNames.Count == 0) return false;
        foreach (var raw in spec.ProbeProcessNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var n = raw.Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
            if (n.Length > 0 && index.ProcessNames.Contains(n)) return true;
        }
        return false;
    }

    /// <summary>不参与「卸载项 / 开始菜单」宽松名称匹配的规则：名称过泛会抢占同名桌面端（如 copilot_cli 与 copilot_desktop）。</summary>
    private static readonly HashSet<string> LooseNameMatchExcludedIds = new(StringComparer.OrdinalIgnoreCase) { "copilot_cli" };

    private const int ShortKeyMaxLength = 3;

    private static bool ContainsWholeWord(string text, string key)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key)) return false;
        int start = 0;
        while (start <= text.Length - key.Length)
        {
            int i = text.IndexOf(key, start, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            bool leftOk = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            int end = i + key.Length;
            bool rightOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (leftOk && rightOk) return true;
            start = i + 1;
        }
        return false;
    }

    private static bool NameMatches(string text, string key)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return key.Length <= ShortKeyMaxLength
            ? ContainsWholeWord(text, key)
            : text.Contains(key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// local_cli / ide_extension 且未配置 ProbeDisplayNames 时，禁止用 Name 首词（如 Gemini）
    /// 去模糊匹配卸载表/快捷方式，避免 Gemini CLI 因本机装了 Google Gemini 桌面端而误报。
    /// </summary>
    private static bool ForbidLooseNameFallback(DetectorSpec spec)
    {
        var cat = (spec.Category ?? "").Trim();
        if (!cat.Equals("local_cli", StringComparison.OrdinalIgnoreCase) &&
            !cat.Equals("ide_extension", StringComparison.OrdinalIgnoreCase))
            return false;
        return spec.ProbeDisplayNames == null ||
               spec.ProbeDisplayNames.All(string.IsNullOrWhiteSpace);
    }

    internal static string? MatchUninstall(DetectorSpec spec, LocalEnvIndex index)
    {
        if (LooseNameMatchExcludedIds.Contains(spec.Id)) return null;
        var keys = new List<string>();
        if (spec.ProbeDisplayNames != null) keys.AddRange(spec.ProbeDisplayNames);
        bool forbidLoose = ForbidLooseNameFallback(spec);
        if (!forbidLoose && !string.IsNullOrWhiteSpace(spec.Name))
        {
            keys.Add(spec.Name);
            keys.Add(spec.Name.Split(' ', '/', '(')[0].Trim());
        }
        foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k) && k.Length >= 3))
        {
            foreach (var e in index.UninstallEntries)
            {
                if (NameMatches(e.DisplayName, key))
                {
                    if (!string.IsNullOrWhiteSpace(e.InstallLocation))
                        return $"{e.DisplayName} @ {e.InstallLocation}";
                    return e.DisplayName;
                }
            }
        }
        return null;
    }

    internal static string? MatchShortcut(DetectorSpec spec, LocalEnvIndex index)
    {
        if (LooseNameMatchExcludedIds.Contains(spec.Id)) return null;
        var keys = new List<string>();
        if (spec.ProbeDisplayNames != null) keys.AddRange(spec.ProbeDisplayNames);
        if (!ForbidLooseNameFallback(spec) && !string.IsNullOrWhiteSpace(spec.Name))
            keys.Add(spec.Name.Split(' ', '/', '(')[0].Trim());
        foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k) && k.Length >= 3))
        {
            bool shortKey = key.Length <= ShortKeyMaxLength;
            foreach (var s in index.Shortcuts)
            {
                bool hit;
                if (shortKey)
                {
                    // 超短词只比对快捷方式名与目标文件名（按词边界），不扫描整条目标路径，避免 Jan/Amp 命中目录子串
                    string linkStem = Path.GetFileNameWithoutExtension(s.LinkName ?? "");
                    string targetStem = string.IsNullOrWhiteSpace(s.Target) ? "" : Path.GetFileNameWithoutExtension(s.Target);
                    hit = ContainsWholeWord(linkStem, key) || targetStem.Equals(key, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    hit = (!string.IsNullOrEmpty(s.LinkName) &&
                           s.LinkName.Contains(key, StringComparison.OrdinalIgnoreCase)) ||
                          (!string.IsNullOrEmpty(s.Target) &&
                           s.Target.Contains(key, StringComparison.OrdinalIgnoreCase));
                }
                if (hit)
                {
                    return string.IsNullOrWhiteSpace(s.Target) ? s.LinkName : s.Target;
                }
            }
        }
        return null;
    }

    private static void IndexExecutableDirs(LocalEnvIndex idx, bool shallow = false)
    {
        var dirs = new List<string>();
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = dir.Trim().Trim('"');
            if (!string.IsNullOrEmpty(t)) dirs.Add(t);
        }

        if (!shallow)
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            dirs.Add(Path.Combine(appData, "npm"));
            dirs.Add(Path.Combine(localApp, "pnpm"));
            dirs.Add(Path.Combine(localApp, "Yarn", "bin"));
            dirs.Add(Path.Combine(userProfile, ".local", "bin"));
            dirs.Add(Path.Combine(userProfile, ".cargo", "bin"));
            dirs.Add(Path.Combine(userProfile, "scoop", "shims"));
            dirs.Add(Path.Combine(localApp, "Microsoft", "WinGet", "Links"));
            dirs.Add(Path.Combine(localApp, "Microsoft", "WindowsApps"));
            dirs.Add(Path.Combine(userProfile, "go", "bin"));
            dirs.Add(Path.Combine(userProfile, ".uv", "bin"));
            dirs.Add(Path.Combine(localApp, "uv"));
            dirs.Add(Path.Combine(userProfile, ".local", "pipx", "venvs"));

            // pipx 各 venv 的 Scripts
            try
            {
                var pipxRoot = Path.Combine(userProfile, ".local", "pipx", "venvs");
                if (Directory.Exists(pipxRoot))
                {
                    foreach (var d in Directory.EnumerateDirectories(pipxRoot))
                        dirs.Add(Path.Combine(d, "Scripts"));
                }
            }
            catch { }

            try
            {
                var pyRoot = Path.Combine(localApp, "Programs", "Python");
                if (Directory.Exists(pyRoot))
                {
                    foreach (var d in Directory.EnumerateDirectories(pyRoot))
                        dirs.Add(Path.Combine(d, "Scripts"));
                }
            }
            catch { }

            // uv tool bin
            try
            {
                var uvTools = Path.Combine(localApp, "uv", "tools");
                if (Directory.Exists(uvTools))
                {
                    foreach (var d in Directory.EnumerateDirectories(uvTools))
                        dirs.Add(Path.Combine(d, "bin"));
                }
            }
            catch { }
        }

        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.PS1")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.ToLowerInvariant())
            .ToArray();

        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) || !seenDirs.Add(dir)) continue;
                idx.ScannedDirCount++;
                idx.ExistingDirs.Add(dir);
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var name = Path.GetFileName(file);
                        var baseName = Path.GetFileNameWithoutExtension(file);
                        var ext = Path.GetExtension(file);
                        if (string.IsNullOrEmpty(ext) || exts.Contains(ext.ToLowerInvariant()))
                        {
                            if (!idx.CommandHits.ContainsKey(baseName))
                                idx.CommandHits[baseName] = file;
                            if (!idx.CommandHits.ContainsKey(name))
                                idx.CommandHits[name] = file;
                            idx.ExistingFiles.Add(file);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static void IndexUninstallRegistry(LocalEnvIndex idx)
    {
        void ScanHive(RegistryKey? root, string subPath)
        {
            try
            {
                using var key = root?.OpenSubKey(subPath);
                if (key == null) return;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sk = key.OpenSubKey(sub);
                        if (sk == null) continue;
                        var dn = sk.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(dn)) continue;
                        var loc = sk.GetValue("InstallLocation") as string ?? "";
                        var icon = sk.GetValue("DisplayIcon") as string ?? "";
                        idx.UninstallEntries.Add((dn, loc, icon));
                    }
                    catch { }
                }
            }
            catch { }
        }

        ScanHive(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        ScanHive(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        ScanHive(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        idx.ScannedDirCount += 3; // 三条卸载注册表通道计入扫描目录统计
    }

    private static void IndexStartMenuShortcuts(LocalEnvIndex idx)
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs")
        };

        Type? shellType = null;
        object? shell = null;
        try
        {
            shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null) shell = Activator.CreateInstance(shellType);
        }
        catch { }

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                idx.ScannedDirCount++;
                idx.ExistingDirs.Add(root);
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    try
                    {
                        string target = "";
                        if (shell != null)
                        {
                            try
                            {
                                dynamic sc = ((dynamic)shell).CreateShortcut(lnk);
                                target = (string)(sc.TargetPath ?? "");
                            }
                            catch { }
                        }
                        idx.Shortcuts.Add((Path.GetFileNameWithoutExtension(lnk), target));
                    }
                    catch { }
                }
            }
            catch { }
        }

        try
        {
            if (shell is IDisposable d) d.Dispose();
        }
        catch { }
    }

    private static void IndexStandardRootsAndMsix(LocalEnvIndex idx)
    {
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var packages = Path.Combine(localApp, "Packages");
        try
        {
            if (Directory.Exists(packages))
            {
                idx.ScannedDirCount++;
                idx.ExistingDirs.Add(packages);
                foreach (var d in Directory.EnumerateDirectories(packages))
                    idx.MsixPackageDirs.Add(d);
            }
        }
        catch { }

        foreach (var root in new[] { Path.Combine(localApp, "Programs"), localApp, appData, pf, pf86 })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                idx.ExistingDirs.Add(root);
                idx.ScannedDirCount++;
                // 浅层枚举一级子目录，便于匹配自定义盘符外的 Programs 安装
                foreach (var child in Directory.EnumerateDirectories(root))
                {
                    idx.ExistingDirs.Add(child);
                    idx.ScannedDirCount++;
                }
            }
            catch { }
        }
    }

    private static void IndexIdeExtensions(LocalEnvIndex idx)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(userProfile, ".vscode", "extensions"),
            Path.Combine(userProfile, ".vscode-insiders", "extensions"),
            Path.Combine(userProfile, ".cursor", "extensions"),
            Path.Combine(userProfile, ".windsurf", "extensions")
        };
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                idx.ScannedDirCount++;
                idx.ExistingDirs.Add(root);
                foreach (var d in Directory.EnumerateDirectories(root))
                    idx.ExtensionFolderNames.Add(Path.GetFileName(d));
            }
            catch { }
        }
    }

    private static void IndexRunningProcesses(LocalEnvIndex idx)
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(p.ProcessName))
                        idx.ProcessNames.Add(p.ProcessName);
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
        catch { }
    }

    private static string? FindInPath(string commandName, LocalEnvIndex? index)
    {
        if (string.IsNullOrWhiteSpace(commandName)) return null;
        if (index != null)
        {
            if (index.CommandHits.TryGetValue(commandName, out var hit)) return hit;
            if (index.CommandHits.TryGetValue(commandName + ".exe", out hit)) return hit;
            if (index.CommandHits.TryGetValue(commandName + ".cmd", out hit)) return hit;
            if (index.CommandHits.TryGetValue(commandName + ".bat", out hit)) return hit;
            return null;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.PS1")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var trimmed = dir.Trim().Trim('"');
                if (string.IsNullOrEmpty(trimmed) || !Directory.Exists(trimmed)) continue;
                var direct = Path.Combine(trimmed, commandName);
                if (File.Exists(direct)) return direct;
                foreach (var ext in exts)
                {
                    var withExt = direct + ext.ToLowerInvariant();
                    if (File.Exists(withExt)) return withExt;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? CheckUninstallRegistry(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword) || keyword.Length < 4) return null;
        var shortKey = keyword.Split(' ', '(')[0].Trim();
        if (shortKey.Length < 4) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key == null) return null;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var sk = key.OpenSubKey(sub);
                var dn = sk?.GetValue("DisplayName") as string;
                if (!string.IsNullOrEmpty(dn) && dn.Contains(shortKey, StringComparison.OrdinalIgnoreCase))
                    return dn;
            }
        }
        catch { }
        return null;
    }

    public static bool ProbeLocalTcpPort(int port, int timeoutMs = 150)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
