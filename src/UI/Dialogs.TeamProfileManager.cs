using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AICollabHub.Models;
using AICollabHub.Services;

namespace AICollabHub.UI;

public static partial class Dialogs
{
    public static bool ShowTeamProfileManagerDialog(
        Window? owner,
        RegistryService reg,
        CloudRecommendationService cloudSvc)
    {
        var (win, getChanged) = BuildTeamProfileManagerDialogWindow(owner, reg, cloudSvc);
        win.ShowDialog();
        return getChanged();
    }

    public static (Window Window, Func<bool> GetChanged) BuildTeamProfileManagerDialogWindow(
        Window? owner,
        RegistryService reg,
        CloudRecommendationService cloudSvc)
    {
        reg.EnsureDefaultTeamProfiles();
        reg.SyncActiveTeamProfileWithCurrentState(saveToDisk: false);

        // 隔离自检必须同步拿到模型；日常打开先出窗口，模型库放到 Loaded 后台刷新。
        if (AppPaths.IsIsolatedTest)
        {
            LocalModelCatalogService.SyncAllAgentsWithLocalClientModels(reg.UserConfig.Agents, invalidateCache: true);
            foreach (var ag in reg.UserConfig.Agents)
            {
                CloudRecommendationService.EnsureAgentModelTiersPopulated(ag);
            }
        }

        bool anyChanged = false;
        int activeTabIdx = 0;       // 0 = 参战AI与模型, 1 = 12类工作路由
        int activeAgentSubTab = 0;  // 0 = 模型与能力, 1 = 权限与通道
        string selectedAgentId = "";
        string modelCatalogFilter = "all"; // all | native | third_party
        bool showAddModelBar = false;      // 默认收起，点击「＋ 添加模型」才展开，大幅降低视觉干扰
        var expandedModelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var win = CreateBaseDialog(owner, "配置方案中心", 1120, 720);
        var rootDock = new DockPanel { Margin = new Thickness(16, 12, 16, 12) };

        var actionStyle = (Style)Application.Current.FindResource("ActionBtn");
        var primaryStyle = (Style)Application.Current.FindResource("PrimaryBtn");

        Button MakeGhostBtn(string text, string fgHex = "#94A3B8", double fontSize = 11)
        {
            return new Button
            {
                Content = text,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = BrushHex(fgHex),
                FontSize = fontSize,
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = Cursors.Hand,
                Style = actionStyle
            };
        }

        // ==================== 底部操作栏 ====================
        var footerBorder = new Border
        {
            BorderBrush = BrushHex("#1E293B"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 0),
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(footerBorder, Dock.Bottom);

        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var footerHint = new TextBlock
        {
            Text = "操作指引：左侧选方案 → 顶部选要编辑的 AI → 设置模型规则与能力强度 → 右下角保存生效",
            FontSize = 11.2,
            Foreground = BrushHex("#64748B"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(footerHint, 0);
        footerGrid.Children.Add(footerHint);

        var footerBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var btnClose = new Button { Content = "取消", Style = actionStyle, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        var btnSaveOnly = new Button
        {
            Content = "保存修改",
            Style = actionStyle,
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "仅保存当前方案的修改，不切换当前生效方案"
        };
        var btnSaveAndActivate = new Button
        {
            Content = "保存并启用",
            Style = primaryStyle,
            Padding = new Thickness(18, 5, 18, 5),
            ToolTip = "保存修改并将当前方案立即设为全局生效方案"
        };
        footerBtns.Children.Add(btnClose);
        footerBtns.Children.Add(btnSaveOnly);
        footerBtns.Children.Add(btnSaveAndActivate);
        Grid.SetColumn(footerBtns, 1);
        footerGrid.Children.Add(footerBtns);
        footerBorder.Child = footerGrid;
        rootDock.Children.Add(footerBorder);

        // ==================== 主体双栏（无冗余套娃边框） ====================
        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // -------- 左栏：方案列表 --------
        var leftCard = new Border
        {
            Background = BrushHex("#0E1520"),
            BorderBrush = BrushHex("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 10, 10, 8)
        };
        var leftDock = new DockPanel();

        var leftHeader = new DockPanel { Margin = new Thickness(2, 0, 2, 8) };
        DockPanel.SetDock(leftHeader, Dock.Top);
        var countBadge = new TextBlock { Text = "0 套", FontSize = 11, Foreground = BrushHex("#64748B"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(countBadge, Dock.Right);
        leftHeader.Children.Add(countBadge);
        leftHeader.Children.Add(new TextBlock
        {
            Text = "配置方案",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushHex("#F1F5F9"),
            VerticalAlignment = VerticalAlignment.Center
        });
        leftDock.Children.Add(leftHeader);

        var leftToolbar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        leftToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        leftToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        leftToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        leftToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        leftToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DockPanel.SetDock(leftToolbar, Dock.Top);

        var btnNewProfile = new Button { Content = "＋ 新建", Style = primaryStyle, Padding = new Thickness(6, 4, 6, 4), FontSize = 11.2 };
        var btnCopyProfile = new Button { Content = "复制", Style = actionStyle, Padding = new Thickness(6, 4, 6, 4), FontSize = 11.2 };
        var btnDelProfile = new Button { Content = "删除", Style = actionStyle, Padding = new Thickness(7, 4, 7, 4), FontSize = 11.2 };
        Grid.SetColumn(btnNewProfile, 0);
        Grid.SetColumn(btnCopyProfile, 2);
        Grid.SetColumn(btnDelProfile, 4);
        leftToolbar.Children.Add(btnNewProfile);
        leftToolbar.Children.Add(btnCopyProfile);
        leftToolbar.Children.Add(btnDelProfile);
        leftDock.Children.Add(leftToolbar);

        // 低频导入/导出沉底，保持左栏顶部极度简洁
        var ioFooter = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        ioFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ioFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        ioFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        DockPanel.SetDock(ioFooter, Dock.Bottom);
        var btnImportProfile = new Button { Content = "导入 JSON", Style = actionStyle, Padding = new Thickness(4, 3, 4, 3), FontSize = 10.8, Foreground = BrushHex("#94A3B8") };
        var btnExportProfile = new Button { Content = "导出 JSON", Style = actionStyle, Padding = new Thickness(4, 3, 4, 3), FontSize = 10.8, Foreground = BrushHex("#94A3B8") };
        Grid.SetColumn(btnImportProfile, 0);
        Grid.SetColumn(btnExportProfile, 2);
        ioFooter.Children.Add(btnImportProfile);
        ioFooter.Children.Add(btnExportProfile);
        leftDock.Children.Add(ioFooter);

        var profilesListStack = new StackPanel();
        leftDock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = profilesListStack });
        leftCard.Child = leftDock;
        Grid.SetColumn(leftCard, 0);
        bodyGrid.Children.Add(leftCard);

        // -------- 右栏：无框清晰三段式（顶部信息行 + 下划线双页签 + 宽敞工作台） --------
        var rightGrid = new Grid();
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0. 方案名 + 全局策略极简头
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1. 下划线主分页
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2. 正文工作区

        // 0) 极简顶部信息与全局策略（去掉多重嵌套盒子，仅两行紧凑排列）
        var topHeaderStack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var nameRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lblName = new TextBlock { Text = "方案名称", FontSize = 11.2, Foreground = BrushHex("#94A3B8"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var nameBox = new TextBox { FontSize = 12.8, FontWeight = FontWeights.SemiBold, Padding = new Thickness(8, 4, 8, 4), ToolTip = "当前配置方案的显示名称（可直接输入修改）" };
        var lblDesc = new TextBlock { Text = "备注", FontSize = 11.2, Foreground = BrushHex("#94A3B8"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var descBox = new TextBox { FontSize = 11.5, Padding = new Thickness(8, 4, 8, 4), Foreground = BrushHex("#CBD5E1"), ToolTip = "给本套方案写一句用途备注，方便以后区分" };

        var topRightGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var btnFillRecommendedAll = new Button
        {
            Content = "一键推荐全套",
            Style = actionStyle,
            Padding = new Thickness(9, 4, 9, 4),
            FontSize = 11.2,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "先预览系统推荐的整套模型与能力方案，确认后再应用"
        };
        var activeStatusPill = new Border
        {
            Background = BrushHex("#064E3B"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        var activeStatusText = new TextBlock { Text = "使用中", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#6EE7B7") };
        activeStatusPill.Child = activeStatusText;
        topRightGroup.Children.Add(btnFillRecommendedAll);
        topRightGroup.Children.Add(activeStatusPill);

        Grid.SetColumn(lblName, 0);
        Grid.SetColumn(nameBox, 1);
        Grid.SetColumn(lblDesc, 3);
        Grid.SetColumn(descBox, 4);
        Grid.SetColumn(topRightGroup, 6);
        nameRow.Children.Add(lblName);
        nameRow.Children.Add(nameBox);
        nameRow.Children.Add(lblDesc);
        nameRow.Children.Add(descBox);
        nameRow.Children.Add(topRightGroup);
        topHeaderStack.Children.Add(nameRow);

        // 全局策略极简单行（无外框，标签与下拉内联）
        var stratRow = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var masterCombo = new ComboBox { FontSize = 11.5, Height = 25, Width = 165, Margin = new Thickness(0, 0, 14, 0) };
        var fallbackCombo = new ComboBox { FontSize = 11.5, Height = 25, Width = 155, Margin = new Thickness(0, 0, 14, 0) };
        var reworkCombo = new ComboBox { FontSize = 11.5, Height = 25, Width = 132, Margin = new Thickness(0, 0, 14, 0) };
        reworkCombo.Items.Add(new ComboBoxItem { Content = "0 轮（直出）", Tag = 0 });
        reworkCombo.Items.Add(new ComboBoxItem { Content = "1 轮（推荐）", Tag = 1 });
        reworkCombo.Items.Add(new ComboBoxItem { Content = "2 轮（标准）", Tag = 2 });
        reworkCombo.Items.Add(new ComboBoxItem { Content = "3 轮（严苛）", Tag = 3 });

        var chkPreferNative = new CheckBox
        {
            Content = "同能力优先自有模型",
            FontSize = 11.2,
            Foreground = BrushHex("#CBD5E1"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "同等能力星级下优先调用该工具的自有原生模型，节省第三方高级额度"
        };

        void AddInlineStrat(string lbl, UIElement ctrl)
        {
            stratRow.Children.Add(new TextBlock
            {
                Text = lbl,
                FontSize = 11.2,
                Foreground = BrushHex("#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            stratRow.Children.Add(ctrl);
        }
        AddInlineStrat("主脑：", masterCombo);
        AddInlineStrat("替补：", fallbackCombo);
        AddInlineStrat("精修：", reworkCombo);
        stratRow.Children.Add(chkPreferNative);
        topHeaderStack.Children.Add(stratRow);

        Grid.SetRow(topHeaderStack, 0);
        rightGrid.Children.Add(topHeaderStack);

        // 1) 清爽底线风格主分页栏（告别粗重的卡片框）
        var tabUnderlineBar = new Border
        {
            BorderBrush = BrushHex("#1E293B"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var tabDock = new DockPanel();
        var tabBtnsPanel = new StackPanel { Orientation = Orientation.Horizontal };

        Border MakeUnderlineTab(string text, out TextBlock textBlk)
        {
            textBlk = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushHex("#94A3B8")
            };
            return new Border
            {
                Padding = new Thickness(4, 4, 4, 7),
                Margin = new Thickness(0, 0, 20, -1),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand,
                Child = textBlk
            };
        }
        var tabAgentsBorder = MakeUnderlineTab("1. 参战 AI 与模型能力", out var tabAgentsText);
        var tabRoutesBorder = MakeUnderlineTab("2. 工作内容路由 (12类)", out var tabRoutesText);
        tabBtnsPanel.Children.Add(tabAgentsBorder);
        tabBtnsPanel.Children.Add(tabRoutesBorder);
        tabDock.Children.Add(tabBtnsPanel);
        tabUnderlineBar.Child = tabDock;
        Grid.SetRow(tabUnderlineBar, 1);
        rightGrid.Children.Add(tabUnderlineBar);

        var tabHostGrid = new Grid();
        Grid.SetRow(tabHostGrid, 2);
        rightGrid.Children.Add(tabHostGrid);

        // --- 分页 0：参战 AI 单行切换条 + 极简无套娃工作台 ---
        var tab0Grid = new Grid();
        tab0Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 参战 AI 单行条
        tab0Grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 工作台主体

        var participantsRowDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(participantsRowDock, 0);
        tab0Grid.Children.Add(participantsRowDock);

        var workbenchHost = new Grid();
        Grid.SetRow(workbenchHost, 1);
        tab0Grid.Children.Add(workbenchHost);

        // --- 分页 1：12 类工作内容路由 ---
        var tab1Dock = new DockPanel { Visibility = Visibility.Collapsed };
        var tab1Header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(tab1Header, Dock.Top);
        var btnResetRoutesAuto = new Button { Content = "全部恢复自动匹配", Style = actionStyle, Padding = new Thickness(10, 3, 10, 3), FontSize = 11.2 };
        DockPanel.SetDock(btnResetRoutesAuto, Dock.Right);
        tab1Header.Children.Add(btnResetRoutesAuto);
        tab1Header.Children.Add(new TextBlock
        {
            Text = "默认按各 AI 单模型的能力强度与规则自动择优；如需某项工作固定走指定 AI 或模型，可在此锁定。",
            FontSize = 11.5,
            Foreground = BrushHex("#94A3B8"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        tab1Dock.Children.Add(tab1Header);
        var routesStack = new StackPanel();
        tab1Dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = routesStack });

        tabHostGrid.Children.Add(tab0Grid);
        tabHostGrid.Children.Add(tab1Dock);

        Grid.SetColumn(rightGrid, 2);
        bodyGrid.Children.Add(rightGrid);
        rootDock.Children.Add(bodyGrid);

        // ==================== 状态与同步逻辑 ====================
        TeamConfigProfile selectedProfile = reg.UserConfig.TeamProfiles.FirstOrDefault(p =>
            string.Equals(p.Id, reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase))
            ?? reg.UserConfig.TeamProfiles[0];
        bool suppressSync = false;

        List<AgentRosterEntry> GetConfigurableInstalledAgents() =>
            reg.UserConfig.Agents
                .Where(a => a.Detected || a.Enabled || string.IsNullOrEmpty(a.DetectorId))
                .ToList();

        TeamAgentSnapshot EnsureSnapshotForAgent(TeamConfigProfile prof, AgentRosterEntry ag)
        {
            prof.AgentSnapshots ??= new List<TeamAgentSnapshot>();
            var snap = prof.AgentSnapshots.FirstOrDefault(s => string.Equals(s.AgentId, ag.Id, StringComparison.OrdinalIgnoreCase));
            if (snap == null)
            {
                snap = new TeamAgentSnapshot
                {
                    AgentId = ag.Id,
                    Enabled = ag.Enabled,
                    ModelSelectionMode = ag.ModelSelectionMode ?? "auto",
                    PreferredEffortTier = ag.PreferredEffortTier ?? "auto",
                    HighTierModel = ag.HighTierModel ?? "",
                    MediumTierModel = ag.MediumTierModel ?? "",
                    LowTierModel = ag.LowTierModel ?? "",
                    ActivePresetId = ag.ActivePresetId ?? "cloud_balanced",
                    CustomWorkNote = ag.CustomWorkNote ?? "",
                    RunnerMode = ag.RunnerMode ?? "",
                    RunnerCmd = ag.RunnerCmd ?? "",
                    LaunchWindowOnDispatch = ag.LaunchWindowOnDispatch,
                    PrimaryWorkRoles = new List<string>(ag.PrimaryWorkRoles ?? new List<string>()),
                    Permissions = ag.Permissions?.Clone() ?? new AgentPermissions(),
                    RuntimeParams = ag.RuntimeParams?.Clone() ?? new AgentRuntimeParams(),
                    ModelProfiles = (ag.ModelProfiles ?? new List<ModelWorkProfile>()).Select(m => m.Clone()).ToList(),
                    TagScores = new Dictionary<string, int>(ag.TagScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase)
                };
                prof.AgentSnapshots.Add(snap);
            }
            snap.PrimaryWorkRoles ??= new List<string>();
            snap.Permissions ??= new AgentPermissions();
            snap.RuntimeParams ??= new AgentRuntimeParams();
            snap.ModelProfiles ??= new List<ModelWorkProfile>();
            snap.TagScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(snap.RunnerMode)) snap.RunnerMode = ag.RunnerMode ?? "";

            if (snap.ModelProfiles.Count == 0)
            {
                if (!LocalModelCatalogService.SupportsModelSelection(ag.Id))
                {
                    var roles = snap.PrimaryWorkRoles.Count > 0 ? new List<string>(snap.PrimaryWorkRoles) : new List<string> { "text_writing", "image_generation", "video_animation" };
                    var managed = new ModelWorkProfile
                    {
                        ModelName = LocalModelCatalogService.ManagedDefaultModelDisplay,
                        Enabled = true,
                        RecommendedEffort = "medium",
                        SpeedTier = "medium",
                        PrimaryRoles = roles
                    };
                    CloudRecommendationService.EnsureModelProfileEnriched(ag.Id, managed, 0, ag.Category);
                    snap.ModelProfiles.Add(managed);
                }
                else
                {
                    foreach (var (mName, tier) in new[] { (snap.HighTierModel, "high"), (snap.MediumTierModel, "medium"), (snap.LowTierModel, "low") })
                    {
                        if (string.IsNullOrWhiteSpace(mName) || LocalModelCatalogService.IsLegacyBogusModelName(mName)) continue;
                        if (snap.ModelProfiles.Any(x => string.Equals(x.ModelName, mName, StringComparison.OrdinalIgnoreCase))) continue;
                        var spec = CloudRecommendationService.InferCloudModelSpec(ag.Id, mName);
                        var roles = snap.PrimaryWorkRoles.Count > 0
                            ? snap.PrimaryWorkRoles.Where(r => spec.RecommendedRoles.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList()
                            : new List<string>(spec.RecommendedRoles);
                        if (roles.Count == 0 && snap.PrimaryWorkRoles.Count > 0) roles = new List<string>(snap.PrimaryWorkRoles);
                        snap.ModelProfiles.Add(new ModelWorkProfile
                        {
                            ModelName = mName,
                            Enabled = true,
                            RecommendedEffort = tier,
                            SpeedTier = tier == "low" ? "fast" : tier,
                            PrimaryRoles = roles,
                            SpecialtyNote = spec.SpecialtyNote,
                            ContextWindowTokens = spec.ContextWindowTokens,
                            MaxOutputTokens = spec.MaxOutputTokens
                        });
                    }
                    if (snap.ModelProfiles.Count == 0 && ag.ModelProfiles != null && ag.ModelProfiles.Count > 0)
                        snap.ModelProfiles = ag.ModelProfiles.Select(m => m.Clone()).ToList();
                }
            }
            for (int i = 0; i < snap.ModelProfiles.Count; i++)
                CloudRecommendationService.EnsureModelProfileEnriched(ag.Id, snap.ModelProfiles[i], i, ag.Category);

            return snap;
        }

        static void ApplyCloudPresetToSnapshot(RegistryService r, AgentRosterEntry ag, TeamAgentSnapshot snap)
        {
            var presets = CloudRecommendationService.BuildCloudPresetsForAgent(r, ag, refreshLocalModels: false);
            var rec = presets.FirstOrDefault(p => p.PresetId == "cloud_balanced") ?? presets.FirstOrDefault();
            if (rec == null) return;
            var clone = rec.Clone();
            snap.ActivePresetId = clone.PresetId;
            snap.PreferredEffortTier = LocalModelCatalogService.SupportsModelSelection(ag.Id) ? (clone.PreferredEffortTier ?? "auto") : "auto";
            snap.Permissions = (clone.Permissions ?? new AgentPermissions()).Clone();
            snap.RuntimeParams = (clone.RuntimeParams ?? new AgentRuntimeParams()).Clone();
            if (clone.ModelProfiles != null && clone.ModelProfiles.Count > 0)
                snap.ModelProfiles = clone.ModelProfiles.Select(m => m.Clone()).ToList();
            CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, ag);
            if (clone.TagScores != null && clone.TagScores.Count > 0)
            {
                foreach (var kv in clone.TagScores)
                    if (!snap.TagScores.ContainsKey(kv.Key)) snap.TagScores[kv.Key] = kv.Value;
            }
        }

        void FlushHeaderFieldsToSelectedProfile()
        {
            if (suppressSync || selectedProfile == null) return;
            var nm = nameBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(nm) && !string.Equals(nm, selectedProfile.Name, StringComparison.Ordinal))
                reg.RenameTeamProfile(selectedProfile.Id, nm, descBox.Text);
            else
                selectedProfile.Description = descBox.Text.Trim();

            if (masterCombo.SelectedItem is ComboBoxItem mci && mci.Tag is string mid)
                selectedProfile.MasterPlannerId = mid;
            if (fallbackCombo.SelectedItem is ComboBoxItem fci && fci.Tag is string fid)
                selectedProfile.FallbackBuilderId = fid;
            if (reworkCombo.SelectedItem is ComboBoxItem rci && rci.Tag is int rw)
                selectedProfile.MaxReworkRounds = rw;
            selectedProfile.PreferNativeModelsWhenTied = chkPreferNative.IsChecked == true;
            selectedProfile.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 保存前把所有快照的 PrimaryWorkRoles / TagScores 与已启用模型能力并集双向同步
            foreach (var ag in GetConfigurableInstalledAgents())
            {
                var sn = selectedProfile.AgentSnapshots?.FirstOrDefault(s =>
                    string.Equals(s.AgentId, ag.Id, StringComparison.OrdinalIgnoreCase));
                if (sn != null)
                    CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(sn, ag);
            }
        }

        void RenderLeftProfileList()
        {
            profilesListStack.Children.Clear();
            var list = reg.UserConfig.TeamProfiles;
            countBadge.Text = $"{list.Count} 套";
            btnDelProfile.IsEnabled = list.Count > 1;
            var installedIds = new HashSet<string>(GetConfigurableInstalledAgents().Select(a => a.Id), StringComparer.OrdinalIgnoreCase);

            foreach (var prof in list)
            {
                bool isSel = string.Equals(prof.Id, selectedProfile.Id, StringComparison.OrdinalIgnoreCase);
                bool isActive = string.Equals(prof.Id, reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase);
                var enSnaps = prof.AgentSnapshots?.Where(s => s.Enabled && installedIds.Contains(s.AgentId)).ToList() ?? new List<TeamAgentSnapshot>();
                int enCount = enSnaps.Count;
                int modelCount = enSnaps.Sum(s => s.ModelProfiles?.Count(m => m.Enabled) ?? 0);

                var itemBorder = new Border
                {
                    Background = BrushHex(isSel ? "#17263C" : "Transparent"),
                    BorderBrush = BrushHex(isSel ? "#38BDF8" : "Transparent"),
                    BorderThickness = new Thickness(isSel ? 2 : 0, 0, 0, 0),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(9, 8, 8, 8),
                    Margin = new Thickness(0, 0, 0, 4),
                    Cursor = Cursors.Hand
                };
                var sp = new StackPanel();
                var topLine = new DockPanel();
                if (isActive)
                {
                    var dot = new TextBlock
                    {
                        Text = "● 使用中",
                        FontSize = 9.8,
                        Foreground = BrushHex("#34D399"),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(4, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    DockPanel.SetDock(dot, Dock.Right);
                    topLine.Children.Add(dot);
                }
                topLine.Children.Add(new TextBlock
                {
                    Text = prof.Name,
                    FontSize = 12,
                    FontWeight = isSel ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = BrushHex(isSel ? "#F8FAFC" : "#CBD5E1"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                sp.Children.Add(topLine);
                sp.Children.Add(new TextBlock
                {
                    Text = $"{enCount} 个 AI · {modelCount} 个模型",
                    FontSize = 10.5,
                    Foreground = BrushHex("#64748B"),
                    Margin = new Thickness(0, 3, 0, 0)
                });
                itemBorder.Child = sp;

                var captured = prof;
                itemBorder.MouseLeftButtonUp += (_, _) =>
                {
                    if (string.Equals(captured.Id, selectedProfile.Id, StringComparison.OrdinalIgnoreCase)) return;
                    FlushHeaderFieldsToSelectedProfile();
                    selectedProfile = captured;
                    showAddModelBar = false;
                    LoadSelectedProfileIntoRightEditor();
                    RenderLeftProfileList();
                };
                profilesListStack.Children.Add(itemBorder);
            }
        }

        void RebuildStrategyCombos(TeamConfigProfile prof)
        {
            var installed = GetConfigurableInstalledAgents();
            var snaps = installed.ToDictionary(a => a.Id, a => EnsureSnapshotForAgent(prof, a), StringComparer.OrdinalIgnoreCase);
            var enabledAgents = installed.Where(a => snaps[a.Id].Enabled).ToList();
            if (enabledAgents.Count == 0 && installed.Count > 0)
            {
                snaps[installed[0].Id].Enabled = true;
                enabledAgents.Add(installed[0]);
            }

            bool prevSuppress = suppressSync;
            suppressSync = true;
            try
            {
                masterCombo.Items.Clear();
                masterCombo.Items.Add(new ComboBoxItem { Content = "自动选最优", Tag = "auto" });
                fallbackCombo.Items.Clear();
                fallbackCombo.Items.Add(new ComboBoxItem { Content = "自动顺延", Tag = "" });

                foreach (var ag in enabledAgents)
                {
                    masterCombo.Items.Add(new ComboBoxItem { Content = ag.Name, Tag = ag.Id });
                    fallbackCombo.Items.Add(new ComboBoxItem { Content = ag.Name, Tag = ag.Id });
                }

                string targetMaster = string.IsNullOrWhiteSpace(prof.MasterPlannerId) ? "auto" : prof.MasterPlannerId;
                masterCombo.SelectedItem = masterCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, targetMaster, StringComparison.OrdinalIgnoreCase))
                    ?? masterCombo.Items[0];

                string targetFb = prof.FallbackBuilderId ?? "";
                fallbackCombo.SelectedItem = fallbackCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, targetFb, StringComparison.OrdinalIgnoreCase))
                    ?? fallbackCombo.Items[0];

                int targetRw = Math.Clamp(prof.MaxReworkRounds, 0, 3);
                reworkCombo.SelectedItem = reworkCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag is int v && v == targetRw)
                    ?? reworkCombo.Items[1];

                chkPreferNative.IsChecked = prof.PreferNativeModelsWhenTied;
            }
            finally
            {
                suppressSync = prevSuppress;
            }
        }

        void RenderAgentWorkbench(TeamConfigProfile prof)
        {
            participantsRowDock.Children.Clear();
            workbenchHost.Children.Clear();

            var installed = GetConfigurableInstalledAgents();
            var snapsById = installed.ToDictionary(a => a.Id, a => EnsureSnapshotForAgent(prof, a), StringComparer.OrdinalIgnoreCase);
            var enabledAgents = installed.Where(a => snapsById[a.Id].Enabled).ToList();
            if (enabledAgents.Count == 0 && installed.Count > 0)
            {
                snapsById[installed[0].Id].Enabled = true;
                enabledAgents.Add(installed[0]);
            }
            var disabledAgents = installed.Where(a => !snapsById[a.Id].Enabled).ToList();

            if (!enabledAgents.Any(a => string.Equals(a.Id, selectedAgentId, StringComparison.OrdinalIgnoreCase)))
                selectedAgentId = enabledAgents.FirstOrDefault()?.Id ?? "";

            // 1) 极简参战 AI 图标栏：仅展示已启用的 AI 图标（鼠标悬停显示工具名称）；未启用 AI 收入右侧「＋」菜单，绝不铺开占地
            if (disabledAgents.Count > 0)
            {
                var btnAddAgentMenu = new Button
                {
                    Content = "＋",
                    Style = actionStyle,
                    Background = Brushes.Transparent,
                    BorderBrush = BrushHex("#243044"),
                    Foreground = BrushHex("#64748B"),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Width = 30,
                    Height = 30,
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "启用其他已安装 AI 加入本方案"
                };
                ToolTipService.SetInitialShowDelay(btnAddAgentMenu, 120);
                btnAddAgentMenu.Click += (_, _) =>
                {
                    var addMenu = new ContextMenu
                    {
                        Background = BrushHex("#111827"),
                        BorderBrush = BrushHex("#243248"),
                        Foreground = BrushHex("#E2E8F0")
                    };
                    foreach (var dag in disabledAgents)
                    {
                        var capAg = dag;
                        var mi = new MenuItem
                        {
                            Header = $"＋ 启用 {dag.Name}",
                            Foreground = BrushHex("#E2E8F0")
                        };
                        mi.Click += (_, _) =>
                        {
                            snapsById[capAg.Id].Enabled = true;
                            CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snapsById[capAg.Id], capAg);
                            selectedAgentId = capAg.Id;
                            showAddModelBar = false;
                            anyChanged = true;
                            RebuildStrategyCombos(prof);
                            RenderLeftProfileList();
                            RenderAgentWorkbench(prof);
                        };
                        addMenu.Items.Add(mi);
                    }
                    addMenu.PlacementTarget = btnAddAgentMenu;
                    addMenu.IsOpen = true;
                };
                DockPanel.SetDock(btnAddAgentMenu, Dock.Right);
                participantsRowDock.Children.Add(btnAddAgentMenu);
            }

            var enabledTabsWrap = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var ag in enabledAgents)
            {
                var agSnap = snapsById[ag.Id];
                bool isFocused = string.Equals(ag.Id, selectedAgentId, StringComparison.OrdinalIgnoreCase);
                int mCnt = agSnap.ModelProfiles.Count(m => m.Enabled);
                string countTip = LocalModelCatalogService.SupportsModelSelection(ag.Id) ? $"{mCnt} 个启用模型" : "官方默认托管";

                var pillBtn = new Button
                {
                    Width = 36,
                    Height = 34,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = BrushHex(isFocused ? "#182B46" : "#0F1622"),
                    BorderBrush = BrushHex(isFocused ? "#38BDF8" : "#1E293B"),
                    BorderThickness = new Thickness(isFocused ? 1.5 : 1),
                    Style = actionStyle,
                    ToolTip = $"{ag.Name}（{countTip}）"
                };
                ToolTipService.SetInitialShowDelay(pillBtn, 120);

                var av = OfficialBrandIconService.CreateCircularAvatarVisual(ag.Id, 22, ag, isFocused ? "#38BDF8" : "#475569", isFocused ? 1.4 : 1.0);
                av.HorizontalAlignment = HorizontalAlignment.Center;
                av.VerticalAlignment = VerticalAlignment.Center;
                pillBtn.Content = av;
                var capId = ag.Id;
                pillBtn.Click += (_, _) =>
                {
                    selectedAgentId = capId;
                    modelCatalogFilter = "all";
                    showAddModelBar = false;
                    RenderAgentWorkbench(prof);
                };
                enabledTabsWrap.Children.Add(pillBtn);
            }
            participantsRowDock.Children.Add(enabledTabsWrap);

            // 2) 当前选中 AI 工作台（去掉外层冗余盒子，只留干净单层工作面板）
            var activeAg = enabledAgents.FirstOrDefault(a => string.Equals(a.Id, selectedAgentId, StringComparison.OrdinalIgnoreCase));
            if (activeAg == null) return;
            var snap = snapsById[activeAg.Id];
            bool canSelectModels = LocalModelCatalogService.SupportsModelSelection(activeAg.Id);
            bool allowCustomModel = LocalModelCatalogService.SupportsCustomModelInput(activeAg.Id);
            var catalog = LocalModelCatalogService.GetClassifiedCatalogForAgent(activeAg, forceRefresh: false);
            int nativeAvailCnt = catalog.Count(c => c.Ownership.IsNativeOwn);
            int thirdAvailCnt = catalog.Count(c => !c.Ownership.IsNativeOwn);

            var wbContainer = new Border
            {
                Background = BrushHex("#0E1520"),
                BorderBrush = BrushHex("#1E293B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var wbDock = new DockPanel();

            // 工作台精简单行顶栏（合并原来的三行工具栏为清爽一行！）
            var wbBar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(wbBar, Dock.Top);

            var wbActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (canSelectModels && activeAgentSubTab == 0)
            {
                var btnToggleAdd = new Button
                {
                    Content = showAddModelBar ? "收起添加栏" : "＋ 添加模型",
                    Style = showAddModelBar ? actionStyle : primaryStyle,
                    FontSize = 11.2,
                    Padding = new Thickness(9, 3, 9, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    ToolTip = "从本机客户端可选模型库筛选（自有 / 第三方）并添加新模型"
                };
                btnToggleAdd.Click += (_, _) => { showAddModelBar = !showAddModelBar; RenderAgentWorkbench(prof); };
                wbActions.Children.Add(btnToggleAdd);
            }

            var btnRefreshCatalog = MakeGhostBtn("刷新模型库", "#94A3B8", 11);
            btnRefreshCatalog.ToolTip = "立即重新扫描本机所有 AI 工具的最新可选模型列表";
            LongActionTracker.ApplyBusyIfNeeded(btnRefreshCatalog, LongActionTracker.ModelCatalogScan, "刷新模型库");
            btnRefreshCatalog.Click += async (_, _) =>
            {
                if (!LongActionTracker.TryBegin(LongActionTracker.ModelCatalogScan, "扫描中"))
                {
                    footerHint.Text = LongActionTracker.DuplicateClickHint(LongActionTracker.ModelCatalogScan);
                    footerHint.Foreground = BrushHex("#FBBF24");
                    return;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                LongActionTracker.ShowBusy(btnRefreshCatalog);
                try
                {
                    await System.Threading.Tasks.Task.Run(() =>
                        LocalModelCatalogService.SyncAllAgentsWithLocalClientModels(reg.UserConfig.Agents, invalidateCache: true));
                }
                catch (Exception ex)
                {
                    LongActionTracker.EndAndRestore(LongActionTracker.ModelCatalogScan);
                    footerHint.Text = $"⚠ 刷新模型库失败：{ex.Message}（耗时 {(int)sw.Elapsed.TotalSeconds}s）";
                    footerHint.Foreground = BrushHex("#FBBF24");
                    return;
                }
                LongActionTracker.EndAndRestore(LongActionTracker.ModelCatalogScan);
                RenderAgentWorkbench(prof);
                footerHint.Text = $"✅ 已重新扫描并同步全部 AI 工具的最新可选模型（耗时 {(int)sw.Elapsed.TotalSeconds}s）。";
                footerHint.Foreground = BrushHex("#7DD3FC");
            };
            var btnRecAgent = MakeGhostBtn("推荐配置", "#7DD3FC", 11);
            btnRecAgent.Click += (_, _) =>
            {
                ApplyCloudPresetToSnapshot(reg, activeAg, snap);
                anyChanged = true;
                RenderLeftProfileList();
                RenderAgentWorkbench(prof);
                footerHint.Text = $"已为「{activeAg.Name}」填入推荐模型规则与能力强度。";
                footerHint.Foreground = BrushHex("#6EE7B7");
            };
            var btnDisableAgent = MakeGhostBtn("停用此AI", "#F87171", 11);
            btnDisableAgent.IsEnabled = enabledAgents.Count > 1;
            btnDisableAgent.Opacity = enabledAgents.Count > 1 ? 1.0 : 0.4;
            btnDisableAgent.Click += (_, _) =>
            {
                if (enabledAgents.Count <= 1) return;
                snap.Enabled = false;
                if (string.Equals(prof.MasterPlannerId, activeAg.Id, StringComparison.OrdinalIgnoreCase)) prof.MasterPlannerId = "auto";
                if (string.Equals(prof.FallbackBuilderId, activeAg.Id, StringComparison.OrdinalIgnoreCase)) prof.FallbackBuilderId = "";
                anyChanged = true;
                selectedAgentId = "";
                RebuildStrategyCombos(prof);
                RenderLeftProfileList();
                RenderAgentWorkbench(prof);
            };

            wbActions.Children.Add(btnRefreshCatalog);
            wbActions.Children.Add(btnRecAgent);
            wbActions.Children.Add(btnDisableAgent);
            DockPanel.SetDock(wbActions, Dock.Right);
            wbBar.Children.Add(wbActions);

            var wbLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            // 简洁分段切换（模型与能力 / 权限与通道）
            var segBorder = new Border
            {
                Background = BrushHex("#141E2E"),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 12, 0)
            };
            var segPanel = new StackPanel { Orientation = Orientation.Horizontal };
            Button MakeSegTab(string txt, int targetSub)
            {
                bool on = activeAgentSubTab == targetSub;
                var b = new Button
                {
                    Content = txt,
                    Background = BrushHex(on ? "#243B5C" : "Transparent"),
                    Foreground = BrushHex(on ? "#FFFFFF" : "#94A3B8"),
                    BorderThickness = new Thickness(0),
                    FontSize = 11.2,
                    FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                    Padding = new Thickness(9, 3, 9, 3),
                    Cursor = Cursors.Hand,
                    Style = actionStyle
                };
                b.Click += (_, _) => { activeAgentSubTab = targetSub; RenderAgentWorkbench(prof); };
                return b;
            }
            segPanel.Children.Add(MakeSegTab($"模型配置 ({snap.ModelProfiles.Count})", 0));
            segPanel.Children.Add(MakeSegTab("权限与通道", 1));
            segBorder.Child = segPanel;
            wbLeft.Children.Add(segBorder);

            wbLeft.Children.Add(new TextBlock
            {
                Text = canSelectModels
                    ? $"可选 {catalog.Count} 个模型（自有 {nativeAvailCnt} · 第三方 {thirdAvailCnt}）"
                    : "官方默认托管模型（防风控锁模 · 仅配规则与能力）",
                FontSize = 11,
                Foreground = BrushHex(canSelectModels ? "#64748B" : "#D97706"),
                VerticalAlignment = VerticalAlignment.Center
            });
            wbBar.Children.Add(wbLeft);
            wbDock.Children.Add(wbBar);

            // ==================== 子视图 0：模型与能力 ====================
            if (activeAgentSubTab == 0)
            {
                // 按需展开的「添加模型」极简条（平时隐藏，让工作台一眼聚焦现有模型！）
                if (canSelectModels && showAddModelBar)
                {
                    var addBar = new Border
                    {
                        Background = BrushHex("#131D2D"),
                        BorderBrush = BrushHex("#243B5C"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 7, 10, 7),
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    DockPanel.SetDock(addBar, Dock.Top);
                    var addStack = new StackPanel();

                    var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                    Button MakeFilterLink(string key, string caption)
                    {
                        bool on = string.Equals(modelCatalogFilter, key, StringComparison.OrdinalIgnoreCase);
                        var fb = new Button
                        {
                            Content = caption,
                            Background = BrushHex(on ? "#1E3A5F" : "Transparent"),
                            BorderBrush = BrushHex(on ? "#38BDF8" : "#243248"),
                            Foreground = BrushHex(on ? "#FFFFFF" : "#94A3B8"),
                            FontSize = 10.8,
                            Padding = new Thickness(8, 2, 8, 2),
                            Margin = new Thickness(0, 0, 6, 0),
                            Style = actionStyle
                        };
                        fb.Click += (_, _) => { modelCatalogFilter = key; RenderAgentWorkbench(prof); };
                        return fb;
                    }
                    filterRow.Children.Add(MakeFilterLink("all", $"全部 ({catalog.Count})"));
                    filterRow.Children.Add(MakeFilterLink("native", $"自有原生 ({nativeAvailCnt})"));
                    filterRow.Children.Add(MakeFilterLink("third_party", $"第三方 ({thirdAvailCnt})"));
                    addStack.Children.Add(filterRow);

                    var addRow = new Grid();
                    addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                    addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var filteredCatalog = catalog.Where(c => modelCatalogFilter switch
                    {
                        "native" => c.Ownership.IsNativeOwn,
                        "third_party" => !c.Ownership.IsNativeOwn,
                        _ => true
                    }).ToList();
                    if (filteredCatalog.Count == 0) filteredCatalog = catalog;

                    var addModelCombo = new ComboBox { Height = 26, FontSize = 11.5 };
                    foreach (var det in filteredCatalog)
                    {
                        bool used = snap.ModelProfiles.Any(m => string.Equals(m.ModelName, det.ModelName, StringComparison.OrdinalIgnoreCase));
                        var ownTag = det.Ownership.IsNativeOwn ? $"自有·{det.Ownership.VendorName}" : $"三方·{det.Ownership.VendorName}";
                        addModelCombo.Items.Add(new ComboBoxItem
                        {
                            Content = $"{(used ? "[已添加] " : "")}[{ownTag}]  {det.ModelName}",
                            Tag = det.ModelName,
                            ToolTip = $"{det.ModelName}\n{det.SpecialtyHint}"
                        });
                    }
                    if (allowCustomModel)
                        addModelCombo.Items.Add(new ComboBoxItem { Content = "＋ 手动输入其他模型名...", Tag = "__custom_inline__" });
                    if (addModelCombo.Items.Count > 0)
                    {
                        int firstUnused = filteredCatalog.FindIndex(c => !snap.ModelProfiles.Any(m => string.Equals(m.ModelName, c.ModelName, StringComparison.OrdinalIgnoreCase)));
                        addModelCombo.SelectedIndex = firstUnused >= 0 ? firstUnused : 0;
                    }

                    var customInlineWrap = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
                    customInlineWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    customInlineWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                    customInlineWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                    var customModelBox = new TextBox { Height = 25, FontSize = 11.5, Padding = new Thickness(7, 2, 7, 2) };
                    var customOwnCombo = new ComboBox { Height = 25, FontSize = 11 };
                    customOwnCombo.Items.Add(new ComboBoxItem { Content = "标为自有", Tag = "native" });
                    customOwnCombo.Items.Add(new ComboBoxItem { Content = "标为第三方", Tag = "third_party" });
                    customOwnCombo.SelectedIndex = 0;
                    Grid.SetColumn(customModelBox, 0);
                    Grid.SetColumn(customOwnCombo, 2);
                    customInlineWrap.Children.Add(customModelBox);
                    customInlineWrap.Children.Add(customOwnCombo);

                    addModelCombo.SelectionChanged += (_, _) =>
                    {
                        var selTag = (addModelCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                        customInlineWrap.Visibility = selTag == "__custom_inline__" ? Visibility.Visible : Visibility.Collapsed;
                        if (selTag == "__custom_inline__") customModelBox.Focus();
                    };

                    var btnConfirmAdd = new Button { Content = "确认添加", Style = primaryStyle, Padding = new Thickness(12, 3, 12, 3), Height = 26, FontSize = 11.5 };
                    btnConfirmAdd.Click += (_, _) =>
                    {
                        var tag = (addModelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                        string mn = tag == "__custom_inline__" ? customModelBox.Text.Trim() : tag;
                        if (string.IsNullOrWhiteSpace(mn)) return;

                        if (tag == "__custom_inline__")
                        {
                            activeAg.AvailableModels ??= new List<string>();
                            if (!activeAg.AvailableModels.Contains(mn, StringComparer.OrdinalIgnoreCase))
                                activeAg.AvailableModels.Add(mn);
                        }
                        var spec = CloudRecommendationService.InferCloudModelSpec(activeAg.Id, mn);
                        var scores = CloudRecommendationService.InferModelCapabilityScores(activeAg.Id, mn);
                        var (pol, diff) = CloudRecommendationService.InferModelUsageRules(activeAg.Id, mn, spec.Tier, snap.ModelProfiles.Count);
                        var own = tag == "__custom_inline__"
                            ? LocalModelCatalogService.ClassifyModelOwnership(activeAg.Id, mn, (customOwnCombo.SelectedItem as ComboBoxItem)?.Tag as string, null, activeAg.Category)
                            : LocalModelCatalogService.ClassifyModelOwnership(activeAg.Id, mn, null, null, activeAg.Category);

                        snap.ModelProfiles.Add(new ModelWorkProfile
                        {
                            ModelName = mn,
                            Enabled = true,
                            OwnershipKind = own.OwnershipKind,
                            VendorName = own.VendorName,
                            UsagePolicy = pol,
                            TaskDifficultyRule = diff,
                            PrimaryRoles = scores.Keys.ToList(),
                            CapabilityScores = scores,
                            RecommendedEffort = spec.Tier,
                            SpeedTier = spec.SpeedTier,
                            SpecialtyNote = spec.SpecialtyNote,
                            ContextWindowTokens = spec.ContextWindowTokens,
                            MaxOutputTokens = spec.MaxOutputTokens
                        });
                        showAddModelBar = false;
                        CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, activeAg);
                        anyChanged = true;
                        RenderLeftProfileList();
                        RenderAgentWorkbench(prof);
                    };

                    Grid.SetColumn(addModelCombo, 0);
                    Grid.SetColumn(btnConfirmAdd, 2);
                    addRow.Children.Add(addModelCombo);
                    addRow.Children.Add(btnConfirmAdd);
                    addStack.Children.Add(addRow);
                    addStack.Children.Add(customInlineWrap);
                    addBar.Child = addStack;
                    wbDock.Children.Add(addBar);
                }

                // 模型卡片列表（支持上下任意拖动排序，拖动条目实时跟随鼠标 + 实时让位预览）
                var modelsListStack = new StackPanel();
                var modelsScrollViewer = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = modelsListStack
                };

                var cardEntries = new List<(Border Card, TranslateTransform Transform, int Index)>();
                int dragSourceIdx = -1;
                int dragTargetIdx = -1;
                bool isDraggingCard = false;
                bool isInMouseMove = false;
                double dragStartMouseY = 0;
                double dragStartScrollY = 0;
                double dragSlotSpan = 0;
                double[] origCardTops = Array.Empty<double>();
                double[] origCardHeights = Array.Empty<double>();

                bool IsInteractiveControl(DependencyObject? src)
                {
                    while (src != null)
                    {
                        if (src is System.Windows.Controls.Primitives.ButtonBase or
                            System.Windows.Controls.Primitives.TextBoxBase or
                            ComboBox or ComboBoxItem or
                            System.Windows.Controls.Primitives.Thumb or
                            System.Windows.Controls.Primitives.ScrollBar)
                            return true;

                        if (src is Visual || src is System.Windows.Media.Media3D.Visual3D)
                            src = VisualTreeHelper.GetParent(src);
                        else
                            src = LogicalTreeHelper.GetParent(src);
                    }
                    return false;
                }

                void FinishCardDrag(bool commit)
                {
                    if (dragSourceIdx < 0) return;
                    int srcIdx = dragSourceIdx;
                    int dstIdx = dragTargetIdx;
                    bool didDrag = isDraggingCard;
                    var dragCard = srcIdx >= 0 && srcIdx < cardEntries.Count ? cardEntries[srcIdx].Card : null;

                    dragSourceIdx = -1;
                    dragTargetIdx = -1;
                    isDraggingCard = false;
                    origCardTops = Array.Empty<double>();
                    origCardHeights = Array.Empty<double>();

                    if (dragCard != null)
                    {
                        if (dragCard.IsMouseCaptured) dragCard.ReleaseMouseCapture();
                        Panel.SetZIndex(dragCard, 0);
                        dragCard.Opacity = 1.0;
                        dragCard.BorderThickness = new Thickness(1);
                    }
                    foreach (var ce in cardEntries)
                    {
                        ce.Transform.Y = 0;
                    }

                    if (commit && didDrag && dstIdx >= 0 && dstIdx != srcIdx &&
                        srcIdx < snap.ModelProfiles.Count && dstIdx < snap.ModelProfiles.Count)
                    {
                        // 同步迁移各卡片的展开状态，避免拖完后展开项错位
                        var wasExpandedMap = new Dictionary<ModelWorkProfile, bool>();
                        for (int k = 0; k < snap.ModelProfiles.Count; k++)
                        {
                            wasExpandedMap[snap.ModelProfiles[k]] = expandedModelKeys.Contains($"{activeAg.Id}::{k}");
                        }

                        var moved = snap.ModelProfiles[srcIdx];
                        snap.ModelProfiles.RemoveAt(srcIdx);
                        snap.ModelProfiles.Insert(dstIdx, moved);

                        expandedModelKeys.RemoveWhere(k => k.StartsWith($"{activeAg.Id}::", StringComparison.OrdinalIgnoreCase));
                        for (int k = 0; k < snap.ModelProfiles.Count; k++)
                        {
                            if (wasExpandedMap.TryGetValue(snap.ModelProfiles[k], out var exp) && exp)
                                expandedModelKeys.Add($"{activeAg.Id}::{k}");
                        }

                        CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, activeAg);
                        anyChanged = true;
                        footerHint.Text = $"已将「{moved.ModelName}」调整至第 {dstIdx + 1} 位。";
                        footerHint.Foreground = BrushHex("#7DD3FC");
                        // 延后到当前鼠标事件派发完毕后再重建列表，防止同步销毁事件源视觉树
                        win.Dispatcher.BeginInvoke(() => RenderAgentWorkbench(prof));
                    }
                    else if (didDrag)
                    {
                        win.Dispatcher.BeginInvoke(() => RenderAgentWorkbench(prof));
                    }
                }

                for (int slotIdx = 0; slotIdx < snap.ModelProfiles.Count; slotIdx++)
                {
                    var mp = snap.ModelProfiles[slotIdx];
                    int myIdx = slotIdx;
                    string expandKey = $"{activeAg.Id}::{myIdx}";
                    bool isExpanded = expandedModelKeys.Contains(expandKey);

                    var own = LocalModelCatalogService.ClassifyModelOwnership(activeAg.Id, mp.ModelName, mp.OwnershipKind, mp.VendorName, activeAg.Category);
                    var cardTransform = new TranslateTransform();
                    var cardBorder = new Border
                    {
                        Background = BrushHex(mp.Enabled ? "#131C2A" : "#0D131D"),
                        BorderBrush = BrushHex(isExpanded ? "#2A456D" : "#1C2738"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(7),
                        Padding = new Thickness(12, 9, 12, 9),
                        Margin = new Thickness(0, 0, 0, 8),
                        RenderTransform = cardTransform
                    };
                    cardEntries.Add((cardBorder, cardTransform, myIdx));

                    if (snap.ModelProfiles.Count > 1)
                    {
                        cardBorder.PreviewMouseLeftButtonDown += (_, e) =>
                        {
                            if (IsInteractiveControl(e.OriginalSource as DependencyObject)) return;
                            dragSourceIdx = myIdx;
                            dragTargetIdx = myIdx;
                            isDraggingCard = false;
                            dragStartMouseY = e.GetPosition(modelsScrollViewer).Y;
                            dragStartScrollY = modelsScrollViewer.VerticalOffset;
                        };

                        cardBorder.MouseMove += (_, e) =>
                        {
                            if (isInMouseMove || dragSourceIdx != myIdx) return;
                            isInMouseMove = true;
                            try
                            {
                                if (e.LeftButton != MouseButtonState.Pressed)
                                {
                                    FinishCardDrag(commit: true);
                                    return;
                                }

                                var curMouseY = e.GetPosition(modelsScrollViewer).Y;
                                if (!isDraggingCard)
                                {
                                    if (Math.Abs(curMouseY - dragStartMouseY) < 4) return;

                                    // 必须先初始化完所有原始位置数组，再调用 CaptureMouse()！
                                    // 因为 CaptureMouse() 会同步触发 MouseDevice.Synchronize() -> 重入 MouseMove
                                    int cnt = cardEntries.Count;
                                    if (myIdx < 0 || myIdx >= cnt) return;
                                    var tops = new double[cnt];
                                    var heights = new double[cnt];
                                    for (int k = 0; k < cnt; k++)
                                    {
                                        var pt = cardEntries[k].Card.TranslatePoint(new Point(0, 0), modelsListStack);
                                        tops[k] = pt.Y;
                                        heights[k] = Math.Max(40.0, cardEntries[k].Card.ActualHeight);
                                    }
                                    origCardTops = tops;
                                    origCardHeights = heights;
                                    dragSlotSpan = heights[myIdx] + 8.0;
                                    isDraggingCard = true;

                                    Panel.SetZIndex(cardBorder, 100);
                                    cardBorder.Opacity = 0.94;
                                    cardBorder.Background = BrushHex("#18253A");
                                    cardBorder.BorderBrush = BrushHex("#38BDF8");
                                    cardBorder.BorderThickness = new Thickness(1.5);
                                    cardBorder.CaptureMouse();
                                }

                                if (myIdx < 0 || myIdx >= origCardTops.Length || myIdx >= origCardHeights.Length)
                                    return;

                                // 靠近列表上下边缘 36px 时自动平滑滚动，方便跨长列表拖动
                                if (curMouseY < 36 && modelsScrollViewer.VerticalOffset > 0)
                                    modelsScrollViewer.ScrollToVerticalOffset(Math.Max(0, modelsScrollViewer.VerticalOffset - 10));
                                else if (curMouseY > modelsScrollViewer.ActualHeight - 36 && modelsScrollViewer.VerticalOffset < modelsScrollViewer.ScrollableHeight)
                                    modelsScrollViewer.ScrollToVerticalOffset(Math.Min(modelsScrollViewer.ScrollableHeight, modelsScrollViewer.VerticalOffset + 10));

                                double deltaY = (curMouseY - dragStartMouseY) + (modelsScrollViewer.VerticalOffset - dragStartScrollY);
                                cardTransform.Y = deltaY;

                                // 根据拖动卡片中心点实时计算目标顺位并让其他条目平滑让位
                                double dragCenterY = origCardTops[myIdx] + deltaY + origCardHeights[myIdx] / 2.0;
                                int newTarget = myIdx;
                                int limit = Math.Min(cardEntries.Count, origCardTops.Length);
                                if (deltaY > 0)
                                {
                                    for (int k = myIdx + 1; k < limit; k++)
                                    {
                                        double midK = origCardTops[k] + origCardHeights[k] / 2.0;
                                        if (dragCenterY > midK) newTarget = k;
                                    }
                                }
                                else if (deltaY < 0)
                                {
                                    for (int k = myIdx - 1; k >= 0; k--)
                                    {
                                        double midK = origCardTops[k] + origCardHeights[k] / 2.0;
                                        if (dragCenterY < midK) newTarget = k;
                                    }
                                }
                                dragTargetIdx = newTarget;

                                for (int k = 0; k < limit; k++)
                                {
                                    if (k == myIdx) continue;
                                    if (newTarget > myIdx && k > myIdx && k <= newTarget)
                                        cardEntries[k].Transform.Y = -dragSlotSpan;
                                    else if (newTarget < myIdx && k >= newTarget && k < myIdx)
                                        cardEntries[k].Transform.Y = dragSlotSpan;
                                    else
                                        cardEntries[k].Transform.Y = 0;
                                }
                            }
                            finally
                            {
                                isInMouseMove = false;
                            }
                        };

                        cardBorder.MouseLeftButtonUp += (_, _) =>
                        {
                            if (dragSourceIdx == myIdx) FinishCardDrag(commit: true);
                        };
                        cardBorder.LostMouseCapture += (_, _) =>
                        {
                            if (dragSourceIdx == myIdx && isDraggingCard) FinishCardDrag(commit: false);
                        };
                    }

                    var cardStack = new StackPanel();

                    // Row 1: 拖拽抓手 + 顺位 + 启用 + 模型选择 + 极简归属标签 + 展开/收起 + 移除
                    var r1 = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                    r1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0: 拖拽抓手 & #
                    r1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1: 启用
                    r1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2: 模型
                    r1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                    r1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 4: 归属
                    r1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                    r1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 6: 操作

                    var orderWrap = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0),
                        Background = System.Windows.Media.Brushes.Transparent,
                        Cursor = snap.ModelProfiles.Count > 1 ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Arrow,
                        ToolTip = snap.ModelProfiles.Count > 1 ? "按住上下拖动调整优先级顺序" : null
                    };
                    if (snap.ModelProfiles.Count > 1)
                    {
                        orderWrap.Children.Add(new TextBlock
                        {
                            Text = "⋮⋮",
                            FontSize = 12,
                            FontWeight = FontWeights.Bold,
                            Foreground = BrushHex("#64748B"),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 5, 0)
                        });
                    }
                    orderWrap.Children.Add(new TextBlock
                    {
                        Text = $"#{myIdx + 1}",
                        FontSize = 11.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = BrushHex("#38BDF8"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    Grid.SetColumn(orderWrap, 0);

                    var cbModelEn = new CheckBox
                    {
                        IsChecked = mp.Enabled,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0),
                        IsEnabled = !mp.Enabled || snap.ModelProfiles.Count(x => x.Enabled) > 1,
                        ToolTip = "启用或暂禁此模型"
                    };
                    cbModelEn.Click += (_, _) =>
                    {
                        mp.Enabled = cbModelEn.IsChecked == true;
                        CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, activeAg);
                        anyChanged = true;
                        RenderLeftProfileList();
                        RenderAgentWorkbench(prof);
                    };
                    Grid.SetColumn(cbModelEn, 1);

                    UIElement modelSelectorEl;
                    if (!canSelectModels)
                    {
                        modelSelectorEl = new TextBlock
                        {
                            Text = "官方默认托管模型（防风控锁定，不可切换模型名）",
                            FontSize = 12,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = BrushHex("#E2E8F0"),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                    }
                    else
                    {
                        var cbSlotModel = new ComboBox { Height = 26, FontSize = 12 };
                        var modelPool = catalog.Select(c => c.ModelName).ToList();
                        if (!string.IsNullOrWhiteSpace(mp.ModelName) && !modelPool.Contains(mp.ModelName, StringComparer.OrdinalIgnoreCase))
                            modelPool.Insert(0, mp.ModelName);

                        foreach (var mn in modelPool)
                        {
                            var mOwn = LocalModelCatalogService.ClassifyModelOwnership(activeAg.Id, mn, null, null, activeAg.Category);
                            var ci = new ComboBoxItem { Content = $"{mn}  ({(mOwn.IsNativeOwn ? "自有" : mOwn.VendorName)})", Tag = mn };
                            cbSlotModel.Items.Add(ci);
                            if (string.Equals(mn, mp.ModelName, StringComparison.OrdinalIgnoreCase))
                                cbSlotModel.SelectedItem = ci;
                        }
                        cbSlotModel.SelectionChanged += (_, _) =>
                        {
                            if (suppressSync || cbSlotModel.SelectedItem is not ComboBoxItem ci || ci.Tag is not string newMn) return;
                            var spec = CloudRecommendationService.InferCloudModelSpec(activeAg.Id, newMn);
                            var newOwn = LocalModelCatalogService.ClassifyModelOwnership(activeAg.Id, newMn, null, null, activeAg.Category);
                            mp.ModelName = newMn;
                            mp.OwnershipKind = newOwn.OwnershipKind;
                            mp.VendorName = newOwn.VendorName;
                            mp.RecommendedEffort = spec.Tier;
                            mp.SpeedTier = spec.SpeedTier;
                            mp.SpecialtyNote = spec.SpecialtyNote;
                            mp.ContextWindowTokens = spec.ContextWindowTokens;
                            mp.MaxOutputTokens = spec.MaxOutputTokens;
                            var scores = CloudRecommendationService.InferModelCapabilityScores(activeAg.Id, newMn);
                            mp.CapabilityScores = scores;
                            mp.PrimaryRoles = scores.Keys.ToList();
                            var (pol, diff) = CloudRecommendationService.InferModelUsageRules(activeAg.Id, newMn, spec.Tier, myIdx);
                            mp.UsagePolicy = pol;
                            mp.TaskDifficultyRule = diff;
                            CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, activeAg);
                            anyChanged = true;
                            RenderLeftProfileList();
                            RenderAgentWorkbench(prof);
                        };
                        modelSelectorEl = cbSlotModel;
                    }
                    Grid.SetColumn(modelSelectorEl, 2);

                    var ownBadge = new Border
                    {
                        Background = BrushHex(own.IsNativeOwn ? "#0D2B24" : "#261E12"),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(7, 3, 7, 3),
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor = allowCustomModel ? Cursors.Hand : Cursors.Arrow,
                        ToolTip = allowCustomModel ? "点击切换自有 / 第三方归属" : $"模型归属：{own.BadgeText}",
                        Child = new TextBlock
                        {
                            Text = own.IsNativeOwn ? $"自有 · {own.VendorName}" : $"第三方 · {own.VendorName}",
                            FontSize = 10.5,
                            Foreground = BrushHex(own.IsNativeOwn ? "#6EE7B7" : "#FCD34D")
                        }
                    };
                    if (allowCustomModel)
                    {
                        ownBadge.MouseLeftButtonUp += (_, _) =>
                        {
                            mp.OwnershipKind = own.IsNativeOwn ? "third_party" : "native";
                            anyChanged = true;
                            RenderAgentWorkbench(prof);
                        };
                    }
                    Grid.SetColumn(ownBadge, 4);

                    var btnToggleExpand = new Button
                    {
                        Content = isExpanded ? "收起能力 ▲" : "配置能力 ▼",
                        Style = actionStyle,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Padding = new Thickness(9, 2, 9, 2),
                        Background = BrushHex(isExpanded ? "#16304C" : "#1A273B"),
                        BorderBrush = BrushHex(isExpanded ? "#38BDF8" : "#3B5275"),
                        Foreground = BrushHex(isExpanded ? "#7DD3FC" : "#E2E8F0"),
                        Cursor = Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    btnToggleExpand.Click += (_, _) =>
                    {
                        if (isExpanded) expandedModelKeys.Remove(expandKey);
                        else expandedModelKeys.Add(expandKey);
                        RenderAgentWorkbench(prof);
                    };

                    var r1Right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    if (canSelectModels && snap.ModelProfiles.Count > 1)
                    {
                        var btnDelSlot = MakeGhostBtn("×", "#64748B", 13);
                        btnDelSlot.ToolTip = "移除此模型";
                        btnDelSlot.Click += (_, _) =>
                        {
                            int oldCount = snap.ModelProfiles.Count;
                            snap.ModelProfiles.RemoveAt(myIdx);
                            expandedModelKeys.Remove(expandKey);
                            for (int k = myIdx + 1; k < oldCount; k++)
                            {
                                if (expandedModelKeys.Remove($"{activeAg.Id}::{k}"))
                                    expandedModelKeys.Add($"{activeAg.Id}::{k - 1}");
                            }
                            CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, activeAg);
                            anyChanged = true;
                            RenderLeftProfileList();
                            RenderAgentWorkbench(prof);
                        };
                        r1Right.Children.Add(btnDelSlot);
                    }
                    Grid.SetColumn(r1Right, 6);

                    r1.Children.Add(orderWrap);
                    r1.Children.Add(cbModelEn);
                    r1.Children.Add(modelSelectorEl);
                    r1.Children.Add(ownBadge);
                    r1.Children.Add(r1Right);
                    cardStack.Children.Add(r1);

                    // Row 2: 极简内联规则单行（去掉 4 行彩色小标题，视觉高度立减一半！）
                    var r2 = new Grid { Margin = new Thickness(0, 0, 0, isExpanded ? 8 : 5) };
                    r2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 策略lbl
                    r2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) }); // 策略
                    r2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                    r2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 档位lbl
                    r2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(102) }); // 档位
                    r2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                    r2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 难度lbl
                    r2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(106) }); // 难度
                    r2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                    r2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 备注

                    TextBlock MakeInlineLbl(string t, int col)
                    {
                        var tb = new TextBlock { Text = t, FontSize = 11, Foreground = BrushHex("#64748B"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
                        Grid.SetColumn(tb, col);
                        return tb;
                    }

                    var cbPolicy = new ComboBox { Height = 24, FontSize = 11 };
                    foreach (var pol in ModelRuleCatalog.UsagePolicies)
                    {
                        var ci = new ComboBoxItem { Content = pol.Label, Tag = pol.Id, ToolTip = pol.Description };
                        cbPolicy.Items.Add(ci);
                        if (string.Equals(mp.UsagePolicy, pol.Id, StringComparison.OrdinalIgnoreCase)) cbPolicy.SelectedItem = ci;
                    }
                    if (cbPolicy.SelectedIndex < 0) cbPolicy.SelectedIndex = 0;
                    cbPolicy.SelectionChanged += (_, _) =>
                    {
                        if (!suppressSync && cbPolicy.SelectedItem is ComboBoxItem ci && ci.Tag is string pid) { mp.UsagePolicy = pid; anyChanged = true; }
                    };

                    var cbEffort = new ComboBox { Height = 24, FontSize = 11, IsEnabled = canSelectModels };
                    foreach (var (ek, el) in new[] { ("high", "高档·深思"), ("medium", "中档·主力"), ("low", "低档·极速") })
                    {
                        var ci = new ComboBoxItem { Content = el, Tag = ek };
                        cbEffort.Items.Add(ci);
                        if (string.Equals(mp.RecommendedEffort, ek, StringComparison.OrdinalIgnoreCase)) cbEffort.SelectedItem = ci;
                    }
                    if (cbEffort.SelectedIndex < 0) cbEffort.SelectedIndex = 1;
                    cbEffort.SelectionChanged += (_, _) =>
                    {
                        if (!suppressSync && cbEffort.SelectedItem is ComboBoxItem ci && ci.Tag is string ev)
                        {
                            mp.RecommendedEffort = ev;
                            mp.SpeedTier = ev == "low" ? "fast" : ev;
                            CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, activeAg);
                            anyChanged = true;
                        }
                    };

                    var cbDiff = new ComboBox { Height = 24, FontSize = 11 };
                    foreach (var df in ModelRuleCatalog.DifficultyRules)
                    {
                        var ci = new ComboBoxItem { Content = df.Label, Tag = df.Id, ToolTip = df.Description };
                        cbDiff.Items.Add(ci);
                        if (string.Equals(mp.TaskDifficultyRule, df.Id, StringComparison.OrdinalIgnoreCase)) cbDiff.SelectedItem = ci;
                    }
                    if (cbDiff.SelectedIndex < 0) cbDiff.SelectedIndex = 0;
                    cbDiff.SelectionChanged += (_, _) =>
                    {
                        if (!suppressSync && cbDiff.SelectedItem is ComboBoxItem ci && ci.Tag is string did) { mp.TaskDifficultyRule = did; anyChanged = true; }
                    };

                    var tbModelNote = new TextBox
                    {
                        Text = mp.SpecialtyNote ?? "",
                        Height = 24,
                        FontSize = 11,
                        Foreground = BrushHex("#94A3B8"),
                        Padding = new Thickness(6, 2, 6, 2),
                        ToolTip = "规则说明或触发关键词（例如：shader / 架构重构 / 贴图生成）"
                    };
                    tbModelNote.TextChanged += (_, _) => { if (!suppressSync) { mp.SpecialtyNote = tbModelNote.Text.Trim(); anyChanged = true; } };

                    Grid.SetColumn(cbPolicy, 1);
                    Grid.SetColumn(cbEffort, 4);
                    Grid.SetColumn(cbDiff, 7);
                    Grid.SetColumn(tbModelNote, 9);
                    r2.Children.Add(MakeInlineLbl("规则", 0));
                    r2.Children.Add(cbPolicy);
                    r2.Children.Add(MakeInlineLbl("档位", 3));
                    r2.Children.Add(cbEffort);
                    r2.Children.Add(MakeInlineLbl("难度", 6));
                    r2.Children.Add(cbDiff);
                    r2.Children.Add(tbModelNote);
                    cardStack.Children.Add(r2);

                    // 第 3 行：展开/收起统一衔接区（收起与展开按钮稳稳停在同一行最右侧，紧贴下方能力矩阵）
                    var capSection = new Border
                    {
                        BorderBrush = BrushHex("#1E293B"),
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        Padding = new Thickness(0, 6, 0, 0)
                    };
                    if (!isExpanded)
                    {
                        var collapsedBar = new DockPanel { LastChildFill = true };
                        DockPanel.SetDock(btnToggleExpand, Dock.Right);
                        btnToggleExpand.VerticalAlignment = VerticalAlignment.Center;
                        collapsedBar.Children.Add(btnToggleExpand);

                        var capSummaryWrap = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                        var activeCaps = WorkSpecialtyCatalog.OrderedItems
                            .Select(w => (Item: w, Score: mp.GetCapabilityScore(w.Id)))
                            .Where(x => x.Score > 0)
                            .OrderByDescending(x => x.Score)
                            .ToList();

                        if (activeCaps.Count == 0)
                        {
                            capSummaryWrap.Children.Add(new TextBlock { Text = "未开启任何能力（点击右侧「配置能力 ▼」设置）", FontSize = 10.5, Foreground = BrushHex("#64748B"), VerticalAlignment = VerticalAlignment.Center });
                        }
                        else
                        {
                            foreach (var ac in activeCaps)
                            {
                                bool isAce = ac.Score >= 5;
                                capSummaryWrap.Children.Add(new Border
                                {
                                    Background = BrushHex(isAce ? "#261E0F" : "#182334"),
                                    CornerRadius = new CornerRadius(4),
                                    Padding = new Thickness(6, 1, 6, 1),
                                    Margin = new Thickness(0, 0, 5, 2),
                                    Child = new TextBlock
                                    {
                                        Text = $"{ac.Item.Label} {ac.Score}★",
                                        FontSize = 10.5,
                                        Foreground = BrushHex(isAce ? "#FCD34D" : "#93C5FD")
                                    }
                                });
                            }
                        }
                        collapsedBar.Children.Add(capSummaryWrap);
                        capSection.Child = collapsedBar;
                        cardStack.Children.Add(capSection);
                    }
                    // 展开态：同一衔接栏右侧带「收起能力 ▲」，正下方紧接 4 列 × 3 行能力下拉矩阵
                    else
                    {
                        var matrixSection = capSection;
                        var mStack = new StackPanel();

                        var mHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                        var quickActs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                        Button MakeQuickLink(string label, Action applyFn)
                        {
                            var qb = MakeGhostBtn(label, "#94A3B8", 10.5);
                            qb.Padding = new Thickness(5, 1, 5, 1);
                            qb.Click += (_, _) =>
                            {
                                applyFn();
                                CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, activeAg);
                                anyChanged = true;
                                RenderAgentWorkbench(prof);
                            };
                            return qb;
                        }
                        quickActs.Children.Add(MakeQuickLink("智能评估", () =>
                        {
                            var inf = CloudRecommendationService.InferModelCapabilityScores(activeAg.Id, mp.ModelName);
                            mp.CapabilityScores = inf;
                            mp.PrimaryRoles = inf.Keys.ToList();
                        }));
                        quickActs.Children.Add(MakeQuickLink("主脑型", () =>
                        {
                            mp.CapabilityScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["brain_analysis"] = 5, ["deep_debug"] = 5, ["visual_accept"] = 5, ["text_writing"] = 4
                            };
                            mp.PrimaryRoles = mp.CapabilityScores.Keys.ToList();
                        }));
                        quickActs.Children.Add(MakeQuickLink("编程型", () =>
                        {
                            mp.CapabilityScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["fast_execution"] = 5, ["ui_shader_vfx"] = 5, ["deep_debug"] = 4, ["data_config"] = 4, ["build_testing"] = 4
                            };
                            mp.PrimaryRoles = mp.CapabilityScores.Keys.ToList();
                        }));
                        quickActs.Children.Add(MakeQuickLink("作图型", () =>
                        {
                            mp.CapabilityScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["image_generation"] = 5, ["video_animation"] = 5, ["text_writing"] = 4, ["model_3d_motion"] = 4, ["audio_sfx_bgm"] = 4
                            };
                            mp.PrimaryRoles = mp.CapabilityScores.Keys.ToList();
                        }));
                        quickActs.Children.Add(MakeQuickLink("清空", () =>
                        {
                            mp.CapabilityScores.Clear();
                            mp.PrimaryRoles.Clear();
                        }));
                        quickActs.Children.Add(new Border
                        {
                            Width = 1,
                            Height = 11,
                            Background = BrushHex("#243248"),
                            Margin = new Thickness(6, 0, 6, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        });
                        btnToggleExpand.VerticalAlignment = VerticalAlignment.Center;
                        quickActs.Children.Add(btnToggleExpand);
                        DockPanel.SetDock(quickActs, Dock.Right);
                        mHeader.Children.Add(quickActs);
                        mHeader.Children.Add(new TextBlock
                        {
                            Text = "能力与强度（勾选开启，下拉选 1~5★）",
                            FontSize = 11,
                            Foreground = BrushHex("#94A3B8"),
                            VerticalAlignment = VerticalAlignment.Center
                        });
                        mStack.Children.Add(mHeader);

                        var capGrid = new UniformGrid { Columns = 4 };
                        foreach (var wItem in WorkSpecialtyCatalog.OrderedItems)
                        {
                            string roleId = wItem.Id;
                            int curScore = mp.GetCapabilityScore(roleId);

                            // 无硬边方框！仅对已开启的能力呈现柔和底色，未开启保持透明弱化，一眼聚焦重点
                            var cell = new Border
                            {
                                Background = BrushHex(curScore > 0 ? "#182438" : "Transparent"),
                                CornerRadius = new CornerRadius(4),
                                Padding = new Thickness(5, 3, 4, 3),
                                Margin = new Thickness(0, 0, 4, 3),
                                ToolTip = $"{wItem.Icon} {wItem.Label}：{wItem.Tooltip}"
                            };
                            var cDock = new DockPanel();

                            var levelCombo = new ComboBox
                            {
                                Width = 74,
                                Height = 21,
                                FontSize = 10,
                                Padding = new Thickness(4, 1, 2, 1),
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            foreach (var (lv, lbl) in new (int Val, string Label)[]
                            {
                                (0, "关闭"),
                                (1, "1★基础"),
                                (2, "2★辅助"),
                                (3, "3★熟练"),
                                (4, "4★强项"),
                                (5, "5★王牌")
                            })
                            {
                                levelCombo.Items.Add(new ComboBoxItem { Content = lbl, Tag = lv });
                            }
                            DockPanel.SetDock(levelCombo, Dock.Right);

                            var cbRole = new CheckBox
                            {
                                Content = wItem.Label,
                                IsChecked = curScore > 0,
                                FontSize = 10.8,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(0, 0, 4, 0)
                            };

                            bool localMute = false;
                            void RefreshCellVisual(int scoreNow)
                            {
                                localMute = true;
                                try
                                {
                                    cbRole.IsChecked = scoreNow > 0;
                                    cbRole.Foreground = BrushHex(scoreNow > 0 ? "#F1F5F9" : "#64748B");
                                    cell.Background = BrushHex(scoreNow >= 5 ? "#241E14" : (scoreNow > 0 ? "#182438" : "Transparent"));
                                    levelCombo.Opacity = scoreNow > 0 ? 1.0 : 0.55;
                                    levelCombo.SelectedItem = levelCombo.Items.OfType<ComboBoxItem>()
                                        .FirstOrDefault(i => i.Tag is int v && v == scoreNow) ?? levelCombo.Items[0];
                                    levelCombo.Foreground = BrushHex(scoreNow >= 5 ? "#FCD34D" : (scoreNow >= 4 ? "#93C5FD" : (scoreNow > 0 ? "#6EE7B7" : "#64748B")));
                                }
                                finally
                                {
                                    localMute = false;
                                }
                            }
                            RefreshCellVisual(curScore);

                            levelCombo.SelectionChanged += (_, _) =>
                            {
                                if (localMute || suppressSync) return;
                                if (levelCombo.SelectedItem is ComboBoxItem ci && ci.Tag is int newLv)
                                {
                                    mp.SetCapabilityScore(roleId, newLv);
                                    RefreshCellVisual(newLv);
                                    CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, activeAg);
                                    anyChanged = true;
                                }
                            };

                            cbRole.Click += (_, _) =>
                            {
                                if (localMute) return;
                                int nextScore = cbRole.IsChecked == true ? 3 : 0;
                                if (nextScore > 0)
                                {
                                    var inf = CloudRecommendationService.InferModelCapabilityScores(activeAg.Id, mp.ModelName, new[] { roleId });
                                    if (inf.TryGetValue(roleId, out var isc) && isc > 0) nextScore = isc;
                                }
                                mp.SetCapabilityScore(roleId, nextScore);
                                RefreshCellVisual(nextScore);
                                CloudRecommendationService.SyncSnapshotDerivedFromModelProfiles(snap, activeAg);
                                anyChanged = true;
                            };

                            cDock.Children.Add(levelCombo);
                            cDock.Children.Add(cbRole);
                            cell.Child = cDock;
                            capGrid.Children.Add(cell);
                        }
                        mStack.Children.Add(capGrid);
                        matrixSection.Child = mStack;
                        cardStack.Children.Add(matrixSection);
                    }

                    cardBorder.Child = cardStack;
                    modelsListStack.Children.Add(cardBorder);
                }

                wbDock.Children.Add(modelsScrollViewer);
            }
            // ==================== 子视图 1：权限与运行通道 ====================
            else
            {
                var permStack = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

                if (canSelectModels)
                {
                    var tierGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                    tierGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    tierGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
                    tierGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                    tierGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    tierGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var tierPrefCombo = new ComboBox { FontSize = 11.5, Height = 26 };
                    foreach (var (tk, tl) in new[]
                    {
                        ("auto", "自动按能力强度与规则选模"),
                        ("high", "强制优先高档旗舰模型"),
                        ("medium", "强制优先中档主力模型"),
                        ("low", "强制优先极速低档模型")
                    })
                        tierPrefCombo.Items.Add(new ComboBoxItem { Content = tl, Tag = tk });
                    tierPrefCombo.SelectedItem = tierPrefCombo.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(i => string.Equals(i.Tag as string, snap.PreferredEffortTier, StringComparison.OrdinalIgnoreCase)) ?? tierPrefCombo.Items[0];
                    tierPrefCombo.SelectionChanged += (_, _) =>
                    {
                        if (!suppressSync && tierPrefCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tv) { snap.PreferredEffortTier = tv; anyChanged = true; }
                    };

                    var noteBox = new TextBox { Text = snap.CustomWorkNote ?? "", FontSize = 11.5, Height = 26, Padding = new Thickness(8, 3, 8, 3) };
                    noteBox.TextChanged += (_, _) => { if (!suppressSync) { anyChanged = true; snap.CustomWorkNote = noteBox.Text.Trim(); } };

                    var l1 = new TextBlock { Text = "工具级调度：", FontSize = 11.5, Foreground = BrushHex("#94A3B8"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                    var l2 = new TextBlock { Text = "擅长说明：", FontSize = 11.5, Foreground = BrushHex("#94A3B8"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                    Grid.SetColumn(l1, 0); Grid.SetColumn(tierPrefCombo, 1); Grid.SetColumn(l2, 3); Grid.SetColumn(noteBox, 4);
                    tierGrid.Children.Add(l1); tierGrid.Children.Add(tierPrefCombo); tierGrid.Children.Add(l2); tierGrid.Children.Add(noteBox);
                    permStack.Children.Add(tierGrid);
                }

                permStack.Children.Add(new TextBlock { Text = "安全与功能权限", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#CBD5E1"), Margin = new Thickness(0, 0, 0, 6) });
                var permGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 12) };
                CheckBox MakePermCb(string caption, bool initVal, Action<bool> setter)
                {
                    var cb = new CheckBox { Content = caption, IsChecked = initVal, FontSize = 11.5, Margin = new Thickness(0, 2, 10, 4) };
                    cb.Click += (_, _) => { anyChanged = true; setter(cb.IsChecked == true); };
                    return cb;
                }
                permGrid.Children.Add(MakePermCb("允许直接修改工程代码与预制体", snap.Permissions.AllowDirectSourceWrite, v => snap.Permissions.AllowDirectSourceWrite = v));
                permGrid.Children.Add(MakePermCb("允许跨关联目录读写", snap.Permissions.AllowMultiDir, v => snap.Permissions.AllowMultiDir = v));
                permGrid.Children.Add(MakePermCb("允许运行构建与测试脚本", snap.Permissions.AllowShellBuild, v => snap.Permissions.AllowShellBuild = v));
                permGrid.Children.Add(MakePermCb("具备实机画面截图能力 (MCP)", snap.Permissions.HasPreviewMcp, v => snap.Permissions.HasPreviewMcp = v));
                permStack.Children.Add(permGrid);

                permStack.Children.Add(new TextBlock { Text = "运行参数与底层通道", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#CBD5E1"), Margin = new Thickness(0, 0, 0, 6) });
                var rtGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                rtGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rtGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                rtGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rtGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                rtGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rtGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                rtGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                rtGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                StackPanel MakeRtCell(string lbl, UIElement ctrl, int row, int col, int colSpan = 1)
                {
                    var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                    sp.Children.Add(new TextBlock { Text = lbl, FontSize = 10.8, Foreground = BrushHex("#94A3B8"), Margin = new Thickness(0, 0, 0, 3) });
                    sp.Children.Add(ctrl);
                    Grid.SetRow(sp, row);
                    Grid.SetColumn(sp, col);
                    if (colSpan > 1) Grid.SetColumnSpan(sp, colSpan);
                    return sp;
                }

                var cbReas = new ComboBox { FontSize = 11.2, Height = 26 };
                foreach (var (rk, rl) in new[] { ("auto", "auto · 跟随档位"), ("low", "low · 低档极速"), ("medium", "medium · 中档均衡"), ("high", "high · 高档长推理"), ("xhigh", "xhigh · 极高攻坚") })
                    cbReas.Items.Add(new ComboBoxItem { Content = rl, Tag = rk });
                cbReas.SelectedItem = cbReas.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, snap.RuntimeParams.ReasoningEffort, StringComparison.OrdinalIgnoreCase)) ?? cbReas.Items[0];
                cbReas.SelectionChanged += (_, _) => { if (!suppressSync && cbReas.SelectedItem is ComboBoxItem ci && ci.Tag is string v) { anyChanged = true; snap.RuntimeParams.ReasoningEffort = v; } };

                var cbCtx = new ComboBox { FontSize = 11.2, Height = 26 };
                foreach (var (ck, cl) in new[] { ("auto", "auto · 自适应"), ("compact", "compact · 32K省流"), ("standard", "standard · 128K标准"), ("max", "max · 200K~1M全量") })
                    cbCtx.Items.Add(new ComboBoxItem { Content = cl, Tag = ck });
                cbCtx.SelectedItem = cbCtx.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, snap.RuntimeParams.ContextBudget, StringComparison.OrdinalIgnoreCase)) ?? cbCtx.Items[0];
                cbCtx.SelectionChanged += (_, _) => { if (!suppressSync && cbCtx.SelectedItem is ComboBoxItem ci && ci.Tag is string v) { anyChanged = true; snap.RuntimeParams.ContextBudget = v; } };

                var cbMaxOut = new ComboBox { FontSize = 11.2, Height = 26 };
                foreach (var (mv, ml) in new (int, string)[] { (0, "0 · 自动不限"), (4096, "4096 · 短输出"), (8192, "8192 · 标准"), (16384, "16384 · 长代码"), (32768, "32768 · 架构"), (65536, "65536 · 超长") })
                    cbMaxOut.Items.Add(new ComboBoxItem { Content = ml, Tag = mv });
                cbMaxOut.SelectedItem = cbMaxOut.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int v && v == snap.RuntimeParams.MaxOutputTokens) ?? cbMaxOut.Items[0];
                cbMaxOut.SelectionChanged += (_, _) => { if (!suppressSync && cbMaxOut.SelectedItem is ComboBoxItem ci && ci.Tag is int v) { anyChanged = true; snap.RuntimeParams.MaxOutputTokens = v; } };

                var tbTimeout = new TextBox { Text = snap.RuntimeParams.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), FontSize = 11.2, Height = 26, Padding = new Thickness(7, 3, 7, 3) };
                tbTimeout.TextChanged += (_, _) => { if (!suppressSync && int.TryParse(tbTimeout.Text.Trim(), out int tv) && tv >= 0) { anyChanged = true; snap.RuntimeParams.TimeoutSeconds = tv; } };

                var cbRetries = new ComboBox { FontSize = 11.2, Height = 26 };
                foreach (var (rv, rl) in new (int, string)[] { (-1, "跟随全局网络重试"), (0, "0 次"), (1, "1 次"), (2, "2 次"), (3, "3 次"), (5, "5 次") })
                    cbRetries.Items.Add(new ComboBoxItem { Content = rl, Tag = rv });
                cbRetries.SelectedItem = cbRetries.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int v && v == snap.RuntimeParams.MaxRetries) ?? cbRetries.Items[0];
                cbRetries.SelectionChanged += (_, _) => { if (!suppressSync && cbRetries.SelectedItem is ComboBoxItem ci && ci.Tag is int v) { anyChanged = true; snap.RuntimeParams.MaxRetries = v; } };

                var cbRunnerMode = new ComboBox { FontSize = 11.2, Height = 26 };
                foreach (var (mk, ml) in new[] { ("codex_exec", "自动混合通道 (推荐)"), ("grok_cli", "Grok 原生无头 CLI"), ("cli_custom", "自定义 CLI Runner"), ("http_openai", "OpenAI HTTP API"), ("bridge_queue", "文件桥接队列"), ("none", "仅规范挂载") })
                    cbRunnerMode.Items.Add(new ComboBoxItem { Content = ml, Tag = mk });
                cbRunnerMode.SelectedItem = cbRunnerMode.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, snap.RunnerMode, StringComparison.OrdinalIgnoreCase)) ?? cbRunnerMode.Items[0];
                cbRunnerMode.SelectionChanged += (_, _) => { if (!suppressSync && cbRunnerMode.SelectedItem is ComboBoxItem ci && ci.Tag is string v) { anyChanged = true; snap.RunnerMode = v; } };

                var tbExtraArgs = new TextBox { Text = snap.RuntimeParams.ExtraRunnerArgs ?? "", FontSize = 11.2, Height = 26, Padding = new Thickness(7, 3, 7, 3) };
                tbExtraArgs.TextChanged += (_, _) => { if (!suppressSync) { anyChanged = true; snap.RuntimeParams.ExtraRunnerArgs = tbExtraArgs.Text.Trim(); } };

                var cbLaunchWin = new CheckBox
                {
                    Content = "派单时若桌面窗未运行则自动后台拉起",
                    IsChecked = snap.LaunchWindowOnDispatch ?? activeAg.LaunchWindowOnDispatch,
                    FontSize = 11.2,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 14, 0, 0)
                };
                cbLaunchWin.Click += (_, _) => { anyChanged = true; snap.LaunchWindowOnDispatch = cbLaunchWin.IsChecked == true; };

                rtGrid.Children.Add(MakeRtCell("推理深度档位", cbReas, 0, 0));
                rtGrid.Children.Add(MakeRtCell("上下文预算", cbCtx, 0, 2));
                rtGrid.Children.Add(MakeRtCell("最大输出 Token (0=不限)", cbMaxOut, 0, 4));
                rtGrid.Children.Add(MakeRtCell("单次超时秒数 (0=全局)", tbTimeout, 1, 0));
                rtGrid.Children.Add(MakeRtCell("异常自动重试次数", cbRetries, 1, 2));
                rtGrid.Children.Add(MakeRtCell("底层执行通道", cbRunnerMode, 1, 4));
                rtGrid.Children.Add(MakeRtCell("附加 CLI / API 参数", tbExtraArgs, 2, 0, colSpan: 3));
                var launchWrap = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                launchWrap.Children.Add(cbLaunchWin);
                Grid.SetRow(launchWrap, 2);
                Grid.SetColumn(launchWrap, 4);
                rtGrid.Children.Add(launchWrap);
                permStack.Children.Add(rtGrid);

                wbDock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = permStack });
            }

            wbContainer.Child = wbDock;
            workbenchHost.Children.Add(wbContainer);

            // 第 1 页改星级/启用后，若第 2 页已打开则立即刷新 12 类命中预览
            if (activeTabIdx == 1)
                RenderWorkRoutesMatrix(prof);
        }

        List<AgentRosterEntry> BuildRoutePreviewCandidates(TeamConfigProfile prof)
        {
            var list = new List<AgentRosterEntry>();
            foreach (var ag in GetConfigurableInstalledAgents())
            {
                var snap = EnsureSnapshotForAgent(prof, ag);
                if (!snap.Enabled) continue;
                list.Add(new AgentRosterEntry
                {
                    Id = ag.Id,
                    Name = ag.Name,
                    Enabled = true,
                    Detected = ag.Detected,
                    Category = ag.Category,
                    DetectorId = ag.DetectorId,
                    HighTierModel = snap.HighTierModel ?? ag.HighTierModel,
                    MediumTierModel = snap.MediumTierModel ?? ag.MediumTierModel,
                    LowTierModel = snap.LowTierModel ?? ag.LowTierModel,
                    PreferredEffortTier = snap.PreferredEffortTier ?? ag.PreferredEffortTier,
                    PrimaryWorkRoles = CloudRecommendationService.NormalizePrimaryWorkRoles(snap.PrimaryWorkRoles),
                    TagScores = new Dictionary<string, int>(snap.TagScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase),
                    ModelProfiles = (snap.ModelProfiles ?? new List<ModelWorkProfile>()).Select(m => m.Clone()).ToList(),
                    AvailableModels = ag.AvailableModels != null ? new List<string>(ag.AvailableModels) : new List<string>()
                });
            }
            return list;
        }

        static string ShortCoreModelName(string? m)
        {
            if (string.IsNullOrWhiteSpace(m)) return "自动";
            if (m == LocalModelCatalogService.ManagedDefaultModelDisplay) return "默认托管";
            int pIdx = m.IndexOf('(');
            var clean = pIdx > 1 ? m[..pIdx].Trim() : m.Trim();
            return clean.Length <= 22 ? clean : clean[..22] + "…";
        }

        static string TierBadgeLabel(string? tier) => (tier ?? "medium").Trim().ToLowerInvariant() switch
        {
            "high" or "xhigh" => "高档",
            "low" or "fast" => "低档",
            _ => "中档"
        };

        static string StarColorHex(int stars) => stars >= 5 ? "#FCD34D" : (stars >= 4 ? "#93C5FD" : (stars > 0 ? "#6EE7B7" : "#64748B"));

        void PaintRouteHitPreview(StackPanel host, TeamConfigProfile prof, string workTypeId)
        {
            host.Children.Clear();
            var candidates = BuildRoutePreviewCandidates(prof);
            var tempProj = new ProjectProfile
            {
                Id = "__preview__",
                Name = "preview",
                PrimaryRoot = "",
                WorkTypeRoutes = new Dictionary<string, WorkRouteRule>(prof.WorkTypeRoutes ?? new Dictionary<string, WorkRouteRule>(), StringComparer.OrdinalIgnoreCase)
            };
            bool bakPrefer = reg.UserConfig.PreferNativeModelsWhenTied;
            reg.UserConfig.PreferNativeModelsWhenTied = prof.PreferNativeModelsWhenTied;
            RoutingService.WorkRouteResolution hit;
            try
            {
                hit = RoutingService.ResolveRouteForWorkType(reg, tempProj, workTypeId, candidates: candidates);
            }
            finally
            {
                reg.UserConfig.PreferNativeModelsWhenTied = bakPrefer;
            }

            bool isLocked = tempProj.WorkTypeRoutes.TryGetValue(workTypeId, out var rule) && rule != null &&
                ((!string.IsNullOrWhiteSpace(rule.TargetAgentId) && !rule.TargetAgentId.Equals("auto", StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(rule.TargetModelName) && !rule.TargetModelName.Equals("auto", StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(rule.TargetEffortTier) && !rule.TargetEffortTier.Equals("auto", StringComparison.OrdinalIgnoreCase)));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0) };
            string prefix = isLocked ? "已锁定优先：" : "当前自动命中：";
            row.Children.Add(new TextBlock
            {
                Text = prefix,
                FontSize = 10.4,
                Foreground = BrushHex(isLocked ? "#FBBF24" : "#64748B"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });

            if (hit.TargetAgent == null)
            {
                row.Children.Add(new TextBlock
                {
                    Text = "无可路由成员",
                    FontSize = 10.6,
                    Foreground = BrushHex("#F87171"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                host.Children.Add(row);
                return;
            }

            row.Children.Add(OfficialBrandIconService.CreateCircularAvatarVisual(hit.TargetAgent.Id, 16, hit.TargetAgent, "#334155", 1.0));
            row.Children.Add(new TextBlock
            {
                Text = hit.TargetAgent.Name,
                FontSize = 10.8,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushHex("#E2E8F0"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 6, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 140
            });

            int stars = 0;
            var mp = (hit.TargetAgent.ModelProfiles ?? new List<ModelWorkProfile>())
                .FirstOrDefault(p => p.Enabled && string.Equals(p.ModelName, hit.TargetModelName, StringComparison.OrdinalIgnoreCase))
                ?? (hit.TargetAgent.ModelProfiles ?? new List<ModelWorkProfile>()).FirstOrDefault(p => p.Enabled);
            if (mp != null) stars = mp.GetCapabilityScore(workTypeId);
            else if (hit.TargetAgent.TagScores != null && hit.TargetAgent.TagScores.TryGetValue(workTypeId, out var ts)) stars = ts;

            row.Children.Add(new TextBlock
            {
                Text = "●",
                FontSize = 11,
                Foreground = BrushHex(StarColorHex(stars)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = stars > 0 ? $"{stars}★" : "—",
                FontSize = 10.4,
                Foreground = BrushHex(StarColorHex(stars)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = ShortCoreModelName(hit.TargetModelName),
                FontSize = 10.6,
                Foreground = BrushHex("#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260
            });
            row.Children.Add(new Border
            {
                Background = BrushHex("#1E293B"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = TierBadgeLabel(hit.TargetEffortTier),
                    FontSize = 9.8,
                    Foreground = BrushHex("#CBD5E1")
                }
            });
            host.Children.Add(row);
        }

        void RenderWorkRoutesMatrix(TeamConfigProfile prof)
        {
            routesStack.Children.Clear();
            prof.WorkTypeRoutes ??= new Dictionary<string, WorkRouteRule>(StringComparer.OrdinalIgnoreCase);

            var installedAgents = GetConfigurableInstalledAgents();
            var snapsById = installedAgents.ToDictionary(a => a.Id, a => EnsureSnapshotForAgent(prof, a), StringComparer.OrdinalIgnoreCase);
            var enabledInProf = installedAgents.Where(a => snapsById[a.Id].Enabled).ToList();

            foreach (var item in WorkSpecialtyCatalog.OrderedItems)
            {
                prof.WorkTypeRoutes.TryGetValue(item.Id, out var existingRule);
                string curTargetAg = existingRule?.TargetAgentId ?? "auto";
                string curTargetModel = existingRule?.TargetModelName ?? "auto";
                string curTargetTier = existingRule?.TargetEffortTier ?? "auto";

                var rowCard = new Border
                {
                    Background = BrushHex("#0F1724"),
                    BorderBrush = BrushHex("#1E293B"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });

                var leftInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                leftInfo.Children.Add(new TextBlock { Text = item.Label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#F1F5F9") });
                leftInfo.Children.Add(new TextBlock { Text = item.Tooltip, FontSize = 10.5, Foreground = BrushHex("#64748B"), TextTrimming = TextTrimming.CharacterEllipsis });
                var hitPreviewHost = new StackPanel();

                var agCombo = new ComboBox { FontSize = 11.5, Height = 26, VerticalAlignment = VerticalAlignment.Center };
                agCombo.Items.Add(new ComboBoxItem { Content = "自动择优 AI", Tag = "auto" });
                foreach (var ag in enabledInProf)
                    agCombo.Items.Add(new ComboBoxItem { Content = ag.Name, Tag = ag.Id });
                agCombo.SelectedItem = agCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(c => string.Equals(c.Tag as string, curTargetAg, StringComparison.OrdinalIgnoreCase)) ?? agCombo.Items[0];

                var modCombo = new ComboBox { FontSize = 11.2, Height = 26, VerticalAlignment = VerticalAlignment.Center };
                var tierCombo = new ComboBox { FontSize = 11.2, Height = 26, VerticalAlignment = VerticalAlignment.Center };
                tierCombo.Items.Add(new ComboBoxItem { Content = "自动选档", Tag = "auto" });
                tierCombo.Items.Add(new ComboBoxItem { Content = "高档模型", Tag = "high" });
                tierCombo.Items.Add(new ComboBoxItem { Content = "中档模型", Tag = "medium" });
                tierCombo.Items.Add(new ComboBoxItem { Content = "低档模型", Tag = "low" });
                tierCombo.SelectedItem = tierCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(c => string.Equals(c.Tag as string, curTargetTier, StringComparison.OrdinalIgnoreCase)) ?? tierCombo.Items[0];

                var workKey = item.Id;
                void FillModelChoices(string selAgId, string selModel)
                {
                    modCombo.Items.Clear();
                    var targetAg = enabledInProf.FirstOrDefault(a => string.Equals(a.Id, selAgId, StringComparison.OrdinalIgnoreCase));
                    if (targetAg != null && !LocalModelCatalogService.SupportsModelSelection(targetAg.Id))
                    {
                        var locked = new ComboBoxItem { Content = "默认托管 (自有)", Tag = "auto" };
                        modCombo.Items.Add(locked);
                        modCombo.SelectedItem = locked;
                        modCombo.IsEnabled = false;
                        return;
                    }
                    modCombo.IsEnabled = true;
                    var autoItem = new ComboBoxItem { Content = "自动按能力选型", Tag = "auto" };
                    modCombo.Items.Add(autoItem);
                    modCombo.SelectedItem = autoItem;

                    if (targetAg != null)
                    {
                        var agSnap = snapsById[targetAg.Id];
                        var configuredModels = (agSnap.ModelProfiles ?? new List<ModelWorkProfile>())
                            .Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.ModelName))
                            .ToList();
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var mp in configuredModels)
                        {
                            seen.Add(mp.ModelName);
                            var own = LocalModelCatalogService.ClassifyModelOwnership(targetAg.Id, mp.ModelName, mp.OwnershipKind, mp.VendorName, targetAg.Category);
                            int sc = mp.GetCapabilityScore(workKey);
                            string starSuffix = sc > 0 ? $" [{sc}★]" : "";
                            var ci = new ComboBoxItem { Content = $"{mp.ModelName}{starSuffix} ({(own.IsNativeOwn ? "自有" : own.VendorName)})", Tag = mp.ModelName };
                            modCombo.Items.Add(ci);
                            if (string.Equals(selModel, mp.ModelName, StringComparison.OrdinalIgnoreCase))
                                modCombo.SelectedItem = ci;
                        }
                        foreach (var mn in targetAg.AvailableModels ?? new List<string>())
                        {
                            if (string.IsNullOrWhiteSpace(mn) || !seen.Add(mn)) continue;
                            var ci = new ComboBoxItem { Content = $"{mn} (未配)", Tag = mn };
                            modCombo.Items.Add(ci);
                            if (string.Equals(selModel, mn, StringComparison.OrdinalIgnoreCase))
                                modCombo.SelectedItem = ci;
                        }
                    }
                }

                FillModelChoices(curTargetAg, curTargetModel);
                bool localMute = false;

                void PersistRoute()
                {
                    if (localMute) return;
                    var selA = (agCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
                    var selM = (modCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
                    var selT = (tierCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
                    anyChanged = true;
                    if (selA == "auto" && selM == "auto" && selT == "auto")
                        prof.WorkTypeRoutes.Remove(workKey);
                    else
                        prof.WorkTypeRoutes[workKey] = new WorkRouteRule { WorkTypeId = workKey, TargetAgentId = selA, TargetModelName = selM, TargetEffortTier = selT };
                    PaintRouteHitPreview(hitPreviewHost, prof, workKey);
                    RenderLeftProfileList();
                }

                agCombo.SelectionChanged += (_, _) =>
                {
                    if (localMute) return;
                    localMute = true;
                    var selA = (agCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
                    FillModelChoices(selA, "auto");
                    localMute = false;
                    PersistRoute();
                };
                modCombo.SelectionChanged += (_, _) => PersistRoute();
                tierCombo.SelectionChanged += (_, _) => PersistRoute();

                PaintRouteHitPreview(hitPreviewHost, prof, workKey);

                Grid.SetRow(leftInfo, 0);
                Grid.SetColumn(leftInfo, 0);
                Grid.SetRow(agCombo, 0);
                Grid.SetColumn(agCombo, 2);
                Grid.SetRow(modCombo, 0);
                Grid.SetColumn(modCombo, 4);
                Grid.SetRow(tierCombo, 0);
                Grid.SetColumn(tierCombo, 6);
                hitPreviewHost.Margin = new Thickness(0, 4, 0, 0);
                Grid.SetRow(hitPreviewHost, 1);
                Grid.SetColumn(hitPreviewHost, 0);
                Grid.SetColumnSpan(hitPreviewHost, 7);
                grid.Children.Add(leftInfo);
                grid.Children.Add(agCombo);
                grid.Children.Add(modCombo);
                grid.Children.Add(tierCombo);
                grid.Children.Add(hitPreviewHost);
                rowCard.Child = grid;
                routesStack.Children.Add(rowCard);
            }
        }

        void SwitchManagerTab(int tabIdx)
        {
            activeTabIdx = tabIdx == 1 ? 1 : 0;
            tabAgentsBorder.BorderBrush = activeTabIdx == 0 ? BrushHex("#38BDF8") : Brushes.Transparent;
            tabAgentsText.Foreground = activeTabIdx == 0 ? BrushHex("#F8FAFC") : BrushHex("#64748B");
            tabRoutesBorder.BorderBrush = activeTabIdx == 1 ? BrushHex("#38BDF8") : Brushes.Transparent;
            tabRoutesText.Foreground = activeTabIdx == 1 ? BrushHex("#F8FAFC") : BrushHex("#64748B");

            tab0Grid.Visibility = activeTabIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            tab1Dock.Visibility = activeTabIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (activeTabIdx == 1) RenderWorkRoutesMatrix(selectedProfile);
        }

        void LoadSelectedProfileIntoRightEditor()
        {
            suppressSync = true;
            try
            {
                nameBox.Text = selectedProfile.Name;
                descBox.Text = selectedProfile.Description;
                bool isActive = string.Equals(selectedProfile.Id, reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase);
                activeStatusPill.Background = BrushHex(isActive ? "#064E3B" : "#1E293B");
                activeStatusText.Text = isActive ? "● 使用中" : "未启用";
                activeStatusText.Foreground = BrushHex(isActive ? "#6EE7B7" : "#94A3B8");

                RebuildStrategyCombos(selectedProfile);
                RenderAgentWorkbench(selectedProfile);
                if (activeTabIdx == 1) RenderWorkRoutesMatrix(selectedProfile);
            }
            finally
            {
                suppressSync = false;
            }
        }

        tabAgentsBorder.MouseLeftButtonUp += (_, _) => SwitchManagerTab(0);
        tabRoutesBorder.MouseLeftButtonUp += (_, _) => SwitchManagerTab(1);
        btnResetRoutesAuto.Click += (_, _) =>
        {
            selectedProfile.WorkTypeRoutes.Clear();
            anyChanged = true;
            RenderWorkRoutesMatrix(selectedProfile);
            RenderLeftProfileList();
        };

        nameBox.TextChanged += (_, _) =>
        {
            if (suppressSync) return;
            anyChanged = true;
            var trimmed = nameBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                reg.RenameTeamProfile(selectedProfile.Id, trimmed, null);
                RenderLeftProfileList();
            }
        };
        descBox.TextChanged += (_, _) =>
        {
            if (suppressSync) return;
            anyChanged = true;
            selectedProfile.Description = descBox.Text.Trim();
        };
        masterCombo.SelectionChanged += (_, _) =>
        {
            if (suppressSync) return;
            anyChanged = true;
            FlushHeaderFieldsToSelectedProfile();
            RenderLeftProfileList();
            RenderAgentWorkbench(selectedProfile);
        };
        fallbackCombo.SelectionChanged += (_, _) => { if (!suppressSync) { anyChanged = true; FlushHeaderFieldsToSelectedProfile(); } };
        reworkCombo.SelectionChanged += (_, _) => { if (!suppressSync) { anyChanged = true; FlushHeaderFieldsToSelectedProfile(); } };
        chkPreferNative.Click += (_, _) => { if (!suppressSync) { anyChanged = true; FlushHeaderFieldsToSelectedProfile(); } };

        btnNewProfile.Click += (_, _) =>
        {
            FlushHeaderFieldsToSelectedProfile();
            var prevActiveId = reg.UserConfig.ActiveTeamProfileId;
            var created = reg.CreateNewTeamProfile($"自定义方案 {reg.UserConfig.TeamProfiles.Count + 1}", "自定义参战 AI 与模型方案");
            reg.UserConfig.ActiveTeamProfileId = string.IsNullOrWhiteSpace(prevActiveId) ? created.Id : prevActiveId;
            selectedProfile = created;
            anyChanged = true;
            reg.SaveUserConfig();
            LoadSelectedProfileIntoRightEditor();
            RenderLeftProfileList();
            nameBox.Focus();
            nameBox.SelectAll();
        };

        btnCopyProfile.Click += (_, _) =>
        {
            FlushHeaderFieldsToSelectedProfile();
            var cloned = reg.DuplicateTeamProfile(selectedProfile.Id);
            if (cloned == null) return;
            selectedProfile = cloned;
            anyChanged = true;
            reg.SaveUserConfig();
            LoadSelectedProfileIntoRightEditor();
            RenderLeftProfileList();
            nameBox.Focus();
            nameBox.SelectAll();
        };

        btnDelProfile.Click += (_, _) =>
        {
            if (reg.UserConfig.TeamProfiles.Count <= 1) return;
            if (!AppPaths.IsIsolatedTest)
            {
                var confirm = MessageBox.Show(win, $"确定要删除配置方案「{selectedProfile.Name}」吗？", "确认删除配置方案", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }
            string delId = selectedProfile.Id;
            bool wasActive = string.Equals(delId, reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase);
            if (reg.DeleteTeamProfile(delId))
            {
                anyChanged = true;
                selectedProfile = reg.UserConfig.TeamProfiles.FirstOrDefault(p => string.Equals(p.Id, reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase)) ?? reg.UserConfig.TeamProfiles[0];
                if (wasActive) reg.ApplyTeamProfile(selectedProfile, saveToDisk: true);
                else reg.SaveUserConfig();
                LoadSelectedProfileIntoRightEditor();
                RenderLeftProfileList();
            }
        };

        btnExportProfile.Click += (_, _) =>
        {
            FlushHeaderFieldsToSelectedProfile();
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出配置方案 (.json)",
                Filter = "JSON 配置方案 (*.json)|*.json",
                FileName = $"{string.Concat(selectedProfile.Name.Split(Path.GetInvalidFileNameChars()))}.json"
            };
            if (sfd.ShowDialog(win) == true)
            {
                reg.ExportTeamProfileToJson(selectedProfile, sfd.FileName);
                footerHint.Text = $"已导出方案「{selectedProfile.Name}」至：{sfd.FileName}";
                footerHint.Foreground = BrushHex("#86EFAC");
            }
        };

        btnImportProfile.Click += (_, _) =>
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "导入配置方案 (.json)", Filter = "JSON 配置方案 (*.json)|*.json" };
            if (ofd.ShowDialog(win) == true)
            {
                var imported = reg.ImportTeamProfileFromJson(ofd.FileName);
                if (imported != null)
                {
                    anyChanged = true;
                    selectedProfile = imported;
                    reg.SaveUserConfig();
                    LoadSelectedProfileIntoRightEditor();
                    RenderLeftProfileList();
                    footerHint.Text = $"已导入配置方案「{imported.Name}」。";
                    footerHint.Foreground = BrushHex("#86EFAC");
                }
                else
                {
                    footerHint.Text = "导入失败：JSON 格式无效。";
                    footerHint.Foreground = BrushHex("#FCA5A5");
                }
            }
        };

        btnFillRecommendedAll.Click += (_, _) =>
        {
            FlushHeaderFieldsToSelectedProfile();
            var installedAgents = GetConfigurableInstalledAgents();
            // 先在临时快照上预演推荐结果，展示给用户确认后再真正写入
            var previewRows = new List<(AgentRosterEntry Agent, TeamAgentSnapshot PreviewSnap, bool WasEnabled)>();
            foreach (var ag in installedAgents)
            {
                var curSnap = EnsureSnapshotForAgent(selectedProfile, ag);
                var cloneSnap = new TeamAgentSnapshot
                {
                    AgentId = ag.Id,
                    Enabled = curSnap.Enabled,
                    PrimaryWorkRoles = new List<string>(curSnap.PrimaryWorkRoles ?? new List<string>()),
                    PreferredEffortTier = curSnap.PreferredEffortTier,
                    HighTierModel = curSnap.HighTierModel,
                    MediumTierModel = curSnap.MediumTierModel,
                    LowTierModel = curSnap.LowTierModel,
                    ModelProfiles = (curSnap.ModelProfiles ?? new List<ModelWorkProfile>()).Select(m => m.Clone()).ToList(),
                    CustomWorkNote = curSnap.CustomWorkNote,
                    Permissions = curSnap.Permissions?.Clone() ?? new AgentPermissions(),
                    RuntimeParams = curSnap.RuntimeParams?.Clone() ?? new AgentRuntimeParams()
                };
                if (curSnap.Enabled)
                {
                    ApplyCloudPresetToSnapshot(reg, ag, cloneSnap);
                }
                previewRows.Add((ag, cloneSnap, curSnap.Enabled));
            }

            var confirmWin = new Window
            {
                Title = "一键推荐配置预览",
                Width = 680,
                Height = 520,
                MinWidth = 560,
                MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = win,
                Background = BrushHex("#131D2E"),
                Foreground = BrushHex("#E2E8F0"),
                FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false,
                ResizeMode = ResizeMode.CanResize
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(confirmWin, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 36,
                ResizeBorderThickness = new Thickness(5),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8)
            });
            ApplyModalDwmRoundedCorners(confirmWin);

            bool confirmed = false;
            var outerBorder = new Border
            {
                Background = BrushHex("#131D2E"),
                BorderBrush = CreateModalSeamlessBorderBrush(),
                BorderThickness = new Thickness(1.8),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true
            };
            var dock = new DockPanel();

            // 标题栏
            var titleGrid = new Grid { Height = 36 };
            var titleBar = new Border
            {
                Height = 38,
                Background = BrushHex("#1A273D"),
                BorderBrush = BrushHex("#334E73"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Child = titleGrid
            };
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.Children.Add(new TextBlock
            {
                Text = $"确认应用推荐配置 · {selectedProfile.Name}",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushHex("#F1F5F9"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            });
            var btnX = new Button
            {
                Content = "✕",
                Width = 38,
                Height = 38,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = BrushHex("#94A3B8"),
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(btnX, true);
            btnX.Click += (_, _) => confirmWin.Close();
            Grid.SetColumn(btnX, 1);
            titleGrid.Children.Add(btnX);
            DockPanel.SetDock(titleBar, Dock.Top);
            dock.Children.Add(titleBar);

            // 底部按钮栏
            var bottomBar = new Border
            {
                Background = BrushHex("#111824"),
                BorderBrush = BrushHex("#1E293B"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(16, 10, 16, 10)
            };
            var bottomGrid = new Grid();
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottomGrid.Children.Add(new TextBlock
            {
                Text = "点「确认应用」才会写入当前方案，点「取消」不会做任何改动。",
                FontSize = 11.2,
                Foreground = BrushHex("#64748B"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var btnCancelRec = new Button { Content = "取消", Style = actionStyle, Padding = new Thickness(14, 5, 14, 5), FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
            var btnOkRec = new Button { Content = "确认应用推荐配置", Style = primaryStyle, Padding = new Thickness(14, 5, 14, 5), FontSize = 12 };
            btnCancelRec.Click += (_, _) => confirmWin.Close();
            btnOkRec.Click += (_, _) => { confirmed = true; confirmWin.Close(); };
            btnPanel.Children.Add(btnCancelRec);
            btnPanel.Children.Add(btnOkRec);
            Grid.SetColumn(btnPanel, 1);
            bottomGrid.Children.Add(btnPanel);
            bottomBar.Child = bottomGrid;
            DockPanel.SetDock(bottomBar, Dock.Bottom);
            dock.Children.Add(bottomBar);

            // 中部推荐内容列表
            var listStack = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            listStack.Children.Add(new TextBlock
            {
                Text = "以下是根据本机已安装客户端与可用模型库为您生成的推荐配置：",
                FontSize = 12,
                Foreground = BrushHex("#94A3B8"),
                Margin = new Thickness(0, 0, 0, 10)
            });

            var enabledPreview = previewRows.Where(r => r.WasEnabled).ToList();
            if (enabledPreview.Count == 0)
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = "当前方案尚未启用任何 AI 工具，请先在方案中启用至少 1 个 AI。",
                    FontSize = 12,
                    Foreground = BrushHex("#FCD34D"),
                    Margin = new Thickness(0, 8, 0, 8)
                });
            }
            else
            {
                foreach (var (ag, pSnap, _) in enabledPreview)
                {
                    var agCard = new Border
                    {
                        Background = BrushHex("#121926"),
                        BorderBrush = BrushHex("#1E293B"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 9, 12, 9),
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    var agStack = new StackPanel();
                    var agModels = (pSnap.ModelProfiles ?? new List<ModelWorkProfile>()).Where(m => m.Enabled).ToList();
                    var headRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                    headRow.Children.Add(new TextBlock
                    {
                        Text = ag.Name,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = BrushHex("#F1F5F9"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0)
                    });
                    headRow.Children.Add(new TextBlock
                    {
                        Text = !LocalModelCatalogService.SupportsModelSelection(ag.Id)
                            ? "官方默认托管"
                            : $"推荐配置 {agModels.Count} 个模型",
                        FontSize = 11.2,
                        Foreground = BrushHex("#64748B"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    agStack.Children.Add(headRow);

                    foreach (var m in agModels)
                    {
                        var own = LocalModelCatalogService.ClassifyModelOwnership(ag.Id, m.ModelName, m.OwnershipKind, m.VendorName, ag.Category);
                        var pol = ModelRuleCatalog.ShortUsagePolicyLabel(m.UsagePolicy);
                        var eff = m.RecommendedEffort?.ToLowerInvariant() switch
                        {
                            "high" => "高档·深思",
                            "low" => "低档·极速",
                            _ => "中档·主力"
                        };
                        var topCaps = WorkSpecialtyCatalog.OrderedItems
                            .Select(w => (w.Label, Score: m.GetCapabilityScore(w.Id)))
                            .Where(x => x.Score > 0)
                            .OrderByDescending(x => x.Score)
                            .Take(3)
                            .Select(x => $"{x.Label} {x.Score}★")
                            .ToList();
                        var capStr = topCaps.Count > 0 ? string.Join(" · ", topCaps) : "均衡辅助";

                        var mRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                        mRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                        mRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        mRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var ownTxt = new TextBlock
                        {
                            Text = own.IsNativeOwn ? "自有" : "三方",
                            FontSize = 10.5,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = own.IsNativeOwn ? BrushHex("#34D399") : BrushHex("#D97706"),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        var nameTxt = new TextBlock
                        {
                            Text = $"{m.ModelName}  [{pol} · {eff}]",
                            FontSize = 11.5,
                            Foreground = BrushHex("#E2E8F0"),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 0)
                        };
                        var capTxt = new TextBlock
                        {
                            Text = capStr,
                            FontSize = 11,
                            Foreground = BrushHex("#94A3B8"),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        Grid.SetColumn(ownTxt, 0);
                        Grid.SetColumn(nameTxt, 1);
                        Grid.SetColumn(capTxt, 2);
                        mRow.Children.Add(ownTxt);
                        mRow.Children.Add(nameTxt);
                        mRow.Children.Add(capTxt);
                        agStack.Children.Add(mRow);
                    }

                    agCard.Child = agStack;
                    listStack.Children.Add(agCard);
                }
            }

            dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listStack });
            outerBorder.Child = dock;
            confirmWin.Content = outerBorder;
            AttachModalLightbox(confirmWin, win);
            confirmWin.ShowDialog();

            if (!confirmed)
                return;

            foreach (var ag in installedAgents)
            {
                var agSnap = EnsureSnapshotForAgent(selectedProfile, ag);
                if (agSnap.Enabled)
                    ApplyCloudPresetToSnapshot(reg, ag, agSnap);
            }
            anyChanged = true;
            footerHint.Text = $"已按推荐方案更新「{selectedProfile.Name}」，点击右下角保存后生效。";
            footerHint.Foreground = BrushHex("#6EE7B7");
            RenderLeftProfileList();
            RenderAgentWorkbench(selectedProfile);
        };

        btnSaveOnly.Click += (_, _) =>
        {
            FlushHeaderFieldsToSelectedProfile();
            if (string.Equals(selectedProfile.Id, reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase))
                reg.ApplyTeamProfile(selectedProfile, saveToDisk: true);
            else
                reg.SaveUserConfig();
            anyChanged = true;
            footerHint.Text = $"已保存方案「{selectedProfile.Name}」。";
            footerHint.Foreground = BrushHex("#86EFAC");
            RenderLeftProfileList();
            LoadSelectedProfileIntoRightEditor();
        };

        btnSaveAndActivate.Click += (_, _) =>
        {
            FlushHeaderFieldsToSelectedProfile();
            reg.ApplyTeamProfile(selectedProfile, saveToDisk: true);
            anyChanged = true;
            win.Close();
        };

        btnClose.Click += (_, _) => win.Close();

        win.Tag = (Action<int, string?>)((tabIdx, focusAgId) =>
        {
            if (!string.IsNullOrWhiteSpace(focusAgId))
            {
                var agObj = GetConfigurableInstalledAgents().FirstOrDefault(a => string.Equals(a.Id, focusAgId, StringComparison.OrdinalIgnoreCase));
                if (agObj != null && EnsureSnapshotForAgent(selectedProfile, agObj).Enabled)
                {
                    selectedAgentId = agObj.Id;
                    activeAgentSubTab = 0;
                    RenderAgentWorkbench(selectedProfile);
                }
            }
            SwitchManagerTab(tabIdx);
        });

        SwitchManagerTab(0);
        LoadSelectedProfileIntoRightEditor();
        RenderLeftProfileList();

        if (!AppPaths.IsIsolatedTest)
        {
            var beforeSig = string.Join("\n", reg.UserConfig.Agents
                .OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                .Select(a => a.Id + "|" + (a.AvailableModels?.Count ?? 0) + "|" + string.Join("\u001f", a.AvailableModels ?? new List<string>())));
            win.Loaded += (_, _) =>
            {
                var agents = reg.UserConfig.Agents;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    LocalModelCatalogService.SyncAllAgentsWithLocalClientModels(agents, invalidateCache: true);
                    var afterSig = string.Join("\n", agents
                        .OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(a => a.Id + "|" + (a.AvailableModels?.Count ?? 0) + "|" + string.Join("\u001f", a.AvailableModels ?? new List<string>())));
                    return !string.Equals(beforeSig, afterSig, StringComparison.Ordinal);
                }).ContinueWith(t =>
                {
                    if (t.IsFaulted || !t.Result) return;
                    win.Dispatcher.BeginInvoke(() =>
                    {
                        foreach (var ag in reg.UserConfig.Agents)
                            CloudRecommendationService.EnsureAgentModelTiersPopulated(ag);
                        if (selectedProfile != null)
                            RenderAgentWorkbench(selectedProfile);
                    });
                });
            };
        }

        win.Content = rootDock;
        return (win, () => anyChanged);
    }
}
