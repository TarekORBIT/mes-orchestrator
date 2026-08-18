using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MesRelayGateway.Mes;

/// <summary>
/// Turns an arbitrary MES_HAI.dll return object (enums, nested objects, lists) into plain
/// dictionaries/lists/scalars so it can be serialized to JSON and printed on stdout.
/// </summary>
internal static class ObjectGraphFlattener
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
            return new Dictionary<string, object?> { ["name"] = value.ToString(), ["value"] = Convert.ToInt64(value) };
        }

        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return value;
        }

        if (value is DateTime dt) return dt.ToString("O");
        if (value is DateTimeOffset dto) return dto.ToString("O");
        if (value is Guid g) return g.ToString();

        if (!type.IsValueType && !visited.Add(value))
        {
            return "[cycle]";
        }

        if (value is IDictionary dict)
        {
            var mapped = new Dictionary<string, object?>();
            foreach (DictionaryEntry item in dict)
            {
                mapped[item.Key?.ToString() ?? "(null)"] = FlattenInternal(item.Value, depth + 1, maxDepth, visited);
            }
            return mapped;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var list = new List<object?>();
            var count = 0;
            foreach (var item in enumerable)
            {
                list.Add(FlattenInternal(item, depth + 1, maxDepth, visited));
                if (++count >= 100) { list.Add("[truncated]"); break; }
            }
            return list;
        }

        var obj = new Dictionary<string, object?>();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead) continue;
            object? propValue;
            try { propValue = p.GetValue(value); }
            catch { continue; }
            obj[p.Name] = FlattenInternal(propValue, depth + 1, maxDepth, visited);
        }

        return obj.Count > 0 ? obj : value.ToString();
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
