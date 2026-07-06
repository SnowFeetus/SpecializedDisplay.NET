using System.Numerics;
using SpecializedDisplay;
using Xunit;

namespace SpecializedDisplay.Tests;

public class FrameCaptureRemapTests
{
    // Small synthetic physical framebuffer so each coordinate fits in a byte.
    private const int PW = 8, PH = 4;

    // Each physical pixel encodes its own (px, py) in the B and G channels.
    private static byte[] MakePhysical()
    {
        var phys = new byte[PW * PH * 4];
        for (int py = 0; py < PH; py++)
        for (int px = 0; px < PW; px++)
        {
            int i = (py * PW + px) * 4;
            phys[i]     = (byte)px; // B
            phys[i + 1] = (byte)py; // G
            phys[i + 2] = 0;        // R
            phys[i + 3] = 123;      // A (remap forces 255)
        }
        return phys;
    }

    // Capture remap must be the exact inverse of DisplayTransform.Compute: for every logical pixel,
    // forward-transform its center with Compute (floor to a physical pixel), and assert the remapped
    // logical buffer holds exactly that physical pixel's encoded coordinates. Round-trip = identity.
    [Theory]
    [InlineData(90, false, false)]
    [InlineData(90, true, false)]
    [InlineData(90, false, true)]
    [InlineData(90, true, true)]
    [InlineData(270, false, false)]
    [InlineData(270, true, false)]
    [InlineData(270, false, true)]
    [InlineData(270, true, true)]
    [InlineData(0, false, false)]
    [InlineData(180, true, true)]
    public void Remap_InvertsCompute(int rot, bool flipX, bool flipY)
    {
        var phys = MakePhysical();
        var logical = FrameCapture.Remap(phys, PW, PH, rot, flipX, flipY);

        var m = DisplayTransform.Compute(rot, flipX, flipY, PW, PH);
        var (lw, lh) = DisplayTransform.LogicalSize(rot, PW, PH);

        for (int ly = 0; ly < lh; ly++)
        for (int lx = 0; lx < lw; lx++)
        {
            // Forward-transform the logical pixel CENTER to the physical pixel it was drawn to.
            var p = Vector2.Transform(new Vector2(lx + 0.5f, ly + 0.5f), m);
            int epx = (int)MathF.Floor(p.X);
            int epy = (int)MathF.Floor(p.Y);

            Assert.InRange(epx, 0, PW - 1);
            Assert.InRange(epy, 0, PH - 1);

            int di = (ly * lw + lx) * 4;
            Assert.Equal((byte)epx, logical[di]);     // B carries px
            Assert.Equal((byte)epy, logical[di + 1]); // G carries py
            Assert.Equal(255, logical[di + 3]);       // A forced opaque
        }
    }

    [Fact]
    public void Remap_ProducesLogicalSizedBuffer()
    {
        var phys = MakePhysical();
        var logical = FrameCapture.Remap(phys, PW, PH, 90, false, false);
        var (lw, lh) = DisplayTransform.LogicalSize(90, PW, PH);
        Assert.Equal(lw * lh * 4, logical.Length);
    }
}
