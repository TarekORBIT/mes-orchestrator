namespace MesRelayGateway.Configuration;

/// <summary>
/// Raw CLI input. Config-file values (client-config.json, same shape as production/) are
/// the defaults; any --flag here overrides the matching config-file value. Nothing about
/// file locations is hardcoded beyond the class defaults in <see cref="GatewayConfig"/>,
/// which mirror the paths already used on the client PC by production/install/install-client.ps1.
/// </summary>
public sealed class AppOptions
{
    public string? ConfigPath { get; init; }
    public string? XmlPathOverride { get; init; }
    public string? DllPathOverride { get; init; }
    public string? IniPathOverride { get; init; }
    public string? StationOverride { get; init; }
    public string? RelayConfigPathOverride { get; init; }
    public string? HaiInstanceOverride { get; init; }
    public bool IsTestMode { get; init; }

    public required MesAction Action { get; init; }
    public string? SerialNumber { get; init; }
    public string Result { get; init; } = "Pass";
    public string? User { get; init; }
    public string? Password { get; init; }

    public static AppOptions Parse(string[] args)
    {
        string? config = null, xml = null, dll = null, ini = null, station = null, relayConfig = null, instance = null, mode = null;
        string? action = null, serial = null, result = null, user = null, password = null;

        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (args[i])
            {
                case "--config": config = Next(); break;
                case "--xml": xml = Next(); break;
                case "--dll": dll = Next(); break;
                case "--ini": ini = Next(); break;
                case "--station": station = Next(); break;
                case "--relay-config": relayConfig = Next(); break;
                case "--action": action = Next(); break;
                case "--serial": serial = Next(); break;
                case "--result": result = Next(); break;
                case "--user": user = Next(); break;
                case "--password": password = Next(); break;
                case "--hai-instance": instance = Next(); break;
                case "--mode": mode = Next(); break;
                case "--help":
                case "-h":
                    throw new HelpRequestedException();
                default:
                    throw new ArgumentException($"Argument inconnu: {args[i]}");
            }
        }

        var isTestMode = mode?.Trim().ToLowerInvariant() switch
        {
            null => false,
            "real" or "reel" => false,
            "test" or "mock" => true,
            _ => throw new ArgumentException($"--mode invalide: '{mode}'. Valeurs: test, real."),
        };

        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("--action <login|get-info|move-in|move-out-and-test> est requis.");

        var parsedAction = action.Trim().ToLowerInvariant() switch
        {
            "login" => MesAction.Login,
            "get-info" or "getinfo" => MesAction.GetInfo,
            "move-in" or "movein" => MesAction.MoveIn,
            "move-out-and-test" or "moveoutandtest" => MesAction.MoveOutAndTest,
            _ => throw new ArgumentException($"--action invalide: '{action}'. Valeurs: login, get-info, move-in, move-out-and-test."),
        };

        if (parsedAction is MesAction.GetInfo or MesAction.MoveIn or MesAction.MoveOutAndTest && string.IsNullOrWhiteSpace(serial))
        {
            throw new ArgumentException($"--serial <numero de serie> est requis pour l'action '{action}'.");
        }

        return new AppOptions
        {
            ConfigPath = config is null ? null : Path.GetFullPath(config),
            XmlPathOverride = xml is null ? null : Path.GetFullPath(xml),
            DllPathOverride = dll is null ? null : Path.GetFullPath(dll),
            IniPathOverride = ini is null ? null : Path.GetFullPath(ini),
            StationOverride = station,
            RelayConfigPathOverride = relayConfig is null ? null : Path.GetFullPath(relayConfig),
            HaiInstanceOverride = instance,
            IsTestMode = isTestMode,
            Action = parsedAction,
            SerialNumber = serial,
            Result = string.IsNullOrWhiteSpace(result) ? "Pass" : result,
            User = user,
            Password = password,
        };
    }

    public static string HelpText => """
        MesRelayGateway.exe - passerelle MES -> relais USB (C#/.NET, meme logique que production/)

        Usage:
          MesRelayGateway.exe --config <client-config.json> --action <action> [--serial <n>] [--result <Pass|Fail>]
          MesRelayGateway.exe --xml <MES_HAI.xml> --dll <MES_HAI.dll> --station <nom> --action <action> [...]

        Toute valeur peut venir soit du fichier --config (meme format que
        production/config/client-config.template.json: haiXmlPath, haiDllPath, stationName,
        haiInstanceName, relayConfigPath), soit d'un --flag explicite qui la surcharge.
        Sans --config, les valeurs par defaut correspondent aux chemins deployes sur le
        poste client:
          haiXmlPath          C:\ProgramData\MESApps\CIM\MES_HAI.xml
          haiDllPath          C:\MESApps\ClientGateway\bridge\MES_HAI.dll
          relayConfigPath     C:\MESApps\ClientGateway\relay-gateway\config\relay-config.json

        Options:
          --config <path>        client-config.json (voir config/client-config.template.json)
          --xml <path>            Surcharge haiXmlPath
          --dll <path>            Surcharge haiDllPath
          --ini <path>            station.ini a lire pour StationName (si --station absent)
          --station <name>        Surcharge stationName
          --relay-config <path>   Surcharge relayConfigPath. Si le fichier resolu n'existe pas,
                                   l'etape relais est simplement ignoree (pas d'erreur).
          --hai-instance <name>   Surcharge haiInstanceName (defaut MES_HAI)
          --mode <test|real>      test = simulation locale (pas de DLL/reseau requis), meme
                                   comportement que le mode mock de l'orchestrateur Node.
                                   real = appel reel MES_HAI.dll (defaut).
          --action <name>         login | get-info | move-in | move-out-and-test
          --serial <n>            Numero de serie (requis sauf pour login)
          --result <Pass|Fail>    Resultat pour move-out-and-test (defaut Pass)
          --user / --password     Identifiants MES optionnels

        Exemple (poste client, apres install):
          MesRelayGateway.exe --config C:\MESApps\ClientGateway\relay-gateway\config\client-config.json ^
            --action move-out-and-test --serial SN001 --result Pass
        """;
}

public sealed class HelpRequestedException : Exception;
