namespace MesRelayGateway.Mes;

public enum RelayVerdict { Pass, Fail }

public sealed record ErrorDecision(string Action, string Reason, string Severity, RelayVerdict Verdict);

/// <summary>
/// Ports the error-classification rules from production/orchestrator/mes-orchestrator.js
/// (classifyErrorDetail) so both the Node orchestrator and this .NET tool agree on what a
/// given MES_HAI ErrorCode/ErrorDescription means, and on the pass/fail verdict driving the relay.
/// </summary>
public static class ErrorClassifier
{
    public static ErrorDecision Classify(int? errorCode, string? errorDescription)
    {
        var description = (errorDescription ?? string.Empty).Trim();
        var descLower = description.ToLowerInvariant();

        if (!errorCode.HasValue)
        {
            return new ErrorDecision("BLOCK_AND_ESCALATE", "ErrorCode invalide", "high", RelayVerdict.Fail);
        }

        if (errorCode.Value == 0)
        {
            return new ErrorDecision("CONTINUE_FLOW", "ErrorCode=0", "none", RelayVerdict.Pass);
        }

        if (descLower.Contains("notlogged") || descLower.Contains("not logged"))
        {
            return new ErrorDecision("RELOGIN_AND_RETRY_ONCE", "Session non connectee", "medium", RelayVerdict.Fail);
        }

        if (descLower.Contains("notregistered") || descLower.Contains("station"))
        {
            return new ErrorDecision("BLOCK_AND_CHECK_STATION_CONFIG", "Station non valide", "high", RelayVerdict.Fail);
        }

        if (descLower.Contains("timeout") || descLower.Contains("connection") || descLower.Contains("network"))
        {
            return new ErrorDecision("SWITCH_SERVER_AND_RETRY_ONCE", "Defaut reseau", "high", RelayVerdict.Fail);
        }

        return new ErrorDecision("BLOCK_PART_AND_CREATE_INCIDENT", "Erreur non classifiee", "high", RelayVerdict.Fail);
    }
}
