using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AICollabHub.Models;

namespace AICollabHub.Services;

public sealed record ModelOwnershipInfo(
    bool IsNativeOwn,
    string OwnershipKind,
    string VendorName,
    string BadgeText,
    string ShortBadge);

public sealed record LocalCatalogModelDetail(
    string ModelName,
    ModelOwnershipInfo Ownership,
    string RecommendedTier,
    int ContextTokens,
    string SpecialtyHint);

public sealed class LocalClientModelsResult
{
    public string SourceDescription { get; set; } = "";
    public bool ReadFromLocalClient { get; set; }
    public List<string> Models { get; set; } = new();
    public string SuggestedHigh { get; set; } = "";
    public string SuggestedMedium { get; set; } = "";
    public string SuggestedLow { get; set; } = "";
}

/// <summary>
/// 直接从本机各 AI 客户端真实配置文件 / SQLite 状态库 / 模型缓存读取真实可用模型列表（零第三方依赖，毫秒级读取）
/// </summary>
public static class LocalModelCatalogService
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (DateTime CachedUtc, LocalClientModelsResult Result)> Cache = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);

    public const string ManagedDefaultModelDisplay = "客户端默认托管（禁止切换模型·防风控）";

    /// <summary>
    /// 判断某个 AI 工具是否允许用户在前端配置和切换具体模型（类似 Grok Bot 这种由客户端全托管默认模型的工具必须锁定禁选，避免触发风控）
    /// </summary>
    public static bool SupportsModelSelection(string? agentId)
    {
        var id = (agentId ?? "").Trim().ToLowerInvariant();
        return id != "grok_bot";
    }

    /// <summary>
    /// 仅自建外部节点 / 本地自定义大模型允许手填自定义模型名；已知桌面端（Antigravity / Cursor / Codex / Grok Bot / Windsurf / Claude Code）严格对齐客户端可选模型，禁止手输或混入桌面端不可选模型
    /// </summary>
    public static bool SupportsCustomModelInput(string? agentId)
    {
        var id = (agentId ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id)) return false;
        return id is not ("antigravity" or "cursor" or "codex_cli" or "grok_bot" or "windsurf" or "claude_code");
    }

    public static ModelOwnershipInfo ClassifyModelOwnership(
        string? agentId,
        string? modelName,
        string? explicitOwnershipKind = null,
        string? explicitVendor = null,
        string? agentCategory = null)
    {
        var aid = (agentId ?? "").Trim().ToLowerInvariant();
        var mn = (modelName ?? "").Trim();
        var lower = mn.ToLowerInvariant();

        if (string.Equals(mn, ManagedDefaultModelDisplay, StringComparison.OrdinalIgnoreCase))
        {
            var managedVendor = aid switch
            {
                "grok_bot" => "xAI",
                "cursor" => "Cursor 自研",
                "codex_cli" => "OpenAI",
                "claude_code" => "Anthropic",
                "antigravity" => "Google",
                _ => "官方托管"
            };
            return new ModelOwnershipInfo(true, "native", managedVendor, $"🏠 自有原生 · {managedVendor}", "🏠自有");
        }

        string vendor = (explicitVendor ?? "").Trim();
        if (string.IsNullOrEmpty(vendor))
        {
            if (lower.Contains("composer") || lower.Contains("cursor-") ||
                (aid == "cursor" && (lower.Contains("auto") || lower == "default")))
                vendor = "Cursor 自研";
            else if (lower.Contains("gemini") || lower.Contains("orchestrator") || lower.Contains("gemma") ||
                     lower.Contains("imagen") || lower.Contains("veo") || lower.Contains("google"))
                vendor = "Google";
            else if (lower.Contains("claude") || lower.Contains("opus") || lower.Contains("sonnet") ||
                     lower.Contains("haiku") || lower.Contains("anthropic"))
                vendor = "Anthropic";
            else if (lower.Contains("gpt-") || lower.Contains("gpt ") || lower.Contains("codex") ||
                     lower.Contains("-luna") || lower.Contains("-terra") || lower.Contains("-sol") ||
                     lower.Contains("sora") || lower.Contains("dall") ||
                     Regex.IsMatch(lower, @"\b(o1|o3|o4)\b"))
                vendor = "OpenAI";
            else if (lower.Contains("grok") || lower.Contains("aurora"))
                vendor = "xAI";
            else if (lower.Contains("deepseek"))
                vendor = "DeepSeek";
            else if (lower.Contains("qwen") || lower.Contains("tongyi"))
                vendor = "阿里千问";
            else if (lower.Contains("glm"))
                vendor = "智谱";
            else if (lower.Contains("kimi") || lower.Contains("moonshot"))
                vendor = "Moonshot";
            else if (lower.Contains("swe-") || lower.Contains("cascade"))
                vendor = "Windsurf 自研";
            else if (string.Equals(agentCategory, "local_llm", StringComparison.OrdinalIgnoreCase) || aid.Contains("ollama"))
                vendor = "本地私有";
            else
                vendor = "外部模型";
        }

        bool autoNative = aid switch
        {
            "antigravity" => vendor == "Google",
            "cursor" => vendor == "Cursor 自研",
            "codex_cli" => vendor == "OpenAI",
            "claude_code" => vendor == "Anthropic",
            "grok_bot" => vendor == "xAI",
            "windsurf" => vendor == "Windsurf 自研",
            _ => string.Equals(agentCategory, "local_llm", StringComparison.OrdinalIgnoreCase) ||
                 aid.Contains("ollama", StringComparison.OrdinalIgnoreCase)
        };

        bool isNative = explicitOwnershipKind?.Trim().ToLowerInvariant() switch
        {
            "native" => true,
            "third_party" => false,
            _ => autoNative
        };

        string kind = isNative ? "native" : "third_party";
        string badge = isNative ? $"🏠 自有原生 · {vendor}" : $"🌐 第三方 · {vendor}";
        string shortBadge = isNative ? "🏠自有" : $"🌐{vendor}";
        return new ModelOwnershipInfo(isNative, kind, vendor, badge, shortBadge);
    }

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    public static void InvalidateAllCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    /// <summary>
    /// 一键实时同步所有 AI 工具（Antigravity / Cursor / Codex / Claude Code / Windsurf / Grok Bot / 外部节点）的最新可选模型列表
    /// </summary>
    public static void SyncAllAgentsWithLocalClientModels(IEnumerable<AgentRosterEntry> agents, bool invalidateCache = true)
    {
        if (invalidateCache)
            InvalidateAllCache();

        foreach (var ag in agents)
        {
            if (ag == null) continue;
            SyncAgentWithLocalClientModels(ag, forceOverwriteTiers: false);
        }
    }

    public static List<LocalCatalogModelDetail> GetClassifiedCatalogForAgent(AgentRosterEntry agent, bool forceRefresh = false)
    {
        var probe = ProbeLocalModelsForAgent(agent.Id, forceRefresh);
        var names = new List<string>();
        // 对于严格对齐本地桌面客户端的工具（Antigravity / Cursor / Codex / Grok Bot），
        // 以最新实时探测到的模型目录为准，不再混入旧配置文件里残留的下线模型
        var sourceNames = SupportsCustomModelInput(agent.Id)
            ? probe.Models.Concat(agent.AvailableModels ?? new List<string>())
            : (probe.Models.Count > 0 ? probe.Models : (agent.AvailableModels ?? new List<string>()));
        foreach (var m in sourceNames)
        {
            if (!string.IsNullOrWhiteSpace(m) && !IsLegacyBogusModelName(m) &&
                !names.Contains(m.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                names.Add(m.Trim());
            }
        }
        var results = new List<LocalCatalogModelDetail>(names.Count);
        foreach (var mn in names)
        {
            var mp = agent.ModelProfiles?.FirstOrDefault(p => string.Equals(p.ModelName, mn, StringComparison.OrdinalIgnoreCase));
            var own = ClassifyModelOwnership(agent.Id, mn, mp?.OwnershipKind, mp?.VendorName, agent.Category);
            var spec = CloudRecommendationService.InferCloudModelSpec(agent.Id, mn);
            results.Add(new LocalCatalogModelDetail(mn, own, spec.Tier, spec.ContextWindowTokens, spec.SpecialtyNote));
        }
        return results;
    }

    public static LocalClientModelsResult ProbeLocalModelsForAgent(string? agentId, bool forceRefresh = false)
    {
        var id = (agentId ?? "").Trim().ToLowerInvariant();
        LocalClientModelsResult? previousCached = null;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(id, out var hit))
            {
                previousCached = hit.Result;
                if (!forceRefresh && (DateTime.UtcNow - hit.CachedUtc) < CacheTtl)
                {
                    return hit.Result;
                }
            }
        }

        LocalClientModelsResult res = !SupportsModelSelection(id)
            ? BuildManagedLockedModelResult()
            : id switch
            {
                "antigravity" => ReadAntigravityLocalModels(),
                "cursor" => ReadCursorLocalModels(previousCached),
                "codex_cli" => ReadCodexAndGrokLocalModels(id, previousCached),
                "claude_code" => ReadClaudeCodeLocalModels(previousCached),
                "windsurf" => ReadWindsurfLocalModels(previousCached),
                "ollama_local" => ReadOllamaLocalModels(previousCached),
                "lm_studio" => ReadLmStudioLocalModels(previousCached),
                _ => BuildGenericFallbackModels()
            };

        lock (CacheLock)
        {
            Cache[id] = (DateTime.UtcNow, res);
        }
        return res;
    }

    private static LocalClientModelsResult BuildManagedLockedModelResult() => new()
    {
        ReadFromLocalClient = true,
        SourceDescription = "该客户端不支持切换模型 · 已锁定默认托管以防封号",
        Models = new List<string> { ManagedDefaultModelDisplay },
        SuggestedHigh = ManagedDefaultModelDisplay,
        SuggestedMedium = ManagedDefaultModelDisplay,
        SuggestedLow = ManagedDefaultModelDisplay
    };

    /// <summary>
    /// 同步刷新某个 Agent 的本机真实模型列表，并自动修复旧版本遗留的假拼装/截断错误模型名
    /// </summary>
    public static LocalClientModelsResult SyncAgentWithLocalClientModels(AgentRosterEntry agent, bool forceOverwriteTiers = false)
    {
        var res = ProbeLocalModelsForAgent(agent.Id, forceRefresh: forceOverwriteTiers);
        if (!SupportsModelSelection(agent.Id))
        {
            // 对不允许选模型的客户端（如 Grok Bot），强制收敛为单条默认托管占位并清除此前残存的可选模型列表，仅保留职责分工
            agent.AvailableModels = new List<string> { ManagedDefaultModelDisplay };
            agent.HighTierModel = ManagedDefaultModelDisplay;
            agent.MediumTierModel = ManagedDefaultModelDisplay;
            agent.LowTierModel = ManagedDefaultModelDisplay;
            agent.PreferredEffortTier = "auto";
            agent.ModelSelectionMode = "auto";

            var firstExisting = agent.ModelProfiles?.FirstOrDefault();
            var preservedRoles = (agent.ModelProfiles ?? new List<ModelWorkProfile>())
                .SelectMany(p => (p.PrimaryRoles ?? new List<string>())
                    .Concat((p.CapabilityScores ?? new Dictionary<string, int>())
                        .Where(kv => kv.Value > 0)
                        .Select(kv => kv.Key)))
                .Concat(agent.PrimaryWorkRoles ?? new List<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (preservedRoles.Count == 0 || forceOverwriteTiers)
            {
                preservedRoles = new List<string> { "text_writing", "image_generation", "video_animation" };
            }
            agent.PrimaryWorkRoles = new List<string>(preservedRoles);

            // 托管客户端（如 Grok Bot）必须完整保留方案中配置的 1~5★ 能力，禁止 Sync 清空
            var preservedScores = firstExisting?.CapabilityScores != null
                ? new Dictionary<string, int>(firstExisting.CapabilityScores, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (firstExisting?.CapabilityScores != null && firstExisting.CapabilityScores.Count > 0)
            {
                agent.TagScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in firstExisting.CapabilityScores)
                {
                    if (kv.Value <= 0 || string.IsNullOrWhiteSpace(kv.Key)) continue;
                    int next = Math.Clamp(kv.Value, 1, 5);
                    if (!agent.TagScores.TryGetValue(kv.Key, out var old) || next > old)
                        agent.TagScores[kv.Key] = next;
                }
            }

            agent.ModelProfiles = new List<ModelWorkProfile>
            {
                new()
                {
                    ModelName = ManagedDefaultModelDisplay,
                    PrimaryRoles = preservedRoles,
                    CapabilityScores = preservedScores,
                    Enabled = firstExisting?.Enabled ?? true,
                    UsagePolicy = string.IsNullOrWhiteSpace(firstExisting?.UsagePolicy)
                        ? "primary_default"
                        : firstExisting!.UsagePolicy,
                    TaskDifficultyRule = string.IsNullOrWhiteSpace(firstExisting?.TaskDifficultyRule)
                        ? "any"
                        : firstExisting!.TaskDifficultyRule,
                    OwnershipKind = firstExisting?.OwnershipKind ?? "",
                    VendorName = firstExisting?.VendorName ?? "",
                    SpeedTier = string.IsNullOrWhiteSpace(firstExisting?.SpeedTier)
                        ? "medium"
                        : firstExisting!.SpeedTier,
                    RecommendedEffort = string.IsNullOrWhiteSpace(firstExisting?.RecommendedEffort)
                        ? "medium"
                        : firstExisting!.RecommendedEffort,
                    SpecialtyNote = firstExisting != null
                        ? (firstExisting.SpecialtyNote ?? "")
                        : "由客户端自动托管默认模型，不向服务端传递切换模型参数（防封号）",
                    CustomTemperature = firstExisting?.CustomTemperature,
                    ContextWindowTokens = firstExisting?.ContextWindowTokens > 0 ? firstExisting.ContextWindowTokens : 131072,
                    MaxOutputTokens = firstExisting?.MaxOutputTokens > 0 ? firstExisting.MaxOutputTokens : 16384
                }
            };
            return res;
        }

        bool allowCustom = SupportsCustomModelInput(agent.Id);
        if (res.Models.Count > 0)
        {
            var merged = new List<string>(res.Models);
            if (allowCustom && agent.ModelProfiles != null)
            {
                foreach (var mp in agent.ModelProfiles)
                {
                    if (!string.IsNullOrWhiteSpace(mp.ModelName) &&
                        !IsLegacyBogusModelName(mp.ModelName) &&
                        !merged.Contains(mp.ModelName, StringComparer.OrdinalIgnoreCase))
                    {
                        merged.Add(mp.ModelName);
                    }
                }
            }
            agent.AvailableModels = merged;
        }

        bool IsValidClientModel(string? m) =>
            !string.IsNullOrWhiteSpace(m) &&
            !IsLegacyBogusModelName(m) &&
            (allowCustom || agent.AvailableModels.Contains(m.Trim(), StringComparer.OrdinalIgnoreCase));

        bool needFixHigh = forceOverwriteTiers || !IsValidClientModel(agent.HighTierModel);
        bool needFixMed = forceOverwriteTiers || !IsValidClientModel(agent.MediumTierModel);
        bool needFixLow = forceOverwriteTiers || !IsValidClientModel(agent.LowTierModel);

        if (needFixHigh && !string.IsNullOrWhiteSpace(res.SuggestedHigh))
            agent.HighTierModel = res.SuggestedHigh;
        if (needFixMed && !string.IsNullOrWhiteSpace(res.SuggestedMedium))
            agent.MediumTierModel = res.SuggestedMedium;
        if (needFixLow && !string.IsNullOrWhiteSpace(res.SuggestedLow))
            agent.LowTierModel = res.SuggestedLow;

        // 同步清理 ModelProfiles 里的旧版假模型名及桌面端已停用/不可选的隐藏模型
        if (forceOverwriteTiers || (agent.ModelProfiles != null && agent.ModelProfiles.Any(p => !IsValidClientModel(p.ModelName))))
        {
            if (agent.ModelProfiles != null && !forceOverwriteTiers)
            {
                var kept = agent.ModelProfiles.Where(p => IsValidClientModel(p.ModelName)).ToList();
                var droppedRoles = agent.ModelProfiles
                    .Where(p => !IsValidClientModel(p.ModelName))
                    .SelectMany(p => p.PrimaryRoles ?? new List<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (kept.Count > 0 && droppedRoles.Count > 0)
                {
                    var fbTarget = kept[^1];
                    foreach (var dr in droppedRoles)
                    {
                        if (!fbTarget.PrimaryRoles.Contains(dr, StringComparer.OrdinalIgnoreCase))
                            fbTarget.PrimaryRoles.Add(dr);
                    }
                    agent.ModelProfiles = kept;
                    CloudRecommendationService.SyncDerivedFieldsFromProfiles(agent);
                    return res;
                }
            }
            agent.ModelProfiles = new List<ModelWorkProfile>();
            CloudRecommendationService.EnsureAgentWorkProfilesPopulated(agent, overwrite: true);
        }

        return res;
    }

    public static bool IsLegacyBogusModelName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var s = name.Trim();
        // 识别早期版本硬编码的假拼接模型名或手滑截断残串
        return s.Contains("Gemini-3.1-Pro-", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("Claude-4.6-Opus-Thinking", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("Claude-4.6-Sonnet", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("Gemini-3-Flash-Low", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("wGemini", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("ledium", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("claude-4.6-opus-high (", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("claude-4.6-sonnet-medium (", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("cursor-fast-low (", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("gpt-6-luna (高档", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("gpt-6-luna (中档", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("gpt-6-luna (低档", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("(高档·", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("(中档·", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("(低档·", StringComparison.OrdinalIgnoreCase);
    }

    private static LocalClientModelsResult ReadAntigravityLocalModels()
    {
        // Antigravity 客户端真实模型菜单列表 + 从本机 ~/.gemini/antigravity 会话日志/对话库实时补充新出现的模型名
        var list = new List<string>
        {
            "Gemini 4 Neon",
            "Gemini 4 Carbon",
            "Gemini 4 Argon",
            "Gemini 4 Hydrogen",
            "Orchestrator (Neon)",
            "Orchestrator (Carbon)",
            "Orchestrator (Argon)",
            "Gemini 3.8 Flash",
            "Claude Opus 5.5",
            "Claude Sonnet 5.5",
            "GPT-OSS 120B (Medium)"
        };

        bool foundLocalLog = false;
        try
        {
            var agyHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity");
            var brainDir = Path.Combine(agyHome, "brain");
            var switchRx = new Regex(@"Model Selection` from ([^`\r\n]+?) to ([^`\.\r\n]+)", RegexOptions.IgnoreCase);
            var genericModelRx = new Regex(@"\b(Gemini\s+[4-9](?:\.\d+)?\s+[A-Z][a-zA-Z0-9]+|Orchestrator\s*\([A-Z][a-zA-Z0-9]+\)|Claude\s+(?:Opus|Sonnet|Haiku)\s+[5-9](?:\.\d+)?)\b");

            void TryAddDetectedModel(string raw)
            {
                var cleaned = Regex.Replace(raw.Trim(), @"\s*\((High|Medium|Low)\)\s*$", "", RegexOptions.IgnoreCase).Trim();
                // 过滤掉已下线的旧 Claude Opus 5（当前官方已升级为 5.5）或无效拼装串
                if (string.IsNullOrWhiteSpace(cleaned) || IsLegacyBogusModelName(cleaned)) return;
                if (string.Equals(cleaned, "Claude Opus 5", StringComparison.OrdinalIgnoreCase)) return;
                if (!list.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                {
                    list.Insert(0, cleaned);
                }
            }

            if (Directory.Exists(brainDir))
            {
                var recentLogs = new DirectoryInfo(brainDir)
                    .GetDirectories()
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .Take(5)
                    .Select(d => Path.Combine(d.FullName, ".system_generated", "logs", "transcript.jsonl"))
                    .Where(File.Exists);

                foreach (var logFile in recentLogs)
                {
                    foundLocalLog = true;
                    using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    int toRead = (int)Math.Min(fs.Length, 256 * 1024);
                    if (fs.Length > toRead) fs.Seek(-toRead, SeekOrigin.End);
                    var buf = new byte[toRead];
                    int read = fs.Read(buf, 0, toRead);
                    var text = Encoding.UTF8.GetString(buf, 0, read);
                    foreach (Match m in switchRx.Matches(text))
                    {
                        TryAddDetectedModel(m.Groups[1].Value);
                        TryAddDetectedModel(m.Groups[2].Value);
                    }
                    foreach (Match m in genericModelRx.Matches(text))
                    {
                        TryAddDetectedModel(m.Groups[1].Value);
                    }
                }
            }

            // 同步扫描最近活跃的 conversations/*.db-wal（毫秒级，捕获刚刚切换的新模型）
            var convDir = Path.Combine(agyHome, "conversations");
            if (Directory.Exists(convDir))
            {
                var recentWals = new DirectoryInfo(convDir)
                    .GetFiles("*.db-wal")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(3);
                foreach (var wf in recentWals)
                {
                    try
                    {
                        using var fs = new FileStream(wf.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        int toRead = (int)Math.Min(fs.Length, 128 * 1024);
                        if (fs.Length > toRead) fs.Seek(-toRead, SeekOrigin.End);
                        var buf = new byte[toRead];
                        int read = fs.Read(buf, 0, toRead);
                        var text = Encoding.UTF8.GetString(buf, 0, read);
                        foreach (Match m in genericModelRx.Matches(text))
                        {
                            TryAddDetectedModel(m.Groups[1].Value);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return new LocalClientModelsResult
        {
            ReadFromLocalClient = foundLocalLog || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity")),
            SourceDescription = $"已实时对齐本机 Antigravity 客户端模型菜单（共 {list.Count} 个模型）",
            Models = list,
            SuggestedHigh = "Gemini 4 Neon",
            SuggestedMedium = "Gemini 4 Carbon",
            SuggestedLow = "Gemini 3.8 Flash"
        };
    }

    private static LocalClientModelsResult ReadCursorLocalModels(LocalClientModelsResult? previousCached = null)
    {
        var list = new List<string>();
        string activeComposerModel = "";
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");

        if (!AppPaths.IsIsolatedTest && File.Exists(dbPath))
        {
            try
            {
                var appUserJson = QueryWinSqliteSingleValue(dbPath, "src.vs.platform.reactivestorage.browser.reactiveStorageServiceImpl.persistentStorage.applicationUser");
                if (!string.IsNullOrWhiteSpace(appUserJson))
                {
                    using var doc = JsonDocument.Parse(appUserJson);
                    var overrideEnabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var overrideDisabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (doc.RootElement.TryGetProperty("aiSettings", out var aiSettings))
                    {
                        if (aiSettings.TryGetProperty("modelConfig", out var mc) &&
                            mc.TryGetProperty("composer", out var comp) &&
                            comp.TryGetProperty("modelName", out var mnProp))
                        {
                            activeComposerModel = mnProp.GetString() ?? "";
                        }
                        if (aiSettings.TryGetProperty("modelOverrideEnabled", out var oeProp) && oeProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var e in oeProp.EnumerateArray())
                            {
                                var sv = e.GetString();
                                if (!string.IsNullOrWhiteSpace(sv)) overrideEnabled.Add(sv.Trim());
                            }
                        }
                        if (aiSettings.TryGetProperty("modelOverrideDisabled", out var odProp) && odProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var e in odProp.EnumerateArray())
                            {
                                var sv = e.GetString();
                                if (!string.IsNullOrWhiteSpace(sv)) overrideDisabled.Add(sv.Trim());
                            }
                        }
                    }

                    if (doc.RootElement.TryGetProperty("availableDefaultModels2", out var modelsArr) &&
                        modelsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in modelsArr.EnumerateArray())
                        {
                            var slug = el.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "") : "";
                            var disp = el.TryGetProperty("clientDisplayName", out var dProp) ? (dProp.GetString() ?? "") : "";
                            if (string.IsNullOrWhiteSpace(slug) && string.IsNullOrWhiteSpace(disp)) continue;

                            // 仅保留桌面端实际允许选中与开启的模型（排除 defaultOn=false 未开启及 supportsAgent=false 的隐藏/下线模型）
                            if (el.TryGetProperty("supportsAgent", out var saProp) && saProp.ValueKind == JsonValueKind.False)
                                continue;
                            bool isDefaultOn = el.TryGetProperty("defaultOn", out var defProp) && defProp.ValueKind == JsonValueKind.True;
                            bool isEnabledInDesktop = (isDefaultOn || overrideEnabled.Contains(slug)) && !overrideDisabled.Contains(slug);
                            if (!isEnabledInDesktop)
                                continue;

                            string formatted = !string.IsNullOrWhiteSpace(disp) && !string.Equals(disp, slug, StringComparison.OrdinalIgnoreCase)
                                ? $"{disp} ({slug})"
                                : (!string.IsNullOrWhiteSpace(disp) ? disp : slug);

                            if (!list.Contains(formatted, StringComparer.OrdinalIgnoreCase))
                            {
                                list.Add(formatted);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        bool fromLocal = list.Count > 0;
        if (!fromLocal && previousCached != null && previousCached.ReadFromLocalClient && previousCached.Models.Count > 0)
        {
            return previousCached;
        }
        if (!fromLocal)
        {
            list.AddRange(new[]
            {
                "Claude Opus 5.5 (claude-opus-5-5)",
                "Claude Sonnet 5.5 (claude-sonnet-5-5)",
                "Claude Fable 5.2 (claude-fable-5-2)",
                "Gemini 4 Neon (gemini-4-neon)",
                "Gemini 4 Carbon (gemini-4-carbon)",
                "Grok 4.7 (grok-4.7)",
                "Grok 4.6 (grok-4.6)",
                "Composer 2.5 (composer-2.5)",
                "Muse Wonder (muse-wonder-1.0)",
                "GPT-5.6 Sol (gpt-5.6-sol)",
                "Auto (default)"
            });
        }

        string FindOrFirst(string keyword, string fallback) =>
            list.FirstOrDefault(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ?? fallback;

        string high = FindOrFirst("Claude Opus 5.5", list[0]);
        if (!string.IsNullOrWhiteSpace(activeComposerModel) && !activeComposerModel.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            var matchedActive = list.FirstOrDefault(x => x.Contains($"({activeComposerModel})", StringComparison.OrdinalIgnoreCase) || x.Equals(activeComposerModel, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(matchedActive)) high = matchedActive;
        }
        string med = FindOrFirst("Claude Sonnet 5.5", FindOrFirst("Composer 2.5", list[0]));
        string low = FindOrFirst("Gemini 4 Hydrogen", FindOrFirst("Auto (default)", list[^1]));

        return new LocalClientModelsResult
        {
            ReadFromLocalClient = fromLocal,
            SourceDescription = fromLocal
                ? $"已对齐 Cursor 桌面端已启用模型（共 {list.Count} 个可选）"
                : "使用 Cursor 桌面端标准可选模型目录",
            Models = list,
            SuggestedHigh = high,
            SuggestedMedium = med,
            SuggestedLow = low
        };
    }

    private static LocalClientModelsResult ReadCodexAndGrokLocalModels(string id, LocalClientModelsResult? previousCached = null)
    {
        var list = new List<string>();
        string defaultConfigModel = "";
        var codexDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var cacheFile = Path.Combine(codexDir, "models_cache.json");
        var cfgFile = Path.Combine(codexDir, "config.toml");

        try
        {
            if (File.Exists(cfgFile))
            {
                foreach (var line in File.ReadAllLines(cfgFile))
                {
                    var m = Regex.Match(line.Trim(), @"^model\s*=\s*""([^""]+)""");
                    if (m.Success) { defaultConfigModel = m.Groups[1].Value.Trim(); break; }
                }
            }

            if (File.Exists(cacheFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cacheFile, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var slug = el.TryGetProperty("slug", out var sProp) ? (sProp.GetString() ?? "") : "";
                        var disp = el.TryGetProperty("display_name", out var dProp) ? (dProp.GetString() ?? "") : "";
                        if (string.IsNullOrWhiteSpace(slug)) continue;
                        // 过滤 Codex 内部隐藏或桌面不可选模型 (visibility="hide" / supported_in_api=false)
                        if (el.TryGetProperty("visibility", out var vProp) &&
                            string.Equals(vProp.GetString(), "hide", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (el.TryGetProperty("supported_in_api", out var apiProp) && apiProp.ValueKind == JsonValueKind.False)
                            continue;

                        var label = !string.IsNullOrWhiteSpace(disp) && !disp.Equals(slug, StringComparison.OrdinalIgnoreCase)
                            ? $"{disp} ({slug})"
                            : slug;
                        if (!list.Contains(label, StringComparer.OrdinalIgnoreCase))
                        {
                            list.Add(label);
                        }
                    }
                }
            }
        }
        catch { }

        bool fromLocal = list.Count > 0;
        if (!fromLocal && previousCached != null && previousCached.ReadFromLocalClient && previousCached.Models.Count > 0)
        {
            return previousCached;
        }
        if (!fromLocal)
        {
            list.AddRange(new[]
            {
                "GPT-6-Luna (gpt-6-luna)",
                "GPT-5.6-Terra (gpt-5.6-terra)",
                "GPT-5.6-Luna (gpt-5.6-luna)"
            });
        }

        if (!string.IsNullOrWhiteSpace(defaultConfigModel) &&
            !list.Any(x => x.Contains(defaultConfigModel, StringComparison.OrdinalIgnoreCase)))
        {
            list.Insert(0, defaultConfigModel);
        }

        string FindModel(string kw, string fb) =>
            list.FirstOrDefault(x => x.Contains(kw, StringComparison.OrdinalIgnoreCase)) ?? fb;

        string primary = !string.IsNullOrWhiteSpace(defaultConfigModel)
            ? FindModel(defaultConfigModel, list[0])
            : FindModel("gpt-6-luna", list[0]);

        return new LocalClientModelsResult
        {
            ReadFromLocalClient = fromLocal,
            SourceDescription = fromLocal
                ? $"已从本机 ~/.codex/models_cache.json 实时读取 {list.Count} 个模型"
                : "使用本机 Codex / Luna 默认模型集",
            Models = list,
            SuggestedHigh = primary,
            SuggestedMedium = FindModel("gpt-5.6-terra", primary),
            SuggestedLow = FindModel("gpt-5.6-luna", primary)
        };
    }

    private static LocalClientModelsResult ReadClaudeCodeLocalModels(LocalClientModelsResult? previousCached = null)
    {
        var list = new List<string>();
        bool fromLocal = false;
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidateFiles = new[]
            {
                Path.Combine(userProfile, ".claude", "settings.json"),
                Path.Combine(userProfile, ".claude.json")
            };
            foreach (var f in candidateFiles)
            {
                if (!File.Exists(f)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(f, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("model", out var mp) && mp.ValueKind == JsonValueKind.String)
                {
                    var mv = mp.GetString()?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(mv) && !list.Contains(mv, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(mv);
                        fromLocal = true;
                    }
                }
                if (doc.RootElement.TryGetProperty("availableModels", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var mv = el.ValueKind == JsonValueKind.String
                            ? el.GetString()?.Trim() ?? ""
                            : (el.TryGetProperty("id", out var idP) ? idP.GetString()?.Trim() ?? "" : "");
                        if (!string.IsNullOrWhiteSpace(mv) && !list.Contains(mv, StringComparer.OrdinalIgnoreCase))
                        {
                            list.Add(mv);
                            fromLocal = true;
                        }
                    }
                }
            }
        }
        catch { }

        foreach (var def in new[]
        {
            "Claude Opus 5.5 (claude-opus-5-5)",
            "Claude Sonnet 5.5 (claude-sonnet-5-5)",
            "Claude Haiku 4.5 (claude-haiku-4-5)"
        })
        {
            if (!list.Any(x => x.Contains(def.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase)))
                list.Add(def);
        }

        return new LocalClientModelsResult
        {
            ReadFromLocalClient = fromLocal,
            SourceDescription = fromLocal
                ? $"已从本机 ~/.claude 实时读取并同步 {list.Count} 个模型"
                : $"使用 Claude Code 官方可选模型目录（共 {list.Count} 个）",
            Models = list,
            SuggestedHigh = list.FirstOrDefault(x => x.Contains("Opus", StringComparison.OrdinalIgnoreCase)) ?? list[0],
            SuggestedMedium = list.FirstOrDefault(x => x.Contains("Sonnet", StringComparison.OrdinalIgnoreCase)) ?? list[0],
            SuggestedLow = list.FirstOrDefault(x => x.Contains("Haiku", StringComparison.OrdinalIgnoreCase)) ?? list[^1]
        };
    }

    private static LocalClientModelsResult ReadWindsurfLocalModels(LocalClientModelsResult? previousCached = null)
    {
        var list = new List<string>();
        bool fromLocal = false;
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Windsurf", "User", "globalStorage", "state.vscdb");
        if (!AppPaths.IsIsolatedTest && File.Exists(dbPath))
        {
            try
            {
                var json = QueryWinSqliteSingleValue(dbPath, "codeium.windsurf.modelConfig");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    foreach (Match m in Regex.Matches(json, @"""([a-zA-Z0-9\-\._]+(?:swe|cascade|claude|gemini|gpt)[a-zA-Z0-9\-\._ ]*)""", RegexOptions.IgnoreCase))
                    {
                        var val = m.Groups[1].Value.Trim();
                        if (!string.IsNullOrWhiteSpace(val) && val.Length <= 48 && !list.Contains(val, StringComparer.OrdinalIgnoreCase))
                        {
                            list.Add(val);
                            fromLocal = true;
                        }
                    }
                }
            }
            catch { }
        }

        if (list.Count == 0 && previousCached != null && previousCached.ReadFromLocalClient && previousCached.Models.Count > 0)
            return previousCached;

        if (list.Count == 0)
        {
            list.AddRange(new[]
            {
                "SWE-1.5 (Windsurf)",
                "Claude Opus 5.5",
                "Claude Sonnet 5.5",
                "Gemini 4 Carbon",
                "GPT-5.6 Sol"
            });
        }

        return new LocalClientModelsResult
        {
            ReadFromLocalClient = fromLocal,
            SourceDescription = fromLocal
                ? $"已从本机 Windsurf 客户端实时同步 {list.Count} 个模型"
                : $"使用 Windsurf 标准可选模型目录（共 {list.Count} 个）",
            Models = list,
            SuggestedHigh = list[0],
            SuggestedMedium = list.FirstOrDefault(x => x.Contains("Sonnet", StringComparison.OrdinalIgnoreCase)) ?? list[0],
            SuggestedLow = list[^1]
        };
    }

    private static LocalClientModelsResult ReadOllamaLocalModels(LocalClientModelsResult? previousCached = null)
    {
        var list = new List<string>();
        bool fromLocal = false;
        try
        {
            // 优先读本机 Ollama 清单文件，避免对每个工具起进程
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new[]
            {
                Path.Combine(home, ".ollama", "models", "manifests"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama", "models", "manifests")
            };
            foreach (var root in candidates)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var libraryDir in Directory.EnumerateDirectories(root))
                {
                    foreach (var modelDir in Directory.EnumerateDirectories(libraryDir))
                    {
                        var vendor = Path.GetFileName(libraryDir);
                        var model = Path.GetFileName(modelDir);
                        foreach (var tagFile in Directory.EnumerateDirectories(modelDir))
                        {
                            var tag = Path.GetFileName(tagFile);
                            var name = string.Equals(vendor, "library", StringComparison.OrdinalIgnoreCase)
                                ? $"{model}:{tag}"
                                : $"{vendor}/{model}:{tag}";
                            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
                            {
                                list.Add(name);
                                fromLocal = true;
                            }
                        }
                    }
                }
                if (list.Count > 0) break;
            }
        }
        catch { }

        if (list.Count == 0 && previousCached?.Models.Count > 0) return previousCached;
        if (list.Count == 0)
            list.AddRange(new[] { "llama3.2:latest", "qwen2.5:latest", "mistral:latest" });

        return new LocalClientModelsResult
        {
            ReadFromLocalClient = fromLocal,
            SourceDescription = fromLocal
                ? $"已从本机 Ollama 模型库同步 {list.Count} 个模型"
                : $"使用 Ollama 常见本地模型占位（共 {list.Count} 个）",
            Models = list,
            SuggestedHigh = list[0],
            SuggestedMedium = list.Count > 1 ? list[1] : list[0],
            SuggestedLow = list[^1]
        };
    }

    private static LocalClientModelsResult ReadLmStudioLocalModels(LocalClientModelsResult? previousCached = null)
    {
        var list = new List<string>();
        bool fromLocal = false;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var modelsRoot = Path.Combine(home, ".cache", "lm-studio", "models");
            if (Directory.Exists(modelsRoot))
            {
                foreach (var vendorDir in Directory.EnumerateDirectories(modelsRoot))
                {
                    foreach (var modelDir in Directory.EnumerateDirectories(vendorDir))
                    {
                        var name = $"{Path.GetFileName(vendorDir)}/{Path.GetFileName(modelDir)}";
                        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
                        {
                            list.Add(name);
                            fromLocal = true;
                        }
                    }
                }
            }
        }
        catch { }

        if (list.Count == 0 && previousCached?.Models.Count > 0) return previousCached;
        if (list.Count == 0)
            list.AddRange(new[] { "lmstudio-community/Meta-Llama-3.1-8B-Instruct-GGUF", "Qwen/Qwen2.5-7B-Instruct-GGUF" });

        return new LocalClientModelsResult
        {
            ReadFromLocalClient = fromLocal,
            SourceDescription = fromLocal
                ? $"已从本机 LM Studio 模型缓存同步 {list.Count} 个模型"
                : $"使用 LM Studio 常见本地模型占位（共 {list.Count} 个）",
            Models = list,
            SuggestedHigh = list[0],
            SuggestedMedium = list.Count > 1 ? list[1] : list[0],
            SuggestedLow = list[^1]
        };
    }

    private static LocalClientModelsResult BuildGenericFallbackModels() => new()
    {
        ReadFromLocalClient = false,
        SourceDescription = "通用模型列表",
        Models = new List<string> { "GPT-6-Luna (gpt-6-luna)", "Claude Sonnet 5.5", "Gemini 3.8 Flash" },
        SuggestedHigh = "GPT-6-Luna (gpt-6-luna)",
        SuggestedMedium = "Claude Sonnet 5.5",
        SuggestedLow = "Gemini 3.8 Flash"
    };

    private static string QueryWinSqliteSingleValue(string dbPath, string itemKey)
    {
        IntPtr db = IntPtr.Zero;
        try
        {
            var pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
            // SQLITE_OPEN_READONLY (0x01) | SQLITE_OPEN_NOMUTEX (0x00010000) = 0x00010001（严禁对活动 WAL 库使用 immutable=1，防止 B-Tree 脏页死循环）
            if (sqlite3_open_v2(pathBytes, out db, 0x00010001, IntPtr.Zero) != 0 || db == IntPtr.Zero)
                return "";

            sqlite3_busy_timeout(db, 120);

            var sql = "SELECT value FROM ItemTable WHERE key = '" + itemKey.Replace("'", "''") + "' LIMIT 1\0";
            var sqlBytes = Encoding.UTF8.GetBytes(sql);
            if (sqlite3_prepare_v2(db, sqlBytes, -1, out IntPtr stmt, IntPtr.Zero) != 0 || stmt == IntPtr.Zero)
                return "";

            try
            {
                if (sqlite3_step(stmt) == 100) // SQLITE_ROW
                {
                    int len = sqlite3_column_bytes(stmt, 0);
                    IntPtr ptr = sqlite3_column_blob(stmt, 0);
                    if (ptr != IntPtr.Zero && len > 0)
                    {
                        var buf = new byte[len];
                        Marshal.Copy(ptr, buf, 0, len);
                        return Encoding.UTF8.GetString(buf);
                    }
                }
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }
        catch
        {
            // 若极少数精简系统无 winsqlite3.dll，静默降级
        }
        finally
        {
            if (db != IntPtr.Zero)
            {
                try { sqlite3_close(db); } catch { }
            }
        }
        return "";
    }
}
