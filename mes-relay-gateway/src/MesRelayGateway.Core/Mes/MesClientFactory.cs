namespace MesRelayGateway.Mes;

/// <summary>
/// Picks how Mode Reel talks to MES_HAI.dll: through MesHaiBridge.exe (production/bridge, one
/// process per call, same mechanism the Node orchestrator uses) when that exe is available, or
/// by loading the DLL directly in this process otherwise. Shared by the CLI and the GUI so both
/// make the same choice the same way.
/// </summary>
public static class MesClientFactory
{
    public sealed record CreateResult(IMesClient Client, string Mode);

    public static CreateResult CreateReal(string dllPath, string haiInstance, string? bridgeExePath, int bridgeTimeoutMs, bool noBridge)
    {
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException($"MES_HAI.dll introuvable: {dllPath}", dllPath);
        }

        if (!noBridge && !string.IsNullOrWhiteSpace(bridgeExePath) && File.Exists(bridgeExePath))
        {
            return new CreateResult(new BridgeMesClient(bridgeExePath, dllPath, haiInstance, bridgeTimeoutMs), "bridge");
        }

        return new CreateResult(MesClient.Load(dllPath, haiInstance), "direct");
    }
}
