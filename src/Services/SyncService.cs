using System.IO;
using System.Text;
using System.Text.Json;
using AICollabHub.Models;

namespace AICollabHub.Services;

public sealed class SyncService
{
    private const string AnchorStart = "<!-- AI-COLLAB-HUB:START -->";
    private const string AnchorEnd = "<!-- AI-COLLAB-HUB:END -->";

    public sealed class SyncHealthItem
    {
        public string AgentId { get; set; } = "";
        public string AgentName { get; set; } = "";
        public bool IsHealthy { get; set; }
        public string StatusText { get; set; } = "";
        public string Detail { get; set; } = "";
        public int AppliedRulesCount { get; set; }
        public int AppliedSkillsCount { get; set; }
        public int AppliedMemoryCount { get; set; }
        public string MountMechanism { get; set; } = "";
    }

    public sealed class RuleSkillInventoryEntry
    {
        public string Kind { get; set; } = ""; // "rule" | "skill" | "global_rule" | "memory"
        public string KindLabel { get; set; } = "";
        public string IdOrName { get; set; } = "";
        public string Summary { get; set; } = "";
        public string FilePath { get; set; } = "";
        public List<(string AgentName, bool Applied, string How)> AgentApplicationStates { get; set; } = new();
    }

    public List<RuleSkillInventoryEntry> InspectRulesAndSkillsInventory(RegistryService reg, ProjectProfile? project)
    {
        var items = new List<RuleSkillInventoryEntry>();
        if (project == null || string.IsNullOrWhiteSpace(project.PrimaryRoot) || !Directory.Exists(project.PrimaryRoot))
            return items;

        var enabled = reg.UserConfig.Agents.Where(a => a.Enabled).ToList();
        var primaryAgentsMd = Path.Combine(project.PrimaryRoot, "AGENTS.md");
        bool hasAgentsAnchor = File.Exists(primaryAgentsMd) && File.ReadAllText(primaryAgentsMd).Contains(AnchorStart);
        var skillsJson = Path.Combine(project.PrimaryRoot, ".agents", "skills.json");
        bool hasSkillsJson = File.Exists(skillsJson);
        bool hasClaudeMd = File.Exists(Path.Combine(project.PrimaryRoot, "CLAUDE.md"));

        List<(string AgentName, bool Applied, string How)> EvalAgentStates(bool isSkill, bool isGlobalAntigravityOnly = false)
        {
            var states = new List<(string AgentName, bool Applied, string How)>();
            foreach (var a in enabled)
            {
                if (isGlobalAntigravityOnly)
                {
                    bool isAg = a.Id == "antigravity";
                    states.Add((a.Name, isAg, isAg ? "全局 ~/.gemini/config/rules 常驻生效" : "通过项目 AGENTS.md 同等约束生效"));
                    continue;
                }

                var ad = reg.Adapters.Adapters.FirstOrDefault(x => x.Id == a.AdapterId);
                bool applied = false;
                string how = "待同步";

                if (a.Id == "cursor")
                {
                    applied = true;
                    how = isSkill ? "原生 .cursor/skills + AGENTS.md" : "原生 .cursor/rules 自动加载";
                }
                else if (ad?.WritesAgentsSkillsJson == true)
                {
                    applied = isSkill ? (hasSkillsJson || hasAgentsAnchor) : hasAgentsAnchor;
                    how = isSkill ? ".agents/skills.json 零拷贝挂载" : "AGENTS.md + 全局规则挂载";
                }
                else if (ad?.UpdatesAgentsMdAnchor == true)
                {
                    applied = hasAgentsAnchor;
                    how = hasAgentsAnchor ? "AGENTS.md 权威锚点已接入" : "待注入 AGENTS.md 锚点";
                }
                else if (ad?.WritesClaudeMdPointer == true && ad?.MirrorsToBridgeDir == false)
                {
                    applied = hasClaudeMd;
                    how = hasClaudeMd ? "CLAUDE.md 跳板已接入" : "待生成 CLAUDE.md";
                }
                else if (ad?.MirrorsToBridgeDir == true)
                {
                    var sub = !string.IsNullOrWhiteSpace(a.BridgeLocalDir) ? GetBridgeSubDir(a.BridgeLocalDir, project) : "";
                    applied = !string.IsNullOrEmpty(sub) && File.Exists(Path.Combine(sub, "AGENTS.md"));
                    how = applied ? "桥接镜像目录已同步" : "待同步至桥接目录";
                }
                else if (ad?.Id == "capsule_only")
                {
                    applied = true;
                    how = "远程胶囊自动附带";
                }

                states.Add((a.Name, applied, how));
            }
            return states;
        }

        // 1. 扫描项目技能 (.cursor/skills/*/SKILL.md)
        var skillsDir = Path.Combine(project.PrimaryRoot, NormalizeRel(project.SkillsRelDir));
        if (Directory.Exists(skillsDir))
        {
            foreach (var subDir in Directory.GetDirectories(skillsDir).OrderBy(Path.GetFileName))
            {
                var skillFile = Path.Combine(subDir, "SKILL.md");
                if (!File.Exists(skillFile)) continue;
                var dirName = Path.GetFileName(subDir);
                var desc = ExtractMarkdownSummary(skillFile, isSkillYaml: true);
                items.Add(new RuleSkillInventoryEntry
                {
                    Kind = "skill",
                    KindLabel = "⚡ 共享技能 (Skill)",
                    IdOrName = dirName,
                    Summary = desc,
                    FilePath = skillFile,
                    AgentApplicationStates = EvalAgentStates(isSkill: true)
                });
            }
        }

        // 2. 扫描全局常驻规则 (~/.gemini/GEMINI.md 与 ~/.gemini/config/rules/*.md)
        if (!AppPaths.IsIsolatedTest)
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rootGeminiMd = Path.Combine(userHome, ".gemini", "GEMINI.md");
            if (File.Exists(rootGeminiMd))
            {
                items.Add(new RuleSkillInventoryEntry
                {
                    Kind = "global_rule",
                    KindLabel = "🌐 跨项目全局规则",
                    IdOrName = "GEMINI.md (user_global)",
                    Summary = ExtractMarkdownSummary(rootGeminiMd, isSkillYaml: false),
                    FilePath = rootGeminiMd,
                    AgentApplicationStates = EvalAgentStates(isSkill: false, isGlobalAntigravityOnly: false)
                });
            }

            var geminiRulesDir = Path.Combine(userHome, ".gemini", "config", "rules");
            if (Directory.Exists(geminiRulesDir))
            {
                foreach (var gf in Directory.GetFiles(geminiRulesDir, "*.md").OrderBy(Path.GetFileName))
                {
                    items.Add(new RuleSkillInventoryEntry
                    {
                        Kind = "global_rule",
                        KindLabel = "🌐 跨项目全局规则",
                        IdOrName = Path.GetFileName(gf),
                        Summary = ExtractMarkdownSummary(gf, isSkillYaml: false),
                        FilePath = gf,
                        AgentApplicationStates = EvalAgentStates(isSkill: false, isGlobalAntigravityOnly: false)
                    });
                }
            }
        }

