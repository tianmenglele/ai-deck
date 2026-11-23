using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AICollabHub.UI;

namespace AICollabHub.Services;

public sealed class TrayIconService : IDisposable
{
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int WM_USER = 0x0400;
    private const int WM_TRAYCALLBACK = WM_USER + 2026;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIconW(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private HwndSource? _hwndSource;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _added;
    private readonly Action _onOpenMain;
    private readonly Action _onToggleFloating;
    private readonly Action _onToggleWorkflow;
    private readonly Action _onExitApp;
    private readonly Func<bool> _isWorkflowEnabled;

    public TrayIconService(
        Action onOpenMain,
        Action onToggleFloating,
        Action onToggleWorkflow,
        Action onExitApp,
        Func<bool> isWorkflowEnabled)
    {
        _onOpenMain = onOpenMain;
        _onToggleFloating = onToggleFloating;
        _onToggleWorkflow = onToggleWorkflow;
        _onExitApp = onExitApp;
        _isWorkflowEnabled = isWorkflowEnabled;
    }

    public static string? ResolveTrayIconFile()
    {
        var diskIco = Path.Combine(AppPaths.AppBaseDir, "Assets", "app_icon.ico");
        if (File.Exists(diskIco)) return diskIco;

        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app_icon.ico", UriKind.Absolute));
            if (res?.Stream != null)
            {
                using var s = res.Stream;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                if (bytes.Length > 0)
                {
                    Directory.CreateDirectory(AppPaths.UserDataDir);
                    var cachedIco = Path.Combine(AppPaths.UserDataDir, "app_icon_tray.ico");
                    if (!File.Exists(cachedIco) || new FileInfo(cachedIco).Length != bytes.Length)
                    {
                        File.WriteAllBytes(cachedIco, bytes);
                    }
                    return cachedIco;
                }
            }
        }
        catch { }
        return null;
    }

