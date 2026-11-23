using AICollabHub.Models;

namespace AICollabHub.Services;

public sealed class RoutingService
{
    public sealed class WorkRouteResolution
    {
        public string WorkTypeId { get; init; } = "";
        public string WorkTypeLabel { get; init; } = "";
        public AgentRosterEntry? TargetAgent { get; init; }
        public string TargetModelName { get; init; } = "";
        public string TargetEffortTier { get; init; } = "medium";
        public bool IsProjectOverride { get; init; }
        public string Reason { get; init; } = "";
    }

    /// <summary>总览面板用：某工作类别下「已启用且能力&gt;0★」的全部 AI 候选（含备选，不按派单规则过滤掉攻坚模型）。</summary>
    public sealed class WorkRouteOverviewCandidate
    {
        public AgentRosterEntry Agent { get; init; } = null!;
        public int Rank { get; init; }
        public bool IsPrimary { get; init; }
        public bool IsManualOverride { get; init; }
        public int BestCapabilityStars { get; init; }
        public int RouteScore { get; init; }
        public string RepresentativeModelName { get; init; } = "";
        public string EffortTier { get; init; } = "medium";
        public ModelWorkProfile? RepresentativeProfile { get; init; }
        public string RankReason { get; init; } = "";
        public List<WorkRouteOverviewModelSlot> CapableModels { get; init; } = new();
    }

    public sealed class WorkRouteOverviewModelSlot
    {
        public string ModelName { get; init; } = "";
        public int Stars { get; init; }
        public string UsagePolicy { get; init; } = "primary_default";
        public string RecommendedEffort { get; init; } = "medium";
        public string OwnershipKind { get; init; } = "";
        public int RouteScore { get; init; }
        public ModelWorkProfile? Profile { get; init; }
    }

