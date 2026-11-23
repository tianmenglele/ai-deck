using System.Text.Json.Serialization;

namespace AICollabHub.Models;

public sealed class DetectorRegistryFile
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("detectors")] public List<DetectorSpec> Detectors { get; set; } = new();
}

public sealed class DetectorSpec
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "local_agent";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("probe_paths")] public List<string> ProbePaths { get; set; } = new();
    [JsonPropertyName("probe_commands")] public List<string> ProbeCommands { get; set; } = new();
    [JsonPropertyName("probe_ports")] public List<int> ProbePorts { get; set; } = new();
    [JsonPropertyName("probe_extension_ids")] public List<string> ProbeExtensionIds { get; set; } = new();
    [JsonPropertyName("probe_process_names")] public List<string> ProbeProcessNames { get; set; } = new();
    [JsonPropertyName("probe_display_names")] public List<string> ProbeDisplayNames { get; set; } = new();
    [JsonPropertyName("probe_msix_prefixes")] public List<string> ProbeMsixPrefixes { get; set; } = new();
    [JsonPropertyName("json_nodes_file")] public string? JsonNodesFile { get; set; }
    [JsonPropertyName("default_adapter")] public string DefaultAdapter { get; set; } = "markdown_anchor";
    [JsonPropertyName("default_runner_mode")] public string DefaultRunnerMode { get; set; } = "none";
    [JsonPropertyName("default_runner_cmd")] public string DefaultRunnerCmd { get; set; } = "";
    [JsonPropertyName("default_gui_exe")] public string DefaultGuiExe { get; set; } = "";
    [JsonPropertyName("default_permissions")] public AgentPermissions DefaultPermissions { get; set; } = new();
    [JsonPropertyName("default_tags")] public Dictionary<string, int> DefaultTags { get; set; } = new();
}

public sealed class AgentPermissions
{
    [JsonPropertyName("allow_direct_source_write")] public bool AllowDirectSourceWrite { get; set; }
    [JsonPropertyName("allow_multi_dir")] public bool AllowMultiDir { get; set; }
    [JsonPropertyName("allow_shell_build")] public bool AllowShellBuild { get; set; }
    [JsonPropertyName("has_preview_mcp")] public bool HasPreviewMcp { get; set; }

    public AgentPermissions Clone() => new()
    {
        AllowDirectSourceWrite = AllowDirectSourceWrite,
        AllowMultiDir = AllowMultiDir,
        AllowShellBuild = AllowShellBuild,
        HasPreviewMcp = HasPreviewMcp
    };
}

public sealed class TagRegistryFile
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("tags")] public List<CapabilityTagSpec> Tags { get; set; } = new();
}

public sealed class CapabilityTagSpec
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "#38BDF8";
}

public sealed class AdapterRegistryFile
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("adapters")] public List<AdapterSpec> Adapters { get; set; } = new();
}

public sealed class AdapterSpec
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("writes_agents_skills_json")] public bool WritesAgentsSkillsJson { get; set; }
    [JsonPropertyName("updates_agents_md_anchor")] public bool UpdatesAgentsMdAnchor { get; set; }
    [JsonPropertyName("writes_claude_md_pointer")] public bool WritesClaudeMdPointer { get; set; }
    [JsonPropertyName("mirrors_to_bridge_dir")] public bool MirrorsToBridgeDir { get; set; }
}

public sealed class WorkflowRegistryFile
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("workflows")] public List<WorkflowTemplate> Workflows { get; set; } = new();
}

public sealed class WorkflowTemplate
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("stages")] public List<WorkflowStageSpec> Stages { get; set; } = new();
}

public sealed class WorkflowStageSpec
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("required_tag")] public string RequiredTag { get; set; } = "";
    [JsonPropertyName("require_direct_write")] public bool RequireDirectWrite { get; set; }
    [JsonPropertyName("require_preview_mcp")] public bool RequirePreviewMcp { get; set; }
    [JsonPropertyName("deliverable")] public string Deliverable { get; set; } = "";
}

public sealed class PresetRegistryFile
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("presets")] public List<ProjectPresetSpec> Presets { get; set; } = new();
}

public sealed class ProjectPresetSpec
{
    [JsonPropertyName("id")] public string Id { get; set; } = "general_code";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("create_gdignore")] public bool CreateGdignore { get; set; }
    [JsonPropertyName("snapshot_extensions")] public List<string> SnapshotExtensions { get; set; } = new();
    [JsonPropertyName("excluded_dirs")] public List<string> ExcludedDirs { get; set; } = new();
}

// ---------------- 用户本机持久化数据 (%LOCALAPPDATA%/AICollabHub) ----------------

