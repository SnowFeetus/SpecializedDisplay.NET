using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Windows.Devices.Display.Core;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct2D1;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace SpecializedDisplay;

/// <summary>
/// An exclusive scanout session over a specialized display target, built on the proven Spike 2
/// acquire pipeline (find target → set 1:1 mode → TryApply → CreateDisplayDevice → D3D11 on the same
/// render adapter → shared primaries + D2D wrapping) and productionized with DisplayCore fence
/// primitives:
/// <list type="bullet">
/// <item>A D3D11 <b>render fence</b> (ID3D11Device5.CreateFence) signalled on the immediate context
/// after each D2D draw and CPU-waited before scanout, so the primary is never scanned out mid-render.</item>
/// <item>A <b>periodic fence</b> (CreatePeriodicFence) opened as an ID3D11Fence via IDisplayDeviceInterop
/// for event-based V-blank pacing (falls back to WaitForVBlank if unavailable).</item>
/// <item>Presentation via <c>CreateSimpleScanoutWithDirtyRectsAndOptions</c> + <c>TryExecuteTask</c>,
/// inspecting <c>DisplayTaskResult.PresentStatus</c>.</item>
/// </list>
/// DEVICE_REMOVED and VIDPN_SOURCE_IN_USE are detected (present status, DeviceRemovedReason, HRESULT)
/// and drive a bounded re-acquire loop that raises typed events.
///
/// <para><b>Threading:</b> a session is single-threaded. All lifecycle (<see cref="Acquire"/>,
/// <see cref="Release"/>) and frame (<see cref="BeginFrame"/>, <see cref="EndFramePresent"/>) calls
/// must run on the one render thread; callers must marshal onto it. Debug builds assert this.</para>
/// </summary>
public sealed class ExclusiveDisplaySession : IDisposable
{
    // Well-known HRESULTs for recovery classification.
    private const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    private const int DXGI_ERROR_DEVICE_HUNG    = unchecked((int)0x887A0006);
    private const int DXGI_ERROR_DEVICE_RESET   = unchecked((int)0x887A0007);
    private const int VIDPN_SOURCE_IN_USE       = unchecked((int)0xC01E0207);

    private readonly DisplaySelector _selector;
    private readonly AcquireOptions _options;
    private readonly Action<LogLevel, string>? _log;

    private readonly int _rot;      // normalized 0/90/180/270
    private readonly bool _flipX, _flipY;
    private Matrix3x2 _transform = Matrix3x2.Identity;   // logical -> physical; built from applied-mode dims
    private RectInt32[] _fullDirty = Array.Empty<RectInt32>();

    // Physical framebuffer dims and applied mode, both set on acquire.
    private int _physW, _physH;
    private AppliedMode? _mode;

    private DisplayManager _mgr = null!;
    private DisplayTarget _target = null!;
    private DisplayDevice _displayDevice = null!;
    private DisplayTaskPool _taskPool = null!;
    private DisplaySource _source = null!;
    // Double-buffered (page-flip) primaries: we draw into the back buffer while the display
    // controller scans out the front, then flip at V-blank. Single-buffering (one primary drawn
    // and scanned at once) was the on-panel tearing/flicker source.
    private DisplaySurface[] _primary = null!;
    private int _back; // index of the back buffer — the one we draw into next

    private ID3D11Device _d3dDevice = null!;
    private ID3D11Device5 _d3dDevice5 = null!;
    private ID3D11DeviceContext _d3dContext = null!;
    private ID3D11DeviceContext4 _d3dContext4 = null!;
    private ID3D11Device1 _d3d1 = null!;
    private ID3D11Texture2D[] _primaryTex = null!;

    // Render-completion fence (D3D-only; not shared).
    private ID3D11Fence _renderFence = null!;
    private ulong _renderValue;
    private AutoResetEvent _renderEvent = null!;

    // V-blank pacing (periodic display fence opened as a D3D fence).
    private DisplayFence _periodicFence = null!;
    private ID3D11Fence _periodicD3D = null!;
    private AutoResetEvent _vblankEvent = null!;
    private bool _useFencePacing;

