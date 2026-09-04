using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BiliLocal.Core;

namespace BiliLocal.Desktop;
public partial class MainWindow : Window
{
    private readonly BiliClient bili = new();
    private readonly AppPaths paths;
    private readonly Core.DownloadQueue queue;
    private readonly VideoLibrary library;
    private readonly MainViewModel vm = new();
    private readonly Dictionary<string, VideoItem> selected = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer keywordTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? search;
    private int currentPage = 1, total;
    private string currentUp = "";
    private string catalogUp = "";
    private bool updatingFilters;
    private bool closing, closed;
    public MainWindow() : this([]) { }
    public MainWindow(string[] args)
    {
        var workspace = new AppPaths();
        var smoke = args.Contains("--smoke");
        paths = smoke ? new AppPaths(Path.Combine(workspace.Root, "artifacts", "ui-smoke-data")) : workspace;
        var artifacts = Path.Combine(workspace.Root, "artifacts");
        library = new(smoke ? new SmokeLibrarySource() : bili);
        queue = new(paths, smoke ? new SmokeDownloader() : new MediaDownloader(bili, paths));
        if (smoke) queue.DeleteAll();
        InitializeComponent(); DataContext = vm;
        vm.DownloadDirectory = paths.Downloads;
        ConcurrencyBox.SelectedIndex = queue.Concurrency - 1;
        if (queue.RecoveryNotice != null) vm.Message = queue.RecoveryNotice;
        RefreshQueue(); timer.Tick += (_, _) => RefreshQueue(); timer.Start();
        keywordTimer.Tick += async (_, _) => { if (vm.Busy) return; keywordTimer.Stop(); await RunAsync(() => SearchAsync(1, currentUp)); };
        Closing += ClosingAsync;
        if (args.Contains("--smoke")) Opened += async (_, _) =>
        {
            await Task.Delay(1500);
            if (Icon == null) throw new InvalidOperationException("窗口图标未加载。");
            using (var playwright = await Microsoft.Playwright.Playwright.CreateAsync())
                if (playwright.Chromium == null) throw new InvalidOperationException("账号登录组件未打包。");
            void Capture(Window window, string name)
            {
                var target = Path.Combine(artifacts, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                bitmap.Render(window); bitmap.Save(target);
            }
            Capture(this, "desktop.png");
            vm.Collections.Add(new("1", "测试合集", "season", 6));
            vm.Collections.Add(new("2", "测试系列", "series", 8));
            CollectionBox.SelectedIndex = 1; ChargeBox.SelectedIndex = 1;
            if (SortBox.IsEnabled || (CollectionBox.SelectedItem as VideoCollection)?.Kind != "season") throw new InvalidOperationException("合集选择检查失败。");
            CollectionBox.SelectedIndex = 2;
            if ((CollectionBox.SelectedItem as VideoCollection)?.Kind != "series") throw new InvalidOperationException("系列选择检查失败。");
            CollectionBox.SelectedIndex = 0; ChargeBox.SelectedIndex = 0;
            vm.Collections.Clear(); vm.Collections.Add(VideoCollection.All); CollectionBox.SelectedIndex = 0;
            currentUp = catalogUp = "2"; vm.Up = "2";
            updatingFilters = true; ChargeBox.SelectedIndex = 1; updatingFilters = false;
            await SearchAsync(1, "2");
            async Task WaitForSearch(Func<bool> completed)
            {
                using var timeout = new CancellationTokenSource(8000);
                while (vm.Busy || !completed()) await Task.Delay(25, timeout.Token);
            }
            KeywordInput.Text = "战后";
            await WaitForSearch(() => total == 2 && vm.Videos.Count == 2 && vm.Videos.All(v => v.Video.Title.Contains("战后") && v.Video.IsCharging == true));
            if (NextButton.IsEnabled) throw new InvalidOperationException("筛选后的分页未更新。");
            Capture(this, "search-filter.png");
            ChargeBox.SelectedIndex = 2;
            await WaitForSearch(() => total == 1 && vm.Videos.Count == 1 && vm.Videos[0].Video.IsCharging == false);
            KeywordInput.Text = "不存在的关键词"; KeywordSearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForSearch(() => total == 0 && vm.Videos.Count == 0);
            KeywordInput.Text = ""; await WaitForSearch(() => total == 3);
            KeywordInput.Text = "战后"; KeywordInput.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await WaitForSearch(() => total == 1);
            currentUp = catalogUp = ""; keywordTimer.Stop(); updatingFilters = true; ChargeBox.SelectedIndex = 0; KeywordInput.Text = ""; updatingFilters = false;
            currentPage = 1; total = 0; vm.Up = ""; vm.Collection = "视频资料库"; vm.PageText = "尚未获取视频"; NextButton.IsEnabled = PreviousButton.IsEnabled = false;
            ReplaceVideos([new() { Bvid = "BV1xx411c7mD", Title = "跨页选择测试 A" }]);
            vm.Videos[0].Video.Selected = true;
            ReplaceVideos([new() { Bvid = "BV13x41117TL", Title = "跨页选择测试 B" }]);
            vm.Videos[0].Video.Selected = true;
            if (selected.Count != 2) throw new InvalidOperationException("跨页选择检查失败。");
            ClearClick(null, new RoutedEventArgs());
            ReplaceVideos(Enumerable.Range(1, 8).Select(i => new VideoItem { Bvid = "BV1xx411c7mD", Title = $"视频列表布局预览 {i} · 这里显示视频标题", Duration = "12:30", Views = 125000, IsCharging = i % 2 == 0 }).ToList());
            await Task.Delay(200); Capture(this, "desktop.png");
            if (VideoListArea.Bounds.Height < Bounds.Height * .55) throw new InvalidOperationException("视频列表可用高度不足。");
            var expandedWidth = VideoListArea.Bounds.Width;
            ToggleSidebarClick(null, new RoutedEventArgs());
            await Task.Delay(200); Capture(this, "desktop-collapsed.png");
            foreach (var icon in Sidebar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && t.Text is "▦" or "↓" or "↗" or "⚙" or "♙"))
                if (icon.GetVisualParent()!.Bounds.Width < icon.Bounds.Width) throw new InvalidOperationException("收起侧栏后的图标被裁剪。");
            if (Sidebar.Bounds.Width != 56 || VideoListArea.Bounds.Width <= expandedWidth) throw new InvalidOperationException("侧栏收起检查失败。");
            ToggleSidebarClick(null, new RoutedEventArgs()); Width = 920; Height = 640;
            await Task.Delay(200); Capture(this, "desktop-compact.png");
            Width = 1240; Height = 850;
            var directoryWindow = BuildDownloadDirectoryWindow(); directoryWindow.Show(this);
            await Task.Delay(200); Capture(directoryWindow, "download-directory.png"); directoryWindow.Close();
            var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
            if (OperatingSystem.IsWindows())
            {
                if (AppPaths.FindTool("ffmpeg") != bundled) throw new InvalidOperationException("未使用内置 FFmpeg。");
                await MediaDownloader.RunProcessAsync(bundled, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.2", "-c:a", "libmp3lame", "-f", "mp3", "-y", Path.Combine(artifacts, "bundled-audio.mp3")], lifetime.Token);
                using var cancelProcess = new CancellationTokenSource(700);
                var cancelledFile = Path.Combine(artifacts, "cancelled-ffmpeg.wav");
                try
                {
                    await MediaDownloader.RunProcessAsync(bundled, ["-hide_banner", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "sine=duration=30", "-y", cancelledFile], cancelProcess.Token);
                    throw new InvalidOperationException("FFmpeg 暂停未生效。");
                }
                catch (OperationCanceledException) when (cancelProcess.IsCancellationRequested) { }
                using var releasedFile = new FileStream(cancelledFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            ReplaceVideos([]);
            vm.Library = false; ConcurrencyBox.SelectedIndex = 1;
            queue.PauseAll();
            queue.Add(Enumerable.Range(1, 5).Select(i => new VideoItem { Bvid = $"BV{i:0000000000}", Title = $"队列操作测试 {i}" }), new());
            RefreshQueue(); StartAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(300); RefreshQueue();
            if (vm.Jobs.Count(j => j.Job.Status == "downloading") != 2) throw new InvalidOperationException("并行下载界面检查失败。");
            Capture(this, "queue.png"); Width = 920; Height = 640;
            await Task.Delay(200); Capture(this, "queue-compact.png"); Width = 1240; Height = 850;
            PauseAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200); RefreshQueue();
            if (vm.Jobs.Any(j => j.Job.Status != "paused")) throw new InvalidOperationException("全部暂停界面检查失败。");
            StartJobClick(new Button { Tag = vm.Jobs[0].Job.Id }, new RoutedEventArgs());
            await Task.Delay(100); RefreshQueue();
            DeleteJobClick(new Button { Tag = vm.Jobs[0].Job.Id }, new RoutedEventArgs());
            if (vm.Jobs.Count != 4) throw new InvalidOperationException("单项删除未移除列表项。");
            DeleteAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200); RefreshQueue();
            if (vm.Jobs.Count != 0 || vm.HasJobs || vm.CanStartJobs || vm.CanPauseJobs) throw new InvalidOperationException("全部删除后列表未清空。");
            var login = new LoginWindow(bili); login.Show(this);
            if (login.Icon == null) throw new InvalidOperationException("登录窗口图标未加载。");
            await Task.Delay(300); Capture(login, "login.png");
            login.FindControl<TabControl>("LoginTabs")!.SelectedIndex = 1;
            await Task.Delay(300); Capture(login, "cookie-login.png");
            login.Close();
            await File.WriteAllTextAsync(Path.Combine(artifacts, "ui-smoke.txt"), "PASS: keyword automatic search, search button, Enter key, keyword/type intersection, clearing keyword, no-match and filtered pagination, window icons, bundled browser login driver, compact layout, sidebar collapse/expand, directory dialog, bundled FFmpeg conversion and cancellation/handle release, filters, cross-page selection, parallel queue, start/pause/delete all buttons, individual start/delete, removal from UI, minimum queue width, login tabs");
            Close();
        };
    }
    private sealed class SmokeDownloader : IMediaDownloader
    {
        public async Task<IReadOnlyList<string>> DownloadAsync(DownloadJob job, IProgress<TransferProgress> progress, CancellationToken ct)
        {
            progress.Report(new(37, 3 * 1048576, job.Title));
            await Task.Delay(Timeout.Infinite, ct); return [];
        }
    }
    private sealed class SmokeLibrarySource : IVideoLibrarySource
    {
        public Task<VideoPage> GetVideosAsync(string up, int page = 1, string order = "pubdate", string keyword = "", CancellationToken ct = default, bool chargingOnly = false)
        {
            ct.ThrowIfCancellationRequested();
            var videos = Enumerable.Range(0, 65).Select(i => new VideoItem { Bvid = $"BV{i:0000000000}", Title = i is 31 or 61 or 64 ? $"战后纪实 · 第 {i} 集" : $"其他视频 {i}", IsCharging = i < 62 }).Where(v => !chargingOnly || v.IsCharging == true).ToList();
            return Task.FromResult(new VideoPage(up, "筛选测试", videos.Skip((page - 1) * 30).Take(30).ToList(), videos.Count, page));
        }
        public Task<VideoPage> GetCollectionVideosAsync(string up, VideoCollection collection, int page, CancellationToken ct = default) => GetVideosAsync(up, page, ct: ct);
        public Task<bool> GetChargingStatusAsync(string bvid, CancellationToken ct = default) => Task.FromResult(true);
    }
    private async void ClosingAsync(object? sender, WindowClosingEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        if (closing) return;
        closing = true; IsEnabled = false; timer.Stop(); keywordTimer.Stop(); lifetime.Cancel(); search?.Cancel();
        try { await queue.DisposeAsync(); }
        finally { bili.Dispose(); foreach (var row in vm.Videos) row.Dispose(); closed = true; Close(); }
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { vm.Message = "操作已取消。"; }
        catch (Exception error) { vm.Message = MediaDownloader.Redact(error.Message); }
    }
    private async void SearchClick(object? sender, RoutedEventArgs e) { library.Clear(); await RunAsync(() => SearchAsync(1, vm.Up)); }
    private async void SearchKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; keywordTimer.Stop(); await RunAsync(() => SearchAsync(1, sender == KeywordInput && currentUp.Length > 0 ? currentUp : vm.Up)); } }
    private async void KeywordSearchClick(object? sender, RoutedEventArgs e) { keywordTimer.Stop(); await RunAsync(() => SearchAsync(1, currentUp.Length > 0 ? currentUp : vm.Up)); }
    private void KeywordChanged(object? sender, TextChangedEventArgs e)
    {
        keywordTimer.Stop();
        if (DataContext != null && currentUp.Length > 0 && !closing && !updatingFilters) keywordTimer.Start();
    }
    private async void SortChanged(object? sender, SelectionChangedEventArgs e) { if (DataContext != null && currentUp.Length > 0) await RunAsync(() => SearchAsync(1, currentUp)); }
    private async void PreviousClick(object? sender, RoutedEventArgs e) => await RunAsync(() => SearchAsync(currentPage - 1, currentUp));
    private async void NextClick(object? sender, RoutedEventArgs e) => await RunAsync(() => SearchAsync(currentPage + 1, currentUp));
    private async void LibraryFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext == null || updatingFilters || vm.Busy) return;
        SortBox.IsEnabled = (CollectionBox.SelectedItem as VideoCollection)?.Kind is null or "all";
        if (currentUp.Length > 0) await RunAsync(() => SearchAsync(1, currentUp));
    }
    private async void RefreshCollectionsClick(object? sender, RoutedEventArgs e)
    {
        catalogUp = ""; library.Clear();
        await RunAsync(() => SearchAsync(1, vm.Up));
    }
    private async Task SearchAsync(int page, string up)
    {
        if (vm.Busy) return;
        keywordTimer.Stop();
        search?.Cancel(); search?.Dispose(); search = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var ct = search.Token;
        var keyword = (KeywordInput.Text ?? "").Trim(); vm.Keyword = keyword; keywordTimer.Stop();
        vm.Busy = true; currentPage = 1; total = 0; vm.PageText = "正在搜索…"; PreviousButton.IsEnabled = NextButton.IsEnabled = false; vm.Message = "正在获取视频列表…";
        ReplaceVideos([]); EmptyText.Text = "正在加载视频…";
        try
        {
            var uid = BiliClient.ParseUid(up);
            string? catalogWarning = null;
            if (catalogUp != uid)
            {
                var previous = currentUp == uid ? CollectionBox.SelectedItem as VideoCollection : null;
                updatingFilters = true;
                try
                {
                    vm.Collections.Clear(); vm.Collections.Add(VideoCollection.All); CollectionBox.SelectedIndex = 0;
                    var collections = await bili.GetCollectionsAsync(uid, ct);
                    foreach (var collection in collections) vm.Collections.Add(collection);
                    CollectionBox.SelectedItem = vm.Collections.FirstOrDefault(c => c.Kind == previous?.Kind && c.Id == previous.Id) ?? VideoCollection.All;
                    catalogUp = uid;
                }
                catch (Exception error) when (error is BiliException or HttpRequestException) { catalogWarning = "合集目录获取失败，可点击刷新目录重试。"; }
                finally { updatingFilters = false; }
            }
            var chosen = CollectionBox.SelectedItem as VideoCollection ?? VideoCollection.All;
            SortBox.IsEnabled = chosen.Kind == "all";
            var result = await library.GetPageAsync(uid, chosen, page, new[] { "pubdate", "click", "stow" }[Math.Clamp(SortBox.SelectedIndex, 0, 2)], keyword, (ChargeFilter)Math.Max(0, ChargeBox.SelectedIndex), new InlineProgress<string>(message => vm.Message = message), ct);
            currentPage = result.Page; currentUp = result.Uid; total = result.Total;
            ReplaceVideos(result.Items);
            vm.Collection = chosen.Kind == "all" ? result.Name + " 的视频" : chosen.Name;
            vm.PageText = $"第 {currentPage} / {Math.Max(1, (total + 29) / 30)} 页 · 共 {total} 个视频";
            vm.Message = catalogWarning ?? $"{(keyword.Length > 0 ? $"搜索“{keyword}”：共 {total} 个匹配视频，本页 {result.Items.Count} 个。" : $"已获取本页 {result.Items.Count} 个视频。")}{(chosen.Kind != "all" ? "合集 / 系列按原有顺序显示。" : "")}";
            _ = LoadCoversAsync(vm.Videos.ToArray(), ct);
        }
        finally { vm.Busy = false; if (vm.PageText == "正在搜索…") vm.PageText = "未取得搜索结果"; EmptyText.Text = "没有匹配的视频，请调整筛选条件。\n若请求失败，请查看底部提示。"; PreviousButton.IsEnabled = currentPage > 1; NextButton.IsEnabled = currentPage * 30 < total; }
    }
    private void CancelSearchClick(object? sender, RoutedEventArgs e) => search?.Cancel();
    private void ReplaceVideos(IEnumerable<VideoItem> videos)
    {
        foreach (var old in vm.Videos) { old.Video.PropertyChanged -= VideoChanged; old.Dispose(); }
        vm.Videos.Clear();
        foreach (var video in videos)
        {
            video.Selected = selected.ContainsKey(video.Bvid);
            video.PropertyChanged += VideoChanged;
            vm.Videos.Add(new(video));
        }
        EmptyText.IsVisible = vm.Videos.Count == 0; UpdateSelection();
    }
    private async Task LoadCoversAsync(VideoRow[] rows, CancellationToken ct)
    {
        using var client = new HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
        using var limit = new SemaphoreSlim(4);
        await Task.WhenAll(rows.Select(async row =>
        {
            try
            {
                await limit.WaitAsync(ct);
                try
                {
                    if (!Uri.TryCreate(row.Video.Cover.StartsWith("//") ? "https:" + row.Video.Cover : row.Video.Cover, UriKind.Absolute, out var uri) || !uri.Host.EndsWith(".hdslb.com")) return;
                    var bytes = await client.GetByteArrayAsync(new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri, ct);
                    ct.ThrowIfCancellationRequested();
                    var bitmap = new Bitmap(new MemoryStream(bytes));
                    if (vm.Videos.Contains(row)) row.Cover = bitmap; else bitmap.Dispose();
                }
                finally { limit.Release(); }
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ArgumentException) { }
        }));
    }
    private void VideoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VideoItem video || e.PropertyName != nameof(VideoItem.Selected)) return;
        if (video.Selected) selected[video.Bvid] = video; else selected.Remove(video.Bvid);
        UpdateSelection();
    }
    private void UpdateSelection() => vm.Selection = $"已选 {selected.Count} 个视频";
    private void SelectPageClick(object? sender, RoutedEventArgs e) { foreach (var row in vm.Videos) row.Video.Selected = true; }
    private void ClearClick(object? sender, RoutedEventArgs e) { selected.Clear(); foreach (var row in vm.Videos) row.Video.Selected = false; UpdateSelection(); }
    private void LibraryClick(object? sender, RoutedEventArgs e) => vm.Library = true;
    private void ToggleSidebarClick(object? sender, RoutedEventArgs e) => vm.SidebarExpanded = !vm.SidebarExpanded;
    private async void DownloadDirectoryClick(object? sender, RoutedEventArgs e) => await BuildDownloadDirectoryWindow().ShowDialog(this);
    private Window BuildDownloadDirectoryWindow()
    {
        var window = new Window { Title = "设置下载目录", Width = 580, Height = 250, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var input = new TextBox { Text = paths.Downloads, Watermark = "输入完整目录路径" };
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var browse = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0) };
        var save = new Button { Content = "保存", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }; save.Classes.Add("primary");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") }; Grid.SetColumn(browse, 1); row.Children.Add(input); row.Children.Add(browse);
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children = { new TextBlock { Text = "新任务保存到此目录；已有任务仍使用原目录。" }, row, error, save } };
        browse.Click += async (_, _) =>
        {
            try
            {
                var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择下载目录", AllowMultiple = false });
                if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder) input.Text = folder;
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        save.Click += (_, _) =>
        {
            try { paths.SetDownloads(input.Text ?? ""); vm.DownloadDirectory = paths.Downloads; vm.Message = "下载目录已保存，新任务将使用新目录。"; window.Close(); }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        return window;
    }
    private void QueueClick(object? sender, RoutedEventArgs e) { vm.Library = false; RefreshQueue(); }
    private void ModeChanged(object? sender, SelectionChangedEventArgs e) { if (QualityBox != null && ModeBox != null) QualityBox.IsEnabled = ModeBox.SelectedIndex == 0; }
    private async void DirectClick(object? sender, RoutedEventArgs e)
    {
        keywordTimer.Stop();
        var input = new TextBox { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 210, Watermark = "每行一个 BV 号或完整视频链接，最多 1000 个" };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Avalonia.Media.Brushes.IndianRed };
        var button = new Button { Content = "添加并选中", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch }; button.Classes.Add("primary");
        var window = new Window { Title = "批量添加视频", Width = 510, Height = 385, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = { input, error, button } };
        button.Click += (_, _) =>
        {
            try
            {
                var ids = BiliClient.ParseVideoInput(input.Text ?? "");
                if (selected.Keys.Union(ids).Count() > 1000) throw new BiliException("最多同时选择 1000 个视频。");
                search?.Cancel();
                var videos = ids.Select(id => new VideoItem { Bvid = id, Title = id }).ToList();
                foreach (var video in videos) selected[video.Bvid] = video;
                ReplaceVideos(videos); vm.Collection = "直接添加的视频"; currentUp = ""; currentPage = 1; total = 0;
                updatingFilters = true;
                vm.Collections.Clear(); vm.Collections.Add(VideoCollection.All); CollectionBox.SelectedIndex = 0; ChargeBox.SelectedIndex = 0; catalogUp = ""; SortBox.IsEnabled = true;
                updatingFilters = false;
                vm.PageText = $"{videos.Count} 个视频"; PreviousButton.IsEnabled = NextButton.IsEnabled = false;
                vm.Message = "已选中添加的视频，下载时自动解析标题。"; window.Close();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        await window.ShowDialog(this);
    }
    private async void LoginClick(object? sender, RoutedEventArgs e)
    {
        await new LoginWindow(bili).ShowDialog(this);
        library.Clear();
        vm.Account = bili.User is { } user ? user.Name + (user.Vip ? " · 大会员" : " · 已登录") : "尚未登录";
    }
    private void DownloadClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (AppPaths.FindTool("ffmpeg") is null) throw new BiliException("内置 FFmpeg 缺失，请重新解压完整软件包。");
            var quality = new[] { "best", "2160", "1080", "720", "480", "360" }[Math.Clamp(QualityBox.SelectedIndex, 0, 5)];
            var result = queue.Add(selected.Values, new(quality, ModeBox.SelectedIndex == 1 ? "audio" : "video", AllPartsBox.IsChecked == true));
            vm.Message = $"已加入 {result.Added} 个任务，跳过 {result.Skipped} 个重复任务。";
            ClearClick(null, e); vm.Library = false; RefreshQueue();
        }
        catch (Exception ex) { vm.Message = ex.Message; }
    }
    private void RefreshQueue()
    {
        var snapshot = queue.Snapshot().OrderBy(j => j.Status switch { "downloading" or "processing" => 0, "queued" => 1, "pausing" => 2, "paused" => 3, "failed" => 4, _ => 5 }).ThenBy(j => j.Created).ToList();
        var ids = snapshot.Select(j => j.Id).ToHashSet();
        foreach (var stale in vm.Jobs.Where(row => !ids.Contains(row.Job.Id)).ToList()) vm.Jobs.Remove(stale);
        for (var index = 0; index < snapshot.Count; index++)
        {
            var job = snapshot[index];
            var row = vm.Jobs.FirstOrDefault(j => j.Job.Id == job.Id);
            if (row == null) vm.Jobs.Insert(index, new(job));
            else { row.Job = job; var previous = vm.Jobs.IndexOf(row); if (previous != index) vm.Jobs.Move(previous, index); }
        }
        vm.HasJobs = snapshot.Count > 0; vm.CanStartJobs = snapshot.Any(j => j.CanStart); vm.CanPauseJobs = snapshot.Any(j => j.CanPause);
        vm.QueueSummary = $"全部 {snapshot.Count}  ·  下载中 {snapshot.Count(j => j.Status is "downloading" or "processing")}  ·  等待 {snapshot.Count(j => j.Status == "queued")}  ·  暂停 {snapshot.Count(j => j.Status is "paused" or "pausing")}  ·  完成 {snapshot.Count(j => j.Status == "completed")}  ·  失败 {snapshot.Count(j => j.Status == "failed")}  ·  {snapshot.Sum(j => j.Speed) / 1048576:0.00} MB/s";
    }
    private void QueueAction(Action action) { try { action(); } catch (Exception ex) { vm.Message = MediaDownloader.Redact(ex.Message); } finally { RefreshQueue(); } }
    private void PauseJobClick(object? sender, RoutedEventArgs e) => QueueAction(() => { if (sender is Button { Tag: string id }) queue.Pause(id); });
    private void StartJobClick(object? sender, RoutedEventArgs e) => QueueAction(() => { if (sender is Button { Tag: string id }) queue.Start(id); });
    private void DeleteJobClick(object? sender, RoutedEventArgs e) => QueueAction(() => { if (sender is Button { Tag: string id } && queue.Delete(id)) vm.Message = "任务已删除，已下载文件保留。"; });
    private void StartAllClick(object? sender, RoutedEventArgs e) => QueueAction(() => vm.Message = $"已开始 {queue.StartAll()} 个任务，最多同时处理 {queue.Concurrency} 个。" );
    private void PauseAllClick(object? sender, RoutedEventArgs e) => QueueAction(() => vm.Message = $"已暂停 {queue.PauseAll()} 个任务，保留下载进度。" );
    private void DeleteAllClick(object? sender, RoutedEventArgs e) => QueueAction(() => vm.Message = $"已删除 {queue.DeleteAll()} 个任务，已下载文件保留。" );
    private void ConcurrencyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext == null || ConcurrencyBox.SelectedIndex < 0 || ConcurrencyBox.SelectedIndex + 1 == queue.Concurrency) return;
        QueueAction(() => { queue.SetConcurrency(ConcurrencyBox.SelectedIndex + 1); vm.Message = $"并行任务数已设为 {queue.Concurrency}；当前下载继续进行。"; });
        ConcurrencyBox.SelectedIndex = queue.Concurrency - 1;
    }
    private void OpenFolderClick(object? sender, RoutedEventArgs e) { try { AppPaths.Open(paths.Downloads); } catch (Exception ex) { vm.Message = ex.Message; } }
    private void JobFolderClick(object? sender, RoutedEventArgs e) { try { if (sender is Button { Tag: string id } && queue.Snapshot().FirstOrDefault(j => j.Id == id) is { } job) { var folder = paths.OutputFolder(job); if (Directory.Exists(folder)) AppPaths.Open(folder); else vm.Message = "下载目录不存在，请检查磁盘连接。"; } } catch (Exception ex) { vm.Message = ex.Message; } }
    private void OpenVideoClick(object? sender, RoutedEventArgs e) { if (sender is Button { Tag: string id } && BiliClient.IsBvid(id)) AppPaths.Open("https://www.bilibili.com/video/" + id); }
}
