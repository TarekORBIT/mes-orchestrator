using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesRelayGateway.Configuration;

/// <summary>
/// Maps a MES verdict to a physical USB relay channel. File pointed to by --relay-config.
/// </summary>
public sealed class RelayConfig
{
    /// <summary>Serial number of the relay board to use. Null/empty = first board found.</summary>
    [JsonPropertyName("relaySerialNumber")]
    public string? RelaySerialNumber { get; set; }

    /// <summary>Channel triggered when the MES flow ends with ErrorCode 0 (pass).</summary>
    [JsonPropertyName("passChannel")]
    public int PassChannel { get; set; } = 1;

    /// <summary>Channel triggered when the MES flow reports any error (block/reject).</summary>
    [JsonPropertyName("failChannel")]
    public int FailChannel { get; set; } = 2;

    /// <summary>How long the channel stays energized before being released, in ms.</summary>
    [JsonPropertyName("pulseMs")]
    public int PulseMs { get; set; } = 500;

    public static RelayConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fichier de configuration relais introuvable: {path}", path);
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<RelayConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        return config ?? throw new InvalidDataException($"Fichier de configuration relais invalide: {path}");
    }
}
