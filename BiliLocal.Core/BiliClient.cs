using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliLocal.Core;

public sealed class BiliException(string message) : Exception(message);

public sealed partial class BiliClient : IDisposable, IVideoLibrarySource
{
    public const string Api = "https://api.bilibili.com";
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private static readonly int[] Mixin = [46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13,37,48,7,16,24,55,40,61,26,17,0,1,60,51,30,4,22,25,54,21,56,59,6,63,57,62,11,36,20,34,44,52];
    private readonly object gate = new();
    private Dictionary<string, string> cookies = [];
    public UserInfo? User { get; private set; }
    public Dictionary<string, string> Snapshot() { lock (gate) return new(cookies); }
    public static bool IsBvid(string value) => Regex.IsMatch(value, "^BV[A-Za-z0-9]{10}$");

    public static Dictionary<string, string> ParseCookies(string raw)
    {
        raw = raw.Trim().Replace("\\_", "_");
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (raw.Contains('\t'))
        {
            foreach (var original in raw.Split('\n'))
            {
                var line = original.TrimEnd('\r');
                if (line.StartsWith("#HttpOnly_")) line = line[10..];
                else if (line.StartsWith('#')) continue;
                var fields = line.Split('\t');
                if (fields.Length == 7 && IsBiliDomain(fields[0])) result[fields[5]] = fields[6];
            }
        }
        else
        {
            if (raw.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) raw = raw[7..].Trim();
            if (raw.Contains('\n') || raw.Contains('\r')) throw new BiliException("Cookie 请求头必须为单行，或导入 Netscape cookies.txt 文本。");
            foreach (var item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = item.IndexOf('=');
                if (index <= 0) continue;
                var name = item[..index].Trim();
                var value = item[(index + 1)..].Trim();
                if (Regex.IsMatch(name, "^[A-Za-z0-9_-]+$") && !value.Any(char.IsControl)) result[name] = value;
            }
        }
        if (!result.TryGetValue("SESSDATA", out var session) || string.IsNullOrEmpty(session)) throw new BiliException("Cookie 缺少 SESSDATA，请从已登录的 Bilibili 页面复制。");
        return result;
    }
    public static bool IsBiliDomain(string domain) => domain.TrimStart('.').Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase);
    public static string ParseUid(string input)
    {
        input = input.Trim();
        if (Regex.IsMatch(input, "^[1-9][0-9]{0,19}$")) return input;
        if (Uri.TryCreate(input.Contains("://") ? input : "https://" + input, UriKind.Absolute, out var uri) && uri.Host == "space.bilibili.com")
        {
            var match = Regex.Match(uri.AbsolutePath, "^/([1-9][0-9]{0,19})(?:/.*)?$");
            if (match.Success) return match.Groups[1].Value;
        }
        throw new BiliException("请输入 UP 主数字 UID 或 space.bilibili.com 空间链接。");
    }
    public static IReadOnlyList<string> ParseVideoInput(string input)
    {
        List<string> result = [];
        foreach (var line in input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bvid = line;
            if (!IsBvid(bvid) && Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host is "www.bilibili.com" or "bilibili.com")
                bvid = Regex.Match(uri.AbsolutePath, "^/video/(BV[A-Za-z0-9]{10})/?$").Groups[1].Value;
            if (!IsBvid(bvid)) throw new BiliException("每行请输入一个有效 BV 号或完整 Bilibili 视频链接。");
            if (!result.Contains(bvid)) result.Add(bvid);
        }
        if (result.Count is < 1 or > 1000) throw new BiliException("每次请添加 1–1000 个视频。");
        return result;
    }
    public static string Query(IEnumerable<KeyValuePair<string, string>> values) => string.Join('&', values.Select(v => Uri.EscapeDataString(v.Key) + "=" + Uri.EscapeDataString(v.Value)));
    public static Dictionary<string, string> SignWbi(Dictionary<string, string> input, string img, string sub, long? timestamp = null)
    {
        var raw = Path.GetFileNameWithoutExtension(new Uri(img).AbsolutePath) + Path.GetFileNameWithoutExtension(new Uri(sub).AbsolutePath);
        if (raw.Length < 64) throw new BiliException("无法取得 Bilibili 签名，请稍后再试。");
        var key = new string(Mixin.Take(32).Select(i => raw[i]).ToArray());
        var values = new SortedDictionary<string, string>(input, StringComparer.Ordinal) { ["wts"] = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture) };
        var clean = values.ToDictionary(v => v.Key, v => Regex.Replace(v.Value, "[!'()*]", ""));
        clean["w_rid"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Query(clean) + key))).ToLowerInvariant();
        return clean;
    }
    public HttpClient CreateHttpClient(Dictionary<string, string>? source = null, CookieContainer? jar = null)
    {
        jar ??= new CookieContainer();
        foreach (var pair in source ?? Snapshot())
        {
            // Browser-only JSON cookies are not required by API calls and violate CookieContainer syntax.
            if (pair.Value.Contains('"') || pair.Value.Contains(',')) continue;
            try { jar.Add(new Cookie(pair.Key, pair.Value, "/", ".bilibili.com") { Secure = true }); } catch (CookieException) { }
        }
        var handler = new HttpClientHandler { CookieContainer = jar, AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Referrer = new("https://www.bilibili.com/");
        return client;
    }
    public static async Task<JsonElement> ReadJsonAsync(HttpClient client, string url, CancellationToken ct = default)
    {
        using var response = await client.GetAsync(url, ct);
        if ((int)response.StatusCode is 403 or 412 or 429) throw new BiliException($"Bilibili 暂时限制请求（HTTP {(int)response.StatusCode}），请登录或稍后再试。");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }
    public static JsonElement Unpack(JsonElement root)
    {
        var code = root.GetProperty("code").GetInt32();
        if (code != 0) throw new BiliException(code == -101 ? "登录失效，请重新登录。" : code is -352 or -412 or -401 or -509 ? $"Bilibili 限制请求（{code}），请稍后重试。" : $"Bilibili 返回 {code}：{root.Text("message")}");
        return root.GetProperty("data");
    }
    public async Task<UserInfo> LoginAsync(string cookie, CancellationToken ct = default) => await LoginAsync(ParseCookies(cookie), ct);
    public async Task<UserInfo> LoginAsync(Dictionary<string, string> candidate, CancellationToken ct = default)
    {
        using var client = CreateHttpClient(candidate);
        var data = Unpack(await ReadJsonAsync(client, Api + "/x/web-interface/nav", ct));
        if (!data.TryGetProperty("isLogin", out var logged) || !logged.GetBoolean()) throw new BiliException("Cookie 无效或已过期。");
        var user = new UserInfo(data.Text("mid"), data.Text("uname"), data.Number("vipStatus") > 0);
        ct.ThrowIfCancellationRequested();
        lock (gate) { cookies = new(candidate); User = user; }
        return user;
    }
    public void Logout() { lock (gate) { cookies.Clear(); User = null; } }
    private async Task<Dictionary<string, string>> SignedAsync(HttpClient client, Dictionary<string, string> parameters, CancellationToken ct)
    {
        var nav = await ReadJsonAsync(client, Api + "/x/web-interface/nav", ct);
        var wbi = nav.GetProperty("data").GetProperty("wbi_img");
        return SignWbi(parameters, wbi.Text("img_url"), wbi.Text("sub_url"));
    }
    public async Task<VideoPage> GetVideosAsync(string up, int page = 1, string order = "pubdate", string keyword = "", CancellationToken ct = default, bool chargingOnly = false)
    {
        var uid = ParseUid(up);
        if (page < 1 || order is not ("pubdate" or "click" or "stow")) throw new ArgumentException("无效分页或排序。");
        using var client = CreateHttpClient();
        var query = await SignedAsync(client, new() { ["mid"] = uid, ["pn"] = page.ToString(), ["ps"] = "30", ["order"] = order, ["keyword"] = keyword, ["platform"] = "web", ["web_location"] = "1550101", ["special_type"] = chargingOnly ? "charging" : "" }, ct);
        var data = Unpack(await ReadJsonAsync(client, Api + "/x/space/wbi/arc/search?" + Query(query), ct));
        var items = data.GetProperty("list").GetProperty("vlist").EnumerateArray().Select(ParseVideo).Where(v => IsBvid(v.Bvid)).ToList();
        if (chargingOnly)
        {
            if (items.Any(v => v.IsCharging == false)) throw new BiliException("Bilibili 未正确应用充电筛选，请稍后重试。");
            foreach (var item in items) item.IsCharging ??= true;
        }
        return new(uid, items.FirstOrDefault()?.Author ?? "UP 主 " + uid, items, (int)data.GetProperty("page").Number("count"), page);
    }
    public async Task<VideoDetails> GetDetailsAsync(string bvid, CancellationToken ct = default)
    {
        if (!IsBvid(bvid)) throw new BiliException("BV 号无效。");
        using var client = CreateHttpClient();
        var data = Unpack(await ReadJsonAsync(client, Api + "/x/web-interface/view?bvid=" + bvid, ct));
        return new(bvid, data.Text("title"), data.GetProperty("pages").EnumerateArray().Select(p => new VideoPart(p.Number("cid"), (int)p.Number("page"), p.Text("part"))).ToList());
    }
    public async Task<JsonElement> GetPlayAsync(string bvid, long cid, CancellationToken ct = default, string quality = "best")
    {
        using var client = CreateHttpClient();
        var qn = quality switch { "2160" => "120", "1080" => "80", "720" => "64", "480" => "32", "360" => "16", _ => "127" };
        var values = new Dictionary<string, string> { ["bvid"] = bvid, ["cid"] = cid.ToString(), ["fnval"] = "4048", ["fnver"] = "0", ["fourk"] = "1", ["qn"] = qn, ["web_location"] = "1550101" };
        if (User is null) values["try_look"] = "1";
        var query = await SignedAsync(client, values, ct);
        return Unpack(await ReadJsonAsync(client, Api + "/x/player/wbi/playurl?" + Query(query), ct));
    }
    public void Dispose() => Logout();
}
public static class JsonExtensions
{
    public static string Text(this JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
    public static long Number(this JsonElement element, string name) => long.TryParse(element.Text(name), out var value) ? value : 0;
}
