using MesRelayGateway.Configuration;

namespace MesRelayGateway.Relay;

/// <summary>Simulates triggering a relay channel — no board, no usb_relay_device.dll required.</summary>
public sealed class MockRelayDriver : IRelayDriver
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
                Simulated = true,
                Error = $"Aucune regle relay-config ne correspond a ErrorCode={errorCode?.ToString() ?? "(aucun)"}.",
            };
        }

        Thread.Sleep(80);
        return new RelayTriggerResult
        {
            Ok = true,
            Channel = rule.Channel,
            Latched = rule.Mode == RelayMode.Latch,
            BoardSerialNumber = "MOCK-RELAY",
            PulseMs = rule.Mode == RelayMode.Pulse ? rule.PulseMs : null,
            RuleDescription = rule.Describe(),
            Simulated = true,
        };
    }
}
