using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesHaiBridge;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine("MesHaiBridge usage:");
            Console.WriteLine("  MesHaiBridge.exe [--request-file <path>]");
            Console.WriteLine("Input JSON expected on stdin when --request-file is not used.");
            return 0;
        }

        var rawInput = await ReadInputAsync(args);
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            await WriteResponseAsync(BridgeResponse.Fail("invalid_request", "Empty request payload."));
            return 1;
        }

        BridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(rawInput, JsonOptions);
        }
        catch (Exception ex)
        {
            await WriteResponseAsync(BridgeResponse.Fail("invalid_json", ex.Message));
            return 1;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Action))
        {
            await WriteResponseAsync(BridgeResponse.Fail("invalid_request", "Action is required."));
            return 1;
        }

        var action = NormalizeAction(request.Action);
        if (action is null)
        {
            await WriteResponseAsync(BridgeResponse.Fail("unsupported_action", $"Unsupported action '{request.Action}'."));
            return 1;
        }

        var dllPath = ResolveDllPath(request.HaiDllPath);
        if (!File.Exists(dllPath))
        {
            await WriteResponseAsync(BridgeResponse.Fail("dll_not_found", $"MES_HAI.dll not found at '{dllPath}'."));
            return 1;
        }

        try
        {
            RegisterAssemblyResolver(Path.GetDirectoryName(dllPath)!);
            var asm = Assembly.LoadFrom(dllPath);
            var traceabilityType = asm.GetType("MES_HAI.Traceability", throwOnError: true)!;
            var traceability = CreateTraceability(traceabilityType, request.HaiInstanceName ?? "MES_HAI");

            try
            {
                var result = ExecuteAction(asm, traceability, action, request);
                var bridgeResponse = BuildResponse(action, result, asm, dllPath);
                await WriteResponseAsync(bridgeResponse);
                return bridgeResponse.Ok ? 0 : 1;
            }
            finally
            {
                TryDispose(traceability);
            }
        }
        catch (TargetInvocationException tex)
        {
            var root = tex.InnerException ?? tex;
            await WriteResponseAsync(BridgeResponse.Fail("invocation_error", root.Message, root));
            return 1;
        }
        catch (Exception ex)
        {
            await WriteResponseAsync(BridgeResponse.Fail("bridge_error", ex.Message, ex));
            return 1;
        }
    }

    private static object? ExecuteAction(Assembly asm, object traceability, string action, BridgeRequest request)
    {
        return action switch
        {
            "login" => ExecuteLogin(traceability, request),
            "get-info" => ExecuteGetInfo(traceability, request),
            "move-in" => ExecuteMoveIn(traceability, request),
            "move-out" => ExecuteMoveOut(asm, traceability, request),
            "move-out-and-test" => ExecuteMoveOutAndTest(asm, traceability, request),
            _ => throw new InvalidOperationException($"Unknown action '{action}'."),
        };
    }

    private static object? ExecuteLogin(object traceability, BridgeRequest request)
    {
        RequireString(request.Station, "station");
        if (!string.IsNullOrWhiteSpace(request.User) || !string.IsNullOrWhiteSpace(request.Password))
        {
            return InvokeMethod(traceability, "Login", request.Station!, request.User ?? string.Empty, request.Password ?? string.Empty);
        }

        return InvokeMethod(traceability, "Login", request.Station!);
    }

    private static object? ExecuteGetInfo(object traceability, BridgeRequest request)
    {
        RequireString(request.Station, "station");
        RequireString(request.SerialNumber, "serialNumber");
        return InvokeMethod(traceability, "Serial_GetInformation", request.Station!, request.SerialNumber!);
    }

    private static object? ExecuteMoveIn(object traceability, BridgeRequest request)
    {
        RequireString(request.Station, "station");
        RequireString(request.SerialNumber, "serialNumber");
        var activateWorkOrder = request.ActivateWorkOrder ?? true;
        var layer = request.Layer ?? 0;
        return InvokeMethod(traceability, "Serial_MoveIn", request.Station!, request.SerialNumber!, activateWorkOrder, layer);
    }

    private static object? ExecuteMoveOut(Assembly asm, object traceability, BridgeRequest request)
    {
        RequireString(request.Station, "station");
        RequireString(request.SerialNumber, "serialNumber");
        var resultValue = ParseEnumRequired(asm, "Results", request.Result, request.ResultCode, "result");
        var layer = request.Layer ?? 0;
        var checkMultiBoard = request.CheckMultiBoard ?? false;
        return InvokeMethod(traceability, "Serial_MoveOut", request.Station!, request.SerialNumber!, resultValue, layer, checkMultiBoard);
    }

    private static object? ExecuteMoveOutAndTest(Assembly asm, object traceability, BridgeRequest request)
    {
        RequireString(request.Station, "station");
        RequireString(request.SerialNumber, "serialNumber");
        var resultValue = ParseEnumRequired(asm, "Results", request.Result, request.ResultCode, "result");
        var groupId = request.GroupId ?? string.Empty;
        var groupVersion = request.GroupVersion ?? string.Empty;
        var layer = request.Layer ?? 0;
        var checkMultiBoard = request.CheckMultiBoard ?? false;

        var measureType = FindType(asm, "MES_HAI.Entity.Measure");
        var measureArray = BuildMeasureArray(asm, measureType, request.Measures);

        try
        {
            return InvokeMethod(
                traceability,
                "Serial_MoveOutAndTestResults",
                request.Station!,
                request.SerialNumber!,
                resultValue,
                groupId,
                groupVersion,
                measureArray,
                layer,
                checkMultiBoard
            );
        }
        catch (MissingMethodException)
        {
            var list = CreateGenericList(measureType, measureArray);
            return InvokeMethod(
                traceability,
                "Serial_MoveOutAndTestResults",
                request.Station!,
                request.SerialNumber!,
                resultValue,
                groupId,
                groupVersion,
                list,
                layer,
                checkMultiBoard
            );
        }
    }

    private static Array BuildMeasureArray(Assembly asm, Type measureType, List<MeasureInput>? measures)
    {
        measures ??= [];
        var arr = Array.CreateInstance(measureType, measures.Count);
        var measureResultsType = FindType(asm, "MeasureResults");

        for (var i = 0; i < measures.Count; i++)
        {
            var src = measures[i];
            var dst = Activator.CreateInstance(measureType) ?? throw new InvalidOperationException("Cannot create Measure instance.");
            SetPropertyIfExists(dst, "Position", src.Position);
            SetPropertyIfExists(dst, "MeasureKey", src.MeasureKey);
            SetPropertyIfExists(dst, "LowLimit", src.LowLimit);
            SetPropertyIfExists(dst, "HighLimit", src.HighLimit);
            SetPropertyIfExists(dst, "MeasureValue", src.MeasureValue);
            SetPropertyIfExists(dst, "MeasureNotes", src.MeasureNotes);
            SetPropertyIfExists(dst, "Tolerance", src.Tolerance);
            SetPropertyIfExists(dst, "UnitOfMeasure", src.UnitOfMeasure);

            object? measureResultValue = null;
            if (src.ResultCode.HasValue)
            {
                measureResultValue = Enum.ToObject(measureResultsType, src.ResultCode.Value);
            }
            else if (!string.IsNullOrWhiteSpace(src.Result))
            {
                measureResultValue = ParseEnumLoose(measureResultsType, src.Result!);
            }

            if (measureResultValue is not null)
            {
                SetPropertyIfExists(dst, "Result", measureResultValue);
            }

            arr.SetValue(dst, i);
        }

        return arr;
    }

    private static object CreateGenericList(Type elementType, Array source)
    {
        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = Activator.CreateInstance(listType) ?? throw new InvalidOperationException("Cannot create generic list.");
        var add = listType.GetMethod("Add") ?? throw new InvalidOperationException("List.Add not found.");
        foreach (var item in source)
        {
            add.Invoke(list, [item]);
        }
        return list;
    }

    private static BridgeResponse BuildResponse(string action, object? resultObj, Assembly asm, string dllPath)
    {
        var response = new BridgeResponse
        {
            Ok = true,
            Action = action,
            TimestampUtc = DateTime.UtcNow,
            Result = ObjectGraphFlattener.Flatten(resultObj),
            Diagnostics = new Dictionary<string, object?>
            {
                ["assembly"] = asm.GetName().Name,
                ["assemblyVersion"] = asm.GetName().Version?.ToString(),
                ["dllPath"] = dllPath,
            }
        };

        var errorCode = TryGetNestedInt(resultObj, "ErrorCode");
        var errorDescription = TryGetNestedString(resultObj, "ErrorDescription");
        if (errorCode.HasValue || !string.IsNullOrWhiteSpace(errorDescription))
        {
            response.ErrorDetail = new ErrorDetailPayload
            {
                ErrorCode = errorCode,
                ErrorDescription = errorDescription,
            };
        }

        return response;
    }

    private static string? TryGetNestedString(object? obj, string propertyName)
    {
        if (obj is null) return null;
        var p = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (p is null) return null;
        var val = p.GetValue(obj);
        return val?.ToString();
    }

    private static int? TryGetNestedInt(object? obj, string propertyName)
    {
        var raw = TryGetNestedString(obj, propertyName);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static object? ParseEnumRequired(Assembly asm, string enumTypeName, string? enumName, int? enumCode, string fieldName)
    {
        var enumType = FindType(asm, enumTypeName);
        if (enumCode.HasValue)
        {
            return Enum.ToObject(enumType, enumCode.Value);
        }

        if (string.IsNullOrWhiteSpace(enumName))
        {
            throw new ArgumentException($"Field '{fieldName}' is required.");
        }

        return ParseEnumLoose(enumType, enumName);
    }

    private static object ParseEnumLoose(Type enumType, string raw)
    {
        if (Enum.TryParse(enumType, raw, true, out var parsed))
        {
            return parsed;
        }

        if (int.TryParse(raw, out var numeric))
        {
            return Enum.ToObject(enumType, numeric);
        }

        throw new ArgumentException($"Cannot parse enum '{enumType.Name}' from '{raw}'.");
    }

    private static Type FindType(Assembly asm, string typeName)
    {
        var exact = asm.GetType(typeName, throwOnError: false);
        if (exact is not null) return exact;

        var shortName = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
        var fallback = asm.GetTypes().FirstOrDefault(t => string.Equals(t.Name, shortName, StringComparison.Ordinal));
        if (fallback is not null) return fallback;

        throw new TypeLoadException($"Type '{typeName}' was not found in MES_HAI assembly.");
    }

    private static object CreateTraceability(Type traceabilityType, string instanceName)
    {
        var ctor = traceabilityType.GetConstructor([typeof(string)]);
        if (ctor is not null)
        {
            return ctor.Invoke([instanceName]);
        }

        return Activator.CreateInstance(traceabilityType)
            ?? throw new InvalidOperationException("Cannot instantiate MES_HAI.Traceability.");
    }

    private static void RegisterAssemblyResolver(string assemblyBaseDir)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            var shortName = new AssemblyName(eventArgs.Name).Name;
            if (string.IsNullOrWhiteSpace(shortName)) return null;
            var candidate = Path.Combine(assemblyBaseDir, $"{shortName}.dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
    }

    private static object? InvokeMethod(object target, string methodName, params object?[] args)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal) && m.GetParameters().Length == args.Length)
            .ToList();

        foreach (var method in methods)
        {
            if (IsCompatible(method.GetParameters(), args))
            {
                return method.Invoke(target, args);
            }
        }

        var signatures = methods
            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})")
            .ToArray();
        var requested = $"{methodName}({string.Join(", ", args.Select(a => a?.GetType().Name ?? "null"))})";
        throw new MissingMethodException($"No compatible overload found. Requested: {requested}. Candidates: {string.Join(" | ", signatures)}");
    }

    private static bool IsCompatible(ParameterInfo[] parameters, object?[] args)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            var pType = parameters[i].ParameterType;
            var arg = args[i];
            if (arg is null)
            {
                if (pType.IsValueType && Nullable.GetUnderlyingType(pType) is null)
                {
                    return false;
                }

                continue;
            }

            if (!pType.IsInstanceOfType(arg))
            {
                return false;
            }
        }

        return true;
    }

    private static void SetPropertyIfExists(object instance, string propertyName, object? value)
    {
        if (value is null) return;
        var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is null || !prop.CanWrite) return;

        var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        object toAssign = value;

        if (!targetType.IsInstanceOfType(value))
        {
            if (targetType.IsEnum)
            {
                toAssign = value is string s
                    ? Enum.Parse(targetType, s, true)
                    : Enum.ToObject(targetType, value);
            }
            else
            {
                toAssign = Convert.ChangeType(value, targetType);
            }
        }

        prop.SetValue(instance, toAssign);
    }

    private static void RequireString(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Field '{fieldName}' is required.");
        }
    }

    private static string ResolveDllPath(string? rawPath)
    {
        if (!string.IsNullOrWhiteSpace(rawPath))
        {
            return Path.GetFullPath(rawPath);
        }

        return Path.Combine(AppContext.BaseDirectory, "MES_HAI.dll");
    }

    private static string? NormalizeAction(string raw)
    {
        var n = raw.Trim().ToLowerInvariant();
        return n switch
        {
            "login" => "login",
            "get-info" or "getinfo" or "serial-get-information" => "get-info",
            "move-in" or "movein" => "move-in",
            "move-out" or "moveout" => "move-out",
            "move-out-and-test" or "move-out-and-test-results" or "moveoutandtestresults" => "move-out-and-test",
            _ => null,
        };
    }

    private static async Task<string> ReadInputAsync(string[] args)
    {
        var idx = Array.FindIndex(args, a => string.Equals(a, "--request-file", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length)
        {
            var file = args[idx + 1];
            return await File.ReadAllTextAsync(file);
        }

        return await Console.In.ReadToEndAsync();
    }

    private static async Task WriteResponseAsync(BridgeResponse response)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        await Console.Out.WriteAsync(json);
    }

    private static void TryDispose(object obj)
    {
        try
        {
            if (obj is IDisposable d)
            {
                d.Dispose();
                return;
            }

            obj.GetType().GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance)?.Invoke(obj, []);
        }
        catch
        {
            // best effort
        }
    }
}

