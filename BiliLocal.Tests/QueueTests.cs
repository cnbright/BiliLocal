using System.Collections.Concurrent;
using System.Threading.Channels;
using BiliLocal.Core;
using Xunit;

namespace BiliLocal.Tests;

public sealed class QueueTests
{
    private static VideoItem Video(int i) => new() { Bvid = $"BV{i:0000000000}", Title = $"video {i}" };
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    [Fact] public async Task ParallelLimitChangesWithoutInterruptingCurrentTransfers()
    {
        await using var h = new Harness(2);
        h.Queue.Add(Enumerable.Range(1, 5).Select(Video), new());
        var a = await h.Source.Next(); var b = await h.Source.Next();
        Assert.Equal(2, h.Source.Active); Assert.Equal(3, h.Queue.Snapshot().Count(j => j.Status == "queued"));
        h.Queue.SetConcurrency(3); var c = await h.Source.Next(); Assert.Equal(3, h.Source.Active);
        h.Queue.SetConcurrency(1); a.Complete();
        await Until(() => h.Queue.Snapshot().Count(j => j.Status == "completed") == 1);
        Assert.Equal(3, h.Source.StartCount); Assert.Equal(2, h.Source.Active);
        b.Complete(); await Until(() => h.Source.Active == 1); Assert.Equal(3, h.Source.StartCount);
        c.Complete(); var d = await h.Source.Next(); Assert.Equal(1, h.Source.Active);
        d.Complete(); var e = await h.Source.Next(); e.Complete();
        await Until(() => h.Queue.Snapshot().All(j => j.Status == "completed"));
        Assert.Equal(3, h.Source.Peak); Assert.Equal(1, new AppPaths(h.Paths.Root).DownloadConcurrency);
    }
    [Fact] public async Task PauseAllIncludesWaitingAndNewJobsAndStartAllSkipsCompleted()
    {
        await using var h = new Harness(2);
        h.Queue.Add(Enumerable.Range(1, 5).Select(Video), new());
        await h.Source.Next(); await h.Source.Next();
        Assert.Equal(5, h.Queue.PauseAll());
        await Until(() => h.Queue.Snapshot().All(j => j.Status == "paused"));
        h.Queue.Add([Video(6)], new()); Assert.Equal(2, h.Source.StartCount);
        Assert.All(h.Queue.Snapshot(), j => Assert.Equal("paused", j.Status));
        Assert.Equal(6, h.Queue.StartAll());
        for (var i = 0; i < 6; i++) (await h.Source.Next()).Complete();
        await Until(() => h.Queue.Snapshot().All(j => j.Status == "completed"));
        Assert.Equal(0, h.Queue.StartAll()); Assert.Equal(8, h.Source.StartCount);
    }
    [Fact] public async Task RapidPauseStartWaitsForOldFileHandlesAndPreservesProgress()
    {
        await using var h = new Harness(1, holdCleanup: true);
        h.Queue.Add([Video(1)], new()); var first = await h.Source.Next();
        first.Progress.Report(new(42, 100, "part"));
        h.Queue.Pause(first.Job.Id); await first.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, h.Queue.Snapshot().Single().Progress);
        h.Queue.Start(first.Job.Id); h.Queue.Start(first.Job.Id);
        Assert.Equal("queued", h.Queue.Snapshot().Single().Status); Assert.Equal(1, h.Source.StartCount);
        first.ReleaseCleanup(); var resumed = await h.Source.Next();
        Assert.Equal(first.Job.Id, resumed.Job.Id); Assert.Equal(1, h.Source.Peak);
        resumed.Complete(); await Until(() => h.Queue.Snapshot().Single().Status == "completed");
    }
    [Fact] public async Task DeletedRunningAndWaitingJobsNeverReturnFromLateCallbacks()
    {
        await using var h = new Harness(1, holdCleanup: true, returnAfterCancel: true);
        h.Queue.Add([Video(1), Video(2)], new()); var first = await h.Source.Next();
        var pending = h.Queue.Snapshot().Single(j => j.Id != first.Job.Id);
        Assert.True(h.Queue.Delete(pending.Id)); Assert.True(h.Queue.Delete(first.Job.Id));
        Assert.Empty(h.Queue.Snapshot()); Assert.DoesNotContain(first.Job.Id, File.ReadAllText(Path.Combine(h.Paths.Data, "jobs.json")));
        await first.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.Progress.Report(new(90, 100, "late progress"));
        h.Queue.Add([Video(3)], new()); Assert.Equal(1, h.Source.StartCount);
        first.ReleaseCleanup(); var next = await h.Source.Next(); next.Complete();
        await Until(() => h.Queue.Snapshot().Single().Status == "completed");
        Assert.Equal(Video(3).Bvid, h.Queue.Snapshot().Single().Bvid); Assert.Equal(1, h.Source.Peak);
        await h.Queue.DisposeAsync();
        await using var restored = new DownloadQueue(h.Paths, h.Source);
        Assert.Single(restored.Snapshot()); Assert.DoesNotContain(restored.Snapshot(), j => j.Id == first.Job.Id || j.Id == pending.Id);
    }
    [Fact] public async Task DeleteAllClearsEveryStateAndPreservesCompletedFiles()
    {
        await using var h = new Harness(2, holdCleanup: true, returnAfterCancel: true);
        h.Queue.Add(Enumerable.Range(1, 4).Select(Video), new());
        var first = await h.Source.Next(); await h.Source.Next(); first.Complete(); await h.Source.Next();
        var completedFolder = h.Paths.JobFolder(first.Job.Id, first.Job.OutputDirectory); Directory.CreateDirectory(completedFolder);
        var output = Path.Combine(completedFolder, "existing.mp4"); await File.WriteAllTextAsync(output, "keep");
        Assert.Equal(4, h.Queue.DeleteAll()); Assert.Empty(h.Queue.Snapshot());
        h.Source.ReleaseAll(); await Until(() => h.Source.Active == 0);
        await h.Queue.DisposeAsync();
        Assert.Equal("keep", await File.ReadAllTextAsync(output));
        await using var restored = new DownloadQueue(h.Paths, h.Source); Assert.Empty(restored.Snapshot());
    }
    [Fact] public async Task ShutdownDuringPendingResumeRestoresPausedWithoutAutoStart()
    {
        await using var h = new Harness(1, holdCleanup: true);
        h.Queue.Add([Video(1), Video(2)], new()); var first = await h.Source.Next();
        h.Queue.Pause(first.Job.Id); await first.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); h.Queue.Start(first.Job.Id);
        var closing = h.Queue.DisposeAsync().AsTask(); first.ReleaseCleanup(); await closing;
        await using var restored = new DownloadQueue(h.Paths, h.Source);
        Assert.All(restored.Snapshot(), j => Assert.Equal("paused", j.Status)); Assert.Equal(1, h.Source.StartCount);
    }
    [Fact] public async Task StartAllRetriesFailuresAndOneFailureDoesNotStopOthers()
    {
        await using var h = new Harness(2);
        h.Queue.Add([Video(1), Video(2)], new()); var a = await h.Source.Next(); var b = await h.Source.Next();
        a.Fail(); b.Complete(); await Until(() => h.Queue.Snapshot().All(j => !j.Active));
        Assert.Equal(1, h.Queue.StartAll()); (await h.Source.Next()).Complete();
        await Until(() => h.Queue.Snapshot().All(j => j.Status == "completed")); Assert.Equal(3, h.Source.StartCount);
    }
    [Fact] public async Task ConcurrencyAndDirectorySettingsPreserveEachOther()
    {
        await using var h = new Harness(2);
        var folder = Path.Combine(h.Paths.Root, "custom"); h.Paths.SetDownloads(folder); h.Queue.SetConcurrency(4);
        var reloaded = new AppPaths(h.Paths.Root); Assert.Equal(folder, reloaded.Downloads); Assert.Equal(4, reloaded.DownloadConcurrency);
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Queue.SetConcurrency(7)); Assert.Equal(4, h.Queue.Concurrency);
        reloaded.SetDownloads(Path.Combine(h.Paths.Root, "next")); Assert.Equal(4, new AppPaths(h.Paths.Root).DownloadConcurrency);
    }
    [Fact] public async Task SelectedPartsDeduplicateByCidAndSurviveRestartAndResume()
    {
        await using var h = new Harness(1);
        h.Queue.PauseAll();
        var parts = new List<VideoPart> { new(103, 3, "第三 P"), new(102, 2, "第二 P") };
        Assert.Equal((1, 0), h.Queue.Add([Video(1)], new(SelectedParts: parts)));
        Assert.Equal((0, 1), h.Queue.Add([Video(1)], new(SelectedParts: [parts[1], parts[0], parts[1]])));
        Assert.Equal((1, 0), h.Queue.Add([Video(1)], new(SelectedParts: [parts[0]])));
        parts.Clear();
        var original = h.Queue.Snapshot().Single(j => j.SelectedParts!.Count == 2);
        Assert.Equal(new long[] { 102, 103 }, original.SelectedParts!.Select(p => p.Cid));
        Assert.Contains("P2、P3", original.Info);
        Assert.False(original.AllParts);
        original.SelectedParts!.Clear();
        await h.Queue.DisposeAsync();

        await using var restored = new DownloadQueue(h.Paths, h.Source);
        var job = restored.Snapshot().Single(j => j.Id == original.Id);
        Assert.Equal(new long[] { 102, 103 }, job.SelectedParts!.Select(p => p.Cid));
        Assert.Equal("paused", job.Status);
        restored.Start(job.Id);
        var resumed = await h.Source.Next();
        Assert.Equal(new long[] { 102, 103 }, resumed.Job.SelectedParts!.Select(p => p.Cid));
        resumed.Complete();
        await Until(() => restored.Snapshot().Single(j => j.Id == job.Id).Status == "completed");
    }

    [Fact] public async Task MixedVideoSelectionsValidateBeforeAddingAnyJobs()
    {
        await using var h = new Harness(1);
        h.Queue.PauseAll();
        var first = new DownloadOptions(SelectedParts: [new(102, 2, "第二 P")]);
        Assert.Throws<BiliException>(() => h.Queue.Add([(Video(1), first), (Video(2), new(SelectedParts: []))]));
        Assert.Empty(h.Queue.Snapshot());
        Assert.Throws<BiliException>(() => h.Queue.Add([Video(1)], new(SelectedParts: [new(0, 1, "无效")])));
        Assert.Equal((2, 0), h.Queue.Add([(Video(1), first), (Video(2), new(SelectedParts: [new(203, 3, "第三 P")]))]));
        Assert.Equal(102, h.Queue.Snapshot().Single(j => j.Bvid == Video(1).Bvid).SelectedParts!.Single().Cid);
        Assert.Equal(203, h.Queue.Snapshot().Single(j => j.Bvid == Video(2).Bvid).SelectedParts!.Single().Cid);
    }

    private sealed class Attempt(DownloadJob job, IProgress<TransferProgress> progress, bool holdCleanup)
    {
        public DownloadJob Job { get; } = job;
        public IProgress<TransferProgress> Progress { get; } = progress;
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleanup { get; } = CreateCleanup(holdCleanup);
        private static TaskCompletionSource CreateCleanup(bool hold) { var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); if (!hold) result.SetResult(); return result; }
        public void Complete() => Finish.TrySetResult();
        public void Fail() => Finish.TrySetException(new IOException("simulated failure"));
        public void ReleaseCleanup() => Cleanup.TrySetResult();
    }
    private sealed class ControlledDownloader(bool holdCleanup, bool returnAfterCancel) : IMediaDownloader
    {
        private readonly Channel<Attempt> starts = Channel.CreateUnbounded<Attempt>();
        private readonly ConcurrentBag<Attempt> attempts = [];
        private int active, peak, count;
        public int Active => Volatile.Read(ref active);
        public int Peak => Volatile.Read(ref peak);
        public int StartCount => Volatile.Read(ref count);
        public async Task<Attempt> Next() => await starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public void ReleaseAll() { foreach (var attempt in attempts) { attempt.ReleaseCleanup(); attempt.Complete(); } }
        public async Task<IReadOnlyList<string>> DownloadAsync(DownloadJob job, IProgress<TransferProgress> progress, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref active); Interlocked.Increment(ref count);
            int old; do { old = peak; if (current <= old) break; } while (Interlocked.CompareExchange(ref peak, current, old) != old);
            var attempt = new Attempt(job, progress, holdCleanup); attempts.Add(attempt); starts.Writer.TryWrite(attempt);
            try
            {
                try { await attempt.Finish.Task.WaitAsync(ct); }
                catch (OperationCanceledException)
                {
                    attempt.Cancelled.TrySetResult(); await attempt.Cleanup.Task;
                    progress.Report(new(99, 999, "late callback"));
                    if (!returnAfterCancel) throw;
                }
                return ["test.mp4"];
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    private sealed class Harness : IAsyncDisposable
    {
        public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "bililocal-queue-test-" + Guid.NewGuid().ToString("N")));
        public ControlledDownloader Source { get; }
        public DownloadQueue Queue { get; }
        public Harness(int concurrency, bool holdCleanup = false, bool returnAfterCancel = false)
        {
            Paths.SetDownloadConcurrency(concurrency); Source = new(holdCleanup, returnAfterCancel); Queue = new(Paths, Source);
        }
        public async ValueTask DisposeAsync()
        {
            var closing = Queue.DisposeAsync(); Source.ReleaseAll(); await closing;
            Directory.Delete(Paths.Root, true);
        }
    }
}
