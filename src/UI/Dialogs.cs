using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AICollabHub.Models;
using AICollabHub.Services;

namespace AICollabHub.UI;

/// <summary>
/// 全局零 GC 冻结画刷池：避免计时器高频重绘与长列表卡片创建大量未冻结 SolidColorBrush。
/// </summary>
public static class UiBrushPool
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SolidColorBrush Get(string hex)
    {
        return Cache.GetOrAdd(hex, static h =>
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(h)!;
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        });
    }
}

public static partial class Dialogs
{
    private static Brush BrushHex(string hex) => UiBrushPool.Get(hex);

    // 1. 项目档案新建 / 编辑窗口（支持主工程 + 任意多个关联子目录 + 预设类型选择）
    public static ProjectProfile? ShowProjectEditor(Window? owner, RegistryService reg, ProjectProfile? existing)
    {
        bool isNew = existing == null;
        var draft = existing == null
            ? new ProjectProfile { Name = "新工程档案", PresetId = "general_code" }
            : new ProjectProfile
            {
                Id = existing.Id,
                Name = existing.Name,
                PrimaryRoot = existing.PrimaryRoot,
                CompanionRoots = new List<string>(existing.CompanionRoots),
                PresetId = existing.PresetId,
                RulesRelDir = existing.RulesRelDir,
                SkillsRelDir = existing.SkillsRelDir,
                MemoryRelDir = existing.MemoryRelDir,
                CollabRelDir = existing.CollabRelDir,
                EnabledHooks = new List<string>(existing.EnabledHooks),
                StageOverrides = new Dictionary<string, string>(existing.StageOverrides),
                FallbackBuilderId = existing.FallbackBuilderId
            };

        var win = CreateBaseDialog(owner, isNew ? "新建项目档案（支持任意类型工程）" : $"编辑项目档案 · {draft.Name}", 620, 470);
        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };

        var nameBox = new TextBox { Text = draft.Name };
        root.Children.Add(MakeFieldRow("项目显示名称", nameBox));

        var presetCombo = new ComboBox();
        foreach (var p in reg.Presets.Presets)
        {
            var cbi = new ComboBoxItem { Content = p.Name, Tag = p.Id };
            presetCombo.Items.Add(cbi);
            if (p.Id == draft.PresetId) presetCombo.SelectedItem = cbi;
        }
        if (presetCombo.SelectedIndex < 0 && presetCombo.Items.Count > 0) presetCombo.SelectedIndex = 0;

