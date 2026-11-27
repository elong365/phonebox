using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class WorkBuddySessionsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("phonebox-workbuddy-").FullName;

    [Fact]
    public void ReadsOnlyLiveInteractiveStatusFiles()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        File.WriteAllText(Path.Combine(dir.FullName, "37.json"), """
            {"pid":37,"kind":"interactive","sessionId":"fixture","cwd":"/work/project","startedAt":100,
             "lastHeartbeat":200,"model":"fixture-model","title":"private title"}
            """);
        File.WriteAllText(Path.Combine(dir.FullName, "38.json"), """
            {"pid":38,"kind":"prewarm","sessionId":"prewarm"}
            """);
        File.WriteAllText(Path.Combine(dir.FullName, "49.json"), """
            {"pid":49,"kind":"interactive","sessionId":"dead-process"}
            """);

        var live = Assert.Single(SessionMonitor.ReadWorkBuddy(_home, pid => pid != 49, nowMs: 201));
        Assert.Equal("workbuddy:fixture", live.Id);
        Assert.Equal("project", live.Project);
        Assert.Equal("busy", live.Status);
        Assert.Equal("fixture-model", live.Model);
        Assert.Null(live.Title);
        // fail-loud: a heartbeat that stopped over 90 s ago and no outcome field in the file
        // reads "unknown", not a healthy idle
        Assert.Equal("unknown", Assert.Single(SessionMonitor.ReadWorkBuddy(_home, pid => pid != 49, nowMs: 90_201)).Status);
    }

    [Fact]
    public void ReadsOutcomeStatesFromStatusFiles()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        File.WriteAllText(Path.Combine(dir.FullName, "60.json"), """
            {"pid":60,"kind":"interactive","sessionId":"done","cwd":"/work/done","startedAt":100,
             "lastHeartbeat":200,"status":"completed"}
            """);
        File.WriteAllText(Path.Combine(dir.FullName, "61.json"), """
            {"pid":61,"kind":"interactive","sessionId":"boom","cwd":"/work/boom","startedAt":100,
             "lastHeartbeat":200,"status":"failed"}
            """);
        File.WriteAllText(Path.Combine(dir.FullName, "62.json"), """
            {"pid":62,"kind":"interactive","sessionId":"halt","cwd":"/work/halt","startedAt":100,
             "lastHeartbeat":200,"outcome":"stopped"}
            """);
        File.WriteAllText(Path.Combine(dir.FullName, "63.json"), """
            {"pid":63,"kind":"interactive","sessionId":"running","cwd":"/work/running","startedAt":100,
             "lastHeartbeat":200,"status":"working"}
            """);

        // no real status file carries a status/outcome field today (WorkBuddy 2.176):
        // these rows come to life only when a future version writes one, so the parser
        // is ready without inventing anything now
        var rows = SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 201).ToDictionary(s => s.Id);
        Assert.Equal("completed", rows["workbuddy:done"].Status);
        Assert.Equal("failed", rows["workbuddy:boom"].Status);
        Assert.Equal("stopped", rows["workbuddy:halt"].Status);
        // "working" is a turn in progress: the row reads busy, and no outcome is carried
        Assert.Equal("busy", rows["workbuddy:running"].Status);
        Assert.Null(rows["workbuddy:running"].Outcome);
    }

    [Fact]
    public void StaleHeartbeatWithNoOutcomeReadsUnknown()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        // no real file has an outcome field: over 90 s of silence with the process
        // still alive fails loud instead of posing as idle
        File.WriteAllText(Path.Combine(dir.FullName, "70.json"), """
            {"pid":70,"kind":"interactive","sessionId":"quiet","cwd":"/work/quiet","startedAt":1000,
             "lastHeartbeat":2000}
            """);
        Assert.Equal("unknown", SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 2000 + 90_001).Single().Status);
    }

    [Fact]
    public void DeadProcessFilesAreDropped()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        File.WriteAllText(Path.Combine(dir.FullName, "71.json"), """
            {"pid":71,"kind":"interactive","sessionId":"gone","cwd":"/work/gone","startedAt":1000,
             "lastHeartbeat":2000,"status":"completed"}
            """);
        // a dead process's file is dropped however fresh or final it looks
        Assert.Empty(SessionMonitor.ReadWorkBuddy(_home, pid => false, nowMs: 2001));
    }

    [Fact]
    public void LiveHeartbeatWithOutcomeReadsBusyUntilTheTurnEnds()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_home, ".workbuddy", "sessions"));
        // "working" is a turn in progress: the row reads busy even though the file carries a status
        File.WriteAllText(Path.Combine(dir.FullName, "80.json"), """
            {"pid":80,"kind":"interactive","sessionId":"live","cwd":"/work/live","startedAt":100,
             "lastHeartbeat":200,"status":"working"}
            """);
        Assert.Equal("busy", SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 201).Single().Status);

        // the same file after the turn ends: the outcome wins over busy
        File.WriteAllText(Path.Combine(dir.FullName, "80.json"), """
            {"pid":80,"kind":"interactive","sessionId":"live","cwd":"/work/live","startedAt":100,
             "lastHeartbeat":200,"status":"completed"}
            """);
        Assert.Equal("completed", SessionMonitor.ReadWorkBuddy(_home, pid => true, nowMs: 201).Single().Status);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void ApprovalRequiresBothOptInsOnAnEnabledPage(bool setting, bool page, bool widget, bool expected)
    {
        var sessions = new WidgetConfig { Type = "sessions" };
        sessions.Set("workBuddyApprovals", widget);
        var cfg = new AgentConfig
        {
            WorkBuddy = new WorkBuddyConfig { PhoneApprovals = setting },
            Pages = [new PageConfig { Enabled = page, Widgets = [sessions] }],
        };
        Assert.Equal(expected, WorkBuddyHook.Allowed(cfg));
    }

    [Fact]
    public async Task WorkBuddyApprovalsDefaultOffEvenWhenClaudeApprovalsAreOn()
    {
        var store = new ConfigStore(Path.Combine(_home, "config.json"));
        Assert.True(store.Get().Claude.PhoneApprovals);
        Assert.False(store.Get().WorkBuddy.PhoneApprovals);
        Assert.False(store.Get().WorkBuddy.Sessions);
        var logger = NullLogger<SessionMonitor>.Instance;
        var monitor = new SessionMonitor(logger, store);
        var pushes = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_home, "push.json"));
        var approvals = new ApprovalStore(pushes, NullLogger<ApprovalStore>.Instance);
        using var input = JsonDocument.Parse("""{"tool_name":"Read","session_id":"test","cwd":"/work/project"}""");
        Assert.Null(await ClaudeHook.HandleWorkBuddyAsync(input.RootElement, store, approvals, monitor, CancellationToken.None));
        Assert.Empty(approvals.Snapshot());
    }

    [Fact]
    public void InstallsTheWorkBuddyEndpointWithoutChangingOtherHooks()
    {
        var path = Path.Combine(_home, "settings.json");
        File.WriteAllText(path, """{"hooks":{"SessionStart":[]},"other":"keep"}""");

        Assert.Null(ClaudeHook.Configure(true, 8765, 90, path, WorkBuddyHook.Endpoint));
        Assert.True(ClaudeHook.Installed(path, WorkBuddyHook.Endpoint));
        Assert.False(ClaudeHook.Installed(path));
        Assert.Contains("SessionStart", File.ReadAllText(path));
        Assert.Contains("other", File.ReadAllText(path));
        Assert.Null(ClaudeHook.Configure(false, 8765, 90, path, WorkBuddyHook.Endpoint));
        Assert.False(ClaudeHook.Installed(path, WorkBuddyHook.Endpoint));
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);
}