public sealed class BridgeRequest
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
    public List<MeasureInput>? Measures { get; set; }
}

public sealed class MeasureInput
{
    public int? Position { get; set; }
    public string? MeasureKey { get; set; }
    public double? LowLimit { get; set; }
    public double? HighLimit { get; set; }
    public string? Result { get; set; }
    public int? ResultCode { get; set; }
    public double? MeasureValue { get; set; }
    public string? MeasureNotes { get; set; }
    public double? Tolerance { get; set; }
    public string? UnitOfMeasure { get; set; }
}

public sealed class ErrorDetailPayload
{
    public int? ErrorCode { get; set; }
    public string? ErrorDescription { get; set; }
}

public sealed class BridgeResponse
{
    public bool Ok { get; set; }
    public string? Action { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public object? Result { get; set; }
    public ErrorDetailPayload? ErrorDetail { get; set; }
    public Dictionary<string, object?>? Diagnostics { get; set; }
    public BridgeError? Error { get; set; }

    public static BridgeResponse Fail(string code, string message, Exception? ex = null)
    {
        return new BridgeResponse
        {
            Ok = false,
            Error = new BridgeError
            {
                Code = code,
                Message = message,
                ExceptionType = ex?.GetType().FullName,
                StackTrace = ex?.StackTrace,
            },
            TimestampUtc = DateTime.UtcNow,
        };
    }
}

public sealed class BridgeError
{
    public string Code { get; set; } = "bridge_error";
    public string Message { get; set; } = string.Empty;
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }
}

