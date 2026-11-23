using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AICollabHub.Models;

namespace AICollabHub.Services;

public sealed class CollabBusService
{
    public string GetCollabDir(ProjectProfile project) =>
        Path.Combine(project.PrimaryRoot, project.CollabRelDir.Replace('/', Path.DirectorySeparatorChar));

    public string GetTaskFilePath(ProjectProfile project) =>
        Path.Combine(GetCollabDir(project), "TASK.md");

    public string GetLatestDiffPath(ProjectProfile project) =>
        Path.Combine(GetCollabDir(project), "LATEST.diff");

    public bool IsGitRepo(ProjectProfile project) =>
        !string.IsNullOrWhiteSpace(project.PrimaryRoot) && Directory.Exists(Path.Combine(project.PrimaryRoot, ".git"));

    private static DateTime _lastConflictProbeUtc = DateTime.MinValue;
    private static string _lastConflictProjectId = "";
    private static string _lastConflictResult = "无编辑器锁冲突";

    public string CheckEditorConflictWarning(ProjectProfile project)
    {
        if (project.PresetId != "godot_project") return "无编辑器锁冲突";
        if (string.Equals(_lastConflictProjectId, project.Id, StringComparison.OrdinalIgnoreCase) &&
            (DateTime.UtcNow - _lastConflictProbeUtc).TotalSeconds < 10)
        {
            return _lastConflictResult;
        }

        string result = "无编辑器锁冲突";
        Process[]? all = null;
        try
        {
            all = Process.GetProcesses();
            int godotCount = all.Count(p =>
            {
                try { return p.ProcessName.Contains("Godot", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
            if (godotCount > 0)
            {
                result = $"检测到 Godot 正在运行 ({godotCount} 个实例)：后台修改 .tscn 前请确认编辑器无未保存改动";
            }
        }
        catch
        {
            // 忽略权限限制
        }
        finally
        {
            if (all != null)
            {
                foreach (var p in all)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }

        _lastConflictProjectId = project.Id;
        _lastConflictProbeUtc = DateTime.UtcNow;
        _lastConflictResult = result;
        return result;
    }

    public void EnsureTaskCard(ProjectProfile project)
    {
        var dir = GetCollabDir(project);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "inbox"));
        Directory.CreateDirectory(Path.Combine(dir, "sandbox"));
        Directory.CreateDirectory(Path.Combine(dir, "archive"));

        var taskFile = GetTaskFilePath(project);
        if (!File.Exists(taskFile))
        {
            File.WriteAllText(taskFile, BuildBlankTaskTemplate(project.Name), new UTF8Encoding(false));
        }
    }

    public string ReadTaskCard(ProjectProfile project)
    {
        EnsureTaskCard(project);
        return File.ReadAllText(GetTaskFilePath(project), Encoding.UTF8);
    }

    public void SaveTaskCard(ProjectProfile project, string content)
    {
        EnsureTaskCard(project);
        File.WriteAllText(GetTaskFilePath(project), content, new UTF8Encoding(false));
    }

    public string CreateBaselineSnapshot(RegistryService reg, ProjectProfile project)
    {
        EnsureTaskCard(project);
        var collabDir = GetCollabDir(project);
        var snapBase = Path.Combine(collabDir, ".snaps", "baseline");
        if (Directory.Exists(snapBase)) Directory.Delete(snapBase, recursive: true);
        Directory.CreateDirectory(snapBase);

        var preset = reg.Presets.Presets.FirstOrDefault(p => p.Id == project.PresetId) ?? new ProjectPresetSpec();
        var files = CollectTrackedTextFiles(project.PrimaryRoot, preset);
        var indexLines = new List<string>();

        foreach (var absFile in files)
        {
            var rel = Path.GetRelativePath(project.PrimaryRoot, absFile);
            var dst = Path.Combine(snapBase, rel);
            var parent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(absFile, dst, overwrite: true);
            indexLines.Add(rel);
        }

        File.WriteAllLines(Path.Combine(snapBase, "_index.txt"), indexLines, new UTF8Encoding(false));
        return $"已创建基准快照（共记录 {files.Count} 个源码/资源文本文件，时间 {DateTime.Now:HH:mm:ss}）。";
    }

    public string GenerateLatestDiff(RegistryService reg, ProjectProfile project)
    {
        EnsureTaskCard(project);
        var diffPath = GetLatestDiffPath(project);

        if (IsGitRepo(project))
        {
            var gitDiff = RunHiddenProcess("git", $"-C \"{project.PrimaryRoot}\" diff");
            if (!string.IsNullOrWhiteSpace(gitDiff))
            {
                var header = $"# Git 原生工作区变更对比 ({DateTime.Now:yyyy-MM-dd HH:mm:ss})\n\n";
                File.WriteAllText(diffPath, header + gitDiff, new UTF8Encoding(false));
                return $"已通过原生 Git 生成 LATEST.diff（长度 {gitDiff.Length} 字符）。";
            }
        }

        var snapBase = Path.Combine(GetCollabDir(project), ".snaps", "baseline");
        var indexFile = Path.Combine(snapBase, "_index.txt");
        if (!File.Exists(indexFile))
        {
            CreateBaselineSnapshot(reg, project);
            File.WriteAllText(diffPath, "# 首次初始化基准快照，尚无增量改动。\n", new UTF8Encoding(false));
            return "尚未建立基准快照，已自动为你创建初始快照（后续修改再点即可生成增量对比）。";
        }

        var preset = reg.Presets.Presets.FirstOrDefault(p => p.Id == project.PresetId) ?? new ProjectPresetSpec();
        var currentFiles = CollectTrackedTextFiles(project.PrimaryRoot, preset)
            .ToDictionary(f => Path.GetRelativePath(project.PrimaryRoot, f), f => f, StringComparer.OrdinalIgnoreCase);
        var oldRels = File.ReadAllLines(indexFile, Encoding.UTF8)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine($"# 内置无 Git 快照行级变更单 ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        int changedCount = 0;

        foreach (var kv in currentFiles.OrderBy(k => k.Key))
        {
            var rel = kv.Key;
            var curAbs = kv.Value;
            var oldAbs = Path.Combine(snapBase, rel);
            if (!oldRels.Contains(rel) || !File.Exists(oldAbs))
            {
                changedCount++;
                sb.AppendLine($"\n--- /dev/null\n+++ b/{rel.Replace('\\', '/')}");
                foreach (var line in ReadSafeLines(curAbs).Take(120))
                    sb.AppendLine("+" + line);
            }
            else if (!FilesEqualFast(oldAbs, curAbs))
            {
                changedCount++;
                AppendSimpleUnifiedDiff(sb, rel.Replace('\\', '/'), ReadSafeLines(oldAbs), ReadSafeLines(curAbs));
            }
        }

        foreach (var deletedRel in oldRels.Except(currentFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            changedCount++;
            sb.AppendLine($"\n--- a/{deletedRel.Replace('\\', '/')}\n+++ /dev/null\n@@ 文件已删除 @@");
        }

        if (changedCount == 0)
        {
            sb.AppendLine("\n（与基准快照相比无任何文件变更）");
        }

        File.WriteAllText(diffPath, sb.ToString(), new UTF8Encoding(false));
        return $"已生成 LATEST.diff：检测到 {changedCount} 个文件发生变更。";
    }

    public string RollbackToBaseline(RegistryService reg, ProjectProfile project)
    {
        var collabDir = GetCollabDir(project);
        var snapBase = Path.Combine(collabDir, ".snaps", "baseline");
        var indexFile = Path.Combine(snapBase, "_index.txt");
        if (!File.Exists(indexFile))
            return "未找到基准快照，无法执行回滚。";

        // 先将当前发生变动的文件存入 safety 目录保护草稿
        var safetyDir = Path.Combine(collabDir, ".snaps", $"pre_rollback_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(safetyDir);

        var oldRels = File.ReadAllLines(indexFile, Encoding.UTF8)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int restored = 0;
        foreach (var rel in oldRels)
        {
            var snapFile = Path.Combine(snapBase, rel);
            var liveFile = Path.Combine(project.PrimaryRoot, rel);
            if (!File.Exists(snapFile)) continue;
            if (!File.Exists(liveFile) || !FilesEqualFast(snapFile, liveFile))
            {
                if (File.Exists(liveFile))
                {
                    var safeCopy = Path.Combine(safetyDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(safeCopy)!);
                    File.Copy(liveFile, safeCopy, overwrite: true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(liveFile)!);
                File.Copy(snapFile, liveFile, overwrite: true);
                restored++;
            }
        }

        AppPaths.PruneDirectoryKeepLatest(Path.Combine(collabDir, ".snaps"), 4);
        return restored == 0
            ? "当前文件与基准快照完全一致，无需回滚。"
            : $"已安全回滚 {restored} 个文件至基准快照（回滚前草稿已留存于 .snaps/{Path.GetFileName(safetyDir)}）。";
    }

    public string BuildSanitizedRemotePack(
        RegistryService reg,
        ProjectProfile project,
        bool includeRules,
        bool includeSchemaHooks,
        bool includeTaskAndDiff)
    {
        EnsureTaskCard(project);
        var collabDir = GetCollabDir(project);

        if (includeSchemaHooks)
        {
            RunEnabledHooks(project);
        }

        var sb = new StringBuilder();
        sb.AppendLine("# 远程 AI 协同上下文胶囊 (REMOTE_PACK)");
        sb.AppendLine($"> 项目：{project.Name} | 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("> 请基于以下项目约束与当前任务上下文输出方案、审查结论或代码补丁（不要编造未列出的字段名）。");
        sb.AppendLine();

        if (includeRules)
        {
            var agentsMd = Path.Combine(project.PrimaryRoot, "AGENTS.md");
            if (File.Exists(agentsMd))
            {
                sb.AppendLine("## 一、 项目协作规范摘要");
                sb.AppendLine(File.ReadAllText(agentsMd, Encoding.UTF8));
                sb.AppendLine();
            }
            var pitfalls = Path.Combine(project.PrimaryRoot, project.MemoryRelDir.Replace('/', Path.DirectorySeparatorChar), "shared-pitfalls.md");
            if (File.Exists(pitfalls))
            {
                sb.AppendLine("## 二、 近期共读避坑记录");
                sb.AppendLine(File.ReadAllText(pitfalls, Encoding.UTF8));
                sb.AppendLine();
            }
        }

        if (includeSchemaHooks)
        {
            var schemaFile = Path.Combine(collabDir, "SCHEMA_DIGEST.md");
            if (File.Exists(schemaFile))
            {
                sb.AppendLine("## 三、 数据表与字段速查");
                sb.AppendLine(File.ReadAllText(schemaFile, Encoding.UTF8));
                sb.AppendLine();
            }
        }

        if (includeTaskAndDiff)
        {
            sb.AppendLine("## 四、 当前任务活卡 (TASK.md)");
            sb.AppendLine(ReadTaskCard(project));
            sb.AppendLine();

            var diffFile = GetLatestDiffPath(project);
            if (File.Exists(diffFile))
            {
                var diffTxt = File.ReadAllText(diffFile, Encoding.UTF8);
                if (diffTxt.Length > 28000) diffTxt = diffTxt[..28000] + "\n... (Diff 超长已截断) ...";
                sb.AppendLine("## 五、 最近代码变更对比 (LATEST.diff)");
                sb.AppendLine("```diff");
                sb.AppendLine(diffTxt.Trim());
                sb.AppendLine("```");
            }
        }

        var sanitized = SanitizeSensitiveContent(sb.ToString(), project.PrimaryRoot);
        var packPath = Path.Combine(collabDir, "REMOTE_PACK.md");
        File.WriteAllText(packPath, sanitized, new UTF8Encoding(false));
        return packPath;
    }

    public string SaveRemoteReplyToInbox(ProjectProfile project, string senderName, string content)
    {
        EnsureTaskCard(project);
        var safeSender = string.Concat((string.IsNullOrWhiteSpace(senderName) ? "remote_ai" : senderName).Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        var inboxFile = Path.Combine(GetCollabDir(project), "inbox", $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeSender}.md");
        File.WriteAllText(inboxFile, content, new UTF8Encoding(false));
        return inboxFile;
    }

    public string ArchiveCurrentTask(ProjectProfile project, string? newPitfallNote)
    {
        EnsureTaskCard(project);
        var taskFile = GetTaskFilePath(project);
        var archiveFile = Path.Combine(GetCollabDir(project), "archive", $"task_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        File.Copy(taskFile, archiveFile, overwrite: true);
        File.WriteAllText(taskFile, BuildBlankTaskTemplate(project.Name), new UTF8Encoding(false));

        string pitfallMsg = "";
        if (!string.IsNullOrWhiteSpace(newPitfallNote))
        {
            var memDir = Path.Combine(project.PrimaryRoot, project.MemoryRelDir.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(memDir);
            var pf = Path.Combine(memDir, "shared-pitfalls.md");
            File.AppendAllText(pf, $"- [{DateTime.Now:yyyy-MM-dd}] {newPitfallNote.Trim()}\n", new UTF8Encoding(false));
            pitfallMsg = "，并已将新避坑经验沉淀至共读经验库";
        }

        return $"已归档至 archive/{Path.GetFileName(archiveFile)} 并重置空白 TASK.md{pitfallMsg}。";
    }

    private void RunEnabledHooks(ProjectProfile project)
    {
        foreach (var hookName in project.EnabledHooks)
        {
            var scriptPath = Path.Combine(AppPaths.PluginsDir, hookName);
            if (!File.Exists(scriptPath)) continue;
            if (scriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                var outDigest = Path.Combine(GetCollabDir(project), "SCHEMA_DIGEST.md");
                RunHiddenProcess("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -ProjectRoot \"{project.PrimaryRoot}\" -OutputFile \"{outDigest}\"");
            }
        }
    }

    public static string SanitizeSensitiveContent(string raw, string projectRoot)
    {
        var text = raw;
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            text = text.Replace(projectRoot, "${PROJECT_ROOT}", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(projectRoot.Replace('\\', '/'), "${PROJECT_ROOT}", StringComparison.OrdinalIgnoreCase);
        }
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userHome))
        {
            text = text.Replace(userHome, "${USER_HOME}", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(userHome.Replace('\\', '/'), "${USER_HOME}", StringComparison.OrdinalIgnoreCase);
        }
        // 过滤常见密钥模式
        text = Regex.Replace(text, @"(sk-[A-Za-z0-9_\-]{16,}|xai-[A-Za-z0-9_\-]{16,}|Bearer\s+[A-Za-z0-9_\-\.]{16,})", "[REDACTED_SECRET]");
        return text;
    }

    private static List<string> CollectTrackedTextFiles(string rootDir, ProjectPresetSpec preset)
    {
        var result = new List<string>();
        if (!Directory.Exists(rootDir)) return result;

        var exts = (preset.SnapshotExtensions.Count > 0
                ? preset.SnapshotExtensions
                : new List<string> { ".cs", ".py", ".gd", ".tscn", ".tres", ".js", ".ts", ".json", ".md", ".ps1" })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var excluded = preset.ExcludedDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0 && result.Count < 4500)
        {
            var cur = stack.Pop();
            try
            {
                foreach (var sub in Directory.GetDirectories(cur))
                {
                    var di = new DirectoryInfo(sub);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (excluded.Contains(di.Name)) continue;
                    stack.Push(sub);
                }

                foreach (var f in Directory.GetFiles(cur))
                {
                    var ext = Path.GetExtension(f);
                    if (!exts.Contains(ext)) continue;
                    var fi = new FileInfo(f);
                    if (fi.Length > 2 * 1024 * 1024) continue; // 跳过超 2MB 大数据表以保毫秒级响应
                    result.Add(f);
                }
            }
            catch
            {
                // 忽略受限子目录
            }
        }
        return result;
    }

    private static bool FilesEqualFast(string f1, string f2)
    {
        var i1 = new FileInfo(f1);
        var i2 = new FileInfo(f2);
        if (i1.Length != i2.Length) return false;
        using var s1 = File.OpenRead(f1);
        using var s2 = File.OpenRead(f2);
        var h1 = SHA256.HashData(s1);
        var h2 = SHA256.HashData(s2);
        return h1.AsSpan().SequenceEqual(h2);
    }

    private static string[] ReadSafeLines(string path)
    {
        try { return File.ReadAllLines(path, Encoding.UTF8); }
        catch { return Array.Empty<string>(); }
    }

    private static void AppendSimpleUnifiedDiff(StringBuilder sb, string relPath, string[] oldLines, string[] newLines)
    {
        sb.AppendLine($"\n--- a/{relPath}\n+++ b/{relPath}");
        int max = Math.Max(oldLines.Length, newLines.Length);
        int printed = 0;
        for (int i = 0; i < max && printed < 80; i++)
        {
            var o = i < oldLines.Length ? oldLines[i] : null;
            var n = i < newLines.Length ? newLines[i] : null;
            if (o == n) continue;
            sb.AppendLine($"@@ line {i + 1} @@");
            if (o != null) { sb.AppendLine("-" + o); printed++; }
            if (n != null) { sb.AppendLine("+" + n); printed++; }
        }
        if (printed >= 80) sb.AppendLine("... (该文件其余差异已折叠) ...");
    }

    private static string BuildBlankTaskTemplate(string projName) =>
        $"# 当前协同任务活卡 ({projName})\n\n" +
        $"更新时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
        "当前写盘锁：`无（空闲）`\n\n" +
        "## 1. 任务目标与设计规格（参谋槽 / 你填写）\n" +
        "- 待填写本轮需求或边界条件。\n\n" +
        "## 2. 底层实装清单与待微调参数入口（主力守门槽填写）\n" +
        "- 已改文件：暂无\n" +
        "- 留给前台精调的参数位置：暂无\n\n" +
        "## 3. 异构审查与实机验收结论（红军槽 / 守门槽填写）\n" +
        "- 审查意见：暂无\n" +
        "- 验收证据：暂无\n";

    private static string RunHiddenProcess(string fileName, string arguments)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch
        {
            return "";
        }
    }
}
