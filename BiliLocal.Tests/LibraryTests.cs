using System.Text.Json;
using BiliLocal.Core;
using Xunit;

namespace BiliLocal.Tests;
public sealed class LibraryTests
{
    [Theory]
    [InlineData("{\"is_charging_arc\":true}", true)]
    [InlineData("{\"is_upower_exclusive\":true}", true)]
    [InlineData("{\"is_charging_arc\":false,\"is_pay\":1}", false)]
    [InlineData("{\"ugc_pay\":1}", null)]
    public void ChargingUsesExplicitFlags(string json, bool? expected)
    {
        using var doc = JsonDocument.Parse(json); Assert.Equal(expected, BiliClient.ChargingFlag(doc.RootElement));
    }
    [Fact] public void ParsesBothCollectionKindsWithSameNumericId()
    {
        using var doc = JsonDocument.Parse("""{"seasons_list":[{"meta":{"season_id":1,"title":"合集甲","total":6}}],"series_list":[{"meta":{"series_id":1,"name":"系列乙","total":8}}]}""");
        var result = BiliClient.ParseCollections(doc.RootElement);
        Assert.Equal(2, result.Count); Assert.Equal("season", result[0].Kind); Assert.Equal("series", result[1].Kind); Assert.Equal("系列乙", result[1].Name);
    }
    [Fact] public async Task ExclusionPaginatesFilteredResultsAcrossSourcePages()
    {
        var source = new Source(); var library = new VideoLibrary(source);
        var first = await library.GetPageAsync("2", VideoCollection.All, 1, "pubdate", "", ChargeFilter.ExcludeCharging);
        var second = await library.GetPageAsync("2", VideoCollection.All, 2, "pubdate", "", ChargeFilter.ExcludeCharging);
        Assert.Equal(38, first.Total); Assert.Equal(30, first.Items.Count); Assert.Equal(8, second.Items.Count);
        Assert.All(first.Items.Concat(second.Items), v => Assert.False(v.IsCharging));
        Assert.Equal(3, source.Requests); // Two ordinary pages + charging reconciliation, then cached paging.
    }
    [Fact] public async Task ChargingOnlyUsesServerSidePaging()
    {
        var source = new Source(); var library = new VideoLibrary(source);
        var result = await library.GetPageAsync("2", VideoCollection.All, 1, "pubdate", "", ChargeFilter.OnlyCharging);
        Assert.True(source.OnlyCharging); Assert.Equal(2, result.Total); Assert.Single(source.RequestedPages);
    }
    [Fact] public async Task ExclusionReconcilesStaleOrdinaryFlagsWithChargingList()
    {
        var source = new Source { StaleOrdinaryFlags = true };
        var result = await new VideoLibrary(source).GetPageAsync("2", VideoCollection.All, 1, "pubdate", "", ChargeFilter.ExcludeCharging);
        Assert.Equal(38, result.Total);
        Assert.DoesNotContain(result.Items, v => v.Bvid is "id0" or "id2");
    }
    [Fact] public async Task CollectionFilterChecksUnknownFlagsAndKeepsAccurateKeywordCount()
    {
        var source = new Source(); var library = new VideoLibrary(source);
        var result = await library.GetPageAsync("2", new("1", "合集", "season", 40), 1, "pubdate", "target", ChargeFilter.OnlyCharging);
        Assert.Equal(1, result.Total); Assert.Equal("id2", result.Items.Single().Bvid); Assert.Equal(1, source.DetailRequests);
    }
    [Fact] public async Task CancelledScanIsNotReturnedAsComplete()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VideoLibrary(new Source()).GetPageAsync("2", VideoCollection.All, 1, "pubdate", "", ChargeFilter.ExcludeCharging, ct: cancel.Token));
    }
    [Theory]
    [InlineData(ChargeFilter.All, 1, 40)]
    [InlineData(ChargeFilter.OnlyCharging, 1, 2)]
    [InlineData(ChargeFilter.ExcludeCharging, 0, 38)]
    public async Task KeywordAndVideoTypeIntersectEvenWhenServerIgnoresKeyword(ChargeFilter filter, int count, int unfiltered)
    {
        var library = new VideoLibrary(new Source());
        var result = await library.GetPageAsync("2", VideoCollection.All, 1, "pubdate", "  TARGET  ", filter);
        Assert.Equal(count, result.Total); Assert.Equal(count, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal("target", item.Title));
        var empty = await library.GetPageAsync("2", VideoCollection.All, 1, "pubdate", "missing", filter);
        Assert.Equal(0, empty.Total); Assert.Empty(empty.Items);
        var reset = await library.GetPageAsync("2", VideoCollection.All, 1, "pubdate", "", filter);
        Assert.Equal(unfiltered, reset.Total);
    }
    [Fact] public async Task ChargingKeywordSearchFindsMatchesBeyondFirstPageAndCachesAcrossKeywords()
    {
        var source = new Source(Enumerable.Range(0, 65).Select(i => new VideoItem { Bvid = "id" + i, Title = i is 31 or 61 ? "战后纪实" : "other", IsCharging = true }));
        var library = new VideoLibrary(source);
        var result = await library.GetPageAsync("2", VideoCollection.All, 1, "pubdate", "战后", ChargeFilter.OnlyCharging);
        Assert.Equal(2, result.Total); Assert.Equal(new[] { "id31", "id61" }, result.Items.Select(v => v.Bvid));
        Assert.Equal(3, source.Requests);
        var page2 = await library.GetPageAsync("2", VideoCollection.All, 2, "pubdate", "other", ChargeFilter.OnlyCharging);
        Assert.Equal(63, page2.Total); Assert.Equal(30, page2.Items.Count); Assert.Equal(3, source.Requests);
        Assert.All(page2.Items, item => Assert.Equal("other", item.Title));
    }
    [Fact] public async Task AllVideosIncludesChargingTitlesMissingFromOrdinaryFeed()
    {
        var library = new VideoLibrary(new Source { ExtraChargingVideo = true });
        var all = await library.GetPageAsync("2", VideoCollection.All, 1, "pubdate", "exclusive", ChargeFilter.All);
        Assert.Equal("charge-only", all.Items.Single().Bvid); Assert.Equal(1, all.Total);
        var excluded = await library.GetPageAsync("2", VideoCollection.All, 1, "pubdate", "exclusive", ChargeFilter.ExcludeCharging);
        Assert.Empty(excluded.Items); Assert.Equal(0, excluded.Total);
        var cleared = await library.GetPageAsync("2", VideoCollection.All, 1, "pubdate", "", ChargeFilter.All);
        Assert.Equal(41, cleared.Total);
    }
    private sealed class Source(IEnumerable<VideoItem>? videos = null) : IVideoLibrarySource
    {
        public int Requests, DetailRequests; public bool OnlyCharging; public List<int> RequestedPages = [];
        public bool StaleOrdinaryFlags;
        public bool ExtraChargingVideo;
        private readonly VideoItem[] items = videos?.ToArray() ?? Enumerable.Range(0, 40).Select(i => new VideoItem { Bvid = "id" + i, Title = i == 2 ? "target" : "other", IsCharging = i is 0 or 2 }).ToArray();
        public Task<VideoPage> GetVideosAsync(string up, int page = 1, string order = "pubdate", string keyword = "", CancellationToken ct = default, bool chargingOnly = false)
        {
            ct.ThrowIfCancellationRequested(); Requests++; RequestedPages.Add(page); OnlyCharging = chargingOnly;
            var source = items.Where(v => !chargingOnly || v.IsCharging == true).ToList();
            if (chargingOnly && ExtraChargingVideo) source.Add(new VideoItem { Bvid = "charge-only", Title = "exclusive", IsCharging = true });
            if (!chargingOnly && StaleOrdinaryFlags) source = source.Select(v => new VideoItem { Bvid = v.Bvid, Title = v.Title, IsCharging = false }).ToList();
            return Task.FromResult(new VideoPage(up, "test", source.Skip((page - 1) * 30).Take(30).ToList(), source.Count, page));
        }
        public Task<VideoPage> GetCollectionVideosAsync(string up, VideoCollection collection, int page, CancellationToken ct = default)
        {
            items[2].IsCharging = null; return GetVideosAsync(up, page, ct: ct);
        }
        public Task<bool> GetChargingStatusAsync(string bvid, CancellationToken ct = default) { DetailRequests++; return Task.FromResult(bvid == "id2"); }
    }
}