    public void Initialize(Window ownerWindow)
    {
        if (AppPaths.IsIsolatedTest) return;
        try
        {
            var helper = new WindowInteropHelper(ownerWindow);
            var hwnd = helper.EnsureHandle();
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);

            var icoPath = ResolveTrayIconFile();
            if (!string.IsNullOrWhiteSpace(icoPath) && File.Exists(icoPath))
            {
                _hIcon = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            }
            if (_hIcon == IntPtr.Zero && !string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
            {
                _hIcon = ExtractIconW(IntPtr.Zero, Environment.ProcessPath, 0);
            }
            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
            }

            var nid = CreateData(hwnd);
            _added = Shell_NotifyIconW(NIM_ADD, ref nid);
        }
        catch { }
    }

    public void UpdateTooltip(string tip)
    {
        if (!_added || _hwndSource == null) return;
        try
        {
            var nid = CreateData(_hwndSource.Handle, tip);
            Shell_NotifyIconW(NIM_MODIFY, ref nid);
        }
        catch { }
    }

    private NOTIFYICONDATAW CreateData(IntPtr hwnd, string? customTip = null)
    {
        bool wfOn = _isWorkflowEnabled();
        var tip = customTip ?? (wfOn
            ? "AIDeck · 多 AI 协同中心 · 工作流运行中（左键打开主界面 / 右键菜单）"
            : "AIDeck · 多 AI 协同中心 · 工作流已暂停（各 AI 独立运行中）");
        if (tip.Length > 120) tip = tip[..120];
        return new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYCALLBACK,
            hIcon = _hIcon,
            szTip = tip
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYCALLBACK)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            if (ev == WM_LBUTTONUP || ev == WM_LBUTTONDBLCLK)
            {
                _onOpenMain();
                handled = true;
            }
            else if (ev == WM_RBUTTONUP)
            {
                ShowTrayContextMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowTrayContextMenu()
    {
        bool wfOn = _isWorkflowEnabled();

        // 自定义现代深色圆角 ContextMenu 模板（彻底消除 WPF 默认左侧大白条与老式边框）
        var menuTemplate = new ControlTemplate(typeof(ContextMenu));
        var outerBd = new FrameworkElementFactory(typeof(Border));
        outerBd.SetValue(Border.BackgroundProperty, UiBrushPool.Get("#111827"));
        outerBd.SetValue(Border.BorderBrushProperty, UiBrushPool.Get("#243248"));
        outerBd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        outerBd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        outerBd.SetValue(Border.PaddingProperty, new Thickness(5));
        outerBd.SetValue(Border.SnapsToDevicePixelsProperty, true);
        var itemsHost = new FrameworkElementFactory(typeof(StackPanel));
        itemsHost.SetValue(Panel.IsItemsHostProperty, true);
        itemsHost.SetValue(System.Windows.Input.KeyboardNavigation.DirectionalNavigationProperty, System.Windows.Input.KeyboardNavigationMode.Cycle);
        outerBd.AppendChild(itemsHost);
        menuTemplate.VisualTree = outerBd;

        var menu = new ContextMenu
        {
            Template = menuTemplate,
            HasDropShadow = true,
            MinWidth = 236,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 12.5
        };

        Style BuildItemStyle(string hoverBgHex, string fgHex)
        {
            var st = new Style(typeof(MenuItem));
            st.Setters.Add(new Setter(Control.ForegroundProperty, UiBrushPool.Get(fgHex)));
            st.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
            st.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));

            var tpl = new ControlTemplate(typeof(MenuItem));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "ItemBd";
            bd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.SetValue(Border.PaddingProperty, new Thickness(10, 7, 10, 7));

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            var hoverTrig = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            hoverTrig.Setters.Add(new Setter(Border.BackgroundProperty, UiBrushPool.Get(hoverBgHex), "ItemBd"));
            tpl.Triggers.Add(hoverTrig);

            st.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return st;
        }

        UIElement MakeRowHeader(string dotOrGlyph, string glyphHex, string title, string? rightSub = null, bool bold = false)
        {
            var g = new Grid { MinWidth = 210 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconTxt = new TextBlock
            {
                Text = dotOrGlyph,
                Foreground = UiBrushPool.Get(glyphHex),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var titleTxt = new TextBlock
            {
                Text = title,
                FontSize = 12.2,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 8, 0)
            };
            Grid.SetColumn(iconTxt, 0);
            Grid.SetColumn(titleTxt, 1);
            g.Children.Add(iconTxt);
            g.Children.Add(titleTxt);

            if (!string.IsNullOrWhiteSpace(rightSub))
            {
                var subTxt = new TextBlock
                {
                    Text = rightSub,
                    FontSize = 10.5,
                    Foreground = UiBrushPool.Get("#64748B"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(subTxt, 2);
                g.Children.Add(subTxt);
            }
            return g;
        }

        Separator MakeDivider()
        {
            var sepTpl = new ControlTemplate(typeof(Separator));
            var line = new FrameworkElementFactory(typeof(Border));
            line.SetValue(FrameworkElement.HeightProperty, 1.0);
            line.SetValue(Border.BackgroundProperty, UiBrushPool.Get("#1E293B"));
            line.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4));
            sepTpl.VisualTree = line;
            return new Separator { Template = sepTpl };
        }

        var normalStyle = BuildItemStyle("#1E2B40", "#E2E8F0");
        var wfStyle = BuildItemStyle("#162A24", wfOn ? "#4ADE80" : "#CBD5E1");
        var dangerStyle = BuildItemStyle("#2E161E", "#F87171");

        var wfItem = new MenuItem
        {
            Style = wfStyle,
            Header = MakeRowHeader("●", wfOn ? "#34D399" : "#64748B", wfOn ? "协同工作流：已启用" : "协同工作流：已关闭", wfOn ? "点击暂停" : "点击启用", bold: true)
        };
        wfItem.Click += (_, _) => _onToggleWorkflow();
        menu.Items.Add(wfItem);
        menu.Items.Add(MakeDivider());

        var openMain = new MenuItem
        {
            Style = normalStyle,
            Header = MakeRowHeader("▣", "#60A5FA", "打开主界面")
        };
        openMain.Click += (_, _) => _onOpenMain();
        menu.Items.Add(openMain);

        var toggleFloat = new MenuItem
        {
            Style = normalStyle,
            Header = MakeRowHeader("◈", "#94A3B8", "显示 / 隐藏悬浮窗")
        };
        toggleFloat.Click += (_, _) => _onToggleFloating();
        menu.Items.Add(toggleFloat);

        menu.Items.Add(MakeDivider());
        var exitItem = new MenuItem
        {
            Style = dangerStyle,
            Header = MakeRowHeader("✕", "#F87171", "彻底退出软件", "恢复原生状态")
        };
        exitItem.Click += (_, _) => _onExitApp();
        menu.Items.Add(exitItem);

        menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_added && _hwndSource != null)
        {
            try
            {
                var nid = CreateData(_hwndSource.Handle);
                Shell_NotifyIconW(NIM_DELETE, ref nid);
            }
            catch { }
            _added = false;
        }
        if (_hIcon != IntPtr.Zero)
        {
            try { DestroyIcon(_hIcon); } catch { }
            _hIcon = IntPtr.Zero;
        }
    }
}
