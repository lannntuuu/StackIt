using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StackIt.App;

/// <summary>
/// 单实例：用命名 Mutex 防多开。第二个进程进来不抢锁，
/// 而是把第一个进程的窗口叫到前台，然后自己安静退出——
/// 否则两个进程会各自写同一份 db.json，后关的那个把前一个整份覆盖掉。
/// </summary>
public sealed class SingleInstance
{
    private readonly Mutex? _mutex;
    private readonly bool _createdNew;

    public SingleInstance()
    {
        // 命名 Mutex 的 Global\ 前缀要求进程持有 SeCreateGlobalPrivilege。
        // 在标准/受限用户上下文（非管理员、某些企业策略、UAC 受限令牌）下，
        // new Mutex(true, @"Global\...") 会直接抛 UnauthorizedAccessException，
        // 于是 App 启动就崩在「启动失败」——其实只是没抢到锁，不该这么严重。
        //
        // 降级到 Local\ 前缀：Local 只在当前会话内互斥。多开互相覆盖数据
        // 这种情况本就只可能发生在同一用户会话里（跨会话是另一桌面/另一用户，
        // 不会共享同一份 %APPDATA%\StackIt\db.json），所以 Local\ 已经够用，
        // 单实例语义不丢。
        //
        // 构造函数绝不允许向外抛：一旦抛，上层 catch 会把它当成「启动失败」整屏
        // 报错，但实际只是没抢到锁。连 Local 都建不出时，我们让 IsOwner=false，
        // 走「已有实例」那条安静退出路径，而不是崩溃。
        try
        {
            // initialOwner=true 只在"我们新建了这个 Mutex"时才真正拿到锁；
            // createdNew=false 表示锁早已被别人拿着，本进程没持有，后面也就
            // 不该去 Release（Release 一个没持有的锁会抛 ApplicationException）。
            _mutex = new Mutex(true, @"Global\StackIt", out _createdNew);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
            || ex is IOException
            || ex is System.Threading.WaitHandleCannotBeOpenedException)
        {
            // Global 抢不到（典型：没有 SeCreateGlobalPrivilege），退回会话内互斥
            Console.Error.WriteLine("Global Mutex 建不出，降级到 Local：" + ex.Message);
            try
            {
                _mutex = new Mutex(true, @"Local\StackIt", out _createdNew);
            }
            catch (Exception ex2)
            {
                // 连 Local 都建不出：当作「已有实例」处理，IsOwner=false，
                // 上层据此安静退出。_mutex 置空，Release 里要防 NRE（见 Release）。
                Console.Error.WriteLine("Local Mutex 也建不出，放弃单实例：" + ex2.Message);
                _mutex = null;
                _createdNew = false;
            }
        }
    }

    /// <summary>是不是第一个（也就是真正持有 Mutex 的那个）进程。</summary>
    public bool IsOwner => _createdNew;

    /// <summary>
    /// 让已经在跑的那个实例把窗口显示出来。找不到窗口就返回 false（调用方据此提示用户）。
    ///
    /// **不能用 Process.MainWindowHandle 找窗口。** 收进托盘之后窗口是隐藏的，
    /// 那个属性只认「可见的、无主的顶层窗口」，隐藏窗口它返回 0。
    /// 早先的实现据此判断「没有已有窗口」直接退出 —— 于是再启动只会弹一句
    /// 「程序已在运行」，窗口永远不出来。托盘图标要是恰好看不到（收在「^」
    /// 折叠区、Explorer 重启后丢了、通知区域设成永不显示），程序就此失联，
    /// 用户只能去任务管理器杀进程。
    ///
    /// 现在按窗口类名枚举（EnumWindows 连隐藏窗口一起给），找到之后发一条
    /// 自注册消息，让**它自己**调 Show()。为什么不让外部直接 ShowWindow：
    /// 那只会改 Win32 的可见位，WPF 那边的 Visibility 还停在 Hidden，
    /// 此后再点右上角 X 会看起来毫无反应。对面若是还不认识这条消息的
    /// 老版本，消息会被静默丢掉，所以等一小会儿，窗口还没出来就退回直接
    /// ShowWindow 硬拉 —— 至少能看见，哪怕那个老进程的 X 会有点别扭。
    /// </summary>
    public static bool TryActivateExisting()
    {
        var hwnd = FindAppWindow();
        if (hwnd == IntPtr.Zero) return false;

        PostMessage(hwnd, ShowMessage, IntPtr.Zero, IntPtr.Zero);

        // 10 × 30ms = 300ms。对面只是 Hide 了窗口，消息循环照跑，够它做完。
        for (var i = 0; i < 10 && !IsWindowVisible(hwnd); i++)
            Thread.Sleep(30);

        if (!IsWindowVisible(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        return true;
    }

    /// <summary>
    /// 让已经在跑的那个实例退出。托盘图标没出来、右键菜单点不到时，
    /// 这是唯一的命令行出口 —— 否则只剩任务管理器。
    /// </summary>
    public static bool TryQuitExisting()
    {
        var hwnd = FindAppWindow();
        return hwnd != IntPtr.Zero && PostMessage(hwnd, QuitMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 两条进程间消息。用 RegisterWindowMessage 而不是 WM_APP + N：
    /// 后者和别的程序撞号没有任何保护，前者由系统保证全系统唯一。
    /// </summary>
    internal static int ShowMessage { get; } = RegisterWindowMessage("StackIt.ShowMainWindow");

    internal static int QuitMessage { get; } = RegisterWindowMessage("StackIt.QuitApp");

    /// <summary>WPF 的顶层窗口类名形如 HwndWrapper[StackIt.App;;{guid}]。</summary>
    private const string WindowClassPrefix = "HwndWrapper[StackIt.App;";

    /// <summary>
    /// 找本程序的主窗口：类名前缀 + 有标题。
    ///
    /// 为什么还要查标题：同一个进程里有一堆同类的无标题辅助窗口
    /// （IME、渲染上下文、SystemResourceNotify 之类），标题是唯一能
    /// 把它们和主窗口区分开的东西。
    ///
    /// internal 而不是 private：`--instance-test` 要断言「隐藏之后仍然找得到」，
    /// 这正是旧实现（Process.MainWindowHandle）做不到的那一步。
    /// </summary>
    internal static IntPtr FindAppWindow()
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var cls = new StringBuilder(256);
            if (GetClassName(h, cls, cls.Capacity) == 0) return true;
            if (!cls.ToString().StartsWith(WindowClassPrefix, StringComparison.Ordinal)) return true;
            if (GetWindowTextLength(h) == 0) return true;
            found = h;
            return false;   // 找到就停
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>释放 Mutex。必须活到 Quit 才调，不能提前丢——提前丢会让第二个进程乘虚而入。</summary>
    public void Release()
    {
        // _mutex 可能为 null：构造函数降级到 Local 都失败时我们没拿到任何锁，
        // 此时无锁可释放，直接返回即可（IsOwner 已是 false，上层本就不会走到这里，
        // 但 Quit 路径总是会调一次，所以这里必须能安全空转）。
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }   // 没持有锁时 Release 会抛这个，忽略即可
        finally { _mutex.Dispose(); }
    }

    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);
}
