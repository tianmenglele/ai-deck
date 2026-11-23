using System.Windows;
using System.Windows.Media;

namespace AICollabHub.Services;

/// <summary>
/// 全局多套主题配色中心（主程序窗口 + 二级弹窗 + 桌面悬浮窗一键同步换肤）。
/// </summary>
public static class ThemeService
{
    public sealed record ThemePalette(
        string Id,
        string DisplayName,
        string ShortTag,
        string PreviewDotHex,
        string CanvasBgHex,
        string HeaderBgHex,
        string SidebarBgHex,
        string SectionBgHex,
        string InputBgHex,
        string ComboBgHex,
        string BorderSectionHex,
        string DividerRowHex,
        string AccentPrimaryHex,
        string PrimaryBtnBgHex,
        string PrimaryBtnBorderHex,
        string FloatOuterBgHex,
        string FloatOuterBorderHex,
        string FloatHeaderBgHex,
        string FloatHeaderBorderHex,
        string FloatPolicyStripBgHex,
        string FloatCardIdleBgHex,
        string FloatCardActiveBgHex);

    public static IReadOnlyList<ThemePalette> Catalog { get; } = new[]
    {
        new ThemePalette(
            "dark_slate", "🌌 深空暗夜（默认经典蓝黑）", "深空暗夜", "#38BDF8",
            "#0F141D", "#141A24", "#131923", "#161E2B", "#0D1219", "#17202E", "#232D3F", "#1E2736",
            "#38BDF8", "#173252", "#2D6296",
            "#FF0F1521", "#2A3F5F", "#151F30", "#24354F", "#111A2B", "#141C2B", "#1A2333"),

        new ThemePalette(
            "cyber_neon", "⚡ 赛博霓虹（电光紫蓝）", "赛博霓虹", "#818CF8",
            "#0B0E1E", "#121631", "#0F1329", "#151A36", "#090C1A", "#1A2042", "#2D366B", "#222954",
            "#818CF8", "#26235C", "#5B54C7",
            "#FF0D1124", "#6366F1", "#161B38", "#4338CA", "#121731", "#151B38", "#1E254D"),

        new ThemePalette(
            "emerald_glass", "💎 翡翠微光（护眼森林绿）", "翡翠微光", "#10B981",
            "#0A1614", "#0F211E", "#0D1C19", "#122622", "#081210", "#16302B", "#214A41", "#1A3B34",
            "#34D399", "#123B31", "#1F7A63",
            "#FF0C1E1B", "#10B981", "#112924", "#059669", "#0E231F", "#122B26", "#173831"),

        new ThemePalette(
            "obsidian_gold", "👑 黑曜流金（暖金琥珀黑）", "黑曜流金", "#F59E0B",
            "#13110E", "#1C1813", "#181510", "#211C16", "#0E0C0A", "#29221A", "#423526", "#33291E",
            "#FBBF24", "#3D2B12", "#9A6B21",
            "#FF16130F", "#D97706", "#221C14", "#92400E", "#1B1610", "#211B14", "#2B2319"),

        new ThemePalette(
            "arctic_ocean", "🧊 极地冰川（深海冷青蓝）", "极地冰川", "#22D3EE",
            "#09151E", "#0E202E", "#0C1B27", "#122636", "#071018", "#163044", "#234B69", "#1B3B54",
            "#22D3EE", "#123A52", "#1D6FA3",
            "#FF0B1924", "#06B6D4", "#102536", "#0E7490", "#0E202E", "#12283A", "#18354C"),

        new ThemePalette(
            "crimson_carbon", "🔥 赤焰碳纤（暗红机甲风）", "赤焰碳纤", "#F43F5E",
            "#150D11", "#211319", "#1C1015", "#26161D", "#10090C", "#301C25", "#4D2A39", "#3B202C",
            "#FB7185", "#471928", "#9F2B48",
            "#FF180E13", "#E11D48", "#26151D", "#9F1239", "#1E1118", "#24151D", "#301C26")
    };

    public static ThemePalette Resolve(string? themeId) =>
        Catalog.FirstOrDefault(t => t.Id.Equals(themeId?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Catalog[0];

    public static void ApplyGlobalTheme(string? themeId)
    {
        var p = Resolve(themeId);
        var app = Application.Current;
        if (app?.Resources == null) return;

        SetResourceBrush(app.Resources, "BgCanvasBrush", p.CanvasBgHex);
        SetResourceBrush(app.Resources, "BgHeaderBrush", p.HeaderBgHex);
        SetResourceBrush(app.Resources, "BgSidebarBrush", p.SidebarBgHex);
        SetResourceBrush(app.Resources, "BgSectionBrush", p.SectionBgHex);
        SetResourceBrush(app.Resources, "BgInputBrush", p.InputBgHex);
        SetResourceBrush(app.Resources, "BgComboBrush", p.ComboBgHex);
        SetResourceBrush(app.Resources, "BorderSectionBrush", p.BorderSectionHex);
        SetResourceBrush(app.Resources, "DividerRowBrush", p.DividerRowHex);
        SetResourceBrush(app.Resources, "AccentBlueBrush", p.AccentPrimaryHex);
    }

    private static void SetResourceBrush(ResourceDictionary dict, string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        dict[key] = brush;
        foreach (var md in dict.MergedDictionaries)
        {
            if (md.Contains(key))
            {
                md[key] = brush;
            }
        }
    }
}
