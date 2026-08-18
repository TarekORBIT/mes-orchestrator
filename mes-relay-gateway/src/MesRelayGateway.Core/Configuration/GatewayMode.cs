namespace MesRelayGateway.Configuration;

/// <summary>
/// Three distinct operating modes, available identically in the CLI and the GUI:
/// - Mock: everything simulated (MES and relay), no file/network/hardware needed.
/// - DllTest: MES_HAI.dll is loaded and called for real (via MesHaiBridge.exe if available,
///   else in-process), producing a real Log\MES_HAI.log - but the relay is never touched,
///   even if a relay-config is configured. Works without the Visteon network: off-network,
///   MES_HAI.dll itself returns a real business status (e.g. ErrorCode 3 "NotLogged") instead
///   of hanging, so this mode is safe to run from any machine to validate the DLL/bridge/log
///   pipeline.
/// - Real: same real MES_HAI.dll call, plus the physical USB relay if a relay-config resolves.
///   Intended for the actual production floor.
/// </summary>
public enum GatewayMode
{
    Mock,
    DllTest,
    Real,
}
