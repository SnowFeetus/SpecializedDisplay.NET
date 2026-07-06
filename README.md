# SpecializedDisplay.NET

Take **exclusive** ownership of a specialized display target (a panel that is not part of the Windows
desktop) and present hardware-paced, tear-free frames to it from C#, using
**Windows.Devices.Display.Core** (DisplayCore) + Direct3D 11 + Direct2D via Vortice.

This is the device-agnostic core: it finds a target by a pluggable selector, applies a native 1:1
mode, wraps the shared scanout primaries as Direct2D targets, and runs a resilient present loop with
fence-based V-blank pacing, background/power-off throttling, and bounded in-process recovery. Device
identity and per-panel defaults live in a separate profile package (e.g. `SpecializedDisplay.Y70ti`);
raw-input/touch lives in `SpecializedDisplay.Touch`.

Extracted as a behavior-preserving port of hardware-verified renderer code.

## Supported platform

**Windows x64.** The target framework is `net10.0-windows10.0.22621.0`. The library builds AnyCPU;
the hosting process governs the actual architecture and should be x64.

## Quick start

```csharp
using SpecializedDisplay;
using Windows.Graphics.DirectX;

// 1. Describe how to find the target (or use a profile package's ready-made selector).
var selector = new DisplaySelector
{
    MatchMonitor = m => m.DeviceId.Contains("RTK409A", StringComparison.OrdinalIgnoreCase)
                     || m.DisplayName.Contains("HYTE", StringComparison.OrdinalIgnoreCase),
    MatchTarget  = t => t.StableMonitorId is { } s && s.Contains("RTK409A", StringComparison.OrdinalIgnoreCase),
};

var options = new AcquireOptions
{
    ModeSelector = ModeSelectors.NativeOneToOne(2560, 682, DirectXPixelFormat.B8G8R8A8UIntNormalized),
    Rotation = 90,
    Log = (level, msg) => Console.WriteLine($"[{level}] {msg}"),
};

// 2. Find it (null if not currently connected; Require() throws instead).
var target = SpecializedDisplays.Find(selector);
if (target is null) return;

// 3. Create the session, subscribe, THEN acquire (so you never miss the initial events).
using var session = target.CreateSession(options);
session.ModeApplied += m => { /* forward to your supervisor as needed */ };
session.Acquired    += () => { /* ... */ };
session.Acquire();

// 4. Render loop on ONE thread.
while (running)
{
    session.BeginFrame();
    var dc = session.Dc;          // ID2D1DeviceContext, already BeginDraw'd with the display transform
    // ... draw into dc in LOGICAL (session.LogicalSize) coordinates ...
    session.EndFramePresent();    // fence, scanout, V-blank pace
}
```

`AcquireExclusive(options)` is sugar for `CreateSession + Acquire`, but a consumer that subscribes
*after* it returns will miss the initial `ModeApplied`/`Acquired`. Prefer `CreateSession` → subscribe
→ `Acquire()`.

## Single-threaded session contract

A session is **single-threaded**. All lifecycle calls (`Acquire`, `Release`) and all frame calls
(`BeginFrame`, `EndFramePresent`, `ConsumeForceRedraw`, `RequestCapture`) must run on the **one render
thread**. The render thread is established as whoever first calls `Acquire()`. If you construct the
session on a setup thread and render on another, marshal `Acquire`/`Release` onto the render thread.
Debug builds assert this affinity.

`Release()` cooperatively gives up the panel (frees GPU/display state, stops auto-reacquire) without
disposing the session; a later `Acquire()` reclaims it. See `docs/resilience.md`.

## Resilience & recovery

The present loop survives backgrounding, display power-off, source loss, and device removal — capping
the frame rate, reviving a dead pacing fence, and performing a **bounded** in-process reacquire before
throwing `DisplayRecoveryFailedException` out of `BeginFrame` (so a supervisor can restart the host).
Full design in [`docs/resilience.md`](docs/resilience.md); DisplayCore mechanics in
[`docs/api-notes.md`](docs/api-notes.md).

## Clone layout (sibling repos)

The related packages consume each other by **`ProjectReference` to sibling paths** during
stabilization (a NuGet feed comes later, tagged at extraction). You must therefore clone all repos as
**siblings under one parent directory**:

```
<parent>\
  SpecializedDisplay.NET\      (this repo — the core)
  SpecializedDisplay.Touch\    (raw-input / touch)
  SpecializedDisplay.Y70ti\    (device profile; ProjectReferences the two above)
  Y70ti_Exclusive_HWMonitor\   (the consuming app)
```

The relative paths in the downstream `.csproj` files assume exactly this arrangement
(`..\..\..\SpecializedDisplay.NET\src\SpecializedDisplay\SpecializedDisplay.csproj`). This core repo
has no such references itself, but downstream repos will fail to restore if the siblings are missing.

## Build & test

```
dotnet build SpecializedDisplay.sln -c Release
dotnet test  SpecializedDisplay.sln -c Release
```

Tests are hardware-free (transform matrices, capture-remap round-trip, options-defaults tripwire,
mode-selector accept/reject). Hardware behavior — including the cooperative Release/Acquire cycle — is
exercised by `samples/PresentSample`, which takes over the panel and must be run **on the device**
(not in CI, and not while another client owns the panel).

## License

MIT © 2026 SnowFeetus. See [LICENSE](LICENSE).
