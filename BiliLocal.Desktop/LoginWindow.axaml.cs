using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using BiliLocal.Core;

namespace BiliLocal.Desktop;
public partial class LoginWindow : Window
{
    private readonly BiliClient bili;
    private CancellationTokenSource? operation;
    private Bitmap? bitmap;
    public LoginWindow() : this(new BiliClient()) { }
    public LoginWindow(BiliClient bili)
    {
        this.bili = bili; InitializeComponent(); LogoutButton.IsVisible = bili.User != null;
        Closed += (_, _) => { operation?.Cancel(); bitmap?.Dispose(); CookieInput.Text = ""; };
    }
    private CancellationToken Begin()
    {
        operation?.Cancel(); operation = new CancellationTokenSource();
        MessageText.Text = ""; return operation.Token;
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        var ct = Begin();
        try { await action(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) MessageText.Text = MediaDownloader.Redact(ex.Message); }
    }
    private void TabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (operation != null && e.Source == LoginTabs) operation.Cancel();
    }
    private async void QrClick(object? sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        using var qr = new QrLogin(bili);
        QrLabel.Text = "正在生成二维码…";
        var bytes = await qr.GenerateAsync(ct);
        ct.ThrowIfCancellationRequested();
        bitmap?.Dispose(); bitmap = new Bitmap(new MemoryStream(bytes)); QrImage.Source = bitmap;
        for (var i = 0; i < 75; i++)
        {
            var result = await qr.PollAsync(ct);
            if (result.Cookies != null)
            {
                await bili.LoginAsync(result.Cookies, ct); Close(); return;
            }
            QrLabel.Text = result.Status;
            if (result.Status.Contains("过期")) return;
            await Task.Delay(2500, ct);
        }
    });
    private async void CookieClick(object? sender, RoutedEventArgs e)
    {
        var raw = CookieInput.Text ?? ""; CookieInput.Text = "";
        await RunAsync(async ct => { await bili.LoginAsync(raw, ct); Close(); });
    }
    private async void BrowserClick(object? sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        MessageText.Text = "正在打开官方登录窗口…";
        var cookies = await BrowserLogin.LoginAsync(new Progress<string>(text => { if (!ct.IsCancellationRequested) MessageText.Text = text; }), ct);
        await bili.LoginAsync(cookies, ct); Close();
    });
    private void LogoutClick(object? sender, RoutedEventArgs e) { operation?.Cancel(); bili.Logout(); Close(); }
}
