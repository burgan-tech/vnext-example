using System.Text.Json;
using Xunit;

namespace Core.IntegrationTests.Helpers;

/// <summary>
/// Frequently used test assertions over <see cref="JsonElement"/> (e.g. GetInstance <c>attributes</c>).
/// </summary>
public static class JsonElementAssertions
{
    public static void AssertPropertyTrue(
        JsonElement parent,
        string name,
        string? message = null
    )
    {
        Assert.True(
            parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True,
            message ?? $"Property '{name}' should be JSON true."
        );
    }

    public static void AssertPropertyString(
        JsonElement parent,
        string name,
        string expected,
        string? message = null
    )
    {
        Assert.True(
            parent.TryGetProperty(name, out var el) && el.GetString() == expected,
            message ?? $"Property '{name}' should equal '{expected}'."
        );
    }

    public static void AssertPropertyNonEmptyString(
        JsonElement parent,
        string name,
        string? message = null
    )
    {
        Assert.True(
            parent.TryGetProperty(name, out var el)
                && el.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(el.GetString()),
            message ?? $"Property '{name}' should be a non-empty string."
        );
    }

    /// <summary>
    /// Follows <paramref name="path"/> as nested objects and returns the final segment object (e.g. <c>["taskResults","notification"]</c>).
    /// </summary>
    public static bool TryGetNestedObject(
        JsonElement root,
        string[] path,
        out JsonElement nested
    )
    {
        nested = default;
        var current = root;
        foreach (var segment in path)
        {
            if (
                current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next)
            )
                return false;
            current = next;
        }

        nested = current;
        return true;
    }

    public static void AssertNestedPropertyTrue(
        JsonElement root,
        string[] pathToParent,
        string booleanPropertyName,
        string? message = null
    )
    {
        Assert.True(
            TryGetNestedObject(root, pathToParent, out var parent)
                && parent.ValueKind == JsonValueKind.Object,
            message
                ?? $"Expected object at path '{string.Join(".", pathToParent)}'."
        );
        AssertPropertyTrue(
            parent,
            booleanPropertyName,
            message ?? $"'{booleanPropertyName}' should be true under {string.Join(".", pathToParent)}."
        );
    }

    /// <summary>
    /// Verifies that the nested string property is non-empty.
    /// Skill vnext-workflow-creation §6.4: that the task actually ran is proven when the response
    /// surfaces as a non-empty value on parent attributes (a literal <c>"completed": true</c> alone is insufficient).
    /// </summary>
    public static void AssertNestedPropertyNonEmptyString(
        JsonElement root,
        string[] pathToParent,
        string stringPropertyName,
        string? message = null
    )
    {
        Assert.True(
            TryGetNestedObject(root, pathToParent, out var parent)
                && parent.ValueKind == JsonValueKind.Object,
            message
                ?? $"Expected object at path '{string.Join(".", pathToParent)}'."
        );
        AssertPropertyNonEmptyString(
            parent,
            stringPropertyName,
            message
                ?? $"'{stringPropertyName}' should be a non-empty string under {string.Join(".", pathToParent)}."
        );
    }
}
