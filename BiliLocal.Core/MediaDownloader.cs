using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliLocal.Core;

public interface IMediaDownloader
{
    Task<IReadOnlyList<string>> DownloadAsync(DownloadJob job, IProgress<TransferProgress> progress, CancellationToken ct);
}

public sealed class MediaDownloader(BiliClient bili, AppPaths paths) : IMediaDownloader
{
    public static string SafeName(string value)
    {
        var name = Regex.Replace(value, "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim().TrimEnd('.');
        if (name.Length > 80) name = name[..80];
        if (name.Length > 0 && char.IsHighSurrogate(name[^1])) name = name[..^1];
        name = name.TrimEnd(' ', '.');
        if (Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)) name = "_" + name;
        return string.IsNullOrWhiteSpace(name) ? "video" : name;
    }
    public static string OutputName(string title, VideoPart part, bool multipart, string mode)
        => SafeName(title) + (multipart ? " - " + SafeName(string.IsNullOrWhiteSpace(part.Title) ? $"P{part.Page}" : part.Title) : "") + (mode == "audio" ? ".mp3" : ".mp4");
    public static JsonElement SelectVideo(JsonElement dash, string quality)
    {
        var cap = int.TryParse(quality, out var number) ? number : int.MaxValue;
        // Resolution caps refer to the shorter axis for portrait videos as well.
        var streams = dash.GetProperty("video").EnumerateArray().Where(v => Math.Min(v.Number("width"), v.Number("height")) <= cap).ToList();
        if (streams.Count == 0) throw new BiliException("没有符合所选分辨率上限的视频流，请选择更高画质。");
        return streams.OrderByDescending(v => Math.Min(v.Number("width"), v.Number("height")))
            .ThenByDescending(v => v.Text("codecs").StartsWith("avc"))
            .ThenByDescending(v => v.Number("bandwidth")).First();
    }
    private static string StreamUrl(JsonElement stream) => stream.Text("baseUrl") is { Length: > 0 } url ? url : stream.Text("base_url");
    private static Uri ValidateMediaUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.IsLoopback || IPAddress.TryParse(uri.Host, out _))
            throw new BiliException("Bilibili 返回无效媒体地址。");
        return uri;
    }
    public static IReadOnlyList<VideoPart> SelectParts(VideoDetails details, DownloadJob job)
    {
        if (job.SelectedParts == null) return job.AllParts ? details.Parts : details.Parts.Take(1).ToList();
        var selected = job.SelectedParts.Select(p => p.Cid).ToHashSet();
        if (selected.Count == 0) throw new BiliException("请至少选择一个分 P。");
        var parts = details.Parts.Where(p => selected.Contains(p.Cid)).ToList();
        if (parts.Count != selected.Count) throw new BiliException("所选分 P 已失效或被移除，请重新选择分 P 后添加任务。");
        return parts;
    }
    public async Task<IReadOnlyList<string>> DownloadAsync(DownloadJob job, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        var ffmpeg = AppPaths.FindTool("ffmpeg") ?? throw new BiliException("内置 FFmpeg 缺失，请重新解压完整软件包。");
        var results = new DownloadOutput(paths, job);
        var legacyFolder = paths.JobFolder(job.Id, job.OutputDirectory);
        // Reuse old partial streams when upgrading an unfinished job; new work stays in app data.
        var folder = Directory.Exists(legacyFolder) ? legacyFolder : paths.JobCache(job.Id);
        Directory.CreateDirectory(folder);
        var details = await bili.GetDetailsAsync(job.Bvid, ct);
        var parts = SelectParts(details, job);
        if (parts.Count == 0) throw new BiliException("该视频没有可下载的分 P。");
        List<string> output = [];
        using var client = bili.CreateHttpClient();
        for (var index = 0; index < parts.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var part = parts[index];
            var label = details.Parts.Count > 1 ? $"{details.Title} · P{part.Page} {part.Title}" : details.Title;
            var finalName = OutputName(details.Title, part, details.Parts.Count > 1, job.Mode);
            if (results.Completed(part.Cid) is { } completed) { output.Add(completed); continue; }
            var legacyFinal = Path.Combine(legacyFolder, $"{SafeName(details.Title)} [{job.Bvid}_p{part.Page}]" + Path.GetExtension(finalName));
            if (File.Exists(legacyFinal)) { output.Add(results.Commit(legacyFinal, part.Cid, finalName)); continue; }
            var play = await bili.GetPlayAsync(job.Bvid, part.Cid, ct, job.Quality);
            var temporary = Path.Combine(folder, $"{part.Cid}.processing" + Path.GetExtension(finalName));
            List<string> media = [];
            List<string> arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y"];
            if (play.TryGetProperty("dash", out var dash) && dash.ValueKind == JsonValueKind.Object)
            {
                var audio = dash.TryGetProperty("audio", out var audios) && audios.ValueKind == JsonValueKind.Array
                    ? audios.EnumerateArray().OrderByDescending(a => a.Number("bandwidth")).ToArray() : [];
                if (job.Mode == "video")
                {
                    var video = SelectVideo(dash, job.Quality);
                    var file = Path.Combine(folder, $"{part.Cid}.v{video.Number("id")}-{video.Number("codecid")}.part");
                    await TransferAsync(client, StreamUrl(video), file, new InlineProgress<(double, double)>(p => progress.Report(new((index + p.Item1 / 100 * .65) / parts.Count * 100, p.Item2, label))), ct);
                    arguments.AddRange(["-i", file]); media.Add(file);
                }
                if (audio.Length > 0)
                {
                    var file = Path.Combine(folder, $"{part.Cid}.a{audio[0].Number("id")}.part");
                    await TransferAsync(client, StreamUrl(audio[0]), file, new InlineProgress<(double, double)>(p => progress.Report(new((index + (job.Mode == "audio" ? p.Item1 / 100 * .95 : .65 + p.Item1 / 100 * .3)) / parts.Count * 100, p.Item2, label))), ct);
                    arguments.AddRange(["-i", file]); media.Add(file);
                }
                else if (job.Mode == "audio") throw new BiliException("该视频没有可用音轨。");
                if (job.Mode == "video") arguments.AddRange(audio.Length > 0 ? ["-map", "0:v:0", "-map", "1:a:0", "-c", "copy"] : ["-c", "copy"]);
            }
            else if (play.TryGetProperty("durl", out var durl) && durl.ValueKind == JsonValueKind.Array && durl.GetArrayLength() > 0)
            {
                var segments = durl.EnumerateArray().ToArray();
                for (var s = 0; s < segments.Length; s++)
                {
                    var file = Path.Combine(folder, $"{part.Cid}.segment{s}.part");
                    await TransferAsync(client, segments[s].Text("url"), file, new InlineProgress<(double, double)>(p => progress.Report(new((index + (s + p.Item1 / 100) / segments.Length * .95) / parts.Count * 100, p.Item2, label))), ct);
                    media.Add(file);
                }
                var list = Path.Combine(folder, $"{part.Cid}.concat.txt");
                await File.WriteAllLinesAsync(list, media.Select(f => "file '" + Path.GetFileName(f) + "'"), ct);
                arguments.AddRange(["-f", "concat", "-safe", "1", "-i", list]); media.Add(list);
                if (job.Mode == "video") arguments.AddRange(["-c", "copy"]);
            }
            else throw new BiliException("没有可下载的视频流，请检查当前账号的播放权限。");
            if (job.Mode == "audio") arguments.AddRange(["-vn", "-c:a", "libmp3lame", "-b:a", "192k"]);
            else arguments.AddRange(["-movflags", "+faststart"]);
            arguments.Add(temporary);
            progress.Report(new((index + .96) / parts.Count * 100, 0, label, true));
            try
            {
                await RunProcessAsync(ffmpeg, arguments, ct);
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new BiliException("FFmpeg 没有生成有效文件。");
                var final = results.Commit(temporary, part.Cid, finalName);
                foreach (var file in media) File.Delete(file);
                output.Add(final);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return output;
    }
    public static async Task TransferAsync(HttpClient client, string url, string file, IProgress<(double, double)> progress, CancellationToken ct)
    {
        var uri = ValidateMediaUrl(url);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var offset = File.Exists(file) ? new FileInfo(file).Length : 0;
                using var response = await SendMediaAsync(client, uri, offset, ct);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && response.Content.Headers.ContentRange?.Length == offset && offset > 0) { progress.Report((100, 0)); return; }
                response.EnsureSuccessStatusCode();
                var resume = response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == offset;
                if (response.StatusCode == HttpStatusCode.PartialContent && !resume) throw new IOException("远程文件续传位置不一致。");
                if (!resume) offset = 0;
                var total = response.Content.Headers.ContentLength.GetValueOrDefault() + offset;
                await using var output = new FileStream(file, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 131072, true);
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[131072]; long received = 0, previousReceived = 0; var timer = Stopwatch.StartNew(); var last = TimeSpan.Zero;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), ct); received += count;
                    if (timer.Elapsed - last > TimeSpan.FromMilliseconds(250))
                    {
                        var now = timer.Elapsed;
                        progress.Report((total > 0 ? (offset + received) * 100d / total : 0, (received - previousReceived) / Math.Max(.01, (now - last).TotalSeconds)));
                        last = now; previousReceived = received;
                    }
                }
                if (total > 0 && offset + received != total) throw new IOException("下载未完成。");
                progress.Report((100, 0)); return;
            }
            catch (Exception e) when (attempt < 2 && !ct.IsCancellationRequested &&
                (e is IOException or TimeoutException or TaskCanceledException || e is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests } || e is HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError }))
            { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
    }
    private static async Task<HttpResponseMessage> SendMediaAsync(HttpClient client, Uri uri, long offset, CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var hops = 0; ; hops++)
        {
            if (hops > 5 || !visited.Add(uri.AbsoluteUri)) throw new BiliException("媒体地址跳转次数过多，请稍后重试。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null) throw new BiliException("媒体地址跳转缺少目标地址。");
            // Keep Range on each hop. The cookie container only sends cookies to their own domain.
            uri = ValidateMediaUrl(new Uri(uri, location).AbsoluteUri);
        }
    }
    public static async Task RunProcessAsync(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 FFmpeg。");
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        // Cancellation kills FFmpeg, then waits for its handles to close before resume/delete completes.
        await process.WaitForExitAsync(); await stdout;
        var error = await stderr;
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new BiliException("FFmpeg 处理失败：" + Redact(error));
    }
    public static string Redact(string message)
    {
        var clean = Regex.Replace(message, "https?://\\S+", "[远程地址]");
        clean = Regex.Replace(clean, "(?i)(SESSDATA|bili_jct|cookie|authorization)\\s*[:=]\\s*\\S+", "$1=[已隐藏]");
        return clean.Length > 400 ? clean[^400..] : clean;
    }
}
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
