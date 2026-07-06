# DisplayCore API notes

Hard-won findings from bringing a specialized display up on **Windows.Devices.Display.Core** from C#
(Vortice + CsWinRT), non-elevated. These are the generic mechanics the library depends on; device
identity (LUIDs, monitor names, mode lists) lives with the device profile, not here.

## Acquire: `EnforceSourceOwnership` + empty state (NOT `ReadCurrentState`)

```csharp
var mgr = DisplayManager.Create(DisplayManagerOptions.EnforceSourceOwnership); // NOT None
var acq = mgr.TryAcquireTargetsAndCreateEmptyState(new[] { target });         // NOT ReadCurrentState
// acq.ErrorCode == DisplayManagerResult.Success
var state = acq.State;
var path  = state.ConnectTarget(target);   // requires EnforceSourceOwnership + EmptyState
var modes = path.FindModes(DisplayModeQueryOptions.None);
```

- **Gotcha (proven):** `DisplayManagerOptions.None` + `ReadCurrentState` + `ConnectTarget` →
  `E_ACCESSDENIED`. You must use `EnforceSourceOwnership` + `CreateEmptyState` to build a source path.
- Acquire is **non-elevated** — no admin needed.
- `DisplayManagerResult.TargetAccessDenied` means another client (usually the desktop compositor)
  holds the source — the `VIDPN_SOURCE_IN_USE` condition. Back off and retry; do not treat it as fatal.

## `IDisplayDeviceInterop` — QI by hand, vtable slot 3, `GENERIC_ALL`

`IDisplayDeviceInterop` is **not projected** by CsWinRT. It derives from `IUnknown` (NOT
`IInspectable`), so `CreateSharedHandle`/`OpenSharedHandle` sit at vtable slots **3** and **4**.

- QI via `WinRT.MarshalInspectable<T>.FromManaged` + `Marshal.QueryInterface`, IID
  `64338358-366A-471B-BD56-DD8EF48E439B`, then call slot 3 through a
  `delegate* unmanaged[Stdcall]<…>`.
- **`Access` MUST be `GENERIC_ALL` (0x10000000)** when creating the shared handle for a **primary
  surface** AND for a **display fence**. `DXGI_SHARED_RESOURCE_READ | WRITE` returns `E_INVALIDARG`
  even though the docs suggest READ/WRITE for surfaces. This bit me hard; do not "tidy" it.
- The caller owns the returned NT handle and must `CloseHandle` it after opening — the opened D3D
  resource/fence holds its own reference.

See `DisplayInterop.cs` (moved verbatim from the original renderer). Any refactor there risks
`E_INVALIDARG` — the warning comments are load-bearing.

## Present plumbing

- `mgr.CreateDisplayDevice(target.Adapter)`; there is **no** `RenderAdapterId` on `DisplayDevice` in
  this SDK — use `target.Adapter.Id` for the LUID.
- Build the D3D11 device on the **same render adapter** as the target (enumerate DXGI adapters, match
  by LUID, `D3D11CreateDevice(adapter, DriverType.Unknown, BgraSupport, …)`).
- `DisplayScanout` / `DisplayTask` are **not** `IDisposable` — do not wrap them in `using`.
- `CreateSimpleScanout*` `subResourceIndex` / `syncInterval` are `uint` — pass `0u, 1u`.
- Present via `CreateSimpleScanoutWithDirtyRectsAndOptions` + `taskPool.CreateTask()` +
  `SetScanout` + `TryExecuteTask`, then inspect `DisplayTaskResult.PresentStatus`.

## Mode selection: always pick a 1:1 `src == tgt` mode

A specialized panel typically enumerates both native 1:1 modes and **scaled** modes where the source
is smaller than the target and the panel's scaler upscales (e.g. `src=1176x664 → tgt=2560x682`).
**Avoid scaled modes** — always require `SourceResolution == TargetResolution` at the native size.
`ModeSelectors.NativeOneToOne(w, h, format)` encodes exactly this filter (and rejects `src < tgt`).

For Direct2D, select the **`B8G8R8A8UIntNormalized`** source format (BGRA, premultiplied-friendly);
the library wraps each shared primary as a `Format.B8G8R8A8_UNorm` D2D target with `AlphaMode.Ignore`.

## Fence-based presentation

Two fences drive presentation (both opened via the interop shared-handle path above):

- **Render-completion fence** (`ID3D11Device5.CreateFence`, D3D-only, not shared): signalled on the
  immediate context after each D2D draw and CPU-waited before scanout, so the primary is never
  scanned out mid-render.
- **Periodic fence** (`DisplayDevice.CreatePeriodicFence`, opened as an `ID3D11Fence`): event-based
  V-blank pacing. Falls back to `DisplayDevice.WaitForVBlank` when unavailable. Its failure/recovery
  behavior is documented in `resilience.md`.
