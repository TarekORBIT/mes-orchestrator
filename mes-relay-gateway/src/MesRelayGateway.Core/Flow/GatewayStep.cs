namespace MesRelayGateway.Flow;

/// <summary>
/// Reported via GatewayRunner.Run's onStep callback right before each call is made, so a UI
/// can highlight "where we are" in the Machine/MES_HAI.dll protocol as it happens - useful
/// since a single real call can take up to ~1-2 minutes off-network.
/// </summary>
public enum GatewayStep
{
    Login,
    GetInfo,
    CheckPartNumber,
    MoveIn,
    MoveOutAndTest,
    Relay,
    Done,
}
