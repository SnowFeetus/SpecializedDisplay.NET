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
    // WHICH flavor of death we saw (hardware-verified 2026-07-06). The rebuild recovers "stopped
    // advancing" on wake, but NEVER the device-removed sentinel (a slept panel that ignored desktop
    // wake) — that flavor escalates to a full release + re-acquire (see the wake-escalation path).
    private FenceDeathFlavor _fenceDeathFlavor;
    private double _nextWakeEscalationMs; // next wake-escalation attempt on the pace clock (0 = none)

    // Console-wake path (see ConsoleDisplayWatcher): a desktop wake never re-powers a specialized
    // target, so when the console display comes back ON while pacing says the panel is dark, we arm
    // a short deadline; if pacing hasn't recovered by itself when it fires, we release + re-acquire
    // (the mode re-apply re-powers scanout — the hardware-verified way to re-light a dark panel on
    // an awake desktop). Null watcher = disabled (option 0) or registration failed.
    private readonly ConsoleDisplayWatcher? _consoleWatcher;
    private double _consoleWakeAtMs; // armed console-wake deadline on the pace clock (0 = none)

    private ID2D1Factory1 _d2dFactory = null!;
    private ID2D1Device _d2dDevice = null!;
    private ID2D1DeviceContext _dc = null!;
    private ID2D1Bitmap1[] _target2d = null!;

    private bool _owned;
    private bool _needReacquire;
    private bool _released;   // cooperative-release latch (NEW)

    // Bounded reacquire + wake escalation, spread ONE attempt per frame (watchdog budget — see BeginFrame).
    private readonly ReacquirePlan _reacquire;
    // The context the consumer draws onto this frame: the live display context when owned, else the idle
    // canvas (a discarded WARP target used between acquire attempts). Set by BeginFrame; exposed via Dc.
    private ID2D1DeviceContext? _activeDc;
    private IdleCanvas? _idle;         // lazily created on the first not-owned frame; lives until Dispose.
    private bool _idleCanvasFailed;    // WARP idle canvas couldn't be built — fall back to in-call blocking.
    private bool _frameHadRecoveryBackoff; // this frame already slept a backoff — don't also idle-throttle.
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
        _reacquire = new ReacquirePlan(options.ReacquireAttempts, options.ReacquireBackoffBaseMs, options.ReacquireBackoffMaxMs);
        if (options.ConsoleWakeGraceMs > 0)
        {
            try { _consoleWatcher = new ConsoleDisplayWatcher(); }
            catch (Exception ex)
            {
                _log?.Invoke(LogLevel.Warn, $"console display watcher unavailable ({ex.Message}); wake-on-console-on disabled");
            }
        }
    }

    // ---- state ----

    // The context whose target is the current frame's framebuffer. While owned this is the live display
    // context; between acquire attempts (not owned) it is the idle canvas set by BeginFrame. Falls back
    // to the live context before the first frame.
    public ID2D1DeviceContext Dc => _activeDc ?? _dc;
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

    /// <summary>Request a one-shot readback of the next presented frame. If called while NOT owned
    /// (recovering / escalating), the request is <b>deferred</b>, not serviced against the idle canvas:
    /// the readback only runs on an owned present (see <see cref="CaptureIfPending"/>), so it always
    /// captures the real panel — never the discarded not-owned scaffold. On the first owned frame after a
    /// re-acquire the force-redraw guarantees a fully painted primary, so the captured frame is never
    /// garbage. If ownership is never restored, the pending request is simply never serviced.</summary>
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

        // WATCHDOG-BUDGET INVARIANT (docs/resilience.md): no single BeginFrame may block anywhere near the
        // supervisor's ~5s "frozen" threshold. Recovery (a present-detected loss) and wake escalation (a
        // persistently dead fence) both re-acquire the target, which on a sleeping/contended panel can cost
        // seconds PER attempt. So we perform AT MOST ONE acquire attempt here — plus at most one bounded
        // backoff sleep — and carry the attempt state across frames in _reacquire. The caller's heartbeat
        // keeps beating between attempts. StepRecovery may restore ownership, stay pending, or (genuine
        // recovery exhausted) throw DisplayRecoveryFailedException.
        _frameHadRecoveryBackoff = false;
        MaybeStartRecovery();
        if (_reacquire.Active)
            StepRecovery();

        if (_owned)
        {
            SetupOwnedFrame();
            return;
        }

        // Not owned this frame: hand the caller the idle canvas so its unconditional Draw is safe. The
        // frame is discarded (no scanout) — see EndFramePresent's not-owned path. IsOwned stays false.
        var idle = BeginIdleFrame();
        if (idle is not null)
        {
            _activeDc = idle;
        }
        else
        {
            // Idle canvas unavailable (best-effort scaffolding, not a correctness dependency — see
            // BeginIdleFrame). We cannot hand back a valid not-owned target, so finish this recovery INSIDE
            // this call (degrading to the pre-spread in-call blocking) rather than return a null Dc. Never
            // an NRE, never a null Dc: DrainRecoveryBlocking makes us owned or throws.
            DrainRecoveryBlocking();
            SetupOwnedFrame();
        }
    }

    /// <summary>Point the live display context at the back buffer and open the frame. Owned path only.</summary>
    private void SetupOwnedFrame()
    {
        _dc.Target = _target2d[_back]; // draw into the BACK buffer (never the one being scanned out)
        _dc.BeginDraw();
        _dc.Transform = _transform; // logical portrait -> physical landscape
        _dc.TextAntialiasMode = TextAntialiasMode.Grayscale;
        _activeDc = _dc;
    }

    public void EndFramePresent()
    {
        if (_released) throw new InvalidOperationException("session is released; call Acquire() first.");
        if (!_owned) { EndIdleFrame(); return; } // recovering/escalating: discard the throwaway draw, throttle
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
            if (!EvaluatePresent(result)) return; // recovery pending; keep _back so the rebuild (BuildGpuState) resets it

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
    /// logged and swallowed so it can never disturb presentation. Called ONLY from the owned present path,
    /// so a capture requested while not owned stays pending until the panel is back (never reads the idle
    /// canvas).</summary>
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

    // ---- not-owned frame: a discarded draw target so the caller's render loop keeps running safely while
    //      a spread reacquire/escalation makes at most one attempt per frame ----

    /// <summary>Begin a discarded frame on the idle canvas and return its context, or null if the idle
    /// canvas cannot be built. The idle canvas is BEST-EFFORT scaffolding, not a correctness dependency:
    /// WARP creation essentially never fails on a supported Windows install, but if it does we record it
    /// once and the caller falls back to in-call blocking (see BeginFrame / DrainRecoveryBlocking).</summary>
    private ID2D1DeviceContext? BeginIdleFrame()
    {
        if (_idle is null && !_idleCanvasFailed)
        {
            try { _idle = new IdleCanvas(_physW, _physH); } // physical dims from the last acquire; target is discarded
            catch (Exception ex)
            {
                _idleCanvasFailed = true;
                _log?.Invoke(LogLevel.Error, $"idle canvas unavailable ({ex.Message}); recovery will block in-call and may exit on exhaustion");
            }
        }
        if (_idle is null) return null;
        _idle.Begin(_transform);
        return _idle.Context;
    }

    private void EndIdleFrame()
    {
        _idle?.End();
        // WATCHDOG BUDGET: at most ONE sleep per frame. If this frame already backed off after a recovery
        // attempt, don't stack the idle throttle on top of it (backoff <= ReacquireBackoffMaxMs ~0.8s keeps
        // the whole BeginFrame+EndFramePresent cycle well under ~1.5s). Otherwise this is a pure idle frame
        // (e.g. waiting between wake-escalation windows) — throttle so the caller doesn't spin drawing
        // discarded frames. Same idle cadence as the background throttle.
        if (!_frameHadRecoveryBackoff)
            Thread.Sleep(_options.BackgroundSleepMs);
    }

    /// <summary>Fallback when the idle canvas is unavailable: finish the active recovery episode INSIDE the
    /// current BeginFrame (the pre-spread in-call blocking) so we never hand back a null Dc. This can breach
    /// the watchdog budget — acceptable only because it is unreachable on a healthy Windows install (WARP
    /// is always present). Genuine exhaustion throws inside StepRecovery; a wake episode that exhausts ends
    /// (Active=false) not-owned, so we throw here too — degrading the never-throw escalation to an exit,
    /// which is the correct outcome when we also cannot scaffold a not-owned frame.</summary>
    private void DrainRecoveryBlocking()
    {
        while (_reacquire.Active && !_owned)
            StepRecovery();
        if (!_owned)
            throw new DisplayRecoveryFailedException("re-acquire failed and the idle canvas is unavailable");
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
            // The device-removed sentinel flavor is NEVER fixed by a rebuild (a rebuilt fence just
            // re-sentinels, which would also keep resetting the wake-escalation timer) — leave that flavor
            // to the wake escalation (see BeginFrame/StepRecovery). The stopped-advancing flavor and a
            // free-running (backgrounded) fence still rebuild here.
            if (_fenceDeathFlavor != FenceDeathFlavor.DeviceRemovedSentinel)
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
                // This sentinel flavor is NOT recovered by rebuilding the fence (hardware-verified
                // 2026-07-06) — it drives the wake escalation.
                if (!CheckDeviceRemoved()) DegradeFencePacing(FenceDeathFlavor.DeviceRemovedSentinel, "CompletedValue = device-removed sentinel");
                return;
            }
            _periodicD3D.SetEventOnCompletion(v + 1, _vblankEvent);
            if (!_vblankEvent.WaitOne(_options.VBlankFenceTimeoutMs) && !CheckDeviceRemoved())
                DegradeFencePacing(FenceDeathFlavor.StoppedAdvancing, "periodic fence stopped advancing");
            return;
        }

        // Fence pacing died mid-flight: return immediately — the background throttle paces us and
        // the revive timer rebuilds the fence. Never fall into WaitForVBlank here: on a powered-off
        // display it could block unboundedly and starve the heartbeat (supervisor would kill us).
        if (_fenceDied) return;

        _displayDevice.WaitForVBlank(_source);
    }

    /// <summary>The periodic fence stopped delivering vblanks (display power-off is the known cause).
    /// Drop to throttled fence-less pacing and schedule rebuild attempts. Remembers the death
    /// <paramref name="flavor"/>: the sentinel flavor is unrecoverable by rebuild and, after
    /// <see cref="AcquireOptions.FenceDeadReacquireAfterMs"/>, escalates to a release + re-acquire.</summary>
    private void DegradeFencePacing(FenceDeathFlavor flavor, string reason)
    {
        _useFencePacing = false;
        _fenceDied = true;
        _fenceDeathFlavor = flavor;
        _periodicD3D?.Dispose(); _periodicD3D = null!;
        _vblankEvent?.Dispose(); _vblankEvent = null!;
        _periodicFence = null!;
        _reviveAtMs = _paceClock.Elapsed.TotalMilliseconds + _options.FenceReviveIntervalMs;
        // Schedule the first wake escalation FenceDeadReacquireAfterMs out (only the sentinel flavor
        // will actually escalate; see BeginFrame). 0 disables escalation.
        _nextWakeEscalationMs = _options.FenceDeadReacquireAfterMs > 0
            ? _paceClock.Elapsed.TotalMilliseconds + _options.FenceDeadReacquireAfterMs
            : 0;
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
                _fenceDeathFlavor = FenceDeathFlavor.None;
                _nextWakeEscalationMs = 0; // rebuild worked — cancel any pending wake escalation
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
    // source-in-use) and wake escalation handles a slept panel (device-removed-sentinel fence). Both
    // re-acquire the target. Historically the whole bounded loop ran INSIDE one BeginFrame; on a
    // sleeping/contended panel the cumulative API + backoff time blew past the supervisor's ~5s watchdog
    // and the process was killed. Now the loop is spread ONE attempt per frame (see BeginFrame's
    // invariant) via _reacquire, so the caller's heartbeat keeps beating between attempts:
    //   * Genuine — a present-detected device/ownership loss. Bounded to ReacquireAttempts; on exhaustion
    //               THROWS DisplayRecoveryFailedException out of BeginFrame so the host exits for its
    //               supervisor to own persistent-failure restart (backoff + crash-loop breaking).
    //   * Wake    — a persistently dead device-removed-sentinel fence (a slept panel). NEVER throws; on
    //               exhaustion it reschedules and stays throttled (a sleeping panel is not fatal).

    /// <summary>Start a recovery episode if one is pending and none is running. A present-detected loss
    /// (<see cref="ReacquireMode.Genuine"/>) takes precedence over a wake escalation
    /// (<see cref="ReacquireMode.Wake"/>), which becomes due once the fence has been dead in the
    /// device-removed-sentinel flavor past <see cref="AcquireOptions.FenceDeadReacquireAfterMs"/>. On the
    /// first step of an episode, <see cref="ReleaseGraphics"/> relinquishes the lost/sleeping target —
    /// required before a re-acquire can restore scanout (hardware-verified 2026-07-06).</summary>
    private void MaybeStartRecovery()
    {
        if (_reacquire.Active) return;
        UpdateConsoleWakeHint();
        if (_needReacquire)
        {
            _needReacquire = false;
            _reacquire.Start(ReacquireMode.Genuine);
        }
        else if (WakeEscalation.IsDue(_fenceDeathFlavor, _paceClock.Elapsed.TotalMilliseconds,
                     _nextWakeEscalationMs, _options.FenceDeadReacquireAfterMs))
        {
            _log?.Invoke(LogLevel.Warn, "fence dead (device-removed sentinel) past escalation threshold; releasing + re-acquiring to wake the panel");
            _reacquire.Start(ReacquireMode.Wake);
        }
        else if (_consoleWakeAtMs > 0 && _paceClock.Elapsed.TotalMilliseconds >= _consoleWakeAtMs)
        {
            _consoleWakeAtMs = 0;
            _log?.Invoke(LogLevel.Warn, "display-off throttle persisted after the console display came on; releasing + re-acquiring to re-light the panel");
            _reacquire.Start(ReacquireMode.Wake);
        }
        else return;

        ReleaseGraphics(); // give up the lost/sleeping target before the first attempt
    }

    /// <summary>Console-wake arming (see <see cref="ConsoleDisplayWatcher"/>). A known-off → on
    /// console transition while pacing says the panel is dark arms a <see
    /// cref="AcquireOptions.ConsoleWakeGraceMs"/> deadline; pacing that recovers by itself within the
    /// grace (a merely-backgrounded session reconnecting) disarms it, otherwise
    /// <see cref="MaybeStartRecovery"/> escalates to the Wake release + re-acquire whose mode
    /// re-apply re-powers the panel's scanout.</summary>
    private void UpdateConsoleWakeHint()
    {
        if (_consoleWatcher is null) return;
        bool panelDark = _fenceDied || _background || !_owned;
        if (_consoleWatcher.ConsumeDisplayTurnedOn())
        {
            if (panelDark && _consoleWakeAtMs <= 0)
            {
                _consoleWakeAtMs = _paceClock.Elapsed.TotalMilliseconds + _options.ConsoleWakeGraceMs;
                _log?.Invoke(LogLevel.Info, $"console display turned on while display-off throttled; re-acquiring in {_options.ConsoleWakeGraceMs / 1000.0:0.#}s unless vblank pacing resumes");
            }
        }
        else if (_consoleWakeAtMs > 0 && !panelDark)
        {
            _consoleWakeAtMs = 0;
            _log?.Invoke(LogLevel.Info, "vblank pacing resumed within the console-wake grace; re-acquire cancelled");
        }
    }

    /// <summary>Perform EXACTLY ONE acquire attempt for the active recovery episode, then return so the
    /// caller's heartbeat can beat. On success raises <see cref="Reacquired"/> (after the ModeApplied +
    /// Acquired that <see cref="BuildGpuState"/> raises, matching the original event order); on failure
    /// backs off (bounded) and stays pending, or terminates the episode (throw for Genuine, reschedule
    /// for Wake).</summary>
    private void StepRecovery()
    {
        int attempt = _reacquire.BeginAttempt();
        try
        {
            AttemptAcquireOnceAndBuild(); // fresh manager + one TryAcquire/TryApply; builds GPU state on success
            _reacquire.Succeeded();
            Reacquired?.Invoke(attempt);
            _log?.Invoke(LogLevel.Info, $"re-acquired display target after {attempt} attempt(s)");
        }
        catch (TargetNotFoundException ex)
        {
            if (attempt == 1) TargetOffDesktop?.Invoke("display target not present");
            HandleAttemptFailure(attempt, ex);
        }
        catch (Exception ex)
        {
            _log?.Invoke(LogLevel.Warn, $"re-acquire attempt {attempt} failed: {ex.Message}");
            HandleAttemptFailure(attempt, ex);
        }
    }

    private void HandleAttemptFailure(int attempt, Exception last)
    {
        ReleaseGraphics(); // clean any partial state the failed attempt left before the next one
        switch (_reacquire.Failed())
        {
            case ReacquireFailAction.Backoff:
                // WATCHDOG BUDGET: this is the ONLY sleep on a recovering frame — one attempt plus this one
                // backoff, bounded by ReacquireBackoffMaxMs (~0.8s). The flag suppresses EndIdleFrame's idle
                // throttle so the two never stack, keeping the whole cycle well under ~1.5s. The next
                // attempt runs on the NEXT frame, so the caller heartbeats between attempts.
                Thread.Sleep(_reacquire.BackoffMs());
                _frameHadRecoveryBackoff = true;
                break;
            case ReacquireFailAction.Fatal:
                _log?.Invoke(LogLevel.Error, $"re-acquire exhausted after {attempt} attempts; exiting for supervisor restart: {last.Message}");
                throw new DisplayRecoveryFailedException(last.Message);
            case ReacquireFailAction.Reschedule:
                // Wake escalation exhausted — a sleeping panel is not fatal. Reschedule the next escalation
                // and stay not-owned + throttled; the idle frames keep the caller heartbeating.
                _nextWakeEscalationMs = _paceClock.Elapsed.TotalMilliseconds + _options.FenceDeadReacquireAfterMs;
                // A console-wake-triggered episode has a flavor the sentinel timer above ignores, so
                // re-arm the console deadline on the same cadence — a still-dark panel keeps retrying
                // while the desktop is awake instead of going silent until the next console transition.
                if (!WakeEscalation.FlavorEscalates(_fenceDeathFlavor)
                    && _consoleWatcher is not null && _options.FenceDeadReacquireAfterMs > 0)
                    _consoleWakeAtMs = _paceClock.Elapsed.TotalMilliseconds + _options.FenceDeadReacquireAfterMs;
                _log?.Invoke(LogLevel.Warn, $"wake escalation exhausted after {attempt} attempts; staying throttled, retry in {_options.FenceDeadReacquireAfterMs / 1000:0}s: {last.Message}");
                break;
        }
    }

    /// <summary>One acquire attempt: a fresh <see cref="DisplayManager"/>, a single target resolve +
    /// TryAcquire + mode find + TryApply, and on success the full GPU/display rebuild via
    /// <see cref="BuildGpuState"/>. This is the single-attempt sibling of <see cref="Acquire"/>'s inner
    /// loop (spread one attempt per frame per the watchdog budget) — keep the two in sync. Throws on any
    /// failure so <see cref="StepRecovery"/> counts it as a failed attempt.</summary>
    private void AttemptAcquireOnceAndBuild()
    {
        var (key, _) = SpecializedDisplays.ResolveKey(_selector);
        _mgr = DisplayManager.Create(DisplayManagerOptions.EnforceSourceOwnership);

        var target = SpecializedDisplays.ResolveTarget(_mgr, key, _selector);
        if (target is null)
            throw new TargetNotFoundException("display target not found (won't touch other displays).");

        var acq = _mgr.TryAcquireTargetsAndCreateEmptyState(new[] { target });
        if (acq.ErrorCode != DisplayManagerResult.Success)
        {
            // TargetAccessDenied == another client holds the source (VIDPN_SOURCE_IN_USE); surface it as
            // the acquire loop does, then fail this attempt (the next frame retries after the backoff).
            if (acq.ErrorCode == DisplayManagerResult.TargetAccessDenied)
                OwnershipLost?.Invoke($"acquire: {acq.ErrorCode}");
            throw new AcquireFailedException($"TryAcquire failed ({acq.ErrorCode}).");
        }

        var state = acq.State;
        try
        {
            var path = state.ConnectTarget(target);
            var mode = FindMatchingMode(path);
            if (mode is null)
                throw new ModeNotFoundException("no mode matched the configured selector.");

            path.ApplyPropertiesFromMode(mode);
            var status = ApplyStateForced(state);
            if (status != DisplayStateOperationStatus.Success)
                throw new AcquireFailedException($"TryApply failed ({status}).");

            BuildGpuState(target, mode);
        }
        catch (Exception) when (!ReferenceEquals(_target, target))
        {
            // Acquired but never installed as session state (_target unset, so the failure path's
            // ReleaseGraphics can't see it): hand it back here, or the OS keeps this process as the
            // owner and the wedged handoff outlives the recovery. BuildGpuState sets _target first,
            // so any throw past that point is covered by ReleaseGraphics instead.
            TryReleaseTargetQuiet(target);
            throw;
        }
    }

    /// <summary>Acquire (or re-acquire) exclusive ownership and rebuild all GPU/display state. Clears
    /// a prior cooperative <see cref="Release"/>. Raises <see cref="ModeApplied"/> then
    /// <see cref="Acquired"/>. Throws <see cref="TargetNotFoundException"/>, <see cref="ModeNotFoundException"/>,
    /// or <see cref="AcquireFailedException"/>.
    /// <para>This is the blocking initial/cooperative-resume path: it retries the inner TryAcquire/TryApply
    /// up to <see cref="AcquireOptions.AcquireAttempts"/> times WITHIN this one call (design decision #3 —
    /// callers invoke it explicitly, off the frame heartbeat). The spread, one-attempt-per-frame recovery
    /// and wake-escalation path uses <see cref="AttemptAcquireOnceAndBuild"/> instead.</para></summary>
    public void Acquire()
    {
        AssertThread();
        _released = false;
        _reacquire.Reset(); // an explicit (re)acquire supersedes any in-flight spread recovery episode

        var (key, _) = SpecializedDisplays.ResolveKey(_selector);
        _mgr = DisplayManager.Create(DisplayManagerOptions.EnforceSourceOwnership);

        DisplayTarget? target = null;
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

            var state = acq.State;
            var path = state.ConnectTarget(target);
            var mode = FindMatchingMode(path);
            if (mode is null)
            {
                TryReleaseTargetQuiet(target); // acquired this iteration; hand it back before bailing
                throw new ModeNotFoundException("no mode matched the configured selector.");
            }

            path.ApplyPropertiesFromMode(mode);
            var status = ApplyStateForced(state);
            if (status == DisplayStateOperationStatus.Success) { applied = mode; break; }

            if (attempt == _options.AcquireAttempts)
            {
                TryReleaseTargetQuiet(target); // ditto — owned but unusable
                throw new AcquireFailedException($"TryApply never succeeded (last={status}).");
            }
        }

        BuildGpuState(target!, applied!);
    }

    /// <summary>Apply the display state with <see cref="DisplayStateApplyOptions.ForceReapply"/> — a
    /// switch handoff re-applies the SAME mode the released owner was scanning, and without the force
    /// the OS treats the identical modeset as a no-op: the path is never re-driven and the glass never
    /// latches onto the new owner's surfaces (frozen old image before the explicit ReleaseTarget, black
    /// after it — hardware-traced 2026-07-06) while every present succeeds. Boot acquires latch because
    /// the path starts disabled, making the apply a real modeset; this makes every acquire look like
    /// boot. Falls back to a plain apply on failure so the previously-working acquire-while-display-off
    /// paths (wake escalation on a sleeping panel) are never regressed by the forced variant.</summary>
    private DisplayStateOperationStatus ApplyStateForced(DisplayState state)
    {
        var apply = state.TryApply(DisplayStateApplyOptions.FailIfStateChanged | DisplayStateApplyOptions.ForceReapply);
        if (apply.Status == DisplayStateOperationStatus.Success) return apply.Status;
        if (apply.Status == DisplayStateOperationStatus.SystemStateChanged)
        {
            // Stale snapshot (the released owner's hand-off races our acquire): let the caller's
            // retry loop rebuild the state and re-apply FORCED. Falling back here would "succeed"
            // as a no-op modeset and silently un-fix the handoff latch.
            _log?.Invoke(LogLevel.Warn, "forced mode re-apply hit SystemStateChanged; retrying with a fresh state");
            return apply.Status;
        }
        _log?.Invoke(LogLevel.Warn, $"forced mode re-apply failed ({apply.Status}); falling back to a plain apply");
        apply = state.TryApply(DisplayStateApplyOptions.FailIfStateChanged);
        return apply.Status;
    }

    /// <summary>Select the first enumerated mode matching <see cref="AcquireOptions.ModeSelector"/>, or
    /// null if none matches. Shared by <see cref="Acquire"/> and <see cref="AttemptAcquireOnceAndBuild"/>.</summary>
    private DisplayModeInfo? FindMatchingMode(DisplayPath path)
    {
        foreach (var mi in path.FindModes(DisplayModeQueryOptions.None))
        {
            var rate = mi.PresentationRate.VerticalSyncRate;
            double hz = rate.Denominator != 0 ? (double)rate.Numerator / rate.Denominator : 0.0;
            var desc = new DisplayModeDescriptor(
                mi.SourceResolution.Width, mi.SourceResolution.Height,
                mi.TargetResolution.Width, mi.TargetResolution.Height,
                mi.SourcePixelFormat, hz);
            if (_options.ModeSelector(desc)) return mi;
        }
        return null;
    }

    /// <summary>Rebuild all GPU/display state for a target whose mode has just been applied, and mark the
    /// session owned. Shared by the blocking <see cref="Acquire"/> and the spread
    /// <see cref="AttemptAcquireOnceAndBuild"/>. Raises <see cref="ModeApplied"/> then
    /// <see cref="Acquired"/>, and resets the force-redraw, pacing, fence-revive AND wake-escalation state
    /// so a re-acquired session never flips to a stale primary or carries a stale recovery timer.</summary>
    private void BuildGpuState(DisplayTarget target, DisplayModeInfo applied)
    {
        _target = target;

        // Physical framebuffer dims come from the applied mode (the source resolution we draw into).
        _physW = applied.SourceResolution.Width;
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
        LogSourceStatus("acquire");
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
        _fenceDeathFlavor = FenceDeathFlavor.None; _nextWakeEscalationMs = 0;             // fresh wake-escalation state
        _consoleWakeAtMs = 0;                                                             // fresh console-wake state
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
        _consoleWakeAtMs = 0;   // a parked session must not wake itself on a console transition
        _reacquire.Reset(); // abandon any in-flight spread recovery/escalation — the caller is parking us
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
        _activeDc = null; // drop the reference to the live context we are about to dispose (Dc falls back to _dc==null)
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
        // DisplayDevice/TaskPool/Source/Surface are WinRT but NOT IClosable: dropping the refs only
        // queues their native releases for finalization. So are the per-frame scanout + task
        // objects — hours of ownership accumulate millions of them. A re-acquiring process whose
        // OLD kernel display device (and scanout chain) is still alive at the next owner's mode
        // apply is the living-process handoff difference: process-first acquires (nothing to
        // finalize) latch on the glass, re-acquires don't. Finalize deterministically BEFORE
        // handing the target back so a released process looks as dead to the kernel as an exited
        // one. Runs only on release/recovery — never per-frame — and stays well under the ~5s
        // watchdog budget.
        _displayDevice = null!; _taskPool = null!; _source = null!; _primary = null!;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        // Hand the target back to the OS explicitly before dropping the manager.
        // DisplayManager.Dispose is documented to revoke ownership of all targets, but a
        // dispose-only release from a LIVING process leaves the glass scanning this session's last
        // primary: the next owner's acquire, mode apply, presents and fences all report success
        // while the panel never latches onto its scanout. Boot handoffs (previous owner process
        // dead) always latched. ReleaseTarget is the explicit ownership hand-off.
        TryReleaseTargetQuiet(_target);
        _target = null!;
        _mgr?.Dispose(); _mgr = null!;
    }

    /// <summary>Log the kernel's view of our scanout source (Active / PoweredOff / Invalid /
    /// OwnedByAnotherDevice / Unowned). Presents, fences and captures all read the FRAMEBUFFER and
    /// cannot see a panel that never latched; this is the only API-side signal that reflects the
    /// glass. Win11+ (UniversalApiContract v14) and best-effort only.</summary>
    private void LogSourceStatus(string when)
    {
        if (_source is null) return;
        try { _log?.Invoke(LogLevel.Info, $"scanout source status ({when}): {_source.Status}"); }
        catch { /* older contract without DisplaySource.Status — stay quiet */ }
    }

    /// <summary>Best-effort <see cref="DisplayManager.ReleaseTarget"/>: releasing a target we no
    /// longer own (or a stale one) throws, and no release path may fail because of that.</summary>
    private void TryReleaseTargetQuiet(DisplayTarget? target)
    {
        if (target is null || _mgr is null) return;
        try { _mgr.ReleaseTarget(target); }
        catch (Exception ex) { _log?.Invoke(LogLevel.Warn, $"ReleaseTarget failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        ReleaseGraphics();
        _consoleWatcher?.Dispose();
        _idle?.Dispose(); _idle = null; // the persistent not-owned fallback lives until the session is torn down
    }

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
