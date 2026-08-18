using System.Reflection;

namespace MesRelayGateway.Mes;

/// <summary>
/// Loads MES_HAI.dll by reflection and calls its Traceability methods, the same way
/// production/bridge/Program.cs does, but scoped to the 4 actions this tool drives:
/// Login, Serial_GetInformation, Serial_MoveIn, Serial_MoveOutAndTestResults.
/// </summary>
public sealed class MesClient : IMesClient
{
    private readonly Assembly _assembly;
    private readonly object _traceability;

    private MesClient(Assembly assembly, object traceability)
    {
        _assembly = assembly;
        _traceability = traceability;
    }

    public static MesClient Load(string dllPath, string instanceName)
    {
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException($"MES_HAI.dll introuvable: {dllPath}", dllPath);
        }

        var baseDir = Path.GetDirectoryName(dllPath)!;
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            var shortName = new AssemblyName(eventArgs.Name).Name;
            if (string.IsNullOrWhiteSpace(shortName)) return null;
            var candidate = Path.Combine(baseDir, $"{shortName}.dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        var assembly = Assembly.LoadFrom(dllPath);
        var traceabilityType = assembly.GetType("MES_HAI.Traceability", throwOnError: true)!;
        var ctor = traceabilityType.GetConstructor([typeof(string)]);
        var traceability = ctor is not null
            ? ctor.Invoke([instanceName])
            : Activator.CreateInstance(traceabilityType) ?? throw new InvalidOperationException("Impossible d'instancier MES_HAI.Traceability.");

        return new MesClient(assembly, traceability);
    }

    public MesResult Login(string station, string? user, string? password)
    {
        var hasCredentials = !string.IsNullOrWhiteSpace(user) || !string.IsNullOrWhiteSpace(password);
        var raw = hasCredentials
            ? Invoke("Login", station, user ?? string.Empty, password ?? string.Empty)
            : Invoke("Login", station);
        return BuildResult("login", raw);
    }

    public MesResult GetInfo(string station, string serialNumber)
    {
        var raw = Invoke("Serial_GetInformation", station, serialNumber);
        return BuildResult("get-info", raw);
    }

    public MesResult MoveIn(string station, string serialNumber, bool activateWorkOrder, int layer)
    {
        var raw = Invoke("Serial_MoveIn", station, serialNumber, activateWorkOrder, layer);
        return BuildResult("move-in", raw);
    }

    public MesResult MoveOutAndTest(string station, string serialNumber, string result, string groupId, string groupVersion, int layer, bool checkMultiBoard)
    {
        var resultsType = FindType("Results");
        var resultValue = ParseEnumLoose(resultsType, result);
        var measureType = FindType("MES_HAI.Entity.Measure");
        var emptyMeasures = Array.CreateInstance(measureType, 0);

        object? raw;
        try
        {
            raw = Invoke("Serial_MoveOutAndTestResults", station, serialNumber, resultValue, groupId, groupVersion, emptyMeasures, layer, checkMultiBoard);
        }
        catch (MissingMethodException)
        {
            var listType = typeof(List<>).MakeGenericType(measureType);
            var emptyList = Activator.CreateInstance(listType)!;
            raw = Invoke("Serial_MoveOutAndTestResults", station, serialNumber, resultValue, groupId, groupVersion, emptyList, layer, checkMultiBoard);
        }

        return BuildResult("move-out-and-test", raw);
    }

    private MesResult BuildResult(string action, object? raw)
    {
        var flattened = ObjectGraphFlattener.Flatten(raw);
        var errorCode = TryGetNestedInt(raw, "ErrorCode");
        var errorDescription = TryGetNestedString(raw, "ErrorDescription");

        return new MesResult
        {
            Ok = !errorCode.HasValue || errorCode.Value == 0,
            Action = action,
            ErrorCode = errorCode,
            ErrorDescription = errorDescription,
            Result = flattened,
        };
    }

    private object? Invoke(string methodName, params object?[] args)
    {
        var candidates = _traceability.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == methodName && m.GetParameters().Length == args.Length)
            .ToList();

        foreach (var method in candidates)
        {
            if (IsCompatible(method.GetParameters(), args))
            {
                return method.Invoke(_traceability, args);
            }
        }

        var signatures = candidates.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
        throw new MissingMethodException($"Aucune surcharge compatible pour '{methodName}'. Candidats: {string.Join(" | ", signatures)}");
    }

    private static bool IsCompatible(ParameterInfo[] parameters, object?[] args)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            var arg = args[i];
            var paramType = parameters[i].ParameterType;
            if (arg is null)
            {
                if (paramType.IsValueType && Nullable.GetUnderlyingType(paramType) is null) return false;
                continue;
            }
            if (!paramType.IsInstanceOfType(arg)) return false;
        }
        return true;
    }

    private Type FindType(string typeName)
    {
        var exact = _assembly.GetType(typeName, throwOnError: false);
        if (exact is not null) return exact;

        var shortName = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
        var fallback = _assembly.GetTypes().FirstOrDefault(t => t.Name == shortName);
        return fallback ?? throw new TypeLoadException($"Type '{typeName}' introuvable dans MES_HAI.dll.");
    }

    private static object ParseEnumLoose(Type enumType, string raw)
    {
        if (Enum.TryParse(enumType, raw, ignoreCase: true, out var parsed)) return parsed!;
        if (int.TryParse(raw, out var numeric)) return Enum.ToObject(enumType, numeric);
        throw new ArgumentException($"Impossible de convertir '{raw}' en '{enumType.Name}'.");
    }

    private static string? TryGetNestedString(object? obj, string propertyName)
    {
        if (obj is null) return null;
        var p = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return p?.GetValue(obj)?.ToString();
    }

    private static int? TryGetNestedInt(object? obj, string propertyName)
    {
        var raw = TryGetNestedString(obj, propertyName);
        return int.TryParse(raw, out var parsed) ? parsed : null;
    }

    public void Dispose()
    {
        try
        {
            if (_traceability is IDisposable disposable)
            {
                disposable.Dispose();
                return;
            }
            _traceability.GetType().GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance)?.Invoke(_traceability, []);
        }
        catch
        {
            // best effort
        }
    }
}