        var primaryPanel = new DockPanel { LastChildFill = true };
        var browseBtn = new System.Windows.Controls.Button
        {
            Content = "浏览...",
            Style = (Style)System.Windows.Application.Current.FindResource("ActionBtn"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(browseBtn, Dock.Right);
        var primaryBox = new TextBox { Text = draft.PrimaryRoot };
        browseBtn.Click += (_, _) =>
        {
            var picked = PickFolder(primaryBox.Text);
            if (!string.IsNullOrEmpty(picked))
            {
                primaryBox.Text = picked;
                if (draft.Name is "新工程档案" or "") nameBox.Text = Path.GetFileName(picked);
                if (File.Exists(Path.Combine(picked, "project.godot")))
                {
                    foreach (ComboBoxItem item in presetCombo.Items)
                        if ((string)item.Tag == "godot_project") presetCombo.SelectedItem = item;
                }
            }
        };
        primaryPanel.Children.Add(browseBtn);
        primaryPanel.Children.Add(primaryBox);
        root.Children.Add(MakeFieldRow("主工程根目录", primaryPanel));
        root.Children.Add(MakeFieldRow("工程类型预设", presetCombo));

        var compBox = new TextBox
        {
            Text = string.Join(Environment.NewLine, draft.CompanionRoots),
            Height = 58,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        var compHeader = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        var addCompBtn = new System.Windows.Controls.Button
        {
            Content = "+ 添加关联目录",
            Style = (Style)System.Windows.Application.Current.FindResource("ActionBtn"),
            Padding = new Thickness(8, 2, 8, 2)
        };
        DockPanel.SetDock(addCompBtn, Dock.Right);
        addCompBtn.Click += (_, _) =>
        {
            var picked = PickFolder("");
            if (!string.IsNullOrEmpty(picked))
            {
                compBox.Text = string.IsNullOrWhiteSpace(compBox.Text)
                    ? picked
                    : compBox.Text.TrimEnd() + Environment.NewLine + picked;
            }
        };
        compHeader.Children.Add(addCompBtn);
        compHeader.Children.Add(new TextBlock
        {
            Text = "关联子工程目录（可选，每行一个，如独立配表工具库）",
            Foreground = BrushHex("#9AA8BE"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(compHeader);
        root.Children.Add(compBox);

        // 权威源相对路径行
        var pathsGrid = new Grid { Margin = new Thickness(0, 10, 0, 6) };
        pathsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        pathsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rulesBox = new TextBox { Text = draft.RulesRelDir };
        var skillsBox = new TextBox { Text = draft.SkillsRelDir };
        var leftStack = new StackPanel();
        leftStack.Children.Add(new TextBlock { Text = "权威规则相对路径", Foreground = BrushHex("#9AA8BE"), FontSize = 12, Margin = new Thickness(0, 0, 0, 3) });
        leftStack.Children.Add(rulesBox);
        var rightStack = new StackPanel();
        rightStack.Children.Add(new TextBlock { Text = "权威技能相对路径", Foreground = BrushHex("#9AA8BE"), FontSize = 12, Margin = new Thickness(0, 0, 0, 3) });
        rightStack.Children.Add(skillsBox);
        Grid.SetColumn(leftStack, 0);
        Grid.SetColumn(rightStack, 2);
        pathsGrid.Children.Add(leftStack);
        pathsGrid.Children.Add(rightStack);
        root.Children.Add(pathsGrid);

        ProjectProfile? confirmed = null;
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "取消", Style = (Style)System.Windows.Application.Current.FindResource("ActionBtn"), Margin = new Thickness(0, 0, 8, 0) };
        var saveBtn = new System.Windows.Controls.Button { Content = isNew ? "创建并切换至此项目" : "保存项目设置", Style = (Style)System.Windows.Application.Current.FindResource("PrimaryBtn") };
        cancelBtn.Click += (_, _) => win.Close();
        saveBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(primaryBox.Text))
            {
                System.Windows.MessageBox.Show(win, "请填写有效的主工程目录路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            draft.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? Path.GetFileName(primaryBox.Text.Trim()) : nameBox.Text.Trim();
            draft.PrimaryRoot = primaryBox.Text.Trim();
            draft.PresetId = (presetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "general_code";
            draft.CompanionRoots = compBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            draft.RulesRelDir = string.IsNullOrWhiteSpace(rulesBox.Text) ? ".cursor/rules" : rulesBox.Text.Trim();
            draft.SkillsRelDir = string.IsNullOrWhiteSpace(skillsBox.Text) ? ".cursor/skills" : skillsBox.Text.Trim();
            confirmed = draft;
            win.Close();
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(saveBtn);
        root.Children.Add(btnRow);

        win.Content = root;
        win.ShowDialog();
        return confirmed;
    }

    // 2. 手动添加外部 VPS 桥接 / 网页或本地自定义 AI 窗口
    public static AgentRosterEntry? ShowAddCustomAgentDialog(Window? owner, RegistryService reg)
    {
        var win = CreateBaseDialog(owner, "添加外部 VPS 桥接 / 远程或本地自定义 AI", 560, 390);
        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };

        var nameBox = new TextBox { Text = "自定义协同 AI（如 VPS Claude / 网页 DeepSeek）" };
        root.Children.Add(MakeFieldRow("AI 工具显示名称", nameBox));

        var modeCombo = new ComboBox();
        modeCombo.Items.Add(new ComboBoxItem { Content = "◆ 外部 VPS / 共享文件夹桥接模式（连接由 TAKT 等外部工具负责）", Tag = "bridge_folder" });
        modeCombo.Items.Add(new ComboBoxItem { Content = "● 无目录远程网页 / 聊天窗口模式（通过剪贴板胶囊收发）", Tag = "capsule_only" });
        modeCombo.Items.Add(new ComboBoxItem { Content = "▸ 本地命令行 / 桌面代理模式（读取项目根目录 AGENTS.md）", Tag = "markdown_anchor" });
        modeCombo.SelectedIndex = 0;
        root.Children.Add(MakeFieldRow("工作流接入方式", modeCombo));

        var dirPanel = new DockPanel { LastChildFill = true };
        var pickBtn = new System.Windows.Controls.Button
        {
            Content = "选择文件夹",
            Style = (Style)System.Windows.Application.Current.FindResource("ActionBtn"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(pickBtn, Dock.Right);
        var bridgeBox = new TextBox { Text = "" };
        pickBtn.Click += (_, _) =>
        {
            var p = PickFolder(bridgeBox.Text);
            if (!string.IsNullOrEmpty(p)) bridgeBox.Text = p;
        };
        dirPanel.Children.Add(pickBtn);
        dirPanel.Children.Add(bridgeBox);
        root.Children.Add(MakeFieldRow("本地同步桥接文件夹（仅桥接模式填写）", dirPanel));

        var primaryTagCombo = new ComboBox();
        foreach (var t in reg.Tags.Tags)
        {
            primaryTagCombo.Items.Add(new ComboBoxItem { Content = $"{t.Name} — {t.Description}", Tag = t.Id });
        }
        if (primaryTagCombo.Items.Count > 0) primaryTagCombo.SelectedIndex = 0;
        root.Children.Add(MakeFieldRow("主打特长方向（后续可在第 ② 页细分调整）", primaryTagCombo));

        AgentRosterEntry? created = null;
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "取消", Style = (Style)System.Windows.Application.Current.FindResource("ActionBtn"), Margin = new Thickness(0, 0, 8, 0) };
        var okBtn = new System.Windows.Controls.Button { Content = "添加并启用", Style = (Style)System.Windows.Application.Current.FindResource("PrimaryBtn") };
        cancelBtn.Click += (_, _) => win.Close();
        okBtn.Click += (_, _) =>
        {
            var adapterId = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "capsule_only";
            var mainTag = (primaryTagCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "adversarial_review";
            var bDir = bridgeBox.Text.Trim();
            created = new AgentRosterEntry
            {
                Id = "custom_" + Guid.NewGuid().ToString("N")[..6],
                Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "自定义 AI" : nameBox.Text.Trim(),
                Category = adapterId == "bridge_folder" ? "vps_bridge" : (adapterId == "capsule_only" ? "remote_capsule" : "local_cli"),
                Enabled = true,
                Detected = true,
                AdapterId = adapterId,
                BridgeLocalDir = bDir,
                Evidence = adapterId == "bridge_folder"
                    ? (string.IsNullOrEmpty(bDir) ? "待指定桥接目录" : $"桥接目录: {bDir}")
                    : (adapterId == "capsule_only" ? "剪贴板胶囊收发模式" : "本地 AGENTS.md 协议接入"),
                Permissions = new AgentPermissions
                {
                    AllowDirectSourceWrite = false,
                    AllowMultiDir = false,
                    AllowShellBuild = false,
                    HasPreviewMcp = false
                },
                TagScores = new Dictionary<string, int> { [mainTag] = 5 }
            };
            win.Close();
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        root.Children.Add(btnRow);

        win.Content = root;
        win.ShowDialog();
        return created;
    }

    // 3. 二级模态窗口：单个 AI 工具的详细权限、执行参数与擅长能力星级设置
    public static bool ShowAgentConfigDialog(
        Window? owner,
        RegistryService reg,
        AgentRosterEntry agent,
        CloudRecommendationService cloudSvc)
    {
        var (win, getSaved) = BuildAgentConfigDialogWindow(owner, reg, agent, cloudSvc);
        win.ShowDialog();
        return getSaved();
    }

    private static string ExtractPureSpecialtyLabel(string cloudBadge)
    {
        var s = (cloudBadge ?? "").Replace("云端定位: ", "", StringComparison.OrdinalIgnoreCase).Trim();
        foreach (var prefix in new[] { "高档 · ", "中档 · ", "低档 · ", "高档·", "中档·", "低档·" })
        {
            if (s.StartsWith(prefix, StringComparison.Ordinal))
            {
                return s[prefix.Length..].Trim();
            }
        }
        return s;
    }

    private static string FormatTokenCountBadge(int tokens)
    {
        if (tokens <= 0) return "";
        if (tokens >= 1000000) return $"{tokens / 1000000.0:0.#}M";
        if (tokens >= 1000) return $"{tokens / 1000}K";
        return tokens.ToString();
    }

    public static (Window Window, Func<bool> GetSaved) BuildAgentConfigDialogWindow(
        Window? owner,
        RegistryService reg,
        AgentRosterEntry agent,
        CloudRecommendationService cloudSvc)
    {
        var localScanInfo = LocalModelCatalogService.SyncAgentWithLocalClientModels(agent, forceOverwriteTiers: false);
        CloudRecommendationService.EnsureAgentModelTiersPopulated(agent);
        CloudRecommendationService.ReconcileLegacySplitTiers(agent);

        agent.RuntimeParams ??= new AgentRuntimeParams();
        agent.CustomPresets ??= new List<AgentConfigPreset>();

        var win = CreateBaseDialog(owner, $"配置 AI 工具 · {agent.Name}", 750, 660);
        var outerDock = new DockPanel { Margin = new Thickness(18, 12, 18, 12) };

        // 1. 顶部 3 分段页签切换栏
        var tabBar = new Border
        {
            Background = BrushHex("#0F1520"),
            BorderBrush = BrushHex("#222D40"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(3),
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(tabBar, Dock.Top);
        var tabGrid = new Grid();
        tabGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var page1 = new Grid(); // 模型与工作分工
        page1.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 0: listHeaderGrid
        page1.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 1: cardsScroll
        page1.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 2: page1BottomFixed

        var page2 = new StackPanel();
        var page3 = new StackPanel();
        var page2Scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page2, Visibility = Visibility.Collapsed };
        var page3Scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page3, Visibility = Visibility.Collapsed };

        Button MakeSegBtn(string text, int col)
        {
            var b = new Button
            {
                Content = text,
                Style = Application.Current?.FindResource("ActionBtn") as Style,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 5, 8, 5),
                FontSize = 12.5
            };
            Grid.SetColumn(b, col);
            tabGrid.Children.Add(b);
            return b;
        }

        var btnTab1 = MakeSegBtn("模型与工作分工", 0);
        var btnTab2 = MakeSegBtn("权限与能力评分", 1);
        var btnTab3 = MakeSegBtn("运行参数与执行通道", 2);

        void SwitchModalTab(int idx)
        {
            page1.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
            page2Scroll.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            page3Scroll.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;

            btnTab1.Background = idx == 0 ? BrushHex("#1F324D") : Brushes.Transparent;
            btnTab1.Foreground = idx == 0 ? BrushHex("#F8FAFC") : BrushHex("#94A3B8");
            btnTab2.Background = idx == 1 ? BrushHex("#1F324D") : Brushes.Transparent;
            btnTab2.Foreground = idx == 1 ? BrushHex("#F8FAFC") : BrushHex("#94A3B8");
            btnTab3.Background = idx == 2 ? BrushHex("#1F324D") : Brushes.Transparent;
            btnTab3.Foreground = idx == 2 ? BrushHex("#F8FAFC") : BrushHex("#94A3B8");
        }
        btnTab1.Click += (_, _) => SwitchModalTab(0);
        btnTab2.Click += (_, _) => SwitchModalTab(1);
        btnTab3.Click += (_, _) => SwitchModalTab(2);
        tabBar.Child = tabGrid;
        outerDock.Children.Add(tabBar);

        // 2. 跨页签常驻：多套云端配置 + 用户多套自定义方案切换中枢栏
        bool canSelectModels = LocalModelCatalogService.SupportsModelSelection(agent.Id);
        bool allowCustomModelInput = LocalModelCatalogService.SupportsCustomModelInput(agent.Id);
        string currentSelectionMode = string.Equals(agent.ModelSelectionMode, "manual", StringComparison.OrdinalIgnoreCase)
            ? "manual"
            : "auto";
        string activePresetId = string.IsNullOrWhiteSpace(agent.ActivePresetId) ? "cloud_balanced" : agent.ActivePresetId;
        string latestLocalSourceDesc = localScanInfo.SourceDescription;
        var cloudPresets = CloudRecommendationService.BuildCloudPresetsForAgent(reg, agent, refreshLocalModels: false);
        bool suppressUiSyncEvents = false;

        var modePanel = new Border
        {
            Background = BrushHex("#101826"),
            BorderBrush = canSelectModels ? BrushHex("#213047") : BrushHex("#78521A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(modePanel, Dock.Top);

        var modePanelStack = new StackPanel();
        var presetRowGrid = new Grid();
        presetRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        presetRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        presetRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        presetRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        presetRowGrid.Children.Add(new TextBlock
        {
            Text = "配置方案：",
            Foreground = BrushHex("#CBD5E1"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });

        var presetCombo = new ComboBox
        {
            Height = 27,
            FontSize = 11.5,
            ToolTip = "选择从网上获取的多套云端配置方案，或切换你自己保存的自定义配置方案"
        };
        Grid.SetColumn(presetCombo, 1);

        var presetActionWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var savePresetBtn = new Button
        {
            Content = "💾 存为方案",
            Style = Application.Current?.FindResource("ActionBtn") as Style,
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 5, 0),
            ToolTip = "将当前 3 个页签（模型、职责、温度/推理/超时、权限与能力星级）保存为你自己的多套方案之一"
        };
        var delPresetBtn = new Button
        {
            Content = "🗑 删除",
            Style = Application.Current?.FindResource("ActionBtn") as Style,
            Padding = new Thickness(7, 3, 7, 3),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 5, 0),
            IsEnabled = false,
            ToolTip = "删除当前选中的用户自定义方案"
        };
        var syncLocalCloudBtn = new Button
        {
            Content = "↻ 联网刷新",
            Style = Application.Current?.FindResource("ActionBtn") as Style,
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 11.5,
            ToolTip = "重新扫描本机客户端模型并从网上拉取 OpenRouter / LiteLLM 最新参数，生成多套云端方案"
        };
        presetActionWrap.Children.Add(savePresetBtn);
        presetActionWrap.Children.Add(delPresetBtn);
        presetActionWrap.Children.Add(syncLocalCloudBtn);
        Grid.SetColumn(presetActionWrap, 3);

        presetRowGrid.Children.Add(presetCombo);
        presetRowGrid.Children.Add(presetActionWrap);
        modePanelStack.Children.Add(presetRowGrid);

        var sourceStatusText = new TextBlock
        {
            Foreground = BrushHex("#34D399"),
            FontSize = 11,
            Margin = new Thickness(0, 5, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        modePanelStack.Children.Add(sourceStatusText);
        modePanel.Child = modePanelStack;
        outerDock.Children.Add(modePanel);

        // 先声明 3 个页签的核心可联动控件，确保方案切换逻辑无向前引用冲突
        var editableProfiles = agent.ModelProfiles.Select(p => p.Clone()).ToList();

        var tierStrategyCombo = new ComboBox
        {
            Height = 26,
            FontSize = 11.5,
            Visibility = canSelectModels ? Visibility.Visible : Visibility.Collapsed
        };
        var tierStrategies = new (string Id, string Label)[]
        {
            ("auto", "调度策略：按任务复杂度自动选档（推荐）"),
            ("high", "调度策略：优先高档（深度分析）"),
            ("medium", "调度策略：优先中档（主力实操）"),
            ("low", "调度策略：优先低档（极速执行）")
        };
        foreach (var ts in tierStrategies)
        {
            tierStrategyCombo.Items.Add(new ComboBoxItem { Content = ts.Label, Tag = ts.Id });
        }
        var cardsHost = new StackPanel { Margin = new Thickness(0, 0, 2, 0) };
        var cardsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = cardsHost,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var addRowHost = new ContentControl { Visibility = canSelectModels ? Visibility.Visible : Visibility.Collapsed };
        var workNoteBox = new TextBox { Text = agent.CustomWorkNote, Height = 27, FontSize = 11.5 };

        // 页签 2 控件
        var cbWrite = new CheckBox { Content = "允许直接修改工程代码与预制体", IsChecked = agent.Permissions.AllowDirectSourceWrite, Margin = new Thickness(0, 4, 8, 4) };
        var cbMulti = new CheckBox { Content = "允许跨关联目录读写", IsChecked = agent.Permissions.AllowMultiDir, Margin = new Thickness(0, 4, 0, 4) };
        var cbShell = new CheckBox { Content = "允许运行构建与测试脚本", IsChecked = agent.Permissions.AllowShellBuild, Margin = new Thickness(0, 4, 8, 4) };
        var cbMcp = new CheckBox { Content = "具备实机画面截图能力 (MCP)", IsChecked = agent.Permissions.HasPreviewMcp, Margin = new Thickness(0, 4, 0, 4) };
        var starCombos = new Dictionary<string, ComboBox>();

        // 页签 3 控件（新增全套运行参数 + 原有窗口与执行接口）
        var tempPresetCombo = new ComboBox { Height = 27, FontSize = 11.5 };
        foreach (var (tv, tl) in new (double Val, string Label)[]
        {
            (0.10, "0.10 · 极严谨"),
            (0.15, "0.15 · 深度推理"),
            (0.25, "0.25 · 严谨编码"),
            (0.30, "0.30 · 均衡推荐"),
            (0.50, "0.50 · 适中弹性"),
            (0.75, "0.75 · 创意多模态"),
            (1.00, "1.00 · 高发散")
        })
        {
            tempPresetCombo.Items.Add(new ComboBoxItem { Content = tl, Tag = tv });
        }
        var tempValueBox = new TextBox
        {
            Text = agent.RuntimeParams.Temperature.ToString("0.00", CultureInfo.InvariantCulture),
            Height = 27,
            Width = 58,
            FontSize = 11.5,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            ToolTip = "采样温度 (0.00 ~ 2.00)：越低越严谨稳定，越高越有创意"
        };

        var reasoningCombo = new ComboBox { Height = 27, FontSize = 11.5 };
        foreach (var (rk, rl) in new[]
        {
            ("auto", "auto · 智能跟随档位"),
            ("low", "low · 低档（极速轻量）"),
            ("medium", "medium · 中档（均衡日常）"),
            ("high", "high · 高档（深度长推理）"),
            ("xhigh", "xhigh · 极高（极限攻坚）")
        })
        {
            reasoningCombo.Items.Add(new ComboBoxItem { Content = rl, Tag = rk });
        }

        var ctxBudgetCombo = new ComboBox { Height = 27, FontSize = 11.5 };
        foreach (var (ck, cl) in new[]
        {
            ("auto", "auto · 智能自适应"),
            ("compact", "compact · 精简省流 (32K)"),
            ("standard", "standard · 标准工程 (128K)"),
            ("max", "max · 全量长窗口 (200K~1M)")
        })
        {
            ctxBudgetCombo.Items.Add(new ComboBoxItem { Content = cl, Tag = ck });
        }

        var maxOutPresetCombo = new ComboBox { Height = 27, FontSize = 11.5 };
        foreach (var (mv, ml) in new (int Val, string Label)[]
        {
            (0, "0 · 自动不限"),
            (4096, "4096 · 短输出"),
            (8192, "8192 · 标准"),
            (16384, "16384 · 长代码"),
            (32768, "32768 · 架构输出"),
            (65536, "65536 · 超长生成")
        })
        {
            maxOutPresetCombo.Items.Add(new ComboBoxItem { Content = ml, Tag = mv });
        }
        var maxOutValueBox = new TextBox
        {
            Text = agent.RuntimeParams.MaxOutputTokens.ToString(CultureInfo.InvariantCulture),
            Height = 27,
            Width = 68,
            FontSize = 11.5,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            ToolTip = "最大输出 Token 数（填 0 表示自动不限）"
        };

        var timeoutBox = new TextBox
        {
            Text = agent.RuntimeParams.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            Height = 27,
            FontSize = 11.5,
            ToolTip = "单次调用超时秒数（填 0 表示跟随全局软件设置，建议 30 ~ 1800）"
        };

        var retriesCombo = new ComboBox { Height = 27, FontSize = 11.5 };
        foreach (var (rv, rl) in new (int Val, string Label)[]
        {
            (-1, "跟随全局网络重试设置"),
            (0, "0 次（失败不重试）"),
            (1, "1 次"),
            (2, "2 次"),
            (3, "3 次"),
            (4, "4 次"),
            (5, "5 次")
        })
        {
            retriesCombo.Items.Add(new ComboBoxItem { Content = rl, Tag = rv });
        }

        var extraArgsBox = new TextBox
        {
            Text = agent.RuntimeParams.ExtraRunnerArgs ?? "",
            Height = 27,
            FontSize = 11.5,
            ToolTip = canSelectModels
                ? "附加 CLI 参数或自定义透传标识（空格分隔；可留空）"
                : "附加运行参数（防风控已锁定，不会向客户端传入模型切换参数 -m）"
        };

        var cbLaunchWin = new CheckBox
        {
            Content = "派发任务时若客户端未运行，允许在后台最小化静默启动（不抢焦点/键鼠）",
            IsChecked = agent.LaunchWindowOnDispatch,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var modeCombo = new ComboBox { Height = 27, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 8) };
        var modes = new (string Id, string Label)[]
        {
            ("codex_exec", "智能后台多通道执行（CLI 优先 ➔ 零焦点后台投递 ➔ 无头引擎）"),
            ("grok_cli", "Grok 原生无头 CLI 后台执行（grok.exe · 零窗口零键鼠占用）"),
            ("cli_custom", "自定义命令行脚本执行（纯后台进程）"),
            ("http_openai", "OpenAI 兼容 HTTP 接口调用（纯后台网络请求）"),
            ("bridge_queue", "本地桥接目录投递与回收（零窗口交互）"),
            ("none", "仅作为主控或纯人工交互")
        };
        foreach (var m in modes)
        {
            modeCombo.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Id });
        }
        var cmdBox = new TextBox { Text = agent.RunnerCmd, Height = 27, FontSize = 11.5 };

        static void SelectComboByTag(ComboBox combo, string? tag)
        {
            foreach (var obj in combo.Items)
            {
                if (obj is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tag ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = cbi;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        static void SelectComboByIntTag(ComboBox combo, int val, int fallbackIdx = 0)
        {
            foreach (var obj in combo.Items)
            {
                if (obj is ComboBoxItem cbi && cbi.Tag is int iv && iv == val)
                {
                    combo.SelectedItem = cbi;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = Math.Clamp(fallbackIdx, 0, combo.Items.Count - 1);
        }

        static void SelectTempPresetCombo(ComboBox combo, double tempVal)
        {
            ComboBoxItem? match = null;
            foreach (var obj in combo.Items)
            {
                if (obj is ComboBoxItem cbi && cbi.Tag is double dv && Math.Abs(dv - tempVal) < 0.015)
                {
                    match = cbi;
                    break;
                }
            }
            combo.SelectedItem = match;
        }

        AgentRuntimeParams ReadRuntimeParamsFromControls()
        {
            double temp = 0.30;
            if (double.TryParse(tempValueBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedT))
                temp = Math.Clamp(Math.Round(parsedT, 2), 0.0, 2.0);
            int maxOut = 0;
            if (int.TryParse(maxOutValueBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOut))
                maxOut = Math.Max(0, parsedOut);
            int timeout = 0;
            if (int.TryParse(timeoutBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTo))
                timeout = Math.Clamp(parsedTo, 0, 3600);
            int retries = (retriesCombo.SelectedItem as ComboBoxItem)?.Tag is int rv ? rv : -1;
            var reason = (reasoningCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
            var ctxB = (ctxBudgetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";

            return new AgentRuntimeParams
            {
                Temperature = temp,
                ReasoningEffort = reason,
                ContextBudget = ctxB,
                MaxOutputTokens = maxOut,
                TimeoutSeconds = timeout,
                MaxRetries = retries,
                ExtraRunnerArgs = extraArgsBox.Text.Trim()
            };
        }

        AgentConfigPreset BuildDraftPresetSnapshot(string presetId, string name, string sourceKind, string desc)
        {
            var rt = ReadRuntimeParamsFromControls();
            var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in starCombos)
            {
                if (kv.Value.SelectedItem is ComboBoxItem ci && ci.Tag is int starVal)
                    scores[kv.Key] = starVal;
            }
            return new AgentConfigPreset
            {
                PresetId = presetId,
                Name = name,
                SourceKind = sourceKind,
                CategoryBadge = sourceKind == "user" ? "用户自定义" : "手动微调",
                Description = desc,
                SourceBasis = "当前弹窗全参数快照",
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                PreferredEffortTier = (tierStrategyCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto",
                CustomWorkNote = workNoteBox.Text.Trim(),
                ModelProfiles = editableProfiles.Where(p => !string.IsNullOrWhiteSpace(p.ModelName)).Select(p => p.Clone()).ToList(),
                Permissions = new AgentPermissions
                {
                    AllowDirectSourceWrite = cbWrite.IsChecked == true,
                    AllowMultiDir = cbMulti.IsChecked == true,
                    AllowShellBuild = cbShell.IsChecked == true,
                    HasPreviewMcp = cbMcp.IsChecked == true
                },
                TagScores = scores,
                RuntimeParams = rt,
                LaunchWindowOnDispatch = cbLaunchWin.IsChecked == true,
                RunnerMode = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? agent.RunnerMode,
                RunnerCmd = cmdBox.Text.Trim()
            };
        }

        static bool IsTemperatureSupportedMode(string? modeTag)
            => string.Equals(modeTag, "http_openai", StringComparison.OrdinalIgnoreCase)
               || string.Equals(modeTag, "cli_custom", StringComparison.OrdinalIgnoreCase);

        void UpdateModeBannerVisual()
        {
            var rt = ReadRuntimeParamsFromControls();
            var curModeTag = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? agent.RunnerMode;
            bool showTemp = IsTemperatureSupportedMode(curModeTag);
            var paramDigest = (showTemp ? $"温度 {rt.Temperature:0.00} · " : "") +
                              $"推理 {rt.ReasoningEffort} · 上下文 {rt.ContextBudget}" +
                              (rt.TimeoutSeconds >= 30 ? $" · 超时 {rt.TimeoutSeconds}s" : "");
            if (!canSelectModels)
            {
                sourceStatusText.Text = $"🔒 客户端默认托管已锁定禁改模型（防风控）  |  {paramDigest}";
                sourceStatusText.Foreground = BrushHex("#FCD34D");
                return;
            }

            var selPreset = (presetCombo.SelectedItem as ComboBoxItem)?.Tag as AgentConfigPreset;
            int onlineHintCount = CloudRecommendationService.CachedOnlineHintCount;
            var cloudSrc = onlineHintCount > 0 ? $"网上实时库 ({onlineHintCount}模型)" : "LiveBench矩阵";

            if (selPreset != null)
            {
                if (string.Equals(selPreset.SourceKind, "user", StringComparison.OrdinalIgnoreCase))
                {
                    sourceStatusText.Text = $"⭐ 我的方案「{selPreset.Name}」 · {paramDigest}";
                    sourceStatusText.Foreground = BrushHex("#38BDF8");
                }
                else
                {
                    sourceStatusText.Text = $"🌐 {cloudSrc} + {latestLocalSourceDesc}  |  {paramDigest}";
                    sourceStatusText.Foreground = BrushHex("#34D399");
                }
            }
            else
            {
                sourceStatusText.Text = $"✋ 已手动微调（点右上「💾 存为方案」可保存为多套）  |  {paramDigest}";
                sourceStatusText.Foreground = BrushHex("#FCD34D");
            }
        }

        void RefreshPresetComboItems(string? targetPresetId = null)
        {
            suppressUiSyncEvents = true;
            try
            {
                presetCombo.Items.Clear();
                ComboBoxItem? targetItem = null;
                foreach (var cp in cloudPresets)
                {
                    var item = new ComboBoxItem
                    {
                        Content = cp.Name,
                        Tag = cp,
                        ToolTip = $"{cp.Description}\n依据：{cp.SourceBasis}"
                    };
                    presetCombo.Items.Add(item);
                    if (!string.IsNullOrWhiteSpace(targetPresetId) &&
                        string.Equals(cp.PresetId, targetPresetId, StringComparison.OrdinalIgnoreCase))
                    {
                        targetItem = item;
                    }
                }
                foreach (var up in agent.CustomPresets)
                {
                    var item = new ComboBoxItem
                    {
                        Content = $"⭐ 我的方案 · {up.Name}",
                        Tag = up,
                        ToolTip = $"{up.Description}\n保存时间：{up.UpdatedAt}"
                    };
                    presetCombo.Items.Add(item);
                    if (!string.IsNullOrWhiteSpace(targetPresetId) &&
                        string.Equals(up.PresetId, targetPresetId, StringComparison.OrdinalIgnoreCase))
                    {
                        targetItem = item;
                    }
                }
                var customMarker = new ComboBoxItem
                {
                    Content = "✋ 当前手动微调（未存为新方案）",
                    Tag = "__unsaved_manual__",
                    ToolTip = "当前已手动微调参数，点击右侧「💾 存为方案」可命名保存为新方案"
                };
                presetCombo.Items.Add(customMarker);

                if (targetItem != null)
                {
                    presetCombo.SelectedItem = targetItem;
                }
                else
                {
                    presetCombo.SelectedItem = currentSelectionMode == "manual"
                        ? customMarker
                        : (presetCombo.Items.Count > 0 ? presetCombo.Items[0] : null);
                }

                delPresetBtn.IsEnabled = (presetCombo.SelectedItem as ComboBoxItem)?.Tag is AgentConfigPreset p &&
                                         string.Equals(p.SourceKind, "user", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                suppressUiSyncEvents = false;
            }
            UpdateModeBannerVisual();
        }

        void MarkDraftAsManualCustom()
        {
            if (suppressUiSyncEvents) return;
            currentSelectionMode = "manual";
            activePresetId = "__custom__";
            suppressUiSyncEvents = true;
            try
            {
                foreach (var obj in presetCombo.Items)
                {
                    if (obj is ComboBoxItem ci && ci.Tag is string s && s == "__unsaved_manual__")
                    {
                        presetCombo.SelectedItem = ci;
                        break;
                    }
                }
                delPresetBtn.IsEnabled = false;
            }
            finally
            {
                suppressUiSyncEvents = false;
            }
            UpdateModeBannerVisual();
        }

        // ==================== 页签 1：模型分工槽位与单模型参数 ====================
        var listHeaderGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        listHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        listHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = canSelectModels ? new GridLength(282) : GridLength.Auto });

        var listTitleText = new TextBlock
        {
            Text = canSelectModels
                ? "模型分工槽位（拖动 ⋮⋮ 可上下排序 · 靠前优先）"
                : "主要负责的工作范围（模型已锁定 · 仅配置擅长工作内容）",
            Foreground = BrushHex("#F1F5F9"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        SelectComboByTag(tierStrategyCombo, string.IsNullOrWhiteSpace(agent.PreferredEffortTier) ? "auto" : agent.PreferredEffortTier);
        tierStrategyCombo.SelectionChanged += (_, _) => MarkDraftAsManualCustom();

        Grid.SetColumn(listTitleText, 0);
        Grid.SetColumn(tierStrategyCombo, 1);
        listHeaderGrid.Children.Add(listTitleText);
        listHeaderGrid.Children.Add(tierStrategyCombo);
        Grid.SetRow(listHeaderGrid, 0);
        page1.Children.Add(listHeaderGrid);

        static bool IsInteractiveChild(DependencyObject? source)
        {
            for (var cur = source; cur != null; cur = VisualTreeHelper.GetParent(cur))
            {
                if (cur is System.Windows.Controls.Primitives.ButtonBase ||
                    cur is ComboBox ||
                    cur is ComboBoxItem ||
                    cur is TextBox)
                {
                    return true;
                }
            }
            return false;
        }

        Action? cancelActiveCardDrag = null;
        win.Deactivated += (_, _) => cancelActiveCardDrag?.Invoke();
        win.Closed += (_, _) => cancelActiveCardDrag?.Invoke();

        void UpdateAddModelRow()
        {
            if (!canSelectModels)
            {
                addRowHost.Content = null;
                return;
            }
            var pool = new List<string>(agent.AvailableModels ?? new List<string>());
            if (allowCustomModelInput)
            {
                foreach (var mp in editableProfiles)
                {
                    if (!string.IsNullOrWhiteSpace(mp.ModelName) && !pool.Contains(mp.ModelName, StringComparer.OrdinalIgnoreCase))
                        pool.Insert(0, mp.ModelName);
                }
            }

            var addRow = new Grid { Margin = new Thickness(0, 2, 0, 6) };
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var addModelCombo = new ComboBox { Height = 27, FontSize = 12 };
            var unusedModels = pool.Where(m => !editableProfiles.Any(p => string.Equals(p.ModelName, m, StringComparison.OrdinalIgnoreCase))).ToList();
            var usedModels = pool.Where(m => editableProfiles.Any(p => string.Equals(p.ModelName, m, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var avail in unusedModels.Concat(usedModels))
            {
                var spec = CloudRecommendationService.InferCloudModelSpec(agent.Id, avail);
                var shortTag = ExtractPureSpecialtyLabel(spec.CloudBadge);
                var ctxBadge = FormatTokenCountBadge(spec.ContextWindowTokens);
                var alreadyUsed = editableProfiles.Any(p => string.Equals(p.ModelName, avail, StringComparison.OrdinalIgnoreCase));
                var prefix = alreadyUsed ? "[已配] " : "";
                addModelCombo.Items.Add(new ComboBoxItem
                {
                    Content = string.IsNullOrEmpty(shortTag) ? $"{prefix}{avail}" : $"{prefix}{avail}  ·  {shortTag}",
                    Tag = avail,
                    ToolTip = $"{avail}\n{spec.SpecialtyNote}\n在线规格：上下文 {ctxBadge} · 建议温度 {spec.RecommendedTemperature:0.00}（{spec.SourceTag}）"
                });
            }
            if (allowCustomModelInput)
            {
                addModelCombo.Items.Add(new ComboBoxItem { Content = "➕ 自定义输入其他模型名...", Tag = "__custom__" });
            }
            if (addModelCombo.Items.Count > 0) addModelCombo.SelectedIndex = 0;

            bool suppressAddSelect = false;
            ComboBoxItem? lastAddSelection = addModelCombo.SelectedItem as ComboBoxItem;
            addModelCombo.SelectionChanged += (_, _) =>
            {
                if (suppressAddSelect) return;
                if (addModelCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag && tag == "__custom__")
                {
                    var custom = PromptTextInputDialog(win, "添加自定义模型名称", "请输入本机或云端自定义模型名称（如：claude-opus-4-6 / deepseek-reasoner）：", "");
                    if (string.IsNullOrWhiteSpace(custom))
                    {
                        suppressAddSelect = true;
                        addModelCombo.SelectedItem = lastAddSelection;
                        suppressAddSelect = false;
                        return;
                    }
                    var trimmed = custom.Trim();
                    agent.AvailableModels ??= new List<string>();
                    if (!agent.AvailableModels.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                        agent.AvailableModels.Insert(0, trimmed);
                    var customSpec = CloudRecommendationService.InferCloudModelSpec(agent.Id, trimmed);
                    var newItem = new ComboBoxItem
                    {
                        Content = $"{trimmed}  ·  {ExtractPureSpecialtyLabel(customSpec.CloudBadge)}",
                        Tag = trimmed
                    };
                    addModelCombo.Items.Insert(0, newItem);
                    suppressAddSelect = true;
                    addModelCombo.SelectedItem = newItem;
                    suppressAddSelect = false;
                    lastAddSelection = newItem;
                    return;
                }
                lastAddSelection = addModelCombo.SelectedItem as ComboBoxItem;
            };

            var addModelBtn = new Button
            {
                Content = "＋ 添加模型分工",
                Style = Application.Current?.FindResource("ActionBtn") as Style,
                Padding = new Thickness(10, 3, 10, 3),
                Height = 27,
                FontSize = 11.5
            };
            addModelBtn.Click += (_, _) =>
            {
                var mn = (addModelCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                if (string.IsNullOrWhiteSpace(mn) || mn == "__custom__") return;
                var spec = CloudRecommendationService.InferCloudModelSpec(agent.Id, mn);
                editableProfiles.Add(new ModelWorkProfile
                {
                    ModelName = mn,
                    PrimaryRoles = new List<string>(spec.RecommendedRoles),
                    SpeedTier = spec.SpeedTier,
                    RecommendedEffort = spec.Tier,
                    SpecialtyNote = spec.SpecialtyNote,
                    ContextWindowTokens = spec.ContextWindowTokens,
                    MaxOutputTokens = spec.MaxOutputTokens
                });
                MarkDraftAsManualCustom();
                RebuildModelProfilesUi();
            };

            Grid.SetColumn(addModelCombo, 0);
            Grid.SetColumn(addModelBtn, 2);
            addRow.Children.Add(addModelCombo);
            addRow.Children.Add(addModelBtn);
            addRowHost.Content = addRow;
        }

        void RebuildModelProfilesUi()
        {
            cancelActiveCardDrag?.Invoke();
            cardsHost.Children.Clear();
            UpdateModeBannerVisual();

            var pool = new List<string>(agent.AvailableModels ?? new List<string>());
            if (allowCustomModelInput)
            {
                foreach (var mp in editableProfiles)
                {
                    if (!string.IsNullOrWhiteSpace(mp.ModelName) && !pool.Contains(mp.ModelName, StringComparer.OrdinalIgnoreCase))
                        pool.Insert(0, mp.ModelName);
                }
            }

            var cardBorders = new List<Border>();
            var cardTransforms = new List<TranslateTransform>();
            var currentOffsets = new double[editableProfiles.Count];

            int dragIdx = -1;
            int hoverIdx = -1;
            bool isDragging = false;
            double startMouseY = 0;
            double[] origTops = Array.Empty<double>();
            double[] origHeights = Array.Empty<double>();
            double hostHeight = 0;
            UIElement? capturedElement = null;

            void AnimateCardOffset(int idx, double targetY)
            {
                if (idx < 0 || idx >= cardTransforms.Count) return;
                if (Math.Abs(currentOffsets[idx] - targetY) < 0.5) return;
                currentOffsets[idx] = targetY;
                var anim = new System.Windows.Media.Animation.DoubleAnimation(targetY, TimeSpan.FromMilliseconds(110))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                };
                cardTransforms[idx].BeginAnimation(TranslateTransform.YProperty, anim);
            }

            void FinishDrag(bool commit)
            {
                if (capturedElement != null && capturedElement.IsMouseCaptured)
                {
                    var ce = capturedElement;
                    capturedElement = null;
                    ce.ReleaseMouseCapture();
                }

                bool moved = commit && isDragging && dragIdx >= 0 && hoverIdx >= 0 && hoverIdx != dragIdx;
                int from = dragIdx;
                int to = hoverIdx;

                dragIdx = -1;
                hoverIdx = -1;
                isDragging = false;

                if (moved)
                {
                    var item = editableProfiles[from];
                    editableProfiles.RemoveAt(from);
                    editableProfiles.Insert(to, item);
                    MarkDraftAsManualCustom();
                    RebuildModelProfilesUi();
                    return;
                }

                for (int i = 0; i < cardBorders.Count; i++)
                {
                    var cb = cardBorders[i];
                    Panel.SetZIndex(cb, 0);
                    cb.Background = BrushHex("#101723");
                    cb.BorderBrush = BrushHex("#212E42");
                    cb.BorderThickness = new Thickness(1);
                    cb.Effect = null;
                    cardTransforms[i].BeginAnimation(TranslateTransform.YProperty, null);
                    cardTransforms[i].Y = 0;
                    currentOffsets[i] = 0;
                }
            }

            for (int slotIdx = 0; slotIdx < editableProfiles.Count; slotIdx++)
            {
                var mp = editableProfiles[slotIdx];
                var rt = new TranslateTransform(0, 0);
                cardTransforms.Add(rt);

                var card = new Border
                {
                    Background = BrushHex("#101723"),
                    BorderBrush = BrushHex("#212E42"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 6, 9, 6),
                    Margin = new Thickness(0, 0, 0, 5),
                    RenderTransform = rt
                };
                cardBorders.Add(card);
                cancelActiveCardDrag = () => FinishDrag(commit: false);
                var sp = new StackPanel();

                var top = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0: 拖拽把手
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 1: 模型下拉
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(122) }); // 3: 档位
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(114) }); // 5: 单模型温度
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 7: 移除

                var dragHandle = new Border
                {
                    Background = BrushHex("#172233"),
                    BorderBrush = BrushHex("#283952"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 0, 6, 0),
                    Margin = new Thickness(0, 0, 7, 0),
                    Height = 26,
                    Cursor = System.Windows.Input.Cursors.SizeAll,
                    ToolTip = "按住上下拖动整个条目调整顺序（靠前优先命中）"
                };
                var handleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                handleStack.Children.Add(new TextBlock
                {
                    Text = "⋮⋮",
                    Foreground = BrushHex("#94A3B8"),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                handleStack.Children.Add(new TextBlock
                {
                    Text = $"{slotIdx + 1}",
                    Foreground = BrushHex("#38BDF8"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                dragHandle.Child = handleStack;

                int myIdx = slotIdx;
                void PreparePointerDrag(System.Windows.Input.MouseButtonEventArgs e)
                {
                    if (cardBorders.Count <= 1) return;
                    dragIdx = myIdx;
                    hoverIdx = myIdx;
                    isDragging = false;
                    startMouseY = e.GetPosition(cardsHost).Y;
                    hostHeight = cardsHost.ActualHeight;
                    origTops = new double[cardBorders.Count];
                    origHeights = new double[cardBorders.Count];
                    for (int k = 0; k < cardBorders.Count; k++)
                    {
                        origTops[k] = cardBorders[k].TranslatePoint(new Point(0, 0), cardsHost).Y;
                        origHeights[k] = cardBorders[k].ActualHeight;
                        currentOffsets[k] = 0;
                    }
                }

                void HandlePointerMove(UIElement captureTarget, System.Windows.Input.MouseEventArgs e)
                {
                    if (dragIdx != myIdx) return;
                    if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
                    {
                        FinishDrag(commit: false);
                        return;
                    }
                    double curY = e.GetPosition(cardsHost).Y;
                    double rawDy = curY - startMouseY;

                    if (!isDragging)
                    {
                        if (Math.Abs(rawDy) < 4) return;
                        isDragging = true;
                        if (capturedElement == null)
                        {
                            capturedElement = captureTarget;
                            captureTarget.CaptureMouse();
                        }
                        var draggedCard = cardBorders[dragIdx];
                        Panel.SetZIndex(draggedCard, 999);
                        draggedCard.Background = BrushHex("#17253B");
                        draggedCard.BorderBrush = BrushHex("#38BDF8");
                        draggedCard.BorderThickness = new Thickness(1.5);
                        draggedCard.Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            Color = Colors.Black,
                            BlurRadius = 18,
                            ShadowDepth = 4,
                            Opacity = 0.65
                        };
                    }

                    double minDy = -origTops[dragIdx] - 8;
                    double maxDy = (hostHeight - origTops[dragIdx] - origHeights[dragIdx]) + 8;
                    double dy = Math.Clamp(rawDy, minDy, maxDy);
                    cardTransforms[dragIdx].Y = dy;

                    double mouseInScrollY = e.GetPosition(cardsScroll).Y;
                    if (mouseInScrollY < 26 && cardsScroll.VerticalOffset > 0)
                        cardsScroll.ScrollToVerticalOffset(Math.Max(0, cardsScroll.VerticalOffset - 10));
                    else if (mouseInScrollY > cardsScroll.ActualHeight - 26 && cardsScroll.VerticalOffset < cardsScroll.ScrollableHeight)
                        cardsScroll.ScrollToVerticalOffset(Math.Min(cardsScroll.ScrollableHeight, cardsScroll.VerticalOffset + 10));

                    double dragCenterY = origTops[dragIdx] + origHeights[dragIdx] * 0.5 + dy;
                    int newHover = dragIdx;
                    if (dy < 0)
                    {
                        for (int i = dragIdx - 1; i >= 0; i--)
                            if (dragCenterY < origTops[i] + origHeights[i] * 0.5) newHover = i;
                    }
                    else if (dy > 0)
                    {
                        for (int i = dragIdx + 1; i < cardBorders.Count; i++)
                            if (dragCenterY > origTops[i] + origHeights[i] * 0.5) newHover = i;
                    }

                    if (newHover != hoverIdx)
                    {
                        hoverIdx = newHover;
                        double step = origHeights[dragIdx] + 5.0;
                        for (int k = 0; k < cardBorders.Count; k++)
                        {
                            if (k == dragIdx) continue;
                            double offset = 0;
                            if (hoverIdx > dragIdx && k > dragIdx && k <= hoverIdx) offset = -step;
                            else if (hoverIdx < dragIdx && k < dragIdx && k >= hoverIdx) offset = step;
                            AnimateCardOffset(k, offset);
                        }
                    }
                }

                dragHandle.MouseLeftButtonDown += (_, e) => { PreparePointerDrag(e); e.Handled = true; };
                dragHandle.MouseMove += (_, e) => HandlePointerMove(dragHandle, e);
                dragHandle.MouseLeftButtonUp += (_, e) => { if (dragIdx == myIdx) { FinishDrag(commit: true); e.Handled = true; } };
                dragHandle.LostMouseCapture += (_, _) => { if (dragIdx == myIdx && capturedElement == dragHandle) FinishDrag(commit: false); };

                card.PreviewMouseLeftButtonDown += (_, e) => { if (!IsInteractiveChild(e.OriginalSource as DependencyObject)) PreparePointerDrag(e); };
                card.PreviewMouseMove += (_, e) => HandlePointerMove(card, e);
                card.PreviewMouseLeftButtonUp += (_, e) => { if (dragIdx == myIdx) { bool was = isDragging; FinishDrag(commit: true); if (was) e.Handled = true; } };
                card.LostMouseCapture += (_, _) => { if (dragIdx == myIdx && capturedElement == card) FinishDrag(commit: false); };

                var captureMp = mp;
                var modelCombo = new ComboBox { Height = 26, FontSize = 11.5 };
                ComboBoxItem? currentModelItem = null;
                foreach (var avail in pool)
                {
                    var spec = CloudRecommendationService.InferCloudModelSpec(agent.Id, avail);
                    var shortTag = ExtractPureSpecialtyLabel(spec.CloudBadge);
                    var ctxBadge = FormatTokenCountBadge(spec.ContextWindowTokens);
                    var outBadge = FormatTokenCountBadge(spec.MaxOutputTokens);
                    var item = new ComboBoxItem
                    {
                        Content = string.IsNullOrEmpty(shortTag) ? avail : $"{avail}  ·  {shortTag}",
                        Tag = avail,
                        ToolTip = $"{avail}\n{spec.SpecialtyNote}\n在线规格：上下文 {ctxBadge} · 最大输出 {outBadge} · 建议温度 {spec.RecommendedTemperature:0.00}\n依据：{spec.SourceTag}"
                    };
                    modelCombo.Items.Add(item);
                    if (string.Equals(avail, mp.ModelName, StringComparison.OrdinalIgnoreCase))
                    {
                        modelCombo.SelectedItem = item;
                        currentModelItem = item;
                    }
                }
                if (allowCustomModelInput)
                {
                    modelCombo.Items.Add(new ComboBoxItem { Content = "➕ 自定义输入其他模型名...", Tag = "__custom__" });
                }

                bool suppressModelChange = false;
                modelCombo.SelectionChanged += (_, _) =>
                {
                    if (suppressModelChange) return;
                    if (modelCombo.SelectedItem is not ComboBoxItem sel || sel.Tag is not string picked) return;

                    if (picked == "__custom__")
                    {
                        var customName = PromptTextInputDialog(win, "添加自定义模型名称", "请输入本机或云端自定义模型名称（如：claude-opus-4-6 / deepseek-reasoner）：", captureMp.ModelName);
                        if (string.IsNullOrWhiteSpace(customName))
                        {
                            suppressModelChange = true;
                            modelCombo.SelectedItem = currentModelItem;
                            suppressModelChange = false;
                            return;
                        }
                        picked = customName.Trim();
                        agent.AvailableModels ??= new List<string>();
                        if (!agent.AvailableModels.Contains(picked, StringComparer.OrdinalIgnoreCase))
                            agent.AvailableModels.Add(picked);
                    }

                    var newSpec = CloudRecommendationService.InferCloudModelSpec(agent.Id, picked);
                    captureMp.ModelName = picked;
                    captureMp.RecommendedEffort = newSpec.Tier;
                    captureMp.SpeedTier = newSpec.SpeedTier;
                    captureMp.PrimaryRoles = new List<string>(newSpec.RecommendedRoles);
                    captureMp.SpecialtyNote = newSpec.SpecialtyNote;
                    captureMp.ContextWindowTokens = newSpec.ContextWindowTokens;
                    captureMp.MaxOutputTokens = newSpec.MaxOutputTokens;
                    MarkDraftAsManualCustom();
                    RebuildModelProfilesUi();
                };

                var effortBox = new ComboBox { Height = 26, FontSize = 11 };
                foreach (var (ek, el) in new[]
                {
                    ("high", "🔥 高档 · 深思"),
                    ("medium", "⚡ 中档 · 主力"),
                    ("low", "🚀 低档 · 极速")
                })
                {
                    var item = new ComboBoxItem { Content = el, Tag = ek };
                    effortBox.Items.Add(item);
                    if (string.Equals(mp.RecommendedEffort, ek, StringComparison.OrdinalIgnoreCase))
                        effortBox.SelectedItem = item;
                }
                if (effortBox.SelectedIndex < 0) effortBox.SelectedIndex = 1;
                effortBox.SelectionChanged += (_, _) =>
                {
                    if (effortBox.SelectedItem is ComboBoxItem ci && ci.Tag is string eVal)
                    {
                        captureMp.RecommendedEffort = eVal;
                        captureMp.SpeedTier = eVal == "low" ? "fast" : eVal;
                        MarkDraftAsManualCustom();
                    }
                };

                var slotSpec = CloudRecommendationService.InferCloudModelSpec(agent.Id, mp.ModelName);
                int showCtx = mp.ContextWindowTokens > 0 ? mp.ContextWindowTokens : slotSpec.ContextWindowTokens;
                var curModeForSlot = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? agent.RunnerMode;
                bool showSlotTemp = IsTemperatureSupportedMode(curModeForSlot);

                var slotCtxBadge = new Border
                {
                    Background = BrushHex("#151F2E"),
                    BorderBrush = BrushHex("#24334B"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 3, 6, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = showSlotTemp ? Visibility.Collapsed : Visibility.Visible,
                    ToolTip = $"该模型在线规格：上下文 {FormatTokenCountBadge(showCtx)} / 最大输出 {FormatTokenCountBadge(slotSpec.MaxOutputTokens)}（桌面客户端由高/中/低档位控制）",
                    Child = new TextBlock
                    {
                        Text = $"上下文 {FormatTokenCountBadge(showCtx)}",
                        FontSize = 11,
                        Foreground = BrushHex("#94A3B8"),
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };

                var slotTempCombo = new ComboBox
                {
                    Height = 26,
                    FontSize = 11,
                    Visibility = showSlotTemp ? Visibility.Visible : Visibility.Collapsed,
                    ToolTip = $"单模型温度覆写（仅 HTTP API / 自定义 CLI 模式生效）· 上下文 {FormatTokenCountBadge(showCtx)}"
                };
                void PopulateSlotTempItems(double? curTemp)
                {
                    slotTempCombo.Items.Clear();
                    slotTempCombo.Items.Add(new ComboBoxItem { Content = "温度: 默认", Tag = "null", ToolTip = "温度跟随「运行参数」页全局设置" });
                    bool matchedKnown = !curTemp.HasValue;
                    foreach (var pVal in new[] { 0.10, 0.15, 0.25, 0.30, 0.50, 0.75, 1.00 })
                    {
                        var pTxt = pVal.ToString("0.00", CultureInfo.InvariantCulture);
                        var ci = new ComboBoxItem { Content = $"温度: {pTxt}", Tag = pTxt };
                        slotTempCombo.Items.Add(ci);
                        if (curTemp.HasValue && Math.Abs(curTemp.Value - pVal) < 0.005)
                        {
                            slotTempCombo.SelectedItem = ci;
                            matchedKnown = true;
                        }
                    }
                    if (curTemp.HasValue && !matchedKnown)
                    {
                        var cTxt = curTemp.Value.ToString("0.00", CultureInfo.InvariantCulture);
                        var customCi = new ComboBoxItem
                        {
                            Content = $"温度: {cTxt}",
                            Tag = cTxt
                        };
                        slotTempCombo.Items.Insert(1, customCi);
                        slotTempCombo.SelectedItem = customCi;
                    }
                    else if (!curTemp.HasValue)
                    {
                        slotTempCombo.SelectedIndex = 0;
                    }
                    slotTempCombo.Items.Add(new ComboBoxItem { Content = "➕ 自定义温度...", Tag = "__custom_temp__" });
                }
                PopulateSlotTempItems(mp.CustomTemperature);

                bool suppressSlotTemp = false;
                slotTempCombo.SelectionChanged += (_, _) =>
                {
                    if (suppressSlotTemp) return;
                    if (slotTempCombo.SelectedItem is not ComboBoxItem ci || ci.Tag is not string rawT) return;
                    if (rawT == "__custom_temp__")
                    {
                        var init = captureMp.CustomTemperature?.ToString("0.00", CultureInfo.InvariantCulture) ?? "0.30";
                        var input = PromptTextInputDialog(win, "自定义单模型采样温度", "请输入采样温度小数（0.00 ~ 2.00），越低越严谨、越高越有创意：", init);
                        if (!string.IsNullOrWhiteSpace(input) &&
                            double.TryParse(input.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dt))
                        {
                            captureMp.CustomTemperature = Math.Clamp(Math.Round(dt, 2), 0.0, 2.0);
                            MarkDraftAsManualCustom();
                        }
                        suppressSlotTemp = true;
                        PopulateSlotTempItems(captureMp.CustomTemperature);
                        suppressSlotTemp = false;
                        return;
                    }

                    captureMp.CustomTemperature = rawT == "null"
                        ? null
                        : (double.TryParse(rawT, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) ? dv : null);
                    MarkDraftAsManualCustom();
                };

                var delBtn = new Button
                {
                    Content = "✕",
                    Style = Application.Current?.FindResource("ActionBtn") as Style,
                    Padding = new Thickness(7, 1, 7, 1),
                    Height = 26,
                    FontSize = 11,
                    IsEnabled = editableProfiles.Count > 1,
                    ToolTip = editableProfiles.Count > 1 ? "移除此模型分工槽位" : "至少保留 1 个模型槽位"
                };
                delBtn.Click += (_, _) =>
                {
                    if (editableProfiles.Count <= 1) return;
                    editableProfiles.Remove(captureMp);
                    MarkDraftAsManualCustom();
                    RebuildModelProfilesUi();
                };

                if (canSelectModels)
                {
                    Grid.SetColumn(dragHandle, 0);
                    Grid.SetColumn(modelCombo, 1);
                    Grid.SetColumn(effortBox, 3);
                    Grid.SetColumn(slotCtxBadge, 5);
                    Grid.SetColumn(slotTempCombo, 5);
                    Grid.SetColumn(delBtn, 7);
                    top.Children.Add(dragHandle);
                    top.Children.Add(modelCombo);
                    top.Children.Add(effortBox);
                    top.Children.Add(slotCtxBadge);
                    top.Children.Add(slotTempCombo);
                    top.Children.Add(delBtn);
                    sp.Children.Add(top);
                }
                else
                {
                    var lockedBanner = new Border
                    {
                        Background = BrushHex("#141D2C"),
                        BorderBrush = BrushHex("#2A3B55"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(10, 5, 10, 5),
                        Margin = new Thickness(0, 0, 0, 6),
                        Child = new TextBlock
                        {
                            Text = "🔒 当前模型：由客户端默认托管（已禁选/禁改模型，防止触发账号风控）",
                            Foreground = BrushHex("#CBD5E1"),
                            FontSize = 11.5,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    };
                    sp.Children.Add(lockedBanner);

                    var quickRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center };
                    quickRow.Children.Add(new TextBlock
                    {
                        Text = "快捷分类勾选：",
                        Foreground = BrushHex("#94A3B8"),
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0)
                    });

                    void AddCategoryToggleBtn(string title, string[] groupRoles, string tip)
                    {
                        var btn = new Button
                        {
                            Content = title,
                            Style = Application.Current?.FindResource("ActionBtn") as Style,
                            Padding = new Thickness(7, 2, 7, 2),
                            FontSize = 11,
                            Margin = new Thickness(0, 0, 5, 0),
                            ToolTip = tip
                        };
                        btn.Click += (_, _) =>
                        {
                            bool allSelected = groupRoles.All(r => captureMp.PrimaryRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
                            if (allSelected)
                                captureMp.PrimaryRoles.RemoveAll(r => groupRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
                            else
                                foreach (var r in groupRoles)
                                    if (!captureMp.PrimaryRoles.Contains(r, StringComparer.OrdinalIgnoreCase)) captureMp.PrimaryRoles.Add(r);
                            MarkDraftAsManualCustom();
                            RebuildModelProfilesUi();
                        };
                        quickRow.Children.Add(btn);
                    }

                    AddCategoryToggleBtn("💻 工程代码组", new[] { "fast_execution", "ui_shader_vfx", "deep_debug", "build_testing" }, "一键勾选/取消：代码开发、UI与特效、排障审查、构建测试");
                    AddCategoryToggleBtn("🎨 多媒体 (图/影/音/3D)", new[] { "image_generation", "video_animation", "audio_sfx_bgm", "model_3d_motion" }, "一键勾选/取消：图片贴图、视频分镜、音效配乐、3D动作");
                    AddCategoryToggleBtn("📝 策划配表组", new[] { "text_writing", "data_config", "brain_analysis" }, "一键勾选/取消：文案剧情、配表数值、总分析架构");

                    var btnAll = new Button { Content = "全选", Style = Application.Current?.FindResource("ActionBtn") as Style, Padding = new Thickness(7, 2, 7, 2), FontSize = 11, Margin = new Thickness(2, 0, 4, 0) };
                    btnAll.Click += (_, _) => { captureMp.PrimaryRoles = WorkSpecialtyCatalog.OrderedItems.Select(x => x.Id).ToList(); MarkDraftAsManualCustom(); RebuildModelProfilesUi(); };
                    var btnNone = new Button { Content = "清空", Style = Application.Current?.FindResource("ActionBtn") as Style, Padding = new Thickness(7, 2, 7, 2), FontSize = 11 };
                    btnNone.Click += (_, _) => { captureMp.PrimaryRoles.Clear(); MarkDraftAsManualCustom(); RebuildModelProfilesUi(); };
                    quickRow.Children.Add(btnAll);
                    quickRow.Children.Add(btnNone);
                    sp.Children.Add(quickRow);
                }

                var roleGrid = new System.Windows.Controls.Primitives.UniformGrid
                {
                    Columns = 4,
                    Margin = new Thickness(0, 1, 0, 0)
                };
                foreach (var item in WorkSpecialtyCatalog.OrderedItems)
                {
                    var cb = new CheckBox
                    {
                        Content = item.Label,
                        IsChecked = mp.PrimaryRoles.Contains(item.Id, StringComparer.OrdinalIgnoreCase),
                        Margin = new Thickness(0, 2, 4, 2),
                        FontSize = 11.5,
                        ToolTip = $"{item.Icon} {item.Label}：{item.Tooltip}"
                    };
                    var roleKey = item.Id;
                    cb.Click += (_, _) =>
                    {
                        if (cb.IsChecked == true)
                        {
                            if (!captureMp.PrimaryRoles.Contains(roleKey, StringComparer.OrdinalIgnoreCase))
                                captureMp.PrimaryRoles.Add(roleKey);
                        }
                        else
                        {
                            captureMp.PrimaryRoles.RemoveAll(r => r.Equals(roleKey, StringComparison.OrdinalIgnoreCase));
                        }
                        MarkDraftAsManualCustom();
                    };
                    roleGrid.Children.Add(cb);
                }
                sp.Children.Add(roleGrid);

                card.Child = sp;
                cardsHost.Children.Add(card);
            }
            UpdateAddModelRow();
        }

        Grid.SetRow(cardsScroll, 1);
        page1.Children.Add(cardsScroll);

        var workNoteRow = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        workNoteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        workNoteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var noteLbl = new TextBlock
        {
            Text = "擅长补充说明：",
            Foreground = BrushHex("#94A3B8"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        workNoteBox.TextChanged += (_, _) => MarkDraftAsManualCustom();
        Grid.SetColumn(noteLbl, 0);
        Grid.SetColumn(workNoteBox, 1);
        workNoteRow.Children.Add(noteLbl);
        workNoteRow.Children.Add(workNoteBox);

        var page1BottomFixed = new StackPanel();
        page1BottomFixed.Children.Add(addRowHost);
        page1BottomFixed.Children.Add(workNoteRow);
        Grid.SetRow(page1BottomFixed, 2);
        page1.Children.Add(page1BottomFixed);

        // ==================== 页签 2：操作权限与能力星级 ====================
        page2.Children.Add(new TextBlock
        {
            Text = "项目安全操作权限",
            Foreground = BrushHex("#F1F5F9"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var permGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        permGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        permGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        permGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        permGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(cbWrite, 0); Grid.SetColumn(cbWrite, 0);
        Grid.SetRow(cbMulti, 0); Grid.SetColumn(cbMulti, 1);
        Grid.SetRow(cbShell, 1); Grid.SetColumn(cbShell, 0);
        Grid.SetRow(cbMcp, 1); Grid.SetColumn(cbMcp, 1);
        permGrid.Children.Add(cbWrite);
        permGrid.Children.Add(cbMulti);
        permGrid.Children.Add(cbShell);
        permGrid.Children.Add(cbMcp);
        foreach (var cb in new[] { cbWrite, cbMulti, cbShell, cbMcp })
        {
            cb.Click += (_, _) => MarkDraftAsManualCustom();
        }
        page2.Children.Add(permGrid);

        page2.Children.Add(new TextBlock
        {
            Text = "细分能力评分（可随云端方案切换或手动微调）",
            Foreground = BrushHex("#F1F5F9"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var starsGrid = new Grid();
        starsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        starsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        starsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tags = reg.Tags.Tags;
        int rowCount = (tags.Count + 1) / 2;
        for (int r = 0; r < rowCount; r++) starsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < tags.Count; i++)
        {
            var t = tags[i];
            int r = i / 2;
            int c = (i % 2) * 2;

            var itemGrid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });

            var lbl = new TextBlock
            {
                Text = t.Name,
                Foreground = BrushHex("#CBD5E1"),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = string.IsNullOrWhiteSpace(t.Description) ? t.Name : t.Description
            };
            var scCombo = new ComboBox { Height = 26 };
            int curStar = agent.TagScores.TryGetValue(t.Id, out var v) ? v : 0;
            for (int s = 0; s <= 5; s++)
            {
                var item = new ComboBoxItem { Content = s == 0 ? "不参与" : $"{s} 星", Tag = s };
                scCombo.Items.Add(item);
                if (s == curStar) scCombo.SelectedItem = item;
            }
            scCombo.SelectionChanged += (_, _) => MarkDraftAsManualCustom();
            starCombos[t.Id] = scCombo;

            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(scCombo, 1);
            itemGrid.Children.Add(lbl);
            itemGrid.Children.Add(scCombo);

            Grid.SetRow(itemGrid, r);
            Grid.SetColumn(itemGrid, c);
            starsGrid.Children.Add(itemGrid);
        }
        page2.Children.Add(starsGrid);

        // ==================== 页签 3：运行参数与执行通道 ====================
        var rtCard = new Border
        {
            Background = BrushHex("#101723"),
            BorderBrush = BrushHex("#212E42"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var rtStack = new StackPanel();
        rtStack.Children.Add(new TextBlock
        {
            Text = "模型推理与运行参数（可随云端方案自动匹配，或由用户自行调整保存为多套方案）",
            Foreground = BrushHex("#F1F5F9"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var rtGrid = new Grid();
        rtGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rtGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        rtGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 4; r++) rtGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel MakeParamCell(string caption, UIElement control)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            cell.Children.Add(new TextBlock { Text = caption, Foreground = BrushHex("#94A3B8"), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 3) });
            cell.Children.Add(control);
            return cell;
        }

        var tempComboHost = new Grid();
        tempComboHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tempComboHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        tempComboHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(tempPresetCombo, 0);
        Grid.SetColumn(tempValueBox, 2);
        tempComboHost.Children.Add(tempPresetCombo);
        tempComboHost.Children.Add(tempValueBox);

        var maxOutHost = new Grid();
        maxOutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        maxOutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        maxOutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(maxOutPresetCombo, 0);
        Grid.SetColumn(maxOutValueBox, 2);
        maxOutHost.Children.Add(maxOutPresetCombo);
        maxOutHost.Children.Add(maxOutValueBox);

        var cReas = MakeParamCell("推理深度档位 (低 / 中 / 高 / 极高)", reasoningCombo);
        var cCtx = MakeParamCell("上下文窗口预算 (Context Budget)", ctxBudgetCombo);
        var cMaxO = MakeParamCell("最大输出 Token (0 = 自动不限)", maxOutHost);
        var cTimeout = MakeParamCell("单次执行超时秒数 (0 = 跟随全局)", timeoutBox);
        var cRetries = MakeParamCell("异常自动重试次数 (Max Retries)", retriesCombo);
        var cTemp = MakeParamCell("采样温度 Temperature (仅 HTTP API / 自定义 CLI 模式生效)", tempComboHost);
        var cExtra = MakeParamCell("附加 CLI / API 扩展参数 (Extra Runner Args)", extraArgsBox);

        var desktopTierHint = new TextBlock
        {
            Text = "ℹ 当前为桌面客户端 / 原生无头模式：桌面工具不提供采样温度设置，已自动聚焦「低 / 中 / 高 / 极高」推理档位控制。",
            FontSize = 11,
            Foreground = BrushHex("#64748B"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };

        Grid.SetRow(cReas, 0); Grid.SetColumn(cReas, 0);
        Grid.SetRow(cCtx, 0); Grid.SetColumn(cCtx, 2);
        Grid.SetRow(cMaxO, 1); Grid.SetColumn(cMaxO, 0);
        Grid.SetRow(cTimeout, 1); Grid.SetColumn(cTimeout, 2);
        Grid.SetRow(cRetries, 2); Grid.SetColumn(cRetries, 0);
        Grid.SetRow(cTemp, 2); Grid.SetColumn(cTemp, 2);
        Grid.SetRow(cExtra, 3); Grid.SetColumn(cExtra, 0); Grid.SetColumnSpan(cExtra, 3);
        cExtra.Margin = new Thickness(0);

        rtGrid.Children.Add(cReas);
        rtGrid.Children.Add(cCtx);
        rtGrid.Children.Add(cMaxO);
        rtGrid.Children.Add(cTimeout);
        rtGrid.Children.Add(cRetries);
        rtGrid.Children.Add(cTemp);
        rtGrid.Children.Add(cExtra);
        rtStack.Children.Add(desktopTierHint);
        rtStack.Children.Add(rtGrid);
        rtCard.Child = rtStack;
        page3.Children.Add(rtCard);

        void SyncTemperatureVisibilityByMode()
        {
            var curM = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? agent.RunnerMode;
            bool showTemp = IsTemperatureSupportedMode(curM);
            cTemp.Visibility = showTemp ? Visibility.Visible : Visibility.Collapsed;
            desktopTierHint.Visibility = showTemp ? Visibility.Collapsed : Visibility.Visible;
        }
        modeCombo.SelectionChanged += (_, _) =>
        {
            if (suppressUiSyncEvents) return;
            SyncTemperatureVisibilityByMode();
            RebuildModelProfilesUi();
            UpdateModeBannerVisual();
            MarkDraftAsManualCustom();
        };

        var chCard = new Border
        {
            Background = BrushHex("#101723"),
            BorderBrush = BrushHex("#212E42"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10)
        };
        var chStack = new StackPanel();
        chStack.Children.Add(new TextBlock
        {
            Text = "客户端窗口联动与底层执行通道",
            Foreground = BrushHex("#F1F5F9"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        chStack.Children.Add(cbLaunchWin);
        chStack.Children.Add(new TextBlock { Text = "执行驱动模式", Foreground = BrushHex("#94A3B8"), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 3) });
        SelectComboByTag(modeCombo, agent.RunnerMode);
        SyncTemperatureVisibilityByMode();
        chStack.Children.Add(modeCombo);
        chStack.Children.Add(new TextBlock { Text = "命令路径或接口地址（默认填 auto）", Foreground = BrushHex("#94A3B8"), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 3) });
        chStack.Children.Add(cmdBox);
        chCard.Child = chStack;
        page3.Children.Add(chCard);

        var cliSrc = CliInstallService.FindSource(agent.Id);
        if (cliSrc != null)
        {
            var cliCard = new Border
            {
                Background = BrushHex("#121B2E"),
                BorderBrush = BrushHex("#284066"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var cliStack = new StackPanel();
            cliStack.Children.Add(new TextBlock
            {
                Text = "官方 CLI 工具检测与一键下载安装",
                Foreground = BrushHex("#38BDF8"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            string? probedBin = CliInstallService.ProbeInstalledBinary(agent.Id);
            var cliStatusTb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(probedBin)
                    ? $"未检测到「{cliSrc.DisplayName}」可执行文件，可点击下方按钮自动拉取官方安装脚本。"
                    : $"✓ 已检测到本机 CLI：{probedBin}",
                Foreground = string.IsNullOrWhiteSpace(probedBin) ? BrushHex("#FBBF24") : BrushHex("#86EFAC"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            cliStack.Children.Add(cliStatusTb);

            var urlHintWrap = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var urlLinkTb = new TextBlock
            {
                Text = $"官方安装文档：{cliSrc.OfficialUrl}",
                Foreground = BrushHex("#7DD3FC"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 3),
                ToolTip = "点击在默认浏览器打开官方页面"
            };
            urlLinkTb.MouseLeftButtonUp += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cliSrc.OfficialUrl) { UseShellExecute = true }); } catch { }
            };
            urlHintWrap.Children.Add(urlLinkTb);
            urlHintWrap.Children.Add(new TextBlock
            {
                Text = $"安装源命令：{cliSrc.InstallCommandHint}",
                Foreground = BrushHex("#94A3B8"),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap
            });
            cliStack.Children.Add(urlHintWrap);

            var cliProgBar = new ProgressBar
            {
                Height = 8,
                Minimum = 0,
                Maximum = 1,
                Value = string.IsNullOrWhiteSpace(probedBin) ? 0 : 1,
                Foreground = BrushHex("#38BDF8"),
                Background = BrushHex("#0B1220"),
                Margin = new Thickness(0, 0, 0, 4)
            };
            var cliProgText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(probedBin)
                    ? "状态：就绪 · 点击下方按钮自动下载并显示实时百分比与字节进度"
                    : "状态：本机 CLI 已可用（支持点击按钮升级或重装最新版本）",
                Foreground = BrushHex("#CBD5E1"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            cliStack.Children.Add(cliProgBar);
            cliStack.Children.Add(cliProgText);

            var cliBtnRow = new WrapPanel { Orientation = Orientation.Horizontal };
            Style? dlActionStyle = Application.Current?.TryFindResource("ActionBtn") as Style;
            Style? dlPrimaryStyle = Application.Current?.TryFindResource("PrimaryBtn") as Style ?? dlActionStyle;

            bool dlgUpdateMode = !string.IsNullOrWhiteSpace(probedBin);
            string dlgCliIdle = dlgUpdateMode ? "↻ 更新 / 重装 CLI" : "⬇ 一键下载并安装 CLI";
            string dlgCliKey = LongActionTracker.CliUpdate(agent.Id);
            var cliInstallBtn = new Button
            {
                Content = dlgCliIdle,
                Style = dlPrimaryStyle,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 4),
                FontSize = 11.5
            };
            LongActionTracker.ApplyBusyIfNeeded(cliInstallBtn, dlgCliKey, dlgCliIdle);
            var cliRecheckBtn = new Button
            {
                Content = "🔄 重新检测",
                Style = dlActionStyle,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 4),
                FontSize = 11.5
            };

            cliRecheckBtn.Click += (_, _) =>
            {
                if (LongActionTracker.IsBusy(dlgCliKey))
                {
                    cliProgText.Text = LongActionTracker.DuplicateClickHint(dlgCliKey);
                    return;
                }
                CliInstallService.RefreshProcessPathFromEnvironment();
                var reProbed = CliInstallService.ProbeInstalledBinary(agent.Id);
                if (!string.IsNullOrWhiteSpace(reProbed))
                {
                    cliStatusTb.Text = $"✓ 已检测到本机 CLI：{reProbed}";
                    cliStatusTb.Foreground = BrushHex("#86EFAC");
                    cliProgBar.Value = 1.0;
                    cliProgText.Text = "状态：检测成功，CLI 通道可用！";
                    cliProgText.Foreground = BrushHex("#86EFAC");
                    cliInstallBtn.Content = "↻ 更新 / 重装 CLI";
                }
                else
                {
                    cliStatusTb.Text = $"⚠ 仍未检测到「{cliSrc.DisplayName}」，请点击「一键下载并安装 CLI」或查看上方官方命令。";
                    cliStatusTb.Foreground = BrushHex("#FBBF24");
                    cliProgBar.Value = 0;
                    cliProgText.Text = "状态：未检测到可执行文件";
                    cliProgText.Foreground = BrushHex("#FBBF24");
                }
            };

            cliInstallBtn.Click += async (_, _) =>
            {
                if (!LongActionTracker.TryBegin(dlgCliKey, dlgUpdateMode ? "更新中" : "安装中"))
                {
                    cliProgText.Text = LongActionTracker.DuplicateClickHint(dlgCliKey);
                    if (owner is MainWindow mainBusy) mainBusy.ReportStatus(LongActionTracker.DuplicateClickHint(dlgCliKey));
                    return;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = false;
                string msg = "";
                string ver = "";
                try
                {
                    LongActionTracker.ShowBusy(cliInstallBtn);
                    cliRecheckBtn.IsEnabled = false;
                    cliProgBar.Value = 0.04;
                    cliProgText.Foreground = BrushHex("#7DD3FC");
                    cliProgText.Text = $"正在启动 {cliSrc.DisplayName} 自动下载安装…";
                    var prog = new Progress<(double Progress, string Status)>(p =>
                    {
                        cliProgBar.Value = Math.Clamp(p.Progress, 0, 1);
                        cliProgText.Text = p.Status;
                    });
                    (ok, msg) = await CliInstallService.InstallAsync(agent.Id, prog);
                    CliInstallService.CliUpdateStatus? st = null;
                    if (ok) st = CliInstallService.InvalidateAndReloadLocalStatus(agent.Id);
                    ver = st?.LocalVersion ?? "";
                    var postBin = st?.InstalledPath;
                    if (string.IsNullOrWhiteSpace(postBin))
                        postBin = CliInstallService.ProbeInstalledBinary(agent.Id);
                    cliProgBar.Value = ok ? 1.0 : cliProgBar.Value;
                    cliProgText.Foreground = ok ? BrushHex("#86EFAC") : BrushHex("#FBBF24");
                    cliProgText.Text = msg;
                    if (ok && !string.IsNullOrWhiteSpace(postBin))
                    {
                        cliStatusTb.Text = $"✓ 已检测到本机 CLI：{postBin}";
                        cliStatusTb.Foreground = BrushHex("#86EFAC");
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    msg = ex.Message;
                    cliProgText.Foreground = BrushHex("#F87171");
                    cliProgText.Text = $"安装异常：{ex.Message}";
                }
                finally
                {
                    LongActionTracker.EndAndRestore(dlgCliKey);
                    cliRecheckBtn.IsEnabled = true;
                }
                int sec = Math.Max(0, (int)sw.Elapsed.TotalSeconds);
                string shown = (ver ?? "").Trim().TrimStart('v', 'V');
                string status = ok
                    ? (string.IsNullOrEmpty(shown)
                        ? $"✅ {agent.Name} CLI {(dlgUpdateMode ? "已更新" : "已安装")}（耗时 {sec}s）"
                        : $"✅ {agent.Name} CLI {(dlgUpdateMode ? "已更新至" : "已安装至")} v{shown}（耗时 {sec}s）")
                    : $"⚠ {agent.Name} CLI {(dlgUpdateMode ? "更新" : "安装")}失败：{msg}（耗时 {sec}s）";
                if (owner is MainWindow mainDone)
                {
                    mainDone.ReportStatus(status);
                    mainDone.RefreshAllViews();
                }
            };

            cliBtnRow.Children.Add(cliInstallBtn);
            cliBtnRow.Children.Add(cliRecheckBtn);
            if (agent.Id is "grok_bot" or "cursor")
            {
                var cliLoginBtn = new Button
                {
                    Content = "🔑 终端登录授权",
                    Style = dlActionStyle,
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 0, 4),
                    FontSize = 11.5,
                    ToolTip = "在独立终端打开官方 CLI 登录向导（如 grok login / cursor-agent login）"
                };
                cliLoginBtn.Click += (_, _) => CliInstallService.LaunchInteractiveCliLogin(agent.Id);
                cliBtnRow.Children.Add(cliLoginBtn);
            }

            cliStack.Children.Add(cliBtnRow);
            cliCard.Child = cliStack;
            page3.Children.Insert(0, cliCard);
        }

        // 页签 3 参数联动事件绑定
        SelectTempPresetCombo(tempPresetCombo, agent.RuntimeParams.Temperature);
        SelectComboByTag(reasoningCombo, agent.RuntimeParams.ReasoningEffort);
        SelectComboByTag(ctxBudgetCombo, agent.RuntimeParams.ContextBudget);
        SelectComboByIntTag(maxOutPresetCombo, agent.RuntimeParams.MaxOutputTokens, -1);
        SelectComboByIntTag(retriesCombo, agent.RuntimeParams.MaxRetries, 0);

        tempPresetCombo.SelectionChanged += (_, _) =>
        {
            if (suppressUiSyncEvents) return;
            if (tempPresetCombo.SelectedItem is ComboBoxItem ci && ci.Tag is double tv)
            {
                suppressUiSyncEvents = true;
                tempValueBox.Text = tv.ToString("0.00", CultureInfo.InvariantCulture);
                suppressUiSyncEvents = false;
                MarkDraftAsManualCustom();
            }
        };
        tempValueBox.TextChanged += (_, _) =>
        {
            if (suppressUiSyncEvents) return;
            if (double.TryParse(tempValueBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tv))
            {
                suppressUiSyncEvents = true;
                SelectTempPresetCombo(tempPresetCombo, tv);
                suppressUiSyncEvents = false;
            }
            MarkDraftAsManualCustom();
        };
        maxOutPresetCombo.SelectionChanged += (_, _) =>
        {
            if (suppressUiSyncEvents) return;
            if (maxOutPresetCombo.SelectedItem is ComboBoxItem ci && ci.Tag is int mv)
            {
                suppressUiSyncEvents = true;
                maxOutValueBox.Text = mv.ToString(CultureInfo.InvariantCulture);
                suppressUiSyncEvents = false;
                MarkDraftAsManualCustom();
            }
        };
        maxOutValueBox.TextChanged += (_, _) =>
        {
            if (suppressUiSyncEvents) return;
            if (int.TryParse(maxOutValueBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mv))
            {
                suppressUiSyncEvents = true;
                SelectComboByIntTag(maxOutPresetCombo, mv, -1);
                suppressUiSyncEvents = false;
            }
            MarkDraftAsManualCustom();
        };
        reasoningCombo.SelectionChanged += (_, _) => MarkDraftAsManualCustom();
        ctxBudgetCombo.SelectionChanged += (_, _) => MarkDraftAsManualCustom();
        timeoutBox.TextChanged += (_, _) => MarkDraftAsManualCustom();
        retriesCombo.SelectionChanged += (_, _) => MarkDraftAsManualCustom();
        extraArgsBox.TextChanged += (_, _) => MarkDraftAsManualCustom();
        cbLaunchWin.Click += (_, _) => MarkDraftAsManualCustom();
        modeCombo.SelectionChanged += (_, _) => MarkDraftAsManualCustom();
        cmdBox.TextChanged += (_, _) => MarkDraftAsManualCustom();

        // 核心草稿加载器（将任意云端方案 / 用户方案完整无损写入全部 3 个页签控件）
        void LoadPresetIntoDraft(AgentConfigPreset preset, bool preserveChannel)
        {
            suppressUiSyncEvents = true;
            try
            {
                activePresetId = string.IsNullOrWhiteSpace(preset.PresetId) ? "cloud_balanced" : preset.PresetId;
                currentSelectionMode = string.Equals(preset.SourceKind, "user", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto";

                editableProfiles.Clear();
                if (!canSelectModels)
                {
                    var managedSpec = CloudRecommendationService.InferCloudModelSpec(agent.Id, LocalModelCatalogService.ManagedDefaultModelDisplay);
                    var roles = (preset.ModelProfiles ?? new List<ModelWorkProfile>())
                        .SelectMany(p => p.PrimaryRoles ?? new List<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (roles.Count == 0) roles = new List<string> { "text_writing", "image_generation", "video_animation" };
                    editableProfiles.Add(new ModelWorkProfile
                    {
                        ModelName = LocalModelCatalogService.ManagedDefaultModelDisplay,
                        PrimaryRoles = roles,
                        RecommendedEffort = "medium",
                        SpeedTier = "medium",
                        SpecialtyNote = managedSpec.SpecialtyNote,
                        CustomTemperature = preset.ModelProfiles?.FirstOrDefault()?.CustomTemperature,
                        ContextWindowTokens = managedSpec.ContextWindowTokens,
                        MaxOutputTokens = managedSpec.MaxOutputTokens
                    });
                }
                else
                {
                    foreach (var mp in preset.ModelProfiles ?? new List<ModelWorkProfile>())
                    {
                        editableProfiles.Add(mp.Clone());
                    }
                }

                SelectComboByTag(tierStrategyCombo, canSelectModels ? (preset.PreferredEffortTier ?? "auto") : "auto");
                workNoteBox.Text = preset.CustomWorkNote ?? "";

                var perms = preset.Permissions ?? new AgentPermissions();
                cbWrite.IsChecked = perms.AllowDirectSourceWrite;
                cbMulti.IsChecked = perms.AllowMultiDir;
                cbShell.IsChecked = perms.AllowShellBuild;
                cbMcp.IsChecked = perms.HasPreviewMcp;

                foreach (var kv in starCombos)
                {
                    int sVal = preset.TagScores != null && preset.TagScores.TryGetValue(kv.Key, out var sv) ? sv : 0;
                    kv.Value.SelectedIndex = Math.Clamp(sVal, 0, 5);
                }

                var rt = preset.RuntimeParams ?? new AgentRuntimeParams();
                tempValueBox.Text = rt.Temperature.ToString("0.00", CultureInfo.InvariantCulture);
                SelectTempPresetCombo(tempPresetCombo, rt.Temperature);
                SelectComboByTag(reasoningCombo, rt.ReasoningEffort);
                SelectComboByTag(ctxBudgetCombo, rt.ContextBudget);
                maxOutValueBox.Text = rt.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
                SelectComboByIntTag(maxOutPresetCombo, rt.MaxOutputTokens, -1);
                timeoutBox.Text = rt.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
                SelectComboByIntTag(retriesCombo, rt.MaxRetries, 0);
                extraArgsBox.Text = rt.ExtraRunnerArgs ?? "";

                if (!preserveChannel && !string.IsNullOrWhiteSpace(preset.RunnerMode))
                {
                    cbLaunchWin.IsChecked = preset.LaunchWindowOnDispatch;
                    SelectComboByTag(modeCombo, preset.RunnerMode);
                    cmdBox.Text = preset.RunnerCmd ?? "";
                }

                delPresetBtn.IsEnabled = string.Equals(preset.SourceKind, "user", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                suppressUiSyncEvents = false;
            }
            RebuildModelProfilesUi();
        }

        presetCombo.SelectionChanged += (_, _) =>
        {
            if (suppressUiSyncEvents) return;
            if (presetCombo.SelectedItem is ComboBoxItem ci && ci.Tag is AgentConfigPreset pickedPreset)
            {
                bool isUser = string.Equals(pickedPreset.SourceKind, "user", StringComparison.OrdinalIgnoreCase);
                LoadPresetIntoDraft(pickedPreset, preserveChannel: !isUser);
                delPresetBtn.IsEnabled = isUser;
            }
            else
            {
                delPresetBtn.IsEnabled = false;
                UpdateModeBannerVisual();
            }
        };

        savePresetBtn.Click += (_, _) =>
        {
            var currentUserPreset = (presetCombo.SelectedItem as ComboBoxItem)?.Tag as AgentConfigPreset;
            var defaultName = currentUserPreset != null && string.Equals(currentUserPreset.SourceKind, "user", StringComparison.OrdinalIgnoreCase)
                ? currentUserPreset.Name
                : $"{agent.Name} 我的方案 {agent.CustomPresets.Count + 1}";

            var inputName = PromptTextInputDialog(
                win,
                "保存自定义配置方案",
                "请输入方案名称（将完整保存当前模型、12 项分工、温度/推理/超时、权限与星级；若名称相同则自动覆盖更新）：",
                defaultName);
            if (string.IsNullOrWhiteSpace(inputName)) return;

            var snap = BuildDraftPresetSnapshot("temp", inputName.Trim(), "user", "");
            var tempAgent = new AgentRosterEntry
            {
                Id = agent.Id,
                Name = agent.Name,
                PreferredEffortTier = snap.PreferredEffortTier,
                CustomWorkNote = snap.CustomWorkNote,
                ModelProfiles = snap.ModelProfiles.Select(p => p.Clone()).ToList(),
                Permissions = snap.Permissions.Clone(),
                TagScores = new Dictionary<string, int>(snap.TagScores, StringComparer.OrdinalIgnoreCase),
                RuntimeParams = snap.RuntimeParams.Clone(),
                LaunchWindowOnDispatch = snap.LaunchWindowOnDispatch,
                RunnerMode = snap.RunnerMode,
                RunnerCmd = snap.RunnerCmd,
                CustomPresets = agent.CustomPresets
            };
            var savedPreset = CloudRecommendationService.CaptureCurrentAsUserPreset(tempAgent, inputName.Trim());
            activePresetId = savedPreset.PresetId;
            currentSelectionMode = "manual";
            reg.SaveUserConfig();
            RefreshPresetComboItems(savedPreset.PresetId);
        };

        delPresetBtn.Click += (_, _) =>
        {
            if ((presetCombo.SelectedItem as ComboBoxItem)?.Tag is not AgentConfigPreset curPreset ||
                !string.Equals(curPreset.SourceKind, "user", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            CloudRecommendationService.DeleteUserPreset(agent, curPreset.PresetId);
            reg.SaveUserConfig();
            var fallback = cloudPresets.FirstOrDefault(p => p.PresetId == "cloud_balanced") ?? cloudPresets[0];
            LoadPresetIntoDraft(fallback, preserveChannel: true);
            RefreshPresetComboItems(fallback.PresetId);
        };

        string agentCloudKey = LongActionTracker.CloudSyncAgent(agent.Id);
        LongActionTracker.ApplyBusyIfNeeded(syncLocalCloudBtn, agentCloudKey, "↻ 联网刷新");
        syncLocalCloudBtn.Click += (_, _) =>
        {
            if (!LongActionTracker.TryBegin(agentCloudKey, "同步中"))
            {
                sourceStatusText.Text = LongActionTracker.DuplicateClickHint(agentCloudKey);
                return;
            }
            var cloudSw = System.Diagnostics.Stopwatch.StartNew();
            LongActionTracker.ShowBusy(syncLocalCloudBtn);
            var targetAgentId = agent.Id;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                Exception? error = null;
                try { LocalModelCatalogService.ProbeLocalModelsForAgent(targetAgentId, forceRefresh: true); }
                catch (Exception ex) { error = ex; }
                try { cloudSvc.FetchOnlineHintsOnly(); }
                catch (Exception ex) { error ??= ex; }
                win.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (!win.IsLoaded && !win.IsVisible) return;
                        if (error != null)
                        {
                            sourceStatusText.Text = $"⚠ 同步失败：{error.Message}（耗时 {(int)cloudSw.Elapsed.TotalSeconds}s）";
                            sourceStatusText.Foreground = BrushHex("#FBBF24");
                            return;
                        }
                        var scanNow = LocalModelCatalogService.ProbeLocalModelsForAgent(targetAgentId, forceRefresh: false);
                        latestLocalSourceDesc = scanNow.SourceDescription;
                        cloudPresets = CloudRecommendationService.BuildCloudPresetsForAgent(reg, agent, refreshLocalModels: false);
                        var selectedCloud = cloudPresets.FirstOrDefault(p => string.Equals(p.PresetId, activePresetId, StringComparison.OrdinalIgnoreCase))
                                            ?? cloudPresets[0];
                        LoadPresetIntoDraft(selectedCloud, preserveChannel: true);
                        RefreshPresetComboItems(selectedCloud.PresetId);
                        sourceStatusText.Text = $"✅ 已刷新本机模型与云端参数（耗时 {(int)cloudSw.Elapsed.TotalSeconds}s）";
                        sourceStatusText.Foreground = BrushHex("#34D399");
                    }
                    finally
                    {
                        LongActionTracker.EndAndRestore(agentCloudKey);
                    }
                });
            });
        };

        // 首次载入当前列表与激活方案标识
        RebuildModelProfilesUi();
        RefreshPresetComboItems(activePresetId);

        // ==================== 3. 底部常驻按钮栏（含导入/导出多套方案 + 恢复推荐 + 保存） ====================
        bool saved = false;
        var bottomBar = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DockPanel.SetDock(bottomBar, Dock.Bottom);

        var leftBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var applyRecBtn = new Button
        {
            Content = "恢复云端推荐",
            Style = Application.Current?.FindResource("ActionBtn") as Style,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "恢复为该 AI 的「🌐 云端方案 · 均衡全能协同（推荐）」全套参数"
        };
        applyRecBtn.Click += (_, _) =>
        {
            cloudPresets = CloudRecommendationService.BuildCloudPresetsForAgent(reg, agent, refreshLocalModels: true);
            var balanced = cloudPresets.FirstOrDefault(p => p.PresetId == "cloud_balanced") ?? cloudPresets[0];
            LoadPresetIntoDraft(balanced, preserveChannel: true);
            RefreshPresetComboItems(balanced.PresetId);
        };

        var exportPresetBtn = new Button
        {
            Content = "📤 导出方案",
            Style = Application.Current?.FindResource("ActionBtn") as Style,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "将当前全部模型、分工与运行参数导出为 .json 方案文件备份或分享"
        };
        exportPresetBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出 AI 配置方案",
                Filter = "AI 配置方案 (*.json)|*.json",
                FileName = $"{agent.Id}_preset_{DateTime.Now:yyyyMMdd_HHmm}.json"
            };
            if (dlg.ShowDialog(win) != true) return;
            var curPreset = (presetCombo.SelectedItem as ComboBoxItem)?.Tag as AgentConfigPreset;
            var exportName = curPreset != null ? curPreset.Name : $"{agent.Name} 导出方案";
            var snap = BuildDraftPresetSnapshot("user_" + Guid.NewGuid().ToString("N")[..8], exportName, "user", curPreset?.Description ?? "导出备份方案");
            RegistryService.WriteJsonAtomic(dlg.FileName, snap);
            MessageBox.Show(win, $"已导出当前全参数配置方案到：\n{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        var importPresetBtn = new Button
        {
            Content = "📥 导入方案",
            Style = Application.Current?.FindResource("ActionBtn") as Style,
            ToolTip = "从本地 .json 文件导入配置方案到「我的方案」并立即载入"
        };
        importPresetBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入 AI 配置方案",
                Filter = "AI 配置方案 (*.json)|*.json"
            };
            if (dlg.ShowDialog(win) != true) return;
            try
            {
                var imported = JsonSerializer.Deserialize<AgentConfigPreset>(File.ReadAllText(dlg.FileName, Encoding.UTF8), RegistryService.JsonOpts);
                if (imported == null)
                {
                    MessageBox.Show(win, "未能解析该 JSON 方案文件。", "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                imported.SourceKind = "user";
                if (string.IsNullOrWhiteSpace(imported.Name)) imported.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
                imported.PresetId = "user_" + Guid.NewGuid().ToString("N")[..8];
                imported.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                if (!canSelectModels)
                {
                    var roles = (imported.ModelProfiles ?? new List<ModelWorkProfile>())
                        .SelectMany(p => p.PrimaryRoles ?? new List<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    imported.ModelProfiles = new List<ModelWorkProfile>
                    {
                        new() { ModelName = LocalModelCatalogService.ManagedDefaultModelDisplay, PrimaryRoles = roles }
                    };
                }
                agent.CustomPresets.Insert(0, imported);
                reg.SaveUserConfig();
                LoadPresetIntoDraft(imported, preserveChannel: string.IsNullOrWhiteSpace(imported.RunnerMode));
                RefreshPresetComboItems(imported.PresetId);
            }
            catch (Exception ex)
            {
                MessageBox.Show(win, $"导入配置方案出错：{ex.Message}", "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        leftBtns.Children.Add(applyRecBtn);
        leftBtns.Children.Add(exportPresetBtn);
        leftBtns.Children.Add(importPresetBtn);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var cancelBtn = new Button { Content = "取消", Style = Application.Current?.FindResource("ActionBtn") as Style, Margin = new Thickness(0, 0, 8, 0) };
        var saveBtn = new Button { Content = "保存配置", Style = Application.Current?.FindResource("PrimaryBtn") as Style, Padding = new Thickness(16, 5, 16, 5) };
        cancelBtn.Click += (_, _) => win.Close();
        saveBtn.Click += (_, _) =>
        {
            var finalSnap = BuildDraftPresetSnapshot(activePresetId, "current", currentSelectionMode == "auto" ? "cloud" : "user", "");
            agent.Permissions = finalSnap.Permissions.Clone();
            agent.LaunchWindowOnDispatch = finalSnap.LaunchWindowOnDispatch;
            agent.PreferredEffortTier = canSelectModels ? finalSnap.PreferredEffortTier : "auto";
            agent.ModelSelectionMode = currentSelectionMode;
            agent.ActivePresetId = activePresetId;
            agent.CustomWorkNote = finalSnap.CustomWorkNote;
            agent.RuntimeParams = finalSnap.RuntimeParams.Clone();
            agent.ModelProfiles = finalSnap.ModelProfiles;
            CloudRecommendationService.SyncDerivedFieldsFromProfiles(agent);
            agent.RunnerMode = finalSnap.RunnerMode;
            agent.RunnerCmd = finalSnap.RunnerCmd;
            foreach (var kv in finalSnap.TagScores)
            {
                agent.TagScores[kv.Key] = kv.Value;
            }
            reg.SaveUserConfig();
            saved = true;
            win.Close();
        };
        rightBtns.Children.Add(cancelBtn);
        rightBtns.Children.Add(saveBtn);

        Grid.SetColumn(leftBtns, 0);
        Grid.SetColumn(rightBtns, 2);
        bottomBar.Children.Add(leftBtns);
        bottomBar.Children.Add(rightBtns);
        outerDock.Children.Add(bottomBar);

        var pagesHost = new Grid();
        pagesHost.Children.Add(page1);
        pagesHost.Children.Add(page2Scroll);
        pagesHost.Children.Add(page3Scroll);
        outerDock.Children.Add(pagesHost);

        SwitchModalTab(0);
        win.Tag = (Action<int>)SwitchModalTab;
        win.Content = outerDock;
        return (win, () => saved);
    }

    public static string? PromptTextInputDialog(Window? owner, string title, string promptCaption, string initialText)
    {
        var promptWin = CreateBaseDialog(owner, title, 450, 155);
        AttachModalLightbox(promptWin, owner);
        var sp = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        sp.Children.Add(new TextBlock
        {
            Text = promptCaption,
            Foreground = BrushHex("#94A3B8"),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });
        var input = new TextBox { Text = initialText ?? "", Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(input);

        string? result = null;
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Style = Application.Current?.FindResource("ActionBtn") as Style, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "确认", Style = Application.Current?.FindResource("PrimaryBtn") as Style, Padding = new Thickness(14, 4, 14, 4) };
        cancel.Click += (_, _) => promptWin.Close();
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            result = input.Text.Trim();
            promptWin.Close();
        };
        btnRow.Children.Add(cancel);
        btnRow.Children.Add(ok);
        sp.Children.Add(btnRow);
        promptWin.Content = sp;
        promptWin.ShowDialog();
        return result;
    }

    // 4. 二级模态窗口：云端 AI 能力推荐榜单与多套团队配置流派一键应用
    public static bool ShowCloudRecommendationDialog(
        Window? owner,
        RegistryService reg,
        CloudRecommendationService cloudSvc)
    {
        var (win, getApplied) = BuildCloudRecommendationDialogWindow(owner, reg, cloudSvc);
        win.ShowDialog();
        return getApplied();
    }

    public static (Window Window, Func<bool> GetApplied) BuildCloudRecommendationDialogWindow(
        Window? owner,
        RegistryService reg,
        CloudRecommendationService cloudSvc)
    {
        var win = CreateBaseDialog(owner, "🌐 云端 AI 能力推荐榜单与多套阵容配置方案", 750, 580);
        var root = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };

        var topBar = new Border
        {
            Background = BrushHex("#111D32"),
            BorderBrush = BrushHex("#28436E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(topBar, Dock.Top);

        var statusTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#7DD3FC") };
        var statusSub = new TextBlock { FontSize = 11.5, Foreground = BrushHex("#9AA8BE"), Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap };

        var fleetRow = new Grid();
        fleetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fleetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fleetRow.Children.Add(new TextBlock
        {
            Text = "全局协同方案流派：",
            Foreground = BrushHex("#CBD5E1"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        var fleetCombo = new ComboBox { Height = 27, FontSize = 11.5 };
        foreach (var fp in CloudRecommendationService.GetGlobalFleetPresets())
        {
            fleetCombo.Items.Add(new ComboBoxItem { Content = fp.Name, Tag = fp.FleetId, ToolTip = fp.Description });
        }
        fleetCombo.SelectedIndex = 0;
        Grid.SetColumn(fleetCombo, 1);
        fleetRow.Children.Add(fleetCombo);

        var fleetDescText = new TextBlock { FontSize = 11, Foreground = BrushHex("#94A3B8"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };

        var topStack = new StackPanel();
        topStack.Children.Add(statusTitle);
        topStack.Children.Add(statusSub);
        topStack.Children.Add(fleetRow);
        topStack.Children.Add(fleetDescText);
        topBar.Child = topStack;
        root.Children.Add(topBar);

        var bottomBar = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DockPanel.SetDock(bottomBar, Dock.Bottom);

        var cbAutoSync = new CheckBox
        {
            Content = "软件启动时自动同步最新云端推荐评分",
            IsChecked = reg.UserConfig.AutoSyncCloudRecommendations,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        cbAutoSync.Click += (_, _) =>
        {
            reg.UserConfig.AutoSyncCloudRecommendations = cbAutoSync.IsChecked == true;
            reg.SaveUserConfig();
        };
        Grid.SetColumn(cbAutoSync, 0);

        var btnWrap = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var refreshOnlineBtn = new Button { Content = "🔄 重新从云端拉取", Style = Application.Current?.FindResource("ActionBtn") as Style, Margin = new Thickness(0, 0, 8, 0) };
        LongActionTracker.ApplyBusyIfNeeded(refreshOnlineBtn, LongActionTracker.CloudSync, "🔄 重新从云端拉取");
        var applyAllBtn = new Button { Content = "✨ 一键应用当前云端方案", Style = Application.Current?.FindResource("PrimaryBtn") as Style, Margin = new Thickness(0, 0, 8, 0), ToolTip = "应用所选流派的全部推荐模型、温度/推理/权限与能力评分（保持各 AI 原有启用开关不变）" };
        var closeBtn = new Button { Content = "关闭", Style = Application.Current?.FindResource("ActionBtn") as Style };
        btnWrap.Children.Add(refreshOnlineBtn);
        btnWrap.Children.Add(applyAllBtn);
        btnWrap.Children.Add(closeBtn);
        Grid.SetColumn(btnWrap, 1);
        bottomBar.Children.Add(cbAutoSync);
        bottomBar.Children.Add(btnWrap);
        root.Children.Add(bottomBar);

        var listStack = new StackPanel();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listStack };
        root.Children.Add(scroll);

        bool appliedAny = false;
        void RenderReportFromMemory(bool applyNow, bool? fetchedOverride = null, int? onlineCountOverride = null, string? sourceOverride = null)
        {
            var selectedFleetId = (fleetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "fleet_balanced";
            var fleetMeta = CloudRecommendationService.GetGlobalFleetPresets().FirstOrDefault(f => f.FleetId == selectedFleetId);
            fleetDescText.Text = fleetMeta?.Description ?? "";

            var rep = cloudSvc.BuildAndApplyReportFromCache(reg, applyToAgents: applyNow, fetchedOverride, onlineCountOverride, sourceOverride, fleetPresetId: selectedFleetId);
            if (applyNow) appliedAny = true;
            statusTitle.Text = rep.FetchedOnline
                ? $"✓ 已连接云端实时模型库 · 共分析 {rep.OnlineModelsAnalyzed} 个主流大模型（4 套团队流派可切换）"
                : "● 已加载内置 SWE-bench / LiveBench 黄金基准库（4 套团队流派可切换）";
            statusSub.Text = $"数据来源：{rep.SourceName}   |   更新时间：{rep.UpdatedAt}";

            listStack.Children.Clear();
            foreach (var item in rep.Items)
            {
                var card = new Border
                {
                    Background = BrushHex("#162032"),
                    BorderBrush = BrushHex("#24324B"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = $"{item.AgentName} — {item.RoleSummary}", FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = BrushHex("#F1F5F9"), TextWrapping = TextWrapping.Wrap });
                var rt = item.RecommendedRuntimeParams ?? new AgentRuntimeParams();
                var permBadge = item.RecommendedPermissions.AllowDirectSourceWrite ? "可改工程" : "只读禁写";
                sp.Children.Add(new TextBlock
                {
                    Text = $"参数配置：温度 {rt.Temperature:0.00} · 推理 {rt.ReasoningEffort} · 上下文 {rt.ContextBudget} · 权限 [{permBadge}]   |   依据：{item.BenchmarkBasis}",
                    FontSize = 11,
                    Foreground = BrushHex("#8C9BB3"),
                    Margin = new Thickness(0, 2, 0, 2),
                    TextWrapping = TextWrapping.Wrap
                });
                sp.Children.Add(new TextBlock
                {
                    Text = $"三档模型：🔥高档 [{item.RecommendedHighModel}]   |   ⚡中档 [{item.RecommendedMediumModel}]   |   🚀低档 [{item.RecommendedLowModel}]",
                    FontSize = 11,
                    Foreground = BrushHex("#FCD34D"),
                    Margin = new Thickness(0, 0, 0, 3),
                    TextWrapping = TextWrapping.Wrap
                });

                var tagsLine = string.Join("   |   ", item.RecommendedTags
                    .Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv =>
                    {
                        var tn = reg.Tags.Tags.FirstOrDefault(t => t.Id == kv.Key)?.Name ?? kv.Key;
                        return $"{tn} ★{kv.Value}";
                    }));
                sp.Children.Add(new TextBlock { Text = tagsLine, FontSize = 11.5, Foreground = BrushHex("#38BDF8"), TextWrapping = TextWrapping.Wrap });
                card.Child = sp;
                listStack.Children.Add(card);
            }
        }

        fleetCombo.SelectionChanged += (_, _) => RenderReportFromMemory(applyNow: false);

        void TriggerAsyncCloudRefresh()
        {
            if (AppPaths.IsIsolatedTest) return;
            if (!LongActionTracker.TryBegin(LongActionTracker.CloudSync, "同步中"))
            {
                statusSub.Text = LongActionTracker.DuplicateClickHint(LongActionTracker.CloudSync);
                return;
            }
            var cloudSw = System.Diagnostics.Stopwatch.StartNew();
            LongActionTracker.ShowBusy(refreshOnlineBtn);
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                Exception? error = null;
                (bool FetchedOnline, int OnlineModelsAnalyzed, string SourceName) net = default;
                try { net = cloudSvc.FetchOnlineHintsOnly(); }
                catch (Exception ex) { error = ex; }
                win.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (!win.IsLoaded && !win.IsVisible) return;
                        if (error != null)
                        {
                            statusTitle.Text = "⚠ 云端同步失败";
                            statusSub.Text = $"{error.Message}（耗时 {(int)cloudSw.Elapsed.TotalSeconds}s）";
                            return;
                        }
                        RenderReportFromMemory(applyNow: false, net.FetchedOnline, net.OnlineModelsAnalyzed, net.SourceName);
                        statusSub.Text = $"{statusSub.Text}   |   耗时 {(int)cloudSw.Elapsed.TotalSeconds}s";
                        if (!net.FetchedOnline)
                            statusTitle.Text = "⚠ 云端拉取未成功，已保留本地基准";
                    }
                    finally
                    {
                        LongActionTracker.EndAndRestore(LongActionTracker.CloudSync);
                    }
                });
            });
        }

        refreshOnlineBtn.Click += (_, _) => TriggerAsyncCloudRefresh();
        applyAllBtn.Click += (_, _) =>
        {
            RenderReportFromMemory(applyNow: true);
            win.Close();
        };
        closeBtn.Click += (_, _) => win.Close();

        RenderReportFromMemory(applyNow: false);
        if (!AppPaths.IsIsolatedTest)
        {
            win.Loaded += (_, _) => TriggerAsyncCloudRefresh();
        }
        win.Content = root;
        return (win, () => appliedAny);
    }

    private static readonly HashSet<Window> LightboxWired = new();

    /// <summary>二级模态打开时给主界面叠灯箱暗幕，关闭时按引用计数收回。</summary>
    public static void AttachModalLightbox(Window dialog, Window? owner)
    {
        if (!LightboxWired.Add(dialog)) return;
        var host = FindLightboxHost(owner) ?? FindLightboxHost(dialog.Owner);
        if (host == null)
        {
            LightboxWired.Remove(dialog);
            return;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            try { host.PushModalBackdrop(); } catch { }
        }
        void OnClosed(object? sender, EventArgs e)
        {
            try { host.PopModalBackdrop(); } catch { }
            dialog.Loaded -= OnLoaded;
            dialog.Closed -= OnClosed;
            LightboxWired.Remove(dialog);
        }
        dialog.Loaded += OnLoaded;
        dialog.Closed += OnClosed;
    }

    private static MainWindow? FindLightboxHost(Window? owner)
    {
        for (Window? cur = owner; cur != null; cur = cur.Owner)
        {
            if (cur is MainWindow main) return main;
        }
        return Application.Current?.MainWindow as MainWindow;
    }

    public static Window CreateBaseDialog(Window? owner, string title, double width, double height)
    {
        var w = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            Height = height + 36,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            Background = BrushHex("#131D2E"),
            Foreground = BrushHex("#E6EDF7"),
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            ResizeMode = ResizeMode.NoResize
        };

        System.Windows.Shell.WindowChrome.SetWindowChrome(w, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            UseAeroCaptionButtons = false
        });

        var template = new ControlTemplate(typeof(Window));
        var outerBorder = new FrameworkElementFactory(typeof(Border));
        outerBorder.SetValue(Border.BackgroundProperty, BrushHex("#131D2E"));
        outerBorder.SetValue(Border.BorderBrushProperty, BrushHex("#5B84BA"));
        outerBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1.8));
        outerBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));

        var dock = new FrameworkElementFactory(typeof(DockPanel));

        var topGlow = new FrameworkElementFactory(typeof(Border));
        topGlow.SetValue(DockPanel.DockProperty, Dock.Top);
        topGlow.SetValue(FrameworkElement.HeightProperty, 2.0);
        topGlow.SetValue(Border.BackgroundProperty, BrushHex("#38BDF8"));
        topGlow.SetValue(UIElement.IsHitTestVisibleProperty, false);

        var titleBar = new FrameworkElementFactory(typeof(Border));
        titleBar.SetValue(DockPanel.DockProperty, Dock.Top);
        titleBar.SetValue(Border.BackgroundProperty, BrushHex("#1A273D"));
        titleBar.SetValue(Border.BorderBrushProperty, BrushHex("#334E73"));
        titleBar.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        titleBar.SetValue(Border.PaddingProperty, new Thickness(14, 6, 8, 6));
        titleBar.AddHandler(UIElement.MouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler((s, e) =>
        {
            if (e.OriginalSource is DependencyObject d)
            {
                for (var cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur))
                {
                    if (cur is System.Windows.Controls.Primitives.ButtonBase) return;
                }
            }
            try { w.DragMove(); } catch { }
        }));

        var headerGrid = new FrameworkElementFactory(typeof(Grid));
        var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var col2 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        headerGrid.AppendChild(col1);
        headerGrid.AppendChild(col2);

        var titleText = new FrameworkElementFactory(typeof(TextBlock));
        titleText.SetValue(Grid.ColumnProperty, 0);
        titleText.SetValue(TextBlock.TextProperty, title);
        titleText.SetValue(TextBlock.FontSizeProperty, 13.0);
        titleText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        titleText.SetValue(TextBlock.ForegroundProperty, BrushHex("#F1F5F9"));
        titleText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var closeBtn = new FrameworkElementFactory(typeof(Button));
        closeBtn.SetValue(Grid.ColumnProperty, 1);
        closeBtn.SetValue(ContentControl.ContentProperty, "✕");
        if (Application.Current?.TryFindResource("WinCloseBtn") is Style closeStyle)
        {
            closeBtn.SetValue(FrameworkElement.StyleProperty, closeStyle);
        }
        closeBtn.SetValue(FrameworkElement.WidthProperty, 32.0);
        closeBtn.SetValue(FrameworkElement.HeightProperty, 26.0);
        closeBtn.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => w.Close()));

        headerGrid.AppendChild(titleText);
        headerGrid.AppendChild(closeBtn);
        titleBar.AppendChild(headerGrid);

        var contentHost = new FrameworkElementFactory(typeof(ContentPresenter));
        dock.AppendChild(topGlow);
        dock.AppendChild(titleBar);
        dock.AppendChild(contentHost);
        outerBorder.AppendChild(dock);
        template.VisualTree = outerBorder;
        w.Template = template;

        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(w, TextRenderingMode.ClearType);
        AttachModalLightbox(w, owner);
        return w;
    }

    private static StackPanel MakeFieldRow(string label, UIElement input)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = BrushHex("#9AA8BE"),
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 4)
        });
        sp.Children.Add(input);
        return sp;
    }

    public sealed class StallResolutionChoice
    {
        public string ActionKind { get; set; } = "";
        public string? TargetAgentId { get; set; }
        public bool CopyTaskToClipboard { get; set; }
    }

    public static StallResolutionChoice? ShowStallResolutionDialog(
        Window? owner,
        RegistryService reg,
        ProjectProfile project,
        AgentRosterEntry stuckAgent,
        LiveAgentWorkState? liveState,
        TaskChainCheckpoint? checkpoint)
    {
        var (win, getChoice) = BuildStallResolutionDialogWindow(owner, reg, project, stuckAgent, liveState, checkpoint);
        win.ShowDialog();
        return getChoice();
    }

    public static (Window Window, Func<StallResolutionChoice?> GetChoice) BuildStallResolutionDialogWindow(
        Window? owner,
        RegistryService reg,
        ProjectProfile project,
        AgentRosterEntry stuckAgent,
        LiveAgentWorkState? liveState,
        TaskChainCheckpoint? checkpoint)
    {
        var win = CreateBaseDialog(owner, $"任务执行卡住 / 通道受阻 · 选择解决方式 ({stuckAgent.Name})", 670, 575);
        StallResolutionChoice? chosen = null;

        string errBadge = !string.IsNullOrWhiteSpace(liveState?.LastFailureBadge)
            ? liveState!.LastFailureBadge
            : (!string.IsNullOrWhiteSpace(checkpoint?.LastFailureLabel) ? checkpoint!.LastFailureLabel : "界面弹窗遮挡或通道无响应");
        string taskSummary = !string.IsNullOrWhiteSpace(checkpoint?.TaskInstruction)
            ? checkpoint!.TaskInstruction
            : (!string.IsNullOrWhiteSpace(liveState?.TaskSummary) ? liveState!.TaskSummary : "当前挂起待办工程任务");
        string shortTask = taskSummary.Length > 110 ? taskSummary[..110] + "..." : taskSummary;
        string diagTail = !string.IsNullOrWhiteSpace(checkpoint?.LastErrorDetail)
            ? checkpoint!.LastErrorDetail
            : (liveState?.LatestLogTail ?? "");
        if (diagTail.Length > 130) diagTail = diagTail[..130] + "...";

        var root = new Grid { Margin = new Thickness(16, 12, 16, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 1. 顶部异常摘要横幅
        var summaryCard = new Border
        {
            Background = BrushHex("#231715"),
            BorderBrush = BrushHex("#7F2B24"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var sumStack = new StackPanel();
        var sumHeader = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        sumHeader.Children.Add(new TextBlock
        {
            Text = $"⚠ {stuckAgent.Name}",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushHex("#FCA5A5"),
            Margin = new Thickness(0, 0, 10, 0)
        });
        sumHeader.Children.Add(new Border
        {
            Background = BrushHex("#3F1D1D"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Child = new TextBlock
            {
                Text = errBadge,
                FontSize = 11,
                Foreground = BrushHex("#FECACA")
            }
        });
        sumStack.Children.Add(sumHeader);
        sumStack.Children.Add(new TextBlock
        {
            Text = $"挂起任务：{shortTask}",
            FontSize = 11.5,
            Foreground = BrushHex("#E2E8F0"),
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(diagTail))
        {
            sumStack.Children.Add(new TextBlock
            {
                Text = $"诊断详情：{diagTail}",
                FontSize = 11,
                Foreground = BrushHex("#94A3B8"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        summaryCard.Child = sumStack;
        Grid.SetRow(summaryCard, 0);
        root.Children.Add(summaryCard);

        // 2. 中间 5 种一键解决方式列表（全程零鼠标键盘抢占）
        var optionsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var optionsStack = new StackPanel();

        Border BuildOptionCard(
            string title,
            string desc,
            string btnText,
            string btnStyleKey,
            Action onExecute,
            UIElement? extraRightControl = null,
            bool highlightRecommended = false)
        {
            var card = new Border
            {
                Background = highlightRecommended ? BrushHex("#142234") : BrushHex("#151D2B"),
                BorderBrush = highlightRecommended ? BrushHex("#2563EB") : BrushHex("#243146"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 7)
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = highlightRecommended ? BrushHex("#93C5FD") : BrushHex("#F1F5F9")
            });
            left.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 11,
                Foreground = BrushHex("#94A3B8"),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (extraRightControl != null)
            {
                right.Children.Add(extraRightControl);
            }
            var actBtn = new Button
            {
                Content = btnText,
                MinWidth = 96,
                Padding = new Thickness(11, 5, 11, 5),
                FontSize = 12
            };
            if (Application.Current?.TryFindResource(btnStyleKey) is Style st)
            {
                actBtn.Style = st;
            }
            actBtn.Click += (_, _) => onExecute();
            right.Children.Add(actBtn);
            Grid.SetColumn(right, 1);
            g.Children.Add(right);

            card.Child = g;
            return card;
        }

        var policyCombo = new ComboBox { Width = 328, Height = 27, FontSize = 11.5 };
        foreach (var (pk, pl) in new[]
        {
            ("auto_then_ask", "先后台自动重试，仍卡住则让我选择（推荐）"),
            ("ask_user_immediately", "一旦卡住立即提醒并由我选择解决方式"),
            ("auto_self_heal", "全自动静默自愈（换通道/换AI/交回主脑）")
        })
        {
            var item = new ComboBoxItem { Content = pl, Tag = pk };
            policyCombo.Items.Add(item);
            if (string.Equals(reg.UserConfig.StallRecoveryPolicy, pk, StringComparison.OrdinalIgnoreCase))
            {
                policyCombo.SelectedItem = item;
            }
        }
        if (policyCombo.SelectedItem == null && policyCombo.Items.Count > 0) policyCombo.SelectedIndex = 0;

        void PersistPolicyAndClose(StallResolutionChoice choice)
        {
            if (policyCombo.SelectedItem is ComboBoxItem selItem && selItem.Tag is string pTag)
            {
                reg.UserConfig.StallRecoveryPolicy = pTag;
                reg.SaveUserConfig();
            }
            chosen = choice;
            win.Close();
        }

        // 选项 1：清理弹窗并在新干净对话后台重试
        optionsStack.Children.Add(BuildOptionCard(
            "1. ↻ 后台静默清理弹窗 · 新建干净对话重试（推荐）",
            "自动在后台点掉阻塞模态框（如 Create New Bot），在所属项目分组下新建/切换干净会话重发，全程不占用您的鼠标键盘。",
            "清理并重试",
            "PrimaryBtn",
            () => PersistPolicyAndClose(new StallResolutionChoice { ActionKind = "retry_clean_dialog" }),
            highlightRecommended: true));

        // 选项 2：切换纯无头 CLI / 桥接后台通道重试
        optionsStack.Children.Add(BuildOptionCard(
            "2. ⚡ 切换纯无头 CLI / 后台桥接通道重试",
            "彻底跳过该 AI 桌面客户端界面，直接调用命令行 CLI（如 grok.exe）或本地无头桥接通道在后台静默执行。",
            "走纯 CLI 重试",
            "ActionBtn",
            () => PersistPolicyAndClose(new StallResolutionChoice { ActionKind = "retry_pure_cli" })));

        // 选项 3：改派给其他在岗 AI 工具接力
        var altAgents = reg.UserConfig.Agents
            .Where(a => a.Enabled && a.Detected &&
                        !a.Id.Equals(stuckAgent.Id, StringComparison.OrdinalIgnoreCase) &&
                        !a.Id.Equals("antigravity", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var altCombo = new ComboBox
        {
            Width = 136,
            Height = 27,
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 8, 0)
        };
        foreach (var alt in altAgents)
        {
            altCombo.Items.Add(new ComboBoxItem { Content = alt.Name, Tag = alt.Id });
        }
        if (altCombo.Items.Count == 0)
        {
            altCombo.Items.Add(new ComboBoxItem { Content = "自动选最佳替补", Tag = "auto" });
        }
        altCombo.SelectedIndex = 0;

        optionsStack.Children.Add(BuildOptionCard(
            "3. ⇄ 改派给其他在岗 AI 工具后台接力",
            "自动关闭原工具阻塞弹窗并清除报错，将断点任务无缝转交给另一款在岗 AI 工具继续落地修改工程。",
            "改派接力",
            "AmberBtn",
            () =>
            {
                var targetId = (altCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
                PersistPolicyAndClose(new StallResolutionChoice { ActionKind = "switch_other_agent", TargetAgentId = targetId });
            },
            extraRightControl: altCombo));

        // 选项 4：交由主脑 AI 直接接管完成
        optionsStack.Children.Add(BuildOptionCard(
            "4. ★ 交由主脑 AI 直接接管完成（并复制任务指令）",
            "释放外部子工具阻塞，标记本次任务正式移交主脑 AI 直接在主会话落地完成，同时将完整指令复制到剪贴板备用。",
            "交由主脑",
            "ActionBtn",
            () => PersistPolicyAndClose(new StallResolutionChoice
            {
                ActionKind = "handover_to_master",
                CopyTaskToClipboard = true
            })));

        // 选项 5：仅清理弹窗并取消复位
        optionsStack.Children.Add(BuildOptionCard(
            "5. ✕ 取消本次挂起任务 · 后台关闭弹窗并复位",
            "不重新派发任务，仅在后台静默关闭外部客户端残留弹窗、清除异常警告与断点，恢复空闲待命状态。",
            "取消并复位",
            "ActionBtn",
            () => PersistPolicyAndClose(new StallResolutionChoice { ActionKind = "cancel_and_reset" })));

        optionsScroll.Content = optionsStack;
        Grid.SetRow(optionsScroll, 1);
        root.Children.Add(optionsScroll);

        // 3. 底部：默认卡死恢复策略快捷设定 + 暂不处理按钮
        var footerBorder = new Border
        {
            BorderBrush = BrushHex("#222D40"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 0),
            Margin = new Thickness(0, 6, 0, 0)
        };
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var policyWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        policyWrap.Children.Add(new TextBlock
        {
            Text = "今后默认策略：",
            FontSize = 11.5,
            Foreground = BrushHex("#94A3B8"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        policyCombo.SelectionChanged += (_, _) =>
        {
            if (policyCombo.SelectedItem is ComboBoxItem selItem && selItem.Tag is string pTag)
            {
                reg.UserConfig.StallRecoveryPolicy = pTag;
                reg.SaveUserConfig();
            }
        };
        policyWrap.Children.Add(policyCombo);
        Grid.SetColumn(policyWrap, 0);
        footerGrid.Children.Add(policyWrap);

        var closeBtn = new Button
        {
            Content = "暂不处理",
            Padding = new Thickness(14, 5, 14, 5),
            FontSize = 12
        };
        if (Application.Current?.TryFindResource("ActionBtn") is Style closeSt) closeBtn.Style = closeSt;
        closeBtn.Click += (_, _) => win.Close();
        Grid.SetColumn(closeBtn, 1);
        footerGrid.Children.Add(closeBtn);

        footerBorder.Child = footerGrid;
        Grid.SetRow(footerBorder, 2);
        root.Children.Add(footerBorder);

        win.Content = root;
        return (win, () => chosen);
    }

    public static string? PickFolder(string initialPath)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择工程或桥接目录",
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : ""
        };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public enum CloseConfirmChoiceKind
    {
        Cancel = 0,
        MinimizeToTray = 1,
        ExitApp = 2
    }

    public sealed class CloseConfirmChoice
    {
        public CloseConfirmChoiceKind Kind { get; set; } = CloseConfirmChoiceKind.Cancel;
        public bool Remember { get; set; }
    }

    /// <summary>构造暗色主题「关闭主界面确认」弹窗，供 ShowDialog / --self-test / --capture-live 复用。</summary>
    public static (Window Window, Func<CloseConfirmChoice?> GetChoice, CheckBox RememberCheck, Button MinimizeBtn, Button ExitBtn, Button CancelBtn)
        BuildCloseConfirmDialogWindow(Window? owner)
    {
        var win = CreateBaseDialog(owner, "关闭主界面确认", 520, 318);
        CloseConfirmChoice? chosen = null;

        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };

        root.Children.Add(new TextBlock
        {
            Text = "请选择关闭主界面后的处理方式：",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushHex("#F1F5F9"),
            Margin = new Thickness(0, 0, 0, 10)
        });

        Border MakeOptionCard(string title, string desc, string accentBorder, string accentBg)
        {
            var card = new Border
            {
                Background = BrushHex(accentBg),
                BorderBrush = BrushHex(accentBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushHex("#E2E8F0")
            });
            sp.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 11.2,
                Foreground = BrushHex("#94A3B8"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
            card.Child = sp;
            return card;
        }

        root.Children.Add(MakeOptionCard(
            "「最小化到系统托盘」",
            "隐藏主界面到右下角系统托盘图标位置（保留桌面悬浮窗与后台协同，随时可双击托盘或从悬浮窗恢复）。",
            "#2563EB", "#142234"));
        root.Children.Add(MakeOptionCard(
            "「关闭软件」",
            "彻底退出多 AI 协同中心与悬浮窗（后台任务与协同锚点将按系统设置收口）。",
            "#B45309", "#2A2013"));

        var rememberChk = new CheckBox
        {
            Content = "记住此操作，下次点击关闭按钮不再提示",
            Foreground = BrushHex("#CBD5E1"),
            FontSize = 12,
            IsChecked = false,
            Margin = new Thickness(0, 2, 0, 2)
        };
        root.Children.Add(rememberChk);
        root.Children.Add(new TextBlock
        {
            Text = "可在「系统设置」中随时修改或重置为每次询问。",
            FontSize = 11,
            Foreground = BrushHex("#64748B"),
            Margin = new Thickness(22, 0, 0, 12)
        });

        var btnRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cancelBtn = new Button
        {
            Content = "取消",
            Style = Application.Current?.TryFindResource("ActionBtn") as Style,
            MinWidth = 88,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var exitBtn = new Button
        {
            Content = "关闭软件",
            MinWidth = 100,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = BrushHex("#3F1D1D"),
            BorderBrush = BrushHex("#B91C1C"),
            Foreground = BrushHex("#FECACA"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        if (Application.Current?.TryFindResource("AmberBtn") is Style amberStyle)
        {
            exitBtn.Style = amberStyle;
        }
        var minimizeBtn = new Button
        {
            Content = "最小化到系统托盘",
            Style = Application.Current?.TryFindResource("PrimaryBtn") as Style,
            MinWidth = 148,
            Padding = new Thickness(14, 6, 14, 6),
            FontWeight = FontWeights.SemiBold
        };

        void Finish(CloseConfirmChoiceKind kind)
        {
            chosen = new CloseConfirmChoice
            {
                Kind = kind,
                Remember = rememberChk.IsChecked == true
            };
            win.Close();
        }

        cancelBtn.Click += (_, _) => Finish(CloseConfirmChoiceKind.Cancel);
        exitBtn.Click += (_, _) => Finish(CloseConfirmChoiceKind.ExitApp);
        minimizeBtn.Click += (_, _) => Finish(CloseConfirmChoiceKind.MinimizeToTray);
        win.Closed += (_, _) =>
        {
            if (chosen == null)
            {
                chosen = new CloseConfirmChoice { Kind = CloseConfirmChoiceKind.Cancel, Remember = false };
            }
        };
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                Finish(CloseConfirmChoiceKind.Cancel);
                e.Handled = true;
            }
        };

        Grid.SetColumn(cancelBtn, 1);
        Grid.SetColumn(exitBtn, 2);
        Grid.SetColumn(minimizeBtn, 3);
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(exitBtn);
        btnRow.Children.Add(minimizeBtn);
        root.Children.Add(btnRow);

        win.Content = root;
        return (win, () => chosen, rememberChk, minimizeBtn, exitBtn, cancelBtn);
    }

    public static CloseConfirmChoice? ShowCloseConfirmDialog(Window? owner)
    {
        var (win, getChoice, _, _, _, _) = BuildCloseConfirmDialogWindow(owner);
        win.ShowDialog();
        return getChoice();
    }
}
