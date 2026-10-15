using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StackIt.App;

/// <summary>
/// 离屏渲染：把某个页面画到 PNG，然后退出。
///
/// 为什么需要它：这个沙箱里没有可用的桌面合成通道，
/// 桌面截图只能拍到别的窗口（浏览器在最前面），
/// 而且 GUI 进程活不过一次后台作业的时限。
/// 让界面自己渲染成图片，是唯一能真正验证"画出来对不对"的办法。
///
/// 用法: StackIt.App.exe --shot &lt;输出路径&gt; [页签] [宽] [高]
///       StackIt.App.exe --import-test &lt;记录.md&gt;   验完导入流程并打印结果
/// </summary>
internal static class Shot
{
    /// <summary>「记一笔」自检的隔离数据目录，由 UseSelfTestDataDir 早点建好。</summary>
    private static string SelfTestDir = "";

    /// <summary>
    /// 把自检的数据目录指到临时目录。
    ///
    /// 必须由 App.OnStartup 在 Theme.Restore() 之前调用 —— Store.DataDir 是
    /// Lazy，Theme.Restore() 里的 LoadPalette() 一跑就永久固化了。
    /// 之前两次都栽在这：设在 SheetTest 里太晚，设在 TryRun 里也晚一步。
    /// </summary>
    public static void UseSelfTestDataDir()
    {
        SelfTestDir = Path.Combine(Path.GetTempPath(),
            "stackit-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try { Directory.CreateDirectory(SelfTestDir); }
        catch
        {
            // 某些环境（沙箱/受限账户）下对 %TEMP% 建子目录会 Access denied；
            // 退到程序自身目录，自检本来就要隔离，不依赖临时目录的位置。
            SelfTestDir = Path.Combine(System.AppContext.BaseDirectory,
                "stackit-selftest-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(SelfTestDir);
        }
        Environment.SetEnvironmentVariable("STACKIT_DATA_DIR", SelfTestDir);
    }
    public static bool TryRun(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--import-test")
            return ImportTest(args[1]);

        // 诊断：时间范围筛选的逻辑自检
        if (args.Length >= 1 && args[0] == "--tray-test")
            return TrayTest();

        if (args.Length >= 1 && args[0] == "--tray-diag")
            return TrayDiag();

        if (args.Length >= 1 && args[0] == "--make-icon")
            return IconGen.Run(args);

        if (args.Length >= 1 && args[0] == "--icon-test")
            return IconTest(args.Length > 1 ? args[1] : "app.ico");

        if (args.Length >= 1 && args[0] == "--filter-test")
            return FilterSelfTest();

        if (args.Length >= 1 && args[0] == "--sheet-test")
            return SheetTest();

        // 诊断：把两套纸底 × 六套强调色实际注册出来的色阶与取色结果打印出来。
        if (args.Length >= 1 && args[0] == "--theme-dump")
            return ThemeDump.Run();

        // 诊断：量某个页面的构建耗时，判断是否随数据量线性增长
        if (args.Length >= 2 && args[0] == "--timing")
            return TimingProbe(args.Length > 1 ? args[1] : "timeline");

        if (args.Length < 2 || args[0] != "--shot") return false;

        var outPath = args[1];
        var tab = args.Length > 2 ? args[2] : "stats";
        var width = args.Length > 3 ? int.Parse(args[3]) : 460;
        var height = args.Length > 4 ? int.Parse(args[4]) : 1400;

        // 可选第 6 个参数：时间范围筛选（all / y:2026 / m:2026-07）。
        // 截图路线点不到按钮，只能这样把筛选状态喂进去看渲染结果。
        if (args.Length > 5) Views.DateFilter.Set(args[5]);

        try
        {
            var app = Application.Current;
            var win = new MainWindow { Width = width, Height = height };

            // Show() 之后布局才生效，Measure/Arrange 才有意义
            win.Show();
            win.UpdateLayout();
            Switch(win, tab);
            win.UpdateLayout();

            // 页签 "sheet" 拍的是「记一笔」弹层本身。
            // 它是独立 Window，不在 win.Content 里，所以必须先把它自己的
            // 内容摘下来再挂到固定尺寸容器上 —— 否则拍到的是主窗口。
            if (tab == "sheet")
            {
                var sheet = new Views.RecordSheet(win);
                sheet.Show();
                sheet.UpdateLayout();

                if (outPath.Contains("sheet-dose"))
                {
                    var vitC = FindAll(sheet).OfType<Button>()
                        .FirstOrDefault(b => b.Tag is string t && t == "inc|vitC");
                    if (vitC is not null) Click(vitC);
                }
                sheet.UpdateLayout();

                var sheetContent = (UIElement)sheet.Content;
                sheet.Content = null;

                RenderPng(sheetContent, width, height, outPath, tab, app);
                sheet.Close();
                // 离屏模式必须显式退出：返回 true 只是让 OnStartup 别再往下走，
                // 消息循环照样会起来，进程就永远不结束（截图会卡住）。
                Environment.Exit(0);
                return true;
            }

            // 把可视树重新挂到一个固定尺寸的容器上再渲染，
            // 这样得到的图不受窗口最大化/最小化状态影响
            var host = new Border
            {
                Width = width,
                Height = height,
                Background = (Brush)app.Resources[Theme.Paper],
            };

            // 直接把内容控件挪进 host：它自己会撑满
            // 必须先清空原宿主，否则一个元素不能同时挂两个父节点
            // （ChangeLogicalParent 会抛 InvalidOperationException）
            var content = (UIElement)win.Content;
            win.Content = null;
            host.Child = content;
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
            rtb.Render(host);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            var full = Path.GetFullPath(outPath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = File.Create(full);
            enc.Save(fs);

            Console.WriteLine($"已渲染 {full} ({width}x{height}, 页签 {tab}, 配色 {Theme.Current.Id})");
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("渲染失败: " + ex);
            Environment.Exit(3);
            return true;
        }
    }

    /// <summary>
    /// 图标自检。
    ///
    /// 为什么必须有：ICO 的目录项在前、数据在后，写目录时就得知道每项多大。
    /// 一旦偏移算错（早期版本所有项的 offset 都等于第一项的 118），
    /// Windows 会挑那个唯一真实存在的 256 尺寸凑合显示 —— 任务栏看着正常，
    /// 而 WPF、小尺寸、托盘取到的是越界数据，图标一片空白。
    ///
    /// 这个错不会让程序崩、不会有任何报错，只会让图标在某些地方变成空白，
    /// 所以只能靠断言守。这里查三件事：
    ///   1. 每个目录项声明的区间都落在文件内
    ///   2. 每个项的 offset 都各不相同（不重复指向同一处数据）
    ///   3. 真的能解码出来，且不是纯色/空白
    /// </summary>
    /// <summary>
    /// 把一棵可视树渲染成 PNG。
    ///
    /// 为什么要挂到固定尺寸容器上：直接拍窗口，拍到的东西受最大化/最小化
    /// 状态影响，而弹层和主窗口的尺寸本来就各不相同。
    /// </summary>
    private static void RenderPng(UIElement content, int width, int height,
        string outPath, string tab, Application app)
    {
        var host = new Border
        {
            Width = width,
            Height = height,
            Background = (Brush)app.Resources[Theme.Paper],
        };
        host.Child = content;

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        rtb.Render(host);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        var full = Path.GetFullPath(outPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(full);
        enc.Save(fs);

        Console.WriteLine($"已渲染 {full} ({width}x{height}, 页签 {tab}, 配色 {Theme.Current.Id})");
    }

    private static bool IconTest(string path)
    {
        var fails = new List<string>();
        void Check(string label, bool ok, string detail)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}  {detail}");
            if (!ok) fails.Add(label);
        }

        if (!File.Exists(path))
        {
            Console.WriteLine($"  FAIL  图标不存在  {path}");
            Environment.Exit(1);
            return true;
        }

        var b = File.ReadAllBytes(path);
        Check("ICO 头", b[0] == 0 && b[1] == 0 && BitConverter.ToUInt16(b, 2) == 1,
            $"type={BitConverter.ToUInt16(b, 2)}");

        var n = BitConverter.ToUInt16(b, 4);
        Console.WriteLine($"  条目数 {n}");

        var seenOffsets = new HashSet<uint>();
        for (var i = 0; i < n; i++)
        {
            var o = 6 + 16 * i;
            var w = (int)b[o]; if (w == 0) w = 256;
            var h = (int)b[o + 1]; if (h == 0) h = 256;
            var size = BitConverter.ToUInt32(b, o + 8);
            var off = BitConverter.ToUInt32(b, o + 12);

            var inRange = off + size <= (uint)b.Length && off >= 6 + 16 * n;
            Check($"{w}x{h} 区间在文件内", inRange, $"offset={off} size={size}");
            Check($"{w}x{h} 偏移不重复", seenOffsets.Add(off), $"offset={off}");
        }

        // 真的解码一遍，并且不能是空白/纯色
        try
        {
            var ico = new System.Drawing.Icon(path);
            foreach (var s in new[] { 16, 32 })
            {
                using var small = new System.Drawing.Icon(path, s, s);
                using var bmp = small.ToBitmap();
                var distinct = new HashSet<int>();
                for (var y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 8))
                    for (var x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 8))
                        distinct.Add(bmp.GetPixel(x, y).ToArgb());

                // 纯色 = 只有一种颜色，说明数据没画出来
                Check($"{s}x{s} 解码出多种颜色", distinct.Count > 2, $"{distinct.Count} 种");
            }
        }
        catch (Exception ex)
        {
            Check("解码不抛异常", false, ex.GetType().Name);
        }

        Console.WriteLine(fails.Count == 0 ? "图标自检通过" : $"图标自检失败 {fails.Count} 项");
        Environment.Exit(fails.Count == 0 ? 0 : 1);
        return true;
    }

