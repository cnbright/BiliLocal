using System.Text;
using System.Text.Json;
using BiliLocal.Core;

Console.OutputEncoding = Encoding.UTF8;
using var bili = new BiliClient();
async Task InspectLibrary()
{
    using var client = bili.CreateHttpClient();
    var nav = await BiliClient.ReadJsonAsync(client, BiliClient.Api + "/x/web-interface/nav");
    var wbi = nav.GetProperty("data").GetProperty("wbi_img");
    foreach (var type in new[] { "", "charging" })
    {
        var values = BiliClient.SignWbi(new() { ["mid"] = "39627524", ["pn"] = "1", ["ps"] = "30", ["special_type"] = type, ["order"] = "pubdate" }, wbi.Text("img_url"), wbi.Text("sub_url"));
        var root = await BiliClient.ReadJsonAsync(client, BiliClient.Api + "/x/space/wbi/arc/search?" + BiliClient.Query(values));
        var data = BiliClient.Unpack(root); var list = data.GetProperty("list");
        Console.WriteLine($"TYPE={type} PAGE={data.GetProperty("page")}");
        Console.WriteLine(list.TryGetProperty("slist", out var slist) ? slist : "no slist");
        Console.WriteLine(list.GetProperty("vlist").EnumerateArray().FirstOrDefault());
    }
    return;
}
if (!args.Contains("--live"))
{
    Console.WriteLine("Use --live to verify login, pagination, QR generation and actual downloads. Cookie is prompted without echo; never saved.");
    return;
}
Console.Write("Cookie (hidden): ");
var secret = new StringBuilder();
if (Console.IsInputRedirected) secret.Append(Console.ReadLine());
else while (true)
{
    var key = Console.ReadKey(true);
    if (key.Key == ConsoleKey.Enter) break;
    if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; }
    else if (!char.IsControl(key.KeyChar)) secret.Append(key.KeyChar);
}
Console.WriteLine();
try
{
    await bili.LoginAsync(secret.ToString()); secret.Clear();
    Console.WriteLine("LOGIN: PASS");
    if (args.Contains("--verify-search"))
    {
        const string uid = "39627524";
        var raw = await bili.GetVideosAsync(uid, keyword: "战后", chargingOnly: true);
        Console.WriteLine($"RAW charging+keyword: total={raw.Total}, pageMatches={raw.Items.Count(v => v.Title.Contains("战后"))}/{raw.Items.Count}");
        var searchLibrary = new VideoLibrary(bili);
        foreach (var filter in new[] { ChargeFilter.OnlyCharging, ChargeFilter.ExcludeCharging, ChargeFilter.All })
        {
            var searchResult = await searchLibrary.GetPageAsync(uid, VideoCollection.All, 1, "pubdate", "战后", filter, new InlineProgress<string>(Console.WriteLine));
            if (searchResult.Items.Any(v => !v.Title.Contains("战后") || (filter != ChargeFilter.All && v.IsCharging != (filter == ChargeFilter.OnlyCharging)))) throw new Exception("Combined search/filter mismatch.");
            Console.WriteLine($"SEARCH {filter}: total={searchResult.Total}, items={searchResult.Items.Count}");
            foreach (var item in searchResult.Items) Console.WriteLine(item.Title);
            var empty = await searchLibrary.GetPageAsync(uid, VideoCollection.All, 1, "pubdate", "__no_matching_title_72894__", filter);
            if (empty.Total != 0 || empty.Items.Count != 0) throw new Exception("No-match keyword was ignored.");
        }
        Console.WriteLine("COMBINED SEARCH: PASS"); return;
    }
    if (args.Contains("--inspect-library")) { await InspectLibrary(); return; }
    if (args.Contains("--verify-library"))
    {
        const string up = "39627524";
        var collections = await bili.GetCollectionsAsync(up);
        Console.WriteLine($"COLLECTIONS: {collections.Count}, seasons={collections.Count(c => c.Kind == "season")}, series={collections.Count(c => c.Kind == "series")}");
        var library = new VideoLibrary(bili);
        var all = await library.GetPageAsync(up, VideoCollection.All, 1, "pubdate", "", ChargeFilter.All);
        var charging = await library.GetPageAsync(up, VideoCollection.All, 1, "pubdate", "", ChargeFilter.OnlyCharging);
        var normal = await library.GetPageAsync(up, VideoCollection.All, 1, "pubdate", "", ChargeFilter.ExcludeCharging, new InlineProgress<string>(Console.WriteLine));
        Console.WriteLine($"COUNTS: all={all.Total}, charging={charging.Total}, other={normal.Total}");
        if (charging.Items.Any(v => v.IsCharging != true) || normal.Items.Any(v => v.IsCharging != false)) throw new Exception("Charging filter mismatch");
        Console.WriteLine($"FILTERS PASS: all={all.Total}, charging={charging.Total}, other={normal.Total}");
        var season = collections.First(c => c.Id == "8590372");
        var selected = await library.GetPageAsync(up, season, 1, "pubdate", "", ChargeFilter.OnlyCharging, new InlineProgress<string>(Console.WriteLine));
        Console.WriteLine($"SEASON FILTER PASS: {season.Name}, charging={selected.Total}");
        var series = collections.First(c => c.Kind == "series");
        var seriesPage = await bili.GetCollectionVideosAsync(up, series, 1);
        Console.WriteLine($"SERIES PASS: {series.Name}, total={seriesPage.Total}, page1={seriesPage.Items.Count}");
        Console.WriteLine("LIBRARY LIVE TEST: PASS"); return;
    }
    var first = await bili.GetVideosAsync("2");
    var second = await bili.GetVideosAsync("2", 2);
    Console.WriteLine($"PAGINATION: total={first.Total}, page1={first.Items.Count}, page2={second.Items.Count}");
    using (var qr = new QrLogin(bili))
    {
        var png = await qr.GenerateAsync(CancellationToken.None);
        Console.WriteLine($"QR: valid PNG = {png.Length > 100 && png[0] == 137}");
    }
    if (args.Contains("--browser"))
    {
        using var cancel = new CancellationTokenSource();
        try { await BrowserLogin.LoginAsync(new InlineProgress<string>(_ => { Console.WriteLine("BROWSER: opened official page; cancelling smoke test"); cancel.Cancel(); }), cancel.Token); }
        catch (OperationCanceledException) { Console.WriteLine("BROWSER CANCEL: PASS"); }
    }
    var paths = new AppPaths(Path.Combine(new AppPaths().Root, "artifacts", "live"));
    await using var queue = new DownloadQueue(paths, new MediaDownloader(bili, paths));
    var before = queue.Snapshot().Select(j => j.Id).ToHashSet();
    // A new diagnostic run uses fresh jobs so earlier failed trials cannot be mistaken for this run.
    foreach (var old in queue.Snapshot().Where(j => j.Status != "completed")) queue.Delete(old.Id);
    var result = queue.Add([new() { Bvid = "BV1ujNV6qEXg", Title = "帮不帮？" }, new() { Bvid = "BV1wd4y1H7RR", Title = "软 路 由" }], new());
    Console.WriteLine($"BATCH: {result.Added} jobs");
    string previous = "";
    for (var i = 0; i < 180; i++)
    {
        var jobs = queue.Snapshot().Where(j => !before.Contains(j.Id)).ToList();
        var states = string.Join(", ", jobs.Select(j => j.Bvid + ":" + j.Status));
        if (states != previous) Console.WriteLine(states);
        previous = states;
        if (jobs.Count > 0 && jobs.All(j => !j.Active))
        {
            foreach (var job in jobs)
            {
                if (job.Status != "completed") throw new BiliException(job.Error);
                foreach (var file in job.Files)
                {
                    var path = paths.ResolveJobFile(job, file);
                    if (Path.GetDirectoryName(path) != paths.Downloads) throw new Exception("Media output is not directly inside destination.");
                    var ffprobe = AppPaths.FindTool("ffprobe")!;
                    var start = new System.Diagnostics.ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=codec_type,width,height:format=duration", "-of", "json", path }) start.ArgumentList.Add(arg);
                    using var probe = System.Diagnostics.Process.Start(start)!;
                    var json = await probe.StandardOutput.ReadToEndAsync(); await probe.WaitForExitAsync();
                    using var doc = JsonDocument.Parse(json);
                    var kinds = doc.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.Text("codec_type")).ToList();
                    if (!kinds.Contains("audio") || !kinds.Contains("video")) throw new Exception("Missing video/audio stream.");
                    Console.WriteLine("MEDIA PASS: " + Path.GetFileName(file));
                }
            }
            Console.WriteLine("LIVE TEST: PASS"); return;
        }
        await Task.Delay(2000);
    }
    throw new TimeoutException("Live download test timed out.");
}
catch (Exception error) { secret.Clear(); Console.Error.WriteLine("FAIL: " + MediaDownloader.Redact(error.Message)); Environment.ExitCode = 1; }
