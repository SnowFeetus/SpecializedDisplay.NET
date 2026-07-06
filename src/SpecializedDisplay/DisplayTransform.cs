using System.Numerics;

namespace SpecializedDisplay;

/// <summary>
/// The root display transform that maps the caller's logical (portrait) canvas onto the physical
/// (landscape) scanout framebuffer. Exact generalization of the renderer's private
/// <c>ComputeTransform</c>: physical dimensions are parameters instead of the baked 2560×682
/// literals, and rotations 0/180 are supported in addition to the 90/270 the panel uses.
///
/// D2D uses the row-vector convention p' = p*M (<see cref="Matrix3x2"/> uses the same convention),
/// and flips are applied in logical space BEFORE rotation, so the composite is <c>flip * rot</c>.
/// This is also the matrix touch mapping inverts to reconcile raw digitizer coordinates.
/// </summary>
public static class DisplayTransform
{
    /// <summary>
    /// Compute the logical→physical transform. <paramref name="rotation"/> must be one of
    /// 0, 90, 180, 270 (other values are normalized modulo 360 and, if not one of the four,
    /// treated as 0). For rotation 90/270 with the panel's 2560×682 framebuffer this reproduces
    /// the original hand-built matrices exactly.
    /// </summary>
    public static Matrix3x2 Compute(int rotation, bool flipX, bool flipY,
        int physicalWidth, int physicalHeight)
    {
        int rot = Normalize(rotation);
        var (lw, lh) = LogicalSize(rot, physicalWidth, physicalHeight);

        // rot 90 (clockwise): (x,y) -> (W - y, x); rot 270 (ccw): (x,y) -> (y, H - x).
        Matrix3x2 m = rot switch
        {
            90  => new Matrix3x2(0, 1, -1, 0, physicalWidth, 0),
            270 => new Matrix3x2(0, -1, 1, 0, 0, physicalHeight),
            180 => new Matrix3x2(-1, 0, 0, -1, physicalWidth, physicalHeight),
            _   => Matrix3x2.Identity,   // rot 0: logical == physical
        };
        if (flipX) m = new Matrix3x2(-1, 0, 0, 1, lw, 0) * m; // mirror logical x
        if (flipY) m = new Matrix3x2(1, 0, 0, -1, 0, lh) * m; // mirror logical y
        return m;
    }

    /// <summary>The logical canvas dimensions for a given rotation: swapped from physical at 90/270,
    /// unchanged at 0/180.</summary>
    public static (int W, int H) LogicalSize(int rotation, int physicalWidth, int physicalHeight)
    {
        int rot = Normalize(rotation);
        return (rot == 90 || rot == 270)
            ? (physicalHeight, physicalWidth)
            : (physicalWidth, physicalHeight);
    }

    // Canonicalize to 0/90/180/270; anything else collapses to 0.
    internal static int Normalize(int rotation)
    {
        int r = ((rotation % 360) + 360) % 360;
        return r is 90 or 180 or 270 ? r : 0;
    }
}
