using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Xunit;

namespace Core.IntegrationTests.Tests.SubflowOrchestrationTestWorkflow;

/// <summary>
/// Attribute/data contract checks for the subflow orchestration workflow family.
/// </summary>
internal static class SubflowOrchestrationInstanceDataAssertions
{
    private const string ContractHint =
        "(subflow-orchestration mappings + api-tests/subflow-orchestration.http expected flags)";

    public static void AssertParentStarted(JsonElement attributes, string expectedTestId)
    {
        JsonElementAssertions.AssertPropertyTrue(attributes, "parentStarted", ContractHint);
        JsonElementAssertions.AssertPropertyString(attributes, "testId", expectedTestId, ContractHint);
    }

    public static void AssertHappyPathCompleted(JsonElement attributes, string expectedTestId)
    {
        AssertParentStarted(attributes, expectedTestId);
        JsonElementAssertions.AssertPropertyTrue(attributes, "childStarted", ContractHint);
        JsonElementAssertions.AssertPropertyTrue(attributes, "grandchildFinished", ContractHint);
        JsonElementAssertions.AssertPropertyTrue(attributes, "grandchildCompleted", ContractHint);
        JsonElementAssertions.AssertPropertyTrue(attributes, "childCompleted", ContractHint);
    }

    public static void AssertParentCommonTransitionExecuted(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "sharedCommonTransitionExecuted",
            "Parent shared-common-transition should mark the parent instance data."
        );
        AssertPropertyMissing(
            attributes,
            "childUpdatedParent",
            "Parent common transition must not perform child updateData behavior."
        );
    }

    public static void AssertChildSharedMarkExecuted(JsonElement dataBody)
    {
        var data = ExtractDataPayload(dataBody);
        JsonElementAssertions.AssertPropertyTrue(
            data,
            "childSharedMarkExecuted",
            "Child shared-child-mark should mark the current child data."
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            data,
            "childSharedMarkAt",
            "Child shared-child-mark should record an execution timestamp."
        );
    }

    public static void AssertChildSharedMarkNotExecuted(JsonElement dataBody)
    {
        var data = ExtractDataPayload(dataBody);
        AssertPropertyMissing(
            data,
            "childSharedMarkExecuted",
            "shared-child-mark must not execute outside its availableIn states."
        );
    }

    public static void AssertUpdateDataApplied(JsonElement attributes)
    {
        JsonElementAssertions.AssertPropertyTrue(
            attributes,
            "childUpdatedParent",
            "update-parent-data should update parent instance data from the child SubFlow."
        );
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attributes,
            "updateParentAt",
            "update-parent-data should record an update timestamp on parent data."
        );
        AssertPropertyMissing(
            attributes,
            "childSharedMarkExecuted",
            "updateData must stay separate from child shared transition markers."
        );
        AssertPropertyMissing(
            attributes,
            "sharedCommonTransitionExecuted",
            "updateData must stay separate from parent common transition markers."
        );
    }

    public static void AssertGrandchildStartedData(JsonElement dataBody)
    {
        var data = ExtractDataPayload(dataBody);
        JsonElementAssertions.AssertPropertyTrue(
            data,
            "grandchildStarted",
            "functions/data should expose deepest active grandchild data while nested."
        );
        JsonElementAssertions.AssertPropertyTrue(
            data,
            "childStarted",
            "Grandchild input should preserve childStarted from the child SubFlow."
        );
    }

    public static void AssertPropertyMissing(
        JsonElement parent,
        string name,
        string? message = null
    )
    {
        Assert.False(
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out _),
            message ?? $"Property '{name}' should not be present."
        );
    }

    private static JsonElement ExtractDataPayload(JsonElement body)
    {
        if (
            body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
        )
        {
            return data;
        }

        return body;
    }
}
