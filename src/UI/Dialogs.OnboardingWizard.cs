using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AICollabHub.Models;
using AICollabHub.Services;

namespace AICollabHub.UI;

/// <summary>3 步新手引导。构造本身不扫描、不写注册表，方便离屏自检。</summary>
public sealed class OnboardingWizardDialog : Window
{
    private readonly RegistryService _reg;
    private readonly DiscoveryService _discovery;
    private readonly MainWindow? _hostMain;
    private readonly StartupAndShortcutService _startup = new();

    private int _step = 1;
    private bool _building;
    private DispatcherTimer? _foreignScanTimer;

    private readonly TextBlock[] _stepMarks = new TextBlock[3];
    private readonly TextBlock[] _stepTitles = new TextBlock[3];
    private readonly Border[] _stepDots = new Border[3];

    private readonly StackPanel _detectedList = new() { Margin = new Thickness(0) };
    private readonly StackPanel _missingList = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _scanHint = new()
    {
        Text = "",
        Foreground = Brush("#8EA0B8"),
        FontSize = 12.5,
        Margin = new Thickness(0, 0, 0, 8)
    };
    private readonly Button _rescanBtn = new()
    {
        Content = "重新扫描",
        MinWidth = 96,
        Height = 32,
        Padding = new Thickness(12, 0, 12, 0),
        FontSize = 12.5,
        Foreground = Brush("#E6EDF7"),
        Background = Brush("#1B2638"),
        BorderBrush = Brush("#31445F"),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand
    };

    private readonly ComboBox _masterCombo = new()
    {
        Height = 36,
        FontSize = 14,
        Margin = new Thickness(0, 6, 0, 0)
    };
    private readonly WrapPanel _workerWrap = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _step2Note = new()
    {
        Text = "主脑派活，勾选的人来做",
        Foreground = Brush("#8EA0B8"),
        FontSize = 13,
        Margin = new Thickness(0, 4, 0, 0),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly CheckBox _floatChk = new()
    {
        Content = "开启桌面置顶悬浮窗",
        IsChecked = true,
        FontSize = 15,
        Foreground = Brush("#F8FAFC"),
        Margin = new Thickness(0, 0, 0, 14)
    };
    private readonly CheckBox _startupChk = new()
    {
        Content = "开机自动启动 AIDeck",
        FontSize = 15,
        Foreground = Brush("#E6EDF7")
    };
    private readonly TextBlock _summaryText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("#E6EDF7"),
        FontSize = 15,
        LineHeight = 24
    };

    private readonly Grid _page1 = new();
    private readonly Grid _page2 = new();
    private readonly Grid _page3 = new();
    private readonly Button _prevBtn;
    private readonly Button _nextBtn;

