namespace SpecializedDisplay;

/// <summary>
/// The requested display target could not be located among the currently-connected targets.
/// Thrown by <see cref="SpecializedDisplays.Require"/> and, during (re)acquire, when the target has
/// dropped off the desktop. In the original renderer this was a private nested type; it is public
/// here because callers (and the bounded-reacquire loop) branch on it explicitly.
/// </summary>
public sealed class TargetNotFoundException(string msg) : Exception(msg);

/// <summary>No display mode matching the configured <see cref="AcquireOptions.ModeSelector"/> was
/// found on the target's path. Thrown immediately (no retry) — a missing mode is not transient.</summary>
public sealed class ModeNotFoundException(string msg) : Exception(msg);

/// <summary>Acquire's inner attempt loop exhausted without a successful <c>TryApply</c> (or another
/// non-transient failure occurred while binding the target).</summary>
public sealed class AcquireFailedException(string msg) : Exception(msg);

/// <summary>The bounded in-process reacquire loop exhausted its attempts. This propagates out of
/// <see cref="ExclusiveDisplaySession.BeginFrame"/> so the host process can exit and let its
/// supervisor own the persistent-failure restart (with backoff + crash-loop breaking). Named
/// <c>RecoveryFailedException</c> in the original renderer.</summary>
public sealed class DisplayRecoveryFailedException(string msg) : Exception(msg);
