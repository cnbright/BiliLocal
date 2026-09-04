using System.Net;
using Microsoft.Playwright;
using QRCoder;

namespace BiliLocal.Core;

public sealed class QrLogin : IDisposable
{
    private readonly CookieContainer jar = new();
    private readonly HttpClient client;
    private string key = "";
    private DateTimeOffset expires;
    public QrLogin(BiliClient bili) => client = bili.CreateHttpClient([], jar);
    public async Task<byte[]> GenerateAsync(CancellationToken ct)
    {
        var data = BiliClient.Unpack(await BiliClient.ReadJsonAsync(client, "https://passport.bilibili.com/x/passport-login/web/qrcode/generate", ct));
        key = data.Text("qrcode_key"); expires = DateTimeOffset.UtcNow.AddMinutes(3);
        using var qr = QRCodeGenerator.GenerateQrCode(data.Text("url"), QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(qr).GetGraphic(6);
    }
    public async Task<(string Status, Dictionary<string, string>? Cookies)> PollAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow >= expires) return ("二维码已过期，请刷新", null);
        var data = BiliClient.Unpack(await BiliClient.ReadJsonAsync(client, "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(key), ct));
        return data.Number("code") switch
        {
            0 => ("success", jar.GetAllCookies().Where(c => BiliClient.IsBiliDomain(c.Domain)).GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.Last().Value)),
            86101 => ("请用哔哩哔哩 App 扫码", null),
            86090 => ("已扫码，请在手机确认登录", null),
            _ => ("二维码已过期，请刷新", null)
        };
    }
    public void Dispose() => client.Dispose();
}

public static class BrowserLogin
{
    public static async Task<Dictionary<string, string>> LoginAsync(IProgress<string> progress, CancellationToken ct)
    {
        using var playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        try
        {
            foreach (var channel in new string?[] { "msedge", "chrome", null })
            {
                ct.ThrowIfCancellationRequested();
                try { browser = await playwright.Chromium.LaunchAsync(new() { Channel = channel, Headless = false, Timeout = 15000 }); break; }
                catch (PlaywrightException) { }
            }
            if (browser is null) throw new BiliException("未找到浏览器，请安装 Edge 或 Chrome 后重试。");
            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            await page.GotoAsync("https://passport.bilibili.com/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }).WaitAsync(ct);
            progress.Report("请在官方窗口选择密码或短信登录，完成后自动同步。");
            for (var i = 0; i < 150; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!browser.IsConnected || context.Pages.Count == 0) throw new BiliException("登录窗口已关闭。");
                var cookies = (await context.CookiesAsync(["https://www.bilibili.com", "https://api.bilibili.com"]))
                    .Where(c => BiliClient.IsBiliDomain(c.Domain)).GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.Last().Value);
                if (cookies.ContainsKey("SESSDATA")) return cookies;
                await Task.Delay(2000, ct);
            }
            throw new BiliException("账号登录超时，请重新打开窗口。");
        }
        finally { if (browser is not null) await browser.DisposeAsync(); }
    }
}
