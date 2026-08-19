using MesRelayGateway.Configuration;

namespace MesRelayGateway.Relay;

/// <summary>Drives an actual USB relay board via <see cref="UsbRelayController"/> (usb_relay_device.dll).</summary>
public sealed class UsbRelayDriver : IRelayDriver
{
    public RelayTriggerResult Trigger(RelayConfig config, int? errorCode)
    {
        var rule = config.FindMatch(errorCode);
        if (rule is null)
        {
            return new RelayTriggerResult
            {
                Ok = false,
                Channel = 0,
                Error = $"Aucune regle relay-config ne correspond a ErrorCode={errorCode?.ToString() ?? "(aucun)"}.",
            };
        }

        try
        {
            using var relay = UsbRelayController.Open(config.RelaySerialNumber);

            if (rule.Mode == RelayMode.Latch)
            {
                relay.OpenChannel(rule.Channel);
            }
            else
            {
                relay.PulseChannel(rule.Channel, rule.PulseMs);
            }

            return new RelayTriggerResult
            {
                Ok = true,
                Channel = rule.Channel,
                Latched = rule.Mode == RelayMode.Latch,
                BoardSerialNumber = relay.SerialNumber,
                PulseMs = rule.Mode == RelayMode.Pulse ? rule.PulseMs : null,
                RuleDescription = rule.Describe(),
            };
        }
        catch (Exception ex)
        {
            return new RelayTriggerResult
            {
                Ok = false,
                Channel = rule.Channel,
                Latched = rule.Mode == RelayMode.Latch,
                RuleDescription = rule.Describe(),
                Error = ex.Message,
            };
        }
    }
}
