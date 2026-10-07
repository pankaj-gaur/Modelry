using System.Collections;
using System.Globalization;

namespace Modelry.Tridion;

/// <summary>
/// Small reflection helper used where Core Service proxy property names/types vary between proxy generators
/// (list definitions, default values, category flags). Keeps the gateway tolerant of minor proxy differences.
/// </summary>
internal static class Reflect
{
    public static bool TrySet(object target, string name, object? value)
    {
        var p = target.GetType().GetProperty(name);
        if (p is null || !p.CanWrite) return false;
        try { p.SetValue(target, ConvertTo(value, p.PropertyType)); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Sets a collection property whatever the proxy generated for it: an array (T[]) or a collection class
    /// such as NestedRegionDataList (a List&lt;T&gt; subclass).
    /// </summary>
    public static void SetCollection<T>(object target, string name, IReadOnlyList<T> items)
    {
        var p = target.GetType().GetProperty(name) ?? throw new MissingMemberException(target.GetType().Name, name);
        var type = p.PropertyType;
        if (type.IsArray)
        {
            var et = type.GetElementType()!;
            var arr = Array.CreateInstance(et, items.Count);
            for (var i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
            p.SetValue(target, arr);
            return;
        }
        var collection = Activator.CreateInstance(type)!;
        var add = type.GetMethod("Add", new[] { typeof(T) }) ?? type.GetMethods().First(m => m.Name == "Add" && m.GetParameters().Length == 1);
        foreach (var item in items) add.Invoke(collection, new object?[] { item });
        p.SetValue(target, collection);
    }

    public static bool TrySetAny(object target, object? value, params string[] names) => names.Any(n => TrySet(target, n, value));

    public static object? Get(object? target, params string[] names)
    {
        if (target is null) return null;
        foreach (var n in names)
        {
            var p = target.GetType().GetProperty(n);
            if (p is not null) return p.GetValue(target);
        }
        return null;
    }

    public static string? GetString(object? target, params string[] names) => Format(Get(target, names));

    public static List<string> GetStrings(object? target, params string[] names) =>
        Get(target, names) is IEnumerable e and not string ? e.Cast<object?>().Select(Format).Where(s => s is not null).Select(s => s!).ToList() : new List<string>();

    public static string? Format(object? v) => v switch
    {
        null => null,
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()
    };

    public static object? ConvertTo(object? value, Type type)
    {
        if (value is null) return null;
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t.IsInstanceOfType(value)) return value;
        if (t.IsEnum) return Enum.Parse(t, value.ToString()!, ignoreCase: true);
        if (t == typeof(string)) return Format(value);
        if (t == typeof(DateTime)) return DateTime.Parse(value.ToString()!, CultureInfo.InvariantCulture);
        if (t.IsArray && value is IEnumerable items and not string)
        {
            var et = t.GetElementType()!;
            var converted = items.Cast<object?>().Select(i => ConvertTo(i, et)).ToArray();
            var arr = Array.CreateInstance(et, converted.Length);
            for (var i = 0; i < converted.Length; i++) arr.SetValue(converted[i], i);
            return arr;
        }
        return Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
    }
}
