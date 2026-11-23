using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AICollabHub.Services;

public enum AppUpdateStatus
{
    UpToDate,
    UpdateAvailable,
    NeedsRepoConfig,
    IsolatedSkipped,
    NetworkFailed,
    InvalidResponse
}

public readonly record struct SemVerTriple(int Major, int Minor, int Patch)
{
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public sealed class AppUpdateEndpoint
{
    public string Kind { get; init; } = "";
    public string ApiUrl { get; init; } = "";
    public string TagsFallbackUrl { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public string Owner { get; init; } = "";
    public string Repo { get; init; } = "";
}

public sealed class AppReleaseInfo
{
    public string TagName { get; set; } = "";
    public string Version { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public string Notes { get; set; } = "";
    public string AssetDownloadUrl { get; set; } = "";
    public string AssetName { get; set; } = "";
}

public sealed class AppUpdateCheckResult
{
    public AppUpdateStatus Status { get; init; }
    public string Message { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string ReleasePageUrl { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Notes { get; init; } = "";
}

public sealed class AppUpdateApplyResult
{
    public bool Ok { get; init; }
    public bool RestartScheduled { get; init; }
    public bool OpenedBrowser { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// 软件自身版本展示与更新检查。隔离测试（<see cref="AppPaths.IsIsolatedTest"/>）禁止真实外网请求。
/// </summary>
public static class AppUpdateService
{
    public const string NeedsRepoConfigMessage =
        "尚未配置软件更新源。请在「软件设置」填写 GitHub / Gitee 仓库地址、owner/repo，或自定义版本 JSON 链接；若程序位于 Git 仓库内，检查时会自动读取 origin。";

    public const string IsolatedSkipMessage =
        "当前为隔离测试模式，已禁止向外网发起软件更新检查。";

    private static readonly Regex SemVerRegex = new(
        @"(?i)(?:^|[^0-9])v?(\d+)\.(\d+)\.(\d+)(?:[^0-9]|$)",
        RegexOptions.Compiled);

    public static string GetCurrentVersion()
    {
        var parsed = ParseNormalizedVersion(ReadAssemblyVersionToken());
        if (parsed.HasValue) return parsed.Value.ToString();
        parsed = ParseNormalizedVersion(AppPaths.AppVersion);
        return parsed.HasValue ? parsed.Value.ToString() : AppPaths.AppVersion;
    }

    public static string GetCurrentVersionDisplay()
    {
        var version = GetCurrentVersion();
        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : "v" + version;
    }

    /// <summary>检查更新与下载安装包时使用的 HTTP User-Agent。品牌为 AIDeck，版本号仍取程序集版本。</summary>
    public static string BuildHttpUserAgent(string? version = null)
    {
        var ver = string.IsNullOrWhiteSpace(version) ? GetCurrentVersion() : version.Trim();
        return $"AIDeck/{ver}";
    }

    public static SemVerTriple? ParseNormalizedVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var match = SemVerRegex.Match(raw.Trim());
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out int major)) return null;
        if (!int.TryParse(match.Groups[2].Value, out int minor)) return null;
        if (!int.TryParse(match.Groups[3].Value, out int patch)) return null;
        return new SemVerTriple(major, minor, patch);
    }

    public static bool IsNewerVersion(string? remote, string? local)
    {
        var remoteVer = ParseNormalizedVersion(remote);
        var localVer = ParseNormalizedVersion(local);
        if (!remoteVer.HasValue || !localVer.HasValue) return false;
        if (remoteVer.Value.Major != localVer.Value.Major) return remoteVer.Value.Major > localVer.Value.Major;
        if (remoteVer.Value.Minor != localVer.Value.Minor) return remoteVer.Value.Minor > localVer.Value.Minor;
        return remoteVer.Value.Patch > localVer.Value.Patch;
    }

    public static bool HasMzHeader(byte[]? header) =>
        header is { Length: >= 2 } && header[0] == 0x4D && header[1] == 0x5A;

    public static AppUpdateEndpoint ResolveUpdateEndpoint(string? raw)
    {
        var text = (raw ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(text))
            return new AppUpdateEndpoint();

        if (TryMatchScp(text, out var scpHost, out var scpOwner, out var scpRepo))
            return BuildHostEndpoint(scpHost, scpOwner, scpRepo);

        if (TryMatchSshUrl(text, out var sshHost, out var sshOwner, out var sshRepo))
            return BuildHostEndpoint(sshHost, sshOwner, sshRepo);

        if (TryMatchHttpRepo(text, out var httpHost, out var httpOwner, out var httpRepo, out bool knownHost))
        {
            if (knownHost)
                return BuildHostEndpoint(httpHost, httpOwner, httpRepo);
            return BuildCustomEndpoint(text);
        }

        var bareHost = Regex.Match(
            text,
            @"^(?<host>github\.com|gitee\.com)/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/?$",
            RegexOptions.IgnoreCase);
        if (bareHost.Success)
        {
            return BuildHostEndpoint(
                bareHost.Groups["host"].Value,
                bareHost.Groups["owner"].Value,
                bareHost.Groups["repo"].Value);
        }

        if (Regex.IsMatch(text, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
        {
            var parts = text.Split('/');
            return BuildHostEndpoint("github.com", parts[0], parts[1]);
        }

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return BuildCustomEndpoint(text);
        }

        return new AppUpdateEndpoint();
    }

    /// <summary>只读向上查找 .git/config（或 gitfile 指向的 gitdir）里的 origin url。</summary>
    public static string? TryDetectOriginRepoUrlFromGit(string? startDirectory = null, int maxParentHops = 12)
    {
        try
        {
            var start = string.IsNullOrWhiteSpace(startDirectory) ? AppPaths.AppBaseDir : startDirectory;
            var dir = new DirectoryInfo(start);
            int hops = Math.Max(0, maxParentHops);
            for (int i = 0; i <= hops && dir != null; i++)
            {
                var url = ReadOriginFromGitEntry(dir.FullName);
                if (!string.IsNullOrWhiteSpace(url)) return url.Trim();
                dir = dir.Parent;
            }
        }
        catch
        {
            // 只读探测失败时保持未配置
        }
        return null;
    }

    public static AppReleaseInfo? ParseReleaseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return null;
                root = root[0];
            }
            if (root.ValueKind != JsonValueKind.Object) return null;

            string tag = FirstString(root, "tag_name", "name", "version", "tag");
            var semver = ParseNormalizedVersion(tag);
            if (!semver.HasValue && string.IsNullOrWhiteSpace(tag)) return null;

            string notes = FirstString(root, "body", "changelog", "notes", "description", "message");
            string html = FirstString(root, "html_url", "release_url", "htmlUrl", "page_url");
            var (assetName, assetUrl) = PickAsset(root);
            if (string.IsNullOrWhiteSpace(assetUrl))
                assetUrl = FirstString(root, "download_url", "installer_url", "exe_url", "asset_url");

            return new AppReleaseInfo
            {
                TagName = string.IsNullOrWhiteSpace(tag) ? semver?.ToString() ?? "" : tag.Trim(),
                Version = semver?.ToString() ?? "",
                HtmlUrl = html,
                Notes = notes,
                AssetDownloadUrl = assetUrl,
                AssetName = assetName
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Task<AppUpdateCheckResult> CheckForUpdateAsync(
        string? configuredRepoUrl,
        CancellationToken cancellationToken = default) =>
        CheckForUpdateAsync(configuredRepoUrl, gitProbeDirectory: null, probeGitOrigin: true, maxParentHops: 12, cancellationToken);

    public static async Task<AppUpdateCheckResult> CheckForUpdateAsync(
        string? configuredRepoUrl,
        string? gitProbeDirectory,
        bool probeGitOrigin,
        int maxParentHops,
        CancellationToken cancellationToken = default)
    {
        string current = GetCurrentVersion();
        string source = (configuredRepoUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(source) && probeGitOrigin)
        {
            source = TryDetectOriginRepoUrlFromGit(gitProbeDirectory, maxParentHops) ?? "";
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return new AppUpdateCheckResult
            {
                Status = AppUpdateStatus.NeedsRepoConfig,
                Message = NeedsRepoConfigMessage,
                CurrentVersion = current
            };
        }

        if (AppPaths.IsIsolatedTest)
        {
            return new AppUpdateCheckResult
            {
                Status = AppUpdateStatus.IsolatedSkipped,
                Message = IsolatedSkipMessage,
                CurrentVersion = current
            };
        }

        var endpoint = ResolveUpdateEndpoint(source);
        if (string.IsNullOrWhiteSpace(endpoint.ApiUrl))
        {
            return new AppUpdateCheckResult
            {
                Status = AppUpdateStatus.InvalidResponse,
                Message = "无法识别更新源。请填写 GitHub / Gitee 仓库地址，或可直接访问的版本 JSON 链接。",
                CurrentVersion = current
            };
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BuildHttpUserAgent(current));
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

            using var response = await http.GetAsync(endpoint.ApiUrl, cancellationToken).ConfigureAwait(false);
            HttpResponseMessage used = response;
            HttpResponseMessage? fallback = null;
            try
            {
                if (response.StatusCode == HttpStatusCode.NotFound &&
                    !string.IsNullOrWhiteSpace(endpoint.TagsFallbackUrl))
                {
                    fallback = await http.GetAsync(endpoint.TagsFallbackUrl, cancellationToken).ConfigureAwait(false);
                    used = fallback;
                }

                if (!used.IsSuccessStatusCode)
                {
                    return new AppUpdateCheckResult
                    {
                        Status = AppUpdateStatus.NetworkFailed,
                        Message = $"检查更新失败：远端返回 {(int)used.StatusCode}。",
                        CurrentVersion = current,
                        ReleasePageUrl = endpoint.HtmlUrl
                    };
                }

                var json = await used.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var release = ParseReleaseJson(json);
                if (release == null || string.IsNullOrWhiteSpace(release.Version))
                {
                    return new AppUpdateCheckResult
                    {
                        Status = AppUpdateStatus.InvalidResponse,
                        Message = "已连上更新源，但没有解析到可用的版本号。",
                        CurrentVersion = current,
                        ReleasePageUrl = endpoint.HtmlUrl
                    };
                }

                if (string.IsNullOrWhiteSpace(release.HtmlUrl))
                    release.HtmlUrl = BuildReleasePage(endpoint, release.TagName);

                bool newer = IsNewerVersion(release.Version, current);
                string latestDisplay = release.Version.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    ? release.Version
                    : "v" + release.Version;
                string currentDisplay = GetCurrentVersionDisplay();
                return new AppUpdateCheckResult
                {
                    Status = newer ? AppUpdateStatus.UpdateAvailable : AppUpdateStatus.UpToDate,
                    Message = newer
                        ? $"AIDeck 发现新版本 {latestDisplay}（当前 {currentDisplay}）。"
                        : $"AIDeck 已是最新版本 {currentDisplay}。",
                    CurrentVersion = current,
                    LatestVersion = release.Version,
                    ReleasePageUrl = release.HtmlUrl,
                    DownloadUrl = release.AssetDownloadUrl,
                    Notes = release.Notes
                };
            }
            finally
            {
                fallback?.Dispose();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AppUpdateCheckResult
            {
                Status = AppUpdateStatus.NetworkFailed,
                Message = "检查更新超时（10 秒）。请稍后重试。",
                CurrentVersion = current
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new AppUpdateCheckResult
            {
                Status = AppUpdateStatus.NetworkFailed,
                Message = "检查更新失败：网络不可达或更新源无响应。",
                CurrentVersion = current
            };
        }
    }

    public static async Task<AppUpdateApplyResult> DownloadAndApplyExeUpdateAsync(
        string? downloadUrl,
        string? releasePageUrl,
        CancellationToken cancellationToken = default)
    {
        if (AppPaths.IsIsolatedTest)
        {
            return new AppUpdateApplyResult
            {
                Ok = false,
                Message = "隔离测试模式不会下载或替换当前程序。"
            };
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            bool opened = OpenBrowser(releasePageUrl);
            return new AppUpdateApplyResult
            {
                Ok = opened,
                OpenedBrowser = opened,
                Message = opened
                    ? "未提供安装包直链，已打开发布页。"
                    : "没有可用的安装包下载地址或发布页。"
            };
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "AICollabHubUpdates");
        Directory.CreateDirectory(tempDir);
        string tempExe = Path.Combine(tempDir, "update-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BuildHttpUserAgent(GetCurrentVersion()));
            using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                bool opened = OpenBrowser(releasePageUrl);
                return new AppUpdateApplyResult
                {
                    Ok = opened,
                    OpenedBrowser = opened,
                    Message = opened
                        ? $"下载失败（{(int)response.StatusCode}），已改为打开发布页。"
                        : $"下载失败（{(int)response.StatusCode}）。"
                };
            }

            await using (var fs = new FileStream(tempExe, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                fs.Position = 0;
                var head = new byte[2];
                int read = await fs.ReadAsync(head.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
                if (read < 2 || !HasMzHeader(head))
                {
                    fs.Close();
                    TryDelete(tempExe);
                    bool opened = OpenBrowser(releasePageUrl);
                    return new AppUpdateApplyResult
                    {
                        Ok = opened,
                        OpenedBrowser = opened,
                        Message = opened
                            ? "下载内容不是 Windows 可执行文件（缺少 MZ 头），已打开发布页。"
                            : "下载内容不是 Windows 可执行文件（缺少 MZ 头）。"
                    };
                }
            }

            string? currentExe = Environment.ProcessPath;
            bool canReplace = !string.IsNullOrWhiteSpace(currentExe) &&
                              currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(Path.GetFileName(currentExe), "dotnet.exe", StringComparison.OrdinalIgnoreCase);
            if (!canReplace)
            {
                bool opened = OpenBrowser(string.IsNullOrWhiteSpace(releasePageUrl) ? downloadUrl : releasePageUrl);
                return new AppUpdateApplyResult
                {
                    Ok = opened,
                    OpenedBrowser = opened,
                    Message = opened
                        ? "当前不是独立 exe 运行，已校验安装包并打开发布页，未替换正在运行的宿主。"
                        : "当前不是独立 exe 运行，已跳过替换。"
                };
            }

            string script =
                "/c ping 127.0.0.1 -n 6 >nul " +
                $"&& move /Y \"{currentExe}\" \"{currentExe}.old\" " +
                $"&& move /Y \"{tempExe}\" \"{currentExe}\" " +
                $"&& start \"\" \"{currentExe}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = script,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return new AppUpdateApplyResult
            {
                Ok = true,
                RestartScheduled = true,
                Message = "已下载并通过 MZ 校验，即将退出并由延迟命令替换后重启。"
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            TryDelete(tempExe);
            bool opened = OpenBrowser(releasePageUrl);
            return new AppUpdateApplyResult
            {
                Ok = opened,
                OpenedBrowser = opened,
                Message = opened
                    ? "下载更新失败，已改为打开发布页。"
                    : "下载更新失败。"
            };
        }
    }

    private static string ReadAssemblyVersionToken()
    {
        var assembly = typeof(AppUpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0) informational = informational[..plus];
            return informational.Trim();
        }

        var ver = assembly.GetName().Version;
        if (ver == null) return "";
        int patch = ver.Build >= 0 ? ver.Build : 0;
        return $"{ver.Major}.{ver.Minor}.{patch}";
    }

    private static bool TryMatchScp(string text, out string host, out string owner, out string repo)
    {
        host = owner = repo = "";
        var match = Regex.Match(
            text,
            @"^git@(?<host>[^:]+):(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        host = match.Groups["host"].Value;
        owner = match.Groups["owner"].Value;
        repo = StripGitSuffix(match.Groups["repo"].Value);
        return true;
    }

    private static bool TryMatchSshUrl(string text, out string host, out string owner, out string repo)
    {
        host = owner = repo = "";
        var match = Regex.Match(
            text,
            @"^ssh://(?:git@)?(?<host>[^/:]+)(?::\d+)?/(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        host = match.Groups["host"].Value;
        owner = match.Groups["owner"].Value;
        repo = StripGitSuffix(match.Groups["repo"].Value);
        return true;
    }

    private static bool TryMatchHttpRepo(
        string text,
        out string host,
        out string owner,
        out string repo,
        out bool knownHost)
    {
        host = owner = repo = "";
        knownHost = false;
        var match = Regex.Match(
            text,
            @"^https?://(?<host>[^/]+)/(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?(?:/.*)?$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        host = match.Groups["host"].Value;
        owner = match.Groups["owner"].Value;
        repo = StripGitSuffix(match.Groups["repo"].Value);
        knownHost = IsGitHubHost(host) || IsGiteeHost(host);
        return true;
    }

    private static AppUpdateEndpoint BuildHostEndpoint(string host, string owner, string repo)
    {
        owner = owner.Trim();
        repo = StripGitSuffix(repo.Trim());
        if (IsGiteeHost(host))
        {
            return new AppUpdateEndpoint
            {
                Kind = "gitee",
                Owner = owner,
                Repo = repo,
                ApiUrl = $"https://gitee.com/api/v5/repos/{owner}/{repo}/releases/latest",
                TagsFallbackUrl = $"https://gitee.com/api/v5/repos/{owner}/{repo}/tags",
                HtmlUrl = $"https://gitee.com/{owner}/{repo}/releases"
            };
        }

        if (IsGitHubHost(host) || string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new AppUpdateEndpoint
            {
                Kind = "github",
                Owner = owner,
                Repo = repo,
                ApiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest",
                TagsFallbackUrl = $"https://api.github.com/repos/{owner}/{repo}/tags",
                HtmlUrl = $"https://github.com/{owner}/{repo}/releases/latest"
            };
        }

        return new AppUpdateEndpoint();
    }

    private static AppUpdateEndpoint BuildCustomEndpoint(string url)
    {
        string tags = "";
        if (Regex.IsMatch(url, @"/releases/latest/?$", RegexOptions.IgnoreCase))
            tags = Regex.Replace(url, @"/releases/latest/?$", "/tags", RegexOptions.IgnoreCase);
        return new AppUpdateEndpoint
        {
            Kind = "custom",
            ApiUrl = url,
            TagsFallbackUrl = tags,
            HtmlUrl = url
        };
    }

    private static string BuildReleasePage(AppUpdateEndpoint endpoint, string tagName)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.HtmlUrl) &&
            !string.Equals(endpoint.Kind, "github", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint.HtmlUrl;
        }
        if (string.Equals(endpoint.Kind, "github", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(endpoint.Owner) &&
            !string.IsNullOrWhiteSpace(tagName))
        {
            return $"https://github.com/{endpoint.Owner}/{endpoint.Repo}/releases/tag/{Uri.EscapeDataString(tagName)}";
        }
        return endpoint.HtmlUrl;
    }

    private static bool IsGitHubHost(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        return h is "github.com" or "www.github.com";
    }

    private static bool IsGiteeHost(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        return h is "gitee.com" or "www.gitee.com";
    }

    private static string StripGitSuffix(string repo)
    {
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            return repo[..^4];
        return repo;
    }

    private static string? ReadOriginFromGitEntry(string directory)
    {
        var gitPath = Path.Combine(directory, ".git");
        if (Directory.Exists(gitPath))
            return ReadOriginUrl(Path.Combine(gitPath, "config"));
        if (!File.Exists(gitPath)) return null;

        string pointer;
        using (var fs = new FileStream(gitPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            pointer = reader.ReadToEnd();
        }
        var match = Regex.Match(pointer, @"gitdir:\s*(.+)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var gitDir = match.Groups[1].Value.Trim().Trim('"');
        if (!Path.IsPathRooted(gitDir))
            gitDir = Path.GetFullPath(Path.Combine(directory, gitDir));
        return ReadOriginUrl(Path.Combine(gitDir, "config"));
    }

    private static string? ReadOriginUrl(string configPath)
    {
        if (!File.Exists(configPath)) return null;
        string[] lines;
        using (var fs = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            var text = reader.ReadToEnd();
            lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        bool inOrigin = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inOrigin = Regex.IsMatch(line, @"^\[remote\s+""origin""\]$", RegexOptions.IgnoreCase);
                continue;
            }
            if (!inOrigin) continue;
            var match = Regex.Match(line, @"^url\s*=\s*(.+)$", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return null;
    }

    private static (string Name, string Url) PickAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return ("", "");

        string bestName = "";
        string bestUrl = "";
        int bestScore = int.MinValue;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            string name = FirstString(asset, "name", "file_name");
            string url = FirstString(asset, "browser_download_url", "download_url", "url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            int score = ScoreAsset(name, url);
            if (score > bestScore)
            {
                bestScore = score;
                bestName = name;
                bestUrl = url;
            }
        }
        return (bestName, bestUrl);
    }

    private static int ScoreAsset(string name, string url)
    {
        var blob = (name + " " + url).ToLowerInvariant();
        int score = 1;
        if (blob.Contains(".exe", StringComparison.Ordinal)) score += 100;
        if (blob.Contains("win", StringComparison.Ordinal)) score += 10;
        if (blob.Contains("x64", StringComparison.Ordinal) || blob.Contains("win64", StringComparison.Ordinal)) score += 5;
        if (blob.Contains(".zip", StringComparison.Ordinal)) score += 2;
        return score;
    }

    private static string FirstString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return "";
    }

    private static bool OpenBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时包删除失败不影响后续提示
        }
    }
}
