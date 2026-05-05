using System.Text.Json;
using Xunit;

namespace Core.IntegrationTests.Tests.ViewFunctionExtensionTestWorkflow;

/// <summary>
/// View-specific assertions for <c>view-function-extension-test-workflow</c>.
/// Validates that <c>GET .../functions/state</c> responses include view metadata
/// (state-level and transition-level).
/// </summary>
public static class VfeViewAssertions
{
    /// <summary>
    /// Asserts that the state function body indicates the current state has a view
    /// (<c>view.hasView == true</c> or <c>view</c> object is present with an <c>href</c>).
    /// </summary>
    public static void AssertStateHasView(JsonElement stateBody)
    {
        Assert.True(
            stateBody.ValueKind == JsonValueKind.Object
                && stateBody.TryGetProperty("view", out var viewEl)
                && viewEl.ValueKind == JsonValueKind.Object,
            "State function body should contain a 'view' object."
        );

        var viewObj = stateBody.GetProperty("view");

        if (viewObj.TryGetProperty("hasView", out var hasView))
        {
            Assert.True(
                hasView.ValueKind == JsonValueKind.True,
                "view.hasView should be true when state has a bound view."
            );
        }

        if (viewObj.TryGetProperty("href", out var href))
        {
            Assert.True(
                href.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(href.GetString()),
                "view.href should be a non-empty string."
            );
        }
    }

    /// <summary>
    /// Asserts that a specific transition in the state function body has a view reference.
    /// </summary>
    public static void AssertTransitionHasView(JsonElement stateBody, string transitionKey)
    {
        Assert.True(
            stateBody.TryGetProperty("transitions", out var transitions)
                && transitions.ValueKind == JsonValueKind.Array,
            "State body should have a transitions array."
        );

        JsonElement? matchedTransition = null;
        foreach (var t in transitions.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object)
                continue;

            string? name = null;
            if (t.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                name = nameEl.GetString();
            if (name == null && t.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
                name = keyEl.GetString();

            if (string.Equals(name, transitionKey, StringComparison.Ordinal))
            {
                matchedTransition = t;
                break;
            }
        }

        Assert.True(
            matchedTransition.HasValue,
            $"Transition '{transitionKey}' should be listed in state transitions."
        );

        var transition = matchedTransition!.Value;
        Assert.True(
            transition.TryGetProperty("view", out var viewEl)
                && viewEl.ValueKind == JsonValueKind.Object,
            $"Transition '{transitionKey}' should have a 'view' object."
        );

        if (viewEl.TryGetProperty("href", out var href))
        {
            Assert.True(
                href.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(href.GetString()),
                $"Transition '{transitionKey}' view.href should be a non-empty string."
            );
        }
        else if (viewEl.TryGetProperty("hasView", out var hasView))
        {
            Assert.True(
                hasView.ValueKind == JsonValueKind.True,
                $"Transition '{transitionKey}' view.hasView should be true (or view.href should be present)."
            );
        }
    }
}
