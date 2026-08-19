using MesRelayGateway.Configuration;

namespace MesRelayGateway.Relay;

/// <summary>
/// Abstraction over "drive the physical relay output", so the GUI/CLI can run against the
/// real USB HID board (<see cref="UsbRelayDriver"/>) or a simulation (<see cref="MockRelayDriver"/>)
/// with the exact same call site.
/// </summary>
public interface IRelayDriver
{
    /// <summary>
    /// Finds the RelayRule in config matching errorCode and applies it (pulse or latch).
    /// Returns Ok=false with no channel driven if no rule matches.
    /// </summary>
    RelayTriggerResult Trigger(RelayConfig config, int? errorCode);
}