public sealed class UserHubConfig
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("active_project_id")] public string ActiveProjectId { get; set; } = "";
    [JsonPropertyName("workflow_engine_enabled")] public bool WorkflowEngineEnabled { get; set; } = true;
    [JsonPropertyName("workflow_only_when_app_running")] public bool WorkflowOnlyWhenAppRunning { get; set; } = true;
    [JsonPropertyName("enable_master_verify_rework_loop")] public bool EnableMasterVerifyReworkLoop { get; set; } = true;
    [JsonPropertyName("max_rework_rounds")] public int MaxReworkRounds { get; set; } = 2;
    [JsonPropertyName("enable_network_auto_retry")] public bool EnableNetworkAutoRetry { get; set; } = true;
    [JsonPropertyName("max_network_retries")] public int MaxNetworkRetries { get; set; } = 3;
    [JsonPropertyName("wait_for_local_network_recovery")] public bool WaitForLocalNetworkRecovery { get; set; } = true;
    [JsonPropertyName("local_network_wait_max_seconds")] public int LocalNetworkWaitMaxSeconds { get; set; } = 45;
    [JsonPropertyName("enable_agent_failover_on_outage")] public bool EnableAgentFailoverOnOutage { get; set; } = true;
    [JsonPropertyName("auto_resume_interrupted_chain_on_net_recover")] public bool AutoResumeInterruptedChainOnNetRecover { get; set; } = true;
    [JsonPropertyName("single_attempt_timeout_seconds")] public int SingleAttemptTimeoutSeconds { get; set; } = 180;
    /// <summary>自动信任已接入协同中心的工程目录，并在后台 CLI 调用时自动传递 --trust / -f / --approve-mcps，免除交互确认拦截。</summary>
    [JsonPropertyName("auto_trust_managed_workspaces")] public bool AutoTrustManagedWorkspaces { get; set; } = true;
    // auto_then_ask (先后台自动拆弹与多通道重试，仍卡住则提供选项让用户决定) | auto_self_heal (全自动重试+换AI+交回主脑) | ask_user_immediately (卡住立即让用户选择)
    [JsonPropertyName("stall_recovery_policy")] public string StallRecoveryPolicy { get; set; } = "auto_then_ask";
    /// <summary>优先走官方 CLI（Cursor Agent / Codex / Grok / Claude），多次失败后再切桌面 UIA。</summary>
    [JsonPropertyName("prefer_official_cli_first")] public bool PreferOfficialCliFirst { get; set; } = true;
    /// <summary>需要桌面可见会话时，投递前确保客户端窗口已启动且可枚举（含跨桌面探测）。</summary>
    [JsonPropertyName("require_desktop_visible_session")] public bool RequireDesktopVisibleSession { get; set; } = true;
    /// <summary>
    /// 派单/后台探测严禁把其他 AI 窗口切回前台抢焦点（最小化仅 SW_SHOWNOACTIVATE + HWND_BOTTOM；
    /// 短瞬粘贴须等键鼠静默空隙并立即还原剪贴板与前台）。旧配置缺省该字段时默认为 true。
    /// </summary>
    [JsonPropertyName("strict_no_foreground_steal_on_dispatch")]
    public bool StrictNoForegroundStealOnDispatch { get; set; } = true;
    /// <summary>
    /// 是否允许「已配置桌面客户端通道」的 AI（Cursor / Grok Bot 等）在桌面直连失败后静默降级到纯后台无头 CLI / Stage3。
    /// 默认 false：桌面 AI 必须走可见会话；失败返回清晰诊断，禁止 cursor-agent / node --print 等用户看不见的后台跑任务。
    /// 旧配置缺省该字段时保持 false。
    /// </summary>
    [JsonPropertyName("allow_headless_cli_fallback_for_desktop_agents")]
    public bool AllowHeadlessCliFallbackForDesktopAgents { get; set; } = false;
    /// <summary>启用协同提示音效（总开关）。</summary>
    [JsonPropertyName("enable_sound_effects")] public bool EnableSoundEffects { get; set; } = true;
    /// <summary>音效轻量化：仅保留任务完成/验收通过提示音，跳过派单飞行与状态切换过程音。</summary>
    [JsonPropertyName("sound_lightweight_mode")] public bool SoundLightweightMode { get; set; } = false;
    /// <summary>软件自检更新源：GitHub / Gitee 地址、SSH、owner/repo，或自定义版本 JSON。空则尝试读取本地 Git origin。</summary>
    [JsonPropertyName("app_update_repo_url")] public string AppUpdateRepoUrl { get; set; } = "";
    /// <summary>启动约 4 秒后后台静默检查一次软件更新。旧配置缺省该字段时为 true。</summary>
    [JsonPropertyName("auto_check_app_update_on_startup")] public bool AutoCheckAppUpdateOnStartup { get; set; } = true;
    [JsonPropertyName("auto_sync_cloud_recommendations")] public bool AutoSyncCloudRecommendations { get; set; } = true;
    [JsonPropertyName("last_cloud_rec_sync_time")] public string LastCloudRecSyncTime { get; set; } = "";
    [JsonPropertyName("cloud_rec_source_label")] public string CloudRecSourceLabel { get; set; } = "OpenRouter & LiveBench 实时基准库";
    [JsonPropertyName("floating_theme_style")] public string FloatingThemeStyle { get; set; } = "dark_space";
    [JsonPropertyName("run_at_startup")] public bool RunAtStartup { get; set; } = false;
    [JsonPropertyName("start_directly_into_floating_mode")] public bool StartDirectlyIntoFloatingMode { get; set; } = true;
    /// <summary>点击主界面关闭按钮 (×) 时的行为：ask / minimize_to_tray / exit。</summary>
    [JsonPropertyName("close_button_action")] public string CloseButtonAction { get; set; } = "ask";
    /// <summary>旧字段：与 CloseButtonAction=minimize_to_tray 同步；默认 false，保持向后兼容。</summary>
    [JsonPropertyName("close_main_window_to_floating")] public bool CloseMainWindowToFloating { get; set; } = false;
    [JsonPropertyName("floating_window_enabled")] public bool FloatingWindowEnabled { get; set; } = true;
    /// <summary>新手引导「开启桌面置顶悬浮窗」读写入口，与 FloatingWindowEnabled 同一开关，不另存一份以免旧配置分叉。</summary>
    [JsonIgnore]
    public bool FloatingWindowVisible
    {
        get => FloatingWindowEnabled;
        set => FloatingWindowEnabled = value;
    }
    [JsonPropertyName("floating_window_topmost")] public bool FloatingWindowTopmost { get; set; } = true;
    [JsonPropertyName("floating_window_collapsed")] public bool FloatingWindowCollapsed { get; set; } = false;
    [JsonPropertyName("floating_layout_mode")] public string FloatingLayoutMode { get; set; } = "auto";
    [JsonPropertyName("floating_window_translucent")] public bool FloatingWindowTranslucent { get; set; } = false;
    [JsonPropertyName("floating_opacity_percent")] public int FloatingOpacityPercent { get; set; } = 96;
    [JsonPropertyName("floating_refresh_interval_ms")] public int FloatingRefreshIntervalMs { get; set; } = 1000;
    /// <summary>已废弃：悬浮窗形态绝对锁定，状态变化不得自动展开。保留字段仅兼容旧配置落盘。</summary>
    [JsonPropertyName("floating_auto_expand_on_working")] public bool FloatingAutoExpandOnWorking { get; set; } = false;
    /// <summary>已废弃：悬浮窗形态绝对锁定，任务完成不得自动收起。保留字段仅兼容旧配置落盘。</summary>
    [JsonPropertyName("floating_auto_collapse_on_done")] public bool FloatingAutoCollapseOnDone { get; set; } = false;
    [JsonPropertyName("floating_show_workflows_strip")] public bool FloatingShowWorkflowsStrip { get; set; } = true;
    [JsonPropertyName("floating_window_left")] public double? FloatingWindowLeft { get; set; }
    [JsonPropertyName("floating_window_top")] public double? FloatingWindowTop { get; set; }
    /// <summary>展开态（Full/Compact）自定义宽度（340~860，默认 420）。</summary>
    [JsonPropertyName("floating_custom_width")] public double? FloatingCustomWidth { get; set; }
    /// <summary>极简行模式（Ultra）自定义宽度（238~420，默认 268）；与 Full 宽度隔离，避免共用把 Ultra 卡宽。</summary>
    [JsonPropertyName("floating_ultra_custom_width")] public double? FloatingUltraCustomWidth { get; set; }
    /// <summary>展开态手动高度（165~760）；null 表示高度自适应内容。</summary>
    [JsonPropertyName("floating_custom_height")] public double? FloatingCustomHeight { get; set; }
    /// <summary>收起胶囊自定义宽度（145~680，默认按启用 AI 数动态；未拖拽时约 196）。</summary>
    [JsonPropertyName("floating_collapsed_custom_width")] public double? FloatingCollapsedCustomWidth { get; set; }
    [JsonPropertyName("global_master_planner_id")] public string GlobalMasterPlannerId { get; set; } = "auto";
    [JsonPropertyName("global_fallback_builder_id")] public string GlobalFallbackBuilderId { get; set; } = "";
    [JsonPropertyName("global_work_type_routes")] public Dictionary<string, WorkRouteRule> GlobalWorkTypeRoutes { get; set; } = new();
    [JsonPropertyName("prefer_native_models_when_tied")] public bool PreferNativeModelsWhenTied { get; set; } = false;
    [JsonPropertyName("active_team_profile_id")] public string ActiveTeamProfileId { get; set; } = "";
    [JsonPropertyName("team_profiles")] public List<TeamConfigProfile> TeamProfiles { get; set; } = new();
    [JsonPropertyName("projects")] public List<ProjectProfile> Projects { get; set; } = new();
    [JsonPropertyName("agents")] public List<AgentRosterEntry> Agents { get; set; } = new();
    /// <summary>已完成或主动跳过 3 步新手引导。旧配置缺省该字段时为 false。</summary>
    [JsonPropertyName("has_completed_onboarding")]
    public bool HasCompletedOnboarding { get; set; } = false;
}

