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
