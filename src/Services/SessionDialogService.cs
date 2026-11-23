using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AICollabHub.Models;

namespace AICollabHub.Services;

/// <summary>
/// 跨 AI 工具项目对话框（Session / Thread / Transcript）寻址、读取与续接服务。
/// 支持自动在 Codex / Grok Bot / Cursor / 本地会话存档中找到当前项目对应的对话框读取上下文或继续分配工作；
/// 若当前项目尚无对话框，则支持自动在该项目目录下新建专属对话框并记录绑定。
/// </summary>
public sealed class SessionDialogService
{
    private static readonly ConcurrentDictionary<string, (long WriteTicks, long Length, ExternalDialogSessionInfo Info)> SessionFileCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string ExtractCleanDialogTitle(string? rawText, int maxLen = 40)
        => AutoDispatchService.ExtractCleanTaskBrief(rawText, maxLen);

    private static bool TryGetCachedSessionInfo(string filePath, out FileInfo? fi, out ExternalDialogSessionInfo? cachedInfo)
    {
        fi = null;
        cachedInfo = null;
        try
        {
            fi = new FileInfo(filePath);
            if (!fi.Exists) return false;
            if (SessionFileCache.TryGetValue(filePath, out var hit) &&
                hit.WriteTicks == fi.LastWriteTimeUtc.Ticks &&
                hit.Length == fi.Length)
            {
                cachedInfo = hit.Info;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static void SaveCachedSessionInfo(string filePath, FileInfo? fi, ExternalDialogSessionInfo info)
    {
        try
        {
            if (fi == null) return;
            SessionFileCache[filePath] = (fi.LastWriteTimeUtc.Ticks, fi.Length, info);
        }
        catch { }
    }

    private static IEnumerable<string> ReadLinesHeadTailSafe(string filePath, long fileLength, int headBytes = 96 * 1024, int tailBytes = 384 * 1024)
    {
        var list = new List<string>();
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fileLength <= headBytes + tailBytes + 32 * 1024)
            {
                using var sr = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = sr.ReadLine()) != null) list.Add(line);
                return list;
            }

            var headBuf = new byte[headBytes];
            int hRead = fs.Read(headBuf, 0, headBytes);
            var headTxt = Encoding.UTF8.GetString(headBuf, 0, hRead);
            var headLines = headTxt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            // 丢弃头部最后一个可能截断的行
            int headKeep = headLines.Length > 1 ? headLines.Length - 1 : headLines.Length;
            for (int i = 0; i < headKeep; i++) list.Add(headLines[i]);

            fs.Seek(-tailBytes, SeekOrigin.End);
            var tailBuf = new byte[tailBytes];
            int tRead = fs.Read(tailBuf, 0, tailBytes);
            var tailTxt = Encoding.UTF8.GetString(tailBuf, 0, tRead);
            var tailLines = tailTxt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            // 丢弃尾部第一行（半行对齐）
            for (int i = 1; i < tailLines.Length; i++) list.Add(tailLines[i]);
        }
        catch { }
        return list;
    }
    public sealed class DesktopVisibleSessionIndex
    {
        public string CliSessionId { get; set; } = "";
        public string DesktopTitleHint { get; set; } = "";
        public string TaskBrief { get; set; } = "";
        public string SyncedAt { get; set; } = "";
        public bool RequireDesktopVisible { get; set; } = true;
    }

    public sealed class ProjectSessionRegistry
    {
        public string ProjectName { get; set; } = "";
        public string ProjectRoot { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public Dictionary<string, string> ActiveSessionByAgent { get; set; } = new();
        /// <summary>CLI 会话 ↔ 桌面侧栏可见索引（官方 CLI 与桌面 UIA 隔离时用于对齐）。</summary>
        public Dictionary<string, DesktopVisibleSessionIndex> DesktopVisibleByAgent { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public string GetSessionRegistryPath(ProjectProfile project)
    {
        var collabDir = Path.Combine(project.PrimaryRoot, project.CollabRelDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(collabDir);
        return Path.Combine(collabDir, "ACTIVE_SESSIONS.json");
    }

    public ProjectSessionRegistry LoadProjectSessionRegistry(ProjectProfile project)
    {
        var path = GetSessionRegistryPath(project);
        if (File.Exists(path))
        {
            try
            {
                var txt = File.ReadAllText(path, Encoding.UTF8);
                var obj = JsonSerializer.Deserialize<ProjectSessionRegistry>(txt, RegistryService.JsonOpts);
                if (obj != null) return obj;
            }
            catch { }
        }
        return new ProjectSessionRegistry
        {
            ProjectName = project.Name,
            ProjectRoot = project.PrimaryRoot,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    public void SaveActiveSessionForAgent(ProjectProfile project, string agentId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var reg = LoadProjectSessionRegistry(project);
        reg.ProjectName = project.Name;
        reg.ProjectRoot = project.PrimaryRoot;
        reg.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        reg.ActiveSessionByAgent[agentId] = sessionId;
        RegistryService.WriteJsonAtomic(GetSessionRegistryPath(project), reg);
    }

    /// <summary>
    /// CLI 派发成功后，把会话 ID / 任务摘要同步进桌面可见会话索引，供 Stage2 UIA 对齐侧栏。
    /// </summary>
    public void SyncDesktopVisibleSessionIndex(
        ProjectProfile project,
        AgentRosterEntry agent,
        string? sessionId,
        string? taskBrief)
    {
        if (project == null || agent == null || string.IsNullOrWhiteSpace(agent.Id)) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
                SaveActiveSessionForAgent(project, agent.Id, sessionId);

            var reg = LoadProjectSessionRegistry(project);
            reg.ProjectName = project.Name;
            reg.ProjectRoot = project.PrimaryRoot;
            reg.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (!string.IsNullOrWhiteSpace(sessionId))
                reg.ActiveSessionByAgent[agent.Id] = sessionId!;

            string brief = ExtractCleanDialogTitle(taskBrief, 48);
            if (!reg.DesktopVisibleByAgent.TryGetValue(agent.Id, out var idx) || idx == null)
            {
                idx = new DesktopVisibleSessionIndex();
                reg.DesktopVisibleByAgent[agent.Id] = idx;
            }
            if (!string.IsNullOrWhiteSpace(sessionId))
                idx.CliSessionId = sessionId!;
            if (!string.IsNullOrWhiteSpace(brief))
            {
                idx.TaskBrief = brief;
                idx.DesktopTitleHint = brief;
            }
            idx.SyncedAt = reg.UpdatedAt;
            idx.RequireDesktopVisible = true;
            RegistryService.WriteJsonAtomic(GetSessionRegistryPath(project), reg);

            // 另写轻量旁路索引，便于桌面 UIA 脚本快速读取
            try
            {
                var collabDir = Path.Combine(project.PrimaryRoot, project.CollabRelDir.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(collabDir);
                var sidePath = Path.Combine(collabDir, "DESKTOP_VISIBLE_SESSIONS.json");
                RegistryService.WriteJsonAtomic(sidePath, reg.DesktopVisibleByAgent);
            }
            catch { }
        }
        catch { }
    }

    /// <summary>
    /// 扫描当前项目在所有其他 AI 工具（Codex / Grok Bot、Cursor、本地协同对话归档）下的对话框列表
    /// </summary>
    public List<ExternalDialogSessionInfo> DiscoverProjectSessions(ProjectProfile project, string? filterAgentId = null)
    {
        var results = new List<ExternalDialogSessionInfo>();
        if (string.IsNullOrWhiteSpace(project.PrimaryRoot)) return results;

        var rootsToMatch = new List<string> { NormalizePathForCompare(project.PrimaryRoot) };
        foreach (var comp in project.CompanionRoots.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            rootsToMatch.Add(NormalizePathForCompare(comp));
        }

        // 1. 扫描 Antigravity 对话目录 (F:\AIChat\Antigravity\DuiHua 或 ~/.gemini/antigravity/brain)
        if (string.IsNullOrWhiteSpace(filterAgentId) ||
            filterAgentId.Equals("antigravity", StringComparison.OrdinalIgnoreCase) ||
            filterAgentId.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            results.AddRange(ScanAntigravityProjectSessions(project.PrimaryRoot, rootsToMatch));
        }

        // 2. 扫描 Codex / Grok Bot 会话目录 (优先 F:\AIChat\codex\sessions 与 archived_sessions)
        if (string.IsNullOrWhiteSpace(filterAgentId) ||
            filterAgentId.Equals("codex_cli", StringComparison.OrdinalIgnoreCase) ||
            filterAgentId.Equals("grok_bot", StringComparison.OrdinalIgnoreCase) ||
            filterAgentId.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            results.AddRange(ScanCodexProjectSessions(rootsToMatch));
        }

        // 3. 扫描 Cursor 项目对话框目录 (优先 F:\AIChat\cursor\projects，兼顾 ~/.cursor/projects)
        if (string.IsNullOrWhiteSpace(filterAgentId) ||
            filterAgentId.Equals("cursor", StringComparison.OrdinalIgnoreCase) ||
            filterAgentId.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            results.AddRange(ScanCursorProjectSessions(project.PrimaryRoot, project.CompanionRoots));
        }

        // 4. 扫描项目内 work/collab/sessions/ 本地持久对话框（适用于自定义 CLI / HTTP / 桥接 AI）
        results.AddRange(ScanLocalCollabSessions(project, filterAgentId));

        return results
            .OrderByDescending(s => s.UpdatedAt, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public sealed class ProjectDispatchTargetInfo
    {
        public string TargetWorkDir { get; set; } = "";
        public string SubProjectLeaf { get; set; } = "";
        public string ParentProjectLeaf { get; set; } = "";
        public List<string> CandidateGroups { get; set; } = new();
        public List<string> HighPriorityTerms { get; set; } = new();
        public List<string> TopicKeywords { get; set; } = new();
        public List<string> SiblingExcludes { get; set; } = new();
    }

    private static readonly HashSet<string> NonSubProjectDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".vscode", ".cursor", ".agents", ".godot", ".mono",
        "bin", "obj", "build", "dist", "out", "output", "outputs", "target", "node_modules",
        "work", "data", "assets", "scripts", "scenes", "addons", "tools", "src", "res",
        "docs", "tests", "test", "resources", "shaders", "textures", "audio", "fonts"
    };

    private static readonly HashSet<string> GenericTaskStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "项目", "工程", "文件", "代码", "修改", "优化", "直接", "落地", "完成", "整体", "当前", "问题",
        "支持", "新增", "修复", "实现", "检查", "验收", "复核", "并行", "旁路", "协同", "派单",
        "主脑", "会话", "进行", "确保", "报错", "同时", "基于", "以及", "所有", "对应", "如下"
    };

    /// <summary>
    /// 从当前工程配置与具体任务指令中提取目标工作目录、子项目层级（如 D:\NX\北大肿瘤医院）、高区分度关键词与同级兄弟目录排斥列表。
    /// 用于消除跨工程/跨子项目误投进错误对话框的问题。
    /// </summary>
    public static ProjectDispatchTargetInfo ExtractProjectDispatchTargetInfo(
        ProjectProfile? project,
        string? taskText,
        IEnumerable<ProjectProfile>? allProjects = null)
    {
        var info = new ProjectDispatchTargetInfo();
        if (project == null || string.IsNullOrWhiteSpace(project.PrimaryRoot))
            return info;

        var primaryRoot = project.PrimaryRoot.Trim().TrimEnd('\\', '/');
        var rawLeaf = Path.GetFileName(primaryRoot);
        bool isCompanionToolRoot = rawLeaf.Equals("配套工具", StringComparison.OrdinalIgnoreCase) ||
                                   rawLeaf.Equals("taixuan-config-tool", StringComparison.OrdinalIgnoreCase) ||
                                   rawLeaf.Equals("ai-collab-hub", StringComparison.OrdinalIgnoreCase);
        var primaryLeaf = isCompanionToolRoot ? "XX" : rawLeaf;

        info.TargetWorkDir = primaryRoot;
        info.SubProjectLeaf = primaryLeaf;

        var text = taskText ?? "";
        var siblingExcludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) 若 taskText 中直接出现了盘符/绝对子目录路径（例如 D:\NX\北大肿瘤医院），优先识别具体子项目
        if (Directory.Exists(primaryRoot))
        {
            try
            {
                var subDirs = Directory.GetDirectories(primaryRoot)
                    .Select(d => new DirectoryInfo(d))
                    .Where(di => !NonSubProjectDirNames.Contains(di.Name) && !di.Name.StartsWith('.'))
                    .ToList();

                string? matchedSubPath = null;
                string matchedSubName = "";
                foreach (var sd in subDirs.OrderByDescending(d => d.Name.Length))
                {
                    if (sd.Name.Length < 2) continue;
                    bool textHasFullPath = text.Contains(sd.FullName, StringComparison.OrdinalIgnoreCase) ||
                                           text.Contains(sd.FullName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
                    bool isWorkspaceRoot = !File.Exists(Path.Combine(primaryRoot, ".git")) &&
                                           !Directory.Exists(Path.Combine(primaryRoot, ".git")) &&
                                           !File.Exists(Path.Combine(primaryRoot, "project.godot"));
                    bool textHasSubName = isWorkspaceRoot && text.Contains(sd.Name, StringComparison.OrdinalIgnoreCase);
                    if (textHasFullPath || textHasSubName)
                    {
                        matchedSubPath = sd.FullName;
                        matchedSubName = sd.Name;
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(matchedSubPath))
                {
                    info.TargetWorkDir = matchedSubPath;
                    info.SubProjectLeaf = matchedSubName;
                    info.ParentProjectLeaf = primaryLeaf;
                    foreach (var sd in subDirs)
                    {
                        if (!sd.Name.Equals(matchedSubName, StringComparison.OrdinalIgnoreCase) && sd.Name.Length >= 2)
                        {
                            siblingExcludes.Add(sd.Name);
                        }
                    }
                }
            }
            catch { }
        }

        // 2) 若当前绑定目录本身已是某多项目大目录下的子目录（如 D:\NX\北大肿瘤医院）
        if (string.IsNullOrWhiteSpace(info.ParentProjectLeaf))
        {
            var parentDir = Path.GetDirectoryName(primaryRoot);
            var parentName = string.IsNullOrWhiteSpace(parentDir) ? "" : Path.GetFileName(parentDir);
            bool parentKnownOrMulti = allProjects?.Any(p =>
                !string.IsNullOrWhiteSpace(p.PrimaryRoot) &&
                string.Equals(p.PrimaryRoot.Trim().TrimEnd('\\', '/'), parentDir, StringComparison.OrdinalIgnoreCase)) == true;

            if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir) &&
                (parentKnownOrMulti || (!string.IsNullOrWhiteSpace(parentName) && parentName.Length <= 16 &&
                 !parentName.Equals("Project", StringComparison.OrdinalIgnoreCase) &&
                 !parentName.Equals("Godot", StringComparison.OrdinalIgnoreCase) &&
                 !parentName.Equals("Projects", StringComparison.OrdinalIgnoreCase))))
            {
                try
                {
                    var peerDirs = Directory.GetDirectories(parentDir)
                        .Select(d => Path.GetFileName(d))
                        .Where(n => !string.IsNullOrWhiteSpace(n) && n.Length >= 2 &&
                                    !NonSubProjectDirNames.Contains(n) && !n.StartsWith('.'))
                        .ToList();
                    if (peerDirs.Count >= 2)
                    {
                        info.ParentProjectLeaf = parentName;
                        foreach (var pd in peerDirs)
                        {
                            if (!pd.Equals(primaryLeaf, StringComparison.OrdinalIgnoreCase) &&
                                !text.Contains(pd, StringComparison.OrdinalIgnoreCase))
                            {
                                siblingExcludes.Add(pd);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // 3) 也将已注册的其他独立工程名加入排斥词，防止把 NX 任务投进 XX 等不相关分组
        if (allProjects != null)
        {
            foreach (var other in allProjects)
            {
                if (string.IsNullOrWhiteSpace(other.PrimaryRoot)) continue;
                var otherLeaf = Path.GetFileName(other.PrimaryRoot.Trim().TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(otherLeaf) ||
                    otherLeaf.Equals("配套工具", StringComparison.OrdinalIgnoreCase) ||
                    otherLeaf.Equals(info.SubProjectLeaf, StringComparison.OrdinalIgnoreCase) ||
                    otherLeaf.Equals(info.ParentProjectLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (otherLeaf.Length >= 2 && !text.Contains(otherLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    siblingExcludes.Add(otherLeaf);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(info.SubProjectLeaf))
        {
            info.CandidateGroups.Add(info.SubProjectLeaf);
        }
        if (!string.IsNullOrWhiteSpace(info.ParentProjectLeaf) &&
            !info.CandidateGroups.Contains(info.ParentProjectLeaf, StringComparer.OrdinalIgnoreCase))
        {
            info.CandidateGroups.Add(info.ParentProjectLeaf);
        }

        // 仅当识别出伞状父子工程，或任务显式提到该子项目名时，将子项目名列入强匹配 HighPriorityTerms
        if (!string.IsNullOrWhiteSpace(info.ParentProjectLeaf) && !string.IsNullOrWhiteSpace(info.SubProjectLeaf))
        {
            info.HighPriorityTerms.Add(info.SubProjectLeaf);
        }
        else if (!string.IsNullOrWhiteSpace(info.SubProjectLeaf) && info.SubProjectLeaf.Length >= 3)
        {
            info.HighPriorityTerms.Add(info.SubProjectLeaf);
        }

        // 4) 提取任务正文里的文件名标识与中文关键短语作为 TopicKeywords
        var kwSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, @"\b([A-Za-z][A-Za-z0-9_]{2,28})\.(?:java|kt|py|cs|gd|tscn|tres|gdshader|json|xml)\b"))
        {
            var stem = m.Groups[1].Value;
            if (!GenericTaskStopwords.Contains(stem) && stem.Length >= 3) kwSet.Add(stem);
        }
        foreach (Match m in Regex.Matches(text, @"[\u4e00-\u9fa5]{2,8}"))
        {
            var w = m.Value;
            if (GenericTaskStopwords.Contains(w)) continue;
            if (!string.IsNullOrWhiteSpace(info.SubProjectLeaf) && w.Equals(info.SubProjectLeaf, StringComparison.OrdinalIgnoreCase)) continue;
            kwSet.Add(w);
            if (kwSet.Count >= 12) break;
        }
        info.TopicKeywords.AddRange(kwSet.Take(10));
        info.SiblingExcludes.AddRange(siblingExcludes);
        return info;
    }

    /// <summary>
    /// 为目标 AI 工具在当前项目下寻找最合适的已有对话框（优先读取与任务语义/子项目匹配的对话框，严防张冠李戴）
    /// </summary>
    public ExternalDialogSessionInfo? FindBestMatchingSession(
        ProjectProfile project,
        string agentId,
        string? explicitSessionId = null,
        IReadOnlyList<ExternalDialogSessionInfo>? preloadedSessions = null,
        string? taskHint = null)
    {
        List<ExternalDialogSessionInfo> all;
        if (preloadedSessions != null)
        {
            all = preloadedSessions.Where(s =>
                string.IsNullOrWhiteSpace(agentId) ||
                agentId.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                s.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase) ||
                (agentId.Equals("grok_bot", StringComparison.OrdinalIgnoreCase) && s.AgentId.Equals("codex_cli", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        else
        {
            all = DiscoverProjectSessions(project, agentId);
        }
        if (!string.IsNullOrWhiteSpace(explicitSessionId) &&
            !explicitSessionId.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !explicitSessionId.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            var exact = all.FirstOrDefault(s =>
                string.Equals(s.SessionId, explicitSessionId, StringComparison.OrdinalIgnoreCase) ||
                s.Title.Contains(explicitSessionId, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        var reg = LoadProjectSessionRegistry(project);
        reg.ActiveSessionByAgent.TryGetValue(agentId, out var boundId);

        if (!string.IsNullOrWhiteSpace(taskHint) && all.Count > 0)
        {
            var targetInfo = ExtractProjectDispatchTargetInfo(project, taskHint);
            bool enforceSubMatch = !string.IsNullOrWhiteSpace(targetInfo.ParentProjectLeaf) && targetInfo.HighPriorityTerms.Count > 0;
            ExternalDialogSessionInfo? bestSess = null;
            int bestScore = int.MinValue;

            foreach (var s in all)
            {
                int score = 0;
                var title = s.Title ?? "";
                var pDir = s.ProjectDir ?? "";
                var combined = $"{title} {pDir} {s.LastUserMessage}";

                if (!string.IsNullOrWhiteSpace(targetInfo.TargetWorkDir) &&
                    pDir.Replace('\\', '/').Contains(targetInfo.TargetWorkDir.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    score += 60;
                }

                bool hitHigh = false;
                foreach (var ht in targetInfo.HighPriorityTerms)
                {
                    if (title.Contains(ht, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 120;
                        hitHigh = true;
                    }
                    else if (combined.Contains(ht, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 60;
                        hitHigh = true;
                    }
                }

                foreach (var kw in targetInfo.TopicKeywords)
                {
                    if (title.Contains(kw, StringComparison.OrdinalIgnoreCase)) score += 20;
                    else if (s.LastUserMessage.Contains(kw, StringComparison.OrdinalIgnoreCase)) score += 10;
                }

                foreach (var sib in targetInfo.SiblingExcludes)
                {
                    if (!string.IsNullOrWhiteSpace(sib) &&
                        (title.Contains(sib, StringComparison.OrdinalIgnoreCase) || pDir.Contains(sib, StringComparison.OrdinalIgnoreCase)))
                    {
                        score -= 200;
                    }
                }

                if (!string.IsNullOrWhiteSpace(boundId) &&
                    string.Equals(s.SessionId, boundId, StringComparison.OrdinalIgnoreCase) &&
                    score >= 0)
                {
                    score += 5;
                }

                if (score > bestScore && (!enforceSubMatch || hitHigh || score >= 60))
                {
                    bestScore = score;
                    bestSess = s;
                }
            }

            if (bestSess != null && bestScore >= 0)
            {
                return bestSess;
            }
            if (enforceSubMatch || bestScore < 0)
            {
                // 存在明确的跨子项目冲突或找不到对应子项目会话时，宁可新建专属对话框，绝不张冠李戴续接别的项目会话
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(boundId))
        {
            var bound = all.FirstOrDefault(s => string.Equals(s.SessionId, boundId, StringComparison.OrdinalIgnoreCase));
            if (bound != null) return bound;
        }

        return all.FirstOrDefault();
    }

    /// <summary>
    /// 读取指定对话框的最近 N 轮完整对话内容与最后反馈，格式化为 Markdown 供总包工头或用户在 UI 中直接检阅
    /// </summary>
    public string ReadSessionTranscriptMarkdown(ExternalDialogSessionInfo session, int maxMessages = 12)
    {
        if (!File.Exists(session.FilePath))
        {
            return BuildTranscriptMarkdownFromTurns(session, null, maxMessages);
        }
        var messages = ExtractConversationTurns(session.FilePath, session.AgentId);
        return BuildTranscriptMarkdownFromTurns(session, messages, maxMessages);
    }

    /// <summary>
    /// 直接从内存中已解析的 turns 生成 Markdown（不再读盘）。turns 为 null 表示源文件不存在。
    /// </summary>
    public string BuildTranscriptMarkdownFromTurns(ExternalDialogSessionInfo session, List<(string Role, string Time, string Text)>? turns, int maxMessages = 20)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 跨工具项目对话框内容读取 · {session.AgentName}");
        sb.AppendLine();
        sb.AppendLine($"- **对话框 ID (SessionId)**：`{session.SessionId}`");
        sb.AppendLine($"- **对话框标题/主题**：{session.Title}");
        sb.AppendLine($"- **所属项目目录**：`{session.ProjectDir}`");
        sb.AppendLine($"- **最后活跃时间**：`{session.UpdatedAt}`");
        sb.AppendLine($"- **本地对话源文件**：`{session.FilePath}`");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        if (turns == null)
        {
            sb.AppendLine("*(对话源文件不存在或已被移动)*");
            return sb.ToString();
        }

        var messages = turns;
        if (messages.Count == 0)
        {
            sb.AppendLine("*(当前对话框尚无可解析的文本消息)*");
            return sb.ToString();
        }

        var lastUserIdx = messages.FindLastIndex(m => m.Role == "user");
        if (lastUserIdx >= 0)
        {
            sb.AppendLine("## ⚡ 最新一轮分配任务与 AI 回复（置顶）");
            sb.AppendLine();
            for (int i = lastUserIdx; i < messages.Count; i++)
            {
                var (role, time, text) = messages[i];
                var roleBadge = role == "user" ? "👤 **【最新指派任务 / 用户指令】**" : "🤖 **【最新 AI 回复与执行反馈】**";
                var timeTag = string.IsNullOrWhiteSpace(time) ? "" : $" `{time}`";
                sb.AppendLine($"### {roleBadge}{timeTag}");
                sb.AppendLine(text.Trim());
                sb.AppendLine();
            }
            sb.AppendLine("---");
            sb.AppendLine();
        }

        var tail = messages.Skip(Math.Max(0, messages.Count - maxMessages)).ToList();
        sb.AppendLine($"## 🕒 近期对话历史（共 {messages.Count} 条，展示最近 {tail.Count} 条）");
        sb.AppendLine();

        foreach (var (role, time, text) in tail)
        {
            var roleBadge = role == "user" ? "👤 **分配任务 / 用户指令**" : "🤖 **AI 回复与执行反馈**";
            var timeTag = string.IsNullOrWhiteSpace(time) ? "" : $" `{time}`";
            sb.AppendLine($"### {roleBadge}{timeTag}");
            sb.AppendLine(text.Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ---------------- Antigravity 对话扫描与解析 (F:\AIChat\Antigravity\DuiHua) ----------------

    private List<ExternalDialogSessionInfo> ScanAntigravityProjectSessions(string primaryRoot, List<string> normalizedProjectRoots)
    {
        var list = new List<ExternalDialogSessionInfo>();
        if (AppPaths.IsIsolatedTest) return list;

        var brainRoot = Directory.Exists(@"F:\AIChat\Antigravity\DuiHua")
            ? @"F:\AIChat\Antigravity\DuiHua"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "brain");
        if (!Directory.Exists(brainRoot)) return list;

        var leafName = Path.GetFileName(primaryRoot.TrimEnd('\\', '/'));
        bool isXxProject = string.Equals(leafName, "XX", StringComparison.OrdinalIgnoreCase);

        try
        {
            var convDirs = new DirectoryInfo(brainRoot)
                .GetDirectories()
                .Where(d => !d.Name.Equals("tempmediaStorage", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Take(40);

            foreach (var dir in convDirs)
            {
                var tf = Path.Combine(dir.FullName, ".system_generated", "logs", "transcript.jsonl");
                if (!File.Exists(tf)) continue;
                var preferredTf = ToPreferredAiChatPath(tf);
                if (TryGetCachedSessionInfo(tf, out var fiCached, out var cachedInfo) && cachedInfo != null)
                {
                    list.Add(cachedInfo);
                    continue;
                }

                // 快速判断该对话是否关联当前项目
                bool belongs = false;
                try
                {
                    foreach (var line in ReadLinesHeadTailSafe(tf, fiCached?.Length ?? 0, 64 * 1024, 64 * 1024).Take(25))
                    {
                        foreach (var nr in normalizedProjectRoots)
                        {
                            var winPath = nr.Replace('/', '\\');
                            var escWinPath = winPath.Replace("\\", "\\\\");
                            if (line.Contains(nr, StringComparison.OrdinalIgnoreCase) ||
                                line.Contains(winPath, StringComparison.OrdinalIgnoreCase) ||
                                line.Contains(escWinPath, StringComparison.OrdinalIgnoreCase))
                            {
                                belongs = true;
                                break;
                            }
                        }
                        if (!belongs && isXxProject &&
                            (line.Contains("太玄", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("configurator_native", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("taixuan-config-tool", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("ai-collab-hub", StringComparison.OrdinalIgnoreCase)))
                        {
                            belongs = true;
                        }
                        if (belongs) break;
                    }
                }
                catch { }

                if (!belongs) continue;

                var turns = ExtractAntigravityTurns(tf, summaryOnlyFastSlice: true);
                if (turns.Count == 0) continue;

                var firstUser = turns.FirstOrDefault(t => t.Role == "user").Text ?? "";
                if (string.IsNullOrWhiteSpace(firstUser)) continue; // 过滤内部 subagent 临时记录，只保留真实用户对话框
                var lastUser = turns.LastOrDefault(t => t.Role == "user").Text ?? "";
                var lastAst = turns.LastOrDefault(t => t.Role == "assistant").Text ?? "";
                var fi = fiCached ?? new FileInfo(tf);

                var info = new ExternalDialogSessionInfo
                {
                    SessionId = dir.Name,
                    AgentId = "antigravity",
                    AgentName = "Antigravity",
                    Title = !string.IsNullOrWhiteSpace(firstUser) ? ExtractCleanDialogTitle(firstUser, 38) : $"Antigravity 对话 ({dir.Name[..Math.Min(8, dir.Name.Length)]})",
                    ProjectDir = primaryRoot,
                    FilePath = preferredTf,
                    UpdatedAt = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    TurnCount = turns.Count,
                    LastUserMessage = TruncateSingleLine(lastUser, 120),
                    LastAssistantMessage = TruncateSingleLine(lastAst, 160)
                };
                SaveCachedSessionInfo(tf, fi, info);
                list.Add(info);
            }
        }
        catch { }

        return list;
    }

    // ---------------- Codex / Grok Bot 会话扫描与解析 ----------------

    private List<ExternalDialogSessionInfo> ScanCodexProjectSessions(List<string> normalizedProjectRoots)
    {
        var list = new List<ExternalDialogSessionInfo>();
        var codexHome = GetCodexHomeDir();
        if (codexHome == null || !Directory.Exists(codexHome)) return list;

        var titleMap = LoadCodexSessionTitles(codexHome);
        var candidateDirs = new List<string>();
        var sessionsDir = Path.Combine(codexHome, "sessions");
        if (Directory.Exists(sessionsDir)) candidateDirs.Add(sessionsDir);
        var archivedDir = Path.Combine(codexHome, "archived_sessions");
        if (Directory.Exists(archivedDir)) candidateDirs.Add(archivedDir);
        if (candidateDirs.Count == 0) return list;

        var files = new List<string>();
        foreach (var cd in candidateDirs)
        {
            try
            {
                files.AddRange(Directory.GetFiles(cd, "rollout-*.jsonl", SearchOption.AllDirectories));
            }
            catch { }
        }

        var orderedFiles = files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(150);

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in orderedFiles)
        {
            try
            {
                var info = TryParseCodexRolloutHeaderAndTail(file, normalizedProjectRoots, titleMap);
                if (info != null && seenIds.Add(info.SessionId))
                {
                    list.Add(info);
                }
            }
            catch { }
        }

        return list;
    }

    /// <summary>
    /// 在执行完 codex exec 后，查找该项目目录下最新生成或更新的 rollout 会话 ID 并返回
    /// </summary>
    public ExternalDialogSessionInfo? FindNewestCodexSessionForDir(string projectDir)
    {
        var norm = NormalizePathForCompare(projectDir);
        var matches = ScanCodexProjectSessions(new List<string> { norm });
        return matches.FirstOrDefault();
    }

    private static string? GetCodexHomeDir()
    {
        if (AppPaths.IsIsolatedTest)
        {
            var mockCodex = Path.Combine(AppPaths.UserDataDir, "mock_codex_home");
            Directory.CreateDirectory(Path.Combine(mockCodex, "sessions"));
            return mockCodex;
        }

        // 优先使用 F:\AIChat\codex 真实聊天存储目录，展示真实 F:\AIChat 路径
        if (Directory.Exists(@"F:\AIChat\codex")) return @"F:\AIChat\codex";

        var env = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        if (Directory.Exists(defaultDir)) return defaultDir;

        return null;
    }

    private static Dictionary<string, string> LoadCodexSessionTitles(string codexHome)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var indexFile = Path.Combine(codexHome, "session_index.jsonl");
        if (!File.Exists(indexFile) && File.Exists(@"F:\AIChat\codex\session_index.jsonl"))
        {
            indexFile = @"F:\AIChat\codex\session_index.jsonl";
        }
        if (!File.Exists(indexFile)) return map;

        try
        {
            foreach (var line in File.ReadLines(indexFile, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) && root.TryGetProperty("thread_name", out var nameEl))
                {
                    var id = idEl.GetString();
                    var name = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    {
                        map[id] = name;
                    }
                }
            }
        }
        catch { }

        return map;
    }

    private static ExternalDialogSessionInfo? TryParseCodexRolloutHeaderAndTail(
        string filePath,
        List<string> normalizedProjectRoots,
        Dictionary<string, string> titleMap)
    {
        if (TryGetCachedSessionInfo(filePath, out var fiCached, out var cachedInfo))
        {
            if (cachedInfo != null) return cachedInfo;
        }

        string? firstLine = null;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8))
        {
            firstLine = sr.ReadLine();
        }
        if (string.IsNullOrWhiteSpace(firstLine)) return null;

        using var doc = JsonDocument.Parse(firstLine);
        var root = doc.RootElement;
        if (!root.TryGetProperty("payload", out var payload)) return null;

        var cwd = payload.TryGetProperty("cwd", out var cwdEl) ? cwdEl.GetString() ?? "" : "";
        var sessionId = payload.TryGetProperty("session_id", out var sidEl) ? sidEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(sessionId) && payload.TryGetProperty("id", out var idEl))
        {
            sessionId = idEl.GetString() ?? "";
        }
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(cwd)) return null;

        var normCwd = NormalizePathForCompare(cwd);
        bool isXxProject = normalizedProjectRoots.Any(r => r.EndsWith("/xx", StringComparison.OrdinalIgnoreCase));
        var matched = normalizedProjectRoots.Any(r =>
            !string.IsNullOrEmpty(r) &&
            (normCwd.Equals(r, StringComparison.OrdinalIgnoreCase) ||
             normCwd.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) ||
             r.StartsWith(normCwd + "/", StringComparison.OrdinalIgnoreCase))) ||
            (isXxProject && (normCwd.EndsWith("/xx", StringComparison.OrdinalIgnoreCase) || normCwd.Contains("/godot/xx", StringComparison.OrdinalIgnoreCase)));

        if (!matched) return null;

        var turns = ExtractCodexTurns(filePath, summaryOnlyFastSlice: true);
        var lastUser = turns.LastOrDefault(t => t.Role == "user").Text ?? "";
        var lastAssistant = turns.LastOrDefault(t => t.Role == "assistant").Text ?? "";

        if (!titleMap.TryGetValue(sessionId, out var title) || string.IsNullOrWhiteSpace(title))
        {
            title = !string.IsNullOrWhiteSpace(lastUser)
                ? ExtractCleanDialogTitle(lastUser, 38)
                : $"Codex 项目会话 ({sessionId[..Math.Min(8, sessionId.Length)]})";
        }
        else
        {
            title = ExtractCleanDialogTitle(title, 38);
        }

        var preferredPath = ToPreferredAiChatPath(filePath);
        var fi = fiCached ?? new FileInfo(filePath);
        var info = new ExternalDialogSessionInfo
        {
            SessionId = sessionId,
            AgentId = "codex_cli",
            AgentName = "Codex / Grok Bot",
            Title = title,
            ProjectDir = cwd,
            FilePath = preferredPath,
            UpdatedAt = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
            TurnCount = turns.Count,
            LastUserMessage = TruncateSingleLine(lastUser, 120),
            LastAssistantMessage = TruncateSingleLine(lastAssistant, 160)
        };
        SaveCachedSessionInfo(filePath, fi, info);
        return info;
    }

    // ---------------- Cursor 项目对话框扫描与解析 ----------------

    private List<ExternalDialogSessionInfo> ScanCursorProjectSessions(string primaryRoot, List<string> companionRoots)
    {
        var list = new List<ExternalDialogSessionInfo>();
        if (AppPaths.IsIsolatedTest) return list;

        var candidateCursorRoots = new List<string>();
        if (Directory.Exists(@"F:\AIChat\cursor\projects"))
            candidateCursorRoots.Add(@"F:\AIChat\cursor\projects");

        var localCursorProjects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "projects");
        if (Directory.Exists(localCursorProjects))
            candidateCursorRoots.Add(localCursorProjects);

        if (candidateCursorRoots.Count == 0) return list;

        var allRoots = new List<string> { primaryRoot };
        allRoots.AddRange(companionRoots.Where(x => !string.IsNullOrWhiteSpace(x)));
        var targetSlugs = new HashSet<string>(allRoots.Select(ConvertPathToCursorSlug), StringComparer.OrdinalIgnoreCase);
        bool isXxProject = string.Equals(Path.GetFileName(primaryRoot.TrimEnd('\\', '/')), "XX", StringComparison.OrdinalIgnoreCase);

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cursorProjectsRoot in candidateCursorRoots)
        {
            try
            {
                var projDirs = Directory.GetDirectories(cursorProjectsRoot);
                foreach (var pDir in projDirs)
                {
                    var dirName = Path.GetFileName(pDir);
                    bool slugMatched = targetSlugs.Any(slug =>
                                           dirName.Equals(slug, StringComparison.OrdinalIgnoreCase) ||
                                           dirName.StartsWith(slug + "-", StringComparison.OrdinalIgnoreCase)) ||
                                       (isXxProject && dirName.EndsWith("-XX", StringComparison.OrdinalIgnoreCase));
                    if (!slugMatched) continue;

                    var projTranscriptDir = Path.Combine(pDir, "agent-transcripts");
                    if (!Directory.Exists(projTranscriptDir)) continue;

                    var jsonlFiles = Directory.GetFiles(projTranscriptDir, "*.jsonl", SearchOption.AllDirectories)
                        .Where(f => !f.Contains("\\subagents\\", StringComparison.OrdinalIgnoreCase) &&
                                    !f.Contains("/subagents/", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .Take(35);

                    foreach (var jf in jsonlFiles)
                    {
                        var sid = Path.GetFileNameWithoutExtension(jf);
                        if (!seenIds.Add(sid)) continue;

                        if (TryGetCachedSessionInfo(jf, out var fiCached, out var cachedInfo) && cachedInfo != null)
                        {
                            list.Add(cachedInfo);
                            continue;
                        }

                        var displayPath = EnsureMirroredToFAiChatCursor(jf, dirName);
                        var turns = ExtractCursorTurns(displayPath, summaryOnlyFastSlice: true);
                        if (turns.Count == 0) continue;

                        var firstUser = turns.FirstOrDefault(t => t.Role == "user").Text ?? "";
                        if (string.IsNullOrWhiteSpace(firstUser)) continue; // 过滤内部 subagent 临时记录，只保留真实用户对话框
                        var lastUser = turns.LastOrDefault(t => t.Role == "user").Text ?? "";
                        var lastAssistant = turns.LastOrDefault(t => t.Role == "assistant").Text ?? "";
                        var fi = fiCached ?? new FileInfo(displayPath);

                        var info = new ExternalDialogSessionInfo
                        {
                            SessionId = sid,
                            AgentId = "cursor",
                            AgentName = "Cursor IDE",
                            Title = !string.IsNullOrWhiteSpace(firstUser) ? ExtractCleanDialogTitle(firstUser, 38) : $"Cursor 对话 ({sid[..Math.Min(8, sid.Length)]})",
                            ProjectDir = primaryRoot,
                            FilePath = displayPath,
                            UpdatedAt = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            TurnCount = turns.Count,
                            LastUserMessage = TruncateSingleLine(lastUser, 120),
                            LastAssistantMessage = TruncateSingleLine(lastAssistant, 160)
                        };
                        SaveCachedSessionInfo(jf, fi, info);
                        list.Add(info);
                    }
                }
            }
            catch { }
        }

        return list;
    }

    private static string EnsureMirroredToFAiChatCursor(string sourceFilePath, string slugDirName)
    {
        try
        {
            if (sourceFilePath.StartsWith(@"F:\AIChat\", StringComparison.OrdinalIgnoreCase))
                return sourceFilePath;

            if (Directory.Exists(@"F:\AIChat\cursor\projects"))
            {
                var marker = "agent-transcripts" + Path.DirectorySeparatorChar;
                var idx = sourceFilePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var relAfter = sourceFilePath[(idx + marker.Length)..];
                    var dest = Path.Combine(@"F:\AIChat\cursor\projects", slugDirName, "agent-transcripts", relAfter);
                    var destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    if (!File.Exists(dest) || File.GetLastWriteTimeUtc(sourceFilePath) > File.GetLastWriteTimeUtc(dest))
                    {
                        File.Copy(sourceFilePath, dest, overwrite: true);
                    }
                    return dest;
                }
            }
        }
        catch { }
        return ToPreferredAiChatPath(sourceFilePath);
    }

    // ---------------- 项目本地 work/collab/sessions/ 对话框管理 ----------------

    public List<ExternalDialogSessionInfo> ScanLocalCollabSessions(ProjectProfile project, string? filterAgentId)
    {
        var list = new List<ExternalDialogSessionInfo>();
        var sessDir = Path.Combine(project.PrimaryRoot, project.CollabRelDir.Replace('/', Path.DirectorySeparatorChar), "sessions");
        if (!Directory.Exists(sessDir)) return list;

        try
        {
            foreach (var file in Directory.GetFiles(sessDir, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (TryGetCachedSessionInfo(file, out var fiCached, out var cachedInfo) && cachedInfo != null)
                {
                    if (string.IsNullOrWhiteSpace(filterAgentId) ||
                        filterAgentId.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                        cachedInfo.AgentId.Equals(filterAgentId, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(cachedInfo);
                    }
                    continue;
                }

                var turns = ExtractCursorTurns(file, summaryOnlyFastSlice: true);
                if (turns.Count == 0) continue;
                var fn = Path.GetFileNameWithoutExtension(file);
                var parts = fn.Split("__", 2);
                var agId = parts.Length == 2 ? parts[0] : "local_cli";
                var sid = parts.Length == 2 ? parts[1] : fn;

                if (!string.IsNullOrWhiteSpace(filterAgentId) &&
                    !filterAgentId.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                    !agId.Equals(filterAgentId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var firstUser = turns.FirstOrDefault(t => t.Role == "user").Text ?? "";
                if (string.IsNullOrWhiteSpace(firstUser)) continue; // 过滤内部 subagent 临时记录，只保留真实用户对话框
                var lastUser = turns.LastOrDefault(t => t.Role == "user").Text ?? "";
                var lastAst = turns.LastOrDefault(t => t.Role == "assistant").Text ?? "";
                var fi = fiCached ?? new FileInfo(file);

                var info = new ExternalDialogSessionInfo
                {
                    SessionId = sid,
                    AgentId = agId,
                    AgentName = $"项目本地会话 ({agId})",
                    Title = !string.IsNullOrWhiteSpace(firstUser) ? ExtractCleanDialogTitle(firstUser, 38) : sid,
                    ProjectDir = project.PrimaryRoot,
                    FilePath = file,
                    UpdatedAt = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    TurnCount = turns.Count,
                    LastUserMessage = TruncateSingleLine(lastUser, 120),
                    LastAssistantMessage = TruncateSingleLine(lastAst, 160)
                };
                SaveCachedSessionInfo(file, fi, info);
                list.Add(info);
            }
        }
        catch { }

        return list;
    }

    public ExternalDialogSessionInfo AppendOrCreateLocalCollabSession(
        ProjectProfile project,
        AgentRosterEntry agent,
        string? existingSessionId,
        string userInstruction,
        string assistantReply)
    {
        var sessDir = Path.Combine(project.PrimaryRoot, project.CollabRelDir.Replace('/', Path.DirectorySeparatorChar), "sessions");
        Directory.CreateDirectory(sessDir);

        var safeAg = string.Concat(agent.Id.Where(c => char.IsLetterOrDigit(c) || c == '_'));
        var sid = !string.IsNullOrWhiteSpace(existingSessionId)
            ? existingSessionId
            : $"dlg_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";

        var filePath = Path.Combine(sessDir, $"{safeAg}__{sid}.jsonl");
        var nowIso = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var userLine = JsonSerializer.Serialize(new
        {
            timestamp = nowIso,
            role = "user",
            message = new { content = new[] { new { type = "text", text = userInstruction } } }
        });
        var astLine = JsonSerializer.Serialize(new
        {
            timestamp = nowIso,
            role = "assistant",
            message = new { content = new[] { new { type = "text", text = assistantReply } } }
        });

        File.AppendAllLines(filePath, new[] { userLine, astLine }, new UTF8Encoding(false));
        SaveActiveSessionForAgent(project, agent.Id, sid);

        return new ExternalDialogSessionInfo
        {
            SessionId = sid,
            AgentId = agent.Id,
            AgentName = agent.Name,
            Title = TruncateSingleLine(userInstruction, 38),
            ProjectDir = project.PrimaryRoot,
            FilePath = filePath,
            UpdatedAt = nowIso,
            TurnCount = ExtractCursorTurns(filePath).Count,
            LastUserMessage = TruncateSingleLine(userInstruction, 120),
            LastAssistantMessage = TruncateSingleLine(assistantReply, 160)
        };
    }

    /// <summary>
    /// 将分配给 Cursor 的任务指令与执行结果写入 F:\AIChat\cursor\projects (及 ~/.cursor/projects) 对应的项目对话框中
    /// </summary>
    public ExternalDialogSessionInfo AppendOrCreateCursorSession(
        ProjectProfile project,
        ExternalDialogSessionInfo? matchedSession,
        string userInstruction,
        string assistantReply)
    {
        var slug = ConvertPathToCursorSlug(project.PrimaryRoot);
        var sid = !string.IsNullOrWhiteSpace(matchedSession?.SessionId)
            ? matchedSession!.SessionId
            : Guid.NewGuid().ToString("D");

        string primaryFile;
        if (matchedSession != null && !string.IsNullOrWhiteSpace(matchedSession.FilePath) && File.Exists(matchedSession.FilePath))
        {
            primaryFile = matchedSession.FilePath;
        }
        else if (Directory.Exists(@"F:\AIChat\cursor\projects"))
        {
            var dir = Path.Combine(@"F:\AIChat\cursor\projects", slug, "agent-transcripts", sid);
            Directory.CreateDirectory(dir);
            primaryFile = Path.Combine(dir, $"{sid}.jsonl");
        }
        else
        {
            var userCursor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "projects", slug, "agent-transcripts", sid);
            Directory.CreateDirectory(userCursor);
            primaryFile = Path.Combine(userCursor, $"{sid}.jsonl");
        }

        var nowIso = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var userLine = JsonSerializer.Serialize(new
        {
            timestamp = nowIso,
            role = "user",
            message = new
            {
                content = new[]
                {
                    new { type = "text", text = $"<timestamp>{nowIso}</timestamp>\n<user_query>\n{userInstruction}\n</user_query>" }
                }
            }
        });
        var astLine = JsonSerializer.Serialize(new
        {
            timestamp = nowIso,
            role = "assistant",
            message = new
            {
                content = new[]
                {
                    new { type = "text", text = assistantReply }
                }
            }
        });
        var turnEndLine = JsonSerializer.Serialize(new { type = "turn_ended", status = "success" });

        var dirName = Path.GetDirectoryName(primaryFile);
        if (!string.IsNullOrEmpty(dirName)) Directory.CreateDirectory(dirName);
        File.AppendAllLines(primaryFile, new[] { userLine, astLine, turnEndLine }, new UTF8Encoding(false));

        // 同步镜像至 ~/.cursor/projects 与 F:\AIChat\cursor\projects 双端，确保 Cursor IDE 与 F:\AIChat 浏览器实时一致
        try
        {
            var userMirror = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "projects", slug, "agent-transcripts", sid, $"{sid}.jsonl");
            if (!string.Equals(Path.GetFullPath(primaryFile), Path.GetFullPath(userMirror), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(userMirror)!);
                File.Copy(primaryFile, userMirror, overwrite: true);
            }
        }
        catch { }

        var displayFile = EnsureMirroredToFAiChatCursor(primaryFile, slug);
        SaveActiveSessionForAgent(project, "cursor", sid);

        var turns = ExtractCursorTurns(displayFile);
        var firstUser = turns.FirstOrDefault(t => t.Role == "user").Text ?? userInstruction;

        return new ExternalDialogSessionInfo
        {
            SessionId = sid,
            AgentId = "cursor",
            AgentName = "Cursor IDE",
            Title = TruncateSingleLine(firstUser, 38),
            ProjectDir = project.PrimaryRoot,
            FilePath = displayFile,
            UpdatedAt = nowIso,
            TurnCount = turns.Count,
            LastUserMessage = TruncateSingleLine(userInstruction, 120),
            LastAssistantMessage = TruncateSingleLine(assistantReply, 160)
        };
    }

    // ---------------- 消息轮次通用解析 ----------------

    public List<(string Role, string Time, string Text)> ExtractConversationTurns(string filePath, string agentId, int maxRecentTurns = 0)
    {
        if (agentId.Equals("antigravity", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith("transcript.jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractAntigravityTurns(filePath, maxRecentTurns: maxRecentTurns);
        }
        if (agentId.Equals("codex_cli", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(filePath).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractCodexTurns(filePath, maxRecentTurns: maxRecentTurns);
        }
        return ExtractCursorTurns(filePath, maxRecentTurns: maxRecentTurns);
    }

    private const int RecentTailFirstBytes = 768 * 1024;
    private const int RecentTailExpandedBytes = (int)(2.5 * 1024 * 1024);

    private static List<string> ReadAllLinesShared(string filePath)
    {
        var list = new List<string>();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) != null) list.Add(line);
        return list;
    }

    /// <summary>从文件末尾读取切片，并丢掉开头那条被截断的半行。</summary>
    private static List<string> ReadTailSliceLines(string filePath, long fileLength, int tailBytes)
    {
        var list = new List<string>();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long take = Math.Min(tailBytes, Math.Max(0, fileLength));
        if (take <= 0) return list;
        long start = fileLength - take;
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[take];
        int read = fs.Read(buf, 0, (int)take);
        if (read <= 0) return list;
        var txt = Encoding.UTF8.GetString(buf, 0, read);
        var lines = txt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        int i0 = start > 0 ? 1 : 0;
        for (int i = i0; i < lines.Length; i++)
        {
            if (i == lines.Length - 1 && lines[i].Length == 0) continue;
            list.Add(lines[i]);
        }
        return list;
    }

    private static List<(string Role, string Time, string Text)> TakeRecentTurns(List<(string Role, string Time, string Text)> turns, int maxRecentTurns)
    {
        if (maxRecentTurns <= 0 || turns.Count <= maxRecentTurns) return turns;
        return turns.GetRange(turns.Count - maxRecentTurns, maxRecentTurns);
    }

    private static List<(string Role, string Time, string Text)> ExtractAntigravityTurns(string filePath, bool summaryOnlyFastSlice = false, int maxRecentTurns = 0)
    {
        try
        {
            long len = new FileInfo(filePath).Length;
            if (maxRecentTurns > 0 && len > 512 * 1024)
            {
                var turns = ParseAntigravityLines(ReadTailSliceLines(filePath, len, RecentTailFirstBytes));
                if (turns.Count < maxRecentTurns)
                    turns = ParseAntigravityLines(ReadTailSliceLines(filePath, len, RecentTailExpandedBytes));
                return TakeRecentTurns(turns, maxRecentTurns);
            }
            IEnumerable<string> lines = (summaryOnlyFastSlice && len > 512 * 1024)
                ? ReadLinesHeadTailSafe(filePath, len)
                : ReadAllLinesShared(filePath);
            return ParseAntigravityLines(lines);
        }
        catch
        {
            return new List<(string Role, string Time, string Text)>();
        }
    }

    private static List<(string Role, string Time, string Text)> ParseAntigravityLines(IEnumerable<string> lines)
    {
        var turns = new List<(string Role, string Time, string Text)>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "" : "";
            if (type != "USER_INPUT" && type != "PLANNER_RESPONSE") continue;

            var content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(content)) continue;

            var ts = root.TryGetProperty("created_at", out var tsEl) ? tsEl.GetString() ?? "" : "";
            if (type == "USER_INPUT")
            {
                var m = Regex.Match(content, @"<USER_REQUEST>\s*(.*?)\s*</USER_REQUEST>", RegexOptions.Singleline);
                var cleaned = m.Success ? m.Groups[1].Value.Trim() : content.Trim();
                if (!string.IsNullOrWhiteSpace(cleaned) && !cleaned.StartsWith("Comments on artifact", StringComparison.OrdinalIgnoreCase))
                {
                    turns.Add(("user", ts, cleaned));
                }
            }
            else if (type == "PLANNER_RESPONSE")
            {
                turns.Add(("assistant", ts, content.Trim()));
            }
            }
            catch { }
        }
        return turns;
    }

    private static string ToPreferredAiChatPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return filePath;
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cCodex = Path.Combine(userHome, ".codex");
        if (filePath.StartsWith(cCodex, StringComparison.OrdinalIgnoreCase) && Directory.Exists(@"F:\AIChat\codex"))
        {
            return @"F:\AIChat\codex" + filePath[cCodex.Length..];
        }
        var cBrain = Path.Combine(userHome, ".gemini", "antigravity", "brain");
        if (filePath.StartsWith(cBrain, StringComparison.OrdinalIgnoreCase) && Directory.Exists(@"F:\AIChat\Antigravity\DuiHua"))
        {
            return @"F:\AIChat\Antigravity\DuiHua" + filePath[cBrain.Length..];
        }
        var cCursorProj = Path.Combine(userHome, ".cursor", "projects");
        if (filePath.StartsWith(cCursorProj, StringComparison.OrdinalIgnoreCase) && Directory.Exists(@"F:\AIChat\cursor\projects"))
        {
            return @"F:\AIChat\cursor\projects" + filePath[cCursorProj.Length..];
        }
        return filePath;
    }

    private static List<(string Role, string Time, string Text)> ExtractCodexTurns(string filePath, bool summaryOnlyFastSlice = false, int maxRecentTurns = 0)
    {
        try
        {
            long len = new FileInfo(filePath).Length;
            if (maxRecentTurns > 0 && len > 512 * 1024)
            {
                var turns = ParseCodexLines(ReadTailSliceLines(filePath, len, RecentTailFirstBytes));
                if (turns.Count < maxRecentTurns)
                    turns = ParseCodexLines(ReadTailSliceLines(filePath, len, RecentTailExpandedBytes));
                return TakeRecentTurns(turns, maxRecentTurns);
            }
            IEnumerable<string> lines = (summaryOnlyFastSlice && len > 512 * 1024)
                ? ReadLinesHeadTailSafe(filePath, len)
                : ReadAllLinesShared(filePath);
            return ParseCodexLines(lines);
        }
        catch
        {
            return new List<(string Role, string Time, string Text)>();
        }
    }

    private static List<(string Role, string Time, string Text)> ParseCodexLines(IEnumerable<string> lines)
    {
        var turns = new List<(string Role, string Time, string Text)>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"response_item\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "response_item") continue;
                if (!root.TryGetProperty("payload", out var payload)) continue;
                if (!payload.TryGetProperty("type", out var pType) || pType.GetString() != "message") continue;

                var role = payload.TryGetProperty("role", out var rEl) ? rEl.GetString() ?? "" : "";
                if (role != "user" && role != "assistant") continue;

                var ts = root.TryGetProperty("timestamp", out var tsEl) ? tsEl.GetString() ?? "" : "";
                if (!payload.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array) continue;

                var sb = new StringBuilder();
                foreach (var item in contentArr.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var txtEl))
                    {
                        var t = txtEl.GetString();
                        if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine(t);
                    }
                }

                var finalTxt = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(finalTxt))
                {
                    turns.Add((role, ts, finalTxt));
                }
            }
            catch { }
        }
        return turns;
    }

    private static List<(string Role, string Time, string Text)> ExtractCursorTurns(string filePath, bool summaryOnlyFastSlice = false, int maxRecentTurns = 0)
    {
        try
        {
            long len = new FileInfo(filePath).Length;
            if (maxRecentTurns > 0 && len > 512 * 1024)
            {
                var turns = ParseCursorLines(ReadTailSliceLines(filePath, len, RecentTailFirstBytes));
                if (turns.Count < maxRecentTurns)
                    turns = ParseCursorLines(ReadTailSliceLines(filePath, len, RecentTailExpandedBytes));
                return TakeRecentTurns(turns, maxRecentTurns);
            }
            IEnumerable<string> lines = (summaryOnlyFastSlice && len > 512 * 1024)
                ? ReadLinesHeadTailSafe(filePath, len)
                : ReadAllLinesShared(filePath);
            return ParseCursorLines(lines);
        }
        catch
        {
            return new List<(string Role, string Time, string Text)>();
        }
    }

    private static List<(string Role, string Time, string Text)> ParseCursorLines(IEnumerable<string> lines)
    {
        var turns = new List<(string Role, string Time, string Text)>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var role = root.TryGetProperty("role", out var rEl) ? rEl.GetString() ?? "" : "";
                if (role != "user" && role != "assistant") continue;

                var ts = root.TryGetProperty("timestamp", out var tsEl) ? tsEl.GetString() ?? "" : "";
                if (!root.TryGetProperty("message", out var msgEl)) continue;
                if (!msgEl.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array) continue;

                var sb = new StringBuilder();
                foreach (var item in contentArr.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var tp) && tp.GetString() == "text" &&
                        item.TryGetProperty("text", out var txtEl))
                    {
                        var raw = txtEl.GetString() ?? "";
                        raw = CleanCursorUserQueryTags(raw);
                        if (!string.IsNullOrWhiteSpace(raw)) sb.AppendLine(raw);
                    }
                }

                var finalTxt = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(finalTxt))
                {
                    turns.Add((role, ts, finalTxt));
                }
            }
            catch { }
        }
        return turns;
    }

    private static string CleanCursorUserQueryTags(string raw)
    {
        var m = Regex.Match(raw, @"<user_query>\s*(.*?)\s*</user_query>", RegexOptions.Singleline);
        if (m.Success) return m.Groups[1].Value.Trim();
        return raw.Trim();
    }

    public static string ConvertPathToCursorSlug(string fullPath)
    {
        var norm = Path.GetFullPath(fullPath).TrimEnd('\\', '/');
        var sb = new StringBuilder();
        foreach (var ch in norm)
        {
            if (ch == ':' || ch == '\\' || ch == '/')
            {
                if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static string NormalizePathForCompare(string path)
    {
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }
        catch
        {
            return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }
    }

    private static string TruncateSingleLine(string text, int maxLen)
    {
        var one = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length <= maxLen ? one : one[..maxLen] + "...";
    }
}
