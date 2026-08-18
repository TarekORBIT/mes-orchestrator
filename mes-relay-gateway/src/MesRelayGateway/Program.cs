using System.Text.Json;
using System.Text.Json.Serialization;
using MesRelayGateway.Configuration;
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

        if (!File.Exists(xmlPath))
        {
            throw new FileNotFoundException($"MES_HAI.xml introuvable: {xmlPath}", xmlPath);
        }
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException($"MES_HAI.dll introuvable: {dllPath}", dllPath);
        }

        var station = ResolveStationName(options, gatewayConfig);
        var servers = MesServerConfig.Load(xmlPath);

        // Relay config is optional: if the resolved path (explicit or default) does not
        // exist, the relay step is simply skipped rather than treated as an error — not
        // every station has the USB relay wired up yet.
        RelayConfig? relayConfig = null;
        string? relaySkippedReason = null;
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

        // ── 2) Appel MES (login puis action demandee) ───────────────────────────
        using var mes = MesClient.Load(dllPath, haiInstance);

        var steps = new List<MesResult>();
        var login = mes.Login(station, options.User, options.Password);
        steps.Add(login);

        MesResult finalResult = login;

        if (login.Ok)
        {
            finalResult = options.Action switch
            {
                MesAction.Login => login,
                MesAction.GetInfo => RunAndTrack(steps, () => mes.GetInfo(station, options.SerialNumber!)),
                MesAction.MoveIn => RunMoveIn(mes, steps, station, options.SerialNumber!),
                MesAction.MoveOutAndTest => RunAndTrack(steps, () => mes.MoveOutAndTest(
                    station, options.SerialNumber!, options.Result, groupId: "", groupVersion: "", layer: 0, checkMultiBoard: false)),
                _ => throw new InvalidOperationException($"Action non geree: {options.Action}"),
            };
        }

        // ── 3) Detection / classification de l'erreur MES ───────────────────────
        var decision = ErrorClassifier.Classify(finalResult.ErrorCode, finalResult.ErrorDescription);

        // ── 4) Sortie commande via relais USB (si configure) ────────────────────
        Dictionary<string, object?>? relayReport = null;
        if (relayConfig is not null)
        {
            relayReport = TriggerRelay(relayConfig, decision);
        }

        var overallOk = finalResult.Ok && (relayReport is null || (bool)(relayReport["ok"] ?? false));

        PrintJson(new Dictionary<string, object?>
        {
            ["ok"] = overallOk,
            ["action"] = options.Action.ToString(),
            ["station"] = station,
            ["serialNumber"] = options.SerialNumber,
            ["config"] = new { xmlPath, dllPath, haiInstance, relayConfigPath },
            ["mesServers"] = servers.Select(s => new { s.IpAddress, s.Port, s.Description }),
            ["steps"] = steps.Select(s => new { s.Action, s.Ok, s.ErrorCode, s.ErrorDescription, s.Result }),
            ["decision"] = decision,
            ["relay"] = relayReport,
            ["relayNote"] = relaySkippedReason,
        });

        return overallOk ? 0 : 1;
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

        throw new ArgumentException("Aucun nom de station: fournir --station, un --config avec stationName, ou --ini avec StationName.");
    }

    private static MesResult RunAndTrack(List<MesResult> steps, Func<MesResult> action)
    {
        var result = action();
        steps.Add(result);
        return result;
    }

    private static MesResult RunMoveIn(MesClient mes, List<MesResult> steps, string station, string serialNumber)
    {
        var info = mes.GetInfo(station, serialNumber);
        steps.Add(info);
        if (!info.Ok) return info;

        var moveIn = mes.MoveIn(station, serialNumber, activateWorkOrder: true, layer: 0);
        steps.Add(moveIn);
        return moveIn;
    }

    private static Dictionary<string, object?> TriggerRelay(RelayConfig relayConfig, ErrorDecision decision)
    {
        var channel = decision.Verdict == RelayVerdict.Pass ? relayConfig.PassChannel : relayConfig.FailChannel;
        try
        {
            using var relay = UsbRelayController.Open(relayConfig.RelaySerialNumber);
            relay.PulseChannel(channel, relayConfig.PulseMs);
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["boardSerialNumber"] = relay.SerialNumber,
                ["channel"] = channel,
                ["verdict"] = decision.Verdict.ToString(),
                ["pulseMs"] = relayConfig.PulseMs,
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["channel"] = channel,
                ["verdict"] = decision.Verdict.ToString(),
                ["error"] = ex.Message,
            };
        }
    }

    private static void PrintJson(object payload)
    {
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
