using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class WorkBuddyEventsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-wb-events-" + Guid.NewGuid().ToString("N"));
    private readonly PushStore _push;
    private long _now = 1_000_000_000_000;

    public WorkBuddyEventsTests()
    {
        Directory.CreateDirectory(_dir);
        _push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events.json"));
        WorkBuddyEvents.Reset();
    }

    private static AgentSession Session(string id, string project, string status) =>
        new("workbuddy:" + id, "workbuddy", project, project, status, null, _epoch + 1, _epoch, Pid: 1);

    private static readonly long _epoch = 1_700_000_000_000;

    private Notify[] NotifyEvents()
    {
        // the notify events this store holds (only our kind arrive in these tests)
        var all = _push.Since(0).Where(e => e.Kind == "notify").ToList();
        return all.Select(e => new Notify(
            e.Data!.Value.GetProperty("title").GetString()!,
            e.Data.Value.GetProperty("text").GetString()!,
            e.Data.Value.GetProperty("level").GetString()!,
            e.Data.Value.GetProperty("vibrate").GetBoolean())).ToArray();
    }

    private sealed record Notify(string Title, string Text, string Level, bool Vibrate);

    private int Watch(params AgentSession[] sessions) =>
        WorkBuddyEvents.Watch(sessions, new WorkBuddyConfig { Sessions = true, Events = true }, _push, _now);

    [Fact]
    public void WaitingAndFailedAlertCompletedNotesQuietly()
    {
        Assert.Equal(1, Watch(Session("1", "report", "waiting")));
        var n = Assert.Single(NotifyEvents());
        Assert.Equal("WorkBuddy", n.Title);
        Assert.Equal("warn", n.Level);
        Assert.True(n.Vibrate);
        Assert.StartsWith("wbEvents.waiting|", n.Text);

        _now += 61_000;
        Assert.Equal(1, Watch(Session("1", "report", "failed")));
        n = NotifyEvents().Last();
        Assert.Equal("error", n.Level);
        Assert.True(n.Vibrate);

        _now += 61_000;
        Assert.Equal(1, Watch(Session("1", "report", "completed")));
        n = NotifyEvents().Last();
        Assert.Equal("ok", n.Level);
        Assert.False(n.Vibrate); // a finished chat is a note, not an alarm
    }

    [Fact]
    public void BusyAndUnknownNeverNotifyAndWaitingToBusyIsSilent()
    {
        Assert.Equal(1, Watch(Session("1", "api", "waiting")));
        // waiting -> busy is the good path: the chat runs again, nothing to tell
        _now += 61_000;
        Assert.Equal(0, Watch(Session("1", "api", "busy")));
        // busy -> unknown (the feed went quiet) is not an event either
        _now += 61_000;
        Assert.Equal(0, Watch(Session("1", "api", "unknown")));
        Assert.Single(NotifyEvents()); // still only the one waiting notification
    }

    [Fact]
    public void TwoTransitionsWithinTheMinuteCollapseIntoOne()
    {
        Assert.Equal(1, Watch(Session("1", "api", "waiting")));
        // the same session flips again far too soon: the notification is debounced
        _now += 5_000;
        Assert.Equal(0, Watch(Session("1", "api", "failed")));
        // and the flip back to waiting is also inside the window: still nothing
        _now += 5_000;
        Assert.Equal(0, Watch(Session("1", "api", "waiting")));
        Assert.Single(NotifyEvents());
        // after the minute a fresh transition tells again
        _now += 61_000;
        Assert.Equal(1, Watch(Session("1", "api", "failed")));
        Assert.Equal(2, NotifyEvents().Length);
    }

    [Fact]
    public void OffSwitchesSendNothing()
    {
        // rows shown but events off: nothing is sent
        Assert.Equal(0, WorkBuddyEvents.Watch([Session("1", "api", "waiting")],
            new WorkBuddyConfig { Sessions = true, Events = false }, _push, _now));
        // rows themselves off: nothing is sent either
        Assert.Equal(0, WorkBuddyEvents.Watch([Session("1", "api", "waiting")],
            new WorkBuddyConfig { Sessions = false, Events = true }, _push, _now));
        Assert.Empty(NotifyEvents());
    }

    [Fact]
    public void OtherToolsNeverNotify()
    {
        var claude = new AgentSession("claude:s", "claude", "proj", "proj", "waiting", null, _epoch + 1, _epoch, Pid: 1);
        Assert.Equal(0, Watch(claude));
        Assert.Empty(NotifyEvents());
    }

    [Fact]
    public void ASessionThatDisappearsCanTellAgainLater()
    {
        Assert.Equal(1, Watch(Session("1", "api", "waiting")));
        // the session is gone from the list: the monitor forgot it, so a new
        // session reusing its id can tell about its own first state
        _now += 1_000;
        Assert.Equal(0, Watch());
        _now += 61_000;
        Assert.Equal(1, Watch(Session("1", "api", "waiting")));
        Assert.Equal(2, NotifyEvents().Length);
    }

    public void Dispose()
    {
        _push.Dispose();
        Directory.Delete(_dir, recursive: true);
    }
}

