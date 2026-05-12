using System.Text.Json;
using Xunit;

namespace Core.IntegrationTests.Tests.ViewFunctionExtensionTestWorkflow;

/// <summary>
/// Function-specific assertions for <c>view-function-extension-test-workflow</c>.
/// <para>
/// Runtime wraps function output in a camelCase wrapper keyed by the task key.
/// For example, task key <c>vfe-script-task</c> → wrapper property <c>vfeScriptTask</c>.
/// Multi-task functions with an IOutputHandler return the handler's output under the
/// function's first task camelCase key (or custom Key from ScriptResponse).
/// </para>
/// <para>
/// <c>ResolveDataRoot</c> handles: root object → first non-system property → inner object.
/// </para>
/// </summary>
public static class VfeFunctionAssertions
{
    public static void AssertFunctionResponseNotEmpty(JsonElement body, string functionName)
    {
        Assert.True(
            body.ValueKind == JsonValueKind.Object || body.ValueKind == JsonValueKind.Array,
            $"Function '{functionName}' should return a JSON object or array, got {body.ValueKind}."
        );
    }

    public static void AssertFunctionProperty(
        JsonElement body,
        string propertyName,
        string expectedValue,
        string functionName
    )
    {
        var resolved = ResolveDataRoot(body);
        Assert.True(
            resolved.TryGetProperty(propertyName, out var el)
                && el.ValueKind == JsonValueKind.String
                && el.GetString() == expectedValue,
            $"Function '{functionName}' response.{propertyName} should equal '{expectedValue}'."
        );
    }

    public static void AssertFunctionPropertyTrue(
        JsonElement body,
        string propertyName,
        string functionName
    )
    {
        var resolved = ResolveDataRoot(body);
        Assert.True(
            resolved.TryGetProperty(propertyName, out var el)
                && el.ValueKind == JsonValueKind.True,
            $"Function '{functionName}' response.{propertyName} should be true."
        );
    }

    public static bool TryAssertFunctionPropertyTrue(JsonElement body, string propertyName)
    {
        var resolved = ResolveDataRoot(body);
        return resolved.TryGetProperty(propertyName, out var el)
            && el.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Runtime wraps function response in <c>{ "camelCaseTaskKey": { ...actual data... } }</c>.
    /// This method unwraps by:
    /// 1. If root has a <c>data</c> object → use it (standard wrapper).
    /// 2. Otherwise, find the first object property in root → use it (task-key wrapper).
    /// 3. Otherwise, return root as-is.
    /// </summary>
    private static JsonElement ResolveDataRoot(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return body;

        if (body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            return data;

        foreach (var prop in body.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
                return prop.Value;
        }

        return body;
    }
}
