using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Turns WorkBuddy session state changes into phone notifications (F5). The monitor calls
/// <see cref="Watch"/> after each published read; the previous snapshot is kept here, so a
/// change of state becomes one event. Only the phone's own notification channel is used
/// (PushStore.Emit("notify", …)) — no request leaves the machine, ever.
///
/// The severities are fixed on purpose: an agent waiting for you or one that failed is
/// urgent (the phone vibrates), a finished one is only a quiet note, and busy/unknown are
/// not worth waking anyone for. Repeats of the same transition within a minute collapse
/// into one notification, so a flickering state cannot spam the phone.
/// </summary>
public static class WorkBuddyEvents
{
    /// <summary>Two of the same notifications for one session collapse within this window.</summary>
    internal static long DebounceMs = 60_000;

    // last state seen per session id, and the time its notification last went out
    private readonly record struct Seen(string Status, long At);
    private static readonly Dictionary<string, Seen> Last = new();
    private static readonly Dictionary<string, long> LastNotified = new();
    private static readonly object Gate = new();

    /// <summary>For tests: forget every session, as a fresh start does.</summary>
    internal static void Reset()
    {
        lock (Gate) { Last.Clear(); LastNotified.Clear(); }
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
            var seen = new HashSet<string>();
            foreach (var s in current.Where(s => s.Tool == "workbuddy"))
            {
                seen.Add(s.Id);
                var before = Last.TryGetValue(s.Id, out var was) ? was.Status : null;
                // a first sighting of a session that is already waiting (or has
                // failed) counts too: the phone was not told about it before
                if (before != s.Status && WorthTelling(s.Status)
                    && (!LastNotified.TryGetValue(s.Id, out var at) || nowMs - at >= DebounceMs))
                {
                    LastNotified[s.Id] = nowMs;
                    Emit(push, s, nowMs);
                    emitted++;
                }
                Last[s.Id] = new Seen(s.Status, nowMs);
            }
            // a session whose file is gone has ended for good: forget it, so a later
            // session reusing its id tells its own first transition again
            foreach (var gone in Last.Keys.Where(id => !seen.Contains(id)).ToList()) { Last.Remove(gone); LastNotified.Remove(gone); }
        }
        return emitted;
    }

    /// <summary>waiting and failed wake the phone; completed only notes; busy and unknown say nothing.</summary>
    private static bool WorthTelling(string status) => status is "waiting" or "failed" or "completed";

    private static void Emit(PushStore push, AgentSession s, long nowMs)
    {
        // The text is the i18n key, not English prose: the phone's dictionary
        // fills it in (wbEvents.*), so the notification speaks the user's
        // language. A phone without the key shows the key — never a wrong fact.
        var (level, vibrate, key) = s.Status switch
        {
            "waiting" => ("warn", true, "wbEvents.waiting"),
            "failed" => ("error", true, "wbEvents.failed"),
            _ => ("ok", false, "wbEvents.completed"),
        };
        push.Emit("notify", JsonSerializer.SerializeToElement(new
        {
            title = "WorkBuddy",
            text = $"{key}|{s.Project}", // key first, the project as its argument
            level,
            vibrate,
            at = nowMs,
        }));
    }
}