    private static readonly Dictionary<string, string[]> WorkTypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brain_analysis"]   = new[] { "统筹", "总分析", "拆解任务", "架构分析", "规划", "brain analysis", "orchestrate" },
        ["fast_execution"]   = new[] { "直接修改", "直接落地", "改工程", "实装", "写代码", "修复代码", "实现", "fast execution", "implement" },
        ["ui_shader_vfx"]    = new[] { "shader", ".gdshader", "着色器", "粒子特效", "vfx", "ui布局", "界面排版", "材质球", "后处理", "光影特效" },
        ["deep_debug"]       = new[] { "深度排障", "排查", "故障", "审计", "审查", "验收", "复核", "核验", "走查", "检视", "旁路复核", "代码审查", "debug", "bug", "review", "根因", "回归风险" },
        ["data_config"]      = new[] { "配表", "数值表", "数值曲线", "掉落表", "csv", "excel", "luban", "配置表", "平衡性数值" },
        ["build_testing"]    = new[] { "打包交付", "构建发布", "导出apk", "导出exe", "跑自检", "单元测试", "编译打包", "dotnet publish" },
        ["text_writing"]     = new[] { "文案", "写作", "文本创作", "策划案", "剧情", "润色", "台词", "text writing", "copywriting" },
        ["image_generation"] = new[] { "图片生成", "图像生成", "生成图片", "生成贴图", "概念图", "概念设计", "原画", "立绘", "图标生成", "生图", "插画", "纹理贴图", "image generation", "concept art" },
        ["video_animation"]  = new[] { "视频生成", "生成视频", "制作视频", "短视频", "宣传片", "分镜", "运镜", "序列帧动画", "cg动画", "sora", "veo", "kling", "video generation", ".mp4", ".webm" },
        ["audio_sfx_bgm"]    = new[] { "音效", "配乐", "背景音乐", "bgm", "sfx", "配音", "语音合成", "tts", "环境音", "打击音", "采样率", ".ogg", ".wav", ".mp3", "sound effect", "audio generation" },
        ["model_3d_motion"]  = new[] { "3d模型", "3d建模", "骨骼", "蒙皮", "权重", "动作轨", "动画重定向", "绑骨", "blender", ".glb", ".gltf", ".fbx", "skeleton3d", "animationtree" },
        ["visual_accept"]    = new[] { "画面验收", "实看", "截图验收", "mcp验收", "视觉验收", "visual accept", "godot截图" }
    };

    /// <summary>
    /// 严格检查某 AI 是否被用户勾选允许承担指定类型的任务。
    /// 已启用模型存在非空 CapabilityScores 时以单模型星级为最高权威；否则兼容 PrimaryRoles / PrimaryWorkRoles / TagScores。
    /// 若用户在配置窗口取消勾选了某项职责（例如 Grok Bot 未勾选 deep_debug / fast_execution / audio_sfx_bgm），则返回 false 一票否决。
    /// </summary>
    public static bool IsAgentEligibleForWorkType(AgentRosterEntry? agent, string? workTypeId)
    {
        if (agent == null) return false;
        if (string.IsNullOrWhiteSpace(workTypeId)) return true;

        var enabledProfiles = (agent.ModelProfiles ?? new List<ModelWorkProfile>())
            .Where(p => p.Enabled)
            .ToList();

        // 任一启用模型对本类 GetCapabilityScore > 0（含仅 PrimaryRoles 的历史槽位）→ 具备资格
        if (enabledProfiles.Any(p => p.GetCapabilityScore(workTypeId) > 0))
            return true;

        // 已明确配置星级（CapabilityScores 非空）但本类为 0★ → 最高权威一票否决，忽略陈旧 PrimaryWorkRoles
        bool anyCapabilityScores = enabledProfiles.Any(HasExplicitCapabilityScores);
        if (anyCapabilityScores)
            return false;

        // 无 CapabilityScores：兼容托管/外部节点与仅有 PrimaryWorkRoles/TagScores 的历史数据
        bool hasAgentRoles = agent.PrimaryWorkRoles != null && agent.PrimaryWorkRoles.Count > 0;
        bool hasTagScores = agent.TagScores != null &&
                            agent.TagScores.Any(kv => kv.Value > 0 && WorkSpecialtyCatalog.Items.ContainsKey(kv.Key));
        bool hasModelPrimaryRoles = enabledProfiles.Any(p => p.PrimaryRoles != null && p.PrimaryRoles.Count > 0);

        if (hasAgentRoles && agent.PrimaryWorkRoles!.Contains(workTypeId, StringComparer.OrdinalIgnoreCase))
            return true;
        if (agent.TagScores != null && agent.TagScores.TryGetValue(workTypeId, out var ts) && ts > 0)
            return true;

        if (!hasAgentRoles && !hasTagScores && !hasModelPrimaryRoles)
            return true; // 完全空白自定义/通用节点兼容放行

        return false;
    }

    public static bool HasExplicitCapabilityScores(ModelWorkProfile? p) =>
        p != null &&
        p.CapabilityScores != null &&
        p.CapabilityScores.Any(kv => kv.Value > 0 && WorkSpecialtyCatalog.Items.ContainsKey(kv.Key));

    public static bool HasConfiguredCapabilities(ModelWorkProfile? p) =>
        HasExplicitCapabilityScores(p) ||
        (p != null && p.PrimaryRoles != null && p.PrimaryRoles.Count > 0);

    public static List<AgentRosterEntry> ResolveEligibleAgentsForWorkType(
        IEnumerable<AgentRosterEntry> pool,
        string? workTypeId,
        out bool usedParentFallback)
    {
        usedParentFallback = false;
        var list = pool.ToList();
        if (string.IsNullOrWhiteSpace(workTypeId)) return list;

        var direct = list.Where(a => IsAgentEligibleForWorkType(a, workTypeId)).ToList();
        if (direct.Count > 0) return direct;

        // 仅对同属“代码工程开发”的新增细分子类在无人显式勾选时平滑兼容到主力代码 (fast_execution)；图片/视频/音效/审查验收等独立能力严禁跨界回退
        if (WorkSpecialtyCatalog.EngineeringParentFallback.TryGetValue(workTypeId, out var parentType))
        {
            var fallback = list.Where(a => IsAgentEligibleForWorkType(a, parentType)).ToList();
            if (fallback.Count > 0)
            {
                usedParentFallback = true;
                return fallback;
            }
        }
        return direct;
    }

    public static bool IsAgentEligibleEffective(AgentRosterEntry? agent, string? workTypeId, IEnumerable<AgentRosterEntry> pool)
    {
        if (agent == null) return false;
        if (IsAgentEligibleForWorkType(agent, workTypeId)) return true;
        if (!string.IsNullOrWhiteSpace(workTypeId) &&
            WorkSpecialtyCatalog.EngineeringParentFallback.TryGetValue(workTypeId, out var parentType) &&
            !pool.Any(a => IsAgentEligibleForWorkType(a, workTypeId)))
        {
            return IsAgentEligibleForWorkType(agent, parentType);
        }
        return false;
    }

    /// <summary>
    /// 真正要产出多媒体资产的显式意图（生成/制作音效视频图片/3D 等），与业务文案里偶发出现「音效/视频」等词严格区分。
    /// </summary>
    public static bool HasExplicitMediaCreationCue(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;

        string[] exactCues =
        {
            "生成音效", "制作音效", "合成音效", "打击音效", "作曲", "编曲", "背景音乐",
            "生成bgm", "生成BGM", "生成音乐", "制作音乐", "配音", "语音合成", "tts", "TTS",
            "生成视频", "制作视频", "短视频", "宣传片", "分镜", "运镜",
            "生成图片", "图像生成", "生图", "概念图", "原画", "立绘", "图标生成",
            "3d建模", "3D建模", "绑骨", "蒙皮", "骨骼蒙皮", "动作重定向",
            "sound effect", "audio generation", "image generation", "video generation", "concept art"
        };
        if (exactCues.Any(c => TextContainsKeyword(t, c)))
            return true;

        // 「制作打击音效」「生成战斗背景音乐」等中间夹词的资产生成句式
        return System.Text.RegularExpressions.Regex.IsMatch(
            t,
            @"(生成|制作|合成|创作|编曲|作曲|绘制|导出).{0,16}(音效|配乐|音乐|bgm|sfx|配音|视频|短视频|宣传片|图片|贴图|立绘|原画|概念图|3d\s*模型|骨骼|蒙皮)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static bool IsGrokBotAgent(AgentRosterEntry? agent)
    {
        if (agent == null) return false;
        if (string.Equals(agent.Id, "grok_bot", StringComparison.OrdinalIgnoreCase)) return true;
        var name = agent.Name ?? "";
        return name.Contains("Grok Bot", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 任务在改/排查工程源码（代码后缀、scripts/、src/ 或明确开发指令），且不是新制作/导出美术或音频素材。
    /// 这类单禁止派给没有直改代码权限的 Grok Bot。
    /// </summary>
    public static bool IsEngineeringSourceEditTask(string? taskText)
    {
        var raw = (taskText ?? "").Trim();
        if (raw.Length == 0) return false;
        if (HasExplicitMediaCreationCue(raw)) return false;

        bool hasCodeExt = System.Text.RegularExpressions.Regex.IsMatch(
            raw,
            @"\.(cs|gd|ts|tsx|js|jsx|py|tscn|tres|gdshader|shader|xaml|java|cpp|h|rs|go)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool hasCodeDir =
            raw.Contains("scripts/", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("scripts\\", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("src/", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("src\\", StringComparison.OrdinalIgnoreCase);
        bool hasDevVerb =
            raw.Contains("直接修改", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("落地修改", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("直接落地", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("修改工程", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("改源码", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("改代码", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("实操修改", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("工程脚本", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("排查", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("排障", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("修复", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("重构", StringComparison.OrdinalIgnoreCase);

        if ((hasCodeExt || hasCodeDir) && hasDevVerb) return true;
        if (hasCodeExt && (raw.Contains("脚本", StringComparison.OrdinalIgnoreCase) ||
                           raw.Contains("源码", StringComparison.OrdinalIgnoreCase) ||
                           raw.Contains("不生效", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    public static string InferWorkTypeFromTask(string? taskText)
    {
        var rawText = (taskText ?? "").Trim();
        if (rawText.Length == 0) return "fast_execution";

        // 剥离文件路径中的中间文件夹目录名（如 scripts/05_地图环境与音效/paving_mesh_tune.gd），仅保留目标文件名与自然语言描述，防止因目录名含“音效/视频/模型”误判代码任务
        var strippedText = System.Text.RegularExpressions.Regex.Replace(
            rawText,
            @"(?:[A-Za-z]:[\\/]|[A-Za-z0-9_\u4e00-\u9fa5.\-]+[\\/])(?:[^\s""'`<>|:*?\r\n]+[\\/])+([^\s""'`<>|:*?\\/]+)",
            " $1 ");

        // 若任务显式指向着色器或纯源码文件且未要求生成多媒体资产文件，优先按目标工程文件后缀精准归类
        bool hasMediaExt = System.Text.RegularExpressions.Regex.IsMatch(rawText, @"\.(ogg|wav|mp3|mp4|webm|mov|png|jpg|jpeg|webp)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // 排查/修改脚本时，正文里的 BGM、音效、图片路径不算资产生成
        if (IsEngineeringSourceEditTask(rawText))
            hasMediaExt = false;
        bool hasShaderExt = System.Text.RegularExpressions.Regex.IsMatch(rawText, @"\.(gdshader|shader|hlsl|glsl)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool hasCodeExt = System.Text.RegularExpressions.Regex.IsMatch(rawText, @"\.(gd|cs|java|py|ts|tsx|js|cpp|h|rs|go|xaml|tscn|tres)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool hasCodePathCue = System.Text.RegularExpressions.Regex.IsMatch(
            rawText,
            @"(?:^|[\s""'`（(])(?:scripts|src)[\\/]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool hasExplicitMediaCreationCue = HasExplicitMediaCreationCue(strippedText);

        bool hasCodeStackCue =
            TextContainsKeyword(strippedText, "C#") ||
            TextContainsKeyword(strippedText, "WPF") ||
            TextContainsKeyword(strippedText, "GDScript") ||
            TextContainsKeyword(strippedText, "dotnet build") ||
            TextContainsKeyword(strippedText, "dotnet publish") ||
            strippedText.Contains("源码", StringComparison.OrdinalIgnoreCase);

        bool hasCodeEditVerbCue =
            strippedText.Contains("请直接落地修改", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("直接落地修改", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("直接落地改", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("落地修改", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("落地改", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("修改工程文件", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("实操修改", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("直接修改", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("直接改", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("改源码", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("改代码", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("修源码", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("排障修复", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("打回重修", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("重构", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("排查", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("修复", StringComparison.OrdinalIgnoreCase);

        bool hasExplicitCodeEditCue =
            hasCodeEditVerbCue ||
            hasCodeExt || hasShaderExt || hasCodeStackCue || hasCodePathCue ||
            System.Text.RegularExpressions.Regex.IsMatch(strippedText, @"\.(cs|xaml|gdshader|gd|tscn)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 明确写代码指令时，不因「反馈/图像/画面」字眼误归为纯 visual_accept
        if (!hasExplicitCodeEditCue &&
            WorkTypeKeywords.TryGetValue("visual_accept", out var vaKws) &&
            vaKws.Any(k => TextContainsKeyword(strippedText, k)))
            return "visual_accept";

        if (!hasMediaExt && hasShaderExt)
            return "ui_shader_vfx";

        // 代码修复/落地改快速通道：仅在未显式要求产出多媒体资产时生效，防止「音效开关」等业务词劫持为 audio_sfx_bgm
        if (!hasMediaExt && !hasExplicitMediaCreationCue)
        {
            bool codeFastPath =
                (hasCodeEditVerbCue && (hasCodeExt || hasCodeStackCue || hasShaderExt || hasCodePathCue)) ||
                ((hasCodeExt || hasCodePathCue) && (
                    strippedText.Contains("直接修改", StringComparison.OrdinalIgnoreCase) ||
                    strippedText.Contains("落地修改", StringComparison.OrdinalIgnoreCase) ||
                    strippedText.Contains("实操修改", StringComparison.OrdinalIgnoreCase) ||
                    strippedText.Contains("落地改", StringComparison.OrdinalIgnoreCase) ||
                    strippedText.Contains("直接改", StringComparison.OrdinalIgnoreCase) ||
                    strippedText.Contains("修复", StringComparison.OrdinalIgnoreCase) ||
                    strippedText.Contains("排障", StringComparison.OrdinalIgnoreCase) ||
                    strippedText.Contains("打回重修", StringComparison.OrdinalIgnoreCase) ||
                    strippedText.Contains("重构", StringComparison.OrdinalIgnoreCase)));

            if (codeFastPath)
            {
                bool deepDebugCue =
                    strippedText.Contains("排障", StringComparison.OrdinalIgnoreCase) ||
                    strippedText.Contains("根因", StringComparison.OrdinalIgnoreCase) ||
                    strippedText.Contains("打回重修", StringComparison.OrdinalIgnoreCase) ||
                    TextContainsKeyword(strippedText, "bug") ||
                    TextContainsKeyword(strippedText, "debug");
                if (deepDebugCue)
                    return "deep_debug";
                if (WorkTypeKeywords.TryGetValue("ui_shader_vfx", out var ufxKws) && ufxKws.Any(k => TextContainsKeyword(strippedText, k)))
                    return "ui_shader_vfx";
                if (WorkTypeKeywords.TryGetValue("build_testing", out var btKws) && btKws.Any(k => TextContainsKeyword(strippedText, k)))
                    return "build_testing";
                return "fast_execution";
            }
        }

        // 采用高辨识度专精能力优先顺序，确保视频/音效/3D/图片/UI特效/排障验收不落入泛化默认类型。
        string[] priority =
        {
            "visual_accept", "video_animation", "audio_sfx_bgm", "model_3d_motion",
            "image_generation", "ui_shader_vfx", "data_config", "build_testing",
            "deep_debug", "text_writing", "brain_analysis", "fast_execution"
        };
        bool hasCodeEngineeringCue =
            hasExplicitCodeEditCue ||
            hasCodePathCue ||
            strippedText.Contains(".html", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains(".js", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains(".ts", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains(".cs", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains(".gd", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains(".tscn", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("修改工程", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("落地修改", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("落地改", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("改源码", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("改代码", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("按钮", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("输入框", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("接口", StringComparison.OrdinalIgnoreCase) ||
            strippedText.Contains("构建", StringComparison.OrdinalIgnoreCase);

        foreach (var id in priority)
        {
            if (id == "text_writing" && hasCodeEngineeringCue) continue;
            if (id == "visual_accept" && hasExplicitCodeEditCue) continue;
            // 代码排障/落地改单：禁止被「音效/视频/图片/3D」业务词劫持成多媒体资产类型
            if (hasCodeEngineeringCue && !hasMediaExt && !hasExplicitMediaCreationCue &&
                id is "audio_sfx_bgm" or "video_animation" or "image_generation" or "model_3d_motion")
                continue;
            if (WorkTypeKeywords.TryGetValue(id, out var kws) && kws.Any(k => TextContainsKeyword(strippedText, k)))
                return id;
        }
        return "fast_execution";
    }

    /// <summary>
    /// 关键词命中：纯 ASCII 字母且长度 ≤4 的短词要求前后非 ASCII 字母数字边界，
    /// 避免类名如 StatusFx 误匹配 sfx 等短词。
    /// </summary>
    public static bool TextContainsKeyword(string? text, string? keyword)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword)) return false;
        bool isShortAsciiWord = keyword.Length <= 4 && IsPureAsciiLetters(keyword);
        if (!isShortAsciiWord)
            return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i <= text.Length - keyword.Length; i++)
        {
            if (string.Compare(text, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            bool leftOk = i == 0 || !IsAsciiLetterOrDigit(text[i - 1]);
            bool rightOk = i + keyword.Length >= text.Length || !IsAsciiLetterOrDigit(text[i + keyword.Length]);
            if (leftOk && rightOk) return true;
        }
        return false;
    }

    private static bool IsPureAsciiLetters(string s)
    {
        foreach (char c in s)
        {
            if (c is (< 'A' or > 'Z') and (< 'a' or > 'z')) return false;
        }
        return s.Length > 0;
    }

    private static bool IsAsciiLetterOrDigit(char c)
        => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    public static string InferDefaultDifficultyForWorkType(string? workTypeId) => (workTypeId ?? "").Trim().ToLowerInvariant() switch
    {
        "brain_analysis" or "deep_debug" => "high",
        "text_writing" or "data_config" or "build_testing" => "medium",
        _ => "medium"
    };

    public static int ScoreModelProfileForTask(
        AgentRosterEntry agent,
        ModelWorkProfile mp,
        int slotIdx,
        string workTypeId,
        string? fallbackTypeId,
        string complexityTier,
        bool isReworkRound,
        bool preferNativeWhenTied,
        string[]? keywords = null,
        WorkRouteRule? projectRule = null)
    {
        if (mp == null || !mp.Enabled) return int.MinValue / 2;

        int score = 0;
        int strength = mp.GetCapabilityScore(workTypeId);
        if (strength > 0)
        {
            score += 70 + strength * 24; // 5★=+190, 3★=+142, 1★=+94
        }
        else if (!string.IsNullOrWhiteSpace(fallbackTypeId))
        {
            int fbStrength = mp.GetCapabilityScore(fallbackTypeId);
            if (fbStrength > 0) score += 35 + fbStrength * 10;
        }

        var effComplexity = string.IsNullOrWhiteSpace(complexityTier) ? "medium" : complexityTier.Trim().ToLowerInvariant();
        var policy = (mp.UsagePolicy ?? "primary_default").Trim().ToLowerInvariant();
        score += policy switch
        {
            "primary_default" => 16,
            "complex_or_rework_only" => (effComplexity == "high" || isReworkRound) ? 38 : -85,
            "light_fast_first" => (effComplexity == "low" && !isReworkRound) ? 42 : ((effComplexity == "medium" && !isReworkRound) ? 12 : -35),
            "strict_specialty_only" => strength >= 4 ? 22 : -80,
            "fallback_only" => -110,
            _ => 8
        };

        var diffRule = (mp.TaskDifficultyRule ?? "any").Trim().ToLowerInvariant();
        score += diffRule switch
        {
            "high_only" => (effComplexity == "high" || isReworkRound) ? 20 : -75,
            "medium_up" => (effComplexity == "low" && !isReworkRound) ? -45 : 6,
            "low_medium" => (effComplexity == "high" || isReworkRound) ? -50 : (effComplexity == "low" ? 18 : 6),
            _ => 0
        };

        if (preferNativeWhenTied)
        {
            var own = LocalModelCatalogService.ClassifyModelOwnership(agent.Id, mp.ModelName, mp.OwnershipKind, mp.VendorName, agent.Category);
            if (own.IsNativeOwn) score += 16;
        }

        if (keywords != null && keywords.Length > 0)
        {
            if (keywords.Any(k => mp.SpecialtyNote.Contains(k, StringComparison.OrdinalIgnoreCase))) score += 18;
            if (keywords.Any(k => agent.CustomWorkNote.Contains(k, StringComparison.OrdinalIgnoreCase))) score += 12;
        }
        if (projectRule?.CustomKeywords?.Any(k => !string.IsNullOrWhiteSpace(k) &&
            (mp.SpecialtyNote.Contains(k, StringComparison.OrdinalIgnoreCase) || agent.CustomWorkNote.Contains(k, StringComparison.OrdinalIgnoreCase))) == true)
        {
            score += 30;
        }
        if (!string.IsNullOrWhiteSpace(projectRule?.TargetModelName) &&
            !projectRule.TargetModelName.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            mp.ModelName.Equals(projectRule.TargetModelName, StringComparison.OrdinalIgnoreCase))
        {
            score += 500;
        }

        var speed = (mp.SpeedTier ?? mp.RecommendedEffort ?? "medium").ToLowerInvariant();
        score += (effComplexity, speed) switch
        {
            ("low", "fast" or "low") => 18,
            ("high", "high") => 18,
            ("high", "fast" or "low") => -12,
            ("medium", "medium") => 6,
            _ => 2
        };

        // 槽位排序平局决胜（靠前优先）
        score += Math.Max(0, 10 - slotIdx);
        return score;
    }

    public static WorkRouteResolution ResolveRouteForWorkType(
        RegistryService reg, ProjectProfile? project, string workTypeId,
        string? taskText = null, IEnumerable<AgentRosterEntry>? candidates = null)
    {
        var typeId = string.IsNullOrWhiteSpace(workTypeId) ? InferWorkTypeFromTask(taskText) : workTypeId.Trim();
        var label = WorkSpecialtyCatalog.Items.TryGetValue(typeId, out var title) ? title : typeId;
        var enabled = (candidates ?? reg.UserConfig.Agents).Where(a => a.Enabled).ToList();
        var eligible = ResolveEligibleAgentsForWorkType(enabled, typeId, out bool usedParentFallback);
        string fallbackTypeId = usedParentFallback && WorkSpecialtyCatalog.EngineeringParentFallback.TryGetValue(typeId, out var pt) ? pt : "";
        bool preferNative = reg.UserConfig.PreferNativeModelsWhenTied;
        string complexityTier = !string.IsNullOrWhiteSpace(taskText)
            ? AnalyzeTaskComplexityTier(taskText, false, false).Tier
            : InferDefaultDifficultyForWorkType(typeId);

        WorkRouteRule? projectRule = null;
        if ((project?.WorkTypeRoutes != null && project.WorkTypeRoutes.TryGetValue(typeId, out var pRule) && pRule != null) ||
            (reg.UserConfig.GlobalWorkTypeRoutes != null && reg.UserConfig.GlobalWorkTypeRoutes.TryGetValue(typeId, out pRule) && pRule != null))
        {
            projectRule = pRule;
            var chosen = string.IsNullOrWhiteSpace(pRule.TargetAgentId) || pRule.TargetAgentId.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? null : eligible.FirstOrDefault(a => a.Id.Equals(pRule.TargetAgentId, StringComparison.OrdinalIgnoreCase));
            if (chosen != null)
            {
                var model = ResolveModel(chosen, typeId, pRule.TargetModelName, complexityTier, preferNative);
                var effort = NormalizeEffort(pRule.TargetEffortTier, FindProfile(chosen, model)?.RecommendedEffort);
                return new WorkRouteResolution { WorkTypeId = typeId, WorkTypeLabel = label, TargetAgent = chosen,
                    TargetModelName = model, TargetEffortTier = effort, IsProjectOverride = true,
                    Reason = "命中工作类型路由配置" };
            }
        }

        AgentRosterEntry? bestAgent = null;
        ModelWorkProfile? bestProfile = null;
        var bestScore = int.MinValue;
        var keywords = WorkTypeKeywords.TryGetValue(typeId, out var kws) ? kws : Array.Empty<string>();
        foreach (var agent in eligible)
        {
            var agentScore = agent.PrimaryWorkRoles.Contains(typeId, StringComparer.OrdinalIgnoreCase)
                ? 70
                : (!string.IsNullOrEmpty(fallbackTypeId) && agent.PrimaryWorkRoles.Contains(fallbackTypeId, StringComparer.OrdinalIgnoreCase) ? 35 : 0);
            agentScore += agent.TagScores.TryGetValue(typeId, out var tagScore) ? tagScore * 8 : 0;
            if (agent.Detected) agentScore += 5;
            var profiles = (agent.ModelProfiles ?? new List<ModelWorkProfile>()).Where(p => p.Enabled).ToList();
            if (profiles.Count == 0)
            {
                if (agentScore > bestScore) { bestScore = agentScore; bestAgent = agent; bestProfile = null; }
                continue;
            }
            for (int pIdx = 0; pIdx < profiles.Count; pIdx++)
            {
                var profile = profiles[pIdx];
                var score = agentScore + ScoreModelProfileForTask(
                    agent, profile, pIdx, typeId, fallbackTypeId,
                    complexityTier, isReworkRound: false, preferNativeWhenTied: preferNative,
                    keywords: keywords, projectRule: projectRule);
                if (score > bestScore) { bestScore = score; bestAgent = agent; bestProfile = profile; }
            }
        }

        var requestedAutoModel = projectRule?.TargetModelName;
        var modelName = bestAgent == null ? "" :
            (!LocalModelCatalogService.SupportsModelSelection(bestAgent.Id)
                ? LocalModelCatalogService.ManagedDefaultModelDisplay
                : (!string.IsNullOrWhiteSpace(requestedAutoModel) && !requestedAutoModel.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    ? requestedAutoModel
                    : (bestProfile?.ModelName ?? ResolveModel(bestAgent, typeId, "auto", complexityTier, preferNative)));
        var effortTier = NormalizeEffort(projectRule?.TargetEffortTier, bestProfile?.RecommendedEffort);
        return new WorkRouteResolution { WorkTypeId = typeId, WorkTypeLabel = label, TargetAgent = bestAgent,
            TargetModelName = modelName, TargetEffortTier = effortTier, IsProjectOverride = false,
            Reason = bestAgent == null ? "没有已启用的可路由工具" : projectRule == null
                ? "依据单模型能力强度、使用规则与自有/三方策略自动匹配" : "项目规则指定自动选择，已综合能力强度与关键词匹配" };
    }

    /// <summary>
    /// 路由总览专用：列出该工作类别下所有「已启用且能力星级 &gt; 0」的 AI。
    /// 派单仍走 ResolveRouteForWorkType；此处不因 complex_or_rework_only / PreferNative 把具备能力的 AI 从面板里藏掉。
    /// </summary>
    public static List<WorkRouteOverviewCandidate> ListCapableRouteCandidatesForOverview(
        RegistryService reg,
        ProjectProfile? project,
        string workTypeId,
        IEnumerable<AgentRosterEntry>? candidates = null)
    {
        var typeId = string.IsNullOrWhiteSpace(workTypeId) ? "fast_execution" : workTypeId.Trim();
        var enabled = (candidates ?? reg.UserConfig.Agents).Where(a => a.Enabled).ToList();
        var eligible = ResolveEligibleAgentsForWorkType(enabled, typeId, out bool usedParentFallback);
        string fallbackTypeId = usedParentFallback && WorkSpecialtyCatalog.EngineeringParentFallback.TryGetValue(typeId, out var pt) ? pt : "";
        bool preferNative = reg.UserConfig.PreferNativeModelsWhenTied;
        string complexityTier = InferDefaultDifficultyForWorkType(typeId);
        var keywords = WorkTypeKeywords.TryGetValue(typeId, out var kws) ? kws : Array.Empty<string>();

        WorkRouteRule? projectRule = null;
        if ((project?.WorkTypeRoutes != null && project.WorkTypeRoutes.TryGetValue(typeId, out var pRule) && pRule != null) ||
            (reg.UserConfig.GlobalWorkTypeRoutes != null && reg.UserConfig.GlobalWorkTypeRoutes.TryGetValue(typeId, out pRule) && pRule != null))
        {
            projectRule = pRule;
        }

        bool isManualAgent = projectRule != null &&
                             !string.IsNullOrWhiteSpace(projectRule.TargetAgentId) &&
                             !projectRule.TargetAgentId.Equals("auto", StringComparison.OrdinalIgnoreCase);
        string? manualAgentId = isManualAgent ? projectRule!.TargetAgentId : null;

        var primaryResolution = ResolveRouteForWorkType(reg, project, typeId, candidates: enabled);
        string? primaryAgentId = primaryResolution.TargetAgent?.Id;

        var scored = new List<(AgentRosterEntry Agent, int BestStars, int RouteScore, ModelWorkProfile? RepProfile, string RepModel, string Effort, List<WorkRouteOverviewModelSlot> Slots)>();

        foreach (var agent in eligible)
        {
            var profiles = (agent.ModelProfiles ?? new List<ModelWorkProfile>())
                .Where(p => p.Enabled)
                .ToList();
            string agentDefaultModel = !LocalModelCatalogService.SupportsModelSelection(agent.Id)
                ? LocalModelCatalogService.ManagedDefaultModelDisplay
                : (!string.IsNullOrWhiteSpace(agent.MediumTierModel) ? agent.MediumTierModel
                    : !string.IsNullOrWhiteSpace(agent.HighTierModel) ? agent.HighTierModel : "默认模型");

            var slots = new List<WorkRouteOverviewModelSlot>();
            bool anyCapabilityScores = profiles.Any(HasExplicitCapabilityScores);

            if (profiles.Count > 0)
            {
                for (int pIdx = 0; pIdx < profiles.Count; pIdx++)
                {
                    var mp = profiles[pIdx];
                    int stars = mp.GetCapabilityScore(typeId);
                    if (stars <= 0 && !string.IsNullOrEmpty(fallbackTypeId))
                        stars = mp.GetCapabilityScore(fallbackTypeId);
                    if (stars <= 0) continue;

                    int routeScore = ScoreModelProfileForTask(
                        agent, mp, pIdx, typeId, fallbackTypeId,
                        complexityTier, isReworkRound: false, preferNativeWhenTied: preferNative,
                        keywords: keywords, projectRule: projectRule);
                    // 总览纳入：即便日常难度下 complex_or_rework_only 派单分很低，仍按能力展示
                    slots.Add(new WorkRouteOverviewModelSlot
                    {
                        ModelName = string.IsNullOrWhiteSpace(mp.ModelName) ? agentDefaultModel : mp.ModelName,
                        Stars = stars,
                        UsagePolicy = mp.UsagePolicy ?? "primary_default",
                        RecommendedEffort = NormalizeEffort(null, mp.RecommendedEffort),
                        OwnershipKind = mp.OwnershipKind ?? "",
                        RouteScore = routeScore,
                        Profile = mp
                    });
                }
            }

            // 全部启用模型均未配置 CapabilityScores（或无启用模型）时，按 PrimaryWorkRoles / TagScores 兜底，严禁静默丢弃
            if (slots.Count == 0 && !anyCapabilityScores)
            {
                int tagStars = agent.TagScores != null && agent.TagScores.TryGetValue(typeId, out var ts) ? ts : 0;
                if (tagStars <= 0 && !string.IsNullOrEmpty(fallbackTypeId) &&
                    agent.TagScores != null && agent.TagScores.TryGetValue(fallbackTypeId, out var fts))
                    tagStars = fts;
                if (tagStars <= 0 && agent.PrimaryWorkRoles != null &&
                    (agent.PrimaryWorkRoles.Contains(typeId, StringComparer.OrdinalIgnoreCase) ||
                     (!string.IsNullOrEmpty(fallbackTypeId) && agent.PrimaryWorkRoles.Contains(fallbackTypeId, StringComparer.OrdinalIgnoreCase))))
                    tagStars = 3;
                if (tagStars > 0)
                {
                    string managedName = profiles.Count > 0 && !string.IsNullOrWhiteSpace(profiles[0].ModelName)
                        ? profiles[0].ModelName : agentDefaultModel;
                    slots.Add(new WorkRouteOverviewModelSlot
                    {
                        ModelName = managedName,
                        Stars = Math.Clamp(tagStars, 1, 5),
                        UsagePolicy = profiles.Count > 0 ? (profiles[0].UsagePolicy ?? "primary_default") : "primary_default",
                        RecommendedEffort = profiles.Count > 0
                            ? NormalizeEffort(null, profiles[0].RecommendedEffort)
                            : NormalizeEffort(agent.PreferredEffortTier, "medium"),
                        OwnershipKind = profiles.Count > 0 ? (profiles[0].OwnershipKind ?? "") : "",
                        RouteScore = tagStars * 24 + 70,
                        Profile = profiles.Count > 0 ? profiles[0] : null
                    });
                }
            }

            if (slots.Count == 0) continue;

            // 代表模型：优先路由命中模型；否则取派单分最高；并列取星级最高
            WorkRouteOverviewModelSlot repSlot;
            if (primaryAgentId != null &&
                agent.Id.Equals(primaryAgentId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(primaryResolution.TargetModelName))
            {
                repSlot = slots.FirstOrDefault(s =>
                               s.ModelName.Equals(primaryResolution.TargetModelName, StringComparison.OrdinalIgnoreCase))
                           ?? slots.OrderByDescending(s => s.RouteScore).ThenByDescending(s => s.Stars).First();
            }
            else if (isManualAgent &&
                     agent.Id.Equals(manualAgentId, StringComparison.OrdinalIgnoreCase) &&
                     projectRule != null &&
                     !string.IsNullOrWhiteSpace(projectRule.TargetModelName) &&
                     !projectRule.TargetModelName.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                repSlot = slots.FirstOrDefault(s =>
                               s.ModelName.Equals(projectRule.TargetModelName, StringComparison.OrdinalIgnoreCase))
                           ?? slots.OrderByDescending(s => s.RouteScore).ThenByDescending(s => s.Stars).First();
            }
            else
            {
                repSlot = slots.OrderByDescending(s => s.RouteScore).ThenByDescending(s => s.Stars).First();
            }

            int agentRouteScore = slots.Max(s => s.RouteScore);
            if (agent.PrimaryWorkRoles != null && agent.PrimaryWorkRoles.Contains(typeId, StringComparer.OrdinalIgnoreCase))
                agentRouteScore += 70;
            else if (!string.IsNullOrEmpty(fallbackTypeId) &&
                     agent.PrimaryWorkRoles != null &&
                     agent.PrimaryWorkRoles.Contains(fallbackTypeId, StringComparer.OrdinalIgnoreCase))
                agentRouteScore += 35;
            if (agent.TagScores != null && agent.TagScores.TryGetValue(typeId, out var tagBonus))
                agentRouteScore += tagBonus * 8;
            if (agent.Detected) agentRouteScore += 5;
            if (isManualAgent && agent.Id.Equals(manualAgentId, StringComparison.OrdinalIgnoreCase))
                agentRouteScore += 100000;

            scored.Add((agent, slots.Max(s => s.Stars), agentRouteScore, repSlot.Profile, repSlot.ModelName,
                NormalizeEffort(null, repSlot.RecommendedEffort), slots.OrderByDescending(s => s.Stars).ThenByDescending(s => s.RouteScore).ToList()));
        }

        // 顺位：手动指定置顶，其次与 ResolveRouteForWorkType 首选对齐，再按综合分
        var ordered = scored
            .OrderByDescending(x => isManualAgent && x.Agent.Id.Equals(manualAgentId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => primaryAgentId != null && x.Agent.Id.Equals(primaryAgentId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.RouteScore)
            .ThenByDescending(x => x.BestStars)
            .ThenBy(x => x.Agent.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<WorkRouteOverviewCandidate>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            int rank = i + 1;
            bool isManual = isManualAgent && item.Agent.Id.Equals(manualAgentId, StringComparison.OrdinalIgnoreCase);
            bool isPrimary = rank == 1;
            string reason;
            if (isManual)
                reason = "方案手动指定为该工作类别执行 AI，其余具备能力的 AI 仍作为协同可用候选展示";
            else if (isPrimary)
                reason = preferNative
                    ? "综合能力、使用规则与「同能力优先自有模型」策略后的首选推荐"
                    : "综合能力星级与使用规则后的首选推荐";
            else
            {
                var top = ordered[0];
                bool sameStars = item.BestStars == top.BestStars && preferNative;
                bool hasComplexOnly = item.Slots.Any(s =>
                    string.Equals(s.UsagePolicy, "complex_or_rework_only", StringComparison.OrdinalIgnoreCase) &&
                    s.Stars >= item.BestStars);
                if (sameStars)
                    reason = $"与首选同为 {item.BestStars}★，因方案开启「同能力优先自有模型」顺延至第 {rank} 位";
                else if (hasComplexOnly && item.BestStars > (top.BestStars > 0 ? top.Slots.FirstOrDefault()?.Stars ?? 0 : 0))
                    reason = $"该 {item.BestStars}★ 模型规则为「仅复杂攻坚/重修」，日常优先由主力模型执行，故顺延至第 {rank} 位";
                else
                    reason = $"具备本类能力（最高 {item.BestStars}★），综合顺位第 {rank}，可作为协同可用候选";
            }

            result.Add(new WorkRouteOverviewCandidate
            {
                Agent = item.Agent,
                Rank = rank,
                IsPrimary = isPrimary,
                IsManualOverride = isManual,
                BestCapabilityStars = item.BestStars,
                RouteScore = item.RouteScore,
                RepresentativeModelName = item.RepModel,
                EffortTier = item.Effort,
                RepresentativeProfile = item.RepProfile,
                RankReason = reason,
                CapableModels = item.Slots
            });
        }

        return result;
    }

    /// <summary>清洗模型展示名：去掉括号后缀与冗长托管文案，得到胶囊内核心简称。</summary>
    public static string CompactModelCoreName(string? modelName)
    {
        var raw = (modelName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        if (raw.Contains("客户端默认托管", StringComparison.Ordinal) ||
            raw.Equals(LocalModelCatalogService.ManagedDefaultModelDisplay, StringComparison.OrdinalIgnoreCase))
            return "托管";

        // 去掉各类括号注记：（禁止…）/(Neon)/(claude-opus-5-5)/(default)/(自有)/(第三方)
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"[（(][^）)]*[）)]", "").Trim();
        raw = raw.Replace("  ", " ").Trim();

        // Claude Opus 5.5 → Opus 5.5；Gemini 4 Neon 已去 Neon → Gemini 4
        if (raw.StartsWith("Claude ", StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring("Claude ".Length).Trim();
        if (raw.StartsWith("Google ", StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring("Google ".Length).Trim();

        if (raw.Length > 18)
            raw = raw.Substring(0, 18).Trim();
        return string.IsNullOrWhiteSpace(raw) ? "—" : raw;
    }

    public static string EffortTierVisualLabel(string? tier) => (tier ?? "medium").Trim().ToLowerInvariant() switch
    {
        "high" => "高档·深思",
        "low" => "低档·速办",
        _ => "中档·主力"
    };

    private static ModelWorkProfile? FindProfile(AgentRosterEntry agent, string model) =>
        agent.ModelProfiles?.FirstOrDefault(p => p.ModelName.Equals(model, StringComparison.OrdinalIgnoreCase));

    private static string ResolveModel(AgentRosterEntry agent, string workTypeId, string? requested, string complexityTier = "medium", bool preferNativeWhenTied = false)
    {
        if (!LocalModelCatalogService.SupportsModelSelection(agent.Id))
            return LocalModelCatalogService.ManagedDefaultModelDisplay;
        if (!string.IsNullOrWhiteSpace(requested) && !requested.Equals("auto", StringComparison.OrdinalIgnoreCase)) return requested;

        var enabledProfiles = (agent.ModelProfiles ?? new List<ModelWorkProfile>()).Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.ModelName)).ToList();
        if (enabledProfiles.Count > 0)
        {
            string fbType = WorkSpecialtyCatalog.EngineeringParentFallback.TryGetValue(workTypeId, out var pt) ? pt : "";
            var best = enabledProfiles
                .Select((mp, idx) => (mp, score: ScoreModelProfileForTask(agent, mp, idx, workTypeId, fbType, complexityTier, false, preferNativeWhenTied)))
                .OrderByDescending(x => x.score)
                .First().mp;
            return best.ModelName;
        }
        return agent.MediumTierModel.Length > 0 ? agent.MediumTierModel : agent.HighTierModel;
    }

    private static string NormalizeEffort(string? requested, string? fallback)
    {
        var value = !string.IsNullOrWhiteSpace(requested) && !requested.Equals("auto", StringComparison.OrdinalIgnoreCase) ? requested : fallback;
        return value?.ToLowerInvariant() switch { "low" => "low", "medium" => "medium", "high" => "high", _ => "medium" };
    }

    public List<StageDispatchResult> ComputeDispatch(
        RegistryService reg,
        ProjectProfile project,
        WorkflowTemplate workflow)
    {
        var enabledAgents = reg.UserConfig.Agents.Where(a => a.Enabled).ToList();
        var results = new List<StageDispatchResult>();

        var gatekeepers = enabledAgents
            .Where(a => a.Permissions.AllowDirectSourceWrite && a.Permissions.HasPreviewMcp)
            .ToList();
        var primaryGatekeeper = gatekeepers.FirstOrDefault()
                                ?? enabledAgents.FirstOrDefault(a => a.Permissions.AllowDirectSourceWrite);

        foreach (var stage in workflow.Stages)
        {
            var overrideKey = $"{workflow.Id}::{stage.Id}";
            project.StageOverrides.TryGetValue(overrideKey, out var modeOrAgentId);

            AgentRosterEntry? chosen = null;
            bool isManual = false;
            string reason;

            if (!string.IsNullOrWhiteSpace(modeOrAgentId) && !string.Equals(modeOrAgentId, "auto", StringComparison.OrdinalIgnoreCase))
            {
                chosen = enabledAgents.FirstOrDefault(a => a.Id == modeOrAgentId);
                if (chosen != null)
                {
                    isManual = true;
                    reason = "手动固定指派";
                }
                else
                {
                    reason = "原手动指定的工具已停用，已回退按特性自动择优";
                }
            }
            else
            {
                reason = "";
            }

            if (chosen == null && enabledAgents.Count > 0)
            {
                chosen = enabledAgents
                    .OrderByDescending(a => ScoreAgentForStage(a, stage))
                    .First();
                var tagTitle = reg.Tags.Tags.FirstOrDefault(t => t.Id == stage.RequiredTag)?.Name ?? stage.RequiredTag;
                var stars = chosen.TagScores.TryGetValue(stage.RequiredTag, out var sc) ? sc : 0;
                if (string.IsNullOrEmpty(reason))
                {
                    reason = $"自动命中特性 [{tagTitle} ★{stars}]";
                }
            }

            var guardNotes = new List<string>();
            if (chosen != null)
            {
                if (stage.RequireDirectWrite && !chosen.Permissions.AllowDirectSourceWrite)
                {
                    var gkName = primaryGatekeeper?.Name ?? "本地主力守门槽";
                    guardNotes.Add($"该工具禁改正式源码/场景 ➔ 产物进 inbox/，由【{gkName}】打快照代合入");
                }
                if (stage.RequirePreviewMcp && !chosen.Permissions.HasPreviewMcp)
                {
                    var gkName = primaryGatekeeper?.Name ?? "具备 MCP 的守门槽";
                    guardNotes.Add($"该工具无实机预览 MCP ➔ 收尾自动追加【{gkName}】执行画面截图验收");
                }
                if (chosen.AdapterId == "capsule_only")
                {
                    guardNotes.Add("无本地目录模式 ➔ 使用「导出远程胶囊」剪贴板收发");
                }
                else if (chosen.AdapterId == "bridge_folder")
                {
                    guardNotes.Add("VPS 桥接目录模式 ➔ 自动投送任务单至本地镜像子目录");
                }
            }

            results.Add(new StageDispatchResult
            {
                StageId = stage.Id,
                StageName = stage.Name,
                AssignedAgentId = chosen?.Id ?? "",
                AssignedAgentName = chosen?.Name ?? "（尚未启用任何 AI 工具）",
                IsManualOverride = isManual,
                Reason = chosen == null ? "请先在「本机检测与接入」页勾选至少 1 个工具" : reason,
                GuardrailNote = guardNotes.Count > 0 ? string.Join("；", guardNotes) : "权限与验收闭环完整"
            });
        }

        return results;
    }

    private static int ScoreAgentForStage(AgentRosterEntry agent, WorkflowStageSpec stage)
    {
        int score = 0;
        if (agent.TagScores.TryGetValue(stage.RequiredTag, out var tagScore))
        {
            score += tagScore * 20;
        }
        if (stage.RequireDirectWrite)
        {
            score += agent.Permissions.AllowDirectSourceWrite ? 35 : -25;
        }
        if (stage.RequirePreviewMcp)
        {
            score += agent.Permissions.HasPreviewMcp ? 35 : -20;
        }
        if (agent.Detected)
        {
            score += 5;
        }
        return score;
    }

    /// <summary>
    /// 计算某个 AI 作为「主脑 / 总分析拆解与验收 AI」的综合分析力得分（结合能力星级与用户配置的权限参数）
    /// </summary>
    public static int ComputeMasterPlannerPowerScore(AgentRosterEntry agent)
    {
        int s = 0;
        int GetTag(string k) => agent.TagScores.TryGetValue(k, out var v) ? v : 0;
        s += GetTag("deep_bug_hunt") * 25;
        s += GetTag("cross_repo_impl") * 25;
        s += GetTag("build_delivery") * 15;
        s += GetTag("visual_gate") * 15;
        s += GetTag("adversarial_review") * 15;
        s += GetTag("design_copywriting") * 12;
        if (agent.Permissions.AllowDirectSourceWrite) s += 20;
        if (agent.Permissions.AllowMultiDir) s += 15;
        if (agent.Permissions.AllowShellBuild) s += 15;
        if (agent.Permissions.HasPreviewMcp) s += 20;
        if (agent.Id == "antigravity") s += 30; // 当前主对话宿主天然具备调度总线与实机 MCP 终验闭环
        return s;
    }

    /// <summary>
    /// 解析当前项目的「主脑 / 最适合做全局分析处理与派单的 AI」
    /// </summary>
    public static AgentRosterEntry? ResolveMasterPlanner(RegistryService reg, ProjectProfile? project)
    {
        var enabled = reg.UserConfig.Agents.Where(a => a.Enabled && a.Detected).ToList();
        if (enabled.Count == 0) enabled = reg.UserConfig.Agents.Where(a => a.Enabled).ToList();
        if (enabled.Count == 0) return reg.UserConfig.Agents.FirstOrDefault();

        var manualId = project?.MasterPlannerId;
        if (!string.IsNullOrWhiteSpace(manualId) && !string.Equals(manualId, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var manual = enabled.FirstOrDefault(a => string.Equals(a.Id, manualId, StringComparison.OrdinalIgnoreCase));
            if (manual != null) return manual;
        }

        return enabled
            .OrderByDescending(ComputeMasterPlannerPowerScore)
            .First();
    }

    public sealed class TaskRouteDecision
    {
        public AgentRosterEntry? MasterPlanner { get; set; }
        public AgentRosterEntry? TargetAgent { get; set; }
        public string TaskCategoryLabel { get; set; } = "";
        public string PrimaryTagId { get; set; } = "";
        public string SecondaryTagId { get; set; } = "";
        public bool RequiresDirectWrite { get; set; }
        public bool RequiresMultiDir { get; set; }
        public bool RequiresShellBuild { get; set; }
        public string TaskComplexityTier { get; set; } = "high"; // high | medium | low
        public string TaskComplexityReason { get; set; } = "";
        public string MasterSelectedModel { get; set; } = "";
        public string MasterSelectedTier { get; set; } = "high";
        public string TargetSelectedModel { get; set; } = "";
        public string TargetSelectedTier { get; set; } = "high";
        public string TargetTierBadge { get; set; } = "高档·High";
        public string WorkTypeId { get; set; } = "";
        public string WorkTypeLabel { get; set; } = "";
        public string DispatchReason { get; set; } = "";
    }

    public static (string Tier, string Reason) AnalyzeTaskComplexityTier(string? taskInstruction, bool reqMultiDir, bool reqShell)
    {
        var t = (taskInstruction ?? "").Trim();
        if (string.IsNullOrWhiteSpace(t))
            return ("medium", "常规协同任务 ➔ 适配【中档·Medium】标准均衡模型");

        bool hasHighKeywords =
            t.Contains("架构") || t.Contains("重构") || t.Contains("跨目录") || t.Contains("底层") ||
            t.Contains("核心") || t.Contains("疑难") || t.Contains("崩溃") || t.Contains("报错") ||
            t.Contains("异常") || t.Contains("死锁") || t.Contains("不生效") || t.Contains("深度") ||
            t.Contains("复杂") || t.Contains("算法") || t.Contains("闭环") || t.Contains("全面") ||
            t.Contains("着色器") || t.Contains(".gdshader", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("打回") || t.Contains("返工") || (reqMultiDir && reqShell) || t.Length >= 150;

        if (hasHighKeywords)
        {
            return ("high", "涉及核心逻辑/跨模块实装或深度排障 ➔ 自动适配【高档·High】深度推理模型");
        }

        bool hasLowKeywords =
            (t.Contains("文案") || t.Contains("注释") || t.Contains("常量") || t.Contains("微调") ||
             t.Contains("简单") || t.Contains("快速") || t.Contains("格式") || t.Contains("改个名")) &&
            !t.Contains("重构") && !t.Contains("系统") && t.Length < 65;

        if (hasLowKeywords)
        {
            return ("low", "轻量文案/数值/格式微调 ➔ 自动适配【低档·Low】极速响应模型");
        }

        return ("medium", "标准工程代码/配置/预制体实操修改 ➔ 自动适配【中档·Medium】均衡实操模型");
    }

    public static (string ModelName, string EffortTier, string TierBadge, string EngineCodexModel) ResolveAgentModelAndTier(
        AgentRosterEntry? agent,
        string autoComplexityTier,
        bool escalateToHighOnRework = false,
        string? workTypeId = null,
        bool preferNativeWhenTied = false)
    {
        if (agent == null)
        {
            return ("gpt-6-luna", "high", "高档·High", "gpt-6-luna");
        }

        CloudRecommendationService.EnsureAgentModelTiersPopulated(agent);

        if (!LocalModelCatalogService.SupportsModelSelection(agent.Id))
        {
            return (LocalModelCatalogService.ManagedDefaultModelDisplay, "medium", "客户端默认托管（防风控锁定）", "");
        }

        string tier = autoComplexityTier;
        if (escalateToHighOnRework)
        {
            tier = "high";
        }
        else if (string.Equals(agent.ModelSelectionMode, "manual", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(agent.PreferredEffortTier) &&
                 !string.Equals(agent.PreferredEffortTier, "auto", StringComparison.OrdinalIgnoreCase))
        {
            tier = agent.PreferredEffortTier.ToLowerInvariant();
        }
        else if (!string.IsNullOrWhiteSpace(agent.PreferredEffortTier) &&
                 !string.Equals(agent.PreferredEffortTier, "auto", StringComparison.OrdinalIgnoreCase))
        {
            tier = agent.PreferredEffortTier.ToLowerInvariant();
        }

        tier = tier switch
        {
            "low" => "low",
            "medium" => "medium",
            _ => "high"
        };

        string chosenModel = tier switch
        {
            "low" => !string.IsNullOrWhiteSpace(agent.LowTierModel) ? agent.LowTierModel : agent.MediumTierModel,
            "medium" => !string.IsNullOrWhiteSpace(agent.MediumTierModel) ? agent.MediumTierModel : agent.HighTierModel,
            _ => !string.IsNullOrWhiteSpace(agent.HighTierModel) ? agent.HighTierModel : "gpt-6-luna (高档·High)"
        };

        // 当处于自动选模模式且提供了工作类型时，依据各单模型的启停状态、1~5 级能力强度、使用规则与自有优先策略择优
        bool isAutoTierPreference = string.IsNullOrWhiteSpace(agent.PreferredEffortTier) ||
                                    string.Equals(agent.PreferredEffortTier, "auto", StringComparison.OrdinalIgnoreCase);
        var enabledProfiles = (agent.ModelProfiles ?? new List<ModelWorkProfile>())
            .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.ModelName))
            .ToList();
        if (!escalateToHighOnRework && isAutoTierPreference && !string.IsNullOrWhiteSpace(workTypeId) && enabledProfiles.Count > 0)
        {
            string fbType = WorkSpecialtyCatalog.EngineeringParentFallback.TryGetValue(workTypeId, out var pt) ? pt : "";
            var scored = enabledProfiles
                .Select((mp, idx) => (mp, score: ScoreModelProfileForTask(
                    agent, mp, idx, workTypeId, fbType,
                    autoComplexityTier, isReworkRound: false, preferNativeWhenTied: preferNativeWhenTied)))
                .OrderByDescending(x => x.score)
                .First();
            chosenModel = scored.mp.ModelName;
            tier = scored.mp.RecommendedEffort switch
            {
                "low" => "low",
                "high" => "high",
                _ => "medium"
            };
        }

        string badge = tier switch
        {
            "low" => "低档·Low (极速)",
            "medium" => "中档·Medium (均衡)",
            _ => "高档·High (深度)"
        };

        // 提炼给底层 codex.exe -m 使用的本机缓存兼容模型 slug（如 gpt-6-luna / gpt-5.6-terra / gpt-5.6-luna）
        string engineCodexModel = "gpt-6-luna";
        if (!string.IsNullOrWhiteSpace(chosenModel))
        {
            var codexSlugs = LocalModelCatalogService.ProbeLocalModelsForAgent("codex_cli").Models;
            foreach (var item in codexSlugs)
            {
                var slugPart = item.Contains('(')
                    ? item[(item.LastIndexOf('(') + 1)..].TrimEnd(')').Trim()
                    : item.Trim();
                if (!string.IsNullOrWhiteSpace(slugPart) &&
                    chosenModel.Contains(slugPart, StringComparison.OrdinalIgnoreCase))
                {
                    engineCodexModel = slugPart;
                    break;
                }
            }
        }

        return (chosenModel, tier, badge, engineCodexModel);
    }

    /// <summary>
    /// 由主脑 AI 根据任务内容、每个 AI 的擅长领域星级 (TagScores) 以及用户为每个 AI 配置的权限参数 (Permissions / StageOverrides) 智能匹配最合适的执行 AI
    /// </summary>
    public static TaskRouteDecision AnalyzeAndSelectAgentForTask(
        RegistryService reg,
        ProjectProfile? project,
        string? taskInstruction,
        string? targetOrAuto,
        List<AgentRosterEntry> executableCandidates)
    {
        var master = ResolveMasterPlanner(reg, project);
        var masterName = master?.Name ?? "Antigravity";
        var text = (taskInstruction ?? "").Trim();

        // 1. 主脑分析任务的工程操作属性与所需能力标签
        bool isReadOnly = text.Contains("只读", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("禁改", StringComparison.OrdinalIgnoreCase);

        string primaryTag = "cross_repo_impl";
        string secondaryTag = "ide_live_tweak";
        string categoryLabel = "代码与工程文件修改";
        string stageOverrideKey = "tier_m_feature::m_build";
        bool reqWrite = !isReadOnly;
        bool reqMultiDir = text.Contains("配套工具") || text.Contains("跨目录") || text.Contains("config-tool") || text.Contains("collab");
        bool reqShell = text.Contains("编译") || text.Contains("构建") || text.Contains("打包") || text.Contains("测试") || text.Contains("publish") || text.Contains("build");

        if (text.Contains(".tscn", StringComparison.OrdinalIgnoreCase) ||
            text.Contains(".tres", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("预制体") || text.Contains("场景") || text.Contains("节点") ||
            text.Contains("UI") || text.Contains("微调") || text.Contains("手感") ||
            text.Contains(".gdshader", StringComparison.OrdinalIgnoreCase) || text.Contains("着色器") || text.Contains("材质"))
        {
            primaryTag = "ide_live_tweak";
            secondaryTag = "cross_repo_impl";
            categoryLabel = "预制体 (.tscn/.tres) / 场景 / 代码微调";
            stageOverrideKey = "tier_s_tweak::s_live_edit";
            reqWrite = !isReadOnly;
        }
        else if (text.Contains("project.godot", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("项目设置") || text.Contains("设计设置") ||
                 text.Contains(".gd", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains(".cs", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("代码") || text.Contains("脚本") || text.Contains("实装") || text.Contains("字段") || text.Contains("配置"))
        {
            primaryTag = "cross_repo_impl";
            secondaryTag = "ide_live_tweak";
            categoryLabel = "项目代码 / 项目设置 / 跨仓实装";
            stageOverrideKey = "tier_m_feature::m_build";
            reqWrite = !isReadOnly;
        }
        else if (reqShell)
        {
            primaryTag = "build_delivery";
            secondaryTag = "cross_repo_impl";
            categoryLabel = "构建打包与测试验证";
            stageOverrideKey = "tier_m_feature::m_build";
        }
        else if (text.Contains("策划") || text.Contains("文案") || text.Contains("曲线") ||
                 text.Contains("数值") || text.Contains("公式") || text.Contains("方案") || text.Contains("设计"))
        {
            primaryTag = "design_copywriting";
            secondaryTag = "offline_math_plot";
            categoryLabel = "机制方案推演与数值曲线";
            stageOverrideKey = "tier_m_feature::m_spec";
            reqWrite = !isReadOnly && (text.Contains("修改") || text.Contains("写入") || text.Contains("直接改"));
        }
        else if (text.Contains("审查") || text.Contains("挑刺") || text.Contains("审计") ||
                 text.Contains("检查") || text.Contains("验收") || text.Contains("复核") ||
                 text.Contains("核验") || text.Contains("review", StringComparison.OrdinalIgnoreCase))
        {
            primaryTag = "adversarial_review";
            secondaryTag = "deep_bug_hunt";
            categoryLabel = "异构红军挑刺与质量审查";
            stageOverrideKey = "tier_l_hardbug::l_red_review";
            reqWrite = !isReadOnly && (text.Contains("直接修改") || text.Contains("顺便修") || text.Contains("直接改"));
        }
        else if (text.Contains("bug", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("报错") || text.Contains("崩溃") || text.Contains("异常") ||
                 text.Contains("不生效") || text.Contains("排查") || text.Contains("会诊"))
        {
            primaryTag = "deep_bug_hunt";
            secondaryTag = "cross_repo_impl";
            categoryLabel = "疑难重症推理与修复";
            stageOverrideKey = "tier_l_hardbug::l_blind_diag";
        }

        // 若调用方显式指定了 stage:<tag>
        if (!string.IsNullOrWhiteSpace(targetOrAuto) && targetOrAuto.StartsWith("stage:", StringComparison.OrdinalIgnoreCase))
        {
            primaryTag = targetOrAuto["stage:".Length..].Trim();
        }

        var (complexityTier, complexityReason) = AnalyzeTaskComplexityTier(text, reqMultiDir, reqShell);
        var (masterModel, masterTier, _, _) = ResolveAgentModelAndTier(master, "high");

        var activePool = executableCandidates.Where(a => a.Enabled && a.Detected).ToList();
        if (activePool.Count == 0) activePool = executableCandidates;

        // 1.5 先识别全维工作能力类型并绑定对应多媒体能力标签，然后按工程子项回退兼容构建白名单候选池
        var inferredWorkTypeId = InferWorkTypeFromTask(text);
        var inferredWorkLabel = WorkSpecialtyCatalog.Items.TryGetValue(inferredWorkTypeId, out var wLbl) ? wLbl : inferredWorkTypeId;
        switch (inferredWorkTypeId)
        {
            case "video_animation":
                primaryTag = "video_storyboard";
                secondaryTag = "image_asset_gen";
                categoryLabel = "视频生成与动态分镜";
                break;
            case "audio_sfx_bgm":
                primaryTag = "audio_sfx_music";
                secondaryTag = "design_copywriting";
                categoryLabel = "音效配乐与语音合成";
                break;
            case "model_3d_motion":
                primaryTag = "model_3d_anim";
                secondaryTag = "ide_live_tweak";
                categoryLabel = "3D模型与骨骼动作";
                break;
            case "image_generation":
                primaryTag = "image_asset_gen";
                secondaryTag = "design_copywriting";
                categoryLabel = "图片生成与美术贴图";
                break;
            case "ui_shader_vfx":
                primaryTag = "ide_live_tweak";
                secondaryTag = "cross_repo_impl";
                categoryLabel = "UI界面排版与 Shader 特效";
                break;
            case "data_config":
                primaryTag = "offline_math_plot";
                secondaryTag = "cross_repo_impl";
                categoryLabel = "配表与数值设计";
                break;
            case "build_testing":
                primaryTag = "build_delivery";
                secondaryTag = "cross_repo_impl";
                categoryLabel = "构建打包与测试自检";
                break;
        }
        var roleEligiblePool = ResolveEligibleAgentsForWorkType(activePool, inferredWorkTypeId, out bool usedEngineeringFallback);
        var magpieRoute = ResolveRouteForWorkType(reg, project, inferredWorkTypeId, text, activePool);

        // 工程源码改/排查单：禁止因 BGM/图片等业务词落到 Grok Bot，优先直改代码的 Cursor
        bool codeEditForbidGrok = IsEngineeringSourceEditTask(text);
        if (codeEditForbidGrok &&
            inferredWorkTypeId is "audio_sfx_bgm" or "video_animation" or "image_generation" or "model_3d_motion")
        {
            bool debugCue =
                text.Contains("排查", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("排障", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("不生效", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("根因", StringComparison.OrdinalIgnoreCase);
            inferredWorkTypeId = debugCue ? "deep_debug" : "fast_execution";
            inferredWorkLabel = WorkSpecialtyCatalog.Items.TryGetValue(inferredWorkTypeId, out var relabel) ? relabel : inferredWorkTypeId;
            if (debugCue)
            {
                primaryTag = "deep_bug_hunt";
                secondaryTag = "cross_repo_impl";
                categoryLabel = "疑难重症推理与修复";
                stageOverrideKey = "tier_l_hardbug::l_blind_diag";
            }
            else
            {
                primaryTag = "cross_repo_impl";
                secondaryTag = "ide_live_tweak";
                categoryLabel = "项目代码 / 项目设置 / 跨仓实装";
                stageOverrideKey = "tier_m_feature::m_build";
            }
            roleEligiblePool = ResolveEligibleAgentsForWorkType(activePool, inferredWorkTypeId, out usedEngineeringFallback);
            magpieRoute = ResolveRouteForWorkType(reg, project, inferredWorkTypeId, text, activePool);
        }
        if (codeEditForbidGrok)
        {
            var withoutGrok = roleEligiblePool.Where(a => !IsGrokBotAgent(a)).ToList();
            if (withoutGrok.Count == 0)
            {
                var cursorWriter = activePool.FirstOrDefault(a =>
                    string.Equals(a.Id, "cursor", StringComparison.OrdinalIgnoreCase) &&
                    a.Permissions.AllowDirectSourceWrite);
                var anyWriter = activePool.FirstOrDefault(a =>
                    !IsGrokBotAgent(a) && a.Permissions.AllowDirectSourceWrite);
                var cursorAny = activePool.FirstOrDefault(a =>
                    string.Equals(a.Id, "cursor", StringComparison.OrdinalIgnoreCase));
                var pick = cursorWriter ?? anyWriter ?? cursorAny;
                if (pick != null) withoutGrok.Add(pick);
            }
            roleEligiblePool = withoutGrok;
        }

        // 2. 若调用方显式指定了具体 AI 名称/ID（非 auto 且非 stage:），仍须严守用户勾选职责白名单
        if (!string.IsNullOrWhiteSpace(targetOrAuto) &&
            !targetOrAuto.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !targetOrAuto.StartsWith("stage:", StringComparison.OrdinalIgnoreCase))
        {
            var exact = reg.UserConfig.Agents.FirstOrDefault(a =>
                string.Equals(a.Id, targetOrAuto, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains(targetOrAuto, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                bool exactEligible = IsAgentEligibleForWorkType(exact, inferredWorkTypeId) ||
                                     (usedEngineeringFallback && WorkSpecialtyCatalog.EngineeringParentFallback.TryGetValue(inferredWorkTypeId, out var pWorkId) &&
                                      IsAgentEligibleForWorkType(exact, pWorkId));
                if (codeEditForbidGrok && IsGrokBotAgent(exact))
                    exactEligible = false;
                if (!exactEligible)
                {
                    var redirected = roleEligiblePool
                        .OrderByDescending(a => (a.TagScores.TryGetValue(primaryTag, out var ps) ? ps * 25 : 0) + (reqWrite && a.Permissions.AllowDirectSourceWrite ? 45 : 0))
                        .FirstOrDefault();
                    if (redirected != null)
                    {
                        var (rMod, rT, rBg, _) = ResolveAgentModelAndTier(redirected, complexityTier, false, inferredWorkTypeId, reg.UserConfig.PreferNativeModelsWhenTied);
                        return new TaskRouteDecision
                        {
                            MasterPlanner = master,
                            TargetAgent = redirected,
                            TaskCategoryLabel = categoryLabel,
                            PrimaryTagId = primaryTag,
                            SecondaryTagId = secondaryTag,
                            RequiresDirectWrite = reqWrite,
                            RequiresMultiDir = reqMultiDir,
                            RequiresShellBuild = reqShell,
                            TaskComplexityTier = complexityTier,
                            TaskComplexityReason = complexityReason,
                            MasterSelectedModel = masterModel,
                            MasterSelectedTier = masterTier,
                            TargetSelectedModel = rMod,
                            TargetSelectedTier = rT,
                            TargetTierBadge = rBg,
                            WorkTypeId = inferredWorkTypeId,
                            WorkTypeLabel = inferredWorkLabel,
                            DispatchReason = $"⚠️ 原指定 [{exact.Name}] 未勾选「{inferredWorkLabel}」职责，已按您的分工配置自动改派合规工具 [{redirected.Name}]（模型：{rMod} [{rBg}]）"
                        };
                    }

                    return new TaskRouteDecision
                    {
                        MasterPlanner = master,
                        TargetAgent = null,
                        TaskCategoryLabel = categoryLabel,
                        PrimaryTagId = primaryTag,
                        SecondaryTagId = secondaryTag,
                        RequiresDirectWrite = reqWrite,
                        RequiresMultiDir = reqMultiDir,
                        RequiresShellBuild = reqShell,
                        TaskComplexityTier = complexityTier,
                        TaskComplexityReason = complexityReason,
                        MasterSelectedModel = masterModel,
                        MasterSelectedTier = masterTier,
                        WorkTypeId = inferredWorkTypeId,
                        WorkTypeLabel = inferredWorkLabel,
                        DispatchReason = $"⚠️ 原指定 [{exact.Name}] 未勾选「{inferredWorkLabel}」职责，且当前无其他合规后台子 AI，已拦截越权派发并交回主脑直接处理"
                    };
                }

                var pName = reg.Tags.Tags.FirstOrDefault(t => t.Id == primaryTag)?.Name ?? primaryTag;
                int pStar = exact.TagScores.TryGetValue(primaryTag, out var sc) ? sc : 0;
                var (tgtModel, tgtTier, tgtBadge, _) = ResolveAgentModelAndTier(exact, complexityTier, false, inferredWorkTypeId, reg.UserConfig.PreferNativeModelsWhenTied);
                return new TaskRouteDecision
                {
                    MasterPlanner = master,
                    TargetAgent = exact,
                    TaskCategoryLabel = categoryLabel,
                    PrimaryTagId = primaryTag,
                    SecondaryTagId = secondaryTag,
                    RequiresDirectWrite = reqWrite,
                    RequiresMultiDir = reqMultiDir,
                    RequiresShellBuild = reqShell,
                    TaskComplexityTier = complexityTier,
                    TaskComplexityReason = complexityReason,
                    MasterSelectedModel = masterModel,
                    MasterSelectedTier = masterTier,
                    TargetSelectedModel = tgtModel,
                    TargetSelectedTier = tgtTier,
                    TargetTierBadge = tgtBadge,
                    WorkTypeId = inferredWorkTypeId,
                    WorkTypeLabel = inferredWorkLabel,
                    DispatchReason = $"由主脑 [{masterName} · {masterModel}] 深度分析「{categoryLabel}」➔ 指派 [{exact.Name}] 实操执行（适配模型：{tgtModel} [{tgtBadge}] · {pName} ★{pStar}）"
                };
            }
        }

        // 3. 检查用户配置的 Magpie 工作类型精细路由（已在 ResolveRouteForWorkType 内强制执行白名单过滤）
        if (magpieRoute.IsProjectOverride && magpieRoute.TargetAgent != null &&
            roleEligiblePool.Any(a => string.Equals(a.Id, magpieRoute.TargetAgent.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var rAgent = magpieRoute.TargetAgent;
            var rTier = magpieRoute.TargetEffortTier;
            var rModel = !string.IsNullOrWhiteSpace(magpieRoute.TargetModelName)
                ? magpieRoute.TargetModelName
                : ResolveAgentModelAndTier(rAgent, rTier).ModelName;
            var rBadge = rTier switch
            {
                "low" => "低档·Low (极速)",
                "medium" => "中档·Medium (均衡)",
                _ => "高档·High (深度)"
            };
            return new TaskRouteDecision
            {
                MasterPlanner = master,
                TargetAgent = rAgent,
                TaskCategoryLabel = categoryLabel,
                PrimaryTagId = primaryTag,
                SecondaryTagId = secondaryTag,
                RequiresDirectWrite = reqWrite,
                RequiresMultiDir = reqMultiDir,
                RequiresShellBuild = reqShell,
                TaskComplexityTier = complexityTier,
                TaskComplexityReason = complexityReason,
                MasterSelectedModel = masterModel,
                MasterSelectedTier = masterTier,
                TargetSelectedModel = rModel,
                TargetSelectedTier = rTier,
                TargetTierBadge = rBadge,
                WorkTypeId = inferredWorkTypeId,
                WorkTypeLabel = inferredWorkLabel,
                DispatchReason = $"🎯 Magpie 路由命中「{inferredWorkLabel}」➔ 指派 [{rAgent.Name}]（模型：{rModel} [{rBadge}] · {magpieRoute.Reason}）"
            };
        }

        // 4. 检查用户在「⑤ 工具接入与分工」是否为对应阶段手动固定指派了某个已启用且勾选了该职责的 AI
        if (project != null && project.StageOverrides.TryGetValue(stageOverrideKey, out var stageAgentId) &&
            !string.IsNullOrWhiteSpace(stageAgentId) &&
            !string.Equals(stageAgentId, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var manualStageAgent = roleEligiblePool.FirstOrDefault(a => string.Equals(a.Id, stageAgentId, StringComparison.OrdinalIgnoreCase));
            if (manualStageAgent != null)
            {
                var (tgtModel, tgtTier, tgtBadge, _) = ResolveAgentModelAndTier(manualStageAgent, complexityTier, false, inferredWorkTypeId, reg.UserConfig.PreferNativeModelsWhenTied);
                return new TaskRouteDecision
                {
                    MasterPlanner = master,
                    TargetAgent = manualStageAgent,
                    TaskCategoryLabel = categoryLabel,
                    PrimaryTagId = primaryTag,
                    SecondaryTagId = secondaryTag,
                    RequiresDirectWrite = reqWrite,
                    RequiresMultiDir = reqMultiDir,
                    RequiresShellBuild = reqShell,
                    TaskComplexityTier = complexityTier,
                    TaskComplexityReason = complexityReason,
                    MasterSelectedModel = masterModel,
                    MasterSelectedTier = masterTier,
                    TargetSelectedModel = tgtModel,
                    TargetSelectedTier = tgtTier,
                    TargetTierBadge = tgtBadge,
                    WorkTypeId = inferredWorkTypeId,
                    WorkTypeLabel = inferredWorkLabel,
                    DispatchReason = $"由主脑 [{masterName}] 深度分析为「{categoryLabel}」➔ 命中固定分工 [{manualStageAgent.Name}] 实操（模型：{tgtModel} [{tgtBadge}]）"
                };
            }
        }

        // 4. 根据每个 AI 的能力星级 (TagScores) + 用户配置的权限参数 (Permissions) + 实时空闲状态综合打分选优（仅从已勾选该职责的 roleEligiblePool 中选优，绝不越权强塞）
        var liveMap = AutoDispatchService.ReadAllLiveWorkStates();
        AgentRosterEntry? best = null;
        int bestScore = int.MinValue;

        foreach (var cand in roleEligiblePool)
        {
            int score = 0;
            int pStar = cand.TagScores.TryGetValue(primaryTag, out var ps) ? ps : 0;
            int sStar = cand.TagScores.TryGetValue(secondaryTag, out var ss) ? ss : 0;
            score += pStar * 25 + sStar * 12;

            // 权限参数匹配加权（严格尊重用户给每个 AI 勾选的权限开关）
            if (reqWrite)
            {
                score += cand.Permissions.AllowDirectSourceWrite ? 45 : -120;
            }
            if (reqMultiDir)
            {
                score += cand.Permissions.AllowMultiDir ? 20 : -40;
            }
            if (reqShell)
            {
                score += cand.Permissions.AllowShellBuild ? 25 : -40;
            }

            // 实时工作流负载均衡：若某 AI 当前正在 busy working，优先把新任务分给空闲且能力匹配的 AI
            if (liveMap.Values.Any(st => st.AgentId.Equals(cand.Id, StringComparison.OrdinalIgnoreCase) && st.Status == "working"))
            {
                score -= 30;
            }

            // Magpie 工作定位与模型画像加成：若该 AI 或其模型明确配置了对应工作角色，给予显著加分
            if (cand.PrimaryWorkRoles.Contains(inferredWorkTypeId, StringComparer.OrdinalIgnoreCase))
            {
                score += 40;
            }
            if (cand.ModelProfiles != null && cand.ModelProfiles.Any(mp => mp.PrimaryRoles.Contains(inferredWorkTypeId, StringComparer.OrdinalIgnoreCase)))
            {
                score += 35;
            }

            if (codeEditForbidGrok &&
                string.Equals(cand.Id, "cursor", StringComparison.OrdinalIgnoreCase) &&
                cand.Permissions.AllowDirectSourceWrite)
            {
                score += 500;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = cand;
            }
        }

        if (best == null)
        {
            return new TaskRouteDecision
            {
                MasterPlanner = master,
                TargetAgent = null,
                TaskCategoryLabel = categoryLabel,
                PrimaryTagId = primaryTag,
                SecondaryTagId = secondaryTag,
                RequiresDirectWrite = reqWrite,
                RequiresMultiDir = reqMultiDir,
                RequiresShellBuild = reqShell,
                TaskComplexityTier = complexityTier,
                TaskComplexityReason = complexityReason,
                MasterSelectedModel = masterModel,
                MasterSelectedTier = masterTier,
                WorkTypeId = inferredWorkTypeId,
                WorkTypeLabel = inferredWorkLabel,
                DispatchReason = $"当前在岗后台子 AI 均未勾选「{inferredWorkLabel}」职责，已拦截越权派发并交回主脑直接处理"
            };
        }
        var priTagTitle = reg.Tags.Tags.FirstOrDefault(t => t.Id == primaryTag)?.Name ?? primaryTag;
        var secTagTitle = reg.Tags.Tags.FirstOrDefault(t => t.Id == secondaryTag)?.Name ?? secondaryTag;
        int bestPriStar = best != null && best.TagScores.TryGetValue(primaryTag, out var bps) ? bps : 0;
        int bestSecStar = best != null && best.TagScores.TryGetValue(secondaryTag, out var bss) ? bss : 0;
        var permDesc = best != null
            ? $"直改工程={(best.Permissions.AllowDirectSourceWrite ? "✓" : "×")}·跨目录={(best.Permissions.AllowMultiDir ? "✓" : "×")}·脚本构建={(best.Permissions.AllowShellBuild ? "✓" : "×")}"
            : "";
        var (bestModel, bestTier, bestBadge, _) = ResolveAgentModelAndTier(best, complexityTier, false, inferredWorkTypeId, reg.UserConfig.PreferNativeModelsWhenTied);

        return new TaskRouteDecision
        {
            MasterPlanner = master,
            TargetAgent = best,
            TaskCategoryLabel = categoryLabel,
            PrimaryTagId = primaryTag,
            SecondaryTagId = secondaryTag,
            RequiresDirectWrite = reqWrite,
            RequiresMultiDir = reqMultiDir,
            RequiresShellBuild = reqShell,
            TaskComplexityTier = complexityTier,
            TaskComplexityReason = complexityReason,
            MasterSelectedModel = masterModel,
            MasterSelectedTier = masterTier,
            TargetSelectedModel = bestModel,
            TargetSelectedTier = bestTier,
            TargetTierBadge = bestBadge,
            WorkTypeId = inferredWorkTypeId,
            WorkTypeLabel = inferredWorkLabel,
            DispatchReason = best != null
                ? $"主脑 [{masterName} · {masterModel}] 深度分析「{inferredWorkLabel} / {categoryLabel}」➔ 派发给 [{best.Name}] 落地执行（模型：{bestModel} [{bestBadge}] | {priTagTitle} ★{bestPriStar} · {permDesc}）"
                : "未找到可用执行 AI"
        };
    }
}
