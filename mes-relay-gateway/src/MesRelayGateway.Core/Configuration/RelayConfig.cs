using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesRelayGateway.Configuration;

/// <summary>
/// Maps MES ErrorCode values to physical USB relay channels. File pointed to by
/// --relay-config. Rules are evaluated in order: the first non-wildcard rule that matches
/// the ErrorCode wins; a "*" rule (if present) is only used when nothing more specific matched.
/// </summary>
public sealed class RelayConfig
{
    /// <summary>Serial number of the relay board to use. Null/empty = first board found.</summary>
    [JsonPropertyName("relaySerialNumber")]
    public string? RelaySerialNumber { get; set; }

    [JsonPropertyName("rules")]
    public List<RelayRule> Rules { get; set; } = [];

    // Legacy shape (passChannel/failChannel/pulseMs), kept only so older relay-config.json
    // files still load - Load() converts them into two Rules the first time it reads one.
    [JsonPropertyName("passChannel")]
    public int? PassChannel { get; set; }

    [JsonPropertyName("failChannel")]
    public int? FailChannel { get; set; }

    [JsonPropertyName("pulseMs")]
    public int? PulseMs { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static RelayConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fichier de configuration relais introuvable: {path}", path);
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<RelayConfig>(json, JsonOptions)
            ?? throw new InvalidDataException($"Fichier de configuration relais invalide: {path}");

        if (config.Rules.Count == 0 && (config.PassChannel.HasValue || config.FailChannel.HasValue))
        {
            var pulseMs = config.PulseMs ?? 500;
            config.Rules.Add(new RelayRule { ErrorCodes = "0", Channel = config.PassChannel ?? 1, Mode = RelayMode.Pulse, PulseMs = pulseMs });
            config.Rules.Add(new RelayRule { ErrorCodes = "*", Channel = config.FailChannel ?? 2, Mode = RelayMode.Pulse, PulseMs = pulseMs });
        }

        return config;
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        });
        File.WriteAllText(path, json);
    }

    /// <summary>First specific (non-wildcard) rule matching errorCode, else the wildcard rule, else null.</summary>
    public RelayRule? FindMatch(int? errorCode)
    {
        foreach (var rule in Rules)
        {
            if (!rule.IsWildcard && rule.Matches(errorCode)) return rule;
        }

        return Rules.FirstOrDefault(r => r.IsWildcard);
    }
}