public static class ObjectGraphFlattener
{
    public static object? Flatten(object? value, int maxDepth = 5)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return FlattenInternal(value, 0, maxDepth, visited);
    }

    private static object? FlattenInternal(object? value, int depth, int maxDepth, HashSet<object> visited)
    {
        if (value is null) return null;
        if (depth > maxDepth) return "[max_depth_reached]";

        var type = value.GetType();
        if (type.IsEnum)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = value.ToString(),
                ["value"] = Convert.ToInt64(value),
            };
        }

        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return value;
        }

        if (value is DateTime dt) return dt.ToString("O");
        if (value is DateTimeOffset dto) return dto.ToString("O");
        if (value is Guid g) return g.ToString();

        if (!type.IsValueType)
        {
            if (!visited.Add(value)) return "[cycle]";
        }

        if (value is IDictionary dict)
        {
            var mapped = new Dictionary<string, object?>();
            foreach (DictionaryEntry item in dict)
            {
                var key = item.Key?.ToString() ?? "(null)";
                mapped[key] = FlattenInternal(item.Value, depth + 1, maxDepth, visited);
            }
            return mapped;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var list = new List<object?>();
            var count = 0;
            foreach (var item in enumerable)
            {
                list.Add(FlattenInternal(item, depth + 1, maxDepth, visited));
                count += 1;
                if (count >= 100)
                {
                    list.Add("[truncated]");
                    break;
                }
            }
            return list;
        }

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var obj = new Dictionary<string, object?>();
        foreach (var p in props)
        {
            if (!p.CanRead) continue;
            object? propValue;
            try
            {
                propValue = p.GetValue(value);
            }
            catch
            {
                continue;
            }

            obj[p.Name] = FlattenInternal(propValue, depth + 1, maxDepth, visited);
        }

        return obj.Count > 0 ? obj : value.ToString();
    }
}

public sealed class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public static readonly ReferenceEqualityComparer Instance = new();
    private ReferenceEqualityComparer() { }
    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
    public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
}
