namespace MesRelayGateway.Mes;

public sealed class MesResult
{
    public required bool Ok { get; init; }
    public required string Action { get; init; }
    public int? ErrorCode { get; init; }
    public string? ErrorDescription { get; init; }
    public object? Result { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>
    /// Raw text appended to MES_HAI.dll's own log4net log (Log\MES_HAI.log) while this
    /// call ran - the diagnostic trace the DLL normally produces on the real machine
    /// (config resolution, load-balancing between CIM servers, WCF errors...). Null when
    /// the log file couldn't be located or nothing was captured.
    /// </summary>
    public string? EngineLog { get; init; }
}
