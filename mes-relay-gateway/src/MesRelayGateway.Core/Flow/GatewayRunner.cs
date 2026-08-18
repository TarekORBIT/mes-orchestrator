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
        string? password)
    {
        var steps = new List<MesResult>();
        var login = mes.Login(station, user, password);
        steps.Add(login);

        var finalResult = login;
        if (login.Ok)
        {
            finalResult = action switch
            {
                MesAction.Login => login,
                MesAction.GetInfo => Track(steps, () => mes.GetInfo(station, serialNumber!)),
                MesAction.MoveIn => RunMoveIn(mes, steps, station, serialNumber!),
                MesAction.MoveOutAndTest => Track(steps, () => mes.MoveOutAndTest(
                    station, serialNumber!, result, groupId: "", groupVersion: "", layer: 0, checkMultiBoard: false)),
                _ => throw new InvalidOperationException($"Action non geree: {action}"),
            };
        }

        var decision = ErrorClassifier.Classify(finalResult.ErrorCode, finalResult.ErrorDescription);

        RelayTriggerResult? relay = null;
        string? relayNote = null;
        if (relayDriver is not null && relayConfig is not null)
        {
            relay = relayDriver.Trigger(relayConfig, decision);
        }
        else if (relayDriver is not null)
        {
            relayNote = "Aucun relay-config fourni - etape relais ignoree.";
        }

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

    private static MesResult Track(List<MesResult> steps, Func<MesResult> action)
    {
        var result = action();
        steps.Add(result);
        return result;
    }

    private static MesResult RunMoveIn(IMesClient mes, List<MesResult> steps, string station, string serialNumber)
    {
        var info = mes.GetInfo(station, serialNumber);
        steps.Add(info);
        if (!info.Ok) return info;

        var moveIn = mes.MoveIn(station, serialNumber, activateWorkOrder: true, layer: 0);
        steps.Add(moveIn);
        return moveIn;
    }
}
