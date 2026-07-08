using System.Runtime.InteropServices;

namespace SpecializedDisplay;

/// <summary>
/// Watches the console display power state (GUID_CONSOLE_DISPLAY_STATE) through
/// <c>PowerSettingRegisterNotification</c>'s callback flavor, which needs no window or message pump
/// (the render host is headless).
///
/// <para><b>Why the session needs this (hardware-traced 2026-07-06):</b> when the Windows display
/// idle timeout fires, EVERY display powers down — including an exclusively-owned specialized
/// target. But on desktop wake Windows re-powers only the monitors in the desktop topology; a
/// specialized target has no desktop presence, so nothing ever re-lights it. The session sat in the
/// display-off throttle for 14 hours while the desktop was awake, because the only signal it
/// watches (a blocking vblank) can never arrive on a target whose scanout stays powered down. The
/// fix is to watch the CONSOLE display state: when it transitions back to ON while the session is
/// display-off throttled, the session release + re-acquires, and the mode re-apply re-powers the
/// panel's scanout (a Release()+Acquire() re-lighting a dark panel on an awake desktop is
/// hardware-verified).</para>
///
/// <para>The OS callback arrives on a system thread and only touches interlocked ints; the session
/// polls <see cref="ConsumeDisplayTurnedOn"/> from the render thread.</para>
/// </summary>
internal sealed class ConsoleDisplayWatcher : IDisposable
{
    // GUID_CONSOLE_DISPLAY_STATE. Payload DWORD: 0 = off, 1 = on, 2 = dimmed.
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private const uint DEVICE_NOTIFY_CALLBACK = 2;
    private const uint PBT_POWERSETTINGCHANGE = 0x8013;

    private readonly DeviceNotifyCallbackRoutine _callback; // rooted: the OS holds this pointer
    private IntPtr _registration;
    private int _displayOn = -1; // -1 until the registration's initial state callback, then 0/1
    private int _turnedOn;       // latched on a known-off -> on transition

    public ConsoleDisplayWatcher()
    {
        _callback = OnPowerSetting;
        var subscribe = new DeviceNotifySubscribeParameters
        {
            Callback = Marshal.GetFunctionPointerForDelegate(_callback),
            Context = IntPtr.Zero,
        };
        uint err = PowerSettingRegisterNotification(in ConsoleDisplayState, DEVICE_NOTIFY_CALLBACK,
            in subscribe, out _registration);
        if (err != 0)
            throw new InvalidOperationException($"PowerSettingRegisterNotification failed (Win32 error {err})");
    }

    /// <summary>True exactly once after the console display transitions from known-off to on. The
    /// initial state delivered at registration never counts as a transition, so a session that
    /// starts with the display already on sees no spurious hint.</summary>
    public bool ConsumeDisplayTurnedOn() => Interlocked.Exchange(ref _turnedOn, 0) == 1;

    private uint OnPowerSetting(IntPtr context, uint type, IntPtr setting)
    {
        // Never let an exception cross into the OS power-notification dispatcher.
        try
        {
            if (type != PBT_POWERSETTINGCHANGE || setting == IntPtr.Zero) return 0;
            // POWERBROADCAST_SETTING: GUID PowerSetting (16 bytes), DWORD DataLength, UCHAR Data[].
            // Only one GUID is registered, so the payload is always the console-display-state DWORD.
            if (Marshal.ReadInt32(setting, 16) < sizeof(int)) return 0;
            int on = Marshal.ReadInt32(setting, 20) != 0 ? 1 : 0; // dimmed still scans out -> "on"
            int prev = Interlocked.Exchange(ref _displayOn, on);
            if (on == 1 && prev == 0)
                Interlocked.Exchange(ref _turnedOn, 1);
        }
        catch { }
        return 0;
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            PowerSettingUnregisterNotification(_registration);
            _registration = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSettingRegisterNotification(in Guid settingGuid, uint flags,
        in DeviceNotifySubscribeParameters recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSettingUnregisterNotification(IntPtr registrationHandle);
}
