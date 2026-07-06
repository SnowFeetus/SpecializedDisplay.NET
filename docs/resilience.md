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

## Dead-fence revive (display power-off) — two flavors

A Windows display power-off (screen timeout / sleep) can kill the periodic fence. On hardware
(2026-07-04 and 2026-07-06) it dies in **two distinct flavors** that recover differently, so the
session records which one it saw (`FenceDeathFlavor`):

1. **Stopped advancing** (DPMS-ish) — the fence stops delivering V-blanks; the wait times out. Every
   fence wait then returns instantly even once the panel is scanning again, so the loop would sit in
   the background throttle forever (~5 fps under touch). **Recovered by rebuilding the fence:** on a
   slow retry cadence (`FenceReviveIntervalMs`) the session disposes the fence and re-runs the periodic
   fence setup; a healthy rebuilt fence **blocks** on its next wait, which clears the throttle naturally.
2. **Device-removed sentinel** — `CompletedValue == ulong.MaxValue` while the device is **not** actually
   removed (`DeviceRemovedReason` is still success). This is a slept panel that ignored the desktop
   wake. **Rebuilding the fence NEVER recovers this flavor** (a rebuilt fence just re-sentinels, and
   would also keep resetting the escalation timer below) — so for this flavor the rebuild is *skipped*
   and recovery is left to the **wake escalation**.

In both flavors the session stops consulting the fence and drops to throttled fence-less pacing. The
rebuild timer also runs while merely backgrounded (flavor `None`), covering a fence that free-runs
(instant completions) rather than stalling.

**Critical invariant:** in the fence-dead state the V-blank wait returns immediately and must
**never** fall through to `WaitForVBlank`. On a powered-off display that call can block unboundedly,
starving the caller's heartbeat and getting the process killed by its watchdog.

Rebuild attempts while the display is still off fail by design and are logged quietly (the degrade
was already logged once) — no per-interval warn spam.

## Wake escalation (device-removed-sentinel fence)

The device-removed-sentinel flavor above is only ever cleared by a full **release + re-acquire** (mode
re-apply): proven on hardware (2026-07-06), a slept panel that ignored the desktop wake came back to a
healthy 60 Hz cadence immediately after a release/re-acquire cycle, whereas a fence rebuild never
touched it.

So when the fence has been dead in the sentinel flavor for longer than `FenceDeadReacquireAfterMs`
(default 15 s; `0` disables), the session **escalates**: it releases GPU/display state and re-acquires
the target. The escalation is *detected/scheduled* in the present path (where the sentinel is observed)
and *executed* through the shared one-attempt-per-frame acquire machine (below), routed to the same
`TargetOffDesktop` / `Reacquired` events as a normal re-acquire.

A sleeping panel is **not** a fatal condition, so this path **never** throws
`DisplayRecoveryFailedException`. On success everything resets (fresh pacing, fence, force-redraw). On
exhaustion it reschedules the next escalation `FenceDeadReacquireAfterMs` later and stays in the
throttled, not-owned state, heartbeating throughout.

## Recovery classification

Present failures are classified and routed to typed events; the host forwards them to its supervisor:

| Signal | Class | Event |
|---|---|---|
| `PresentStatus.DeviceInvalid`, `DeviceRemovedReason` failure, HRESULT `DEVICE_REMOVED`/`HUNG`/`RESET` | device removed | `DeviceRemoved` |
| `PresentStatus.SourceInvalid` / `SourceStatusPreventedPresent`, `VIDPN_SOURCE_IN_USE`, acquire `TargetAccessDenied` | ownership lost | `OwnershipLost` |
| `PresentStatus.ScanoutInvalid` / `UnknownFailure` | transient | (logged, continue) |

A recovery sets a pending-reacquire latch (double-fire guarded) and defers the actual work to the
next `BeginFrame`.

## Bounded reacquire — one attempt per frame (watchdog budget)

In-process recovery handles the common transient case (brief ownership loss, momentary source-in-use)
and is the mechanism the wake escalation also drives.

**Watchdog-budget invariant:** no single `BeginFrame` call may block anywhere near a typical
supervisor's ~5 s "frozen" threshold. Re-acquiring the target costs seconds *per attempt* on a
sleeping/contended panel (each `TryAcquireTargetsAndCreateEmptyState` / `TryApply` can itself block for
seconds — the API time, not just the backoff sleeps, is what the original `< ~3 s` budget under-counted).
Running the whole bounded loop inside one `BeginFrame` therefore blew past the watchdog and got the
renderer killed (observed on hardware, 2026-07-06). So the loop is now **spread one attempt per frame**:

- Each recovering `BeginFrame` performs **at most one** acquire attempt (one `TryAcquire` + one
  `TryApply`) plus **at most one** bounded backoff sleep (`Min(ReacquireBackoffMaxMs,
  ReacquireBackoffBaseMs * attempt)`, ≤ ~0.8 s). Attempt state is carried across frames.
- Between attempts, control returns to the caller so its **heartbeat keeps beating**.
- Because the caller draws unconditionally after `BeginFrame` (there is no `IsOwned` gate in the hot
  loop), a not-owned frame is handed an **idle canvas** — a discarded WARP-backed D2D target (see
  `IdleCanvas`, built lazily and independent of the display adapter so it survives a real device-removed).
  The frame is drawn and thrown away; nothing is scanned out; `IsOwned`/`IsReleased` stay `false`.

Preserved semantics of the bounded reacquire:

- `ReacquireAttempts` total attempts (default 6), the same `Min(ReacquireBackoffMaxMs,
  ReacquireBackoffBaseMs * attempt)` backoff.
- `TargetOffDesktop` is raised once when the **first** attempt hits `TargetNotFound`.
- On success, `ModeApplied` → `Acquired` → `Reacquired(attempt)` fire in that order.
- On exhaustion of a **genuine** recovery, `BeginFrame` throws **`DisplayRecoveryFailedException`**; the
  host lets the process exit so its supervisor owns persistent-failure restart (backoff + crash-loop
  breaking). A **wake** escalation instead reschedules and never throws (a sleeping panel isn't fatal).

Total wall-clock recovery is now a little longer than the old in-one-call loop (attempts are paced by
the caller's frame cadence), but a single `BeginFrame` is bounded to one attempt + one backoff, which is
the whole point: the heartbeat never stalls.

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