    public OnboardingWizardDialog(RegistryService reg, DiscoveryService discovery, MainWindow? hostMain = null)
    {
        _reg = reg ?? throw new ArgumentNullException(nameof(reg));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _hostMain = hostMain;

        Title = "欢迎使用 AIDeck";
        Width = 760;
        Height = 560;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#131D2E");
        Foreground = Brush("#E6EDF7");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            UseAeroCaptionButtons = false
        });

        _prevBtn = MakeQuietButton("上一步");
        _nextBtn = MakePrimaryButton("下一步");
        _prevBtn.Click += (_, _) => { if (_step > 1) ShowStep(_step - 1); };
        _nextBtn.Click += (_, _) =>
        {
            if (_step < 3) ShowStep(_step + 1);
            else Commit(skip: false, closeDialog: true, touchStartupRegistry: true);
        };
        _rescanBtn.Click += (_, _) => BeginScan(fromUser: true);
        LongActionTracker.Arm(_rescanBtn, LongActionTracker.ScanLocal, "重新扫描");

        _startupChk.IsChecked = _reg.UserConfig.RunAtStartup;
        _floatChk.IsChecked = _reg.UserConfig.FloatingWindowVisible;

        Content = BuildShell();
        Dialogs.AttachModalLightbox(this, _hostMain);
        AttachHostIfReady();
        RebuildAgentLists();
        RebuildStep2();
        ShowStep(1);

        Loaded += (_, _) =>
        {
            if (AppPaths.IsIsolatedTest) return;
            BeginScan(fromUser: false);
        };
        Closed += (_, _) => _foreignScanTimer?.Stop();
    }

    private void AttachHostIfReady()
    {
        if (_hostMain == null || AppPaths.IsIsolatedTest) return;
        try
        {
            if (!_hostMain.IsLoaded) return;
            Owner = _hostMain;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        catch
        {
            /* 宿主还没进可视树时保持独立窗口，避免离屏构造抛错 */
        }
    }

    public static bool ShouldAutoShowOnboarding(UserHubConfig cfg, bool configFileExistedBeforeLoad, bool isIsolatedTest)
    {
        if (isIsolatedTest || cfg.HasCompletedOnboarding) return false;
        if (configFileExistedBeforeLoad && (cfg.Agents?.Any(a => a.Enabled) ?? false)) return false;
        return true;
    }

    public void SwitchStepForTest(int step)
    {
        int next = step < 1 ? 1 : (step > 3 ? 3 : step);
        ShowStep(next);
    }

    public int GetCurrentStepForTest() => _step;

    public void CompleteForTest(bool skip = false)
        => Commit(skip, closeDialog: false, touchStartupRegistry: false);

    private void Commit(bool skip, bool closeDialog, bool touchStartupRegistry)
    {
        if (!skip)
        {
            bool showFloat = _floatChk.IsChecked == true;
            _reg.UserConfig.FloatingWindowVisible = showFloat;
            if (showFloat) _reg.UserConfig.FloatingWindowTopmost = true;
            _reg.UserConfig.RunAtStartup = _startupChk.IsChecked == true;
            if (touchStartupRegistry && !AppPaths.IsIsolatedTest)
            {
                try
                {
                    _startup.ApplyStartupSetting(_reg.UserConfig.RunAtStartup, _reg.UserConfig.StartDirectlyIntoFloatingMode);
                }
                catch
                {
                    /* 启动项写失败不阻断进入工作台 */
                }
            }
        }

        try { _reg.SyncActiveTeamProfileWithCurrentState(saveToDisk: false); }
        catch { /* 没有方案时忽略，仍落盘引导标记 */ }

        _reg.UserConfig.HasCompletedOnboarding = true;
        _reg.SaveUserConfig();

        if (!closeDialog) return;
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            try { Close(); } catch { /* 离屏实例没有对话框循环 */ }
        }
    }

    private void ShowStep(int step)
    {
        _step = step;
        _page1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        _page2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        _page3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        _prevBtn.IsEnabled = step > 1;
        _prevBtn.Opacity = step > 1 ? 1 : 0.4;
        _nextBtn.Content = step == 3 ? "完成并进入工作台" : "下一步";
        PaintProgress();
        if (step == 1) RebuildAgentLists();
        if (step == 2) RebuildStep2();
        if (step == 3) RefreshSummary();
    }

    private void PaintProgress()
    {
        string[] titles = ["发现本机 AI", "选主脑与成员", "开启悬浮窗"];
        string[] marks = ["①", "②", "③"];
        for (int i = 0; i < 3; i++)
        {
            int n = i + 1;
            bool done = n < _step;
            bool current = n == _step;
            _stepMarks[i].Text = done ? "✓" : marks[i];
            _stepMarks[i].Foreground = done ? Brush("#06281A") : (current ? Brush("#F8FAFC") : Brush("#94A3B8"));
            _stepDots[i].Background = done ? Brush("#34D399") : (current ? Brush("#2563EB") : Brush("#1B2638"));
            _stepDots[i].BorderBrush = current ? Brush("#7DD3FC") : Brush("#2A3A52");
            _stepTitles[i].Text = titles[i];
            _stepTitles[i].Foreground = current ? Brush("#F8FAFC") : (done ? Brush("#86EFAC") : Brush("#8EA0B8"));
            _stepTitles[i].FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private UIElement BuildShell()
    {
        var root = new Grid { Margin = new Thickness(28, 18, 28, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(BuildHeader());
        var progress = BuildProgress();
        Grid.SetRow(progress, 1);
        root.Children.Add(progress);

        BuildPage1();
        BuildPage2();
        BuildPage3();
        var host = new Grid { Margin = new Thickness(0, 16, 0, 8) };
        host.Children.Add(_page1);
        host.Children.Add(_page2);
        host.Children.Add(_page3);
        Grid.SetRow(host, 2);
        root.Children.Add(host);

        var footer = BuildFooter();
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        var glow = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        glow.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#38BDF8")!, 0));
        glow.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#6366F1")!, 1));
        var shell = new DockPanel();
        var topGlow = new Border
        {
            Height = 2,
            Background = glow,
            IsHitTestVisible = false
        };
        DockPanel.SetDock(topGlow, Dock.Top);
        shell.Children.Add(topGlow);
        shell.Children.Add(root);
        var frame = new Border
        {
            Name = "WizardOuterFrame",
            Background = Brush("#131D2E"),
            BorderBrush = Brush("#5B84BA"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = shell
        };
        RegisterName("WizardOuterFrame", frame);
        return frame;
    }

    private UIElement BuildHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src)
            {
                for (var cur = src; cur != null; cur = VisualTreeHelper.GetParent(cur))
                {
                    if (cur is Button) return;
                }
            }
            try { DragMove(); } catch { /* 鼠标未按下时忽略 */ }
        };

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = "欢迎使用 AIDeck",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F8FAFC")
        });
        titles.Children.Add(new TextBlock
        {
            Text = "三步就好，之后可以随时再改。",
            FontSize = 13,
            Foreground = Brush("#8EA0B8"),
            Margin = new Thickness(0, 4, 0, 0)
        });
        grid.Children.Add(titles);

        var close = new Button
        {
            Content = "✕",
            Width = 32,
            Height = 32,
            FontSize = 14,
            Foreground = Brush("#94A3B8"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);
        return grid;
    }

    private UIElement BuildProgress()
    {
        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (i > 0) cell.Margin = new Thickness(18, 0, 0, 0);
            var dot = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0),
                Child = _stepMarks[i] = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold
                }
            };
            _stepDots[i] = dot;
            var title = new TextBlock { FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
            _stepTitles[i] = title;
            cell.Children.Add(dot);
            cell.Children.Add(title);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private void BuildPage1()
    {
        var dock = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
        DockPanel.SetDock(_rescanBtn, Dock.Right);
        top.Children.Add(_rescanBtn);
        DockPanel.SetDock(_scanHint, Dock.Left);
        _scanHint.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(_scanHint);
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);

        var expander = new Expander
        {
            Header = "查看未安装的 AI 工具（仅作参考）",
            IsExpanded = false,
            Foreground = Brush("#8EA0B8"),
            FontSize = 13,
            Margin = new Thickness(0, 12, 0, 0),
            Content = _missingList
        };
        DockPanel.SetDock(expander, Dock.Bottom);
        dock.Children.Add(expander);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _detectedList
        };
        dock.Children.Add(scroll);
        _page1.Children.Add(dock);
    }

    private void BuildPage2()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var stack = new StackPanel();
        var hero = Card();
        hero.Padding = new Thickness(16, 10, 16, 10);
        var heroStack = new StackPanel();
        heroStack.Children.Add(new TextBlock
        {
            Text = "先用一套顺手的分工",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F8FAFC")
        });
        heroStack.Children.Add(new TextBlock
        {
            Text = "按已经打开、并且这台电脑上有的助手来排。",
            FontSize = 13,
            Foreground = Brush("#8EA0B8"),
            Margin = new Thickness(0, 4, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });
        var apply = MakePrimaryButton("一键应用推荐分工");
        apply.Height = 44;
        apply.FontSize = 16;
        apply.HorizontalAlignment = HorizontalAlignment.Left;
        apply.Click += (_, _) => ApplyRecommendedSplit();
        heroStack.Children.Add(apply);
        hero.Child = heroStack;
        stack.Children.Add(hero);

        stack.Children.Add(new TextBlock
        {
            Text = "谁来做总控主脑",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F8FAFC"),
            Margin = new Thickness(0, 10, 0, 0)
        });
        stack.Children.Add(_masterCombo);
        stack.Children.Add(new TextBlock
        {
            Text = "谁参与后台干活",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F8FAFC"),
            Margin = new Thickness(0, 8, 0, 0)
        });
        stack.Children.Add(_workerWrap);
        stack.Children.Add(_step2Note);
        scroll.Content = stack;
        _page2.Children.Add(scroll);

        _masterCombo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (_masterCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string id || id.Length == 0) return;
            AssignMaster(id);
            var ag = _reg.UserConfig.Agents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            if (ag != null) ag.Enabled = true;
            RefreshSummary();
        };
    }

    private void BuildPage3()
    {
        var stack = new StackPanel();
        var switches = Card();
        switches.Padding = new Thickness(20, 18, 20, 18);
        var switchStack = new StackPanel();
        switchStack.Children.Add(_floatChk);
        switchStack.Children.Add(_startupChk);
        switches.Child = switchStack;
        stack.Children.Add(switches);

        var summary = Card();
        summary.Margin = new Thickness(0, 14, 0, 0);
        summary.Padding = new Thickness(20, 16, 20, 16);
        summary.Child = _summaryText;
        stack.Children.Add(summary);

        _page3.Children.Add(stack);
    }

    private UIElement BuildFooter()
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var skip = MakeQuietButton("跳过引导");
        skip.HorizontalAlignment = HorizontalAlignment.Left;
        skip.Click += (_, _) => Commit(skip: true, closeDialog: true, touchStartupRegistry: false);
        grid.Children.Add(skip);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _prevBtn.Margin = new Thickness(0, 0, 8, 0);
        right.Children.Add(_prevBtn);
        right.Children.Add(_nextBtn);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private void BeginScan(bool fromUser)
    {
        if (AppPaths.IsIsolatedTest)
        {
            RebuildAgentLists();
            return;
        }

        if (!LongActionTracker.TryBegin(LongActionTracker.ScanLocal, "扫描中"))
        {
            if (fromUser)
            {
                _scanHint.Text = LongActionTracker.DuplicateClickHint(LongActionTracker.ScanLocal);
                return;
            }
            WatchForeignScan();
            return;
        }

        LongActionTracker.ShowBusy(_rescanBtn);
        _scanHint.Text = "正在查找这台电脑上的助手…";
        _detectedList.IsEnabled = false;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                _discovery.RunDiscovery(_reg);
            }
            catch
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _detectedList.IsEnabled = true;
                    _scanHint.Text = "这次没扫成，可以再点一次";
                    LongActionTracker.EndAndRestore(LongActionTracker.ScanLocal);
                });
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                _detectedList.IsEnabled = true;
                RebuildAgentLists();
                RebuildStep2();
                int found = _reg.UserConfig.Agents.Count(a => a.Detected);
                _scanHint.Text = found > 0 ? $"找到 {found} 个可用助手" : "这台电脑上还没发现助手";
                LongActionTracker.EndAndRestore(LongActionTracker.ScanLocal);
            });
        });
    }

    private void WatchForeignScan()
    {
        _scanHint.Text = "正在查找这台电脑上的助手…";
        _foreignScanTimer?.Stop();
        _foreignScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _foreignScanTimer.Tick += (_, _) =>
        {
            if (LongActionTracker.IsBusy(LongActionTracker.ScanLocal)) return;
            _foreignScanTimer?.Stop();
            RebuildAgentLists();
            RebuildStep2();
            int found = _reg.UserConfig.Agents.Count(a => a.Detected);
            _scanHint.Text = found > 0 ? $"找到 {found} 个可用助手" : "这台电脑上还没发现助手";
        };
        _foreignScanTimer.Start();
    }

    private void RebuildAgentLists()
    {
        _detectedList.Children.Clear();
        _missingList.Children.Clear();
        var agents = _reg.UserConfig.Agents ?? new List<AgentRosterEntry>();
        var detected = agents.Where(a => a.Detected).OrderBy(a => a.Name).ToList();
        var missing = agents.Where(a => !a.Detected).OrderBy(a => a.Name).ToList();

        if (detected.Count == 0)
        {
            _detectedList.Children.Add(new TextBlock
            {
                Text = "还没有发现已安装的助手。可以点右上角再找一次。",
                Foreground = Brush("#8EA0B8"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            });
        }
        else
        {
            foreach (var ag in detected)
                _detectedList.Children.Add(BuildAgentCard(ag, interactive: true));
        }

        foreach (var ag in missing)
            _missingList.Children.Add(BuildAgentCard(ag, interactive: false));
    }

    private UIElement BuildAgentCard(AgentRosterEntry ag, bool interactive)
    {
        var card = Card();
        card.Padding = new Thickness(14, 10, 14, 10);
        card.Margin = new Thickness(0, 0, 0, 8);
        card.Opacity = interactive ? 1 : 0.72;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        try { icon.Source = OfficialBrandIconService.GetOfficialIcon(ag.Id, ag); }
        catch { /* 没有图标时只留名字 */ }
        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(ag.Name) ? ag.Id : ag.Name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F8FAFC"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var toggle = new CheckBox
        {
            IsChecked = ag.Enabled,
            IsEnabled = interactive,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 2, 0),
            LayoutTransform = new ScaleTransform(1.25, 1.25)
        };
        if (interactive)
        {
            toggle.Checked += (_, _) => ag.Enabled = true;
            toggle.Unchecked += (_, _) => ag.Enabled = false;
        }
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        var body = new StackPanel();
        body.Children.Add(grid);
        body.Children.Add(new TextBlock
        {
            Text = ShortRole(ag),
            FontSize = 12.5,
            Foreground = Brush("#8EA0B8"),
            Margin = new Thickness(44, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        card.Child = body;
        return card;
    }

    private void RebuildStep2()
    {
        _building = true;
        try
        {
            _masterCombo.Items.Clear();
            _workerWrap.Children.Clear();
            var agents = CandidateAgents();
            string current = _reg.UserConfig.GlobalMasterPlannerId ?? "";
            ComboBoxItem? selected = null;
            foreach (var ag in agents)
            {
                var item = new ComboBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(ag.Name) ? ag.Id : ag.Name,
                    Tag = ag.Id
                };
                _masterCombo.Items.Add(item);
                if (string.Equals(ag.Id, current, StringComparison.OrdinalIgnoreCase))
                    selected = item;
            }
            if (selected != null) _masterCombo.SelectedItem = selected;

            foreach (var ag in agents)
            {
                var chip = new CheckBox
                {
                    Content = string.IsNullOrWhiteSpace(ag.Name) ? ag.Id : ag.Name,
                    IsChecked = ag.Enabled,
                    FontSize = 13,
                    Foreground = Brush("#E6EDF7"),
                    Margin = new Thickness(0),
                    Padding = new Thickness(8, 3, 8, 3)
                };
                var captured = ag;
                chip.Checked += (_, _) => captured.Enabled = true;
                chip.Unchecked += (_, _) => captured.Enabled = false;
                var shell = new Border
                {
                    CornerRadius = new CornerRadius(16),
                    Background = Brush("#172233"),
                    BorderBrush = Brush("#2C415C"),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(2, 1, 6, 1),
                    Child = chip
                };
                _workerWrap.Children.Add(shell);
            }
        }
        finally
        {
            _building = false;
        }
    }

    private void ApplyRecommendedSplit()
    {
        var pool = CandidateAgents();
        if (pool.Count == 0)
        {
            _step2Note.Text = "还没发现可用助手";
            return;
        }

        var chosen = pool.Where(a => a.Enabled).ToList();
        if (chosen.Count == 0)
        {
            var ranked = pool.OrderByDescending(RoutingService.ComputeMasterPlannerPowerScore).ToList();
            ranked[0].Enabled = true;
            chosen.Add(ranked[0]);
            foreach (var id in new[] { "cursor", "codex_cli", "windsurf", "claude_code", "grok_bot" })
            {
                if (chosen.Count >= 3) break;
                var hit = pool.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
                if (hit == null || chosen.Any(a => string.Equals(a.Id, hit.Id, StringComparison.OrdinalIgnoreCase))) continue;
                hit.Enabled = true;
                chosen.Add(hit);
            }
        }

        var master = chosen.OrderByDescending(RoutingService.ComputeMasterPlannerPowerScore).First();
        master.Enabled = true;
        AssignMaster(master.Id);
        var backup = chosen.FirstOrDefault(a =>
                         !string.Equals(a.Id, master.Id, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(a.Id, "cursor", StringComparison.OrdinalIgnoreCase))
                     ?? chosen.FirstOrDefault(a => !string.Equals(a.Id, master.Id, StringComparison.OrdinalIgnoreCase));
        _reg.UserConfig.GlobalFallbackBuilderId = backup?.Id ?? "";
        var active = ActiveProfile();
        if (active != null) active.FallbackBuilderId = _reg.UserConfig.GlobalFallbackBuilderId ?? "";
        RebuildStep2();
        _step2Note.Text = "主脑派活，勾选的人来做";
    }

    private List<AgentRosterEntry> CandidateAgents()
    {
        var agents = _reg.UserConfig.Agents ?? new List<AgentRosterEntry>();
        var detected = agents.Where(a => a.Detected).OrderBy(a => a.Name).ToList();
        if (detected.Count > 0) return detected;
        var enabled = agents.Where(a => a.Enabled).OrderBy(a => a.Name).ToList();
        return enabled.Count > 0 ? enabled : agents.OrderBy(a => a.Name).ToList();
    }

    private void AssignMaster(string agentId)
    {
        _reg.UserConfig.GlobalMasterPlannerId = agentId;
        foreach (var project in _reg.UserConfig.Projects ?? new List<ProjectProfile>())
            project.MasterPlannerId = agentId;
        var active = ActiveProfile();
        if (active != null) active.MasterPlannerId = agentId;
    }

    private TeamConfigProfile? ActiveProfile()
    {
        var profiles = _reg.UserConfig.TeamProfiles;
        if (profiles == null || profiles.Count == 0) return null;
        return profiles.FirstOrDefault(p =>
                   string.Equals(p.Id, _reg.UserConfig.ActiveTeamProfileId, StringComparison.OrdinalIgnoreCase))
               ?? profiles[0];
    }

    private void RefreshSummary()
    {
        var agents = _reg.UserConfig.Agents ?? new List<AgentRosterEntry>();
        int n = agents.Count(a => a.Enabled);
        string master = CurrentMasterName();
        _summaryText.Text =
            $"当前主脑：{master}\n" +
            $"已启用执行成员：{n} 个\n" +
            "之后随时可在左侧「AI 配置」修改";
    }

    private string CurrentMasterName()
    {
        var id = _reg.UserConfig.GlobalMasterPlannerId;
        if (string.IsNullOrWhiteSpace(id) || id.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "自动挑选";
        var ag = _reg.UserConfig.Agents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (ag == null) return "还没选定";
        return string.IsNullOrWhiteSpace(ag.Name) ? ag.Id : ag.Name;
    }

    private static string ShortRole(AgentRosterEntry ag)
    {
        string text = (ag.Id ?? "").ToLowerInvariant() switch
        {
            "cursor" => "改项目文件的主力",
            "antigravity" => "适合做总指挥",
            "grok_bot" => "适合出主意",
            "codex_cli" => "后台写代码",
            "claude_code" => "终端里写代码",
            "claude_desktop" => "适合深想难题",
            "chatgpt_desktop" => "聊天和写文案",
            "gemini_cli" => "终端里帮忙",
            "windsurf" => "另一个写代码窗",
            "trae" => "写代码的窗口",
            "kiro" => "写代码的窗口",
            "ollama_local" => "本机离线模型",
            "lm_studio" => "本机离线模型",
            "vscode_copilot" or "copilot_cli" => "补全和改代码",
            _ => "本机上的助手"
        };
        return text.Length <= 18 ? text : text[..18];
    }

    private static Border Card() => new()
    {
        Background = Brush("#141C2B"),
        BorderBrush = Brush("#243044"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12)
    };

    private static Button MakePrimaryButton(string text)
    {
        var btn = new Button
        {
            Content = text,
            Height = 36,
            MinWidth = 108,
            Padding = new Thickness(16, 0, 16, 0),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Background = Brush("#2563EB"),
            BorderBrush = Brush("#1D4ED8"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        if (Application.Current?.TryFindResource("PrimaryBtn") is Style style)
            btn.Style = style;
        return btn;
    }

    private static Button MakeQuietButton(string text) => new()
    {
        Content = text,
        Height = 36,
        MinWidth = 88,
        Padding = new Thickness(12, 0, 12, 0),
        FontSize = 13.5,
        Foreground = Brush("#C5D0E0"),
        Background = Brushes.Transparent,
        BorderBrush = Brush("#31445F"),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand
    };

    private static SolidColorBrush Brush(string hex) => UiBrushPool.Get(hex);
}
