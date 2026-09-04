using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliLocal.Core;

public sealed partial class BiliClient
{
    public static bool? ChargingFlag(JsonElement item)
    {
        var found = false;
        foreach (var field in new[] { "is_charging_arc", "is_upower_exclusive", "is_upower_exclusive_with_qa", "is_upower_preview" })
        {
            if (!item.TryGetProperty(field, out var value)) continue;
            found = true;
            if (value.ValueKind == JsonValueKind.True || value.ToString() == "1") return true;
        }
        // is_pay and ugc_pay describe other paid products, not charging exclusivity.
        return found ? false : null;
    }
    public static VideoItem ParseVideo(JsonElement item)
    {
        var duration = item.Text("length");
        if (duration.Length == 0) { var seconds = item.Number("duration"); duration = $"{seconds / 60:00}:{seconds % 60:00}"; }
        return new()
        {
            Bvid = item.Text("bvid"), Title = item.Text("title"), Author = item.Text("author"), Cover = item.Text("pic"), Duration = duration,
            Created = item.Number("created") != 0 ? item.Number("created") : item.Number("pubdate"),
            Views = item.TryGetProperty("stat", out var stat) ? stat.Number("view") : item.Number("play"), Favorites = item.Number("favorites"), IsCharging = ChargingFlag(item)
        };
    }
    public static IReadOnlyList<VideoCollection> ParseCollections(JsonElement lists)
    {
        List<VideoCollection> result = [];
        foreach (var (field, kind, idField) in new[] { ("seasons_list", "season", "season_id"), ("series_list", "series", "series_id") })
        {
            if (!lists.TryGetProperty(field, out var values) || values.ValueKind != JsonValueKind.Array) continue;
            foreach (var value in values.EnumerateArray())
            {
                var meta = value.GetProperty("meta"); var id = meta.Text(idField);
                if (!Regex.IsMatch(id, "^[1-9][0-9]*$")) continue;
                result.Add(new(id, meta.Text("title") is { Length: > 0 } title ? title : meta.Text("name"), kind, (int)meta.Number("total")));
            }
        }
        return result;
    }
    public async Task<IReadOnlyList<VideoCollection>> GetCollectionsAsync(string up, CancellationToken ct = default)
    {
        var uid = ParseUid(up); using var client = CreateHttpClient();
        List<VideoCollection> result = [];
        for (var page = 1; ; page++)
        {
            var data = Unpack(await ReadJsonAsync(client, Api + $"/x/polymer/web-space/seasons_series_list?mid={uid}&page_num={page}&page_size=20", ct));
            var lists = data.GetProperty("items_lists"); var batch = ParseCollections(lists);
            var fresh = batch.Where(c => !result.Any(old => old.Id == c.Id && old.Kind == c.Kind)).ToList();
            result.AddRange(fresh);
            if (batch.Count == 0 || result.Count >= lists.GetProperty("page").Number("total")) break;
            if (fresh.Count == 0) throw new BiliException("合集目录分页未前进，请稍后刷新。");
            await Task.Delay(200, ct);
        }
        return result;
    }
    public async Task<VideoPage> GetCollectionVideosAsync(string up, VideoCollection collection, int page, CancellationToken ct = default)
    {
        var uid = ParseUid(up);
        if (!Regex.IsMatch(collection.Id, "^[1-9][0-9]*$") || collection.Kind is not ("season" or "series") || page < 1) throw new BiliException("无效合集或系列。");
        using var client = CreateHttpClient();
        var path = collection.Kind == "season"
            ? $"/x/polymer/web-space/seasons_archives_list?mid={uid}&season_id={collection.Id}&sort_reverse=false&page_num={page}&page_size=30"
            : $"/x/series/archives?mid={uid}&series_id={collection.Id}&only_normal=true&sort=desc&pn={page}&ps=30";
        var data = Unpack(await ReadJsonAsync(client, Api + path, ct));
        var items = data.GetProperty("archives").EnumerateArray().Select(ParseVideo).Where(v => IsBvid(v.Bvid)).ToList();
        return new(uid, collection.Name, items, (int)data.GetProperty("page").Number("total"), page);
    }
    public async Task<bool> GetChargingStatusAsync(string bvid, CancellationToken ct = default)
    {
        if (!IsBvid(bvid)) throw new BiliException("BV 号无效。");
        using var client = CreateHttpClient();
        var data = Unpack(await ReadJsonAsync(client, Api + "/x/web-interface/view?bvid=" + bvid, ct));
        return ChargingFlag(data) ?? throw new BiliException("Bilibili 未返回充电属性，请稍后重试。");
    }
}

public interface IVideoLibrarySource
{
    Task<VideoPage> GetVideosAsync(string up, int page = 1, string order = "pubdate", string keyword = "", CancellationToken ct = default, bool chargingOnly = false);
    Task<VideoPage> GetCollectionVideosAsync(string up, VideoCollection collection, int page, CancellationToken ct = default);
    Task<bool> GetChargingStatusAsync(string bvid, CancellationToken ct = default);
}

