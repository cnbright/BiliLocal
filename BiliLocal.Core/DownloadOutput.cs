using System.Text.Json;

namespace BiliLocal.Core;

// Per-part receipts belong in application data, not beside the user's videos.
public sealed class DownloadOutput
{
    private readonly string directory, receipts;
    private sealed record Receipt(string File, string Source, long Length, bool Completed);
    public DownloadOutput(AppPaths paths, DownloadJob job)
    {
        directory = Path.GetFullPath(string.IsNullOrWhiteSpace(job.OutputDirectory) ? paths.Downloads : job.OutputDirectory);
        receipts = paths.JobCache(job.Id);
        Directory.CreateDirectory(directory); Directory.CreateDirectory(receipts);
    }
    private string ReceiptPath(long cid)
    {
        if (cid <= 0) throw new ArgumentOutOfRangeException(nameof(cid));
        return Path.Combine(receipts, $"completed-{cid}.json");
    }
    public string? Completed(long cid)
    {
        var receipt = ReceiptPath(cid);
        if (!File.Exists(receipt)) return null;
        var saved = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receipt));
        if (saved == null || Path.GetFileName(saved.File) != saved.File) return null;
        var final = Path.Combine(directory, saved.File);
        // Also recover a crash after the move but before the completion receipt update.
        return (saved.Completed || !File.Exists(saved.Source)) && File.Exists(final) && new FileInfo(final).Length == saved.Length ? final : null;
    }
    private void Save(long cid, Receipt receipt)
    {
        var file = ReceiptPath(cid);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(receipt)); File.Move(file + ".tmp", file, true);
    }
    public string Commit(string source, long cid, string name)
    {
        if (Path.GetFileName(name) != name || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("无效视频文件名。");
        var length = new FileInfo(source).Length;
        if (length == 0) throw new IOException("视频文件为空。");
        var stem = Path.GetFileNameWithoutExtension(name); var extension = Path.GetExtension(name);
        for (var number = 1; ; number++)
        {
            var candidate = number == 1 ? name : $"{stem} ({number}){extension}";
            var final = Path.Combine(directory, candidate);
            if (File.Exists(final) || Directory.Exists(final)) continue;
            var receipt = new Receipt(candidate, source, length, false);
            Save(cid, receipt);
            try { File.Move(source, final, false); }
            catch (IOException) when (File.Exists(source) && (File.Exists(final) || Directory.Exists(final))) { continue; }
            Save(cid, receipt with { Completed = true });
            return final;
        }
    }
}
