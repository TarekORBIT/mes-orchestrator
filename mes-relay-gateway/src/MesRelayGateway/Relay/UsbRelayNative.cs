using System.Runtime.InteropServices;

namespace MesRelayGateway.Relay;

/// <summary>
/// P/Invoke bindings for usb_relay_device.dll (https://github.com/pavel-a/usb-relay-hid).
/// Signatures mirror lib/usb_relay_device.h. The native DLL is NOT shipped with this
/// project — it must be built from the usb-relay-hid repository (or obtained from the
/// board vendor) and placed next to MesRelayGateway.exe, matching its architecture
/// (x86 vs x64).
/// </summary>
internal static class UsbRelayNative
{
    private const string LibName = "usb_relay_device.dll";

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usb_relay_init();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usb_relay_exit();

    /// <summary>Returns a pointer to the head of a linked list of usb_relay_device_info.</summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr usb_relay_device_enumerate();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void usb_relay_device_free_enumerate(IntPtr deviceInfoList);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr usb_relay_device_open_with_serial_number(string serialNumber, uint len);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr usb_relay_device_open(IntPtr deviceInfo);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void usb_relay_device_close(IntPtr handle);

    /// <summary>index is 1-based channel number. Returns 0=success, 1=error, 2=invalid index.</summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usb_relay_device_open_one_relay_channel(IntPtr handle, int index);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usb_relay_device_open_all_relay_channel(IntPtr handle);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usb_relay_device_close_one_relay_channel(IntPtr handle, int index);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usb_relay_device_close_all_relay_channel(IntPtr handle);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usb_relay_device_get_status(IntPtr handle, out uint status);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usb_relay_device_lib_version();

    // Helpers for non-native callers (added on top of the vendor API) — used instead of
    // dereferencing the usb_relay_device_info struct fields directly from C#.
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr usb_relay_device_next_dev(IntPtr deviceInfoPtr);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usb_relay_device_get_num_relays(IntPtr deviceInfoPtr);

    /// <summary>Returns pointer to a 0-terminated ANSI string (device id / serial number).</summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr usb_relay_device_get_id_string(IntPtr deviceInfoPtr);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usb_relay_device_get_status_bitmap(IntPtr handle);
}