public sealed class TeamAgentSnapshot
{
    [JsonPropertyName("agent_id")] public string AgentId { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("model_selection_mode")] public string ModelSelectionMode { get; set; } = "auto";
    [JsonPropertyName("preferred_effort_tier")] public string PreferredEffortTier { get; set; } = "auto";
    [JsonPropertyName("high_tier_model")] public string HighTierModel { get; set; } = "";
    [JsonPropertyName("medium_tier_model")] public string MediumTierModel { get; set; } = "";
    [JsonPropertyName("low_tier_model")] public string LowTierModel { get; set; } = "";
    [JsonPropertyName("active_preset_id")] public string ActivePresetId { get; set; } = "cloud_balanced";
    [JsonPropertyName("custom_work_note")] public string CustomWorkNote { get; set; } = "";
    [JsonPropertyName("runner_mode")] public string RunnerMode { get; set; } = "";
    [JsonPropertyName("runner_cmd")] public string RunnerCmd { get; set; } = "";
    [JsonPropertyName("launch_window_on_dispatch")] public bool? LaunchWindowOnDispatch { get; set; }
    [JsonPropertyName("primary_work_roles")] public List<string> PrimaryWorkRoles { get; set; } = new();
    [JsonPropertyName("permissions")] public AgentPermissions Permissions { get; set; } = new();
    [JsonPropertyName("runtime_params")] public AgentRuntimeParams RuntimeParams { get; set; } = new();
    [JsonPropertyName("model_profiles")] public List<ModelWorkProfile> ModelProfiles { get; set; } = new();
    [JsonPropertyName("tag_scores")] public Dictionary<string, int> TagScores { get; set; } = new();
}

