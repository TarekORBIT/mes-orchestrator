namespace MesRelayGateway.Gui;

public sealed class HistoryEntry
{
    public required string Time { get; init; }
    public required string Mode { get; init; }
    public required string Action { get; init; }
    public required string Station { get; init; }
    public string? Serial { get; init; }
    public required string Result { get; init; }
    public string? Relay { get; init; }
    public string? Detail { get; init; }
}
