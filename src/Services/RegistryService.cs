using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AICollabHub.Models;

namespace AICollabHub.Services;

public sealed class RegistryService
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly string[] EmbeddedRegistryFiles =
    {
        "detectors.json",
        "adapters.json",
        "capability_tags.json",
        "workflows.json",
        "project_presets.json"
    };

    public DetectorRegistryFile Detectors { get; private set; } = new();
    public TagRegistryFile Tags { get; private set; } = new();
    public AdapterRegistryFile Adapters { get; private set; } = new();
    public WorkflowRegistryFile Workflows { get; private set; } = new();
    public PresetRegistryFile Presets { get; private set; } = new();
    public UserHubConfig UserConfig { get; private set; } = new();
    /// <summary>本次 LoadAll 读盘前，用户配置文件是否已经存在。供新手引导区分老用户，避免随后的保存把新文件误判成老配置。</summary>
    public bool ConfigFileExistedAtLoad { get; private set; }
    private DateTime _lastUserConfigDiskWriteUtc = DateTime.MinValue;

    public void LoadAll()
    {
        EnsureEmbeddedRegistryFilesMaterialized();
        EnsureEmbeddedPluginScriptMaterialized();

        Detectors = LoadJsonWithEmbeddedFallback<DetectorRegistryFile>(
            Path.Combine(AppPaths.RegistryDir, "detectors.json"),
            "AICollabHub.Embedded.registry.detectors.json");
        EnsureBuiltinDetectorsPresent();
        Tags = LoadJsonWithEmbeddedFallback<TagRegistryFile>(
            Path.Combine(AppPaths.RegistryDir, "capability_tags.json"),
            "AICollabHub.Embedded.registry.capability_tags.json");
        EnsureBuiltinCapabilityTagsPresent();
        Adapters = LoadJsonWithEmbeddedFallback<AdapterRegistryFile>(
            Path.Combine(AppPaths.RegistryDir, "adapters.json"),
            "AICollabHub.Embedded.registry.adapters.json");
        Workflows = LoadJsonWithEmbeddedFallback<WorkflowRegistryFile>(
            Path.Combine(AppPaths.RegistryDir, "workflows.json"),
            "AICollabHub.Embedded.registry.workflows.json");
        Presets = LoadJsonWithEmbeddedFallback<PresetRegistryFile>(
            Path.Combine(AppPaths.RegistryDir, "project_presets.json"),
            "AICollabHub.Embedded.registry.project_presets.json");
        ConfigFileExistedAtLoad = File.Exists(AppPaths.UserConfigPath);
        UserConfig = LoadJsonOrDefault<UserHubConfig>(AppPaths.UserConfigFile);
        if (File.Exists(AppPaths.UserConfigFile))
        {
            _lastUserConfigDiskWriteUtc = File.GetLastWriteTimeUtc(AppPaths.UserConfigFile);
        }

        EnsureInitialProjectIfEmpty();
        EnsureCursorAndGrokDispatchReady();
        if (!AppPaths.IsIsolatedTest)
        {
            new DiscoveryService().RefreshStaleDetectionStates(this);
        }
        SyncGlobalRoutesAndAgentRoleConsistency(persistIfChanged: true);
        EnsureDefaultTeamProfiles();
    }

    /// <summary>若 registry 目录缺少内置 JSON，从程序集内嵌资源释放到磁盘。</summary>
    public static void EnsureEmbeddedRegistryFilesMaterialized()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RegistryDir);
            foreach (var name in EmbeddedRegistryFiles)
            {
                var dest = Path.Combine(AppPaths.RegistryDir, name);
                if (File.Exists(dest))
                {
                    try
                    {
                        var txt = File.ReadAllText(dest, Encoding.UTF8);
                        if (!string.IsNullOrWhiteSpace(txt) && txt.TrimStart().StartsWith('{'))
                            continue;
                    }
                    catch { /* 损坏则覆盖 */ }
                }
                TryMaterializeEmbeddedResource($"AICollabHub.Embedded.registry.{name}", dest);
            }
        }
        catch { }
    }

    /// <summary>把 5 份内嵌 registry JSON 与插件脚本释放到空白目录并加载探测器数量。</summary>
    public static (int DetectorCount, bool ProbeFieldsPresent, int ReleasedFileCount) ReleaseEmbeddedBundleToBlankDirectory(string blankRoot)
    {
        var registryDir = Path.Combine(blankRoot, "registry");
        var pluginsDir = Path.Combine(blankRoot, "plugins");
        Directory.CreateDirectory(registryDir);
        Directory.CreateDirectory(pluginsDir);
        int released = 0;
        foreach (var name in EmbeddedRegistryFiles)
        {
            var dest = Path.Combine(registryDir, name);
            if (TryMaterializeEmbeddedResource($"AICollabHub.Embedded.registry.{name}", dest) &&
                File.Exists(dest) && new FileInfo(dest).Length > 2)
                released++;
        }
        var script = Path.Combine(pluginsDir, "export_config_digest.ps1");
        if (TryMaterializeEmbeddedResource("AICollabHub.Embedded.plugins.export_config_digest.ps1", script) &&
            File.Exists(script) && new FileInfo(script).Length > 2)
            released++;

        var detectorsPath = Path.Combine(registryDir, "detectors.json");
        var parsed = JsonSerializer.Deserialize<DetectorRegistryFile>(File.ReadAllText(detectorsPath, Encoding.UTF8), JsonOpts);
        var dets = parsed?.Detectors ?? new List<DetectorSpec>();
        bool probes = dets.Count > 0 && dets.All(d =>
            !string.IsNullOrWhiteSpace(d.Id) &&
            (d.ProbePaths.Any(s => !string.IsNullOrWhiteSpace(s)) ||
             d.ProbeCommands.Any(s => !string.IsNullOrWhiteSpace(s)) ||
             d.ProbeProcessNames.Any(s => !string.IsNullOrWhiteSpace(s)) ||
             d.ProbeDisplayNames.Any(s => !string.IsNullOrWhiteSpace(s)) ||
             d.ProbeExtensionIds.Any(s => !string.IsNullOrWhiteSpace(s)) ||
             d.ProbeMsixPrefixes.Any(s => !string.IsNullOrWhiteSpace(s)) ||
             d.ProbePorts.Count > 0 ||
             !string.IsNullOrWhiteSpace(d.JsonNodesFile)));
        return (dets.Count, probes, released);
    }

    /// <summary>PluginsDir 缺少 export_config_digest.ps1 时按需释放内嵌脚本。</summary>
    public static void EnsureEmbeddedPluginScriptMaterialized()
    {
        try
        {
            var dest = Path.Combine(AppPaths.PluginsDir, "export_config_digest.ps1");
            if (File.Exists(dest)) return;
            TryMaterializeEmbeddedResource("AICollabHub.Embedded.plugins.export_config_digest.ps1", dest);
        }
        catch { }
    }

    private static bool TryMaterializeEmbeddedResource(string logicalName, string destPath)
    {
        try
        {
            using var stream = OpenEmbeddedResourceStream(logicalName);
            if (stream == null) return false;
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = File.Create(destPath);
            stream.CopyTo(fs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Stream? OpenEmbeddedResourceStream(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceStream(logicalName);
    }

    private static string? ReadEmbeddedResourceText(string logicalName)
    {
        try
        {
            using var stream = OpenEmbeddedResourceStream(logicalName);
            if (stream == null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 磁盘 JSON 缺失、损坏或核心集合为空时，从内嵌资源释放并加载；写盘失败时仍可直接从流反序列化。
    /// </summary>
    private static T LoadJsonWithEmbeddedFallback<T>(string path, string embeddedLogicalName) where T : new()
    {
        T? fromDisk = default;
        bool diskOk = false;
        try
        {
            if (File.Exists(path))
            {
                var txt = File.ReadAllText(path, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    fromDisk = JsonSerializer.Deserialize<T>(txt, JsonOpts);
                    diskOk = fromDisk != null && !IsCoreCollectionEmpty(fromDisk);
                }
            }
        }
        catch
        {
            diskOk = false;
        }

        if (diskOk && fromDisk != null)
            return fromDisk;

        var embeddedTxt = ReadEmbeddedResourceText(embeddedLogicalName);
        if (!string.IsNullOrWhiteSpace(embeddedTxt))
        {
            try
            {
                TryMaterializeEmbeddedResource(embeddedLogicalName, path);
            }
            catch { }

            try
            {
                var fromEmb = JsonSerializer.Deserialize<T>(embeddedTxt, JsonOpts);
                if (fromEmb != null && !IsCoreCollectionEmpty(fromEmb))
                    return fromEmb;
                if (fromEmb != null)
                    return fromEmb;
            }
            catch { }
        }

        return fromDisk ?? new T();
    }

    private static bool IsCoreCollectionEmpty<T>(T obj)
    {
        return obj switch
        {
            DetectorRegistryFile d => d.Detectors == null || d.Detectors.Count == 0,
            TagRegistryFile t => t.Tags == null || t.Tags.Count == 0,
            AdapterRegistryFile a => a.Adapters == null || a.Adapters.Count == 0,
            WorkflowRegistryFile w => w.Workflows == null || w.Workflows.Count == 0,
            PresetRegistryFile p => p.Presets == null || p.Presets.Count == 0,
            _ => false
        };
    }

    private void EnsureBuiltinCapabilityTagsPresent()
    {
        Tags.Tags ??= new List<CapabilityTagSpec>();
        var defaults = new (string Id, string Name, string Desc, string Color)[]
        {
            ("image_asset_gen", "图片与贴图创作", "概念原画、UI 图标、材质贴图与立绘视觉生成", "#F472B6"),
            ("video_storyboard", "视频与动态分镜", "宣传视频生成、镜头运动设计与序列帧动态表现", "#FB7185"),
            ("audio_sfx_music", "音效配乐与语音", "SFX 打击环境音效、BGM 作曲编曲与角色 TTS 配音", "#34D399"),
            ("model_3d_anim", "3D建模与骨骼动作", "Blender/glTF 模型处理、骨骼权重蒙皮与动画轨道", "#60A5FA")
        };
        foreach (var (id, name, desc, color) in defaults)
        {
            if (!Tags.Tags.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                Tags.Tags.Add(new CapabilityTagSpec { Id = id, Name = name, Description = desc, Color = color });
            }
        }
    }

    /// <summary>
    /// 从内嵌 detectors.json 补齐缺失项；对磁盘已有但探测字段全空/残缺的条目补全探测字段。
    /// 绝不再创建 ProbePaths/ProbeCommands/ProbeProcessNames 全空的空壳 DetectorSpec。
    /// </summary>
    private void EnsureBuiltinDetectorsPresent()
    {
        Detectors.Detectors ??= new List<DetectorSpec>();
        var embedded = LoadEmbeddedDetectorRegistry();
        if (embedded == null || embedded.Detectors == null || embedded.Detectors.Count == 0)
            return;

        var byId = new Dictionary<string, DetectorSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in embedded.Detectors)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.Id)) continue;
            byId[d.Id] = d;
        }

        foreach (var builtin in byId.Values)
        {
            var existing = Detectors.Detectors.FirstOrDefault(x =>
                string.Equals(x.Id, builtin.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                Detectors.Detectors.Add(CloneDetectorSpec(builtin));
                continue;
            }
            MergeMissingProbeFields(existing, builtin);
        }
    }

    private static DetectorRegistryFile? LoadEmbeddedDetectorRegistry()
    {
        try
        {
            var txt = ReadEmbeddedResourceText("AICollabHub.Embedded.registry.detectors.json");
            if (string.IsNullOrWhiteSpace(txt))
            {
                var path = Path.Combine(AppPaths.RegistryDir, "detectors.json");
                if (File.Exists(path))
                    txt = File.ReadAllText(path, Encoding.UTF8);
            }
            if (string.IsNullOrWhiteSpace(txt)) return null;
            return JsonSerializer.Deserialize<DetectorRegistryFile>(txt, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static DetectorSpec CloneDetectorSpec(DetectorSpec src)
    {
        var json = JsonSerializer.Serialize(src, JsonOpts);
        return JsonSerializer.Deserialize<DetectorSpec>(json, JsonOpts) ?? new DetectorSpec
        {
            Id = src.Id,
            Name = src.Name,
            Category = src.Category,
            Description = src.Description,
            DefaultAdapter = src.DefaultAdapter,
            ProbePaths = new List<string>(src.ProbePaths ?? new List<string>()),
            ProbeCommands = new List<string>(src.ProbeCommands ?? new List<string>()),
            ProbeProcessNames = new List<string>(src.ProbeProcessNames ?? new List<string>()),
            ProbeDisplayNames = new List<string>(src.ProbeDisplayNames ?? new List<string>()),
            ProbePorts = new List<int>(src.ProbePorts ?? new List<int>()),
            ProbeExtensionIds = new List<string>(src.ProbeExtensionIds ?? new List<string>())
        };
    }

    private static void MergeMissingProbeFields(DetectorSpec target, DetectorSpec builtin)
    {
        static void MergeList(List<string> dest, List<string>? src)
        {
            if (src == null || src.Count == 0) return;
            dest ??= new List<string>();
            foreach (var s in src)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (!dest.Contains(s, StringComparer.OrdinalIgnoreCase))
                    dest.Add(s);
            }
        }

        target.ProbePaths ??= new List<string>();
        target.ProbeCommands ??= new List<string>();
        target.ProbeProcessNames ??= new List<string>();
        target.ProbeDisplayNames ??= new List<string>();
        target.ProbePorts ??= new List<int>();
        target.ProbeExtensionIds ??= new List<string>();

        bool probesEmpty = target.ProbePaths.Count == 0 &&
                           target.ProbeCommands.Count == 0 &&
                           target.ProbeProcessNames.Count == 0;
        if (probesEmpty || target.ProbePaths.Count == 0)
            MergeList(target.ProbePaths, builtin.ProbePaths);
        if (probesEmpty || target.ProbeCommands.Count == 0)
            MergeList(target.ProbeCommands, builtin.ProbeCommands);
        if (probesEmpty || target.ProbeProcessNames.Count == 0)
            MergeList(target.ProbeProcessNames, builtin.ProbeProcessNames);
        if (target.ProbeDisplayNames.Count == 0)
            MergeList(target.ProbeDisplayNames, builtin.ProbeDisplayNames);
        if (target.ProbeExtensionIds.Count == 0)
            MergeList(target.ProbeExtensionIds, builtin.ProbeExtensionIds);
        if ((target.ProbePorts == null || target.ProbePorts.Count == 0) &&
            builtin.ProbePorts != null && builtin.ProbePorts.Count > 0)
        {
            target.ProbePorts = new List<int>(builtin.ProbePorts);
        }

        if (string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(builtin.Name))
            target.Name = builtin.Name;
        if (string.IsNullOrWhiteSpace(target.Category) && !string.IsNullOrWhiteSpace(builtin.Category))
            target.Category = builtin.Category;
        if (string.IsNullOrWhiteSpace(target.DefaultAdapter) && !string.IsNullOrWhiteSpace(builtin.DefaultAdapter))
            target.DefaultAdapter = builtin.DefaultAdapter;
    }

    public sealed class WorkflowToggleResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public bool Enabled { get; init; }
    }

    /// <summary>
    /// 原子启停工作流（兼容 out 反馈）：同步 agent.Enabled + 当前激活方案 AgentSnapshot。
    /// </summary>
    public bool SetAgentWorkflowEnabled(string agentId, bool enabled, out string feedbackMessage)
    {
        var result = SetAgentWorkflowEnabled(agentId, enabled);
        feedbackMessage = result.Message;
        return result.Ok;
    }

    /// <summary>
    /// 批量原子启停：按顺序应用；若某次关闭会把工作流清成 0 个则整批中止并回滚该次，返回 false。
    /// </summary>
    public bool SetAgentsWorkflowEnabled(IEnumerable<(string AgentId, bool Enabled)> toggles, out string feedbackMessage)
    {
        feedbackMessage = "";
        if (toggles == null)
        {
            feedbackMessage = "无效的批量开关列表";
            return false;
        }
        var list = toggles.ToList();
        if (list.Count == 0)
        {
            feedbackMessage = "没有需要切换的工具";
            return true;
        }

        int okCount = 0;
        foreach (var (agentId, enabled) in list)
        {
            var result = SetAgentWorkflowEnabled(agentId, enabled);
            if (!result.Ok)
            {
                feedbackMessage = okCount > 0
                    ? $"已成功切换 {okCount} 项，后续失败：{result.Message}"
                    : result.Message;
                return false;
            }
            okCount++;
        }
        feedbackMessage = $"已批量切换 {okCount} 项工作流开关";
        return true;
    }

    /// <summary>
    /// 原子启停工作流：同步 agent.Enabled + 当前激活方案 AgentSnapshot，首次开启时补快照/默认模型/职责，
    /// 禁止关成 0 个；若关闭当前主脑或主脑节点则自动重选/同步主脑与路由。
    /// </summary>
    public WorkflowToggleResult SetAgentWorkflowEnabled(string agentId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return new WorkflowToggleResult { Ok = false, Message = "无效的 AI 工具 ID", Enabled = false };

        var agent = UserConfig.Agents.FirstOrDefault(a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));
        if (agent == null)
            return new WorkflowToggleResult { Ok = false, Message = $"未找到 AI 工具：{agentId}", Enabled = false };

        if (!enabled)
        {
            int enabledCount = UserConfig.Agents.Count(a => a.Enabled && !string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));
            if (enabledCount <= 0)
            {
                return new WorkflowToggleResult
                {
                    Ok = false,
                    Message = "至少需保留 1 个工具在工作流中，已取消关闭操作",
                    Enabled = true
                };
            }
        }

        agent.Enabled = enabled;
        if (enabled)
        {
            // 首次开启新检出主流 AI：从探测器补能力分
            var det = Detectors.Detectors?.FirstOrDefault(d =>
                string.Equals(d.Id, agent.DetectorId ?? agent.Id, StringComparison.OrdinalIgnoreCase));
            if (det?.DefaultTags != null)
            {
                agent.TagScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in det.DefaultTags)
                {
                    if (!agent.TagScores.ContainsKey(kv.Key) || agent.TagScores[kv.Key] < kv.Value)
                        agent.TagScores[kv.Key] = kv.Value;
                }
            }
            CloudRecommendationService.EnsureAgentModelTiersPopulated(agent);
            CloudRecommendationService.SyncDerivedFieldsFromProfiles(agent);
            if (agent.PrimaryWorkRoles == null || agent.PrimaryWorkRoles.Count == 0)
            {
                agent.PrimaryWorkRoles = agent.TagScores?
                    .Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .Take(4)
                    .ToList() ?? new List<string>();
            }
        }

        UserConfig.TeamProfiles ??= new List<TeamConfigProfile>();
        var active = UserConfig.TeamProfiles.FirstOrDefault(p =>
                         string.Equals(p.Id, UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase))
                     ?? UserConfig.TeamProfiles.FirstOrDefault();
        if (active != null)
        {
            active.AgentSnapshots ??= new List<TeamAgentSnapshot>();
            var snap = active.AgentSnapshots.FirstOrDefault(s =>
                string.Equals(s.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase));
            if (snap == null)
            {
                snap = new TeamAgentSnapshot
                {
                    AgentId = agent.Id,
                    Enabled = enabled,
                    ModelSelectionMode = agent.ModelSelectionMode ?? "auto",
                    PreferredEffortTier = agent.PreferredEffortTier ?? "auto",
                    HighTierModel = agent.HighTierModel ?? "",
                    MediumTierModel = agent.MediumTierModel ?? "",
                    LowTierModel = agent.LowTierModel ?? "",
                    ActivePresetId = agent.ActivePresetId ?? "cloud_balanced",
                    CustomWorkNote = agent.CustomWorkNote ?? "",
                    RunnerMode = agent.RunnerMode ?? "",
                    RunnerCmd = agent.RunnerCmd ?? "",
                    LaunchWindowOnDispatch = agent.LaunchWindowOnDispatch,
                    PrimaryWorkRoles = new List<string>(agent.PrimaryWorkRoles ?? new List<string>()),
                    Permissions = agent.Permissions?.Clone() ?? new AgentPermissions(),
                    RuntimeParams = agent.RuntimeParams?.Clone() ?? new AgentRuntimeParams(),
                    ModelProfiles = (agent.ModelProfiles ?? new List<ModelWorkProfile>()).Select(m => m.Clone()).ToList(),
                    TagScores = new Dictionary<string, int>(agent.TagScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase)
                };
                active.AgentSnapshots.Add(snap);
            }
            else
            {
                snap.Enabled = enabled;
                if (enabled)
                {
                    if (snap.ModelProfiles == null || snap.ModelProfiles.Count == 0)
                        snap.ModelProfiles = (agent.ModelProfiles ?? new List<ModelWorkProfile>()).Select(m => m.Clone()).ToList();
                    if (snap.PrimaryWorkRoles == null || snap.PrimaryWorkRoles.Count == 0)
                        snap.PrimaryWorkRoles = new List<string>(agent.PrimaryWorkRoles ?? new List<string>());
                    if (snap.TagScores == null || snap.TagScores.Count == 0)
                        snap.TagScores = new Dictionary<string, int>(agent.TagScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
                    CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, agent);
                }
            }
            active.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        // 关闭当前主脑或主脑节点 → 自动改回 auto 并重选；同时同步路由一致性
        if (!enabled)
        {
            bool masterIsAuto = string.IsNullOrWhiteSpace(UserConfig.GlobalMasterPlannerId) ||
                                UserConfig.GlobalMasterPlannerId.Equals("auto", StringComparison.OrdinalIgnoreCase);
            string currentMaster = UserConfig.GlobalMasterPlannerId ?? "auto";
            if (active != null && !string.IsNullOrWhiteSpace(active.MasterPlannerId) &&
                !active.MasterPlannerId.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                currentMaster = active.MasterPlannerId;
                masterIsAuto = false;
            }

            if (string.Equals(currentMaster, agent.Id, StringComparison.OrdinalIgnoreCase))
            {
                UserConfig.GlobalMasterPlannerId = "auto";
                if (active != null) active.MasterPlannerId = "auto";
                foreach (var p in UserConfig.Projects) p.MasterPlannerId = "auto";
                _ = RoutingService.ResolveMasterPlanner(this, GetActiveProject());
            }
            else if (masterIsAuto)
            {
                _ = RoutingService.ResolveMasterPlanner(this, GetActiveProject());
            }

            if (!string.IsNullOrWhiteSpace(UserConfig.GlobalFallbackBuilderId) &&
                string.Equals(UserConfig.GlobalFallbackBuilderId, agent.Id, StringComparison.OrdinalIgnoreCase))
            {
                UserConfig.GlobalFallbackBuilderId = UserConfig.Agents.Any(a => a.Enabled && a.Id.Equals("cursor", StringComparison.OrdinalIgnoreCase))
                    ? "cursor"
                    : (UserConfig.Agents.FirstOrDefault(a => a.Enabled && !string.Equals(a.Id, agent.Id, StringComparison.OrdinalIgnoreCase))?.Id ?? "");
                if (active != null) active.FallbackBuilderId = UserConfig.GlobalFallbackBuilderId;
            }
        }

        SyncGlobalRoutesAndAgentRoleConsistency(persistIfChanged: false);
        SaveUserConfig();
        return new WorkflowToggleResult
        {
            Ok = true,
            Enabled = enabled,
            Message = enabled ? $"已将「{agent.Name}」加入工作流" : $"已将「{agent.Name}」移出工作流"
        };
    }

    public bool ReloadUserConfigFromDiskIfNeeded()
    {
        try
        {
            if (!File.Exists(AppPaths.UserConfigFile)) return false;
            var diskTime = File.GetLastWriteTimeUtc(AppPaths.UserConfigFile);
            if (diskTime <= _lastUserConfigDiskWriteUtc) return false;
            var fresh = LoadJsonOrDefault<UserHubConfig>(AppPaths.UserConfigFile);
            if (fresh.Projects.Count > 0)
            {
                fresh.FloatingWindowLeft ??= UserConfig.FloatingWindowLeft;
                fresh.FloatingWindowTop ??= UserConfig.FloatingWindowTop;
                UserConfig = fresh;
                _lastUserConfigDiskWriteUtc = diskTime;
                SyncGlobalRoutesAndAgentRoleConsistency(persistIfChanged: false);
                return true;
            }
        }
        catch
        {
            // 忽略并发写盘瞬态异常
        }
        return false;
    }

    public bool ReloadUserConfigFromDiskForce()
    {
        try
        {
            if (!File.Exists(AppPaths.UserConfigFile)) return false;
            var diskTime = File.GetLastWriteTimeUtc(AppPaths.UserConfigFile);
            var fresh = LoadJsonOrDefault<UserHubConfig>(AppPaths.UserConfigFile);
            if (fresh.Projects.Count > 0)
            {
                fresh.FloatingWindowLeft ??= UserConfig.FloatingWindowLeft;
                fresh.FloatingWindowTop ??= UserConfig.FloatingWindowTop;
                UserConfig = fresh;
                _lastUserConfigDiskWriteUtc = diskTime;
                SyncGlobalRoutesAndAgentRoleConsistency(persistIfChanged: false);
                return true;
            }
        }
        catch { }
        return false;
    }

    public void SyncGlobalRoutesAndAgentRoleConsistency(bool persistIfChanged = false)
    {
        bool changed = false;
        UserConfig.GlobalWorkTypeRoutes ??= new Dictionary<string, WorkRouteRule>(StringComparer.OrdinalIgnoreCase);

        if (UserConfig.GlobalWorkTypeRoutes.Count == 0)
        {
            var donor = UserConfig.Projects.FirstOrDefault(p => p.WorkTypeRoutes != null && p.WorkTypeRoutes.Count > 0);
            if (donor != null)
            {
                foreach (var kv in donor.WorkTypeRoutes)
                {
                    UserConfig.GlobalWorkTypeRoutes[kv.Key] = CloneRouteRule(kv.Value);
                }
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(UserConfig.GlobalMasterPlannerId) || UserConfig.GlobalMasterPlannerId == "auto")
        {
            var mDonor = UserConfig.Projects.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.MasterPlannerId) && p.MasterPlannerId != "auto");
            if (mDonor != null)
            {
                UserConfig.GlobalMasterPlannerId = mDonor.MasterPlannerId;
                changed = true;
            }
        }
        if (string.IsNullOrWhiteSpace(UserConfig.GlobalFallbackBuilderId))
        {
            var fDonor = UserConfig.Projects.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.FallbackBuilderId));
            if (fDonor != null)
            {
                UserConfig.GlobalFallbackBuilderId = fDonor.FallbackBuilderId;
                changed = true;
            }
        }

        var enabledIds = new HashSet<string>(
            UserConfig.Agents.Where(a => a.Enabled).Select(a => a.Id),
            StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(UserConfig.GlobalFallbackBuilderId) && !enabledIds.Contains(UserConfig.GlobalFallbackBuilderId))
        {
            UserConfig.GlobalFallbackBuilderId = enabledIds.Contains("cursor") ? "cursor" : "";
            changed = true;
        }

        foreach (var proj in UserConfig.Projects)
        {
            proj.WorkTypeRoutes ??= new Dictionary<string, WorkRouteRule>(StringComparer.OrdinalIgnoreCase);
            if (proj.WorkTypeRoutes.Count == 0 && UserConfig.GlobalWorkTypeRoutes.Count > 0)
            {
                foreach (var kv in UserConfig.GlobalWorkTypeRoutes)
                {
                    proj.WorkTypeRoutes[kv.Key] = CloneRouteRule(kv.Value);
                }
                changed = true;
            }
            if ((string.IsNullOrWhiteSpace(proj.MasterPlannerId) || proj.MasterPlannerId == "auto") &&
                !string.IsNullOrWhiteSpace(UserConfig.GlobalMasterPlannerId) && UserConfig.GlobalMasterPlannerId != "auto")
            {
                proj.MasterPlannerId = UserConfig.GlobalMasterPlannerId;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(proj.FallbackBuilderId) && !enabledIds.Contains(proj.FallbackBuilderId))
            {
                proj.FallbackBuilderId = UserConfig.GlobalFallbackBuilderId;
                changed = true;
            }
            else if (string.IsNullOrWhiteSpace(proj.FallbackBuilderId) && !string.IsNullOrWhiteSpace(UserConfig.GlobalFallbackBuilderId))
            {
                proj.FallbackBuilderId = UserConfig.GlobalFallbackBuilderId;
                changed = true;
            }
        }

        foreach (var ag in UserConfig.Agents)
        {
            var normalized = CloudRecommendationService.NormalizePrimaryWorkRoles(ag.PrimaryWorkRoles);
            if (ag.PrimaryWorkRoles == null ||
                ag.PrimaryWorkRoles.Count != normalized.Count ||
                !ag.PrimaryWorkRoles.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase))
            {
                ag.PrimaryWorkRoles = normalized;
                changed = true;
            }

            if (ag.PrimaryWorkRoles.Count == 0 || ag.ModelProfiles == null || ag.ModelProfiles.Count == 0)
                continue;

            var allowed = new HashSet<string>(ag.PrimaryWorkRoles, StringComparer.OrdinalIgnoreCase);
            foreach (var mp in ag.ModelProfiles)
            {
                if (mp.PrimaryRoles == null) continue;
                var cleanedMp = CloudRecommendationService.NormalizePrimaryWorkRoles(mp.PrimaryRoles)
                    .Where(r => allowed.Contains(r))
                    .ToList();
                if (cleanedMp.Count != mp.PrimaryRoles.Count ||
                    !mp.PrimaryRoles.SequenceEqual(cleanedMp, StringComparer.OrdinalIgnoreCase))
                {
                    mp.PrimaryRoles = cleanedMp;
                    changed = true;
                }
            }

            foreach (var role in ag.PrimaryWorkRoles)
            {
                if (!ag.ModelProfiles.Any(mp => mp.PrimaryRoles != null && mp.PrimaryRoles.Contains(role, StringComparer.OrdinalIgnoreCase)))
                {
                    ag.ModelProfiles[0].PrimaryRoles ??= new List<string>();
                    if (!ag.ModelProfiles[0].PrimaryRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                    {
                        ag.ModelProfiles[0].PrimaryRoles.Add(role);
                        changed = true;
                    }
                }
            }
        }

        // 方案快照一并去重，防止写入膨胀回灌运行态
        if (UserConfig.TeamProfiles != null)
        {
            foreach (var tp in UserConfig.TeamProfiles)
            {
                if (tp.AgentSnapshots == null) continue;
                foreach (var snap in tp.AgentSnapshots)
                {
                    var n = CloudRecommendationService.NormalizePrimaryWorkRoles(snap.PrimaryWorkRoles);
                    if (snap.PrimaryWorkRoles == null ||
                        snap.PrimaryWorkRoles.Count != n.Count ||
                        !snap.PrimaryWorkRoles.SequenceEqual(n, StringComparer.OrdinalIgnoreCase))
                    {
                        snap.PrimaryWorkRoles = n;
                        changed = true;
                    }
                }
            }
        }

        if (changed && persistIfChanged)
        {
            SaveUserConfig();
        }
    }

    public static WorkRouteRule CloneRouteRule(WorkRouteRule src) => new()
    {
        WorkTypeId = src.WorkTypeId,
        TargetAgentId = src.TargetAgentId,
        TargetModelName = src.TargetModelName,
        TargetEffortTier = src.TargetEffortTier,
        CustomKeywords = src.CustomKeywords != null ? new List<string>(src.CustomKeywords) : new List<string>()
    };

    public TeamConfigProfile CaptureCurrentAsTeamProfile(string name, string description = "", string? fixedId = null)
    {
        UserConfig.TeamProfiles ??= new List<TeamConfigProfile>();
        var trimmed = string.IsNullOrWhiteSpace(name) ? $"配置方案 {UserConfig.TeamProfiles.Count + 1}" : name.Trim();
        var existing = UserConfig.TeamProfiles.FirstOrDefault(p =>
            (!string.IsNullOrWhiteSpace(fixedId) && string.Equals(p.Id, fixedId, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        var proj = GetActiveProject();
        var masterId = !string.IsNullOrWhiteSpace(UserConfig.GlobalMasterPlannerId) && UserConfig.GlobalMasterPlannerId != "auto"
            ? UserConfig.GlobalMasterPlannerId
            : (proj?.MasterPlannerId ?? "auto");
        var fbId = !string.IsNullOrWhiteSpace(UserConfig.GlobalFallbackBuilderId)
            ? UserConfig.GlobalFallbackBuilderId
            : (proj?.FallbackBuilderId ?? "");

        var routesCopy = new Dictionary<string, WorkRouteRule>(StringComparer.OrdinalIgnoreCase);
        var srcRoutes = UserConfig.GlobalWorkTypeRoutes != null && UserConfig.GlobalWorkTypeRoutes.Count > 0
            ? UserConfig.GlobalWorkTypeRoutes
            : (proj?.WorkTypeRoutes ?? new Dictionary<string, WorkRouteRule>());
        foreach (var kv in srcRoutes)
        {
            routesCopy[kv.Key] = CloneRouteRule(kv.Value);
        }

        var snaps = UserConfig.Agents.Select(ag => new TeamAgentSnapshot
        {
            AgentId = ag.Id,
            Enabled = ag.Enabled,
            ModelSelectionMode = ag.ModelSelectionMode ?? "auto",
            PreferredEffortTier = ag.PreferredEffortTier ?? "auto",
            HighTierModel = ag.HighTierModel ?? "",
            MediumTierModel = ag.MediumTierModel ?? "",
            LowTierModel = ag.LowTierModel ?? "",
            ActivePresetId = ag.ActivePresetId ?? "cloud_balanced",
            CustomWorkNote = ag.CustomWorkNote ?? "",
            RunnerMode = ag.RunnerMode ?? "",
            RunnerCmd = ag.RunnerCmd ?? "",
            LaunchWindowOnDispatch = ag.LaunchWindowOnDispatch,
            PrimaryWorkRoles = new List<string>(ag.PrimaryWorkRoles ?? new List<string>()),
            Permissions = ag.Permissions?.Clone() ?? new AgentPermissions(),
            RuntimeParams = ag.RuntimeParams?.Clone() ?? new AgentRuntimeParams(),
            ModelProfiles = (ag.ModelProfiles ?? new List<ModelWorkProfile>()).Select(m => m.Clone()).ToList(),
            TagScores = new Dictionary<string, int>(ag.TagScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase)
        }).ToList();

        if (existing != null)
        {
            existing.Name = trimmed;
            if (!string.IsNullOrWhiteSpace(description))
            {
                existing.Description = description;
            }
            existing.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            existing.MasterPlannerId = masterId;
            existing.FallbackBuilderId = fbId;
            existing.MaxReworkRounds = UserConfig.MaxReworkRounds;
            existing.PreferNativeModelsWhenTied = UserConfig.PreferNativeModelsWhenTied;
            existing.WorkTypeRoutes = routesCopy;
            existing.AgentSnapshots = snaps;
            UserConfig.ActiveTeamProfileId = existing.Id;
            return existing;
        }

        var profile = new TeamConfigProfile
        {
            Id = !string.IsNullOrWhiteSpace(fixedId) ? fixedId : Guid.NewGuid().ToString("N")[..8],
            Name = trimmed,
            Description = description ?? "",
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            MasterPlannerId = masterId,
            FallbackBuilderId = fbId,
            MaxReworkRounds = UserConfig.MaxReworkRounds,
            PreferNativeModelsWhenTied = UserConfig.PreferNativeModelsWhenTied,
            WorkTypeRoutes = routesCopy,
            AgentSnapshots = snaps
        };
        UserConfig.TeamProfiles.Add(profile);
        UserConfig.ActiveTeamProfileId = profile.Id;
        return profile;
    }

    /// <summary>将当前主界面/弹窗中的所有模型、分工、路由实时同步回当前激活的配置方案。</summary>
    public void SyncActiveTeamProfileWithCurrentState(bool saveToDisk = false)
    {
        UserConfig.TeamProfiles ??= new List<TeamConfigProfile>();
        var active = UserConfig.TeamProfiles.FirstOrDefault(p =>
            string.Equals(p.Id, UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase))
            ?? UserConfig.TeamProfiles.FirstOrDefault();
        if (active != null)
        {
            CaptureCurrentAsTeamProfile(active.Name, active.Description, fixedId: active.Id);
        }
        else
        {
            CaptureCurrentAsTeamProfile("默认全局协同方案", "自动同步的全局配置方案");
        }
        if (saveToDisk)
        {
            SaveUserConfig();
        }
    }

    public TeamConfigProfile CreateNewTeamProfile(string name, string description = "")
    {
        UserConfig.TeamProfiles ??= new List<TeamConfigProfile>();
        string baseName = string.IsNullOrWhiteSpace(name) ? $"新建配置方案 {UserConfig.TeamProfiles.Count + 1}" : name.Trim();
        string uniqueName = EnsureUniqueTeamProfileName(baseName, null);
        string freshId = "tp_" + Guid.NewGuid().ToString("N")[..8];
        return CaptureCurrentAsTeamProfile(uniqueName, string.IsNullOrWhiteSpace(description) ? "新建自定义团队与模型配置方案" : description.Trim(), fixedId: freshId);
    }

    public TeamConfigProfile? DuplicateTeamProfile(string sourceProfileId, string? customNewName = null)
    {
        UserConfig.TeamProfiles ??= new List<TeamConfigProfile>();
        var src = UserConfig.TeamProfiles.FirstOrDefault(p => string.Equals(p.Id, sourceProfileId, StringComparison.OrdinalIgnoreCase));
        if (src == null) return null;

        string targetName = string.IsNullOrWhiteSpace(customNewName)
            ? EnsureUniqueTeamProfileName($"{src.Name} - 副本", null)
            : EnsureUniqueTeamProfileName(customNewName.Trim(), null);

        var routesCopy = new Dictionary<string, WorkRouteRule>(StringComparer.OrdinalIgnoreCase);
        if (src.WorkTypeRoutes != null)
        {
            foreach (var kv in src.WorkTypeRoutes) routesCopy[kv.Key] = CloneRouteRule(kv.Value);
        }
        var snapsCopy = (src.AgentSnapshots ?? new List<TeamAgentSnapshot>()).Select(s => new TeamAgentSnapshot
        {
            AgentId = s.AgentId,
            Enabled = s.Enabled,
            ModelSelectionMode = s.ModelSelectionMode,
            PreferredEffortTier = s.PreferredEffortTier,
            HighTierModel = s.HighTierModel,
            MediumTierModel = s.MediumTierModel,
            LowTierModel = s.LowTierModel,
            ActivePresetId = s.ActivePresetId,
            CustomWorkNote = s.CustomWorkNote,
            RunnerMode = s.RunnerMode,
            RunnerCmd = s.RunnerCmd,
            LaunchWindowOnDispatch = s.LaunchWindowOnDispatch,
            PrimaryWorkRoles = new List<string>(s.PrimaryWorkRoles ?? new List<string>()),
            Permissions = s.Permissions?.Clone() ?? new AgentPermissions(),
            RuntimeParams = s.RuntimeParams?.Clone() ?? new AgentRuntimeParams(),
            ModelProfiles = (s.ModelProfiles ?? new List<ModelWorkProfile>()).Select(m => m.Clone()).ToList(),
            TagScores = new Dictionary<string, int>(s.TagScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase)
        }).ToList();

        var copy = new TeamConfigProfile
        {
            Id = "tp_" + Guid.NewGuid().ToString("N")[..8],
            Name = targetName,
            Description = string.IsNullOrWhiteSpace(src.Description) ? $"复制自「{src.Name}」" : src.Description,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            MasterPlannerId = src.MasterPlannerId,
            FallbackBuilderId = src.FallbackBuilderId,
            MaxReworkRounds = src.MaxReworkRounds,
            PreferNativeModelsWhenTied = src.PreferNativeModelsWhenTied,
            WorkTypeRoutes = routesCopy,
            AgentSnapshots = snapsCopy
        };
        int srcIdx = UserConfig.TeamProfiles.IndexOf(src);
        if (srcIdx >= 0 && srcIdx + 1 < UserConfig.TeamProfiles.Count)
            UserConfig.TeamProfiles.Insert(srcIdx + 1, copy);
        else
            UserConfig.TeamProfiles.Add(copy);
        return copy;
    }

    public bool RenameTeamProfile(string profileId, string newName, string? newDescription = null)
    {
        if (UserConfig.TeamProfiles == null || string.IsNullOrWhiteSpace(profileId)) return false;
        var target = UserConfig.TeamProfiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (target == null) return false;
        var trimmed = (newName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        target.Name = EnsureUniqueTeamProfileName(trimmed, target.Id);
        if (newDescription != null)
        {
            target.Description = newDescription.Trim();
        }
        target.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return true;
    }

    private string EnsureUniqueTeamProfileName(string desired, string? selfId)
    {
        UserConfig.TeamProfiles ??= new List<TeamConfigProfile>();
        string candidate = desired;
        int seq = 2;
        while (UserConfig.TeamProfiles.Any(p =>
                   !string.Equals(p.Id, selfId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{desired} ({seq++})";
        }
        return candidate;
    }

    public void ApplyTeamProfile(TeamConfigProfile profile, bool saveToDisk = false)
    {
        if (profile == null) return;
        UserConfig.ActiveTeamProfileId = profile.Id;
        UserConfig.GlobalMasterPlannerId = string.IsNullOrWhiteSpace(profile.MasterPlannerId) ? "auto" : profile.MasterPlannerId;
        UserConfig.GlobalFallbackBuilderId = profile.FallbackBuilderId ?? "";
        UserConfig.MaxReworkRounds = Math.Clamp(profile.MaxReworkRounds, 0, 3);
        UserConfig.PreferNativeModelsWhenTied = profile.PreferNativeModelsWhenTied;

        UserConfig.GlobalWorkTypeRoutes = new Dictionary<string, WorkRouteRule>(StringComparer.OrdinalIgnoreCase);
        if (profile.WorkTypeRoutes != null)
        {
            foreach (var kv in profile.WorkTypeRoutes)
            {
                UserConfig.GlobalWorkTypeRoutes[kv.Key] = CloneRouteRule(kv.Value);
            }
        }

        foreach (var p in UserConfig.Projects)
        {
            p.MasterPlannerId = UserConfig.GlobalMasterPlannerId;
            p.FallbackBuilderId = UserConfig.GlobalFallbackBuilderId;
            p.WorkTypeRoutes = new Dictionary<string, WorkRouteRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in UserConfig.GlobalWorkTypeRoutes)
            {
                p.WorkTypeRoutes[kv.Key] = CloneRouteRule(kv.Value);
            }
        }

        if (profile.AgentSnapshots != null)
        {
            foreach (var snap in profile.AgentSnapshots)
            {
                var ag = UserConfig.Agents.FirstOrDefault(a => string.Equals(a.Id, snap.AgentId, StringComparison.OrdinalIgnoreCase));
                if (ag == null) continue;
                ag.Enabled = snap.Enabled;
                ag.ModelSelectionMode = string.IsNullOrWhiteSpace(snap.ModelSelectionMode) ? "auto" : snap.ModelSelectionMode;
                ag.PreferredEffortTier = string.IsNullOrWhiteSpace(snap.PreferredEffortTier) ? "auto" : snap.PreferredEffortTier;
                if (!string.IsNullOrWhiteSpace(snap.HighTierModel)) ag.HighTierModel = snap.HighTierModel;
                if (!string.IsNullOrWhiteSpace(snap.MediumTierModel)) ag.MediumTierModel = snap.MediumTierModel;
                if (!string.IsNullOrWhiteSpace(snap.LowTierModel)) ag.LowTierModel = snap.LowTierModel;
                if (!string.IsNullOrWhiteSpace(snap.ActivePresetId)) ag.ActivePresetId = snap.ActivePresetId;
                ag.CustomWorkNote = snap.CustomWorkNote ?? ag.CustomWorkNote;
                if (!string.IsNullOrWhiteSpace(snap.RunnerMode)) ag.RunnerMode = snap.RunnerMode;
                if (!string.IsNullOrWhiteSpace(snap.RunnerCmd)) ag.RunnerCmd = snap.RunnerCmd;
                if (snap.LaunchWindowOnDispatch.HasValue) ag.LaunchWindowOnDispatch = snap.LaunchWindowOnDispatch.Value;
                ag.Permissions = snap.Permissions?.Clone() ?? ag.Permissions;
                ag.RuntimeParams = snap.RuntimeParams?.Clone() ?? ag.RuntimeParams;
                if (snap.ModelProfiles != null && snap.ModelProfiles.Count > 0)
                {
                    bool snapUsesCurrentModels = ag.AvailableModels == null || ag.AvailableModels.Count == 0 ||
                        snap.ModelProfiles.Any(m => ag.AvailableModels.Contains(m.ModelName, StringComparer.OrdinalIgnoreCase));
                    if (snapUsesCurrentModels || ag.ModelProfiles == null || ag.ModelProfiles.Count == 0)
                    {
                        ag.ModelProfiles = snap.ModelProfiles.Select(m => m.Clone()).ToList();
                    }
                    else
                    {
                        // 保留快照模型能力，禁止用陈旧 PrimaryWorkRoles 覆盖新勾选的 CapabilityScores
                        ag.ModelProfiles = snap.ModelProfiles.Select(m => m.Clone()).ToList();
                    }
                    for (int idx = 0; idx < ag.ModelProfiles.Count; idx++)
                    {
                        CloudRecommendationService.EnsureModelProfileEnriched(ag.Id, ag.ModelProfiles[idx], idx, ag.Category);
                    }
                }
                CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, ag);
                CloudRecommendationService.ApplyDerivedRolesToRuntimeAgent(snap, ag);

                // 显式同步 TagScores：清空能力后旧高分不得残留；空字典且启用模型有能力时从模型聚合
                if (snap.TagScores != null)
                {
                    ag.TagScores = new Dictionary<string, int>(snap.TagScores, StringComparer.OrdinalIgnoreCase);
                }
                bool snapTagsEmpty = snap.TagScores == null ||
                                     snap.TagScores.Count == 0 ||
                                     !snap.TagScores.Any(kv => kv.Value > 0);
                if (snapTagsEmpty && ag.ModelProfiles != null)
                {
                    var enabledSlots = ag.ModelProfiles.Where(m => m.Enabled).ToList();
                    bool hasCaps = enabledSlots.Any(m =>
                        m.CapabilityScores != null && m.CapabilityScores.Any(kv => kv.Value > 0));
                    if (hasCaps)
                    {
                        ag.TagScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in WorkSpecialtyCatalog.OrderedItems)
                        {
                            int best = 0;
                            foreach (var slot in enabledSlots)
                                best = Math.Max(best, slot.GetCapabilityScore(item.Id));
                            if (best > 0) ag.TagScores[item.Id] = best;
                            else ag.TagScores.Remove(item.Id);
                        }
                    }
                }
            }
        }

        SyncGlobalRoutesAndAgentRoleConsistency(persistIfChanged: false);
        if (saveToDisk)
        {
            SaveUserConfig();
        }
    }

    public void ExportTeamProfileToJson(TeamConfigProfile profile, string filePath)
    {
        if (profile == null || string.IsNullOrWhiteSpace(filePath)) return;
        WriteJsonAtomic(filePath, profile);
    }

    public TeamConfigProfile? ImportTeamProfileFromJson(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        try
        {
            var raw = File.ReadAllText(filePath, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<TeamConfigProfile>(raw, JsonOpts);
            if (parsed == null) return null;
            UserConfig.TeamProfiles ??= new List<TeamConfigProfile>();
            parsed.Id = "tp_" + Guid.NewGuid().ToString("N")[..8];
            parsed.Name = EnsureUniqueTeamProfileName(
                string.IsNullOrWhiteSpace(parsed.Name) ? $"导入方案 {UserConfig.TeamProfiles.Count + 1}" : parsed.Name.Trim(),
                null);
            parsed.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            parsed.WorkTypeRoutes ??= new Dictionary<string, WorkRouteRule>(StringComparer.OrdinalIgnoreCase);
            parsed.AgentSnapshots ??= new List<TeamAgentSnapshot>();
            UserConfig.TeamProfiles.Add(parsed);
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    public bool DeleteTeamProfile(string profileId)
    {
        if (UserConfig.TeamProfiles == null || string.IsNullOrWhiteSpace(profileId)) return false;
        int removed = UserConfig.TeamProfiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0 && string.Equals(UserConfig.ActiveTeamProfileId, profileId, StringComparison.OrdinalIgnoreCase))
        {
            UserConfig.ActiveTeamProfileId = UserConfig.TeamProfiles.FirstOrDefault()?.Id ?? "";
        }
        return removed > 0;
    }

    public void EnsureDefaultTeamProfiles()
    {
        if (AppPaths.IsIsolatedTest) return;
        UserConfig.TeamProfiles ??= new List<TeamConfigProfile>();
        if (UserConfig.TeamProfiles.Count > 0)
        {
            ReconcileSaveBrainProfileAndActiveRoles();
            return;
        }

        // 1. 原样保留用户当前正在使用的配置为「常规均衡配置（主脑深度参与）」
        var p1 = CaptureCurrentAsTeamProfile(
            "常规均衡配置（主脑深度参与）",
            "保留自您此前的配置：Antigravity 深度参与分析、排障审查与验收；Cursor 负责开发实装",
            fixedId: "tp_balanced_default");

        // 2. 基于当前配置构建「省主脑额度（Cursor 主力开发 + 排障 + 自检）」并默认启用
        var ecoSnap = p1.AgentSnapshots.Select(s => new TeamAgentSnapshot
        {
            AgentId = s.AgentId,
            Enabled = s.Enabled,
            PreferredEffortTier = s.PreferredEffortTier,
            PrimaryWorkRoles = new List<string>(s.PrimaryWorkRoles),
            Permissions = s.Permissions.Clone(),
            RuntimeParams = s.RuntimeParams.Clone(),
            ModelProfiles = s.ModelProfiles.Select(m => m.Clone()).ToList(),
            TagScores = new Dictionary<string, int>(s.TagScores, StringComparer.OrdinalIgnoreCase)
        }).ToList();

        foreach (var sn in ecoSnap)
        {
            if (sn.AgentId.Equals("antigravity", StringComparison.OrdinalIgnoreCase))
            {
                sn.PrimaryWorkRoles = new List<string> { "brain_analysis", "visual_accept" };
                sn.RuntimeParams.ReasoningEffort = "medium";
                sn.PreferredEffortTier = "auto";
                for (int idx = 0; idx < sn.ModelProfiles.Count; idx++)
                {
                    sn.ModelProfiles[idx].PrimaryRoles = idx == 0
                        ? new List<string> { "brain_analysis", "visual_accept" }
                        : new List<string>();
                }
            }
            else if (sn.AgentId.Equals("cursor", StringComparison.OrdinalIgnoreCase))
            {
                sn.PrimaryWorkRoles = new List<string> { "fast_execution", "deep_debug", "ui_shader_vfx", "data_config", "build_testing", "image_generation" };
                sn.Permissions.AllowDirectSourceWrite = true;
                sn.Permissions.AllowMultiDir = true;
                sn.Permissions.AllowShellBuild = true;
                sn.Permissions.HasPreviewMcp = true;
                sn.RuntimeParams.ReasoningEffort = "auto";
                sn.RuntimeParams.ContextBudget = "standard";
            }
        }

        var ecoRoutes = new Dictionary<string, WorkRouteRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["brain_analysis"] = new() { WorkTypeId = "brain_analysis", TargetAgentId = "antigravity", TargetModelName = "auto", TargetEffortTier = "auto" },
            ["visual_accept"] = new() { WorkTypeId = "visual_accept", TargetAgentId = "antigravity", TargetModelName = "auto", TargetEffortTier = "auto" },
            ["fast_execution"] = new() { WorkTypeId = "fast_execution", TargetAgentId = "cursor", TargetModelName = "auto", TargetEffortTier = "auto" },
            ["deep_debug"] = new() { WorkTypeId = "deep_debug", TargetAgentId = "cursor", TargetModelName = "auto", TargetEffortTier = "auto" },
            ["ui_shader_vfx"] = new() { WorkTypeId = "ui_shader_vfx", TargetAgentId = "cursor", TargetModelName = "auto", TargetEffortTier = "auto" },
            ["data_config"] = new() { WorkTypeId = "data_config", TargetAgentId = "cursor", TargetModelName = "auto", TargetEffortTier = "auto" },
            ["build_testing"] = new() { WorkTypeId = "build_testing", TargetAgentId = "cursor", TargetModelName = "auto", TargetEffortTier = "auto" },
            ["text_writing"] = new() { WorkTypeId = "text_writing", TargetAgentId = "grok_bot", TargetModelName = "auto", TargetEffortTier = "auto" },
            ["image_generation"] = new() { WorkTypeId = "image_generation", TargetAgentId = "cursor", TargetModelName = "auto", TargetEffortTier = "auto" }
        };

        var p2 = new TeamConfigProfile
        {
            Id = "tp_cursor_heavy_save_brain",
            Name = "省主脑额度（Cursor 主力开发 + 排障 + 自检）",
            Description = "Antigravity 专注出方案与最终验收；Cursor 全面承担开发、UI特效、排障审查、配表、测试与作图",
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            MasterPlannerId = "antigravity",
            FallbackBuilderId = "cursor",
            MaxReworkRounds = 1,
            WorkTypeRoutes = ecoRoutes,
            AgentSnapshots = ecoSnap
        };

        UserConfig.TeamProfiles.Add(p2);
        ApplyTeamProfile(p2, saveToDisk: true);
    }

    /// <summary>
    /// 启动与方案一致性自愈：确保「省主脑额度」方案中的 Antigravity 始终专注「总分析与架构 + 画面实看验收」，
    /// 并同步更新快照里的过时模型名为当前最新模型，防止云端模型刷新后反向覆盖出「开发/配表」等越权分工。
    /// </summary>
    private void ReconcileSaveBrainProfileAndActiveRoles()
    {
        bool changed = false;
        var saveBrain = UserConfig.TeamProfiles.FirstOrDefault(p =>
            string.Equals(p.Id, "tp_cursor_heavy_save_brain", StringComparison.OrdinalIgnoreCase));
        var agRuntime = UserConfig.Agents.FirstOrDefault(a =>
            string.Equals(a.Id, "antigravity", StringComparison.OrdinalIgnoreCase));

        if (saveBrain != null)
        {
            if (saveBrain.WorkTypeRoutes != null &&
                saveBrain.WorkTypeRoutes.TryGetValue("brain_analysis", out var br) &&
                br != null &&
                br.TargetModelName.Contains("Carbon", StringComparison.OrdinalIgnoreCase))
            {
                br.TargetModelName = "auto";
                changed = true;
            }

            var agSnap = saveBrain.AgentSnapshots?.FirstOrDefault(s =>
                string.Equals(s.AgentId, "antigravity", StringComparison.OrdinalIgnoreCase));
            if (agSnap != null)
            {
                var desiredRoles = new List<string> { "brain_analysis", "visual_accept" };
                if (agSnap.PrimaryWorkRoles == null ||
                    !agSnap.PrimaryWorkRoles.Contains("brain_analysis", StringComparer.OrdinalIgnoreCase) ||
                    agSnap.PrimaryWorkRoles.Contains("fast_execution", StringComparer.OrdinalIgnoreCase) ||
                    agSnap.PrimaryWorkRoles.Contains("data_config", StringComparer.OrdinalIgnoreCase))
                {
                    agSnap.PrimaryWorkRoles = new List<string>(desiredRoles);
                    changed = true;
                }

                if (agRuntime?.ModelProfiles != null && agRuntime.ModelProfiles.Count > 0)
                {
                    bool snapStale = agSnap.ModelProfiles == null || agSnap.ModelProfiles.Count == 0 ||
                        agSnap.ModelProfiles.Any(m => !agRuntime.ModelProfiles.Any(rm => string.Equals(rm.ModelName, m.ModelName, StringComparison.OrdinalIgnoreCase)));
                    if (snapStale)
                    {
                        agSnap.ModelProfiles = agRuntime.ModelProfiles.Select(m => m.Clone()).ToList();
                        changed = true;
                    }
                }

                if (agSnap.ModelProfiles != null)
                {
                    for (int idx = 0; idx < agSnap.ModelProfiles.Count; idx++)
                    {
                        var targetRoles = idx == 0 ? new List<string>(desiredRoles) : new List<string>();
                        if (!agSnap.ModelProfiles[idx].PrimaryRoles.SequenceEqual(targetRoles, StringComparer.OrdinalIgnoreCase))
                        {
                            agSnap.ModelProfiles[idx].PrimaryRoles = targetRoles;
                            changed = true;
                        }
                    }
                }
            }
        }

        if (string.Equals(UserConfig.ActiveTeamProfileId, "tp_cursor_heavy_save_brain", StringComparison.OrdinalIgnoreCase) &&
            agRuntime != null)
        {
            var desiredRoles = new List<string> { "brain_analysis", "visual_accept" };
            bool runtimeCorrupted = agRuntime.PrimaryWorkRoles == null ||
                !agRuntime.PrimaryWorkRoles.Contains("brain_analysis", StringComparer.OrdinalIgnoreCase) ||
                agRuntime.PrimaryWorkRoles.Contains("fast_execution", StringComparer.OrdinalIgnoreCase) ||
                agRuntime.PrimaryWorkRoles.Contains("data_config", StringComparer.OrdinalIgnoreCase);

            if (runtimeCorrupted)
            {
                agRuntime.PrimaryWorkRoles = new List<string>(desiredRoles);
                if (agRuntime.ModelProfiles != null)
                {
                    for (int idx = 0; idx < agRuntime.ModelProfiles.Count; idx++)
                    {
                        agRuntime.ModelProfiles[idx].PrimaryRoles = idx == 0
                            ? new List<string>(desiredRoles)
                            : new List<string>();
                    }
                }
                if (UserConfig.GlobalWorkTypeRoutes.TryGetValue("brain_analysis", out var gbr) &&
                    gbr != null && gbr.TargetModelName.Contains("Carbon", StringComparison.OrdinalIgnoreCase))
                {
                    gbr.TargetModelName = "auto";
                }
                changed = true;
            }
        }

        if (changed)
        {
            SyncGlobalRoutesAndAgentRoleConsistency(persistIfChanged: false);
            SaveUserConfig();
        }
    }

    private void EnsureCursorAndGrokDispatchReady()
    {
        bool changed = false;
        foreach (var ag in UserConfig.Agents)
        {
            if ((ag.Id == "cursor" || ag.Id == "grok_bot") &&
                (string.IsNullOrWhiteSpace(ag.RunnerMode) || ag.RunnerMode == "none"))
            {
                if (ag.Detected || AutoDispatchService.ResolveGuiExecutable(ag) != null)
                {
                    ag.RunnerMode = "codex_exec";
                    if (string.IsNullOrWhiteSpace(ag.RunnerCmd)) ag.RunnerCmd = "auto";
                    changed = true;
                }
            }
        }
        if (changed) SaveUserConfig();
    }

    public void SaveRegistries()
    {
        WriteJsonAtomic(Path.Combine(AppPaths.RegistryDir, "detectors.json"), Detectors);
        WriteJsonAtomic(Path.Combine(AppPaths.RegistryDir, "capability_tags.json"), Tags);
        WriteJsonAtomic(Path.Combine(AppPaths.RegistryDir, "workflows.json"), Workflows);
    }

    public void SaveUserConfig()
    {
        // 落盘前对全部 Agents / TeamProfiles 快照做 PrimaryWorkRoles 去重，根除无限累积
        foreach (var ag in UserConfig.Agents ?? new List<AgentRosterEntry>())
            ag.PrimaryWorkRoles = CloudRecommendationService.NormalizePrimaryWorkRoles(ag.PrimaryWorkRoles);
        if (UserConfig.TeamProfiles != null)
        {
            foreach (var tp in UserConfig.TeamProfiles)
            {
                if (tp.AgentSnapshots == null) continue;
                foreach (var snap in tp.AgentSnapshots)
                    snap.PrimaryWorkRoles = CloudRecommendationService.NormalizePrimaryWorkRoles(snap.PrimaryWorkRoles);
            }
        }

        // --cli 静默进程（或磁盘已被 GUI 更新得更新）落盘前合并保留最新 UI 偏好，避免旧内存覆写静音开关
        bool preserveUiPrefs = SoundFxService.IsCliSilent;
        if (!preserveUiPrefs && File.Exists(AppPaths.UserConfigFile))
        {
            try
            {
                var diskTime = File.GetLastWriteTimeUtc(AppPaths.UserConfigFile);
                if (diskTime > _lastUserConfigDiskWriteUtc)
                    preserveUiPrefs = true;
            }
            catch { }
        }
        if (preserveUiPrefs && File.Exists(AppPaths.UserConfigFile))
        {
            try
            {
                var diskCfg = LoadJsonOrDefault<UserHubConfig>(AppPaths.UserConfigFile);
                UserConfig.EnableSoundEffects = diskCfg.EnableSoundEffects;
                UserConfig.SoundLightweightMode = diskCfg.SoundLightweightMode;
                UserConfig.FloatingWindowCollapsed = diskCfg.FloatingWindowCollapsed;
                UserConfig.FloatingLayoutMode = diskCfg.FloatingLayoutMode ?? UserConfig.FloatingLayoutMode;
                UserConfig.FloatingOpacityPercent = diskCfg.FloatingOpacityPercent;
            }
            catch
            {
                // 磁盘瞬时不可读时按内存继续写，不阻断 Save
            }
        }

        WriteJsonAtomic(AppPaths.UserConfigFile, UserConfig);
        if (File.Exists(AppPaths.UserConfigFile))
        {
            _lastUserConfigDiskWriteUtc = File.GetLastWriteTimeUtc(AppPaths.UserConfigFile);
        }
    }

    public ProjectProfile? GetActiveProject()
    {
        var proj = UserConfig.Projects.FirstOrDefault(p => p.Id == UserConfig.ActiveProjectId)
                   ?? UserConfig.Projects.FirstOrDefault();
        if (proj != null && UserConfig.ActiveProjectId != proj.Id)
        {
            UserConfig.ActiveProjectId = proj.Id;
        }
        return proj;
    }

    public ProjectProfile EnsureOrSwitchProjectByDir(string rawProjectDir)
    {
        var fullDir = Path.GetFullPath(rawProjectDir.Trim().Trim('"'))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var existing = UserConfig.Projects.FirstOrDefault(p =>
            string.Equals(Path.GetFullPath(p.PrimaryRoot).TrimEnd(Path.DirectorySeparatorChar), fullDir, StringComparison.OrdinalIgnoreCase) ||
            p.CompanionRoots.Any(c => string.Equals(Path.GetFullPath(c).TrimEnd(Path.DirectorySeparatorChar), fullDir, StringComparison.OrdinalIgnoreCase)));

        if (existing != null)
        {
            UserConfig.ActiveProjectId = existing.Id;
            SaveUserConfig();
            return existing;
        }

        bool isGodot = File.Exists(Path.Combine(fullDir, "project.godot"));
        var folderName = Path.GetFileName(fullDir);
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "自动接入工程";

        var created = new ProjectProfile
        {
            Id = "proj_" + Guid.NewGuid().ToString("N")[..6],
            Name = isGodot ? $"{folderName} (Godot 工程)" : $"{folderName} (自动纳管工程)",
            PrimaryRoot = fullDir,
            PresetId = isGodot ? "godot_project" : "general_code",
            RulesRelDir = ".cursor/rules",
            SkillsRelDir = ".cursor/skills",
            MemoryRelDir = ".cursor/memory",
            CollabRelDir = "work/collab"
        };

        UserConfig.Projects.Add(created);
        UserConfig.ActiveProjectId = created.Id;
        SyncGlobalRoutesAndAgentRoleConsistency(persistIfChanged: false);
        SaveUserConfig();
        return created;
    }

    private void EnsureInitialProjectIfEmpty()
    {
        if (UserConfig.Projects.Count > 0) return;

        // 动态探测当前电脑父级工作区是否有正在使用的 Godot 工程（零硬编码，仅在本机首次无配置时智能初始化）
        var probe = new DirectoryInfo(AppPaths.AppBaseDir);
        DirectoryInfo? toolRepoRoot = null;
        for (int depth = 0; depth < 7 && probe.Parent != null; depth++)
        {
            var parent = probe.Parent;
            foreach (var sibling in parent.GetDirectories())
            {
                if (string.Equals(sibling.FullName, probe.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(Path.Combine(sibling.FullName, "project.godot")))
                {
                    toolRepoRoot = probe;
                    var p = new ProjectProfile
                    {
                        Id = "proj_default",
                        Name = sibling.Name + " (Godot 多目录工程)",
                        PrimaryRoot = sibling.FullName,
                        PresetId = "godot_project",
                        RulesRelDir = ".cursor/rules",
                        SkillsRelDir = ".cursor/skills",
                        MemoryRelDir = ".cursor/memory",
                        CollabRelDir = "work/collab"
                    };
                    if (Directory.Exists(toolRepoRoot.FullName))
                    {
                        p.CompanionRoots.Add(toolRepoRoot.FullName);
                    }
                    if (File.Exists(Path.Combine(AppPaths.PluginsDir, "export_config_digest.ps1")))
                    {
                        p.EnabledHooks.Add("export_config_digest.ps1");
                    }
                    UserConfig.Projects.Add(p);
                    UserConfig.ActiveProjectId = p.Id;
                    SaveUserConfig();
                    return;
                }
            }
            probe = parent;
        }

        // 其他任何电脑首次打开时：创建一个未绑定路径的示例引导档案，界面保持完整不空白
        var cwd = Environment.CurrentDirectory;
        var sample = new ProjectProfile
        {
            Id = "proj_welcome",
            Name = "示例项目（点击顶部「项目设置」选择你的工程目录）",
            PrimaryRoot = Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            PresetId = "general_code"
        };
        UserConfig.Projects.Add(sample);
        UserConfig.ActiveProjectId = sample.Id;
        SaveUserConfig();
    }

    private static T LoadJsonOrDefault<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
            {
                var txt = File.ReadAllText(path, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    var obj = JsonSerializer.Deserialize<T>(txt, JsonOpts);
                    if (obj != null) return obj;
                }
            }
        }
        catch
        {
            // 损坏时安全降级默认实例
        }
        return new T();
    }

    public static void WriteJsonAtomic<T>(string path, T data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(data, JsonOpts);
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