public sealed class TeamConfigProfile
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("master_planner_id")] public string MasterPlannerId { get; set; } = "auto";
    [JsonPropertyName("fallback_builder_id")] public string FallbackBuilderId { get; set; } = "";
    [JsonPropertyName("max_rework_rounds")] public int MaxReworkRounds { get; set; } = 2;
    [JsonPropertyName("prefer_native_models_when_tied")] public bool PreferNativeModelsWhenTied { get; set; } = false;
    [JsonPropertyName("work_type_routes")] public Dictionary<string, WorkRouteRule> WorkTypeRoutes { get; set; } = new();
    [JsonPropertyName("agent_snapshots")] public List<TeamAgentSnapshot> AgentSnapshots { get; set; } = new();
}

public sealed class ProjectProfile
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("name")] public string Name { get; set; } = "未命名项目";
    [JsonPropertyName("primary_root")] public string PrimaryRoot { get; set; } = "";
    [JsonPropertyName("companion_roots")] public List<string> CompanionRoots { get; set; } = new();
    [JsonPropertyName("preset_id")] public string PresetId { get; set; } = "general_code";
    [JsonPropertyName("rules_rel_dir")] public string RulesRelDir { get; set; } = ".cursor/rules";
    [JsonPropertyName("skills_rel_dir")] public string SkillsRelDir { get; set; } = ".cursor/skills";
    [JsonPropertyName("memory_rel_dir")] public string MemoryRelDir { get; set; } = ".cursor/memory";
    [JsonPropertyName("collab_rel_dir")] public string CollabRelDir { get; set; } = "work/collab";
    [JsonPropertyName("enabled_hooks")] public List<string> EnabledHooks { get; set; } = new();
    // 键: workflowId + "::" + stageId，值: "auto" 或指定 agentId
    [JsonPropertyName("stage_overrides")] public Dictionary<string, string> StageOverrides { get; set; } = new();
    [JsonPropertyName("work_type_routes")] public Dictionary<string, WorkRouteRule> WorkTypeRoutes { get; set; } = new();
    // 主脑 / 总分析拆解 AI ("auto" 自动选综合分析力最高的 AI，或手动指定 agentId)
    [JsonPropertyName("master_planner_id")] public string MasterPlannerId { get; set; } = "auto";
    [JsonPropertyName("fallback_builder_id")] public string FallbackBuilderId { get; set; } = "";
}

