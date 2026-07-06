using System.Numerics;
using SpecializedDisplay;
using Xunit;

namespace SpecializedDisplay.Tests;

public class DisplayTransformTests
{
    // The panel's physical framebuffer.
    private const int PW = 2560, PH = 682;

    // Exact reconstruction of the renderer's original private ComputeTransform, with the logical
    // canvas literals it used (682 wide x 2560 tall at rot 90/270). The port must reproduce this
    // bit-for-bit for the rotations/flips the app exercises.
    private static Matrix3x2 Original(int rot, bool flipX, bool flipY)
    {
        const float canvasW = 682, canvasH = 2560;
        Matrix3x2 m = rot == 270
            ? new Matrix3x2(0, -1, 1, 0, 0, 682)
            : new Matrix3x2(0, 1, -1, 0, 2560, 0);
        if (flipX) m = new Matrix3x2(-1, 0, 0, 1, canvasW, 0) * m;
        if (flipY) m = new Matrix3x2(1, 0, 0, -1, 0, canvasH) * m;
        return m;
    }

    [Fact]
    public void Rot90_NoFlip_IsExactLiteral()
        => Assert.Equal(new Matrix3x2(0, 1, -1, 0, 2560, 0), DisplayTransform.Compute(90, false, false, PW, PH));

    [Fact]
    public void Rot270_NoFlip_IsExactLiteral()
        => Assert.Equal(new Matrix3x2(0, -1, 1, 0, 0, 682), DisplayTransform.Compute(270, false, false, PW, PH));

    [Theory]
    [InlineData(90, false, false)]
    [InlineData(90, true, false)]
    [InlineData(90, false, true)]
    [InlineData(90, true, true)]
    [InlineData(270, false, false)]
    [InlineData(270, true, false)]
    [InlineData(270, false, true)]
    [InlineData(270, true, true)]
    public void FlipCompositions_MatchOriginalHandBuiltMatrices(int rot, bool flipX, bool flipY)
        => Assert.Equal(Original(rot, flipX, flipY), DisplayTransform.Compute(rot, flipX, flipY, PW, PH));

    [Theory]
    [InlineData(0, 2560, 682)]     // no rotation: unchanged
    [InlineData(180, 2560, 682)]   // 180: unchanged
    [InlineData(90, 682, 2560)]    // 90: swapped
    [InlineData(270, 682, 2560)]   // 270: swapped
    public void LogicalSize_SwapRules(int rot, int expectedW, int expectedH)
    {
        var (w, h) = DisplayTransform.LogicalSize(rot, PW, PH);
        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
    }

    [Fact]
    public void Rot0_NoFlip_IsIdentity()
        => Assert.Equal(Matrix3x2.Identity, DisplayTransform.Compute(0, false, false, PW, PH));

    [Fact]
    public void Rot180_NoFlip_IsPointReflection()
        => Assert.Equal(new Matrix3x2(-1, 0, 0, -1, PW, PH), DisplayTransform.Compute(180, false, false, PW, PH));
}
