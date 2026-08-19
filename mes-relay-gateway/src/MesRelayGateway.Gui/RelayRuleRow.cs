namespace MesRelayGateway.Gui;

/// <summary>Editable row backing the "Relais USB" rules DataGrid — plain strings so mid-edit values never fail to bind.</summary>
public sealed class RelayRuleRow
{
    public string ErrorCodes { get; set; } = "*";
    public string Channel { get; set; } = "1";
    public string Mode { get; set; } = "Pulse";
    public string PulseMs { get; set; } = "3000";
}
