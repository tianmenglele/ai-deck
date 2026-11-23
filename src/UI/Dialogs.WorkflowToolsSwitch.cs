using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AICollabHub.Models;
using AICollabHub.Services;

namespace AICollabHub.UI;

public static partial class Dialogs
{
    public static bool ShowWorkflowToolsSwitchDialog(
        Window? owner,
        RegistryService reg,
        DiscoveryService discovery,
        Func<string>? onRescanRequested = null)
    {
        var win = CreateBaseDialog(owner, "工作流开关 · 集中管理本机 AI 工具", 780, 640);
        bool saved = false;
        bool showCatalog = false;
        var pending = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in reg.UserConfig.Agents)
            pending[a.Id] = a.Enabled;

        var root = new DockPanel { Margin = new Thickness(16, 10, 16, 12) };

        var topBar = new Border
        {
            Background = BrushHex("#111827"),
            BorderBrush = BrushHex("#24324B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(topBar, Dock.Top);
        var topStack = new StackPanel();
        topStack.Children.Add(new TextBlock
        {
            Text = "按分组启停工作流参与。保存后立即写入当前配置方案与路由候选。",
            FontSize = 12,
            Foreground = BrushHex("#94A3B8"),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });
        var btnRow = new WrapPanel();
        var btnEnableInstalled = new Button
        {
            Content = "全部开启已装",
            Style = (Style)Application.Current.FindResource("ActionBtn"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 4),
            FontSize = 11.5
        };
        var btnKeepCore = new Button
        {
            Content = "仅保留主力",
            Style = (Style)Application.Current.FindResource("ActionBtn"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 4),
            FontSize = 11.5
        };
        var btnRescan = new Button
        {
            Content = "重新彻底扫描",
            Style = (Style)Application.Current.FindResource("ActionBtn"),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 4),
            FontSize = 11.5
        };
        LongActionTracker.ApplyBusyIfNeeded(btnRescan, LongActionTracker.ScanLocal, "重新彻底扫描");
        var chkShowCatalog = new CheckBox
        {
            Content = "显示未安装工具目录",
            Foreground = BrushHex("#CBD5E1"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 4)
        };
        btnRow.Children.Add(btnEnableInstalled);
        btnRow.Children.Add(btnKeepCore);
        btnRow.Children.Add(btnRescan);
        btnRow.Children.Add(chkShowCatalog);
        topStack.Children.Add(btnRow);
        var statusHint = new TextBlock
        {
            Text = "",
            FontSize = 11.5,
            Foreground = BrushHex("#FBBF24"),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        topStack.Children.Add(statusHint);
        topBar.Child = topStack;
        root.Children.Add(topBar);

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var btnCancel = new Button
        {
            Content = "取消",
            Style = (Style)Application.Current.FindResource("ActionBtn"),
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var btnSave = new Button
        {
            Content = "保存并应用",
            Style = (Style)Application.Current.FindResource("PrimaryBtn"),
            Padding = new Thickness(16, 5, 16, 5)
        };
        bottom.Children.Add(btnCancel);
        bottom.Children.Add(btnSave);
        root.Children.Add(bottom);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var listHost = new StackPanel();
        scroll.Content = listHost;
        root.Children.Add(scroll);

        static string GroupKey(string? category) => (category ?? "").Trim().ToLowerInvariant() switch
        {
            "local_ide" => "AI IDE / 编辑器",
            "local_agent" => "AI 桌面客户端",
            "local_cli" => "AI 命令行 CLI / Agent",
            "ide_extension" or "local_llm" => "本地大模型与扩展",
            "vps_bridge" or "remote_capsule" => "远程服务器节点",
            _ => OfficialBrandIconService.GetCategoryZh(null, category)
        };

        void RebuildList()
        {
            listHost.Children.Clear();
            var agents = reg.UserConfig.Agents
                .Where(a => showCatalog || a.Detected || string.IsNullOrEmpty(a.DetectorId))
                .ToList();

            if (showCatalog)
            {
                foreach (var spec in (reg.Detectors.Detectors ?? new List<DetectorSpec>()))
                {
                    if (agents.Any(a => string.Equals(a.Id, spec.Id, StringComparison.OrdinalIgnoreCase))) continue;
                    agents.Add(new AgentRosterEntry
                    {
                        Id = spec.Id,
                        DetectorId = spec.Id,
                        Name = spec.Name,
                        Category = spec.Category,
                        Detected = false,
                        Enabled = false,
                        Evidence = "目录项（本机未安装）",
                        TagScores = new Dictionary<string, int>(spec.DefaultTags ?? new Dictionary<string, int>())
                    });
                }
            }

            agents = agents
                .OrderBy(a => GroupKey(a.Category))
                .ThenByDescending(a => a.Detected)
                .ThenBy(a => a.Name)
                .ToList();

            string? lastGroup = null;
            foreach (var agent in agents)
            {
                string g = GroupKey(agent.Category);
                if (!string.Equals(lastGroup, g, StringComparison.Ordinal))
                {
                    lastGroup = g;
                    listHost.Children.Add(new TextBlock
                    {
                        Text = g,
                        FontSize = 12.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = BrushHex("#E2E8F0"),
                        Margin = new Thickness(0, 10, 0, 6)
                    });
                }

                bool isOn = pending.TryGetValue(agent.Id, out var pe) ? pe : agent.Enabled;
                bool canToggle = agent.Detected || string.IsNullOrEmpty(agent.DetectorId);
                var row = new Border
                {
                    Background = BrushHex(agent.Detected ? "#121824" : "#0E131D"),
                    BorderBrush = BrushHex("#1E293B"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Opacity = canToggle ? 1.0 : 0.72
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var avatar = OfficialBrandIconService.CreateCircularAvatarVisual(
                    agent.Id, 28, agent, OfficialBrandIconService.GetBrandColor(agent.Id), 1.2);
                avatar.Margin = new Thickness(0, 0, 10, 0);
                Grid.SetColumn(avatar, 0);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = agent.Name + (agent.Detected ? "" : "（未安装）"),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushHex(agent.Detected ? "#F1F5F9" : "#64748B")
                });
                string caps = string.Join(" · ", (agent.TagScores ?? new Dictionary<string, int>())
                    .Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .Select(kv => WorkSpecialtyCatalog.Items.TryGetValue(kv.Key, out var lb) ? lb : kv.Key));
                string evidence = string.IsNullOrWhiteSpace(agent.Evidence) ? "" : agent.Evidence.Trim();
                if (evidence.Length > 72) evidence = evidence[..72] + "…";
                info.Children.Add(new TextBlock
                {
                    Text = evidence + (string.IsNullOrEmpty(caps) ? "" : $"  |  {caps}"),
                    FontSize = 11,
                    Foreground = BrushHex("#64748B"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = agent.Evidence
                });
                Grid.SetColumn(info, 1);

                var toggle = CreateWorkflowPillToggle(agent.Id, isOn, canToggle);
                toggle.IsEnabled = canToggle;
                string capturedId = agent.Id;
                toggle.Checked += (_, _) => pending[capturedId] = true;
                toggle.Unchecked += (_, _) =>
                {
                    int others = reg.UserConfig.Agents.Count(a =>
                    {
                        bool on = pending.TryGetValue(a.Id, out var v) ? v : a.Enabled;
                        return on && !string.Equals(a.Id, capturedId, StringComparison.OrdinalIgnoreCase);
                    });
                    if (others <= 0)
                    {
                        statusHint.Text = "至少需保留 1 个工具在工作流中";
                        toggle.IsChecked = true;
                        pending[capturedId] = true;
                        return;
                    }
                    pending[capturedId] = false;
                    statusHint.Text = "";
                };
                Grid.SetColumn(toggle, 2);

                grid.Children.Add(avatar);
                grid.Children.Add(info);
                grid.Children.Add(toggle);
                row.Child = grid;
                listHost.Children.Add(row);
            }
        }

        btnEnableInstalled.Click += (_, _) =>
        {
            foreach (var a in reg.UserConfig.Agents.Where(x => x.Detected || string.IsNullOrEmpty(x.DetectorId)))
                pending[a.Id] = true;
            statusHint.Text = "已勾选全部已安装工具（点保存并应用生效）";
            RebuildList();
        };
        btnKeepCore.Click += (_, _) =>
        {
            var core = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "antigravity", "cursor", "grok_bot" };
            foreach (var a in reg.UserConfig.Agents)
                pending[a.Id] = core.Contains(a.Id) && (a.Detected || string.IsNullOrEmpty(a.DetectorId));
            if (!pending.Any(kv => kv.Value))
            {
                var first = reg.UserConfig.Agents.FirstOrDefault(a => a.Detected);
                if (first != null) pending[first.Id] = true;
            }
            statusHint.Text = "已切换为仅保留主力三件套（点保存并应用生效）";
            RebuildList();
        };
        btnRescan.Click += async (_, _) =>
        {
            if (!LongActionTracker.TryBegin(LongActionTracker.ScanLocal, "扫描中"))
            {
                statusHint.Text = LongActionTracker.DuplicateClickHint(LongActionTracker.ScanLocal);
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LongActionTracker.ShowBusy(btnRescan);
            statusHint.Text = "正在重新扫描本机 AI 工具…";
            try
            {
                string statusText = await System.Threading.Tasks.Task.Run(() =>
                    onRescanRequested != null
                        ? (onRescanRequested() ?? "")
                        : discovery.RunDiscoveryWithStats(reg).StatusText);
                foreach (var a in reg.UserConfig.Agents)
                {
                    if (!pending.ContainsKey(a.Id)) pending[a.Id] = a.Enabled;
                }
                var summary = string.IsNullOrWhiteSpace(statusText) ? "已完成重新彻底扫描" : statusText;
                statusHint.Text = $"✅ {summary}（耗时 {(int)sw.Elapsed.TotalSeconds}s）";
                RebuildList();
            }
            catch (Exception ex)
            {
                statusHint.Text = $"⚠ 扫描失败：{ex.Message}（耗时 {(int)sw.Elapsed.TotalSeconds}s）";
            }
            finally
            {
                LongActionTracker.EndAndRestore(LongActionTracker.ScanLocal);
            }
        };
        chkShowCatalog.Checked += (_, _) => { showCatalog = true; RebuildList(); };
        chkShowCatalog.Unchecked += (_, _) => { showCatalog = false; RebuildList(); };
        btnCancel.Click += (_, _) => win.Close();
        btnSave.Click += (_, _) =>
        {
            int onCount = reg.UserConfig.Agents.Count(a =>
            {
                bool on = pending.TryGetValue(a.Id, out var v) ? v : a.Enabled;
                return on && (a.Detected || string.IsNullOrEmpty(a.DetectorId));
            });
            if (onCount <= 0)
            {
                statusHint.Text = "至少需保留 1 个工具在工作流中，请先开启后再保存";
                return;
            }

            // 先开后关，避免批量过程中误触「最后一个」保护；走原子批量 API
            var batch = new List<(string AgentId, bool Enabled)>();
            foreach (var a in reg.UserConfig.Agents.Where(x => pending.TryGetValue(x.Id, out var want) && want && !x.Enabled))
                batch.Add((a.Id, true));
            foreach (var a in reg.UserConfig.Agents.Where(x => pending.TryGetValue(x.Id, out var want) && !want && x.Enabled))
                batch.Add((a.Id, false));
            if (!reg.SetAgentsWorkflowEnabled(batch, out var feedback))
            {
                statusHint.Text = feedback;
                foreach (var a in reg.UserConfig.Agents)
                    pending[a.Id] = a.Enabled;
                RebuildList();
                return;
            }
            saved = true;
            win.Close();
        };

        RebuildList();
        win.Content = root;
        win.ShowDialog();
        return saved;
    }

    public static ToggleButton CreateWorkflowPillToggle(string agentId, bool isOn, bool interactive)
    {
        var tb = new ToggleButton
        {
            IsChecked = isOn,
            Tag = "WorkflowToggle:" + agentId,
            Width = 66,
            Height = 26,
            Cursor = interactive ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
            ToolTip = isOn
                ? "工作流已开启：该 AI 可参与自动派单与路由。点击关闭。"
                : "已停用：该 AI 不参与工作流。点击开启。",
            Focusable = interactive,
            Style = CreatePillToggleStyle()
        };
        tb.Checked += (_, _) =>
            tb.ToolTip = "工作流已开启：该 AI 可参与自动派单与路由。点击关闭。";
        tb.Unchecked += (_, _) =>
            tb.ToolTip = "已停用：该 AI 不参与工作流。点击开启。";
        return tb;
    }

    private static Style CreatePillToggleStyle()
    {
        var style = new Style(typeof(ToggleButton));
        var template = new ControlTemplate(typeof(ToggleButton));
        var borderFactory = new FrameworkElementFactory(typeof(Border), "bd");
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(10, 2, 10, 2));
        borderFactory.SetValue(Border.BackgroundProperty, BrushHex("#475569"));
        var textFactory = new FrameworkElementFactory(typeof(TextBlock), "txt");
        textFactory.SetValue(TextBlock.TextProperty, "已停用");
        textFactory.SetValue(TextBlock.ForegroundProperty, BrushHex("#E2E8F0"));
        textFactory.SetValue(TextBlock.FontSizeProperty, 11.5);
        textFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        textFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(textFactory);
        template.VisualTree = borderFactory;

        var triggerOn = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        triggerOn.Setters.Add(new Setter(Border.BackgroundProperty, BrushHex("#10B981"), "bd"));
        triggerOn.Setters.Add(new Setter(TextBlock.TextProperty, "工作中", "txt"));
        triggerOn.Setters.Add(new Setter(TextBlock.ForegroundProperty, BrushHex("#ECFDF5"), "txt"));
        template.Triggers.Add(triggerOn);

        var triggerOff = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = false };
        triggerOff.Setters.Add(new Setter(Border.BackgroundProperty, BrushHex("#475569"), "bd"));
        triggerOff.Setters.Add(new Setter(TextBlock.TextProperty, "已停用", "txt"));
        template.Triggers.Add(triggerOff);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}
