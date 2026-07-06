using System.Runtime.InteropServices;
using Windows.Devices.Display.Core;

namespace SpecializedDisplay;

/// <summary>
/// Native access to <c>IDisplayDeviceInterop</c> (not projected by CsWinRT). This interface derives
/// from <c>IUnknown</c> (NOT <c>IInspectable</c>), so its methods live at vtable slots 3
/// (<c>CreateSharedHandle</c>) and 4 (<c>OpenSharedHandle</c>) — proven in Spike 2.
/// </summary>
internal static class DisplayInterop
{
    private static readonly Guid IID_IDisplayDeviceInterop = new("64338358-366A-471B-BD56-DD8EF48E439B");

    // The display primary AND display fences require GENERIC_ALL access when creating the shared
    // handle — DXGI_SHARED_RESOURCE_READ|WRITE returns E_INVALIDARG (Spike 2 hard-won finding).
    private const uint GENERIC_ALL = 0x10000000;

    /// <summary>
    /// Create an NT shared handle for a display object (a <see cref="DisplaySurface"/> primary or a
    /// <see cref="DisplayFence"/>). The caller owns the handle and must CloseHandle it after opening.
    /// </summary>
    public static unsafe IntPtr CreateSharedHandle<T>(DisplayDevice device, T inspectable) where T : class
    {
        var iid = IID_IDisplayDeviceInterop;
        IntPtr pDev = WinRT.MarshalInspectable<DisplayDevice>.FromManaged(device);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(pDev, in iid, out IntPtr pInterop));
            try
            {
                IntPtr pObj = WinRT.MarshalInspectable<T>.FromManaged(inspectable);
                try
                {
                    void** vtbl = *(void***)pInterop;
                    // slot 3: CreateSharedHandle(this, IInspectable* obj, SECURITY_ATTRIBUTES*, DWORD access, HSTRING name, HANDLE* out)
                    var createShared = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, IntPtr, IntPtr*, int>)vtbl[3];
                    IntPtr handle;
                    Marshal.ThrowExceptionForHR(createShared(pInterop, pObj, IntPtr.Zero, GENERIC_ALL, IntPtr.Zero, &handle));
                    return handle;
                }
                finally { Marshal.Release(pObj); }
            }
            finally { Marshal.Release(pInterop); }
        }
        finally { Marshal.Release(pDev); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr h);
}
