namespace MesRelayGateway.Mes;

public sealed class MesResult
{
    public required bool Ok { get; init; }
    public required string Action { get; init; }
    public int? ErrorCode { get; init; }
    public string? ErrorDescription { get; init; }
    public object? Result { get; init; }
    public string? FailureReason { get; init; }
}
