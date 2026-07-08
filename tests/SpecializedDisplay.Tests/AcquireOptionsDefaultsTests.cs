using SpecializedDisplay;
using Xunit;

namespace SpecializedDisplay.Tests;

// Tripwire: every AcquireOptions default is the hardware-tuned value the original renderer used as a
// named constant. One assert per constant — a failure here means a default silently drifted, which
// would change pacing/throttle/revive/recovery behavior on hardware.
public class AcquireOptionsDefaultsTests
{
    private static AcquireOptions Defaults() => new() { ModeSelector = _ => true };

    [Fact] public void Rotation_Default() => Assert.Equal(0, Defaults().Rotation);
    [Fact] public void FlipX_Default() => Assert.False(Defaults().FlipX);
    [Fact] public void FlipY_Default() => Assert.False(Defaults().FlipY);
    [Fact] public void BufferCount_Default() => Assert.Equal(2, Defaults().BufferCount);
    [Fact] public void RefreshHz_Default() => Assert.Equal(60.0, Defaults().RefreshHz);
    [Fact] public void InstantWaitFraction_Default() => Assert.Equal(0.5, Defaults().InstantWaitFraction);
    [Fact] public void BackgroundEnterFrames_Default() => Assert.Equal(8, Defaults().BackgroundEnterFrames);
    [Fact] public void BackgroundSleepMs_Default() => Assert.Equal(200, Defaults().BackgroundSleepMs);
    [Fact] public void FenceReviveIntervalMs_Default() => Assert.Equal(5000, Defaults().FenceReviveIntervalMs);
    [Fact] public void FenceDeadReacquireAfterMs_Default() => Assert.Equal(15000, Defaults().FenceDeadReacquireAfterMs);
    [Fact] public void ConsoleWakeGraceMs_Default() => Assert.Equal(2000, Defaults().ConsoleWakeGraceMs);
    [Fact] public void RenderFenceTimeoutMs_Default() => Assert.Equal(500, Defaults().RenderFenceTimeoutMs);
    [Fact] public void VBlankFenceTimeoutMs_Default() => Assert.Equal(500, Defaults().VBlankFenceTimeoutMs);
    [Fact] public void AcquireAttempts_Default() => Assert.Equal(6, Defaults().AcquireAttempts);
    [Fact] public void AcquireRetryBaseMs_Default() => Assert.Equal(150, Defaults().AcquireRetryBaseMs);
    [Fact] public void ReacquireAttempts_Default() => Assert.Equal(6, Defaults().ReacquireAttempts);
    [Fact] public void ReacquireBackoffBaseMs_Default() => Assert.Equal(150, Defaults().ReacquireBackoffBaseMs);
    [Fact] public void ReacquireBackoffMaxMs_Default() => Assert.Equal(800, Defaults().ReacquireBackoffMaxMs);
    [Fact] public void Log_Default_IsNull() => Assert.Null(Defaults().Log);
}
