using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class MarketIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("phonebox-markets-").FullName;

    [Fact]
    public void DefaultsKeepSessionAndApprovalSharingOffAndNormalizeTheWatchlist()
    {
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """
            {"stocks":[" SH000001 ","sh000001","HK00700","usAAPL","hk5","usMicrosoft","sh123456?"]}
            """);
        var cfg = new ConfigStore(path).Get();

        Assert.Equal(["sh000001", "hk00700", "usaapl"], cfg.Stocks);
        Assert.False(cfg.WorkBuddy.PhoneApprovals);
        Assert.False(cfg.WorkBuddy.Sessions);
        Assert.False(cfg.WorkBuddy.ShareSessionTitles);
        Assert.False(cfg.ChatDev.Sessions);
        Assert.False(cfg.ChatDev.ShareSessionTitles);
    }

    [Fact]
    public void WorkBuddyReadsWalMetadataAndOnlyPublishesTitlesWhenEnabled()
    {
        var workbuddy = Path.Combine(_root, ".workbuddy");
        var sessions = Directory.CreateDirectory(Path.Combine(workbuddy, "sessions"));
        File.WriteAllText(Path.Combine(sessions.FullName, "37.json"), """
            {"pid":37,"kind":"interactive","sessionId":"fixture","cwd":"/example/project","startedAt":100}
            """);
        File.WriteAllText(Path.Combine(sessions.FullName, "38.json"), """
            {"pid":38,"kind":"prewarm","sessionId":"prewarm"}
            """);

        var dbFile = Path.Combine(workbuddy, "workbuddy.db");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbFile, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE sessions (id TEXT, title TEXT, custom_title TEXT, model TEXT, status TEXT, deleted_at INTEGER);
            INSERT INTO sessions VALUES ('fixture', 'generated title', 'custom title', 'vendor:model', 'working', NULL);
            """;
        command.ExecuteNonQuery();

        var hidden = Assert.Single(SessionMonitor.ReadWorkBuddy(_root, _ => true));
        Assert.Equal("model", hidden.Model);
        Assert.Equal("busy", hidden.Status);
        Assert.Null(hidden.Title);

        var shown = Assert.Single(SessionMonitor.ReadWorkBuddy(_root, _ => true, titles: true));
        Assert.Equal("custom title", shown.Title);

        command.CommandText = "UPDATE sessions SET custom_title = 'updated title' WHERE id = 'fixture'";
        command.ExecuteNonQuery();
        var latest = Assert.Single(SessionMonitor.ReadWorkBuddy(_root, _ => true, titles: true));
        Assert.Equal("updated title", latest.Title);
    }

    [Fact]
    public async Task ChartRequestsOutsideTheWatchlistDoNotReachTheFeed()
    {
        var store = new ConfigStore(Path.Combine(_root, "config.json"));
        var stocks = new StockService(store, NullLogger<StockService>.Instance);
        var rejected = await stocks.MinutesAsync("sh000001");
        Assert.Null(rejected.Points);
        Assert.Equal("code not in watchlist", rejected.Error);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