    // Disconnect-robust pacing. When the owning session is backgrounded (fast-user-switch / RDP /
    // lock), the vblank wait stops blocking and the loop would free-run at 300-1000fps (owned=true,
    // no device_removed) — cooking the GPU on a thermals panel. We time each wait: a HARD CAP floors
    // every present at the refresh interval so we never exceed the target rate even when the wait
    // returns instantly, and a run of near-instant waits flips us into an idle-throttle (Sleep) until
    // real vblanks return (session reconnected). We do NOT re-acquire here — a backgrounded session
    // can't, and nothing was actually lost; normal pacing must simply resume when real vblanks return.
    private readonly double _targetFrameMs;   // 1000 / RefreshHz
    private readonly double _instantWaitMs;    // TargetFrameMs * InstantWaitFraction; shorter = "no real vblank"
    private readonly Stopwatch _paceClock = Stopwatch.StartNew();
    private double _lastPaceEndMs = double.NegativeInfinity;
    private int _instantWaits;
    private bool _background;

    // A Windows display power-off (screen timeout) can kill the periodic fence PERMANENTLY —
    // observed on-hardware 2026-07-04: after the timeout fired, every fence wait returned
    // instantly even once the panel was scanning again, so the backend sat in the background
    // throttle forever (~5fps under touch). Recovery: when the fence dies (device-removed
    // sentinel or a wait timeout), stop consulting it and REBUILD it on a slow retry cadence;
    // a healthy rebuilt fence blocks on the next wait, which clears the throttle naturally.
    // The same retry runs while merely backgrounded, covering a fence that free-runs instead.
    private bool _fenceDied;    // fence pacing lost mid-flight (vs never available at acquire)
    private double _reviveAtMs; // next fence-rebuild attempt on the pace clock (0 = none scheduled)

    private ID2D1Factory1 _d2dFactory = null!;
    private ID2D1Device _d2dDevice = null!;
    private ID2D1DeviceContext _dc = null!;
    private ID2D1Bitmap1[] _target2d = null!;

    private bool _owned;
    private bool _needReacquire;
    private bool _released;   // cooperative-release latch (NEW)
    // Countdown of upcoming presents that must be full redraws. Set to BufferCount on (re)acquire so
    // EVERY freshly-allocated (garbage) primary is painted before the render-on-change loop can idle
    // and later flip to a never-drawn buffer. Self-decrementing via ConsumeForceRedraw.
    private int _forceRedraw;

    // Debug capture: callback to invoke on the next frame, or null. Set by RequestCapture, consumed
    // once inside EndFramePresent after the render fence confirms the draw is complete.
    private Action<CapturedFrame>? _pendingCapture;

    // Debug-only single-thread affinity guard (risk register #17). The render thread is whoever
    // first calls Acquire; every subsequent lifecycle/frame call must be on that same thread.
    private int _threadId;

    internal ExclusiveDisplaySession(DisplaySelector selector, AcquireOptions options)
    {
        _selector = selector;
        _options = options;
        _log = options.Log;
        _rot = DisplayTransform.Normalize(options.Rotation);
        _flipX = options.FlipX;
        _flipY = options.FlipY;
        _targetFrameMs = 1000.0 / options.RefreshHz;
        _instantWaitMs = _targetFrameMs * options.InstantWaitFraction;
    }

    // ---- state ----

    public ID2D1DeviceContext Dc => _dc;
    public Size PhysicalSize => new(_physW, _physH);
    public Size LogicalSize
    {
        get { var (w, h) = DisplayTransform.LogicalSize(_rot, _physW, _physH); return new Size(w, h); }
    }
    public Matrix3x2 LogicalTransform => _transform;
    public AppliedMode? Mode => _mode;
    public bool IsOwned => _owned;
    public bool IsReleased => _released;

    public bool ConsumeForceRedraw()
    {
        if (_forceRedraw <= 0) return false;
        _forceRedraw--;
        return true;
    }

    public void RequestCapture(Action<CapturedFrame> onCaptured) => _pendingCapture = onCaptured;

