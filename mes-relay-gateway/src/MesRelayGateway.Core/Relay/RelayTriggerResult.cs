namespace MesRelayGateway.Relay;

public sealed class RelayTriggerResult
{
    public required bool Ok { get; init; }
    public required int Channel { get; init; }

    /// <summary>True if the channel was left ON (RelayMode.Latch) rather than pulsed.</summary>
    public bool Latched { get; init; }

    public string? BoardSerialNumber { get; init; }
    public int? PulseMs { get; init; }

    /// <summary>Human-readable description of the rule that matched (see RelayRule.Describe()).</summary>
    public string? RuleDescription { get; init; }

    public string? Error { get; init; }
    public bool Simulated { get; init; }
}
