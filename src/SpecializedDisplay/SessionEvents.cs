using Windows.Graphics.DirectX;

namespace SpecializedDisplay;

/// <summary>How ownership of the display was lost, mirroring the two recovery classes the present
/// path distinguishes: a removed/hung/reset device vs. a lost source (another client took it).</summary>
public enum DisplayLossKind { DeviceRemoved, OwnershipLost }

/// <summary>The mode actually applied to the target. Carries the structured dimensions/format/refresh
/// so the host can format its own IPC detail string (the renderer's <c>"2560x682 BGRA @60"</c>
/// literal is produced app-side, keeping the supervisor-visible line byte-identical).</summary>
public sealed record AppliedMode(int Width, int Height, DirectXPixelFormat Format, double RefreshHz);

/// <summary>A captured frame in upright logical orientation (the display transform has been inverted
/// per-pixel). Alpha is forced opaque (the scanout target is <c>AlphaMode.Ignore</c>). PNG encoding
/// is the caller's responsibility.</summary>
public sealed record CapturedFrame(int Width, int Height, byte[] Bgra);
