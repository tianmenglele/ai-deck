using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AICollabHub.Models;

namespace AICollabHub.Services;

public sealed class ShareExportService
{
    public string ExportCleanPortableZip(string destZipPath)
    {
        var tempStage = Path.Combine(Path.GetTempPath(), $"AICollabHub_Share_{Guid.NewGuid():N}");
        var rootFolder = Path.Combine(tempStage, "AI协同中心_绿色版");
        Directory.CreateDirectory(rootFolder);

        try
        {
            // 1. 复制当前可执行文件与必要运行文件（绝不复制任何用户个人数据）
            var currentExe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(currentExe) && File.Exists(currentExe))
            {
                File.Copy(currentExe, Path.Combine(rootFolder, "AI协同中心.exe"), overwrite: true);
            }
            else
            {
                foreach (var f in Directory.GetFiles(AppPaths.AppBaseDir))
                {
                    var name = Path.GetFileName(f);
                    if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                    File.Copy(f, Path.Combine(rootFolder, name), overwrite: true);
                }
            }

            // 2. 复制明文开放活口目录 registry/ 与 plugins/
            CopyFolderClean(AppPaths.RegistryDir, Path.Combine(rootFolder, "registry"));
            CopyFolderClean(AppPaths.PluginsDir, Path.Combine(rootFolder, "plugins"));

            // 3. 生成开箱即用中文说明（零私人路径）
            var guide =
                "【多 AI 协同配置中心 · Windows 绿色便携版】\r\n\r\n" +
                "1. 开箱即用：无需安装 Python / Node.js，直接双击「AI协同中心.exe」即可运行。\r\n" +
                "2. 首次使用：\r\n" +
                "   - 顶部点击「项目设置 / 新建项目」选择你自己的工程文件夹（支持任意代码/游戏工程，单目录或多目录均可）；\r\n" +
                "   - 在「① 本机检测与接入」页勾选本机已安装的 AI 工具或外部 VPS 桥接文件夹；\r\n" +
                "   - 在「② 特性与分工设置」页按特长自动排班或手动指派每个环节由哪个 AI 负责；\r\n" +
                "   - 在「③ 规则/技能同步」页点击「一键全员同步」，即可让所有已勾选 AI 共享同一套规则与技能。\r\n" +
                "3. 扩展修改：同级目录 registry/*.json 与 plugins/ 均为明文开放配置，可直接增删新工具探针、特性标签与流程模板。\r\n" +
                "4. 个人数据隔离：你的项目列表与个人设置保存在本机 %LOCALAPPDATA%\\AICollabHub 中，不会污染本程序目录。\r\n";
            File.WriteAllText(Path.Combine(rootFolder, "使用说明.txt"), guide, new UTF8Encoding(false));

            var parent = Path.GetDirectoryName(destZipPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (File.Exists(destZipPath)) File.Delete(destZipPath);
            ZipFile.CreateFromDirectory(tempStage, destZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return destZipPath;
        }
        finally
        {
            try { Directory.Delete(tempStage, recursive: true); } catch { }
        }
    }

    public void ExportWorkflowTemplateBundle(RegistryService reg, string destJsonPath)
    {
        var bundle = new ShareableTemplateBundle
        {
            SchemaVersion = 1,
            ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Tags = reg.Tags.Tags,
            Workflows = reg.Workflows.Workflows,
            Detectors = reg.Detectors.Detectors
        };
        RegistryService.WriteJsonAtomic(destJsonPath, bundle);
    }

    public string ImportWorkflowTemplateBundle(RegistryService reg, string srcJsonPath)
    {
        var txt = File.ReadAllText(srcJsonPath, Encoding.UTF8);
        var bundle = JsonSerializer.Deserialize<ShareableTemplateBundle>(txt, RegistryService.JsonOpts)
                     ?? throw new InvalidOperationException("模板文件格式无效。");

        int addedTags = 0, addedWfs = 0, addedDets = 0;
        foreach (var t in bundle.Tags)
        {
            if (reg.Tags.Tags.All(x => x.Id != t.Id)) { reg.Tags.Tags.Add(t); addedTags++; }
        }
        foreach (var w in bundle.Workflows)
        {
            var idx = reg.Workflows.Workflows.FindIndex(x => x.Id == w.Id);
            if (idx >= 0) reg.Workflows.Workflows[idx] = w;
            else { reg.Workflows.Workflows.Add(w); addedWfs++; }
        }
        foreach (var d in bundle.Detectors)
        {
            if (reg.Detectors.Detectors.All(x => x.Id != d.Id)) { reg.Detectors.Detectors.Add(d); addedDets++; }
        }
        reg.SaveRegistries();
        return $"导入成功：新增 {addedTags} 个特性标签、{addedWfs} 条工作流模板、{addedDets} 条探测规则（同名工作流已更新）。";
    }

    private static void CopyFolderClean(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        if (!Directory.Exists(src)) return;
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }
}
