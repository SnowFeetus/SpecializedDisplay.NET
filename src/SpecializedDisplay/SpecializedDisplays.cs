using Windows.Devices.Display;
using Windows.Devices.Display.Core;
using Windows.Devices.Enumeration;

namespace SpecializedDisplay;

// Adapter LUID + target id that keys a DisplayMonitor to a DisplayTarget (Spike 1 correlation).
internal readonly record struct MonitorKey(long High, uint Low, uint TargetId);

/// <summary>
/// Static entry point: enumerate connected monitors (diagnostics) and locate a target via a
/// <see cref="DisplaySelector"/>. The two-phase resolution mirrors the renderer's
/// <c>FindY70tiKey</c> (phase 1: DisplayMonitor correlation, last-match-wins, exceptions swallowed
/// to a null key) + <c>FindTarget</c> (phase 2: DisplayTarget StableMonitorId fallback).
/// </summary>
public static class SpecializedDisplays
{
    /// <summary>All currently-connected monitors, projected to plain data. Diagnostics only; errors
    /// are swallowed and a partial (possibly empty) list is returned.</summary>
    public static IReadOnlyList<DisplayMonitorInfo> EnumerateMonitors()
    {
        var list = new List<DisplayMonitorInfo>();
        try
        {
            var infos = DeviceInformation.FindAllAsync(DisplayMonitor.GetDeviceSelector()).GetAwaiter().GetResult();
            foreach (var di in infos)
            {
                DisplayMonitor? mon = null;
                try { mon = DisplayMonitor.FromInterfaceIdAsync(di.Id).GetAwaiter().GetResult(); } catch { }
                if (mon is null) continue;
                list.Add(Project(mon));
            }
        }
        catch { /* diagnostics only — return whatever was collected */ }
        return list;
    }

    /// <summary>Locate the target now. Returns null if it is not currently connected.</summary>
    public static SpecializedDisplayTarget? Find(DisplaySelector selector)
    {
        var (key, matched) = ResolveKey(selector);
        var mgr = DisplayManager.Create(DisplayManagerOptions.EnforceSourceOwnership);
        try
        {
            return ResolveTarget(mgr, key, selector) is null
                ? null
                : new SpecializedDisplayTarget(selector, matched);
        }
        finally { mgr.Dispose(); }
    }

    /// <summary>Locate the target now, throwing <see cref="TargetNotFoundException"/> if absent.</summary>
    public static SpecializedDisplayTarget Require(DisplaySelector selector)
        => Find(selector) ?? throw new TargetNotFoundException("display target not found.");

    // ---- shared resolution (used by Find and by every session (re)acquire) ----

    // Phase 1: DisplayMonitor correlation. LAST match wins (no break; risk register #1). The whole
    // enumeration is guarded — on failure the key is null and phase 2 (MatchTarget) takes over.
    internal static (MonitorKey? key, DisplayMonitorInfo? matched) ResolveKey(DisplaySelector selector)
    {
        if (selector.MatchMonitor is not { } matchMonitor) return (null, null);

        MonitorKey? key = null;
        DisplayMonitorInfo? matched = null;
        try
        {
            var infos = DeviceInformation.FindAllAsync(DisplayMonitor.GetDeviceSelector()).GetAwaiter().GetResult();
            foreach (var di in infos)
            {
                DisplayMonitor? mon = null;
                try { mon = DisplayMonitor.FromInterfaceIdAsync(di.Id).GetAwaiter().GetResult(); } catch { }
                if (mon is null) continue;
                var info = Project(mon);
                if (matchMonitor(info))
                {
                    key = new MonitorKey(info.AdapterIdHighPart, info.AdapterIdLowPart, info.AdapterTargetId);
                    matched = info;
                }
            }
        }
        catch { /* fall back to StableMonitorId matching in ResolveTarget */ }
        return (key, matched);
    }

    // Phase 2: match a live connected target by adapter/target key, else by the MatchTarget predicate
    // (StableMonitorId). Reading StableMonitorId can throw for some targets — guarded to a no-match.
    internal static DisplayTarget? ResolveTarget(DisplayManager mgr, MonitorKey? key, DisplaySelector selector)
    {
        foreach (var t in mgr.GetCurrentTargets())
        {
            if (!t.IsConnected) continue;
            long ah = t.Adapter.Id.HighPart; uint al = t.Adapter.Id.LowPart; uint arid = t.AdapterRelativeId;
            bool m = key is { } k && ah == k.High && al == k.Low && arid == k.TargetId;
            if (!m && selector.MatchTarget is { } matchTarget)
            {
                try { m = matchTarget(new DisplayTargetInfo(t.StableMonitorId, ah, al, arid, t.IsConnected)); }
                catch { /* StableMonitorId unavailable → treat as no-match */ }
            }
            if (m) return t;
        }
        return null;
    }

    private static DisplayMonitorInfo Project(DisplayMonitor mon)
        => new(mon.DeviceId, mon.DisplayName,
               mon.DisplayAdapterId.HighPart, mon.DisplayAdapterId.LowPart, mon.DisplayAdapterTargetId,
               mon.NativeResolutionInRawPixels.Width, mon.NativeResolutionInRawPixels.Height);
}

/// <summary>
/// A handle that remembers HOW to find the target (the <see cref="DisplaySelector"/>), not a live
/// WinRT object — required because every (re)acquire recreates the <c>DisplayManager</c> and must
/// re-resolve the target. Obtained from <see cref="SpecializedDisplays.Find"/>/<see cref="SpecializedDisplays.Require"/>.
/// </summary>
public sealed class SpecializedDisplayTarget
{
    public DisplaySelector Selector { get; }

    /// <summary>The monitor that matched at <see cref="SpecializedDisplays.Find"/> time; informational
    /// (null if resolution fell back to the target-only phase).</summary>
    public DisplayMonitorInfo? LastKnownMonitor { get; }

    internal SpecializedDisplayTarget(DisplaySelector selector, DisplayMonitorInfo? lastKnownMonitor)
    {
        Selector = selector;
        LastKnownMonitor = lastKnownMonitor;
    }

    /// <summary>Create a session bound to this target. Does NOT acquire — subscribe to events first,
    /// then call <see cref="ExclusiveDisplaySession.Acquire"/> so the initial mode/acquired events
    /// are not missed.</summary>
    public ExclusiveDisplaySession CreateSession(AcquireOptions options)
        => new(Selector, options);

    /// <summary>Convenience: <see cref="CreateSession"/> + <see cref="ExclusiveDisplaySession.Acquire"/>.
    /// A consumer that subscribes after this returns will miss the initial mode/acquired events.</summary>
    public ExclusiveDisplaySession AcquireExclusive(AcquireOptions options)
    {
        var session = CreateSession(options);
        session.Acquire();
        return session;
    }
}
