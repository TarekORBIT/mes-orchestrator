using MesRelayGateway.Configuration;
using MesRelayGateway.Mes;

namespace MesRelayGateway.Relay;

/// <summary>Drives an actual USB relay board via <see cref="UsbRelayController"/> (usb_relay_device.dll).</summary>
public sealed class UsbRelayDriver : IRelayDriver
{
    public RelayTriggerResult Trigger(RelayConfig config, ErrorDecision decision)
    {
        var channel = decision.Verdict == RelayVerdict.Pass ? config.PassChannel : config.FailChannel;
        try
        {
            using var relay = UsbRelayController.Open(config.RelaySerialNumber);
            relay.PulseChannel(channel, config.PulseMs);
            return new RelayTriggerResult
            {
                Ok = true,
                Channel = channel,
                Verdict = decision.Verdict,
                BoardSerialNumber = relay.SerialNumber,
                PulseMs = config.PulseMs,
            };
        }
        catch (Exception ex)
        {
            return new RelayTriggerResult
            {
                Ok = false,
                Channel = channel,
                Verdict = decision.Verdict,
                Error = ex.Message,
            };
        }
    }
}
