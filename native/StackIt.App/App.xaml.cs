using System.IO;
using System.Windows;

namespace StackIt.App;

/// <summary>
/// 应用入口。主题必须在窗口创建前应用完，
/// 否则窗口第一次渲染会拿到空资源（DynamicResource 会等，但 DynamicResource
/// 解析不到时会回退成透明/黑色，出现一闪）。
/// </summary>
public partial class App : Application
{
    /// <summary>单实例锁。挂到 Application 上，让 MainWindow.Quit() 能拿到它释放。</summary>
    internal SingleInstance? Single { get; private set; }

    /// <summary>
    /// 已被兜住的界面异常条数。
    ///
    /// 这些异常都被标记成 Handled，不会再往外冒，外部观察者一个也看不见。
    /// 「记一笔」连点时每次重画底部条都抛一次 —— 界面看着完全正常，
    /// 只是一直弹窗。不计数就只能靠人肉盯着弹窗，自检也就无从断言"没抛"。
    /// </summary>
    internal static int LoggedErrors;

    /// <summary>交还单实例锁。必须活到真正退出这一刻才调。</summary>
    internal void ReleaseSingle()
    {
        Single?.Release();
        Single = null;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 界面抛出的异常自己兜住并记日志，不弹那个没有父窗口、
        // 会把窗口卡住不响应的系统崩溃对话框。
        DispatcherUnhandledException += (_, args) =>
        {
            LoggedErrors++;
            LogCrash(args.Exception);
            // 自检/离屏模式不弹框：MessageBox 是模态的，在没有消息循环的
            // 自检进程里会把整个自检永久卡死 —— 症状是只打印了第一行就
            // 再无输出，既不像通过也不像失败，最难查。
            // 写全类型名：Application.MainWindow 是个 Window 类型的属性，
            // 在这里裸写 MainWindow 会解析到那个属性上。
            if (!StackIt.App.MainWindow.SuppressDialogs)
                MessageBox.Show(
                    args.Exception.Message + "\n\n详情已写入日志，程序继续运行。",
                    "StackIt", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception);

        try
        {
            base.OnStartup(e);

            // 自检/离屏模式必须在任何东西碰到 Store 之前把数据目录指走 ——
            // 紧挨着 Theme.Restore() 之前。Theme.Restore() 会调 Store.LoadPalette()，
            // 那一下就把 DataDir 这个 Lazy 求值并永久缓存了，之后再设环境变量
            // 已经没有意义。「记一笔」自检会点真按钮、真写记录，不隔离的话
            // 每跑一次自检，用户的 db.json 就多一批假记录，而自检本身是绿的。
            if (e.Args.Length >= 1 && e.Args[0] == "--sheet-test")
                Shot.UseSelfTestDataDir();

            // 「纸底」（浅 / 深）先恢复，**再**装交互层样式表。
            // 顺序是硬要求：样式表里那两枚 DropShadowEffect 的颜色写的是
            // {x:Static M.ShadowInk}，那是解析期快照，装完就不再重取。
            // 深纸的阴影墨色必须在这次解析之前落到 M.ShadowInk 上。
            Theme.PreparePaper();
            // 交互层样式表（StackItButton / Chip / Card / Cta / Tab / focus 环）。
            // 必须在建窗口之前：MainWindow.xaml 里用 DynamicResource 引这些键。
            StackIt.App.UI.Interactions.Install();
            // 带任何命令行参数进来的都是自检 / 离屏渲染：那些路径没有消息循环推进
            // 动画时间线，BeginAnimation 会让元素停在**起始值**上 —— 截图里页签
            // 指示条永远是 scaleX 0，看着像功能没做。关掉之后直接落终值。
            if (e.Args.Length > 0) StackIt.App.UI.Motion.Enabled = false;

            // 主题必须先应用，否则 DynamicResource 解析不到，
            // 渲染出来会是透明/黑块
            Theme.Restore();
            // 离屏渲染模式：画完 PNG 就退出，不进入消息循环。
            // 自检/离屏模式**不能**抢 Mutex——否则它再起第二个进程会被自己踢掉，
            // 递归地把唯一能跑的那个也关了。
            if (Shot.TryRun(e.Args)) return;

            // 单实例：第二个实例只把已有窗口叫到前台，然后安静退出，
            // 绝不创建第二个 MainWindow——两个窗口会互相覆盖同一份 db.json。
            Single = new SingleInstance();
            if (!Single.IsOwner)
            {
                // 这里 MainWindow 这个名字会解析成 Application.MainWindow 属性
                // （类型是 Window），要拿到静态成员必须写全类型名。
                if (!StackIt.App.MainWindow.SuppressDialogs)
                    MessageBox.Show("程序已在运行。", "StackIt",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                SingleInstance.TryActivateExisting();
                Shutdown(0);
                return;
            }

            var win = new MainWindow();
            MainWindow = win;
            win.Show();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            // 自检模式同样不弹框，否则自检失败会表现成"卡住不动"，
            // 而不是一行 FAIL + 非零退出码。
            if (!StackIt.App.MainWindow.SuppressDialogs)
                MessageBox.Show(
                    ex.Message + "\n\n详情已写入：\n" + CrashPath,
                    "StackIt 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string CrashPath =>
        Path.Combine(AppContext.BaseDirectory, "startup-error.log");

    private static void LogCrash(Exception? ex)
    {
        try
        {
            File.AppendAllText(CrashPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch
        {
            // 连日志都写不了就只能放弃了
        }
    }
}
