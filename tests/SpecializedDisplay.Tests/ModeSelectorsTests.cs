using SpecializedDisplay;
using Windows.Graphics.DirectX;
using Xunit;

namespace SpecializedDisplay.Tests;

public class ModeSelectorsTests
{
    private static readonly Func<DisplayModeDescriptor, bool> Native =
        ModeSelectors.NativeOneToOne(2560, 682, DirectXPixelFormat.B8G8R8A8UIntNormalized);

    private static DisplayModeDescriptor Mode(int sw, int sh, int tw, int th,
        DirectXPixelFormat fmt = DirectXPixelFormat.B8G8R8A8UIntNormalized, double hz = 60.001)
        => new(sw, sh, tw, th, fmt, hz);

    [Fact]
    public void Accepts_Native1To1Bgra()
        => Assert.True(Native(Mode(2560, 682, 2560, 682)));

    [Fact]
    public void Accepts_RegardlessOfRefreshRate()
        => Assert.True(Native(Mode(2560, 682, 2560, 682, hz: 59.94)));

    [Fact]
    public void Rejects_ScaledSourceSmallerThanTarget()
        => Assert.False(Native(Mode(1176, 664, 2560, 682))); // RTK409A upscaled mode (display-findings.md)

    [Fact]
    public void Rejects_WrongPixelFormat()
        => Assert.False(Native(Mode(2560, 682, 2560, 682, DirectXPixelFormat.R8G8B8A8UIntNormalized)));

    [Fact]
    public void Rejects_WrongResolution()
        => Assert.False(Native(Mode(1920, 1080, 1920, 1080)));

    [Fact]
    public void Rejects_TargetMismatch()
        => Assert.False(Native(Mode(2560, 682, 2560, 720)));
}
