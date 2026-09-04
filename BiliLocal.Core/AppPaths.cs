using System.Diagnostics;
using System.Text.Json;

namespace BiliLocal.Core;

public sealed class AppPaths
{
    public string Root { get; }
    public string LegacyDownloads => Path.Combine(Root, "downloads");
    public string Downloads { get; private set; }
    public int DownloadConcurrency { get; private set; } = 3;
    public string Data => Path.Combine(Root, "data");
    public AppPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Environment.GetEnvironmentVariable("BILILOCAL_HOME") ?? FindRoot());
        Directory.CreateDirectory(Data);
        Downloads = LegacyDownloads;
        var settings = Path.Combine(Data, "settings.json");
        if (File.Exists(settings))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<DownloadSettings>(File.ReadAllText(settings));
                if (saved?.Concurrency is >= 1 and <= 6) DownloadConcurrency = saved.Concurrency;
                if (saved?.DownloadDirectory is { Length: > 0 } directory && Path.IsPathFullyQualified(directory)) Downloads = Path.GetFullPath(directory);
            }
            catch (Exception error) when (error is JsonException or IOException or ArgumentException) { }
        }
        // A saved destination can be an offline removable drive; don't prevent opening the app.
        if (Downloads == LegacyDownloads) Directory.CreateDirectory(Downloads);
    }
    private sealed record DownloadSettings(string DownloadDirectory, int Concurrency = 3);
    private void SaveSettings(string directory, int concurrency)
    {
        var file = Path.Combine(Data, "settings.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new DownloadSettings(directory, concurrency)));
        File.Move(file + ".tmp", file, true);
    }
    public void SetDownloadConcurrency(int value)
    {
        if (value is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(value), "并行任务数应为 1–6。");
        SaveSettings(Downloads, value); DownloadConcurrency = value;
    }
    public void SetDownloads(string directory)
    {
        directory = directory.Trim();
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("请选择或输入完整的下载目录路径。");
        var fullPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullPath);
        var probe = Path.Combine(fullPath, ".bililocal-write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        SaveSettings(fullPath, DownloadConcurrency);
        Downloads = fullPath;
    }
    private static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var path = new DirectoryInfo(start); path != null; path = path.Parent)
                if (File.Exists(Path.Combine(path.FullName, "BiliLocal.sln"))) return path.FullName;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliLocal");
    }
    public string JobFolder(string id, string? downloadDirectory = null)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("无效任务 ID。");
        return Path.Combine(string.IsNullOrWhiteSpace(downloadDirectory) ? Downloads : downloadDirectory, id);
    }
    public string JobCache(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("无效任务 ID。");
        return Path.Combine(Data, "transfers", id);
    }
    public string ResolveJobFile(DownloadJob job, string file) => Path.IsPathFullyQualified(file) ? file : Path.Combine(JobFolder(job.Id, job.OutputDirectory), file);
    public string OutputFolder(DownloadJob job)
    {
        var file = job.Files.Select(f => ResolveJobFile(job, f)).FirstOrDefault(File.Exists);
        return file != null ? Path.GetDirectoryName(file)! : string.IsNullOrWhiteSpace(job.OutputDirectory) ? Downloads : job.OutputDirectory;
    }
    public static string? FindTool(string name)
    {
        var file = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        foreach (var folder in new[] { Path.Combine(AppContext.BaseDirectory, "tools"), AppContext.BaseDirectory }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)))
            if (!string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder.Trim('"'), file))) return Path.Combine(folder.Trim('"'), file);
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (OperatingSystem.IsWindows() && Directory.Exists(winget))
            foreach (var folder in Directory.EnumerateDirectories(winget, "Gyan.FFmpeg*"))
            {
                var tool = Directory.EnumerateFiles(folder, file, SearchOption.AllDirectories).FirstOrDefault();
                if (tool != null) return tool;
            }
        return null;
    }
    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
