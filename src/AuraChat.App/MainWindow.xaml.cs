using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AuraChat.Core;

namespace AuraChat.App;

/// <summary>
/// 主窗口：服务正常时直接内嵌自带网页端（WebView2 指向本机服务），
/// 初始化向导、聊天、空间、设置全都在这一层，桌面端和网页端永远是同一份界面。
/// 服务没起来或 WebView2 运行时缺失时，退回简版状态页（还能一键用浏览器打开）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppHost _host;

    public MainWindow(AppHost host, string? startError)
    {
        InitializeComponent();
        _host = host;

        var tag = _host.Config.Current["botTag"]?.ToString();
        TagText.Text = string.IsNullOrWhiteSpace(tag) ? AuraChatInfo.Version : $"{AuraChatInfo.Version} · {tag}";
        FooterText.Text = $"数据目录：{_host.Store.DataDir}";

        if (startError is not null)
        {
            ShowStatus($"服务没起来：{startError}", "—");
            return;
        }

        if (_host.Auth.NeedSetup)
            StatusText.Text = "服务已启动，还没有主人账号——界面里走一遍初始化就行。";

        // 默认先给网页壳；WebView2 初始化失败再退回状态页
        WebRoot.Visibility = Visibility.Visible;
        StatusRoot.Visibility = Visibility.Collapsed;
        _ = InitWebAsync();
    }

    /// <summary>初始化 WebView2 并打开本机服务。运行时缺失/初始化失败 → 退回状态页。</summary>
    private async Task InitWebAsync()
    {
        try
        {
            await Web.EnsureCoreWebView2Async();
            Web.CoreWebView2.DocumentTitleChanged += (_, _) => Title = Web.CoreWebView2.DocumentTitle is { Length: > 0 } t ? $"AuraChat — {t}" : "AuraChat";
            Web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                // 网页里的外链别在窗口里跳走，交给系统浏览器
                e.Handled = true;
                try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
            };
            Web.Source = new Uri(_host.LocalUrl);
        }
        catch (Exception ex)
        {
            _host.Log.Child("ui").Warn("WebView2 初始化失败，退回状态页：", ex.Message);
            ShowStatus(
                "内置界面组件（WebView2 运行时）没装好，先用浏览器凑合，装上运行时后重启就是内嵌界面了。\n" +
                "下载地址：https://developer.microsoft.com/microsoft-edge/webview2/",
                _host.LocalUrl);
        }
    }

    private void ShowStatus(string status, string url)
    {
        StatusText.Text = status;
        UrlText.Text = url;
        WebRoot.Visibility = Visibility.Collapsed;
        StatusRoot.Visibility = Visibility.Visible;
    }

    private void OnOpenWeb(object sender, RoutedEventArgs e)
    {
        if (!_host.Server.Running) return;
        try { Process.Start(new ProcessStartInfo(_host.LocalUrl) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "打不开浏览器"); }
    }

    private void OnOpenData(object sender, RoutedEventArgs e) => OpenFolder(_host.Store.DataDir);

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        var file = _host.Log.FilePath;
        if (file is null) return;
        OpenFolder(Path.GetDirectoryName(file)!);
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "打不开目录"); }
    }
}
