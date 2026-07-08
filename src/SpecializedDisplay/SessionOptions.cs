using Windows.Graphics.DirectX;

namespace SpecializedDisplay;

/// <summary>A candidate display mode surfaced to the <see cref="AcquireOptions.ModeSelector"/> during
/// acquire. Mirrors the fields the renderer inspected on each <c>DisplayModeInfo</c>.</summary>
public sealed record DisplayModeDescriptor(
    int SourceWidth, int SourceHeight, int TargetWidth, int TargetHeight,
    DirectXPixelFormat SourcePixelFormat, double VerticalRefreshHz);

/// <summary>Ready-made mode predicates.</summary>
public static class ModeSelectors
{
    /// <summary>Accept only a 1:1 mode whose source AND target are exactly <paramref name="width"/> ×
    /// <paramref name="height"/> in the given <paramref name="format"/>. This is the renderer's exact
    /// filter, named — it rejects the RTK409A's scaled modes (src &lt; tgt), which must never be used
    /// (display-findings.md).</summary>
    public static Func<DisplayModeDescriptor, bool> NativeOneToOne(
        int width, int height, DirectXPixelFormat format)
        => m => m.SourceWidth == width && m.SourceHeight == height
             && m.TargetWidth == width && m.TargetHeight == height
             && m.SourcePixelFormat == format;
}

/// <summary>
/// Everything that governs an acquire and the subsequent present loop. Every named constant from the
/// original renderer appears here name-for-name with its hardware-tuned current value as the default,
/// so a caller that constructs <c>new AcquireOptions { ModeSelector = … }</c> gets byte-identical
/// pacing/throttle/revive/recovery behavior. The defaults are covered by a tripwire unit test.
/// </summary>
public sealed class AcquireOptions
{
    /// <summary>Chooses the mode to apply from the target's enumerated modes. Required.</summary>
    public required Func<DisplayModeDescriptor, bool> ModeSelector { get; init; }

    public int  Rotation { get; init; } = 0;              // 0 | 90 | 180 | 270 (panel uses 90/270)
    public bool FlipX { get; init; }
    public bool FlipY { get; init; }
    public int  BufferCount { get; init; } = 2;           // page-flip primaries (2 = double-buffered)
    public double RefreshHz { get; init; } = 60.0;        // pacing target; TargetFrameMs is derived

    // Background/disconnect throttle.
    public double InstantWaitFraction { get; init; } = 0.5;   // InstantWaitMs = TargetFrameMs * this
    public int    BackgroundEnterFrames { get; init; } = 8;   // consecutive instant waits before throttling
    public int    BackgroundSleepMs { get; init; } = 200;     // low cadence while backgrounded (~5fps)

    // Dead-fence revive (hardware-verified 2026-07-04: display power-off can kill the periodic fence).
    public double FenceReviveIntervalMs { get; init; } = 5000;

    // Wake escalation (hardware-verified 2026-07-06). The periodic fence can die in two flavors: it can
    // stop advancing (DPMS-ish — the FenceReviveIntervalMs rebuild recovers it on wake), or it can report
    // the device-removed SENTINEL (CompletedValue == ulong.MaxValue with the device NOT actually removed)
    // — a slept panel that ignores desktop wake. The latter flavor is NEVER recovered by rebuilding the
    // fence: only a full Release()+Acquire() (mode re-apply) restores scanout. When the fence has been
    // dead in the sentinel flavor for longer than this and the rebuilds have not helped, the session
    // escalates to an internal release + re-acquire, retried on this same cadence. 0 disables escalation
    // (fall back to rebuild-only). A sleeping panel is not fatal, so this path never throws.
    public double FenceDeadReacquireAfterMs { get; init; } = 15000;

    // Console-wake re-acquire (hardware-traced 2026-07-06). On desktop wake Windows re-powers only
    // the monitors in the desktop topology — an exclusively-owned specialized target stays dark
    // until its owner re-applies a mode, and the session's only other signal (a blocking vblank)
    // can never arrive on a powered-down scanout (observed: 14h dark panel on an awake desktop).
    // When the console display state transitions to ON while the session is display-off throttled,
    // wait this grace (a vblank that resumes by itself within it — a merely-backgrounded session
    // reconnecting — cancels the escalation), then release + re-acquire to re-light the panel.
    // 0 disables the console-wake path. Retries on the FenceDeadReacquireAfterMs cadence if the
    // re-acquire itself exhausts (so a still-dark panel keeps trying while the desktop is awake).
    public double ConsoleWakeGraceMs { get; init; } = 2000;

    // Fence waits.
    public int RenderFenceTimeoutMs { get; init; } = 500;
    public int VBlankFenceTimeoutMs { get; init; } = 500;

    // Acquire inner loop: TryAcquire/TryApply retry (Sleep(AcquireRetryBaseMs * attempt)).
    public int AcquireAttempts { get; init; } = 6;
    public int AcquireRetryBaseMs { get; init; } = 150;

    // Bounded reacquire: total < ~3s so a 5s watchdog never fires. Sleep(Min(Max, Base * attempt)).
    public int ReacquireAttempts { get; init; } = 6;
    public int ReacquireBackoffBaseMs { get; init; } = 150;
    public int ReacquireBackoffMaxMs { get; init; } = 800;

    /// <summary>Optional diagnostic sink. The library logs ONLY through this — never Console/IPC.</summary>
    public Action<LogLevel, string>? Log { get; init; }
}
