namespace MesRelayGateway.Mes;

/// <summary>
/// Abstraction over "talk to the MES", so the GUI/CLI can run against the real
/// MES_HAI.dll (<see cref="MesClient"/>) or a local simulation (<see cref="MockMesClient"/>)
/// with the exact same call sites and result shape.
/// </summary>
public interface IMesClient : IDisposable
{
    MesResult Login(string station, string? user, string? password);
    MesResult GetInfo(string station, string serialNumber);
    MesResult MoveIn(string station, string serialNumber, bool activateWorkOrder, int layer);
    MesResult MoveOutAndTest(string station, string serialNumber, string result, string groupId, string groupVersion, int layer, bool checkMultiBoard);
}
