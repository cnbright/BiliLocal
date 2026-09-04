using BiliLocal.Core;
using Xunit;

namespace BiliLocal.Tests;
public sealed class OutputTests
{
    [Theory]
    [InlineData("旅行记录", "上集", false, "video", "旅行记录.mp4")]
    [InlineData("旅行记录", "上集", true, "video", "旅行记录 - 上集.mp4")]
    [InlineData("旅行记录", "下集", true, "audio", "旅行记录 - 下集.mp3")]
    [InlineData("旅行记录", "", true, "video", "旅行记录 - P2.mp4")]
    [InlineData("CON", "问题:答案?", true, "video", "_CON - 问题_答案_.mp4")]
    public void NamesUseTitleAndOptionalPartName(string title, string part, bool multiple, string mode, string expected)
        => Assert.Equal(expected, MediaDownloader.OutputName(title, new(1, 2, part), multiple, mode));

    [Fact] public async Task ConcurrentSameNamesNeverOverwriteExistingVideos()
    {
        var root = Path.Combine(Path.GetTempPath(), "bililocal-output-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root); var existing = Path.Combine(paths.Downloads, "视频.mp4"); await File.WriteAllTextAsync(existing, "original");
            var outputs = await Task.WhenAll(Enumerable.Range(1, 4).Select(i => Task.Run(() =>
            {
                var job = new DownloadJob { OutputDirectory = paths.Downloads }; var result = new DownloadOutput(paths, job);
                var source = Path.Combine(paths.JobCache(job.Id), "processed.mp4"); File.WriteAllText(source, "video" + i);
                return result.Commit(source, 1, "视频.mp4");
            })));
            Assert.Equal(4, outputs.Distinct().Count()); Assert.Equal("original", await File.ReadAllTextAsync(existing));
            Assert.All(outputs, path => Assert.Equal(paths.Downloads, Path.GetDirectoryName(path)));
            Assert.Empty(Directory.GetDirectories(paths.Downloads));
            Assert.Equal(new[] { "video1", "video2", "video3", "video4" }, outputs.Select(File.ReadAllText).OrderBy(x => x));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void PartReceiptSurvivesRestartAndDoesNotConfuseOtherSameNamedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "bililocal-output-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root); var job = new DownloadJob { OutputDirectory = paths.Downloads };
            var output = new DownloadOutput(paths, job); var source = Path.Combine(paths.JobCache(job.Id), "merge.mp4"); File.WriteAllText(source, "media");
            var final = output.Commit(source, 101, "标题 - 第一集.mp4");
            Assert.Equal(final, new DownloadOutput(new AppPaths(root), job).Completed(101));
            Assert.Null(output.Completed(102));
            File.Delete(final); Assert.Null(output.Completed(101));
            File.WriteAllText(final, "an unrelated replacement"); Assert.Null(output.Completed(101));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void FileLinksSupportNewFlatPathsAndOldTaskFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "bililocal-output-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root); var job = new DownloadJob { OutputDirectory = paths.Downloads };
            var flat = Path.Combine(paths.Downloads, "标题.mp4"); File.WriteAllText(flat, "flat"); job.Files = [flat];
            Assert.Equal(flat, paths.ResolveJobFile(job, flat)); Assert.Equal(paths.Downloads, paths.OutputFolder(job));
            var legacy = paths.JobFolder(job.Id, job.OutputDirectory); Directory.CreateDirectory(legacy); File.WriteAllText(Path.Combine(legacy, "old.mp4"), "old"); job.Files = ["old.mp4"];
            Assert.Equal(legacy, paths.OutputFolder(job));
        }
        finally { Directory.Delete(root, true); }
    }
}