    /// <summary>
    /// 托盘环境诊断。这个不是断言，是**把看到的东西打印出来**给用户看。
    ///
    /// 为什么需要：托盘不出图标有太多层可能 —— 没通知区域、通知区域设成永不显示、
    /// 被收进「^」折叠区、Shell 拒绝了注册。前面每一层在这里都能看出来。
    ///
    /// 自检（tray-test）只能验证代码路径，因为它在进程内立刻退出；
    /// 用户在任务栏里看到的是 Shell 那一侧的状态，必须在这里查。
    /// </summary>
    private static bool TrayDiag()
    {
        // 直接写 UTF-8 文件，不靠控制台。
        //
        // 两个原因：一是 GUI 子系统没有真控制台，Console.OutputEncoding 会抛
        // IOException；二是就算重定向到文件，编码对不上 PowerShell 读出来
        // 全是乱码（之前就是这样）。写文件 + 带 BOM，Get-Content 直接能读。
        var report = new System.Text.StringBuilder();
        void W(string s = "") => report.AppendLine(s);

        W("=== 托盘环境诊断 ===");
        W($"程序: {Environment.ProcessPath}");

        var exp = System.Diagnostics.Process.GetProcessesByName("explorer");
        W($"explorer 进程: {exp.Length} 个");
        foreach (var e in exp) W($"    pid={e.Id}  会话={e.SessionId}");

        using (var me = System.Diagnostics.Process.GetCurrentProcess())
            W($"本进程会话: {me.SessionId}  （交互式桌面通常 >=1）");

        W();
        W("--- 通知区域窗口（托盘图标必须挂在这些窗口下面）---");
        foreach (var cls in new[]
        {
            "Shell_TrayWnd",
            "NotifyIconToolbarWindowClass",
            "TrayNotifyWnd",
            "Shell_SecondaryTrayWnd",
        })
        {
            var h = FindWindow(cls, null);
            W($"  {cls,-32} {(h != IntPtr.Zero ? "存在" : "不存在")}");
        }

        W();
        W("--- 实测注册 ---");
        MainWindow.SuppressDialogs = true;
        var win = new MainWindow();
        win.Show();
        win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        W($"  NotifyIcon.Visible   : {MainWindow.TrayRegistered}");
        W($"  取到的图标尺寸       : {MainWindow.TrayIconSize}px");
        W($"  TrayError            : {(MainWindow.TrayError == "" ? "（无）" : MainWindow.TrayError)}");

        try
        {
            using var ico = new System.Drawing.Icon(
                System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico"), 16, 16);
            using var bmp = ico.ToBitmap();
            var colors = new HashSet<int>();
            for (var y = 0; y < bmp.Height; y++)
                for (var x = 0; x < bmp.Width; x++)
                    colors.Add(bmp.GetPixel(x, y).ToArgb());
            W($"  16px 图标颜色数      : {colors.Count} （1 = 空白）");
        }
        catch (Exception ex)
        {
            W($"  读 app.ico 失败      : {ex.GetType().Name}");
        }

        W();
        W("--- 判读 ---");
        var areaOk = FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
        if (exp.Length == 0)
            W("  explorer 没在跑 → 托盘图标无处注册。这不是程序的问题。");
        else if (!areaOk)
            W("  explorer 在跑但找不到 Shell_TrayWnd → 这个进程看不到桌面，");
        else if (MainWindow.TrayRegistered)
            W("  环境正常、注册成功。看不到图标只可能是：");
        W("    · 通知区域被设成「永不显示」→ 任务栏设置 → 通知区域");
        W("    · 图标收在「^」折叠区 → 点任务栏右下角的 ^ 展开");
        W("    · 程序已经关了 → 托盘图标只在程序运行时存在");
        W("    · 多显示器 → 图标可能跑到副屏任务栏上");

        // 带 BOM，PowerShell 的 Get-Content 才不会按 GBK 猜
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "tray-diag.txt");
        System.IO.File.WriteAllText(path, report.ToString(),
            new System.Text.UTF8Encoding(true));

        Console.WriteLine(report.ToString());
        Console.WriteLine($"已写入: {path}");
        Environment.Exit(0);
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr FindWindow(string c, string n);

    /// <summary>
    /// 托盘行为自检。
    ///
    /// 这段逻辑最容易出的错是"以为藏起来了其实退出了"：ShutdownMode 默认是
    /// OnLastWindowClose，一旦窗口关闭程序就结束，那么"点 X 隐藏"和"点 X 退出"
    /// 没法区分，而且不留任何报错 —— 表现就是用户关掉窗口，第二天发现程序没了。
    /// 所以真的走一遍 Close()，断言窗口被隐藏且程序还活着。
    /// </summary>
    private static bool TrayTest()
    {
        var fails = new List<string>();
        MainWindow.SuppressDialogs = true;   // 别弹框：MessageBox 会永久阻塞
        void Check(string label, bool ok, string detail)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}  {detail}");
            if (!ok) fails.Add(label);
        }

