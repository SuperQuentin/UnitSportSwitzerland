namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Reflection;

/// <summary>A stats list of examples, first ones only: it stays this short when the blocks' stats are added up.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class StatsSampleAttribute(int max) : Attribute
{
    public int Max { get; } = max;
}

/// <summary>
/// Adds one block's stats object into the run's (#1539), field by field, so a counter added to a stats class is summed without
/// anyone remembering it: numbers add up, a field named <c>Max*</c> keeps the larger, arrays add element by element, lists and
/// sets take the other's after their own (<see cref="StatsSampleAttribute"/>: up to its cap), dictionaries add up per key, a nested
/// stats object recurses. Strings and <see cref="TileRewriter.HeightAudit"/> are left to the caller. A field of any other type
/// throws: better a failed run than a number silently wrong.
/// </summary>
public static class StatsMerge
{
    public static void Add(object into, object from)
    {
        foreach (var field in into.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? a = field.GetValue(into), b = field.GetValue(from);
            switch (b)
            {
                case null or string or TileRewriter.HeightAudit: break;
                case int or long or double: field.SetValue(into, Sum(a!, b, field.Name.StartsWith("Max", StringComparison.Ordinal))); break;
                case int[] x: { var t = (int[])a!; for (int i = 0; i < x.Length; i++) t[i] += x[i]; break; }
                case double[] x: { var t = (double[])a!; for (int i = 0; i < x.Length; i++) t[i] += x[i]; break; }
                case System.Collections.IDictionary x: AddDictionary((System.Collections.IDictionary)a!, x, Cap(field)); break;
                case SortedSet<string> x: ((SortedSet<string>)a!).UnionWith(x); break;
                case System.Collections.IList x: Append((System.Collections.IList)a!, x, Cap(field)); break;
                default:
                    if (!field.FieldType.IsClass || !field.FieldType.Name.EndsWith("Stats", StringComparison.Ordinal)
                        && field.FieldType.Name is not ("Stats" or "Tally"))
                        throw new InvalidOperationException($"StatsMerge: no rule for {into.GetType().Name}.{field.Name} ({field.FieldType.Name})");
                    Add(a!, b);
                    break;
            }
        }
    }

    private static int Cap(FieldInfo field) => field.GetCustomAttribute<StatsSampleAttribute>()?.Max ?? int.MaxValue;

    private static void Append(System.Collections.IList into, System.Collections.IList from, int cap)
    {
        foreach (var item in from)
        {
            if (into.Count >= cap) return;
            into.Add(item);
        }
    }

    private static void AddDictionary(System.Collections.IDictionary into, System.Collections.IDictionary from, int cap)
    {
        foreach (System.Collections.DictionaryEntry e in from)
            into[e.Key] = into.Contains(e.Key) ? Sum(into[e.Key]!, e.Value!, false, cap) : e.Value;
    }

    private static object Sum(object a, object b, bool max, int cap = int.MaxValue) => (a, b) switch
    {
        (int x, int y) => max ? Math.Max(x, y) : x + y,
        (long x, long y) => max ? Math.Max(x, y) : x + y,
        (double x, double y) => max ? Math.Max(x, y) : x + y,
        (System.Runtime.CompilerServices.ITuple x, System.Runtime.CompilerServices.ITuple y) =>
            Activator.CreateInstance(a.GetType(), Enumerable.Range(0, x.Length).Select(i => Sum(x[i]!, y[i]!, false)).ToArray())!,
        (System.Collections.IList x, System.Collections.IList y) => Appended(x, y, cap),
        _ => throw new InvalidOperationException($"StatsMerge: no sum for {a.GetType().Name}"),
    };

    private static System.Collections.IList Appended(System.Collections.IList x, System.Collections.IList y, int cap)
    {
        Append(x, y, cap);
        return x;
    }
}