    // ---- typed events (replace the app's direct IPC sends; the app forwards them verbatim) ----

    public event Action? Acquired;                  // after EVERY successful acquire (initial, explicit, re-)
    public event Action<AppliedMode>? ModeApplied;  // fires BEFORE Acquired (preserves IPC order)
    public event Action<string>? OwnershipLost;     // detail, e.g. "present: SourceInvalid"
    public event Action<string>? DeviceRemoved;     // detail, e.g. "DeviceRemovedReason=0x887A0005"
    public event Action<string>? TargetOffDesktop;  // first TargetNotFound during reacquire
    public event Action<int>? Reacquired;           // attempt count; fires AFTER ModeApplied+Acquired
    public event Action? Released;                   // cooperative release completed (NEW)

    // ---- frame loop (single-threaded; call only on the render thread) ----

    public void BeginFrame()
    {
        AssertThread();
        if (_released) throw new InvalidOperationException("session is released; call Acquire() first.");
        if (_needReacquire)
            Reacquire();

        _dc.Target = _target2d[_back]; // draw into the BACK buffer (never the one being scanned out)
        _dc.BeginDraw();
        _dc.Transform = _transform; // logical portrait -> physical landscape
        _dc.TextAntialiasMode = TextAntialiasMode.Grayscale;
    }

    public void EndFramePresent()
    {
        if (_released) throw new InvalidOperationException("session is released; call Acquire() first.");
        try
        {
            _dc.EndDraw();

            // 1) Ensure the GPU has finished the D2D draw before the display scans out the primary.
            _renderValue++;
            _d3dContext4.Signal(_renderFence, _renderValue);
            _d3dContext.Flush();
            _renderFence.SetEventOnCompletion(_renderValue, _renderEvent);
            if (!_renderEvent.WaitOne(_options.RenderFenceTimeoutMs) && CheckDeviceRemoved()) return;

            // 1b) Debug capture (only when pending): the fence above guarantees the GPU has finished the
            // D2D draw into the back primary, so it is safe to read back now — before the scanout below.
            CaptureIfPending();

            // 2) Page-flip: scan out the freshly rendered back buffer.
            var scanout = _displayDevice.CreateSimpleScanoutWithDirtyRectsAndOptions(
                _source, _primary[_back], 0u, 1u, _fullDirty, DisplayScanoutOptions.None);
            var task = _taskPool.CreateTask();
            task.SetScanout(scanout);
            var result = _taskPool.TryExecuteTask(task);
            if (!EvaluatePresent(result)) return; // recovery pending; keep _back so Reacquire resets it

            // 3) Pace to the next V-blank — the flip to the back buffer completes here, which is also
            //    when the previously-displayed buffer stops being scanned and becomes free to reuse.
            PaceToVBlank();

            // 4) Advance: the buffer just presented is now the front; the previously-shown buffer
            //    (no longer scanned as of the V-blank above) becomes the next back buffer. A surface
            //    is therefore never drawn into while it is the active scan-out target -> no tearing.
            _back = (_back + 1) % _options.BufferCount;
        }
        catch (Exception ex)
        {
            HandlePresentException(ex);
        }
    }

    // ---- debug capture ----

    /// <summary>Read back the just-drawn back primary and hand the caller an UPRIGHT logical-orientation
    /// BGRA frame. Off the hot path (pending-only) and fully guarded — a readback OR callback failure is
    /// logged and swallowed so it can never disturb presentation.</summary>
    private void CaptureIfPending()
    {
        if (_pendingCapture is not { } callback) return;
        _pendingCapture = null;
        try
        {
            var frame = FrameCapture.Capture(_d3dDevice, _d3dContext, _primaryTex[_back],
                _physW, _physH, _rot, _flipX, _flipY);
            callback(frame);
        }
        catch (Exception ex)
        {
            _log?.Invoke(LogLevel.Warn, $"capture failed: {ex.Message}");
        }
    }

    // ---- present-path helpers ----

