# Design: SpecializedDisplay.NET / .Touch / .Y70ti extraction

> Source of truth for the three-library extraction from `D:\bolly\Y70ti_Exclusive_HWMonitor`.
> This document covers all three repos plus the hwmonitor refactor. Implementation agents:
> follow this exactly; every deviation must be flagged to the team lead. The extraction is a
> **behavior-preservation port** of hardware-verified code — regressions are the main risk.

## 0. Ground truth (source files)

- `Y70ti_Exclusive_HWMonitor\renderer\Display\Y70tiBackend.cs` (737 lines) — acquire pipeline, present loop, pacing/throttle/revive, recovery, capture, transform, target correlation.
- `renderer\Display\DisplayInterop.cs` — IDisplayDeviceInterop vtable interop, GENERIC_ALL.
- `renderer\Input\{RawTouchSource,NativeTouch,TouchTypes,TouchGate,TouchSelfTest,TouchInput,GestureRecognizer}.cs`
- `renderer\Program.cs`, `renderer\Render\IRenderBackend.cs`, `renderer\Render\WindowBackend.cs`, `renderer\Render\Png.cs`, `renderer\Ipc\IpcClient.cs`
- `renderer\renderer.csproj` (TFM `net10.0-windows10.0.22621.0`, Vortice `3.8.3` x3, x64, unsafe)
- `provisioning\build-dist.ps1` (per-project `dotnet publish -o dist` — ProjectReference DLLs flow into dist automatically), no CI anywhere.
- `docs\display-findings.md`, `docs\touch-findings.md`, `shared\ipc.md` (hb `owned` field; supervisor 5s frozen threshold).

Confirmed boundary: **GestureRecognizer.cs and TouchInput.cs stay in the app.** GestureRecognizer's tunables are canvas-specific ("canvas is 682 wide, so ~6px ≈ 1mm", bezel-roll slop tuned for this panel's dashboard) and `Gesture`/`GestureKind` are consumed directly by `Dashboard`. `TouchInput` is the app's render-loop pump with the app-specific `Engaged`/grace-window redraw policy. Only `RawTouchSource` + `NativeTouch` + `TouchContact`/`TouchFrame` + `TouchCalibration` + `TouchGate` move. `TouchTypes.cs` must be **split**: `TouchContact`/`TouchFrame`/`TouchCalibration` → lib; `Gesture`/`GestureKind` → stay app-side.

## 1. Repo scaffolding (all three repos)

```
D:\bolly\SpecializedDisplay.NET\
  SpecializedDisplay.sln
  src\SpecializedDisplay\SpecializedDisplay.csproj        (namespace SpecializedDisplay)
  tests\SpecializedDisplay.Tests\SpecializedDisplay.Tests.csproj
  samples\PresentSample\PresentSample.csproj              (spike2-shaped on-hardware smoke tool)
  docs\api-notes.md  docs\resilience.md
  README.md  LICENSE(MIT)  .gitignore

D:\bolly\SpecializedDisplay.Touch\
  SpecializedDisplay.Touch.sln
  src\SpecializedDisplay.Touch\SpecializedDisplay.Touch.csproj
  tests\SpecializedDisplay.Touch.Tests\...
  docs\raw-input-notes.md
  README.md  LICENSE  .gitignore

D:\bolly\SpecializedDisplay.Y70ti\
  SpecializedDisplay.Y70ti.sln
  src\SpecializedDisplay.Y70ti\SpecializedDisplay.Y70ti.csproj
  tests\SpecializedDisplay.Y70ti.Tests\...
  docs\device-notes.md
  README.md  LICENSE  .gitignore
```

`.gitignore` per repo: copy the app's (`**/bin/`, `**/obj/`, `.vs/`, `*.user`, `*.suo`, `Thumbs.db`, `desktop.ini`, `*.log`) minus the provisioning line.

### csproj contents

