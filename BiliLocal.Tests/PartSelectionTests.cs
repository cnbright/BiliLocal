using System.Text.Json;
using BiliLocal.Core;
using Xunit;

namespace BiliLocal.Tests;

public sealed class PartSelectionTests
{
    private static readonly VideoDetails Details = new("BV1xx411c7mD", "课程",
        [new(101, 1, "第一课"), new(102, 2, "第二课"), new(103, 3, "第三课")]);

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 1)]
    public void LegacyJobsKeepTheirOriginalDownloadRange(bool allParts, int count)
    {
        var job = JsonSerializer.Deserialize<DownloadJob>("{\"all_parts\":" + (allParts ? "true" : "false") + "}")!;
        Assert.Null(job.SelectedParts);
        Assert.Equal(Details.Parts.Take(count), MediaDownloader.SelectParts(Details, job));
    }

    [Fact] public void SingleSelectionDownloadsTheChosenPartInsteadOfTheFirst()
    {
        var job = new DownloadJob { SelectedParts = [Details.Parts[1]] };
        Assert.Equal(new[] { Details.Parts[1] }, MediaDownloader.SelectParts(Details, job));
    }

    [Fact] public void MultipleSelectionsFollowCurrentVideoOrderAndMatchStableCids()
    {
        var job = new DownloadJob { SelectedParts = [new(103, 8, "旧标题"), new(102, 9, "旧标题"), new(103, 8, "旧标题")] };
        Assert.Equal(Details.Parts.Skip(1), MediaDownloader.SelectParts(Details, job));
    }

    [Fact] public void RemovedPartsAndEmptySelectionsFailInsteadOfDownloadingOtherParts()
    {
        Assert.Throws<BiliException>(() => MediaDownloader.SelectParts(Details, new() { SelectedParts = [] }));
        Assert.Throws<BiliException>(() => MediaDownloader.SelectParts(Details, new() { SelectedParts = [Details.Parts[1], new(999, 4, "已移除")] }));
    }
}
