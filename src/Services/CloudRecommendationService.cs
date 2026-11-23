using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AICollabHub.Models;

namespace AICollabHub.Services;

public sealed class CloudAgentRecommendation
{
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string PresetId { get; set; } = "cloud_balanced";
    public string RoleSummary { get; set; } = "";
    public string BenchmarkBasis { get; set; } = "";
    public string RecommendedHighModel { get; set; } = "";
    public string RecommendedMediumModel { get; set; } = "";
    public string RecommendedLowModel { get; set; } = "";
    public List<string> AvailableModels { get; set; } = new();
    public Dictionary<string, int> RecommendedTags { get; set; } = new();
    public AgentPermissions RecommendedPermissions { get; set; } = new();
    public AgentRuntimeParams RecommendedRuntimeParams { get; set; } = new();
}

public sealed class GlobalFleetPresetInfo
{
    public string FleetId { get; init; } = "fleet_balanced";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed class OnlineModelCapabilityHint
{
    public string ModelId { get; set; } = "";
    public int ContextLength { get; set; }
    public int MaxCompletionTokens { get; set; }
    public bool SupportsReasoning { get; set; }
    public bool SupportsImageOutput { get; set; }
    public bool SupportsVisionInput { get; set; }
    public bool SupportsAudio { get; set; }
    public bool SupportsVideo { get; set; }
    public bool SupportsTools { get; set; }
    public bool IsHighCostFlagship { get; set; }
    public bool IsFastLowCost { get; set; }
    public double RecommendedTemperature { get; set; } = 0.3;
}

public sealed class CloudModelSpec
{
    public string Tier { get; set; } = "medium";       // "high" | "medium" | "low"
    public string SpeedTier { get; set; } = "medium";  // "high" | "medium" | "fast"
    public List<string> RecommendedRoles { get; set; } = new();
    public string CloudBadge { get; set; } = "";
    public string SpecialtyNote { get; set; } = "";
    public string SourceTag { get; set; } = "";
    public int ContextWindowTokens { get; set; } = 128000;
    public int MaxOutputTokens { get; set; } = 16384;
    public double RecommendedTemperature { get; set; } = 0.3;
}

public sealed class CloudSyncReport
{
    public bool FetchedOnline { get; set; }
    public string SourceName { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public int OnlineModelsAnalyzed { get; set; }
    public List<CloudAgentRecommendation> Items { get; set; } = new();
    public Dictionary<string, OnlineModelCapabilityHint> OnlineHints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CloudRecommendationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly object HintLock = new();
    private static Dictionary<string, OnlineModelCapabilityHint> _onlineHints = new(StringComparer.OrdinalIgnoreCase);
    private static bool _onlineCacheAttemptedLoad;
    private static int _lastOnlineAnalyzedCount;
    private static string _lastOnlineSourceName = "内置 SWE-bench / LiveBench 校准基准库";

    public static string CacheFilePath => Path.Combine(AppPaths.UserDataDir, "cloud_recommendations_cache.json");

    public static int CachedOnlineHintCount
    {
        get
        {
            EnsureOnlineCacheLoadedOnce();
            lock (HintLock) return _onlineHints.Count;
        }
    }

    private static void EnsureOnlineCacheLoadedOnce()
    {
        lock (HintLock)
        {
            if (_onlineCacheAttemptedLoad) return;
            _onlineCacheAttemptedLoad = true;
            try
            {
                if (File.Exists(CacheFilePath))
                {
                    var cached = JsonSerializer.Deserialize<CloudSyncReport>(File.ReadAllText(CacheFilePath, Encoding.UTF8));
                    if (cached?.OnlineHints != null && cached.OnlineHints.Count > 0)
                    {
                        _onlineHints = new Dictionary<string, OnlineModelCapabilityHint>(cached.OnlineHints, StringComparer.OrdinalIgnoreCase);
                        _lastOnlineAnalyzedCount = cached.OnlineModelsAnalyzed > 0 ? cached.OnlineModelsAnalyzed : _onlineHints.Count;
                        if (!string.IsNullOrWhiteSpace(cached.SourceName))
                            _lastOnlineSourceName = cached.SourceName;
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 纯后台线程安全的在线模型特征拉取：主源 OpenRouter + 备选公开源 LiteLLM 参数库，只做网络拉取与线程安全 Hint 字典更新
    /// </summary>
    public (bool FetchedOnline, int OnlineModelsAnalyzed, string SourceName) FetchOnlineHintsOnly()
    {
        if (AppPaths.IsIsolatedTest)
        {
            return (false, 0, "内置 SWE-bench / LiveBench 校准基准库");
        }

        // 主源：OpenRouter 实时模型参数库
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
            req.Headers.Add("User-Agent", "AICollabHub-Commercial/2.0");
            using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
            {
                var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    int onlineCount = dataArr.GetArrayLength();
                    var parsedHints = new Dictionary<string, OnlineModelCapabilityHint>(StringComparer.OrdinalIgnoreCase);
                    foreach (var el in dataArr.EnumerateArray())
                    {
                        var id = el.TryGetProperty("id", out var idProp) ? (idProp.GetString() ?? "") : "";
                        if (!string.IsNullOrWhiteSpace(id) &&
                            !id.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase) &&
                            !id.Equals("auto", StringComparison.OrdinalIgnoreCase))
                        {
                            var hint = ExtractOnlineHint(id, el);
                            parsedHints[id] = hint;
                            var slashIdx = id.IndexOf('/');
                            if (slashIdx >= 0 && slashIdx + 1 < id.Length)
                            {
                                var slug = id[(slashIdx + 1)..];
                                if (!slug.StartsWith("auto", StringComparison.OrdinalIgnoreCase) && !parsedHints.ContainsKey(slug))
                                    parsedHints[slug] = hint;
                            }
                        }
                    }

                    if (parsedHints.Count > 0)
                    {
                        var src = $"OpenRouter 实时模型库 ({onlineCount} 个模型在线分析) + LiveBench 协同矩阵";
                        lock (HintLock)
                        {
                            _onlineHints = parsedHints;
                            _onlineCacheAttemptedLoad = true;
                            _lastOnlineAnalyzedCount = onlineCount;
                            _lastOnlineSourceName = src;
                        }
                        return (true, onlineCount, src);
                    }
                }
            }
        }
        catch { }

        // 备选源：LiteLLM 公开模型上下文与能力参数表
        try
        {
            using var reqAlt = new HttpRequestMessage(HttpMethod.Get, "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json");
            reqAlt.Headers.Add("User-Agent", "AICollabHub-Commercial/2.0");
            using var respAlt = Http.SendAsync(reqAlt).GetAwaiter().GetResult();
            if (respAlt.IsSuccessStatusCode)
            {
                var jsonAlt = respAlt.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var docAlt = JsonDocument.Parse(jsonAlt);
                if (docAlt.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var parsedHints = new Dictionary<string, OnlineModelCapabilityHint>(StringComparer.OrdinalIgnoreCase);
                    int altCount = 0;
                    foreach (var prop in docAlt.RootElement.EnumerateObject())
                    {
                        if (prop.Name.Equals("sample_spec", StringComparison.OrdinalIgnoreCase) ||
                            prop.Value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }
                        altCount++;
                        var hint = ExtractLiteLlmHint(prop.Name, prop.Value);
                        parsedHints[prop.Name] = hint;
                        var slashIdx = prop.Name.IndexOf('/');
                        if (slashIdx >= 0 && slashIdx + 1 < prop.Name.Length)
                        {
                            var slug = prop.Name[(slashIdx + 1)..];
                            if (!parsedHints.ContainsKey(slug)) parsedHints[slug] = hint;
                        }
                    }

                    if (parsedHints.Count > 0)
                    {
                        var src = $"LiteLLM 公开模型参数库 ({altCount} 个模型在线分析) + LiveBench 协同矩阵";
                        lock (HintLock)
                        {
                            _onlineHints = parsedHints;
                            _onlineCacheAttemptedLoad = true;
                            _lastOnlineAnalyzedCount = altCount;
                            _lastOnlineSourceName = src;
                        }
                        return (true, altCount, src);
                    }
                }
            }
        }
        catch { }

        EnsureOnlineCacheLoadedOnce();
        lock (HintLock)
        {
            if (_onlineHints.Count > 0)
            {
                return (true, _lastOnlineAnalyzedCount > 0 ? _lastOnlineAnalyzedCount : _onlineHints.Count, _lastOnlineSourceName);
            }
        }
        return (false, 0, "内置 SWE-bench / LiveBench 校准基准库（离线就绪）");
    }

    public static IReadOnlyList<GlobalFleetPresetInfo> GetGlobalFleetPresets() => new List<GlobalFleetPresetInfo>
    {
        new()
        {
            FleetId = "fleet_balanced",
            Name = "🌐 云端流派 1 · 均衡全能协同阵容（推荐）",
            Description = "主脑深度统筹、实操主力平衡、图文辅助各司其职，兼顾质量与响应速度"
        },
        new()
        {
            FleetId = "fleet_game_fullstack",
            Name = "🎮 云端流派 2 · 游戏全栈研发（码 + 图 + 影 + 音 + 3D）",
            Description = "强化程序代码与 UI/Shader、3D 动作及多模态美术/视频/音效全链协同（提升多模态创意权重）"
        },
        new()
        {
            FleetId = "fleet_fast_agile",
            Name = "⚡ 云端流派 3 · 极速省流敏捷迭代阵容",
            Description = "全员优先低延迟高性价比模型与紧凑上下文预算（Compact），大幅降低耗时与 Token 开销"
        },
        new()
        {
            FleetId = "fleet_safe_review",
            Name = "🛡️ 云端流派 4 · 高安全读写分离双盲审查阵容",
            Description = "编码主力专职修改工程，旁路异构节点（Grok/Codex 等）自动设为只读审查禁写源码模式，严防冲突"
        }
    };

    /// <summary>
    /// 纯内存 0ms UI 线程报告构建与可选多流派应用（零网络请求、零阻塞，且绝不改变任何 AI 原有的 Enabled 启停开关）
    /// </summary>
    public CloudSyncReport BuildAndApplyReportFromCache(
        RegistryService reg,
        bool applyToAgents = true,
        bool? fetchedOnlineOverride = null,
        int? onlineCountOverride = null,
        string? sourceNameOverride = null,
        string fleetPresetId = "fleet_balanced",
        bool saveToDisk = true)
    {
        EnsureOnlineCacheLoadedOnce();
        Dictionary<string, OnlineModelCapabilityHint> hintsSnapshot;
        int cachedCount;
        string cachedSrc;
        lock (HintLock)
        {
            hintsSnapshot = new Dictionary<string, OnlineModelCapabilityHint>(_onlineHints, StringComparer.OrdinalIgnoreCase);
            cachedCount = _lastOnlineAnalyzedCount > 0 ? _lastOnlineAnalyzedCount : hintsSnapshot.Count;
            cachedSrc = _lastOnlineSourceName;
        }

        bool fetched = fetchedOnlineOverride ?? (hintsSnapshot.Count > 0 && !AppPaths.IsIsolatedTest);
        int analyzed = onlineCountOverride ?? (fetched ? cachedCount : 0);
        string srcName = !string.IsNullOrWhiteSpace(sourceNameOverride)
            ? sourceNameOverride
            : (fetched ? cachedSrc : "内置 SWE-bench / LiveBench 校准基准库");

        var report = new CloudSyncReport
        {
            FetchedOnline = fetched,
            OnlineModelsAnalyzed = analyzed,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            SourceName = srcName,
            OnlineHints = hintsSnapshot
        };

        foreach (var ag in reg.UserConfig.Agents)
        {
            var agentPresetId = MapFleetToAgentPresetId(fleetPresetId, ag.Id);
            var cloudPresets = BuildCloudPresetsForAgent(reg, ag, refreshLocalModels: false);
            var matchedPreset = cloudPresets.FirstOrDefault(p => string.Equals(p.PresetId, agentPresetId, StringComparison.OrdinalIgnoreCase))
                                ?? cloudPresets.First();

            var rec = BuildRecommendationForAgent(ag);
            rec.PresetId = matchedPreset.PresetId;
            rec.RecommendedPermissions = matchedPreset.Permissions.Clone();
            rec.RecommendedTags = new Dictionary<string, int>(matchedPreset.TagScores, StringComparer.OrdinalIgnoreCase);
            rec.RecommendedRuntimeParams = matchedPreset.RuntimeParams.Clone();

            if (LocalModelCatalogService.SupportsModelSelection(ag.Id) && matchedPreset.ModelProfiles.Count > 0)
            {
                rec.RecommendedHighModel = matchedPreset.ModelProfiles.FirstOrDefault(p => p.RecommendedEffort == "high")?.ModelName ?? matchedPreset.ModelProfiles[0].ModelName;
                rec.RecommendedMediumModel = matchedPreset.ModelProfiles.FirstOrDefault(p => p.RecommendedEffort == "medium")?.ModelName ?? matchedPreset.ModelProfiles[0].ModelName;
                rec.RecommendedLowModel = matchedPreset.ModelProfiles.FirstOrDefault(p => p.RecommendedEffort == "low")?.ModelName ?? matchedPreset.ModelProfiles[^1].ModelName;
            }
            if (!string.IsNullOrWhiteSpace(matchedPreset.Description))
            {
                rec.RoleSummary = $"{matchedPreset.Name} · {matchedPreset.Description}";
            }
            report.Items.Add(rec);

            ag.RecommendedSummary = rec.RoleSummary;
            EnsureAgentModelTiersPopulated(ag, rec, overwrite: false);
            if (applyToAgents)
            {
                // 仅写入配置参数与方案映射，严格保留用户原有的 ag.Enabled 开关不变
                ApplyPresetToAgent(ag, matchedPreset, preserveRunnerChannel: true);
            }
        }

        reg.UserConfig.LastCloudRecSyncTime = report.UpdatedAt;
        reg.UserConfig.CloudRecSourceLabel = report.SourceName;
        if (saveToDisk)
        {
            reg.SaveUserConfig();
        }

        try
        {
            RegistryService.WriteJsonAtomic(CacheFilePath, report);
        }
        catch { }

        return report;
    }

    private static string MapFleetToAgentPresetId(string? fleetId, string? agentId)
    {
        var fid = (fleetId ?? "fleet_balanced").Trim().ToLowerInvariant();
        var aid = (agentId ?? "").Trim().ToLowerInvariant();
        return fid switch
        {
            "fleet_game_fullstack" => aid switch
            {
                "grok_bot" => "cloud_multimedia",
                "antigravity" => "cloud_deep_arch",
                _ => "cloud_balanced"
            },
            "fleet_fast_agile" => "cloud_fast_dev",
            "fleet_safe_review" => aid switch
            {
                "antigravity" => "cloud_deep_arch",
                "cursor" => "cloud_balanced",
                _ => "cloud_safe_audit"
            },
            _ => "cloud_balanced"
        };
    }

    public CloudSyncReport FetchAndAnalyzeRecommendations(RegistryService reg, bool applyToAgents = true)
    {
        if (AppPaths.IsIsolatedTest)
        {
            return BuildAndApplyReportFromCache(reg, applyToAgents, false, 0, "内置 SWE-bench / LiveBench 校准基准库");
        }
        var net = FetchOnlineHintsOnly();
        return BuildAndApplyReportFromCache(reg, applyToAgents, net.FetchedOnline, net.OnlineModelsAnalyzed, net.SourceName);
    }

    private static OnlineModelCapabilityHint ExtractOnlineHint(string id, JsonElement el)
    {
        int ctx = 0;
        if (el.TryGetProperty("context_length", out var ctxProp) && ctxProp.TryGetInt32(out int cVal))
            ctx = cVal;

        int maxComp = 0;
        if (el.TryGetProperty("top_provider", out var tpProp) && tpProp.ValueKind == JsonValueKind.Object &&
            tpProp.TryGetProperty("max_completion_tokens", out var mcProp) && mcProp.TryGetInt32(out int mcVal))
        {
            maxComp = mcVal;
        }

        bool supportsReasoning = false;
        bool supportsTools = false;
        if (el.TryGetProperty("supported_parameters", out var spProp) && spProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in spProp.EnumerateArray())
            {
                var pv = p.GetString() ?? "";
                if (pv.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) supportsReasoning = true;
                if (pv.Contains("tools", StringComparison.OrdinalIgnoreCase)) supportsTools = true;
            }
        }

        bool supportsVision = false;
        bool supportsImageOut = false;
        bool supportsAudio = false;
        bool supportsVideo = false;
        if (el.TryGetProperty("architecture", out var archProp) && archProp.ValueKind == JsonValueKind.Object)
        {
            if (archProp.TryGetProperty("modality", out var modProp))
            {
                var mod = modProp.GetString() ?? "";
                if (mod.Contains("image", StringComparison.OrdinalIgnoreCase)) supportsVision = true;
                if (mod.Contains("audio", StringComparison.OrdinalIgnoreCase)) supportsAudio = true;
                if (mod.Contains("video", StringComparison.OrdinalIgnoreCase)) supportsVideo = true;
                var arrow = mod.IndexOf("->", StringComparison.Ordinal);
                if (arrow >= 0 && mod[(arrow + 2)..].Contains("image", StringComparison.OrdinalIgnoreCase))
                    supportsImageOut = true;
            }
            if (archProp.TryGetProperty("input_modalities", out var inMod) && inMod.ValueKind == JsonValueKind.Array)
            {
                foreach (var im in inMod.EnumerateArray())
                {
                    var v = im.GetString() ?? "";
                    if (v.Contains("image", StringComparison.OrdinalIgnoreCase)) supportsVision = true;
                    if (v.Contains("audio", StringComparison.OrdinalIgnoreCase)) supportsAudio = true;
                    if (v.Contains("video", StringComparison.OrdinalIgnoreCase)) supportsVideo = true;
                }
            }
            if (archProp.TryGetProperty("output_modalities", out var outMod) && outMod.ValueKind == JsonValueKind.Array)
            {
                foreach (var om in outMod.EnumerateArray())
                {
                    var v = om.GetString() ?? "";
                    if (v.Contains("image", StringComparison.OrdinalIgnoreCase)) supportsImageOut = true;
                    if (v.Contains("audio", StringComparison.OrdinalIgnoreCase)) supportsAudio = true;
                    if (v.Contains("video", StringComparison.OrdinalIgnoreCase)) supportsVideo = true;
                }
            }
        }

        double promptPrice = 0;
        if (el.TryGetProperty("pricing", out var priceProp) && priceProp.ValueKind == JsonValueKind.Object &&
            priceProp.TryGetProperty("prompt", out var pValProp))
        {
            var raw = pValProp.ValueKind == JsonValueKind.String ? pValProp.GetString() : pValProp.ToString();
            double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out promptPrice);
        }

        double recTemp = supportsImageOut || supportsVideo ? 0.70 : (supportsReasoning ? 0.15 : 0.30);

        // promptPrice 单位通常为美元/token：>= 5e-6 ($5/1M) 属于旗舰级；<= 5e-7 ($0.5/1M) 属于高速轻量级
        return new OnlineModelCapabilityHint
        {
            ModelId = id,
            ContextLength = ctx,
            MaxCompletionTokens = maxComp,
            SupportsReasoning = supportsReasoning,
            SupportsImageOutput = supportsImageOut,
            SupportsVisionInput = supportsVision,
            SupportsAudio = supportsAudio,
            SupportsVideo = supportsVideo,
            SupportsTools = supportsTools,
            IsHighCostFlagship = promptPrice >= 0.000005,
            IsFastLowCost = promptPrice > 0 && promptPrice <= 0.0000006,
            RecommendedTemperature = recTemp
        };
    }

    private static OnlineModelCapabilityHint ExtractLiteLlmHint(string id, JsonElement el)
    {
        int ctx = 0;
        if (el.TryGetProperty("max_input_tokens", out var miProp) && miProp.TryGetInt32(out int miVal)) ctx = miVal;
        else if (el.TryGetProperty("max_tokens", out var mtProp) && mtProp.TryGetInt32(out int mtVal)) ctx = mtVal;

        int maxOut = 0;
        if (el.TryGetProperty("max_output_tokens", out var moProp) && moProp.TryGetInt32(out int moVal)) maxOut = moVal;

        bool supportsReasoning = el.TryGetProperty("supports_reasoning", out var sr) && sr.ValueKind == JsonValueKind.True;
        bool supportsVision = el.TryGetProperty("supports_vision", out var sv) && sv.ValueKind == JsonValueKind.True;
        bool supportsTools = el.TryGetProperty("supports_function_calling", out var st) && st.ValueKind == JsonValueKind.True;
        bool supportsAudio = (el.TryGetProperty("supports_audio_input", out var sai) && sai.ValueKind == JsonValueKind.True) ||
                             (el.TryGetProperty("supports_audio_output", out var sao) && sao.ValueKind == JsonValueKind.True);

        double cost = 0;
        if (el.TryGetProperty("input_cost_per_token", out var icProp) && icProp.TryGetDouble(out double icVal))
            cost = icVal;

        return new OnlineModelCapabilityHint
        {
            ModelId = id,
            ContextLength = ctx,
            MaxCompletionTokens = maxOut,
            SupportsReasoning = supportsReasoning,
            SupportsVisionInput = supportsVision,
            SupportsAudio = supportsAudio,
            SupportsTools = supportsTools,
            IsHighCostFlagship = cost >= 0.000005,
            IsFastLowCost = cost > 0 && cost <= 0.0000006,
            RecommendedTemperature = supportsReasoning ? 0.15 : 0.30
        };
    }

    private static OnlineModelCapabilityHint? FindOnlineHintForModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        EnsureOnlineCacheLoadedOnce();
        lock (HintLock)
        {
            if (_onlineHints.Count == 0) return null;
            var raw = modelName.Trim();
            if (raw.StartsWith("auto", StringComparison.OrdinalIgnoreCase)) return null;

            bool IsUsableHint(string k, OnlineModelCapabilityHint h) =>
                !k.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase) &&
                !h.ModelId.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase) &&
                !k.Equals("auto", StringComparison.OrdinalIgnoreCase);

