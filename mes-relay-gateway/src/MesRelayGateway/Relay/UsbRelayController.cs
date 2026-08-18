using System.Runtime.InteropServices;

namespace MesRelayGateway.Relay;

public sealed record RelayDeviceInfo(string SerialNumber, int ChannelCount);

public sealed class UsbRelayException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// High-level wrapper around usb_relay_device.dll: find a board (by serial number, or the
/// first one found), open/close a channel, and read status. One instance == one open handle.
/// </summary>
public sealed class UsbRelayController : IDisposable
{
    private IntPtr _handle;
    private bool _libInitialized;

    public string SerialNumber { get; }
    public int ChannelCount { get; }

    private UsbRelayController(IntPtr handle, string serialNumber, int channelCount, bool libInitialized)
    {
        _handle = handle;
        _libInitialized = libInitialized;
        SerialNumber = serialNumber;
        ChannelCount = channelCount;
    }

    /// <summary>
    /// Opens a relay board. If <paramref name="serialNumber"/> is null/empty, the first
    /// enumerated board is used.
    /// </summary>
    public static UsbRelayController Open(string? serialNumber)
    {
        if (UsbRelayNative.usb_relay_init() != 0)
        {
            throw new UsbRelayException("usb_relay_init a echoue (bibliotheque native usb_relay_device.dll introuvable ou defaillante).");
        }

        var listHead = UsbRelayNative.usb_relay_device_enumerate();
        if (listHead == IntPtr.Zero)
        {
            UsbRelayNative.usb_relay_exit();
            throw new UsbRelayException("Aucune carte relais USB detectee (usb_relay_device_enumerate a retourne une liste vide).");
        }

        try
        {
            var target = IntPtr.Zero;
            string? resolvedSerial = null;
            var current = listHead;

            while (current != IntPtr.Zero)
            {
                var idPtr = UsbRelayNative.usb_relay_device_get_id_string(current);
                var id = idPtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(idPtr);

                if (string.IsNullOrEmpty(serialNumber) || string.Equals(id, serialNumber, StringComparison.OrdinalIgnoreCase))
                {
                    target = current;
                    resolvedSerial = id;
                    break;
                }

                current = UsbRelayNative.usb_relay_device_next_dev(current);
            }

            if (target == IntPtr.Zero)
            {
                throw new UsbRelayException(string.IsNullOrEmpty(serialNumber)
                    ? "Aucune carte relais USB detectee."
                    : $"Aucune carte relais USB avec le numero de serie '{serialNumber}' n'a ete trouvee.");
            }

            var channelCount = UsbRelayNative.usb_relay_device_get_num_relays(target);
            var handle = UsbRelayNative.usb_relay_device_open(target);
            if (handle == IntPtr.Zero)
            {
                throw new UsbRelayException($"Impossible d'ouvrir la carte relais '{resolvedSerial}'.");
            }

            return new UsbRelayController(handle, resolvedSerial ?? "UNKNOWN", channelCount, libInitialized: true);
        }
        finally
        {
            UsbRelayNative.usb_relay_device_free_enumerate(listHead);
        }
    }

    public static IReadOnlyList<RelayDeviceInfo> ListDevices()
    {
        if (UsbRelayNative.usb_relay_init() != 0)
        {
            throw new UsbRelayException("usb_relay_init a echoue.");
        }

        try
        {
            var result = new List<RelayDeviceInfo>();
            var current = UsbRelayNative.usb_relay_device_enumerate();
            var head = current;
            try
            {
                while (current != IntPtr.Zero)
                {
                    var idPtr = UsbRelayNative.usb_relay_device_get_id_string(current);
                    var id = idPtr == IntPtr.Zero ? "UNKNOWN" : Marshal.PtrToStringAnsi(idPtr) ?? "UNKNOWN";
                    var channels = UsbRelayNative.usb_relay_device_get_num_relays(current);
                    result.Add(new RelayDeviceInfo(id, channels));
                    current = UsbRelayNative.usb_relay_device_next_dev(current);
                }
            }
            finally
            {
                if (head != IntPtr.Zero) UsbRelayNative.usb_relay_device_free_enumerate(head);
            }

            return result;
        }
        finally
        {
            UsbRelayNative.usb_relay_exit();
        }
    }

    public void OpenChannel(int channel)
    {
        EnsureOpen();
        var rc = UsbRelayNative.usb_relay_device_open_one_relay_channel(_handle, channel);
        if (rc != 0)
        {
            throw new UsbRelayException($"Activation du canal {channel} echouee (code {rc}).");
        }
    }

    public void CloseChannel(int channel)
    {
        EnsureOpen();
        var rc = UsbRelayNative.usb_relay_device_close_one_relay_channel(_handle, channel);
        if (rc != 0)
        {
            throw new UsbRelayException($"Desactivation du canal {channel} echouee (code {rc}).");
        }
    }

    public void CloseAllChannels()
    {
        EnsureOpen();
        UsbRelayNative.usb_relay_device_close_all_relay_channel(_handle);
    }

    /// <summary>Activates a channel, holds it for <paramref name="pulseMs"/>, then deactivates it.</summary>
    public void PulseChannel(int channel, int pulseMs)
    {
        OpenChannel(channel);
        try
        {
            if (pulseMs > 0)
            {
                Thread.Sleep(pulseMs);
            }
        }
        finally
        {
            CloseChannel(channel);
        }
    }

    public uint GetStatusBitmap()
    {
        EnsureOpen();
        var rc = UsbRelayNative.usb_relay_device_get_status(_handle, out var status);
        if (rc != 0)
        {
            throw new UsbRelayException($"Lecture du statut relais echouee (code {rc}).");
        }
        return status;
    }

    private void EnsureOpen()
    {
        if (_handle == IntPtr.Zero)
        {
            throw new UsbRelayException("La carte relais n'est pas ouverte.");
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            UsbRelayNative.usb_relay_device_close(_handle);
            _handle = IntPtr.Zero;
        }

        if (_libInitialized)
        {
            UsbRelayNative.usb_relay_exit();
            _libInitialized = false;
        }
    }
}