All three library projects:
```xml
<TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
<Nullable>enable</Nullable> <ImplicitUsings>enable</ImplicitUsings> <LangVersion>latest</LangVersion>
```
- **SpecializedDisplay.csproj**: `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` (DisplayInterop fn-pointers) + PackageReference `Vortice.Direct3D11` 3.8.3, `Vortice.Direct2D1` 3.8.3, `Vortice.DXGI` 3.8.3 (pin exactly — same versions as the app so the app's publish output DLL set is unchanged).
- **SpecializedDisplay.Touch.csproj**: no packages (Win32/HID P/Invoke + `Microsoft.Win32.Registry` is inbox on the windows TFM). Document x64-only support in README (`NativeTouch` struct layouts are hand-verified for x64; `HeaderSize` already adapts via `IntPtr.Size` but nothing else is verified for x86/ARM).
- Build libraries as AnyCPU (don't carry `<Platforms>x64</Platforms>` into the libs — the app stays `PlatformTarget=x64` and that governs the process; README states "supported: Windows x64").
- **SpecializedDisplay.Y70ti.csproj**: ProjectReference to the other two (below).

### Cross-repo consumption: ProjectReference to sibling paths

`SpecializedDisplay.Y70ti` references:
```xml
<ProjectReference Include="..\..\..\SpecializedDisplay.NET\src\SpecializedDisplay\SpecializedDisplay.csproj" />
<ProjectReference Include="..\..\..\SpecializedDisplay.Touch\src\SpecializedDisplay.Touch\SpecializedDisplay.Touch.csproj" />
```
and `renderer.csproj` references all three the same way (it needs core + touch types directly, plus the profile).

Justification:
- The hwmonitor app has **no CI today**; builds happen via `provisioning\build-dist.ps1` → `dotnet publish` per project. ProjectReference publishes lib DLLs into `dist\` automatically — `build-dist.ps1` needs **zero changes**.
- During a behavior-preservation port, lib edits are frequent; a local NuGet feed adds a pack/bump/restore cycle per edit and invites the stale-package trap — the exact failure mode we cannot afford while chasing bit-identical behavior.
- Cost: the four repos must be cloned as siblings under one parent. Document this in every README ("clone layout" section).
- Plan a **later** (post-stabilization, out of this task's critical path) switch to nuget.org packages, tagging `v0.1.0` at extraction time.

## 2. SpecializedDisplay.NET (core) — file inventory and public API

### File inventory (src/SpecializedDisplay/)

| New file | Contents / provenance |
|---|---|
| `SpecializedDisplays.cs` | Static entry: enumerate + find. From `Y70tiBackend.FindY70tiKey`/`FindTarget` (lines 681–718), generalized. |
| `DisplaySelector.cs` | Two-phase selector type + descriptor records. |
| `ExclusiveDisplaySession.cs` | THE port. `Y70tiBackend` minus IPC/Layout/PNG/IRenderBackend: acquire sequence, present loop, pacing, background throttle, dead-fence revive, recovery, bounded reacquire, force-redraw, D2D wrapping, plus NEW Release/Acquire. |
| `SessionOptions.cs` | `AcquireOptions` + all named constants with current values as defaults. |
| `SessionEvents.cs` | Event-arg records, `DisplayLossKind`. |
| `DisplayTransform.cs` | `ComputeTransform` (lines 466–475) generalized to physical W/H; logical-size math. Public (touch baseline needs it). |
| `FrameCapture.cs` | Readback + inverse-transform remap from `CaptureIfPending` (lines 216–278), minus PNG writing — produces raw upright BGRA. |
| `DisplayInterop.cs` | **Verbatim** from `renderer\Display\DisplayInterop.cs`, only the namespace changes (stays `internal`). |
| `Logging.cs` | `enum LogLevel { Info, Warn, Error }` — the log abstraction is just `Action<LogLevel,string>?`. |
| `Exceptions.cs` | `TargetNotFoundException` (public now — it's private today), `ModeNotFoundException`, `AcquireFailedException`, `DisplayRecoveryFailedException`. |

### Public API surface (signatures)

```csharp
namespace SpecializedDisplay;

public enum LogLevel { Info, Warn, Error }

// ---- selection: two-phase, mirroring FindY70tiKey/FindTarget exactly ----
public sealed record DisplayMonitorInfo(
    string DeviceId, string DisplayName,
    long AdapterIdHighPart, uint AdapterIdLowPart, uint AdapterTargetId,
    int NativeWidth, int NativeHeight);

public sealed record DisplayTargetInfo(
    string? StableMonitorId,
    long AdapterIdHighPart, uint AdapterIdLowPart, uint AdapterRelativeId,
    bool IsConnected);

public sealed class DisplaySelector
{
    // Phase 1: DisplayMonitor correlation (DeviceId/DisplayName). Last match wins (risk register #1).
    public Func<DisplayMonitorInfo, bool>? MatchMonitor { get; init; }
    // Phase 2 fallback: DisplayTarget properties (StableMonitorId).
    public Func<DisplayTargetInfo, bool>? MatchTarget  { get; init; }
}

public static class SpecializedDisplays
{
    public static IReadOnlyList<DisplayMonitorInfo> EnumerateMonitors();   // diagnostics
    public static SpecializedDisplayTarget? Find(DisplaySelector selector);    // null if absent now
    public static SpecializedDisplayTarget Require(DisplaySelector selector); // throws TargetNotFoundException
}

// A handle that remembers HOW to find the target (the selector), not a live WinRT object —
// required because every (re)acquire recreates DisplayManager and must re-resolve the target.
public sealed class SpecializedDisplayTarget
{
    public DisplaySelector Selector { get; }
    public DisplayMonitorInfo? LastKnownMonitor { get; }   // as of Find() time; informational
    public ExclusiveDisplaySession CreateSession(AcquireOptions options); // does NOT acquire
    public ExclusiveDisplaySession AcquireExclusive(AcquireOptions options); // CreateSession + Acquire (convenience)
}

// ---- mode selection ----
public sealed record DisplayModeDescriptor(
    int SourceWidth, int SourceHeight, int TargetWidth, int TargetHeight,
    Windows.Graphics.DirectX.DirectXPixelFormat SourcePixelFormat,
    double VerticalRefreshHz);

public static class ModeSelectors
{
    // 1:1 src=tgt at exactly w×h with the given format — the current filter, named.
    public static Func<DisplayModeDescriptor, bool> NativeOneToOne(
        int width, int height, Windows.Graphics.DirectX.DirectXPixelFormat format);
}

// ---- options: every constant from Y70tiBackend, name-for-name, current value as default ----
public sealed class AcquireOptions
{
    public required Func<DisplayModeDescriptor, bool> ModeSelector { get; init; }

    public int  Rotation { get; init; } = 0;              // 0 | 90 | 180 | 270 (app passes 90/270)
    public bool FlipX { get; init; }
    public bool FlipY { get; init; }
    public int  BufferCount { get; init; } = 2;           // Y70tiBackend.BufferCount
    public double RefreshHz { get; init; } = 60.0;        // RefreshHz (pacing target; TargetFrameMs derived)

    // Background/disconnect throttle (lines 95–103)
    public double InstantWaitFraction { get; init; } = 0.5;   // InstantWaitMs = TargetFrameMs * this
    public int    BackgroundEnterFrames { get; init; } = 8;
    public int    BackgroundSleepMs { get; init; } = 200;

    // Dead-fence revive (lines 112–114; hardware-verified 2026-07-04)
    public double FenceReviveIntervalMs { get; init; } = 5000;

    // Fence waits (lines 177, 335)
    public int RenderFenceTimeoutMs { get; init; } = 500;
    public int VBlankFenceTimeoutMs { get; init; } = 500;

    // Acquire inner loop (Acquire(), lines 531–569): TryAcquire/TryApply retry
    public int AcquireAttempts { get; init; } = 6;
    public int AcquireRetryBaseMs { get; init; } = 150;    // Sleep(150 * attempt)

    // Bounded reacquire (lines 484, 505, 512): total < ~3s so a 5s watchdog never fires
    public int ReacquireAttempts { get; init; } = 6;       // MaxReacquireAttempts
    public int ReacquireBackoffBaseMs { get; init; } = 150;
    public int ReacquireBackoffMaxMs { get; init; } = 800; // Min(800, 150*attempt)

    public Action<LogLevel, string>? Log { get; init; }    // lib never assumes IpcClient
}

// ---- the session ----
public enum DisplayLossKind { DeviceRemoved, OwnershipLost }

public sealed record AppliedMode(int Width, int Height,
    Windows.Graphics.DirectX.DirectXPixelFormat Format, double RefreshHz);
public sealed record CapturedFrame(int Width, int Height, byte[] Bgra); // upright logical orientation, opaque alpha

public sealed class ExclusiveDisplaySession : IDisposable
{
    // -- state --
    public bool IsOwned { get; }          // false while lost-and-pending OR released
    public bool IsReleased { get; }       // cooperative-release latch
    public System.Drawing.Size PhysicalSize { get; }  // from applied mode (2560x682)
    public System.Drawing.Size LogicalSize  { get; }  // rotated (682x2560 at rot 90/270)
    public System.Numerics.Matrix3x2 LogicalTransform { get; } // the display transform (touch reconciliation source)
    public AppliedMode? Mode { get; }

    // -- lifecycle --
    public void Acquire();     // initial + after Release(); throws TargetNotFound/ModeNotFound/AcquireFailed
    public void Release();     // NEW: voluntary — dispose GPU/display state, suppress auto-reacquire, IsOwned=false
    public void Dispose();     // ReleaseGraphics, no events

    // -- frame loop (single-threaded; call only on the render thread) --
    public Vortice.Direct2D1.ID2D1DeviceContext Dc { get; }
    public void BeginFrame();          // runs pending bounded-reacquire first (may throw DisplayRecoveryFailedException)
    public void EndFramePresent();
    public bool ConsumeForceRedraw();  // BufferCount countdown after every (re)acquire
    public void RequestCapture(Action<CapturedFrame> onCaptured); // one-shot; failures logged+swallowed

    // -- typed events (replace the app's direct IPC sends; app forwards) --
    public event Action? Acquired;                            // after EVERY successful acquire (initial, explicit, re-)
    public event Action<AppliedMode>? ModeApplied;            // fires BEFORE Acquired (preserves IPC order)
    public event Action<string>? OwnershipLost;               // detail, e.g. "present: SourceInvalid" / "acquire: TargetAccessDenied"
    public event Action<string>? DeviceRemoved;               // detail, e.g. "DeviceRemovedReason=0x887A0005"
    public event Action<string>? TargetOffDesktop;            // first TargetNotFound during reacquire
    public event Action<int>? Reacquired;                     // attempt count; fires AFTER ModeApplied+Acquired
    public event Action? Released;                            // NEW: cooperative release completed
}

public static class DisplayTransform
{
    // Exact generalization of Y70tiBackend.ComputeTransform (D2D row-vector p' = p*M; flip-then-rotate).
    public static System.Numerics.Matrix3x2 Compute(int rotation, bool flipX, bool flipY,
        int physicalWidth, int physicalHeight);
    public static (int W, int H) LogicalSize(int rotation, int physicalWidth, int physicalHeight);
}
```

**Locked API decisions:**

1. **`CreateSession` + explicit `Acquire()` is the primary path**, with `AcquireExclusive` as sugar. Today the ctor acquires and IPC events flow immediately; a consumer that subscribes after `AcquireExclusive` returns would *miss the initial `mode_applied`/`acquired`*. The hwmonitor adapter must use `CreateSession` → subscribe → `Acquire()` to preserve event timing exactly.
2. **Event detail strings are generated in the lib and forwarded verbatim by the app**, *except* `mode_applied`: today the detail is the literal `"2560x682 BGRA @60"`; the lib event carries structured `AppliedMode` and the **app formats the literal** so the supervisor-visible IPC line is byte-identical.
3. **Release semantics**: `Release()` = `ReleaseGraphics()` + `_released=true` + clear `_needReacquire` + raise `Released`. While released, `BeginFrame`/`EndFramePresent` throw `InvalidOperationException`. `Acquire()` clears `_released` and runs the standard acquire (throwing, single logical attempt with the existing inner 6-attempt TryAcquire/TryApply loop). No arbitration/named-pipe logic anywhere in core. Sessions are single-threaded; callers must marshal Release/Acquire onto the render thread — document in README.
4. **Frame API stays `BeginFrame`/`EndFramePresent`** (no IDisposable Frame wrapper for v0) — maps 1:1 onto current code and `IRenderBackend`, minimizing the port diff.
5. **Capture** takes a raw-pixels callback; PNG writing stays app-side. The lib keeps the swallow-all guard *around both readback and the callback*.
6. **Generalizations** (the only intentional behavior deltas, all parameter-driven): physical 2560×682 literals → applied-mode dims (`_fullDirty`, capture, transform); `Layout.CanvasW/H` → `LogicalSize`; rot 0/180 added to `DisplayTransform` (new code, but the app only ever passes 90/270 so its matrices are untouched — guarded by unit tests asserting the exact matrix literals from lines 469–473).

### Core code-movement map (Y70tiBackend.cs → core lib)

| Source region (Y70tiBackend.cs) | Destination |
|---|---|
| L44–47 HRESULT consts | `ExclusiveDisplaySession` (private) |
| L51 `BufferCount` | `AcquireOptions.BufferCount` default |
| L53–57 rot/flip/transform/_fullDirty fields | session fields; `_fullDirty` built from mode dims |
| L59–130 device/fence/pacing/state fields | session fields, unchanged names |
| L95–114 pacing + revive constants | `AcquireOptions` defaults |
| L132–140 ctor | split: field init in `CreateSession`; `Acquire()` explicit |
| L142–153 `Dc`/`PixelSize`/`IsOwned`/`ConsumeForceRedraw`/`RequestCapture` | session (`PixelSize`→`PhysicalSize`; `RequestCapture` becomes callback form) |
| L155–204 `BeginFrame`/`EndFramePresent` | session, verbatim except `CaptureIfPending` call → `FrameCapture` |
| L207 `PumpEvents` | **stays app-side** (adapter returns `true`) |
| L216–278 `CaptureIfPending` | `FrameCapture.cs` (raw BGRA out; `Png.Write` + `Directory.CreateDirectory` + "capture written" log move to the app adapter) |
| L282–408 `PaceToVBlank`/`WaitForVBlankOnce`/`DegradeFencePacing`/`TryReviveFencePacing`/`UpdateBackgroundState` | session, verbatim (constants read from options) |
| L410–460 `EvaluatePresent`/`CheckDeviceRemoved`/`HandlePresentException`/`TriggerRecovery` | session; `_ipc?.SendEvent(kind, …)` → raise `DeviceRemoved`/`OwnershipLost` |
| L466–475 `ComputeTransform` | `DisplayTransform.Compute` (public, parameterized) |
| L484–518 `Reacquire` + backoff | session (private, invoked from `BeginFrame`); `RecoveryFailedException` → public `DisplayRecoveryFailedException` |
| L520–521 exception types | `Exceptions.cs` (public) |
| L523–631 `Acquire` | session `Acquire()`; mode filter → `options.ModeSelector`; events raised in the same order/places |
| L633–651 `SetupPeriodicFence` | session, verbatim |
| L653–675 `ReleaseGraphics` | session, verbatim; reused by `Release()` and `Dispose()` |
| L681–718 `FindY70tiKey`/`FindTarget` | `SpecializedDisplays` + session re-resolve, predicates from `DisplaySelector` |
| L720–735 `CreateD3D11OnAdapter` | session (private static), verbatim |
| `renderer\Display\DisplayInterop.cs` (whole file) | `src\SpecializedDisplay\DisplayInterop.cs`, namespace only |

**Port comments too — they encode hardware findings.**

### Core tests (tests/SpecializedDisplay.Tests, xunit, no hardware)

- `DisplayTransform.Compute(90,f,f,2560,682)` equals the literal `Matrix3x2(0,1,-1,0,2560,0)`; rot270 equals `(0,-1,1,0,0,682)`; flip compositions equal the current hand-built matrices; `LogicalSize` swap rules.
- Capture remap round-trip: for a synthetic physical buffer, forward-transform a logical pixel with `Compute`, run the inverse capture remap, assert identity (both rot 90 and 270, with flips).
- `AcquireOptions` default-values test (one assert per constant — a tripwire against accidental default drift).
- `ModeSelectors.NativeOneToOne` accepts/rejects documented mode shapes (incl. rejecting scaled src<tgt modes per display-findings.md).
- Hardware-dependent behavior is covered by `samples/PresentSample` (port of `spikes\spike2-present\Program.cs` onto the new API: find → acquire → clear-color frames for N seconds → **Release → wait → Acquire → render again** → exit). Doubles as the cooperative-release on-hardware proof.

### Docs migration (core)

- `docs/api-notes.md`: from `display-findings.md` — generic sections: EnforceSourceOwnership+EmptyState gotcha, IDisplayDeviceInterop IID/slot-3/GENERIC_ALL, `DisplayScanout`/`DisplayTask` not-IDisposable, `uint` args, adapter-LUID device creation, 1:1 mode guidance.
- `docs/resilience.md`: background-throttle + dead-fence-revive design (from the L92–114 comments + this port), bounded-reacquire watchdog contract.
- Y70ti-specific lines (RTK409A/LUID/BenQ warning/orientation lock) go to the Y70ti repo. The app **keeps** `docs/display-findings.md` unchanged (historical record) with a pointer header.

## 3. SpecializedDisplay.Touch — file inventory and public API

### File inventory (src/SpecializedDisplay.Touch/)

| New file | Provenance |
|---|---|
| `RawTouchSource.cs` | from `renderer\Input\RawTouchSource.cs`; VID/PID → `TouchDeviceFilter` param; `IpcClient?` → `Action<LogLevel,string>?`; thread name → `"SpecializedDisplay-touch-input"`, window class → `"SpDispTouchInput_" + pid` (diagnostic-only strings; safe) |
| `NativeTouch.cs` | verbatim, namespace change, stays `internal` |
| `TouchTypes.cs` | `TouchContact`, `TouchFrame` only (record structs, moved as-is) |
| `TouchCalibration.cs` | parameterized model (below) |
| `TouchGate.cs` | logger param instead of `IpcClient` |
| `Logging.cs` | same `LogLevel` shape as core (duplicated tiny enum — keeps Touch dependency-free; do NOT reference core from Touch) |

### Public API

```csharp
namespace SpecializedDisplay.Touch;

public readonly record struct TouchContact(int Id, float X, float Y, int RawX, int RawY);
public readonly record struct TouchFrame(long TimestampMs, TouchContact[] Contacts);

public sealed class TouchDeviceFilter
{
    // Matches the raw-input device path; default impl reproduces today's
    // name.Contains("vid_222a") && name.Contains("pid_0001") check.
    public static TouchDeviceFilter ByVidPid(ushort vid, ushort pid);
    public Func<string /*devicePath*/, bool> Matches { get; init; }
    // The digitizer-TLC caps check (UsagePage 0x0D / Usage 0x04) is ALWAYS applied in addition.
}

public sealed class TouchCalibrationModel
{
    public required int RawXMin, RawXMax, RawYMin, RawYMax;   // measured active-glass bounds
    public required bool HorizontalFromRawY;   // Y70ti: true  (physical horizontal = raw Y)
    public required bool InvertHorizontal;     // Y70ti: false
    public required bool InvertVertical;       // Y70ti: true  (raw X top≈max → bottom≈min)
}

public sealed class TouchCalibration
{
    // displayTransform: the ACTUAL matrix the display session is using (session.LogicalTransform).
    // baselineTransform: the display transform under which the corner calibration was CAPTURED.
    // delta = baseline * inverse(display) — the structural replacement for the hand-synced copy.
    public TouchCalibration(TouchCalibrationModel model,
        float canvasWidth, float canvasHeight,
        System.Numerics.Matrix3x2 baselineTransform,
        System.Numerics.Matrix3x2 displayTransform);
    public void Map(int rawX, int rawY, out float logicalX, out float logicalY);
}

public sealed class RawTouchSource : IDisposable
{
    public RawTouchSource(TouchDeviceFilter filter, TouchCalibration calibration,
                          Action<LogLevel, string>? log = null);
    public bool Bound { get; }
    public void Start();
    public bool TryDequeue(out TouchFrame frame);
    public void Dispose();
}

public static class TouchGate
{
    public static int? Read();
    public static void EnsureDisabled(Action<LogLevel, string>? log = null);
}
```

**Structural sync of the rotation-reconciliation delta** (replacing the "keep in sync by hand" note at TouchTypes.cs L49–50): `TouchCalibration` no longer contains its own copy of the display matrix. The caller passes `session.LogicalTransform` (or `DisplayTransform.Compute(...)` pre-acquire) as `displayTransform`, and the Y70ti profile supplies the rot-90 baseline computed by the **same** core function. Touch stays dependency-free (takes plain `Matrix3x2`), so touch-without-display consumers work.

**Deliberately NOT moved**: `GestureRecognizer.cs`, `TouchInput.cs`, `Gesture`/`GestureKind` (app), `TouchSelfTest.cs` (stays app-side; it now exercises lib code through the profile; corner assertions duplicated as xunit tests in the Touch repo).

### Touch tests
- Calibration corners (the 4 measured corner-holds from `TouchSelfTest.Run` L20–23, expected within 40px).
- rot270 reconciliation = exact 180° of rot90 (L26–28 equivalents).
- Clamp order preserved: raw clamped to [0,1] **before** transform, result clamped to canvas after (matches `Map` L69–75).
- `TouchDeviceFilter.ByVidPid(0x222A,1)` matches the documented ILITEK path string from touch-findings.md and rejects near-misses.

### Docs migration (touch)
- `docs/raw-input-notes.md`: from `touch-findings.md` — capture-method section (ERROR_SHARING_VIOLATION, RIDEV_INPUTSINK, preparsed-per-handle re-bind), TouchGate mechanism/rationale (generic part).
- ILITEK device identity + corner table → Y70ti repo. App keeps `touch-findings.md` with a pointer header.

## 4. SpecializedDisplay.Y70ti (profile) — inventory and API

Files: `Y70tiDisplay.cs`, `Y70tiTouch.cs` (~200 lines total), `docs/device-notes.md`.

```csharp
namespace SpecializedDisplay.Y70ti;

public static class Y70tiDisplay
{
    public const int PhysicalWidth = 2560, PhysicalHeight = 682;
    public const double NativeRefreshHz = 60.0;
    public const int DefaultRotation = 90;   // locked on-panel 2026-07-03

    // DeviceId contains "RTK409A" OR DisplayName contains "HYTE"/"Y70"; fallback StableMonitorId contains "RTK409A".
    public static DisplaySelector Selector { get; }
    public static Func<DisplayModeDescriptor, bool> NativeMode { get; } // 1:1 2560x682 B8G8R8A8UIntNormalized

    public static AcquireOptions CreateOptions(int rotation = DefaultRotation,
        bool flipX = false, bool flipY = false, Action<LogLevel,string>? log = null);
        // = new AcquireOptions { ModeSelector = NativeMode, Rotation = …, Log = … } — resilience knobs at defaults

    public static SpecializedDisplayTarget? Find();       // SpecializedDisplays.Find(Selector)
    public static SpecializedDisplayTarget Require();
}

public static class Y70tiTouch
{
    public const ushort Vid = 0x222A; public const ushort Pid = 0x0001;
    // Measured active-glass bounds (docs/touch-findings.md corner-holds)
    public static TouchCalibrationModel CalibrationModel { get; } // 253/9464/245/9499, HorizontalFromRawY, InvertVertical

    public static TouchCalibration CreateCalibration(int rotation = 90, bool flipX = false, bool flipY = false);
        // baseline = DisplayTransform.Compute(90,false,false,2560,682); display = DisplayTransform.Compute(rot,fx,fy,2560,682)
    public static RawTouchSource CreateTouchSource(TouchCalibration cal, Action<LogLevel,string>? log = null);
        // = new RawTouchSource(TouchDeviceFilter.ByVidPid(Vid,Pid), cal, log)
}
```

Profile tests: selector accepts fake `DisplayMonitorInfo("...RTK409A...", …)`, `("…","HYTE Y70ti",…)`, `("…","…Y70…")`; rejects `("…","XL2586X+",…)` (the BenQ); `MatchTarget` accepts StableMonitorId `RTK409AW09C23CM024L_...`; calibration corners through `CreateCalibration()`.

`docs/device-notes.md`: Y70ti sections of both findings docs — LUID/relId/StableMonitorId, BenQ do-not-acquire warning, mode list, orientation-locked note, ILITEK identity + corner table + TouchGate history.

## 5. hwmonitor refactor (file-by-file)

After all three libs compile and tests pass, on a branch, one commit per file group.

1. **`renderer\renderer.csproj`**: add three ProjectReferences. Keep the Vortice PackageReferences (WindowBackend/Dashboard use them directly).
2. **`renderer\Display\Y70tiBackend.cs`** — REPLACE contents with the thin adapter (keep file path and class name `Y70tiBackend : IRenderBackend`):
   - ctor `(int rotation, bool flipX, bool flipY, IpcClient? ipc, bool startReleased = false)`:
     - `var log = IpcLog.Sink(ipc);`
     - `var target = SpecializedDisplays.Require(Y70tiDisplay.Selector);` (throws → Program's existing "backend init failed" path, exit 1)
     - `_session = target.CreateSession(Y70tiDisplay.CreateOptions(rotation, flipX, flipY, log));`
     - subscribe events → IPC **before** acquiring:
       - `ModeApplied` → `ipc?.SendEvent("mode_applied", "2560x682 BGRA @60")` (literal preserved)
       - `Acquired` → `"acquired"`; `Reacquired` → `"reacquired"`; `OwnershipLost` → `("ownership_lost", detail)`; `DeviceRemoved` → `("device_removed", detail)`; `TargetOffDesktop` → `("target_off_desktop", "Y70ti target not present")` (app supplies the literal)
     - `if (!startReleased) _session.Acquire();`  // startReleased = shell-connected deferred acquire (Y70Deck design)
   - `Dc`/`PixelSize`/`IsOwned`/`ConsumeForceRedraw`/`BeginFrame`/`EndFramePresent`/`Dispose` → 1-line delegations; `PumpEvents() => true`; NEW `Release()`/`Acquire()` delegations (IRenderBackend gains these; WindowBackend no-ops).
   - `RequestCapture(string outPath)` → `_session.RequestCapture(f => { Directory.CreateDirectory(Path.GetDirectoryName(outPath)!); Png.Write(outPath, f.Width, f.Height, f.Bgra); ipc?.Log("info", $"capture written {outPath}"); });`
3. **DELETE `renderer\Display\DisplayInterop.cs`** (now in core).
4. **Add `IpcLog.cs`**: `static Action<LogLevel,string> Sink(IpcClient? ipc) => (l,m) => ipc?.Log(l switch { LogLevel.Info=>"info", LogLevel.Warn=>"warn", _=>"error" }, m);`
5. **DELETE `renderer\Input\RawTouchSource.cs`, `NativeTouch.cs`, `TouchGate.cs`.**
6. **`renderer\Input\TouchTypes.cs`** → shrink to `Gesture` + `GestureKind` only. `TouchContact`/`TouchFrame`/`TouchCalibration` come from `SpecializedDisplay.Touch` (add `using`; `GestureRecognizer`/`TouchInput` signatures unchanged — record shapes identical).
7. **`renderer\Input\TouchInput.cs`**: ctor takes the lib `TouchCalibration`; `new RawTouchSource(calibration, ipc)` → `Y70tiTouch.CreateTouchSource(calibration, IpcLog.Sink(ipc))`. Everything else untouched.
8. **`renderer\Input\TouchSelfTest.cs`**: `new TouchCalibration()` / `new TouchCalibration(270)` → `Y70tiTouch.CreateCalibration()` / `CreateCalibration(270)`. Output must be identical.
9. **`renderer\Program.cs`**: `new TouchCalibration(rot, flipX, flipY)` (L85) → `Y70tiTouch.CreateCalibration(rot, flipX, flipY)`; `TouchGate.EnsureDisabled(ipc)` (L88) → lib TouchGate + sink. Everything else unchanged (render-loop catch at L216–225 already handles the recovery exception generically → exit 2; verify `DisplayRecoveryFailedException` propagates out of `BeginFrame` exactly as `RecoveryFailedException` does today).
10. **Untouched**: `IRenderBackend.cs` (except Release/Acquire addition), `WindowBackend.cs` (except no-op Release/Acquire), `Dashboard*`, `Layout.cs`, `Png.cs`, `OffscreenShots.cs`, `GestureRecognizer.cs`, `Ipc\` (except sink helper), `Settings\`, `supervisor\`, `sensord\`, `provisioning\` (build-dist works as-is).
11. **Docs**: pointer headers on `docs\display-findings.md` / `docs\touch-findings.md`; extraction + sibling-clone note in app README.

## 6. Behavior-change risk register (all 17 — every place regressions can sneak in, and the guard)

| # | Risk | Guard |
|---|---|---|
| 1 | **Selector semantics**: `FindY70tiKey` takes the **last** matching monitor (no `break`, L692–697), swallows all enumeration exceptions → key=null, then `FindTarget` falls back to StableMonitorId. A "cleaner" first-match/throwing rewrite changes device selection. | Port loop verbatim; unit test with two matching fakes asserting last-wins; keep the try/catch→null fallback. |
| 2 | **Acquire loop control flow** (L531–569): TargetAccessDenied → `ownership_lost` event **per attempt** + `Sleep(150*attempt)` + retry; mode-not-found and target-not-found **throw immediately** (no retry); TryApply failure loops with target re-find. | Line-by-line port; side-by-side review; event ordering asserted in PresentSample logs. |
| 3 | **Event order**: `mode_applied` → `acquired` (→ `reacquired` after recovery); `target_off_desktop` only on first reacquire attempt. Missed initial events if subscription happens post-acquire. | Two-step `CreateSession`+`Acquire()` in the adapter; on-panel log-sequence check. |
| 4 | **Pacing math**: `InstantWaitMs = TargetFrameMs*0.5`, saturating `_instantWaits`, deficit sleep gated `>0.5 && <= TargetFrameMs` with `Ceiling`, `_lastPaceEndMs` updated after sleeps, revive gated on `(_fenceDied || _background) && _reviveAtMs > 0`, `_reviveAtMs=0` reset only when `!_fenceDied`. | Verbatim port (constants via options); options-defaults unit test; power-off/disconnect drills. |
| 5 | **Dead-fence path**: sentinel `CompletedValue == ulong.MaxValue`; `_fenceDied → return` in `WaitForVBlankOnce` (must NEVER fall into `WaitForVBlank` — unbounded block starves the heartbeat and the watchdog kills the process); quiet-mode revive logging. | Verbatim; comment preserved; power-off drill. |
| 6 | **Recovery classification**: present-status switch (DeviceInvalid vs SourceInvalid/SourceStatusPreventedPresent vs log-and-continue), HRESULT map, `HandlePresentException` default → `ownership_lost`, `TriggerRecovery` double-fire guard. | Verbatim port; ownership-loss drill. |
| 7 | **Bounded reacquire ≤ ~3s** vs supervisor 5s freeze: 6 attempts, `Sleep(Min(800,150*attempt))`, exhaustion throws out of `BeginFrame`. | Options defaults + defaults test; app catch path unchanged (exit 2). |
| 8 | **Force-redraw contract**: `_forceRedraw = BufferCount` on every acquire; pacing/fence state reset block (L628–629) must run on every acquire including cooperative re-acquire. | Reset block lives in `Acquire()`, not the ctor; PresentSample renders post-Release-Acquire without stale-buffer garbage. |
| 9 | **Transform/capture generalization** (2560/682/`Layout` literals → parameters): a sign or offset slip flips the panel or corrupts captures. | Unit tests assert exact matrix literals for rot90/270±flips; capture round-trip test; on-panel eyeball + `--capture` diff. |
| 10 | **GENERIC_ALL / interop**: any "tidying" of DisplayInterop breaks with E_INVALIDARG. | Move verbatim; keep warning comments. |
| 11 | **Capture side effects**: fence-before-readback ordering, swallow-all guard now also wrapping the app callback, "capture written" logged by adapter (plus RunLoop's existing confirm line — two lines, same as today). | Adapter code per §5.2; on-panel capture check. |
| 12 | **Touch decode**: idle empty-frame coalescing (`_lastFrameEmpty`), preparsed-per-handle cache + `WM_INPUT_DEVICE_CHANGE` eviction, STA background thread + 100ms MsgWait pump, caps-based TLC confirmation in addition to VID/PID. | Verbatim port; on-panel touch check + replug drill. |
| 13 | **Calibration**: clamp01-before-transform-then-canvas-clamp order; delta = `M90 * inverse(M_display)`; baseline now computed by core's `DisplayTransform` (structural sync) — must equal the old hand-copied matrix. | Touch tests (corners + rot270=180°); `--selftest-touch` byte-identical output. |
| 14 | **IPC surface**: event `kind` strings, hb `owned` semantics, `mode_applied` detail literal, `target_off_desktop` detail literal — all owned by the **app adapter**, not the lib. | Adapter forwards fixed literals; grep supervisor `supervise.go`/`ipc.go` kinds against adapter strings before merge. |
| 15 | **Log-line drift**: lib log text loses "Y70ti" literals (e.g. "re-acquired display target after n attempt(s)"). Free-form logs only — supervisor parses events, not logs. | Accept; note in app changelog. |
| 16 | **Package/TFM drift**: different Vortice versions between lib and app → duplicate assemblies in dist. | Pin 3.8.3 everywhere; dist inspection during verify. |
| 17 | **Threading contract of Release/Acquire** (new): calling off the render thread corrupts session state. | Documented contract; debug-only thread-affinity assert in `Release`/`Acquire`/`BeginFrame` (`Debug.Assert(_threadId == Environment.CurrentManagedThreadId)`). |