    private void PaceToVBlank()
    {
        // Run the real pacing primitive and measure how long it actually blocked. A near-instant
        // return means there is no true vblank to wait on (owning session disconnected/backgrounded).
        double before = _paceClock.Elapsed.TotalMilliseconds;
        WaitForVBlankOnce();
        double waitMs = _paceClock.Elapsed.TotalMilliseconds - before;
        UpdateBackgroundState(waitMs);

        // Dead fence and/or backgrounded: retry rebuilding the periodic fence at a slow cadence.
        // Once the display is scanning again the fresh fence BLOCKS, UpdateBackgroundState sees a
        // real wait on the next cycle, and normal pacing resumes with no other intervention.
        if ((_fenceDied || _background) && _reviveAtMs > 0 &&
            _paceClock.Elapsed.TotalMilliseconds >= _reviveAtMs)
        {
            _reviveAtMs = _paceClock.Elapsed.TotalMilliseconds + _options.FenceReviveIntervalMs;
            TryReviveFencePacing();
        }

        if (_background)
        {
            // Not really presenting to a live scanout — throttle hard so we don't cook the GPU. We
            // still call WaitForVBlankOnce every cycle, so once real (blocking) vblanks return after
            // reconnect, UpdateBackgroundState clears this state and normal pacing resumes.
            Thread.Sleep(_options.BackgroundSleepMs);
        }
        else
        {
            // Hard frame cap: even before the background throttle engages (and if it never does),
            // never exceed the refresh rate. If this present cycle came in under the frame budget
            // (because the vblank wait returned early), sleep the remainder.
            double sinceLast = _paceClock.Elapsed.TotalMilliseconds - _lastPaceEndMs;
            double deficit = _targetFrameMs - sinceLast;
            if (deficit > 0.5 && deficit <= _targetFrameMs)
                Thread.Sleep((int)Math.Ceiling(deficit));
        }

        _lastPaceEndMs = _paceClock.Elapsed.TotalMilliseconds;
    }

    private void WaitForVBlankOnce()
    {
        if (_useFencePacing)
        {
            ulong v = _periodicD3D.CompletedValue;
            if (v == ulong.MaxValue) // fence device-removed sentinel
            {
                // Not an actual device removal (that path triggers recovery): the fence itself
                // died, typically because the display was powered off. Degrade + schedule revive.
                if (!CheckDeviceRemoved()) DegradeFencePacing("CompletedValue = device-removed sentinel");
                return;
            }
            _periodicD3D.SetEventOnCompletion(v + 1, _vblankEvent);
            if (!_vblankEvent.WaitOne(_options.VBlankFenceTimeoutMs) && !CheckDeviceRemoved())
                DegradeFencePacing("periodic fence stopped advancing");
            return;
        }

        // Fence pacing died mid-flight: return immediately — the background throttle paces us and
        // the revive timer rebuilds the fence. Never fall into WaitForVBlank here: on a powered-off
        // display it could block unboundedly and starve the heartbeat (supervisor would kill us).
        if (_fenceDied) return;

        _displayDevice.WaitForVBlank(_source);
    }

    /// <summary>The periodic fence stopped delivering vblanks (display power-off is the known cause).
    /// Drop to throttled fence-less pacing and schedule rebuild attempts.</summary>
    private void DegradeFencePacing(string reason)
    {
        _useFencePacing = false;
        _fenceDied = true;
        _periodicD3D?.Dispose(); _periodicD3D = null!;
        _vblankEvent?.Dispose(); _vblankEvent = null!;
        _periodicFence = null!;
        _reviveAtMs = _paceClock.Elapsed.TotalMilliseconds + _options.FenceReviveIntervalMs;
        _log?.Invoke(LogLevel.Warn, $"periodic fence dead ({reason}); display power-off? throttling and rebuilding every {_options.FenceReviveIntervalMs / 1000:0}s");
    }

