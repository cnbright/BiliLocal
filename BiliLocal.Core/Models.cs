using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BiliLocal.Core;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(name); return true;
    }
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed record UserInfo(string Uid, string Name, bool Vip);
public sealed class VideoItem : Observable
{
    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Duration { get; set; } = "";
    public long Views { get; set; }
    public long Favorites { get; set; }
    public long Created { get; set; }
    public bool? IsCharging { get; set; }
    private bool selected;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public string Meta => $"{(IsCharging == true ? "⚡ 充电专属  ·  " : "")}{Duration}  ·  {(Views >= 10000 ? $"{Views / 10000d:0.0} 万" : Views)} 播放  ·  {Bvid}";
}
public enum ChargeFilter { All, OnlyCharging, ExcludeCharging }
public sealed record VideoCollection(string Id, string Name, string Kind, int Count)
{
    public static VideoCollection All { get; } = new("", "全部视频", "all", 0);
    public override string ToString() => Kind == "all" ? Name : $"[{(Kind == "season" ? "合集" : "系列")}] {Name}（{Count}）";
}
public sealed record VideoPage(string Uid, string Name, IReadOnlyList<VideoItem> Items, int Total, int Page);
public sealed record VideoPart(long Cid, int Page, string Title);
public sealed record VideoDetails(string Bvid, string Title, IReadOnlyList<VideoPart> Parts);
public sealed record DownloadOptions(string Quality = "best", string Mode = "video", bool AllParts = true);

public sealed class DownloadJob
{
    public string OutputDirectory { get; set; } = "";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Quality { get; set; } = "best";
    public string Mode { get; set; } = "video";
    [JsonPropertyName("all_parts")] public bool AllParts { get; set; } = true;
    public string Status { get; set; } = "queued";
    public double Progress { get; set; }
    public double Speed { get; set; }
    public string Error { get; set; } = "";
    public string Part { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public double Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    [JsonIgnore] public bool Active => Status is "queued" or "downloading" or "processing" or "pausing" or "cancelling";
    [JsonIgnore] public bool CanStart => Status is "paused" or "pausing" or "failed" or "cancelled";
    [JsonIgnore] public bool CanPause => Status is "queued" or "downloading" or "processing";
    [JsonIgnore] public string StartLabel => Status == "failed" ? "重试" : "开始";
    [JsonIgnore] public bool HasError => !string.IsNullOrWhiteSpace(Error);
    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Part) ? Title : Part;
    [JsonIgnore] public string StateLabel => Status switch { "queued" => "等待中", "downloading" => "下载中", "processing" => "合并 / 转换中", "completed" => "已完成", "failed" => "失败", "paused" or "cancelled" => "已暂停", "pausing" or "cancelling" => "暂停中", _ => Status };
    [JsonIgnore] public string Info => $"{Bvid}  ·  {(Mode == "audio" ? "MP3" : "MP4")}  ·  {(Quality == "best" ? "最高可用" : Quality + "P 上限")}  ·  {(AllParts ? "全部分 P" : "首个分 P")}";
    [JsonIgnore] public string ProgressLabel => $"{StateLabel}  {Progress:0}%  {(Speed > 0 ? $"· {Speed / 1048576:0.00} MB/s" : "")}";
}
public sealed record TransferProgress(double Percent, double Speed, string Part, bool Processing = false);
