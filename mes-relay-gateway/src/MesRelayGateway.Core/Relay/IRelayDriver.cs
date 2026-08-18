using MesRelayGateway.Configuration;
using MesRelayGateway.Mes;

namespace MesRelayGateway.Relay;

/// <summary>
/// Abstraction over "drive the physical relay output", so the GUI/CLI can run against the
/// real USB HID board (<see cref="UsbRelayDriver"/>) or a simulation (<see cref="MockRelayDriver"/>)
/// with the exact same call site.
/// </summary>
public interface IRelayDriver
{
    RelayTriggerResult Trigger(RelayConfig config, ErrorDecision decision);
}
