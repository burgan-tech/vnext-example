using System.Text.Json;
using Core.IntegrationTests.Helpers;
using Xunit;

namespace Core.IntegrationTests.Tests.CollectionObjectTest;

/// <summary>
/// Assertions for <c>collection-object-test-workflow</c> instance <c>attributes</c> after the full
/// ScriptBase collection/object API pipeline completes.
/// </summary>
public static class CollectionObjectInstanceDataAssertions
{
    public static void AssertInitialization(JsonElement attrs)
    {
        JsonElementAssertions.AssertPropertyNonEmptyString(
            attrs,
            "testId",
            "InitCollectionTestMapping should write testId."
        );
    }

    public static void AssertCreateAndSet(JsonElement attrs)
    {
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["createAndSetResult"],
            "success"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["createAndSetResult"],
            "propertiesSet"
        );
    }

    public static void AssertGetListAndAsList(JsonElement attrs)
    {
        JsonElementAssertions.AssertNestedPropertyTrue(attrs, ["getListResult"], "success");
        JsonElementAssertions.AssertNestedPropertyTrue(attrs, ["getListResult"], "getListWorked");
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["getListResult"],
            "asListNullReturnsEmpty"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["getListResult"],
            "asListInvalidReturnsEmpty"
        );
    }

    public static void AssertFilterCountAny(JsonElement attrs)
    {
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["filterCountAnyResult"],
            "success"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["filterCountAnyResult"],
            "filterWorked"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["filterCountAnyResult"],
            "countWorked"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["filterCountAnyResult"],
            "anyWorked"
        );
    }

    public static void AssertFirstLast(JsonElement attrs)
    {
        JsonElementAssertions.AssertNestedPropertyTrue(attrs, ["firstLastResult"], "success");
        JsonElementAssertions.AssertNestedPropertyTrue(attrs, ["firstLastResult"], "firstWorked");
        JsonElementAssertions.AssertNestedPropertyTrue(attrs, ["firstLastResult"], "lastWorked");
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["firstLastResult"],
            "firstWithPredicateWorked"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["firstLastResult"],
            "lastWithPredicateWorked"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["firstLastResult"],
            "firstNotFoundIsNull"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["firstLastResult"],
            "emptyFirstIsNull"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["firstLastResult"],
            "emptyLastIsNull"
        );
    }

    public static void AssertListSelect(JsonElement attrs)
    {
        JsonElementAssertions.AssertNestedPropertyTrue(attrs, ["listSelectResult"], "success");
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["listSelectResult"],
            "selectStringWorked"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["listSelectResult"],
            "selectIntWorked"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["listSelectResult"],
            "selectTransformWorked"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["listSelectResult"],
            "emptySelectWorked"
        );
    }

    public static void AssertListAddRemove(JsonElement attrs)
    {
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["listAddRemoveResult"],
            "success"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(attrs, ["listAddRemoveResult"], "addWorked");
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["listAddRemoveResult"],
            "removeWorked"
        );
    }

    public static void AssertRemovePropertyToDictionary(JsonElement attrs)
    {
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["removeToDictResult"],
            "success"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["removeToDictResult"],
            "removePropertyWorked"
        );
        JsonElementAssertions.AssertNestedPropertyTrue(
            attrs,
            ["removeToDictResult"],
            "toDictionaryWorked"
        );
    }

    /// <summary>Runs all assertions in pipeline order (matches workflow state order).</summary>
    public static void AssertFullHappyPathAttributes(JsonElement attrs)
    {
        Assert.False(
            attrs.ValueKind == JsonValueKind.Object
                && attrs.TryGetProperty("error", out var rootErr)
                && rootErr.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(rootErr.GetString()),
            "attributes should not contain a top-level error string after happy path."
        );

        AssertInitialization(attrs);
        AssertCreateAndSet(attrs);
        AssertGetListAndAsList(attrs);
        AssertFilterCountAny(attrs);
        AssertFirstLast(attrs);
        AssertListSelect(attrs);
        AssertListAddRemove(attrs);
        AssertRemovePropertyToDictionary(attrs);
    }
}
