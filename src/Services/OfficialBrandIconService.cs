using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AICollabHub.Models;

namespace AICollabHub.Services;

/// <summary>
/// 优先加载内嵌 LobeHub 官方正圆透明 PNG（Assets/icons/{id}.png）；
/// 未知自定义 AI 才用集群矢量圆标兜底。结果经静态线程安全缓存并 Freeze。
/// </summary>
public static class OfficialBrandIconService
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> EmbeddedBrandIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "antigravity", "cursor", "grok_bot", "codex_cli", "claude_code"
    };

    private static readonly Dictionary<string, (string Color, string Label, string CategoryZh)> BrandMeta =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["antigravity"] = ("#8B5CF6", "AG", "AI 桌面客户端"),
            ["cursor"] = ("#0EA5E9", "Cu", "AI IDE / 编辑器"),
            ["windsurf"] = ("#06B6D4", "Ws", "AI IDE / 编辑器"),
            ["trae"] = ("#000000", "Tr", "AI IDE / 编辑器"),
            ["kiro"] = ("#FF9900", "Ki", "AI IDE / 编辑器"),
            ["zed"] = ("#146AFF", "Ze", "AI IDE / 编辑器"),
            ["codebuddy"] = ("#0052D9", "CB", "AI IDE / 编辑器"),
            ["qoder"] = ("#FF6A00", "Qd", "AI IDE / 编辑器"),
            ["void"] = ("#111827", "Vd", "AI IDE / 编辑器"),
            ["pearai"] = ("#84CC16", "Pe", "AI IDE / 编辑器"),
            ["jetbrains_ai"] = ("#FE2858", "JB", "AI IDE / 编辑器"),
            ["vscode_copilot"] = ("#24292F", "CP", "本地大模型与扩展"),
            ["cline"] = ("#F97316", "Cl", "本地大模型与扩展"),
            ["roo_code"] = ("#A855F7", "Ro", "本地大模型与扩展"),
            ["continue_dev"] = ("#22C55E", "Co", "本地大模型与扩展"),
            ["lingma"] = ("#FF6A00", "灵", "本地大模型与扩展"),
            ["augment"] = ("#6366F1", "Au", "本地大模型与扩展"),
            ["marscode"] = ("#3370FF", "火星", "本地大模型与扩展"),
            ["grok_bot"] = ("#111111", "Gk", "AI 桌面客户端"),
            ["codex_cli"] = ("#10A37F", "Cx", "AI 命令行 CLI / Agent"),
            ["claude_desktop"] = ("#D97706", "Cd", "AI 桌面客户端"),
            ["claude_code"] = ("#D97706", "CC", "AI 命令行 CLI / Agent"),
            ["chatgpt_desktop"] = ("#10A37F", "GPT", "AI 桌面客户端"),
            ["cherry_studio"] = ("#EC4899", "Ch", "AI 桌面客户端"),
            ["chatbox"] = ("#3B82F6", "Cb", "AI 桌面客户端"),
            ["doubao"] = ("#3B82F6", "豆", "AI 桌面客户端"),
            ["kimi_desktop"] = ("#1E293B", "Ki", "AI 桌面客户端"),
            ["devin"] = ("#0B7A5E", "Dv", "AI 桌面客户端"),
            ["workbuddy"] = ("#0052D9", "WB", "AI 桌面客户端"),
            ["copilot_desktop"] = ("#0078D4", "Co", "AI 桌面客户端"),
            ["gemini_desktop"] = ("#4285F4", "Gm", "AI 桌面客户端"),
            ["deepseek_desktop"] = ("#4D6BFE", "DS", "AI 桌面客户端"),
            ["yuanbao"] = ("#12B7F5", "元", "AI 桌面客户端"),
            ["anythingllm"] = ("#0EA5E9", "AL", "AI 桌面客户端"),
            ["jan"] = ("#22C55E", "Jan", "AI 桌面客户端"),
            ["perplexity"] = ("#20808D", "Px", "AI 桌面客户端"),
            ["gemini_cli"] = ("#4285F4", "Ge", "AI 命令行 CLI / Agent"),
            ["opencode"] = ("#F59E0B", "Oc", "AI 命令行 CLI / Agent"),
            ["aider"] = ("#14B8A6", "Ai", "AI 命令行 CLI / Agent"),
            ["qwen_code"] = ("#615CED", "Qw", "AI 命令行 CLI / Agent"),
            ["goose"] = ("#F97316", "Go", "AI 命令行 CLI / Agent"),
            ["amp"] = ("#A855F7", "Amp", "AI 命令行 CLI / Agent"),
            ["amazon_q"] = ("#FF9900", "AQ", "AI 命令行 CLI / Agent"),
            ["copilot_cli"] = ("#24292F", "gh", "AI 命令行 CLI / Agent"),
            ["openclaw"] = ("#64748B", "Claw", "AI 命令行 CLI / Agent"),
            ["droid"] = ("#EF4444", "Dr", "AI 命令行 CLI / Agent"),
            ["ollama_local"] = ("#111827", "Ol", "本地大模型与扩展"),
            ["lm_studio"] = ("#6D28D9", "LM", "本地大模型与扩展"),
            ["takt_vps_bridge"] = ("#60A5FA", "VPS", "远程服务器节点")
        };

    public static string GetCategoryZh(string? agentId, string? category = null)
    {
        if (!string.IsNullOrWhiteSpace(agentId) && BrandMeta.TryGetValue(agentId.Trim(), out var meta))
            return meta.CategoryZh;
        return (category ?? "").Trim().ToLowerInvariant() switch
        {
            "local_ide" => "AI IDE / 编辑器",
            "local_agent" => "AI 桌面客户端",
            "local_cli" => "AI 命令行 CLI / Agent",
            "ide_extension" => "本地大模型与扩展",
            "local_llm" => "本地大模型与扩展",
            "vps_bridge" => "远程服务器节点",
            "remote_capsule" => "远程服务器节点",
            _ => "扩展工具"
        };
    }

    public static string GetBrandColor(string? agentId)
    {
        if (!string.IsNullOrWhiteSpace(agentId) && BrandMeta.TryGetValue(agentId.Trim(), out var meta))
            return meta.Color;
        return "#6366F1";
    }

    /// <summary>按 agent.Id 解析高清官方头像（已 Freeze，可跨线程只读共享）。</summary>
    public static ImageSource GetOfficialIcon(string? agentId, AgentRosterEntry? agent = null)
    {
        string id = string.IsNullOrWhiteSpace(agentId) ? (agent?.Id ?? "unknown") : agentId.Trim();
        return Cache.GetOrAdd(id, key =>
        {
            ImageSource? embedded = TryLoadEmbeddedOfficialPng(key);
            // windsurf / vps_remote / 自定义 ID：无官方 PNG 一律彩色字母徽标，集群图仅作最终兜底
            ImageSource src = embedded ?? TryBuildMonogram(key, agent?.Name) ?? BuildServerCluster();
            if (src.CanFreeze && !src.IsFrozen) src.Freeze();
            return src;
        });
    }

    /// <summary>自检用：强制解析并返回非空图标。</summary>
    public static ImageSource GetOfficialIconForTest(string agentId)
        => GetOfficialIcon(agentId, new AgentRosterEntry { Id = agentId, Name = agentId });

    /// <summary>加载 pack:// 内嵌官方正圆 PNG（DecodePixelWidth=128）。</summary>
    public static BitmapSource? TryLoadEmbeddedOfficialPng(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || !EmbeddedBrandIds.Contains(agentId))
            return null;

        try
        {
            // 单文件 / 自检场景确保 pack URI 能解析到本程序集资源
            if (Application.ResourceAssembly == null)
                Application.ResourceAssembly = typeof(OfficialBrandIconService).Assembly;

            var uri = new Uri($"pack://application:,,,/Assets/icons/{agentId.Trim()}.png", UriKind.Absolute);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = uri;
            bi.DecodePixelWidth = 128;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.EndInit();
            if (bi.CanFreeze) bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 1:1 渲染官方正圆 PNG：EllipseGeometry 硬裁切，不做放大裁边，标志完整居中。
    /// </summary>
    public static FrameworkElement CreateCircularAvatarVisual(
        string agentId,
        double size,
        AgentRosterEntry? agent = null,
        string? borderHex = null,
        double borderThickness = 1.5)
    {
        try
        {
            string id = string.IsNullOrWhiteSpace(agentId) ? (agent?.Id ?? "unknown") : agentId;
            // windsurf / vps_remote / 任意自定义 ID：无官方 PNG 时走彩色字母徽标，绝不空白
            var src = GetOfficialIcon(id, agent) ?? TryBuildMonogram(id, agent?.Name) ?? BuildServerCluster();
            double r = size / 2.0;

            var host = new Grid
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Clip = new EllipseGeometry(new Point(r, r), r, r),
                Tag = $"avatar:{id}"
            };

            var icon = new Image
            {
                Source = src,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            host.Children.Add(icon);

            if (!string.IsNullOrWhiteSpace(borderHex) && borderThickness > 0)
            {
                host.Children.Add(new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = Brushes.Transparent,
                    Stroke = BrushFrom(borderHex),
                    StrokeThickness = borderThickness,
                    IsHitTestVisible = false
                });
            }

            return host;
        }
        catch
        {
            string fid = string.IsNullOrWhiteSpace(agentId) ? "?" : agentId.Trim();
            return new Grid
            {
                Width = size,
                Height = size,
                Tag = $"avatar:{fid}",
                Children =
                {
                    new Ellipse { Width = size, Height = size, Fill = BrushFrom("#6366F1") },
                    new TextBlock
                    {
                        Text = BuildMonogramText(fid, agent?.Name),
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.Bold,
                        FontSize = Math.Max(8, size * 0.42),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
        }
    }

    private static readonly string[] MonogramPalette =
    {
        "#0EA5E9", "#8B5CF6", "#10B981", "#F59E0B", "#EF4444",
        "#EC4899", "#14B8A6", "#6366F1", "#F97316", "#84CC16"
    };

    private static bool IsRemoteNodeId(string id) =>
        id.Equals("vps_remote", StringComparison.OrdinalIgnoreCase);

    public static string BuildMonogramText(string id, string? name)
    {
        string src = !string.IsNullOrWhiteSpace(name) ? name!.Trim() : id;
        foreach (var ch in src)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF) return ch.ToString();
        }
        var words = System.Text.RegularExpressions.Regex.Split(src, @"[^A-Za-z0-9]+")
            .Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return "?";
        if (words.Count == 1)
        {
            var w = words[0];
            return w.Length == 1 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w.Substring(1, 1).ToLowerInvariant();
        }
        return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
    }

    private static DrawingImage? TryBuildMonogram(string id, string? name)
    {
        try
        {
            string bg;
            if (BrandMeta.TryGetValue(id, out var meta))
            {
                bg = meta.Color;
            }
            else
            {
                int h = 17;
                foreach (var ch in id.ToLowerInvariant()) h = unchecked(h * 31 + ch);
                bg = MonogramPalette[(int)((uint)h % (uint)MonogramPalette.Length)];
            }
            string text = BrandMeta.TryGetValue(id, out var m2) && m2.Label.Length <= 3
                ? m2.Label
                : BuildMonogramText(id, name);

            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(BrushFrom(bg), null, new EllipseGeometry(new Point(64, 64), 64, 64)));
            g.Children.Add(new GeometryDrawing(null, new Pen(BrushFrom("#59FFFFFF"), 4),
                new EllipseGeometry(new Point(64, 64), 58, 58)));
            var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI, Microsoft YaHei"),
                    FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                text.Length > 1 ? 56 : 66, Brushes.White, 1.0);
            var geo = ft.BuildGeometry(new Point(0, 0));
            var b = geo.Bounds;
            if (!b.IsEmpty)
                geo.Transform = new TranslateTransform(64 - (b.X + b.Width / 2), 64 - (b.Y + b.Height / 2));
            g.Children.Add(new GeometryDrawing(Brushes.White, null, geo));
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    private static DrawingImage BuildServerCluster()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(BrushFrom("#111827"), null, new EllipseGeometry(new Point(64, 64), 64, 64)));
        var node = BrushFrom("#60A5FA");
        var link = new Pen(BrushFrom("#93C5FD"), 3);
        g.Children.Add(new GeometryDrawing(null, link, Geometry.Parse("M 40,48 L 64,36 L 88,48 L 88,76 L 64,90 L 40,76 Z")));
        foreach (var pt in new[]
                 {
                     new Point(40, 48), new Point(64, 36), new Point(88, 48),
                     new Point(88, 76), new Point(64, 90), new Point(40, 76), new Point(64, 62)
                 })
        {
            g.Children.Add(new GeometryDrawing(node, null, new EllipseGeometry(pt, 6, 6)));
        }

        var img = new DrawingImage(g);
        img.Freeze();
        return img;
    }

    private static SolidColorBrush BrushFrom(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}