public sealed class AgentRosterEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("detector_id")] public string? DetectorId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "local_agent";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("detected")] public bool Detected { get; set; }
    [JsonPropertyName("evidence")] public string Evidence { get; set; } = "";
    [JsonPropertyName("adapter_id")] public string AdapterId { get; set; } = "markdown_anchor";
    [JsonPropertyName("runner_mode")] public string RunnerMode { get; set; } = "none";
    [JsonPropertyName("runner_cmd")] public string RunnerCmd { get; set; } = "";
    [JsonPropertyName("gui_exe_path")] public string GuiExePath { get; set; } = "";
    [JsonPropertyName("launch_window_on_dispatch")] public bool LaunchWindowOnDispatch { get; set; } = false;
    [JsonPropertyName("bridge_local_dir")] public string BridgeLocalDir { get; set; } = "";
    [JsonPropertyName("permissions")] public AgentPermissions Permissions { get; set; } = new();
    [JsonPropertyName("tag_scores")] public Dictionary<string, int> TagScores { get; set; } = new();
    [JsonPropertyName("primary_work_roles")] public List<string> PrimaryWorkRoles { get; set; } = new();
    [JsonPropertyName("custom_work_note")] public string CustomWorkNote { get; set; } = "";
    [JsonPropertyName("model_profiles")] public List<ModelWorkProfile> ModelProfiles { get; set; } = new();
    [JsonPropertyName("model_selection_mode")] public string ModelSelectionMode { get; set; } = "auto"; // "auto" | "manual"
    [JsonPropertyName("preferred_effort_tier")] public string PreferredEffortTier { get; set; } = "auto"; // "auto" | "high" | "medium" | "low"
    [JsonPropertyName("high_tier_model")] public string HighTierModel { get; set; } = "";
    [JsonPropertyName("medium_tier_model")] public string MediumTierModel { get; set; } = "";
    [JsonPropertyName("low_tier_model")] public string LowTierModel { get; set; } = "";
    [JsonPropertyName("available_models")] public List<string> AvailableModels { get; set; } = new();
    [JsonPropertyName("recommended_summary")] public string RecommendedSummary { get; set; } = "";
    [JsonPropertyName("last_synced_at")] public string LastSyncedAt { get; set; } = "";
    [JsonPropertyName("runtime_params")] public AgentRuntimeParams RuntimeParams { get; set; } = new();
    [JsonPropertyName("active_preset_id")] public string ActivePresetId { get; set; } = "cloud_balanced";
    [JsonPropertyName("custom_presets")] public List<AgentConfigPreset> CustomPresets { get; set; } = new();
}

