using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace SpecializedDisplay;

/// <summary>
/// A standalone, discarded D2D draw target used ONLY while the session is not owned — i.e. between the
/// acquire attempts of a spread-across-frames reacquire or wake escalation. The consumer's render loop
/// draws unconditionally after <c>BeginFrame</c>; when ownership isn't (yet) restored there is no live
/// display context to hand it, so we hand it this throwaway target and never scan the result out.
///
/// <para>Built on the <b>WARP</b> software rasterizer, deliberately: it is independent of the display
/// adapter, so it stays valid even through a real device-removed of that adapter (the exact condition
/// under which a not-owned frame must remain safe). It is created lazily on the first not-owned frame
/// and lives for the rest of the session (a small, persistent fallback), disposed with the session.</para>
///
/// <para>Mirrors the proven offscreen D3D11 + D2D target pattern used by the renderer's screenshot
/// harness (WARP device → render-target texture → D2D bitmap-from-DXGI-surface).</para>
/// </summary>
internal sealed class IdleCanvas : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11Texture2D _rt;
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _dc;
    private readonly ID2D1Bitmap1 _target;

    /// <summary>The context the consumer draws onto while not owned. Its target is a discarded texture.</summary>
    public ID2D1DeviceContext Context => _dc;

    public IdleCanvas(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels,
            out _device, out _ctx).CheckError();

        var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None);
        _rt = _device.CreateTexture2D(desc);

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded);
        _d2dDevice = _factory.CreateDevice(dxgiDevice);
        _dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        using var surface = _rt.QueryInterface<IDXGISurface>();
        var props = new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Ignore),
            96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw);
        _target = _dc.CreateBitmapFromDxgiSurface(surface, props);
    }

    /// <summary>Begin a throwaway frame with the same target/transform contract as a live frame, so the
    /// caller's draw code behaves identically (it just lands on a surface that is never presented).</summary>
    public void Begin(Matrix3x2 transform)
    {
        _dc.Target = _target;
        _dc.BeginDraw();
        _dc.Transform = transform;
        _dc.TextAntialiasMode = TextAntialiasMode.Grayscale;
    }

    /// <summary>End and discard the throwaway frame. Any EndDraw error is irrelevant to a surface that is
    /// never read or presented, so it is swallowed — a not-owned frame must never take the loop down.</summary>
    public void End()
    {
        try { _dc.EndDraw(); }
        catch { /* discarded target; nothing depends on the result */ }
    }

    public void Dispose()
    {
        _target?.Dispose();
        _dc?.Dispose();
        _d2dDevice?.Dispose();
        _factory?.Dispose();
        _rt?.Dispose();
        _ctx?.Dispose();
        _device?.Dispose();
    }
}
