using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesRelayGateway.Configuration;

/// <summary>
/// Same shape/naming as production/config/client-config.template.json (haiXmlPath,
/// haiDllPath, stationName, haiInstanceName) so this tool reads the exact same
/// client-config.json produced by production/install/install-client.ps1, plus the
/// relay-specific additions (relayConfigPath). Defaults match the deployment target on
/// the client PC:
///   C:\MESApps\ClientGateway\bridge\MES_HAI.dll
///   C:\ProgramData\MESApps\CIM\MES_HAI.xml
/// </summary>
public sealed class GatewayConfig
{
    [JsonPropertyName("stationName")]
    public string? StationName { get; set; }

    [JsonPropertyName("haiXmlPath")]
    public string HaiXmlPath { get; set; } = @"C:\ProgramData\MESApps\CIM\MES_HAI.xml";

    [JsonPropertyName("haiDllPath")]
    public string HaiDllPath { get; set; } = @"C:\MESApps\ClientGateway\bridge\MES_HAI.dll";

    [JsonPropertyName("haiInstanceName")]
    public string HaiInstanceName { get; set; } = "MES_HAI";

    /// <summary>
    /// Same key/default as production/orchestrator's client-config.json. When this file
    /// exists, Mode Reel calls MES_HAI.dll through MesHaiBridge.exe (production/bridge) in a
    /// child process, exactly like the Node orchestrator does; otherwise it falls back to
    /// loading MES_HAI.dll directly in-process.
    /// </summary>
    [JsonPropertyName("bridgeExePath")]
    public string BridgeExePath { get; set; } = @"C:\MESApps\ClientGateway\bridge\MesHaiBridge.exe";

    /// <summary>
    /// production/orchestrator defaults this to 20000ms, tuned for a reachable factory
    /// network. Mode Test DLL no longer needs a long timeout itself (it swaps in a
    /// fast-refusing address, see MesXmlOverride) but Mode Reel's real network calls can
    /// legitimately take longer than 20s under load, so 120s stays as a generous safety
    /// ceiling rather than the actual expected wait.
    /// </summary>
    [JsonPropertyName("bridgeTimeoutMs")]
    public int BridgeTimeoutMs { get; set; } = 120000;

    [JsonPropertyName("relayConfigPath")]
    public string? RelayConfigPath { get; set; } = @"C:\MESApps\ClientGateway\relay-gateway\config\relay-config.json";

    [JsonPropertyName("logDir")]
    public string? LogDir { get; set; } = @"C:\MESApps\ClientGateway\logs";

    [JsonPropertyName("logFileName")]
    public string LogFileName { get; set; } = "mes-relay-gateway.log";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static GatewayConfig Default() => new();

    /// <summary>Loads a client-config.json, falling back to defaults for any key it omits.</summary>
    public static GatewayConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fichier de configuration introuvable: {path}", path);
        }

        var json = File.ReadAllText(path).TrimStart('\uFEFF');
        var config = JsonSerializer.Deserialize<GatewayConfig>(json, JsonOptions);
        return config ?? throw new InvalidDataException($"Fichier de configuration invalide: {path}");
    }
}
