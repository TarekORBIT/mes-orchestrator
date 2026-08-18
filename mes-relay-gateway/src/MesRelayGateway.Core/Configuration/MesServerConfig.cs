using System.Xml.Linq;

namespace MesRelayGateway.Configuration;

public sealed record MesServer(string IpAddress, string Port, string Description);

/// <summary>
/// Reads the &lt;Servers&gt; list from MES_HAI.xml (the file normally deployed at
/// C:\ProgramData\MESApps\CIM\MES_HAI.xml). This tool only reads it for diagnostics/logging —
/// the actual server selection is done internally by MES_HAI.dll.
/// </summary>
public static class MesServerConfig
{
    public static IReadOnlyList<MesServer> Load(string xmlPath)
    {
        if (!File.Exists(xmlPath))
        {
            throw new FileNotFoundException($"Fichier MES_HAI.xml introuvable: {xmlPath}", xmlPath);
        }

        var doc = XDocument.Load(xmlPath);
        var servers = new List<MesServer>();

        foreach (var el in doc.Descendants("Server"))
        {
            var ip = (string?)el.Attribute("IpAddress");
            if (string.IsNullOrWhiteSpace(ip)) continue;

            servers.Add(new MesServer(
                IpAddress: ip,
                Port: (string?)el.Attribute("Port") ?? "8634",
                Description: (string?)el.Attribute("Description") ?? string.Empty));
        }

        return servers;
    }
}
