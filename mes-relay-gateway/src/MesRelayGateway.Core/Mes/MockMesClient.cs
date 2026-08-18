namespace MesRelayGateway.Mes;

/// <summary>
/// Local simulation of the MES, no MES_HAI.dll / no factory network required. Same known
/// serials and error codes as the mock bridge in production/orchestrator/mes-orchestrator.js,
/// so "Mode Test" behaves identically between the Node orchestrator and this tool.
/// </summary>
public sealed class MockMesClient : IMesClient
{
    private static readonly Dictionary<string, (string PartNumber, string WorkOrder)> KnownSerials = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SN001"] = ("PN-VISTEON-001", "WO-2026-001"),
        ["SN002"] = ("PN-VISTEON-002", "WO-2026-002"),
    };

    public MesResult Login(string station, string? user, string? password)
    {
        Simulate();
        var ok = !string.IsNullOrWhiteSpace(station) && station != "STATION_NAME_HERE";
        return ok
            ? Ok("login", new { name = "Connected", value = 1 })
            : Fail("login", 3, "StationNotRegistered: station name is empty or invalid");
    }

    public MesResult GetInfo(string station, string serialNumber)
    {
        Simulate();
        if (!KnownSerials.TryGetValue(serialNumber, out var info))
        {
            return Fail("get-info", 102, $"SerialNotFound: serial '{serialNumber}' not found in MES");
        }

        return Ok("get-info", new { SerialInformation = new { SerialNumber = serialNumber, info.PartNumber, info.WorkOrder, Status = "Active" } });
    }

    public MesResult MoveIn(string station, string serialNumber, bool activateWorkOrder, int layer)
    {
        Simulate();
        if (!KnownSerials.TryGetValue(serialNumber, out var info))
        {
            return Fail("move-in", 102, $"SerialNotFound: serial '{serialNumber}' not found in MES");
        }

        return Ok("move-in", new { Result = new { name = "Pass", value = 1 }, ResultMessage = "MoveIn accepted", UnitId = serialNumber, info.WorkOrder });
    }

    public MesResult MoveOutAndTest(string station, string serialNumber, string result, string groupId, string groupVersion, int layer, bool checkMultiBoard)
    {
        Simulate();
        return Ok("move-out-and-test", new { Result = new { name = result, value = string.Equals(result, "Pass", StringComparison.OrdinalIgnoreCase) ? 1 : 0 } });
    }

    private static void Simulate() => Thread.Sleep(120);

    private static MesResult Ok(string action, object result) => new()
    {
        Ok = true,
        Action = action,
        ErrorCode = 0,
        ErrorDescription = "OK",
        Result = result,
    };

    private static MesResult Fail(string action, int errorCode, string description) => new()
    {
        Ok = false,
        Action = action,
        ErrorCode = errorCode,
        ErrorDescription = description,
        Result = null,
    };

    public void Dispose() { }
}
