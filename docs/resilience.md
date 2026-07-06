# Resilience: pacing, throttle, dead-fence revive, and bounded reacquire

An exclusive scanout session runs its own present loop and must survive the messy real-world states a
`DisplayManager`-owned panel gets into: the owning session being backgrounded, the display powering
off, the source being yanked by another client, or the device being removed. This is the design of
those safeguards — all hardware-verified on the reference panel.

## Hard frame cap + background throttle

When the owning session is backgrounded (fast-user-switch / RDP / lock screen), the V-blank wait
**stops blocking** and returns near-instantly. Ownership is still held (`IsOwned == true`, no
device-removed), so nothing signals a loss — but the present loop would free-run at 300–1000 fps,
cooking the GPU on a thermals-sensitive panel.

Two layered defenses, both in the pace step after each present:

1. **Hard frame cap.** Every present is floored to the refresh interval (`1000 / RefreshHz` ms). If a
   cycle came in under budget because the V-blank wait returned early, we sleep the remainder. This
   caps the rate at the refresh target even when the wait never blocks.
2. **Background throttle.** We time each wait. A run of `BackgroundEnterFrames` consecutive
   near-instant waits (shorter than `TargetFrameMs * InstantWaitFraction`) flips the loop into an
   idle cadence (`Sleep(BackgroundSleepMs)`, ~5 fps). We **do not re-acquire** here — a backgrounded
   session cannot, and nothing was actually lost. The loop still calls the V-blank wait every cycle,
   so the moment real (blocking) V-blanks return (session reconnected), the throttle clears itself
   and normal pacing resumes with no other intervention.

Each transition logs once for observability. The supervisor never sees an event for this — it is a
pacing condition, not an ownership loss.

## Dead-fence revive (display power-off)

A Windows display power-off (screen timeout) can kill the periodic fence **permanently**: after the
timeout fired, every fence wait returned instantly even once the panel was scanning again, so the
loop sat in the background throttle forever (~5 fps under touch). Observed on hardware.

Recovery: when the fence dies — signalled by the device-removed **sentinel** (`CompletedValue ==
ulong.MaxValue`, which is NOT an actual device removal) or by a wait timeout — the session stops
consulting it, drops to throttled fence-less pacing, and **rebuilds** the periodic fence on a slow
retry cadence (`FenceReviveIntervalMs`). A healthy rebuilt fence **blocks** on its next wait, which
clears the throttle naturally. The same rebuild timer runs while merely backgrounded, covering a
fence that free-runs (instant completions) rather than stalling.

**Critical invariant:** in the fence-dead state the V-blank wait returns immediately and must
**never** fall through to `WaitForVBlank`. On a powered-off display that call can block unboundedly,
starving the caller's heartbeat and getting the process killed by its watchdog.

Rebuild attempts while the display is still off fail by design and are logged quietly (the degrade
was already logged once) — no per-interval warn spam.

## Recovery classification

Present failures are classified and routed to typed events; the host forwards them to its supervisor:

| Signal | Class | Event |
|---|---|---|
| `PresentStatus.DeviceInvalid`, `DeviceRemovedReason` failure, HRESULT `DEVICE_REMOVED`/`HUNG`/`RESET` | device removed | `DeviceRemoved` |
| `PresentStatus.SourceInvalid` / `SourceStatusPreventedPresent`, `VIDPN_SOURCE_IN_USE`, acquire `TargetAccessDenied` | ownership lost | `OwnershipLost` |
| `PresentStatus.ScanoutInvalid` / `UnknownFailure` | transient | (logged, continue) |

A recovery sets a pending-reacquire latch (double-fire guarded) and defers the actual work to the
next `BeginFrame`.

## Bounded reacquire vs. the supervisor watchdog

In-process recovery handles the common transient case (brief ownership loss, momentary
source-in-use). It is intentionally **bounded and short** so `BeginFrame` never blocks past a typical
supervisor's ~5s "frozen" threshold:

- `ReacquireAttempts` attempts (default 6), each backing off `Min(ReacquireBackoffMaxMs,
  ReacquireBackoffBaseMs * attempt)` — total well under ~3s.
- The **first** `TargetNotFound` during reacquire raises `TargetOffDesktop` once.
- On success, `ModeApplied` → `Acquired` → `Reacquired(attempt)` fire in that order.
- On exhaustion, `BeginFrame` throws **`DisplayRecoveryFailedException`**. The host is expected to let
  the process exit so its supervisor owns persistent-failure restart (backoff + crash-loop breaking) —
  the right layer for a genuinely absent or held panel.

## Cooperative Release / Acquire

`Release()` voluntarily gives up ownership without tearing down the session object: it releases all
GPU/display state, suppresses auto-reacquire, latches `IsReleased`, and raises `Released`. While
released, `BeginFrame`/`EndFramePresent` throw `InvalidOperationException`. A later `Acquire()` clears
the latch and runs the standard acquire, including the **force-redraw + fresh pacing/fence-revive
state reset** — so a re-acquired session never flips to a stale, never-drawn primary. This lets a host
hand the panel to another process (or park it) and reclaim it later on the same session instance.

## Tunables

Every constant above is an `AcquireOptions` property defaulting to the hardware-tuned value; a unit
test asserts the defaults so they cannot silently drift.
