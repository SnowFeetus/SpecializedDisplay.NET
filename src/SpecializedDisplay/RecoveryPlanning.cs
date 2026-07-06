namespace SpecializedDisplay;

/// <summary>How the periodic V-blank fence died mid-flight. The two flavors were distinguished on
/// hardware (2026-07-06) and recover DIFFERENTLY, so the session must remember which one it saw:
/// <list type="bullet">
/// <item><see cref="StoppedAdvancing"/> — the fence stopped delivering V-blanks (a DPMS-ish display
/// power-down). Rebuilding the periodic fence (<c>TryReviveFencePacing</c>) recovers this when the
/// display wakes: a healthy rebuilt fence blocks on its next wait and the throttle clears.</item>
/// <item><see cref="DeviceRemovedSentinel"/> — <c>CompletedValue == ulong.MaxValue</c> while the device
/// is NOT actually removed (a slept panel that ignored desktop wake). Rebuilding the fence NEVER
/// recovers this flavor; only a full release + re-acquire (mode re-apply) restores scanout.</item>
/// </list>
/// </summary>
internal enum FenceDeathFlavor
{
    /// <summary>Fence healthy, or no death recorded since the last (re)acquire.</summary>
    None,
    /// <summary>Fence stopped advancing (wait timeout). Recovered by rebuilding the fence.</summary>
    StoppedAdvancing,
    /// <summary>Device-removed sentinel with the device alive. Recovered only by release + re-acquire.</summary>
    DeviceRemovedSentinel,
}

/// <summary>Timing gate for the wake escalation (release + re-acquire) triggered by a persistently dead
/// fence. Pure so it is unit-testable without hardware. Escalation runs ONLY for the
/// <see cref="FenceDeathFlavor.DeviceRemovedSentinel"/> flavor (the other flavor is recovered by the
/// fence rebuild) and only while enabled (interval &gt; 0).</summary>
internal static class WakeEscalation
{
    /// <summary>The sentinel flavor is the only one a fence rebuild cannot fix, so it is the only one
    /// that warrants escalating to a release + re-acquire.</summary>
    public static bool FlavorEscalates(FenceDeathFlavor flavor)
        => flavor == FenceDeathFlavor.DeviceRemovedSentinel;

    /// <summary>True when an escalation attempt is due now: escalation is enabled
    /// (<paramref name="reacquireAfterMs"/> &gt; 0), the fence died in the escalating flavor, and the
    /// scheduled time (<paramref name="nextEscalationMs"/>, on the caller's pace clock) has arrived.</summary>
    public static bool IsDue(FenceDeathFlavor flavor, double nowMs, double nextEscalationMs, double reacquireAfterMs)
        => reacquireAfterMs > 0 && FlavorEscalates(flavor) && nowMs >= nextEscalationMs;
}

/// <summary>Which recovery episode is driving the shared one-attempt-per-frame acquire machine.</summary>
internal enum ReacquireMode
{
    /// <summary>A present-detected device/ownership loss. Bounded; on exhaustion the session THROWS
    /// <see cref="DisplayRecoveryFailedException"/> out of BeginFrame so the host can exit for its
    /// supervisor to restart it.</summary>
    Genuine,
    /// <summary>A persistently-dead-fence wake escalation. On exhaustion the session NEVER throws — a
    /// sleeping panel is not fatal; it reschedules the escalation and stays in the throttled state.</summary>
    Wake,
}

/// <summary>Terminal action when an acquire attempt fails.</summary>
internal enum ReacquireFailAction
{
    /// <summary>Attempts remain — back off (bounded) and retry on the next frame.</summary>
    Backoff,
    /// <summary>Genuine recovery exhausted — throw <see cref="DisplayRecoveryFailedException"/>.</summary>
    Fatal,
    /// <summary>Wake escalation exhausted — reschedule and return to the throttled fence-dead state.</summary>
    Reschedule,
}

/// <summary>
/// The bounded-reacquire attempt state machine, extracted from the original in-<c>BeginFrame</c> loop so
/// it can be spread across successive frames (one attempt per frame) and unit-tested as pure logic.
///
/// <para><b>Watchdog budget invariant.</b> The original loop ran up to <c>ReacquireAttempts</c> acquire
/// attempts inside a SINGLE BeginFrame call; on a sleeping/contended target the cumulative API + backoff
/// time exceeded the supervisor's ~5s "frozen" threshold and the process was killed. This machine hands
/// out ONE attempt at a time; the session performs at most one attempt (one TryAcquire + one TryApply)
/// plus one bounded backoff sleep per frame, so the caller's heartbeat keeps beating between attempts.</para>
///
/// <para>Behavior preserved from the original bounded reacquire: <c>MaxAttempts</c> total attempts, the
/// same <c>Min(BackoffMaxMs, BackoffBaseMs * attempt)</c> backoff, and (for <see cref="ReacquireMode.Genuine"/>)
/// the terminal throw. The <see cref="ReacquireMode.Wake"/> escalation reuses the same machine but never
/// throws on exhaustion.</para>
/// </summary>
internal sealed class ReacquirePlan
{
    private readonly int _maxAttempts;
    private readonly int _backoffBaseMs;
    private readonly int _backoffMaxMs;

    /// <summary>Which episode is running. Meaningful only while <see cref="Active"/>.</summary>
    public ReacquireMode Mode { get; private set; }
    /// <summary>True between <see cref="Start"/> and a terminal (<see cref="Succeeded"/> / exhausted).</summary>
    public bool Active { get; private set; }
    /// <summary>The 1-based number of the most recently begun attempt (0 before the first).</summary>
    public int Attempt { get; private set; }

    public ReacquirePlan(int maxAttempts, int backoffBaseMs, int backoffMaxMs)
    {
        _maxAttempts = maxAttempts;
        _backoffBaseMs = backoffBaseMs;
        _backoffMaxMs = backoffMaxMs;
    }

    /// <summary>Begin a recovery episode. Idempotent while one is already running (a mode cannot be
    /// changed mid-episode — the owned/not-owned states that trigger Genuine vs Wake are mutually
    /// exclusive, so this only guards against re-entrancy).</summary>
    public void Start(ReacquireMode mode)
    {
        if (Active) return;
        Active = true;
        Mode = mode;
        Attempt = 0;
    }

    /// <summary>Claim the next attempt number for this frame. Call once per recovering frame before
    /// performing the acquire attempt.</summary>
    public int BeginAttempt() => ++Attempt;

    /// <summary>The bounded backoff for the current attempt, matching the original
    /// <c>Min(ReacquireBackoffMaxMs, ReacquireBackoffBaseMs * attempt)</c>.</summary>
    public int BackoffMs() => Math.Min(_backoffMaxMs, _backoffBaseMs * Attempt);

    /// <summary>Ownership was restored — end the episode.</summary>
    public void Succeeded() => Reset();

    /// <summary>Abandon any episode in progress (e.g. an explicit Acquire/Release supersedes it).</summary>
    public void Reset()
    {
        Active = false;
        Attempt = 0;
    }

    /// <summary>The current attempt failed. Returns whether to back off and retry, or terminate (throw
    /// for <see cref="ReacquireMode.Genuine"/>, reschedule for <see cref="ReacquireMode.Wake"/>). On a
    /// terminal outcome the episode ends.</summary>
    public ReacquireFailAction Failed()
    {
        if (Attempt >= _maxAttempts)
        {
            Active = false;
            return Mode == ReacquireMode.Genuine ? ReacquireFailAction.Fatal : ReacquireFailAction.Reschedule;
        }
        return ReacquireFailAction.Backoff;
    }
}
