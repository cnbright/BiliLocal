using System.Text.Json;

namespace BiliLocal.Core;

public sealed class DownloadQueue : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly AppPaths paths;
    private readonly IMediaDownloader downloader;
    private readonly Dictionary<string, DownloadJob> jobs = [];
    private sealed class Run
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
    }
    // Keep deleted runs until all file handles/processes have closed.
    private readonly Dictionary<string, Run> running = [];
    private bool allPaused, disposing;
    private Task? disposal;
    private int concurrency;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public string? RecoveryNotice { get; private set; }
    public int Concurrency { get { lock (gate) return concurrency; } }
    public bool AllPaused { get { lock (gate) return allPaused; } }
    public DownloadQueue(AppPaths paths, IMediaDownloader downloader)
    {
        this.paths = paths; this.downloader = downloader; concurrency = paths.DownloadConcurrency;
        var file = Path.Combine(paths.Data, "jobs.json");
        if (!File.Exists(file)) return;
        try
        {
            foreach (var job in JsonSerializer.Deserialize<List<DownloadJob>>(File.ReadAllText(file), Json) ?? [])
            {
                if (!Guid.TryParseExact(job.Id, "N", out _) || !BiliClient.IsBvid(job.Bvid)) continue;
                if (string.IsNullOrWhiteSpace(job.OutputDirectory)) job.OutputDirectory = paths.LegacyDownloads;
                if (job.Active || job.Status is "cancelled" or "cancelling") { job.Status = "paused"; job.Error = ""; }
                job.Speed = 0; jobs[job.Id] = job;
            }
        }
        catch (JsonException)
        {
            File.Copy(file, file + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true);
            RecoveryNotice = "任务记录损坏，已备份原文件；下载文件不受影响。";
        }
    }
    public IReadOnlyList<DownloadJob> Snapshot()
    {
        lock (gate) return jobs.Values.OrderByDescending(j => j.Created).Select(Clone).ToList();
    }
    private static DownloadJob Clone(DownloadJob job) => JsonSerializer.Deserialize<DownloadJob>(JsonSerializer.Serialize(job, Json), Json)!;
    private static bool Same(DownloadJob a, DownloadJob b) => a.Bvid == b.Bvid && a.Mode == b.Mode && a.Quality == b.Quality && a.AllParts == b.AllParts;
    private void Save()
    {
        var file = Path.Combine(paths.Data, "jobs.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(jobs.Values, Json));
        File.Move(file + ".tmp", file, true);
    }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposing, this);
    public void SetConcurrency(int value)
    {
        lock (gate)
        {
            EnsureOpen(); paths.SetDownloadConcurrency(value); concurrency = value;
            // Lowering the limit leaves current transfers intact; new starts obey it.
            Pump();
        }
    }
    public (int Added, int Skipped) Add(IEnumerable<VideoItem> videos, DownloadOptions options)
    {
        if (options.Mode is not ("video" or "audio") || options.Quality is not ("best" or "2160" or "1080" or "720" or "480" or "360")) throw new BiliException("无效下载选项。");
        var batch = videos.ToList();
        if (batch.Count is < 1 or > 1000 || batch.Any(v => !BiliClient.IsBvid(v.Bvid))) throw new BiliException("请选择 1–1000 个有效视频。");
        var added = 0; var skipped = 0;
        lock (gate)
        {
            EnsureOpen();
            foreach (var v in batch)
            {
                var job = new DownloadJob { Bvid = v.Bvid, Title = string.IsNullOrWhiteSpace(v.Title) ? v.Bvid : v.Title, Quality = options.Quality, Mode = options.Mode, AllParts = options.AllParts, OutputDirectory = paths.Downloads, Status = allPaused ? "paused" : "queued" };
                if (jobs.Values.Any(j => j.Status != "completed" && Same(j, job))) { skipped++; continue; }
                jobs[job.Id] = job; added++;
            }
            Save(); Pump();
        }
        return (added, skipped);
    }
    public void Pause(string id)
    {
        lock (gate)
        {
            EnsureOpen();
            if (jobs.TryGetValue(id, out var job)) PauseJob(job);
            Save();
        }
    }
    private void PauseJob(DownloadJob job)
    {
        if (!job.Active) return;
        job.Speed = 0; job.Error = "";
        if (running.TryGetValue(job.Id, out var run)) { job.Status = "pausing"; run.Cancellation.Cancel(); }
        else job.Status = "paused";
    }
    public int PauseAll()
    {
        lock (gate)
        {
            EnsureOpen(); allPaused = true;
            var active = jobs.Values.Where(j => j.Active).ToList();
            foreach (var job in active) PauseJob(job);
            Save(); return active.Count;
        }
    }
    public void Start(string id)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!jobs.TryGetValue(id, out var job) || !job.CanStart) return;
            if (jobs.Values.Any(j => j.Id != id && j.Active && Same(j, job))) throw new BiliException("相同视频已在队列中运行。");
            allPaused = false; job.Status = "queued"; job.Error = ""; job.Speed = 0;
            Save(); Pump();
        }
    }
    public int StartAll()
    {
        lock (gate)
        {
            EnsureOpen(); allPaused = false; var count = 0;
            foreach (var job in jobs.Values.OrderBy(j => j.Created))
            {
                if (!job.CanStart || jobs.Values.Any(j => j.Id != job.Id && j.Active && Same(j, job))) continue;
                job.Status = "queued"; job.Error = ""; job.Speed = 0; count++;
            }
            Save(); Pump(); return count;
        }
    }
    public bool Delete(string id)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!jobs.Remove(id)) return false;
            if (running.TryGetValue(id, out var run)) run.Cancellation.Cancel();
            Save(); Pump(); return true;
        }
    }
    public int DeleteAll()
    {
        lock (gate)
        {
            EnsureOpen(); var count = jobs.Count; jobs.Clear(); allPaused = false;
            foreach (var run in running.Values.ToArray()) run.Cancellation.Cancel();
            Save(); return count;
        }
    }
    // Called under gate. A restarted id waits for its old run to release its files.
    private void Pump()
    {
        if (disposing || allPaused) return;
        foreach (var job in jobs.Values.Where(j => j.Status == "queued").OrderBy(j => j.Created))
        {
            if (running.Count >= concurrency) break;
            if (running.ContainsKey(job.Id)) continue;
            var run = new Run(); running[job.Id] = run; job.Status = "downloading";
            var snapshot = Clone(job);
            run.Task = Task.Run(() => RunJobAsync(snapshot, run));
        }
    }
    private async Task RunJobAsync(DownloadJob snapshot, Run run)
    {
        var id = snapshot.Id; var ct = run.Cancellation.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            var progress = new InlineProgress<TransferProgress>(p =>
            {
                lock (gate)
                {
                    if (ct.IsCancellationRequested || !jobs.TryGetValue(id, out var job)) return;
                    job.Progress = Math.Clamp(p.Percent, 0, 99); job.Speed = p.Speed; job.Part = p.Part;
                    job.Status = p.Processing ? "processing" : "downloading";
                }
            });
            var files = await downloader.DownloadAsync(snapshot, progress, ct);
            lock (gate)
            {
                if (!ct.IsCancellationRequested && jobs.TryGetValue(id, out var job))
                { job.Files = files.ToList(); job.Status = "completed"; job.Progress = 100; job.Error = ""; }
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (!ct.IsCancellationRequested && jobs.TryGetValue(id, out var job))
                { job.Status = "failed"; job.Error = MediaDownloader.Redact(error.Message); }
            }
        }
        finally
        {
            lock (gate)
            {
                running.Remove(id);
                if (jobs.TryGetValue(id, out var job))
                { job.Speed = 0; if (job.Status == "pausing") job.Status = "paused"; }
                run.Cancellation.Dispose(); Save(); Pump();
            }
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal != null) return new(disposal);
            disposing = true;
            foreach (var job in jobs.Values) PauseJob(job);
            foreach (var run in running.Values.ToArray()) run.Cancellation.Cancel();
            Save(); disposal = Task.WhenAll(running.Values.Select(r => r.Task)); return new(disposal);
        }
    }
}
