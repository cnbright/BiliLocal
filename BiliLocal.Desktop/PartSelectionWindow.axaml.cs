using Avalonia.Controls;
using Avalonia.Interactivity;
using BiliLocal.Core;

namespace BiliLocal.Desktop;

public sealed class PartSelectionRow(VideoPart part) : Observable
{
    public VideoPart Part { get; } = part;
    public string Label => $"P{Part.Page} · {Part.Title}";
    private bool selected;
    public bool Selected { get => selected; set => Set(ref selected, value); }
}

public sealed class PartSelectionVideo(VideoDetails details)
{
    public VideoDetails Details { get; } = details;
    public IReadOnlyList<PartSelectionRow> Parts { get; } = details.Parts.Select(part => new PartSelectionRow(part)).ToList();
}

public partial class PartSelectionWindow : Window
{
    private readonly DownloadOptions options;
    private bool updatingSelection;
    public IReadOnlyList<PartSelectionVideo> Videos { get; }

    public PartSelectionWindow() : this([], new()) { }

    public PartSelectionWindow(IReadOnlyList<VideoDetails> videos, DownloadOptions options)
    {
        this.options = options;
        Videos = videos.Select(video => new PartSelectionVideo(video)).ToList();
        InitializeComponent();
        DataContext = this;
        foreach (var row in Videos.SelectMany(video => video.Parts))
            row.PropertyChanged += (_, _) => { if (!updatingSelection) UpdateSelection(); };
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var count = Videos.Sum(video => video.Parts.Count(part => part.Selected));
        SelectionSummary.Text = $"已选 {count} 个分 P";
        ConfirmButton.IsEnabled = count > 0;
    }

    private void SetSelection(bool selected)
    {
        updatingSelection = true;
        foreach (var row in Videos.SelectMany(video => video.Parts)) row.Selected = selected;
        updatingSelection = false;
        UpdateSelection();
    }

    private void SelectAllClick(object? sender, RoutedEventArgs e) => SetSelection(true);
    private void ClearClick(object? sender, RoutedEventArgs e) => SetSelection(false);
    private void CancelClick(object? sender, RoutedEventArgs e) => Close();
    private void ConfirmClick(object? sender, RoutedEventArgs e)
    {
        var downloads = new List<(VideoItem Video, DownloadOptions Options)>();
        foreach (var video in Videos)
        {
            var parts = video.Parts.Where(part => part.Selected).Select(part => part.Part).ToList();
            if (parts.Count == 0) continue;
            downloads.Add((new VideoItem { Bvid = video.Details.Bvid, Title = video.Details.Title },
                options with { AllParts = false, SelectedParts = parts }));
        }
        if (downloads.Count > 0) Close(downloads);
    }
}
