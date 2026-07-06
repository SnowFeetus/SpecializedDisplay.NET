// PresentSample — a spike2-shaped on-hardware smoke tool for SpecializedDisplay.NET.
//
// Proves the full pipeline AND the NEW cooperative Release/Acquire path end to end:
//   find -> subscribe -> Acquire -> solid clear-color frames ~5s
//        -> Release() -> wait 3s (panel free) -> Acquire() -> render ~5s -> Dispose.
//
// Every session event is printed as it fires, so the console log is a live proof of event ORDER
// (ModeApplied before Acquired; Released on cooperative release; ModeApplied/Acquired again on the
// second Acquire). Selector is built inline (RTK409A / HYTE / Y70) — no profile-repo dependency.
//
// Run on the Y70ti hardware only. It takes exclusive ownership of the panel while it runs.

using System.Diagnostics;
using SpecializedDisplay;
using Vortice.Mathematics;
using Windows.Graphics.DirectX;

Action<LogLevel, string> log = (level, msg) => Console.WriteLine($"  [log:{level}] {msg}");

Console.WriteLine("=== PresentSample: cooperative Release/Acquire proof on the specialized panel ===\n");

// Inline selector: correlate the Y70ti by monitor identity, fall back to StableMonitorId.
var selector = new DisplaySelector
{
    MatchMonitor = m =>
        m.DeviceId.Contains("RTK409A", StringComparison.OrdinalIgnoreCase) ||
        m.DisplayName.Contains("HYTE", StringComparison.OrdinalIgnoreCase) ||
        m.DisplayName.Contains("Y70", StringComparison.OrdinalIgnoreCase),
    MatchTarget = t =>
        t.StableMonitorId is { } sid && sid.Contains("RTK409A", StringComparison.OrdinalIgnoreCase),
};

var options = new AcquireOptions
{
    ModeSelector = ModeSelectors.NativeOneToOne(2560, 682, DirectXPixelFormat.B8G8R8A8UIntNormalized),
    Rotation = 90,
    Log = log,
};

var target = SpecializedDisplays.Find(selector);
if (target is null)
{
    Console.WriteLine("target not found; aborting (won't touch other displays).");
    return 3;
}
Console.WriteLine($"found target; last-known monitor: {target.LastKnownMonitor?.DisplayName ?? "(target-only match)"}\n");

var session = target.CreateSession(options);

// Subscribe BEFORE the first Acquire so the initial ModeApplied/Acquired are observed (event order).
session.ModeApplied += m => Console.WriteLine($"[event] ModeApplied {m.Width}x{m.Height} {m.Format} @{m.RefreshHz:0.###}Hz");
session.Acquired += () => Console.WriteLine("[event] Acquired");
session.Reacquired += n => Console.WriteLine($"[event] Reacquired after {n} attempt(s)");
session.OwnershipLost += d => Console.WriteLine($"[event] OwnershipLost: {d}");
session.DeviceRemoved += d => Console.WriteLine($"[event] DeviceRemoved: {d}");
session.TargetOffDesktop += d => Console.WriteLine($"[event] TargetOffDesktop: {d}");
session.Released += () => Console.WriteLine("[event] Released");

try
{
    Console.WriteLine(">>> Acquire #1");
    session.Acquire();

    Console.WriteLine(">>> Rendering ~5s. LOOK AT THE PANEL (cycling colors).");
    RenderFor(session, 5);

    Console.WriteLine(">>> Release() — cooperative give-up (panel should go free)");
    session.Release();
    Console.WriteLine($"    IsOwned={session.IsOwned} IsReleased={session.IsReleased}");

    Console.WriteLine(">>> Waiting 3s while released");
    Thread.Sleep(3000);

    Console.WriteLine(">>> Acquire #2 — re-take the panel after release");
    session.Acquire();
    Console.WriteLine($"    IsOwned={session.IsOwned} IsReleased={session.IsReleased}");

    Console.WriteLine(">>> Rendering ~5s again (proves no stale-buffer garbage post-release)");
    RenderFor(session, 5);
}
finally
{
    Console.WriteLine(">>> Dispose");
    session.Dispose();
}

Console.WriteLine("\nPresentSample complete.");
return 0;

// Clear the whole primary to a cycling solid color every frame (each frame is a full redraw, so the
// force-redraw contract is trivially satisfied). Pacing is handled inside EndFramePresent.
static void RenderFor(ExclusiveDisplaySession session, double seconds)
{
    var colors = new[]
    {
        new Color4(0.85f, 0.10f, 0.10f, 1f), // red
        new Color4(0.10f, 0.75f, 0.20f, 1f), // green
        new Color4(0.15f, 0.35f, 0.95f, 1f), // blue
        new Color4(0.95f, 0.75f, 0.10f, 1f), // amber
    };
    var sw = Stopwatch.StartNew();
    long frames = 0;
    int lastIdx = -1;
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        int idx = (int)(sw.Elapsed.TotalSeconds / 1.0) % colors.Length;
        session.BeginFrame();
        session.Dc.Clear(colors[idx]);
        session.EndFramePresent();
        frames++;
        if (idx != lastIdx)
        {
            Console.WriteLine($"    t={sw.Elapsed.TotalSeconds:0.0}s color#{idx} frames={frames}");
            lastIdx = idx;
        }
    }
    Console.WriteLine($"    rendered {frames} frames (~{frames / sw.Elapsed.TotalSeconds:0} fps).");
}