public sealed class VideoLibrary(IVideoLibrarySource source)
{
    private readonly Dictionary<string, (DateTimeOffset Time, VideoPage Page)> cache = [];
    public void Clear() => cache.Clear();
    public async Task<VideoPage> GetPageAsync(string up, VideoCollection collection, int page, string order, string keyword, ChargeFilter filter, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var uid = BiliClient.ParseUid(up); var all = collection.Kind == "all";
        keyword = keyword.Trim();
        ct.ThrowIfCancellationRequested();
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        if (all && filter == ChargeFilter.OnlyCharging && keyword.Length == 0) return await source.GetVideosAsync(uid, page, order, "", ct, true);
        if (!all && filter == ChargeFilter.All && keyword.Length == 0) return await source.GetCollectionVideosAsync(uid, collection, page, ct);
        // The charging endpoint ignores keyword. Scan the chosen source without a keyword,
        // then intersect title and charging filters before pagination; cache across keywords.
        var key = $"{uid}:{collection.Kind}:{collection.Id}:{order}:{(all ? filter == ChargeFilter.OnlyCharging ? "charging" : "all" : "collection")}";
        if (!cache.TryGetValue(key, out var entry) || DateTimeOffset.UtcNow - entry.Time > TimeSpan.FromMinutes(5))
        {
            List<VideoItem> videos = []; var seen = new HashSet<string>(); VideoPage? first = null;
            for (var pn = 1; ; pn++)
            {
                ct.ThrowIfCancellationRequested(); progress?.Report($"正在建立筛选列表，第 {pn} 页…");
                var batch = all ? await source.GetVideosAsync(uid, pn, order, "", ct, filter == ChargeFilter.OnlyCharging) : await source.GetCollectionVideosAsync(uid, collection, pn, ct);
                first ??= batch;
                var fresh = batch.Items.Where(v => seen.Add(v.Bvid)).ToList(); videos.AddRange(fresh);
                if (pn * 30 >= batch.Total) break;
                if (batch.Items.Count == 0 || fresh.Count == 0) throw new BiliException("视频分页不完整，请刷新后重试。");
                await Task.Delay(200, ct);
            }
            if (all && filter != ChargeFilter.OnlyCharging)
            {
                // Charging-only lists can include entries whose ordinary-list flag is missing/stale.
                // Reconcile by BV id, never subtract the endpoint counts from one another.
                var chargingIds = new HashSet<string>();
                for (var pn = 1; ; pn++)
                {
                    progress?.Report($"正在核对充电专属列表，第 {pn} 页…");
                    var charging = await source.GetVideosAsync(uid, pn, order, "", ct, true);
                    var added = charging.Items.Count(v => chargingIds.Add(v.Bvid));
                    // Some charging videos are absent from the ordinary upload feed.
                    // Include them so “全部视频” is a superset of “仅充电视频”.
                    foreach (var video in charging.Items) if (seen.Add(video.Bvid)) videos.Add(video);
                    if (pn * 30 >= charging.Total) break;
                    if (added == 0) throw new BiliException("充电列表分页不完整，请稍后重试。");
                    await Task.Delay(200, ct);
                }
                foreach (var video in videos) if (chargingIds.Contains(video.Bvid)) video.IsCharging = true;
                videos = order switch
                {
                    "pubdate" => videos.OrderByDescending(v => v.Created).ToList(),
                    "click" => videos.OrderByDescending(v => v.Views).ToList(),
                    "stow" => videos.OrderByDescending(v => v.Favorites).ToList(),
                    _ => videos
                };
            }
            entry = (DateTimeOffset.UtcNow, first! with { Items = videos, Total = videos.Count });
            progress?.Report($"已读取 {videos.Count} 个不重复视频（接口计数 {first!.Total}），其中充电标记 {videos.Count(v => v.IsCharging == true)} 个。");
            cache[key] = entry;
        }
        var matches = entry.Page.Items.Where(v => v.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) || v.Bvid.Equals(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filter != ChargeFilter.All)
        {
            var unknown = matches.Where(v => v.IsCharging == null).ToList();
            for (var i = 0; i < unknown.Count; i++)
            {
                progress?.Report($"正在识别充电视频 {i + 1}/{unknown.Count}…");
                unknown[i].IsCharging = await source.GetChargingStatusAsync(unknown[i].Bvid, ct);
                if (i + 1 < unknown.Count) await Task.Delay(150, ct);
            }
        }
        var filtered = matches.Where(v => filter == ChargeFilter.All || v.IsCharging == (filter == ChargeFilter.OnlyCharging)).ToList();
        return entry.Page with { Items = filtered.Skip((page - 1) * 30).Take(30).ToList(), Total = filtered.Count, Page = page };
    }
}
