using System.Net.Http;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Latest posts from the East Money stock forum (guba.eastmoney.com).
/// The list page embeds a "var article_list = {...}" JSON block; the phone never
/// talks to East Money itself, this feed is what the panel calls instead.</summary>
public static partial class GubaFeed
{
    private const int MaxPosts = 15;                    // hard cap; the widget picks fewer
    private const long CacheMs = 10 * 60 * 1000;        // the forum does not need to be fresh
    private const long RetryMs = 5 * 60 * 1000;         // keep the retry pace gentle: East Money rate-limits hard

    private static readonly object Gate = new();
    private static string _code = "";
    private static long _at;
    private static List<object>? _cache;
    private static string? _error;

    /// <summary>The last failure reason, shown in the agent log via /api/guba responses.</summary>
    public static string? LastError => _error;

    [GeneratedRegex(@"var article_list\s*=\s*(\{.*?\});", RegexOptions.Singleline)]
    private static partial Regex ListRe();

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex CodeRe();

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseProxy = false,           // system proxies (VPN/clash) often break this host
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>Latest posts for a 6-digit stock code (e.g. "002317").
    /// <paramref name="count"/> is how many the widget wants (1..15).
    /// When the feed is rate-limited, the last successful list is served (staleness
    /// beats an empty widget).</summary>
    public static (List<object>? Posts, long Updated, string? Error) Latest(string? code, int count = 10)
    {
        code = (code ?? "").Trim();
        if (!CodeRe().IsMatch(code)) return (null, 0, "bad code");
        if (count < 1) count = 1;
        if (count > MaxPosts) count = MaxPosts;
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_code == code && now - _at < (CacheMs * (_cache is null ? 0 : 1) + (_cache is null ? RetryMs : 0)) && (_cache is not null || now - _at < RetryMs))
            {
                // recent success or recent failure: don't hammer the feed
                if (_cache is not null) return (_cache.Take(count).ToList(), _at, null);
                return (null, 0, _error);
            }
            try
            {
                var posts = TryFetchPage(code) ?? TryFetchApi(code);
                if (posts is null)
                {
                    // rate-limited: fall back to the last good list, mark it stale
                    if (_cache is not null && _code == code) return (_cache.Take(count).ToList(), _at, "stale");
                    _error = "fetch failed (rate-limited?)";
                    return (null, 0, _error);
                }
                _code = code; _cache = posts; _at = now; _error = null;
                return (posts.Take(count).ToList(), now, null);
            }
            catch (Exception ex)
            {
                _error = "fetch failed: " + ex.GetType().Name + ": " + ex.Message;
                if (_cache is not null && _code == code) return (_cache.Take(count).ToList(), _at, "stale");
                return (null, 0, _error);
            }
        }
    }

    /// <summary>Parse the list page's embedded article_list JSON. Returns null when the
    /// page is a captcha / rate-limit stub.</summary>
    private static List<object>? TryFetchPage(string code)
    {
        var url = $"https://guba.eastmoney.com/list,{code},f.html";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        var html = Http.SendAsync(req).ConfigureAwait(false).GetAwaiter().GetResult()
            .Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        var m = ListRe().Match(html);
        if (!m.Success) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(m.Groups[1].Value);
        if (!doc.RootElement.TryGetProperty("re", out var re) || re.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            _error = "page re not array, len=" + m.Groups[1].Value.Length;
            return null;
        }
        return Posts(re);
    }

    /// <summary>Backup: the gbapi JSON endpoint (also rate-limits, sometimes later).</summary>
    private static List<object>? TryFetchApi(string code)
    {
        var url = $"https://gbapi.eastmoney.com/webarticlelist/api/Article/Articlelist?code={code}&type=0&ps={MaxPosts}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        var json = Http.SendAsync(req).ConfigureAwait(false).GetAwaiter().GetResult()
            .Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("re", out var re) || re.GetArrayLength() == 0) return null;
        return Posts(re);
    }

    private static List<object> Posts(System.Text.Json.JsonElement arr)
    {
        var posts = new List<object>();
        foreach (var a in arr.EnumerateArray())
        {
            if (posts.Count >= MaxPosts) break;
            // skip pinned announcements and ads: they are not "latest chatter"
            if (a.TryGetProperty("post_top_status", out var top) && top.ValueKind == System.Text.Json.JsonValueKind.Number && top.GetInt32() != 0) continue;
            static string S(System.Text.Json.JsonElement e, string name)
            {
                // fields like post_id/user_id come back as numbers sometimes
                return e.TryGetProperty(name, out var v)
                    ? v.ValueKind == System.Text.Json.JsonValueKind.String
                        ? v.GetString() ?? "" : v.ToString()
                    : "";
            }
            posts.Add(new
            {
                id = S(a, "post_id"),
                title = S(a, "post_title"),
                author = S(a, "user_nickname"),
                time = S(a, "post_publish_time"),
                reads = a.TryGetProperty("post_click_count", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number ? r.GetInt64() : 0,
                comments = a.TryGetProperty("post_comment_count", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number ? c.GetInt64() : 0,
            });
        }
        return posts;
    }
}
