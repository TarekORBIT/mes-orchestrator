using MesRelayGateway.Configuration;
using MesRelayGateway.Mes;

namespace MesRelayGateway.Relay;

/// <summary>Simulates triggering a relay channel — no board, no usb_relay_device.dll required.</summary>
public sealed class MockRelayDriver : IRelayDriver
{
    public RelayTriggerResult Trigger(RelayConfig config, ErrorDecision decision)
    {
        var channel = decision.Verdict == RelayVerdict.Pass ? config.PassChannel : config.FailChannel;
        Thread.Sleep(80);
        return new RelayTriggerResult
        {
            Ok = true,
            Channel = channel,
            Verdict = decision.Verdict,
            BoardSerialNumber = "MOCK-RELAY",
            PulseMs = config.PulseMs,
            Simulated = true,
        };
    }
}