public sealed class WorkBuddyRuleStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-wb-rule-" + Guid.NewGuid().ToString("N"));

    public WorkBuddyRuleStatusTests() => Directory.CreateDirectory(_dir);

    private static AgentSession Session(string project, string status) =>
        new("workbuddy:1", "workbuddy", project, project, status, null, 1, 1, Pid: 1);

    /// <summary>The engine with the session snapshot injected, driven without its loop. The
    /// config turns WorkBuddy sessions on: the snapshot filters workbuddy rows otherwise.</summary>
    private (RuleEngine Engine, PushStore Push, SessionMonitor Sessions) EngineWith(string ruleJson)
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, $$"""{ "workBuddy": { "sessions": true }, "rules": [ {{ruleJson}} ] }""");
        var cfg = new ConfigStore(cfgPath);
        var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "push.json"));
        var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance, cfg);
        var engine = new RuleEngine(cfg, push, sessions,
            new MediaMonitor(NullLogger<MediaMonitor>.Instance),
            new SystemMonitor(NullLogger<SystemMonitor>.Instance),
            new PrivacyMonitor(NullLogger<PrivacyMonitor>.Instance),
            new CalendarMonitor(cfg, NullLogger<CalendarMonitor>.Instance),
            new ObsMonitor(cfg, NullLogger<ObsMonitor>.Instance),
            new QuotaMonitor(NullLogger<QuotaMonitor>.Instance),
            new GitHubMonitor(NullLogger<GitHubMonitor>.Instance),
            new BatteryMonitor(NullLogger<BatteryMonitor>.Instance),
            new PingMonitor(), new AudioControl(), NullLogger<RuleEngine>.Instance);
        engine.StartedAtMs = 0; // skip the start-up grace
        return (engine, push, sessions);
    }

    [Fact]
    public void WbStatusRuleFiresAndNotifies()
    {
        var (engine, push, sessions) = EngineWith(
            """{"id":"wb","name":"WB","when":"wb-status","match":"waiting|failed","then":"notify","text":"{detail}"}""");
        sessions.Sessions = [Session("report", "waiting")];
        var seq = push.Seq;
        engine.Evaluate();
        var notify = push.Since(seq).Single(e => e.Kind == "notify");
        Assert.Contains("report is now waiting", notify.Data!.Value.GetProperty("text").GetString());
    }

    [Fact]
    public void WbStatusRuleStaysSilentOnOtherStatesAndTools()
    {
        var (engine, push, sessions) = EngineWith(
            """{"id":"wb","name":"WB","when":"wb-status","match":"waiting|failed","then":"notify"}""");
        // a busy WorkBuddy chat matches none of the listed states
        sessions.Sessions = [Session("report", "busy")];
        engine.Evaluate();
        // a waiting Claude chat is not WorkBuddy's
        sessions.Sessions = [new AgentSession("claude:s", "claude", "report", "report", "waiting", null, 1, 1, Pid: 1)];
        engine.Evaluate();
        Assert.Empty(push.Since(0).Where(e => e.Kind == "notify"));
    }

    [Fact]
    public void WbStatusWithoutAMatchDefaultsToWaitingAndFailed()
    {
        var (engine, push, sessions) = EngineWith(
            """{"id":"wb","name":"WB","when":"wb-status","then":"notify"}""");
        sessions.Sessions = [Session("report", "completed")]; // not in the default pair
        engine.Evaluate();
        sessions.Sessions = [Session("report", "failed")];
        engine.Evaluate();
        Assert.Single(push.Since(0).Where(e => e.Kind == "notify"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
