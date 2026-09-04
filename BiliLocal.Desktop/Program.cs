using Avalonia;

namespace BiliLocal.Desktop;
internal static class Program
{
    [STAThread] public static void Main(string[] args)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(new Core.AppPaths().Root)))[..20];
        using var instance = new Mutex(true, "BiliLocal-" + key, out var created);
        if (!created) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