public sealed class ModelWorkProfile
{
    [JsonPropertyName("model_name")] public string ModelName { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("ownership_kind")] public string OwnershipKind { get; set; } = ""; // "" | "native" | "third_party"
    [JsonPropertyName("vendor_name")] public string VendorName { get; set; } = "";
    [JsonPropertyName("usage_policy")] public string UsagePolicy { get; set; } = "primary_default";
    [JsonPropertyName("task_difficulty_rule")] public string TaskDifficultyRule { get; set; } = "any";
    [JsonPropertyName("primary_roles")] public List<string> PrimaryRoles { get; set; } = new();
    [JsonPropertyName("capability_scores")] public Dictionary<string, int> CapabilityScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("speed_tier")] public string SpeedTier { get; set; } = "medium";
    [JsonPropertyName("recommended_effort")] public string RecommendedEffort { get; set; } = "medium";
    [JsonPropertyName("specialty_note")] public string SpecialtyNote { get; set; } = "";
    [JsonPropertyName("custom_temperature")] public double? CustomTemperature { get; set; }
    [JsonPropertyName("context_window_tokens")] public int ContextWindowTokens { get; set; }
    [JsonPropertyName("max_output_tokens")] public int MaxOutputTokens { get; set; }

    public int GetCapabilityScore(string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId)) return 0;
        if (CapabilityScores != null && CapabilityScores.TryGetValue(roleId, out var sc) && sc > 0)
            return Math.Clamp(sc, 1, 5);
        if (PrimaryRoles != null && PrimaryRoles.Contains(roleId, StringComparer.OrdinalIgnoreCase))
            return 3;
        return 0;
    }

    public void SetCapabilityScore(string roleId, int score)
    {
        if (string.IsNullOrWhiteSpace(roleId)) return;
        PrimaryRoles ??= new List<string>();
        CapabilityScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (score <= 0)
        {
            CapabilityScores.Remove(roleId);
            PrimaryRoles.RemoveAll(r => string.Equals(r, roleId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            CapabilityScores[roleId] = Math.Clamp(score, 1, 5);
            if (!PrimaryRoles.Contains(roleId, StringComparer.OrdinalIgnoreCase))
                PrimaryRoles.Add(roleId);
        }
    }

    public ModelWorkProfile Clone() => new()
    {
        ModelName = ModelName,
        Enabled = Enabled,
        OwnershipKind = OwnershipKind ?? "",
        VendorName = VendorName ?? "",
        UsagePolicy = string.IsNullOrWhiteSpace(UsagePolicy) ? "primary_default" : UsagePolicy,
        TaskDifficultyRule = string.IsNullOrWhiteSpace(TaskDifficultyRule) ? "any" : TaskDifficultyRule,
        PrimaryRoles = new List<string>(PrimaryRoles ?? new List<string>()),
        CapabilityScores = new Dictionary<string, int>(CapabilityScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase),
        SpeedTier = SpeedTier,
        RecommendedEffort = RecommendedEffort,
        SpecialtyNote = SpecialtyNote,
        CustomTemperature = CustomTemperature,
        ContextWindowTokens = ContextWindowTokens,
        MaxOutputTokens = MaxOutputTokens
    };
}

public sealed class AgentRuntimeParams
{
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.3;
    [JsonPropertyName("reasoning_effort")] public string ReasoningEffort { get; set; } = "auto"; // auto | low | medium | high | xhigh
    [JsonPropertyName("context_budget")] public string ContextBudget { get; set; } = "auto"; // auto | compact | standard | max
    [JsonPropertyName("max_output_tokens")] public int MaxOutputTokens { get; set; } = 0; // 0 = 自动不限
    [JsonPropertyName("timeout_seconds")] public int TimeoutSeconds { get; set; } = 0; // 0 = 跟随全局默认
    [JsonPropertyName("max_retries")] public int MaxRetries { get; set; } = -1; // -1 = 跟随全局默认
    [JsonPropertyName("extra_runner_args")] public string ExtraRunnerArgs { get; set; } = "";

    public AgentRuntimeParams Clone() => new()
    {
        Temperature = Temperature,
        ReasoningEffort = string.IsNullOrWhiteSpace(ReasoningEffort) ? "auto" : ReasoningEffort,
        ContextBudget = string.IsNullOrWhiteSpace(ContextBudget) ? "auto" : ContextBudget,
        MaxOutputTokens = MaxOutputTokens,
        TimeoutSeconds = TimeoutSeconds,
        MaxRetries = MaxRetries,
        ExtraRunnerArgs = ExtraRunnerArgs ?? ""
    };
}

public sealed class AgentConfigPreset
{
    [JsonPropertyName("preset_id")] public string PresetId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("source_kind")] public string SourceKind { get; set; } = "cloud"; // cloud | user
    [JsonPropertyName("category_badge")] public string CategoryBadge { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("source_basis")] public string SourceBasis { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("preferred_effort_tier")] public string PreferredEffortTier { get; set; } = "auto";
    [JsonPropertyName("custom_work_note")] public string CustomWorkNote { get; set; } = "";
    [JsonPropertyName("model_profiles")] public List<ModelWorkProfile> ModelProfiles { get; set; } = new();
    [JsonPropertyName("permissions")] public AgentPermissions Permissions { get; set; } = new();
    [JsonPropertyName("tag_scores")] public Dictionary<string, int> TagScores { get; set; } = new();
    [JsonPropertyName("runtime_params")] public AgentRuntimeParams RuntimeParams { get; set; } = new();
    [JsonPropertyName("launch_window_on_dispatch")] public bool LaunchWindowOnDispatch { get; set; } = false;
    [JsonPropertyName("runner_mode")] public string RunnerMode { get; set; } = "";
    [JsonPropertyName("runner_cmd")] public string RunnerCmd { get; set; } = "";

    public AgentConfigPreset Clone() => new()
    {
        PresetId = PresetId,
        Name = Name,
        SourceKind = SourceKind,
        CategoryBadge = CategoryBadge,
        Description = Description,
        SourceBasis = SourceBasis,
        UpdatedAt = UpdatedAt,
        PreferredEffortTier = PreferredEffortTier,
        CustomWorkNote = CustomWorkNote,
        ModelProfiles = (ModelProfiles ?? new List<ModelWorkProfile>()).Select(p => p.Clone()).ToList(),
        Permissions = (Permissions ?? new AgentPermissions()).Clone(),
        TagScores = new Dictionary<string, int>(TagScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase),
        RuntimeParams = (RuntimeParams ?? new AgentRuntimeParams()).Clone(),
        LaunchWindowOnDispatch = LaunchWindowOnDispatch,
        RunnerMode = RunnerMode ?? "",
        RunnerCmd = RunnerCmd ?? ""
    };
}

public sealed class WorkRouteRule
{
    [JsonPropertyName("work_type_id")] public string WorkTypeId { get; set; } = "";
    [JsonPropertyName("target_agent_id")] public string TargetAgentId { get; set; } = "auto";
    [JsonPropertyName("target_model_name")] public string TargetModelName { get; set; } = "auto";
    [JsonPropertyName("target_effort_tier")] public string TargetEffortTier { get; set; } = "auto";
    [JsonPropertyName("custom_keywords")] public List<string> CustomKeywords { get; set; } = new();
}

