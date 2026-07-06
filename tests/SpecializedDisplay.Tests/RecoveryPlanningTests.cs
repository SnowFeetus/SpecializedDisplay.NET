using SpecializedDisplay;
using Xunit;

namespace SpecializedDisplay.Tests;

// Pure recovery-logic guards (no hardware): the wake-escalation timing gate and the bounded-reacquire
// attempt state machine that the session drives one-attempt-per-frame.
public class RecoveryPlanningTests
{
    // ---- WakeEscalation ----

    // FenceDeathFlavor / ReacquireMode / ReacquireFailAction are internal; xunit test methods are public,
    // so the enums appear only in method BODIES (accessible via InternalsVisibleTo), never in signatures.
    [Fact] public void FlavorEscalates_Sentinel_True() => Assert.True(WakeEscalation.FlavorEscalates(FenceDeathFlavor.DeviceRemovedSentinel));
    [Fact] public void FlavorEscalates_StoppedAdvancing_False() => Assert.False(WakeEscalation.FlavorEscalates(FenceDeathFlavor.StoppedAdvancing));
    [Fact] public void FlavorEscalates_None_False() => Assert.False(WakeEscalation.FlavorEscalates(FenceDeathFlavor.None));

    [Fact]
    public void IsDue_TrueOnlyForSentinel_WhenEnabledAndScheduled()
    {
        // now (5000) has reached the scheduled escalation time (5000), enabled (15000).
        Assert.True(WakeEscalation.IsDue(FenceDeathFlavor.DeviceRemovedSentinel, 5000, 5000, 15000));
    }

    [Fact]
    public void IsDue_FalseBeforeScheduledTime()
        => Assert.False(WakeEscalation.IsDue(FenceDeathFlavor.DeviceRemovedSentinel, 4999, 5000, 15000));

    [Fact]
    public void IsDue_FalseForStoppedAdvancingFlavor()
        => Assert.False(WakeEscalation.IsDue(FenceDeathFlavor.StoppedAdvancing, 99999, 0, 15000));

    [Fact]
    public void IsDue_FalseWhenDisabled_IntervalZero()
        => Assert.False(WakeEscalation.IsDue(FenceDeathFlavor.DeviceRemovedSentinel, 99999, 0, 0));

    // ---- ReacquirePlan: shared attempt machine ----

    private static ReacquirePlan Plan() => new(maxAttempts: 6, backoffBaseMs: 150, backoffMaxMs: 800);

    [Fact]
    public void Backoff_MatchesMinCapFormula()
    {
        var p = Plan();
        p.Start(ReacquireMode.Genuine);
        // Min(800, 150 * attempt): 150, 300, 450, 600, 750, then capped at 800.
        int[] expected = { 150, 300, 450, 600, 750, 800 };
        for (int i = 0; i < 6; i++)
        {
            p.BeginAttempt();
            Assert.Equal(expected[i], p.BackoffMs());
            if (i < 5) p.Failed(); // stay active for the next attempt
        }
    }

    [Fact]
    public void Genuine_SixthFailureIsFatal_EarlierFailuresBackoff()
    {
        var p = Plan();
        p.Start(ReacquireMode.Genuine);
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            p.BeginAttempt();
            Assert.Equal(ReacquireFailAction.Backoff, p.Failed());
            Assert.True(p.Active);
        }
        p.BeginAttempt(); // 6th
        Assert.Equal(ReacquireFailAction.Fatal, p.Failed());
        Assert.False(p.Active); // episode ended
    }

    [Fact]
    public void Wake_SixthFailureReschedules_NeverFatal()
    {
        var p = Plan();
        p.Start(ReacquireMode.Wake);
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            p.BeginAttempt();
            Assert.Equal(ReacquireFailAction.Backoff, p.Failed());
        }
        p.BeginAttempt(); // 6th
        Assert.Equal(ReacquireFailAction.Reschedule, p.Failed());
        Assert.False(p.Active);
    }

    [Fact]
    public void Success_EndsEpisode_AndResetsAttempt()
    {
        var p = Plan();
        p.Start(ReacquireMode.Genuine);
        p.BeginAttempt();
        p.BeginAttempt();
        Assert.Equal(2, p.Attempt);
        p.Succeeded();
        Assert.False(p.Active);
        Assert.Equal(0, p.Attempt);
    }

    [Fact]
    public void Start_IsIdempotent_WhileActive_AndDoesNotChangeMode()
    {
        var p = Plan();
        p.Start(ReacquireMode.Genuine);
        p.BeginAttempt();
        p.Start(ReacquireMode.Wake); // ignored: already active
        Assert.Equal(ReacquireMode.Genuine, p.Mode);
        Assert.Equal(1, p.Attempt); // not reset
    }

    [Fact]
    public void Start_AfterTerminal_BeginsFreshEpisode()
    {
        var p = Plan();
        p.Start(ReacquireMode.Genuine);
        for (int i = 1; i <= 6; i++) { p.BeginAttempt(); p.Failed(); }
        Assert.False(p.Active);
        // A later loss can start a new episode, possibly in a different mode.
        p.Start(ReacquireMode.Wake);
        Assert.True(p.Active);
        Assert.Equal(ReacquireMode.Wake, p.Mode);
        Assert.Equal(0, p.Attempt);
    }
}
