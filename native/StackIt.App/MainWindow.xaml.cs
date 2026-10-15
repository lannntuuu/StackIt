using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;   // DropShadowEffect（底部条那枚常驻浮层投影）
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StackIt.App.UI;
using StackIt.App.Views;
using StackIt.Core;

namespace StackIt.App;

/// <summary>
/// 主窗口。持有数据与当前页签，三个页面都是纯 UI，各自拿 Db 去画。
/// 数据改动一律走 Core.Store，改完立即 Refresh，界面跟着重画。
/// </summary>
public partial class MainWindow : Window
{
    public Db Db { get; private set; }
    public string CurrentTab { get; private set; } = "stats";

    /// <summary>真退出时才置位。托盘菜单的「退出」和关掉托盘图标都走这里。</summary>
    private bool _reallyQuitting;

    /// <summary>
    /// 是否已经走过真退出。给托盘自检用：点 X 之后它必须仍是 false，
    /// 这是"隐藏 ≠ 退出"的直接证据。
    /// </summary>
    internal static bool ShutdownRequested { get; private set; }

    private readonly System.Windows.Forms.NotifyIcon _tray = new();
    /// <summary>托盘最后一次按下的键，用来把右键触发的 Click 挡掉（右键只出菜单）。</summary>
    private System.Windows.Forms.MouseButtons _trayLastButton;

    public MainWindow()
    {
        InitializeComponent();
        Db = Store.Load();

        // 筛选条在统计页和时间流都有，点哪儿都要重画。
        // DateFilter.Set 内部会先比对再触发，不会重复刷新。
        Views.DateFilter.Changed += () => Refresh();

        SetupTray();
        SetupWindowIcon();

        // Ctrl+Enter 唤起「记一笔」。底部那颗键帽提示说的就是这个键，
        // 提示必须真的能用 —— 本项目底线是界面上写出来的东西不能是假的。
        KeyDown += OnGlobalKeyDown;

        // 换纸要**立刻全窗口生效**、不用重开页面：颜色那一半靠 DynamicResource 天然成立；
        // 剩下几样不是资源键能带着走的（卡片皮肤是 Style 实例、投影是解析期快照），
        // 只能重刷常驻浮层 + 重建一次当前页才会换过来。
        Theme.PaperChanged += _ => { ApplyChrome(); Refresh(); };

        ApplyChrome();
        ShowPage("stats");
    }

    /// <summary>
    /// 把「不跟着资源键走」的那几样按当前纸底重算一次。
    ///
    /// 底部条是全站唯一的常驻浮层：浅纸上是一道暖棕投影，深纸上投影墨色换成
    /// 近黑的暖棕、并且只留这一层（卡片层改用 1px 描边 + 顶高光，见 CardSkin）。
    /// 浅纸那组数与改之前的 XAML 声明逐条一致（depth 4 / blur 20 / dir 90 / opacity .30），
    /// 所以默认主题的观感没有变。
    /// </summary>
    private void ApplyChrome()
    {
        var p = Theme.CurrentPaper;
        BottomBar.Effect = new DropShadowEffect
        {
            Color = p.ShadowInk,
            ShadowDepth = 4,
            BlurRadius = 20,
            // 光源固定左上（Direction 是投影的方向）：这条压在页面区之上，
            // 所以朝上 —— 底部条是唯一的一处，不是随手写反。
            Direction = 90,
            Opacity = p.IsDark ? 0.55 : 0.30,
            RenderingBias = RenderingBias.Performance,
        };
    }