            if (_onlineHints.TryGetValue(raw, out var exact) && IsUsableHint(raw, exact)) return exact;

            // 若显示为 "Display Name (slug)"，优先提取括号内的 slug 精确匹配
            var paren = Regex.Match(raw, @"\(([^)]+)\)\s*$");
            if (paren.Success)
            {
                var slug = paren.Groups[1].Value.Trim();
                if (!slug.Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    if (_onlineHints.TryGetValue(slug, out var bySlug) && IsUsableHint(slug, bySlug)) return bySlug;
                    var slugNorm = slug.Replace('.', '-');
                    if (_onlineHints.TryGetValue(slugNorm, out var byNorm) && IsUsableHint(slugNorm, byNorm)) return byNorm;
                }
            }

            // 仅按完整模型名斜杠后缀精确匹配（如 "google/gemini-4-argon" 匹配 "gemini-4-argon"），不做宽泛子串误匹配
            var simplified = Regex.Replace(raw, @"\s*\([^)]*\)", "").Trim().ToLowerInvariant().Replace(' ', '-');
            if (simplified.Length >= 5 && !simplified.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                if (_onlineHints.TryGetValue(simplified, out var bySimp) && IsUsableHint(simplified, bySimp)) return bySimp;
                foreach (var kv in _onlineHints)
                {
                    if (kv.Key.EndsWith("/" + simplified, StringComparison.OrdinalIgnoreCase) && IsUsableHint(kv.Key, kv.Value))
                    {
                        return kv.Value;
                    }
                }
            }
            return null;
        }
    }

    private static (int Ctx, int MaxOut, double Temp) EstimateDefaultModelParams(string lower, string tier, OnlineModelCapabilityHint? online)
    {
        int ctx = online?.ContextLength > 0 ? online.ContextLength :
            (lower.Contains("gemini") || lower.Contains("1m") ? 1000000 :
            (lower.Contains("claude") ? 200000 :
            (lower.Contains("gpt-6") || lower.Contains("gpt-5") || lower.Contains("codex") ? 272000 :
            (lower.Contains("grok") ? 131072 : 128000))));
        int maxOut = online?.MaxCompletionTokens > 0 ? online.MaxCompletionTokens :
            (tier == "high" ? 32768 : (tier == "low" ? 8192 : 16384));
        double temp = online?.RecommendedTemperature > 0 ? online.RecommendedTemperature :
            (tier == "high" ? 0.15 : (tier == "low" ? 0.25 : 0.30));
        return (ctx, maxOut, temp);
    }

    /// <summary>
    /// 结合网上云端模型库特征 + 内置权威评测矩阵，推断任意本机模型的最佳档位、参数规格与擅长工作职责
    /// </summary>
    public static CloudModelSpec InferCloudModelSpec(string? agentId, string? modelName)
    {
        var aid = (agentId ?? "").Trim().ToLowerInvariant();
        var mn = (modelName ?? "").Trim();
        var lower = mn.ToLowerInvariant();
        var online = FindOnlineHintForModel(mn);
        var srcTag = online != null ? "OpenRouter/LiteLLM 实时特征 + LiveBench" : "SWE-bench / LiveBench 矩阵";

        static bool HasWord(string text, string pattern) =>
            Regex.IsMatch(text, $@"(^|[^a-z0-9])({pattern})([^a-z0-9]|$)", RegexOptions.IgnoreCase);

        if (!LocalModelCatalogService.SupportsModelSelection(aid) ||
            mn.Contains("客户端默认托管", StringComparison.OrdinalIgnoreCase))
        {
            return new CloudModelSpec
            {
                Tier = "medium",
                SpeedTier = "medium",
                RecommendedRoles = new List<string> { "text_writing", "image_generation", "video_animation" },
                CloudBadge = "客户端默认托管（不可切换）",
                SpecialtyNote = "该客户端不支持用户选择或切换模型，派单时仅透传默认托管运行以防风控",
                SourceTag = "客户端安全锁定",
                ContextWindowTokens = 131072,
                MaxOutputTokens = 16384,
                RecommendedTemperature = 0.35
            };
        }

        if (lower == "auto" || lower.StartsWith("auto ") || lower.Contains("(default)"))
        {
            return new CloudModelSpec
            {
                Tier = "medium",
                SpeedTier = "medium",
                RecommendedRoles = new List<string> { "fast_execution", "text_writing" },
                CloudBadge = "云端定位: 中档 · 智能路由主力",
                SpecialtyNote = "客户端内置智能选型默认档：日常代码实操、快速调整与文本辅助",
                SourceTag = "客户端默认智能路由",
                ContextWindowTokens = 200000,
                MaxOutputTokens = 16384,
                RecommendedTemperature = 0.30
            };
        }

        bool isVideoGen = HasWord(lower, "sora|veo|kling|runway|luma|hailuo|wan|seedance|pika|video") || (online?.SupportsVideo == true && HasWord(lower, "pro|gen"));
        if (isVideoGen)
        {
            var (c, m, _) = EstimateDefaultModelParams(lower, "medium", online);
            return new CloudModelSpec
            {
                Tier = "medium",
                SpeedTier = "medium",
                RecommendedRoles = new List<string> { "video_animation", "image_generation", "text_writing" },
                CloudBadge = "云端定位: 多模态 · 视频/分镜创作",
                SpecialtyNote = "多模态视频生成、动态分镜与镜头表现专精模型",
                SourceTag = srcTag,
                ContextWindowTokens = c,
                MaxOutputTokens = m,
                RecommendedTemperature = 0.75
            };
        }

        bool isAudioGen = HasWord(lower, "suno|udio|eleven|whisper|cosyvoice|audiogen|musicgen|audio|tts|voice");
        if (isAudioGen)
        {
            var (c, m, _) = EstimateDefaultModelParams(lower, "medium", online);
            return new CloudModelSpec
            {
                Tier = "medium",
                SpeedTier = "medium",
                RecommendedRoles = new List<string> { "audio_sfx_bgm", "text_writing" },
                CloudBadge = "云端定位: 多模态 · 音效/配乐/语音",
                SpecialtyNote = "音效合成、BGM 配乐编曲与角色 TTS 配音专精模型",
                SourceTag = srcTag,
                ContextWindowTokens = c,
                MaxOutputTokens = m,
                RecommendedTemperature = 0.70
            };
        }

        bool is3dGen = HasWord(lower, "meshy|tripo|rodin|hunyuan3d|trellis");
        if (is3dGen)
        {
            var (c, m, _) = EstimateDefaultModelParams(lower, "medium", online);
            return new CloudModelSpec
            {
                Tier = "medium",
                SpeedTier = "medium",
                RecommendedRoles = new List<string> { "model_3d_motion", "image_generation" },
                CloudBadge = "云端定位: 多模态 · 3D模型/动作",
                SpecialtyNote = "3D 模型建模与动作资产创作专精模型",
                SourceTag = srcTag,
                ContextWindowTokens = c,
                MaxOutputTokens = m,
                RecommendedTemperature = 0.40
            };
        }

        bool isImageGen =
            HasWord(lower, "image|muse|flux|dall|imagen|midjourney") ||
            (online != null && online.SupportsImageOutput && !online.ModelId.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase));

        if (isImageGen)
        {
            var (c, m, _) = EstimateDefaultModelParams(lower, "medium", online);
            return new CloudModelSpec
            {
                Tier = "medium",
                SpeedTier = "medium",
                RecommendedRoles = new List<string> { "image_generation", "text_writing" },
                CloudBadge = "云端定位: 多模态 · 生图/视觉创作",
                SpecialtyNote = "多模态生图与视觉概念设计专精模型",
                SourceTag = srcTag,
                ContextWindowTokens = c,
                MaxOutputTokens = m,
                RecommendedTemperature = 0.75
            };
        }

        bool isFastLow =
            HasWord(lower, "flash|haiku|mini|nano|fast|instant|lite") ||
            lower.Contains("5.6-luna") || lower.Contains("5.6 luna") ||
            (online != null && online.IsFastLowCost && !online.SupportsReasoning);

        if (isFastLow)
        {
            var (c, m, t) = EstimateDefaultModelParams(lower, "low", online);
            return new CloudModelSpec
            {
                Tier = "low",
                SpeedTier = "fast",
                RecommendedRoles = new List<string> { "fast_execution", "text_writing" },
                CloudBadge = "云端定位: 低档 · 极速快执行",
                SpecialtyNote = "极速轻量模型：响应最快，适合快速改工程、脚本补全与批处理",
                SourceTag = srcTag,
                ContextWindowTokens = c,
                MaxOutputTokens = m,
                RecommendedTemperature = t
            };
        }

        if (HasWord(lower, "orchestrator") && HasWord(lower, "argon"))
        {
            var (c, m, _) = EstimateDefaultModelParams(lower, "medium", online);
            return new CloudModelSpec
            {
                Tier = "medium",
                SpeedTier = "medium",
                RecommendedRoles = new List<string> { "brain_analysis", "fast_execution", "visual_accept" },
                CloudBadge = "云端定位: 中档 · 轻量统筹/快分析",
                SpecialtyNote = "轻量统筹编排模型：适合任务拆解、日常分析与协同派单",
                SourceTag = srcTag,
                ContextWindowTokens = c,
                MaxOutputTokens = m,
                RecommendedTemperature = 0.25
            };
        }

        bool isExplicitMidWorkhorse = HasWord(lower, "sonnet|argon|hydrogen|composer|terra|sol") || lower.Contains("gpt-oss");
        bool isFlagshipHigh =
            HasWord(lower, "orchestrator|neon|carbon|opus|o3|o4") ||
            lower.Contains("grok 4.7") || lower.Contains("grok-4.7") || lower.Contains("grok-4") ||
            lower.Contains("gpt-6-luna") || lower.Contains("gpt-5.4-pro") ||
            lower.Contains("pro-high") || HasWord(lower, "r1") ||
            (HasWord(lower, "high") && !lower.Contains("gpt-oss")) ||
            (!isExplicitMidWorkhorse && online != null && online.IsHighCostFlagship);

        if (isFlagshipHigh && !lower.Contains("gpt-oss"))
        {
            var roles = aid switch
            {
                "antigravity" => new List<string> { "brain_analysis", "deep_debug", "visual_accept" },
                "cursor" => new List<string> { "deep_debug", "fast_execution", "ui_shader_vfx", "model_3d_motion" },
                "grok_bot" => new List<string> { "text_writing", "brain_analysis", "image_generation" },
                "codex_cli" => new List<string> { "deep_debug", "brain_analysis", "fast_execution", "build_testing" },
                _ => new List<string> { "brain_analysis", "deep_debug" }
            };
            var (c, m, t) = EstimateDefaultModelParams(lower, "high", online);
            return new CloudModelSpec
            {
                Tier = "high",
                SpeedTier = "high",
                RecommendedRoles = roles,
                CloudBadge = "云端定位: 高档 · 旗舰总分析/排障",
                SpecialtyNote = "旗舰深度推理模型：适合主大脑总分析、复杂架构与疑难排障",
                SourceTag = srcTag,
                ContextWindowTokens = c,
                MaxOutputTokens = m,
                RecommendedTemperature = t
            };
        }

        // 中档主力均衡：Sonnet / Argon / Hydrogen / Composer / Terra / Sol / GPT-OSS / Auto 等
        var medRoles = aid switch
        {
            "antigravity" => lower.Contains("argon") || lower.Contains("hydrogen") || lower.Contains("gpt-oss")
                ? new List<string> { "fast_execution", "text_writing", "data_config" }
                : new List<string> { "fast_execution", "ui_shader_vfx", "data_config", "text_writing" },
            "cursor" => new List<string> { "fast_execution", "ui_shader_vfx", "data_config", "build_testing" },
            "grok_bot" => new List<string> { "text_writing", "image_generation", "video_animation" },
            "codex_cli" => new List<string> { "fast_execution", "data_config", "build_testing", "text_writing" },
            _ => new List<string> { "fast_execution", "text_writing" }
        };
        var (mCtx, mOut, mTemp) = EstimateDefaultModelParams(lower, "medium", online);

        return new CloudModelSpec
        {
            Tier = "medium",
            SpeedTier = "medium",
            RecommendedRoles = medRoles,
            CloudBadge = aid == "grok_bot" ? "云端定位: 中档 · 文案/生图/实操" : "云端定位: 中档 · 主力实操/文本",
            SpecialtyNote = "中档均衡主力模型：适合日常代码实操、配置调整与文本创作",
            SourceTag = srcTag,
            ContextWindowTokens = mCtx,
            MaxOutputTokens = mOut,
            RecommendedTemperature = mTemp
        };
    }

    public static Dictionary<string, int> InferModelCapabilityScores(
        string? agentId,
        string? modelName,
        IEnumerable<string>? activeRoles = null)
    {
        var aid = (agentId ?? "").Trim().ToLowerInvariant();
        var lower = (modelName ?? "").Trim().ToLowerInvariant();
        var spec = InferCloudModelSpec(agentId, modelName);
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (lower.Contains("opus"))
        {
            scores["brain_analysis"] = 5;
            scores["deep_debug"] = 5;
            scores["fast_execution"] = 5;
            scores["ui_shader_vfx"] = 4;
            scores["data_config"] = 4;
            scores["build_testing"] = 4;
            scores["text_writing"] = 4;
            scores["model_3d_motion"] = 4;
            if (aid == "antigravity") scores["visual_accept"] = 4;
        }
        else if (lower.Contains("neon") || lower.Contains("carbon"))
        {
            scores["brain_analysis"] = 5;
            scores["deep_debug"] = 5;
            scores["visual_accept"] = 5;
            scores["fast_execution"] = 4;
            scores["ui_shader_vfx"] = 4;
            scores["model_3d_motion"] = 4;
            scores["data_config"] = 4;
        }
        else if (lower.Contains("orchestrator"))
        {
            scores["brain_analysis"] = 5;
            scores["deep_debug"] = 5;
            scores["visual_accept"] = 5;
            scores["data_config"] = 4;
            scores["text_writing"] = 4;
        }
        else if (lower.Contains("sonnet"))
        {
            scores["fast_execution"] = 5;
            scores["ui_shader_vfx"] = 5;
            scores["deep_debug"] = 4;
            scores["data_config"] = 4;
            scores["build_testing"] = 4;
            scores["text_writing"] = 4;
            scores["model_3d_motion"] = 4;
            scores["brain_analysis"] = 3;
        }
        else if (lower.Contains("composer"))
        {
            scores["fast_execution"] = 4;
            scores["build_testing"] = 5;
            scores["ui_shader_vfx"] = 4;
            scores["data_config"] = 4;
            scores["deep_debug"] = 3;
        }
        else if (lower.Contains("hydrogen"))
        {
            scores["fast_execution"] = 4;
            scores["ui_shader_vfx"] = 4;
            scores["image_generation"] = 4;
            scores["model_3d_motion"] = 4;
            scores["data_config"] = 3;
            scores["build_testing"] = 3;
        }
        else if (lower.Contains("argon"))
        {
            scores["brain_analysis"] = 4;
            scores["fast_execution"] = 4;
            scores["data_config"] = 4;
            scores["text_writing"] = 4;
            scores["ui_shader_vfx"] = 3;
        }
        else if (lower.Contains("gpt-6") || (lower.Contains("luna") && !lower.Contains("5.6")) || Regex.IsMatch(lower, @"\bo3\b|\bo4\b"))
        {
            scores["brain_analysis"] = 5;
            scores["deep_debug"] = 5;
            scores["fast_execution"] = 5;
            scores["build_testing"] = 4;
            scores["data_config"] = 4;
            scores["text_writing"] = 4;
        }
        else if (lower.Contains("terra") || lower.Contains("sol"))
        {
            scores["fast_execution"] = 4;
            scores["deep_debug"] = 4;
            scores["data_config"] = 4;
            scores["build_testing"] = 4;
            scores["ui_shader_vfx"] = 3;
        }
        else if (lower.Contains("grok") || aid == "grok_bot")
        {
            scores["text_writing"] = 5;
            scores["image_generation"] = 5;
            scores["video_animation"] = 4;
            scores["deep_debug"] = 4;
            scores["brain_analysis"] = 4;
            scores["data_config"] = 4;
            scores["fast_execution"] = 3;
        }
        else if (lower.Contains("flash") || lower.Contains("haiku") || lower.Contains("5.6-luna") || lower.Contains("5.6 luna") || lower == "auto (default)")
        {
            scores["fast_execution"] = 3;
            scores["text_writing"] = 4;
            scores["data_config"] = 4;
            scores["build_testing"] = 4;
            scores["ui_shader_vfx"] = 3;
        }

        if (spec.RecommendedRoles.Contains("image_generation", StringComparer.OrdinalIgnoreCase))
            scores["image_generation"] = Math.Max(scores.TryGetValue("image_generation", out var sImg) ? sImg : 0, lower.Contains("image") || lower.Contains("flux") || lower.Contains("imagen") || lower.Contains("grok") ? 5 : 4);
        if (spec.RecommendedRoles.Contains("video_animation", StringComparer.OrdinalIgnoreCase))
            scores["video_animation"] = Math.Max(scores.TryGetValue("video_animation", out var sVid) ? sVid : 0, 5);
        if (spec.RecommendedRoles.Contains("audio_sfx_bgm", StringComparer.OrdinalIgnoreCase))
            scores["audio_sfx_bgm"] = Math.Max(scores.TryGetValue("audio_sfx_bgm", out var sAud) ? sAud : 0, 5);
        if (spec.RecommendedRoles.Contains("model_3d_motion", StringComparer.OrdinalIgnoreCase))
            scores["model_3d_motion"] = Math.Max(scores.TryGetValue("model_3d_motion", out var s3d) ? s3d : 0, 4);

        foreach (var r in spec.RecommendedRoles)
        {
            if (!scores.ContainsKey(r))
                scores[r] = spec.Tier == "high" ? 4 : 3;
        }

        var targetKeys = activeRoles != null
            ? activeRoles.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : spec.RecommendedRoles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var filtered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in targetKeys)
        {
            filtered[k] = scores.TryGetValue(k, out var val) ? Math.Clamp(val, 1, 5) : (spec.Tier == "high" ? 4 : 3);
        }
        return filtered;
    }

    public static (string UsagePolicy, string DifficultyRule) InferModelUsageRules(
        string? agentId,
        string? modelName,
        string? effortTier,
        int slotIndex = 0)
    {
        var aid = (agentId ?? "").Trim().ToLowerInvariant();
        var spec = InferCloudModelSpec(agentId, modelName);
        bool pureMedia = spec.RecommendedRoles.Count > 0 &&
                         spec.RecommendedRoles.All(r => r is "image_generation" or "video_animation" or "audio_sfx_bgm" or "model_3d_motion" or "text_writing") &&
                         spec.RecommendedRoles.Any(r => r is "image_generation" or "video_animation" or "audio_sfx_bgm" or "model_3d_motion");
        if (pureMedia)
            return ("strict_specialty_only", "any");

        var tier = string.IsNullOrWhiteSpace(effortTier) ? spec.Tier : effortTier.Trim().ToLowerInvariant();
        if (tier == "low")
            return ("light_fast_first", "low_medium");
        if (tier == "high")
            return aid == "antigravity" && slotIndex == 0
                ? ("primary_default", "any")
                : ("primary_default", "medium_up");

        return ("primary_default", "any");
    }

    public static void EnsureModelProfileEnriched(
        string? agentId,
        ModelWorkProfile? mp,
        int slotIndex = 0,
        string? agentCategory = null)
    {
        if (mp == null) return;
        var own = LocalModelCatalogService.ClassifyModelOwnership(agentId, mp.ModelName, mp.OwnershipKind, mp.VendorName, agentCategory);
        mp.OwnershipKind = own.OwnershipKind;
        mp.VendorName = own.VendorName;

        var (defPolicy, defDiff) = InferModelUsageRules(agentId, mp.ModelName, mp.RecommendedEffort, slotIndex);
        if (string.IsNullOrWhiteSpace(mp.UsagePolicy)) mp.UsagePolicy = defPolicy;
        if (string.IsNullOrWhiteSpace(mp.TaskDifficultyRule)) mp.TaskDifficultyRule = defDiff;

        mp.PrimaryRoles ??= new List<string>();
        mp.CapabilityScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 清理 0 分/负分无效键，同时确保正分数与 PrimaryRoles 双向一致
        var deadKeys = mp.CapabilityScores.Where(kv => kv.Value <= 0 || string.IsNullOrWhiteSpace(kv.Key)).Select(kv => kv.Key).ToList();
        foreach (var dk in deadKeys) mp.CapabilityScores.Remove(dk);

        foreach (var kv in mp.CapabilityScores.ToList())
        {
            mp.CapabilityScores[kv.Key] = Math.Clamp(kv.Value, 1, 5);
            if (!mp.PrimaryRoles.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                mp.PrimaryRoles.Add(kv.Key);
        }

        if (mp.PrimaryRoles.Count > 0)
        {
            var missingRoles = mp.PrimaryRoles.Where(r => !mp.CapabilityScores.ContainsKey(r)).ToList();
            if (missingRoles.Count > 0)
            {
                var inferred = InferModelCapabilityScores(agentId, mp.ModelName, missingRoles);
                foreach (var mr in missingRoles)
                {
                    mp.CapabilityScores[mr] = inferred.TryGetValue(mr, out var sc) ? sc : 3;
                }
            }
        }
    }

    public static void SyncSnapshotDerivedFromModelProfiles(TeamAgentSnapshot snap, AgentRosterEntry? agentRef)
    {
        if (snap == null) return;
        snap.ModelProfiles ??= new List<ModelWorkProfile>();
        if (snap.ModelProfiles.Count > 0 && !snap.ModelProfiles.Any(m => m.Enabled))
        {
            snap.ModelProfiles[0].Enabled = true;
        }
        for (int i = 0; i < snap.ModelProfiles.Count; i++)
        {
            EnsureModelProfileEnriched(snap.AgentId, snap.ModelProfiles[i], i, agentRef?.Category);
        }

        var activeSlots = snap.ModelProfiles
            .Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.ModelName))
            .ToList();
        if (activeSlots.Count == 0 && snap.ModelProfiles.Count > 0)
            activeSlots = snap.ModelProfiles;

        if (!LocalModelCatalogService.SupportsModelSelection(snap.AgentId))
        {
            snap.HighTierModel = LocalModelCatalogService.ManagedDefaultModelDisplay;
            snap.MediumTierModel = LocalModelCatalogService.ManagedDefaultModelDisplay;
            snap.LowTierModel = LocalModelCatalogService.ManagedDefaultModelDisplay;
        }
        else if (activeSlots.Count > 0)
        {
            var firstHigh = activeSlots.FirstOrDefault(p => string.Equals(p.RecommendedEffort, "high", StringComparison.OrdinalIgnoreCase))
                            ?? activeSlots.FirstOrDefault(p => string.Equals(p.RecommendedEffort, "medium", StringComparison.OrdinalIgnoreCase))
                            ?? activeSlots[0];
            var firstMed = activeSlots.FirstOrDefault(p => string.Equals(p.RecommendedEffort, "medium", StringComparison.OrdinalIgnoreCase))
                           ?? firstHigh;
            var firstLow = activeSlots.FirstOrDefault(p => string.Equals(p.RecommendedEffort, "low", StringComparison.OrdinalIgnoreCase))
                           ?? activeSlots[^1];
            snap.HighTierModel = firstHigh.ModelName;
            snap.MediumTierModel = firstMed.ModelName;
            snap.LowTierModel = firstLow.ModelName;
        }

        snap.PrimaryWorkRoles = NormalizePrimaryWorkRoles(activeSlots
            .SelectMany(m => (m.PrimaryRoles ?? new List<string>())
                .Concat((m.CapabilityScores ?? new Dictionary<string, int>())
                    .Where(kv => kv.Value > 0)
                    .Select(kv => kv.Key))));

        // 依据多模型 1~5 能力强度聚合同步工具级 TagScores，供总览与全局评分兼容读取
        int MaxRoleScore(params string[] roleIds)
        {
            int best = 0;
            foreach (var slot in activeSlots)
            {
                foreach (var rid in roleIds)
                    best = Math.Max(best, slot.GetCapabilityScore(rid));
            }
            return best;
        }

        snap.TagScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void SyncTagFromRoles(string tagId, params string[] roleIds)
        {
            int sc = MaxRoleScore(roleIds);
            if (sc > 0) snap.TagScores[tagId] = sc;
            else snap.TagScores.Remove(tagId);
        }
        // 工作类别本身的 TagScores 与模型能力并集保持一致（含取消勾选后清零）
        foreach (var item in WorkSpecialtyCatalog.OrderedItems)
            SyncTagFromRoles(item.Id, item.Id);
        SyncTagFromRoles("cross_repo_impl", "brain_analysis", "fast_execution");
        SyncTagFromRoles("ide_live_tweak", "fast_execution", "ui_shader_vfx");
        SyncTagFromRoles("deep_bug_hunt", "deep_debug");
        SyncTagFromRoles("adversarial_review", "deep_debug", "brain_analysis");
        SyncTagFromRoles("offline_math_plot", "data_config");
        SyncTagFromRoles("build_delivery", "build_testing");
        SyncTagFromRoles("design_copywriting", "text_writing");
        SyncTagFromRoles("image_asset_gen", "image_generation");
        SyncTagFromRoles("video_storyboard", "video_animation");
        SyncTagFromRoles("audio_sfx_music", "audio_sfx_bgm");
        SyncTagFromRoles("model_3d_anim", "model_3d_motion");
        SyncTagFromRoles("visual_gate", "visual_accept");
    }

    /// <summary>清理空白并大小写去重，防止 PrimaryWorkRoles 无限膨胀。</summary>
    public static List<string> NormalizePrimaryWorkRoles(IEnumerable<string>? roles) =>
        (roles ?? Enumerable.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 把方案快照中由模型能力派生的 PrimaryWorkRoles / TagScores 回写到运行态 AI，
    /// 让主界面路由总览与真实派单在保存后立即看到新勾选能力。
    /// </summary>
    public static void ApplyDerivedRolesToRuntimeAgent(TeamAgentSnapshot snap, AgentRosterEntry? agent)
    {
        if (snap == null || agent == null) return;
        agent.PrimaryWorkRoles = NormalizePrimaryWorkRoles(snap.PrimaryWorkRoles);
        agent.TagScores = new Dictionary<string, int>(snap.TagScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
    }

    public CloudAgentRecommendation ApplyRecommendedToSingleAgent(RegistryService reg, AgentRosterEntry agent, bool saveToDisk = true)
    {
        var rec = BuildRecommendationForAgent(agent, true, true, true, true);
        agent.RecommendedSummary = rec.RoleSummary;
        var presets = BuildCloudPresetsForAgent(reg, agent, refreshLocalModels: true);
        var balanced = presets.FirstOrDefault(p => p.PresetId == "cloud_balanced") ?? presets[0];
        ApplyPresetToAgent(agent, balanced, preserveRunnerChannel: true);
        if (saveToDisk)
        {
            reg.SaveUserConfig();
        }
        return rec;
    }

    /// <summary>
    /// 基于网上模型特征缓存 + 本机已开启的真实模型，为任意 AI 工具生成 5 套不同场景定位的云端配置方案
    /// </summary>
    public static List<AgentConfigPreset> BuildCloudPresetsForAgent(
        RegistryService? reg,
        AgentRosterEntry agent,
        bool refreshLocalModels = false)
    {
        var id = (agent.Id ?? "").Trim().ToLowerInvariant();
        bool canSelectModels = LocalModelCatalogService.SupportsModelSelection(id);
        var probe = LocalModelCatalogService.ProbeLocalModelsForAgent(agent.Id, forceRefresh: refreshLocalModels);
        var baseRec = BuildRecommendationForAgent(agent);

        string sourceBasis = CachedOnlineHintCount > 0
            ? $"OpenRouter/LiteLLM 实时特征库 ({CachedOnlineHintCount} 模型) + {probe.SourceDescription}"
            : $"SWE-bench / LiveBench 校准矩阵 + {probe.SourceDescription}";

        var availPool = probe.Models.Count > 0
            ? new List<string>(probe.Models)
            : new List<string>(agent.AvailableModels ?? new List<string>());
        if (canSelectModels && availPool.Count == 0 && !string.IsNullOrWhiteSpace(baseRec.RecommendedHighModel))
        {
            availPool.Add(baseRec.RecommendedHighModel);
        }

        string highM = !string.IsNullOrWhiteSpace(probe.SuggestedHigh) ? probe.SuggestedHigh : baseRec.RecommendedHighModel;
        string medM = !string.IsNullOrWhiteSpace(probe.SuggestedMedium) ? probe.SuggestedMedium : baseRec.RecommendedMediumModel;
        string lowM = !string.IsNullOrWhiteSpace(probe.SuggestedLow) ? probe.SuggestedLow : baseRec.RecommendedLowModel;

        List<ModelWorkProfile> BuildScenarioProfiles(
            string[] orderedModels,
            string[] orderedTiers,
            Func<string, CloudModelSpec, List<string>> roleSelector,
            double? overrideTemp = null)
        {
            if (!canSelectModels)
            {
                var managedSpec = InferCloudModelSpec(agent.Id, LocalModelCatalogService.ManagedDefaultModelDisplay);
                var managedRoles = roleSelector("medium", managedSpec).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var managedMp = new ModelWorkProfile
                {
                    ModelName = LocalModelCatalogService.ManagedDefaultModelDisplay,
                    PrimaryRoles = managedRoles,
                    RecommendedEffort = "medium",
                    SpeedTier = "medium",
                    SpecialtyNote = managedSpec.SpecialtyNote,
                    CustomTemperature = overrideTemp,
                    ContextWindowTokens = managedSpec.ContextWindowTokens,
                    MaxOutputTokens = managedSpec.MaxOutputTokens
                };
                EnsureModelProfileEnriched(agent.Id, managedMp, 0, agent.Category);
                return new List<ModelWorkProfile> { managedMp };
            }

            var list = new List<ModelWorkProfile>();
            for (int i = 0; i < orderedModels.Length; i++)
            {
                var mn = orderedModels[i];
                var tier = i < orderedTiers.Length ? orderedTiers[i] : "medium";
                if (string.IsNullOrWhiteSpace(mn) || LocalModelCatalogService.IsLegacyBogusModelName(mn)) continue;
                if (list.Any(p => string.Equals(p.ModelName, mn, StringComparison.OrdinalIgnoreCase))) continue;

                var spec = InferCloudModelSpec(agent.Id, mn);
                var roles = roleSelector(tier, spec).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var mp = new ModelWorkProfile
                {
                    ModelName = mn.Trim(),
                    PrimaryRoles = roles,
                    RecommendedEffort = tier,
                    SpeedTier = tier == "low" ? "fast" : tier,
                    SpecialtyNote = spec.SpecialtyNote,
                    CustomTemperature = overrideTemp,
                    ContextWindowTokens = spec.ContextWindowTokens,
                    MaxOutputTokens = spec.MaxOutputTokens
                };
                EnsureModelProfileEnriched(agent.Id, mp, list.Count, agent.Category);
                list.Add(mp);
            }

            if (list.Count == 0 && availPool.Count > 0)
            {
                var fallback = availPool[0];
                var spec = InferCloudModelSpec(agent.Id, fallback);
                list.Add(new ModelWorkProfile
                {
                    ModelName = fallback,
                    PrimaryRoles = roleSelector(spec.Tier, spec),
                    RecommendedEffort = spec.Tier,
                    SpeedTier = spec.SpeedTier,
                    SpecialtyNote = spec.SpecialtyNote,
                    CustomTemperature = overrideTemp,
                    ContextWindowTokens = spec.ContextWindowTokens,
                    MaxOutputTokens = spec.MaxOutputTokens
                });
            }
            return list;
        }

        Dictionary<string, int> ScaleTags(Dictionary<string, int> baseTags, params (string Key, int Value)[] overrides)
        {
            var dict = new Dictionary<string, int>(baseTags, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in overrides)
            {
                dict[k] = Math.Clamp(v, 0, 5);
            }
            return dict;
        }

        bool hasMcp = baseRec.RecommendedPermissions.HasPreviewMcp || id is "antigravity" or "cursor";

        // 方案 1：均衡全能协同（默认推荐）
        var balancedProfiles = BuildScenarioProfiles(
            new[] { highM, medM, lowM },
            new[] { "high", "medium", "low" },
            (tier, spec) =>
            {
                var r = new List<string>(spec.RecommendedRoles);
                if (tier == "high" && hasMcp && !r.Contains("visual_accept", StringComparer.OrdinalIgnoreCase) && id == "antigravity")
                    r.Add("visual_accept");
                if (tier == "medium" && id == "grok_bot" && !r.Contains("image_generation", StringComparer.OrdinalIgnoreCase))
                    r.Add("image_generation");
                if (tier == "low" && !r.Contains("fast_execution", StringComparer.OrdinalIgnoreCase))
                    r.Insert(0, "fast_execution");
                return r;
            });

        var pBalanced = new AgentConfigPreset
        {
            PresetId = "cloud_balanced",
            Name = "🌐 云端方案 · 均衡全能协同（推荐）",
            SourceKind = "cloud",
            CategoryBadge = "全能推荐",
            Description = "高/中/低黄金三档合理分工，兼顾架构准确性、编码速度与日常创作",
            SourceBasis = sourceBasis,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            PreferredEffortTier = "auto",
            CustomWorkNote = id switch
            {
                "antigravity" => "适合做主大脑统筹分析、跨模块拆解、Godot画面实看MCP与最终验收",
                "cursor" => "IDE实操响应快，擅长直接修改代码、预制体、项目设置与深度排障",
                "grok_bot" => "擅长文本文案策划、图像生成与视觉设计提示词，以及配置脚本快速实操",
                "codex_cli" => "后台无头推理与批量重构引擎（默认待命，按需勾选启用）",
                _ => "均衡全能协同执行"
            },
            ModelProfiles = balancedProfiles,
            Permissions = baseRec.RecommendedPermissions.Clone(),
            TagScores = new Dictionary<string, int>(baseRec.RecommendedTags, StringComparer.OrdinalIgnoreCase),
            RuntimeParams = new AgentRuntimeParams
            {
                Temperature = 0.30,
                ReasoningEffort = "auto",
                ContextBudget = "auto",
                MaxOutputTokens = 16384,
                TimeoutSeconds = 0,
                MaxRetries = -1
            },
            LaunchWindowOnDispatch = agent.LaunchWindowOnDispatch,
            RunnerMode = agent.RunnerMode,
            RunnerCmd = agent.RunnerCmd
        };

        // 方案 2：深度架构与硬核排障
        var deepPerms = baseRec.RecommendedPermissions.Clone();
        deepPerms.AllowShellBuild = true;
        var deepProfiles = BuildScenarioProfiles(
            new[] { highM, medM },
            new[] { "high", "medium" },
            (tier, spec) => tier == "high"
                ? (canSelectModels
                    ? new List<string> { "brain_analysis", "deep_debug", "fast_execution", "model_3d_motion", "data_config" }
                    : new List<string> { "brain_analysis", "deep_debug", "text_writing", "data_config" })
                : new List<string> { "fast_execution", "deep_debug", "build_testing" },
            overrideTemp: 0.15);

        var pDeepArch = new AgentConfigPreset
        {
            PresetId = "cloud_deep_arch",
            Name = "🔥 云端方案 · 深度架构与硬核排障",
            SourceKind = "cloud",
            CategoryBadge = "旗舰深思",
            Description = "锁定旗舰高推理模型置顶 + 严谨低温度 (0.15) + 长超时 (300s) + 最大上下文预算",
            SourceBasis = sourceBasis,
            UpdatedAt = pBalanced.UpdatedAt,
            PreferredEffortTier = canSelectModels ? "high" : "auto",
            CustomWorkNote = "深度根因分析、跨模块重架构、疑难 Bug 攻坚与高难度逻辑推演",
            ModelProfiles = deepProfiles,
            Permissions = deepPerms,
            TagScores = ScaleTags(baseRec.RecommendedTags, ("deep_bug_hunt", 5), ("adversarial_review", 5), ("cross_repo_impl", 5), ("offline_math_plot", 5)),
            RuntimeParams = new AgentRuntimeParams
            {
                Temperature = 0.15,
                ReasoningEffort = "high",
                ContextBudget = "max",
                MaxOutputTokens = 32768,
                TimeoutSeconds = 300,
                MaxRetries = 4
            },
            LaunchWindowOnDispatch = agent.LaunchWindowOnDispatch,
            RunnerMode = agent.RunnerMode,
            RunnerCmd = agent.RunnerCmd
        };

        // 方案 3：极速编码与高频省流
        var fastProfiles = BuildScenarioProfiles(
            new[] { lowM, medM },
            new[] { "low", "medium" },
            (tier, spec) => canSelectModels
                ? new List<string> { "fast_execution", "ui_shader_vfx", "data_config", "build_testing" }
                : new List<string> { "fast_execution", "text_writing", "data_config" },
            overrideTemp: 0.25);

        var pFastDev = new AgentConfigPreset
        {
            PresetId = "cloud_fast_dev",
            Name = "⚡ 云端方案 · 极速编码与高频省流",
            SourceKind = "cloud",
            CategoryBadge = "极速敏捷",
            Description = "轻量高性价比模型优先 + 低延迟轻推理 + 紧凑上下文预算，适合高频改工程",
            SourceBasis = sourceBasis,
            UpdatedAt = pBalanced.UpdatedAt,
            PreferredEffortTier = canSelectModels ? "low" : "auto",
            CustomWorkNote = "高频常规业务代码、UI 微调、配表刷表与构建测试快速响应",
            ModelProfiles = fastProfiles,
            Permissions = baseRec.RecommendedPermissions.Clone(),
            TagScores = ScaleTags(baseRec.RecommendedTags, ("ide_live_tweak", 5), ("build_delivery", 5), ("cross_repo_impl", 4)),
            RuntimeParams = new AgentRuntimeParams
            {
                Temperature = 0.25,
                ReasoningEffort = "low",
                ContextBudget = "compact",
                MaxOutputTokens = 8192,
                TimeoutSeconds = 120,
                MaxRetries = 2
            },
            LaunchWindowOnDispatch = agent.LaunchWindowOnDispatch,
            RunnerMode = agent.RunnerMode,
            RunnerCmd = agent.RunnerCmd
        };

        // 方案 4：游戏美术与多媒体创作
        var multiPerms = baseRec.RecommendedPermissions.Clone();
        var multiModels = availPool
            .Where(m => InferCloudModelSpec(agent.Id, m).RecommendedRoles.Any(r => r is "image_generation" or "video_animation" or "audio_sfx_bgm" or "model_3d_motion"))
            .Concat(new[] { medM, highM })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        if (multiModels.Length == 0) multiModels = new[] { medM, highM };

        var multiProfiles = BuildScenarioProfiles(
            multiModels,
            new[] { "medium", "high", "medium" },
            (_, _) => new List<string> { "image_generation", "video_animation", "audio_sfx_bgm", "model_3d_motion", "text_writing", "ui_shader_vfx" },
            overrideTemp: 0.75);

        var pMultimedia = new AgentConfigPreset
        {
            PresetId = "cloud_multimedia",
            Name = "🎨 云端方案 · 游戏美术与多媒体创作",
            SourceKind = "cloud",
            CategoryBadge = "多模态创作",
            Description = "激发创造力温度 (0.75) + 全面开启图/影/音/3D/文案与特效能力分工",
            SourceBasis = sourceBasis,
            UpdatedAt = pBalanced.UpdatedAt,
            PreferredEffortTier = canSelectModels ? "medium" : "auto",
            CustomWorkNote = "原画概念贴图、宣传片分镜视频、游戏 SFX/BGM 音效配乐、3D 模型动作及剧情策划",
            ModelProfiles = multiProfiles,
            Permissions = multiPerms,
            TagScores = ScaleTags(baseRec.RecommendedTags, ("image_asset_gen", 5), ("video_storyboard", 5), ("audio_sfx_music", 4), ("model_3d_anim", 5), ("design_copywriting", 5)),
            RuntimeParams = new AgentRuntimeParams
            {
                Temperature = 0.75,
                ReasoningEffort = "medium",
                ContextBudget = "standard",
                MaxOutputTokens = 16384,
                TimeoutSeconds = 240,
                MaxRetries = 3
            },
            LaunchWindowOnDispatch = agent.LaunchWindowOnDispatch,
            RunnerMode = agent.RunnerMode,
            RunnerCmd = agent.RunnerCmd
        };

        // 方案 5：只读审查与安全验收（严禁直接写工程源码）
        var safePerms = baseRec.RecommendedPermissions.Clone();
        safePerms.AllowDirectSourceWrite = false;
        safePerms.AllowMultiDir = false;
        safePerms.AllowShellBuild = true;
        var safeRoles = hasMcp
            ? new List<string> { "deep_debug", "visual_accept" }
            : new List<string> { "deep_debug" };

        var safeProfiles = BuildScenarioProfiles(
            new[] { highM, medM },
            new[] { "high", "medium" },
            (_, _) => new List<string>(safeRoles),
            overrideTemp: 0.10);

        var pSafeAudit = new AgentConfigPreset
        {
            PresetId = "cloud_safe_audit",
            Name = "🛡️ 云端方案 · 只读审查与安全验收",
            SourceKind = "cloud",
            CategoryBadge = "只读安全",
            Description = "强制关闭写源码权限 + 最低严谨温度 (0.10) + 高推理深度，专职代码审查与验收",
            SourceBasis = sourceBasis,
            UpdatedAt = pBalanced.UpdatedAt,
            PreferredEffortTier = canSelectModels ? "high" : "auto",
            CustomWorkNote = "仅负责代码审查、缺陷挑刺与验收把关，严禁擅自改写工程源文件",
            ModelProfiles = safeProfiles,
            Permissions = safePerms,
            TagScores = ScaleTags(baseRec.RecommendedTags, ("adversarial_review", 5), ("deep_bug_hunt", 5), ("visual_gate", hasMcp ? 5 : 0)),
            RuntimeParams = new AgentRuntimeParams
            {
                Temperature = 0.10,
                ReasoningEffort = "high",
                ContextBudget = "max",
                MaxOutputTokens = 16384,
                TimeoutSeconds = 240,
                MaxRetries = 2
            },
            LaunchWindowOnDispatch = agent.LaunchWindowOnDispatch,
            RunnerMode = agent.RunnerMode,
            RunnerCmd = agent.RunnerCmd
        };

        return new List<AgentConfigPreset> { pBalanced, pDeepArch, pFastDev, pMultimedia, pSafeAudit };
    }

    /// <summary>
    /// 将任意云端方案或用户自定义方案完整应用到 AI 成员实例（严格保留客户端防封号模型锁定）
    /// </summary>
    public static void ApplyPresetToAgent(AgentRosterEntry agent, AgentConfigPreset preset, bool preserveRunnerChannel = true)
    {
        var copy = preset.Clone();
        bool canSelectModels = LocalModelCatalogService.SupportsModelSelection(agent.Id);

        agent.ActivePresetId = string.IsNullOrWhiteSpace(copy.PresetId) ? "cloud_balanced" : copy.PresetId;
        agent.PreferredEffortTier = canSelectModels
            ? (string.IsNullOrWhiteSpace(copy.PreferredEffortTier) ? "auto" : copy.PreferredEffortTier)
            : "auto";
        agent.ModelSelectionMode = string.Equals(copy.SourceKind, "user", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto";
        agent.CustomWorkNote = copy.CustomWorkNote ?? "";
        agent.Permissions = (copy.Permissions ?? new AgentPermissions()).Clone();
        agent.TagScores = new Dictionary<string, int>(copy.TagScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
        agent.RuntimeParams = (copy.RuntimeParams ?? new AgentRuntimeParams()).Clone();

        if (!preserveRunnerChannel && !string.IsNullOrWhiteSpace(copy.RunnerMode))
        {
            agent.LaunchWindowOnDispatch = copy.LaunchWindowOnDispatch;
            agent.RunnerMode = copy.RunnerMode;
            agent.RunnerCmd = copy.RunnerCmd ?? "";
        }

        if (!canSelectModels)
        {
            var mergedRoles = (copy.ModelProfiles ?? new List<ModelWorkProfile>())
                .SelectMany(p => p.PrimaryRoles ?? new List<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (mergedRoles.Count == 0)
            {
                mergedRoles = new List<string> { "text_writing", "image_generation", "video_animation" };
            }
            agent.PrimaryWorkRoles = mergedRoles;
            agent.ModelProfiles = new List<ModelWorkProfile>
            {
                new()
                {
                    ModelName = LocalModelCatalogService.ManagedDefaultModelDisplay,
                    PrimaryRoles = new List<string>(mergedRoles),
                    SpeedTier = "medium",
                    RecommendedEffort = "medium",
                    SpecialtyNote = "由客户端自动托管默认模型，不向服务端传递切换模型参数（防封号）",
                    CustomTemperature = copy.ModelProfiles?.FirstOrDefault()?.CustomTemperature,
                    ContextWindowTokens = copy.ModelProfiles?.FirstOrDefault()?.ContextWindowTokens ?? 131072,
                    MaxOutputTokens = copy.ModelProfiles?.FirstOrDefault()?.MaxOutputTokens ?? 16384
                }
            };
            LocalModelCatalogService.SyncAgentWithLocalClientModels(agent, forceOverwriteTiers: false);
            return;
        }

        agent.ModelProfiles = (copy.ModelProfiles ?? new List<ModelWorkProfile>()).Select(p => p.Clone()).ToList();
        SyncDerivedFieldsFromProfiles(agent);
    }

    /// <summary>
    /// 将当前 AI 的全部配置与运行参数打包保存为用户自定义方案（同名自动更新覆盖，不同名新建多套并存）
    /// </summary>
    public static AgentConfigPreset CaptureCurrentAsUserPreset(AgentRosterEntry agent, string presetName, string? description = null)
    {
        var cleanName = string.IsNullOrWhiteSpace(presetName)
            ? $"自定义方案 {DateTime.Now:MM-dd HH:mm}"
            : presetName.Trim();

        agent.CustomPresets ??= new List<AgentConfigPreset>();
        var existing = agent.CustomPresets.FirstOrDefault(p => string.Equals(p.Name.Trim(), cleanName, StringComparison.OrdinalIgnoreCase));
        var presetId = existing?.PresetId ?? ("user_" + Guid.NewGuid().ToString("N")[..8]);

        var snap = new AgentConfigPreset
        {
            PresetId = presetId,
            Name = cleanName,
            SourceKind = "user",
            CategoryBadge = "用户自定义",
            Description = string.IsNullOrWhiteSpace(description)
                ? $"用户自建配置 · 温度 {(agent.RuntimeParams?.Temperature ?? 0.3):0.00} · 推理 {(agent.RuntimeParams?.ReasoningEffort ?? "auto")}"
                : description.Trim(),
            SourceBasis = "用户手动精细配置",
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            PreferredEffortTier = agent.PreferredEffortTier ?? "auto",
            CustomWorkNote = agent.CustomWorkNote ?? "",
            ModelProfiles = (agent.ModelProfiles ?? new List<ModelWorkProfile>()).Select(p => p.Clone()).ToList(),
            Permissions = (agent.Permissions ?? new AgentPermissions()).Clone(),
            TagScores = new Dictionary<string, int>(agent.TagScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase),
            RuntimeParams = (agent.RuntimeParams ?? new AgentRuntimeParams()).Clone(),
            LaunchWindowOnDispatch = agent.LaunchWindowOnDispatch,
            RunnerMode = agent.RunnerMode ?? "",
            RunnerCmd = agent.RunnerCmd ?? ""
        };

        if (existing != null)
        {
            var idx = agent.CustomPresets.IndexOf(existing);
            agent.CustomPresets[idx] = snap;
        }
        else
        {
            agent.CustomPresets.Insert(0, snap);
        }

        agent.ActivePresetId = presetId;
        agent.ModelSelectionMode = "manual";
        return snap;
    }

    public static bool DeleteUserPreset(AgentRosterEntry agent, string presetId)
    {
        if (agent.CustomPresets == null || string.IsNullOrWhiteSpace(presetId)) return false;
        int removed = agent.CustomPresets.RemoveAll(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0 && string.Equals(agent.ActivePresetId, presetId, StringComparison.OrdinalIgnoreCase))
        {
            agent.ActivePresetId = "cloud_balanced";
        }
        return removed > 0;
    }

    /// <summary>
    /// 结合本机客户端真实模型列表 + 云端模型特征推断引擎，自动构建无冲突的高/中/低协同分工槽位
    /// </summary>
    public static void BuildAutoModelProfilesForAgent(AgentRosterEntry agent, bool refreshLocalModels = false)
    {
        if (!LocalModelCatalogService.SupportsModelSelection(agent.Id))
        {
            LocalModelCatalogService.SyncAgentWithLocalClientModels(agent, forceOverwriteTiers: true);
            return;
        }

        var aid = (agent.Id ?? "").ToLowerInvariant();
        var probe = LocalModelCatalogService.ProbeLocalModelsForAgent(agent.Id, forceRefresh: refreshLocalModels);
        if (probe.Models.Count > 0)
        {
            var merged = new List<string>(probe.Models);
            if (agent.ModelProfiles != null)
            {
                foreach (var mp in agent.ModelProfiles)
                {
                    if (!string.IsNullOrWhiteSpace(mp.ModelName) &&
                        !LocalModelCatalogService.IsLegacyBogusModelName(mp.ModelName) &&
                        !merged.Contains(mp.ModelName, StringComparer.OrdinalIgnoreCase))
                    {
                        merged.Add(mp.ModelName);
                    }
                }
            }
            agent.AvailableModels = merged;
        }

        string highModel = !string.IsNullOrWhiteSpace(probe.SuggestedHigh) ? probe.SuggestedHigh : agent.HighTierModel;
        string medModel = !string.IsNullOrWhiteSpace(probe.SuggestedMedium) ? probe.SuggestedMedium : agent.MediumTierModel;
        string lowModel = !string.IsNullOrWhiteSpace(probe.SuggestedLow) ? probe.SuggestedLow : agent.LowTierModel;

        var list = new List<ModelWorkProfile>();
        void AddSlot(string modelName, string forcedTier)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return;
            if (list.Any(p => string.Equals(p.ModelName, modelName, StringComparison.OrdinalIgnoreCase))) return;
            var spec = InferCloudModelSpec(agent.Id, modelName);
            var roles = new List<string>(spec.RecommendedRoles);
            if (forcedTier == "high" && aid == "antigravity" && !roles.Contains("visual_accept", StringComparer.OrdinalIgnoreCase))
                roles.Add("visual_accept");
            if (forcedTier == "medium" && aid == "grok_bot" && !roles.Contains("image_generation", StringComparer.OrdinalIgnoreCase))
                roles.Add("image_generation");
            if (forcedTier == "low" && !roles.Contains("fast_execution", StringComparer.OrdinalIgnoreCase))
                roles.Insert(0, "fast_execution");

            var mp = new ModelWorkProfile
            {
                ModelName = modelName.Trim(),
                PrimaryRoles = roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                RecommendedEffort = forcedTier,
                SpeedTier = forcedTier == "low" ? "fast" : forcedTier,
                SpecialtyNote = spec.SpecialtyNote,
                ContextWindowTokens = spec.ContextWindowTokens,
                MaxOutputTokens = spec.MaxOutputTokens
            };
            EnsureModelProfileEnriched(agent.Id, mp, list.Count, agent.Category);
            list.Add(mp);
        }

        AddSlot(highModel, "high");
        AddSlot(medModel, "medium");
        AddSlot(lowModel, "low");

        // 若外部节点/本机仅检测到单模型时，补齐至少 1 个完整职责槽位
        if (list.Count == 0 && agent.AvailableModels.Count > 0)
        {
            var first = agent.AvailableModels[0];
            var spec = InferCloudModelSpec(agent.Id, first);
            var fallbackMp = new ModelWorkProfile
            {
                ModelName = first,
                PrimaryRoles = new List<string>(spec.RecommendedRoles),
                RecommendedEffort = spec.Tier,
                SpeedTier = spec.SpeedTier,
                SpecialtyNote = spec.SpecialtyNote,
                ContextWindowTokens = spec.ContextWindowTokens,
                MaxOutputTokens = spec.MaxOutputTokens
            };
            EnsureModelProfileEnriched(agent.Id, fallbackMp, 0, agent.Category);
            list.Add(fallbackMp);
        }

        agent.ModelProfiles = list;
        agent.ModelSelectionMode = "auto";
        agent.PreferredEffortTier = "auto";
        SyncDerivedFieldsFromProfiles(agent);
    }

    /// <summary>
    /// 单一真实数据源回写：以 agent.ModelProfiles 为准，自动同步 High/Medium/LowTierModel、PrimaryWorkRoles 与 AvailableModels
    /// </summary>
    public static void SyncDerivedFieldsFromProfiles(AgentRosterEntry agent)
    {
        if (!LocalModelCatalogService.SupportsModelSelection(agent.Id))
        {
            LocalModelCatalogService.SyncAgentWithLocalClientModels(agent, forceOverwriteTiers: false);
            return;
        }

        agent.ModelProfiles ??= new List<ModelWorkProfile>();
        agent.AvailableModels ??= new List<string>();

        var cleaned = new List<ModelWorkProfile>();
        foreach (var mp in agent.ModelProfiles)
        {
            var mn = (mp.ModelName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(mn) || LocalModelCatalogService.IsLegacyBogusModelName(mn)) continue;
            mp.ModelName = mn;
            mp.RecommendedEffort = (mp.RecommendedEffort ?? "medium").ToLowerInvariant() switch
            {
                "high" => "high",
                "low" => "low",
                _ => "medium"
            };
            mp.SpeedTier = mp.RecommendedEffort == "low" ? "fast" : mp.RecommendedEffort;
            mp.PrimaryRoles = (mp.PrimaryRoles ?? new List<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (string.IsNullOrWhiteSpace(mp.SpecialtyNote) || mp.ContextWindowTokens <= 0 || mp.MaxOutputTokens <= 0)
            {
                var spec = InferCloudModelSpec(agent.Id, mn);
                if (string.IsNullOrWhiteSpace(mp.SpecialtyNote)) mp.SpecialtyNote = spec.SpecialtyNote;
                if (mp.ContextWindowTokens <= 0) mp.ContextWindowTokens = spec.ContextWindowTokens;
                if (mp.MaxOutputTokens <= 0) mp.MaxOutputTokens = spec.MaxOutputTokens;
            }
            EnsureModelProfileEnriched(agent.Id, mp, cleaned.Count, agent.Category);

            cleaned.Add(mp);
            if (LocalModelCatalogService.SupportsCustomModelInput(agent.Id) &&
                !agent.AvailableModels.Contains(mn, StringComparer.OrdinalIgnoreCase))
            {
                agent.AvailableModels.Add(mn);
            }
        }

        agent.ModelProfiles = cleaned;
        if (cleaned.Count == 0)
        {
            BuildAutoModelProfilesForAgent(agent, refreshLocalModels: false);
            return;
        }

        var activeSlots = cleaned.Where(p => p.Enabled).ToList();
        if (activeSlots.Count == 0) activeSlots = cleaned;

        var firstHigh = activeSlots.FirstOrDefault(p => p.RecommendedEffort == "high")
                        ?? activeSlots.FirstOrDefault(p => p.RecommendedEffort == "medium")
                        ?? activeSlots[0];
        var firstMed = activeSlots.FirstOrDefault(p => p.RecommendedEffort == "medium")
                       ?? firstHigh;
        var firstLow = activeSlots.FirstOrDefault(p => p.RecommendedEffort == "low")
                       ?? activeSlots.Last();

        agent.HighTierModel = firstHigh.ModelName;
        agent.MediumTierModel = firstMed.ModelName;
        agent.LowTierModel = firstLow.ModelName;

        // 以已启用模型 CapabilityScores/PrimaryRoles 并集为权威，回写 PrimaryWorkRoles 与 TagScores（禁止用陈旧角色覆盖新勾选能力）
        var unionRoles = activeSlots
            .SelectMany(p =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in p.PrimaryRoles ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(r)) set.Add(r);
                if (p.CapabilityScores != null)
                {
                    foreach (var kv in p.CapabilityScores)
                        if (kv.Value > 0 && !string.IsNullOrWhiteSpace(kv.Key)) set.Add(kv.Key);
                }
                return set;
            })
            .ToList();
        if (unionRoles.Count == 0)
            unionRoles = InferCloudModelSpec(agent.Id, firstHigh.ModelName).RecommendedRoles;
        agent.PrimaryWorkRoles = NormalizePrimaryWorkRoles(unionRoles);

        agent.TagScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in WorkSpecialtyCatalog.OrderedItems)
        {
            int best = 0;
            foreach (var slot in activeSlots)
                best = Math.Max(best, slot.GetCapabilityScore(item.Id));
            if (best > 0) agent.TagScores[item.Id] = best;
            else agent.TagScores.Remove(item.Id);
        }
    }

    /// <summary>
    /// 平滑兼容迁移旧版本上下分裂数据：若用户曾在旧界面单独改过上方的 高/中/低 三档模型而未展开下方槽位，自动合并进对应槽位
    /// </summary>
    public static void ReconcileLegacySplitTiers(AgentRosterEntry agent)
    {
        if (!LocalModelCatalogService.SupportsModelSelection(agent.Id))
        {
            LocalModelCatalogService.SyncAgentWithLocalClientModels(agent, forceOverwriteTiers: false);
            return;
        }

        if (agent.ModelProfiles == null || agent.ModelProfiles.Count == 0)
        {
            EnsureAgentWorkProfilesPopulated(agent, overwrite: false);
            return;
        }

        // 若有任何旧版假模型名残留，直接按本机真实模型重建
        if (agent.ModelProfiles.Any(p => LocalModelCatalogService.IsLegacyBogusModelName(p.ModelName)))
        {
            BuildAutoModelProfilesForAgent(agent, refreshLocalModels: false);
            return;
        }

        bool IsValidReal(string? m) => !string.IsNullOrWhiteSpace(m) && !LocalModelCatalogService.IsLegacyBogusModelName(m);
        var slotHigh = agent.ModelProfiles.FirstOrDefault(p => string.Equals(p.RecommendedEffort, "high", StringComparison.OrdinalIgnoreCase));
        var slotMed = agent.ModelProfiles.FirstOrDefault(p => string.Equals(p.RecommendedEffort, "medium", StringComparison.OrdinalIgnoreCase));
        var slotLow = agent.ModelProfiles.FirstOrDefault(p => string.Equals(p.RecommendedEffort, "low", StringComparison.OrdinalIgnoreCase));

        bool changed = false;
        void MergeTierModel(string tier, string legacyModel, ModelWorkProfile? existingSlot)
        {
            if (!IsValidReal(legacyModel)) return;
            if (existingSlot != null && string.Equals(existingSlot.ModelName, legacyModel, StringComparison.OrdinalIgnoreCase)) return;

            if (existingSlot != null)
            {
                existingSlot.ModelName = legacyModel.Trim();
                var spec = InferCloudModelSpec(agent.Id, legacyModel);
                existingSlot.PrimaryRoles = spec.RecommendedRoles;
                existingSlot.SpecialtyNote = spec.SpecialtyNote;
                changed = true;
            }
            else if (!agent.ModelProfiles.Any(p => string.Equals(p.ModelName, legacyModel, StringComparison.OrdinalIgnoreCase)))
            {
                var spec = InferCloudModelSpec(agent.Id, legacyModel);
                agent.ModelProfiles.Add(new ModelWorkProfile
                {
                    ModelName = legacyModel.Trim(),
                    PrimaryRoles = spec.RecommendedRoles,
                    RecommendedEffort = tier,
                    SpeedTier = tier == "low" ? "fast" : tier,
                    SpecialtyNote = spec.SpecialtyNote
                });
                changed = true;
            }
        }

        // 仅当三档中至少一档与现有槽位不一致时，合并同步并消除重名槽位冲突
        MergeTierModel("high", agent.HighTierModel, slotHigh);
        MergeTierModel("medium", agent.MediumTierModel, slotMed);
        MergeTierModel("low", agent.LowTierModel, slotLow);

        if (changed)
        {
            // 若旧用户将同一个模型填到多个档位或合并后产生完全相同模型名，仅在完全同名时去重保留首个最精准职责槽位
            var dedup = new List<ModelWorkProfile>();
            foreach (var mp in agent.ModelProfiles)
            {
                if (!dedup.Any(x => string.Equals(x.ModelName, mp.ModelName, StringComparison.OrdinalIgnoreCase)))
                    dedup.Add(mp);
            }
            agent.ModelProfiles = dedup;
            SyncDerivedFieldsFromProfiles(agent);
        }
        else
        {
            SyncDerivedFieldsFromProfiles(agent);
        }
    }

    public static void EnsureAgentModelTiersPopulated(AgentRosterEntry agent, CloudAgentRecommendation? rec = null, bool overwrite = false)
    {
        agent.RuntimeParams ??= new AgentRuntimeParams();
        agent.CustomPresets ??= new List<AgentConfigPreset>();
        if (string.IsNullOrWhiteSpace(agent.ActivePresetId)) agent.ActivePresetId = "cloud_balanced";

        LocalModelCatalogService.SyncAgentWithLocalClientModels(agent, forceOverwriteTiers: overwrite);
        rec ??= BuildRecommendationForAgent(agent);
        if (overwrite || string.IsNullOrWhiteSpace(agent.HighTierModel))
            agent.HighTierModel = rec.RecommendedHighModel;
        if (overwrite || string.IsNullOrWhiteSpace(agent.MediumTierModel))
            agent.MediumTierModel = rec.RecommendedMediumModel;
        if (overwrite || string.IsNullOrWhiteSpace(agent.LowTierModel))
            agent.LowTierModel = rec.RecommendedLowModel;
        if (agent.AvailableModels == null || agent.AvailableModels.Count == 0)
            agent.AvailableModels = new List<string>(rec.AvailableModels);
        if (string.IsNullOrWhiteSpace(agent.ModelSelectionMode))
            agent.ModelSelectionMode = "auto";
        if (string.IsNullOrWhiteSpace(agent.PreferredEffortTier))
            agent.PreferredEffortTier = "auto";

        EnsureAgentWorkProfilesPopulated(agent, overwrite);
    }

    public static void EnsureAgentWorkProfilesPopulated(AgentRosterEntry agent, bool overwrite = false)
    {
        var id = (agent.Id ?? "").ToLowerInvariant();
        if (!LocalModelCatalogService.SupportsModelSelection(id))
        {
            if (overwrite || string.IsNullOrWhiteSpace(agent.CustomWorkNote))
                agent.CustomWorkNote = "擅长文本文案策划、图像生成与视觉设计提示词，以及配置脚本快速实操";
            LocalModelCatalogService.SyncAgentWithLocalClientModels(agent, forceOverwriteTiers: overwrite);
            return;
        }

        if (overwrite || string.IsNullOrWhiteSpace(agent.CustomWorkNote))
        {
            agent.CustomWorkNote = id switch
            {
                "antigravity" => "适合做主大脑统筹分析、跨模块拆解、Godot画面实看MCP与最终验收",
                "cursor" => "IDE实操响应快，擅长直接修改代码、预制体、项目设置与深度排障",
                "grok_bot" => "擅长文本文案策划、图像生成与视觉设计提示词，以及配置脚本快速实操",
                "codex_cli" => "后台无头推理与批量重构引擎（默认待命，按需勾选启用）",
                _ => "通用协同执行与文本辅助处理"
            };
        }

        if (overwrite || agent.ModelProfiles == null || agent.ModelProfiles.Count == 0 ||
            agent.ModelProfiles.Any(p => LocalModelCatalogService.IsLegacyBogusModelName(p.ModelName)))
        {
            var list = new List<ModelWorkProfile>();
            void TryAddTier(string? modelName, string tier)
            {
                if (string.IsNullOrWhiteSpace(modelName) || LocalModelCatalogService.IsLegacyBogusModelName(modelName)) return;
                if (list.Any(p => string.Equals(p.ModelName, modelName, StringComparison.OrdinalIgnoreCase))) return;
                var spec = InferCloudModelSpec(agent.Id, modelName);
                var roles = new List<string>(spec.RecommendedRoles);
                if (tier == "high" && id == "antigravity" && !roles.Contains("visual_accept", StringComparer.OrdinalIgnoreCase))
                    roles.Add("visual_accept");
                if (tier == "medium" && id == "grok_bot" && !roles.Contains("image_generation", StringComparer.OrdinalIgnoreCase))
                    roles.Add("image_generation");
                if (tier == "low" && !roles.Contains("fast_execution", StringComparer.OrdinalIgnoreCase))
                    roles.Insert(0, "fast_execution");
                list.Add(new ModelWorkProfile
                {
                    ModelName = modelName.Trim(),
                    PrimaryRoles = roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    SpeedTier = tier == "low" ? "fast" : tier,
                    RecommendedEffort = tier,
                    SpecialtyNote = spec.SpecialtyNote,
                    ContextWindowTokens = spec.ContextWindowTokens,
                    MaxOutputTokens = spec.MaxOutputTokens
                });
            }

            TryAddTier(agent.HighTierModel, "high");
            TryAddTier(agent.MediumTierModel, "medium");
            TryAddTier(agent.LowTierModel, "low");

            if (list.Count == 0 && agent.AvailableModels != null && agent.AvailableModels.Count > 0)
            {
                var first = agent.AvailableModels[0];
                var spec = InferCloudModelSpec(agent.Id, first);
                list.Add(new ModelWorkProfile
                {
                    ModelName = first,
                    PrimaryRoles = new List<string>(spec.RecommendedRoles),
                    SpeedTier = spec.SpeedTier,
                    RecommendedEffort = spec.Tier,
                    SpecialtyNote = spec.SpecialtyNote,
                    ContextWindowTokens = spec.ContextWindowTokens,
                    MaxOutputTokens = spec.MaxOutputTokens
                });
            }
            agent.ModelProfiles = list;
        }

        if (overwrite)
        {
            SyncDerivedFieldsFromProfiles(agent);
        }
        else
        {
            ReconcileLegacySplitTiers(agent);
        }
    }

    public static CloudAgentRecommendation BuildRecommendationForAgent(
        AgentRosterEntry agent,
        bool hasGemini = true,
        bool hasClaude = true,
        bool hasGpt = true,
        bool hasGrok = true)
    {
        var id = (agent.Id ?? "").ToLowerInvariant();
        var safeId = agent.Id ?? "";
        var safeName = agent.Name ?? "";
        var localCatalog = LocalModelCatalogService.ProbeLocalModelsForAgent(safeId);

        if (id == "antigravity")
        {
            return new CloudAgentRecommendation
            {
                AgentId = safeId,
                AgentName = safeName,
                RoleSummary = "全能总包主脑 · 负责全局深度分析、派发实操任务、严格验收与打回重修闭环",
                BenchmarkBasis = localCatalog.SourceDescription,
                RecommendedHighModel = localCatalog.SuggestedHigh,
                RecommendedMediumModel = localCatalog.SuggestedMedium,
                RecommendedLowModel = localCatalog.SuggestedLow,
                AvailableModels = new List<string>(localCatalog.Models),
                RecommendedPermissions = new AgentPermissions
                {
                    AllowDirectSourceWrite = true,
                    AllowMultiDir = true,
                    AllowShellBuild = true,
                    HasPreviewMcp = true
                },
                RecommendedTags = new Dictionary<string, int>
                {
                    ["cross_repo_impl"] = 5,
                    ["build_delivery"] = 5,
                    ["visual_gate"] = 5,
                    ["deep_bug_hunt"] = 5,
                    ["ide_live_tweak"] = 4,
                    ["design_copywriting"] = 4,
                    ["adversarial_review"] = 4,
                    ["offline_math_plot"] = 4,
                    ["image_asset_gen"] = 4,
                    ["video_storyboard"] = 3,
                    ["audio_sfx_music"] = 2,
                    ["model_3d_anim"] = 4
                }
            };
        }

        if (id == "cursor")
        {
            return new CloudAgentRecommendation
            {
                AgentId = safeId,
                AgentName = safeName,
                RoleSummary = "IDE 代码与预制体实操主力 · 擅长直接修改代码、项目设置与预制体",
                BenchmarkBasis = localCatalog.SourceDescription,
                RecommendedHighModel = localCatalog.SuggestedHigh,
                RecommendedMediumModel = localCatalog.SuggestedMedium,
                RecommendedLowModel = localCatalog.SuggestedLow,
                AvailableModels = new List<string>(localCatalog.Models),
                RecommendedPermissions = new AgentPermissions
                {
                    AllowDirectSourceWrite = true,
                    AllowMultiDir = true,
                    AllowShellBuild = true,
                    HasPreviewMcp = true
                },
                RecommendedTags = new Dictionary<string, int>
                {
                    ["ide_live_tweak"] = 5,
                    ["cross_repo_impl"] = 5,
                    ["deep_bug_hunt"] = 4,
                    ["build_delivery"] = 4,
                    ["adversarial_review"] = 4,
                    ["visual_gate"] = 3,
                    ["design_copywriting"] = 3,
                    ["offline_math_plot"] = 3,
                    ["image_asset_gen"] = 2,
                    ["video_storyboard"] = 1,
                    ["audio_sfx_music"] = 2,
                    ["model_3d_anim"] = 4
                }
            };
        }

        if (id == "grok_bot")
        {
            return new CloudAgentRecommendation
            {
                AgentId = safeId,
                AgentName = safeName,
                RoleSummary = "异构推演与文本/视觉/多媒体专家 · 擅长文案策划、图像视频生成设计与脚本实操",
                BenchmarkBasis = localCatalog.SourceDescription,
                RecommendedHighModel = localCatalog.SuggestedHigh,
                RecommendedMediumModel = localCatalog.SuggestedMedium,
                RecommendedLowModel = localCatalog.SuggestedLow,
                AvailableModels = new List<string>(localCatalog.Models),
                RecommendedPermissions = new AgentPermissions
                {
                    AllowDirectSourceWrite = true,
                    AllowMultiDir = true,
                    AllowShellBuild = true,
                    HasPreviewMcp = false
                },
                RecommendedTags = new Dictionary<string, int>
                {
                    ["design_copywriting"] = 5,
                    ["offline_math_plot"] = 5,
                    ["adversarial_review"] = 5,
                    ["cross_repo_impl"] = 4,
                    ["ide_live_tweak"] = 4,
                    ["deep_bug_hunt"] = 4,
                    ["build_delivery"] = 3,
                    ["visual_gate"] = 0,
                    ["image_asset_gen"] = 5,
                    ["video_storyboard"] = 4,
                    ["audio_sfx_music"] = 2,
                    ["model_3d_anim"] = 2
                }
            };
        }

        if (id == "codex_cli")
        {
            return new CloudAgentRecommendation
            {
                AgentId = safeId,
                AgentName = safeName,
                RoleSummary = "深度排障与无人值守执行引擎 · 擅长硬骨头 Bug 推理与重构（默认待命）",
                BenchmarkBasis = localCatalog.SourceDescription,
                RecommendedHighModel = localCatalog.SuggestedHigh,
                RecommendedMediumModel = localCatalog.SuggestedMedium,
                RecommendedLowModel = localCatalog.SuggestedLow,
                AvailableModels = new List<string>(localCatalog.Models),
                RecommendedPermissions = new AgentPermissions
                {
                    AllowDirectSourceWrite = true,
                    AllowMultiDir = true,
                    AllowShellBuild = true,
                    HasPreviewMcp = false
                },
                RecommendedTags = new Dictionary<string, int>
                {
                    ["deep_bug_hunt"] = 5,
                    ["adversarial_review"] = 5,
                    ["design_copywriting"] = 5,
                    ["cross_repo_impl"] = 4,
                    ["build_delivery"] = 4,
                    ["offline_math_plot"] = 4,
                    ["ide_live_tweak"] = 3,
                    ["visual_gate"] = 0,
                    ["image_asset_gen"] = 2,
                    ["video_storyboard"] = 1,
                    ["audio_sfx_music"] = 2,
                    ["model_3d_anim"] = 3
                }
            };
        }

        if (id == "claude_code")
        {
            return new CloudAgentRecommendation
            {
                AgentId = safeId,
                AgentName = safeName,
                RoleSummary = "🧠 超长上下文架构重构与疑难排障专家",
                BenchmarkBasis = "SWE-bench Verified 复杂工程重构高分模型",
                RecommendedHighModel = "claude-4.6-opus-high",
                RecommendedMediumModel = "claude-4.6-sonnet-medium",
                RecommendedLowModel = "claude-4.5-haiku-low",
                AvailableModels = new List<string> { "claude-4.6-opus-high", "claude-4.6-sonnet-medium", "claude-4.5-haiku-low" },
                RecommendedPermissions = new AgentPermissions
                {
                    AllowDirectSourceWrite = true,
                    AllowMultiDir = true,
                    AllowShellBuild = true,
                    HasPreviewMcp = false
                },
                RecommendedTags = new Dictionary<string, int>
                {
                    ["cross_repo_impl"] = 5,
                    ["deep_bug_hunt"] = 5,
                    ["adversarial_review"] = 5,
                    ["build_delivery"] = 4,
                    ["ide_live_tweak"] = 4,
                    ["design_copywriting"] = 4
                }
            };
        }

        return new CloudAgentRecommendation
        {
            AgentId = safeId,
            AgentName = safeName,
            RoleSummary = "⚙ 通用协同 AI 节点 · 支持按配置执行代码修改、方案推演与审查任务",
            BenchmarkBasis = localCatalog.SourceDescription,
            RecommendedHighModel = localCatalog.SuggestedHigh,
            RecommendedMediumModel = localCatalog.SuggestedMedium,
            RecommendedLowModel = localCatalog.SuggestedLow,
            AvailableModels = new List<string>(localCatalog.Models),
            RecommendedPermissions = new AgentPermissions
            {
                AllowDirectSourceWrite = true,
                AllowMultiDir = true,
                AllowShellBuild = true,
                HasPreviewMcp = false
            },
            RecommendedTags = new Dictionary<string, int>
            {
                ["cross_repo_impl"] = 4,
                ["ide_live_tweak"] = 4,
                ["adversarial_review"] = 4,
                ["design_copywriting"] = 4,
                ["deep_bug_hunt"] = 3
            }
        };
    }
}
