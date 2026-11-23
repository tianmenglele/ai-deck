using System.IO;
using Microsoft.Win32;

namespace AICollabHub.Services;

public sealed class StartupAndShortcutService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AICollabHub";

    public static string ResolveCurrentExecutablePath()
    {
        var procPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(procPath) && File.Exists(procPath))
        {
            return Path.GetFullPath(procPath);
        }
        return Path.GetFullPath(Path.Combine(AppPaths.AppBaseDir, "AI协同中心.exe"));
    }

    public (bool IsRegistered, string CommandLine) QueryStartupRegistryStatus()
    {
        if (AppPaths.IsIsolatedTest)
        {
            return (false, "隔离测试模式不读写系统注册表");
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            var val = key?.GetValue(RunValueName) as string;
            if (!string.IsNullOrWhiteSpace(val))
            {
                return (true, val);
            }
        }
        catch (Exception ex)
        {
            return (false, $"读取失败: {ex.Message}");
        }

        return (false, "未注册开机自启");
    }

    public (bool Ok, string Message) ApplyStartupSetting(bool enableStartup, bool directFloatingMode)
    {
        if (AppPaths.IsIsolatedTest)
        {
            return (true, "隔离测试模式跳过注册表写入");
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);

            if (!enableStartup)
            {
                if (key.GetValue(RunValueName) != null)
                {
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
                return (true, "已取消开机自动启动（已从 HKCU\\...\\Run 移除 AICollabHub）。");
            }

            var exePath = ResolveCurrentExecutablePath();
            var args = directFloatingMode ? " --floating" : "";
            var cmd = $"\"{exePath}\"{args}";
            key.SetValue(RunValueName, cmd, RegistryValueKind.String);
            return (true, $"已开启开机自启动：登录 Windows 时自动运行 {cmd}");
        }
        catch (Exception ex)
        {
            return (false, $"设置开机自启动失败：{ex.Message}");
        }
    }

    public (bool Ok, string Message) CreateDesktopShortcuts()
    {
        try
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktopDir) || !Directory.Exists(desktopDir))
            {
                return (false, "未找到当前用户的桌面目录。");
            }

            var exePath = ResolveCurrentExecutablePath();
            var desktopExePath = Path.Combine(desktopDir, "AI协同中心.exe");
            if (!string.Equals(exePath, desktopExePath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(exePath, desktopExePath, overwrite: true);
            }

            return (true, $"已将带专属图标的独立单文件程序「AI协同中心.exe」直接放置到您的桌面：{desktopExePath}");
        }
        catch (Exception ex)
        {
            return (false, $"放置桌面 EXE 程序失败：{ex.Message}");
        }
    }
}
