using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BiliLocal.Core;
using Xunit;

namespace BiliLocal.Tests;
public sealed class CoreTests
{
    [Theory]
    [InlineData("2", "2")]
    [InlineData("https://space.bilibili.com/123/video?x=1", "123")]
    public void ParseUid(string value, string expected) => Assert.Equal(expected, BiliClient.ParseUid(value));
    [Theory]
    [InlineData("https://evil.example/123")]
    [InlineData("https://space.bilibili.com.evil.example/2")]
    [InlineData("../2")]
    public void RejectForeignUid(string value) => Assert.Throws<BiliException>(() => BiliClient.ParseUid(value));
    [Fact] public void CookieMarkdownAndJsonDoNotLoseFollowingFields()
    {
        var parsed = BiliClient.ParseCookies("SESSDATA=sample; bili\\_jct=test; bmg_af_sc={\"none\":{\"on\":1}}; b\\_lsid=last");
        Assert.Equal("test", parsed["bili_jct"]); Assert.Equal("last", parsed["b_lsid"]);
    }
    [Fact] public void NetscapeFiltersForeignDomains()
    {
        var parsed = BiliClient.ParseCookies("# Netscape HTTP Cookie File\n#HttpOnly_.bilibili.com\tTRUE\t/\tTRUE\t0\tSESSDATA\tgood\n.evil.example\tTRUE\t/\tTRUE\t0\tSESSDATA\tbad");
        Assert.Equal("good", parsed["SESSDATA"]);
    }
    [Fact] public void RejectHeaderInjection() => Assert.Throws<BiliException>(() => BiliClient.ParseCookies("SESSDATA=test\r\nX-Evil: yes"));
    [Fact] public void SignatureVector()
    {
        var signed = BiliClient.SignWbi(new() { ["foo"] = "114", ["bar"] = "514", ["zab"] = "1919810" }, "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png", "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png", 1702204169);
        Assert.Equal("8f6f2b5b3d485fe1886cec6a0be8c5d4", signed["w_rid"]);
    }
    [Fact] public void VideoInputDeduplicatesAndRejectsOtherHosts()
    {
        Assert.Single(BiliClient.ParseVideoInput("BV1xx411c7mD\nhttps://www.bilibili.com/video/BV1xx411c7mD/?p=2"));
        Assert.Throws<BiliException>(() => BiliClient.ParseVideoInput("https://evil.example/video/BV1xx411c7mD"));
    }
    [Fact] public void QualityCapHandlesPortraitAndPrefersAvc()
    {
        using var data = JsonDocument.Parse("""{"video":[{"width":1080,"height":1920,"codecs":"av01","bandwidth":400},{"width":1080,"height":1920,"codecs":"avc1","bandwidth":200},{"width":720,"height":1280,"codecs":"avc1","bandwidth":100}]}""");
        Assert.Equal("avc1", MediaDownloader.SelectVideo(data.RootElement, "1080").Text("codecs"));
        Assert.Equal(720, MediaDownloader.SelectVideo(data.RootElement, "720").Number("width"));
        Assert.Throws<BiliException>(() => MediaDownloader.SelectVideo(data.RootElement, "360"));
    }
    [Fact] public async Task RangeResumeUsesPartialContent()
    {
        var file = Path.GetTempFileName(); await File.WriteAllTextAsync(file, "abc");
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StringContent("def", Encoding.ASCII) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6); return response;
        }));
        try { await MediaDownloader.TransferAsync(client, "https://media.bilivideo.com/v", file, new InlineProgress<(double, double)>(_ => { }), default); Assert.Equal("abcdef", await File.ReadAllTextAsync(file)); }
        finally { File.Delete(file); }
    }
    [Fact] public async Task RangeIgnoredRestartsFile()
    {
        var file = Path.GetTempFileName(); await File.WriteAllTextAsync(file, "old");
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("new") }));
        try { await MediaDownloader.TransferAsync(client, "https://media.bilivideo.com/v", file, new InlineProgress<(double, double)>(_ => { }), default); Assert.Equal("new", await File.ReadAllTextAsync(file)); }
        finally { File.Delete(file); }
    }
    [Fact] public async Task PausedTransferClosesPartialFileAndResumesWithoutDuplicateBytes()
    {
        var file = Path.GetTempFileName(); var blocked = new PausingStream(); var requests = 0;
        using var cancel = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(request =>
        {
            if (++requests == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(blocked) };
                response.Content.Headers.ContentLength = 6; return response;
            }
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            var resumed = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StringContent("def", Encoding.ASCII) };
            resumed.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6); return resumed;
        }));
        try
        {
            var transfer = MediaDownloader.TransferAsync(client, "https://media.bilivideo.com/v", file, new InlineProgress<(double, double)>(_ => { }), cancel.Token);
            await blocked.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
            using (var released = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.Equal(3, released.Length);
            await MediaDownloader.TransferAsync(client, "https://media.bilivideo.com/v", file, new InlineProgress<(double, double)>(_ => { }), default);
            Assert.Equal("abcdef", await File.ReadAllTextAsync(file)); Assert.Equal(2, requests);
        }
        finally { cancel.Cancel(); File.Delete(file); }
    }
    [Fact] public async Task MediaRedirectPreservesResumeOffset()
    {
        var file = Path.GetTempFileName(); await File.WriteAllTextAsync(file, "abc"); var requests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            if (++requests == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = new Uri("https://other.bilivideo.com/media"); return redirect;
            }
            Assert.Equal("other.bilivideo.com", request.RequestUri!.Host);
            var result = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StringContent("def", Encoding.ASCII) };
            result.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6); return result;
        }));
        try
        {
            await MediaDownloader.TransferAsync(client, "https://media.bilivideo.com/v", file, new InlineProgress<(double, double)>(_ => { }), default);
            Assert.Equal("abcdef", await File.ReadAllTextAsync(file)); Assert.Equal(2, requests);
        }
        finally { File.Delete(file); }
    }
    [Theory]
    [InlineData("http://127.0.0.1/private")]
    [InlineData("https://media.bilivideo.com/v")]
    public async Task InvalidMediaRedirectOrLoopFailsBeforeSecondRequest(string target)
    {
        var file = Path.GetTempFileName(); var requests = 0;
        using var client = new HttpClient(new Handler(_ => { requests++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri(target); return response; }));
        try
        {
            await Assert.ThrowsAsync<BiliException>(() => MediaDownloader.TransferAsync(client, "https://media.bilivideo.com/v", file, new InlineProgress<(double, double)>(_ => { }), default));
            Assert.Equal(1, requests);
        }
        finally { File.Delete(file); }
    }
    [Fact] public async Task QueueDeduplicatesPausesAndPersistsWithoutCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "bililocal-test-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        try
        {
            await using (var queue = new DownloadQueue(paths, new WaitingDownloader()))
            {
                var item = new VideoItem { Bvid = "BV1xx411c7mD", Title = "test" };
                Assert.Equal(1, queue.Add([item], new()).Added);
                Assert.Equal(1, queue.Add([item], new()).Skipped);
                var id = queue.Snapshot()[0].Id; queue.Pause(id);
                for (var i = 0; i < 100 && queue.Snapshot()[0].Active; i++) await Task.Delay(10);
                Assert.Equal("paused", queue.Snapshot()[0].Status);
            }
            var json = await File.ReadAllTextAsync(Path.Combine(paths.Data, "jobs.json"));
            Assert.DoesNotContain("SESSDATA", json);
            await using var restored = new DownloadQueue(paths, new WaitingDownloader());
            Assert.Single(restored.Snapshot());
            Assert.Throws<ArgumentException>(() => paths.JobFolder("../evil"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task LegacyHistoryImportsCompletedFilesAndRecoversInterruptedJobs()
    {
        var root = Path.Combine(Path.GetTempPath(), "bililocal-test-" + Guid.NewGuid().ToString("N")); var paths = new AppPaths(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(paths.Data, "jobs.json"), """[{"id":"0123456789abcdef0123456789abcdef","bvid":"BV1xx411c7mD","title":"legacy","all_parts":true,"status":"processing","files":["existing.mp4"],"created":1788514503.75}]""");
            paths.SetDownloads(Path.Combine(root, "new destination"));
            await using var queue = new DownloadQueue(paths, new WaitingDownloader());
            var job = queue.Snapshot().Single(); Assert.True(job.AllParts); Assert.Equal("paused", job.Status); Assert.Equal("existing.mp4", job.Files.Single());
            Assert.Equal(paths.LegacyDownloads, job.OutputDirectory);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void DownloadDirectoryPersistsAndRejectsInvalidDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "bililocal-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root); var folder = Path.Combine(root, "自定义目录 with spaces");
            paths.SetDownloads(folder);
            Assert.Equal(folder, new AppPaths(root).Downloads);
            Assert.Empty(Directory.GetFiles(folder));
            Assert.Throws<ArgumentException>(() => paths.SetDownloads("relative-folder"));
            var file = Path.Combine(root, "not-a-folder"); File.WriteAllText(file, "test");
            Assert.Throws<IOException>(() => paths.SetDownloads(file));
            Assert.Equal(folder, paths.Downloads); Assert.Equal(folder, new AppPaths(root).Downloads);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task ChangingDirectoryKeepsEachQueuedJobsOriginalDestinationAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "bililocal-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root); var next = Path.Combine(root, "next");
            await using (var queue = new DownloadQueue(paths, new WaitingDownloader()))
            {
                queue.Add([new() { Bvid = "BV1xx411c7mD" }], new());
                paths.SetDownloads(next);
                queue.Add([new() { Bvid = "BV13x41117TL" }], new());
                Assert.Equal(paths.LegacyDownloads, queue.Snapshot().Single(j => j.Bvid == "BV1xx411c7mD").OutputDirectory);
                Assert.Equal(next, queue.Snapshot().Single(j => j.Bvid == "BV13x41117TL").OutputDirectory);
            }
            await using var restored = new DownloadQueue(new AppPaths(root), new WaitingDownloader());
            var original = restored.Snapshot().Single(j => j.Bvid == "BV1xx411c7mD");
            Assert.Equal(Path.Combine(paths.LegacyDownloads, original.Id), paths.JobFolder(original.Id, original.OutputDirectory));
            Assert.Equal(next, restored.Snapshot().Single(j => j.Bvid == "BV13x41117TL").OutputDirectory);
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(callback(request)); }
    private sealed class WaitingDownloader : IMediaDownloader
    { public async Task<IReadOnlyList<string>> DownloadAsync(DownloadJob job, IProgress<TransferProgress> progress, CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return []; } }
    private sealed class PausingStream : Stream
    {
        private bool sent;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!sent) { sent = true; Encoding.ASCII.GetBytes("abc").CopyTo(buffer); return 3; }
            Waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