public static class WorkSpecialtyCatalog
{
    public static IReadOnlyList<(string Id, string Label, string Icon, string Tooltip)> OrderedItems { get; } = new List<(string, string, string, string)>
    {
        ("brain_analysis",   "总分析与架构",   "🧠", "全局需求拆解、跨模块架构设计、任务统筹规划"),
        ("fast_execution",   "代码开发实装",   "💻", "编写与修改业务代码、逻辑脚本 (.gd/.cs/.py/.java) 及工程落地"),
        ("ui_shader_vfx",    "UI界面与特效",   "✨", "界面布局排版 (.tscn/.xaml)、Shader 着色器材质与粒子视觉特效"),
        ("deep_debug",       "排障与代码审查", "🔍", "疑难 Bug 根因排查、报错崩溃修复、代码审查与编译结果验收复核"),
        ("data_config",      "配表与数值设计", "📊", "数值公式曲线推演、JSON/CSV/Luban 配表数据与平衡性配置"),
        ("build_testing",    "构建打包与测试", "🛠️", "编译发布包 (.exe/.apk)、自动化测试脚本编写与版本交付自检"),
        ("text_writing",     "文案剧情策划",   "📝", "世界观剧情台词、系统玩法策划案、提示词撰写与多语言润色"),
        ("image_generation", "图片与美术贴图", "🎨", "概念原画、UI 图标、材质纹理贴图、立绘插画生成与视觉设计"),
        ("video_animation",  "视频与动态分镜", "🎬", "宣传片/CG 视频生成、动态分镜脚本、镜头运镜与序列帧动画设计"),
        ("audio_sfx_bgm",    "音效配乐与语音", "🎵", "游戏打击/环境音效 (SFX)、背景音乐 (BGM)、角色配音 (TTS) 与音频处理"),
        ("model_3d_motion",  "3D模型与动作",   "🧊", "3D 网格建模脚本 (Blender/glTF)、骨骼蒙皮权重、动作轨与动画状态机"),
        ("visual_accept",    "画面实看验收",   "🖥️", "调用引擎或浏览器预览 MCP 截取真实运行画面进行视觉终验把关")
    };