    /// <summary>Dispose any fence remnants and re-run the standard fence setup. Quiet on failure
    /// (retried on the next interval — the display may simply still be off); logs once on success.</summary>
    private void TryReviveFencePacing()
    {
        _periodicD3D?.Dispose(); _periodicD3D = null!;
        _vblankEvent?.Dispose(); _vblankEvent = null!;
        _periodicFence = null!;
        _useFencePacing = false;
        try
        {
            SetupPeriodicFence(quiet: true);
            if (_useFencePacing)
            {
                _fenceDied = false;
                _log?.Invoke(LogLevel.Info, "periodic fence rebuilt (display power recovery); pacing resumes on the next blocking wait");
            }
        }
        catch { /* stay degraded; retry next interval */ }
    }

    // Enter the throttle after a run of near-instant waits (session backgrounded); leave it the moment
    // a real vblank blocks again (reconnected). Logs each transition once for observability. Never
    // triggers a re-acquire — this is a pacing condition, not a device/ownership loss.
    private void UpdateBackgroundState(double waitMs)
    {
        if (waitMs < _instantWaitMs)
        {
            if (_instantWaits < _options.BackgroundEnterFrames) _instantWaits++;
            if (!_background && _instantWaits >= _options.BackgroundEnterFrames)
            {
                _background = true;
                // Schedule fence rebuilds even when the fence didn't formally die — a display
                // power-off can leave it free-running (instant completions) rather than stalled.
                if (_reviveAtMs <= 0) _reviveAtMs = _paceClock.Elapsed.TotalMilliseconds + _options.FenceReviveIntervalMs;
                _log?.Invoke(LogLevel.Info, "vblank not blocking (session disconnected/backgrounded, or display off?); throttling to idle cadence, no re-acquire");
            }
        }
        else
        {
            if (_background)
            {
                _background = false;
                if (!_fenceDied) _reviveAtMs = 0; // healthy pacing again — stop the rebuild timer
                _log?.Invoke(LogLevel.Info, "vblank pacing resumed (session reconnected / display on); normal cadence restored");
            }
            _instantWaits = 0;
        }
    }

    /// <summary>Returns true to continue; false if a recovery was triggered.</summary>
    private bool EvaluatePresent(DisplayTaskResult result)
    {
        switch (result.PresentStatus)
        {
            case DisplayPresentStatus.Success:
                return true;
            case DisplayPresentStatus.DeviceInvalid:
                TriggerRecovery(DisplayLossKind.DeviceRemoved, "present: DeviceInvalid");
                return false;
            case DisplayPresentStatus.SourceInvalid:
            case DisplayPresentStatus.SourceStatusPreventedPresent:
                TriggerRecovery(DisplayLossKind.OwnershipLost, $"present: {result.PresentStatus}");
                return false;
            default:
                // ScanoutInvalid / UnknownFailure — usually transient; log and keep going.
                _log?.Invoke(LogLevel.Warn, $"present status {result.PresentStatus}");
                return true;
        }
    }

    private bool CheckDeviceRemoved()
    {
        Result reason = _d3dDevice.DeviceRemovedReason;
        if (reason.Failure)
        {
            TriggerRecovery(DisplayLossKind.DeviceRemoved, $"DeviceRemovedReason=0x{reason.Code:X8}");
            return true;
        }
        return false;
    }

    private void HandlePresentException(Exception ex)
    {
        DisplayLossKind kind = ex.HResult switch
        {
            DXGI_ERROR_DEVICE_REMOVED or DXGI_ERROR_DEVICE_HUNG or DXGI_ERROR_DEVICE_RESET => DisplayLossKind.DeviceRemoved,
            VIDPN_SOURCE_IN_USE => DisplayLossKind.OwnershipLost,
            _ => DisplayLossKind.OwnershipLost,
        };
        TriggerRecovery(kind, ex.Message);
    }

    private void TriggerRecovery(DisplayLossKind kind, string detail)
    {
        if (!_owned && _needReacquire) return; // already pending
        _owned = false;
        _needReacquire = true;
        string kindStr;
        if (kind == DisplayLossKind.DeviceRemoved) { kindStr = "device_removed"; DeviceRemoved?.Invoke(detail); }
        else { kindStr = "ownership_lost"; OwnershipLost?.Invoke(detail); }
        _log?.Invoke(LogLevel.Warn, $"{kindStr}: {detail}; will re-acquire");
    }

