using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SpecializedDisplay;

/// <summary>
/// Reads back the just-drawn back primary and produces an UPRIGHT logical-orientation BGRA frame.
/// The physical framebuffer is landscape with the portrait UI baked in by the rot90/270+flip display
/// transform; the remap inverts that transform per-pixel so the output is the logical canvas the
/// viewer actually sees on the mounted panel. Exact pixel permutation (no sampling). PNG encoding is
/// the caller's job. Ported from <c>Y70tiBackend.CaptureIfPending</c> (dimensions parameterized).
/// </summary>
internal static class FrameCapture
{
    /// <summary>Copy the completed back primary into a CPU-readable staging texture, lift it into a
    /// tight managed buffer, and remap to upright logical orientation.</summary>
    public static CapturedFrame Capture(
        ID3D11Device d3dDevice, ID3D11DeviceContext d3dContext, ID3D11Texture2D backPrimary,
        int physicalWidth, int physicalHeight, int rotation, bool flipX, bool flipY)
    {
        int pw = physicalWidth, ph = physicalHeight;   // physical framebuffer (landscape)

        // Copy the completed back primary into a CPU-readable staging texture (drop the shared/misc
        // flags — a staging resource can't be shared) and map it pitch-aware.
        var stagingDesc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)pw, (uint)ph, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
        using var staging = d3dDevice.CreateTexture2D(stagingDesc);
        d3dContext.CopyResource(staging, backPrimary);
        d3dContext.Flush();

        var map = d3dContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int pitch = (int)map.RowPitch;
            // 1) lift the whole physical frame into a tight managed buffer (few hundred Marshal.Copy
            //    calls, pitch-aware) so the remap below is pure fast array indexing.
            var phys = new byte[pw * ph * 4];
            var rowbuf = new byte[pw * 4];
            for (int y = 0; y < ph; y++)
            {
                Marshal.Copy(IntPtr.Add(map.DataPointer, y * pitch), rowbuf, 0, pw * 4);
                Array.Copy(rowbuf, 0, phys, y * pw * 4, pw * 4);
            }

            var (lw, lh) = DisplayTransform.LogicalSize(rotation, pw, ph);
            var portrait = Remap(phys, pw, ph, rotation, flipX, flipY);
            return new CapturedFrame(lw, lh, portrait);
        }
        finally { d3dContext.Unmap(staging, 0); }
    }

    /// <summary>Pure inverse-transform pixel permutation: for each logical pixel, apply the SAME
    /// flip-then-rotate the renderer used to find the physical pixel it was drawn to, and copy it.
    /// No hardware; unit-tested for round-trip identity against <see cref="DisplayTransform.Compute"/>.</summary>
    internal static byte[] Remap(byte[] phys, int physicalWidth, int physicalHeight,
        int rotation, bool flipX, bool flipY)
    {
        int pw = physicalWidth, ph = physicalHeight;
        int rot = DisplayTransform.Normalize(rotation);
        var (lw, lh) = DisplayTransform.LogicalSize(rot, pw, ph);

        var portrait = new byte[lw * lh * 4];
        for (int ly = 0; ly < lh; ly++)
        {
            int lye = flipY ? (lh - 1 - ly) : ly;
            for (int lx = 0; lx < lw; lx++)
            {
                int lxe = flipX ? (lw - 1 - lx) : lx;
                int px, py;
                switch (rot)
                {
                    case 270: px = lye;            py = (ph - 1) - lxe; break; // (x,y) -> (y, H - x)
                    case 90:  px = (pw - 1) - lye; py = lxe;            break; // (x,y) -> (W - y, x)
                    case 180: px = (pw - 1) - lxe; py = (ph - 1) - lye; break; // (x,y) -> (W - x, H - y)
                    default:  px = lxe;            py = lye;            break; // rot 0: identity
                }
                int si = (py * pw + px) * 4;
                int di = (ly * lw + lx) * 4;
                portrait[di]     = phys[si];     // B
                portrait[di + 1] = phys[si + 1]; // G
                portrait[di + 2] = phys[si + 2]; // R
                portrait[di + 3] = 255;          // A (target is AlphaMode.Ignore)
            }
        }
        return portrait;
    }
}
