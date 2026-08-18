using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesRelayGateway.Mes;

/// <summary>
/// Talks to MES_HAI.dll the same way production/orchestrator/mes-orchestrator.js does: by
/// spawning production/bridge's MesHaiBridge.exe and exchanging one JSON request/response
/// over stdin/stdout per call, instead of loading the DLL in this process. Reuses the exact
/// bridge everything else in production/ already depends on.
/// </summary>
public sealed class BridgeMesClient : IMesClient
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _bridgeExePath;
    private readonly string _dllPath;
    private readonly string _haiInstanceName;
    private readonly int _timeoutMs;
    private readonly string _logPath;

    public BridgeMesClient(string bridgeExePath, string dllPath, string haiInstanceName, int timeoutMs)
    {
        if (!File.Exists(bridgeExePath))
        {
            throw new FileNotFoundException($"MesHaiBridge.exe introuvable: {bridgeExePath}", bridgeExePath);
        }

        _bridgeExePath = bridgeExePath;
        _dllPath = dllPath;
        _haiInstanceName = haiInstanceName;
        _timeoutMs = timeoutMs;

        // We pin the child process's working directory to the bridge's own folder (see
        // Invoke), so log4net's relative "Log\MES_HAI.log" always lands in a predictable
        // place we can read back.
        _logPath = Path.Combine(Path.GetDirectoryName(bridgeExePath)!, "Log", "MES_HAI.log");
    }

    public MesResult Login(string station, string? user, string? password) =>
        Invoke("login", new BridgeRequestDto { Station = station, User = user, Password = password });

    public MesResult GetInfo(string station, string serialNumber) =>
        Invoke("get-info", new BridgeRequestDto { Station = station, SerialNumber = serialNumber });

    public MesResult MoveIn(string station, string serialNumber, bool activateWorkOrder, int layer) =>
        Invoke("move-in", new BridgeRequestDto { Station = station, SerialNumber = serialNumber, ActivateWorkOrder = activateWorkOrder, Layer = layer });

    public MesResult MoveOutAndTest(string station, string serialNumber, string result, string groupId, string groupVersion, int layer, bool checkMultiBoard) =>
        Invoke("move-out-and-test", new BridgeRequestDto
        {
            Station = station,
            SerialNumber = serialNumber,
            Result = result,
            GroupId = groupId,
            GroupVersion = groupVersion,
            Layer = layer,
            CheckMultiBoard = checkMultiBoard,
        });

    private MesResult Invoke(string action, BridgeRequestDto request)
    {
        request.Action = action;
        request.HaiDllPath = _dllPath;
        request.HaiInstanceName = _haiInstanceName;

        var offset = MesLogReader.GetLength(_logPath);
        var response = RunBridgeProcess(request);
        var engineLog = MesLogReader.ReadFrom(_logPath, offset);

        if (!response.Ok && response.Error is not null)
        {
            return new MesResult
            {
                Ok = false,
                Action = action,
                ErrorCode = null,
                ErrorDescription = $"{response.Error.Code}: {response.Error.Message}",
                Result = null,
                EngineLog = engineLog,
            };
        }

        return new MesResult
        {
            // Same conservative rule as MesClient: only an explicit ErrorCode 0 counts as Ok.
            Ok = response.ErrorDetail?.ErrorCode == 0,
            Action = action,
            ErrorCode = response.ErrorDetail?.ErrorCode,
            ErrorDescription = response.ErrorDetail?.ErrorDescription,
            Result = response.Result,
            EngineLog = engineLog,
        };
    }

    private BridgeResponseDto RunBridgeProcess(BridgeRequestDto request)
    {
        var json = JsonSerializer.Serialize(request, RequestJsonOptions);

        var psi = new ProcessStartInfo
        {
            FileName = _bridgeExePath,
            WorkingDirectory = Path.GetDirectoryName(_bridgeExePath),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Impossible de demarrer MesHaiBridge.exe.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        process.StandardInput.Write(json);
        process.StandardInput.Close();

        if (!process.WaitForExit(_timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"MesHaiBridge.exe n'a pas repondu apres {_timeoutMs}ms (action={request.Action}).");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException($"MesHaiBridge.exe n'a rien retourne sur stdout. exit={process.ExitCode}, stderr={stderr.Trim()}");
        }

        try
        {
            return JsonSerializer.Deserialize<BridgeResponseDto>(stdout, ResponseJsonOptions)
                ?? throw new InvalidOperationException("Reponse MesHaiBridge.exe vide.");
        }
        catch (JsonException ex)
        {
            var preview = stdout.Length > 500 ? stdout[..500] : stdout;
            throw new InvalidOperationException($"Reponse MesHaiBridge.exe illisible: {ex.Message}. Brut: {preview}");
        }
    }

    public void Dispose() { }
}

// Mirrors production/bridge/Program.cs's BridgeRequest / BridgeResponse contracts.
internal sealed class BridgeRequestDto
{
    public string Action { get; set; } = string.Empty;
    public string? HaiDllPath { get; set; }
    public string? HaiInstanceName { get; set; }
    public string? Station { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? SerialNumber { get; set; }
    public bool? ActivateWorkOrder { get; set; }
    public int? Layer { get; set; }
    public string? Result { get; set; }
    public int? ResultCode { get; set; }
    public string? GroupId { get; set; }
    public string? GroupVersion { get; set; }
    public bool? CheckMultiBoard { get; set; }
}

internal sealed class BridgeResponseDto
{
    public bool Ok { get; set; }
    public string? Action { get; set; }
    public object? Result { get; set; }
    public BridgeErrorDetailDto? ErrorDetail { get; set; }
    public Dictionary<string, object?>? Diagnostics { get; set; }
    public BridgeErrorDto? Error { get; set; }
}

internal sealed class BridgeErrorDetailDto
{
    public int? ErrorCode { get; set; }
    public string? ErrorDescription { get; set; }
}

internal sealed class BridgeErrorDto
{
    public string Code { get; set; } = "bridge_error";
    public string Message { get; set; } = string.Empty;
}
