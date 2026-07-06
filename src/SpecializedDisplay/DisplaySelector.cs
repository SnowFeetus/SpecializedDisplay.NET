namespace SpecializedDisplay;

/// <summary>A <c>DisplayMonitor</c> projected into plain data for phase-1 correlation. Mirrors the
/// fields the renderer read: identity strings plus the adapter LUID / target id used to key a
/// <see cref="DisplayTargetInfo"/>.</summary>
public sealed record DisplayMonitorInfo(
    string DeviceId, string DisplayName,
    long AdapterIdHighPart, uint AdapterIdLowPart, uint AdapterTargetId,
    int NativeWidth, int NativeHeight);

/// <summary>A <c>DisplayTarget</c> projected into plain data for phase-2 fallback matching (by
/// <see cref="StableMonitorId"/>).</summary>
public sealed record DisplayTargetInfo(
    string? StableMonitorId,
    long AdapterIdHighPart, uint AdapterIdLowPart, uint AdapterRelativeId,
    bool IsConnected);

/// <summary>
/// Two-phase target selector, mirroring the renderer's <c>FindY70tiKey</c>/<c>FindTarget</c> pair.
/// Phase 1 correlates a <c>DisplayMonitor</c> (identity strings) to an adapter/target key;
/// phase 2 falls back to matching a live <c>DisplayTarget</c> by its <c>StableMonitorId</c> when the
/// monitor enumeration produced no key. Both predicates are optional but at least one must match for
/// a target to be selected.
/// </summary>
public sealed class DisplaySelector
{
    /// <summary>Phase 1: correlate by <see cref="DisplayMonitorInfo"/>. Enumeration takes the LAST
    /// matching monitor (see risk register #1), so this predicate may match several monitors.</summary>
    public Func<DisplayMonitorInfo, bool>? MatchMonitor { get; init; }

    /// <summary>Phase 2 fallback: match a connected <see cref="DisplayTargetInfo"/> directly, used
    /// when phase 1 yielded no adapter key (e.g. the monitor enumeration threw and was swallowed).</summary>
    public Func<DisplayTargetInfo, bool>? MatchTarget { get; init; }
}
