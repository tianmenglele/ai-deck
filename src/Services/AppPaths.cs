using System.IO;

namespace AICollabHub.Services;

public static class AppPaths
{
    public const string AppVersion = "1.9.17";
    private static string? _overrideDataDir;
    public static bool IsIsolatedTest => _overrideDataDir != null;

    public static void SetDataDirOverride(string? dir)
    {
        if (!string.IsNullOrWhiteSpace(dir))
        {
            _overrideDataDir = Path.GetFullPath(dir);
        }
    }

    public static string AppBaseDir
    {
        get
        {
            var exeDir = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
                ? Path.GetDirectoryName(Environment.ProcessPath)
                : null;
            var raw = !string.IsNullOrWhiteSpace(exeDir) ? exeDir : AppContext.BaseDirectory;
            return raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public static string RegistryDir
    {
        get
        {
            var direct = Path.Combine(AppBaseDir, "registry");
            if (Directory.Exists(direct) && File.Exists(Path.Combine(direct, "workflows.json")))
                return direct;

            // 向上查找工程内的 registry 目录
            var cur = new DirectoryInfo(AppBaseDir);
            for (int i = 0; i < 5 && cur.Parent != null; i++)
            {
                cur = cur.Parent;
                var cand = Path.Combine(cur.FullName, "registry");
                if (Directory.Exists(cand) && File.Exists(Path.Combine(cand, "workflows.json")))
                    return cand;
            }

            // 独立单文件模式：回落到用户数据目录下的 registry，绝不在桌面生成杂乱文件夹，也不硬编码私人仓库路径
            var fallbackReg = Path.Combine(UserDataDir, "registry");
            Directory.CreateDirectory(fallbackReg);
            return fallbackReg;
        }
    }

    public static string PluginsDir
    {
        get
        {
            var parent = Directory.GetParent(RegistryDir)?.FullName ?? AppBaseDir;
            var dir = Path.Combine(parent, "plugins");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string UserDataDir
    {
        get
        {
            var dir = _overrideDataDir
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AICollabHub");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string UserConfigFile => Path.Combine(UserDataDir, "user_hub_config.json");

    /// <summary>与 <see cref="UserConfigFile"/> 同一路径。新手引导用它判断配置文件在本次读取前是否已在磁盘上。</summary>
    public static string UserConfigPath => UserConfigFile;

    public static string BackupsDir
    {
        get
        {
            var dir = Path.Combine(UserDataDir, "backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void PruneDirectoryKeepLatest(string parentDir, int maxKeep = 3)
    {
        if (!Directory.Exists(parentDir)) return;
        var subDirs = new DirectoryInfo(parentDir)
            .GetDirectories()
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .ToList();
        for (int i = maxKeep; i < subDirs.Count; i++)
        {
            try
            {
                if ((subDirs[i].Attributes & FileAttributes.ReparsePoint) != 0) continue;
                subDirs[i].Delete(recursive: true);
            }
            catch
            {
                // 忽略正在占用的目录
            }
        }
    }
}
