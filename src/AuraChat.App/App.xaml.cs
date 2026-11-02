using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AuraChat.App;

/// <summary>
/// 应用入口：先装配后台服务（HTTP + 业务核心），再开界面。
/// 原版是「网页端 + 独立客户端」两截，现在服务端进了 exe，客户端和网页共用同一份数据。
/// </summary>
public partial class App : Application
{
    public static AppHost? Host { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // --headless：只跑后台服务、不开窗口（自动化测试 / 当服务用）
        var headless = e.Args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase));

        // 整段启动包进 try：任何一步挂掉都把完整堆栈落盘（headless 下没人点弹窗）
        try
        {
            Host = AppHost.Create();

            var reason = await Host.StartServerAsync();

            // 聊天中枢 / 自驱 / 每分钟巡检——就算 HTTP 端口被占也要把功能跑起来
            try { await Host.StartServicesAsync(); }
            catch (Exception ex) { Host.Log.Child("app").Error("功能服务启动失败：", ex.Message); }

            if (headless)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Host.Log.Child("app").Info(reason is null ? "以 --headless 启动" : $"以 --headless 启动，但服务没起来：{reason}");
                if (reason is not null)
                {
                    Crash("服务没起来：" + reason);
                    Shutdown(2);
                }
                return;
            }

            var window = new MainWindow(Host, reason);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Crash(ex.ToString());
            MessageBox.Show($"启动失败：{ex.Message}", "AuraChat", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>崩溃原因落盘（AURACHAT_DATA_DIR 或 AppData 下的 startup-crash.log）。</summary>
    private static void Crash(string text)
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("AURACHAT_DATA_DIR")
                      ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            File.WriteAllText(Path.Combine(dir, "startup-crash.log"), text);
        }
        catch { /* 落盘失败就算了 */ }
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Host?.Log.Child("ui").Error(e.Exception.ToString());
        Crash(e.Exception.ToString());
        MessageBox.Show($"出了点问题：{e.Exception.Message}", "AuraChat", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        base.OnExit(e);
    }
}
