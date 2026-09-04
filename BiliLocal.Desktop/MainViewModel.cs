using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using BiliLocal.Core;

namespace BiliLocal.Desktop;

public sealed class VideoRow(VideoItem video) : Observable, IDisposable
{
    public VideoItem Video { get; } = video;
    private Bitmap? cover;
    public Bitmap? Cover { get => cover; set => Set(ref cover, value); }
    public void Dispose() => Cover?.Dispose();
}
public sealed class JobRow(DownloadJob job) : Observable
{
    private DownloadJob model = job;
    public DownloadJob Job { get => model; set => Set(ref model, value); }
}
public sealed class MainViewModel : Observable
{
    public ObservableCollection<VideoRow> Videos { get; } = [];
    public ObservableCollection<JobRow> Jobs { get; } = [];
    public ObservableCollection<VideoCollection> Collections { get; } = [VideoCollection.All];
    private string up = "", keyword = "", message = "就绪：输入 UP 主 UID 或空间链接开始。", account = "尚未登录", collection = "视频资料库", pageText = "尚未获取视频", selection = "已选 0 个视频", queueSummary = "暂无下载任务", environment = "";
    private bool busy, library = true;
    public string Up { get => up; set => Set(ref up, value); }
    public string Keyword { get => keyword; set => Set(ref keyword, value); }
    public string Message { get => message; set => Set(ref message, value); }
    public string Account { get => account; set => Set(ref account, value); }
    public string Collection { get => collection; set => Set(ref collection, value); }
    public string PageText { get => pageText; set => Set(ref pageText, value); }
    public string Selection { get => selection; set => Set(ref selection, value); }
    public string QueueSummary { get => queueSummary; set => Set(ref queueSummary, value); }
    private bool hasJobs, canStartJobs, canPauseJobs;
    public bool HasJobs { get => hasJobs; set => Set(ref hasJobs, value); }
    public bool CanStartJobs { get => canStartJobs; set => Set(ref canStartJobs, value); }
    public bool CanPauseJobs { get => canPauseJobs; set => Set(ref canPauseJobs, value); }
    public string DownloadDirectory { get => environment; set => Set(ref environment, value); }
    private bool sidebarExpanded = true;
    public bool SidebarExpanded { get => sidebarExpanded; set { if (Set(ref sidebarExpanded, value)) Notify(nameof(SidebarWidth)); } }
    public double SidebarWidth => SidebarExpanded ? 190 : 56;
    public bool Busy { get => busy; set { if (Set(ref busy, value)) Notify(nameof(Ready)); } }
    public bool Ready => !Busy;
    public bool Library { get => library; set { if (Set(ref library, value)) Notify(nameof(Queue)); } }
    public bool Queue => !Library;
}
