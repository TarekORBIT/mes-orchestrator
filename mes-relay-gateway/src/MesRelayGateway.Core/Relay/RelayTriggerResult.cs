using MesRelayGateway.Mes;

namespace MesRelayGateway.Relay;

public sealed class RelayTriggerResult
{
    public required bool Ok { get; init; }
    public required int Channel { get; init; }
    public required RelayVerdict Verdict { get; init; }
    public string? BoardSerialNumber { get; init; }
    public int? PulseMs { get; init; }
    public string? Error { get; init; }
    public bool Simulated { get; init; }
}
