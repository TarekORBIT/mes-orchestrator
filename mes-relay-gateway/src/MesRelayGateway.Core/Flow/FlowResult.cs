using MesRelayGateway.Configuration;
using MesRelayGateway.Mes;
using MesRelayGateway.Relay;

namespace MesRelayGateway.Flow;

public sealed class FlowResult
{
    public required bool Ok { get; init; }
    public required MesAction Action { get; init; }
    public required string Station { get; init; }
    public string? SerialNumber { get; init; }
    public required IReadOnlyList<MesResult> Steps { get; init; }
    public required MesResult FinalResult { get; init; }
    public required ErrorDecision Decision { get; init; }
    public RelayTriggerResult? Relay { get; init; }
    public string? RelayNote { get; init; }
}
