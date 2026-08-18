namespace MesRelayGateway.Configuration;

/// <summary>
/// Minimal reader for station.ini, in the format produced by production/install/install-client.ps1:
/// [MES]
/// StationName=STATION_01
/// TimeoutMs=1200
/// RetryCount=1
/// </summary>
public sealed class StationIniConfig
{
    public string? StationName { get; private set; }
    public int? TimeoutMs { get; private set; }
    public int? RetryCount { get; private set; }

    public static StationIniConfig Load(string iniPath)
    {
        if (!File.Exists(iniPath))
        {
            throw new FileNotFoundException($"Fichier station.ini introuvable: {iniPath}", iniPath);
        }

        var config = new StationIniConfig();
        var currentSection = string.Empty;

        foreach (var rawLine in File.ReadAllLines(iniPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0) continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (!string.Equals(currentSection, "MES", StringComparison.OrdinalIgnoreCase)) continue;

            switch (key.ToUpperInvariant())
            {
                case "STATIONNAME":
                    config.StationName = value;
                    break;
                case "TIMEOUTMS":
                    if (int.TryParse(value, out var timeout)) config.TimeoutMs = timeout;
                    break;
                case "RETRYCOUNT":
                    if (int.TryParse(value, out var retry)) config.RetryCount = retry;
                    break;
            }
        }

        return config;
    }
}
