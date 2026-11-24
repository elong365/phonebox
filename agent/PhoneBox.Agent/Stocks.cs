using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record StockQuote(
    string Code, string Name, double Price, double PrevClose,
    double Change, double ChangePct, string Market, string Time);

/// <summary>Live quotes for the Stocks widget, pulled from Tencent's qt.gtimg.cn feed
/// for the watchlist in Config.Stocks. Quotes are fetched on demand and cached for ~15 s;
/// a failed fetch serves the last good answer with a flag.</summary>
public sealed partial class StockService(ConfigStore config, ILogger<StockService> log)
{
    private const int TimeoutMs = 6000;

    static StockService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly Lock _gate = new();
    private Dictionary<string, StockQuote> _cache = [];
    private long _fetchedAt;
    private string? _error;
    private string _symbolsKey = "";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };

    [GeneratedRegex(@"v_(\w+)=""([^""]*)""")]
    private static partial Regex LineRe();

    /// <summary>Quotes for the watchlist; null symbols reads Config.Stocks.</summary>
    public (List<StockQuote> Quotes, long Updated, string? Error) Snapshot(string? symbols = null)
    {
        var list = ((IEnumerable<string>)(symbols is { Length: > 0 }
            ? symbols.Split(',', ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : config.Get().Stocks))
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => Regex.IsMatch(s, @"^(sh|sz|bj|hk|us)[a-z0-9]{4,8}$"))
            .Distinct()
            .Take(20)
            .ToArray();
        if (list.Length == 0) return ([], 0, null);

        var key = string.Join(",", list);
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (key != _symbolsKey || now - _fetchedAt > 15_000)
            {
                Fetch(list);
                _symbolsKey = key;
                _fetchedAt = now;
            }
            var quotes = list.Select(c => _cache.TryGetValue(c, out var q) ? q : null).Where(q => q is not null).Cast<StockQuote>().ToList();
            return (quotes, _fetchedAt, _error);
        }
    }

    private void Fetch(string[] symbols)
    {
        try
        {
            var url = "http://qt.gtimg.cn/q=" + string.Join(",", symbols.Select(Sanitize));
            var bytes = Http.GetByteArrayAsync(url).ConfigureAwait(false).GetAwaiter().GetResult();
            // qt.gtimg.cn answers in GB18030 (some HK names use chars outside GB2312)
            var text = Encoding.GetEncoding("GB18030").GetString(bytes);
            var next = new Dictionary<string, StockQuote>();
            foreach (Match m in LineRe().Matches(text))
            {
                var q = Parse(m.Groups[1].Value, m.Groups[2].Value.Split('~'));
                if (q is not null) next[q.Code] = q;
            }
            lock (_gate)
            {
                foreach (var (code, q) in next) _cache[code] = q;
                _error = null;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "stock fetch failed");
            lock (_gate) _error = "fetch failed";
        }
    }

    /// <summary>Intraday minute prices ("分时图") for one symbol, as [hhmm, price] pairs,
    /// plus the previous close (the 0% baseline). Served through the agent so the phone
    /// never talks to the quote feed itself.</summary>
    public (List<object[]>? Points, double PrevClose, string Date, long Updated, string? Error) Minutes(string code)
    {
        code = (code ?? "").Trim().ToLowerInvariant();
        if (!Regex.IsMatch(code, @"^(sh|sz|bj|hk|us)[a-z0-9]{4,8}$")) return (null, 0, "", 0, "bad code");
        // a short cache keeps the quote feed happy without feeling stale on the phone
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_minuteCode == code && now - _minuteAt < 30_000 && _minutePoints is not null)
                return (_minutePoints, _minutePrev, _minuteDate, _minuteAt, null);
            try
            {
                // the proxy endpoint also returns the qt block with yesterday's close
                var url = "https://proxy.finance.qq.com/ifzqgtimg/appstock/app/minute/query?code=" + Sanitize(code);
                var json = Http.GetStringAsync(url).ConfigureAwait(false).GetAwaiter().GetResult();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var node = doc.RootElement.GetProperty("data").GetProperty(Sanitize(code));
                var root = node.GetProperty("data");
                var date = root.TryGetProperty("date", out var dt) ? dt.GetString() ?? "" : "";
                var pts = new List<object[]>();
                foreach (var row in root.GetProperty("data").EnumerateArray())
                {
                    // each row: "hhmm price volume amount"
                    var parts = row.GetString()?.Split(' ') ?? [];
                    if (parts.Length >= 2 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var p))
                        pts.Add([parts[0], p]);
                }
                double prev = 0;
                if (node.TryGetProperty("qt", out var qt) &&
                    qt.TryGetProperty(Sanitize(code), out var qa) && qa.GetArrayLength() > 4 &&
                    double.TryParse(qa[4].GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pv))
                    prev = pv;
                _minuteCode = code; _minuteDate = date; _minutePoints = pts; _minutePrev = prev; _minuteAt = now;
                return (pts, prev, date, now, null);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "stock minutes fetch failed");
                return (null, 0, "", 0, "fetch failed");
            }
        }
    }

    private string _minuteCode = "";
    private long _minuteAt;
    private string _minuteDate = "";
    private List<object[]>? _minutePoints;
    private double _minutePrev;

    private static string Sanitize(string code) =>
        // the feed expects lowercase exchange prefixes and a plain symbol
        code.StartsWith("us") ? "us" + code[2..].ToUpperInvariant() : code;

    private static StockQuote? Parse(string code, string[] f)
    {
        // fields: 1 name, 3 price, 4 prev close, 31 change, 32 change %, 30 quote time (A-shares)
        if (f.Length < 34) return null;
        try
        {
            var name = f[1].Trim();
            var price = ParseD(f[3]);
            var prev = ParseD(f[4]);
            if (price <= 0 && f.Length > 45)
            {
                // HK/US rows put the price further along; try a few known slots
                price = ParseD(f[45]);
            }
            if (price <= 0) return null;
            var change = ParseD(f[31]);
            var pct = ParseD(f[32]);
            var market = code.StartsWith("sh") || code.StartsWith("sz") || code.StartsWith("bj") ? "A"
                       : code.StartsWith("hk") ? "HK" : code.StartsWith("us") ? "US" : "";
            return new StockQuote(code, name, price, prev, change, pct, market, f[30]);
        }
        catch { return null; }
    }

    private static double ParseD(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