    // ---- acquire / release ----

    // In-process recovery handles the common transient case (brief ownership loss, momentary
    // source-in-use). It is intentionally BOUNDED and short (< ~3s of backoff) so BeginFrame never
    // blocks past a typical supervisor's 5s "frozen" threshold. If it can't recover quickly, we throw
    // and let the host exit — a supervisor owns persistent-failure restart with backoff + crash-loop
    // breaking, which is the right layer for a genuinely absent/held panel.

    private void Reacquire()
    {
        _needReacquire = false;
        ReleaseGraphics();
        Exception? last = null;
        for (int attempt = 1; attempt <= _options.ReacquireAttempts; attempt++)
        {
            try
            {
                Acquire();
                Reacquired?.Invoke(attempt);
                _log?.Invoke(LogLevel.Info, $"re-acquired display target after {attempt} attempt(s)");
                return;
            }
            catch (TargetNotFoundException ex)
            {
                last = ex;
                if (attempt == 1) TargetOffDesktop?.Invoke("display target not present");
                ReleaseGraphics();
                Thread.Sleep(Math.Min(_options.ReacquireBackoffMaxMs, _options.ReacquireBackoffBaseMs * attempt));
            }
            catch (Exception ex)
            {
                last = ex;
                _log?.Invoke(LogLevel.Warn, $"re-acquire attempt {attempt} failed: {ex.Message}");
                ReleaseGraphics();
                Thread.Sleep(Math.Min(_options.ReacquireBackoffMaxMs, _options.ReacquireBackoffBaseMs * attempt));
            }
        }

        _log?.Invoke(LogLevel.Error, $"re-acquire exhausted after {_options.ReacquireAttempts} attempts; exiting for supervisor restart: {last?.Message}");
        throw new DisplayRecoveryFailedException(last?.Message ?? "re-acquire failed");
    }

