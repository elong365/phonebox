using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Turns WorkBuddy session state changes into phone notifications (F5). The monitor calls
/// <see cref="Watch"/> after each published read; the previous snapshot is kept here, so a
/// change of state becomes one event. Only the phone's own notification channel is used
/// (PushStore.Emit("notify", …)) — no request leaves the machine, ever.
///
/// The severities are fixed on purpose: a chat that failed is urgent (the phone vibrates
/// and lights the screen), a finished or stopped one is only a quiet note, and busy/unknown
/// are not worth waking anyone for. WorkBuddy files carry no "waiting" state, so nothing
/// here waits either. Repeats of the same state within a minute collapse into one
/// notification, so a flickering state cannot spam the phone.
/// </summary>
public static class WorkBuddyEvents
{
    /// <summary>Two notifications for the same session and state collapse within this window.</summary>
    internal static long DebounceMs = 60_000;

    // last state seen per session id, and — keyed by session and state — the time
    // its notification last went out. A deduplication entry survives a tick in which
    // the session's file is missed (it can be replaced mid-read) and is dropped only
    // when its window is up, so a lost tick cannot replay the same notification.
    private readonly record struct Seen(string Status, long At);
    private static readonly Dictionary<string, Seen> Last = new();
    private static readonly Dictionary<(string Session, string Status), long> LastNotified = new();
    private static bool _seeded;
    private static readonly object Gate = new();

    /// <summary>For tests: forget every session, as a fresh start does.</summary>
    internal static void Reset()
    {
        lock (Gate) { Last.Clear(); LastNotified.Clear(); _seeded = false; }
    }

    /// <summary>
    /// Compares one published read with the one before it and emits a notify event per
    /// transition worth telling about. Returns the events emitted (for tests).
    /// </summary>
    public static int Watch(IReadOnlyList<AgentSession> current, WorkBuddyConfig config, PushStore push, long nowMs)
    {
        if (!config.Sessions || !config.Events) return 0; // both switches must be on: the rows and the telling about them
        var emitted = 0;
        lock (Gate)
        {
            // the first scan after a start (or after the switch was off) only seeds:
            // sessions already in that snapshot had their beginning before the agent
            // could watch for it, so telling about them would flood the phone with
            // every old failure at once
            var seed = !_seeded;
            _seeded = true;
            var seen = new HashSet<string>();
            foreach (var s in current.Where(s => s.Tool == "workbuddy"))
            {
                seen.Add(s.Id);
                var before = Last.TryGetValue(s.Id, out var was) ? was.Status : null;
                // a first sighting of a session that has already failed (or
                // finished) counts too: the phone was not told about it before
                if (!seed && before != s.Status && WorthTelling(s.Status)
                    && (!LastNotified.TryGetValue((s.Id, s.Status), out var at) || nowMs - at >= DebounceMs))
                {
                    LastNotified[(s.Id, s.Status)] = nowMs;
                    Emit(push, s, nowMs);
                    emitted++;
                }
                Last[s.Id] = new Seen(s.Status, nowMs);
            }
            // a session whose file is gone has ended for good: forget its state, so a
            // later session reusing its id tells its own first transition again. The
            // deduplication entries stay until their window is up — the file can come
            // back the next tick after a missed read — and the sweep drops expired ones.
            foreach (var gone in Last.Keys.Where(id => !seen.Contains(id)).ToList()) Last.Remove(gone);
            foreach (var old in LastNotified.Where(kv => nowMs - kv.Value >= DebounceMs).Select(kv => kv.Key).ToList()) LastNotified.Remove(old);
        }
        return emitted;
    }

    /// <summary>failed wakes the phone; completed and stopped only note; busy and unknown say nothing.</summary>
    private static bool WorthTelling(string status) => status is "failed" or "completed" or "stopped";

    private static void Emit(PushStore push, AgentSession s, long nowMs)
    {
        // The text is the i18n key, not English prose: the phone's dictionary
        // fills it in (wbEvents.*), so the notification speaks the user's
        // language. A phone without the key shows the key — never a wrong fact.
        var (level, urgent, key) = s.Status switch
        {
            "failed" => ("error", true, "wbEvents.failed"),
            "stopped" => ("ok", false, "wbEvents.stopped"),
            _ => ("ok", false, "wbEvents.completed"),
        };
        push.Emit("notify", JsonSerializer.SerializeToElement(new
        {
            title = "WorkBuddy",
            text = $"{key}|{s.Project}", // key first, the project as its argument
            level,
            vibrate = urgent,
            // a failed chat lights the night screen, like the rules' warn and error notifications do
            wake = urgent,
            at = nowMs,
        }));
    }
}
