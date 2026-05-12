using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Xunit;

namespace Core.IntegrationTests.Tests.SchemaValidationTestWorkflow;

/// <summary>
/// Instance data (attributes) assertions specific to <c>schema-validation-test-workflow</c>.
/// Validates the contract set by <c>SchemaInitMapping.csx</c> and <c>ConfirmExecutionMapping.csx</c>.
/// </summary>
public static class SchemaValidationInstanceDataAssertions
{
    /// <summary>
    /// After start transition (SchemaInitMapping): orderId, customerName, amount, currency,
    /// status="initialized" must be present.
    /// Note: internalNote and auditLog are set by the mapping but may be hidden by field roles
    /// in responses without appropriate role headers. Those are tested separately in FieldRoles tests.
    /// </summary>
    public static void AssertInitializedAttributes(JsonElement attrs)
    {
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attrs,
            "orderId",
            "SchemaInitMapping should preserve orderId."
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attrs,
            "customerName",
            "SchemaInitMapping should preserve customerName."
        );
        JsonElementAssertions.AssertPropertyString(
            attrs,
            "status",
            "initialized",
            "SchemaInitMapping should set status='initialized'."
        );

        Assert.True(
            attrs.TryGetProperty("amount", out var amountEl)
                && amountEl.ValueKind == JsonValueKind.Number,
            "SchemaInitMapping should preserve amount as a number."
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attrs,
            "currency",
            "SchemaInitMapping should preserve currency."
        );
    }

    /// <summary>
    /// After confirm transition: ConfirmExecutionMapping sets status='confirmed',
    /// copies confirmed/confirmedBy from payload, and sets updatedAt.
    /// </summary>
    public static void AssertConfirmedAttributes(JsonElement attrs)
    {
        JsonElementAssertions.AssertPropertyString(
            attrs,
            "status",
            "confirmed",
            "ConfirmExecutionMapping should set status='confirmed'."
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attrs,
            "confirmedBy",
            "ConfirmExecutionMapping should preserve confirmedBy from transition payload."
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attrs,
            "updatedAt",
            "ConfirmExecutionMapping should set updatedAt timestamp."
        );

        Assert.True(
            attrs.TryGetProperty("confirmed", out var confirmedEl)
                && confirmedEl.ValueKind == JsonValueKind.True,
            "ConfirmExecutionMapping should preserve confirmed=true from payload."
        );
    }

    /// <summary>
    /// Asserts that a specific string property exists in the data body (for field-role tests).
    /// </summary>
    public static void AssertFieldVisible(JsonElement dataBody, string fieldName)
    {
        Assert.True(
            dataBody.TryGetProperty(fieldName, out var el)
                && el.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(el.GetString()),
            $"Field '{fieldName}' should be visible (non-empty string) in functions/data response."
        );
    }

    /// <summary>
    /// Asserts that a specific property does NOT exist in the data body (filtered by field role).
    /// </summary>
    public static void AssertFieldHidden(JsonElement dataBody, string fieldName)
    {
        var exists = dataBody.TryGetProperty(fieldName, out var el)
            && el.ValueKind != JsonValueKind.Null
            && el.ValueKind != JsonValueKind.Undefined;
        Assert.False(
            exists,
            $"Field '{fieldName}' should be hidden (absent) in functions/data response for this role."
        );
    }
}