    /// <summary>Acquire (or re-acquire) exclusive ownership and rebuild all GPU/display state. Clears
    /// a prior cooperative <see cref="Release"/>. Raises <see cref="ModeApplied"/> then
    /// <see cref="Acquired"/>. Throws <see cref="TargetNotFoundException"/>, <see cref="ModeNotFoundException"/>,
    /// or <see cref="AcquireFailedException"/>.</summary>
    public void Acquire()
    {
        AssertThread();
        _released = false;

        var (key, _) = SpecializedDisplays.ResolveKey(_selector);
        _mgr = DisplayManager.Create(DisplayManagerOptions.EnforceSourceOwnership);

        DisplayTarget? target = null;
        DisplayState? state = null;
        DisplayPath? path = null;
        DisplayModeInfo? applied = null;
        for (int attempt = 1; attempt <= _options.AcquireAttempts; attempt++)
        {
            target = SpecializedDisplays.ResolveTarget(_mgr, key, _selector);
            if (target is null)
                throw new TargetNotFoundException("display target not found (won't touch other displays).");

            var acq = _mgr.TryAcquireTargetsAndCreateEmptyState(new[] { target });
            if (acq.ErrorCode != DisplayManagerResult.Success)
            {
                // TargetAccessDenied == another client (usually the desktop compositor) holds the
                // source == the VIDPN_SOURCE_IN_USE condition; back off and retry.
                if (acq.ErrorCode == DisplayManagerResult.TargetAccessDenied)
                    OwnershipLost?.Invoke($"acquire: {acq.ErrorCode}");
                Thread.Sleep(_options.AcquireRetryBaseMs * attempt);
                continue;
            }

            state = acq.State;
            path = state.ConnectTarget(target);

            DisplayModeInfo? mode = null;
            foreach (var mi in path.FindModes(DisplayModeQueryOptions.None))
            {
                var rate = mi.PresentationRate.VerticalSyncRate;
                double hz = rate.Denominator != 0 ? (double)rate.Numerator / rate.Denominator : 0.0;
                var desc = new DisplayModeDescriptor(
                    mi.SourceResolution.Width, mi.SourceResolution.Height,
                    mi.TargetResolution.Width, mi.TargetResolution.Height,
                    mi.SourcePixelFormat, hz);
                if (_options.ModeSelector(desc)) { mode = mi; break; }
            }
            if (mode is null)
                throw new ModeNotFoundException("no mode matched the configured selector.");

            path.ApplyPropertiesFromMode(mode);
            var apply = state.TryApply(DisplayStateApplyOptions.FailIfStateChanged);
            if (apply.Status == DisplayStateOperationStatus.Success) { applied = mode; break; }

            state = null; path = null;
            if (attempt == _options.AcquireAttempts)
                throw new AcquireFailedException($"TryApply never succeeded (last={apply.Status}).");
        }

        _target = target!;

        // Physical framebuffer dims come from the applied mode (the source resolution we draw into).
        _physW = applied!.SourceResolution.Width;
        _physH = applied.SourceResolution.Height;
        _transform = DisplayTransform.Compute(_rot, _flipX, _flipY, _physW, _physH);
        _fullDirty = new[] { new RectInt32 { X = 0, Y = 0, Width = _physW, Height = _physH } };

        var appliedRate = applied.PresentationRate.VerticalSyncRate;
        double appliedHz = appliedRate.Denominator != 0 ? (double)appliedRate.Numerator / appliedRate.Denominator : 0.0;
        _mode = new AppliedMode(_physW, _physH, applied.SourcePixelFormat, appliedHz);
        ModeApplied?.Invoke(_mode);

        // D3D11 device on the SAME render adapter as the target.
        _displayDevice = _mgr.CreateDisplayDevice(_target.Adapter);
        long renderLuid = ((long)_target.Adapter.Id.HighPart << 32) | _target.Adapter.Id.LowPart;
        (_d3dDevice, _d3dContext, _d3d1) = CreateD3D11OnAdapter(renderLuid);
        _d3dDevice5 = _d3dDevice.QueryInterface<ID3D11Device5>();
        _d3dContext4 = _d3dContext.QueryInterface<ID3D11DeviceContext4>();

        _taskPool = _displayDevice.CreateTaskPool();
        _source = _displayDevice.CreateScanoutSource(_target);
        var primaryDesc = new DisplayPrimaryDescription((uint)_physW, (uint)_physH,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXColorSpace.RgbFullG22NoneP709,
            false,
            new Direct3DMultisampleDescription { Count = 1, Quality = 0 });

        // Allocate BufferCount primaries and open each as its own shared D3D texture
        // (IDisplayDeviceInterop, GENERIC_ALL) so we can page-flip between them.
        _primary = new DisplaySurface[_options.BufferCount];
        _primaryTex = new ID3D11Texture2D[_options.BufferCount];
        for (int i = 0; i < _options.BufferCount; i++)
        {
            _primary[i] = _displayDevice.CreatePrimary(_target, primaryDesc);
            IntPtr sharedPrimary = DisplayInterop.CreateSharedHandle(_displayDevice, _primary[i]);
            _primaryTex[i] = _d3d1.OpenSharedResource1<ID3D11Texture2D>(sharedPrimary);
            DisplayInterop.CloseHandle(sharedPrimary);
        }
        _back = 0;

        // Render-completion fence (D3D-only).
        _renderFence = _d3dDevice5.CreateFence<ID3D11Fence>(0, FenceFlags.None);
        _renderValue = 0;
        _renderEvent = new AutoResetEvent(false);

        // V-blank pacing via a periodic fence opened as a D3D fence (graceful fallback to WaitForVBlank).
        SetupPeriodicFence();

        // Wrap each shared primary as its own Direct2D target on the same D3D device.
        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        var props = new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore),
            96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw);
        _target2d = new ID2D1Bitmap1[_options.BufferCount];
        for (int i = 0; i < _options.BufferCount; i++)
        {
            using var surface = _primaryTex[i].QueryInterface<IDXGISurface>();
            _target2d[i] = _dc.CreateBitmapFromDxgiSurface(surface, props);
        }

        _owned = true;
        _forceRedraw = _options.BufferCount; // every fresh primary contains garbage — force one full redraw EACH
        _background = false; _instantWaits = 0; _lastPaceEndMs = double.NegativeInfinity; // fresh pacing state
        _fenceDied = false; _reviveAtMs = 0;                                              // fresh fence-revive state
        Acquired?.Invoke();
    }

    /// <summary>Voluntarily give up ownership without tearing the session down: release all GPU/display
    /// state, suppress auto-reacquire, and latch <see cref="IsReleased"/>. Frame calls throw until the
    /// next <see cref="Acquire"/>. NEW cooperative-release path (design locked decision #3).</summary>
    public void Release()
    {
        AssertThread();
        ReleaseGraphics();
        _released = true;
        _needReacquire = false;
        Released?.Invoke();
    }

    private void SetupPeriodicFence(bool quiet = false)
    {
        try
        {
            _periodicFence = _displayDevice.CreatePeriodicFence(_target, TimeSpan.Zero);
            IntPtr h = DisplayInterop.CreateSharedHandle(_displayDevice, _periodicFence);
            _periodicD3D = _d3dDevice5.OpenSharedFence<ID3D11Fence>(h);
            DisplayInterop.CloseHandle(h);
            _vblankEvent = new AutoResetEvent(false);
            _useFencePacing = true;
        }
        catch (Exception ex)
        {
            _useFencePacing = false;
            // quiet: revive attempts while the display is powered off fail repeatedly by design —
            // don't spam a warn every retry interval (the degrade itself was already logged once).
            if (!quiet) _log?.Invoke(LogLevel.Warn, $"periodic-fence pacing unavailable, using WaitForVBlank: {ex.Message}");
        }
    }

    private void ReleaseGraphics()
    {
        _owned = false;
        if (_target2d != null) { foreach (var t in _target2d) t?.Dispose(); _target2d = null!; }
        _dc?.Dispose(); _dc = null!;
        _d2dDevice?.Dispose(); _d2dDevice = null!;
        _d2dFactory?.Dispose(); _d2dFactory = null!;
        _periodicD3D?.Dispose(); _periodicD3D = null!;
        _vblankEvent?.Dispose(); _vblankEvent = null!;
        _periodicFence = null!;
        _useFencePacing = false;
        _renderFence?.Dispose(); _renderFence = null!;
        _renderEvent?.Dispose(); _renderEvent = null!;
        if (_primaryTex != null) { foreach (var t in _primaryTex) t?.Dispose(); _primaryTex = null!; }
        _d3dContext4?.Dispose(); _d3dContext4 = null!;
        _d3dDevice5?.Dispose(); _d3dDevice5 = null!;
        _d3d1?.Dispose(); _d3d1 = null!;
        _d3dContext?.Dispose(); _d3dContext = null!;
        _d3dDevice?.Dispose(); _d3dDevice = null!;
        // DisplayDevice/TaskPool/Source/Surface are WinRT and released with the manager.
        _displayDevice = null!; _taskPool = null!; _source = null!; _primary = null!; _target = null!;
        _mgr?.Dispose(); _mgr = null!;
    }

    public void Dispose() => ReleaseGraphics();

    private static (ID3D11Device, ID3D11DeviceContext, ID3D11Device1) CreateD3D11OnAdapter(long luid)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? chosen = null;
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            if (adapter.Description1.Luid == luid) { chosen = adapter; break; }
            adapter.Dispose();
        }
        if (chosen is null) throw new InvalidOperationException($"No DXGI adapter with LUID 0x{luid:X16}");
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11CreateDevice(chosen, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
            out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
        chosen.Dispose();
        return (dev, ctx, dev.QueryInterface<ID3D11Device1>());
    }

    [Conditional("DEBUG")]
    private void AssertThread()
    {
        if (_threadId == 0) _threadId = Environment.CurrentManagedThreadId;
        else Debug.Assert(_threadId == Environment.CurrentManagedThreadId,
            "ExclusiveDisplaySession is single-threaded; Acquire/Release/frame calls must all run on the render thread.");
    }
}