    /// <summary>
    /// 窗口图标。
    ///
    /// Window.Icon 要的是 ImageSource，不是 System.Drawing.Icon（那个给托盘用）。
    /// 从图标资源读 ICO 再转成位图：直接 Icon="app.ico" 写在 XAML 里也行，
    /// 但那要求它是 WPF Resource 项；写成 None 的话运行时会抛
    /// XamlParseException: 找不到资源，直接让程序起不来。这里放代码里，
    /// 顺带还能兜住"图标文件不在"的情况。
    /// </summary>
    private void SetupWindowIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/app.ico");
            var img = new BitmapImage(uri);
            img.Freeze();
            Icon = img;
        }
        catch
        {
            // 提不出来就算了，窗口图标不值得为此让程序起不来
        }
    }

    /// <summary>
    /// 托盘图标：点右上角关闭只是收起来，程序继续跑，数据随时能补记。
    ///
    /// 一个必须显式处理的地方：ShutdownMode 必须是 OnExplicitShutdown ——
    /// 默认的 OnLastWindowClose 会在最后一个窗口关闭时退出程序，
    /// 隐藏窗口等于直接退出，而且不留任何报错。
    ///
    /// 托盘注册要经过 Explorer（Shell_NotifyIcon 往它的通知区域塞图标）。
    /// 没有 explorer.exe 的环境（服务、某些沙箱、部分远程会话）里注册会静默失败：
    /// 不抛异常、不打日志，只是图标永远不出现。所以这里把 Visible
    /// 设完立刻回读确认，失败就告诉用户，不让人以为程序坏了。
    /// </summary>
    private void SetupTray()
    {
        // 必须存成字段：Icon 包着 HICON，局部变量会被 GC 终结，
        // HICON 一销毁，托盘上的图标就变成空白（现象是"托盘没有图标"）。
        _trayIcon = LoadTrayIcon();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(MenuItem("显示主窗口", () => Restore()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(MenuItem("退出", Quit));

        _tray.Icon = _trayIcon;
        _tray.Text = "StackIt";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;

        TrayRegistered = _tray.Visible;
        TrayIconSize = _trayIcon?.ToBitmap()?.Width ?? 0;

        // 双击托盘图标 = 显示窗口（单击不响应：单击容易误触）
        _tray.DoubleClick += (_, _) => Restore();

        // 单击也响应。用户找不到图标时会去点；单击不响应的话，
        // 用户会以为图标不存在。单击叫回窗口是任何托盘程序都这么做的，
        // 不算意外行为。
        // 但右键必须挡掉：WinForms 的 NotifyIcon 右键时会依次抛
        // MouseDown(Right) → MouseUp(Right) → Click，而 ContextMenuStrip 是自己在
        // MouseUp 里弹出来的。不在 Click 这一刀上分辨按键，右键就会「菜单弹出来，
        // 同时窗口也叫开了」，窗口正好盖在菜单下面 —— 而用户要的只是菜单。
        _tray.MouseUp += (_, e) => _trayLastButton = e.Button;
        _tray.Click += (_, _) =>
        {
            var b = _trayLastButton;
            _trayLastButton = System.Windows.Forms.MouseButtons.None;
            if (b == System.Windows.Forms.MouseButtons.Right) return;
            Restore();
        };

        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!TrayRegistered)
            TrayError =
                "托盘图标没能注册成功。通常是因为 explorer.exe（Windows 资源管理器）没在运行，" +
                "或当前是服务 / 远程会话等没有通知区域的环境。" +
                "这种情况下点右上角关闭仍会把程序收起来，只是没法从托盘叫回来，" +
                "得用任务管理器结束 —— 这种情况请告诉我，我改回直接退出。";
    }

    /// <summary>托盘图标是否真的注册上了。见 SetupTray 的说明。</summary>
    internal static bool TrayRegistered { get; private set; }

    /// <summary>托盘图标实际取到的像素尺寸。0 表示没取到（会是空白）。</summary>
    internal static int TrayIconSize { get; private set; }

    /// <summary>注册失败时给用户看的原因；成功则为空。</summary>
    internal static string TrayError { get; private set; } = "";

    /// <summary>
    /// 自检/离屏模式下不弹任何对话框。
    /// MessageBox 会阻塞等输入，自动化跑的时候就是一个永远不返回的进程 ——
    /// 表现为"回归脚本卡在这一步不动了"，而不是干脆地失败。
    /// </summary>
    internal static bool SuppressDialogs { get; set; }

    private static System.Windows.Forms.ToolStripMenuItem MenuItem(string text, Action click)
    {
        var mi = new System.Windows.Forms.ToolStripMenuItem(text);
        mi.Click += (_, _) => click();
        return mi;
    }

    /// <summary>
    /// 托盘图标。
    ///
    /// 这里踩了两个坑：
    ///
    /// 1. **不能用 ExtractAssociatedIcon 的返回值当局部变量**。它包着一个
    ///    HICON，方法一返回局部变量就成了垃圾；GC 回收时 Icon 被终结、
    ///    HICON 被销毁，托盘上那个图标就变成空白 —— 现象正好是"托盘没有图标"。
    ///    所以必须存成字段，让它活到进程结束。
    /// 2. **从 exe 提取只能拿到 32x32 单尺寸**，托盘和任务栏缩放时会糊。
    ///    直接读 app.ico（7 个尺寸都在里面），各取所需。
    /// </summary>
    private System.Drawing.Icon? _trayIcon;

    private System.Drawing.Icon LoadTrayIcon()
    {
        // 从嵌入的 ico 读字节，构造多尺寸图标
        try
        {
            var uri = new Uri("pack://application:,,,/app.ico");
            var info = Application.GetResourceStream(uri);
            if (info != null)
            {
                using var s = info.Stream;
                var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                return new System.Drawing.Icon(ms);
            }
        }
        catch
        {
            // 读不到就退回 exe 提取
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch
        {
            // 提不出来就用系统图标，不至于没图标
        }

        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>从托盘把窗口叫回来。</summary>
    public void Restore()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>真退出。菜单点「退出」或托盘图标右键关闭都走这里。</summary>
    public void Quit()
    {
        _reallyQuitting = true;
        ShutdownRequested = true;
        _tray.Visible = false;
        _tray.Dispose();
        // 托盘图标必须活到这一刻才释放。提前释放的话，最后一次绘制用的
        // HICON 已经没了，图标会在消失前闪一下空白。
        _trayIcon?.Dispose();
        _trayIcon = null;
        // 交还单实例锁。必须活到真正退出这一刻：提前释放会让第二个进程乘虚而入。
        (Application.Current as App)?.ReleaseSingle();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 数据变了要重画当前页。页面自己订阅不到，统一从这里推。
    ///
    /// 一律推迟到当前事件派发结束之后执行。原因：几乎所有调用都发生在
    /// 某个按钮自己的 Click 回调里，而重画会把那个按钮连同整棵可视树拆掉。
    /// 在事件派发中途失去源元素，WPF 的后续行为不可预期（弹窗不显示、
    /// 焦点丢失、偶发卡住）。推到 Dispatcher 队列尾，让回调先正常返回。
    /// </summary>
    public void Refresh()
    {
        if (Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshNow),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        else
        {
            Dispatcher.Invoke(RefreshNow);
        }
    }

    /// <summary>立刻重画。只在没有消息循环可推时用（离屏渲染、自检）。</summary>
    public void RefreshNow() => ShowPage(CurrentTab);

    /// <summary>切页。离屏渲染与页签点击都走这里。</summary>
    public void GoTo(string tab) => ShowPage(tab);

    /// <summary>
    /// 切页，但推迟到当前事件处理结束之后。
    /// 给"在被销毁的页面自己的回调里要求切页"用，见 Refresh 的说明。
    /// </summary>
    public void GoToDeferred(string tab)
    {
        if (Dispatcher.CheckAccess())
            Dispatcher.BeginInvoke(new Action(() => ShowPage(tab)),
                System.Windows.Threading.DispatcherPriority.Background);
        else
            Dispatcher.Invoke(() => ShowPage(tab));
    }

    private void ShowPage(string tab)
    {
        CurrentTab = tab;
        try
        {
            PageHost.Content = tab switch
            {
                "stats" => new StatsView(this),
                "timeline" => new TimelineView(this),
                _ => new DataView(this),
            };
        }
        catch (Exception ex)
        {
            // 页面的构造函数跑在 UI 线程上。任何一个抛异常都会顺着点击事件
            // 冒到调度器，表现是窗口卡住不响应。
            // 所以这里就地兜住：记日志、把页面换成一句说明，应用继续能用。
            LogPageError(tab, ex);
            PageHost.Content = ErrorPanel(tab, ex);
            return;
        }

        Highlight(tab);
        ScrollToTop();
    }

    /// <summary>页面构建失败时的替身页。说出哪个页面、报了什么，并给出可执行的下一步。</summary>
    private UIElement ErrorPanel(string tab, Exception ex)
    {
        var name = tab switch
        {
            "stats" => "统计",
            "timeline" => "时间流",
            _ => "数据",
        };

        var sp = new StackPanel { Margin = new Thickness(20, 24, 20, 24) };

        // 颜色一律 SetResourceReference（= DynamicResource）。
        // 这里原来写的是 (Brush)Application.Current.Resources[key]：取的是**那一刻的实例**，
        // 换纸底 / 换配色之后这张错误页会留在旧色上 —— 而它偏偏是出事后才被人看到的那一页。
        var title = new TextBlock
        {
            Text = $"{name}页打不开",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink);
        sp.Children.Add(title);

        var note = new TextBlock
        {
            Text = "应用本身没问题，换个页签可以继续用。",
            FontSize = 13,
            Margin = new Thickness(0, 6, 0, 12),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, Theme.Ink3);
        sp.Children.Add(note);

        var detail = new TextBlock
        {
            Text = ex.Message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 19,
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, Theme.Danger);

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = M.R,
            Padding = new Thickness(12, 10, 12, 10),
            Child = detail,
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.DangerSoft);
        card.SetResourceReference(Border.BorderBrushProperty, Theme.Danger);
        sp.Children.Add(card);

        return sp;
    }

    private static void LogPageError(string tab, Exception ex)
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            File.AppendAllText(
                Path.Combine(dir, "StackIt", "startup-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 构建页面 {tab} 失败: {ex}\r\n\r\n");
        }
        catch
        {
            // 日志写不了就算了，别因此再抛一次
        }
    }

    /// <summary>
    /// 当前页签指示：文字转 Ink + 加粗（由样式里 ui:TabHost.IsCurrent 触发器负责）
    /// + 下方 20×2 短横用 scaleX 长出来。
    ///
    /// 为什么这里不再写 b.Foreground = ...：那是**本地值**，优先级高于样式里的
    /// IsCurrent / IsMouseOver 触发器 —— 当前页签会既看不到 hover 反应，
    /// 又从此不跟着配色换（切强调色时它留在旧色上）。代码只说"哪个是当前"。
    /// </summary>
    private void Highlight(string tab)
    {
        Mark(TabStats, TabStatsInd, "stats", tab);
        Mark(TabTimeline, TabTimelineInd, "timeline", tab);
        Mark(TabData, TabDataInd, "data", tab);
    }

    private static void Mark(Button b, FrameworkElement ind, string key, string current)
    {
        var on = key == current;
        TabHost.SetIsCurrent(b, on);
        Motion.ScaleX(ind, on ? 1.0 : 0.0);
    }

    /// <summary>切页后回到顶部，不然会停在上一个页面的滚动位置。</summary>
    private void ScrollToTop()
    {
        // 时间流页外层禁用了滚动、真正的滚动条在内层 ListBox 上，
        // 所以不能只看最外层：先试外层，再找里面第一个可滚动的。
        if (PageHost.Content is ScrollViewer sv && sv.ScrollableHeight > 0)
        {
            sv.ScrollToTop();
            return;
        }

        if (PageHost.Content is DependencyObject root)
        {
            var inner = FindScrollable(root);
            inner?.ScrollToTop();
        }
    }

    /// <summary>深度优先找第一个能滚的 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollable(DependencyObject root)
    {
        if (root is ScrollViewer { ScrollableHeight: > 0 } sv) return sv;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var hit = FindScrollable(VisualTreeHelper.GetChild(root, i));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>
    /// 点右上角关闭 = 收进托盘，不是退出。
    /// 这是个记录工具：随手记东西的应用，最常见的使用姿势是开着窗口
    /// 记一笔、关掉、继续过日子。退出应该由用户明确说，而不是顺手一点。
    ///
    /// 真退出走托盘菜单。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyQuitting)
        {
            // 托盘没注册上时不能藏 —— 藏了用户就没法再打开，
            // 只能去任务管理器杀进程，数据还没存就丢了。
            // 这种情况老老实实退出，并说清楚为什么。
            if (!TrayRegistered)
            {
                base.OnClosing(e);
                if (!TrayErrorShown && !SuppressDialogs)
                {
                    TrayErrorShown = true;
                    MessageBox.Show(
                        TrayError + "\n\n这次将直接退出。",
                        "StackIt", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            // 取消关闭，改为隐藏。第一次隐藏时给一句提示，
            // 否则用户会以为程序崩了或者根本没启动成功。
            e.Cancel = true;
            Hide();

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(
                    4000, "StackIt",
                    "已收进托盘，点图标可以再打开。右键托盘图标可以退出。",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        base.OnClosing(e);
    }

    private bool _trayHintShown;
    private static bool TrayErrorShown;

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
            ShowPage(tag);
    }

    private void OnRecordClick(object sender, RoutedEventArgs e) => OpenRecordSheet();

    private void OnGlobalKeyDown(object sender, KeyEventArgs e)
    {
        // Key.Enter 与 Key.Return 在 WPF 里是同一个值，写一个就够。
        if (e.Key != Key.Return || Keyboard.Modifiers != ModifierKeys.Control) return;
        OpenRecordSheet();
        e.Handled = true;
    }

    private void OpenRecordSheet()
    {
        var sheet = new RecordSheet(this);
        sheet.ShowDialog();
        // 弹层里可能记了东西，关掉后重画
        Refresh();
    }

    /* ---------- 给页面用的写操作 ---------- */

    /// <summary>
    /// 落盘失败的统一处理。Store 现在可能抛 SaveFailedException，而且抛之前
    /// 已经把内存回滚到"没改之前"了（见 Store.Restore）——所以这次改动
    /// 真的没有发生，界面重画后用户看到的就是改之前的记录，不会有
    /// "看着记上了其实没存"的静默不一致。
    ///
    /// 这里：自检/离屏模式直接吞掉（模态框会永久阻塞自检），
    /// 否则弹窗说清这一笔没存上、没存上的是什么、以及怎么才能重试。
    /// 返回 false 表示已被吞掉、调用方应直接 return。
    /// </summary>
    private bool FailSave(Store.SaveFailedException ex)
    {
        if (SuppressDialogs) return false;   // 自检/离屏：不弹框、不抛，避免卡死
        MessageBox.Show(
            "这一笔没存上，已经撤销了。\n\n" +
            ex.Message +
            "\n\n处理办法：确认磁盘还有空间、" + Store.DataDir + " 目录可写、" +
            "没有别的程序正占着 db.json，然后重新记一次。" +
            "\n之前已经存上的记录不受影响。",
            "StackIt", MessageBoxButton.OK, MessageBoxImage.Warning);
        Refresh();
        return true;
    }

    /// <summary>
    /// 记一条。id 由调用方给，这样弹层能记住自己加了哪几条以便撤销。
    /// </summary>
    public void AddEvent(string id, string itemId, When when, int? dose, string? slot)
    {
        try
        {
            Db = Store.AddEvent(Db, new IntakeEvent
            {
                Id = id,
                ItemId = itemId,
                When = when,
                Dose = dose,
                Slot = slot,
                CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            });
        }
        catch (Store.SaveFailedException ex)
        {
            if (!FailSave(ex)) return;
        }
    }

    public void RemoveEvent(string id)
    {
        try { Db = Store.RemoveEvent(Db, id); }
        catch (Store.SaveFailedException ex) { if (!FailSave(ex)) return; }
    }

    public void UpdateEvent(string id, string? itemId, When? when, int? dose, string? slot)
    {
        try { Db = Store.UpdateEvent(Db, id, itemId, when, dose, slot); }
        catch (Store.SaveFailedException ex) { if (!FailSave(ex)) return; }
    }

    public void AddObservation(string kind, string subject, string? text)
    {
        try
        {
            Db = Store.AddObservation(Db, new Observation
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Kind = kind,
                Subject = subject,
                Severity = null,
                When = new When { Date = DateTime.Now.ToString("yyyy-MM-dd"), Time = null, Precision = "day" },
                Text = string.IsNullOrWhiteSpace(text) ? null : text,
                CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            });
        }
        catch (Store.SaveFailedException ex) { if (!FailSave(ex)) return; }
    }

    public void RemoveObservation(string id)
    {
        try { Db = Store.RemoveObservation(Db, id); }
        catch (Store.SaveFailedException ex) { if (!FailSave(ex)) return; }
    }

    public void UpdateObservation(string id, string? kind, string? subject, string? text, bool clear)
    {
        try { Db = Store.UpdateObservation(Db, id, kind, subject, text, clear); }
        catch (Store.SaveFailedException ex) { if (!FailSave(ex)) return; }
    }

    /// <summary>导入覆盖全部数据。</summary>
    public void ReplaceDb(Db next)
    {
        try
        {
            Store.ReplaceAll(next);
            Db = next;
        }
        catch (Store.SaveFailedException ex)
        {
            if (!FailSave(ex)) return;
        }
    }

    /// <summary>导出到指定路径。</summary>
    public void Export(string path, bool asMarkdown)
    {
        try
        {
            File.WriteAllText(path, asMarkdown ? Store.ToMarkdown(Db) : Store.ToJson(Db),
                System.Text.Encoding.UTF8);
        }
        catch (Store.SaveFailedException ex)
        {
            // 导出本身不调 Store 写盘，理论上不抛 SaveFailedException；
            // 仍兜住，保持与其它写操作一致，避免漏掉提示。
            if (!FailSave(ex)) return;
        }
    }
}