using System.Text.Json;
using System.Text.Json.Serialization;
using MesRelayGateway.Configuration;
using MesRelayGateway.Flow;
using MesRelayGateway.Mes;
using MesRelayGateway.Relay;

namespace MesRelayGateway;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static int Main(string[] args)
    {
        AppOptions options;
        try
        {
            options = AppOptions.Parse(args);
        }
        catch (HelpRequestedException)
        {
            Console.WriteLine(AppOptions.HelpText);
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Argument invalide: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(AppOptions.HelpText);
            return 2;
        }

        try
        {
            return Run(options);
        }
        catch (Exception ex)
        {
            // Unwrap reflection-invocation exceptions so the real MES_HAI.dll error surfaces
            // instead of the generic "Exception has been thrown by the target of an invocation."
            var effective = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
            PrintJson(new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["error"] = effective.GetType().Name,
                ["message"] = effective.Message,
            });
            return 1;
        }
    }

    private static int Run(AppOptions options)
    {
        // ── 1) Configuration : defauts production, fichier --config, puis overrides CLI ──
        var gatewayConfig = options.ConfigPath is null ? GatewayConfig.Default() : GatewayConfig.Load(options.ConfigPath);

        var xmlPath = options.XmlPathOverride ?? gatewayConfig.HaiXmlPath;
        var dllPath = options.DllPathOverride ?? gatewayConfig.HaiDllPath;
        var haiInstance = options.HaiInstanceOverride ?? gatewayConfig.HaiInstanceName;
        var relayConfigPath = options.RelayConfigPathOverride ?? gatewayConfig.RelayConfigPath;
        var bridgeExePath = options.BridgeExePathOverride ?? gatewayConfig.BridgeExePath;

        var station = ResolveStationName(options, gatewayConfig);

        IReadOnlyList<MesServer> servers = Array.Empty<MesServer>();
        RelayConfig? relayConfig = null;
        string? relaySkippedReason = null;

        // Relay config is optional: if the resolved path (explicit or default) does not
        // exist, the relay step is simply skipped rather than treated as an error — not
        // every station has the USB relay wired up yet.
        if (!string.IsNullOrWhiteSpace(relayConfigPath))
        {
            if (File.Exists(relayConfigPath))
            {
                relayConfig = RelayConfig.Load(relayConfigPath);
            }
            else
            {
                relaySkippedReason = $"Fichier relay-config introuvable ({relayConfigPath}) - etape relais ignoree.";
            }
        }

        // ── 2) Choix Mode Test (simulation) vs Mode Reel (DLL + relais physiques) ────────
        var mesClientMode = "mock";
        using IMesClient mes = options.IsTestMode
            ? new MockMesClient()
            : LoadRealMesClient(xmlPath, dllPath, haiInstance, bridgeExePath, gatewayConfig.BridgeTimeoutMs, options.NoBridge, out servers, out mesClientMode);

        IRelayDriver? relayDriver = relayConfig is null ? null : (options.IsTestMode ? new MockRelayDriver() : new UsbRelayDriver());

        // ── 3) Appel MES + classification d'erreur + sortie relais ──────────────────────
        var flow = GatewayRunner.Run(mes, relayDriver, relayConfig, station, options.Action, options.SerialNumber, options.Result, options.User, options.Password);

        PrintJson(new Dictionary<string, object?>
        {
            ["ok"] = flow.Ok,
            ["mode"] = options.IsTestMode ? "test" : "real",
            ["mesClientMode"] = mesClientMode,
            ["action"] = flow.Action.ToString(),
            ["station"] = flow.Station,
            ["serialNumber"] = flow.SerialNumber,
            ["config"] = new { xmlPath, dllPath, haiInstance, bridgeExePath, relayConfigPath },
            ["mesServers"] = servers.Select(s => new { s.IpAddress, s.Port, s.Description }),
            ["steps"] = flow.Steps.Select(s => new { s.Action, s.Ok, s.ErrorCode, s.ErrorDescription, s.Result, s.EngineLog }),
            ["decision"] = flow.Decision,
            ["relay"] = flow.Relay,
            ["relayNote"] = relaySkippedReason ?? flow.RelayNote,
        });

        return flow.Ok ? 0 : 1;
    }

    private static IMesClient LoadRealMesClient(
        string xmlPath, string dllPath, string haiInstance, string? bridgeExePath, int bridgeTimeoutMs, bool noBridge,
        out IReadOnlyList<MesServer> servers, out string mesClientMode)
    {
        servers = File.Exists(xmlPath) ? MesServerConfig.Load(xmlPath) : Array.Empty<MesServer>();

        var created = MesClientFactory.CreateReal(dllPath, haiInstance, bridgeExePath, bridgeTimeoutMs, noBridge);
        mesClientMode = created.Mode;
        return created.Client;
    }

    private static string ResolveStationName(AppOptions options, GatewayConfig gatewayConfig)
    {
        if (!string.IsNullOrWhiteSpace(options.StationOverride)) return options.StationOverride;
        if (!string.IsNullOrWhiteSpace(gatewayConfig.StationName)) return gatewayConfig.StationName;

        if (!string.IsNullOrWhiteSpace(options.IniPathOverride))
        {
            var ini = StationIniConfig.Load(options.IniPathOverride);
            if (!string.IsNullOrWhiteSpace(ini.StationName)) return ini.StationName;
        }

        if (options.IsTestMode) return "TEST_STATION";

        throw new ArgumentException("Aucun nom de station: fournir --station, un --config avec stationName, ou --ini avec StationName.");
    }

    private static void PrintJson(object payload)
    {
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
