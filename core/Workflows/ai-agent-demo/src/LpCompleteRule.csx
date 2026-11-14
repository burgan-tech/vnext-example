using System;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;

/// <summary>Amount, term and income are all known: run the policy.</summary>
public class LpCompleteRule : ScriptBase, IConditionMapping
{
    public Task<bool> Handler(ScriptContext context)
    {
        var root = Root(context.Instance?.Data);
        return Task.FromResult(!Missing(root));
    }

    private static JsonElement Root(object? data) =>
        data == null ? default : JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(data));

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static double? Num(JsonElement root, params string[] path)
    {
        JsonElement? cur = root;
        foreach (var segment in path) { cur = cur is { } c ? Prop(c, segment) : null; }
        if (cur is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    private static string? Str(JsonElement root, params string[] path)
    {
        JsonElement? cur = root;
        foreach (var segment in path) { cur = cur is { } c ? Prop(c, segment) : null; }
        return cur is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    }

    /// <summary>True when the intake agent left amount, term or income unstated (null or not positive).</summary>
    private static bool Missing(JsonElement root) =>
        !(Num(root, "intake", "amount") > 0) || !(Num(root, "intake", "termMonths") > 0) || !(Num(root, "intake", "monthlyIncome") > 0);
}
