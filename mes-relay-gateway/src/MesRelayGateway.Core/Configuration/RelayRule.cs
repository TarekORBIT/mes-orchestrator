using System.Text.Json.Serialization;

namespace MesRelayGateway.Configuration;

public enum RelayMode
{
    /// <summary>Activate the channel, hold it for PulseMs, then release it.</summary>
    Pulse,

    /// <summary>Activate the channel and leave it on — stays on until manually reset.</summary>
    Latch,
}

/// <summary>
/// One row of the relay's trigger table: which ErrorCode(s) fire which channel, and how
/// (a timed pulse, or a latch that stays on until reset by hand).
/// </summary>
public sealed class RelayRule
{
    /// <summary>
    /// Comma-separated list of ErrorCode values this rule matches (e.g. "0" or "1,2"), or
    /// "*" to match anything not matched by a more specific rule.
    /// </summary>
    [JsonPropertyName("errorCodes")]
    public string ErrorCodes { get; set; } = "*";

    [JsonPropertyName("channel")]
    public int Channel { get; set; } = 1;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonPropertyName("mode")]
    public RelayMode Mode { get; set; } = RelayMode.Pulse;

    /// <summary>Only used when Mode == Pulse.</summary>
    [JsonPropertyName("pulseMs")]
    public int PulseMs { get; set; } = 3000;

    public bool IsWildcard => ErrorCodes.Trim() == "*";

    public bool Matches(int? errorCode)
    {
        if (IsWildcard) return true;
        if (!errorCode.HasValue) return false;

        return ErrorCodes
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(part => int.TryParse(part, out var code) && code == errorCode.Value);
    }

    public string Describe() =>
        Mode == RelayMode.Pulse
            ? $"ErrorCode={ErrorCodes} -> canal {Channel} (impulsion {PulseMs}ms)"
            : $"ErrorCode={ErrorCodes} -> canal {Channel} (maintenu ON jusqu'a reset manuel)";
}