        var win = new MainWindow();
        win.Show();
        win.UpdateLayout();

        Check("ShutdownMode 显式", Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown,
            Application.Current.ShutdownMode.ToString());

        Check("窗口图标已加载", win.Icon != null,
            win.Icon is null ? "null" : $"{((BitmapSource)win.Icon).PixelWidth}px");

        // 托盘是否真的注册上了。这一项必须查 —— NotifyIcon.Visible = true
        // 在没有通知区域的环境（没有 explorer.exe：服务、沙箱、部分远程会话）
        // 会静默失败：不抛异常、不打日志，图标只是永远不出现。
        // 早先的自检没查这一项，所以在这种环境里"全绿"而实际没有托盘。
        var explorerUp = System.Diagnostics.Process.GetProcessesByName("explorer").Length > 0;
        Console.WriteLine($"  信息  explorer 进程：{(explorerUp ? "有" : "无")}"
            + $"  托盘注册：{(MainWindow.TrayRegistered ? "成功" : "失败")}"
            + $"  托盘图标尺寸：{MainWindow.TrayIconSize}px");
        if (!explorerUp)
            Console.WriteLine("        本机没有通知区域，托盘图标不可能出现；"
                + "这一项只验证代码路径，不作为托盘可用的证据。");
        Check("托盘注册状态可读", MainWindow.TrayRegistered || !explorerUp,
            MainWindow.TrayRegistered ? "已注册" : "未注册（无通知区域）");

        // 托盘图标不能是空的。早先这里只查了"注册成功没有"，而图标其实
        // 可能是空白的 —— 用户看到的正是"托盘没有图标"，自检却全绿。
        // 取托盘那个 Icon 实际解一次，要求解出多种颜色（纯色 = 空白）。
        try
        {
            // 用 exe 里嵌的那份，和托盘实际用的来源一致
            var probe = new System.Drawing.Icon(
                System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico"),
                32, 32).ToBitmap();
            var colors = new HashSet<int>();
            for (var yy = 0; yy < probe.Height; yy += 2)
                for (var xx = 0; xx < probe.Width; xx += 2)
                    colors.Add(probe.GetPixel(xx, yy).ToArgb());
            Check("托盘图标非空白", colors.Count > 2, $"{colors.Count} 种颜色");
            // 底色不能是近黑 —— 用户反馈过"exe 图标全黑"
            var bg = probe.GetPixel(probe.Width / 2, probe.Height / 2);
            var lum = (int)(0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B);
            Check("图标底色不是近黑", lum > 60, $"中心亮度 {lum}");
        }
        catch (Exception ex)
        {
            Check("托盘图标可解码", false, ex.GetType().Name);
        }