    public static IReadOnlyDictionary<string, string> Items { get; } =
        OrderedItems.ToDictionary(x => x.Id, x => x.Label, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Icons { get; } =
        OrderedItems.ToDictionary(x => x.Id, x => x.Icon, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Tooltips { get; } =
        OrderedItems.ToDictionary(x => x.Id, x => x.Tooltip, StringComparer.OrdinalIgnoreCase);

    // 仅对同属“代码工程开发”的新增细分子类提供向 fast_execution 的平滑兼容（当没有任何已启用 AI 显式勾选该细分子类时启用）；图片/视频/音效/审查验收等独立能力严禁跨界回退
    public static IReadOnlyDictionary<string, string> EngineeringParentFallback { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ui_shader_vfx"] = "fast_execution",
            ["data_config"] = "fast_execution",
            ["build_testing"] = "fast_execution",
            ["model_3d_motion"] = "fast_execution"
        };
}

public static class ModelRuleCatalog
{
    public static IReadOnlyList<(string Id, string Label, string Description)> UsagePolicies { get; } = new List<(string, string, string)>
    {
        ("primary_default",       "主力优先调用",   "匹配该模型能力时作为日常主力优先派发"),
        ("complex_or_rework_only","仅复杂攻坚/重修", "仅在高难度复杂任务或主脑打回重修轮次调用，保护旗舰/第三方高级额度"),
        ("light_fast_first",      "轻量速办优先",   "低/中难度常规任务优先抢单，响应极快且节省主力额度"),
        ("strict_specialty_only", "仅强项专精命中", "仅当任务命中该模型 ≥4★ 强项专精能力时调用，不接泛化杂活"),
        ("fallback_only",         "仅作替补兜底",   "平时待命，仅当顺位主力模型不匹配或受阻时兜底接力")
    };

    public static IReadOnlyList<(string Id, string Label, string Description)> DifficultyRules { get; } = new List<(string, string, string)>
    {
        ("any",        "不限难度",     "低、中、高全部难度任务均可接单"),
        ("high_only",  "仅高难度攻坚", "仅允许高难度或重修排障任务调用"),
        ("medium_up",  "中/高难度",    "适合标准开发与复杂任务，跳过轻量杂活"),
        ("low_medium", "低/中日常任务", "适合轻量速改与标准实操，复杂攻坚留给主力")
    };

    public static string StrengthLevelText(int level) => level switch
    {
        1 => "1·基础",
        2 => "2·辅助",
        3 => "3·熟练",
        4 => "4·强项",
        5 => "5·王牌",
        _ => "未开启"
    };

    public static string ShortStrengthWord(int level) => level switch
    {
        1 => "基础",
        2 => "辅助",
        3 => "熟练",
        4 => "强项",
        5 => "王牌",
        _ => "关闭"
    };

    public static string ShortUsagePolicyLabel(string? policyId) => (policyId ?? "").Trim().ToLowerInvariant() switch
    {
        "complex_or_rework_only" => "攻坚/重修",
        "light_fast_first" => "轻量优先",
        "strict_specialty_only" => "专精直击",
        "fallback_only" => "替补兜底",
        _ => "主力优先"
    };

    public static string ShortDifficultyLabel(string? diffId) => (diffId ?? "").Trim().ToLowerInvariant() switch
    {
        "high_only" => "仅高难",
        "medium_up" => "中高难",
        "low_medium" => "轻中量",
        _ => "不限难度"
    };
}

public sealed class HeadlessDispatchOutcome
{
    public bool Success { get; set; }
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string MasterPlannerName { get; set; } = "";
    public string SelectedModel { get; set; } = "";
    public string SelectedEffortTier { get; set; } = "high"; // high | medium | low
    public string SelectedTierLabel { get; set; } = "";
    public int RoundsExecuted { get; set; } = 1;
    public int MaxRounds { get; set; } = 3;
    public bool VerificationPassed { get; set; } = true;
    public string VerificationSummary { get; set; } = "";
    public string ChangedFilesSummary { get; set; } = "";
    public int NetworkRetriesUsed { get; set; }
    public bool FailoverOccurred { get; set; }
    public string OriginalFailedAgentName { get; set; } = "";
    public string FailureKind { get; set; } = "none";
    public string CheckpointFilePath { get; set; } = "";
    public bool CanResumeFromCheckpoint { get; set; }
    public string SessionId { get; set; } = "";
    public string SessionAction { get; set; } = ""; // resumed | created
    public string ConfirmedGuiChatTitle { get; set; } = "";
    public bool AsyncStillWorkingInGui { get; set; }
    public string OutputInboxFile { get; set; } = "";
    public string OutputText { get; set; } = "";
    public string SummaryMessage { get; set; } = "";
}

public sealed class ExternalDialogSessionInfo
{
    public string SessionId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Title { get; set; } = "";
    public string ProjectDir { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public int TurnCount { get; set; }
    public string LastUserMessage { get; set; } = "";
    public string LastAssistantMessage { get; set; } = "";
}

public sealed class ShareableTemplateBundle
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("exported_at")] public string ExportedAt { get; set; } = "";
    [JsonPropertyName("tags")] public List<CapabilityTagSpec> Tags { get; set; } = new();
    [JsonPropertyName("workflows")] public List<WorkflowTemplate> Workflows { get; set; } = new();
    [JsonPropertyName("detectors")] public List<DetectorSpec> Detectors { get; set; } = new();
}

public sealed class StageDispatchResult
{
    public string StageId { get; set; } = "";
    public string StageName { get; set; } = "";
    public string AssignedAgentId { get; set; } = "";
    public string AssignedAgentName { get; set; } = "";
    public bool IsManualOverride { get; set; }
    public string Reason { get; set; } = "";
    public string GuardrailNote { get; set; } = "";
}

public sealed class TaskChainCheckpoint
{
    [JsonPropertyName("chain_id")] public string ChainId { get; set; } = Guid.NewGuid().ToString("N")[..10];
    // running | reconnecting | interrupted | completed
    [JsonPropertyName("status")] public string Status { get; set; } = "running";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("project_name")] public string ProjectName { get; set; } = "";
    [JsonPropertyName("project_root")] public string ProjectRoot { get; set; } = "";
    [JsonPropertyName("task_instruction")] public string TaskInstruction { get; set; } = "";
    [JsonPropertyName("master_planner_name")] public string MasterPlannerName { get; set; } = "";
    [JsonPropertyName("target_agent_id")] public string TargetAgentId { get; set; } = "";
    [JsonPropertyName("target_agent_name")] public string TargetAgentName { get; set; } = "";
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
    [JsonPropertyName("session_mode")] public string SessionMode { get; set; } = "auto";
    [JsonPropertyName("selected_model")] public string SelectedModel { get; set; } = "";
    [JsonPropertyName("selected_effort_tier")] public string SelectedEffortTier { get; set; } = "high";
    [JsonPropertyName("current_round")] public int CurrentRound { get; set; } = 1;
    [JsonPropertyName("max_rounds")] public int MaxRounds { get; set; } = 3;
    [JsonPropertyName("network_retries_used")] public int NetworkRetriesUsed { get; set; }
    [JsonPropertyName("max_network_retries")] public int MaxNetworkRetries { get; set; } = 3;
    [JsonPropertyName("last_failure_kind")] public string LastFailureKind { get; set; } = "none";
    [JsonPropertyName("last_failure_label")] public string LastFailureLabel { get; set; } = "";
    [JsonPropertyName("last_error_detail")] public string LastErrorDetail { get; set; } = "";
    [JsonPropertyName("rework_feedback")] public string ReworkFeedback { get; set; } = "";
    [JsonPropertyName("prev_round_output")] public string PrevRoundOutput { get; set; } = "";
    [JsonPropertyName("failover_tried_agent_ids")] public List<string> FailoverTriedAgentIds { get; set; } = new();
}