        // 3. 扫描项目规则 (.cursor/rules/*.mdc, *.md)
        var rulesDir = Path.Combine(project.PrimaryRoot, NormalizeRel(project.RulesRelDir));
        if (Directory.Exists(rulesDir))
        {
            var ruleFiles = Directory.GetFiles(rulesDir)
                .Where(f => f.EndsWith(".mdc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName);
            foreach (var rf in ruleFiles)
            {
                items.Add(new RuleSkillInventoryEntry
                {
                    Kind = "rule",
                    KindLabel = "📜 项目规则 (Rule)",
                    IdOrName = Path.GetFileName(rf),
                    Summary = ExtractMarkdownSummary(rf, isSkillYaml: true),
                    FilePath = rf,
                    AgentApplicationStates = EvalAgentStates(isSkill: false)
                });
            }
        }

        // 4. 扫描共读经验库 (.cursor/memory/*.md)
        var memDir = Path.Combine(project.PrimaryRoot, NormalizeRel(project.MemoryRelDir));
        if (Directory.Exists(memDir))
        {
            foreach (var mf in Directory.GetFiles(memDir, "*.md").OrderBy(Path.GetFileName))
            {
                items.Add(new RuleSkillInventoryEntry
                {
                    Kind = "memory",
                    KindLabel = "🧠 共读避坑经验",
                    IdOrName = Path.GetFileName(mf),
                    Summary = ExtractMarkdownSummary(mf, isSkillYaml: false),
                    FilePath = mf,
                    AgentApplicationStates = EvalAgentStates(isSkill: false)
                });
            }
        }

        return items;
    }

    private static string ExtractMarkdownSummary(string filePath, bool isSkillYaml)
    {
        try
        {
            var lines = File.ReadLines(filePath, Encoding.UTF8).Take(25).ToList();
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    var d = t["description:".Length..].Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(d)) return TruncateSummary(d, 72);
                }
            }
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (string.IsNullOrWhiteSpace(t) || t == "---" || t.StartsWith("name:") || t.StartsWith("globs:") || t.StartsWith("alwaysApply:"))
                    continue;
                if (t.StartsWith('#'))
                {
                    var heading = t.TrimStart('#', ' ').Trim();
                    if (!string.IsNullOrWhiteSpace(heading)) return TruncateSummary(heading, 72);
                }
                return TruncateSummary(t, 72);
            }
        }
        catch { }
        return "项目协同规范条目";
    }

    private static string TruncateSummary(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public List<SyncHealthItem> InspectSyncHealth(RegistryService reg, ProjectProfile? project)
    {
        var list = new List<SyncHealthItem>();
        if (project == null || string.IsNullOrWhiteSpace(project.PrimaryRoot) || !Directory.Exists(project.PrimaryRoot))
        {
            return list;
        }

        var enabled = reg.UserConfig.Agents.Where(a => a.Enabled).ToList();
        var primaryAgentsMd = Path.Combine(project.PrimaryRoot, "AGENTS.md");
        bool hasAnchor = File.Exists(primaryAgentsMd) && File.ReadAllText(primaryAgentsMd).Contains(AnchorStart);
        var skillsJson = Path.Combine(project.PrimaryRoot, ".agents", "skills.json");
        var inv = InspectRulesAndSkillsInventory(reg, project);
        int totalRules = inv.Count(x => x.Kind == "rule" || x.Kind == "global_rule");
        int totalSkills = inv.Count(x => x.Kind == "skill");
        int totalMem = inv.Count(x => x.Kind == "memory");

        foreach (var a in enabled)
        {
            var adapter = reg.Adapters.Adapters.FirstOrDefault(x => x.Id == a.AdapterId);
            bool ok = true;
            var notes = new List<string>();
            string mech = "AGENTS.md 权威锚点";

            if (a.Id == "cursor")
            {
                mech = "原生 .cursor/rules + .cursor/skills + AGENTS.md";
            }
            else if (adapter?.WritesAgentsSkillsJson == true)
            {
                mech = ".agents/skills.json + 全局规则 + AGENTS.md";
                if (!File.Exists(skillsJson)) { ok = false; notes.Add("缺少 .agents/skills.json 技能挂载指针"); }
                else notes.Add(".agents/skills.json 已就绪");
            }

            if (adapter?.UpdatesAgentsMdAnchor == true)
            {
                if (!hasAnchor) { ok = false; notes.Add("AGENTS.md 尚未注入协同入口锚点"); }
                else notes.Add("AGENTS.md 协同锚点已对齐");

                foreach (var comp in project.CompanionRoots.Where(Directory.Exists))
                {
                    if (!File.Exists(Path.Combine(comp, "AGENTS.md")))
                    {
                        ok = false;
                        notes.Add($"副目录 [{Path.GetFileName(comp)}] 缺少 AGENTS.md 跳板");
                    }
                }
            }

            if (adapter?.WritesClaudeMdPointer == true && adapter.MirrorsToBridgeDir == false)
            {
                mech = "CLAUDE.md 跳板 + AGENTS.md";
                if (!File.Exists(Path.Combine(project.PrimaryRoot, "CLAUDE.md")))
                {
                    ok = false;
                    notes.Add("缺少 CLAUDE.md 规则跳板");
                }
                else notes.Add("CLAUDE.md 跳板已就绪");
            }

            if (adapter?.MirrorsToBridgeDir == true)
            {
                mech = "外部桥接目录镜像同步";
                if (string.IsNullOrWhiteSpace(a.BridgeLocalDir) || !Directory.Exists(a.BridgeLocalDir))
                {
                    ok = false;
                    notes.Add("尚未指定有效的本地桥接同步目录");
                }
                else
                {
                    var sub = GetBridgeSubDir(a.BridgeLocalDir, project);
                    if (!File.Exists(Path.Combine(sub, "AGENTS.md")))
                    {
                        ok = false;
                        notes.Add($"桥接子目录待投送规则 ({sub})");
                    }
                    else notes.Add($"桥接目录已同步 ({sub})");
                }
            }

            if (adapter?.Id == "capsule_only")
            {
                mech = "远程胶囊自动打包";
                notes.Add("胶囊收发模式（导出 REMOTE_PACK.md 时自动附带规则摘要）");
            }

            notes.Insert(0, $"已应用 {totalSkills} 个技能 · {totalRules} 条规则 · {totalMem} 份经验库（挂载方式：{mech}）");

            list.Add(new SyncHealthItem
            {
                AgentId = a.Id,
                AgentName = a.Name,
                IsHealthy = ok,
                StatusText = ok ? $"✓ 已应用 ({totalSkills}技能/{totalRules}规则)" : "待同步",
                Detail = string.Join("；", notes),
                AppliedRulesCount = ok ? totalRules : 0,
                AppliedSkillsCount = ok ? totalSkills : 0,
                AppliedMemoryCount = ok ? totalMem : 0,
                MountMechanism = mech
            });
        }

        return list;
    }

    public string InitializeSharedFoundation(RegistryService reg, ProjectProfile project)
    {
        if (string.IsNullOrWhiteSpace(project.PrimaryRoot))
            throw new InvalidOperationException("请先设置有效的主工程目录。");

        Directory.CreateDirectory(project.PrimaryRoot);
        var rulesDir = Path.Combine(project.PrimaryRoot, NormalizeRel(project.RulesRelDir));
        var skillsDir = Path.Combine(project.PrimaryRoot, NormalizeRel(project.SkillsRelDir));
        var memDir = Path.Combine(project.PrimaryRoot, NormalizeRel(project.MemoryRelDir));
        var collabDir = Path.Combine(project.PrimaryRoot, NormalizeRel(project.CollabRelDir));

        Directory.CreateDirectory(rulesDir);
        Directory.CreateDirectory(skillsDir);
        Directory.CreateDirectory(memDir);
        Directory.CreateDirectory(collabDir);
        Directory.CreateDirectory(Path.Combine(collabDir, "inbox"));
        Directory.CreateDirectory(Path.Combine(collabDir, "sandbox"));
        Directory.CreateDirectory(Path.Combine(collabDir, "archive"));

        var preset = reg.Presets.Presets.FirstOrDefault(p => p.Id == project.PresetId);
        if (preset?.CreateGdignore == true || File.Exists(Path.Combine(project.PrimaryRoot, "project.godot")))
        {
            var gdignore = Path.Combine(collabDir, ".gdignore");
            if (!File.Exists(gdignore)) File.WriteAllText(gdignore, "", new UTF8Encoding(false));
        }

        var pitfallsFile = Path.Combine(memDir, "shared-pitfalls.md");
        if (!File.Exists(pitfallsFile))
        {
            File.WriteAllText(pitfallsFile,
                "# 多 AI 共读经验与避坑账本\n\n" +
                "> 任一 AI 在开发或审查中发现的共性陷阱集中记录于此，一处登记、全员免疫。\n\n" +
                "- 修改正式资源或场景前，先确认无未保存脏状态并建立快照，严禁多工具同时并发写入同一文件。\n",
                new UTF8Encoding(false));
        }

        var agentsMd = Path.Combine(project.PrimaryRoot, "AGENTS.md");
        if (!File.Exists(agentsMd))
        {
            File.WriteAllText(agentsMd,
                $"# {project.Name} · 项目协作总纲\n\n" +
                $"- 规则目录：`{project.RulesRelDir}`\n" +
                $"- 技能目录：`{project.SkillsRelDir}`\n" +
                $"- 共读经验库：`{project.MemoryRelDir}`\n",
                new UTF8Encoding(false));
        }

        return $"已就绪共享底座目录：{rulesDir}、{skillsDir}、{memDir}、{collabDir}";
    }

    public string ExecuteSyncAll(RegistryService reg, ProjectProfile project, CollabBusService bus)
    {
        if (!reg.UserConfig.WorkflowEngineEnabled)
        {
            return DeactivateAndStripAllCollabAnchors(reg, project);
        }

        InitializeSharedFoundation(reg, project);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupSessionDir = Path.Combine(AppPaths.BackupsDir, $"sync_{stamp}");
        Directory.CreateDirectory(backupSessionDir);
        var manifest = new List<BackupRecord>();

        var enabled = reg.UserConfig.Agents.Where(a => a.Enabled).ToList();
        bool needSkillsJson = enabled.Any(a => reg.Adapters.Adapters.FirstOrDefault(x => x.Id == a.AdapterId)?.WritesAgentsSkillsJson == true);
        bool needAgentsAnchor = enabled.Any(a => reg.Adapters.Adapters.FirstOrDefault(x => x.Id == a.AdapterId)?.UpdatesAgentsMdAnchor == true);
        bool needClaudeMd = enabled.Any(a =>
        {
            if (!a.Detected && !string.IsNullOrEmpty(a.DetectorId)) return false;
            var ad = reg.Adapters.Adapters.FirstOrDefault(x => x.Id == a.AdapterId);
            return ad?.WritesClaudeMdPointer == true && !ad.MirrorsToBridgeDir;
        });

        // 1. 生成原生 .agents/skills.json 零拷贝挂载指针（主目录 + 关联副目录）
        if (needSkillsJson)
        {
            WriteSkillsJsonPointer(project.PrimaryRoot, project.SkillsRelDir.Replace('\\', '/'), backupSessionDir, manifest);
            var absMainSkills = Path.GetFullPath(Path.Combine(project.PrimaryRoot, NormalizeRel(project.SkillsRelDir)));
            foreach (var comp in project.CompanionRoots.Where(Directory.Exists))
            {
                var relOrAbs = MakeForwardSlashPath(Path.GetRelativePath(comp, absMainSkills));
                // 若跨盘符或上级路径复杂，Antigravity skills.json 支持以 / 开头的本地路径或相对路径
                if (relOrAbs.StartsWith(".."))
                {
                    WriteSkillsJsonPointer(comp, relOrAbs, backupSessionDir, manifest);
                }
                else
                {
                    WriteSkillsJsonPointer(comp, relOrAbs, backupSessionDir, manifest);
                }
            }
        }

        // 2. 幂等更新主目录 AGENTS.md 协同锚点块 + 副目录跳板
        if (needAgentsAnchor)
        {
            var primaryAgentsMd = Path.Combine(project.PrimaryRoot, "AGENTS.md");
            BackupFileBeforeWrite(primaryAgentsMd, backupSessionDir, manifest);
            var anchorBlock = BuildAgentsMdAnchorBlock(reg, project, enabled);
            UpsertMarkdownAnchor(primaryAgentsMd, anchorBlock);

            foreach (var comp in project.CompanionRoots.Where(Directory.Exists))
            {
                var compAgents = Path.Combine(comp, "AGENTS.md");
                BackupFileBeforeWrite(compAgents, backupSessionDir, manifest);
                var pointerBlock = BuildCompanionJumpBlock(project);
                if (!File.Exists(compAgents))
                {
                    File.WriteAllText(compAgents,
                        $"# 关联工程协作入口\n\n{pointerBlock}\n",
                        new UTF8Encoding(false));
                }
                else
                {
                    UpsertMarkdownAnchor(compAgents, pointerBlock);
                }
            }
        }

        // 3. 按需维护或剥离 CLAUDE.md 跳板（未启用/未安装 Claude 时清理残留锚点）
        var claudeMd = Path.Combine(project.PrimaryRoot, "CLAUDE.md");
        if (needClaudeMd)
        {
            BackupFileBeforeWrite(claudeMd, backupSessionDir, manifest);
            var block = $"{AnchorStart}\n## 共享规则与技能入口\n请严格先读取同目录 `AGENTS.md`、规则目录 `{project.RulesRelDir}`、技能目录 `{project.SkillsRelDir}`、经验库 `{project.MemoryRelDir}` 及当前任务活卡 `{project.CollabRelDir}/TASK.md`。\n{AnchorEnd}";
            if (!File.Exists(claudeMd)) File.WriteAllText(claudeMd, "# Claude 协作引导\n\n" + block + "\n", new UTF8Encoding(false));
            else UpsertMarkdownAnchor(claudeMd, block);
        }
        else if (File.Exists(claudeMd))
        {
            RemoveMarkdownAnchor(claudeMd);
        }

        // 4. 同步全局硬闸门摘要至本机 ~/.gemini/config/rules/（若启用了 Antigravity 且项目有硬闸门规则）
        if (enabled.Any(a => a.Id == "antigravity"))
        {
            SyncAntigravityGlobalGateDigest(project, backupSessionDir, manifest);
        }

        // 4b. 为本机已检出的 Codex (含待命未启用状态) 预同步 ~/.codex/AGENTS.md 共享规则与技能入口，确保随时启用即刻生效
        var codexStandby = reg.UserConfig.Agents.FirstOrDefault(a => a.Id == "codex_cli" && a.Detected);
        if (codexStandby != null && !AppPaths.IsIsolatedTest)
        {
            try
            {
                var codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                if (Directory.Exists(codexHome))
                {
                    var codexGlobalAgentsMd = Path.Combine(codexHome, "AGENTS.md");
                    BackupFileBeforeWrite(codexGlobalAgentsMd, backupSessionDir, manifest);
                    var mainNorm = MakeForwardSlashPath(project.PrimaryRoot);
                    var codexBlock = $"{AnchorStart}\n" +
                                     $"## 多 AI 协同中心 · Codex 工作流预接通配置\n" +
                                     $"- 当前绑定主工程：`{mainNorm}` (`{project.Name}`)\n" +
                                     $"- 权威项目总纲：`{mainNorm}/AGENTS.md`；规则目录：`{mainNorm}/{project.RulesRelDir}`；技能目录：`{mainNorm}/{project.SkillsRelDir}`；共读经验库：`{mainNorm}/{project.MemoryRelDir}`\n" +
                                     $"- 协同任务活卡：`{mainNorm}/{project.CollabRelDir}/TASK.md`；产出回收目录：`{mainNorm}/{project.CollabRelDir}/inbox/`\n" +
                                     $"- 对话记录索引库：`F:/AIChat/codex`\n" +
                                     $"{AnchorEnd}";
                    if (!File.Exists(codexGlobalAgentsMd))
                        File.WriteAllText(codexGlobalAgentsMd, "# Codex 全局协同总纲\n\n" + codexBlock + "\n", new UTF8Encoding(false));
                    else
                        UpsertMarkdownAnchor(codexGlobalAgentsMd, codexBlock);
                }
            }
            catch { }
        }

        // 5. 写入项目内 work/collab/ROSTER.json 与 TASK.md
        bus.EnsureTaskCard(project);
        var rosterSnapPath = Path.Combine(project.PrimaryRoot, NormalizeRel(project.CollabRelDir), "ROSTER.json");
        var standbyConfigured = reg.UserConfig.Agents.Where(a => !a.Enabled && a.Detected && a.Id == "codex_cli").ToList();
        RegistryService.WriteJsonAtomic(rosterSnapPath, new
        {
            synced_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            project_name = project.Name,
            fallback_builder_id = project.FallbackBuilderId,
            enabled_agents = enabled.Select(a => new
            {
                a.Id,
                a.Name,
                a.Category,
                a.AdapterId,
                a.Permissions,
                a.TagScores
            }),
            standby_configured_agents = standbyConfigured.Select(a => new
            {
                a.Id,
                a.Name,
                status = "standby_disabled",
                a.Category,
                a.AdapterId,
                a.RunnerMode,
                a.GuiExePath,
                a.Permissions,
                a.TagScores
            })
        });

        // 6. 投送到外部 VPS / 共享桥接目录
        int bridgeCount = 0;
        foreach (var bridgeAgent in enabled.Where(a =>
                     reg.Adapters.Adapters.FirstOrDefault(x => x.Id == a.AdapterId)?.MirrorsToBridgeDir == true))
        {
            if (!string.IsNullOrWhiteSpace(bridgeAgent.BridgeLocalDir) && Directory.Exists(bridgeAgent.BridgeLocalDir))
            {
                MirrorToBridgeFolder(bridgeAgent.BridgeLocalDir, project);
                bridgeCount++;
            }
        }

        // 更新同步时间戳与保存备份清单（只保留最新 3 个备份目录）
        var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var a in enabled) a.LastSyncedAt = nowStr;
        if (codexStandby != null) codexStandby.LastSyncedAt = nowStr;
        reg.SaveUserConfig();

        RegistryService.WriteJsonAtomic(Path.Combine(backupSessionDir, "manifest.json"), manifest);
        AppPaths.PruneDirectoryKeepLatest(AppPaths.BackupsDir, 3);

        return $"已同步 {enabled.Count} 个启用工具（含 {bridgeCount} 个外部桥接目录），原文件已备份至 {Path.GetFileName(backupSessionDir)}（仅留最新 3 份）。";
    }

    public string RestoreLatestBackup()
    {
        if (!Directory.Exists(AppPaths.BackupsDir)) return "暂无可用备份记录。";
        var latest = new DirectoryInfo(AppPaths.BackupsDir)
            .GetDirectories("sync_*")
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest == null) return "暂无可用备份记录。";

        var mfPath = Path.Combine(latest.FullName, "manifest.json");
        if (!File.Exists(mfPath)) return "最新备份目录缺少 manifest.json。";

        var records = JsonSerializer.Deserialize<List<BackupRecord>>(File.ReadAllText(mfPath), RegistryService.JsonOpts) ?? new();
        int restored = 0;
        foreach (var r in records)
        {
            try
            {
                if (!r.ExistedBefore)
                {
                    if (File.Exists(r.OriginalPath)) File.Delete(r.OriginalPath);
                }
                else if (File.Exists(r.BackupFilePath))
                {
                    var dir = Path.GetDirectoryName(r.OriginalPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.Copy(r.BackupFilePath, r.OriginalPath, overwrite: true);
                }
                restored++;
            }
            catch
            {
                // 忽略单文件占用
            }
        }
        return $"已从 {latest.Name} 恢复 {restored} 个配置文件至同步前状态。";
    }

    private static void WriteSkillsJsonPointer(string rootDir, string relSkillsPath, string backupDir, List<BackupRecord> manifest)
    {
        var agentsDir = Path.Combine(rootDir, ".agents");
        Directory.CreateDirectory(agentsDir);
        var skillsJson = Path.Combine(agentsDir, "skills.json");
        BackupFileBeforeWrite(skillsJson, backupDir, manifest);
        var payload = new
        {
            entries = new[]
            {
                new { path = relSkillsPath }
            }
        };
        RegistryService.WriteJsonAtomic(skillsJson, payload);
    }

    private static void SyncAntigravityGlobalGateDigest(ProjectProfile project, string backupDir, List<BackupRecord> manifest)
    {
        if (AppPaths.IsIsolatedTest) return;

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var geminiDir = Path.Combine(userHome, ".gemini");
        var geminiRulesDir = Path.Combine(geminiDir, "config", "rules");
        Directory.CreateDirectory(geminiRulesDir);

        var hubLauncher = ResolveStableHubLauncherPath();

        var target = Path.Combine(geminiRulesDir, "shared-hard-gates.md");
        BackupFileBeforeWrite(target, backupDir, manifest);
        var sb = new StringBuilder();
        sb.AppendLine("# 全局硬闸门与跨项目多 AI 总包工头强制规程（由多 AI 协同中心对齐）");
        sb.AppendLine();
        sb.AppendLine("## 一、全局交付硬闸门");
        sb.AppendLine("- **先验后交**：做完必须自己运行真实测试或查看真实截图验证，未验证或仍有问题绝不称完成。");
        sb.AppendLine("- **不抢键鼠**：能后台、无窗口脚本或进程内注入/MCP 完成的操作，严禁调用物理键鼠自动化抢占用户桌面。");
        sb.AppendLine("- **省 Token 守质量**：按任务索引按需读取对应专题规则与 Skill，不盲目全盘输出，所有共享规则与技能以项目唯一真身为准。");
        sb.AppendLine();
        sb.AppendLine("## 二、【全局强制铁律 · 新旧对话一律执行】主脑只拆解出方案与终验 ➔ 派给子 AI（Cursor/Grok）实操与自检");
        sb.AppendLine("- **【抗旧对话惯性 · 强制工具禁令】**：无论用户在哪个 AI 工具（Antigravity / Cursor / Grok Bot）、不论是**新开对话还是已跑几十轮的旧对话**（哪怕前文历史里主脑曾直接读改过业务源码），从当前回合起必须立即彻底停手！**严禁主脑调用 `edit_file`、`write_file`、`replace_file_content`、`multi_replace_file_content` 直接修改业务工程文件（如 `.java` / `.cs` / `.gd` / `.tscn` / `.xml` 等），严禁在未派单前先用 `read_file`/`view_file`/`grep_search` 通读多个业务源码代替子 AI 排障，严禁只用内置 subagent 应付！**");
        sb.AppendLine("- **【省主脑额度 · 第一步必须派单】**：收到任何开发、改代码/UI/Shader/预制体、排障查 Bug、配表或测试构建需求，主脑准备动手前先自检 1 秒——只要用户在**当前这一次**发任务时未显式要求「由你本人直接改」，**第一步必须直接调用下方 `dispatch` 派给 `Cursor` / `Grok Bot` 去读源码、定位、修改并编译自检**！");
        sb.AppendLine("- **【单次授权不延续 · 默认一律走协同】**：即使某一轮用户曾显式指定「由你本人直接改」，该授权也**仅限那一次任务生效，绝不延续到后续轮次或新任务**；下一条未特别说明的需求必须立即自动恢复为走 AI 协同派单。");
        sb.AppendLine("- **主脑严格验收与打回重做闭环**：其他 AI 执行完毕后交由主脑验收（核验 `LATEST.diff` 实际文件改动、语法构建与画面效果）；若未通过，继续调用 `dispatch` 打回同一对话框重修直至达标后向用户交付。");
        sb.AppendLine("- **每次接到实质性任务时，必须通过 `run_command` 调用「多 AI 协同中心」执行实操派单与验收闭环**：");
        sb.AppendLine("  1. **查找或读取外部 AI 在当前项目下的对话框内容**：");
        sb.AppendLine("     ```powershell");
        sb.AppendLine($"     powershell -NoProfile -ExecutionPolicy Bypass -File \"{hubLauncher}\" sessions --project-dir \"<当前对话所在项目根目录>\"");
        sb.AppendLine($"     powershell -NoProfile -ExecutionPolicy Bypass -File \"{hubLauncher}\" read-session --project-dir \"<当前对话所在项目根目录>\" --agent auto --session auto");
        sb.AppendLine("     ```");
        sb.AppendLine("  2. **由主脑深度分析后，自动适配模型与高中低档位，派发实操任务并执行主脑验收打回闭环**：");
        sb.AppendLine("     ```powershell");
        sb.AppendLine($"     powershell -NoProfile -ExecutionPolicy Bypass -File \"{hubLauncher}\" dispatch --project-dir \"<当前对话所在项目根目录>\" --agent auto --session auto --task \"请直接落地修改工程文件完成：<具体实操子任务说明与验收标准>\"");
        sb.AppendLine("     ```");
        sb.AppendLine("     *（若需强制新建对话框，将 `--session auto` 改为 `--session new`；若主脑复核发现仍有瑕疵，直接再次运行 `dispatch` 打回同一对话框继续修改直至彻底完成）*");
        sb.AppendLine("- **自动拉起窗口与实时状态**：上述命令会自动将当前项目纳管、自动匹配模型与高/中/低档位、启动目标 AI 桌面窗口与协同中心实时看板，实时进度写入 `<项目根目录>/work/collab/LIVE_STATUS.md`，最终反馈与验收回执写入 `<项目根目录>/work/collab/inbox/`。");
        sb.AppendLine("- **主脑终验与交付**：命令返回后，主脑核对 `INBOX_FILE` 与实际工程文件改动，确认 100% 达标后向用户一站式交付。");
        File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false));

        // 同步注入 ~/.gemini/GEMINI.md（Antigravity 顶层 <RULE[user_global]> 的直接加载源，确保所有新旧对话框 100% 强制遵循）
        var rootGeminiMd = Path.Combine(geminiDir, "GEMINI.md");
        BackupFileBeforeWrite(rootGeminiMd, backupDir, manifest);
        if (!File.Exists(rootGeminiMd))
        {
            File.WriteAllText(rootGeminiMd,
                "# 语言偏好设置 (Language Preference)\n\n" +
                "- 请始终默认使用**简体中文**与用户进行交流、回答问题和解释代码。\n" +
                "- 生成的所有文档、任务计划（Implementation Plan）、任务清单（Task List）、总结报告（Walkthrough）等 Artifacts 默认使用**简体中文**编写。\n" +
                "- 代码中的标识符、专有技术术语、命令行指令及配置项保留英文原样，代码注释优先使用简体中文（除非项目另有规范）。\n",
                new UTF8Encoding(false));
        }

        var globalBlock = new StringBuilder();
        globalBlock.AppendLine(AnchorStart);
        globalBlock.AppendLine("# 【全局强制铁律 · 新旧对话均生效】主脑只拆解出方案与终验 ➔ 一律派给子 AI（Cursor/Grok）实操与自检");
        globalBlock.AppendLine();
        globalBlock.AppendLine("- **【抗旧对话惯性 · 强制工具禁令】**：");
        globalBlock.AppendLine("  无论当前是**新开对话还是已连续执行多轮的旧对话**（哪怕旧对话前文历史里主脑自己曾直接读过/改过业务源码），无论当前工程（如 `XX`、`NX`、`北大肿瘤医院`、`TBH`、`VPS` 或任意项目目录）是否已手动同步规则：");
        globalBlock.AppendLine("  1. **【强制自检闸门 · 单次授权不延续】只要用户提出开发、改代码/UI/Shader/预制体、排障查 Bug、配表、测试构建或作图需求，除非用户在「当前这一次」任务中显式指定「由你本人直接改」（且该授权仅限当次、绝不延续到下一条需求），否则绝对禁止调用 `edit_file`、`write_file`、`replace_file_content`、`multi_replace_file_content` 修改业务文件，也禁止在派单前连续调用 `read_file`/`view_file`/`grep_search` 通读业务源码！**");
        globalBlock.AppendLine("  2. **【第一步必须派单】收到需求后，直接按需求目标与验收标准第一时间调用 `run_command` 执行下方 `dispatch`，把「读源码 + 定位 + 修改 + 编译/自检」整包派给 `Cursor` / `Grok Bot`（严禁只用内置 `invoke_subagent` 代替外部 AI）！**");
        globalBlock.AppendLine("  3. **【先验后交】** 执行 AI 完成后，由**主脑 AI 严格验收 `work/collab/inbox/` 产出与 `LATEST.diff` 改动**（仅验收或复杂架构设计时允许按需核对），未达标继续调用 `dispatch` 打回重修，彻底通过后再向用户交付。");
        globalBlock.AppendLine("- **标准强制执行动作（在任务执行中直接运行以下命令）**：");
        globalBlock.AppendLine($"  1. **主脑派发实操任务（自动按当前团队方案匹配 Cursor/Grok 与高中低档位 + 后台零键鼠占用）**：");
        globalBlock.AppendLine($"     `powershell -NoProfile -ExecutionPolicy Bypass -File \"{hubLauncher}\" dispatch --project-dir \"<当前工作区或具体子工程目录>\" --agent auto --session auto --task \"请直接落地修改工程文件并完成自检：<具体实操子任务说明与验收标准>\"`");
        globalBlock.AppendLine($"  2. **查看/读取当前项目在其他外部 AI 工具下的对应对话框（按需）**：");
        globalBlock.AppendLine($"     `powershell -NoProfile -ExecutionPolicy Bypass -File \"{hubLauncher}\" sessions --project-dir \"<当前项目根目录>\"`");
        globalBlock.AppendLine($"     `powershell -NoProfile -ExecutionPolicy Bypass -File \"{hubLauncher}\" read-session --project-dir \"<当前项目根目录>\" --agent auto --session auto`");
        globalBlock.AppendLine($"  3. **主脑验收与最终交付**：读取 `<项目根目录>/work/collab/inbox/` 产出与 `<项目根目录>/work/collab/LATEST.diff` 实际改动；若仍有未竟之处继续调用 `dispatch` 打回重修，彻底完成后向用户交付。");
        globalBlock.Append(AnchorEnd);
        UpsertMarkdownAnchor(rootGeminiMd, globalBlock.ToString());
    }

    private static void MirrorToBridgeFolder(string bridgeBaseDir, ProjectProfile project)
    {
        var sub = GetBridgeSubDir(bridgeBaseDir, project);
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(sub, "in"));
        Directory.CreateDirectory(Path.Combine(sub, "outbox"));

        var srcRules = Path.Combine(project.PrimaryRoot, NormalizeRel(project.RulesRelDir));
        var srcSkills = Path.Combine(project.PrimaryRoot, NormalizeRel(project.SkillsRelDir));
        var srcMem = Path.Combine(project.PrimaryRoot, NormalizeRel(project.MemoryRelDir));
        var srcCollab = Path.Combine(project.PrimaryRoot, NormalizeRel(project.CollabRelDir));

        if (Directory.Exists(srcRules)) CopyDirectorySmallText(srcRules, Path.Combine(sub, "rules"));
        if (Directory.Exists(srcSkills)) CopyDirectorySmallText(srcSkills, Path.Combine(sub, "skills"));
        if (Directory.Exists(srcMem)) CopyDirectorySmallText(srcMem, Path.Combine(sub, "memory"));

        foreach (var fn in new[] { "TASK.md", "LATEST.diff", "ROSTER.json", "SCHEMA_DIGEST.md" })
        {
            var sf = Path.Combine(srcCollab, fn);
            if (File.Exists(sf)) File.Copy(sf, Path.Combine(sub, "in", fn), overwrite: true);
        }

        var readme = "# 远程 VPS 桥接工作流指引\n\n" +
                     "- 共享规则位于 `./rules/`，技能位于 `./skills/`，共读经验位于 `./memory/`。\n" +
                     "- 当前任务单位于 `./in/TASK.md`，最近代码对比位于 `./in/LATEST.diff`。\n" +
                     "- 请将产出的设计方案、审查意见或修改后的代码文件写入 `./outbox/`，由本地守门槽打快照安全合入并完成实机验收。\n";
        File.WriteAllText(Path.Combine(sub, "AGENTS.md"), readme, new UTF8Encoding(false));
    }

    private static string GetBridgeSubDir(string bridgeBaseDir, ProjectProfile project)
    {
        var safeName = string.Concat(project.Name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "project";
        return Path.Combine(bridgeBaseDir, $"{safeName}_collab");
    }

    private static void CopyDirectorySmallText(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.Length > 512 * 1024) continue; // 只同步规则/技能文本，跳过大体积二进制
            var rel = Path.GetRelativePath(srcDir, file);
            var destFile = Path.Combine(dstDir, rel);
            var parent = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, destFile, overwrite: true);
        }
    }

    private static string BuildAgentsMdAnchorBlock(RegistryService reg, ProjectProfile project, List<AgentRosterEntry> enabled)
    {
        var hubLauncher = ResolveStableHubLauncherPath();
        var mainNorm = MakeForwardSlashPath(project.PrimaryRoot);
        var master = RoutingService.ResolveMasterPlanner(reg, project);
        var masterName = master?.Name ?? "Antigravity";
        var sb = new StringBuilder();
        sb.AppendLine(AnchorStart);
        sb.AppendLine("## 多 AI 协同总线与主脑实操验收闭环入口（由多 AI 协同中心维护）");
        sb.AppendLine($"- **当前唯一任务活卡**：`{project.CollabRelDir}/TASK.md`；最近行级变更单：`{project.CollabRelDir}/LATEST.diff`；在岗花名册：`{project.CollabRelDir}/ROSTER.json`。");
        sb.AppendLine($"- **当前统领主脑 AI**：**{masterName}**（负责深度分析问题、自动适配各 AI 的模型与高/中/低档位、派发实操修改任务、严格验收并在不合格时打回重做直至彻底完成）。");
        sb.AppendLine($"- **共享技能直达目录**：`{project.SkillsRelDir}`（遇到对应场景须先打开对应子目录下的 `SKILL.md` 执行）。");
        sb.AppendLine($"- **共读错题与经验库**：`{project.MemoryRelDir}`（一处记坑、全员免疫）。");
        sb.AppendLine($"- **【强制默认铁律】全入口汇总主脑 ➔ 派给其他 AI 实操改工程 ➔ 主脑循环验收打回 ➔ 最终交付**：");
        sb.AppendLine($"  无论用户在哪个 AI 工具发起任务，均通过 `{project.SkillsRelDir}/multi-ai-auto-dispatch/SKILL.md` 调用 `powershell -NoProfile -ExecutionPolicy Bypass -File \"{hubLauncher}\" dispatch --project-dir \"{mainNorm}\" --agent auto --session auto --task \"请直接落地修改工程文件完成：...\"`，由主脑自动匹配最适合的执行 AI 与其高/中/低档位模型去直接动手改代码/改设置/改预制体，执行后由主脑验收（不合格自动打回重做），彻底完成后再向用户交付！");
        sb.AppendLine("- **当前已启用协同成员、权限与三档模型配置**：");
        foreach (var a in enabled)
        {
            CloudRecommendationService.EnsureAgentModelTiersPopulated(a);
            var writeMode = a.Permissions.AllowDirectSourceWrite ? "允许快照护航直接改工程文件" : $"只写 `{project.CollabRelDir}/TASK.md` 或 `inbox/`，禁直改正式资源";
            var modelNote = $"[高档:{a.HighTierModel} | 中档:{a.MediumTierModel} | 低档:{a.LowTierModel}]";
            sb.AppendLine($"  - **{a.Name}**：{writeMode} · {modelNote}");
        }
        sb.Append(AnchorEnd);
        return sb.ToString();
    }

    private static string BuildCompanionJumpBlock(ProjectProfile project)
    {
        var hubLauncher = ResolveStableHubLauncherPath();
        var mainNorm = MakeForwardSlashPath(project.PrimaryRoot);
        return $"{AnchorStart}\n" +
               "## 跨目录主工程共享规则与强制协同入口\n" +
               $"- 主工程权威规则：`{mainNorm}/{project.RulesRelDir}` 与 `{mainNorm}/AGENTS.md`\n" +
               $"- 共享技能目录：`{mainNorm}/{project.SkillsRelDir}`；共读经验库：`{mainNorm}/{project.MemoryRelDir}`\n" +
               $"- 协同活任务卡：`{mainNorm}/{project.CollabRelDir}/TASK.md`\n" +
               $"- **【强制默认铁律】**：在本工程或关联工程接到实质性开发/优化/分析/审查任务时，严禁只靠内置 subagent 单干，必须通过 `run_command` 调用 `powershell -NoProfile -ExecutionPolicy Bypass -File \"{hubLauncher}\" dispatch --project-dir \"{mainNorm}\" --agent auto --session auto --task \"...\"` 调度本机外部 AI 工具（Codex / Grok Bot / Cursor）并从 `{mainNorm}/{project.CollabRelDir}/inbox/` 收回结果。\n" +
               $"{AnchorEnd}";
    }

    /// <summary>
    /// 当用户关闭「工作流总开关」或退出本软件（开启了仅软件运行时生效）时，
    /// 彻底卸载并剥离所有写入到全局/项目的 AI-COLLAB-HUB 协同锚点，使所有 AI 工具完全恢复未安装本软件前的原生独立对话状态。
    /// </summary>
    public string DeactivateAndStripAllCollabAnchors(RegistryService reg, ProjectProfile? activeProject = null)
    {
        int strippedCount = 0;

        // 1. 剥离全局 ~/.gemini/GEMINI.md、删除 shared-hard-gates.md、剥离 ~/.codex/AGENTS.md
        if (!AppPaths.IsIsolatedTest)
        {
            try
            {
                var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var rootGeminiMd = Path.Combine(userHome, ".gemini", "GEMINI.md");
                if (RemoveMarkdownAnchor(rootGeminiMd)) strippedCount++;

                var hardGateRule = Path.Combine(userHome, ".gemini", "config", "rules", "shared-hard-gates.md");
                if (File.Exists(hardGateRule))
                {
                    File.Delete(hardGateRule);
                    strippedCount++;
                }

                var codexGlobalAgents = Path.Combine(userHome, ".codex", "AGENTS.md");
                if (RemoveMarkdownAnchor(codexGlobalAgents)) strippedCount++;
            }
            catch { }
        }

        // 2. 剥离所有已纳管项目的 AGENTS.md、CLAUDE.md 及关联副目录 AGENTS.md 中的协同锚点块
        var allProjs = new List<ProjectProfile>(reg.UserConfig.Projects);
        if (activeProject != null && allProjs.All(p => p.Id != activeProject.Id))
        {
            allProjs.Add(activeProject);
        }

        foreach (var p in allProjs)
        {
            if (string.IsNullOrWhiteSpace(p.PrimaryRoot) || !Directory.Exists(p.PrimaryRoot)) continue;
            try
            {
                if (RemoveMarkdownAnchor(Path.Combine(p.PrimaryRoot, "AGENTS.md"))) strippedCount++;
                if (RemoveMarkdownAnchor(Path.Combine(p.PrimaryRoot, "CLAUDE.md"))) strippedCount++;
                foreach (var comp in p.CompanionRoots.Where(Directory.Exists))
                {
                    if (RemoveMarkdownAnchor(Path.Combine(comp, "AGENTS.md"))) strippedCount++;
                }

                var rosterPath = Path.Combine(p.PrimaryRoot, NormalizeRel(p.CollabRelDir), "ROSTER.json");
                if (File.Exists(rosterPath))
                {
                    RegistryService.WriteJsonAtomic(rosterPath, new
                    {
                        synced_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        project_name = p.Name,
                        workflow_engine_enabled = false,
                        note = "多 AI 协同工作流已停用或软件已退出，当前各 AI 处于原生独立工作模式"
                    });
                }
            }
            catch { }
        }

        return $"已停用多 AI 协同工作流并清理 {strippedCount} 处协同锚点：现在您在任何 AI 工具中对话均由其独立执行（如同未开启本工具）。";
    }

    private static bool RemoveMarkdownAnchor(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            var existing = File.ReadAllText(filePath, Encoding.UTF8);
            int s = existing.IndexOf(AnchorStart, StringComparison.Ordinal);
            int e = existing.IndexOf(AnchorEnd, StringComparison.Ordinal);
            if (s < 0 || e <= s) return false;

            var cleaned = (existing[..s].TrimEnd() + "\n" + existing[(e + AnchorEnd.Length)..].TrimStart()).Trim();
            File.WriteAllText(filePath, string.IsNullOrWhiteSpace(cleaned) ? "" : cleaned + "\n", new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void UpsertMarkdownAnchor(string filePath, string newBlock)
    {
        var existing = File.Exists(filePath) ? File.ReadAllText(filePath, Encoding.UTF8) : "";
        int s = existing.IndexOf(AnchorStart, StringComparison.Ordinal);
        int e = existing.IndexOf(AnchorEnd, StringComparison.Ordinal);
        string updated;
        if (s >= 0 && e > s)
        {
            updated = existing[..s] + newBlock + existing[(e + AnchorEnd.Length)..];
        }
        else
        {
            updated = existing.TrimEnd() + "\n\n" + newBlock + "\n";
        }
        File.WriteAllText(filePath, updated, new UTF8Encoding(false));
    }

    private static void BackupFileBeforeWrite(string targetPath, string backupDir, List<BackupRecord> manifest)
    {
        if (manifest.Any(m => string.Equals(m.OriginalPath, targetPath, StringComparison.OrdinalIgnoreCase)))
            return;

        bool exists = File.Exists(targetPath);
        string bakFile = "";
        if (exists)
        {
            bakFile = Path.Combine(backupDir, $"{manifest.Count:D2}_{Path.GetFileName(targetPath)}.bak");
            File.Copy(targetPath, bakFile, overwrite: true);
        }
        manifest.Add(new BackupRecord
        {
            OriginalPath = targetPath,
            ExistedBefore = exists,
            BackupFilePath = bakFile
        });
    }

    public static string ResolveStableHubLauncherPath(bool standaloneSingleFile = false)
    {
        if (standaloneSingleFile)
            return MakeForwardSlashPath(EnsurePortableHubLauncherScript());

        var dir = new DirectoryInfo(AppPaths.AppBaseDir);
        while (dir != null)
        {
            var launcher = Path.Combine(dir.FullName, "启动协同中心.ps1");
            var outputsSub = Path.Combine(dir.FullName, "outputs");
            if (File.Exists(launcher) && Directory.Exists(outputsSub))
                return MakeForwardSlashPath(launcher);
            dir = dir.Parent;
        }

        var localLauncher = Path.Combine(AppPaths.AppBaseDir, "启动协同中心.ps1");
        if (File.Exists(localLauncher)) return MakeForwardSlashPath(localLauncher);

        // 独立单文件模式：在用户数据目录生成/刷新便携启动脚本（调用当前运行的 exe）
        return MakeForwardSlashPath(EnsurePortableHubLauncherScript());
    }

    /// <summary>
    /// 在 UserDataDir 生成便携版「启动协同中心.ps1」：无参启动界面，有参透传 --cli。
    /// </summary>
    private static string EnsurePortableHubLauncherScript()
    {
        var path = Path.Combine(AppPaths.UserDataDir, "启动协同中心.ps1");
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            exe = Path.Combine(AppPaths.AppBaseDir, "AI协同中心.exe");
        exe = Path.GetFullPath(exe);
        var exeLiteral = exe.Replace("'", "''");
        var script =
            "$ErrorActionPreference = 'Stop'\n" +
            "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}\n" +
            "$CliArgs = @($args)\n" +
            $"$exePath = '{exeLiteral}'\n" +
            "if (-not (Test-Path -LiteralPath $exePath)) {\n" +
            "    throw \"未找到 AI协同中心.exe：$exePath\"\n" +
            "}\n" +
            "if ($CliArgs.Count -gt 0) {\n" +
            "    $pass = @('--cli') + @($CliArgs | Where-Object { $_ -ne '-Cli' -and $_ -ne '--cli' })\n" +
            "    & $exePath @pass\n" +
            "    exit $LASTEXITCODE\n" +
            "}\n" +
            "Start-Process -FilePath $exePath\n";
        try
        {
            Directory.CreateDirectory(AppPaths.UserDataDir);
            bool needWrite = true;
            if (File.Exists(path))
            {
                try
                {
                    var existing = File.ReadAllText(path);
                    if (existing.Contains(exe, StringComparison.OrdinalIgnoreCase) &&
                        existing.Contains("--cli", StringComparison.Ordinal))
                        needWrite = false;
                }
                catch { needWrite = true; }
            }
            if (needWrite)
                File.WriteAllText(path, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // 写失败仍返回目标路径，调用方可感知
        }
        return path;
    }

    private static string NormalizeRel(string rel) => rel.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
    private static string MakeForwardSlashPath(string p) => p.Replace('\\', '/');

    public sealed class BackupRecord
    {
        public string OriginalPath { get; set; } = "";
        public bool ExistedBefore { get; set; }
        public string BackupFilePath { get; set; } = "";
    }
}