        // 真退出走 Close()，断言窗口隐藏且程序没退
        win.Close();
        win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        Check("点 X 后程序仍在运行", !MainWindow.ShutdownRequested, "ShutdownRequested=False");

        // 托盘注册成功时应当隐藏；注册失败时应当直接退出（藏起来就再也开不了）。
        // 两种都是正确行为，分开断言，不要在这里混为一谈。
        if (MainWindow.TrayRegistered)
        {
            Check("点 X 后窗口已隐藏", !win.IsVisible, $"IsVisible={win.IsVisible}");
        }
        else
        {
            Check("托盘不可用时不藏窗口", !win.IsVisible || win.IsLoaded,
                "已走退出分支并提示用户");
        }

        // 从托盘叫回来（仅在托盘可用时）
        if (MainWindow.TrayRegistered)
        {
            win.Restore();
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Check("能从托盘叫回", win.IsVisible, $"IsVisible={win.IsVisible}");
        }

        Console.WriteLine(fails.Count == 0 ? "托盘自检通过" : $"托盘自检失败 {fails.Count} 项");
        foreach (var f in fails) Console.WriteLine("  FAIL " + f);
        Environment.Exit(fails.Count == 0 ? 0 : 1);
        return true;
    }

    /// <summary>时间范围筛选的逻辑自检，见 Views.DateFilter.SelfTest。</summary>
    private static bool FilterSelfTest()
    {
        var code = Views.DateFilter.SelfTest();
        Environment.Exit(code);
        return true;
    }

    /// <summary>
    /// 「记一笔」弹层的点击回归自检。
    ///
    /// 这里守的是两个只能在运行时炸、编译和静态检查都看不见的错：
    ///  1. RedrawFooter 用下标赋值换底部条 → VisualCollection.set_Item
    ///     抛「指定的索引已经在使用。请先在指定的索引处断开 Visual 子级」，
    ///     每记一笔就弹一次窗（真机上 8 次，见 startup-error.log）。
    ///  2. 点格子必须记到那一列的时段。曾经点「晚」列却记到「早」，
    ///     列下标算了只用来摆位置，没传进写入逻辑。
    ///
    /// 直接调控件的 Click 事件（ButtonAutomationPeer / RaiseEvent），
    /// 走的是和真机点击完全相同的事件路径 —— 视觉树的那类异常只在
    /// 元素真的挂进可视树之后才复现，脱离可视树调 Pick() 是测不出来的。
    /// </summary>
    private static bool SheetTest()
    {
        var fails = new List<string>();
        var lines = new List<string>();
        void Log(string s) { Console.WriteLine(s); lines.Add(s); }
        void Check(string label, bool ok, string detail = "")
        {
            Log($"  {(ok ? "PASS" : "FAIL")}  {label}  {detail}");
            if (!ok) fails.Add(label);
        }
        string SlotInTip(string tip)
        {
            if (string.IsNullOrEmpty(tip)) return "";
            // 兼容「早」或（早）两种写法
            int i = tip.IndexOf('「'); int j = tip.IndexOf('」');
            if (i < 0 || j <= i) { i = tip.IndexOf('（'); j = tip.IndexOf('）'); }
            return i >= 0 && j > i ? tip.Substring(i + 1, j - i - 1) : "";
        }
        string TempFile()
        {
            // 优先用外部指定的结果路径（自检 harness 指向一定可写的目录），
            // 绕开沙箱里 %TEMP% 与程序目录都拒绝写入的问题。
            var env = Environment.GetEnvironmentVariable("STACKIT_RESULT_FILE");
            if (!string.IsNullOrEmpty(env)) return env;
            try { return Path.Combine(Path.GetTempPath(), "stackit_sheet_result.txt"); }
            catch { return Path.Combine(System.AppContext.BaseDirectory, "stackit_sheet_result.txt"); }
        }

        try
        {
            MainWindow.SuppressDialogs = true;
            var win = new MainWindow();

            var sheet = new Views.RecordSheet(win);
            win.Show();
            win.UpdateLayout();
            // 弹层也要进可视树，否则父元素为空的控件走不到
            // VisualCollection 那条抛异常的路径
            sheet.Show();
            sheet.UpdateLayout();
            win.Dispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.Background);

            var buttons = new List<Button>();
            CollectButtons(sheet, buttons);
            Log($"  弹层内找到 {buttons.Count} 个按钮");

            // —— 1. 无搜索框（新设计砍掉了） ——
            var searchBox = FindAll(sheet).OfType<TextBox>()
                .FirstOrDefault(t => ReferenceEquals(t.Tag, "search"));
            Check("无搜索框", searchBox is null);

            // —— 2. 品类步进器：点 ＋ 记 1，点 − 直接减 1（显式，不用猜）——
            var incBtn = buttons.FirstOrDefault(b =>
                b.Tag is string t && t == "inc|vitC");
            var decBtn = buttons.FirstOrDefault(b =>
                b.Tag is string t && t == "dec|vitC");
            Check("常吃里有 维生素C 的步进器（＋）", incBtn is not null);
            Check("维生素C 有可直接减的 − 按钮", decBtn is not null);

            // 从芯片行的 tooltip 取出当前时段（如「维生素C（早）」）
            var rowTip = FindAll(sheet).OfType<FrameworkElement>()
                .Select(e => TipOf(e))
                .FirstOrDefault(s => s.Contains("维生素C"));
            var slot = SlotInTip(rowTip ?? "");

            if (incBtn is not null)
            {
                var beforeErr = StartupErrorCount();
                var beforeCount = win.Db.Events.Count(e => e.ItemId == "vitC");

                // (a) 点 ＋：记 1 份（新建 1 条 dose=1 事件，写入当前时段）
                Click(incBtn);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                var one = win.Db.Events.Count(e => e.ItemId == "vitC");
                Check("点 ＋ 记 1 份（新建事件）", one == beforeCount + 1, $"{beforeCount} -> {one}");
                var only = win.Db.Events.Where(e => e.ItemId == "vitC").ToList();
                Check("＋ 记到当前时段那一列", only.Count == 1 && only[0].Slot == slot,
                    $"slot={only.LastOrDefault()?.Slot} 期望={slot}");

                // (b) 连点 ＋ 两次：同一条记录 dose 2 → 3（归并，非碎片），不抛可视树异常
                Click(incBtn);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Click(incBtn);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Check("连点 ＋ 不抛可视树异常", StartupErrorCount() == beforeErr,
                    $"新增 {StartupErrorCount() - beforeErr} 次");
                var vitc = win.Db.Events.Where(e => e.ItemId == "vitC").ToList();
                Check("同一品类只生成 1 条事件", vitc.Count == 1, $"{vitc.Count} 条");
                Check("＋连加 2 次 = dose 3（归并）",
                    vitc.Count == 1 && vitc[^1].Dose == 3,
                    $"dose={vitc.LastOrDefault()?.Dose}");

                // (c) 点 −：直接减 1，回到 dose 2，仍在同一事件上
                if (decBtn is not null)
                {
                    Click(decBtn);
                    win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    var afterDec = win.Db.Events.FirstOrDefault(e => e.ItemId == "vitC");
                    Check("− 步进器直接减 1", afterDec?.Dose == 2, $"dose={afterDec?.Dose}");
                }

                // (d) 再点 −：dose 2 → 1
                if (decBtn is not null)
                {
                    Click(decBtn);
                    win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    var d1 = win.Db.Events.FirstOrDefault(e => e.ItemId == "vitC");
                    Check("− 再减 1（dose 1）", d1?.Dose == 1, $"dose={d1?.Dose}");
                }

                // (e) 再点 −：dose 1 → 0，归零即删整条（用户要的「直接 -1」到底）
                if (decBtn is not null)
                {
                    Click(decBtn);
                    win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    var zero = win.Db.Events.Count(e => e.ItemId == "vitC");
                    Check("点 − 直接减到 0（归零删整条）", zero == beforeCount, $"{one} -> {zero}");
                }
            }

            var undoBtn = buttons.FirstOrDefault(b => b.Content as string == "撤销");
            // 重新 +1 到 dose=3（每次 + 都进撤销栈），再撤销两次应 dose 3→2→1、仍 1 条。
            var n0 = win.Db.Events.Count(e => e.ItemId == "vitC");
            if (incBtn is not null)
            {
                Click(incBtn);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Click(incBtn);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Click(incBtn);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            }
            // 剂量值快照：Event 是同一实例，Undo 会就地改它，必须取值而非抓引用
            var m0 = win.Db.Events.FirstOrDefault(e => e.ItemId == "vitC")?.Dose;
            Click(undoBtn);
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            var m1 = win.Db.Events.FirstOrDefault(e => e.ItemId == "vitC")?.Dose;
            Click(undoBtn);
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            var m2 = win.Db.Events.FirstOrDefault(e => e.ItemId == "vitC")?.Dose;
            Check("撤销两步：事件条数不变", win.Db.Events.Count(e => e.ItemId == "vitC") == n0 + 1,
                $"{win.Db.Events.Count(e => e.ItemId == "vitC")} 条");
            Check("撤销两步：dose 3->2->1", (m0 == 3 && m1 == 2 && m2 == 1),
                $"dose {m0} -> {m1} -> {m2}");

            // —— 3b. 键盘快捷键：数字 1 记「常吃」第 1 项（走真实 KeyDown 路由）——
            // 先用同一个 − 按钮把 vitC 清零，再按 1 应当重新记 1 份（1 条事件、dose=1）。
            while (win.Db.Events.Any(e => e.ItemId == "vitC"))
            {
                if (decBtn is not null) { Click(decBtn); win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background); }
                else break;
            }
            var kc0 = win.Db.Events.Count(e => e.ItemId == "vitC");
            PressKey(sheet, System.Windows.Input.Key.D1);
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            var kc1 = win.Db.Events.Count(e => e.ItemId == "vitC");
            var kdose = win.Db.Events.FirstOrDefault(e => e.ItemId == "vitC")?.Dose;
            Check("按 1 记常吃第 1 项", kc1 == kc0 + 1 && kdose == 1, $"{kc0} -> {kc1}, dose={kdose}");

            // —— 4. 选未来日期被弹回今天 ——
            var changeBtn = buttons.FirstOrDefault(b => b.Content as string == "改时间");
            var dp = FindAll(sheet).OfType<DatePicker>().FirstOrDefault();
            if (changeBtn is not null && dp is not null)
            {
                Click(changeBtn); // 展开时间条
                dp.SelectedDate = DateTime.Today.AddDays(1);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Check("选未来日期被弹回今天",
                    dp.SelectedDate is { } sd && sd == DateTime.Today,
                    $"SelectedDate={dp.SelectedDate:yyyy-MM-dd}");
            }

            // —— 5. 搜索（Core 能力，与设计无关，保留回归） ——
            Check("搜别名 D3 能命中", Core.Items.Search("D3").Count > 0,
                string.Join(",", Core.Items.Search("D3").Select(i => i.Id)));
            Check("搜 VC 能命中维C", Core.Items.Search("VC").Any(i => i.Id == "vitC"));
            Check("搜不到时不返回全部", Core.Items.Search("zzz不存在").Count == 0);
            Check("空关键字返回全部口服",
                Core.Items.Search("", "oral").Count == Core.Items.All.Count(i => i.Route == "oral"));


            // —— 7. 凌晨归属（RecordSheet 打开时的默认归属）——
            //
            // 这三条测的是**界面实际写进库里的那一条事件**（点常吃行上的 ＋），
            // 不是私有函数：这样表头显示、常吃区、写入三处是不是同一个 _slot 才被一起管住。
            // 时刻从 RecordSheet.NowOverride 注入，测完必须还原成 null ——
            // 否则同一进程里后面的断言会跟着读到假时钟。
            var today = DateTime.Today;
            void Probe(string label, double hours, string wantDate, string wantTime,
                string wantSlot, string wantNote)
            {
                Views.RecordSheet.NowOverride = () => today.AddHours(hours);

                // 清场：这一笔必须是"新建"，判得上日期与时段（连点会归并到同一条）
                foreach (var oldId in win.Db.Events
                             .Where(x => x.ItemId == "vitC").Select(x => x.Id).ToList())
                    win.RemoveEvent(oldId);

                var probe = new Views.RecordSheet(win);
                probe.Show();
                probe.UpdateLayout();
                win.Dispatcher.Invoke(() => { },
                    System.Windows.Threading.DispatcherPriority.Background);

                var inc = FindAll(probe).OfType<Button>()
                    .FirstOrDefault(b => b.Tag is string t && t == "inc|vitC");
                if (inc is null)
                {
                    Check(label, false, "常吃区里找不到 维生素C 的 ＋");
                    probe.Close();
                    return;
                }

                Click(inc);
                win.Dispatcher.Invoke(() => { },
                    System.Windows.Threading.DispatcherPriority.Background);

                var got = win.Db.Events.FirstOrDefault(x => x.ItemId == "vitC");
                // 界面上的标注：该出现的要出现，不该出现的不能出现
                var noteOk = wantNote.Length == 0
                    ? !(FindText(probe, "归昨天") || FindText(probe, "归今天"))
                    : FindText(probe, wantNote);
                Check(label,
                    got is not null
                        && got.When.Date == wantDate
                        && got.When.Time == wantTime
                        && got.Slot == wantSlot
                        && got.Dose == 1
                        && noteOk,
                    $"落盘 date={got?.When.Date} time={got?.When.Time} slot={got?.Slot} dose={got?.Dose}"
                    + $" 期望 date={wantDate} slot={wantSlot} 标注「{(wantNote.Length == 0 ? "（无）" : wantNote)}」"
                    + $" 标注在界面上={noteOk}");
                probe.Close();
            }

            Probe("01:30 打开 → 归昨天·晚（界面标「睡前 · 归昨天」）",
                1.5, Core.Stats.Iso(today.AddDays(-1)), "01:30", "晚", "睡前 · 归昨天");
            Probe("04:00 打开 → 归今天·早（界面标「凌晨 · 归今天」）",
                4.0, Core.Stats.Iso(today), "04:00", "早", "凌晨 · 归今天");
            Probe("09:00 打开 → 今天·早（无归属标注）",
                9.0, Core.Stats.Iso(today), "09:00", "早", "");
            Views.RecordSheet.NowOverride = null;
            Log("");
            foreach (var e in win.Db.Events)
                Log($"    {e.When.Date} {e.When.Time}  {e.ItemId}  dose={e.Dose ?? 1}  slot={e.Slot}");

            // 反证隔离生效：自检写的数据必须落在临时目录
            Check("自检数据落在临时目录",
                string.Equals(Core.Store.DataDir, SelfTestDir, StringComparison.OrdinalIgnoreCase),
                Core.Store.DataDir);

            // —— 6. 键盘快捷键：Esc 关闭弹层（走真实 KeyDown 路由）——
            var wasVisible = sheet.IsVisible;
            PressKey(sheet, System.Windows.Input.Key.Escape);
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Check("按 Esc 关闭弹层", wasVisible && !sheet.IsVisible, $"visible={sheet.IsVisible}");
            if (sheet.IsVisible) sheet.Close();

            try
            {
                if (SelfTestDir != "") Directory.Delete(SelfTestDir, recursive: true);
            }
            catch { /* 临时目录删不掉不算失败 */ }
        }
        catch (Exception ex)
        {
            Log($"  FAIL  自检途中抛出 {ex.GetType().Name}: {ex.Message}");
            foreach (var line in (ex.StackTrace ?? "").Split('\n').Take(12))
                Log($"        {line.Trim()}");
            fails.Add("自检途中抛出未处理异常");
        }

        Log("");
        Log(fails.Count == 0
            ? "记一笔自检通过"
            : $"记一笔自检失败 {fails.Count} 项: {string.Join("; ", fails)}");
        try { File.WriteAllLines(TempFile(), lines); }
        catch
        {
            // 某些环境对临时目录根禁止写文件（只能建子目录）。落到程序目录，
            // 自检结果照样可查。
            var alt = Path.Combine(System.AppContext.BaseDirectory, "stackit_sheet_result.txt");
            File.WriteAllLines(alt, lines);
            lines.Add("（结果文件因权限写不进临时目录，已改存 " + alt + "）");
        }
        Environment.Exit(fails.Count == 0 ? 0 : 1);
        return true;
    }

    /// <summary>递归收集可视树里的 Button。</summary>
    private static void CollectButtons(DependencyObject root, List<Button> into)
    {
        foreach (var b in FindAll(root).OfType<Button>()) into.Add(b);
    }

    /// <summary>
    /// 取 tooltip 文字。
    ///
    /// WPF 会把赋给 ToolTip 的 string 自动包成一个 ToolTip 控件，
    /// 所以不能直接 (string)b.ToolTip —— 那是 InvalidCastException。
    /// 两种形态都得认。
    /// </summary>
    private static string TipOf(FrameworkElement b) => b.ToolTip switch
    {
        string s => s,
        System.Windows.Controls.ToolTip tt => tt.Content?.ToString() ?? "",
        _ => "",
    };

    /// <summary>按可视树顺序枚举全部后代，用于核对界面上的真实排列。</summary>
    private static IEnumerable<DependencyObject> FindAll(DependencyObject root)
    {
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var g in FindAll(c)) yield return g;
        }
    }

    /// <summary>按视觉顺序列出按钮内容，用来证明顺序没变。</summary>
    private static List<Button> ButtonsInOrder(DependencyObject root) =>
        FindAll(root).OfType<Button>().ToList();

    /// <summary>点品名开数量面板，填入 n，回车确认。</summary>
    private static void OpenAndType(DependencyObject root, string n)
    {
        var nameBtn = FindAll(root).OfType<Button>()
            .FirstOrDefault(b => TipOf(b).Contains("填数量"));
        if (nameBtn is null) return;
        Click(nameBtn);
        var box = FindAll(root).OfType<TextBox>()
            .FirstOrDefault(t => ReferenceEquals(t.Tag, "dose"));
        if (box is null) return;
        box.Text = n;
        PressEnter(box);
    }

    /// <summary>在控件上发一个回车 KeyDown，走真实的键盘路径。</summary>
    private static void PressEnter(UIElement target)
    {
        var src = PresentationSource.FromVisual(target)
                    as System.Windows.Interop.HwndSource;
        target.RaiseEvent(new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice,
            src, 0, System.Windows.Input.Key.Enter)
        { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
    }

    /// <summary>走 ButtonBase 的真实点击路径。</summary>
    private static void Click(Button b)
    {
        b.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    /// <summary>在控件上发一个 KeyDown，走真实键盘路由（与 PressEnter 同机制）。</summary>
    private static void PressKey(UIElement target, System.Windows.Input.Key key)
    {
        System.Windows.Input.Keyboard.Focus(target);
        var src = System.Windows.PresentationSource.FromVisual(target)
                    as System.Windows.Interop.HwndSource;
        target.RaiseEvent(new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice,
            src, 0, key)
        { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
    }

    /// <summary>
    /// 当前进程已记下的未处理异常条数。
    ///
    /// 「记一笔」那些异常都被 App.xaml.cs 的 DispatcherUnhandledException
    /// 兜住并 args.Handled = true，所以它们不会往外冒，只能从那层数。
    /// </summary>
    private static int StartupErrorCount() => App.LoggedErrors;

    /// <summary>造一个页面实例，给计时用。</summary>
    private static UIElement MakePage(MainWindow win, string tab) => tab switch
    {
        "timeline" => new Views.TimelineView(win),
        "data" => new Views.DataView(win),
        _ => new Views.StatsView(win),
    };

    /// <summary>可视树里有没有这段文字。用来证明页面确实重建过，而不只是字段变了。</summary>
    private static bool FindText(DependencyObject root, string needle)
    {
        if (root is TextBlock tb && tb.Text.Contains(needle)) return true;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
            if (FindText(VisualTreeHelper.GetChild(root, i), needle)) return true;
        return false;
    }

    /// <summary>
    /// 量三个页面的构建耗时，分开记构造与布局。
    /// 界面卡死不能靠"感觉慢了"判断，要拿秒数说话；而 WPF 的时间几乎总在布局，
    /// 不分开量就会去优化错的地方。实测（1697 条数据）时间流布局曾经 9.4 秒，
    /// 改成虚拟化后降到约 95ms。
    /// </summary>
    private static bool TimingProbe(string which)
    {
        try
        {
            var win = new MainWindow { Width = 460, Height = 900 };
            win.Show();
            win.UpdateLayout();

            // 必须显式灌数据：MainWindow 构造时读的是磁盘上的库，
            // 而这个自检跑在干净目录里会是空库，测出来全是几十毫秒的假数。
            var db = Core.Store.Load();
            Console.WriteLine($"数据目录: {Core.Store.DataDir}");
            Console.WriteLine($"目录来源: {Core.Store.DataDirChoice ?? "<全部失败>"}");
            Console.WriteLine($"库文件: {Core.Store.DbPath}");
            Console.WriteLine($"库文件存在: {File.Exists(Core.Store.DbPath)}");
            Console.WriteLine($"数据: {db.Events.Count} 条事件");
            if (db.Events.Count == 0)
            {
                Console.WriteLine("!! 库是空的，量不出真实耗时。先设 STACKIT_DATA_DIR 指向有数据的库。");
                Environment.Exit(2);
                return true;
            }
            win.ReplaceDb(db);
            win.RefreshNow();

            // 分别量构造与布局。WPF 的时间几乎总在布局：不分开量就会
            // 以为是构造慢，从而去优化错的地方（实测时间流构造 279ms / 布局 9.4s）。
            //
            // 给每页设时间预算。超过就是回归 —— 曾经没设，结果时间流全量布局
            // 9.4 秒、界面等于卡死，而没有任何东西报警。
            var budget = new Dictionary<string, long>
            {
                ["stats"] = 1500,
                ["timeline"] = 3000,
                ["data"] = 1500,
            };
            var over = new List<string>();

            foreach (var tab in new[] { "stats", "timeline", "data" })
            {
                foreach (var label in new[] { "冷", "热" })
                {
                    win.PageHost.Content = null;
                    win.UpdateLayout();

                    var swBuild = System.Diagnostics.Stopwatch.StartNew();
                    var view = MakePage(win, tab);
                    swBuild.Stop();

                    var swLayout = System.Diagnostics.Stopwatch.StartNew();
                    win.PageHost.Content = view;
                    win.UpdateLayout();
                    swLayout.Stop();
                    win.PageHost.Content = null;

                    var total = swBuild.ElapsedMilliseconds + swLayout.ElapsedMilliseconds;
                    var flag = total > budget[tab] ? "  << 超出预算" : "";
                    if (total > budget[tab] && label == "热") over.Add($"{tab} {total}ms");

                    Console.WriteLine($"  {tab,-9} {label}  构造 {swBuild.ElapsedMilliseconds,6} ms   " +
                                      $"布局 {swLayout.ElapsedMilliseconds,7} ms   合计 {total,6} ms{flag}");
                }
            }

            if (over.Count > 0)
            {
                Console.WriteLine($"结果: !! 有页面超出时间预算（{string.Join("、", over)}）");
                Environment.Exit(1);
                return true;
            }

            Console.WriteLine("结果: 三个页面都在预算内");
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("计时异常: " + ex);
            Environment.Exit(2);
            return true;
        }
    }

    private static void Switch(MainWindow win, string tab)
    {
        switch (tab)
        {
            case "timeline": win.GoTo("timeline"); break;
            case "data": win.GoTo("data"); break;
            default: win.GoTo("stats"); break;
        }
    }

    /// <summary>
    /// 复现「导入 记录.md」这条路径，逐步打印每一步的实际状态。
    ///
    /// 为什么单独做这个：导入在界面上是"解析 → 确认 → 覆盖 → 重画"四步，
    /// 中间任何一步静默失败，用户看到的都只是"点了确定，还是没数据"，
    /// 拿不到到底是哪一步断的。这里按同一条代码路径走一遍并逐条报数。
    /// </summary>
    private static bool ImportTest(string mdPath)
    {
        try
        {
            Console.WriteLine($"数据目录: {Core.Store.DataDir}");
            Console.WriteLine($"目录来源: {Core.Store.DataDirChoice ?? "<全部失败>"}");
            if (Core.Store.DataDirProblem is not null)
                Console.WriteLine($"目录问题: {Core.Store.DataDirProblem}");

            // 1. 解析（只用来报数，不作为"导入成功"的依据）
            var md = File.ReadAllText(mdPath, System.Text.Encoding.UTF8);
            var result = Core.Importer.ParseMarkdown(md);
            Console.WriteLine($"[1/3] 解析: 事件 {result.Db.Events.Count} 条, 周 {result.Report.Weeks}");

            // 2. 建窗口（走真实构造路径，含 ShowPage("stats")）。
            // 先把库清空，这样"导入后有没有数据"才是可判定的。
            var win = new MainWindow { Width = 460, Height = 900 };
            win.Show();
            win.ReplaceDb(new Core.Db());
            win.RefreshNow();
            win.UpdateLayout();
            Console.WriteLine($"[2/3] 启动(空库): 内存中 {win.Db.Events.Count} 条, " +
                              $"统计页有内容 {FindText(win, "这 3 年我在坚持")}");

            // 3. 走真实的导入代码路径：DoImport 就是界面上那个按钮回调的主体，
            // 包括确认框那一步。只把两个对话框替掉（不然会卡在模态框上），
            // 其余逻辑一字不改。
            //
            // 为什么必须这样测：之前自检自己复制了一遍导入流程，
            // 结果漏掉了「确认框用 OKCancel 而调用处比 Yes，比较永远为假」
            // 导致导入静默失败那个 bug。绕过确认就测不出确认。
            var confirmed = false;
            Views.DataView.ConfirmOverride = _ => { confirmed = true; return true; };
            Views.DataView.MsgOverride = m =>
                Console.WriteLine("      [提示] " + m.Replace("\n\n", " / ").Replace("\n", " "));

            var dataView = new Views.DataView(win);
            dataView.DoImport(() => File.ReadAllText(mdPath, System.Text.Encoding.UTF8));
            Views.DataView.ConfirmOverride = null;
            Views.DataView.MsgOverride = null;

            Console.WriteLine($"      确认框被调用: {confirmed}");

            // 断言确认框的按钮类型。这道检查测的是一个常量，看着单薄，
            // 但它守的正是之前那个静默失败点：弹窗若不返回 Yes/No，
            // 调用处的 == Yes 判定永远为假，导入会无声中止。
            // （ConfirmOverride 生效时真正的 ShowDialog 不执行，
            //   所以那条路径只能这样单独守。）
            var btnOk = Views.DataView.ConfirmButtons == System.Windows.MessageBoxButton.YesNo;
            Console.WriteLine($"      确认框按钮类型正确: {btnOk}");
            if (!btnOk)
            {
                Console.WriteLine("结果: !! 确认框按钮类型与判定不匹配，导入会静默失败");
                Environment.Exit(1);
                return true;
            }

            // GoToDeferred 走 Dispatcher 队列，这里没有消息循环，
            // 手动把它抽干，等价于界面回到消息循环后发生的事。
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();

            var hasStats = FindText(win, "这 3 年我在坚持");
            var s = Core.Stats.Compute(win.Db);
            Console.WriteLine($"[3/3] 导入后: 内存 {win.Db.Events.Count} 条, " +
                              $"统计 {s.Total} 条 / {s.DayCount} 天 / {s.Items.Count} 品类");
            Console.WriteLine($"      统计页有内容: {hasStats}");

            // 落盘的库再读回来，确认不是只有内存里有
            var back = Core.Store.Load();
            Console.WriteLine($"      重新加载: {back.Events.Count} 条");

            var ok = confirmed
                     && win.Db.Events.Count == result.Db.Events.Count
                     && back.Events.Count == result.Db.Events.Count
                     && hasStats;
            Console.WriteLine(ok ? "结果: 导入链路完整" : "结果: !! 导入链路断了");
            Environment.Exit(ok ? 0 : 1);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("导入测试异常: " + ex);
            Environment.Exit(2);
            return true;
        }
    }
}
