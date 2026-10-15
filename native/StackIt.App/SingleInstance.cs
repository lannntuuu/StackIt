using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    /// 把已经运行的那个实例叫到前台。找不到就返回 false（调用方据此自己退出）。
    /// 托盘运行时窗口是隐藏的，所以先 ShowWindow(SW_RESTORE) 把它恢复出来，
    /// 再 SetForegroundWindow 抢焦点。SetForegroundWindow 受系统限制可能调不通，
    /// 那不算失败，静默即可——能把窗口恢复显示出来就已经达到目的。
    /// MainWindowHandle 为 0 的进程（主窗口还没建/已卸载）跳过继续找下一个。
    /// </summary>
    public static bool TryActivateExisting()
    {
        var self = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName("StackIt.App"))
        {
            try
            {
                if (p.Id == self) continue;
                var hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;   // 拿不到主窗口句柄，跳过继续找
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                return true;
            }
            catch
            {
                // 单个进程处理失败不影响继续尝试别的进程
            }
            finally
            {
                p.Dispose();
            }
        }
        return false;
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

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
