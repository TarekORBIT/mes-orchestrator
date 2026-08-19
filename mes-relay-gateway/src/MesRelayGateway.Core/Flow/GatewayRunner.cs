using System.Reflection;
using System.Text.Json;
using MesRelayGateway.Configuration;
using MesRelayGateway.Mes;
using MesRelayGateway.Relay;

namespace MesRelayGateway.Flow;

/// <summary>
/// The one place that knows the MES call sequence (login, then the requested action),
/// error classification, and relay triggering. Both the CLI (Program.cs) and the GUI call
/// this so "Mode Test" (mock client/driver) and "Mode Reel" (real client/driver) behave
/// identically apart from which IMesClient/IRelayDriver they were given.
/// </summary>
public static class GatewayRunner
{
    public static FlowResult Run(
        IMesClient mes,
        IRelayDriver? relayDriver,
        RelayConfig? relayConfig,
        string station,
        MesAction action,
        string? serialNumber,
        string result,
        string? user,
        string? password,
        Action<GatewayStep>? onStep = null)
    {
        var steps = new List<MesResult>();
        onStep?.Invoke(GatewayStep.Login);
        var login = mes.Login(station, user, password);
        steps.Add(login);

        var finalResult = login;
        if (login.Ok)
        {
            finalResult = action switch
            {
                MesAction.Login => login,
                MesAction.GetInfo => Track(steps, GatewayStep.GetInfo, onStep, () => mes.GetInfo(station, serialNumber!)),
                MesAction.MoveIn => RunMoveIn(mes, steps, station, serialNumber!, onStep),
                MesAction.MoveOutAndTest => Track(steps, GatewayStep.MoveOutAndTest, onStep, () => mes.MoveOutAndTest(
                    station, serialNumber!, result, groupId: "", groupVersion: "", layer: 0, checkMultiBoard: false)),
                _ => throw new InvalidOperationException($"Action non geree: {action}"),
            };
        }

        var decision = ErrorClassifier.Classify(finalResult.ErrorCode, finalResult.ErrorDescription);

        RelayTriggerResult? relay = null;
        string? relayNote = null;
        if (relayDriver is not null && relayConfig is not null)
        {
            onStep?.Invoke(GatewayStep.Relay);
            relay = relayDriver.Trigger(relayConfig, finalResult.ErrorCode);
        }
        else if (relayDriver is not null)
        {
            relayNote = "Aucun relay-config fourni - etape relais ignoree.";
        }

        onStep?.Invoke(GatewayStep.Done);

        var overallOk = finalResult.Ok && (relay is null || relay.Ok);

        return new FlowResult
        {
            Ok = overallOk,
            Action = action,
            Station = station,
            SerialNumber = serialNumber,
            Steps = steps,
            FinalResult = finalResult,
            Decision = decision,
            Relay = relay,
            RelayNote = relayNote,
        };
    }

    private static MesResult Track(List<MesResult> steps, GatewayStep step, Action<GatewayStep>? onStep, Func<MesResult> action)
    {
        onStep?.Invoke(step);
        var result = action();
        steps.Add(result);
        return result;
    }

    /// <summary>
    /// Mirrors the reference Machine/MES_HAI.dll protocol (Login -> Serial_GetInformation ->
    /// PartNumber check against the station's active work order -> Serial_MoveIn): fetches the
    /// expected PartNumber via WorkOrder_GetActiveByStation and compares it against
    /// SerialInformation.PartNumber before allowing MoveIn. If either side's PartNumber can't
    /// be determined (e.g. Mode Test's mock, which never resolves one), the check is skipped
    /// rather than blocking - only an actual mismatch blocks the flow.
    /// </summary>
    private static MesResult RunMoveIn(IMesClient mes, List<MesResult> steps, string station, string serialNumber, Action<GatewayStep>? onStep)
    {
        onStep?.Invoke(GatewayStep.GetInfo);
        var info = mes.GetInfo(station, serialNumber);
        steps.Add(info);
        if (!info.Ok) return info;

        onStep?.Invoke(GatewayStep.CheckPartNumber);
        var workOrder = mes.GetActiveWorkOrder(station);
        steps.Add(workOrder);
        if (!workOrder.Ok) return workOrder;

        var expectedPartNumber = ExtractField(workOrder.Result, "WorkOrder", "PartNumber");
        var actualPartNumber = ExtractField(info.Result, "SerialInformation", "PartNumber");
        if (expectedPartNumber is not null && actualPartNumber is not null
            && !string.Equals(expectedPartNumber, actualPartNumber, StringComparison.OrdinalIgnoreCase))
        {
            var mismatch = new MesResult
            {
                Ok = false,
                Action = "part-number-check",
                ErrorCode = null,
                ErrorDescription = $"PartNumberMismatch: attendu '{expectedPartNumber}' (WorkOrder actif), recu '{actualPartNumber}' (SerialInformation)",
                Result = new { expectedPartNumber, actualPartNumber },
            };
            steps.Add(mismatch);
            return mismatch;
        }

        onStep?.Invoke(GatewayStep.MoveIn);
        var moveIn = mes.MoveIn(station, serialNumber, activateWorkOrder: true, layer: 0);
        steps.Add(moveIn);
        return moveIn;
    }

    /// <summary>
    /// Digs a dotted-path field out of a MesResult.Result, whatever shape it happens to be in:
    /// a Dictionary&lt;string, object?&gt; (direct MesClient, via ObjectGraphFlattener), a
    /// JsonElement (BridgeMesClient, deserialized from the bridge's JSON), or a plain
    /// POCO/anonymous object (MockMesClient) via reflection.
    /// </summary>
    private static string? ExtractField(object? source, params string[] path)
    {
        object? current = source;
        foreach (var key in path)
        {
            if (current is null) return null;

            current = current switch
            {
                IDictionary<string, object?> dict => dict.TryGetValue(key, out var v) ? v : null,
                JsonElement { ValueKind: JsonValueKind.Object } je => je.TryGetProperty(key, out var prop) ? prop : null,
                _ => current.GetType().GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(current),
            };
        }

        return current switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            _ => current.ToString(),
        };
    }
}
